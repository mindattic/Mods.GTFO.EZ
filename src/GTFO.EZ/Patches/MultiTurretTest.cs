using System;
using System.Collections.Generic;
using GameData;
using Gear;
using HarmonyLib;
using MindAttic.GTFO.Shared;
using Player;
using UnityEngine;

namespace GTFO.EZ.Patches;

// G.EZ-T111 test 1: while the Tool slot is wielded, pressing its select key again cycles the local
// player's Sentry Gun through four real vanilla fire-mode variants (SentryGun_Burst,
// SentryGun_Auto_staggering, SentryGun_Semi_Sniper, SentryGun_ShotgunSemi - all PlayerOfflineGearDataBlock
// entries under the vanilla SentryGun category, confirmed by SentryGearDiscovery; the category's fifth
// entry, LevelSpawned_Security_Turret_Burst, is the hostile enemy emplacement and is left out) instead of
// just the one picked at loadout - the same key-press-cycles-the-slot interaction GTFO.EZ.AbsurdWeapons'
// GearCycler uses for weapons. Placement itself is untouched (plain E, vanilla), so one player can end up
// with several different turrets down at once, each with its own fire pattern instead of a color.
[HarmonyPatch(typeof(Dam_PlayerDamageLocal), nameof(Dam_PlayerDamageLocal.Update))]
internal static class MultiTurretTest
{
    // Burst, staggering auto, sniper semi, shotgun - in cycle order.
    private static readonly uint[] VariantGearBlockIds = { 24, 6, 23, 25 };

    private static List<GearIDRange>? _variants;
    private static int _currentIndex = -1;
    private static float _nextAllowedCycleAt;

    [HarmonyPostfix]
    private static void Postfix(Dam_PlayerDamageLocal __instance)
    {
        if (!LocalPlayerDamage.IsMine(__instance)) return;
        if (!Plugin.MultiSentryCyclingEnabled.Value || !Hotkeys.InPlay) return;
        if (Time.time < _nextAllowedCycleAt) return;

        try
        {
            if (!PlayerManager.TryGetLocalPlayerAgent(out var agent) || agent?.Inventory == null) return;
            if (agent.Inventory.WieldedSlot != InventorySlot.GearClass) return;
            if (!InputMapper.GetButtonDownKeyMouse(InputAction.SelectTool, FocusStateManager.CurrentState)) return;

            var variants = Variants();
            if (variants.Count == 0)
            {
                Plugin.Logger.LogWarning("G.EZ-T111: no Sentry Gun variants resolved - nothing to cycle to.");
                return;
            }

            _currentIndex = (_currentIndex + 1) % variants.Count;
            var next = variants[_currentIndex];
            PlayerBackpackManager.EquipLocalGear(next);
            _nextAllowedCycleAt = Time.time + 0.3f; // the new item is built asynchronously - don't re-trigger mid-swap

            var name = GameDataBlockBase<PlayerOfflineGearDataBlock>.GetBlock(VariantGearBlockIds[_currentIndex])?.name ?? "Sentry Gun";
            MessageHelper.Show($"Sentry Gun: {name}  [{_currentIndex + 1}/{variants.Count}]", MessageType.Center, 3f);
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"G.EZ-T111: couldn't cycle your Sentry Gun. {e}");
        }
    }

    private static List<GearIDRange> Variants()
    {
        if (_variants != null) return _variants;

        var list = new List<GearIDRange>();
        foreach (var id in VariantGearBlockIds)
        {
            var block = GameDataBlockBase<PlayerOfflineGearDataBlock>.GetBlock(id);
            if (block == null)
            {
                Plugin.Logger.LogWarning($"G.EZ-T111: Sentry Gun variant block {id} not found - skipping it.");
                continue;
            }
            try
            {
                list.Add(new GearIDRange(block.GearJSON));
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning($"G.EZ-T111: couldn't build a GearIDRange for variant block {id}. {e}");
            }
        }
        _variants = list;
        return list;
    }
}
