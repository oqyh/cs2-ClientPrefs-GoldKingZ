using ClientPrefs_GoldKingZ.Shared;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using Microsoft.Extensions.Logging;

namespace ClientPrefsTest_GoldKingZ;

// ============================================================================
// STEP 1: Define your data class
//
// - Only declare fields YOUR plugin needs
// - PlayerName, PlayerSteamID, DateAndTime are RESERVED by ClientPrefs
//   (do NOT add them here — ClientPrefs injects them automatically)
// - C# default values (= false, = 0, = "") are the starting state for new players
// - Supported types: bool, int, long, ulong, float, double, string, DateTime
//
// AUTO-MIGRATION:
//   You can freely edit this class at any time — add, remove, or rename fields.
//   ClientPrefs will automatically update your cookies.db and MySQL table
//   to match your changes. No data loss, no need to delete the database.
//
//   Add a new field    → new column added, existing players keep their data
//   Remove a field     → column dropped, other columns stay untouched
//   Change field type  → column type updated (table rebuilt safely for cookies.db,
//                         MODIFY COLUMN for MySQL)
//
//   Just edit this class, rebuild your plugin, and reload. Done.
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

public sealed class ClientPrefsTestPlugin : BasePlugin
{
    public override string ModuleName    => "ClientPrefs Test Plugin";
    public override string ModuleVersion => "1.0.0";
    public override string ModuleAuthor => "Gold KingZ";
    public override string ModuleDescription => "https://github.com/oqyh";

    // ============================================================================
    // STEP 2: Declare your store variable
    //
    // - IPrefsStore<T> is the main API you interact with
    // - Nullable because ClientPrefs core might not be loaded
    // ============================================================================
    private IPrefsStore<ClientPrefs>? _prefs;

    // ============================================================================
    // STEP 3: Register your prefs in OnAllPluginsLoaded
    //
    // - This runs AFTER all plugins are loaded (including ClientPrefs core)
    // - ClientPrefsApi.Get() returns null if core plugin is not installed
    // - CreatePrefs<T>() registers your data class and sets up cookies/mysql
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
            // PrefsAPI_CookiesEnable
            // ================================================================
            // When to save player data to cookies.db (local file on server)
            //
            // Values:
            //   PrefsAPI_SaveMode.Disabled            = don't save to cookies
            //   PrefsAPI_SaveMode.OnPlayerDisconnect  = save when player leaves the server
            //   PrefsAPI_SaveMode.OnMapEnd            = save when map changes
            //
            // Default if not set: PrefsAPI_SaveMode.Disabled
            // ================================================================
            PrefsAPI_CookiesEnable = PrefsAPI_SaveMode.OnPlayerDisconnect,
 
            // ================================================================
            // PrefsAPI_CookiesAutoRemoveInactivePlayersOlderThanDays
            // ================================================================
            // Auto-delete inactive players from cookies.db after X days
            // Runs once on every map change
            //
            // Values:
            //   0   = never delete (disabled)
            //   1+  = delete players who haven't joined in X days
            //
            // Default if not set: 7
            // ================================================================
            PrefsAPI_CookiesAutoRemoveInactivePlayersOlderThanDays = 30,
 
            // ================================================================
            // PrefsAPI_MySqlEnable
            // ================================================================
            // When to save player data to MySQL (remote database)
            // If both cookies and MySQL are enabled, MySQL is the source of truth
            // (MySQL values override cookies values on player connect)
            //
            // Values:
            //   PrefsAPI_SaveMode.Disabled            = don't save to MySQL
            //   PrefsAPI_SaveMode.OnPlayerDisconnect  = save when player leaves the server
            //   PrefsAPI_SaveMode.OnMapEnd            = save when map changes
            //
            // Default if not set: PrefsAPI_SaveMode.Disabled
            // ================================================================
            PrefsAPI_MySqlEnable = PrefsAPI_SaveMode.OnMapEnd,
 
