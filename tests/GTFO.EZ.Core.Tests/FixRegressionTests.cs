using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using GTFO.EZ.Core;
using NUnit.Framework;

namespace GTFO.EZ.Core.Tests;

// G.EZ-T117: headless checks that G.EZ-T115 (levels dark again) and G.EZ-T116 (multi-sentry quick
// deploy) landed. The game itself can't run here, so the decision logic both fixes use lives in
// GTFO.EZ.Core and is tested directly; the plugin only gathers the inputs from the game.
[TestFixture]
public class ConfigMigrationRulesTests
{
    // G.EZ-T115's v6 migration: Fog Clearing Enabled shipped true, now defaults false.
    [Test]
    public void FogClearing_StillOnOldDefault_IsSwitchedOff() =>
        Assert.That(ConfigMigrationRules.ShouldMigrate(reachedVersion: 5, migrationVersion: 6, currentValue: true, oldDefault: true), Is.True);

    [Test]
    public void FogClearing_AlreadyOff_IsLeftAlone() =>
        Assert.That(ConfigMigrationRules.ShouldMigrate(5, 6, false, true), Is.False);

    [Test]
    public void FogClearing_PlayerTurnedBackOnAfterMigrating_IsNotReMigrated() =>
        Assert.That(ConfigMigrationRules.ShouldMigrate(reachedVersion: 6, migrationVersion: 6, currentValue: true, oldDefault: true), Is.False);

    [Test]
    public void FreshInstall_RunsEveryMigration() =>
        Assert.That(ConfigMigrationRules.ShouldMigrate(0, 6, true, true), Is.True);

    [Test]
    public void FloatMigrations_StillBehaveTheSame()
    {
        Assert.That(ConfigMigrationRules.ShouldMigrate(4, 5, 1.0f, 1.0f), Is.True);
        Assert.That(ConfigMigrationRules.ShouldMigrate(4, 5, 0.3f, 1.0f), Is.False);
        Assert.That(ConfigMigrationRules.ShouldMigrate(5, 5, 1.0f, 1.0f), Is.False);
    }
}

[TestFixture]
public class TeamSentryRulesTests
{
    private const float LongAgo = 10f;

    [Test]
    public void Host_WithBotTurretsLeft_PlacesABotsTurretAndKeepsOwnInHand() =>
        Assert.That(TeamSentryRules.Decide(false, LongAgo, isHost: true, wieldingTool: true, undeployedBotSentries: 3),
            Is.EqualTo(TeamSentryPlacement.PlaceBotInstead));

    [Test]
    public void Host_AllBotTurretsDown_PlacesOwnLast() =>
        Assert.That(TeamSentryRules.Decide(false, LongAgo, true, true, 0), Is.EqualTo(TeamSentryPlacement.PlaceOwn));

    [Test]
    public void ShiftE_NeverPlacesOwn() =>
        Assert.That(TeamSentryRules.Decide(shiftEActive: true, LongAgo, true, true, 0), Is.EqualTo(TeamSentryPlacement.HoldOwn));

    [Test]
    public void HeldE_InsideCooldown_PlacesNothing()
    {
        Assert.That(TeamSentryRules.Decide(false, 0.1f, true, true, 2), Is.EqualTo(TeamSentryPlacement.HoldOwn));
        // ...including your own right after the last bot's went down.
        Assert.That(TeamSentryRules.Decide(false, 0.1f, true, true, 0), Is.EqualTo(TeamSentryPlacement.HoldOwn));
    }

    [Test]
    public void Client_PlacesOnlyOwn() =>
        Assert.That(TeamSentryRules.Decide(false, LongAgo, isHost: false, true, 3), Is.EqualTo(TeamSentryPlacement.PlaceOwn));

    // The whole sequence the user asked for: 3 bots + you = 4 presses, slot 4 kept until the last.
    [Test]
    public void FourPressSequence_ThreeBotsThenOwn()
    {
        var bots = 3;
        var t = 0f;
        var lastTeamPlace = -10f;
        var placements = new List<TeamSentryPlacement>();
        for (var press = 0; press < 4; press++, t += 1f)
        {
            var d = TeamSentryRules.Decide(false, t - lastTeamPlace, true, true, bots);
            placements.Add(d);
            if (d == TeamSentryPlacement.PlaceBotInstead) { bots--; lastTeamPlace = t; }
        }

        Assert.That(placements, Is.EqualTo(new[]
        {
            TeamSentryPlacement.PlaceBotInstead, TeamSentryPlacement.PlaceBotInstead,
            TeamSentryPlacement.PlaceBotInstead, TeamSentryPlacement.PlaceOwn,
        }));
    }
}

// Guards that the shipped plugin source still wires the fixes in - the plugin project can't be
// loaded outside the game, so these read Plugin.cs / DeployPickupHotkeys.cs directly.
[TestFixture]
public class PluginWiringTests
{
    private static string Src(string relative, [CallerFilePath] string here = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "src", "GTFO.EZ", relative)));

    [Test]
    public void FogClearing_DefaultsOff() =>
        Assert.That(Regex.IsMatch(Src("Plugin.cs"), @"""Fog Clearing Enabled"",\s*false"), Is.True);

    [Test]
    public void FogClearing_HasItsV6Migration() =>
        Assert.That(Regex.IsMatch(Src("Plugin.cs"), @"\(\s*6\s*,\s*\(\)\s*=>\s*FogClearEnabled\s*,\s*true\s*\)"), Is.True);

    [Test]
    public void SentryPlacement_UsesTeamSentryRules() =>
        Assert.That(Src(Path.Combine("Patches", "DeployPickupHotkeys.cs")), Does.Contain("TeamSentryRules.Decide("));
}
