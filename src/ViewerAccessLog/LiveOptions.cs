namespace ViewerAccessLog;

/// <summary>
/// DataMode=Live 時の接続設定（appsettings.json "Live" セクションにバインドする）。
/// 実パスワードは appsettings.Production.json に置きコミットしない。
/// </summary>
public sealed class LiveOptions
{
    public AuditPgOptions  AuditPg             { get; set; } = new();
    public ConfigPgOptions ConfigPg             { get; set; } = new();
    public string          SfeSqlitePath        { get; set; } = "";
    public string          CachePath            { get; set; } = "cache.db";
    public string          Dept                 { get; set; } = "技術部";
    public int             SyncIntervalSeconds  { get; set; } = 300;
    public int             LookbackDays         { get; set; } = 30;

    /// <summary>viewer ロール読み取り接続が設定済か（プレースホルダー残存 = 未設定）。</summary>
    public bool IsPgConfigured =>
        !string.IsNullOrWhiteSpace(AuditPg.ConnectionString) &&
        !AuditPg.ConnectionString.Contains("__SET_IN_PRODUCTION__") &&
        !AuditPg.ConnectionString.Contains("__SET_IN_LOCAL__");

    /// <summary>config_editor ロール書込接続が設定済か（未設定時は書込 API が 503）。</summary>
    public bool IsConfigWriteEnabled =>
        !string.IsNullOrWhiteSpace(ConfigPg.ConnectionString) &&
        !ConfigPg.ConnectionString.Contains("__SET_IN_PRODUCTION__");
}

/// <summary>AuditLogger PostgreSQL 読み取り専用（viewer ロール）接続設定。</summary>
public sealed class AuditPgOptions
{
    public string ConnectionString { get; set; } = "";
    public string Schema           { get; set; } = "audit";
}

/// <summary>
/// AuditLogger PostgreSQL 設定書込（config_editor ロール）接続設定。
/// viewer とは完全に別接続・別ロール。ログ本体(audit_logs 等)へは書き込まない。
/// </summary>
public sealed class ConfigPgOptions
{
    public string ConnectionString { get; set; } = "";
    public string Schema           { get; set; } = "audit";
}
