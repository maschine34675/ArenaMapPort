# Yard source delta: the 0.5.4 spike package versus the Arena 0.6.2 native package

Read-only comparison of the spike package (extracted 2026-09-15 from Arena 0.5.4, no native features; since 2026-11-20 renamed to
`work/extract/yard-spike-20260915`) with `work/extract/yard-native-v1` (extracted 2026-11-20 from Arena 0.6.2.0.48473 with
`--native-features`, plan digest `53c5908f...` after the scene roles were added). Same role as
[autoservice-source-062.md](autoservice-source-062.md) for Chop Shop, [iceberg-source-061.md](iceberg-source-061.md) and
[bowl-source-060.md](bowl-source-060.md). Surveys S0 and S1 of [yard-release.md](yard-release.md) produced the expectations; where the
extraction differs, this file states it. Plan: [PLAN-YARD.md](../PLAN-YARD.md) items 3 to 5.

## Scene content

Arena's scene list moved by one for three scenes between 0.5.4 and 0.6.2: TeamFight 309 -> 310, TeamFight_DesignStuff 316 -> 317 and the
AI scene 319 -> 320; 72, 73, 74 and 162 did not move. The plan already carried the new indices. Every duplicated BuildSettings level file
is byte-identical to its first-listed copy (72/165, 73/168, 74/167/318, 75/164, 153/319, 310/316, 169/312, 281/314) and the later copies
have tiny `sharedassets` files without `.resS`, so the plan's first-listed picks are the full scenes (S1).

| Scene | index | spike objects | 0.6.2 objects | note |
|---|---|---|---|---|
| Arena_Yard | 72 | 28,082 | 28,082 | same objects and order; 4 gain a kinematic or dynamic Rigidbody (extractor growth, below) |
| Arena_Yard_Grass | 162 | 3 | 3 | three empty objects; the grass data is `StreamingAssets/Grass/ArenaGrassPrefabs_Yard.pcl`, not a scene |
| Arena_Yard_DesignStuff | 73 | 67 | 67 | byte-identical scene JSON |
| Arena_Yard_TeamFight | 310 | 11,285 | 11,285 | 9 Rigidbodies added; 3 `hemisphere_shadow` renderers changed material |
| Arena_Yard_TeamFight_DesignStuff | 317 | 4,657 | 4,657 | only mesh ids differ |
| Arena_Yard_Light | 74 | 814 | 814 | byte-identical scene JSON |

Totals: 44,908 objects in both packages (no object, transform, name, layer, parent or activity changed anywhere, unlike Chop Shop's 372 new
border cubes), 6,077 meshes, 652 -> 750 materials and 1,342 -> 1,428 textures (the native decals add 98 materials of the shader
`Decal/DeferredDecalDiffuseNormalsDynamicEditor` and 86 textures; the survey's scratch extract without native features had the spike's 652 and
1,342), 0 errors, 137 -> 137 warnings (97 `MeshFilter N: no mesh assigned, component skipped`, 19 `MeshCollider N: no mesh assigned` and 21 cubemap
strip notices). The package sits at `work/extract/yard-native-v1` (ignored).

What did change (S0, S1):

- **Ids rehash.** Object, mesh, material and texture ids hash the source file and path id; 458 mesh ids, 3 material ids and 74 texture ids differ
  between the two packages, 82 mesh ids and 19 texture ids are shared by different objects (the same id names another mesh or texture), so the
  spike package, its bundles and its receipt are stale and never an input again. Geometry did not change: all 6,056 comparable meshes are
  byte-identical (the 6,057th is a MeshCollider with no mesh); of the 74 new texture ids 61 have a same-name, same-size, byte-identical twin and 13
  are re-imported or retired textures (names such as `Bowl_*` and `City_Brick_rough_big_*`).
