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

## 2026-10-30 - NavMesh oracle and bake inputs (item 9, offline; worktree branch, not promoted)

Workstream of the Iceberg fan-out (worktree `wf_451c0467-c1b-6`). Nothing here touches the descriptor, the sidecar, `base.json`, the
csproj files or the release validators; the decided values are pinned in `tools/tests/test_iceberg_bake_inputs.py` and are written
into `tools/maps/iceberg.json` and `ArenaMapDescriptor.Iceberg` by the promotion commit. No Unity run, no build, no deploy.

**Code and tools.**
- `navmesh_diff.ARENA_SOURCES["iceberg"]`: `sharedassets377.assets` path 2, mask 0x40042A00 (= `AIRPIT_ARENA_LAYER_MASK`, layers
  9/11/13/18/30), AI scene 377 `Arena_Sanatorium_CheckPoint_AI`, package `work/extract/iceberg-native-v1`. Placement is the **identity**
  (`m_Position` 0, `m_Rotation` (0,0,0,1), no Bowl yaw): 64 tiles, 2,473 polygons, 93 components, main 1,681 polygons, 4,787.6 m2,
  x -58.7..108.7, y -1.80..6.15, z -126.5..-34.8; all 11 `AICorePoint` and all 20 pocket markers lie on the main component. Its
  `NavMeshSurface` is `PhysicsColliders` over a 180 x 80 x 180 Volume at (18.7, 4, -73), so **x 108.7 is the edge of Arena's volume, not of the
  play space** (the Bear pocket's tunnel runs on to x 118 inside the cubes).
- Finding, fixed: `BakeNavMesh.RoundOpenedReason` drops every ancestor-or-self whose name starts with `LevelBorder` (it was written for
  Equator's `LevelBorder (N)`); Iceberg's 365 cubes are called `LevelBorder (N)` under a `LevelBorders` group, so with
  `keep_level_borders` and layer 29 in the list the bake would still have lost every cube, silently (Bowl's cubes are called `Cube`).
  The prefix rule is now skipped when the descriptor keeps its borders (`BakeNavMesh.cs`, harness cases `kept-border-iceberg-cube` etc. in
  `test_navmesh_source_layers.py`, 429 -> 434 cases). The offline model got the same switch: `port_colliders.bake_exclusion_reason(o,
  keep_borders)`, `source_owner_mask(..., keep_borders)`, `navmesh_diff.analyse(keep_level_borders=)`, CLI `--keep-level-borders` and the
  descriptor key; the report lists a bounding box per port-model component. Bowl is the only released map that keeps its borders and none of
  the objects in its two DesignStuff scenes is named `LevelBorder*` (312 + 42 layer-29 objects, all `Cube`), so its bake is unchanged; the
  other maps do not set the key. Defaults leave every other map unchanged
  (`test_navmesh_diff` 48 OK, `test_bowl_navmesh_oracle` 19 OK).

**Oracle runs on the live package** (`navmesh_diff.py --map iceberg --package D:/ArenaMapPort/work/extract/iceberg-native-v1 --points <70
markers + 3 capture points + 3 crates + 4 exit candidates> --live-core-points`, about ten minutes each, 0.2 m lattice):

| run | bake sources | Arena samples blocked (Arena agent / port agent) | passable | port component 0 | probes in it | greedy list |
|---|---|---|---|---|---|---|
| every layer but Water, no barrier, no cubes | 11,804 | 2,214 / 2,825 | 115,606 | 4,000.9 m2 | 60 of 84 | GATES_B (+181), GATES_A (+30), 2 refill suitcases (+26 each), `Main/SOO_LOD0/RESPAWN_OUTDOOR` (+303) -> 83 of 84 |
| `opened_barriers` GATES_A/B, layers 9/11/13/18/29/30, cubes kept | 5,135 | 1,995 / 2,498 | 115,915 | 4,515.3 m2 (94.3 %) | 82 of 84 | `RESPAWN_OUTDOOR` (+256) -> 83 |

A probe is located when a lattice sample of Arena's mesh lies within 0.8 m: 84 of the 135 given (not located: the 44 patrol points, Bear stage
markers (47)-(51), capture point A and the crate at (19.7, 3.1, -68.3)). GATES_B joined 11 probes (the 10 Usec pocket markers and core
point 11), GATES_A 9 (the Bear pocket markers except (12)). Exclusion table of the second run: `SPAWN_BOUND*` 1, Triggers layer 13 83, Water 1,
not in `navmesh_bake_layers` 6,993, `opened_barriers` 42, source 5,135. The two probes still outside: Bear house (12) at (97.75, 0.73, -96.15),
0.53 m from the tunnel's right wall (collider box up to z -96.68; blocked for the 0.30 port agent, passable for Arena's 0.28 agent;
`SpawnPlacementRepairs` territory, decided on the host), and Bear house (29) at (47.82, 1.91, -85.27), over a patch where Arena's mesh stands
0.6 to 0.9 m above the port floor. All 70 markers have a port floor (mask layers, slope <= 48) within -0.7..+0.4 m of their height, the
six Bear stage markers that are off Arena's mesh included; capture points A and B stand 0.5 and 0.4 m below their floors (trigger centres).

**Classification of the 120 / 123 "solids Arena walks through"** (instances, m2 of lattice samples):
- Opened gates, 2 instances, 5.0 m2: `DOORS/GATES_B` (3 sliding gates + the rotating `Sanatorium_CheckPoint_Gate`, 26 colliders) and
  `DOORS/GATES_A` (2 sliding gates, 16 colliders). They are the oracle's first two greedy steps; the motor gate's 4.08 m sheet lies on
  Arena's main component along its whole width (5 of 5 points), the sliding gates on 2 to 3 of 5 (the rest is post and wall). Both paths
  resolve with `pc.resolve_barriers` to exactly one root below the single scene root `DOORS`. The two `Sanatorium_Electric_engine` props
  are siblings of the gates under `DOORS`, 0.57 x 0.36 m boxes at y 6.3..6.6, three metres above the pocket floor: they stay standing and
  are not listed.
- CheckPoint cover, 61 / 59 instances, 59.1 / 56.6 m2: `BARRICADE_NN` (SOO and OO groups of level375). **Not gates, not opened.** Arena's
  NavMeshData still has the holes of the **0.5.4 layout**: a cover-sized layer-18 collider (0.5-4 m tall, at most 12 m long) of the 0.5.4
  CheckPoint scene has its centre on Arena's mesh at floor level in 3 of 155 cases (2 %), one of the 0.6.1 scene in 70 of 193 (36 %), and the
  0.5.4 spike run found 1 walked-through prop of the 127 it classified in the CheckPoint scene (35 respected) where this run finds 48 of
  100 SOO props (14 respected) and 13 OO props. Arena 0.6.0 replaced the cover (the mesh was not re-baked: **inferred**, the file cannot
  be dated). The port bake walls the new cover, as the player collides with it.
- Base-scene props, 53 / 48 instances, 40.8 / 35.3 m2: ladders (`Sanatorium_Ladder_01/03`, `Cafe_ladder`), pool-room showers and shelves, theatre
  walls, interior metal door leaves, sandbags, trees; none of them carries a connection (the greedy list needs none). Left as bake sources: the
  player collides with them too.
- `Main/SOO_LOD0/RESPAWN_OUTDOOR`, 1 instance, 6.4 / 5.2 m2: the Bear tunnel's railway buffer stops (1.1 m tall), two cabinets, a switchbox and
  window frames on the tunnel floor at y -2..1.5. Real props, not a gate; it is only on the greedy list for Bear (12) above. Left in.
- Interior metal doors (`Inside_Door_Metal_16/13_*_door_L/R`, layers 9/30, static closed leaves): the three small components of the second
  run inside Arena's footprint are rooms they seal together with cover: #1 60.2 m2 (x 18.6..35.2, z -45.4..-36.4, y 2..3), #2 25.1 m2 (x
  15.2..18.1, z -117.5..-105.5, y 5.0) and #5 6.0 m2 (x 32..35.6, z -89.8..-87.5); #3 11.6 m2 is the strip along the Bear tunnel wall and #4
  6.3 m2 a theatre wall corner. About 91 m2 (1.9 % of Arena's footprint) behind doors, no probe inside; Arena's mesh passes 4 of the 16
  `Inside_Door_Metal_16` instances and respects 2, so it is no systematic "doors open" record. **Open question, not decided here**: they
  stay walls (the player collides with them); opening them is an `opened_barriers` entry per door pair plus a rebuild if a host raid shows a
  room that should be reachable.
- Refill suitcases (`REFILL_CASE/...`, layer 22), 2 instances, 2.4 m2: the layer list drops them (layer 22 is Interactive).
- Border cubes, 14 instances in the Arena-agent list (14.4 m2, 0.3 % of Arena's footprint; 20 clusters / 18.3 m2 for the port agent): the
  largest are `LevelBorder (203)` 4.8 m2 at (8.6, 2.1, -43.1), `(102)` 4.6 m2 at (54.6, 2.0, -78.3) and `(164)/(163)` 2.2 m2 at (33.5, 3.2,
  -89.9). All probes stay in one component, so no cube seals a route inside Arena's footprint; they are walls for the player and stay walls.

**Bake inputs decided** (sidecar spelling; C# names in `test_iceberg_bake_inputs.py`):
- `opened_barriers` `["DOORS/GATES_A", "DOORS/GATES_B"]` (root name plus child, scene root `DOORS` of `Arena_Sanatorium_CheckPoint_DesignStuff`),
  `start_gates` the same two, `held_gates` `[]`. The strings must equal the `barrier` strings of `iceberg.doors.json` (exact match in
  `ResolveOpenedBarriers` and the client): if the doors rows come out unit-level (`DOORS/GATES_A/sanatorium_resp_gate` with `at`,
  `DOORS/GATES_B/sanatorium_resp_gate`, `DOORS/GATES_B/Sanatorium_CheckPoint_Gate`, as the survey's dry run did) these three are the
  opened barriers and start gates instead; the dropped colliders are the same 42 (every collider below the two roots sits in one of the six
  units), only the strings change.
- `navmesh_bake_layers` `[9, 11, 13, 18, 29, 30]`, `keep_level_borders` true, `uses_server_time` true, `navmesh_bake_exclusions` none.
- `bake_bounds` `[-62, 121, -3, 8, -131, -33]`. x/z: the 365 cubes span x -60.11..119.53, z -128.87..-34.86 (+1.5 to 2.1 m); Arena's mesh stops at
  x 108.7 by its own volume, so the cubes, not the mesh, are the reference on the east. y floor -3: Arena's lowest floor is -1.79, the solid
  `KillTrigger` (a layer-29 BoxCollider at [-53.02, -10.98, -70.69]..[-41.91, -6.14, -53.48], not a trigger) would be a 191 m2 island at its top
  face below it, and Arena's other components inside the box at y -19..-6 (ledges) stay out; y ceiling 8: Arena's highest floor is 6.15 (+1.7
  head room), its elevated islands start at y 8.5 and the 149 m2 roof at y 11, and the cube tops are the other reason (below). Above Arena's
  walkable columns the package has upward floor faces (cafe, dressing rooms, library): about 2,000 m2 at y 6..8 (inside the box) and 1,800 m2
  at y 8..14 (outside), none with a polygon in Arena's mesh; whether the ones inside the box come out as islands or as stairs is for the
  U1 bundle (`navmesh_diff.py --compare-bundle`), not for this offline pass.
- Cube-top islands, no exclusion: all 365 cubes have an upward face (857.7 m2 projected, 564.3 m2 after the agent radius erodes tops under
  0.6 m; 364 are islands, one - `LevelBorder (213)`, top y 3.21 - is flush with the floor). A ceiling of 8 leaves 84.6 m2 of it (143 cubes,
  15.7 m2 of that the flush one), 10 would keep 518 m2, none 564 m2 (Bowl accepted about 608 m2).
- Audit (AGENTS.md "Layer-0 blockout"): live layer-0 solids in the cube footprint are six, only two stand on Arena's floor: the SPAWN_BOUND
  box (dropped by name) and `Goshan_garbage_10_COLLIDER` (0.6 x 0.6 x 0.7 m at (35.5, 3.5, -67.5)); the other four are cliff rocks 16..38 m below.
  Of 6,518 live layer-12 solids in the footprint 37 have no movement-layer twin (street-lamp glass, wires, signboards at y > 6), none touches
  Arena's floor. Layer-12/21/22/0 solids are what the layer list drops (7,000 owners); the package has no live layer-11 collider.

**Puddles** (census on the package, `Decal/Water Deferred Decal`: 3 materials, 30 renderers, all active; surface under 5 x 5 samples per quad):
`0877f2335044042e` "puddle" (USERAIN, 23 `puddle2` quads, 14 in `Arena_Sanatorium` + 9 in `_Light`; Concrete 335 + 63 off-plane of 575,
WoodThick 34, Soil 21) and `34f527e8925ffebb` "City_puddle_atlas_white" (static, 4 atlas quads in the pool hall; Concrete 55, GlassShattered 12)
are water; `299a841bb64d31bd` "puddle fuel" (3 renderers named `puddle_oil`) is oil and is excluded. `puddle_water_material_names`
`["0877f2335044042e", "34f527e8925ffebb"]`, no `puddle_extra_surfaces` (Soil is 3.7 % of the rain quads' samples in plane, Concrete 58 %: the Concrete/Asphalt guard is enough).

**Shaders.** The package has 31 shader names; six are not in EFT's global shader bundle: `Cloth/ClothShader` (3 materials, 280 renderer slots),
`Cloth/ClothShader_backface` (1), `ANGRYMESH/PBR Rocks/PBR BlendTopDetail (Legacy)` (10 / 151), `Custom/Billboard_FogSheet_Simple` (1 / 8)
- all four resolve in process from Customs' scene assets (AirPit host evidence; Equator, Bay5 and Bowl carry the same materials) - and `MK4/Rock Simple` (1 / 1) and
`Nature/SpeedTree8` (4 / 16, the CAFE olive tree), which no released map uses. Decision for the promotion: **no `shader_fallback_names`**.
`ShaderStandinPolicy.FallbacksActive(mapKey, bay5, airPit, bowl)` is true only for those three keys, so a list on Iceberg's descriptor would be
inert; a real row needs (1) `IsIceberg` and a fourth switch argument in `FallbacksActive` and its callers, (2) a new BepInEx key
`MaterialFixes/IcebergShaderStandins` bound in `ShaderStandins.BindConfig` (new key, default true, no migration), (3) for the four
Customs shaders `UsesExtendedStandins`/`FixFor` for Iceberg (cloth and rock texture/float fixes are AirPit-only today) and `AirPitTargets`
for the backface, (4) an own target dictionary for `MK4/Rock Simple` -> `MK4/Rock` (no row in the six-row table; its texture slots must be
read from the material first) and `Nature/SpeedTree8` deliberately stays row-less, (5) `ShaderStandinPolicyTests` and
`test_shader_fallback_names.py` rows, the descriptor list and the sidecar key in one commit. The first host raid decides: `[RebindShaders]`
lines for the six names (resolved to EFT's own loaded shader, or unresolved Error).

**Tests.** New `tools/tests/test_iceberg_bake_inputs.py` (44 tests; 20 need no data, one of them waits for the promotion commit, the rest skip
without the package, the installed Arena or UnityPy; `ARENAMAPPORT_WORK_DIR=D:/ArenaMapPort/work` points a worktree at the ignored inputs; 12 s with data). Edited: `test_navmesh_diff.py`
(descriptor input dict, `--keep-level-borders`, the border rule), `test_navmesh_source_layers.py` (the Iceberg cases), and the two
"no other sidecar carries these keys" tests in `test_airpit_bake_inputs.py` and `test_shader_fallback_names.py` skip `iceberg` now.
