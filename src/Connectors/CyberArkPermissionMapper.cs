namespace PasMigration.Connectors;

/// <summary>
/// Delinea folder access role. Values are the rank ladder: higher wins, never downgraded.
/// </summary>
public enum CyberArkFolderRole
{
    View = 1,
    AddSecret = 2,
    Edit = 3,
    Owner = 4,
}

/// <summary>
/// Delinea secret access role. Note the asymmetry with <see cref="CyberArkFolderRole"/>:
/// secret roles have a <c>None</c> and a <c>List</c> level, folder roles have neither, and
/// folder roles have an <c>Add Secret</c> level that secret roles do not. This is Secret
/// Server's model, not a modelling choice here.
/// </summary>
public enum CyberArkSecretRole
{
    None = 0,
    List = 1,
    View = 2,
    Edit = 3,
    Owner = 4,
}

/// <summary>Workflow facts that are policy configuration, not folder/secret ACLs.</summary>
public sealed record CyberArkWorkflow
{
    public bool IsApprover { get; init; }
    public int ApprovalLevel { get; init; }
    public bool BypassesApproval { get; init; }
}

/// <summary>
/// The translation of ONE CyberArk safe membership into Delinea terms.
///
/// Four things are returned separately and deliberately: the folder role, the secret role,
/// the tenant-wide role permissions the principal additionally needs, and the role permissions
/// that must NOT be granted. Collapsing CyberArk's independent permission switches onto Delinea's
/// cumulative levels always over-permissions somebody, so the loss is reported rather than hidden.
/// </summary>
public sealed record CyberArkPermissionMapping
{
    public CyberArkFolderRole FolderRole { get; init; } = CyberArkFolderRole.View;
    public CyberArkSecretRole SecretRole { get; init; } = CyberArkSecretRole.None;

    /// <summary>Delinea ROLE permissions this principal needs. Reported, never assigned.</summary>
    public IReadOnlyList<string> RolePermissions { get; init; } = Array.Empty<string>();

    /// <summary>Role permission name -> the CyberArk permission labels that caused it.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> RoleReasons { get; init; }
        = new Dictionary<string, IReadOnlyList<string>>();

    /// <summary>Role permissions that must NOT be granted — CyberArk withheld the equivalent.</summary>
    public IReadOnlyList<string> WithholdRolePermissions { get; init; } = Array.Empty<string>();

    public CyberArkWorkflow Workflow { get; init; } = new();

    /// <summary>Human-readable access profile, e.g. "Safe owner", "PSM-only user".</summary>
    public string Profile { get; init; } = "No effective access";

    /// <summary>Sorted '+'-joined canonical tokens. Two memberships with the same key are identical.</summary>
    public string ProfileKey { get; init; } = "";

    /// <summary>Labels of the CyberArk permissions that were ON, in canonical display order.</summary>
    public IReadOnlyList<string> Granted { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();

    /// <summary>Access this principal GAINS that CyberArk did not give them.</summary>
    public IReadOnlyList<string> OverGrants { get; init; } = Array.Empty<string>();

    /// <summary>CyberArk permissions with no Delinea folder/secret equivalent at all.</summary>
    public IReadOnlyList<string> Unmapped { get; init; } = Array.Empty<string>();

    /// <summary>True when a human must decide whether the launcher may reveal the password.</summary>
    public bool NeedsLauncherDecision { get; init; }
}

/// <summary>
/// Translates CyberArk safe-member permissions into Delinea folder/secret roles.
///
/// Ported from Convert-CyberArkPermission in the EMEA Professional Services toolkit
/// (Migrate-CyberArkToDelinea.ps1). The decision rules are reproduced exactly; two things were
/// deliberately changed in the port and are called out at each site:
///   1. Ordering is made explicit (the PowerShell original iterated unordered hashtables, so
///      profile keys and reason lists were non-deterministic run to run).
///   2. Roles are enums rather than magic strings, so a typo cannot reach the Secret Server API.
///
/// Nothing in this class performs I/O. It is pure, total, and fully unit-testable.
///
/// DESIGN NOTE — why four outputs and not one: CyberArk safe permissions are 22 independent
/// switches; Delinea folder and secret roles are 4-level cumulative ladders. A member can be
/// allowed to CONNECT with an account but not REVEAL its password; Delinea's Secret View reveals.
/// Any single-level collapse either strands somebody or over-permissions them. So the folder role,
/// the secret role, the required role permissions and the withheld role permissions are decided
/// separately, and the residue (over-grants, unmapped, notes) is reported for a human.
/// </summary>
public static class CyberArkPermissionMapper
{
    // ---- canonical permission tokens, in display order -------------------------------------
    // This array IS the ordering contract: it fixes the order of Granted[] and of the reason
    // lists. The PowerShell original relied on an [ordered] hashtable for the same purpose.
    private static readonly string[] CanonicalOrder =
    [
        "list", "use", "retrieve", "add", "updateProps", "updateContent", "cpm", "nextContent",
        "rename", "delete", "unlock", "manageSafe", "backupSafe", "viewAudit", "viewMembers",
        "manageMembers", "approve1", "approve2", "bypass", "move", "createFolders", "deleteFolders",
    ];

