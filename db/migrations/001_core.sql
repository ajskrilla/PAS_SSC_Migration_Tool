-- 001_core.sql
-- Core relational model: engagement -> tenants -> inventory -> reconciliation.
-- NO secret values, passwords, or file bytes are ever stored here. Metadata/status/hashes only.

CREATE EXTENSION IF NOT EXISTS "uuid-ossp";
CREATE EXTENSION IF NOT EXISTS vector;

-- One row per customer migration. Parent of everything.
CREATE TABLE engagement (
    id            UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
    name          TEXT NOT NULL,
    customer_name TEXT NOT NULL,
    status        TEXT NOT NULL DEFAULT 'planning'
                  CHECK (status IN ('planning','active','completed')),
    created_at    TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at    TIMESTAMPTZ NOT NULL DEFAULT now()
);

-- Source (PAS) and target (SS/Platform) connection per engagement. No creds stored by default.
CREATE TABLE tenant_connection (
    id              UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
    engagement_id   UUID NOT NULL REFERENCES engagement(id) ON DELETE CASCADE,
    role            TEXT NOT NULL CHECK (role IN ('source','target')),
    system_type     TEXT NOT NULL CHECK (system_type IN ('pas','secret_server')),
    base_url        TEXT,
    platform_tenant TEXT,
    auth_mode       TEXT NOT NULL
                    CHECK (auth_mode IN ('platform_client_credentials','legacy_password')),
    credential_ref  UUID,  -- null = in-memory only; else encrypted_credential.id or SS secret id
    created_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
    UNIQUE (engagement_id, role)
);

-- Point-in-time capture of one tenant for a phase (pre/post).
CREATE TABLE inventory_snapshot (
    id                   UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
    engagement_id        UUID NOT NULL REFERENCES engagement(id) ON DELETE CASCADE,
    tenant_connection_id UUID NOT NULL REFERENCES tenant_connection(id) ON DELETE CASCADE,
    phase                TEXT NOT NULL CHECK (phase IN ('pre','post')),
    captured_at          TIMESTAMPTZ NOT NULL DEFAULT now(),
    summary              JSONB NOT NULL DEFAULT '{}'::jsonb,  -- counts by type, managed vs unmanaged
    status               TEXT NOT NULL DEFAULT 'completed'
);

-- Discovered objects within a snapshot. No secret values.
CREATE TABLE inventory_item (
    id               UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
    snapshot_id      UUID NOT NULL REFERENCES inventory_snapshot(id) ON DELETE CASCADE,
    item_type        TEXT NOT NULL
                     CHECK (item_type IN ('account','text_secret','file_secret','folder')),
    source_native_id TEXT NOT NULL,
    name             TEXT NOT NULL,
    folder_path      TEXT,
    parent_ref       TEXT,
    is_managed       BOOLEAN,         -- accounts only
    size_bytes       BIGINT,          -- files only
    attributes       JSONB NOT NULL DEFAULT '{}'::jsonb,
    UNIQUE (snapshot_id, item_type, source_native_id)
);
CREATE INDEX idx_inventory_item_snapshot ON inventory_item(snapshot_id);
CREATE INDEX idx_inventory_item_type ON inventory_item(item_type);

-- Source<->target diff for the health check. Can be refreshed as a derived result.
CREATE TABLE reconciliation_result (
    id             UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
    engagement_id  UUID NOT NULL REFERENCES engagement(id) ON DELETE CASCADE,
    item_type      TEXT NOT NULL,
    match_key      TEXT NOT NULL,     -- e.g. folder_path + name
    source_item_id UUID REFERENCES inventory_item(id) ON DELETE SET NULL,
    target_item_id UUID REFERENCES inventory_item(id) ON DELETE SET NULL,
    match_status   TEXT NOT NULL
                   CHECK (match_status IN ('source_only','target_only','matched','conflict')),
    notes          TEXT,
    computed_at    TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE INDEX idx_recon_engagement ON reconciliation_result(engagement_id);
