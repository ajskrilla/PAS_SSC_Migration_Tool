using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using PasMigration.Connectors;
using PasMigration.Data;
using Xunit;

namespace PasMigration.UnitTests;

/// <summary>
/// Unit tests for <see cref="CredentialResolver"/> — the extracted vault-load + merge logic
/// that every credential-consuming endpoint (/inventory/run, /templates, /migrate, /revert)
/// now flows through. These lock in the precedence rules (stored wins when present, caller
/// value is fallback) and the restart path (persisted AES-GCM ciphertext is loaded into the
/// vault on demand). Uses the REAL CredentialVault and CredentialEncryptionService with a fake
/// ICredentialRepository, so the crypto round-trip is exercised without a database.
/// </summary>
public class CredentialResolverTests
{
    private static readonly Guid Eng = Guid.Parse("33333333-3333-3333-3333-333333333333");

    // ── Test doubles / builders ─────────────────────────────────────────────────────

    private sealed class FakeCredentialRepository : ICredentialRepository
    {
        public List<StoredCredentialBlob> Blobs { get; } = new();
        public int LoadCalls { get; private set; }

        public Task<Guid?> FindConnectionIdAsync(Guid engagementId, string role, CancellationToken ct = default) =>
            Task.FromResult<Guid?>(null);

        public Task UpsertCiphertextAsync(Guid tenantConnectionId, string kmsKeyId, byte[] ciphertext, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<StoredCredentialBlob>> GetBlobsByEngagementAsync(Guid engagementId, CancellationToken ct = default)
        {
            LoadCalls++;
            return Task.FromResult<IReadOnlyList<StoredCredentialBlob>>(Blobs);
        }

        public Task DeleteByConnectionAsync(Guid tenantConnectionId, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private static (CredentialResolver Resolver, CredentialVault Vault,
                    CredentialEncryptionService Enc, FakeCredentialRepository Repo) Make()
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Auth__JwtSecret"] = "unit-test-secret-at-least-32-characters!!",
        }).Build();
        var repo  = new FakeCredentialRepository();
        var vault = new CredentialVault(TimeSpan.FromMinutes(60));
        var enc   = new CredentialEncryptionService(repo, cfg, NullLogger<CredentialEncryptionService>.Instance);
        return (new CredentialResolver(vault, enc), vault, enc, repo);
    }

    private static SessionCredentials StoredCreds(
        string? baseUrl = "https://stored.example", string? appId = "stored-app",
        string clientId = "stored-client", string clientSecret = "stored-secret",
        string? platformBaseUrl = null, string? secretServerBaseUrl = null,
        string? username = "stored-user", string? scope = "stored-scope") =>
        new("pas", "platform_client_credentials", baseUrl, platformBaseUrl, secretServerBaseUrl,
            appId, clientId, clientSecret, username, scope);

    private static RunInventoryInput InventoryInput(string role = "source") =>
        new(role, "pas", "platform_client_credentials",
            BaseUrl: "https://sent.example", PlatformBaseUrl: "https://sent-platform.example",
            SecretServerBaseUrl: null, AppId: "sent-app",
            ClientId: "sent-client", ClientSecret: "sent-secret",
            Username: "sent-user", Scope: "sent-scope");

    // ── Precedence ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Stored_values_win_over_sent_values()
    {
        var (resolver, vault, _, _) = Make();
        vault.Put(Eng, "source", StoredCreds());

        var merged = await resolver.MergeAsync(Eng, InventoryInput());

        Assert.Equal("https://stored.example", merged.BaseUrl);
        Assert.Equal("stored-client",          merged.ClientId);
        Assert.Equal("stored-secret",          merged.ClientSecret);
        Assert.Equal("stored-scope",           merged.Scope);
    }

    [Fact]
    public async Task Sent_values_kept_where_stored_is_absent()
    {
        var (resolver, vault, _, _) = Make();
        // Stored entry with null optionals and empty client id/secret — nothing to prefer.
        vault.Put(Eng, "source", StoredCreds(
            baseUrl: null, appId: null, clientId: "", clientSecret: "",
            username: null, scope: null));

        var merged = await resolver.MergeAsync(Eng, InventoryInput());

        Assert.Equal("https://sent.example", merged.BaseUrl);
        Assert.Equal("sent-app",             merged.AppId);
        Assert.Equal("sent-client",          merged.ClientId);
        Assert.Equal("sent-secret",          merged.ClientSecret);
        Assert.Equal("sent-user",            merged.Username);
        Assert.Equal("sent-scope",           merged.Scope);
    }

