# GTFO.EZ

A BepInEx IL2CPP plugin for GTFO that tunes health, regen, stamina, fall damage, ammo, and enemy
detection for an easier co-op run, with 530+ further config values exposed underneath for players
who want to go deeper.

![Language](https://img.shields.io/badge/language-C%23%20(.NET%206)-blue)
![Platform](https://img.shields.io/badge/platform-BepInEx%20IL2CPP-informational)
![Status](https://img.shields.io/badge/status-active%20(v15)-success)

Try it: install from [Thunderstore](https://thunderstore.io/c/gtfo/p/MindAttic/GtfoEZ/) (listed
there as `GtfoEZ`, since package names can't contain a period).

## Why

- Play GTFO's existing rundowns without a full difficulty rewrite - this patches fields on the
  game's own loaded datablocks at load time instead of shipping replacement JSON, so it keeps
  working across patches that only rebalance base values.
- Defaults are tuned against other long-running GTFO easy-mode mods' settled-on numbers, not one
  person's guess, so a fresh group can drop in without an hour of config tuning first.
- Nothing is hidden: every tunable value, curated or not, lands in the config file BepInEx writes
  on first launch, so a group can push further (or less) without anyone recompiling.
- Upgrades don't silently undo a player's own choices: BepInEx normally freezes whatever's already
  in a `.cfg`, so this mod runs a one-time, per-setting migration whenever a *default* changes,
  instead of leaving upgraders stuck on a stale number forever.

## Features

**Core tuning** (community-consensus defaults; see `package/README.md` for the full
vanilla-vs-default table):

- Health, regen rate and regen cap, stamina (removes the in-combat penalty, plus a `Stamina
  Multiplier` that goes further still), fall damage amount and max-damage height, weapon/tool ammo,
  and enemy movement-detection range.
- Consumable loot: glow sticks, lock melters, C-foam, trip mines and fog repellers are disabled by
  default, so their share goes to the rest. Consumables as kits moved to
  [GTFO.EZ.AbsurdItems](https://github.com/mindattic/GTFO.EZ.AbsurdItems): with its *Consumables Are
  Kits* on (its Loot Mix adds Hand Grenades and Stim Packs to the kits), that mod decides the
  consumable loot, replacing this mod's per-item spawn odds; if its kits don't fill the tables,
  those odds still apply.
- Fog: the fog's own enabled flag is switched off, and since fog still showed in play, a silent
  fog-clearing sphere (the Fog Repeller's, minus its light, sound and time limit) follows you,
  widening from 4 m to 25 m over 90 s each level. Tunable under `Fog Clearing`.
- Revive duration is cut roughly two-thirds.
- Sentry guns place and pick back up instantly - the vanilla hold-to-confirm charge is removed by
  default. Placing your own switches you straight back to the weapon you had out before.
- **Bots Always Equip Sentry Gun** (`Bots`, off by default): forces every bot's tool slot to the
  basic Sentry Gun.
- On first launch only, switches on four of the game's own control options (toggle comms menu,
  toggle sprint, toggle aim, stand on jump) - a one-time nudge, not an enforced setting.
- **Insert** toggles infinite health and **Delete** infinite ammo (your own player only), each
  announced in the event log and always in the console too; both start off. Every GTFO.* mod has
  these two keys, from one shared copy of the code (see Shared code below), and with several
  installed only one of them answers the keys, so a press never toggles twice.
- A load confirmation: under the rundown title on the rundown-select screen, one "<mod> vN Active"
  line per GTFO.* mod that's installed and switched on, and the same list as a warden-intel message
  in-level once you spawn.

**Everything else** - 530+ further config entries covering flashlights, movement speed, friendly
fire, battery drain, no-air damage, per-enemy health/damage, the full per-difficulty-tier
`ExpeditionBalanceDataBlock` (loot budgets, enemy caps, level generation, doors), every melee
weapon, and every ranged weapon archetype plus sentries - all left at vanilla by default. See
`package/README.md` for the complete breakdown, including real in-game weapon names.

**Experimental** (`Experimental` config section; see Limitations below):

- **Bot tool controls** (on by default): aim your own sentry's placement ghost and press
  **Shift+E** to drop the next bot's equipped tool there instead of yours; **Z**
  instantly recalls every bot-placed tool back into its bot; **X** places your own sentry without
  pulling it out. Shift+E and Z only work for the host (bots belong to the host); on a client,
  Shift+E does nothing and never places your own sentry.
- **Ping Anywhere** (on by default): `PING <KEY>` from any terminal reaches an item anywhere in
  the level, not just the terminal's own zone. Same-zone pings are left to the game's own PING.
  Technique credit: [BetterTerminal](https://github.com/AlexanderMakunin/BetterTerminal) by
  AlexanderMakunin (MIT).

| Input | Action |
|---|---|
| E (aiming your sentry) | Place your own sentry - instant, then back to your previous weapon |
| Shift+E (aiming your sentry) | Place the next bot's tool at your aim instead (host only) |
| Insert | Infinite health on/off |
| Delete | Infinite ammo on/off |
| Z | Recall every bot-placed tool at once (host only) |
| X | Place your own sentry without pulling it out (at your aim if holding it, else just ahead of you) |

## Quick start

Requires the [.NET 6 SDK](https://dotnet.microsoft.com/) and `BepInExPack_GTFO` already installed
and launched at least once ("Modded" start) in a Thunderstore Mod Manager or r2modman profile.

```powershell
git clone --recurse-submodules https://github.com/mindattic/GTFO.EZ.git
cd GTFO.EZ
.\build.ps1
```

This builds against the Thunderstore Mod Manager profile named "Easy Mode DEV" by default
(`.\build.ps1 -Profile MyProfile` to use another one), places the plugin straight into that
profile's `BepInEx/plugins/GTFO.EZ/` folder so you can launch the game and test immediately, and
also produces `dist/GTFO.EZ-<version>.zip` for sharing. A successful load lists "GTFO.EZ vN
Active" (plus a line per other active GTFO.* mod) on the rundown-select screen and again as a warden-intel message in-level.

If you're on the older r2modman app instead, its profiles live under a different path - point the
build at it directly:

```powershell
dotnet build .\src\GTFO.EZ\GTFO.EZ.csproj -p:BepInEx="$env:AppData\r2modmanPlus-local\GTFO\profiles\Default\BepInEx"
```

## How it works

```text
GameDataInit.Initialize (game boot)
        |
        v
  GameDataTweaks postfix patch
        |
        +--> ApplyPlayerTweaks (health, stamina, fall damage, ammo, movement...) /
        |    ApplyFlashlightTweaks / ApplyDetectionTweaks / ApplyLootTweaks / ...
        |      (each of the 9 tweak categories runs in its own try/catch -
        |       one broken category logs and skips, the rest still apply)
        v
  multiplies fields directly on the game's own loaded datablocks
  (PlayerDataBlock, FogSettingsDataBlock, ExpeditionBalanceDataBlock, ...)
```

No datablock JSON is shipped or copied. Everything is a multiplier (or toggle) applied in memory
once, at load time, read from the BepInEx config. Additional Harmony patches cover behaviour that
isn't a simple datablock field: one-time control
defaults (`ControlDefaultsPatch`), revive speed, bot sentry equipping, instant sentry placement
(`SentryInstantPlacementPatch`), sentries passing through each other and bullets
(`SentryPassThrough`: a live sentry's solid colliders move to a layer no bullet or sentry ray
includes, picked at runtime from `LayerManager`'s masks, and live sentries ignore each other's
colliders), the experimental bot tool controls
(`DeployPickupHotkeys`), Ping Anywhere (`TerminalPingPatch`), and fog clearing (`FogClearPulse`).

## Configuration

All tweaks are multipliers or toggles, tunable after first launch in
`BepInEx/config/MindAttic.GTFO.EZ.cfg` - no rebuild needed to retune a value. The first entry,
`[0. Mod] Enabled`, is the master switch: `false` and the mod returns from `Load` before binding or
patching anything else (the other GTFO.* mods then treat it as not installed). Config sections
mirror the feature groups above: Health, Stamina, Fall Damage, Ammo, Lighting, Enemy Detection,
Movement, Combat, Utility, Environment, Enemy Balance, Loot, Interactions, Controls,
Bots, Expedition Resources/Enemies/Level Gen/Doors, one section per melee weapon and per weapon
type/archetype, Sentries, Fog Clearing, and Experimental. (The Artifacts section and the kit
settings under Loot moved to GTFO.EZ.AbsurdItems; an older `.cfg` keeps them as leftover lines that
do nothing here.)

BepInEx keeps whatever value is already in a player's `.cfg`, so when this mod changes a *default*
between versions, an existing install won't pick up the new number on its own -
`Plugin.ApplyConfigDefaultMigrations` applies the one-time, per-setting correction instead of
leaving upgraders silently stuck on a stale default (see `package/CHANGELOG.md` for the version
each migration was introduced in).

## Testing

```powershell
dotnet test tests\GTFO.EZ.Core.Tests\GTFO.EZ.Core.Tests.csproj
```

No game install or BepInEx profile needed - `GTFO.EZ.Core` holds the plugin's pure balance data and
math (weapon/melee config-table generation, the `WeaponMult`/`MeleeMult`/`ScaleInt` multiplier and
rounding logic, config-key validation) with no BepInEx/Il2Cpp dependency, so it's unit tested
directly with NUnit. The datablock patches themselves (`src/GTFO.EZ/Patches/`) can't be unit
tested - they only run against the game's own loaded data - so verify those by launching the game
after `build.ps1`.

## Project layout

```text
src/GTFO.EZ/          the plugin project (BepInEx/Il2Cpp - needs the game installed to build)
src/GTFO.EZ.Core/     pure balance data/math the plugin uses, with no BepInEx/Il2Cpp dependency -
                      split out so it's unit-testable; ships alongside GTFO.EZ.dll at runtime
tests/                GTFO.EZ.Core.Tests - NUnit, runs with plain `dotnet test`, no game needed
package/              exactly what ships to Thunderstore / gets zipped for sharing
  manifest.json       Thunderstore package name is "GtfoEZ" (no period allowed there)
  README.md           becomes the Thunderstore page - full vanilla-vs-default tables and changelog
  CHANGELOG.md
  icon.png            256x256, resized from reference/logo-1254x1254.png
reference/            (gitignored) local-only: EasyMode's files, vanilla data dumps, notes
build.ps1             builds the plugin and packages dist/GTFO.EZ-<version>.zip
external/
  GTFO.EZ.Shared/     git submodule: the code every GTFO.* mod compiles in (see Shared code)
```

## Shared code

GTFO.EZ is the core of the GTFO.EZ family: GTFO.EZ.AbsurdWeapons, GTFO.EZ.AbsurdMoves and
GTFO.EZ.AbsurdItems are companion mods built on it (each compiles in the code below; each works
on its own too), and GTFO.CrashTestDummies compiles in the same shared code.

The code shared by every GTFO.* mod lives in its own repo,
[GTFO.EZ.Shared](https://github.com/mindattic/GTFO.EZ.Shared). Each mod has it as a git submodule
at `external/GTFO.EZ.Shared` and compiles the files it uses straight into its own DLL (linked
`<Compile>` items in its `.csproj`, shown under `Shared\` in the IDE), so players install nothing
extra. Clone with `--recurse-submodules`, or run `git submodule update --init` in an existing clone. The files:

- `SharedOwner.cs` - with several of the mods installed, picks the one that answers shared keys
  (the first installed of GTFO.EZ, GTFO.EZ.AbsurdWeapons, GTFO.EZ.AbsurdMoves, GTFO.EZ.AbsurdItems,
  GTFO.CrashTestDummies)
  and holds state every copy must agree on once for the whole game process. A mod whose master
  switch (`[0. Mod] Enabled`) is off counts as not installed. `SharedOwner.IsActive(guid)` and
  `SharedOwner.TryGetSetting<T>(guid, section, key, out entry)` are how one mod checks another
  (installed, loaded and switched on) and reads its settings.
- `InfiniteToggles.cs` - Insert / Delete, infinite health and ammo, announced in the event log and
  always in the console too.
- `MessageHelper.cs` - on-screen messages for the local player, wherever they fit:
  `MessageHelper.Show(text, MessageType.Warden | EventLog | Center | Subtitle, seconds)`. Compiled by every GTFO.* mod.
- `KitLoot.cs` - kits as consumables. Compiled by GTFO.EZ.AbsurdItems only (its *Consumables Are
  Kits*, with Loot Mix's Hand Grenades and Stim Packs as extras). GTFO.EZ doesn't use it, so
  `GTFO.EZ.csproj` leaves it out of this mod's build.
- `RemoteEvents.cs` - `RemoteEvents.Register<T>(name, handler)`, a GTFO-API network event whose
  handler can't throw (an exception escaping one would hand GTFO-API's packet to the game's own
  receive). Compiled by the mods that use GTFO-API - GTFO.EZ.AbsurdWeapons and GTFO.EZ.AbsurdMoves -
  and, like `KitLoot.cs`, left out of this mod's build.

To change any of them, commit and push to GTFO.EZ.Shared, then in each mod move the submodule to
that commit (`git -C external/GTFO.EZ.Shared pull origin main`), rebuild and commit the new
submodule pointer.

## Versioning

v1, v2, v3... - major-only bumps, matching the convention across other MindAttic projects.
Thunderstore requires `manifest.json`'s `version_number` to be strict `x.y.z`, so "v1" is `1.0.0`,
"v2" is `2.0.0`, and so on (minor/patch stay 0). `package/CHANGELOG.md` headers use the short `vN`
form; the manifest keeps the `N.0.0` form Thunderstore requires.

The GTFO.EZ family - GTFO.EZ, GTFO.EZ.AbsurdWeapons, GTFO.EZ.AbsurdMoves and GTFO.EZ.AbsurdItems -
shares one version number, so a matching set is easy to spot: they were aligned at **v13** (the
companions were v1 before, and GTFO.EZ's in-development v14 was folded into v13), and every release of
the family bumps all four together. GTFO.CrashTestDummies and GTFO.SkipDrop keep their own
numbering. Current release: v15.

## Sharing with friends

- **Private:** Import the zip from `dist/` as a local mod in your mod manager, then
  `Settings -> Profile -> Export profile as code` and send friends the code.
- **Public:** Published on Thunderstore at https://thunderstore.io/c/gtfo/p/MindAttic/GtfoEZ/.
  Everyone in a lobby needs the identical mod and config.

## Limitations

- Ping Anywhere is confirmed in play. An earlier attempt broke the terminal
  by letting the game also evaluate the PING it had already handled; this version owns the
  command fully (BetterTerminal's approach) and falls back to the game's PING on any error.
- The bot tool controls call the bot AI's own placement/pickup methods directly (vanilla has no
  "deploy now" order). Shift+E, X (which does nothing on a spot the game wouldn't allow, behind a
  wall or door, or while you're downed) and Z are confirmed working in play, as is the weapon
  switch-back after placing your own.
- Fog clearing is confirmed in play; its density is read from the vanilla
  Fog Repeller prefab at runtime (logged), with a Density Override if that doesn't clear fog.
- Datablock patches can only be verified by launching the game; they have no automated test
  coverage (see Testing above).

## Documentation

- `docs/USER_STORIES.md` - the project's living feature/ticket log (generated from the shared
  workflow database; don't hand-edit it).
- `package/README.md` - the Thunderstore page: the complete vanilla-vs-default stat tables and the
  full version-by-version changelog.
- `AGENTS.md` - entrypoint for LLM agents working in this repo.

## License

No LICENSE file is included in this repository; all rights are reserved.

## Attributions

See `package/README.md` for full credits to the other GTFO mods and the modding community this
project's design and defaults are built on.

---

Part of [MindAttic](https://mindattic.com) - see more projects at
[github.com/mindattic](https://github.com/mindattic). Other GTFO mods from the same team:
[GTFO.EZ.AbsurdWeapons](https://github.com/mindattic/GTFO.EZ.AbsurdWeapons) (new weapons, with its
own Home toggle to carry them on a serious run),
[GTFO.EZ.AbsurdMoves](https://github.com/mindattic/GTFO.EZ.AbsurdMoves) (new movement abilities,
gated behind its own End toggle),
[GTFO.EZ.AbsurdItems](https://github.com/mindattic/GTFO.EZ.AbsurdItems) (artifact buffs and glow,
the Hand Grenade and Stim Pack, consumables as kits, and breadcrumb markers),
[GTFO.CrashTestDummies](https://github.com/mindattic/GTFO.CrashTestDummies), and
[GTFO.SkipDrop](https://github.com/mindattic/GTFO.SkipDrop).
