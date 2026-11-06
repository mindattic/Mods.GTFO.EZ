using System;
using System.Collections.Generic;
using HarmonyLib;
using MindAttic.GTFO.Shared;
using Player;
using SNetwork;
using UnityEngine;

namespace GTFO.EZ.Patches;

// G.EZ-T34 spike, since extended by G.EZ-T107: Shift+E deploys the first bot with an undeployed
// tool at wherever the local player is currently aiming their own tool's placement ghost - reusing
// the exact validated pose/validity SentryGunFirstPerson already computes every frame for the
// player's own placement, rather than guessing a pose ourselves. X deploys the same way but at the
// player's current position/orientation instead of their aim, one turret per press (every bot with
// an undeployed one, then finally the player's own), so repeated presses deploy the whole team
// without aiming at all. There's no vanilla order for "deploy now" to route through instead
// (PlayerBotManager's order enum only has Guard/Follow/Stop/Sneak/Roam/Invite/Activate/Deactivate -
// no Deploy, no item reference), so this calls the bot AI's own placement method directly (exposed
// as public by the IL2CPP interop stubs); Z, the reverse, likewise calls the bot AI's own pickup
// action for bots and SentryGunInstance.PickUp(PlayerAgent) directly (also public) for the local
// player's own - confirmed to compile against the real interop assemblies, not confirmed safe by
// decompiling alone - every call here is guarded so a failure logs and moves on instead of breaking
// the mod.
[HarmonyPatch(typeof(Dam_PlayerDamageLocal), nameof(Dam_PlayerDamageLocal.Update))]
internal static class DeployPickupHotkeys_Update_Patch
{
    [HarmonyPostfix]
    private static void Postfix(Dam_PlayerDamageLocal __instance)
    {
        if (!LocalPlayerDamage.IsMine(__instance)) return;
        if (!Plugin.BotsDeployOnShiftE.Value || !Hotkeys.InPlay) return;

        // Bots belong to the host: a client running these would only move its own copy of the bot's
        // turret, leaving host and client disagreeing about it. X and Z gate their bot half the same
        // way (SNet.IsMaster) and always run their own-sentry half regardless of host/client. The
        // Shift+E guard below is armed on a client too, so Shift+E means the same thing for everyone:
        // never your own placement.
        if (Hotkeys.ShiftHeld && Input.GetKeyDown(KeyCode.E))
        {
            Hotkeys.ShiftEPressedAt = Time.time;
            if (SNet.IsMaster) BotDeployAtAim.TryDeployNext();
        }

        if (Input.GetKeyDown(KeyCode.Z))
        {
            // G.EZ-T109: blocked while downed, the same as X (a downed player can't pick a turret up).
            if (!PlayerManager.TryGetLocalPlayerAgent(out var agent) || agent == null || !agent.Alive)
            {
                Plugin.Logger.LogInfo("Z: you can't recall turrets while downed.");
            }
            else
            {
                if (SNet.IsMaster) PickupAllBotTools.Activate();
                OwnSentryAutoPickup.Pickup();
            }
        }

        if (Input.GetKeyDown(KeyCode.X)) QuickDropAll.Activate();
    }
}

internal static class Hotkeys
{
    // Only in normal first-person play - not while typing at a terminal or in chat, in the Q comms
    // menu, on the map, etc., where these letters are just text or menu input.
    internal static bool InPlay => FocusStateManager.CurrentState == eFocusState.FPS;
    internal static bool ShiftHeld => Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);

    // When the last Shift+E bot deploy was pressed. Only the vanilla placement that same press
    // triggers is held back - plain E with Shift held for sprinting still places your own sentry.
    internal static float ShiftEPressedAt = -10f;
    internal const float ShiftEPressWindow = 0.25f;
}

