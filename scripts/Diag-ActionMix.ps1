# READ-ONLY: break down what makes up the 'direct' content-read count, to judge folder-browse inflation.
# Run on WORKSTATION. Prompts: MTSV admin, viewer password. ASCII-only.
$ErrorActionPreference = 'Stop'

$sql = @'
\pset pager off
\echo '=== (1) direct content-reads by ACTION (last 3 days, all depts, real users) ==='
SELECT action, count(*) AS events, count(DISTINCT file_path) AS distinct_files
FROM audit.audit_logs
WHERE event_time >= now() - interval '3 days'
  AND is_content_read = TRUE AND result::text = 'Success'
  AND user_name IS NOT NULL AND user_name !~* 'MTSV\$'
  AND user_name !~* '(NETWORK SERVICE|LOCAL SYSTEM|^SYSTEM$|svc[-_])'
GROUP BY action ORDER BY events DESC;

\echo ''
\echo '=== (2) noise breakdown within direct content-reads (last 3 days) ==='
SELECT
  count(*) FILTER (WHERE file_path ILIKE '%:Zone.Identifier') AS zone_identifier,
  count(*) FILTER (WHERE file_name ILIKE 'desktop.ini' OR file_name ILIKE 'thumbs.db') AS sys_ini_thumbs,
  count(*) FILTER (WHERE file_name ILIKE '%.lnk') AS lnk,
  count(*) FILTER (WHERE file_name ILIKE '%.tmp' OR file_name ILIKE '~$%') AS tmp_lock,
  count(*) AS total
FROM audit.audit_logs
WHERE event_time >= now() - interval '3 days'
  AND is_content_read = TRUE AND result::text = 'Success'
  AND user_name IS NOT NULL AND user_name !~* 'MTSV\$'
  AND user_name !~* '(NETWORK SERVICE|LOCAL SYSTEM|^SYSTEM$|svc[-_])';

\echo ''
\echo '=== (3) repeat factor: events vs distinct(user|file) (last 1 day) ==='
SELECT count(*) AS events,
       count(DISTINCT (user_name || '|' || file_path)) AS distinct_user_file,
       round(count(*)::numeric / NULLIF(count(DISTINCT (user_name || '|' || file_path)),0), 2) AS events_per_file
FROM audit.audit_logs
WHERE event_time >= now() - interval '1 day'
  AND is_content_read = TRUE AND result::text = 'Success'
  AND user_name IS NOT NULL AND user_name !~* 'MTSV\$'
  AND user_name !~* '(NETWORK SERVICE|LOCAL SYSTEM|^SYSTEM$|svc[-_])';

\echo ''
\echo '=== (4) accesses(mask text) distribution within direct content-reads (last 1 day, top 10) ==='
SELECT accesses, count(*) AS events
FROM audit.audit_logs
WHERE event_time >= now() - interval '1 day'
  AND is_content_read = TRUE AND result::text = 'Success'
  AND user_name IS NOT NULL AND user_name !~* 'MTSV\$'
  AND user_name !~* '(NETWORK SERVICE|LOCAL SYSTEM|^SYSTEM$|svc[-_])'
GROUP BY accesses ORDER BY events DESC LIMIT 10;
'@

$mc = Get-Credential -Message 'MTSV (192.168.1.242) admin  [for remoting]'
$vc = Get-Credential -Message 'viewer role password' -UserName 'viewer'

Invoke-Command -ComputerName lineworks-mtsv -Credential $mc -ArgumentList $vc.GetNetworkCredential().Password, $sql -ScriptBlock {
  param($vpw, $sql)
  try { [Console]::OutputEncoding = [Text.Encoding]::UTF8 } catch {}
  try { $OutputEncoding = [Text.Encoding]::UTF8 } catch {}
  $env:PGCLIENTENCODING = 'UTF8'
  $psql = (Get-ChildItem 'D:\PostgreSQL\*\bin\psql.exe','C:\Program Files\PostgreSQL\*\bin\psql.exe' -ErrorAction SilentlyContinue |
           Select-Object -First 1 -ExpandProperty FullName)
  if (-not $psql) { return 'psql not found' }
  $env:PGPASSWORD = $vpw
  $sql | & $psql -U viewer -h localhost -d audit_logger -v ON_ERROR_STOP=0 -f -
  Remove-Item Env:PGPASSWORD -ErrorAction SilentlyContinue
}
