using System.Data;
using Dapper;

namespace PasMigration.Connectors;

/// <summary>
/// Manages tenant_connection rows (metadata only) and runs the live auth handshake.
/// Credentials are accepted per request, used in memory, and never persisted or logged.
/// </summary>
public sealed class ConnectionService(
    IDbConnection db,
    IPasConnectorFactory pasFactory,
    ISecretServerConnectorFactory ssFactory,
    ICyberArkConnectorFactory caFactory)
{
    /// <summary>Create or update the source/target connection metadata for an engagement.</summary>
    public async Task<Guid> UpsertAsync(Guid engagementId, ConnectionInput input)
    {
        // One connection per (engagement, role) - upsert on that unique key.
        var id = await db.ExecuteScalarAsync<Guid>(
            @"INSERT INTO tenant_connection
                (engagement_id, role, system_type, base_url, platform_tenant, auth_mode,
                 identity_token_url, credential_ref)
              VALUES (@EngagementId, @Role, @SystemType, @BaseUrl, @PlatformTenant, @AuthMode,
                      @IdentityTokenUrl, NULL)
              ON CONFLICT (engagement_id, role) DO UPDATE SET
                system_type       = EXCLUDED.system_type,
                base_url          = EXCLUDED.base_url,
                platform_tenant   = EXCLUDED.platform_tenant,
                auth_mode         = EXCLUDED.auth_mode,
                identity_token_url= EXCLUDED.identity_token_url
              RETURNING id",
            new
            {
                EngagementId = engagementId,
                input.Role,
                input.SystemType,
                input.BaseUrl,
                input.PlatformTenant,
                input.AuthMode,
                input.IdentityTokenUrl,
            });
        return id;
    }

    public async Task<IEnumerable<dynamic>> ListAsync(Guid engagementId) =>
        await db.QueryAsync(
            @"SELECT id, role, system_type, base_url, platform_tenant, auth_mode,
                     identity_token_url, credential_ref
              FROM tenant_connection WHERE engagement_id = @engagementId ORDER BY role",
            new { engagementId });

    /// <summary>
    /// Live auth handshake against the real tenant. Read-only: authenticates and, for PAS,
    /// runs one tiny RedRock probe to confirm the token works. Credentials are used in memory
    /// only and disposed immediately. Returns a structured result for the UI.
    /// </summary>
    public async Task<TestConnectionResult> TestAsync(TestConnectionInput input, CancellationToken ct)
    {
        using var creds = new TenantCredentials
        {
            ClientId = input.ClientId,
            ClientSecret = input.ClientSecret,
            Scope = input.Scope,
        };

        try
        {
            if (input.SystemType == "pas")
            {
                var pas = pasFactory.Create(input.BaseUrl!, input.AppId!);
                await pas.AuthenticateAsync(creds, ct);
                // Tiny read probe: confirms the token authorizes RedRock. Count only - no data leaves.
                var rows = await pas.QueryAsync("SELECT COUNT(*) AS n FROM VaultAccount", ct);
                var n = rows.Count > 0 && rows[0].TryGetValue("n", out var v) ? v : null;
                return TestConnectionResult.Ok($"Authenticated to PAS. VaultAccount probe returned: {n}.");
            }
            else if (input.SystemType == "cyberark")
            {
                return await TestCyberArkAsync(input, ct);
            }
            else // secret_server
            {
                var ss = ssFactory.Create(
                    input.PlatformBaseUrl ?? input.BaseUrl!,
                    input.SecretServerBaseUrl ?? input.BaseUrl!,
                    input.AuthMode == "legacy_password"
                        ? SecretServerConnector.AuthMode.LegacyPassword
                        : SecretServerConnector.AuthMode.PlatformClientCredentials);

                if (input.AuthMode == "legacy_password")
                    await ss.AuthenticateLegacyAsync(input.Username!, input.ClientSecret, ct);
                else
                    await ss.AuthenticatePlatformAsync(creds, ct);

                return TestConnectionResult.Ok("Authenticated to Secret Server / Platform.");
            }
        }
        catch (Exception ex)
        {
            // Surface a useful but non-sensitive message. Never echo credentials.
            return TestConnectionResult.Fail(ex.Message);
        }
    }

    /// <summary>
    /// CyberArk handshake. Authenticates, then enumerates safes as a read probe — that call is
    /// the one that proves the session can actually see vault data, which a successful logon on
    /// its own does not (a valid user with no safe membership authenticates fine and can read
    /// nothing). The count is reported so the operator can sanity-check scope before inventory.
    ///
    /// Logs off afterwards. Leaving PVWA sessions open across every connection test would
    /// accumulate against the vault's concurrent-session limit.
    /// </summary>
    private async Task<TestConnectionResult> TestCyberArkAsync(
        TestConnectionInput input, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(input.BaseUrl))
            return TestConnectionResult.Fail(
                "A PVWA base URL is required, including the virtual directory " +
                "(e.g. https://pvwa.corp.local/PasswordVault).");

        var ca = caFactory.Create(input.BaseUrl);
        using var caCreds = new CyberArkCredentials
        {
            AuthMethod = CyberArkMigrationService.ParseAuthMethod(input.AuthMode),
            Username = input.Username,
            // For the session logons the vault password rides in ClientSecret, the same field the
            // OAuth client secret uses; AuthMode decides which it is.
            Password = input.ClientSecret,
            ClientId = input.ClientId,
            ClientSecret = input.ClientSecret,
            IdentityTokenUrl = input.IdentityTokenUrl,
        };

        await ca.AuthenticateAsync(caCreds, ct);
        try
        {
            var safes = await ca.ListSafesAsync(ct);
            return TestConnectionResult.Ok(
                $"Authenticated to CyberArk. {safes.Count} migratable safe(s) visible " +
                "(CyberArk system safes excluded).");
        }
        finally
        {
            await ca.LogoffAsync(CancellationToken.None);
        }
    }

    /// <summary>Authenticate to Secret Server and return its templates for the UI picker.</summary>
    public async Task<List<TemplateOption>> ListTemplatesAsync(TestConnectionInput input, CancellationToken ct)
    {
        using var creds = new TenantCredentials
        {
            ClientId = input.ClientId, ClientSecret = input.ClientSecret, Scope = input.Scope,
        };
        var ss = ssFactory.Create(
            input.PlatformBaseUrl ?? input.BaseUrl!,
            input.SecretServerBaseUrl ?? input.BaseUrl!,
            input.AuthMode == "legacy_password"
                ? SecretServerConnector.AuthMode.LegacyPassword
                : SecretServerConnector.AuthMode.PlatformClientCredentials);
        if (input.AuthMode == "legacy_password")
            await ss.AuthenticateLegacyAsync(input.Username!, input.ClientSecret, ct);
        else
            await ss.AuthenticatePlatformAsync(creds, ct);

        var templates = await ss.ListTemplatesAsync(ct);
        return templates.Select(t => new TemplateOption(t.Id, t.Name)).ToList();
    }

    /// <summary>Create a file-capable template (Name/Description/File) on the target.</summary>
    public async Task<TemplateOption> CreateFileTemplateAsync(
        TestConnectionInput input, string name, CancellationToken ct)
    {
        using var creds = new TenantCredentials
        {
            ClientId = input.ClientId, ClientSecret = input.ClientSecret, Scope = input.Scope,
        };
        var ss = ssFactory.Create(
            input.PlatformBaseUrl ?? input.BaseUrl!,
            input.SecretServerBaseUrl ?? input.BaseUrl!,
            input.AuthMode == "legacy_password"
                ? SecretServerConnector.AuthMode.LegacyPassword
                : SecretServerConnector.AuthMode.PlatformClientCredentials);
        if (input.AuthMode == "legacy_password")
            await ss.AuthenticateLegacyAsync(input.Username!, input.ClientSecret, ct);
        else
            await ss.AuthenticatePlatformAsync(creds, ct);

        var tplName = string.IsNullOrWhiteSpace(name) ? "Migration File Template" : name;
        var id = await ss.CreateFileTemplateAsync(tplName, ct);
        return new TemplateOption(id, tplName);
    }
}

public sealed record TemplateOption(long Id, string Name);

/// <summary>Connection metadata to persist (no credentials).</summary>
public sealed record ConnectionInput(
    string Role,            // source | target
    string SystemType,      // pas | cyberark | secret_server
    string? BaseUrl,
    string? PlatformTenant,
    // platform_client_credentials | legacy_password | cyberark_{ldap,vault,radius,windows,oauth}
    string AuthMode,
    string? IdentityTokenUrl = null);   // CyberArk Privilege Cloud only

/// <summary>Test-connection request. Credentials are in this body and never stored.</summary>
public sealed record TestConnectionInput(
    string SystemType,
    string AuthMode,
    string? BaseUrl,
    string? PlatformBaseUrl,
    string? SecretServerBaseUrl,
    string? AppId,
    string ClientId,
    string ClientSecret,
    string? Username,
    string? Scope,
    Guid? EngagementId = null,
    string? Role = null,
    string? IdentityTokenUrl = null);   // CyberArk Privilege Cloud only

public sealed record TestConnectionResult(bool Success, string Message)
{
    public static TestConnectionResult Ok(string m) => new(true, m);
    public static TestConnectionResult Fail(string m) => new(false, m);
}
