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

## Native result after host release — 2026-11-21

The user released GPU-2 later in this task. Its installed client still identified
`9ed0503` (0.6.0), while the separate original checkout's 0.7.0 preparation remained
uncommitted. This branch's code commit `8a1ec4e` was built without release staging,
then exactly the client DLL and the Yard pair were deployed through reviewed dry
runs and the scoped deploy script. Synchronization verified all three files by
hash on GPU-2; no original-checkout release files were staged or changed.

A fresh game started through gpu-run and a plain PMC Block raid ran without bots,
at noon with clear weather. Loaded informational version
`0.6.0+8a1ec4e91bf78fcf5350f9230b4b5c958e955dbd`, MVID
`f8a38cc5-254a-4131-9540-6a15b2cf094d`, matched the VM and host file. The source code
behind the concurrent release is not rolled back or merged by this task.

- The native catalogue has all 11 existing definitions plus seven owned packs.
  The selected definition is Block, and its loaded layer uses the SVG inside
  `maschine-ArenaMapPort/dynamicmaps/ArenaMapPort_Yard`.
- Unity produced 26,771 sprite vertices and 81,834 triangle indices, below its
  65,500-vertex limit. Ground and marker are active at full alpha.
- South `(-12.68, 7.27, 9.88)`, core `(-5.08, 6.15, 57.92)`, north
  `(-10.83, 6.18, 145.09)`, raised route `(-22, 9.1981, 48.8)` and west bake edge
  `(-65, 6.15, 70)` passed native traversal probes and marker projection checks.
  Headings 0/90/180/270/0 degrees match the native marker. The largest comparison
  difference is 5 cm from UiBridge's rounded player coordinates. The added context
  west of X -67 was reviewed visually, not tested as a player route.
- The 83-step measurement scenario and three cleanup steps passed. Independent
  screenshot review accepted all five map views and the raised/west world views. MapPort and DynamicMaps
  warning/error scans both returned zero with no log gap. Five deliberately
  wrong evidence controls were rejected by the independent validator.
- One earlier wait for the map title failed because the user's saved zoom placed
  the title offscreen; the layer was already rendered correctly. Recovery checked
  the actual selected MapDef. For the screenshots only, zoom and automatic player
  centering changed in memory; all nine In-Raid settings match their pre-test
  values after restoration, and no temporary value was saved.
- At native cleanup: MainMenu, no raid or control lease, godmode/ammo overrides/tracking
  off, no pending time/weather override. The tested DLL and map pair remain
  installed at that point. This branch is committed locally; no publication.

Evidence is retained under ignored `work/dynamicmaps-yard/evidence/native-points`
(screenshots, complete reads and report), `geometry-review/native-evidence-review.json`
and `geometry-review/native-visual-review.json`.

## Preservation reconciliation

Full before/after copies were captured on the VM and GPU-2, not just hashes.
On both, all 93 third-party DynamicMaps files, twelve existing overview files and
seven protected client binary/debug controls are byte-identical. The two Yard
files and installed candidate DLL match their pinned build inputs. No existing
CFG value changed. Besides DynamicMaps' new `arena_yard=Automatic` binding, the
new base client registered twelve previously approved stage-2 grass defaults
(four each for Bowl, Iceberg and Yard); the old probe keys remain untouched.

The strict preservation script intentionally still reports differences: its
clock-only profile rule was too narrow for a real PMC raid. The independent
reconciliation enumerates all 22 changed profile paths, ordinary skill/session
progress, resource regeneration and the native disconnect/healing flow. Complete
PMC inventory, body-part health, the Scav character and other top-level profiles
are unchanged. Three old backup groups rotate to three new ones under SPT's
normal backup limit; all retired copies remain in the private baseline. No save
was rolled back to hide normal game progression.

ArenaMode's generated settings mirror changes only `UpdatedAt`, with identical
entries. PitFireTeam's one changed database contains only migration and raid
diagnostic documents: the migration and all 401 player-item/one transfer-item
IDs remain unchanged. Its VM snapshot initially preceded the final raid report;
after synchronization, two equal-hash polls confirmed the completed database on
both hosts. A second complete `after-settled` snapshot retains this final state.

`native-review/preservation.json` remains the unmodified strict difference report;
`native-review/reconciliation.md` explains the measured changes. Acceptance means
protected map/config data and profile integrity are preserved, with the normal
test raid's progression explicitly accounted for, not that every save byte stayed
the same. Temporary map display settings were restored exactly in memory as well.

## Concurrent release integration

