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
    /// <summary>Let vanilla place your own held sentry (and the postfix put slot 4 away).</summary>
    PlaceOwn,
    /// <summary>Skip your own placement this press, keeping it in hand (Shift+E, or a held E settling).</summary>
    HoldOwn,
    /// <summary>Place the next bot's undeployed sentry at your ghost instead of your own.</summary>
    PlaceBotInstead,
}

/// <summary>
/// What plain E does with your own sentry in hand (G.EZ-T116): every bot's undeployed sentry goes
/// down first, one per press, your own last; Shift+E never places your own; a held E inside the
/// cooldown places nothing; a client (bots belong to the host) just places its own. G.EZ-T117.
/// </summary>
public static class TeamSentryRules
{
    public const float TeamPlaceCooldown = 0.4f;

    public static TeamSentryPlacement Decide(
        bool shiftEActive, float secondsSinceLastTeamPlace, bool isHost, bool wieldingTool, int undeployedBotSentries)
    {
        if (shiftEActive) return TeamSentryPlacement.HoldOwn;
        if (secondsSinceLastTeamPlace < TeamPlaceCooldown) return TeamSentryPlacement.HoldOwn;
        if (!isHost || !wieldingTool || undeployedBotSentries <= 0) return TeamSentryPlacement.PlaceOwn;
        return TeamSentryPlacement.PlaceBotInstead;
    }
}
