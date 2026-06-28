# Viewer-Access-Log / P2 本番確認ランナー（MTSV上で実行・読み取り専用）
# - PostgreSQL(audit_logger) を SELECT のみで確認（psql 自動検出・接続文字列を AuditLogger の appsettings から取得）
# - SFE の SQLite(DBファイル) の所在を確認
# 再起動・書き込みは一切しない。業務中に流しても安全。
#
# 実行: 右クリック→PowerShellで実行、または
#   powershell -NoProfile -ExecutionPolicy Bypass -File .\Run-P2Check.ps1
#
$ErrorActionPreference = 'Continue'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
Write-Host "===== Viewer-Access-Log : P2 読み取り専用チェック (MTSV) =====`n"

# ---- 1. psql 検出 ----
$psql = Get-ChildItem 'C:\Program Files\PostgreSQL\*\bin\psql.exe' -ErrorAction SilentlyContinue |
        Select-Object -First 1 -ExpandProperty FullName
Write-Host ("psql.exe : " + ($(if ($psql) { $psql } else { '見つからず（pgAdminで p2-readonly-check.sql を実行してください）' })))

# ---- 2. AuditLogger の接続文字列を自動探索 ----
$conn = $null; $src = $null
$cands = Get-ChildItem -Path 'D:\Apps','C:\Apps','D:\AuditLogger','C:\AuditLogger' -Recurse -Filter 'appsettings*.json' -ErrorAction SilentlyContinue |
         Where-Object { Select-String -Path $_.FullName -Pattern 'audit_logger|AuditDb' -Quiet -ErrorAction SilentlyContinue }
foreach ($f in $cands) {
  try {
    $j = Get-Content -LiteralPath $f.FullName -Raw | ConvertFrom-Json
    $cs = $j.ConnectionStrings.AuditDb
    if (-not $cs) { $cs = $j.ConnectionStrings.PSObject.Properties.Value | Where-Object { $_ -match 'audit_logger' } | Select-Object -First 1 }
    if ($cs) { $conn = $cs; $src = $f.FullName; break }
  } catch {}
}
Write-Host ("接続文字列の取得元 : " + ($(if ($src) { $src } else { '見つからず' })))

function Field($cs, $key) {
  ($cs -split ';' | Where-Object { $_ -match "^\s*$key\s*=" } | ForEach-Object { ($_ -split '=', 2)[1].Trim() } | Select-Object -First 1)
}

# ---- 3. SQL（このスクリプトに同梱・全てSELECT） ----
$sql = @'
\pset pager off
\echo '=== 1. audit_logs カラム一覧（is_content_read 等の実在確認） ==='
SELECT column_name, data_type FROM information_schema.columns
 WHERE table_schema='audit' AND table_name='audit_logs' ORDER BY ordinal_position;
\echo ''
\echo '=== 2. action 列挙値 ==='
SELECT unnest(enum_range(NULL::audit.action_type))::text AS action_type;
\echo ''
\echo '=== 3. 件数・最古/最新イベント時刻 ==='
SELECT count(*) AS total_rows, min(event_time) AS oldest, max(event_time) AS latest FROM audit.audit_logs;
\echo ''
\echo '=== 4. 技術部への実ユーザー直接アクセス(MTSV$除外・内容読取) 直近5件 ==='
SELECT event_time, server_name, user_name, action::text, is_content_read, left(file_path,80) AS file_path
 FROM audit.audit_logs
 WHERE folder_path ~* '[\\/]技術部([\\/]|$)' AND user_name IS NOT NULL
   AND user_name !~* 'MTSV\$' AND is_content_read = TRUE
 ORDER BY event_time DESC LIMIT 5;
\echo ''
\echo '=== 5. 直近7日の user_name 分布 上位15 ==='
SELECT user_name, count(*) AS n FROM audit.audit_logs
 WHERE event_time > now() - interval '7 days' GROUP BY user_name ORDER BY n DESC LIMIT 15;
\echo ''
\echo '=== 6. ロールの有無(viewerは未作成のはず) ==='
SELECT rolname, rolcanlogin FROM pg_roles WHERE rolname IN ('viewer','collector','postgres');
\echo ''
\echo '=== 7. 収集状態(GAP健全性) ==='
SELECT server_name, channel, last_record_id, last_event_time, last_status
 FROM audit.collector_state ORDER BY server_name, channel;
'@

# ---- 4. 実行 ----
if ($psql -and $conn) {
  $h = Field $conn 'Host'; if (-not $h) { $h = 'localhost' }
  $p = Field $conn 'Port'; if (-not $p) { $p = '5432' }
  $db = Field $conn 'Database'; if (-not $db) { $db = 'audit_logger' }
  $u = Field $conn 'Username'; if (-not $u) { $u = 'postgres' }
  $pw = Field $conn 'Password'
  Write-Host ("接続先 : Host=$h Port=$p Db=$db User=$u （パスワードは表示しません）`n")
  if ($pw) { $env:PGPASSWORD = $pw }
  $sql | & $psql -h $h -p $p -d $db -U $u -v ON_ERROR_STOP=0 -f -
  Remove-Item Env:PGPASSWORD -ErrorAction SilentlyContinue
} else {
  Write-Host "`n!! psql か接続文字列が自動取得できませんでした。pgAdmin 等で scripts/p2-readonly-check.sql を実行し、結果を貼ってください。"
}

# ---- 5. SFE の SQLite 所在 ----
Write-Host "`n===== SFE の DBファイル所在（🟦ビューアーログの元） ====="
$dbs = Get-ChildItem -Path 'D:\Apps' -Recurse -Include '*.db','*.sqlite','*.sqlite3' -ErrorAction SilentlyContinue |
       Select-Object FullName, Length, LastWriteTime
if ($dbs) { $dbs | Format-Table -AutoSize | Out-String | Write-Host }
else { Write-Host "  D:\Apps 配下に .db/.sqlite が見つかりません（配置先が異なる可能性）。" }

Write-Host "`n===== 完了（読み取りのみ・無変更） ====="
