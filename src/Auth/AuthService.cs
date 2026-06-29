using System.Data;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Dapper;
using Microsoft.IdentityModel.Tokens;

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

public sealed class AuthService(IDbConnection db, IConfiguration cfg, ILogger<AuthService> log)
{
    private const int BcryptCost = 12;

    // ── Login ────────────────────────────────────────────────────────────────────────

    public async Task<LoginResult> LoginAsync(LoginRequest req)
    {
        var row = await db.QueryFirstOrDefaultAsync(
            @"SELECT id, username, email, display_name, role,
                     password_hash, force_password_change, is_active,
                     engagement_ids
              FROM app_user
              WHERE (username = @u OR email = @u) AND is_active = true",
            new { u = req.Username });

        if (row is null)
            return new(false, null, null, "Invalid username or password.");

        var d = (IDictionary<string, object?>)row;
        var hash = d["password_hash"]?.ToString();

        if (string.IsNullOrEmpty(hash) || !BCrypt.Net.BCrypt.Verify(req.Password, hash))
        {
            log.LogWarning("Failed login for {User}", req.Username);
            return new(false, null, null, "Invalid username or password.");
        }

        var user = MapRow(d);
        await db.ExecuteAsync(
            "UPDATE app_user SET last_login_at = now() WHERE id = @id",
            new { id = user.Id });

        var token = GenerateToken(user);
        log.LogInformation("Login: {User} role={Role}", user.Username, user.Role);
        return new(true, token, user, null);
    }

    // ── Change password ──────────────────────────────────────────────────────────────

    public async Task<(bool Ok, string? Error)> ChangePasswordAsync(Guid userId, ChangePasswordRequest req)
    {
        if (req.NewPassword.Length < 10)
            return (false, "Password must be at least 10 characters.");
        if (!HasComplexity(req.NewPassword))
            return (false, "Password must contain uppercase, lowercase, a number, and a special character.");

        var row = await db.QueryFirstOrDefaultAsync(
            "SELECT password_hash FROM app_user WHERE id = @id",
            new { id = userId });

        if (row is null) return (false, "User not found.");
        var d = (IDictionary<string, object?>)row;
        var hash = d["password_hash"]?.ToString();

        if (!string.IsNullOrEmpty(hash) && !BCrypt.Net.BCrypt.Verify(req.CurrentPassword, hash))
            return (false, "Current password is incorrect.");

        var newHash = BCrypt.Net.BCrypt.HashPassword(req.NewPassword, BcryptCost);
        await db.ExecuteAsync(
            "UPDATE app_user SET password_hash=@h, force_password_change=false WHERE id=@id",
            new { h = newHash, id = userId });

        log.LogInformation("Password changed for user {UserId}", userId);
        return (true, null);
    }

    // ── Admin: create user ────────────────────────────────────────────────────────────

    public async Task<(bool Ok, string? Error, AppUser? User)> CreateUserAsync(CreateUserRequest req)
    {
        if (await db.ExecuteScalarAsync<bool>(
            "SELECT EXISTS(SELECT 1 FROM app_user WHERE username=@u OR email=@e)",
            new { u = req.Username, e = req.Email }))
            return (false, "Username or email already exists.", null);

        // Generate a temporary password or use provided one
        var tempPassword = string.IsNullOrEmpty(req.InitialPassword)
            ? GenerateTempPassword()
            : req.InitialPassword;

        var hash = BCrypt.Net.BCrypt.HashPassword(tempPassword, BcryptCost);
        var id   = Guid.NewGuid();

        await db.ExecuteAsync(
            @"INSERT INTO app_user (id, email, username, display_name, role,
                                    password_hash, force_password_change, engagement_ids)
              VALUES (@id, @email, @username, @displayName, @role,
                      @hash, true, @engIds::uuid[])",
            new
            {
                id,
                email       = req.Email,
                username    = req.Username,
                displayName = req.DisplayName ?? req.Username,
                role        = req.Role,
                hash,
                engIds      = req.EngagementIds?.Select(e => e.ToString()).ToArray() ?? Array.Empty<string>(),
            });

        var user = await GetUserAsync(id);
        log.LogInformation("Created user {Username} role={Role}", req.Username, req.Role);
        return (true, $"User created. Temporary password: {tempPassword}", user);
    }

    public async Task<List<AppUser>> ListUsersAsync() =>
        (await db.QueryAsync(
            @"SELECT id, username, email, display_name, role,
                     password_hash, force_password_change, is_active, engagement_ids
              FROM app_user ORDER BY created_at"))
        .Select(r => MapRow((IDictionary<string, object?>)r))
        .ToList();

    public async Task<(bool Ok, string? Error)> DeactivateUserAsync(Guid userId) =>
        await db.ExecuteAsync(
            "UPDATE app_user SET is_active=false WHERE id=@id", new { id = userId }) > 0
            ? (true, null) : (false, "User not found.");

    public async Task<(bool Ok, string? Error)> ResetPasswordAsync(Guid userId)
    {
        var temp = GenerateTempPassword();
        var hash = BCrypt.Net.BCrypt.HashPassword(temp, BcryptCost);
        var ok   = await db.ExecuteAsync(
            "UPDATE app_user SET password_hash=@h, force_password_change=true WHERE id=@id",
            new { h = hash, id = userId }) > 0;
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

    private async Task<AppUser?> GetUserAsync(Guid id)
    {
        var row = await db.QueryFirstOrDefaultAsync(
            @"SELECT id, username, email, display_name, role,
                     password_hash, force_password_change, is_active, engagement_ids
              FROM app_user WHERE id=@id", new { id });
        return row is null ? null : MapRow((IDictionary<string, object?>)row);
    }

    private static AppUser MapRow(IDictionary<string, object?> d) => new(
        Id:                  (Guid)(d["id"] ?? Guid.Empty),
        Username:            d["username"]?.ToString() ?? "",
        Email:               d["email"]?.ToString()    ?? "",
        DisplayName:         d["display_name"]?.ToString() ?? "",
        Role:                d["role"]?.ToString()     ?? "viewer",
        ForcePasswordChange: d["force_password_change"] is true,
        IsActive:            d["is_active"] is not false,
        EngagementIds:       d["engagement_ids"] is Guid[] arr ? arr : Array.Empty<Guid>());

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
