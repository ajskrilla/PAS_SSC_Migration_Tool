using System.Data;
using Dapper;

namespace PasMigration.Data;

/// <summary>
/// Data-access seam for inventory-domain reads: the <c>inventory_snapshot</c>,
/// <c>inventory_item</c>, and <c>reconciliation_result</c> tables. The only place this SQL lives.
///
/// All methods return Dapper dynamic rows, preserving the exact snake_case column names the SPA
/// consumes (Dashboard cards, drill-down tables). Response-shaping that used to sit in the route
/// handlers (e.g. parsing the snapshot summary JSONB into an object) stays in the transport layer —
/// this repository returns the raw rows only, including <c>summary_json</c> as text.
/// </summary>
public interface IInventoryRepository
{
    /// <summary>Latest snapshot per role for an engagement. Includes <c>summary_json</c> (JSONB as text).</summary>
    Task<IEnumerable<dynamic>> GetLatestSnapshotSummariesAsync(Guid engagementId, CancellationToken ct = default);

    /// <summary>Items in a snapshot, optionally filtered by item type.</summary>
    Task<IEnumerable<dynamic>> GetSnapshotItemsAsync(Guid snapshotId, string? type, CancellationToken ct = default);

    /// <summary>Reconciliation diff rows for an engagement.</summary>
    Task<IEnumerable<dynamic>> GetReconciliationAsync(Guid engagementId, CancellationToken ct = default);
}

public sealed class InventoryRepository(IDbConnection db) : IInventoryRepository
{
    public async Task<IEnumerable<dynamic>> GetLatestSnapshotSummariesAsync(Guid engagementId, CancellationToken ct = default) =>
        await db.QueryAsync(new CommandDefinition(
            @"SELECT DISTINCT ON (tc.role) tc.role, s.id AS snapshot_id, s.captured_at,
                     s.summary::text AS summary_json
              FROM inventory_snapshot s
              JOIN tenant_connection tc ON tc.id = s.tenant_connection_id
              WHERE s.engagement_id = @id
              ORDER BY tc.role, s.captured_at DESC",
            new { id = engagementId }, cancellationToken: ct));

    public async Task<IEnumerable<dynamic>> GetSnapshotItemsAsync(Guid snapshotId, string? type, CancellationToken ct = default)
    {
        var sql = @"SELECT item_type, source_native_id, name, folder_path, is_managed, size_bytes
                    FROM inventory_item WHERE snapshot_id = @snapshotId"
                  + (type is null ? "" : " AND item_type = @type")
                  + " ORDER BY item_type, name";
        return await db.QueryAsync(new CommandDefinition(sql, new { snapshotId, type }, cancellationToken: ct));
    }

    public async Task<IEnumerable<dynamic>> GetReconciliationAsync(Guid engagementId, CancellationToken ct = default) =>
        await db.QueryAsync(new CommandDefinition(
            @"SELECT item_type, match_key, match_status FROM reconciliation_result
              WHERE engagement_id = @id ORDER BY match_status, item_type",
            new { id = engagementId }, cancellationToken: ct));
}
