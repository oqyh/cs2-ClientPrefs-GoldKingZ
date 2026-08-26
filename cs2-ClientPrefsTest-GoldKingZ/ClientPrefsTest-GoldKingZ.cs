using ClientPrefs_GoldKingZ.Shared;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using Microsoft.Extensions.Logging;

namespace ClientPrefsTest_GoldKingZ;

// ============================================================================
// STEP 1: Your data class — only fields YOUR plugin needs.
// PlayerName / PlayerSteamID / DateAndTime are RESERVED (auto-injected).
// Defaults (= 50, = "") are the starting values for new players.
// You can add/remove/rename fields anytime — tables auto-migrate (no data loss).
// Supported types: bool, int, long, ulong, float, double, string, DateTime
// One plugin can register MORE THAN ONE class — each is a separate isolated
// store with its own table (see ClientPrefsHud + ClientPrefsSounds below).
// ============================================================================
public sealed class ClientPrefs
{
    public bool   ChatMuted { get; set; } = false;
    public int    Volume    { get; set; } = 50;
    public string FavPack   { get; set; } = "";
    public string IpAddress    { get; set; } = "";
}

public sealed class ClientPrefsHud
{
    public bool ShowHud  { get; set; } = true;
    public int  HudColor { get; set; } = 0xFFFFFF;
}

public sealed class ClientPrefsSounds
{
    public bool  SoundsEnabled { get; set; } = true;
}

// For <R> commands (css_db_all / css_search_all): YOUR OWN result class.
// Declare only the fields you want back — filled by matching column names
// from any plugin's table. No need to reference other plugins' classes.
public sealed class CrossRow
{
    public int    Volume { get; set; }
    public string? IpAddress { get; set; }
    public bool Toggle_Something { get; set; }
}

public sealed class ClientPrefsTestPlugin : BasePlugin
{
    public override string ModuleName => "Shared player Preferences API Per-Plugin Isolation With [Cookies(SQLite) + MySQL] (API Test)";
    public override string ModuleVersion => "1.0.4";
    public override string ModuleAuthor  => "Gold KingZ";
    public override string ModuleDescription => "https://github.com/oqyh";

    // ============================================================================
    // STEP 2: One store variable per data class
    // - IPrefsStore<T> is the main API you interact with
    // - Nullable because ClientPrefs core might not be installed
    // ============================================================================
    private IPrefsStore<ClientPrefs>?       _prefs;
    private IPrefsStore<ClientPrefsHud>?    _hud;
    private IPrefsStore<ClientPrefsSounds>? _sounds;