// With sentry placement made instant, the plain E inside Shift+E would also complete the
// vanilla placement of your own held sentry in the same press. Hold that back for your own sentry
// only - a bot's placement and X's own call go through.
//
// G.EZ-T116: plain E with your own sentry in hand also works through the whole team, one turret per
// press. Placing your own sentry marks the Tool slot deployed (the item is now in the world), so
// SentryGunFirstPerson_PlaceOnGround_Patch below switches you back to your previous weapon and slot 4
// is empty - which is why one placement used to "remove the slot 4 items": the G.EZ-T111 variants are
// only different builds of that one slot, so nothing was left to place. So while any bot still has an
// undeployed sentry, E places that bot's sentry at your placement ghost instead and keeps your own in
// hand, ghost and all, for the next press; your own (the variant you've cycled to) goes down last,
// and only then does the existing postfix put slot 4 away and return your previous weapon.
[HarmonyPatch(typeof(SentryGunFirstPerson), nameof(SentryGunFirstPerson.PlaceOnGround))]
internal static class SentryGunFirstPerson_PlaceOnGround_ModifierGuard
{
    // With placement instant, a held E can re-trigger placement on the next frames; without the
    // cooldown (TeamSentryRules.TeamPlaceCooldown) one press would dump every remaining turret (and
    // then your own) on the same spot.
    private static float _lastTeamPlaceAt = -10f;

    [HarmonyPrefix]
    private static bool Prefix(SentryGunFirstPerson __instance)
    {
        if (OwnSentryAutoPlace.Placing || !Plugin.BotsDeployOnShiftE.Value) return true;
        var own = BotDeployAtAim.LocalSentry();
        if (own == null || own.Pointer != __instance.Pointer) return true; // a bot's placement - never ours to hold back

        // The press itself counts too: the sentry's own update can place it this frame before the
        // hotkey postfix has stamped ShiftEPressedAt (Unity doesn't order the two components).
        var pressedNow = Input.GetKeyDown(KeyCode.E);
        var shiftE = Hotkeys.ShiftHeld && (pressedNow || Time.time - Hotkeys.ShiftEPressedAt <= Hotkeys.ShiftEPressWindow);

        // Bots belong to the host; a client's E stays plain vanilla (just your own).
        var wieldingTool = false;
        var undeployed = 0;
        if (!shiftE && SNet.IsMaster)
        {
            try
            {
                wieldingTool = PlayerManager.TryGetLocalPlayerAgent(out var agent)
                    && agent?.Inventory != null && agent.Inventory.WieldedSlot == InventorySlot.GearClass;
                if (wieldingTool) undeployed = BotDeployAtAim.CountUndeployedBotSentries();
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning($"E: couldn't count bot turrets - placing your own instead. {e}");
            }
        }

        // The decision is GTFO.EZ.Core.TeamSentryRules (unit tested); this only gathers the inputs.
        return GTFO.EZ.Core.TeamSentryRules.Decide(shiftE, Time.time - _lastTeamPlaceAt, SNet.IsMaster, wieldingTool, undeployed) switch
        {
            GTFO.EZ.Core.TeamSentryPlacement.HoldOwn => false,
            GTFO.EZ.Core.TeamSentryPlacement.PlaceBotInstead => !TryPlaceNextBotInstead(__instance),
            _ => true,
        };
    }

    // True when a bot's sentry went down in place of your own, so the vanilla placement of your own
    // is skipped and it stays in your hands.
    private static bool TryPlaceNextBotInstead(SentryGunFirstPerson own)
    {
        try
        {
            var pose = new Pose(own.m_placePos, own.m_cameraLookRot);
            if (!BotDeployAtAim.DeployBotAt(pose, "E")) return false;

            _lastTeamPlaceAt = Time.time;
            var left = BotDeployAtAim.CountUndeployedBotSentries();
            MessageHelper.Show(left > 0
                ? $"Bot turret placed - {left} more bot turret(s), then yours"
                : "Bot turret placed - yours is next", MessageType.Center, 2.5f);
            return true;
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"E: couldn't place the next bot turret - placing your own instead. {e}");
            return false;
        }
    }
}

