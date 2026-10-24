using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP;
using BepInEx.Logging;
using GTFO.EZ.Core;
using HarmonyLib;
using UnityEngine;

namespace GTFO.EZ;

[BepInPlugin(GUID, NAME, VERSION)]
[BepInProcess("GTFO.exe")]
public class Plugin : BasePlugin
{
    public const string GUID = "MindAttic.GTFO.EZ";
    public const string NAME = "GTFO.EZ";
    public const string VERSION = "16.0.0";

    private static readonly string[] FamilyGuids =
    {
        GUID,
        "MindAttic.GTFO.EZ.AbsurdWeapons",
        "MindAttic.GTFO.EZ.AbsurdMoves",
        "MindAttic.GTFO.EZ.AbsurdItems",
        "MindAttic.GTFO.CrashTestDummies",
        "MindAttic.GTFO.SkipDrop",
    };

    // Shared between the rundown-select header and the in-level warden-intel message so the two
    // surfaces never drift apart. One line per family mod that's loaded with its master switch on,
    // built on each call since sibling mods may load after this one. Major version only (vN).
    internal static string EasterEggMessage
    {
        get
        {
            var lines = new List<string>();
            foreach (var guid in FamilyGuids)
            {
                if (!MindAttic.GTFO.Shared.SharedOwner.IsActive(guid)) continue;
                var meta = IL2CPPChainloader.Instance.Plugins[guid].Metadata;
                lines.Add($"{meta.Name} v{meta.Version.Major} Active");
            }
            lines.Add("Goo goo. Gah gah.");
            return string.Join("<br>", lines);
        }
    }

    internal static ManualLogSource Logger = null!;

    // Bookkeeping only, not a gameplay stat - lets ApplyLootDefaultMigrations run its one-time
    // fixup below exactly once per install rather than every launch.
    internal static ConfigEntry<int> ConfigMigrationVersion = null!;

    internal static ConfigEntry<float> HealthMultiplier = null!;
    internal static ConfigEntry<float> HealthRegenRateMultiplier = null!;
    internal static ConfigEntry<float> HealthRegenCapMultiplier = null!;
    internal static ConfigEntry<bool> RemoveCombatStaminaPenalty = null!;
    internal static ConfigEntry<float> StaminaMultiplier = null!;
    internal static ConfigEntry<float> FallDamageMultiplier = null!;
    internal static ConfigEntry<float> FallDamageMinHeightMultiplier = null!;
    internal static ConfigEntry<float> FallDamageMaxHeightMultiplier = null!;
    internal static ConfigEntry<float> AmmoMultiplier = null!;
    internal static ConfigEntry<float> FlashlightAngleMultiplier = null!;
    internal static ConfigEntry<float> FlashlightIntensityMultiplier = null!;
    internal static ConfigEntry<float> DetectionDistanceMultiplier = null!;
    internal static ConfigEntry<float> WalkSpeedMultiplier = null!;
    internal static ConfigEntry<float> RunSpeedMultiplier = null!;
    internal static ConfigEntry<float> AirSpeedMultiplier = null!;
    internal static ConfigEntry<float> CrouchSpeedMultiplier = null!;
    internal static ConfigEntry<float> LadderSpeedMultiplier = null!;
    internal static ConfigEntry<float> JumpHeightMultiplier = null!;
    internal static ConfigEntry<float> FriendlyFireMultiplier = null!;
    internal static ConfigEntry<float> BatteryCapacityMultiplier = null!;
    internal static ConfigEntry<float> SmallBatteryDrainMultiplier = null!;
    internal static ConfigEntry<float> MediumBatteryDrainMultiplier = null!;
    internal static ConfigEntry<float> LargeBatteryDrainMultiplier = null!;
    internal static ConfigEntry<float> NoAirDamageMultiplier = null!;
    internal static ConfigEntry<float> NoAirDepletionTimeMultiplier = null!;
    internal static ConfigEntry<bool> DisableFog = null!;
    internal static ConfigEntry<float> EnemyHealthMultiplier = null!;
    internal static ConfigEntry<float> EnemyMeleeDamageMultiplier = null!;
    internal static ConfigEntry<float> EnemyTentacleDamageMultiplier = null!;
    internal static ConfigEntry<float> ConsumableSpawnMultiplier = null!;
    internal static ConfigEntry<float> BigPickupSpawnMultiplier = null!;
    internal static ConfigEntry<float> ReviveDurationMultiplier = null!;
    internal static ConfigEntry<float> FlashlightItemSpawnWeightMultiplier = null!;
    internal static ConfigEntry<float> GlowstickSpawnWeightMultiplier = null!;
    internal static ConfigEntry<float> CFoamGrenadeSpawnWeightMultiplier = null!;
    internal static ConfigEntry<float> FogRepellerSpawnWeightMultiplier = null!;
    internal static ConfigEntry<float> LockMelterSpawnWeightMultiplier = null!;
    internal static ConfigEntry<float> ExplosiveTripMineSpawnWeightMultiplier = null!;
    internal static ConfigEntry<float> CFoamTripmineSpawnWeightMultiplier = null!;
    internal static ConfigEntry<bool> BotsAlwaysEquipSentryGun = null!;
    internal static ConfigEntry<bool> ControlDefaultsApplied = null!;

    // G.EZ-T10 - see Patches/TerminalPingPatch.cs.
    internal static ConfigEntry<bool> TerminalPingAnywhere = null!;

    // G.EZ-T34 spike - see Patches/DeployPickupHotkeys.cs. On by default at the user's request to
    // test it live; calls private bot-AI methods directly and is only confirmed to compile, not
    // confirmed safe by decompiling alone - may be reverted if it doesn't hold up in play.
    internal static ConfigEntry<bool> BotsDeployOnShiftE = null!;

    // G.EZ-T39 - see Patches/FogClearPulse.cs.
    internal static ConfigEntry<bool> FogClearEnabled = null!;
    internal static ConfigEntry<float> FogClearStartRange = null!;
    internal static ConfigEntry<float> FogClearMaxRange = null!;
    internal static ConfigEntry<float> FogClearRampSeconds = null!;
    internal static ConfigEntry<float> FogClearDensityOverride = null!;

    // ExpeditionBalanceDataBlock - the per-difficulty-tier resource/enemy/door budget table.
    internal static ConfigEntry<float> HealthPackResourceMultiplier = null!;
    internal static ConfigEntry<float> DisinfectionResourceMultiplier = null!;
    internal static ConfigEntry<float> WeaponAmmoResourceMultiplier = null!;
    internal static ConfigEntry<float> ToolAmmoResourceMultiplier = null!;
    internal static ConfigEntry<float> CommodityValueMultiplier = null!;
    internal static ConfigEntry<float> CommodityInContainerChanceMultiplier = null!;
    internal static ConfigEntry<float> ArtifactInContainerChanceMultiplier = null!;
    internal static ConfigEntry<float> LargeCommodityPackChanceMultiplier = null!;
    internal static ConfigEntry<float> MediumCommodityPackChanceMultiplier = null!;
    internal static ConfigEntry<float> ResourceContainerReuseChanceMultiplier = null!;
    internal static ConfigEntry<float> MaxPacksPerContainerMultiplier = null!;
    internal static ConfigEntry<float> EmptyWeakContainersPerZoneMultiplier = null!;
    internal static ConfigEntry<float> EmptySecureContainersPerZoneMultiplier = null!;
    internal static ConfigEntry<float> LootPerZoneMultiplier = null!;
    internal static ConfigEntry<float> AirPerZoneMultiplier = null!;
    internal static ConfigEntry<float> AirPerZoneInNoAirZoneMultiplier = null!;
    internal static ConfigEntry<float> TerminalsPerZoneMultiplier = null!;
    internal static ConfigEntry<float> LockedWeakContainerWithPackChanceMultiplier = null!;
    internal static ConfigEntry<float> ResourcePackSizeMultiplier = null!;

