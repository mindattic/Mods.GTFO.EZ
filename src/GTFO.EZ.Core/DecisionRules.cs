namespace GTFO.EZ.Core;

/// <summary>
/// One-time config default migrations (Plugin.ApplyConfigDefaultMigrations). A setting is moved to
/// its new default only when the install hasn't reached the migration's version yet AND the value
/// is still the old default - a player's own choice is never overwritten. G.EZ-T117.
/// </summary>
public static class ConfigMigrationRules
{
    public static bool ShouldMigrate<T>(int reachedVersion, int migrationVersion, T currentValue, T oldDefault) =>
        reachedVersion < migrationVersion && EqualityComparer<T>.Default.Equals(currentValue, oldDefault);
}

public enum TeamSentryPlacement
{
    /// <summary>Let vanilla place your own held sentry.</summary>
    PlaceOwn,
    /// <summary>Skip your own placement this press, keeping it in hand (Shift+E, or a held E settling).</summary>
    HoldOwn,
    /// <summary>Place the selected bot's sentry at your ghost instead of your own, keeping yours in hand.</summary>
    PlaceBotInstead,
}

/// <summary>
/// What the vanilla E placement does with your own sentry in hand (G.EZ-T120, replacing G.EZ-T116's
/// fixed bot order): whichever turret the Tool key has selected goes down - the selected bot's in
/// place of yours (yours stays in hand), or your own once no bot's is left. Shift+E never places your own; a held E inside the
/// cooldown places nothing. Host and clients alike - a client's bot placement is a request to the host.
/// </summary>
public static class TeamSentryRules
{
    public const float TeamPlaceCooldown = 0.4f;

    /// <param name="poolUnknown">G.EZ-T124: the host's pool state hasn't arrived yet (or went stale) -
    /// bot turrets may still be queued, so your own is held (own-last) rather than placed.</param>
    public static TeamSentryPlacement Decide(bool shiftEActive, float secondsSinceLastTeamPlace, bool selectedIsBot, bool poolUnknown = false)
    {
        if (shiftEActive) return TeamSentryPlacement.HoldOwn;
        if (poolUnknown) return TeamSentryPlacement.HoldOwn;
        if (secondsSinceLastTeamPlace < TeamPlaceCooldown) return TeamSentryPlacement.HoldOwn;
        return selectedIsBot ? TeamSentryPlacement.PlaceBotInstead : TeamSentryPlacement.PlaceOwn;
    }
}

/// <summary>What the Tool select key (4) does this frame, G.EZ-T120.</summary>
public enum ToolKeyAction
{
    /// <summary>Leave it to vanilla (wield the slot) or to another mod.</summary>
    None,
    /// <summary>Step the selection to the next available turret.</summary>
    Cycle,
}

/// <summary>
/// The team turret pool (G.EZ-T120). Every human's pool is their own sentry plus every bot's sentry
/// that isn't down; entry 0 is always your own (available only once no bot's is - <see cref="OwnSelectable"/>),
/// 1.. the bots in the host's order. The selection
/// is kept by position here and by owner in the plugin. Nothing here ever creates a turret: an
/// entry is only ever placed through its owner's own backpack item, gated on that item not being
/// deployed already (on the host, for a bot's - <see cref="HostAccepts"/>).
/// </summary>
public static class TeamTurretPool
{
    public const int None = -1;
    public const int Own = 0;

    /// <summary>
    /// G.EZ-T124: a client's request stays pending - that bot turret off its list - until the host's
    /// answer to that request id arrives (or a state shows the bot down); this is only the give-up
    /// time for an answer that never comes.
    /// </summary>
    public const float ClientRequestTimeoutSeconds = 5f;

    /// <summary>How long a client trusts the host's last pool broadcast (the host repeats it every second).</summary>
    public const float HostStateFreshSeconds = 3f;

    /// <summary>
    /// G.EZ-T124: with no pool broadcast from the host this long into the level (or this long since the
    /// last one), the host is taken not to run the pool (no GTFO.EZ, an older build, or it left) and
    /// you place your own like vanilla rather than hold it forever.
    /// </summary>
    public const float NoHostPoolSeconds = 10f;

    /// <summary>G.EZ-T124: a bot turret whose placement failed on the host sits out of the pool this long.</summary>
    public const float FailedCooldownSeconds = 5f;

    /// <summary>G.EZ-T124: how long after a Z recall the host checks the recalled turrets are really gone.</summary>
    public const float RecallVerifySeconds = 0.5f;

