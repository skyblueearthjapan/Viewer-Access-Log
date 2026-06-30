# READ-ONLY: test the "Excel external-link auto-read" hypothesis for a user.
# Shows process_name + millisecond event_time + file_name to see if reads are same-instant bursts
# (= one workbook open pulling in linked source workbooks) rather than human file opens.
# Run on WORKSTATION: powershell -NoProfile -ExecutionPolicy Bypass -File Diag-ExcelLinks.ps1
# Prompts: MTSV admin, viewer password. ASCII-only. Pass the target account as $acct below.
$ErrorActionPreference = 'Stop'

$acct = 'imaizumi'   # target account (Imaizumi). Change here if different.

$sqlTemplate = @'
\pset pager off
\echo '=== user account match ==='
SELECT user_name, display_name, department FROM audit.users WHERE user_name ILIKE '%__ACCT__%';
\echo ''
\echo '=== distinct process_name for this user (last 1 day) ==='
SELECT COALESCE(NULLIF(process_name,''),'(empty)') AS process_name, count(*) AS events
FROM audit.audit_logs
WHERE user_name ILIKE '__ACCT__' AND event_time >= now() - interval '1 day'
  AND is_content_read = TRUE
GROUP BY 1 ORDER BY events DESC;
\echo ''
\echo '=== events-per-second burst check (top 10 busiest seconds) ==='
SELECT date_trunc('second', event_time AT TIME ZONE 'Asia/Tokyo') AS sec_jst,
       count(*) AS reads_in_that_second,
       count(DISTINCT file_path) AS distinct_files
FROM audit.audit_logs
WHERE user_name ILIKE '__ACCT__' AND event_time >= now() - interval '1 day'
  AND is_content_read = TRUE
GROUP BY 1 ORDER BY reads_in_that_second DESC LIMIT 10;
\echo ''
\echo '=== sample burst (one busy second, ms precision + process) ==='
SELECT (event_time AT TIME ZONE 'Asia/Tokyo')::time(3) AS t_ms,
       COALESCE(NULLIF(process_name,''),'(empty)') AS proc,
       file_name
FROM audit.audit_logs
WHERE user_name ILIKE '__ACCT__' AND event_time >= now() - interval '1 day'
  AND is_content_read = TRUE
ORDER BY event_time DESC LIMIT 25;
'@

$sql = $sqlTemplate.Replace('__ACCT__', $acct)

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
