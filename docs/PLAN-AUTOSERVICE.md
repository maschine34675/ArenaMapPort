# Plan: Autoservice - Sixth Release Map at Iceberg Quality

Source: four read-only surveys run 2026-11-08 (S1 source delta and identity, S2 gameplay objects, modes and navigation, S3
environment, rendering and audio, S4 pipeline gap against the Iceberg precedent). The probes used the repo's own parsers
read-only on the VM's Arena 0.6.2.0.48473 (`D:/Battlestate Games/Escape from Tarkov Arena/EscapeFromTarkovArena_Data`) and on
the September spike package `work/extract/autoservice`; a scratch re-extract of the spike's unchanged plan lives under the
ignored `work/tmp/autoservice-survey/`. Evidence is quoted as file:line, counts or object paths; inferences are marked
**(inferred)**. Status lives in [HANDOFF.md](../HANDOFF.md) and the progress log
[docs/progress/autoservice-release.md](progress/autoservice-release.md) (survey record, dated work, host evidence). This file
is the plan, not the record. Work happens on the branch `feature/autoservice` (from `main` = 52d0a4d). The user's request of
2026-11-08, "dann wuerde ich gerne mit der naechsten map weitermachen: Autoservice", is a go without further constraints, as
Iceberg's "dann wuerde ich gerne Iceberg als naechste map angehen" was: the decisions below are the agent's unless marked
**(user)**.

Autoservice is Arena's `Arena_AutoService` (player-facing name "Chop Shop", read from Arena's `TestBackendLocaleEn`
in resources.assets: the key `Arena_AutoService` and the row `6441004f8ab20b218807f14b Name` both hold "Chop Shop"; preset `arena_autoservice_preset.bundle`, ServerName
`Arena_AutoService`, port key `arena_autoservice`, port id_field `e6aa4591d3e1a59835d625b3`): a small garage and street yard,
play area about 80.6 x 47.3 m on ground y -3.8 with two long pockets, a yard between them and tribunes at y 4.4 and above. The
preset has no MongoId field (its only identifier is the ServerName string). It offers ShootOutSolo, ShootOutDuo, ShootOutTrio
(ShootOut preset: `ShootOut_DesignStuff` + Culling), TeamFight and Coop (TeamFight preset: `TeamFight_DesignStuff` + Light +
Culling) and LastHero (`LastHero_DesignStuff` + the same Light and Culling guids). There is no CheckPoint, Domination or
BlastGang set and no per-mode AI scene (one `Arena_AutoService_AI`, level 63). The map is a crowd-of-bodies map, not a stands
map: 30 `ArenaFanEntity` full player-body rigs under `SCAV_SPECTATORS` (see section 0). Arena's locales carry an official name
and description for it under Arena's own location id `6441004f8ab20b218807f14b` (en 148 characters, ge 143, ru 119; the id is read from the
`Name` and `Description` rows of that table, see section 2).

