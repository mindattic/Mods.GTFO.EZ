using System.Collections.Generic;
using GTFO.EZ.Core;
using NUnit.Framework;

namespace GTFO.EZ.Core.Tests;

// G.EZ-T125: host/client level-settings sync - fingerprints, wire format, comparison and the
// re-apply decision. The game side (gathering settings, restoring datablocks) can't run headless.
[TestFixture]
public class LevelSettingsSyncTests
{
    private static List<LevelSetting> Settings(params (string Key, float Value)[] s)
    {
        var list = new List<LevelSetting>();
        foreach (var (key, value) in s) list.Add(new LevelSetting(key, value));
        return list;
    }

    [Test]
    public void Fingerprint_IgnoresGatherOrder()
    {
        var a = LevelFingerprint.Compute(Settings(("Loot/A", 1f), ("Loot/B", 2f)), 0);
        var b = LevelFingerprint.Compute(Settings(("Loot/B", 2f), ("Loot/A", 1f)), 0);
        Assert.That(a, Is.EqualTo(b));
    }

    [Test]
    public void Fingerprint_ChangesWithAnyValueKeyOrMask()
    {
        var baseline = LevelFingerprint.Compute(Settings(("Loot/A", 1f)), 0);
        Assert.That(LevelFingerprint.Compute(Settings(("Loot/A", 1.0001f)), 0), Is.Not.EqualTo(baseline));
        Assert.That(LevelFingerprint.Compute(Settings(("Loot/B", 1f)), 0), Is.Not.EqualTo(baseline));
        Assert.That(LevelFingerprint.Compute(Settings(("Loot/A", 1f)), 1), Is.Not.EqualTo(baseline));
    }

    [Test]
    public void Fingerprint_NegativeZeroAndNaNsAreCanonical()
    {
        Assert.That(LevelFingerprint.Compute(Settings(("A", -0f)), 0), Is.EqualTo(LevelFingerprint.Compute(Settings(("A", 0f)), 0)));
        var otherNaN = System.BitConverter.UInt32BitsToSingle(0x7FC00001);
        Assert.That(LevelFingerprint.Compute(Settings(("A", otherNaN)), 0), Is.EqualTo(LevelFingerprint.Compute(Settings(("A", float.NaN)), 0)));
    }

    [Test]
    public void Fingerprint_IsStable() =>
        // Every machine must agree: pinned so a change to the hash is a deliberate protocol change.
        // FNV-1a 32 of "a" is the published test vector 0xE40C292C.
        Assert.That(LevelFingerprint.KeyHash("a"), Is.EqualTo(0xE40C292Cu));

    [Test]
    public void Mask_SetsBitPerPresentBlock() =>
        Assert.That(LevelFingerprint.Mask(new[] { true, false, true }), Is.EqualTo(0b101UL));

    [Test]
    public void Differences_NoneWhenEqual()
    {
        var mods = new[] { new ModFingerprint("GTFO.EZ", true, 5, 0) };
        Assert.That(FingerprintComparison.Differences(mods, mods, "the host"), Is.Empty);
    }

    [Test]
    public void Differences_NamesModAndWhat()
    {
        var mine = new[] { new ModFingerprint("GTFO.EZ", true, 5, 0), new ModFingerprint("GTFO.EZ.AbsurdItems", true, 1, 3) };
        var theirs = new[] { new ModFingerprint("GTFO.EZ", true, 6, 0), new ModFingerprint("GTFO.EZ.AbsurdItems", true, 1, 1) };
        var lines = FingerprintComparison.Differences(mine, theirs, "Bob");
        Assert.That(lines, Is.EqualTo(new[]
        {
            "GTFO.EZ: level settings differ from Bob's",
            "GTFO.EZ.AbsurdItems: added items/weapons differ from Bob's",
        }));
    }

    [Test]
    public void Differences_InstalledOnOneSideOnly()
    {
        var mine = new[] { new ModFingerprint("GTFO.EZ.AbsurdWeapons", false, 0, 0) };
        var theirs = new[] { new ModFingerprint("GTFO.EZ.AbsurdWeapons", true, 0, 7), new ModFingerprint("GTFO.EZ.AbsurdMoves", true, 0, 0) };
        Assert.That(FingerprintComparison.Differences(mine, theirs, "the host"), Is.EqualTo(new[]
        {
            "GTFO.EZ.AbsurdWeapons is on for the host but not for you",
            "GTFO.EZ.AbsurdMoves is on for the host but not for you",
        }));
    }

