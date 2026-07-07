using Microsoft.Extensions.Logging.Abstractions;
using PasMigration.Auth;
using PasMigration.Data;
using Xunit;

namespace PasMigration.UnitTests;

/// <summary>
/// Resolution matrix for <see cref="ExternalIdentityService"/> (SSO step 2): existing link,
/// deactivated link, first-time email link (verified / explicitly-unverified), unknown user,
/// domain allow-list, and the rule that the JIT flag is not yet honored. All fakes, no I/O.
/// </summary>
public class ExternalIdentityServiceTests
{
    private static readonly Guid ProviderId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid UserId     = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    // ── Fakes ───────────────────────────────────────────────────────────────────────

    private sealed class FakeIdentities : IUserIdentityRepository
    {
        public Dictionary<(Guid, string), Guid> Links { get; } = new();
        public int TouchCalls { get; private set; }

        public Task<Guid?> FindUserIdAsync(Guid providerId, string subject, CancellationToken ct = default) =>
            Task.FromResult<Guid?>(Links.TryGetValue((providerId, subject), out var uid) ? uid : null);

        public Task LinkAsync(Guid userId, Guid providerId, string subject, string? emailAtLink, CancellationToken ct = default)
        { Links[(providerId, subject)] = userId; return Task.CompletedTask; }

        public Task TouchLoginAsync(Guid providerId, string subject, CancellationToken ct = default)
        { TouchCalls++; return Task.CompletedTask; }

        public Task<int> CountByProviderAsync(Guid providerId, CancellationToken ct = default) =>
            Task.FromResult(Links.Count(kv => kv.Key.Item1 == providerId));
    }

    private sealed class FakeUsers : IUserRepository
    {
        public List<AppUser> All { get; } = new();
        public int TouchCalls { get; private set; }

        public Task<UserWithHash?> FindActiveByLoginWithHashAsync(string usernameOrEmail, CancellationToken ct = default)
        {
            var u = All.FirstOrDefault(x => x.IsActive &&
                (string.Equals(x.Email, usernameOrEmail, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(x.Username, usernameOrEmail, StringComparison.OrdinalIgnoreCase)));
            return Task.FromResult<UserWithHash?>(u is null ? null : new UserWithHash(u, "$2b$12$fakehash"));
        }

        public Task<AppUser?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
            Task.FromResult(All.FirstOrDefault(x => x.Id == id));

        public Task TouchLastLoginAsync(Guid userId, CancellationToken ct = default)
        { TouchCalls++; return Task.CompletedTask; }

        // Unused by ExternalIdentityService — fail loudly if that ever changes.
        public Task<string?> GetPasswordHashAsync(Guid userId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SetPasswordAsync(Guid userId, string newHash, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<int> SetPasswordForceChangeAsync(Guid userId, string newHash, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> ExistsByUsernameOrEmailAsync(string username, string email, CancellationToken ct = default) => throw new NotSupportedException();
        public Task InsertAsync(Guid id, string email, string username, string displayName, string role,
                                string passwordHash, string[] engagementIds, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<AppUser>> ListAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<int> DeactivateAsync(Guid userId, CancellationToken ct = default) => throw new NotSupportedException();
    }

    // ── Builders ────────────────────────────────────────────────────────────────────

    private static IdentityProviderRow Provider(string[]? domains = null, bool jit = false) => new()
    {
        Id = ProviderId, Name = "Test IdP", Slug = "test-idp", Type = "oidc",
        Authority = "https://idp.example", ClientId = "client",
        Enabled = true, JitProvisioning = jit, DefaultRole = "viewer",
        AllowedEmailDomains = domains ?? [],
    };

    private static AppUser User(bool active = true) =>
        new(UserId, "jane", "jane@contoso.com", "Jane", "operator",
            ForcePasswordChange: false, IsActive: active, EngagementIds: []);

    private static ExternalIdentityService Make(FakeIdentities ids, FakeUsers users) =>
        new(ids, users, NullLogger<ExternalIdentityService>.Instance);

    private static ExternalIdentity Identity(
        string sub = "sub-123", string? email = "jane@contoso.com",
        bool verified = true, string? name = "Jane") => new(sub, email, verified, name);

    // ── Matrix ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Existing_link_resolves_and_touches()
    {
        var ids = new FakeIdentities(); var users = new FakeUsers();
        users.All.Add(User());
        ids.Links[(ProviderId, "sub-123")] = UserId;

        var r = await Make(ids, users).ResolveAsync(Provider(), Identity());

        Assert.NotNull(r.User);
        Assert.Equal(UserId, r.User!.Id);
        Assert.Equal(1, ids.TouchCalls);
        Assert.Equal(1, users.TouchCalls);
    }

    [Fact]
    public async Task Deactivated_linked_user_is_rejected_despite_valid_assertion()
    {
        var ids = new FakeIdentities(); var users = new FakeUsers();
        users.All.Add(User(active: false));
        ids.Links[(ProviderId, "sub-123")] = UserId;

        var r = await Make(ids, users).ResolveAsync(Provider(), Identity());

        Assert.Null(r.User);
        Assert.Equal(SsoRejection.Deactivated, r.Rejection);
    }

    [Fact]
    public async Task Verified_email_links_precreated_user_once()
    {
        var ids = new FakeIdentities(); var users = new FakeUsers();
        users.All.Add(User());

        var r = await Make(ids, users).ResolveAsync(Provider(), Identity(verified: true));

        Assert.NotNull(r.User);
        Assert.True(ids.Links.ContainsKey((ProviderId, "sub-123")));   // link persisted
        Assert.Equal(UserId, ids.Links[(ProviderId, "sub-123")]);
    }

    [Fact]
    public async Task Explicitly_unverified_email_never_links()
    {
        var ids = new FakeIdentities(); var users = new FakeUsers();
        users.All.Add(User());

        var r = await Make(ids, users).ResolveAsync(Provider(), Identity(verified: false));

        Assert.Null(r.User);
        Assert.Equal(SsoRejection.UnknownUser, r.Rejection);
        Assert.Empty(ids.Links);
    }

    [Fact]
    public async Task Unknown_user_rejected_even_when_jit_flag_is_set()
    {
        // JIT provisioning is a later phase; the flag must be inert until then.
        var ids = new FakeIdentities(); var users = new FakeUsers();

        var r = await Make(ids, users).ResolveAsync(Provider(jit: true), Identity(email: "nobody@contoso.com"));

        Assert.Null(r.User);
        Assert.Equal(SsoRejection.UnknownUser, r.Rejection);
    }

    [Theory]
    [InlineData("jane@evil.com")]   // wrong domain
    [InlineData(null)]              // no email claim at all
    public async Task Domain_allow_list_rejects(string? email)
    {
        var ids = new FakeIdentities(); var users = new FakeUsers();
        users.All.Add(User());
        ids.Links[(ProviderId, "sub-123")] = UserId;  // even an existing link doesn't bypass it

        var r = await Make(ids, users).ResolveAsync(
            Provider(domains: ["contoso.com"]), Identity(email: email));

        Assert.Null(r.User);
        Assert.Equal(SsoRejection.DomainNotAllowed, r.Rejection);
    }

    [Fact]
    public async Task Domain_allow_list_is_case_insensitive()
    {
        var ids = new FakeIdentities(); var users = new FakeUsers();
        users.All.Add(User());
        ids.Links[(ProviderId, "sub-123")] = UserId;

        var r = await Make(ids, users).ResolveAsync(
            Provider(domains: ["CONTOSO.COM"]), Identity(email: "jane@contoso.com"));

        Assert.NotNull(r.User);
    }
}
