using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using PasMigration.Data;

namespace PasMigration.Auth;

public sealed record AppUser(
    Guid Id,
    string Username,
    string Email,
    string DisplayName,
    string Role,               // admin | operator | viewer
    bool ForcePasswordChange,
    bool IsActive,
    Guid[] EngagementIds);     // empty = all engagements

public sealed record LoginRequest(string Username, string Password);
public sealed record LoginResult(bool Success, string? Token, AppUser? User, string? Error);
public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);

/// <summary>
/// Authentication + user management. Owns password hashing (BCrypt cost 12), credential
/// verification, and JWT issuance. All persistence is delegated to <see cref="IUserRepository"/> —
/// this class contains no SQL. The cryptographic behavior is unchanged from the pre-DAL version:
/// BCrypt.Verify / HashPassword calls happen here, exactly as before.
/// </summary>
public sealed class AuthService(IUserRepository users, IConfiguration cfg, ILogger<AuthService> log)
{
    private const int BcryptCost = 12;

    // ── Login ────────────────────────────────────────────────────────────────────────

    public async Task<LoginResult> LoginAsync(LoginRequest req, CancellationToken ct = default)
    {
        var found = await users.FindActiveByLoginWithHashAsync(req.Username, ct);
        if (found is null)
            return new(false, null, null, "Invalid username or password.");

        if (string.IsNullOrEmpty(found.PasswordHash) ||
            !BCrypt.Net.BCrypt.Verify(req.Password, found.PasswordHash))
        {
            log.LogWarning("Failed login for {User}", req.Username);
            return new(false, null, null, "Invalid username or password.");
        }

        var user = found.User;
        await users.TouchLastLoginAsync(user.Id, ct);

        var token = GenerateToken(user);
        log.LogInformation("Login: {User} role={Role}", user.Username, user.Role);
        return new(true, token, user, null);
    }

    // ── Change password ──────────────────────────────────────────────────────────────

    public async Task<(bool Ok, string? Error)> ChangePasswordAsync(
        Guid userId, ChangePasswordRequest req, CancellationToken ct = default)
    {
        if (req.NewPassword.Length < 10)
            return (false, "Password must be at least 10 characters.");
        if (!HasComplexity(req.NewPassword))
            return (false, "Password must contain uppercase, lowercase, a number, and a special character.");

        var hash = await users.GetPasswordHashAsync(userId, ct);
        if (hash is null) return (false, "User not found.");

        if (!string.IsNullOrEmpty(hash) && !BCrypt.Net.BCrypt.Verify(req.CurrentPassword, hash))
            return (false, "Current password is incorrect.");

        var newHash = BCrypt.Net.BCrypt.HashPassword(req.NewPassword, BcryptCost);
        await users.SetPasswordAsync(userId, newHash, ct);

        log.LogInformation("Password changed for user {UserId}", userId);
        return (true, null);
    }

    // ── Admin: create user ────────────────────────────────────────────────────────────

    public async Task<(bool Ok, string? Error, AppUser? User)> CreateUserAsync(
        CreateUserRequest req, CancellationToken ct = default)
    {
        if (await users.ExistsByUsernameOrEmailAsync(req.Username, req.Email, ct))
            return (false, "Username or email already exists.", null);

        // Generate a temporary password or use provided one
        var tempPassword = string.IsNullOrEmpty(req.InitialPassword)
            ? GenerateTempPassword()
            : req.InitialPassword;

        var hash = BCrypt.Net.BCrypt.HashPassword(tempPassword, BcryptCost);
        var id   = Guid.NewGuid();

        var engIds = req.EngagementIds?.Select(e => e.ToString()).ToArray() ?? Array.Empty<string>();
        await users.InsertAsync(id, req.Email, req.Username, req.DisplayName ?? req.Username,
                                req.Role, hash, engIds, ct);

        var user = await users.GetByIdAsync(id, ct);
        log.LogInformation("Created user {Username} role={Role}", req.Username, req.Role);
        return (true, $"User created. Temporary password: {tempPassword}", user);
    }

    public async Task<List<AppUser>> ListUsersAsync(CancellationToken ct = default) =>
        (await users.ListAsync(ct)).ToList();

    public async Task<(bool Ok, string? Error)> DeactivateUserAsync(Guid userId, CancellationToken ct = default) =>
        await users.DeactivateAsync(userId, ct) > 0
            ? (true, null) : (false, "User not found.");

    public async Task<(bool Ok, string? Error)> ResetPasswordAsync(Guid userId, CancellationToken ct = default)
    {
        var temp = GenerateTempPassword();
        var hash = BCrypt.Net.BCrypt.HashPassword(temp, BcryptCost);
        var ok   = await users.SetPasswordForceChangeAsync(userId, hash, ct) > 0;
        return ok ? (true, $"Temporary password: {temp}") : (false, "User not found.");
    }

    // ── JWT ───────────────────────────────────────────────────────────────────────────

    public string GenerateToken(AppUser user)
    {
        var key  = GetKey();
        var creds= new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name,           user.Username),
            new(ClaimTypes.Email,          user.Email),
            new(ClaimTypes.Role,           user.Role),
            new("force_pwd",               user.ForcePasswordChange.ToString().ToLower()),
            new("eng_ids",                 string.Join(",", user.EngagementIds)),
        };

        var token = new JwtSecurityToken(
            issuer:             "pas-migration",
            audience:           "pas-migration",
            claims:             claims,
            expires:            DateTime.UtcNow.AddHours(8),
            signingCredentials: creds);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public SymmetricSecurityKey GetKey()
    {
        var secret = cfg["Auth__JwtSecret"]
                  ?? Environment.GetEnvironmentVariable("AUTH_JWT_SECRET")
                  ?? "dev-secret-change-in-production-min-32-chars!!";
        return new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────────

    private static bool HasComplexity(string p) =>
        p.Any(char.IsUpper) && p.Any(char.IsLower) &&
        p.Any(char.IsDigit) && p.Any(c => !char.IsLetterOrDigit(c));

    private static string GenerateTempPassword()
    {
        const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghjkmnpqrstuvwxyz23456789!@#$";
        var rng  = System.Security.Cryptography.RandomNumberGenerator.Create();
        var bytes = new byte[16];
        rng.GetBytes(bytes);
        return new string(bytes.Select(b => chars[b % chars.Length]).ToArray());
    }
}

public sealed record CreateUserRequest(
    string Username,
    string Email,
    string? DisplayName,
    string Role,               // admin | operator | viewer
    string? InitialPassword,
    Guid[]? EngagementIds);
