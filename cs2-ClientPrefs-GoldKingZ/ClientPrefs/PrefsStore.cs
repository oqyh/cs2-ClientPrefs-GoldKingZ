using System.Collections.Concurrent;
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

    private string DumpValues(T payload)
    {
        return string.Join(", ", _info.Props.Select(p => $"{p.Name}={p.GetValue(payload)}"));
    }

    private static CCSPlayerController? FindPlayerBySlot(int slot)
    {
        try
        {
            var p = CounterStrikeSharp.API.Utilities.GetPlayerFromSlot(slot);
            return p != null && p.IsValid && !p.IsBot && !p.IsHLTV ? p : null;
        }
        catch
        {
            return null;
        }
    }

    public Task LoadPlayerAsync(CCSPlayerController? player)
    {
        if (player == null || !player.IsValid || player.IsBot || player.IsHLTV) return Task.CompletedTask;

        var slot = player.Slot;
        var steamId = player.SteamID;
        var playerName = player.PlayerName ?? "";

        _debug($"LoadPlayerAsync start — {playerName} slot {slot} steamId {steamId}", false);

        foreach (var kv in _bySlot)
        {
            if (kv.Key != slot && kv.Value.SteamId == steamId)
            {
                if (_bySlot.TryRemove(kv.Key, out var moved))
                {
                    _bySlot[slot] = moved;
                    _debug($"moved envelope from slot {kv.Key} to slot {slot}, values: [{DumpValues(moved.Payload)}]", false);
                }
                break;
            }
        }

        if (_bySlot.TryGetValue(slot, out var existing))
        {
            if (existing.SteamId != steamId)
            {
                _debug($"slot {slot} had different player ({existing.SteamId}), removing", false);
                _bySlot.TryRemove(slot, out _);
            }
            else if (!_opts.PrefsAPI_ReloadOnReconnect)
            {
                existing.PlayerName = playerName;
                _debug($"{playerName} already in memory, KEEPING EXISTING values: [{DumpValues(existing.Payload)}] (dirty={existing.HasBeenMutated || existing.LoadedFromStorage})", false);
                return Task.CompletedTask;
            }
            else
            {
                _debug($"{playerName} reconnected, ReloadOnReconnect=true, discarding old values: [{DumpValues(existing.Payload)}]", false);
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

        _debug($"new envelope with defaults: [{DumpValues(env.Payload)}]", false);

        bool cookiesActive = _opts.PrefsAPI_CookiesEnable != PrefsAPI_SaveMode.Disabled && _cookies != null;
        bool mysqlActive   = _opts.PrefsAPI_MySqlEnable   != PrefsAPI_SaveMode.Disabled && _mysql   != null;

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
                _debug($"cookies applied (date {cached.Value.date:yyyy-MM-dd HH:mm:ss}): [{DumpValues(env.Payload)}]", false);
            }
            else
            {
                _debug($"no cookies row for {steamId}", false);
            }
        }

        if (!cookiesActive && !mysqlActive)
        {
            env.StorageReady = true;
            _debug($"{playerName} storage READY (cookies disabled + mysql disabled — memory only)", false);
        }
        else if (cookiesActive && !mysqlActive)
        {
            env.StorageReady = true;
            _debug($"{playerName} storage READY (cookies applied, mysql disabled — nothing to wait for)", false);
        }
        else
        {
            LoadNotifier.RegisterPending(slot, steamId);
            _debug($"{playerName} storage NOT ready yet — waiting for MySQL stage (cookies {(cookiesActive ? "applied" : "disabled")})", false);
        }

        env.Baseline = Clone(env.Payload);
        _bySlot[slot] = env;
        _debug($"Player {playerName} (slot {slot}, {steamId}) ready", false);

        if (mysqlActive)
        {
            _ = Task.Run(() => ApplyMySqlStage(slot, env, steamId, playerName));
        }

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

            _debug($"row loaded for {playerName} (date {row.Value.date:yyyy-MM-dd HH:mm:ss}): [{DumpValues(row.Value.payload)}]", false);

            lock (env.MergeGate)
            {
                if (!_bySlot.TryGetValue(slot, out var current))
                {
                    _debug($"slot {slot} has NO envelope anymore — discarding row", false);
                    return;
                }
                if (!ReferenceEquals(current, env))
                {
                    _debug($"slot {slot} envelope was REPLACED — discarding row", false);
                    return;
                }

                if (env.HasBeenMutated || _info.DiffersFromBaseline(env.Payload, env.Baseline))
                {
                    _debug($"{playerName} already changed values, keeping current: [{DumpValues(env.Payload)}]", false);
                    return;
                }

                if (env.CookiesApplied && row.Value.date < env.CookiesDate)
                {
                    _debug($"row older than cookies ({row.Value.date:yyyy-MM-dd HH:mm:ss} < {env.CookiesDate:yyyy-MM-dd HH:mm:ss}) — keeping cookies values: [{DumpValues(env.Payload)}]", false);
                    return;
                }

                _debug($"values BEFORE merge: [{DumpValues(env.Payload)}]", false);
                CopyInto(row.Value.payload, env.Payload);
                env.Date = row.Value.date;
                env.LoadedFromStorage = true;
                env.Baseline = Clone(env.Payload);
                _debug($"values AFTER merge: [{DumpValues(env.Payload)}]", false);
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
            _debug($"{playerName} storage READY — TryGetValue unlocked", false);
        }
    }

    private T Clone(T src) => (T)_info.Clone(src);

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

    private void RefreshName(Envelope env, int slot)
    {
        try
        {
            var live = FindPlayerBySlot(slot);
            if (live != null && !string.IsNullOrEmpty(live.PlayerName))
                env.PlayerName = live.PlayerName;
        }
        catch
        {
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
                bool cookieOk = !saveCookie || await _cookies!.SaveAsync(env.SteamId, env.PlayerName, env.Date, snapshot);
                bool mysqlOk  = !saveMySql  || await _mysql!.SaveAsync  (env.SteamId, env.PlayerName, env.Date, snapshot);

                if (cookieOk && mysqlOk)
                {
                    MarkClean(env, snapshot);
                }
                else
                {
                    _debug($"Disconnect save failed — keeping {env.PlayerName} values in memory, will retry on next save", true);
                }
            }
            catch (Exception ex)
            {
                _debug($"OnPlayerDisconnect save error: {ex.Message}", true);
            }
        }
        else
        {
            _debug($"Player (slot {slot}) disconnected — no changes to save", false);
        }
    }

    public Task OnMapEndAsync()
    {
        try
        {
            bool saveCookie  = _opts.PrefsAPI_CookiesEnable == PrefsAPI_SaveMode.OnMapEnd && _cookies != null;
            bool saveMySql   = _opts.PrefsAPI_MySqlEnable   == PrefsAPI_SaveMode.OnMapEnd && _mysql != null;
            bool cleanCookie = _opts.PrefsAPI_CookiesEnable != PrefsAPI_SaveMode.Disabled && _cookies != null;
            bool cleanMySql  = _opts.PrefsAPI_MySqlEnable   != PrefsAPI_SaveMode.Disabled && _mysql != null;

            var now = DateTime.Now;
            var toSave = new List<(Envelope env, T snapshot)>();

            foreach (var kv in _bySlot)
            {
                var env = kv.Value;

                if ((saveCookie || saveMySql) && IsDirty(env))
                {
                    env.Date = now;
                    toSave.Add((env, Clone(env.Payload)));
                }
                else
                {
                    _bySlot.TryRemove(kv.Key, out _);
                }
            }

            _debug($"Map ended — {toSave.Count} changed, saving (mode 2), values kept in memory until save is confirmed", false);

            if (toSave.Count == 0 && !cleanCookie && !cleanMySql)
                return Task.CompletedTask;

            _ = Task.Run(async () =>
            {
                try
                {
                    bool cookieAllOk = true;
                    if (saveCookie && toSave.Count > 0)
                    {
                        var rows = toSave.Select(x => (x.env.SteamId, x.env.PlayerName, now, x.snapshot)).ToList();
                        cookieAllOk = await _cookies!.SaveManyAsync(rows);
                    }

                    bool mysqlAllOk = true;
                    if (saveMySql && toSave.Count > 0)
                    {
                        var rows = toSave.Select(x => (x.env.SteamId, x.env.PlayerName, now, x.snapshot)).ToList();
                        mysqlAllOk = await _mysql!.SaveManyAsync(rows);
                    }

                    if ((!saveCookie || cookieAllOk) && (!saveMySql || mysqlAllOk))
                    {
                        foreach (var (env, snapshot) in toSave)
                            MarkClean(env, snapshot);
                    }
                    else
                    {
                        _debug($"Map end save failed — {toSave.Count} player(s) kept in memory with their values, will retry on next save", true);
                    }

                    if (cleanCookie) await _cookies!.RemoveOldAsync(_opts.PrefsAPI_CookiesAutoRemoveInactivePlayersOlderThanDays);
                    if (cleanMySql)  await _mysql!.DeleteOldAsync  (_opts.PrefsAPI_MySqlAutoRemoveInactivePlayersOlderThanDays);
                }
                catch (Exception ex)
                {
                    _debug($"OnMapEnd save error: {ex.Message}", true);
                }
            });
        }
        catch (Exception ex)
        {
            _debug($"OnMapEnd error: {ex.Message}", true);
        }

        return Task.CompletedTask;
    }

    private void CopyInto(T src, T dst)
    {
        foreach (var p in _info.Props)
        {
            var v = p.GetValue(src);
            if (v != null) p.SetValue(dst, v);
        }
    }

    public bool TryGetValue(CCSPlayerController player, out T data)
    {
        if (player == null || !player.IsValid || player.IsBot || player.IsHLTV)
        {
            data = null!;
            return false;
        }

        if (_bySlot.TryGetValue(player.Slot, out var e) && !e.StorageReady)
        {
            _debug($"TryGetValue slot {player.Slot} — storage not ready yet, telling player to wait", false);
            LoadNotifier.ShowWaitMessage(player);
            data = null!;
            return false;
        }

        return TryGetValue(player.Slot, out data);
    }

    public bool TryGetValue(int slot, out T data)
    {
        if (_bySlot.TryGetValue(slot, out var e))
        {
            if (!e.StorageReady)
            {
                _debug($"TryGetValue slot {slot} — storage not ready yet, telling player to wait", false);
                LoadNotifier.ShowWaitMessage(FindPlayerBySlot(slot));
                data = null!;
                return false;
            }
            data = e.Payload;
            return true;
        }
        data = null!; return false;
    }

    public bool TryGetValue(CCSPlayerController player, Action<T> action)
    {
        if (player == null || !player.IsValid || player.IsBot || player.IsHLTV) return false;

        if (_bySlot.TryGetValue(player.Slot, out var e) && !e.StorageReady)
        {
            _debug($"TryGetValue slot {player.Slot} — storage not ready yet, telling player to wait", false);
            LoadNotifier.ShowWaitMessage(player);
            return false;
        }

        return TryGetValue(player.Slot, action);
    }

    public bool TryGetValue(int slot, Action<T> action)
    {
        if (_bySlot.TryGetValue(slot, out var e))
        {
            if (!e.StorageReady)
            {
                _debug($"TryGetValue slot {slot} — storage not ready yet, telling player to wait", false);
                LoadNotifier.ShowWaitMessage(FindPlayerBySlot(slot));
                return false;
            }
            action(e.Payload);
            if (!e.HasBeenMutated && _info.DiffersFromBaseline(e.Payload, e.Baseline))
                e.HasBeenMutated = true;
            return true;
        }
        return false;
    }

    public void ForceSave(CCSPlayerController player)
    {
        if (player == null || !player.IsValid || player.IsBot || player.IsHLTV) return;
        ForceSave(player.Slot);
    }

    public void ForceSave(int slot)
    {
        ForceSaveSlot(slot);
    }

    public void ForceSaveSlot(int slot)
    {
        if (!_bySlot.TryGetValue(slot, out var e)) return;
        if (!IsDirty(e)) return;

        e.Date = DateTime.Now;
        RefreshName(e, slot);
        _debug($"ForceSave slot {slot}", false);

        bool saveCookie = _opts.PrefsAPI_CookiesEnable != PrefsAPI_SaveMode.Disabled && _cookies != null;
        bool saveMySql  = _opts.PrefsAPI_MySqlEnable   != PrefsAPI_SaveMode.Disabled && _mysql != null;

        var steamId = e.SteamId;
        var name = e.PlayerName;
        var date = e.Date;
        var snapshot = Clone(e.Payload);

        _ = Task.Run(async () =>
        {
            try
            {
                bool cookieOk = !saveCookie || await _cookies!.SaveAsync(steamId, name, date, snapshot);
                bool mysqlOk  = !saveMySql  || await _mysql!.SaveAsync  (steamId, name, date, snapshot);

                if (cookieOk && mysqlOk)
                {
                    MarkClean(e, snapshot);
                }
                else
                {
                    _debug($"ForceSave slot {slot} failed — keeping values in memory, will retry on next save", true);
                }
            }
            catch (Exception ex)
            {
                _debug($"ForceSave error: {ex.Message}", true);
            }
        });
    }

    public void ForceSavePlayer_To_All_Instances(CCSPlayerController player)
    {
        if (player == null || !player.IsValid || player.IsBot || player.IsHLTV) return;
        ForceSavePlayer_To_All_Instances(player.Slot);
    }

    public void ForceSavePlayer_To_All_Instances(int slot)
    {
        _debug($"ForceSavePlayer_To_All_Instances slot {slot}", false);
        foreach (var store in _allStores())
        {
            try { store.ForceSaveSlot(slot); } catch { }
        }
    }

    public void DropPlayer(CCSPlayerController player)
    {
        if (player == null || !player.IsValid || player.IsBot || player.IsHLTV) return;
        DropPlayer(player.Slot);
    }

    public void DropPlayer(int slot)
    {
        DropSlot(slot);
    }

    public void DropSlot(int slot)
    {
        if (!_bySlot.TryRemove(slot, out var env)) return;

        var steamId = env.SteamId;
        var playerName = env.PlayerName;
        _debug($"DropPlayer slot {slot} (steamId {steamId}) — wiping from memory + storage", false);

        bool hasCookie = _opts.PrefsAPI_CookiesEnable != PrefsAPI_SaveMode.Disabled && _cookies != null;
        bool hasMySql  = _opts.PrefsAPI_MySqlEnable   != PrefsAPI_SaveMode.Disabled && _mysql != null;

        if (hasCookie || hasMySql)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    if (hasCookie) await _cookies!.DeleteAsync(steamId);
                    if (hasMySql)  await _mysql!.DeleteAsync  (steamId);
                }
                catch (Exception ex)
                {
                    _debug($"DropPlayer delete error: {ex.Message}", true);
                }
            });
        }

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

    public void DropPlayer_To_All_Instances(CCSPlayerController player)
    {
        if (player == null || !player.IsValid || player.IsBot || player.IsHLTV) return;
        DropPlayer_To_All_Instances(player.Slot);
    }

    public void DropPlayer_To_All_Instances(int slot)
    {
        _debug($"DropPlayer_To_All_Instances slot {slot}", false);
        foreach (var store in _allStores())
        {
            try { store.DropSlot(slot); } catch { }
        }
    }

    public void ForceSaveAndClear()
    {
        bool saveCookie = _opts.PrefsAPI_CookiesEnable != PrefsAPI_SaveMode.Disabled && _cookies != null;
        bool saveMySql  = _opts.PrefsAPI_MySqlEnable   != PrefsAPI_SaveMode.Disabled && _mysql != null;

        var now = DateTime.Now;
        var dirty = new List<(ulong steamId, string name, DateTime date, T snapshot)>();
        foreach (var kv in _bySlot)
        {
            if (!IsDirty(kv.Value)) continue;
            kv.Value.Date = now;
            dirty.Add((kv.Value.SteamId, kv.Value.PlayerName, now, Clone(kv.Value.Payload)));
        }

        _bySlot.Clear();

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
                catch (Exception ex)
                {
                    _debug($"ForceSaveAndClear error: {ex.Message}", true);
                }
            });
        }
        else
        {
            _debug("ForceSaveAndClear — nothing to save, memory cleared", false);
        }
    }

    public void Refresh()
    {
        _debug("Refresh — saving + reloading all players", false);
        ForceSaveAndClear();

        try
        {
            foreach (var p in CounterStrikeSharp.API.Utilities.GetPlayers())
            {
                if (p == null || !p.IsValid) continue;
                if (p.IsBot || p.IsHLTV) continue;

                _ = LoadPlayerAsync(p);
            }
        }
        catch (Exception ex)
        {
            _debug($"Refresh reload error: {ex.Message}", true);
        }
    }

    public void Unload()
    {
        _debug("Unload — saving all + closing connections + clearing memory", true);

        bool saveCookie = _opts.PrefsAPI_CookiesEnable != PrefsAPI_SaveMode.Disabled && _cookies != null;
        bool saveMySql  = _opts.PrefsAPI_MySqlEnable   != PrefsAPI_SaveMode.Disabled && _mysql != null;

        var now = DateTime.Now;
        var dirty = new List<(ulong steamId, string name, DateTime date, T snapshot)>();
        foreach (var kv in _bySlot)
        {
            if (!IsDirty(kv.Value)) continue;
            kv.Value.Date = now;
            dirty.Add((kv.Value.SteamId, kv.Value.PlayerName, now, Clone(kv.Value.Payload)));
        }

        _bySlot.Clear();

        if (dirty.Count > 0) _debug($"Unload — saving {dirty.Count} player(s)", true);
        else _debug("Unload — no players to save", true);

        _ = Task.Run(async () =>
        {
            if (dirty.Count > 0 && (saveCookie || saveMySql))
            {
                try
                {
                    if (saveCookie) await _cookies!.SaveManyAsync(dirty);
                    if (saveMySql)  await _mysql!.SaveManyAsync(dirty);
                }
                catch (Exception ex)
                {
                    _debug($"Unload save error: {ex.Message}", true);
                }
            }

            if (_cookies != null)
            {
                try { _cookies.Dispose(); } catch { }
                _debug("SQLite connection closed", true);
            }

            if (_mysql != null)
            {
                try { _mysql.Dispose(); } catch { }
                _debug("MySQL manager released", true);
            }
        });
    }
}