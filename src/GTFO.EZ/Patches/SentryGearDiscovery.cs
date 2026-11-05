using System;
using GameData;
using HarmonyLib;

namespace GTFO.EZ.Patches;

// G.EZ-T111 one-shot startup diagnostic: finds the vanilla Sentry Gun's actual
// GearCategoryDataBlock/PlayerOfflineGearDataBlock block names by walking from
// GD.Gear.Basic_SentryGun's own base item id (the same numeric chain BotSentryEquipPatch already
// uses) rather than guessing a name string - GTFO ships no loose GameData JSON to check one
// against. The multi-turret-variant feature needs these names to clone the Sentry Gun the same way
// GTFO.EZ.AbsurdWeapons' GunInjector.AddGlueTool clones the C-Foam launcher. Remove once that
// feature is built from this output.
[HarmonyPatch(typeof(GameDataInit), nameof(GameDataInit.Initialize))]
internal static class SentryGearDiscovery
{
    [HarmonyPostfix]
    private static void Postfix()
    {
        try
        {
            var gearBlock = GameDataBlockBase<GearDataBlock>.GetBlock(GD.Gear.Basic_SentryGun);
            var itemBlock = gearBlock != null ? GameDataBlockBase<ItemDataBlock>.GetBlock(gearBlock.baseItemID) : null;
            if (itemBlock == null)
            {
                Plugin.Logger.LogWarning("G.EZ-T111 discovery: couldn't resolve the Sentry Gun's base item block.");
                return;
            }
            Plugin.Logger.LogInfo($"G.EZ-T111 discovery: Sentry Gun base item id = {itemBlock.persistentID}.");

            var found = 0;
            foreach (var category in GameDataBlockBase<GearCategoryDataBlock>.GetAllBlocks())
            {
                if (category.BaseItem != itemBlock.persistentID) continue;
                Plugin.Logger.LogInfo($"G.EZ-T111 discovery: category name='{category.name}' id={category.persistentID}.");

                foreach (var gear in GameDataBlockBase<PlayerOfflineGearDataBlock>.GetAllBlocks())
                {
                    if (GearJson.CategoryId(gear.GearJSON) != category.persistentID) continue;
                    Plugin.Logger.LogInfo($"G.EZ-T111 discovery: gear name='{gear.name}' id={gear.persistentID}.");
                    found++;
                }
            }

            if (found == 0) Plugin.Logger.LogWarning("G.EZ-T111 discovery: no PlayerOfflineGearDataBlock matched the Sentry Gun's category - it may not be loadout-selectable the normal way.");
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"G.EZ-T111 discovery failed: {e}");
        }
    }
}
