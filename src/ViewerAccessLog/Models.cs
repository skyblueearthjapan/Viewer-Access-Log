namespace ViewerAccessLog;

/// <summary>
/// アクセスの帰属（3色）。2色ではなく3色にするのが本設計の肝（DESIGN.md §2）。
///   Viewer  (青) = SFE SQLite に存在 = ビューアー経由として確実
///   Direct  (赤) = audit_logs の実ユーザー直接アクセス（is_content_read）
///   Unknown (灰) = MTSV$ / NULL / サービス = 未帰属（ビューアーの証明ではない）
/// </summary>
public enum SourceKind { Viewer, Direct, Unknown }

/// <summary>操作の大分類（バッジ色分け用）。</summary>
public enum ActionKind { Read, Write, Delete, Copy, Login, Search }

/// <summary>画面に出す1行（正規化済みアクセス）。</summary>
public record AccessRow(
    long Id,
    DateTimeOffset Time,
    SourceKind Source,
    string User,
    string Dept,
    string Action,
    ActionKind Kind,
    string? File,
    string? Folder,
    string? Pc,
    string? Ip,
    bool Success,
    string? Note);

/// <summary>検索条件。</summary>
public record LogQuery(
    DateTimeOffset? From,
    DateTimeOffset? To,
    string? User,
    string[]? Sources,
    string[]? Kinds,
    string? Q,
    int Page = 1,
    int PageSize = 50,
    string? Dept = null,
    string? Sort = null,    // time / user / dept / kind（既定 time）
    bool Desc = true);      // true=降順（既定）/ false=昇順

public record LogPage(long Total, int Page, int PageSize, IReadOnlyList<AccessRow> Rows);

/// <summary>効果測定の集計（生ログでなく指標を主役にする / DESIGN.md §6）。</summary>
public record Summary(
    long Total,
    long Viewer,
    long Direct,
    long Unknown,
    int DirectUsers,
    int DirectFiles,
    double AdoptionRate,     // 青 / (青+赤)  ※GAP時間帯は分母から除外
    int GapMinutes);         // 監査が止まっていた合計分（利用率の信頼性に直結）

public record GapWindow(DateTimeOffset Start, DateTimeOffset End, string Reason);

/// <summary>収集エンジン（コレクター）の稼働状態。サーバー状態画面で表示。</summary>
public record CollectorState(
    string Server,
    string Channel,
    DateTimeOffset LastEvent,
    int LagSeconds,
    string Status);

public record HealthInfo(
    string DataMode,
    DateTimeOffset? LastSync,
    int LagSeconds,
    DateTimeOffset? AuditLatestEvent,
    IReadOnlyList<GapWindow> Gaps,
    IReadOnlyList<CollectorState> Collectors);

/// <summary>アラート1件（ルール検知）。状態変更はP4で書込予定・今は閲覧のみ。</summary>
public record AlertItem(
    long Id,
    DateTimeOffset Time,
    string Severity,   // High / Medium / Low
    string Rule,
    string User,
    long Count,
    string Status);

/// <summary>検知インシデント（大量持ち出し / 部署外アクセス 等）。</summary>
public record IncidentItem(
    long Id,
    DateTimeOffset Time,
    string Type,       // BULK_CONTENT_READ / CROSS_DEPT_ACCESS 等
    string Severity,   // High / Medium / Low
    string User,
    long MatchCount,
    string Metric,
    string Status);

/// <summary>ユーザー別一覧の1行（青/赤/灰を横断表示）。</summary>
public record UserRow(
    string User,
    string Dept,
    long Viewer,
    long Direct,
    long Unknown,
    DateTimeOffset LastAccess);

/// <summary>ユーザー詳細（ソース別サマリ＋時系列タイムライン＋時間帯別＋操作種別内訳）。</summary>
public record UserDetail(
    string User,
    string Dept,
    long Viewer,
    long Direct,
    long Unknown,
    IReadOnlyList<AccessRow> Timeline,
    IReadOnlyList<HourPoint> Hourly,             // B2: 時間帯別 3色
    IReadOnlyList<NameCount> ActionBreakdown);    // B2: 操作種別×件数

/// <summary>時間帯別スタック棒の1点（時×3色）。</summary>
public record HourPoint(int Hour, long Viewer, long Direct, long Unknown);

/// <summary>名前×件数（Topユーザー・部署別件数の横棒用）。</summary>
public record NameCount(string Name, long Count);

/// <summary>ダッシュボード一括取得（KPI＋各グラフ＋直近インシデント＋操作種別内訳）。</summary>
public record DashboardData(
    Summary Summary,
    IReadOnlyList<HourPoint> Hourly,
    IReadOnlyList<NameCount> DirectTopUsers,
    IReadOnlyList<NameCount> DeptCounts,
    IReadOnlyList<IncidentItem> RecentIncidents,
    IReadOnlyList<NameCount> ActionBreakdown);    // B1: 操作種別×件数

// ---- P4 設定系モデル（読み取りのみ。書込は P4 の限定書込ロールで実装予定）------

/// <summary>監視フォルダ。monitored_folders テーブル相当。</summary>
public record MonitoredFolder(
    int Id, string Server, string Path, string Importance,
    bool ReadEnabled, bool WriteEnabled, bool DeleteEnabled, bool Enabled);

/// <summary>ユーザー設定。users テーブル相当（NTFS アクセス管理用）。</summary>
public record UserConfig(
    int Id, string Domain, string Name, string Display,
    string Dept, string Role, bool Enabled);

/// <summary>アラートルール。alert_rules テーブル相当。</summary>
public record AlertRule(
    int Id, string Name, string Condition, string Severity,
    string Target, int Threshold, int WindowMinutes, bool OffHours, bool Enabled);

/// <summary>検知除外設定。detection_exclusions テーブル相当。</summary>
public record DetectionExclusion(
    int Id, string User, string? Process, string? Path, string Reason);

/// <summary>部署外アクセス判定から除外する共通フォルダ。</summary>
public record CommonFolder(int Id, string Path, string Description);

/// <summary>ユーザーへの特定フォルダアクセス付与（dept | postbox）。</summary>
public record UserFolderGrant(int Id, string User, string Kind, string Value);

/// <summary>アプリ設定（key-value）。app_settings テーブル相当。</summary>
public record AppSetting(string Key, string Value, string Description);

/// <summary>設定一括取得（/api/settings の返値）。</summary>
public record SettingsData(
    IReadOnlyList<MonitoredFolder> Folders,
    IReadOnlyList<UserConfig> Users,
    IReadOnlyList<AlertRule> Rules,
    IReadOnlyList<DetectionExclusion> Exclusions,
    IReadOnlyList<CommonFolder> CommonFolders,
    IReadOnlyList<UserFolderGrant> UserGrants,
    IReadOnlyList<AppSetting> AppSettings);

/// <summary>
/// ログ取得元の抽象。今は SampleLogSource（サンプル）。
/// 後で SfeSqliteSource(🟦) + AuditPgSource(🟥灰) + 同期キャッシュに差し替える。
/// </summary>
public interface ILogSource
{
    IReadOnlyList<AccessRow> All();
    IReadOnlyList<GapWindow> Gaps();
    DateTimeOffset? LastSync();
    DateTimeOffset? AuditLatestEvent();
    IReadOnlyList<AlertItem> Alerts();
    IReadOnlyList<IncidentItem> Incidents();
    IReadOnlyList<CollectorState> Collectors();
    SettingsData Settings();                      // P4: 設定一括取得（読み取りのみ）
}
