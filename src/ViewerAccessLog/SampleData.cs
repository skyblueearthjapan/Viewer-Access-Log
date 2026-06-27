namespace ViewerAccessLog;

/// <summary>
/// サンプルデータ源（DataMode=Sample）。サーバー未接続の自宅でも 3色UI を動かすための種データ。
/// 日付は固定（2026-06-27）で決め打ち＝毎回同じ画面になる。
/// 本番では SfeSqliteSource + AuditPgSource + 同期キャッシュに差し替える。
/// </summary>
public sealed class SampleLogSource : ILogSource
{
    private static readonly DateTimeOffset Day = new(2026, 6, 27, 0, 0, 0, TimeSpan.FromHours(9));
    private static readonly TimeSpan Jst = TimeSpan.FromHours(9);

    private const string Base = @"\\lineworks-sv\Data\技術部\機械設計";

    private readonly List<AccessRow> _rows = Build();
    private readonly List<GapWindow> _gaps = new()
    {
        // 監査が止まっていた区間。利用率の分母から除外する対象（DESIGN.md §6）。
        new GapWindow(Day.AddHours(2).AddMinutes(5), Day.AddHours(2).AddMinutes(43),
                      "Collector停止（EVTXログ一巡 / GAP疑い）"),
    };

    public IReadOnlyList<AccessRow> All() => _rows;
    public IReadOnlyList<GapWindow> Gaps() => _gaps;
    public DateTimeOffset? LastSync() => Day.AddHours(10).AddMinutes(32);          // 直近同期
    public DateTimeOffset? AuditLatestEvent() => Day.AddHours(10).AddMinutes(31);  // 監査最新イベント

    private static List<AccessRow> Build()
    {
        var rows = new List<AccessRow>();
        long id = 1;

        void Add(double hour, SourceKind src, string user, string action, ActionKind kind,
                 string? file, string folderSub, bool ok = true, string? pc = null,
                 string? ip = null, string? note = null)
        {
            var time = Day.AddHours(hour);
            var folder = $"{Base}\\{folderSub}";
            rows.Add(new AccessRow(id++, time, src, user, action, kind,
                file is null ? null : $"{folder}\\{file}", folder, pc, ip, ok, note));
        }

        // ---- 🟦 青：ビューアー経由（SFE SQLite）= 本人特定・確実 --------------------
        Add(9.05, SourceKind.Viewer, "yamanaka", "閲覧", ActionKind.Read, "梁.xlsx", "06強度計算(LW標準)", pc: "PC-YAMA", ip: "192.168.1.51");
        Add(9.13, SourceKind.Viewer, "kataoka", "閲覧", ActionKind.Read, "標準.pdf", "05設計標準書", pc: "PC-KATA", ip: "192.168.1.47");
        Add(9.20, SourceKind.Viewer, "imaizumi", "検索", ActionKind.Search, null, "（全体）", pc: "PC-IMAI", ip: "192.168.1.50", note: "クエリ: \"強度計算\"");
        Add(9.34, SourceKind.Viewer, "higurashi", "開く", ActionKind.Read, "作図.docx", "07作図標準書", pc: "PC-HIGU", ip: "192.168.1.58");
        Add(9.41, SourceKind.Viewer, "yamanaka", "閲覧", ActionKind.Read, "設計.xlsx", "09製品設計_標準設計書", pc: "PC-YAMA", ip: "192.168.1.51");
        Add(9.58, SourceKind.Viewer, "kataoka", "閲覧", ActionKind.Read, "報告.pptx", "10不具合報告会", pc: "PC-KATA", ip: "192.168.1.47");
        Add(10.06, SourceKind.Viewer, "kinoshita", "閲覧", ActionKind.Read, "購入部品.xlsx", "08購入部品選定標準書", pc: "PC-KINO", ip: "192.168.1.63");
        Add(10.18, SourceKind.Viewer, "higurashi", "開く", ActionKind.Read, "マニュアル.pdf", "11設計標準マニュアル", pc: "PC-HIGU", ip: "192.168.1.58");
        Add(10.27, SourceKind.Viewer, "imaizumi", "閲覧", ActionKind.Read, "梁.xlsx", "06強度計算(LW標準)", pc: "PC-IMAI", ip: "192.168.1.50");

        // ---- 🟥 赤：サーバー直接アクセス（audit 実ユーザー・is_content_read）= 要注目 ----
        Add(9.07, SourceKind.Direct, "sasou", "読み取り", ActionKind.Read, "図面A.dwg", "03技術資料", pc: "PC-SASOU", ip: "192.168.1.31");
        Add(9.08, SourceKind.Direct, "sasou", "読み取り", ActionKind.Read, "図面B.dwg", "03技術資料", pc: "PC-SASOU", ip: "192.168.1.31");
        Add(9.08, SourceKind.Direct, "sasou", "読み取り", ActionKind.Read, "図面C.dwg", "03技術資料", pc: "PC-SASOU", ip: "192.168.1.31");
        Add(9.19, SourceKind.Direct, "sasou", "コピー疑い", ActionKind.Copy, "(37ファイル連続読取)", "03技術資料", pc: "PC-SASOU", ip: "192.168.1.31", note: "短時間に distinct 37 ファイルを内容読取");
        Add(9.46, SourceKind.Direct, "oku", "読み取り", ActionKind.Read, "設計.xlsx", "09製品設計_標準設計書", ok: false, pc: "PC-OKU", ip: "192.168.1.22", note: "アクセス拒否（権限なし）");
        Add(10.02, SourceKind.Direct, "nishida", "読み取り", ActionKind.Read, "標準.pdf", "05設計標準書", pc: "PC-NISI", ip: "192.168.1.40");

        // ---- ⬜ 灰：未帰属（MTSV$ / サービス / NULL）= ビューアーの証明ではない -----------
        Add(9.05, SourceKind.Unknown, @"LINEWORKS-MTSV$", "読み取り", ActionKind.Read, "梁.xlsx", "06強度計算(LW標準)", note: "マシンアカウント（ビューアー代理の可能性／未帰属）");
        Add(9.34, SourceKind.Unknown, @"LINEWORKS-MTSV$", "読み取り", ActionKind.Read, "作図.docx", "07作図標準書", note: "マシンアカウント（未帰属）");
        Add(3.12, SourceKind.Unknown, @"LINEWORKS-NET\svc-dove", "読み取り", ActionKind.Read, "梁.xlsx", "06強度計算(LW標準)", note: "バックアップサービス（除外候補）");
        Add(7.45, SourceKind.Unknown, "(NULL)", "読み取り", ActionKind.Read, "図面A.dwg", "03技術資料", note: "SID未解決／パース失敗（要確認）");

        return rows.OrderByDescending(r => r.Time).ToList();
    }
}
