using System.Data;
using System.Text.Json;
using Dapper;

namespace PasMigration.Ai;

/// <summary>
/// Read-only data tools the assistant can call. Every method is a SELECT-equivalent —
/// no writes, no credential access. The five tools map directly to existing DB queries
/// already used by the dashboard and migration pages.
/// </summary>
public sealed class AssistantTools(IDbConnection db)
{
    // ── Tool 1: check_prerequisites ──────────────────────────────────────────────────
    // v1: reports what is determinable from existing connection/inventory data.
    // UVA mode and admin-role membership return "unknown" — verify on the Readiness page.

    public async Task<object> CheckPrerequisitesAsync(Guid engagementId)
    {
        // Has a source connection been saved and tested?
        var conns = await db.QueryAsync(
            @"SELECT tc.role, tc.system_type, cs.status, cs.tested_at
              FROM tenant_connection tc
              LEFT JOIN connection_status cs ON cs.tenant_connection_id = tc.id
              WHERE tc.engagement_id = @id",
            new { id = engagementId });

        var connList = conns.Cast<IDictionary<string, object?>>().ToList();

        bool sourceConnected = connList.Any(c =>
            c["role"]?.ToString() == "source" && c["status"]?.ToString() == "ok");
        bool targetConnected = connList.Any(c =>
            c["role"]?.ToString() == "target" && c["status"]?.ToString() == "ok");

        // Has a source inventory been captured?
        var inventoryCaptured = await db.ExecuteScalarAsync<bool>(
            @"SELECT EXISTS(
                SELECT 1 FROM inventory_snapshot s
                JOIN tenant_connection tc ON tc.id = s.tenant_connection_id
                WHERE s.engagement_id = @id AND tc.role = 'source')",
            new { id = engagementId });

