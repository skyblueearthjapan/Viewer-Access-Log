# Verify the read-only 'viewer' role can run the real dept direct-access query. READ-ONLY (SELECT only).
# Run on the WORKSTATION:
#   powershell -NoProfile -ExecutionPolicy Bypass -File Test-ViewerQuery.ps1
# Prompts (secure dialogs): MTSV admin (remoting), viewer role password.
# ASCII-only (PS 5.1). The dept token (Japanese) is built at runtime and substituted into the SQL.
$ErrorActionPreference = 'Stop'

$sqlText = @'
\pset pager off
SELECT current_user AS connected_as;
\echo '--- direct access to dept, last 7 days (can viewer read it?) ---'
SELECT count(*) AS direct_rows_7d
FROM audit.audit_logs
WHERE folder_path ~* ('[\\/]__DEPT__([\\/]|$)')
  AND user_name IS NOT NULL AND user_name !~* 'MTSV\$'
  AND user_name !~* '(NETWORK SERVICE|LOCAL SYSTEM|^SYSTEM$|svc[-_])'
  AND is_content_read = TRUE AND result::text = 'Success'
  AND event_time > now() - interval '7 days';
\echo '--- latest 3 direct rows ---'
SELECT event_time, user_name, left(file_path, 70) AS file_path
FROM audit.audit_logs
WHERE folder_path ~* ('[\\/]__DEPT__([\\/]|$)')
  AND user_name IS NOT NULL AND user_name !~* 'MTSV\$'
  AND user_name !~* '(NETWORK SERVICE|LOCAL SYSTEM|^SYSTEM$|svc[-_])'
  AND is_content_read = TRUE AND result::text = 'Success'
ORDER BY event_time DESC LIMIT 3;
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
  if (-not $psql) { Write-Host 'psql.exe not found'; return }
  $dept = -join ([char]0x6280, [char]0x8853, [char]0x90E8)   # gijutsu-bu
  $sql  = $sqlText.Replace('__DEPT__', $dept)
  $env:PGPASSWORD = $vpw
  $sql | & $psql -U viewer -h localhost -d audit_logger -v ON_ERROR_STOP=0 -f -
  Remove-Item Env:PGPASSWORD -ErrorAction SilentlyContinue
}
Write-Host "`n(done. connected_as should be 'viewer'; direct_rows_7d > 0 means the viewer role can read the real query.)"
