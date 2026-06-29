namespace ViewerAccessLog;

using System.Text.Json;
using Npgsql;

/// <summary>
/// P4a: 設定テーブルへの書込を行うクラス（config_editor ロール専用接続）。
///
/// 書込対象は app_settings と detection_exclusions のみ。
/// ログ本体（audit_logs 等）へは一切書き込まない。
///
/// 全操作は 1 トランザクション内で以下を行う:
///   1. 旧値を SELECT（監査ログの old_values 用）
///   2. 設定テーブルを INSERT/UPDATE/DELETE
///   3. config_audit_log に INSERT（誰が・いつ・何を・旧値/新値）
/// 例外時はトランザクションをロールバックし、例外を再スローする。
/// </summary>
public sealed class ConfigWriter : IDisposable
{
    private readonly NpgsqlDataSource _ds;
    private readonly string           _schema;

    public ConfigWriter(string connStr, string schema)
    {
        _ds     = NpgsqlDataSource.Create(connStr);
        _schema = schema;
    }

    // ================================================================
    //  app_settings
    // ================================================================

    /// <summary>
    /// app_settings の key を upsert する。
    /// 旧値を SELECT してから upsert し、config_audit_log に記録する。
    /// </summary>
    public async Task UpsertAppSettingAsync(string key, string value, string op,
        CancellationToken ct = default)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var tx   = await conn.BeginTransactionAsync(ct);
        try
        {
            // 旧値取得
            var oldValue = await ScalarAsync<string?>(conn, tx, ct,
                $"SELECT value FROM {_schema}.app_settings WHERE key = @k",
                ("@k", key));

            // Upsert
            await ExecAsync(conn, tx, ct,
                $"""
                INSERT INTO {_schema}.app_settings(key, value, updated_at)
                VALUES(@k, @v, now())
                ON CONFLICT(key) DO UPDATE
                SET value = @v, updated_at = now()
                """,
                ("@k", (object)key), ("@v", value));

            // 監査
            await AuditAsync(conn, tx, ct, op, "app_settings", "UPSERT", key,
                oldValue is null ? null : Obj(new { value = oldValue }),
                Obj(new { value }));

            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    // ================================================================
    //  detection_exclusions
    // ================================================================

    /// <summary>
    /// detection_exclusions に新規行を INSERT し、新しい Id を返す。
    /// </summary>
    public async Task<int> InsertExclusionAsync(DetectionExclusion e, string op,
        CancellationToken ct = default)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var tx   = await conn.BeginTransactionAsync(ct);
        try
        {
            var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = $"""
                INSERT INTO {_schema}.detection_exclusions
                    (enabled, user_pattern, process_pattern, path_regex, reason)
                VALUES (true, @u, @p, @path, @r)
                RETURNING id
                """;
            cmd.Parameters.AddWithValue("@u",    e.User);
            cmd.Parameters.AddWithValue("@p",    (object?)e.Process ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@path", (object?)e.Path    ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@r",    e.Reason);
            var newId = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));

            await AuditAsync(conn, tx, ct, op, "detection_exclusions", "INSERT",
                newId.ToString(), null,
                Obj(new { id = newId, user = e.User, process = e.Process, path = e.Path, reason = e.Reason }));