Autoservice before 2026-11-08: the September spike (package `work/extract/autoservice`, Arena 0.5.4, extracted 2026-09-15, 4
scenes, 4,270 meshes, 401 materials, 872 textures, no native features; bundles `work/build/autoservice`, scenes 708,353,294 B,
NavMesh 397,983 B with a renderer-union bake of 5,165 sources, 30,540 vertices, 13,866 triangles over about 597 x 99 x 686 m;
receipt PORT_VALIDATED 2026-09-16, offline spawn placement passed), `db/arena_autoservice/base.json` a placeholder ("Arena:
Auto Service (spike)", `BotMax 0`, donor description), registered only in `ArenaMapRegistry.Catalog`
(`ArenaMapPort.Shared/ArenaMapDescriptor.cs:1210-1224`, 14 required fields, none of the optional policy fields), never raided
(`docs/progress/remaining-maps.md` rows: GPU raid Not run). Unlike Iceberg's CheckPoint scenes (+9), **no level index moved**
between Arena 0.5.4 and 0.6.2 (62, 63, 65, 66, 69, 112, 135, 136, 243 are identical), but the content changed in two places
(372 border cubes, two props), every object, mesh and texture id rehashed, and the spike's receipt is invalid against 0.6.2.

## 0. Decisions (agent, 2026-11-08)

- **Source = the VM's Arena 0.6.2.0.48473**, packages `work/extract/autoservice-native-v<N>`: v1 = native features without
  ambient audio, v2 = v1 + ambient audio (the Iceberg split). The spike package `work/extract/autoservice` (Arena 0.5.4) is never
  an `--output` again and is not an accepted input; `work/build/autoservice` is archived as
  `work/build/autoservice-spike-20260915` before the first Unity run. The scratch extract of the survey is a reference only.
  The plan gains scene `role` fields (scripts 69, sound 112, ai 63, culling 135), so the plan digest changes. GPU-2 still runs
  Arena 0.6.1; the stage-2 wrap is verified with `verify-keys --arena` against every build at hand (VM and MASCHINE11 0.6.2,
  GPU-2 0.6.1).
- **Mode = TeamFight alone** (`Arena_AutoService_TeamFight_DesignStuff` 136 + `Light` 66 + `Culling` 135, with the base scene
  62, `DesignStuff` 65 and the AI scene 63). It is the only mode with two pockets of 10 markers, two spawn bounds, one capture
  point and the four gates; ShootOut has tribune teams (15 Bear markers on the tribunes, four more TVs, no clean two-pocket
  split by the tool's rule) and LastHero is a single-bound free-for-all with resupply crates. Every mode shares scenes 62, 65 and
  63, so the choice changes only markers, bounds, capture point and gates. ShootOut and LastHero are not imported and get no plan
  record.
- **Time and weather = `UsesServerTime`** **(user, 2026-11-08: "serverzeit ok")**, as Bay5, AirPit, Bowl and Iceberg: the imported scenes carry no Directional light,
  sky, lightmap or reflection probe (189 + 12 realtime lights only); the sun, sky, fog and weather stack live in the excluded
  Scripts scene 69 (one Directional light of 0.5, fog colour (0.0053, 0.0080, 0.0155) = the sidecar's `fog_color`). A fixed
  night clock like Arena's look is a look choice for later **(user)**.
- **Summer set only, no snow and no winter work** **(user, 2026-11-08: "sommer reicht")**. Scenes 243-246 (Scripts_Winter, Light_Winter, Culling_Winter, Winter) are not
  in the TeamFight preset and stay out. Scripts_Winter carries 602 `AirdropPoint` objects, 22 `DryPlane` and a Lighthouse
  leftover: nothing of it is imported (no loot, no airdrops). Which season Arena loads live was not determined (the main preset
  lists Scripts and Scripts_Winter); a winter variant is an addition **(user)**.
- **Grass and terrain**: none exist. Trees stay as the extractor leaves them: 235 `TreeWind`, 52 `AmplifyImpostor` with 52
  `ImpostorsArrayElement`, 11 `TreeInteractive`, 3 `Trunk`, nine `Nature/SpeedTreeEFT` materials (204 slots).
- **Borders = keep the 372 layer-29 `LevelBorder` cubes** of `Arena_AutoService_LevelBorder` in `DesignStuff` (new in 0.6.2;
  disabled renderers, enabled solid `BoxCollider`s), the Bowl and Iceberg policy: `keep_level_borders: true` and
  `navmesh_bake_layers` `[9, 11, 13, 18, 29, 30]` (Arena's own surface mask 0x40042A00 = 9/11/13/18/30, plus 29). The cube AABB
  is x -413.15..-332.55, y -5.85..9.49, z 465.67..512.99. The cubes alone do not close the perimeter in plan view (buildings and
  props make most of it, **inferred**); the 7-point `ExcludeLevelBorders` polygon in the culling scene is a culling outline, not
  the movement border, so `authored_borders` is not used. The single layer-29 `levelBoarder` collider on a prop at (-341.36,
  -0.73, 469.86) and the `Arena_AutoService_LevelBorder` copy in the excluded Scripts_Winter are noted, not imported.
- **Bake bounds = Arena's own NavMeshSurface volume** (centre (-370.3, 2.0, 486.8), size (101.8, 122.0, 81.7): x -421.2..-319.4,
  z 446.0..527.65), with y cut to just below the ground (collider AABB y from -5.85) up to above the tribunes; the renderer union
  reaches z 774 (the extreme is `Background/Buildings/Building_P49_LODLAST (19)/City_Building_P49_LODLAST` at z 774.2; `Ceh_Complex_6` meshes sit at z about 542) and is unusable. The second Arena NavMesh island (20 polygons,
  33.8 m2, x -336.2..-324.2, z 521.3..527.7, outside the cubes) is checked, not copied. The oracle on the live package decides
  the final box.
- **Team starts = the 20 `SpawnPointMarker`s** (all stem "Usec house", suffix none and 1-19): the east pocket (x -334.8..-340.6,
  z 476.5..507.7, y -3.78..-3.84; indices none, 1-4, 8, 16-19) in `SPAWN_BOUND_Fighter` (centre (-338.43, -2.80, 492.79), footprint
  x -344.2..-332.6) and the west pocket (x -405.9..-410.0, z 476.1..501.7, y -3.72..-3.74; indices 5-7, 9-15) in
  `SPAWN_BOUND_Fighter (1)` (centre (-408.20, -2.80, 492.47)). The tool's pocket split passes only marginally (ratio 2.1 against
  the limit 2.0) and its "north/south" names are geometrically east/west, so sides are named by bound containment (an explicit
  rule like Iceberg's `TEAM_BOUND_RULES`) and the tests pin the ratio. The one capture point (`CapturePointEntity` 33001 on
  `CAPTURE_POINT/BOWL_CAPTURE_POINT_LIGHT_03`, a Bowl leftover name) is at (-369.29, -3.80, 499.49). TeamFight has no resupply
  crates (they exist only in LastHero), no kill, spawn-protection or minefield triggers.
- **Gates = four barriers, all opened at raid start and held on request.** `opened_barriers` = `start_gates` = the four unit
  paths under `DOORS`: `big_gate_metal` (east pocket, slides +4.40 m along world +z in 8.0 s), `equator_roller_shutters_01`
  (east pocket, rises +2.56 m in 4.5 s), `roller_shutters_techlight_01` and `roller_shutters_techlight_01 (1)` (west pocket,
  rise +4.11 m in 6.733 s); `held_gates` = none. Arena's own mesh was baked with all four open (it crosses each pocket wall only
  at the gate openings). The four `DotAnimator` lamps (`LIGHT_1..4`: RED off, GREEN on) become four `switch` rows. The door tool
  needs three patches first and two extractor paths (S2; `extract()` dispatches exclusively, a `DOT_SOURCES` key to `extract_dot`, else a `MAP_SOURCES` key to `extract_clips`, so the clip gates and the DotAnimator lamps need both paths and a merge, item 7): accept Arena's spelling `clossed`, read the clips through the ShootOut scene's identical gates
  (level 70, because level 136's controllers live in `sharedassets70.assets`), and a reviewed override for
  `equator_roller_shutters_01`, whose scene collider sits 0.978 m below the clip's closed pose (Arena's scene is inconsistent).
  Drives take `Spawns/SpawnGateDriveScale` 0.5 like every map. The posed leaves are dropped by the bake; the frame colliders
  stay standing and may not close a doorway (checked with `navmesh_diff.py --doors`).
- **Gate sounds = the four `EntitySoundPlayer`s** under `ARENA_GATES_SOUND` in the Sound scene (clips `gates_outdoor_left`
  10.43 s, `gates_outdoor_right` 4.68 s, `gates_indoor_left` 8.01 s, `gates_indoor_right` 7.90 s; each sits exactly on its gate
  root, distance 0; four rows, four distinct clips, no borrowed row). The clips are fitted to the halved drives by the existing
  seam table. The clips come from `sharedassets112.assets/.resource`; Sound scene 112 is not one of the port's scenes, so S1 lists
  no key-source file there, but the extractor's copy check against `tools/bundle/key-sources.json` still runs (the Air Pit rule).
- **Score monitors**: four `ArenaMonitorHolder`s in `DesignStuff`, all with a face on TeamFight: two timer (type 0, 3.127 x
  1.884 m, at (-345.82, 8.17, 489.18) and (-400.95, 8.14, 489.42)) and two alive-players (type 2, 2.244 x 1.352 m, at (-367.77,
  4.74, 509.60) and (-381.48, 4.85, 468.89)). ShootOut's score and playerboard TVs are not imported.
- **Superseded on 2026-11-11 (user question "die zuschauer fehlen aber noch oder?"; the premise below was wrong): Autoservice has Arena's own crowd.**
  The 30 fans are the same ArenaFan rigs, controller and appearances as the four crowd maps', exported by the unchanged pipeline (record,
  2026-11-11 "Phase C"); `CROWDLESS_MAP_KEYS` is Iceberg only. The original bullet:
