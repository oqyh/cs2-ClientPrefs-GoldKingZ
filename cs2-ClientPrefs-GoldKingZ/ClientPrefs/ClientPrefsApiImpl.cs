using System.Text.RegularExpressions;
using ClientPrefs_GoldKingZ.Shared;
using CounterStrikeSharp.API.Core;

namespace ClientPrefs_GoldKingZ;

internal sealed class ClientPrefsApiImpl : IClientPrefsApi
{
    private readonly BasePlugin _core;
    private readonly List<IStoreLifecycle> _stores = new();
    private readonly object _storesLock = new();

    internal IEnumerable<IStoreLifecycle> All
    {
        get { lock (_storesLock) return _stores.ToList(); }
    }

    public ClientPrefsApiImpl(BasePlugin core)
    {
        _core = core;
    }

    public IPrefsStore<T> CreatePrefs<T>(BasePlugin plugin, ClientPrefsOptions options)
        where T : class, new()
    {
        if (plugin == null)  throw new ArgumentNullException(nameof(plugin));
        if (options == null) throw new ArgumentNullException(nameof(options));

        string pluginName = Path.GetFileName(plugin.ModuleDirectory);
        string typeName   = typeof(T).Name;

        string rawName = string.IsNullOrWhiteSpace(options.PrefsAPI_TableName)
            ? $"{pluginName}_{typeName}"
            : options.PrefsAPI_TableName!;
        string tableName = SanitizeTableName(rawName);

        string storeKey = $"{pluginName}::{tableName}";

        Action<string, bool> debug = (msg, important) =>
        {
            if (!important && !options.PrefsAPI_DebugEnable) return;
            Console.ForegroundColor = ConsoleColor.Magenta;
            Console.WriteLine($"[ClientPrefs_{pluginName}]: {msg}");
            Console.ResetColor();
        };

        try
        {
            var oldCookiesDir = Path.Combine(plugin.ModuleDirectory, "cookies");
            if (Directory.Exists(oldCookiesDir))
            {
                Directory.Delete(oldCookiesDir, true);
                debug($"Removed Old Cookies Folder (Moved To Core): {oldCookiesDir}", false);
            }
        }
        catch
        {
        }

        MainPlugin.DebugCore($"CreatePrefs<{typeName}> Registered By '{pluginName}' -> table '{tableName}'");

        CookiesBackend<T>? cookies = null;
        if (options.PrefsAPI_CookiesEnable != PrefsAPI_SaveMode.Disabled)
        {
            var dbPath = Path.Combine(_core.ModuleDirectory, pluginName, "cookies.db");
            cookies = new CookiesBackend<T>(dbPath, tableName, debug);
            cookies.Initialize();
        }

        MySqlBackend<T>? mysql = null;
        if (options.PrefsAPI_MySqlEnable != PrefsAPI_SaveMode.Disabled)
        {
            mysql = new MySqlBackend<T>(tableName, options, debug);
            _ = mysql.EnsureTableAsync();
        }

        var store = new PrefsStore<T>(
            plugin, pluginName, storeKey, tableName,
            options, cookies, mysql, debug, () => All);

        lock (_storesLock)
        {
            foreach (var old in _stores.Where(s => s.StoreKey == storeKey).ToList())
            {
                try { old.Unload(); } catch { }
            }
            _stores.RemoveAll(s => s.StoreKey == storeKey);
            _stores.Add(store);
        }

        return store;
    }

    internal void ForceSaveAllStores()
    {
        MainPlugin.DebugCore("ForceSaveAllStores — Saving All Instances...");
        foreach (var store in All)
        {
            try { store.ForceSaveAndClear(); } catch { }
        }
    }

    internal void RefreshAllStores()
    {
        MainPlugin.DebugCore("RefreshAllStores — Saving + Reloading All Instances...");
        foreach (var store in All)
        {
            try { store.Refresh(); } catch { }
        }
    }

    internal void UnloadAllStores()
    {
        MainPlugin.DebugCore("UnloadAllStores — Saving + Closing All Instances...");
        List<IStoreLifecycle> snapshot;
        lock (_storesLock)
        {
            snapshot = _stores.ToList();
            _stores.Clear();
        }
        foreach (var store in snapshot)
        {
            try { store.Unload(); } catch { }
        }
    }

    private static readonly Regex _sanitizer = new(@"[^A-Za-z0-9_]", RegexOptions.Compiled);
    private static string SanitizeTableName(string raw)
    {
        var clean = _sanitizer.Replace(raw, "_");
        if (clean.Length == 0 || char.IsDigit(clean[0])) clean = "ClientPrefs_" + clean;
        return clean.Length > 64 ? clean.Substring(0, 64) : clean;
    }
}