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
