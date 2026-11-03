using System;
using System.Collections.Generic;
using GameData;
using HarmonyLib;

namespace GTFO.EZ.Patches;

// m_deployPickupInteractionDuration is a plain instance field set up in Setup(), not something
// GameDataTweaks' usual data-block edits reach - it governs how long you hold the interact key to
// both place and retrieve a sentry gun. Zeroing it here removes the charge-up/wait entirely.
[HarmonyPatch(typeof(SentryGunFirstPerson), nameof(SentryGunFirstPerson.Setup))]
internal static class SentryGunFirstPerson_Setup_Patch
{
    // G.EZ-T109: as for revives - a Setup re-run on the same instance that left our value in place
    // isn't scaled again (compounding); a reset value is the new base and is scaled once.
    private static readonly Dictionary<IntPtr, float> Scaled = new();

    [HarmonyPostfix]
    private static void Postfix(SentryGunFirstPerson __instance, ItemDataBlock data)
    {
        if (__instance == null) return;
        var key = __instance.Pointer;
        if (Scaled.TryGetValue(key, out var left) && __instance.m_deployPickupInteractionDuration == left) return;
        if (Scaled.Count > 256) Scaled.Clear();
        __instance.m_deployPickupInteractionDuration *= Plugin.SentryDeployPickupInteractionDurationMultiplier.Value;
        Scaled[key] = __instance.m_deployPickupInteractionDuration;
    }
}
