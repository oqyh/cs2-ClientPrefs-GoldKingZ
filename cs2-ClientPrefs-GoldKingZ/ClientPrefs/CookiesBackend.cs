using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using Microsoft.Data.Sqlite;

namespace ClientPrefs_GoldKingZ;

internal sealed class CookiesBackend<T> where T : class, new()
{
    private readonly PrefsTypeInfo _info = PrefsTypeInfo.Of<T>();
    private readonly string _dbPath;
    private readonly string _tableName;
    private readonly ConcurrentDictionary<ulong, (string name, DateTime date, T payload)> _cache = new();
    private readonly Action<string, bool> _debug;

    private CookiesDatabase? _db;
    private volatile bool _disposed;
    private string _upsertSql = "";
    private string _deleteSql = "";
    private SqliteConnection Conn => _db!.Connection;

    public CookiesBackend(string dbPath, string tableName, Action<string, bool> debug)
    {
        _dbPath = dbPath;
        _tableName = tableName;
        _debug = debug;
    }

    public void Initialize()
    {
        try
        {
            _db = CookiesDatabase.Acquire(_dbPath, _debug);

            _db.WriteLock.Wait();
            try
            {
                EnsureTable();
                Migrate();
                BuildCachedSql();
                LoadCache();
                _debug($"SQLite table '{_tableName}' Ready — {_cache.Count} Player(s) Cached", false);
            }
            finally { _db.WriteLock.Release(); }
        }
        catch (Exception ex)
        {
            _debug($"SQLite Initialize Failed ({_tableName}): {ex.Message}", true);
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        var db = _db;
        _db = null;
        db?.Release(_debug);
    }

    public (string name, DateTime date, T payload)? GetCached(ulong steamId) =>
        _cache.TryGetValue(steamId, out var v) ? v : null;

    public Task<bool> SaveAsync(ulong steamId, string playerName, DateTime date, T payload)
    {
        return SaveManyAsync(new List<(ulong, string, DateTime, T)> { (steamId, playerName, date, payload) });
    }

    public async Task<bool> SaveManyAsync(List<(ulong steamId, string playerName, DateTime date, T payload)> rows)
    {
        if (rows.Count == 0) return true;
        if (_disposed) return false;

        foreach (var row in rows)
        {
            _info.FillNullStrings(row.payload);
            _cache[row.steamId] = (row.playerName ?? "", row.date, row.payload);
        }

        var db = _db;
        if (db == null) return false;

        await db.WriteLock.WaitAsync();
        try
        {
            if (_disposed || _db == null) return false;

            await Task.Run(() =>
            {
                if (_disposed || _db == null) return;

                using var tx = _db.Connection.BeginTransaction();
                try
                {
                    foreach (var row in rows)
                    {
                        using var cmd = _db.Connection.CreateCommand();
                        cmd.CommandText = _upsertSql;
                        cmd.Transaction = tx;

                        cmd.Parameters.AddWithValue("@" + PrefsTypeInfo.NameColumn, row.playerName ?? "");
                        cmd.Parameters.AddWithValue("@" + PrefsTypeInfo.PkColumn, (long)row.steamId);
                        cmd.Parameters.AddWithValue("@" + PrefsTypeInfo.DateColumn, row.date.ToString("yyyy-MM-dd HH:mm:ss"));
                        foreach (var p in _info.Props)
                            cmd.Parameters.AddWithValue("@" + p.Name, ToSqlite(p.GetValue(row.payload), p.PropertyType));

                        cmd.ExecuteNonQuery();
                    }
                    tx.Commit();
                }
                catch
                {
                    try { tx.Rollback(); } catch { }
                    throw;
                }
            });

            _debug(rows.Count == 1
                ? $"SQLite: Saved Player {rows[0].playerName} ({rows[0].steamId})"
                : $"SQLite: Saved {rows.Count} Player(s) In One Transaction", false);
            return true;
        }
        catch (Exception ex)
        {
            if (!_disposed)
                _debug($"SQLite SaveManyAsync Error: {ex.Message}", true);
            return false;
        }
        finally { db.WriteLock.Release(); }
    }

    public async Task DeleteAsync(ulong steamId)
    {
        _cache.TryRemove(steamId, out _);

        if (_disposed) return;

        var db = _db;
        if (db == null) return;

        await db.WriteLock.WaitAsync();
        try
        {
            if (_disposed || _db == null) return;

            await Task.Run(() =>
            {
                if (_disposed || _db == null) return;

                using var cmd = _db.Connection.CreateCommand();
                cmd.CommandText = _deleteSql;
                cmd.Parameters.AddWithValue("@id", (long)steamId);
                cmd.ExecuteNonQuery();
            });
            _debug($"SQLite: Deleted Player {steamId}", false);
        }
        catch (Exception ex)
        {
            if (!_disposed)
                _debug($"SQLite DeleteAsync Error: {ex.Message}", true);
        }
        finally { db.WriteLock.Release(); }
    }

    public async Task RemoveOldAsync(int days)
    {
        if (days < 1 || _disposed) return;
        
        var cutoff = DateTime.Now.AddDays(-days);

        var expired = _cache
            .Where(kv => kv.Value.date < cutoff)
            .Select(kv => (steamId: kv.Key, name: kv.Value.name, date: kv.Value.date))
            .ToList();

        foreach (var e in expired) _cache.TryRemove(e.steamId, out _);

        if (expired.Count == 0) return;

        _debug($"SQLite Cleanup: Removing {expired.Count} Inactive Player(s) Older Than {days} Day(s):", false);
        foreach (var e in expired)
            _debug($"SQLite Cleanup: Removed {e.name} ({e.steamId}) — Last Active {e.date:yyyy-MM-dd HH:mm:ss}", false);

        var db = _db;
        if (db == null) return;

        await db.WriteLock.WaitAsync();
        try
        {
            if (_disposed || _db == null) return;

            await Task.Run(() =>
            {
                if (_disposed || _db == null) return;

                using var cmd = _db.Connection.CreateCommand();
                cmd.CommandText = $"DELETE FROM {_tableName} WHERE {PrefsTypeInfo.DateColumn} < @cutoff";
                cmd.Parameters.AddWithValue("@cutoff", cutoff.ToString("yyyy-MM-dd HH:mm:ss"));
                cmd.ExecuteNonQuery();
            });
        }
        catch (Exception ex)
        {
            if (!_disposed)
                _debug($"SQLite RemoveOldAsync Error: {ex.Message}", true);
        }
        finally { db.WriteLock.Release(); }
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

        using var cmd = Conn.CreateCommand();
        cmd.CommandText = $"CREATE TABLE IF NOT EXISTS {_tableName} ({string.Join(", ", cols)})";
        cmd.ExecuteNonQuery();
    }

    private void Migrate()
    {
        var existing = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using (var cmd = Conn.CreateCommand())
        {
            cmd.CommandText = $"PRAGMA table_info({_tableName})";
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
        using var cmd = Conn.CreateCommand();
        cmd.CommandText = $"ALTER TABLE {_tableName} DROP COLUMN {col}";
        cmd.ExecuteNonQuery();
    }

    private void AddColumn(string col, string typeDef)
    {
        using var cmd = Conn.CreateCommand();
        cmd.CommandText = $"ALTER TABLE {_tableName} ADD COLUMN {col} {typeDef}";
        cmd.ExecuteNonQuery();
    }

    private void RebuildTable()
    {
        _debug($"SQLite Migration: Rebuilding Table '{_tableName}' For Type Change...", false);

        var newCols = new List<string>
        {
            $"{PrefsTypeInfo.NameColumn} TEXT NOT NULL DEFAULT ''",
            $"{PrefsTypeInfo.PkColumn} INTEGER PRIMARY KEY",
            $"{PrefsTypeInfo.DateColumn} TEXT NOT NULL DEFAULT ''",
        };
        newCols.AddRange(_info.Props.Select(p => $"{p.Name} {SqlType(p)}"));

        var existingCols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var cmd = Conn.CreateCommand())
        {
            cmd.CommandText = $"PRAGMA table_info({_tableName})";
            using var r = cmd.ExecuteReader();
            while (r.Read()) existingCols.Add(r.GetString(1));
        }

        var keepCols = new List<string> { PrefsTypeInfo.NameColumn, PrefsTypeInfo.PkColumn, PrefsTypeInfo.DateColumn };
        keepCols.AddRange(_info.Props.Select(p => p.Name).Where(n => existingCols.Contains(n)));
        var colList = string.Join(", ", keepCols);

        using var tx = Conn.BeginTransaction();
        try
        {
            Exec($"ALTER TABLE {_tableName} RENAME TO {_tableName}_old", tx);
            Exec($"CREATE TABLE {_tableName} ({string.Join(", ", newCols)})", tx);
            Exec($"INSERT INTO {_tableName} ({colList}) SELECT {colList} FROM {_tableName}_old", tx);
            Exec($"DROP TABLE {_tableName}_old", tx);
            tx.Commit();
            _debug($"SQLite Migration: Table '{_tableName}' Rebuilt Successfully", false);
        }
        catch (Exception ex)
        {
            _debug($"SQLite RebuildTable Error: {ex.Message}", true);
            tx.Rollback();
        }
    }

    private void Exec(string sql, SqliteTransaction tx)
    {
        using var cmd = Conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = tx;
        cmd.ExecuteNonQuery();
    }

    private void BuildCachedSql()
    {
        var allCols = BuildColumnList();
        var parms   = string.Join(", ", allCols.Select(c => "@" + c));
        var cols    = string.Join(", ", allCols);
        var update  = string.Join(", ", allCols
            .Where(c => c != PrefsTypeInfo.PkColumn)
            .Select(c => $"{c} = excluded.{c}"));

        _upsertSql = $"INSERT INTO {_tableName} ({cols}) VALUES ({parms}) ON CONFLICT({PrefsTypeInfo.PkColumn}) DO UPDATE SET {update}";
        _deleteSql = $"DELETE FROM {_tableName} WHERE {PrefsTypeInfo.PkColumn} = @id";
    }

    private void LoadCache()
    {
        using var cmd = Conn.CreateCommand();
        cmd.CommandText = $"SELECT * FROM {_tableName}";
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

    internal static double FloatToStorage(float f)
    {
        return double.Parse(f.ToString("R", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
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
        if (type == typeof(float))    return FloatToStorage((float)value);
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