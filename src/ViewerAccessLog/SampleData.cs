namespace ViewerAccessLog;

/// <summary>
/// サンプルデータ源（DataMode=Sample）。サーバー未接続の自宅でも 3色UI を動かすための種データ。
/// 日付は固定（2026-06-27）で決め打ち＝毎回同じ画面になる（Random / DateTime.Now を判定に使わない）。
/// 全部署（技術部/営業部/総務部/製造部/購買部/部署間共通/郵便局）を網羅し、各画面に3色を通す。
/// 本番では SfeSqliteSource + AuditPgSource + 同期キャッシュに差し替える。
/// </summary>
public sealed class SampleLogSource : ILogSource
{
    private static readonly DateTimeOffset Day = new(2026, 6, 27, 0, 0, 0, TimeSpan.FromHours(9));

    private const string Root = @"\\lineworks-sv\Data";

    private readonly List<AccessRow> _rows = Build();

    private readonly List<GapWindow> _gaps = new()
    {
        // 監査が止まっていた区間。利用率の分母から除外する対象（DESIGN.md §6）。
        new GapWindow(Day.AddHours(2).AddMinutes(5), Day.AddHours(2).AddMinutes(43),
                      "Collector停止（EVTXログ一巡 / GAP疑い）"),
        new GapWindow(Day.AddHours(12).AddMinutes(2), Day.AddHours(12).AddMinutes(18),
                      "MTSV再起動（パッチ適用 / 監査一時停止）"),
    };

    private readonly List<AlertItem> _alerts = new()
    {
        new AlertItem(1, Day.AddHours(9.32), "High",   "BULK_CONTENT_READ",    "sasou",    12, "未対応"),
        new AlertItem(2, Day.AddHours(9.46), "Medium", "ACCESS_DENIED_REPEAT", "oku",       3, "確認中"),
        new AlertItem(3, Day.AddHours(10.02), "Medium", "DIRECT_ACCESS",       "nishida",   1, "未対応"),
        new AlertItem(4, Day.AddHours(3.12), "Low",    "SERVICE_ACCOUNT_READ", @"LINEWORKS-NET\svc-dove", 5, "既知(除外候補)"),
        new AlertItem(5, Day.AddHours(7.45), "Low",    "UNRESOLVED_SID",       "(NULL)",    1, "調査中"),
    };

    private readonly List<IncidentItem> _incidents = new()
    {
        new IncidentItem(1, Day.AddHours(9.30), "BULK_CONTENT_READ", "High",   "sasou",   12, "distinct 12ファイル / 23分", "未対応"),
        new IncidentItem(2, Day.AddHours(9.30), "CROSS_DEPT_ACCESS", "High",   "sasou",   12, "営業部→技術部 直接読取",     "未対応"),
        new IncidentItem(3, Day.AddHours(9.46), "CROSS_DEPT_ACCESS", "Medium", "oku",      1, "営業部→技術部（拒否）",       "対応済"),
        new IncidentItem(4, Day.AddHours(10.02), "DIRECT_BYPASS",    "Medium", "nishida",  1, "ビューアー未経由の直接読取",   "確認中"),
    };

    private readonly List<CollectorState> _collectors = new()
    {
        new CollectorState("lineworks-sv",   "Security 4663 (NTFS監査)", Day.AddHours(10).AddMinutes(31),  60, "OK"),
        new CollectorState("lineworks-mtsv", "SFE SQLite / 同期Worker",  Day.AddHours(10).AddMinutes(20), 720, "遅延"),
    };

    public string DataMode => "Sample";
    public IReadOnlyList<AccessRow> All() => _rows;
    public IReadOnlyList<GapWindow> Gaps() => _gaps;
    public DateTimeOffset? LastSync() => Day.AddHours(10).AddMinutes(32);          // 直近同期
    public DateTimeOffset? AuditLatestEvent() => Day.AddHours(10).AddMinutes(31);  // 監査最新イベント
    public IReadOnlyList<AlertItem> Alerts() => _alerts;
    public IReadOnlyList<IncidentItem> Incidents() => _incidents;
    public IReadOnlyList<CollectorState> Collectors() => _collectors;
    public SettingsData Settings() => _settings;

