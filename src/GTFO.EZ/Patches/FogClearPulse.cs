using System;
using HarmonyLib;
using Player;
using UnityEngine;

namespace GTFO.EZ.Patches;

// Disable Fog flips FogSettingsDataBlock.Enabled, but fog still showed in play, so this clears it
// the way the Fog Repeller item does instead: a FogSphereAllocator sphere carved out of the fog
// volume. It's the bare fog-system sphere under FogRepeller_Sphere, without the item's light,
// sound or 70 s lifetime, so it's silent and permanent. It follows the local player and widens the
// longer you're in a level, from Start Range to Max Range over Ramp Seconds. Off by default since
// G.EZ-T115: that fog is what keeps levels dark, and with it carved away no flashlight was needed.
[HarmonyPatch(typeof(Dam_PlayerDamageLocal))]
internal static class FogClearPulse_Patch
{
    [HarmonyPatch(nameof(Dam_PlayerDamageLocal.Update))]
    [HarmonyPostfix]
    private static void Update_Postfix(Dam_PlayerDamageLocal __instance)
    {
        if (LocalPlayerDamage.IsMine(__instance)) FogClearPulse.Tick();
    }

    [HarmonyPatch(nameof(Dam_PlayerDamageLocal.Setup))]
    [HarmonyPostfix]
    private static void Setup_Postfix(Dam_PlayerDamageLocal __instance)
    {
        if (LocalPlayerDamage.IsMine(__instance)) FogClearPulse.Reset();
    }
}

internal static class FogClearPulse
{
    // A failed allocation (the fog system's sphere pool can be full, or not built yet early in a
    // level) is retried this often rather than every frame.
    private const float RetrySeconds = 2f;
    private const float FallbackDensity = -1f;

    private static FogSphereAllocator? _sphere;
    private static float _elapsed;
    private static float _retryIn;
    private static float? _vanillaDensity;
    private static bool _failed;

    internal static void Reset()
    {
        try
        {
            _sphere?.Deallocate();
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"Fog Clearing: couldn't release the previous sphere. {e.Message}");
        }
        _sphere = null;
        _elapsed = 0f;
        _retryIn = 0f;
        _failed = false; // a failure (e.g. mid level unload) only switches it off until the next level
    }

    internal static void Tick()
    {
        if (!Plugin.FogClearEnabled.Value)
        {
            if (_sphere != null) Reset();
            return;
        }
        if (_failed) return;

        try
        {
            if (!PlayerManager.TryGetLocalPlayerAgent(out var agent) || agent == null) return;

            _elapsed += Time.deltaTime;
            if (_sphere == null && !TryAllocate()) return;

            var ramp = Plugin.FogClearRampSeconds.Value;
            var t = ramp <= 0f ? 1f : Mathf.Clamp01(_elapsed / ramp);
            var range = Mathf.Lerp(Plugin.FogClearStartRange.Value, Plugin.FogClearMaxRange.Value, t);
            _sphere!.SetPositionRange(agent.transform.position + Vector3.up, range);
        }
        catch (Exception e)
        {
            // Stops until the next level rather than throwing every frame.
            _failed = true;
            Plugin.Logger.LogError($"Fog Clearing: failed, turning it off until the next level. {e}");
        }
    }

    private static bool TryAllocate()
    {
        _retryIn -= Time.deltaTime;
        if (_retryIn > 0f) return false;
        _retryIn = RetrySeconds;

        var sphere = new FogSphereAllocator();
        if (!sphere.TryAllocate()) return false;

        var density = Density();
        sphere.SetDensity(density);
        _sphere = sphere;
        Plugin.Logger.LogInfo($"Fog Clearing: sphere allocated (density {density:0.###}).");
        return true;
    }

    // The override wins if set; otherwise the vanilla Fog Repeller's own density, read off its
    // loaded prefab, so this clears fog exactly as hard as the item does.
    private static float Density()
    {
        var overrideDensity = Plugin.FogClearDensityOverride.Value;
        if (overrideDensity != 0f) return overrideDensity;
        if (_vanillaDensity is { } density) return density;

        // Only a density actually read is cached - the fallback is retried at the next lookup, since
        // the Fog Repeller prefab may simply not be loaded yet on the first one.
        var read = ReadVanillaDensity();
        if (read.HasValue) _vanillaDensity = read;
        return read ?? FallbackDensity;
    }

    private static float? ReadVanillaDensity()
    {
        foreach (var obj in Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.Of<FogRepeller_Sphere>()))
        {
            var repeller = obj?.TryCast<FogRepeller_Sphere>();
            if (repeller == null || repeller.Density == 0f) continue;
            Plugin.Logger.LogInfo($"Fog Clearing: vanilla Fog Repeller is range {repeller.Range:0.##}, density {repeller.Density:0.###}, grow {repeller.GrowDuration:0.##} s.");
            return repeller.Density;
        }
        Plugin.Logger.LogWarning($"Fog Clearing: no loaded Fog Repeller to copy density from, using {FallbackDensity} for now. Set Density Override if fog doesn't clear.");
        return null;
    }
}
