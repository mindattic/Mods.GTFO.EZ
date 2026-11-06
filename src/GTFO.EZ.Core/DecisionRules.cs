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

    public static TeamSentryPlacement Decide(bool shiftEActive, float secondsSinceLastTeamPlace, bool selectedIsBot)
    {
        if (shiftEActive) return TeamSentryPlacement.HoldOwn;
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

    /// <summary>How long a client treats a bot turret it asked the host to place as taken, waiting for the answer.</summary>
    public const float ClientPendingSeconds = 2f;

    /// <summary>How long a client trusts the host's last pool broadcast (the host repeats it every second).</summary>
    public const float HostStateFreshSeconds = 3f;

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

    /// <summary>A bot turret as a client sees it: only from a fresh host broadcast, not down, and not already asked for.</summary>
    public static bool ClientBotAvailable(bool hostStateFresh, bool hostSaysDeployed, bool requestPending) =>
        hostStateFresh && !hostSaysDeployed && !requestPending;

    /// <summary>
    /// Your own always goes down last (user decision): it's selectable only once no bot turret is
    /// left available to you, so your sentry - and its placement ghost - stays in hand while you
    /// empty the bot queue.
    /// </summary>
    public static bool OwnSelectable(bool ownUndeployed, int availableBotTurrets) =>
        ownUndeployed && availableBotTurrets <= 0;

    /// <summary>
    /// The Tool key: with your own sentry in hand, and in hand last frame too (so the press that
    /// wields the slot doesn't also cycle it), cycle the selection. Otherwise vanilla. Absurd
    /// Weapons' gear cycling owns the key while it's switched on.
    /// </summary>
    public static ToolKeyAction DecideToolKey(bool absurdWeaponsCyclingOn, bool wieldingOwnSentry, bool wieldedToolLastFrame) =>
        !absurdWeaponsCyclingOn && wieldingOwnSentry && wieldedToolLastFrame ? ToolKeyAction.Cycle : ToolKeyAction.None;
}
