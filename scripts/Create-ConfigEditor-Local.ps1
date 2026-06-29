# Create the limited-write 'config_editor' role + config_audit_log table on MTSV PostgreSQL,
# then add the config_editor connection string into the running app's appsettings.json and restart.
# Run on WORKSTATION: powershell -NoProfile -ExecutionPolicy Bypass -File Create-ConfigEditor-Local.ps1
# Prompts (secure): MTSV admin, postgres superuser, NEW config_editor password.
# Writes ONLY to config tables (via the role) and to the app config file. Log bodies stay read-only.
# ASCII-only (PS 5.1).
$ErrorActionPreference = 'Stop'

$here    = Split-Path -Parent $MyInvocation.MyCommand.Path
$sqlPath = Join-Path $here 'create-config-editor.sql'
if (-not (Test-Path -LiteralPath $sqlPath)) { throw "not found: $sqlPath" }
$sqlText = Get-Content -LiteralPath $sqlPath -Raw

$mc = Get-Credential -Message 'MTSV (192.168.1.242) admin  [for remoting]'
$pg = Get-Credential -Message 'PostgreSQL superuser' -UserName 'postgres'
$cc = Get-Credential -Message 'NEW config_editor role password' -UserName 'config_editor'
$pgpw = $pg.GetNetworkCredential().Password
$cpw  = $cc.GetNetworkCredential().Password

$out = Invoke-Command -ComputerName lineworks-mtsv -Credential $mc -ArgumentList $pgpw, $cpw, $sqlText -ScriptBlock {
  param($pgpw, $cpw, $sqlText)
  $ErrorActionPreference = 'Continue'
  try { [Console]::OutputEncoding = [Text.Encoding]::UTF8 } catch {}
  try { $OutputEncoding = [Text.Encoding]::UTF8 } catch {}
  $env:PGCLIENTENCODING = 'UTF8'
  $log = New-Object System.Collections.Generic.List[string]

  $psql = (Get-ChildItem 'D:\PostgreSQL\*\bin\psql.exe','C:\Program Files\PostgreSQL\*\bin\psql.exe' -ErrorAction SilentlyContinue |
           Select-Object -First 1 -ExpandProperty FullName)
  if (-not $psql) { return 'psql.exe not found' }

  # 1) create role + audit table + grants (as postgres)
  $sql = $sqlText.Replace('__CPW__', $cpw.Replace("'", "''"))
  $env:PGPASSWORD = $pgpw
  $sqlOut = ($sql | & $psql -U postgres -h localhost -d audit_logger -v ON_ERROR_STOP=1 -f - 2>&1 | Out-String)
  Remove-Item Env:PGPASSWORD -ErrorAction SilentlyContinue
  $log.Add("=== psql ===`n$sqlOut")

  # 2) add Live.ConfigPg into the running app's appsettings.json (keep everything else)
  $appcfg = 'D:\Apps\ViewerAccessLog\appsettings.json'
  if (Test-Path -LiteralPath $appcfg) {
    $conn = "Host=localhost;Port=5432;Database=audit_logger;Username=config_editor;Password=$cpw;Search Path=audit"
    $j = Get-Content -LiteralPath $appcfg -Raw | ConvertFrom-Json
    $cfgpg = [pscustomobject]@{ ConnectionString = $conn; Schema = 'audit' }
    if ($j.Live.PSObject.Properties.Name -contains 'ConfigPg') { $j.Live.ConfigPg = $cfgpg }
    else { $j.Live | Add-Member -NotePropertyName ConfigPg -NotePropertyValue $cfgpg }
    ($j | ConvertTo-Json -Depth 10) | Set-Content -Path $appcfg -Encoding UTF8
    & icacls $appcfg /inheritance:r /grant 'SYSTEM:R' 'Administrators:F' | Out-Null
    $log.Add('appsettings.json: Live.ConfigPg added (ACL restricted)')
  } else {
    $log.Add("appsettings.json NOT FOUND at $appcfg (deploy the app first)")
  }

  # 3) restart the service to pick up ConfigPg
  Restart-Service -Name 'ViewerAccessLog' -Force -ErrorAction SilentlyContinue
  Start-Sleep -Seconds 10
  $log.Add('service status: ' + (Get-Service -Name 'ViewerAccessLog' -ErrorAction SilentlyContinue).Status)

  $log -join "`n"
}
Write-Host $out
