using Microsoft.AspNetCore.Mvc;
using System.Data;
using System.Security.Authentication;
using Dapper;
using Hangfire;
using Hangfire.PostgreSql;
using Npgsql;
using PasMigration.Connectors;
using PasMigration.Auth;
using PasMigration.Ai;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;

var builder = WebApplication.CreateBuilder(args);

var connString = builder.Configuration.GetConnectionString("Postgres")
    ?? throw new InvalidOperationException("ConnectionStrings:Postgres is required.");

// Postgres connection factory (Dapper over Npgsql).
builder.Services.AddScoped<IDbConnection>(_ => new NpgsqlConnection(connString));

// HttpClient used for all tenant API calls. Enforce TLS 1.2+ per the PAS API requirements.
builder.Services.AddHttpClient("tenant").ConfigurePrimaryHttpMessageHandler(() =>
    new HttpClientHandler { SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13 });

builder.Services.AddScoped<ConnectionService>();
builder.Services.AddScoped<InventoryService>();
builder.Services.AddScoped<MigrationService>();
// CyberArk source. A sibling service rather than a branch inside MigrationService — the flows
// differ in stages, ordering and safety rules; see CyberArkMigrationService's class comment.
builder.Services.AddScoped<CyberArkMigrationService>();
// Data-access layer (repositories own SQL). First seam: engagements.
builder.Services.AddScoped<PasMigration.Data.IEngagementRepository, PasMigration.Data.EngagementRepository>();
builder.Services.AddScoped<PasMigration.Data.IUserRepository, PasMigration.Data.UserRepository>();
builder.Services.AddScoped<PasMigration.Data.ICredentialRepository, PasMigration.Data.CredentialRepository>();
builder.Services.AddScoped<PasMigration.Data.IInventoryRepository, PasMigration.Data.InventoryRepository>();
builder.Services.AddScoped<PasMigration.Data.IMigrationRepository, PasMigration.Data.MigrationRepository>();
builder.Services.AddScoped<PasMigration.Data.ISettingsRepository, PasMigration.Data.SettingsRepository>();
builder.Services.AddScoped<PasMigration.Data.IIdentityProviderRepository, PasMigration.Data.IdentityProviderRepository>();
builder.Services.AddScoped<PasMigration.Data.IUserIdentityRepository, PasMigration.Data.UserIdentityRepository>();
// Encrypts OIDC client secrets at rest (config-only dependencies — singleton is fine).
builder.Services.AddSingleton<IdpSecretProtector>();
// Connector factories (own HttpClient creation; make services testable with fake clients).
builder.Services.AddSingleton<PasMigration.Connectors.IPasConnectorFactory, PasMigration.Connectors.PasConnectorFactory>();
builder.Services.AddSingleton<PasMigration.Connectors.ISecretServerConnectorFactory, PasMigration.Connectors.SecretServerConnectorFactory>();
builder.Services.AddSingleton<PasMigration.Connectors.ICyberArkConnectorFactory, PasMigration.Connectors.CyberArkConnectorFactory>();
// Session credential store: in-memory, 60-min sliding idle, cleared on restart.
builder.Services.AddSingleton(new CredentialVault(TimeSpan.FromMinutes(60)));
// Encrypts credentials for persistence across restarts.
builder.Services.AddScoped<CredentialEncryptionService>();
// Resolves stored credentials into request inputs (vault auto-load + stored-over-sent merge).
builder.Services.AddScoped<CredentialResolver>();
// Tracks running migration jobs so they can be aborted from the UI.
builder.Services.AddSingleton<JobRegistry>();

// AI assistant (read-only advisor, local Ollama by default). HttpClient is named "ollama" and
// given a BaseAddress so OllamaProvider's relative-path calls (/api/chat, /api/embeddings)
// resolve correctly; CPU inference can be slow, hence the longer timeout than the tenant client.
builder.Services.AddHttpClient("ollama", client =>
{
    var endpoint = builder.Configuration["Ai__Ollama__Endpoint"]
                ?? Environment.GetEnvironmentVariable("OLLAMA_ENDPOINT")
                ?? "http://ollama:11434";
    client.BaseAddress = new Uri(endpoint);
    client.Timeout = TimeSpan.FromMinutes(5);
});
// OllamaProvider registered as both its concrete type (AssistantService's constructor asks for
// it directly) and as ILlmProvider (AssistantRouter/AssistantCatalog ask for the abstraction) —
// same singleton instance either way, which matters for AssistantCatalog's embedding cache.
builder.Services.AddSingleton<OllamaProvider>();
builder.Services.AddSingleton<ILlmProvider>(sp => sp.GetRequiredService<OllamaProvider>());
builder.Services.AddSingleton<AssistantCatalog>();
builder.Services.AddSingleton<AssistantRouter>();
builder.Services.AddSingleton<ContentGuard>();
// Scoped, not Singleton: it depends on the Scoped IDbConnection, same as the other services above.
builder.Services.AddScoped<AssistantService>();

// Resumable background jobs (migration orchestrator) backed by Postgres.
builder.Services.AddHangfire(cfg => cfg
    .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
    .UseSimpleAssemblyNameTypeSerializer()
    .UseRecommendedSerializerSettings()
    .UsePostgreSqlStorage(o => o.UseNpgsqlConnection(connString)));
builder.Services.AddHangfireServer();

// App-level auth (OIDC/JWT). Configuration supplied via env in real deployments.
// Auth services
builder.Services.AddScoped<AuthService>();
builder.Services.AddScoped<IExternalIdentityService, ExternalIdentityService>();
builder.Services.AddHttpContextAccessor();

// JWT — read secret directly from config, no BuildServiceProvider needed.
// No fallback, deliberately: this secret signs JWTs AND derives the credential-encryption
// key, so a known default would make tokens forgeable and stored tenant credentials
// decryptable by anyone with the source. Fail fast instead.
var jwtSecret = builder.Configuration["Auth__JwtSecret"]
             ?? Environment.GetEnvironmentVariable("AUTH_JWT_SECRET")
             ?? throw new InvalidOperationException(
                    "AUTH_JWT_SECRET is required. Generate one (`openssl rand -base64 48`) and set it in .env. " +
                    "NOTE: it also derives the credential-encryption key — changing it later invalidates " +
                    "stored tenant credentials, which must then be re-entered on the Pre-migration page.");
