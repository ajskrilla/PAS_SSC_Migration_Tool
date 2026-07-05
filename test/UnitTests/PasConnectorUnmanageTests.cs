using System.Net;
using System.Text;
using PasMigration.Connectors;
using Xunit;

namespace PasMigration.UnitTests;

/// <summary>
/// Regression tests for <see cref="PasConnector.UnmanageAccountAsync"/> — the read-modify-write fix.
///
/// The bug: PAS <c>UpdateAccount</c> rejects a minimal <c>{ID, IsManaged=false}</c> body with
/// HTTP 200 + <c>success=false</c> ("Invalid arguments"), and the old code trusted the status code,
/// so a failed unmanage looked like success (silent no-op). These tests lock in that the connector
/// now (a) reads the full object, (b) posts it back, and (c) THROWS when the response body says
/// <c>success=false</c> — using a fake HttpMessageHandler, no network.
/// </summary>
public class PasConnectorUnmanageTests
{
    // ── Fake transport ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Routes requests by URL path and returns canned PAS responses. The UpdateAccount response is
    /// configurable so each test can simulate success vs. the 200+success=false failure.
    /// </summary>
    private sealed class FakePasHandler : HttpMessageHandler
    {
        public string UpdateAccountBody { get; set; } = """{"success":true}""";
        public string VerifyIsManagedBody { get; set; } =
            """{"success":true,"Result":{"Results":[{"Row":{"ID":"acc-1","IsManaged":false}}]}}""";
        public bool SawUpdateAccount { get; private set; }
        public string? LastUpdateAccountRequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            string body;

            if (path.Contains("/oauth2/token/"))
            {
                body = """{"access_token":"fake-token","expires_in":3600}""";
            }
            else if (path.Contains("/GetAllAccountInformation"))
            {
                // Full account object nested at Result.VaultAccount.Row, incl. _STAMP + _TableName.
                body = """
                {"success":true,"Result":{"VaultAccount":{"Row":{
                    "_STAMP":"stamp-guid","_RowKey":"rowkey-guid","_TableName":"pvaccount",
                    "ID":"acc-1","IsManaged":true,"User":"rotateme","Name":"G.COM"
                }}}}
                """;
            }
            else if (path.Contains("/UpdateAccount"))
            {
                SawUpdateAccount = true;
                LastUpdateAccountRequestBody =
                    request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
                body = UpdateAccountBody;
            }
            else if (path.Contains("/RedRock/Query"))
            {
                body = VerifyIsManagedBody;
            }
            else
            {
                body = """{"success":true}""";
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        }
    }

    private static async Task<PasConnector> AuthedConnector(FakePasHandler handler)
    {
        var http = new HttpClient(handler) { BaseAddress = null };
        var pas  = new PasConnector(http, "https://tenant.example.net", "app-id");
        using var creds = new TenantCredentials { ClientId = "cid", ClientSecret = "secret", Scope = null };
        await pas.AuthenticateAsync(creds);
        return pas;
    }

    // ── Tests ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Unmanage_throws_when_body_reports_success_false()
    {
        // The exact bug shape: HTTP 200 but success=false.
        var handler = new FakePasHandler
        {
            UpdateAccountBody = """{"success":false,"Message":"Invalid arguments passed to the server."}"""
        };
        var pas = await AuthedConnector(handler);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => pas.UnmanageAccountAsync("acc-1"));

        Assert.Contains("Invalid arguments", ex.Message);
        Assert.True(handler.SawUpdateAccount);   // it really did attempt the write
    }

    [Fact]
    public async Task Unmanage_posts_full_object_with_IsManaged_false()
    {
        var handler = new FakePasHandler();   // defaults to success + IsManaged reads back false
        var pas = await AuthedConnector(handler);

        await pas.UnmanageAccountAsync("acc-1");

        // The write body must be the FULL object (carrying _STAMP), not a minimal {ID,IsManaged}.
        Assert.NotNull(handler.LastUpdateAccountRequestBody);
        Assert.Contains("_STAMP", handler.LastUpdateAccountRequestBody!);
        Assert.Contains("\"IsManaged\":false", handler.LastUpdateAccountRequestBody!);
        // _TableName is dropped before the write (matches the portal's body).
        Assert.DoesNotContain("_TableName", handler.LastUpdateAccountRequestBody!);
    }

    [Fact]
    public async Task Unmanage_throws_when_verify_shows_still_managed()
    {
        // Write "succeeds" but the re-read shows IsManaged still true → must throw (no silent no-op).
        var handler = new FakePasHandler
        {
            UpdateAccountBody   = """{"success":true}""",
            VerifyIsManagedBody = """{"success":true,"Result":{"Results":[{"Row":{"ID":"acc-1","IsManaged":true}}]}}"""
        };
        var pas = await AuthedConnector(handler);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => pas.UnmanageAccountAsync("acc-1"));

        Assert.Contains("still true", ex.Message);
    }

    [Fact]
    public async Task Unmanage_succeeds_on_clean_flow()
    {
        var handler = new FakePasHandler();   // success write + verify reads back false
        var pas = await AuthedConnector(handler);

        // Should complete without throwing.
        await pas.UnmanageAccountAsync("acc-1");

        Assert.True(handler.SawUpdateAccount);
    }
}
