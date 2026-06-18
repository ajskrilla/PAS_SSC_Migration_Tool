using System.Data;
using System.Text.Json;
using Dapper;

namespace PasMigration.Connectors;

/// <summary>
/// Orchestrates migration jobs from PAS into a staging folder in Secret Server / Platform.
/// Order per spec: text secrets -> file secrets -> accounts. Each item's state is tracked
/// in migration_item for resumability; every action is written to event_log.
///
/// Security: secret values/passwords/file bytes flow through memory only - never persisted
/// to our DB, never logged. Only metadata, status, and outcomes are stored.
/// </summary>
public sealed class MigrationService(IDbConnection db, IHttpClientFactory httpFactory, JobRegistry jobs)
{
    /// <summary>
    /// Run a migration job for one item type (or full). Credentials are passed in-memory.
    /// Dry-run performs all reads/planning but no target writes.
    /// </summary>
    public async Task<MigrationJobResult> RunAsync(
        Guid engagementId, MigrationRunInput input, CancellationToken ct)
    {
        var http = httpFactory.CreateClient("tenant");
        await OpenAsync(ct);

        // Create the job row.
        var jobId = Guid.NewGuid();
        await db.ExecuteAsync(
            @"INSERT INTO migration_job (id, engagement_id, job_type, mode, status, started_at, params)
              VALUES (@Id, @Eng, @JobType, @Mode, 'running', now(), @Params::jsonb)",
            new
            {
                Id = jobId, Eng = engagementId, JobType = input.JobType,
                Mode = input.DryRun ? "dry_run" : "live",
                Params = JsonSerializer.Serialize(new { input.StagingFolderName, input.SelectedIds }),
            });

        var result = new MigrationJobResult { JobId = jobId };
        // Register for cancellation so an abort request can stop this run mid-flight.
        var (_, token) = jobs.Register(jobId, ct);
        ct = token;
        try
        {
            // Connect to both tenants.
            var pas = new PasConnector(http, input.PasBaseUrl!, input.PasAppId!);
            using (var pcreds = new TenantCredentials
                { ClientId = input.PasClientId, ClientSecret = input.PasClientSecret, Scope = input.PasScope })
                await pas.AuthenticateAsync(pcreds, ct);

            var ss = new SecretServerConnector(
                http, input.SsPlatformBaseUrl ?? input.SsBaseUrl!, input.SsSecretServerBaseUrl ?? input.SsBaseUrl!,
                SecretServerConnector.AuthMode.PlatformClientCredentials);
            using (var screds = new TenantCredentials
                { ClientId = input.SsClientId, ClientSecret = input.SsClientSecret })
                await ss.AuthenticatePlatformAsync(screds, ct);

            // Ensure the staging root folder (idempotent).
            var stagingName = string.IsNullOrWhiteSpace(input.StagingFolderName)
                ? $"PAS_Migration_{DateTime.UtcNow:yyyyMMdd}"
                : input.StagingFolderName!;
            long stagingId = 0;
            if (!input.DryRun)
            {
                stagingId = await ss.EnsureFolderAsync(stagingName, -1, ct);
                // Persist the staging folder id so Revert can delete the whole tree later.
                await db.ExecuteAsync(
                    @"UPDATE migration_job SET params = jsonb_set(
                        COALESCE(params,'{}'::jsonb), '{stagingFolderId}', to_jsonb(@sid::bigint))
                      WHERE id=@jid", new { sid = stagingId, jid = jobId });
            }
            await Log(engagementId, jobId, "user_action", action: $"staging folder '{stagingName}'",
                outcome: input.DryRun ? "planned" : "ready");

            // Load the selected source items from the latest source snapshot.
            var sourceItems = await LoadSourceItems(engagementId, input, ct);

            // Folder cache: maps a path key -> target folder id. Pre-load EVERY existing folder
            // under staging so re-runs REUSE folders instead of creating duplicates.
            var folderCache = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            if (!input.DryRun && stagingId > 0)
                await PreloadFolderCache(ss, stagingId, folderCache, ct);

            foreach (var si in sourceItems)
            {
                ct.ThrowIfCancellationRequested();
                if (input.JobType != "full" && si.ItemType != JobTypeToItem(input.JobType)) continue;

                await UpsertMigrationItem(jobId, engagementId, si);
                try
                {
                    if (si.ItemType == "text_secret")
                        await MigrateTextSecret(pas, ss, si, stagingId, folderCache, input, jobId, engagementId, ct);
                    else if (si.ItemType == "file_secret")
                        await MigrateFileSecret(pas, ss, si, stagingId, folderCache, input, jobId, engagementId, ct);
                    else if (si.ItemType == "account")
                        await MigrateAccount(pas, ss, si, stagingId, folderCache, input, jobId, engagementId, result, ct);
                    else
                        continue; // folders are created on demand

                    result.Succeeded++;
                    await SetItemStatus(engagementId, si, "succeeded", null);
                }
                catch (MigrationSkip)
                {
                    // Already recorded as skipped+excluded inside MigrateAccount; don't double-count.
                }
                catch (OperationCanceledException)
                {
                    throw; // propagate to the outer handler -> job marked 'cancelled'
                }
                catch (Exception ex)
                {
                    result.Failed++;
                    // Find the first stack frame in OUR code (skip internal .NET ThrowHelper frames)
                    // so the log names the exact connector method + line that threw.
                    var frames = (ex.StackTrace ?? "").Split('\n')
                        .Select(f => f.Trim())
                        .Where(f => f.Length > 0)
                        .ToArray();
                    var ourFrame = frames.FirstOrDefault(f => f.Contains("PasMigration"))
                                   ?? frames.FirstOrDefault() ?? "(no stack)";
                    var detail = $"[{ex.GetType().Name}] {ex.Message} @ {ourFrame}";
                    await SetItemStatus(engagementId, si, "failed", detail);
                    await Log(engagementId, jobId, "api_call", migrationItemSource: si.SourceNativeId,
                        action: $"migrate {si.ItemType} '{si.Name}'", outcome: "failed", message: detail);
                }
                result.Total++;
            }

            await db.ExecuteAsync(
                @"UPDATE migration_job SET status='completed', finished_at=now(),
                    total=@T, succeeded=@S, failed=@F, skipped=@K WHERE id=@Id",
                new { Id = jobId, T = result.Total, S = result.Succeeded, F = result.Failed, K = result.Skipped });
        }
        catch (OperationCanceledException)
        {
            await db.ExecuteAsync(
                @"UPDATE migration_job SET status='cancelled', finished_at=now(),
                    total=@T, succeeded=@S, failed=@F, skipped=@K WHERE id=@Id",
                new { Id = jobId, T = result.Total, S = result.Succeeded, F = result.Failed, K = result.Skipped });
            await Log(engagementId, jobId, "user_action", action: "job", outcome: "cancelled",
                message: "Aborted by user.");
            result.Error = "Cancelled by user.";
        }
        catch (Exception ex)
        {
            await db.ExecuteAsync(
                "UPDATE migration_job SET status='failed', finished_at=now() WHERE id=@Id", new { Id = jobId });
            await Log(engagementId, jobId, "user_action", action: "job", outcome: "failed", message: ex.Message);
            result.Error = ex.Message;
        }
        finally
        {
            jobs.Remove(jobId);
        }
        return result;
    }

