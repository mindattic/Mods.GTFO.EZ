---
description: Release the whole GTFO.EZ mod family to Thunderstore - skips the release if nothing changed anywhere, otherwise bumps all four versions in lockstep, builds each dist zip, publishes in dependency order, commits and pushes.
argument-hint: "(no arguments)"
allowed-tools: Read, Edit, Write, Bash, PowerShell, Skill
---

Overrides the global NexusMods-oriented `/publish` inside this repo family - these four ship on
Thunderstore, not Nexus, and always release together. This file is identical in all four repos
(Mods.GTFO.EZ, Mods.GTFO.EZ.AbsurdItems, Mods.GTFO.EZ.AbsurdMoves, Mods.GTFO.EZ.AbsurdWeapons) so
`/publish` works the same from any of their windows; always act on all four by their absolute
paths below, never just the one you're sitting in.

```
AbsurdItems = D:\Projects\MindAttic\Mods.GTFO.EZ.AbsurdItems
GTFO_EZ     = D:\Projects\MindAttic\Mods.GTFO.EZ
Moves       = D:\Projects\MindAttic\Mods.GTFO.EZ.AbsurdMoves
Weapons     = D:\Projects\MindAttic\Mods.GTFO.EZ.AbsurdWeapons
```

**History check (`git log --follow -p -- package/manifest.json` in each repo) shows all four have
moved in exact lockstep on every past release (16→17→...→21, same date each time) - this command
preserves that: every run bumps all four by one major version together, even if one mod's own
`package/CHANGELOG.md` has nothing pending**, so GTFO.EZ's pinned AbsurdItems dependency version
never drifts from what's actually published.

