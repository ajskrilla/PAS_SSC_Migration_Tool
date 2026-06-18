using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace PasMigration.Connectors;

/// <summary>
/// Delinea Platform + Secret Server connector. Ported from Import-PASFilesToSecretServer.ps1
/// and the Platform invite/permission sample.
///
/// Key fact: a single Platform OAuth2 bearer token authorizes BOTH the platform identity API
/// (https://{tenant}.delinea.app/identity/api/...) AND the Secret Server Cloud API
/// (https://{tenant}.secretservercloud.com/api/v1/...). So for platform/migrated tenants we
/// acquire a platform token once and reuse it for SS resource calls.
/// </summary>
public sealed class SecretServerConnector
{
    private readonly HttpClient _http;
    private readonly string _platformBaseUrl;     // https://{tenant}.delinea.app
    private readonly string _secretServerBaseUrl; // https://{tenant}.secretservercloud.com
    private readonly AuthMode _authMode;
    private string? _accessToken;
    private DateTimeOffset _tokenExpiresAt = DateTimeOffset.MinValue;

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public enum AuthMode { PlatformClientCredentials, LegacyPassword }

    public SecretServerConnector(
        HttpClient http, string platformBaseUrl, string secretServerBaseUrl, AuthMode authMode)
    {
        _http = http;
        _platformBaseUrl = platformBaseUrl.TrimEnd('/');
        _secretServerBaseUrl = secretServerBaseUrl.TrimEnd('/');
        _authMode = authMode;
    }