if (jwtSecret.Trim().Length < 32)
    throw new InvalidOperationException("AUTH_JWT_SECRET must be at least 32 characters.");
var jwtKey = new SymmetricSecurityKey(
    System.Text.Encoding.UTF8.GetBytes(jwtSecret));

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        o.TokenValidationParameters = new()
        {
            ValidateIssuer           = true,
            ValidIssuer              = "pas-migration",
            ValidateAudience         = true,
            ValidAudience            = "pas-migration",
            ValidateLifetime         = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey         = jwtKey,
        };
        o.Events = new JwtBearerEvents
        {
            OnMessageReceived = ctx =>
            {
                if (ctx.Request.Cookies.TryGetValue("auth_token", out var cookieToken))
                    ctx.Token = cookieToken;
                return Task.CompletedTask;
            }
        };
    });

// ── SSO: register one OIDC authentication scheme per enabled provider (from the DB) ─────
// Startup-time registration (design phase 2): adding or enabling a provider requires an api
// restart; dynamic scheme reload is a later phase. If the DB is unreachable here, SSO scheme
// registration is skipped with a warning and local password login remains fully functional.
// The built-in OpenIdConnect handler does ALL protocol validation (issuer, audience, nonce,
// PKCE, signing keys via metadata discovery) — we write none of it.
var ssoRegistry = new SsoSchemeRegistry();
builder.Services.AddSingleton(ssoRegistry);
try
{
    using var ssoDb = new NpgsqlConnection(connString);
    var ssoProviders = ssoDb.Query<(Guid Id, string Slug, string Authority, string ClientId, byte[]? SecretEnc)>(
        "SELECT id, slug, authority, client_id, client_secret_enc FROM identity_provider WHERE enabled AND type = 'oidc'")
        .ToList();

    if (ssoProviders.Count > 0)
    {
        var ssoProtector = new IdpSecretProtector(builder.Configuration,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<IdpSecretProtector>.Instance);
        var ssoAuth = builder.Services.AddAuthentication();
        // Carrier scheme required by the OIDC handler for the handshake result. Our own JWT
        // cookie is issued in OnTicketReceived and this cookie is never actually written.
        ssoAuth.AddCookie("sso-external");

        foreach (var sp in ssoProviders)
        {
            var scheme     = $"oidc-{sp.Slug}";
            var providerId = sp.Id;
            var slug       = sp.Slug;
            var secret     = sp.SecretEnc is { Length: > 0 } ? ssoProtector.Unprotect(providerId, sp.SecretEnc) : null;

            ssoAuth.AddOpenIdConnect(scheme, options =>
            {
                options.SignInScheme  = "sso-external";
                options.Authority     = sp.Authority;
                options.ClientId      = sp.ClientId;
                options.ClientSecret  = secret;
                options.ResponseType  = "code";   // authorization code flow only
                options.UsePkce       = true;     // (also the handler default for code flow)
                options.CallbackPath  = $"/api/auth/sso/{slug}/callback";
                // https metadata required except for http://localhost test IdPs (mirrors the
                // authority validation rule in IdentityProviderInput.Validate).
                options.RequireHttpsMetadata = sp.Authority.StartsWith("https", StringComparison.OrdinalIgnoreCase);
                options.Scope.Clear();
                options.Scope.Add("openid");
                options.Scope.Add("profile");
                options.Scope.Add("email");
                options.GetClaimsFromUserInfoEndpoint = true;
                options.SaveTokens = false;       // we issue our own session; IdP tokens are not kept
                options.Events = new Microsoft.AspNetCore.Authentication.OpenIdConnect.OpenIdConnectEvents
                {
                    OnTicketReceived = ctx => SsoLogin.CompleteAsync(ctx, providerId, slug),
                    OnRemoteFailure  = ctx =>
                    {
                        // Correlation failures, user-cancelled consent, IdP errors — coarse
                        // code to the client, detail stays in the server log.
                        ctx.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>()
                            .CreateLogger("PasMigration.Auth.SsoLogin")
                            .LogWarning("SSO remote failure: provider={Slug} error={Error}", slug, ctx.Failure?.Message);
                        ctx.Response.Redirect("/?sso_error=provider_error");
                        ctx.HandleResponse();
                        return Task.CompletedTask;
                    },
                };
            });
            ssoRegistry.Register(slug, scheme, providerId);
        }
        Console.WriteLine($"[startup] SSO: registered {ssoProviders.Count} OIDC scheme(s): {string.Join(", ", ssoProviders.Select(x => x.Slug))}");
    }
}
catch (Exception ex)
{
    Console.WriteLine($"[startup] WARNING: SSO scheme registration skipped ({ex.Message}). Local login is unaffected.");
}
// Fallback policy: every endpoint requires an authenticated user unless it carries explicit
// authorization metadata (.AllowAnonymous() or its own .RequireAuthorization(...) policy).
// This closes the previous gap where only the auth/admin endpoints were protected and all
// business endpoints (migrate, revert, inventory, credentials, assistant, diagnostics) were
// anonymous. Only login, logout, and the health probes opt out below.
builder.Services.AddSingleton<Microsoft.AspNetCore.Authorization.IAuthorizationHandler, EngagementMemberHandler>();
builder.Services.AddAuthorization(o =>
{
    o.FallbackPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();

    // Read access to one engagement's data: any authenticated role (viewer+), but only for
    // engagements in the caller's eng_ids claim (empty = all; admin = all). Enforced by
    // EngagementMemberHandler against the "{id}" route value.
    o.AddPolicy("EngagementMember", p => p
        .RequireAuthenticatedUser()
        .AddRequirements(new EngagementMemberRequirement()));

    // Mutating or tenant-API-touching actions: operator or admin, engagement-scoped where
    // the route carries an engagement id (vacuous on non-engagement routes like /api/templates).
    o.AddPolicy("OperatorWrite", p => p
        .RequireRole("operator", "admin")
        .AddRequirements(new EngagementMemberRequirement()));
});