- **Eleven material swaps.** Besides mesh ids and Rigidbodies exactly 11 renderers differ: eight `Office_cabin_module_02/04` LOD and decal renderers
  under `SBG_Arena_Yard/SOO` (their material lists now name `Bowl_Painted_Corrugated_Sheets_Grey` and `Bowl_leakage_decal_gray`) and three
  `hemisphere_shadow` renderers under `SBG_Arena_Yard_TeamFight/OO/BACKGROUND/concrete_hemisphere_pained*` (the `SHADOW_TRUE` material id).
- **Thirteen "added" Rigidbodies are the extractor's growth, not new content.** Four in level 72 and nine in level 310 (for example `sliver_1` to
  `sliver_3`, kinematic, mass 0.1; `package_garbage3_Physic`, dynamic, mass 2). The extractor learned to read Rigidbodies in commit `5716665`
  (2026-09-25, "Two-map plan item 5: physics props"), ten days after the spike package of 2026-09-15, which therefore holds none. v1 carries
  13 Rigidbody components in all, pinned by the contract tests.
- **The source NavMesh reference list is empty in both packages** (`navmesh_ref/yard_source_navmesh.json`, `navmeshes: []`): that file is the extractor's
  list; the oracle for the bake is Arena's NavMeshData in `sharedassets281.assets` (plan section 0, item 10).

## Native blocks of v1 (absent from the spike)

All numbers equal the survey expectations of the plan, except the ballistic total (three reviewed owners are left out, see the next section):

