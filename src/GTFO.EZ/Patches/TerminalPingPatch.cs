using System;
using System.Collections.Generic;
using HarmonyLib;
using LevelGeneration;
using UnityEngine;

namespace GTFO.EZ.Patches;

// Ping anywhere (G.EZ-T10): vanilla PING only reaches items in the terminal's own zone; this lets
// "PING <KEY>" from any terminal reach an item anywhere in the level.
//
// Technique credit: BetterTerminal by AlexanderMakunin (MIT, Copyright (c) 2026 AlexanderMakunin,
// https://github.com/AlexanderMakunin/BetterTerminal, Thunderstore "Daem0n/BetterTerminal"). Its
// PING (ConsumablePingPatch + ConsumableTerminalRegistry.TryPlayPing) owns the whole command - it
// writes the echo and output straight into the interpreter's buffers (TerminalCommandOutput.
// WriteImmediate) and returns false so vanilla never also evaluates the same input - and expires its
// temporary ping itself (AttemptSetTerminalPing(false) + RemoveLocatorBeaconIfActive). An earlier
// attempt here pinged from a prefix but still let vanilla evaluate the line too, and broke the
// terminal in play.
//
// What this does differently: it only takes over a PING that vanilla would refuse - a known key
// whose item is in a different zone. Same-zone pings, unknown keys, flags-only or malformed input
// all go to vanilla untouched, so normal PING output and behaviour never change, and any exception
// falls back to vanilla rather than eating the command.
[HarmonyPatch(typeof(LG_ComputerTerminalCommandInterpreter), nameof(LG_ComputerTerminalCommandInterpreter.EvaluateInput))]
internal static class TerminalPing_EvaluateInput_Patch
{
    private const string Separator = "------------------------------------------------------------";

    [HarmonyPrefix]
    private static bool Prefix(LG_ComputerTerminalCommandInterpreter __instance, string inputString)
    {
        if (!Plugin.TerminalPingAnywhere.Value) return true;

        try
        {
            if (!TryParsePing(inputString, out var key)) return true;

            var item = FindItem(key);
            if (item == null) return true; // vanilla reports an unknown key itself

            var terminalZone = __instance.m_terminal?.SpawnNode?.m_zone;
            var itemZone = item.SpawnNode?.m_zone;
            if (terminalZone == null || itemZone == null || terminalZone.Pointer == itemZone.Pointer) return true;

            TerminalPingAnywhere.Ping(item);
            Write(__instance, $"PING {key}", $"Pinging {key} in ZONE_{itemZone.Alias} from this terminal.");
            Plugin.Logger.LogInfo($"Ping Anywhere: pinged {key} in ZONE_{itemZone.Alias} from a ZONE_{terminalZone.Alias} terminal.");
            return false;
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"Ping Anywhere: failed on \"{inputString}\", leaving it to the game's own PING. {e}");
            return true;
        }
    }

    // "PING KEY", "PING KEY -t" or "PING -t KEY" (vanilla's repeat flag is accepted but not repeated
    // across zones); anything else isn't ours.
    private static bool TryParsePing(string? input, out string key)
    {
        key = string.Empty;
        var tokens = (input ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 2 || !string.Equals(tokens[0], "PING", StringComparison.OrdinalIgnoreCase)) return false;

        for (var i = 1; i < tokens.Length; i++)
        {
            if (tokens[i].StartsWith("-")) continue;
            if (key.Length > 0) return false; // two keys - not a form vanilla takes either
            key = tokens[i].ToUpperInvariant();
        }
        return key.Length > 0;
    }

    // G.EZ-T118: this used to look the key up in LG_LevelInteractionManager.m_terminalItemsByKeyString,
    // but that's an Il2Cpp Dictionary<string, iTerminalItem> - an interface value type Il2CppInterop
    // can't build, so every call threw "MissingMethodException: System.__Canon..ctor(IntPtr)" and every
    // cross-zone PING silently fell back to vanilla's same-zone one. Terminal items (consumables,
    // containers, doors, generators...) are LG_GenericTerminalItem components, a concrete class, so
    // the key is matched against those instead. Prefab copies outside the level have no SpawnNode.
    private static LG_GenericTerminalItem? FindItem(string key)
    {
        foreach (var obj in Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.Of<LG_GenericTerminalItem>()))
        {
            var item = obj?.TryCast<LG_GenericTerminalItem>();
            if (item == null || item.SpawnNode == null) continue;
            if (string.Equals(item.TerminalItemKey, key, StringComparison.OrdinalIgnoreCase)) return item;
        }
        return null;
    }

    // Same shape vanilla's own command output takes - blank line, prompt + command, separator,
    // output, separator - written the way BetterTerminal's WriteImmediate does it, plus the command
    // into input history so up-arrow recalls it.
    private static void Write(LG_ComputerTerminalCommandInterpreter interpreter, string commandLine, string output)
    {
        interpreter.m_inputBuffer.Add(commandLine);
        interpreter.m_inputBufferStep = 0;

        var screen = interpreter.m_screenBuffer;
        screen.Add(string.Empty);
        screen.Add(Prompt(screen) + commandLine);
        screen.Add(Separator);
        screen.Add(output);
        screen.Add(Separator);
        screen.Add(string.Empty);

        if (interpreter.m_terminal != null) interpreter.m_terminal.m_currentLine = string.Empty;
        interpreter.ResetLinesSinceCommand();
    }

    // The terminal's current prompt (e.g. "\\ROOT\") from its most recent prompt line.
    private static string Prompt(Il2CppSystem.Collections.Generic.List<string> screen)
    {
        for (var i = screen.Count - 1; i >= 0; i--)
        {
            var line = screen[i];
            if (line == null || !line.StartsWith(@"\\", StringComparison.Ordinal)) continue;
            var end = line.IndexOf('\\', 2);
            if (end >= 0) return line.Substring(0, end + 1);
        }
        return @"\\ROOT\";
    }
}

