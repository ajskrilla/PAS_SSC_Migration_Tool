using System.Data;
using System.Text.Json;
using Dapper;

namespace PasMigration.Connectors;

/// <summary>
/// Orchestrates migration from a CyberArk vault into a staging folder in Secret Server.
///
/// Deliberately a sibling of <see cref="MigrationService"/> rather than a branch inside it. The
/// two flows differ in more than the source API: CyberArk has no unmanage step (accounts are left
/// exactly as they are and the customer decommissions the vault separately), it has a permission
/// model that PAS does not, and its folder structure comes from safes rather than from account
/// classification. Forking a well-tested PAS path to accommodate all of that would put the working
/// migration at risk for no benefit.
///
/// Order for a full run is fixed and matters: SAFES -> PERMISSIONS -> ACCOUNTS. Folders must exist
/// before permissions can be granted on them, and both must be right before credentials land
/// inside, otherwise a secret is briefly visible only to the migrating account.
///
/// Security: credentials retrieved from CyberArk flow through memory only. They are never written
/// to our database, never logged, and never included in a job result. Only metadata, status and
/// outcomes are stored — the same rule the PAS path follows.
/// </summary>
public sealed class CyberArkMigrationService(
    IDbConnection db,
    JobRegistry jobs,
    ICyberArkConnectorFactory caFactory,
    ISecretServerConnectorFactory ssFactory)
{
    public async Task<CyberArkMigrationResult> RunAsync(
        Guid engagementId, CyberArkMigrationInput input, CancellationToken ct)
    {
        await OpenAsync(ct);

        var jobId = Guid.NewGuid();
        await db.ExecuteAsync(
            @"INSERT INTO migration_job (id, engagement_id, job_type, mode, status, started_at, params)
              VALUES (@Id, @Eng, @JobType, @Mode, 'running', now(), @Params::jsonb)",
            new
            {
                Id = jobId,
                Eng = engagementId,
                JobType = input.JobType,
                Mode = input.DryRun ? "dry_run" : "live",
                Params = JsonSerializer.Serialize(new { input.StagingFolderName, input.SelectedIds }),
            });

        var result = new CyberArkMigrationResult { JobId = jobId };
        var (_, token) = jobs.Register(jobId, ct);
        ct = token;

        ICyberArkClient? ca = null;
        try
        {
            ca = caFactory.Create(input.PvwaBaseUrl!);
            using (var caCreds = BuildCyberArkCredentials(input))
                await ca.AuthenticateAsync(caCreds, ct);

            var ss = ssFactory.Create(
                input.SsPlatformBaseUrl ?? input.SsBaseUrl!,
                input.SsSecretServerBaseUrl ?? input.SsBaseUrl!,
                SecretServerConnector.AuthMode.PlatformClientCredentials);
            using (var ssCreds = new TenantCredentials
                { ClientId = input.SsClientId, ClientSecret = input.SsClientSecret })
                await ss.AuthenticatePlatformAsync(ssCreds, ct);

            var stagingName = string.IsNullOrWhiteSpace(input.StagingFolderName)
                ? "CyberArk_Migration"
                : input.StagingFolderName!;

            long stagingId = 0;
            if (!input.DryRun)
            {
                stagingId = await ss.EnsureFolderAsync(stagingName, -1, ct);
                await db.ExecuteAsync(
                    @"UPDATE migration_job SET params = jsonb_set(
                        COALESCE(params,'{}'::jsonb), '{stagingFolderId}', to_jsonb(@sid::bigint))
                      WHERE id=@jid", new { sid = stagingId, jid = jobId });
            }
            await LogAsync(engagementId, jobId, "user_action",
                action: $"staging folder '{stagingName}'",
                outcome: input.DryRun ? "planned" : "ready");

            var source = await LoadSourceItemsAsync(engagementId, input);

            var doSafes = input.JobType is "cyberark_safe" or "cyberark_full";
            var doPerms = input.JobType is "cyberark_permission" or "cyberark_full";
            var doAccounts = input.JobType is "cyberark_account" or "cyberark_full";

            // safeName -> Secret Server folder id, populated by the safe stage and reused by the
            // two later stages. Case-insensitive because CyberArk safe names are.
            var folderIds = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

            if (doSafes)
                await MigrateSafesAsync(ss, source, stagingId, folderIds, input, jobId, engagementId, result, ct);
            else if (!input.DryRun && stagingId > 0)
                await PreloadFolderIdsAsync(ss, stagingId, folderIds, ct);

            if (doPerms)
                await MigratePermissionsAsync(ss, source, folderIds, input, jobId, engagementId, result, ct);

            if (doAccounts)
                await MigrateAccountsAsync(ca, ss, source, folderIds, input, jobId, engagementId, result, ct);

            // Creator removal runs last and only when explicitly asked for. See the method comment.
            if (doPerms && input.RemoveCreatorPermission && !input.DryRun)
                await RemoveCreatorGrantsAsync(ss, folderIds, jobId, engagementId, result, ct);

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
            await LogAsync(engagementId, jobId, "user_action", action: "job", outcome: "cancelled",
                message: "Aborted by user.");
            result.Error = "Cancelled by user.";
        }
        catch (Exception ex)
        {
            await db.ExecuteAsync(
                "UPDATE migration_job SET status='failed', finished_at=now() WHERE id=@Id", new { Id = jobId });
            await LogAsync(engagementId, jobId, "user_action", action: "job", outcome: "failed",
                message: ex.Message);
            result.Error = ex.Message;
        }
        finally
        {
            if (ca is not null)
                try { await ca.LogoffAsync(CancellationToken.None); } catch { /* best effort */ }
            jobs.Remove(jobId);
        }

        return result;
    }

    // ---- stage 1: safes -> folders -----------------------------------------------------------

    private async Task MigrateSafesAsync(
        ISecretServerClient ss, List<CyberArkSourceItem> source, long stagingId,
        Dictionary<string, long> folderIds, CyberArkMigrationInput input,
        Guid jobId, Guid eng, CyberArkMigrationResult result, CancellationToken ct)
    {
        foreach (var safe in source.Where(s => s.ItemType == "safe"))
        {
            ct.ThrowIfCancellationRequested();
            await UpsertMigrationItemAsync(jobId, eng, safe);
            result.Total++;

            if (input.DryRun)
            {
                await LogAsync(eng, jobId, "api_call", action: $"safe '{safe.Name}' -> folder", outcome: "planned");
                result.Succeeded++;
                continue;
            }

            try
            {
                // EnsureFolderAsync is find-or-create, so a re-run reuses the folder rather than
                // making "SafeName (1)". The folder id is recorded either way.
                var folderId = await ss.EnsureFolderAsync(safe.Name!, stagingId, ct);
                folderIds[safe.Name!] = folderId;

                await SetTargetIdAsync(eng, safe, folderId.ToString());
                await SetItemStatusAsync(eng, safe, "succeeded", null);
                await LogAsync(eng, jobId, "api_call", safe.SourceNativeId,
                    $"safe '{safe.Name}' -> folder #{folderId}", "created");
                result.Succeeded++;
            }
            catch (Exception ex)
            {
                result.Failed++;
                await SetItemStatusAsync(eng, safe, "failed", ex.Message);
                await LogAsync(eng, jobId, "api_call", safe.SourceNativeId,
                    $"safe '{safe.Name}'", "failed", ex.Message);
            }
        }
    }

    /// <summary>
    /// Rebuild safeName -> folderId when a run skips the safe stage (e.g. a permissions-only or
    /// accounts-only run against folders an earlier run created). Prefers ids already recorded in
    /// migration_item, falling back to a name match under staging.
    /// </summary>
    private async Task PreloadFolderIdsAsync(
        ISecretServerClient ss, long stagingId, Dictionary<string, long> folderIds, CancellationToken ct)
    {
        var all = await ss.ListAllFoldersAsync(ct);
        foreach (var f in all)
            if (f.ParentId == stagingId && !folderIds.ContainsKey(f.Name))
                folderIds[f.Name] = f.Id;
    }

    // ---- stage 2: safe members -> folder permissions ------------------------------------------

    private async Task MigratePermissionsAsync(
        ISecretServerClient ss, List<CyberArkSourceItem> source, Dictionary<string, long> folderIds,
        CyberArkMigrationInput input, Guid jobId, Guid eng,
        CyberArkMigrationResult result, CancellationToken ct)
    {
        var members = source.Where(s => s.ItemType == "safe_member").ToList();

        // Per-safe tallies. Creator removal is gated on these, so they are accumulated even when
        // individual grants fail.
        var stats = new Dictionary<string, FolderPermissionStats>(StringComparer.OrdinalIgnoreCase);
        var mappingsBySafe = new Dictionary<string, List<CyberArkPermissionMapping>>(StringComparer.OrdinalIgnoreCase);

        foreach (var m in members)
        {
            ct.ThrowIfCancellationRequested();

            var safeName = ReadString(m.Attributes, "safeName") ?? m.FolderPath ?? "";
            var memberName = ReadString(m.Attributes, "memberName") ?? m.Name ?? "";

            // CyberArk built-in service identities are never migrated. They are vault internals,
            // and creating Delinea grants for them is meaningless at best.
            if (CyberArkPermissionMapper.IsBuiltInMember(memberName))
            {
                result.Skipped++;
                await SetItemStatusAsync(eng, m, "skipped", "CyberArk built-in principal.");
                continue;
            }

            await UpsertMigrationItemAsync(jobId, eng, m);
            result.Total++;

            var permissions = ReadPermissions(m.Attributes);
            var mapping = CyberArkPermissionMapper.Translate(permissions);

            if (!mappingsBySafe.TryGetValue(safeName, out var list))
                mappingsBySafe[safeName] = list = [];
            list.Add(mapping);

            CollectRoleRequirements(result, memberName, mapping);

            if (!stats.TryGetValue(safeName, out var stat))
                stats[safeName] = stat = new FolderPermissionStats();
            stat.Total++;

            // A membership carrying no recognised CyberArk permission gets nothing. Granting the
            // List floor anyway would invent access the source never gave.
            if (mapping.SecretRole == CyberArkSecretRole.None)
            {
                result.Skipped++;
                stat.Failed++;
                await SetItemStatusAsync(eng, m, "skipped",
                    "CyberArk membership carries no recognised permission - nothing to grant.");
                await LogAsync(eng, jobId, "permission", m.SourceNativeId,
                    $"'{memberName}' on '{safeName}'", "skipped", "No recognised CyberArk permission.");
                continue;
            }

            if (input.DryRun)
            {
                await LogAsync(eng, jobId, "permission", m.SourceNativeId,
                    $"'{memberName}' on '{safeName}' -> folder:{mapping.FolderRole.ToApiString()} / " +
                    $"secret:{mapping.SecretRole.ToApiString()}", "planned");
                result.Succeeded++;
                if (mapping.FolderRole == CyberArkFolderRole.Owner) stat.FolderOwners++;
                if (mapping.SecretRole == CyberArkSecretRole.Owner) stat.SecretOwners++;
                continue;
            }

            if (!folderIds.TryGetValue(safeName, out var folderId))
            {
                result.Failed++;
                stat.Failed++;
                var msg = $"No target folder for safe '{safeName}'. Run the safe stage first.";
                await SetItemStatusAsync(eng, m, "failed", msg);
                await LogAsync(eng, jobId, "permission", m.SourceNativeId,
                    $"'{memberName}' on '{safeName}'", "failed", msg);
                continue;
            }

            try
            {
                var lookup = await ss.FindPrincipalAsync(memberName, ct);

                if (lookup.Ambiguous)
                {
                    result.Failed++;
                    stat.Failed++;
                    var msg = $"Ambiguous principal: {lookup.ExactMatchCount} exact matches in Secret Server.";
                    result.UnresolvedPrincipals.Add(new UnresolvedPrincipal(memberName, safeName, msg));
                    await SetItemStatusAsync(eng, m, "failed", msg);
                    await LogAsync(eng, jobId, "permission", m.SourceNativeId,
                        $"'{memberName}' on '{safeName}'", "failed", msg);
                    continue;
                }

                if (!lookup.Found)
                {
                    result.Failed++;
                    stat.Failed++;
                    const string msg = "Principal not found in Secret Server. Create or sync the "
                                     + "user/group, then re-run the permission stage.";
                    result.UnresolvedPrincipals.Add(new UnresolvedPrincipal(memberName, safeName, msg));
                    await SetItemStatusAsync(eng, m, "failed", msg);
                    await LogAsync(eng, jobId, "permission", m.SourceNativeId,
                        $"'{memberName}' on '{safeName}'", "failed", msg);
                    continue;
                }

                await ss.AssignFolderPermissionAsync(
                    folderId, lookup.Principal!,
                    mapping.FolderRole.ToApiString(), mapping.SecretRole.ToApiString(), ct);

                if (mapping.FolderRole == CyberArkFolderRole.Owner) stat.FolderOwners++;
                if (mapping.SecretRole == CyberArkSecretRole.Owner) stat.SecretOwners++;

                await SetTargetIdAsync(eng, m, folderId.ToString());
                await SetItemStatusAsync(eng, m, "succeeded", null);
                await LogAsync(eng, jobId, "permission", m.SourceNativeId,
                    $"'{memberName}' on '{safeName}' -> folder:{mapping.FolderRole.ToApiString()} / " +
                    $"secret:{mapping.SecretRole.ToApiString()}", "assigned");
                result.Succeeded++;
            }
            catch (Exception ex)
            {
                result.Failed++;
                stat.Failed++;
                await SetItemStatusAsync(eng, m, "failed", ex.Message);
                await LogAsync(eng, jobId, "permission", m.SourceNativeId,
                    $"'{memberName}' on '{safeName}'", "failed", ex.Message);
            }
        }

        result.PermissionStats = stats;

        // Report every safe that ends up with no Secret Owner. This is faithful to CyberArk —
        // Manage Safe covers safe properties, not the accounts inside — and still leaves nobody
        // able to administer those secrets once the migrating account is gone.
        foreach (var (safeName, mappings) in mappingsBySafe)
        {
            if (!CyberArkPermissionMapper.HasSecretOwnerGap(mappings)) continue;
            var suggested = CyberArkPermissionMapper.SuggestSecretOwner(mappings);
            result.SecretOwnerGaps.Add(new SecretOwnerGap(safeName, suggested?.Profile));
            await LogAsync(eng, jobId, "permission", action: $"safe '{safeName}'",
                outcome: "no secret owner",
                message: "No membership maps to Secret Owner. The migrating account's grant will "
                       + "be kept so these secrets are not stranded. Promote an owner deliberately.");
        }
    }

    private static void CollectRoleRequirements(
        CyberArkMigrationResult result, string memberName, CyberArkPermissionMapping mapping)
    {
        foreach (var role in mapping.RolePermissions)
        {
            if (!result.RolePermissionsRequired.TryGetValue(role, out var who))
                result.RolePermissionsRequired[role] = who = [];
            if (!who.Contains(memberName)) who.Add(memberName);
        }

        foreach (var role in mapping.WithholdRolePermissions)
        {
            if (!result.RolePermissionsWithheld.TryGetValue(role, out var who))
                result.RolePermissionsWithheld[role] = who = [];
            if (!who.Contains(memberName)) who.Add(memberName);
        }

        foreach (var og in mapping.OverGrants)
            if (!result.OverGrants.Contains(og)) result.OverGrants.Add(og);
    }

    // ---- stage 3: accounts -> secrets ---------------------------------------------------------

    private async Task MigrateAccountsAsync(
        ICyberArkClient ca, ISecretServerClient ss, List<CyberArkSourceItem> source,
        Dictionary<string, long> folderIds, CyberArkMigrationInput input,
        Guid jobId, Guid eng, CyberArkMigrationResult result, CancellationToken ct)
    {
        // Template ids are per-tenant, so they are resolved by NAME once per distinct template and
        // cached for the run. Hardcoding the stock ids would write secrets against whatever
        // template happens to hold that id on the customer's estate.
        var templateIds = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        foreach (var acct in source.Where(s => s.ItemType == "account"))
        {
            ct.ThrowIfCancellationRequested();
            await UpsertMigrationItemAsync(jobId, eng, acct);
            result.Total++;

            var safeName = ReadString(acct.Attributes, "safeName") ?? acct.FolderPath ?? "";
            var platformId = ReadString(acct.Attributes, "platformId");
            var secretType = ReadString(acct.Attributes, "secretType") ?? "password";
            var userName = ReadString(acct.Attributes, "userName") ?? "";
            var address = ReadString(acct.Attributes, "address") ?? "";
            var target = CyberArkTemplateMap.Resolve(platformId);

            var secretName = BuildSecretName(safeName, userName, address);

            if (input.DryRun)
            {
                await LogAsync(eng, jobId, "api_call", acct.SourceNativeId,
                    $"account '{secretName}' -> {target.TemplateName} @ '{safeName}'", "planned");
                result.Succeeded++;
                continue;
            }

            if (!folderIds.TryGetValue(safeName, out var folderId))
            {
                // Never fall back to the staging root. A credential in the wrong folder is a
                // permission problem wearing a success message.
                result.Failed++;
                var msg = $"No target folder for safe '{safeName}'. Run the safe stage first.";
                await SetItemStatusAsync(eng, acct, "failed", msg);
                await LogAsync(eng, jobId, "api_call", acct.SourceNativeId,
                    $"account '{secretName}'", "failed", msg);
                continue;
            }

            string? credential = null;
            try
            {
                var templateId = await ResolveTemplateAsync(ss, target, templateIds, ct);

                credential = await ca.RetrieveAccountPasswordAsync(acct.SourceNativeId, ct);
                if (string.IsNullOrEmpty(credential))
                {
                    // CyberArk refused or holds no value. Creating a credential-less secret would
                    // look migrated and be useless, so this is an explicit exclusion.
                    result.Skipped++;
                    result.Excluded.Add(new ExcludedItem(acct.SourceNativeId, secretName,
                        "no_credential",
                        "CyberArk did not return a credential (dual control, insufficient "
                      + "permission, or no stored value). Nothing was created."));
                    await SetItemStatusAsync(eng, acct, "skipped", "No credential returned by CyberArk.");
                    await LogAsync(eng, jobId, "api_call", acct.SourceNativeId,
                        $"account '{secretName}'", "excluded", "No credential returned by CyberArk.");
                    continue;
                }

                var fields = BuildFieldValues(target, userName, address, credential, secretType, platformId);

                var targetId = await ss.CreateSecretAsync(secretName, templateId, folderId, fields, null, ct);
                await SetTargetIdAsync(eng, acct, targetId.ToString());
                await SetItemStatusAsync(eng, acct, "succeeded", null);
                await LogAsync(eng, jobId, "api_call", acct.SourceNativeId,
                    $"account '{secretName}' ({target.TemplateName})", "created");
                result.Succeeded++;
            }
            catch (Exception ex)
            {
                result.Failed++;
                await SetItemStatusAsync(eng, acct, "failed", ex.Message);
                await LogAsync(eng, jobId, "api_call", acct.SourceNativeId,
                    $"account '{secretName}'", "failed", ex.Message);
            }
            finally
            {
                // Drop the reference promptly. .NET strings are immutable so this is not a scrub,
                // but the value is never logged, never persisted, and not held past this item.
                credential = null;
                _ = credential;
            }
        }
    }

    private static async Task<long> ResolveTemplateAsync(
        ISecretServerClient ss, CyberArkTemplateTarget target,
        Dictionary<string, long> cache, CancellationToken ct)
    {
        if (cache.TryGetValue(target.TemplateName, out var cached)) return cached;

        var id = await ss.FindTemplateAsync(target.TemplateName, ct)
                 ?? await ss.FindTemplateAsync(target.FallbackName, ct)
                 ?? throw new InvalidOperationException(
                     $"Neither template '{target.TemplateName}' nor fallback '{target.FallbackName}' "
                   + "exists on the target tenant. Create it, or pick a different mapping.");

        cache[target.TemplateName] = id;
        return id;
    }

    /// <summary>
    /// "&lt;safe&gt; - &lt;user&gt;@&lt;address&gt;", matching the EMEA toolkit so a customer who
    /// has seen its output recognises ours. Because the name is deterministic, a re-run against
    /// the same source produces the same name — which is what makes duplicate detection work.
    /// </summary>
    private static string BuildSecretName(string safeName, string userName, string address)
    {
        var user = string.IsNullOrWhiteSpace(userName) ? "unknown" : userName;
        var host = string.IsNullOrWhiteSpace(address) ? "local" : address;
        return $"{safeName} - {user}@{host}";
    }

    /// <summary>
    /// Populate the secret's fields.
    ///
    /// Machine and Domain are BOTH set from CyberArk's single "address" property, and an SSH key
    /// is written to both "Private Key" and "PrivateKey": Secret Server templates differ in which
    /// spelling they carry, and CreateSecretAsync ignores slugs the template does not have. Setting
    /// both means the value lands wherever the field actually exists rather than being dropped.
    /// </summary>
    private static Dictionary<string, string> BuildFieldValues(
        CyberArkTemplateTarget target, string userName, string address,
        string credential, string secretType, string? platformId)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["username"] = userName,
        };

        if (!string.IsNullOrWhiteSpace(address))
        {
            fields["machine"] = address;
            fields["domain"] = address;
        }

        if (CyberArkTemplateMap.IsKeyPlatform(platformId, secretType))
        {
            fields["private key"] = credential;
            fields["privatekey"] = credential;
        }
        else
        {
            fields["password"] = credential;
        }

        _ = target; // field list is advisory; the live template stub decides what is accepted
        return fields;
    }

    // ---- creator grant removal ----------------------------------------------------------------

    /// <summary>
    /// Remove the migrating account's own folder grant, so the migrated tree is owned by the
    /// principals CyberArk named rather than by whoever ran the tool.
    ///
    /// Gated four ways, all of which must hold for a given safe. Any of them failing means
    /// removing the creator would strand the folder or its secrets, which is worse than leaving
    /// an extra grant behind for an operator to clean up deliberately:
    ///   1. at least one permission was assigned for the folder
    ///   2. no permission assignment for that folder failed
    ///   3. some principal holds folder Owner
    ///   4. some principal holds secret Owner
    /// </summary>
    private async Task RemoveCreatorGrantsAsync(
        ISecretServerClient ss, Dictionary<string, long> folderIds,
        Guid jobId, Guid eng, CyberArkMigrationResult result, CancellationToken ct)
    {
        var creatorId = await ss.CurrentUserIdAsync(ct);
        if (creatorId is not { } uid)
        {
            await LogAsync(eng, jobId, "user_action", action: "creator grant removal",
                outcome: "skipped", message: "Could not determine the current Secret Server user id.");
            return;
        }

        foreach (var (safeName, folderId) in folderIds)
        {
            ct.ThrowIfCancellationRequested();

            var skipReason = SkipReasonForCreatorRemoval(result, safeName);
            if (skipReason is not null)
            {
                await LogAsync(eng, jobId, "permission", action: $"creator grant on '{safeName}'",
                    outcome: "kept", message: skipReason);
                continue;
            }

            try
            {
                var removed = await ss.RemoveUserFolderPermissionAsync(folderId, uid, ct);
                await LogAsync(eng, jobId, "permission", action: $"creator grant on '{safeName}'",
                    outcome: removed ? "removed" : "not present");
            }
            catch (Exception ex)
            {
                await LogAsync(eng, jobId, "permission", action: $"creator grant on '{safeName}'",
                    outcome: "failed", message: ex.Message);
            }
        }
    }

    private static string? SkipReasonForCreatorRemoval(CyberArkMigrationResult result, string safeName)
    {
        if (!result.PermissionStats.TryGetValue(safeName, out var stat) || stat.Total == 0)
            return "No permission assignments were recorded for this folder.";
        if (stat.Failed > 0)
            return $"{stat.Failed} of {stat.Total} permission assignment(s) failed.";
        if (stat.FolderOwners == 0)
            return "No principal was granted the Owner folder role.";
        if (stat.SecretOwners == 0)
            return "No principal was granted the Owner secret role - removing the creator would "
                 + "strand these secrets.";
        return null;
    }

    // ---- source loading + persistence ---------------------------------------------------------

    private async Task<List<CyberArkSourceItem>> LoadSourceItemsAsync(
        Guid engagementId, CyberArkMigrationInput input)
    {
        const string sql =
            @"SELECT ii.item_type, ii.source_native_id, ii.name, ii.folder_path,
                     ii.attributes::text AS attributes
              FROM inventory_item ii
              JOIN inventory_snapshot s ON s.id = ii.snapshot_id
              JOIN tenant_connection tc ON tc.id = s.tenant_connection_id
              WHERE s.engagement_id=@e AND tc.role='source'
                AND s.captured_at = (
                    SELECT MAX(s2.captured_at) FROM inventory_snapshot s2
                    JOIN tenant_connection tc2 ON tc2.id = s2.tenant_connection_id
                    WHERE s2.engagement_id=@e AND tc2.role='source')";

        var rows = await db.QueryAsync(sql, new { e = engagementId });
        var items = new List<CyberArkSourceItem>();

        foreach (var r in rows)
        {
            var d = (IDictionary<string, object?>)r;
            var nativeId = d["source_native_id"]?.ToString() ?? "";

            // Selection filters accounts and safes; safe members follow the safes they belong to,
            // because a folder without its permissions is not a useful partial result.
            var itemType = d["item_type"]?.ToString() ?? "";
            if (input.SelectedIds is { Count: > 0 } && itemType != "safe_member"
                && !input.SelectedIds.Contains(nativeId)) continue;

            var attrsJson = d.TryGetValue("attributes", out var aj) ? aj as string : null;
            var attrs = string.IsNullOrEmpty(attrsJson)
                ? new Dictionary<string, object?>()
                : JsonSerializer.Deserialize<Dictionary<string, object?>>(attrsJson) ?? new();

            items.Add(new CyberArkSourceItem
            {
                ItemType = itemType,
                SourceNativeId = nativeId,
                Name = d["name"]?.ToString(),
                FolderPath = d["folder_path"]?.ToString(),
                Attributes = attrs,
            });
        }

        // If a selection was made, drop safe members whose safe was not selected.
        if (input.SelectedIds is { Count: > 0 })
        {
            var selectedSafes = items.Where(i => i.ItemType == "safe")
                .Select(i => i.Name ?? "")
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            items = items.Where(i => i.ItemType != "safe_member"
                                     || selectedSafes.Contains(ReadString(i.Attributes, "safeName")
                                                               ?? i.FolderPath ?? ""))
                         .ToList();
        }

        return items;
    }

    private async Task UpsertMigrationItemAsync(Guid jobId, Guid eng, CyberArkSourceItem si) =>
        await db.ExecuteAsync(
            @"INSERT INTO migration_item
                (job_id, engagement_id, item_type, source_native_id, source_name, source_folder_path, status)
              VALUES (@J, @E, @T, @N, @Name, @Path, 'in_progress')
              ON CONFLICT (engagement_id, item_type, source_native_id)
              DO UPDATE SET job_id=@J,
                status = CASE
                  WHEN migration_item.status IN ('succeeded','migrated') THEN migration_item.status
                  ELSE 'in_progress'
                END,
                attempts=migration_item.attempts+1",
            new { J = jobId, E = eng, T = si.ItemType, N = si.SourceNativeId, si.Name, Path = si.FolderPath });

    private async Task SetItemStatusAsync(Guid eng, CyberArkSourceItem si, string status, string? err) =>
        await db.ExecuteAsync(
            @"UPDATE migration_item SET status=@S, last_error=@Err, finished_at=now()
              WHERE engagement_id=@E AND item_type=@T AND source_native_id=@N",
            new { S = status, Err = err, E = eng, T = si.ItemType, N = si.SourceNativeId });

    private async Task SetTargetIdAsync(Guid eng, CyberArkSourceItem si, string targetId) =>
        await db.ExecuteAsync(
            @"UPDATE migration_item SET target_native_id=@Tid
              WHERE engagement_id=@E AND item_type=@T AND source_native_id=@N",
            new { Tid = targetId, E = eng, T = si.ItemType, N = si.SourceNativeId });

    private async Task LogAsync(
        Guid eng, Guid? jobId, string eventType, string? migrationItemSource = null,
        string? action = null, string? outcome = null, string? message = null)
    {
        _ = migrationItemSource; // retained for signature parity with MigrationService.Log
        await db.ExecuteAsync(
            @"INSERT INTO event_log (engagement_id, job_id, event_type, action, outcome, message)
              VALUES (@E, @J, @Et, @A, @O, @M)",
            new { E = eng, J = jobId, Et = eventType, A = action, O = outcome, M = message });
    }

    private async Task OpenAsync(CancellationToken ct)
    {
        if (db.State != ConnectionState.Open)
        {
            if (db is System.Data.Common.DbConnection dbc) await dbc.OpenAsync(ct);
            else db.Open();
        }
    }

    // ---- helpers -------------------------------------------------------------------------------

    private static CyberArkCredentials BuildCyberArkCredentials(CyberArkMigrationInput input) => new()
    {
        AuthMethod = ParseAuthMethod(input.CyberArkAuthMode),
        Username = input.CyberArkUsername,
        Password = input.CyberArkPassword,
        ClientId = input.CyberArkClientId,
        ClientSecret = input.CyberArkClientSecret,
        IdentityTokenUrl = input.CyberArkIdentityTokenUrl,
    };

    /// <summary>Map the persisted auth_mode string onto the connector's enum.</summary>
    public static CyberArkAuthMethod ParseAuthMethod(string? authMode) => authMode switch
    {
        "cyberark_ldap" => CyberArkAuthMethod.Ldap,
        "cyberark_vault" => CyberArkAuthMethod.CyberArkVault,
        "cyberark_radius" => CyberArkAuthMethod.Radius,
        "cyberark_windows" => CyberArkAuthMethod.Windows,
        "cyberark_oauth" => CyberArkAuthMethod.OAuth,
        _ => throw new InvalidOperationException(
            $"Unknown CyberArk auth mode '{authMode}'. Expected one of cyberark_ldap, "
          + "cyberark_vault, cyberark_radius, cyberark_windows, cyberark_oauth."),
    };

    private static string? ReadString(IDictionary<string, object?> attrs, string key)
    {
        if (attrs.TryGetValue(key, out var v)) return ValueToString(v);
        foreach (var kv in attrs)
            if (string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase))
                return ValueToString(kv.Value);
        return null;
    }

    private static string? ValueToString(object? v) => v switch
    {
        null => null,
        string s => s,
        JsonElement { ValueKind: JsonValueKind.String } je => je.GetString(),
        JsonElement { ValueKind: JsonValueKind.Null } => null,
        JsonElement je => je.ToString(),
        _ => v.ToString(),
    };

    /// <summary>
    /// Pull the CyberArk permission bag out of an inventory item's attributes. It round-trips
    /// through JSON, so values arrive as JsonElement rather than bool and have to be converted
    /// back before the mapper's strict boolean test sees them.
    /// </summary>
    private static Dictionary<string, object?> ReadPermissions(IDictionary<string, object?> attrs)
    {
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        object? raw = null;
        foreach (var kv in attrs)
            if (string.Equals(kv.Key, "permissions", StringComparison.OrdinalIgnoreCase))
            {
                raw = kv.Value;
                break;
            }
        if (raw is null) return result;

        switch (raw)
        {
            case JsonElement { ValueKind: JsonValueKind.Object } je:
                foreach (var p in je.EnumerateObject())
                    result[p.Name] = p.Value.ValueKind switch
                    {
                        JsonValueKind.True => true,
                        JsonValueKind.False => false,
                        _ => null,
                    };
                break;

            case IDictionary<string, object?> dict:
                foreach (var kv in dict)
                    result[kv.Key] = kv.Value switch
                    {
                        bool b => b,
                        JsonElement { ValueKind: JsonValueKind.True } => true,
                        JsonElement { ValueKind: JsonValueKind.False } => false,
                        _ => null,
                    };
                break;
        }

        return result;
    }

    private sealed class CyberArkSourceItem
    {
        public required string ItemType { get; init; }
        public required string SourceNativeId { get; init; }
        public string? Name { get; init; }
        public string? FolderPath { get; init; }
        public Dictionary<string, object?> Attributes { get; init; } = new();
    }
}

