# ArenaMapPort DynamicMaps tools

Generates a local map pack for the installed DynamicMaps 1.2.1. The map uses the
existing extracted colliders and native triangulation of each map's built NavMesh.
Generation does not change scene bundles or DynamicMaps configuration. ArenaMapPort's
optional client integration adds these maps to installed DynamicMaps 1.2.1 in memory.
Game-derived output remains under ignored `work/`; do not commit it or assume
redistribution permission from this tool's availability.

## Generate

Run from the repository root in PowerShell (Unity 2022.3.43f1):

```powershell
python -m venv work/dynamicmaps/venv
& ./work/dynamicmaps/venv/Scripts/python.exe -m pip install -r tools/dynamicmaps/requirements.txt

$editor = 'C:/Program Files/Unity/Hub/Editor/2022.3.43f1/Editor/Unity.exe'
$repo = (Get-Location).Path
$navBundle = "$repo/work/build/full/arena_equator_navmesh.bundle"
$output = "$repo/work/dynamicmaps/equator"
New-Item -ItemType Directory -Force -Path $output | Out-Null
Start-Process -FilePath $editor -WindowStyle Hidden -Wait -ArgumentList @(
  '-batchmode', '-nographics', '-projectPath', "$repo/UnityProject",
  '-executeMethod', 'ArenaMapPort.Editor.NavMesh.ExportMapTriangulation.Run',
  '-mapNavMeshBundle', $navBundle, '-mapTriangulationOutput', "$output/navmesh.json",
  '-logFile', "$output/unity-export.log"
)
# Check the exit/log and the [MapTriangulation] exported line before continuing.
& ./work/dynamicmaps/venv/Scripts/python.exe tools/dynamicmaps/export_equator.py `
  --package work/extract/equator-native-v5 `
  --navmesh work/dynamicmaps/equator/navmesh.json --output work/dynamicmaps/equator
& ./work/dynamicmaps/venv/Scripts/python.exe -m unittest discover -s tools/tests -p test_dynamicmaps_export.py -v
pwsh -NoProfile -File tools/tests/test_dynamicmaps_deploy.ps1

# Review the two-file plan; -Confirm performs the authorized copy.
pwsh -NoProfile -File tools/deploy-to-spt.ps1 -SptRoot 'D:/SPT41' `
  -DynamicMapsPackage work/dynamicmaps/equator/pack
pwsh -NoProfile -File tools/deploy-to-spt.ps1 -SptRoot 'D:/SPT41' `
  -DynamicMapsPackage work/dynamicmaps/equator/pack -Confirm
```

Run the Unity export in a fresh batch editor with no loaded NavMesh. This reads the
existing bundle using Unity's own triangulation API; it does **not** rebuild anything.
`navmesh.json` records the bundle SHA-256. Use the plaintext build counterpart of the
installed bundle and preserve that receipt. Since 0.3.0, `dist/` and the install contain
encrypted AMPB containers, which this Unity probe cannot load; use `work/build/full`
(Equator), `work/build/bay5`, `work/build/airpit` or `work/build/bowl`. Verify the staged
container corresponds to that build through the bundle tooling. Restart EFT after
installing/replacing the pack to avoid cached map definitions/sprites.

Pack destination:
`BepInEx/plugins/maschine-ArenaMapPort/dynamicmaps/ArenaMapPort_Equator/`

### Bay5

Use the same Unity export with `arena_bay5_navmesh.bundle` and output
`work/dynamicmaps/bay5/navmesh.json`, then:

