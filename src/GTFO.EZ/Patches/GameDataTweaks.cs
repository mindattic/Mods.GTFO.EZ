using System;
using System.Collections.Generic;
using GameData;
using GTFO.EZ.Core;
using HarmonyLib;
using Player;
using UnityEngine;

namespace GTFO.EZ.Patches;

/// <summary>
/// Applies all stat tweaks as multipliers against whatever GameDataInit just loaded, instead of
/// shipping replacement datablock files. Survives game patches that rebalance base values, and
/// only breaks if Overkill renames/restructures a field this touches.
/// </summary>
[HarmonyPatch(typeof(GameDataInit), nameof(GameDataInit.Initialize))]
internal static class GameDataInit_Initialize_Patch
{
    // G.EZ-T109: GameDataInit has a ReInitialize too. If Initialize runs again over the SAME block
    // objects with the values this postfix already scaled still in them, scaling again would compound
    // every multiplier. The player block (id 1) is the witness: same object and the exact values this
    // postfix left in it means "already applied, not reloaded" and the run is skipped. Fresh block
    // objects, or values the reload put back, are a real reload and get the tweaks once.
    private static IntPtr appliedPlayerBlock;
    private static (float Health, float Regen, float FallMin, float FallMinHeight, float RegenCap, float RunSpeed) appliedPlayerValues;

    private static (float, float, float, float, float, float) PlayerFingerprint(PlayerDataBlock player) =>
        (player.health, player.healthRegenPerSecond, player.fallDamageMin, player.fallDamageMinHeight,
            player.healthRegenRelMax, player.runMoveSpeed);

    private static bool AlreadyApplied()
    {
        var player = GameDataBlockBase<PlayerDataBlock>.GetBlock(1);
        return player != null && appliedPlayerBlock != IntPtr.Zero && player.Pointer == appliedPlayerBlock
            && PlayerFingerprint(player).Equals(appliedPlayerValues);
    }

    private static void RememberApplied()
    {
        var player = GameDataBlockBase<PlayerDataBlock>.GetBlock(1);
        if (player == null) return;
        appliedPlayerBlock = player.Pointer;
        appliedPlayerValues = PlayerFingerprint(player);
    }

    [HarmonyPostfix]
    private static void Postfix()
    {
        try
        {
            if (AlreadyApplied())
            {
                Plugin.Logger.LogInfo("GTFO.EZ: GameDataInit ran again over the already-tweaked datablocks - not scaling them a second time.");
                return;
            }
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"GTFO.EZ couldn't check for an earlier tweak pass, applying: {e.Message}");
        }

