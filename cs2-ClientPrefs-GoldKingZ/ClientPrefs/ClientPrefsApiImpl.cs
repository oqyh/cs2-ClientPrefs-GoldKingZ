using System.Text.RegularExpressions;
using ClientPrefs_GoldKingZ.Shared;
using CounterStrikeSharp.API.Core;

namespace ClientPrefs_GoldKingZ;

internal sealed class ClientPrefsApiImpl : IClientPrefsApi
{
    private readonly List<IStoreLifecycle> _stores = new();

    internal IEnumerable<IStoreLifecycle> All => _stores;

    public IPrefsStore<T> CreatePrefs<T>(BasePlugin plugin, ClientPrefsOptions options)
        where T : class, new()
    {
        if (plugin == null)  throw new ArgumentNullException(nameof(plugin));
        if (options == null) throw new ArgumentNullException(nameof(options));

        string pluginName = Path.GetFileName(plugin.ModuleDirectory);

        Action<string, bool> debug = (msg, important) =>
        {
            if (!important && !options.PrefsAPI_DebugEnable) return;
            Console.ForegroundColor = ConsoleColor.Magenta;
            Console.WriteLine($"[ClientPrefs_{pluginName}]: {msg}");
            Console.ResetColor();
        };

        MainPlugin.DebugCore($"CreatePrefs<{typeof(T).Name}> Registered By '{pluginName}'");

        CookiesBackend<T>? cookies = null;
        if (options.PrefsAPI_CookiesEnable != PrefsAPI_SaveMode.Disabled)
        {
            var dbPath = Path.Combine(plugin.ModuleDirectory, "cookies", "cookies.db");
            cookies = new CookiesBackend<T>(dbPath, debug);
            cookies.Initialize();
        }

        MySqlBackend<T>? mysql = null;
        if (options.PrefsAPI_MySqlEnable != PrefsAPI_SaveMode.Disabled)
        {
            var tableName = SanitizeTableName(options.PrefsAPI_MySqlTableName ?? $"ClientPrefs_{pluginName}");
            mysql = new MySqlBackend<T>(tableName, options, debug);
            _ = mysql.EnsureTableAsync();
        }

        var store = new PrefsStore<T>(plugin, pluginName, options, cookies, mysql, debug, () => _stores);
        _stores.RemoveAll(s => s.PluginName == pluginName);
        _stores.Add(store);

        return store;
    }

    internal void ForceSaveAllStores()
    {
        MainPlugin.DebugCore("ForceSaveAllStores — Saving All Instances...");
        foreach (var store in _stores)
        {
            try { store.ForceSaveAndClear(); } catch { }
        }
    }

    internal void RefreshAllStores()
    {
        MainPlugin.DebugCore("RefreshAllStores — Saving + Reloading All Instances...");
        foreach (var store in _stores)
        {
            try { store.Refresh(); } catch { }
        }
    }

    internal void UnloadAllStores()
    {
        MainPlugin.DebugCore("UnloadAllStores — Saving + Closing All Instances...");
        foreach (var store in _stores)
        {
            try { store.Unload(); } catch { }
        }
        _stores.Clear();
    }

    private static readonly Regex _sanitizer = new(@"[^A-Za-z0-9_]", RegexOptions.Compiled);
    private static string SanitizeTableName(string raw)
    {
        var clean = _sanitizer.Replace(raw, "_");
        if (clean.Length == 0 || char.IsDigit(clean[0])) clean = "ClientPrefs_" + clean;
        return clean.Length > 64 ? clean.Substring(0, 64) : clean;
    }
}