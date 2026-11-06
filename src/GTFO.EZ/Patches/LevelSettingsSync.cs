using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP;
using GameData;
using GTFO.API;
using GTFO.EZ.Core;
using MindAttic.GTFO.Shared;
using SNetwork;
using UnityEngine;

namespace GTFO.EZ.Patches;

/// <summary>
/// G.EZ-T125: the level-affecting settings - everything the expedition balance, artifact and loot
/// tweaks read (GameDataTweaks' ApplyExpeditionBalanceTweaks/ApplyArtifactTweaks/ApplyLootTweaks).
/// Every machine builds the level from its own GameData, so these must be the host's on every
/// client: <see cref="Effective(ConfigEntry{float})"/> is the host's value while one is held, else
/// your own. Nothing here writes the client's .cfg. Per-player settings (your own health, stamina,
/// weapons...) are never in this set and stay your own.
/// </summary>
internal static class LevelSettings
{
    // The sections whose every entry feeds the level tweaks. A per-player setting must not be bound
    // in one of these (or must be listed in Excluded) - it would follow the host.
    private static readonly HashSet<string> Sections = new(StringComparer.Ordinal)
    {
        "Expedition Resources", "Expedition Enemies", "Expedition Level Gen", "Expedition Doors", "Artifact Spawns", "Loot",
    };

    // Applied per player at the drop, not part of level generation (G.EZ-T123).
    private static readonly HashSet<string> Excluded = new(StringComparer.Ordinal) { "Expedition Resources/Full Ammo On Drop" };

    private static readonly List<(string Key, ConfigEntryBase Entry)> Entries = new();
    private static readonly Dictionary<ConfigEntryBase, string> KeyOf = new();

    internal static readonly LevelOverrides Host = new();

    internal static void Init(ConfigFile config)
    {
        Entries.Clear();
        KeyOf.Clear();
        foreach (var pair in config)
        {
            var key = Key(pair.Key);
            if (!Sections.Contains(pair.Key.Section) || Excluded.Contains(key) || ToFloat(pair.Value.BoxedValue) == null) continue;
            Entries.Add((key, pair.Value));
            KeyOf[pair.Value] = key;
        }
        Entries.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
        Plugin.Logger.LogInfo($"Level settings sync: {Entries.Count} level-affecting settings follow the host in multiplayer.");
    }

    internal static string Key(ConfigDefinition d) => $"{d.Section}/{d.Key}";

    internal static float Effective(ConfigEntry<float> entry) =>
        KeyOf.TryGetValue(entry, out var key) ? Host.Resolve(key, entry.Value) : entry.Value;

    internal static bool Effective(ConfigEntry<bool> entry) =>
        KeyOf.TryGetValue(entry, out var key) ? Host.Resolve(key, entry.Value ? 1f : 0f) != 0f : entry.Value;

    /// <summary>This machine's own values (its .cfg).</summary>
    internal static List<LevelSetting> Local()
    {
        var list = new List<LevelSetting>(Entries.Count);
        foreach (var (key, entry) in Entries) list.Add(new LevelSetting(key, ToFloat(entry.BoxedValue) ?? 0f));
        return list;
    }

    /// <summary>The values the level tweaks use right now: the host's where held, else your own.</summary>
    internal static List<LevelSetting> EffectiveValues()
    {
        var list = Local();
        for (var i = 0; i < list.Count; i++) list[i] = list[i] with { Value = Host.Resolve(list[i].Key, list[i].Value) };
        return list;
    }

    /// <summary>
    /// What the level datablocks hold once the tweaks run with the effective values - GTFO.EZ's and
    /// GTFO.EZ.AbsurdItems' (whose kit loot is run again with them, G.EZ-T128).
    /// </summary>
    internal static ulong EffectiveSignature()
    {
        var values = EffectiveValues();
        values.AddRange(AbsurdItemsLevel.Effective());
        return LevelFingerprint.Compute(values, 0);
    }

    internal static float? ToFloat(object? value) => value switch
    {
        float f => f,
        bool b => b ? 1f : 0f,
        int i => i,
        double d => (float)d,
        _ => null,
    };
}

