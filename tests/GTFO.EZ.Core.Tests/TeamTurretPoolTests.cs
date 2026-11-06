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
    public void ClientBotAvailable_NeedsLiveHostPool_AndNoPendingRequest()
    {
        Assert.That(TeamTurretPool.ClientBotAvailable(HostPoolStatus.Live, false, false, false), Is.True);
        Assert.That(TeamTurretPool.ClientBotAvailable(HostPoolStatus.Unknown, false, false, false), Is.False, "stale or not yet heard");
        Assert.That(TeamTurretPool.ClientBotAvailable(HostPoolStatus.Off, false, false, false), Is.False, "host without the pool");
        Assert.That(TeamTurretPool.ClientBotAvailable(HostPoolStatus.Live, true, false, false), Is.False);
        Assert.That(TeamTurretPool.ClientBotAvailable(HostPoolStatus.Live, false, true, false), Is.False, "sitting out a failed placement");
        Assert.That(TeamTurretPool.ClientBotAvailable(HostPoolStatus.Live, false, false, true), Is.False, "already asked the host for it");
    }

    // G.EZ-T124 (a): no word from the host is "unknown", never "0 bot turrets".
    [Test]
    public void PoolStatus_NothingYet_IsUnknown_ThenOffAfterTheGrace()
    {
        Assert.That(TeamTurretPool.ClientPoolStatus(false, 999f, false, secondsInLevel: 0.5f), Is.EqualTo(HostPoolStatus.Unknown));
        Assert.That(TeamTurretPool.ClientPoolStatus(false, 999f, false, TeamTurretPool.NoHostPoolSeconds), Is.EqualTo(HostPoolStatus.Off), "a host without the pool (no GTFO.EZ, older build)");
    }

    [Test]
    public void PoolStatus_Fresh_IsLive_Stale_IsUnknown_LongGone_IsOff()
    {
        Assert.That(TeamTurretPool.ClientPoolStatus(true, 0.5f, true, 60f), Is.EqualTo(HostPoolStatus.Live));
        Assert.That(TeamTurretPool.ClientPoolStatus(true, TeamTurretPool.HostStateFreshSeconds, true, 60f), Is.EqualTo(HostPoolStatus.Unknown));
        Assert.That(TeamTurretPool.ClientPoolStatus(true, TeamTurretPool.NoHostPoolSeconds, true, 60f), Is.EqualTo(HostPoolStatus.Off));
    }

    [Test]
    public void PoolStatus_HostSaysOff_IsOff() =>
        Assert.That(TeamTurretPool.ClientPoolStatus(true, 0.1f, false, 1f), Is.EqualTo(HostPoolStatus.Off));

    // Unknown: your own is held (own-last) - even with no bot turret visible.
    [Test]
    public void Own_NotSelectableWhilePoolUnknown_SelectableWhenHostHasNoPool()
    {
        Assert.That(TeamTurretPool.OwnSelectable(true, 0, HostPoolStatus.Unknown), Is.False);
        Assert.That(TeamTurretPool.OwnSelectable(true, 0, HostPoolStatus.Off), Is.True);
        Assert.That(TeamTurretPool.OwnSelectable(false, 0, HostPoolStatus.Off), Is.False);
        Assert.That(TeamTurretPool.OwnSelectable(true, 2, HostPoolStatus.Live), Is.False);
    }

    [Test]
    public void PoolUnknown_NothingSelectable_SoXDropsNothing()
    {
        var unknown = Av(TeamTurretPool.OwnSelectable(true, 0, HostPoolStatus.Unknown),
            TeamTurretPool.ClientBotAvailable(HostPoolStatus.Unknown, false, false, false));
        Assert.That(TeamTurretPool.QuickDropPick(unknown), Is.EqualTo(TeamTurretPool.None));
        Assert.That(TeamTurretPool.Resolve(unknown, 1), Is.EqualTo(TeamTurretPool.None));
    }

    // (b) a late state can't put a placed turret back; sequence numbers wrap safely.
    [Test]
    public void IsNewerState_OnlyNewer_WrapSafe()
    {
        Assert.That(TeamTurretPool.IsNewerState(false, 0, 5), Is.True, "the first one");
        Assert.That(TeamTurretPool.IsNewerState(true, 5, 6), Is.True);
        Assert.That(TeamTurretPool.IsNewerState(true, 6, 5), Is.False);
        Assert.That(TeamTurretPool.IsNewerState(true, 6, 6), Is.False);
        Assert.That(TeamTurretPool.IsNewerState(true, uint.MaxValue, 1), Is.True, "wrapped");
    }

    // The client sets its floor to (answer's state seq - 1): that state is taken, anything older isn't.
    [Test]
    public void ResultFloor_DropsOlderStates_KeepsTheAnsweringOne()
    {
        const uint answerSeq = 42;
        var floor = answerSeq - 1;
        Assert.That(TeamTurretPool.IsNewerState(true, floor, 40), Is.False, "sent before the placement, arriving late");
        Assert.That(TeamTurretPool.IsNewerState(true, floor, answerSeq), Is.True);
    }

    [Test]
    public void Request_TimesOutOnlyAfterTheGiveUpTime()
    {
        Assert.That(TeamTurretPool.RequestTimedOut(2.5f), Is.False, "the old 2 s window no longer clears it");
        Assert.That(TeamTurretPool.RequestTimedOut(TeamTurretPool.ClientRequestTimeoutSeconds), Is.True);
    }

    // (c) reason codes.
    [Test]
    public void HostDecide_ReasonCodes()
    {
        Assert.That(TeamTurretPool.HostDecide(true, true, true, false, false), Is.EqualTo(TurretPlaceResult.Placed));
        Assert.That(TeamTurretPool.HostDecide(true, true, true, true, false), Is.EqualTo(TurretPlaceResult.Taken));
        Assert.That(TeamTurretPool.HostDecide(true, true, true, false, true), Is.EqualTo(TurretPlaceResult.Failed), "sitting out");
        Assert.That(TeamTurretPool.HostDecide(true, false, true, false, false), Is.EqualTo(TurretPlaceResult.Failed), "bot gone");
        Assert.That(TeamTurretPool.HostDecide(true, true, false, false, false), Is.EqualTo(TurretPlaceResult.Failed), "no sentry");
        Assert.That(TeamTurretPool.HostDecide(false, true, true, false, false), Is.EqualTo(TurretPlaceResult.Disabled));
    }

    [Test]
    public void HostDecide_NeverPlacesWhatHostAcceptsRefuses()
    {
        foreach (var found in new[] { false, true })
        foreach (var sentry in new[] { false, true })
        foreach (var deployed in new[] { false, true })
        foreach (var cooling in new[] { false, true })
            if (TeamTurretPool.HostDecide(true, found, sentry, deployed, cooling) == TurretPlaceResult.Placed)
                Assert.That(TeamTurretPool.HostAccepts(found, sentry, deployed), Is.True);
    }

    [Test]
    public void Refused_SaysWhy()
    {
        Assert.That(TeamTurretMessages.Refused(TurretPlaceResult.Taken, "Woods's turret", 5f), Is.EqualTo("Someone else got Woods's turret first"));
        Assert.That(TeamTurretMessages.Refused(TurretPlaceResult.Failed, "Woods's turret", 5f), Is.EqualTo("Woods's turret couldn't be placed - out of the pool for 5 s"));
        Assert.That(TeamTurretMessages.Refused(TurretPlaceResult.Disabled, "Woods's turret", 5f), Does.Contain("switched off"));
    }

    // (d) the team-wide lines.
    [Test]
    public void TeamMessages_SayWhoAndHowManyLeft()
    {
        Assert.That(TeamTurretMessages.Placed("Erin", "Woods's turret", 2), Is.EqualTo("Erin placed Woods's turret - 2 bot turrets left"));
        Assert.That(TeamTurretMessages.Placed("Erin", "Woods's turret", 1), Does.EndWith("1 bot turret left"));
        Assert.That(TeamTurretMessages.Placed("Erin", "Woods's turret", 0), Does.EndWith("no bot turrets left"));
        Assert.That(TeamTurretMessages.Recalled("Ryan", 2, true, 3), Is.EqualTo("Ryan recalled 2 bot turrets and their own - 3 bot turrets left"));
        Assert.That(TeamTurretMessages.Recalled("You", 0, true, 0, actorIsYou: true), Is.EqualTo("You recalled your own turret - no bot turrets left"));
        Assert.That(TeamTurretMessages.Unknown, Is.EqualTo("Team turret pool unavailable (host)"));
    }

    // (e) a recalled bot turret comes back into the pool - but never while it's still standing.
    [Test]
    public void ClearDeployedAfterRecall_OnlyWhenTheTurretIsGone()
    {
        Assert.That(TeamTurretPool.ClearDeployedAfterRecall(true, false, TeamTurretPool.RecallVerifySeconds), Is.True);
        Assert.That(TeamTurretPool.ClearDeployedAfterRecall(true, true, 5f), Is.False, "still standing: clearing would allow a duplicate");
        Assert.That(TeamTurretPool.ClearDeployedAfterRecall(true, false, 0.1f), Is.False, "too soon to tell");
        Assert.That(TeamTurretPool.ClearDeployedAfterRecall(false, false, 5f), Is.False, "already back");
    }

    [Test]
    public void SentryRules_PoolUnknown_HoldsOwn()
    {
        Assert.That(TeamSentryRules.Decide(false, 10f, selectedIsBot: false, poolUnknown: true), Is.EqualTo(TeamSentryPlacement.HoldOwn));
        Assert.That(TeamSentryRules.Decide(false, 10f, selectedIsBot: false, poolUnknown: false), Is.EqualTo(TeamSentryPlacement.PlaceOwn));
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
    public void BotPlacement_GoesThroughHostAccepts()
    {
        var src = File.ReadAllText(Path.Combine(SrcDir(), "Patches", "TeamTurrets.cs"));
        Assert.That(src, Does.Contain("TeamTurretPool.HostAccepts("));
        Assert.That(src, Does.Contain("TeamTurretPool.HostDecide("));
    }

    // G.EZ-T124: pool states are sequence-checked, and a level's state never carries into the next.
    [Test]
    public void ClientState_IsSequenceChecked_AndResetPerLevel()
    {
        var src = File.ReadAllText(Path.Combine(SrcDir(), "Patches", "TeamTurrets.cs"));
        Assert.That(src, Does.Contain("TeamTurretPool.IsNewerState("));
        Assert.That(src, Does.Contain("ResetClient();"));
        Assert.That(src, Does.Contain("TeamTurretPool.ClientPoolStatus("));
    }

    [Test]
    public void Recall_ChecksTheBotIsBackInThePool() =>
        Assert.That(File.ReadAllText(Path.Combine(SrcDir(), "Patches", "DeployPickupHotkeys.cs")), Does.Contain("TeamTurretPool.ClearDeployedAfterRecall("));
}