```powershell
& ./work/dynamicmaps/venv/Scripts/python.exe tools/dynamicmaps/export_equator.py `
  --package work/extract/bay5-native-v5 --descriptor tools/maps/bay5.json `
  --navmesh work/dynamicmaps/bay5/navmesh.json --output work/dynamicmaps/bay5
& ./work/dynamicmaps/venv/Scripts/python.exe -m unittest discover -s tools/tests -p 'test_dynamicmaps*.py' -v
pwsh -NoProfile -File tools/tests/test_dynamicmaps_deploy.ps1
pwsh -NoProfile -File tools/deploy-to-spt.ps1 -SptRoot 'D:/SPT41' `
  -DynamicMapsPackage work/dynamicmaps/bay5/pack -DynamicMapsMap Bay5
```

The historical exporter filename remains compatible with Equator commands; the descriptor
selects the profile (Equator, Bay5, AirPit and Bowl). Bay5 requires native triangulation. Its exact package files are
`ArenaMapPort_Bay5.jsonc` and `Layers/Ground.svg`; its fixed destination is
`BepInEx/plugins/maschine-ArenaMapPort/dynamicmaps/ArenaMapPort_Bay5/`. Each package deploy
leaves the other map's files alone. Bay5 requires `-DynamicMapsMap Bay5`; omitting
the selector defaults to Equator and rejects a Bay5 package. Add `-Confirm` only
for the authorized installation.
**Bay5 was installed and natively tested after the user's host release on 2026-09-22.**
See [acceptance and limits](../../docs/progress/bay5-dynamicmaps.md).

When working in an isolated checkout, pass absolute read-only source/bundle paths.
Do not launch an editor against another session's Unity project. A separate minimal
project with the matching `ProjectVersion.txt`, this repository's
`ExportMapTriangulation.cs` under `Assets/Editor`, and the Unity modules `ai`,
`assetbundle`, `jsonserialize` (each `1.0.0`) can read the existing bundle headlessly.

Only the selected map's JSON and approved SVG layers are deployed. The preview
and source/hash receipt are outside `pack/`. Missing, extra, linked or malformed
package files are rejected. The deploy is copy-only and sequential; after a partial
I/O failure, correct the cause and repeat the deployment.

## AirPit (including original team boxes)

Export `arena_airpit_navmesh.bundle` in the isolated Unity probe, then run:

```powershell
& ./work/dynamicmaps/venv/Scripts/python.exe tools/dynamicmaps/export_equator.py `
  --package work/extract/airpit-native-v9 --descriptor tools/maps/airpit.json `
  --navmesh work/dynamicmaps/airpit/navmesh.json --output work/dynamicmaps/airpit/ground-only
pwsh -NoProfile -File tools/deploy-to-spt.ps1 -SptRoot D:/SPT41 `
  -DynamicMapsPackage work/dynamicmaps/airpit/ground-only/pack -DynamicMapsMap AirPit
```

AirPit ships only `ArenaMapPort_AirPit.jsonc` and `Layers/Ground.svg`, under its
own `dynamicmaps/ArenaMapPort_AirPit/` folder. **The upper area is for spectators,
not players** (user clarification, 2026-10-13); no upper layer is generated/shipped.
Ground uses world Y -0.5..1.2 m; 1 cm projection snapping closes float-sized tile
seams that would otherwise cut the south apron away from the anchored component.
The fixed drawing extent is X -60..40 / Z -86..86, north up, including both original
team boxes. Re-export triangulation from the current U8 bundle before generation;
expanding the drawing alone cannot restore the north pocket missing from U6.
Muted ground includes
isolated NavMesh regions; cartography is not proof that such areas are accessible.

Accepted source, checks and native screenshots: [AirPit overview](../../docs/progress/airpit-dynamicmaps.md).
Legacy cleanup remains restricted to the old Equator/Bay5 packages.

## Bowl (PLAN-BOWL item 17, 2026-10-18)