    // ---- per-type migration ----

    private async Task MigrateTextSecret(
        PasConnector pas, SecretServerConnector ss, SourceItem si, long stagingId,
        Dictionary<string, long> folderCache, MigrationRunInput input, Guid jobId, Guid eng, CancellationToken ct)
    {
        if (input.DryRun)
        {
            await Log(eng, jobId, "api_call", si.SourceNativeId, $"text_secret '{si.Name}'", "planned");
            return;
        }
        var folderId = await EnsureFolderPath(ss, stagingId, si.FolderPath, folderCache, ct);
        var template = await ss.FindTemplateAsync("Password", ct)
            ?? throw new InvalidOperationException("Password template not found on target.");
        var content = await pas.RetrieveTextSecretAsync(si.SourceNativeId, ct); // in memory only
        var targetId = await ss.CreateSecretAsync(
            si.Name ?? "(unnamed)", template, folderId,
            new Dictionary<string, string> { ["password"] = content, ["notes"] = si.Description ?? "" },
            null, ct);
        await SetTargetId(eng, si, targetId.ToString());
        await Log(eng, jobId, "api_call", si.SourceNativeId, $"text_secret '{si.Name}'", "created");
    }

    private async Task MigrateFileSecret(
        PasConnector pas, SecretServerConnector ss, SourceItem si, long stagingId,
        Dictionary<string, long> folderCache, MigrationRunInput input, Guid jobId, Guid eng, CancellationToken ct)
    {
        if (input.DryRun)
        {
            await Log(eng, jobId, "api_call", si.SourceNativeId, $"file_secret '{si.Name}'", "planned");
            return;
        }
        var folderId = await EnsureFolderPath(ss, stagingId, si.FolderPath, folderCache, ct);
        var template = await ss.EnsureFileMigrationTemplateAsync(ct);
        var bytes = await pas.DownloadFileSecretAsync(si.SourceNativeId, ct); // in memory only
        var b64 = Convert.ToBase64String(bytes);
        var filename = si.Name ?? "file";
        var targetId = await ss.CreateSecretAsync(
            si.Name ?? "(unnamed)", template, folderId,
            new Dictionary<string, string> { ["description"] = si.Description ?? "" },
            (Slug: "file", Filename: filename, Base64: b64), ct);
        await SetTargetId(eng, si, targetId.ToString());

        // Byte-fidelity check (the §11 open item): confirm the attachment landed.
        var ok = await ss.SecretHasFileAttachmentAsync(targetId, ct);
        await Log(eng, jobId, "api_call", si.SourceNativeId, $"file_secret '{si.Name}'",
            ok ? "created+verified" : "created (attachment unverified)");
    }

