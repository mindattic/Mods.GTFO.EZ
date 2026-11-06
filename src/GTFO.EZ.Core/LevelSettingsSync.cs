using System.Text;

namespace GTFO.EZ.Core;

// G.EZ-T125: the pure half of the host/client level-settings sync. Every machine builds the level
// from its own GameData, which the GTFO.* mods shape from each player's own .cfg - so two players
// with different settings generated different levels. The plugin gathers settings and datablocks
// and moves bytes over GTFO-API; the hashing, wire format, comparison and decisions live here.

/// <summary>One level-affecting setting: "Section/Key" and its value (a bool as 1/0).</summary>
public readonly record struct LevelSetting(string Key, float Value);

/// <summary>
/// One mod's level fingerprint: whether it's installed and on, a hash of its level-affecting
/// settings, and a bitmask of which of its injected datablocks this game holds.
/// </summary>
public sealed record ModFingerprint(string Mod, bool Installed, ulong SettingsHash, ulong InjectedMask);

public static class LevelFingerprint
{
    private const ulong FnvOffset64 = 14695981039346656037UL;
    private const ulong FnvPrime64 = 1099511628211UL;
    private const uint FnvOffset32 = 2166136261U;
    private const uint FnvPrime32 = 16777619U;

    /// <summary>A setting's id on the wire: 32-bit FNV-1a of its UTF-8 "Section/Key".</summary>
    public static uint KeyHash(string key)
    {
        var hash = FnvOffset32;
        foreach (var b in Encoding.UTF8.GetBytes(key))
        {
            hash ^= b;
            hash *= FnvPrime32;
        }
        return hash;
    }

    /// <summary>
    /// -0 is 0 and every NaN is the same NaN, so values that act the same hash the same.
    /// </summary>
    public static float Canonical(float value) => float.IsNaN(value) ? float.NaN : value == 0f ? 0f : value;

    /// <summary>
    /// 64-bit FNV-1a over every setting (in ordinal key order, so the order they're gathered in
    /// doesn't matter) and the injected-block mask. Stable across machines and runs.
    /// </summary>
    public static ulong Compute(IEnumerable<LevelSetting> settings, ulong injectedMask)
    {
        var hash = FnvOffset64;
        void Mix(byte b)
        {
            hash ^= b;
            hash *= FnvPrime64;
        }
        void MixUInt(uint v)
        {
            for (var i = 0; i < 4; i++) Mix((byte)(v >> (8 * i)));
        }

        foreach (var setting in settings.OrderBy(s => s.Key, StringComparer.Ordinal))
        {
            foreach (var b in Encoding.UTF8.GetBytes(setting.Key)) Mix(b);
            Mix(0);
            MixUInt(BitConverter.SingleToUInt32Bits(Canonical(setting.Value)));
        }
        for (var i = 0; i < 8; i++) Mix((byte)(injectedMask >> (8 * i)));
        return hash;
    }

    /// <summary>Bit i set when block i of a mod's fixed list is present (up to 64).</summary>
    public static ulong Mask(IReadOnlyList<bool> present)
    {
        ulong mask = 0;
        for (var i = 0; i < present.Count && i < 64; i++)
            if (present[i]) mask |= 1UL << i;
        return mask;
    }
}

