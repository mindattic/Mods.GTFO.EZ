# MindAttic project agent entrypoint

Read the shared protocol at ..\mindattic-agent-standard\AGENTS.md and this project's own
documentation. Common prompt commands are implemented by the shared runner; do not add a second
copy under a provider-specific command folder.

## Golden rule: GTFO.EZ.AbsurdItems compatibility (G.EZ-T112)

GTFO.EZ.AbsurdItems is NOT merged into this mod - it stays its own repo, plugin and config - but
`package/manifest.json` lists it as a Thunderstore dependency, so every GTFO.EZ install auto-installs
it too. Both are therefore effectively always running together in the wild. Before changing a Harmony
patch here, check whether GTFO.EZ.AbsurdItems patches the same original method (there is real
precedent: G.EZ-T108, a patch-ordering cycle between the two mods' loot-cleanup patches) - use
explicit `[HarmonyBefore]`/`[HarmonyAfter]` naming the other mod's GUID rather than relying on load
order, and keep config section/key names from colliding across the two mods' `.cfg` files. When this
mod's version bumps, check whether GTFO.EZ.AbsurdItems also needs a bump, and if so update the pinned
version in the `MindAttic-GtfoEZAbsurdItems-X.Y.Z` dependency string here to match what's actually
published - an unpinned/stale version here breaks the auto-install for every new user.
