namespace ViewerAccessLog;

/// <summary>
/// DataMode=Live 時の接続設定（appsettings.json "Live" セクションにバインドする）。
/// 実パスワードは appsettings.Production.json に置きコミットしない。
/// </summary>
public sealed class LiveOptions
{
    public AuditPgOptions AuditPg          { get; set; } = new();
    public string         SfeSqlitePath    { get; set; } = "";
    public string         CachePath        { get; set; } = "cache.db";
    public string         Dept             { get; set; } = "技術部";
    public int            SyncIntervalSeconds { get; set; } = 300;
    public int            LookbackDays     { get; set; } = 30;

    /// <summary>PostgreSQL 接続文字列にプレースホルダが残っているかどうか（未設定判定）。</summary>
    public bool IsPgConfigured =>
        !string.IsNullOrWhiteSpace(AuditPg.ConnectionString) &&
        !AuditPg.ConnectionString.Contains("__SET_IN_PRODUCTION__") &&
        !AuditPg.ConnectionString.Contains("__SET_IN_LOCAL__");
}

public sealed class AuditPgOptions
{
    public string ConnectionString { get; set; } = "";
    public string Schema           { get; set; } = "audit";
}