- **No spectator crowd and no murmur or reaction beds (crowdless)** **(user, 2026-11-08: "crowdless passt")**. The 30 `ArenaFanEntity` of `SCAV_SPECTATORS` carry full EFT
  player-body rigs (PlayerBones, LimbIK, SkinnedMeshRenderer, ...), an incompatible layout for the stands extractor (the crowdless call rests on that layout, not on position). They stand on
  the long tribune edges (y 4.4-4.6 at z 467 and z 511, x -398..-347) plus five on a y 1.9-2.0 ledge at x about -395 beside the
  west pocket (re-derived from `SCAV_SPECTATORS` in level 136: x -398.4..-346.7, y 1.92..4.58, z 467.0..511.0); `DesignStuff` holds none. Autoservice joins `validate_map_descriptors.CROWDLESS_MAP_KEYS`
  with Iceberg (packer, distribution validator, staging and the C# crowd switch share that one list) and
  `SpectatorCrowd.IsAvailable` stays false there. A port-only or body-rig crowd is an addition **(user)**. The 6 inactive
  `FansAmbient` and 10 inactive `ArenaFanSpeakerEntity` emitters stay unused.
- **Description = Arena's own official text**, (the location id is confirmed: the `Name` row of `6441004f8ab20b218807f14b` reads "Chop Shop"; item 9 only checks that it is Arena's location id and not the port's `id_field`): a `DESCRIPTION_KEYS` row and
  the server's `OfficialTextMapKeys` row change together (the texts are BSG-derived and stay in the ignored
  `work/extract/map-descriptions`); the pack then covers five of six maps (Iceberg stays the textless one), so
  `ArenaMapDescriptionsTests` keeps `All.Count - 1`. If that item 9 check fails, the descriptor's own-words
  `LocaleDescription` is used. The locale name loses its "(spike)" suffix: "Arena: Chop Shop" (from the name row above); the notices and the
  lobby label read `LocaleName`.
- **Identity art**: banner `Matchmaker_Banner_chop-shop` (resources.assets path 1738, 769 x 460 RGB24; the singular "Banner" like
  four of the five released maps), loading screen `AllScreens_Loadingscreen-chop-shop` (path 1731, 1920 x 1080 DXT1) and the four
  `Arena_MainMenu_Paralax_ChopShop-1..4` (sharedassets1.assets ids 438, 1445, 987, 135, 2048 x 1024). That these are Autoservice's
  art is settled by looking at them before any row is written (path ids are build hints, names decide). The selection marker
  (407, 471) already exists in sidecar, descriptor and `markers.json`.
- **Ambient audio** (package v2): 42 players in the Sound scene (23 `AmbientSoundPlayer`, 16 `SeasonAmbientSoundPlayer`, 3
  `SeasonLoopAmbientSoundPlayer`), two groups (`CommonAmbientInGroup` 21 on `CommonAmbInEffects`, `CommonAmbientOutGroup` 20 on
  `CommonAmbOutEffects`), 1 `BezierSpline`. New reviewed contract work (a schema with states): the `SeasonLoopAmbientSoundPlayer`
  class (3 players `indoor_winds_winter`, a Winter-only key, volume 0.3, max 7/10/7 m; EFT has the class, layout decoded and
  consumed to the last byte), nine `water_drops` players with `minDistance` 0 (volume 0.07, max 20 m; its own pin like Iceberg's
  21), four `far_fight` players forced onto `CommonAmbOutBypass` plus one on `CommonAmbInEffects`, and the inactive chain
  (ungrouped `AmbientSoundPlayer` 1.0 / 50 m, `AmbientPlayerSplineMappedEmitter`, `SplineEmitterPathMover`, the 22-point spline),
  which is **dropped from the contract, not ported**. Defaults start at Bowl's provisional row (3 dB, offset 0.4, range 3), and a
  lower offset is tried first by ear, because offset 0.4 lifts the 0.07 water drops to 0.47 in every interior **(inferred)**; the
  values are host-tuned start values, not Bowl's measurements.
