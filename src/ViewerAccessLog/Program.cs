using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication.Negotiate;
using ViewerAccessLog;

var builder = WebApplication.CreateBuilder(args);

// Windowsサービスとして起動された場合に正しく動作する（コンソール起動時は無害な no-op）。
builder.Host.UseWindowsService();

// JSON: プロパティ camelCase、enum も camelCase 文字列で返す（UIから扱いやすく）。
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.CamelCase));
});

// P4a: Windows 認証（Negotiate/Kerberos/NTLM）で操作者を特定する。
// 読み取り系エンドポイントは匿名可のまま変更しない。
// 書込エンドポイントのみ .RequireAuthorization() を付与する。
builder.Services.AddAuthentication(NegotiateDefaults.AuthenticationScheme).AddNegotiate();
builder.Services.AddAuthorization();

// データ源。DataMode=Sample（既定）または DataMode=Live を切り替える。
// Live: appsettings.json "Live" セクションを LiveOptions にバインドして CacheLogSource を使用。
//       SyncWorker (BackgroundService) が SFE catalog.db + AuditLogger PostgreSQL から
//       cache.db へ増分同期する。サーバーへの書込エンドポイントは存在しない。
var mode = builder.Configuration["DataMode"] ?? "Sample";

if (string.Equals(mode, "Live", StringComparison.OrdinalIgnoreCase))
{
    var liveOpts = builder.Configuration.GetSection("Live").Get<LiveOptions>() ?? new LiveOptions();
    builder.Services.AddSingleton(liveOpts);
    builder.Services.AddSingleton<ILogSource, CacheLogSource>();
    builder.Services.AddHostedService<SyncWorker>();

    // P4a: config_editor ロール書込基盤。未設定時は書込 API が 503 を返す（接続は作らない）。
    if (liveOpts.IsConfigWriteEnabled)
        builder.Services.AddSingleton(new ConfigWriter(
            liveOpts.ConfigPg.ConnectionString, liveOpts.ConfigPg.Schema));
}
else
{
    builder.Services.AddSingleton<ILogSource>(_ => new SampleLogSource());
}

builder.Services.AddSingleton<LogService>();

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

// P4a: 認証/認可ミドルウェア（静的ファイルの後ろに置く）。
app.UseAuthentication();
app.UseAuthorization();

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

// P4 設定一括取得（読み取りのみ。ログ本体は常に読み取り専用堅持）。
app.MapGet("/api/settings", (LogService svc) => Results.Ok(svc.Settings()));

// ====================================================================
// P4a 書込エンドポイント（全て .RequireAuthorization()）
// 書込は設定テーブル(app_settings / detection_exclusions)のみ。
// config_editor ロール専用接続 ConfigWriter 経由。ログ本体には一切書かない。
// ConfigWriter 未設定（ConfigPg プレースホルダー残存）の場合は 503 を返す。
// ====================================================================

// 書込 API 共通ヘルパー：Windows 認証で特定した操作者名を取得する。
static string Op(HttpContext c) => c.User?.Identity?.Name ?? "(unknown)";

// ConfigWriter が DI に登録されているか確認し、なければ null（→503 を返す）。
static ConfigWriter? GetWriter(HttpContext ctx) =>
    ctx.RequestServices.GetService<ConfigWriter>();

// ---- app_settings ----------------------------------------------------

// PUT /api/appsettings/{key}
// body: { "value": "新しい値" }
// 成功: 200 { key, value }　失敗: 503 / 500
app.MapPut("/api/appsettings/{key}", async (string key, AppSettingUpdate body, HttpContext ctx) =>
{
    var writer = GetWriter(ctx);
    if (writer is null)
        return Results.Problem("config write not enabled — ConfigPg not configured", statusCode: 503);
    try
    {
        await writer.UpsertAppSettingAsync(key, body.Value, Op(ctx));
        return Results.Ok(new { key, value = body.Value });
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message, statusCode: 500);
    }
}).RequireAuthorization();