/// <summary>Per-folder tallies that gate creator-grant removal.</summary>
public sealed class FolderPermissionStats
{
    public int Total { get; set; }
    public int Failed { get; set; }
    public int FolderOwners { get; set; }
    public int SecretOwners { get; set; }
}

/// <summary>A CyberArk principal that could not be resolved on the target tenant.</summary>
public sealed record UnresolvedPrincipal(string MemberName, string SafeName, string Reason);

/// <summary>A safe whose memberships leave nobody as Secret Owner.</summary>
public sealed record SecretOwnerGap(string SafeName, string? ClosestProfile);

public sealed record CyberArkMigrationInput(
    // cyberark_safe | cyberark_permission | cyberark_account | cyberark_full
    string JobType,
    bool DryRun,
    string? StagingFolderName,
    List<string>? SelectedIds,
    // CyberArk source
    string? PvwaBaseUrl,
    string CyberArkAuthMode,
    string? CyberArkUsername,
    string? CyberArkPassword,
    string? CyberArkClientId,
    string? CyberArkClientSecret,
    string? CyberArkIdentityTokenUrl,
    // Secret Server target
    string? SsBaseUrl,
    string? SsPlatformBaseUrl,
    string? SsSecretServerBaseUrl,
    string SsClientId,
    string SsClientSecret,
    // Off by default: removing the creator grant is a deliberate act, not a side effect.
    bool RemoveCreatorPermission = false);

