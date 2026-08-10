namespace PasMigration.Connectors;

/// <summary>
/// The Secret Server template a CyberArk platform should become, plus the fields that template
/// is expected to carry.
/// </summary>
/// <param name="TemplateName">Primary template name to resolve on the target tenant.</param>
/// <param name="FallbackName">
/// Second name to try if the primary is absent. Tenants rename stock templates; a single name is
/// a single point of failure.
/// </param>
/// <param name="Fields">
/// Field names this platform is expected to populate. Used to warn, before a run, that a value
/// has nowhere to land — not to build the request body (that comes from the live template stub).
/// </param>
public sealed record CyberArkTemplateTarget(
    string TemplateName,
    string FallbackName,
    IReadOnlyList<string> Fields);

/// <summary>
/// Maps a CyberArk platformId onto a Secret Server secret template.
///
/// Ported from $DefaultTemplateMappings / Get-TemplateMappingForPlatform in the EMEA Professional
/// Services toolkit, with two deliberate changes:
///
/// 1. NO HARDCODED TEMPLATE IDS. The PowerShell original carried numeric ids (6003, 6028, ...).
///    Those are the ids on a stock tenant and they differ between tenants, so using them would
///    silently write secrets against the wrong template on a customer's estate. This port resolves
///    by NAME against the live tenant, exactly as the existing PAS account path already does
///    (<c>ISecretServerClient.FindTemplateAsync</c>), with a fallback name per platform.
///
/// 2. DETERMINISTIC RESOLUTION. The original iterated PowerShell hashtables, whose key order is
///    unspecified. Stages 3 and 4 returned whichever key the runtime happened to enumerate first,
///    so a platform id containing both "SSH" and "Cisco" — or both "SAP" and "SapHana" — could
///    resolve differently between two runs over the same data. Here both stages iterate in
///    longest-key-first order, which is stable and picks the most specific match.
///
/// Pure and total: every platform id resolves to something, falling back to "Password".
/// </summary>
public static class CyberArkTemplateMap
{
    private static readonly string[] UserPassMachine = ["Username", "Password", "Machine"];
    private static readonly string[] UserPassMachineDomain = ["Username", "Password", "Machine", "Domain"];
    private static readonly string[] UserPassMachineDb = ["Username", "Password", "Machine", "Database"];

    private static readonly CyberArkTemplateTarget ActiveDirectory =
        new("Active Directory Account", "Active Directory", UserPassMachineDomain);
    private static readonly CyberArkTemplateTarget WindowsAccount =
        new("Windows Account", "Password", UserPassMachine);
    private static readonly CyberArkTemplateTarget UnixSsh =
        new("Unix Account (SSH)", "Unix Account", UserPassMachine);
    private static readonly CyberArkTemplateTarget UnixSshKey =
        new("Unix Account (SSH Key Rotation)", "Unix Account (SSH)",
            ["Username", "Private Key", "Machine", "Passphrase"]);
    private static readonly CyberArkTemplateTarget SqlServer =
        new("SQL Server Account", "Password", UserPassMachineDb);
    private static readonly CyberArkTemplateTarget OracleAccount =
        new("Oracle Account", "Password", UserPassMachineDb);
    private static readonly CyberArkTemplateTarget MySqlAccount =
        new("MySql Account", "Password", UserPassMachineDb);
    private static readonly CyberArkTemplateTarget SybaseAccount =
        new("Sybase Account", "Password", UserPassMachineDb);
    private static readonly CyberArkTemplateTarget CiscoSsh =
        new("Cisco Account (SSH)", "Unix Account (SSH)",
            ["Username", "Password", "Machine", "EnablePassword"]);
    private static readonly CyberArkTemplateTarget CiscoTelnet =
        new("Cisco Account (Telnet)", "Cisco Account (SSH)",
            ["Username", "Password", "Machine", "EnablePassword"]);
    private static readonly CyberArkTemplateTarget AmazonIam =
        new("Amazon IAM Key", "Password", ["Username", "Password", "Machine", "AccessKeyId"]);
    private static readonly CyberArkTemplateTarget AzureAd =
        new("Azure AD Account", "Password", ["Username", "Password", "Machine", "TenantId"]);
    private static readonly CyberArkTemplateTarget IbmISeries =
        new("IBM iSeries Mainframe", "Password", UserPassMachine);
    private static readonly CyberArkTemplateTarget OpenLdap =
        new("OpenLDAP Account", "Password", UserPassMachine);
    private static readonly CyberArkTemplateTarget SapAccount =
        new("SAP Account", "Password", ["Username", "Password", "Machine", "ClientNumber"]);
    private static readonly CyberArkTemplateTarget HpIlo =
        new("HP iLO Account (SSH)", "Unix Account (SSH)", UserPassMachine);
    private static readonly CyberArkTemplateTarget WebPassword =
        new("Web Password", "Password", ["Username", "Password", "URL"]);

