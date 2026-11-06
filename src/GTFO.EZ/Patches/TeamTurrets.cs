using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Unity.IL2CPP;
using GTFO.API;
using GTFO.EZ.Core;
using MindAttic.GTFO.Shared;
using Player;
using SNetwork;
using UnityEngine;

namespace GTFO.EZ.Patches;

// G.EZ-T120: the shared team turret pool. Every human's slot 4 is every bot's sentry that isn't
// down - one pool the whole team draws on - and then, last, their own. Your own always goes down
// last (user decision), so it stays in your hands, placement ghost and all, while you empty the bot
// queue: the Tool key (4) pressed again with your sentry in hand cycles the available bot turrets,
// with a label naming whose turret is selected and how many bot turrets are left before yours, and E
// places the selected bot's turret at your ghost. Only once no bot turret is left (placed by anyone)
// does your own become the selection, and E then places it like vanilla - after which the existing
// postfix puts slot 4 away and returns your previous weapon. If someone else places the bot turret
// you have selected, the selection moves on to the next available one (or your own if none is left).
//
// No duplicates, ever: nothing here creates a sentry. A turret is only ever placed through its
// owner's own backpack item - yours through vanilla PlaceOnGround, a bot's through that bot's own
// SentryGunFirstPerson via the bot AI's PlaceSentryGunOnGround - and only while that item isn't
// deployed. Bots belong to the host, so every bot placement, a client's included, is decided on the
// host by TeamTurretPool.HostAccepts against the team's state at that moment, and the bot is marked
// deployed in the same call; a client never places a bot turret itself, it asks and waits. Requests
// are handled one at a time, so of two players asking for the same bot turret the first wins and the
// second is refused. However you cut it, the turrets the team brought in are the only ones placed.
internal static class TeamTurrets
{
    internal sealed class Entry
    {
        public ulong Bot;         // 0 = your own; otherwise the bot's SNet lookup
        public string Name = "";
        public bool Available;
    }

    // The selection is kept by owner, not position - the list's shape changes as turrets go down.
    private static ulong _selected;
    private static bool _hasSelection;
    private static ulong _justPlaced;          // a bot turret this player just placed/asked for, for the label
    private static bool _toolWieldedLastFrame;
    private static float _lastTeamPlaceAt = -10f;

    internal static float LastTeamPlaceAt => _lastTeamPlaceAt;

    internal static string Label(ulong bot) => bot == 0 ? "your turret" : $"{Bots.NameOf(bot)}'s turret";

    // Your own first (entry 0 - available only once no bot's is, and never when you carry no sentry),
    // then the bots: from the backpacks on the host, from the host's broadcast on a client.
    internal static List<Entry> Build()
    {
        var list = new List<Entry> { new Entry { Bot = 0, Name = Label(0) } };

        if (SNet.IsMaster)
        {
            foreach (var b in Bots.Sentries())
                list.Add(new Entry { Bot = b.Lookup, Name = Label(b.Lookup), Available = !b.Deployed });
        }
        else
        {
            var fresh = TeamTurretNet.HostStateFresh;
            foreach (var (bot, deployed) in TeamTurretNet.HostView)
                list.Add(new Entry { Bot = bot, Name = Label(bot), Available = TeamTurretPool.ClientBotAvailable(fresh, deployed, TeamTurretNet.Pending(bot)) });
        }

        var backpack = PlayerBackpackManager.LocalBackpack;
        var ownUndeployed = BotDeployAtAim.LocalSentry() != null && backpack != null && !backpack.IsDeployed(InventorySlot.GearClass);
        list[0].Available = TeamTurretPool.OwnSelectable(ownUndeployed, AvailableBots(Availability(list)));
        return list;
    }

    private static bool[] Availability(List<Entry> entries)
    {
        var a = new bool[entries.Count];
        for (var i = 0; i < a.Length; i++) a[i] = entries[i].Available;
        return a;
    }

    private static int IndexOf(List<Entry> entries, ulong bot)
    {
        for (var i = 0; i < entries.Count; i++) if (entries[i].Bot == bot) return i;
        return TeamTurretPool.None;
    }

    private static int AvailableBots(bool[] available)
    {
        var n = 0;
        for (var i = 1; i < available.Length; i++) if (available[i]) n++;
        return n;
    }

    // Re-resolves the selection against the team's state right now: the selected bot's lookup, or 0
    // for your own (or nothing). E's placement uses this so it never acts on a frame-old selection.
    internal static ulong ResolveNow()
    {
        var entries = Build();
        Select(entries, TeamTurretPool.Resolve(Availability(entries), _hasSelection ? IndexOf(entries, _selected) : TeamTurretPool.None));
        return _hasSelection ? _selected : 0UL;
    }

