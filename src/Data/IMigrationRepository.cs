using System.Data;
using Dapper;

namespace PasMigration.Data;

/// <summary>
/// Data-access seam for migration-domain reads: the <c>migration_job</c>, <c>migration_item</c>,
/// and <c>event_log</c> tables, plus the source-inventory-vs-migration join that drives the
/// migration checklist. The only place this SQL lives.
///
/// All methods return Dapper dynamic rows, preserving the exact snake_case shape the SPA consumes
/// (Logs table, migration checklist, report/status views). Input validation (e.g. clamping page
/// size) stays at the transport boundary; the repository trusts the values it is handed.
///
/// Note: the heavier Overview <c>/metrics</c> queries (4 multi-CTE aggregates) are added to this
/// same interface in a separate step (4c), isolated so that high-risk change deploys on its own.
/// </summary>
public interface IMigrationRepository
{
    /// <summary>Jobs for an engagement, newest first (migration report).</summary>
    Task<IEnumerable<dynamic>> GetJobsAsync(Guid engagementId, CancellationToken ct = default);

    /// <summary>All migration items for an engagement (migration report).</summary>
    Task<IEnumerable<dynamic>> GetReportItemsAsync(Guid engagementId, CancellationToken ct = default);

    /// <summary>
    /// Source-snapshot items joined to migration progress, for the checklist. Optional filters by
    /// item type and account scope. Migrated items sort to the bottom.
    /// </summary>
    Task<IEnumerable<dynamic>> GetSourceItemsAsync(Guid engagementId, string? type, string? scope, CancellationToken ct = default);

    /// <summary>Total event_log count for an engagement (pagination), with optional filters.</summary>
    Task<long> CountEventsAsync(
        Guid engagementId, string? search, string? eventType, bool failuresOnly, CancellationToken ct = default);

    /// <summary>
    /// A page of event_log rows, newest first, with optional filters. Caller supplies validated
    /// limit/offset. <paramref name="search"/> matches against action/outcome/message;
    /// <paramref name="eventType"/> is an exact match (e.g. "api_call", "user_action");
    /// <paramref name="failuresOnly"/> restricts to outcomes indicating failure.
    /// </summary>
    Task<IEnumerable<dynamic>> GetEventsAsync(
        Guid engagementId, string? search, string? eventType, bool failuresOnly,
        int limit, int offset, CancellationToken ct = default);

    /// <summary>The most recent running job for an engagement, or null if none is running.</summary>
    Task<dynamic?> GetRunningJobAsync(Guid engagementId, CancellationToken ct = default);

    /// <summary>Per-item-type migrated/failed/pending delta summary.</summary>
    Task<IEnumerable<dynamic>> GetStatusSummaryAsync(Guid engagementId, CancellationToken ct = default);

    /// <summary>Migration items for the status view (capped at 1000).</summary>
    Task<IEnumerable<dynamic>> GetStatusItemsAsync(Guid engagementId, CancellationToken ct = default);

    // ── Overview /metrics aggregates (added 4c; isolated because these are the heaviest queries) ──

    /// <summary>Account breakdown by bucket (multiplexed / domain / local_unix / local_windows).</summary>
    Task<IEnumerable<dynamic>> GetAccountBreakdownAsync(Guid engagementId, CancellationToken ct = default);

    /// <summary>Managed vs unmanaged split across accounts.</summary>
    Task<IEnumerable<dynamic>> GetManagedSplitAsync(Guid engagementId, CancellationToken ct = default);

    /// <summary>Per-type migration progress (total vs migrated) against the latest source snapshot.</summary>
    Task<IEnumerable<dynamic>> GetProgressByTypeAsync(Guid engagementId, CancellationToken ct = default);

    /// <summary>Source-vs-target item counts per type (latest snapshot each side, full outer join).</summary>
    Task<IEnumerable<dynamic>> GetSourceVsTargetAsync(Guid engagementId, CancellationToken ct = default);
}

public sealed class MigrationRepository(IDbConnection db) : IMigrationRepository
{
    public async Task<IEnumerable<dynamic>> GetJobsAsync(Guid engagementId, CancellationToken ct = default) =>
        await db.QueryAsync(new CommandDefinition(
            @"SELECT id, job_type, mode, status, started_at, finished_at, total, succeeded, failed, skipped
              FROM migration_job WHERE engagement_id=@id ORDER BY started_at DESC",
            new { id = engagementId }, cancellationToken: ct));

    public async Task<IEnumerable<dynamic>> GetReportItemsAsync(Guid engagementId, CancellationToken ct = default) =>
        await db.QueryAsync(new CommandDefinition(
            @"SELECT item_type, source_name, source_folder_path, target_native_id, status, last_error
              FROM migration_item WHERE engagement_id=@id ORDER BY item_type, source_name",
            new { id = engagementId }, cancellationToken: ct));

