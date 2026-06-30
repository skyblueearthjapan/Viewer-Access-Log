namespace ViewerAccessLog;

using System.Globalization;
using System.Text;
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

    /// <summary>access_rows の取得列（AccessRow と同じ並び）。</summary>
    private const string RowCols =
        "id, source, time, user, dept, action, kind, file, folder, pc, ip, success, note";

    /// <summary>
    /// 10分バケット・セッションキー（SQLite 集計専用）。
    /// user + folder + file + 10分窓の組み合わせで重複イベントを1セッションに畳む。
    /// time は 'yyyy-MM-dd HH:mm:ss'(UTC固定幅)保存なので substr(time,1,15) = 'yyyy-MM-dd HH:m' = 10分バケット。
    /// 集計の COUNT に使用。ログ検索(Search/SearchAll)は生イベントのまま。
    /// </summary>
    private const string SessKey =
        "user || '|' || COALESCE(folder,'') || '|' || COALESCE(file,'') || '|' || substr(time,1,15)";

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
                _pg = new AuditPgReader(opts.AuditPg.ConnectionString, opts.AuditPg.Schema);
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
            cmd.CommandText = $"SELECT {RowCols} FROM access_rows ORDER BY time ASC";
            var rows = new List<AccessRow>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
                rows.Add(ReadRow(r));
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

    // ====================================================================
    // SQL pushdown（Live 集計）：cache.db に対して WHERE/集計/LIMIT を実行し、
    // 全行をメモリに載せない（MTSV の RAM 逼迫対策）。LogService が DataMode=="Live"
    // のとき各メソッドへ委譲する。Sample 経路（LINQ）は LogService 側で不変。
    // 読み取り専用接続（Mode=ReadOnly）。SQL は全てパラメータ化。
    // ====================================================================

    /// <summary>検索（ページング・ソート）。WHERE + ORDER BY + LIMIT/OFFSET、別途 COUNT(*)。</summary>
    public LogPage SearchSql(LogQuery q)
    {
        var page = Math.Max(1, q.Page);
        var size = Math.Clamp(q.PageSize, 1, 500);
        try
        {
            using var conn = OpenRead();

            long total;
            using (var ccmd = conn.CreateCommand())
            {
                var cwhere = new StringBuilder("WHERE 1=1");
                ApplyFilters(ccmd, cwhere, q, applySourceKind: true);
                ccmd.CommandText = $"SELECT COUNT(*) FROM access_rows {cwhere}";
                total = Convert.ToInt64(ccmd.ExecuteScalar() ?? 0L);
            }

            var rows = new List<AccessRow>();
            using (var cmd = conn.CreateCommand())
            {
                var where = new StringBuilder("WHERE 1=1");
                ApplyFilters(cmd, where, q, applySourceKind: true);
                cmd.CommandText =
                    $"SELECT {RowCols} FROM access_rows {where} " +
                    $"ORDER BY {OrderBy(q)} LIMIT @size OFFSET @off";
                cmd.Parameters.AddWithValue("@size", size);
                cmd.Parameters.AddWithValue("@off", (page - 1) * size);
                using var r = cmd.ExecuteReader();
                while (r.Read()) rows.Add(ReadRow(r));
            }
            return new LogPage(total, page, size, rows);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SearchSql failed");
            return new LogPage(0, page, size, []);
        }
    }

    /// <summary>CSV 用：ページングなし（上限 50000 行）。同 WHERE/ORDER。</summary>
    public IReadOnlyList<AccessRow> SearchAllSql(LogQuery q)
    {
        try
        {
            using var conn = OpenRead();
            using var cmd = conn.CreateCommand();
            var where = new StringBuilder("WHERE 1=1");
            ApplyFilters(cmd, where, q, applySourceKind: true);
            cmd.CommandText =
                $"SELECT {RowCols} FROM access_rows {where} ORDER BY {OrderBy(q)} LIMIT 50000";
            var rows = new List<AccessRow>();
            using var r = cmd.ExecuteReader();
            while (r.Read()) rows.Add(ReadRow(r));
            return rows;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SearchAllSql failed");
            return [];
        }
    }

    /// <summary>KPI 集計。ソース/操作トグルは無視（applySourceKind:false）。GAP 時間帯は利用率の分母から除外。</summary>
    public Summary SummarizeSql(LogQuery q)
    {
        try
        {
            using var conn = OpenRead();

            long viewer = 0, direct = 0, unknown = 0;
            using (var cmd = conn.CreateCommand())
            {
                var where = new StringBuilder("WHERE 1=1");
                ApplyFilters(cmd, where, q, applySourceKind: false);
                // セッション数 = 10分バケット内の同一 user+folder+file を1件と数える（公平カウント）。
                cmd.CommandText = $"SELECT source, COUNT(DISTINCT ({SessKey})) FROM access_rows {where} GROUP BY source";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    var c = r.GetInt64(1);
                    switch (r.GetString(0))
                    {
                        case "viewer":  viewer  = c; break;
                        case "direct":  direct  = c; break;
                        default:        unknown = c; break;
                    }
                }
            }
            long total = viewer + direct + unknown;

            int directUsers = 0, directFiles = 0;
            using (var cmd = conn.CreateCommand())
            {
                var where = new StringBuilder("WHERE source='direct'");
                ApplyFilters(cmd, where, q, applySourceKind: false);
                cmd.CommandText =
                    "SELECT COUNT(DISTINCT user), " +
                    "COUNT(DISTINCT CASE WHEN file IS NOT NULL THEN file END) " +
                    $"FROM access_rows {where}";
                using var r = cmd.ExecuteReader();
                if (r.Read())
                {
                    directUsers = r.IsDBNull(0) ? 0 : (int)r.GetInt64(0);
                    directFiles = r.IsDBNull(1) ? 0 : (int)r.GetInt64(1);
                }
            }

            // 利用率: GAP 時間帯と Unknown を分母から除外（DESIGN.md §6）。
            var gaps = Gaps();
            long tViewer = 0, tDirect = 0;
            using (var cmd = conn.CreateCommand())
            {
                var where = new StringBuilder("WHERE source IN ('viewer','direct')");
                ApplyFilters(cmd, where, q, applySourceKind: false);
                AppendGapExclusions(cmd, where, gaps);
                // 利用率算出もセッション数ベース（同一バケット重複除外 + GAP/Unknown除外）。
                cmd.CommandText = $"SELECT source, COUNT(DISTINCT ({SessKey})) FROM access_rows {where} GROUP BY source";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    var c = r.GetInt64(1);
                    if (r.GetString(0) == "viewer") tViewer = c; else tDirect = c;
                }
            }
            double adoption = (tViewer + tDirect) == 0 ? 0 : (double)tViewer / (tViewer + tDirect);
            int gapMinutes = LogService.GapMinutesInRange(q, gaps);

            return new Summary(total, viewer, direct, unknown,
                directUsers, directFiles, Math.Round(adoption, 3), gapMinutes);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SummarizeSql failed");
            return new Summary(0, 0, 0, 0, 0, 0, 0, 0);
        }
    }

    /// <summary>ダッシュボード：KPI＋時間帯別(JST)＋直接Topユーザー＋部署別件数＋操作種別内訳＋直近インシデント。</summary>
    public DashboardData DashboardSql(LogQuery q)
    {
        var summary = SummarizeSql(q);
        var recentIncidents = Incidents().OrderByDescending(i => i.Time).Take(5).ToList();

        try
        {
            using var conn = OpenRead();

            // hourly: JST 時で source 別に集計し 24×3 に整形。
            var hViewer = new long[24]; var hDirect = new long[24]; var hUnknown = new long[24];
            using (var cmd = conn.CreateCommand())
            {
                var where = new StringBuilder("WHERE 1=1");
                ApplyFilters(cmd, where, q, applySourceKind: false);
                // セッション数ベースで時間帯別に集計（10分バケット重複除外）。
                cmd.CommandText =
                    $"SELECT CAST(strftime('%H', datetime(time,'+9 hours')) AS INTEGER) hr, source, COUNT(DISTINCT ({SessKey})) " +
                    $"FROM access_rows {where} GROUP BY hr, source";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    if (r.IsDBNull(0)) continue;
                    var hr = (int)r.GetInt64(0);
                    if (hr < 0 || hr > 23) continue;
                    var c = r.GetInt64(2);
                    switch (r.GetString(1))
                    {
                        case "viewer":  hViewer[hr]  = c; break;
                        case "direct":  hDirect[hr]  = c; break;
                        default:        hUnknown[hr] = c; break;
                    }
                }
            }
            var hourly = Enumerable.Range(0, 24)
                .Select(h => new HourPoint(h, hViewer[h], hDirect[h], hUnknown[h])).ToList();

            var directTop = new List<NameCount>();
            using (var cmd = conn.CreateCommand())
            {
                var where = new StringBuilder("WHERE source='direct'");
                ApplyFilters(cmd, where, q, applySourceKind: false);
                cmd.CommandText =
                    $"SELECT user, COUNT(DISTINCT ({SessKey})) c FROM access_rows {where} " +
                    "GROUP BY user ORDER BY c DESC, user LIMIT 8";
                using var r = cmd.ExecuteReader();
                while (r.Read()) directTop.Add(new NameCount(r.GetString(0), r.GetInt64(1)));
            }

            var deptCounts = new List<NameCount>();
            using (var cmd = conn.CreateCommand())
            {
                var where = new StringBuilder("WHERE 1=1");
                ApplyFilters(cmd, where, q, applySourceKind: false);
                cmd.CommandText =
                    $"SELECT dept, COUNT(DISTINCT ({SessKey})) c FROM access_rows {where} " +
                    "GROUP BY dept ORDER BY c DESC, dept";
                using var r = cmd.ExecuteReader();
                while (r.Read()) deptCounts.Add(new NameCount(r.GetString(0), r.GetInt64(1)));
            }

            // 操作種別内訳：kind で集計→C# で日本語ラベルに畳む（未知 kind が同ラベルに合流しても正しく合算）。
            var kindRaw = new List<(string Kind, long Count)>();
            using (var cmd = conn.CreateCommand())
            {
                var where = new StringBuilder("WHERE 1=1");
                ApplyFilters(cmd, where, q, applySourceKind: false);
                cmd.CommandText = $"SELECT kind, COUNT(DISTINCT ({SessKey})) c FROM access_rows {where} GROUP BY kind";
                using var r = cmd.ExecuteReader();
                while (r.Read()) kindRaw.Add((r.GetString(0), r.GetInt64(1)));
            }
            var actionBreakdown = kindRaw
                .GroupBy(x => LogService.KindLabel(ParseKind(x.Kind)))
                .Select(g => new NameCount(g.Key, g.Sum(x => x.Count)))
                .OrderByDescending(x => x.Count)
                .ToList();

            return new DashboardData(summary, hourly, directTop, deptCounts, recentIncidents, actionBreakdown);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DashboardSql failed");
            return new DashboardData(summary, [], [], [], recentIncidents, []);
        }
    }

    /// <summary>ユーザー別一覧（青/赤/灰件数・最終アクセス）。部署は最頻出フォルダ部署で代表させる。</summary>
    public IReadOnlyList<UserRow> UsersSql(LogQuery q)
    {
        try
        {
            using var conn = OpenRead();

            // user × dept 件数 → C# で user 毎の最頻 dept に畳む（件数はユーザー×部署で有界）。
            var deptByUser = new Dictionary<string, (string Dept, long Count)>();
            using (var cmd = conn.CreateCommand())
            {
                var where = new StringBuilder("WHERE 1=1");
                ApplyFilters(cmd, where, q, applySourceKind: false);
                // topDept 判定もセッション数（最頻 dept をセッション数で比較）。
                cmd.CommandText =
                    $"SELECT user, dept, COUNT(DISTINCT ({SessKey})) c FROM access_rows {where} " +
                    "GROUP BY user, dept ORDER BY user, c DESC, dept";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    var u = r.GetString(0); var d = r.GetString(1); var c = r.GetInt64(2);
                    if (!deptByUser.TryGetValue(u, out var cur) || c > cur.Count
                        || (c == cur.Count && string.CompareOrdinal(d, cur.Dept) < 0))
                        deptByUser[u] = (d, c);
                }
            }

            var users = new List<UserRow>();
            using (var cmd = conn.CreateCommand())
            {
                var where = new StringBuilder("WHERE 1=1");
                ApplyFilters(cmd, where, q, applySourceKind: false);
                // ユーザー別3色件数をセッション数で（同一 user+folder+file+10分窓を1件に畳む）。
                // CASE が NULL を返す場合 COUNT(DISTINCT) は NULL を無視するので正しく集計できる。
                cmd.CommandText =
                    $"SELECT user, " +
                    $"COUNT(DISTINCT CASE WHEN source='viewer'  THEN ({SessKey}) END), " +
                    $"COUNT(DISTINCT CASE WHEN source='direct'  THEN ({SessKey}) END), " +
                    $"COUNT(DISTINCT CASE WHEN source='unknown' THEN ({SessKey}) END), " +
                    "MAX(time) " +
                    $"FROM access_rows {where} GROUP BY user";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    var u = r.GetString(0);
                    var dept = deptByUser.TryGetValue(u, out var dv) ? dv.Dept : "(不明)";
                    users.Add(new UserRow(u, dept,
                        r.IsDBNull(1) ? 0 : r.GetInt64(1),
                        r.IsDBNull(2) ? 0 : r.GetInt64(2),
                        r.IsDBNull(3) ? 0 : r.GetInt64(3),
                        r.IsDBNull(4) ? DateTimeOffset.MinValue : ParseUtc(r.GetString(4))));
                }
            }

            return users
                .OrderByDescending(u => u.Direct)
                .ThenByDescending(u => u.Viewer + u.Unknown)
                .ThenBy(u => u.User)
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "UsersSql failed");
            return [];
        }
    }

    /// <summary>ユーザー詳細（時系列タイムライン＋3色件数＋時間帯別(JST)＋操作種別内訳）。単一ユーザーで有界。</summary>
    public UserDetail? UserDetailSql(string name, LogQuery q)
    {
        try
        {
            using var conn = OpenRead();
            using var cmd = conn.CreateCommand();
            var where = new StringBuilder("WHERE user = @name COLLATE NOCASE");
            cmd.Parameters.AddWithValue("@name", name);
            ApplyFilters(cmd, where, q, applySourceKind: false);
            cmd.CommandText = $"SELECT {RowCols} FROM access_rows {where} ORDER BY time ASC";
            var rows = new List<AccessRow>();
            using (var r = cmd.ExecuteReader())
                while (r.Read()) rows.Add(ReadRow(r));
            if (rows.Count == 0) return null;

            var dept = rows.GroupBy(r => r.Dept)
                .OrderByDescending(d => d.Count()).ThenBy(d => d.Key).First().Key;

            // セッションキー（C#版）= SQL SessKey と同一定義。10分バケットで重複を畳む。
            static string SKey(AccessRow r) =>
                FormattableString.Invariant(
                    $"{r.User}|{r.Folder ?? ""}|{r.File ?? ""}|{r.Time.UtcDateTime:yyyy-MM-dd HH:m}");

            var jst = TimeSpan.FromHours(9);
            var userHourly = Enumerable.Range(0, 24).Select(h =>
            {
                var hr = rows.Where(r => r.Time.ToOffset(jst).Hour == h).ToList();
                return new HourPoint(h,
                    hr.Where(r => r.Source == SourceKind.Viewer).Select(SKey).Distinct().LongCount(),
                    hr.Where(r => r.Source == SourceKind.Direct).Select(SKey).Distinct().LongCount(),
                    hr.Where(r => r.Source == SourceKind.Unknown).Select(SKey).Distinct().LongCount());
            }).ToList();

            var actionBreakdown = rows
                .GroupBy(r => LogService.KindLabel(r.Kind))
                .Select(g => new NameCount(g.Key, g.Count()))
                .OrderByDescending(x => x.Count)
                .ToList();

            // 3色件数はセッション数。timeline(rows)は生イベントのまま（詳細監査証跡）。
            return new UserDetail(rows[0].User, dept,
                rows.Where(r => r.Source == SourceKind.Viewer).Select(SKey).Distinct().LongCount(),
                rows.Where(r => r.Source == SourceKind.Direct).Select(SKey).Distinct().LongCount(),
                rows.Where(r => r.Source == SourceKind.Unknown).Select(SKey).Distinct().LongCount(),
                rows, userHourly, actionBreakdown);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "UserDetailSql failed");
            return null;
        }
    }

    /// <summary>部署別利用率：dept毎にセッション数(viewer/direct/unknown/total)と利用率を返す。Total降順。</summary>
    public IReadOnlyList<DeptAdoption> DepartmentsSql(LogQuery q)
    {
        try
        {
            using var conn = OpenRead();
            using var cmd  = conn.CreateCommand();
            var where = new StringBuilder("WHERE 1=1");
            ApplyFilters(cmd, where, q, applySourceKind: false);
            cmd.CommandText =
                $"SELECT dept, " +
                $"COUNT(DISTINCT CASE WHEN source='viewer'  THEN ({SessKey}) END) v, " +
                $"COUNT(DISTINCT CASE WHEN source='direct'  THEN ({SessKey}) END) d, " +
                $"COUNT(DISTINCT CASE WHEN source='unknown' THEN ({SessKey}) END) u, " +
                $"COUNT(DISTINCT ({SessKey})) total " +
                $"FROM access_rows {where} " +
                "GROUP BY dept ORDER BY total DESC, dept";
            var result = new List<DeptAdoption>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var dept  = r.GetString(0);
                long v    = r.IsDBNull(1) ? 0 : r.GetInt64(1);
                long d    = r.IsDBNull(2) ? 0 : r.GetInt64(2);
                long u    = r.IsDBNull(3) ? 0 : r.GetInt64(3);
                long tot  = r.IsDBNull(4) ? 0 : r.GetInt64(4);
                double ad = (v + d) == 0 ? 0 : Math.Round((double)v / (v + d), 3);
                result.Add(new DeptAdoption(dept, v, d, u, tot, ad));
            }
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DepartmentsSql failed");
            return [];
        }
    }

    /// <summary>フィルタ候補：DISTINCT user / DISTINCT dept（sources/kinds は固定列挙のため LogService 側）。</summary>
    public (string[] Users, string[] Depts) FiltersSql()
    {
        try
        {
            using var conn = OpenRead();
            var users = new List<string>();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT DISTINCT user FROM access_rows ORDER BY user";
                using var r = cmd.ExecuteReader();
                while (r.Read()) users.Add(r.GetString(0));
            }
            var depts = new List<string>();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT DISTINCT dept FROM access_rows ORDER BY dept";
                using var r = cmd.ExecuteReader();
                while (r.Read()) depts.Add(r.GetString(0));
            }
            return (users.ToArray(), depts.ToArray());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "FiltersSql failed");
            return ([], []);
        }
    }

    // ---- SQL pushdown 内部ヘルパー -----------------------------------------

    private SqliteConnection OpenRead()
    {
        var conn = new SqliteConnection($"Data Source={_opts.CachePath};Mode=ReadOnly;Cache=Shared");
        conn.Open();
        return conn;
    }

    /// <summary>UTC 固定幅文字列 'yyyy-MM-dd HH:mm:ss'（SQLite 比較用）。</summary>
    private static string Utc(DateTimeOffset t) => t.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss");

    /// <summary>保存 UTC 文字列を DateTimeOffset(UTC, Zero offset) として復元（旧 'o' 形式も許容）。</summary>
    private static DateTimeOffset ParseUtc(string s) =>
        DateTime.TryParse(s, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dt)
            ? new DateTimeOffset(dt, TimeSpan.Zero)
            : DateTimeOffset.UtcNow;

    private static AccessRow ReadRow(SqliteDataReader r) => new(
        Id:      r.GetInt64(0),
        Time:    ParseUtc(r.GetString(2)),
        Source:  ParseSource(r.GetString(1)),
        User:    r.GetString(3),
        Dept:    r.GetString(4),
        Action:  r.GetString(5),
        Kind:    ParseKind(r.GetString(6)),
        File:    r.IsDBNull(7)  ? null : r.GetString(7),
        Folder:  r.IsDBNull(8)  ? null : r.GetString(8),
        Pc:      r.IsDBNull(9)  ? null : r.GetString(9),
        Ip:      r.IsDBNull(10) ? null : r.GetString(10),
        Success: r.GetInt32(11) != 0,
        Note:    r.IsDBNull(12) ? null : r.GetString(12));

    /// <summary>ORDER BY 列（ホワイトリスト）＋方向。既定 time。</summary>
    private static string OrderBy(LogQuery q)
    {
        var col = q.Sort?.ToLowerInvariant() switch
        {
            "user" => "user",
            "dept" => "dept",
            "kind" => "kind",
            _      => "time",
        };
        return $"{col} {(q.Desc ? "DESC" : "ASC")}";
    }

    /// <summary>
    /// LogQuery を SQL WHERE 断片＋パラメータに変換し cmd へ適用する。
    /// where は "WHERE ..." で始めて渡すこと（本メソッドは " AND ..." を追記する）。
    /// Live で From/To が null の場合は直近24時間にクランプ（重い全件走査を回避）。
    /// </summary>
    private static void ApplyFilters(SqliteCommand cmd, StringBuilder where, LogQuery q, bool applySourceKind)
    {
        var to   = q.To   ?? DateTimeOffset.UtcNow;
        var from = q.From ?? to.AddDays(-1);
        where.Append(" AND time >= @from AND time < @to");
        cmd.Parameters.AddWithValue("@from", Utc(from));
        cmd.Parameters.AddWithValue("@to",   Utc(to));
        where.Append(" AND is_open = 1");

        if (!string.IsNullOrWhiteSpace(q.User))
        {
            where.Append(" AND user LIKE '%'||@user||'%'");
            cmd.Parameters.AddWithValue("@user", q.User.Trim());
        }
        if (!string.IsNullOrWhiteSpace(q.Dept))
        {
            where.Append(" AND dept = @dept");
            cmd.Parameters.AddWithValue("@dept", q.Dept);
        }
        if (!string.IsNullOrWhiteSpace(q.Q))
        {
            where.Append(" AND (file LIKE @qp OR folder LIKE @qp OR user LIKE @qp OR note LIKE @qp)");
            cmd.Parameters.AddWithValue("@qp", "%" + q.Q.Trim() + "%");
        }

        if (applySourceKind)
        {
            if (q.Sources is { Length: > 0 })
            {
                var names = q.Sources.Select(s => s.ToLowerInvariant())
                    .Where(s => s is "viewer" or "direct" or "unknown").Distinct().ToList();
                if (names.Count > 0)
                {
                    var ph = new List<string>();
                    for (int i = 0; i < names.Count; i++)
                    {
                        ph.Add($"@src{i}");
                        cmd.Parameters.AddWithValue($"@src{i}", names[i]);
                    }
                    where.Append($" AND source IN ({string.Join(",", ph)})");
                }
            }
            if (q.Kinds is { Length: > 0 })
            {
                var names = q.Kinds.Select(s => s.ToLowerInvariant()).Distinct().ToList();
                if (names.Count > 0)
                {
                    var ph = new List<string>();
                    for (int i = 0; i < names.Count; i++)
                    {
                        ph.Add($"@knd{i}");
                        cmd.Parameters.AddWithValue($"@knd{i}", names[i]);
                    }
                    where.Append($" AND kind IN ({string.Join(",", ph)})");
                }
            }
        }
    }

    /// <summary>各 GAP 時間帯の行を WHERE から除外する（利用率の信頼性確保）。UTC 比較。</summary>
    private static void AppendGapExclusions(SqliteCommand cmd, StringBuilder where, IReadOnlyList<GapWindow> gaps)
    {
        for (int i = 0; i < gaps.Count; i++)
        {
            where.Append($" AND NOT(time >= @gs{i} AND time < @ge{i})");
            cmd.Parameters.AddWithValue($"@gs{i}", Utc(gaps[i].Start));
            cmd.Parameters.AddWithValue($"@ge{i}", Utc(gaps[i].End));
        }
    }

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
