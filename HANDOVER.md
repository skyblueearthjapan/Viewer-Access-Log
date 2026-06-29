# HANDOVER — Viewer-Access-Log（引き継ぎ書）

> このファイル1つで、別のエージェント/担当者が全体を把握して続行できることを目的とする。
> 詳細設計は [DESIGN.md](DESIGN.md)、確定事実は Claude の memory（末尾にリンク）に分散記録。
> 最終更新: 2026-06-29。

---

## 0. TL;DR / 現在地

- **Viewer-Access-Log（VAL）は本番稼働中**。MTSV上の Windows サービス `ViewerAccessLog`（自動起動）。
  - URL: **http://lineworks-mtsv:5090**（管理者PCから閲覧）
  - 役割: 技術部フォルダへのアクセスを **🟦ビューアー経由 / 🟥サーバー直接 / ⬜未帰属** の3色で可視化し、
    NTFSロックダウンの「ビューアー矯正効果」を効果測定する **読み取り専用ダッシュボード**。
- **動く範囲（Live実データ）**: ログ検索 / ダッシュボード / ユーザー別 / アラート / 検知インシデント / サーバー状態 / **設定9タブ（読み書き）**。
  - 実測: ビューアー利用率 約1.1%（＝98.9%が直接アクセス＝ロックダウン前の現実）。
- **読み書き両対応（2026-06-29）**：AuditLogger の重いBlazor UIを **読み(P1〜P3)・書き(P4a/P4b)とも実質置換**。設定保存・アラート/インシデント状態変更が可能（書込は `config_editor` 限定ロール＋Windows認証＋`config_audit_log`監査・ログ本体は読み取り専用堅持）。
- **残り（小／本丸）**:
  - (A) **本丸＝技術部NTFSロックダウンの本番実施が未着手**（VALで測定できる状態は完成）→ §5
  - (B) P4の **ルール手動評価のみ未実装**（AuditLogger評価エンジン連携要・保留）
  - (C) 小: `lastSync` 表示が古い軽微バグ／catalog.dbロック堅牢化／メモリ→SQLite直問い合わせ最適化

---

## 1. 登場する3プロジェクト（関係図）

| プロジェクト | 場所 | 役割 | VALとの関係 |
|---|---|---|---|
| **Secure-File-Explorer (SFE)** | `Documents/Secure-File-Explorer` | WPF+ASP.NET のファイルビューアー。技術部版(:5080)/全社版(:5081)。**NetworkService＝ネット越し `MTSV$` で共有を代理読み**。実パス秘匿。操作を `catalog.db` の `AccessLogs` に記録 | 🟦の取得元（読み取り） |
| **Internal-Server-Audit-Logger (AuditLogger)** | `Documents/Internal-Server-Audit-Logger` | Win監査イベント(4663等)→PostgreSQL `audit_logger`。Blazor管理UI(:8080・**重い/固まる**)。本番稼働 | 🟥/⬜・アラート/インシデント/collectorの取得元（読み取り） |
| **Viewer-Access-Log (VAL／本書)** | `Documents/Viewer-Access-Log` | 新規。上記2つを**読み取り専用で投影**する効果測定ダッシュボード | 本体 |

**設計思想**: SFE=予防、AuditLogger=検知、VAL=測定UI。**VALは既存2システムを一切変更しない（保険）**。
VALは将来 AuditLogger の重いBlazor UIを段階的に置き換える「日常UIの恒久仕様（案A）」。

---

## 2. VAL 本番稼働の詳細（運用に必要）

- **サービス**: `ViewerAccessLog`（LocalSystem・自動起動）。配置 `D:\Apps\ViewerAccessLog`。`UseWindowsService` 対応済。
- **設定ファイル**: `D:\Apps\ViewerAccessLog\appsettings.json`（`DataMode=Live` / viewer接続文字列 / `SfeSqlitePath=D:\Apps\SecureFileExplorer\catalog.db` / `CachePath=D:\Apps\ViewerAccessLog\cache.db` / `Dept=技術部` / `LookbackDays=14` / `Urls=http://0.0.0.0:5090`）。**viewerパスワードを含むため ACL=SYSTEM/Administrators 限定**。git非コミット。
- **データフロー**: `audit_logs`(viewerロール=SELECT専用) + SFE `catalog.db`(ReadOnly) →（SyncWorker・5分間隔）→ 自前 `cache.db`(SQLite) ← UI/APIはここだけ参照。
- **ポート**: 5090（ファイアウォール Domain/Private 開放済）。
- **操作**:
  - 状態: `Get-Service ViewerAccessLog`
  - 再起動（設定変更後）: `Restart-Service ViewerAccessLog`
  - 設定変更: `appsettings.json` を編集 → Restart
