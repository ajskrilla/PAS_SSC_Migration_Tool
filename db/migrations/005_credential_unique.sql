-- 005_credential_unique.sql
-- Add unique constraint so we can upsert one credential row per tenant_connection.
--
-- NOTE: Postgres has no "ADD CONSTRAINT IF NOT EXISTS" — that clause is only valid for
-- columns/indexes/DROP CONSTRAINT, never for ADD CONSTRAINT. The original version of this
-- file used it and never actually created the constraint. Wrapped in a DO block with an
-- explicit pg_constraint check instead, so this is safe to run both against a fresh DB and
-- by hand against an already-initialized one that's missing the constraint.
DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint WHERE conname = 'encrypted_credential_tenant_connection_id_key'
    ) THEN
        ALTER TABLE encrypted_credential
            ADD CONSTRAINT encrypted_credential_tenant_connection_id_key
            UNIQUE (tenant_connection_id);
    END IF;
END $$;
