# Equator DynamicMaps prototype — 2026-09-22

## Scope and result

The user accepted an Equator-first prototype. A locally generated ground plan is now
installed and natively tested with DynamicMaps 1.2.1, SPT 4.1.6, EFT 0.16.9.5-40743
and Unity 2022.3.43f1. Bay5 and the other maps have no new packs in this milestone.
Branch `feature/equator-dynamicmaps`, base `2c673e2`.

Generated SVG/JSON, preview, triangulation and evidence remain ignored under
`work/dynamicmaps/equator/`. No game-derived data or third-party code/assets were
committed. The two static exit markers reference the existing installed icon.

## Implementation and offline evidence

- `tools/dynamicmaps/export_equator.py` projects extracted colliders through their
  complete parent transforms. It excludes inactive/disabled/trigger geometry and
  follows the runtime/NavMesh border and round-open door/shutter exclusions.
- `ExportMapTriangulation.Run` reads the existing NavMesh bundle using Unity's native
  API, without rebuilding bundles: **10,404 vertices / 4,572 triangles**, bundle SHA-256
  `0c32649698dcf85633818c5a9077f579816a25c3e1db0ed17318d44848f17e1a`.
- Light ground is the simplified projected polygon containing the tested atrium
  anchor `(0,0)`; other projected components are darker. This is a drawing aid,
  not proof of native triangle-graph connectivity or exact walkability.
- Bounds: world X **−29..34**, world Z **−51..57**. SVG viewBox `0 0 63 108`,
  `(u,v)=(x+29,57-z)`; JSON uses X/Z directly, rotation 0, level 0, ID `arena_equator`.
  A full-viewBox background fixes the sprite extent; text uses JSON Labels.
- DynamicMaps supplies the live player arrow and active exits. Package static
  exit markers are hidden in raids to avoid duplication.
- `deploy-to-spt.ps1 -DynamicMapsPackage ...` validates/copies exactly two owned files,
  with dry-run default and `-Confirm` for installation. The standard deploy body
  and existing 72-file ArenaMapPort distribution remain unchanged.

The [reproduction guide](../../tools/dynamicmaps/README.md) contains dependencies,
Unity export, generator/test/deploy commands, color meaning and limitations.

**27** independent synthetic exporter tests passed: transforms, filtering, binary
formats/corruption, clipping, holes, SVG axes, native triangulation and reproducibility.
**59** deployment fixture cases passed, including unchanged standard 72-file mapping.
Parent re-verification passed both suites. Unity compiled and executed the new helper.

Independent review found and fixed two defects before native testing: the floor-piece
area threshold also removed courtyard holes, and coordinate rounding introduced two
self-intersections. Separate hole thresholds and topological quantization now have
regression tests. The final SVG contains **566 valid rings**, **five valid filled
geometries** and **10,270 path points**, all within bounds. The quantization regression
fails under the deliberate negative control with quantization disabled.

Two full exports produced byte-identical JSON, SVG, receipt and preview. All **661**
hashed inputs remained unchanged. Generator SHA-256:
`8d24493db320b9bff7f1e0fa498bc52121e0207e9027a57c5e4955cc08b66b23`.

| File | Bytes | SHA-256 |
|---|---:|---|
| `ArenaMapPort_Equator.jsonc` | 1,688 | `1d5a986565e7286d5bb323c4f1db25d7af1ea21d17ec3b8f4f7a55447a06f84b` |
| `Layers/Ground.svg` | 153,356 | `9045bda346eb97decbac62dac69397be9ef1b0c491a0d58e0a8daf1be99ab4ec` |

The sanctioned script installed these beneath
`BepInEx/plugins/mpstark-dynamicmaps/Maps/ArenaMapPort_Equator/`.
After native testing and EFT shutdown, all **163** baseline files still matched their
pre-task hashes: the 72 MapPort files plus the existing DynamicMaps files and config.
The two installed pack files matched the final generated hashes.

## Native acceptance (CEST)

Fresh EFT process **96ebdca3**, started **11:36:40**. Before/after the raid, all
**46 plugin DLLs and five libraries** matched across VM, host disk and loaded code.
The ordinary PMC test raid used bot amount None.

DynamicMaps logged the custom MapDef load at **11:37:02.389**, its successful name
load at **11:37:02.427**, SVG precache at **11:37:02.473**, and MapScreen selection
at **11:40:50.741**. Inventory → **MAP** selected Equator automatically and rendered
the final ground plan. The first northern view was centered on the player and cropped
the bottom; later atrium views showed the full map.

| Probe | World X/Y/Z | Yaw | Observed arrow |
|---|---|---:|---|
| North start | −11.70 / 0.10 / 48.60 | 180° | Down, northwest start room |
| Atrium | 0 / 0.16 / 0 | 90°, then 0° | Right, then up at the same point |
| Southeast | 15.53 / 0.06 / −19.57 | 270° | Left, southeast structure |
| South approach | 5 / 0.06 / −40 | 180° | Down, four metres north of South Gate |

The map's Player text matched X/Z/height. Movement with the map open updated the
marker without reloading. Both live **Equator North Gate** `(2,46)` and **Equator
South Gate** `(5,−44)` markers matched the authored locations. UiBridge point probes
passed NavMesh/zone/reachability/room checks; exfil-place probes passed NavMesh/zone/room.

Normal South Gate extraction completed in **9.4 s**, then main-menu return in **31 s**.
The PMC selection screen retained **440/440 HP**. Ammo stayed **37 + 1**, with no test
shots fired. EFT was exited normally and the host is free.

DynamicMaps emitted zero Warning-or-worse messages during this process. The whole
captured game log had zero Warning-or-worse messages from first map opening through
menu return; unrelated earlier startup errors are outside that claim. Native rendering
passed without tessellation errors. Actual tessellated vertex count was not instrumented;
the path-point count must not be presented as the native vertex count.

Evidence: `work/dynamicmaps/equator/evidence/native.json`, `dm-north.png`,
`dm-atrium-east.png`, `dm-atrium-north.png`, `dm-east-west.png`, `dm-south.png`.
The useful user preview is `dm-atrium-east.png`. Independent review inspected all five.

## Limits and next step

This is a simplified ground plan, without roofs, upper decoration or extensive
landmark labels. Tall colliders may appear as outlines; NavMesh holes supply additional
obstacle footprints. Simplification and agent clearance prevent exact walkability or
line-of-sight claims. Acceptance covers this ordinary botless Equator raid, not every
ArenaMode respawn/mode, setting combination or future DynamicMaps version.

Bay5 can reuse this approach after feedback, but needs reviewed height bands, its own
main-region anchor and native coordinate checks. No Bay5 map pack was built here.

## Note 2026-09-25

Bay5 has its own DynamicMaps pack, installed by the user (`Maps/ArenaMapPort_Bay5/ArenaMapPort_Bay5.jsonc`,
verified unchanged on 2026-09-23). "No Bay5 map pack was built here" above means "not built by this tooling",
not "missing" — do not list Bay5 DynamicMaps as an open item.
