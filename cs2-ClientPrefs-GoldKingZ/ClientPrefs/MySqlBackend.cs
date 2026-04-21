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
    private readonly object _lock = new();
    private bool _isMigrating;
    public bool IsReady { get; private set; }

    public MySqlBackend(string tableName, ClientPrefsOptions opts, Action<string, bool> debug)
    {
        _tableName = tableName;
        _opts = opts;
        _debug = debug;
    }

    public async Task<MySqlConnection?> GetConnectionAsync()
    {
        var servers = _opts.PrefsAPI_MySqlConfig.EffectiveServers;
        if (servers.Count == 0) return null;

        for (int attempt = 0; attempt < _opts.PrefsAPI_MySqlRetryAttempts; attempt++)
        {
            if (attempt > 0) await Task.Delay(TimeSpan.FromSeconds(_opts.PrefsAPI_MySqlRetryDelay));

            foreach (var s in servers)
            {
                try
                {
                    var conn = new MySqlConnection(new MySqlConnectionStringBuilder
                    {
                        Server   = s.Server,
                        Port     = (uint)s.Port,
                        Database = s.Database,
                        UserID   = s.Username,
                        Password = s.Password,
                        ConnectionTimeout = (uint)_opts.PrefsAPI_MySqlConnectionTimeout,
                        Pooling = true, MinimumPoolSize = 0, MaximumPoolSize = 100,
                    }.ConnectionString);

                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(_opts.PrefsAPI_MySqlConnectionTimeout));
                    await conn.OpenAsync(cts.Token);
                    return conn;
                }
                catch (Exception ex)
                {
                    _debug($"MySQL connection failed ({s.Server}:{s.Port}): {ex.Message}", true);
                }
            }
        }
        _debug($"MySQL all connection attempts exhausted ({_opts.PrefsAPI_MySqlRetryAttempts} attempts)", true);
        return null;
    }

    public async Task<bool> EnsureTableAsync()
    {
        lock (_lock) { if (_isMigrating) return false; _isMigrating = true; }
        try
        {
            await using var conn = await GetConnectionAsync();
            if (conn == null) return false;

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
                _debug($"MySQL table '{_tableName}' created", false);
            }
            else
            {
                await MigrateAsync(conn);
            }
            IsReady = true;
            return true;
        }
        catch (Exception ex)
        {
            _debug($"MySQL EnsureTableAsync error: {ex.Message}", true);
            return false;
        }
        finally { lock (_lock) { _isMigrating = false; } }
    }

    private async Task MigrateAsync(MySqlConnection conn)
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
                _debug($"MySQL migration: dropping removed column '{col}'", false);
                await using var cmd = new MySqlCommand($"ALTER TABLE {_tableName} DROP COLUMN {col}", conn);
                await cmd.ExecuteNonQueryAsync();
                existing.Remove(col);
            }
            else if (existing[col] != DataType(prop))
            {
                _debug($"MySQL migration: type changed for '{col}' ({existing[col]} → {DataType(prop)}), modifying", false);
                await using var cmd = new MySqlCommand(
                    $"ALTER TABLE {_tableName} MODIFY COLUMN {col} {SqlType(prop)}", conn);
                await cmd.ExecuteNonQueryAsync();
                existing[col] = DataType(prop);
            }
        }

        if (!existing.ContainsKey(PrefsTypeInfo.NameColumn))
        {
            _debug($"MySQL migration: adding missing column '{PrefsTypeInfo.NameColumn}'", false);
            await using var cmd = new MySqlCommand(
                $"ALTER TABLE {_tableName} ADD COLUMN {PrefsTypeInfo.NameColumn} VARCHAR(255) NOT NULL DEFAULT '' FIRST", conn);
            await cmd.ExecuteNonQueryAsync();
        }

        if (!existing.ContainsKey(PrefsTypeInfo.DateColumn))
        {
            _debug($"MySQL migration: adding missing column '{PrefsTypeInfo.DateColumn}'", false);
            await using var cmd = new MySqlCommand(
                $"ALTER TABLE {_tableName} ADD COLUMN {PrefsTypeInfo.DateColumn} DATETIME NOT NULL DEFAULT NOW() AFTER {PrefsTypeInfo.PkColumn}", conn);
            await cmd.ExecuteNonQueryAsync();
        }

        string prev = PrefsTypeInfo.DateColumn;
        foreach (var p in _info.Props)
        {
            if (!existing.ContainsKey(p.Name))
            {
                _debug($"MySQL migration: adding new column '{p.Name}'", false);
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

        try
        {
            await using var conn = await GetConnectionAsync();
            if (conn == null) return false;

            await using var cmd = new MySqlCommand(
                $"INSERT INTO {_tableName} ({cols}) VALUES ({parms}) ON DUPLICATE KEY UPDATE {update}", conn);

            cmd.Parameters.Add("@" + PrefsTypeInfo.NameColumn, MySqlDbType.VarChar, 255).Value = playerName;
            cmd.Parameters.Add("@" + PrefsTypeInfo.PkColumn,   MySqlDbType.UInt64).Value       = steamId;
            cmd.Parameters.Add("@" + PrefsTypeInfo.DateColumn, MySqlDbType.DateTime).Value     = date;
            foreach (var p in _info.Props)
                cmd.Parameters.Add("@" + p.Name, DbType(p.PropertyType)).Value = p.GetValue(payload)!;

            await cmd.ExecuteNonQueryAsync();
            return true;
        }
        catch (Exception ex)
        {
            _debug($"MySQL SaveAsync error: {ex.Message}", true);
            return false;
        }
    }

    public async Task<(DateTime date, T payload)?> LoadAsync(ulong steamId)
    {
        try
        {
            await using var conn = await GetConnectionAsync();
            if (conn == null) return null;

            await using var cmd = new MySqlCommand(
                $"SELECT * FROM {_tableName} WHERE {PrefsTypeInfo.PkColumn} = @id", conn);
            cmd.Parameters.Add("@id", MySqlDbType.UInt64).Value = steamId;

            await using var reader = await cmd.ExecuteReaderAsync();
            if (!await reader.ReadAsync()) return null;

            var date = reader.GetDateTime(PrefsTypeInfo.DateColumn);

            var obj = new T();
            foreach (var p in _info.Props)
            {
                try { p.SetValue(obj, ReadColumn(reader, p)); } catch { }
            }
            return (date, obj);
        }
        catch (Exception ex)
        {
            _debug($"MySQL LoadAsync error: {ex.Message}", true);
            return null;
        }
    }

    public async Task<bool> DeleteAsync(ulong steamId)
    {
        try
        {
            await using var conn = await GetConnectionAsync();
            if (conn == null) return false;

            await using var cmd = new MySqlCommand(
                $"DELETE FROM {_tableName} WHERE {PrefsTypeInfo.PkColumn} = @id", conn);
            cmd.Parameters.Add("@id", MySqlDbType.UInt64).Value = steamId;
            await cmd.ExecuteNonQueryAsync();
            _debug($"MySQL: deleted player {steamId}", false);
            return true;
        }
        catch (Exception ex)
        {
            _debug($"MySQL DeleteAsync error: {ex.Message}", true);
            return false;
        }
    }

    public async Task<bool> DeleteOldAsync(int days)
    {
        if (days < 1) return false;

        try
        {
            await using var conn = await GetConnectionAsync();
            if (conn == null) return false;

            await using var cmd = new MySqlCommand(
                $"DELETE FROM {_tableName} WHERE {PrefsTypeInfo.DateColumn} < NOW() - INTERVAL @Days DAY", conn);
            cmd.Parameters.Add("@Days", MySqlDbType.Int32).Value = days;
            var rows = await cmd.ExecuteNonQueryAsync();

            if (rows > 0)
                _debug($"MySQL cleanup: removed {rows} inactive player(s) older than {days} day(s)", false);

            return true;
        }
        catch (Exception ex)
        {
            _debug($"MySQL DeleteOldAsync error: {ex.Message}", true);
            return false;
        }
    }

    private static string SqlType(PropertyInfo p)
    {
        if (p.PropertyType == typeof(float))    return "FLOAT NOT NULL DEFAULT 0";
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
        if (p.PropertyType == typeof(float))    return "float";
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
        if (t == typeof(float))    return MySqlDbType.Float;
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
        if (p.PropertyType == typeof(float))    return r.GetFloat(p.Name);
        if (p.PropertyType == typeof(double))   return r.GetDouble(p.Name);
        if (p.PropertyType == typeof(bool))     return r.GetBoolean(p.Name);
        if (p.PropertyType == typeof(DateTime)) return r.GetDateTime(p.Name);
        return r.IsDBNull(r.GetOrdinal(p.Name)) ? string.Empty : r.GetString(p.Name);
    }
}