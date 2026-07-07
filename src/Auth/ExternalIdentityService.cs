using Microsoft.Extensions.Logging;
using PasMigration.Data;

namespace PasMigration.Auth;

/// <summary>Claims extracted from a validated OIDC ticket. EmailVerified reflects the
/// email_verified claim; see <see cref="ExternalIdentityService"/> for how absence is treated.</summary>
public sealed record ExternalIdentity(string Subject, string? Email, bool EmailVerified, string? DisplayName);

/// <summary>Typed rejection reasons — each is an explicit branch, never a generic catch.
/// The reason is logged server-side and surfaced to the login page as a coarse error code.</summary>
public enum SsoRejection { UnknownUser, Deactivated, DomainNotAllowed }

/// <summary>Either a resolved local user or a typed rejection, never both.</summary>
public sealed record SsoResolution(AppUser? User, SsoRejection? Rejection)
{
    public static SsoResolution Success(AppUser user) => new(user, null);
    public static SsoResolution Reject(SsoRejection reason) => new(null, reason);
}

/// <summary>
/// Maps a validated external identity to a local <see cref="AppUser"/>. This is the ONLY
/// authorization decision the SSO flow makes — the IdP authenticates, the app authorizes.
/// The resolved user feeds <c>AuthService.GenerateTokenAsync</c>, which remains the sole
/// JWT authority; everything downstream is IdP-unaware.
/// </summary>
public interface IExternalIdentityService
{
    Task<SsoResolution> ResolveAsync(IdentityProviderRow provider, ExternalIdentity identity,
                                     CancellationToken ct = default);
}

/// <summary>
/// Resolution rules, in order:
/// 1. Domain allow-list (when configured) applies to EVERY sign-in.
/// 2. An existing (provider, subject) link wins — the OIDC 'sub' is the stable key, never email.
/// 3. No link: a pre-created ACTIVE local user with a matching email is linked exactly once.
///    Email is trusted for this one purpose only, and only when the IdP asserts it
///    (email_verified true, or the claim entirely absent — Entra ID omits it; an explicit
///    email_verified=false is never trusted). The caller encodes that rule.
/// 4. Otherwise UnknownUser. JIT provisioning is a LATER phase — the provider's JIT flag is
///    deliberately not honored yet, even when set in the admin UI.
/// Deactivation wins everywhere: an inactive user is rejected even with a valid IdP assertion.
/// </summary>
public sealed class ExternalIdentityService(
    IUserIdentityRepository identities,
    IUserRepository users,
    ILogger<ExternalIdentityService> log) : IExternalIdentityService
{
    public async Task<SsoResolution> ResolveAsync(IdentityProviderRow provider, ExternalIdentity identity,
                                                  CancellationToken ct = default)
    {
        // 1. Domain allow-list (empty = any). No email claim at all fails a configured list.
        if (provider.AllowedEmailDomains.Length > 0)
        {
            var domain = identity.Email?.Split('@').LastOrDefault();
            if (string.IsNullOrEmpty(domain) ||
                !provider.AllowedEmailDomains.Contains(domain, StringComparer.OrdinalIgnoreCase))
            {
                log.LogWarning("SSO rejected (domain): provider={Slug} sub={Sub} email-domain={Domain}",
                    provider.Slug, identity.Subject, domain ?? "(none)");
                return SsoResolution.Reject(SsoRejection.DomainNotAllowed);
            }
        }

        // 2. Existing link on (provider, subject).
        var linkedUserId = await identities.FindUserIdAsync(provider.Id, identity.Subject, ct);
        if (linkedUserId is { } uid)
        {
            var user = await users.GetByIdAsync(uid, ct);
            if (user is null || !user.IsActive)
            {
                log.LogWarning("SSO rejected (deactivated): provider={Slug} sub={Sub} user={UserId}",
                    provider.Slug, identity.Subject, uid);
                return SsoResolution.Reject(SsoRejection.Deactivated);
            }
            await identities.TouchLoginAsync(provider.Id, identity.Subject, ct);
            await users.TouchLastLoginAsync(user.Id, ct);
            return SsoResolution.Success(user);
        }

        // 3. First-time link of a pre-created local user by asserted email. The repository
        //    method only returns ACTIVE users, so a deactivated account can't be linked.
        if (!string.IsNullOrEmpty(identity.Email) && identity.EmailVerified)
        {
            var found = await users.FindActiveByLoginWithHashAsync(identity.Email, ct);
            if (found is not null)
            {
                await identities.LinkAsync(found.User.Id, provider.Id, identity.Subject, identity.Email, ct);
                await identities.TouchLoginAsync(provider.Id, identity.Subject, ct);
                await users.TouchLastLoginAsync(found.User.Id, ct);
                log.LogInformation("SSO first-time link: provider={Slug} sub={Sub} user={UserId}",
                    provider.Slug, identity.Subject, found.User.Id);
                return SsoResolution.Success(found.User);
            }
        }

        // 4. Nobody to be. (JIT provisioning arrives in a later step.)
        log.LogWarning("SSO rejected (unknown): provider={Slug} sub={Sub}", provider.Slug, identity.Subject);
        return SsoResolution.Reject(SsoRejection.UnknownUser);
    }
}