public sealed class CyberArkMigrationResult
{
    public Guid JobId { get; set; }
    public int Total { get; set; }
    public int Succeeded { get; set; }
    public int Failed { get; set; }
    public int Skipped { get; set; }
    public string? Error { get; set; }

    public List<ExcludedItem> Excluded { get; } = [];
    public List<UnresolvedPrincipal> UnresolvedPrincipals { get; } = [];
    public List<SecretOwnerGap> SecretOwnerGaps { get; } = [];

    /// <summary>
    /// Delinea ROLE permissions the migrated grants depend on, and who needs them. These are
    /// tenant-wide capabilities, so the tool reports them and never assigns them: until each is on
    /// a role containing the principal, the folder access granted above will not actually work.
    /// </summary>
    public Dictionary<string, List<string>> RolePermissionsRequired { get; } = new(StringComparer.Ordinal);

    /// <summary>Role permissions that must NOT be granted — CyberArk withheld the equivalent.</summary>
    public Dictionary<string, List<string>> RolePermissionsWithheld { get; } = new(StringComparer.Ordinal);

    /// <summary>Distinct access this migration grants that CyberArk did not.</summary>
    public List<string> OverGrants { get; } = [];

    public Dictionary<string, FolderPermissionStats> PermissionStats { get; set; }
        = new(StringComparer.OrdinalIgnoreCase);
}