    // ---- P4 設定サンプル（AuditLogger スキーマに沿う。読み取りのみ）--------------
    private static readonly SettingsData _settings = new(
        Folders: new List<MonitoredFolder>
        {
            new(1, "lineworks-sv", $@"{Root}\技術部",   "High",   true,  false, false, true),
            new(2, "lineworks-sv", $@"{Root}\営業部",   "Medium", true,  true,  false, true),
            new(3, "lineworks-sv", $@"{Root}\総務部",   "Medium", true,  false, false, true),
            new(4, "lineworks-sv", $@"{Root}\製造部",   "Low",    true,  false, false, true),
            new(5, "lineworks-sv", $@"{Root}\購買部",   "Low",    true,  false, false, false),
        },
        Users: new List<UserConfig>
        {
            new(1, "LINEWORKS-NET", "yamanaka",  "山中 太郎",         "技術部", "viewer",  true),
            new(2, "LINEWORKS-NET", "imaizumi",  "今泉 一郎",         "技術部", "admin",   true),
            new(3, "LINEWORKS-NET", "sasou",     "佐相 次郎",         "営業部", "viewer",  true),
            new(4, "LINEWORKS-NET", "takahashi", "高橋 花子",         "総務部", "viewer",  true),
            new(5, "LINEWORKS-NET", "svc-dove",  "バックアップSVC",   "システム","service", false),
        },
        Rules: new List<AlertRule>
        {
            new(1, "大量持ち出し検知",         "BULK_CONTENT_READ",    "High",   "*", 10, 30, false, true),
            new(2, "権限外アクセス繰返し",     "ACCESS_DENIED_REPEAT", "Medium", "*",  3,  5, false, true),
            new(3, "ビューアー未経由直接読取", "DIRECT_BYPASS",        "Low",    "*",  1, 60, false, true),
        },
        Exclusions: new List<DetectionExclusion>
        {
            new(1, @"LINEWORKS-NET\svc-dove", null, null, "バックアップサービス（定期読取）"),
            new(2, @"LINEWORKS-MTSV$",        null, null, "マシンアカウント（SFEエージェント代理の可能性）"),
            new(3, "(NULL)",                  null, null, "SID解決失敗（パース不能・要確認）"),
        },
        CommonFolders: new List<CommonFolder>
        {
            new(1, $@"{Root}\部署間共通", "全部署共通フォルダ（部署外アクセス判定から除外）"),
            new(2, $@"{Root}\郵便局",     "郵便局フォルダ（全社アクセス可・部署外判定除外）"),
        },
        UserGrants: new List<UserFolderGrant>
        {
            new(1, "kyodo",   "dept",    "部署間共通"),
            new(2, "shibata", "dept",    "部署間共通"),
            new(3, "yubin",   "postbox", "郵便局"),
        },
        AppSettings: new List<AppSetting>
        {
            new("notification.email.enabled",               "true",                         "メール通知を有効にする"),
            new("notification.email.to",                    "admin-01@lineworks-local.info","通知先メールアドレス"),
            new("notification.email.subject_prefix",        "[監査警告]",                   "件名プレフィックス"),
            new("detection.bulk.enabled",                   "true",                         "大量持ち出し検知を有効にする"),
            new("detection.bulk.threshold",                 "10",                           "検知しきい値（distinct ファイル数）"),
            new("detection.bulk.window_minutes",            "30",                           "検知時間窓（分）"),
            new("detection.offhours.start",                 "20:00",                        "夜間開始時刻"),
            new("detection.offhours.end",                   "06:00",                        "夜間終了時刻"),
            new("detection.crossdept.enabled",              "true",                         "部署外アクセス検知を有効にする"),
            new("detection.crossdept.common_folders_excluded","true",                       "共通フォルダを部署外判定から除外する"),
            new("log.retention_days",                       "365",                          "ログ保持日数"),
        }
    );

    /// <summary>ユーザーのPC/IP（決定的・固定）。</summary>
    private static readonly Dictionary<string, (string Pc, string Ip)> People = new()
    {
        ["yamanaka"] = ("PC-YAMA", "192.168.1.51"),
        ["kataoka"]  = ("PC-KATA", "192.168.1.47"),
        ["imaizumi"] = ("PC-IMAI", "192.168.1.50"),
        ["higurashi"] = ("PC-HIGU", "192.168.1.58"),
        ["kinoshita"] = ("PC-KINO", "192.168.1.63"),
        ["sasou"]    = ("PC-SASOU", "192.168.1.31"),
        ["oku"]      = ("PC-OKU", "192.168.1.22"),
        ["nishida"]  = ("PC-NISI", "192.168.1.40"),
        ["fujimoto"] = ("PC-FUJI", "192.168.1.35"),
        ["takahashi"] = ("PC-TAKA", "192.168.1.71"),
        ["ito"]      = ("PC-ITO", "192.168.1.72"),
        ["kobayashi"] = ("PC-KOBA", "192.168.1.81"),
        ["saito"]    = ("PC-SAITO", "192.168.1.82"),
        ["matsuda"]  = ("PC-MATSU", "192.168.1.83"),
        ["kato"]     = ("PC-KATO", "192.168.1.91"),
        ["yoshida"]  = ("PC-YOSI", "192.168.1.92"),
        ["kyodo"]    = ("PC-KYODO", "192.168.1.101"),
        ["shibata"]  = ("PC-SHIBA", "192.168.1.102"),
        ["postoffice"] = ("PC-POST", "192.168.1.111"),
        ["yubin"]    = ("PC-YUBIN", "192.168.1.112"),
    };

