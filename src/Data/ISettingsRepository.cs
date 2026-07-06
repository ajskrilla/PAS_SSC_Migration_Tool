using System.Data;
using Dapper;

namespace PasMigration.Data;

/// <summary>
/// Data-access seam for the <c>app_setting</c> key-value table. Currently backs a single
/// setting (JWT/session timeout) but shaped as a generic key-value store so future settings
/// don't need another migration.
/// </summary>
public interface ISettingsRepository
{
    /// <summary>Raw string value for a key, or null if it doesn't exist.</summary>
    Task<string?> GetAsync(string key, CancellationToken ct = default);

    /// <summary>Upserts a key's value.</summary>
    Task SetAsync(string key, string value, CancellationToken ct = default);
}

public sealed class SettingsRepository(IDbConnection db) : ISettingsRepository
{
    public async Task<string?> GetAsync(string key, CancellationToken ct = default) =>
        await db.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT value FROM app_setting WHERE key=@key",
            new { key }, cancellationToken: ct));

    public async Task SetAsync(string key, string value, CancellationToken ct = default) =>
        await db.ExecuteAsync(new CommandDefinition(
            @"INSERT INTO app_setting (key, value, updated_at)
              VALUES (@key, @value, now())
              ON CONFLICT (key) DO UPDATE SET value=@value, updated_at=now()",
            new { key, value }, cancellationToken: ct));
}