- **Spatial acoustics are required** and need no repair: the repo's exporter runs clean on the 0.6.2 Sound scene with no
  `REVIEWED_BAKE_REPAIRS` and no mirrored volumes (7 rooms, 16 portals, 9 trigger areas, bake 4,465 B with 21 room pairs and 139
  routes, one room tone `amb_arena_autoservice_winter_night_indoor`, mixer effects present). Room reverb, rain audio and
  precipitation rows are added to the per-map client arrays.
- **Shaders**: all 33 distinct shader names of the survey census (409 materials; the manifest counts 402; an earlier line said 34, not reproducible) exist in EFT 0.16.9.5, and item 2 re-runs the census on package v1 and pins the count. `Custom/Billboard_FogSheet_Simple` (80 slots), both
  `Cloth/ClothShader` variants (92 slots) resolve in process from Customs' shared assets (the AirPit evidence), so AirPit's
  stand-in set (`ShaderFallbackNames`) is the safety net. Unproven: `p0/Reflective/Bumped Specular SMap Baked` (1 material, 1
  slot, `Factory_Building_04_Outdoor_LODLAST`, EFT `sharedassets216`, a scene the shader trap does not name), checked on the first
  host raid and then given a reviewed stand-in or hidden, with evidence. No MK4-style normal-slot case was found.
- **Emission cap: none.** The strongest emissive is 3.88 (`Material #37`, `_EmissionPower` x visibility), below the released
  maps' 4.13 and far below Iceberg's 67.8, and no Standard material carries the `_EMISSION` keyword; `EmissionCap` stays 0 (off)
  and bloom is the donor pair 2.10 / 0.35.
- **No native water**: no `WaterContainer`, `SimpleWater4`, `WaterForSSR` or Terrain exists; `NATIVE_WATER_MAP_KEYS` is
  unchanged. Puddles use the shared `puddle` material `0877f2335044042e` (42 slots) for `puddle_water_material_names`; the wet
  mold decal `arena_autoservice_mold_decal_atlas_01_wet` (id `aa54d9752ed43430`, 4 slots) gets the mesh/UV/mask review before it is
  listed.
- **MultiFlare: yes.** 21 `FlareLight` in the Light scene, 0 negative scales, all pass `_schema_check` and `validate_light`; the
  expected census tuple is (21, 8, 77, 32). The exporter tables, `NativeMultiFlare` and `FLARE_MAP_KEYS` get Autoservice.
- **DynamicMaps overview: yes**, in Phase C (the Iceberg route: connected-route ground plan, owned package, scoped deploy).
- **Exits**: Arena authors no extraction here. Two 4 x 3 x 4 m boxes on the main NavMesh component outside the gates, outside
  the gates (the corridors are tight): east about (-349.0, -3.78, 489.0), 10.4 m from the east big-gate root (-344.09, 479.86); west
  about (-393, -3.79, 501), 9.3 m from the west techlight shutter root (-402.26, -3.78, 501.22) ((-398, 501) is only 4.3 m from it and
  is dropped); distances are stated per box, not as one range (clearance 3.3-3.4 m, nearest marker 8.7 m or more, **inferred** offline); verified with a NavMesh route and `uibridge_walk`.
- **Bots and AI**: the AI scene's 16 bot born points (5 per pocket, 6 in the yard), 7 `AICorePoint`s (group 7), the 5-hub
  PatrolWay with 59 patrol points and the 23 mine points are authored on the main component and imported through
  `extract_ai_anchors` with the plan's `ai` role; the stale far-field leftovers (2,696 `ParentOfBearingPoints`, AI_SUSP, 44
  inactive `AdditionalNavmeshBlock` cubes, a Shuttle) lie more than 400 m away from the play area and are dropped. `AIMinePoint`s
  are AI data, not minefields; `AMinePlace_15` lies off Arena's mesh.
- **NavMesh oracle**: `navmesh_diff.ARENA_SOURCES` row `arena_autoservice`, `sharedassets63.assets` path 2 (NavMeshData, 695
  polygons, 1,212 triangles, 1,250 vertices, 19 of 30 tiles, main component 1,915.6 m2 holding every marker, the capture point
  and all AI points), mask 0x40042A00, identity placement, level 63, scene `Arena_AutoService_AI`.
- **Release** number and form **(user)**: the next release would carry six maps; the staged scenes bundle is projected at about
  0.93-0.95 GB (0.86-0.88 GiB) and `dist/` at about 6 GiB **(inferred, section 2)**. Until the user decides, nothing is tagged, uploaded or
  pushed for Autoservice, and it stays out of `ArenaMapRegistry.All` until Phase A and B pass offline.
- **Unchanged**: no loot (the `LootPointsGroup`, the Loot scene 67 and the airdrop points stay out), no scav raids, encrypted
  bundles in `dist/` and never in git, content keys never committed or logged (`work/release-keys` backed up off the VM after
  the first Autoservice stage), no switch for the Arena check, deploy only through `tools/deploy-to-spt.ps1`, ArenaMode
  read-only, the released maps' dist hashes untouched, the encryption never called copy protection.

