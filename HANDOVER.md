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
- **動く範囲（Live実データ）**: ログ検索 / ダッシュボード / ユーザー別 / アラート / 検知インシデント / サーバー状態。
  - 実測: ビューアー利用率 約1.1%（＝98.9%が直接アクセス＝ロックダウン前の現実）。
- **未完（重要）**:
  - (A) **Live時に「設定タブ」と「監査GAP」が空**（P3の積み残し・中規模）
  - (B) **P4＝設定の実保存・状態変更などの書込系が 0%**（AuditLogger UI 完全置換に必須・大規模）
  - (C) **本丸＝技術部NTFSロックダウンの本番実施が未着手**（VALで測定できる状態は整った）

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
- **再デプロイ手順**: ① ローカルで `dotnet publish -c Release -r win-x64 --self-contained true` → ② publish を MTSV共有へ robocopy → ③ `scripts/Deploy-ViewerAccessLog.ps1` を `!` で実行（MTSV管理者+viewerパスワードをGet-Credential）。スクリプトが配置/appsettings生成/サービス再登録/FW/起動確認まで行う。

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

### 4-2. P4 書込系（**0%実装**／規模:大〜超大）— AuditLogger UI 完全置換に必須

現状すべて**クライアント側の楽観更新＋トーストのみ**（`app.js` の `PROTO_MSG="プロトタイプ：本番ではP4の限定書込ロールで保存されます（現在は未永続）"`）。サーバー書込エンドポイントは **0件**。

必要なもの:
1. **限定書込ロール** `config_editor`（PostgreSQL）: 設定テーブルのみ INSERT/UPDATE/DELETE。**audit_logs等のログ本体は触らせない**。
2. **操作者監査** `config_audit_log` テーブル（誰がいつ何を変更）。
3. **書込APIエンドポイント（14+）**: folders/users/rules/exclusions の POST/PUT/DELETE、app_settings の PUT、`alerts/{id}/status`・`incidents/{id}/status` の PATCH（状態変更）、`rules/{id}/evaluate` の POST（手動評価）。
4. **クライアント改修**: `app.js` の7タブ（`bindFolders/bindSettUsers/bindRules/bindExclusions/renderAppSettings`）の楽観更新を実API呼び出しへ。アラート/インシデントの**状態変更ボタン**追加。`PROTO_MSG` 削除。
5. **モデル**は定義済（`Models.cs`）、`SampleData` にスキーマ実装済 → これらを実DB read/write に接続する形。

> P4は「データ(audit_logs)は読み取り専用を堅持／設定テーブルのみ書込」が大原則。書込面が増えるほど保険が薄れるので、最後に・慎重に。

### 4-3. 運用・将来最適化

- **viewerパスワード**: 現状 `appsettings.json` に平文（ACL制限）。将来 Credential Manager 等へ。
- **LookbackDays**: `LiveOptions` 既定30 / 本番appsettingsは14。初回バックフィルは数分（2h窓ループ）。
- **インメモリ保持**: `CacheLogSource` は cache.db 全行をメモリ保持（TTL 30s）。データ増で圧迫 → **SQLite直問い合わせ化**が将来必要。

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
  - `Run-P2Check.ps1` … 本番DB/スキーマ/SFE DB所在の読み取り確認
  - `create-viewer-role.sql` + `Create-ViewerRole-Local.ps1` … viewerロール作成（postgres権限・実行済）
  - `Test-ViewerQuery.ps1` … viewerで実クエリ確認
  - `Diag-PgPlan.ps1` … クエリ計画（EXPLAIN）診断
  - `Run-LiveValidation.ps1` … MTSVで一時起動して実データ検証
  - `Deploy-ViewerAccessLog.ps1` … **恒久デプロイ（サービス登録・FW・appsettings生成）**
  - `Snapshot-SfeDb.ps1` … catalog.db 読み取りスナップショット
- **Claude memory（背景・確定事実）**: `[[viewer-access-log-project]]`, `[[auditlogger-sibling-project]]`, `[[share-lockdown-mtsv-pregrant]]`, `[[client-launcher-troubleshooting]]`, `[[do-not-touch-real-share]]`, `[[mtserver-production]]`, `[[all-in-version-deployed]]`, `[[project-overview]]`。

---

## 7. 次の一手（推奨順）

1. **P3仕上げ**（中）: Live設定読取（設定タブを実configで埋める）＋ Live GAP検出。これでVALの全画面がLiveで意味を持つ。
2. **技術部ロックダウン本番**（本丸）: §5の手順。VALで効果測定しながら。
3. **P4書込系**（大）: AuditLogger UI を完全に置き換える段階。limited-writeロール＋API＋UI。
4. 将来: catalog.dbロック堅牢化、インメモリ→SQLite直問い合わせ、viewerパスワード保管強化。
