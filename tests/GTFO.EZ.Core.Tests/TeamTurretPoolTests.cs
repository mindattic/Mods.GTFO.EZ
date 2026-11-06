using System.IO;
using System.Runtime.CompilerServices;
using GTFO.EZ.Core;
using NUnit.Framework;

namespace GTFO.EZ.Core.Tests;

// G.EZ-T120: the shared team turret pool. Entry 0 is your own sentry, 1.. the bots' (host order).
// The game can't run headless, so the decisions live in GTFO.EZ.Core and the plugin only gathers inputs.
[TestFixture]
public class TeamTurretPoolTests
{
    private static bool[] Av(params bool[] a) => a;

    [Test]
    public void Next_CyclesOnlyAvailable_Wrapping()
    {
        var available = Av(true, false, true, true); // yours, bot1 (down), bot2, bot3
        Assert.That(TeamTurretPool.Next(available, 0), Is.EqualTo(2));
        Assert.That(TeamTurretPool.Next(available, 2), Is.EqualTo(3));
        Assert.That(TeamTurretPool.Next(available, 3), Is.EqualTo(0));
    }

    [Test]
    public void Next_FromNothingSelected_StartsAtYourOwn() =>
        Assert.That(TeamTurretPool.Next(Av(true, true), TeamTurretPool.None), Is.EqualTo(0));

    [Test]
    public void Next_OnlyOneLeft_StaysOnIt() =>
        Assert.That(TeamTurretPool.Next(Av(false, true, false), 1), Is.EqualTo(1));

    [Test]
    public void Next_NothingLeft_IsNone() =>
        Assert.That(TeamTurretPool.Next(Av(false, false, false), 1), Is.EqualTo(TeamTurretPool.None));

    [Test]
    public void Resolve_KeepsAnAvailableSelection() =>
        Assert.That(TeamTurretPool.Resolve(Av(true, true, true), 1), Is.EqualTo(1));

    // "if they lay bot1's turret and I have bot1's turret equipped it automatically switches to bot2 or my turret depending on order"
    [Test]
    public void Resolve_SomeoneElsePlacedMySelection_AdvancesToTheNext()
    {
        Assert.That(TeamTurretPool.Resolve(Av(true, false, true), 1), Is.EqualTo(2));
        Assert.That(TeamTurretPool.Resolve(Av(true, true, false), 2), Is.EqualTo(0));
    }

    [Test]
    public void Resolve_EverythingDown_IsNone() =>
        Assert.That(TeamTurretPool.Resolve(Av(false, false), 0), Is.EqualTo(TeamTurretPool.None));

    // 2 players + 2 bots: each player's pool is their own + both bots; 4 turrets, never a fifth.
    [Test]
    public void TwoPlayersTwoBots_SharedBotPool_NeverMoreThanFourPlaced()
    {
        var botDown = new bool[2];
        var ownDown = new bool[2]; // player A, player B
        var placed = 0;

        bool[] PoolOf(int player)
        {
            var bots = (botDown[0] ? 0 : 1) + (botDown[1] ? 0 : 1);
            return new[] { TeamTurretPool.OwnSelectable(!ownDown[player], bots), !botDown[0], !botDown[1] };
        }

        // Both players select bot1 (entry 1) and press E in the same frame: the host takes them one
        // at a time, first wins.
        var requests = new[] { TeamTurretPool.Resolve(PoolOf(0), 1), TeamTurretPool.Resolve(PoolOf(1), 1) };
        Assert.That(requests, Is.EqualTo(new[] { 1, 1 }));
        foreach (var sel in requests)
            if (TeamTurretPool.HostAccepts(true, true, botDown[sel - 1])) { botDown[sel - 1] = true; placed++; }
        Assert.That(placed, Is.EqualTo(1), "the second request for bot1 must be refused");

        // Now everyone keeps pressing E on whatever their selection resolves to until nothing is left.
        for (var round = 0; round < 10; round++)
            foreach (var player in new[] { 0, 1 })
            {
                var sel = TeamTurretPool.Resolve(PoolOf(player), 1);
                if (sel == TeamTurretPool.None) continue;
                if (sel == TeamTurretPool.Own)
                {
                    Assert.That(botDown, Is.All.True, "your own only ever goes down last");
                    ownDown[player] = true;
                    placed++;
                }
                else if (TeamTurretPool.HostAccepts(true, true, botDown[sel - 1])) { botDown[sel - 1] = true; placed++; }
            }

        Assert.That(placed, Is.EqualTo(4));
        Assert.That(TeamTurretPool.CountAvailable(PoolOf(0)) + TeamTurretPool.CountAvailable(PoolOf(1)), Is.Zero);
    }

    [Test]
    public void HostAccepts_OnlyAnUndeployedSentry()
    {
        Assert.That(TeamTurretPool.HostAccepts(true, true, false), Is.True);
        Assert.That(TeamTurretPool.HostAccepts(true, true, true), Is.False);
        Assert.That(TeamTurretPool.HostAccepts(true, false, false), Is.False, "a bot with a mine deployer/bio tracker");
        Assert.That(TeamTurretPool.HostAccepts(false, true, false), Is.False, "a bot that left");
    }

