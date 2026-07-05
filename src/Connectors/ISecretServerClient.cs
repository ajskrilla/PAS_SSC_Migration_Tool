using Microsoft.Extensions.Logging;

namespace PasMigration.Connectors;

/// <summary>
/// Abstraction over the Secret Server (Cloud) API client. Extracted so services depend on this
/// interface rather than constructing <see cref="SecretServerConnector"/> directly, making the
/// calling services unit-testable with a fake client.
///
/// Behavior is unchanged: <see cref="SecretServerConnector"/> is the sole production implementation
/// and its methods are exactly as before. The <c>AuthMode</c> enum remains nested on the concrete
/// connector (all call sites already reference it as <c>SecretServerConnector.AuthMode</c>);
/// authentication stays an explicit call the caller makes after obtaining a client.
/// </summary>
public interface ISecretServerClient
{
    Task AuthenticatePlatformAsync(TenantCredentials creds, CancellationToken ct = default);
    Task AuthenticateLegacyAsync(string username, string password, CancellationToken ct = default);
    Task<long?> FindFolderAsync(string name, long parentFolderId, CancellationToken ct = default);
    Task<long> CreateFolderAsync(string name, long parentFolderId, CancellationToken ct = default);
    Task<long> EnsureFolderAsync(string name, long parentFolderId, CancellationToken ct = default);
    Task<List<(long Id, string Name)>> ListTemplatesAsync(CancellationToken ct = default);
    Task<bool> TemplateHasFileFieldAsync(long templateId, long folderId, CancellationToken ct = default);
    Task<long> CreateFileTemplateAsync(string name, CancellationToken ct = default);
    Task<long?> FindTemplateAsync(string name, CancellationToken ct = default);
    Task<long> CreateSecretAsync(
        string name, long templateId, long folderId,
        IReadOnlyDictionary<string, string> textValuesBySlug,
        (string Slug, string Filename, string Base64)? fileField,
        CancellationToken ct = default);
    Task<long> EnsureFileMigrationTemplateAsync(CancellationToken ct = default);
    Task<bool> SecretHasFileAttachmentAsync(long secretId, CancellationToken ct = default);
    Task<List<(long Id, string Name, long ParentId)>> ListAllFoldersAsync(CancellationToken ct = default);
    Task<bool> DeleteSecretAsync(long secretId, CancellationToken ct = default);
    Task<bool> DeleteFolderAsync(long folderId, CancellationToken ct = default);
    Task<List<Dictionary<string, object?>>> InventoryFoldersAsync(CancellationToken ct = default);
    Task<List<Dictionary<string, object?>>> InventorySecretsAsync(CancellationToken ct = default);
}

/// <summary>
/// Creates <see cref="ISecretServerClient"/> instances bound to a specific tenant. Owns HttpClient
/// acquisition (via the injected <see cref="IHttpClientFactory"/> "tenant" client), so callers no
/// longer construct connectors or touch <c>IHttpClientFactory</c> for this purpose. A fake factory
/// can be injected in tests to return a fake client.
/// </summary>
public interface ISecretServerConnectorFactory
{
    ISecretServerClient Create(string platformBaseUrl, string secretServerBaseUrl,
                               SecretServerConnector.AuthMode authMode);
}

public sealed class SecretServerConnectorFactory(IHttpClientFactory httpFactory) : ISecretServerConnectorFactory
{
    public ISecretServerClient Create(string platformBaseUrl, string secretServerBaseUrl,
                                      SecretServerConnector.AuthMode authMode)
    {
        var http = httpFactory.CreateClient("tenant");
        return new SecretServerConnector(http, platformBaseUrl, secretServerBaseUrl, authMode);
    }
}
