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
// Data-access layer (repositories own SQL). First seam: engagements.
builder.Services.AddScoped<PasMigration.Data.IEngagementRepository, PasMigration.Data.EngagementRepository>();
builder.Services.AddScoped<PasMigration.Data.IUserRepository, PasMigration.Data.UserRepository>();
builder.Services.AddScoped<PasMigration.Data.ICredentialRepository, PasMigration.Data.CredentialRepository>();
builder.Services.AddScoped<PasMigration.Data.IInventoryRepository, PasMigration.Data.InventoryRepository>();
builder.Services.AddScoped<PasMigration.Data.IMigrationRepository, PasMigration.Data.MigrationRepository>();
// Connector factories (own HttpClient creation; make services testable with fake clients).
builder.Services.AddSingleton<PasMigration.Connectors.IPasConnectorFactory, PasMigration.Connectors.PasConnectorFactory>();
builder.Services.AddSingleton<PasMigration.Connectors.ISecretServerConnectorFactory, PasMigration.Connectors.SecretServerConnectorFactory>();
// Session credential store: in-memory, 60-min sliding idle, cleared on restart.
builder.Services.AddSingleton(new CredentialVault(TimeSpan.FromMinutes(60)));
// Encrypts credentials for persistence across restarts.
builder.Services.AddScoped<CredentialEncryptionService>();
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
builder.Services.AddHttpContextAccessor();

// JWT — read secret directly from config, no BuildServiceProvider needed
var jwtSecret = builder.Configuration["Auth__JwtSecret"]
             ?? Environment.GetEnvironmentVariable("AUTH_JWT_SECRET")
             ?? "dev-secret-change-in-production-min-32-chars!!";
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
builder.Services.AddAuthorization();
builder.Services.AddAuthorization();

// In production the SPA is served same-origin through the nginx reverse proxy, so CORS
// isn't exercised by the browser. This policy only matters for cross-origin dev (e.g.
// `npm run dev` on :5173). Origins are configurable via Cors:Origins (comma-separated).
var corsOrigins = (builder.Configuration["Cors:Origins"] ?? "http://localhost:5173")
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.WithOrigins(corsOrigins).AllowAnyHeader().AllowAnyMethod()));

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

app.UseCors();
app.UseAuthentication();
app.UseAuthorization();

// Liveness/readiness. Readiness pings the DB.
app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));
app.MapGet("/health/ready", async (IDbConnection db) =>
{
    try { await db.ExecuteScalarAsync("SELECT 1"); return Results.Ok(new { status = "ready" }); }
    catch (Exception ex) { return Results.Problem($"db not ready: {ex.Message}"); }
});

// First real read endpoint: list engagements (proves DB wiring end-to-end).
app.MapGet("/api/engagements", async (PasMigration.Data.IEngagementRepository repo, CancellationToken ct) =>
    Results.Ok(await repo.ListAsync(ct)));

app.MapPost("/api/engagements", async (PasMigration.Data.IEngagementRepository repo, CreateEngagement input, CancellationToken ct) =>
{
    var id = await repo.CreateAsync(input.Name, input.CustomerName, ct);
    return Results.Created($"/api/engagements/{id}", new { id });
});

// ---- Tenant connections (metadata only; credentials never persisted) ----

app.MapGet("/api/engagements/{id:guid}/connections",
    async (Guid id, ConnectionService svc) => Results.Ok(await svc.ListAsync(id)));

app.MapPut("/api/engagements/{id:guid}/connections",
    async (Guid id, ConnectionInput input, ConnectionService svc) =>
        Results.Ok(new { id = await svc.UpsertAsync(id, input) }));

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
                input.Username, input.Scope);
            vault.Put(eng, role, creds2);

            // Persist encrypted credentials so they survive container restarts.
            // Look up the tenant_connection row for this engagement+role.
            var tcId = await creds.FindConnectionIdAsync(eng, role, ct);
            if (tcId.HasValue)
                await enc.SaveAsync(tcId.Value, eng, creds2, ct);
        }
        return result.Success ? Results.Ok(result) : Results.BadRequest(result);
    });


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
    });

// Which roles currently have active session credentials (for the UI badge).
app.MapGet("/api/engagements/{id:guid}/credentials/status",
    async (Guid id, CredentialVault vault, CredentialEncryptionService enc, CancellationToken ct) =>
    {
        // If vault is empty (e.g. after restart), try to load from encrypted storage.
        if (!vault.Has(id, "source") && !vault.Has(id, "target"))
            await enc.LoadIntoVaultAsync(id, vault, ct);
        return Results.Ok(new { source = vault.Has(id, "source"), target = vault.Has(id, "target") });
    });

