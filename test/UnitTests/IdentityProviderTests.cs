using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using PasMigration.Auth;
using Xunit;

namespace PasMigration.UnitTests;

/// <summary>
/// Unit tests for the SSO groundwork: <see cref="IdpSecretProtector"/> (AES-256-GCM round-trip,
/// wrong-key and tamper rejection) and <see cref="IdentityProviderInput.Validate"/> (the admin
/// CRUD input rules). Pure in-memory — no database, no network.
/// </summary>
public class IdentityProviderTests
{
    private static readonly Guid ProviderA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid ProviderB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    private static IdpSecretProtector MakeProtector()
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Auth__JwtSecret"] = "unit-test-secret-at-least-32-characters!!",
        }).Build();
        return new IdpSecretProtector(cfg, NullLogger<IdpSecretProtector>.Instance);
    }

    // ── IdpSecretProtector ──────────────────────────────────────────────────────────

    [Fact]
    public void Protect_then_unprotect_round_trips()
    {
        var protector = MakeProtector();

        var blob = protector.Protect(ProviderA, "s3cr3t-client-value");

        Assert.Equal("s3cr3t-client-value", protector.Unprotect(ProviderA, blob));
        // Nonce (12) + tag (16) prefix the ciphertext.
        Assert.True(blob.Length > 28);
    }

    [Fact]
    public void Unprotect_with_wrong_provider_id_fails()
    {
        // The key is HKDF-salted with the provider id — provider B's key can't open A's blob.
        var protector = MakeProtector();
        var blob = protector.Protect(ProviderA, "s3cr3t-client-value");

        Assert.Null(protector.Unprotect(ProviderB, blob));
    }

    [Fact]
    public void Unprotect_rejects_tampered_ciphertext()
    {
        var protector = MakeProtector();
        var blob = protector.Protect(ProviderA, "s3cr3t-client-value");
        blob[^1] ^= 0xFF;  // flip a bit in the ciphertext — GCM tag must reject it

        Assert.Null(protector.Unprotect(ProviderA, blob));
    }

    [Fact]
    public void Unprotect_rejects_truncated_blob()
    {
        var protector = MakeProtector();

        Assert.Null(protector.Unprotect(ProviderA, new byte[10])); // shorter than nonce+tag
    }

    // ── IdentityProviderInput.Validate ──────────────────────────────────────────────

    private static IdentityProviderInput ValidInput() => new(
        Name: "Contoso Entra ID", Slug: "contoso-entra",
        Authority: "https://login.microsoftonline.com/tenant-id/v2.0",
        ClientId: "client-abc", ClientSecret: "secret");

    [Fact]
    public void Valid_input_passes()
    {
        Assert.Null(ValidInput().Validate());
    }

    [Theory]
    [InlineData("Has Spaces")]
    [InlineData("UPPER")]
    [InlineData("x")]           // too short
    [InlineData("-leading")]    // must start alphanumeric
    public void Invalid_slugs_are_rejected(string slug)
    {
        Assert.NotNull((ValidInput() with { Slug = slug }).Validate());
    }

    [Fact]
    public void Non_https_authority_is_rejected()
    {
        Assert.NotNull((ValidInput() with { Authority = "http://idp.example.com" }).Validate());
    }

    [Fact]
    public void Http_localhost_authority_is_allowed_for_testing()
    {
        Assert.Null((ValidInput() with { Authority = "http://localhost:8081/realms/test" }).Validate());
    }

    [Fact]
    public void Unknown_default_role_is_rejected()
    {
        Assert.NotNull((ValidInput() with { DefaultRole = "superuser" }).Validate());
    }

    [Fact]
    public void Malformed_role_mappings_json_is_rejected()
    {
        Assert.NotNull((ValidInput() with { RoleMappingsJson = "{not json" }).Validate());
        Assert.Null((ValidInput() with { RoleMappingsJson = "{\"PS-Admins\":\"operator\"}" }).Validate());
    }
}