After the successful raid and its first final snapshot, the separate release
session deployed `0.7.0+1ad18c3`, replacing the tested client on both installations.
The settled snapshot identifies that later DLL; it does not contain the Block
overview registration or the 1.3 loader. The successful raid belongs to the
pinned `8a1ec4e` candidate, not this later binary. The Yard data pair remains
installed, but the current 0.7.0 client does not load it after a restart.

The release preparation was then committed as `ca26292` on `feature/yard`. This
task merges that committed preparation into the isolated `feature/block-dynamicmaps`
branch, preserving version 0.7.0, all grass/rain features and release descriptions.
The two overlapping README sections are reconciled to describe seven owned
overviews and the verified 1.3 adapter. The combined branch must be adopted by
the release checkout and its client rebuilt/deployed before publishing it. No
later deployment is silently overwritten by this task.

Combined source commit `2d174c0` builds as `0.7.0+2d174c0` with zero client warnings
or errors. Its 28 production loader/catalog tests pass. The merged package suite
passes 202 tests with two data-dependent skips (the isolated checkout lacks the
spectator-audio input and a complete staged server distribution). These are
offline checks, not a native raid on the combined binary. A ready candidate is
staged only in the isolated worktree; no second live deployment was performed.
The short integration handoff is retained at `work/dynamicmaps-yard/HANDOFF.md`.

## 2026-11-22 - Adopted into `feature/yard` (`4587e5b`), run on the merged build

User, 2026-11-22: "die beiden anderen zweige können gemerged werden, Codex hat dazu auch irgendwas verfasst". The combined branch (`feature/block-dynamicmaps`, tip `b09bece`, source commit `2d174c0` inside it) was merged into `feature/yard` as `4587e5b` (conflict-free), and the lead added `3dcf98d` (try/catch around `DynamicMapsIntegration.Install` in `Plugin.Awake`). The older sections above are the state before the merge and are unchanged; where they say the branch "must be adopted by the release checkout" or that the combined binary had no native raid, the adoption has happened and a raid has run (below).

- The native five-position evidence of this record still belongs to client `0.6.0+8a1ec4e` (raid of 2026-11-21 at 23:13 CET, 22:13:48Z to 22:14:40Z, in the other session's EFT instance that started at 23:11; see the evidence audit in `release-preparation-20261121.md` "2026-11-22").
- The lead's host run of the merged build `0.7.0+3dcf98d` on GPU-2, 2026-11-22: Block's raid at about 09:46 (plain PMC raid, no bots, clear noon), inventory, then MAP opened once. The player marker stood in the south pocket (`Player: -14.86 13.30 7.27`, DynamicMaps prints x, z, y) beside the exit marker "Block South Gate"; after a teleport to the raised route (-22.0, 9.2, 48.8) at yaw 270 the readout was `Player: -22.00 48.80 9.21` and the marker stood on the raised walkway pointing west. The catalogue read in that raid (`MapSelectDropdown._mapDefs`, DynamicMaps 1.3.0.0) held 18 definitions: 11 under `mpstark-dynamicmaps\Maps\` and 7 under `maschine-ArenaMapPort\dynamicmaps\ArenaMapPort_<Yard|AutoService|Iceberg|Bowl|AirPit|Bay5|Equator>\`. Startup logged `[DynamicMaps] Optional owned map loader installed (1.3 catalogue)`; 0 Warnings from `maschine-ArenaMapPort` and from DynamicMaps in the whole session. Screenshots: `analysis/screenshots/merge-070-20261122/01-block-overview-south-pocket.png`, `02-block-overview-raised-route-yaw270.png` (ignored).
- The other six overviews were opened once each in the same process (spawn position, one marker, by eye).
- dist is 424 files, the Yard part 24 (the pair `ArenaMapPort_Yard.jsonc` 1,685 B and `Layers/Ground.svg` 158,498 B included).
- The full record, the gates, the review and the NOT-checked list are in [release-preparation-20261121.md](release-preparation-20261121.md) "2026-11-22 - The two DynamicMaps branches merged, the review, the gates and the host run of the merged build". The author has not looked at the overview.
- EV-5 (review, evidence audit): the mirror's DynamicMaps config gained DynamicMaps' own comment blocks and the line `arena_yard = Automatic` (DynamicMaps' normal preference registration; no existing value changed).
- EV-1, as a fact: for the Yard pair and the DLL this record names "reviewed dry runs and the scoped deploy script", and the other session's folder retains only a dry-run log (`DRY RUN (default) -- no files were copied`), no `-Confirm` log.