- **初回恒久デプロイ**: ① ローカルで `dotnet publish -c Release -r win-x64 --self-contained true` → ② publish を MTSV共有 `_p2check\val_app` へ robocopy → ③ `scripts/Deploy-ViewerAccessLog.ps1` を `!`（MTSV管理者+viewerパスワード）。配置/appsettings生成/サービス登録/FW/起動確認まで実施。
- **コード更新（恒久後の通常運用）**: publishを共有へステージ → `scripts/Update-ViewerAccessLog.ps1`（**管理者のみ**・バイナリのみ差替・**appsettings.json/cache.db は保持** ＝viewerパスワード/設定/同期キャッシュを失わない）。
- **書込(P4)有効化済**: `appsettings.json` に `Live.ConfigPg`（config_editor接続）。書込APIは Windows認証必須・操作者を `config_audit_log` に記録。書込ロール＋監査テーブルは `scripts/create-config-editor.sql`＋`Create-ConfigEditor-Local.ps1`、アラート/インシデント状態変更の追加権限は `grant-status-update.sql`＋`Grant-StatusUpdate-Local.ps1`。
  - 設定テーブルの実カラムは `scripts/Diag-SettingsSchema.ps1` で確認可。

---

## 3. 確定済みの技術事実（前提にしてよい）

- **viewerロール**: PostgreSQL `audit_logger` に作成済（**SELECT専用**・rolsuper/rolcreaterole=f）。実クエリで5万件超を読めることを確認済。
- **接続・ツール**: `psql=D:\PostgreSQL\16\bin\psql.exe`。AuditLogger接続文字列は `D:\AuditLogger\app\appsettings.json`（collectorロール）。VALは別途 viewer で接続。
- **audit_logs**: `is_content_read`(bool) 実在。action enum 23値。約535万行(9日)・リアルタイム収集中。技術部の直接アクセスは `folder_path`/`file_path` に技術部を含む。
- **性能の肝（重要）**: `file_path ILIKE '%技術部%'` の **trigram索引はCJK3文字では効かず Seq Scan（14日5.79万件で37秒）**。**`event_time` の時間窓は Index Scan で高速**。→ 初回同期は **event_time 2時間窓を now から過去へ刻むループ**（`SyncWorker`）。増分は id ベース。`CommandTimeout=120`。
- **catalog.db AccessLogs**: 列 `Id/TimestampUtc(UTC)/UserName('LINEWORKS-NET\\user')/MachineName/IpAddress/Action(int)/FileId/FolderId/Target/TargetPath(論理パンくず)/Success/FailureReason`。**AccessAction: 0=ListFolder,1=OpenFile,2=Search,3=Error**。技術部版catalog.dbは電気設計も記録。
- **サーバー**: lineworks-sv=192.168.1.240(ファイルサーバ), lineworks-mtsv=192.168.1.242(DB/UI/アプリ・**RAM逼迫**)。MTSVへは**管理者資格情報でPS Remoting可**。⚠️ **Windows PowerShell 5.1 は BOM無し非ASCII .ps1 を誤読する → scripts は ASCII のみで書く**（日本語は実行時に[char]コードで生成）。

---

## 4. 残作業（徹底調査・2026-06-29）

### 4-1. P3 仕上げ（Liveの実ギャップ／実装規模:中）

