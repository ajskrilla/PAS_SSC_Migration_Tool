namespace PasMigration.Connectors;

/// <summary>
/// Resolves stored session credentials into request inputs. This is the single home for the
/// "load the vault if empty, then prefer stored values over what the client sent" logic that
/// was previously copy-pasted across five route handlers in Program.cs (/inventory/run,
/// /templates, /templates/create-file, /migrate, /revert).
///
/// Merge precedence (unchanged from the inline versions): a stored value wins whenever it is
/// present (non-null for optional fields, non-empty for ClientId/ClientSecret); the caller's
/// value is the fallback. The frontend deliberately sends blank credential fields once a
/// green connection test has stored them server-side, so "stored wins" is what makes
/// re-entry unnecessary.
///
/// Vault-load semantics: each role is loaded on demand — if the vault has no live entry for a
/// role (e.g. after a container restart or idle expiry), the encrypted blobs for the whole
/// engagement are loaded once via <see cref="CredentialEncryptionService.LoadIntoVaultAsync"/>.
/// This matches the previous per-handler behavior; the only difference is that a dual-role
/// merge may issue one extra (harmless) load attempt when a role genuinely has nothing stored.
/// </summary>
public sealed class CredentialResolver(CredentialVault vault, CredentialEncryptionService enc)
{
    /// <summary>
    /// Active credentials for an engagement+role, loading persisted ciphertext into the vault
    /// first if the vault has no live entry. Null if nothing is stored for that role.
    /// </summary>
    public async Task<SessionCredentials?> GetAsync(Guid engagementId, string role, CancellationToken ct = default)
    {
        if (!vault.Has(engagementId, role))
            await enc.LoadIntoVaultAsync(engagementId, vault, ct);
        return vault.Get(engagementId, role);
    }

    /// <summary>Merges stored credentials for the input's role into an inventory run.</summary>
    public async Task<RunInventoryInput> MergeAsync(Guid engagementId, RunInventoryInput input, CancellationToken ct = default)
    {
        var stored = await GetAsync(engagementId, input.Role, ct);
        if (stored is null) return input;

        return input with
        {
            BaseUrl             = stored.BaseUrl ?? input.BaseUrl,
            PlatformBaseUrl     = stored.PlatformBaseUrl ?? input.PlatformBaseUrl,
            SecretServerBaseUrl = stored.SecretServerBaseUrl ?? input.SecretServerBaseUrl,
            AppId               = stored.AppId ?? input.AppId,
            ClientId            = stored.ClientId.Length > 0 ? stored.ClientId : input.ClientId,
            ClientSecret        = stored.ClientSecret.Length > 0 ? stored.ClientSecret : input.ClientSecret,
            Username            = stored.Username ?? input.Username,
            Scope               = stored.Scope ?? input.Scope,
            IdentityTokenUrl    = stored.IdentityTokenUrl ?? input.IdentityTokenUrl,
        };
    }

    /// <summary>
    /// Merges stored credentials into a connection-shaped input (template listing / creation).
    /// If the input names no engagement+role there is nothing stored to merge — returned as-is.
    /// </summary>
    public async Task<TestConnectionInput> MergeAsync(TestConnectionInput input, CancellationToken ct = default)
    {
        if (input.EngagementId is not { } eng || input.Role is not { } role)
            return input;

        var stored = await GetAsync(eng, role, ct);
        if (stored is null) return input;

        return input with
        {
            BaseUrl             = stored.BaseUrl ?? input.BaseUrl,
            PlatformBaseUrl     = stored.PlatformBaseUrl ?? input.PlatformBaseUrl,
            SecretServerBaseUrl = stored.SecretServerBaseUrl ?? input.SecretServerBaseUrl,
            AppId               = stored.AppId ?? input.AppId,
            ClientId            = stored.ClientId.Length > 0 ? stored.ClientId : input.ClientId,
            ClientSecret        = stored.ClientSecret.Length > 0 ? stored.ClientSecret : input.ClientSecret,
            Username            = stored.Username ?? input.Username,
            Scope               = stored.Scope ?? input.Scope,
            IdentityTokenUrl    = stored.IdentityTokenUrl ?? input.IdentityTokenUrl,
        };
    }

