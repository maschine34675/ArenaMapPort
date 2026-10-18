# Bowl release log (fourth map, branch `feature/bowl`)

The plan is [docs/PLAN-BOWL.md](../PLAN-BOWL.md); this file is the dated record of what landed, with command output and
host evidence. Appended, never rewritten.

## 2026-10-18 - start: plan, scene roles, first native extraction

- Surveys (read-only, four agents + synthesis): Bowl is the September spike (0.5.4 package `work/extract/bowl`, 80,163
  objects, no native features; spike bundles now under `work/build/bowl-spike-20260915/`), never raided on a host.
  Arena 0.6.0 content matches the spike except 15 added and 2 moved layer-29 border cubes in `Arena_Bowl_DesignStuff`.
  Bowl-specific: two spawn pockets east (x 46..56, z -11..-3) and west (x -58..-49, z -69..-60), each behind a
  `GateEntity` root (`DOORS_SPAWN_A`: Bowl_Gates_Respawn_01 + BTR_82_closed; `DOORS_SPAWN_B`: Gates_Army_01 +
  Bowl_Respawn_Ambulance_Animated), capture point centroid (-1.76, -0.05, -33.92), 312 + 42 layer-29 LevelBorder cubes
  as the only play-area bound (the culling polygon is a coarse placeholder), Arena's NavMeshData rotated 90 degrees
  about Y (mask bits 9/11/18/30), one shader missing in EFT (`p0/Reflective/Bumped Specular_TwoSided`, Tarp_Tunnel),
  GPUInstancer grass scene, no Directional light in the imported scenes, no Arena loading screen (four parallax images
  exist), official description key `62eba95b7ba21c1d534d305a Description`, banner `Matchmaker_Banner_TheBowl`.
- Plan roles (`plans/bowl.json`: scripts 44, sound 111, culling 96, ai 128); the roleless-plan fixtures of
  `test_extractor_plan_roles.py` and `test_ai_anchors.py` now use `prison.json`. `extract_map.py --census-only` on the
  live install: Arena_Bowl 39,911 GameObjects, Building 12,380, Grass 3, Light 1,738, DesignStuff 15,664 (10 of them
  RectTransform UI the extractor skips), TeamFight 9,814, TeamFight_DesignStuff 684.
- First `--native-features` extraction (10:38) failed closed: `Ballistics: preset/material disagreement`. Diagnosis
  (scratch `bowl_ballistics_diag.py` over all seven levels, 9,215 BallisticColliders): exactly two components, both
  `Guardian_Tower_Fance_BALLISTIC_MetalThin` in Arena_Bowl (level41), carry the named preset `Chainfence` over the
  material `MetalThin`; every other rule (hit/net role, surface sound, material mapping) holds for all 9,215.
  Decision: import the reviewed pair as authored (EFT keeps TypeOfMaterial and Preset as two fields, Arena ran it that
  way, and `validate_native_features.ballistic_expected` derives the same two values independently):
  `extract_ballistics.REVIEWED_PRESET_MATERIAL_PAIRS = {("Chainfence", "MetalThin")}`; any other pair still fails.
  Test `test_reviewed_preset_material_pair_imports_as_authored_and_others_still_fail` (21 ballistics tests OK). The
  released packages are untouched (the rule only widens for the listed pair).

## 2026-10-18 - package v1 validated, native contract, AI anchors, audio census

- Second extraction run (08:47 UTC, about 9 min): `work/extract/bowl-native-v1`, 0 errors, 239 warnings (the spike's
  "MeshFilter: no mesh assigned" family), 3,721 meshes, 467 materials, 905 textures; native blocks 9,215
  BallisticCollider (41: 5,048, 46: 2,375, 45: 269, 43: 29, 47: 1,421, 48: 73), 766 StaticDeferredDecal, 39
  NavMeshModifier + 76 NavMeshModifierVolume, 106 CullingLightObject, 4 VolumetricLight, 8 LightFlicker, 341
  SmallPhysicsObject, rain cover 10 IndoorTrigger (Scripts level44), `required = ["ballistics"]`.
  `refresh_native_volumes.py`: no Tex3D, manifest untouched. Delta note: [bowl-source-060.md](bowl-source-060.md).
- `tools/extract/native_contracts/bowl.json` (ballistics per scene, feature pins, `audio: null`); the parked-contract
  fixtures of `test_native_contracts.py`, `test_native_features_validation.py`, `test_airpit_audio.py` and
  `test_equator_audio.py` use `arena_prison` now (91 / 50 / 20 / 22 tests OK, orchestration 54 OK).