/// <summary>
/// The GTFO.* mods' level fingerprints, all gathered here (GTFO.EZ is always installed alongside
/// GTFO.EZ.AbsurdItems, a Thunderstore dependency of it): each mod's level-affecting settings,
/// read off its own config, and which of its injected datablocks this game holds.
/// </summary>
internal static class ModFingerprints
{
    private const string AbsurdItemsGuid = "MindAttic.GTFO.EZ.AbsurdItems";
    private const string AbsurdWeaponsGuid = "MindAttic.GTFO.EZ.AbsurdWeapons";

    // AbsurdItems' injected items: the Frag Grenade (1203, the old Hand Grenade's id), the Stim Pack
    // and the Plasma Grenade (1205, GAI-T37) - all always registered (GAI-T39).
    private static readonly uint[] AbsurdItemsItems = { 1203, 1204, 1205 };

    // AbsurdWeapons' gear categories (melee 100x, guns 11xx, the Victory Banner tool 1202;
    // G.AWPN-T236: always registered).
    private static readonly uint[] AbsurdWeaponsGear = { 1002, 1003, 1005, 1006, 1101, 1102, 1103, 1104, 1105, 1106, 1107, 1108, 1109, 1115, 1116, 1118, 1119, 1202 };

    internal static List<ModFingerprint> Local()
    {
        var list = new List<ModFingerprint>
        {
            new(Plugin.NAME, true, LevelFingerprint.Compute(LevelSettings.Local(), 0), 0),
        };
        list.Add(Other(AbsurdItemsLevel.Name, AbsurdItemsGuid, AbsurdItemsLevel.IsLevelSetting,
            () => Mask(AbsurdItemsItems, id => GameDataBlockBase<ItemDataBlock>.HasBlock(id))));
        list.Add(Other("GTFO.EZ.AbsurdWeapons", AbsurdWeaponsGuid, _ => false,
            () => Mask(AbsurdWeaponsGear, id => GameDataBlockBase<GearCategoryDataBlock>.HasBlock(id))));
        return list;
    }

    /// <summary>Another GTFO.* mod's plugin, when it's installed and switched on.</summary>
    internal static BasePlugin? ActivePlugin(string guid) =>
        SharedOwner.IsActive(guid) && IL2CPPChainloader.Instance.Plugins.TryGetValue(guid, out var info)
            && info.Instance is BasePlugin plugin ? plugin : null;

    /// <summary>A plugin's level-affecting settings off its own config ("Section/Key", a bool as 1/0).</summary>
    internal static List<LevelSetting> Settings(BasePlugin plugin, Func<ConfigDefinition, bool> isLevel)
    {
        var settings = new List<LevelSetting>();
        foreach (var pair in plugin.Config)
        {
            if (!isLevel(pair.Key)) continue;
            var value = LevelSettings.ToFloat(pair.Value.BoxedValue);
            if (value != null) settings.Add(new LevelSetting(LevelSettings.Key(pair.Key), value.Value));
        }
        return settings;
    }

    private static ModFingerprint Other(string name, string guid, Func<ConfigDefinition, bool> isLevel, Func<ulong> mask)
    {
        try
        {
            var plugin = ActivePlugin(guid);
            if (plugin == null) return new ModFingerprint(name, false, 0, 0);
            var settings = Settings(plugin, isLevel);
            var m = mask();
            return new ModFingerprint(name, true, LevelFingerprint.Compute(settings, m), m);
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"Level settings sync: couldn't fingerprint {name}: {e.Message}");
            return new ModFingerprint(name, false, 0, 0);
        }
    }

    private static ulong Mask(uint[] ids, Func<uint, bool> has)
    {
        var present = new bool[ids.Length];
        for (var i = 0; i < ids.Length; i++) present[i] = has(ids[i]);
        return LevelFingerprint.Mask(present);
    }
}

