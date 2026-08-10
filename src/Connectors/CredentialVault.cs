using System.Collections.Concurrent;

namespace PasMigration.Connectors;

/// <summary>
/// In-memory session store for tenant credentials so the operator enters them once per
/// session instead of on every action. Credentials NEVER touch disk, are never logged, and
/// are cleared on container restart or after a sliding idle timeout (default 60 min).
///
/// This is the §4 "in-memory only by default" posture - not persistence. For migrations that
/// must survive restarts, the encrypted_credential / KMS path is the separate, deliberate choice.
///
/// Registered as a singleton so the store lives for the process lifetime.
/// </summary>
public sealed class CredentialVault : IDisposable
{
    private readonly record struct Entry(SessionCredentials Creds, DateTimeOffset LastUsedUtc);

    private readonly ConcurrentDictionary<string, Entry> _store = new();
    private readonly TimeSpan _idleTimeout;
    private readonly Timer _sweeper;

    public CredentialVault(TimeSpan? idleTimeout = null)
    {
        _idleTimeout = idleTimeout ?? TimeSpan.FromMinutes(60);
        // Sweep expired entries every 5 minutes.
        _sweeper = new Timer(_ => SweepExpired(), null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
    }

    private static string Key(Guid engagementId, string role) => $"{engagementId}:{role}";

    /// <summary>Store (or replace) credentials for an engagement+role.</summary>
    public void Put(Guid engagementId, string role, SessionCredentials creds) =>
        _store[Key(engagementId, role)] = new Entry(creds, DateTimeOffset.UtcNow);

    /// <summary>
    /// Get credentials if present and not idle-expired. Refreshes the last-used timestamp
    /// (sliding expiry). Returns null if absent or expired.
    /// </summary>
    public SessionCredentials? Get(Guid engagementId, string role)
    {
        var key = Key(engagementId, role);
        if (!_store.TryGetValue(key, out var e)) return null;
        if (DateTimeOffset.UtcNow - e.LastUsedUtc > _idleTimeout)
        {
            _store.TryRemove(key, out _);
            return null;
        }
        _store[key] = e with { LastUsedUtc = DateTimeOffset.UtcNow };
        return e.Creds;
    }

    /// <summary>Whether active (non-expired) credentials exist for a role.</summary>
    public bool Has(Guid engagementId, string role) => Get(engagementId, role) is not null;

    /// <summary>Forget credentials for an engagement (both roles) - e.g. on explicit sign-out.</summary>
    public void Clear(Guid engagementId)
    {
        foreach (var role in new[] { "source", "target" })
            _store.TryRemove(Key(engagementId, role), out _);
    }

    private void SweepExpired()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var kv in _store)
            if (now - kv.Value.LastUsedUtc > _idleTimeout)
                _store.TryRemove(kv.Key, out _);
    }

    public void Dispose() => _sweeper.Dispose();
}

/// <summary>
/// Credentials held for a session. Includes the connection metadata needed to act, so callers
/// don't have to re-supply URLs either. Secret material here lives in process memory only.
/// </summary>
public sealed record SessionCredentials(
    string SystemType,            // pas | cyberark | secret_server
    string AuthMode,
    string? BaseUrl,
    string? PlatformBaseUrl,
    string? SecretServerBaseUrl,
    string? AppId,
    string ClientId,
    string ClientSecret,
    string? Username,
    string? Scope,
    // CyberArk Privilege Cloud only: the CyberArk Identity token endpoint. It is on a different
    // hostname to the PVWA and cannot be derived from it, so it needs its own field. Trailing and
    // optional so existing encrypted blobs — serialized before this field existed — still
    // deserialize (JSON deserialization leaves it null).
    string? IdentityTokenUrl = null);
