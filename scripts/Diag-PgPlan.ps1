# Diagnose query plans for the dept slice (READ-ONLY, EXPLAIN only = no execution).
# Tells us whether file_path ILIKE uses the trigram GIN index, or falls back to a seq scan.
# Run on WORKSTATION: powershell -NoProfile -ExecutionPolicy Bypass -File Diag-PgPlan.ps1
# Prompts: MTSV admin, viewer password. ASCII-only; dept built at runtime.
$ErrorActionPreference = 'Stop'

$sqlText = @'
\echo '=== A) file_path ILIKE only (expect Bitmap Index Scan on *_filepath_trgm) ==='
EXPLAIN
SELECT id FROM audit.audit_logs
WHERE file_path ILIKE '%__DEPT__%'
  AND is_content_read = TRUE AND result::text = 'Success'
  AND event_time >= now() - interval '14 days'
LIMIT 200000;
\echo ''
\echo '=== B) event_time window 1h + ILIKE (expect index scan on event_time) ==='
EXPLAIN
SELECT id FROM audit.audit_logs
WHERE event_time >= now() - interval '1 hour'
  AND file_path ILIKE '%__DEPT__%' AND is_content_read = TRUE AND result::text = 'Success';
\echo ''
\echo '=== C) timed COUNT via ILIKE (executes; shows match count + time) ==='
\timing on
SELECT count(*) FROM audit.audit_logs
WHERE file_path ILIKE '%__DEPT__%'
  AND is_content_read = TRUE AND result::text = 'Success'
  AND event_time >= now() - interval '14 days';
\timing off
'@

$mc = Get-Credential -Message 'MTSV (192.168.1.242) admin  [for remoting]'
$vc = Get-Credential -Message 'viewer role password' -UserName 'viewer'
$vpw = $vc.GetNetworkCredential().Password

Invoke-Command -ComputerName lineworks-mtsv -Credential $mc -ArgumentList $vpw, $sqlText -ScriptBlock {
  param($vpw, $sqlText)
  try { [Console]::OutputEncoding = [Text.Encoding]::UTF8 } catch {}
  try { $OutputEncoding = [Text.Encoding]::UTF8 } catch {}
  $env:PGCLIENTENCODING = 'UTF8'
  $psql = (Get-ChildItem 'D:\PostgreSQL\*\bin\psql.exe','C:\Program Files\PostgreSQL\*\bin\psql.exe' -ErrorAction SilentlyContinue |
           Select-Object -First 1 -ExpandProperty FullName)
  if (-not $psql) { Write-Host 'psql not found'; return }
  $dept = -join ([char]0x6280, [char]0x8853, [char]0x90E8)
  $sql  = $sqlText.Replace('__DEPT__', $dept)
  $env:PGPASSWORD = $vpw
  $sql | & $psql -U viewer -h localhost -d audit_logger -v ON_ERROR_STOP=0 -f -
  Remove-Item Env:PGPASSWORD -ErrorAction SilentlyContinue
}