    // ============================================================================
    // STEP 3: Register your prefs in OnAllPluginsLoaded
    // - Runs AFTER all plugins are loaded (including ClientPrefs core)
    // - ClientPrefsApi.Get() returns null if core plugin is not installed
    // - CreatePrefs<T>() registers your data class and sets up cookies/mysql
    // - Call it MULTIPLE TIMES (once per class) to get isolated stores
    // - If hotReload is true, call Refresh() to reload all connected players
    // ============================================================================
    public override void OnAllPluginsLoaded(bool hotReload)
    {
        var api = ClientPrefsApi.Get();
        if (api == null)
        {
            Logger.LogError("[ClientPrefsTest] Missing cs2-ClientPrefs-GoldKingZ API !");
            return;
        }

        _prefs = api.CreatePrefs<ClientPrefs>(this, new ClientPrefsOptions
        {
            // ================================================================
            // PrefsAPI_CookiesEnable — when to save to cookies (SQLite, local file)
            //   PrefsAPI_SaveMode.Disabled            = don't save to cookies
            //   PrefsAPI_SaveMode.OnPlayerDisconnect  = save when player leaves
            //   PrefsAPI_SaveMode.OnMapEnd            = save when map changes
            // Default if not set: Disabled
            // ================================================================
            PrefsAPI_CookiesEnable = PrefsAPI_SaveMode.OnMapEnd,

            // Auto-delete inactive players from cookies after X days (0 = never).
            // Runs on every map change. Default: 7
            PrefsAPI_CookiesAutoRemoveInactivePlayersOlderThanDays = 30,

            // ================================================================
            // PrefsAPI_MySqlEnable — when to save to MySQL (remote database)
            // Same values as cookies. If BOTH enabled → NEWEST data wins on load.
            // Default if not set: Disabled
            // ================================================================
            PrefsAPI_MySqlEnable = PrefsAPI_SaveMode.Disabled,

            // Auto-delete inactive players from MySQL after X days (0 = never). Default: 7
            PrefsAPI_MySqlAutoRemoveInactivePlayersOlderThanDays = 30,

            PrefsAPI_MySqlConnectionTimeout = 30,   // seconds, default 30
            PrefsAPI_MySqlRetryAttempts     = 3,    // default 3
            PrefsAPI_MySqlRetryDelay        = 2,    // seconds between retries, default 2

            // ================================================================
            // PrefsAPI_TableName — custom table name, applies to BOTH cookies + MySQL
            // Default if not set: <FolderName>_<ClassName>
            // Example if not set: Plugin-A-GoldKingZ + ClientPrefs → Plugin_A_GoldKingZ_ClientPrefs
            // ================================================================
            PrefsAPI_TableName = null,

            // MySQL connection (single server):
            PrefsAPI_MySqlConfig = new MySqlConfig
            {
                Server   = "localhost",
                Port     = 3306,
                Database = "test",
                Username = "root",
                Password = "",
            },

            // Multiple MySQL servers (saves/deletes go to ALL servers, loads use the NEWEST row):
            // - Save → written to every reachable server (unreachable ones sync on next save)
            // - Load → all servers checked, the row with the newest DateAndTime wins
            // PrefsAPI_MySqlConfig = new MySqlConfig
            // {
            //     MySql_Servers = new List<MySqlServer>
            //     {
            //         new() { Server = "server1", Port = 3306, Database = "cs2", Username = "u", Password = "p" },
            //         new() { Server = "server2", Port = 3306, Database = "cs2", Username = "u", Password = "p" },
            //     }
            // },

            // false = keep in-memory data on reconnect; true = reload from storage. Default: false
            PrefsAPI_ReloadOnReconnect = false,

            // After DropPlayer(): false = no data until rejoin; true = instant fresh defaults. Default: true
            PrefsAPI_LoadDefaultAfterDrop = true,

            // Verbose console logging (errors/warnings always show regardless). Default: false
            PrefsAPI_DebugEnable = true,
        });

        //register Multiple store classes 
        _hud = api.CreatePrefs<ClientPrefsHud>(this, new ClientPrefsOptions
        {
            PrefsAPI_CookiesEnable = PrefsAPI_SaveMode.OnMapEnd,
            PrefsAPI_MySqlEnable   = PrefsAPI_SaveMode.Disabled,
            PrefsAPI_TableName     = "test_hud",
            PrefsAPI_DebugEnable   = true,
        });

        _sounds = api.CreatePrefs<ClientPrefsSounds>(this, new ClientPrefsOptions
        {
            PrefsAPI_CookiesEnable = PrefsAPI_SaveMode.OnMapEnd,
            PrefsAPI_MySqlEnable   = PrefsAPI_SaveMode.Disabled,
            PrefsAPI_TableName     = "test_sounds",
            PrefsAPI_DebugEnable   = true,
        });

        if (hotReload)
        {
            _prefs?.Refresh();
            _hud?.Refresh();
            _sounds?.Refresh();
        }
    }

    // ============================================================================
    // STEP 4: Always call Unload() on EACH store (saves unsaved data before cleanup)
    // ============================================================================
    public override void Unload(bool hotReload)
    {
        _prefs?.Unload();
        _hud?.Unload();
        _sounds?.Unload();
    }

    // Do Something On Player Connect By Using OnPlayerLoaded
    [GameEventHandler]
    public HookResult OnPlayerConnectFull(EventPlayerConnectFull @event, GameEventInfo info)
    {
        var player = @event?.Userid;
        if (player == null || !player.IsValid || player.IsBot || player.IsHLTV) return HookResult.Continue;

        // ------------------------------------------------------------------------
        // OnPlayerLoaded — runs your callback ONCE for THIS player as soon as their
        // data is ready (or right away if already loaded). Game thread safe.
        // Call it wherever you want (here or OnClientPutInServer, ...anywhere) — it fires per
        // ------------------------------------------------------------------------
        _prefs?.OnPlayerLoaded(player, (p, data) =>
        {
            // THIS plugin's data is ready — ban checks / setup go here
            p.PrintToChat($" [Loaded] Welcome {p.PlayerName}! Volume={data.Volume}");
        });

        _prefs?.OnPlayerLoaded(player, (p, data) =>
        {
            // fires once EVERY plugin's data is ready for this player
            data.IpAddress = p.IpAddress?.Split(':')[0] ?? "";
            p.PrintToChat($" [Loaded-All] All plugins finished loading you.");
            p.PrintToChat($" [Loaded-All] Your IpAddress Is {data.IpAddress}");
        }, All_Plugins: true); // true = wait until player is loaded in ALL plugins

        return HookResult.Continue;
    }