### Needs the user

Decided by the user on 2026-11-08 ("sommer reicht, crowdless passt, serverzeit ok, leg los mit phase A"): summer set only, crowdless, server
time. Still the user's, nothing blocks on them before item 18: (1) release number and form; (2) the ArenaMode hand-off (item 19, the user's
repository). A winter variant, a crowd or a fixed night clock would be later additions **(user)**.

## 1. Ordered work items

Phase A (package) -> Phase B (a playable raid) -> Phase C (native features and polish) -> Phase D (release). U = Unity build
(the spike took 13 min; Bowl needed four U1 attempts and Iceberg several, so budget several), H = host raid. Autoservice stays
Catalog-only until item 12 (the registry promotion), which is why nothing deploys before it, and after it nothing deploys before item 14 (see item 12).

### Phase A - Package

1. **Housekeeping and plan roles**: archive `work/build/autoservice` to `work/build/autoservice-spike-20260915` (before any Unity
   run); `tools/extract/plans/autoservice.json` gains the roles scripts 69, sound 112, ai 63, culling 135 and the reviewed
   ballistic and lamp roles the first extraction asks for; remove `autoservice` from `park-unreleased-dist.ps1` ($parked, `park-unreleased-dist.ps1:31`)
   only in item 12; `build_remaining.ps1` drops it from `$Maps` and `$allMaps` (lines 27, 35) and refuses it with a `throw` like
   Bowl's and Iceberg's (line 39: its package would be the 0.5.4 spike and its output would overwrite `work/build/autoservice`). (done 2026-11-08, see the record)
2. **Package v1**: `python tools/extract/extract_map.py --plan tools/extract/plans/autoservice.json --native-features --output
   work/extract/autoservice-native-v1 [--target <SPT EscapeFromTarkov_Data>]`, then `python tools/extract/refresh_native_volumes.py
   <dir>`, then `validate_extract.py --package ... --extractor-timeout 1800`. Re-run the shader census on the package and pin the distinct-shader count (33 in the survey census). Expected from the scratch run: about 32,097 objects,
   4,270 meshes, 402 materials, 872 textures, 116 `MeshFilter ... no mesh assigned` warnings, 0 errors. Run with
   `PYTHONUTF8=1` (the 0.6.2 names carry real Cyrillic characters; the spike has mojibake of them, so hierarchy paths differ). A
   source note `docs/progress/autoservice-source-062.md` (the `iceberg-source-061.md` pattern) records the 0.5.4 to 0.6.2 delta:
   `DesignStuff` 124 -> 497 objects (372 cubes), main scene 25,678 -> 25,692 (two `metal_sheet_hanging_01_rusted` props), the
   rest identical. (U/H: none; about 20-30 min.) (done 2026-11-08, see the record)
3. **Native contract** `tools/extract/native_contracts/autoservice.json` (pins from the surveys): decals 304 (299 + 3 + 2),
   `CullingLightObject` 184 (172 + 12), `VolumetricLight` 78 (68 + 10), `LightFlicker` 0, `SmallPhysicsObject` 134 (14 Books, 6
   MediumGeneric, 2 Monitor, 40 SmallPlastic, 34 GlassBottle, 19 SmallBox, 19 SmallPaper), `NavMeshModifier` 10 (62) + 20 (136),
   volumes 6 + 1, rain cover 1 `IndoorTrigger` / 0 `DryPlane` / 0 `WeatherObstacle`, ballistic counts from the extraction,
   `audio: null` for v1. (done 2026-11-08, see the record)
4. **Ambient audio and package v2**: the `SeasonLoopAmbientSoundPlayer` parser (3 players), the reviewed `water_drops`
   `minDistance` 0 pin (9), forced-mixer and inactive-player states, the dropped spline chain, `AUDIO_EXPORTERS["arena_autoservice"]`,
   the contract's audio block, `Bay5AudioImporter` (`RootNameFor` and `AssetFolderFor` throw for an unknown map: two case rows and
   a root-name constant), re-extract as `autoservice-native-v2`, validate. The client counterparts: `AmbientAudioProfiles` (an
   `AmbientAudioProfile` mirroring the contract, pinned by `AmbientAudioProfilesTests`) and `AmbientAudioDefaults` (three
   switches: Values, PreviousValues, HasLegacyMix). Precedent: Iceberg item 5, Bowl `9c6bc17`, AirPit v6. (done 2026-11-08, see the record)
5. **Stage-2 key-source rows**: `tools/bundle/key-sources.json` gets `arena_autoservice`: 62 `[sharedassets62.assets.resS,
   sharedassets62.resource]`, 65 `[]`, 66 `[sharedassets66.assets.resS]`, 136 `[]` = 3 files, 464,942,688 B (458,749,216 +
   2,036,672 + 4,156,800), unchanged in size from 0.5.4; with `test_bundle_crypt_tool.py` (`KeySourcesTests`: the table keys, the
   LISTED counts, the accepted-package map) and `BundleCryptoTests.cs`. The first stage (key creation) runs only after a U1 bundle
   passed every validator. (done 2026-11-08, see the record)

### Phase B - A playable raid

