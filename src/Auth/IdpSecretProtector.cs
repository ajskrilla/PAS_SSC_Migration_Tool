using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace PasMigration.Auth;

/// <summary>
/// Encrypts/decrypts OIDC client secrets for at-rest storage in
/// <c>identity_provider.client_secret_enc</c>. Deliberately mirrors
/// <c>CredentialEncryptionService</c>: AES-256-GCM, key = HKDF-SHA256(AUTH_JWT_SECRET,
/// salt = provider id, info = "pas-migration-idp-secret-v1"), blob = [12 nonce][16 tag][ct].
/// The distinct info string guarantees these keys can never collide with tenant-credential
/// keys even for an identical salt GUID.
///
/// Rotation coupling (same as tenant credentials): the key derives from AUTH_JWT_SECRET, so
/// rotating that secret invalidates stored client secrets — the admin must re-enter them.
/// The planned re-key tool must cover this table too.
/// </summary>
public sealed class IdpSecretProtector(IConfiguration cfg, ILogger<IdpSecretProtector> log)
{
    private byte[] DeriveKey(Guid providerId)
    {
        var secret = cfg["Auth__JwtSecret"]
                  ?? Environment.GetEnvironmentVariable("AUTH_JWT_SECRET")
                  ?? throw new InvalidOperationException("AUTH_JWT_SECRET is not set.");

        var ikm  = Encoding.UTF8.GetBytes(secret);
        var salt = providerId.ToByteArray();
        var info = Encoding.UTF8.GetBytes("pas-migration-idp-secret-v1");

        return HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 32, salt, info);
    }

    public byte[] Protect(Guid providerId, string clientSecret)
    {
        var key       = DeriveKey(providerId);
        var plaintext = Encoding.UTF8.GetBytes(clientSecret);

        var nonce = new byte[AesGcm.NonceByteSizes.MaxSize]; // 12 bytes
        RandomNumberGenerator.Fill(nonce);

        var ciphertext = new byte[plaintext.Length];
        var tag        = new byte[AesGcm.TagByteSizes.MaxSize]; // 16 bytes

        using var aes = new AesGcm(key, AesGcm.TagByteSizes.MaxSize);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);

        // Format: [12 nonce][16 tag][ciphertext]
        var result = new byte[nonce.Length + tag.Length + ciphertext.Length];
        Buffer.BlockCopy(nonce,      0, result, 0,                         nonce.Length);
        Buffer.BlockCopy(tag,        0, result, nonce.Length,              tag.Length);
        Buffer.BlockCopy(ciphertext, 0, result, nonce.Length + tag.Length, ciphertext.Length);
        return result;
    }

    /// <summary>Null on any failure (wrong key, tampered blob, truncated) — callers treat a
    /// null as "secret must be re-entered", never as an empty secret.</summary>
    public string? Unprotect(Guid providerId, byte[] blob)
    {
        try
        {
            var key = DeriveKey(providerId);
            const int nonceLen = 12, tagLen = 16;
            if (blob.Length < nonceLen + tagLen) return null;

            var nonce      = blob[..nonceLen];
            var tag        = blob[nonceLen..(nonceLen + tagLen)];
            var ciphertext = blob[(nonceLen + tagLen)..];
            var plaintext  = new byte[ciphertext.Length];

            using var aes = new AesGcm(key, tagLen);
            aes.Decrypt(nonce, ciphertext, tag, plaintext);

            return Encoding.UTF8.GetString(plaintext);
        }
        catch (Exception ex)
        {
            log.LogWarning("IdP client secret decryption failed: {Message}", ex.Message);
            return null;
        }
    }
}
