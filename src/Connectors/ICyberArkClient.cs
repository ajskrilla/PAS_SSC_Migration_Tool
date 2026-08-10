namespace PasMigration.Connectors;

/// <summary>
/// How to authenticate against CyberArk. The first four are PVWA session logons used by
/// Self-Hosted / on-prem deployments; <see cref="OAuth"/> is Privilege Cloud, which authenticates
/// against a CyberArk Identity token endpoint instead.
/// </summary>
public enum CyberArkAuthMethod
{
    /// <summary>PVWA logon against a directory-mapped user. The common on-prem choice.</summary>
    Ldap,
    /// <summary>PVWA logon against a vault-local user.</summary>
    CyberArkVault,
    /// <summary>PVWA logon with RADIUS challenge.</summary>
    Radius,
    /// <summary>Integrated Windows Auth. No credential is sent; the process identity is used.</summary>
    Windows,
    /// <summary>Privilege Cloud: OAuth2 client_credentials against CyberArk Identity.</summary>
    OAuth,
}

/// <summary>
/// Credentials for one CyberArk connection. Held in memory only, never persisted in cleartext,
/// never logged. Mirrors <see cref="TenantCredentials"/> but carries both credential shapes,
/// because CyberArk's on-prem and cloud paths need genuinely different fields.
/// </summary>
public sealed class CyberArkCredentials : IDisposable
{
    public required CyberArkAuthMethod AuthMethod { get; init; }

    /// <summary>Username for Ldap / CyberArkVault / Radius. Unused for Windows and OAuth.</summary>
    public string? Username { get; init; }

    /// <summary>Password for Ldap / CyberArkVault / Radius. Unused for Windows and OAuth.</summary>
    public string? Password { get; init; }

    /// <summary>OAuth service-user client id (Privilege Cloud only).</summary>
    public string? ClientId { get; init; }

    /// <summary>OAuth service-user secret (Privilege Cloud only).</summary>
    public string? ClientSecret { get; init; }

    /// <summary>
    /// Full CyberArk Identity token URL, e.g.
    /// https://[tenant].id.cyberark.cloud/oauth2/platformtoken. Required for OAuth.
    /// </summary>
    public string? IdentityTokenUrl { get; init; }

    public void Dispose() => GC.SuppressFinalize(this);

    /// <summary>Never expose secret material in ToString (avoids accidental logging).</summary>
    public override string ToString() => $"CyberArkCredentials(Method={AuthMethod}, User={Username})";
}

/// <summary>One CyberArk safe.</summary>
public sealed record CyberArkSafe(string SafeName, string? Description, string? ManagingCpm)
{
    public Dictionary<string, object?> Raw { get; init; } = new();
}

/// <summary>One privileged account inside a safe. <see cref="Password"/> is never populated by
/// inventory — only by an explicit retrieval during a migration run.</summary>
public sealed record CyberArkAccount
{
    public required string Id { get; init; }
    public string? Name { get; init; }
    public string? UserName { get; init; }
    public string? Address { get; init; }
    public string? PlatformId { get; init; }
    public required string SafeName { get; init; }
    /// <summary>"password" or "key".</summary>
    public string SecretType { get; init; } = "password";
    public bool CpmManaged { get; init; }
    public string? CpmStatus { get; init; }
    /// <summary>The whole raw account object, including platformAccountProperties.</summary>
    public Dictionary<string, object?> Raw { get; init; } = new();
}

/// <summary>One safe member (user or group) and its raw permission switches.</summary>
public sealed record CyberArkSafeMember
{
    public required string SafeName { get; init; }
    public required string MemberName { get; init; }
    /// <summary>"User" or "Group".</summary>
    public string MemberType { get; init; } = "User";
    public string? MemberId { get; init; }
    /// <summary>Raw permission bag as returned by PVWA. Booleans keyed by CyberArk's own names.</summary>
    public Dictionary<string, object?> Permissions { get; init; } = new();
}

/// <summary>
/// Abstraction over the CyberArk PVWA API. Deliberately mirrors <see cref="IPasClient"/>: services
/// depend on this interface rather than constructing <see cref="CyberArkConnector"/>, so the
/// inventory and migration paths stay unit-testable with a fake client.
///
/// Everything here except <see cref="RetrieveAccountPasswordAsync"/> is read-only. This connector
/// never writes to CyberArk — unlike the PAS path, there is no unmanage step, because CyberArk
/// accounts are left exactly as they are and the customer decommissions the vault separately.
/// </summary>
public interface ICyberArkClient
{
    Task AuthenticateAsync(CyberArkCredentials creds, CancellationToken ct = default);

    /// <summary>Best-effort session logoff. No-op for OAuth (there is no session to end).</summary>
    Task LogoffAsync(CancellationToken ct = default);

    /// <summary>All safes visible to the authenticated principal, excluding CyberArk system safes.</summary>
    Task<List<CyberArkSafe>> ListSafesAsync(CancellationToken ct = default);

    /// <summary>Accounts in one safe. Paged; throws rather than truncating.</summary>
    Task<List<CyberArkAccount>> ListAccountsAsync(string safeName, CancellationToken ct = default);

    /// <summary>Members of one safe, with their permission switches. Paged.</summary>
    Task<List<CyberArkSafeMember>> ListSafeMembersAsync(string safeName, CancellationToken ct = default);

    /// <summary>
    /// Retrieve one account's password or SSH key. The ONLY call in this interface that returns
    /// secret material; the value stays in memory and is the caller's responsibility per the
    /// security model. Returns null when CyberArk refuses (dual control, missing permission),
    /// which the caller must treat as "skip this account", never as an empty password.
    /// </summary>
    Task<string?> RetrieveAccountPasswordAsync(string accountId, CancellationToken ct = default);
}

/// <summary>
/// Creates <see cref="ICyberArkClient"/> instances bound to a specific PVWA. Owns HttpClient
/// acquisition via the shared "tenant" named client, matching
/// <see cref="IPasConnectorFactory"/> and <see cref="ISecretServerConnectorFactory"/>.
/// </summary>
public interface ICyberArkConnectorFactory
{
    /// <param name="pvwaBaseUrl">
    /// Full PVWA base including the virtual directory, e.g. https://pvwa.corp.local/PasswordVault
    /// or https://[sub].privilegecloud.cyberark.cloud/PasswordVault.
    /// </param>
    ICyberArkClient Create(string pvwaBaseUrl);
}

public sealed class CyberArkConnectorFactory(IHttpClientFactory httpFactory) : ICyberArkConnectorFactory
{
    public ICyberArkClient Create(string pvwaBaseUrl)
    {
        var http = httpFactory.CreateClient("tenant");
        return new CyberArkConnector(http, pvwaBaseUrl);
    }
}

/// <summary>
/// Thrown when a PVWA call fails. Carries the status code so callers can distinguish
/// "this account is protected" from "the vault is unreachable".
/// </summary>
public sealed class CyberArkApiException(int statusCode, string method, string path, string message)
    : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public string Method { get; } = method;
    public string Path { get; } = path;
}