| 項目 | 状態 | 内容 |
|---|---|---|
| **Live設定読取** | ✅ **完了(2026-06-29)** | `AuditPgReader.ReadSettingsAsync()`（7テーブルの実カラムを確認の上SELECT）＋`CacheLogSource.Settings()`が実DB読取。設定タブがLiveで実config表示（**読み取り専用**・保存はP4）。実データ検証済(/api/settingsが folders10/users46/rules6/grants108/app_settings12 を返す) |
| **Live GAP検出** | ✅ **完了(2026-06-29)** | `AuditPgReader.ReadGapsAsync()`＝collector_stateの停滞(last_event_time>15分前 or status GAP/RESET)を GapWindow として返す＋`CacheLogSource.Gaps()`が実装。健全時は空(=正常)。※過去GAP区間の完全復元はcollector_stateだけでは不可(現在状態のみ保持) |
| **catalog.db ロック堅牢化** | ⏳ 残 | `SfeSqliteSource`(`Mode=ReadOnly`)失敗時リトライ無し・ログのみ。SFE稼働中はロック競合の可能性 → 指数バックオフのリトライ＋スナップショット読取フォールバック |

### 4-2. P4 書込系（AuditLogger UI 完全置換）

**P4a ✅ 完了(2026-06-29)— 書込基盤＋安全な2面**
- **`config_editor` 限定書込ロール**作成済（**設定7テーブル＋config_audit_logのみ**書込・`audit_logs`等ログ本体は権限なし＝grant検証済）。`scripts/create-config-editor.sql` + `Create-ConfigEditor-Local.ps1`。
- **`config_audit_log` 監査テーブル**（changed_at/user_name/table_name/action/record_key/old_values/new_values）。
- **`ConfigWriter`**（config_editor接続・設定変更＋監査INSERTを1トランザクション）。
- **Windows認証(Negotiate)**で操作者特定。読み取り系は匿名可・**書込のみ `.RequireAuthorization()`**。
- **書込API**: `PUT /api/appsettings/{key}` / `POST,PUT,DELETE /api/exclusions`（ConfigWriter未設定時503）。
- **クライアント**: 検知除外タブ＋app_settings系タブ(通知/持ち出し/部署外)が**実API保存**。
- **接続**: `appsettings.json` の `Live.ConfigPg`（config_editor）。
- **実書込検証済**: ネットワーク越しWindows認証で exclusions INSERT(201)→DELETE(204)成功・操作者記録。

**P4b ✅ 完了(2026-06-29)— 残りCRUD＋状態変更**
- **設定9タブ全て実API保存**：folders/users/rules/common_folders/user_folder_grants の CRUD（ConfigWriterに各メソッド・監査1トランザクション）。PROTO_MSGモックは撤去（残るは openModal の同期フォールバックのみ）。
- **アラート/インシデント状態変更**：`PATCH /api/alerts/{id}/status`・`/api/incidents/{id}/status`（config_editor が **status系列の列だけ** UPDATE＝`grant-status-update.sql`/`Grant-StatusUpdate-Local.ps1` で付与・検知データ本体は不可）＋UIに確認/クローズボタン。
- **alert_rules UPDATE は捕捉8列のみ更新・他列(action/notify等)は保持**。
- 書込API：app_settings PUT / exclusions CRUD（P4a）＋ folders/users/rules/commonfolders/usergrants CRUD ＋ alerts/incidents status PATCH（P4b）＝**全て `.RequireAuthorization()`・config_editor経由・未設定時503**。
- **実検証済**：folders POST(201)→DELETE(204)、alerts/incidents status PATCH(200)、ネット越しWindows認証で成功。
- ⚠️ 監視フォルダ/ルールの書込は**本番AuditLoggerの監視/検知挙動を約5分で変える**。運用時は慎重に。

**P4 残（小／任意）**: **ルール手動評価**（`POST /api/rules/{id}/evaluate`）のみ未実装＝AuditLogger側の評価エンジン連携が必要（VAL単独では叩けない）。要設計のため保留。

> 大原則: データ(audit_logs/raw)は**読み取り専用を堅持**／**設定テーブルのみ** config_editor で書込。grant検証済（config_editor は audit_logs に権限なし）。

### 4-3. 運用・将来最適化

