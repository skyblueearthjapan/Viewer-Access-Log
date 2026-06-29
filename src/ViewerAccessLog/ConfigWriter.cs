namespace ViewerAccessLog;

using System.Text.Json;
using Npgsql;

/// <summary>
/// P4a/P4b: 設定テーブルへの書込を行うクラス（config_editor ロール専用接続）。
///
/// 書込対象: app_settings, detection_exclusions, monitored_folders, users,
///           alert_rules, common_folders, user_folder_grants
///           + alert_histories / detected_incidents の status 列のみ。
///
/// ログ本体（audit_logs / raw_audit_logs 等）へは一切書き込まない。
///
/// 全操作は 1 トランザクション内で:
///   1. 旧値 SELECT（監査ログ old_values 用）
///   2. 設定テーブルを INSERT/UPDATE/DELETE
///   3. config_audit_log に INSERT（誰が・いつ・何を・旧値/新値）
/// 例外時はトランザクションをロールバックし再スローする。
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

    public async Task UpsertAppSettingAsync(string key, string value, string op,
        CancellationToken ct = default)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var tx   = await conn.BeginTransactionAsync(ct);
        try
        {
            var oldValue = await ScalarAsync<string?>(conn, tx, ct,
                $"SELECT value FROM {_schema}.app_settings WHERE key = @k",
                ("@k", key));

            await ExecAsync(conn, tx, ct,
                $"""
                INSERT INTO {_schema}.app_settings(key, value, updated_at)
                VALUES(@k, @v, now())
                ON CONFLICT(key) DO UPDATE
                SET value = @v, updated_at = now()
                """,
                ("@k", (object)key), ("@v", value));

            await AuditAsync(conn, tx, ct, op, "app_settings", "UPSERT", key,
                oldValue is null ? null : Obj(new { value = oldValue }),
                Obj(new { value }));

            await tx.CommitAsync(ct);
        }
        catch { await tx.RollbackAsync(ct); throw; }
    }

    // ================================================================
    //  detection_exclusions
    // ================================================================

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
        catch { await tx.RollbackAsync(ct); throw; }
    }

    public async Task UpdateExclusionAsync(int id, DetectionExclusion e, string op,
        CancellationToken ct = default)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var tx   = await conn.BeginTransactionAsync(ct);
        try
        {
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
        catch { await tx.RollbackAsync(ct); throw; }
    }

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
        catch { await tx.RollbackAsync(ct); throw; }
    }

    // ================================================================
    //  monitored_folders
    //  確定カラム: server_name, folder_path, importance(::audit.importance_level),
    //             monitor_read, monitor_write, monitor_delete, enabled
    //  ※ note 列は触らない
    // ================================================================

    public async Task<int> InsertFolderAsync(MonitoredFolder f, string op,
        CancellationToken ct = default)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var tx   = await conn.BeginTransactionAsync(ct);
        try
        {
            var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = $"""
                INSERT INTO {_schema}.monitored_folders
                    (server_name, folder_path, importance, monitor_read, monitor_write, monitor_delete, enabled)
                VALUES(@srv, @fp, @imp::{_schema}.importance_level, @mr, @mw, @md, @en)
                RETURNING id
                """;
            cmd.Parameters.AddWithValue("@srv", f.Server);
            cmd.Parameters.AddWithValue("@fp",  f.Path);
            cmd.Parameters.AddWithValue("@imp", f.Importance);
            cmd.Parameters.AddWithValue("@mr",  f.ReadEnabled);
            cmd.Parameters.AddWithValue("@mw",  f.WriteEnabled);
            cmd.Parameters.AddWithValue("@md",  f.DeleteEnabled);
            cmd.Parameters.AddWithValue("@en",  f.Enabled);
            var newId = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));

            await AuditAsync(conn, tx, ct, op, "monitored_folders", "INSERT",
                newId.ToString(), null,
                Obj(new { id = newId, server = f.Server, path = f.Path, importance = f.Importance,
                    readEnabled = f.ReadEnabled, writeEnabled = f.WriteEnabled,
                    deleteEnabled = f.DeleteEnabled, enabled = f.Enabled }));

            await tx.CommitAsync(ct);
            return newId;
        }
        catch { await tx.RollbackAsync(ct); throw; }
    }

    public async Task UpdateFolderAsync(int id, MonitoredFolder f, string op,
        CancellationToken ct = default)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var tx   = await conn.BeginTransactionAsync(ct);
        try
        {
            var old = await ReadFolderAsync(conn, tx, ct, id)
                      ?? throw new InvalidOperationException($"folder id={id} not found");

            await ExecAsync(conn, tx, ct,
                $"""
                UPDATE {_schema}.monitored_folders
                SET server_name=@srv, folder_path=@fp, importance=@imp::{_schema}.importance_level,
                    monitor_read=@mr, monitor_write=@mw, monitor_delete=@md, enabled=@en
                WHERE id = @id
                """,
                ("@id",  (object)id), ("@srv", f.Server), ("@fp",  f.Path),
                ("@imp", f.Importance), ("@mr", f.ReadEnabled), ("@mw", f.WriteEnabled),
                ("@md",  f.DeleteEnabled), ("@en", f.Enabled));

            await AuditAsync(conn, tx, ct, op, "monitored_folders", "UPDATE",
                id.ToString(),
                Obj(new { id = old.Id, server = old.Server, path = old.Path }),
                Obj(new { id, server = f.Server, path = f.Path, importance = f.Importance,
                    readEnabled = f.ReadEnabled, writeEnabled = f.WriteEnabled,
                    deleteEnabled = f.DeleteEnabled, enabled = f.Enabled }));

            await tx.CommitAsync(ct);
        }
        catch { await tx.RollbackAsync(ct); throw; }
    }

    public async Task DeleteFolderAsync(int id, string op, CancellationToken ct = default)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var tx   = await conn.BeginTransactionAsync(ct);
        try
        {
            var old = await ReadFolderAsync(conn, tx, ct, id)
                      ?? throw new InvalidOperationException($"folder id={id} not found");

            var rows = await ExecAsync(conn, tx, ct,
                $"DELETE FROM {_schema}.monitored_folders WHERE id = @id",
                ("@id", (object)id));

            if (rows == 0) throw new InvalidOperationException($"folder id={id} not deleted");

            await AuditAsync(conn, tx, ct, op, "monitored_folders", "DELETE",
                id.ToString(),
                Obj(new { id = old.Id, server = old.Server, path = old.Path }), null);

            await tx.CommitAsync(ct);
        }
        catch { await tx.RollbackAsync(ct); throw; }
    }

    // ================================================================
    //  users
    //  確定カラム: domain_name, user_name, display_name, department, role, enabled
    //  ※ user_sid は触らない
    // ================================================================

    public async Task<int> InsertUserAsync(UserConfig u, string op,
        CancellationToken ct = default)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var tx   = await conn.BeginTransactionAsync(ct);
        try
        {
            var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = $"""
                INSERT INTO {_schema}.users
                    (domain_name, user_name, display_name, department, role, enabled)
                VALUES(@dom, @un, @dn, @dept, @role, @en)
                RETURNING id
                """;
            cmd.Parameters.AddWithValue("@dom",  u.Domain);
            cmd.Parameters.AddWithValue("@un",   u.Name);
            cmd.Parameters.AddWithValue("@dn",   u.Display);
            cmd.Parameters.AddWithValue("@dept", u.Dept);
            cmd.Parameters.AddWithValue("@role", u.Role);
            cmd.Parameters.AddWithValue("@en",   u.Enabled);
            var newId = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));

            await AuditAsync(conn, tx, ct, op, "users", "INSERT",
                newId.ToString(), null,
                Obj(new { id = newId, domain = u.Domain, name = u.Name, display = u.Display,
                    dept = u.Dept, role = u.Role, enabled = u.Enabled }));

            await tx.CommitAsync(ct);
            return newId;
        }
        catch { await tx.RollbackAsync(ct); throw; }
    }

    public async Task UpdateUserAsync(int id, UserConfig u, string op,
        CancellationToken ct = default)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var tx   = await conn.BeginTransactionAsync(ct);
        try
        {
            var old = await ReadUserAsync(conn, tx, ct, id)
                      ?? throw new InvalidOperationException($"user id={id} not found");

            await ExecAsync(conn, tx, ct,
                $"""
                UPDATE {_schema}.users
                SET domain_name=@dom, user_name=@un, display_name=@dn,
                    department=@dept, role=@role, enabled=@en
                WHERE id = @id
                """,
                ("@id", (object)id), ("@dom", u.Domain), ("@un", u.Name),
                ("@dn", u.Display), ("@dept", u.Dept), ("@role", u.Role), ("@en", u.Enabled));

            await AuditAsync(conn, tx, ct, op, "users", "UPDATE",
                id.ToString(),
                Obj(new { id = old.Id, domain = old.Domain, name = old.Name }),
                Obj(new { id, domain = u.Domain, name = u.Name, display = u.Display,
                    dept = u.Dept, role = u.Role, enabled = u.Enabled }));

            await tx.CommitAsync(ct);
        }
        catch { await tx.RollbackAsync(ct); throw; }
    }

    public async Task DeleteUserAsync(int id, string op, CancellationToken ct = default)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var tx   = await conn.BeginTransactionAsync(ct);
        try
        {
            var old = await ReadUserAsync(conn, tx, ct, id)
                      ?? throw new InvalidOperationException($"user id={id} not found");

            var rows = await ExecAsync(conn, tx, ct,
                $"DELETE FROM {_schema}.users WHERE id = @id",
                ("@id", (object)id));

            if (rows == 0) throw new InvalidOperationException($"user id={id} not deleted");

            await AuditAsync(conn, tx, ct, op, "users", "DELETE",
                id.ToString(),
                Obj(new { id = old.Id, domain = old.Domain, name = old.Name }), null);

            await tx.CommitAsync(ct);
        }
        catch { await tx.RollbackAsync(ct); throw; }
    }

    // ================================================================
    //  alert_rules
    //  確定カラム: rule_name, condition_type, severity(::audit.alert_severity),
    //             target_folder, threshold_count, time_window_minutes,
    //             only_off_hours, enabled
    //  UPDATE は捕捉列のみ更新・他列（action_type 等）は保持する
    // ================================================================

    public async Task<int> InsertRuleAsync(AlertRule r, string op,
        CancellationToken ct = default)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var tx   = await conn.BeginTransactionAsync(ct);
        try
        {
            var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = $"""
                INSERT INTO {_schema}.alert_rules
                    (rule_name, condition_type, severity, target_folder,
                     threshold_count, time_window_minutes, only_off_hours, enabled)
                VALUES(@rn, @ct, @sev::{_schema}.alert_severity, @tf, @thr, @win, @off, @en)
                RETURNING id
                """;
            cmd.Parameters.AddWithValue("@rn",  r.Name);
            cmd.Parameters.AddWithValue("@ct",  r.Condition);
            cmd.Parameters.AddWithValue("@sev", r.Severity);
            cmd.Parameters.AddWithValue("@tf",  (object?)r.Target ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@thr", r.Threshold);
            cmd.Parameters.AddWithValue("@win", r.WindowMinutes);
            cmd.Parameters.AddWithValue("@off", r.OffHours);
            cmd.Parameters.AddWithValue("@en",  r.Enabled);
            var newId = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));

            await AuditAsync(conn, tx, ct, op, "alert_rules", "INSERT",
                newId.ToString(), null,
                Obj(new { id = newId, name = r.Name, condition = r.Condition, severity = r.Severity,
                    target = r.Target, threshold = r.Threshold, windowMinutes = r.WindowMinutes,
                    offHours = r.OffHours, enabled = r.Enabled }));

            await tx.CommitAsync(ct);
            return newId;
        }
        catch { await tx.RollbackAsync(ct); throw; }
    }

    public async Task UpdateRuleAsync(int id, AlertRule r, string op,
        CancellationToken ct = default)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var tx   = await conn.BeginTransactionAsync(ct);
        try
        {
            var old = await ReadRuleAsync(conn, tx, ct, id)
                      ?? throw new InvalidOperationException($"rule id={id} not found");

            // UPDATE は捕捉対象列のみ。他列（action_type 等）は保持する。
            await ExecAsync(conn, tx, ct,
                $"""
                UPDATE {_schema}.alert_rules
                SET rule_name=@rn, condition_type=@ct, severity=@sev::{_schema}.alert_severity,
                    target_folder=@tf, threshold_count=@thr, time_window_minutes=@win,
                    only_off_hours=@off, enabled=@en
                WHERE id = @id
                """,
                ("@id",  (object)id), ("@rn", r.Name), ("@ct", r.Condition),
                ("@sev", r.Severity), ("@tf", (object?)r.Target ?? DBNull.Value),
                ("@thr", r.Threshold), ("@win", r.WindowMinutes),
                ("@off", r.OffHours), ("@en", r.Enabled));

            await AuditAsync(conn, tx, ct, op, "alert_rules", "UPDATE",
                id.ToString(),
                Obj(new { id = old.Id, name = old.Name }),
                Obj(new { id, name = r.Name, condition = r.Condition, severity = r.Severity,
                    target = r.Target, threshold = r.Threshold, windowMinutes = r.WindowMinutes,
                    offHours = r.OffHours, enabled = r.Enabled }));

            await tx.CommitAsync(ct);
        }
        catch { await tx.RollbackAsync(ct); throw; }
    }

    public async Task DeleteRuleAsync(int id, string op, CancellationToken ct = default)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var tx   = await conn.BeginTransactionAsync(ct);
        try
        {
            var old = await ReadRuleAsync(conn, tx, ct, id)
                      ?? throw new InvalidOperationException($"rule id={id} not found");

            var rows = await ExecAsync(conn, tx, ct,
                $"DELETE FROM {_schema}.alert_rules WHERE id = @id",
                ("@id", (object)id));

            if (rows == 0) throw new InvalidOperationException($"rule id={id} not deleted");

            await AuditAsync(conn, tx, ct, op, "alert_rules", "DELETE",
                id.ToString(),
                Obj(new { id = old.Id, name = old.Name }), null);

            await tx.CommitAsync(ct);
        }
        catch { await tx.RollbackAsync(ct); throw; }
    }

    // ================================================================
    //  common_folders
    //  PK は folder_top(text)。Id は UI 連番なので書込はPathで識別する。
    //  確定カラム: folder_top(=Path), enabled, note(=Description)
    // ================================================================

    public async Task UpsertCommonFolderAsync(CommonFolder f, string op,
        CancellationToken ct = default)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var tx   = await conn.BeginTransactionAsync(ct);
        try
        {
            var oldDesc = await ScalarAsync<string?>(conn, tx, ct,
                $"SELECT note FROM {_schema}.common_folders WHERE folder_top = @pt",
                ("@pt", f.Path));

            await ExecAsync(conn, tx, ct,
                $"""
                INSERT INTO {_schema}.common_folders(folder_top, enabled, note)
                VALUES(@pt, @en, @nt)
                ON CONFLICT(folder_top) DO UPDATE
                SET enabled = @en, note = @nt
                """,
                ("@pt", (object)f.Path), ("@en", true),
                ("@nt", (object?)f.Description ?? DBNull.Value));

            await AuditAsync(conn, tx, ct, op, "common_folders",
                oldDesc is null ? "INSERT" : "UPDATE",
                f.Path,
                oldDesc is null ? null : Obj(new { path = f.Path, description = oldDesc }),
                Obj(new { path = f.Path, description = f.Description }));

            await tx.CommitAsync(ct);
        }
        catch { await tx.RollbackAsync(ct); throw; }
    }

    public async Task DeleteCommonFolderAsync(string path, string op,
        CancellationToken ct = default)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var tx   = await conn.BeginTransactionAsync(ct);
        try
        {
            var oldDesc = await ScalarAsync<string?>(conn, tx, ct,
                $"SELECT note FROM {_schema}.common_folders WHERE folder_top = @pt",
                ("@pt", path));

            if (oldDesc is null && await ScalarAsync<long?>(conn, tx, ct,
                    $"SELECT COUNT(*) FROM {_schema}.common_folders WHERE folder_top = @pt",
                    ("@pt", path)) == 0)
                throw new InvalidOperationException($"common_folder path={path} not found");

            var rows = await ExecAsync(conn, tx, ct,
                $"DELETE FROM {_schema}.common_folders WHERE folder_top = @pt",
                ("@pt", (object)path));

            if (rows == 0) throw new InvalidOperationException($"common_folder path={path} not deleted");

            await AuditAsync(conn, tx, ct, op, "common_folders", "DELETE",
                path, Obj(new { path, description = oldDesc }), null);

            await tx.CommitAsync(ct);
        }
        catch { await tx.RollbackAsync(ct); throw; }
    }

    // ================================================================
    //  user_folder_grants
    //  確定カラム: user_name, kind, value, enabled(=true), reason(任意)
    // ================================================================

    public async Task<int> InsertGrantAsync(UserFolderGrant g, string op,
        CancellationToken ct = default)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var tx   = await conn.BeginTransactionAsync(ct);
        try
        {
            var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = $"""
                INSERT INTO {_schema}.user_folder_grants
                    (user_name, kind, value, enabled)
                VALUES(@un, @kind, @val, true)
                RETURNING id
                """;
            cmd.Parameters.AddWithValue("@un",   g.User);
            cmd.Parameters.AddWithValue("@kind", g.Kind);
            cmd.Parameters.AddWithValue("@val",  g.Value);
            var newId = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));

            await AuditAsync(conn, tx, ct, op, "user_folder_grants", "INSERT",
                newId.ToString(), null,
                Obj(new { id = newId, user = g.User, kind = g.Kind, value = g.Value }));

            await tx.CommitAsync(ct);
            return newId;
        }
        catch { await tx.RollbackAsync(ct); throw; }
    }

    public async Task UpdateGrantAsync(int id, UserFolderGrant g, string op,
        CancellationToken ct = default)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var tx   = await conn.BeginTransactionAsync(ct);
        try
        {
            var old = await ReadGrantAsync(conn, tx, ct, id)
                      ?? throw new InvalidOperationException($"grant id={id} not found");

            await ExecAsync(conn, tx, ct,
                $"""
                UPDATE {_schema}.user_folder_grants
                SET user_name=@un, kind=@kind, value=@val
                WHERE id = @id
                """,
                ("@id", (object)id), ("@un", g.User), ("@kind", g.Kind), ("@val", g.Value));

            await AuditAsync(conn, tx, ct, op, "user_folder_grants", "UPDATE",
                id.ToString(),
                Obj(new { id = old.Id, user = old.User, kind = old.Kind, value = old.Value }),
                Obj(new { id, user = g.User, kind = g.Kind, value = g.Value }));

            await tx.CommitAsync(ct);
        }
        catch { await tx.RollbackAsync(ct); throw; }
    }

    public async Task DeleteGrantAsync(int id, string op, CancellationToken ct = default)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var tx   = await conn.BeginTransactionAsync(ct);
        try
        {
            var old = await ReadGrantAsync(conn, tx, ct, id)
                      ?? throw new InvalidOperationException($"grant id={id} not found");

            var rows = await ExecAsync(conn, tx, ct,
                $"DELETE FROM {_schema}.user_folder_grants WHERE id = @id",
                ("@id", (object)id));

            if (rows == 0) throw new InvalidOperationException($"grant id={id} not deleted");

            await AuditAsync(conn, tx, ct, op, "user_folder_grants", "DELETE",
                id.ToString(),
                Obj(new { id = old.Id, user = old.User, kind = old.Kind, value = old.Value }), null);

            await tx.CommitAsync(ct);
        }
        catch { await tx.RollbackAsync(ct); throw; }
    }

    // ================================================================
    //  alert_histories ／ detected_incidents — status 列のみ
    //  ログ本体の他列は一切変更しない。status 系列のみ許可。
    // ================================================================

    /// <summary>
    /// alert_histories の status を更新する。
    /// 他列（rule_name / user_name 等）は変更しない。
    /// </summary>
    public async Task UpdateAlertStatusAsync(long id, string status, string op,
        CancellationToken ct = default)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var tx   = await conn.BeginTransactionAsync(ct);
        try
        {
            await ExecAsync(conn, tx, ct,
                $"""
                UPDATE {_schema}.alert_histories
                SET status = @s::{_schema}.alert_status, acked_by = @op, acked_at = now()
                WHERE id = @id
                """,
                ("@s", (object)status), ("@op", op), ("@id", id));

            await AuditAsync(conn, tx, ct, op, "alert_histories", "STATUS",
                id.ToString(), null, Obj(new { id, status }));

            await tx.CommitAsync(ct);
        }
        catch { await tx.RollbackAsync(ct); throw; }
    }

    /// <summary>
    /// detected_incidents の status を更新する。
    /// 他列（type / user_name / match_count 等）は変更しない。
    /// </summary>
    public async Task UpdateIncidentStatusAsync(long id, string status, string op,
        CancellationToken ct = default)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var tx   = await conn.BeginTransactionAsync(ct);
        try
        {
            await ExecAsync(conn, tx, ct,
                $"""
                UPDATE {_schema}.detected_incidents
                SET status = @s::{_schema}.alert_status
                WHERE id = @id
                """,
                ("@s", (object)status), ("@id", id));

            await AuditAsync(conn, tx, ct, op, "detected_incidents", "STATUS",
                id.ToString(), null, Obj(new { id, status }));

            await tx.CommitAsync(ct);
        }
        catch { await tx.RollbackAsync(ct); throw; }
    }

    // ================================================================
    //  内部ヘルパー: テーブル行読み取り（旧値取得用）
    // ================================================================

    private async Task<DetectionExclusion?> ReadExclusionAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, CancellationToken ct, int id)
    {
        var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"""
            SELECT id, user_pattern, process_pattern, path_regex, reason
            FROM {_schema}.detection_exclusions WHERE id = @id
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

    private async Task<MonitoredFolder?> ReadFolderAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, CancellationToken ct, int id)
    {
        var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"""
            SELECT id, server_name, folder_path, importance::text,
                   monitor_read, monitor_write, monitor_delete, enabled
            FROM {_schema}.monitored_folders WHERE id = @id
            """;
        cmd.Parameters.AddWithValue("@id", id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return new MonitoredFolder(
            Id:            r.GetInt32(0),
            Server:        r.GetString(1),
            Path:          r.GetString(2),
            Importance:    r.GetString(3),
            ReadEnabled:   r.GetBoolean(4),
            WriteEnabled:  r.GetBoolean(5),
            DeleteEnabled: r.GetBoolean(6),
            Enabled:       r.GetBoolean(7));
    }

    private async Task<UserConfig?> ReadUserAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, CancellationToken ct, int id)
    {
        var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"""
            SELECT id, domain_name, user_name, display_name, department, role, enabled
            FROM {_schema}.users WHERE id = @id
            """;
        cmd.Parameters.AddWithValue("@id", id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return new UserConfig(
            Id:      r.GetInt32(0),
            Domain:  r.IsDBNull(1) ? "" : r.GetString(1),
            Name:    r.GetString(2),
            Display: r.IsDBNull(3) ? "" : r.GetString(3),
            Dept:    r.IsDBNull(4) ? "" : r.GetString(4),
            Role:    r.IsDBNull(5) ? "" : r.GetString(5),
            Enabled: r.GetBoolean(6));
    }

    private async Task<AlertRule?> ReadRuleAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, CancellationToken ct, int id)
    {
        var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"""
            SELECT id, rule_name, condition_type, severity::text, target_folder,
                   threshold_count, time_window_minutes, only_off_hours, enabled
            FROM {_schema}.alert_rules WHERE id = @id
            """;
        cmd.Parameters.AddWithValue("@id", id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return new AlertRule(
            Id:            r.GetInt32(0),
            Name:          r.GetString(1),
            Condition:     r.IsDBNull(2) ? "" : r.GetString(2),
            Severity:      r.GetString(3),
            Target:        r.IsDBNull(4) ? "" : r.GetString(4),
            Threshold:     r.GetInt32(5),
            WindowMinutes: r.GetInt32(6),
            OffHours:      r.GetBoolean(7),
            Enabled:       r.GetBoolean(8));
    }

    private async Task<UserFolderGrant?> ReadGrantAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, CancellationToken ct, int id)
    {
        var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"""
            SELECT id, user_name, kind, value
            FROM {_schema}.user_folder_grants WHERE id = @id
            """;
        cmd.Parameters.AddWithValue("@id", id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return new UserFolderGrant(
            Id:    r.GetInt32(0),
            User:  r.GetString(1),
            Kind:  r.IsDBNull(2) ? "" : r.GetString(2),
            Value: r.IsDBNull(3) ? "" : r.GetString(3));
    }

    // ================================================================
    //  内部ヘルパー: SQL 実行
    // ================================================================

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

    private static string Obj(object value) => JsonSerializer.Serialize(value);

    public void Dispose() => _ds.Dispose();
}