| Feature | Total | By scene (level) |
|---|---|---|
| BallisticCollider | 5,144 | 72: 3,668, 73: 10, 310: 1,304, 317: 65, 74: 97, 162: 0 (the survey's 5,147 includes the three physics-less owners) |
| StaticDeferredDecal | 526 | 72: 326, 73: 3, 310: 165, 317: 32, 74: 0 |
| NavMeshModifier | 53 | 72: 19, 310: 14, 317: 20 |
| NavMeshModifierVolume | 90 | 72: 43, 310: 47 |
| CullingLightObject | 96 | 74: 92, 317: 4 |
| VolumetricLight | 44 | 74: 42, 317: 2 |
| LightFlicker | 0 | |
| SmallPhysicsObject | 4 | 72: SoundType 6 (MediumGeneric) x1; 310: SoundType 9 (SmallBox) x2 and 5 (HardPlastic) x1 |
| Rain cover | 9 IndoorTrigger, 0 DryPlane, no WeatherObstacle | Scripts scene level75, 0 baked-vs-live Bounds mismatches |
| Rigidbody | 13 | 72: 4, 310: 9 (see above) |

NavMesh areas Walkable / Not Walkable / Jump / Sitdown / Danger (costs 1, 1, 2, 1, 2). These are the largest native counts of any map so far
(5,144 ballistic colliders, 526 decals, 143 NavMesh components). Ambient audio is not in v1 (`required = ["ballistics"]`, contract `audio: null`;
package v2 adds it, plan item 6). The census is pinned in `YardContractTests` (`tools/tests/test_native_contracts.py`) against the contract
`tools/extract/native_contracts/yard.json`.

## The two reviewed ballistic rules (plan item 3)

A scratch run of the survey stopped twice in `extract_ballistics` and was finished with both cases tolerated; v1 needs neither tolerance. Both rules
are pinned by **scene name and hierarchy path**, never by an id (ids rehash per Arena build), and neither changes what any released map accepts.

1. **Glass over MetalThin on four owners.** The four `Decor_metal_02_A_BALLISTIC_metalthin` colliders (MeshCollider, layer 30, enabled, not
   triggers, hit role 0 / network id 0, surface sound 0) under `DOORS/Construction_Cradle_02/Construction_Cradle_02_A/Construction_Cradle_02 Group/Decor_metal_02_A`,
   `... (1)`, `... (2)` and `... (3)` of Arena_Yard_TeamFight_DesignStuff (level 317, the south gondola) carry the preset **Glass** over the payload
   material **MetalThin**. The names say metal and the payload says metal; the preset is a slip of the authors. They import as authored. The pair
   is **not** in `REVIEWED_PRESET_MATERIAL_PAIRS` (still exactly Chainfence/MetalThin, Gravel/Concrete, Pebbles/Rubber): a new table
   `REVIEWED_OWNER_PAIRS` maps (scene name, owner path) to the extra pair that owner may carry, so the same payload anywhere else still fails with
   "preset/material disagreement". The importer has no pair list.
2. **Three physics-less owners are left out.** `SBG_Arena_Yard/OO/FRONT/Pipe_broken/Pipe_broken_LOD0` (level 72) and the two
   `SBG_Arena_Yard_TeamFight/OO/Props_Flat/Home_theater (2)|(3)/Video_player/video_player_LOD0` (level 310) are render-only LOD0 meshes
   (MeshFilter and MeshRenderer, layer 12, active) whose BallisticCollider (Plastic/Plastic on the video players, MetalThin/MetalThin on the pipe) has
   no collider to label: a ballistic component only labels the hits of a collider on its own object, so it is inert. (Air Pit's four
   `video_player_LOD0` of level 38 are the same objects and `scene_select.ScopedFile` hides them as "ballistic orphans".) The survey recommended
   importing them as authored; that is **not** done, because the package validator requires an imported physical collider under every ballistic
   component ("ballistic owner lacks imported physical collider") and the Unity side has never seen a collider-less BallisticCollider. The rule
   `REVIEWED_PHYSICS_LESS_OWNERS` skips exactly these three (scene and path) in `augment_scene`; any other physics-less owner still fails closed
   ("source/imported owner physics or state differ"). `validate_native_features.py` keeps its own independent copy of the three paths and expects
   them absent from the package (and refuses a source component that is missing for any other reason); a test pins the two lists equal.
   Tests: `test_ballistics.py` (the scoped pair, the three owners, every other path still fails) and `test_native_features_validation.py`.

## Border cubes (plan item 4): fallback taken

Arena's play-space boundary is 118 static `LevelBorder` cubes (BoxCollider + MeshFilter + MeshRenderer, layer 29, solid). Only 11
(TeamFight_LevelBorders) and 3 gate cubes are in the package; **104 sit under the root `Arena_Yard_LevelBorders` of the excluded Scripts scene,
level 75** (heights 0.34 to 8.67 m, 96 of them rotated). The plan's first choice, taking the root through the partial-scene `select` while level 75
keeps its `scripts` role, is **refused by the machinery in three places and is not a small change**:

- `source_plan.validate_plan` raises "an imported scene cannot also be excluded" for a scene in `scenes` and `excluded_scenes`, and `scene_roles` raises
  "scene role ... cannot be assigned to the imported scene" (role `scripts` is what `extract_raincover` and `native_features` read level 75 by);
- a second `scenes` record for index 75 under another name hits "conflicting scene mapping" (one record per index);
- even with those checks relaxed, the package would hold a scene file `Arena_Yard_Scripts.json`, and the Unity importer authors its own Scripts
  scene under exactly that name (`AuthorScriptsScene`, descriptor `scripts_scene_name`): a collision that cannot be tried offline.

So the fallback of the plan applies: `tools/extract/extract_level_borders.py` reads the root straight from the installed Arena (read only) through the plan's
`scripts` role and writes the world-space boxes as the tracked sidecar **`tools/maps/yard.borders.json`** (schema 1; per cube: path, layer, active,
collider enabled/trigger, world `center`, world `size`, rotation quaternion, world AABB; positions only). Result: **104 cubes, all layer 29,
active, enabled, not triggers, no sheared matrix; overall world bounds x -71.686..10.649, y 5.087..20.327, z -0.206..151.174**. A regenerate test
(`tools/tests/test_level_borders.py`) compares the sidecar with the installed Arena when it matches the plan's scene mapping. How the cubes enter the
bake (as `keep_level_borders` sources) and the raid (an `AuthoredBorderRepairs`-style runtime wall) is plan item 10's decision; the package
v1 therefore holds 11 + 3 border cubes, not 118. The plan's `select` stays unchanged.

## Shader census of v1

30 distinct shader names over the 750 materials of the package (`materials/*.json`, field `shader`): the survey's 29 over 652 plus the native decal
shader. Counts: `p0/Reflective/Bumped Specular SMap` 303, `Decal/DeferredDecalDiffuseNormalsDynamicEditor` 98, `p0/Reflective/Bumped Specular` 97,
`Decal/Ultra Deferred Decal Of God 3000` 77, `p0/Cutout/Bumped Diffuse` 35, `p0/Reflective/Bumped Specular SMap_Decal` 31,
`Global Fog/Transparent Reflective Specular` 23, `... SMap Parallax` 11, `Standard` 10, `Nature/SpeedTreeEFT` 9, seven each of
`p0/Reflective/Bumped Emissive Specular SMap`, `... SMap Transparent Cutoff` and `... SMap Baked`, `shadow` 5, `Cloth/ClothShader` 4, `Custom/Vert Paint Shader Gloss Solid` 4,
`Custom/Vert Paint Shader Solid` 3, `Cloth/ClothShader_backface` 3, `p0/Bumped Specular SMap` 3, and eleven names with one or two materials. Four names are new to
the six accepted packages, one material each: `Impostors/Examples/StandardCrossfade`, `Hidden/Amplify Impostors/Octahedron Impostor`,
`Custom/WireNetting` and `Nature/SpeedTree`; the Baked SMap shader has seven materials (Chop Shop one). Pinned in `YardContractTests`. That the
shaders exist in EFT 0.16.9.5 is not re-proven here (survey S3: `Custom/WireNetting` and the octahedron impostor are in EFT's files, `StandardCrossfade`
in none, a stand-in is likely); plan item 13 decides `ShaderFallbackNames` / `ShaderStandinPolicy`.

## Commands

- Housekeeping (item 1, another agent): `work/build/yard` moved to `work/build/yard-spike-20260915`, `work/extract/yard` to `work/extract/yard-spike-20260915`;
  plan roles scripts 75, sound 163, ai 320, culling 153; `tools/build_remaining.ps1` refuses `yard`.
- `python tools/extract/extract_map.py --plan tools/extract/plans/yard.json --native-features --target D:/SPT41/EscapeFromTarkov_Data --output D:/ArenaMapPort/work/extract/yard-native-v1`
  (about 10 minutes): `DONE arena_yard: 44908 objects, 6077 meshes, 750 materials, 1428 textures, 137 warnings`, preceded by
  `arena_yard: 5144 BallisticCollider, 526 StaticDeferredDecal, 53 NavMeshModifier + 90 NavMeshModifierVolume, 96 CullingLightObject, 44 VolumetricLight, 0 LightFlicker, 4 SmallPhysicsObject, required=['ballistics']`.
- `python tools/extract/refresh_native_volumes.py work/extract/yard-native-v1`: `NATIVE_VOLUMES_REFRESHED count=0 ... (no Tex3D textures, manifest untouched)`.
- Native contract `tools/extract/native_contracts/yard.json` (the pins above, `audio: null`; item 6 adds the audio block). A first `validate_extract.py` run ended at
  `Native features: unsupported or missing native map identity` because the contract did not exist yet; with it the native validation passes.
- `python tools/extract/extract_level_borders.py --plan tools/extract/plans/yard.json --root Arena_Yard_LevelBorders --out tools/maps/yard.borders.json`:
  `BORDERS_OK arena_yard 104 boxes layers=[29] bounds=[[-71.6863, 5.087, -0.2058], [10.6488, 20.3267, 151.1742]]`.
- `python tools/validate/validate_extract.py --package work/extract/yard-native-v1 --extractor-timeout 1800`: `OK -- all checks passed` with 2 non-blocking warnings
  (the 137 manifest warnings; 21 meshes no scene references), `distinct shaders: 30`, both reproducibility runs exit 0 and byte-identical (0 diffs),
  package freshness 0 diffs, about 17 minutes. An earlier run (with the contract in place) failed in its second reproducibility run with
  `[Errno 2] No such file or directory: '...\Temp\wp6_validate_extract_repro_...\run2\navmesh_ref\yard_source_navmesh.json'`, a vanished temp directory
  while other agents were working (inferred, not reproduced); the repeat run above is the record.

## Review fixes (2026-11-20, Phase A)

Statements the three reviewers asked the record to carry:

- **Border cubes: 104 in the sidecar, 14 in the package.** The 104 layer-29 cubes of `Arena_Yard_LevelBorders` live in the Scripts scene (75), which stays excluded
  from the package (the plan's `select` route is not used), so they exist only in the tracked sidecar `tools/maps/yard.borders.json` (item 4 fallback). The package
  holds 11 `TeamFight_LevelBorders/Cube (N)` and 3 `DOORS/.../Cube` cubes on layer 29. Nothing reads the sidecar yet: item 10 (bake) and the runtime walls must decide
  how it reaches the bake exclusion and the raid. The package is NOT complete for the border walls. The sidecar holds positions and sizes only (no hash, texture, audio or text).
- **Accepted package: v2 only.** v1 passed `validate_extract.py` (item 5) before item 6 added the audio requirement to the contract; since then
  `validate_native_features.validate_package` on v1 raises `ValueError: Native features: incomplete required feature contract`. v1 is superseded, v2 is the only accepted package.
- **Gondola panels let bullets through.** The four colliders `Decor_metal_02_A[ (1)|(2)|(3)]/Decor_metal_02_A_BALLISTIC_metalthin` carry preset Glass
  (`penetration_chance` 1.0, `penetration_level` 0.0) on a MetalThin-named material: as in Arena, bullets pass through them. Imported as authored; whether to keep Glass or use a MetalThin preset is the user's call.
- **Silent clip wording.** `extract_bay5_audio.export` pops the `silent` flag from the decoded record; it is only used for the silent-clip pin and never reaches
  `native-audio.json` (`amb_yard_cat_06` has no `silent` key; its WAV is 11,008 bytes of zero PCM). The released maps' file shape is unchanged.
- **Key-source row is a 0.6.2 measurement.** The `arena_yard` row (`sharedassets72/74/310` streams, 639,778,432 B, sizes only) was measured on Arena 0.6.2.0.48473; the
  scene-index test skips on other builds and Yard's indices moved by one between 0.5.4 and 0.6.2. Run `verify-keys` on 0.6.1 (GPU-2) before the first stage (plan item 14).
- **Deviations from the plan, stated:** (a) the three physics-less ballistic owners are left OUT of the package (5,144 colliders, not 5,147) instead of tolerated,
  with the independent list `validate_native_features.REVIEWED_PHYSICS_LESS_OWNERS` (now through `is_reviewed_physics_less_owner`, unit-tested); (b) `silent_clips` is a new
  optional contract state; (c) the borders fallback above; (d) Arena's BuildSettings list some Yard scenes twice (found in item 7); (e) item 1's role check ran through
  `SP.load_plan`, not `extract_ai_anchors.py --plan`.
- **Not covered by the new-map checklist** (docstring of `test_new_map_checklist.py`): ai anchor pins, mode-object bound rules, `gate_sound_fit.json`, the spawn_coverage donor rows,
  the selection `markers.json` row, spatial-audio reviewed rows, `UsesExtendedStandins`, descriptor field completeness.
- `.gitattributes` `eol=lf` for `tools/maps/**`: only the working-copy line endings differ (committed bytes are LF); no action.
- `docs/dist-manifest.md` no longer names the deleted `AdditionalMapBuilds.props` as present.