public static class FingerprintComparison
{
    /// <summary>
    /// What differs between two sets of mod fingerprints, one line per mod, in `mine`'s order then
    /// any mod only `theirs` lists. Empty when they agree. `theirName` is who the other side is
    /// ("the host", a player's name) for the wording.
    /// </summary>
    public static List<string> Differences(IReadOnlyList<ModFingerprint> mine, IReadOnlyList<ModFingerprint> theirs, string theirName)
    {
        var lines = new List<string>();
        var theirsByMod = new Dictionary<string, ModFingerprint>(StringComparer.Ordinal);
        foreach (var t in theirs) theirsByMod[t.Mod] = t;
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var m in mine)
        {
            seen.Add(m.Mod);
            theirsByMod.TryGetValue(m.Mod, out var t);
            var line = Describe(m, t, theirName);
            if (line != null) lines.Add(line);
        }
        foreach (var t in theirs)
        {
            if (seen.Contains(t.Mod)) continue;
            var line = Describe(null, t, theirName);
            if (line != null) lines.Add(line);
        }
        return lines;
    }

    private static string? Describe(ModFingerprint? mine, ModFingerprint? theirs, string theirName)
    {
        var mineOn = mine?.Installed == true;
        var theirsOn = theirs?.Installed == true;
        var mod = mine?.Mod ?? theirs!.Mod;
        if (!mineOn && !theirsOn) return null;
        if (mineOn != theirsOn) return mineOn ? $"{mod} is on for you but not for {theirName}" : $"{mod} is on for {theirName} but not for you";

        var parts = new List<string>();
        if (mine!.SettingsHash != theirs!.SettingsHash) parts.Add("level settings");
        if (mine.InjectedMask != theirs.InjectedMask) parts.Add("added items/weapons");
        return parts.Count == 0 ? null : $"{mod}: {string.Join(" and ", parts)} differ from {theirName}'s";
    }
}

/// <summary>
/// The host's level-affecting values, as a client holds them. Keyed by <see cref="LevelFingerprint.KeyHash"/>
/// so a setting is matched by name, whatever order or version each side lists them in.
/// </summary>
public sealed class LevelOverrides
{
    private readonly Dictionary<uint, float> _values = new();

    public bool Active { get; private set; }

    public void Set(IReadOnlyDictionary<uint, float> values)
    {
        _values.Clear();
        foreach (var (key, value) in values) _values[key] = value;
        Active = true;
    }

    public void Clear()
    {
        _values.Clear();
        Active = false;
    }

    /// <summary>The host's value for this setting while active and the host sent it, else `local`.</summary>
    public float Resolve(string key, float local) =>
        Active && _values.TryGetValue(LevelFingerprint.KeyHash(key), out var host) ? host : local;

    /// <summary>The local settings whose value the host's replaces (key, yours, host's).</summary>
    public List<(string Key, float Local, float Host)> Differences(IEnumerable<LevelSetting> local)
    {
        var list = new List<(string, float, float)>();
        if (!Active) return list;
        foreach (var s in local)
            if (_values.TryGetValue(LevelFingerprint.KeyHash(s.Key), out var host)
                && BitConverter.SingleToUInt32Bits(LevelFingerprint.Canonical(host)) != BitConverter.SingleToUInt32Bits(LevelFingerprint.Canonical(s.Value)))
                list.Add((s.Key, s.Value, host));
        return list;
    }

    /// <summary>Local settings the host didn't send (an older/newer GTFO.EZ): they stay at your own value.</summary>
    public List<string> Missing(IEnumerable<LevelSetting> local)
    {
        var list = new List<string>();
        if (!Active) return list;
        foreach (var s in local)
            if (!_values.ContainsKey(LevelFingerprint.KeyHash(s.Key))) list.Add(s.Key);
        return list;
    }
}

/// <summary>The two GTFO-API payloads: a peer's fingerprints, and the host's fingerprints plus its level values.</summary>
public static class LevelSettingsCodec
{
    public const byte Version = 1;
    private const byte KindHello = 1;
    private const byte KindHost = 2;
    private const int MaxMods = 32;
    private const int MaxValues = 4096;

