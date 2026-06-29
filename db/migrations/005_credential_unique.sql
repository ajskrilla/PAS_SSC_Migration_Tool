-- 005_credential_unique.sql
-- Add unique constraint so we can upsert one credential row per tenant_connection.
ALTER TABLE encrypted_credential
    ADD CONSTRAINT IF NOT EXISTS encrypted_credential_tenant_connection_id_key
    UNIQUE (tenant_connection_id);