        // Each category gets its own try/catch rather than one wrapping the whole method - a
        // future game patch that renames/restructures a single field (the one failure mode this
        // mod can't survive, per the class comment above) would otherwise take down every other,
        // unrelated category too, since the original code bailed out of the entire sequence on
        // the first exception.
        RunTweakCategory("player", ApplyPlayerTweaks);
        RunTweakCategory("flashlight", ApplyFlashlightTweaks);
        RunTweakCategory("detection", ApplyDetectionTweaks);
        RunTweakCategory("enemy balance", ApplyEnemyBalanceTweaks);
        RunTweakCategory("fog", ApplyFogTweaks);
        // G.EZ-T125: the level-shaping categories read LevelSettings.Effective (the host's values in
        // someone else's lobby), and the vanilla fields they touch are snapshotted first so a client
        // can put them back and apply the host's values without compounding.
        RunTweakCategory("level snapshot", () => levelSnapshot = LevelBlockSnapshot.Capture());
        RunLevelCategories();
        RunTweakCategory("melee", ApplyMeleeTweaks);
        RunTweakCategory("weapon", ApplyWeaponTweaks);
        RunTweakCategory("re-apply guard", RememberApplied);
        Plugin.Logger.LogInfo("GTFO.EZ tweaks applied. Goo goo. Gah gah.");
    }

    // ── G.EZ-T125: level settings follow the host ──────────────────────────────────────────────
    // A real tweak pass (not one the re-apply guard skipped) snapshots the vanilla level fields.
    // ReapplyLevelTweaks puts them back and runs only the level categories again with the values
    // wanted now - never the player/weapon ones, and never over already-scaled values. The player
    // block (the G.EZ-T109 witness) isn't touched, so a later GameDataInit re-run is still recognized.
    private static LevelBlockSnapshot? levelSnapshot;

    internal static bool LevelSnapshotTaken => levelSnapshot != null;

    // The LevelSettings signature the level datablocks were last tweaked with.
    internal static ulong AppliedLevelSignature { get; private set; }

    private static void RunLevelCategories()
    {
        var signature = LevelSettings.EffectiveSignature();
        RunTweakCategory("expedition balance", ApplyExpeditionBalanceTweaks);
        RunTweakCategory("artifacts", ApplyArtifactTweaks);
        RunTweakCategory("loot", ApplyLootTweaks);
        AppliedLevelSignature = signature;
    }

    // Everything downstream of these blocks runs again too: GTFO.EZ.AbsurdItems' kit loot refills the
    // consumable tables (and sizes them from the expedition balance budget), then the all-disabled
    // tables are emptied - the same order as at GameDataInit.
    internal static void ReapplyLevelTweaks(string what)
    {
        if (levelSnapshot == null) return;
        try
        {
            levelSnapshot.Restore();
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"GTFO.EZ couldn't restore the vanilla level datablocks - keeping the level tweaks as they are: {e}");
            AppliedLevelSignature = LevelSettings.EffectiveSignature(); // don't retry every second
            return;
        }
        RunLevelCategories();
        RunTweakCategory("GTFO.EZ.AbsurdItems kit loot", RerunAbsurdItemsKitLoot);
        GameDataInit_EmptyDisabledLoot_Patch.Postfix();
        Plugin.Logger.LogInfo($"GTFO.EZ: level tweaks re-applied with {what}.");
    }

    private const string AbsurdItemsGuid = "MindAttic.GTFO.EZ.AbsurdItems";

    // AbsurdItems' kit-loot postfix (ConsumablesAreKits.Apply), found among GameDataInit.Initialize's
    // patches by owner and type - nothing to do when AbsurdItems isn't installed or has no such patch.
    private static void RerunAbsurdItemsKitLoot()
    {
        var info = Harmony.GetPatchInfo(AccessTools.Method(typeof(GameDataInit), nameof(GameDataInit.Initialize)));
        if (info == null) return;
        var ran = false;
        foreach (var patch in info.Postfixes)
        {
            if (patch.owner != AbsurdItemsGuid || patch.PatchMethod.DeclaringType?.Name != "ConsumablesAreKits") continue;
            if (patch.PatchMethod.GetParameters().Length != 0) continue;
            patch.PatchMethod.Invoke(null, null);
            ran = true;
        }
        if (!ran && MindAttic.GTFO.Shared.SharedOwner.IsActive(AbsurdItemsGuid))
            Plugin.Logger.LogWarning("GTFO.EZ couldn't find GTFO.EZ.AbsurdItems' kit loot to run again - this game's consumable tables may not match the host's.");
    }

    private static void RunTweakCategory(string name, Action apply)
    {
        try
        {
            apply();
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"GTFO.EZ failed to apply {name} tweaks: {e}");
        }
    }

    // Player datablock is hardcoded to id 1 in GTFO.
    private static void ApplyPlayerTweaks()
    {
        var player = GameDataBlockBase<PlayerDataBlock>.GetBlock(1);
        if (player == null)
        {
            Plugin.Logger.LogWarning("PlayerDataBlock (id 1) not found, skipping player tweaks.");
            return;
        }

        var healthMult = Plugin.HealthMultiplier.Value;
        player.health *= healthMult;

        // Rate and cap are deliberately separate multipliers: vanilla under-regens relative to
        // its own cap, so hitting both target numbers (0.1/s against a 40% cap) needs different
        // factors. Regen delay after damage is left untouched on purpose - see config comment.
        player.healthRegenPerSecond *= Plugin.HealthRegenRateMultiplier.Value;
        player.healthRegenRelMax = Math.Min(1f, player.healthRegenRelMax * Plugin.HealthRegenCapMultiplier.Value);

        // Equalize rather than multiply: vanilla's "in combat" rates are approximate research
        // figures, but copying the known-good "out of combat" rates is exact regardless of the
        // real vanilla numbers and stays correct if Overkill rebalances them later.
        //
        // The "NotResting" pair was the original target here, but vanilla has both of those at
        // 0 regardless of combat state (stamina only regens while standing still/"resting"), so
        // equalizing them was a no-op - this feature shipped doing nothing. The real combat
        // penalty is twofold: a lower regen rate while resting in combat (0.15/s vs 0.25/s out
        // of combat), and a hard 90% cap on stamina while in combat. Both are fixed here now.
        if (Plugin.RemoveCombatStaminaPenalty.Value)
        {
            player.StaminaRegenNotRestingInCombat = player.StaminaRegenNotRestingOutOfCombat;
            player.StaminaRegenRestingInCombat = player.StaminaRegenRestingOutOfCombat;
            player.StaminaMaximumCapWhenInCombat = 1f;
        }

        // Stamina in GTFO is a 0-1 meter with no maximum to raise, so "more stamina" divides what
        // every action costs and speeds regen up by the same factor (moved over from
        // GTFO.EZ.AbsurdMoves' Run Speed/Stamina feature, G.EZ-T97).
        var staminaMult = Plugin.StaminaMultiplier.Value;
        if (staminaMult > 0f)
        {
            player.StaminaJumpCost = Cheaper(player.StaminaJumpCost, staminaMult);
            player.StaminaSneakCost = Cheaper(player.StaminaSneakCost, staminaMult);
            player.StaminaWalkCost = Cheaper(player.StaminaWalkCost, staminaMult);
            player.StaminaRunCost = Cheaper(player.StaminaRunCost, staminaMult);
            player.StaminaCrouchEnterCost = Cheaper(player.StaminaCrouchEnterCost, staminaMult);
            player.StaminaQuickStrafeCost = Cheaper(player.StaminaQuickStrafeCost, staminaMult);
            player.StaminaRegenRestingInCombat *= staminaMult;
            player.StaminaRegenNotRestingInCombat *= staminaMult;
            player.StaminaRegenRestingOutOfCombat *= staminaMult;
            player.StaminaRegenNotRestingOutOfCombat *= staminaMult;
        }

        var fallDamageMult = Plugin.FallDamageMultiplier.Value;
        player.fallDamageMin *= fallDamageMult;
        player.fallDamageMax *= fallDamageMult;

        player.fallDamageMinHeight *= Plugin.FallDamageMinHeightMultiplier.Value;
        player.fallDamageMaxHeight *= Plugin.FallDamageMaxHeightMultiplier.Value;

        var ammoMult = Plugin.AmmoMultiplier.Value;
        player.AmmoStandardInitial = ScaleUp(player.AmmoStandardInitial, ammoMult);
        player.AmmoStandardMaxCap = ScaleUp(player.AmmoStandardMaxCap, ammoMult);
        player.AmmoStandardResourcePackMaxCap = ScaleUp(player.AmmoStandardResourcePackMaxCap, ammoMult);
        player.AmmoSpecialInitial = ScaleUp(player.AmmoSpecialInitial, ammoMult);
        player.AmmoSpecialMaxCap = ScaleUp(player.AmmoSpecialMaxCap, ammoMult);
        player.AmmoSpecialResourcePackMaxCap = ScaleUp(player.AmmoSpecialResourcePackMaxCap, ammoMult);
        player.AmmoClassInitial = ScaleUp(player.AmmoClassInitial, ammoMult);
        player.AmmoClassMaxCap = ScaleUp(player.AmmoClassMaxCap, ammoMult);
        player.AmmoClassResourcePackMaxCap = ScaleUp(player.AmmoClassResourcePackMaxCap, ammoMult);

        player.walkMoveSpeed *= Plugin.WalkSpeedMultiplier.Value;
        player.runMoveSpeed *= Plugin.RunSpeedMultiplier.Value;
        player.airMoveSpeed *= Plugin.AirSpeedMultiplier.Value;
        player.crouchMoveSpeed *= Plugin.CrouchSpeedMultiplier.Value;
        player.ladderMoveSpeed *= Plugin.LadderSpeedMultiplier.Value;
        player.jumpVelInitial *= Plugin.JumpHeightMultiplier.Value;

        player.friendlyFireMulti *= Plugin.FriendlyFireMultiplier.Value;

        player.battery = ScaleUp(player.battery, Plugin.BatteryCapacityMultiplier.Value);
        player.smallBatteryConsumtionPerSec *= Plugin.SmallBatteryDrainMultiplier.Value;
        player.mediumBatteryConsumtionPerSec *= Plugin.MediumBatteryDrainMultiplier.Value;
        player.largeBatteryConsumtionPerSec *= Plugin.LargeBatteryDrainMultiplier.Value;

        player.noAirDamageRel *= Plugin.NoAirDamageMultiplier.Value;
        player.noAirTimeToEmpty *= Plugin.NoAirDepletionTimeMultiplier.Value;
    }

    // Covers both headlamps and weapon-mounted lights: both reference the same datablock type.
    // Angle + intensity only, not range - a longer beam reaches further into the dark than the
    // level's lighting was designed for, while angle/intensity just make the existing cone better.
    private static void ApplyFlashlightTweaks()
    {
        var angleMult = Plugin.FlashlightAngleMultiplier.Value;
        var intensityMult = Plugin.FlashlightIntensityMultiplier.Value;

        foreach (var block in GameDataBlockBase<FlashlightSettingsDataBlock>.GetAllBlocks())
        {
            block.angle *= angleMult;
            block.intensity *= intensityMult;
        }
    }

    // Multiple detection profiles exist (e.g. normal enemies vs. sleepers); scale all of them.
    private static void ApplyDetectionTweaks()
    {
        var mult = Plugin.DetectionDistanceMultiplier.Value;

        foreach (var block in GameDataBlockBase<EnemyDetectionDataBlock>.GetAllBlocks())
        {
            block.movementDetectionDistance *= mult;
        }
    }

    // One EnemyBalancingDataBlock per enemy type. Health is a nested struct, so it's copied out,
    // modified, and written back rather than mutated in place.
    private static void ApplyEnemyBalanceTweaks()
    {
        var healthMult = Plugin.EnemyHealthMultiplier.Value;
        var meleeMult = Plugin.EnemyMeleeDamageMultiplier.Value;
        var tentacleMult = Plugin.EnemyTentacleDamageMultiplier.Value;

        foreach (var block in GameDataBlockBase<EnemyBalancingDataBlock>.GetAllBlocks())
        {
            var health = block.Health;
            health.HealthMax *= healthMult;
            block.Health = health;

            block.MeleeAttackDamage *= meleeMult;
            block.TentacleAttackDamage *= tentacleMult;
        }
    }

    // FogSettingsDataBlock entries are shared across rundowns/dimensions, so this reaches
    // visibility fog everywhere it's used. A density multiplier used to live here, but scaling
    // FogDensity down (even to 0.1) produced no visible change in-game - whatever drives the
    // actual look of fog isn't gated purely on that field - so this now turns the fog off outright
    // via its Enabled flag, which every other code path has to respect, and zeroes density too as
    // a second line of defense.
    private static void ApplyFogTweaks()
    {
        if (!Plugin.DisableFog.Value) return;

        foreach (var block in GameDataBlockBase<FogSettingsDataBlock>.GetAllBlocks())
        {
            block.Enabled = false;
            block.FogDensity = 0f;
        }
    }

    // Consumables (what boxes and lockers hold, and what lies around a zone) and big pickups (fog
    // turbines, artifacts) are separate distribution datablocks, each with its own spawns-per-zone
    // count. Med/ammo/tool kits from the zone's resource budget are a separate system again and are
    // never touched here. Kits as consumable loot live in GTFO.EZ.AbsurdItems now; it runs after this.
    private static void ApplyLootTweaks()
    {
        var consumableMult = LevelSettings.Effective(Plugin.ConsumableSpawnMultiplier);
        foreach (var block in GameDataBlockBase<ConsumableDistributionDataBlock>.GetAllBlocks())
            block.SpawnsPerZone = ScaleUp(block.SpawnsPerZone, consumableMult);

        // Each consumable is drawn from a shared weighted lottery per spawn slot, so scaling an
        // item's weight retunes the odds between items rather than the total amount of loot (that's
        // the SpawnsPerZone scale above). Both glow stick colors share one entry. Always applied: a
        // GTFO.EZ.AbsurdItems kit run replaces the items (and these weights with them) after this, and
        // when it doesn't fill the tables (switched off, no kit items found, an error) these stand.
        // A table left with every item disabled is emptied later, once that run is done (see
        // GameDataInit_EmptyDisabledLoot_Patch) - emptying it here would leave it empty under kits too.
        foreach (var block in GameDataBlockBase<ConsumableDistributionDataBlock>.GetAllBlocks())
        {
            if (block.SpawnData == null || block.SpawnData.Count == 0) continue;
            foreach (var entry in block.SpawnData)
                if (ConsumableWeightMultipliers.TryGetValue(entry.ItemID, out var getMultiplier)) entry.Weight *= getMultiplier();
        }

        var bigPickupMult = LevelSettings.Effective(Plugin.BigPickupSpawnMultiplier);
        foreach (var block in GameDataBlockBase<BigPickupDistributionDataBlock>.GetAllBlocks())
        {
            block.SpawnsPerZone = ScaleUp(block.SpawnsPerZone, bigPickupMult);
        }
    }

    // ItemIDs confirmed by logging GameDataBlockBase<ItemDataBlock> entries referenced from
    // ConsumableDistributionDataBlock.SpawnData at runtime - not documented anywhere official.
    private static readonly Dictionary<uint, Func<float>> ConsumableWeightMultipliers = new()
    {
        { 30, () => LevelSettings.Effective(Plugin.FlashlightItemSpawnWeightMultiplier) },   // Long Range Flashlight
        { 114, () => LevelSettings.Effective(Plugin.GlowstickSpawnWeightMultiplier) },        // Glow Stick
        { 174, () => LevelSettings.Effective(Plugin.GlowstickSpawnWeightMultiplier) },        // Glow Stick Yellow
        { 115, () => LevelSettings.Effective(Plugin.CFoamGrenadeSpawnWeightMultiplier) },     // C-Foam Grenade
        { 117, () => LevelSettings.Effective(Plugin.FogRepellerSpawnWeightMultiplier) },      // Fog Repeller
        { 116, () => LevelSettings.Effective(Plugin.LockMelterSpawnWeightMultiplier) },       // Lock Melter
        { 139, () => LevelSettings.Effective(Plugin.ExplosiveTripMineSpawnWeightMultiplier) }, // Explosive Trip Mine
        { 144, () => LevelSettings.Effective(Plugin.CFoamTripmineSpawnWeightMultiplier) },    // C-Foam Tripmine
    };

    // ExpeditionBalanceDataBlock is the per-difficulty-tier resource/enemy/door budget table -
    // one block per tier (Easy/Normal/Hard/...), so every tier gets scaled by the same factor.
    // G.EZ-T121: artifact amount and color odds. Per-layer color weights live on
    // ArtifactDistributionDataBlock (Basic = Muted/blue, Specialized = Bold/green, Advanced =
    // Aggressive/red); each expedition layer can also override them per zone
    // (ArtifactLayerData.ArtifactZoneDistributions) and scales the amount (ArtifactAmountMulti).
    // How many artifacts can spawn per segment/layer is ApplyExpeditionBalanceTweaks'.
    private static void ApplyArtifactTweaks()
    {
        var even = LevelSettings.Effective(Plugin.ArtifactEvenColors);
        var mutedMult = LevelSettings.Effective(Plugin.ArtifactMutedWeightMultiplier);
        var boldMult = LevelSettings.Effective(Plugin.ArtifactBoldWeightMultiplier);
        var aggressiveMult = LevelSettings.Effective(Plugin.ArtifactAggressiveWeightMultiplier);

        foreach (var block in GameDataBlockBase<ArtifactDistributionDataBlock>.GetAllBlocks())
        {
            (block.BasicWeight, block.SpecializedWeight, block.AdvancedWeight) = ArtifactOdds.Rebalance(
                block.BasicWeight, block.SpecializedWeight, block.AdvancedWeight, even, mutedMult, boldMult, aggressiveMult);
        }

        var amountMult = LevelSettings.Effective(Plugin.ArtifactAmountMultiplier);
        foreach (var rundown in GameDataBlockBase<RundownDataBlock>.GetAllBlocks())
        {
            foreach (var tier in new[] { rundown.TierA, rundown.TierB, rundown.TierC, rundown.TierD, rundown.TierE })
            {
                if (tier == null) continue;
                foreach (var expedition in tier)
                {
                    if (expedition == null) continue;
                    foreach (var layer in new[] { expedition.MainLayerData, expedition.SecondaryLayerData, expedition.ThirdLayerData })
                    {
                        var artifacts = layer?.ArtifactData;
                        if (artifacts == null) continue;
                        artifacts.ArtifactAmountMulti *= amountMult;
                        if (artifacts.ArtifactZoneDistributions == null) continue;
                        foreach (var zone in artifacts.ArtifactZoneDistributions)
                        {
                            if (zone == null) continue;
                            (zone.BasicArtifactWeight, zone.SpecializedArtifactWeight, zone.AdvancedArtifactWeight) = ArtifactOdds.Rebalance(
                                zone.BasicArtifactWeight, zone.SpecializedArtifactWeight, zone.AdvancedArtifactWeight,
                                even, mutedMult, boldMult, aggressiveMult);
                        }
                    }
                }
            }
        }
    }

    private static void ApplyExpeditionBalanceTweaks()
    {
        foreach (var block in GameDataBlockBase<ExpeditionBalanceDataBlock>.GetAllBlocks())
        {
            block.HealthPerZone *= LevelSettings.Effective(Plugin.HealthPackResourceMultiplier);
            block.DisinfectionPerZone *= LevelSettings.Effective(Plugin.DisinfectionResourceMultiplier);
            block.WeaponAmmoPerZone *= LevelSettings.Effective(Plugin.WeaponAmmoResourceMultiplier);
            block.ToolAmmoPerZone *= LevelSettings.Effective(Plugin.ToolAmmoResourceMultiplier);
            block.CommodityValuePerZone *= LevelSettings.Effective(Plugin.CommodityValueMultiplier);
            block.ChanceToPutCommodityInResourceContainer *= LevelSettings.Effective(Plugin.CommodityInContainerChanceMultiplier);
            block.ChanceToPutArtifactInResourceContainer *= LevelSettings.Effective(Plugin.ArtifactInContainerChanceMultiplier);
            block.ChanceToSpawnCommodityLargePack *= LevelSettings.Effective(Plugin.LargeCommodityPackChanceMultiplier);
            block.ChanceToSpawnCommodityMediumPack *= LevelSettings.Effective(Plugin.MediumCommodityPackChanceMultiplier);
            block.ChanceToReUseResourceContainer *= LevelSettings.Effective(Plugin.ResourceContainerReuseChanceMultiplier);
            block.MaxPacksPerResourceContainer = ScaleUp(block.MaxPacksPerResourceContainer, LevelSettings.Effective(Plugin.MaxPacksPerContainerMultiplier));
            block.EmptyWeakResourceContainersPerZone *= LevelSettings.Effective(Plugin.EmptyWeakContainersPerZoneMultiplier);
            block.EmptySecureResourceContainersPerZone *= LevelSettings.Effective(Plugin.EmptySecureContainersPerZoneMultiplier);
            block.LootPerZone *= LevelSettings.Effective(Plugin.LootPerZoneMultiplier);
            block.AirPerZone *= LevelSettings.Effective(Plugin.AirPerZoneMultiplier);
            block.AirPerZoneInNoAir *= LevelSettings.Effective(Plugin.AirPerZoneInNoAirZoneMultiplier);
            block.TerminalsPerZone *= LevelSettings.Effective(Plugin.TerminalsPerZoneMultiplier);
            block.WeakResourceContainerWithPackChanceForLocked *= LevelSettings.Effective(Plugin.LockedWeakContainerWithPackChanceMultiplier);

            var packSizeMult = LevelSettings.Effective(Plugin.ResourcePackSizeMultiplier);
            for (var i = 0; i < block.ResourcePackSizes.Count; i++)
            {
                block.ResourcePackSizes[i] *= packSizeMult;
            }

            block.EnemyPatrolGroupsPerZone = ScaleDown(block.EnemyPatrolGroupsPerZone, LevelSettings.Effective(Plugin.EnemyPatrolGroupsPerZoneMultiplier));
            block.StaticEnemiesMaxPerZone = ScaleDown(block.StaticEnemiesMaxPerZone, LevelSettings.Effective(Plugin.StaticEnemiesMaxPerZoneMultiplier));
            block.StaticEnemiesMaxSmallArea = ScaleDown(block.StaticEnemiesMaxSmallArea, LevelSettings.Effective(Plugin.StaticEnemiesMaxSmallAreaMultiplier));
            block.StaticEnemiesMaxMediumArea = ScaleDown(block.StaticEnemiesMaxMediumArea, LevelSettings.Effective(Plugin.StaticEnemiesMaxMediumAreaMultiplier));
            block.StaticEnemiesMaxLargeArea = ScaleDown(block.StaticEnemiesMaxLargeArea, LevelSettings.Effective(Plugin.StaticEnemiesMaxLargeAreaMultiplier));
            block.StaticEnemiesMaxHugeArea = ScaleDown(block.StaticEnemiesMaxHugeArea, LevelSettings.Effective(Plugin.StaticEnemiesMaxHugeAreaMultiplier));
            block.EnemyPopulationPerZone *= LevelSettings.Effective(Plugin.EnemyPopulationPerZoneMultiplier);

            block.VoxelCoverageAreaMultiplier *= LevelSettings.Effective(Plugin.LevelGenVoxelCoverageMultiplier);
            block.VoxelCoverageAreaScoringRandomMultiplier *= LevelSettings.Effective(Plugin.LevelGenVoxelCoverageRandomnessMultiplier);
            block.ArtifactsPerSegment = ScaleUp(block.ArtifactsPerSegment, LevelSettings.Effective(Plugin.ArtifactsPerSegmentMultiplier));
            block.ArtifactsPerLayer = ScaleUp(block.ArtifactsPerLayer, LevelSettings.Effective(Plugin.ArtifactsPerLayerMultiplier));

            block.WeakDoor4x4Health *= LevelSettings.Effective(Plugin.WeakDoor4x4HealthMultiplier);
            block.WeakDoor8x4Health *= LevelSettings.Effective(Plugin.WeakDoor8x4HealthMultiplier);
            block.WeakDoorChanceLockWeightNoLock *= LevelSettings.Effective(Plugin.WeakDoorNoLockWeightMultiplier);
            block.WeakDoorChanceLockWeightMeleeLock *= LevelSettings.Effective(Plugin.WeakDoorMeleeLockWeightMultiplier);
            block.WeakDoorChanceLockWeightHackableLock *= LevelSettings.Effective(Plugin.WeakDoorHackableLockWeightMultiplier);
            block.WeakDoorUnlockedChanceForOpen *= LevelSettings.Effective(Plugin.WeakDoorUnlockedOpenChanceMultiplier);
            block.WeakDoorOpenChanceForWallRemoverUsed *= LevelSettings.Effective(Plugin.WeakDoorWallRemoverOpenChanceMultiplier);
            block.WeakDoorLockHealth *= LevelSettings.Effective(Plugin.WeakDoorLockHealthMultiplier);
            block.GlueVolumeToDoorHealthConversion *= LevelSettings.Effective(Plugin.GlueVolumeToDoorHealthConversionMultiplier);
            block.GlueVolumeForDoorGlueMaxState *= LevelSettings.Effective(Plugin.GlueVolumeForDoorMaxStateMultiplier);

            ApplyStaticEnemyData(block.TentacleTraps,
                LevelSettings.Effective(Plugin.TentacleTrapHealthMultiplier),
                LevelSettings.Effective(Plugin.TentacleTrapAttackDamageMultiplier),
                LevelSettings.Effective(Plugin.TentacleTrapMaxPerZoneMultiplier),
                LevelSettings.Effective(Plugin.TentacleTrapMaxSmallAreaMultiplier),
                LevelSettings.Effective(Plugin.TentacleTrapMaxMediumAreaMultiplier),
                LevelSettings.Effective(Plugin.TentacleTrapMaxLargeAreaMultiplier),
                LevelSettings.Effective(Plugin.TentacleTrapMaxHugeAreaMultiplier));

            ApplyStaticEnemyData(block.ParasiteNests,
                LevelSettings.Effective(Plugin.ParasiteNestHealthMultiplier),
                LevelSettings.Effective(Plugin.ParasiteNestAttackDamageMultiplier),
                LevelSettings.Effective(Plugin.ParasiteNestMaxPerZoneMultiplier),
                LevelSettings.Effective(Plugin.ParasiteNestMaxSmallAreaMultiplier),
                LevelSettings.Effective(Plugin.ParasiteNestMaxMediumAreaMultiplier),
                LevelSettings.Effective(Plugin.ParasiteNestMaxLargeAreaMultiplier),
                LevelSettings.Effective(Plugin.ParasiteNestMaxHugeAreaMultiplier));
        }
    }

    // TentacleTraps/ParasiteNests are each a StaticEnemyData reference, not every tier has one
    // (e.g. tiers with no tentacle traps leave the property null), so this is a reference type
    // mutated in place rather than a struct copied out and written back like EnemyBalancingDataBlock.Health.
    private static void ApplyStaticEnemyData(
        StaticEnemyData? data, float healthMult, float attackDamageMult, float maxPerZoneMult,
        float maxSmallAreaMult, float maxMediumAreaMult, float maxLargeAreaMult, float maxHugeAreaMult)
    {
        if (data == null) return;

        data.Health *= healthMult;
        data.AttackDamage *= attackDamageMult;
        data.MaxPerZone = ScaleDown(data.MaxPerZone, maxPerZoneMult);
        data.MaxSmallArea = ScaleDown(data.MaxSmallArea, maxSmallAreaMult);
        data.MaxMediumArea = ScaleDown(data.MaxMediumArea, maxMediumAreaMult);
        data.MaxLargeArea = ScaleDown(data.MaxLargeArea, maxLargeAreaMult);
        data.MaxHugeArea = ScaleDown(data.MaxHugeArea, maxHugeAreaMult);
    }

    // One MeleeArchetypeDataBlock per weapon type - real PublicName values are Bat, Hammer,
    // Knife, Spear. Looked up by "{PublicName}.{FieldKey}" against the dictionary Plugin builds
    // from MeleeStatDefs x MeleeArchetypes, rather than 76 named fields. An archetype the game
    // adds later (unknown PublicName) is silently left at vanilla rather than throwing.
    // Stamina costs are ActionCost structs, copied out and written back like
    // EnemyBalancingDataBlock.Health. PublicName, NoiseLevel, the animation/SFX set IDs, and the
    // boolean behavior flags (SkipLimbDestruction, CanHitMultipleEnemies, etc.) aren't exposed -
    // none of them are multiplier-friendly balance values.
    private static void ApplyMeleeTweaks()
    {
        foreach (var block in GameDataBlockBase<MeleeArchetypeDataBlock>.GetAllBlocks())
        {
            var name = block.PublicName;

            block.LightAttackDamage *= MeleeMult(name, "LightAttackDamage");
            block.ChargedAttackDamage *= MeleeMult(name, "ChargedAttackDamage");
            block.LightStaggerMulti *= MeleeMult(name, "LightStaggerMulti");
            block.ChargedStaggerMulti *= MeleeMult(name, "ChargedStaggerMulti");
            block.LightPrecisionMulti *= MeleeMult(name, "LightPrecisionMulti");
            block.ChargedPrecisionMulti *= MeleeMult(name, "ChargedPrecisionMulti");
            block.LightEnvironmentMulti *= MeleeMult(name, "LightEnvironmentMulti");
            block.ChargedEnvironmentMulti *= MeleeMult(name, "ChargedEnvironmentMulti");
            block.LightBackstabberMulti *= MeleeMult(name, "LightBackstabberMulti");
            block.ChargedBackstabberMulti *= MeleeMult(name, "ChargedBackstabberMulti");
            block.LightSleeperMulti *= MeleeMult(name, "LightSleeperMulti");
            block.ChargedSleeperMulti *= MeleeMult(name, "ChargedSleeperMulti");
            block.CameraDamageRayLength *= MeleeMult(name, "CameraDamageRayLength");
            block.AttackSphereRadius *= MeleeMult(name, "AttackSphereRadius");
            block.PushDamageSphereRadius *= MeleeMult(name, "PushDamageSphereRadius");
            block.PlayerRunSpeedMultiWhileCharging *= MeleeMult(name, "PlayerRunSpeedMultiWhileCharging");

            var lightMult = MeleeMult(name, "LightAttackStaminaCost");
            var lightCost = block.LightAttackStaminaCost;
            lightCost.baseStaminaCostInCombat *= lightMult;
            lightCost.baseStaminaCostOutOfCombat *= lightMult;
            block.LightAttackStaminaCost = lightCost;

            var chargedMult = MeleeMult(name, "ChargedAttackStaminaCost");
            var chargedCost = block.ChargedAttackStaminaCost;
            chargedCost.baseStaminaCostInCombat *= chargedMult;
            chargedCost.baseStaminaCostOutOfCombat *= chargedMult;
            block.ChargedAttackStaminaCost = chargedCost;

            var pushMult = MeleeMult(name, "PushStaminaCost");
            var pushCost = block.PushStaminaCost;
            pushCost.baseStaminaCostInCombat *= pushMult;
            pushCost.baseStaminaCostOutOfCombat *= pushMult;
            block.PushStaminaCost = pushCost;
        }
    }

    private static float MeleeMult(string archetypeName, string fieldKey) =>
        Plugin.MeleeMultipliers.TryGetValue($"{archetypeName}.{fieldKey}", out var entry) ? entry.Value : 1f;

    // ArchetypeDataBlock covers ranged weapons and sentries alike. Core combat stats get the
    // type x individual treatment for every archetype this mod found a GearCategory owner for
    // (see Plugin.WeaponArchetypes); everything else - including sentries, which don't route
    // through GearCategoryDataBlock at all - gets one flat multiplier applied to every archetype.
    private static readonly Dictionary<uint, (string Category, string Label)> WeaponArchetypeOwners = BuildWeaponArchetypeOwners();

    private static Dictionary<uint, (string Category, string Label)> BuildWeaponArchetypeOwners()
    {
        var map = new Dictionary<uint, (string Category, string Label)>();
        foreach (var weapon in BalanceTables.WeaponArchetypes)
        {
            map[weapon.ArchetypeId] = (weapon.TypeCategory, weapon.Label);
        }
        return map;
    }

    private static void ApplyWeaponTweaks()
    {
        foreach (var block in GameDataBlockBase<ArchetypeDataBlock>.GetAllBlocks())
        {
            if (WeaponArchetypeOwners.TryGetValue(block.persistentID, out var owner))
            {
                block.Damage *= WeaponMult(block.persistentID, owner.Category, "Damage");
                block.StaggerDamageMulti *= WeaponMult(block.persistentID, owner.Category, "StaggerDamageMulti");
                block.PrecisionDamageMulti *= WeaponMult(block.persistentID, owner.Category, "PrecisionDamageMulti");
                block.DefaultClipSize = ScaleUp(block.DefaultClipSize, WeaponMult(block.persistentID, owner.Category, "DefaultClipSize"));
                block.DefaultReloadTime *= WeaponMult(block.persistentID, owner.Category, "DefaultReloadTime");
            }

            block.CostOfBullet *= Plugin.WeaponCostOfBulletMultiplier.Value;
            block.ShotDelay *= Plugin.WeaponShotDelayMultiplier.Value;
            block.HipFireSpread *= Plugin.WeaponHipFireSpreadMultiplier.Value;
            block.AimSpread *= Plugin.WeaponAimSpreadMultiplier.Value;
            block.PiercingDamageCountLimit = ScaleUp(block.PiercingDamageCountLimit, Plugin.WeaponPiercingDamageCountLimitMultiplier.Value);
            block.BurstDelay *= Plugin.WeaponBurstDelayMultiplier.Value;
            block.BurstShotCount = ScaleUp(block.BurstShotCount, Plugin.WeaponBurstShotCountMultiplier.Value);
            block.ShotgunBulletCount = ScaleUp(block.ShotgunBulletCount, Plugin.WeaponShotgunBulletCountMultiplier.Value);
            block.ShotgunConeSize = ScaleDown(block.ShotgunConeSize, Plugin.WeaponShotgunConeSizeMultiplier.Value);
            block.ShotgunBulletSpread = ScaleDown(block.ShotgunBulletSpread, Plugin.WeaponShotgunBulletSpreadMultiplier.Value);
            block.SpecialChargetupTime *= Plugin.WeaponSpecialChargetupTimeMultiplier.Value;
            block.SpecialCooldownTime *= Plugin.WeaponSpecialCooldownTimeMultiplier.Value;
            block.SpecialSemiBurstCountTimeout *= Plugin.WeaponSpecialSemiBurstCountTimeoutMultiplier.Value;
            block.EquipTransitionTime *= Plugin.WeaponEquipTransitionTimeMultiplier.Value;
            block.AimTransitionTime *= Plugin.WeaponAimTransitionTimeMultiplier.Value;
            block.ShellCasingSize *= Plugin.WeaponShellCasingSizeMultiplier.Value;

            block.Sentry_StartFireDelay *= Plugin.SentryStartFireDelayMultiplier.Value;
            block.Sentry_RotationSpeed *= Plugin.SentryRotationSpeedMultiplier.Value;
            block.Sentry_DetectionMaxRange *= Plugin.SentryDetectionMaxRangeMultiplier.Value;
            block.Sentry_DetectionMaxAngle *= Plugin.SentryDetectionMaxAngleMultiplier.Value;
            block.Sentry_LongRangeThreshold *= Plugin.SentryLongRangeThresholdMultiplier.Value;
            block.Sentry_ShortRangeThreshold *= Plugin.SentryShortRangeThresholdMultiplier.Value;
            block.Sentry_StartFireDelayTagMulti *= Plugin.SentryStartFireDelayTagMultiplier.Value;
            block.Sentry_RotationSpeedTagMulti *= Plugin.SentryRotationSpeedTagMultiplier.Value;
            block.Sentry_DamageTagMulti *= Plugin.SentryDamageTagMultiplier.Value;
            block.Sentry_StaggerDamageTagMulti *= Plugin.SentryStaggerDamageTagMultiplier.Value;
            block.Sentry_CostOfBulletTagMulti *= Plugin.SentryCostOfBulletTagMultiplier.Value;
            block.Sentry_ShotDelayTagMulti *= Plugin.SentryShotDelayTagMultiplier.Value;
        }
    }

    private static float WeaponMult(uint archetypeId, string category, string fieldKey)
    {
        var typeMult = Plugin.WeaponTypeMultipliers.TryGetValue($"{category}.{fieldKey}", out var t) ? t.Value : 1f;
        var individualMult = Plugin.WeaponIndividualMultipliers.TryGetValue($"{archetypeId}.{fieldKey}", out var i) ? i.Value : 1f;
        return typeMult * individualMult;
    }

    private static PlayerStamina.ActionCost Cheaper(PlayerStamina.ActionCost cost, float mult)
    {
        cost.baseStaminaCostInCombat /= mult;
        cost.baseStaminaCostOutOfCombat /= mult;
        return cost;
    }

    // More of this stat helps the player, so a fractional result rounds up.
    private static int ScaleUp(int value, float multiplier) => BalanceMath.ScaleIntUp(value, multiplier);

    // Less of this stat helps the player (enemy counts, spread), so a fractional result rounds down.
    private static int ScaleDown(int value, float multiplier) => BalanceMath.ScaleIntDown(value, multiplier);
}