/// <summary>
/// G.EZ-T128 (GAI-T40): GTFO.EZ.AbsurdItems' level-affecting settings follow the host too. The host
/// sends them with its own level values, each key prefixed with <see cref="WirePrefix"/> (both mods
/// have a [Loot] section); a client hands the host's to AbsurdItems' public
/// <c>GTFO.EZ.AbsurdItems.HostLevelSettings</c> (found by reflection - an older AbsurdItems without it
/// keeps its own values and the fingerprint warning) before the level tweaks and AbsurdItems' kit loot
/// are run again. AbsurdItems reads them instead of its .cfg, which is never written.
/// </summary>
internal static class AbsurdItemsLevel
{
    internal const string Name = "GTFO.EZ.AbsurdItems";
    internal const string WirePrefix = "GTFO.EZ.AbsurdItems|";
    private const string Guid = "MindAttic.GTFO.EZ.AbsurdItems";
    private const string HookType = "GTFO.EZ.AbsurdItems.HostLevelSettings";

    private static bool _looked;
    private static MethodInfo? _set;
    private static string? _pushed;

    // AbsurdItems' settings that shape a level: the kit loot and Loot Mix ([Loot], all of it), which
    // of its items may spawn, and how many grenades a pickup holds (rolled from the level seed).
    // GAI-T37 split the Hand Grenade into the Frag and Plasma Grenades, each with its own section.
    internal static bool IsLevelSetting(ConfigDefinition d) =>
        d.Section == "Loot"
        || (d.Section is "Frag Grenade" or "Plasma Grenade" or "Hand Grenade"
            && d.Key is "Frag Grenade Enabled" or "Plasma Grenade Enabled" or "Hand Grenade Enabled" or "PickupCountMin" or "PickupCountMax")
        || (d.Section == "Stim Pack" && d.Key == "Stim Pack Enabled");

    /// <summary>Its level settings off its own .cfg (none when it isn't installed or is off).</summary>
    internal static List<LevelSetting> Local()
    {
        try
        {
            var plugin = ModFingerprints.ActivePlugin(Guid);
            return plugin == null ? new List<LevelSetting>() : ModFingerprints.Settings(plugin, IsLevelSetting);
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"Level settings sync: couldn't read {Name}'s settings: {e.Message}");
            return new List<LevelSetting>();
        }
    }

    /// <summary>Its level settings as the host sends them (prefixed keys).</summary>
    internal static List<LevelSetting> Wire()
    {
        var list = Local();
        for (var i = 0; i < list.Count; i++) list[i] = list[i] with { Key = WirePrefix + list[i].Key };
        return list;
    }

    /// <summary>Whether this AbsurdItems takes the host's values (has the hook).</summary>
    internal static bool FollowsHost => Hook() != null;

    /// <summary>The values its kit loot uses now (prefixed keys): the host's where held and taken, else its own.</summary>
    internal static List<LevelSetting> Effective()
    {
        var list = Wire();
        if (!FollowsHost) return list;
        for (var i = 0; i < list.Count; i++) list[i] = list[i] with { Value = LevelSettings.Host.Resolve(list[i].Key, list[i].Value) };
        return list;
    }

    /// <summary>
    /// Hands AbsurdItems the host's values for its settings (none: back to its own), when they changed
    /// since the last time. Called before every apply check, so they're in place before a re-apply.
    /// </summary>
    internal static void Push()
    {
        var set = Hook();
        if (set == null) return;
        var values = new Dictionary<string, float>(StringComparer.Ordinal);
        if (LevelSettings.Host.Active)
            foreach (var s in Local())
                if (LevelSettings.Host.TryGet(WirePrefix + s.Key, out var host)) values[s.Key] = host;

        var keys = new List<string>(values.Keys);
        keys.Sort(StringComparer.Ordinal);
        var state = string.Join(";", keys.ConvertAll(k => k + "=" + values[k].ToString("R", CultureInfo.InvariantCulture)));
        if (state == _pushed) return;
        _pushed = state; // tried once per change; a failure is said once, not every second
        try
        {
            set.Invoke(null, new object?[] { values });
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"Level settings sync: couldn't hand {Name} the host's values - its loot uses your own: {e.InnerException?.Message ?? e.Message}");
        }
    }

    private static MethodInfo? Hook()
    {
        if (_looked) return _set;
        var plugin = ModFingerprints.ActivePlugin(Guid);
        if (plugin == null) return null; // looked for again later
        _looked = true;
        try
        {
            _set = plugin.GetType().Assembly.GetType(HookType, false)
                ?.GetMethod("Set", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(IDictionary<string, float>) }, null);
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"Level settings sync: couldn't look for {Name}'s host-settings hook: {e.Message}");
        }
        Plugin.Logger.LogInfo(_set != null
            ? $"Level settings sync: {Name}'s level settings follow the host too."
            : $"Level settings sync: this {Name} can't take the host's level settings (an older version?) - a difference there is only warned about.");
        return _set;
    }

    /// <summary>Whether a fingerprint difference line is settled by using the host's values.</summary>
    internal static bool Fixes(string line) =>
        line.StartsWith(Plugin.NAME + ":", StringComparison.Ordinal)
        || (FollowsHost && line.StartsWith(Name + ": level settings differ", StringComparison.Ordinal));
}

