using ClientPrefs_GoldKingZ.Shared;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Capabilities;

namespace ClientPrefs_GoldKingZ;

public sealed class MainPlugin : BasePlugin
{
    public override string ModuleName => "Shared player Preferences API Per-Plugin Isolation With [Cookies(SQLite) + MySQL]"; 
    public override string ModuleVersion => "1.0.0";
    public override string ModuleAuthor => "Gold KingZ";
    public override string ModuleDescription => "https://github.com/oqyh";

    public static MainPlugin Instance { get; private set; } = null!;

    private readonly ClientPrefsApiImpl _api = new();

    public override void Load(bool hotReload)
    {
        Instance = this;
        Capabilities.RegisterPluginCapability(ClientPrefsApi.Capability, () => _api);

        RegisterEventHandler<EventPlayerConnectFull>(OnPlayerConnectFull);
        RegisterEventHandler<EventPlayerDisconnect>(OnPlayerDisconnect);
        RegisterListener<Listeners.OnMapEnd>(OnMapEnd);

        if (hotReload)
        {
            _api.RefreshAllStores();
        }
    }

    public override void Unload(bool hotReload)
    {
        _api.ForceSaveAllStores();
        _api.DisposeAllBackends();
    }

    private HookResult OnPlayerConnectFull(EventPlayerConnectFull @event, GameEventInfo info)
    {
        var player = @event?.Userid;
        if (player == null || !player.IsValid || player.IsBot || player.IsHLTV) return HookResult.Continue;

        foreach (var store in _api.All)
            _ = store.LoadPlayerAsync(player);

        return HookResult.Continue;
    }

    private HookResult OnPlayerDisconnect(EventPlayerDisconnect @event, GameEventInfo info)
    {
        var player = @event?.Userid;
        if (player == null || !player.IsValid || player.IsBot) return HookResult.Continue;

        int slot = player.Slot;
        foreach (var store in _api.All)
            _ = store.OnPlayerDisconnectAsync(slot);

        return HookResult.Continue;
    }

    private void OnMapEnd()
    {
        foreach (var store in _api.All)
            _ = store.OnMapEndAsync();
    }

    internal static void DebugCore(string message)
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"[ClientPrefs]: {message}");
        Console.ResetColor();
    }
}