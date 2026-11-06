using Gear;
using GameData;
using HarmonyLib;
using Player;
using SNetwork;

namespace GTFO.EZ.Patches;

/// <summary>
/// Forces a bot's Tool slot to the basic Sentry Gun (GD.Gear.Basic_SentryGun, resolved through the
/// game's own name->ID catalog rather than a hardcoded number) instead of whatever favorite/inherited
/// gear it would otherwise get. Hooks both bot-gear-assignment paths: filling an empty slot
/// (GiveFavoriteGearToBot) and replacing a player mid-expedition (CopyGearToBot).
/// </summary>
[HarmonyPatch(typeof(PlayerBackpackManager))]
internal static class BotSentryEquipPatch
{
    private static GearIDRange? _sentryGear;

    private static GearIDRange? GetSentryGear()
    {
        if (_sentryGear != null) return _sentryGear;

        var gearBlock = GameDataBlockBase<GearDataBlock>.GetBlock(GD.Gear.Basic_SentryGun);
        var itemBlock = gearBlock != null ? GameDataBlockBase<ItemDataBlock>.GetBlock(gearBlock.baseItemID) : null;
        if (itemBlock == null || !GearManager.TryGetGearFromItem(InventorySlot.GearClass, itemBlock, out var gear))
        {
            Plugin.Logger.LogWarning("GTFO.EZ: could not resolve Sentry Gun gear for bots.");
            return null;
        }

        _sentryGear = gear;
        return _sentryGear;
    }

    [HarmonyPatch(nameof(PlayerBackpackManager.GiveFavoriteGearToBot))]
    [HarmonyPostfix]
    private static void GiveFavoriteGearToBot_Postfix(SNet_Player bot)
    {
        if (!Plugin.BotsAlwaysEquipSentryGun.Value) return;
        var gear = GetSentryGear();
        if (gear != null) PlayerBackpackManager.EquipBotGear(bot, gear);
    }

    [HarmonyPatch(nameof(PlayerBackpackManager.CopyGearToBot))]
    [HarmonyPostfix]
    private static void CopyGearToBot_Postfix(SNet_Player bot)
    {
        if (!Plugin.BotsAlwaysEquipSentryGun.Value) return;
        // G.EZ-T119: a bot taking over a player whose sentry is already down keeps that deployed one -
        // re-equipping would hand it a fresh undeployed sentry, a second turret for E/X to place.
        if (PlayerBackpackManager.TryGetBackpack(bot, out var backpack) && backpack != null && backpack.IsDeployed(InventorySlot.GearClass)) return;
        var gear = GetSentryGear();
        if (gear != null) PlayerBackpackManager.EquipBotGear(bot, gear);
    }
}