// In production the SPA is served same-origin through the nginx reverse proxy, so CORS
// isn't exercised by the browser. This policy only matters for cross-origin dev (e.g.
// `npm run dev` on :5173). Origins are configurable via Cors:Origins (comma-separated).
var corsOrigins = (builder.Configuration["Cors:Origins"] ?? "http://localhost:5173")
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.WithOrigins(corsOrigins).AllowAnyHeader().AllowAnyMethod()));

// Login rate limiting: 5 attempts per minute per client IP, no queueing. BCrypt makes each
// guess slow; this stops sustained online guessing outright. The partition key relies on
// X-Forwarded-For from nginx (see UseForwardedHeaders below) — without it every client would
// share the proxy's IP and one attacker could lock the door for everyone.
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.OnRejected = async (ctx, ct) =>
    {
        ctx.HttpContext.Response.ContentType = "application/json";
        await ctx.HttpContext.Response.WriteAsync(
            "{\"error\":\"Too many login attempts. Try again in a minute.\"}", ct);
    };
    o.AddPolicy("login", ctx => RateLimitPartition.GetFixedWindowLimiter(
        partitionKey: ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        factory: _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 5,
            Window      = TimeSpan.FromMinutes(1),
            QueueLimit  = 0,
        }));
});

var app = builder.Build();

// Idempotent schema fixups applied at startup (the docker-entrypoint-initdb.d migrations only
// run on a fresh volume; this keeps an already-provisioned DB current).
try
{
    using var fixupConn = new NpgsqlConnection(connString);
    await fixupConn.OpenAsync();
    await fixupConn.ExecuteAsync(@"
        ALTER TABLE inventory_item DROP CONSTRAINT IF EXISTS inventory_item_item_type_check;
        ALTER TABLE inventory_item ADD CONSTRAINT inventory_item_item_type_check
            CHECK (item_type IN ('account','text_secret','file_secret','folder','multiplexed_account'));

        ALTER TABLE migration_job DROP CONSTRAINT IF EXISTS migration_job_job_type_check;
        ALTER TABLE migration_job ADD CONSTRAINT migration_job_job_type_check
            CHECK (job_type IN ('account_local','account_domain','account_unmanage_export',
                                'text_secret','file_secret','folder_structure','full'));");
}
catch (Exception ex)
{
    app.Logger.LogWarning("Startup schema fixup skipped: {Message}", ex.Message);
}

// Honor X-Forwarded-For / X-Forwarded-Proto from nginx so RemoteIpAddress is the real
// client (rate-limit partitioning) and Request.IsHttps reflects the TLS front door.
// Trusting any immediate proxy is safe HERE because the API publishes no host port —
// it is reachable only through nginx on the compose network (port lockdown, July 2026).
// If a host port mapping is ever reintroduced, this trust must be revisited.
var fwd = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
};
fwd.KnownNetworks.Clear();
fwd.KnownProxies.Clear();
app.UseForwardedHeaders(fwd);

app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

// Liveness/readiness. Readiness pings the DB. Anonymous by design: docker/nginx health
// checks and the pre-login SPA readiness probe have no session.
app.MapGet("/health/live", () => Results.Ok(new { status = "live" })).AllowAnonymous();
app.MapGet("/health/ready", async (IDbConnection db) =>
{
    try { await db.ExecuteScalarAsync("SELECT 1"); return Results.Ok(new { status = "ready" }); }
    catch (Exception ex) { return Results.Problem($"db not ready: {ex.Message}"); }
}).AllowAnonymous();

// First real read endpoint: list engagements (proves DB wiring end-to-end).
// Scoped to the caller's eng_ids claim (empty = all; admin = all) so restricted users
// don't see engagements they can't open.
app.MapGet("/api/engagements", async (PasMigration.Data.IEngagementRepository repo,
                                      ClaimsPrincipal user, CancellationToken ct) =>
{
    var rows = await repo.ListAsync(ct);
    var engIds = user.FindFirst("eng_ids")?.Value;
    if (!user.IsInRole("admin") && !string.IsNullOrWhiteSpace(engIds))
    {
        var allowed = engIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        rows = rows.Where(r => allowed.Contains(
            ((IDictionary<string, object>)r)["id"].ToString()!)).ToList();
    }
    return Results.Ok(rows);
});

app.MapPost("/api/engagements", async (PasMigration.Data.IEngagementRepository repo, CreateEngagement input, CancellationToken ct) =>
{
    var id = await repo.CreateAsync(input.Name, input.CustomerName, ct);
    return Results.Created($"/api/engagements/{id}", new { id });
}).RequireAuthorization("OperatorWrite");

// ---- Tenant connections (metadata only; credentials never persisted) ----

app.MapGet("/api/engagements/{id:guid}/connections",
    async (Guid id, ConnectionService svc) => Results.Ok(await svc.ListAsync(id))).RequireAuthorization("EngagementMember");

app.MapPut("/api/engagements/{id:guid}/connections",
    async (Guid id, ConnectionInput input, ConnectionService svc) =>
        Results.Ok(new { id = await svc.UpsertAsync(id, input) })).RequireAuthorization("OperatorWrite");

