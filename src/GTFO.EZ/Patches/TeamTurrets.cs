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
// host by TeamTurretPool.HostDecide (HostAccepts) against the team's state at that moment, and the bot
// is marked deployed in the same call; a client never places a bot turret itself, it asks and waits.
// Requests are handled one at a time, so of two players asking for the same bot turret the first wins
// and the second is refused. However you cut it, the turrets the team brought in are the only ones placed.
//
// G.EZ-T124: the counts are the same on every machine. A client that has no fresh word from the host
// shows "Team turret pool unavailable (host)" and keeps holding its own (own-last) instead of
// counting 0 bot turrets; a host that says its pool is off - or never sends one - means your own
// places like vanilla. Pool states carry a sequence number (a late one can't undo a placement), a
// request stays pending until the host answers that request id, a successful answer marks the bot
// down right away, refusals say why (taken / failed / off), and every placement, failure and Z recall
// is announced in every player's event log with how many bot turrets are left.
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
    private static HostPoolStatus _lastStatus = HostPoolStatus.Unknown;
    private static bool _offShown;

    internal static float LastTeamPlaceAt => _lastTeamPlaceAt;

    // The host runs the pool itself; a client goes by what the host has told it.
    internal static HostPoolStatus Status => SNet.IsMaster ? HostPoolStatus.Live : TeamTurretNet.Status;
    internal static bool PoolUnknown => Status == HostPoolStatus.Unknown;

    internal static string Label(ulong bot) => bot == 0 ? "your turret" : $"{Bots.NameOf(bot)}'s turret";

    // A new level: nothing of the last one's selection carries over (TeamTurretNet resets its side).
    internal static void ResetLevel()
    {
        _selected = 0;
        _hasSelection = false;
        _justPlaced = 0;
        _toolWieldedLastFrame = false;
        _lastTeamPlaceAt = -10f;
        _lastStatus = HostPoolStatus.Unknown;
        _offShown = false;
    }

    // Your own first (entry 0 - available only once no bot's is, never while the host's pool is
    // unknown, and never when you carry no sentry), then the bots: from the backpacks on the host,
    // from the host's broadcast on a client.
    internal static List<Entry> Build()
    {
        var list = new List<Entry> { new Entry { Bot = 0, Name = Label(0) } };
        var status = Status;

        if (SNet.IsMaster)
        {
            foreach (var b in Bots.Sentries())
                list.Add(new Entry { Bot = b.Lookup, Name = Label(b.Lookup), Available = !b.Deployed && !TeamTurretNet.HostCooling(b.Lookup) });
        }
        else
        {
            foreach (var v in TeamTurretNet.HostView)
                list.Add(new Entry { Bot = v.Bot, Name = Label(v.Bot), Available = TeamTurretPool.ClientBotAvailable(status, v.Deployed, v.Cooling, TeamTurretNet.Pending(v.Bot)) });
        }

        var backpack = PlayerBackpackManager.LocalBackpack;
        var ownUndeployed = BotDeployAtAim.LocalSentry() != null && backpack != null && !backpack.IsDeployed(InventorySlot.GearClass);
        list[0].Available = TeamTurretPool.OwnSelectable(ownUndeployed, AvailableBots(Availability(list)), status);
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
        if (!PlayerManager.TryGetLocalPlayerAgent(out var agent) || agent?.Inventory == null) return;
        if (TeamTurretNet.Tick(agent.Pointer, enabled: true)) ResetLevel();

        var wielded = agent.Inventory.WieldedSlot;
        var wieldingOwnSentry = wielded == InventorySlot.GearClass && BotDeployAtAim.LocalSentry() != null;
        var status = Status;

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
                    : $"{Capital(gone.Name)} is no longer available - now: {Describe(entries, available, resolved)}",
                    MessageType.Center, 2.5f);
            }
            Select(entries, resolved);
        }

        if (Hotkeys.InPlay && InputMapper.GetButtonDownKeyMouse(InputAction.SelectTool, FocusStateManager.CurrentState)
            && TeamTurretPool.DecideToolKey(AbsurdWeaponsCompat.CyclingOn(), wieldingOwnSentry, _toolWieldedLastFrame) == ToolKeyAction.Cycle)
        {
            Select(entries, TeamTurretPool.Next(available, _hasSelection ? IndexOf(entries, _selected) : TeamTurretPool.None));
            ShowSelection(entries, available, status, pressed: true);
        }
        else if (wieldingOwnSentry && (!_toolWieldedLastFrame || status != _lastStatus)
                 && (status != HostPoolStatus.Live || (_hasSelection && _selected != 0)))
        {
            // Just took slot 4 out (or the host's pool came, went or turned out to be off): say what E
            // will do - the queued bot turret, that the pool is unknown and yours is held, or that the
            // host has no pool and yours places.
            ShowSelection(entries, available, status);
        }
        _toolWieldedLastFrame = wielded == InventorySlot.GearClass;
        _lastStatus = status;
    }

    private static void Select(List<Entry> entries, int index)
    {
        _hasSelection = index >= 0;
        _selected = index >= 0 ? entries[index].Bot : 0UL;
    }

    private static void ShowSelection(List<Entry> entries, bool[] available, HostPoolStatus status, bool pressed = false)
    {
        if (status == HostPoolStatus.Unknown)
        {
            ShowUnknown();
            return;
        }
        if (status == HostPoolStatus.Off)
        {
            // Once per level (and on a Tool-key press), not every time slot 4 comes out.
            if (!_offShown || pressed) MessageHelper.Show(TeamTurretMessages.HostOff, MessageType.Center, 3f);
            _offShown = true;
            return;
        }
        var index = _hasSelection ? IndexOf(entries, _selected) : TeamTurretPool.None;
        MessageHelper.Show(index < 0 ? "No turrets left to place" : $"Turret: {Describe(entries, available, index)}", MessageType.Center, 3f);
    }

    internal static void ShowUnknown() => MessageHelper.Show(TeamTurretMessages.Unknown, MessageType.Center, 2.5f);

    // "Woods's turret  [1/2] - 2 bot turrets before yours", or "your turret (last one)".
    private static string Describe(List<Entry> entries, bool[] available, int index)
    {
        if (entries[index].Bot == 0) return $"{entries[index].Name} (last one)";
        var bots = AvailableBots(available);
        var position = 0;
        for (var i = 1; i <= index; i++) if (available[i]) position++;
        return $"{entries[index].Name}  [{position}/{bots}] - {bots} bot turret{(bots == 1 ? "" : "s")} before yours";
    }

    internal static string Capital(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);

    // Places one bot's own turret at a pose. On the host: now, if HostDecide says the bot's sentry is
    // still undeployed. On a client: only a request - the host checks the team's current state and
    // places it or refuses; nothing is put down locally. True when it went down (host) or the request
    // went out (client): the press is spent and the bot is off this player's list until the host says.
    internal static bool PlaceBot(ulong bot, Pose pose, string logTag)
    {
        if (bot == 0) return false;
        bool ok;
        if (SNet.IsMaster)
        {
            var actor = SNet.LocalPlayer != null ? SNet.LocalPlayer.Lookup : 0UL;
            var result = TeamTurretNet.HostPlaceAndAnnounce(actor, bot, pose, logTag, out _);
            ok = result == TurretPlaceResult.Placed;
            if (!ok) OnRefused(bot, result);
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
    // request alike. TeamTurretPool.HostDecide is the no-duplicates gate (HostAccepts): the bot must
    // still carry an undeployed sentry at this moment, and it's marked deployed right here, before
    // anything else can ask.
    internal static TurretPlaceResult HostPlaceBot(ulong lookup, Pose pose, string logTag)
    {
        if (!SNet.IsMaster) return TurretPlaceResult.Failed;
        var found = Bots.Find(lookup, out var bot, out var backpack, out var item, out var isSentry);
        var deployed = found && backpack!.IsDeployed(InventorySlot.GearClass);
        var decision = TeamTurretPool.HostDecide(Plugin.BotsDeployOnShiftE.Value, found, isSentry, deployed, TeamTurretNet.HostCooling(lookup));
        if (decision != TurretPlaceResult.Placed)
        {
            Plugin.Logger.LogInfo($"{logTag}: refused {Label(lookup)} ({decision}) - {(!found ? "no such bot" : !isSentry ? "it isn't carrying a sentry" : deployed ? "already deployed" : "sitting out a failed placement or the pool is off")}.");
            return decision;
        }
        // HostAccepts must hold here - checked again so the gate can't be bypassed by HostDecide changing.
        if (!TeamTurretPool.HostAccepts(found, isSentry, deployed)) return TurretPlaceResult.Taken;

        try
        {
            var desc = new PlayerBotActionDeploySentryGun.Descriptor(bot!) { InstallationPose = pose };
            var action = new PlayerBotActionDeploySentryGun(desc) { m_backpackItem = item! };
            action.PlaceSentryGunOnGround();
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"{logTag}: couldn't place {Label(lookup)}. {e}");
            return TurretPlaceResult.Failed;
        }
        // Placed-but-still-owned, the bookkeeping the bot's own AI does after this call (clearing the
        // slot instead orphaned the turret and gave Z nothing to recall) - and the flag every later
        // request is checked against.
        backpack!.SetDeployed(InventorySlot.GearClass, true);
        Plugin.Logger.LogInfo($"{logTag}: placed {Label(lookup)}.");
        return TurretPlaceResult.Placed;
    }

    // Shift+E: a bot's turret, never your own - the selected bot's, else the first available - at
    // your held sentry's placement ghost.
    internal static void ShiftE()
    {
        if (PoolUnknown)
        {
            ShowUnknown();
            return;
        }
        var entries = Build();
        var pick = TeamTurretPool.BotPick(Availability(entries), _hasSelection ? IndexOf(entries, _selected) : TeamTurretPool.None);
        if (pick == TeamTurretPool.None)
        {
            Plugin.Logger.LogInfo("Shift+E: no bot turret left to place.");
            MessageHelper.Show(Status == HostPoolStatus.Off ? TeamTurretMessages.HostOff : "No bot turrets left to place", MessageType.Center, 2.5f);
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
        if (PoolUnknown)
        {
            // Bot turrets may still be queued: your own isn't dropped ahead of them.
            ShowUnknown();
            return;
        }
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

    internal static void OnRefused(ulong bot, TurretPlaceResult reason) =>
        MessageHelper.Show(TeamTurretMessages.Refused(reason, Label(bot), TeamTurretPool.FailedCooldownSeconds), MessageType.Center, 2.5f);
}

// Bots belong to the host: a client asks the host to place a bot's turret, and learns which bot
// turrets are down from the host's broadcast (sent on every change and once a second, numbered).
// G.EZ-T124: a client with no fresh broadcast treats the pool as unknown (holds its own), not empty;
// the event names carry a version so a GTFO.EZ build from before the change (different packet shapes)
// is simply "host doesn't run the pool" rather than a misread packet.
internal static class TeamTurretNet
{
    private const string StateEvent = "GTFO.EZ.TeamTurrets.State.v2";
    private const string RequestEvent = "GTFO.EZ.TeamTurrets.Request.v2";
    private const string ResultEvent = "GTFO.EZ.TeamTurrets.Result.v2";
    private const string TeamEvent = "GTFO.EZ.TeamTurrets.Event.v2";
    private const byte KindPlace = 1;
    private const byte KindRecallBots = 2;
    private const byte EventPlaced = 1;
    private const byte EventRecalled = 2;
    private const byte EventFailed = 3;
    private const byte FlagEnabled = 1;
    private const int MaxBots = 4;
    private const float HeartbeatSeconds = 1f;

    private struct PoolState
    {
        public uint Seq;
        public byte Flags;
        public byte Count;
        public byte DeployedMask;
        public byte CoolingMask;
        public ulong Bot0, Bot1, Bot2, Bot3;
    }

    private struct Request
    {
        public byte Kind;
        public byte OwnToo;
        public uint Id;
        public ulong Bot;
        public float Px, Py, Pz, Rx, Ry, Rz, Rw;
    }

    private struct Result
    {
        public ulong Requester;
        public ulong Bot;
        public uint RequestId;
        public uint StateSeq;
        public byte Reason;
    }

    private struct TeamNotice
    {
        public byte Kind;
        public byte Count;
        public byte Left;
        public byte OwnToo;
        public ulong Actor;
        public ulong Bot;
    }

    internal sealed class ViewEntry
    {
        public ulong Bot;
        public bool Deployed;
        public bool Cooling;
    }

    private sealed class RecallBatch
    {
        public ulong Actor;
        public bool OwnToo;
        public float At;
        public readonly List<ulong> Bots = new();
    }

    private static bool _registered;

    // Host side.
    private static uint _hostSeq;                 // never reset: a client compares against it across levels
    private static float _nextHeartbeat;
    private static PoolState _lastSent;
    private static readonly Dictionary<ulong, float> _failedUntil = new();
    private static readonly List<RecallBatch> _recalls = new();

    // Client side.
    private static readonly List<ViewEntry> _hostView = new();
    private static bool _haveState;
    private static bool _haveSeq;
    private static uint _lastSeq;
    private static bool _hostEnabled;
    private static float _lastStateAt = -100f;
    private static ulong _stateMaster;
    private static uint _nextRequestId;
    private static readonly Dictionary<ulong, (uint Id, float SentAt)> _pending = new();

    // Level tracking.
    private static IntPtr _levelKey = IntPtr.Zero;
    private static bool _leftLevel = true;
    private static float _levelStartAt;

    internal static IReadOnlyList<ViewEntry> HostView => _hostView;
    internal static bool Pending(ulong bot) => _pending.ContainsKey(bot);
    internal static bool HostCooling(ulong bot) => _failedUntil.TryGetValue(bot, out var until) && Time.unscaledTime < until;

    internal static HostPoolStatus Status => TeamTurretPool.ClientPoolStatus(
        _haveState, Time.unscaledTime - _lastStateAt, _hostEnabled, Time.unscaledTime - _levelStartAt);

    // Every frame for the local player. True when a new level (or a new host) started and the last
    // one's state was dropped - the caller resets its own selection then.
    internal static bool Tick(IntPtr localAgent, bool enabled)
    {
        Register();
        var reset = TrackLevel(localAgent);

        if (SNet.IsMaster)
        {
            if (enabled) VerifyRecalls();
            var state = Snapshot(enabled);
            if (Time.unscaledTime >= _nextHeartbeat || !Same(state, _lastSent)) Send(state);
            return reset;
        }

        // A client: a different host than the one the view came from means that view is meaningless.
        var master = SNet.Master;
        if (_haveState && master != null && master.Lookup != _stateMaster)
        {
            ResetClient();
            _levelStartAt = Time.unscaledTime;
            reset = true;
        }
        ExpirePending();
        return reset;
    }

    // The pool switched off on this machine: the host still says so (so clients place their own
    // instead of waiting), and still answers requests (with Disabled).
    internal static void TickDisabled()
    {
        if (!PlayerManager.TryGetLocalPlayerAgent(out var agent) || agent == null) return;
        Tick(agent.Pointer, enabled: false);
    }

    private static bool TrackLevel(IntPtr localAgent)
    {
        var inLevel = GameStateManager.CurrentStateName == eGameStateName.InLevel;
        if (!inLevel)
        {
            _leftLevel = true;
            return false;
        }
        if (!_leftLevel && localAgent == _levelKey) return false;

        _leftLevel = false;
        _levelKey = localAgent;
        _levelStartAt = Time.unscaledTime;
        ResetClient();
        _failedUntil.Clear();
        _recalls.Clear();
        _nextHeartbeat = 0f;
        return true;
    }

    private static void ResetClient()
    {
        _hostView.Clear();
        _haveState = false;
        _haveSeq = false;
        _lastSeq = 0;
        _hostEnabled = false;
        _lastStateAt = -100f;
        _stateMaster = 0;
        _pending.Clear();
    }

    private static void ExpirePending()
    {
        if (_pending.Count == 0) return;
        List<ulong>? expired = null;
        foreach (var kv in _pending)
            if (TeamTurretPool.RequestTimedOut(Time.unscaledTime - kv.Value.SentAt)) (expired ??= new()).Add(kv.Key);
        if (expired == null) return;
        foreach (var bot in expired)
        {
            _pending.Remove(bot);
            Plugin.Logger.LogInfo($"Team turrets: no answer from the host about {TeamTurrets.Label(bot)}.");
            MessageHelper.Show($"No answer from the host about {TeamTurrets.Label(bot)}", MessageType.Center, 2.5f);
        }
    }

    internal static void BroadcastNow()
    {
        if (SNet.IsMaster) Send(Snapshot(Plugin.BotsDeployOnShiftE.Value));
    }

    private static uint Send(PoolState state)
    {
        state.Seq = ++_hostSeq;
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
        return state.Seq;
    }

    private static PoolState Snapshot(bool enabled)
    {
        var state = new PoolState { Flags = enabled ? FlagEnabled : (byte)0 };
        if (!enabled) return state;
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
            if (HostCooling(b.Lookup)) state.CoolingMask |= (byte)(1 << state.Count);
            state.Count++;
        }
        return state;
    }

    // Seq left out: it's new on every send.
    private static bool Same(PoolState a, PoolState b) =>
        a.Flags == b.Flags && a.Count == b.Count && a.DeployedMask == b.DeployedMask && a.CoolingMask == b.CoolingMask
        && a.Bot0 == b.Bot0 && a.Bot1 == b.Bot1 && a.Bot2 == b.Bot2 && a.Bot3 == b.Bot3;

    // Bot turrets the whole team can still place, as the host sees them now.
    private static int HostBotsLeft()
    {
        var n = 0;
        foreach (var b in Bots.Sentries()) if (!b.Deployed && !HostCooling(b.Lookup)) n++;
        return n;
    }

    // On the host: one bot placement for anyone (the host's own press, or a client's request), then
    // the new state to everyone and the line every event log shows. Returns the reason for the requester.
    // The new state goes out before the announcement and before the requester's answer, which names
    // its sequence number (stateSeq) so the requester can drop anything older that arrives late.
    internal static TurretPlaceResult HostPlaceAndAnnounce(ulong actor, ulong bot, Pose pose, string logTag, out uint stateSeq)
    {
        var result = TeamTurrets.HostPlaceBot(bot, pose, logTag);
        if (result == TurretPlaceResult.Failed) _failedUntil[bot] = Time.unscaledTime + TeamTurretPool.FailedCooldownSeconds;
        stateSeq = Send(Snapshot(Plugin.BotsDeployOnShiftE.Value));
        if (result == TurretPlaceResult.Failed)
            Announce(new TeamNotice { Kind = EventFailed, Actor = actor, Bot = bot, Left = (byte)HostBotsLeft() });
        else if (result == TurretPlaceResult.Placed)
            Announce(new TeamNotice { Kind = EventPlaced, Actor = actor, Bot = bot, Left = (byte)HostBotsLeft() });
        return result;
    }

    // On the host: Z, the host's own or a client's. The bots' turrets go back now; whether each really
    // came back into the pool is checked RecallVerifySeconds later (VerifyRecalls), and only then is it
    // announced, so the count everyone sees is the real one.
    internal static void HostRecall(ulong actor, bool ownToo)
    {
        if (!SNet.IsMaster) return;
        var batch = new RecallBatch { Actor = actor, OwnToo = ownToo, At = Time.unscaledTime };
        batch.Bots.AddRange(PickupAllBotTools.Activate());
        BroadcastNow();
        if (batch.Bots.Count > 0 || ownToo) _recalls.Add(batch);
    }

    private static void VerifyRecalls()
    {
        if (_recalls.Count == 0) return;
        var now = Time.unscaledTime;
        for (var i = _recalls.Count - 1; i >= 0; i--)
        {
            var batch = _recalls[i];
            if (now - batch.At < TeamTurretPool.RecallVerifySeconds) continue;
            _recalls.RemoveAt(i);

            var back = 0;
            foreach (var lookup in batch.Bots)
                if (PickupAllBotTools.EnsureBackInPool(lookup, now - batch.At)) back++;
            BroadcastNow();
            Announce(new TeamNotice { Kind = EventRecalled, Actor = batch.Actor, Count = (byte)back, OwnToo = (byte)(batch.OwnToo ? 1 : 0), Left = (byte)HostBotsLeft() });
        }
    }

    private static void Announce(TeamNotice notice)
    {
        try
        {
            NetworkAPI.InvokeEvent(TeamEvent, notice);
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"Team turrets: couldn't tell the team. {e}");
        }
        ShowNotice(notice);
    }

    private static void ShowNotice(TeamNotice n)
    {
        var you = SNet.LocalPlayer != null && n.Actor == SNet.LocalPlayer.Lookup;
        var actor = you ? "You" : Bots.PlayerName(n.Actor);
        var text = n.Kind switch
        {
            EventPlaced => TeamTurretMessages.Placed(actor, TeamTurrets.Label(n.Bot), n.Left),
            EventRecalled => TeamTurretMessages.Recalled(actor, n.Count, n.OwnToo != 0, n.Left, you),
            EventFailed => TeamTurrets.Capital(TeamTurretMessages.Failed(TeamTurrets.Label(n.Bot), TeamTurretPool.FailedCooldownSeconds)),
            _ => null,
        };
        if (text != null) MessageHelper.Show(text, MessageType.EventLog);
    }

    internal static bool RequestPlace(ulong bot, Pose pose)
    {
        var master = SNet.Master;
        if (master == null) return false;
        Register();
        var id = ++_nextRequestId;
        try
        {
            NetworkAPI.InvokeEvent(RequestEvent, new Request
            {
                Kind = KindPlace, Id = id, Bot = bot,
                Px = pose.position.x, Py = pose.position.y, Pz = pose.position.z,
                Rx = pose.rotation.x, Ry = pose.rotation.y, Rz = pose.rotation.z, Rw = pose.rotation.w,
            }, master);
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"Team turrets: couldn't ask the host to place {TeamTurrets.Label(bot)}. {e}");
            return false;
        }
        _pending[bot] = (id, Time.unscaledTime);
        return true;
    }

    // A client's Z: the host recalls the bots (they're its) and tells everyone; true when it was asked.
    internal static bool RequestRecallBots(bool ownToo)
    {
        var master = SNet.Master;
        if (master == null || Status != HostPoolStatus.Live) return false;
        Register();
        try
        {
            NetworkAPI.InvokeEvent(RequestEvent, new Request { Kind = KindRecallBots, OwnToo = (byte)(ownToo ? 1 : 0), Id = ++_nextRequestId }, master);
            return true;
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"Team turrets: couldn't ask the host to recall the bots' turrets. {e}");
            return false;
        }
    }

    private static void Register()
    {
        if (_registered) return;
        _registered = true;
        if (!NetworkAPI.IsEventRegistered(StateEvent)) RemoteEvents.Register<PoolState>(StateEvent, OnState);
        if (!NetworkAPI.IsEventRegistered(RequestEvent)) RemoteEvents.Register<Request>(RequestEvent, OnRequest);
        if (!NetworkAPI.IsEventRegistered(ResultEvent)) RemoteEvents.Register<Result>(ResultEvent, OnResult);
        if (!NetworkAPI.IsEventRegistered(TeamEvent)) RemoteEvents.Register<TeamNotice>(TeamEvent, OnNotice);
    }

    private static bool FromMaster(ulong sender)
    {
        if (SNet.IsMaster) return false;
        var master = SNet.Master;
        return master != null && master.Lookup == sender;
    }

    // On a client: the host's bot turrets, if this state is newer than the last one taken.
    private static void OnState(ulong sender, PoolState state)
    {
        if (!FromMaster(sender)) return;
        if (_haveState && sender != _stateMaster) ResetClient();
        if (!TeamTurretPool.IsNewerState(_haveSeq, _lastSeq, state.Seq)) return;

        _haveSeq = true;
        _lastSeq = state.Seq;
        _haveState = true;
        _stateMaster = sender;
        _lastStateAt = Time.unscaledTime;
        _hostEnabled = (state.Flags & FlagEnabled) != 0;

        _hostView.Clear();
        for (var i = 0; i < Math.Min((int)state.Count, MaxBots); i++)
        {
            var bot = i switch { 0 => state.Bot0, 1 => state.Bot1, 2 => state.Bot2, _ => state.Bot3 };
            var deployed = (state.DeployedMask & (1 << i)) != 0;
            _hostView.Add(new ViewEntry { Bot = bot, Deployed = deployed, Cooling = (state.CoolingMask & (1 << i)) != 0 });
            if (deployed) _pending.Remove(bot);
        }
    }

    // On the host: a client wants a bot's turret placed (or every bot's recalled).
    private static void OnRequest(ulong sender, Request request)
    {
        if (!SNet.IsMaster) return;
        if (!Plugin.BotsDeployOnShiftE.Value)
        {
            if (request.Kind == KindPlace) Reply(sender, request, TurretPlaceResult.Disabled, _hostSeq);
            return;
        }

        if (request.Kind == KindRecallBots)
        {
            HostRecall(sender, request.OwnToo != 0);
            return;
        }
        if (request.Kind != KindPlace) return;

        var position = new Vector3(request.Px, request.Py, request.Pz);
        var rotation = new Quaternion(request.Rx, request.Ry, request.Rz, request.Rw);
        var tag = $"Request from {Bots.PlayerName(sender)}";
        TurretPlaceResult result;
        uint seq;
        if (Finite(position) && Finite(rotation))
        {
            result = HostPlaceAndAnnounce(sender, request.Bot, new Pose(position, Normalized(rotation)), tag, out seq);
        }
        else
        {
            // A garbled spot: Failed like any other, so the requester's message (sits out) is true.
            Plugin.Logger.LogInfo($"{tag}: refused {TeamTurrets.Label(request.Bot)} - not a valid spot.");
            result = TurretPlaceResult.Failed;
            _failedUntil[request.Bot] = Time.unscaledTime + TeamTurretPool.FailedCooldownSeconds;
            seq = Send(Snapshot(true));
            Announce(new TeamNotice { Kind = EventFailed, Actor = sender, Bot = request.Bot, Left = (byte)HostBotsLeft() });
        }
        Reply(sender, request, result, seq);
    }

    private static void Reply(ulong requester, Request request, TurretPlaceResult reason, uint stateSeq)
    {
        try
        {
            NetworkAPI.InvokeEvent(ResultEvent, new Result { Requester = requester, Bot = request.Bot, RequestId = request.Id, StateSeq = stateSeq, Reason = (byte)reason });
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"Team turrets: couldn't answer a placement request. {e}");
        }
    }

    // On a client: the host's answer to our own request.
    private static void OnResult(ulong sender, Result result)
    {
        if (!FromMaster(sender)) return;
        if (SNet.LocalPlayer == null || result.Requester != SNet.LocalPlayer.Lookup) return;

        // Only this request's answer settles its pending entry - not an older request's, not a timer.
        if (_pending.TryGetValue(result.Bot, out var p) && p.Id == result.RequestId) _pending.Remove(result.Bot);

        // States older than the one that reflects this answer are dropped from now on.
        var floor = result.StateSeq - 1;
        if (TeamTurretPool.IsNewerState(_haveSeq, _lastSeq, floor))
        {
            _haveSeq = true;
            _lastSeq = floor;
        }

        var reason = (TurretPlaceResult)result.Reason;
        if (reason == TurretPlaceResult.Placed || reason == TurretPlaceResult.Taken)
        {
            // Down either way: mark it now rather than wait for the state packet.
            foreach (var v in _hostView) if (v.Bot == result.Bot) v.Deployed = true;
        }
        else if (reason == TurretPlaceResult.Failed)
        {
            foreach (var v in _hostView) if (v.Bot == result.Bot) v.Cooling = true;
        }
        else if (reason == TurretPlaceResult.Disabled)
        {
            _hostEnabled = false;
        }
        // A Failed is in everyone's event log too; the requester also gets the reason centre-screen.
        if (reason != TurretPlaceResult.Placed) TeamTurrets.OnRefused(result.Bot, reason);
    }

    // On a client: a placement, failure or recall by anyone, from the host.
    private static void OnNotice(ulong sender, TeamNotice notice)
    {
        if (!FromMaster(sender)) return;
        ShowNotice(notice);
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