// Explicit sign-out: forget session credentials for this engagement.
app.MapPost("/api/engagements/{id:guid}/credentials/clear",
    (Guid id, CredentialVault vault) => { vault.Clear(id); return Results.Ok(new { cleared = true }); });

// ---- Inventory (read-only) ----

// Run a full inventory capture for one tenant role using session credentials.
app.MapPost("/api/engagements/{id:guid}/inventory/run",
    async (Guid id, RunInventoryInput input, InventoryService svc,
           CredentialVault vault, CredentialEncryptionService enc,
           CancellationToken ct) =>
    {
        // Auto-load persisted credentials if vault is empty (e.g. after restart) — mirrors
        // /migrate so "Run inventory" works without re-entering the secret every time.
        if (!vault.Has(id, input.Role))
            await enc.LoadIntoVaultAsync(id, vault, ct);

        // Merge stored credentials into input — the frontend may send blank credential
        // fields once they're already stored server-side for this role.
        var stored = vault.Get(id, input.Role);
        if (stored is not null)
        {
            input = input with
            {
                BaseUrl             = stored.BaseUrl ?? input.BaseUrl,
                PlatformBaseUrl     = stored.PlatformBaseUrl ?? input.PlatformBaseUrl,
                SecretServerBaseUrl = stored.SecretServerBaseUrl ?? input.SecretServerBaseUrl,
                AppId               = stored.AppId ?? input.AppId,
                ClientId            = stored.ClientId.Length > 0 ? stored.ClientId : input.ClientId,
                ClientSecret        = stored.ClientSecret.Length > 0 ? stored.ClientSecret : input.ClientSecret,
                Username            = stored.Username ?? input.Username,
                Scope               = stored.Scope ?? input.Scope,
            };
        }

        try { return Results.Ok(await svc.CaptureAsync(id, input, ct)); }
        catch (Exception ex) { return Results.BadRequest(new { message = ex.Message }); }
    });

// Recompute the source/target reconciliation diff.
app.MapPost("/api/engagements/{id:guid}/reconcile",
    async (Guid id, InventoryService svc) =>
        Results.Ok(new { count = await svc.ReconcileAsync(id) }));

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
    });

// Inventory items for a snapshot (drill-down table).
app.MapGet("/api/snapshots/{snapshotId:guid}/items",
    async (Guid snapshotId, PasMigration.Data.IInventoryRepository inventory, string? type, CancellationToken ct) =>
        Results.Ok(await inventory.GetSnapshotItemsAsync(snapshotId, type, ct)));

// Reconciliation results (diff table).
app.MapGet("/api/engagements/{id:guid}/reconciliation",
    async (Guid id, PasMigration.Data.IInventoryRepository inventory, CancellationToken ct) =>
        Results.Ok(await inventory.GetReconciliationAsync(id, ct)));

// ---- Migration (write; dry-run aware) ----

// Run a migration job (text_secret | file_secret | account_unmanage_export | full).
app.MapPost("/api/templates/create-file",
    async (CreateFileTemplateRequest req, ConnectionService svc,
           CredentialVault vault, CredentialEncryptionService enc, CancellationToken ct) =>
    {
        var conn = req.Connection;
        if (conn.EngagementId is { } eng && conn.Role is { } role)
        {
            if (!vault.Has(eng, role))
                await enc.LoadIntoVaultAsync(eng, vault, ct);
            var stored = vault.Get(eng, role);
            if (stored is not null)
            {
                conn = conn with
                {
                    BaseUrl             = stored.BaseUrl ?? conn.BaseUrl,
                    PlatformBaseUrl     = stored.PlatformBaseUrl ?? conn.PlatformBaseUrl,
                    SecretServerBaseUrl = stored.SecretServerBaseUrl ?? conn.SecretServerBaseUrl,
                    AppId               = stored.AppId ?? conn.AppId,
                    ClientId            = stored.ClientId.Length > 0 ? stored.ClientId : conn.ClientId,
                    ClientSecret        = stored.ClientSecret.Length > 0 ? stored.ClientSecret : conn.ClientSecret,
                    Username            = stored.Username ?? conn.Username,
                    Scope               = stored.Scope ?? conn.Scope,
                };
            }
        }
        try { return Results.Ok(await svc.CreateFileTemplateAsync(conn, req.Name, ct)); }
        catch (Exception ex) { return Results.BadRequest(new { message = ex.Message }); }
    });

