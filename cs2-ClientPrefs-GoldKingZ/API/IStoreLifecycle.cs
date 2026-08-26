using CounterStrikeSharp.API.Core;

namespace ClientPrefs_GoldKingZ;

internal interface IStoreLifecycle
{
    string PluginName { get; }
    string StoreKey   { get; }
    string TableName  { get; }

    Task LoadPlayerAsync(CCSPlayerController? player);
    Task OnPlayerDisconnectAsync(int slot);
    Task OnMapEndAsync();

    void ForceSaveAndClear();
    void Refresh();
    void ForceSaveSlot(int slot, Action<bool>? done = null);
    void DropSlot(int slot, Action<bool>? done = null);
    void DropSteam(ulong steamId, Action<bool>? done = null);
    void Unload();

    bool IsPlayerReady(int slot, ulong steamId);

    Task<(string name, DateTime date, Dictionary<string, object?> values)?> GetRawAsync(ulong steamId);
    Task<List<(ulong steamId, string name, DateTime date, Dictionary<string, object?> values)>> SearchRawAsync(string column, object value);
}