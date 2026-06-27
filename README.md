# Viewer-Access-Log

技術部フォルダへのアクセスを **🟦ビューアー経由 / 🟥サーバー直接 / ⬜未帰属** の3色で区別し、
NTFSロックダウンの「ビューアー矯正効果」を測定する、**完全分離・読み取り専用**のダッシュボード。

> 既存2システム（Secure-File-Explorer / AuditLogger）は**一切変更せず読むだけ**。設計の全文は [DESIGN.md](DESIGN.md)。
> 設計は Claude と OpenAI Codex のレビューを統合したもの。

## 実行（自宅・サンプルデータ）

```bash
cd src/ViewerAccessLog
dotnet run
# → http://localhost:5099 をブラウザで開く
```

`DataMode=Sample`（既定）なので**サーバー未接続でも動きます**。サンプルデータは 2026-06-27 固定。

## 構成

```
src/ViewerAccessLog/
├─ Program.cs        Minimal API（/api/logs /api/summary /api/health /api/filters）+ 静的UI
├─ Models.cs         AccessRow / SourceKind(Viewer/Direct/Unknown) / Summary / Health
├─ SampleData.cs     SampleLogSource（サンプル種データ・GAP区間つき）
├─ LogService.cs     検索・集計・GAP除外の利用率（UIはキャッシュ投影だけ見る）
├─ appsettings.json  DataMode と 本番接続テンプレート
└─ wwwroot/          index.html / styles.css / app.js（3色UI）
```

## 3色の意味（重要）

| 色 | 区分 | 根拠 |
|---|---|---|
| 🟦 Viewer | ビューアー経由 | SFE SQLite に存在＝本人特定・確実 |
| 🟥 Direct | サーバー直接 | audit_logs の実ユーザー＋is_content_read＋対象パス（MTSV$/サービス除外） |
| ⬜ Unknown | 未帰属 | MTSV$ / NULL / サービス＝**ビューアーの証明ではない** |

`MTSV$`除外を「ビューアー経由の証明」と誤解しないために、灰を**隠さず可視化**する。

## ロードマップ

- [x] P1: サンプルデータで3色UI（本リポジトリ）
- [ ] P2: 本番確認（デプロイ版ズレ / `is_content_read`実在 / SFE SQLite位置 / `viewer`ロール）
- [ ] P3: 実DB接続（Sync Worker：SFE SQLite + Audit PG → 自前SQLiteキャッシュ）

## 安全性

- PostgreSQL は **SELECT専用 `viewer` ロール**、接続1〜2・`statement_timeout`付き。
- SFE SQLite は **`mode=ro`**。書き込みは一切しない。失敗しても既存は無傷。
