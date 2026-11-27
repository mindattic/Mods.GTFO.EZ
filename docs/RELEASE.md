---
layer: release
status: living
---
# GTFO.EZ — Release charter

> The one finish line this repo is working toward. Every open ticket in this repo sits under one of the epics
> below, and every epic is part of the milestone. Work that does not fit is out of scope until this milestone is
> reached (owner, 2026-11-27, MAT-T319).

## 1. What this is
A BepInEx plugin that makes GTFO easier for a co-op group, with every tunable in the config.

## 2. The milestone: GTFO.EZ v22 team-sync release {#milestone}
- **Milestone:** MS-52, "Mods.GTFO.EZ v22 team-sync release"
- **Published means:** GtfoEZ v22 on Thunderstore (https://thunderstore.io/c/gtfo/p/MindAttic/GtfoEZ/), with
  the `MindAttic-GtfoEZAbsurdItems` dependency pinned to the same release. The four family mods ship together.
- **The natural stopping point:** the host-authoritative push (G.EZ-T122) is complete: every setting that
  touches other players follows the host, and turret counts are accurate for everyone. Once that is live, the
  mod does what its players asked for.

## 3. Done when (acceptance checks)
1. build — `src/GTFO.EZ/GTFO.EZ.csproj` builds clean with `build.ps1` on main.
2. exists — the Thunderstore listing MindAttic/GtfoEZ shows v22.
3. signoff — the owner plays a co-op drop with a client: level, toggles and turret counts match for every player.

## 4. Epics, in priority order
| Priority | Epic (ticket) | What it delivers | Features |
|---|---|---|---|
| 900 | G.EZ-T148 Host-authoritative team play | Turret counts accurate for every player | G.EZ-T149 Accurate turret counts (990): G.EZ-T140 |
| 800 | G.EZ-T150 v22 Thunderstore release | README current and the lockstep v22 publish | G.EZ-T151 Release docs and publish (890): G.EZ-T135, G.EZ-T143 |

G.EZ-T143 (the release) requires G.EZ-T140, G.EZ-T135 and GES-T20 (all four mods pinned to Shared HEAD).

## 5. Out of scope (not before this milestone)
- New tunables or game systems; this mod tunes the game, content lives in the Absurd companions.
- Multi-sentry carry (G.EZ-T111): replaced by the shared team turret pool.
- Syncing purely personal settings (movement, fall damage, lighting) beyond announcing them.
- Bringing GTFO.SkipDrop or GTFO.CrashTestDummies into the lockstep `/publish`.
- Open-ended bug hunts; a real bug is filed as its own ticket.

## 6. Current verified state
- GtfoEZ 21.0.0 is live on Thunderstore (`.publish-state.json`, Thunderstore API on 2026-11-27).
- Level settings follow the host (377614a, G.EZ-T125); toggles and refills (8c3eeed, T138); game-data buffs
  (3b4c890, T144); companion settings (36ebe6c, T145). The audit is `docs/LOCAL_ONLY_AUDIT.md` (993417f).
- AbsurdItems is a pinned Thunderstore dependency with a golden rule in AGENTS.md (387dfba).

## 7. Decision record
- 2026-11-27 — charter drafted from README/AGENTS/LOCAL_ONLY_AUDIT/git log/tickets; Legion vote: Approve,
  plurality quorum reached, 7 of 9 voters answered, consensus 1.0, no dissent.
- Triage (G.EZ-T147): 11 open → done 3 (T112, T113, T122), duplicate 0, wont-do 5 (T110, T111, T114, T136,
  T142), kept 3 (T135, T140, T143), new tasks 0. G.EZ-T136 was split into per-repo README tasks (G.AWPN-T247,
  GAI-T48, GAM-T75, GES-T21).