// The player's own placement already goes through the normal vanilla flow, but switches weapons
// back to whatever was equipped before pulling out the tool - vanilla leaves you empty-handed in
// the Tool slot afterward, which this treats as a missed step rather than desired behaviour.
[HarmonyPatch(typeof(SentryGunFirstPerson), nameof(SentryGunFirstPerson.PlaceOnGround))]
internal static class SentryGunFirstPerson_PlaceOnGround_Patch
{
    [HarmonyPostfix]
    private static void Postfix(SentryGunFirstPerson __instance, bool __runOriginal)
    {
        // Postfixes run even when a prefix skipped the original: when the Shift+E guard held your
        // own placement back, nothing was placed and you're still aiming the sentry for the bot.
        if (!__runOriginal) return;
        try
        {
            var localBackpack = PlayerBackpackManager.LocalBackpack;
            if (localBackpack == null) return;
            // Pointer, not ==: two Il2Cpp wrappers around the same native object aren't reference-equal.
            if (!localBackpack.TryGetBackpackItem(InventorySlot.GearClass, out var backpackItem) || backpackItem?.Instance?.Pointer != __instance.Pointer) return;

            if (!PlayerManager.TryGetLocalPlayerAgent(out var localAgent) || localAgent?.Inventory == null) return;
            // Only when you were actually holding the sentry - X places it from the backpack while
            // you hold something else, and that weapon must stay in your hands.
            if (localAgent.Inventory.WieldedSlot != InventorySlot.GearClass) return;
            var lastSlot = InventorySlot.None;
            if (!localBackpack.GetLastEquippedGear(ref lastSlot) || lastSlot == InventorySlot.None || lastSlot == InventorySlot.GearClass) return;
            if (!localBackpack.TryGetBackpackItem(lastSlot, out var previousItem) || previousItem?.Instance == null) return;
            var previousEquippable = previousItem.Instance.TryCast<ItemEquippable>();
            if (previousEquippable == null) return;

            localAgent.Inventory.SyncWieldItem(previousEquippable);
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"Instant Sentry Placement: couldn't switch back to your previous weapon after placing. {e}");
        }
    }
}

internal static class Bots
{
    internal static List<PlayerAIBot> List()
    {
        var result = new List<PlayerAIBot>();
        foreach (var agent in PlayerManager.PlayerAgentsInLevel)
        {
            if (agent == null || agent.Owner == null || !agent.Owner.IsBot) continue;
            var bot = agent.GetComponent<PlayerAIBot>();
            if (bot != null) result.Add(bot);
        }
        return result;
    }
}

internal static class BotDeployAtAim
{
    internal static void TryDeployNext()
    {
        Pose pose;
        try
        {
            if (!TryGetAimPose(out pose))
            {
                Plugin.Logger.LogInfo("Shift+E: aim your own equipped tool at a valid placement spot first - that's the aim this reuses.");
                return;
            }
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"Shift+E: couldn't read your placement aim. {e}");
            return;
        }