// ---- detection_exclusions --------------------------------------------

// POST /api/exclusions
// body: DetectionExclusion (Id は無視し RETURNING id を使用)
// 成功: 201 + 作成オブジェクト（新 Id 含む）
app.MapPost("/api/exclusions", async (DetectionExclusion body, HttpContext ctx) =>
{
    var writer = GetWriter(ctx);
    if (writer is null)
        return Results.Problem("config write not enabled — ConfigPg not configured", statusCode: 503);
    try
    {
        var newId   = await writer.InsertExclusionAsync(body, Op(ctx));
        var created = body with { Id = newId };
        return Results.Created($"/api/exclusions/{newId}", created);
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message, statusCode: 500);
    }
}).RequireAuthorization();

// PUT /api/exclusions/{id}
// body: DetectionExclusion (Id はパスパラメータを優先)
// 成功: 200 + 更新後オブジェクト　行なし: 500
app.MapPut("/api/exclusions/{id:int}", async (int id, DetectionExclusion body, HttpContext ctx) =>
{
    var writer = GetWriter(ctx);
    if (writer is null)
        return Results.Problem("config write not enabled — ConfigPg not configured", statusCode: 503);
    try
    {
        await writer.UpdateExclusionAsync(id, body, Op(ctx));
        return Results.Ok(body with { Id = id });
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message, statusCode: 500);
    }
}).RequireAuthorization();

// DELETE /api/exclusions/{id}
// 成功: 204　行なし: 500
app.MapDelete("/api/exclusions/{id:int}", async (int id, HttpContext ctx) =>
{
    var writer = GetWriter(ctx);
    if (writer is null)
        return Results.Problem("config write not enabled — ConfigPg not configured", statusCode: 503);
    try
    {
        await writer.DeleteExclusionAsync(id, Op(ctx));
        return Results.NoContent();
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message, statusCode: 500);
    }
}).RequireAuthorization();

// ====================================================================
// P4b 書込エンドポイント（全て .RequireAuthorization()）
// alert_histories / detected_incidents は status 列のみ変更。他列は一切触れない。
// ====================================================================

// ---- monitored_folders -----------------------------------------------

app.MapPost("/api/folders", async (MonitoredFolder body, HttpContext ctx) =>
{
    var writer = GetWriter(ctx);
    if (writer is null)
        return Results.Problem("config write not enabled — ConfigPg not configured", statusCode: 503);
    try
    {
        var newId   = await writer.InsertFolderAsync(body, Op(ctx));
        var created = body with { Id = newId };
        return Results.Created($"/api/folders/{newId}", created);
    }
    catch (Exception ex) { return Results.Problem(ex.Message, statusCode: 500); }
}).RequireAuthorization();

app.MapPut("/api/folders/{id:int}", async (int id, MonitoredFolder body, HttpContext ctx) =>
{
    var writer = GetWriter(ctx);
    if (writer is null)
        return Results.Problem("config write not enabled — ConfigPg not configured", statusCode: 503);
    try
    {
        await writer.UpdateFolderAsync(id, body, Op(ctx));
        return Results.Ok(body with { Id = id });
    }
    catch (Exception ex) { return Results.Problem(ex.Message, statusCode: 500); }
}).RequireAuthorization();

app.MapDelete("/api/folders/{id:int}", async (int id, HttpContext ctx) =>
{
    var writer = GetWriter(ctx);
    if (writer is null)
        return Results.Problem("config write not enabled — ConfigPg not configured", statusCode: 503);
    try
    {
        await writer.DeleteFolderAsync(id, Op(ctx));
        return Results.NoContent();
    }
    catch (Exception ex) { return Results.Problem(ex.Message, statusCode: 500); }
}).RequireAuthorization();

// ---- users -----------------------------------------------------------

