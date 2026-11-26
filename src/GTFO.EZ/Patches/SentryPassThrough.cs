using HarmonyLib;
using UnityEngine;

namespace GTFO.EZ.Patches;

// Placed sentries never block each other or anyone's bullets: every solid collider on a live
// sentry moves to a layer none of the bullet or sentry ray masks include (chosen once from the
// game's own masks, not assumed), and live sentries ignore each other's colliders so two can stand
// overlapping. Triggers and the interaction layer - what the pickup prompt looks for - are left
// alone. Runs on every machine when a sentry is turned on (SyncedTurnOn), as each one simulates its
// own copy.
[HarmonyPatch(typeof(SentryGunInstance))]
internal static class SentryPassThrough
{
    private static int? _layer;
    private static bool _layerChosen;
    private static readonly Dictionary<IntPtr, List<Collider>> live = new();

    [HarmonyPatch(nameof(SentryGunInstance.SyncedTurnOn))]
    [HarmonyPostfix]
    private static void AfterTurnOn(SentryGunInstance __instance)
    {
        try
        {
            if (!LevelSettings.Effective(Plugin.SentriesPassThrough) || __instance == null) return;
            var layer = PassLayer();
            if (layer == null) return;

            Forget(null); // drop sentries destroyed without a pickup
            var mine = new List<Collider>();
            foreach (var collider in __instance.GetComponentsInChildren<Collider>(true))
            {
                if (collider == null || collider.isTrigger || collider.gameObject.layer == LayerManager.LAYER_INTERACTION) continue;
                collider.gameObject.layer = layer.Value;
                mine.Add(collider);
            }
            foreach (var other in live.Values)
                foreach (var a in mine)
                    foreach (var b in other)
                        if (a != null && b != null) Physics.IgnoreCollision(a, b, true);
            live[__instance.Pointer] = mine;
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"Sentries: couldn't make a sentry pass-through. {e.Message}");
        }
    }

    [HarmonyPatch(nameof(SentryGunInstance.SyncedTurnOff))]
    [HarmonyPostfix]
    private static void AfterTurnOff(SentryGunInstance __instance) => Forget(__instance);

    [HarmonyPatch(nameof(SentryGunInstance.OnDespawn))]
    [HarmonyPostfix]
    private static void AfterDespawn(SentryGunInstance __instance) => Forget(__instance);

    // Stops tracking one sentry (or, with null, every one whose object is already gone).
    private static void Forget(SentryGunInstance? sentry)
    {
        try
        {
            if (sentry != null)
            {
                live.Remove(sentry.Pointer);
                return;
            }
            var gone = new List<IntPtr>();
            foreach (var (ptr, colliders) in live)
                if (colliders.Count == 0 || colliders.TrueForAll(c => c == null || c.WasCollected)) gone.Add(ptr);
            foreach (var ptr in gone) live.Remove(ptr);
        }
        catch (Exception)
        {
            // Bookkeeping only; never let it reach the game.
        }
    }

    // The first candidate layer no bullet or sentry ray can hit and the player's camera still draws
    // (a collider's object can carry the sentry's mesh too), or null (logged) if none qualifies.
    private static int? PassLayer()
    {
        if (_layerChosen) return _layer;
        var camera = Camera.main;
        if (camera == null) return null; // try again on the next sentry, once there's a camera
        _layerChosen = true;
        var blocked = LayerManager.MASK_SENTRYGUN_RAY | LayerManager.MASK_SENTRYGUN_DETECTION_BLOCKERS
            | LayerManager.MASK_SENTRYGUN_WEAPON_TARGETS | LayerManager.MASK_BULLETWEAPON_RAY
            | LayerManager.MASK_BULLETWEAPON_PIERCING_PASS;
        foreach (var candidate in new[] { LayerManager.LAYER_NOCOLLISION_BLOCKGRAPH, LayerManager.LAYER_DEBRIS, LayerManager.LAYER_DEFAULT_NOGRAPH })
        {
            if ((blocked & (1 << candidate)) != 0 || (camera.cullingMask & (1 << candidate)) == 0) continue;
            _layer = candidate;
            Plugin.Logger.LogInfo($"Sentries: pass-through bodies go on layer {LayerMask.LayerToName(candidate)} ({candidate}).");
            return _layer;
        }
        Plugin.Logger.LogWarning("Sentries: no visible layer is free of every bullet and sentry ray - sentries keep their vanilla bodies.");
        return null;
    }
}