// The ping itself, and expiring it: one cross-zone ping at a time, shown for PingSeconds, then the
// terminal-ping marker and locator beacon are taken down again (vanilla's own same-zone PING
// handles its own; these were set outside its path, so nothing else would).
internal static class TerminalPingAnywhere
{
    private const float PingSeconds = 10f;

    private static uint? _activeId;
    private static float _expiresAt;

    internal static void Ping(LG_GenericTerminalItem item)
    {
        Clear();
        var position = item.LocatorBeaconPosition;
        var id = item.TerminalItemId;
        item.PlayPing();
        GuiManager.AttemptSetLocatorBeacon(true, position, id);
        GuiManager.AttemptSetTerminalPing(true, position, id);
        _activeId = id;
        _expiresAt = Time.time + PingSeconds;
    }

    internal static void Tick()
    {
        if (_activeId.HasValue && Time.time >= _expiresAt) Clear();
    }

    internal static void Clear()
    {
        if (!_activeId.HasValue) return;
        var id = _activeId.Value;
        _activeId = null;
        try
        {
            GuiManager.AttemptSetTerminalPing(false, Vector3.zero, id);
            GuiManager.RemoveLocatorBeaconIfActive(id);
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"Ping Anywhere: couldn't clear the last ping's marker. {e.Message}");
        }
    }
}

[HarmonyPatch(typeof(Dam_PlayerDamageLocal))]
internal static class TerminalPingAnywhere_Tick_Patch
{
    [HarmonyPatch(nameof(Dam_PlayerDamageLocal.Update))]
    [HarmonyPostfix]
    private static void Update_Postfix(Dam_PlayerDamageLocal __instance)
    {
        if (LocalPlayerDamage.IsMine(__instance)) TerminalPingAnywhere.Tick();
    }

    // A new level: the old level's marker ids mean nothing any more.
    [HarmonyPatch(nameof(Dam_PlayerDamageLocal.Setup))]
    [HarmonyPostfix]
    private static void Setup_Postfix(Dam_PlayerDamageLocal __instance)
    {
        if (LocalPlayerDamage.IsMine(__instance)) TerminalPingAnywhere.Clear();
    }
}