        // DeployBotAt logs its own reason (and now, per G.EZ-T110, which bot had what) on failure.
        DeployBotAt(pose, "Shift+E");
    }

    // Deploys the first bot with an undeployed sentry at the given pose. Shared by Shift+E (pose from
    // your aim) and X (pose from your current position/orientation) - only the pose source differs.
    internal static bool DeployBotAt(Pose pose, string logTag)
    {
        List<PlayerAIBot> bots;
        try
        {
            bots = Bots.List();
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"{logTag}: couldn't list bots. {e}");
            return false;
        }

        // Filled in as bots are skipped, so a total failure can say *why* instead of just "no bot has
        // an undeployed tool" - the symptom looked identical whether no bot carried a sentry, every
        // sentry was already down, or a bot carried something else entirely, and that ambiguity made
        // G.EZ-T110 impossible to pin down from a log alone.
        var skipped = new List<string>();

        foreach (var bot in bots)
        {
            try
            {
                var backpack = bot?.Backpack;
                if (backpack == null) { skipped.Add("no backpack"); continue; }
                if (!backpack.TryGetBackpackItem(InventorySlot.GearClass, out var backpackItem) || backpackItem == null)
                {
                    skipped.Add("empty Tool slot");
                    continue;
                }

                var gearName = backpackItem.GearIDRange?.PublicGearName ?? "unknown gear";
                if (backpack.IsDeployed(InventorySlot.GearClass))
                {
                    skipped.Add($"{gearName} (already deployed)");
                    continue;
                }

                // Only a sentry: a bot carrying a mine deployer, C-foam launcher or bio tracker would
                // fail the placement (or be marked deployed with nothing placed) ahead of a sentry bot.
                if (backpackItem.Instance?.TryCast<SentryGunFirstPerson>() == null)
                {
                    skipped.Add($"{gearName} (not a sentry)");
                    continue;
                }

                var desc = new PlayerBotActionDeploySentryGun.Descriptor(bot) { InstallationPose = pose };
                var action = new PlayerBotActionDeploySentryGun(desc) { m_backpackItem = backpackItem };
                action.PlaceSentryGunOnGround();
                // Marks it placed-but-still-owned, the bookkeeping the bot's own AI does after this
                // call. Clearing the slot instead (an earlier version) orphaned the turret, which
                // broke bot item commands and gave Z nothing to recall.
                backpack.SetDeployed(InventorySlot.GearClass, true);
                Plugin.Logger.LogInfo($"{logTag}: deployed a bot's tool.");
                return true;
            }
            catch (Exception e)
            {
                skipped.Add($"error: {e.Message}");
                Plugin.Logger.LogWarning($"{logTag}: deploy attempt failed for one bot, trying the next. {e}");
            }
        }

        Plugin.Logger.LogInfo(skipped.Count == 0
            ? $"{logTag}: no bots in the level."
            : $"{logTag}: no bot has an undeployed tool to place. Bots' Tool slots: {string.Join(", ", skipped)}.");
        return false;
    }

    // How many bots still carry a sentry that isn't down - the same test DeployBotAt picks by.
    internal static int CountUndeployedBotSentries()
    {
        var count = 0;
        foreach (var bot in Bots.List())
        {
            var backpack = bot?.Backpack;
            if (backpack == null || backpack.IsDeployed(InventorySlot.GearClass)) continue;
            if (!backpack.TryGetBackpackItem(InventorySlot.GearClass, out var item) || item?.Instance?.TryCast<SentryGunFirstPerson>() == null) continue;
            count++;
        }
        return count;
    }

    // Mirrors exactly what the player's own SentryGunFirstPerson.Update() already computes every
    // frame for their own placement ghost - m_placePos/m_cameraLookRot are the validated spot,
    // m_lastCanPlace is the same green/red check the player sees. No separate raycast of our own.
    internal static bool TryGetAimPose(out Pose pose)
    {
        pose = default;

        var sentry = LocalSentry();
        // Only while it's actually in hand: m_placePos/m_lastCanPlace stop updating once it's put
        // away, so reading them then would reuse wherever it was last aimed.
        if (sentry == null || !sentry.m_lastCanPlace || !IsWielded(InventorySlot.GearClass)) return false;

        pose = new Pose(sentry.m_placePos, sentry.m_cameraLookRot);
        return true;
    }

    internal static SentryGunFirstPerson? LocalSentry()
    {
        var backpack = PlayerBackpackManager.LocalBackpack;
        if (backpack == null) return null;
        if (!backpack.TryGetBackpackItem(InventorySlot.GearClass, out var backpackItem) || backpackItem?.Instance == null) return null;
        return backpackItem.Instance.TryCast<SentryGunFirstPerson>();
    }

    private static bool IsWielded(InventorySlot slot) =>
        PlayerManager.TryGetLocalPlayerAgent(out var agent) && agent?.Inventory != null && agent.Inventory.WieldedSlot == slot;
}

