using System;
using System.Collections.Generic;
using HarmonyLib;
using MindAttic.GTFO.Shared;
using Player;
using SNetwork;
using UnityEngine;

namespace GTFO.EZ.Patches;

// G.EZ-T120: which turret each key places now comes from the shared team turret pool
// (TeamTurrets.cs) - host and clients alike, bots' first and your own last, every bot placement
// decided by the host. The history below still describes how the placement calls themselves work.
//
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
[HarmonyBefore("MindAttic.GTFO.EZ.AbsurdItems")] // G.EZ-T119: explicit order on a method AbsurdItems also patches (AGENTS.md golden rule); Before matches its After(GTFO.EZ) convention, so no cycle (cf. G.EZ-T108)
internal static class DeployPickupHotkeys_Update_Patch
{
    [HarmonyPostfix]
    private static void Postfix(Dam_PlayerDamageLocal __instance)
    {
        if (!LocalPlayerDamage.IsMine(__instance)) return;
        if (!Plugin.BotsDeployOnShiftE.Value)
        {
            // G.EZ-T124: a host with the pool off still says so, so clients place their own instead
            // of holding it as "pool unknown".
            try
            {
                TeamTurretNet.TickDisabled();
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning($"Team turrets: tick failed. {e}");
            }
            return;
        }

        // G.EZ-T120: the shared team turret pool - selection, the Tool key's cycling, and the host's
        // pool broadcast (which runs outside FPS focus too, so a host at a terminal still answers).
        try
        {
            TeamTurrets.Tick();
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"Team turrets: tick failed. {e}");
        }

        if (!Hotkeys.InPlay) return;

