using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace PasMigration.Connectors;

/// <summary>
/// CyberArk PVWA connector. Raw REST against the Gen2 API, covering both Self-Hosted/on-prem
/// (session-token logon) and Privilege Cloud (OAuth against CyberArk Identity).
///
/// Ported from the EMEA Professional Services toolkit
/// (Export-CyberArkForMigration.ps1 / Migrate-CyberArkToDelinea.ps1). The API quirks encoded here
/// were expensive to discover and each one is commented at its site.
///
/// Security: the only method that returns secret material is
/// <see cref="RetrieveAccountPasswordAsync"/>; that value stays in memory and is never logged.
/// Session tokens are never logged. Inventory reads no credential values at all.
/// </summary>
public sealed class CyberArkConnector : ICyberArkClient
{
    private readonly HttpClient _http;

    /// <summary>PVWA base INCLUDING the virtual directory, e.g. https://host/PasswordVault.</summary>
    private readonly string _pvwaBaseUrl;

    /// <summary>
    /// The exact string to put in the Authorization header. For on-prem logons this is the raw
    /// session token with NO scheme prefix; for OAuth it is "Bearer &lt;token&gt;". See
    /// <see cref="AuthenticateAsync"/> for why.
    /// </summary>
    private string? _authorizationHeader;

    /// <summary>Kept so a 401 mid-inventory can transparently re-authenticate rather than
    /// truncating a multi-hour extract.</summary>
    private CyberArkCredentials? _creds;

    private static readonly JsonSerializerOptions JsonOpts = new();

