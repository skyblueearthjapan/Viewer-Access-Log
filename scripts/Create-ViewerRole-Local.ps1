# Run on the WORKSTATION:
#   powershell -NoProfile -ExecutionPolicy Bypass -File Create-ViewerRole-Local.ps1
# Prompts (secure dialogs) for: MTSV admin (remoting), postgres password, and a NEW viewer-role password.
# Creates the SELECT-only 'viewer' role on MTSV PostgreSQL. GRANT only. No data change, no restart.
# Passwords are entered in dialogs and never printed (not in chat, not in files).
# ASCII-only file (PS 5.1). The SQL (with Japanese comments) is read from create-viewer-role.sql and
# passed to the remote psql via stdin with UTF-8 client encoding.
$ErrorActionPreference = 'Stop'

$here    = Split-Path -Parent $MyInvocation.MyCommand.Path
$sqlPath = Join-Path $here 'create-viewer-role.sql'
if (-not (Test-Path -LiteralPath $sqlPath)) { throw "not found: $sqlPath" }
$sqlText = Get-Content -LiteralPath $sqlPath -Raw

$mc = Get-Credential -Message 'MTSV (192.168.1.242) admin  [for remoting]'
$pg = Get-Credential -Message 'PostgreSQL superuser' -UserName 'postgres'
$vc = Get-Credential -Message 'NEW password for the viewer role (type it twice-safe)' -UserName 'viewer'
$pgpw = $pg.GetNetworkCredential().Password
$vpw  = $vc.GetNetworkCredential().Password

Invoke-Command -ComputerName lineworks-mtsv -Credential $mc -ArgumentList $pgpw, $vpw, $sqlText -ScriptBlock {
  param($pgpw, $vpw, $sqlText)
  try { [Console]::OutputEncoding = [Text.Encoding]::UTF8 } catch {}
  try { $OutputEncoding = [Text.Encoding]::UTF8 } catch {}
  $env:PGCLIENTENCODING = 'UTF8'
  $psql = (Get-ChildItem 'D:\PostgreSQL\*\bin\psql.exe','C:\Program Files\PostgreSQL\*\bin\psql.exe' -ErrorAction SilentlyContinue |
           Select-Object -First 1 -ExpandProperty FullName)
  if (-not $psql) { Write-Host 'psql.exe not found on MTSV'; return }
  $env:PGPASSWORD = $pgpw
  $sql = $sqlText.Replace('__VPW__', $vpw.Replace("'", "''"))   # escape single quotes for SQL literal
  $sql | & $psql -U postgres -h localhost -d audit_logger -v ON_ERROR_STOP=1 -f -
  Remove-Item Env:PGPASSWORD -ErrorAction SilentlyContinue
}
Write-Host "`n(done. If the viewer role was created/granted with no error -> success. Paste any error output.)"
