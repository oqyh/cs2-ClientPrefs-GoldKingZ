using ClientPrefs_GoldKingZ.Shared;
using MySqlConnector;

namespace ClientPrefs_GoldKingZ;

internal sealed class MySqlManager
{
    private sealed class Endpoint
    {
        public string Key = "";
        public string ConnectionString = "";
        public readonly SemaphoreSlim Gate = new(1, 1);
        public volatile bool Healthy;
    }

    private static readonly object _registryLock = new();
    private static readonly Dictionary<string, MySqlManager> _registry = new();

    private readonly List<Endpoint> _endpoints = new();
    private readonly string _key;
    private int _refCount;

    private MySqlManager(string key, IReadOnlyList<MySqlServer> servers, int connectionTimeout)
    {
        _key = key;
        foreach (var s in servers)
        {
            _endpoints.Add(new Endpoint
            {
                Key = $"{s.Server}:{s.Port}/{s.Database}",
                ConnectionString = new MySqlConnectionStringBuilder
                {
                    Server   = s.Server,
                    Port     = (uint)s.Port,
                    Database = s.Database,
                    UserID   = s.Username,
                    Password = s.Password,
                    ConnectionTimeout = (uint)connectionTimeout,
                    Pooling = true, MinimumPoolSize = 0, MaximumPoolSize = 100,
                }.ConnectionString
            });
        }
    }

    public static MySqlManager Acquire(ClientPrefsOptions opts)
    {
        var servers = opts.PrefsAPI_MySqlConfig.EffectiveServers;
        var key = string.Join("|", servers.Select(s => $"{s.Server}:{s.Port}/{s.Database}@{s.Username}"));

        lock (_registryLock)
        {
            if (!_registry.TryGetValue(key, out var mgr))
            {
                mgr = new MySqlManager(key, servers, opts.PrefsAPI_MySqlConnectionTimeout);
                _registry[key] = mgr;
                MainPlugin.DebugCore($"MySqlManager Created For [{key}] ({mgr._endpoints.Count} Server(s), All Servers Mode)");
            }
            mgr._refCount++;
            return mgr;
        }
    }

    public void Release()
    {
        lock (_registryLock)
        {
            if (_refCount > 0) _refCount--;
            if (_refCount > 0) return;

            _registry.Remove(_key);
            MainPlugin.DebugCore($"MySqlManager Released For [{_key}] — Clearing Its Pools");

            foreach (var e in _endpoints)
            {
                try { MySqlConnection.ClearPool(new MySqlConnection(e.ConnectionString)); } catch { }
            }
        }
    }

    public async Task<List<(string serverKey, MySqlConnection conn)>> GetAllConnectionsAsync(
        int timeout, int attempts, int delay, Action<string, bool> debug)
    {
        if (_endpoints.Count == 0) return new List<(string, MySqlConnection)>();

        var tasks = _endpoints.Select(e => OpenEndpointAsync(e, timeout, attempts, delay, debug)).ToArray();
        var results = await Task.WhenAll(tasks);

        var list = new List<(string, MySqlConnection)>();
        foreach (var (key, conn) in results)
        {
            if (conn != null) list.Add((key, conn));
        }
        return list;
    }

    private static async Task<(string key, MySqlConnection? conn)> OpenEndpointAsync(
        Endpoint e, int timeout, int attempts, int delay, Action<string, bool> debug)
    {
        if (e.Healthy)
        {
            var quick = await TryOpenOnceAsync(e, timeout, debug);
            if (quick != null) return (e.Key, quick);
            e.Healthy = false;
        }

        await e.Gate.WaitAsync();
        try
        {
            if (e.Healthy)
            {
                var quick = await TryOpenOnceAsync(e, timeout, debug);
                if (quick != null) return (e.Key, quick);
                e.Healthy = false;
            }

            for (int attempt = 0; attempt < attempts; attempt++)
            {
                if (attempt > 0) await Task.Delay(TimeSpan.FromSeconds(delay));

                var conn = await TryOpenOnceAsync(e, timeout, debug);
                if (conn != null)
                {
                    e.Healthy = true;
                    return (e.Key, conn);
                }
            }

            debug($"MySQL [{e.Key}] all connection attempts exhausted ({attempts} attempts)", true);
            return (e.Key, null);
        }
        finally { e.Gate.Release(); }
    }

    private static async Task<MySqlConnection?> TryOpenOnceAsync(Endpoint e, int timeout, Action<string, bool> debug)
    {
        MySqlConnection? conn = null;
        try
        {
            conn = new MySqlConnection(e.ConnectionString);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeout));
            await conn.OpenAsync(cts.Token);
            return conn;
        }
        catch (Exception ex)
        {
            try { conn?.Dispose(); } catch { }
            debug($"MySQL connection failed ({e.Key}): {ex.Message}", true);
            return null;
        }
    }
}