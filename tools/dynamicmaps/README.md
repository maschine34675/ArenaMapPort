# ArenaMapPort DynamicMaps tools

Generates a local map pack for the installed DynamicMaps 1.2.1. The map uses the
existing extracted Equator colliders and native triangulation of our built NavMesh.
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

Only `ArenaMapPort_Equator.jsonc` and `Layers/Ground.svg` are deployed. The preview
and source/hash receipt are outside `pack/`. Missing, extra, linked or malformed
package files are rejected. The deploy is copy-only and sequential; after a partial
I/O failure, correct the cause and repeat the deployment.

## Coordinates and appearance

- Map identity: `arena_equator`, ground level 0, rotation 0.
- World `(x,y,z)` becomes map `(x,z,y)`; map height is world Y.
- SVG `(u,v) = (x-xmin, zmax-z)` with a zero-origin viewBox and a full-size
  background rectangle. JSON Bounds and ImageBounds use the same world X/Z extents.
- Light ground: the projected NavMesh component containing Equator's tested atrium
  anchor `(0,0)`. Muted blue: other projected components. Dark blue: underlying floor
  and tall obstacle contours. Ochre: lower obstacle projections.
- Original floor faces within ±0.45 m and obstacle slabs at 0.4–1.8 m contribute.
  Disabled/inactive/trigger colliders, water, numeric trigger/border layers, round-open
  doors and listed shutter leaves are excluded. Listed shutter frames remain.
- Geometry is simplified (12 cm obstacle tolerance, 4.5 cm NavMesh tolerance,
  7 cm obstacle closing, tiny holes below 0.15 m² removed). This improves legibility;
  the result is not an exact collision, line-of-sight or navigation guarantee.
- Upper floors, roofs and background scenery are omitted. Round primitive colliders
  use conservative boxes; tall closed meshes may appear as outlines. Some obstacles
  only appear as NavMesh holes. Future maps need their own height bands/anchor review.

The exporter enforces a 16,000 path-point ceiling; actual Unity tessellation must
still be checked in-game against DynamicMaps' 65,500-vertex limit. The PNG preview
is an offline geometry preview, not proof of the native SVG renderer.

## In-game acceptance

In an Equator PMC raid, open inventory → **MAP**. DynamicMaps should select the new
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
Equator's exporter includes it automatically. Bay5's existing user-supplied drawing
can receive the same marker without regenerating its geometry or map definition:

```powershell
& ./work/dynamicmaps/venv/Scripts/python.exe tools/dynamicmaps/update_north_marker.py `
  --source D:/SPT41/BepInEx/plugins/mpstark-dynamicmaps/Maps/ArenaMapPort_Bay5 `
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
