using System.Reflection;
using ClientPrefs_GoldKingZ.Shared;
using MySqlConnector;

namespace ClientPrefs_GoldKingZ;

internal sealed class MySqlBackend<T> where T : class, new()
{
    private readonly PrefsTypeInfo _info = PrefsTypeInfo.Of<T>();
    private readonly string _tableName;
    private readonly ClientPrefsOptions _opts;
    private readonly Action<string, bool> _debug;
    private readonly object _ensureLock = new();
    private readonly HashSet<string> _ensuredServers = new();
    private MySqlManager? _manager;
    private volatile bool _disposed;

    public MySqlBackend(string tableName, ClientPrefsOptions opts, Action<string, bool> debug)
    {
        _tableName = tableName;
        _opts = opts;
        _debug = debug;
        _manager = MySqlManager.Acquire(opts);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        var mgr = _manager;
        _manager = null;
        mgr?.Release();
    }

    private Task<List<(string serverKey, MySqlConnection conn)>> GetAllConnectionsAsync()
    {
        var mgr = _manager;
        if (_disposed || mgr == null)
            return Task.FromResult(new List<(string, MySqlConnection)>());

        return mgr.GetAllConnectionsAsync(
            _opts.PrefsAPI_MySqlConnectionTimeout,
            _opts.PrefsAPI_MySqlRetryAttempts,
            _opts.PrefsAPI_MySqlRetryDelay,
            _debug);
    }

    public async Task<bool> EnsureTableAsync()
    {
        var conns = await GetAllConnectionsAsync();
        if (conns.Count == 0) return false;

        bool anyOk = false;
        foreach (var (key, conn) in conns)
        {
            try
            {
                if (await EnsureTableOnAsync(key, conn)) anyOk = true;
            }
            finally
            {
                try { await conn.DisposeAsync(); } catch { }
            }
        }
        return anyOk;
    }

    private async Task<bool> EnsureTableOnAsync(string serverKey, MySqlConnection conn)
    {
        lock (_ensureLock)
        {
            if (_ensuredServers.Contains(serverKey)) return true;
        }

        try
        {
            await using var check = new MySqlCommand(
                "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name = @t", conn);
            check.Parameters.AddWithValue("@t", _tableName);
            bool exists = Convert.ToInt32(await check.ExecuteScalarAsync()) > 0;

            if (!exists)
            {
                var cols = new List<string>
                {
                    $"{PrefsTypeInfo.NameColumn} VARCHAR(255) NOT NULL DEFAULT ''",
                    $"{PrefsTypeInfo.PkColumn} BIGINT UNSIGNED PRIMARY KEY",
                    $"{PrefsTypeInfo.DateColumn} DATETIME NOT NULL",
                };
                cols.AddRange(_info.Props.Select(p => $"{p.Name} {SqlType(p)}"));

                await using var cmd = new MySqlCommand(
                    $"CREATE TABLE {_tableName} ({string.Join(", ", cols)})", conn);
                await cmd.ExecuteNonQueryAsync();
                _debug($"MySQL [{serverKey}]: table '{_tableName}' created", false);
            }
            else
            {
                await MigrateAsync(serverKey, conn);
            }

            lock (_ensureLock)
            {
                _ensuredServers.Add(serverKey);
            }
            return true;
        }
        catch (Exception ex)
        {
            _debug($"MySQL [{serverKey}] EnsureTable error: {ex.Message}", true);
            return false;
        }
    }

    private async Task MigrateAsync(string serverKey, MySqlConnection conn)
    {
        var existing = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        await using (var cmd = new MySqlCommand(
            "SELECT COLUMN_NAME, DATA_TYPE FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @t", conn))
        {
            cmd.Parameters.AddWithValue("@t", _tableName);
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                existing[r.GetString(0)] = r.GetString(1).ToLower();
        }

        bool IsReserved(string col) =>
            col.Equals(PrefsTypeInfo.NameColumn, StringComparison.OrdinalIgnoreCase) ||
            col.Equals(PrefsTypeInfo.PkColumn,   StringComparison.OrdinalIgnoreCase) ||
            col.Equals(PrefsTypeInfo.DateColumn, StringComparison.OrdinalIgnoreCase);

        var propByName = new Dictionary<string, PropertyInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in _info.Props) propByName[p.Name] = p;

