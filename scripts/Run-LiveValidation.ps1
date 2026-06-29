# Temporary Live validation on MTSV (READ-ONLY against pg+catalog.db; writes only to a throwaway cache).
# Starts the published app on MTSV with DataMode=Live for ~75s, queries its API, then stops it and
# deletes the temp cache. No service install, no DB writes, no restart of anything.
# Run on the WORKSTATION:
#   powershell -NoProfile -ExecutionPolicy Bypass -File Run-LiveValidation.ps1
# Prompts (secure dialogs): MTSV admin (remoting), viewer role password.
# ASCII-only (PS 5.1). Japanese path/dept built at runtime from code points.
$ErrorActionPreference = 'Stop'

$kanren = [string][char]0x95A2 + [char]0x9023               # 'kanren'
$exe    = 'D:\MTlock' + $kanren + '\system\_p2check\val_app\ViewerAccessLog.exe'
$dept   = [string][char]0x6280 + [char]0x8853 + [char]0x90E8 # 'gijutsu-bu'
$sqlite = 'D:\Apps\SecureFileExplorer\catalog.db'

$mc = Get-Credential -Message 'MTSV (192.168.1.242) admin  [for remoting]'
$vc = Get-Credential -Message 'viewer role password' -UserName 'viewer'
$connstr = "Host=localhost;Port=5432;Database=audit_logger;Username=viewer;Password=" + $vc.GetNetworkCredential().Password + ";Search Path=audit"

$out = Invoke-Command -ComputerName lineworks-mtsv -Credential $mc -ArgumentList $exe, $sqlite, $connstr, $dept -ScriptBlock {
  param($exe, $sqlite, $connstr, $dept)
  if (-not (Test-Path -LiteralPath $exe)) { return "EXE NOT FOUND: $exe" }
  $cache = Join-Path $env:TEMP 'vac_val.db'
  $log   = Join-Path $env:TEMP 'vac_val.log'
  Remove-Item "$cache*", "$log*" -Force -ErrorAction SilentlyContinue

  $envmap = @{
    DataMode                          = 'Live'
    'Live__AuditPg__ConnectionString' = $connstr
    'Live__AuditPg__Schema'           = 'audit'
    'Live__SfeSqlitePath'             = $sqlite
    'Live__CachePath'                 = $cache
    'Live__Dept'                      = $dept
    'Live__LookbackDays'              = '14'
    'Live__SyncIntervalSeconds'       = '3600'
    'Urls'                            = 'http://localhost:5090'
    'ASPNETCORE_ENVIRONMENT'          = 'Production'
  }
  foreach ($kv in $envmap.GetEnumerator()) { [Environment]::SetEnvironmentVariable($kv.Key, $kv.Value, 'Process') }

  $p = Start-Process -FilePath $exe -PassThru -WorkingDirectory (Split-Path $exe -Parent) `
        -RedirectStandardOutput $log -RedirectStandardError "$log.err"
  Start-Sleep -Seconds 75

  $res = New-Object System.Collections.Generic.List[string]
  $q = 'from=2026-06-15T00:00:00%2B09:00&to=2026-07-01T00:00:00%2B09:00'
  foreach ($u in @('http://localhost:5090/api/health', "http://localhost:5090/api/summary?$q")) {
    try { $res.Add($u + "`n" + (Invoke-WebRequest -Uri $u -UseBasicParsing -TimeoutSec 40).Content) }
    catch { $res.Add($u + ' => ERR ' + $_.Exception.Message) }
  }
  try { Stop-Process -Id $p.Id -Force } catch {}
  Start-Sleep -Seconds 2
  $tail = (Get-Content $log -Tail 20 -ErrorAction SilentlyContinue | Out-String) + (Get-Content "$log.err" -Tail 15 -ErrorAction SilentlyContinue | Out-String)
  $res.Add("--- app log tail ---`n" + $tail)
  Remove-Item "$cache*", "$log*" -Force -ErrorAction SilentlyContinue
  $res -join "`n`n"
}
Write-Host $out
