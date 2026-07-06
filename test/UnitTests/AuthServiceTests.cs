using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using PasMigration.Auth;
using PasMigration.Data;
using Xunit;

namespace PasMigration.UnitTests;

/// <summary>
/// Unit tests for <see cref="AuthService"/> using a hand-written fake <see cref="IUserRepository"/>.
/// No database, no network — deterministic. These lock in the security-critical login and
/// change-password behavior so a regression (e.g. accepting a bad password) can't ship silently.
/// </summary>
public class AuthServiceTests
{
    // ── Test doubles ────────────────────────────────────────────────────────────────

    /// <summary>In-memory fake repository. Only the methods the tests exercise are meaningful.</summary>
    private sealed class FakeUserRepository : IUserRepository
    {
        public UserWithHash? SeededUser { get; set; }
        public string? SeededHashById { get; set; }
        public Guid? LastTouchedLogin { get; private set; }
        public (Guid Id, string Hash)? LastSetPassword { get; private set; }

        public Task<UserWithHash?> FindActiveByLoginWithHashAsync(string usernameOrEmail, CancellationToken ct = default)
            => Task.FromResult(SeededUser);

        public Task<string?> GetPasswordHashAsync(Guid userId, CancellationToken ct = default)
            => Task.FromResult(SeededHashById);

        public Task TouchLastLoginAsync(Guid userId, CancellationToken ct = default)
        { LastTouchedLogin = userId; return Task.CompletedTask; }

        public Task SetPasswordAsync(Guid userId, string newHash, CancellationToken ct = default)
        { LastSetPassword = (userId, newHash); return Task.CompletedTask; }

        public Task<int> SetPasswordForceChangeAsync(Guid userId, string newHash, CancellationToken ct = default)
            => Task.FromResult(1);

        public Task<bool> ExistsByUsernameOrEmailAsync(string username, string email, CancellationToken ct = default)
            => Task.FromResult(false);

        public Task InsertAsync(Guid id, string email, string username, string displayName, string role,
                                string passwordHash, string[] engagementIds, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<AppUser>> ListAsync(CancellationToken ct = default)
            => Task.FromResult((IReadOnlyList<AppUser>)new List<AppUser>());

        public Task<AppUser?> GetByIdAsync(Guid id, CancellationToken ct = default)
            => Task.FromResult<AppUser?>(null);

        public Task<int> DeactivateAsync(Guid userId, CancellationToken ct = default)
            => Task.FromResult(1);
    }

    /// <summary>In-memory fake settings store. Empty by default — tests exercise AuthService's
    /// fallback-to-default behavior, not a specific configured timeout.</summary>
    private sealed class FakeSettingsRepository : ISettingsRepository
    {
        private readonly Dictionary<string, string> _values = new();
        public Task<string?> GetAsync(string key, CancellationToken ct = default)
            => Task.FromResult(_values.TryGetValue(key, out var v) ? v : null);
        public Task SetAsync(string key, string value, CancellationToken ct = default)
        { _values[key] = value; return Task.CompletedTask; }
    }

    private static AuthService MakeService(FakeUserRepository repo)
    {
        // A JWT secret long enough for HMAC-SHA256; supplied via in-memory config.
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Auth__JwtSecret"] = "test-only-secret-value-at-least-32-chars-long!!"
            })
            .Build();
        return new AuthService(repo, new FakeSettingsRepository(), cfg, NullLogger<AuthService>.Instance);
    }

    private static AppUser SampleUser(bool forcePwd = false) => new(
        Id: Guid.NewGuid(), Username: "alice", Email: "alice@example.com",
        DisplayName: "Alice", Role: "operator",
        ForcePasswordChange: forcePwd, IsActive: true, EngagementIds: Array.Empty<Guid>());

    // ── Login ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Login_with_correct_password_succeeds_and_issues_token()
    {
        var user = SampleUser();
        var hash = BCrypt.Net.BCrypt.HashPassword("CorrectHorse1!", 12);
        var repo = new FakeUserRepository { SeededUser = new UserWithHash(user, hash) };
        var svc  = MakeService(repo);

        var result = await svc.LoginAsync(new LoginRequest("alice", "CorrectHorse1!"));

        Assert.True(result.Success);
        Assert.NotNull(result.Token);
        Assert.Equal(user.Id, result.User!.Id);
        Assert.Equal(user.Id, repo.LastTouchedLogin);   // last_login stamped
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task Login_with_wrong_password_fails_without_token()
    {
        var hash = BCrypt.Net.BCrypt.HashPassword("CorrectHorse1!", 12);
        var repo = new FakeUserRepository { SeededUser = new UserWithHash(SampleUser(), hash) };
        var svc  = MakeService(repo);

        var result = await svc.LoginAsync(new LoginRequest("alice", "WrongPassword9!"));

        Assert.False(result.Success);
        Assert.Null(result.Token);
        Assert.Null(result.User);
        Assert.NotNull(result.Error);
        Assert.Null(repo.LastTouchedLogin);             // no login stamp on failure
    }

    [Fact]
    public async Task Login_with_unknown_user_fails()
    {
        var repo = new FakeUserRepository { SeededUser = null };   // no such user
        var svc  = MakeService(repo);

        var result = await svc.LoginAsync(new LoginRequest("nobody", "whatever1!"));

        Assert.False(result.Success);
        Assert.Null(result.Token);
    }

    // ── Change password ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task ChangePassword_rejects_too_short()
    {
        var repo = new FakeUserRepository { SeededHashById = BCrypt.Net.BCrypt.HashPassword("OldPassw0rd!", 12) };
        var svc  = MakeService(repo);

        var (ok, err) = await svc.ChangePasswordAsync(Guid.NewGuid(),
            new ChangePasswordRequest("OldPassw0rd!", "Short1!"));

        Assert.False(ok);
        Assert.NotNull(err);
        Assert.Null(repo.LastSetPassword);              // nothing persisted
    }

    [Fact]
    public async Task ChangePassword_rejects_wrong_current_password()
    {
        var repo = new FakeUserRepository { SeededHashById = BCrypt.Net.BCrypt.HashPassword("OldPassw0rd!", 12) };
        var svc  = MakeService(repo);

        var (ok, err) = await svc.ChangePasswordAsync(Guid.NewGuid(),
            new ChangePasswordRequest("NotTheOldOne1!", "BrandNewValid9!"));

        Assert.False(ok);
        Assert.NotNull(err);
        Assert.Null(repo.LastSetPassword);
    }

    [Fact]
    public async Task ChangePassword_succeeds_with_valid_new_password()
    {
        var userId = Guid.NewGuid();
        var repo = new FakeUserRepository { SeededHashById = BCrypt.Net.BCrypt.HashPassword("OldPassw0rd!", 12) };
        var svc  = MakeService(repo);

        var (ok, err) = await svc.ChangePasswordAsync(userId,
            new ChangePasswordRequest("OldPassw0rd!", "BrandNewValid9!"));

        Assert.True(ok);
        Assert.Null(err);
        Assert.NotNull(repo.LastSetPassword);
        Assert.Equal(userId, repo.LastSetPassword!.Value.Id);
        // The persisted value is a BCrypt hash, never the plaintext.
        Assert.StartsWith("$2", repo.LastSetPassword!.Value.Hash);
        Assert.DoesNotContain("BrandNewValid9!", repo.LastSetPassword!.Value.Hash);
    }
}
