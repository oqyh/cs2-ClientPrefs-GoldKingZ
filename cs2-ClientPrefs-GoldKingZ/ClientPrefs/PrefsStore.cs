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
    }

    private readonly PrefsTypeInfo _info = PrefsTypeInfo.Of<T>();
    private readonly ClientPrefsOptions _opts;
    private readonly CookiesBackend<T>? _cookies;
    private readonly MySqlBackend<T>?   _mysql;
    private readonly Action<string, bool> _debug;
    private readonly Func<IEnumerable<IStoreLifecycle>> _allStores;

    private readonly ConcurrentDictionary<int, Envelope> _bySlot = new();

    public string PluginName { get; }
    public BasePlugin Plugin { get; }

    public PrefsStore(BasePlugin plugin, string pluginName, ClientPrefsOptions opts,
                      CookiesBackend<T>? cookies, MySqlBackend<T>? mysql,
                      Action<string, bool> debug, Func<IEnumerable<IStoreLifecycle>> allStores)
    {
        Plugin = plugin;
        PluginName = pluginName;
        _opts = opts;
        _cookies = cookies;
        _mysql = mysql;
        _debug = debug;
        _allStores = allStores;
    }

    public async Task LoadPlayerAsync(CCSPlayerController? player)
    {
        if (player == null || !player.IsValid || player.IsBot || player.IsHLTV) return;

        var slot = player.Slot;
        var steamId = player.SteamID;
        var playerName = player.PlayerName ?? "";

        if (_bySlot.TryGetValue(slot, out var existing))
        {
            if (existing.SteamId != steamId)
            {
                _debug($"Slot {slot} reused by different player ({existing.SteamId} → {steamId}), reloading fresh", false);
                _bySlot.TryRemove(slot, out _);
            }
            else if (!_opts.PrefsAPI_ReloadOnReconnect)
            {
                _debug($"Player {playerName} (slot {slot}) already in memory, skipping load", false);
                return;
            }
            else
            {
                _debug($"Player {playerName} (slot {slot}) reconnected, ReloadOnReconnect=true, reloading", false);
                _bySlot.TryRemove(slot, out _);
            }
        }

        var env = new Envelope
        {
            SteamId = steamId,
            PlayerName = playerName ?? "",
            Date = DateTime.Now,
            Payload = new T()
        };

        if (_opts.PrefsAPI_CookiesEnable != PrefsAPI_SaveMode.Disabled && _cookies != null)
        {
            var cached = _cookies.GetCached(steamId);
            if (cached.HasValue)
            {
                CopyInto(cached.Value.payload, env.Payload);
                env.Date = cached.Value.date;
                env.LoadedFromStorage = true;
                _debug($"Player {playerName} loaded from cookies.db", false);
            }
        }

        if (_opts.PrefsAPI_MySqlEnable != PrefsAPI_SaveMode.Disabled && _mysql is { IsReady: true })
        {
            var row = await _mysql.LoadAsync(steamId);
            if (row.HasValue)
            {
                CopyInto(row.Value.payload, env.Payload);
                env.Date = row.Value.date;
                env.LoadedFromStorage = true;
                _debug($"Player {playerName} loaded from MySQL (overrides cookies)", false);
            }
        }

        env.Baseline = Clone(env.Payload);
        _bySlot.TryAdd(slot, env);
        _debug($"Player {playerName} (slot {slot}, {steamId}) ready", false);
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

    private void RefreshName(Envelope env, int slot)
    {
        var live = CounterStrikeSharp.API.Utilities.GetPlayers()
            .FirstOrDefault(p => p.Slot == slot && p.IsValid && !p.IsBot && !p.IsHLTV);
        if (live != null && !string.IsNullOrEmpty(live.PlayerName))
            env.PlayerName = live.PlayerName;
    }

    public async Task OnPlayerDisconnectAsync(int slot)
    {
        if (!_bySlot.TryGetValue(slot, out var env)) return;

        bool saveCookie = _opts.PrefsAPI_CookiesEnable == PrefsAPI_SaveMode.OnPlayerDisconnect && _cookies != null;
        bool saveMySql  = _opts.PrefsAPI_MySqlEnable   == PrefsAPI_SaveMode.OnPlayerDisconnect && _mysql is { IsReady: true };

        if ((saveCookie || saveMySql) && IsDirty(env))
        {
            env.Date = DateTime.Now;
            RefreshName(env, slot);
            _debug($"Player {env.PlayerName} (slot {slot}) disconnected — saving (mode 1)", false);
            try
            {
                if (saveCookie) await _cookies!.SaveAsync(env.SteamId, env.PlayerName, env.Date, env.Payload);
                if (saveMySql)  await _mysql!.SaveAsync  (env.SteamId, env.PlayerName, env.Date, env.Payload);
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
        bool saveCookie  = _opts.PrefsAPI_CookiesEnable == PrefsAPI_SaveMode.OnMapEnd && _cookies != null;
        bool saveMySql   = _opts.PrefsAPI_MySqlEnable   == PrefsAPI_SaveMode.OnMapEnd && _mysql is { IsReady: true };
        bool cleanCookie = _opts.PrefsAPI_CookiesEnable != PrefsAPI_SaveMode.Disabled && _cookies != null;
        bool cleanMySql  = _opts.PrefsAPI_MySqlEnable   != PrefsAPI_SaveMode.Disabled && _mysql is { IsReady: true };

        List<Envelope>? playersToSave = null;
        if (saveCookie || saveMySql)
        {
            playersToSave = new List<Envelope>();
            foreach (var kv in _bySlot)
            {
                if (!IsDirty(kv.Value)) continue;
                RefreshName(kv.Value, kv.Key);
                playersToSave.Add(kv.Value);
            }
        }

        if (playersToSave?.Count is null or 0 && !cleanCookie && !cleanMySql)
        {
            _bySlot.Clear();
            _debug("Map ended — nothing to save, memory cleared", false);
            return Task.CompletedTask;
        }

        int totalPlayers = _bySlot.Count;
        int dirtyCount = playersToSave?.Count ?? 0;
        _bySlot.Clear();

        _debug($"Map ended — {dirtyCount} changed / {totalPlayers} total, saving (mode 2)", false);

        if (playersToSave != null)
        {
            var now = DateTime.Now;
            foreach (var e in playersToSave) e.Date = now;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var tasks = new List<Task>();

                if (playersToSave?.Count > 0)
                {
                    foreach (var e in playersToSave)
                    {
                        if (saveCookie) tasks.Add(_cookies!.SaveAsync(e.SteamId, e.PlayerName, e.Date, e.Payload));
                        if (saveMySql)  tasks.Add(_mysql!.SaveAsync  (e.SteamId, e.PlayerName, e.Date, e.Payload));
                    }
                }

                if (cleanCookie) tasks.Add(_cookies!.RemoveOldAsync(_opts.PrefsAPI_CookiesAutoRemoveInactivePlayersOlderThanDays));
                if (cleanMySql)  tasks.Add(_mysql!.DeleteOldAsync   (_opts.PrefsAPI_MySqlAutoRemoveInactivePlayersOlderThanDays));

                if (tasks.Count > 0) await Task.WhenAll(tasks);
            }
            catch (Exception ex)
            {
                _debug($"OnMapEnd save error: {ex.Message}", true);
            }
        });

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
        return TryGetValue(player.Slot, out data);
    }

    public bool TryGetValue(int slot, out T data)
    {
        if (_bySlot.TryGetValue(slot, out var e)) { data = e.Payload; return true; }
        data = null!; return false;
    }

    public bool TryGetValue(CCSPlayerController player, Action<T> action)
    {
        if (player == null || !player.IsValid || player.IsBot || player.IsHLTV) return false;
        return TryGetValue(player.Slot, action);
    }

    public bool TryGetValue(int slot, Action<T> action)
    {
        if (_bySlot.TryGetValue(slot, out var e))
        {
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
        bool saveMySql  = _opts.PrefsAPI_MySqlEnable   != PrefsAPI_SaveMode.Disabled && _mysql is { IsReady: true };

        var steamId = e.SteamId;
        var name = e.PlayerName;
        var date = e.Date;
        var payload = e.Payload;

        _ = Task.Run(async () =>
        {
            try
            {
                if (saveCookie) await _cookies!.SaveAsync(steamId, name, date, payload);
                if (saveMySql)  await _mysql!.SaveAsync  (steamId, name, date, payload);
            }
            catch (Exception ex)
            {
                _debug($"ForceSave error: {ex.Message}", true);
            }
        });

        e.Baseline = Clone(e.Payload);
        e.LoadedFromStorage = false;
        e.HasBeenMutated = false;
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
        bool hasMySql  = _opts.PrefsAPI_MySqlEnable   != PrefsAPI_SaveMode.Disabled && _mysql is { IsReady: true };

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
        bool saveMySql  = _opts.PrefsAPI_MySqlEnable   != PrefsAPI_SaveMode.Disabled && _mysql is { IsReady: true };

        var dirty = new List<Envelope>();
        foreach (var kv in _bySlot)
        {
            if (!IsDirty(kv.Value)) continue;
            RefreshName(kv.Value, kv.Key);
            dirty.Add(kv.Value);
        }

        _bySlot.Clear();

        if (dirty.Count > 0 && (saveCookie || saveMySql))
        {
            _debug($"ForceSaveAndClear — saving {dirty.Count} player(s)", false);
            var now = DateTime.Now;
            foreach (var e in dirty) e.Date = now;

            _ = Task.Run(async () =>
            {
                try
                {
                    var tasks = new List<Task>();
                    foreach (var e in dirty)
                    {
                        if (saveCookie) tasks.Add(_cookies!.SaveAsync(e.SteamId, e.PlayerName, e.Date, e.Payload));
                        if (saveMySql)  tasks.Add(_mysql!.SaveAsync  (e.SteamId, e.PlayerName, e.Date, e.Payload));
                    }
                    if (tasks.Count > 0) await Task.WhenAll(tasks);
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

        foreach (var p in CounterStrikeSharp.API.Utilities.GetPlayers())
        {
            if (p == null || !p.IsValid) continue;
            if (p.IsBot || p.IsHLTV) continue;

            _ = LoadPlayerAsync(p);
        }
    }

    public void Unload()
    {
        _debug("Unload — saving all + closing connections + clearing memory", true);

        bool saveCookie = _opts.PrefsAPI_CookiesEnable != PrefsAPI_SaveMode.Disabled && _cookies != null;
        bool saveMySql  = _opts.PrefsAPI_MySqlEnable   != PrefsAPI_SaveMode.Disabled && _mysql is { IsReady: true };

        var dirty = new List<Envelope>();
        foreach (var kv in _bySlot)
        {
            if (!IsDirty(kv.Value)) continue;
            RefreshName(kv.Value, kv.Key);
            dirty.Add(kv.Value);
        }

        _bySlot.Clear();

        if (dirty.Count > 0) _debug($"Unload — saving {dirty.Count} player(s)", true);
        else _debug("Unload — no players to save", true);

        var now = DateTime.Now;
        foreach (var e in dirty) e.Date = now;

        _ = Task.Run(async () =>
        {
            if (dirty.Count > 0 && (saveCookie || saveMySql))
            {
                try
                {
                    var tasks = new List<Task>();
                    foreach (var e in dirty)
                    {
                        if (saveCookie) tasks.Add(_cookies!.SaveAsync(e.SteamId, e.PlayerName, e.Date, e.Payload));
                        if (saveMySql)  tasks.Add(_mysql!.SaveAsync  (e.SteamId, e.PlayerName, e.Date, e.Payload));
                    }
                    if (tasks.Count > 0) await Task.WhenAll(tasks);
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
                try { MySqlConnector.MySqlConnection.ClearAllPools(); } catch { }
                _debug("MySQL connection pool cleared", true);
            }
        });
    }
}