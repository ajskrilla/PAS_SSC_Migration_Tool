-- 007_sso.sql
-- SSO/SCIM groundwork: external identity providers (OIDC), external identity links,
-- SCIM bearer tokens, and password-less (SSO-only) local users.
-- Schema + admin CRUD only at this stage — login behavior does not change until the
-- OIDC challenge/callback flow ships in a later step.

-- One row per configured IdP. "type" is constrained to 'oidc'; 'saml' would be added to the
-- CHECK only if a customer ever demands it (see docs/SSO_SCIM_DESIGN.md, DECISION 1).
CREATE TABLE IF NOT EXISTS identity_provider (
    id                    UUID PRIMARY KEY,             -- generated app-side: the client-secret
                                                        -- encryption key is HKDF-salted with this id
    name                  TEXT NOT NULL,                -- display name ("Contoso Entra ID")
    slug                  TEXT NOT NULL UNIQUE,         -- appears in URLs (/api/auth/sso/{slug}/...)
    type                  TEXT NOT NULL DEFAULT 'oidc' CHECK (type IN ('oidc')),
    authority             TEXT NOT NULL,                -- OIDC issuer / metadata URL
    client_id             TEXT NOT NULL,
    client_secret_enc     BYTEA,                        -- AES-256-GCM, same scheme as tenant creds
    enabled               BOOLEAN NOT NULL DEFAULT true,
    jit_provisioning      BOOLEAN NOT NULL DEFAULT false,
    default_role          TEXT NOT NULL DEFAULT 'viewer'
                          CHECK (default_role IN ('admin','operator','viewer')),
    allowed_email_domains TEXT[] NOT NULL DEFAULT '{}', -- empty = any domain
    role_claim            TEXT,                         -- claim name carrying role/group info
    role_mappings         JSONB,                        -- claim value -> role / engagement ids
    created_at            TIMESTAMPTZ NOT NULL DEFAULT now()
);

-- (provider, subject) <-> local user link. Identity matching is on the OIDC 'sub' claim —
-- NEVER email: emails change and can be spoofed across IdPs. email_at_link is a record of
-- the email observed at first link, for audit/display only.
CREATE TABLE IF NOT EXISTS user_identity (
    id            UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
    user_id       UUID NOT NULL REFERENCES app_user(id)          ON DELETE CASCADE,
    provider_id   UUID NOT NULL REFERENCES identity_provider(id) ON DELETE RESTRICT,
    subject       TEXT NOT NULL,
    email_at_link TEXT,
    created_at    TIMESTAMPTZ NOT NULL DEFAULT now(),
    last_login_at TIMESTAMPTZ,
    UNIQUE (provider_id, subject)
);
CREATE INDEX IF NOT EXISTS idx_user_identity_user ON user_identity(user_id);

-- Long-lived bearer tokens presented by the IdP's SCIM client. Only a SHA-256 hash is stored;
-- the plaintext token is shown exactly once at creation. Endpoints arrive in a later step.
CREATE TABLE IF NOT EXISTS scim_token (
    id           UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
    provider_id  UUID NOT NULL REFERENCES identity_provider(id) ON DELETE CASCADE,
    token_hash   TEXT NOT NULL,
    created_at   TIMESTAMPTZ NOT NULL DEFAULT now(),
    last_used_at TIMESTAMPTZ,
    revoked_at   TIMESTAMPTZ
);
CREATE INDEX IF NOT EXISTS idx_scim_token_provider ON scim_token(provider_id);

-- SSO-only users have no local password (password_hash NULL). The column was already nullable
-- (004 added it without NOT NULL); this DROP NOT NULL is an idempotent no-op that documents the
-- intent. Both the login and change-password paths reject null/empty hashes (fixed in the SSO
-- prerequisites patch) so a password-less account can never authenticate locally.
ALTER TABLE app_user ALTER COLUMN password_hash DROP NOT NULL;
