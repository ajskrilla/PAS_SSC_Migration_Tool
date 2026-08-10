using PasMigration.Connectors;
using Xunit;

namespace PasMigration.UnitTests;

/// <summary>
/// Tier 1 tests for CyberArk platform -> Secret Server template resolution.
///
/// The determinism tests are the point of this file. The PowerShell original iterated unordered
/// hashtables in its prefix and keyword stages, so a platform id matching more than one key
/// resolved to whichever the runtime enumerated first — meaning two runs over the same vault
/// could write secrets against different templates. These tests pin the resolution order.
/// </summary>
public class CyberArkTemplateMapTests
{
    [Theory]
    [InlineData("WinDomain", "Active Directory Account")]
    [InlineData("WinServerLocal", "Windows Account")]
    [InlineData("UnixSSH", "Unix Account (SSH)")]
    [InlineData("UnixSSHKeys", "Unix Account (SSH Key Rotation)")]
    [InlineData("MSSql", "SQL Server Account")]
    [InlineData("Oracle", "Oracle Account")]
    [InlineData("CiscoSSH", "Cisco Account (SSH)")]
    [InlineData("CiscoTelnet", "Cisco Account (Telnet)")]
    [InlineData("AWSAccessKeys", "Amazon IAM Key")]
    [InlineData("AzurePasswordMgmt", "Azure AD Account")]
    [InlineData("LDAP", "OpenLDAP Account")]
    [InlineData("SAP", "SAP Account")]
    [InlineData("HPILO", "HP iLO Account (SSH)")]
    [InlineData("GenericWebApp", "Web Password")]
    [InlineData("iSeriesAS400", "IBM iSeries Mainframe")]
    public void Exact_platform_ids_resolve_to_their_template(string platformId, string expected) =>
        Assert.Equal(expected, CyberArkTemplateMap.Resolve(platformId).TemplateName);

    [Fact]
    public void Platform_id_matching_is_case_insensitive() =>
        Assert.Equal("Active Directory Account", CyberArkTemplateMap.Resolve("windomain").TemplateName);

    [Theory]
    [InlineData("WinDomain-Corp")]
    [InlineData("WinDomain_Custom")]
    [InlineData("WinDomain Prod")]
    public void Cloned_platforms_resolve_via_the_stripped_suffix(string platformId) =>
        Assert.Equal("Active Directory Account", CyberArkTemplateMap.Resolve(platformId).TemplateName);

    [Fact]
    public void Longest_prefix_wins_over_a_shorter_one()
    {
        // "UnixSSHKeysCustom" must resolve to the key-rotation template, not to plain UnixSSH.
        // With unordered enumeration this was a coin flip.
        Assert.Equal("Unix Account (SSH Key Rotation)",
            CyberArkTemplateMap.Resolve("UnixSSHKeysCustom").TemplateName);
    }

    [Fact]
    public void Longest_keyword_wins_over_a_shorter_one()
    {
        // Contains both "Windows" and "Win". Longest-first ordering makes this stable.
        var a = CyberArkTemplateMap.Resolve("CustomerWindowsThing");
        var b = CyberArkTemplateMap.Resolve("CustomerWindowsThing");
        Assert.Equal(a.TemplateName, b.TemplateName);
        Assert.Equal("Active Directory Account", a.TemplateName);
    }

    [Fact]
    public void Resolution_is_stable_across_repeated_calls()
    {
        // The original's failure mode: same input, different answer between runs. Ambiguous ids
        // that contain several keywords are the ones that exposed it.
        foreach (var id in new[] { "AcmeSSHCiscoBox", "LegacySapHanaCluster", "SQLUnixBridge" })
        {
            var first = CyberArkTemplateMap.Resolve(id).TemplateName;
            for (var i = 0; i < 25; i++)
                Assert.Equal(first, CyberArkTemplateMap.Resolve(id).TemplateName);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("SomethingNobodyHasEverSeen")]
    public void Unknown_or_missing_platforms_fall_back_to_password(string? platformId)
    {
        var t = CyberArkTemplateMap.Resolve(platformId);
        Assert.Equal("Password", t.TemplateName);
    }

    [Fact]
    public void Every_target_declares_a_fallback_that_is_a_real_template_name()
    {
        // FindTemplateAsync tries TemplateName then FallbackName. A fallback that is itself
        // exotic gives no protection on a tenant with renamed stock templates.
        var names = CyberArkTemplateMap.AllTemplateNames();
        Assert.Contains("Password", names);
        Assert.Contains("Active Directory Account", names);
        Assert.Contains("Unix Account (SSH)", names);
    }

    [Fact]
    public void Ssh_key_platforms_are_recognised_as_key_platforms()
    {
        Assert.True(CyberArkTemplateMap.IsKeyPlatform("UnixSSHKeys", "password"));
        Assert.True(CyberArkTemplateMap.IsKeyPlatform("LinuxSSHKey", "password"));
    }

    [Fact]
    public void Secret_type_key_overrides_the_platform_mapping()
    {
        // CyberArk's own secretType is authoritative: an account explicitly flagged as a key
        // must not have its material written into a Password field, whatever the platform says.
        Assert.True(CyberArkTemplateMap.IsKeyPlatform("WinDomain", "key"));
        Assert.False(CyberArkTemplateMap.IsKeyPlatform("WinDomain", "password"));
    }

    [Fact]
    public void No_mapping_carries_a_hardcoded_template_id()
    {
        // Guard against reintroducing the PowerShell original's numeric ids. Template ids differ
        // between Secret Server tenants, so resolution must stay name-based.
        var t = CyberArkTemplateMap.Resolve("WinDomain");
        Assert.False(string.IsNullOrWhiteSpace(t.TemplateName));
        Assert.False(string.IsNullOrWhiteSpace(t.FallbackName));
    }
}
