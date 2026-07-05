using System.Data;
using Dapper;

namespace PasMigration.Data;

/// <summary>
/// One decrypted-blob row keyed by tenant role, as stored in <c>encrypted_credential</c>.
/// <see cref="Ciphertext"/> is the raw AES-256-GCM blob (<c>[12 nonce][16 tag][ciphertext]</c>) —
/// this repository treats it as opaque bytes and never interprets or transforms it.
/// </summary>
public sealed record StoredCredentialBlob(string Role, byte[] Ciphertext);

/// <summary>
/// Data-access seam for credential persistence: the <c>encrypted_credential</c> table plus the
/// <c>tenant_connection</c> id lookups that credential save/load depend on. The only place this SQL
/// lives.
///
/// Security boundary (Option 1): this repository performs NO cryptography. AES-256-GCM
/// encryption/decryption and HKDF key derivation stay in <c>CredentialEncryptionService</c>. The
/// repository only moves already-encrypted bytes in and out of the database — it never encrypts,
/// decrypts, or derives keys, and it never sees a plaintext credential.
/// </summary>
public interface ICredentialRepository
{
    /// <summary>Resolves the tenant_connection id for an engagement+role. Null if none exists.</summary>
    Task<Guid?> FindConnectionIdAsync(Guid engagementId, string role, CancellationToken ct = default);

    /// <summary>Upserts the encrypted blob for a tenant_connection (one row per connection).</summary>
    Task UpsertCiphertextAsync(Guid tenantConnectionId, string kmsKeyId, byte[] ciphertext, CancellationToken ct = default);

    /// <summary>All stored blobs for an engagement, tagged with their tenant role.</summary>
    Task<IReadOnlyList<StoredCredentialBlob>> GetBlobsByEngagementAsync(Guid engagementId, CancellationToken ct = default);

    /// <summary>Removes the stored credential for a tenant_connection.</summary>
    Task DeleteByConnectionAsync(Guid tenantConnectionId, CancellationToken ct = default);
}

public sealed class CredentialRepository(IDbConnection db) : ICredentialRepository
{
    public async Task<Guid?> FindConnectionIdAsync(Guid engagementId, string role, CancellationToken ct = default) =>
        await db.ExecuteScalarAsync<Guid?>(new CommandDefinition(
            "SELECT id FROM tenant_connection WHERE engagement_id=@e AND role=@r LIMIT 1",
            new { e = engagementId, r = role }, cancellationToken: ct));

    public async Task UpsertCiphertextAsync(Guid tenantConnectionId, string kmsKeyId, byte[] ciphertext, CancellationToken ct = default) =>
        await db.ExecuteAsync(new CommandDefinition(
            @"INSERT INTO encrypted_credential (id, tenant_connection_id, kms_key_id, ciphertext)
              VALUES (uuid_generate_v4(), @tcid, @kms, @ct)
              ON CONFLICT (tenant_connection_id)
              DO UPDATE SET ciphertext=@ct, created_at=now()",
            new { tcid = tenantConnectionId, kms = kmsKeyId, ct = ciphertext }, cancellationToken: ct));

    public async Task<IReadOnlyList<StoredCredentialBlob>> GetBlobsByEngagementAsync(Guid engagementId, CancellationToken ct = default)
    {
        var rows = await db.QueryAsync(new CommandDefinition(
            @"SELECT tc.role, ec.ciphertext
              FROM encrypted_credential ec
              JOIN tenant_connection tc ON tc.id = ec.tenant_connection_id
              WHERE tc.engagement_id = @id",
            new { id = engagementId }, cancellationToken: ct));

        var list = new List<StoredCredentialBlob>();
        foreach (var row in rows.Cast<IDictionary<string, object?>>())
        {
            var role = row["role"]?.ToString() ?? "";
            if (row["ciphertext"] is byte[] blob)
                list.Add(new StoredCredentialBlob(role, blob));
        }
        return list;
    }

    public async Task DeleteByConnectionAsync(Guid tenantConnectionId, CancellationToken ct = default) =>
        await db.ExecuteAsync(new CommandDefinition(
            "DELETE FROM encrypted_credential WHERE tenant_connection_id = @id",
            new { id = tenantConnectionId }, cancellationToken: ct));
}
