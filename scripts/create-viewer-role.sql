-- Viewer-Access-Log 用の SELECT 専用ロール 'viewer' を作成する。
-- 実行は **スーパーユーザー(postgres)** で。データ(audit_logs等)は一切変更しない＝GRANT のみ。
-- パスワードは psql 変数 :vpw で渡す（ファイルにもチャットにも平文を残さない）。
--   例: psql -U postgres -d audit_logger -v vpw='<strong-pw>' -f create-viewer-role.sql

-- viewer が無ければ作成（あれば何もしない）。:'vpw' は通常SQL文脈なので psql が安全に展開する。
SELECT format('CREATE ROLE viewer LOGIN PASSWORD %L', :'vpw')
WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'viewer')
\gexec

-- SELECT 専用の権限付与（既存・将来パーティション含む）
GRANT CONNECT ON DATABASE audit_logger TO viewer;
GRANT USAGE  ON SCHEMA audit TO viewer;
GRANT SELECT ON ALL TABLES    IN SCHEMA audit TO viewer;
GRANT SELECT ON ALL SEQUENCES IN SCHEMA audit TO viewer;
ALTER DEFAULT PRIVILEGES IN SCHEMA audit GRANT SELECT ON TABLES    TO viewer;
ALTER DEFAULT PRIVILEGES IN SCHEMA audit GRANT SELECT ON SEQUENCES TO viewer;

\echo '--- viewer ロール確認（rolsuper/rolcreaterole は f であるべき） ---'
SELECT rolname, rolcanlogin, rolsuper, rolcreaterole, rolcreatedb
FROM pg_roles WHERE rolname = 'viewer';