    public static byte[] EncodeHello(IReadOnlyList<ModFingerprint> mods)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8);
        writer.Write(Version);
        writer.Write(KindHello);
        WriteMods(writer, mods);
        writer.Flush();
        return stream.ToArray();
    }

    public static bool TryDecodeHello(byte[]? bytes, out List<ModFingerprint> mods)
    {
        mods = new List<ModFingerprint>();
        try
        {
            using var reader = Reader(bytes, KindHello);
            if (reader == null) return false;
            mods = ReadMods(reader);
            return true;
        }
        catch (Exception)
        {
            mods = new List<ModFingerprint>();
            return false;
        }
    }

    public static byte[] EncodeHost(IReadOnlyList<ModFingerprint> mods, IReadOnlyList<LevelSetting> values)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8);
        writer.Write(Version);
        writer.Write(KindHost);
        WriteMods(writer, mods);
        writer.Write((ushort)values.Count);
        foreach (var v in values)
        {
            writer.Write(LevelFingerprint.KeyHash(v.Key));
            writer.Write(v.Value);
        }
        writer.Flush();
        return stream.ToArray();
    }

    public static bool TryDecodeHost(byte[]? bytes, out List<ModFingerprint> mods, out Dictionary<uint, float> values)
    {
        mods = new List<ModFingerprint>();
        values = new Dictionary<uint, float>();
        try
        {
            using var reader = Reader(bytes, KindHost);
            if (reader == null) return false;
            mods = ReadMods(reader);
            int count = reader.ReadUInt16();
            if (count > MaxValues) return false;
            for (var i = 0; i < count; i++) values[reader.ReadUInt32()] = reader.ReadSingle();
            return true;
        }
        catch (Exception)
        {
            mods = new List<ModFingerprint>();
            values = new Dictionary<uint, float>();
            return false;
        }
    }

    private static BinaryReader? Reader(byte[]? bytes, byte kind)
    {
        if (bytes == null || bytes.Length < 2 || bytes[0] != Version || bytes[1] != kind) return null;
        var reader = new BinaryReader(new MemoryStream(bytes, 2, bytes.Length - 2), Encoding.UTF8);
        return reader;
    }

    private static void WriteMods(BinaryWriter writer, IReadOnlyList<ModFingerprint> mods)
    {
        writer.Write((byte)Math.Min(mods.Count, MaxMods));
        for (var i = 0; i < mods.Count && i < MaxMods; i++)
        {
            var m = mods[i];
            writer.Write(m.Mod);
            writer.Write(m.Installed);
            writer.Write(m.SettingsHash);
            writer.Write(m.InjectedMask);
        }
    }

    private static List<ModFingerprint> ReadMods(BinaryReader reader)
    {
        int count = reader.ReadByte();
        if (count > MaxMods) throw new InvalidDataException("too many mods");
        var mods = new List<ModFingerprint>(count);
        for (var i = 0; i < count; i++)
            mods.Add(new ModFingerprint(reader.ReadString(), reader.ReadBoolean(), reader.ReadUInt64(), reader.ReadUInt64()));
        return mods;
    }
}

/// <summary>What to do when the level settings wanted now (the host's, or your own) aren't the ones applied.</summary>
public enum LevelReapply
{
    /// <summary>The datablocks already hold what's wanted.</summary>
    UpToDate,
    /// <summary>Restore vanilla and apply the wanted values now.</summary>
    Reapply,
    /// <summary>A level is already built from the old values; wait for it to end, and say so.</summary>
    TooLate,
    /// <summary>GameData hasn't been initialized yet: the first tweak pass will use the wanted values anyway.</summary>
    NotReady,
}

public static class LevelSyncRules
{
    public static LevelReapply Decide(bool snapshotTaken, ulong appliedSignature, ulong wantedSignature, bool levelBuilt)
    {
        if (!snapshotTaken) return LevelReapply.NotReady;
        if (appliedSignature == wantedSignature) return LevelReapply.UpToDate;
        return levelBuilt ? LevelReapply.TooLate : LevelReapply.Reapply;
    }

    /// <summary>
    /// Whether a client should warn that it's building a level without the host's values: it's in
    /// someone else's lobby and nothing ever arrived from that host.
    /// </summary>
    public static bool WarnNoHostValues(bool isClientOfAnotherHost, bool hostValuesReceived) =>
        isClientOfAnotherHost && !hostValuesReceived;
}
