# READ-ONLY: why is a given user classified as DIRECT (not viewer)?
# Shows audit_logs columns, the user's dept, recent rows (all columns incl process if any),
# and the user_name breakdown for a sample file (viewer=MTSV$ would appear if viewer was used).
# Run on WORKSTATION. Prompts: MTSV admin, viewer password. ASCII-only (names via placeholders).
$ErrorActionPreference = 'Stop'

$dept = [string][char]0x6280 + [char]0x8853 + [char]0x90E8   # gijutsu-bu
$kata = [string][char]0x7247 + [char]0x5CA1                  # Kataoka surname

$sqlTemplate = @'
\pset pager off
\echo '=== audit_logs columns ==='
SELECT string_agg(column_name, ', ' ORDER BY ordinal_position) AS columns
FROM information_schema.columns WHERE table_schema='audit' AND table_name='audit_logs';
\echo ''
\echo '=== Kataoka user record(s) ==='
SELECT user_name, domain_name, display_name, department, role, enabled
FROM audit.users WHERE display_name LIKE '%__KATA__%' OR user_name ILIKE '%kataoka%';
\echo ''
\echo '=== recent gijutsu-bu rows by Kataoka-like user (ALL columns, 2 rows) ==='
SELECT * FROM audit.audit_logs
WHERE (folder_path ILIKE '%__DEPT__%' OR file_path ILIKE '%__DEPT__%')
  AND user_name IN (SELECT user_name FROM audit.users WHERE display_name LIKE '%__KATA__%' OR user_name ILIKE '%kataoka%')
  AND event_time >= now() - interval '24 hours'
ORDER BY event_time DESC LIMIT 2;
\echo ''
\echo '=== who accessed file 12672158 in last 24h (user_name breakdown) ==='
SELECT user_name, count(*) AS hits
FROM audit.audit_logs
WHERE file_path ILIKE '%12672158%' AND event_time >= now() - interval '24 hours'
GROUP BY user_name ORDER BY hits DESC;
\echo ''
\echo '=== does the viewer (MTSV$) ever appear for gijutsu-bu in last 24h? ==='
SELECT user_name, count(*) AS hits
FROM audit.audit_logs
WHERE (folder_path ILIKE '%__DEPT__%' OR file_path ILIKE '%__DEPT__%')
  AND event_time >= now() - interval '24 hours'
  AND (user_name ILIKE '%MTSV%' OR user_name ILIKE '%$' )
GROUP BY user_name ORDER BY hits DESC LIMIT 10;
'@

$sql = $sqlTemplate.Replace('__DEPT__', $dept).Replace('__KATA__', $kata)

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
  $sql | & $psql -U viewer -h localhost -d audit_logger -v ON_ERROR_STOP=0 -x -f -
  Remove-Item Env:PGPASSWORD -ErrorAction SilentlyContinue
}
