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

    public AuditPgReader(string connStr, string schema)
    {
        _ds     = NpgsqlDataSource.Create(connStr);
        _schema = schema;
    }

    // ---- アクセス行（Direct / Unknown）増分取得 ----------------------------

    /// <summary>
    /// 🟥 Direct: 実ユーザーによる直接ファイル読取を増分取得する（全部署対象）。
    /// サービスアカウント・MTSV$ を除外。
    /// since が指定された場合はさらに event_time で下限を設ける（初回 LookbackDays 絞り込み用）。
    /// </summary>
    public async Task<IReadOnlyList<(long SrcId, AccessRow Row)>> ReadDirectRowsAsync(
        long lastId, DateTimeOffset? since = null, int batch = 500, bool bulk = false,
        DateTimeOffset? until = null, CancellationToken ct = default)
    {
        // bulk=初回: event_time の窓(@since〜@until)で索引(ix_audit_event_time)を使い、その窓内のみ走査。
        // 非bulk=増分: id > lastId の少量を id順で取得。
        // 部署フィルタは廃止（D:\Data 配下＋MTSV領域の全フォルダを対象とする）。
        var sql = bulk
            ? $"""
                SELECT id, event_time, server_name, user_name,
                       action::text, file_path, folder_path, file_name,
                       process_name, host(source_ip) AS source_ip
                FROM {_schema}.audit_logs
                WHERE event_time >= @since AND event_time < @until   -- 時間窓=索引が効く
                  AND user_name IS NOT NULL
                  AND user_name !~* 'MTSV\$'
                  AND user_name !~* '(NETWORK SERVICE|LOCAL SYSTEM|^SYSTEM$|svc[-_])'
                  AND is_content_read = TRUE
                  AND result::text = 'Success'
                  AND COALESCE(file_path,'') NOT ILIKE '%:Zone.Identifier'
                  AND COALESCE(file_name,'') NOT ILIKE 'desktop.ini'
                  AND COALESCE(file_name,'') NOT ILIKE 'thumbs.db'
                  AND COALESCE(file_name,'') NOT ILIKE '%.lnk'
                  AND COALESCE(file_name,'') NOT ILIKE '%.tmp'
                  AND COALESCE(file_name,'') NOT ILIKE '~$%'
                  AND (position(chr(92) || 'Data' || chr(92) in COALESCE(folder_path,'') || '|' || COALESCE(file_path,'')) > 0
                    OR position(chr(92) || 'MTlock関連' || chr(92) in COALESCE(folder_path,'') || '|' || COALESCE(file_path,'')) > 0)
                LIMIT 200000
                """
            : $"""
                SELECT id, event_time, server_name, user_name,
                       action::text, file_path, folder_path, file_name,
                       process_name, host(source_ip) AS source_ip
                FROM {_schema}.audit_logs
                WHERE id > @lastId
                  AND user_name IS NOT NULL
                  AND user_name !~* 'MTSV\$'
                  AND user_name !~* '(NETWORK SERVICE|LOCAL SYSTEM|^SYSTEM$|svc[-_])'
                  AND is_content_read = TRUE
                  AND result::text = 'Success'
                  AND COALESCE(file_path,'') NOT ILIKE '%:Zone.Identifier'
                  AND COALESCE(file_name,'') NOT ILIKE 'desktop.ini'
                  AND COALESCE(file_name,'') NOT ILIKE 'thumbs.db'
                  AND COALESCE(file_name,'') NOT ILIKE '%.lnk'
                  AND COALESCE(file_name,'') NOT ILIKE '%.tmp'
                  AND COALESCE(file_name,'') NOT ILIKE '~$%'
                  AND (position(chr(92) || 'Data' || chr(92) in COALESCE(folder_path,'') || '|' || COALESCE(file_path,'')) > 0
                    OR position(chr(92) || 'MTlock関連' || chr(92) in COALESCE(folder_path,'') || '|' || COALESCE(file_path,'')) > 0)
                ORDER BY id
                LIMIT @batch
                """;

        return await FetchAuditRowsAsync(sql, SourceKind.Direct, lastId, since, batch, ct, until);
    }

    /// <summary>
    /// ⬜ Unknown: user_name が NULL の真に未帰属なアクセスのみ。MTSV$(ビューアー代理・二重計上防止)と
    /// サービスアカウント(svc-*・システム監視)は除外＝どのソースにも出さない。全部署対象。
    /// </summary>
    public async Task<IReadOnlyList<(long SrcId, AccessRow Row)>> ReadUnknownRowsAsync(
        long lastId, DateTimeOffset? since = null, int batch = 500, bool bulk = false,
        DateTimeOffset? until = null, CancellationToken ct = default)
    {
        // 部署フィルタは廃止（全フォルダを対象）。user_name の判定は現状を踏襲。
        var sql = bulk
            ? $"""
                SELECT id, event_time, server_name, user_name,
                       action::text, file_path, folder_path, file_name,
                       process_name, host(source_ip) AS source_ip
                FROM {_schema}.audit_logs
                WHERE event_time >= @since AND event_time < @until
                  AND user_name IS NULL
                  AND is_content_read = TRUE
                  AND result::text = 'Success'
                  AND COALESCE(file_path,'') NOT ILIKE '%:Zone.Identifier'
                  AND COALESCE(file_name,'') NOT ILIKE 'desktop.ini'
                  AND COALESCE(file_name,'') NOT ILIKE 'thumbs.db'
                  AND COALESCE(file_name,'') NOT ILIKE '%.lnk'
                  AND COALESCE(file_name,'') NOT ILIKE '%.tmp'
                  AND COALESCE(file_name,'') NOT ILIKE '~$%'
                  AND (position(chr(92) || 'Data' || chr(92) in COALESCE(folder_path,'') || '|' || COALESCE(file_path,'')) > 0
                    OR position(chr(92) || 'MTlock関連' || chr(92) in COALESCE(folder_path,'') || '|' || COALESCE(file_path,'')) > 0)
                LIMIT 200000
                """
            : $"""
                SELECT id, event_time, server_name, user_name,
                       action::text, file_path, folder_path, file_name,
                       process_name, host(source_ip) AS source_ip
                FROM {_schema}.audit_logs
                WHERE id > @lastId
                  AND user_name IS NULL
                  AND is_content_read = TRUE
                  AND result::text = 'Success'
                  AND COALESCE(file_path,'') NOT ILIKE '%:Zone.Identifier'
                  AND COALESCE(file_name,'') NOT ILIKE 'desktop.ini'
                  AND COALESCE(file_name,'') NOT ILIKE 'thumbs.db'
                  AND COALESCE(file_name,'') NOT ILIKE '%.lnk'
                  AND COALESCE(file_name,'') NOT ILIKE '%.tmp'
                  AND COALESCE(file_name,'') NOT ILIKE '~$%'
                  AND (position(chr(92) || 'Data' || chr(92) in COALESCE(folder_path,'') || '|' || COALESCE(file_path,'')) > 0
                    OR position(chr(92) || 'MTlock関連' || chr(92) in COALESCE(folder_path,'') || '|' || COALESCE(file_path,'')) > 0)
                ORDER BY id
                LIMIT @batch
                """;

        return await FetchAuditRowsAsync(sql, SourceKind.Unknown, lastId, since, batch, ct, until);
    }

    private async Task<IReadOnlyList<(long SrcId, AccessRow Row)>> FetchAuditRowsAsync(
        string sql, SourceKind source, long lastId, DateTimeOffset? since, int batch, CancellationToken ct,
        DateTimeOffset? until = null)
    {
        var results = new List<(long, AccessRow)>();
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd  = new NpgsqlCommand(sql, conn);
        cmd.CommandTimeout = 120;
        cmd.Parameters.AddWithValue("@lastId",      lastId);
        cmd.Parameters.AddWithValue("@batch",       batch);
        if (since.HasValue)
            cmd.Parameters.AddWithValue("@since", since.Value);
        if (until.HasValue)
            cmd.Parameters.AddWithValue("@until", until.Value);

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

            // 部署に解決しない読み取り（Data共有ルート直下＝パスがスラッシュのみ）は除外。
            // また MTlock関連\system 配下（VAL自身の配置領域・システムアプリ）も部署アクセスではないので除外。
            if (dept == "(不明)") continue;
            var probe = (fpath ?? "") + "|" + (dpath ?? "");
            if (probe.Contains("\\MTlock関連\\system\\", StringComparison.OrdinalIgnoreCase)) continue;

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

    // ---- 設定(P4設定の読み取り)・GAP（読み取りのみ）-----------------------

    /// <summary>汎用: SQL を実行し各行を map で射影してリスト化（テーブル欠落/権限不足時は空）。</summary>
    private async Task<List<T>> QueryListAsync<T>(string sql, Func<NpgsqlDataReader, T> map, CancellationToken ct)
    {
        var list = new List<T>();
        try
        {
            await using var conn = await _ds.OpenConnectionAsync(ct);
            await using var cmd  = new NpgsqlCommand(sql, conn) { CommandTimeout = 30 };
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) list.Add(map((NpgsqlDataReader)r));
        }
        catch { /* テーブル無し/権限不足など — 部分的に空 */ }
        return list;
    }

    /// <summary>AuditLogger の設定テーブル群を読み取り SettingsData を返す（読み取り専用）。</summary>
    public async Task<SettingsData> ReadSettingsAsync(CancellationToken ct = default)
    {
        static string S(NpgsqlDataReader r, int i)  => r.IsDBNull(i) ? "" : r.GetString(i);
        static string? Sn(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
        static int I(NpgsqlDataReader r, int i)     => Convert.ToInt32(r.GetValue(i));

        var folders = await QueryListAsync(
            $"SELECT id, server_name, folder_path, importance::text, monitor_read, monitor_write, monitor_delete, enabled FROM {_schema}.monitored_folders ORDER BY server_name, folder_path",
            r => new MonitoredFolder(I(r,0), S(r,1), S(r,2), S(r,3), r.GetBoolean(4), r.GetBoolean(5), r.GetBoolean(6), r.GetBoolean(7)), ct);

        var users = await QueryListAsync(
            $"SELECT id, domain_name, user_name, display_name, department, role, enabled FROM {_schema}.users ORDER BY user_name",
            r => new UserConfig(I(r,0), S(r,1), S(r,2), S(r,3), S(r,4), S(r,5), r.GetBoolean(6)), ct);

        var rules = await QueryListAsync(
            $"SELECT id, rule_name, condition_type, severity::text, COALESCE(target_folder,target_user,target_server,''), COALESCE(threshold_count,0), COALESCE(time_window_minutes,0), only_off_hours, enabled FROM {_schema}.alert_rules ORDER BY rule_name",
            r => new AlertRule(I(r,0), S(r,1), S(r,2), S(r,3), S(r,4), I(r,5), I(r,6), r.GetBoolean(7), r.GetBoolean(8)), ct);

        var excl = await QueryListAsync(
            $"SELECT id, COALESCE(user_pattern,''), process_pattern, path_regex, COALESCE(reason,'') FROM {_schema}.detection_exclusions ORDER BY id",
            r => new DetectionExclusion(I(r,0), S(r,1), Sn(r,2), Sn(r,3), S(r,4)), ct);

        var common = await QueryListAsync(
            $"SELECT (row_number() OVER (ORDER BY folder_top))::int, folder_top, COALESCE(note,'') FROM {_schema}.common_folders ORDER BY folder_top",
            r => new CommonFolder(I(r,0), S(r,1), S(r,2)), ct);

        var grants = await QueryListAsync(
            $"SELECT id, user_name, kind, value FROM {_schema}.user_folder_grants ORDER BY user_name, kind, value",
            r => new UserFolderGrant(I(r,0), S(r,1), S(r,2), S(r,3)), ct);

        var app = await QueryListAsync(
            $"SELECT key, value FROM {_schema}.app_settings ORDER BY key",
            r => new AppSetting(S(r,0), S(r,1), ""), ct);

        return new SettingsData(folders, users, rules, excl, common, grants, app);
    }

    /// <summary>
    /// 監査GAP: collector_state から「停滞中の収集」を検出する（last_event_time が15分以上前、
    /// または status が GAP/RESET）。＝現在の途切れを GapWindow(最終イベント〜now) として返す。
    /// 過去のGAP区間の完全復元は collector_state だけでは不可（現在状態のみ保持のため）。
    /// </summary>
    public async Task<IReadOnlyList<GapWindow>> ReadGapsAsync(CancellationToken ct = default)
    {
        var gaps = new List<GapWindow>();
        try
        {
            await using var conn = await _ds.OpenConnectionAsync(ct);
            await using var cmd  = new NpgsqlCommand(
                $"SELECT server_name, COALESCE(status, last_status, 'OK'), last_event_time FROM {_schema}.collector_state", conn) { CommandTimeout = 30 };
            await using var r = await cmd.ExecuteReaderAsync(ct);
            var now = DateTimeOffset.UtcNow;
            while (await r.ReadAsync(ct))
            {
                var server = r.IsDBNull(0) ? "(unknown)" : r.GetString(0);
                var status = r.IsDBNull(1) ? "OK" : r.GetString(1);
                DateTimeOffset? lastEvt = r.IsDBNull(2) ? null : r.GetFieldValue<DateTimeOffset>(2);
                var stale = lastEvt is { } le && (now - le).TotalMinutes > 15;
                var bad   = status.IndexOf("GAP", StringComparison.OrdinalIgnoreCase) >= 0
                         || status.IndexOf("RESET", StringComparison.OrdinalIgnoreCase) >= 0;
                if ((stale || bad) && lastEvt is { } start)
                {
                    var mins = (int)(now - start).TotalMinutes;
                    gaps.Add(new GapWindow(start, now, $"{server} collector stalled (status={status}, ~{mins}m)"));
                }
            }
        }
        catch { }
        return gaps;
    }

    // ---- 内部ヘルパー -------------------------------------------------------

    /// <summary>共有ルート。Data\ を優先し、無ければ MTlock関連\ を見る。</summary>
    private static readonly string[] ShareRoots = { "Data\\", "MTlock関連\\" };

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
    /// 'D:\Data\技術部\電気設計\resource.xlsx' / '\\sv\Data\技術部\…' / 'D:\MTlock関連\技術部\…'
    /// → 共有ルート(Data または MTlock関連)以降の論理パス '技術部\電気設計\resource.xlsx'。
    /// どちらの共有ルートも見つからない場合は正規化したフルパスをそのまま返す。
    /// </summary>
    private static string? NormPath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        var norm = path.Replace('/', '\\');
        foreach (var root in ShareRoots)
        {
            var idx = norm.IndexOf(root, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0) return norm[(idx + root.Length)..];
        }
        return norm;
    }

    /// <summary>
    /// 共有ルート直下の最初のセグメントを部署名として抽出する。
    /// 'D:\Data\技術部\機械設計\…'→技術部 / 'D:\MTlock関連\技術部\…'→技術部 / どちらも無ければ '(不明)'。
    /// </summary>
    private static string ExtractDept(string? path)
    {
        if (string.IsNullOrEmpty(path)) return "(不明)";
        var norm = path.Replace('/', '\\');
        foreach (var root in ShareRoots)
        {
            var idx = norm.IndexOf(root, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) continue;
            var rest  = norm[(idx + root.Length)..];
            var slash = rest.IndexOf('\\');
            var seg   = slash >= 0 ? rest[..slash] : rest;
            if (!string.IsNullOrWhiteSpace(seg)) return seg;
        }
        return "(不明)";
    }

    public void Dispose() => _ds.Dispose();
}
