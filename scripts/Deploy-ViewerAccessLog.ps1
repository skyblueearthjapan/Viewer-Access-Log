# Permanent deploy of Viewer-Access-Log on MTSV as a Windows service (DataMode=Live).
# Run on the WORKSTATION:
#   powershell -NoProfile -ExecutionPolicy Bypass -File Deploy-ViewerAccessLog.ps1
# Prereq: the published app already copied to the share \\...\_p2check\val_app.
# Prompts (secure): MTSV admin (remoting), viewer role password.
# Reads pg as the read-only 'viewer' role and SFE catalog.db read-only. No writes to those systems.
# ASCII-only (PS 5.1). Japanese path/dept built at runtime from code points.
$ErrorActionPreference = 'Stop'

$kanren = [string][char]0x95A2 + [char]0x9023                 # 'kanren'
$dept   = [string][char]0x6280 + [char]0x8853 + [char]0x90E8  # 'gijutsu-bu'
$srcShare = 'D:\MTlock' + $kanren + '\system\_p2check\val_app'

$mc = Get-Credential -Message 'MTSV (192.168.1.242) admin  [for remoting]'
$vc = Get-Credential -Message 'viewer role password' -UserName 'viewer'
$vpw = $vc.GetNetworkCredential().Password

$out = Invoke-Command -ComputerName lineworks-mtsv -Credential $mc -ArgumentList $vpw, $dept, $srcShare -ScriptBlock {
  param($vpw, $dept, $src)
  $ErrorActionPreference = 'Continue'
  $name = 'ViewerAccessLog'
  $app  = 'D:\Apps\ViewerAccessLog'
  $log  = New-Object System.Collections.Generic.List[string]

  if (-not (Test-Path -LiteralPath (Join-Path $src 'ViewerAccessLog.exe'))) { return "SRC EXE NOT FOUND: $src" }

  # 1) stop & delete existing service if present (idempotent)
  $svc = Get-Service -Name $name -ErrorAction SilentlyContinue
  if ($svc) {
    Stop-Service -Name $name -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2
    & sc.exe delete $name | Out-Null
    Start-Sleep -Seconds 2
    $log.Add('existing service stopped & deleted')
  }

  # 2) deploy (share -> D:\Apps\ViewerAccessLog)
  New-Item -ItemType Directory -Force -Path $app | Out-Null
  & robocopy $src $app /MIR /R:1 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null
  $log.Add("deployed to $app (robocopy rc=$LASTEXITCODE)")

  # 3) write appsettings.json (Live). ConvertTo-Json escapes; password lives only in this file.
  $conn = "Host=localhost;Port=5432;Database=audit_logger;Username=viewer;Password=$vpw;Search Path=audit"
  $cfg = [ordered]@{
    Logging  = @{ LogLevel = @{ Default = 'Information'; 'Microsoft.AspNetCore' = 'Warning' } }
    DataMode = 'Live'
    Urls     = 'http://0.0.0.0:5090'
    Live = [ordered]@{
      AuditPg = [ordered]@{ ConnectionString = $conn; Schema = 'audit' }
      SfeSqlitePath       = 'D:\Apps\SecureFileExplorer\catalog.db'
      CachePath           = "$app\cache.db"
      Dept                = $dept
      SyncIntervalSeconds = 300
      LookbackDays        = 14
    }
  }
  $json = $cfg | ConvertTo-Json -Depth 8
  Set-Content -Path "$app\appsettings.json" -Value $json -Encoding UTF8
  # appsettings.json holds credentials -> SYSTEM/Administrators only
  & icacls "$app\appsettings.json" /inheritance:r /grant 'SYSTEM:R' 'Administrators:F' | Out-Null
  $log.Add('appsettings.json written (Live, ACL restricted)')

  # 4) create service (LocalSystem, automatic start)
  New-Service -Name $name -BinaryPathName "$app\ViewerAccessLog.exe" -DisplayName 'Viewer Access Log' -StartupType Automatic | Out-Null
  Start-Service -Name $name
  $log.Add('service created & started')

  # 5) firewall (inbound TCP 5090, Domain/Private)
  Remove-NetFirewallRule -DisplayName 'ViewerAccessLog 5090' -ErrorAction SilentlyContinue
  New-NetFirewallRule -DisplayName 'ViewerAccessLog 5090' -Direction Inbound -Protocol TCP -LocalPort 5090 -Action Allow -Profile Domain,Private | Out-Null
  $log.Add('firewall rule for TCP 5090 added')

  # 6) verify
  Start-Sleep -Seconds 12
  $st = (Get-Service -Name $name -ErrorAction SilentlyContinue).Status
  $log.Add("service status: $st")
  try { $h = (Invoke-WebRequest 'http://localhost:5090/api/health' -UseBasicParsing -TimeoutSec 20).Content; $log.Add("health: $h") }
  catch { $log.Add('health: ERR ' + $_.Exception.Message) }

  $log -join "`n"
}
Write-Host $out
Write-Host "`n(open from an admin PC: http://lineworks-mtsv:5090  - initial backfill takes a few minutes)"
