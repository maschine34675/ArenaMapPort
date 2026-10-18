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

## 2026-10-18 - U1 build attempt 2: a BC7 outlier pixel; package v2 validated

- `validate_extract.py --package work/extract/bowl-native-v2`: `OK -- all checks passed` (18 non-blocking warnings, both
  reproducibility runs exit 0, freshness 0 diffs). v2 is the accepted build input.
- U1 attempt 2 (11:46) failed closed on `City_Signs_Road_Metal_01_N` (BC7 Linear, 1024 x 1024, `City_signs_metal_01`
  on the SMap shader's `_BumpMap`): `R=251..255, minA=0, maxGreenBlueDifference=18`, two above the BC7 bound of 16
  from AirPit's ten BC7 normals. A replica of the policy over all 291 normal textures of v2 (scratch
  `bowl_normal_policy_check.py`): 278 DXT5 + 1 Format29 PackedRaG, 9 BC7 PackedRaG, 1 RgbNormal (the tarp), 1
  ClothNormalsMask, and this single reject; its G/B difference is 18 in ONE pixel of 1,048,576 with a mean of 0.00
  (the other nine BC7 normals peak at 1..9). Bound widened to 24 (an RGB normal differs by 100 and more and keeps
  B near 255, so the other layout stays rejected); harness cases at 18 (accepted) and 25/26 (rejected). U1 attempt 3
  started afterwards.

## 2026-10-18 - items 8-9: promotion into the released set (offline, while U1 runs)

Offline only: no Unity, no host, no deploy; nothing under `work/build`, `UnityProject/` or the dist `bundles/` folder was
touched (the U1 build writes them). Wrote repository files, the ignored `dist/` (banner, loading images, DllOnly staging of the AI
sidecar and acoustics, both Release DLLs, `db/arena_bowl/base.json`) and the ignored `work/extract/map-descriptions` and
`work/extract/spatial-audio/arena_bowl`. Bowl is now in `ArenaMapRegistry.All` and in every list that names the release; release
0.2.2 stays the three-map package and no release file is cut.

