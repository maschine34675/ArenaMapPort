# Block / Yard DynamicMaps overview — 2026-11-21

User request: add an overview for the new Yard map, named Block in Arena. The
location remains `arena_yard`; the owned pack is `ArenaMapPort_Yard`. Work is in
the isolated `feature/block-dynamicmaps` branch so the concurrent Yard release
changes remain intact. The previously verified DynamicMaps 1.3 adapter is merged
into this branch; its 1.2.1 path is retained. This task does not publish a release.

## Source and cartography

- Accepted scene package: `work/extract/yard-native-v2`; accepted plaintext bake:
  `work/build/yard/arena_yard_navmesh.bundle`. No scene rebake or source extraction.
- The read-only bundle decoder matches all 8,599 triangles of Iceberg's previous
  native Unity export as an exact rounded triangle multiset. A displaced triangle
  fails this comparison. Input hashes and control receipts remain under ignored
  `work/dynamicmaps-yard/geometry-review`.
- Yard has 3,953 triangles in 132 edge-connected components. Original Arena AI
  core 3 `(-5.08, 6.15, 57.92)` selects the main 1,601-triangle component, using
  1 cm seam welding before projection. The ground spans Y 6.15..10.70 m; the
  reviewed height band is 5.8..11. A ceiling at 8, 9 or 10 m removes connected
  raised paths. All seven AI cores, 22 player/bot rows and two exits lie on the
  selected component within the 0.5 m anchor tolerance.
- The projected union is about 4,511.6 m2, with about 36.6 m2 of stacked projected
  route area. This is one combined overview, not independent building floors.
  Detached roofs and outer ground islands do not become playable ground.
- Bounds X -77..21 / Z -1..162 (98 x 163 m), north = +Z. The west edge extends
  10 m beyond the bake to include the source's dead-end alley at x -76..-67,
  z 52..92, documented in `yard-release.md`'s border measurement. Its physical
  floor is muted context outside the bright baked footprint. The first preview
  at the exact bake bounds visibly cropped this alley; only the drawing extent
  changes, not the NavMesh, source scenes or physical borders.
  DynamicMaps coordinates remain `(Unity x,y,z) -> (x,z,y)`, rotation zero.
  The three door-pose rows represent the raised cradle and two open gate leaves.

## Integration and acceptance

The loader, explicit release/DLL-only staging, validator, release checklist and
scoped deploy selector include Yard. Exactly `ArenaMapPort_Yard.jsonc` and
`Layers/Ground.svg` belong under
`BepInEx/plugins/maschine-ArenaMapPort/dynamicmaps/ArenaMapPort_Yard/`.
Game-derived files remain ignored and are never committed.

Generation, final validation, build/deployment identity, native screenshots and
preservation evidence are recorded below once observed. Gate ledger and private
evidence: `work/dynamicmaps-yard/`. Native host availability was requested before
changing the test installation.

## Observed offline result

The expanded final drawing has 10,175 SVG path points in a 98 x 163 m view box.
Its bright ground geometry is exactly the first drawing's ground after the 10 m
origin shift; the extra west context does not extend the NavMesh. An independent
review accepted the preview, all 31 source anchors and three raised-route probes.
All three gate-pose rows resolve (four moved colliders). The generator's generic
receipt sentence about omitting higher surfaces is too broad for Yard: connected
raised routes are included in this single projected plan, with about 36.6 m2 of
vertical overlap; detached roof components are omitted.

`work/dynamicmaps-yard/verify-package.py` rechecked all 1,663 recorded input hashes,
the generator and accepted bake hashes, both output hashes and the staged files.
Exactly 14 files are staged for seven packs; the other six packs' 12 files still
match the accepted source byte for byte. The accepted Yard pair is also retained
in the original checkout's ignored `work/dynamicmaps/release/ArenaMapPort_Yard`
for a later merge/build. It is not copied into that checkout's release `dist`.

Checks passed:

- 69 cartography tests, including the raised-route/disconnected-roof distinction
  and west-context coverage; one package README check.
- 168 scoped-deployment cases; 70 distribution fixtures; 14 feature/packaging
  checks; six registry-checklist checks, without skips.
- 28 production owned-loader/catalog checks, actual DynamicMaps 1.2.1 and 1.3.0
  DLL contracts, and eight deliberately broken adapter variants rejected.
- Client Release build: zero warnings and errors, without release staging.
- Explicit map staging and the real-installation dry run: exactly the new Yard
  JSONC/SVG pair in ArenaMapPort's own folder, no writes to the installation.

## Remaining native acceptance

No installation files, configurations or running games were changed. GPU-2
availability is still awaiting the user's reply. Before any later DLL deployment,
reconcile this branch with the concurrent Yard 0.7.0 release work in the original
checkout; do not overwrite a newer client with this branch's 0.6.0 candidate.

Then build the reconciled commit, deploy the owned pair and client through the
reviewed script, verify synchronization and loaded identity, and check a fresh
Block raid at the south pocket, core 3, north pocket, raised path and west edge.
Capture player position/heading and native map rendering, including Unity SVG
tessellation, and preserve other map files, DynamicMaps files, settings and the
profile. The PNG preview and offline checks do not prove native marker alignment.