    // Every frame for the local player in a level (DeployPickupHotkeys' Dam_PlayerDamageLocal.Update postfix).
    internal static void Tick()
    {
        TeamTurretNet.Tick();
        if (!PlayerManager.TryGetLocalPlayerAgent(out var agent) || agent?.Inventory == null) return;

        var wielded = agent.Inventory.WieldedSlot;
        var wieldingOwnSentry = wielded == InventorySlot.GearClass && BotDeployAtAim.LocalSentry() != null;

        var entries = Build();
        var available = Availability(entries);
        var current = _hasSelection ? IndexOf(entries, _selected) : TeamTurretPool.None;
        var resolved = TeamTurretPool.Resolve(available, current);
        if (resolved != current)
        {
            if (wieldingOwnSentry && _hasSelection && current >= 0 && resolved != TeamTurretPool.None)
            {
                var gone = entries[current];
                var mine = gone.Bot != 0 && gone.Bot == _justPlaced;
                MessageHelper.Show(mine
                    ? $"{(SNet.IsMaster ? "Placed" : "Placing")} {gone.Name} - next: {Describe(entries, available, resolved)}"
                    : $"{Capital(gone.Name)} was placed by someone else - now: {Describe(entries, available, resolved)}",
                    MessageType.Center, 2.5f);
            }
            Select(entries, resolved);
        }

        if (Hotkeys.InPlay && InputMapper.GetButtonDownKeyMouse(InputAction.SelectTool, FocusStateManager.CurrentState)
            && TeamTurretPool.DecideToolKey(AbsurdWeaponsCompat.CyclingOn(), wieldingOwnSentry, _toolWieldedLastFrame) == ToolKeyAction.Cycle)
        {
            Select(entries, TeamTurretPool.Next(available, _hasSelection ? IndexOf(entries, _selected) : TeamTurretPool.None));
            ShowSelection(entries, available);
        }
        else if (wieldingOwnSentry && !_toolWieldedLastFrame && _hasSelection && _selected != 0)
        {
            // Just took slot 4 out with bot turrets queued: say what E will place.
            ShowSelection(entries, available);
        }
        _toolWieldedLastFrame = wielded == InventorySlot.GearClass;
    }

    private static void Select(List<Entry> entries, int index)
    {
        _hasSelection = index >= 0;
        _selected = index >= 0 ? entries[index].Bot : 0UL;
    }

    private static void ShowSelection(List<Entry> entries, bool[] available)
    {
        var index = _hasSelection ? IndexOf(entries, _selected) : TeamTurretPool.None;
        MessageHelper.Show(index < 0 ? "No turrets left to place" : $"Turret: {Describe(entries, available, index)}", MessageType.Center, 3f);
    }

    // "Woods's turret  [1/2] - 2 bot turrets before yours", or "your turret (last one)".
    private static string Describe(List<Entry> entries, bool[] available, int index)
    {
        if (entries[index].Bot == 0) return $"{entries[index].Name} (last one)";
        var bots = AvailableBots(available);
        var position = 0;
        for (var i = 1; i <= index; i++) if (available[i]) position++;
        return $"{entries[index].Name}  [{position}/{bots}] - {bots} bot turret{(bots == 1 ? "" : "s")} before yours";
    }

    private static string Capital(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);

    // Places one bot's own turret at a pose. On the host: now, if HostAccepts says the bot's sentry is
    // still undeployed. On a client: only a request - the host checks the team's current state and
    // places it or refuses; nothing is put down locally. True when it went down (host) or the request
    // went out (client): the press is spent and the bot is off this player's list until the host says.
    internal static bool PlaceBot(ulong bot, Pose pose, string logTag)
    {
        if (bot == 0) return false;
        bool ok;
        if (SNet.IsMaster)
        {
            ok = HostPlaceBot(bot, pose, logTag);
            if (ok) TeamTurretNet.BroadcastNow();
            else MessageHelper.Show($"{Capital(Label(bot))} is already down", MessageType.Center, 2.5f);
        }
        else
        {
            ok = TeamTurretNet.RequestPlace(bot, pose);
        }
        if (ok)
        {
            _justPlaced = bot;
            _lastTeamPlaceAt = Time.time;
        }
        return ok;
    }

