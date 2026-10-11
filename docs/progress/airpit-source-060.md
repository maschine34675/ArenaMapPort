# AirPit source 0.6.0: re-extraction and content delta (PLAN-AIRPIT item 5)

Measured 2026-10-11, 22:11-22:30 local. Offline only: no Unity, no host, no deploy. The source is the live
Arena install (`D:/Battlestate Games/Escape from Tarkov Arena`, **0.6.0.47752**, Unity 6000.3.6f1, read-only),
the SPT target data is `D:/SPT41/EscapeFromTarkov_Data` (read-only, ballistic preset contract). The new input
is `work/extract/airpit-native-v5` (ignored, BSG-derived). The spike package `work/extract/airpit` (Arena
0.5.4, extracted 2026-09-15, no native features) was **read only** for the comparison and is left untouched;
it is superseded and no longer referenced by any tool default. Command log and test counts:
[airpit-release.md](airpit-release.md), "Item 5".

## The accepted input

```
python tools/extract/extract_map.py --plan tools/extract/plans/airpit.json --native-features \
  --target D:/SPT41/EscapeFromTarkov_Data --output work/extract/airpit-native-v5
python tools/extract/refresh_native_volumes.py work/extract/airpit-native-v5
python tools/validate/validate_extract.py --package work/extract/airpit-native-v5 --skip-reproducibility
python tools/validate/validate_extract.py --package work/extract/airpit-native-v5 --extractor-timeout 900
```

Extraction exit 0 in 4 min 24 s: `DONE arena_airpit: 40719 objects, 2487 meshes, 435 materials, 805 textures,
240 warnings`, 0 errors. `source.extracted_utc` `2026-10-11T20:16:21Z`, `source_plan_sha256` `398f5d70...`
(the item 1 plan with scene roles). The full validation, including the two fresh reproducibility reruns and the
freshness check against the live source, passed (exit 0, 0 differences).

| Item | Count |
|---|---|
| Objects per scene (ShootOut / Light / DesignStuff / TeamFight / TeamFight_DesignStuff) | 20,252 / 3,833 / 15,349 / 638 / 647 = 40,719 |
| Meshes / materials / textures | 2,487 / 435 / 805 (782 Tex2D + 23 Cube, 0 Tex3D) |
| Disk | 1,389,639,829 B (old spike package 1,316,270,985 B, +73.4 MB) |
| BallisticCollider | 3,548 = 2,856 / 512 / 28 / 94 / 58 per scene, in plan order |
| StaticDeferredDecal | 481 = 423 / 0 / 4 / 0 / 54 |
| NavMeshModifier / NavMeshModifierVolume | 20 (TeamFight_DesignStuff) / 12 (ShootOut) |
| CullingLightObject / VolumetricLight / LightFlicker | 533 / 130 / 0 |
| SmallPhysicsObject | 0 |
| Rain cover | 2 IndoorTrigger, 0 DryPlane, no WeatherObstacle, source `Scripts (level36)`, 1 baked-vs-live Bounds mismatch (`Bus_IndoorTrigger`: 155.2 m apart, 125.76 m on the largest single axis, which is the figure the extractor log printed; baked centre used) |
| Manifest | `native_features.required == ["ballistics"]`, no `native_audio` (item 13), `source.target_data_root` recorded |

