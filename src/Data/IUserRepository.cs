using System.Data;
using Dapper;
using PasMigration.Auth;

namespace PasMigration.Data;

/// <summary>
/// Carries an <see cref="AppUser"/> together with its stored BCrypt hash. Used ONLY on the two
/// credential-verification paths (login, change-password) where <c>AuthService</c> needs the hash
/// to call BCrypt. The hash never appears on <see cref="AppUser"/> itself, so it cannot leak into
/// any API response DTO. This record does not leave the service layer.
/// </summary>
public sealed record UserWithHash(AppUser User, string? PasswordHash);

/// <summary>
/// Data-access seam for the <c>app_user</c> table. The only place user SQL lives.
///
/// Security boundary (Option A): this repository performs NO cryptography. BCrypt hashing and
/// verification stay in <c>AuthService</c>. The repository only reads/writes rows — including the
/// already-hashed <c>password_hash</c> value, which it treats as opaque bytes. It never hashes,
/// never verifies, and never compares passwords.
/// </summary>
public interface IUserRepository
{
    /// <summary>Active user matching username or email, plus hash for verification. Null if none.</summary>
    Task<UserWithHash?> FindActiveByLoginWithHashAsync(string usernameOrEmail, CancellationToken ct = default);

    /// <summary>Stored hash for a user id (change-password verification). Null if user not found.</summary>
    Task<string?> GetPasswordHashAsync(Guid userId, CancellationToken ct = default);

    /// <summary>Stamps <c>last_login_at = now()</c> for the given user.</summary>
    Task TouchLastLoginAsync(Guid userId, CancellationToken ct = default);

    /// <summary>Sets a new (already-hashed) password and clears the force-change flag.</summary>
    Task SetPasswordAsync(Guid userId, string newHash, CancellationToken ct = default);

    /// <summary>Sets a new (already-hashed) password and RAISES the force-change flag (admin reset). Returns rows affected.</summary>
    Task<int> SetPasswordForceChangeAsync(Guid userId, string newHash, CancellationToken ct = default);

    /// <summary>True if any user already has this username or email.</summary>
    Task<bool> ExistsByUsernameOrEmailAsync(string username, string email, CancellationToken ct = default);

    /// <summary>Inserts a new user with an already-hashed password. force_password_change = true.</summary>
    Task InsertAsync(Guid id, string email, string username, string displayName, string role,
                     string passwordHash, string[] engagementIds, CancellationToken ct = default);

    /// <summary>All users (no hash), ordered by creation.</summary>
    Task<IReadOnlyList<AppUser>> ListAsync(CancellationToken ct = default);

    /// <summary>Single user by id (no hash). Null if not found.</summary>
    Task<AppUser?> GetByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>Marks a user inactive. Returns rows affected (0 = user not found).</summary>
    Task<int> DeactivateAsync(Guid userId, CancellationToken ct = default);
}

public sealed class UserRepository(IDbConnection db) : IUserRepository
{
    // Column list shared by the identity SELECTs. password_hash is included only where a caller
    // needs it for verification; the public mappers ignore it.
    private const string UserColumns =
        "id, username, email, display_name, role, password_hash, force_password_change, is_active, engagement_ids";

    public async Task<UserWithHash?> FindActiveByLoginWithHashAsync(string usernameOrEmail, CancellationToken ct = default)
    {
        var row = await db.QueryFirstOrDefaultAsync(new CommandDefinition(
            $@"SELECT {UserColumns} FROM app_user
               WHERE (username = @u OR email = @u) AND is_active = true",
            new { u = usernameOrEmail }, cancellationToken: ct));
        if (row is null) return null;
        var d = (IDictionary<string, object?>)row;
        return new UserWithHash(MapRow(d), d["password_hash"]?.ToString());
    }

    public async Task<string?> GetPasswordHashAsync(Guid userId, CancellationToken ct = default)
    {
        var row = await db.QueryFirstOrDefaultAsync(new CommandDefinition(
            "SELECT password_hash FROM app_user WHERE id = @id",
            new { id = userId }, cancellationToken: ct));
        if (row is null) return null;
        return ((IDictionary<string, object?>)row)["password_hash"]?.ToString();
    }

    public async Task TouchLastLoginAsync(Guid userId, CancellationToken ct = default) =>
        await db.ExecuteAsync(new CommandDefinition(
            "UPDATE app_user SET last_login_at = now() WHERE id = @id",
            new { id = userId }, cancellationToken: ct));

    public async Task SetPasswordAsync(Guid userId, string newHash, CancellationToken ct = default) =>
        await db.ExecuteAsync(new CommandDefinition(
            "UPDATE app_user SET password_hash=@h, force_password_change=false WHERE id=@id",
            new { h = newHash, id = userId }, cancellationToken: ct));

    public async Task<int> SetPasswordForceChangeAsync(Guid userId, string newHash, CancellationToken ct = default) =>
        await db.ExecuteAsync(new CommandDefinition(
            "UPDATE app_user SET password_hash=@h, force_password_change=true WHERE id=@id",
            new { h = newHash, id = userId }, cancellationToken: ct));

    public async Task<bool> ExistsByUsernameOrEmailAsync(string username, string email, CancellationToken ct = default) =>
        await db.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS(SELECT 1 FROM app_user WHERE username=@u OR email=@e)",
            new { u = username, e = email }, cancellationToken: ct));

    public async Task InsertAsync(Guid id, string email, string username, string displayName, string role,
                                  string passwordHash, string[] engagementIds, CancellationToken ct = default) =>
        await db.ExecuteAsync(new CommandDefinition(
            @"INSERT INTO app_user (id, email, username, display_name, role,
                                    password_hash, force_password_change, engagement_ids)
              VALUES (@id, @email, @username, @displayName, @role,
                      @hash, true, @engIds::uuid[])",
            new { id, email, username, displayName, role, hash = passwordHash, engIds = engagementIds },
            cancellationToken: ct));

    public async Task<IReadOnlyList<AppUser>> ListAsync(CancellationToken ct = default)
    {
        var rows = await db.QueryAsync(new CommandDefinition(
            $"SELECT {UserColumns} FROM app_user ORDER BY created_at",
            cancellationToken: ct));
        return rows.Select(r => MapRow((IDictionary<string, object?>)r)).ToList();
    }

    public async Task<AppUser?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        var row = await db.QueryFirstOrDefaultAsync(new CommandDefinition(
            $"SELECT {UserColumns} FROM app_user WHERE id=@id",
            new { id }, cancellationToken: ct));
        return row is null ? null : MapRow((IDictionary<string, object?>)row);
    }

    public async Task<int> DeactivateAsync(Guid userId, CancellationToken ct = default) =>
        await db.ExecuteAsync(new CommandDefinition(
            "UPDATE app_user SET is_active=false WHERE id=@id",
            new { id = userId }, cancellationToken: ct));

    // Row → AppUser. Identical mapping to the one previously in AuthService; password_hash is
    // intentionally NOT surfaced on AppUser.
    private static AppUser MapRow(IDictionary<string, object?> d) => new(
        Id:                  (Guid)(d["id"] ?? Guid.Empty),
        Username:            d["username"]?.ToString() ?? "",
        Email:               d["email"]?.ToString()    ?? "",
        DisplayName:         d["display_name"]?.ToString() ?? "",
        Role:                d["role"]?.ToString()     ?? "viewer",
        ForcePasswordChange: d["force_password_change"] is true,
        IsActive:            d["is_active"] is not false,
        EngagementIds:       d["engagement_ids"] is Guid[] arr ? arr : Array.Empty<Guid>());
}
