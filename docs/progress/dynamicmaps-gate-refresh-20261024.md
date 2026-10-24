# DynamicMaps gate/area refresh — 2026-10-24

User request: regenerate the four ported maps after their original spawn gates and changed playable areas were restored.
The parallel spawn-gate completion API work is outside this change.

## Export behavior

`tools/dynamicmaps/export_equator.py` now reads the map's `.doors.json`. It resolves every pose against the original
scene before moving anything: exact hierarchy path, instance world position (`at`, 5 cm), repeated leaf names by closed
local position, and closed-pose validation (5 cm / 2 degrees). Missing, duplicated or ambiguous row targets fail export.
Unity Z-X-Y Euler composition, unchanged source rotation for pure DotAnimator/borrowed slides, nested transforms and
active-state switches follow the runtime behavior. The receipt fingerprints the sidecar and reports row/switch counts.

Posed barriers keep their structure: Bay5 depot posts, Bowl frames/rails, and AirPit's standing back bus-door row.
Animated collider projections at their open poses survive the ordinary subtraction of static NavMesh ground, because
the bake omitted those moving subtrees and runtime adds their park carvers later. Whole barrier bounds are not used
as filled obstacles. Existing projection/simplification limits still apply; tall meshes may appear as contours.

The map shows the **released combat layout**, not live gate state. In particular AirPit's passport gates remain closed
in raids without the hold/release cycle, although the static team-combat overview draws them open.

## Generation and visual checks

Fresh triangulation exported with Unity 2022.3.43f1 in the isolated `work/dynamicmaps/UnityProbe` project; its export script
hash matched the repository copy. Inputs are the current plaintext `work/build/{full,bay5,airpit,bowl}` NavMesh bundles,
not the encrypted files under `dist/`. BundleCrypt `verify` confirmed each installed encrypted NavMesh is exactly the
encryption of the corresponding input. No map rebuild or DLL build was needed.

| Map | Extract | Native vertices / triangles | Matched pose rows | SVG points | Ground.svg SHA-256 prefix |
|---|---|---:|---:|---:|---|
| Equator | equator-native-v5 | 10,395 / 4,567 | 10 / 10 | 10,171 | `69e48684b3e76247` |
| Bay5 | bay5-native-v5 | 22,130 / 10,220 | 7 / 7 | 13,844 | `425ad6bd97b520f3` |
| AirPit | airpit-native-v9 | 11,811 / 5,433 | 30 / 30 (2 switches) | 15,077 | `6fc669cd1dd7a3dd` |
| Bowl | bowl-native-v2 | 12,704 / 5,890 | 19 / 19 | 14,255 | `c5f5a2c5352f1870` |

- Equator: closed FCK shutter and its disconnected floor region; booth leaves remain at open poses. Independent geometry
  inspection found the old FCK spawn on a disconnected component, the shutter line outside the walkable surface, and the
  replacement bot spawn plus both booth samples on the main component.
- Bay5: roller `(3)` remains closed; animated rollers/hinged depot leaves retain open geometry. The computed northern
  drawing edge changes from Z=74 to 75 (one metre of padding from `ceil(maxZ)+3`; main ground ends at about 71.2).
  The matching image/game bounds and title position follow that edge. World metre mapping, rotation and exits are unchanged.
- AirPit: both original boxes, changed static ground near the bus doors, parked front leaves, standing back row and open
  passport leaves are included.
- Bowl: the unchanged static triangulation is augmented visually by the parked BTR, ambulance and gate-leaf projections.

All four exact SVGs were rendered with sharp/librsvg and visually inspected, alongside the generator previews and an
independent review. Quantized path rings are valid and each map remains below the existing 16,000-point guard. All exit
markers, map IDs, rotation and default layer are unchanged. JSON definitions other than Bay5 are byte-identical.

Evidence (ignored, derived game data): `work/dynamicmaps/gate-refresh-20261024/`, including per-map `navmesh.json`,
`unity-export.log`, `receipt.json`, `pack/`, `preview.png`, `new-svg.png` and `old-svg.png`; `generated-verified.json`
and `deployed-verified.json` contain all output hashes. `baseline/` preserves the previous installed packs.

## Validation and delivery

- 61 DynamicMaps Python tests pass, including 8 new synthetic door tests with stale/missing/ambiguous-target negatives,
  repeated roots/leaves, independent mixed-axis rotation, parent-before-child matching, switches, retained structure,
  a raised roller and a parked obstacle visible over static native ground.
- The existing deployment fixture harness passes all 122 cases.
- Four acceptance gates met, zero unmet/abandoned: pose/export tests, source/output contracts, visual review, exact delivery.
- Copied only each accepted pack's two files into `work/dynamicmaps/release/ArenaMapPort_<Map>` and the matching `dist`
  folders; future Release builds therefore preserve the update.
- All four data-only deployment dry runs were inspected, then confirmed with `tools/deploy-to-spt.ps1 -SptRoot D:/SPT41
  -DynamicMapsPackage <pack> -DynamicMapsMap <Map> -Confirm`. Eight files in scope; five changed bytes (four SVGs and Bay5 JSON).
- Generation, accepted release staging, dist and installation hashes match. All 91 snapshotted third-party DynamicMaps
  files/config files are unchanged. No client/server DLL, map bundle, profile or third-party settings were written by this task.
- Read-only checks on the GPU-2 share confirm all eight synced files match their generation receipts
  (`gpu2-sync-verified.json`); no host process was restarted or controlled.

This is offline geometry/render and installation validation, **not a new native EFT acceptance run**. The GPU test session
was not interrupted. Restart EFT to discard DynamicMaps' cached definitions/SVGs; a later native check should cover SVG
tessellation and the gate regions, especially AirPit's passport row and Bowl's parked vehicles.