6. **Mode objects and spawn sidecars**: `extract_mode_objects.py` gets the explicit bound rule (east/west by `SPAWN_BOUND_Fighter`
   containment, a test pinning the 2.1 pocket ratio), capture point from `capture_point_light_centroid` (1 light);
   `autoservice.modes.json`, `autoservice.bots.json` (player spawns from the pockets, bot zones from the 7 core points and the
   16 born points, waves and PMC squads after AirPit), `autoservice.exits.json` (the two boxes above), `autoservice.ai.json`
   (via `extract_ai_anchors`, bake-bounds table row, the far-field leftovers dropped). Spawn-placement covers all 20 markers
   (Arena's mesh has all 20 on the main component). (done 2026-11-11, see the record: 4 player rows, 7 bot rows, the east exit
   0.5 m south of the draft for clearance, exit names 'Chop Shop East Gate' / 'Chop Shop West Gate')
7. **Doors, gate sounds, score monitors**: patch `extract_door_poses.py` (`clossed`, clips through level 70, the equator-shutter
   override). The four gates are Mecanim clips (controllers in `sharedassets70`), so they need a `MAP_SOURCES["arena_autoservice"]`
   row (scene `Arena_AutoService_TeamFight_DesignStuff`, clip scene level 70, the Bowl path `extract_clips`); the four lamps
   (`DOORS/LIGHT_1..4` under TeamFight, 1 and 3 GREEN on, 2 and 4 RED off) are DotAnimators, so they need a
   `DOT_SOURCES["arena_autoservice"]` scan with `"switches": True`. Because `extract()` takes one path only, either `extract()` runs
   both and merges the rows into one sidecar or `extract_clips` learns switch rows (decide in the item, pin it with a test); result
   `autoservice.doors.json` (4 motion rows + 4 `switch` rows); `extract_gate_sounds`
   row (`MAPS`), `gate_sound_fit.json` rows for the four clips, `autoservice.gatesounds.json`; `extract_score_monitors.MAPS`
   row, `autoservice.monitors.json` (2 timer + 2 alive faces). (done 2026-11-11, see the record: 19 motion rows, one per moved
   node, + 4 switch rows = 23; the equator shutter keeps the scene pose as the closed pose of its collider nodes)
8. **NavMesh oracle and bake inputs, sidecar + descriptor in ONE commit**: `ARENA_SOURCES` row, the oracle on v1/v2 (with
   `--doors`), `bake_bounds`, `navmesh_bake_layers`, `keep_level_borders`, `opened_barriers`, `start_gates`, `uses_server_time`,
   `puddle_water_material_names`, a bake-input audit (layer-0 solids in the play space: only the two MeshColliders of the
   `vaz_2106` front door at (-353.72, -3.32, 483.75); the tribune levels above y -0.43 that Arena's mesh does not cover; the
   cube-top islands; the second Arena island), `test_autoservice_bake_inputs.py` and `_spawns.py`. Precedent: Bowl `4cea40b`,
   Iceberg item 9. (done 2026-11-11, see the record: `bake_bounds` y -5..2, not above the tribunes; oracle 1 component, 1,914.6 m2)