    [Test]
    public void Differences_BothOffIsNotADifference() =>
        Assert.That(FingerprintComparison.Differences(
            new[] { new ModFingerprint("X", false, 1, 1) }, new[] { new ModFingerprint("X", false, 2, 2) }, "Bob"), Is.Empty);

    [Test]
    public void Hello_RoundTrips()
    {
        var mods = new[] { new ModFingerprint("GTFO.EZ", true, ulong.MaxValue, 0), new ModFingerprint("GTFO.EZ.AbsurdItems", false, 0, 3) };
        Assert.That(LevelSettingsCodec.TryDecodeHello(LevelSettingsCodec.EncodeHello(mods), out var back), Is.True);
        Assert.That(back, Is.EqualTo(mods));
    }

    [Test]
    public void Host_RoundTrips_ValuesByKeyHash()
    {
        var mods = new[] { new ModFingerprint("GTFO.EZ", true, 42, 0) };
        var values = Settings(("Loot/A", 2.5f), ("Artifact Spawns/Even Artifact Colors", 1f));
        Assert.That(LevelSettingsCodec.TryDecodeHost(LevelSettingsCodec.EncodeHost(mods, values), out var backMods, out var backValues), Is.True);
        Assert.That(backMods, Is.EqualTo(mods));
        Assert.That(backValues[LevelFingerprint.KeyHash("Loot/A")], Is.EqualTo(2.5f));
        Assert.That(backValues[LevelFingerprint.KeyHash("Artifact Spawns/Even Artifact Colors")], Is.EqualTo(1f));
    }

    [Test]
    public void Decode_RejectsWrongKindVersionAndGarbage()
    {
        var hello = LevelSettingsCodec.EncodeHello(new[] { new ModFingerprint("GTFO.EZ", true, 1, 0) });
        Assert.That(LevelSettingsCodec.TryDecodeHost(hello, out _, out _), Is.False);
        var wrongVersion = (byte[])hello.Clone();
        wrongVersion[0] = 99;
        Assert.That(LevelSettingsCodec.TryDecodeHello(wrongVersion, out _), Is.False);
        Assert.That(LevelSettingsCodec.TryDecodeHello(new byte[] { LevelSettingsCodec.Version, 1, 5 }, out _), Is.False);
        Assert.That(LevelSettingsCodec.TryDecodeHello(null, out _), Is.False);
    }

    [Test]
    public void Overrides_ResolveHostValueOnlyWhileActive()
    {
        var overrides = new LevelOverrides();
        Assert.That(overrides.Resolve("Loot/A", 1f), Is.EqualTo(1f));
        overrides.Set(new Dictionary<uint, float> { [LevelFingerprint.KeyHash("Loot/A")] = 3f });
        Assert.That(overrides.Resolve("Loot/A", 1f), Is.EqualTo(3f));
        Assert.That(overrides.Resolve("Loot/Unknown", 1f), Is.EqualTo(1f), "a setting the host didn't send keeps your own value");
        overrides.Clear();
        Assert.That(overrides.Resolve("Loot/A", 1f), Is.EqualTo(1f));
    }

    [Test]
    public void Overrides_ListDifferencesAndMissing()
    {
        var overrides = new LevelOverrides();
        overrides.Set(new Dictionary<uint, float> { [LevelFingerprint.KeyHash("A")] = 2f, [LevelFingerprint.KeyHash("B")] = 1f });
        var local = Settings(("A", 1f), ("B", 1f), ("C", 1f));
        Assert.That(overrides.Differences(local), Is.EqualTo(new[] { ("A", 1f, 2f) }));
        Assert.That(overrides.Missing(local), Is.EqualTo(new[] { "C" }));
    }

    [TestCase(false, 1UL, 2UL, false, LevelReapply.NotReady)]
    [TestCase(true, 1UL, 1UL, false, LevelReapply.UpToDate)]
    [TestCase(true, 1UL, 1UL, true, LevelReapply.UpToDate)]
    [TestCase(true, 1UL, 2UL, false, LevelReapply.Reapply)]
    [TestCase(true, 1UL, 2UL, true, LevelReapply.TooLate)]
    public void Decide(bool snapshot, ulong applied, ulong wanted, bool levelBuilt, LevelReapply expected) =>
        Assert.That(LevelSyncRules.Decide(snapshot, applied, wanted, levelBuilt), Is.EqualTo(expected));

    [Test]
    public void WarnNoHostValues_OnlyForAClientThatNeverHeard()
    {
        Assert.That(LevelSyncRules.WarnNoHostValues(true, false), Is.True);
        Assert.That(LevelSyncRules.WarnNoHostValues(true, true), Is.False);
        Assert.That(LevelSyncRules.WarnNoHostValues(false, false), Is.False);
    }
}