    private async Task MigrateAccount(
        PasConnector pas, SecretServerConnector ss, SourceItem si, long stagingId,
        Dictionary<string, long> folderCache, MigrationRunInput input, Guid jobId, Guid eng,
        MigrationJobResult result, CancellationToken ct)
    {
        // Unmanage first; if it fails, bail this item -> exclusion list, keep going.
        if (!input.DryRun)
        {
            try
            {
                await pas.UnmanageAccountAsync(si.SourceNativeId, ct);
                await Log(eng, jobId, "api_call", si.SourceNativeId, $"unmanage '{si.Name}'", "ok");
            }
            catch (Exception ex)
            {
                result.Excluded.Add(new ExcludedItem(si.SourceNativeId, si.Name, "unmanage_failed", ex.Message));
                result.Skipped++;
                await SetItemStatus(eng, si, "skipped", $"unmanage failed: {ex.Message}");
                await Log(eng, jobId, "api_call", si.SourceNativeId, $"unmanage '{si.Name}'",
                    "excluded", ex.Message);
                throw new MigrationSkip(); // counted as skip, not failure
            }
        }

        // Pick Windows vs UNIX template from ComputerClass captured in inventory attributes.
        var computerClass = (si.Attributes.TryGetValue("ComputerClass", out var cc) ? cc?.ToString() : null) ?? "";
        var isUnix = computerClass.Contains("Unix", StringComparison.OrdinalIgnoreCase)
                  || computerClass.Contains("Linux", StringComparison.OrdinalIgnoreCase);
        var templateName = isUnix ? "Unix Account (SSH)" : "Windows Account";

        if (input.DryRun)
        {
            await Log(eng, jobId, "api_call", si.SourceNativeId,
                $"account '{si.Name}' -> {templateName}", "planned");
            return;
        }

        var folderId = await EnsureFolderPath(ss, stagingId, si.FolderPath, folderCache, ct);
        var template = await ss.FindTemplateAsync(templateName, ct)
            ?? await ss.FindTemplateAsync(isUnix ? "Unix Account" : "Windows Account", ct)
            ?? throw new InvalidOperationException($"Account template '{templateName}' not found on target.");

        var (password, coid) = await pas.CheckoutPasswordAsync(si.SourceNativeId, ct);
        try
        {
            var targetId = await ss.CreateSecretAsync(
                si.Name ?? "(unnamed)", template, folderId,
                new Dictionary<string, string>
                {
                    ["username"] = si.Name ?? "",
                    ["password"] = password,
                    ["machine"] = si.FolderPath ?? "",
                    ["notes"] = si.Description ?? "",
                }, null, ct);
            await SetTargetId(eng, si, targetId.ToString());
            await Log(eng, jobId, "api_call", si.SourceNativeId,
                $"account '{si.Name}' ({templateName})", "created");
        }
        finally
        {
            if (!string.IsNullOrEmpty(coid))
                try { await pas.CheckinPasswordAsync(coid, ct); } catch { /* best effort */ }
        }
    }

