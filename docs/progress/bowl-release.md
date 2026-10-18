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
- Not done in this session: the hideout open check after the Bowl client build (needs the Windows MCP clicks;
  `windows-gpu-2` did not connect) and the F8 rain readback (both raids were dry). Items 11/12 of PLAN-BOWL are
  otherwise satisfied by U1: spawns, gates, borders, exits, bots, audio, lighting and the stand-in all behaved on the
  first build; no U2 is needed for playability.
