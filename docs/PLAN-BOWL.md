# Plan: Bowl — Fourth Release Map at AirPit Quality

Source: four read-only surveys run 2026-10-18 (spike state, Arena 0.6.0 source, pipeline gap against the AirPit
precedent, plus the unrelated passport-lamp question) and their synthesis; probes used UnityPy read-only on the live
Arena install (0.6.0.47752, Unity 6000.3.6f1) and on `work/extract/bowl`. Evidence is quoted as file:line, counts or
object paths; inferences are marked **(inferred)**. Status lives in [HANDOFF.md](../HANDOFF.md) and the progress log
[docs/progress/bowl-release.md](progress/bowl-release.md); this file is the plan, not the record. Work happens on the
branch `feature/bowl`, fast-forwarded into `main` on 2026-10-18 evening (user decision 2026-10-18: "mit der Bowl-Umsetzung starten, so wie du es am sinnvollsten
erachtest").

Bowl today (2026-09-15 spike): package `work/extract/bowl` (Arena 0.5.4 source, 80,163 objects, no
`native_features`), bundles archived under `work/build/bowl-spike-20260915/` (scenes 768,406,926 B, renderer-union
bake 51,335 vertices / 22,879 triangles), `db/arena_bowl/base.json` a placeholder (`BotMax 0`, no spawns, exits or
waves, name "Arena: Bowl (spike)"), never played on a host (`docs/progress/remaining-maps.md:79`), registered only in
`ArenaMapRegistry.Catalog` (`ArenaMapDescriptor.cs:638-653`), parked out of `All` and out of `dist/`. The plan
`tools/extract/plans/bowl.json` still verifies against the live 0.6.0 install; only Arena_Bowl_DesignStuff drifted
(15 added and 2 moved layer-29 border cubes).

## 0. Decisions (taken by the agent under the user's "as you see fit", 2026-10-18)

- **Source = the live Arena 0.6.0 install, re-extracted with `--native-features`.** Package naming
  `work/extract/bowl-native-v<N>` (v1 = native features without ambient audio, v2 = v1 + ambient audio, as AirPit's
  v5/v6). The 0.5.4 spike package `work/extract/bowl` is never an `--output` again; the spike bundles are a geometry
  reference only.
- **Mode = TeamFight** (the plan's `source_mode`; 20 spawn markers in two pockets, two gated team boxes, one capture
  point), like the three released maps. The TDM set (83 markers, 3 capture points) is noted for ArenaMode later.
- **Time and weather = `UsesServerTime`** like Bay5 and AirPit: no Directional light exists in the imported scenes
  (Bowl's sun and sky sit in the excluded Scripts scene), so the WeatherRig supplies both.
- **Grass is not ported.** `Arena_Bowl_Grass` is a GPUInstancer scene (prototypes in sharedassets95, placement
  **(inferred)** in `StreamingAssets/Grass/ArenaGrassPrefabs_Bowl.pcl`); no importer handles it and the shipped docs
  already list GPUInstancer grass as excluded. Recorded as a known limitation.
- **Trees, impostors, billboards, window breakers and the stands crowd stay as the extractor leaves them** (same as on
  the released maps; the 18 `filbert_medium01_Billboard` renderers are disabled in the source anyway).
- **Border policy = keep Bowl's layer-29 LevelBorder cubes** (312 in `Arena_Bowl_LevelBorders` of the base scene, 42
  under `Arena_Bowl_LevelBorders_TeamFight`): they are the stadium's only play-area bound (Arena's culling polygon is a
  coarse placeholder, so `authored_borders` is not available). They stay active at raid start and are baked as walls,
  which needs a per-map switch in `MapBorderRepairs` and `BakeNavMesh` (item 7). Cubes inside the gate roots open with
  the gates.
- **Loading art = the four `Arena_MainMenu_Paralax_Bowl-1..4` images** (Arena ships no loading screen for Bowl);
  banner `Matchmaker_Banner_TheBowl`; official description key `62eba95b7ba21c1d534d305a Description`.
- **MultiFlare, DynamicMaps overview, shot-acoustics room classes = polish after the first host raid** (item 17).
- **Release = 0.3.0**, single ZIP unless the measured size forces the multi-part layout; decided in item 18.
- **Unchanged:** no loot, no scav raids, bundles never in git, deploy only through `tools/deploy-to-spt.ps1`, Equator /
  Bay5 / AirPit dist hashes untouched.

