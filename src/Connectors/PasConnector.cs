using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PasMigration.Connectors;

/// <summary>
/// Delinea/Centrify PAS connector. Raw REST (no PAS_PCM). Ported from
/// Export-PASLocalAccounts.ps1 / Export-PASTextSecrets.ps1 / Export-PASFileSecrets.ps1 /
/// Diagnose-PASDataVaultSchema.ps1.
///
/// Security: secret material returned by retrieval methods stays in memory and is the
/// caller's responsibility to handle per the security model. Tokens are never logged.
/// </summary>
public sealed class PasConnector : IPasClient
{
    private readonly HttpClient _http;
    private readonly string _tenantBaseUrl; // e.g. https://acme.my.centrify.net
    private readonly string _appId;
    private string? _accessToken;
    private DateTimeOffset _tokenExpiresAt = DateTimeOffset.MinValue;

    // PAS REST expects EXACT PascalCase property names (ID, IsManaged, Script, Args).
    // JsonSerializerDefaults.Web would camelCase them (ID -> id), which PAS rejects with
    // "Parameter 'ID' must be specified". So we use default options that preserve names.
    private static readonly JsonSerializerOptions JsonOpts = new();

    public PasConnector(HttpClient http, string tenantBaseUrl, string appId)
    {
        _http = http;
        _tenantBaseUrl = tenantBaseUrl.TrimEnd('/');
        _appId = appId;
    }

    // X-CENTRIFY-NATIVE-CLIENT: true is required on EVERY request, including the token call.
    private HttpRequestMessage NewRequest(HttpMethod method, string path)
    {
        var req = new HttpRequestMessage(method, $"{_tenantBaseUrl}{path}");
        req.Headers.TryAddWithoutValidation("X-CENTRIFY-NATIVE-CLIENT", "true");
        return req;
    }

    /// <summary>OAuth2 client-credentials. POST /oauth2/token/{AppId}.</summary>
    public async Task AuthenticateAsync(TenantCredentials creds, CancellationToken ct = default)
    {
        var req = NewRequest(HttpMethod.Post, $"/oauth2/token/{_appId}");
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = creds.ClientId,
            ["client_secret"] = creds.ClientSecret,
        };
        if (!string.IsNullOrEmpty(creds.Scope)) form["scope"] = creds.Scope;
        req.Content = new FormUrlEncodedContent(form);

