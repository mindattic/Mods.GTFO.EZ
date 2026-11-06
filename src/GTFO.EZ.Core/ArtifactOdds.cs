namespace GTFO.EZ.Core;

/// <summary>
/// Artifact color odds (G.EZ-T121). Vanilla weights Muted (blue, "Basic") far above Bold (green,
/// "Specialized") and Aggressive (red, "Advanced"), so a level full of blue and no red/green is
/// normal. With Even on, the three weights are set to their mean first - same total, so the same
/// overall spawn odds, just an even split - then each color's own multiplier applies.
/// </summary>
public static class ArtifactOdds
{
    public static (float Muted, float Bold, float Aggressive) Rebalance(
        float muted, float bold, float aggressive, bool even,
        float mutedMultiplier, float boldMultiplier, float aggressiveMultiplier)
    {
        if (even)
        {
            var mean = (muted + bold + aggressive) / 3f;
            muted = bold = aggressive = mean;
        }
        return (muted * mutedMultiplier, bold * boldMultiplier, aggressive * aggressiveMultiplier);
    }
}
