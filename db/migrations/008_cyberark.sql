-- 008_cyberark.sql
-- Adds CyberArk as a second SOURCE system alongside PAS.
--
-- No new tables. CyberArk safes, accounts and safe members are all inventory_item rows, and
-- migrating a safe permission is a migration_item row, so the existing resumability, revert,
-- reconciliation and audit machinery applies to CyberArk unchanged. This migration only widens
-- the CHECK constraints that would otherwise reject the new values.
--
-- NOTE ON SYNTAX: PostgreSQL has no "ALTER TABLE ... ADD CONSTRAINT IF NOT EXISTS". IF EXISTS is
-- valid on DROP CONSTRAINT (and on ADD COLUMN / CREATE INDEX), which is why each constraint below
-- is dropped-then-added rather than added conditionally. This is the same trap that made
-- 005_credential_unique.sql a no-op.
--
-- Constraint names are the PostgreSQL defaults for a column CHECK: <table>_<column>_check.

BEGIN;

-- ---------------------------------------------------------------------------
-- 1. tenant_connection.system_type: allow 'cyberark' as a source system.
-- ---------------------------------------------------------------------------
ALTER TABLE tenant_connection DROP CONSTRAINT IF EXISTS tenant_connection_system_type_check;
ALTER TABLE tenant_connection ADD CONSTRAINT tenant_connection_system_type_check
    CHECK (system_type IN ('pas','cyberark','secret_server'));

-- ---------------------------------------------------------------------------
-- 2. tenant_connection.auth_mode: CyberArk's five logon methods.
--    The four session methods are PVWA logons (Self-Hosted / on-prem); cyberark_oauth is
--    Privilege Cloud, which authenticates against a CyberArk Identity token endpoint.
-- ---------------------------------------------------------------------------
ALTER TABLE tenant_connection DROP CONSTRAINT IF EXISTS tenant_connection_auth_mode_check;
ALTER TABLE tenant_connection ADD CONSTRAINT tenant_connection_auth_mode_check
    CHECK (auth_mode IN (
        'platform_client_credentials',
        'legacy_password',
        'cyberark_ldap',
        'cyberark_vault',
        'cyberark_radius',
        'cyberark_windows',
        'cyberark_oauth'
    ));

-- ---------------------------------------------------------------------------
-- 3. tenant_connection: where the CyberArk Identity token URL lives.
--    Privilege Cloud needs a token endpoint that is neither the PVWA base URL nor derivable
--    from it (different hostname, different domain), so it gets its own column rather than
--    being smuggled through platform_tenant.
-- ---------------------------------------------------------------------------
ALTER TABLE tenant_connection ADD COLUMN IF NOT EXISTS identity_token_url TEXT;

-- ---------------------------------------------------------------------------
-- 4. inventory_item.item_type: safes and safe members.
--    'safe'        -> becomes a Secret Server folder
--    'safe_member' -> becomes a folder permission grant; source_native_id is
--                     '<safeName>|<memberName>', unique within a snapshot by construction
-- ---------------------------------------------------------------------------
ALTER TABLE inventory_item DROP CONSTRAINT IF EXISTS inventory_item_item_type_check;
ALTER TABLE inventory_item ADD CONSTRAINT inventory_item_item_type_check
    CHECK (item_type IN (
        'account','text_secret','file_secret','folder','multiplexed_account',
        'safe','safe_member'
    ));

-- ---------------------------------------------------------------------------
-- 5. migration_job.job_type: CyberArk run types.
--    Deliberately staged the same way the EMEA toolkit stages them, because folders must exist
--    and be owned before secrets land in them:
--      cyberark_safe       -> create folders from safes
--      cyberark_permission -> translate safe members into folder permissions
--      cyberark_account    -> retrieve credentials and create secrets
--      cyberark_full       -> all three, in that order
-- ---------------------------------------------------------------------------
ALTER TABLE migration_job DROP CONSTRAINT IF EXISTS migration_job_job_type_check;
ALTER TABLE migration_job ADD CONSTRAINT migration_job_job_type_check
    CHECK (job_type IN (
        'account_local','account_domain','account_unmanage_export',
        'text_secret','file_secret','folder_structure','full',
        'cyberark_safe','cyberark_permission','cyberark_account','cyberark_full'
    ));

-- ---------------------------------------------------------------------------
-- 6. event_log.event_type: 'ai_safety' and 'permission'.
--
--    BUG FIX, not a CyberArk change. AssistantService.LogSafetyEventAsync has been inserting
--    event_type 'ai_safety' since the content guard shipped, but this CHECK only permitted
--    ('status_change','api_call','user_action'). Every content-guard verdict therefore violated
--    the constraint; the insert is wrapped in a try/catch that logs and continues, so the output
--    side of the content guard has been silently recording nothing. The Logs page "AI safety"
--    filter has correspondingly never had rows to show.
--
--    'permission' is the CyberArk addition: folder-permission grants are audited separately from
--    generic api_call so the Logs page can filter them.
-- ---------------------------------------------------------------------------
ALTER TABLE event_log DROP CONSTRAINT IF EXISTS event_log_event_type_check;
ALTER TABLE event_log ADD CONSTRAINT event_log_event_type_check
    CHECK (event_type IN ('status_change','api_call','user_action','ai_safety','permission'));

-- ---------------------------------------------------------------------------
-- 7. Reporting index. The permission review screens filter safe members by snapshot and type;
--    without this they sequential-scan a table that also holds every account.
-- ---------------------------------------------------------------------------
CREATE INDEX IF NOT EXISTS idx_inventory_item_snapshot_type
    ON inventory_item(snapshot_id, item_type);

COMMIT;
