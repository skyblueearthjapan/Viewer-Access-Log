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
    int PageSize = 50);

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

public record HealthInfo(
    string DataMode,
    DateTimeOffset? LastSync,
    int LagSeconds,
    DateTimeOffset? AuditLatestEvent,
    IReadOnlyList<GapWindow> Gaps);

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
}
