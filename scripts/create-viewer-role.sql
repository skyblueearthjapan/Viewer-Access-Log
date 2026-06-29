-- Viewer-Access-Log 用の SELECT 専用ロール 'viewer' を作成する。
-- 実行は **スーパーユーザー(postgres)** で。データ(audit_logs等)は一切変更しない＝GRANT のみ。
-- 実行前に 'CHANGE_ME_STRONG' を十分に強いパスワードへ置き換えること。
--   例: psql -U postgres -d audit_logger -f create-viewer-role.sql

DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'viewer') THEN
    CREATE ROLE viewer LOGIN PASSWORD 'CHANGE_ME_STRONG';
  END IF;
END $$;

GRANT CONNECT ON DATABASE audit_logger TO viewer;
GRANT USAGE  ON SCHEMA audit TO viewer;

-- 既存テーブル(パーティション含む)へ SELECT のみ
GRANT SELECT ON ALL TABLES    IN SCHEMA audit TO viewer;
GRANT SELECT ON ALL SEQUENCES IN SCHEMA audit TO viewer;

-- 今後作られる月次パーティション等にも自動で SELECT を付与
ALTER DEFAULT PRIVILEGES IN SCHEMA audit GRANT SELECT ON TABLES    TO viewer;
ALTER DEFAULT PRIVILEGES IN SCHEMA audit GRANT SELECT ON SEQUENCES TO viewer;

-- 確認
\echo '--- viewer ロールの権限確認 ---'
SELECT rolname, rolcanlogin, rolsuper, rolcreaterole, rolcreatedb
FROM pg_roles WHERE rolname = 'viewer';
