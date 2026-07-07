using System.Data;
using Dapper;

namespace PasMigration.Data;

/// <summary>
/// Data-access seam for the <c>user_identity</c> table — the (provider, subject) → local user
/// link. Identity matching is ALWAYS on the OIDC <c>sub</c> claim, never email (emails change
/// and can be spoofed across IdPs). Minimal surface for now: the OIDC callback flow (later
/// step) resolves and links through these methods; the admin delete endpoint uses the count.
/// </summary>
public interface IUserIdentityRepository
{
    /// <summary>The local user linked to (provider, subject), or null if none.</summary>
    Task<Guid?> FindUserIdAsync(Guid providerId, string subject, CancellationToken ct = default);

    /// <summary>Creates the link. Fails on the (provider, subject) unique constraint if it exists.</summary>
    Task LinkAsync(Guid userId, Guid providerId, string subject, string? emailAtLink,
                   CancellationToken ct = default);

    /// <summary>Stamps last_login_at for an existing link.</summary>
    Task TouchLoginAsync(Guid providerId, string subject, CancellationToken ct = default);

    /// <summary>Number of identities linked to a provider (delete guard / admin display).</summary>
    Task<int> CountByProviderAsync(Guid providerId, CancellationToken ct = default);
}

public sealed class UserIdentityRepository(IDbConnection db) : IUserIdentityRepository
{
    public async Task<Guid?> FindUserIdAsync(Guid providerId, string subject, CancellationToken ct = default) =>
        await db.QuerySingleOrDefaultAsync<Guid?>(new CommandDefinition(
            "SELECT user_id FROM user_identity WHERE provider_id = @ProviderId AND subject = @Subject",
            new { ProviderId = providerId, Subject = subject }, cancellationToken: ct));

    public async Task LinkAsync(Guid userId, Guid providerId, string subject, string? emailAtLink,
                                CancellationToken ct = default) =>
        await db.ExecuteAsync(new CommandDefinition(
            @"INSERT INTO user_identity (user_id, provider_id, subject, email_at_link)
              VALUES (@UserId, @ProviderId, @Subject, @EmailAtLink)",
            new { UserId = userId, ProviderId = providerId, Subject = subject, EmailAtLink = emailAtLink },
            cancellationToken: ct));

    public async Task TouchLoginAsync(Guid providerId, string subject, CancellationToken ct = default) =>
        await db.ExecuteAsync(new CommandDefinition(
            @"UPDATE user_identity SET last_login_at = now()
              WHERE provider_id = @ProviderId AND subject = @Subject",
            new { ProviderId = providerId, Subject = subject }, cancellationToken: ct));

    public async Task<int> CountByProviderAsync(Guid providerId, CancellationToken ct = default) =>
        await db.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM user_identity WHERE provider_id = @ProviderId",
            new { ProviderId = providerId }, cancellationToken: ct));
}
