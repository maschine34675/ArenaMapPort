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

## 2026-10-30 - item 7: mode objects, spawn rows, AI sidecar, bots and exits (workstream branch, offline)

Offline only: package `work/extract/iceberg-native-v1`, the installed Arena 0.6.1.0.48206 (read-only), no Unity, no host, no
deploy. Four tracked sidecars (`tools/maps/iceberg.{modes,ai,bots,exits}.json`), two extractor changes, one new validation
module, tests. Commits `7e323ea` (extractors, modes, AI), `4886740` (bots, exits, probe, spawn tests), and the C# AI rows.

- **Modes sidecar** (`extract_mode_objects.py`, `TEAM_BOUND_RULES["arena_iceberg"]`): the old run still stops with "all 70 spawn
  markers lie on one side of z == 0 ...". The team rule names the sides `bear` / `usec` (Bear = `SPAWN_BOUND_FIGHTER` = east, Usec
  = `SPAWN_BOUND_DEFENCE` = west), takes the two unsuffixed bounds as the pockets and calls a marker a pocket marker when it lies
  inside its team's unsuffixed bound (published box, 0.05 m edge tolerance): Bear 10 of 40 (margins 2.665 to 7.318 m), Usec 10 of 30
  (nine at 0.92 m, marker (6) at -0.002 m, the one reason for the tolerance); no other marker lies inside any unsuffixed bound.
  `capture_point` is null, `capture_points` lists A (16.77, 3.9, -91.39), B (10.614, 2.69, -77.727), C (21.91, 2.85, -47.3).
  With `--arena-data` the tool raw-decodes the 12 `Game.Modes.Data.ArenaSpawnBoundCheckPointData` records of level376 (layout: three
  ints team / initial / flag, link list, one null PPtr, marker list, capture-point list; end offset == object size; GameObject
  path ids equal the package ids) into `stages`: Bear (1)+(5) serve A, (2)+(4) B, (3)+(6) C; Usec (4) A, (2) A+B, (1) B+C, (5) C;
  bound (4) of Bear links across to Usec (2). Cross-checks that pass: every bound has one record, every marker is listed exactly
  once and by its own team, team and `initial` agree with the bound names, the two initial records equal the geometric pockets,
  and every marker lies inside the bound that lists it (0.225 to 7.318 m; Usec (6) -0.002 m). The four released modes sidecars
  regenerate byte-identical from `equator-native-v5`, `bay5-native-v5`, `airpit-native-v9`, `bowl-native-v2` (9,589 / 9,641 /
  10,825 / 10,299 characters). Regenerate with
  `python tools/extract/extract_mode_objects.py --package work/extract/iceberg-native-v1 --arena-data "D:/Battlestate Games/Escape from Tarkov Arena/EscapeFromTarkovArena_Data"`;
  a later package (v2) changes only `source_package`.
- **AI sidecar** (`extract_ai_anchors.py --plan tools/extract/plans/iceberg.json`, `PLAY_AREA["iceberg"]` x -61..121, z -130..-33):
  11 core points (ids 1..11, group 11) all inside the box; the 4-hub PatrolWay (40 sub points, x 69..104, z 5..24, y -3.9) is
  dropped and recorded under `dropped` (4 hubs, 40 sub points, 1 way); `patrol_ways` is `[]`. Measured on Arena's mesh: none of
  the 44 patrol points and none of the ten Savage markers (x 55..130, z -12.8..64.6, y about -3) lies on it. Client, read in
  `AiAnchors.Parse`, `AICorePointRepairs.InitPrefix` and `PatrolWayFallback.EnsurePatrolWays`: `patrol_ways: []` parses
  (`PatrolWays.Count == 0`), the 11 real core points are still authored (`EnsureCorePoint` needs only `CorePoints.Count > 0`,
  debug line "authored 11 real AI core point(s), 0 patrol hub(s)"), and the zone gets the NavMesh-derived ways from the Bot spawn
  markers (`BuildForZone`), exactly like a map without a sidecar. The four released AI sidecars regenerate byte-identical.