    // The one place a bot's turret is ever put down, for the host's own presses and every client's
    // request alike. TeamTurretPool.HostAccepts is the no-duplicates gate: the bot must still carry an
    // undeployed sentry at this moment, and it's marked deployed right here, before anything else can ask.
    internal static bool HostPlaceBot(ulong lookup, Pose pose, string logTag)
    {
        if (!SNet.IsMaster) return false;
        var found = Bots.Find(lookup, out var bot, out var backpack, out var item, out var isSentry);
        var deployed = found && backpack!.IsDeployed(InventorySlot.GearClass);
        if (!TeamTurretPool.HostAccepts(found, isSentry, deployed))
        {
            Plugin.Logger.LogInfo($"{logTag}: refused {Label(lookup)} - {(!found ? "no such bot" : !isSentry ? "it isn't carrying a sentry" : "already deployed")}.");
            return false;
        }

        try
        {
            var desc = new PlayerBotActionDeploySentryGun.Descriptor(bot!) { InstallationPose = pose };
            var action = new PlayerBotActionDeploySentryGun(desc) { m_backpackItem = item! };
            action.PlaceSentryGunOnGround();
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"{logTag}: couldn't place {Label(lookup)}. {e}");
            return false;
        }
        // Placed-but-still-owned, the bookkeeping the bot's own AI does after this call (clearing the
        // slot instead orphaned the turret and gave Z nothing to recall) - and the flag every later
        // request is checked against.
        backpack!.SetDeployed(InventorySlot.GearClass, true);
        Plugin.Logger.LogInfo($"{logTag}: placed {Label(lookup)}.");
        return true;
    }

    // Shift+E: a bot's turret, never your own - the selected bot's, else the first available - at
    // your held sentry's placement ghost.
    internal static void ShiftE()
    {
        var entries = Build();
        var pick = TeamTurretPool.BotPick(Availability(entries), _hasSelection ? IndexOf(entries, _selected) : TeamTurretPool.None);
        if (pick == TeamTurretPool.None)
        {
            Plugin.Logger.LogInfo("Shift+E: no bot turret left to place.");
            MessageHelper.Show("No bot turrets left to place", MessageType.Center, 2.5f);
            return;
        }

        Pose pose;
        try
        {
            if (!BotDeployAtAim.TryGetAimPose(out pose))
            {
                Plugin.Logger.LogInfo("Shift+E: aim your own equipped sentry at a valid placement spot first - that's the aim this reuses.");
                return;
            }
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"Shift+E: couldn't read your placement aim. {e}");
            return;
        }
        PlaceBot(entries[pick].Bot, pose, "Shift+E");
    }

    // X: every available bot turret first, then your own - the same own-last order as E.
    internal static void QuickDrop(Pose pose)
    {
        var entries = Build();
        var pick = TeamTurretPool.QuickDropPick(Availability(entries));
        if (pick == TeamTurretPool.None)
        {
            Plugin.Logger.LogInfo("X: nothing left to drop - every turret is already placed.");
            return;
        }
        if (pick != TeamTurretPool.Own)
        {
            PlaceBot(entries[pick].Bot, pose, "X");
            return;
        }
        if (OwnSentryAutoPlace.TryPlaceAt(pose)) Plugin.Logger.LogInfo("X: placed your sentry.");
    }

    internal static void OnRequestRefused(ulong bot) =>
        MessageHelper.Show($"Someone else got {Label(bot)} first", MessageType.Center, 2.5f);
}

// Bots belong to the host: a client asks the host to place a bot's turret, and learns which bot
// turrets are down from the host's broadcast (sent on every change and once a second). A client
// whose host doesn't run this (no broadcast) sees no bot turrets at all - only its own.
internal static class TeamTurretNet
{
    private const string StateEvent = "GTFO.EZ.TeamTurrets.State";
    private const string RequestEvent = "GTFO.EZ.TeamTurrets.Request";
    private const string ResultEvent = "GTFO.EZ.TeamTurrets.Result";
    private const byte KindPlace = 1;
    private const byte KindRecallBots = 2;
    private const int MaxBots = 4;
    private const float HeartbeatSeconds = 1f;

    private struct PoolState
    {
        public byte Count;
        public byte DeployedMask;
        public ulong Bot0, Bot1, Bot2, Bot3;
    }

    private struct Request
    {
        public byte Kind;
        public ulong Bot;
        public float Px, Py, Pz, Rx, Ry, Rz, Rw;
    }

    private struct Result
    {
        public ulong Requester;
        public ulong Bot;
        public byte Ok;
    }