// A consumable table where this mod's spawn odds disabled every item spawns nothing rather than run
// an all-zero lottery. Runs after every other GTFO.* mod's GameDataInit work (GTFO.EZ.AbsurdItems' kit
// loot replaces the items, and with them these weights): only a table that still has nothing left
// to pick is emptied, so it's right whether or not that run filled the tables.
//
// Patched under its own Harmony ID (Apply, from Plugin.Load) rather than by PatchAll: AbsurdItems'
// kit postfixes are [HarmonyAfter("MindAttic.GTFO.EZ")], which covers every patch under that ID -
// this one included - so under it the two "after"s formed a cycle, and Harmony breaks a cycle by
// dropping the lowest-priority patch's dependency, i.e. this one's. It then ran before the kit fill,
// zeroing tables the kits were about to fill (KitLoot scales SpawnsPerZone from what it finds).
internal static class GameDataInit_EmptyDisabledLoot_Patch
{
    private const string HarmonyId = Plugin.GUID + ".EmptyDisabledLoot";

    internal static void Apply()
    {
        var postfix = new HarmonyMethod(AccessTools.Method(typeof(GameDataInit_EmptyDisabledLoot_Patch), nameof(Postfix)))
        {
            priority = Priority.Last,
            after = new[] { "MindAttic.GTFO.EZ.AbsurdItems" },
        };
        new Harmony(HarmonyId).Patch(AccessTools.Method(typeof(GameDataInit), nameof(GameDataInit.Initialize)), postfix: postfix);
    }

