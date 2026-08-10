using PasMigration.Connectors;
using Xunit;

namespace PasMigration.UnitTests;

/// <summary>
/// Tier 1 regression tests for the CyberArk safe-permission translation.
///
/// This is the one piece of the CyberArk path where a quiet mistake becomes a security incident:
/// every rule here either withholds access somebody should not have, or refuses to invent access
/// the source never granted. Pure in-memory — no database, no network.
/// </summary>
public class CyberArkPermissionMapperTests
{
    private static Dictionary<string, object?> Perms(params string[] on)
    {
        var d = new Dictionary<string, object?>();
        foreach (var k in on) d[k] = true;
        return d;
    }

    // ── normalization ────────────────────────────────────────────────────────────────

    [Fact]
    public void Only_real_boolean_true_counts_as_granted()
    {
        // CyberArk emits JSON booleans. A string or a number here means malformed input, and
        // inventing a grant from malformed input is exactly the failure mode to avoid.
        var raw = new Dictionary<string, object?>
        {
            ["retrieveAccounts"] = "true",
            ["listAccounts"] = 1,
            ["useAccounts"] = false,
            ["addAccounts"] = true,
        };

        var set = CyberArkPermissionMapper.NormalizePermissions(raw);

        Assert.Equal(["add"], set);
    }

    [Fact]
    public void File_era_permission_names_map_onto_the_same_tokens()
    {
        // Older Self-Hosted reports and API versions use the file-era spellings. Both must land
        // on the same canonical token or an on-prem vault translates to nothing.
        var modern = CyberArkPermissionMapper.NormalizePermissions(
            Perms("listAccounts", "retrieveAccounts", "updateAccountContent", "manageSafeMembers"));
        var legacy = CyberArkPermissionMapper.NormalizePermissions(
            Perms("ListFiles", "RetrieveFiles", "UpdateObjects", "ManageSafeOwners"));

        Assert.Equal(modern.OrderBy(x => x), legacy.OrderBy(x => x));
    }

    [Fact]
    public void Unknown_permission_keys_are_dropped_not_fatal()
    {
        var set = CyberArkPermissionMapper.NormalizePermissions(
            Perms("listAccounts", "someFuturePermissionCyberArkAdded"));

        Assert.Equal(["list"], set);
    }

    // ── the load-bearing case: connect but do not reveal ──────────────────────────────

    [Fact]
    public void Use_without_retrieve_stays_at_list_and_withholds_launcher_password()
    {
        // CyberArk let this member connect through PSM without ever seeing the password.
        // Delinea Secret View would reveal it, so the mapping must NOT raise to View.
        var m = CyberArkPermissionMapper.Translate(Perms("listAccounts", "useAccounts"));

        Assert.Equal(CyberArkSecretRole.List, m.SecretRole);
        Assert.Equal(CyberArkFolderRole.View, m.FolderRole);
        Assert.Contains("Secret Launch", m.RolePermissions);
        Assert.Contains("View Launcher Password", m.WithholdRolePermissions);
        Assert.True(m.NeedsLauncherDecision);
        Assert.Equal("PSM-only user", m.Profile);
    }

    [Fact]
    public void Use_with_retrieve_does_reveal_and_withholds_nothing()
    {
        var m = CyberArkPermissionMapper.Translate(Perms("listAccounts", "useAccounts", "retrieveAccounts"));

        Assert.Equal(CyberArkSecretRole.View, m.SecretRole);
        Assert.Contains("Secret Launch", m.RolePermissions);
        Assert.Empty(m.WithholdRolePermissions);
        Assert.False(m.NeedsLauncherDecision);
        Assert.Equal("Password viewer", m.Profile);
    }

    // ── the secret-owner gap ──────────────────────────────────────────────────────────

    [Fact]
    public void ManageSafe_raises_folder_owner_but_not_secret_owner()
    {
        // CyberArk's Manage Safe covers safe PROPERTIES, not the accounts inside. Faithfully
        // reproducing that is what creates the gap the migration has to refuse to paper over.
        var m = CyberArkPermissionMapper.Translate(Perms("manageSafe"));

        Assert.Equal(CyberArkFolderRole.Owner, m.FolderRole);
        Assert.NotEqual(CyberArkSecretRole.Owner, m.SecretRole);
        Assert.Contains("Administer Folders", m.RolePermissions);
    }

    [Fact]
    public void ManageSafeMembers_raises_both_to_owner()
    {
        var m = CyberArkPermissionMapper.Translate(Perms("manageSafeMembers"));

        Assert.Equal(CyberArkFolderRole.Owner, m.FolderRole);
        Assert.Equal(CyberArkSecretRole.Owner, m.SecretRole);
        Assert.Contains("Own Secret", m.RolePermissions);
    }

    [Fact]
    public void Safe_with_only_manageSafe_admins_is_flagged_as_a_secret_owner_gap()
    {
        var memberships = new[]
        {
            CyberArkPermissionMapper.Translate(Perms("manageSafe")),
            CyberArkPermissionMapper.Translate(Perms("listAccounts", "retrieveAccounts")),
        };

        Assert.True(CyberArkPermissionMapper.HasSecretOwnerGap(memberships));
        Assert.Equal("Safe owner", CyberArkPermissionMapper.SuggestSecretOwner(memberships)!.Profile);
    }

    [Fact]
    public void Safe_with_a_secret_owner_is_not_a_gap()
    {
        var memberships = new[]
        {
            CyberArkPermissionMapper.Translate(Perms("manageSafeMembers")),
            CyberArkPermissionMapper.Translate(Perms("listAccounts")),
        };

        Assert.False(CyberArkPermissionMapper.HasSecretOwnerGap(memberships));
    }