// Live auth handshake. On success, cache credentials in the session vault so subsequent
// inventory/migration actions don't require re-entry. Credentials stay in memory only.
app.MapPost("/api/connections/test",
    async (TestConnectionInput input, ConnectionService svc, CredentialVault vault,
           CredentialEncryptionService enc, PasMigration.Data.ICredentialRepository creds,
           CancellationToken ct) =>
    {
        var result = await svc.TestAsync(input, ct);
        if (result.Success && input.EngagementId is { } eng && input.Role is { } role)
        {
            var creds2 = new SessionCredentials(
                input.SystemType, input.AuthMode, input.BaseUrl, input.PlatformBaseUrl,
                input.SecretServerBaseUrl, input.AppId, input.ClientId, input.ClientSecret,
                input.Username, input.Scope, input.IdentityTokenUrl);
            vault.Put(eng, role, creds2);

            // Persist encrypted credentials so they survive container restarts.
            // Look up the tenant_connection row for this engagement+role.
            var tcId = await creds.FindConnectionIdAsync(eng, role, ct);
            if (tcId.HasValue)
                await enc.SaveAsync(tcId.Value, eng, creds2, ct);
        }
        return result.Success ? Results.Ok(result) : Results.BadRequest(result);
    }).RequireAuthorization("OperatorWrite");


// Returns non-sensitive credential metadata for display (no secrets).
app.MapGet("/api/engagements/{id:guid}/credentials/info",
    async (Guid id, CredentialVault vault, CredentialEncryptionService enc, CancellationToken ct) =>
    {
        if (!vault.Has(id, "source") && !vault.Has(id, "target"))
            await enc.LoadIntoVaultAsync(id, vault, ct);

        static object? Mask(SessionCredentials? c) => c is null ? null : new
        {
            systemType    = c.SystemType,
            authMode      = c.AuthMode,
            baseUrl       = c.BaseUrl,
            platformBaseUrl = c.PlatformBaseUrl,
            secretServerBaseUrl = c.SecretServerBaseUrl,
            clientId      = c.ClientId,
            appId         = c.AppId,
            scope         = c.Scope,
            // Never return ClientSecret or Username — masked in UI
            clientSecretMasked = c.ClientSecret.Length > 0 ? "••••••••" : null,
        };

        return Results.Ok(new
        {
            source = Mask(vault.Get(id, "source")),
            target = Mask(vault.Get(id, "target")),
        });
    }).RequireAuthorization("EngagementMember");

// Which roles currently have active session credentials (for the UI badge).
app.MapGet("/api/engagements/{id:guid}/credentials/status",
    async (Guid id, CredentialVault vault, CredentialEncryptionService enc, CancellationToken ct) =>
    {
        // If vault is empty (e.g. after restart), try to load from encrypted storage.
        if (!vault.Has(id, "source") && !vault.Has(id, "target"))
            await enc.LoadIntoVaultAsync(id, vault, ct);
        return Results.Ok(new { source = vault.Has(id, "source"), target = vault.Has(id, "target") });
    }).RequireAuthorization("EngagementMember");

// Explicit sign-out: forget session credentials for this engagement.
app.MapPost("/api/engagements/{id:guid}/credentials/clear",
    (Guid id, CredentialVault vault) => { vault.Clear(id); return Results.Ok(new { cleared = true }); }).RequireAuthorization("OperatorWrite");

// ---- Inventory (read-only) ----

// Run a full inventory capture for one tenant role using session credentials.
app.MapPost("/api/engagements/{id:guid}/inventory/run",
    async (Guid id, RunInventoryInput input, InventoryService svc,
           CredentialResolver creds, CancellationToken ct) =>
    {
        // Merge stored credentials for the requested role (auto-loading persisted ciphertext
        // after a restart) — the frontend sends blank credential fields once they're stored.
        input = await creds.MergeAsync(id, input, ct);
        try { return Results.Ok(await svc.CaptureAsync(id, input, ct)); }
        catch (Exception ex) { return Results.BadRequest(new { message = ex.Message }); }
    }).RequireAuthorization("OperatorWrite");

// Recompute the source/target reconciliation diff.
app.MapPost("/api/engagements/{id:guid}/reconcile",
    async (Guid id, InventoryService svc) =>
        Results.Ok(new { count = await svc.ReconcileAsync(id) })).RequireAuthorization("OperatorWrite");

// Latest snapshot summaries per role (for dashboard cards).
app.MapGet("/api/engagements/{id:guid}/inventory/summary",
    async (Guid id, PasMigration.Data.IInventoryRepository inventory, CancellationToken ct) =>
    {
        var rows = await inventory.GetLatestSnapshotSummariesAsync(id, ct);

        // Parse the JSONB (returned as text) into a real object so the client gets numbers.
        var shaped = rows.Select(r =>
        {
            var d = (IDictionary<string, object?>)r;
            var json = d.TryGetValue("summary_json", out var sj) ? sj as string : null;
            object? summary = null;
            if (!string.IsNullOrEmpty(json))
                summary = System.Text.Json.JsonSerializer.Deserialize<
                    System.Collections.Generic.Dictionary<string, int>>(json);
            return new
            {
                role = d.TryGetValue("role", out var role) ? role : null,
                snapshot_id = d.TryGetValue("snapshot_id", out var sid) ? sid : null,
                captured_at = d.TryGetValue("captured_at", out var ca) ? ca : null,
                summary,
            };
        });
        return Results.Ok(shaped);
    }).RequireAuthorization("EngagementMember");

// Inventory items for a snapshot (drill-down table).
app.MapGet("/api/snapshots/{snapshotId:guid}/items",
    async (Guid snapshotId, PasMigration.Data.IInventoryRepository inventory, string? type, CancellationToken ct) =>
        Results.Ok(await inventory.GetSnapshotItemsAsync(snapshotId, type, ct)));

// Reconciliation results (diff table).
app.MapGet("/api/engagements/{id:guid}/reconciliation",
    async (Guid id, PasMigration.Data.IInventoryRepository inventory, CancellationToken ct) =>
        Results.Ok(await inventory.GetReconciliationAsync(id, ct))).RequireAuthorization("EngagementMember");

// ---- Migration (write; dry-run aware) ----

// Run a migration job (text_secret | file_secret | account_unmanage_export | full).
app.MapPost("/api/templates/create-file",
    async (CreateFileTemplateRequest req, ConnectionService svc,
           CredentialResolver creds, CancellationToken ct) =>
    {
        var conn = await creds.MergeAsync(req.Connection, ct);
        try { return Results.Ok(await svc.CreateFileTemplateAsync(conn, req.Name, ct)); }
        catch (Exception ex) { return Results.BadRequest(new { message = ex.Message }); }
    }).RequireAuthorization("OperatorWrite");