- **viewerパスワード**: 現状 `appsettings.json` に平文（ACL制限）。将来 Credential Manager 等へ。
- **LookbackDays**: `LiveOptions` 既定30 / 本番appsettingsは14。初回バックフィルは数分（2h窓ループ）。
- **インメモリ保持**: `CacheLogSource` は cache.db 全行をメモリ保持（TTL 30s）。データ増で圧迫 → **SQLite直問い合わせ化**が将来必要。
- **同期停止バグ ✅ 修正済(2026-06-29)**: 増分同期が `id>lastId＋技術部正規表現` の Seq スキャンで毎回タイムアウトし、**初回バックフィル以降キャッシュが固まっていた**（再起動時も lastId≠0 で同じ失敗パス）。→ `SyncWorker.SyncStreamAsync` を**初回も増分も event_time 窓(2h・索引)走査に統一**、floor=前回 `last_time`-10分。`last_time` に実 event_time を記録（`CacheDb.GetLastTime`/`SetSyncState`）。これで `health` の lastSync/lag も正確化。診断は `scripts/Diag-SyncLag.ps1`。

---

## 5. 本丸：技術部NTFSロックダウン（VALで測定可能になった）

VALの目的は、これを**実データで効果測定しながら安全に実施**すること。

- **現状**: 技術部フォルダに `Authenticated Users`/`Users` の Read が残存＝**全社員が直接読める**（利用率~1.1%）。`MTSV$` は付与済（ビューアーの生命線）。
- **未完の前提作業**: `機械設計` 直下の **残り10サブフォルダに MTSV$ 付与が未完**（`/T` フリーズの続き）。ロックダウン前に完了必須（でないとビューアーがそのフォルダで空になる）。
- **手順（設計済）**: ①MTSV$付与完了 → ②テスト1アカウントで Deny 予行演習 → ③技術部ビューアーを全社員へ配布（ランチャー共有へ Auth Users 読取付与・代替経路を先に）→ ④広域Allow（Authenticated Users/Users）削除 → **VALダッシュボードで利用率↑/直接↓を監視、問題あれば即ロールバック**。
- **詳細**: SFE `docs/setup/server-deploy/SHARE-LOCKDOWN-DESIGN.md`、memory [[share-lockdown-mtsv-pregrant]]。
- ⚠️ **実共有のNTFSは慎重に**。`[[do-not-touch-real-share]]` の原則（無断変更しない・手動/承認前提）。`/Q` 静かモードでフルスキャン凍結回避。

---

## 6. リポジトリ・場所・スクリプト

- **VAL repo**: GitHub `skyblueearthjapan/Viewer-Access-Log`（main）。`src/ViewerAccessLog/`（Minimal API + 静的UI + Live層）、`scripts/`、`DESIGN.md`、`README.md`、本 `HANDOVER.md`。
- **MTSV上**: `D:\Apps\ViewerAccessLog`(稼働), `D:\Apps\SecureFileExplorer\catalog.db`(🟦元), `D:\AuditLogger`(AuditLogger), `D:\PostgreSQL\16`(DB)。
- **scripts/（すべてASCII・`!`+Get-Credentialでremoting実行）**:
  - 読取/検証系: `Run-P2Check.ps1`（本番DB/SFE DB所在の確認）, `Test-ViewerQuery.ps1`（viewer実クエリ）, `Diag-PgPlan.ps1`（EXPLAIN診断）, `Diag-SettingsSchema.ps1`（設定テーブル実カラム確認）, `Run-LiveValidation.ps1`（一時起動検証）, `Snapshot-SfeDb.ps1`（catalog.db読取スナップショット）
  - ロール作成: `create-viewer-role.sql`+`Create-ViewerRole-Local.ps1`（viewer・読取専用・実行済）, `create-config-editor.sql`+`Create-ConfigEditor-Local.ps1`（**config_editor・限定書込＋config_audit_log＋appsettingsにConfigPg追加**・実行済）, `grant-status-update.sql`+`Grant-StatusUpdate-Local.ps1`（**状態変更の列UPDATE権限**・実行済）
  - デプロイ/更新: `Deploy-ViewerAccessLog.ps1`（**初回恒久デプロイ**：サービス登録・FW・appsettings生成）, `Update-ViewerAccessLog.ps1`（**通常のコード更新**：バイナリのみ・appsettings/cache保持）
- **Claude memory（背景・確定事実）**: `[[viewer-access-log-project]]`, `[[auditlogger-sibling-project]]`, `[[share-lockdown-mtsv-pregrant]]`, `[[client-launcher-troubleshooting]]`, `[[do-not-touch-real-share]]`, `[[mtserver-production]]`, `[[all-in-version-deployed]]`, `[[project-overview]]`。