The numbers are exactly the pins of the AirPit native contract (item 3) and the item 2 dry run. A component
census of the written scene JSON gives the same per-scene totals. `refresh_native_volumes.py` found 0 Texture3D
(23 Cube + 782 Tex2D) and so changed no texture or volume. It also re-serialised `manifest.json` with one extra
trailing CRLF (invisible to the validators, which compare manifests as JSON). The review follow-up made the script
a true no-op for a package without Tex3D and restored the package manifest: the accepted package is now byte-identical
(3,735 files, 0 differences) to a fresh extraction run with the same `--fixed-timestamp` (see the log, "Item 5 review
follow-up").

## Content delta, Arena 0.5.4 -> 0.6.0

Method: ids in the package are hashes of source identity (`<file>:<path_id>:<type>`), so a changed source layout
changes ids without changing content. The comparison therefore matches by content, not by id: meshes by SHA-256
of the `.mesh.bin` (plus source object name and vertex count to resolve duplicate bodies), textures by SHA-256 of
the PNG, materials by their JSON after the texture remap, then every object of every scene is compared field by
field after remapping the old object's ids. Native components (present only in the new package) are excluded from
the comparison. Scripts are kept in ignored `work/airpit-parity/item5_*.py` with their JSON output.

**Result in one line: the geometry, hierarchy, transforms, layers, activity, components and material assignments
are identical; only the ids of 55 meshes, 31 textures and 3 materials changed, and 214 objects of one scene
reference them.**

| Scope | Result |
|---|---|
| Scenes | 4 of 5 are identical object for object (Light 3,833, DesignStuff 15,349, TeamFight 638, TeamFight_DesignStuff 647; same ids, same order). `Arena_AirPit_ShootOut` has the same 20,252 objects in the same order; **214 of them differ**, every one only by a re-keyed mesh id (189) or a re-keyed mesh id plus a re-keyed material id (25). After remapping the ids, **0 objects differ** in name, parent, transform, active flag, layer, tag or any component field. The 214 are 149 render objects (MeshFilter) and 65 collider objects (MeshCollider). |
| Which props | Fence_Armored_1 (7 instances, 77 objects), tarpaulin_support (6 instances, 48), Fence_Armored_2 (3 instances, 33), tarpaulin_wall_01 (3 instances, 30), Fence_ema_01_white (3 instances, 15), Stairs_ramp (5), Sandbag_03_angle (4), Box016 (2). All in the ShootOut scene, mostly under `SBG_Arena_AirPit_ShootOut/SOO`. |
| Meshes | 2,487 before and after. **53 ids are absent from the new package** (and 53 new ids appear). Two more ids survive but now hold different geometry: `759ce849e662f398` (was `Fence_Armored_1_COLLIDER`, now `fence_ema_01_BALLISTIC_fabric`) and `cfc6ec710a837db0` (was `Fence_Armored_1_BALLISTIC_metalthick`, now `fence_ema_01_LOD0`). So **55 mesh bodies sit under a different id**, all 55 matched uniquely by content; the set of mesh bodies is byte-identical (old and new hash sets equal, 2,432 meshes keep id and bytes). The 55 belong to the families Fence_Armored_1 (11), Fence_Armored_2 (11), tarpaulin_wall_01 (10), tarpaulin_support (8), fence_ema_01 (5), Stairs_ramp (5), Sandbag_03_angle (4), Box016 (1). |
| Textures | 762 -> 805. 731 ids exist in both with **byte-identical PNGs** (stricter than pixel-identical). 31 ids were re-keyed; each new PNG is byte-identical to its old one (Kamaz/fence/paint/plastic textures, 4 cubemaps among them). The remaining **+43 are new** native decal textures (all Tex2D). |
| Materials | 380 -> 435. 377 ids are shared and identical after the texture remap; 3 were re-keyed with identical content (`Bowl_Metal_Threadplate_rust`, `fence_ema_01_white`, `Factory_painted_metal_VP_Grey`); **+55 are new**: `Decal/DeferredDecalDiffuseNormalsDynamicEditor` materials of the original StaticDeferredDecal components (item 2 native feature, absent from the plain package). 155 shared materials reference a re-keyed texture and still match. |
| Manifest | `warnings` 240 = 240: the same 217 `MeshFilter ...: no mesh assigned` entries (same order) and the same 23 cubemap notes (four carry a re-keyed texture id). `errors` empty. `anchors_reference` (20 SpawnPointMarkers) and `navmesh_ref/airpit_source_navmesh.json` are identical. `scene_registry` and the BuildSettings indices 324/325/40/29/28 are unchanged. New keys: `native_ballistics`, `native_features`, `source.target_data_root`; plan digest `0a6b5a96...` -> `398f5d70...` (the roles added in item 1). |
| Validator pins | 21 meshes are unreferenced by any scene in both packages (same bodies by content) and the two `barrel_fire` LODGroup notes repeat: non-blocking warnings, not new in 0.6.0. |

Reading of the cause: Arena repacked its assets in 0.6.0 and reused or shifted serialized object ids (the same
mechanism that `arena-update-20261003.md` records for `resources.assets`). Geometry did not change; the content
hashes show a pure re-keying of 55 prop meshes. The earlier statement "53 of 2,487 mesh ids re-keyed" (PLAN-AIRPIT
section 0) is the count of ids that vanished; the full permutation is 55 bodies because two ids were swapped into
use by other bodies.

**Consequences.**

- Nothing measured offline on the 0.5.4 package changes. The item 7 NavMesh oracle, the collider model and the
  item 4/6 positions use hierarchy, transforms and mesh bodies, all of which are identical. The only thing that
  must not be mixed is a list of mesh ids: id `759ce849e662f398` or `cfc6ec710a837db0` taken from the old package
  names different geometry in the new one.
- The old bundles in `work/build/airpit` (scenes 664,670,450 B, built from the 0.5.4 package) are stale and are
  overwritten by item 12, never patched.
- `tools/maps/airpit.modes.json` was regenerated from the new package (`python tools/extract/extract_mode_objects.py
  --package work/extract/airpit-native-v5`). Only `source_package.name` (`airpit` -> `airpit-native-v5`) and
  `source_package.extracted_utc` changed; every position, bound, marker and the capture point are identical.

## The `level40` count: 15,357 versus 15,349

Resolved; it is not a loss of content. The raw `Arena_Airpit_DesignStuff` (`level40`) holds 15,357 GameObjects:
15,349 with a `Transform` and **8 with a `RectTransform`**. The extractor walks `Transform` components only
(`extract_equator.py:228`, `elif tn == "Transform"`, shared by `extract_map.py`), so the 8 UI GameObjects are not
emitted; the 15,349 extracted ids equal the 15,349 Transform GameObjects exactly, and the 8 missing GameObjects
equal the 8 RectTransform GameObjects exactly.

The 8 are `ArenaMonitorHolder` objects (path ids 4451-4457 and 8591), one under each of eight
`Arena_Home_theater` props (`Arena_Home_theater` and its copies (1) to (5), (9) and (11), all in `level40`). Each is an active leaf with `Canvas` (RenderMode 2, world space, sorting order 0), `CanvasRenderer`,
`CanvasScaler` and the Arena script `Game.Modes.Data.Monitors.ArenaMonitorHolder`; the rect is 1930 x 1163 at
scale 0.00054 (about 1.04 x 0.63 m, a mode score monitor). None has children. The old spike package also has 15,349
objects, and 0.5.4 and 0.6.0 agree, so this skip is common to both packages, to the Equator/Bay5 extractors, and
to the importer (there is no Canvas builder). Nothing renders in the ported scene for them. The other four scenes
have no RectTransform (raw counts equal extracted counts: 20,252 / 3,833 / 638 / 647). If a mode monitor is ever
wanted it needs a deliberate UI port, not an extraction fix.

## Verification

| Check | Result |
|---|---|
| `validate_extract.py --package work/extract/airpit-native-v5 --skip-reproducibility` | exit 0, 72 s, no FAIL, 4 non-blocking warnings; includes "optional native feature source/clip/reference contract validates" (live 0.6.0 recheck of the 3,548 ballistic components by scene index) |
| Same, full (`--extractor-timeout 900`) | exit 0, about 10 min; both reruns exit 0, "byte-identical package" 0 diffs, package-freshness check 0 diffs |
| `python -m unittest discover -s tools/tests -p "test_*.py"` | 926 tests, OK, 44 skipped (was 46 skipped: the two tests gated on this package now run and pass); **929 tests, OK, 44 skipped** after the review follow-up (+3 `test_refresh_native_volumes.py`) |
| Accepted packages, `validate_extract.py --package work/extract/{equator,bay5}-native-v5 --skip-reproducibility` | unchanged from before this item: Equator 1 FAIL, Bay5 2 FAIL (the two documented 0.6.0 live-source drifts: preset offset 5708 != 5716, Bay5 disabled MeshRenderer 302 vs 305); `work/airpit-parity/parity_gate.py check` reports both UNCHANGED against the HEAD baseline |
| `work/extract/airpit` | not modified (manifest 2026-09-15; opened read-only by the comparison scripts) |
| `git status` | `tools/maps/airpit.modes.json` (regenerated) and the two progress documents; the review follow-up adds comment/log-text edits in `extract_map.py`, `extract_mode_objects.py`, `extract_raincover.py`, the `refresh_native_volumes.py` early return, `tests/test_airpit_spawns.py`, `tests/test_extractor_plan_roles.py` and the new `tests/test_refresh_native_volumes.py`; `work/` stays ignored |
