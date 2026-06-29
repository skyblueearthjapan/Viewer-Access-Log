namespace ViewerAccessLog;

using Npgsql;

/// <summary>
/// AuditLogger PostgreSQL を viewer ロール（読み取り専用）で参照する。
/// 書込エンドポイントは一切存在しない。
/// NpgsqlDataSource を保持し、接続プールを共有する。
/// </summary>
public sealed class AuditPgReader : IDisposable
{
    private readonly NpgsqlDataSource _ds;
    private readonly string           _schema;
    private readonly string           _dept;

    public AuditPgReader(string connStr, string schema, string dept)
    {
        _ds     = NpgsqlDataSource.Create(connStr);
        _schema = schema;
        _dept   = dept;
    }

    // ---- アクセス行（Direct / Unknown）増分取得 ----------------------------

    /// <summary>
    /// 🟥 Direct: 実ユーザーによる直接ファイル読取を増分取得する。
    /// id &gt; lastId かつ、サービスアカウント・MTSV$ を除外。
    /// since が指定された場合はさらに event_time で下限を設ける（初回 LookbackDays 絞り込み用）。
    /// </summary>
    public async Task<IReadOnlyList<(long SrcId, AccessRow Row)>> ReadDirectRowsAsync(
        long lastId, DateTimeOffset? since = null, int batch = 500, CancellationToken ct = default)
    {
        var sql = $"""
            SELECT id, event_time, server_name, user_name,
                   action::text, file_path, folder_path, file_name,
                   process_name, host(source_ip) AS source_ip
            FROM {_schema}.audit_logs
            WHERE id > @lastId
              AND file_path ILIKE @deptLike          -- trigram索引(ix_audit_filepath_trgm)で高速プレフィルタ
              AND folder_path ~* @deptPattern         -- 正規表現で精密化(小さな候補集合に対して)
              AND user_name IS NOT NULL
              AND user_name !~* 'MTSV\$'
              AND user_name !~* '(NETWORK SERVICE|LOCAL SYSTEM|^SYSTEM$|svc[-_])'
              AND is_content_read = TRUE
              AND result::text = 'Success'
              {(since.HasValue ? "AND event_time >= @since" : "")}
            ORDER BY id
            LIMIT @batch
            """;

        return await FetchAuditRowsAsync(sql, SourceKind.Direct, lastId, since, batch, ct);
    }

    /// <summary>
    /// ⬜ Unknown: サービス/NULL ユーザーによるアクセス（MTSV$ は除外・ビューアー二重計上防止）。
    /// </summary>
    public async Task<IReadOnlyList<(long SrcId, AccessRow Row)>> ReadUnknownRowsAsync(
        long lastId, DateTimeOffset? since = null, int batch = 500, CancellationToken ct = default)
    {
        var sql = $"""
            SELECT id, event_time, server_name, user_name,
                   action::text, file_path, folder_path, file_name,
                   process_name, host(source_ip) AS source_ip
            FROM {_schema}.audit_logs
            WHERE id > @lastId
              AND file_path ILIKE @deptLike
              AND folder_path ~* @deptPattern
              AND (user_name IS NULL OR user_name ~* 'svc[-_]')
              AND user_name !~* 'MTSV\$'
              AND is_content_read = TRUE
              AND result::text = 'Success'
              {(since.HasValue ? "AND event_time >= @since" : "")}
            ORDER BY id
            LIMIT @batch
            """;

        return await FetchAuditRowsAsync(sql, SourceKind.Unknown, lastId, since, batch, ct);
    }

