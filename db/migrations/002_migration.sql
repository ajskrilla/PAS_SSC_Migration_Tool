-- 002_migration.sql
-- Migration execution + audit + verification. Resumability keyed to source id.

-- A migration run (per type or full).
CREATE TABLE migration_job (
    id            UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
    engagement_id UUID NOT NULL REFERENCES engagement(id) ON DELETE CASCADE,
    job_type      TEXT NOT NULL CHECK (job_type IN
                  ('account_unmanage_export','text_secret','file_secret','folder_structure','full')),
    mode          TEXT NOT NULL CHECK (mode IN ('dry_run','live')),
    status        TEXT NOT NULL DEFAULT 'queued'
                  CHECK (status IN ('queued','running','completed','failed','cancelled')),
    started_at    TIMESTAMPTZ,
    finished_at   TIMESTAMPTZ,
    total         INTEGER NOT NULL DEFAULT 0,
    succeeded     INTEGER NOT NULL DEFAULT 0,
    failed        INTEGER NOT NULL DEFAULT 0,
    skipped       INTEGER NOT NULL DEFAULT 0,
    params        JSONB NOT NULL DEFAULT '{}'::jsonb,
    created_by    TEXT
);
CREATE INDEX idx_job_engagement ON migration_job(engagement_id);

-- Per-item migration state. The resumability core.
CREATE TABLE migration_item (
    id                 UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
    job_id             UUID NOT NULL REFERENCES migration_job(id) ON DELETE CASCADE,
    engagement_id      UUID NOT NULL REFERENCES engagement(id) ON DELETE CASCADE,
    item_type          TEXT NOT NULL,
    source_native_id   TEXT NOT NULL,
    source_name        TEXT,
    source_folder_path TEXT,
    target_native_id   TEXT,            -- null until created on target
    target_folder_path TEXT,
    stage              TEXT,            -- multi-step flows e.g. unmanaged->exported->loaded
    status             TEXT NOT NULL DEFAULT 'pending'
                       CHECK (status IN ('pending','in_progress','succeeded','failed','skipped')),
    attempts           INTEGER NOT NULL DEFAULT 0,
    last_error         TEXT,
    started_at         TIMESTAMPTZ,
    finished_at        TIMESTAMPTZ,
    metadata           JSONB NOT NULL DEFAULT '{}'::jsonb,
    -- idempotent re-runs: one logical item per engagement+type+source id
    UNIQUE (engagement_id, item_type, source_native_id)
);
CREATE INDEX idx_mig_item_job ON migration_item(job_id);
CREATE INDEX idx_mig_item_status ON migration_item(status);

-- Status transitions + tenant API actions. Audit trail + timeline analytics.
CREATE TABLE event_log (
    id                UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
    engagement_id     UUID NOT NULL REFERENCES engagement(id) ON DELETE CASCADE,
    migration_item_id UUID REFERENCES migration_item(id) ON DELETE SET NULL,
    job_id            UUID REFERENCES migration_job(id) ON DELETE SET NULL,
    event_type        TEXT NOT NULL CHECK (event_type IN ('status_change','api_call','user_action')),
    from_status       TEXT,
    to_status         TEXT,
    tenant_role       TEXT,
    action            TEXT,
    outcome           TEXT,
    message           TEXT,
    actor             TEXT,
    occurred_at       TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE INDEX idx_event_engagement ON event_log(engagement_id, occurred_at);

-- Post-migration fidelity. Booleans/hashes only - never compared secret values.
CREATE TABLE verification_result (
    id                UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
    engagement_id     UUID NOT NULL REFERENCES engagement(id) ON DELETE CASCADE,
    migration_item_id UUID NOT NULL REFERENCES migration_item(id) ON DELETE CASCADE,
    check_type        TEXT NOT NULL CHECK (check_type IN
                      ('exists','value_hash_match','file_byte_match','folder_placement')),
    result            TEXT NOT NULL CHECK (result IN ('pass','fail','warning')),
    detail            TEXT,
    verified_at       TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE INDEX idx_verif_engagement ON verification_result(engagement_id);

-- Tool users for app-level auth/RBAC (OIDC-backed).
CREATE TABLE app_user (
    id           UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
    email        TEXT NOT NULL UNIQUE,
    display_name TEXT,
    role         TEXT NOT NULL DEFAULT 'viewer' CHECK (role IN ('admin','operator','viewer')),
    created_at   TIMESTAMPTZ NOT NULL DEFAULT now()
);

-- Only used if persistence is opted into. Envelope-encrypted; default flow leaves this unused.
CREATE TABLE encrypted_credential (
    id                   UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
    tenant_connection_id UUID NOT NULL REFERENCES tenant_connection(id) ON DELETE CASCADE,
    kms_key_id           TEXT NOT NULL,
    ciphertext           BYTEA NOT NULL,
    created_at           TIMESTAMPTZ NOT NULL DEFAULT now()
);
