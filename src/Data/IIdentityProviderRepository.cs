using System.Data;
using Dapper;

namespace PasMigration.Data;

/// <summary>A configured identity provider row. ClientSecretEnc is AES-256-GCM ciphertext
/// (see <c>IdpSecretProtector</c>) and must never leave the server decrypted or otherwise.
///
/// Deliberately property-based, NOT a positional record: Dapper's constructor mapping
/// requires the reader's reported field type to match each parameter exactly, and Npgsql
/// reports the <c>text[]</c> column (<c>allowed_email_domains</c>) as <c>System.Array</c>,
/// which fails to match a <c>string[]</c> parameter ("no matching signature" at runtime).
/// Property mapping assigns the actual value — a real <c>string[]</c> — so it works.</summary>
public sealed record IdentityProviderRow
{
    public Guid     Id                  { get; set; }
    public string   Name                { get; set; } = "";
    public string   Slug                { get; set; } = "";
    public string   Type                { get; set; } = "";
    public string   Authority           { get; set; } = "";
    public string   ClientId            { get; set; } = "";
    public byte[]?  ClientSecretEnc     { get; set; }
    public bool     Enabled             { get; set; }
    public bool     JitProvisioning     { get; set; }
    public string   DefaultRole         { get; set; } = "viewer";
    public string[] AllowedEmailDomains { get; set; } = [];
    public string?  RoleClaim           { get; set; }
    public string?  RoleMappingsJson    { get; set; }
    public DateTime CreatedAt           { get; set; }
}

/// <summary>Writable fields for insert/update — everything except id, type, and the secret,
/// which are handled explicitly by the callers.</summary>
public sealed record IdentityProviderWrite(
    string Name, string Slug, string Authority, string ClientId,
    bool Enabled, bool JitProvisioning, string DefaultRole,
    string[] AllowedEmailDomains, string? RoleClaim, string? RoleMappingsJson);

/// <summary>
/// Data-access seam for the <c>identity_provider</c> table. The only place its SQL lives.
/// Unlike the engagement repository (which returns dynamic rows to preserve a legacy
/// snake_case wire shape), this is a NEW table with no existing consumers, so it uses typed
/// records; the wire shape is defined by the route handlers.
/// </summary>
public interface IIdentityProviderRepository
{
    /// <summary>All providers, oldest first (stable order for the login page later).</summary>
    Task<IReadOnlyList<IdentityProviderRow>> ListAsync(CancellationToken ct = default);

    Task<IdentityProviderRow?> GetByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>True if the slug is taken by a provider other than <paramref name="excludeId"/>.</summary>
    Task<bool> SlugExistsAsync(string slug, Guid? excludeId, CancellationToken ct = default);

    /// <summary>Insert with an app-generated id (the secret encryption key is salted with it).</summary>
    Task InsertAsync(Guid id, IdentityProviderWrite w, byte[]? clientSecretEnc, CancellationToken ct = default);

    /// <summary>Update. A null <paramref name="clientSecretEnc"/> keeps the existing secret.
    /// Returns affected row count (0 = not found).</summary>
    Task<int> UpdateAsync(Guid id, IdentityProviderWrite w, byte[]? clientSecretEnc, CancellationToken ct = default);

    /// <summary>Returns affected row count (0 = not found). scim_token rows cascade;
    /// user_identity rows RESTRICT — callers must check links first.</summary>
    Task<int> DeleteAsync(Guid id, CancellationToken ct = default);
}

public sealed class IdentityProviderRepository(IDbConnection db) : IIdentityProviderRepository
{
    // Aliases map snake_case columns onto the record's constructor parameters.
    private const string Cols = @"id AS Id, name AS Name, slug AS Slug, type AS Type,
        authority AS Authority, client_id AS ClientId, client_secret_enc AS ClientSecretEnc,
        enabled AS Enabled, jit_provisioning AS JitProvisioning, default_role AS DefaultRole,
        allowed_email_domains AS AllowedEmailDomains, role_claim AS RoleClaim,
        role_mappings::text AS RoleMappingsJson, created_at AS CreatedAt";

    public async Task<IReadOnlyList<IdentityProviderRow>> ListAsync(CancellationToken ct = default) =>
        (await db.QueryAsync<IdentityProviderRow>(new CommandDefinition(
            $"SELECT {Cols} FROM identity_provider ORDER BY created_at",
            cancellationToken: ct))).AsList();

    public async Task<IdentityProviderRow?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        await db.QuerySingleOrDefaultAsync<IdentityProviderRow>(new CommandDefinition(
            $"SELECT {Cols} FROM identity_provider WHERE id = @Id",
            new { Id = id }, cancellationToken: ct));

    public async Task<bool> SlugExistsAsync(string slug, Guid? excludeId, CancellationToken ct = default) =>
        await db.ExecuteScalarAsync<bool>(new CommandDefinition(
            @"SELECT EXISTS (SELECT 1 FROM identity_provider
               WHERE slug = @Slug AND (@ExcludeId::uuid IS NULL OR id <> @ExcludeId))",
            new { Slug = slug, ExcludeId = excludeId }, cancellationToken: ct));

    public async Task InsertAsync(Guid id, IdentityProviderWrite w, byte[]? clientSecretEnc,
                                  CancellationToken ct = default) =>
        await db.ExecuteAsync(new CommandDefinition(
            @"INSERT INTO identity_provider
                (id, name, slug, authority, client_id, client_secret_enc, enabled,
                 jit_provisioning, default_role, allowed_email_domains, role_claim, role_mappings)
              VALUES
                (@Id, @Name, @Slug, @Authority, @ClientId, @ClientSecretEnc, @Enabled,
                 @JitProvisioning, @DefaultRole, @AllowedEmailDomains, @RoleClaim,
                 @RoleMappingsJson::jsonb)",
            new
            {
                Id = id, w.Name, w.Slug, w.Authority, w.ClientId, ClientSecretEnc = clientSecretEnc,
                w.Enabled, w.JitProvisioning, w.DefaultRole, w.AllowedEmailDomains,
                w.RoleClaim, w.RoleMappingsJson,
            },
            cancellationToken: ct));

    public async Task<int> UpdateAsync(Guid id, IdentityProviderWrite w, byte[]? clientSecretEnc,
                                       CancellationToken ct = default) =>
        await db.ExecuteAsync(new CommandDefinition(
            @"UPDATE identity_provider SET
                name = @Name, slug = @Slug, authority = @Authority, client_id = @ClientId,
                client_secret_enc = COALESCE(@ClientSecretEnc, client_secret_enc),
                enabled = @Enabled, jit_provisioning = @JitProvisioning,
                default_role = @DefaultRole, allowed_email_domains = @AllowedEmailDomains,
                role_claim = @RoleClaim, role_mappings = @RoleMappingsJson::jsonb
              WHERE id = @Id",
            new
            {
                Id = id, w.Name, w.Slug, w.Authority, w.ClientId, ClientSecretEnc = clientSecretEnc,
                w.Enabled, w.JitProvisioning, w.DefaultRole, w.AllowedEmailDomains,
                w.RoleClaim, w.RoleMappingsJson,
            },
            cancellationToken: ct));

    public async Task<int> DeleteAsync(Guid id, CancellationToken ct = default) =>
        await db.ExecuteAsync(new CommandDefinition(
            "DELETE FROM identity_provider WHERE id = @Id",
            new { Id = id }, cancellationToken: ct));
}
