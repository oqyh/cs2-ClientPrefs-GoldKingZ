using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.Data.Sqlite;

namespace ClientPrefs_GoldKingZ;

internal sealed class CookiesBackend<T> where T : class, new()
{
    private const string TableName = "PlayerCookies";

    private readonly PrefsTypeInfo _info = PrefsTypeInfo.Of<T>();
    private readonly string _dbPath;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<ulong, (string name, DateTime date, T payload)> _cache = new();
    private readonly Action<string, bool> _debug;

    private SqliteConnection? _conn;

    public CookiesBackend(string dbPath, Action<string, bool> debug)
    {
        _dbPath = dbPath;
        _debug = debug;
    }

    public void Initialize()
    {
        try
        {
            SQLitePCL.Batteries.Init();
        }
        catch (Exception ex)
        {
            _debug($"SQLitePCL.Batteries.Init() Failed: {ex.Message}", true);
            throw;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);
            _debug($"Opening SQLite: {_dbPath}", false);

            _conn = new SqliteConnection($"Data Source={_dbPath}");
            _conn.Open();

            using (var pragma = _conn.CreateCommand())
            {
                pragma.CommandText = @"
                    PRAGMA journal_mode = WAL;
                    PRAGMA synchronous  = NORMAL;
                    PRAGMA busy_timeout = 5000;
                    PRAGMA cache_size   = -8000;
                    PRAGMA temp_store   = MEMORY;
                    PRAGMA wal_autocheckpoint = 1;
                ";
                pragma.ExecuteNonQuery();
            }

            EnsureTable();
            Migrate();
            LoadCache();

            _debug($"SQLite Ready — {_cache.Count} Player(s) Cached", false);
        }
        catch (Exception ex)
        {
            _debug($"SQLite Initialize Failed: {ex.Message}", true);
            throw;
        }
    }

    public void Dispose()
    {
        if (_conn != null)
        {
            try
            {
                using (var cmd = _conn.CreateCommand())
                {
                    cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
                    cmd.ExecuteNonQuery();
                }
                using (var cmd = _conn.CreateCommand())
                {
                    cmd.CommandText = "PRAGMA journal_mode = DELETE";
                    cmd.ExecuteNonQuery();
                }
            }
            catch { }

            _conn.Close();
            _conn.Dispose();
            _conn = null;
        }
    }

    public (string name, DateTime date, T payload)? GetCached(ulong steamId) =>
        _cache.TryGetValue(steamId, out var v) ? v : null;

    public async Task SaveAsync(ulong steamId, string playerName, DateTime date, T payload)
    {
        _info.FillNullStrings(payload);
        playerName ??= "";
        _cache[steamId] = (playerName, date, payload);

        await _writeLock.WaitAsync();
        try
        {
            await Task.Run(() =>
            {
                if (_conn == null) return;

                var allCols = BuildColumnList();
                var parms   = string.Join(", ", allCols.Select(c => "@" + c));
                var cols    = string.Join(", ", allCols);
                var update  = string.Join(", ", allCols
                    .Where(c => c != PrefsTypeInfo.PkColumn)
                    .Select(c => $"{c} = excluded.{c}"));

                using var cmd = _conn.CreateCommand();
                cmd.CommandText = $@"
                    INSERT INTO {TableName} ({cols}) VALUES ({parms})
                    ON CONFLICT({PrefsTypeInfo.PkColumn}) DO UPDATE SET {update}
                ";

                cmd.Parameters.AddWithValue("@" + PrefsTypeInfo.NameColumn, playerName);
                cmd.Parameters.AddWithValue("@" + PrefsTypeInfo.PkColumn, (long)steamId);
                cmd.Parameters.AddWithValue("@" + PrefsTypeInfo.DateColumn, date.ToString("yyyy-MM-dd HH:mm:ss"));
                foreach (var p in _info.Props)
                    cmd.Parameters.AddWithValue("@" + p.Name, ToSqlite(p.GetValue(payload), p.PropertyType));

                cmd.ExecuteNonQuery();
            });
        }
        catch (Exception ex)
        {
            _debug($"SQLite SaveAsync Error: {ex.Message}", true);
        }
        finally { _writeLock.Release(); }
    }

    public async Task DeleteAsync(ulong steamId)
    {
        _cache.TryRemove(steamId, out _);

        await _writeLock.WaitAsync();
        try
        {
            await Task.Run(() =>
            {
                if (_conn == null) return;

                using var cmd = _conn.CreateCommand();
                cmd.CommandText = $"DELETE FROM {TableName} WHERE {PrefsTypeInfo.PkColumn} = @id";
                cmd.Parameters.AddWithValue("@id", (long)steamId);
                cmd.ExecuteNonQuery();
            });
            _debug($"SQLite: Deleted Player {steamId}", false);
        }
        catch (Exception ex)
        {
            _debug($"SQLite DeleteAsync Error: {ex.Message}", true);
        }
        finally { _writeLock.Release(); }
    }

    public async Task RemoveOldAsync(int days)
    {
        if (days < 1) return;

        var cutoff = DateTime.Now.AddDays(-days);

        var expired = _cache
            .Where(kv => kv.Value.date < cutoff)
            .Select(kv => kv.Key)
            .ToList();

        foreach (var id in expired) _cache.TryRemove(id, out _);

        if (expired.Count > 0)
            _debug($"SQLite Cleanup: Removing {expired.Count} Inactive Player(s) Older Than {days} Day(s)", false);

        if (expired.Count == 0) return;

        await _writeLock.WaitAsync();
        try
        {
            await Task.Run(() =>
            {
                if (_conn == null) return;

                using var cmd = _conn.CreateCommand();
                cmd.CommandText = $"DELETE FROM {TableName} WHERE {PrefsTypeInfo.DateColumn} < @cutoff";
                cmd.Parameters.AddWithValue("@cutoff", cutoff.ToString("yyyy-MM-dd HH:mm:ss"));
                cmd.ExecuteNonQuery();
            });
        }
        catch (Exception ex)
        {
            _debug($"SQLite RemoveOldAsync Error: {ex.Message}", true);
        }
        finally { _writeLock.Release(); }
    }

    private void EnsureTable()
    {
        var cols = new List<string>
        {
            $"{PrefsTypeInfo.NameColumn} TEXT NOT NULL DEFAULT ''",
            $"{PrefsTypeInfo.PkColumn} INTEGER PRIMARY KEY",
            $"{PrefsTypeInfo.DateColumn} TEXT NOT NULL DEFAULT ''",
        };
        cols.AddRange(_info.Props.Select(p => $"{p.Name} {SqlType(p)}"));

        using var cmd = _conn!.CreateCommand();
        cmd.CommandText = $"CREATE TABLE IF NOT EXISTS {TableName} ({string.Join(", ", cols)})";
        cmd.ExecuteNonQuery();
    }

    private void Migrate()
    {
        var existing = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using (var cmd = _conn!.CreateCommand())
        {
            cmd.CommandText = $"PRAGMA table_info({TableName})";
            using var r = cmd.ExecuteReader();
            while (r.Read())
                existing[r.GetString(1)] = r.GetString(2).ToUpper();
        }

        bool IsReserved(string col) =>
            col.Equals(PrefsTypeInfo.NameColumn, StringComparison.OrdinalIgnoreCase) ||
            col.Equals(PrefsTypeInfo.PkColumn,   StringComparison.OrdinalIgnoreCase) ||
            col.Equals(PrefsTypeInfo.DateColumn,  StringComparison.OrdinalIgnoreCase);

        var propByName = new Dictionary<string, PropertyInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in _info.Props) propByName[p.Name] = p;

        bool needsRebuild = false;
        foreach (var col in existing.Keys.ToList())
        {
            if (IsReserved(col)) continue;

            if (!propByName.TryGetValue(col, out var prop))
            {
                _debug($"SQLite Migration: Dropping Removed Column '{col}'", false);
                DropColumn(col);
                existing.Remove(col);
            }
            else if (existing[col] != SqlAffinity(prop))
            {
                _debug($"SQLite Migration: Type Changed For '{col}', Will Rebuild Table", false);
                needsRebuild = true;
            }
        }

        if (needsRebuild)
        {
            RebuildTable();
            return;
        }

        if (!existing.ContainsKey(PrefsTypeInfo.NameColumn))
        {
            _debug($"SQLite Migration: Adding Missing Column '{PrefsTypeInfo.NameColumn}'", false);
            AddColumn(PrefsTypeInfo.NameColumn, "TEXT NOT NULL DEFAULT ''");
        }

        if (!existing.ContainsKey(PrefsTypeInfo.DateColumn))
        {
            _debug($"SQLite Migration: Adding Missing Column '{PrefsTypeInfo.DateColumn}'", false);
            AddColumn(PrefsTypeInfo.DateColumn, "TEXT NOT NULL DEFAULT ''");
        }

        foreach (var p in _info.Props)
        {
            if (!existing.ContainsKey(p.Name))
            {
                _debug($"SQLite Migration: Adding New Column '{p.Name}'", false);
                AddColumn(p.Name, SqlType(p));
            }
        }
    }

    private void DropColumn(string col)
    {
        using var cmd = _conn!.CreateCommand();
        cmd.CommandText = $"ALTER TABLE {TableName} DROP COLUMN {col}";
        cmd.ExecuteNonQuery();
    }

    private void AddColumn(string col, string typeDef)
    {
        using var cmd = _conn!.CreateCommand();
        cmd.CommandText = $"ALTER TABLE {TableName} ADD COLUMN {col} {typeDef}";
        cmd.ExecuteNonQuery();
    }

    private void RebuildTable()
    {
        _debug("SQLite Migration: Rebuilding Table For Type Change...", false);

        var newCols = new List<string>
        {
            $"{PrefsTypeInfo.NameColumn} TEXT NOT NULL DEFAULT ''",
            $"{PrefsTypeInfo.PkColumn} INTEGER PRIMARY KEY",
            $"{PrefsTypeInfo.DateColumn} TEXT NOT NULL DEFAULT ''",
        };
        newCols.AddRange(_info.Props.Select(p => $"{p.Name} {SqlType(p)}"));

        var existingCols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var cmd = _conn!.CreateCommand())
        {
            cmd.CommandText = $"PRAGMA table_info({TableName})";
            using var r = cmd.ExecuteReader();
            while (r.Read()) existingCols.Add(r.GetString(1));
        }

        var keepCols = new List<string> { PrefsTypeInfo.NameColumn, PrefsTypeInfo.PkColumn, PrefsTypeInfo.DateColumn };
        keepCols.AddRange(_info.Props.Select(p => p.Name).Where(n => existingCols.Contains(n)));
        var colList = string.Join(", ", keepCols);

        using var tx = _conn!.BeginTransaction();
        try
        {
            Exec($"ALTER TABLE {TableName} RENAME TO {TableName}_old", tx);
            Exec($"CREATE TABLE {TableName} ({string.Join(", ", newCols)})", tx);
            Exec($"INSERT INTO {TableName} ({colList}) SELECT {colList} FROM {TableName}_old", tx);
            Exec($"DROP TABLE {TableName}_old", tx);
            tx.Commit();
            _debug("SQLite Migration: Table Rebuilt Successfully", false);
        }
        catch (Exception ex)
        {
            _debug($"SQLite RebuildTable Error: {ex.Message}", true);
            tx.Rollback();
        }
    }

    private void Exec(string sql, SqliteTransaction tx)
    {
        using var cmd = _conn!.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = tx;
        cmd.ExecuteNonQuery();
    }

    private void LoadCache()
    {
        using var cmd = _conn!.CreateCommand();
        cmd.CommandText = $"SELECT * FROM {TableName}";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            try
            {
                var steamId = (ulong)r.GetInt64(r.GetOrdinal(PrefsTypeInfo.PkColumn));
                var name = GetStringOr(r, PrefsTypeInfo.NameColumn, "");
                var dateStr = GetStringOr(r, PrefsTypeInfo.DateColumn, "");
                var date = DateTime.TryParse(dateStr, null, System.Globalization.DateTimeStyles.None, out var dt)
                    ? dt : DateTime.MinValue;

                var payload = new T();
                foreach (var p in _info.Props)
                {
                    var ord = TryGetOrdinal(r, p.Name);
                    if (ord < 0 || r.IsDBNull(ord)) continue;
                    try { p.SetValue(payload, FromSqlite(r, ord, p.PropertyType)); } catch { }
                }

                _cache[steamId] = (name, date, payload);
            }
            catch { }
        }
    }

    private List<string> BuildColumnList()
    {
        var list = new List<string> { PrefsTypeInfo.NameColumn, PrefsTypeInfo.PkColumn, PrefsTypeInfo.DateColumn };
        list.AddRange(_info.Props.Select(p => p.Name));
        return list;
    }

    private static string SqlType(PropertyInfo p)
    {
        if (p.PropertyType == typeof(float))    return "REAL NOT NULL DEFAULT 0";
        if (p.PropertyType == typeof(double))   return "REAL NOT NULL DEFAULT 0";
        if (p.PropertyType == typeof(int))      return "INTEGER NOT NULL DEFAULT 0";
        if (p.PropertyType == typeof(long))     return "INTEGER NOT NULL DEFAULT 0";
        if (p.PropertyType == typeof(ulong))    return "INTEGER NOT NULL DEFAULT 0";
        if (p.PropertyType == typeof(bool))     return "INTEGER NOT NULL DEFAULT 0";
        if (p.PropertyType == typeof(DateTime)) return "TEXT NOT NULL DEFAULT ''";
        return "TEXT NOT NULL DEFAULT ''";
    }

    private static string SqlAffinity(PropertyInfo p)
    {
        if (p.PropertyType == typeof(float))    return "REAL";
        if (p.PropertyType == typeof(double))   return "REAL";
        if (p.PropertyType == typeof(int))      return "INTEGER";
        if (p.PropertyType == typeof(long))     return "INTEGER";
        if (p.PropertyType == typeof(ulong))    return "INTEGER";
        if (p.PropertyType == typeof(bool))     return "INTEGER";
        return "TEXT";
    }

    private static object ToSqlite(object? value, Type type)
    {
        if (value == null) return DBNull.Value;
        if (type == typeof(bool))     return (bool)value ? 1L : 0L;
        if (type == typeof(ulong))    return (long)(ulong)value;
        if (type == typeof(float))    return (double)(float)value;
        if (type == typeof(DateTime)) return ((DateTime)value).ToString("yyyy-MM-dd HH:mm:ss");
        return value;
    }

    private static object FromSqlite(SqliteDataReader r, int ord, Type type)
    {
        if (type == typeof(bool))     return r.GetInt64(ord) != 0;
        if (type == typeof(int))      return r.GetInt32(ord);
        if (type == typeof(long))     return r.GetInt64(ord);
        if (type == typeof(ulong))    return (ulong)r.GetInt64(ord);
        if (type == typeof(float))    return (float)r.GetDouble(ord);
        if (type == typeof(double))   return r.GetDouble(ord);
        if (type == typeof(DateTime))
        {
            var s = r.GetString(ord);
            return DateTime.TryParse(s, null, System.Globalization.DateTimeStyles.None, out var dt)
                ? dt : DateTime.MinValue;
        }
        return r.GetString(ord);
    }

    private static string GetStringOr(SqliteDataReader r, string col, string fallback)
    {
        var ord = TryGetOrdinal(r, col);
        return ord >= 0 && !r.IsDBNull(ord) ? r.GetString(ord) : fallback;
    }

    private static int TryGetOrdinal(SqliteDataReader r, string col)
    {
        try { return r.GetOrdinal(col); } catch { return -1; }
    }
}