app.MapPost("/api/users", async (UserConfig body, HttpContext ctx) =>
{
    var writer = GetWriter(ctx);
    if (writer is null)
        return Results.Problem("config write not enabled — ConfigPg not configured", statusCode: 503);
    try
    {
        var newId   = await writer.InsertUserAsync(body, Op(ctx));
        var created = body with { Id = newId };
        return Results.Created($"/api/users/{newId}", created);
    }
    catch (Exception ex) { return Results.Problem(ex.Message, statusCode: 500); }
}).RequireAuthorization();

app.MapPut("/api/users/{id:int}", async (int id, UserConfig body, HttpContext ctx) =>
{
    var writer = GetWriter(ctx);
    if (writer is null)
        return Results.Problem("config write not enabled — ConfigPg not configured", statusCode: 503);
    try
    {
        await writer.UpdateUserAsync(id, body, Op(ctx));
        return Results.Ok(body with { Id = id });
    }
    catch (Exception ex) { return Results.Problem(ex.Message, statusCode: 500); }
}).RequireAuthorization();

app.MapDelete("/api/users/{id:int}", async (int id, HttpContext ctx) =>
{
    var writer = GetWriter(ctx);
    if (writer is null)
        return Results.Problem("config write not enabled — ConfigPg not configured", statusCode: 503);
    try
    {
        await writer.DeleteUserAsync(id, Op(ctx));
        return Results.NoContent();
    }
    catch (Exception ex) { return Results.Problem(ex.Message, statusCode: 500); }
}).RequireAuthorization();

// ---- alert_rules -----------------------------------------------------

app.MapPost("/api/rules", async (AlertRule body, HttpContext ctx) =>
{
    var writer = GetWriter(ctx);
    if (writer is null)
        return Results.Problem("config write not enabled — ConfigPg not configured", statusCode: 503);
    try
    {
        var newId   = await writer.InsertRuleAsync(body, Op(ctx));
        var created = body with { Id = newId };
        return Results.Created($"/api/rules/{newId}", created);
    }
    catch (Exception ex) { return Results.Problem(ex.Message, statusCode: 500); }
}).RequireAuthorization();

app.MapPut("/api/rules/{id:int}", async (int id, AlertRule body, HttpContext ctx) =>
{
    var writer = GetWriter(ctx);
    if (writer is null)
        return Results.Problem("config write not enabled — ConfigPg not configured", statusCode: 503);
    try
    {
        await writer.UpdateRuleAsync(id, body, Op(ctx));
        return Results.Ok(body with { Id = id });
    }
    catch (Exception ex) { return Results.Problem(ex.Message, statusCode: 500); }
}).RequireAuthorization();

app.MapDelete("/api/rules/{id:int}", async (int id, HttpContext ctx) =>
{
    var writer = GetWriter(ctx);
    if (writer is null)
        return Results.Problem("config write not enabled — ConfigPg not configured", statusCode: 503);
    try
    {
        await writer.DeleteRuleAsync(id, Op(ctx));
        return Results.NoContent();
    }
    catch (Exception ex) { return Results.Problem(ex.Message, statusCode: 500); }
}).RequireAuthorization();

// ---- common_folders（PK は folder_top テキスト。id は使わない）---------

app.MapPost("/api/commonfolders", async (CommonFolder body, HttpContext ctx) =>
{
    var writer = GetWriter(ctx);
    if (writer is null)
        return Results.Problem("config write not enabled — ConfigPg not configured", statusCode: 503);
    try
    {
        await writer.UpsertCommonFolderAsync(body, Op(ctx));
        return Results.Created($"/api/commonfolders", body);
    }
    catch (Exception ex) { return Results.Problem(ex.Message, statusCode: 500); }
}).RequireAuthorization();

app.MapPut("/api/commonfolders", async (CommonFolder body, HttpContext ctx) =>
{
    var writer = GetWriter(ctx);
    if (writer is null)
        return Results.Problem("config write not enabled — ConfigPg not configured", statusCode: 503);
    try
    {
        await writer.UpsertCommonFolderAsync(body, Op(ctx));
        return Results.Ok(body);
    }
    catch (Exception ex) { return Results.Problem(ex.Message, statusCode: 500); }
}).RequireAuthorization();

