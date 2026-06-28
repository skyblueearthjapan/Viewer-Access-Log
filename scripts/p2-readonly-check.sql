-- Viewer-Access-Log / P2 本番確認（読み取り専用・全てSELECT）
-- 実行: psql -h <host> -p 5432 -d audit_logger -U <user> -f p2-readonly-check.sql
--   または pgAdmin の Query Tool に貼り付けて実行。書き込みは一切しない。
\pset pager off

\echo '=== 1. audit_logs カラム一覧（is_content_read 等の実在確認） ==='
SELECT column_name, data_type
FROM information_schema.columns
WHERE table_schema = 'audit' AND table_name = 'audit_logs'
ORDER BY ordinal_position;

\echo ''
\echo '=== 2. action 列挙値（FILE_READ / FILE_METADATA_READ / FOLDER_LIST 等の実在） ==='
SELECT unnest(enum_range(NULL::audit.action_type))::text AS action_type;

\echo ''
\echo '=== 3. 件数・最古/最新イベント時刻 ==='
SELECT count(*) AS total_rows, min(event_time) AS oldest, max(event_time) AS latest
FROM audit.audit_logs;

\echo ''
\echo '=== 4. 技術部フォルダへの「実ユーザー直接アクセス（MTSV$除外・内容読取）」直近5件 ==='
\echo '    ※ is_content_read 列が無い場合この問い合わせはエラーになるが、後続は続行する'
SELECT event_time, server_name, user_name, action::text, is_content_read,
       left(file_path, 80) AS file_path
FROM audit.audit_logs
WHERE folder_path ~* '[\\/]技術部([\\/]|$)'
  AND user_name IS NOT NULL
  AND user_name !~* 'MTSV\$'
  AND is_content_read = TRUE
ORDER BY event_time DESC
LIMIT 5;

\echo ''
\echo '=== 5. 直近7日の user_name 分布（MTSV$ / サービス / 実ユーザーの見え方）上位15 ==='
SELECT user_name, count(*) AS n
FROM audit.audit_logs
WHERE event_time > now() - interval '7 days'
GROUP BY user_name
ORDER BY n DESC
LIMIT 15;

\echo ''
\echo '=== 6. ロールの有無（viewer は未作成のはず／collector は収集用） ==='
SELECT rolname, rolcanlogin FROM pg_roles WHERE rolname IN ('viewer', 'collector', 'postgres');

\echo ''
\echo '=== 7. 収集状態（GAP健全性の手がかり） ==='
SELECT server_name, channel, last_record_id, last_event_time, last_status
FROM audit.collector_state
ORDER BY server_name, channel;
