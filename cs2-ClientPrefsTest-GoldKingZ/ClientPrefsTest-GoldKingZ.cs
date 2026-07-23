using ClientPrefs_GoldKingZ.Shared;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using Microsoft.Extensions.Logging;

namespace ClientPrefsTest_GoldKingZ;

// ============================================================================
// STEP 1: Define your data class(es)
//
// - Only declare fields YOUR plugin needs
// - PlayerName, PlayerSteamID, DateAndTime are RESERVED by ClientPrefs
//   (do NOT add them here — ClientPrefs injects them automatically)
// - C# default values (= false, = 0, = "") are the starting state for new players
// - Supported types: bool, int, long, ulong, float, double, string, DateTime
//
// ISOLATION (NEW):
//   One plugin can register MORE THAN ONE prefs store. Each store is a separate
//   class and a separate table. Below we register two:
//
//     ClientPrefs     -> general per-player settings  (table: auto-named)
//     ClientPrefsHud  -> isolated HUD settings        (table: custom "test_hud")
//
//   Call CreatePrefs<T>() once per class. They never share columns or rows.
//
// AUTO-MIGRATION:
//   You can freely edit these classes at any time — add, remove, or rename fields.
//   ClientPrefs auto-updates cookies.db AND the MySQL table to match.
//   Add a field -> new column; 
//   remove a field -> column dropped;
//   change a type -> column type updated.
//   No data loss, no manual DB edits.
// ============================================================================
public sealed class ClientPrefs
{
    public bool   ChatMuted  { get; set; } = false;
    public int    Volume     { get; set; } = 50;
    public float  XAxis      { get; set; } = 0f;
    public float  YAxis      { get; set; } = 0f;
    public string FavPack    { get; set; } = "";
    public int    Mode       { get; set; } = 0;
}

// Second, fully isolated store for this same plugin (goal: multiple isolate).
public sealed class ClientPrefsHud
{
    public bool ShowHud    { get; set; } = true;
    public int  HudColor   { get; set; } = 0xFFFFFF;
    public bool ShowTimer  { get; set; } = false;
}

public sealed class ClientPrefsTestPlugin : BasePlugin
{
    public override string ModuleName    => "ClientPrefs Test Plugin";
    public override string ModuleVersion => "1.0.3";
    public override string ModuleAuthor => "Gold KingZ";
    public override string ModuleDescription => "https://github.com/oqyh";

    // ============================================================================
    // STEP 2: Declare your store variables (one per data class)
    //
    // - IPrefsStore<T> is the main API you interact with
    // - Nullable because ClientPrefs core might not be loaded
    // ============================================================================
    private IPrefsStore<ClientPrefs>?    _prefs;     // general settings
    private IPrefsStore<ClientPrefsHud>? _hud;       // isolated HUD settings

    // ============================================================================
    // STEP 3: Register your prefs in OnAllPluginsLoaded
    //
    // - This runs AFTER all plugins are loaded (including ClientPrefs core)
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