    // ---- revert (delete tool-created items under staging) ----

    /// <summary>
    /// Delete every target item this tool created for the engagement (tracked by target_native_id).
    /// For lab testing only. Requires explicit confirm at the API layer.
    /// </summary>
    public async Task<RevertResult> RevertAsync(Guid engagementId, MigrationRunInput conn, CancellationToken ct)
    {
        var http = httpFactory.CreateClient("tenant");
        await OpenAsync(ct);
        var ss = new SecretServerConnector(
            http, conn.SsPlatformBaseUrl ?? conn.SsBaseUrl!, conn.SsSecretServerBaseUrl ?? conn.SsBaseUrl!,
            SecretServerConnector.AuthMode.PlatformClientCredentials);
        using (var screds = new TenantCredentials { ClientId = conn.SsClientId, ClientSecret = conn.SsClientSecret })
            await ss.AuthenticatePlatformAsync(screds, ct);

        var created = (await db.QueryAsync<(string id, string type)>(
            @"SELECT target_native_id AS id, item_type AS type FROM migration_item
              WHERE engagement_id=@e AND target_native_id IS NOT NULL",
            new { e = engagementId })).ToList();

        var res = new RevertResult();
        // 1) Delete the tracked secrets first (so counts reflect actual tool-created secrets).
        foreach (var c in created)
        {
            if (!long.TryParse(c.id, out var tid)) continue;
            var ok = await ss.DeleteSecretAsync(tid, ct);
            if (ok) res.Deleted++; else res.Failed++;
        }

        // 2) Delete the staging folder(s) this engagement's jobs created. Deleting a folder in
        //    Secret Server cascades to its subfolders AND any secrets inside - so this cleans up
        //    the entire mirrored tree, including duplicates from earlier partial runs.
        var stagingIds = (await db.QueryAsync<long?>(
            @"SELECT (params->>'stagingFolderId')::bigint FROM migration_job
              WHERE engagement_id=@e AND params ? 'stagingFolderId'",
            new { e = engagementId }))
            .Where(x => x.HasValue).Select(x => x!.Value).Distinct().ToList();

        foreach (var fid in stagingIds)
        {
            var ok = await ss.DeleteFolderAsync(fid, ct);
            if (ok) res.FoldersDeleted++; else res.Failed++;
            await Log(engagementId, null, "user_action",
                action: $"revert staging folder #{fid}", outcome: ok ? "deleted" : "failed");
        }

        // Clear target ids so the items can be re-migrated cleanly.
        await db.ExecuteAsync(
            "UPDATE migration_item SET target_native_id=NULL, status='pending' WHERE engagement_id=@e",
            new { e = engagementId });
        await Log(engagementId, null, "user_action", action: "revert",
            outcome: $"deleted {res.Deleted} secrets, {res.FoldersDeleted} folders, failed {res.Failed}");
        return res;
    }

    // ---- helpers ----

