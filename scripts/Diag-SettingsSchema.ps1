# Dump the real column names of the AuditLogger config tables (READ-ONLY).
# Run on WORKSTATION: powershell -NoProfile -ExecutionPolicy Bypass -File Diag-SettingsSchema.ps1
# Prompts: MTSV admin, viewer password. ASCII-only.
$ErrorActionPreference = 'Stop'

$sqlText = @'
\pset pager off
\echo '=== config/aux table columns ==='
SELECT table_name, string_agg(column_name || ':' || data_type, ', ' ORDER BY ordinal_position) AS cols
FROM information_schema.columns
WHERE table_schema = 'audit'
  AND table_name IN ('monitored_folders','users','alert_rules','detection_exclusions',
                     'common_folders','user_folder_grants','app_settings','collector_state',
                     'detected_incidents','alert_histories')
GROUP BY table_name
ORDER BY table_name;
\echo ''
\echo '=== app_settings (key/value) ==='
SELECT key, left(value, 50) AS value FROM audit.app_settings ORDER BY key;
\echo ''
\echo '=== collector_state ==='
SELECT server_name, channel, last_event_time, last_status FROM audit.collector_state;
\echo ''
\echo '=== row counts ==='
SELECT 'monitored_folders' t, count(*) n FROM audit.monitored_folders
UNION ALL SELECT 'users', count(*) FROM audit.users
UNION ALL SELECT 'alert_rules', count(*) FROM audit.alert_rules
UNION ALL SELECT 'detection_exclusions', count(*) FROM audit.detection_exclusions
UNION ALL SELECT 'common_folders', count(*) FROM audit.common_folders
UNION ALL SELECT 'user_folder_grants', count(*) FROM audit.user_folder_grants;
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
  $env:PGPASSWORD = $vpw
  $sqlText | & $psql -U viewer -h localhost -d audit_logger -v ON_ERROR_STOP=0 -f -
  Remove-Item Env:PGPASSWORD -ErrorAction SilentlyContinue
}