The user authorized this command's whole sequence - including the commits, the pushes and the
public Thunderstore publish - when they asked for `/publish` to be wired up this way (see this
repo's own commit that added this file). Running `/publish` is that standing approval for this
run; don't re-ask before publishing or pushing. Do stop and ask if something below says to.

## 0. Preconditions

- **Crossrepo.** This command touches all four repos. If the session isn't already cross-repo'd
  into the other three (check the `[Orchestrator] Crossrepo is ...` line from this session's
  start, or just try step 2's edits and see if the gate refuses), **ask the user to type**
  `/crossrepo Mods.GTFO.EZ.AbsurdItems,Mods.GTFO.EZ.AbsurdMoves,Mods.GTFO.EZ.AbsurdWeapons` (adjust
  the list to exclude whichever repo this window is already in) **and stop**. Never call the
  crossrepo Skill yourself for this - the gate only accepts it from the user's own typed prompt,
  confirmed by this family's own publish history.
- **Token.** Read `%APPDATA%\MindAttic\Thunderstore\tokens.json`, key `thunderstore` (same Vault
  convention as `TokenStore.ForBucket("Thunderstore")`). If the file or key is missing, stop and
  tell the user: get a token from thunderstore.io account settings -> API Keys, and either paste
  it here so it can be stored the same way, or add it to that file themselves. Don't fabricate or
  reuse an old one from memory.
- **tcli.** `Get-Command tcli`; if missing, `dotnet tool install --global tcli`.
- **Clean trees.** Each of the four repos should have no unrelated uncommitted changes before you
  start (`git status --short`); if one does, stop and ask rather than bundling someone else's
  in-progress edit into a release commit.

## 1. No-op guard - skip the whole release if nothing changed anywhere

Build all four at their **current, not-yet-bumped** version first: `.\build.ps1` in each (same
params as step 4). Hash each one's `package\` folder with
`D:\Projects\MindAttic\mindattic-agent-standard\shared\compute-mod-hash.ps1 -Path <repo>\package
-ExcludeNames manifest.json,CHANGELOG.md,README.md` (excludes the files a version bump always
touches regardless of code, so the hash reflects only the compiled/packaged payload). Compare each
result to that repo's own `.publish-state.json` at its root
(`{ "lastPublishedVersion": "...", "lastPublishedHash": "...", "lastPublishedRef": "<sha>" }`) - if
a repo has none yet, treat it as changed.

**If every one of the four hashes matches its own `lastPublishedHash`:** tell the user plainly that
none of the four mods changed since the last publish, and stop - don't bump, don't rebuild, don't
publish.

**If even one differs:** continue below and bump/build/publish all four together as always - a
single changed mod still means all four rebuild and republish, to keep the family's version pins
from drifting the way step 0's lockstep note already requires. This guard only skips the case where
a run would do nothing but increment every version number for no actual change anywhere.

## 2. Compute the new version

Read `version_number` from `GTFO_EZ\package\manifest.json` and confirm the other three
(`AbsurdItems`, `Moves`, `Weapons`) all show the same value - if any has drifted, stop and say so
rather than guessing which is right. New version = that major number + 1, as `N.0.0` (whole
numbers only, matching every past release).

## 3. Bump each repo

For each of the four, in any order:

- `package\manifest.json`: `"version_number": "<old>"` -> `"<new>"`.
- `src\<ProjectName>\Plugin.cs`: `public const string VERSION = "<old>"` -> `"<new>"` (the project
  folder under `src\` is the repo's own mod name, e.g. `GTFO.EZ.AbsurdItems`).
- **`GTFO_EZ` only**, also bump the dependency pin in its own `package\manifest.json`:
  `"MindAttic-GtfoEZAbsurdItems-<old>"` -> `"MindAttic-GtfoEZAbsurdItems-<new>"` (AGENTS.md's
  golden rule - this pin must always match what's actually published).
- `package\CHANGELOG.md`: if the top section is `## Unreleased` with at least one bullet under it,
  rename that heading to `## <new>`. If `## Unreleased` is missing or empty, insert a new
  `## <new>` section at the very top (above whatever was there) with one line: `- Version bump to
  keep the GTFO.EZ family in lockstep; no functional changes in this mod this release.` Never
  silently bump a repo's changelog with nothing written down for it.

## 4. Build and verify every one before publishing any

Run `.\build.ps1` in each of the four (default params are already right: `-GameName GTFO -Profile
"Easy Mode DEV"`). Each must report **0 Warning(s), 0 Error(s)** and print a `Packaged: ...dist\*-
<new>.zip` line. **If any of the four fails to build clean, stop entirely - publish nothing, and
report which repo and why.** A half-built family release (e.g. GTFO.EZ live pointing at an
AbsurdItems version that failed to build) is worse than not releasing.

## 5. Publish, in this order: AbsurdItems, then GTFO.EZ, then Moves and Weapons (either order)

For each, from that repo's own directory:

```powershell
$token = (Get-Content -Raw (Join-Path $env:APPDATA 'MindAttic\Thunderstore\tokens.json') | ConvertFrom-Json).thunderstore
tcli publish --file "<repo>\dist\<DisplayName>-<new>.zip" --token $token
$token = $null
```

Check the exit code and the printed `https://thunderstore.io/package/download/...` URL for each.
**If AbsurdItems fails to publish, stop before touching GTFO.EZ** - GTFO.EZ's manifest now pins a
version of AbsurdItems that isn't live yet, which would break auto-install for new users; fix
AbsurdItems first (or revert its version bump) rather than publish GTFO.EZ anyway. If GTFO.EZ,
Moves or Weapons fails after AbsurdItems succeeded, still publish the ones that didn't fail (they
don't depend on each other) and report the failure plainly.

(`thunderstore.toml` already exists in each repo for this; `--file` reads name/namespace/version/
dependencies straight from the zip's own `manifest.json`, so it doesn't need to track the version
itself - only `[publish].communities = ["gtfo"]` matters for this publish path. Don't let `tcli
build`/`tcli init` regenerate it or touch `icon.png`/`README.md` at the repo root - the files that
actually ship live under `package\`, and `tcli init` has previously dropped a placeholder
`icon.png` at repo root that is not the real mod icon.)

## 6. Record publish state, commit and push each repo that published successfully

Recompute each repo's payload hash post-build (same command as step 1 - the bumped version string
compiles into the DLL, so this hash differs from step 1's even when no other code changed; that's
expected and correct) and write its `.publish-state.json`: new hash, `"lastPublishedVersion":
"<new>"`, and `lastPublishedRef` set to the commit you're about to make (fill in its real sha after
committing).

One commit per repo, touching only `package\manifest.json`, `src\*\Plugin.cs`,
`package\CHANGELOG.md` and `.publish-state.json` (and `package\manifest.json`'s dependency line for
GTFO.EZ). File a ticket first (`tickets ticket new --repo <Repo> --type chore --summary "Publish
v<new>.0.0 to Thunderstore"`), commit referencing it, close the ticket with the commit hash as
`--ref`, then `git push`. End the commit message and this turn's git activity with the attribution
lines from this session's own system reminder.

## 7. Report

One line per mod: old version -> new version, the Thunderstore URL (or the failure), and whether
its repo was committed/pushed. Say plainly if anything was skipped or stopped early, and why.
