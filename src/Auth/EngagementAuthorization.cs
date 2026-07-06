using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;

namespace PasMigration.Auth;

/// <summary>
/// Authorization requirement: the caller may only act on the engagement named in the route
/// (the "{id}" route value on /api/engagements/{id:guid}/... endpoints). Membership comes
/// from the JWT's "eng_ids" claim — a comma-separated list of engagement GUIDs issued at
/// login from <c>AppUser.EngagementIds</c>. Semantics match that record: empty = all
/// engagements. Admins always pass. Routes without an "id" route value are not
/// engagement-scoped, so the requirement is vacuously satisfied there (any role part of the
/// same policy still applies).
/// </summary>
public sealed class EngagementMemberRequirement : IAuthorizationRequirement { }

/// <summary>
/// Evaluates <see cref="EngagementMemberRequirement"/>. Deny-by-default: the requirement is
/// only satisfied on an explicit <c>Succeed</c>; falling through leaves it unmet and the
/// pipeline returns 403. Pure claim/route logic — no I/O — so it is trivially unit-testable
/// (see test/UnitTests/EngagementAuthorizationTests.cs).
/// </summary>
public sealed class EngagementMemberHandler : AuthorizationHandler<EngagementMemberRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context, EngagementMemberRequirement requirement)
    {
        // Minimal APIs pass the HttpContext as the authorization resource. Anything else
        // means we can't see the route, so we leave the requirement unmet (deny).
        if (context.Resource is not HttpContext http)
            return Task.CompletedTask;

        // Admins are never engagement-restricted.
        if (context.User.IsInRole("admin"))
        {
            context.Succeed(requirement);
            return Task.CompletedTask;
        }

        // Not an engagement-routed endpoint (no "{id}" in the route): nothing to scope.
        var routeId = http.Request.RouteValues.TryGetValue("id", out var v) ? v?.ToString() : null;
        if (string.IsNullOrEmpty(routeId))
        {
            context.Succeed(requirement);
            return Task.CompletedTask;
        }

        // Empty claim = unrestricted (matches AppUser.EngagementIds: "empty = all engagements").
        var engIds = context.User.FindFirst("eng_ids")?.Value;
        if (string.IsNullOrWhiteSpace(engIds))
        {
            context.Succeed(requirement);
            return Task.CompletedTask;
        }

        var allowed = engIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (allowed.Contains(routeId, StringComparer.OrdinalIgnoreCase))
            context.Succeed(requirement);

        return Task.CompletedTask;
    }
}