/// <summary>
/// G.EZ-T125 over GTFO-API. In someone else's lobby your game sends the host your fingerprints
/// (Hello) whenever the host changes; the host compares them with its own, says what differs in its
/// event log naming you, and answers with its fingerprints and level values (Host) - which it also
/// broadcasts every few seconds while no level is built, and straight away when they change. Your
/// game compares, says what differs, and re-applies the level tweaks with the host's values
/// (GameDataInit_Initialize_Patch.ReapplyLevelTweaks) before the level is built. Leaving the
/// lobby or hosting one yourself goes back to your own values.
/// </summary>
internal static class LevelSettingsSync
{
    private const string HelloEvent = "MindAttic.GTFO.EZ.LevelSettings.Hello";
    private const string HostEvent = "MindAttic.GTFO.EZ.LevelSettings.Host";

    private const float HostHeartbeatSeconds = 5f;
    private const float HelloRetrySeconds = 5f;
    private const float CheckSeconds = 1f;

    private static bool _registered;
    private static bool _broken;
    private static ulong _master;
    private static bool _hostValuesReceived;
    private static float _nextHello;
    private static float _nextHeartbeat;
    private static float _nextCheck;
    private static ulong _lastBroadcastHash;
    private static bool _levelBuilt;
    private static bool _tooLateWarned;
    private static string _lastHostDifferences = "";
    private static readonly List<string> Notices = new();

    internal static void Init()
    {
        try
        {
            LevelAPI.OnBuildStart += OnBuildStart;
            LevelAPI.OnEnterLevel += OnEnterLevel;
            LevelAPI.OnLevelCleanup += OnLevelCleanup;
        }
        catch (Exception e)
        {
            _broken = true;
            Plugin.Logger.LogWarning($"Level settings sync is off (GTFO-API unavailable?): {e.Message}");
        }
    }

    internal static void Tick()
    {
        if (_broken || Time.unscaledTime < _backoffUntil) return;
        try
        {
            Register();
            var now = Time.unscaledTime;
            var master = SNet.Master;
            var masterLookup = master != null ? master.Lookup : 0UL;
            if (masterLookup != _master) OnMasterChanged(masterLookup);

            var client = IsClientOfAnotherHost();
            // A few tries; after that the host's own heartbeat still reaches us if it runs GTFO.EZ.
            if (client && !_hostValuesReceived && now >= _nextHello && _hellos < MaxHellos)
            {
                _nextHello = now + HelloRetrySeconds;
                _hellos++;
                NetworkAPI.InvokeFreeSizedEvent(HelloEvent, LevelSettingsCodec.EncodeHello(ModFingerprints.Local()), master);
            }

            if (SNet.IsMaster && master != null && !_levelBuilt)
            {
                var payload = HostPayload(out var hash);
                if (now >= _nextHeartbeat || hash != _lastBroadcastHash)
                {
                    _nextHeartbeat = now + HostHeartbeatSeconds;
                    _lastBroadcastHash = hash;
                    NetworkAPI.InvokeFreeSizedEvent(HostEvent, payload);
                }
            }

            if (now >= _nextCheck)
            {
                _nextCheck = now + CheckSeconds;
                EnsureApplied();
            }
        }
        catch (Exception e)
        {
            // A send can fail between lobbies (no session yet); try again shortly, saying so once.
            _backoffUntil = Time.unscaledTime + BackoffSeconds;
            if (_errors++ < 3) Plugin.Logger.LogWarning($"Level settings sync hit an error (retrying in {BackoffSeconds:0} s): {e.Message}");
        }
    }