    /// <summary>
    /// List every folder once and populate the cache with the relative path (under staging)
    /// -> folder id for all descendants of the staging folder. This makes re-runs REUSE the
    /// existing folder tree instead of creating duplicates (the path keys here match exactly
    /// the running-path keys built in EnsureFolderPath: slash-joined, relative to staging).
    /// </summary>
    private static async Task PreloadFolderCache(
        SecretServerConnector ss, long stagingId, Dictionary<string, long> cache, CancellationToken ct)
    {
        var all = await ss.ListAllFoldersAsync(ct);
        // Index by parent for a downward walk from staging.
        var byParent = all.GroupBy(f => f.ParentId)
                          .ToDictionary(g => g.Key, g => g.ToList());

        // BFS from staging's direct children, accumulating the relative path.
        var queue = new Queue<(long Id, string Path)>();
        if (byParent.TryGetValue(stagingId, out var roots))
            foreach (var c in roots) queue.Enqueue((c.Id, c.Name));

        while (queue.Count > 0)
        {
            var (id, path) = queue.Dequeue();
            // First writer wins; if a duplicate same-path folder exists, we reuse the first.
            if (!cache.ContainsKey(path)) cache[path] = id;
            if (byParent.TryGetValue(id, out var kids))
                foreach (var k in kids)
                    queue.Enqueue((k.Id, $"{path}/{k.Name}"));
        }
    }

    private async Task<long> EnsureFolderPath(
        SecretServerConnector ss, long stagingId, string? sourcePath,
        Dictionary<string, long> cache, CancellationToken ct)
    {
        // Mirror the source path under staging. Leaf-only paths create one level; deeper
        // paths (a/b/c) create the chain. Empty path -> staging root.
        if (string.IsNullOrWhiteSpace(sourcePath)) return stagingId;
        if (cache.TryGetValue(sourcePath, out var cached)) return cached;

        var parts = sourcePath.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
        var parentId = stagingId;
        var running = "";
        foreach (var part in parts)
        {
            running = running.Length == 0 ? part : $"{running}/{part}";
            if (!cache.TryGetValue(running, out var fid))
            {
                fid = await ss.EnsureFolderAsync(part, parentId, ct);
                cache[running] = fid;
            }
            parentId = fid;
        }
        cache[sourcePath] = parentId;
        return parentId;
    }

    private async Task<List<SourceItem>> LoadSourceItems(
        Guid engagementId, MigrationRunInput input, CancellationToken ct)
    {
        var sql = @"SELECT ii.item_type, ii.source_native_id, ii.name, ii.folder_path,
                           ii.is_managed, ii.attributes::text AS attributes
                    FROM inventory_item ii
                    JOIN inventory_snapshot s ON s.id = ii.snapshot_id
                    JOIN tenant_connection tc ON tc.id = s.tenant_connection_id
                    WHERE s.engagement_id=@e AND tc.role='source'
                      AND s.captured_at = (
                        SELECT MAX(s2.captured_at) FROM inventory_snapshot s2
                        JOIN tenant_connection tc2 ON tc2.id = s2.tenant_connection_id
                        WHERE s2.engagement_id=@e AND tc2.role='source')";
        var rows = await db.QueryAsync(sql, new { e = engagementId });
        var items = new List<SourceItem>();
        foreach (var r in rows)
        {
            var d = (IDictionary<string, object?>)r;
            var nativeId = d["source_native_id"]?.ToString() ?? "";
            if (input.SelectedIds is { Count: > 0 } && !input.SelectedIds.Contains(nativeId)) continue;
            var attrsJson = d.TryGetValue("attributes", out var aj) ? aj as string : null;
            var attrs = string.IsNullOrEmpty(attrsJson)
                ? new Dictionary<string, object?>()
                : JsonSerializer.Deserialize<Dictionary<string, object?>>(attrsJson) ?? new();
            items.Add(new SourceItem
            {
                ItemType = d["item_type"]?.ToString() ?? "",
                SourceNativeId = nativeId,
                Name = d["name"]?.ToString(),
                FolderPath = d["folder_path"]?.ToString(),
                Description = attrs.TryGetValue("Description", out var de) ? de?.ToString() : null,
                Attributes = attrs,
            });
        }
        return items;
    }

