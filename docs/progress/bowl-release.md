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
