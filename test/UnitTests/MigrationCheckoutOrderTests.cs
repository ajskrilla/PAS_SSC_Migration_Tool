using PasMigration.Connectors;
using Xunit;

namespace PasMigration.UnitTests;

/// <summary>
/// Regression tests for <see cref="MigrationService.CheckoutThenUnmanageAsync"/>.
///
/// The bug: account migration used to unmanage an account FIRST, then attempt to check out its
/// password. If PAS then refused the checkout (observed in the field as an "Uncertain passwords"
/// message), the account was left unmanaged with its password never captured — stranded, per the
/// customer's own PAS unmanage script, which documents this exact risk as the reason it never
/// touches passwords at all.
///
/// These tests lock in the fix: checkout happens first, so (a) a checkout failure never touches
/// PAS at all, and (b) an unmanage failure after a successful checkout checks the password back in
/// before the account can be left stranded. No network — a fake <see cref="IPasClient"/>.
/// </summary>
public class MigrationCheckoutOrderTests
{
    private sealed class FakePasClient : IPasClient
    {
        public bool CheckoutShouldThrow { get; set; }
        public bool UnmanageShouldThrow { get; set; }
        public string CheckoutPassword { get; set; } = "s3cret";
        public string CheckoutCoid { get; set; } = "coid-123";

        public List<string> CallOrder { get; } = new();
        public string? CheckedInCoid { get; private set; }

        public Task<(string Password, string CheckoutId)> CheckoutPasswordAsync(
            string accountId, CancellationToken ct = default)
        {
            CallOrder.Add("checkout");
            if (CheckoutShouldThrow)
                throw new InvalidOperationException("PAS password checkout refused for account " +
                    accountId + ": Uncertain passwords");
            return Task.FromResult((CheckoutPassword, CheckoutCoid));
        }

        public Task UnmanageAccountAsync(string id, CancellationToken ct = default)
        {
            CallOrder.Add("unmanage");
            if (UnmanageShouldThrow)
                throw new InvalidOperationException("UpdateAccount returned success=false");
            return Task.CompletedTask;
        }

        public Task CheckinPasswordAsync(string checkoutId, CancellationToken ct = default)
        {
            CallOrder.Add("checkin");
            CheckedInCoid = checkoutId;
            return Task.CompletedTask;
        }

        // Unused by these tests — CheckoutThenUnmanageAsync never calls them.
        public Task AuthenticateAsync(TenantCredentials creds, CancellationToken ct = default) => Task.CompletedTask;
        public Task<List<Dictionary<string, object?>>> QueryAsync(string sql, CancellationToken ct = default)
            => Task.FromResult(new List<Dictionary<string, object?>>());
        public Task<string> RetrieveTextSecretAsync(string id, CancellationToken ct = default) => Task.FromResult("");
        public Task<byte[]> DownloadFileSecretAsync(string id, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
        public Task<List<Dictionary<string, object?>>> InventoryLocalAccountsAsync(CancellationToken ct = default)
            => Task.FromResult(new List<Dictionary<string, object?>>());
        public Task<List<Dictionary<string, object?>>> InventoryDomainAccountsAsync(CancellationToken ct = default)
            => Task.FromResult(new List<Dictionary<string, object?>>());
        public Task<List<Dictionary<string, object?>>> InventoryMultiplexedAccountsAsync(CancellationToken ct = default)
            => Task.FromResult(new List<Dictionary<string, object?>>());
        public Task<List<Dictionary<string, object?>>> InventoryLocalAccountsNoJoinAsync(CancellationToken ct = default)
            => Task.FromResult(new List<Dictionary<string, object?>>());
        public Task<List<Dictionary<string, object?>>> InventorySecretsAsync(CancellationToken ct = default)
            => Task.FromResult(new List<Dictionary<string, object?>>());
        public Task<List<Dictionary<string, object?>>> InventoryFolderSetsAsync(CancellationToken ct = default)
            => Task.FromResult(new List<Dictionary<string, object?>>());
    }

    [Fact]
    public async Task HappyPath_ChecksOutBeforeUnmanaging_ReturnsPasswordAndCoid()
    {
        var fake = new FakePasClient();

        var (password, coid) = await MigrationService.CheckoutThenUnmanageAsync(fake, "acc-1", default);

        Assert.Equal("s3cret", password);
        Assert.Equal("coid-123", coid);
        Assert.Equal(new[] { "checkout", "unmanage" }, fake.CallOrder);
        Assert.Null(fake.CheckedInCoid); // not checked in on the happy path — caller owns that after use
    }

    [Fact]
    public async Task CheckoutFails_UnmanageIsNeverCalled_AccountUntouched()
    {
        var fake = new FakePasClient { CheckoutShouldThrow = true };

        var ex = await Assert.ThrowsAsync<AccountCheckoutFailedException>(
            () => MigrationService.CheckoutThenUnmanageAsync(fake, "acc-1", default));

        Assert.Contains("Uncertain passwords", ex.Message);
        Assert.Equal(new[] { "checkout" }, fake.CallOrder); // unmanage never reached
        Assert.Null(fake.CheckedInCoid);
    }

    [Fact]
    public async Task UnmanageFailsAfterCheckout_ChecksPasswordBackIn_AccountRemainsManaged()
    {
        var fake = new FakePasClient { UnmanageShouldThrow = true };

        var ex = await Assert.ThrowsAsync<AccountUnmanageFailedException>(
            () => MigrationService.CheckoutThenUnmanageAsync(fake, "acc-1", default));

        Assert.Contains("checked back in", ex.Message);
        Assert.Equal(new[] { "checkout", "unmanage", "checkin" }, fake.CallOrder);
        Assert.Equal("coid-123", fake.CheckedInCoid); // the exact checkout was released, not a stray value
    }
}
