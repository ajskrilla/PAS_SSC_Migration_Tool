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
        // Which tenant connections exist and what auth mode are they using?
        var conns = (await db.QueryAsync(
            @"SELECT role, system_type, auth_mode
              FROM tenant_connection
              WHERE engagement_id = @id",
            new { id = engagementId })).Cast<IDictionary<string, object?>>().ToList();

        bool sourceExists = conns.Any(c => c["role"]?.ToString() == "source");
        bool targetExists = conns.Any(c => c["role"]?.ToString() == "target");

        // Platform-enabled = source using platform_client_credentials auth
        bool platformEnabled = conns.Any(c =>
            c["role"]?.ToString() == "source" &&
            c["auth_mode"]?.ToString() == "platform_client_credentials");

        // Has a source inventory been captured (proves the connection actually worked)?
        bool inventoryCaptured = await db.ExecuteScalarAsync<bool>(
            @"SELECT EXISTS(
                SELECT 1 FROM inventory_snapshot s
                JOIN tenant_connection tc ON tc.id = s.tenant_connection_id
                WHERE s.engagement_id = @id AND tc.role = 'source')",
            new { id = engagementId });

        bool targetInventoryCaptured = await db.ExecuteScalarAsync<bool>(
            @"SELECT EXISTS(
                SELECT 1 FROM inventory_snapshot s
                JOIN tenant_connection tc ON tc.id = s.tenant_connection_id
                WHERE s.engagement_id = @id AND tc.role = 'target')",
            new { id = engagementId });

        return new
        {
            source_connection_configured = sourceExists ? "ok" : "missing — add source connection on Pre-migration page",
            target_connection_configured = targetExists ? "ok" : "missing — add target connection on Pre-migration page",
            source_inventory_captured    = inventoryCaptured ? "ok" : "not yet — run inventory on Pre-migration page",
            target_inventory_captured    = targetInventoryCaptured ? "ok" : "not yet — run inventory on Pre-migration page",
            platform_unified             = platformEnabled
                                             ? "ok — source is using Platform (OAuth2) auth, tenant appears unified"
                                             : "unknown — source is not using Platform auth; verify tenant is Unified/Platform-enabled",
            oauth2_app_in_pas            = inventoryCaptured
                                             ? "implied ok — inventory capture succeeded, which requires a working OAuth2 app"
                                             : "unverified — run inventory first to confirm OAuth2 app is configured",
            uva_mode                     = "unknown — verify manually: Secret Server Admin > Configuration > Unlimited Vault Access",
            pas_admin_role               = "unknown — verify manually: PAS service account must be in System Administrator role",
            ss_admin_role                = "unknown — verify manually: SS service account must be in Secret Server Administrator role",
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
            return new { error = "No source inventory captured yet. Run inventory first from the Pre-migration page." };

        var d = (IDictionary<string, object?>)snapshot;
        var json = d["summary_json"] as string;
        Dictionary<string, int>? summary = null;
        if (!string.IsNullOrEmpty(json))
            summary = JsonSerializer.Deserialize<Dictionary<string, int>>(json);

        int managed  = summary?.GetValueOrDefault("managed", 0)       ?? 0;
        int accounts = summary?.GetValueOrDefault("accounts", 0)      ?? 0;
        int text     = summary?.GetValueOrDefault("text_secrets", 0)  ?? 0;
        int files    = summary?.GetValueOrDefault("file_secrets", 0)  ?? 0;
        int total    = summary?.GetValueOrDefault("total", 0)         ?? 0;

        string recommendation;
        if (managed == 0 && accounts == 0)
            recommendation = "No accounts found. This is a secrets-only vault. Migrate text secrets first, then file secrets. Single migration day is appropriate.";
        else if (managed < 20_000)
            recommendation = $"{managed:N0} managed accounts — under the 20,000 threshold. Schedule a single migration day once prerequisites are green. Recommended order: text secrets → file secrets → accounts.";
        else
            recommendation = $"{managed:N0} managed accounts — OVER the 20,000 threshold. A dedicated account migration path is required. Coordinate with all stakeholders, agree on a cutoff date for active password rotation, and plan the unmanage/re-manage sequence carefully before starting. Migrate text and file secrets first, then execute the account plan.";

        return new
        {
            captured_at = d["captured_at"],
            vault_size = new { total, accounts, managed_accounts = managed, text_secrets = text, file_secrets = files },
            migration_recommendation = recommendation
        };
    }

    // ── Tool 4: reconciliation_status ────────────────────────────────────────────────

    public async Task<object> ReconciliationStatusAsync(Guid engagementId)
    {
        bool hasData = await db.ExecuteScalarAsync<bool>(
            "SELECT EXISTS(SELECT 1 FROM reconciliation_result WHERE engagement_id=@id)",
            new { id = engagementId });

        if (!hasData)
            return new { error = "No reconciliation data yet. Run a reconcile pass from the Pre-migration page first." };

        var rows = await db.QueryAsync(
            @"SELECT item_type, match_status, COUNT(*) AS n
              FROM reconciliation_result WHERE engagement_id = @id
              GROUP BY item_type, match_status ORDER BY item_type, match_status",
            new { id = engagementId });

        return new { reconciliation = rows };
    }

    // ── Tool 5: recent_activity ──────────────────────────────────────────────────────

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
