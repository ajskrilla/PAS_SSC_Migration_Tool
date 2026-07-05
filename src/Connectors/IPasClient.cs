using Microsoft.Extensions.Logging;

namespace PasMigration.Connectors;

/// <summary>
/// Abstraction over the PAS (Centrify) tenant API client. Extracted so services depend on this
/// interface rather than constructing <see cref="PasConnector"/> directly, which makes the calling
/// services unit-testable with a fake client.
///
/// Behavior is unchanged: <see cref="PasConnector"/> is the sole production implementation and its
/// methods are exactly as before. Authentication is still an explicit call the caller makes after
/// obtaining a client (the factory does not authenticate).
/// </summary>
public interface IPasClient
{
    Task AuthenticateAsync(TenantCredentials creds, CancellationToken ct = default);
    Task<List<Dictionary<string, object?>>> QueryAsync(string sql, CancellationToken ct = default);
    Task<(string Password, string CheckoutId)> CheckoutPasswordAsync(string accountId, CancellationToken ct = default);
    Task CheckinPasswordAsync(string checkoutId, CancellationToken ct = default);
    Task<string> RetrieveTextSecretAsync(string id, CancellationToken ct = default);
    Task<byte[]> DownloadFileSecretAsync(string id, CancellationToken ct = default);
    Task UnmanageAccountAsync(string id, CancellationToken ct = default);
    Task<List<Dictionary<string, object?>>> InventoryLocalAccountsAsync(CancellationToken ct = default);
    Task<List<Dictionary<string, object?>>> InventoryDomainAccountsAsync(CancellationToken ct = default);
    Task<List<Dictionary<string, object?>>> InventoryMultiplexedAccountsAsync(CancellationToken ct = default);
    Task<List<Dictionary<string, object?>>> InventoryLocalAccountsNoJoinAsync(CancellationToken ct = default);
    Task<List<Dictionary<string, object?>>> InventorySecretsAsync(CancellationToken ct = default);
    Task<List<Dictionary<string, object?>>> InventoryFolderSetsAsync(CancellationToken ct = default);
}

/// <summary>
/// Creates <see cref="IPasClient"/> instances bound to a specific tenant. Owns HttpClient
/// acquisition (via the injected <see cref="IHttpClientFactory"/> "tenant" client), so callers no
/// longer construct connectors or touch <c>IHttpClientFactory</c> for this purpose. A fake factory
/// can be injected in tests to return a fake client.
/// </summary>
public interface IPasConnectorFactory
{
    IPasClient Create(string tenantBaseUrl, string appId);
}

public sealed class PasConnectorFactory(IHttpClientFactory httpFactory) : IPasConnectorFactory
{
    public IPasClient Create(string tenantBaseUrl, string appId)
    {
        var http = httpFactory.CreateClient("tenant");
        return new PasConnector(http, tenantBaseUrl, appId);
    }
}
