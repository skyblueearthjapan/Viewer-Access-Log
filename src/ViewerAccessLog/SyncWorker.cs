namespace ViewerAccessLog;

/// <summary>
/// DataMode=Live のときだけ登録される BackgroundService。
/// SFE catalog.db (🟦 Viewer) と AuditLogger PostgreSQL (🟥 Direct / ⬜ Unknown) から
/// cache.db へ増分同期する。
///
/// 起動時:  LookbackDays 分遡って初期同期（既存 last_id が 0 の場合のみ）
/// 以降:    SyncIntervalSeconds 間隔で増分同期（id &gt; last_id）
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

        using var sfe   = new SfeSqliteSource(path);
        using var cache = CacheDb.Open(opts.CachePath);

        var lastId = CacheDb.GetLastId(cache, "viewer");
        int total  = 0;

        // バッチループ（catalog.db は軽いが念のため）
        while (true)
        {
            var rows = sfe.ReadViewerRows(lastId);
            if (rows.Count == 0) break;

            CacheDb.UpsertRows(cache, "viewer", rows.Select(x => (x.SrcId, x.Row)));
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

        using var pg    = new AuditPgReader(opts.AuditPg.ConnectionString, opts.AuditPg.Schema, opts.Dept);
        using var cache = CacheDb.Open(opts.CachePath);

        var since = isInitial
            ? DateTimeOffset.UtcNow.AddDays(-opts.LookbackDays)
            : (DateTimeOffset?)null;   // 増分は id のみで管理

        // 初回(lastId=0)は trigram索引を使う bulk 取得、以降は id 増分のバッチ取得。
        async Task<int> SyncStreamAsync(
            string key,
            Func<long, DateTimeOffset?, bool, Task<IReadOnlyList<(long SrcId, AccessRow Row)>>> read)
        {
            var lastId = CacheDb.GetLastId(cache, key);
            int total = 0;
            if (isInitial && lastId == 0)
            {
                var rows = await read(0, since, true);   // bulk
                if (rows.Count > 0)
                {
                    CacheDb.UpsertRows(cache, key, rows.Select(x => (x.SrcId, x.Row)));
                    CacheDb.SetLastId(cache, key, rows.Max(x => x.SrcId));
                    total = rows.Count;
                }
            }
            else
            {
                while (true)
                {
                    var rows = await read(lastId, null, false);   // incremental
                    if (rows.Count == 0) break;
                    CacheDb.UpsertRows(cache, key, rows.Select(x => (x.SrcId, x.Row)));
                    lastId = rows.Max(x => x.SrcId);
                    CacheDb.SetLastId(cache, key, lastId);
                    total += rows.Count;
                    if (rows.Count < 500) break;
                }
            }
            return total;
        }

        int directTotal  = await SyncStreamAsync("direct",  (l, s, b) => pg.ReadDirectRowsAsync(l, s, bulk: b, ct: ct));
        int unknownTotal = await SyncStreamAsync("unknown", (l, s, b) => pg.ReadUnknownRowsAsync(l, s, bulk: b, ct: ct));

        if (directTotal + unknownTotal > 0)
            logger.LogInformation("Audit sync: Direct+{D} Unknown+{U}", directTotal, unknownTotal);
    }
}
