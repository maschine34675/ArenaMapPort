# Equator and Bay5 DynamicMaps prototypes

Generates a local map pack for the installed DynamicMaps 1.2.1. The map uses the
existing extracted colliders and native triangulation of each map's built NavMesh.
It does not change any scene bundle, client/server DLL, or DynamicMaps configuration.
Game-derived output remains under ignored `work/`; do not commit it or assume
redistribution permission from this tool's availability.

## Generate

Run from the repository root in PowerShell (Unity 2022.3.43f1):

```powershell
python -m venv work/dynamicmaps/venv
& ./work/dynamicmaps/venv/Scripts/python.exe -m pip install -r tools/dynamicmaps/requirements.txt

$editor = 'C:/Program Files/Unity/Hub/Editor/2022.3.43f1/Editor/Unity.exe'
$repo = (Get-Location).Path
$navBundle = "$repo/dist/BepInEx/plugins/ArenaMapPort/bundles/arena_equator_navmesh.bundle"
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
  --package work/extract/equator-native-features `
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
`navmesh.json` records the bundle SHA-256. Use the same NavMesh bundle as the live
install and preserve that receipt when regenerating the drawing. Restart EFT after
installing/replacing the pack to avoid cached map definitions/sprites.

Pack destination:
`BepInEx/plugins/mpstark-dynamicmaps/Maps/ArenaMapPort_Equator/`

### Bay5

Use the same Unity export with `arena_bay5_navmesh.bundle` and output
`work/dynamicmaps/bay5/navmesh.json`, then:

```powershell
& ./work/dynamicmaps/venv/Scripts/python.exe tools/dynamicmaps/export_equator.py `
  --package work/extract/bay5-native-features --descriptor tools/maps/bay5.json `
  --navmesh work/dynamicmaps/bay5/navmesh.json --output work/dynamicmaps/bay5
& ./work/dynamicmaps/venv/Scripts/python.exe -m unittest discover -s tools/tests -p 'test_dynamicmaps*.py' -v
pwsh -NoProfile -File tools/tests/test_dynamicmaps_deploy.ps1
pwsh -NoProfile -File tools/deploy-to-spt.ps1 -SptRoot 'D:/SPT41' `
  -DynamicMapsPackage work/dynamicmaps/bay5/pack
```

The historical exporter filename remains compatible with Equator commands; the descriptor
selects the profile. Bay5 requires native triangulation. Its exact package files are
`ArenaMapPort_Bay5.jsonc` and `Layers/Ground.svg`; its fixed destination is
`BepInEx/plugins/mpstark-dynamicmaps/Maps/ArenaMapPort_Bay5/`. Each package deploy
leaves the other map's files alone. Add `-Confirm` only for the authorized installation.
**Current Bay5 candidate is staged only: deployment and native testing wait for the
user's explicit release of the shared host.**

When working in an isolated checkout, pass absolute read-only source/bundle paths.
Do not launch an editor against another session's Unity project. A separate minimal
project with the matching `ProjectVersion.txt`, this repository's
`ExportMapTriangulation.cs` under `Assets/Editor`, and the Unity modules `ai`,
`assetbundle`, `jsonserialize` (each `1.0.0`) can read the existing bundle headlessly.

Only the selected map's JSON and `Layers/Ground.svg` are deployed. The preview
and source/hash receipt are outside `pack/`. Missing, extra, linked or malformed
package files are rejected. The deploy is copy-only and sequential; after a partial
I/O failure, correct the cause and repeat the deployment.

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
  X **41..149**, Z **-18..74**. Height alone is not a semantic floor classifier.
- Bay5 obstacle heights are relative to the nearest native ground triangle, with
  barycentric interpolation and clamping at triangle edges. Its varying floor and
  ramp elevations therefore do not require one global zero plane. Native projected
  floor area takes priority over obstacle overlays, preventing large floor triangles
  from producing false diagonal cover. Overhangs above that ground are omitted.
- Original floor faces within ±0.45 m and obstacle slabs at 0.4–1.8 m contribute.
  Disabled/inactive/trigger colliders, water, numeric trigger/border layers, round-open
  doors and listed shutter leaves are excluded. Listed shutter frames remain.
- Geometry is simplified (12 cm Equator / 18 cm Bay5 obstacle tolerance, 4.5 cm NavMesh tolerance,
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