- `validate_extract.py --package work/extract/bowl-native-v1`: `OK -- all checks passed` with 18 non-blocking warnings
  (16 `Emergency_Signal_Flare (N)` LODGroup slots whose renderer is a particle system, 21 unreferenced mesh ids, the
  manifest warnings); both reproducibility runs exit 0 and the freshness check reports 0 diffs.
- `extract_ai_anchors.py --plan tools/extract/plans/bowl.json` -> `tools/maps/bowl.ai.json`: 19 core points, 7 hubs,
  79 sub points, all 86 PatrolPoints consumed.
- Ambient audio census of level111 with the exporter's own parsers (scratch `bowl_audio_census.py`): 33
  AmbientSoundPlayer, 2 AmbientSoundPlayerGroup (`CommonAmbientInGroup` -> mixer `CommonAmbInEffects`, 15 players;
  `CommonAmbientOutGroup` -> `CommonAmbOutEffects`, 18), 11 banks, 61 clips, all players grouped, none disabled, four
  inactive (`cracles (2)`, `water_drops`, `water_drops (1)`, `water_drops (4)`), no loop players. These become the
  contract's audio block for package v2.

## 2026-10-18 - package v2 (ambient audio): the same-named indoor_wind players

- The contract's audio block (pins above) and `AUDIO_EXPORTERS["arena_bowl"]` were added; the first v2 extraction
  (10:56) failed closed in the exporter: `ambient_audio export failed: inconsistent shared audio hierarchy`. Cause
  (scratch census of level111): two AmbientSoundPlayer leaves are both named `indoor_wind` under
  `AmbientAudioSystem/AmbientPlayersGroup/CommonAmbientInGroup` (source ids 560 and 597, 7 m and 60 m apart), so
  they shared one hierarchy path, which is the object key of the Unity importer (`objects[path]`) and of the client.
  Equator, Bay5 and AirPit have no such duplicates.
- Fix (`9c6bc17`): `extract_bay5_audio.disambiguate_sibling_paths` renames the second and later duplicates, in source
  id order, with the leaf suffix `~2`, `~3` ... and keeps the authored leaf in `sourceLeafName`;
  `validate_native_features.validate_audio_source` applies the same rule before comparing names with the source.
  Tests in `test_audio_sibling_names.py`; the AirPit export re-run into a scratch directory is byte-identical to the
  accepted v9 graph (`native-audio.json` sha `fa50a114...`).
- Side rule for two pockets (`ff23a28`): `extract_mode_objects.py` finds the pockets by single linkage; Bowl's
  `north` is the east pocket (x 46..56), `south` the west pocket (x -58..-49), 10 markers each, with a `side_rule`
  block in the sidecar; the released sidecars stay byte-identical (verified against their packages).
- `KeepLevelBorders` (`6873576`): Bowl keeps its 354 layer-29 LevelBorder cubes (descriptor + sidecar
  `keep_level_borders: true`); the client's `RemoveTeamFightBorders` key defaults to false for Bowl and the bake treats
  layer 29 as physical when the map's `navmesh_bake_layers` lists it. 875 .NET tests OK.

## 2026-10-18 - package v2 accepted input, oracle, bake-input analysis, sidecars and descriptor (items 4-7)

- Third v2 run (09:32 UTC) with both fixes: `required = ["ballistics", "ambient_audio"]`, 33 players (two `indoor_wind`
  leaves, the second now `indoor_wind~2`), 11 banks, 61 clips, `metal_rigging` pinned in `states.forced_mixer_players`
  and routed like its group. `validate_extract.py` on v2 running (result below).
- NavMesh oracle (`4cea40b`): Arena's Bowl NavMeshData is placed by a 90 degree yaw; in the world frame its main component
  spans x -63.84..59.64, y 0.04..9.25, z -79.45..8.75 (4,987 m2, 1,477 polygons, 11 components), 19/19 core points and
  20/20 spawn markers lie on it (3/19 and 3/20 in the raw frame). `navmesh_diff.py --map bowl` against the v1 colliders:
  123,336 of 124,540 Arena samples passable, 63 without a port floor. AirPit's report is byte-identical.
