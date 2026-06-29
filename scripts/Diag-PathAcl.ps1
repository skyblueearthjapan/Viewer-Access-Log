# READ-ONLY: show the NTFS ACL from a user's actually-read gijutsu-bu path up to the dept root,
# to find where Allow leaks / inheritance is broken (i.e., why a Deny is not effective).
# Path is fetched from the DB (no manual retyping). icacls is read-only; the share is NOT modified.
# Run on WORKSTATION: powershell -NoProfile -ExecutionPolicy Bypass -File Diag-PathAcl.ps1
# Prompts: MTSV admin (to query PG), viewer password. icacls runs from the workstation against the UNC path
# using your current credentials (single hop, no double-hop).
# ASCII-only; Japanese built from code points at runtime.
$ErrorActionPreference = 'Stop'

$dept     = [string][char]0x6280 + [char]0x8853 + [char]0x90E8   # gijutsu-bu (folder name)
$targetUser = 'kataoka'

# 1) get the user's most-recent gijutsu-bu folder_path from audit_logs (authoritative UTF-8)
$query = "SELECT folder_path FROM audit.audit_logs WHERE user_name = '$targetUser' AND folder_path ILIKE '%$dept%' ORDER BY event_time DESC LIMIT 1;"

$mc = Get-Credential -Message 'MTSV (192.168.1.242) admin  [for PG query]'
$vc = Get-Credential -Message 'viewer role password' -UserName 'viewer'

$path = Invoke-Command -ComputerName lineworks-mtsv -Credential $mc -ArgumentList $vc.GetNetworkCredential().Password, $query -ScriptBlock {
  param($vpw, $q)
  try { [Console]::OutputEncoding = [Text.Encoding]::UTF8 } catch {}
  try { $OutputEncoding = [Text.Encoding]::UTF8 } catch {}
  $env:PGCLIENTENCODING = 'UTF8'
  $psql = (Get-ChildItem 'D:\PostgreSQL\*\bin\psql.exe','C:\Program Files\PostgreSQL\*\bin\psql.exe' -ErrorAction SilentlyContinue |
           Select-Object -First 1 -ExpandProperty FullName)
  if (-not $psql) { return '' }
  $env:PGPASSWORD = $vpw
  $out = ($q | & $psql -U viewer -h localhost -d audit_logger -t -A -f -)
  Remove-Item Env:PGPASSWORD -ErrorAction SilentlyContinue
  ($out | Select-Object -First 1)
}

if ([string]::IsNullOrWhiteSpace($path)) { Write-Host "no gijutsu-bu path found for user '$targetUser'"; return }
Write-Host ("DB folder_path: " + $path)

# 2) normalize any prefix (D:\Data\... or \\*\Data\...) to \\lineworks-sv\Data\...
$marker = 'Data\'
$idx = $path.IndexOf($marker)
if ($idx -lt 0) { Write-Host ("cannot find 'Data\' in path: " + $path); return }
$unc = '\\lineworks-sv\' + $path.Substring($idx)
Write-Host ("UNC: " + $unc)
Write-Host ""

# 3) walk up to and including the dept folder, printing each level's ACL
$ErrorActionPreference = 'Continue'   # do not stop on icacls stderr
$cur = $unc
$guard = 0
while ($cur -and $cur.Contains($dept) -and $guard -lt 20) {
  $guard++
  Write-Host "==================================================================="
  Write-Host ("ACL: " + $cur)
  Write-Host "-------------------------------------------------------------------"
  (& icacls "$cur" 2>&1) | ForEach-Object { Write-Host $_ }
  Write-Host ""
  $parent = Split-Path -Path $cur -Parent
  if (-not $parent -or $parent -eq $cur) { break }
  $cur = $parent
}
