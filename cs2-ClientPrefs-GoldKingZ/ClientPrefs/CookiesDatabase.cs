using Microsoft.Data.Sqlite;

namespace ClientPrefs_GoldKingZ;

internal sealed class CookiesDatabase
{
    private static readonly object _registryLock = new();
    private static readonly Dictionary<string, CookiesDatabase> _registry =
        new(StringComparer.OrdinalIgnoreCase);

    public SqliteConnection Connection { get; private set; } = null!;
    public SemaphoreSlim    WriteLock  { get; } = new(1, 1);

    private readonly string _path;
    private int  _refCount;
    private bool _open;

    private CookiesDatabase(string path) => _path = path;

    public static CookiesDatabase Acquire(string dbPath, Action<string, bool> debug)
    {
        var full = Path.GetFullPath(dbPath);
        lock (_registryLock)
        {
            if (!_registry.TryGetValue(full, out var db))
            {
                db = new CookiesDatabase(full);
                _registry[full] = db;
            }
            db._refCount++;
            db.Open(debug);
            return db;
        }
    }

    private void Open(Action<string, bool> debug)
    {
        if (_open) return;

        try { SQLitePCL.Batteries.Init(); }
        catch (Exception ex) { debug($"SQLitePCL.Batteries.Init() Failed: {ex.Message}", true); throw; }

        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        debug($"Opening SQLite: {_path}", false);

        Connection = new SqliteConnection($"Data Source={_path}");
        Connection.Open();

        using var pragma = Connection.CreateCommand();
        pragma.CommandText = @"
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous  = NORMAL;
            PRAGMA busy_timeout = 5000;
            PRAGMA cache_size   = -8000;
            PRAGMA temp_store   = MEMORY;
            PRAGMA wal_autocheckpoint = 1;
        ";
        pragma.ExecuteNonQuery();

        _open = true;
    }

    public void Release(Action<string, bool> debug)
    {
        lock (_registryLock)
        {
            if (_refCount > 0) _refCount--;
            if (_refCount > 0) return;

            _registry.Remove(_path);

            if (Connection != null)
            {
                WriteLock.Wait();
                try
                {
                    try
                    {
                        using (var cmd = Connection.CreateCommand())
                        { cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)"; cmd.ExecuteNonQuery(); }
                        using (var cmd = Connection.CreateCommand())
                        { cmd.CommandText = "PRAGMA journal_mode = DELETE"; cmd.ExecuteNonQuery(); }
                    }
                    catch { }

                    Connection.Close();
                    Connection.Dispose();
                    Connection = null!;
                }
                finally { WriteLock.Release(); }
            }

            _open = false;
            debug($"SQLite file closed: {_path}", false);
        }
    }
}