    private async Task<IReadOnlyList<(long SrcId, AccessRow Row)>> FetchAuditRowsAsync(
        string sql, SourceKind source, long lastId, DateTimeOffset? since, int batch, CancellationToken ct)
    {
        var results = new List<(long, AccessRow)>();
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd  = new NpgsqlCommand(sql, conn);
        cmd.CommandTimeout = 180;   // 初回はフルスキャンになり得るため余裕を持たせる(増分は軽い)
        cmd.Parameters.AddWithValue("@lastId",      lastId);
        cmd.Parameters.AddWithValue("@deptPattern", DeptPattern(_dept));
        cmd.Parameters.AddWithValue("@deptLike",    "%" + _dept + "%");
        cmd.Parameters.AddWithValue("@batch",       batch);
        if (since.HasValue)
            cmd.Parameters.AddWithValue("@since", since.Value);

        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var srcId  = r.GetInt64(0);
            var time   = r.GetFieldValue<DateTimeOffset>(1);
            var server = r.IsDBNull(2) ? null : r.GetString(2);
            var rawUser = r.IsDBNull(3) ? "(不明)" : r.GetString(3);
            var action  = r.IsDBNull(4) ? "read"  : r.GetString(4);
            var fpath   = r.IsDBNull(5) ? null : r.GetString(5);
            var dpath   = r.IsDBNull(6) ? null : r.GetString(6);
            var fname   = r.IsDBNull(7) ? null : r.GetString(7);
            var pc      = r.IsDBNull(8) ? null : r.GetString(8);
            var ip      = r.IsDBNull(9) ? null : r.GetString(9);

            var user   = StripDomain(rawUser);
            var dept   = ExtractDept(fpath ?? dpath);
            var logFile = NormPath(fpath) ?? (fname is not null ? NormPath(dpath + "\\" + fname) : null);
            var logDir  = NormPath(dpath);
            var (kind, label) = MapAction(action);

            results.Add((srcId, new AccessRow(
                Id:      srcId,
                Time:    time,
                Source:  source,
                User:    user,
                Dept:    dept,
                Action:  label,
                Kind:    kind,
                File:    logFile,
                Folder:  logDir,
                Pc:      pc,
                Ip:      ip,
                Success: true,
                Note:    null)));
        }
        return results;
    }

    // ---- Alerts / Incidents / Collectors（読み取りのみ）-------------------

    /// <summary>alert_histories を読み取り AlertItem のリストを返す。テーブルが存在しない場合は空。</summary>
    public async Task<IReadOnlyList<AlertItem>> ReadAlertsAsync(CancellationToken ct = default)
    {
        var list = new List<AlertItem>();
        try
        {
            await using var conn = await _ds.OpenConnectionAsync(ct);
            await using var cmd  = new NpgsqlCommand(
                $"SELECT id, triggered_at, severity::text, rule_name, user_name, matched_count, status::text " +
                $"FROM {_schema}.alert_histories ORDER BY triggered_at DESC LIMIT 100", conn);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                list.Add(new AlertItem(
                    r.GetInt64(0),
                    r.GetFieldValue<DateTimeOffset>(1),
                    r.IsDBNull(2) ? "Medium"  : r.GetString(2),
                    r.IsDBNull(3) ? "(不明)"  : r.GetString(3),
                    r.IsDBNull(4) ? ""        : r.GetString(4),
                    r.IsDBNull(5) ? 0L        : Convert.ToInt64(r.GetValue(5)),
                    r.IsDBNull(6) ? "New"     : r.GetString(6)));
        }
        catch { /* テーブルが存在しない場合など — ログは呼び出し元で */ }
        return list;
    }

    /// <summary>detected_incidents を読み取り IncidentItem のリストを返す。</summary>
    public async Task<IReadOnlyList<IncidentItem>> ReadIncidentsAsync(CancellationToken ct = default)
    {
        var list = new List<IncidentItem>();
        try
        {
            await using var conn = await _ds.OpenConnectionAsync(ct);
            await using var cmd  = new NpgsqlCommand(
                $"SELECT id, detected_at, detection_type::text, severity::text, user_name, matched_count, metrics::text, status::text " +
                $"FROM {_schema}.detected_incidents ORDER BY detected_at DESC LIMIT 100", conn);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                list.Add(new IncidentItem(
                    r.GetInt64(0),
                    r.GetFieldValue<DateTimeOffset>(1),
                    r.IsDBNull(2) ? "UNKNOWN"  : r.GetString(2),
                    r.IsDBNull(3) ? "Medium"   : r.GetString(3),
                    r.IsDBNull(4) ? ""         : r.GetString(4),
                    r.IsDBNull(5) ? 0L         : Convert.ToInt64(r.GetValue(5)),
                    r.IsDBNull(6) ? ""         : r.GetString(6),
                    r.IsDBNull(7) ? "Open"     : r.GetString(7)));
        }
        catch { }
        return list;
    }

    /// <summary>collector_state を読み取り CollectorState のリストを返す。</summary>
    public async Task<IReadOnlyList<CollectorState>> ReadCollectorsAsync(CancellationToken ct = default)
    {
        var list = new List<CollectorState>();
        try
        {
            await using var conn = await _ds.OpenConnectionAsync(ct);
            await using var cmd  = new NpgsqlCommand(
                $"SELECT server_name, channel, last_event_time, last_status " +
                $"FROM {_schema}.collector_state", conn);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var lastEvt = r.IsDBNull(2) ? DateTimeOffset.MinValue : r.GetFieldValue<DateTimeOffset>(2);
                var lag = lastEvt == DateTimeOffset.MinValue ? 0
                          : (int)Math.Max(0, (DateTimeOffset.UtcNow - lastEvt).TotalSeconds);
                list.Add(new CollectorState(
                    r.GetString(0),
                    r.IsDBNull(1) ? "unknown"  : r.GetString(1),
                    lastEvt,
                    lag,
                    r.IsDBNull(3) ? "Unknown"  : r.GetString(3)));
            }
        }
        catch { }
        return list;
    }

    /// <summary>collector_state の MAX(last_event_at) を AuditLatestEvent として返す。</summary>
    public async Task<DateTimeOffset?> ReadAuditLatestEventAsync(CancellationToken ct = default)
    {
        try
        {
            await using var conn = await _ds.OpenConnectionAsync(ct);
            await using var cmd  = new NpgsqlCommand(
                $"SELECT MAX(last_event_time) FROM {_schema}.collector_state", conn);
            var val = await cmd.ExecuteScalarAsync(ct);
            return val switch
            {
                DateTimeOffset dto => dto,
                DateTime dt        => new DateTimeOffset(dt, TimeSpan.Zero),
                _                  => null,
            };
        }
        catch { return null; }
    }

    // ---- 内部ヘルパー -------------------------------------------------------

    /// <summary>部署名からフォルダパス一致正規表現パターンを生成する（Npgsql パラメータで安全に渡す）。</summary>
    private static string DeptPattern(string dept) => $"[\\/]{dept}([\\/]|$)";

    /// <summary>'LINEWORKS-NET\taro' → 'taro'（小文字化なし、グルーピングはサービス側で）</summary>
    private static string StripDomain(string raw)
    {
        var idx = raw.IndexOf('\\');
        return idx >= 0 ? raw[(idx + 1)..].Trim() : raw.Trim();
    }

    /// <summary>audit_logs の action::text → (ActionKind, 表示文字列)</summary>
    private static (ActionKind Kind, string Label) MapAction(string a) =>
        a.ToUpperInvariant() switch
        {
            "WRITE" or "SETATTR" or "SETATTRIBUTE" or "RENAME" => (ActionKind.Write,  "編集"),
            "DELETE" or "REMOVEDIR"                             => (ActionKind.Delete, "削除"),
            _                                                   => (ActionKind.Read,   "読取"),
        };

    /// <summary>
    /// 'D:\Data\技術部\電気設計\resource.xlsx' または '\\sv\Data\技術部\…'
    /// → 'Data\' 以降の論理パス '技術部\電気設計\resource.xlsx'
    /// </summary>
    private static string? NormPath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        var norm = path.Replace('/', '\\');
        var idx  = norm.IndexOf("Data\\", StringComparison.OrdinalIgnoreCase);
        return idx >= 0 ? norm[(idx + 5)..] : norm;
    }

    /// <summary>'D:\Data\技術部\…' からパス直下の部署名セグメントを抽出する。</summary>
    private static string ExtractDept(string? path)
    {
        if (string.IsNullOrEmpty(path)) return "(不明)";
        var norm = path.Replace('/', '\\');
        var idx  = norm.IndexOf("Data\\", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return "(不明)";
        var rest  = norm[(idx + 5)..];
        var slash = rest.IndexOf('\\');
        return slash >= 0 ? rest[..slash] : rest;
    }

    public void Dispose() => _ds.Dispose();
}