    /// <summary>
    /// Fallback for any platform that resolves to nothing more specific. "Password" is the one
    /// template every Secret Server tenant has.
    /// </summary>
    public static readonly CyberArkTemplateTarget Default =
        new("Password", "Password", ["Username", "Password"]);

    // Platform id -> target. Case-insensitive, matching CyberArk's own behaviour.
    private static readonly Dictionary<string, CyberArkTemplateTarget> Map =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // Windows
            ["WinDomain"] = ActiveDirectory,
            ["WindowsDomainAccount"] = ActiveDirectory,
            ["WinDomainPS"] = ActiveDirectory,
            ["WindowsService"] = ActiveDirectory,
            ["WindowsScheduledTask"] = ActiveDirectory,
            ["WinDomainComDomain"] = ActiveDirectory,
            ["WinServerLocal"] = WindowsAccount,
            ["WinDesktopLocal"] = WindowsAccount,
            ["WindowsServerLocal"] = WindowsAccount,
            ["WindowsDesktopLocal"] = WindowsAccount,
            ["WinLocalWMI"] = WindowsAccount,

            // Unix / Linux
            ["UnixSSH"] = UnixSsh,
            ["UnixViaSSH"] = UnixSsh,
            ["UnixViaTelnet"] = UnixSsh,
            ["LinuxSSH"] = UnixSsh,
            ["UnixSSHKeys"] = UnixSshKey,
            ["LinuxSSHKey"] = UnixSshKey,

            // Database
            ["MSSql"] = SqlServer,
            ["Oracle"] = OracleAccount,
            ["OracleDB"] = OracleAccount,
            ["MySql"] = MySqlAccount,
            ["Sybase"] = SybaseAccount,
            ["SapHana"] = Default,
            ["PostgreSQL"] = Default,
            ["DB2ViaSSH"] = UnixSsh,
            ["InformixViaSSH"] = UnixSsh,

            // Network devices
            ["CiscoSSH"] = CiscoSsh,
            ["CiscoEnable"] = CiscoSsh,
            ["CiscoSwitch"] = CiscoSsh,
            ["CiscoSSHKeys"] = CiscoSsh,
            ["CiscoTelnet"] = CiscoTelnet,
            ["JuniperJUNOS"] = UnixSsh,
            ["JuniperScreenOS"] = UnixSsh,
            ["NetScreen"] = UnixSsh,
            ["PaloAlto"] = UnixSsh,
            ["F5BigIP"] = UnixSsh,
            ["F5-BigIP"] = UnixSsh,
            ["CheckPointGAIA"] = UnixSsh,
            ["Fortinet"] = UnixSsh,
            ["FortiGate"] = UnixSsh,
            ["SonicWallNSA"] = Default,

            // Cloud
            ["AWSAccessKeys"] = AmazonIam,
            ["AWS"] = AmazonIam,
            ["AWSKeyManagement"] = AmazonIam,
            ["AWSAccount"] = AmazonIam,
            ["AzurePasswordMgmt"] = AzureAd,
            ["AzureADPassword"] = AzureAd,
            ["AzureServicesAccount"] = AzureAd,
            ["AzureManagement"] = AzureAd,
            ["GoogleCloudPlatform"] = Default,
            ["OpenShift"] = Default,

            // Mainframe / midrange
            ["iSeriesAS400"] = IbmISeries,
            ["IBMiSeries"] = IbmISeries,
            ["AS400"] = Default,
            ["OS390"] = Default,
            ["OS390LDAP"] = Default,
            ["OS390ViaSSH"] = Default,
            ["MainframeRACF"] = Default,
            ["MainframeTopSecret"] = Default,
            ["MainframeACF2"] = Default,
            ["zOS"] = Default,

            // Directory
            ["LDAP"] = OpenLdap,
            ["OpenLDAP"] = OpenLdap,
            ["NovellDirectory"] = OpenLdap,
            ["SunOne"] = OpenLdap,
            ["SunOneDirectory"] = OpenLdap,

            // SAP
            ["SAP"] = SapAccount,
            ["SAPNetWeaver"] = SapAccount,

            // VMware
            ["VMwareESXi"] = Default,
            ["VMware"] = Default,
            ["VMwareVCenter"] = Default,
            ["VMwareESXAPI"] = Default,

