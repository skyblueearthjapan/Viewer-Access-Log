# Viewer-Access-Log / P2 read-only check (run on MTSV). SELECT-only. No writes, no restart.
# ASCII-only on purpose: Windows PowerShell 5.1 mis-decodes non-ASCII .ps1 without a BOM.
# The Japanese folder token is built at runtime from code points (see $G).
$ErrorActionPreference = 'Continue'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$OutputEncoding = [Text.Encoding]::UTF8
$env:PGCLIENTENCODING = 'UTF8'
Write-Host "===== Viewer-Access-Log : P2 read-only check (MTSV) ====="

# ---- psql ----
$psql = Get-ChildItem 'C:\Program Files\PostgreSQL\*\bin\psql.exe' -ErrorAction SilentlyContinue |
        Select-Object -First 1 -ExpandProperty FullName
Write-Host ("psql.exe : " + ($(if ($psql) { $psql } else { 'NOT FOUND' })))

# ---- AuditLogger connection string (auto-discover) ----
$conn = $null; $src = $null
$cands = Get-ChildItem -Path 'D:\Apps','C:\Apps','D:\AuditLogger','C:\AuditLogger' -Recurse -Filter 'appsettings*.json' -ErrorAction SilentlyContinue |
         Where-Object { Select-String -Path $_.FullName -Pattern 'audit_logger|AuditDb' -Quiet -ErrorAction SilentlyContinue }
foreach ($f in $cands) {
  try {
    $j  = Get-Content -LiteralPath $f.FullName -Raw | ConvertFrom-Json
    $cs = $j.ConnectionStrings.AuditDb
    if ($cs) { $conn = $cs; $src = $f.FullName; break }
  } catch {}
}
Write-Host ("connstr from : " + ($(if ($src) { $src } else { 'NOT FOUND' })))

function Field($cs, $key) {
  ($cs -split ';' | Where-Object { $_ -match "^\s*$key\s*=" } | ForEach-Object { ($_ -split '=', 2)[1].Trim() } | Select-Object -First 1)
}

# Japanese token "gijutsu-bu" built from code points to keep this file ASCII.
$G = -join ([char]0x6280, [char]0x8853, [char]0x90E8)

$sql = @'
\pset pager off
\echo '=== 1. audit_logs columns (is_content_read exists?) ==='
SELECT column_name, data_type FROM information_schema.columns
 WHERE table_schema='audit' AND table_name='audit_logs' ORDER BY ordinal_position;
\echo ''
\echo '=== 2. action enum values ==='
SELECT unnest(enum_range(NULL::audit.action_type))::text AS action_type;
\echo ''
\echo '=== 3. row count / oldest / latest ==='
SELECT count(*) AS total_rows, min(event_time) AS oldest, max(event_time) AS latest FROM audit.audit_logs;
\echo ''
\echo '=== 4. direct user access to target dept folder (MTSV$ excluded, content read) latest 5 ==='
SELECT event_time, server_name, user_name, action::text, is_content_read, left(file_path,80) AS file_path
 FROM audit.audit_logs
 WHERE folder_path ~* '[\\/]__G__([\\/]|$)' AND user_name IS NOT NULL
   AND user_name !~* 'MTSV\$' AND is_content_read = TRUE
 ORDER BY event_time DESC LIMIT 5;
\echo ''
\echo '=== 5. user_name distribution last 7d top15 ==='
SELECT user_name, count(*) AS n FROM audit.audit_logs
 WHERE event_time > now() - interval '7 days' GROUP BY user_name ORDER BY n DESC LIMIT 15;
\echo ''
\echo '=== 6. roles (viewer should NOT exist yet) ==='
SELECT rolname, rolcanlogin FROM pg_roles WHERE rolname IN ('viewer','collector','postgres');
\echo ''
\echo '=== 7. collector_state (GAP health) ==='
SELECT server_name, channel, last_record_id, last_event_time, last_status FROM audit.collector_state ORDER BY server_name, channel;
'@
$sql = $sql.Replace('__G__', $G)

if ($psql -and $conn) {
  $h  = Field $conn 'Host';     if (-not $h)  { $h  = 'localhost' }
  $p  = Field $conn 'Port';     if (-not $p)  { $p  = '5432' }
  $db = Field $conn 'Database'; if (-not $db) { $db = 'audit_logger' }
  $u  = Field $conn 'Username'; if (-not $u)  { $u  = 'postgres' }
  $pw = Field $conn 'Password'
  Write-Host ("connect : Host=$h Port=$p Db=$db User=$u (password hidden)")
  Write-Host ""
  if ($pw) { $env:PGPASSWORD = $pw }
  $sql | & $psql -h $h -p $p -d $db -U $u -v ON_ERROR_STOP=0 -f -
  Remove-Item Env:PGPASSWORD -ErrorAction SilentlyContinue
} else {
  Write-Host "psql or connstr not found -> use pgAdmin with p2-readonly-check.sql instead."
}

# ---- SFE SQLite location (source of viewer logs) ----
Write-Host "`n===== SFE SQLite DB files under D:\Apps ====="
$dbs = Get-ChildItem -Path 'D:\Apps' -Recurse -Include '*.db','*.sqlite','*.sqlite3' -ErrorAction SilentlyContinue |
       Select-Object FullName, Length, LastWriteTime
if ($dbs) { $dbs | Format-Table -AutoSize | Out-String | Write-Host }
else { Write-Host "  no .db/.sqlite under D:\Apps" }

Write-Host "`n===== done (read-only, no changes) ====="