## 1. Ordered work items

Phase A (offline plumbing) -> Phase B (a playable raid) -> Phase C (native features and polish) -> Phase D (release).
U = Unity rebuild (about 20-40 min for 80k objects, estimate), H = host raid.

### Phase A - Package

1. **Plan roles** (done 2026-10-18): `role` scripts=44, sound=111, culling=96, ai=128 on the excluded scenes of
   `plans/bowl.json`; the roleless-plan fixtures moved to `prison.json`
   (`test_extractor_plan_roles.py`, `test_ai_anchors.py`). Precedent: `plans/airpit.json:1425-1446`.
2. **Package v1** (`extract_map.py --plan tools/extract/plans/bowl.json --native-features --target
   D:/SPT41/EscapeFromTarkov_Data --output work/extract/bowl-native-v1`, then `refresh_native_volumes.py`),
   `docs/progress/bowl-source-060.md` with the 0.5.4 -> 0.6.0 delta. Precedent: PLAN-AIRPIT item 5.
3. **Native contract** `tools/extract/native_contracts/bowl.json` from the v1 manifest (scenes, per-scene ballistics,
   features pins, `audio: null`), the parked-contract fixtures in `test_native_contracts.py` /
   `test_native_features_validation.py` move to Prison; `validate_extract.py` (1800 s timeout) passes.
   Precedent: `native_contracts/airpit.json`, PLAN-AIRPIT item 3.
4. **Ambient audio pins and package v2**: audio block in the contract (level111: 33 AmbientSoundPlayer, 2 groups
   `CommonAmbientIn/OutGroup`, 8 banks), `AUDIO_EXPORTERS["arena_bowl"]`, re-extract as v2, validate. Precedent:
   AirPit v6 (airpit-release.md item 13 phase 1).

### Phase B - A playable raid

5. **Sidecars**: `tools/maps/bowl.bots.json` (player spawns from the 20 markers, bot spawns from the 19 Arena core
   points via `extract_ai_anchors.py --plan`, waves / PMC squads copied from AirPit), `bowl.exits.json` (two boxes on
   real floor near the pockets), `bowl.modes.json` (`extract_mode_objects.py` needs a two-pocket side rule: both
   clusters have z < 0), `bowl.ai.json`; descriptor `Exits`; `make_base_json.py`; `BowlMapTests.cs`,
   `test_bowl_spawns.py`. Precedent: PLAN-AIRPIT item 6, `airpit.*.json`, `AirPitMapTests.cs`.