    [Test]
    public void ClientBotAvailable_NeedsFreshHostState_AndNoPendingRequest()
    {
        Assert.That(TeamTurretPool.ClientBotAvailable(true, false, false), Is.True);
        Assert.That(TeamTurretPool.ClientBotAvailable(false, false, false), Is.False, "host without the mod, or stale");
        Assert.That(TeamTurretPool.ClientBotAvailable(true, true, false), Is.False);
        Assert.That(TeamTurretPool.ClientBotAvailable(true, false, true), Is.False, "already asked the host for it");
    }

    [Test]
    public void QuickDrop_BotsFirstThenOwn()
    {
        Assert.That(TeamTurretPool.QuickDropPick(Av(true, false, true)), Is.EqualTo(2));
        Assert.That(TeamTurretPool.QuickDropPick(Av(true, false, false)), Is.EqualTo(0));
        Assert.That(TeamTurretPool.QuickDropPick(Av(false, false)), Is.EqualTo(TeamTurretPool.None));
    }

    [Test]
    public void ShiftE_NeverPicksOwn()
    {
        Assert.That(TeamTurretPool.BotPick(Av(true, true, true), 2), Is.EqualTo(2));
        Assert.That(TeamTurretPool.BotPick(Av(true, true, true), 0), Is.EqualTo(1));
        Assert.That(TeamTurretPool.BotPick(Av(true, false, false), 0), Is.EqualTo(TeamTurretPool.None));
    }

    [Test]
    public void ToolKey_HeldSentry_Cycles_ButNotOnThePressThatWieldsIt()
    {
        Assert.That(TeamTurretPool.DecideToolKey(false, true, true), Is.EqualTo(ToolKeyAction.Cycle));
        Assert.That(TeamTurretPool.DecideToolKey(false, true, false), Is.EqualTo(ToolKeyAction.None));
        Assert.That(TeamTurretPool.DecideToolKey(false, false, false), Is.EqualTo(ToolKeyAction.None));
    }

    [Test]
    public void ToolKey_AbsurdWeaponsCyclingOn_StandsDown() =>
        Assert.That(TeamTurretPool.DecideToolKey(true, true, true), Is.EqualTo(ToolKeyAction.None));

    // "you always have to set down your own turret last"
    [Test]
    public void Own_SelectableOnlyOnceNoBotTurretIsLeft()
    {
        Assert.That(TeamTurretPool.OwnSelectable(ownUndeployed: true, availableBotTurrets: 2), Is.False);
        Assert.That(TeamTurretPool.OwnSelectable(true, 0), Is.True);
        Assert.That(TeamTurretPool.OwnSelectable(false, 0), Is.False);
    }

    // Someone else places the last bot turret you had selected: you fall through to your own.
    [Test]
    public void Resolve_LastBotTakenElsewhere_FallsToOwn()
    {
        var botsLeft = 0;
        var available = Av(TeamTurretPool.OwnSelectable(true, botsLeft), false, false);
        Assert.That(TeamTurretPool.Resolve(available, 2), Is.EqualTo(TeamTurretPool.Own));
    }
}

[TestFixture]
public class TeamSentryRulesTests
{
    private const float LongAgo = 10f;

    [Test]
    public void SelectedBot_PlacesTheBotsAndKeepsOwnInHand() =>
        Assert.That(TeamSentryRules.Decide(false, LongAgo, selectedIsBot: true), Is.EqualTo(TeamSentryPlacement.PlaceBotInstead));

    [Test]
    public void SelectedOwn_PlacesOwn_EvenWithBotsLeft() =>
        Assert.That(TeamSentryRules.Decide(false, LongAgo, selectedIsBot: false), Is.EqualTo(TeamSentryPlacement.PlaceOwn));

    [Test]
    public void ShiftE_NeverPlacesOwn() =>
        Assert.That(TeamSentryRules.Decide(shiftEActive: true, LongAgo, false), Is.EqualTo(TeamSentryPlacement.HoldOwn));

    [Test]
    public void HeldE_InsideCooldown_PlacesNothing()
    {
        Assert.That(TeamSentryRules.Decide(false, 0.1f, true), Is.EqualTo(TeamSentryPlacement.HoldOwn));
        Assert.That(TeamSentryRules.Decide(false, 0.1f, false), Is.EqualTo(TeamSentryPlacement.HoldOwn));
    }
}

// The shipped plugin source keeps the no-duplicates rules wired in (the plugin can't load headless).
[TestFixture]
public class TeamTurretWiringTests
{
    private static string SrcDir([CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "src", "GTFO.EZ"));

    // G.EZ-T111's variant cycling built extra sentries through EquipLocalGear; it's gone, and nothing
    // in the plugin may equip local gear again.
    [Test]
    public void NoPluginCodeEquipsLocalGear()
    {
        Assert.That(File.Exists(Path.Combine(SrcDir(), "Patches", "MultiTurretTest.cs")), Is.False);
        foreach (var file in Directory.GetFiles(SrcDir(), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")) continue;
            Assert.That(File.ReadAllText(file), Does.Not.Contain("EquipLocalGear("), file);
        }
    }

    [Test]
    public void BotPlacement_GoesThroughHostAccepts() =>
        Assert.That(File.ReadAllText(Path.Combine(SrcDir(), "Patches", "TeamTurrets.cs")), Does.Contain("TeamTurretPool.HostAccepts("));
}
