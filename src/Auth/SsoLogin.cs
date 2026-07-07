using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PasMigration.Data;

namespace PasMigration.Auth;

/// <summary>
/// Slug → (ASP.NET authentication scheme, provider id) lookup for the OIDC schemes registered
/// at startup. The challenge endpoint uses it to 404 unknown/disabled slugs instead of letting
/// an unknown-scheme challenge produce a 500. Populated once at startup: adding or enabling a
/// provider requires an api restart to take effect (dynamic scheme reload is a later phase).
/// </summary>
public sealed class SsoSchemeRegistry
{
    private readonly Dictionary<string, (string Scheme, Guid ProviderId)> _bySlug =
        new(StringComparer.OrdinalIgnoreCase);

    public void Register(string slug, string scheme, Guid providerId) =>
        _bySlug[slug] = (scheme, providerId);

    public bool TryGetScheme(string slug, out string scheme)
    {
        if (_bySlug.TryGetValue(slug, out var entry)) { scheme = entry.Scheme; return true; }
        scheme = "";
        return false;
    }

    public int Count => _bySlug.Count;
}

/// <summary>
/// Completes an OIDC sign-in: the built-in OpenIdConnect handler has already validated the
/// token (issuer, audience, nonce, PKCE, signature via metadata discovery) by the time
/// TicketReceived fires. This class only maps the external identity to a local user and
/// issues the SAME httpOnly JWT cookie the password login issues — the IdP authenticates,
/// the app authorizes, and everything downstream stays IdP-unaware.
/// </summary>
public static class SsoLogin
{
    public static async Task CompleteAsync(TicketReceivedContext ctx, Guid providerId, string slug)
    {
        // We own the response from here: never sign in to the carrier cookie scheme.
        ctx.HandleResponse();

        var sp        = ctx.HttpContext.RequestServices;
        var providers = sp.GetRequiredService<IIdentityProviderRepository>();
        var external  = sp.GetRequiredService<IExternalIdentityService>();
        var auth      = sp.GetRequiredService<AuthService>();
        var log       = sp.GetRequiredService<ILoggerFactory>().CreateLogger("PasMigration.Auth.SsoLogin");

        var principal = ctx.Principal;
        // The handler's default inbound claim mapping may rename 'sub'/'email' to the
        // ClaimTypes URIs — accept either form.
        var sub   = principal?.FindFirst("sub")?.Value
                 ?? principal?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        var email = principal?.FindFirst("email")?.Value
                 ?? principal?.FindFirst(ClaimTypes.Email)?.Value;
        var name  = principal?.FindFirst("name")?.Value
                 ?? principal?.FindFirst(ClaimTypes.Name)?.Value;

        // email_verified handling (deliberate interop decision, mirrored in
        // ExternalIdentityService docs): claim absent → trust the IdP-asserted email
        // (Entra ID omits the claim); claim explicitly "false" → do NOT trust.
        var verifiedClaim = principal?.FindFirst("email_verified")?.Value;
        var emailVerified = verifiedClaim is null
                         || string.Equals(verifiedClaim, "true", StringComparison.OrdinalIgnoreCase);

        if (string.IsNullOrEmpty(sub))
        {
            log.LogWarning("SSO ticket without a subject claim: provider={Slug}", slug);
            ctx.Response.Redirect("/?sso_error=no_subject");
            return;
        }

        // Re-read the provider row fresh: config (enabled flag, allowed domains) is always
        // current even though the SCHEME registration is startup-time.
        var provider = await providers.GetByIdAsync(providerId);
        if (provider is null || !provider.Enabled)
        {
            log.LogWarning("SSO callback for missing/disabled provider: {Slug}", slug);
            ctx.Response.Redirect("/?sso_error=provider_disabled");
            return;
        }

        var resolution = await external.ResolveAsync(provider,
            new ExternalIdentity(sub, email, emailVerified, name));

        if (resolution.User is null)
        {
            // Reason already logged with detail by ExternalIdentityService; the client gets
            // only a coarse code.
            ctx.Response.Redirect($"/?sso_error={resolution.Rejection.ToString()!.ToLowerInvariant()}");
            return;
        }

        var (token, expires) = await auth.GenerateTokenAsync(resolution.User);

        // SameSite=Lax here, unlike the Strict cookie set by password login: the browser
        // arrives at this callback via a cross-site redirect from the IdP, and a Strict
        // cookie would not be sent on the very next navigation of that redirect chain —
        // the user would land on "/" apparently logged out until a manual refresh. Lax
        // still withholds the cookie from cross-site subresource/POST requests.
        ctx.Response.Cookies.Append("auth_token", token, new CookieOptions
        {
            HttpOnly = true,
            Secure   = true,
            SameSite = SameSiteMode.Lax,
            Expires  = expires,
        });

        log.LogInformation("SSO sign-in: provider={Slug} user={UserId} role={Role}",
            slug, resolution.User.Id, resolution.User.Role);
        ctx.Response.Redirect("/");
    }
}
