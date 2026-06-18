using System.Net;
using System.Security.Authentication;
using PasMigration.Connectors;

// Standalone test: exercises the REAL PasConnector against a live tenant.
// Reads creds from args so nothing is hardcoded:
//   dotnet run -- <tenantBaseUrl> <appId> <clientId> <clientSecret> <scope>

if (args.Length < 4)
{
    Console.WriteLine("Usage: dotnet run -- <tenantBaseUrl> <appId> <clientId> <clientSecret> [scope]");
    return 1;
}
var (baseUrl, appId, clientId, clientSecret) = (args[0], args[1], args[2], args[3]);
var scope = args.Length > 4 ? args[4] : null;

var handler = new HttpClientHandler { SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13 };
using var http = new HttpClient(handler);
var pas = new PasConnector(http, baseUrl, appId);

Console.WriteLine("== Authenticating ==");
using var creds = new TenantCredentials { ClientId = clientId, ClientSecret = clientSecret, Scope = scope };
await pas.AuthenticateAsync(creds);
Console.WriteLine("  OK");

Console.WriteLine("== Querying one Text secret ==");
var rows = await pas.QueryAsync("SELECT ID, SecretName FROM DataVault WHERE Type='Text'");
if (rows.Count == 0) { Console.WriteLine("  No text secrets found."); return 0; }
// take first
var first = rows[0];
var id = first.TryGetValue("ID", out var v) ? v?.ToString() : null;
Console.WriteLine($"  First secret ID = {id}  (name={first.GetValueOrDefault("SecretName")})");

Console.WriteLine("== Retrieving its contents (THE call that fails in the app) ==");
try
{
    var text = await pas.RetrieveTextSecretAsync(id!);
    Console.WriteLine($"  SUCCESS - retrieved {text.Length} chars.");
}
catch (Exception ex)
{
    Console.WriteLine($"  FAILED: {ex.Message}");
    return 2;
}
Console.WriteLine("== All good ==");
return 0;
