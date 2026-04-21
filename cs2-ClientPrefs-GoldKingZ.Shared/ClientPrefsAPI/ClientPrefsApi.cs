using CounterStrikeSharp.API.Core.Capabilities;

namespace ClientPrefs_GoldKingZ.Shared;

public static class ClientPrefsApi
{
    public const string CapabilityName = "goldkingz:clientprefs";

    public static readonly PluginCapability<IClientPrefsApi> Capability = new(CapabilityName);

    public static IClientPrefsApi? Get() => Capability.Get();
}