// Places your own sentry without pulling it out first, at a given pose. Goes through the sentry's
// own PlaceOnGround, the same call a normal placement ends in. Used by X (QuickDropAll) with a pose
// from your current position/orientation.
internal static class OwnSentryAutoPlace
{
    private const float StepAhead = 1.5f;
    private const float GroundProbeHeight = 1.5f;
    private const float GroundProbeDepth = 4f;
    // Steeper than this (stairs edges, railings, crate corners) isn't ground a sentry stands on.
    private const float MaxGroundSlope = 30f;
    // Aims the sight check just above the ground so it isn't blocked by the floor it's checking.
    private const float SightLift = 0.2f;

    // Lets X's own PlaceOnGround call past the Shift modifier guard.
    internal static bool Placing;

    // True if your own sentry was placed; false if you don't have one out, it's already placed, or
    // you're downed - these aren't failures, so QuickDropAll logs them itself rather than this.
    internal static bool TryPlaceAt(Pose pose)
    {
        try
        {
            var backpack = PlayerBackpackManager.LocalBackpack;
            var sentry = BotDeployAtAim.LocalSentry();
            if (backpack == null || sentry == null) return false;
            if (backpack.IsDeployed(InventorySlot.GearClass)) return false;

            sentry.m_placePos = pose.position;
            sentry.m_cameraLookRot = pose.rotation;
            sentry.m_lastCanPlace = true;
            Placing = true;
            sentry.PlaceOnGround();
            // PlaceOnGround leaves the yellow ground arrow showing; the vanilla flow only hides it
            // when you put the sentry away, which never happens when it's placed from the backpack.
            sentry.m_placementIndicator?.SetVisible(false);
            return true;
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"X: couldn't place your sentry. {e}");
            return false;
        }
        finally
        {
            Placing = false;
        }
    }

    internal static bool TryGroundAhead(PlayerAgent agent, out Pose pose)
    {
        pose = default;
        if (agent.FPSCamera == null) return false;

        var forward = agent.FPSCamera.FlatForward;
        var origin = agent.transform.position + forward * StepAhead + Vector3.up * GroundProbeHeight;
        if (!Physics.Raycast(origin, Vector3.down, out var hit, GroundProbeDepth, LayerManager.MASK_WORLD)) return false;
        if (Vector3.Angle(hit.normal, Vector3.up) > MaxGroundSlope) return false;

        // The probe starts a step ahead, so on its own it finds floor on the far side of a wall or
        // closed door. The spot only counts if your camera can see it, blocked by the same mask the
        // game's own sentry placement aim uses, plus world geometry.
        var eye = agent.FPSCamera.Position;
        var sightMask = LayerManager.MASK_WORLD | LayerManager.MASK_SENTRYGUN_CAMERARAY;
        if (Physics.Linecast(eye, origin, sightMask)) return false;
        if (Physics.Linecast(eye, hit.point + Vector3.up * SightLift, sightMask)) return false;

        pose = new Pose(hit.point, Quaternion.LookRotation(forward, Vector3.up));
        return true;
    }
}

// X: drops the next turret at wherever you're currently standing and looking - every bot that still
// has one undeployed, one per press, then finally your own - so you can deploy the whole team
// without aiming through anyone's placement ghost (Shift+E and E still do that, for precise spots).
internal static class QuickDropAll
{
    internal static void Activate()
    {
        try
        {
            if (!PlayerManager.TryGetLocalPlayerAgent(out var agent) || agent == null || !agent.Alive)
            {
                Plugin.Logger.LogInfo("X: you can't drop a turret while downed.");
                return;
            }
            if (!OwnSentryAutoPlace.TryGroundAhead(agent, out var pose))
            {
                Plugin.Logger.LogInfo("X: no clear ground in front of you to place on.");
                return;
            }

            // Bots first (host only - they belong to the host), then your own, so repeated presses
            // clear the whole team's turrets in order before cycling back to yours.
            if (SNet.IsMaster && BotDeployAtAim.DeployBotAt(pose, "X")) return;

            if (OwnSentryAutoPlace.TryPlaceAt(pose))
            {
                Plugin.Logger.LogInfo("X: placed your sentry.");
                return;
            }

            Plugin.Logger.LogInfo("X: nothing left to drop - every turret is already placed.");
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"X: couldn't drop a turret. {e}");
        }
    }
}

