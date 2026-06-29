namespace ViewerAccessLog;

using Microsoft.Data.Sqlite;

/// <summary>
/// cache.db スキーマ管理ユーティリティ。
/// access_rows(source,src_id UNIQUE) + sync_state を保持し、
/// SyncWorker が UPSERT する際のコネクション生成とスキーマ確保を行う。
/// </summary>
internal static class CacheDb
{
    /// <summary>cache.db を開き（なければ作り）、スキーマを確保してコネクションを返す。</summary>
    public static SqliteConnection Open(string path)
    {
        var conn = new SqliteConnection($"Data Source={path}");
        conn.Open();
        EnsureSchema(conn);
        return conn;
    }

    public static void EnsureSchema(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;

            CREATE TABLE IF NOT EXISTS access_rows (
                id      INTEGER PRIMARY KEY AUTOINCREMENT,
                source  TEXT    NOT NULL,
                src_id  INTEGER NOT NULL,
                time    TEXT    NOT NULL,
                user    TEXT    NOT NULL,
                dept    TEXT    NOT NULL,
                action  TEXT    NOT NULL,
                kind    TEXT    NOT NULL,
                file    TEXT,
                folder  TEXT,
                pc      TEXT,
                ip      TEXT,
                success INTEGER NOT NULL DEFAULT 1,
                note    TEXT,
                UNIQUE(source, src_id)
            );

            CREATE INDEX IF NOT EXISTS idx_ar_time   ON access_rows(time);
            CREATE INDEX IF NOT EXISTS idx_ar_user   ON access_rows(user);
            CREATE INDEX IF NOT EXISTS idx_ar_source ON access_rows(source);

            CREATE TABLE IF NOT EXISTS sync_state (
                source    TEXT PRIMARY KEY,
                last_id   INTEGER NOT NULL DEFAULT 0,
                last_time TEXT
            );
            """;
        cmd.ExecuteNonQuery();
    }

    /// <summary>指定ソースの last_id を取得（未登録なら 0）。</summary>
    public static long GetLastId(SqliteConnection conn, string source)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT last_id FROM sync_state WHERE source = $s";
        cmd.Parameters.AddWithValue("$s", source);
        var val = cmd.ExecuteScalar();
        return val is long l ? l : (val is not null && val != DBNull.Value ? Convert.ToInt64(val) : 0L);
    }

    /// <summary>指定ソースの last_id を更新（INSERT OR REPLACE）。</summary>
    public static void SetLastId(SqliteConnection conn, string source, long id)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO sync_state(source, last_id, last_time)
            VALUES($s, $id, $t)
            ON CONFLICT(source) DO UPDATE
            SET last_id = excluded.last_id, last_time = excluded.last_time
            """;
        cmd.Parameters.AddWithValue("$s",  source);
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$t",  DateTimeOffset.UtcNow.ToString("o"));
        cmd.ExecuteNonQuery();
    }

    /// <summary>指定ソースの last_time（最後に同期した最大 event_time）を取得（未登録/NULL なら null）。</summary>
    public static DateTimeOffset? GetLastTime(SqliteConnection conn, string source)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT last_time FROM sync_state WHERE source = $s";
        cmd.Parameters.AddWithValue("$s", source);
        var v = cmd.ExecuteScalar();
        return v is string s &&
               DateTimeOffset.TryParse(s, System.Globalization.CultureInfo.InvariantCulture,
                   System.Globalization.DateTimeStyles.RoundtripKind, out var d)
            ? d : null;
    }

    /// <summary>last_id と last_time（= 同期した最大 event_time）をまとめて更新する。</summary>
    public static void SetSyncState(SqliteConnection conn, string source, long lastId, DateTimeOffset? lastTime)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO sync_state(source, last_id, last_time)
            VALUES($s, $id, $t)
            ON CONFLICT(source) DO UPDATE
            SET last_id = excluded.last_id, last_time = excluded.last_time
            """;
        cmd.Parameters.AddWithValue("$s",  source);
        cmd.Parameters.AddWithValue("$id", lastId);
        cmd.Parameters.AddWithValue("$t",  (object?)lastTime?.ToString("o") ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// バッチ行を access_rows へ upsert する。
    /// ON CONFLICT(source,src_id) DO NOTHING = 既存行は上書きしない（冪等）。
    /// </summary>
    public static void UpsertRows(SqliteConnection conn, string source,
        IEnumerable<(long SrcId, AccessRow Row)> rows)
    {
        using var tx  = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO access_rows(source,src_id,time,user,dept,action,kind,file,folder,pc,ip,success,note)
            VALUES($src,$sid,$t,$u,$dept,$act,$kind,$f,$dir,$pc,$ip,$ok,$note)
            ON CONFLICT(source,src_id) DO NOTHING
            """;

        // パラメータを一度だけ追加し、ループ内で値だけ差し替えてバッチ実行する。
        var pSrc  = cmd.Parameters.Add("$src",  SqliteType.Text);
        var pSid  = cmd.Parameters.Add("$sid",  SqliteType.Integer);
        var pT    = cmd.Parameters.Add("$t",    SqliteType.Text);
        var pU    = cmd.Parameters.Add("$u",    SqliteType.Text);
        var pDept = cmd.Parameters.Add("$dept", SqliteType.Text);
        var pAct  = cmd.Parameters.Add("$act",  SqliteType.Text);
        var pKind = cmd.Parameters.Add("$kind", SqliteType.Text);
        var pF    = cmd.Parameters.Add("$f",    SqliteType.Text);
        var pDir  = cmd.Parameters.Add("$dir",  SqliteType.Text);
        var pPc   = cmd.Parameters.Add("$pc",   SqliteType.Text);
        var pIp   = cmd.Parameters.Add("$ip",   SqliteType.Text);
        var pOk   = cmd.Parameters.Add("$ok",   SqliteType.Integer);
        var pNote = cmd.Parameters.Add("$note", SqliteType.Text);

        foreach (var (sid, row) in rows)
        {
            pSrc.Value  = source;
            pSid.Value  = sid;
            pT.Value    = row.Time.ToString("o");
            pU.Value    = row.User;
            pDept.Value = row.Dept;
            pAct.Value  = row.Action;
            pKind.Value = row.Kind.ToString().ToLowerInvariant();
            pF.Value    = (object?)row.File   ?? DBNull.Value;
            pDir.Value  = (object?)row.Folder ?? DBNull.Value;
            pPc.Value   = (object?)row.Pc     ?? DBNull.Value;
            pIp.Value   = (object?)row.Ip     ?? DBNull.Value;
            pOk.Value   = row.Success ? 1 : 0;
            pNote.Value = (object?)row.Note   ?? DBNull.Value;
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
    }
}