        foreach (var col in existing.Keys.ToList())
        {
            if (IsReserved(col)) continue;

            if (!propByName.TryGetValue(col, out var prop))
            {
                _debug($"MySQL [{serverKey}] migration: dropping removed column '{col}'", false);
                await using var cmd = new MySqlCommand($"ALTER TABLE {_tableName} DROP COLUMN {col}", conn);
                await cmd.ExecuteNonQueryAsync();
                existing.Remove(col);
            }
            else if (existing[col] != DataType(prop))
            {
                _debug($"MySQL [{serverKey}] migration: type changed for '{col}' ({existing[col]} -> {DataType(prop)}), modifying", false);
                await using var cmd = new MySqlCommand(
                    $"ALTER TABLE {_tableName} MODIFY COLUMN {col} {SqlType(prop)}", conn);
                await cmd.ExecuteNonQueryAsync();
                existing[col] = DataType(prop);
            }
        }

        if (!existing.ContainsKey(PrefsTypeInfo.NameColumn))
        {
            _debug($"MySQL [{serverKey}] migration: adding missing column '{PrefsTypeInfo.NameColumn}'", false);
            await using var cmd = new MySqlCommand(
                $"ALTER TABLE {_tableName} ADD COLUMN {PrefsTypeInfo.NameColumn} VARCHAR(255) NOT NULL DEFAULT '' FIRST", conn);
            await cmd.ExecuteNonQueryAsync();
        }

        if (!existing.ContainsKey(PrefsTypeInfo.DateColumn))
        {
            _debug($"MySQL [{serverKey}] migration: adding missing column '{PrefsTypeInfo.DateColumn}'", false);
            await using var cmd = new MySqlCommand(
                $"ALTER TABLE {_tableName} ADD COLUMN {PrefsTypeInfo.DateColumn} DATETIME NOT NULL DEFAULT NOW() AFTER {PrefsTypeInfo.PkColumn}", conn);
            await cmd.ExecuteNonQueryAsync();
        }