app.MapDelete("/api/commonfolders", async (string path, HttpContext ctx) =>
{
    var writer = GetWriter(ctx);
    if (writer is null)
        return Results.Problem("config write not enabled — ConfigPg not configured", statusCode: 503);
    try
    {
        await writer.DeleteCommonFolderAsync(path, Op(ctx));
        return Results.NoContent();
    }
    catch (Exception ex) { return Results.Problem(ex.Message, statusCode: 500); }
}).RequireAuthorization();

// ---- user_folder_grants ---------------------------------------------

app.MapPost("/api/usergrants", async (UserFolderGrant body, HttpContext ctx) =>
{
    var writer = GetWriter(ctx);
    if (writer is null)
        return Results.Problem("config write not enabled — ConfigPg not configured", statusCode: 503);
    try
    {
        var newId   = await writer.InsertGrantAsync(body, Op(ctx));
        var created = body with { Id = newId };
        return Results.Created($"/api/usergrants/{newId}", created);
    }
    catch (Exception ex) { return Results.Problem(ex.Message, statusCode: 500); }
}).RequireAuthorization();

app.MapPut("/api/usergrants/{id:int}", async (int id, UserFolderGrant body, HttpContext ctx) =>
{
    var writer = GetWriter(ctx);
    if (writer is null)
        return Results.Problem("config write not enabled — ConfigPg not configured", statusCode: 503);
    try
    {
        await writer.UpdateGrantAsync(id, body, Op(ctx));
        return Results.Ok(body with { Id = id });
    }
    catch (Exception ex) { return Results.Problem(ex.Message, statusCode: 500); }
}).RequireAuthorization();

app.MapDelete("/api/usergrants/{id:int}", async (int id, HttpContext ctx) =>
{
    var writer = GetWriter(ctx);
    if (writer is null)
        return Results.Problem("config write not enabled — ConfigPg not configured", statusCode: 503);
    try
    {
        await writer.DeleteGrantAsync(id, Op(ctx));
        return Results.NoContent();
    }
    catch (Exception ex) { return Results.Problem(ex.Message, statusCode: 500); }
}).RequireAuthorization();

// ---- alert_histories / detected_incidents — status のみ変更 ----------

// PATCH /api/alerts/{id}/status
// body: { "status": "ack" | "closed" }
// alert_histories の status/acked_by/acked_at 列のみ更新。他列は変更しない。
app.MapMethods("/api/alerts/{id:long}/status", ["PATCH"],
    async (long id, AlertStatusUpdate body, HttpContext ctx) =>
{
    var writer = GetWriter(ctx);
    if (writer is null)
        return Results.Problem("config write not enabled — ConfigPg not configured", statusCode: 503);
    try
    {
        await writer.UpdateAlertStatusAsync(id, body.Status, Op(ctx));
        return Results.Ok(new { id, status = body.Status });
    }
    catch (Exception ex) { return Results.Problem(ex.Message, statusCode: 500); }
}).RequireAuthorization();

// PATCH /api/incidents/{id}/status
// body: { "status": "ack" | "closed" }
// detected_incidents の status 列のみ更新。他列は変更しない。
app.MapMethods("/api/incidents/{id:long}/status", ["PATCH"],
    async (long id, IncidentStatusUpdate body, HttpContext ctx) =>
{
    var writer = GetWriter(ctx);
    if (writer is null)
        return Results.Problem("config write not enabled — ConfigPg not configured", statusCode: 503);
    try
    {
        await writer.UpdateIncidentStatusAsync(id, body.Status, Op(ctx));
        return Results.Ok(new { id, status = body.Status });
    }
    catch (Exception ex) { return Results.Problem(ex.Message, statusCode: 500); }
}).RequireAuthorization();

var url = builder.Configuration["Urls"] ?? "http://localhost:5099";
app.Run(url);
