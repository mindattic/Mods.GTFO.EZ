using System;
using HarmonyLib;
using Player;
using SNetwork;

namespace GTFO.EZ.Patches;

// G.EZ-T123: nobody drops into a level short on supplies. Vanilla expeditions start you on a share
// of your reserves; this fills every gun reserve (main, special), the tool reserve - which is what
// a sentry is loaded from when it's placed - and every magazine, once per drop. Each human's
// backpack is owned by their own game, so every player's copy fills its own; the host's copy also
// fills its bots. Only a real drop counts: the flag re-arms in the lobby, so a checkpoint restart
// keeps the ammo the checkpoint saved.
[HarmonyPatch(typeof(Dam_PlayerDamageLocal), nameof(Dam_PlayerDamageLocal.Update))]
[HarmonyBefore("MindAttic.GTFO.EZ.AbsurdItems")] // G.EZ-T119 ordering convention
internal static class FullAmmoOnDrop
{
    private static readonly InventorySlot[] Slots = { InventorySlot.GearStandard, InventorySlot.GearSpecial, InventorySlot.GearClass };

    private static bool _filledThisDrop;
    private static bool _failed;

    [HarmonyPostfix]
    private static void Postfix(Dam_PlayerDamageLocal __instance)
    {
        if (!LocalPlayerDamage.IsMine(__instance)) return;

        var state = GameStateManager.CurrentStateName;
        if (state == eGameStateName.Lobby)
        {
            _filledThisDrop = false;
            _failed = false;
            return;
        }
        if (_filledThisDrop || _failed || state != eGameStateName.InLevel || !Plugin.FullAmmoOnDrop.Value) return;

        try
        {
            if (!Fill(PlayerBackpackManager.LocalBackpack, isLocal: true)) return; // backpack not ready yet - next frame
            if (SNet.IsMaster)
            {
                foreach (var agent in PlayerManager.PlayerAgentsInLevel)
                {
                    if (agent == null || agent.Owner == null || !agent.Owner.IsBot) continue;
                    if (PlayerBackpackManager.TryGetBackpack(agent.Owner, out var backpack)) Fill(backpack, isLocal: false);
                }
            }
            _filledThisDrop = true;
            Plugin.Logger.LogInfo("Full ammo on drop: reserves and magazines filled" + (SNet.IsMaster ? ", bots included." : "."));
        }
        catch (Exception e)
        {
            _failed = true; // once per drop, not every frame
            Plugin.Logger.LogWarning($"Full ammo on drop: couldn't fill. {e}");
        }
    }

    // False while the backpack's ammo isn't set up yet (caps still 0), so the next frame retries.
    private static bool Fill(PlayerBackpack? backpack, bool isLocal)
    {
        var storage = backpack?.AmmoStorage;
        if (storage == null) return false;

        var anyCap = false;
        foreach (var slot in Slots)
        {
            var ammo = storage.GetInventorySlotAmmo(slot);
            if (ammo == null || ammo.AmmoMaxCap <= 0f) continue;
            anyCap = true;
            if (ammo.AmmoInPack < ammo.AmmoMaxCap) storage.SetAmmo(ammo.AmmoType, ammo.AmmoMaxCap);
        }
        if (!anyCap) return false;

        storage.FillAllClips();
        if (isLocal) storage.UpdateAllAmmoUI();
        storage.NeedsSync = true;
        return true;
    }
}
