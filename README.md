---
<h2 align="center">.:[ Community | Support ]:.</h2>
<p align="center">
  <a href="https://discord.com/invite/U7AuQhu">
    <img src="https://img.shields.io/badge/Discord-Join-5865F2?style=for-the-badge&logo=discord&logoColor=white" />
  </a>
  <a href="https://ko-fi.com/goldkingz">
    <img src="https://img.shields.io/badge/Ko--fi-Support-FF5E5B?style=for-the-badge&logo=kofi&logoColor=white" />
  </a>
</p>

---

# [CS2-API] ClientPrefs-GoldKingZ (1.0.4)

Shared player Preferences API Per-Plugin Isolation With [Cookies(SQLite) + MySQL]

<img width="1100" height="2690" alt="Clientprefs architecture 1 0 4" src="https://github.com/user-attachments/assets/80b7c17e-65c4-42d6-aa6c-17f03f112a6f" />


---

## 📦 Dependencies

[![Metamod:Source](https://img.shields.io/badge/Metamod:Source-REQUIRED_TO_DOWNLOAD-red?logo=sourceengine&labelColor=2d2d2d)](https://www.sourcemm.net)

[![CounterStrikeSharp](https://img.shields.io/badge/CounterStrikeSharp-REQUIRED_TO_DOWNLOAD-red?logo=github&labelColor=83358F)](https://github.com/roflmuffin/CounterStrikeSharp)

[![MySQL](https://img.shields.io/badge/MySQL-INCLUDED_IN_ZIP-brightgreen?logo=mysql&logoColor=white&labelColor=07405E)](https://dev.mysql.com/doc/connector-net/en/)

[![SQLite](https://img.shields.io/badge/SQLite-INCLUDED_IN_ZIP-brightgreen?logo=sqlite&logoColor=white&labelColor=07405E)](https://www.sqlite.org/)


---

## 📥 Installation

### Plugin Installation

1. Download the latest `ClientPrefs-GoldKingZ.x.x.x.zip` release
2. Extract contents to your `csgo` directory
3. Restart your server

---

## 💻 For Developers
 
**Option A — NuGet (recommended):**
 
```bash
dotnet add package ClientPrefs-GoldKingZ.Shared
```
 
Or add to your `.csproj`:
 
```xml
<PackageReference Include="ClientPrefs-GoldKingZ.Shared" Version="1.0.4" />
```
 
**Option B — Reference the DLL directly:**
 
1. Download the latest `ClientPrefs-GoldKingZ.x.x.x.zip` release
2. Open the `.zip`, find the `shared` folder, take `ClientPrefs-GoldKingZ.Shared.dll`
3. Drop it into your project's root folder
4. Reference it in your `.csproj`:
```xml
<Reference Include="ClientPrefs-GoldKingZ.Shared">
    <HintPath>ClientPrefs-GoldKingZ.Shared.dll</HintPath>
    <Private>false</Private>
</Reference>
```
 
> Either way, the **core plugin** (`ClientPrefs-GoldKingZ.dll`) must be installed on the server — the NuGet package / DLL is only the API surface your plugin compiles against.
 
---
 
## 🚀 Quick Start
 
### 1. Define your data class
 
```csharp
public sealed class ClientPrefs
{
    public bool   ChatMuted  { get; set; } = false;
    public int    Volume     { get; set; } = 50;
    public float  XAxis      { get; set; } = 0f;
    public string FavPack    { get; set; } = "";
    public int    Mode       { get; set; } = 0;
}
```
> **Supported types:** `bool`, `int`, `long`, `ulong`, `float`, `double`, `string`, `DateTime`
>
> **Reserved names (do NOT use):** `PlayerName`, `PlayerSteamID`, `DateAndTime` — injected automatically by ClientPrefs
 
### 2. Register in your plugin
 
```csharp
using ClientPrefs_GoldKingZ.Shared;
 
private IPrefsStore<ClientPrefs>? _prefs;
 
public override void OnAllPluginsLoaded(bool hotReload)
{
    var api = ClientPrefsApi.Get();
    if (api == null)
    {
        Logger.LogError("[MyPlugin] Missing cs2-ClientPrefs-GoldKingZ API !");
        return;
    }
 
    _prefs = api.CreatePrefs<ClientPrefs>(this, new ClientPrefsOptions
    {
        PrefsAPI_CookiesEnable = PrefsAPI_SaveMode.OnPlayerDisconnect,
    });
 
    if (hotReload)
    {
        _prefs?.Refresh();
    }
 
}
 
public override void Unload(bool hotReload)
{
    _prefs?.Unload();
}
```
 
### 3. Use it
 
```csharp
if (_prefs?.TryGetValue(player.Slot, out var data) != true) return;
 
data.ChatMuted = !data.ChatMuted;
data.Volume = 75;
```
 
That's it. Changes are tracked automatically and saved on disconnect or map end.
 
> **Note:** `TryGetValue` returns `false` while a player's data is still loading. The player automatically gets a chat message to wait, and another when everything is loaded. Just handle the `false` return and let the player retry.
 
---
 
## 🧩 Multiple Isolated Stores
 
One plugin can register more than one store — call `CreatePrefs<T>()` once per data class. Each store gets its own table and never shares rows or columns with the others.
 
```csharp
private IPrefsStore<ClientPrefs>?    _prefs;
private IPrefsStore<ClientPrefsHud>? _hud;
 
public override void OnAllPluginsLoaded(bool hotReload)
{
    var api = ClientPrefsApi.Get();
    if (api == null) return;
 
    _prefs = api.CreatePrefs<ClientPrefs>(this, new ClientPrefsOptions { /* ... */ });
 
    _hud = api.CreatePrefs<ClientPrefsHud>(this, new ClientPrefsOptions
    {
        PrefsAPI_TableName = "test_hud",   // exact table name on SQLite AND MySQL
    });
}
 
public override void Unload(bool hotReload)
{
    _prefs?.Unload();
    _hud?.Unload();
}
```
 
> Without `PrefsAPI_TableName`, each store auto-names its table `<FolderName>_<ClassName>` — so multiple stores never collide.
 
---
 
## ⚙️ Configuration Options
 
| Option                                                   | Values                                                           | Default                              |
| -------------------------------------------------------- | ---------------------------------------------------------------- | ------------------------------------ |
| `PrefsAPI_CookiesEnable`                                 | `Disabled` / `OnPlayerDisconnect` / `OnMapEnd`                   | `Disabled`                           |
| `PrefsAPI_CookiesAutoRemoveInactivePlayersOlderThanDays` | `0` = never delete, `1+` = days                                  | `7`                                  |
| `PrefsAPI_MySqlEnable`                                   | `Disabled` / `OnPlayerDisconnect` / `OnMapEnd`                   | `Disabled`                           |
| `PrefsAPI_MySqlAutoRemoveInactivePlayersOlderThanDays`   | `0` = never delete, `1+` = days                                  | `7`                                  |
| `PrefsAPI_MySqlConnectionTimeout`                        | seconds                                                          | `30`                                 |
| `PrefsAPI_MySqlRetryAttempts`                            | any positive integer                                             | `3`                                  |
| `PrefsAPI_MySqlRetryDelay`                               | seconds between retries                                          | `2`                                  |
| `PrefsAPI_TableName`                                     | string or `null` for auto. Applies to **both** SQLite and MySQL  | `null` → `<FolderName>_<ClassName>`  |
| `PrefsAPI_MySqlConfig`                                   | `MySqlConfig { Server, Port, Database, Username, Password }` or `MySql_Servers` list for failover | empty |
| `PrefsAPI_ReloadOnReconnect`                             | `true` = reload from storage / `false` = keep memory             | `false`                              |
| `PrefsAPI_LoadDefaultAfterDrop`                          | `true` = give defaults after drop / `false` = empty until rejoin | `true`                               |
| `PrefsAPI_DebugEnable`                                   | `true` = show all logs / `false` = errors only                   | `false`                              |
 
---
 
## 📖 API Methods
 
### Read / Write Data (in-memory, instant)
 
| Method                            | Description                                                    |
| --------------------------------- | ------------------------------------------------------------- |
| `TryGetValue(player, out data)`   | Get player data by controller. Returns `false` if not loaded  |
| `TryGetValue(slot, out data)`     | Get player data by slot number                                |
| `TryGetValue(steamId, out data)`  | Get player data by SteamID64 (must be in memory)              |
| `TryGetValue(player, action)`     | Run action if player is loaded (callback style)               |
| `TryGetValue(slot, action)`       | Run action if slot is loaded (callback style)                 |
| `TryGetValue(steamId, action)`    | Run action if SteamID64 is in memory and loaded               |
 
### Player Loaded Event
 
| Method                                                | Description                                                       |
| ----------------------------------------------------- | ---------------------------------------------------------------- |
| `OnPlayerLoaded(player, callback)`                    | Run callback ONCE when THIS store finishes loading the player    |
| `OnPlayerLoaded(player, callback, All_Plugins: true)` | Run callback ONCE when EVERY plugin finished loading the player  |
 
> Call it anywhere (a connect event, etc). It fires once per call for the player you pass — it is **not** a registration. Call it twice = fires twice, you control it. Runs on the game thread.
 
### DataBase (works for OFFLINE players)
 
| Method                                                              | Description                                                            |
| ------------------------------------------------------------------- | --------------------------------------------------------------------- |
| `FetchPlayer(steamId, callback)`                                    | Get one player's stored data (offline OK)                             |
| `FetchPlayer<TResult>(steamId, callback, All_Plugins: true)`        | Get one player from EVERY plugin into your own result class           |
| `SearchByField(field, value, callback)`                             | Find all players where a field equals a value                         |
| `SearchByField<TResult>(field, value, callback, All_Plugins: true)` | Search EVERY plugin into your own result class                        |
| `ModifyAndSave(steamId, modify, done)`                              | Modify one player's stored data and save it (offline OK, this plugin) |
 
> Reads cookies + MySQL per this store's config — newest data wins. In `<TResult>` results, `result.Plugin` and `result.Table` tell you which plugin / table each row came from.
 
### Force Save
 
| Method                                        | Description                                            |
| --------------------------------------------- | ----------------------------------------------------- |
| `ForceSave(player)` / `(slot)` / `(steamId)`  | Save this player now — this plugin only               |
| `ForceSave(..., All_Plugins: true)`           | Save this player across ALL plugins using ClientPrefs |
| `ForceSave(..., done: saved => { })`          | Optional callback — `true` = saved OK                 |
 
### Drop Player (wipe memory + cookies.db + MySQL)
 
| Method                                        | Description                                            |
| --------------------------------------------- | ------------------------------------------------------ |
| `DropPlayer(player)` / `(slot)` / `(steamId)` | Wipe this player — this plugin only                    |
| `DropPlayer(..., All_Plugins: true)`          | Wipe this player from ALL plugins using ClientPrefs    |
| `DropPlayer(..., done: dropped => { })`       | Optional callback — `true` = wiped                     |
 
> `DropPlayer` by `steamId` works even if the player is **offline** (deletes from storage). Wipes only the backends this store has enabled.
 
### Lifecycle
 
| Method      | Description                                              |
| ----------- | -------------------------------------------------------- |
| `Refresh()` | Save all changed data + reload all players from storage  |
| `Unload()`  | Save all changed data + close connections + clear memory |
 
---
 
## 🔄 Auto-Migration
 
You can freely edit your data class at any time. ClientPrefs automatically updates your cookies.db and MySQL table to match — no need to delete the database.
 
| Change            | What happens                                                                |
| ----------------- | -------------------------------------------------------------------------- |
| Add a new field   | New column added, existing players keep their data                         |
| Remove a field    | Column dropped, other columns stay untouched                               |
| Change field type | Column type updated (table rebuilt for cookies, `MODIFY COLUMN` for MySQL) |
 
> **No data loss when adding or removing fields.** Changing a field's **type** or **renaming** it (drop old + add new) resets that one column to its default — other columns are unaffected. Just edit your data class, rebuild your plugin, and reload.
 
---
 
## 📂 Folder Structure
 
After installation:
 
```
csgo/
└── addons/counterstrikesharp/
    ├── plugins/
    │   └── ClientPrefs-GoldKingZ/                       ← Core plugin
    │       ├── ClientPrefs-GoldKingZ.dll
    │       ├── ClientPrefs-GoldKingZ.Shared.dll
    │       ├── Microsoft.Data.Sqlite.dll
    │       ├── MySqlConnector.dll
    │       ├── SQLitePCLRaw.batteries_v2.dll
    │       ├── SQLitePCLRaw.core.dll
    │       ├── SQLitePCLRaw.provider.e_sqlite3.dll
    │       ├── e_sqlite3.dll                            ← Native SQLite (Windows)
    │       ├── libe_sqlite3.so                          ← Native SQLite (Linux)
    │       └── lang/
    │           └── en.json                              ← Chat messages
    └── shared/
        └── ClientPrefs-GoldKingZ.Shared/
            └── ClientPrefs-GoldKingZ.Shared.dll         ← API reference for developers
```
 
Each consumer plugin gets its own isolated storage inside the core folder:
 
```
plugins/
└── ClientPrefs-GoldKingZ/
    ├── YourPlugin/
    │   └── cookies.db                                   ← Created automatically
    └── AnotherPlugin/
        └── cookies.db
```
 
---
 
## 🗄️ Browsing & Editing the Database
 
If you ever need to inspect or manually edit values in `cookies.db`, the easiest tool is **DB Browser for SQLite**
 
🔗 **Download:** <https://github.com/sqlitebrowser/sqlitebrowser/releases>
 
### 📂 Locate the database file
 
Each consumer plugin has its own isolated database inside the core folder:
 
```
csgo/addons/counterstrikesharp/plugins/ClientPrefs-GoldKingZ/<YourPlugin>/cookies.db
```
 
### 🔍 How to browse And Edit
 
1. Open **DB Browser For SQLite**
2. Click **`Open Database`** And Select The `cookies.db` File
3. Switch To The **`Browse Data`** Tab
4. Edit What Ever You Like Then Apply
5. Select **`File`** Tab And Then Select `Close Database`
6. `Save`
---
 
## 🧪 Example Plugin
 
See [cs2-ClientPrefsTest-GoldKingZ](https://github.com/oqyh/cs2-ClientPrefs-GoldKingZ/tree/main/cs2-ClientPrefsTest-GoldKingZ) for a full working example covering every API method.
 
---

## 📜 Changelog

<details>
<summary>📋 View Version History (Click to expand 🔽)</summary>

### [1.0.4]
- Added `TryGetValue` by SteamID64 (out + callback style)
- Added `OnPlayerLoaded(player, callback, All_Plugins)` — fires once when a player's data is ready; call it anywhere (connect event, etc), fires per call, you control when
- Added `FetchPlayer` / `SearchByField` — read or search stored players, works for OFFLINE players
- Added `<TResult>` overloads on FetchPlayer/SearchByField — read across ALL plugins into your own result class (`result.Plugin` + `result.Table` tell you where each row came from)
- Added `ModifyAndSave` — modify one player's stored data and save it, works OFFLINE
- Added `All_Plugins` option to ForceSave/DropPlayer — act across every plugin using ClientPrefs
- Added `steamId` overloads to ForceSave/DropPlayer (DropPlayer by steamId works OFFLINE too)
- Added optional `done` callback to ForceSave/DropPlayer (`true` = success)
- **Breaking:** removed `ForceSavePlayer_To_All_Instances` / `DropPlayer_To_All_Instances` — use `ForceSave(x, All_Plugins: true)` / `DropPlayer(x, All_Plugins: true)` instead
- Fixed hot-reload freeze — reloading a plugin using ClientPrefs no longer stalls the server (SQLite close/checkpoint and cache load moved off the game thread)
- Fixed memory leak — player data is now freed on disconnect instead of lingering until map change (was growing on servers that rarely change map)
- Fixed disconnected players with unsaved data being retried forever — now one save attempt after they leave, then dropped
- Fixed a rare data-loss race where a reconnecting player's edit could be wiped by an in-flight map-end save
- Fixed MySQL auto-migration racing itself across multiple stores (duplicate ALTER / "Can't DROP COLUMN" error)
- Cleaner Unload logging — one line per store

### [1.0.3]
- Fixed A Build Configuration
- Downgraded SQLite To Fix Linux Server Crash (`GLIBC_2.33` Error) On Older OS.

### [1.0.2]
- Upgraded to .NET 10
- Fix values reset to default when MySQL is down or slow (kept in memory until save confirmed, retried next save)
- Fix MySQL startup race where an early-connecting player could overwrite data with defaults
- Fix duplicate saves on map end + crash silently killing map-end saves
- Fix float values different between SQLite and MySQL (both now store the exact same value)
- Fix SQLite connection leak on consumer hot-reload
- Added multiple isolated stores per plugin (call `CreatePrefs<T>()` once per class)
- Added shared MySQL connection manager — plugins on the same server share health + one retry cycle
- Added loading gate: `TryGetValue` returns `false` until player data is fully loaded + chat messages to player (lang support)
- Renamed `PrefsAPI_MySqlTableName` → `PrefsAPI_TableName` (now applies to BOTH cookies/SQLite and MySQL)
- SQLite table name now matches MySQL (was hardcoded `PlayerCookies`)
- Moved `cookies.db` into the core folder per consumer: `plugins/ClientPrefs-GoldKingZ/<YourPlugin>/cookies.db`
- Isolated tables sharing one `cookies.db` now share a single connection (refcounted)
- `MySql_Servers` now saves to ALL configured servers, loads use the newest row (was: first reachable only)

### [1.0.1]
- Fix Freeze Players On Unload Core
- Fix SaveAsync Error: Cannot access a disposed object
- Added Debug Message On SaveAsync On Sql And MySql

### [1.0.0]
- Initial plugin release

</details>