        // Bots belong to the host: every bot placement below goes through TeamTurrets.PlaceBot, which
        // places directly on the host and only asks the host from a client (G.EZ-T120) - a client never
        // moves its own copy of a bot's turret. The Shift+E guard below means the same thing for
        // everyone: never your own placement.
        if (Hotkeys.ShiftHeld && Input.GetKeyDown(KeyCode.E))
        {
            Hotkeys.ShiftEPressedAt = Time.time;
            TeamTurrets.ShiftE();
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
                // G.EZ-T124: who recalled what, and how many bot turrets are left, goes to every
                // player's event log (from the host, once the recalled turrets are confirmed back).
                var ownToo = OwnSentryAutoPickup.Pickup();
                if (SNet.IsMaster)
                {
                    TeamTurretNet.HostRecall(SNet.LocalPlayer != null ? SNet.LocalPlayer.Lookup : 0UL, ownToo);
                }
                else if (!TeamTurretNet.RequestRecallBots(ownToo) && ownToo) // bots belong to the host - it recalls them
                {
                    MessageHelper.Show("You recalled your own turret", MessageType.EventLog);
                }
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
// G.EZ-T120 (replacing G.EZ-T116's fixed bot order): plain E with your own sentry in hand places
// whichever turret the Tool key has selected from the shared team pool (TeamTurrets). While any bot
// turret is available that's always a bot's - placed at your ghost, through the host, with your own
// kept in hand, ghost and all, for the next press; your own goes down last, and only then does the
// postfix below put slot 4 away and return your previous weapon.
[HarmonyPatch(typeof(SentryGunFirstPerson), nameof(SentryGunFirstPerson.PlaceOnGround))]
internal static class SentryGunFirstPerson_PlaceOnGround_ModifierGuard
{
    // With placement instant, a held E can re-trigger placement on the next frames; the cooldown
    // (TeamSentryRules.TeamPlaceCooldown, from TeamTurrets.LastTeamPlaceAt) keeps one press from
    // dumping every remaining turret (and then your own) on the same spot.
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

        var selectedBot = 0UL;
        var poolUnknown = false;
        if (!shiftE)
        {
            try
            {
                // G.EZ-T124: no fresh word from the host - bot turrets may still be queued, so your
                // own is held (own-last), not placed as if the pool were empty.
                poolUnknown = TeamTurrets.PoolUnknown;
                selectedBot = TeamTurrets.ResolveNow();
            }
            catch (Exception e)
            {
                // Our own failure, not a placement decision: hold your own rather than risk placing it
                // ahead of bot turrets that are still queued.
                Plugin.Logger.LogWarning($"E: couldn't read the team turret pool - placing nothing this press. {e}");
                return false;
            }
        }

        // The decision is GTFO.EZ.Core.TeamSentryRules (unit tested); this only gathers the inputs.
        switch (GTFO.EZ.Core.TeamSentryRules.Decide(shiftE, Time.time - TeamTurrets.LastTeamPlaceAt, selectedBot != 0, poolUnknown))
        {
            case GTFO.EZ.Core.TeamSentryPlacement.HoldOwn:
                if (poolUnknown && pressedNow) TeamTurrets.ShowUnknown();
                return false;
            case GTFO.EZ.Core.TeamSentryPlacement.PlaceBotInstead:
                // The bot's turret goes down (host) or is asked for (client); either way your own
                // stays in hand. A refusal just leaves the selection to move on next frame.
                try
                {
                    TeamTurrets.PlaceBot(selectedBot, new Pose(__instance.m_placePos, __instance.m_cameraLookRot), "E");
                }
                catch (Exception e)
                {
                    Plugin.Logger.LogWarning($"E: couldn't place {TeamTurrets.Label(selectedBot)}. {e}");
                }
                return false;
            default:
                return true;
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

    internal readonly struct BotSentry
    {
        public BotSentry(ulong lookup, bool deployed) { Lookup = lookup; Deployed = deployed; }
        public ulong Lookup { get; }
        public bool Deployed { get; }
    }

    // G.EZ-T120: every bot carrying a Sentry Gun (a mine deployer/C-foam/bio tracker bot isn't part
    // of the turret pool), in level order, with whether it's down. Host-side truth for the pool.
    // Cached per frame - the pool reads it several times a frame.
    private static int _sentriesFrame = -1;
    private static readonly List<BotSentry> _sentries = new();

    internal static List<BotSentry> Sentries()
    {
        if (_sentriesFrame == Time.frameCount) return _sentries;
        _sentriesFrame = Time.frameCount;
        _sentries.Clear();
        foreach (var agent in PlayerManager.PlayerAgentsInLevel)
        {
            try
            {
                if (agent == null || agent.Owner == null || !agent.Owner.IsBot) continue;
                if (!PlayerBackpackManager.TryGetBackpack(agent.Owner, out var backpack) || backpack == null) continue;
                if (!backpack.TryGetBackpackItem(InventorySlot.GearClass, out var item) || item?.Instance?.TryCast<SentryGunFirstPerson>() == null) continue;
                _sentries.Add(new BotSentry(agent.Owner.Lookup, backpack.IsDeployed(InventorySlot.GearClass)));
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning($"Team turrets: couldn't read a bot's Tool slot, skipping it. {e}");
            }
        }
        return _sentries;
    }

    // The bot with this SNet lookup, its backpack and Tool slot item, re-read now (never cached) -
    // what the host's placement gate checks.
    internal static bool Find(ulong lookup, out PlayerAIBot? bot, out PlayerBackpack? backpack, out BackpackItem? item, out bool isSentry)
    {
        bot = null; backpack = null; item = null; isSentry = false;
        foreach (var agent in PlayerManager.PlayerAgentsInLevel)
        {
            if (agent == null || agent.Owner == null || !agent.Owner.IsBot || agent.Owner.Lookup != lookup) continue;
            bot = agent.GetComponent<PlayerAIBot>();
            backpack = bot?.Backpack;
            if (bot == null || backpack == null) return false;
            if (backpack.TryGetBackpackItem(InventorySlot.GearClass, out var found) && found != null)
            {
                item = found;
                isSentry = found.Instance?.TryCast<SentryGunFirstPerson>() != null;
            }
            return true;
        }
        return false;
    }

    internal static string NameOf(ulong lookup) => PlayerName(lookup, "a bot");

    internal static string PlayerName(ulong lookup, string fallback = "a teammate")
    {
        try
        {
            foreach (var agent in PlayerManager.PlayerAgentsInLevel)
                if (agent?.Owner != null && agent.Owner.Lookup == lookup && !string.IsNullOrEmpty(agent.Owner.NickName))
                    return agent.Owner.NickName;
        }
        catch (Exception)
        {
            // Just a label.
        }
        return fallback;
    }
}

internal static class BotDeployAtAim
{
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

            // G.EZ-T120: from the shared team pool - every available bot turret first (placed by the
            // host, asked for from a client), then your own last, so repeated presses clear the team.
            TeamTurrets.QuickDrop(pose);
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
    // The SNet lookups of the bots whose turret was recalled (TeamTurretNet checks they're back in the
    // pool a moment later - EnsureBackInPool - and only then tells the team).
    internal static List<ulong> Activate()
    {
        var recalled = new List<ulong>();
        List<PlayerAIBot> bots;
        try
        {
            bots = Bots.List();
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"Z: couldn't list bots. {e}");
            return recalled;
        }

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
            return recalled;
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
                var lookup = owner.Owner?.Lookup ?? 0UL;
                if (lookup != 0) recalled.Add(lookup);
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning($"Z: couldn't recall a bot's sentry, skipping it. {e}");
            }
        }

        Plugin.Logger.LogInfo($"Z: recalled {recalled.Count} bot sentry(s) of {placed.Length} placed in the level.");
        return recalled;
    }