    private const float BackoffSeconds = 5f;
    private static float _backoffUntil;
    private static int _errors;
    private const int MaxHellos = 6;
    private static int _hellos;

    private static bool IsClientOfAnotherHost() => !SNet.IsMaster && SNet.Master != null && SNet.LocalPlayer != null && SNet.Master.Lookup != SNet.LocalPlayer.Lookup;

    private static void Register()
    {
        if (_registered) return;
        _registered = true;
        if (!NetworkAPI.IsEventRegistered(HelloEvent)) NetworkAPI.RegisterFreeSizedEvent(HelloEvent, Safe(OnHello, HelloEvent));
        if (!NetworkAPI.IsEventRegistered(HostEvent)) NetworkAPI.RegisterFreeSizedEvent(HostEvent, Safe(OnHost, HostEvent));
    }

    // A handler that can't throw (see GTFO.EZ.Shared's RemoteEvents for why).
    private static Action<ulong, byte[]> Safe(Action<ulong, byte[]> handler, string name) => (sender, bytes) =>
    {
        try
        {
            handler(sender, bytes);
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"Couldn't handle {name} from {sender}: {e}");
        }
    };

    private static byte[] HostPayload(out ulong hash)
    {
        var mods = ModFingerprints.Local();
        var values = LevelSettings.Local();
        values.AddRange(AbsurdItemsLevel.Wire()); // G.EZ-T128
        hash = LevelFingerprint.Compute(values, 0);
        foreach (var m in mods) hash = hash * 31 + m.SettingsHash + m.InjectedMask + (m.Installed ? 1UL : 0UL);
        return LevelSettingsCodec.EncodeHost(mods, values);
    }

    private static void OnMasterChanged(ulong master)
    {
        _master = master;
        _hostValuesReceived = false;
        _nextHello = 0f;
        _hellos = 0;
        _nextHeartbeat = 0f;
        _lastHostDifferences = "";
        Notices.Clear();
        if (!LevelSettings.Host.Active) return;
        // A new lobby, hosting one yourself, or back to the menu: your own values again until (and
        // unless) a new host sends theirs.
        LevelSettings.Host.Clear();
        Plugin.Logger.LogInfo("Level settings sync: no longer in that host's lobby - back to your own level settings.");
        EnsureApplied();
    }

    // On the host: a player joined (or their host changed) and sent their fingerprints.
    private static void OnHello(ulong sender, byte[] bytes)
    {
        if (!SNet.IsMaster) return;
        if (!LevelSettingsCodec.TryDecodeHello(bytes, out var theirs))
        {
            Plugin.Logger.LogWarning($"Level settings sync: {Name(sender)} sent fingerprints this GTFO.EZ version can't read (a different GTFO.EZ version?).");
            return;
        }
        var differences = FingerprintComparison.Differences(ModFingerprints.Local(), theirs, Name(sender));
        foreach (var line in differences)
            Notify($"{Name(sender)}: {line}" + (line.StartsWith(Plugin.NAME + ":") ? " - their game uses your GTFO.EZ level settings"
                : line.StartsWith(AbsurdItemsLevel.Name + ": level settings differ", StringComparison.Ordinal) ? " - their game uses yours if its GTFO.EZ.AbsurdItems is up to date"
                : " - their level may differ from yours"));
        if (differences.Count == 0) Plugin.Logger.LogInfo($"Level settings sync: {Name(sender)}'s level settings match yours.");

        if (SNet.TryGetPlayer(sender, out var player) && player != null)
            NetworkAPI.InvokeFreeSizedEvent(HostEvent, HostPayload(out _), player);
    }

    // On a client: the host's fingerprints and level values.
    private static void OnHost(ulong sender, byte[] bytes)
    {
        if (SNet.IsMaster) return;
        var master = SNet.Master;
        if (master == null || master.Lookup != sender) return;
        if (!LevelSettingsCodec.TryDecodeHost(bytes, out var mods, out var values))
        {
            if (!_hostValuesReceived) Notify("The host's GTFO.EZ is a version whose level settings this one can't read - this level is built from your own and may differ from the host's");
            _hostValuesReceived = true; // stop asking; the warning above says what happens
            return;
        }

        var first = !_hostValuesReceived;
        _hostValuesReceived = true;
        LevelSettings.Host.Set(values);

        var differences = FingerprintComparison.Differences(ModFingerprints.Local(), mods, "the host");
        var joined = string.Join("\n", differences);
        if (joined != _lastHostDifferences)
        {
            _lastHostDifferences = joined;
            foreach (var line in differences)
                Notify(line + (AbsurdItemsLevel.Fixes(line) ? " - using the host's" : " - your level may differ from the host's"));
        }
        if (first)
        {
            var local = LevelSettings.Local();
            if (AbsurdItemsLevel.FollowsHost) local.AddRange(AbsurdItemsLevel.Wire());
            var replaced = LevelSettings.Host.Differences(local);
            foreach (var (key, mine, host) in replaced) Plugin.Logger.LogInfo($"Level settings sync: [{key}] {mine} -> host's {host}");
            foreach (var key in LevelSettings.Host.Missing(local)) Plugin.Logger.LogInfo($"Level settings sync: the host didn't send [{key}] - your own value stays.");
            Plugin.Logger.LogInfo($"Level settings sync: host's level settings received ({replaced.Count} differ from yours).");
        }
        EnsureApplied();
    }

    // The datablocks always hold what's wanted now, unless a level is already built from them.
    private static void EnsureApplied()
    {
        AbsurdItemsLevel.Push(); // G.EZ-T128: AbsurdItems holds the wanted values before any re-apply
        var decision = LevelSyncRules.Decide(GameDataInit_Initialize_Patch.LevelSnapshotTaken,
            GameDataInit_Initialize_Patch.AppliedLevelSignature, LevelSettings.EffectiveSignature(), _levelBuilt);
        switch (decision)
        {
            case LevelReapply.Reapply:
                GameDataInit_Initialize_Patch.ReapplyLevelTweaks(LevelSettings.Host.Active ? "the host's level settings" : "your own level settings");
                _tooLateWarned = false;
                break;
            case LevelReapply.TooLate when !_tooLateWarned:
                _tooLateWarned = true;
                Notify("The host's GTFO.EZ level settings changed after this level was built - they apply from the next level");
                break;
        }
    }

    // Level generation starts: last chance to apply, and say so if the host never sent anything.
    private static void OnBuildStart()
    {
        try
        {
            if (!_broken)
            {
                EnsureApplied();
                if (LevelSyncRules.WarnNoHostValues(IsClientOfAnotherHost(), _hostValuesReceived))
                    Notify("No level settings came from the host's GTFO.EZ (not installed, or an older version?) - this level is built from your own settings and may not match the host's");
            }
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"Level settings sync: build-start check failed: {e.Message}");
        }
        _levelBuilt = true;
    }

    // In the level the event log is on screen: what was said in the lobby is said again there.
    private static void OnEnterLevel()
    {
        foreach (var notice in Notices) MessageHelper.Show(notice, MessageType.EventLog);
        Notices.Clear();
    }

    private static void OnLevelCleanup()
    {
        _levelBuilt = false;
        _tooLateWarned = false;
    }

    private static void Notify(string message)
    {
        Plugin.Logger.LogWarning($"Level settings sync: {message}");
        if (!Notices.Contains(message)) Notices.Add(message);
        MessageHelper.Show(message, MessageType.EventLog);
    }

    private static string Name(ulong lookup)
    {
        try
        {
            return SNet.TryGetPlayer(lookup, out var player) && player != null && !string.IsNullOrWhiteSpace(player.NickName)
                ? player.NickName
                : "A player";
        }
        catch (Exception)
        {
            return "A player";
        }
    }
}

/// <summary>Drives <see cref="LevelSettingsSync.Tick"/> every frame, in the lobby as well as in a level.</summary>
internal sealed class LevelSettingsSyncDriver : MonoBehaviour
{
    public LevelSettingsSyncDriver(IntPtr pointer) : base(pointer) { }

    private void Update() => LevelSettingsSync.Tick();
}