    private static bool _registered;
    private static float _nextHeartbeat;
    private static PoolState _lastSent;
    private static readonly List<(ulong Bot, bool Deployed)> _hostView = new();
    private static float _hostViewAt = -100f;
    private static readonly Dictionary<ulong, float> _pendingUntil = new();

    internal static IReadOnlyList<(ulong Bot, bool Deployed)> HostView => _hostView;
    internal static bool HostStateFresh => Time.unscaledTime - _hostViewAt < TeamTurretPool.HostStateFreshSeconds;
    internal static bool Pending(ulong bot) => _pendingUntil.TryGetValue(bot, out var until) && Time.unscaledTime < until;

    internal static void Tick()
    {
        Register();
        if (!SNet.IsMaster) return;
        var state = Snapshot();
        var now = Time.unscaledTime;
        if (now < _nextHeartbeat && Same(state, _lastSent)) return;
        Send(state);
    }

    internal static void BroadcastNow()
    {
        if (SNet.IsMaster) Send(Snapshot());
    }

    private static void Send(PoolState state)
    {
        try
        {
            NetworkAPI.InvokeEvent(StateEvent, state);
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"Team turrets: couldn't send the pool state. {e}");
        }
        _lastSent = state;
        _nextHeartbeat = Time.unscaledTime + HeartbeatSeconds;
    }

    private static PoolState Snapshot()
    {
        var state = new PoolState();
        foreach (var b in Bots.Sentries())
        {
            if (state.Count >= MaxBots) break;
            switch (state.Count)
            {
                case 0: state.Bot0 = b.Lookup; break;
                case 1: state.Bot1 = b.Lookup; break;
                case 2: state.Bot2 = b.Lookup; break;
                default: state.Bot3 = b.Lookup; break;
            }
            if (b.Deployed) state.DeployedMask |= (byte)(1 << state.Count);
            state.Count++;
        }
        return state;
    }

    private static bool Same(PoolState a, PoolState b) =>
        a.Count == b.Count && a.DeployedMask == b.DeployedMask && a.Bot0 == b.Bot0 && a.Bot1 == b.Bot1 && a.Bot2 == b.Bot2 && a.Bot3 == b.Bot3;

    internal static bool RequestPlace(ulong bot, Pose pose)
    {
        var master = SNet.Master;
        if (master == null) return false;
        Register();
        try
        {
            NetworkAPI.InvokeEvent(RequestEvent, new Request
            {
                Kind = KindPlace, Bot = bot,
                Px = pose.position.x, Py = pose.position.y, Pz = pose.position.z,
                Rx = pose.rotation.x, Ry = pose.rotation.y, Rz = pose.rotation.z, Rw = pose.rotation.w,
            }, master);
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"Team turrets: couldn't ask the host to place {TeamTurrets.Label(bot)}. {e}");
            return false;
        }
        _pendingUntil[bot] = Time.unscaledTime + TeamTurretPool.ClientPendingSeconds;
        return true;
    }

    internal static void RequestRecallBots()
    {
        var master = SNet.Master;
        if (master == null || !HostStateFresh) return;
        Register();
        try
        {
            NetworkAPI.InvokeEvent(RequestEvent, new Request { Kind = KindRecallBots }, master);
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"Team turrets: couldn't ask the host to recall the bots' turrets. {e}");
        }
    }

    private static void Register()
    {
        if (_registered) return;
        _registered = true;
        if (!NetworkAPI.IsEventRegistered(StateEvent)) RemoteEvents.Register<PoolState>(StateEvent, OnState);
        if (!NetworkAPI.IsEventRegistered(RequestEvent)) RemoteEvents.Register<Request>(RequestEvent, OnRequest);
        if (!NetworkAPI.IsEventRegistered(ResultEvent)) RemoteEvents.Register<Result>(ResultEvent, OnResult);
    }

    // On a client: the host's bot turrets.
    private static void OnState(ulong sender, PoolState state)
    {
        if (SNet.IsMaster) return;
        var master = SNet.Master;
        if (master == null || master.Lookup != sender) return;

        _hostView.Clear();
        for (var i = 0; i < Math.Min((int)state.Count, MaxBots); i++)
        {
            var bot = i switch { 0 => state.Bot0, 1 => state.Bot1, 2 => state.Bot2, _ => state.Bot3 };
            var deployed = (state.DeployedMask & (1 << i)) != 0;
            _hostView.Add((bot, deployed));
            if (deployed) _pendingUntil.Remove(bot);
        }
        _hostViewAt = Time.unscaledTime;
    }

    // On the host: a client wants a bot's turret placed (or every bot's recalled).
    private static void OnRequest(ulong sender, Request request)
    {
        if (!SNet.IsMaster) return;
        if (!Plugin.BotsDeployOnShiftE.Value)
        {
            if (request.Kind == KindPlace) Reply(sender, request.Bot, false);
            return;
        }

        if (request.Kind == KindRecallBots)
        {
            PickupAllBotTools.Activate();
            BroadcastNow();
            return;
        }
        if (request.Kind != KindPlace) return;

        var position = new Vector3(request.Px, request.Py, request.Pz);
        var rotation = new Quaternion(request.Rx, request.Ry, request.Rz, request.Rw);
        var ok = Finite(position) && Finite(rotation)
            && TeamTurrets.HostPlaceBot(request.Bot, new Pose(position, Normalized(rotation)), $"Request from {Bots.PlayerName(sender)}");
        Reply(sender, request.Bot, ok);
        BroadcastNow();
        if (ok) MessageHelper.Show($"{Bots.PlayerName(sender)} placed {TeamTurrets.Label(request.Bot)}", MessageType.EventLog);
    }

    private static void Reply(ulong requester, ulong bot, bool ok)
    {
        try
        {
            NetworkAPI.InvokeEvent(ResultEvent, new Result { Requester = requester, Bot = bot, Ok = (byte)(ok ? 1 : 0) });
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"Team turrets: couldn't answer a placement request. {e}");
        }
    }

    // On a client: the host's answer to our own request.
    private static void OnResult(ulong sender, Result result)
    {
        if (SNet.IsMaster) return;
        var master = SNet.Master;
        if (master == null || master.Lookup != sender) return;
        if (SNet.LocalPlayer == null || result.Requester != SNet.LocalPlayer.Lookup) return;
        _pendingUntil.Remove(result.Bot);
        if (result.Ok == 0) TeamTurrets.OnRequestRefused(result.Bot);
    }

    private static bool Finite(Vector3 v) => float.IsFinite(v.x) && float.IsFinite(v.y) && float.IsFinite(v.z);
    private static bool Finite(Quaternion q) => float.IsFinite(q.x) && float.IsFinite(q.y) && float.IsFinite(q.z) && float.IsFinite(q.w);

    private static Quaternion Normalized(Quaternion q)
    {
        var m = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
        return m < 1e-4f ? Quaternion.identity : new Quaternion(q.x / m, q.y / m, q.z / m, q.w / m);
    }
}

