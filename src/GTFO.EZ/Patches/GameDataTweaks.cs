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
    [HarmonyPostfix]
    private static void Postfix()
    {
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
        RunTweakCategory("expedition balance", ApplyExpeditionBalanceTweaks);
        RunTweakCategory("loot", ApplyLootTweaks);
        RunTweakCategory("melee", ApplyMeleeTweaks);
        RunTweakCategory("weapon", ApplyWeaponTweaks);
        Plugin.Logger.LogInfo("GTFO.EZ tweaks applied. Goo goo. Gah gah.");
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
        var consumableMult = Plugin.ConsumableSpawnMultiplier.Value;
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

        var bigPickupMult = Plugin.BigPickupSpawnMultiplier.Value;
        foreach (var block in GameDataBlockBase<BigPickupDistributionDataBlock>.GetAllBlocks())
        {
            block.SpawnsPerZone = ScaleUp(block.SpawnsPerZone, bigPickupMult);
        }
    }

    // ItemIDs confirmed by logging GameDataBlockBase<ItemDataBlock> entries referenced from
    // ConsumableDistributionDataBlock.SpawnData at runtime - not documented anywhere official.
    private static readonly Dictionary<uint, Func<float>> ConsumableWeightMultipliers = new()
    {
        { 30, () => Plugin.FlashlightItemSpawnWeightMultiplier.Value },   // Long Range Flashlight
        { 114, () => Plugin.GlowstickSpawnWeightMultiplier.Value },        // Glow Stick
        { 174, () => Plugin.GlowstickSpawnWeightMultiplier.Value },        // Glow Stick Yellow
        { 115, () => Plugin.CFoamGrenadeSpawnWeightMultiplier.Value },     // C-Foam Grenade
        { 117, () => Plugin.FogRepellerSpawnWeightMultiplier.Value },      // Fog Repeller
        { 116, () => Plugin.LockMelterSpawnWeightMultiplier.Value },       // Lock Melter
        { 139, () => Plugin.ExplosiveTripMineSpawnWeightMultiplier.Value }, // Explosive Trip Mine
        { 144, () => Plugin.CFoamTripmineSpawnWeightMultiplier.Value },    // C-Foam Tripmine
    };

    // ExpeditionBalanceDataBlock is the per-difficulty-tier resource/enemy/door budget table -
    // one block per tier (Easy/Normal/Hard/...), so every tier gets scaled by the same factor.
    private static void ApplyExpeditionBalanceTweaks()
    {
        foreach (var block in GameDataBlockBase<ExpeditionBalanceDataBlock>.GetAllBlocks())
        {
            block.HealthPerZone *= Plugin.HealthPackResourceMultiplier.Value;
            block.DisinfectionPerZone *= Plugin.DisinfectionResourceMultiplier.Value;
            block.WeaponAmmoPerZone *= Plugin.WeaponAmmoResourceMultiplier.Value;
            block.ToolAmmoPerZone *= Plugin.ToolAmmoResourceMultiplier.Value;
            block.CommodityValuePerZone *= Plugin.CommodityValueMultiplier.Value;
            block.ChanceToPutCommodityInResourceContainer *= Plugin.CommodityInContainerChanceMultiplier.Value;
            block.ChanceToPutArtifactInResourceContainer *= Plugin.ArtifactInContainerChanceMultiplier.Value;
            block.ChanceToSpawnCommodityLargePack *= Plugin.LargeCommodityPackChanceMultiplier.Value;
            block.ChanceToSpawnCommodityMediumPack *= Plugin.MediumCommodityPackChanceMultiplier.Value;
            block.ChanceToReUseResourceContainer *= Plugin.ResourceContainerReuseChanceMultiplier.Value;
            block.MaxPacksPerResourceContainer = ScaleUp(block.MaxPacksPerResourceContainer, Plugin.MaxPacksPerContainerMultiplier.Value);
            block.EmptyWeakResourceContainersPerZone *= Plugin.EmptyWeakContainersPerZoneMultiplier.Value;
            block.EmptySecureResourceContainersPerZone *= Plugin.EmptySecureContainersPerZoneMultiplier.Value;
            block.LootPerZone *= Plugin.LootPerZoneMultiplier.Value;
            block.AirPerZone *= Plugin.AirPerZoneMultiplier.Value;
            block.AirPerZoneInNoAir *= Plugin.AirPerZoneInNoAirZoneMultiplier.Value;
            block.TerminalsPerZone *= Plugin.TerminalsPerZoneMultiplier.Value;
            block.WeakResourceContainerWithPackChanceForLocked *= Plugin.LockedWeakContainerWithPackChanceMultiplier.Value;

            var packSizeMult = Plugin.ResourcePackSizeMultiplier.Value;
            for (var i = 0; i < block.ResourcePackSizes.Count; i++)
            {
                block.ResourcePackSizes[i] *= packSizeMult;
            }

            block.EnemyPatrolGroupsPerZone = ScaleDown(block.EnemyPatrolGroupsPerZone, Plugin.EnemyPatrolGroupsPerZoneMultiplier.Value);
            block.StaticEnemiesMaxPerZone = ScaleDown(block.StaticEnemiesMaxPerZone, Plugin.StaticEnemiesMaxPerZoneMultiplier.Value);
            block.StaticEnemiesMaxSmallArea = ScaleDown(block.StaticEnemiesMaxSmallArea, Plugin.StaticEnemiesMaxSmallAreaMultiplier.Value);
            block.StaticEnemiesMaxMediumArea = ScaleDown(block.StaticEnemiesMaxMediumArea, Plugin.StaticEnemiesMaxMediumAreaMultiplier.Value);
            block.StaticEnemiesMaxLargeArea = ScaleDown(block.StaticEnemiesMaxLargeArea, Plugin.StaticEnemiesMaxLargeAreaMultiplier.Value);
            block.StaticEnemiesMaxHugeArea = ScaleDown(block.StaticEnemiesMaxHugeArea, Plugin.StaticEnemiesMaxHugeAreaMultiplier.Value);
            block.EnemyPopulationPerZone *= Plugin.EnemyPopulationPerZoneMultiplier.Value;

            block.VoxelCoverageAreaMultiplier *= Plugin.LevelGenVoxelCoverageMultiplier.Value;
            block.VoxelCoverageAreaScoringRandomMultiplier *= Plugin.LevelGenVoxelCoverageRandomnessMultiplier.Value;
            block.ArtifactsPerSegment = ScaleUp(block.ArtifactsPerSegment, Plugin.ArtifactsPerSegmentMultiplier.Value);
            block.ArtifactsPerLayer = ScaleUp(block.ArtifactsPerLayer, Plugin.ArtifactsPerLayerMultiplier.Value);

            block.WeakDoor4x4Health *= Plugin.WeakDoor4x4HealthMultiplier.Value;
            block.WeakDoor8x4Health *= Plugin.WeakDoor8x4HealthMultiplier.Value;
            block.WeakDoorChanceLockWeightNoLock *= Plugin.WeakDoorNoLockWeightMultiplier.Value;
            block.WeakDoorChanceLockWeightMeleeLock *= Plugin.WeakDoorMeleeLockWeightMultiplier.Value;
            block.WeakDoorChanceLockWeightHackableLock *= Plugin.WeakDoorHackableLockWeightMultiplier.Value;
            block.WeakDoorUnlockedChanceForOpen *= Plugin.WeakDoorUnlockedOpenChanceMultiplier.Value;
            block.WeakDoorOpenChanceForWallRemoverUsed *= Plugin.WeakDoorWallRemoverOpenChanceMultiplier.Value;
            block.WeakDoorLockHealth *= Plugin.WeakDoorLockHealthMultiplier.Value;
            block.GlueVolumeToDoorHealthConversion *= Plugin.GlueVolumeToDoorHealthConversionMultiplier.Value;
            block.GlueVolumeForDoorGlueMaxState *= Plugin.GlueVolumeForDoorMaxStateMultiplier.Value;

            ApplyStaticEnemyData(block.TentacleTraps,
                Plugin.TentacleTrapHealthMultiplier.Value,
                Plugin.TentacleTrapAttackDamageMultiplier.Value,
                Plugin.TentacleTrapMaxPerZoneMultiplier.Value,
                Plugin.TentacleTrapMaxSmallAreaMultiplier.Value,
                Plugin.TentacleTrapMaxMediumAreaMultiplier.Value,
                Plugin.TentacleTrapMaxLargeAreaMultiplier.Value,
                Plugin.TentacleTrapMaxHugeAreaMultiplier.Value);

            ApplyStaticEnemyData(block.ParasiteNests,
                Plugin.ParasiteNestHealthMultiplier.Value,
                Plugin.ParasiteNestAttackDamageMultiplier.Value,
                Plugin.ParasiteNestMaxPerZoneMultiplier.Value,
                Plugin.ParasiteNestMaxSmallAreaMultiplier.Value,
                Plugin.ParasiteNestMaxMediumAreaMultiplier.Value,
                Plugin.ParasiteNestMaxLargeAreaMultiplier.Value,
                Plugin.ParasiteNestMaxHugeAreaMultiplier.Value);
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
[HarmonyPatch(typeof(GameDataInit), nameof(GameDataInit.Initialize))]
internal static class GameDataInit_EmptyDisabledLoot_Patch
{
    [HarmonyPostfix]
    [HarmonyAfter("MindAttic.GTFO.EZ.AbsurdItems")]
    [HarmonyPriority(Priority.Last)]
    private static void Postfix()
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