// Z: instantly recalls every bot-placed tool still down, straight back into the bot that
// placed it, all at once - the reverse of X's bot half. Your own placed tool is recalled too, by
// OwnSentryAutoPickup below - the reverse of X's own half.
internal static class PickupAllBotTools
{
    internal static void Activate()
    {
        List<PlayerAIBot> bots;
        try
        {
            bots = Bots.List();
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"Z: couldn't list bots. {e}");
            return;
        }

        var recalled = 0;
        // Placed sentries are found in the level and matched to their bot through the backpack each
        // one records it belongs to. Neither backpack lookup works for this: the game's
        // GatherDeployables.Descriptor.Evaluate wrapper throws a TypeLoadException on Il2CppSystem
        // List`1, and TryGetDeployedItem finds no replicator for a sentry Shift+E placed (the game
        // logs "Non replicatior found with key 0").
        UnityEngine.Object[] placed;
        try
        {
            placed = UnityEngine.Object.FindObjectsOfType(Il2CppInterop.Runtime.Il2CppType.Of<SentryGunInstance>());
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"Z: couldn't list placed sentries. {e}");
            return;
        }

        foreach (var obj in placed)
        {
            try
            {
                var sentry = obj?.TryCast<SentryGunInstance>();
                var owner = sentry?.m_belongsToBackpack;
                if (sentry == null || owner == null) continue;

                PlayerAIBot? bot = null;
                foreach (var candidate in bots)
                    if (candidate?.Backpack != null && candidate.Backpack.Pointer == owner.Pointer) bot = candidate;
                if (bot == null) continue; // yours, or a teammate's - not a bot's

                var desc = new PlayerBotActionGatherDeployables.Descriptor(bot);
                var action = new PlayerBotActionGatherDeployables(desc) { m_targetItem = sentry };
                action.PickUpItem();
                recalled++;
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning($"Z: couldn't recall a bot's sentry, skipping it. {e}");
            }
        }

        Plugin.Logger.LogInfo($"Z: recalled {recalled} bot sentry(s) of {placed.Length} placed in the level.");
    }
}

// Z (own half): instantly recalls your own placed sentry straight back into your inventory, the
// reverse of X's own half - works for the host and every client, since it's your own item, not a
// bot's. SentryGunInstance.PickUp(PlayerAgent) is the same real pickup call the vanilla walk-up-and-
// interact flow ends in (confirmed public in the interop metadata), called directly here exactly
// like PlaceOnGround already is for placing.
internal static class OwnSentryAutoPickup
{
    internal static void Pickup()
    {
        try
        {
            var backpack = PlayerBackpackManager.LocalBackpack;
            if (backpack == null || !backpack.IsDeployed(InventorySlot.GearClass)) return; // nothing of yours is out

            if (!PlayerManager.TryGetLocalPlayerAgent(out var agent) || agent == null) return;

            SentryGunInstance? placed = null;
            foreach (var obj in UnityEngine.Object.FindObjectsOfType(Il2CppInterop.Runtime.Il2CppType.Of<SentryGunInstance>()))
            {
                var candidate = obj?.TryCast<SentryGunInstance>();
                if (candidate?.m_belongsToBackpack != null && candidate.m_belongsToBackpack.Pointer == backpack.Pointer)
                {
                    placed = candidate;
                    break;
                }
            }
            if (placed == null)
            {
                Plugin.Logger.LogInfo("Z: couldn't find your placed sentry in the level.");
                return;
            }

            placed.PickUp(agent);
            Plugin.Logger.LogInfo("Z: recalled your own sentry.");
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"Z: couldn't recall your own sentry. {e}");
        }
    }
}
