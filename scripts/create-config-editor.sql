-- config_editor: a LIMITED write role (config tables only) + the config_audit_log table.
-- Run as a SUPERUSER (postgres). The runner replaces __CPW__ with the new role password.
-- IMPORTANT: this role gets NO privilege on the log bodies (audit_logs / raw_windows_events) -- read-only insurance stays intact.

DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'config_editor') THEN
    CREATE ROLE config_editor LOGIN PASSWORD '__CPW__';
  END IF;
END $$;

GRANT CONNECT ON DATABASE audit_logger TO config_editor;
GRANT USAGE  ON SCHEMA audit TO config_editor;

-- write privilege ONLY on the 7 config tables
GRANT SELECT, INSERT, UPDATE, DELETE ON
  audit.monitored_folders,
  audit.users,
  audit.alert_rules,
  audit.detection_exclusions,
  audit.common_folders,
  audit.user_folder_grants,
  audit.app_settings
TO config_editor;

-- change-audit table (who changed what)
CREATE TABLE IF NOT EXISTS audit.config_audit_log (
  id          BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  changed_at  TIMESTAMPTZ NOT NULL DEFAULT now(),
  user_name   TEXT NOT NULL,
  table_name  TEXT NOT NULL,
  action      TEXT NOT NULL,
  record_key  TEXT,
  old_values  JSONB,
  new_values  JSONB
);
GRANT SELECT, INSERT ON audit.config_audit_log TO config_editor;

-- sequences (IDENTITY columns do not need this, but SERIAL would; harmless for write safety
-- because config_editor has NO table-write grant on audit_logs)
GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA audit TO config_editor;

\echo '--- roles (config_editor: rolsuper/rolcreaterole should be f) ---'
SELECT rolname, rolcanlogin, rolsuper, rolcreaterole FROM pg_roles WHERE rolname IN ('viewer','config_editor');

\echo '--- config_editor table grants (MUST be only the 7 config tables + config_audit_log; NOT audit_logs) ---'
SELECT table_name, string_agg(DISTINCT privilege_type, ',' ORDER BY privilege_type) AS privs
FROM information_schema.role_table_grants
WHERE grantee = 'config_editor' AND table_schema = 'audit'
GROUP BY table_name ORDER BY table_name;
