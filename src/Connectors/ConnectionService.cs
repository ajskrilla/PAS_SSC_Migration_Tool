using System.Data;
using Dapper;

namespace PasMigration.Connectors;

/// <summary>
/// Manages tenant_connection rows (metadata only) and runs the live auth handshake.
/// Credentials are accepted per request, used in memory, and never persisted or logged.
/// </summary>
public sealed class ConnectionService(IDbConnection db, IPasConnectorFactory pasFactory, ISecretServerConnectorFactory ssFactory)
{
    /// <summary>Create or update the source/target connection metadata for an engagement.</summary>
    public async Task<Guid> UpsertAsync(Guid engagementId, ConnectionInput input)
    {
        // One connection per (engagement, role) - upsert on that unique key.
        var id = await db.ExecuteScalarAsync<Guid>(
            @"INSERT INTO tenant_connection
                (engagement_id, role, system_type, base_url, platform_tenant, auth_mode, credential_ref)
              VALUES (@EngagementId, @Role, @SystemType, @BaseUrl, @PlatformTenant, @AuthMode, NULL)
              ON CONFLICT (engagement_id, role) DO UPDATE SET
                system_type    = EXCLUDED.system_type,
                base_url       = EXCLUDED.base_url,
                platform_tenant= EXCLUDED.platform_tenant,
                auth_mode      = EXCLUDED.auth_mode
              RETURNING id",
            new
            {
                EngagementId = engagementId,
                input.Role,
                input.SystemType,
                input.BaseUrl,
                input.PlatformTenant,
                input.AuthMode,
            });
        return id;
    }

    public async Task<IEnumerable<dynamic>> ListAsync(Guid engagementId) =>
        await db.QueryAsync(
            @"SELECT id, role, system_type, base_url, platform_tenant, auth_mode, credential_ref
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
    string SystemType,      // pas | secret_server
    string? BaseUrl,
    string? PlatformTenant,
    string AuthMode);       // platform_client_credentials | legacy_password

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
    string? Role = null);

public sealed record TestConnectionResult(bool Success, string Message)
{
    public static TestConnectionResult Ok(string m) => new(true, m);
    public static TestConnectionResult Fail(string m) => new(false, m);
}
