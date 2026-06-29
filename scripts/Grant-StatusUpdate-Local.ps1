# P4b: grant config_editor the minimal column-level UPDATE(status) on alert_histories / detected_incidents.
# Run on WORKSTATION: powershell -NoProfile -ExecutionPolicy Bypass -File Grant-StatusUpdate-Local.ps1
# Prompts (secure): MTSV admin, postgres superuser. No new password (grants to existing config_editor role).
# Does NOT touch appsettings or the service. ASCII-only.
$ErrorActionPreference = 'Stop'

$here    = Split-Path -Parent $MyInvocation.MyCommand.Path
$sqlPath = Join-Path $here 'grant-status-update.sql'
if (-not (Test-Path -LiteralPath $sqlPath)) { throw "not found: $sqlPath" }
$sqlText = Get-Content -LiteralPath $sqlPath -Raw

$mc = Get-Credential -Message 'MTSV (192.168.1.242) admin  [for remoting]'
$pg = Get-Credential -Message 'PostgreSQL superuser' -UserName 'postgres'

$out = Invoke-Command -ComputerName lineworks-mtsv -Credential $mc -ArgumentList $pg.GetNetworkCredential().Password, $sqlText -ScriptBlock {
  param($pgpw, $sqlText)
  try { [Console]::OutputEncoding = [Text.Encoding]::UTF8 } catch {}
  try { $OutputEncoding = [Text.Encoding]::UTF8 } catch {}
  $env:PGCLIENTENCODING = 'UTF8'
  $psql = (Get-ChildItem 'D:\PostgreSQL\*\bin\psql.exe','C:\Program Files\PostgreSQL\*\bin\psql.exe' -ErrorAction SilentlyContinue |
           Select-Object -First 1 -ExpandProperty FullName)
  if (-not $psql) { return 'psql not found' }
  $env:PGPASSWORD = $pgpw
  $o = ($sqlText | & $psql -U postgres -h localhost -d audit_logger -v ON_ERROR_STOP=1 -f - 2>&1 | Out-String)
  Remove-Item Env:PGPASSWORD -ErrorAction SilentlyContinue
  $o
}
Write-Host $out