| Area | Change |
|---|---|
| `ArenaMapPort.Shared/ArenaMapDescriptor.cs` | `All = { Equator, Bay5, AirPit, Bowl }` (Catalog order kept), doc comment says Bowl is registered and staged for 0.3.0 |
| `tools/validate/validate_map_descriptors.py` | `RELEASE_MAP_NAMES` / `RELEASE_MAP_KEYS` carry Bowl; `validate_distribution.py` and `make_release_zips.py` derive from them (comments only, no logic change) |
| `tools/park-unreleased-dist.ps1` | `bowl` leaves the parked stems (10 stems, 50 rows) |
| `tools/build_remaining.ps1` | `bowl` removed from the default list and refused with a message, like AirPit: the runner derives `work/extract/bowl` (the Arena 0.5.4 spike package) and writes `work/build/bowl`, which would overwrite the U1 output |
| `ArenaMapPort.Client/ArenaMapPort.Client.csproj` | `ArenaMapPortBowlBuildDir`; scenes + navmesh from `work/build/bowl`, `work/build/preset/arena_bowl_preset.bundle`, `material-shaders.json` staged as `material-shaders.arena_bowl.json` (own `<Error>` row and message); `tools/maps/bowl.ai.json` row; `work/extract/spatial-audio/arena_bowl` acoustics row and the missing-input condition. No flares, water or DynamicMaps row |
| `ArenaMapPort.Server/ArenaMapPort.Server.csproj`, `ArenaMapBanners.cs` | `db/arena_bowl/base.json` Content and DeployToDist rows; banner row `arena_bowl_banner` / stem `arena_bowl` |
| `NativeDecalRepairs`, `NativeSpatialAudio`, `PrecipitationAudio`, `RoomReverb` | the per-map table gets `ArenaMapRegistry.Bowl` (new sections `Decals (arena_bowl)`, `Audio (arena_bowl)`; hidden for players by the existing section-suffix rule); `MapConfigVisibility` already derives from `All` |
| `MapLightingDefaults` | Bowl joins the Reserve rig, ZeroLevel -4.4 (its 20 spawn markers sit at y 0.047), no authored-sun keys (no Directional light) |
| `AmbientAudioDefaults` | `arena_bowl` gain 3 dB, offset .4, range 3 (provisional like AirPit's first mix); previous = same; `HasLegacyMix` false |
| `AmbientAudioProfiles` | Bowl profile mirroring the contract: root `ArenaMapPort_BowlNativeAudio`, 33 players, 29 active, four inactive (`cracles (2)`, `water_drops (4)`, `water_drops (1)`, `water_drops`), routes in `group_mixers` order: `CommonAmbientInGroup` (AmbientIn, 15, effects + reverb reroute) then `CommonAmbientOutGroup` (AmbientOut, 18). See the finding below |
| `tools/extract/extract_banners.py`, `extract_loadscreens.py`, `extract_map_descriptions.py` | `BANNERS["arena_bowl"]` = `Matchmaker_Banner_TheBowl` (hint path_id 2538), `LOADSCREENS["arena_bowl"]` = `Arena_MainMenu_Paralax_Bowl-1..4` (hints 164, 1314, 274, 834; 2048 x 1024; Arena ships no `AllScreens_Loadingscreen` for Bowl, so four files and no fake fifth; every consumer derives the count from the table), `DESCRIPTION_KEYS["arena_bowl"]` = `62eba95b7ba21c1d534d305a Description` |
| Docs | `AGENTS.md` (Bowl registered and staged for 0.3.0, not released; builds from `bowl-native-v2`), `docs/dist-manifest.md` (dated paragraph, bundles pending), this section |

### Finding: Bowl's groups list the inactive players, the ambient driver did not allow that

The task text and the plan describe the profile as 29 active of 33 players with group members 15 + 18. Those numbers cannot
all hold in the existing model: the contract says `group_coverage "all_players"` and `grouped_players 33`, and the exported
package confirms it (both `AmbientSoundPlayerGroup` records list all 33 players, the four `water_drops`/`cracles` ones sit
under an inactive ancestor). `AmbientAudioProfile` required `sum(Members) == ActivePlayers` (Equator's groups list only the
active players), and `Bay5AmbientAudio.ValidateGraph` required the group members to equal the enabled-and-active players, so
(a) the profile table's static initializer threw for every map (first dotnet run: 10 failures, `Inconsistent ambient player
census for arena_bowl`) and (b) Bowl's ambient graph would have been refused at raid start (`native group membership differs
from enabled active players`). The importer already accepts both codings (`ValidateMembership`, "an inactive member never plays").
Change: `AmbientAudioProfile.GroupsListInactivePlayers` (ctor parameter, default false; true needs original activation and at
least one inactive player) and `GroupedPlayers` (all players when true, else the active ones); `ValidateGraph` compares the
member list with `GroupedPlayers`, derives the started set as the active members when the flag is set (and then also demands
that the members are exactly the graph's players), and is byte-for-byte the old rule for Equator, Bay5 and AirPit. The group's
own `SetMixerGroup` is safe on never-awakened players (decompile `BaseAmbientSoundPlayer.SetMixerGroup` uses the lazy
`Source`). Not host-verified: the first Bowl raid must show `[NativeAudio] ready players=33 started=29 ... sourceInactive=4` at
Debug level and no `refusing playback` error.

### Extractor runs (read-only on the Arena install)

- `python tools/extract/extract_banners.py`, `extract_loadscreens.py`, `extract_map_descriptions.py`: Bowl banner 769 x 460
  (219,073 B), four loading images 2048 x 1024 (1,055,400 / 1,005,830 / 973,521 / 1,062,160 B; viewed: the stadium), the
  description pack now holds 4 maps in en/ge/ru. SHA-256 of the 41 files that existed before (the Equator/Bay5/AirPit
  banners and loading images, the three spatial-audio folders, the staged acoustics and ai folders, the locales copy) compared
  before and after: 40 identical; the only changed file is `work/extract/map-descriptions/map-descriptions.json` (new
  `arena_bowl` key; the 9 existing entries, 3 locales x 3 maps, are byte-identical to the previously shipped pack).
- `python tools/extract/extract_spatial_audio.py --map arena_bowl` -> `work/extract/spatial-audio/arena_bowl`: source scene
  level111, 14 rooms, 25 portals, 24 areas, 1 clip (`sharedassets111.assets_40.wav`, 2,800,324 B), `mixer_effects` present,
  `source.audiobakedata` 7,039 B (55 room pairs, 196 routes, sha `d7291dea...0035`), `spatial-audio.json` 156,032 B.

### Fixtures moved off Bowl

Prison is the parked fixture everywhere (no test needed another property): `ArenaMapBannersTests` (no-banner map, locale no-op;
Bowl gets its own descriptor row and joins the empty-directory loop), `ArenaMapDescriptionsTests` (stadium fixture text,
`unexpected-map` = `arena_prison`, new `three-map-pack` fault: a pack from before Bowl is discarded wholesale),
`ReleaseConfigurationTests` (`Lighting (arena_prison)` parked, `Lighting (arena_bowl)` hidden for players and visible in developer
mode, "only four maps are playable", 10 parked), `FixedRaidTimeSelectionTests` (Bowl joins `NightTimeAllowedLocations` and keeps
PAST, Prison is the vanilla-reset fixture), `AmbientAudioProfilesTests` (key list, a Bowl test, the contract mirror uses
`GroupedPlayers`, ctor invariants for the new flag, driver text pins), `AirPitWiringTests` (Bowl row pinned, regex),
`AirPitMapTests` / `SelectionMapPositionTests` (four released keys), `WeatherRigDefaultsTests` (Bowl rig row, 10 parked, no sun
keys), `test_park_unreleased_dist.py` (10 stems / 50 rows, Bowl files preserved), `test_validate_distribution.py` (four-map
fixture, `DISTRIBUTION_OK 4 maps 12 bundles 4 sidecars`, Prison as the parked output, new Bowl test: four loading images, a fifth
or a missing fourth rejected, no overview/flares/water), `test_release_zips.py` (Prison as the parked fixture, new
`BowlPartTests`: a five-part fixture with Bowl's four images and no flares/water/overview), `test_upload_pixeldrain.py` (the
single-ZIP smoke test pins the fixture's three-map release list like the packager tests do), `test_banners.py`,
`test_loadscreens.py` (five images per map except Bowl's four), `test_map_descriptions.py`, `test_map_pipeline.py` (four released
names), `test_dynamicmaps_deploy.ps1` (unsupported selection `Prison`, not run, see below) and the `ConfigRetirementCheck` tool
(`Lighting (arena_prison)` as the hidden/parked section).

### Commands and results

- `dotnet build ArenaMapPort.Server/ArenaMapPort.Server.csproj -c Release`: 0 warnings, 0 errors; server DLL
  `4adf04e371fa3ef9f954726f17c04c3ddfd95a0be135b2f917d4b4ca12e45d7e` (74,240 B), bin == dist.
- `dotnet build ArenaMapPort.Client/ArenaMapPort.Client.csproj -c Release -p:ArenaMapPortDllOnly=true`: 0 warnings, 0 errors;
  client DLL `4a613df1b6fb2976ad3389c5b20e38df15557f535389e8b85b7ccc34b3b1e0a3` (818,688 B), bin == dist. It stages
  `ai/bowl.ai.json` and `acoustics/arena_bowl/` into dist.
- `dotnet test ArenaMapPort.Server.Tests/ArenaMapPort.Server.Tests.csproj -c Release`: **881 passed**, 0 failed (873 before:
  eight new tests, ten failures fixed on the way); 887 after the adversarial review added six Bowl rows to
  `AmbientAudioDefaultsTests` (accepted mix, revision-3 survival, new-keys-never-migrate).
- `python -m unittest discover -s tools/tests -p "test_*.py"`: `Ran 1799 tests in 202.399s`, `OK (skipped=47)` (implementer's run
  234.703 s, same counts; the 47 skips are the Arena-install / host / symlink gated tests and the real-dist smoke test).
- `python tools/validate/validate_map_descriptors.py`: `OK - 14 tools/maps descriptors, all 14 catalog registry entries, release
  All membership, and ArenaPaths.cs's EquatorDefault() all agree.`
- `dotnet run --project tools/tests/ConfigRetirementCheck/ConfigRetirementCheck.csproj -c Release`: all three `*_OK` lines.
- NOT run: the non-DllOnly client build and `validate_distribution.py` (both need the U1 bundles and `material-shaders.json`
  of `work/build/bowl`), `tools/tests/test_dynamicmaps_deploy.ps1` (it drives `deploy-to-spt.ps1`, which this pass must not run),
  `test_unity_log_scan.ps1` (no editor code touched). `test_release_zips.RealDistSmokeTests` skips until the Bowl bundles are in
  dist.

### Open follow-ups

- When U1 finishes: non-DllOnly client build, `validate_distribution.py` (expect `DISTRIBUTION_OK 4 maps 12 bundles 4 sidecars`),
  then the first host raid; add the Bowl spike bundles' hashes to `STALE_SPIKE_BUNDLES` only if the 0.5.4 spike ever lands in
  `work/build/bowl` again (it is archived under `work/build/bowl-spike-20260915`, so nothing stale can be staged today).
- Per-map rows that default for an unlisted map and were left alone on purpose (PLAN-BOWL items 9/17): `BloomTuning` (-1/-1 = the
  camera's own values), `HdrImageTuning` (neutral), `RoomShotAcoustics` (global room classes), `NativeMultiFlare` (Equator and
  Bay5 only), `SainLocationShim` (generic fallback), ambient mix defaults (provisional 3 dB / .4 / 3). `RainCoverRepairs` limits
  the play-space footprint by `BakeBounds`, which Bowl carries like AirPit (judge it on the first rain check).
- `tools/maps/AdditionalMapBuilds.props` still lists `bowl` among the additional maps (nothing imports it, as for AirPit).
- The working tree also shows Unity-side changes that are not part of this promotion: the `UnityProject/Assets/ArenaMapPort/
  StandinShaders/*.shader` files (rewritten by the running build) and two new untracked stand-in shaders
  (`p0_Reflective_Bumped_Specular_TwoSided`, `Custom_Vert_Paint_SoftCutout_Decal`) with their `.meta` files.

## 2026-10-18 - U1 build attempts 3 and 4: audio group membership, the Lum_lamp twins

- Attempt 3 (11:50) imported every texture and failed in the Scripts-scene authoring: `Native audio group membership
  differs from active source graph`. Arena groups Bowl's four inactive players too (contract `group_coverage`
  `all_players`, 33 of 33 grouped), while `Bay5AudioImporter.ValidateMembership` accepted only the active set
  (Equator's reading). Both readings are accepted now (`ce846f8`); an inactive member never plays.
- Attempt 4 (12:02-12:20) built the bundles: `arena_bowl_scenes.bundle` 1,006,073,696 B, `arena_bowl_navmesh.bundle`
  185,840 B. Bake inputs honoured: `bounds from the descriptor (bake_bounds), layer mask 0x60040A00
  (navmesh_bake_layers), 1 navmesh_bake_exclusions, 2 opened_barriers`; excluded 14 sources under DOORS_SPAWN_A, 19
  under DOORS_SPAWN_B, the slab; `sources=5476`; `triangulation: vertices=12704 triangles=5890` (the spike's
  renderer-union bake had 51,335 / 22,879). `validate_bundle_roundtrip` then failed: `missing/ambiguous built ballistic
  owner 6535` = the `ballistic` child of `SBG_Bowl_Light/OO/LEFT/Lum_lamp_intact`. Each of the six lamps carries TWO
  `ballistic` children at the same pose whose rotations differ by 1e-7 (`[-0.50000006, 0.5, 0.49999997, 0.50000012]`
  vs `[-0.5, 0.5, 0.49999988, 0.50000012]`, different meshes), so both built twins matched the first package twin
  within the float32 tolerance and the matcher reported two candidates. Fix: when several built owners match a chain,
  the component's own built fields pick the twin, and indistinguishable twins are consumed in order (the second package
  twin takes the remaining one). Post-Unity steps re-run with `-SkipUnityBuild` (base.json, preset, validators).

## 2026-10-18 - U1 accepted by the pipeline (bundles, identity patch, every validator)

- After the twin-matcher fix the post-Unity steps (`build_all.ps1 ... -FullMap -SkipUnityBuild`, 12:28-12:55) passed:
  `patch_monoscript_identity` nothing to patch on both bundles, `validate_extract` OK (v2, 18 non-blocking warnings),
  `validate_bundle_roundtrip` OK (1 non-blocking warning), `validate_preset` OK, `validate_map_descriptors` OK;
  `build_all` reports `work/build/bowl is safe to copy into dist/`. base.json and the preset regenerated from the
  sidecars (byte-identical to the committed base.json). Bundles: scenes 1,006,073,696 B, navmesh 185,840 B, preset
  6,947 B.
- The Unity build regenerated the stand-in shader stubs under `UnityProject/Assets/ArenaMapPort/StandinShaders/` from
  Bowl's material properties: 22 stubs changed their property lists and two new ones appeared
  (`p0_Reflective_Bumped_Specular_TwoSided`, `Custom_Vert_Paint_SoftCutout_Decal`), committed with this build as the
  AirPit precedent did (d29bad4). Next: full client build staging the bundles, `validate_distribution`, deploy, raid.

## 2026-10-18 - deploy and the first Bowl raid on GPU-2 (U1, item 11)