    private static readonly Dictionary<string, string> Labels = new(StringComparer.Ordinal)
    {
        ["list"] = "List accounts",
        ["use"] = "Use Accounts",
        ["retrieve"] = "Retrieve accounts",
        ["add"] = "Add accounts",
        ["updateProps"] = "Update account properties",
        ["updateContent"] = "Update account content",
        ["cpm"] = "Initiate CPM account management operations",
        ["nextContent"] = "Specify next account content",
        ["rename"] = "Rename accounts",
        ["delete"] = "Delete accounts",
        ["unlock"] = "Unlock accounts",
        ["manageSafe"] = "Manage Safe",
        ["backupSafe"] = "Backup Safe",
        ["viewAudit"] = "View audit log",
        ["viewMembers"] = "View Safe members",
        ["manageMembers"] = "Manage Safe members",
        ["approve1"] = "Confirm requests (level 1)",
        ["approve2"] = "Confirm requests (level 2)",
        ["bypass"] = "Access Safe without confirmation",
        ["move"] = "Move accounts/folders",
        ["createFolders"] = "Create folders",
        ["deleteFolders"] = "Delete folders",
    };

    // ---- alias table -----------------------------------------------------------------------
    // Keys are ALREADY normalized (lowercased, non-alphanumerics stripped). Both the modern
    // Gen2 "accounts" spellings and the older file-era spellings map onto the same token, because
    // Self-Hosted reports and older API versions still emit the file-era names.
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.Ordinal)
    {
        ["listaccounts"] = "list",
        ["listfiles"] = "list",
        ["useaccounts"] = "use",
        ["usepasswords"] = "use",
        ["retrieveaccounts"] = "retrieve",
        ["retrievefiles"] = "retrieve",
        ["addaccounts"] = "add",
        ["createobjects"] = "add",
        ["updateaccountproperties"] = "updateProps",
        ["updateobjectproperties"] = "updateProps",
        ["updateaccountcontent"] = "updateContent",
        ["updateobjects"] = "updateContent",
        ["initiatecpmaccountmanagementoperations"] = "cpm",
        ["specifynextaccountcontent"] = "nextContent",
        ["renameaccounts"] = "rename",
        ["renameobjects"] = "rename",
        ["deleteaccounts"] = "delete",
        ["deleteobjects"] = "delete",
        ["unlockaccounts"] = "unlock",
        ["managesafe"] = "manageSafe",
        ["backupsafe"] = "backupSafe",
        ["viewauditlog"] = "viewAudit",
        ["viewsafemembers"] = "viewMembers",
        ["viewsafeowners"] = "viewMembers",
        ["managesafemembers"] = "manageMembers",
        ["managesafeowners"] = "manageMembers",
        ["requestsauthorizationlevel1"] = "approve1",
        ["requestsauthorizationlevel2"] = "approve2",
        ["accesswithoutconfirmation"] = "bypass",
        ["moveaccountsandfolders"] = "move",
        ["createfolders"] = "createFolders",
        ["deletefolders"] = "deleteFolders",
    };

