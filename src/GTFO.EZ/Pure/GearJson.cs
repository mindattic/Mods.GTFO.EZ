using System;
using System.Text.RegularExpressions;

namespace GTFO.EZ;

/// <summary>
/// Reads and rewrites a loadout entry's gear JSON (PlayerOfflineGearDataBlock.GearJSON): its packet
/// is a list of components, each <c>{"c": component, "v": value}</c> - 1 = fire mode, 2 = gear
/// category, 4 = first-person hold (ItemFPSSettings), and so on. No game types here, ported from
/// GTFO.EZ.AbsurdWeapons' GearCycler/GunInjector support (already unit tested there).
/// </summary>
internal static class GearJson
{
    // What players see in the loadout and HUD: the item's name marked "(!)" so it reads as
    // modded, not vanilla. Block names (lookup keys) stay unmarked.
    internal static string DisplayName(string name) => name + " (!)";

    // The value of one component, or null if the packet doesn't have it.
    internal static uint? Component(string json, int component)
    {
        var match = Regex.Match(json, $"\"c\"\\s*:\\s*{component}\\s*,\\s*\"v\"\\s*:\\s*(\\d+)");
        return match.Success ? uint.Parse(match.Groups[1].Value) : null;
    }

    // The gear category a loadout entry belongs to (component 2).
    internal static uint? CategoryId(string json) => Component(json, 2);

    // Sets one component to `value`. Returns the JSON unchanged if the component isn't there.
    internal static string SetComponent(string json, int component, uint value) =>
        Regex.Replace(json, $"(\"c\"\\s*:\\s*{component}\\s*,\\s*\"v\"\\s*:\\s*)\\d+", $"${{1}}{value}", RegexOptions.None, TimeSpan.FromSeconds(1));

    // Points the gear entry at the new item's category and renames it. Returns null if the
    // category component isn't there, rather than registering gear that would silently stay the
    // model's original item.
    internal static string? Rewrite(string json, uint fromCategory, uint toCategory, string name)
    {
        var categoryComp = new Regex($"\"c\"\\s*:\\s*2\\s*,\\s*\"v\"\\s*:\\s*{fromCategory}\\b");
        if (!categoryComp.IsMatch(json)) return null;

        // The name goes into JSON strings and regex replacements: escape both.
        var jsonName = name.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("$", "$$");
        json = categoryComp.Replace(json, $"\"c\":2,\"v\":{toCategory}", 1);
        json = Regex.Replace(json, "\"Name\"\\s*:\\s*\"[^\"]*\"", $"\"Name\":\"{jsonName}\"");
        json = Regex.Replace(json, "(\"publicName\"\\s*:\\s*\\{\\s*\"data\"\\s*:\\s*)\"[^\"]*\"", $"$1\"{jsonName}\"");
        return json;
    }
}
