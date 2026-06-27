namespace ViewerAccessLog;

/// <summary>
/// キャッシュ（今はサンプル）を読み、検索・集計・健全性を返す。
/// UIはここ（=キャッシュ投影）だけを叩く。ソースDBへは直接アクセスしない（DESIGN.md §3）。
/// </summary>
public sealed class LogService(ILogSource source)
{
    public LogPage Search(LogQuery q)
    {
        var rows = Filtered(q, applySourceKind: true)
            .OrderByDescending(r => r.Time)
            .ToList();

        var page = Math.Max(1, q.Page);
        var size = Math.Clamp(q.PageSize, 1, 500);
        var slice = rows.Skip((page - 1) * size).Take(size).ToList();
        return new LogPage(rows.Count, page, size, slice);
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
        return new HealthInfo("Sample", last, Math.Abs(lag), latest, source.Gaps());
    }

    public object Filters()
    {
        var all = source.All();
        return new
        {
            users = all.Select(r => r.User).Distinct().OrderBy(u => u).ToArray(),
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
