# Snapshot SFE catalog.db (READ-ONLY copy) to the _p2check share for offline schema inspection.
# No DB writes, no service restart. ASCII-only (PS 5.1 mis-decodes non-ASCII no-BOM .ps1).
# The share folder name contains Japanese, so it is built from code points at runtime.
$ErrorActionPreference = 'Continue'

# D:\MTlock<kan><ren>\system\_p2check\sfe_snapshot   (<kan>=U+95A2 <ren>=U+9023)
$share = 'D:\MTlock' + [char]0x95A2 + [char]0x9023 + '\system\_p2check\sfe_snapshot'
New-Item -ItemType Directory -Force -Path $share | Out-Null
Write-Host "snapshot dir: $share"

$srcs = @('D:\Apps\SecureFileExplorer', 'D:\Apps\SecureFileExplorerAll')
foreach ($s in $srcs) {
  $tag = Split-Path $s -Leaf
  $d = Join-Path $share $tag
  New-Item -ItemType Directory -Force -Path $d | Out-Null
  foreach ($ext in 'catalog.db', 'catalog.db-wal', 'catalog.db-shm') {
    $f = Join-Path $s $ext
    if (Test-Path -LiteralPath $f) {
      Copy-Item -LiteralPath $f -Destination $d -Force -ErrorAction SilentlyContinue
      Write-Host ("  copied: $tag\$ext")
    }
  }
}
Write-Host ""
Get-ChildItem -LiteralPath $share -Recurse -File | Select-Object Length, FullName | Format-Table -AutoSize | Out-String | Write-Host
Write-Host "done (read-only copy)"