app.MapPost("/api/templates",
    async (TestConnectionInput input, ConnectionService svc,
           CredentialVault vault, CredentialEncryptionService enc, CancellationToken ct) =>
    {
        // The frontend only ever has a masked placeholder for a credential loaded from
        // Pre-migration — never the real secret — so it sends engagementId+role and relies
        // on the server to resolve the rest from the vault, same as /migrate and /inventory/run.
        if (input.EngagementId is { } eng && input.Role is { } role)
        {
            if (!vault.Has(eng, role))
                await enc.LoadIntoVaultAsync(eng, vault, ct);
            var stored = vault.Get(eng, role);
            if (stored is not null)
            {
                input = input with
                {
                    BaseUrl             = stored.BaseUrl ?? input.BaseUrl,
                    PlatformBaseUrl     = stored.PlatformBaseUrl ?? input.PlatformBaseUrl,
                    SecretServerBaseUrl = stored.SecretServerBaseUrl ?? input.SecretServerBaseUrl,
                    AppId               = stored.AppId ?? input.AppId,
                    ClientId            = stored.ClientId.Length > 0 ? stored.ClientId : input.ClientId,
                    ClientSecret        = stored.ClientSecret.Length > 0 ? stored.ClientSecret : input.ClientSecret,
                    Username            = stored.Username ?? input.Username,
                    Scope               = stored.Scope ?? input.Scope,
                };
            }
        }
        try { return Results.Ok(await svc.ListTemplatesAsync(input, ct)); }
        catch (Exception ex) { return Results.BadRequest(new { message = ex.Message }); }
    });

app.MapPost("/api/engagements/{id:guid}/migrate",
    async (Guid id, MigrationRunInput input, MigrationService svc,
           CredentialVault vault, CredentialEncryptionService enc,
           CancellationToken ct) =>
    {
        // Auto-load persisted credentials if vault is empty (e.g. after restart).
        if (!vault.Has(id, "source") || !vault.Has(id, "target"))
            await enc.LoadIntoVaultAsync(id, vault, ct);

        // Merge vault credentials into input — frontend sends metadata only (no secrets).
        var src = vault.Get(id, "source");
        var tgt = vault.Get(id, "target");
        if (src is not null || tgt is not null)
        {
            input = input with
            {
                PasBaseUrl      = src?.BaseUrl ?? src?.PlatformBaseUrl ?? input.PasBaseUrl,
                PasAppId        = src?.AppId   ?? input.PasAppId,
                PasClientId     = src?.ClientId.Length > 0 ? src.ClientId : input.PasClientId,
                PasClientSecret = src?.ClientSecret.Length > 0 ? src.ClientSecret : input.PasClientSecret,
                PasScope        = src?.Scope   ?? input.PasScope,
                SsBaseUrl           = tgt?.BaseUrl ?? input.SsBaseUrl,
                SsPlatformBaseUrl   = tgt?.PlatformBaseUrl ?? input.SsPlatformBaseUrl,
                SsSecretServerBaseUrl = tgt?.SecretServerBaseUrl ?? input.SsSecretServerBaseUrl,
                SsClientId      = tgt?.ClientId.Length > 0 ? tgt.ClientId : input.SsClientId,
                SsClientSecret  = tgt?.ClientSecret.Length > 0 ? tgt.ClientSecret : input.SsClientSecret,
            };
        }

        try { return Results.Ok(await svc.RunAsync(id, input, ct)); }
        catch (Exception ex) { return Results.BadRequest(new { message = ex.Message }); }
    });

// Revert: delete tool-created target items (lab testing). Requires explicit confirm=true.
app.MapPost("/api/engagements/{id:guid}/revert",
    async (Guid id, RevertRequest req, MigrationService svc,
           CredentialVault vault, CredentialEncryptionService enc, IDbConnection db,
           CancellationToken ct) =>
    {
        if (!req.Confirm)
            return Results.BadRequest(new { message = "Revert requires confirm=true. This deletes migrated target data." });
        // Auto-load and merge vault credentials for revert too.
        if (!vault.Has(id, "source") || !vault.Has(id, "target"))
            await enc.LoadIntoVaultAsync(id, vault, ct);
        var src = vault.Get(id, "source");
        var tgt = vault.Get(id, "target");
        var conn = req.Connection;
        if (src is not null || tgt is not null)
        {
            conn = conn with
            {
                PasBaseUrl      = src?.BaseUrl ?? src?.PlatformBaseUrl ?? conn.PasBaseUrl,
                PasClientId     = src?.ClientId.Length > 0 ? src.ClientId : conn.PasClientId,
                PasClientSecret = src?.ClientSecret.Length > 0 ? src.ClientSecret : conn.PasClientSecret,
                SsPlatformBaseUrl   = tgt?.PlatformBaseUrl ?? conn.SsPlatformBaseUrl,
                SsSecretServerBaseUrl = tgt?.SecretServerBaseUrl ?? conn.SsSecretServerBaseUrl,
                SsClientId      = tgt?.ClientId.Length > 0 ? tgt.ClientId : conn.SsClientId,
                SsClientSecret  = tgt?.ClientSecret.Length > 0 ? tgt.ClientSecret : conn.SsClientSecret,
            };
        }
        try { return Results.Ok(await svc.RevertAsync(id, conn, ct)); }
        catch (Exception ex) { return Results.BadRequest(new { message = ex.Message }); }
    });

