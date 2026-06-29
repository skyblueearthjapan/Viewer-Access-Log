-- Viewer-Access-Log 用の SELECT 専用ロール 'viewer' を作成する。
-- 実行は **スーパーユーザー(postgres)** で。データ(audit_logs等)は一切変更しない＝GRANT のみ。
-- パスワードは実行ランナーが __VPW__ を安全に置換して埋める（ファイル/チャットに平文を残さない）。

DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'viewer') THEN
    CREATE ROLE viewer LOGIN PASSWORD '__VPW__';
  END IF;
END $$;

-- SELECT 専用の権限付与（既存・将来パーティション含む）
GRANT CONNECT ON DATABASE audit_logger TO viewer;
GRANT USAGE  ON SCHEMA audit TO viewer;
GRANT SELECT ON ALL TABLES    IN SCHEMA audit TO viewer;
GRANT SELECT ON ALL SEQUENCES IN SCHEMA audit TO viewer;
ALTER DEFAULT PRIVILEGES IN SCHEMA audit GRANT SELECT ON TABLES    TO viewer;
ALTER DEFAULT PRIVILEGES IN SCHEMA audit GRANT SELECT ON SEQUENCES TO viewer;

\echo '--- viewer role (rolsuper / rolcreaterole should be f) ---'
SELECT rolname, rolcanlogin, rolsuper, rolcreaterole, rolcreatedb
FROM pg_roles WHERE rolname = 'viewer';
