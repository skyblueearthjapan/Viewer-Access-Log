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

app.MapGet("/api/logs", (string? from, string? to, string? user, string? sources,
                         string? kinds, string? q, int? page, int? pageSize, LogService svc) =>
{
    var query = new LogQuery(ParseDate(from), ParseDate(to), user,
        SplitCsv(sources), SplitCsv(kinds), q, page ?? 1, pageSize ?? 50);
    return Results.Ok(svc.Search(query));
});

app.MapGet("/api/summary", (string? from, string? to, string? user, string? q, LogService svc) =>
{
    var query = new LogQuery(ParseDate(from), ParseDate(to), user, null, null, q);
    return Results.Ok(svc.Summarize(query));
});

app.MapGet("/api/health", (LogService svc) => Results.Ok(svc.Health()));
app.MapGet("/api/filters", (LogService svc) => Results.Ok(svc.Filters()));

var url = builder.Configuration["Urls"] ?? "http://localhost:5099";
app.Run(url);