        string prev = PrefsTypeInfo.DateColumn;
        foreach (var p in _info.Props)
        {
            if (!existing.ContainsKey(p.Name))
            {
                _debug($"MySQL [{serverKey}] migration: adding new column '{p.Name}'", false);
                await using var cmd = new MySqlCommand(
                    $"ALTER TABLE {_tableName} ADD COLUMN {p.Name} {SqlType(p)} AFTER {prev}", conn);
                await cmd.ExecuteNonQueryAsync();
            }
            prev = p.Name;
        }
    }

    public async Task<bool> SaveAsync(ulong steamId, string playerName, DateTime date, T payload)
    {
        _info.FillNullStrings(payload);
        playerName ??= "";

        var conns = await GetAllConnectionsAsync();
        if (conns.Count == 0) return false;

        bool anyOk = false;
        foreach (var (key, conn) in conns)
        {
            try
            {
                if (!await EnsureTableOnAsync(key, conn)) continue;

                await using var cmd = BuildUpsertCommand(conn, steamId, playerName, date, payload);
                await cmd.ExecuteNonQueryAsync();
                _debug($"MySQL [{key}]: Saved Player {playerName} ({steamId})", false);
                anyOk = true;
            }
            catch (Exception ex)
            {
                _debug($"MySQL [{key}] SaveAsync error: {ex.Message}", true);
            }
            finally
            {
                try { await conn.DisposeAsync(); } catch { }
            }
        }
        return anyOk;
    }

    public async Task<bool> SaveManyAsync(List<(ulong steamId, string playerName, DateTime date, T payload)> rows)
    {
        if (rows.Count == 0) return true;

        foreach (var row in rows)
            _info.FillNullStrings(row.payload);

        var conns = await GetAllConnectionsAsync();
        if (conns.Count == 0) return false;

        bool anyOk = false;
        foreach (var (key, conn) in conns)
        {
            try
            {
                if (!await EnsureTableOnAsync(key, conn)) continue;

                await using var tx = await conn.BeginTransactionAsync();
                try
                {
                    foreach (var row in rows)
                    {
                        await using var cmd = BuildUpsertCommand(conn, row.steamId, row.playerName ?? "", row.date, row.payload);
                        cmd.Transaction = tx;
                        await cmd.ExecuteNonQueryAsync();
                    }
                    await tx.CommitAsync();
                    _debug($"MySQL [{key}]: Saved {rows.Count} Player(s) In One Transaction", false);
                    anyOk = true;
                }
                catch
                {
                    try { await tx.RollbackAsync(); } catch { }
                    throw;
                }
            }
            catch (Exception ex)
            {
                _debug($"MySQL [{key}] SaveManyAsync error: {ex.Message}", true);
            }
            finally
            {
                try { await conn.DisposeAsync(); } catch { }
            }
        }
        return anyOk;
    }

    private MySqlCommand BuildUpsertCommand(MySqlConnection conn, ulong steamId, string playerName, DateTime date, T payload)
    {
        var allCols = new List<string>
        {
            PrefsTypeInfo.NameColumn,
            PrefsTypeInfo.PkColumn,
            PrefsTypeInfo.DateColumn,
        };
        allCols.AddRange(_info.Props.Select(p => p.Name));

        var parms  = string.Join(", ", allCols.Select(c => "@" + c));
        var cols   = string.Join(", ", allCols);
        var update = string.Join(", ", allCols
            .Where(c => c != PrefsTypeInfo.PkColumn)
            .Select(c => $"{c} = VALUES({c})"));

        var cmd = new MySqlCommand(
            $"INSERT INTO {_tableName} ({cols}) VALUES ({parms}) ON DUPLICATE KEY UPDATE {update}", conn);

        cmd.Parameters.Add("@" + PrefsTypeInfo.NameColumn, MySqlDbType.VarChar, 255).Value = playerName;
        cmd.Parameters.Add("@" + PrefsTypeInfo.PkColumn,   MySqlDbType.UInt64).Value       = steamId;
        cmd.Parameters.Add("@" + PrefsTypeInfo.DateColumn, MySqlDbType.DateTime).Value     = date;
        foreach (var p in _info.Props)
        {
            var v = p.GetValue(payload)!;
            if (p.PropertyType == typeof(float)) v = CookiesBackend<T>.FloatToStorage((float)v);
            cmd.Parameters.Add("@" + p.Name, DbType(p.PropertyType)).Value = v;
        }

        return cmd;
    }

    public async Task<(DateTime date, T payload)?> LoadAsync(ulong steamId)
    {
        var conns = await GetAllConnectionsAsync();
        if (conns.Count == 0) return null;

        (DateTime date, T payload)? best = null;
        string bestKey = "";

        foreach (var (key, conn) in conns)
        {
            try
            {
                if (!await EnsureTableOnAsync(key, conn)) continue;

                await using var cmd = new MySqlCommand(
                    $"SELECT * FROM {_tableName} WHERE {PrefsTypeInfo.PkColumn} = @id", conn);
                cmd.Parameters.Add("@id", MySqlDbType.UInt64).Value = steamId;

                await using var reader = await cmd.ExecuteReaderAsync();
                if (!await reader.ReadAsync()) continue;

                var date = reader.GetDateTime(PrefsTypeInfo.DateColumn);

                if (best == null || date > best.Value.date)
                {
                    var obj = new T();
                    foreach (var p in _info.Props)
                    {
                        try { p.SetValue(obj, ReadColumn(reader, p)); } catch { }
                    }
                    best = (date, obj);
                    bestKey = key;
                }
            }
            catch (Exception ex)
            {
                _debug($"MySQL [{key}] LoadAsync error: {ex.Message}", true);
            }
            finally
            {
                try { await conn.DisposeAsync(); } catch { }
            }
        }

        if (best != null && conns.Count > 1)
            _debug($"MySQL: newest row for {steamId} came from [{bestKey}] ({best.Value.date:yyyy-MM-dd HH:mm:ss})", false);

        return best;
    }

    public async Task<bool> DeleteAsync(ulong steamId)
    {
        var conns = await GetAllConnectionsAsync();
        if (conns.Count == 0) return false;

        bool anyOk = false;
        foreach (var (key, conn) in conns)
        {
            try
            {
                await using var cmd = new MySqlCommand(
                    $"DELETE FROM {_tableName} WHERE {PrefsTypeInfo.PkColumn} = @id", conn);
                cmd.Parameters.Add("@id", MySqlDbType.UInt64).Value = steamId;
                await cmd.ExecuteNonQueryAsync();
                _debug($"MySQL [{key}]: deleted player {steamId}", false);
                anyOk = true;
            }
            catch (Exception ex)
            {
                _debug($"MySQL [{key}] DeleteAsync error: {ex.Message}", true);
            }
            finally
            {
                try { await conn.DisposeAsync(); } catch { }
            }
        }
        return anyOk;
    }

    public async Task<bool> DeleteOldAsync(int days)
    {
        if (days < 1) return false;

        var conns = await GetAllConnectionsAsync();
        if (conns.Count == 0) return false;

        bool anyOk = false;
        foreach (var (key, conn) in conns)
        {
            try
            {
                var expired = new List<(ulong steamId, string name, DateTime date)>();
                await using (var select = new MySqlCommand(
                    $"SELECT {PrefsTypeInfo.PkColumn}, {PrefsTypeInfo.NameColumn}, {PrefsTypeInfo.DateColumn} FROM {_tableName} WHERE {PrefsTypeInfo.DateColumn} < NOW() - INTERVAL @Days DAY", conn))
                {
                    select.Parameters.Add("@Days", MySqlDbType.Int32).Value = days;
                    await using var r = await select.ExecuteReaderAsync();
                    while (await r.ReadAsync())
                        expired.Add((r.GetUInt64(0), r.GetString(1), r.GetDateTime(2)));
                }

                if (expired.Count > 0)
                {
                    _debug($"MySQL [{key}] Cleanup: Removing {expired.Count} Inactive Player(s) Older Than {days} Day(s):", false);
                    foreach (var e in expired)
                        _debug($"MySQL [{key}] Cleanup: Removed {e.name} ({e.steamId}) — Last Active {e.date:yyyy-MM-dd HH:mm:ss}", false);

                    await using var cmd = new MySqlCommand(
                        $"DELETE FROM {_tableName} WHERE {PrefsTypeInfo.DateColumn} < NOW() - INTERVAL @Days DAY", conn);
                    cmd.Parameters.Add("@Days", MySqlDbType.Int32).Value = days;
                    await cmd.ExecuteNonQueryAsync();
                }

                anyOk = true;
            }
            catch (Exception ex)
            {
                _debug($"MySQL [{key}] DeleteOldAsync error: {ex.Message}", true);
            }
            finally
            {
                try { await conn.DisposeAsync(); } catch { }
            }
        }
        return anyOk;
    }

    private static string SqlType(PropertyInfo p)
    {
        if (p.PropertyType == typeof(float))    return "DOUBLE NOT NULL DEFAULT 0";
        if (p.PropertyType == typeof(double))   return "DOUBLE NOT NULL DEFAULT 0";
        if (p.PropertyType == typeof(int))      return "INT NOT NULL DEFAULT 0";
        if (p.PropertyType == typeof(long))     return "BIGINT NOT NULL DEFAULT 0";
        if (p.PropertyType == typeof(ulong))    return "BIGINT UNSIGNED NOT NULL DEFAULT 0";
        if (p.PropertyType == typeof(bool))     return "TINYINT(1) NOT NULL DEFAULT 0";
        if (p.PropertyType == typeof(DateTime)) return "DATETIME NOT NULL";
        return "VARCHAR(255) NOT NULL DEFAULT ''";
    }

    private static string DataType(PropertyInfo p)
    {
        if (p.PropertyType == typeof(float))    return "double";
        if (p.PropertyType == typeof(double))   return "double";
        if (p.PropertyType == typeof(int))      return "int";
        if (p.PropertyType == typeof(long))     return "bigint";
        if (p.PropertyType == typeof(ulong))    return "bigint";
        if (p.PropertyType == typeof(bool))     return "tinyint";
        if (p.PropertyType == typeof(DateTime)) return "datetime";
        return "varchar";
    }

    private static MySqlDbType DbType(Type t)
    {
        if (t == typeof(ulong))    return MySqlDbType.UInt64;
        if (t == typeof(long))     return MySqlDbType.Int64;
        if (t == typeof(int))      return MySqlDbType.Int32;
        if (t == typeof(float))    return MySqlDbType.Double;
        if (t == typeof(double))   return MySqlDbType.Double;
        if (t == typeof(bool))     return MySqlDbType.Byte;
        if (t == typeof(DateTime)) return MySqlDbType.DateTime;
        return MySqlDbType.VarChar;
    }

    private static object ReadColumn(MySqlDataReader r, PropertyInfo p)
    {
        if (p.PropertyType == typeof(ulong))    return r.GetUInt64(p.Name);
        if (p.PropertyType == typeof(long))     return r.GetInt64(p.Name);
        if (p.PropertyType == typeof(int))      return r.GetInt32(p.Name);
        if (p.PropertyType == typeof(float))    return (float)r.GetDouble(p.Name);
        if (p.PropertyType == typeof(double))   return r.GetDouble(p.Name);
        if (p.PropertyType == typeof(bool))     return r.GetBoolean(p.Name);
        if (p.PropertyType == typeof(DateTime)) return r.GetDateTime(p.Name);
        return r.IsDBNull(r.GetOrdinal(p.Name)) ? string.Empty : r.GetString(p.Name);
    }
}