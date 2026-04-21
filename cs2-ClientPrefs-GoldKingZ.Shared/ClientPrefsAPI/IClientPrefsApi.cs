using CounterStrikeSharp.API.Core;

namespace ClientPrefs_GoldKingZ.Shared;

public interface IClientPrefsApi
{
    IPrefsStore<T> CreatePrefs<T>(BasePlugin plugin, ClientPrefsOptions options) where T : class, new();
}
