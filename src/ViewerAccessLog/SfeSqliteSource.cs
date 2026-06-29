namespace ViewerAccessLog;

using Microsoft.Data.Sqlite;

/// <summary>
/// SFE (Secure File Explorer) の catalog.db AccessLogs テーブルを読み取り専用で参照する。
/// Mode=ReadOnly で開くのでアプリはファイルへ一切書き込まない。
///
/// AccessLogs スキーマ（確定）:
///   Id INTEGER, TimestampUtc TEXT(UTC), UserName TEXT('DOMAIN\user'),
///   MachineName TEXT, IpAddress TEXT, Action INTEGER(0=ListFolder,1=OpenFile,2=Search,3=Error),
///   FileId INTEGER, FolderId INTEGER, Target TEXT, TargetPath TEXT('技術部 › …'),
///   Success INTEGER, FailureReason TEXT
/// </summary>
public sealed class SfeSqliteSource : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly string           _dept;

    /// <param name="dept">この catalog.db が属する部署名（全行の Dept に固定付与）。</param>
    public SfeSqliteSource(string dbPath, string dept)
    {
        _conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly;Cache=Shared");
        _conn.Open();
        _dept = dept;
    }

    /// <summary>
    /// AccessLogs から Id > lastId の行を取得して AccessRow に変換する（🟦 Viewer）。
    /// </summary>
    public IReadOnlyList<(long SrcId, AccessRow Row)> ReadViewerRows(long lastId, int batch = 1000)
    {
        var results = new List<(long, AccessRow)>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT Id, TimestampUtc, UserName, MachineName, IpAddress,
                   Action, Target, TargetPath, Success, FailureReason
            FROM AccessLogs
            WHERE Id > $lastId
            ORDER BY Id
            LIMIT $batch
            """;
        cmd.Parameters.AddWithValue("$lastId", lastId);
        cmd.Parameters.AddWithValue("$batch",  batch);

        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var srcId    = r.GetInt64(0);
            var tsStr    = r.IsDBNull(1) ? null : r.GetString(1);
            var rawUser  = r.IsDBNull(2) ? "unknown" : r.GetString(2);
            var pc       = r.IsDBNull(3) ? null : r.GetString(3);
            var ip       = r.IsDBNull(4) ? null : r.GetString(4);
            var action   = r.IsDBNull(5) ? 0 : r.GetInt32(5);
            var target   = r.IsDBNull(6) ? null : r.GetString(6);   // ファイル/フォルダ名
            var tpath    = r.IsDBNull(7) ? null : r.GetString(7);   // 論理パンくず
            var success  = r.IsDBNull(8) ? 1 : r.GetInt32(8);
            var failRsn  = r.IsDBNull(9) ? null : r.GetString(9);

            // UTC文字列 → DateTimeOffset(UTC)
            DateTimeOffset time;
            if (tsStr is not null && DateTime.TryParse(tsStr,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal |
                System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var dt))
                time = new DateTimeOffset(dt, TimeSpan.Zero);
            else
                time = DateTimeOffset.UtcNow;

            var user   = StripDomain(rawUser);
            // catalog.db は opts.Dept のビューアー専用 = Dept を固定付与（ExtractDept は使わない）。
            var dept   = _dept;
            var folder = NormalizeBreadcrumb(tpath);   // '技術部 › 設計書' → '技術部\設計書'
            var (kind, label) = MapAction(action);
            var note   = success == 0 && failRsn is not null ? failRsn : null;

            results.Add((srcId, new AccessRow(
                Id:      srcId,
                Time:    time,
                Source:  SourceKind.Viewer,
                User:    user,
                Dept:    dept,
                Action:  label,
                Kind:    kind,
                File:    target,
                Folder:  folder,
                Pc:      pc,
                Ip:      ip,
                Success: success != 0,
                Note:    note)));
        }
        return results;
    }

    // ---- 内部ヘルパー -------------------------------------------------------

    /// <summary>'LINEWORKS-NET\taro' → 'taro'</summary>
    private static string StripDomain(string raw)
    {
        var idx = raw.IndexOf('\\');
        return idx >= 0 ? raw[(idx + 1)..].Trim() : raw.Trim();
    }

    /// <summary>AccessAction int → (ActionKind, 表示文字列)</summary>
    private static (ActionKind Kind, string Label) MapAction(int a) => a switch
    {
        1 => (ActionKind.Read,   "開く"),
        2 => (ActionKind.Search, "検索"),
        3 => (ActionKind.Read,   "エラー読取"),
        _ => (ActionKind.Read,   "フォルダ参照"),   // 0 = ListFolder
    };

    /// <summary>'技術部 › 電気設計 › 資料' → '技術部\電気設計\資料'</summary>
    private static string? NormalizeBreadcrumb(string? tpath)
    {
        if (string.IsNullOrWhiteSpace(tpath)) return null;
        return string.Join("\\",
            tpath.Split(new[] { '›', '>' },
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    public void Dispose() => _conn.Dispose();
}