    // G.EZ-T124: whether the bot AI's PickUpItem clears the backpack's deployed flag isn't something
    // the interop stubs show, and the pool counts a bot whose flag is still set as down - so a recalled
    // turret could stay out of the pool for good. Checked a moment after the recall: if no placed
    // sentry of this bot's is left in the level but its backpack still says deployed, the flag is
    // cleared (TeamTurretPool.ClearDeployedAfterRecall). Never while its turret is still standing -
    // that would let a second one go down. True when the bot's turret is back in the pool.
    internal static bool EnsureBackInPool(ulong lookup, float secondsSinceRecall)
    {
        try
        {
            if (!Bots.Find(lookup, out _, out var backpack, out _, out var isSentry) || backpack == null || !isSentry) return false;
            var deployed = backpack.IsDeployed(InventorySlot.GearClass);
            if (!deployed) return true;

            var standing = false;
            foreach (var obj in UnityEngine.Object.FindObjectsOfType(Il2CppInterop.Runtime.Il2CppType.Of<SentryGunInstance>()))
            {
                var owner = obj?.TryCast<SentryGunInstance>()?.m_belongsToBackpack;
                if (owner != null && owner.Pointer == backpack.Pointer) { standing = true; break; }
            }

            if (GTFO.EZ.Core.TeamTurretPool.ClearDeployedAfterRecall(deployed, standing, secondsSinceRecall))
            {
                backpack.SetDeployed(InventorySlot.GearClass, false);
                Plugin.Logger.LogInfo($"Z: {TeamTurrets.Label(lookup)} was picked up but its backpack still said deployed - cleared, it's back in the pool.");
                return true;
            }
            if (standing) Plugin.Logger.LogWarning($"Z: {TeamTurrets.Label(lookup)} is still standing after the recall - left out of the pool.");
            return false;
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"Z: couldn't check {TeamTurrets.Label(lookup)} came back. {e}");
            return false;
        }
    }
}

// Z (own half): instantly recalls your own placed sentry straight back into your inventory, the
// reverse of X's own half - works for the host and every client, since it's your own item, not a
// bot's. SentryGunInstance.PickUp(PlayerAgent) is the same real pickup call the vanilla walk-up-and-
// interact flow ends in (confirmed public in the interop metadata), called directly here exactly
// like PlaceOnGround already is for placing.
internal static class OwnSentryAutoPickup
{
    // True when your own sentry was picked up (G.EZ-T124: so the team's event log can say so).
    internal static bool Pickup()
    {
        try
        {
            var backpack = PlayerBackpackManager.LocalBackpack;
            if (backpack == null || !backpack.IsDeployed(InventorySlot.GearClass)) return false; // nothing of yours is out

            if (!PlayerManager.TryGetLocalPlayerAgent(out var agent) || agent == null) return false;

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
                return false;
            }

            placed.PickUp(agent);
            Plugin.Logger.LogInfo("Z: recalled your own sentry.");
            return true;
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"Z: couldn't recall your own sentry. {e}");
            return false;
        }
    }
}