    /// <summary>部署ごとの種：代表ユーザー・代表フォルダ・代表ファイル・開始時刻。</summary>
    private sealed record DeptSeed(
        string Dept, string Base, string[] Users, string[] Subs, string[] Files, double BaseHour);

    private static readonly DeptSeed[] Seeds =
    {
        new("技術部", $@"{Root}\技術部\機械設計",
            new[] { "yamanaka", "kataoka", "imaizumi", "higurashi", "kinoshita" },
            new[] { "06強度計算(LW標準)", "05設計標準書", "07作図標準書", "09製品設計_標準設計書", "11設計標準マニュアル" },
            new[] { "梁.xlsx", "標準.pdf", "作図.docx", "設計.xlsx", "報告.pptx", "マニュアル.pdf", "購入部品.xlsx" }, 9.0),

        new("営業部", $@"{Root}\営業部",
            new[] { "sasou", "oku", "nishida", "fujimoto" },
            new[] { "01見積書", "02顧客台帳", "03提案資料" },
            new[] { "見積_A社.xlsx", "顧客一覧.xlsx", "提案.pptx", "価格表.pdf" }, 10.0),

        new("総務部", $@"{Root}\総務部",
            new[] { "takahashi", "ito" },
            new[] { "01就業規則", "02稟議", "03社内通達" },
            new[] { "就業規則.pdf", "稟議書.docx", "社内通達.pdf", "組織図.xlsx" }, 11.0),

        new("製造部", $@"{Root}\製造部",
            new[] { "kobayashi", "saito", "matsuda" },
            new[] { "01工程表", "02検査記録", "03作業手順" },
            new[] { "工程表.xlsx", "検査記録.xlsx", "作業手順.pdf", "不良集計.xlsx" }, 13.0),

        new("購買部", $@"{Root}\購買部",
            new[] { "kato", "yoshida" },
            new[] { "01発注", "02仕入先", "03単価" },
            new[] { "発注書.xlsx", "仕入先台帳.xlsx", "単価表.pdf" }, 14.0),

        new("部署間共通", $@"{Root}\部署間共通",
            new[] { "kyodo", "shibata" },
            new[] { "01全社通達", "02申請様式", "03カレンダー" },
            new[] { "全社通達.pdf", "申請様式.docx", "年間予定.xlsx" }, 15.0),

        new("郵便局", $@"{Root}\郵便局",
            new[] { "postoffice", "yubin" },
            new[] { "01窓口記録", "02料金", "03受付簿" },
            new[] { "窓口記録.xlsx", "料金表.pdf", "受付簿.xlsx" }, 16.0),
    };