- Bake-input analysis (read-only agent, scratch `bowl-bake/`): the play space is bounded by architecture plus the 354
  layer-29 cubes, which seal two side zones (about 475 + 453 m2) next to the pockets that Arena's own mesh never covered;
  with the cubes kept the walk model overlaps Arena's mesh at 98 % (83 % without), route lengths unchanged (80 / 78 m
  pocket to capture area, 149 m pocket to pocket). Each pocket has two independent exits (east: Bowl_Gates_Respawn_01 and
  BTR_82_closed; west: Gates_Army_01 and the ambulance group), so all four direct children of the two GateEntity roots
  open together; no name contains "frame", no layer-0 blockout exists, the two SPAWN_BOUND_FIGHTER boxes are the only
  layer-0 solids. The 165 x 144 m under-floor slab `Arena_Bowl_LevelBorders/Cube` (y -1..0) must be excluded from the
  bake or it becomes a walkable plane at floor level. Arena's mask is 9/11/18/30; y max 10 keeps the guard-tower tops.
  Known cost of baking the cubes as solids: about 608 m2 of unreachable cube-top islands (accepted; bots spawn from fixed
  core points, Arena's own mesh carries 133 m2 of islands too).
- Decided and written (sidecar `tools/maps/bowl.json` + C# descriptor in one commit): `opened_barriers` and `start_gates`
  = `DOORS_SPAWN_A`, `DOORS_SPAWN_B` (no held gates), `bake_bounds` [-66, 62, -2, 10, -82, 11], `navmesh_bake_layers`
  [9, 11, 18, 29, 30], `navmesh_bake_exclusions` [`Arena_Bowl_LevelBorders/Cube`], `keep_level_borders` true,
  `shader_fallback_names` [`p0/Reflective/Bumped Specular_TwoSided`], `uses_server_time` true, identity text
  "Arena: Bowl" with an own-words description.
- `bowl.exits.json` / descriptor Exits: `Bowl BTR Gate` (51.0, 0.1, -31.0) south of the BTR opening and `Bowl Army Gate`
  (-51.0, 0.1, -49.0) north of Gates_Army; both on Arena's main component with 5.1 m border clearance, 20.9 m / 12.3 m
  from the nearest pocket marker (oracle survey of a 1 m grid outside each gate). `bowl.bots.json`: four player spawns
  on original markers ((5) and (8) east, (1) and (12) west), 17 bot spawns on Arena's core points (core 7 inside the west
  pocket and core 17 on the east gate line skipped), waves and PMC boss waves from AirPit. `bowl.modes.json` from v2
  (north = east pocket, south = west pocket, `side_rule` block). `make_base_json.py --map tools/maps/bowl.json`: Name
  "Arena: Bowl", BotMax 8, 21 spawn points, 2 exits, 5 waves, 4 boss waves.
- Open for the host: the capture-point centroid (-1.76, -33.92) lies under the Mi-26 belly (not on the mesh; the nearest
  walkable cell is the helicopter top at y 1.6), the east guard-tower stairs, vault limits above 1.2 m, and
  `BoothDoorRepairs.ReportRays`, which measures from the gate root at the origin and will print no verdict for Bowl.

## 2026-10-18 - U1 build attempt 1: the backface cloth normal map

- `build_all.ps1 -FullMap -BakeNavMesh` from v2 (11:42) failed closed before the Scripts-scene authoring:
  `Normal texture 18ed78eea2b2b8a7 rejected: unverified normal encoding: format=DXT1, colorSpace=sRGB, R=49..181,
  minA=255, maxGreenBlueDifference=178`. The texture is `City_Tarp_01_N` (1024 x 1024), bound by the material
  `City_Antisniper_tarps` (`Cloth/ClothShader_backface`, slot `_NormalMap2`) on 80 renderers (Cloth_fabric_01,
  Sniper_defence_tarps_02..04 LODs). Pixel census: B 231..255, R 49..181, G 69..182, alpha 255 everywhere, RGB unit
  length 1.006 +/- 0.013 over every pixel, z reconstructed from xy within 0.010: a plain RGB tangent-space normal map,
  the same texture AirPit binds on `Cloth/ClothShader` `_NormalMap1`, where the policy's `RgbNormal` plan already
  accepts it. The policy counted only `Cloth/ClothShader` as an RGB reader, so the backface variant fell through.
- Fix: `NormalTextureImportPolicy` treats both cloth variants alike (`IsClothShader`: `_NormalMap1`/`_NormalMap2` read
  RGB, `_NormalsMask` is the mask); the DXT1/DXT5 plans and every other map are unchanged (Equator, Bay5 and AirPit
  bind no normal texture on the backface shader). Test case added to `tools/tests/test_normal_texture_import.ps1`.
  U1 attempt 2 started 11:55.
