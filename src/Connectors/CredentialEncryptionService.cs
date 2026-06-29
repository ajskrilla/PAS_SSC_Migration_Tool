using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace PasMigration.Connectors;

/// <summary>
/// Encrypts and persists session credentials to the encrypted_credential table so they
/// survive container restarts. Uses AES-256-GCM with a key derived from the app secret
/// + engagement ID — no external KMS required for the default deployment.
///
/// Security posture: the encryption key is derived from AUTH_JWT_SECRET (which must be set
/// in the environment). If the secret changes, stored credentials can no longer be decrypted
/// and the operator must re-enter them on the Pre-migration page.
/// </summary>
public sealed class CredentialEncryptionService(
    IConfiguration cfg,
    ILogger<CredentialEncryptionService> log)
{
    private const string KmsKeyId = "local-aes256gcm-v1";

    // ── Derive a 32-byte AES key from the app secret + engagement ID ────────────────

    private byte[] DeriveKey(Guid engagementId)
    {
        var secret = cfg["Auth__JwtSecret"]
                  ?? Environment.GetEnvironmentVariable("AUTH_JWT_SECRET")
                  ?? "dev-secret-change-in-production-min-32-chars!!";

        // HKDF: PRK = HMAC-SHA256(salt=engagementId bytes, ikm=secret)
        // OKM = first 32 bytes → AES-256 key
        var ikm  = Encoding.UTF8.GetBytes(secret);
        var salt = engagementId.ToByteArray();
        var info = Encoding.UTF8.GetBytes("pas-migration-credential-v1");

        return HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 32, salt, info);
    }

    // ── Encrypt ──────────────────────────────────────────────────────────────────────

    public byte[] Encrypt(Guid engagementId, SessionCredentials creds)
    {
        var key      = DeriveKey(engagementId);
        var json     = JsonSerializer.Serialize(creds);
        var plaintext= Encoding.UTF8.GetBytes(json);

        var nonce      = new byte[AesGcm.NonceByteSizes.MaxSize]; // 12 bytes
        RandomNumberGenerator.Fill(nonce);

        var ciphertext = new byte[plaintext.Length];
        var tag        = new byte[AesGcm.TagByteSizes.MaxSize];   // 16 bytes

        using var aes = new AesGcm(key, AesGcm.TagByteSizes.MaxSize);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);

        // Format: [12 nonce][16 tag][ciphertext]
        var result = new byte[nonce.Length + tag.Length + ciphertext.Length];
        Buffer.BlockCopy(nonce,       0, result, 0,                             nonce.Length);
        Buffer.BlockCopy(tag,         0, result, nonce.Length,                  tag.Length);
        Buffer.BlockCopy(ciphertext,  0, result, nonce.Length + tag.Length,     ciphertext.Length);
        return result;
    }

    // ── Decrypt ──────────────────────────────────────────────────────────────────────

    public SessionCredentials? Decrypt(Guid engagementId, byte[] blob)
    {
        try
        {
            var key        = DeriveKey(engagementId);
            const int nonceLen = 12, tagLen = 16;
            if (blob.Length < nonceLen + tagLen) return null;

            var nonce      = blob[..nonceLen];
            var tag        = blob[nonceLen..(nonceLen + tagLen)];
            var ciphertext = blob[(nonceLen + tagLen)..];
            var plaintext  = new byte[ciphertext.Length];

            using var aes = new AesGcm(key, tagLen);
            aes.Decrypt(nonce, ciphertext, tag, plaintext);

            var json = Encoding.UTF8.GetString(plaintext);
            return JsonSerializer.Deserialize<SessionCredentials>(json);
        }
        catch (Exception ex)
        {
            log.LogWarning("Credential decryption failed: {Message}", ex.Message);
            return null;
        }
    }

    // ── Persist to DB ─────────────────────────────────────────────────────────────────

    public async Task SaveAsync(IDbConnection db, Guid tenantConnectionId, Guid engagementId,
                                 SessionCredentials creds)
    {
        var ciphertext = Encrypt(engagementId, creds);

        // Upsert — one row per tenant_connection
        await db.ExecuteAsync(
            @"INSERT INTO encrypted_credential (id, tenant_connection_id, kms_key_id, ciphertext)
              VALUES (uuid_generate_v4(), @tcid, @kms, @ct)
              ON CONFLICT (tenant_connection_id)
              DO UPDATE SET ciphertext=@ct, created_at=now()",
            new { tcid = tenantConnectionId, kms = KmsKeyId, ct = ciphertext });

        log.LogInformation("Credentials encrypted and persisted for connection {Id}", tenantConnectionId);
    }

    // ── Load from DB into vault ────────────────────────────────────────────────────────

    public async Task LoadIntoVaultAsync(IDbConnection db, Guid engagementId, CredentialVault vault)
    {
        var rows = await db.QueryAsync(
            @"SELECT tc.role, ec.ciphertext
              FROM encrypted_credential ec
              JOIN tenant_connection tc ON tc.id = ec.tenant_connection_id
              WHERE tc.engagement_id = @id",
            new { id = engagementId });

        foreach (var row in rows.Cast<IDictionary<string, object?>>())
        {
            var role  = row["role"]?.ToString() ?? "";
            var blob  = row["ciphertext"] as byte[];
            if (blob is null) continue;

            var creds = Decrypt(engagementId, blob);
            if (creds is not null)
            {
                vault.Put(engagementId, role, creds);
                log.LogDebug("Loaded persisted credentials for {Role}", role);
            }
        }
    }

    // ── Delete (e.g. when connection is removed) ──────────────────────────────────────

    public async Task DeleteAsync(IDbConnection db, Guid tenantConnectionId)
    {
        await db.ExecuteAsync(
            "DELETE FROM encrypted_credential WHERE tenant_connection_id = @id",
            new { id = tenantConnectionId });
    }
}
