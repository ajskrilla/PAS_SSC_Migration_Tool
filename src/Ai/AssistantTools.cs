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
                     COUNT(*) FILTER (WHERE status IN ('migrated','succeeded') OR target_native_id IS NOT NULL) AS migrated,
                     COUNT(*) FILTER (WHERE status='failed') AS failed,
                     COUNT(*) FILTER (WHERE status NOT IN ('migrated','succeeded','failed') AND target_native_id IS NULL) AS pending
              FROM migration_item WHERE engagement_id=@id
              GROUP BY item_type ORDER BY item_type",
            new { id = engagementId });

        var jobs = await db.QueryAsync(
            @"SELECT job_type, mode, status, started_at, finished_at, total, succeeded, failed
              FROM migration_job WHERE engagement_id=@id ORDER BY started_at DESC LIMIT 10",
            new { id = engagementId });

        var totals = await db.QueryFirstOrDefaultAsync(
            @"SELECT COUNT(*) AS total,
                     COUNT(*) FILTER (WHERE status IN ('migrated','succeeded') OR target_native_id IS NOT NULL) AS migrated
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

public sealed class AssistantTools2(IDbConnection db)
{
    // ── Tool 6: explain_failures ─────────────────────────────────────────────────────
    // Pulls failed migration items + their error messages, groups by error pattern,
    // and maps known patterns to explanations + fix steps. LLM narrates unknowns.

    private static readonly (string Pattern, string Title, string Explanation, string Fix)[] KnownErrors =
    [
        ("405",
         "HTTP 405 Method Not Allowed",
         "The API endpoint was called with the wrong HTTP verb (GET/POST/PUT).",
         "This is usually a Secret Server API version mismatch. Check which HTTP verb the endpoint requires for your tenant version. Common case: file upload needs PUT not POST."),

        ("401",
         "HTTP 401 Unauthorized",
         "The API credentials were rejected or the token expired during a long migration run.",
         "Re-test the connection on the Pre-migration page to refresh the session token, then re-run the migration."),

        ("403",
         "HTTP 403 Forbidden",
         "The API service account does not have sufficient permissions for this action.",
         "Verify the PAS account is in System Administrator role and the Secret Server account is in Secret Server Administrator role."),

        ("404",
         "HTTP 404 Not Found",
         "A resource (folder, template, or secret) referenced during migration does not exist on the target.",
         "Run a fresh inventory on the target first. If a template is missing, create it via the Pre-migration page before migrating."),

        ("template",
         "Template mismatch",
         "The secret template selected for migration does not match the field structure of the source secret.",
         "Verify the text template and file template selections on the Migration page. Use 'Create file template' if the file template is missing."),

        ("folder",
         "Folder creation failed",
         "The staging folder or a parent folder could not be created on Secret Server.",
         "Check that the SS service account has permission to create folders at the root level. Verify Unlimited Vault Access is enabled."),

        ("inheritpermission",
         "Folder permissions error",
         "Folder inherit-permissions flag caused a conflict during nested folder creation.",
         "This is a known issue with root-level folder creation. Ensure the staging folder is not set to inherit permissions from a non-existent parent."),

        ("timeout",
         "Request timeout",
         "A tenant API call timed out, usually on large file uploads or slow networks.",
         "Re-run the migration — failed items are tracked and will be retried. For persistent timeouts on large files, check network connectivity to the tenant."),

        ("duplicate",
         "Duplicate secret name",
         "A secret with the same name already exists in the target folder.",
         "Run a reconcile on the Pre-migration page to identify duplicates. You may need to revert partial migrations before re-running."),

        ("password",
         "Password field empty",
         "The password/credential field was not populated on the migrated secret.",
         "This can indicate a slug mismatch on the target template. Verify the template field slugs match what the migration expects (usually 'password' for text secrets)."),
    ];

    public async Task<object> ExplainFailuresAsync(Guid engagementId)
    {
        // Get failed migration items with their errors
        var failed = (await db.QueryAsync(
            @"SELECT mi.item_type, mi.source_name, mi.last_error,
                     mj.job_type, mj.started_at
              FROM migration_item mi
              JOIN migration_job mj ON mj.id = mi.job_id
              WHERE mi.engagement_id = @id
                AND (mi.status = 'failed' OR mi.last_error IS NOT NULL)
              ORDER BY mj.started_at DESC, mi.item_type
              LIMIT 50",
            new { id = engagementId }))
            .Cast<IDictionary<string, object?>>().ToList();

        if (failed.Count == 0)
            return new { message = "No failed items found. The last migration run completed without errors.", failures = Array.Empty<object>() };

        // Also get recent error events from the event log
        var errorEvents = (await db.QueryAsync(
            @"SELECT action, message, tenant_role, occurred_at
              FROM event_log
              WHERE engagement_id = @id AND outcome = 'error'
              ORDER BY occurred_at DESC LIMIT 20",
            new { id = engagementId }))
            .Cast<IDictionary<string, object?>>().ToList();

        // Group failures by error pattern and match to known errors
        var grouped = failed
            .GroupBy(f =>
            {
                var err = f["last_error"]?.ToString()?.ToLowerInvariant() ?? "";
                var match = KnownErrors.FirstOrDefault(k => err.Contains(k.Pattern));
                return match.Pattern ?? "unknown";
            })
            .Select(g =>
            {
                var pattern = g.Key;
                var known = KnownErrors.FirstOrDefault(k => k.Pattern == pattern);
                return new
                {
                    error_pattern = pattern,
                    count = g.Count(),
                    title = known.Title ?? "Unknown error",
                    explanation = known.Explanation ?? "Error pattern not recognized — see raw errors below.",
                    fix = known.Fix ?? "Review the raw error messages below and check the event log for more detail.",
                    affected_items = g.Take(5).Select(f => new
                    {
                        name      = f["source_name"]?.ToString() ?? "(unknown)",
                        type      = f["item_type"]?.ToString(),
                        raw_error = f["last_error"]?.ToString(),
                    }).ToList(),
                    has_more = g.Count() > 5,
                };
            })
            .OrderByDescending(g => g.count)
            .ToList();

        var summary = new
        {
            total_failed = failed.Count,
            error_groups = grouped,
            recent_error_events = errorEvents.Take(5).Select(e => new
            {
                action  = e["action"]?.ToString(),
                message = e["message"]?.ToString(),
                role    = e["tenant_role"]?.ToString(),
                at      = e["occurred_at"]?.ToString(),
            }).ToList(),
        };

        return summary;
    }

    // ── Tool 7: risk_scan ────────────────────────────────────────────────────────────
    // Pre-migration scan: flags secrets likely to cause problems before the run starts.

    public async Task<object> RiskScanAsync(Guid engagementId)
    {
        var snapshotId = await db.ExecuteScalarAsync<Guid?>(
            @"SELECT s.id FROM inventory_snapshot s
              JOIN tenant_connection tc ON tc.id = s.tenant_connection_id
              WHERE s.engagement_id = @id AND tc.role = 'source'
              ORDER BY s.captured_at DESC LIMIT 1",
            new { id = engagementId });

        if (snapshotId is null)
            return new { error = "No source inventory captured yet. Run inventory first." };

        var risks = new List<object>();

        // 1. Large files (>50MB) — slow uploads, timeout risk
        var largeFiles = await db.QueryAsync(
            @"SELECT name, folder_path, size_bytes
              FROM inventory_item
              WHERE snapshot_id = @sid AND item_type = 'file_secret'
                AND size_bytes > 52428800
              ORDER BY size_bytes DESC LIMIT 10",
            new { sid = snapshotId });

        var largeList = largeFiles.Cast<IDictionary<string, object?>>().ToList();
        if (largeList.Count > 0)
            risks.Add(new
            {
                risk = "large_files",
                severity = "medium",
                title = "Large file secrets (>50MB)",
                description = largeList.Count + " file secret(s) exceed 50MB. These may time out during upload on slow connections.",
                advice = "These will still migrate — just be aware they take longer. If they fail, re-run; the migration is idempotent.",
                items = largeList.Select(f => new
                {
                    name = f["name"]?.ToString(),
                    size_mb = Math.Round(Convert.ToDouble(f["size_bytes"] ?? 0) / 1048576, 1),
                }).ToList(),
            });

        // 2. Secrets with no folder (root-level) — may cause folder-structure issues
        var noFolder = await db.ExecuteScalarAsync<int>(
            @"SELECT COUNT(*) FROM inventory_item
              WHERE snapshot_id = @sid
                AND (folder_path IS NULL OR folder_path = '')
                AND item_type != 'folder'",
            new { sid = snapshotId });

        if (noFolder > 0)
            risks.Add(new
            {
                risk = "no_folder",
                severity = "low",
                title = "Root-level secrets (" + noFolder + ")",
                description = noFolder + " secret(s) have no folder path and will land at the root of the staging folder.",
                advice = "These migrate fine but won't have folder nesting on the target. Acceptable for most customers.",
            });

        // 3. Duplicate names within same folder — can cause conflicts
        var dupes = await db.ExecuteScalarAsync<int>(
            @"SELECT COUNT(*) FROM (
                SELECT name, folder_path, COUNT(*) c
                FROM inventory_item
                WHERE snapshot_id = @sid AND item_type != 'folder'
                GROUP BY name, folder_path HAVING COUNT(*) > 1
              ) dupe",
            new { sid = snapshotId });

        if (dupes > 0)
            risks.Add(new
            {
                risk = "duplicate_names",
                severity = "high",
                title = "Duplicate secret names (" + dupes + " groups)",
                description = dupes + " group(s) of secrets share the same name within the same folder.",
                advice = "Secret Server enforces unique names per folder. Duplicates will fail on the second import. Resolve naming conflicts before migrating.",
            });

        // 4. Very large total item counts — set expectations
        var total = await db.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM inventory_item WHERE snapshot_id = @sid AND item_type != 'folder'",
            new { sid = snapshotId });

        var managed = await db.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM inventory_item WHERE snapshot_id = @sid AND item_type = 'account' AND is_managed = true",
            new { sid = snapshotId });

        // 5. Managed accounts — always flag with context
        if (managed > 0)
            risks.Add(new
            {
                risk = "managed_accounts",
                severity = managed > 20_000 ? "high" : "medium",
                title = "Managed accounts (" + managed + ")",
                description = "Managed accounts must be unmanaged in PAS before migration, then re-managed in Secret Server after.",
                advice = managed > 20_000
                    ? "Over 20,000 managed accounts — a dedicated account migration plan is required. Coordinate stakeholders and agree on a cutoff date before proceeding."
                    : "Under 20,000 managed accounts — can be handled in a single migration day. Ensure all stakeholders know active password rotation will pause during migration.",
            });

        return new
        {
            total_items = total,
            managed_accounts = managed,
            risk_count = risks.Count,
            risks,
            overall = risks.Count == 0
                ? "No significant risks detected. Ready to migrate."
                : risks.Any(r => r.GetType().GetProperty("severity")?.GetValue(r)?.ToString() == "high")
                    ? "High-severity risks detected — review before running migration."
                    : "Low/medium risks only — review and proceed with awareness.",
        };
    }
}