app.MapPost("/api/templates",
    async (TestConnectionInput input, ConnectionService svc,
           CredentialResolver creds, CancellationToken ct) =>
    {
        // The frontend only ever has a masked placeholder for a stored credential — never the
        // real secret — so it sends engagementId+role and the resolver supplies the rest.
        input = await creds.MergeAsync(input, ct);
        try { return Results.Ok(await svc.ListTemplatesAsync(input, ct)); }
        catch (Exception ex) { return Results.BadRequest(new { message = ex.Message }); }
    }).RequireAuthorization("OperatorWrite");

app.MapPost("/api/engagements/{id:guid}/migrate",
    async (Guid id, MigrationRunInput input, MigrationService svc,
           CredentialResolver creds, CancellationToken ct) =>
    {
        // Merge both roles' stored credentials — frontend sends metadata only (no secrets).
        input = await creds.MergeAsync(id, input, ct);
        try { return Results.Ok(await svc.RunAsync(id, input, ct)); }
        catch (Exception ex) { return Results.BadRequest(new { message = ex.Message }); }
    }).RequireAuthorization("OperatorWrite");

// CyberArk migration. A separate route rather than a branch inside /migrate because the request
// shape genuinely differs (five source auth methods, a permission stage, no unmanage step) and
// because a malformed CyberArk body should never be able to start a PAS run or vice versa.
app.MapPost("/api/engagements/{id:guid}/migrate/cyberark",
    async (Guid id, CyberArkMigrationInput input, CyberArkMigrationService svc,
           CredentialResolver creds, CancellationToken ct) =>
    {
        input = await creds.MergeAsync(id, input, ct);
        try { return Results.Ok(await svc.RunAsync(id, input, ct)); }
        catch (Exception ex) { return Results.BadRequest(new { message = ex.Message }); }
    }).RequireAuthorization("OperatorWrite");

// Pre-migration permission review: what each CyberArk safe membership WILL become, straight from
// the latest source snapshot. Read-only, and the screen an engineer is expected to sign off before
// running the permission stage — the translation is lossy by nature and the losses are listed here.
app.MapGet("/api/engagements/{id:guid}/cyberark/permission-preview",
    async (Guid id, IDbConnection db, CancellationToken ct) =>
    {
        var rows = await db.QueryAsync(new CommandDefinition(
            @"SELECT ii.name AS member_name, ii.folder_path AS safe_name, ii.attributes::text AS attributes
              FROM inventory_item ii
              JOIN inventory_snapshot s ON s.id = ii.snapshot_id
              JOIN tenant_connection tc ON tc.id = s.tenant_connection_id
              WHERE s.engagement_id=@e AND tc.role='source' AND ii.item_type='safe_member'
                AND s.captured_at = (
                    SELECT MAX(s2.captured_at) FROM inventory_snapshot s2
                    JOIN tenant_connection tc2 ON tc2.id = s2.tenant_connection_id
                    WHERE s2.engagement_id=@e AND tc2.role='source')
              ORDER BY ii.folder_path, ii.name",
            new { e = id }, cancellationToken: ct));

        var items = rows.Select(r =>
        {
            var d = (IDictionary<string, object?>)r;
            var attrs = d["attributes"] as string;
            return new
            {
                safeName = d["safe_name"]?.ToString(),
                memberName = d["member_name"]?.ToString(),
                mapping = string.IsNullOrEmpty(attrs)
                    ? null
                    : System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(attrs),
            };
        }).ToList();

        return Results.Ok(items);
    }).RequireAuthorization("EngagementMember");

// Revert: delete tool-created target items (lab testing). Requires explicit confirm=true.
app.MapPost("/api/engagements/{id:guid}/revert",
    async (Guid id, RevertRequest req, MigrationService svc,
           CredentialResolver creds, CancellationToken ct) =>
    {
        if (!req.Confirm)
            return Results.BadRequest(new { message = "Revert requires confirm=true. This deletes migrated target data." });
        // Same dual-role merge as /migrate. Alignment note: revert's previous inline copy had
        // drifted to a subset of fields (it skipped PasAppId, PasScope, SsBaseUrl); it now
        // receives the full set — strictly more stored metadata, fields it doesn't use are ignored.
        var conn = await creds.MergeAsync(id, req.Connection, ct);
        try { return Results.Ok(await svc.RevertAsync(id, conn, ct)); }
        catch (Exception ex) { return Results.BadRequest(new { message = ex.Message }); }
    }).RequireAuthorization("OperatorWrite");

// Migration report: jobs + per-item outcomes + event timeline.
app.MapGet("/api/engagements/{id:guid}/migration/report",
    async (Guid id, PasMigration.Data.IMigrationRepository migrations, CancellationToken ct) =>
    {
        var jobs  = await migrations.GetJobsAsync(id, ct);
        var items = await migrations.GetReportItemsAsync(id, ct);
        return Results.Ok(new { jobs, items });
    }).RequireAuthorization("EngagementMember");

// Source items for the migration checklist (from latest source snapshot).
// Overview metrics: account breakdown (win/unix/domain/multiplexed), managed split,
// source-vs-target counts, and migration progress per type. Drives the Overview charts.
app.MapGet("/api/engagements/{id:guid}/metrics",
    async (Guid id, PasMigration.Data.IMigrationRepository migrations, CancellationToken ct) =>
    {
        var accounts       = (await migrations.GetAccountBreakdownAsync(id, ct)).ToList();
        var managed        = (await migrations.GetManagedSplitAsync(id, ct)).ToList();
        var progress       = (await migrations.GetProgressByTypeAsync(id, ct)).ToList();
        var sourceVsTarget = (await migrations.GetSourceVsTargetAsync(id, ct)).ToList();

        return Results.Ok(new { accounts, managed, progress, sourceVsTarget });
    }).RequireAuthorization("EngagementMember");

