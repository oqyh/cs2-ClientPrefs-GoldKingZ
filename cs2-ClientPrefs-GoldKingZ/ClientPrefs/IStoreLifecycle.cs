using CounterStrikeSharp.API.Core;

namespace ClientPrefs_GoldKingZ;

internal interface IStoreLifecycle
{
    string PluginName { get; }
    Task LoadPlayerAsync(CCSPlayerController? player);
    Task OnPlayerDisconnectAsync(int slot);
    Task OnMapEndAsync();
    void ForceSaveAndClear();
    void Refresh();
    void ForceSaveSlot(int slot);
    void DropSlot(int slot);
    void Unload();
}