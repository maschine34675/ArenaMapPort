# Iceberg release log (fifth map, branch `feature/iceberg`)

The plan is [docs/PLAN-ICEBERG.md](../PLAN-ICEBERG.md); this file is the dated record of what landed, with command output and
host evidence. Appended, never rewritten.

## 2026-10-30 - start: request, surveys, plan

- Request (user, German): "dann würde ich gerne Iceberg als nächste map angehen", right after the 0.4.0 publication.
- Four read-only surveys (workflow `wf_f2b7eb1e-4cd`, about 47 min) against the live Arena 0.6.1.0.48206 and the repo, plus a
  synthesis and a critic pass. The findings that shaped the plan:
  - **Preset**: `Arena_Sanatorium.ScenesPreset`, ServerName `Arena_Iceberg`, base scenes Scripts/base/Grass/Light/DesignStuff/
    Sound, modes BlastGang (372-374, no AI scene), CheckPoint (375-378) and OverRun (reuses CheckPoint's preset). No TeamFight.
  - **CheckPoint (level376)**: 70 `SpawnPointMarker` (40 "Bear house", 30 "Usec house"; identical serialized `SpawnPoint`,
    team only by name and bound), 12 `ArenaSpawnBound`. Initial pockets: Bear 5-14 in `SPAWN_BOUND_FIGHTER` (x 89.3..97.9,
    z -96.1..-83.7, y 0.73-1.89), Usec 5-14 in `SPAWN_BOUND_DEFENCE` (x -44.5..-37.0, z -95.0..-79.9, y 3.25). Stage markers
    linked to capture points A (16.77, 3.90, -91.39), B (10.61, 2.69, -77.73), C (21.91, 2.85, -47.30). Gates under `DOORS`:
    `GATES_A` (2 x `sanatorium_resp_gate` at x 82.38) and `GATES_B` (3 x `sanatorium_resp_gate` at x -36.04 plus the rotating
    `Sanatorium_CheckPoint_Gate`); 11 DotAnimators, linear drives 2.0-5.0 s. 17 `ArenaMinefield`, 3 `ArenaPresetRefillContainer`,
    2 spawn-protection triggers, 6 `ArenaMonitorHolder` (type words 0 x4, 13 x2). 365 layer-29 border cubes plus one solid
    `KillTrigger` at y about -11..-6.
  - **AI scene (level377)**: NavMeshData `sharedassets377.assets` path 2, identity placement, agent radius 0.30, height 1.7,
    slope 48, climb 0.38; 2,473 polygons in 93 components, main component 4,788 m2 (x -58.7..108.7, y -1.8..6.15,
    z -126.5..-34.8) holding the 20 initial markers, the capture points and the 11 `AICorePoint`; surface mask 0x40042A00
    (layers 9/11/13/18/30). The 4-hub PatrolWay and 10 Savage/Bot markers lie off the mesh, 30 m or more outside the play area.
  - **Environment**: no Directional light, sky or lightmap in the imported scenes (Arena's dusk sun is in Scripts level255);
    not a winter map; 815 point and 464 spot lights; 1,145 `TreeWind`, 272 impostor data objects, GPUInstancer grass; two
    `WaterContainer` (`WaterForSSrv3` pool, `Sea`) and seven `FX/SimpleWater4` renderers.
  - **Audio**: level259 holds 104 players (52 ambient, 30 loop, 22 season), four groups (`CommonAmbientInGroup` 46,
    `LampsAmbientInGroup` 20, `CommonAmbientOutGroup` 37, `AmbientSplineEmitterWaterGroup` 1), 28 banks, 237 clips; 21 loop
    players have `minDistance` 0, which the exporter refuses. Spatial bake `arena_sanatorium_sound.audiobakedata`: 46 rooms,
    89 portals, 120 trigger areas, 17,751 routes; 10 trigger areas have a negative local scale and 533 routes fail the strict
    route check. Six `EntitySoundPlayer` gate emitters. No spectator crowd anywhere (12 `FansAmbient` sound entries only).
  - **Identity**: banner `Matchmaker_Banners_Iceberg`, loading screen `AllScreens_Loadingscreen-Iceberg`; no official name or
    description in the en/ge/ru locales.
  - **Shaders**: `Nature/SpeedTree8` (16 renderers, Streets' shared assets) and `MK4/Rock Simple` (1 material) are not in EFT's
    global shader bundle and have no host evidence yet; Cloth, ANGRYMESH and the fog sheet resolve from Customs (AirPit).
  - **Pipeline**: `extract_mode_objects.py` fails on the unequal sets ("all 70 spawn markers lie on one side of z == 0"),
    `extract_door_poses` needs depth 3 with `at`, `extract_gate_sounds` stops on `Sliding_door_2` ("join is not clear"),
    `extract_score_monitors` stops on type word 13, `extract_spatial_audio` and `extract_multiflare` stop on negative scales.
    `ArenaMapDescriptions.Load` drops the whole official pack when its keys differ from `All`.
  - **Arena builds**: MASCHINE11 runs 0.6.2.0.48473 now; Iceberg's four key-source files match the VM's 0.6.1 by size and sampled
    content.
- Survey reports, the plan draft and the critique are kept in the session scratchpad; the plan carries the decisions.

## 2026-10-30 - package v1: scene roles, two reviewed ballistic authorings, native contract

- Plan roles (`817eed0`): scripts 255, sound 259, ai 377, culling 378 on the excluded scenes of `plans/iceberg.json`.
- First `--native-features` extraction (14:05) failed closed after 13 s: `Ballistics: preset/material disagreement`. Diagnosis
  (scratch `iceberg_ballistics_diag.py`, `ballistic_owners.py` over levels 256/258/257/375/376 and BlastGang 372/373 for
  reference): 8 colliders `SBG_Arena_Sanatorium_Main/OO/OUTDOOR/bag_cement_block .. (7)/concrete` carry the preset Gravel over
  the material Concrete, and 11 Glass colliders carry hit role 1 with network id 0: the head/tail light ballistics of
  `PREFAB_VARIANTS/OUTDOOR/Ford_Focus_closed_off` and `Kamaz_4310_police_off (3)` (level256) and three
  `Lamp_Industrial_01_BALLISTIC_glass` (level258). EFT's `EHitType` 1 is `Lamp`; `Turnable.RegisterForNetwork` sets it at
  runtime for a `LampController`, and `ShotExtension.GetShotInfo` only copies it into the network shot packet. Decision: import
  both as authored (Gravel/Concrete joins `REVIEWED_PRESET_MATERIAL_PAIRS`; the new `reviewed_hit_role` accepts Lamp + id 0 on
  Glass, imported as HitType Default and NetId 0 like the stale window ids). Extractor and `BallisticColliderImporter` share
  the rule; `test_lamp_glass_role_imports_as_plain_glass_and_other_lamp_roles_fail` (22 ballistics tests OK).
- Second extraction (14:24-14:30, 6 min 40 s): `work/extract/iceberg-native-v1`, 49,861 objects, 5,370 meshes, 823 materials,
  1,407 textures, 59 warnings, 0 errors; native blocks as in [iceberg-source-061.md](iceberg-source-061.md).
  `refresh_native_volumes.py`: no Tex3D, manifest untouched.
- Native contract `tools/extract/native_contracts/iceberg.json` (`0f72aed`, the contract-set test in `12e1151`).
- `validate_extract.py --package work/extract/iceberg-native-v1 --extractor-timeout 1800`: `OK -- all checks passed` with 32
  non-blocking warnings (the manifest warnings, 31 LOD slots whose renderer is a particle system: `Spark_Drops_FX`, candles,
  `RainDrops_Fall_FX`); both reproducibility runs exit 0; 16 min 20 s.
- Python suite after the contract: 2,444 tests, 2 errors that are the Bash tool's console encoding (the validator prints an
  em dash in cp1252 into a UTF-8 pipe); both pass from PowerShell. Run the suite from PowerShell.

## 2026-10-30 - key-source row, door poses, gate sounds, score monitors (PLAN-ICEBERG items 6 and 8)

Workstream branch `worktree-wf_451c0467-c1b-4` (from `feature/iceberg` d366c5c, one commit per step plus this record). Read-only against the VM's Arena 0.6.1.0.48206;
no key created, nothing staged, no Unity run. Ignored output: `work/extract/gate-sounds/arena_iceberg` (3 WAVs and a manifest, 2,392,812 B of WAV).

### Item 6 - stage-2 key-source row (`0e7a94a`)

- `tools/bundle/key-sources.json` `arena_iceberg`, scenes in ascending index order: 256 `sharedassets256.assets.resS` (467,380,832 B), 257 none,
  258 `.resS` (91,702,864), 260 none, 375 `.resS` (79,072), 376 `.resS` (1,307,264) = 4 files, 560,470,032 B; no `.resource` file exists for any of the six.
  Live Arena: `ls` of `level/sharedassets 256 257 258 260 375 376` finds exactly these four streams.
- `test_bundle_crypt_tool.KeySourcesTests`: `LISTED` gets `(4, 560_470_032)`; the "exactly the released maps" test now allows `PREPARED_MAP_KEYS = ("arena_iceberg",)`
  (the lead removes it at the promotion); the plan test covers `plans/iceberg.json`; the manifest test `iceberg-native-v1` (checked by hand here, the
  worktree has no `work/`: registry equals the table). `BundleCryptoTests.CryptoRealDataTests` gets `("arena_iceberg", 4, 560_470_032)`:
  `arena_iceberg: 4 files, 3,224,800 of 560,470,032 bytes sampled, key derived in 13 ms (CNG), Arena 0.6.1.0.48206`.
- Arena 0.6.2.0.48473 on MASCHINE11: the same four sizes and the same sampled content (the tool's sampling rule, all five maps identical); the door, monitor and
  gate-sound extractions and the three clip decodes give byte-identical results there.

### Item 8 - door poses (`7dbd596`)

- `DOT_SOURCES["arena_iceberg"]`: scene `Arena_Sanatorium_CheckPoint_DesignStuff` (level376), scan root `DOORS`, depth 3, `at` true.
  `tools/maps/iceberg.doors.json`: 11 DotAnimators = 11 rows on 3 barrier paths, linear, all unscaled seconds from Arena:

  | barrier | instances (`at`) | part | seconds | motion |
  |---|---|---|---|---|
  | `DOORS/GATES_A/sanatorium_resp_gate` | 2 (82.382, 2.176, -93.689 / -86.125) | `..._A` / `..._B` | 4.0 / 5.0 | local z 0.82 -> 3.568 / 2.192 -> 3.568 |
  | `DOORS/GATES_B/sanatorium_resp_gate` | 3 (-36.042, 3.091, -85.925 / -81.778 / -77.657) | `..._A` / `..._B` | 2.0 / 2.5 | the same travel |
  | `DOORS/GATES_B/Sanatorium_CheckPoint_Gate` | 1 (-38.908, 6.439, -75.664) | `""` (the root rotates) | 3.0 | euler x -90 -> -214.082 (124 degrees the short way) |

  A depth-2 scan stops with "occurs twice" (the gates of a group share one name). The leaves A and B end where the static third leaf `..._C` (local z 3.57)
  stands. Byte-stable across runs; the package check finds all 11 closed transforms once in `iceberg-native-v1`; the scan covers every DotAnimator of level376
  (11 objects, 11 elements; `RealIcebergTests`).
- **The barrier is the gate, not `GATES_A`/`GATES_B`:** `validate_map_descriptors.py` accepts a pose barrier only as an `opened_barriers` entry, so the descriptor
  must list the three paths above (the plan's `DOORS/GATES_A`, `DOORS/GATES_B` was shorthand). Until the promotion the validator reports 11 barrier errors for
  `iceberg.doors.json`; the offline simulation with `opened_barriers` set and the rendered `DoorPoses` literals gives no error, and a changed number is reported.

### Item 8 - gate sounds (`9609eae`)

The six `EntitySoundPlayer`s of `ARENA_SANATORIUM_GATES` (level259) against `iceberg.doors.json`:

| player | clip | m to its gate | next other gate | row |
|---|---|---|---|---|
| `Sliding_door_3` / `_4` | `arena_san_gates_red` (7.37 s, Vorbis) | 0.099 / 0.058 | 7.6 m | GATES_A gate at z -93.689 / -86.125, part null |
| `Sliding_door_1` / `_2` / `_5` | `arena_san_gates_blue` (4.65 s) | 0.033 / 0.329 / 0.090 | 4.15 / 3.83 / 3.55 m | GATES_B gates, part null |
| `Motor_door` | `arena_equator_old_slide_doors` (3.57 s, Equator's clip, `sharedassets56`) | 0.569 | 2.91 m (the third sliding gate) | the motor gate, part `""` |

- `Sliding_door_2` stops the global join (0.33 m / 3.83 m, margin 4.0) and so does `Motor_door` (2.91 m). MAPS rows get optional `join_margin_m` (Iceberg 2.5; a
  competitor inside it still stops the tool) and `join_root`: the nearest-place rule gave `Sliding_door_4` (0.007 m from leaf A, the leaves lie 0.06 m apart
  horizontally, the emitter 2.5 m above) leaf A alone and left leaf B silent; the six players are one per assembly, so Iceberg joins to the root.
- All six players have `pitch` 1.15, no mixer group, AudioSource volume 1.0 and player volume 0.9, min/max distance 1.0/30.0. The extractor's rule (client at
  pitch 1, authored pitch baked) needs `bake_pitch` 1.15 for all three clips; Equator plays the shared slide clip at 1.0, so the fit file gets
  `map_clips.arena_iceberg` (per-map entries win over `clips`). Measured on the baked clips (50 ms envelope, 10 ms transient scan, centroid): blue event 2.55 s
  body [1.0, 2.4], red 5.15 s body [0.8, 5.0], slide 2.61 s body [0.4, 2.45]. Seam tables at drive scale 0.5: blue 10/12 clean, cut 1.198 s, end event lands 1.352 s
  (ideal 1.275); red 25/40, 2.499 s, 2.651 s (ideal 2.575); slide 13/18, 1.297 s, 1.313 s (ideal 1.305).
- The sidecar's `mixer` block is read from the mixer by name now (no Iceberg row names the group); all five sidecars carry the same block.
- `rows --check`: equator, bay5, airpit, bowl and iceberg "is current". `clips --map arena_iceberg`: `3 clip(s), 2392812 bytes of WAV`, built three times to identical
  bytes; no 512-byte sample run of any clip occurs in the four key-source streams (the clips live in `sharedassets56.resource` and `sharedassets259.resource`, which are
  no key source of any map). Copied to `work/extract/gate-sounds/arena_iceberg`.
- **Loudness is open:** with no mixer group and a source volume of 1.0 the baked level is 0.9 (-0.9 dB) against -15 dB on the other four maps. That follows the data;
  whether Arena attenuates these sources elsewhere is unknown, `Spawns/GateSoundVolume` is the lever and the user's ears decide.

### Item 8 - score monitors (`a51e616`)

- Six `ArenaMonitorHolder`s, all in `Arena_Sanatorium_CheckPoint_DesignStuff`, all active: word 0 x4 (`SCORE`, `SIDE`, `SIDE (1)`, `ALIVE (3)`), word 13 x2
  (`SCORE (1)` at (-24.4, 5.3, -70.2), `TIMER (1)` at (29.0, 5.3, -59.0), the latter stacked 1.34 m above a timer). The prop names are copies and do not follow the words.
- **Word 13 = ALL POINT STATUS board.** Census of all 388 level files (467 holders, 0 errors), prop names per word: 0 TIMER/SIDE, 1 SCORE, 2 ALIVE, 11 CAPTURE POINT (6 levels),
  12 POINT STATUS A/B/X (level 228 only), 13 in three levels: `Arena_Yard_TDM_DesignStuff` 228 (four holders, all called ALL POINT STATUS), `Arena_Location_TEMP_
  CheckPoint_DesignStuff` 345 (TIMER (1), (2), inactive) and Iceberg's 376; 14 in 219 (`Bowl_TDM`, ALL POINT STATUS x2) and 346 (`AirPit_2.0_CheckPoint`, TIMER x5). The
  CheckPoint design scenes carry their capture-point boards as 13 or 14 on copied timer TVs. The port has no capture-point face: `TYPE_WORDS[13] = (None, False, "none")`
  like word 10; 11, 12 and 14 stay unclassified. The client's `FaceOf` already gives every other word none, so four of six rows get the timer face.
- `iceberg.monitors.json`: 6 rows; the props are found once in `iceberg-native-v1` with the same activity; `--check` of all five sidecars: current.

### Tests

- New test classes: `test_door_poses` `IcebergSyntheticTests`, `IcebergSidecarTests`, `RealIcebergTests`; `test_gate_sounds` `IcebergRowSidecarTests`, `IcebergFitTests`,
  `IcebergJoinTests`, `IcebergBuildRowTests`, `RealIcebergRowTests`, `IcebergPackageTests` (runs when `work/extract/gate-sounds/arena_iceberg` exists; the package tests honour
  `ARENAMAPPORT_GATE_SOUNDS_DIR`); `test_score_monitors` `IcebergMonitorSidecarTests`, `IcebergTypeWordTests`, `RealIcebergMonitorTests` (package cross-check honours
  `ARENAMAPPORT_EXTRACT_ROOT`); `test_bundle_crypt_tool.KeySourcesTests` and `CryptoRealDataTests` extended. 35 of these run in 14 s, all green.
- Python, all of `tools/tests` from PowerShell: `Ran 2500 tests in 528.560s`, `FAILED (failures=8, skipped=125)`. Six fail only because the lead's promotion has not
  happened: each runs `validate_map_descriptors.py`, which now reports 13 errors for Iceberg (11 pose barriers that are no `opened_barriers` entry, 2 monitor rows with
  type word 13 that the validator's table does not know): `test_door_poses.SidecarTests.test_the_validator_run_passes`,
  `test_score_monitors.SidecarTests.test_the_validator_run_passes`, `test_score_monitors.TypeWordTests.test_the_validators_table_is_the_extractors`,
  `test_gate_sounds.RowSidecarTests.test_the_rows_validate_against_the_door_rows_and_the_descriptor`,
  `test_validate_distribution.RegistrySidecarTests.test_real_registry_accepts_sidecars`, `test_map_pipeline.RegistryTests.test_distant_shadow_sidecar_drift_is_rejected`.
  Two come from this worktree, not from the change: `test_score_monitors.SidecarTests.test_the_sidecar_text_is_what_the_cli_writes` (autocrlf gave the worktree CRLF copies of
  the tracked sidecars; the main checkout has LF) and `test_dll_versions.DllVersions.test_actual_candidate_and_stale_binary_control` (no client DLL in the worktree's `dist/`).
- .NET, `dotnet test ArenaMapPort.Server.Tests -c Release`: `Bestanden!: Fehler: 0, erfolgreich: 2678, übersprungen: 0, gesamt: 2678`.
  (The server build wants `work/extract/map-descriptions/map-descriptions.json`; a copy sits in the worktree's ignored `work/`.)

### Left for the promotion commit

- `opened_barriers` and `start_gates` = the three pose barrier paths above, the descriptor's `DoorPoses` = `E.render_csharp` of `iceberg.doors.json`,
  `ScoreMonitors` = `extract_score_monitors.py --map arena_iceberg --csharp` (6 rows), word 13 in `validate_map_descriptors.SCORE_MONITOR_TYPE_WORDS`
  (`13: (None, False, "none")`), the client csproj gate-sound rows (`arena_iceberg` folder, `*.wav`) and the released-set pins of the door, monitor and gate-sound tests
  (`SidecarTests` in `test_door_poses` and `test_score_monitors`, the C# `DoorPoseSidecarTests` and `ScoreMonitorSidecarTests`). Open: the gate loudness (above), a face for word 13 (a capture-point status board, ArenaMode's Domination points A, B, C could use one), and whether
  `Spawns/SpawnGateDriveScale` 0.5 is right for gates that Arena opens in 2.0 to 5.0 s.
