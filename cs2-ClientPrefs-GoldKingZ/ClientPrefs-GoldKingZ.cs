using ClientPrefs_GoldKingZ.Shared;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Capabilities;

namespace ClientPrefs_GoldKingZ;

public sealed class MainPlugin : BasePlugin
{
    public override string ModuleName => "Shared player Preferences API Per-Plugin Isolation With [Cookies(SQLite) + MySQL]";
    public override string ModuleVersion => "1.0.3";
    public override string ModuleAuthor => "Gold KingZ";
    public override string ModuleDescription => "https://github.com/oqyh";
	public static MainPlugin Instance { get; set; } = new();

    private readonly ClientPrefsApiImpl _api;
    private bool _mapEndHandled;

    public MainPlugin()
    {
        _api = new ClientPrefsApiImpl(this);
    }

    public override void Load(bool hotReload)
    {
        Instance = this;
        Capabilities.RegisterPluginCapability(ClientPrefsApi.Capability, () => _api);

        RegisterEventHandler<EventPlayerConnectFull>(OnPlayerConnectFull);
        RegisterEventHandler<EventPlayerDisconnect>(OnPlayerDisconnect);
        RegisterListener<Listeners.OnMapStart>(OnMapStart);
        RegisterListener<Listeners.OnMapEnd>(OnMapEnd);
    }

    public override void Unload(bool hotReload)
    {
        _api.UnloadAllStores();
    }

    private HookResult OnPlayerConnectFull(EventPlayerConnectFull @event, GameEventInfo info)
    {
        if (@event == null) return HookResult.Continue;

        var player = @event.Userid;
        if (player == null || !player.IsValid || player.IsBot || player.IsHLTV) return HookResult.Continue;

        foreach (var store in _api.All)
            _ = store.LoadPlayerAsync(player);

        return HookResult.Continue;
    }

    private HookResult OnPlayerDisconnect(EventPlayerDisconnect @event, GameEventInfo info)
    {
        if (@event == null) return HookResult.Continue;

        var player = @event.Userid;
        if (player == null || !player.IsValid || player.IsBot || player.IsHLTV) return HookResult.Continue;

        int slot = player.Slot;
        LoadNotifier.ClearSlot(slot);
        foreach (var store in _api.All)
            _ = store.OnPlayerDisconnectAsync(slot);

        return HookResult.Continue;
    }

    private void OnMapStart(string mapName)
    {
        _mapEndHandled = false;
    }

    private void OnMapEnd()
    {
        if (_mapEndHandled)
        {
            DebugCore("OnMapEnd fired again — already handled, skipping duplicate");
            return;
        }
        _mapEndHandled = true;
        LoadNotifier.ClearAll();

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