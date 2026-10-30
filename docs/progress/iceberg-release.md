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

## 2026-10-30 - identity art and descriptions (PLAN-ICEBERG item 10)

Workstream branch `worktree-wf_451c0467-c1b-5` (from `feature/iceberg` at `d366c5c`); live Arena 0.6.1.0.48206, read-only.

- **Banner and loading screen exist** (UnityPy, `resources.assets`, unique names): `Matchmaker_Banners_Iceberg` (plural; path 1482,
  769 x 460, format 3) and `AllScreens_Loadingscreen-Iceberg` (path 1183, 1920 x 1080, format 10). `Popups_background_Iceberg`
  (path 3298, 1920 x 500) is a popup strip and stays out.
- **`Arena_MainMenu_Paralax_Resort-1..4` are Iceberg's.** Arena has no `Paralax_Iceberg` set; the 18 parallax sets in
  `sharedassets1.assets` are AirPit (+Alt), BP0, Bay5 (+NY), Block, Bowl, ChopShop, Customs, Equator, Factory, NY, Prison, Resort,
  Sawmill, Shoreline (+NY), Train, Woods, WoodsMCHS, lab; `Resort` is the only one not already another map's. Exported to scratch
  (`scratchpad/iceberg/identity/img`) and looked at, next to the banner and loading screen and next to Shoreline-1, BP0-1, Bowl-1
  and AirPit-1:
  - banner and loading screen: the same view, a multi-storey hotel with a rotunda whose lettering reads the Cyrillic word for
    "Iceberg" (cut off after "AYSB"), a tower crane, street lamps, and a promenade slab that spans a rocky canyon, at a pink dusk;
  - Resort-2: the hotel lobby, the reception wall carries an iceberg-shaped polygon logo above the full Cyrillic wordmark
    "Aisberg" (the same lettering as the rotunda); this is the decisive image;
  - Resort-1: the lobby glazing with columns, broken glass on the floor, and outside the same canyon rock, the same street lamps
    and railing as the banner; Resort-3: the lift door, a planter and two suitcases; Resort-4: the lobby corner with a sofa,
    a planter, a lamp column and a graffiti room;
  - Shoreline-1 is a walled combat yard, BP0-1 a range room, Bowl-1 the stadium field, AirPit-1 the airliner hall: not the hotel.
  So Iceberg gets the five-image rotation of Equator, Bay5 and AirPit (loading screen + Resort-1..4).
- **Rows**: `extract_banners.BANNERS["arena_iceberg"]` and `extract_loadscreens.LOADSCREENS["arena_iceberg"]` (five entries, path ids
  are 0.6.1 hints, lookup by unique name). Real run against the live install into scratch (`dist/` and the main `work/` untouched):
  `extract_banners.py` writes 5 files (iceberg 194,615 B), `extract_loadscreens.py` writes 24 (iceberg 1,035,669 / 783,098 / 667,703 /
  581,589 / 674,689 B), both `--check` OK. `ArenaMapBanners.ByMapKey` gets the `arena_iceberg_banner` / `arena_iceberg` row: the C# tests
  allow a non-released row (they list the bannered maps explicitly and only the maps of `All` are ever resolved), so it lands now.
- **Why `test_validate_distribution` needed a change**: `validate_art` demands the extractor tables equal the released set, so
  one extra row failed the whole `DistributionSidecarTests` class (186 failures, 19 errors, all "Banner table does not match the
  released maps"). The fixture now filters the two tables to its own `RELEASED`; that is a no-op once Iceberg is in `RELEASED`.
  `validate_distribution.py` itself stays strict and fails on the real tree until Iceberg is in `ArenaMapRegistry.All`.
- **Locales re-verified** with `extract_map_descriptions.LOCALE_ASSETS` on `resources.assets`: en/ge/ru have 28,044 / 28,044 / 28,057
  keys; the only keys matching iceberg, sanatorium or resort are `EArenaBackgroundType/Iceberg` and two EFT scav-exit keys
  (`exit_scav_sanatorium...`); the Arena locations with a Name + Description are Air Pit, Equator, Bay 5, Sawmill, Bowl, Chop Shop,
  Block, SkyBridge and Fort. Iceberg has neither row, in any language.
- **Description policy**: `ArenaMapDescriptions.Load` now requires the pack to cover exactly the released maps that HAVE an official
  text (`ArenaMapDescriptions.OfficialTextMapKeys`, mirrored against `extract_map_descriptions.DESCRIPTION_KEYS` by a test that
  parses the Python table); a map without one is absent from the pack and answers with its descriptor's `LocaleDescription`
  (one English string for every language: `Resolve` has no per-language fallback beyond the pack). A damaged, stale or
  over-complete pack (a missing official map, a missing `en`, an empty text, an `arena_iceberg` entry) is still discarded whole with
  the one Warning. `validate_pack` stays strict for the four keys. Promoting Iceberg therefore needs no regenerated pack.
- **Tests**: `ArenaMapDescriptionsTests` iterates the official-text maps and injects `All + Iceberg` for the Iceberg cases (so it
  holds before and after the promotion); `Released_registry_is_exactly_the_four_maps...` became the mirror test. Python:
  `test_banners` 5 maps, `test_loadscreens` 5 maps and the Iceberg entries, `test_map_descriptions` (Iceberg is no key, a pack
  carrying it is rejected, source rows for it are ignored).
- **Results** (PowerShell, this worktree): `dotnet test ArenaMapPort.Server.Tests -c Release` 2,688 passed, 0 failed (the server build
  needs a `work/extract/map-descriptions` pack, copied from the scratch extraction into this worktree's ignored `work/`; a target-level
  property wins over `-p:MapDescriptions`). `unittest discover -s tools/tests`: 2,448 tests (2,444 before, +4), 2 failures that are
  this worktree and not the change: `test_dll_versions.test_actual_candidate_and_stale_binary_control` (no client DLL in this
  worktree's `dist/`) and `test_score_monitors.SidecarTests.test_the_sidecar_text_is_what_the_cli_writes` (the checkout writes CRLF,
  `core.autocrlf=true`, `git ls-files --eol`: `i/lf w/crlf`); neither touches code of this change (not re-run in the main checkout).
