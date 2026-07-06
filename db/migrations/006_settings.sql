-- 006_settings.sql
-- Simple key-value store for admin-configurable app settings (currently just the JWT/session
-- timeout, but shaped to hold more without another migration). Seeded with today's hardcoded
-- default so existing behavior is unchanged until an admin actually changes it.
CREATE TABLE IF NOT EXISTS app_setting (
    key         TEXT PRIMARY KEY,
    value       TEXT NOT NULL,
    updated_at  TIMESTAMPTZ NOT NULL DEFAULT now()
);

INSERT INTO app_setting (key, value)
VALUES ('session_timeout_hours', '8')
ON CONFLICT (key) DO NOTHING;