    /// <summary>
    /// CyberArk system safes. These hold vault internals, PSM recordings and CPM working data —
    /// never customer credentials — so they are excluded from inventory rather than shown and
    /// then manually deselected on every engagement.
    /// </summary>
    private static readonly HashSet<string> SystemSafes = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "VaultInternal", "Notification Engine", "PVWAConfig", "PVWAReports",
        "PVWATicketingSystem", "PVWAPublicData", "PVWAUserPrefs", "SharedAuth_Internal",
        "PasswordManager", "PasswordManagerShared", "PasswordManager_Pending",
        "PasswordManager_workspace", "PasswordManager_ADInternal", "PasswordManager_Info",
        "AccountsFeedADAccounts", "AccountsFeedDiscovery", "PSM", "PSMSessions",
        "PSMUniversalConnectors", "PSMLiveSessions", "PSMRecordings",
        "PSMUnmanagedSessionAccounts", "AppProviderConf", "xRay",
    };

    private const int PageSize = 500;

    /// <summary>Refuse to page forever if a PVWA keeps handing back a nextLink.</summary>
    private const int MaxPages = 1000;

    public CyberArkConnector(HttpClient http, string pvwaBaseUrl)
    {
        _http = http;
        _pvwaBaseUrl = pvwaBaseUrl.TrimEnd('/');
    }

    /// <summary>True when this safe should be skipped as a CyberArk internal safe.</summary>
    public static bool IsSystemSafe(string? safeName) =>
        !string.IsNullOrWhiteSpace(safeName) && SystemSafes.Contains(safeName.Trim());

    // ---- authentication --------------------------------------------------------------------

    /// <summary>
    /// Authenticate. Two shapes, and the difference matters:
    ///
    /// On-prem PVWA logon returns a bare session token as a JSON string — literally
    /// <c>"abc123..."</c>, quotes included — and that token goes into the Authorization header
    /// VERBATIM, with no "Bearer " prefix. Adding the prefix produces a 401 that looks exactly
    /// like bad credentials.
    ///
    /// Privilege Cloud OAuth returns a normal OAuth envelope and DOES use "Bearer".
    /// </summary>
    public async Task AuthenticateAsync(CyberArkCredentials creds, CancellationToken ct = default)
    {
        _creds = creds;

        if (creds.AuthMethod == CyberArkAuthMethod.OAuth)
        {
            await AuthenticateOAuthAsync(creds, ct);
            return;
        }

        await AuthenticateSessionAsync(creds, ct);
    }

    private async Task AuthenticateOAuthAsync(CyberArkCredentials creds, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(creds.IdentityTokenUrl))
            throw new InvalidOperationException(
                "CyberArk Identity token URL is required for OAuth (Privilege Cloud) authentication.");
        if (string.IsNullOrWhiteSpace(creds.ClientId) || string.IsNullOrWhiteSpace(creds.ClientSecret))
            throw new InvalidOperationException(
                "Client id and client secret are required for OAuth (Privilege Cloud) authentication.");

        var req = new HttpRequestMessage(HttpMethod.Post, creds.IdentityTokenUrl)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = creds.ClientId!,
                ["client_secret"] = creds.ClientSecret!,
            }),
        };

        using var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new CyberArkApiException((int)resp.StatusCode, "POST", "oauth2 token",
                $"CyberArk Identity token request failed ({(int)resp.StatusCode}): {Trunc(body)}");

        using var doc = JsonDocument.Parse(body);
        var token = doc.RootElement.TryGetProperty("access_token", out var t) ? t.GetString() : null;
        if (string.IsNullOrWhiteSpace(token))
            throw new CyberArkApiException((int)resp.StatusCode, "POST", "oauth2 token",
                "CyberArk Identity returned no access_token.");

        _authorizationHeader = $"Bearer {token}";
    }

    private async Task AuthenticateSessionAsync(CyberArkCredentials creds, CancellationToken ct)
    {
        var methodSegment = creds.AuthMethod switch
        {
            CyberArkAuthMethod.Ldap => "LDAP",
            CyberArkAuthMethod.CyberArkVault => "CyberArk",
            CyberArkAuthMethod.Radius => "RADIUS",
            CyberArkAuthMethod.Windows => "Windows",
            _ => throw new InvalidOperationException($"Unsupported CyberArk auth method: {creds.AuthMethod}"),
        };

        var path = $"/API/Auth/{methodSegment}/Logon";
        var req = new HttpRequestMessage(HttpMethod.Post, _pvwaBaseUrl + path);

        if (creds.AuthMethod == CyberArkAuthMethod.Windows)
        {
            // IWA: the handler negotiates using the process identity. An empty JSON object is
            // still required — PVWA rejects a bodyless POST here.
            req.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        }
        else
        {
            if (string.IsNullOrWhiteSpace(creds.Username))
                throw new InvalidOperationException(
                    $"A username is required for CyberArk {methodSegment} authentication.");
            req.Content = new StringContent(
                JsonSerializer.Serialize(new { username = creds.Username, password = creds.Password ?? "" }, JsonOpts),
                Encoding.UTF8, "application/json");
        }

        using var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new CyberArkApiException((int)resp.StatusCode, "POST", path,
                $"CyberArk logon failed ({(int)resp.StatusCode}) at {path}: {Trunc(body)}");

        var token = Unquote(body);
        if (string.IsNullOrWhiteSpace(token))
            throw new CyberArkApiException((int)resp.StatusCode, "POST", path,
                "CyberArk logon returned an empty session token.");

        // NO "Bearer " prefix. See the method doc comment.
        _authorizationHeader = token;
    }

    /// <summary>
    /// PVWA returns tokens (and retrieved passwords) as a JSON string literal, so the raw body
    /// arrives wrapped in double quotes. Strip them, and unescape, before use.
    /// </summary>
    private static string Unquote(string raw)
    {
        var s = raw.Trim();
        if (s.Length >= 2 && s[0] == '"' && s[^1] == '"')
        {
            try { return JsonSerializer.Deserialize<string>(s) ?? ""; }
            catch (JsonException) { return s[1..^1]; }
        }
        return s;
    }

    public async Task LogoffAsync(CancellationToken ct = default)
    {
        // OAuth tokens are not a session; there is nothing to end and PVWA has no endpoint for it.
        if (_creds is null || _creds.AuthMethod == CyberArkAuthMethod.OAuth || _authorizationHeader is null)
            return;

        try
        {
            var req = NewAuthedRequest(HttpMethod.Post, "/API/Auth/Logoff");
            req.Content = new StringContent("{}", Encoding.UTF8, "application/json");
            using var resp = await _http.SendAsync(req, ct);
            _ = resp; // best effort: a failed logoff is not a migration failure
        }
        catch (HttpRequestException) { /* best effort */ }
        catch (TaskCanceledException) { /* best effort */ }
        finally
        {
            _authorizationHeader = null;
        }
    }

    private HttpRequestMessage NewAuthedRequest(HttpMethod method, string pathOrAbsoluteUrl)
    {
        if (_authorizationHeader is null)
            throw new InvalidOperationException("CyberArk session missing; call AuthenticateAsync first.");

        var uri = pathOrAbsoluteUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? pathOrAbsoluteUrl
            : _pvwaBaseUrl + pathOrAbsoluteUrl;

        var req = new HttpRequestMessage(method, uri);
        // TryAddWithoutValidation because an on-prem session token is not a valid
        // "scheme parameter" pair and AuthenticationHeaderValue would reject it.
        req.Headers.TryAddWithoutValidation("Authorization", _authorizationHeader);
        return req;
    }

    // ---- request plumbing: retry, backoff, transparent re-auth -------------------------------

    /// <summary>
    /// Send with bounded retry. Retries 429 and 5xx with exponential backoff (honouring
    /// Retry-After), retries transient transport faults, and re-authenticates ONCE on a 401.
    ///
    /// The 401 path matters in practice: a full inventory of a large vault outlives the PVWA
    /// session timeout, and without this a long extract dies most of the way through.
    /// </summary>
    private async Task<string> SendWithRetryAsync(
        HttpMethod method, string pathOrUrl, HttpContent? content, CancellationToken ct,
        int maxAttempts = 4)
    {
        var attempt = 0;
        var reauthDone = false;

        while (true)
        {
            attempt++;
            ct.ThrowIfCancellationRequested();

            HttpResponseMessage? resp = null;
            try
            {
                var req = NewAuthedRequest(method, pathOrUrl);
                if (content is not null) req.Content = await CloneContentAsync(content, ct);

                resp = await _http.SendAsync(req, ct);
                var body = await resp.Content.ReadAsStringAsync(ct);

                if (resp.IsSuccessStatusCode) return body;

                var status = (int)resp.StatusCode;

                if (status == 401 && !reauthDone && _creds is not null)
                {
                    reauthDone = true;
                    await AuthenticateAsync(_creds, ct);
                    resp.Dispose();
                    continue;
                }

                if (!IsRetryableStatus(status) || attempt >= maxAttempts)
                    throw new CyberArkApiException(status, method.Method, pathOrUrl,
                        $"CyberArk {method.Method} {Redact(pathOrUrl)} -> {status} {resp.ReasonPhrase}: {Trunc(body)}");

                await DelayForRetryAsync(resp, attempt, ct);
            }
            catch (CyberArkApiException) { throw; }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (IsTransient(ex))
            {
                if (attempt >= maxAttempts)
                    throw new CyberArkApiException(0, method.Method, pathOrUrl,
                        $"CyberArk {method.Method} {Redact(pathOrUrl)} failed after {attempt} attempts: {ex.Message}");
                await Task.Delay(BackoffFor(attempt), ct);
            }
            finally
            {
                resp?.Dispose();
            }
        }
    }

    private static bool IsRetryableStatus(int status) => status == 429 || status is >= 500 and <= 599;

    private static bool IsTransient(Exception ex) =>
        ex is HttpRequestException or TaskCanceledException or TimeoutException
           or System.Net.Sockets.SocketException;

    private static TimeSpan BackoffFor(int attempt) =>
        TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, attempt - 1)));

    private static async Task DelayForRetryAsync(HttpResponseMessage resp, int attempt, CancellationToken ct)
    {
        var delay = BackoffFor(attempt);
        if (resp.Headers.RetryAfter is { } ra)
        {
            if (ra.Delta is { } d && d > TimeSpan.Zero) delay = d;
            else if (ra.Date is { } when)
            {
                var fromDate = when - DateTimeOffset.UtcNow;
                if (fromDate > TimeSpan.Zero) delay = fromDate;
            }
            if (delay > TimeSpan.FromSeconds(30)) delay = TimeSpan.FromSeconds(30);
        }
        await Task.Delay(delay, ct);
    }

    /// <summary>HttpContent cannot be reused across sends, so a retry needs its own copy.</summary>
    private static async Task<HttpContent> CloneContentAsync(HttpContent original, CancellationToken ct)
    {
        var bytes = await original.ReadAsByteArrayAsync(ct);
        var clone = new ByteArrayContent(bytes);
        foreach (var h in original.Headers)
            clone.Headers.TryAddWithoutValidation(h.Key, h.Value);
        return clone;
    }

    // ---- paging -----------------------------------------------------------------------------

    /// <summary>
    /// Page a PVWA collection endpoint.
    ///
    /// Two things here are load-bearing:
    ///
    /// 1. A failed page THROWS. The original export script swallowed errors in its paging loop and
    ///    treated a 429 or an expired session as end-of-data, which produced a silently truncated
    ///    export that reported success. A partial inventory that looks complete is worse than a
    ///    failed one.
    ///
    /// 2. The response envelope differs by endpoint — "Safes" for safes, "members" for safe
    ///    members, "value" for accounts, and some deployments return a bare array. All four are
    ///    handled rather than assumed.
    /// </summary>
    private async Task<List<JsonElement>> GetPagedAsync(string path, CancellationToken ct)
    {
        var all = new List<JsonElement>();
        var offset = 0;
        string? nextUrl = null;
        var pages = 0;

        while (true)
        {
            if (++pages > MaxPages)
                throw new CyberArkApiException(0, "GET", path,
                    $"CyberArk paging did not terminate for {Redact(path)} after {MaxPages} pages.");

            var url = nextUrl ?? $"{path}{(path.Contains('?') ? '&' : '?')}limit={PageSize}&offset={offset}";
            var body = await SendWithRetryAsync(HttpMethod.Get, url, null, ct);

            using var doc = JsonDocument.Parse(body);
            var items = ExtractItems(doc.RootElement);
            if (items.Count == 0) break;

            all.AddRange(items);

            nextUrl = null;
            if (doc.RootElement.TryGetProperty("nextLink", out var nl) &&
                nl.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(nl.GetString()))
            {
                nextUrl = ResolveNextLink(nl.GetString()!);
                if (nextUrl is not null) continue;
            }

            if (items.Count < PageSize) break;
            offset += PageSize;
        }

        return all;
    }

    /// <summary>Unwrap whichever envelope this endpoint used. Clones elements because the
    /// owning JsonDocument is disposed per page.</summary>
    private static List<JsonElement> ExtractItems(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
            return root.EnumerateArray().Select(e => e.Clone()).ToList();

        foreach (var name in new[] { "Safes", "members", "value", "Members", "Value" })
            if (root.TryGetProperty(name, out var arr) && arr.ValueKind == JsonValueKind.Array)
                return arr.EnumerateArray().Select(e => e.Clone()).ToList();

        return [];
    }

    /// <summary>
    /// CyberArk's nextLink is relative to the PVWA ROOT, not to the API path — it comes back as
    /// e.g. "API/Accounts?offset=25". Naively appending it to the request URL loses the
    /// /PasswordVault virtual directory and 404s on every deployment that uses one.
    /// </summary>
    private string? ResolveNextLink(string nextLink)
    {
        if (string.IsNullOrWhiteSpace(nextLink)) return null;
        if (nextLink.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return nextLink;
        return $"{_pvwaBaseUrl}/{nextLink.TrimStart('/')}";
    }

    // ---- reads ------------------------------------------------------------------------------

    public async Task<List<CyberArkSafe>> ListSafesAsync(CancellationToken ct = default)
    {
        var raw = await GetPagedAsync("/API/Safes", ct);
        var safes = new List<CyberArkSafe>();

        foreach (var el in raw)
        {
            var name = GetString(el, "safeName") ?? GetString(el, "SafeName");
            if (string.IsNullOrWhiteSpace(name) || IsSystemSafe(name)) continue;

            safes.Add(new CyberArkSafe(
                name,
                GetString(el, "description"),
                GetString(el, "managingCPM"))
            {
                Raw = ToDictionary(el),
            });
        }
        return safes;
    }

    public async Task<List<CyberArkAccount>> ListAccountsAsync(string safeName, CancellationToken ct = default)
    {
        // The filter value is a expression, not a bare value: "safeName eq <name>".
        var filter = Uri.EscapeDataString($"safeName eq {safeName}");
        var raw = await GetPagedAsync($"/API/Accounts?filter={filter}", ct);

        var accounts = new List<CyberArkAccount>();
        foreach (var el in raw)
        {
            var id = GetString(el, "id");
            if (string.IsNullOrWhiteSpace(id)) continue;

            bool cpmManaged = false;
            string? cpmStatus = null;
            if (el.TryGetProperty("secretManagement", out var sm) && sm.ValueKind == JsonValueKind.Object)
            {
                cpmManaged = sm.TryGetProperty("automaticManagementEnabled", out var am)
                             && am.ValueKind == JsonValueKind.True;
                cpmStatus = GetString(sm, "status");
            }

            accounts.Add(new CyberArkAccount
            {
                Id = id,
                Name = GetString(el, "name"),
                UserName = GetString(el, "userName"),
                Address = GetString(el, "address"),
                PlatformId = GetString(el, "platformId"),
                SafeName = safeName,
                SecretType = GetString(el, "secretType") ?? "password",
                CpmManaged = cpmManaged,
                CpmStatus = cpmStatus,
                Raw = ToDictionary(el),
            });
        }
        return accounts;
    }

    public async Task<List<CyberArkSafeMember>> ListSafeMembersAsync(
        string safeName, CancellationToken ct = default)
    {
        // This endpoint IS paged. An unpaged GET silently returns only the server's default page
        // size, so a safe with many members loses the rest — and with them their permissions.
        var raw = await GetPagedAsync($"/API/Safes/{Uri.EscapeDataString(safeName)}/Members", ct);

        var members = new List<CyberArkSafeMember>();
        foreach (var el in raw)
        {
            var name = GetString(el, "memberName") ?? GetString(el, "userName")
                       ?? GetString(el, "groupName") ?? GetString(el, "name");
            if (string.IsNullOrWhiteSpace(name)) continue;

            var type = GetString(el, "memberType")
                       ?? (GetString(el, "groupName") is not null ? "Group" : "User");

            var perms = new Dictionary<string, object?>();
            if (el.TryGetProperty("permissions", out var p) || el.TryGetProperty("Permissions", out p))
                perms = ToDictionary(p);

            members.Add(new CyberArkSafeMember
            {
                SafeName = safeName,
                MemberName = name,
                MemberType = type,
                MemberId = GetString(el, "memberId") ?? GetString(el, "id"),
                Permissions = perms,
            });
        }
        return members;
    }

    /// <summary>
    /// Retrieve one account's credential. POST with an empty JSON object body — a GET, or a POST
    /// with no body, is rejected. The response is a JSON string literal, so it needs unquoting
    /// exactly like the logon token.
    ///
    /// Returns null on 403/404: CyberArk refusing a retrieval (dual control pending, insufficient
    /// permission, one-time password already used) is an expected outcome on a real vault, and the
    /// caller must skip that account rather than migrate an empty credential.
    /// </summary>
    public async Task<string?> RetrieveAccountPasswordAsync(
        string accountId, CancellationToken ct = default)
    {
        var path = $"/API/Accounts/{Uri.EscapeDataString(accountId)}/Password/Retrieve";
        try
        {
            var body = await SendWithRetryAsync(
                HttpMethod.Post, path,
                new StringContent("{}", Encoding.UTF8, "application/json"), ct);

            var value = Unquote(body);
            return string.IsNullOrEmpty(value) ? null : value;
        }
        catch (CyberArkApiException ex) when (ex.StatusCode is (int)HttpStatusCode.Forbidden
                                                            or (int)HttpStatusCode.NotFound
                                                            or (int)HttpStatusCode.BadRequest)
        {
            return null;
        }
    }

    // ---- json helpers ------------------------------------------------------------------------

    private static string? GetString(JsonElement el, string name)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        if (!el.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.ToString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        };
    }

    private static Dictionary<string, object?> ToDictionary(JsonElement el)
    {
        var dict = new Dictionary<string, object?>();
        if (el.ValueKind != JsonValueKind.Object) return dict;

        foreach (var prop in el.EnumerateObject())
            dict[prop.Name] = ToClrValue(prop.Value);
        return dict;
    }

    private static object? ToClrValue(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.String => v.GetString(),
        JsonValueKind.Number => v.TryGetInt64(out var l) ? l : v.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        JsonValueKind.Object => v.EnumerateObject().ToDictionary(p => p.Name, p => ToClrValue(p.Value)),
        JsonValueKind.Array => v.EnumerateArray().Select(ToClrValue).ToList(),
        _ => v.GetRawText(),
    };

    private static string Trunc(string s, int max = 300) =>
        string.IsNullOrEmpty(s) ? "(empty body)" : (s.Length <= max ? s : s[..max] + "…");

    /// <summary>
    /// Strip anything after the account id in a retrieval path before it reaches a log or an
    /// exception message. Account ids are not secret, but the URL is the one place a
    /// credential-adjacent identifier travels, so error text stays deliberately coarse.
    /// </summary>
    private static string Redact(string pathOrUrl)
    {
        var q = pathOrUrl.IndexOf('?');
        return q < 0 ? pathOrUrl : pathOrUrl[..q] + "?…";
    }
}