    internal static ConfigEntry<float> EnemyPatrolGroupsPerZoneMultiplier = null!;
    internal static ConfigEntry<float> StaticEnemiesMaxPerZoneMultiplier = null!;
    internal static ConfigEntry<float> StaticEnemiesMaxSmallAreaMultiplier = null!;
    internal static ConfigEntry<float> StaticEnemiesMaxMediumAreaMultiplier = null!;
    internal static ConfigEntry<float> StaticEnemiesMaxLargeAreaMultiplier = null!;
    internal static ConfigEntry<float> StaticEnemiesMaxHugeAreaMultiplier = null!;
    internal static ConfigEntry<float> EnemyPopulationPerZoneMultiplier = null!;

    // TentacleTraps and ParasiteNests on ExpeditionBalanceDataBlock are each a StaticEnemyData
    // reference - the same 7-field shape (Health/AttackDamage/MaxPerZone/MaxSmallArea/
    // MaxMediumArea/MaxLargeArea/MaxHugeArea) already used for roaming/sleeper enemies above.
    internal static ConfigEntry<float> TentacleTrapHealthMultiplier = null!;
    internal static ConfigEntry<float> TentacleTrapAttackDamageMultiplier = null!;
    internal static ConfigEntry<float> TentacleTrapMaxPerZoneMultiplier = null!;
    internal static ConfigEntry<float> TentacleTrapMaxSmallAreaMultiplier = null!;
    internal static ConfigEntry<float> TentacleTrapMaxMediumAreaMultiplier = null!;
    internal static ConfigEntry<float> TentacleTrapMaxLargeAreaMultiplier = null!;
    internal static ConfigEntry<float> TentacleTrapMaxHugeAreaMultiplier = null!;

    internal static ConfigEntry<float> ParasiteNestHealthMultiplier = null!;
    internal static ConfigEntry<float> ParasiteNestAttackDamageMultiplier = null!;
    internal static ConfigEntry<float> ParasiteNestMaxPerZoneMultiplier = null!;
    internal static ConfigEntry<float> ParasiteNestMaxSmallAreaMultiplier = null!;
    internal static ConfigEntry<float> ParasiteNestMaxMediumAreaMultiplier = null!;
    internal static ConfigEntry<float> ParasiteNestMaxLargeAreaMultiplier = null!;
    internal static ConfigEntry<float> ParasiteNestMaxHugeAreaMultiplier = null!;

    internal static ConfigEntry<float> LevelGenVoxelCoverageMultiplier = null!;
    internal static ConfigEntry<float> LevelGenVoxelCoverageRandomnessMultiplier = null!;
    internal static ConfigEntry<float> ArtifactsPerSegmentMultiplier = null!;
    internal static ConfigEntry<float> ArtifactsPerLayerMultiplier = null!;

    internal static ConfigEntry<float> WeakDoor4x4HealthMultiplier = null!;
    internal static ConfigEntry<float> WeakDoor8x4HealthMultiplier = null!;
    internal static ConfigEntry<float> WeakDoorNoLockWeightMultiplier = null!;
    internal static ConfigEntry<float> WeakDoorMeleeLockWeightMultiplier = null!;
    internal static ConfigEntry<float> WeakDoorHackableLockWeightMultiplier = null!;
    internal static ConfigEntry<float> WeakDoorUnlockedOpenChanceMultiplier = null!;
    internal static ConfigEntry<float> WeakDoorWallRemoverOpenChanceMultiplier = null!;
    internal static ConfigEntry<float> WeakDoorLockHealthMultiplier = null!;
    internal static ConfigEntry<float> GlueVolumeToDoorHealthConversionMultiplier = null!;
    internal static ConfigEntry<float> GlueVolumeForDoorMaxStateMultiplier = null!;

    // MeleeArchetypeDataBlock - one entry per melee weapon type: Bat, Hammer, Knife, Spear (real
    // in-game names, confirmed from PublicName - "Hammer" covers what's commonly called the
    // sledgehammer). Organized by archetype, then by action type (Light/Charged/Push/General),
    // so with 4 archetypes x 19 stats each this is a lookup table rather than 76 named fields.
    internal static readonly Dictionary<string, ConfigEntry<float>> MeleeMultipliers = new();

    internal static readonly Dictionary<string, ConfigEntry<float>> WeaponTypeMultipliers = new();
    internal static readonly Dictionary<string, ConfigEntry<float>> WeaponIndividualMultipliers = new();

    // Remaining ArchetypeDataBlock stats are left as one flat multiplier shared by every archetype
    // (guns, sentries, and other tools alike) rather than getting the type x individual treatment
    // above, to keep the config from tripling in size again for stats nobody asked to tune per-gun.
    internal static ConfigEntry<float> WeaponCostOfBulletMultiplier = null!;
    internal static ConfigEntry<float> WeaponShotDelayMultiplier = null!;
    internal static ConfigEntry<float> WeaponHipFireSpreadMultiplier = null!;
    internal static ConfigEntry<float> WeaponAimSpreadMultiplier = null!;
    internal static ConfigEntry<float> WeaponPiercingDamageCountLimitMultiplier = null!;
    internal static ConfigEntry<float> WeaponBurstDelayMultiplier = null!;
    internal static ConfigEntry<float> WeaponBurstShotCountMultiplier = null!;
    internal static ConfigEntry<float> WeaponShotgunBulletCountMultiplier = null!;
    internal static ConfigEntry<float> WeaponShotgunConeSizeMultiplier = null!;
    internal static ConfigEntry<float> WeaponShotgunBulletSpreadMultiplier = null!;
    internal static ConfigEntry<float> WeaponSpecialChargetupTimeMultiplier = null!;
    internal static ConfigEntry<float> WeaponSpecialCooldownTimeMultiplier = null!;
    internal static ConfigEntry<float> WeaponSpecialSemiBurstCountTimeoutMultiplier = null!;
    internal static ConfigEntry<float> WeaponEquipTransitionTimeMultiplier = null!;
    internal static ConfigEntry<float> WeaponAimTransitionTimeMultiplier = null!;
    internal static ConfigEntry<float> WeaponShellCasingSizeMultiplier = null!;
    internal static ConfigEntry<float> SentryStartFireDelayMultiplier = null!;
    internal static ConfigEntry<float> SentryRotationSpeedMultiplier = null!;
    internal static ConfigEntry<float> SentryDetectionMaxRangeMultiplier = null!;
    internal static ConfigEntry<float> SentryDetectionMaxAngleMultiplier = null!;
    internal static ConfigEntry<float> SentryLongRangeThresholdMultiplier = null!;
    internal static ConfigEntry<float> SentryShortRangeThresholdMultiplier = null!;
    internal static ConfigEntry<float> SentryStartFireDelayTagMultiplier = null!;
    internal static ConfigEntry<float> SentryRotationSpeedTagMultiplier = null!;
    internal static ConfigEntry<float> SentryDamageTagMultiplier = null!;
    internal static ConfigEntry<float> SentryStaggerDamageTagMultiplier = null!;
    internal static ConfigEntry<float> SentryCostOfBulletTagMultiplier = null!;
    internal static ConfigEntry<float> SentryShotDelayTagMultiplier = null!;
    internal static ConfigEntry<float> SentryDeployPickupInteractionDurationMultiplier = null!;
    internal static ConfigEntry<bool> SentriesPassThrough = null!;

