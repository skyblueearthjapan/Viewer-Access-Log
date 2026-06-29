namespace ViewerAccessLog;

using Microsoft.Data.Sqlite;

/// <summary>
/// DataMode=Live のときに ILogSource を実装するクラス。
///
/// All() → cache.db の access_rows を全件返す（TTL付きインメモリキャッシュ）
/// Gaps() → 当面空（仕様通り）
/// Alerts/Incidents/Collectors() → AuditLogger PostgreSQL を viewer ロールで直接 SELECT
/// LastSync() → cache.db の sync_state から最終同期時刻を返す
/// AuditLatestEvent() → PostgreSQL collector_state の MAX(last_event_at)
/// Settings() → 空 SettingsData（Live モードでは P4 設定は未実装）
///
/// 書込は一切行わない（AuditPgReader / CacheDb 双方とも読み取り専用）。
/// </summary>
public sealed class CacheLogSource : ILogSource, IDisposable
{
    private readonly LiveOptions              _opts;
    private readonly ILogger<CacheLogSource> _logger;
    private readonly AuditPgReader?          _pg;

    private List<AccessRow>? _cachedRows;
    private DateTimeOffset   _cacheAt = DateTimeOffset.MinValue;
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);

    private static readonly SettingsData EmptySettings = new([], [], [], [], [], [], []);

    public CacheLogSource(LiveOptions opts, ILogger<CacheLogSource> logger)
    {
        _opts   = opts;
        _logger = logger;

        // cache.db スキーマを確保（ファイルがなければ空で作成する）
        try
        {
            using var conn = CacheDb.Open(opts.CachePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialise cache.db at '{Path}'", opts.CachePath);
        }

        // PostgreSQL reader（接続文字列が設定されている場合のみ）
        if (opts.IsPgConfigured)
        {
            try
            {
                _pg = new AuditPgReader(opts.AuditPg.ConnectionString, opts.AuditPg.Schema, opts.Dept);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create AuditPgReader");
            }
        }
    }

    public string DataMode => "Live";

    // ---- ILogSource 実装 ---------------------------------------------------

    public IReadOnlyList<AccessRow> All()
    {
        var now = DateTimeOffset.UtcNow;
        if (_cachedRows is not null && (now - _cacheAt) < Ttl)
            return _cachedRows;

        try
        {
            using var conn = new SqliteConnection($"Data Source={_opts.CachePath};Cache=Shared");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT id, source, time, user, dept, action, kind,
                       file, folder, pc, ip, success, note
                FROM access_rows
                ORDER BY time ASC
                """;
            var rows = new List<AccessRow>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                rows.Add(new AccessRow(
                    Id:      r.GetInt64(0),
                    Source:  ParseSource(r.GetString(1)),
                    Time:    DateTimeOffset.Parse(r.GetString(2),
                                 System.Globalization.CultureInfo.InvariantCulture),
                    User:    r.GetString(3),
                    Dept:    r.GetString(4),
                    Action:  r.GetString(5),
                    Kind:    ParseKind(r.GetString(6)),
                    File:    r.IsDBNull(7)  ? null : r.GetString(7),
                    Folder:  r.IsDBNull(8)  ? null : r.GetString(8),
                    Pc:      r.IsDBNull(9)  ? null : r.GetString(9),
                    Ip:      r.IsDBNull(10) ? null : r.GetString(10),
                    Success: r.GetInt32(11) != 0,
                    Note:    r.IsDBNull(12) ? null : r.GetString(12)));
            }
            _cachedRows = rows;
            _cacheAt    = now;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CacheLogSource.All() failed");
            return _cachedRows ?? [];
        }
        return _cachedRows;
    }

    /// <summary>監査GAP（収集停滞）を collector_state から検出して返す。</summary>
    public IReadOnlyList<GapWindow> Gaps()
    {
        if (_pg is null) return [];
        try { return _pg.ReadGapsAsync().GetAwaiter().GetResult(); }
        catch (Exception ex) { _logger.LogWarning(ex, "Gaps() failed"); return []; }
    }

    public DateTimeOffset? LastSync()
    {
        try
        {
            using var conn = new SqliteConnection($"Data Source={_opts.CachePath};Cache=Shared");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT MAX(last_time) FROM sync_state";
            var val = cmd.ExecuteScalar();
            if (val is string s && !string.IsNullOrEmpty(s) &&
                DateTimeOffset.TryParse(s, out var dto))
                return dto;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LastSync() failed");
        }
        return null;
    }

    public DateTimeOffset? AuditLatestEvent()
    {
        if (_pg is null) return null;
        try
        {
            return _pg.ReadAuditLatestEventAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AuditLatestEvent() failed");
            return null;
        }
    }

    public IReadOnlyList<AlertItem> Alerts()
    {
        if (_pg is null) return [];
        try
        {
            return _pg.ReadAlertsAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Alerts() failed");
            return [];
        }
    }

    public IReadOnlyList<IncidentItem> Incidents()
    {
        if (_pg is null) return [];
        try
        {
            return _pg.ReadIncidentsAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Incidents() failed");
            return [];
        }
    }

    public IReadOnlyList<CollectorState> Collectors()
    {
        if (_pg is null) return [];
        try
        {
            return _pg.ReadCollectorsAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Collectors() failed");
            return [];
        }
    }

    /// <summary>設定タブ用に AuditLogger の設定テーブル群を読み取って返す（読み取りのみ）。</summary>
    public SettingsData Settings()
    {
        if (_pg is null) return EmptySettings;
        try { return _pg.ReadSettingsAsync().GetAwaiter().GetResult(); }
        catch (Exception ex) { _logger.LogError(ex, "Settings() failed"); return EmptySettings; }
    }

    public void Dispose() => _pg?.Dispose();

    // ---- 内部パーサー -------------------------------------------------------

    private static SourceKind ParseSource(string s) => s switch
    {
        "viewer"  => SourceKind.Viewer,
        "direct"  => SourceKind.Direct,
        "unknown" => SourceKind.Unknown,
        _         => SourceKind.Unknown,
    };

    private static ActionKind ParseKind(string s) => s switch
    {
        "read"   => ActionKind.Read,
        "write"  => ActionKind.Write,
        "delete" => ActionKind.Delete,
        "copy"   => ActionKind.Copy,
        "login"  => ActionKind.Login,
        "search" => ActionKind.Search,
        _        => ActionKind.Read,
    };
}