    /// <summary>
    /// Platform client-credentials (primary path). Same token works for SS Cloud API.
    /// scope=xpmheadless, endpoint .../identity/api/oauth2/token/xpmplatform.
    /// </summary>
    public async Task AuthenticatePlatformAsync(TenantCredentials creds, CancellationToken ct = default)
    {
        var url = $"{_platformBaseUrl}/identity/api/oauth2/token/xpmplatform";
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["scope"] = "xpmheadless",
            ["client_id"] = creds.ClientId,
            ["client_secret"] = creds.ClientSecret,
        };
        using var resp = await _http.PostAsync(url, new FormUrlEncodedContent(form), ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"Platform token request failed ({(int)resp.StatusCode}): {Trunc(body)}");
        ReadToken(body);
    }

    /// <summary>Legacy fallback: standalone Secret Server password grant.</summary>
    public async Task AuthenticateLegacyAsync(
        string username, string password, CancellationToken ct = default)
    {
        var url = $"{_secretServerBaseUrl}/oauth2/token";
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["username"] = username,
            ["password"] = password,
        };
        using var resp = await _http.PostAsync(url, new FormUrlEncodedContent(form), ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"Legacy token request failed ({(int)resp.StatusCode}): {Trunc(body)}");
        ReadToken(body);
    }

    private static string Trunc(string s) =>
        string.IsNullOrEmpty(s) ? "(empty body)" : (s.Length <= 300 ? s : s[..300] + "…");

    /// <summary>
    /// Throw a detailed exception (method, path, status, body) on a non-success response.
    /// Returns the body string on success so callers can parse it without re-reading.
    /// </summary>
    private static async Task<string> EnsureOk(
        HttpResponseMessage resp, string method, string path, CancellationToken ct)
    {
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new SecretServerApiException(
                (int)resp.StatusCode, method, path,
                $"Secret Server {method} {path} -> {(int)resp.StatusCode} {resp.ReasonPhrase}: {Trunc(body)}");
        return body;
    }

    private void ReadToken(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        _accessToken = root.GetProperty("access_token").GetString();
        var expiresIn = root.TryGetProperty("expires_in", out var e) ? e.GetInt32() : 3600;
        _tokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn - 60);
    }

    private HttpRequestMessage SsRequest(HttpMethod method, string apiPath)
    {
        if (_accessToken is null || DateTimeOffset.UtcNow >= _tokenExpiresAt)
            throw new InvalidOperationException("SS token missing or expired; authenticate first.");
        var req = new HttpRequestMessage(method, $"{_secretServerBaseUrl}/api/v1{apiPath}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
        return req;
    }

    // ---- Folders: search-or-create for idempotent structure (root parent = -1) ----

    public async Task<long?> FindFolderAsync(
        string name, long parentFolderId, CancellationToken ct = default)
    {
        // Search by name only (searchText). Filtering by parentFolderId as a query param is
        // unreliable on SS Cloud (root = -1 can 404), so we match the parent client-side.
        var path = $"/folders?filter.searchText={Uri.EscapeDataString(name)}";
        var req = SsRequest(HttpMethod.Get, path);
        using var resp = await _http.SendAsync(req, ct);

        // A search that finds nothing may come back 404 - that's "not found", not an error.
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        var bodyText = await EnsureOk(resp, "GET", path, ct);

        using var doc = JsonDocument.Parse(bodyText);
        if (!doc.RootElement.TryGetProperty("records", out var records)) return null;
        foreach (var rec in records.EnumerateArray())
        {
            var fName = rec.TryGetProperty("folderName", out var fn) ? fn.GetString() : null;
            if (!string.Equals(fName, name, StringComparison.OrdinalIgnoreCase)) continue;
            // Match the parent so we don't reuse a same-named folder elsewhere in the tree.
            var recParent = rec.TryGetProperty("parentFolderId", out var pp) && pp.TryGetInt64(out var pid)
                ? pid : long.MinValue;
            if (recParent == parentFolderId && rec.TryGetProperty("id", out var idEl)
                && idEl.TryGetInt64(out var fid2))
                return fid2;
        }
        return null;
    }

    public async Task<long> CreateFolderAsync(
        string name, long parentFolderId, CancellationToken ct = default)
    {
        // Per the Delinea REST examples: GET /folders/stub, modify ONLY the fields we control
        // on the returned object, then POST the WHOLE stub back. Posting a hand-built partial
        // body causes Secret Server Cloud to reject with a generic API_AccessDenied.
        var stubReq = SsRequest(HttpMethod.Get, "/folders/stub");
        using var stubResp = await _http.SendAsync(stubReq, ct);
        var stubBody = await EnsureOk(stubResp, "GET", "/folders/stub", ct);

        // Parse the stub into a mutable dictionary so we preserve every field it returned.
        var body = JsonSerializer.Deserialize<Dictionary<string, object?>>(stubBody)
                   ?? new Dictionary<string, object?>();
        body["folderName"] = name;
        body["folderTypeId"] = 1;            // 1 = Folder
        body["parentFolderId"] = parentFolderId;
        // A root folder (parent = -1) has no parent to inherit from; Secret Server rejects
        // inherit=true at root with a generic "API_AccessDenied". Inherit only under a real parent.
        var atRoot = parentFolderId <= 0;
        body["inheritPermissions"] = !atRoot;
        body["inheritSecretPolicy"] = !atRoot;

        var req = SsRequest(HttpMethod.Post, "/folders");
        req.Content = JsonBody(body);
        using var resp = await _http.SendAsync(req, ct);
        var created = await EnsureOk(resp, "POST", "/folders", ct);
        using var doc = JsonDocument.Parse(created);
        return doc.RootElement.GetProperty("id").GetInt64();
    }

    /// <summary>Idempotent: return existing folder id or create it.</summary>
    public async Task<long> EnsureFolderAsync(
        string name, long parentFolderId, CancellationToken ct = default)
        => await FindFolderAsync(name, parentFolderId, ct)
           ?? await CreateFolderAsync(name, parentFolderId, ct);

    /// <summary>Find a secret template id by name.</summary>
    public async Task<long?> FindTemplateAsync(string name, CancellationToken ct = default)
    {
        var req = SsRequest(HttpMethod.Get,
            $"/secret-templates?filter.searchText={Uri.EscapeDataString(name)}");
        using var resp = await _http.SendAsync(req, ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        var bodyText = await EnsureOk(resp, "GET", "/secret-templates", ct);
        using var doc = JsonDocument.Parse(bodyText);
        if (!doc.RootElement.TryGetProperty("records", out var records)) return null;
        foreach (var rec in records.EnumerateArray())
        {
            // Null-safe: some template records may omit name/id; don't let GetProperty throw.
            var recName = rec.TryGetProperty("name", out var n) ? n.GetString() : null;
            if (string.Equals(recName, name, StringComparison.OrdinalIgnoreCase)
                && rec.TryGetProperty("id", out var idEl) && idEl.TryGetInt64(out var id))
                return id;
        }
        return null;
    }

    // ---- Secret creation (write) ----

    /// <summary>
    /// Create a secret from a template, filling item values by slug. For a file field,
    /// pass fieldSlug + base64 content + filename in <paramref name="fileField"/>.
    /// Returns the new secret id. Secret values are passed in memory only.
    /// </summary>
    public async Task<long> CreateSecretAsync(
        string name, long templateId, long folderId,
        IReadOnlyDictionary<string, string> textValuesBySlug,
        (string Slug, string Filename, string Base64)? fileField,
        CancellationToken ct = default)
    {
        // Get the stub for this template to learn the exact item shape (slugs, fieldIds, isFile).
        // Secret Server Cloud REQUIRES folderId on the stub call (else API_FolderIdRequired).
        var stubReq = SsRequest(HttpMethod.Get,
            $"/secrets/stub?filter.secrettemplateid={templateId}&filter.folderId={folderId}");
        using var stubResp = await _http.SendAsync(stubReq, ct);
        var stubBody = await EnsureOk(stubResp, "GET", "/secrets/stub", ct);
        using var stubDoc = JsonDocument.Parse(stubBody);
        var stub = stubDoc.RootElement;

        // Echo the stub object back, modifying only the item values we have (by slug).
        // Rebuilding items from scratch risks dropping fields the API requires and can hit
        // null fields (e.g. fieldId null) - so we mutate the parsed stub in place instead.
        var body = JsonSerializer.Deserialize<Dictionary<string, object?>>(stubBody)
                   ?? new Dictionary<string, object?>();
        body["name"] = name;
        body["secretTemplateId"] = templateId;
        body["folderId"] = folderId;
        body["siteId"] = 1; // Local

        // Re-derive items from the raw stub JSON, preserving each item's full shape.
        var items = new List<Dictionary<string, object?>>();
        if (stub.TryGetProperty("items", out var stubItems) && stubItems.ValueKind == JsonValueKind.Array)
        {
            foreach (var it in stubItems.EnumerateArray())
            {
                // Preserve every field on the stub item as-is.
                var item = JsonSerializer.Deserialize<Dictionary<string, object?>>(it.GetRawText())
                           ?? new Dictionary<string, object?>();
                var slug = it.TryGetProperty("slug", out var s) ? s.GetString() ?? "" : "";
                var isFile = it.TryGetProperty("isFile", out var f) && f.ValueKind == JsonValueKind.True;
                if (isFile && fileField is { } ff && slug == ff.Slug)
                {
                    item["filename"] = ff.Filename;
                    item["itemValue"] = ff.Base64;
                }
                else if (textValuesBySlug.TryGetValue(slug, out var val))
                {
                    item["itemValue"] = val;
                }
                items.Add(item);
            }
        }
        body["items"] = items;

        var req = SsRequest(HttpMethod.Post, "/secrets");
        req.Content = JsonBody(body);
        using var resp = await _http.SendAsync(req, ct);
        var createdBody = await EnsureOk(resp, "POST", "/secrets", ct);
        using var doc = JsonDocument.Parse(createdBody);
        return doc.RootElement.GetProperty("id").GetInt64();
    }

    /// <summary>
    /// Ensure the "File Migration Template" exists (fields: Name, File, Description).
    /// Returns its template id. Created once, reused thereafter.
    /// </summary>
    public async Task<long> EnsureFileMigrationTemplateAsync(CancellationToken ct = default)
    {
        const string name = "File Migration Template";
        var existing = await FindTemplateAsync(name, ct);
        if (existing is { } id) return id;

        // Create the template with a file field + description. Name is the secret name itself.
        var body = new
        {
            name,
            fields = new object[]
            {
                new { name = "File", isFile = true, fieldSlug = "file" },
                new { name = "Description", isFile = false, fieldSlug = "description" },
            },
        };
        var req = SsRequest(HttpMethod.Post, "/secret-templates");
        req.Content = JsonBody(body);
        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"Create File Migration Template failed ({(int)resp.StatusCode}): {Trunc(await resp.Content.ReadAsStringAsync(ct))}. " +
                "If template creation is restricted on this tenant, create it manually with fields Name/File/Description.");
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        return doc.RootElement.GetProperty("id").GetInt64();
    }

    /// <summary>Verify a secret's file item has an attachment id (byte-fidelity check helper).</summary>
    public async Task<bool> SecretHasFileAttachmentAsync(long secretId, CancellationToken ct = default)
    {
        var req = SsRequest(HttpMethod.Get, $"/secrets/{secretId}");
        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) return false;
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        if (!doc.RootElement.TryGetProperty("items", out var items)) return false;
        foreach (var it in items.EnumerateArray())
            if (it.TryGetProperty("fileAttachmentId", out var fa) &&
                fa.ValueKind == JsonValueKind.Number && fa.GetInt64() > 0)
                return true;
        return false;
    }

    // ---- Delete (revert; tool-created items only - caller enforces) ----

    public async Task<bool> DeleteSecretAsync(long secretId, CancellationToken ct = default)
    {
        var req = SsRequest(HttpMethod.Delete, $"/secrets/{secretId}");
        using var resp = await _http.SendAsync(req, ct);
        return resp.IsSuccessStatusCode;
    }

    public async Task<bool> DeleteFolderAsync(long folderId, CancellationToken ct = default)
    {
        var req = SsRequest(HttpMethod.Delete, $"/folders/{folderId}");
        using var resp = await _http.SendAsync(req, ct);
        return resp.IsSuccessStatusCode;
    }

    // ---- Inventory (read-only) ----

    /// <summary>
    /// List all folders, paging through results. Returns raw record dictionaries.
    /// </summary>
    public async Task<List<Dictionary<string, object?>>> InventoryFoldersAsync(CancellationToken ct = default)
        => await PagedRecordsAsync("/folders?take=1000", ct);

    /// <summary>List all secrets (metadata only - no values retrieved), paging through results.</summary>
    public async Task<List<Dictionary<string, object?>>> InventorySecretsAsync(CancellationToken ct = default)
        => await PagedRecordsAsync("/secrets?take=1000", ct);

    /// <summary>
    /// Generic pager over Secret Server list endpoints that return { records[], hasNext, nextSkip }.
    /// Reads whatever fields each record carries (folderName/folderPath/id, or name/folderId/id).
    /// </summary>
    private async Task<List<Dictionary<string, object?>>> PagedRecordsAsync(
        string apiPath, CancellationToken ct)
    {
        var all = new List<Dictionary<string, object?>>();
        var skip = 0;
        while (true)
        {
            var sep = apiPath.Contains('?') ? "&" : "?";
            var req = SsRequest(HttpMethod.Get, $"{apiPath}{sep}skip={skip}");
            using var resp = await _http.SendAsync(req, ct);
            var pageBody = await EnsureOk(resp, "GET", apiPath, ct);
            using var doc = JsonDocument.Parse(pageBody);
            var root = doc.RootElement;
            if (!root.TryGetProperty("records", out var records)) break;

            var count = 0;
            foreach (var rec in records.EnumerateArray())
            {
                all.Add(ToDict(rec));
                count++;
            }

            var hasNext = root.TryGetProperty("hasNext", out var hn) && hn.ValueKind == JsonValueKind.True;
            if (!hasNext || count == 0) break;
            skip = root.TryGetProperty("nextSkip", out var ns) && ns.TryGetInt32(out var n) ? n : skip + count;
        }
        return all;
    }

    private static Dictionary<string, object?> ToDict(JsonElement obj)
    {
        var d = new Dictionary<string, object?>();
        foreach (var p in obj.EnumerateObject())
            d[p.Name] = p.Value.ValueKind switch
            {
                JsonValueKind.String => p.Value.GetString(),
                JsonValueKind.Number => p.Value.TryGetInt64(out var l) ? l : p.Value.GetDouble(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Null => null,
                _ => p.Value.GetRawText()
            };
        return d;
    }

    private static StringContent JsonBody(object o) =>
        new(JsonSerializer.Serialize(o, JsonOpts), Encoding.UTF8, "application/json");

    // NOTE: CreateSecret (text + inline-base64 file fields) and GET-verify are the next
    // methods to port from Import-PASFilesToSecretServer.ps1. Stubbed in the migration
    // module so the orchestrator interface is wired before the open file-fidelity item
    // (inline base64 vs multipart) is resolved.
}

/// <summary>Carries the status code, method, and path of a failed Secret Server call.</summary>
public sealed class SecretServerApiException(int statusCode, string method, string path, string message)
    : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public string Method { get; } = method;
    public string Path { get; } = path;
}