    private async Task UpsertMigrationItem(Guid jobId, Guid eng, SourceItem si) =>
        await db.ExecuteAsync(
            @"INSERT INTO migration_item
                (job_id, engagement_id, item_type, source_native_id, source_name, source_folder_path, status)
              VALUES (@J, @E, @T, @N, @Name, @Path, 'in_progress')
              ON CONFLICT (engagement_id, item_type, source_native_id)
              DO UPDATE SET job_id=@J, status='in_progress', attempts=migration_item.attempts+1",
            new { J = jobId, E = eng, T = si.ItemType, N = si.SourceNativeId, si.Name, Path = si.FolderPath });

    private async Task SetItemStatus(Guid eng, SourceItem si, string status, string? err) =>
        await db.ExecuteAsync(
            @"UPDATE migration_item SET status=@S, last_error=@Err, finished_at=now()
              WHERE engagement_id=@E AND item_type=@T AND source_native_id=@N",
            new { S = status, Err = err, E = eng, T = si.ItemType, N = si.SourceNativeId });

    private async Task SetTargetId(Guid eng, SourceItem si, string targetId) =>
        await db.ExecuteAsync(
            @"UPDATE migration_item SET target_native_id=@Tid
              WHERE engagement_id=@E AND item_type=@T AND source_native_id=@N",
            new { Tid = targetId, E = eng, T = si.ItemType, N = si.SourceNativeId });

    private async Task Log(
        Guid eng, Guid? jobId, string eventType, string? migrationItemSource = null,
        string? action = null, string? outcome = null, string? message = null) =>
        await db.ExecuteAsync(
            @"INSERT INTO event_log (engagement_id, job_id, event_type, action, outcome, message)
              VALUES (@E, @J, @Et, @A, @O, @M)",
            new { E = eng, J = jobId, Et = eventType, A = action, O = outcome, M = message });

    private static string JobTypeToItem(string jobType) => jobType switch
    {
        "text_secret" => "text_secret",
        "file_secret" => "file_secret",
        "account_unmanage_export" => "account",
        _ => jobType,
    };

    private async Task OpenAsync(CancellationToken ct)
    {
        if (db.State != ConnectionState.Open)
        {
            if (db is System.Data.Common.DbConnection dbc) await dbc.OpenAsync(ct);
            else db.Open();
        }
    }

    private sealed class SourceItem
    {
        public required string ItemType { get; init; }
        public required string SourceNativeId { get; init; }
        public string? Name { get; init; }
        public string? FolderPath { get; init; }
        public string? Description { get; init; }
        public Dictionary<string, object?> Attributes { get; init; } = new();
    }

    private sealed class MigrationSkip : Exception { }
}

public sealed record MigrationRunInput(
    string JobType,            // text_secret | file_secret | account_unmanage_export | full
    bool DryRun,
    string? StagingFolderName,
    List<string>? SelectedIds, // null/empty = all of the type
    // PAS connection
    string? PasBaseUrl, string? PasAppId, string PasClientId, string PasClientSecret, string? PasScope,
    // SS connection
    string? SsBaseUrl, string? SsPlatformBaseUrl, string? SsSecretServerBaseUrl,
    string SsClientId, string SsClientSecret);

public sealed class MigrationJobResult
{
    public Guid JobId { get; set; }
    public int Total { get; set; }
    public int Succeeded { get; set; }
    public int Failed { get; set; }
    public int Skipped { get; set; }
    public string? Error { get; set; }
    public List<ExcludedItem> Excluded { get; set; } = new();
}

public sealed record ExcludedItem(string SourceNativeId, string? Name, string Reason, string Detail);

public sealed class RevertResult { public int Deleted { get; set; } public int FoldersDeleted { get; set; } public int Failed { get; set; } }