        // ----- STORE #1: general settings -----------------------------------
        _prefs = api.CreatePrefs<ClientPrefs>(this, new ClientPrefsOptions
        {
            // ================================================================
            // PrefsAPI_CookiesEnable
            // ================================================================
            // When to save player data to cookies (SQLite, local file on server)
            //
            // Values:
            //   PrefsAPI_SaveMode.Disabled            = don't save to cookies
            //   PrefsAPI_SaveMode.OnPlayerDisconnect  = save when player leaves
            //   PrefsAPI_SaveMode.OnMapEnd            = save when map changes
            //
            // Default if not set: PrefsAPI_SaveMode.Disabled
            // ================================================================
            PrefsAPI_CookiesEnable = PrefsAPI_SaveMode.Disabled,

            // Auto-delete inactive players from cookies after X days (0 = never).
            // Runs once on every map change. Default: 7
            PrefsAPI_CookiesAutoRemoveInactivePlayersOlderThanDays = 30,

            // ================================================================
            // PrefsAPI_MySqlEnable
            // ================================================================
            // When to save player data to MySQL (remote database).
            // If both cookies and MySQL are enabled, MySQL is the source of truth
            // (MySQL values override cookies values on player connect).
            // Default: PrefsAPI_SaveMode.Disabled
            // ================================================================
            PrefsAPI_MySqlEnable = PrefsAPI_SaveMode.OnMapEnd,

            // Auto-delete inactive players from MySQL after X days (0 = never). Default: 7
            PrefsAPI_MySqlAutoRemoveInactivePlayersOlderThanDays = 30,

            PrefsAPI_MySqlConnectionTimeout = 30,   // seconds, default 30
            PrefsAPI_MySqlRetryAttempts     = 3,    // default 3
            PrefsAPI_MySqlRetryDelay        = 2,    // seconds between retries, default 2

            // ================================================================
            // PrefsAPI_TableName  
            // ================================================================
            // Custom table name — applies to BOTH SQLite (cookies.db) and MySQL
            // Default if not set: <FolderName>_<ClassName>
            // Example if not set: Plugin-A-GoldKingZ + ClientPrefs → Plugin_A_GoldKingZ_ClientPrefs
            // ================================================================
            PrefsAPI_TableName = null,

            // MySQL server connection details (single server):
            PrefsAPI_MySqlConfig = new MySqlConfig
            {
                Server   = "localhost",
                Port     = 3306,
                Database = "test",
                Username = "root",
                Password = "",
            },

            // For Multiple MySQL servers (saves/deletes go to ALL servers, loads use the NEWEST row):
            // - Save  → written to every reachable server (unreachable ones sync on the player's next save)
            // - Load  → all servers checked, the row with the newest DateAndTime wins
            // PrefsAPI_MySqlConfig = new MySqlConfig
            // {
            //     MySql_Servers = new List<MySqlServer>
            //     {
            //         new() { Server = "server1", Port = 3306, Database = "cs2", Username = "u", Password = "p" },
            //         new() { Server = "server2", Port = 3306, Database = "cs2", Username = "u", Password = "p" },
            //     }
            // },

            // false = keep in-memory data on reconnect; true = reload from storage. Default false
            PrefsAPI_ReloadOnReconnect = false,

            // After DropPlayer(): false = no data until rejoin; true = instant fresh defaults. Default true
            PrefsAPI_LoadDefaultAfterDrop = true,

            // Verbose console logging (errors/warnings always show regardless). Default false
            PrefsAPI_DebugEnable = true,
        });

        // ----- STORE #2: isolated HUD settings ------------------------------
        // Same plugin, second class, its own table. Here we force a CUSTOM name
        // "test_hud" — this exact name is used on SQLite.
        _hud = api.CreatePrefs<ClientPrefsHud>(this, new ClientPrefsOptions // ***** NOTE In This Example : It Will Save Only In SQLite With Table "test_hud" Only Because Mysql Disabled 
        {
            PrefsAPI_CookiesEnable = PrefsAPI_SaveMode.OnMapEnd,
            PrefsAPI_MySqlEnable   = PrefsAPI_SaveMode.Disabled,
            PrefsAPI_TableName     = "test_hud",   
            PrefsAPI_DebugEnable   = true,
        });