    internal static void Postfix()
    {
        try
        {
            var emptied = 0;
            foreach (var block in GameDataBlockBase<ConsumableDistributionDataBlock>.GetAllBlocks())
            {
                if (block?.SpawnData == null || block.SpawnData.Count == 0 || block.SpawnsPerZone == 0) continue;
                var totalWeight = 0f;
                foreach (var entry in block.SpawnData) totalWeight += entry.Weight;
                if (totalWeight > 0f) continue;
                block.SpawnsPerZone = 0;
                emptied++;
            }
            if (emptied > 0) Plugin.Logger.LogInfo($"Loot: {emptied} consumable table(s) with every item disabled spawn nothing.");
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"GTFO.EZ failed to empty all-disabled loot tables: {e}");
        }
    }
}

// G.EZ-T125: the vanilla values of every level datablock field the expedition balance, artifact and
// loot tweaks touch, captured right before they run, so they can be put back and the tweaks run again
// with other values (the host's) without compounding. Fields are named with nameof and read/written
// through the interop properties; lists (pack sizes, consumable tables) are copied element by element.
internal sealed class LevelBlockSnapshot
{
    private readonly List<Action> restore = new();

    internal static LevelBlockSnapshot Capture()
    {
        var s = new LevelBlockSnapshot();

        foreach (var block in GameDataBlockBase<ExpeditionBalanceDataBlock>.GetAllBlocks())
        {
            if (block == null) continue;
            s.Props(block,
                nameof(ExpeditionBalanceDataBlock.HealthPerZone), nameof(ExpeditionBalanceDataBlock.DisinfectionPerZone),
                nameof(ExpeditionBalanceDataBlock.WeaponAmmoPerZone), nameof(ExpeditionBalanceDataBlock.ToolAmmoPerZone),
                nameof(ExpeditionBalanceDataBlock.CommodityValuePerZone), nameof(ExpeditionBalanceDataBlock.ChanceToPutCommodityInResourceContainer),
                nameof(ExpeditionBalanceDataBlock.ChanceToPutArtifactInResourceContainer), nameof(ExpeditionBalanceDataBlock.ChanceToSpawnCommodityLargePack),
                nameof(ExpeditionBalanceDataBlock.ChanceToSpawnCommodityMediumPack), nameof(ExpeditionBalanceDataBlock.ChanceToReUseResourceContainer),
                nameof(ExpeditionBalanceDataBlock.MaxPacksPerResourceContainer), nameof(ExpeditionBalanceDataBlock.EmptyWeakResourceContainersPerZone),
                nameof(ExpeditionBalanceDataBlock.EmptySecureResourceContainersPerZone), nameof(ExpeditionBalanceDataBlock.LootPerZone),
                nameof(ExpeditionBalanceDataBlock.AirPerZone), nameof(ExpeditionBalanceDataBlock.AirPerZoneInNoAir),
                nameof(ExpeditionBalanceDataBlock.TerminalsPerZone), nameof(ExpeditionBalanceDataBlock.WeakResourceContainerWithPackChanceForLocked),
                nameof(ExpeditionBalanceDataBlock.EnemyPatrolGroupsPerZone), nameof(ExpeditionBalanceDataBlock.StaticEnemiesMaxPerZone),
                nameof(ExpeditionBalanceDataBlock.StaticEnemiesMaxSmallArea), nameof(ExpeditionBalanceDataBlock.StaticEnemiesMaxMediumArea),
                nameof(ExpeditionBalanceDataBlock.StaticEnemiesMaxLargeArea), nameof(ExpeditionBalanceDataBlock.StaticEnemiesMaxHugeArea),
                nameof(ExpeditionBalanceDataBlock.EnemyPopulationPerZone), nameof(ExpeditionBalanceDataBlock.VoxelCoverageAreaMultiplier),
                nameof(ExpeditionBalanceDataBlock.VoxelCoverageAreaScoringRandomMultiplier), nameof(ExpeditionBalanceDataBlock.ArtifactsPerSegment),
                nameof(ExpeditionBalanceDataBlock.ArtifactsPerLayer), nameof(ExpeditionBalanceDataBlock.WeakDoor4x4Health),
                nameof(ExpeditionBalanceDataBlock.WeakDoor8x4Health), nameof(ExpeditionBalanceDataBlock.WeakDoorChanceLockWeightNoLock),
                nameof(ExpeditionBalanceDataBlock.WeakDoorChanceLockWeightMeleeLock), nameof(ExpeditionBalanceDataBlock.WeakDoorChanceLockWeightHackableLock),
                nameof(ExpeditionBalanceDataBlock.WeakDoorUnlockedChanceForOpen), nameof(ExpeditionBalanceDataBlock.WeakDoorOpenChanceForWallRemoverUsed),
                nameof(ExpeditionBalanceDataBlock.WeakDoorLockHealth), nameof(ExpeditionBalanceDataBlock.GlueVolumeToDoorHealthConversion),
                nameof(ExpeditionBalanceDataBlock.GlueVolumeForDoorGlueMaxState));

            var sizes = block.ResourcePackSizes;
            if (sizes != null)
            {
                var values = new float[sizes.Count];
                for (var i = 0; i < values.Length; i++) values[i] = sizes[i];
                s.restore.Add(() =>
                {
                    for (var i = 0; i < values.Length && i < sizes.Count; i++) sizes[i] = values[i];
                });
            }

            foreach (var data in new[] { block.TentacleTraps, block.ParasiteNests })
            {
                if (data == null) continue;
                s.Props(data, nameof(StaticEnemyData.Health), nameof(StaticEnemyData.AttackDamage), nameof(StaticEnemyData.MaxPerZone),
                    nameof(StaticEnemyData.MaxSmallArea), nameof(StaticEnemyData.MaxMediumArea), nameof(StaticEnemyData.MaxLargeArea),
                    nameof(StaticEnemyData.MaxHugeArea));
            }
        }

        foreach (var block in GameDataBlockBase<ArtifactDistributionDataBlock>.GetAllBlocks())
            if (block != null)
                s.Props(block, nameof(ArtifactDistributionDataBlock.BasicWeight), nameof(ArtifactDistributionDataBlock.SpecializedWeight),
                    nameof(ArtifactDistributionDataBlock.AdvancedWeight));

        foreach (var rundown in GameDataBlockBase<RundownDataBlock>.GetAllBlocks())
        {
            if (rundown == null) continue;
            foreach (var tier in new[] { rundown.TierA, rundown.TierB, rundown.TierC, rundown.TierD, rundown.TierE })
            {
                if (tier == null) continue;
                foreach (var expedition in tier)
                {
                    if (expedition == null) continue;
                    foreach (var layer in new[] { expedition.MainLayerData, expedition.SecondaryLayerData, expedition.ThirdLayerData })
                    {
                        var artifacts = layer?.ArtifactData;
                        if (artifacts == null) continue;
                        s.Props(artifacts, nameof(artifacts.ArtifactAmountMulti));
                        if (artifacts.ArtifactZoneDistributions == null) continue;
                        foreach (var zone in artifacts.ArtifactZoneDistributions)
                            if (zone != null)
                                s.Props(zone, nameof(zone.BasicArtifactWeight), nameof(zone.SpecializedArtifactWeight), nameof(zone.AdvancedArtifactWeight));
                    }
                }
            }
        }

        foreach (var block in GameDataBlockBase<ConsumableDistributionDataBlock>.GetAllBlocks())
        {
            if (block == null) continue;
            s.Props(block, nameof(ConsumableDistributionDataBlock.SpawnsPerZone), nameof(ConsumableDistributionDataBlock.ChanceToSpawnInResourceContainer));
            var spawnData = block.SpawnData;
            if (spawnData == null) continue;
            var entries = new List<(uint Id, float Weight)>();
            foreach (var entry in spawnData) entries.Add((entry.ItemID, entry.Weight));
            s.restore.Add(() =>
            {
                block.SpawnData.Clear();
                foreach (var (id, weight) in entries) block.SpawnData.Add(new ConsumableSpawnData { ItemID = id, Weight = weight });
            });
        }

        foreach (var block in GameDataBlockBase<BigPickupDistributionDataBlock>.GetAllBlocks())
            if (block != null)
                s.Props(block, nameof(BigPickupDistributionDataBlock.SpawnsPerZone));

        Plugin.Logger.LogInfo($"GTFO.EZ: {s.restore.Count} vanilla level values kept for the host/client level settings sync.");
        return s;
    }

    internal void Restore()
    {
        foreach (var action in restore) action();
    }

    private void Props(object target, params string[] names)
    {
        var type = target.GetType();
        foreach (var name in names)
        {
            var property = type.GetProperty(name) ?? throw new MissingMemberException(type.Name, name);
            var value = property.GetValue(target);
            restore.Add(() => property.SetValue(target, value));
        }
    }
}