    // ── refusing to invent access ─────────────────────────────────────────────────────

    [Fact]
    public void Membership_with_no_recognised_permission_yields_secret_role_none()
    {
        // None is the signal to the migration service to assign nothing at all. Every other
        // path floors at List; this one must not, or the tool grants access CyberArk never did.
        var m = CyberArkPermissionMapper.Translate(new Dictionary<string, object?>());

        Assert.Equal(CyberArkSecretRole.None, m.SecretRole);
        Assert.Equal("No effective access", m.Profile);
        Assert.Empty(m.RolePermissions);
    }

    [Fact]
    public void Any_recognised_permission_floors_the_secret_role_at_list()
    {
        // An approver with no account permission still has to see the folder they approve for.
        var m = CyberArkPermissionMapper.Translate(Perms("requestsAuthorizationLevel1"));

        Assert.Equal(CyberArkSecretRole.List, m.SecretRole);
        Assert.True(m.Workflow.IsApprover);
        Assert.Equal(1, m.Workflow.ApprovalLevel);
    }

    // ── over-grant reporting ──────────────────────────────────────────────────────────

    [Fact]
    public void Property_edit_without_content_edit_is_reported_as_an_over_grant()
    {
        // Delinea Secret Edit covers both, so this membership gains the ability to change the
        // credential. The tool cannot prevent it, so it must say so.
        var m = CyberArkPermissionMapper.Translate(Perms("updateAccountProperties"));

        Assert.Equal(CyberArkSecretRole.Edit, m.SecretRole);
        Assert.NotEmpty(m.OverGrants);
        Assert.Contains(m.OverGrants, o => o.Contains("property changes"));
    }

    [Fact]
    public void Cpm_without_content_edit_is_reported_as_an_over_grant()
    {
        var m = CyberArkPermissionMapper.Translate(Perms("initiateCPMAccountManagementOperations"));

        Assert.Contains(m.OverGrants, o => o.Contains("CPM operations"));
        Assert.Contains("Edit Secret", m.RolePermissions);
    }

    [Fact]
    public void Delete_maps_to_deactivate_and_does_not_imply_erase()
    {
        var m = CyberArkPermissionMapper.Translate(Perms("deleteAccounts"));

        Assert.Contains("Deactivate Secret", m.RolePermissions);
        Assert.DoesNotContain("Erase Secret", m.RolePermissions);
    }

    // ── determinism ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Profile_key_is_order_independent()
    {
        // Two members with the same permissions in a different property order must cluster
        // together. The PowerShell original relied on unordered hashtable enumeration here.
        var a = CyberArkPermissionMapper.Translate(Perms("listAccounts", "retrieveAccounts", "useAccounts"));
        var b = CyberArkPermissionMapper.Translate(Perms("useAccounts", "listAccounts", "retrieveAccounts"));

        Assert.Equal(a.ProfileKey, b.ProfileKey);
        Assert.Equal(a.Granted, b.Granted);
        Assert.Equal(a.RolePermissions, b.RolePermissions);
    }

    // ── built-ins ─────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Master")]
    [InlineData("Batch")]
    [InlineData("PasswordManager")]
    [InlineData("Notification Engines")]
    [InlineData("pvwagwaccounts")]   // case-insensitive
    public void CyberArk_builtin_principals_are_recognised(string name) =>
        Assert.True(CyberArkPermissionMapper.IsBuiltInMember(name));

    [Theory]
    [InlineData("CORP\\svc-sql")]
    [InlineData("alice@corp.local")]
    [InlineData("")]
    [InlineData(null)]
    public void Real_principals_are_not_treated_as_builtins(string? name) =>
        Assert.False(CyberArkPermissionMapper.IsBuiltInMember(name));

    // ── API role strings ──────────────────────────────────────────────────────────────

    [Fact]
    public void Role_enums_render_the_exact_strings_secret_server_accepts()
    {
        Assert.Equal("View", CyberArkFolderRole.View.ToApiString());
        Assert.Equal("Add Secret", CyberArkFolderRole.AddSecret.ToApiString());
        Assert.Equal("Edit", CyberArkFolderRole.Edit.ToApiString());
        Assert.Equal("Owner", CyberArkFolderRole.Owner.ToApiString());

        Assert.Equal("None", CyberArkSecretRole.None.ToApiString());
        Assert.Equal("List", CyberArkSecretRole.List.ToApiString());
        Assert.Equal("View", CyberArkSecretRole.View.ToApiString());
        Assert.Equal("Edit", CyberArkSecretRole.Edit.ToApiString());
        Assert.Equal("Owner", CyberArkSecretRole.Owner.ToApiString());
    }

    // ── ranks never downgrade ─────────────────────────────────────────────────────────

    [Fact]
    public void A_broad_membership_lands_on_the_highest_level_each_permission_implies()
    {
        var m = CyberArkPermissionMapper.Translate(Perms(
            "listAccounts", "retrieveAccounts", "useAccounts", "addAccounts",
            "updateAccountContent", "deleteAccounts", "manageSafeMembers"));

        Assert.Equal(CyberArkFolderRole.Owner, m.FolderRole);
        Assert.Equal(CyberArkSecretRole.Owner, m.SecretRole);
        Assert.Equal("Safe owner", m.Profile);
        // Owner does not silently confer the launcher: Use came with Retrieve here, so nothing
        // is withheld, but a bare Owner is still not given Erase.
        Assert.DoesNotContain("Erase Secret", m.RolePermissions);
    }
}
