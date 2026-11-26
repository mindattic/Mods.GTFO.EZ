# Local-only effects audit (G.EZ-T137)

Scope: GTFO.EZ (`src/`), GTFO.EZ.AbsurdItems, GTFO.EZ.AbsurdMoves, GTFO.EZ.AbsurdWeapons and
GTFO.EZ.Shared, read as of 2026-11-26 (after G.EZ-T138 commit 8c3eeed and G.EZ-T140 commit 212ca34
landed). A "local-only effect" is one that depends on one machine's own `.cfg` or datablocks, so a
teammate with a different config (or without the mod) gets a different result and nobody is told.

How GTFO decides where things run, which sorts every finding below: damage to a human player is
applied on that player's own game; enemies, bots and doors are simulated on the host; a gun's hit is
resolved on the shooter's game; the level is built by every machine from its own GameData.

Already sound (no action): infinite health/ammo toggles (`Shared/InfiniteToggles.cs`, host holds
state, broadcast, announced); level generation settings (`Expedition *`, `Artifact Spawns`, `Loot`
sections follow the host through `LevelSettingsSync`, mismatches reported by fingerprint, AbsurdItems
level settings pushed through `HostLevelSettings`); Ping Anywhere (team event); team turret pool
(host-authoritative, counts broadcast); AbsurdWeapons projectile/beam/lightning/hammer visuals
(versioned events, attacker-side damage, `NetPresence` warning when the host lacks the mod);
AbsurdMoves Bullet Time (trigger carries duration and scale) and the Moves on/off notice;
AbsurdItems halo grenades (throw/stick/detonate events) and artifact pickup tally.

## A. GTFO.EZ settings that stay per-machine though other players are affected

| # | Setting (section) | Where applied | Who is affected | Problem |
|---|---|---|---|---|
| A1 | Enemy Health / Melee Damage / Tentacle Damage Multiplier (`Enemy Balance`) | `GameDataTweaks.ApplyEnemyBalanceTweaks` | Enemies run on the host | Only the host's value matters for kills and damage taken; a client who sets it gets nothing and the fingerprint does not flag the difference. Section is not in `LevelSettings.Sections`. |
| A2 | Movement Detection Distance Multiplier (`Enemy Detection`) | `ApplyDetectionTweaks` | Enemy detection runs on the host | Same as A1: host's value only, silent for clients. |
| A3 | Max Health, Health Regen Rate/Cap (`Health`) | `ApplyPlayerTweaks` | Your own player; the host's bots use the host's datablock | Each human gets their own value (intended), but the bots and the "team max health" vary by whose game it is; nobody is told teammates differ. |
| A4 | Friendly Fire Multiplier (`Combat`) | `ApplyPlayerTweaks` | Damage lands on the victim's game, so the victim's value applies | Your setting does nothing to teammates who left it at 1; the shooter cannot know. |
| A5 | Fall Damage, No-Air Damage/Depletion, Stamina, Walk/Run/Air/Crouch/Ladder speed, Jump height, Flashlight angle/intensity, Battery capacity/drain (`Fall Damage`, `Environment`, `Stamina`, `Movement`, `Lighting`, `Utility`) | `ApplyPlayerTweaks` | Own player only | Purely personal by design; not announced. Listed so T138 can decide per setting (follow host or stay personal). |
| A6 | Weapon Ammo Multiplier (`Ammo`) | `ApplyPlayerTweaks` | Own backpack | Personal; but starting ammo differs per player with no notice (the original "only gave me" complaint). |
| A7 | Per-weapon, per-type, per-melee and sentry multipliers (`Weapon *`, `Melee *`, `Sentries`) | `ApplyWeaponTweaks` | The shooter's gun; sentries run on their owner's game, bot sentries on the host | Shooter's value wins; a client's sentry and a host's bot sentry can differ with no notice. |
| A8 | Disable Fog (`Environment`), Fog Clear Pulse settings | `ApplyFogTweaks`, `FogClearPulse` | Visuals on this machine | Fog is rendered per machine, so a mixed table sees different fog. Cosmetic; list only. |
| A9 | Revive Duration, Sentry Deploy/Pickup Duration, Sentries Pass Through, Full Ammo On Drop, Ping Anywhere, Bots Deploy On Shift+E | per-machine patches | Interaction timing, bot tools | Changed by G.EZ-T138 (8c3eeed) to take the host's value in someone else's lobby; confirm that is complete (see D). |
| A10 | Bots Always Equip Sentry Gun (`Bots`) | `BotSentryEquipPatch` | Bots are the host's | Only the host's value acts; a client's setting is silently ignored. |
| A11 | Control Defaults Applied, Ping/hotkey bindings | local | Own controls | Personal by design. No action. |

## B. GTFO.EZ.AbsurdItems

