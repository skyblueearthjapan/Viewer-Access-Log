-- P4b: extend config_editor with the MINIMAL privilege to change alert/incident STATUS
-- (operational ack/close). Column-level UPDATE only -- it CANNOT alter detection data,
-- and still has NO access to audit_logs / raw_windows_events. Run as postgres.

GRANT SELECT ON audit.alert_histories, audit.detected_incidents TO config_editor;
GRANT UPDATE (status, acked_by, acked_at) ON audit.alert_histories  TO config_editor;
GRANT UPDATE (status)                       ON audit.detected_incidents TO config_editor;

\echo '--- config_editor column-level UPDATE grants (status only) ---'
SELECT table_name, column_name, privilege_type
FROM information_schema.column_privileges
WHERE grantee = 'config_editor' AND table_schema = 'audit'
  AND table_name IN ('alert_histories','detected_incidents')
  AND privilege_type = 'UPDATE'
ORDER BY table_name, column_name;