app.MapGet("/api/engagements/{id:guid}/source-items",
    async (Guid id, PasMigration.Data.IMigrationRepository migrations, string? type, string? scope, CancellationToken ct) =>
        Results.Ok(await migrations.GetSourceItemsAsync(id, type, scope, ct))).RequireAuthorization("EngagementMember");

// Event log (audit + diagnostics): every tenant action with outcome and message.
// Migration delta/status: how many of each item type are migrated vs pending vs failed.
// Compares the latest source inventory against migration_item progress.
app.MapGet("/api/engagements/{id:guid}/logs",
    async (Guid id, PasMigration.Data.IMigrationRepository migrations,
           int? limit, int? offset, string? q, string? eventType, bool? failuresOnly, CancellationToken ct) =>
    {
        var lim = limit is > 0 and <= 200 ? limit!.Value : 50;
        var off = offset is > 0 ? offset!.Value : 0;
        var failOnly = failuresOnly ?? false;
        var total = await migrations.CountEventsAsync(id, q, eventType, failOnly, ct);
        var rows  = await migrations.GetEventsAsync(id, q, eventType, failOnly, lim, off, ct);
        return Results.Ok(new { total, limit = lim, offset = off, rows });
    }).RequireAuthorization("EngagementMember");

// The currently-running job for an engagement (so the UI can offer an abort button
// even though the migrate request is still in flight).
app.MapGet("/api/engagements/{id:guid}/running-job",
    async (Guid id, PasMigration.Data.IMigrationRepository migrations, CancellationToken ct) =>
    {
        var job = await migrations.GetRunningJobAsync(id, ct);
        return Results.Ok(job is null ? new { running = false } : new { running = true, job });
    }).RequireAuthorization("EngagementMember");

// Abort a running migration job.
app.MapPost("/api/jobs/{jobId:guid}/cancel",
    (Guid jobId, JobRegistry jobs) =>
        jobs.Cancel(jobId)
            ? Results.Ok(new { cancelled = true })
            : Results.NotFound(new { message = "Job not running (already finished or unknown)." })).RequireAuthorization("OperatorWrite");

// Migration delta: per-type counts of what's migrated vs pending, plus the item list.
app.MapGet("/api/engagements/{id:guid}/migration-status",
    async (Guid id, PasMigration.Data.IMigrationRepository migrations, CancellationToken ct) =>
    {
        var summary = await migrations.GetStatusSummaryAsync(id, ct);
        var items   = await migrations.GetStatusItemsAsync(id, ct);
        return Results.Ok(new { summary, items });
    }).RequireAuthorization("EngagementMember");


// ── Auth endpoints ────────────────────────────────────────────────────────────────────

app.MapPost("/api/auth/login", async (LoginRequest req, AuthService auth, HttpResponse response) =>
{
    var result = await auth.LoginAsync(req);
    if (!result.Success)
        return Results.Json(new { error = result.Error }, statusCode: 401);
    response.Cookies.Append("auth_token", result.Token!, new CookieOptions
    {
        HttpOnly = true, Secure = true, SameSite = SameSiteMode.Strict,
        Expires = result.Expires,
    });
    return Results.Ok(new { user = new {
        id = result.User!.Id, username = result.User.Username,
        email = result.User.Email, displayName = result.User.DisplayName,
        role = result.User.Role, forcePasswordChange = result.User.ForcePasswordChange,
        engagementIds = result.User.EngagementIds,
    }});
}).AllowAnonymous().RequireRateLimiting("login");

// Anonymous: a user with an expired or invalid token must still be able to clear the cookie.
app.MapPost("/api/auth/logout", (HttpResponse response) =>
{
    response.Cookies.Delete("auth_token");
    return Results.Ok(new { message = "Logged out." });
}).AllowAnonymous();

// ── SSO sign-in (anonymous by nature) ────────────────────────────────────────────────────
// Kicks off the OIDC dance for the named provider. The registry check turns an unknown or
// disabled slug into a 404 instead of an unknown-scheme 500. The callback
// (/api/auth/sso/{slug}/callback) is handled entirely by the OpenIdConnect middleware and
// completed in SsoLogin.CompleteAsync — there is deliberately no route mapped for it here.
app.MapGet("/api/auth/sso/{slug}/login", (string slug, SsoSchemeRegistry registry) =>
{
    if (!registry.TryGetScheme(slug, out var scheme))
        return Results.NotFound(new { message = "Unknown or disabled identity provider (a newly added provider needs an api restart)." });
    return Results.Challenge(new AuthenticationProperties { RedirectUri = "/" }, [scheme]);
}).AllowAnonymous();

app.MapPost("/api/auth/change-password",
    async (ChangePasswordRequest req, AuthService auth, ClaimsPrincipal user,
           HttpResponse response, CancellationToken ct) =>
    {
        var userId = Guid.Parse(user.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? Guid.Empty.ToString());
        var (ok, err) = await auth.ChangePasswordAsync(userId, req);
        if (!ok) return Results.BadRequest(new { error = err });

        // Re-issue JWT so force_pwd claim is updated to false in the new token.
        var users = await auth.ListUsersAsync();
        var updated = users.FirstOrDefault(u => u.Id == userId);
        if (updated is not null)
        {
            var (newToken, expires) = await auth.GenerateTokenAsync(updated, ct);
            response.Cookies.Append("auth_token", newToken, new CookieOptions
            {
                HttpOnly = true, Secure = true, SameSite = SameSiteMode.Strict,
                Expires = expires,
            });
        }
        return Results.Ok(new { message = "Password changed." });
    }).RequireAuthorization();

