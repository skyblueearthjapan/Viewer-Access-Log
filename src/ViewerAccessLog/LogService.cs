namespace ViewerAccessLog;

/// <summary>
/// キャッシュ（今はサンプル）を読み、検索・集計・健全性を返す。
/// UIはここ（=キャッシュ投影）だけを叩く。ソースDBへは直接アクセスしない（DESIGN.md §3）。
/// </summary>
public sealed class LogService(ILogSource source)
{
    public LogPage Search(LogQuery q)
    {
        var filtered = Filtered(q, applySourceKind: true).ToList();

        // ソート列とソート方向。既定は日時降順。
        var rows = q.Sort?.ToLowerInvariant() switch
        {
            "user" => q.Desc ? filtered.OrderByDescending(r => r.User).ToList() : filtered.OrderBy(r => r.User).ToList(),
            "dept" => q.Desc ? filtered.OrderByDescending(r => r.Dept).ToList() : filtered.OrderBy(r => r.Dept).ToList(),
            "kind" => q.Desc ? filtered.OrderByDescending(r => r.Kind).ToList() : filtered.OrderBy(r => r.Kind).ToList(),
            _      => q.Desc ? filtered.OrderByDescending(r => r.Time).ToList() : filtered.OrderBy(r => r.Time).ToList(),
        };

        var page = Math.Max(1, q.Page);
        var size = Math.Clamp(q.PageSize, 1, 500);
        var slice = rows.Skip((page - 1) * size).Take(size).ToList();
        return new LogPage(rows.Count, page, size, slice);
    }

    /// <summary>CSV用：ページングなし全件取得（上限 50000 行）。</summary>
    public IReadOnlyList<AccessRow> SearchAll(LogQuery q)
    {
        var filtered = Filtered(q, applySourceKind: true).ToList();
        return (q.Sort?.ToLowerInvariant() switch
        {
            "user" => q.Desc ? filtered.OrderByDescending(r => r.User) : (IEnumerable<AccessRow>)filtered.OrderBy(r => r.User),
            "dept" => q.Desc ? filtered.OrderByDescending(r => r.Dept) : (IEnumerable<AccessRow>)filtered.OrderBy(r => r.Dept),
            "kind" => q.Desc ? filtered.OrderByDescending(r => r.Kind) : (IEnumerable<AccessRow>)filtered.OrderBy(r => r.Kind),
            _      => q.Desc ? filtered.OrderByDescending(r => r.Time) : (IEnumerable<AccessRow>)filtered.OrderBy(r => r.Time),
        }).Take(50000).ToList();
    }

    /// <summary>KPI集計。期間/ユーザー/検索語は効かせるが、ソース・操作トグルは無視して全体像を出す。</summary>
    public Summary Summarize(LogQuery q)
    {
        var inRange = Filtered(q, applySourceKind: false).ToList();

        long viewer = inRange.Count(r => r.Source == SourceKind.Viewer);
        long direct = inRange.Count(r => r.Source == SourceKind.Direct);
        long unknown = inRange.Count(r => r.Source == SourceKind.Unknown);

        // 利用率は「監査GAP時間帯」を分母から除外する（DESIGN.md §6 / Codex）。
        var gaps = source.Gaps();
        var trustworthy = inRange.Where(r => !InGap(r.Time, gaps) && r.Source != SourceKind.Unknown).ToList();
        long tViewer = trustworthy.Count(r => r.Source == SourceKind.Viewer);
        long tDirect = trustworthy.Count(r => r.Source == SourceKind.Direct);
        double adoption = (tViewer + tDirect) == 0 ? 0 : (double)tViewer / (tViewer + tDirect);

        int directUsers = inRange.Where(r => r.Source == SourceKind.Direct).Select(r => r.User).Distinct().Count();
        int directFiles = inRange.Where(r => r.Source == SourceKind.Direct && r.File is not null)
                                 .Select(r => r.File).Distinct().Count();

        int gapMinutes = GapMinutesInRange(q, gaps);

        return new Summary(inRange.Count, viewer, direct, unknown,
            directUsers, directFiles, Math.Round(adoption, 3), gapMinutes);
    }

    public HealthInfo Health()
    {
        var last = source.LastSync();
        var latest = source.AuditLatestEvent();
        int lag = last is { } l && latest is { } e ? (int)(l - e).TotalSeconds : 0;
        return new HealthInfo("Sample", last, Math.Abs(lag), latest, source.Gaps(), source.Collectors());
    }

    public IReadOnlyList<AlertItem> Alerts() => source.Alerts();
    public IReadOnlyList<IncidentItem> Incidents() => source.Incidents();

    /// <summary>ダッシュボード：KPI＋時間帯別スタック＋直接Topユーザー＋部署別件数＋直近インシデント。</summary>
    public DashboardData Dashboard(LogQuery q)
    {
        var summary = Summarize(q);
        var inRange = Filtered(q, applySourceKind: false).ToList();

        var hourly = Enumerable.Range(0, 24).Select(h =>
        {
            var hr = inRange.Where(r => r.Time.Hour == h).ToList();
            return new HourPoint(h,
                hr.Count(r => r.Source == SourceKind.Viewer),
                hr.Count(r => r.Source == SourceKind.Direct),
                hr.Count(r => r.Source == SourceKind.Unknown));
        }).ToList();

        var directTop = inRange.Where(r => r.Source == SourceKind.Direct)
            .GroupBy(r => r.User)
            .Select(g => new NameCount(g.Key, g.Count()))
            .OrderByDescending(x => x.Count).ThenBy(x => x.Name)
            .Take(8).ToList();

        var deptCounts = inRange
            .GroupBy(r => r.Dept)
            .Select(g => new NameCount(g.Key, g.Count()))
            .OrderByDescending(x => x.Count).ThenBy(x => x.Name)
            .ToList();

        var recentIncidents = source.Incidents()
            .OrderByDescending(i => i.Time).Take(5).ToList();

        // B1: 操作種別内訳（全ソース対象）
        var actionBreakdown = inRange
            .GroupBy(r => r.Kind.ToString())
            .Select(g => new NameCount(g.Key, g.Count()))
            .OrderByDescending(x => x.Count)
            .ToList();

        return new DashboardData(summary, hourly, directTop, deptCounts, recentIncidents, actionBreakdown);
    }