    /// <summary>
    /// G.EZ-T124: what a client knows about the host's pool. Never "0 bot turrets" just because nothing
    /// arrived: no broadcast yet, or a stale one, is <see cref="HostPoolStatus.Unknown"/> (your own is
    /// held, own-last); only an explicit "off" from the host, or nothing at all for
    /// <see cref="NoHostPoolSeconds"/>, is <see cref="HostPoolStatus.Off"/> (place your own).
    /// </summary>
    public static HostPoolStatus ClientPoolStatus(bool everReceived, float secondsSinceLastState, bool hostSaysEnabled, float secondsInLevel)
    {
        if (!everReceived) return secondsInLevel >= NoHostPoolSeconds ? HostPoolStatus.Off : HostPoolStatus.Unknown;
        if (!hostSaysEnabled) return HostPoolStatus.Off;
        if (secondsSinceLastState < HostStateFreshSeconds) return HostPoolStatus.Live;
        return secondsSinceLastState >= NoHostPoolSeconds ? HostPoolStatus.Off : HostPoolStatus.Unknown;
    }

    /// <summary>G.EZ-T124: a pool state is used only if it's newer than the last one taken (wrap-safe), so a late packet can't put a placed turret back.</summary>
    public static bool IsNewerState(bool haveLast, uint lastSeq, uint incomingSeq) =>
        !haveLast || unchecked((int)(incomingSeq - lastSeq)) > 0;

    /// <summary>A pending request with no answer for <see cref="ClientRequestTimeoutSeconds"/> is given up on.</summary>
    public static bool RequestTimedOut(float secondsSinceSent) => secondsSinceSent >= ClientRequestTimeoutSeconds;

    /// <summary>
    /// G.EZ-T124: the host's answer to one bot placement, its own or a client's. <see cref="HostAccepts"/>
    /// stays the no-duplicates gate; a bot sitting out a failed placement is refused as Failed.
    /// </summary>
    public static TurretPlaceResult HostDecide(bool poolEnabled, bool botFound, bool carriesSentry, bool alreadyDeployed, bool coolingDown)
    {
        if (!poolEnabled) return TurretPlaceResult.Disabled;
        if (botFound && carriesSentry && alreadyDeployed) return TurretPlaceResult.Taken;
        if (coolingDown || !HostAccepts(botFound, carriesSentry, alreadyDeployed)) return TurretPlaceResult.Failed;
        return TurretPlaceResult.Placed;
    }

    /// <summary>
    /// G.EZ-T124: after a Z recall, a bot whose backpack still says its sentry is down while no placed
    /// sentry of its is left in the level gets the flag cleared, so the turret is back in the pool.
    /// Never while the turret is still standing - that would let a second one go down.
    /// </summary>
    public static bool ClearDeployedAfterRecall(bool backpackSaysDeployed, bool turretStillInLevel, float secondsSinceRecall) =>
        backpackSaysDeployed && !turretStillInLevel && secondsSinceRecall >= RecallVerifySeconds;

    /// <summary>The next available entry after <paramref name="current"/>, wrapping; <paramref name="current"/> itself only when it's the only one left; <see cref="None"/> when none is.</summary>
    public static int Next(IReadOnlyList<bool> available, int current)
    {
        var n = available.Count;
        if (n == 0) return None;
        var start = current < 0 || current >= n ? n - 1 : current;
        for (var step = 1; step <= n; step++)
        {
            var i = (start + step) % n;
            if (available[i]) return i;
        }
        return None;
    }

    /// <summary>Keeps the selection while it's still available; otherwise moves on to the next one that is (someone else placed it).</summary>
    public static int Resolve(IReadOnlyList<bool> available, int current) =>
        current >= 0 && current < available.Count && available[current] ? current : Next(available, current);

    public static int CountAvailable(IReadOnlyList<bool> available)
    {
        var count = 0;
        foreach (var a in available) if (a) count++;
        return count;
    }

    /// <summary>X's order: every available bot turret first, then your own.</summary>
    public static int QuickDropPick(IReadOnlyList<bool> available)
    {
        for (var i = 1; i < available.Count; i++) if (available[i]) return i;
        return available.Count > Own && available[Own] ? Own : None;
    }

    /// <summary>Shift+E's pick (never your own): the selected bot if one is, else the first available bot.</summary>
    public static int BotPick(IReadOnlyList<bool> available, int selected)
    {
        if (selected > Own && selected < available.Count && available[selected]) return selected;
        for (var i = 1; i < available.Count; i++) if (available[i]) return i;
        return None;
    }

    /// <summary>
    /// The host's arbitration of every bot placement, its own and every client's: only a bot that
    /// carries a sentry that isn't already down. Requests are handled one at a time on the host's
    /// main thread and the bot is marked deployed in the same call that places it, so of two players
    /// asking for the same bot turret the first wins and the second is refused - never a second turret.
    /// </summary>
    public static bool HostAccepts(bool botFound, bool carriesSentry, bool alreadyDeployed) =>
        botFound && carriesSentry && !alreadyDeployed;