| # | Item | Problem |
|---|---|---|
| B1 | Stim Pack heal %, max overheal %, drain rate, damage boost % and seconds (`StimPack.cs`) | Values come from the user's own `.cfg`; heal goes through the synced `AddHealth`, the overheal and drain are the user's own game. Teammates see none of it and the damage boost is the shooter's value. Personal, but not announced. |
| B2 | Artifact buffs (`ArtifactBuffs.cs`): Bold max health, Aggressive damage %, Muted detection % | Counts are shared (every machine sees every pickup), but the percentages are each machine's `.cfg`: the damage % is the shooter's, the detection % is the host's. A mixed-config table gets different buffs than the tally says. |
| B3 | Frag and Plasma grenade damage, radius, fuse, plasma panic (`Grenades.cs`, `GrenadePanic.cs`) | Blast damage is the thrower's value (sent over events for the visuals); Plasma panic is the host's `PlasmaPanicEnabled` and fuse values. A host with the setting off silently gives clients no panic. No `NetPresence` style warning exists in this mod. |
| B4 | Breadcrumbs, Artifact Glow | Cosmetic and per machine by design; teammates without the mod see none. List only. |
| B5 | Injected item datablocks (Frag 1203, Stim, Plasma 1205) | Present only when the mod is on. The fingerprint mask covers presence; a teammate without the mod holding the id is the T139 case. |

## C. GTFO.EZ.AbsurdMoves

| # | Move | Problem |
|---|---|---|
| C1 | Ground Pound (`GroundPound.cs`) | The shove and hit run in `Land` only when `SNet.IsMaster`, with the host's Radius, Damage, Stagger and PushDistance, not the pounder's. If the host lacks the mod there is no damage and no warning (AbsurdWeapons has `NetPresence`, this mod has nothing like it). |
| C2 | Dodge, Slide, Mantle, Double Jump | Move through the game's own controller so they sync as movement; speeds and distances are the mover's config. No notice to teammates. Personal. |
| C3 | Fire Mid-Air, Fire While Running | Gate only the local player's own trigger. Personal, nothing to sync. |
| C4 | Moves toggle (Home) | Per player by design, announced to the team. No action. |
| C5 | Fall-damage immunity during a pound | `DisableFallDamageTemporarily` is set on the pounder's own game only, which is right, since fall damage is applied there. No action. |

## D. GTFO.EZ.AbsurdWeapons and Shared

| # | Item | Problem |
|---|---|---|
| D1 | Weapon numbers (`WeaponSettings`: damage, reach, stagger, blade length, tempo) | The attacker's `.cfg` decides damage; a teammate's copy of the same gun hits differently. Blade length is a visual scale applied per machine, so the same sword has different lengths on different screens. |
| D2 | Knockback, Shadow Dagger stealth, Shrink Beam slowdown | Host-side effects carried out with the host's config; the client warns once (`NetPresence.WarnIfHostLacksMod`) when the host lacks the mod, but not when the host has different values. |
| D3 | `Ammo/MagazineSeconds`, `Tentacle` pool size (`TentaclePool`) | Per machine. Tentacle pool is a render limit: when full, tentacles do not draw, so a machine with a smaller pool sees fewer tentacles. Cosmetic. |
| D4 | Hive Locks markers | Local only by design (documented). No action. |
| D5 | Victory Banner (`VictoryBanner.cs`) | Host-driven at `SNet.IsMaster`; clients without the mod see nothing. Cosmetic. |
| D6 | Gear Cycler toggle | Event `AbsurdToggle.v2` announces the toggle; each player's cycle is their own. No action. |
| D7 | Shared `KitLoot` | Runs only inside AbsurdItems and reads `HostLevelSettings`; no direct finding, covered by B5. |

## Recommendations by follow-up ticket

- G.EZ-T138 (host-authoritative GTFO.EZ): decide A1, A2, A10 first (host-only effects, so make a
  client's value follow the host and fingerprint them), then A3 and A4 (follow host), then announce
  A5 to A7 differences in the event log instead of syncing them.
- G.EZ-T139 (Absurd mods team sync): C1 (pounder's values on the wire, `NetPresence` warning), B2 and
  B3 (carry the shooter's or thrower's numbers in the event, as AbsurdWeapons' `.v2` events do), D1 and
  D2 (send the shooter's numbers, warn on a differing host).
- G.EZ-T140 (turret counts): no new gaps found beyond what 212ca34 covers; the pool is
  host-authoritative and broadcast. A7's sentry multipliers are the only turret-related per-machine
  setting.

## Resolved by G.EZ-T144

A3 (health, regen), A5 (fall damage, stamina, movement, battery), A7 (weapon, melee and sentry
multipliers) and A8's Disable Fog now follow the host in someone else's lobby: their sections are in
`LevelSettings`, the player, weapon, melee and fog datablocks are snapshotted (`DataBlockSnapshot`)
and re-applied with the host's values before the level builds. Ammo, Friendly Fire, No-Air,
Flashlight, Enemy Balance and Detection stay per machine.
