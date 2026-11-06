using System;
using System.Collections.Generic;

namespace GTFO.EZ.Core;

/// <summary>
/// Pure multiplier math shared by the weapon and melee tweak passes in GameDataTweaks.cs - no
/// BepInEx/Il2Cpp dependency, so it can be unit tested directly (docs/USER_STORIES.md GE-T4).
/// </summary>
public static class BalanceMath
{
    // Integer stats always round in the player's favor rather than to nearest: a stat where more
    // helps the player (ammo, clip size, loot) rounds up, one where fewer helps (enemy caps, spread)
    // rounds down. 3 x 1.5 clip = 5 rounds; 3 x 1.5 patrol groups = 4.
    public static int ScaleIntUp(int value, float multiplier) => ToInt(Math.Ceiling(Product(value, multiplier)));

    public static int ScaleIntDown(int value, float multiplier) => ToInt(Math.Floor(Product(value, multiplier)));

    // G.EZ-T119: an unchecked cast of a product past int.MaxValue (e.g. a 1e7 ammo multiplier on a
    // ~460-round cap) wraps to int.MinValue - a negative ammo cap. Saturate instead.
    private static int ToInt(double value) =>
        value >= int.MaxValue ? int.MaxValue : value <= int.MinValue ? int.MinValue : (int)value;

    // Multipliers like 0.3 aren't exact in binary, so 10 x 0.3 lands on 3.0000001 and would
    // ceiling to 4; trimming to 4 decimals keeps a whole-number product whole.
    private static double Product(int value, float multiplier) => Math.Round(value * (double)multiplier, 4);

    // A missing key defaults to 1 (no-op multiplier) rather than 0, so an archetype/field
    // combination nobody configured yet - or one the game adds later - is left at vanilla instead
    // of silently zeroing out a stat.
    public static float LookupMultiplierOrDefault(IReadOnlyDictionary<string, float> table, string key) =>
        table.TryGetValue(key, out var value) ? value : 1f;

    // Weapon stats compose a type-level slider (shared by every fire-mode in a weapon family) with
    // an individual-archetype slider; melee stats only ever use the individual-style lookup, so
    // ComposeWeaponMultiplier's two-table shape only applies to ranged weapons/sentries.
    public static float ComposeWeaponMultiplier(
        IReadOnlyDictionary<string, float> typeTable, string typeKey,
        IReadOnlyDictionary<string, float> individualTable, string individualKey) =>
        LookupMultiplierOrDefault(typeTable, typeKey) * LookupMultiplierOrDefault(individualTable, individualKey);
}
