namespace ViewerAccessLog;

/// <summary>
/// DataMode=Live のときだけ登録される BackgroundService。
/// SFE catalog.db (🟦 Viewer) と AuditLogger PostgreSQL (🟥 Direct / ⬜ Unknown) から
/// cache.db へ増分同期する。
///
/// 同期:    event_time の窓を now から過去へ索引で走査（初回は LookbackDays 遡る、
///          以降は前回同期した最大 event_time = last_time から重ねて）。SyncIntervalSeconds 間隔。
///          src_id 冪等 upsert。id 増分の正規表現 seq スキャン（タイムアウトで同期停止）を回避。
///
/// 書込は cache.db のみ。audit_logs・AccessLogs への書込は一切行わない。
/// </summary>
public sealed class SyncWorker(LiveOptions opts, ILogger<SyncWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation(
            "SyncWorker started. Dept={Dept}, Interval={Sec}s, LookbackDays={Days}",
            opts.Dept, opts.SyncIntervalSeconds, opts.LookbackDays);

        // 初回同期（起動直後）
        await RunSyncAsync(isInitial: true, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(opts.SyncIntervalSeconds), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            await RunSyncAsync(isInitial: false, stoppingToken);
        }

        logger.LogInformation("SyncWorker stopped.");
    }

    private async Task RunSyncAsync(bool isInitial, CancellationToken ct)
    {
        try
        {
            SyncViewer(isInitial);
            await SyncAuditAsync(isInitial, ct);
            logger.LogInformation("Sync done at {Now}", DateTimeOffset.UtcNow);
        }
        catch (OperationCanceledException)
        {
            // シャットダウン時は握りつぶす
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Sync failed");
        }
    }

    // ---- 🟦 Viewer: SFE catalog.db ----------------------------------------

    private void SyncViewer(bool isInitial)
    {
        var path = opts.SfeSqlitePath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            logger.LogWarning("SfeSqlitePath not found or not set: '{Path}' — skipping viewer sync", path);
            return;
        }

        // opts.Dept を渡してビューアー行の Dept を統一する（直接/未帰属と部署名を合算可能にする）。
        using var sfe   = new SfeSqliteSource(path, opts.Dept);
        using var cache = CacheDb.Open(opts.CachePath);

        var lastId = CacheDb.GetLastId(cache, "viewer");
        int total  = 0;

        // バッチループ（catalog.db は軽いが念のため）
        while (true)
        {
            var rows = sfe.ReadViewerRows(lastId);
            if (rows.Count == 0) break;

            CacheDb.UpsertRows(cache, "viewer", rows.Select(x => (x.SrcId, x.Row, 1)));
            lastId = rows.Max(x => x.SrcId);
            CacheDb.SetLastId(cache, "viewer", lastId);
            total += rows.Count;

            if (rows.Count < 1000) break;  // 最終バッチ（< batch = 全件読み切り）
        }

        if (total > 0)
            logger.LogInformation("Viewer sync: +{Count} rows (lastId={Id})", total, lastId);
    }

    // ---- 🟥 Direct / ⬜ Unknown: AuditLogger PostgreSQL --------------------

    private async Task SyncAuditAsync(bool isInitial, CancellationToken ct)
    {
        if (!opts.IsPgConfigured)
        {
            logger.LogWarning("AuditPg connection string not configured — skipping pg sync");
            return;
        }

        using var pg    = new AuditPgReader(opts.AuditPg.ConnectionString, opts.AuditPg.Schema);
        using var cache = CacheDb.Open(opts.CachePath);

        // 初回も増分も常に event_time の窓を now から過去へ刻んで索引で走査する。
        // 走査下限 floor = 前回同期した最大 event_time から 10 分重ねた点（無ければ LookbackDays 遡る）。
        // upsert は src_id 冪等なので重なり分の再取得は無害。これにより CJK 正規表現の
        // seq スキャン（id 増分方式のタイムアウト）を回避し、同期停止を防ぐ。
        async Task<int> SyncStreamAsync(
            string key,
            Func<long, DateTimeOffset?, DateTimeOffset?, bool, Task<IReadOnlyList<(long SrcId, AccessRow Row)>>> read)
        {
            var now      = DateTimeOffset.UtcNow;
            var window   = TimeSpan.FromHours(2);
            var lastId   = CacheDb.GetLastId(cache, key);
            var lastTime = CacheDb.GetLastTime(cache, key);

            var hardFloor = now.AddDays(-opts.LookbackDays);
            var floor = lastTime is { } lt ? lt - TimeSpan.FromMinutes(10) : hardFloor;
            if (floor < hardFloor) floor = hardFloor;

            long maxId = lastId;
            DateTimeOffset? maxTime = lastTime;
            int total = 0;
            var until = now;
            while (until > floor && !ct.IsCancellationRequested)
            {
                var from = until - window;
                if (from < floor) from = floor;
                var rows = await read(0, from, until, true);   // event_time 窓(索引)で取得
                if (rows.Count > 0)
                {
                    // バースト畳み込み: 同一ユーザーの連続アクセスで直前との間隔が
                    // BurstCollapseSeconds 以内の行は is_open=0 とする（最初だけ is_open=1）。
                    var threshold    = TimeSpan.FromSeconds(opts.BurstCollapseSeconds);
                    var burstLastTime = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
                    var tagged       = rows
                        .OrderBy(x => x.Row.User, StringComparer.OrdinalIgnoreCase)
                        .ThenBy(x => x.Row.Time)
                        .Select(x =>
                        {
                            var u = x.Row.User;
                            int isOpen = !burstLastTime.TryGetValue(u, out var prev)
                                         || (x.Row.Time - prev) > threshold ? 1 : 0;
                            burstLastTime[u] = x.Row.Time;
                            return (x.SrcId, x.Row, isOpen);
                        });
                    CacheDb.UpsertRows(cache, key, tagged);
                    var mId = rows.Max(x => x.SrcId);
                    if (mId > maxId) maxId = mId;
                    var mT = rows.Max(x => x.Row.Time);
                    if (maxTime is null || mT > maxTime) maxTime = mT;
                    total += rows.Count;
                }
                until = from;
            }
            CacheDb.SetSyncState(cache, key, maxId, maxTime ?? lastTime);
            return total;
        }

        int directTotal  = await SyncStreamAsync("direct",  (l, s, u, b) => pg.ReadDirectRowsAsync(l, s, bulk: b, until: u, ct: ct));
        int unknownTotal = await SyncStreamAsync("unknown", (l, s, u, b) => pg.ReadUnknownRowsAsync(l, s, bulk: b, until: u, ct: ct));

        if (directTotal + unknownTotal > 0)
            logger.LogInformation("Audit sync: Direct+{D} Unknown+{U}", directTotal, unknownTotal);
    }
}