app.MapGet("/api/auth/me", (ClaimsPrincipal user) =>
    Results.Ok(new {
        id = user.FindFirst(ClaimTypes.NameIdentifier)?.Value,
        username = user.FindFirst(ClaimTypes.Name)?.Value,
        email = user.FindFirst(ClaimTypes.Email)?.Value,
        role = user.FindFirst(ClaimTypes.Role)?.Value,
        forcePasswordChange = user.FindFirst("force_pwd")?.Value == "true",
        engagementIds = (user.FindFirst("eng_ids")?.Value ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries),
    })).RequireAuthorization();

app.MapGet("/api/admin/users",
    async ([FromServices] AuthService auth) => Results.Ok(await auth.ListUsersAsync()))
    .RequireAuthorization(p => p.RequireRole("admin"));

app.MapPost("/api/admin/users",
    async (CreateUserRequest req, AuthService auth) =>
    {
        var (ok, msg, user) = await auth.CreateUserAsync(req);
        return ok ? Results.Ok(new { message = msg, user }) : Results.BadRequest(new { error = msg });
    }).RequireAuthorization(p => p.RequireRole("admin"));

app.MapPost("/api/admin/users/{id:guid}/deactivate",
    async (Guid id, AuthService auth) =>
    {
        var (ok, err) = await auth.DeactivateUserAsync(id);
        return ok ? Results.Ok(new { message = "User deactivated." }) : Results.NotFound(new { error = err });
    }).RequireAuthorization(p => p.RequireRole("admin"));

app.MapPost("/api/admin/users/{id:guid}/reset-password",
    async (Guid id, AuthService auth) =>
    {
        var (ok, msg) = await auth.ResetPasswordAsync(id);
        return ok ? Results.Ok(new { message = msg }) : Results.NotFound(new { error = msg });
    }).RequireAuthorization(p => p.RequireRole("admin"));

// ---- Admin: app settings (currently just the login session length) ----
app.MapGet("/api/admin/settings",
    async (PasMigration.Data.ISettingsRepository settings, CancellationToken ct) =>
    {
        var raw = await settings.GetAsync("session_timeout_hours", ct);
        var hours = int.TryParse(raw, out var h) ? h : 8;
        return Results.Ok(new
        {
            sessionTimeoutHours = hours,
            minSessionTimeoutHours = AuthService.MinSessionTimeoutHours,
            maxSessionTimeoutHours = AuthService.MaxSessionTimeoutHours,
        });
    }).RequireAuthorization(p => p.RequireRole("admin"));

app.MapPost("/api/admin/settings",
    async (UpdateSettingsRequest req, PasMigration.Data.ISettingsRepository settings, CancellationToken ct) =>
    {
        if (req.SessionTimeoutHours is < AuthService.MinSessionTimeoutHours or > AuthService.MaxSessionTimeoutHours)
            return Results.BadRequest(new
            {
                message = $"Session timeout must be between {AuthService.MinSessionTimeoutHours} and " +
                           $"{AuthService.MaxSessionTimeoutHours} hours."
            });
        await settings.SetAsync("session_timeout_hours", req.SessionTimeoutHours.ToString(), ct);
        // Deliberately does not affect anyone already logged in — only tokens issued from now on
        // use the new length. Existing sessions keep whatever expiry they were issued with.
        return Results.Ok(new { message = "Settings saved. Applies to new logins — existing sessions are unaffected." });
    }).RequireAuthorization(p => p.RequireRole("admin"));

// ---- Migration Assistant (read-only AI advisor; streams SSE) ----
// nginx has a dedicated unbuffered proxy block for exactly this path (see frontend/nginx.conf).
app.MapPost("/api/engagements/{id:guid}/assistant",
    async (Guid id, AssistantRequest req, AssistantService assistant, HttpContext ctx, CancellationToken ct) =>
    {
        ctx.Response.ContentType = "text/event-stream";
        ctx.Response.Headers["Cache-Control"] = "no-cache";
        ctx.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpResponseBodyFeature>()?.DisableBuffering();

        try
        {
            await foreach (var chunk in assistant.AskStreamAsync(id, req.Question, req.History, ct))
            {
                await ctx.Response.WriteAsync(chunk, ct);
                await ctx.Response.Body.FlushAsync(ct);
            }
        }
        catch (OperationCanceledException)
        {
            // Client aborted (Stop button / navigated away) — nothing to send.
        }
        catch (Exception ex)
        {
            // Streaming has already started (status 200 sent), so this can't become a different
            // HTTP status — emit it as an SSE error event instead. The frontend already has a
            // handler for {type:"error"}.
            var msg = "data: {\"type\":\"error\",\"message\":" +
                      System.Text.Json.JsonSerializer.Serialize(ex.Message) + "}\n\n";
            await ctx.Response.WriteAsync(msg, ct);
            await ctx.Response.Body.FlushAsync(ct);
        }
    }).RequireAuthorization("EngagementMember");

app.MapGet("/api/diag/ollama", async ([FromServices] ILlmProvider llm, [FromServices] IConfiguration cfg) =>
{
    var endpoint  = cfg["Ai:Ollama:Endpoint"]  ?? Environment.GetEnvironmentVariable("OLLAMA_ENDPOINT")  ?? "(not set)";
    var chatModel = cfg["Ai:Ollama:ChatModel"]  ?? Environment.GetEnvironmentVariable("OLLAMA_CHAT_MODEL") ?? "(not set)";
    var embedModel= cfg["Ai:Ollama:EmbedModel"] ?? Environment.GetEnvironmentVariable("OLLAMA_EMBED_MODEL")?? "(not set)";
    try
    {
        var embedding = await llm.EmbedAsync("ping", CancellationToken.None);
        return Results.Ok(new { endpoint, chatModel, embedModel, embed_test = "ok", embed_dims = embedding.Length });
    }
    catch (Exception ex)
    {
        return Results.Ok(new { endpoint, chatModel, embedModel, embed_test = "error: " + ex.Message });
    }
}).RequireAuthorization(p => p.RequireRole("admin"));

// ── Admin: identity providers (SSO groundwork — configuration CRUD only) ─────────────────
// No login-flow change yet: these rows are inert until the OIDC challenge/callback step
// ships. Client secrets are encrypted at rest (IdpSecretProtector) and are NEVER returned by
// the API — responses expose only hasClientSecret. On update, a blank/omitted clientSecret
// keeps the stored one.