- **Arena's NavMesh** (`sharedassets377.assets` path 2, identity placement, 64 tiles, 2,473 polygons, 93 components, main component
  1,681 polygons / 4,787.6 m2, x -58.7..108.7, y -1.8..6.15, z -126.5..-34.8): 64 of the 70 markers and all 20 pocket markers
  (clearance 0.67 to 2.83 m) lie on it; the six that do not are Bear stage markers (38) (0.29 m beyond the border, y 3.17) and
  (47)-(51) (2.3 to 7.7 m from it, y 1.87). All 11 core points lie on it. In the Bear pocket Arena's mesh and the markers
  disagree by up to 0.57 m (+0.02 to +0.57 m over the low part at y 0.73, -0.05 to -0.46 m under the terrace markers at y 1.89),
  and the mesh lies 0.02 to 0.57 m above the port's `Sanatorium_tunnel_respawn_floor` colliders (floor 0.734): item 9's bake audit
  should look at base `SOO_LOD0/RESPAWN_OUTDOOR` (the survey's oracle run named it among the props Arena's mesh walks through).
- **Bots and exits** (`iceberg.bots.json`, `iceberg.exits.json`): four player rows on pocket markers - `all` Bear (14)
  (95.29, 0.88, -89.56) and `bear` Bear (8) (95.96, 0.73, -94.07) in the east pocket, `usec` Usec (8) (-39.85, 3.25, -91.16) and
  `west-all` Usec (14) (-44.53, 3.25, -90.42) in the west one (clearance 1.42 to 2.83 m). Nine bot rows from the core points
  1, 3, 4, 5, 6, 7, 8, 9, 10 (radius 25, Bot + BotPmc); core 2 (9.6 m outside GATES_A) and 11 (6.5 m from the motor gate) are
  skipped by the rule "no bot spawn within 10 m of a gate leaf or a pocket marker", no core point lies inside a team box. Core 6
  keeps 0.58 m of border and moves 1.41 m to (-29.4, 2.05, -52.7) (1.98 m); core 9 is the AICore of capture point A and stands in
  the swimming pool (collider basin floor 4.40, deck and Arena's mesh about 5.0, a 0.6 m step), so its row moves 3.54 m to the deck
  at (19.05, 4.98, -89.09) (3.14 m, collider top and mesh within 2 cm); every bot y is Arena's surface at the point. Waves and PMC
  squads are AirPit's and Bowl's (equal). Exits `Iceberg East Gate` (72.0, 1.95, -94.0) (clearance 5.79 m, 10.4 m from the nearest
  GATES_A leaf) and `Iceberg West Gate` (-30.5, 2.92, -85.0) (clearance 4.40 m, 5.6 m from the nearest GATES_B leaf), 4 x 3 x 4 m,
  8 s, both outside their gates and at least 11.2 m from every player row.
- **Placement** (`tools/validate/spawn_placement_probe.py`, the offline stand-in for `SpawnPlacementRepairs.IsValid`: ground ray,
  ceiling, 16 capsule rays at 0.35 m on the package's colliders): all 13 rows and both exit centres have a floor within 0.22 m and
  the 13 rows pass; 69 of the 70 markers pass, `Bear house (38)` touches the border cube `LevelBorder (238)` at 0.02 to 0.04 m;
  the six off-mesh markers all stand on real colliders (`Sanatorium_balcony_terrain_05`, `wood_board01_long`). What
  `SpawnPlacementRepairs` would do with them: nothing, it never sees them. `make_base_json.py` fills `SpawnPointsCollection` with
  exactly the sidecar's player and bot rows (`CreateFromScene` -> `FixSpawns` deletes every other scene marker), so the 70 Arena
  markers are plain GameObjects; only a mode plugin that places players on the six would meet the border-cube and mesh questions.
- **Tests**: `test_mode_objects` 85 (team rule, box margin, capture points, stage decoder and graph with 13 fail-closed cases,
  the tracked file against a fresh extraction, the four released sidecars byte-identical), `test_ai_anchors` 49 (the drop rule, the
  tracked file against the live source, the released AI sidecars carry no `dropped`), `test_spawn_placement_probe` 10,
  `test_iceberg_spawns` 25 (19 without the ignored package: set `ARENAMAPPORT_WORK_DIR` to a checkout's `work/` when running from a
  second worktree), `AiAnchorsTests` +5 (.NET 2,683 passed with `-p:BuildProjectReferences=false` in the worktree). The full Python
  suite from PowerShell: 2,527 tests, 2 failures that are the worktree and not this change (`test_dll_versions`: no client DLL in
  its `dist/`; `test_score_monitors`: CRLF in a `core.autocrlf` checkout of a tracked sidecar).
