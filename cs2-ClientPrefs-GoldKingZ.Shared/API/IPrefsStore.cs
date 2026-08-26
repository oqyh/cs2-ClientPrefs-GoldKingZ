using CounterStrikeSharp.API.Core;

namespace ClientPrefs_GoldKingZ.Shared;

// ##########################################################
//  Search result — one row per matched player
// ##########################################################
public sealed class PrefsSearchResult<TResult> where TResult : class, new()
{
    public ulong    PlayerSteamID { get; init; }
    public string   PlayerName    { get; init; } = "";
    public DateTime DateAndTime   { get; init; }
    public string   Plugin        { get; init; } = "";
    public string   Table         { get; init; } = "";
    public TResult  Data          { get; init; } = null!;
}

public interface IPrefsStore<T> where T : class, new()
{
    // ##########################################################
    //  Read / Write — in-memory (instant)
    //  Returned data is a LIVE reference — changes are tracked
    //  and saved automatically on disconnect / map end.
    // ##########################################################

    /// <summary>
    /// To all TryGetValue if not loaded yet and player rush to use it 
    /// ClientPrefs will send message to player to wait "PrintToChatToPlayer.ClientPrefs.Loading"
    /// Then send "PrintToChatToPlayer.ClientPrefs.Loaded" once it ready 
    /// Note: these messages only send who rush TryGetValue without waiting his ClientPrefs to be load
    /// </summary>

    /// <summary>Get data by player controller. Returns false if not loaded yet.</summary>
    bool TryGetValue(CCSPlayerController player, out T data);

    /// <summary>Get data by slot number. Returns false if not loaded yet.</summary>
    bool TryGetValue(int slot, out T data);

    /// <summary>Run action if player is loaded (callback style).</summary>
    bool TryGetValue(CCSPlayerController player, Action<T> action);

    /// <summary>Run action if slot is loaded (callback style).</summary>
    bool TryGetValue(int slot, Action<T> action);

    /// <summary>Get data by SteamID64 — works for players currently in memory. Returns false if not found or not loaded yet.</summary>
    bool TryGetValue(ulong steamId, out T data);

    /// <summary>Run action if a player with this SteamID64 is in memory and loaded (callback style).</summary>
    bool TryGetValue(ulong steamId, Action<T> action);

    /// <summary>
    /// Run a callback ONCE for THIS player as soon as their data is ready (or immediately if already loaded).
    /// Call it wherever you want (OnPlayerConnectFull, OnClientPutInServer, ...) — it fires per call, for that
    /// All_Plugins = false: waits until THIS store has the player ready.
    /// All_Plugins = true: waits until EVERY store in EVERY plugin has the player ready.
    /// </summary>
    void OnPlayerLoaded(CCSPlayerController player, Action<CCSPlayerController, T> callback, bool All_Plugins = false);

    // ##########################################################
    //  DataBase — read / search / write (works for OFFLINE players)
    //  All callbacks run on the game thread.
    //  Sources depend on this store's config
    //  (PrefsAPI_CookiesEnable / PrefsAPI_MySqlEnable) — newest data wins.
    // ##########################################################

    /// <summary>
    /// Get ONE player's data by SteamID64 from storage — works for OFFLINE players.
    /// data = null if the player was never saved.
    /// If the player is online and loaded, live values are returned instead (edits persist).
    /// Offline data is a read-only snapshot — edits to it are NOT saved (use ModifyAndSave to write).
    /// </summary>
    void FetchPlayer(ulong steamId, Action<T?> callback);

    /// <summary>
    /// Get ONE player's data using YOUR OWN result class TResult — declare in TResult only the fields you want back.
    /// All_Plugins = false: reads THIS store only (list has 0 or 1 entry).
    /// All_Plugins = true: one entry per plugin that has saved data for this player.
    /// Fields in TResult are filled by matching column names; missing columns stay at TResult's defaults.
    /// Results are READ-ONLY snapshots; result.Plugin tells you which plugin each entry came from.
    /// </summary>
    void FetchPlayer<TResult>(ulong steamId, Action<List<PrefsSearchResult<TResult>>> callback, bool All_Plugins = false) where TResult : class, new();