            // Hardware / out-of-band
            ["DellDRAC"] = Default,
            ["HPILO"] = HpIlo,

            // Web / application
            ["GenericWebApp"] = WebPassword,
            ["WebApplication"] = WebPassword,

            // CyberArk internal
            ["CyberArkVault"] = ActiveDirectory,
            ["CyberArk"] = ActiveDirectory,
        };

    // Keyword fallbacks, tried last. Ordered longest-first at construction so that, for example,
    // "Windows" is considered before "Win" and "FortiGate" before "F5".
    private static readonly (string Keyword, CyberArkTemplateTarget Target)[] Keywords =
        new (string, CyberArkTemplateTarget)[]
        {
            ("Windows", ActiveDirectory),
            ("Win", ActiveDirectory),
            ("Unix", UnixSsh),
            ("Linux", UnixSsh),
            ("SSH", UnixSsh),
            ("Cisco", CiscoSsh),
            ("Oracle", OracleAccount),
            ("MySQL", MySqlAccount),
            ("MSSQL", SqlServer),
            ("SQL", SqlServer),
            ("Sybase", SybaseAccount),
            ("SAP", SapAccount),
            ("AWS", AmazonIam),
            ("Azure", AzureAd),
            ("LDAP", OpenLdap),
            ("F5", UnixSsh),
            ("Juniper", UnixSsh),
            ("Palo", UnixSsh),
            ("Check", UnixSsh),
            ("FortiGate", UnixSsh),
            ("Fortinet", UnixSsh),
            ("VMware", Default),
            ("ESXi", Default),
            ("iLO", HpIlo),
            ("DRAC", Default),
            ("iSeries", IbmISeries),
            ("AS400", Default),
            ("Mainframe", Default),
            ("RACF", Default),
            ("TopSecret", Default),
            ("ACF2", Default),
        }
        .OrderByDescending(k => k.Item1.Length)
        .ThenBy(k => k.Item1, StringComparer.Ordinal)
        .ToArray();

    private static readonly char[] SuffixSeparators = new[] { '-', '_', ' ', '\t' };

    // Exact-map keys, longest first, for the prefix stage. Computed once.
    private static readonly string[] KeysLongestFirst =
        Map.Keys.OrderByDescending(k => k.Length).ThenBy(k => k, StringComparer.Ordinal).ToArray();

    /// <summary>
    /// Resolve a CyberArk platformId to a target template. Four stages, most specific first:
    ///   1. exact match
    ///   2. match after stripping a "-suffix", "_suffix" or " suffix" (custom platforms are
    ///      routinely cloned as "WinDomain-Corp" / "UnixSSH_Custom")
    ///   3. longest matching key that the id starts with
    ///   4. longest matching keyword contained anywhere in the id
    /// Falls back to <see cref="Default"/>. Never throws; a null/blank id yields the default.
    /// </summary>
    public static CyberArkTemplateTarget Resolve(string? platformId)
    {
        if (string.IsNullOrWhiteSpace(platformId)) return Default;
        var id = platformId.Trim();

        // 1. exact
        if (Map.TryGetValue(id, out var exact)) return exact;

        // 2. strip the first '-', '_' or whitespace and everything after it.
        // Explicit array rather than a collection expression: string.IndexOfAny has both
        // char[] and span overloads, and an untyped [...] can be ambiguous between them.
        var cut = id.IndexOfAny(SuffixSeparators);
        if (cut > 0 && Map.TryGetValue(id[..cut], out var stripped)) return stripped;

        // 3. longest prefix
        foreach (var key in KeysLongestFirst)
            if (id.StartsWith(key, StringComparison.OrdinalIgnoreCase)) return Map[key];

        // 4. longest contained keyword
        foreach (var (keyword, target) in Keywords)
            if (id.Contains(keyword, StringComparison.OrdinalIgnoreCase)) return target;

        return Default;
    }

    /// <summary>
    /// True when this platform stores an SSH key rather than a password. Drives which field the
    /// credential lands in, and which "no credential" warning the operator sees.
    /// </summary>
    public static bool IsKeyPlatform(string? platformId, string? secretType)
    {
        if (string.Equals(secretType, "key", StringComparison.OrdinalIgnoreCase)) return true;
        var target = Resolve(platformId);
        return target.Fields.Contains("Private Key");
    }

    /// <summary>Every distinct template name this map can produce, for pre-flight tenant checks.</summary>
    public static IReadOnlyList<string> AllTemplateNames() =>
        Map.Values.Append(Default)
            .SelectMany(t => new[] { t.TemplateName, t.FallbackName })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();
}