    public override void Load()
    {
        Logger = Log;

        // The master switch is the cfg's first entry ("0. Mod" sorts ahead of every other section), so
        // the mod can be turned off from its .cfg where a mod manager profile doesn't list it. When it's
        // off nothing else is bound, migrated or patched: the other settings stay in the file untouched,
        // and the migrations still due run the next time the mod is switched on.
        var enabled = Config.Bind("0. Mod", "Enabled", true,
            "Turn the whole mod on or off. When false, the mod loads nothing: no patches, no gameplay tuning, no hotkeys.");
        if (!enabled.Value)
        {
            Logger.LogInfo($"{NAME} is switched off in its config ([0. Mod] Enabled) - not loading.");
            return;
        }

        BindConfig();
        ApplyConfigDefaultMigrations();

        new Harmony(GUID).PatchAll();

        Logger.LogInfo($"{NAME} v{VERSION} loaded.");
    }

    // BepInEx keeps whatever value is already in an upgrader's .cfg, so when this mod changes a
    // default, existing installs never pick up the new one. Each entry here nudges a setting still
    // sitting on its old default over to the new one - and runs only for installs that haven't yet
    // reached the migration version that introduced it. Re-running the whole list on every bump
    // would undo a player who deliberately set something back after an earlier migration (v10 and
    // v11 both did exactly that). Health Regen Cap's v8 drop to 0.5 had no stated rationale and
    // never matched how the mod was played, so v11 restores 2.0 and migrates anyone moved onto 0.5.
    // Glow Stick isn't listed: it was introduced already disabled, with no older default to strand.
    private static readonly (int Version, Func<ConfigEntry<float>> Entry, float OldDefault)[] DefaultMigrations =
    {
        (1, () => CFoamGrenadeSpawnWeightMultiplier, 1.0f),     // v9
        (1, () => LockMelterSpawnWeightMultiplier, 1.0f),
        (2, () => ExplosiveTripMineSpawnWeightMultiplier, 1.0f), // v9
        (2, () => CFoamTripmineSpawnWeightMultiplier, 1.0f),
        (3, () => HealthRegenRateMultiplier, 5.0f),              // v10
        (3, () => FallDamageMultiplier, 1.0f),
        (3, () => FallDamageMinHeightMultiplier, 2.0f),
        (3, () => FlashlightAngleMultiplier, 1.25f),
        (3, () => FlashlightIntensityMultiplier, 1.25f),
        (3, () => ReviveDurationMultiplier, 1.0f),
        (4, () => HealthRegenCapMultiplier, 0.5f),               // v11
        (5, () => FogRepellerSpawnWeightMultiplier, 1.0f),
    };

    private static void ApplyConfigDefaultMigrations()
    {
        var reached = ConfigMigrationVersion.Value;
        var latest = reached;
        foreach (var (version, entry, oldDefault) in DefaultMigrations)
        {
            if (reached < version) MigrateStaleDefault(entry(), oldDefault);
            latest = Math.Max(latest, version);
        }
        ConfigMigrationVersion.Value = latest;
    }

    // Reads the new default straight off the entry itself (ConfigEntryBase.DefaultValue) rather
    // than taking it as a second parameter, so there's only ever one place (the Config.Bind call)
    // that can get the "new" value wrong.
    private static void MigrateStaleDefault(ConfigEntry<float> entry, float oldDefault)
    {
        if (entry.Value == oldDefault) entry.Value = (float)entry.DefaultValue;
    }