static PasMigration.Data.IdentityProviderWrite ToProviderWrite(IdentityProviderInput i) => new(
    i.Name.Trim(), i.Slug, i.Authority, i.ClientId.Trim(),
    i.Enabled, i.JitProvisioning, i.DefaultRole,
    i.AllowedEmailDomains ?? [], i.RoleClaim, i.RoleMappingsJson);

app.MapGet("/api/admin/identity-providers",
    async (PasMigration.Data.IIdentityProviderRepository repo, CancellationToken ct) =>
    {
        var rows = await repo.ListAsync(ct);
        return Results.Ok(rows.Select(r => new
        {
            id = r.Id, name = r.Name, slug = r.Slug, type = r.Type,
            authority = r.Authority, clientId = r.ClientId,
            hasClientSecret = r.ClientSecretEnc is { Length: > 0 },
            enabled = r.Enabled, jitProvisioning = r.JitProvisioning,
            defaultRole = r.DefaultRole, allowedEmailDomains = r.AllowedEmailDomains,
            roleClaim = r.RoleClaim, roleMappingsJson = r.RoleMappingsJson,
            createdAt = r.CreatedAt,
        }));
    }).RequireAuthorization(p => p.RequireRole("admin"));

app.MapPost("/api/admin/identity-providers",
    async (IdentityProviderInput input, PasMigration.Data.IIdentityProviderRepository repo,
           IdpSecretProtector protector, CancellationToken ct) =>
    {
        if (input.Validate() is { } error)
            return Results.BadRequest(new { message = error });
        if (await repo.SlugExistsAsync(input.Slug, null, ct))
            return Results.Conflict(new { message = $"Slug '{input.Slug}' is already in use." });

        // Id is generated app-side: the secret encryption key is HKDF-salted with the
        // provider id, so the id must exist before the secret can be encrypted.
        var id  = Guid.NewGuid();
        var enc = string.IsNullOrEmpty(input.ClientSecret) ? null : protector.Protect(id, input.ClientSecret);
        await repo.InsertAsync(id, ToProviderWrite(input), enc, ct);
        return Results.Created($"/api/admin/identity-providers/{id}", new { id });
    }).RequireAuthorization(p => p.RequireRole("admin"));

app.MapPut("/api/admin/identity-providers/{id:guid}",
    async (Guid id, IdentityProviderInput input, PasMigration.Data.IIdentityProviderRepository repo,
           IdpSecretProtector protector, CancellationToken ct) =>
    {
        if (input.Validate() is { } error)
            return Results.BadRequest(new { message = error });
        if (await repo.SlugExistsAsync(input.Slug, id, ct))
            return Results.Conflict(new { message = $"Slug '{input.Slug}' is already in use." });

        // Blank secret = keep the existing ciphertext (COALESCE in the repository).
        var enc  = string.IsNullOrEmpty(input.ClientSecret) ? null : protector.Protect(id, input.ClientSecret);
        var rows = await repo.UpdateAsync(id, ToProviderWrite(input), enc, ct);
        return rows == 0
            ? Results.NotFound(new { message = "Identity provider not found." })
            : Results.Ok(new { id });
    }).RequireAuthorization(p => p.RequireRole("admin"));

app.MapDelete("/api/admin/identity-providers/{id:guid}",
    async (Guid id, PasMigration.Data.IIdentityProviderRepository repo,
           PasMigration.Data.IUserIdentityRepository identities, CancellationToken ct) =>
    {
        // user_identity FK is ON DELETE RESTRICT — give a friendly error instead of a 500.
        var links = await identities.CountByProviderAsync(id, ct);
        if (links > 0)
            return Results.Conflict(new
            { message = $"Provider has {links} linked user identities. Unlink them before deleting." });

        var rows = await repo.DeleteAsync(id, ct);
        return rows == 0
            ? Results.NotFound(new { message = "Identity provider not found." })
            : Results.Ok(new { deleted = true });
    }).RequireAuthorization(p => p.RequireRole("admin"));

app.Run();

public record CreateEngagement(string Name, string CustomerName);
public record RevertRequest(bool Confirm, MigrationRunInput Connection);
public record CreateFileTemplateRequest(TestConnectionInput Connection, string Name);
public record UpdateSettingsRequest(int SessionTimeoutHours);

/// <summary>Admin input for creating/updating an identity provider. ClientSecret is
/// write-only: blank on update means "keep the stored secret".</summary>
public sealed record IdentityProviderInput(
    string Name, string Slug, string Authority, string ClientId, string? ClientSecret,
    bool Enabled = true, bool JitProvisioning = false, string DefaultRole = "viewer",
    string[]? AllowedEmailDomains = null, string? RoleClaim = null, string? RoleMappingsJson = null)
{
    private static readonly System.Text.RegularExpressions.Regex SlugRx =
        new("^[a-z0-9][a-z0-9-]{1,39}$", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly string[] ValidRoles = ["admin", "operator", "viewer"];

    /// <summary>Returns an error message, or null when valid. Pure — unit-tested.</summary>
    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(Name))
            return "Name is required.";
        if (Slug is null || !SlugRx.IsMatch(Slug))
            return "Slug must be 2-40 characters: lowercase letters, digits, hyphens (it appears in URLs).";
        if (!Uri.TryCreate(Authority, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)))
            return "Authority must be an https URL (http is allowed for localhost testing only).";
        if (string.IsNullOrWhiteSpace(ClientId))
            return "ClientId is required.";
        if (!ValidRoles.Contains(DefaultRole))
            return "DefaultRole must be admin, operator, or viewer.";
        if (!string.IsNullOrWhiteSpace(RoleMappingsJson))
        {
            try { using var _ = System.Text.Json.JsonDocument.Parse(RoleMappingsJson); }
            catch { return "RoleMappingsJson must be valid JSON."; }
        }
        return null;
    }
}