    // ============================================================================
    // TryGetValue — read/write in-memory (instant, live reference, auto-saved)
    // ============================================================================
    [ConsoleCommand("css_check", "Show all your values from every store")]
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void CmdCheck(CCSPlayerController? player, CommandInfo cmd)
    {
        if (player == null || !player.IsValid) return;

        if (_prefs != null && _prefs.TryGetValue(player, out var prefs))
            cmd.ReplyToCommand($"[Prefs] ChatMuted={prefs.ChatMuted} | Volume={prefs.Volume} | FavPack={prefs.FavPack} | LastIp={prefs.IpAddress}");

        if (_hud != null && _hud.TryGetValue(player, out var hud))
            cmd.ReplyToCommand($"[Hud] ShowHud={hud.ShowHud} | HudColor=0x{hud.HudColor:X}");

        if (_sounds != null && _sounds.TryGetValue(player, out var sounds))
            cmd.ReplyToCommand($"[Sounds] SoundsEnabled={sounds.SoundsEnabled}");
    }

    [ConsoleCommand("css_vol", "Set your volume (modify example)")]
    [CommandHelper(minArgs: 1, usage: "<0-100>", whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void CmdVol(CCSPlayerController? player, CommandInfo cmd)
    {
        if (_prefs == null || player == null || !player.IsValid) return;
        if (!int.TryParse(cmd.GetArg(1), out var vol)) return;

        _prefs.TryGetValue(player, data =>
        {
            data.Volume = Math.Clamp(vol, 0, 100);               // just edit — saving is automatic
            cmd.ReplyToCommand($"Volume = {data.Volume}");
        });
    }

    [ConsoleCommand("css_hud", "Toggle HUD (second isolated store)")]
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void CmdHud(CCSPlayerController? player, CommandInfo cmd)
    {
        if (_hud == null || player == null || !player.IsValid) return;

        if(!_hud.TryGetValue(player, out var data)) return; // you can do it like that shortcut read/write you can use player/player.Slot/player.SteamID

        data.ShowHud = !data.ShowHud;
        
        cmd.ReplyToCommand($"ShowHud = {data.ShowHud}");
    }

    [ConsoleCommand("css_sound", "Toggle SoundsEnabled (third isolated store)")]
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void CmdSound(CCSPlayerController? player, CommandInfo cmd)
    {
        if (_sounds == null || player == null || !player.IsValid) return;

        if(!_sounds.TryGetValue(player.Slot, out var data)) return; // you can do it like that shortcut read/write you can use player/player.Slot/player.SteamID

        data.SoundsEnabled = !data.SoundsEnabled;
        cmd.ReplyToCommand($"SoundsEnabled = {data.SoundsEnabled}");
    }

    [ConsoleCommand("css_getsteam", "Read an in-memory player by SteamID64")]
    [CommandHelper(minArgs: 1, usage: "<steamid64>", whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void CmdGetSteam(CCSPlayerController? player, CommandInfo cmd)
    {
        if (_prefs == null || !ulong.TryParse(cmd.GetArg(1), out var steamId)) return;

        if (!_prefs.TryGetValue(steamId, out var data)) // you can do it like that shortcut read/write you can use player/player.Slot/player.SteamID
        {
            cmd.ReplyToCommand("Not in memory (offline? use css_db).");
            return;
        }
        cmd.ReplyToCommand($"[{steamId}] Volume={data.Volume} | FavPack={data.FavPack}");
    }

    // ============================================================================
    // DataBase — works for OFFLINE players (cookies + MySQL per config, newest wins)
    // ============================================================================
    [ConsoleCommand("css_db", "Get one player from database")]
    [CommandHelper(minArgs: 1, usage: "<steamid64>", whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void CmdDb(CCSPlayerController? player, CommandInfo cmd)
    {
        if (_prefs == null || !ulong.TryParse(cmd.GetArg(1), out var steamId)) return;
        var caller = player;

        _prefs.FetchPlayer(steamId, data =>
        {
            if (caller == null || !caller.IsValid) return;
            caller.PrintToChat(data == null
                ? $" [DB] No saved data for {steamId}."
                : $" [DB] {steamId}: Volume={data.Volume} | FavPack={data.FavPack} | IpAddress={data.IpAddress}");
        });
    }

    //If not Found the value it will return | bool false | int 0 | string empty | ect...
    [ConsoleCommand("css_db_all", "Get one player from EVERY plugin (uses <R>)")]
    [CommandHelper(minArgs: 1, usage: "<steamid64>", whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void CmdDbAll(CCSPlayerController? player, CommandInfo cmd)
    {
        if (_prefs == null || !ulong.TryParse(cmd.GetArg(1), out var steamId)) return;
        var caller = player;

        _prefs.FetchPlayer<CrossRow>(steamId, results =>
        {
            if (caller == null || !caller.IsValid) return;
            if (results.Count == 0) { caller.PrintToChat(" [DB-All] No data in any plugin."); return; }

            foreach (var r in results)
                caller.PrintToChat($" [{r.Plugin} / {r.Table}] Volume={r.Data.Volume} | IpAddress={r.Data.IpAddress} | Toggle_Something={r.Data.Toggle_Something}");
        }, true); // <---- true To GetFromDataBase All Plugins
    }

    [ConsoleCommand("css_search", "Find all players by field value")]
    [CommandHelper(minArgs: 2, usage: "<field> <value>", whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void CmdSearch(CCSPlayerController? player, CommandInfo cmd)
    {
        if (_prefs == null) return;
        var caller = player;

        // examples: !search IpAddress 1.2.3.4 | !search Volume 75 | !search PlayerName GoldKingZ
        _prefs.SearchByField(cmd.GetArg(1), cmd.GetArg(2), results =>
        {
            if (caller == null || !caller.IsValid) return;
            if (results.Count == 0) { caller.PrintToChat(" No players found."); return; }

            foreach (var r in results)
                caller.PrintToChat($" {r.PlayerName} ({r.PlayerSteamID}) — Volume={r.Data.Volume}");
        });
    }

    [ConsoleCommand("css_search_all", "Find players across EVERY plugin (uses <R>)")]
    [CommandHelper(minArgs: 2, usage: "<field> <value>", whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void CmdSearchAll(CCSPlayerController? player, CommandInfo cmd)
    {
        if (_prefs == null) return;
        var caller = player;

        // examples: !search_all IpAddress 1.2.3.4 | !search_all Volume 75 | !search_all PlayerName GoldKingZ
        _prefs.SearchByField<CrossRow>(cmd.GetArg(1), cmd.GetArg(2), results =>
        {
            if (caller == null || !caller.IsValid) return;
            if (results.Count == 0) { caller.PrintToChat(" No players found in any plugin."); return; }

            foreach (var r in results)
                caller.PrintToChat($" [{r.Plugin} / {r.Table}] {r.PlayerName} ({r.PlayerSteamID}) — Volume={r.Data.Volume} — IpAddress={r.Data.IpAddress} — Toggle_Something={r.Data.Toggle_Something}");
        }, true); // <---- true To SearchByField In All Plugins
    }

    [ConsoleCommand("css_savedb", "Modify an OFFLINE player and save (this plugin only)")]
    [CommandHelper(minArgs: 2, usage: "<steamid64> <volume>", whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void CmdSaveDb(CCSPlayerController? player, CommandInfo cmd)
    {
        if (_prefs == null || !ulong.TryParse(cmd.GetArg(1), out var steamId) || !int.TryParse(cmd.GetArg(2), out var vol)) return;
        var caller = player;

        _prefs.ModifyAndSave(steamId, data =>
        {
            data.Volume = Math.Clamp(vol, 0, 100);
        }, done =>
        {
            //when done do this action
            if (caller == null || !caller.IsValid) return;
            caller.PrintToChat(done ? $" [DB] {steamId} saved." : " [DB] Save failed (no storage / servers down).");
        });
    }
    // ============================================================================
    // ForceSave — save now.  done: optional (true = saved OK).  All_Plugins: true = every plugin.
    // ============================================================================

    // 1) ForceSave — no callback, this plugin only
    [ConsoleCommand("css_save", "Save yourself now (this plugin)")]
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void CmdSave(CCSPlayerController? player, CommandInfo cmd)
    {
        if (_prefs == null || player == null || !player.IsValid) return;
        _prefs.ForceSave(player);
        cmd.ReplyToCommand("Saved (this plugin).");
    }

    // 2) ForceSave — no callback, ALL plugins
    [ConsoleCommand("css_save_all", "Save yourself now (ALL plugins)")]
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void CmdSaveAll(CCSPlayerController? player, CommandInfo cmd)
    {
        if (_prefs == null || player == null || !player.IsValid) return;
        _prefs.ForceSave(player, All_Plugins: true);
        cmd.ReplyToCommand("Saved (all plugins).");
    }

    // 3) ForceSave — with done callback, ALL plugins, by SteamID64 (must be in memory)
    [ConsoleCommand("css_save_all_steam", "Save a player by SteamID64 (ALL plugins) with result")]
    [CommandHelper(minArgs: 1, usage: "<steamid64>", whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void CmdSaveSteam(CCSPlayerController? player, CommandInfo cmd)
    {
        if (_prefs == null || !ulong.TryParse(cmd.GetArg(1), out var steamId)) return;
        var caller = player;

        _prefs.ForceSave(steamId, done =>
        {
            if (caller == null || !caller.IsValid) return;
            caller.PrintToChat(done
                ? $" [Save] {steamId} saved (all plugins)."
                : $" [Save] {steamId} not in memory / nothing to save.");
        }, All_Plugins: true);
    }

    // ============================================================================
    // DropPlayer — wipe now.  done: optional (true = wiped).  All_Plugins: true = every plugin.
    // ============================================================================

    // 1) DropPlayer — no callback, this plugin only
    [ConsoleCommand("css_drop", "Wipe your data (this plugin)")]
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void CmdDrop(CCSPlayerController? player, CommandInfo cmd)
    {
        if (_prefs == null || player == null || !player.IsValid) return;
        _prefs.DropPlayer(player);
        cmd.ReplyToCommand("Wiped (this plugin).");
    }

    // 2) DropPlayer — no callback, ALL plugins
    [ConsoleCommand("css_drop_all", "Wipe your data (ALL plugins)")]
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void CmdDropAll(CCSPlayerController? player, CommandInfo cmd)
    {
        if (_prefs == null || player == null || !player.IsValid) return;
        _prefs.DropPlayer(player, All_Plugins: true);
        cmd.ReplyToCommand("Wiped (all plugins).");
    }

    // 3) DropPlayer — with done callback, ALL plugins, by SteamID64 (works ONLINE + OFFLINE)
    [ConsoleCommand("css_drop_all_steam", "Wipe a player by SteamID64 (ALL plugins) with result — works offline")]
    [CommandHelper(minArgs: 1, usage: "<steamid64>", whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void CmdWipeAll(CCSPlayerController? player, CommandInfo cmd)
    {
        if (_prefs == null || !ulong.TryParse(cmd.GetArg(1), out var steamId)) return;
        var caller = player;

        _prefs.DropPlayer(steamId, done =>
        {
            if (caller == null || !caller.IsValid) return;
            caller.PrintToChat(done
                ? $" [Wipe-All] {steamId} wiped from ALL plugins."
                : $" [Wipe-All] Some stores had nothing to wipe for {steamId}.");
        }, All_Plugins: true);
    }

    // ============================================================================
    // Lifecycle
    // ============================================================================
    [ConsoleCommand("css_refresh", "Save + reload all players (server only)")]
    [CommandHelper(whoCanExecute: CommandUsage.SERVER_ONLY)]
    public void CmdRefresh(CCSPlayerController? player, CommandInfo cmd)
    {
        _prefs?.Refresh();
        cmd.ReplyToCommand("Refreshed.");
    }
}

// ============================================================================
// QUICK REFERENCE:
//   !check                        — show all your values from every store
//   !vol <0-100>                  — modify a value (auto-saved)
//   !hud                          — toggle ShowHud (isolated store "test_hud")
//   !sound                        — toggle SoundsEnabled (isolated store "test_sounds")
//   !getsteam <steamid>           — read in-memory player by SteamID64
//   !db <steamid>                 — get one player from database (offline OK)
//   !db_all <steamid>             — same, from EVERY plugin (<R>)
//   !search <field> <value>       — find all players by field value
//   !search_all <field> <val>     — same, across EVERY plugin (<R>)
//   !savedb <steamid> <vol>       — modify OFFLINE player + save (this plugin only)
//   !save / !save_all             — force save (this plugin / all plugins)
//   !save_all_steam <steamid>     — force save by SteamID64 with result (all plugins)
//   !drop / !drop_all             — wipe data (this plugin / all plugins)
//   !drop_all_steam <steamid>     — wipe by SteamID64 with result, works offline (all plugins)
// ============================================================================