- Full client Release build staged the Bowl bundles and `material-shaders.arena_bowl.json`; `validate_distribution`:
  `DISTRIBUTION_OK 4 maps 12 bundles 4 sidecars`. `deploy-to-spt.ps1 -Confirm` into `D:\SPT41`: 81 files (9 new, 7
  overwritten, 65 identical, 3,104 MB). `gpu-run deploy-wait --server --force --game`: 2,267 files identical on the host
  after 120 s, SPT server stopped and started, EFT restarted, build 18/18 identical (12:38). Server log:
  `Arena: Bowl is available in location selection`, banner wired.
- Raid 1 (12:39-12:42, `gpu-run raid --mode pmc --map bowl --bots none`, tile "ARENA: BOWL"): no ArenaMapPort warning
  or error. `[NavMesh] added navmesh for 'arena_bowl': 12704 vertices, 5890 triangles`; `[RaidFix] spawn openings:
  spawn barrier 'DOORS_SPAWN_A' at (0.0, 0.0, 0.0): deactivated 2 part(s) ... 'DOORS_SPAWN_B' ... deactivated 2 part(s)`
  (no ReportRays verdict, as predicted); `[RaidFix] arena_bowl TeamFight borders: 354 object(s) matched (layer
  'LevelBorder'=29) - RemoveTeamFightBorders=False (colliders left enabled, renderers always disabled)`;
  `[RebindShaders] 'p0/Reflective/Bumped Specular_TwoSided' has no SPT shader - using fallback 'Legacy
  Shaders/Transparent/Cutout/Bumped Diffuse 2 side'` (341/341 + 21 + 21 + 18 + 37 + 9 stand-ins rebound);
  `[NativeAudio] ready players=33 started=29 ... mixers=CommonAmbInEffects,CommonAmbOutEffects ... sourceInactive=4`;
  `[Lighting] arena_bowl applied ... 106 light(s) (cullingLights=106, editorIntensityBaselines=104)`; `[TimeOfDay]
  arena_bowl: native server time and weather`; `[RainCover] ... RainBounds ... center=(-2.00, 24.24, -35.50)
  size=(208.00, 53.08, 173.00) ... 3096 outside the play space`. Spawn `SpawnPoint_Player_All` (50.2, 0.0, -3.2),
  server time 05:42, autumn, 120 fps (max frame 8.3 ms). EFT's own errors.log: the usual ported-map lines
  (`WeatherObstacle.Instance is null`, `SeasonsMaterialsFixer.Instance is null`, `ImpostorsRenderer.Instance is null`,
  maperrors 3 scene markers deleted / 21 backend spawn points added) plus `Invalid windSettings` (to compare with AirPit).
- Reachability: `uibridge_teleport dryRun` from the east pocket to both exits (51, 0.1, -31) and (-51, 0.1, -49) and to
  core point 2 (2.5, 0.1, -21.1): all passable (gates open, pocket connected). `uibridge_walk` from the field centre to
  the Army gate: 67.2 m along the navmesh, arrived after 12.5 s sprinting, and the player extracted through `Bowl Army
  Gate` after 8 s (the exit boxes work; `uibridge_places` lists both exfils, type pmc, entryPoints arenabowl).