9. **Identity and descriptions**: `extract_banners.BANNERS`, `extract_loadscreens.LOADSCREENS` (banner, loading screen and the
   four parallax images), `ArenaMapBanners` (+ `ArenaMapBannersTests`, `test_banners.py`), confirm the location id and add the
   `DESCRIPTION_KEYS` / `OfficialTextMapKeys` row, drop "(spike)" from the locale name and the placeholder description. (done
   2026-11-11, see the record: locale name 'Arena: Chop Shop'; the description loader accepts a pending map's entry)
10. **Spatial acoustics**: `extract_spatial_audio --map arena_autoservice` (no repair rules; the plan's `sound` role), graph JSON,
    bake and room tone staged; room reverb and rain audio rows. Second opinion on the bake: SPT-SEB's MIT checker
    `check_live_acoustics.py` (routes against portal connections, buffer bounds, lost pairs; see
    [progress/spt-seb-compatibility.md](progress/spt-seb-compatibility.md)) runs against the new `.audiobakedata` as tool use (no code copied); the same
    run on Iceberg's repaired bake is cheap and still open. (done 2026-11-11 offline, see the record: export and pins; the client
    rows move to item 12; SEB check ok for Autoservice, Iceberg's repaired bake has 510 duplicate routes in 153 pairs, open)
11. **Light, flares and shaders offline**: MultiFlare exporter tables (`extract_multiflare.py:39-48`, tuple (21, 8, 77, 32)),
    the shader stand-in row decisions (`ShaderFallbackNames` for FogSheet and the two Cloth shaders), `MapLightingDefaults` zero
    level from the pockets' spawn floor (about y -3.72..-3.84; Iceberg's -3.7 is its own measurement, so this is a measurement,
    not a copy), `BloomTuning` donor pair. (done 2026-11-11 offline, see the record: ZeroLevel -8.2, no emission cap, a fourth
    stand-in `p0/Reflective/Bumped Specular SMap Baked` and an Autoservice stand-in switch are item 12 rows)
12. **Lock-step promotion into `ArenaMapRegistry.All`**, every list in one commit. First step: `git show --stat 1bd5868` (Iceberg's
    promotion, 79 files) and one row per file below or an explicit "no change needed because ...". Rows:
    - descriptor and sidecar; base.json (`make_base_json.py --map tools/maps/autoservice.json`, or `-SkipUnityBuild` alone).
    - client csproj: `<Description>` text; build dir property; three bundle items and the "expected bundle missing" Error text;
      shader-sidecar source property, its missing-file `Error` and the `Copy` to `material-shaders.arena_autoservice.json`;
      `ArenaMapPortAiSidecar` row for `autoservice.ai.json` (without it the AI anchors are never staged); acoustics `Error`
      condition, its `ArenaMapPortAcousticFile` item and the `extract_spatial_audio --map arena_autoservice` hint; gate-sounds `Error`
      condition and `ArenaMapPortGateSoundFile` item; the key-create guard (lines 468-469) and the property at 477; the stage `Exec`
      (489-495); DynamicMaps and flare staging; server csproj (lines 96 and 124).
    - per-map client files and arrays: `NativeDecalRepairs`, `NativeSpatialAudio`, `PrecipitationAudio`, `RoomReverb`,
      `ReverbAudioProbe`, `SpatialAudioRepairs`, `Bay5AmbientAudio`, `DynamicMapAssets`, `NativeMultiFlare`, `AmbientAudioDefaults`,
      `AmbientAudioProfiles`, `MapLightingDefaults`, `ShaderStandinPolicy` as the census requires, `AiAnchors`, `TimeOfDayRepairs`,
      `FixedRaidTimeSelection`, `HdrImageTuning`, `EquatorNightNeon`, `WeatherTestOverride`, `LoadingScreenArt`,
      `PuddleFootstepRepairs`, and `ArenaSpectators.DesignScene` (the explicit crowdless case that `test_crowdless_maps` pins).
    - pipeline and docs: validators' lists (`RELEASE_MAP_NAMES/KEYS`, `CROWDLESS_MAP_KEYS`, `OVERVIEW_MAP_NAMES`,
      `FLARE_MAP_KEYS`), `tools/release/make_release_zips.py` (the CROWDLESS filter and part rules, a part for the map),
      `docs/dist-manifest.md` (file counts and a "pending, not staged" paragraph as Iceberg's), the crowd-map comments in
      `extract_spectator.py`, `stage_spectators.py` and `deploy-to-spt.ps1` (`-DynamicMapsMap` ValidateSet, comment and name
      switch), the parked lists (`park-unreleased-dist.ps1`; `AdditionalMapBuilds.props` stays).
    - tests that pin the released set from five to six: `ReleaseConfigurationTests`, `ArenaServerStatusTests`, `ServerStatusParserTests`,
      `MapNoticeInputsTests`, `MapNoticeSchedulerTests`, `MapDescriptorConsistencyTests`, `DoorPoseSidecarTests`,
      `ScoreMonitorSidecarTests`, `AiAnchorsTests`, `FixedRaidTime*Tests`, `SelectionMapPositionTests`, `WeatherRigDefaultsTests`; the
      Python modules `test_validate_distribution`, `test_crowdless_maps`, `test_map_pipeline`, `test_park_unreleased_dist`,
      `test_release_zips`, `test_door_poses`, `test_score_monitors`, `test_gate_sounds`, `test_puddle_water_census`; plus the new
      per-map tests (`AutoServiceMapTests`, acoustics and ambient tests, the bake-input and spawn tests,
      `test_spatial_audio_autoservice.py`). The gate is the full `dotnet test -c Release` and `unittest discover` run from
      PowerShell (the Bash console encoding breaks two subprocess tests).
    - (done 2026-11-11 as `b2fd6e3`, review fixes `18e21df`; see the record.)
    - After this item no deploy of any kind goes to a host until U1 (item 13) and the first stage (item 14) are done: a full non-DllOnly
      client build stops at the first missing bundle, `deploy-to-spt.ps1` refuses a whole-dist deploy while the staged server mod has
      a `db/arena_autoservice` folder and the client plugin lacks the map's three bundles or key file, and a `-DllOnly` deploy logs
      `Required map file is missing` and raises the incomplete-install notice (Iceberg rule 6).
13. **Build U1** (`pwsh -Command "& 'tools/build_all.ps1' -MapKey arena_autoservice -MapDescriptor
    D:/ArenaMapPort/tools/maps/autoservice.json -PackageDir D:/ArenaMapPort/work/extract/autoservice-native-v2 -FullMap
    -OutputDir D:/ArenaMapPort/work/build/autoservice -BakeNavMesh \$true -Verify \$true -SkipGameRefsSync"`; about 13 min), then by hand
    any validator the script did not reach (`validate_bundle_blocks.py`, `validate_bundle_roundtrip.py`, `validate_preset.py`,
    `validate_map_descriptors.py`); never two Unity builds at once. Regenerated stand-in shader stubs are committed after checking
    that they do not change a released map's build. (done 2026-11-11, first attempt; stubs `1f5dbe8`; see the record.)
14. **First stage, deploy, first raid**: `dotnet build ArenaMapPort.Client/ArenaMapPort.Client.csproj -c Release
    -p:ArenaMapPortCreateReleaseKeys=true -p:ArenaMapPortCreateKeyMap=arena_autoservice` once, without DllOnly (`ls
    work/release-keys` first; the tool prints `CREATED ... BACK UP`: copy `work/release-keys` off the VM at once), `verify-keys`
    against every Arena build at hand, server build, `validate_distribution.py` and `validate_port.py --distribution`, a dry run
    and then `deploy-to-spt.ps1 -SptRoot 'D:\SPT41' -Confirm` (close EFT first; about 0.9 GiB more through Syncthing, check the
    GPU-2 free space), EFT restart, `gpu-run raid --map autoservice` without bots, log baseline (NavMesh line, spawn openings,
    warnings, shader resolution), `uibridge_teleport dryRun` from both pockets to the capture point and the yard, a raid with bots,
    the hideout once, max frame time. (done 2026-11-11: key created and verified on three Arena builds, deployed, two GPU-2 raids and the
    hideout, 0 warnings; see the record.)
15. **Host iteration U2..Un**: gates and pockets, borders and the tribunes, props the oracle flags, rain F8, bots, cross-map raids.

### Phase C - Native features and polish

16. Host verification of the native features (ballistics F10, 304 decals, light culling F7 with 184 culling and 78 volumetric
    lights, rain cover with its single `IndoorTrigger`), the frame budget, the `IndoorFloorSmoothness` freeze, puddle footsteps
    and the wet mold decal.
17. MultiFlare host check (21 lights), DynamicMaps overview (`export_equator.py` table row, staging, `DynamicMapAssets`,
    `test_dynamicmaps_autoservice.py`), shot-acoustics room classes, the ambient mix by ear (offset against the water drops),
    the `SMap Baked` shader decision, the lighting zero level, gate sounds listened to with `Spawns/SpawnGateHold`.

### Phase D - Release

18. Release texts, version, dist manifest, cut, VirusTotal, key backup, tag and upload only after the user's decision on number
    and form **(user)**.
19. **ArenaMode hand-off (user)**: catalog entry and optional-maps revision, a team formation from the two `SPAWN_BOUND_Fighter`
    boxes (10 + 10), the single capture point, the four gates through `SpawnGates.GatesOpened`, the four monitor faces; no
    resupply crates and no spawn-protection triggers exist in TeamFight; `SpectatorCrowd.IsAvailable` stays false.

## 2. Autoservice-specific risks

1. **Navigation**: the spike's bake was a renderer union; four gate leaves sit on layers 0/12/18 and Arena's mesh was baked with
   them open; two roller frames and the big gate's rails stay standing; the corridors are tight (1-2.4 m clearance 4-5 m inside
   the gate mouths); Arena's mesh covers nothing above y -0.43 (tribunes), so the fresh bake decides what is walkable up there.