    /// <summary>
    /// Search ALL players by a field value — e.g. SearchByField("LastIp", "1.2.3.4", results => ...).
    /// fieldName = a property of your data class, or "PlayerName".
    /// Searches storage + online players' live values. Empty list if none match.
    /// Results are read-only snapshots — use ModifyAndSave (offline) or TryGetValue (online) to edit.
    /// </summary>
    void SearchByField(string fieldName, object value, Action<List<PrefsSearchResult<T>>> callback);

    /// <summary>
    /// Search by a field value using YOUR OWN result class TResult — declare in TResult only the fields you want back.
    /// All_Plugins = false: searches THIS store only.
    /// All_Plugins = true: searches EVERY store in EVERY plugin whose table has that field (matched by name).
    /// Fields in TResult are filled by matching column names; missing columns stay at TResult's defaults.
    /// Results are READ-ONLY snapshots; result.Plugin tells you which plugin each row came from.
    /// </summary>
    void SearchByField<TResult>(string fieldName, object value, Action<List<PrefsSearchResult<TResult>>> callback, bool All_Plugins = false) where TResult : class, new();

    /// <summary>
    /// Modify ONE player's stored data by SteamID64 and save it back — works for OFFLINE players.
    /// If the player is ONLINE, the modify runs on their live in-memory data instead (saved normally).
    /// If the player was never saved, starts from your class defaults.
    /// modify = your changes; done = optional callback (true = saved OK on all enabled backends).
    /// </summary>
    void ModifyAndSave(ulong steamId, Action<T> modify, Action<bool>? done = null);

    // ##########################################################
    //  Force Save  (done: true = saved OK; All_Plugins fires done once after all)
    // ##########################################################

    /// <summary>Save this player now. All_Plugins = true saves across ALL plugins using ClientPrefs.</summary>
    void ForceSave(CCSPlayerController player, Action<bool>? done = null, bool All_Plugins = false);

    /// <summary>Save this player now by slot. All_Plugins = true saves across ALL plugins using ClientPrefs.</summary>
    void ForceSave(int slot, Action<bool>? done = null, bool All_Plugins = false);

    /// <summary>Save this player now by SteamID64 (must be in memory). All_Plugins = true saves across ALL plugins using ClientPrefs.</summary>
    void ForceSave(ulong steamId, Action<bool>? done = null, bool All_Plugins = false);

    // ##########################################################
    //  Drop Player (wipe memory + cookies.db + MySQL — whichever enabled)
    //  (done: true = wiped; All_Plugins fires done once after all)
    // ##########################################################

    /// <summary>Wipe this player from memory + storage. All_Plugins = true wipes from ALL plugins using ClientPrefs.</summary>
    void DropPlayer(CCSPlayerController player, Action<bool>? done = null, bool All_Plugins = false);

    /// <summary>Wipe this player from memory + storage by slot. All_Plugins = true wipes from ALL plugins using ClientPrefs.</summary>
    void DropPlayer(int slot, Action<bool>? done = null, bool All_Plugins = false);

    /// <summary>Wipe this player by SteamID64 — works for ONLINE and OFFLINE players (offline = deleted from storage). All_Plugins = true wipes from ALL plugins using ClientPrefs.</summary>
    void DropPlayer(ulong steamId, Action<bool>? done = null, bool All_Plugins = false);

    // ##########################################################
    //  Lifecycle
    // ##########################################################

    /// <summary>Save all changed data + reload all connected players from storage (this plugin's store only). Use on hot reload.</summary>
    void Refresh();

    /// <summary>Save all changed data + close connections + clear memory (this plugin's store only). Call in your plugin's Unload.</summary>
    void Unload();
}