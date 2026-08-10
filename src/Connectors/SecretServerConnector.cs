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
public sealed class SecretServerConnector : ISecretServerClient
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

    /// <summary>
    /// List all secret templates (id, name, hasFileField). Used to populate the UI template
    /// picker. hasFileField is determined by fetching each template's fields - to keep it cheap
    /// we only flag by name heuristic here; callers needing certainty can fetch the stub.
    /// </summary>
    public async Task<List<(long Id, string Name)>> ListTemplatesAsync(CancellationToken ct = default)
    {
        var all = new List<(long, string)>();
        var skip = 0;
        while (true)
        {
            var req = SsRequest(HttpMethod.Get, $"/secret-templates?take=1000&skip={skip}");
            using var resp = await _http.SendAsync(req, ct);
            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) break;
            var body = await EnsureOk(resp, "GET", "/secret-templates", ct);
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("records", out var records)) break;
            var count = 0;
            foreach (var rec in records.EnumerateArray())
            {
                count++;
                if (!rec.TryGetProperty("id", out var idEl) || !idEl.TryGetInt64(out var id)) continue;
                var name = rec.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                all.Add((id, name));
            }
            var hasNext = doc.RootElement.TryGetProperty("hasNext", out var hn) && hn.ValueKind == JsonValueKind.True;
            if (!hasNext || count == 0) break;
            skip += count;
        }
        return all.OrderBy(t => t.Item2, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Returns true if the template has at least one File-type field (so it can hold a file
    /// attachment). Used to bail before migrating files into a template that can't store them.
    /// </summary>
    public async Task<bool> TemplateHasFileFieldAsync(long templateId, long folderId, CancellationToken ct = default)
    {
        // Read the template's field shape via GET /secrets/stub - the SAME call CreateSecretAsync
        // relies on, so it's proven to work on this tenant. The older GET /secret-templates/{id}/fields
        // returns 405 here (that path only supports POST-to-add-a-field). The stub requires a
        // folderId (else API_FolderIdRequired); the staging folder id is passed in.
        var req = SsRequest(HttpMethod.Get,
            $"/secrets/stub?filter.secrettemplateid={templateId}&filter.folderId={folderId}");
        using var resp = await _http.SendAsync(req, ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return false;
        var body = await EnsureOk(resp, "GET", "/secrets/stub", ct);
        using var doc = JsonDocument.Parse(body);
        // The stub carries an "items" array; each item describes one template field with isFile.
        if (!doc.RootElement.TryGetProperty("items", out var items)
            || items.ValueKind != JsonValueKind.Array) return false;
        foreach (var f in items.EnumerateArray())
        {
            // A file field reports isFile=true, or type/fieldType "File".
            if (f.TryGetProperty("isFile", out var isf) && isf.ValueKind == JsonValueKind.True) return true;
            foreach (var typeProp in new[] { "type", "fieldType", "dataType" })
                if (f.TryGetProperty(typeProp, out var t) && t.ValueKind == JsonValueKind.String
                    && string.Equals(t.GetString(), "File", StringComparison.OrdinalIgnoreCase))
                    return true;
        }
        return false;
    }

    /// <summary>
    /// Create a file-capable template (fields: Name[text], Description[text], File[file]) -
    /// mirrors the working "Migration File Template" the user built by hand. Returns its id.
    /// Field creation uses the per-field endpoint, which is more reliable than inlining fields
    /// on the template-create body (that path fails with "Failed to set column ...").
    /// </summary>
    public async Task<long> CreateFileTemplateAsync(string name, CancellationToken ct = default)
    {
        // 1. Create the bare template.
        var createReq = SsRequest(HttpMethod.Post, "/secret-templates");
        createReq.Content = JsonBody(new { name });
        using var createResp = await _http.SendAsync(createReq, ct);
        var createdBody = await EnsureOk(createResp, "POST", "/secret-templates", ct);
        using var createdDoc = JsonDocument.Parse(createdBody);
        var templateId = createdDoc.RootElement.GetProperty("id").GetInt64();

        // 2. Add fields one at a time: Name (text), Description (text), File (file).
        async Task AddField(string fieldName, string slug, bool isFile)
        {
            var fReq = SsRequest(HttpMethod.Post, $"/secret-templates/{templateId}/fields");
            fReq.Content = JsonBody(new
            {
                secretTemplateId = templateId,
                displayName = fieldName,
                fieldSlugName = slug,
                isFile,
                isPassword = false,
                isNotes = false,
                isRequired = isFile,           // require the file field
                fieldDescription = fieldName,
            });
            using var fResp = await _http.SendAsync(fReq, ct);
            await EnsureOk(fResp, "POST", $"/secret-templates/{templateId}/fields", ct);
        }

        await AddField("Name", "name", false);
        await AddField("Description", "description", false);
        await AddField("File", "file", true);
        return templateId;
    }

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
        string? fileSlug = null;  // set if the template has a file field we need to upload to
        if (stub.TryGetProperty("items", out var stubItems) && stubItems.ValueKind == JsonValueKind.Array)
        {
            foreach (var it in stubItems.EnumerateArray())
            {
                // Preserve every field on the stub item as-is.
                var item = JsonSerializer.Deserialize<Dictionary<string, object?>>(it.GetRawText())
                           ?? new Dictionary<string, object?>();
                var slug = it.TryGetProperty("slug", out var s) ? s.GetString() ?? "" : "";
                var isFile = it.TryGetProperty("isFile", out var f) && f.ValueKind == JsonValueKind.True;
                // For a file field, DON'T inline the base64 in the create body - Secret Server caps
                // item value length (API_InvalidSecretItemValueLength) and large files blow past it.
                // We create the secret with the file field empty, then upload via the dedicated
                // file-upload endpoint below (no size cap).
                if (isFile && fileField is { } ff)
                {
                    item["filename"] = ff.Filename;   // set the name; value uploaded separately
                    fileSlug = string.IsNullOrEmpty(slug) ? "file" : slug;
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
        var secretId = doc.RootElement.GetProperty("id").GetInt64();

        // If this is a file secret, upload the bytes to the file field via the dedicated
        // endpoint (multipart). This avoids the inline base64 length cap entirely.
        if (fileSlug is not null && fileField is { } ff2)
            await UploadSecretFileAsync(secretId, fileSlug, ff2.Filename, ff2.Base64, ct);

        return secretId;
    }

    /// <summary>
    /// Upload a file to a secret's file field via PUT /secrets/{id}/fields/{slug} (multipart).
    /// Used instead of inlining base64 on create, which Secret Server caps in length.
    /// </summary>
    private async Task UploadSecretFileAsync(
        long secretId, string slug, string filename, string base64, CancellationToken ct)
    {
        var bytes = Convert.FromBase64String(base64);
        using var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(bytes);
        fileContent.Headers.ContentType =
            new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        // Secret Server expects the file part named "file" with the filename.
        content.Add(fileContent, "file", filename);

        // Probe against the dzntz tenant confirmed this field-file upload uses PUT, not POST.
        // POST routed to a handler that tried to bind the body as [0].Key/[0].Value pairs and
        // returned 405. PUT /secrets/{id}/fields/{slug} with a multipart part named "file" works.
        var req = SsRequest(HttpMethod.Put, $"/secrets/{secretId}/fields/{slug}");
        req.Content = content;  // multipart, not JSON
        using var resp = await _http.SendAsync(req, ct);
        await EnsureOk(resp, "PUT", $"/secrets/{secretId}/fields/{slug}", ct);
    }

    /// <summary>
    /// Ensure the "File Migration Template" exists (fields: Name, File, Description).
    /// <summary>
    /// Resolve the template to use for migrated file secrets. Secret Server ships a built-in
    /// "File" template, so we find and reuse it (like the reference import script) rather than
    /// creating a custom template - template creation is often restricted and the create body
    /// shape is finicky ("Failed to set column SecretFieldDescription").
    /// </summary>
    public async Task<long> EnsureFileMigrationTemplateAsync(CancellationToken ct = default)
    {
        // Try the common built-in file-template names, in order of preference.
        foreach (var candidate in new[] { "File", "File Migration Template", "File Attachment", "Secret File" })
        {
            var found = await FindTemplateAsync(candidate, ct);
            if (found is { } id) return id;
        }
        throw new InvalidOperationException(
            "No file-capable secret template found on the target (looked for 'File', " +
            "'File Migration Template', 'File Attachment', 'Secret File'). Create a template " +
            "with a file field in Secret Server, or tell me its exact name and I'll target it.");
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

    /// <summary>
    /// List ALL folders (id, name, parentFolderId) by paging through /folders. Used to build
    /// an in-memory folder map so we reuse existing folders instead of creating duplicates.
    /// </summary>
    public async Task<List<(long Id, string Name, long ParentId)>> ListAllFoldersAsync(CancellationToken ct = default)
    {
        var all = new List<(long, string, long)>();
        var skip = 0;
        while (true)
        {
            var req = SsRequest(HttpMethod.Get, $"/folders?take=1000&skip={skip}");
            using var resp = await _http.SendAsync(req, ct);
            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) break;
            var body = await EnsureOk(resp, "GET", "/folders", ct);
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("records", out var records)) break;
            var count = 0;
            foreach (var rec in records.EnumerateArray())
            {
                count++;
                if (!rec.TryGetProperty("id", out var idEl) || !idEl.TryGetInt64(out var id)) continue;
                var name = rec.TryGetProperty("folderName", out var fn) ? fn.GetString() ?? "" : "";
                var parent = rec.TryGetProperty("parentFolderId", out var pp) && pp.TryGetInt64(out var pid)
                    ? pid : -1;
                all.Add((id, name, parent));
            }
            var hasNext = doc.RootElement.TryGetProperty("hasNext", out var hn) && hn.ValueKind == JsonValueKind.True;
            if (!hasNext || count == 0) break;
            skip += count;
        }
        return all;
    }

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

    // ---- Folder permissions ------------------------------------------------------------------
    // Added for the CyberArk source, which is the first source with a permission model to carry
    // across. PAS folder sets have no member ACL, so nothing before this needed them.

    /// <summary>
    /// Resolve a principal name to a user or group by EXACT match, users first.
    ///
    /// filter.searchText is a SUBSTRING search, so its results must be filtered client-side. The
    /// original PowerShell called this out explicitly and it is worth repeating: taking the first
    /// search hit would grant a different account access to the folder. A name that matches more
    /// than one principal exactly is reported as ambiguous rather than guessed at.
    ///
    /// Users are searched before groups, matching the source toolkit. CyberArk's own memberType
    /// is deliberately NOT used to pick which to search: it reflects how CyberArk classified the
    /// principal, which need not match how Secret Server holds it.
    /// </summary>
    public async Task<PrincipalLookup> FindPrincipalAsync(string name, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name)) return PrincipalLookup.NotFound();
        var enc = Uri.EscapeDataString(name);

        var users = await SearchPrincipalsAsync(
            $"/users?filter.searchText={enc}", name, isUser: true,
            nameProps: ["userName", "displayName"], ct);
        if (users.Count == 1) return PrincipalLookup.Single(users[0]);
        if (users.Count > 1) return PrincipalLookup.TooMany(users.Count);

        var groups = await SearchPrincipalsAsync(
            $"/groups?filter.searchText={enc}", name, isUser: false,
            nameProps: ["groupName", "name"], ct);
        if (groups.Count == 1) return PrincipalLookup.Single(groups[0]);
        if (groups.Count > 1) return PrincipalLookup.TooMany(groups.Count);

        return PrincipalLookup.NotFound();
    }

    private async Task<List<PrincipalRef>> SearchPrincipalsAsync(
        string path, string wanted, bool isUser, string[] nameProps, CancellationToken ct)
    {
        var matches = new List<PrincipalRef>();

        var req = SsRequest(HttpMethod.Get, path);
        using var resp = await _http.SendAsync(req, ct);
        // A search that matches nothing can come back 404 on SS Cloud — that is "none", not an error.
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return matches;
        var body = await EnsureOk(resp, "GET", path, ct);

        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("records", out var records)) return matches;

        foreach (var rec in records.EnumerateArray())
        {
            if (!rec.TryGetProperty("id", out var idEl) || !idEl.TryGetInt64(out var id)) continue;

            foreach (var prop in nameProps)
            {
                if (!rec.TryGetProperty(prop, out var nv) || nv.ValueKind != JsonValueKind.String) continue;
                var candidate = nv.GetString();
                if (!string.Equals(candidate, wanted, StringComparison.OrdinalIgnoreCase)) continue;

                // Dedupe: userName and displayName can both match the same record.
                if (matches.All(m => m.Id != id))
                    matches.Add(new PrincipalRef(id, isUser, candidate!));
                break;
            }
        }
        return matches;
    }

    /// <summary>
    /// POST /folder-permissions. The endpoint takes role NAMES ("View", "Add Secret", "Edit",
    /// "Owner" for the folder; "List", "View", "Edit", "Owner" for secrets) rather than ids, and
    /// exactly one of userId / groupId.
    /// </summary>
    public async Task AssignFolderPermissionAsync(
        long folderId, PrincipalRef principal, string folderAccessRoleName,
        string secretAccessRoleName, CancellationToken ct = default)
    {
        var body = new Dictionary<string, object?>
        {
            ["folderId"] = folderId,
            ["folderAccessRoleName"] = folderAccessRoleName,
            ["secretAccessRoleName"] = secretAccessRoleName,
        };
        if (principal.IsUser) body["userId"] = principal.Id;
        else body["groupId"] = principal.Id;

        var req = SsRequest(HttpMethod.Post, "/folder-permissions");
        req.Content = JsonBody(body);
        using var resp = await _http.SendAsync(req, ct);
        await EnsureOk(resp, "POST", "/folder-permissions", ct);
    }

    public async Task<long?> CurrentUserIdAsync(CancellationToken ct = default)
    {
        var req = SsRequest(HttpMethod.Get, "/users/current");
        using var resp = await _http.SendAsync(req, ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        var body = await EnsureOk(resp, "GET", "/users/current", ct);

        using var doc = JsonDocument.Parse(body);
        foreach (var prop in new[] { "id", "userId" })
            if (doc.RootElement.TryGetProperty(prop, out var v) && v.TryGetInt64(out var id))
                return id;
        return null;
    }

    public async Task<bool> RemoveUserFolderPermissionAsync(
        long folderId, long userId, CancellationToken ct = default)
    {
        var path = $"/folder-permissions?filter.folderId={folderId}&filter.userId={userId}";
        var req = SsRequest(HttpMethod.Get, path);
        using var resp = await _http.SendAsync(req, ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return false;
        var body = await EnsureOk(resp, "GET", path, ct);

        var ids = new List<long>();
        using (var doc = JsonDocument.Parse(body))
        {
            if (doc.RootElement.TryGetProperty("records", out var records))
                foreach (var rec in records.EnumerateArray())
                    if (rec.TryGetProperty("id", out var idEl) && idEl.TryGetInt64(out var pid))
                        ids.Add(pid);
        }
        if (ids.Count == 0) return false;

        var removed = false;
        foreach (var pid in ids)
        {
            var delPath = $"/folder-permissions/{pid}";
            var delReq = SsRequest(HttpMethod.Delete, delPath);
            using var delResp = await _http.SendAsync(delReq, ct);
            if (delResp.IsSuccessStatusCode) removed = true;
        }
        return removed;
    }

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