    /// <summary>
    /// CyberArk built-in principals that must never be migrated. These are vault service
    /// identities, not customer users — creating Delinea grants for them is meaningless at best.
    /// </summary>
    private static readonly HashSet<string> BuiltInMembers = new(StringComparer.OrdinalIgnoreCase)
    {
        "Master", "Batch", "Backup Users", "DR Users", "Auditors", "Operators",
        "Notification Engines", "PVWAGWAccounts", "PasswordManager",
    };

    /// <summary>True when this safe member is a CyberArk built-in and should be skipped entirely.</summary>
    public static bool IsBuiltInMember(string? memberName) =>
        !string.IsNullOrWhiteSpace(memberName) && BuiltInMembers.Contains(memberName.Trim());

    /// <summary>
    /// Normalize a raw CyberArk permissions bag into the set of canonical tokens that are ON.
    ///
    /// Only a real boolean true counts. CyberArk emits JSON booleans here; a string "true" or a
    /// numeric 1 is not something the API produces, and treating it as ON would mean inventing a
    /// grant from malformed input. Unrecognized keys are dropped silently — CyberArk adds
    /// permission fields between versions and an unknown switch is not a reason to fail an import.
    /// </summary>
    public static IReadOnlySet<string> NormalizePermissions(IReadOnlyDictionary<string, object?>? permissions)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (permissions is null) return set;

