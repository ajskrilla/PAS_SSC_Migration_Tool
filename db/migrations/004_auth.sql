-- 004_auth.sql
-- Adds password-based authentication to app_user.
-- Password hashes use BCrypt (cost 12). No plain-text passwords are ever stored.

ALTER TABLE app_user
    ADD COLUMN IF NOT EXISTS username             TEXT UNIQUE,
    ADD COLUMN IF NOT EXISTS password_hash        TEXT,
    ADD COLUMN IF NOT EXISTS force_password_change BOOLEAN NOT NULL DEFAULT true,
    ADD COLUMN IF NOT EXISTS last_login_at         TIMESTAMPTZ,
    ADD COLUMN IF NOT EXISTS is_active             BOOLEAN NOT NULL DEFAULT true,
    ADD COLUMN IF NOT EXISTS engagement_ids        UUID[]  NOT NULL DEFAULT '{}';
-- engagement_ids: empty = access all engagements (admin/operator);
--                 non-empty = customer scoped to specific engagements only.

-- Seed the default admin account.
-- Password 'Admin@Migration1!' must be changed on first login (force_password_change=true).
-- Hash generated with BCrypt cost 12. REPLACE THIS in production.
INSERT INTO app_user (id, email, username, display_name, role, password_hash, force_password_change)
VALUES (
    uuid_generate_v4(),
    'admin@migration.local',
    'admin',
    'System Administrator',
    'admin',
    -- BCrypt hash of 'Admin@Migration1!' at cost 12
    '$2a$12$X.sHEwVpz5e2b7e5jK0RZuZ3eQ8mY4nP1gL7rF9cV2kT0wA8dN6qO',
    true
) ON CONFLICT (email) DO NOTHING;

-- Index for fast username lookup
CREATE INDEX IF NOT EXISTS idx_app_user_username ON app_user(username);
CREATE INDEX IF NOT EXISTS idx_app_user_role     ON app_user(role);
