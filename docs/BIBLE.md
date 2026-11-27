---
layer: bible
status: living
---
# GTFO.EZ — Project Bible

> Single source of truth for what GTFO.EZ IS, is NOT, and the rules that keep it coherent.
> README says how to build and run it; this says how to think about it. The finish line lives in
> `docs/RELEASE.md`.

## 1. The one sentence {#GEZ-§1}
A BepInEx IL2CPP plugin for GTFO that tunes health, regen, stamina, fall damage, ammo and enemy
detection for an easier co-op run, with 530+ further config values underneath.

## 2. The product promise {#GEZ-§2}
- A fresh group drops into GTFO's own rundowns on easier, community-consensus defaults with no
  config work.
- Every tunable value is in the `.cfg` BepInEx writes; nothing is hidden or hardcoded.
- Upgrades never silently undo a player's choices: a changed default gets a one-time, per-setting
  migration.
- In a co-op lobby every player plays one set of rules: level, loot, toggle and game-data settings
  follow the host, and team-wide effects (Insert/Delete, Ping Anywhere, turret pool) are announced
  to everyone.

## 3. What it is NOT {#GEZ-§3}
- Not a content mod: new weapons, items and moves live in the companions GTFO.EZ.AbsurdWeapons,
  GTFO.EZ.AbsurdItems and GTFO.EZ.AbsurdMoves.
- Not a replacement-datablock pack: it patches fields on the game's loaded datablocks at load time
  and ships no JSON.
- Not a standalone release train for GTFO.SkipDrop or GTFO.CrashTestDummies.

## 4. Architecture canon {#GEZ-§4}
```
 BepInEx (IL2CPP) ──> Plugin.cs (config, migrations, Harmony)
                        │
     ┌──────────────────┼────────────────────────────┐
  Patches/GameDataTweaks  Patches/LevelSettingsSync   Patches/TeamTurrets, DeployPickupHotkeys
  (datablock multipliers) (host values over GTFO-API) (shared team turret pool, Shift+E/X/Z)
                        │
  GTFO.EZ.Core (pure, tested: BalanceMath, DecisionRules, LevelSettingsSync, ArtifactOdds)
                        │
  external/GTFO.EZ.Shared (submodule: SharedOwner, InfiniteToggles, MessageHelper, RemoteEvents)
```
### 4.1 Projects
- `src/GTFO.EZ` — the plugin (Plugin.cs, `Patches/*`, `Pure/GearJson.cs`).
- `src/GTFO.EZ.Core` — game-free logic, covered by `tests/GTFO.EZ.Core.Tests`.
- `external/GTFO.EZ.Shared` — shared family source, compiled in.
- `package/` — Thunderstore manifest, README, CHANGELOG, icon.
### 4.2 Domain nouns
Datablock (player, weapon, melee, sentry, fog, expedition balance), level settings, team turret
pool, bot, host, client, event log.
### 4.3 Key verbs
Apply tweaks at GameDataInit; snapshot and re-apply with the host's values before the level builds;
broadcast pool state from the host; fingerprint level settings on join and name differences.

## 5. The Laws {#GEZ-§5}
Inherits `MindAttic.HouseRules.md` by reference.
1. {#GEZ-LAW-1} Patch the game's own loaded data; never ship or edit data files.
2. {#GEZ-LAW-2} Anything that affects other players follows the host and is announced to the team.
3. {#GEZ-LAW-3} GTFO.EZ.AbsurdItems compatibility (AGENTS.md golden rule): explicit
   `HarmonyBefore`/`HarmonyAfter` by GUID on shared patch targets, no colliding config keys, and
   the `MindAttic-GtfoEZAbsurdItems-X.Y.Z` dependency pin tracks what is published.
4. {#GEZ-LAW-4} The four GTFO.EZ mods release in lockstep (same vN) through the family `/publish`.
5. {#GEZ-LAW-5} Shared code changes in GTFO.EZ.Shared first, then the submodule moves.
6. {#GEZ-LAW-6} A default change ships with a one-time config migration.

## 6. Verified state {#GEZ-§6}
- GtfoEZ 21.0.0 is live on Thunderstore (`.publish-state.json`, Thunderstore API 2026-11-27).
- Host-following level settings (G.EZ-T125), toggles (T138), game-data buffs (T144) and companion
  settings (T145) are landed; the local-only audit is `docs/LOCAL_ONLY_AUDIT.md`.

## 7. Active frontier {#GEZ-§7}
See `docs/RELEASE.md`: the v22 team-sync release.

## 8. Quality bar {#GEZ-§8}
A feature is done when `build.ps1` builds clean, the setting is in the `.cfg` with a sane default,
`package/README.md` and `package/CHANGELOG.md` describe it, and it behaves the same for host and
client in a co-op lobby.

## 9. Glossary {#GEZ-§9}
- **Family** — GTFO.EZ plus its three Absurd companions, versioned together.
- **Host-authoritative** — a client uses the host's value in the host's lobby, without writing
  its own `.cfg`.
- **Team turret pool** — every bot's undeployed sentry, placeable by any player through the host.
