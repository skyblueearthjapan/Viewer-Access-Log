using System.Text.Json.Serialization;
using ViewerAccessLog;

var builder = WebApplication.CreateBuilder(args);

// JSON: プロパティ camelCase、enum も camelCase 文字列で返す（UIから扱いやすく）。
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.CamelCase));
});

// データ源。今は Sample。後で本番ソース（SFE SQLite + Audit PG + 同期キャッシュ）に差し替える。
var mode = builder.Configuration["DataMode"] ?? "Sample";
builder.Services.AddSingleton<ILogSource>(_ => mode switch
{
    // "Live" => new CacheSource(...),  // P3 で実装
    _ => new SampleLogSource(),
});
builder.Services.AddSingleton<LogService>();

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

static string[]? SplitCsv(string? s) =>
    string.IsNullOrWhiteSpace(s) ? null : s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

static DateTimeOffset? ParseDate(string? s) =>
    DateTimeOffset.TryParse(s, out var d) ? d : null;

// CSV フィールドのクォート処理（RFC 4180 準拠）。
static string CsvEsc(string? s)
{
    s ??= "";
    return s.Contains(',') || s.Contains('"') || s.Contains('\n') || s.Contains('\r')
        ? $"\"{s.Replace("\"", "\"\"")}\"" : s;
}

app.MapGet("/api/logs", (string? from, string? to, string? user, string? sources,
                         string? kinds, string? q, string? dept,
                         string? sort, string? dir,
                         int? page, int? pageSize, LogService svc) =>
{
    // dir=asc なら昇順、それ以外（desc / 未指定）は降順（既定）。
    bool desc = !string.Equals(dir, "asc", StringComparison.OrdinalIgnoreCase);
    var query = new LogQuery(ParseDate(from), ParseDate(to), user,
        SplitCsv(sources), SplitCsv(kinds), q, page ?? 1, pageSize ?? 50, dept, sort, desc);
    return Results.Ok(svc.Search(query));
});

// CSV エクスポート: 現在の検索条件で全件（上限 50000 行）を UTF-8 BOM 付き CSV で返す。
// Excel の文字化け回避のため BOM を先頭に付加する。
app.MapGet("/api/logs.csv", (string? from, string? to, string? user, string? sources,
                              string? kinds, string? q, string? dept,
                              string? sort, string? dir, LogService svc) =>
{
    bool desc = !string.Equals(dir, "asc", StringComparison.OrdinalIgnoreCase);
    var query = new LogQuery(ParseDate(from), ParseDate(to), user,
        SplitCsv(sources), SplitCsv(kinds), q, 1, 50000, dept, sort, desc);
    var rows = svc.SearchAll(query);

    var sb = new System.Text.StringBuilder();
    sb.AppendLine("日時(JST),ソース,部署,ユーザー,操作,ファイル,PC,IP,結果,メモ");
    var jst = TimeSpan.FromHours(9);
    foreach (var r in rows)
    {
        var t = r.Time.ToOffset(jst).ToString("yyyy-MM-dd HH:mm:ss");
        sb.AppendLine(string.Join(",", new[]
        {
            CsvEsc(t), CsvEsc(r.Source.ToString()), CsvEsc(r.Dept), CsvEsc(r.User),
            CsvEsc(r.Action), CsvEsc(r.File), CsvEsc(r.Pc), CsvEsc(r.Ip),
            CsvEsc(r.Success ? "OK" : "拒否"), CsvEsc(r.Note),
        }));
    }

    var bom = new byte[] { 0xEF, 0xBB, 0xBF };
    var body = System.Text.Encoding.UTF8.GetBytes(sb.ToString());
    var full = new byte[bom.Length + body.Length];
    bom.CopyTo(full, 0);
    body.CopyTo(full, bom.Length);
    return Results.File(full, "text/csv; charset=utf-8", "access-log.csv");
});

app.MapGet("/api/summary", (string? from, string? to, string? user, string? q, string? dept, LogService svc) =>
{
    var query = new LogQuery(ParseDate(from), ParseDate(to), user, null, null, q, Dept: dept);
    return Results.Ok(svc.Summarize(query));
});

app.MapGet("/api/dashboard", (string? from, string? to, string? dept, LogService svc) =>
{
    var query = new LogQuery(ParseDate(from), ParseDate(to), null, null, null, null, Dept: dept);
    return Results.Ok(svc.Dashboard(query));
});

app.MapGet("/api/users", (string? from, string? to, string? dept, LogService svc) =>
{
    var query = new LogQuery(ParseDate(from), ParseDate(to), null, null, null, null, Dept: dept);
    return Results.Ok(svc.Users(query));
});

app.MapGet("/api/user", (string? name, string? from, string? to, LogService svc) =>
{
    if (string.IsNullOrWhiteSpace(name)) return Results.BadRequest(new { error = "name is required" });
    var query = new LogQuery(ParseDate(from), ParseDate(to), null, null, null, null);
    var detail = svc.UserDetail(name, query);
    return detail is null ? Results.NotFound() : Results.Ok(detail);
});

app.MapGet("/api/alerts", (LogService svc) => Results.Ok(svc.Alerts()));
app.MapGet("/api/incidents", (LogService svc) => Results.Ok(svc.Incidents()));

app.MapGet("/api/health", (LogService svc) => Results.Ok(svc.Health()));
app.MapGet("/api/filters", (LogService svc) => Results.Ok(svc.Filters()));

// P4 設定一括取得（読み取りのみ。書込エンドポイントは P4 の限定書込ロールで実装予定）。
// サーバーへ書き込むエンドポイントは一切存在しない（クライアント側モックのみ）。
app.MapGet("/api/settings", (LogService svc) => Results.Ok(svc.Settings()));

var url = builder.Configuration["Urls"] ?? "http://localhost:5099";
app.Run(url);