---

## 7. 次の一手（推奨順）

P1〜P3・P3仕上げ・P4a・P4b は**完了**（読み書き両対応で AuditLogger UI 実質置換済）。残りは：

1. **技術部ロックダウン本番**（本丸）: §5の手順。VALで効果測定しながら。前提=機械設計の残り10サブフォルダ MTSV$ 付与完了。
2. **小タスク**（任意）: `lastSync` 表示バグ修正（SyncWorker で last_time 更新）／catalog.db ロック堅牢化（リトライ＋スナップショットフォールバック）。
3. **P4ルール手動評価**（保留）: AuditLogger 評価エンジン連携の設計が要る。
4. 将来最適化: インメモリ→SQLite直問い合わせ、viewerパスワード保管強化（Credential Manager 等）。

## 8. 直近セッションの経緯（2026-06-29）

P2本番確認 → viewerロール作成 → P3コード(Live層) → **P3 Live実データ検証成功**(direct 13,095/利用率1.1%) → **P3恒久デプロイ**(Windowsサービス5090) → **P3仕上げ**(Live設定読取＋GAP検出) → **P4a**(書込基盤＋検知除外/app_settings) → **P4b**(残りCRUD＋状態変更)。各段階でビルド→publish→共有ステージ→`Update`(or `Deploy`)→ネット越しWindows認証で実検証、を反復。詳細コミットは GitHub `Viewer-Access-Log` main の履歴に。

**UI改善(2026-06-29)**:
- **日付バグ修正**: ログ検索/各画面の期間が**サンプル固定日2026-06-27**になっていた → Live時は起動(boot)で `dataMode=Live` を見て**実日付(JST)**に切替。クイックボタン/ラベルもアンカー基準で動的化。`nextDay`をTZ非依存(UTC)に修正。Sampleは従来通り固定日維持。`app.js` 冒頭の `ANCHOR`/`jstToday()`/`addDays`/`monthStart`/`monthEnd` と末尾 `boot()`。
- **検知インシデント日本語化**: 種別(CROSS_DEPT_ACCESS→部署外アクセス等)/状態(new→新規)/重要度(Medium→中)を日本語ラベル、**指標(生JSON)を「対象ファイルN件 / フォルダN / 直近N分 / しきい値N」等の日本語サマリー**に。詳細パネルはサマリー＋ルール/クローズ理由＋生JSON(折りたたみ)。`app.js` の `incTypeLabel`/`incStatusLabel`/`sevLabel`/`metricSummary`/`metricDetailHtml`。**※アラート画面の重要度は未対応(同じ `sevLabel` で容易に拡張可)**。
- **ビューアー経由のフルパス表示**: 🟦ビューアー行は SFE catalog.db の `Target`(ファイル名)＋`TargetPath`(フォルダ経路パンくず)に分かれており、従来ファイル列はファイル名のみだった。→ `app.js` の `fullPath(r)` で viewer は `folder\file` を結合してフルパス表示(直接アクセスと同じ見え方)。ログ検索の行/詳細パネル/ユーザー別タイムラインに適用。**経路先頭の SFE 表示名「技術部データ」は「技術部」に正規化**(直接アクセスと表記統一)。表示のみ(データ/キャッシュ不変)。**CSVエクスポートも対応済**: `Program.cs` の `FullPath(AccessRow)`(同じ結合＋正規化)を `/api/logs.csv` のファイル列に適用。
- **ユーザー表示名の統一**: 全画面のユーザー表示を**設定 `users.display_name`** に統一(`app.js` の `USER_MAP`/`userCell`/`userDisp`/`loadUserMap`・boot時に `/api/settings` から読込)。ログ検索/ダッシュボード(Topユーザー・直近インシデント)/ユーザー別/アラート/インシデント(一覧+詳細)に適用。**ドリル/検索/遷移・設定の編集フォームはアカウント名を維持**。未登録はアカウント名のまま＋表示名にtitleツールチップでアカウント名。現状46人中43人に表示名あり(なしは Administrator/svc-dove/svc-zaiko)。**表示名の編集は設定→ユーザータブ(P4b書込)で可。反映はブラウザ再読込**(USER_MAPはboot読込)。