Export the plaintext `arena_bowl_navmesh.bundle` from `work/build/bowl` (the source of dist's encrypted copy) in the
isolated Unity probe, then generate and review the two-file plan:

```powershell
# The probe's script must equal the repository copy before it is used.
(Get-FileHash work/dynamicmaps/UnityProbe/Assets/Editor/ExportMapTriangulation.cs).Hash -eq `
  (Get-FileHash UnityProject/Assets/ArenaMapPort/Editor/NavMesh/ExportMapTriangulation.cs).Hash
$editor = 'C:/Program Files/Unity/Hub/Editor/2022.3.43f1/Editor/Unity.exe'
$repo = (Get-Location).Path
Start-Process -FilePath $editor -WindowStyle Hidden -Wait -ArgumentList @(
  '-batchmode', '-nographics', '-projectPath', "$repo/work/dynamicmaps/UnityProbe",
  '-executeMethod', 'ArenaMapPort.Editor.NavMesh.ExportMapTriangulation.Run',
  '-mapNavMeshBundle', "$repo/work/build/bowl/arena_bowl_navmesh.bundle",
  '-mapTriangulationOutput', "$repo/work/dynamicmaps/bowl/navmesh.json",
  '-logFile', "$repo/work/dynamicmaps/bowl/unity-export.log")
& ./work/dynamicmaps/venv/Scripts/python.exe tools/dynamicmaps/export_equator.py `
  --package work/extract/bowl-native-v2 --descriptor tools/maps/bowl.json `
  --navmesh work/dynamicmaps/bowl/navmesh.json --output work/dynamicmaps/bowl
& ./work/dynamicmaps/venv/Scripts/python.exe -m unittest discover -s tools/tests -p 'test_dynamicmaps*.py' -v
pwsh -NoProfile -File tools/deploy-to-spt.ps1 -SptRoot 'D:/SPT41' `
  -DynamicMapsPackage work/dynamicmaps/bowl/pack -DynamicMapsMap Bowl
```

Bowl ships only `ArenaMapPort_Bowl.jsonc` and `Layers/Ground.svg` under
`BepInEx/plugins/maschine-ArenaMapPort/dynamicmaps/ArenaMapPort_Bowl/` (display `Bowl`, title `BOWL`).
Add `-Confirm` only for the authorized installation; omitting `-DynamicMapsMap Bowl` rejects the package.

- Ground band world Y -0.5..1.2 m (the pitch lies at y 0.0-0.1). The Mi-26 roof cell (y 1.6), the guard-tower
  platforms and the stands (to y 11) are above it and never become ground. Obstacle heights are relative to the
  nearest native ground triangle (like Bay5/AirPit).
- Anchor: Arena's core point 2 at (2.5, -21.1), the same NavMesh component as both team pockets. The capture-point
  centroid (-1.76, -33.92) lies under the helicopter belly, off the mesh.
- Fixed bounds X -66..62 / Z -82..11, north up: exactly the descriptor's `bake_bounds` (a test pins the two together).
- The U1 NavMesh tiles meet at z=-51.2 with a float-sized gap like AirPit's, so the 1 cm seam grid is on; without it
  the south half (1,380 m2) is cut away from the anchored component.
- Obstacle simplification 20 cm (AirPit's value): 14,104 SVG points; the 18 cm default gave 15,878, 122 under the
  16,000-point guard.
- `hidden_paths` (new, optional per profile) drops hierarchy prefixes from the drawing exactly like opened barriers.
  Bowl hides `SBG_Arena_Bowl_Building/SOO_LOD0/BUILDING/Stadium` and `.../OO/Stadium_Chairs_F1`, the stands' lowest
  rows that otherwise draw two full-width strips along z=10 and z=-81. The prefixes match whole path segments.
- Two `Cabin_ladder` MeshColliders carry no mesh (`mesh_id: null`); the exporter skips explicit nulls and counts them
  (`meshless_mesh_colliders_skipped` in the receipt). A malformed non-null ID still fails.
- `DOORS_SPAWN_A/B` are roots of the TeamFight design scene. Their `.doors.json` poses keep the BTR, ambulance and
  gate leaves visible in their open combat positions, including standing frames and rails.

Source, counts and limits: [Bowl overview](../../docs/progress/bowl-dynamicmaps.md).

## Release staging and migration (2026-10-11)

Accepted packs are stored under ignored
`work/dynamicmaps/release/ArenaMapPort_Equator/` and `ArenaMapPort_Bay5/`, plus `ArenaMapPort_AirPit/` and
`ArenaMapPort_Bowl/` (item 17).
Copy the approved files from each accepted `pack/` there after visual validation.
The 2026-10-24 gate refresh supersedes the earlier compass-only Equator/Bay5 packs and the October AirPit/Bowl packs;
see [the regeneration record](../../docs/progress/dynamicmaps-gate-refresh-20261024.md).
Every client Release build stages these eight explicit files under
`dist/BepInEx/plugins/maschine-ArenaMapPort/dynamicmaps/`, including DLL-only builds.
Preview PNGs and generation receipts are not shipped.

DynamicMaps remains optional. Its soft dependency enables two reflected hooks when
installed: load each owned package after the native `Maps` search, and resolve that
package's allowlisted SVG layers to absolute paths. JSON's historical `Maps/ArenaMapPort_...`
token is retained for byte compatibility; only the in-memory owned layer is rewritten.
Native `Markers/exit.png`, other definitions, settings and DynamicMaps binaries stay unchanged.

For older development installations, close EFT, deploy the adapter DLL and both owned
packs, then review and run the **separate** cleanup:

```powershell
pwsh -NoProfile -File tools/deploy-to-spt.ps1 -SptRoot D:/SPT41 -RemoveLegacyDynamicMaps
pwsh -NoProfile -File tools/deploy-to-spt.ps1 -SptRoot D:/SPT41 -RemoveLegacyDynamicMaps -Confirm
```

Cleanup verifies both installed replacements and the client DLL against dist before
removing only the four old JSONC/SVG files, then their empty directories. Unknown
content or links stop the entire preflight. No recursive deletion is used. Missing
legacy directories are a no-op. Restart EFT afterwards: DynamicMaps does not evict
old definitions when files are deleted during a running process.

## Coordinates and appearance

- Map identity: `arena_equator`, ground level 0, rotation 0.
  Bay5 uses `arena_bay5` with the same level and rotation convention.
- World `(x,y,z)` becomes map `(x,z,y)`; map height is world Y.
- SVG `(u,v) = (x-xmin, zmax-z)` with a zero-origin viewBox and a full-size
  background rectangle. JSON Bounds and ImageBounds use the same world X/Z extents.
- Light ground: the projected NavMesh component containing Equator's tested atrium
  anchor `(0,0)`. Muted blue: other projected components. Dark blue: underlying floor
  and tall obstacle contours. Ochre: lower obstacle projections.
- Bay5 selects native ground triangles entirely within world Y **-4.5..-2.5 m** and
  the projected component containing X/Z **(90,30)**. This excludes most higher
  prop/container surfaces and large disconnected background regions. Its bounds are
  X **41..149**, Z **-18..75** with the 2026-10-24 triangulation (previous north edge 74;
  the extra metre is drawing padding). Height alone is not a semantic floor classifier.
- Bay5 obstacle heights are relative to the nearest native ground triangle, with
  barycentric interpolation and clamping at triangle edges. Its varying floor and
  ramp elevations therefore do not require one global zero plane. Native projected
  floor area takes priority over obstacle overlays, preventing large floor triangles
  from producing false diagonal cover. Overhangs above that ground are omitted.
- Original floor faces within ±0.45 m and obstacle slabs at 0.4–1.8 m contribute.
  Disabled/inactive/trigger colliders, water and numeric trigger/border layers are excluded.
- The sibling `.doors.json` sidecar places gates in their **released combat poses**. Resolve against the original scene
  first: full hierarchy path, optional closed-world `at` within 5 cm, repeated part names by closed local position;
  reject missing/ambiguous rows and closed-pose drift over 5 cm / 2 degrees. Euler composition follows Unity's Z-X-Y
  order; pure DotAnimator slides keep their scene rotation, switches change only active state.
  Unposed structure remains (depot posts, Bowl rails, AirPit's back bus-door row). Legacy barriers without pose rows
  retain the former child-hiding/frame-preserving treatment. Profile `hidden_paths` always hides exact path segments.
- Animated collider projections survive the usual native-floor subtraction: their parked positions are absent from the
  static bake, with runtime carvers added after opening. Whole gate/frame bounds are never drawn as solid barriers.
  Tall closed meshes can appear as contours. The map is static: AirPit's passport gates are drawn open for team combat
  although they stay closed in raids without a hold/release. It does not display the current opening animation or carver state.
- Each receipt records the door-sidecar hash, resolved row and switch counts and parked projected areas. Validate all rows
  for every production map (currently Equator 18, Bay5 7, AirPit 36, Bowl 19; since 2026-10-25 the exit-lamp switch rows count); a missing sidecar must not be accepted as a refresh.
- Geometry is simplified (12 cm Equator / 18 cm Bay5 / 20 cm AirPit / 20 cm Bowl obstacle tolerance, 4.5 cm NavMesh tolerance,
  7 cm obstacle closing, tiny holes below 0.15 m² removed). This improves legibility;
  the result is not an exact collision, line-of-sight or navigation guarantee.
- Upper floors, roofs and background scenery are omitted. Round primitive colliders
  use conservative boxes; tall closed meshes may appear as outlines. Some obstacles
  only appear as NavMesh holes. Future maps need their own height bands/anchor review.

The exporter enforces a 16,000 path-point ceiling; actual Unity tessellation must
still be checked in-game against DynamicMaps' 65,500-vertex limit. The PNG preview
is an offline geometry preview, not proof of the native SVG renderer.

## In-game acceptance

In a matching PMC raid, open inventory → **MAP**. DynamicMaps should select the new
map automatically; with one matching definition its map selector can be hidden.
Verify the player coordinate text and arrow at several world positions/headings.
North is world +Z (up), east is +X (right). Active raid exits come from DynamicMaps'
native provider; the package's two static markers are shown only outside raids.
Do not copy or modify the third-party plugin or its saved settings.

Check logs for load/tessellation errors, then leave the raid normally and verify
the profile. The repository's progress log records the tested candidate and limits.

## Compass-only updates (Equator and the existing Bay5 pack)

The upper-left compass has a prominent N, a gold north half and a muted south half.
All shapes, including N, are SVG paths: no native font/text support is required.
Both exporter profiles include it automatically. An existing older Bay5 drawing
can receive the same marker without regenerating its geometry or map definition:

```powershell
& ./work/dynamicmaps/venv/Scripts/python.exe tools/dynamicmaps/update_north_marker.py `
  --source D:/SPT41/BepInEx/plugins/maschine-ArenaMapPort/dynamicmaps/ArenaMapPort_Bay5 `
  --output work/dynamicmaps/north-marker/Bay5/pack --map Bay5
pwsh -NoProfile -File tools/deploy-to-spt.ps1 -SptRoot D:/SPT41 `
  -DynamicMapsPackage work/dynamicmaps/north-marker/Bay5/pack -DynamicMapsMap Bay5
# Add -Confirm after reviewing the two-file plan. Restart EFT to clear cached SVGs.
```

The updater requires exactly one original arrow and stages a separate output; it
refuses an already-updated or unfamiliar drawing. All other SVG bytes and the JSONC
are preserved. For Equator use `--map Equator` and its matching paths. Deployment
defaults to Equator for compatibility; `-DynamicMapsMap Bay5` explicitly selects
the other allowed destination. Both modes validate exactly the map's JSONC and
`Layers/Ground.svg`, bound to the selected identity and image path.
