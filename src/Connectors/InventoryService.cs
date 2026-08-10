using System.Data;
using System.Text.Json;
using Dapper;

namespace PasMigration.Connectors;

/// <summary>
/// Runs read-only inventory against a tenant and persists the result as an
/// inventory_snapshot + inventory_item rows. Also builds the source/target
/// reconciliation diff. No secret values are read or stored - metadata only.
/// </summary>
public sealed class InventoryService(
    IDbConnection db,
    IPasConnectorFactory pasFactory,
    ISecretServerConnectorFactory ssFactory,
    ICyberArkConnectorFactory caFactory)
{
    /// <summary>
    /// Capture a full inventory for one tenant connection using session credentials.
    /// Returns the snapshot id and a summary. Read-only.
    /// </summary>
    public async Task<InventoryRunResult> CaptureAsync(
        Guid engagementId, RunInventoryInput input, CancellationToken ct)
    {
        // Resolve (or create) the tenant_connection row for this role.
        var connId = await db.ExecuteScalarAsync<Guid>(
            @"INSERT INTO tenant_connection
                (engagement_id, role, system_type, base_url, platform_tenant, auth_mode, credential_ref)
              VALUES (@EngagementId, @Role, @SystemType, @BaseUrl, NULL, @AuthMode, NULL)
              ON CONFLICT (engagement_id, role) DO UPDATE SET
                system_type = EXCLUDED.system_type,
                base_url    = EXCLUDED.base_url,
                auth_mode   = EXCLUDED.auth_mode
              RETURNING id",
            new
            {
                EngagementId = engagementId,
                input.Role,
                input.SystemType,
                BaseUrl = input.BaseUrl,
                input.AuthMode,
            });

        var items = input.SystemType switch
        {
            "pas" => await CapturePasAsync(pasFactory, input, ct),
            "cyberark" => await CaptureCyberArkAsync(caFactory, input, ct),
            _ => await CaptureSecretServerAsync(ssFactory, input, ct),
        };

        // Summary counts for the snapshot.
        var summary = new
        {
            accounts = items.Count(i => i.ItemType == "account"),
            local_accounts = items.Count(i => i.ItemType == "account"
                && (i.Attributes.TryGetValue("AccountScope", out var s) ? s?.ToString() : null) == "local"),
            domain_accounts = items.Count(i => i.ItemType == "account"
                && (i.Attributes.TryGetValue("AccountScope", out var s) ? s?.ToString() : null) == "domain"),
            text_secrets = items.Count(i => i.ItemType == "text_secret"),
            file_secrets = items.Count(i => i.ItemType == "file_secret"),
            folders = items.Count(i => i.ItemType == "folder"),
            // CyberArk only. Zero on a PAS or Secret Server snapshot.
            safes = items.Count(i => i.ItemType == "safe"),
            safe_members = items.Count(i => i.ItemType == "safe_member"),
            managed = items.Count(i => i is { ItemType: "account", IsManaged: true }),
            unmanaged = items.Count(i => i is { ItemType: "account", IsManaged: false }),
            // Multiplexed accounts have NO migration path - surfaced so they're visible pre-migration.
            multiplexed_accounts_no_migration_path = items.Count(i => i.ItemType == "multiplexed_account"),
            total = items.Count,
        };

        // Persist snapshot, then items, in a transaction.
        var phase = input.Role == "source" ? "pre" : "pre"; // both source+target capture in pre-phase
        var snapshotId = Guid.NewGuid();
        // Dapper auto-opens/closes per call, but an explicit transaction needs an open
        // connection. Open it ourselves before BeginTransaction.
        if (db.State != ConnectionState.Open)
        {
            if (db is System.Data.Common.DbConnection dbc)
                await dbc.OpenAsync(ct);
            else
                db.Open();
        }
        using (var tx = db.BeginTransaction())
        {
            await db.ExecuteAsync(
                @"INSERT INTO inventory_snapshot
                    (id, engagement_id, tenant_connection_id, phase, summary, status)
                  VALUES (@Id, @Eng, @Conn, @Phase, @Summary::jsonb, 'completed')",
                new
                {
                    Id = snapshotId,
                    Eng = engagementId,
                    Conn = connId,
                    Phase = phase,
                    Summary = JsonSerializer.Serialize(summary),
                }, tx);

            foreach (var it in items)
            {
                await db.ExecuteAsync(
                    @"INSERT INTO inventory_item
                        (snapshot_id, item_type, source_native_id, name, folder_path,
                         parent_ref, is_managed, size_bytes, attributes)
                      VALUES (@Snapshot, @ItemType, @NativeId, @Name, @FolderPath,
                              @ParentRef, @IsManaged, @SizeBytes, @Attributes::jsonb)
                      ON CONFLICT (snapshot_id, item_type, source_native_id) DO NOTHING",
                    new
                    {
                        Snapshot = snapshotId,
                        it.ItemType,
                        NativeId = it.NativeId,
                        it.Name,
                        FolderPath = it.FolderPath,
                        ParentRef = it.ParentRef,
                        IsManaged = it.IsManaged,
                        SizeBytes = it.SizeBytes,
                        Attributes = JsonSerializer.Serialize(it.Attributes),
                    }, tx);
            }
            tx.Commit();
        }

        return new InventoryRunResult(snapshotId, summary.total, summary.accounts,
            summary.text_secrets, summary.file_secrets, summary.folders,
            summary.safes, summary.safe_members);
    }

    // ---- PAS capture ----
    private static async Task<List<InvItem>> CapturePasAsync(
        IPasConnectorFactory pasFactory, RunInventoryInput input, CancellationToken ct)
    {
        var pas = pasFactory.Create(input.BaseUrl!, input.AppId!);
        using var creds = new TenantCredentials
            { ClientId = input.ClientId, ClientSecret = input.ClientSecret, Scope = input.Scope };
        await pas.AuthenticateAsync(creds, ct);

        var items = new List<InvItem>();

        // LOCAL accounts (Host set): classify Windows vs Unix by ComputerClass downstream.
        var localAccounts = await pas.InventoryLocalAccountsAsync(ct);
        if (localAccounts.Count == 0)
            localAccounts = await pas.InventoryLocalAccountsNoJoinAsync(ct);
        foreach (var r in localAccounts)
        {
            r["AccountScope"] = "local";
            items.Add(new InvItem
            {
                ItemType = "account",
                NativeId = Str(r, "ID"),
                Name = Str(r, "User"),
                FolderPath = Str(r, "ServerFQDN") ?? Str(r, "ServerName"),
                IsManaged = Bool(r, "IsManaged"),
                Attributes = r,
            });
        }

        // DOMAIN accounts (DomainID set): always Active Directory template.
        var domainAccounts = await pas.InventoryDomainAccountsAsync(ct);
        foreach (var r in domainAccounts)
        {
            r["AccountScope"] = "domain";
            items.Add(new InvItem
            {
                ItemType = "account",
                NativeId = Str(r, "ID"),
                Name = Str(r, "User"),
                FolderPath = Str(r, "DomainName"),
                IsManaged = Bool(r, "IsManaged"),
                Attributes = r,
            });
        }

        // MULTIPLEXED accounts: NO migration path - capture and flag for the pre-migration report.
        try
        {
            var muxed = await pas.InventoryMultiplexedAccountsAsync(ct);
            foreach (var r in muxed)
                items.Add(new InvItem
                {
                    ItemType = "multiplexed_account",
                    NativeId = Str(r, "ID"),
                    Name = Str(r, "Name"),
                    FolderPath = null,
                    Attributes = r,
                });
        }
        catch { /* table may be absent on some tenants */ }

        // Secrets: Type is Text or File.
        var secrets = await pas.InventorySecretsAsync(ct);
        foreach (var r in secrets)
        {
            var type = (Str(r, "Type") ?? "").Trim();
            var isFile = string.Equals(type, "File", StringComparison.OrdinalIgnoreCase);
            items.Add(new InvItem
            {
                ItemType = isFile ? "file_secret" : "text_secret",
                NativeId = Str(r, "ID"),
                Name = Str(r, "SecretName"),
                FolderPath = Str(r, "ParentPath"),
                SizeBytes = isFile ? Long(r, "SecretFileSize") : null,
                Attributes = r,
            });
        }

        // Folders: Phantom Sets.
        var folders = await pas.InventoryFolderSetsAsync(ct);
        foreach (var r in folders)
        {
            items.Add(new InvItem
            {
                ItemType = "folder",
                NativeId = Str(r, "ID"),
                Name = Str(r, "Name"),
                FolderPath = Str(r, "ParentPath"),
                Attributes = r,
            });
        }

        return items;
    }

    // ---- CyberArk capture ----

    /// <summary>
    /// Read-only capture of a CyberArk vault: safes, the accounts inside them, and the safe
    /// members with their raw permission switches.
    ///
    /// NO credential values are retrieved. Inventory deliberately does not call
    /// Password/Retrieve — that happens only during a migration run, for accounts the operator
    /// has selected. This keeps a discovery pass safe to run in a customer's production vault
    /// (it also keeps it fast: retrieval is one API call per account and is what makes the
    /// PowerShell export slow).
    ///
    /// Failures are NOT swallowed. If a safe cannot be enumerated the whole capture throws, so a
    /// partial snapshot is never persisted looking complete — the trap the original export script
    /// fell into, where a 429 mid-run produced a short CSV that reported success.
    /// </summary>
    private static async Task<List<InvItem>> CaptureCyberArkAsync(
        ICyberArkConnectorFactory caFactory, RunInventoryInput input, CancellationToken ct)
    {
        var ca = caFactory.Create(input.BaseUrl!);
        using var creds = new CyberArkCredentials
        {
            AuthMethod = CyberArkMigrationService.ParseAuthMethod(input.AuthMode),
            Username = input.Username,
            Password = input.ClientSecret,
            ClientId = input.ClientId,
            ClientSecret = input.ClientSecret,
            IdentityTokenUrl = input.IdentityTokenUrl,
        };
        await ca.AuthenticateAsync(creds, ct);

        try
        {
            var items = new List<InvItem>();
            var safes = await ca.ListSafesAsync(ct);

            foreach (var safe in safes)
            {
                ct.ThrowIfCancellationRequested();

                items.Add(new InvItem
                {
                    ItemType = "safe",
                    NativeId = safe.SafeName,   // safe names are unique within a vault
                    Name = safe.SafeName,
                    FolderPath = null,
                    Attributes = safe.Raw,
                });

                foreach (var acct in await ca.ListAccountsAsync(safe.SafeName, ct))
                {
                    var attrs = new Dictionary<string, object?>(acct.Raw)
                    {
                        ["safeName"] = acct.SafeName,
                        ["platformId"] = acct.PlatformId,
                        ["secretType"] = acct.SecretType,
                        ["userName"] = acct.UserName,
                        ["address"] = acct.Address,
                        ["cpmStatus"] = acct.CpmStatus,
                        // Resolved here so the pre-migration report can show the target template
                        // without re-deriving it, and so a surprising mapping is visible before
                        // anything is written.
                        ["delineaTemplateName"] = CyberArkTemplateMap.Resolve(acct.PlatformId).TemplateName,
                    };

                    items.Add(new InvItem
                    {
                        ItemType = "account",
                        NativeId = acct.Id,
                        Name = acct.Name ?? acct.UserName,
                        FolderPath = acct.SafeName,
                        IsManaged = acct.CpmManaged,
                        Attributes = attrs,
                    });
                }

                foreach (var member in await ca.ListSafeMembersAsync(safe.SafeName, ct))
                {
                    // Built-ins are vault service identities. Excluded at capture so they never
                    // reach a review screen or a permission run.
                    if (CyberArkPermissionMapper.IsBuiltInMember(member.MemberName)) continue;

                    var mapping = CyberArkPermissionMapper.Translate(member.Permissions);

                    items.Add(new InvItem
                    {
                        ItemType = "safe_member",
                        // Unique within the vault, and stable across runs so migration_item's
                        // (engagement, type, source id) key makes re-runs idempotent.
                        NativeId = $"{member.SafeName}|{member.MemberName}",
                        Name = member.MemberName,
                        FolderPath = member.SafeName,
                        Attributes = new Dictionary<string, object?>
                        {
                            ["safeName"] = member.SafeName,
                            ["memberName"] = member.MemberName,
                            ["memberType"] = member.MemberType,
                            ["memberId"] = member.MemberId,
                            ["permissions"] = member.Permissions,
                            // The translation is stored alongside the source so the pre-migration
                            // review shows what WILL happen, and so the over-grant and
                            // launcher-decision warnings are visible before anything is written.
                            ["delineaFolderRole"] = mapping.FolderRole.ToApiString(),
                            ["delineaSecretRole"] = mapping.SecretRole.ToApiString(),
                            ["accessProfile"] = mapping.Profile,
                            ["accessProfileKey"] = mapping.ProfileKey,
                            ["rolePermissions"] = mapping.RolePermissions,
                            ["withholdRolePermissions"] = mapping.WithholdRolePermissions,
                            ["cyberArkGranted"] = mapping.Granted,
                            ["overGrants"] = mapping.OverGrants,
                            ["notes"] = mapping.Notes,
                            ["unmapped"] = mapping.Unmapped,
                            ["needsLauncherDecision"] = mapping.NeedsLauncherDecision,
                            ["isApprover"] = mapping.Workflow.IsApprover,
                            ["approvalLevel"] = mapping.Workflow.ApprovalLevel,
                            ["bypassesApproval"] = mapping.Workflow.BypassesApproval,
                        },
                    });
                }
            }

            return items;
        }
        finally
        {
            // Best effort: PVWA sessions count against the vault's concurrent-session limit, so
            // an abandoned session after every inventory run is a real operational problem.
            try { await ca.LogoffAsync(CancellationToken.None); } catch { /* best effort */ }
        }
    }

    // ---- Secret Server capture ----
    private static async Task<List<InvItem>> CaptureSecretServerAsync(
        ISecretServerConnectorFactory ssFactory, RunInventoryInput input, CancellationToken ct)
    {
        var ss = ssFactory.Create(
            input.PlatformBaseUrl ?? input.BaseUrl!,
            input.SecretServerBaseUrl ?? input.BaseUrl!,
            input.AuthMode == "legacy_password"
                ? SecretServerConnector.AuthMode.LegacyPassword
                : SecretServerConnector.AuthMode.PlatformClientCredentials);

        using var creds = new TenantCredentials
            { ClientId = input.ClientId, ClientSecret = input.ClientSecret, Scope = input.Scope };
        if (input.AuthMode == "legacy_password")
            await ss.AuthenticateLegacyAsync(input.Username!, input.ClientSecret, ct);
        else
            await ss.AuthenticatePlatformAsync(creds, ct);

        var items = new List<InvItem>();

        var folders = await ss.InventoryFoldersAsync(ct);
        foreach (var r in folders)
            items.Add(new InvItem
            {
                ItemType = "folder",
                NativeId = Str(r, "id"),
                Name = Str(r, "folderName") ?? Str(r, "name"),
                FolderPath = Str(r, "folderPath"),
                Attributes = r,
            });

        var secrets = await ss.InventorySecretsAsync(ct);
        foreach (var r in secrets)
        {
            // SS list doesn't always distinguish file vs text; default to text_secret.
            items.Add(new InvItem
            {
                ItemType = "text_secret",
                NativeId = Str(r, "id"),
                Name = Str(r, "name"),
                FolderPath = Str(r, "folderPath") ?? Str(r, "folderId"),
                Attributes = r,
            });
        }

        return items;
    }

    // ---- Reconciliation: diff the latest source vs target snapshot ----
    public async Task<int> ReconcileAsync(Guid engagementId)
    {
        // Pull the most recent source and target snapshot ids.
        var snaps = (await db.QueryAsync(
            @"SELECT DISTINCT ON (tc.role) s.id, tc.role
              FROM inventory_snapshot s
              JOIN tenant_connection tc ON tc.id = s.tenant_connection_id
              WHERE s.engagement_id = @engagementId
              ORDER BY tc.role, s.captured_at DESC",
            new { engagementId })).ToList();

        Guid? sourceSnap = null, targetSnap = null;
        foreach (var s in snaps)
        {
            if ((string)s.role == "source") sourceSnap = (Guid)s.id;
            else if ((string)s.role == "target") targetSnap = (Guid)s.id;
        }
        if (sourceSnap is null) return 0;

        var src = (await db.QueryAsync(
            @"SELECT item_type, name, folder_path, id FROM inventory_item WHERE snapshot_id=@s",
            new { s = sourceSnap })).ToList();
        var tgt = targetSnap is null ? new List<dynamic>() : (await db.QueryAsync(
            @"SELECT item_type, name, folder_path, id FROM inventory_item WHERE snapshot_id=@s",
            new { s = targetSnap })).ToList();

        // match key = item_type + lower(folder_path)+name
        static string Key(string type, string? path, string? name) =>
            $"{type}|{(path ?? "").ToLowerInvariant()}|{(name ?? "").ToLowerInvariant()}";

        var tgtByKey = new Dictionary<string, dynamic>();
        foreach (var t in tgt)
            tgtByKey[Key((string)t.item_type, (string?)t.folder_path, (string?)t.name)] = t;

        // Clear prior results for this engagement, then recompute.
        await db.ExecuteAsync("DELETE FROM reconciliation_result WHERE engagement_id=@e",
            new { e = engagementId });

        var matchedTargetIds = new HashSet<Guid>();
        var count = 0;
        foreach (var s in src)
        {
            var key = Key((string)s.item_type, (string?)s.folder_path, (string?)s.name);
            var hasMatch = tgtByKey.TryGetValue(key, out var t);
            if (hasMatch) matchedTargetIds.Add((Guid)t.id);
            await db.ExecuteAsync(
                @"INSERT INTO reconciliation_result
                    (engagement_id, item_type, match_key, source_item_id, target_item_id, match_status)
                  VALUES (@e, @type, @key, @sid, @tid, @status)",
                new
                {
                    e = engagementId,
                    type = (string)s.item_type,
                    key,
                    sid = (Guid)s.id,
                    tid = hasMatch ? (Guid?)t.id : null,
                    status = hasMatch ? "matched" : "source_only",
                });
            count++;
        }
        // target_only: targets with no source match
        foreach (var t in tgt)
        {
            if (matchedTargetIds.Contains((Guid)t.id)) continue;
            await db.ExecuteAsync(
                @"INSERT INTO reconciliation_result
                    (engagement_id, item_type, match_key, source_item_id, target_item_id, match_status)
                  VALUES (@e, @type, @key, NULL, @tid, 'target_only')",
                new
                {
                    e = engagementId,
                    type = (string)t.item_type,
                    key = Key((string)t.item_type, (string?)t.folder_path, (string?)t.name),
                    tid = (Guid)t.id,
                });
            count++;
        }
        return count;
    }

    // ---- helpers: defensive reads from row dictionaries (case-insensitive) ----
    private static string? Str(Dictionary<string, object?> r, string key)
    {
        var v = Get(r, key);
        return v?.ToString();
    }
    private static bool? Bool(Dictionary<string, object?> r, string key)
    {
        var v = Get(r, key);
        return v switch { bool b => b, null => null, _ => bool.TryParse(v.ToString(), out var b) ? b : null };
    }
    private static long? Long(Dictionary<string, object?> r, string key)
    {
        var v = Get(r, key);
        return v switch { long l => l, null => null, _ => long.TryParse(v.ToString(), out var l) ? l : null };
    }
    private static object? Get(Dictionary<string, object?> r, string key)
    {
        if (r.TryGetValue(key, out var v)) return v;
        foreach (var kv in r) if (string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase)) return kv.Value;
        return null;
    }

    private sealed class InvItem
    {
        public required string ItemType { get; init; }
        public string? NativeId { get; init; }
        public string? Name { get; init; }
        public string? FolderPath { get; init; }
        public string? ParentRef { get; init; }
        public bool? IsManaged { get; init; }
        public long? SizeBytes { get; init; }
        public Dictionary<string, object?> Attributes { get; init; } = new();
    }
}

/// <summary>Run-inventory request. Credentials in-body, used in memory, never stored.</summary>
public sealed record RunInventoryInput(
    string Role,            // source | target
    string SystemType,      // pas | cyberark | secret_server
    string AuthMode,
    string? BaseUrl,
    string? PlatformBaseUrl,
    string? SecretServerBaseUrl,
    string? AppId,
    string ClientId,
    string ClientSecret,
    string? Username,
    string? Scope,
    string? IdentityTokenUrl = null);   // CyberArk Privilege Cloud only

public sealed record InventoryRunResult(
    Guid SnapshotId, int Total, int Accounts, int TextSecrets, int FileSecrets, int Folders,
    // CyberArk only; zero for the other source types.
    int Safes = 0, int SafeMembers = 0);
