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

// G.EZ-T119: a huge multiplier saturates instead of wrapping to a negative stat.
[TestFixture]
public class BalanceMathOverflowTests
{
    [Test]
    public void ScaleIntUp_HugeMultiplier_SaturatesNotNegative() =>
        Assert.That(BalanceMath.ScaleIntUp(460, 1e7f), Is.EqualTo(int.MaxValue));

    [Test]
    public void ScaleIntDown_HugeMultiplier_SaturatesNotNegative() =>
        Assert.That(BalanceMath.ScaleIntDown(460, 1e7f), Is.EqualTo(int.MaxValue));
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

// G.EZ-T121: artifact colors. Vanilla-like odds (blue far ahead) become an even split with the same total.
[TestFixture]
public class ArtifactOddsTests
{
    [Test]
    public void Even_SplitsTheTotalEquallyAcrossColors()
    {
        var (m, b, a) = ArtifactOdds.Rebalance(8f, 3f, 1f, even: true, 1f, 1f, 1f);
        Assert.That((m, b, a), Is.EqualTo((4f, 4f, 4f)));
    }

    [Test]
    public void Off_KeepsVanillaWeights() =>
        Assert.That(ArtifactOdds.Rebalance(8f, 3f, 1f, even: false, 1f, 1f, 1f), Is.EqualTo((8f, 3f, 1f)));

    [Test]
    public void ColorMultipliers_ApplyAfterEvening() =>
        Assert.That(ArtifactOdds.Rebalance(8f, 3f, 1f, true, 1f, 1f, 2f), Is.EqualTo((4f, 4f, 8f)));
}