- Stills (host `C:\Temp\uibridge-host\captures\stills\`): `bowl-u1-spawn` (east pocket at dawn: containers, roof
  lattice, graffiti, a green signal smoke), `bowl-u1-center` (the Mi-26 with the Welcome graffiti and the crates). Both
  dark (05:42); the daylight look, the tarp stand-in at (-3.5, 5.4, -72.8) / (13.5, 5.4, -71.2) and bots follow in
  raid 2.

## 2026-10-18 - raid 2: noon, medium bots, the tarp stand-in and the west pocket (U1, items 11/12)

- Raid 2 (12:43-12:49, same EFT process as raid 1, `gpu-run raid --mode pmc --map bowl --bots medium --clock 12:00`;
  "next-raid clock 12:00:00 applied at raid start"): every repair repeated with the raid-1 numbers (`[NavMesh] ...
  12704 vertices, 5890 triangles`, both spawn barriers 2 parts deactivated, 354 borders kept, `[Lighting] ... 106
  light(s)`, `[TimeOfDay] arena_bowl: native server time and weather`). The ambient graph re-armed in the same process:
  raid 1 ended with `[NativeAudio] stopped ownedPlayers=29 emptyClipsReleased=29 cleanupFailures=0; owned graph
  disposal scheduled`, raid 2 reported `[NativeAudio] ready players=33 started=29 ... sourceInactive=4` again. The
  `[ArenaMapPort]` Warning/Error query over the whole process (buffer since 12:38:44, both raids) returned 0 lines.
- Bots: `[BotSpawn] BotZone.Init 'BotZone_Main': markers=17 onNavMesh=17 points=17 maxPersons=-1 patrolWays=1`; the
  zone took its real patrol way (7 hubs, 79 sub points) and 19 core points from `ai/bowl.ai.json`. Waves at 16 s, 146 s
  and 251 s each created one assault Savage (`BotOwner.Create -> 'Bot1' startCorePoint=0`, `ActivateBotCallback ...
  bot registered with BotsController`, `Updated assault bot ... to use: assault brain`). `uibridge_bots` at 12:48:
  2/2 bots `state Active, layer PatrolAssault, standby active` at (-11.3, 0, -39.3) moving and (-16.3, 0, -35), 71-73 m
  from the player in the east pocket - they patrol the field, not the pockets. Third-party noise as on Equator
  (bots-stage1.md): Bot Placement System's `TryToSpawnInZonePatch EXCEPTION ... GetNewValidBotZone` once at the third
  wave; Bot3 was created and registered right after it.
- Tarp stand-in: teleports to (-3.5, 0.1, -66) and the marker side were refused as unreachable (the oracle's floor cells
  sit at (-3.5, 0.04, -70.8) under the tarp building); from there `bowl-u2-tarp`, `bowl-u2-tarpA-south` and
  `bowl-u2-tarpA-north` show the tunnel tarp rendered by the `Legacy Shaders/Transparent/Cutout/Bumped Diffuse 2 side`
  stand-in (cloth texture, both faces, no magenta). `bowl-u2-spawn-noon` is the east pocket at 12:00 (daylight stadium,
  stands, roof lattice, city skyline), `bowl-u2-west-pocket` the west pocket from marker (1) at (-55.3, 0.1, -69.1)
  looking yaw 51 (containers, graffiti wall, road barriers, the green signal smoke, the skyline behind the roof).
- Crossing: `uibridge_walk` from the west pocket marker back to the east spawn (50.2, 0.1, -3.3): 124.4 m straight,
  151.4 m along the navmesh, arrived after 29.6 s sprinting - the two pockets and the field are one NavMesh component.
  Performance over 300 frames: 120.0 fps, mean 8.3 ms, p95 8.3 ms, max 8.5 ms; RAM 11.6 GB (managed 2.8 GB).
- Left through `gpu-run leave` (DISCONNECT -> CONFIRM LEAVE -> ExitStatus ... -> HealthTreatment -> MainMenu, 24 s).
- Hideout check (12:55, after both raids in the same process): `gpu-run scenario` (its `click` step drives the
  host's Windows MCP, which the VM's own `windows-gpu-2` connection could not reach): HIDEOUT clicked, screen
  `Hideout` with the overview, 31 labels (ENTER, BACK, the seven station buttons, the bottom bar) after 8 s;
  `logAbsent` level error contains `[ArenaMapPort]` passed, 0 warnings; BACK returned to `MainMenu`. Screenshot
  `hideout-open.png` in the scenario report. Not done in this session: the F8 rain readback (both raids were dry). Items 11/12 of PLAN-BOWL are
  otherwise satisfied by U1: spawns, gates, borders, exits, bots, audio, lighting and the stand-in all behaved on the
  first build; no U2 is needed for playability.

## 2026-10-18 - release texts, identity checks, the windSettings line

Texts and records only: README.md, FORGE.md, the new `docs/releases/v0.3.0.md`, the two Bowl sentences of AGENTS.md and
`docs/progress/INDEX.md` (it now also lists `bowl-dynamicmaps.md` and `bowl-source-060.md`). No code, no version numbers
(0.2.2 -> 0.3.0 is the integrator's), no csproj, no dist, no deploy, no Unity, no host. A review the same day corrected
five points of the first draft; they are folded into the bullets below and listed under "Fix round" at the end of this section.

### Release texts (PLAN-BOWL item 18, preparation)

- `docs/releases/v0.3.0.md` follows `v0.2.2.md`: Included (four maps), Changes since 0.2.2 (Bowl as the new map, the weather
  sky-provider rebind of 6c6a38c, the distribution note), Known limitations, Download, Installation. Every size, hash, URL,
  source commit and VirusTotal value in Download is a placeholder in angle brackets inside a code span. The installation
  needs the full ZIP (Bowl's bundles are new); the compatibility line is unchanged (SPT 4.1.6 / EFT 0.16.9.5-40743, server
  `~4.1.5`, DynamicMaps 1.2.1). README.md and FORGE.md name four maps (title, map table or bullet list, gunshot-reverb
  sentence and its `Audio (arena_bowl)` key, DynamicMaps sentence, natural-night "unaffected" list, scope sentence).
- What the texts claim, and what each claim rests on, so the release step can re-check it:
  - Flares: Bowl draws none. The four active `FlareLight`s are 771 to 869 m from the play space and the runtime culls beyond
    500 m ([native-multiflare.md](native-multiflare.md), Bowl section). README and FORGE keep "Equator and Bay5 also have
    restored ... lamp flares"; the release notes list it as a limitation.
  - Overview: Bowl has none in the release as the tree stands, and the texts say so. The pack exists offline
    (`work/dynamicmaps/release/ArenaMapPort_Bowl`, [bowl-dynamicmaps.md](bowl-dynamicmaps.md)) but is not staged: the client csproj
    list has 0 Bowl rows, `DynamicMapAssets.Maps` is `{ Equator, Bay5, AirPit }` (the client would not register Bowl even with the
    files staged), `validate_distribution.OVERVIEW_MAP_NAMES` excludes Bowl (staging the files fails it with "Unexpected or
    missing overview map files"), the tests pin that state, and the pack has not been opened in game. The first draft promised the
    overview in README, FORGE and the notes; README, FORGE and `v0.3.0.md` now say that Equator, Bay5 and Air Pit have overviews
    and Bowl has none yet (a Known limitations bullet in the notes). Reinstatement: only after all six "Integration rows" of
    bowl-dynamicmaps.md are applied AND one in-game MAP check passed. Then put back, in `v0.3.0.md`, the Included bullet
    "Optional DynamicMaps overview maps for all four maps ... Air Pit and Bowl show their ground floor; their upper spectator
    areas are omitted.", the Changes bullet "The DynamicMaps overview covers the ground floor: the field, both pockets (drawn
    open), the outline of the Mi-26 and the containers. The stands, the Mi-26's roof and the tower platforms are not drawn." and
    drop the "No DynamicMaps overview for Bowl yet" limitation; in README (DynamicMaps section) and FORGE (compatibility
    paragraph) name all four maps again and say that Air Pit and Bowl omit their upper spectator areas.
  - Gunshot reverb "on all four maps" holds (the switch defaults on; `RoomShotAcoustics.Install` loops
    `ArenaMapRegistry.All`). The Mi-26 room-class change of [shot-acoustics.md](shot-acoustics.md) is not host-verified and the
    texts do not mention it.
  - Decals, ballistic surfaces, light culling, physics props and room acoustics are listed as included because the validated
    build carries them; no host log line for them is quoted in this file yet (PLAN-BOWL item 13 is open).
  - "The scenes and navigation of those three maps are unchanged from 0.2.2" is the plan's rule (their dist hashes stay
    untouched); compare the bundles against the 0.2.2 manifest before the ZIP is cut. The client DLL differs from 0.2.2 by the
    Bowl rows, generalisations whose old-map behaviour is pinned by tests, and the weather sky-provider rebind.
  - Known limitations: no GPUInstancer grass, tree wind not ported (the line below), stands are scenery, the capture point
    (-1.76, -0.05, -33.92) lies under the Mi-26 belly (see "Open for the host" above), no flares, no gate hold has run on Bowl
    (see "Fix round"), no Bowl overview map.

### Identity check (PLAN-BOWL item 16): what exists

- Selection marker: `tools/maps/selection/markers.json` holds the entry `Bowl` / `Arena_Bowl` / `arena_bowl` (screenshot
  `Screenshot 2026-10-13 112305.png`, screen centre 1251.186 / 395.681, `icon_x` 919, `icon_y` 679; the file has 14 markers).
  `ArenaMapDescriptor.Bowl` carries `IconX: 919, IconY: 679` (`ArenaMapDescriptor.cs:658-659`), `tools/maps/bowl.json` carries
  `icon_x` 919 / `icon_y` 679 and `dist/.../db/arena_bowl/base.json` carries `IconX` 919 / `IconY` 679 (read with a Python
  one-liner in this pass). `SelectionMapPositionTests.ScreenshotPositionsMatchCatalogSidecarsAndDatabase` compares marker,
  descriptor, sidecar and database for every catalog map with a marker, so it covers Bowl; it was not run here (no dotnet in this
  pass), the last full run is the 887 passed above.
- Banner: `extract_banners.BANNERS["arena_bowl"]` = `Matchmaker_Banner_TheBowl` (hint path_id 2538). Staged:
  `dist/SPT_Runtime/user/mods/ArenaMapPort/banners/arena_bowl.jpg`, 219,073 B, 769 x 460, SHA-256 `5c433492...bb3ee`, identical
  to the copy under `D:\SPT41`. The server log of the first raid said the banner is wired.
- Loading screens: `extract_loadscreens.LOADSCREENS["arena_bowl"]` lists the four `Arena_MainMenu_Paralax_Bowl-1..4` images
  (path ids 164, 1314, 274, 834; Arena ships no loading screen for Bowl, so there is no fifth). Staged:
  `dist/BepInEx/plugins/maschine-ArenaMapPort/loadscreens/arena_bowl_1..4.jpg`, each 2048 x 1024 (1,055,400 / 1,005,830 /
  973,521 / 1,062,160 B); the four files are also under `D:\SPT41`.
- Official description: `extract_map_descriptions.DESCRIPTION_KEYS["arena_bowl"]` = `62eba95b7ba21c1d534d305a Description`.
  `work/extract/map-descriptions/map-descriptions.json` (1,813 B, SHA-256 `89b9021d...e32d0bb0`) has a non-empty `arena_bowl`
  string in en, ge and ru; it is byte-identical to the staged `locales/map-descriptions.json` in dist and under `D:\SPT41`.
  The BSG-derived text is not copied into this repository's tracked files.
- The region-selection artwork (`map-selection/region-map.png` + manifest) is staged unchanged since 2026-10-13.
- Tests run in this pass: `python -m unittest tools/tests/test_banners.py` Ran 22 tests OK; `test_loadscreens.py` Ran 25 tests
  OK; `test_map_descriptions.py` Ran 18 tests OK.
- Not recorded anywhere yet: the Bowl banner and loading images seen on screen in the menu / loading screen on GPU-2.

### The `Invalid windSettings` line

- What it is: `WindController.SetWind(TreeWind.Settings[])` (decompile `EFT.Weather/WindController.cs:61-76`) logs
  `Invalid windSettings` through `Debug.LogError` when the array is null or empty. The only caller of that overload in the
  0.16.9.5 decompile is `LevelSettings.ApplyTreeWindSettings` (`LevelSettings.cs:306-323`, called from `OnPostLoadingScene`),
  which passes `LocationScene.GetAll<TreeWind.Settings>()`; `LocationScene.FillTreeWinds` (`LocationScene.cs:470-483`) fills
  those presets from the `TreeWind` components of the scene. None of the four accepted packages carries a `TreeWind` component
  (a count of the string in `scenes/*.json`: `equator-native-v5` 0, `bay5-native-v5` 0, `airpit-native-v9` 0,
  `bowl-native-v2` 0), nothing in the importer or the client creates one, so the array is empty on every ported map.
  The wind compute calculator is then simply not created (`_windCalculator` stays null); `ApplyWindToPlants` still sets the
  global wind shader vectors. This is the scenes' missing TreeWind data, not a Bowl defect and not something the weather rig
  supplies (the wording "donor weather rig carries no TreeWind settings" of the task is corrected here).
- Evidence (host EFT `errors.log`, entries stamped +02:00; the folders are the retained session folders under `D:\SPT41\Logs`):
  - `log_2026.10.18_11-24-24...`: `13:25:22.788 ... Error|Default|Invalid windSettings`, followed by `New spatial system created`
    (13:25:22.934), the `maperrors` lines (3 SpawnPointMarker deleted / 21 added, 13:25:23.9) and
    `[Transit] ... Locations:arena_bowl` (13:25:24.674). `application.log`: `scene preset path:maps/arena_bowl_preset.bundle`
    13:25:18.5, `LocationLoaded` 13:25:23.3. This session has exactly one Bowl load and one such line.
  - `log_2026.10.18_11-56-56...`: `14:10:54.674 ... Invalid windSettings`, followed by `New spatial system created` (14:10:54.805),
    `maperrors` (3 deleted / 38 added) and `[Transit] ... Locations:arena_airpit` (14:10:56.837); `scene preset path:maps/
    arena_airpit_preset.bundle` 14:10:51.0, `LocationLoaded` 14:10:55.1. The same session loaded vanilla Factory twice
    (14:01:20 and 14:18:56) and logged the line on neither; the other two retained sessions hold three more Factory loads and no
    such line.
  - [equator-profiles.md](equator-profiles.md) (2026-10-14) already counts it among "EFT's usual ported-map noise" next to
    `WeatherObstacle.Instance is null`, the SpawnPointMarker counts, JetMaterial and the seasons singletons.
- Effect: none visible; the only thing missing is EFT's GPU wind calculation for tree materials, and the ported scenes have no
  `TreeWind` renderers to feed. It is a log line, not a repair target. Do not suppress it (AGENTS.md: never suppress
  Unity/Harmony warnings globally). It is the reason the release notes say tree wind is not ported.
- Clock note, resolved in the fix round: there is one clock, the host's local time (+02:00), and three machines write into the
  mirror. EFT names its session folders in UTC (`log_2026.10.18_11-24-24` = 13:24:24 +02:00; the lines inside carry the +02:00
  offset), the BepInEx logs under `D:\SPT41\BepInEx` are named by host and local time, and `BepInEx/logs/current-<host>.txt`
  records "local = UTC (UTC+02:00)" per host. Mapping (BepInEx start vs EFT session start):
  - `log_2026.10.18_11-24-24` (13:24:24) = MASCHINE11, a third machine, not GPU-2: `current-MASCHINE11.txt` says session start
    13:24:10, `LogOutput-MASCHINE11-2026-10-18_13-24-10.log` holds the Bowl raid (`[TimeOfDay] arena_bowl` 13:25:23.802, `added
    navmesh for 'arena_bowl': 12704 vertices, 5890 triangles` 13:25:27.089, the same U1 bake). So the Bowl `Invalid windSettings`
    line quoted above comes from MASCHINE11's raid at 13:25.
  - `log_2026.10.18_11-56-56` (13:56:56) = GPU-2 (`BepInEx/logs/LogOutput-SPT-GPU-2-2026-10-18_13-56-45.log`, which holds the AirPit
    `[TimeOfDay] arena_airpit` 14:10:55.531 next to the EFT line at 14:10:54.674); `log_2026.10.18_12-59-32` (14:59:32) = GPU-2
    (`..._14-59-20.log`); `log_2026.10.18_14-19-01` (16:19:01) = GPU-2 (`current-SPT-GPU-2.txt`, 16:18:49).
  - GPU-2's raids 1 and 2 are in `BepInEx/logs/LogOutput-SPT-GPU-2-2026-10-18_12-38-36.log` (`[TimeOfDay] arena_bowl` 12:39:44.822 and
    12:43:34.279, `added navmesh ... 12704 vertices, 5890 triangles` 12:39:48.002 and 12:43:36.699). The EFT session folder of that
    process (`log_2026.10.18_10-38-xx` in UTC) is no longer retained, which is why the raid-1 `Invalid windSettings` line rests on
    the errors.log read at the time (see the raid 1 section) and the retained Bowl proof is MASCHINE11's.

### Also seen in the same Bowl errors.log (not analysed, recorded for item 13)

- `13:30:22` `Coroutine couldn't be started because the game object 'MenuScreen' is inactive!`, then at `13:30:27.826` an EFT
  exception `assets/content/audio/prefabs/shells/weaponsounds.bundle is not loaded` from `SmallPhysicsObject.SetupSoundBank`
  <- `OnCollisionEnter` <- `Physics.Simulate` (`Simulation.Update`). A Bowl physics prop collided after the raid appears to have
  been left. `PhysicsPropSoundRepairs` is gated on `ArenaMapRaid.IsLoaded()`, which is false then, so vanilla's lazy
  `SetupSoundBank` ran and threw. No `[ArenaMapPort]` line accompanies it, and the AirPit session has no such exception. Whether
  this is the same teardown noise as the `SmallPhysicsSystem.Update` line of two-maps-item-5-physics-props.md (Equator, 2026-09-25)
  or a Bowl-specific case (341 props) is open.

### Fix round (same day): five corrections to the first draft

- "Football pitch" removed from README, FORGE and `v0.3.0.md` (they say "sunken stadium field" or "field"). Nothing supports a
  pitch: `grep -rniE 'football|soccer'` found only the three new lines, the scene objects carry no pitch/turf/goal names (only
  `Stadium_chair_*`), the descriptor text says "the sunken field of an old sports arena", and the staged loading images
  `arena_bowl_1.jpg` and `arena_bowl_3.jpg` show a worn dirt and dry-grass field with sandbags, containers and the Mi-26, no pitch markings.
- Overview promises removed (details in the Overview bullet above): the pack is not staged, `DynamicMapAssets.Maps` and
  `validate_distribution.OVERVIEW_MAP_NAMES` do not know Bowl, and nothing was opened in game.
- Gate hold: the notes said the hold "has not been exercised with ArenaMode on Bowl", which implied that only ArenaMode is missing.
  No hold of any kind has run on Bowl. `docs/progress/spawn-gates.md` records its host evidence from the developer test hold
  (`Spawns/SpawnGateHold`) on other maps; `grep -n -i 'SpawnGate\|hold' docs/progress/bowl-release.md` shows only descriptor and plan
  mentions (`StartGates` = `DOORS_SPAWN_A`, `DOORS_SPAWN_B`, `HeldGates` empty) and the raid-1 opening of both barriers. The carving,
  the sealing probe and the re-opening on Bowl's four gate parts are untested; the notes say exactly that. Host check when wanted:
  a Bowl raid with `Spawns/SpawnGateHold` on, expecting the "held closed" and "released" lines and the sealing probe of
  [spawn-gates.md](spawn-gates.md).
- Border cubes: the notes said they "are also part of the navigation mesh". They are bake inputs, not mesh: `navmesh_bake_layers`
  of `tools/maps/bowl.json` is [9, 11, 18, 29, 30] (29 = LevelBorder), so the 354 cubes are baked as solid colliders and the mesh
  ends at them; the known cost is about 608 m2 of unreachable cube-top islands (Bake-input analysis above). The notes say "The
  navigation mesh ends at them".
- Clock note resolved (see above). Also reworded: bots "patrol the field along a patrol way built from Arena's own patrol points"
  (the extraction turned Arena's 86 PatrolPoints into 7 hubs and 79 sub points; the 17 bot spawns sit on Arena's core points).
- Not changed, for the integrator: `NativeMultiFlare.Install` in the working tree loops Equator, Bay5 and Bowl and binds
  `NativeMultiFlare` (default true) for Bowl, but no Bowl flare package is staged or validated (`FLARE_MAP_KEYS` is Equator and Bay5,
  the csproj has no `flares/arena_bowl` rows). A Bowl raid would then try to read `flares/arena_bowl/flares.json`, a file that is not staged, and `Fail` would log an Error (the zero-warning state of today's Bowl raids would be lost). The texts say Bowl
  draws no flares, which holds either way; the consistent choices are to drop Bowl from the loop or to stage the package together with the
  `FLARE_MAP_KEYS` change and the csproj rows of [native-multiflare.md](native-multiflare.md).

## 2026-10-18 - polish integrated: flares, overview map, shot-acoustics classes, release texts, 0.3.0 version

Integration pass after the four polish areas (MultiFlare, DynamicMaps overview, shot-acoustics classes, release texts) and their
reviews. Build and test pass only: nothing was deployed, nothing was committed, no host raid ran. The client DLL, the server DLL
and dist/ are 0.3.0 now; the release is not cut.

### What was integrated

- Version 0.2.2 -> 0.3.0 in `ArenaMapPort.Client.csproj` and `ArenaMapPort.Server.csproj` (`<Version>`), `Plugin.PluginVersion` and
  `ModMetadata.Version`. `grep -rn 0.2.2` over `*.cs/*.csproj/*.py/*.ps1/*.json` found only these four spots and a descriptor
  comment ("Equator, Bay5 and AirPit are the released ones (0.2.2)", still true); no test pins the version.
  `make_release_zips.py --plan` reads both csproj versions and reports `version 0.3.0`.
- Both csproj `<Description>` texts name the four maps ("Equator, Bay5, Air Pit and Bowl").
- MultiFlare (area A): client csproj target `ArenaMapPortStageMultiFlare` got the Bowl `<Error>` row and the Bowl
  `ArenaMapPortFlareFile` item (`flares.json` + `textures\*.bin`, `MapKey` `arena_bowl`); `validate_distribution.FLARE_MAP_KEYS` is
  `(arena_equator, arena_bay5, arena_bowl)`; `NativeMultiFlare.cs` (area A, already in the tree) binds `Lighting (arena_bowl)`.
  The review's order requirement holds: C# change, csproj rows, validator tuple and tests landed together, so a Bowl raid finds
  `flares/arena_bowl/flares.json`.
- DynamicMaps overview (area B): client csproj target `ArenaMapPortStageDynamicMaps` has the two Bowl rows (eight overview files),
  `DynamicMapAssets.Maps` is `{ Equator, Bay5, AirPit, Bowl }`, `validate_distribution.OVERVIEW_MAP_NAMES` is all four maps,
  `AGENTS.md` names `-DynamicMapsMap Equator|Bay5|AirPit|Bowl`. The deploy script already accepted `Bowl` (area B).
- Tests moved with the rows: `tools/tests/test_validate_distribution.py` (fixture writes the Bowl overview files and a Bowl flare
  package with scene `Arena_Bowl_Light`; the Bowl test is renamed `test_bowl_carries_four_loadscreens_an_overview_and_flares_but_no_water`
  and now requires the pack and the package exactly: stray overview file, missing overview file, stale overview bytes, missing flare
  texture, stray flare file are each refused; the water refusal stays) and `ArenaMapPort.Server.Tests/DynamicMapAssetsTests.cs`
  (`[InlineData("Bowl")]`, the reload count is `DynamicMapAssets.Maps.Length`, the missing-pack list is `Bay5, AirPit, Bowl`).
- Shot-acoustics classes (area C: Mi-26 IndoorMetalThick room of 15 m or more is Metal) needed no integration row; its tests are in
  the 903 below.
- No edit needed (read, not changed): `tools/release/make_release_zips.py` (its flare and dynamicmaps rules derive from
  `RELEASE_MAP_KEYS/NAMES`; the real dist classifies as `86 files, 0 unclassified, parts core=5, equator=20, bay5=25, airpit=17,
  bowl=19`), `tools/park-unreleased-dist.ps1` (Bowl left its list on the promotion, nothing of Bowl is parked: `PARKED_DIST_OK
  Client 0 files`, `Server 0 files`), `test_release_zips.py`, `test_park_unreleased_dist.py`.

### Evidence

- `dotnet build ArenaMapPort.Client/ArenaMapPort.Client.csproj -c Release` (full, not DllOnly): `0 Warnung(en)  0 Fehler`, 12.65 s.
  It printed the `[ArenaMapPort] staged ...` lines for all 12 bundles (`arena_bowl_scenes/navmesh/preset.bundle`) and the four
  `material-shaders.arena_<key>.json`. The flare and overview `Copy` tasks print nothing at this verbosity; the proof is the dist
  listing and the hash lines below. `PARKED_DIST_OK Client 0 files`.
- `dotnet build ArenaMapPort.Server/ArenaMapPort.Server.csproj -c Release`: `0 Warnung(en)  0 Fehler`, 5.06 s; `PARKED_DIST_OK Server 0 files`.
- `dotnet test ArenaMapPort.Server.Tests/ArenaMapPort.Server.Tests.csproj -c Release`:
  `Bestanden!   : Fehler: 0, erfolgreich: 903, übersprungen: 0, gesamt: 903` (887 in the last U1 pass above; the 16 more are
  area C's room-class tests and the new Bowl theory case of `DynamicMapAssetsTests`). The nullable-warning list of that build comes from the linked client sources in the
  test project and is not new.
- `python -m unittest discover -s tools/tests -p "test_*.py"`: `Ran 1818 tests in 174.461s`, `OK (skipped=55)`. The 55 skips are
  26 `DynamicMaps exporter requires optional numpy and shapely`, 10 `optional cartography dependencies unavailable`, 9 Bay5 exporter
  and 7 Bay5 package tests (numpy/shapely/Pillow), and one each for the superseded AirPit v8 plan, the already-filtered Lighthouse
  set and the full-exporter fixture. Re-run with the optional-dependency venv:
  `work/dynamicmaps/venv/Scripts/python.exe -m unittest discover -s tools/tests -p "test_dynamicmaps*.py"` -> `Ran 53 tests ... OK`
  (includes the 9 `test_dynamicmaps_bowl.py` tests), and `test_multiflare_extraction.py` + `test_validate_distribution.py` -> `Ran 44 tests ... OK`.
- `pwsh -NoProfile -File tools/tests/test_dynamicmaps_deploy.ps1`: `DYNAMICMAPS_DEPLOY_TESTS_PASSED 122 cases`.
- `python tools/validate/validate_distribution.py`: `[validate_bundle_names] OK -- 12 bundle(s), every internal directory name is
  unique`, then `DISTRIBUTION_OK 4 maps 12 bundles 4 sidecars`.
- Staged Bowl additions equal their sources: `flares/arena_bowl/flares.json` SHA-256 `6090e9e448b33d186cd2581d372fa4fb21630cfc2178b1e3acfcf22f9b3fc2a2`
  (= `work/extract/multiflare/arena_bowl/flares.json`), `ArenaMapPort_Bowl.jsonc`
  `0a7601772705111075f4006412991bf917d52f8b4d4c61aee5c4138991de53e3`, `Ground.svg`
  `138750ffc16aaef073c74bd2ac5579f1b4c00b839ba680889b10a96d079064dd` (= `work/dynamicmaps/release/ArenaMapPort_Bowl/...`). The two Bowl flare
  textures have the same SHA-256 as Equator's and Bay5's (`4a2028a2...76601d`, `d04a4cfb...d95170`).
- DLLs: client 819,200 B, SHA-256 `8AFF8B2DFD5417A780BD61E761491391952FA594D2DF394CB4D0B83DFA3855F3`; server 74,240 B,
  `7662D8BBFB5390C277ABE8D5C10FCBA5490AC5A7741747FE9D53F945CDFAE473`; both `FileVersion 0.3.0.0`, `ProductVersion
  0.3.0+5f0d479ce8fce2529d390c15a7063216efbd67c2` (the commit is HEAD; the tree is dirty, so a build for the release is made after
  the commit).
- `python tools/release/make_release_zips.py --single --plan` (writes nothing): `version 0.3.0`, `86 dist files, raw 3,256,514,833 B
  (3,105.7 MiB), estimated zip 3,148,925,260 B`, `estimated single ZIP: 2.93 GiB`. Multi-part `--plan`: 5 parts (core 5 files, equator
  20, bay5 25, airpit 17, bowl 19), the largest `maschine-ArenaMapPort-0.3.0-part5of5-bowl.zip` ~964,531,607 B, all below 2 GiB.
- `pwsh -NoProfile -File tools/deploy-to-spt.ps1 -SptRoot 'D:\SPT41'` (dry run, no `-Confirm`, nothing copied): `plan: 86 file(s),
  3105.7 MB total -- 5 new, 2 to overwrite, 79 already identical`; the 5 new are `ArenaMapPort_Bowl.jsonc`, `Ground.svg`,
  `flares.json`, `resources.assets_2969.bin`, `sharedassets4.assets_11.bin`; the 2 overwrites are the two DLLs.
- `git status --porcelain`: only tracked sources, tests and docs (25 modified, 3 untracked); nothing from `work/` or `dist/`.

### dist listing (BepInEx/plugins/maschine-ArenaMapPort)

```
flares/arena_bay5/flares.json                                  109,370 B
flares/arena_bay5/textures/resources.assets_2812.bin           262,144 B
flares/arena_bay5/textures/sharedassets4.assets_11.bin       1,048,576 B
flares/arena_bowl/flares.json                                  149,472 B
flares/arena_bowl/textures/resources.assets_2969.bin           262,144 B
flares/arena_bowl/textures/sharedassets4.assets_11.bin       1,048,576 B
flares/arena_equator/flares.json                               115,141 B
flares/arena_equator/textures/resources.assets_2812.bin        262,144 B
flares/arena_equator/textures/sharedassets4.assets_11.bin    1,048,576 B
dynamicmaps/ArenaMapPort_AirPit/ArenaMapPort_AirPit.jsonc        1,691 B
dynamicmaps/ArenaMapPort_AirPit/Layers/Ground.svg              232,927 B
dynamicmaps/ArenaMapPort_Bay5/ArenaMapPort_Bay5.jsonc            1,704 B
dynamicmaps/ArenaMapPort_Bay5/Layers/Ground.svg                207,192 B
dynamicmaps/ArenaMapPort_Bowl/ArenaMapPort_Bowl.jsonc            1,671 B
dynamicmaps/ArenaMapPort_Bowl/Layers/Ground.svg                214,968 B
dynamicmaps/ArenaMapPort_Equator/ArenaMapPort_Equator.jsonc      1,688 B
dynamicmaps/ArenaMapPort_Equator/Layers/Ground.svg             153,567 B
```

There is no `flares/arena_airpit` and no `water/arena_bowl`, as intended.

### Inconsistencies and findings the integrator could not fix inside its files

- **Release texts vs dist.** README.md (line 84-89), FORGE.md (line 33-35) and `docs/releases/v0.3.0.md` (line 20, line 76) say that
  Bowl has no DynamicMaps overview. With this integration dist, the client and the validator carry one. Either the texts get the
  reinstatement of the "Overview" bullet above (after an in-game MAP check) or the Bowl overview rows are taken out again; staying
  as it is would ship an undocumented, never-opened map pack. The flare statements of the texts ("No lamp flares on Bowl") stay true.
- **Bowl flares draw nothing.** The 4 active lights are 771 to 869 m from the play space and `NativeMultiFlare.Update` keeps lamps
  beyond their 500 m cull distance off. The package is staged only so the Bowl raid does not log an error. A raid should show the
  F7 probe line `[MultiFlare] arena_bowl lights=4` and four `lamp=... distance=` lines; a visible glow needs a map-specific cull
  rule, a separate decision.
- **Doc corrections owed by their owners** (not made here, outside the integrator's file list): `docs/progress/native-multiflare.md`
  section "Open: the four active lights are outside their own cull distance" says the elements "also fade within 0.1-40 m". The
  review found no support: EFT's `Flare.cs` tooltip and `PrepareGpuData` give MaxScale/MaxAlpha at and beyond MaxDist, and the
  package has element 1 `max_alpha` 1.0, element 2 0.3. Only the 500 m cull is established. `docs/progress/bowl-dynamicmaps.md`
  says the walkable ground spans x -63.84..59.64, z -79.45..8.75 (that is the anchored pitch component; the muted ground reaches
  x -66..62, z -81.7..10.9) and that core-12/13/14 stand "under" the helicopter (their y is 2.05, 1.81, 1.64: on the Mi-26 roof cells);
  the 3.5 / 5.1 / 1.6 m distances stand.
- **Release size.** The single ZIP estimates at 2.93 GiB (0.2.2: 2.00 GiB). Pixeldrain takes it; a channel with a 2 GiB file cap
  needs the multi-part layout (5 parts, largest ~0.92 GiB).

### Still needs the host (GPU-2, with the 0.3.0 DLLs and the five new files)

1. Deploy (dry run first, then `-Confirm`; client DLL needs an EFT restart, server DLL a server restart) and confirm the new
   client MVID with `uibridge_status`; check the server DLL hash on the host before the restart.
2. Bowl raid: `[ArenaMapPort][MultiFlare] arena_bowl` must not log an error; F7 `[MultiFlare] arena_bowl lights=4` with the four
   `distance=` lines (expected beyond 500 m, nothing drawn); no `disabled after error`.
3. DynamicMaps (1.2.1): open inventory -> MAP in a Bowl raid: the pack registers (no `reportMissing` line), the ground-floor
   plan is drawn, the player arrow sits correctly at several positions (spawn pockets, the Mi-26, the containers), and read the
   sprite vertex count (`MapLayer.Image.sprite.vertices.Length`, estimated ~35,000 against the 65,500 limit). Then decide on the
   release texts.
4. Stand inside the Mi-26 (room 4, graph id 563) and read the `[RoomShotAcoustics]` line: expected class Metal (before: Shop);
   the containers must still read Metal, the Offices rooms Shop, the FanZone Hall, the field Outdoor.
5. F7/F8/F10 diagnostics as in the earlier Bowl raids; `uibridge_status` `max` frame time; the hideout opened once.
6. Cross-map raids on Equator, Bay5 and AirPit with the same DLL (flares on Equator and Bay5 unchanged, AirPit and Bowl overview
   present), then the 0.3.0 package cut from a committed tree.

## 2026-10-18 - host verification of the 0.3.0 build on GPU-2 (raids 3-5, hideout) and the user's own Bowl raid

Deploy: the clean-HEAD rebuild (`185f8f8`, both DLLs `FileVersion 0.3.0.0`, `ProductVersion 0.3.0+185f8f8...`) went into
`D:\SPT41` with `deploy-to-spt.ps1 -Confirm` (86 files: 5 new = Bowl flares and overview, 2 overwritten = the DLLs, 79
identical). `gpu-run deploy-wait --server --force --game` stopped the SPT server, saw the 7 files identical on the host after
28 s and then failed on a busy EFT main thread (the old process froze 8 s when its server vanished); `gpu-run restart --force`
(78 s) started the server and a fresh EFT: `17/17 identical`, server log `Mod: ArenaMapPort Version: 0.3.0 ... geladen`
and `Arena: Bowl is available in location selection` (17:41). All three raids and the hideout ran in that one process
(instance 4eeae3d5), with `General/DeveloperMode = true` on the host for the F7/F8/F10 probes. The `[ArenaMapPort]`
Warning/Error query over the whole process stayed at 0 lines; no `disabled after error` line (the Bowl flare package loads).

- **Raid 3 (17:42-17:46, Bowl, no bots, `--clock 00:30`, the raid's own weather: rain 0.25).** `[MultiFlare] arena_bowl
  enabled: sourceLights=28 activeLights=4 flares=20 boundOriginals=4 nativeBatches=3 originalTextures=2`. F7 (`press-key`
  scenario through the host's Windows MCP): `[MultiFlare] arena_bowl lights=4 cameras=1 ... intensity=0.500` and the four
  `lamp=SBG_Bowl_Light/OO/BACK_FLARES/Flare_City_background ... distance=788.91 / 790.53 / 880.99 / 887.65`, all beyond the
  500 m cull, as predicted: the package binds and draws nothing. `[LightProbe] arena_bowl camera=(55.82, 1.53, -5.85)
  CullingManager=True cullingLights=96 visible=24 | inside fade start: full=12 ... beyond fade end: zero=68`, `volumetric:
  setting=True components=4 activeAndEnabled=4`, flicker 4 enabled, HDR/Prism/UltimateBloom lines as on AirPit.
  `[Acoustics] arena_bowl: prepared rooms=11, portals=19, bake pairs=55, routes=196` and `native initialized`; room tones
  started after the audio reset. **Shot-acoustics room classes** (teleports, each followed by the `[ShotAcoustics]` line):
  spawn `room=8/OutdoorCommon class=Outdoor ... model=AcousticRayTracing`; Mi-26 (1.1, 1.6, -33.5) `room=4/IndoorMetalThick
  class=Metal dims=20.8x4.5x13.8 materials=Steel,Steel,Steel,Carpet,Steel,Steel clutter=0.70 model=ShoeboxRoom` (the new
  rule; the user's own raid at 13:27 still showed `class=Shop` with GypsumBoard); container (34.1, 0.2, -55.1)
  `room=1/IndoorMetalThin class=Metal dims=8.2x2.7x7.0`; office (0.9, 2.8, -13.4) `room=3/IndoorWoodThick class=Shop
  dims=14.4x2.5x2.3 materials=GypsumBoard,...`; back on the field `room=8/OutdoorCommon class=Outdoor`. `[RoomReverb]`
  followed each change (`IndoorMetalThick, source category=IndoorGeneric`). F8 inside the container:
  `[RainCoverLab] RainCover depth readback @ (33.95, 1.72, -55.10): ... height(reversedZ)=2.78` (the container roof covers).
  **DynamicMaps** (`map-check` scenario: Tab, MAP tab): DynamicMaps loaded `ArenaMapPort_Bowl\ArenaMapPort_Bowl.jsonc` and
  precached `Layers\Ground.svg` at startup, 0 DynamicMaps warnings; the MAP tab showed `BOWL / GROUND` with the player arrow
  at (50.20, -3.30), the `Bowl BTR Gate` exit marker, the containers, the pockets and the Mi-26 outline (still
  `bowl-u3-map`, desktop screenshot `bowl-map-screen.png` in the scenario report); `uibridge_inspect` on
  `DynamicMaps.UI.Components.MapLayer` `Ground Floor`: `Image.sprite.vertices.Length = 35530` (limit 65,500; the estimate was
  ~35,000). Still `bowl-u3-night-spawn`: the stadium at 00:30 is very dark (a few small lamps, the exit sign; Arena's Bowl is
  a daytime map, the night comes from the server-time choice as on Bay5/AirPit). 120 fps, max 8.9 ms.
- **Raid 4 (17:46-17:48, Bowl, no bots, `--clock 12:00 --weather rain`, rain 0.7 override).** F8 at the spawn (50.00, 1.53,
  -3.29): `height(reversedZ)=0.01` = open sky, rain falls (still `bowl-u3-rain-spawn`: wet grey field, fine streaks in the
  crop, exit list EXFIL01 Bowl BTR Gate / EXFIL02 Bowl Army Gate). `[RainCover] ... RoofDepthBlack scan: keptForBounds=19815
  consideredForCover=17946 elevated=17921 excluded(outsidePlaySpace)=3096 excluded(material)=1869 ... drew 17921 elevated
  mesh(es) (28580 submesh draw call(s))`. F10 from the spawn: `Physics.Raycast: hit 'Arena_Bowl_LevelBorders/Cube (129)'
  dist=12.13 layer=LevelBorder ... ballistic=<none>` (the kept border cubes carry no ballistic material, as in Arena) and
  the renderer dump behind it (`p0/Reflective/Bumped Specular SMap ...`). Max frame 11.2 ms.
- **Raid 5 (17:49-17:51, Air Pit after two Bowl raids, no bots, 12:00 clear).** 0 warnings; `[NavMesh] added navmesh for
  'arena_airpit': 11787 vertices, 5423 triangles`; `[Acoustics] arena_airpit: prepared rooms=24, portals=94 ... routes=16304`;
  `[SpawnGates] navmesh installed: 16 carver(s) ... 36 held-gate part(s)` and `navmesh probe: every probed gate sealed (16
  probed)`; TeamFight borders 67 removed (RemoveTeamFightBorders=True, AirPit's own default); still `airpit-after-bowl-030`
  (passport hall, correct). 120 fps, max 8.4 ms. (The first `gpu-run raid --map airpit` call stopped on SelectLocation: the
  tile is `ARENA: AIR PIT`, so the map argument must be `'air pit'`; `back-to-menu` scenario, then the retry.)
- **Hideout** opened once after the three raids (`hideout-open` scenario, screen `Hideout`, 31 labels, `logAbsent` error
  passed) and closed with BACK (`allowDangerous: true`, the runner's own list).
- **The user's own raid (MASCHINE11, 13:24-13:30, ArenaMode teamdeathmatch on Bowl, 0.2.2-era client of the U1 deploy):**
  `LogOutput-MASCHINE11-2026-10-18_13-24-10.log` on the shared install: 0 ArenaMapPort warnings; `[Acoustics] arena_bowl:
  prepared rooms=11, portals=19`; NavMesh 12704/5890; `BotZone.Init ... markers=17 onNavMesh=17`; `[NativeAudio] ready
  players=33 started=29`; `[RoomReverb]` Outdoor -> `IndoorMetalThick` at 13:27:03 (the Mi-26) with the old `[ShotAcoustics]
  ... class=Shop ... GypsumBoard` line, which raid 3 above now shows as `class=Metal`.
- Release texts: the overview wording is reinstated in README, FORGE and `v0.3.0.md` (recipe above), the Mi-26 class
  change has its own Changes bullet. Still open on the host: the spawn-gate hold on Bowl (no hold has run yet), the east
  guard-tower stairs and vault limits, the capture point under the Mi-26 (ArenaMode's concern).

## 2026-10-18 - raid 6: the spawn-gate hold on Bowl (developer test hold, GPU-2)

Host developer config set live through `uibridge_config_set` (`com.maschine.ArenaMapPort` / `Spawns`): `SpawnGateHold true`,
`SpawnGateHoldSeconds 60` (the config lease blocks `gpu-run raid` for 90 s with a 409: wait it out before the recipe), restored to
`false` / `30` after the raid. Raid 6 (17:55-17:58, Bowl, no bots, 12:00 clear, same process 4eeae3d5):

- Hold: `[RaidFix] spawn openings: spawn barrier 'DOORS_SPAWN_A' ...: deactivated 0 part(s), 0 already off, kept 0 frame part(s),
  2 part(s) held closed (4 part(s) registered; developer test hold 60 s)` and the same for `DOORS_SPAWN_B` (both passes);
  `[SpawnGates] navmesh installed: 28 carver(s) over 28 movement collider(s) of 4 gate part(s) (28 carver(s) in place, 0
  held-gate part(s)); 1 doorway(s) already disconnected on the map's own navmesh (not judged)`; `navmesh probe: every probed gate
  sealed (1 probed, 2 inconclusive)`: `Gates_Army_01` (-51.7, 1.2, -54.7) `PathPartial`, `BTR_82_closed` and
  `Bowl_Respawn_Ambulance_Animated Group` `no navmesh within 1.5 m of a probe point` (vehicle bodies, their probe points sit off
  the mesh). `uibridge_teleport dryRun` from the east spawn (56.0, 0.0, -5.8) to the field (2.5, 0.1, -21.1): `not reachable on
  foot` twice during the hold (the pocket is sealed for bots and the player alike).
- Release: `[SpawnGates] released (developer test hold, 60.0 s after the raid timer started): 0 leaf(s) slid 1.38 m in 1.0 s, 4
  gate part(s) switched off, 28 carver(s) removed` (17:57:05), then `navmesh probe: every probed gate open (3 probed)`:
  `BTR_82_closed` (51.2, 0.1, -23.0) -> (51.2, 0.1, -20.0) `PathComplete, 2 corners`, `Gates_Army_01` `PathComplete`,
  `Bowl_Respawn_Ambulance_Animated Group` (-43.7, 0.0, -69.8) -> (-40.7, 0.0, -69.8) `PathComplete`; the same dryRun to the field
  then passed (`would teleport to (2.5, 0.0, -21.1)`). No ArenaMapPort warning. 120 fps, max 8.4 ms.
- Conclusion: Bowl's two pocket gates hold and release like the other maps' (PLAN-BOWL item 19's SpawnGates side is covered);
  the release notes' "Pocket gates" limitation is withdrawn. ArenaMode's own hold/release on Bowl is still the user's.

## 2026-10-18 - 0.3.0 candidate cut (not published)

`python tools/release/make_release_zips.py --single --extract-check work/releases/0.3.0-extract-check` on the clean tree at
`26b9216`: `writing single ZIP (full): 86 files, 3,256,514,833 raw bytes`, `maschine-ArenaMapPort-0.3.0.zip 3,078,043,845 B
8f9b65913501adda2206bbd60919e990e3ca312faa7db0f0a8d812a2edddeb8b`, `single ZIP: 2.87 GiB`, `verified single ZIP`,
`extract-check OK: 86 files identical to dist, 4 generated entries ignored`, `RELEASE_ZIPS_BUILT`. (The first call without the
scratch directory argument stops at argparse: `--extract-check` needs an EMPTY directory.) Pre-cut comparison with the final 0.2.2
ZIP's `parts/full.json` (source `326d71b`): identical=64, changed=3 (`maschine-ArenaMapPort.Client.dll` 815,104 -> 819,200 B,
`maschine-ArenaMapPort.Server.dll` 73,216 -> 74,240 B, `locales/map-descriptions.json` 1,432 -> 1,813 B); every bundle, overview,
acoustics, flare and art file of the three released maps is unchanged, as PLAN-BOWL item 18 requires. The ZIP ships the DLLs
that ran raids 3-6 and the hideout on GPU-2 (`185f8f8`; `3e983c3` and `26b9216` changed only docs). Not done, by design: upload,
VirusTotal, tag, GitHub release, the merge of `feature/bowl` into `main` (a fast-forward: `main` is at `a3188cd`, the merge base),
the Forge entry and the `D:\SPT41vanilla` deploy; all of these wait for the user's decision. `docs/releases/v0.3.0.md` carries the
size, SHA-256 and source commit; the Pixeldrain and VirusTotal placeholders remain.

## 2026-10-18 - raid 7: guard towers and the BTR gate extraction (GPU-2, after the merge into main)

EFT had been closed on the host at 19:44 (the user's session); `gpu-run start` (52 s, 17/17 identical, the 0.3.0 DLLs) and
`gpu-run raid --map bowl --bots none --clock 12:00 --weather clear` (21:58). 0 ArenaMapPort warnings.

- Tower positions read live (`uibridge_inspect gameObject` + `Transform.position`): big towers `SBG_Arena_Bowl/SOO_LOD0/
  Guardian_Tower` at (53.4, -64.2) (east, south of the east pocket) and `Guardian_Tower_1` at (-58.1, -9.1) (west); small towers
  `OO/PROPS/FRONT/Guardian_Tower_Small` at (51.5, 3.5) and `OO/PROPS/BACK/Guardian_Tower_Small (1)` at (-53.9, -76.7).
- **East guard-tower stairs: walkable.** `uibridge_teleport dryRun` to (53.4, 4.5, -64.2) snapped to a reachable NavMesh point
  (51.6, 4.9, -64.6); `uibridge_walk` from the east spawn arrived there after 8.8 s (61.6 m straight, 71.8 m along the navmesh),
  `uibridge_player` then reported (51.3, 4.8, -64.8): the stairs are part of the connected mesh, bots can take the tower (still
  `bowl-u3-east-tower`, looking up through the tower's upper deck). The west big tower has no NavMesh within 2 m at y 3.0, 4.5
  and 7.0 (its platform is not in the bake band or not baked), and both small towers have a NavMesh patch at y 4.5 that is
  "not reachable on foot" (islands without a baked stair). Vault limits above 1.2 m remain untested (needs player input).
- **BTR gate extraction: works.** Teleport into the `Bowl BTR Gate` box at (51.0, 0.1, -31.0) from the tower; `uibridge_wait
  inRaid=false` matched after 5.7 s (the extraction timer ran from the teleport into the box; the wait was issued a few seconds
  later), the result screens followed (`gpu-run menu`: ExitStatus -> KillList -> SessionStatistics ->
  SessionExperience -> MainMenu). With raid 1's Army gate walk both exits have extracted a player once.