    // Defaults follow community-consensus values rather than naive "bigger number" guesses:
    // health x2 is the one near-universal agreement among GTFO easy-mode mods (going further,
    // e.g. x4, was reported to make the game too easy and got walked back). Regen rate and cap
    // need separate multipliers because vanilla under-regens relative to its own cap (rate 0.2/s
    // against a 20% cap), so a single shared multiplier can't hit both target numbers at once.
    private void BindConfig()
    {
        ConfigMigrationVersion = Config.Bind("Internal", "Config Migration Version", 0,
            "Internal bookkeeping - do not edit. Tracks which one-time default migrations have already run.");

        HealthMultiplier = BindNonNegative("Health", "Max Health Multiplier", 2.0f,
            "Multiplies player max health. Vanilla 25 -> 50 at the default 2.0.");

        HealthRegenRateMultiplier = BindNonNegative("Health", "Health Regen Rate Multiplier", 0.5f,
            "Multiplies health regen per second. Vanilla 0.2/s -> 0.1/s at the default 0.5.");

        HealthRegenCapMultiplier = BindNonNegative("Health", "Health Regen Cap Multiplier", 2.0f,
            "Multiplies how much of max health can regenerate without a med kit. Vanilla 20% -> 40% at the default 2.0. Regen delay is left at vanilla on purpose - fast regen with no delay is overpowered.");

        RemoveCombatStaminaPenalty = Config.Bind("Stamina", "Remove Combat Stamina Penalty", true,
            "Makes stamina regen while in combat as fast as out of combat, and removes the 90% stamina cap while in combat.");

        StaminaMultiplier = BindNonNegative("Stamina", "Stamina Multiplier", 1.0f,
            "How much further stamina goes: every action's stamina cost (sprinting, jumping, walking, crouching, strafing) " +
            "is divided by this and stamina regen is multiplied by it. 1.5 = half as much again. Left at vanilla (1.0) by " +
            "default - moved over from GTFO.EZ.AbsurdMoves' Run Speed/Stamina feature (G.EZ-T97) so it works even without " +
            "AbsurdMoves installed.");

        FallDamageMultiplier = BindNonNegative("Fall Damage", "Fall Damage Multiplier", 0.1f,
            "Multiplies fall damage dealt once you're past the no-damage height. Default 0.1 (10% of normal) on top of the height change below.");

        FallDamageMinHeightMultiplier = BindNonNegative("Fall Damage", "Fall Damage Min Height Multiplier", 1.0f,
            "Multiplies the minimum fall height before any damage is taken. Left at vanilla (1.0) - the 10% damage multiplier above already does the work.");

        FallDamageMaxHeightMultiplier = BindNonNegative("Fall Damage", "Fall Damage Max Height Multiplier", 1.5f,
            "Multiplies the fall height at which damage maxes out. Vanilla 20m -> 30m at the default 1.5.");

        AmmoMultiplier = BindNonNegative("Ammo", "Weapon Ammo Multiplier", 1.5f,
            "Multiplies max reserve ammo, starting ammo, and refill amounts for standard/special/consumable ammo. Deliberately not paired with a damage buff - that combination was reported as overtuned.");

        FlashlightAngleMultiplier = BindNonNegative("Lighting", "Flashlight Angle Multiplier", 1.0f,
            "Multiplies the cone angle of headlamps and weapon-mounted lights. Left at vanilla (1.0) by default.");

        FlashlightIntensityMultiplier = BindNonNegative("Lighting", "Flashlight Intensity Multiplier", 1.0f,
            "Multiplies the brightness of headlamps and weapon-mounted lights. Left at vanilla (1.0) by default - brighter lights make you more visible to enemies too.");

        DetectionDistanceMultiplier = BindNonNegative("Enemy Detection", "Movement Detection Distance Multiplier", 0.75f,
            "Multiplies how far enemies can detect player movement noise. 0.75 on an 8m base gives 6m.");

        // Exposed for players who want to retune feel/difficulty further, but every one of these
        // defaults to 1.0 (vanilla, no change) - unlike the stats above, there's no community
        // consensus that these need adjusting for an easier game, so we're not guessing.
        WalkSpeedMultiplier = BindNonNegative("Movement", "Walk Speed Multiplier", 1.0f,
            "Multiplies walking move speed. Left at vanilla (1.0) by default.");

        RunSpeedMultiplier = BindNonNegative("Movement", "Run Speed Multiplier", 1.0f,
            "Multiplies sprinting move speed. Left at vanilla (1.0) by default.");

        AirSpeedMultiplier = BindNonNegative("Movement", "Air Speed Multiplier", 1.0f,
            "Multiplies mid-air move speed (control while jumping/falling). Left at vanilla (1.0) by default.");

        CrouchSpeedMultiplier = BindNonNegative("Movement", "Crouch Speed Multiplier", 1.0f,
            "Multiplies crouched move speed. Left at vanilla (1.0) by default.");

        LadderSpeedMultiplier = BindNonNegative("Movement", "Ladder Speed Multiplier", 1.0f,
            "Multiplies ladder climb speed. Left at vanilla (1.0) by default.");

        JumpHeightMultiplier = BindNonNegative("Movement", "Jump Height Multiplier", 1.0f,
            "Multiplies jump height (initial jump velocity). Left at vanilla (1.0) by default.");

        FriendlyFireMultiplier = BindNonNegative("Combat", "Friendly Fire Multiplier", 1.0f,
            "Multiplies damage taken from teammates' weapons. Left at vanilla (1.0) by default.");

        BatteryCapacityMultiplier = BindNonNegative("Utility", "Battery Capacity Multiplier", 1.0f,
            "Multiplies max battery charge for flashlights and powered tools. Left at vanilla (1.0) by default.");

        SmallBatteryDrainMultiplier = BindNonNegative("Utility", "Small Battery Drain Multiplier", 1.0f,
            "Multiplies battery drain per second for small powered tools. Left at vanilla (1.0) by default.");

        MediumBatteryDrainMultiplier = BindNonNegative("Utility", "Medium Battery Drain Multiplier", 1.0f,
            "Multiplies battery drain per second for medium powered tools. Left at vanilla (1.0) by default.");

        LargeBatteryDrainMultiplier = BindNonNegative("Utility", "Large Battery Drain Multiplier", 1.0f,
            "Multiplies battery drain per second for large powered tools. Left at vanilla (1.0) by default.");

        NoAirDamageMultiplier = BindNonNegative("Environment", "No-Air Damage Multiplier", 1.0f,
            "Multiplies the damage rate from suffocating in no-air zones once your air runs out. Left at vanilla (1.0) by default.");

        NoAirDepletionTimeMultiplier = BindNonNegative("Environment", "No-Air Depletion Time Multiplier", 1.0f,
            "Multiplies how long your air meter lasts in no-air zones before it empties. Left at vanilla (1.0) by default.");

        DisableFog = Config.Bind("Environment", "Disable Fog", true,
            "Turns off the visual fog that limits visibility. Default on - vanilla fog was judged more annoying than fun, and scaling its density down (what this setting used to do) turned out to have no visible effect in-game. Set to false to restore vanilla fog.");

        EnemyHealthMultiplier = BindNonNegative("Enemy Balance", "Enemy Health Multiplier", 1.0f,
            "Multiplies max health for every enemy type. Left at vanilla (1.0) by default.");

        EnemyMeleeDamageMultiplier = BindNonNegative("Enemy Balance", "Enemy Melee Damage Multiplier", 1.0f,
            "Multiplies melee attack damage for every enemy type. Left at vanilla (1.0) by default.");

        EnemyTentacleDamageMultiplier = BindNonNegative("Enemy Balance", "Enemy Tentacle Damage Multiplier", 1.0f,
            "Multiplies tentacle attack damage (Striker/Tank grabs, Scout tentacles, etc.) for every enemy type. Left at vanilla (1.0) by default.");

        ConsumableSpawnMultiplier = BindNonNegative("Loot", "Consumable Spawn Multiplier", 1.0f,
            "Multiplies how many consumables (flashlights, glow sticks, etc.) spawn per zone. With GTFO.EZ.AbsurdItems' Consumables Are Kits on, its Kit Loot sets how many kits instead. Left at vanilla (1.0) by default.");

        BigPickupSpawnMultiplier = BindNonNegative("Loot", "Big Pickup Spawn Multiplier", 1.0f,
            "Multiplies how many big pickups (fog turbines, artifacts, etc.) spawn per zone. Left at vanilla (1.0) by default.");

        ReviveDurationMultiplier = BindNonNegative("Interactions", "Revive Duration Multiplier", 0.33f,
            "Multiplies how long it takes to revive a downed teammate. Lower is faster. Default 0.33 (33% of vanilla).");

        // Each consumable is picked from a shared weighted lottery, so these retune the odds
        // between them rather than the total amount of loot (that's ConsumableSpawnMultiplier
        // above). A GTFO.EZ.AbsurdItems kit run replaces them along with the items (see GameDataTweaks).
        // Glow Stick, C-Foam Grenade, Fog Repeller, Lock Melter and both tripmines default to 0
        // (disabled); the flashlight is left at vanilla.
        FlashlightItemSpawnWeightMultiplier = BindNonNegative("Loot", "Long Range Flashlight Spawn Weight Multiplier", 1.0f,
            "Multiplies how likely a Long Range Flashlight is picked when a consumable spawns. Left at vanilla (1.0) by default.");

        GlowstickSpawnWeightMultiplier = BindNonNegative("Loot", "Glow Stick Spawn Weight Multiplier", 0.0f,
            "Multiplies how likely a glow stick (either color) is picked when a consumable spawns. Defaults to 0 (disabled) - its share goes to the other consumables.");

        CFoamGrenadeSpawnWeightMultiplier = BindNonNegative("Loot", "C-Foam Grenade Spawn Weight Multiplier", 0.0f,
            "Multiplies how likely a C-Foam Grenade is picked when a consumable spawns. Defaults to 0 (disabled) - its share goes to the other consumables.");

        FogRepellerSpawnWeightMultiplier = BindNonNegative("Loot", "Fog Repeller Spawn Weight Multiplier", 0.0f,
            "Multiplies how likely a Fog Repeller is picked when a consumable spawns. Defaults to 0 (disabled) - fog clearing already handles fog.");

        LockMelterSpawnWeightMultiplier = BindNonNegative("Loot", "Lock Melter Spawn Weight Multiplier", 0.0f,
            "Multiplies how likely a Lock Melter is picked when a consumable spawns. Defaults to 0 (disabled) - its share goes to the other consumables.");

        ExplosiveTripMineSpawnWeightMultiplier = BindNonNegative("Loot", "Explosive Trip Mine Spawn Weight Multiplier", 0.0f,
            "Multiplies how likely an Explosive Trip Mine is picked when a consumable spawns. Defaults to 0 (disabled) - its share goes to the other consumables.");

        CFoamTripmineSpawnWeightMultiplier = BindNonNegative("Loot", "C-Foam Tripmine Spawn Weight Multiplier", 0.0f,
            "Multiplies how likely a C-Foam Tripmine is picked when a consumable spawns. Defaults to 0 (disabled) - its share goes to the other consumables.");

        ControlDefaultsApplied = Config.Bind("Controls", "Control Defaults Applied", false,
            "On first launch the mod turns on comms-menu toggle (no holding Q), sprint toggle, aim toggle and stand-when-jumping in the game's own settings, then sets this to true and never touches them again. Set back to false to reapply.");

        BotsAlwaysEquipSentryGun = Config.Bind("Bots", "Bots Always Equip Sentry Gun", false,
            "Forces any bot's Tool slot to the basic Sentry Gun the moment it gets gear, whether it's " +
            "filling an empty slot or replacing a player. Off by default - bot loadouts can already be set " +
            "manually in COMS; this is here for anyone who wants it automatic instead.");

        // ExpeditionBalanceDataBlock: the per-difficulty-tier resource/enemy/door budget table.
        // Every entry here is left at vanilla (1.0) by default - this is a much bigger tuning
        // surface than the rest of the mod, exposed for players who want to go further themselves.
        HealthPackResourceMultiplier = BindNonNegative("Expedition Resources", "Health Pack Resource Multiplier", 1.0f,
            "Multiplies the health-pack resource budget distributed per zone. Left at vanilla (1.0) by default.");

        DisinfectionResourceMultiplier = BindNonNegative("Expedition Resources", "Disinfection Resource Multiplier", 1.0f,
            "Multiplies the infection-cure resource budget distributed per zone. Left at vanilla (1.0) by default.");

        WeaponAmmoResourceMultiplier = BindNonNegative("Expedition Resources", "Weapon Ammo Resource Multiplier", 1.0f,
            "Multiplies the weapon ammo-pack resource budget distributed per zone. Left at vanilla (1.0) by default.");

        ToolAmmoResourceMultiplier = BindNonNegative("Expedition Resources", "Tool Ammo Resource Multiplier", 1.0f,
            "Multiplies the tool ammo (sentries, C-foam launcher, etc.) resource budget per zone. Left at vanilla (1.0) by default.");

        CommodityValueMultiplier = BindNonNegative("Expedition Resources", "Commodity Value Multiplier", 1.0f,
            "Multiplies the total commodity (crafting material) value distributed per zone. Left at vanilla (1.0) by default.");

        CommodityInContainerChanceMultiplier = BindNonNegative("Expedition Resources", "Commodity In Container Chance Multiplier", 1.0f,
            "Multiplies the chance a resource container holds a commodity. Left at vanilla (1.0) by default.");

        ArtifactInContainerChanceMultiplier = BindNonNegative("Expedition Resources", "Artifact In Container Chance Multiplier", 1.0f,
            "Multiplies the chance a resource container holds an artifact. Left at vanilla (1.0) by default.");

        LargeCommodityPackChanceMultiplier = BindNonNegative("Expedition Resources", "Large Commodity Pack Chance Multiplier", 1.0f,
            "Multiplies the chance a commodity spawn is a large pack instead of small. Left at vanilla (1.0) by default.");

        MediumCommodityPackChanceMultiplier = BindNonNegative("Expedition Resources", "Medium Commodity Pack Chance Multiplier", 1.0f,
            "Multiplies the chance a commodity spawn is a medium pack instead of small. Left at vanilla (1.0) by default.");

        ResourceContainerReuseChanceMultiplier = BindNonNegative("Expedition Resources", "Resource Container Reuse Chance Multiplier", 1.0f,
            "Multiplies the chance a resource container gets reused for a second pack. Left at vanilla (1.0) by default.");

        MaxPacksPerContainerMultiplier = BindNonNegative("Expedition Resources", "Max Packs Per Container Multiplier", 1.0f,
            "Multiplies the max number of packs a single resource container can hold. Left at vanilla (1.0) by default.");

        EmptyWeakContainersPerZoneMultiplier = BindNonNegative("Expedition Resources", "Empty Weak Containers Per Zone Multiplier", 1.0f,
            "Multiplies how many empty weak (cardboard box) containers spawn per zone. Left at vanilla (1.0) by default.");

        EmptySecureContainersPerZoneMultiplier = BindNonNegative("Expedition Resources", "Empty Secure Containers Per Zone Multiplier", 1.0f,
            "Multiplies how many empty secure (locker) containers spawn per zone. Left at vanilla (1.0) by default.");

        LootPerZoneMultiplier = BindNonNegative("Expedition Resources", "Loot Per Zone Multiplier", 1.0f,
            "Multiplies the general loot budget distributed per zone. Left at vanilla (1.0) by default.");

        AirPerZoneMultiplier = BindNonNegative("Expedition Resources", "Air Per Zone Multiplier", 1.0f,
            "Multiplies the breathable-air resource budget per zone. Left at vanilla (1.0) by default.");

        AirPerZoneInNoAirZoneMultiplier = BindNonNegative("Expedition Resources", "Air Per Zone In No-Air Zone Multiplier", 1.0f,
            "Multiplies the air resource budget per zone specifically inside no-air areas. Left at vanilla (1.0) by default.");

        TerminalsPerZoneMultiplier = BindNonNegative("Expedition Resources", "Terminals Per Zone Multiplier", 1.0f,
            "Multiplies how many terminals spawn per zone. Left at vanilla (1.0) by default.");

        LockedWeakContainerWithPackChanceMultiplier = BindNonNegative("Expedition Resources", "Locked Weak Container With Pack Chance Multiplier", 1.0f,
            "Multiplies the chance a weak container holding a pack is locked. Left at vanilla (1.0) by default.");

        ResourcePackSizeMultiplier = BindNonNegative("Expedition Resources", "Resource Pack Size Multiplier", 1.0f,
            "Multiplies the size of every resource pack tier (small/medium/large). Left at vanilla (1.0) by default.");

        EnemyPatrolGroupsPerZoneMultiplier = BindNonNegative("Expedition Enemies", "Enemy Patrol Groups Per Zone Multiplier", 1.0f,
            "Multiplies how many roaming enemy patrol groups spawn per zone. Left at vanilla (1.0) by default.");

        StaticEnemiesMaxPerZoneMultiplier = BindNonNegative("Expedition Enemies", "Static Enemies Max Per Zone Multiplier", 1.0f,
            "Multiplies the max number of stationary (sleeper) enemies per zone. Left at vanilla (1.0) by default.");

        StaticEnemiesMaxSmallAreaMultiplier = BindNonNegative("Expedition Enemies", "Static Enemies Max Small Area Multiplier", 1.0f,
            "Multiplies the max stationary enemies allowed in a small area. Left at vanilla (1.0) by default.");

        StaticEnemiesMaxMediumAreaMultiplier = BindNonNegative("Expedition Enemies", "Static Enemies Max Medium Area Multiplier", 1.0f,
            "Multiplies the max stationary enemies allowed in a medium area. Left at vanilla (1.0) by default.");

        StaticEnemiesMaxLargeAreaMultiplier = BindNonNegative("Expedition Enemies", "Static Enemies Max Large Area Multiplier", 1.0f,
            "Multiplies the max stationary enemies allowed in a large area. Left at vanilla (1.0) by default.");

        StaticEnemiesMaxHugeAreaMultiplier = BindNonNegative("Expedition Enemies", "Static Enemies Max Huge Area Multiplier", 1.0f,
            "Multiplies the max stationary enemies allowed in a huge area. Left at vanilla (1.0) by default.");

        EnemyPopulationPerZoneMultiplier = BindNonNegative("Expedition Enemies", "Enemy Population Per Zone Multiplier", 1.0f,
            "Multiplies the overall enemy population budget per zone. Left at vanilla (1.0) by default.");

        TentacleTrapHealthMultiplier = BindNonNegative("Expedition Enemies", "Tentacle Trap Health Multiplier", 1.0f,
            "Multiplies max health for tentacle traps. Left at vanilla (1.0) by default.");

        TentacleTrapAttackDamageMultiplier = BindNonNegative("Expedition Enemies", "Tentacle Trap Attack Damage Multiplier", 1.0f,
            "Multiplies attack damage dealt by tentacle traps. Left at vanilla (1.0) by default.");

        TentacleTrapMaxPerZoneMultiplier = BindNonNegative("Expedition Enemies", "Tentacle Trap Max Per Zone Multiplier", 1.0f,
            "Multiplies the max number of tentacle traps per zone. Left at vanilla (1.0) by default.");

        TentacleTrapMaxSmallAreaMultiplier = BindNonNegative("Expedition Enemies", "Tentacle Trap Max Small Area Multiplier", 1.0f,
            "Multiplies the max tentacle traps allowed in a small area. Left at vanilla (1.0) by default.");

        TentacleTrapMaxMediumAreaMultiplier = BindNonNegative("Expedition Enemies", "Tentacle Trap Max Medium Area Multiplier", 1.0f,
            "Multiplies the max tentacle traps allowed in a medium area. Left at vanilla (1.0) by default.");

        TentacleTrapMaxLargeAreaMultiplier = BindNonNegative("Expedition Enemies", "Tentacle Trap Max Large Area Multiplier", 1.0f,
            "Multiplies the max tentacle traps allowed in a large area. Left at vanilla (1.0) by default.");

        TentacleTrapMaxHugeAreaMultiplier = BindNonNegative("Expedition Enemies", "Tentacle Trap Max Huge Area Multiplier", 1.0f,
            "Multiplies the max tentacle traps allowed in a huge area. Left at vanilla (1.0) by default.");

        ParasiteNestHealthMultiplier = BindNonNegative("Expedition Enemies", "Parasite Nest Health Multiplier", 1.0f,
            "Multiplies max health for parasite nests. Left at vanilla (1.0) by default.");

        ParasiteNestAttackDamageMultiplier = BindNonNegative("Expedition Enemies", "Parasite Nest Attack Damage Multiplier", 1.0f,
            "Multiplies attack damage dealt by parasite nests. Left at vanilla (1.0) by default.");

        ParasiteNestMaxPerZoneMultiplier = BindNonNegative("Expedition Enemies", "Parasite Nest Max Per Zone Multiplier", 1.0f,
            "Multiplies the max number of parasite nests per zone. Left at vanilla (1.0) by default.");

        ParasiteNestMaxSmallAreaMultiplier = BindNonNegative("Expedition Enemies", "Parasite Nest Max Small Area Multiplier", 1.0f,
            "Multiplies the max parasite nests allowed in a small area. Left at vanilla (1.0) by default.");

        ParasiteNestMaxMediumAreaMultiplier = BindNonNegative("Expedition Enemies", "Parasite Nest Max Medium Area Multiplier", 1.0f,
            "Multiplies the max parasite nests allowed in a medium area. Left at vanilla (1.0) by default.");

        ParasiteNestMaxLargeAreaMultiplier = BindNonNegative("Expedition Enemies", "Parasite Nest Max Large Area Multiplier", 1.0f,
            "Multiplies the max parasite nests allowed in a large area. Left at vanilla (1.0) by default.");

        ParasiteNestMaxHugeAreaMultiplier = BindNonNegative("Expedition Enemies", "Parasite Nest Max Huge Area Multiplier", 1.0f,
            "Multiplies the max parasite nests allowed in a huge area. Left at vanilla (1.0) by default.");

        // Internal level-generation tuning, not a gameplay-difficulty stat - exposed for
        // completeness, but retuning it changes how levels get built, not just how hard they are.
        LevelGenVoxelCoverageMultiplier = BindNonNegative("Expedition Level Gen", "Voxel Coverage Area Multiplier", 1.0f,
            "Multiplies an internal level-generation area-coverage factor. Left at vanilla (1.0) by default.");

        LevelGenVoxelCoverageRandomnessMultiplier = BindNonNegative("Expedition Level Gen", "Voxel Coverage Area Randomness Multiplier", 1.0f,
            "Multiplies the randomness factor in that same level-generation coverage scoring. Left at vanilla (1.0) by default.");

        ArtifactsPerSegmentMultiplier = BindNonNegative("Expedition Level Gen", "Artifacts Per Segment Multiplier", 1.0f,
            "Multiplies how many artifacts can spawn per level segment. Left at vanilla (1.0) by default.");

        ArtifactsPerLayerMultiplier = BindNonNegative("Expedition Level Gen", "Artifacts Per Layer Multiplier", 1.0f,
            "Multiplies how many artifacts can spawn per level layer. Left at vanilla (1.0) by default.");

        WeakDoor4x4HealthMultiplier = BindNonNegative("Expedition Doors", "Weak Door 4x4 Health Multiplier", 1.0f,
            "Multiplies the health of small (4x4) weak doors. Left at vanilla (1.0) by default.");

        WeakDoor8x4HealthMultiplier = BindNonNegative("Expedition Doors", "Weak Door 8x4 Health Multiplier", 1.0f,
            "Multiplies the health of large (8x4) weak doors. Left at vanilla (1.0) by default.");

        WeakDoorNoLockWeightMultiplier = BindNonNegative("Expedition Doors", "Weak Door No-Lock Weight Multiplier", 1.0f,
            "Multiplies the odds a weak door spawns with no lock at all. Left at vanilla (1.0) by default.");

        WeakDoorMeleeLockWeightMultiplier = BindNonNegative("Expedition Doors", "Weak Door Melee Lock Weight Multiplier", 1.0f,
            "Multiplies the odds a weak door spawns with a melee-breakable lock. Left at vanilla (1.0) by default.");

        WeakDoorHackableLockWeightMultiplier = BindNonNegative("Expedition Doors", "Weak Door Hackable Lock Weight Multiplier", 1.0f,
            "Multiplies the odds a weak door spawns with a hackable lock. Left at vanilla (1.0) by default.");

        WeakDoorUnlockedOpenChanceMultiplier = BindNonNegative("Expedition Doors", "Weak Door Unlocked Open Chance Multiplier", 1.0f,
            "Multiplies the chance an unlocked weak door starts already open. Left at vanilla (1.0) by default.");

        WeakDoorWallRemoverOpenChanceMultiplier = BindNonNegative("Expedition Doors", "Weak Door Wall Remover Open Chance Multiplier", 1.0f,
            "Multiplies the chance a door starts open where a wall-remover charge was used. Left at vanilla (1.0) by default.");

        WeakDoorLockHealthMultiplier = BindNonNegative("Expedition Doors", "Weak Door Lock Health Multiplier", 1.0f,
            "Multiplies the health of a weak door's lock itself (separate from the door). Left at vanilla (1.0) by default.");

        GlueVolumeToDoorHealthConversionMultiplier = BindNonNegative("Expedition Doors", "Glue Volume To Door Health Conversion Multiplier", 1.0f,
            "Multiplies how effectively C-Foam volume converts into door damage. Left at vanilla (1.0) by default.");

        GlueVolumeForDoorMaxStateMultiplier = BindNonNegative("Expedition Doors", "Glue Volume For Door Max State Multiplier", 1.0f,
            "Multiplies how much C-Foam volume is needed to fully seal a door. Left at vanilla (1.0) by default.");

        // MeleeArchetypeDataBlock: one weapon type per archetype (Bat, Hammer, Knife, Spear),
        // one section per archetype+action-type combination. Every entry defaults to vanilla (1.0).
        foreach (var archetype in BalanceTables.MeleeArchetypes)
        {
            foreach (var stat in BalanceTables.MeleeStatDefs)
            {
                var section = $"Melee - {archetype} - {stat.ActionType}";
                var entry = BindNonNegative(section, stat.DisplayName, 1.0f,
                    $"{stat.Description} Left at vanilla (1.0) by default.");
                MeleeMultipliers[$"{archetype}.{stat.FieldKey}"] = entry;
            }
        }

        // ArchetypeDataBlock: weapon-type tier (shared by every fire mode in that family), then the
        // individual-archetype tier. Both default to vanilla (1.0) and are cumulative.
        foreach (var category in BalanceTables.WeaponTypeCategories)
        {
            foreach (var stat in BalanceTables.WeaponCoreStatDefs)
            {
                var section = $"Weapon Type - {category.Replace('_', ' ')}";
                var entry = BindNonNegative(section, stat.DisplayName, 1.0f,
                    $"{stat.Description} Applies to every fire-mode variant of this weapon type. Left at vanilla (1.0) by default.");
                WeaponTypeMultipliers[$"{category}.{stat.FieldKey}"] = entry;
            }
        }

        foreach (var weapon in BalanceTables.WeaponArchetypes)
        {
            foreach (var stat in BalanceTables.WeaponCoreStatDefs)
            {
                var section = $"Weapon - {weapon.Label}";
                var namePrefix = BalanceTables.WeaponNames.TryGetValue(weapon.ArchetypeId, out var realName)
                    ? $"{realName}. "
                    : "No player weapon uses this fire mode. ";
                var entry = BindNonNegative(section, stat.DisplayName, 1.0f,
                    $"{namePrefix}{stat.Description} Stacks multiplicatively with this weapon's type-level multiplier. Left at vanilla (1.0) by default.");
                WeaponIndividualMultipliers[$"{weapon.ArchetypeId}.{stat.FieldKey}"] = entry;
            }
        }

        WeaponCostOfBulletMultiplier = BindNonNegative("Weapon - Other", "Ammo Cost Per Shot Multiplier", 1.0f,
            "Multiplies how much reserve ammo each shot consumes, for every weapon and sentry. Left at vanilla (1.0) by default.");

        WeaponShotDelayMultiplier = BindNonNegative("Weapon - Other", "Shot Delay Multiplier", 1.0f,
            "Multiplies the delay between shots (fire rate) for every weapon and sentry. Left at vanilla (1.0) by default.");

        WeaponHipFireSpreadMultiplier = BindNonNegative("Weapon - Other", "Hip-Fire Spread Multiplier", 1.0f,
            "Multiplies bullet spread while firing from the hip, for every weapon. Left at vanilla (1.0) by default.");

        WeaponAimSpreadMultiplier = BindNonNegative("Weapon - Other", "Aim Spread Multiplier", 1.0f,
            "Multiplies bullet spread while aiming down sights, for every weapon. Left at vanilla (1.0) by default.");

        WeaponPiercingDamageCountLimitMultiplier = BindNonNegative("Weapon - Other", "Piercing Target Count Multiplier", 1.0f,
            "Multiplies how many enemies a piercing bullet can hit in one shot, for every weapon that pierces. Left at vanilla (1.0) by default.");

        WeaponBurstDelayMultiplier = BindNonNegative("Weapon - Other", "Burst Delay Multiplier", 1.0f,
            "Multiplies the delay between shots within a burst, for every burst-fire weapon. Left at vanilla (1.0) by default.");

        WeaponBurstShotCountMultiplier = BindNonNegative("Weapon - Other", "Burst Shot Count Multiplier", 1.0f,
            "Multiplies how many rounds fire per burst, for every burst-fire weapon. Left at vanilla (1.0) by default.");

        WeaponShotgunBulletCountMultiplier = BindNonNegative("Weapon - Other", "Shotgun Pellet Count Multiplier", 1.0f,
            "Multiplies how many pellets a shotgun blast fires, for every shotgun. Left at vanilla (1.0) by default.");

        WeaponShotgunConeSizeMultiplier = BindNonNegative("Weapon - Other", "Shotgun Cone Size Multiplier", 1.0f,
            "Multiplies the spread cone size of a shotgun blast, for every shotgun. Left at vanilla (1.0) by default.");

        WeaponShotgunBulletSpreadMultiplier = BindNonNegative("Weapon - Other", "Shotgun Pellet Spread Multiplier", 1.0f,
            "Multiplies how spread out individual pellets are within a shotgun blast. Left at vanilla (1.0) by default.");

        WeaponSpecialChargetupTimeMultiplier = BindNonNegative("Weapon - Other", "Special Charge-Up Time Multiplier", 1.0f,
            "Multiplies charge-up time for weapons with a special charged shot. Left at vanilla (1.0) by default.");

        WeaponSpecialCooldownTimeMultiplier = BindNonNegative("Weapon - Other", "Special Cooldown Time Multiplier", 1.0f,
            "Multiplies cooldown time for weapons with a special charged shot. Left at vanilla (1.0) by default.");

        WeaponSpecialSemiBurstCountTimeoutMultiplier = BindNonNegative("Weapon - Other", "Semi-Burst Timeout Multiplier", 1.0f,
            "Multiplies the window to chain shots into a semi-burst, for weapons with that firing mode. Left at vanilla (1.0) by default.");

        WeaponEquipTransitionTimeMultiplier = BindNonNegative("Weapon - Other", "Equip Transition Time Multiplier", 1.0f,
            "Multiplies how long it takes to raise a weapon after switching to it. Left at vanilla (1.0) by default.");

        WeaponAimTransitionTimeMultiplier = BindNonNegative("Weapon - Other", "Aim Transition Time Multiplier", 1.0f,
            "Multiplies how long it takes to raise sights when aiming down sights. Left at vanilla (1.0) by default.");

        WeaponShellCasingSizeMultiplier = BindNonNegative("Weapon - Other", "Shell Casing Size Multiplier", 1.0f,
            "Multiplies the visual size of ejected shell casings. Purely cosmetic. Left at vanilla (1.0) by default.");

        SentryStartFireDelayMultiplier = BindNonNegative("Sentries", "Start Fire Delay Multiplier", 1.0f,
            "Multiplies how long a sentry waits after detecting a target before it opens fire. Left at vanilla (1.0) by default.");

        SentryRotationSpeedMultiplier = BindNonNegative("Sentries", "Rotation Speed Multiplier", 1.0f,
            "Multiplies how fast a sentry turns to track targets. Left at vanilla (1.0) by default.");

        SentryDetectionMaxRangeMultiplier = BindNonNegative("Sentries", "Detection Max Range Multiplier", 1.0f,
            "Multiplies the maximum range at which a sentry can detect targets. Left at vanilla (1.0) by default.");

        SentryDetectionMaxAngleMultiplier = BindNonNegative("Sentries", "Detection Max Angle Multiplier", 1.0f,
            "Multiplies the detection cone angle a sentry scans. Left at vanilla (1.0) by default.");

        SentryLongRangeThresholdMultiplier = BindNonNegative("Sentries", "Long Range Threshold Multiplier", 1.0f,
            "Multiplies the distance beyond which a sentry treats a target as long-range. Left at vanilla (1.0) by default.");

        SentryShortRangeThresholdMultiplier = BindNonNegative("Sentries", "Short Range Threshold Multiplier", 1.0f,
            "Multiplies the distance within which a sentry treats a target as short-range. Left at vanilla (1.0) by default.");

        SentryStartFireDelayTagMultiplier = BindNonNegative("Sentries", "Tagged-Target Start Fire Delay Multiplier", 1.0f,
            "Multiplies the start-fire-delay multiplier applied against a tagged (scanned) target. Left at vanilla (1.0) by default.");

        SentryRotationSpeedTagMultiplier = BindNonNegative("Sentries", "Tagged-Target Rotation Speed Multiplier", 1.0f,
            "Multiplies the rotation-speed multiplier applied against a tagged (scanned) target. Left at vanilla (1.0) by default.");

        SentryDamageTagMultiplier = BindNonNegative("Sentries", "Tagged-Target Damage Multiplier", 1.0f,
            "Multiplies the damage multiplier applied against a tagged (scanned) target. Left at vanilla (1.0) by default.");

        SentryStaggerDamageTagMultiplier = BindNonNegative("Sentries", "Tagged-Target Stagger Multiplier", 1.0f,
            "Multiplies the stagger multiplier applied against a tagged (scanned) target. Left at vanilla (1.0) by default.");

        SentryCostOfBulletTagMultiplier = BindNonNegative("Sentries", "Tagged-Target Ammo Cost Multiplier", 1.0f,
            "Multiplies the ammo-cost multiplier applied against a tagged (scanned) target. Left at vanilla (1.0) by default.");

        SentryShotDelayTagMultiplier = BindNonNegative("Sentries", "Tagged-Target Shot Delay Multiplier", 1.0f,
            "Multiplies the shot-delay multiplier applied against a tagged (scanned) target. Left at vanilla (1.0) by default.");

        SentryDeployPickupInteractionDurationMultiplier = BindNonNegative("Sentries", "Deploy/Pickup Interaction Duration Multiplier", 0.0f,
            "Multiplies how long you have to hold the interact key to place or retrieve a sentry gun. Default 0 " +
            "(instant) - no charge-up to place, no wait to pick back up. Set to 1.0 to keep vanilla timing.");

        SentriesPassThrough = Config.Bind("Sentries", "Pass Through Each Other", true,
            "On: placed sentries never block each other or anyone's bullets - a sentry shoots and sees straight " +
            "through another one, players' and bots' bullets pass through them, and two can stand overlapping. " +
            "Picking one up still works as usual. Off: vanilla sentry bodies.");

        TerminalPingAnywhere = Config.Bind("Experimental", "Ping Anywhere", true,
            "PING <KEY> from any terminal reaches an item anywhere in the level, not just in the terminal's own zone " +
            "(the marker shows for 10 s). Same-zone pings and unknown keys are left to the game's own PING, and any " +
            "error falls back to it too. Technique credit: BetterTerminal by AlexanderMakunin (MIT). Set to false if a " +
            "terminal misbehaves.");

        BotsDeployOnShiftE = Config.Bind("Experimental", "Bots Deploy On Shift+E (SPIKE)", true,
            "G.EZ-T34 spike: while you're aiming your own equipped tool at a valid placement spot (the normal " +
            "green ghost/arrow), Shift+E deploys the first bot with an undeployed tool there instead, reusing " +
            "that same aim - your own plain-E placement is untouched. Calls the bot AI's own placement method " +
            "directly since there's no vanilla order for 'deploy now' - only confirmed to compile, not confirmed " +
            "safe by decompiling alone. This switch also covers the other experimental sentry keys - Z (recall " +
            "bot tools) and X (place your own) - so false turns all of them off. On by default to test live.");

        FogClearEnabled = Config.Bind("Fog Clearing", "Fog Clearing Enabled", true,
            "Keeps a silent, invisible fog-clearing sphere around you - the same fog-system sphere the Fog Repeller " +
            "item uses, without its light, sound or time limit. It widens the longer you're in a level.");

        FogClearStartRange = BindNonNegative("Fog Clearing", "Start Range (meters)", 4f,
            "Radius of clear air around you when a level starts. Default 4.");

        FogClearMaxRange = BindNonNegative("Fog Clearing", "Max Range (meters)", 25f,
            "Radius it grows to. Default 25 (the vanilla Fog Repeller's own range is logged on first use, for comparison).");

        FogClearRampSeconds = BindNonNegative("Fog Clearing", "Ramp Seconds", 90f,
            "Seconds from Start Range to Max Range. Default 90. 0 = full range straight away.");

        FogClearDensityOverride = Config.Bind("Fog Clearing", "Density Override", 0f,
            "0 (default) copies the vanilla Fog Repeller's own density. Set a value only if fog doesn't clear - " +
            "LogOutput.log shows the density in use.");
    }

    // Every float setting here is a multiplier, range, duration or percentage, none of which mean
    // anything below 0 - a negative one turned into negative health, damage or spawn counts. BepInEx
    // clamps a value it reads from the .cfg to this, so a typo'd minus sign lands on 0 instead.
    // Density Override is the one exception (a fog-repeller density is negative), bound directly.
    private ConfigEntry<float> BindNonNegative(string section, string key, float defaultValue, string description) =>
        Config.Bind(section, key, defaultValue, new ConfigDescription(description, NonNegative.Instance));

    private sealed class NonNegative : AcceptableValueBase
    {
        internal static readonly NonNegative Instance = new();

        private NonNegative() : base(typeof(float)) { }

        // !(f >= 0) also catches NaN.
        public override object Clamp(object value) => value is float f && !(f >= 0f) ? 0f : value;
        public override bool IsValid(object value) => value is float f && f >= 0f;
        public override string ToDescriptionString() => "# Acceptable value range: 0 or more";
    }
}