    /// <summary>
    /// Merges BOTH roles into a CyberArk migration run: CyberArk fields from the stored "source"
    /// credentials, Secret Server fields from the stored "target" credentials. Same precedence as
    /// the PAS overload — a stored value wins whenever it is present.
    /// </summary>
    public async Task<CyberArkMigrationInput> MergeAsync(
        Guid engagementId, CyberArkMigrationInput input, CancellationToken ct = default)
    {
        var src = await GetAsync(engagementId, "source", ct);
        var tgt = await GetAsync(engagementId, "target", ct);
        if (src is null && tgt is null) return input;

        return input with
        {
            PvwaBaseUrl              = src?.BaseUrl ?? input.PvwaBaseUrl,
            CyberArkAuthMode         = !string.IsNullOrEmpty(src?.AuthMode) ? src.AuthMode : input.CyberArkAuthMode,
            CyberArkUsername         = src?.Username ?? input.CyberArkUsername,
            // For the on-prem session logons the vault password rides in ClientSecret, the same
            // slot the OAuth client secret uses. Which one it is depends on CyberArkAuthMode.
            CyberArkPassword         = src?.ClientSecret.Length > 0 ? src.ClientSecret : input.CyberArkPassword,
            CyberArkClientId         = src?.ClientId.Length > 0 ? src.ClientId : input.CyberArkClientId,
            CyberArkClientSecret     = src?.ClientSecret.Length > 0 ? src.ClientSecret : input.CyberArkClientSecret,
            CyberArkIdentityTokenUrl = src?.IdentityTokenUrl ?? input.CyberArkIdentityTokenUrl,
            SsBaseUrl                = tgt?.BaseUrl ?? input.SsBaseUrl,
            SsPlatformBaseUrl        = tgt?.PlatformBaseUrl ?? input.SsPlatformBaseUrl,
            SsSecretServerBaseUrl    = tgt?.SecretServerBaseUrl ?? input.SsSecretServerBaseUrl,
            SsClientId               = tgt?.ClientId.Length > 0 ? tgt.ClientId : input.SsClientId,
            SsClientSecret           = tgt?.ClientSecret.Length > 0 ? tgt.ClientSecret : input.SsClientSecret,
        };
    }

    /// <summary>
    /// Merges BOTH roles into a migration run: PAS fields from the stored "source" credentials,
    /// Secret Server fields from the stored "target" credentials. Used by /migrate and /revert.
    /// </summary>
    public async Task<MigrationRunInput> MergeAsync(Guid engagementId, MigrationRunInput input, CancellationToken ct = default)
    {
        var src = await GetAsync(engagementId, "source", ct);
        var tgt = await GetAsync(engagementId, "target", ct);
        if (src is null && tgt is null) return input;

        return input with
        {
            PasBaseUrl      = src?.BaseUrl ?? src?.PlatformBaseUrl ?? input.PasBaseUrl,
            PasAppId        = src?.AppId   ?? input.PasAppId,
            PasClientId     = src?.ClientId.Length > 0 ? src.ClientId : input.PasClientId,
            PasClientSecret = src?.ClientSecret.Length > 0 ? src.ClientSecret : input.PasClientSecret,
            PasScope        = src?.Scope   ?? input.PasScope,
            SsBaseUrl           = tgt?.BaseUrl ?? input.SsBaseUrl,
            SsPlatformBaseUrl   = tgt?.PlatformBaseUrl ?? input.SsPlatformBaseUrl,
            SsSecretServerBaseUrl = tgt?.SecretServerBaseUrl ?? input.SsSecretServerBaseUrl,
            SsClientId      = tgt?.ClientId.Length > 0 ? tgt.ClientId : input.SsClientId,
            SsClientSecret  = tgt?.ClientSecret.Length > 0 ? tgt.ClientSecret : input.SsClientSecret,
        };
    }
}
