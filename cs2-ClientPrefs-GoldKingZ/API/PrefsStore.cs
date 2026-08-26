using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using ClientPrefs_GoldKingZ.Shared;
using CounterStrikeSharp.API.Core;

namespace ClientPrefs_GoldKingZ;

internal sealed class PrefsStore<T> : IPrefsStore<T>, IStoreLifecycle where T : class, new()
{
    private sealed class Envelope
    {
        public ulong    SteamId;
        public string   PlayerName = "";
        public DateTime Date;
        public T        Payload = null!;
        public T        Baseline = null!;
        public bool     LoadedFromStorage;
        public bool     HasBeenMutated;

        public readonly object MergeGate = new();
        public bool     CookiesApplied;
        public DateTime CookiesDate = DateTime.MinValue;

        public volatile bool StorageReady;

        public bool GoneRetryUsed;
    }

    private readonly PrefsTypeInfo _info = PrefsTypeInfo.Of<T>();
    private readonly ClientPrefsOptions _opts;
    private readonly CookiesBackend<T>? _cookies;
    private readonly MySqlBackend<T>?   _mysql;
    private readonly Action<string, bool> _debug;
    private readonly Func<IEnumerable<IStoreLifecycle>> _allStores;

    private readonly ConcurrentDictionary<int, Envelope> _bySlot = new();

    public string PluginName { get; }
    public string StoreKey   { get; }
    public string TableName  { get; }
    public BasePlugin Plugin { get; }

    public PrefsStore(BasePlugin plugin, string pluginName, string storeKey, string tableName,
                      ClientPrefsOptions opts, CookiesBackend<T>? cookies, MySqlBackend<T>? mysql,
                      Action<string, bool> debug, Func<IEnumerable<IStoreLifecycle>> allStores)
    {
        Plugin = plugin;
        PluginName = pluginName;
        StoreKey = storeKey;
        TableName = tableName;
        _opts = opts;
        _cookies = cookies;
        _mysql = mysql;
        _debug = debug;
        _allStores = allStores;
    }

    private bool CookiesOn => _opts.PrefsAPI_CookiesEnable != PrefsAPI_SaveMode.Disabled && _cookies != null;
    private bool MySqlOn   => _opts.PrefsAPI_MySqlEnable   != PrefsAPI_SaveMode.Disabled && _mysql   != null;

    private T Clone(T src) => (T)_info.Clone(src);

    private string DumpValues(T payload) =>
        string.Join(", ", _info.Props.Select(p => $"{p.Name}={p.GetValue(payload)}"));

    private static CCSPlayerController? FindPlayerBySlot(int slot)
    {
        try
        {
            var p = CounterStrikeSharp.API.Utilities.GetPlayerFromSlot(slot);
            return p != null && p.IsValid && !p.IsBot && !p.IsHLTV ? p : null;
        }
        catch { return null; }
    }

    private void CopyInto(T src, T dst)
    {
        foreach (var p in _info.Props)
        {
            var v = p.GetValue(src);
            if (v != null) p.SetValue(dst, v);
        }
    }

    private bool IsDirty(Envelope env)
    {
        if (env.HasBeenMutated || env.LoadedFromStorage) return true;
        if (_info.DiffersFromBaseline(env.Payload, env.Baseline))
        {
            env.HasBeenMutated = true;
            return true;
        }
        return false;
    }

    private static void MarkClean(Envelope env, T savedSnapshot)
    {
        env.Baseline = savedSnapshot;
        env.LoadedFromStorage = false;
        env.HasBeenMutated = false;
    }

    private void MarkMutatedIfChanged(Envelope env)
    {
        if (!env.HasBeenMutated && _info.DiffersFromBaseline(env.Payload, env.Baseline))
            env.HasBeenMutated = true;
    }

    private void RefreshName(Envelope env, int slot)
    {
        try
        {
            var live = FindPlayerBySlot(slot);
            if (live != null && !string.IsNullOrEmpty(live.PlayerName))
                env.PlayerName = live.PlayerName;
        }
        catch { }
    }

    private Envelope? FindBySteamId(ulong steamId, out int slot)
    {
        foreach (var kv in _bySlot)
        {
            if (kv.Value.SteamId == steamId)
            {
                slot = kv.Key;
                return kv.Value;
            }
        }
        slot = -1;
        return null;
    }