        // Was the source connection auth mode Platform (proves Unified tenant)?
        var platformEnabled = await db.ExecuteScalarAsync<bool>(
            @"SELECT EXISTS(
                SELECT 1 FROM tenant_connection
                WHERE engagement_id = @id AND role = 'source'
                  AND auth_mode = 'platform_client_credentials')",
            new { id = engagementId });

        return new
        {
            source_connection = sourceConnected ? "ok" : "not_tested",
            target_connection = targetConnected ? "ok" : "not_tested",
            inventory_captured = inventoryCaptured,
            platform_unified = platformEnabled ? "ok" : "unknown — verify on Readiness page",
            uva_mode = "unknown — verify manually in Secret Server Admin > Configuration",
            pas_admin_role = "unknown — verify manually: PAS service account must be System Administrator",
            ss_admin_role = "unknown — verify manually: SS service account must be Secret Server Administrator",
            oauth2_app = sourceConnected ? "implied_ok — source connection tested successfully" : "unverified",
        };
    }

    // ── Tool 2: migration_stats ──────────────────────────────────────────────────────
    // Per-type migrated/pending/failed counts + migration job dates.

    public async Task<object> MigrationStatsAsync(Guid engagementId)
    {
        var summary = await db.QueryAsync(
            @"SELECT item_type,
                     COUNT(*) AS total,
                     COUNT(*) FILTER (WHERE status='migrated' OR target_native_id IS NOT NULL) AS migrated,
                     COUNT(*) FILTER (WHERE status='failed') AS failed,
                     COUNT(*) FILTER (WHERE status NOT IN ('migrated','failed') AND target_native_id IS NULL) AS pending
              FROM migration_item WHERE engagement_id=@id
              GROUP BY item_type ORDER BY item_type",
            new { id = engagementId });

        var jobs = await db.QueryAsync(
            @"SELECT job_type, mode, status, started_at, finished_at, total, succeeded, failed
              FROM migration_job WHERE engagement_id=@id ORDER BY started_at DESC LIMIT 10",
            new { id = engagementId });

        // Overall percentage
        var totals = await db.QueryFirstOrDefaultAsync(
            @"SELECT COUNT(*) AS total,
                     COUNT(*) FILTER (WHERE status='migrated' OR target_native_id IS NOT NULL) AS migrated
              FROM migration_item WHERE engagement_id=@id",
            new { id = engagementId });

        double pct = 0;
        if (totals != null)
        {
            var d = (IDictionary<string, object?>)totals;
            long tot = Convert.ToInt64(d["total"] ?? 0);
            long mig = Convert.ToInt64(d["migrated"] ?? 0);
            pct = tot > 0 ? Math.Round((double)mig / tot * 100, 1) : 0;
        }

        return new
        {
            overall_percent_migrated = pct,
            by_type = summary,
            recent_jobs = jobs
        };
    }

    // ── Tool 3: environment_summary ──────────────────────────────────────────────────
    // Counts from the latest source snapshot — the "how big is this vault" answer.

    public async Task<object> EnvironmentSummaryAsync(Guid engagementId)
    {
        var snapshot = await db.QueryFirstOrDefaultAsync(
            @"SELECT s.id, s.captured_at, s.summary::text AS summary_json
              FROM inventory_snapshot s
              JOIN tenant_connection tc ON tc.id = s.tenant_connection_id
              WHERE s.engagement_id = @id AND tc.role = 'source'
              ORDER BY s.captured_at DESC LIMIT 1",
            new { id = engagementId });

        if (snapshot == null)
            return new { error = "No source inventory captured yet. Run inventory first." };

        var d = (IDictionary<string, object?>)snapshot;
        var json = d["summary_json"] as string;
        Dictionary<string, int>? summary = null;
        if (!string.IsNullOrEmpty(json))
            summary = JsonSerializer.Deserialize<Dictionary<string, int>>(json);

        int managed = summary?.GetValueOrDefault("managed", 0) ?? 0;

        string recommendation;
        if (managed == 0)
            recommendation = "No managed accounts detected. Standard single-day migration: run in order text → file → accounts.";
        else if (managed < 20_000)
            recommendation = $"{managed:N0} managed accounts — under the 20 000 threshold. Schedule a single migration day once prerequisites are green. Migrate in order: text secrets → file secrets → accounts.";
        else
            recommendation = $"{managed:N0} managed accounts — OVER the 20 000 threshold. A dedicated account migration path is required. Ensure all stakeholders are informed, agree on a cutoff date for active rotation, and plan the unmanage/re-manage sequence carefully. Migrate text and file secrets first, then execute the account plan.";

        return new
        {
            captured_at = d["captured_at"],
            counts = summary,
            managed_account_recommendation = recommendation
        };
    }

    // ── Tool 4: reconciliation_status ────────────────────────────────────────────────
    // Source-vs-target diff — what's matched, source-only, target-only, conflicted.

    public async Task<object> ReconciliationStatusAsync(Guid engagementId)
    {
        var rows = await db.QueryAsync(
            @"SELECT item_type, match_status, COUNT(*) AS n
              FROM reconciliation_result WHERE engagement_id = @id
              GROUP BY item_type, match_status ORDER BY item_type, match_status",
            new { id = engagementId });

        var hasData = await db.ExecuteScalarAsync<bool>(
            "SELECT EXISTS(SELECT 1 FROM reconciliation_result WHERE engagement_id=@id)",
            new { id = engagementId });

        if (!hasData)
            return new { error = "No reconciliation data yet. Run a reconcile pass from the Pre-migration page first." };

        return new { reconciliation = rows };
    }

    // ── Tool 5: recent_activity ──────────────────────────────────────────────────────
    // Last N event log entries — what has been happening in this engagement.

    public async Task<object> RecentActivityAsync(Guid engagementId, int limit = 20)
    {
        var lim = Math.Clamp(limit, 1, 50);
        var rows = await db.QueryAsync(
            @"SELECT occurred_at, event_type, action, outcome, message, tenant_role
              FROM event_log WHERE engagement_id=@id
              ORDER BY occurred_at DESC LIMIT @lim",
            new { id = engagementId, lim });

        return new { recent_events = rows };
    }
}