            await tx.CommitAsync(ct);
            return newId;
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    /// <summary>
    /// detection_exclusions の id 行を UPDATE する。行が存在しない場合は例外を投げる。
    /// </summary>
    public async Task UpdateExclusionAsync(int id, DetectionExclusion e, string op,
        CancellationToken ct = default)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var tx   = await conn.BeginTransactionAsync(ct);
        try
        {
            // 旧値取得
            var old = await ReadExclusionAsync(conn, tx, ct, id)
                      ?? throw new InvalidOperationException($"exclusion id={id} not found");

            await ExecAsync(conn, tx, ct,
                $"""
                UPDATE {_schema}.detection_exclusions
                SET user_pattern=@u, process_pattern=@p, path_regex=@path, reason=@r
                WHERE id = @id
                """,
                ("@id", (object)id), ("@u", e.User),
                ("@p", (object?)e.Process ?? DBNull.Value),
                ("@path", (object?)e.Path ?? DBNull.Value),
                ("@r", e.Reason));

            await AuditAsync(conn, tx, ct, op, "detection_exclusions", "UPDATE",
                id.ToString(),
                Obj(new { id = old.Id, user = old.User, process = old.Process, path = old.Path, reason = old.Reason }),
                Obj(new { id, user = e.User, process = e.Process, path = e.Path, reason = e.Reason }));

            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    /// <summary>
    /// detection_exclusions の id 行を DELETE する。行が存在しない場合は例外を投げる。
    /// </summary>
    public async Task DeleteExclusionAsync(int id, string op, CancellationToken ct = default)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var tx   = await conn.BeginTransactionAsync(ct);
        try
        {
            var old = await ReadExclusionAsync(conn, tx, ct, id)
                      ?? throw new InvalidOperationException($"exclusion id={id} not found");

            var rows = await ExecAsync(conn, tx, ct,
                $"DELETE FROM {_schema}.detection_exclusions WHERE id = @id",
                ("@id", (object)id));

            if (rows == 0) throw new InvalidOperationException($"exclusion id={id} not deleted");

            await AuditAsync(conn, tx, ct, op, "detection_exclusions", "DELETE",
                id.ToString(),
                Obj(new { id = old.Id, user = old.User, process = old.Process, path = old.Path, reason = old.Reason }),
                null);

            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    // ================================================================
    //  内部ヘルパー
    // ================================================================

    private async Task<DetectionExclusion?> ReadExclusionAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, CancellationToken ct, int id)
    {
        var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"""
            SELECT id, user_pattern, process_pattern, path_regex, reason
            FROM {_schema}.detection_exclusions
            WHERE id = @id
            """;
        cmd.Parameters.AddWithValue("@id", id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return new DetectionExclusion(
            Id:      r.GetInt32(0),
            User:    r.GetString(1),
            Process: r.IsDBNull(2) ? null : r.GetString(2),
            Path:    r.IsDBNull(3) ? null : r.GetString(3),
            Reason:  r.IsDBNull(4) ? ""   : r.GetString(4));
    }

    private async Task AuditAsync(NpgsqlConnection conn, NpgsqlTransaction tx, CancellationToken ct,
        string userName, string tableName, string action, string recordKey,
        string? oldJson, string? newJson)
    {
        var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"""
            INSERT INTO {_schema}.config_audit_log
                (changed_at, user_name, table_name, action, record_key, old_values, new_values)
            VALUES (now(), @op, @tbl, @act, @key, @old::jsonb, @new::jsonb)
            """;
        cmd.Parameters.AddWithValue("@op",  userName);
        cmd.Parameters.AddWithValue("@tbl", tableName);
        cmd.Parameters.AddWithValue("@act", action);
        cmd.Parameters.AddWithValue("@key", recordKey);
        cmd.Parameters.AddWithValue("@old", (object?)oldJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@new", (object?)newJson ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<int> ExecAsync(NpgsqlConnection conn, NpgsqlTransaction tx,
        CancellationToken ct, string sql, params (string Name, object Value)[] prms)
    {
        var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (n, v) in prms) cmd.Parameters.AddWithValue(n, v);
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<T?> ScalarAsync<T>(NpgsqlConnection conn, NpgsqlTransaction tx,
        CancellationToken ct, string sql, params (string Name, object Value)[] prms)
    {
        var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (n, v) in prms) cmd.Parameters.AddWithValue(n, v);
        var val = await cmd.ExecuteScalarAsync(ct);
        return val is T t ? t : default;
    }

    /// <summary>匿名オブジェクトを JSON 文字列に変換する（audit の old/new values 用）。</summary>
    private static string Obj(object value) => JsonSerializer.Serialize(value);

    public void Dispose() => _ds.Dispose();
}
