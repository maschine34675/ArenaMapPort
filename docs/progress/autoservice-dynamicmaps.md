# Chop Shop DynamicMaps overview — 2026-11-14

User request: add the missing overview for Chop Shop. Work is on
`feature/chopshop-dynamicmaps`, after published 0.6.0; no release or upload requested.

## Geometry and package

- Map ID `arena_autoservice`, player-facing name `Chop Shop`, canonical package
  `ArenaMapPort_AutoService` (the existing release packer's identity).
- Accepted `work/extract/autoservice-native-v2`, current built
  `work/build/autoservice/arena_autoservice_navmesh.bundle`. No scene/bundle rebuild.
- The Unity probe failed before export because the VM has no valid Editor license.
  Existing `navmesh_diff.load_bundle_mesh(...).fan_triangles()` reads the bundle's
  native Detour polygons instead. The fallback was checked against the existing
  Iceberg Unity export: all 8,599 triangles match, including multiplicity, at 1e-6
  world-coordinate precision; a deliberately displaced vertex is rejected.
  Local reproducible script/receipt: `work/dynamicmaps/autoservice/export-triangulation.py`
  and `navmesh.json`. Chop Shop has 1,946 triangles in total.
- A 3D edge-connected selection at original Arena AI core 3
  `(-367.94, -3.79, 492.36)`, height band -4.1..-0.1 and 1 cm seam welding,
  retains the hall, yard, both pockets and raised northern route. It removes
  disconnected roofs and ground islands before XZ projection. The independently
  decoded component has 981 triangles, 1,924.8211 m2 and Y -3.80..-0.45.
  About 3.245 m2 overlap in projection at the raised car/ramp around X -372,
  Z 504..508; this is one overview, not separate building floors.
- Bounds X -415..-331 / Z 464..515 match the bake (84 x 51 m). North = +Z.
  Coordinates remain `(Unity x,y,z) -> (DynamicMaps x,z,y)`, rotation zero.
- 3,422 colliders considered; all 23 door-pose rows (four switches) resolve.
  The drawing uses open combat gate poses, relative obstacle heights and 20 cm
  obstacle simplification. 6,060 SVG points, below the 16,000-point guard.
- Exactly `ArenaMapPort_AutoService.jsonc` and `Layers/Ground.svg` go under
  `maschine-ArenaMapPort/dynamicmaps/ArenaMapPort_AutoService/`. The owned loader,
  Release/DllOnly staging, validator and scoped deploy selector include this pack.
  DynamicMaps' own files and other overview packs are unchanged.

## Validation status

Offline preview reviewed: both pockets, central cover and the raised northern
route are represented. Native rendering, marker alignment, final test results and
deployment evidence will be appended after the host run. Local evidence and gate
ledger: `work/dynamicmaps/autoservice/`.

## Native acceptance and final checks — 2026-11-14

Candidate source `322e3a2d2d739656022458ec4c2df7db3c177917`, client Release build:
zero warnings/errors. The only live deployment was the client DLL plus the two
overview files through `deploy-to-spt.ps1`, after dry runs. Syncthing matched all
three files on GPU-2 after 14.2 seconds. Server and scene bundles were not deployed.

- Loaded client `0.6.0+322e3a2d2d739656022458ec4c2df7db3c177917`, MVID
  `63757983-c2ce-41bf-aee8-c8244dcb5f54`, SHA-256
  `1834f28c37dc933fdadad607804bb513ed6cb50e6996b414045ca00278ceac8e`.
  VM build, host disk and running process agree.
- Fresh GPU-2 process `7c749539206a45e6b9642bb2160c7661`, native scenario
  20:30:57–20:32:17 Europe/Berlin, 49 steps plus three cleanup steps, all passed.
  Plain PMC, no bots, temporary clear/noon overrides, test profile only.
- Four screenshots confirm the player on the expected route and correct heading:

| Position | Requested Unity X / Y / Z | Heading |
|---|---|---|
| East pocket | -338.45 / -3.8 / 490.47 | north, arrow up |
| Central core 3 | -367.94 / -3.79 / 492.36 | east, arrow right |
| West pocket | -409.31 / -3.74 / 487.04 | west, arrow left |
| Raised north route | -385.87 / -1.25 / 511.27 | south, arrow down |

Native floor sampling makes small Y adjustments. The overview automatically loads
as `CHOP SHOP / GROUND`; one level, explicit compass/N, and both native exit markers
render correctly. An independent reviewer checked all four screenshots. Native
player-follow panning crops the viewport near edges; the asset itself is complete.
Screenshots and structured results: `work/dynamicmaps/autoservice/native/`.

MapPort and DynamicMaps warning/error queries are empty. The full file log has one
Unity `MenuScreen is inactive` coroutine error while leaving, also present in six
earlier GPU-2 logs that day before this candidate (including 17:59); this overview
does not fix that existing menu issue. Normal leave/heal completed; final status
shows MainMenu, no raid, no control lease, godmode off, no clock/weather override.
No minimap-specific or Domination overlay acceptance is claimed by this test.

Offline checks: 67 cartography tests, 62 distribution fixtures, 154 scoped deploy
cases, nine C# owned-loader tests, 13 feature-list tests and ten package-text tests.
The altered synthetic roof makes removing the 3D component selection fail; an
independent in-memory mutation check confirmed that. Actual distribution validation
passes for six maps, 18 encrypted bundles and six sidecars; packaging classifies the
two new files under Chop Shop. The distribution now contains 398 files, not the
396-file published 0.6.0 ZIP. No release, upload or version bump was requested.

Receipt audit: all 22 player/bot/exit anchors lie on the selected ground within the
0.5 m tolerance; generated, accepted, staged, installed and host map bytes agree.
The pre-start audit matched all 335 original configuration/DynamicMaps/overview
files. After the game test, 333 remain byte-identical, including every pre-existing
overview and all DynamicMaps files/settings. Two unrelated config files were
rewritten during game startup before the raid: `com.maschine.ArenaMode.cfg` at
20:30:10 and `com.wtt.commonlib.cfg` at 20:30:17. No direct config edits or config-set
calls were made. Only their pre-start hashes were saved, so semantic preservation
of those two files is not claimed. Their before/after hashes and startup timestamps
are explicitly retained in `startup-config-rewrites.json`; the final audit pins
those exact observed results rather than dropping either file silently.

Gate ledger and pinned test-source/log receipts remain under the same ignored work
folder. All game-derived outputs remain local; only source, tests and documentation
are committed. The independent source and visual review found no remaining issue.
Final gate run: **five met, zero unmet, zero abandoned**. A final read of the host's
two map files and client DLL matches the installed candidate (`host-final-hashes.json`).