    private Dictionary<string, object?> ToDict(T payload)
    {
        var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in _info.Props)
            dict[p.Name] = p.GetValue(payload);
        return dict;
    }

    private static TResult BuildResult<TResult>(Dictionary<string, object?> values, PropertyInfo[] rProps) where TResult : class, new()
    {
        var data = new TResult();
        foreach (var rp in rProps)
        {
            if (!values.TryGetValue(rp.Name, out var v) || v == null) continue;
            try { rp.SetValue(data, Convert.ChangeType(v, rp.PropertyType, CultureInfo.InvariantCulture)); }
            catch { }
        }
        return data;
    }

    private async Task<(string name, DateTime date, T payload)?> LoadNewestStoredAsync(ulong steamId)
    {
        T? payload = null;
        string name = "";
        DateTime date = DateTime.MinValue;

        if (CookiesOn)
        {
            var cached = _cookies!.GetCached(steamId);
            if (cached.HasValue)
            {
                payload = Clone(cached.Value.payload);
                name = cached.Value.name;
                date = cached.Value.date;
            }
        }

        if (MySqlOn)
        {
            var row = await _mysql!.LoadAsync(steamId, fast: true);
            if (row.HasValue && row.Value.date > date)
            {
                payload = Clone(row.Value.payload);
                date = row.Value.date;
            }
        }

        return payload == null ? null : (name, date, payload);
    }

    private async Task<bool> PersistSnapshotAsync(bool saveCookie, bool saveMySql, ulong steamId, string name, DateTime date, T snapshot)
    {
        bool cookieOk = !saveCookie || await _cookies!.SaveAsync(steamId, name, date, snapshot);
        bool mysqlOk  = !saveMySql  || await _mysql!.SaveAsync  (steamId, name, date, snapshot);
        return cookieOk && mysqlOk;
    }

    private async Task<bool> DeleteFromBackendsAsync(ulong steamId)
    {
        bool ok = true;
        if (CookiesOn) await _cookies!.DeleteAsync(steamId);
        if (MySqlOn)   ok = await _mysql!.DeleteAsync(steamId);
        return ok;
    }

    private void DeliverDone(Action<bool>? done, bool ok)
    {
        if (done == null) return;
        CounterStrikeSharp.API.Server.NextFrame(() =>
        {
            try { done(ok); } catch (Exception ex) { _debug($"done-callback error: {ex.Message}", true); }
        });
    }

    private void RunOnAllStores(Action<IStoreLifecycle, Action<bool>> op, Action<bool>? done)
    {
        var stores = _allStores().ToList();
        if (stores.Count == 0) { DeliverDone(done, false); return; }

        int remaining = stores.Count;
        bool allOk = true;

        foreach (var store in stores)
        {
            try
            {
                op(store, ok =>
                {
                    if (!ok) allOk = false;
                    if (Interlocked.Decrement(ref remaining) == 0) DeliverDone(done, allOk);
                });
            }
            catch
            {
                allOk = false;
                if (Interlocked.Decrement(ref remaining) == 0) DeliverDone(done, allOk);
            }
        }
    }

    public bool IsPlayerReady(int slot, ulong steamId) =>
        _bySlot.TryGetValue(slot, out var env) && env.SteamId == steamId && env.StorageReady;

    private bool AllStoresReady(int slot, ulong steamId)
    {
        foreach (var store in _allStores())
            if (!store.IsPlayerReady(slot, steamId)) return false;
        return true;
    }

    public void OnPlayerLoaded(CCSPlayerController player, Action<CCSPlayerController, T> callback, bool All_Plugins = false)
    {
        if (player == null || !player.IsValid || player.IsBot || player.IsHLTV) return;

        var slot = player.Slot;
        var steamId = player.SteamID;

        void TryRun(int attempt)
        {
            CounterStrikeSharp.API.Server.NextFrame(() =>
            {
                try
                {
                    var live = FindPlayerBySlot(slot);
                    if (live == null || live.SteamID != steamId) return;

                    bool ready = All_Plugins ? AllStoresReady(slot, steamId) : IsPlayerReady(slot, steamId);
                    if (ready)
                    {
                        if (_bySlot.TryGetValue(slot, out var env) && env.SteamId == steamId)
                        {
                            callback(live, env.Payload);
                            MarkMutatedIfChanged(env);
                        }
                        return;
                    }

                    if (attempt < 3000) TryRun(attempt + 1);
                    else _debug($"OnPlayerLoaded slot {slot} — gave up waiting for load", true);
                }
                catch (Exception ex)
                {
                    _debug($"OnPlayerLoaded callback error: {ex.Message}", true);
                }
            });
        }

        TryRun(0);
    }

    public Task LoadPlayerAsync(CCSPlayerController? player)
    {
        if (player == null || !player.IsValid || player.IsBot || player.IsHLTV) return Task.CompletedTask;

        var slot = player.Slot;
        var steamId = player.SteamID;
        var playerName = player.PlayerName ?? "";

        _debug($"LOAD-DEBUG: LoadPlayerAsync start — {playerName} slot {slot} steamId {steamId}", false);

        foreach (var kv in _bySlot)
        {
            if (kv.Key != slot && kv.Value.SteamId == steamId)
            {
                if (_bySlot.TryRemove(kv.Key, out var moved))
                {
                    _bySlot[slot] = moved;
                    _debug($"LOAD-DEBUG: moved envelope from slot {kv.Key} to slot {slot}, values: [{DumpValues(moved.Payload)}]", false);
                }
                break;
            }
        }

        if (_bySlot.TryGetValue(slot, out var existing))
        {
            if (existing.SteamId != steamId)
            {
                _debug($"LOAD-DEBUG: slot {slot} had different player ({existing.SteamId}), removing", false);
                _bySlot.TryRemove(slot, out _);
            }
            else if (!_opts.PrefsAPI_ReloadOnReconnect)
            {
                existing.PlayerName = playerName;
                existing.GoneRetryUsed = false;
                _debug($"LOAD-DEBUG: {playerName} already in memory, KEEPING EXISTING values: [{DumpValues(existing.Payload)}] (dirty={existing.HasBeenMutated || existing.LoadedFromStorage})", false);
                return Task.CompletedTask;
            }
            else
            {
                _debug($"LOAD-DEBUG: {playerName} reconnected, ReloadOnReconnect=true, discarding old values: [{DumpValues(existing.Payload)}]", false);
                _bySlot.TryRemove(slot, out _);
            }
        }

        var env = new Envelope
        {
            SteamId = steamId,
            PlayerName = playerName,
            Date = DateTime.Now,
            Payload = new T()
        };

        _debug($"LOAD-DEBUG: new envelope with defaults: [{DumpValues(env.Payload)}]", false);

        bool cookiesActive = CookiesOn;
        bool mysqlActive   = MySqlOn;

        if (cookiesActive)
        {
            var cached = _cookies!.GetCached(steamId);
            if (cached.HasValue)
            {
                CopyInto(cached.Value.payload, env.Payload);
                env.Date = cached.Value.date;
                env.CookiesDate = cached.Value.date;
                env.CookiesApplied = true;
                env.LoadedFromStorage = true;
                _debug($"LOAD-DEBUG: cookies applied (date {cached.Value.date:yyyy-MM-dd HH:mm:ss}): [{DumpValues(env.Payload)}]", false);
            }
            else
            {
                _debug($"LOAD-DEBUG: no cookies row for {steamId}", false);
            }
        }

        if (!mysqlActive)
        {
            env.StorageReady = true;
            _debug($"LOAD-DEBUG: {playerName} storage READY ({(cookiesActive ? "cookies applied, mysql disabled" : "cookies disabled + mysql disabled — memory only")})", false);
        }
        else
        {
            LoadNotifier.RegisterPending(slot, steamId);
            _debug($"LOAD-DEBUG: {playerName} storage NOT ready yet — waiting for MySQL stage (cookies {(cookiesActive ? "applied" : "disabled")})", false);
        }

        env.Baseline = Clone(env.Payload);
        _bySlot[slot] = env;
        _debug($"Player {playerName} (slot {slot}, {steamId}) ready", false);

        if (mysqlActive)
            _ = Task.Run(() => ApplyMySqlStage(slot, env, steamId, playerName));

        return Task.CompletedTask;
    }

    private async Task ApplyMySqlStage(int slot, Envelope env, ulong steamId, string playerName)
    {
        try
        {
            var row = await _mysql!.LoadAsync(steamId);
            if (!row.HasValue)
            {
                _debug($"MySQL stage: no row / no connection for {playerName}, keeping current values", false);
                return;
            }

            _debug($"MYSQL-DEBUG: row loaded for {playerName} (date {row.Value.date:yyyy-MM-dd HH:mm:ss}): [{DumpValues(row.Value.payload)}]", false);

            lock (env.MergeGate)
            {
                if (!_bySlot.TryGetValue(slot, out var current))
                {
                    _debug($"MYSQL-DEBUG: slot {slot} has NO envelope anymore — discarding row", false);
                    return;
                }
                if (!ReferenceEquals(current, env))
                {
                    _debug($"MYSQL-DEBUG: slot {slot} envelope was REPLACED — discarding row", false);
                    return;
                }
                if (env.HasBeenMutated || _info.DiffersFromBaseline(env.Payload, env.Baseline))
                {
                    _debug($"MYSQL-DEBUG: {playerName} already changed values, keeping current: [{DumpValues(env.Payload)}]", false);
                    return;
                }
                if (env.CookiesApplied && row.Value.date < env.CookiesDate)
                {
                    _debug($"MYSQL-DEBUG: row older than cookies ({row.Value.date:yyyy-MM-dd HH:mm:ss} < {env.CookiesDate:yyyy-MM-dd HH:mm:ss}) — keeping cookies values: [{DumpValues(env.Payload)}]", false);
                    return;
                }

                _debug($"MYSQL-DEBUG: values BEFORE merge: [{DumpValues(env.Payload)}]", false);
                CopyInto(row.Value.payload, env.Payload);
                env.Date = row.Value.date;
                env.LoadedFromStorage = true;
                env.Baseline = Clone(env.Payload);
                _debug($"MYSQL-DEBUG: values AFTER merge: [{DumpValues(env.Payload)}]", false);
                _debug($"MySQL stage: {playerName} values applied (overrides sql/defaults)", false);
            }
        }
        catch (Exception ex)
        {
            _debug($"MySQL stage error: {ex.Message}", true);
        }
        finally
        {
            env.StorageReady = true;
            LoadNotifier.MarkReady(slot, steamId);
            _debug($"LOAD-DEBUG: {playerName} storage READY — TryGetValue unlocked", false);
        }
    }

    private bool TryGetReadyEnvelope(int slot, out Envelope env)
    {
        if (_bySlot.TryGetValue(slot, out env!))
        {
            if (env.StorageReady) return true;
            _debug($"TryGetValue slot {slot} — storage not ready yet, telling player to wait", false);
            LoadNotifier.ShowWaitMessage(FindPlayerBySlot(slot));
        }
        env = null!;
        return false;
    }

    public bool TryGetValue(CCSPlayerController player, out T data)
    {
        if (player == null || !player.IsValid || player.IsBot || player.IsHLTV) { data = null!; return false; }
        return TryGetValue(player.Slot, out data);
    }

    public bool TryGetValue(int slot, out T data)
    {
        if (TryGetReadyEnvelope(slot, out var e)) { data = e.Payload; return true; }
        data = null!;
        return false;
    }

    public bool TryGetValue(CCSPlayerController player, Action<T> action)
    {
        if (player == null || !player.IsValid || player.IsBot || player.IsHLTV) return false;
        return TryGetValue(player.Slot, action);
    }

    public bool TryGetValue(int slot, Action<T> action)
    {
        if (!TryGetReadyEnvelope(slot, out var e)) return false;
        action(e.Payload);
        MarkMutatedIfChanged(e);
        return true;
    }

    public bool TryGetValue(ulong steamId, out T data)
    {
        var env = FindBySteamId(steamId, out var slot);
        if (env == null) { data = null!; return false; }
        return TryGetValue(slot, out data);
    }

    public bool TryGetValue(ulong steamId, Action<T> action)
    {
        var env = FindBySteamId(steamId, out var slot);
        if (env == null) return false;
        return TryGetValue(slot, action);
    }

    public void FetchPlayer(ulong steamId, Action<T?> callback)
    {
        void Deliver(T? result) => CounterStrikeSharp.API.Server.NextFrame(() =>
        {
            try { callback(result); } catch (Exception ex) { _debug($"FetchPlayer callback error: {ex.Message}", true); }
        });

        var env = FindBySteamId(steamId, out _);
        if (env != null && env.StorageReady) { Deliver(env.Payload); return; }
        if (!CookiesOn && !MySqlOn) { Deliver(null); return; }

        _ = Task.Run(async () =>
        {
            T? result = null;
            try
            {
                var row = await LoadNewestStoredAsync(steamId);
                if (row != null)
                {
                    result = row.Value.payload;
                    _debug($"FetchPlayer: row for {steamId} (date {row.Value.date:yyyy-MM-dd HH:mm:ss})", false);
                }
                else _debug($"FetchPlayer: no stored data for {steamId}", false);
            }
            catch (Exception ex) { _debug($"FetchPlayer error: {ex.Message}", true); }
            Deliver(result);
        });
    }

    public void FetchPlayer<TResult>(ulong steamId, Action<List<PrefsSearchResult<TResult>>> callback, bool All_Plugins = false) where TResult : class, new()
    {
        var targets = All_Plugins ? _allStores().ToList() : new List<IStoreLifecycle> { this };
        var rProps = typeof(TResult).GetProperties(BindingFlags.Public | BindingFlags.Instance);

        _ = Task.Run(async () =>
        {
            var list = new List<PrefsSearchResult<TResult>>();
            foreach (var store in targets)
            {
                try
                {
                    var row = await store.GetRawAsync(steamId);
                    if (row == null) continue;

                    list.Add(new PrefsSearchResult<TResult>
                    {
                        PlayerSteamID = steamId,
                        PlayerName    = row.Value.name,
                        DateAndTime   = row.Value.date,
                        Plugin        = store.PluginName,
                        Table         = store.TableName,
                        Data          = BuildResult<TResult>(row.Value.values, rProps),
                    });
                }
                catch (Exception ex) { _debug($"FetchPlayer<TResult> store '{store.PluginName}' error: {ex.Message}", true); }
            }

            _debug($"FetchPlayer<TResult> {steamId} (All_Plugins={All_Plugins}) -> {list.Count} plugin row(s)", false);
            CounterStrikeSharp.API.Server.NextFrame(() =>
            {
                try { callback(list); } catch (Exception ex) { _debug($"FetchPlayer<TResult> callback error: {ex.Message}", true); }
            });
        });
    }

    public async Task<(string name, DateTime date, Dictionary<string, object?> values)?> GetRawAsync(ulong steamId)
    {
        var env = FindBySteamId(steamId, out _);
        if (env != null && env.StorageReady)
            return (env.PlayerName, env.Date, ToDict(Clone(env.Payload)));

        try
        {
            var row = await LoadNewestStoredAsync(steamId);
            if (row == null) return null;
            return (row.Value.name, row.Value.date, ToDict(row.Value.payload));
        }
        catch (Exception ex) { _debug($"GetRawAsync error: {ex.Message}", true); return null; }
    }
    private async Task<List<(ulong steamId, string name, DateTime date, T payload)>> SearchCoreAsync(string fieldName, object value)
    {
        var empty = new List<(ulong, string, DateTime, T)>();

        bool byName = fieldName.Equals(PrefsTypeInfo.NameColumn, StringComparison.OrdinalIgnoreCase);
        var prop = byName ? null : _info.Props.FirstOrDefault(p => p.Name.Equals(fieldName, StringComparison.OrdinalIgnoreCase));

        if (!byName && prop == null)
        {
            _debug($"SearchByField: unknown field '{fieldName}' (valid: {PrefsTypeInfo.NameColumn}, {string.Join(", ", _info.Props.Select(p => p.Name))})", false);
            return empty;
        }

        object converted;
        try
        {
            converted = byName ? value?.ToString() ?? ""
                               : Convert.ChangeType(value, prop!.PropertyType, CultureInfo.InvariantCulture);
        }
        catch
        {
            _debug($"SearchByField: value '{value}' cannot convert to {(byName ? "string" : prop!.PropertyType.Name)}", false);
            return empty;
        }

        var merged = new Dictionary<ulong, (string name, DateTime date, T payload)>();

        bool Matches(T payload, string name)
        {
            if (byName) return string.Equals(name, (string)converted, StringComparison.OrdinalIgnoreCase);
            var v = prop!.GetValue(payload);
            return v != null && v.Equals(converted);
        }

        try
        {
            if (CookiesOn)
            {
                foreach (var (steamId, name, date, payload) in _cookies!.SearchCached(Matches))
                    if (!merged.TryGetValue(steamId, out var ex) || date > ex.date)
                        merged[steamId] = (name, date, Clone(payload));
            }

            if (MySqlOn)
            {
                object sqlValue = converted;
                if (!byName && prop!.PropertyType == typeof(float))
                    sqlValue = CookiesBackend<T>.FloatToStorage((float)converted);

                string column = byName ? PrefsTypeInfo.NameColumn : prop!.Name;
                foreach (var (steamId, name, date, payload) in await _mysql!.SearchAsync(column, sqlValue, fast: true))
                    if (!merged.TryGetValue(steamId, out var ex) || date > ex.date)
                        merged[steamId] = (name, date, payload);
            }

            foreach (var kv in _bySlot)
            {
                var env = kv.Value;
                if (!env.StorageReady) continue;

                if (Matches(env.Payload, env.PlayerName))
                    merged[env.SteamId] = (env.PlayerName, env.Date, Clone(env.Payload));
                else
                    merged.Remove(env.SteamId);
            }
        }
        catch (Exception ex) { _debug($"SearchByField error: {ex.Message}", true); }

        return merged.Select(kv => (kv.Key, kv.Value.name, kv.Value.date, kv.Value.payload)).ToList();
    }

    public void SearchByField(string fieldName, object value, Action<List<PrefsSearchResult<T>>> callback)
    {
        _ = Task.Run(async () =>
        {
            var rows = await SearchCoreAsync(fieldName, value);
            var list = rows.Select(r => new PrefsSearchResult<T>
            {
                PlayerSteamID = r.steamId,
                PlayerName    = r.name,
                DateAndTime   = r.date,
                Plugin        = PluginName,
                Data          = r.payload,
            }).ToList();

            _debug($"SearchByField '{fieldName}' = '{value}' -> {list.Count} match(es)", false);
            CounterStrikeSharp.API.Server.NextFrame(() =>
            {
                try { callback(list); } catch (Exception ex) { _debug($"SearchByField callback error: {ex.Message}", true); }
            });
        });
    }

    public void SearchByField<TResult>(string fieldName, object value, Action<List<PrefsSearchResult<TResult>>> callback, bool All_Plugins = false) where TResult : class, new()
    {
        var targets = All_Plugins ? _allStores().ToList() : new List<IStoreLifecycle> { this };
        var rProps = typeof(TResult).GetProperties(BindingFlags.Public | BindingFlags.Instance);

        _ = Task.Run(async () =>
        {
            var list = new List<PrefsSearchResult<TResult>>();
            foreach (var store in targets)
            {
                try
                {
                    var rows = await store.SearchRawAsync(fieldName, value);
                    foreach (var (steamId, name, date, values) in rows)
                        list.Add(new PrefsSearchResult<TResult>
                        {
                            PlayerSteamID = steamId,
                            PlayerName    = name,
                            DateAndTime   = date,
                            Plugin        = store.PluginName,
                            Table         = store.TableName,
                            Data          = BuildResult<TResult>(values, rProps),
                        });
                }
                catch (Exception ex) { _debug($"SearchByField<TResult> store '{store.PluginName}' error: {ex.Message}", true); }
            }

            _debug($"SearchByField<TResult> '{fieldName}' = '{value}' (All_Plugins={All_Plugins}) -> {list.Count} match(es)", false);
            CounterStrikeSharp.API.Server.NextFrame(() =>
            {
                try { callback(list); } catch (Exception ex) { _debug($"SearchByField<TResult> callback error: {ex.Message}", true); }
            });
        });
    }

    public async Task<List<(ulong steamId, string name, DateTime date, Dictionary<string, object?> values)>> SearchRawAsync(string column, object value)
    {
        var rows = await SearchCoreAsync(column, value);
        return rows.Select(r => (r.steamId, r.name, r.date, ToDict(r.payload))).ToList();
    }

    public void ModifyAndSave(ulong steamId, Action<T> modify, Action<bool>? done = null)
    {
        var envOnline = FindBySteamId(steamId, out var onlineSlot);
        if (envOnline != null && envOnline.StorageReady)
        {
            CounterStrikeSharp.API.Server.NextFrame(() =>
            {
                try
                {
                    modify(envOnline.Payload);
                    MarkMutatedIfChanged(envOnline);
                    ForceSaveSlot(onlineSlot);
                    _debug($"ModifyAndSave: {steamId} is ONLINE — modified live values and force-saved", false);
                    DeliverDone(done, true);
                }
                catch (Exception ex)
                {
                    _debug($"ModifyAndSave online-modify error: {ex.Message}", true);
                    DeliverDone(done, false);
                }
            });
            return;
        }

        if (!CookiesOn && !MySqlOn)
        {
            _debug($"ModifyAndSave: no storage enabled — nothing to modify for {steamId}", true);
            DeliverDone(done, false);
            return;
        }

        _ = Task.Run(async () =>
        {
            bool ok = false;
            try
            {
                var row = await LoadNewestStoredAsync(steamId);
                T payload = row?.payload ?? new T();
                string name = row?.name ?? "";
                if (row == null) _debug($"ModifyAndSave: no existing row for {steamId} — starting from defaults", false);

                modify(payload);

                var now = DateTime.Now;
                bool cookieOk = !CookiesOn || await _cookies!.SaveAsync(steamId, name, now, payload);
                bool mysqlOk  = !MySqlOn  || await _mysql!.SaveAsync  (steamId, name, now, payload, fast: true);

                ok = (CookiesOn && cookieOk) || (MySqlOn && mysqlOk);
                _debug($"ModifyAndSave: {steamId} OFFLINE row modified and saved (cookies={cookieOk}, mysql={mysqlOk})", false);
            }
            catch (Exception ex) { _debug($"ModifyAndSave error: {ex.Message}", true); }
            DeliverDone(done, ok);
        });
    }

    public void ForceSave(CCSPlayerController player, Action<bool>? done = null, bool All_Plugins = false)
    {
        if (player == null || !player.IsValid || player.IsBot || player.IsHLTV) { DeliverDone(done, false); return; }
        ForceSave(player.Slot, done, All_Plugins);
    }

    public void ForceSave(int slot, Action<bool>? done = null, bool All_Plugins = false)
    {
        if (All_Plugins)
        {
            _debug($"ForceSave slot {slot} (All_Plugins = true)", false);
            RunOnAllStores((store, cb) => store.ForceSaveSlot(slot, cb), done);
        }
        else ForceSaveSlot(slot, done);
    }

    public void ForceSave(ulong steamId, Action<bool>? done = null, bool All_Plugins = false)
    {
        var env = FindBySteamId(steamId, out var slot);
        if (env == null)
        {
            _debug($"ForceSave steamId {steamId} — not in memory, nothing to save", false);
            DeliverDone(done, false);
            return;
        }
        ForceSave(slot, done, All_Plugins);
    }

    public void ForceSaveSlot(int slot, Action<bool>? done = null)
    {
        if (!_bySlot.TryGetValue(slot, out var e)) { DeliverDone(done, false); return; }
        if (!IsDirty(e)) { DeliverDone(done, true); return; }

        e.Date = DateTime.Now;
        RefreshName(e, slot);
        _debug($"ForceSave slot {slot}", false);

        bool saveCookie = CookiesOn;
        bool saveMySql  = MySqlOn;
        var steamId = e.SteamId;
        var name = e.PlayerName;
        var date = e.Date;
        var snapshot = Clone(e.Payload);

        _ = Task.Run(async () =>
        {
            bool ok = false;
            try
            {
                ok = await PersistSnapshotAsync(saveCookie, saveMySql, steamId, name, date, snapshot);
                if (ok) MarkClean(e, snapshot);
                else _debug($"ForceSave slot {slot} failed — keeping values in memory, will retry on next save", true);
            }
            catch (Exception ex) { _debug($"ForceSave error: {ex.Message}", true); }
            DeliverDone(done, ok);
        });
    }

    public void DropPlayer(CCSPlayerController player, Action<bool>? done = null, bool All_Plugins = false)
    {
        if (player == null || !player.IsValid || player.IsBot || player.IsHLTV) { DeliverDone(done, false); return; }
        DropPlayer(player.Slot, done, All_Plugins);
    }

    public void DropPlayer(int slot, Action<bool>? done = null, bool All_Plugins = false)
    {
        if (All_Plugins)
        {
            _debug($"DropPlayer slot {slot} (All_Plugins = true)", false);
            RunOnAllStores((store, cb) => store.DropSlot(slot, cb), done);
        }
        else DropSlot(slot, done);
    }

    public void DropPlayer(ulong steamId, Action<bool>? done = null, bool All_Plugins = false)
    {
        if (All_Plugins)
        {
            _debug($"DropPlayer steamId {steamId} (All_Plugins = true)", false);
            RunOnAllStores((store, cb) => store.DropSteam(steamId, cb), done);
        }
        else DropSteam(steamId, done);
    }

    public void DropSteam(ulong steamId, Action<bool>? done = null)
    {
        var env = FindBySteamId(steamId, out var slot);
        if (env != null) { DropSlot(slot, done); return; }

        if (!CookiesOn && !MySqlOn)
        {
            _debug($"DropPlayer steamId {steamId} — offline and no storage enabled, nothing to wipe", false);
            DeliverDone(done, false);
            return;
        }

        _debug($"DropPlayer steamId {steamId} — OFFLINE, deleting from storage (table '{TableName}')", false);
        _ = Task.Run(async () =>
        {
            bool ok = true;
            try { ok = await DeleteFromBackendsAsync(steamId); }
            catch (Exception ex) { ok = false; _debug($"DropPlayer offline delete error: {ex.Message}", true); }
            DeliverDone(done, ok);
        });
    }

    public void DropSlot(int slot, Action<bool>? done = null)
    {
        if (!_bySlot.TryRemove(slot, out var env)) { DeliverDone(done, false); return; }

        var steamId = env.SteamId;
        var playerName = env.PlayerName;
        _debug($"DropPlayer slot {slot} (steamId {steamId}) — wiping from memory + storage (table '{TableName}')", false);

        if (CookiesOn || MySqlOn)
        {
            _ = Task.Run(async () =>
            {
                bool ok = true;
                try { ok = await DeleteFromBackendsAsync(steamId); }
                catch (Exception ex) { ok = false; _debug($"DropPlayer delete error: {ex.Message}", true); }
                DeliverDone(done, ok);
            });
        }
        else DeliverDone(done, true);

        if (_opts.PrefsAPI_LoadDefaultAfterDrop)
        {
            var fresh = new Envelope
            {
                SteamId = steamId,
                PlayerName = playerName,
                Date = DateTime.Now,
                Payload = new T()
            };
            fresh.Baseline = Clone(fresh.Payload);
            fresh.StorageReady = true;
            _bySlot.TryAdd(slot, fresh);
            _debug($"DropPlayer slot {slot} — reloaded with defaults", false);
        }
    }

    public async Task OnPlayerDisconnectAsync(int slot)
    {
        if (!_bySlot.TryGetValue(slot, out var env)) return;

        bool saveCookie = _opts.PrefsAPI_CookiesEnable == PrefsAPI_SaveMode.OnPlayerDisconnect && _cookies != null;
        bool saveMySql  = _opts.PrefsAPI_MySqlEnable   == PrefsAPI_SaveMode.OnPlayerDisconnect && _mysql != null;

        if ((saveCookie || saveMySql) && IsDirty(env))
        {
            env.Date = DateTime.Now;
            RefreshName(env, slot);
            var snapshot = Clone(env.Payload);
            _debug($"Player {env.PlayerName} (slot {slot}) disconnected — saving (mode 1)", false);
            try
            {
                if (await PersistSnapshotAsync(saveCookie, saveMySql, env.SteamId, env.PlayerName, env.Date, snapshot))
                    MarkClean(env, snapshot);
                else
                    _debug($"Disconnect save failed — keeping {env.PlayerName} values in memory, will retry on next save", true);
            }
            catch (Exception ex) { _debug($"OnPlayerDisconnect save error: {ex.Message}", true); }
        }
        else
        {
            _debug($"Player (slot {slot}) disconnected — no changes to save", false);
        }

        bool mapEndPending =
            (_opts.PrefsAPI_CookiesEnable == PrefsAPI_SaveMode.OnMapEnd ||
             _opts.PrefsAPI_MySqlEnable   == PrefsAPI_SaveMode.OnMapEnd) && IsDirty(env);

        if (!mapEndPending)
        {
            var goneSteamId = env.SteamId;
            _bySlot.TryRemove(slot, out _);
            _cookies?.EvictCached(goneSteamId);
        }
    }

    public Task OnMapEndAsync()
    {
        try
        {
            bool saveCookie  = _opts.PrefsAPI_CookiesEnable == PrefsAPI_SaveMode.OnMapEnd && _cookies != null;
            bool saveMySql   = _opts.PrefsAPI_MySqlEnable   == PrefsAPI_SaveMode.OnMapEnd && _mysql != null;
            bool cleanCookie = CookiesOn;
            bool cleanMySql  = MySqlOn;

            var now = DateTime.Now;
            var toSave = new List<(Envelope env, T snapshot)>();

            foreach (var kv in _bySlot)
            {
                var env = kv.Value;
                bool stillConnected = FindPlayerBySlot(kv.Key) != null;

                if ((saveCookie || saveMySql) && IsDirty(env))
                {
                    if (!stillConnected && env.GoneRetryUsed)
                    {
                        _debug($"Map end — dropping gone player {env.PlayerName} ({env.SteamId}) after 1 retry, still unsaved", true);
                        _bySlot.TryRemove(kv.Key, out _);
                        continue;
                    }

                    env.Date = now;
                    toSave.Add((env, Clone(env.Payload)));

                    if (!stillConnected)
                        env.GoneRetryUsed = true;
                }
                else
                {
                    _bySlot.TryRemove(kv.Key, out _);
                }
            }

            _debug($"Map ended — {toSave.Count} changed, saving (mode 2)", false);

            if (toSave.Count == 0 && !cleanCookie && !cleanMySql)
                return Task.CompletedTask;

            _ = Task.Run(async () =>
            {
                try
                {
                    bool cookieAllOk = true;
                    if (saveCookie && toSave.Count > 0)
                        cookieAllOk = await _cookies!.SaveManyAsync(toSave.Select(x => (x.env.SteamId, x.env.PlayerName, now, x.snapshot)).ToList());

                    bool mysqlAllOk = true;
                    if (saveMySql && toSave.Count > 0)
                        mysqlAllOk = await _mysql!.SaveManyAsync(toSave.Select(x => (x.env.SteamId, x.env.PlayerName, now, x.snapshot)).ToList());

                    if ((!saveCookie || cookieAllOk) && (!saveMySql || mysqlAllOk))
                    {
                        foreach (var (env, snapshot) in toSave)
                            MarkCleanIfUnchanged(env, snapshot);
                    }
                    else
                    {
                        _debug($"Map end save failed — connected players kept; gone players drop after their 1 retry", true);
                    }

                    if (cleanCookie) await _cookies!.RemoveOldAsync(_opts.PrefsAPI_CookiesAutoRemoveInactivePlayersOlderThanDays);
                    if (cleanMySql)  await _mysql!.DeleteOldAsync  (_opts.PrefsAPI_MySqlAutoRemoveInactivePlayersOlderThanDays);
                }
                catch (Exception ex) { _debug($"OnMapEnd save error: {ex.Message}", true); }
            });
        }
        catch (Exception ex) { _debug($"OnMapEnd error: {ex.Message}", true); }

        return Task.CompletedTask;
    }
    private void MarkCleanIfUnchanged(Envelope env, T savedSnapshot)
    {
        lock (env.MergeGate)
        {
            if (_info.DiffersFromBaseline(env.Payload, savedSnapshot))
            {
                return;
            }
            MarkClean(env, savedSnapshot);
        }
    }

    private List<(ulong steamId, string name, DateTime date, T snapshot)> CollectDirtyAndClear()
    {
        var now = DateTime.Now;
        var dirty = new List<(ulong, string, DateTime, T)>();
        foreach (var kv in _bySlot)
        {
            if (!IsDirty(kv.Value)) continue;
            kv.Value.Date = now;
            dirty.Add((kv.Value.SteamId, kv.Value.PlayerName, now, Clone(kv.Value.Payload)));
        }
        _bySlot.Clear();
        return dirty;
    }

    public void ForceSaveAndClear()
    {
        bool saveCookie = CookiesOn;
        bool saveMySql  = MySqlOn;
        var dirty = CollectDirtyAndClear();

        if (dirty.Count > 0 && (saveCookie || saveMySql))
        {
            _debug($"ForceSaveAndClear — saving {dirty.Count} player(s)", false);
            _ = Task.Run(async () =>
            {
                try
                {
                    if (saveCookie) await _cookies!.SaveManyAsync(dirty);
                    if (saveMySql)  await _mysql!.SaveManyAsync(dirty);
                }
                catch (Exception ex) { _debug($"ForceSaveAndClear error: {ex.Message}", true); }
            });
        }
        else _debug("ForceSaveAndClear — nothing to save, memory cleared", false);
    }

    public void Refresh()
    {
        _debug("Refresh — saving + reloading all players", false);
        ForceSaveAndClear();

        try
        {
            foreach (var p in CounterStrikeSharp.API.Utilities.GetPlayers())
            {
                if (p == null || !p.IsValid || p.IsBot || p.IsHLTV) continue;
                _ = LoadPlayerAsync(p);
            }
        }
        catch (Exception ex) { _debug($"Refresh reload error: {ex.Message}", true); }
    }

    public void Unload()
    {
        bool saveCookie = CookiesOn;
        bool saveMySql  = MySqlOn;
        var dirty = CollectDirtyAndClear();

        _debug($"Unload '{TableName}' — {(dirty.Count > 0 ? $"saving {dirty.Count} player(s)" : "nothing to save")}", true);

        var cookies = _cookies;
        var mysql   = _mysql;

        _ = Task.Run(async () =>
        {
            try
            {
                if (dirty.Count > 0 && (saveCookie || saveMySql))
                {
                    try
                    {
                        if (saveCookie && cookies != null) await cookies.SaveManyAsync(dirty);
                        if (saveMySql  && mysql   != null) await mysql.SaveManyAsync(dirty);
                    }
                    catch (Exception ex) { _debug($"Unload save error: {ex.Message}", true); }
                }
            }
            finally
            {
                if (cookies != null) { try { cookies.Dispose(); } catch { } }
                if (mysql   != null) { try { mysql.Dispose(); } catch { } }
            }
        });
    }
}