    /// <summary>ユーザー別一覧（青/赤/灰件数・最終アクセス）。部署は最頻出フォルダ部署で代表させる。</summary>
    public IReadOnlyList<UserRow> Users(LogQuery q)
    {
        return Filtered(q, applySourceKind: false)
            .GroupBy(r => r.User)
            .Select(g => new UserRow(
                g.Key,
                g.GroupBy(r => r.Dept).OrderByDescending(d => d.Count()).ThenBy(d => d.Key).First().Key,
                g.Count(r => r.Source == SourceKind.Viewer),
                g.Count(r => r.Source == SourceKind.Direct),
                g.Count(r => r.Source == SourceKind.Unknown),
                g.Max(r => r.Time)))
            .OrderByDescending(u => u.Direct).ThenByDescending(u => u.Viewer + u.Unknown).ThenBy(u => u.User)
            .ToList();
    }

    /// <summary>ユーザー詳細（時系列タイムライン＋ソース別サマリ＋時間帯別＋操作種別内訳）。</summary>
    public UserDetail? UserDetail(string name, LogQuery q)
    {
        var rows = Filtered(q, applySourceKind: false)
            .Where(r => r.User.Equals(name, StringComparison.OrdinalIgnoreCase))
            .OrderBy(r => r.Time)
            .ToList();
        if (rows.Count == 0) return null;

        var dept = rows.GroupBy(r => r.Dept).OrderByDescending(d => d.Count()).ThenBy(d => d.Key).First().Key;

        // B2: 時間帯別 3色
        var userHourly = Enumerable.Range(0, 24).Select(h =>
        {
            var hr = rows.Where(r => r.Time.Hour == h).ToList();
            return new HourPoint(h,
                hr.Count(r => r.Source == SourceKind.Viewer),
                hr.Count(r => r.Source == SourceKind.Direct),
                hr.Count(r => r.Source == SourceKind.Unknown));
        }).ToList();

        // B2: 操作種別内訳
        var actionBreakdown = rows
            .GroupBy(r => r.Kind.ToString())
            .Select(g => new NameCount(g.Key, g.Count()))
            .OrderByDescending(x => x.Count)
            .ToList();

        return new UserDetail(rows[0].User, dept,
            rows.Count(r => r.Source == SourceKind.Viewer),
            rows.Count(r => r.Source == SourceKind.Direct),
            rows.Count(r => r.Source == SourceKind.Unknown),
            rows, userHourly, actionBreakdown);
    }

    /// <summary>P4 設定取得（読み取りのみ。書込は P4 の限定書込ロール）。</summary>
    public SettingsData Settings() => source.Settings();

    public object Filters()
    {
        var all = source.All();
        return new
        {
            users = all.Select(r => r.User).Distinct().OrderBy(u => u).ToArray(),
            depts = all.Select(r => r.Dept).Distinct().OrderBy(d => d).ToArray(),
            sources = new[] { "viewer", "direct", "unknown" },
            kinds = Enum.GetNames<ActionKind>().Select(s => s.ToLowerInvariant()).ToArray(),
        };
    }

    private IEnumerable<AccessRow> Filtered(LogQuery q, bool applySourceKind)
    {
        IEnumerable<AccessRow> rows = source.All();

        if (q.From is { } from) rows = rows.Where(r => r.Time >= from);
        if (q.To is { } to) rows = rows.Where(r => r.Time < to);
        if (!string.IsNullOrWhiteSpace(q.User))
            rows = rows.Where(r => r.User.Contains(q.User, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(q.Dept))
            rows = rows.Where(r => r.Dept == q.Dept);

        if (!string.IsNullOrWhiteSpace(q.Q))
        {
            var s = q.Q.Trim();
            rows = rows.Where(r =>
                (r.File?.Contains(s, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (r.Folder?.Contains(s, StringComparison.OrdinalIgnoreCase) ?? false) ||
                r.User.Contains(s, StringComparison.OrdinalIgnoreCase) ||
                (r.Note?.Contains(s, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        if (applySourceKind)
        {
            if (q.Sources is { Length: > 0 })
            {
                var set = q.Sources.Select(s => s.ToLowerInvariant()).ToHashSet();
                rows = rows.Where(r => set.Contains(r.Source.ToString().ToLowerInvariant()));
            }
            if (q.Kinds is { Length: > 0 })
            {
                var set = q.Kinds.Select(s => s.ToLowerInvariant()).ToHashSet();
                rows = rows.Where(r => set.Contains(r.Kind.ToString().ToLowerInvariant()));
            }
        }

        return rows;
    }

    private static bool InGap(DateTimeOffset t, IReadOnlyList<GapWindow> gaps)
        => gaps.Any(g => t >= g.Start && t < g.End);

    private static int GapMinutesInRange(LogQuery q, IReadOnlyList<GapWindow> gaps)
    {
        var from = q.From ?? DateTimeOffset.MinValue;
        var to = q.To ?? DateTimeOffset.MaxValue;
        double total = 0;
        foreach (var g in gaps)
        {
            var s = g.Start > from ? g.Start : from;
            var e = g.End < to ? g.End : to;
            if (e > s) total += (e - s).TotalMinutes;
        }
        return (int)Math.Round(total);
    }
}