// Migration report: jobs + per-item outcomes + event timeline.
app.MapGet("/api/engagements/{id:guid}/migration/report",
    async (Guid id, PasMigration.Data.IMigrationRepository migrations, CancellationToken ct) =>
    {
        var jobs  = await migrations.GetJobsAsync(id, ct);
        var items = await migrations.GetReportItemsAsync(id, ct);
        return Results.Ok(new { jobs, items });
    });

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
    });

app.MapGet("/api/engagements/{id:guid}/source-items",
    async (Guid id, PasMigration.Data.IMigrationRepository migrations, string? type, string? scope, CancellationToken ct) =>
        Results.Ok(await migrations.GetSourceItemsAsync(id, type, scope, ct)));

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
    });

// The currently-running job for an engagement (so the UI can offer an abort button
// even though the migrate request is still in flight).
app.MapGet("/api/engagements/{id:guid}/running-job",
    async (Guid id, PasMigration.Data.IMigrationRepository migrations, CancellationToken ct) =>
    {
        var job = await migrations.GetRunningJobAsync(id, ct);
        return Results.Ok(job is null ? new { running = false } : new { running = true, job });
    });

// Abort a running migration job.
app.MapPost("/api/jobs/{jobId:guid}/cancel",
    (Guid jobId, JobRegistry jobs) =>
        jobs.Cancel(jobId)
            ? Results.Ok(new { cancelled = true })
            : Results.NotFound(new { message = "Job not running (already finished or unknown)." }));

// Migration delta: per-type counts of what's migrated vs pending, plus the item list.
app.MapGet("/api/engagements/{id:guid}/migration-status",
    async (Guid id, PasMigration.Data.IMigrationRepository migrations, CancellationToken ct) =>
    {
        var summary = await migrations.GetStatusSummaryAsync(id, ct);
        var items   = await migrations.GetStatusItemsAsync(id, ct);
        return Results.Ok(new { summary, items });
    });


// ── Auth endpoints ────────────────────────────────────────────────────────────────────

app.MapPost("/api/auth/login", async (LoginRequest req, AuthService auth, HttpResponse response) =>
{
    var result = await auth.LoginAsync(req);
    if (!result.Success)
        return Results.Json(new { error = result.Error }, statusCode: 401);
    response.Cookies.Append("auth_token", result.Token!, new CookieOptions
    {
        HttpOnly = true, Secure = false, SameSite = SameSiteMode.Strict,
        Expires = DateTimeOffset.UtcNow.AddHours(8),
    });
    return Results.Ok(new { user = new {
        id = result.User!.Id, username = result.User.Username,
        email = result.User.Email, displayName = result.User.DisplayName,
        role = result.User.Role, forcePasswordChange = result.User.ForcePasswordChange,
        engagementIds = result.User.EngagementIds,
    }});
});

app.MapPost("/api/auth/logout", (HttpResponse response) =>
{
    response.Cookies.Delete("auth_token");
    return Results.Ok(new { message = "Logged out." });
});

app.MapPost("/api/auth/change-password",
    async (ChangePasswordRequest req, AuthService auth, ClaimsPrincipal user,
           HttpResponse response) =>
    {
        var userId = Guid.Parse(user.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? Guid.Empty.ToString());
        var (ok, err) = await auth.ChangePasswordAsync(userId, req);
        if (!ok) return Results.BadRequest(new { error = err });

        // Re-issue JWT so force_pwd claim is updated to false in the new token.
        var users = await auth.ListUsersAsync();
        var updated = users.FirstOrDefault(u => u.Id == userId);
        if (updated is not null)
        {
            var newToken = auth.GenerateToken(updated);
            response.Cookies.Append("auth_token", newToken, new CookieOptions
            {
                HttpOnly = true, Secure = false, SameSite = SameSiteMode.Strict,
                Expires = DateTimeOffset.UtcNow.AddHours(8),
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
    });

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
});

app.Run();

public record CreateEngagement(string Name, string CustomerName);
public record RevertRequest(bool Confirm, MigrationRunInput Connection);
public record CreateFileTemplateRequest(TestConnectionInput Connection, string Name);