// GTFO.EZ.AbsurdWeapons' GearCycler also listens to the Tool key - pressed again with slot 4 in
// hand, it swaps the tool for one of its own - but only while its End toggle has it switched on.
// Then it owns the key and the turret selection stands down, so one press never does both. Its
// state is a private field of another plugin, read by reflection; any failure means "not on".
internal static class AbsurdWeaponsCompat
{
    private const string Guid = "MindAttic.GTFO.EZ.AbsurdWeapons";
    private static bool _unavailable;
    private static FieldInfo? _active;
    private static Type? _type;
    private static object? _instance;

    internal static bool CyclingOn()
    {
        if (_unavailable) return false;
        try
        {
            if (_type == null)
            {
                if (!IL2CPPChainloader.Instance.Plugins.ContainsKey(Guid)) { _unavailable = true; return false; }
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    _type = assembly.GetType("GTFO.EZ.AbsurdWeapons.GearCycler", false);
                    if (_type != null) break;
                }
                _active = _type?.GetField("active", BindingFlags.Instance | BindingFlags.NonPublic);
                if (_type == null || _active == null)
                {
                    _unavailable = true;
                    Plugin.Logger.LogWarning("Team turrets: GTFO.EZ.AbsurdWeapons is installed but its gear cycling state wasn't found; the Tool key cycles turrets regardless.");
                    return false;
                }
            }

            if (_instance == null)
            {
                foreach (var obj in Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.From(_type)))
                {
                    _instance = Il2CppInterop.Runtime.Runtime.ClassInjectorBase.GetMonoObjectFromIl2CppPointer(obj.Pointer);
                    if (_instance != null) break;
                }
                if (_instance == null) return false;
            }
            return _active!.GetValue(_instance) is true;
        }
        catch (Exception e)
        {
            _unavailable = true;
            Plugin.Logger.LogWarning($"Team turrets: couldn't read GTFO.EZ.AbsurdWeapons' gear cycling state; the Tool key cycles turrets regardless. {e}");
            return false;
        }
    }
}
