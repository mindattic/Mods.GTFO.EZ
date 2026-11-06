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
[HarmonyBefore("MindAttic.GTFO.EZ.AbsurdItems")] // G.EZ-T119: explicit order on a method AbsurdItems also patches (AGENTS.md golden rule); Before matches its After(GTFO.EZ) convention, so no cycle (cf. G.EZ-T108)
internal static class MultiTurretTest
{
    // Burst, staggering auto, sniper semi, shotgun - in cycle order.
    private static readonly uint[] VariantGearBlockIds = { 24, 6, 23, 25 };

    private static List<(uint BlockId, GearIDRange Gear)>? _variants;
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
            // G.EZ-T119: only a Sentry Gun cycles - a Bio Tracker, mine deployer or C-Foam launcher in
            // the Tool slot used to be swapped for a sentry it could never be cycled back to.
            if (BotDeployAtAim.LocalSentry() == null) return;
            if (!InputMapper.GetButtonDownKeyMouse(InputAction.SelectTool, FocusStateManager.CurrentState)) return;

            var variants = Variants();
            if (variants.Count == 0)
            {
                Plugin.Logger.LogWarning("G.EZ-T111: no Sentry Gun variants resolved - nothing to cycle to.");
                return;
            }

            // G.EZ-T119: step from the variant actually in hand (a fresh level or a Burst loadout used to
            // start at index 0 and rebuild the same sentry); the remembered index is only the fallback.
            var held = EquippedIndex(variants);
            _currentIndex = ((held >= 0 ? held : _currentIndex) + 1) % variants.Count;
            var next = variants[_currentIndex].Gear;
            PlayerBackpackManager.EquipLocalGear(next);
            _nextAllowedCycleAt = Time.time + 0.3f; // the new item is built asynchronously - don't re-trigger mid-swap

            var name = GameDataBlockBase<PlayerOfflineGearDataBlock>.GetBlock(variants[_currentIndex].BlockId)?.name ?? "Sentry Gun";
            MessageHelper.Show($"Sentry Gun: {name}  [{_currentIndex + 1}/{variants.Count}]", MessageType.Center, 3f);
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"G.EZ-T111: couldn't cycle your Sentry Gun. {e}");
        }
    }

    private static int EquippedIndex(List<(uint BlockId, GearIDRange Gear)> variants)
    {
        var backpack = PlayerBackpackManager.LocalBackpack;
        if (backpack == null || !backpack.TryGetBackpackItem(InventorySlot.GearClass, out var item)) return -1;
        var name = item?.GearIDRange?.PublicGearName;
        if (string.IsNullOrEmpty(name)) return -1;
        var match = -1;
        for (var i = 0; i < variants.Count; i++)
        {
            if (variants[i].Gear.PublicGearName != name) continue;
            if (match >= 0) return -1; // names not unique across variants - can't tell, use the remembered index
            match = i;
        }
        return match;
    }

    private static List<(uint BlockId, GearIDRange Gear)> Variants()
    {
        if (_variants != null) return _variants;

        var list = new List<(uint BlockId, GearIDRange Gear)>();
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
                list.Add((id, new GearIDRange(block.GearJSON)));
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