    [Fact]
    public async Task No_stored_credentials_returns_input_unchanged()
    {
        var (resolver, _, _, _) = Make(); // empty vault, empty repo

        var input  = InventoryInput();
        var merged = await resolver.MergeAsync(Eng, input);

        Assert.Same(input, merged);
    }

    [Fact]
    public async Task Connection_input_without_engagement_or_role_is_untouched()
    {
        var (resolver, vault, _, _) = Make();
        vault.Put(Eng, "source", StoredCreds());

        var input = new TestConnectionInput(
            "pas", "platform_client_credentials", "https://sent.example", null, null,
            "sent-app", "sent-client", "sent-secret", null, null,
            EngagementId: null, Role: null);

        var merged = await resolver.MergeAsync(input);

        Assert.Same(input, merged); // nothing to merge without an engagement+role key
    }

    // ── Dual-role migration merge ───────────────────────────────────────────────────

    [Fact]
    public async Task Migration_merge_maps_source_to_pas_and_target_to_ss()
    {
        var (resolver, vault, _, _) = Make();
        vault.Put(Eng, "source", StoredCreds(clientId: "pas-client", clientSecret: "pas-secret"));
        vault.Put(Eng, "target", StoredCreds(
            baseUrl: "https://ss.example", platformBaseUrl: "https://ss-platform.example",
            secretServerBaseUrl: "https://ss-sscloud.example",
            clientId: "ss-client", clientSecret: "ss-secret"));

        var input = new MigrationRunInput(
            "full", DryRun: true, StagingFolderName: null, SelectedIds: null,
            PasBaseUrl: null, PasAppId: null, PasClientId: "", PasClientSecret: "", PasScope: null,
            SsBaseUrl: null, SsPlatformBaseUrl: null, SsSecretServerBaseUrl: null,
            SsClientId: "", SsClientSecret: "");

        var merged = await resolver.MergeAsync(Eng, input);

        Assert.Equal("pas-client",                    merged.PasClientId);
        Assert.Equal("pas-secret",                    merged.PasClientSecret);
        Assert.Equal("https://ss.example",            merged.SsBaseUrl);
        Assert.Equal("https://ss-platform.example",   merged.SsPlatformBaseUrl);
        Assert.Equal("https://ss-sscloud.example",    merged.SsSecretServerBaseUrl);
        Assert.Equal("ss-client",                     merged.SsClientId);
        Assert.Equal("ss-secret",                     merged.SsClientSecret);
    }

    [Fact]
    public async Task Pas_base_url_falls_back_to_platform_base_url()
    {
        var (resolver, vault, _, _) = Make();
        // Stored source has no BaseUrl but does have PlatformBaseUrl — the documented chain
        // is BaseUrl ?? PlatformBaseUrl ?? sent value.
        vault.Put(Eng, "source", StoredCreds(baseUrl: null, platformBaseUrl: "https://platform.example"));

        var input = new MigrationRunInput(
            "full", true, null, null,
            PasBaseUrl: "https://sent-pas.example", PasAppId: null, PasClientId: "", PasClientSecret: "", PasScope: null,
            SsBaseUrl: null, SsPlatformBaseUrl: null, SsSecretServerBaseUrl: null, SsClientId: "", SsClientSecret: "");

        var merged = await resolver.MergeAsync(Eng, input);

        Assert.Equal("https://platform.example", merged.PasBaseUrl);
    }

    // ── Restart path: persisted ciphertext is loaded on demand ─────────────────────

    [Fact]
    public async Task Persisted_ciphertext_is_loaded_into_empty_vault_and_merged()
    {
        var (resolver, _, enc, repo) = Make(); // vault empty — simulates post-restart state

        // Persist a real AES-256-GCM blob the way /connections/test does.
        repo.Blobs.Add(new StoredCredentialBlob("source", enc.Encrypt(Eng, StoredCreds())));

        var merged = await resolver.MergeAsync(Eng, InventoryInput());

        Assert.Equal("https://stored.example", merged.BaseUrl);
        Assert.Equal("stored-secret",          merged.ClientSecret);
        Assert.True(repo.LoadCalls >= 1); // the resolver went to persistence, not just memory
    }
}