            // ================================================================
            // PrefsAPI_MySqlAutoRemoveInactivePlayersOlderThanDays
            // ================================================================
            // Auto-delete inactive players from MySQL after X days
            // Runs once on every map change
            //
            // Values:
            //   0   = never delete (disabled)
            //   1+  = delete players who haven't joined in X days
            //
            // Default if not set: 7
            // ================================================================
            PrefsAPI_MySqlAutoRemoveInactivePlayersOlderThanDays = 30,
 
            // ================================================================
            // PrefsAPI_MySqlConnectionTimeout
            // ================================================================
            // How long to wait (in seconds) for a MySQL connection before timeout
            //
            // Values: any positive integer (seconds)
            // Default if not set: 30
            // ================================================================
            PrefsAPI_MySqlConnectionTimeout = 30,
 
            // ================================================================
            // PrefsAPI_MySqlRetryAttempts
            // ================================================================
            // How many times to retry connecting to MySQL before giving up
            //
            // Values: any positive integer
            // Default if not set: 3
            // ================================================================
            PrefsAPI_MySqlRetryAttempts = 3,
 
            // ================================================================
            // PrefsAPI_MySqlRetryDelay
            // ================================================================
            // How long to wait (in seconds) between each retry attempt
            //
            // Values: any positive integer (seconds)
            // Default if not set: 2
            // ================================================================
            PrefsAPI_MySqlRetryDelay = 2,
 
            // ================================================================
            // PrefsAPI_MySqlTableName
            // ================================================================
            // Override the MySQL table name
            // If null, the table name is auto-generated: ClientPrefs_<PluginFolderName>
            //
            // Values: any string (or null for auto)
            // Default if not set: null (auto-generated)
            // ================================================================
            PrefsAPI_MySqlTableName = null,
 
            // ================================================================
            // PrefsAPI_MySqlConfig
            // ================================================================
            // MySQL server connection details
            //
            // Single server:
            PrefsAPI_MySqlConfig = new MySqlConfig
            {
                Server   = "localhost",
                Port     = 3306,
                Database = "cs2",
                Username = "user",
                Password = "pass",
            },


            // Multiple servers (tried in order, first one that connects wins):
            // PrefsAPI_MySqlConfig = new MySqlConfig
            // {
            //     MySql_Servers = new List<MySqlServer>
            //     {
            //         new() { Server = "primary",   Port = 3306, Database = "cs2", Username = "u", Password = "p" },
            //         new() { Server = "secondary", Port = 3306, Database = "cs2", Username = "u", Password = "p" },
            //     }
            // },
 
            // ================================================================
            // PrefsAPI_ReloadOnReconnect
            // ================================================================
            // What happens when a player reconnects mid-map
            // (their data is still in memory from before they disconnected)
            //
            // Values:
            //   false = keep current in-memory data, skip loading from storage
            //   true  = clear memory and reload fresh from cookies.db / MySQL
            //
            // Default if not set: false
            // ================================================================
            PrefsAPI_ReloadOnReconnect = false,
 
            // ================================================================
            // PrefsAPI_LoadDefaultAfterDrop
            // ================================================================
            // What happens after DropPlayer() wipes a player's data
            //
            // Values:
            //   false = player has NO data until they rejoin the server
            //   true  = player instantly gets fresh default values without rejoining
            //
            // Default if not set: true
            // ================================================================
            PrefsAPI_LoadDefaultAfterDrop = true,
 
