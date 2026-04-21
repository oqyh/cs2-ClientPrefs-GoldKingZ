using CounterStrikeSharp.API.Core;

namespace ClientPrefs_GoldKingZ.Shared;

public interface IPrefsStore<T> where T : class, new()
{
    bool TryGetValue(CCSPlayerController player, out T data);
    bool TryGetValue(int slot, out T data);

    bool TryGetValue(CCSPlayerController player, Action<T> action);
    bool TryGetValue(int slot, Action<T> action);

    void ForceSave(CCSPlayerController player);
    void ForceSave(int slot);

    void ForceSavePlayer_To_All_Instances(CCSPlayerController player);
    void ForceSavePlayer_To_All_Instances(int slot);

    void DropPlayer(CCSPlayerController player);
    void DropPlayer(int slot);

    void DropPlayer_To_All_Instances(CCSPlayerController player);
    void DropPlayer_To_All_Instances(int slot);

    void Refresh();
    void Unload();
}