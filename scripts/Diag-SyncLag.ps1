# READ-ONLY diagnosis: is there recent gijutsu-bu DIRECT activity in audit_logs that the VAL cache is missing?
# Run on WORKSTATION: powershell -NoProfile -ExecutionPolicy Bypass -File Diag-SyncLag.ps1
# Prompts: MTSV admin, viewer password. ASCII-only (dept injected via placeholder).
$ErrorActionPreference = 'Stop'

$dept = [string][char]0x6280 + [char]0x8853 + [char]0x90E8   # gijutsu-bu

# literal here-string (no PS interpolation); __DEPT__ replaced below.
$sqlTemplate = @'
\pset pager off
\echo '=== gijutsu-bu DIRECT content-reads, last 6h grouped by JST hour ==='
SELECT date_trunc('hour', event_time AT TIME ZONE 'Asia/Tokyo') AS hour_jst,
       count(*) AS rows,
       max(event_time AT TIME ZONE 'Asia/Tokyo') AS latest_jst,
       max(id) AS max_id
FROM audit.audit_logs
WHERE event_time >= now() - interval '6 hours'
  AND (folder_path ILIKE '%__DEPT__%' OR file_path ILIKE '%__DEPT__%')
  AND is_content_read = TRUE
  AND user_name IS NOT NULL
  AND user_name !~* 'MTSV\$'
  AND user_name !~* '(NETWORK SERVICE|LOCAL SYSTEM|^SYSTEM$|svc[-_])'
GROUP BY 1 ORDER BY 1;
\echo ''
\echo '=== latest 5 gijutsu-bu DIRECT reads (JST) ==='
SELECT (event_time AT TIME ZONE 'Asia/Tokyo') AS t_jst, user_name, id
FROM audit.audit_logs
WHERE (folder_path ILIKE '%__DEPT__%' OR file_path ILIKE '%__DEPT__%')
  AND is_content_read = TRUE
  AND user_name IS NOT NULL
  AND user_name !~* 'MTSV\$'
  AND user_name !~* '(NETWORK SERVICE|LOCAL SYSTEM|^SYSTEM$|svc[-_])'
ORDER BY event_time DESC LIMIT 5;
\echo ''
\echo '=== overall freshness (all events) ==='
SELECT max(id) AS max_id_all, (max(event_time) AT TIME ZONE 'Asia/Tokyo') AS latest_all_jst FROM audit.audit_logs;
'@

$sql = $sqlTemplate.Replace('__DEPT__', $dept)

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