        using var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"PAS token request failed ({(int)resp.StatusCode}): {Truncate(body, 300)}");
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        _accessToken = root.GetProperty("access_token").GetString();
        var expiresIn = root.TryGetProperty("expires_in", out var e) ? e.GetInt32() : 3600;
        // refresh a minute early
        _tokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn - 60);
    }

    private void EnsureToken()
    {
        if (_accessToken is null || DateTimeOffset.UtcNow >= _tokenExpiresAt)
            throw new InvalidOperationException("PAS token missing or expired; call AuthenticateAsync.");
    }

    private HttpRequestMessage NewAuthedRequest(HttpMethod method, string path)
    {
        EnsureToken();
        var req = NewRequest(method, path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
        return req;
    }

    /// <summary>
    /// RedRock SQL query. Returns rows as dictionaries (column name -> value),
    /// with the internal _TableName marker dropped.
    /// </summary>
    public async Task<List<Dictionary<string, object?>>> QueryAsync(
        string sql, CancellationToken ct = default)
    {
        var req = NewAuthedRequest(HttpMethod.Post, "/RedRock/Query");
        var payload = new
        {
            Script = sql,
            Args = new { PageNumber = 1, PageSize = 10000, Limit = 100000, Caching = -1 }
        };
        req.Content = new StringContent(
            JsonSerializer.Serialize(payload, JsonOpts), Encoding.UTF8, "application/json");

        using var resp = await _http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));

        var results = new List<Dictionary<string, object?>>();
        if (!doc.RootElement.TryGetProperty("Result", out var result)) return results;
        if (!result.TryGetProperty("Results", out var rows)) return results;

        foreach (var entry in rows.EnumerateArray())
        {
            if (!entry.TryGetProperty("Row", out var row)) continue;
            var dict = new Dictionary<string, object?>();
            foreach (var prop in row.EnumerateObject())
            {
                if (prop.Name == "_TableName") continue; // drop internal marker
                dict[prop.Name] = prop.Value.ValueKind switch
                {
                    JsonValueKind.String => prop.Value.GetString(),
                    JsonValueKind.Number => prop.Value.TryGetInt64(out var l) ? l : prop.Value.GetDouble(),
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.Null => null,
                    _ => prop.Value.GetRawText()
                };
            }
            results.Add(dict);
        }
        return results;
    }

    // ---- Retrieval (secret material stays in memory; caller must not log/persist) ----

    /// <summary>Checkout an account password. Returns (password, checkoutId/COID).</summary>
    public async Task<(string Password, string CheckoutId)> CheckoutPasswordAsync(
        string accountId, CancellationToken ct = default)
    {
        var req = NewAuthedRequest(HttpMethod.Post, "/ServerManage/CheckoutPassword");
        req.Content = JsonBody(new { ID = accountId });
        using var resp = await _http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        var bodyText = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(bodyText);
        var root = doc.RootElement;

        // A failed checkout returns success=false with the reason in Message (e.g. approval
        // required, no managed password, account not checkout-able). Surface it clearly.
        if (root.TryGetProperty("success", out var ok) && ok.ValueKind == JsonValueKind.False)
        {
            var msg = root.TryGetProperty("Message", out var m) ? m.GetString() : null;
            throw new InvalidOperationException(
                $"PAS password checkout refused for account {accountId}: {msg ?? "no message"}");
        }
        if (!root.TryGetProperty("Result", out var r) || r.ValueKind == JsonValueKind.Null)
            throw new InvalidOperationException(
                $"PAS checkout for account {accountId} returned no Result. Body: " +
                (bodyText.Length > 300 ? bodyText[..300] : bodyText));

        var pw = r.TryGetProperty("Password", out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString() ?? "" : null;
        if (pw is null)
            throw new InvalidOperationException(
                $"PAS checkout for account {accountId} returned no Password. Result fields: " +
                string.Join(", ", r.EnumerateObject().Select(x => x.Name)));
        var coid = r.TryGetProperty("COID", out var c) && c.ValueKind == JsonValueKind.String
            ? c.GetString() ?? "" : "";
        return (pw, coid);
    }

    /// <summary>Check a password back in using the checkout id (COID).</summary>
    public async Task CheckinPasswordAsync(string checkoutId, CancellationToken ct = default)
    {
        var req = NewAuthedRequest(HttpMethod.Post, "/ServerManage/CheckinPassword");
        req.Content = JsonBody(new { ID = checkoutId });
        using var resp = await _http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
    }

    /// <summary>Retrieve a text secret's contents. ID may be a GUID or numeric (passed as-is).</summary>
    public async Task<string> RetrieveTextSecretAsync(string id, CancellationToken ct = default)
    {
        var req = NewAuthedRequest(HttpMethod.Post, "/ServerManage/RetrieveDataVaultItemContents");
        req.Content = JsonBody(new { ID = id });
        using var resp = await _http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        var bodyText = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(bodyText);
        var root = doc.RootElement;

        // PAS wraps results as { success, Result, Message }. A failed retrieval (e.g. approval
        // required, no access) returns success=false with Result null and a reason in Message.
        if (root.TryGetProperty("success", out var ok) && ok.ValueKind == JsonValueKind.False)
        {
            var msg = root.TryGetProperty("Message", out var m) ? m.GetString() : null;
            throw new InvalidOperationException(
                $"PAS retrieval refused for secret {id}: {msg ?? "no message"}");
        }
        if (!root.TryGetProperty("Result", out var result) || result.ValueKind == JsonValueKind.Null)
            throw new InvalidOperationException(
                $"PAS retrieval for secret {id} returned no Result. Body: {(bodyText.Length > 300 ? bodyText[..300] : bodyText)}");

        if (result.TryGetProperty("SecretText", out var st) && st.ValueKind == JsonValueKind.String)
            return st.GetString() ?? "";
        // Fallback: some shapes nest the value differently; surface what we got.
        throw new InvalidOperationException(
            $"PAS retrieval for secret {id} had no SecretText field. Result fields: " +
            string.Join(", ", result.EnumerateObject().Select(p => p.Name)));
    }

    /// <summary>
    /// Download a file secret (two-step). Field name on the handle varies by version
    /// (Location / DownloadUrl / path) so we probe all three. ID may be a GUID or numeric.
    /// </summary>
    public async Task<byte[]> DownloadFileSecretAsync(string id, CancellationToken ct = default)
    {
        // Step 1: request the download handle. Try {secretID} then fall back to {ID}.
        JsonElement result = default;
        var got = false;
        foreach (var bodyObj in new object[] { new { secretID = id }, new { ID = id } })
        {
            var r1 = NewAuthedRequest(HttpMethod.Post, "/ServerManage/RequestSecretDownloadUrl");
            r1.Content = JsonBody(bodyObj);
            using var resp1 = await _http.SendAsync(r1, ct);
            if (!resp1.IsSuccessStatusCode) continue;
            using var doc = JsonDocument.Parse(await resp1.Content.ReadAsStringAsync(ct));
            if (doc.RootElement.TryGetProperty("success", out var ok) && ok.ValueKind == JsonValueKind.False)
                continue;
            if (doc.RootElement.TryGetProperty("Result", out var res) && res.ValueKind == JsonValueKind.Object)
            {
                result = res.Clone();
                got = true;
                break;
            }
        }
        if (!got)
            throw new InvalidOperationException($"RequestSecretDownloadUrl failed for secret {id}.");

        // Resolve the handle across the field names PAS versions use.
        string? handle = null;
        foreach (var field in new[] { "Location", "DownloadUrl", "FileDownloadUrl", "Url", "path", "Path" })
            if (result.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.String
                && !string.IsNullOrEmpty(v.GetString()))
            { handle = v.GetString(); break; }
        if (handle is null)
            throw new InvalidOperationException(
                "No download handle in response. Result fields: " +
                string.Join(", ", result.EnumerateObject().Select(p => p.Name)));

        // Build the download target. If the handle is already absolute, use it directly;
        // otherwise express it as a path relative to the tenant base (NewAuthedRequest prepends base).
        string? absoluteUrl = null;
        string relativePath;
        if (handle.StartsWith("http://") || handle.StartsWith("https://")) { absoluteUrl = handle; relativePath = ""; }
        else if (handle.StartsWith("/")) relativePath = handle;
        else if (handle.Contains("DownloadFile")) relativePath = $"/{handle}";
        else relativePath = $"/Core/DownloadFile?path={Uri.EscapeDataString(handle)}";

        // Step 2: pull the bytes. Docs show POST; GET as fallback.
        foreach (var method in new[] { HttpMethod.Post, HttpMethod.Get })
        {
            HttpRequestMessage r2;
            if (absoluteUrl is not null)
            {
                r2 = new HttpRequestMessage(method, absoluteUrl);
                EnsureToken();
                r2.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
                r2.Headers.Add("X-CENTRIFY-NATIVE-CLIENT", "true");
            }
            else
            {
                r2 = NewAuthedRequest(method, relativePath);
            }
            using var resp2 = await _http.SendAsync(r2, ct);
            if (resp2.IsSuccessStatusCode)
                return await resp2.Content.ReadAsByteArrayAsync(ct);
        }
        throw new InvalidOperationException($"DownloadFile failed for secret {id}.");
    }

    /// <summary>
    /// Unmanage an account (write). Minimal body first; some versions need the full
    /// account object echoed back (open item) - caller can retry via UpdateAccountFullAsync.
    /// </summary>
    /// <summary>
    /// Unmanage an account. PAS <c>UpdateAccount</c> requires the FULL account object echoed back
    /// (including the live <c>_STAMP</c> concurrency token), not a partial body — a minimal
    /// <c>{ID, IsManaged=false}</c> is rejected with "Invalid arguments passed to the server" as
    /// HTTP 200 + <c>success=false</c>. So this reads the full object via GetAllAccountInformation,
    /// flips IsManaged, and posts the whole object back, mirroring what the PAS portal does.
    ///
    /// The full object (with _STAMP) is only available from GetAllAccountInformation — RedRock
    /// <c>SELECT * FROM VaultAccount</c> does NOT include _STAMP.
    /// </summary>
    public async Task UnmanageAccountAsync(string id, CancellationToken ct = default)
    {
        // 1. Read the full account object (Result.VaultAccount.Row).
        var row = await GetAccountFullAsync(id, ct);

        // 2. Flip IsManaged; drop the internal _TableName marker to match the portal's write body.
        row["IsManaged"] = false;
        row.Remove("_TableName");

        // 3. Post the whole object back.
        var req = NewAuthedRequest(HttpMethod.Post, "/ServerManage/UpdateAccount");
        req.Content = new StringContent(row.ToJsonString(JsonOpts), Encoding.UTF8, "application/json");
        using var resp = await _http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();

        // 4. PAS returns HTTP 200 + success=false for logical failures — status code is not enough.
        var bodyText = await resp.Content.ReadAsStringAsync(ct);
        using (var doc = JsonDocument.Parse(bodyText))
        {
            var root = doc.RootElement;
            if (root.TryGetProperty("success", out var ok) && ok.ValueKind == JsonValueKind.False)
            {
                var msg = root.TryGetProperty("Message", out var m) ? m.GetString() : null;
                throw new InvalidOperationException(
                    $"PAS unmanage failed for account {id}: {msg ?? "no message"}");
            }
        }

        // 5. Verify the flip actually took effect — some shapes return OK without applying.
        var check = await QueryAsync($"SELECT ID, IsManaged FROM VaultAccount WHERE ID='{id}'", ct);
        if (check.Count > 0 && check[0].TryGetValue("IsManaged", out var im) && IsTruthy(im))
            throw new InvalidOperationException(
                $"PAS unmanage for account {id} reported success but IsManaged is still true.");
    }

    // RedRock may return IsManaged as a JSON bool, or (tenant-dependent) as a string/number.
    // Treat any of true / "true" / "1" / 1 as still-managed so verification can't silently pass.
    private static bool IsTruthy(object? v) => v switch
    {
        bool b   => b,
        string s => s.Equals("true", StringComparison.OrdinalIgnoreCase) || s == "1",
        long l   => l != 0,
        double d => d != 0,
        _        => false,
    };

    /// <summary>
    /// Fetch the full account object from <c>/ServerManage/GetAllAccountInformation</c>. The account
    /// object is nested at <c>Result.VaultAccount.Row</c> and includes the <c>_STAMP</c> concurrency
    /// token required for a subsequent UpdateAccount write. Returns a mutable <see cref="JsonObject"/>.
    /// Throws on <c>success=false</c> or a missing Row.
    /// </summary>
    private async Task<JsonObject> GetAccountFullAsync(string id, CancellationToken ct = default)
    {
        var req = NewAuthedRequest(HttpMethod.Post, "/ServerManage/GetAllAccountInformation");
        req.Content = JsonBody(new { ID = id });
        using var resp = await _http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        var bodyText = await resp.Content.ReadAsStringAsync(ct);

        var node = JsonNode.Parse(bodyText) as JsonObject
            ?? throw new InvalidOperationException(
                $"PAS GetAllAccountInformation for {id} returned a non-object body.");

        if (node["success"] is JsonValue sv && sv.TryGetValue<bool>(out var ok) && !ok)
        {
            var msg = node["Message"]?.GetValue<string>();
            throw new InvalidOperationException(
                $"PAS GetAllAccountInformation refused for account {id}: {msg ?? "no message"}");
        }

        // The account object lives at Result.VaultAccount.Row — this exact path matters.
        var rowNode = node["Result"]?["VaultAccount"]?["Row"] as JsonObject;
        if (rowNode is null)
            throw new InvalidOperationException(
                $"PAS GetAllAccountInformation for {id} had no Result.VaultAccount.Row. Body: " +
                (bodyText.Length > 300 ? bodyText[..300] : bodyText));

        // Detach from the parent document so we can mutate and re-serialize it standalone.
        return (JsonObject)rowNode.DeepClone();
    }

    private static StringContent JsonBody(object o) =>
        new(JsonSerializer.Serialize(o, JsonOpts), Encoding.UTF8, "application/json");

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) ? "(empty body)" : (s.Length <= max ? s : s[..max] + "…");

    // ---- Inventory (read-only) ----
    // Returns raw row dictionaries; the inventory service maps them. Queries are written to
    // match the documented schema but the caller reads whatever columns come back, so a
    // tenant-specific column variation surfaces as data rather than a hard failure.

    /// <summary>
    /// Local accounts (DomainID IS NULL), joined to Server for host context.
    /// The Host->Server.ID join column can vary by tenant; if this returns no Server fields,
    /// fall back to InventoryLocalAccountsNoJoinAsync.
    /// </summary>
    /// <summary>
    /// All LOCAL accounts (Host set, DomainID null) joined to their Server for FQDN/ComputerClass.
    /// </summary>
    public Task<List<Dictionary<string, object?>>> InventoryLocalAccountsAsync(CancellationToken ct = default) =>
        QueryAsync(
            @"SELECT VaultAccount.ID, VaultAccount.User, VaultAccount.IsManaged,
                     VaultAccount.Description, VaultAccount.Status, VaultAccount.MPParent,
                     Server.Name AS ServerName, Server.FQDN AS ServerFQDN,
                     Server.ComputerClass AS ComputerClass
              FROM VaultAccount
              LEFT JOIN Server ON VaultAccount.Host = Server.ID
              WHERE VaultAccount.DomainID IS NULL AND VaultAccount.Host IS NOT NULL", ct);

    /// <summary>
    /// All DOMAIN accounts (DomainID set) joined to VaultDomain for the domain name.
    /// </summary>
    public Task<List<Dictionary<string, object?>>> InventoryDomainAccountsAsync(CancellationToken ct = default) =>
        QueryAsync(
            @"SELECT VaultAccount.ID, VaultAccount.User, VaultAccount.IsManaged,
                     VaultAccount.Description, VaultAccount.Status, VaultAccount.MPParent,
                     VaultDomain.Name AS DomainName
              FROM VaultAccount
              JOIN VaultDomain ON VaultAccount.DomainID = VaultDomain.ID", ct);

    /// <summary>
    /// Multiplexed accounts - these have NO migration path and must be flagged in inventory.
    /// Returns empty if the tenant has none.
    /// </summary>
    public Task<List<Dictionary<string, object?>>> InventoryMultiplexedAccountsAsync(CancellationToken ct = default) =>
        QueryAsync(@"SELECT ID, Name, Description FROM MultiplexedAccount", ct);

    /// <summary>Fallback: local accounts without the Server join (when the join column differs).</summary>
    public Task<List<Dictionary<string, object?>>> InventoryLocalAccountsNoJoinAsync(CancellationToken ct = default) =>
        QueryAsync(
            @"SELECT ID, User, IsManaged, Description, Status, Host
              FROM VaultAccount WHERE DomainID IS NULL", ct);

    /// <summary>Secrets (text + file). Type is only 'Text' or 'File'. Folder path is ParentPath.</summary>
    public Task<List<Dictionary<string, object?>>> InventorySecretsAsync(CancellationToken ct = default) =>
        QueryAsync(
            @"SELECT ID, FolderId, SecretName, Description, Type, IsFavorite,
                     SecretFileName, SecretFileSize, WhenCreated, WhenContentsReplaced,
                     ParentPath, WorkflowEnabled, Version
              FROM DataVault", ct);

    /// <summary>
    /// Secret folders are Phantom Sets (CollectionType=Phantom, ObjectType=DataVault),
    /// NOT Type='Folder' DataVault rows.
    /// </summary>
    public Task<List<Dictionary<string, object?>>> InventoryFolderSetsAsync(CancellationToken ct = default) =>
        QueryAsync(
            @"SELECT ID, Name, ParentPath, CollectionType, ObjectType
              FROM Sets WHERE CollectionType='Phantom' AND ObjectType='DataVault'", ct);
}