        // If this is a hot reload, refresh all connected players for every store
        if (hotReload)
        {
            _prefs?.Refresh();
            _hud?.Refresh();
        }
    }

    // ============================================================================
    // STEP 4: Handle plugin unload
    //
    // - Always call Unload() on EACH store to save unsaved data before cleanup
    // ============================================================================
    public override void Unload(bool hotReload)
    {
        _prefs?.Unload();
        _hud?.Unload();
    }

    // ============================================================================
    // TryGetValue — out variable style
    //
    // Get direct access to the player's data object.
    // Returns false if player is not loaded (null, bot, HLTV, or not connected).
    // The returned 'data' is a LIVE REFERENCE — any changes you make are
    // automatically tracked and saved on disconnect/map end. No Save() needed.
    // ============================================================================
    [ConsoleCommand("css_get", "Get player data using out variable")]
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void TestGet(CCSPlayerController? player, CommandInfo cmd)
    {
        if (_prefs == null) return;
        if (player == null || !player.IsValid) return;

        if (!_prefs.TryGetValue(player.Slot, out var data)) return;

        cmd.ReplyToCommand($"ChatMuted={data.ChatMuted}, Volume={data.Volume}, Mode={data.Mode}, FavPack={data.FavPack}");
    }

    // ============================================================================
    // TryGetValue — reading from the SECOND (isolated) store
    //
    // _hud is a totally separate store/table. Same API, different data class.
    // ============================================================================
    [ConsoleCommand("css_hud", "Get HUD data from the isolated store")]
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void TestHudGet(CCSPlayerController? player, CommandInfo cmd)
    {
        if (_hud == null) return;
        if (player == null || !player.IsValid) return;
        if (!_hud.TryGetValue(player.Slot, out var data)) return;

        cmd.ReplyToCommand($"[HUD] ShowHud={data.ShowHud}, HudColor=0x{data.HudColor:X}, ShowTimer={data.ShowTimer}");
    }

    // ============================================================================
    // Modify the isolated HUD store
    // ============================================================================
    [ConsoleCommand("css_hud_toggle", "Toggle HUD visibility (isolated store)")]
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void TestHudToggle(CCSPlayerController? player, CommandInfo cmd)
    {
        if (_hud == null) return;
        if (player == null || !player.IsValid) return;
        if (!_hud.TryGetValue(player.Slot, out var data)) return;

        data.ShowHud = !data.ShowHud;
        cmd.ReplyToCommand($"[HUD] ShowHud is now: {data.ShowHud}");
    }

    // ============================================================================
    // TryGetValue — out variable with player controller
    // ============================================================================
    [ConsoleCommand("css_get2", "Get player data using player controller")]
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void TestGet2(CCSPlayerController? player, CommandInfo cmd)
    {
        if (_prefs == null) return;
        if (player == null || !player.IsValid) return;
        if (!_prefs.TryGetValue(player, out var data)) return;

        cmd.ReplyToCommand($"XAxis={data.XAxis}, YAxis={data.YAxis}");
    }

    // ============================================================================
    // TryGetValue — modify data using out variable
    // ============================================================================
    [ConsoleCommand("css_toggle", "Toggle ChatMuted")]
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void TestToggle(CCSPlayerController? player, CommandInfo cmd)
    {
        if (_prefs == null) return;
        if (player == null || !player.IsValid) return;
        if (!_prefs.TryGetValue(player.Slot, out var data)) return;

        data.ChatMuted = !data.ChatMuted;
        cmd.ReplyToCommand($"ChatMuted is now: {data.ChatMuted}");
    }

    // ============================================================================
    // TryGetValue — Action/callback style
    // ============================================================================
    [ConsoleCommand("css_action", "Modify data using Action callback")]
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void TestAction(CCSPlayerController? player, CommandInfo cmd)
    {
        if (_prefs == null) return;
        if (player == null || !player.IsValid) return;

        _prefs.TryGetValue(player.Slot, data =>
        {
            data.Volume = 75;
            data.Mode = 3;
            data.FavPack = "test_pack";

            cmd.ReplyToCommand($"Set Volume={data.Volume}, Mode={data.Mode}, FavPack={data.FavPack}");
        });
    }

    // ============================================================================
    // TryGetValue — Action with player controller
    // ============================================================================
    [ConsoleCommand("css_action2", "Modify data using Action with player controller")]
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void TestAction2(CCSPlayerController? player, CommandInfo cmd)
    {
        if (_prefs == null) return;
        if (player == null || !player.IsValid) return;

        _prefs.TryGetValue(player, data =>
        {
            data.XAxis = 100.5f;
            data.YAxis = -50.3f;
        });

        cmd.ReplyToCommand("Set XAxis=100.5, YAxis=-50.3");
    }

    // ============================================================================
    // ForceSave — save one player immediately (THIS store only)
    // ============================================================================
    [ConsoleCommand("css_forcesave", "Force save this player now")]
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void TestForceSave(CCSPlayerController? player, CommandInfo cmd)
    {
        if (_prefs == null) return;
        if (player == null || !player.IsValid) return;

        _prefs.ForceSave(player);
        cmd.ReplyToCommand("Force saved your data for this plugin Only");
    }

    // ============================================================================
    // ForceSavePlayer_To_All_Instances — save one player across ALL stores/plugins
    //
    // Saves this player's data in EVERY ClientPrefs store (every class in every
    // plugin), including this plugin's own _prefs AND _hud.
    // ============================================================================
    [ConsoleCommand("css_forcesave_all", "Force save this player across all stores")]
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void TestForceSaveAll(CCSPlayerController? player, CommandInfo cmd)
    {
        if (_prefs == null) return;
        if (player == null || !player.IsValid) return;

        _prefs.ForceSavePlayer_To_All_Instances(player);
        cmd.ReplyToCommand("Force saved your data across All Plugins");
    }

    // ============================================================================
    // DropPlayer — wipe one player from THIS store
    // ============================================================================
    [ConsoleCommand("css_drop", "Wipe this player's data for this store")]
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void TestDrop(CCSPlayerController? player, CommandInfo cmd)
    {
        if (_prefs == null) return;
        if (player == null || !player.IsValid) return;

        _prefs.DropPlayer(player);
        cmd.ReplyToCommand("Your data has been wiped for this plugin Only. Rejoin to get defaults.");
    }

    // ============================================================================
    // DropPlayer_To_All_Instances — wipe one player from ALL stores/plugins
    // ============================================================================
    [ConsoleCommand("css_drop_all", "Wipe this player's data from ALL stores")]
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void TestDropAll(CCSPlayerController? player, CommandInfo cmd)
    {
        if (_prefs == null) return;
        if (player == null || !player.IsValid) return;

        _prefs.DropPlayer_To_All_Instances(player);
        cmd.ReplyToCommand("Your data has been wiped from All Plugins");
    }

    // ============================================================================
    // Refresh — save all + reload all players (THIS store only)
    // ============================================================================
    [ConsoleCommand("css_refresh", "Refresh - save and reload all players")]
    [CommandHelper(whoCanExecute: CommandUsage.SERVER_ONLY)]
    public void TestRefresh(CCSPlayerController? player, CommandInfo cmd)
    {
        if (_prefs == null) return;

        _prefs.Refresh();
        cmd.ReplyToCommand("Refreshed — all players saved and reloaded from storage.");
    }

    // ============================================================================
    // Unload — save all + clear memory (THIS store only)
    // ============================================================================
    [ConsoleCommand("css_unload", "Unload - save all and clear memory")]
    [CommandHelper(whoCanExecute: CommandUsage.SERVER_ONLY)]
    public void TestUnload(CCSPlayerController? player, CommandInfo cmd)
    {
        if (_prefs == null) return;

        _prefs.Unload();
        cmd.ReplyToCommand("Unloaded — all players saved, memory cleared.");
    }

    // ============================================================================
    // PRACTICAL EXAMPLE: Using prefs in game logic
    // ============================================================================
    [ConsoleCommand("css_chat", "Send a message if not muted")]
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void TestChat(CCSPlayerController? player, CommandInfo cmd)
    {
        if (_prefs == null) return;
        if (player == null || !player.IsValid) return;
        if (!_prefs.TryGetValue(player.Slot, out var data)) return;

        if (data.ChatMuted)
        {
            cmd.ReplyToCommand("You have chat muted. Use !css_toggle to unmute.");
            return;
        }

        cmd.ReplyToCommand($"Chat is enabled! Your volume is {data.Volume}.");
    }

    // ============================================================================
    // PRACTICAL EXAMPLE: Setting multiple values at once
    // ============================================================================
    [ConsoleCommand("css_reset", "Reset all values to default")]
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void TestReset(CCSPlayerController? player, CommandInfo cmd)
    {
        if (_prefs == null) return;
        if (player == null || !player.IsValid) return;
        _prefs.TryGetValue(player.Slot, data =>
        {
            data.ChatMuted = false;
            data.Volume = 50;
            data.XAxis = 0f;
            data.YAxis = 0f;
            data.FavPack = "";
            data.Mode = 0;
        });

        cmd.ReplyToCommand("All your settings have been reset to defaults.");
    }

    // ============================================================================
    // PRACTICAL EXAMPLE: Setting a value from command argument
    // ============================================================================
    [ConsoleCommand("css_vol", "Set volume 0-100")]
    [CommandHelper(minArgs: 1, usage: "<0-100>", whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void TestVolume(CCSPlayerController? player, CommandInfo cmd)
    {
        if (_prefs == null) return;
        if (player == null || !player.IsValid) return;
        if (!_prefs.TryGetValue(player.Slot, out var data)) return;

        if (!int.TryParse(cmd.GetArg(1), out var vol))
        {
            cmd.ReplyToCommand("Usage: !css_vol <0-100>");
            return;
        }

        var vol_before = data.Volume;
        data.Volume = Math.Clamp(vol, 0, 100);
        cmd.ReplyToCommand($"Changed Volume From {vol_before} To {data.Volume}.");
    }

    // ============================================================================
    // PRACTICAL EXAMPLE: Setting a string value
    // ============================================================================
    [ConsoleCommand("css_pack", "Set favorite pack name")]
    [CommandHelper(minArgs: 1, usage: "<pack_name>", whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void TestPack(CCSPlayerController? player, CommandInfo cmd)
    {
        if (_prefs == null) return;
        if (player == null || !player.IsValid) return;
        if (!_prefs.TryGetValue(player.Slot, out var data)) return;

        data.FavPack = cmd.GetArg(1);
        cmd.ReplyToCommand($"Favorite pack set to: {data.FavPack}");
    }

    // ============================================================================
    // PRACTICAL EXAMPLE: Reading another player's data
    // ============================================================================
    [ConsoleCommand("css_inspect", "Inspect a player by slot number")]
    [CommandHelper(minArgs: 1, usage: "<slot>", whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void TestInspect(CCSPlayerController? player, CommandInfo cmd)
    {
        if (_prefs == null) return;
        if (!int.TryParse(cmd.GetArg(1), out var slot))
        {
            cmd.ReplyToCommand("Usage: !css_inspect <slot_number>");
            return;
        }

        if (!_prefs.TryGetValue(slot, out var data))
        {
            cmd.ReplyToCommand($"No data found for slot {slot}.");
            return;
        }

        cmd.ReplyToCommand($"Slot {slot}: Muted={data.ChatMuted}, Vol={data.Volume}, Mode={data.Mode}, Pack={data.FavPack}");
    }
}

// ============================================================================
// QUICK REFERENCE — all commands in this test plugin:
//
// GENERAL STORE (_prefs):
//   !css_get            — read your data (out variable, slot)
//   !css_get2           — read your data (out variable, player controller)
//   !css_toggle         — toggle ChatMuted true/false
//   !css_action         — modify data using Action callback (slot)
//   !css_action2        — modify data using Action callback (player controller)
//   !css_forcesave      — force save your data (this store only)
//   !css_forcesave_all  — force save your data (all stores/plugins)
//   !css_drop           — wipe your data (this store only)
//   !css_drop_all       — wipe your data (all stores/plugins)
//   !css_refresh        — save + reload all players (server only)
//   !css_unload         — save + clear memory (server only)
//   !css_chat           — practical: check ChatMuted before action
//   !css_reset          — practical: reset all fields to default
//   !css_vol <0-100>    — practical: set volume from command arg
//   !css_pack <name>    — practical: set string value
//   !css_inspect <slot> — practical: read another player's data
//
// ISOLATED HUD STORE (_hud) — separate class + table "test_hud":
//   !css_hud            — read HUD data from the isolated store
//   !css_hud_toggle     — toggle HUD visibility (isolated store)
// ============================================================================