    public async Task<IEnumerable<dynamic>> GetSourceItemsAsync(
        Guid engagementId, string? type, string? scope, CancellationToken ct = default)
    {
        var sql = @"SELECT ii.item_type, ii.source_native_id, ii.name, ii.folder_path, ii.is_managed,
                           COALESCE(mi.status, 'pending') AS migration_status,
                           mi.target_native_id IS NOT NULL AS is_migrated
                    FROM inventory_item ii
                    JOIN inventory_snapshot s ON s.id = ii.snapshot_id
                    JOIN tenant_connection tc ON tc.id = s.tenant_connection_id
                    LEFT JOIN migration_item mi
                           ON mi.engagement_id = @id
                          AND mi.item_type = ii.item_type
                          AND mi.source_native_id = ii.source_native_id
                    WHERE s.engagement_id=@id AND tc.role='source'
                      AND s.captured_at = (
                        SELECT MAX(s2.captured_at) FROM inventory_snapshot s2
                        JOIN tenant_connection tc2 ON tc2.id=s2.tenant_connection_id
                        WHERE s2.engagement_id=@id AND tc2.role='source')"
                  + (type is null ? "" : " AND ii.item_type=@type")
                  + (scope is null ? "" : " AND ii.attributes->>'AccountScope' = @scope")
                  + @" ORDER BY
                        CASE WHEN COALESCE(mi.status,'pending') IN ('succeeded','migrated')
                             THEN 1 ELSE 0 END,
                        ii.item_type, ii.name";
        return await db.QueryAsync(new CommandDefinition(sql, new { id = engagementId, type, scope }, cancellationToken: ct));
    }

    public async Task<long> CountEventsAsync(
        Guid engagementId, string? search, string? eventType, bool failuresOnly, CancellationToken ct = default)
    {
        var (filterSql, ps) = BuildEventFilter(engagementId, search, eventType, failuresOnly);
        return await db.ExecuteScalarAsync<long>(new CommandDefinition(
            $"SELECT COUNT(*) FROM event_log WHERE engagement_id=@id{filterSql}", ps, cancellationToken: ct));
    }