    private static List<AccessRow> Build()
    {
        var rows = new List<AccessRow>();
        long id = 1;

        void Add(double hour, SourceKind src, string user, string dept, string action, ActionKind kind,
                 string? file, string folder, bool ok = true, string? note = null)
        {
            People.TryGetValue(user, out var who);
            var time = Day.AddHours(hour);
            rows.Add(new AccessRow(id++, time, src, user, dept, action, kind,
                file is null ? null : $"{folder}\\{file}", folder,
                who.Pc, who.Ip, ok, note));
        }

        // ---- 🟦 青：各部署のビューアー経由（SFE SQLite）= 本人特定・確実 ----------------------
        // 各ユーザー3アクセスを決定的に生成（ファイル/フォルダ/時刻はインデックスで回す）。
        foreach (var s in Seeds)
        {
            int idx = 0;
            foreach (var user in s.Users)
            {
                for (int k = 0; k < 3; k++)
                {
                    var sub = s.Subs[idx % s.Subs.Length];
                    var file = s.Files[idx % s.Files.Length];
                    var folder = $@"{s.Base}\{sub}";
                    var action = k == 0 ? "検索" : (k == 2 ? "開く" : "閲覧");
                    var kind = k == 0 ? ActionKind.Search : ActionKind.Read;
                    var f = k == 0 ? null : file;
                    var note = k == 0 ? $"クエリ: \"{file.Split('.')[0]}\"" : null;
                    Add(s.BaseHour + idx * 0.17, SourceKind.Viewer, user, s.Dept, action, kind, f, folder, note: note);
                    idx++;
                }
            }
        }

        // ---- 🟥 赤：サーバー直接アクセス（audit 実ユーザー・is_content_read）= 要注目 ----------
        // ★目立つ事例：営業部 sasou が技術部フォルダを直接・大量(distinct 12)読取（部署外＋大量持ち出し）。
        const string GijutsuShiryo = $@"{Root}\技術部\機械設計\03技術資料";
        string[] bulk =
        {
            "図面A.dwg", "図面B.dwg", "図面C.dwg", "図面D.dwg", "図面E.dwg", "図面F.dwg",
            "部品表.xlsx", "仕様書.pdf", "回路図.dwg", "treatment.docx", "原価.xlsx", "金型.dwg",
        };
        for (int i = 0; i < bulk.Length; i++)
            Add(9.07 + i * 0.018, SourceKind.Direct, "sasou", "営業部", "読み取り", ActionKind.Read,
                bulk[i], GijutsuShiryo, note: i == 0 ? "短時間に distinct 多数を内容読取（部署外）" : null);
        Add(9.32, SourceKind.Direct, "sasou", "営業部", "コピー疑い", ActionKind.Copy,
            "(12ファイル連続読取)", GijutsuShiryo, note: "23分で distinct 12 ファイルを内容読取 — 大量持ち出し検知");

        // 営業部 oku が技術部フォルダへ直接アクセス → 権限なしで拒否（部署外アクセス・失敗行）。
        Add(9.46, SourceKind.Direct, "oku", "営業部", "読み取り", ActionKind.Read,
            "設計.xlsx", $@"{Root}\技術部\機械設計\09製品設計_標準設計書", ok: false, note: "アクセス拒否（部署外・権限なし）");

        // 営業部 nishida が自部署フォルダをビューアー未経由で直接読取（直接バイパス）。
        Add(10.02, SourceKind.Direct, "nishida", "営業部", "読み取り", ActionKind.Read,
            "顧客一覧.xlsx", $@"{Root}\営業部\02顧客台帳", note: "ビューアー未経由の直接読取");

        // 購買部 kato が自部署フォルダを直接読取。
        Add(14.1, SourceKind.Direct, "kato", "購買部", "読み取り", ActionKind.Read,
            "単価表.pdf", $@"{Root}\購買部\03単価");
        // 製造部 saito が直接読取。
        Add(13.4, SourceKind.Direct, "saito", "製造部", "読み取り", ActionKind.Read,
            "検査記録.xlsx", $@"{Root}\製造部\02検査記録");

        // ---- ⬜ 灰：未帰属（MTSV$ / サービス / NULL）= ビューアーの証明ではない -----------------
        Add(9.05, SourceKind.Unknown, @"LINEWORKS-MTSV$", "技術部", "読み取り", ActionKind.Read,
            "梁.xlsx", $@"{Root}\技術部\機械設計\06強度計算(LW標準)", note: "マシンアカウント（ビューアー代理の可能性／未帰属）");
        Add(9.34, SourceKind.Unknown, @"LINEWORKS-MTSV$", "技術部", "読み取り", ActionKind.Read,
            "作図.docx", $@"{Root}\技術部\機械設計\07作図標準書", note: "マシンアカウント（未帰属）");
        Add(10.1, SourceKind.Unknown, @"LINEWORKS-MTSV$", "営業部", "読み取り", ActionKind.Read,
            "提案.pptx", $@"{Root}\営業部\03提案資料", note: "マシンアカウント（未帰属）");
        Add(3.12, SourceKind.Unknown, @"LINEWORKS-NET\svc-dove", "技術部", "読み取り", ActionKind.Read,
            "梁.xlsx", $@"{Root}\技術部\機械設計\06強度計算(LW標準)", note: "バックアップサービス（除外候補）");
        Add(2.20, SourceKind.Unknown, @"LINEWORKS-NET\svc-dove", "製造部", "読み取り", ActionKind.Read,
            "工程表.xlsx", $@"{Root}\製造部\01工程表", note: "バックアップサービス（除外候補）");
        Add(7.45, SourceKind.Unknown, "(NULL)", "技術部", "読み取り", ActionKind.Read,
            "図面A.dwg", $@"{Root}\技術部\機械設計\03技術資料", note: "SID未解決／パース失敗（要確認）");

        return rows.OrderByDescending(r => r.Time).ToList();
    }
}
