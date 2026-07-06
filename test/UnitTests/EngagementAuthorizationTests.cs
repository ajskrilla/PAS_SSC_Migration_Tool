using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using PasMigration.Auth;
using Xunit;

namespace PasMigration.UnitTests;

/// <summary>
/// Unit tests for <see cref="EngagementMemberHandler"/> — the engagement-scoping authorization
/// introduced with the "EngagementMember" / "OperatorWrite" policies. Pure claim/route logic,
/// no I/O. These lock in the security semantics: admin bypass, empty claim = all engagements,
/// membership match (case-insensitive), non-member denial, vacuous pass on non-engagement
/// routes, and deny-by-default on an unknown resource.
/// </summary>
public class EngagementAuthorizationTests
{
    private static readonly Guid EngA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid EngB = Guid.Parse("22222222-2222-2222-2222-222222222222");

    // ── Helpers ─────────────────────────────────────────────────────────────────────

    private static ClaimsPrincipal User(string role, string? engIds)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new(ClaimTypes.Role, role),
        };
        if (engIds is not null)
            claims.Add(new Claim("eng_ids", engIds));
        // authenticationType must be non-null so IsAuthenticated is true, matching a real JWT.
        return new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "test"));
    }

    private static async Task<bool> EvaluateAsync(ClaimsPrincipal user, object? resource)
    {
        var requirement = new EngagementMemberRequirement();
        var context = new AuthorizationHandlerContext(new[] { requirement }, user, resource);
        await new EngagementMemberHandler().HandleAsync(context);
        return context.HasSucceeded;
    }

    private static HttpContext HttpWithRoute(Guid? engagementId)
    {
        var http = new DefaultHttpContext();
        if (engagementId is { } id)
            http.Request.RouteValues["id"] = id.ToString();
        return http;
    }

    // ── Tests ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Admin_bypasses_engagement_restriction()
    {
        // Admin scoped (nonsensically) to EngA still passes for EngB — admins are never restricted.
        var ok = await EvaluateAsync(User("admin", EngA.ToString()), HttpWithRoute(EngB));
        Assert.True(ok);
    }

    [Fact]
    public async Task Member_of_routed_engagement_passes()
    {
        var ok = await EvaluateAsync(User("operator", $"{EngA},{EngB}"), HttpWithRoute(EngB));
        Assert.True(ok);
    }

    [Fact]
    public async Task Non_member_is_denied()
    {
        var ok = await EvaluateAsync(User("operator", EngA.ToString()), HttpWithRoute(EngB));
        Assert.False(ok);
    }

    [Fact]
    public async Task Empty_claim_means_all_engagements()
    {
        // Matches AppUser.EngagementIds semantics: empty = unrestricted.
        var okEmpty   = await EvaluateAsync(User("viewer", ""),   HttpWithRoute(EngB));
        var okMissing = await EvaluateAsync(User("viewer", null), HttpWithRoute(EngB));
        Assert.True(okEmpty);
        Assert.True(okMissing);
    }

    [Fact]
    public async Task Membership_match_is_case_insensitive()
    {
        var ok = await EvaluateAsync(
            User("viewer", EngA.ToString().ToUpperInvariant()), HttpWithRoute(EngA));
        Assert.True(ok);
    }

    [Fact]
    public async Task Route_without_engagement_id_is_not_scoped()
    {
        // e.g. /api/templates or /api/jobs/{jobId}/cancel — the requirement is vacuous there;
        // the role part of the policy (RequireRole) still applies separately.
        var ok = await EvaluateAsync(User("operator", EngA.ToString()), HttpWithRoute(null));
        Assert.True(ok);
    }

    [Fact]
    public async Task Unknown_resource_is_denied_by_default()
    {
        // If the resource isn't an HttpContext we can't see the route — never succeed.
        var ok = await EvaluateAsync(User("operator", EngA.ToString()), resource: null);
        Assert.False(ok);
    }
}