        foreach (var kv in permissions)
        {
            if (kv.Value is not bool on || !on) continue;
            var key = StripNonAlphanumeric(kv.Key).ToLowerInvariant();
            if (Aliases.TryGetValue(key, out var canonical)) set.Add(canonical);
        }
        return set;
    }

    private static string StripNonAlphanumeric(string s)
    {
        var buf = new System.Text.StringBuilder(s.Length);
        foreach (var c in s)
            if (char.IsAsciiLetterOrDigit(c)) buf.Append(c);
        return buf.ToString();
    }

    /// <summary>Display label for a canonical token, or the token itself if unknown.</summary>
    public static string LabelFor(string canonicalToken) =>
        Labels.TryGetValue(canonicalToken, out var l) ? l : canonicalToken;

    /// <summary>
    /// Translate one safe membership. Total function: every input produces a mapping, and the
    /// only path that yields <see cref="CyberArkSecretRole.None"/> is a membership with no
    /// recognized permission at all. Callers must refuse to assign a <c>None</c> secret role
    /// rather than substituting a floor — see <c>CyberArkMigrationService</c>.
    /// </summary>
    public static CyberArkPermissionMapping Translate(IReadOnlyDictionary<string, object?>? permissions)
        => Translate(NormalizePermissions(permissions));

    /// <summary>Translate from an already-normalized token set.</summary>
    public static CyberArkPermissionMapping Translate(IReadOnlySet<string> p)
    {
        // Granted labels and the profile key, both in deterministic order.
        var grantedTokens = CanonicalOrder.Where(p.Contains).ToArray();
        var granted = grantedTokens.Select(LabelFor).ToArray();
        var profileKey = string.Join("+", grantedTokens.OrderBy(t => t, StringComparer.Ordinal));

        if (grantedTokens.Length == 0)
        {
            return new CyberArkPermissionMapping
            {
                FolderRole = CyberArkFolderRole.View,
                SecretRole = CyberArkSecretRole.None,
                Profile = "No effective access",
                ProfileKey = "",
                Granted = granted,
                Notes = ["No recognised CyberArk permissions on this membership. Nothing will be granted."],
            };
        }

        var folder = CyberArkFolderRole.View;
        var secret = CyberArkSecretRole.None;
        var roles = new List<string>();
        var withhold = new List<string>();
        var reasons = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var notes = new List<string>();
        var overGrants = new List<string>();
        var unmapped = new List<string>();
        var needsLauncherDecision = false;

        void AddRole(string roleName, string because)
        {
            if (!roles.Contains(roleName)) roles.Add(roleName);
            if (!reasons.TryGetValue(roleName, out var why))
            {
                why = [];
                reasons[roleName] = why;
            }
            if (!why.Contains(because)) why.Add(because);
        }

        // Ranks only ever increase. Nothing in this method downgrades an already-decided level.
        void RaiseFolder(CyberArkFolderRole r) { if (r > folder) folder = r; }
        void RaiseSecret(CyberArkSecretRole r) { if (r > secret) secret = r; }

        // ---- Block A: access -----------------------------------------------------------------
        if (p.Contains("list")) RaiseSecret(CyberArkSecretRole.List);

        if (p.Contains("retrieve"))
        {
            RaiseSecret(CyberArkSecretRole.View);
            notes.Add("Retrieve accounts becomes Secret View. Add the View Launcher Password role " +
                      "permission only where launcher passwords must be unmasked.");
        }

        if (p.Contains("use"))
        {
            AddRole("Secret Launch", LabelFor("use"));

            // THE load-bearing case. CyberArk let this member connect through PSM without ever
            // seeing the password. Delinea's Secret View would reveal it. Staying at Secret List
            // and explicitly withholding View Launcher Password preserves "connect but do not
            // reveal"; raising it is a decision a human must make per launcher.
            if (!p.Contains("retrieve"))
            {
                RaiseSecret(CyberArkSecretRole.List);
                withhold.Add("View Launcher Password");
                needsLauncherDecision = true;
                notes.Add("Use Accounts without Retrieve accounts: connect but do not reveal. Kept at " +
                          "Secret List with View Launcher Password withheld. If the launcher for this " +
                          "template requires Secret View, raise it deliberately and hide the password " +
                          "on the secret policy.");
            }
        }

        // ---- Block B: account management -----------------------------------------------------
        if (p.Contains("add"))
        {
            RaiseFolder(CyberArkFolderRole.AddSecret);
            AddRole("Add Secret", LabelFor("add"));
            notes.Add("Delinea Add Secret does not give access to the secret once added; the folder " +
                      "or secret grant must cover it separately.");
        }

        // Order here is the reason-list order for "Edit Secret". Kept literal, as in the original.
        string[] editish = ["updateProps", "updateContent", "rename", "cpm", "nextContent", "delete", "move"];
        var editGranted = editish.Where(p.Contains).ToArray();
        if (editGranted.Length > 0)
        {
            RaiseFolder(CyberArkFolderRole.Edit);
            RaiseSecret(CyberArkSecretRole.Edit);
            foreach (var e in editGranted) AddRole("Edit Secret", LabelFor(e));
        }

        if (p.Contains("updateProps") && !p.Contains("updateContent"))
            overGrants.Add("CyberArk allowed property changes but not content changes. Delinea Secret " +
                           "Edit covers both, so this membership gains the ability to change the credential.");

        if (p.Contains("cpm") && !p.Contains("updateContent"))
            overGrants.Add("CPM operations without content update. Delinea has no equivalent that permits " +
                           "rotation alone; Secret Edit plus RPC configuration also permits manual change.");

        if (p.Contains("cpm"))
            notes.Add("Initiate CPM operations depends on Remote Password Changing being enabled and " +
                      "configured for the template; it is not conferred by a folder permission.");

        if (p.Contains("nextContent"))
            overGrants.Add("Specify next account content has no granular Delinea equivalent. Secret Edit " +
                           "plus password-change configuration permits more than CyberArk did.");

        if (p.Contains("rename") && !p.Contains("updateContent"))
            overGrants.Add("Rename accounts is covered by Secret Edit in Delinea, which also permits " +
                           "content changes.");

        if (p.Contains("delete"))
        {
            AddRole("Deactivate Secret", LabelFor("delete"));
            notes.Add("Delete accounts maps to deactivation. Permanent removal additionally requires the " +
                      "Erase Secret role permission, which is deliberately not implied here.");
        }

        if (p.Contains("unlock"))
        {
            AddRole("Force Check In", LabelFor("unlock"));
            notes.Add("Unlock accounts maps to Force Check In, which only applies where Delinea " +
                      "check-out is in use.");
        }

        // ---- Block C: safe management --------------------------------------------------------
        if (p.Contains("manageSafe"))
        {
            // Manage Safe covers safe PROPERTIES, not the accounts inside. So it raises the folder
            // role but deliberately leaves the secret role where account permissions put it. That
            // faithfulness is what creates the secret-owner gap — see HasSecretOwnerGap.
            folder = CyberArkFolderRole.Owner;
            AddRole("Administer Folders", LabelFor("manageSafe"));
        }

        if (p.Contains("manageMembers"))
        {
            folder = CyberArkFolderRole.Owner;
            AddRole("Administer Folders", LabelFor("manageMembers"));
            AddRole("Own Secret", LabelFor("manageMembers"));
            RaiseSecret(CyberArkSecretRole.Owner);
        }

        if (p.Contains("backupSafe"))
        {
            AddRole("Administer Backup", LabelFor("backupSafe"));
            notes.Add("Backup Safe is a system-level Delinea role permission with no folder equivalent.");
        }

        if (p.Contains("viewAudit"))
        {
            AddRole("View Secret Audit", LabelFor("viewAudit"));
            RaiseSecret(CyberArkSecretRole.List);
            notes.Add("Reporting on the audit data may additionally require report role permissions.");
        }

        if (p.Contains("viewMembers") && !p.Contains("manageMembers"))
            unmapped.Add("View Safe members has no standalone Delinea equivalent. Delinea does not " +
                         "separate seeing folder sharing from administering it.");

        // ---- Block D: advanced ---------------------------------------------------------------
        if (p.Contains("move"))
        {
            RaiseSecret(CyberArkSecretRole.Owner);
            notes.Add("Move accounts requires Secret Owner where secrets inherit permissions, plus Edit " +
                      "or Owner on the destination folder. Moving folders also requires Administer Folders.");
        }

        if (p.Contains("createFolders") || p.Contains("deleteFolders"))
        {
            if (p.Contains("deleteFolders")) folder = CyberArkFolderRole.Owner;
            else RaiseFolder(CyberArkFolderRole.Edit);

            if (p.Contains("createFolders"))
            {
                AddRole("Administer Folders", LabelFor("createFolders"));
                notes.Add("Creating folders at the root of Delinea additionally requires the " +
                          "Create Root Folders role permission.");
            }
            if (p.Contains("deleteFolders")) AddRole("Administer Folders", LabelFor("deleteFolders"));
        }

        // ---- Block E: workflow (policy configuration, not an ACL) ----------------------------
        var isApprover = p.Contains("approve1") || p.Contains("approve2");
        var approvalLevel = p.Contains("approve2") ? 2 : p.Contains("approve1") ? 1 : 0;

        if (isApprover)
            unmapped.Add("Confirm requests is Delinea workflow approver configuration on the secret " +
                         "policy, not a folder permission. It is reported, not assigned here.");

        if (p.Contains("bypass"))
            unmapped.Add("Access Safe without confirmation is a workflow exemption. In Delinea it means " +
                         "this principal must be left out of the approval requirement on the policy.");

        // ---- Block F: floor ------------------------------------------------------------------
        // A principal who administers or approves for a folder still has to be able to see it.
        if (secret == CyberArkSecretRole.None &&
            (p.Contains("viewMembers") || isApprover || p.Contains("bypass")))
        {
            secret = CyberArkSecretRole.List;
            notes.Add("No account-level permission in CyberArk. Granted Secret List so the principal can " +
                      "see the folder they administer or approve for.");
        }
        if (secret == CyberArkSecretRole.None) secret = CyberArkSecretRole.List;

        // ---- Block G: access profile (first match wins; order IS the precedence) --------------
        var profile =
            p.Contains("manageSafe") || p.Contains("manageMembers") ? "Safe owner"
            : p.Contains("add") || p.Contains("updateContent") || p.Contains("updateProps")
              || p.Contains("cpm") || p.Contains("rename") || p.Contains("delete") ? "Account administrator"
            : p.Contains("retrieve") ? "Password viewer"
            : p.Contains("use") ? "PSM-only user"
            : p.Contains("viewAudit") ? "Auditor"
            : p.Contains("list") ? "List only"
            : "Workflow only";

        return new CyberArkPermissionMapping
        {
            FolderRole = folder,
            SecretRole = secret,
            RolePermissions = roles.OrderBy(r => r, StringComparer.Ordinal).ToArray(),
            RoleReasons = reasons.ToDictionary(
                kv => kv.Key,
                kv => (IReadOnlyList<string>)kv.Value.ToArray(),
                StringComparer.Ordinal),
            WithholdRolePermissions = withhold.Distinct(StringComparer.Ordinal).ToArray(),
            Workflow = new CyberArkWorkflow
            {
                IsApprover = isApprover,
                ApprovalLevel = approvalLevel,
                BypassesApproval = p.Contains("bypass"),
            },
            Profile = profile,
            ProfileKey = profileKey,
            Granted = granted,
            Notes = notes.ToArray(),
            OverGrants = overGrants.ToArray(),
            Unmapped = unmapped.ToArray(),
            NeedsLauncherDecision = needsLauncherDecision,
        };
    }

    // ---- API string forms ---------------------------------------------------------------------
    // Secret Server's folder-permissions endpoint takes role NAMES, not ids. These are the exact
    // strings it accepts; the enums exist so a typo cannot reach the wire.

    public static string ToApiString(this CyberArkFolderRole role) => role switch
    {
        CyberArkFolderRole.View => "View",
        CyberArkFolderRole.AddSecret => "Add Secret",
        CyberArkFolderRole.Edit => "Edit",
        CyberArkFolderRole.Owner => "Owner",
        _ => "View",
    };

    public static string ToApiString(this CyberArkSecretRole role) => role switch
    {
        CyberArkSecretRole.None => "None",
        CyberArkSecretRole.List => "List",
        CyberArkSecretRole.View => "View",
        CyberArkSecretRole.Edit => "Edit",
        CyberArkSecretRole.Owner => "Owner",
        _ => "List",
    };

    /// <summary>
    /// True when a safe's memberships collectively leave nobody as Secret Owner.
    ///
    /// CyberArk's Manage Safe covers safe properties rather than the accounts inside, so a safe
    /// whose only administrator holds Manage Safe (and not Manage Safe Members) maps to a folder
    /// with an Owner but no secret Owner. That is faithful to the source and still broken: once
    /// the migrating account's permission is removed, nobody can administer those secrets. The
    /// caller must refuse to remove the creator in that case, and surface the gap for a human.
    /// </summary>
    public static bool HasSecretOwnerGap(IEnumerable<CyberArkPermissionMapping> safeMemberships)
    {
        var any = false;
        foreach (var m in safeMemberships)
        {
            any = true;
            if (m.SecretRole == CyberArkSecretRole.Owner) return false;
        }
        return any; // a safe with no memberships at all is not a "gap", it is empty
    }

    /// <summary>
    /// The closest thing to an owner in a safe that has no Secret Owner, or null if there are no
    /// memberships. Ranked by: is a Safe owner profile, then folder role, then secret role.
    /// This is a SUGGESTION for a human — promotion is never automatic, because inventing an owner
    /// grants access the source never gave.
    /// </summary>
    public static CyberArkPermissionMapping? SuggestSecretOwner(
        IEnumerable<CyberArkPermissionMapping> safeMemberships) =>
        safeMemberships
            .OrderByDescending(m => m.Profile == "Safe owner")
            .ThenByDescending(m => (int)m.FolderRole)
            .ThenByDescending(m => (int)m.SecretRole)
            .FirstOrDefault();
}