            // ================================================================
            // PrefsAPI_DebugEnable
            // ================================================================
            // Show detailed debug messages in server console
            // Errors and warnings are ALWAYS shown regardless of this setting
            //
            // Values:
            //   false = only show errors and warnings
            //   true  = show everything (player loads, saves, migrations, cleanup, etc.)
            //
            // Default if not set: false
            // ================================================================
            PrefsAPI_DebugEnable = true,
        });

        // If this is a hot reload, refresh all connected players
        if (hotReload)
        {
            _prefs.Refresh();
        }
    }

    // ============================================================================
    // STEP 4: Handle plugin unload
    //
    // - Always call Unload() to save any unsaved player data before cleanup
    // ============================================================================
    public override void Unload(bool hotReload)
    {
        _prefs?.Unload();
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
        if(_prefs == null)return;
        if (player == null || !player.IsValid) return;

        // Check if _prefs is null (ClientPrefs not loaded) AND if player has data
        if (!_prefs.TryGetValue(player.Slot, out var data)) return;

        // Read values directly from the data object
        cmd.ReplyToCommand($"ChatMuted={data.ChatMuted}, Volume={data.Volume}, Mode={data.Mode}, FavPack={data.FavPack}");
    }

    // ============================================================================
    // TryGetValue — out variable with player controller
    //
    // Same as above but pass the player controller instead of slot number.
    // Internally does the same null/IsValid/IsBot/IsHLTV check.
    // ============================================================================
    [ConsoleCommand("css_get2", "Get player data using player controller")]
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void TestGet2(CCSPlayerController? player, CommandInfo cmd)
    {
        if(_prefs == null)return;
        
        if (player == null || !player.IsValid) return;
        if (!_prefs.TryGetValue(player, out var data)) return;

        cmd.ReplyToCommand($"XAxis={data.XAxis}, YAxis={data.YAxis}");
    }

    // ============================================================================
    // TryGetValue — modify data using out variable
    //
    // Since 'data' is a live reference, just change the fields directly.
    // ClientPrefs detects the change and saves it automatically.
    // ============================================================================
    [ConsoleCommand("css_toggle", "Toggle ChatMuted")]
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void TestToggle(CCSPlayerController? player, CommandInfo cmd)
    {
        if(_prefs == null)return;
        if (player == null || !player.IsValid) return;
        if (!_prefs.TryGetValue(player.Slot, out var data)) return;


        data.ChatMuted = !data.ChatMuted;
        cmd.ReplyToCommand($"ChatMuted is now: {data.ChatMuted}");
    }

    // ============================================================================
    // TryGetValue — Action/callback style
    //
    // Runs the lambda only if the player is loaded.
    // Returns true if the action ran, false if skipped.
    // Good for one-liner mutations where you don't need an early return.
    // ============================================================================
    [ConsoleCommand("css_action", "Modify data using Action callback")]
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void TestAction(CCSPlayerController? player, CommandInfo cmd)
    {
        if(_prefs == null)return;
        if (player == null || !player.IsValid) return;

        // The lambda runs only if the player has data loaded
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
    //
    // Same callback style but pass the player controller.
    // ============================================================================
    [ConsoleCommand("css_action2", "Modify data using Action with player controller")]
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void TestAction2(CCSPlayerController? player, CommandInfo cmd)
    {
        if(_prefs == null)return;
        if (player == null || !player.IsValid) return;

        _prefs.TryGetValue(player, data =>
        {
            data.XAxis = 100.5f;
            data.YAxis = -50.3f;
        });

        cmd.ReplyToCommand("Set XAxis=100.5, YAxis=-50.3");
    }

    // ============================================================================
    // ForceSave — save one player immediately (THIS plugin only)
    //
    // Normally data saves on disconnect or map end (depending on your save mode).
    // ForceSave writes to cookies.db / MySQL right now without waiting.
    // Only saves THIS plugin's data — other plugins using ClientPrefs are unaffected.
    // Runs on a background thread — does not block the game.
    // ============================================================================
    [ConsoleCommand("css_forcesave", "Force save this player now")]
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void TestForceSave(CCSPlayerController? player, CommandInfo cmd)
    {
        if(_prefs == null)return;
        if (player == null || !player.IsValid) return;

        // Save this player's data for THIS plugin only
        _prefs.ForceSave(player);

        // Can also use slot number:
        // _prefs.ForceSave(player.Slot);

        cmd.ReplyToCommand("Force saved your data for this plugin.");
    }

    // ============================================================================
    // ForceSavePlayer_To_All_Instances — save one player across ALL plugins
    //
    // Same as ForceSave but saves this player's data in EVERY plugin that uses
    // ClientPrefs. If Plugin A, B, and C all use ClientPrefs, calling this from
    // Plugin A will save the player's data in A, B, and C.
    //
    // Use case: admin command that forces a full save for a specific player.
    // ============================================================================
    [ConsoleCommand("css_forcesave_all", "Force save this player across all plugins")]
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void TestForceSaveAll(CCSPlayerController? player, CommandInfo cmd)
    {
        if(_prefs == null)return;
        if (player == null || !player.IsValid) return;

        // Save this player's data across ALL plugins using ClientPrefs
        _prefs.ForceSavePlayer_To_All_Instances(player);

        // Can also use slot number:
        // _prefs.ForceSavePlayer_To_All_Instances(player.Slot);

        cmd.ReplyToCommand("Force saved your data across ALL plugins.");
    }

    // ============================================================================
    // DropPlayer — wipe one player from THIS plugin
    //
    // Completely removes the player:
    //   1. Removes from memory (current session)
    //   2. Deletes from cookies.db
    //   3. Deletes from MySQL
    //
    // The player's data is GONE. Next time they join, they start fresh with
    // default values. Only affects THIS plugin — other plugins keep their data.
    //
    // Use case: admin wants to reset a specific player's settings.
    // ============================================================================
    [ConsoleCommand("css_drop", "Wipe this player's data for this plugin")]
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void TestDrop(CCSPlayerController? player, CommandInfo cmd)
    {
        if(_prefs == null)return;
        if (player == null || !player.IsValid) return;

        // Wipe this player from THIS plugin's storage
        _prefs.DropPlayer(player);

        // Can also use slot number:
        // _prefs.DropPlayer(player.Slot);

        cmd.ReplyToCommand("Your data has been wiped for this plugin. Rejoin to get defaults.");
    }

    // ============================================================================
    // DropPlayer_To_All_Instances — wipe one player from ALL plugins
    //
    // Nuclear option. Removes the player's data from EVERY plugin that uses
    // ClientPrefs. Memory + cookies.db + MySQL — all gone, everywhere.
    //
    // Use case: GDPR deletion request, or banning a cheater and wiping their data.
    // ============================================================================
    [ConsoleCommand("css_drop_all", "Wipe this player's data from ALL plugins")]
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void TestDropAll(CCSPlayerController? player, CommandInfo cmd)
    {
        if(_prefs == null)return;
        if (player == null || !player.IsValid) return;

        // Wipe this player from ALL plugins using ClientPrefs
        _prefs.DropPlayer_To_All_Instances(player);

        // Can also use slot number:
        // _prefs.DropPlayer_To_All_Instances(player.Slot);

        cmd.ReplyToCommand("Your data has been wiped from ALL plugins.");
    }

    // ============================================================================
    // Refresh — save all + reload all players (THIS plugin only)
    //
    // What it does:
    //   1. Saves all changed player data to cookies.db / MySQL
    //   2. Clears all player data from memory
    //   3. Reloads every connected player from storage
    //
    // Use case: called on hot reload so players don't lose their data.
    // Already shown in OnAllPluginsLoaded above, but here's a command version.
    // ============================================================================
    [ConsoleCommand("css_refresh", "Refresh - save and reload all players")]
    [CommandHelper(whoCanExecute: CommandUsage.SERVER_ONLY)]
    public void TestRefresh(CCSPlayerController? player, CommandInfo cmd)
    {
        if(_prefs == null)return;

        _prefs.Refresh();
        cmd.ReplyToCommand("Refreshed — all players saved and reloaded from storage.");
    }

    // ============================================================================
    // Unload — save all + clear memory (THIS plugin only)
    //
    // What it does:
    //   1. Saves all changed player data to cookies.db / MySQL
    //   2. Clears all player data from memory
    //   3. Does NOT reload (unlike Refresh)
    //
    // Use case: called in your plugin's Unload() method.
    // Already shown above, but here's what it looks like standalone.
    // ============================================================================
    [ConsoleCommand("css_unload", "Unload - save all and clear memory")]
    [CommandHelper(whoCanExecute: CommandUsage.SERVER_ONLY)]
    public void TestUnload(CCSPlayerController? player, CommandInfo cmd)
    {
        if(_prefs == null)return;

        _prefs.Unload();
        cmd.ReplyToCommand("Unloaded — all players saved, memory cleared.");
    }

    // ============================================================================
    // PRACTICAL EXAMPLE: Using prefs in game logic
    //
    // Shows how you'd typically use TryGetValue in real plugin code —
    // checking a player's setting before performing an action.
    // ============================================================================
    [ConsoleCommand("css_chat", "Send a message if not muted")]
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void TestChat(CCSPlayerController? player, CommandInfo cmd)
    {
        if(_prefs == null)return;
        if (player == null || !player.IsValid) return;
        if (!_prefs.TryGetValue(player.Slot, out var data)) return;

        // Check the player's preference before doing something
        if (data.ChatMuted)
        {
            cmd.ReplyToCommand("You have chat muted. Use !test_toggle to unmute.");
            return;
        }

        cmd.ReplyToCommand($"Chat is enabled! Your volume is {data.Volume}.");
    }

    // ============================================================================
    // PRACTICAL EXAMPLE: Setting multiple values at once
    //
    // Shows the Action/callback style for setting multiple fields in one call.
    // ============================================================================
    [ConsoleCommand("css_reset", "Reset all values to default")]
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void TestReset(CCSPlayerController? player, CommandInfo cmd)
    {
        if(_prefs == null)return;
        if (player == null || !player.IsValid) return;
        _prefs.TryGetValue(player.Slot, data =>
        {
            // Reset everything to defaults
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
        if(_prefs == null)return;
        if (player == null || !player.IsValid) return;
        if (!_prefs.TryGetValue(player.Slot, out var data)) return;

        if (!int.TryParse(cmd.GetArg(1), out var vol))
        {
            cmd.ReplyToCommand("Usage: !test_vol <0-100>");
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
        if(_prefs == null)return;
        if (player == null || !player.IsValid) return;
        if (!_prefs.TryGetValue(player.Slot, out var data)) return;

        data.FavPack = cmd.GetArg(1);
        cmd.ReplyToCommand($"Favorite pack set to: {data.FavPack}");
    }

    // ============================================================================
    // PRACTICAL EXAMPLE: Reading another player's data
    //
    // You can read any connected player's data by slot number.
    // Useful for admin commands that inspect other players.
    // ============================================================================
    [ConsoleCommand("css_inspect", "Inspect a player by slot number")]
    [CommandHelper(minArgs: 1, usage: "<slot>", whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void TestInspect(CCSPlayerController? player, CommandInfo cmd)
    {
        if(_prefs == null)return;
        if (!int.TryParse(cmd.GetArg(1), out var slot))
        {
            cmd.ReplyToCommand("Usage: !test_inspect <slot_number>");
            return;
        }

        // Read another player's data by slot
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
// !test_get           — read your data (out variable, slot)
// !test_get2          — read your data (out variable, player controller)
// !test_toggle        — toggle ChatMuted true/false
// !test_action        — modify data using Action callback (slot)
// !test_action2       — modify data using Action callback (player controller)
// !test_forcesave     — force save your data (this plugin only)
// !test_forcesave_all — force save your data (all plugins)
// !test_drop          — wipe your data (this plugin only)
// !test_drop_all      — wipe your data (all plugins)
// !test_refresh       — save + reload all players (server only)
// !test_unload        — save + clear memory (server only)
// !test_chat          — practical: check ChatMuted before action
// !test_reset         — practical: reset all fields to default
// !test_vol <0-100>   — practical: set volume from command arg
// !test_pack <name>   — practical: set string value
// !test_inspect <slot> — practical: read another player's data
// ============================================================================