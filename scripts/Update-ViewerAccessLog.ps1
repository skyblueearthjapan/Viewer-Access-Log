# Update the running ViewerAccessLog service binaries only (keeps appsettings.json + cache.db).
# Use this for code updates after the first full Deploy. No viewer password needed (config preserved).
# Run on WORKSTATION: powershell -NoProfile -ExecutionPolicy Bypass -File Update-ViewerAccessLog.ps1
# Prereq: new publish staged at the share \\...\_p2check\val_app.
# Prompts: MTSV admin only. ASCII-only.
$ErrorActionPreference = 'Stop'

$kanren = [string][char]0x95A2 + [char]0x9023
$src    = 'D:\MTlock' + $kanren + '\system\_p2check\val_app'

$mc = Get-Credential -Message 'MTSV (192.168.1.242) admin  [for remoting]'

$out = Invoke-Command -ComputerName lineworks-mtsv -Credential $mc -ArgumentList $src -ScriptBlock {
  param($src)
  $name = 'ViewerAccessLog'
  $app  = 'D:\Apps\ViewerAccessLog'
  if (-not (Test-Path -LiteralPath (Join-Path $src 'ViewerAccessLog.exe'))) { return "SRC EXE NOT FOUND: $src" }

  Stop-Service -Name $name -Force -ErrorAction SilentlyContinue
  Start-Sleep -Seconds 3
  # mirror binaries but keep production config + cache (exclude them from copy AND delete)
  & robocopy $src $app /MIR /XF appsettings.json cache.db cache.db-wal cache.db-shm /R:1 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null
  $rc = $LASTEXITCODE
  Start-Service -Name $name
  Start-Sleep -Seconds 12

  $st = (Get-Service -Name $name -ErrorAction SilentlyContinue).Status
  $res = "robocopy rc=$rc (0-7 ok); service=$st"
  foreach ($u in @('http://localhost:5090/api/health',
                   'http://localhost:5090/api/settings')) {
    try {
      $c = (Invoke-WebRequest $u -UseBasicParsing -TimeoutSec 25).Content
      if ($c.Length -gt 500) { $c = $c.Substring(0, 500) + '...' }
      $res += "`n$u => $c"
    } catch { $res += "`n$u => ERR " + $_.Exception.Message }
  }
  $res
}
Write-Host $out
