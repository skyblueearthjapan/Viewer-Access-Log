# Rebuild the VAL cache from scratch (stop service, delete cache.db, start -> full backfill).
# Needed after schema/scope changes (e.g., all-departments + UTC time normalization).
# Run on WORKSTATION: powershell -NoProfile -ExecutionPolicy Bypass -File Reset-ViewerAccessLogCache.ps1
# Prompts: MTSV admin only. Does NOT touch appsettings (viewer/config_editor passwords preserved).
# The first all-departments backfill (LookbackDays) may take several minutes; the dashboard fills gradually.
# ASCII-only.
$ErrorActionPreference = 'Stop'

$mc = Get-Credential -Message 'MTSV (192.168.1.242) admin  [for remoting]'

$out = Invoke-Command -ComputerName lineworks-mtsv -Credential $mc -ScriptBlock {
  $name = 'ViewerAccessLog'
  $app  = 'D:\Apps\ViewerAccessLog'
  $log  = New-Object System.Collections.Generic.List[string]

  Stop-Service -Name $name -Force -ErrorAction SilentlyContinue
  Start-Sleep -Seconds 4

  foreach ($f in 'cache.db','cache.db-wal','cache.db-shm') {
    $p = Join-Path $app $f
    if (Test-Path -LiteralPath $p) { Remove-Item -LiteralPath $p -Force -ErrorAction SilentlyContinue; $log.Add("deleted $f") }
  }

  Start-Service -Name $name
  $log.Add('service restarted (full backfill started in background)')
  Start-Sleep -Seconds 20

  $st = (Get-Service -Name $name -ErrorAction SilentlyContinue).Status
  $log.Add("service status: $st")
  try { $h = (Invoke-WebRequest 'http://localhost:5090/api/health' -UseBasicParsing -TimeoutSec 25).Content; $log.Add("health: $h") }
  catch { $log.Add('health: ERR ' + $_.Exception.Message) }

  $log -join "`n"
}
Write-Host $out
Write-Host "`n(backfill continues in background; check the dashboard again in a few minutes for full all-departments data)"