    public async Task<IEnumerable<dynamic>> GetEventsAsync(
        Guid engagementId, string? search, string? eventType, bool failuresOnly,
        int limit, int offset, CancellationToken ct = default)
    {
        var (filterSql, ps) = BuildEventFilter(engagementId, search, eventType, failuresOnly);
        ps.Add("lim", limit);
        ps.Add("off", offset);
        return await db.QueryAsync(new CommandDefinition(
            $@"SELECT occurred_at, event_type, action, outcome, message, tenant_role
               FROM event_log WHERE engagement_id=@id{filterSql}
               ORDER BY occurred_at DESC LIMIT @lim OFFSET @off", ps, cancellationToken: ct));
    }

    /// <summary>
    /// Shared optional-filter SQL fragment + params for event_log queries: free-text search
    /// across action/outcome/message, an exact event_type match, and a "failures only" heuristic
    /// (outcome contains fail/excluded/error). Conditional string concatenation — not a runtime
    /// <c>@param IS NULL</c> check — matching the pattern already used for optional filters in
    /// <see cref="GetSourceItemsAsync"/>. Parameters are only added when their filter is active,
    /// which sidesteps any null-parameter type-inference ambiguity.
    /// </summary>
    private static (string Sql, DynamicParameters Params) BuildEventFilter(
        Guid engagementId, string? search, string? eventType, bool failuresOnly)
    {
        var ps = new DynamicParameters();
        ps.Add("id", engagementId);
        var sql = "";
        if (!string.IsNullOrWhiteSpace(search))
        {
            sql += " AND (action ILIKE @search OR outcome ILIKE @search OR message ILIKE @search)";
            ps.Add("search", $"%{search.Trim()}%");
        }
        if (!string.IsNullOrWhiteSpace(eventType))
        {
            sql += " AND event_type=@eventType";
            ps.Add("eventType", eventType);
        }
        if (failuresOnly)
            sql += " AND (outcome ILIKE '%fail%' OR outcome ILIKE '%excluded%' OR outcome ILIKE '%error%')";
        return (sql, ps);
    }

    public async Task<dynamic?> GetRunningJobAsync(Guid engagementId, CancellationToken ct = default) =>
        await db.QueryFirstOrDefaultAsync(new CommandDefinition(
            @"SELECT id, job_type, mode, started_at FROM migration_job
              WHERE engagement_id=@id AND status='running' ORDER BY started_at DESC LIMIT 1",
            new { id = engagementId }, cancellationToken: ct));

    public async Task<IEnumerable<dynamic>> GetStatusSummaryAsync(Guid engagementId, CancellationToken ct = default) =>
        await db.QueryAsync(new CommandDefinition(
            @"SELECT item_type,
                     COUNT(*) AS total,
                     COUNT(*) FILTER (WHERE status='migrated' OR target_native_id IS NOT NULL) AS migrated,
                     COUNT(*) FILTER (WHERE status='failed') AS failed,
                     COUNT(*) FILTER (WHERE status NOT IN ('migrated','failed') AND target_native_id IS NULL) AS pending
              FROM migration_item WHERE engagement_id=@id
              GROUP BY item_type ORDER BY item_type",
            new { id = engagementId }, cancellationToken: ct));

    public async Task<IEnumerable<dynamic>> GetStatusItemsAsync(Guid engagementId, CancellationToken ct = default) =>
        await db.QueryAsync(new CommandDefinition(
            @"SELECT item_type, name, source_native_id, target_native_id, status
              FROM migration_item WHERE engagement_id=@id
              ORDER BY item_type, name LIMIT 1000",
            new { id = engagementId }, cancellationToken: ct));

    // ── Overview /metrics aggregates (4c) ──────────────────────────────────────────────
    // The first three aggregates all run over the latest source snapshot; this shared subquery
    // is interpolated into each (identical to the pre-DAL inline handler).
    private const string LatestSource = @"
        SELECT ii.* FROM inventory_item ii
        JOIN inventory_snapshot s ON s.id=ii.snapshot_id
        JOIN tenant_connection tc ON tc.id=s.tenant_connection_id
        WHERE s.engagement_id=@id AND tc.role='source'
          AND s.captured_at=(SELECT MAX(s2.captured_at) FROM inventory_snapshot s2
            JOIN tenant_connection tc2 ON tc2.id=s2.tenant_connection_id
            WHERE s2.engagement_id=@id AND tc2.role='source')";

    public async Task<IEnumerable<dynamic>> GetAccountBreakdownAsync(Guid engagementId, CancellationToken ct = default) =>
        await db.QueryAsync(new CommandDefinition(
            $@"SELECT
                 CASE
                   WHEN item_type='multiplexed_account' THEN 'multiplexed'
                   WHEN attributes->>'AccountScope'='domain' THEN 'domain'
                   WHEN attributes->>'ComputerClass' ILIKE '%ssh%'
                     OR attributes->>'ComputerClass' ILIKE '%unix%'
                     OR attributes->>'ComputerClass' ILIKE '%linux%' THEN 'local_unix'
                   ELSE 'local_windows'
                 END AS bucket,
                 COUNT(*) AS n
               FROM ({LatestSource}) q
               WHERE item_type IN ('account','multiplexed_account')
               GROUP BY bucket",
            new { id = engagementId }, cancellationToken: ct));

    public async Task<IEnumerable<dynamic>> GetManagedSplitAsync(Guid engagementId, CancellationToken ct = default) =>
        await db.QueryAsync(new CommandDefinition(
            $@"SELECT COALESCE(is_managed,false) AS is_managed, COUNT(*) AS n
               FROM ({LatestSource}) q WHERE item_type='account'
               GROUP BY COALESCE(is_managed,false)",
            new { id = engagementId }, cancellationToken: ct));

    public async Task<IEnumerable<dynamic>> GetProgressByTypeAsync(Guid engagementId, CancellationToken ct = default) =>
        await db.QueryAsync(new CommandDefinition(
            $@"SELECT q.item_type AS type, COUNT(*) AS total,
                      COUNT(mi.target_native_id) AS migrated
               FROM ({LatestSource}) q
               LEFT JOIN migration_item mi
                 ON mi.engagement_id=@id AND mi.item_type=q.item_type
                AND mi.source_native_id=q.source_native_id
               GROUP BY q.item_type ORDER BY q.item_type",
            new { id = engagementId }, cancellationToken: ct));

    public async Task<IEnumerable<dynamic>> GetSourceVsTargetAsync(Guid engagementId, CancellationToken ct = default) =>
        await db.QueryAsync(new CommandDefinition(
            @"WITH latest_src AS (
                SELECT ii.item_type, COUNT(*) n FROM inventory_item ii
                JOIN inventory_snapshot s ON s.id=ii.snapshot_id
                JOIN tenant_connection tc ON tc.id=s.tenant_connection_id
                WHERE s.engagement_id=@id AND tc.role='source'
                  AND s.captured_at=(SELECT MAX(s2.captured_at) FROM inventory_snapshot s2
                    JOIN tenant_connection tc2 ON tc2.id=s2.tenant_connection_id
                    WHERE s2.engagement_id=@id AND tc2.role='source')
                GROUP BY ii.item_type),
              latest_tgt AS (
                SELECT ii.item_type, COUNT(*) n FROM inventory_item ii
                JOIN inventory_snapshot s ON s.id=ii.snapshot_id
                JOIN tenant_connection tc ON tc.id=s.tenant_connection_id
                WHERE s.engagement_id=@id AND tc.role='target'
                  AND s.captured_at=(SELECT MAX(s2.captured_at) FROM inventory_snapshot s2
                    JOIN tenant_connection tc2 ON tc2.id=s2.tenant_connection_id
                    WHERE s2.engagement_id=@id AND tc2.role='target')
                GROUP BY ii.item_type)
              SELECT COALESCE(s.item_type,t.item_type) AS type,
                     COALESCE(s.n,0) AS source, COALESCE(t.n,0) AS target
              FROM latest_src s FULL OUTER JOIN latest_tgt t ON s.item_type=t.item_type
              ORDER BY type",
            new { id = engagementId }, cancellationToken: ct));
}
