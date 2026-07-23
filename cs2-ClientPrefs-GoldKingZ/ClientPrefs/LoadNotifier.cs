using System.Collections.Concurrent;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using System.Text.RegularExpressions;
using CounterStrikeSharp.API.Core.Translations;

namespace ClientPrefs_GoldKingZ;

internal static class LoadNotifier
{
    private const int WaitMessageCooldownSeconds = 3;
    private static readonly string MessageStillLoading = MainPlugin.Instance.Localizer["PrintToChatToPlayer.ClientPrefs.Loading"];
    private static readonly string MessageLoaded = MainPlugin.Instance.Localizer["PrintToChatToPlayer.ClientPrefs.Loaded"];
    private sealed class SlotState
    {
        public ulong SteamId;
        public int   Pending;
        public bool  WaitMessageShown;
        public bool  LoadedMessageSent;
        public DateTime LastWaitMessage = DateTime.MinValue;
    }

    private static readonly ConcurrentDictionary<int, SlotState> _slots = new();
    private static readonly object _lock = new();

    public static void RegisterPending(int slot, ulong steamId)
    {
        lock (_lock)
        {
            var s = _slots.GetOrAdd(slot, _ => new SlotState { SteamId = steamId });

            if (s.SteamId != steamId || s.Pending == 0)
            {
                s.SteamId = steamId;
                s.WaitMessageShown = false;
                s.LoadedMessageSent = false;
                s.LastWaitMessage = DateTime.MinValue;
            }

            s.Pending++;
        }
    }

    public static void MarkReady(int slot, ulong steamId)
    {
        bool fireLoaded = false;

        lock (_lock)
        {
            if (!_slots.TryGetValue(slot, out var s) || s.SteamId != steamId) return;

            if (s.Pending > 0) s.Pending--;

            if (s.Pending == 0 && s.WaitMessageShown && !s.LoadedMessageSent)
            {
                s.LoadedMessageSent = true;
                fireLoaded = true;
            }
        }

        if (fireLoaded)
        {
            Server.NextFrame(() => PrintToSlot(slot, MessageLoaded));
        }
    }

    public static void ShowWaitMessage(CCSPlayerController? player)
    {
        if (player == null || !player.IsValid) return;

        lock (_lock)
        {
            if (!_slots.TryGetValue(player.Slot, out var s)) return;
            if (s.Pending == 0) return;
            if ((DateTime.Now - s.LastWaitMessage).TotalSeconds < WaitMessageCooldownSeconds) return;

            s.LastWaitMessage = DateTime.Now;
            s.WaitMessageShown = true;
        }

        try { AdvancedPlayerPrintToChat(player, null!, MessageStillLoading); } catch { }
    }

    public static void ClearSlot(int slot)
    {
        lock (_lock)
        {
            _slots.TryRemove(slot, out _);
        }
    }

    public static void ClearAll()
    {
        lock (_lock)
        {
            _slots.Clear();
        }
    }
    
    private static void PrintToSlot(int slot, string message)
    {
        try
        {
            var player = Utilities.GetPlayerFromSlot(slot);
            if (player == null || !player.IsValid || player.IsBot || player.IsHLTV) return;
            AdvancedPlayerPrintToChat(player, null!, message);
        }
        catch
        {
        }
    }

    public static void AdvancedPlayerPrintToChat(CCSPlayerController player, CounterStrikeSharp.API.Modules.Commands.CommandInfo commandInfo, string message, params object[] args)
    {
        if (string.IsNullOrEmpty(message)) return;

        for (int i = 0; i < args.Length; i++)
        {
            message = message.Replace($"{{{i}}}", args[i]?.ToString() ?? "");
        }

        if (Regex.IsMatch(message, "{nextline}", RegexOptions.IgnoreCase))
        {
            string[] parts = Regex.Split(message, "{nextline}", RegexOptions.IgnoreCase);
            foreach (string part in parts)
            {
                string trimmedPart = part.Trim();
                trimmedPart = trimmedPart.ReplaceColorTags();
                if (!string.IsNullOrEmpty(trimmedPart))
                {
                    if (commandInfo != null && commandInfo.CallingContext == CounterStrikeSharp.API.Modules.Commands.CommandCallingContext.Console)
                    {
                        player.PrintToConsole(" " + trimmedPart);
                    }
                    else
                    {
                        player.PrintToChat(" " + trimmedPart);
                    }
                }
            }
        }
        else
        {
            message = message.ReplaceColorTags();
            if (commandInfo != null && commandInfo.CallingContext == CounterStrikeSharp.API.Modules.Commands.CommandCallingContext.Console)
            {
                player.PrintToConsole(message);
            }
            else
            {
                player.PrintToChat(message);
            }
        }
    }
}