    /// <summary>A bot turret as a client sees it: only from a live host pool, not down, not sitting out a failed placement, and not already asked for.</summary>
    public static bool ClientBotAvailable(HostPoolStatus status, bool hostSaysDeployed, bool coolingDown, bool requestPending) =>
        status == HostPoolStatus.Live && !hostSaysDeployed && !coolingDown && !requestPending;

    /// <summary>
    /// Your own always goes down last (user decision): it's selectable only once no bot turret is
    /// left available to you, so your sentry - and its placement ghost - stays in hand while you
    /// empty the bot queue.
    /// </summary>
    public static bool OwnSelectable(bool ownUndeployed, int availableBotTurrets) =>
        OwnSelectable(ownUndeployed, availableBotTurrets, HostPoolStatus.Live);

    /// <summary>
    /// G.EZ-T124: with the host's pool unknown your own isn't selectable (it may not be last); with the
    /// host known not to run the pool, it is (there's no bot queue to empty).
    /// </summary>
    public static bool OwnSelectable(bool ownUndeployed, int availableBotTurrets, HostPoolStatus status) => status switch
    {
        HostPoolStatus.Live => ownUndeployed && availableBotTurrets <= 0,
        HostPoolStatus.Off => ownUndeployed,
        _ => false,
    };

    /// <summary>
    /// The Tool key: with your own sentry in hand, and in hand last frame too (so the press that
    /// wields the slot doesn't also cycle it), cycle the selection. Otherwise vanilla. Absurd
    /// Weapons' gear cycling owns the key while it's switched on.
    /// </summary>
    public static ToolKeyAction DecideToolKey(bool absurdWeaponsCyclingOn, bool wieldingOwnSentry, bool wieldedToolLastFrame) =>
        !absurdWeaponsCyclingOn && wieldingOwnSentry && wieldedToolLastFrame ? ToolKeyAction.Cycle : ToolKeyAction.None;
}

/// <summary>G.EZ-T124: what a client knows about the host's team turret pool.</summary>
public enum HostPoolStatus
{
    /// <summary>No fresh broadcast (not yet, or stale): bot turrets may still be queued - hold your own.</summary>
    Unknown,
    /// <summary>A fresh broadcast with the pool on: the bot turrets it lists are the truth.</summary>
    Live,
    /// <summary>The host says its pool is off, or never sent one: no bot queue - your own places like vanilla.</summary>
    Off,
}

/// <summary>G.EZ-T124: the host's answer to a bot turret placement, sent back to the requester.</summary>
public enum TurretPlaceResult : byte
{
    Placed = 1,
    /// <summary>Already down - someone else got it first.</summary>
    Taken = 2,
    /// <summary>The placement itself didn't work (an error, a bad spot, the bot gone); the bot sits out for a few seconds.</summary>
    Failed = 3,
    /// <summary>The host has the team turret pool switched off.</summary>
    Disabled = 4,
}

/// <summary>G.EZ-T124: the lines every player's event log shows for the team's turrets, so everyone's count agrees.</summary>
public static class TeamTurretMessages
{
    public static string Left(int botTurretsLeft) =>
        botTurretsLeft <= 0 ? "no bot turrets left" : $"{botTurretsLeft} bot turret{(botTurretsLeft == 1 ? "" : "s")} left";

    public static string Placed(string actor, string turret, int botTurretsLeft) =>
        $"{actor} placed {turret} - {Left(botTurretsLeft)}";

    public static string Recalled(string actor, int botTurrets, bool ownToo, int botTurretsLeft, bool actorIsYou = false)
    {
        var whose = actorIsYou ? "your" : "their";
        var what = botTurrets > 0
            ? $"{botTurrets} bot turret{(botTurrets == 1 ? "" : "s")}{(ownToo ? $" and {whose} own" : "")}"
            : $"{whose} own turret";
        return $"{actor} recalled {what} - {Left(botTurretsLeft)}";
    }

    public static string Failed(string turret, float seconds) =>
        $"{turret} couldn't be placed - out of the pool for {seconds:0} s";

    public static string Refused(TurretPlaceResult reason, string turret, float failedSeconds) => reason switch
    {
        TurretPlaceResult.Taken => $"Someone else got {turret} first",
        TurretPlaceResult.Disabled => "The host has the team turret pool switched off",
        _ => Failed(turret, failedSeconds),
    };

    public const string Unknown = "Team turret pool unavailable (host)";
    public const string HostOff = "Host isn't running the team turret pool - placing your own";
}
