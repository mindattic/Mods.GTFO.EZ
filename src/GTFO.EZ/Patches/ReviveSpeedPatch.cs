using System;
using System.Collections.Generic;
using HarmonyLib;

namespace GTFO.EZ.Patches;

/// <summary>
/// Scales how long a revive interaction takes. Interact_Revive inherits InteractDuration from the
/// generic Interact_Timed base, but each instance (revive, door hacking, item pickups, etc.) owns
/// its own value, so patching Setup here only ever touches revives.
/// </summary>
[HarmonyPatch(typeof(Interact_Revive), nameof(Interact_Revive.Setup))]
internal static class Interact_Revive_Setup_Patch
{
    // G.EZ-T109: the duration this patch last left on each instance. If Setup runs again on the same
    // instance without resetting the value, it still equals what we wrote and isn't scaled a second
    // time; a value Setup did reset (anything else) is the new base and is scaled once.
    private static readonly Dictionary<IntPtr, float> Scaled = new();

    [HarmonyPostfix]
    private static void Postfix(Interact_Revive __instance)
    {
        if (__instance == null) return;
        var key = __instance.Pointer;
        if (Scaled.TryGetValue(key, out var left) && __instance.InteractDuration == left) return;
        if (Scaled.Count > 256) Scaled.Clear(); // destroyed instances' pointers; a handful exist at once
        __instance.InteractDuration *= Plugin.ReviveDurationMultiplier.Value;
        Scaled[key] = __instance.InteractDuration;
    }
}