2. **Borders**: 372 solid cubes that do not close the perimeter alone. A cube on a legitimate route shows up as a NavMesh cut in
   the oracle or a wall on the host; a second island at z 521-528 lies outside the cubes.
3. **Door tool blockers** (S2): `clossed`, the exclusive `MAP_SOURCES`/`DOT_SOURCES` dispatch (clips and lamps need both paths), controllers in `sharedassets70.assets`, and Arena's inconsistent
   `equator_roller_shutters_01` closed pose (0.978 m); the bake and the visual disagree until the override is reviewed.
4. **Pocket rule margin**: the pocket split passes at 2.1 against 2.0, and "north/south" are east/west.
5. **New audio class and states**: `SeasonLoopAmbientSoundPlayer`, minDistance 0, forced mixers and an inactive chain each stop
   the strict exporter today; the Winter-only loops never play in summer.
6. **Crowd of bodies** (resolved 2026-11-11: not a different problem, the stands pipeline exports them unchanged; the crowd is in): the 30 player-body rigs are a different problem from the stands; crowdless is the cheap start and a
   later crowd is a user decision.
7. **Official text and id**: the location id `6441004f8ab20b218807f14b` is confirmed by a lookup in `TestBackendLocaleEn` (its
   `Name` row reads "Chop Shop", as does the key `Arena_AutoService`; the four released ids carry the Name rows Equator, Bay 5, Air
   Pit and Bowl). Open only that it is Arena's location id and not the port's `id_field` (the official rows use Arena's own id and
   no `e6aa4591...` row exists), checked when the `DESCRIPTION_KEYS` row is written.
8. **Rain**: only one authored `IndoorTrigger` (MainRoom) and no `DryPlane` in the summer Scripts scene; the heuristic cover
   is the only depth photo input. Needs host evidence.
9. **Shaders**: `SMap Baked` (1 slot) may render magenta; fog sheet and cloth depend on Customs' shared assets being loaded.
10. **Size and key stability**: scenes bundle about 0.93-0.95 GB (0.86-0.88 GiB) plaintext (the spike's 708,353,294 B times the 1.31-1.34 growth of
    Bowl and Iceberg, **inferred**), `dist/` about 6 GiB with roughly 390 files (**inferred**), a six-map ZIP about 6.2 GB; the key source is one
    458.7 MB stream plus two small ones, byte-equal in size to 0.5.4, content equality inferred, checked only against the Arena
    builds at hand.
11. **Lock-step breadth**: about 12 sidecar files, 12 extractor tables, 8 client arrays or switches and 20+ tests; a list that is
    missed excludes the map silently (flares, water and overview lists are explicit "has it" lists); after the promotion a deploy
    to a host before the first stage (item 14) is refused or raises the incomplete-install notice.
12. **Light budget**: 189 + 12 realtime lights is small against Iceberg's 1,126 culling lights; 17 `CullingAdvancedLightObject`, 29
    `AreaLight`, 6 `StencilShadow` and 2 `AnalyticSource` are not covered by the exporters (dark signage is a host question).

## 3. Deliberately not in this plan

ShootOut and LastHero, the Winter scenes and any season switch, the airdrop points and the Loot scene, a spectator crowd (stands,
body-rig or port-only), the inactive `FansAmbient` and `ArenaFanSpeakerEntity` emitters, the inactive audio spline chain, Arena's
fixed night clock, native water, grass, shader stand-ins beyond the reviewed ones, `ArenaLevelSettings` (172
bytes, not decoded), and every ArenaMode change (the user's repository).