6. **NavMesh oracle for Bowl**: `navmesh_oracle.py` / `navmesh_diff.py` apply the NavMeshData rotation (Bowl's is 90
   degrees about Y, AirPit's identity) and take the per-map assets / mask / live core points as parameters; Bowl probe
   points. Precedent: PLAN-AIRPIT item 7.
7. **Bake inputs and barriers, sidecar + descriptor in ONE commit**: `opened_barriers` (`DOORS_SPAWN_A`,
   `DOORS_SPAWN_B`; gate roots Bowl_Gates_Respawn_01 + BTR_82_closed, Gates_Army_01 + ambulance), `start_gates` (both),
   `held_gates` (none unless Arena keeps a box closed), `bake_bounds` (from Arena's NavMesh volume, about x -64..60,
   z -80..9, y -2..8), `navmesh_bake_layers` [9, 11, 18, 30] (Arena's own mask for Bowl), the keep-borders switch,
   `shader_fallback_names` (Tarp_Tunnel's `p0/Reflective/Bumped Specular_TwoSided`), `uses_server_time`, real identity
   text. Mirror tests, `validate_map_descriptors.py`, `test_bowl_bake_inputs.py`. Precedent: PLAN-AIRPIT item 8,
   commits f4e4505, 2a1e0ce, f011eb6.
8. **Lock-step promotion into `ArenaMapRegistry.All`** with every explicit list in one commit: `validate_map_descriptors.py`,
   `park-unreleased-dist.ps1`, `validate_distribution.py`, client csproj rows (bundles, shader sidecar
   `material-shaders.arena_bowl.json`, AI sidecar, acoustics), server csproj (`db/arena_bowl/base.json`),
   `ArenaMapBanners.cs`, `extract_banners.BANNERS`, `extract_loadscreens.LOADSCREENS`,
   `extract_map_descriptions.DESCRIPTION_KEYS`, the per-map client arrays (`NativeDecalRepairs`,
   `NativeSpatialAudio`, `PrecipitationAudio`, `RoomReverb`), and the parked fixtures (`ArenaMapBannersTests`,
   `ArenaMapDescriptionsTests`, `LocationRegistrationTests`, `ReleaseConfigurationTests`,
   `FixedRaidTimeSelectionTests`, `test_park_unreleased_dist.py`, `test_validate_distribution.py`,
   `test_release_zips.py`) move to another parked map. Precedent: PLAN-AIRPIT item 9, airpit-release.md:1282.
9. **Runtime defaults**: `MapLightingDefaults` row (rig on, zero level from spawn y), sun keys, bloom / HDR rows,
   time-policy tests, `AmbientAudioDefaults` / `AmbientAudioProfiles` rows (they throw for unlisted keys), Unity
   `Bay5AudioImporter.RootNameFor` / `AssetFolderFor`. Precedent: PLAN-AIRPIT item 10, item 13.
10. **Shader stand-in** for the TwoSided reflective shader: `ShaderStandinPolicy` row and the Bowl switch in
    `FallbacksActive`, test. Precedent: PLAN-AIRPIT item 11.
11. **Build U1 and the first host raid**: `build_all.ps1 -FullMap -BakeNavMesh $true` from v2, client Release build
    (not DllOnly), `deploy-to-spt.ps1 -Confirm`, EFT restart, `gpu-run raid`, log baseline (spawn openings, NavMesh
    counts, warnings), `uibridge_teleport dryRun` from both pockets to the capture point, hideout opens. Precedent:
    PLAN-AIRPIT item 12.
12. **Host iteration U2..Un**: pockets and gate removal, bounds, props the oracle flags, borders, rain F8, bots.

### Phase C - Native features and polish

13. **Verification of the native features on the host** (ballistics F10, decals, modifier counts, physics props 341,
    barbed wire 118, light culling F7, acoustics 14 rooms / 25 portals, cross-map raids, max frame time).
14. **Spatial acoustics, room reverb, rain audio** (`extract_spatial_audio.py --map arena_bowl`, graph JSON, client
    per-map arrays). Precedent: PLAN-AIRPIT item 21.
15. **AI anchors** staged (`bowl.ai.json`, csproj row). Precedent: item 20.
16. **Identity text and art**: banner, four parallax images, official description en/ge/ru, selection marker (919, 679
    is measured). Precedent: item 23.
17. **Polish**: MultiFlare (28 FlareLights, 6 inactive ones fail the extractor's range check), DynamicMaps overview,
    shot-acoustics room classes.

### Phase D - Release

18. **Release 0.3.0**: version bump, release notes, dist manifest, `make_release_zips.py --single --extract-check`,
    Pixeldrain, VirusTotal, tag, GitHub release; the three released maps' dist hashes unchanged.
19. **ArenaMode hand-off (user)**: catalog entry, side-by-pocket formation, SpawnGates hold / release on the two gate
    roots.

## 2. Bowl-specific risks

1. **Navigation**: the spike bake had no bounds, no layer mask and no opened barriers (51k vertices). Bowl's gate roots
   carry vehicles (BTR, ambulance) and their own layer-29 cubes; the "deactivate everything but the frame" rule may need
   exceptions, and the oracle is blind until the rotation fix (item 6).
2. **Borders**: keeping 354 cubes is the right default for a stadium but has no precedent; a cube on a legitimate route
   shows up as a NavMesh cut in the oracle or as a wall on the host.
3. **Two pockets east / west, not north / south**: `extract_mode_objects.py` and ArenaMode's formation recipe assume a z
   split; the sidecar needs a per-map rule.
4. **Size**: the spike scenes bundle was 768 MB without decals; a four-map ZIP is about 3 GB (estimate).
5. **Visual gaps without precedent**: pink Tarp_Tunnel until item 10, trees without wind or impostors, empty grass
   scene, empty stands.

## 3. Deliberately not in this plan

GPUInstancer grass, SpeedTree / impostor trees, window breakers, the stands crowd, Arena's BTR / ambulance animations
(gates switch off like every other barrier), the non-TeamFight mode sets (Tournament, LastHero, BlastGang, TDM), the
AirPit partial-scene and lamp-fill machinery (Bowl's object counts match the live source).
