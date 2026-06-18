namespace PasMigration.Connectors;

/// <summary>
/// Credentials for a single migration run. Held in memory only, never written to the
/// database and never logged (per the security model). Implements IDisposable so the
/// caller can scrub the secret material as soon as the run completes / on teardown.
/// </summary>
public sealed class TenantCredentials : IDisposable
{
    public required string ClientId { get; init; }

    // The secret material. Deliberately not auto-property-printed; never include in logs.
    public required string ClientSecret { get; init; }

    /// <summary>OAuth scope, where the tenant requires one.</summary>
    public string? Scope { get; init; }

    public void Dispose()
    {
        // Best-effort: there is no guaranteed scrub for managed strings in .NET, but we
        // avoid holding references. For stronger guarantees, hold secrets in a
        // pinned char[]/SecureString-style buffer. Documented as an open hardening item.
        GC.SuppressFinalize(this);
    }

    /// <summary>Never expose secret material in ToString (avoids accidental logging).</summary>
    public override string ToString() => $"TenantCredentials(ClientId={ClientId})";
}
