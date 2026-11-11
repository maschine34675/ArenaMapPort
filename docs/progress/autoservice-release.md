# Autoservice release log (sixth map, branch `feature/autoservice`)

The plan is [docs/PLAN-AUTOSERVICE.md](../PLAN-AUTOSERVICE.md); this file is the dated record of what landed, with command output
and host evidence. Appended, never rewritten.

## 2026-11-08 - start: request, surveys, plan

- Request (user, German): "dann wuerde ich gerne mit der naechsten map weitermachen: Autoservice", the day after the 0.5.0
  publication (five maps) and after the user found the score monitors. A go without further constraints, as Iceberg's was.
- Branch `feature/autoservice` from `main` = 52d0a4d, clean tree. Nothing was built, deployed, committed or pushed in this
  step; the four surveys wrote only under the ignored `work/tmp/autoservice-survey/` (s1 to s4). Arena was only read.
- Four read-only surveys on the VM's Arena 0.6.2.0.48473 and the spike package. Findings that shaped the plan:
  - **S1 (source delta and identity).** Installed BuildSettings (388 scenes) lists the Autoservice scenes at the same indices as
    the spike's 0.5.4 plan: 62 main, 63 AI, 65 DesignStuff, 66 Light, 69 Scripts, 112 Sound, 135 Culling, 136 TeamFight_DesignStuff,
    243-246 Winter set (also 64/68/71 temp scenes, 67 Loot, 70 ShootOut_DesignStuff, 172 LastHero_DesignStuff). The unchanged plan
    re-extracts read-only to 32,097 objects, 4,270 meshes, 402 materials, 872 textures, 116 warnings, 0 errors. Real 0.6.x delta:
    `DesignStuff` 124 -> 497 objects (the new `Arena_AutoService_LevelBorder` root, 372 `Cube` children on layer 29, `BoxCollider`
    4 -> 376) and two new `metal_sheet_hanging_01_rusted` props in the main scene (25,678 -> 25,692); `TeamFight_DesignStuff` 4,710
    and `Light` 1,198 identical. All object, mesh (730) and texture (110) ids rehash, so the spike package cannot be reused. The
    preset offers ShootOutSolo/Duo/Trio, TeamFight, Coop and LastHero; no MongoId field, only the ServerName. Key source: three
    streams, `sharedassets62.assets.resS` 458,749,216 B, `sharedassets62.resource` 2,036,672 B, `sharedassets66.assets.resS`
    4,156,800 B (464,942,688 B), equal in size to 0.5.4. Identity art under the name `chop-shop`: banner path 1738 (769 x 460),
    loading screen path 1731 (1920 x 1080), `Arena_MainMenu_Paralax_ChopShop-1..4`; official name and description rows exist in
    en, ge and ru under Arena's own id `6441004f8ab20b218807f14b` (confirmed by a lookup in `TestBackendLocaleEn`: the `Name` row of that id and the key `Arena_AutoService` both read "Chop Shop"; no text copied).
  - **S2 (gameplay objects, modes, navigation).** TeamFight is the natural mode: 20 `SpawnPointMarker`s (all "Usec house") in an
    east pocket (x about -337) and a west pocket (x about -409), two `SPAWN_BOUND_Fighter` bounds, one capture point at (-369.29,
    -3.80, 499.49), four gates, no kill, spawn-protection or minefield triggers, no resupply crates (LastHero only). Pocket split
    ratio 2.1 against the limit 2.0, names north/south are east/west. Arena's NavMeshData (`sharedassets63.assets` path 2): 695
    polygons, 1,212 triangles, 19 of 30 tiles, main component 1,915.6 m2 with every marker and AI point; surface volume x -421.2..
    -319.4, z 446.0..527.65, mask 0x40042A00; nothing above y -0.43 is on Arena's mesh. 372 border cubes (AABB 80.6 x 47.3 m)
    plus props do not close the perimeter in plan view; the `ExcludeLevelBorders` polygon is a culling outline. Gates: Mecanim
    clips with the spelling `clossed`, controllers of level 136 live in `sharedassets70.assets`, `equator_roller_shutters_01`'s
    scene collider sits 0.978 m below its clip's closed pose; decoded drives: big gate +4.40 m in 8.0 s, rollers +4.11 m in 6.733
    s, equator shutter +2.56 m in 4.5 s; four lamp `DotAnimator`s. Four `EntitySoundPlayer` gate clips (10.43, 4.68, 8.01, 7.90 s).
    Four score-monitor holders with faces (2 timer, 2 alive). AI scene: 16 bot born points, 7 core points, a 5-hub PatrolWay with
    59 points, 23 mine points; stale far-field leftovers (2,696 bearing points, 44 inactive navmesh blocks) lie 400 m or more out.
    ShootOut has 27 markers (15 on tribunes) and four more TVs, LastHero 24 markers, one bound and two refill crates.
  - **S3 (environment, rendering, audio).** Fully realtime lighting: 189 + 12 `Light`s, 0 lightmaps, 0 probes, no Directional
    light in the imported scenes (Scripts 69 holds the sun at 0.5); existing decoders accept 304 decals, 184 culling lights, 78
    volumetric lights, 0 flicker. Strongest emissive 3.88 (no cap needed). 33 distinct shaders (census of 409 materials; the earlier 34 is not reproducible), all present in EFT 0.16.9.5; four need a
    check (FogSheet and two Cloth shaders resolve from Customs per the AirPit evidence; `SMap Baked` lives in `sharedassets216`).
    No water, grass or terrain; puddle material `0877f2335044042e` (42 slots) plus one wet mold decal. Rain cover: 1
    `IndoorTrigger`, 0 `DryPlane`. Spatial exporter runs clean with no repairs (7 rooms, 16 portals, 9 areas, 139 routes). Ambient:
    42 players incl. 3 `SeasonLoopAmbientSoundPlayer` (a class the repo does not know), 9 `water_drops` with `minDistance` 0, forced
    mixers and an inactive spline chain: new reviewed contract work. 30 `SCAV_SPECTATORS` fans are full player-body rigs in the
    TeamFight scene, `DesignStuff` has none. 21 `FlareLight`s pass the existing checks (tuple (21, 8, 77, 32)).
  - **S4 (pipeline gap).** Autoservice is Catalog-only with a spike identity ("Arena: Auto Service (spike)",
    `ArenaMapDescriptor.cs:1210-1224`), parked in `park-unreleased-dist.ps1:31` and `build_remaining.ps1:27/35`. A sixth released
    map needs about 12 sidecar files, 12 extractor tables, 8 client arrays or switches, the client csproj staging and key-create
    guard (lines 190-507), the lock-step release lists and 20+ tests; Iceberg's promotion `1bd5868` (79 files) is the checklist.
    The server C# is data-driven (base.json, csproj rows, banners, description pack). `Bay5AudioImporter` throws for an unknown map
    (two switch rows). Size: spike scenes 708,353,294 B, growth 1.31-1.34x on Bowl and Iceberg, so about 0.93-0.95 GB (0.86-0.88 GiB) plaintext
    (inferred); `dist/` is 359 files, 5.03 GiB now and about 6 GiB after.
- Plan decisions in short (agent): TeamFight alone; `UsesServerTime`; summer set only, no winter; no grass; keep the 372 border
  cubes (`keep_level_borders`, layers 9/11/13/18/29/30); bake bounds from Arena's NavMeshSurface volume; team sides by bound; four
  gates opened and holdable, door tool patched (`clossed`, level 70 clips, equator-shutter override); four gate sounds; four monitor
  faces; crowdless (CROWDLESS_MAP_KEYS); official description once the id is confirmed; chop-shop art after looking at it; ambient
  contract with the new season-loop class; spatial acoustics without repairs; no emission cap; no native water; MultiFlare yes;
  DynamicMaps overview in Phase C; stays Catalog-only until item 12.
- Open questions: (1) closed: the id is confirmed by the table lookup, only the check against the port `id_field` remains (item 9); (2) which season Arena loads
  live and whether a winter variant is wanted (user); (3) crowd of the 30 body-rig fans or crowdless (user); (4) do the frame
  colliders of the roller gates leave a doorway after the posed leaves are dropped (`navmesh_diff.py --doors`, then a host walk);
  (5) is the 0.978 m closed-pose gap of the equator shutter a scene inconsistency or a clip offset; (6) who closes the north edge
  and the second island at z 521-528; (7) are the tribunes (y 4.4 and above) walkable in the port (the fresh bake decides);
  (8) the rain heuristic with one `IndoorTrigger`; (9) `SMap Baked` shader at raid start; (10) the wet mold decal as a puddle
  surface; (11) the 7 textures with the same id but different size between the spike and 0.6.2; (12) `ArenaLevelSettings` and the
  mixer group name of the gate players are not decoded. Survey discrepancy to settle in item 5: S1 lists three key-source files
  (scenes 62 and 66 only) while S2 calls `sharedassets112.resource` a likely key-source file; the key source is the port's plan
  scenes (112 is not one), and the gate-clip copy check runs anyway. S4's note that the description-count test holds only if
  Autoservice has no official text is wrong for `All.Count - 1`: five of six maps with a text is still `All.Count - 1`.
- Next step: Phase A items 1 to 3 (housekeeping and plan roles, package v1 with `PYTHONUTF8=1`, the native contract), then the
  `SeasonLoop` audio work (item 4) and the key-source rows (item 5). Nothing builds or deploys before item 12.

## 2026-11-08 - user decisions: summer, crowdless, server time; Phase A starts

User (2026-11-08): "sommer reicht, crowdless passt, serverzeit ok, leg los mit phase A". The plan's three agent decisions on season, crowd and time are the
user's now (section 0 marks them); open for the user remain the release number and form and the ArenaMode hand-off. Phase A (items 1-5) starts on
`feature/autoservice`: housekeeping and plan roles, package v1 with native features, the native contract, the ambient-audio contract and package v2, the
stage-2 key-source rows. Results are appended below as they land.

## 2026-11-08 - Phase A: housekeeping, package v1 and v2, native contract, audio contract, key-source rows

Phase A (PLAN-AUTOSERVICE items 1 to 5) ran offline on `feature/autoservice` after the user's go ("sommer reicht, crowdless passt, serverzeit ok, leg los mit
phase A"). Arena 0.6.2.0.48473 (`D:/Battlestate Games/...`, read only). No Unity build, no `dist/`, no deploy, no commit, no push; Autoservice is not in
`ArenaMapRegistry.All`; no content key exists or was printed, and the key-source rows carry paths and sizes only. Python runs used `PYTHONUTF8=1` and
`PYTHONIOENCODING=utf-8`.

**Item 1: housekeeping and plan roles.**
- `work/build/autoservice` was moved to `work/build/autoservice-spike-20260915` (the target did not exist).
- `tools/extract/plans/autoservice.json` has the roles scripts 69, sound 112, ai 63, culling 135 in Iceberg's shape; `Scripts_Winter` (243) has no role. Plan
  digest `3e9de53e` -> `a893914d` (the package manifests record `a893914d`).
- `tools/build_remaining.ps1` drops `autoservice` from `$Maps` and `$allMaps` and throws for it with the plan's reason (`pwsh tools/build_remaining.ps1
  -Maps autoservice` throws; the script parses with 0 errors).

**Item 2: package v1** (`work/extract/autoservice-native-v1`).
- The first extraction stopped with `FAILED: Ballistics: preset/material disagreement`: six `rabble` colliders of the `logan_wheel (N)/balistic_col` props of
  level62 (`Autoservice_Little_Office/logan_wheel (1)`, `Props/logan_wheel (3)` and `(4)`, `Autoservice_Main_Office/logan_wheel (1)`, `(2)`, `(3)`) carry the preset
  Pebbles over the material Rubber (hit role 0/0, surface sound 0, layer 12, one shared mesh). Reviewed as rubble on a wheel prop and imported as authored:
  `("Pebbles", "Rubber")` joined `REVIEWED_PRESET_MATERIAL_PAIRS` in `tools/extract/extract_ballistics.py`, pinned in `tools/tests/test_ballistics.py`; the
  importer has no pair list. (The edit of `extract_ballistics.py` was not on the stage-1 owned list; the answer to the ask lives there.)
- Second run (3 min 50 s): `python tools/extract/extract_map.py --plan tools/extract/plans/autoservice.json --native-features --target
  D:/SPT41/EscapeFromTarkov_Data --output work/extract/autoservice-native-v1`: `DONE arena_autoservice: 32097 objects, 4270 meshes, 479 materials, 950
  textures, 116 warnings`, exit 0. `refresh_native_volumes.py`: `NATIVE_VOLUMES_REFRESHED count=0 ... manifest untouched`. `validate_extract.py --package
  work/extract/autoservice-native-v1 --extractor-timeout 1800`: `OK -- all checks passed ... (5 non-blocking warning(s))`, exit 0, both reproducibility runs
  byte-identical, freshness 0 diffs, 10 min 56 s. (An earlier run failed with `unsupported or missing native map identity` while the contract file was new
  and another agent's loader edits were mid-flight; a direct `validate_package` passed seconds later and the repeat run is the record.)
- Numbers: 4 scenes (62: 25,692 objects, 65: 497, 136: 4,710, 66: 1,198). Survey differences: 479 materials and 950 textures, not 402 and 872 (the native
  decals add 77 and 78); the 116 warnings are 97 "no mesh assigned" plus 19 cubemap notices; objects and meshes match. Shader census 28 distinct names over the
  479 materials (the survey's 27 over 402 plus one name from the decals; the "33 over 409" and earlier 34 do not reproduce, the extraction wins); pinned in
  `AutoserviceContractTests` with 77 decal materials and 1 `SMap Baked` material. The 0.5.4 -> 0.6.2 delta (DesignStuff 124 -> 497, main scene 25,678 ->
  25,692, +387 objects, 0 names removed, 372 new names) is in [autoservice-source-062.md](autoservice-source-062.md).

**Item 3: native contract** `tools/extract/native_contracts/autoservice.json` (v1: `audio: null`). Pins: scenes in plan order 62, 65, 136, 66;
BallisticCollider 62: 2,897, 65: 18, 136: 8, 66: 194 (3,117); StaticDeferredDecal 304; CullingLightObject 184; VolumetricLight 78; LightFlicker 0;
NavMeshModifier 30 (10 + 20), NavMeshModifierVolume 7 (6 + 1); SmallPhysicsObject 134, all in level62 (SoundType 0 x14, 4 x34, 6 x6, 8 x2, 9 x19, 10 x19, 11 x40 =
Books, GlassBottle, MediumGeneric, Monitor, SmallBox, SmallPaper, SmallPlastic); rain cover 1 IndoorTrigger / 0 DryPlane / no WeatherObstacle;
`require_unsliced` true, `implicit_scene_identity` false. Every number equals the survey's expectation. Tests: `AutoserviceContractTests` in
`test_native_contracts.py`, the contract-set and file-list pins gain `arena_autoservice`.

**Item 4: audio contract and package v2.**
- Census of level112 (`Arena_AutoService_Sound`, plan role `sound`; exporter's own parsers): 38 exported players (22 `AmbientSoundPlayer`, 16
  `SeasonAmbientSoundPlayer`), 2 `AmbientSoundPlayerGroup` (`CommonAmbientInGroup` 18 on `CommonAmbInEffects`, `CommonAmbientOutGroup` 20 on
  `CommonAmbOutEffects`), 13 banks, 113 clips, every player grouped once (`all_players`), none inactive, no loops. The strict check passes on the extended
  exporter; the scratch export is deterministic (two runs, one hash).
- Differences from the plan: the nine `water_drops` are `SeasonAmbientSoundPlayer`s, not `AmbientSoundPlayer`s; only four `far_fight` players force
  `CommonAmbOutBypass` (the plan's "fifth, on `CommonAmbInEffects`" is the ungrouped inactive chain player `level112:469`, dropped); three Season players have
  Winter-only banks (exported, silent outside winter, as Iceberg's).
- New optional contract states (default `{}`, the five released contracts are unchanged): `dropped_players` (4: ids 415, 486, 495 = `indoor_winds_winter`,
  `(2)`, `(1)`, and 469), `dropped_support` (3: 408 `LoopAmbientSoundPlayer_spline_emitter` with the `SplineEmitterPathMover`, 435 `spline`, 468 the mapped
  emitter), `zero_min_distance_players` (9: ids 389, 399, 443, 454, 458, 459, 470, 478, 490 = `water_drops` and `(1)` to `(8)`), `forced_mixer_players` (4: ids
  393, 395, 411, 481, all `CommonAmbOutBypass`). A dropped id may not carry any other pin.
- Decisions: (a) the three `SeasonLoopAmbientSoundPlayer`s (Winter-only) are dropped, not exported (summer set only; exporting them needs an importer branch, the
  client driver and a Winter WAV): every season-loop player must be pinned, a droppable one must have exactly the clip key `Winter`, the index gets a
  `dropped` audit block only when something was dropped; in an SPT session whose season is Winter the three indoor wind loops are missing. (c) the four
  `far_fight` players are pinned to `CommonAmbOutBypass` but routed like their group (`CommonAmbOutEffects`) through the existing `mixerPolicy` "group" marker,
  as Bowl's `metal_rigging`: the authored bypass tone is lost (honouring it needs client work in `Bay5AmbientAudio`; host-ear list). (d) the spline chain is
  dropped (its chain player must be inactive and in no group). (f) start mix in `AmbientAudioDefaults`: 3 dB, offset 0.2, range 3 (offset: the nine 0.07
  drops land at about 0.30 against 0.37 to 0.48 for the other interior players, computed not heard; Bowl's 0.4 would put them near 0.50; range 3 is the plan's
  value, untried).
- Code: `extract_bay5_audio.py` (parser, drop rules, `validate_graph`), `native_contract_loader.py` (the two keys), `validate_native_features.py`
  (`with_dropped_members`, `check_dropped_source`, dropped-pin comparison in `check_audio_group_states`), `Bay5AudioImporter.cs` (`AutoServiceRootName`,
  `RootNameFor`/`AssetFolderFor` rows), `AmbientAudioProfiles.cs` (profile in `Staged`, not in `Table`: Autoservice is Catalog-only, a `Table` row would bind F12
  keys and break the released-set tests; item 12 moves it) and `AmbientAudioDefaults.cs` (Values, PreviousValues, HasLegacyMix rows). Profile: 38 players, two
  routes, `usesSeasons` true, season kind 16, loop kind 0, display name `AutoService`, root `ArenaMapPort_AutoServiceNativeAudio`.
- Registration (this stage): the contract's `audio` block (schema 2, `level112`, from the reviewed proposal, states above, `group_mixers` in source order
  `CommonAmbInEffects`, `CommonAmbOutEffects`) and `AUDIO_EXPORTERS["arena_autoservice"] = ("ambient_audio", extract_bay5_audio.export)` in
  `tools/extract/native_features.py`; the contract's `audio: null` note is replaced by an audio note.
- **Package v2** (`work/extract/autoservice-native-v2`): `python tools/extract/extract_map.py --plan tools/extract/plans/autoservice.json
  --native-features --target D:/SPT41/EscapeFromTarkov_Data --output work/extract/autoservice-native-v2`, 14:42:14-14:47:06, exit 0: `DONE arena_autoservice:
  32097 objects, 4270 meshes, 479 materials, 950 textures, 116 warnings` (as v1), `required = ['ballistics', 'ambient_audio']`, `native_audio` players 38,
  banks 13, clips 113, schema 2, `native-audio.json` sha `78ebed64...e1a9f` (equal to the scratch export), 114 files under `audio/` (73,118,730 B).
  `refresh_native_volumes.py`: `count=0 ... manifest untouched`. `validate_extract.py --package work/extract/autoservice-native-v2 --extractor-timeout 1800`
  (14:47:16-14:59:31): `OK -- all checks passed ... (5 non-blocking warning(s))`, `optional native feature source/clip/reference contract validates`, both
  reproducibility runs byte-identical, freshness 0 diffs, 28 distinct shaders.

**Item 5: key-source rows.** `tools/bundle/key-sources.json` (schema 2, CRLF kept) gets `arena_autoservice`: 62 `[sharedassets62.assets.resS,
sharedassets62.resource]`, 65 `[]`, 66 `[sharedassets66.assets.resS]`, 136 `[]`. Sizes checked by file size only on 0.6.2.0.48473 (no hash, no sample read):
458,749,216 + 2,036,672 + 4,156,800 = 464,942,688 B in 3 files, as the plan; `level62/65/66/136` exist. Sound scene 112 is not one of the port's imported scenes
and is not a key source, so the key-source copy check cannot trigger for the gate clips of `sharedassets112` (the extractor's copy check itself was not run).
`test_bundle_crypt_tool.py`: Autoservice is a `PENDING_MAPS` entry (the "exactly the released maps" test expects released plus pending and asserts the two sets
do not overlap, so a promoted map must leave `PENDING_MAPS`), `LISTED` has `(3, 464_942_688)`, the extraction-plan test and the accepted-package map
(`autoservice-native-v1`, key scenes a subset of the package's `scene_registry`) include it; the C# `BundleCryptoTests.cs` mirrors it. The size checks used to
run only on the build equal to `arena_build` (0.6.1) and so were skipped on this VM; they now also run on any 0.6.2.* build (the streams are size-identical across
the hotfix; every map's listed size, the five existing included, passed on 0.6.2.0.48473). The first stage (key creation) runs only after a U1 bundle passed
every validator.

**Gates (2026-11-08, after package v2):** `python -m unittest discover -s tools/tests -p "test_*.py"` (`PYTHONUTF8=1`): `Ran 2913 tests in 997.913s`, `OK (skipped=67)`; `dotnet test ArenaMapPort.Server.Tests/ArenaMapPort.Server.Tests.csproj -c Release --filter "FullyQualifiedName~Ambient|FullyQualifiedName~Contract"`: `Bestanden! Fehler: 0, erfolgreich: 130, uebersprungen: 0, gesamt: 130` (this includes the five `AutoServiceAmbientAudioTests` and `ProfilesMirrorTheExtractionContract(arena_autoservice)`, which failed with "no audio block yet" before the block was registered); the full .NET suite `Fehler: 0, erfolgreich: 3227, uebersprungen: 0, gesamt: 3227`. `test_native_contracts.AutoserviceContractTests` pins the audio block and gained a package-v2 manifest test.

**Decisions of the plan that are the user's now:** summer set only, crowdless (`CROWDLESS_MAP_KEYS` at item 12), server time. Open for the user: release number
and form, the ArenaMode hand-off.

**Next:** Phase B (items 6 to 11: mode objects and spawn sidecars, doors, gate sounds and score monitors, NavMesh oracle and bake inputs, identity and descriptions,
spatial acoustics, light, flares and shaders offline), then item 12 (lock-step promotion). Nothing builds or deploys before item 12.

## 2026-11-11 - Phase B item 6: mode objects and spawn sidecars

Branch `phase-b/modes` (two commits on `feature/autoservice` bd64fc4: c40bf02 and the review fix d97bea8). Offline: no Unity build, no deploy, nothing under `D:/SPT41`, Autoservice still Catalog-only. Source: package `work/extract/autoservice-native-v2`, the installed Arena 0.6.2.0.48473 (read only), `PYTHONUTF8=1`. The worktree started on `main` and was reset to bd64fc4 first.

**Modes sidecar.** `extract_mode_objects.py` gets `SIDE_BOUND_RULES` (a `SideBoundRule` of two `SideRule`s) next to `TEAM_BOUND_RULES`. Autoservice's 20 markers all carry the stem `Usec house`, so the Iceberg team rule cannot be used, and the ordinary pocket split passes only marginally: `python tools/extract/extract_mode_objects.py --package work/extract/autoservice-native-v2` without a rule gave cut 66.2853 m / wider pocket diameter 31.2284 m = 2.1226 against the limit 2.0, and named the pockets north (x -337.8, mean z 492.8) and south (x -408.3, mean z 488.2), which are east and west. With the rule: `arena_autoservice: capture point [-369.29, -3.8, 499.49] (capture_point_light_centroid), 2 spawn bound(s), markers east 10, west 10 (bound containment)`. A marker belongs to the side whose published spawn-bound box contains it (0.05 m edge tolerance); it fails closed on a marker in no or two boxes, unequal counts, an unknown/missing/repeated bound or a second scene. East = `SPAWN_BOUND_Fighter` (centre (-338.4315, -2.8034, 492.7926), markers (1),(2),(3),(4),(8),(16)-(19) and the unsuffixed one), west = `SPAWN_BOUND_Fighter (1)` (centre (-408.2015, -2.8033, 492.4696), markers (5)-(7),(9)-(15)). Measured: every marker lies 2.17-5.77 m (east) / 2.95-4.00 m (west) inside its own box and more than 60 m outside the other. The record carries `side_rule.generic_pocket_split` (cut, spread, ratio 2.1226, limit 2.0) so a change of the margin is visible; `test_mode_objects.AutoserviceSidecarTests` pins the ratio to 4 places. `capture_point` stays the single-light centroid (`CAPTURE_POINT/BOWL_CAPTURE_POINT_LIGHT_03`), no `capture_points`. The sidecar keys are `east`/`west` (counts `spawn_markers_east/west`). `test_mode_objects` Ran 103 OK, 0 skipped, including the fresh-extraction equality of the equator, bay5, airpit, bowl and iceberg sidecars (all byte-identical).

**AI sidecar.** `python tools/extract/extract_ai_anchors.py --plan tools/extract/plans/autoservice.json`: `7 core point(s), 5 hub(s), 54 sub point(s) -- 59 PatrolPoint object(s) total, all consumed`. Everything the tool decodes lies on the map, so nothing is dropped: `PLAY_AREA['autoservice']` (Arena's NavMeshSurface volume, x -421.2..-319.4, z 445.9..527.7) is a fail-closed guard and no `dropped` key is written. The stale far-field data of the plan are other classes the tool never decodes. A new per-map `AI_SCENE_DATA` row adds three blocks to `autoservice.ai.json` only: `born_points` (16 `SpawnPointMarker` objects `p1`, `p1 (1)`..`p1 (16)` without (3); 5 in each pocket, 6 in the yard), `mine_points` (23 `AIMinePoint`s `AMinePlace_N`, AI data, not minefields; the counts are pins; 21 of 23 lie on Arena's main NavMesh component, `AMinePlace_15` and `AMinePlace_44` lie off it) and `scene_census` (every transform of the AI scene grouped by root with its horizontal distance from the play-area centre; radius 400 m). Census, measured: 3,021 transforms (19 containers at the world origin, 136 near, 2,866 beyond 400 m): ParentOfBearingPoints 2,696 null-script `base_navmesh_point` objects (543-682 m), AI_SUSP 93, NavmeshObstacles 40 inactive cubes, Shuttle1 1, AIPlaceInfoHolder 4, 31 of BotZone's 107 transforms; BotCellController has its root at (-258, 0, -342) and one on-map `Cube` child at (-380.2, -3.8, 489.8). None of it is exported. The five released AI sidecars regenerate byte-identical (equator, bay5, airpit, bowl, iceberg compared in scratch). `.NET AiAnchorsTests` got the autoservice rows (7/5/54, ids, positions, no connection fields) and a Fact for the three blocks; `test_ai_anchors` Ran 67 OK.

**Bots and exits.** `autoservice.bots.json`: four player rows on pocket markers, the plain raid's faction split copied from Bowl (a guess): east Usec = marker (4), All = (1); west Bear = (6), All = (14); border clearance 1.74-3.86 m. Seven bot rows (radius 25, `Bot`/`BotPmc`): cores 1, 2, 3, 4, 5, 7 and born point `p1`. Rule as Iceberg's: no bot spawn within 10 m of the swept footprint of a gate or of a pocket marker, none within 10 m of an exit centre, >= 1.35 m Arena-mesh clearance (rows with less move onto >= 1.5 m within 3.5 m, else drop). Measured: core 6 (9.0 m from the east big gate, 0.83 m clearance, no admissible spot) skipped; the 10 pocket born points (0.5-4.3 m from a marker) and born 12/13 (8.2/7.5 m from the big gate) skipped; born 1 duplicates born 0 (1.8 m); born 7/8 have 0.54/0.55 m and no spot with 1.5 m; moves: core 1 0.71 m, core 3 0.56 m, core 4 1.95 m, born 0 0.71 m (all rows y = Arena's surface). Waves and boss waves are AirPit's (equal to Bowl's and Iceberg's). Coverage (measured, not asserted): with 7 rows 1,343 of 1,931 one-metre cells of the main component (69.6 %) keep three rows 25 m or more away, 80 (4.1 %) keep none; every pocket cell keeps at least 5; the south-east of the yard has no row. `autoservice.exits.json`: Chop Shop East Gate (-349.0, -3.77, 488.5) and Chop Shop West Gate (-393.0, -3.79, 501.0), 4 x 3 x 4 m, 8 s, Individual. Offline verification: both on Arena's main component, clearance 3.49 m / 3.37 m against the 2.83 m half diagonal (+0.5 m bar = 3.33 m), 9.94 m / 9.26 m from the nearest gate root, 4.8 m / 9.05 m from the nearest swept gate footprint (box edge 2.8 m / 7.0 m), 10.73 m / 13.71 m from the nearest marker, >= 10 m from every player and bot row, no port solid above the floor inside the box. The plan's east position (-349.0, 489.0) keeps only 3.30 m (below the bar), so it moved 0.5 m (a deviation, data over plan).

**Tests.** New `tools/tests/test_autoservice_spawns.py` (Ran 28 OK, 0 skipped): sidecar shape and generator (11 SpawnPointParams, 5 waves, 4 boss waves, 2 exits), player rows = marker world positions, the bot rule both ways, no row in a team box, spacing, exits; Arena's NavMeshData `sharedassets63.assets` path 2 (30 tiles, 695 polygons, identity, main component 675 polygons 1,915.6 m2, second island 20 polygons 33.8 m2; all 20 markers, the capture point, 7 core, 16 born and 59 patrol points on the main component; 21 of 23 mine points, `AMinePlace_15` and `_44` are off); placement probe (all 20 markers valid, floor within 0.05 m; rows, core and born points valid; exit boxes empty above the floor; swept-footprint table equal to the package colliders within 0.02 m). The probe needed no extension. Gates: `dotnet test -c Release` 3232 passed, 0 failed, 0 skipped (3227 + 5); the full Python suite 2977 tests, 2 failures that are worktree conditions (no `dist/` client DLL for `test_dll_versions`; CRLF working-tree files vs `test_score_monitors`' no-CR assertion with `core.autocrlf=true`), 132 skips (a worktree without work/dist); the touched modules plus iceberg/airpit/navmesh_diff/probe ran OK with 3 skips (AirPit tests with a hardcoded repo-relative `work/` path).

**Decisions and reasons.** Sides by containment, not by the generic split (2.12 vs 2.0 is too thin a margin and the names would be wrong). AI extras under an explicit table row so no released sidecar changes. Exit names use the player-facing "Chop Shop". Measured versus inferred: every number above is measured from the package, Arena's NavMeshData or Arena's AI scene; that the faction split, the 10 m rule radius and the bot-row set are good for play is inferred and needs the first host raid (`uibridge_teleport dryRun`).

**Open.** Exit names vs locale key; a bot row in the yard's south-east; the faction split guess; host confirmation of every point; item 7's equator-shutter override changes one entry of the gate table in `test_autoservice_spawns.py`.

**Review fixes (d97bea8).** One finding (modes-rules-R1, low): the `mine_points` bullet in the module docstring of `tools/extract/extract_ai_anchors.py` named only `AMinePlace_15` as lying off Arena's mesh and implied the points are on the component. The measured data (and `test_autoservice_spawns`, which pins `OFF_MESH_MINES = ("AMinePlace_15", "AMinePlace_44")`) show 21 of 23 on the main component and both of those two off it. The bullet now says exactly that. Comment-only change in one file, CRLF preserved (774 of 774 lines); `python -m unittest tools.tests.test_ai_anchors tools.tests.test_autoservice_spawns` Ran 95 OK with the env overrides. The .NET suite was not rerun (no C# or data change). The line "(AMinePlace_15 ...)" in the AI sidecar paragraph above is corrected to match.

## 2026-11-11 - Phase B item 7: Doors, gate sounds, score monitors

Branch `phase-b/doors` (commit 95c4df2, on bd64fc4). Autoservice stays Catalog-only. Offline only: no Unity build, no deploy, no staging, no key. All values below were measured on the installed Arena 0.6.2.0.48473 unless marked inferred.

### Doors (`tools/extract/extract_door_poses.py`, `tools/maps/autoservice.doors.json`, 23 rows)

Commands: `python tools/extract/extract_door_poses.py --map arena_autoservice --arena auto --package D:/ArenaMapPort/work/extract/autoservice-native-v2 --output tools/maps/autoservice.doors.json --csharp` -> `package check: 19 node(s) exist once in autoservice-native-v2 with Arena's closed transform`, `package check: 4 pose(s) exist in autoservice-native-v2 ...`, `== merged sidecar: 23 rows`, `wrote ... (23 poses)`; a rerun is byte-identical.

Findings and changes (plan item 7 / S2 / risk 3):
- All four gates (`DOORS/big_gate_metal`, `equator_roller_shutters_01`, `roller_shutters_techlight_01`, `... (1)`) carry their Animator on the gate root; the controllers live in `sharedassets70.assets` (external PPtr of level 136). `ArenaScene(..., extra_shared=[70])` loads that file beside the scene; level 70 (ShootOut) holds identical gates (every descendant's local transform equal within 1e-4, same controller path ids), which a test proves; the clips are read through level 136's own Animators, level 70 is not read otherwise.
- `clossed` is a closed clip name. All four controllers' default state is the `clossed` state, write defaults on (measured).
- Rows are per moved node: 19 clip rows (big gate 1, equator shutter 6, each techlight 6), not the plan's 4. World drives (composed through the parents): big gate +4.403 m along +z in 8.0 s, equator shutter +2.560 m in 4.5 s, techlights +4.109 m in 6.733 s: the plan's numbers. The clip curves are not all smoothstep (progress at 25/50/75 %: techlight 0.156/0.5/0.844 = smoothstep; equator shutter 0.118/0.609/0.991; big gate 0.08/0.44/0.919); the sidecar keeps the smoothstep stand-in (as Bowl).
- `DOT_SOURCES["arena_autoservice"]`: scan `DOORS` depth 1 with switches: barrier `DOORS`, parts LIGHT_1..4, nodes GREEN/RED (a switch row must name a node below its part). LIGHT_1/3 GREEN inactive->active, LIGHT_2/4 RED active->inactive: the plan's expectation confirmed. `DOORS` is a lamp-group barrier and the parent of the four gate barriers.
- `extract()` decision: a map in both tables runs both paths and merges (`extract_combined`: clip rows first, then lamps; same scene/level/assets/arena_version required, no node claimed twice). Pinned by `test_extract_runs_both_paths_for_exactly_the_maps_that_are_in_both_tables` (the intersection of the two tables must be exactly {arena_autoservice}; Bowl/Equator call one path). Released maps: Equator, Bay5, AirPit, Bowl, Iceberg doors sidecars regenerate byte-identical to HEAD (only the recorded `arena_version` 0.6.1 vs installed 0.6.2 differs).
- **The 0.978 m of `equator_roller_shutters_01` (decided):** `roller_shutters_01_COLLIDER`, `_BALLISTIC_metalthin`, `_SHADOW_LOD0` stand at local z 0 in both scenes; the clip's closed pose and the three LODs are at 0.978-0.981 (collider 0.835 m below the curtain in the world). Decision: the sidecar's closed pose is the SCENE's (z 0), via the pinned `accepted_closed_offsets` (0.978 +-5 mm per named node; an unused or mismatching entry stops the tool). Reasons: the client drops a whole part to the switch-off when a node is >5 cm/2 deg from the row's closed pose (`DoorPoseModel.PoseMatchPosition/Angle`, `SpawnGates` `PoseMatchesScene`); the scene collider reaches the floor so a held gate seals fully (a clip-closed collider would leave a 0.835 m gap under the curtain, inferred from the default state, not watched in a run); the bake drops all six posed nodes whichever pose is meant. All six end at the same open pose (local z 3.008, world y -1.78). Limit: during a drive the collider travels 2.56 m, the LODs 1.725 m.
- `validate_map_descriptors.PENDING_MIRROR_KEYS = ("arena_autoservice",)`: validates the staged doors/monitors sidecars in themselves, waives only the registry mirror and (while the descriptor has no `opened_barriers`) the barrier rule; main() refuses a released map in the tuple.

### Gate sounds (`extract_gate_sounds.py`, `gate_sound_fit.json`, `tools/maps/autoservice.gatesounds.json`)

`rows --map arena_autoservice` -> `4 row(s), 4 clip(s), 0 skipped; autoservice.gatesounds.json is current`. Four 100-byte `EntitySoundPlayer`s under `ARENA_GATES_SOUND` (level 112), each on its gate root (match distance 0.0), ArenaGates -15 dB, pitch 1.0, min 0.45, max 35/30/30/30; default join rules suffice (no MAPS option). Clips `Arena_autoservice_gates_outdoor_left` 10.4254 s (big gate), `_outdoor_right` 4.6785 s (shutter), `_indoor_left` 8.0096 s and `_indoor_right` 7.897 s (techlights). Fit rows in `map_clips.arena_autoservice` (event/body from 20 ms level scans: indoor bangs at 6.40 s and 6.28 s, outdoor_left a 7 dB step at 6.88 s then a fade, outdoor_right a clunk from 3.05 s): all `body_cut`, 54/55, 16/17, 49/50, 49/49 clean seams; at scale 0.5 the ends land 0.045, 0.022, 0.096, 0.038 s after the ideal. `clips --map arena_autoservice --output D:/ArenaMapPort/work/extract/gate-sounds` -> 4 clips, 5,470,412 B, files 4 WAV + `gate-sounds.json`. Key-source copy guard: `sharedassets112.*` is no key-source file of any map, `prepare_clip` passed, and the window search of every WAV against Autoservice's three key-source files finds nothing. Not listened to. Older maps: rows identical except `arena_version`; their 12 WAVs regenerate byte-identical in scratch (main folders not rewritten).

### Score monitors (`extract_score_monitors.py`, `tools/maps/autoservice.monitors.json`)

`--map arena_autoservice` -> `4 holder(s), 4 active (2 alive, 2 timer)`, `4 prop(s) found once in autoservice-native-v2 with the same activity`. Holders only in level 65: timers at (-400.9498, 8.1449, 489.424) yaw 90 and (-345.8202, 8.1699, 489.18) yaw 270 (3.127 x 1.884 m); alive at (-381.4771, 4.8499, 468.8919) yaw 2.2 and (-367.771, 4.7428, 509.5993) yaw 180.4 (2.244 x 1.352 m). Sidecar LF.

### Tests

test_door_poses 226 OK (27 new), test_gate_sounds 75 OK (15 new), test_score_monitors 87 with 1 failure (CRLF worktree checkout of the released sidecars, unrelated), 16 related modules 651 OK (40 environment skips elsewhere), validate_map_descriptors OK, .NET 3227/0/0 (needs `-p:BuildProjectReferences=false` in a worktree).

### Open points

Lamp-group barrier `DOORS` above the gate barriers and RED active->inactive in a lamp group (host check); the shutter's collider-leads-LODs drive; gate sounds not listened to; `navmesh_diff --doors`/bake behaviour of the unposed frame and rail colliders (item 8); full `unittest discover` not run here.

## 2026-11-11 - Phase B item 8: NavMesh oracle and bake inputs

Item 8 of [PLAN-AUTOSERVICE.md](../PLAN-AUTOSERVICE.md), offline only (no Unity build, no deploy, no staging, no key). Branch `phase-b/navmesh`, commit `f248552` (on top of `phase-b/doors` = item 7, merged fast-forward first). Arena 0.6.2.0.48473, package `work/extract/autoservice-native-v2`. Sidecar and descriptor are one commit, with `tools/tests/test_autoservice_bake_inputs.py`. Measured = read from Arena or the package or computed by the oracle; inferred = a column-model result that only the U1 bake and a host walk can confirm.

**1. Oracle row.** `navmesh_diff.ARENA_SOURCES["autoservice"]` = `ArenaSource("arena_autoservice", "sharedassets63.assets", 2, 0x40042A00, 63, "Arena_AutoService_AI", "work/extract/autoservice-native-v2")`. Measured: the NavMeshSurface (probe_mode_toggler) has layers 9/11/13/18/30, volume centre (-370.3, 2.0, 486.8) size (101.8, 121.99, 81.7), voxel 0.07, geometry PhysicsColliders; the NavMeshData has position 0 / rotation identity, agent 0.3 / 1.7 / 48 / 0.38, **695 polygons, 1,212 triangles, 1,250 vertices, 19 of 30 tiles**, two components: **675 polygons, 1,915.6 m2** (x -412.4..-333.7, y -3.82..-0.43, z 466.6..511.9) and **20 polygons, 33.8 m2** (x -336.2..-324.2, y -3.23..-3.09, z 521.3..527.7). Every plan number was reproduced. The 20 markers, the capture point (-369.29, -3.80, 499.49), the two exit drafts and the 66 AI points (7 core, 5 hubs, 54 patrol points) all stand on the main component, at most 0.15 m off its floor. The AI scene is the plan's `ai` role (test). Shared code: only a dict row was added; no function changed, the other three rows are pinned unchanged, no other sidecar or descriptor entry was touched (`git diff --stat`), so the five released maps' outputs cannot change.

**2. Oracle run** (`navmesh_diff.py --map autoservice --package <abs> --descriptor <candidate> --doors tools/maps/autoservice.doors.json --points <20 markers, capture, 2 exit drafts> --live-core-points`, about 15 s): 5,649 colliders, **2,346 bake sources** (SPAWN_BOUND* 2, Triggers layer 13 20, not in the layer list 3,272, opened_barriers 9). 47,951 Arena samples (1,918.0 m2): 47,865 passable for the port agent, 0 unsupported, **86 blocked** (3.84 m2: 7 border cubes 2.32 m2, 12 small props 1.52 m2); Arena's own agent meets 38 (107 without slack, 22 on Arena's layers only). Walked-through by Arena: 12 prop instances, 1.7 m2 (7 edge cubes: 26, 135, 196, 217, 377, 378, 379; Ladder_03 (1), Office_cabin_frame_01 (7), garbage_mix6_set_reserve, Garbage_set_04 (11), COPY_plywood_board_2 (1)), none a route. **One port-model component, 1,914.6 m2, all 89 probes in it.** Without the gate rows (no opened_barriers): 3 components (yard 1,380.8 m2, each pocket with its 10 markers), 263 blocked, 2,350 sources: the gates are the only thing that seals the pockets, and the rows open them.

**Frames and rails** (geometry from the collider vertices, plus the model): the posed leaves dropped are 9 colliders (big gate `gate/colider 1`, `gate/gate 1`, `gate/Vishel_Plane` (layer 0, 200 triangles, moves with the leaf), the Equator roller and both techlight rollers: COLLIDER + BALLISTIC each); 8 stand: the three roller frames (non-convex, 20 triangles: two posts and a header; Equator posts 0.04 m at z 505.89/507.73 with 1.80 m between, header lower edge y -1.75; techlight posts 0.07 m, 4.56 m between, header lower edge y 0.62) and the big gate's static header rail `colider` (y 0.85..0.97, 4.59 m above the floor; the big gate has no frame). The 8 header triangles of each roller are the only ones over the free span; no diagonal face closes it. In the model every Arena sample of each doorway slab is passable for the port agent (36/36, 10/10, 37/37, 38/38). Arena's mesh runs through the closed leaf of all four (8, 5, 9, 9 of 9 sample points). Equator roller: Arena's doorway floor is -3.62 (the shutter root at -4.34 lies below it), so the header leaves 1.87 m: tight for the 1.8 m player, listed under open points. The legacy drop-all rule would additionally drop the big gate's rail (2,345 sources).

**Cubes and routes.** Dijkstra on the modelled passable samples (4-neighbour 0.2 m lattice, so a Manhattan-like length) with the cubes as walls and without them gives identical lengths on all eight routes, and the same lengths as Arena's own mesh: pocket to pocket 106.0 m, east pocket to capture 59.2 m, west pocket to capture 56.4 m, east pocket to the east exit draft 35.2 m, west pocket to the west exit draft 26.8 m, exit to exit 62.8 m, pocket pairs 33.2 m and 26.4 m. Without cubes: 1,973 sources, 31 blocked, 1,916.8 m2 (the cubes take 2.2 m2 at the edge). No cube cuts a legitimate route.

**Layer 0.** Five colliders, not two: the vaz_2106 right front door (`colider 3` and `plastic 3`, MeshColliders at (-353.72, -3.32, 483.75), inside the layer-18 car housing footprint), the two SPAWN_BOUND_Fighter boxes (dropped by name) and `Vishel_Plane` (moves with the gate). Decision: layer 0 stays out of the list. With layer 0 added: 2,348 sources, 0 additional blocked samples, still 1 component; Arena's mesh has a hole at the door. Other dropped layers: 8 (330 = 30 spectator rigs x 11, crowdless), 12 (ballistic twins), 22 (139 small physics props). Layer census: 0:5, 8:330, 9:21, 12:2,805, 13:20, 18:1,936, 22:139, 29:373, 30:20.

**Fresh bake above Arena's mesh (inferred, column model of the port agent over the bake sources, floor candidates from up-facing faces, 0.2 m, grid ends at the ceiling).** The ground component (unique 0.2 m cells 1,997.8 vs Arena's 1,915.6 m2 polygons; bbox x -412.3..-333.7, z 466.7..511.9 = Arena's footprint; max y -0.45) is identical for ceilings 1.5, 2.0, 2.5, 8 and 14. Everything above -0.43 is an island with no ground route: ceiling 8.0 islands 1,910.6 m2, by band -0.43..1: 267, 1..3: 899, 3..5: 443, 5..7: 200, 7..10: 5 m2 (tribune decks at y 4.35-5.3, the west tribune cube tops at 6.78, roofs, cabin tops, the 96 m2 warehouse cap `levelBoarder` at y -0.5); ceiling 2.0: islands 1,631.5 m2 (658 m2 of them at ground level behind the walls and in closed rooms). Cube tops: all 372 cubes have an upward face (1,094.9 m2 projected), 236 tops at or below 2.0, 94 at or below -0.43. The tribune ladder/stair colliders exist; whether a player can climb up is a host question.

**3. Decided inputs** (`tools/maps/autoservice.json` and the `AutoService` descriptor entry, equal; `validate_map_descriptors.py` OK):
- `opened_barriers` = `start_gates` = the four unit paths exactly as the doors rows name them; `held_gates` [] (Arena authors none; the 'DOORS' lamp group is not a barrier). Each path names exactly one gate.
- `navmesh_bake_layers` [9, 11, 13, 18, 29, 30], `keep_level_borders` true (372 cubes `Cube (N)` under `Arena_AutoService_LevelBorder`, no object name starts with `LevelBorder`, so the name rule never applies), `navmesh_bake_exclusions` [] (no under-floor slab: the lowest large up-face is the road at -3.80).
- `bake_bounds` [-415, -331, -5, 2, 464, 515]: cube AABB x -413.15..-332.55, z 465.67..512.99 (margins 1.55-2.01 m), Arena's mesh inside with 0.75 m or more; y floor 1.2 m under Arena's lowest floor (-3.82), ceiling 2.0 = Arena's highest floor -0.43 plus the 1.7 m agent (1.27) with room; the ceiling only changes the island count (see above). The second Arena island (z 521.3..527.7) is outside the box on purpose; no probe lies on it.
- `uses_server_time` true (user 2026-11-08). `puddle_water_material_names` ["0877f2335044042e"] (42 `puddle2` quads, USERAIN, all active, mesh 2e93310068beccf4 as on the released maps; floor under the quads: Concrete 774, MetalThin 66, Plastic 53, WoodThin 40, MetalThick 25, Soil 9 of 1,050 samples, so no `puddle_extra_surfaces`); the wet mold atlas `aa54d9752ed43430` sits on four inactive `Heating_register_01_Decals_LOD0/1` renderers and is not listed. Not needed: `has_native_water` (no water), `emission_cap`/`floor` (strongest emissive 3.88), `shader_fallback_names` (item 11), `authored_borders` (the culling polygon is no movement border), `held_gates`, DoorPoses/exits/bots (item 12).

**4. Tests.** New `test_autoservice_bake_inputs.py` (50 tests: constants and validators, sidecar equality, the oracle row and CLI, the package border/gate/layer/marker/puddle audits, the Arena mesh pins, three cached oracle runs plus the layer-0 and closed-gate variants, two fresh-bake model runs; 75 s, 0 skipped with the env of the task). `test_navmesh_diff.py` gets the table pins, `test_airpit_bake_inputs` skips the new sidecar, and the C# Catalog-wide consistency tests name AutoService (`MapDescriptorConsistencyTests`, `Bay5MapTests`, `FixedRaidTimeWeatherTests`). Gates: .NET 3,227 passed (with `-p:BuildProjectReferences=false` in a worktree, see below), full Python discover 3,019 tests, 2 failures that are environmental (`test_dll_versions` needs `dist/`; `test_score_monitors` sees CRLF in working-tree sidecars under autocrlf), 132 skips of other maps' missing inputs.

**Open points:** tribune/stair climbability and the cube caps (host U2); Unity's real y clipping vs the model (`--compare-bundle` on the U1 bundle); the 1.87 m Equator header against the player; the bake honours 30 ignoreFromBuild modifiers and 7 NotWalkable volumes that the offline model does not (source count may differ from 2,346); the puddle mask/UV review is by identical ids, not re-read.

## 2026-11-11 - Phase B item 9: Identity and descriptions

Branch `phase-b/identity` (commit 905a689, on top of bd64fc4). Offline only; no Unity build, no deploy, no dist/ write, Autoservice stays Catalog-only.

**Art check (measured, then looked at with the image reader).** A UnityPy name search over resources.assets and sharedassets1.assets of Arena 0.6.2.0.48473 found exactly the plan's objects at the plan's path ids: Matchmaker_Banner_chop-shop (1738, 769 x 460, RGB24), AllScreens_Loadingscreen-chop-shop (1731, 1920 x 1080, UnityPy format code 10 = DXT5/BC3, not DXT1 as the plan says; the extractor checks size only), Arena_MainMenu_Paralax_ChopShop-1/2/3/4 (438, 1445, 987, 135, 2048 x 1024, format 10). A further object Popup_background_chop-shop (2724, 1920 x 500) is a popup strip and is not in the rotation. All six images were extracted into `work/tmp/autoservice-phase-b/identity/` with the two extractors (`--output` scratch) and viewed:
- banner: a dim industrial hall mouth with a large graffiti-covered double door, a white van with sandbags in its open rear at the left, the front of an old saloon at the right foreground, torn hanging cloth and a horn speaker above;
- loading screen: the same view with Arena's dark vignette and scanline treatment (so banner and loading screen are one picture family);
- ChopShop-1: a pink sports car on a lift bay in a tiled garage stall with an overhead ring light and murals;
- ChopShop-2: a hall interior with a yellow van behind a block wall, a white saloon at the right, container cabins, sandbag-topped stacks and a drinks machine;
- ChopShop-3: a dark corner of the same hall with a red hand-painted warning on a corrugated cabin wall, a car at the left edge, steel shelving and a graffiti panel;
- ChopShop-4: a view toward a bright outdoor gap in the brick wall with plank barricades, a chain-link cage, a pickup behind it and a steel staircase at the right.
All five rotation images and the banner show the same garage hall/yard, so the names decide correctly; no image shows another map. No BSG text is copied here.

**Rows.** `extract_banners.BANNERS['arena_autoservice']`, `extract_loadscreens.LOADSCREENS['arena_autoservice']` (5 entries: loading screen, then the four ChopShop backgrounds), `ArenaMapBanners.ByMapKey` (route id `arena_autoservice_banner`, stem `arena_autoservice`), tests in `ArenaMapBannersTests`, `test_banners.py`, `test_loadscreens.py`. The row precedes the promotion exactly like Iceberg's (7e4b640): the banner map is only consulted for the maps of `All`, and `test_validate_distribution`'s fixture already filters the art tables to its RELEASED set (no change needed there beyond the pack fixture below).

**Description.** Lookup in TestBackendLocaleEn/ge/Ru: `6441004f8ab20b218807f14b Name` is 'Chop Shop' (en, ge; ru has its own 8-character name) and equals the value of the key `Arena_AutoService`; `6441004f... Description` has 148 / 143 / 119 characters. The port's `id_field` `e6aa4591d3e1a59835d625b3` has no Name, Description or ShortName row and appears in no key, so the id is Arena's location id and the own-words fallback is not needed as the main text. `DESCRIPTION_KEYS['arena_autoservice']` and `OfficialTextMapKeys` got the row together (same order, pinned by `Official_text_maps_are_released_maps_and_the_key_set_of_the_extractors_pack`, whose first assertion now reads 'Catalog map' instead of 'released').

**Decision with a reason (loader).** The old `ArenaMapDescriptions.Load` required the pack to hold exactly the released official-text maps. With Autoservice in the table but not in `All`, a five-entry pack would be discarded as a whole (the four released texts lost). Load now allows, but does not require, entries of official-text maps that are not released yet (pending = `OfficialTextMapKeys` minus released keys). A pack of the four released maps stays valid, a Prison or Iceberg entry is still refused, and after promotion the entry is mandatory with no further code change. Three tests added (`A_pack_with_the_pending_autoservice_entry...` for en/ge/ru, `Once_autoservice_is_released_its_entry_is_required`, `Autoservice_is_an_official_text_map_that_is_pending...`). `All.Count - 1` now counts the released subset of `OfficialTextMapKeys`.

**Pack regeneration (measured).** `python tools/extract/extract_map_descriptions.py --output <scratch>` produced a 3-locale, 5-map pack; removing the autoservice rows and re-serialising gives a file byte-identical to the previous `work/extract/map-descriptions/map-descriptions.json`; the old rows are equal in all three locales. The new pack was copied to `D:/ArenaMapPort/work/extract/map-descriptions/map-descriptions.json` and `--check` passes. Danger recorded: the released 0.5.0 server DLL would discard this five-entry pack, so it must only travel with a server DLL built from this line.

**Locale.** `tools/maps/autoservice.json` and the AutoService descriptor entry: `locale_name`/`LocaleName` 'Arena: Chop Shop' and an own-words fallback description (garage hall and walled street yard, a team pocket at each end, vans, cars, containers and shelving as cover, tribune edges bounding the play space); only those two fields touched in each place. `db/arena_autoservice/base.json` still shows the old spike name; it is generated from the sidecar and is regenerated at item 12.

**Commands and results.** test_banners 24 OK, test_loadscreens 27 OK, test_map_descriptions 22 OK, test_validate_distribution 61 OK (the real-source extraction tests ran), `validate_map_descriptors.py` OK (14 descriptors, 14 catalog entries, All membership). .NET: 3231 passed, 1 failed (`CryptoThroughputTests.Cng_ReachesAtLeast300MiBPerSecond`, 293 against 300 MiB/s, a timing test on a loaded host); the rerun of it with the descriptions and banners tests gave 60 passed. A plain `dotnet test` in a worktree stops in the server csproj's DeployToDist for lack of `work/`; I used `-p:BuildProjectReferences=false` after the server DLL compiled, so no dist/ appeared.

**Released maps unchanged (inferred and measured).** The banner and loading-screen changes add one dictionary row each; `extract_one` selects its entry by map key, so the other maps' outputs cannot change (inferred from the code path; the scratch extraction of all six maps succeeded and passed the size checks). The description pack is proven byte-identical for the four released maps (measured above). Sidecars of the other maps untouched.

**Open.** Fallback text is the agent's wording. Item 12 must carry the facts in facts_for_item12 (art in validate_art's released set, base.json regeneration, removal of the 'pending' test, deploy order of pack and server DLL).

## 2026-11-11 - Phase B item 10: Spatial acoustics (export, pins, second opinion)

Branch `phase-b/acoustics` (commit 5eb8632 on `bd64fc4`); two new tracked files, no exporter or client change. The worktree started on main's tip and was fast-forwarded to bd64fc4.

**Export (measured).** `python tools/extract/extract_spatial_audio.py --map arena_autoservice --output D:/ArenaMapPort/work/extract/spatial-audio` (installed Arena 0.6.2.0.48473) ran clean, no repair rule, no mirrored volume, no table row (the plan's `sound` role gives `level112`; `verify_scene_mapping` passes):
`arena_autoservice {"bytes": 4465, "room_pairs_count": 21, "routes_count": 139, "sha256": "8f32eb9f...ae17d80f"}` and `{"source_scene": "level112", "rooms": 7, "portals": 16, "areas": 9, "clips": 1, "mixer_effects": true}`. As the survey said. The shipped `source.audiobakedata` is byte-identical to Arena's `arena_autoservice_sound.audiobakedata` (cmp). Also: 25 colliders, 34 nodes, 1 group, 1 system, caps (max routes per pair, max portals per pair) 19/54 = the scene's `location_info`, bake settings depth 8/8/8, distance 500/300/500. All 16 portals are Door type, state 0; 16 portal nodes are flat planes (z scale 0, allowed).

**Rooms.** 1 IndoorResp2 (type 2048) 11.0x5.8x39.1 m; 2 AudioRoom_4 (512, IndoorMetalThin) 6.6x3.3x6.2; 3 AudioRoom_2 (16384) 8.8x2.8x7.2; 4 AudioRoom_1 (8192) 9.1x3.1x6.2; 5 MainRoom (2048, 3 areas) 57.5x22.7x46.7; 6 OutdoorResp1 (1, outdoor flag) 99.4x14.7x51.8; 7 AudioRoom_3 (512) 22.7x2.9x2.9. Shot-acoustics classes (RoomShotAcousticsModel, generic, no row): Shop 1/3/4, Metal 2/7, Hall 5, Outdoor 6; room 1 is 0.92 m under the Hall edge. RoomReverb categories `{67:67, 2048:4, 512:4, 16384:4, 8192:4, 1:67}` (no conflicting type).

**Mixer.** Effects asset `CommonMixerParamsData` (sharedassets39.assets:105), handlers reverb 13 + delay 5 + effects sends 8 = 26 parameters, effects volumes 0, three categories each; `room_reverb.validate` passes.

**Room tone.** One clip, `amb_arena_autoservice_winter_night_indoor` (sharedassets112.assets:29, 16 s, stereo, 44.1 kHz, 705,600 frames, 2,822,444 B WAV). It is the ONLY indoor tone of the scene (the AudioClip census of sharedassets112 lists no other tone, only fans, owls, BTR, gates, water drops, wind), all seven rooms reference it, no room has a season preset, and the client binds tones by room ambient data without any season logic (NativeSpatialAudio.cs:268-269). Inferred: it plays in the summer set as in Arena. Not heard.

**Other maps unchanged.** The exporter and its tables are untouched, so nothing can move by construction. Control: re-export of Airpit, Bowl, Iceberg, Equator and Bay5 into scratch (`work/tmp/autoservice-phase-b/acoustics/scratch`) and `diff -rq` against the main checkout's packages: every bake and WAV is byte-identical; only `spatial-audio.json` differs, in 2 (Iceberg), 7 (AirPit, Bowl) and 9 (Equator, Bay5) provenance leaves (source-asset/scene/mixer hashes and `location_info.id`) because those packages were exported from an older Arena build than the installed 0.6.2; no structural leaf differs. Not a result of this item.

**Tests.** `tools/tests/test_spatial_audio_autoservice.py` (16 tests: exporter rows absent, plan role, packages shape/validator/room table/portal reciprocity/bake/route walks/no duplicate routes/location info/tone/mixer layout/category map; live: shipped bake == Arena's file, export == package, deterministic, plan mapping) and `ArenaMapPort.Server.Tests/AutoServiceAcousticsTests.cs` (6 tests: room classes, near misses, outdoor rule, mixer category rule, package table, no negative scale). Results: autoservice 16 OK, iceberg 46 OK, test_autoservice_audio 22 OK, test_spatial_audio 20 OK (1 skip, hardcoded work path), test_room_reverb 16 OK (1 skip, same), `dotnet test -c Release` 3233/3233 (first full run: 1 failure in SpectatorReactionBedModelTests.TheEnvelopeAddsNothingToTheGcHeap, an allocation measurement, passed alone and on rerun).

**Client rows: deferred to item 12.** NativeSpatialAudio.cs:69, RoomReverb.cs:45 and PrecipitationAudio.cs:42 loop over the released maps and bind a config section per map; adding Autoservice to them now would change the released client's configuration, so they are listed for the promotion (with ReverbAudioProbe texts and the SpatialAudioRepairs comment).

**Second opinion: SPT-SEB `check_live_acoustics.py` (MIT, tag v1.0.3, tool use only).** Fetched `tools/check_live_acoustics.py` and its single import `tools/extract_live_acoustics.py` plus LICENSE into `work/tmp/autoservice-phase-b/acoustics/seb/` (untracked); both read completely first. The CLI cannot judge an Arena bake (it walks SPT's own `*_Sound` scenes through `TypeTreeGenerator.load_local_game`, compares against SPT's bake of the same name and demands SEB `.scene` sidecars), so I imported the module and called `check_map()` from my own driver with room/portal records built from our graph, `python -I` from another directory, files read only, nothing written. Results:
- Autoservice (vs itself): ok. Format: whole file consumed, pair key = id, rooms ordered, one entry per room pair, every pair has routes, TotalPortalsCount and ShortestRouteLength right, no route twice, pairs listed once under their first room, route index consistent, within ushort; caps (19, 54) = maxima <= location caps; scene: 0 absent rooms, 0 absent portals, 0 routes through doors that do not join their rooms; 0 lost pairs.
- Iceberg repaired (vs itself): scene checks ok (0/0/0, caps 180/1015 ok) but FAIL "same route twice" in 153 pairs. Measured: 510 routes duplicate another route's portal list in the same pair with different cost and distance; Arena's original has none. Cause (inferred from the repair rule): the 66->30 rewrite folded two original doorways onto one portal ID. Whether the game double-counts them was not verified.
- Negative control, Iceberg's original Arena bake vs the shipped graph: "533 new routes go through doors that don't join their rooms", exactly the repair rule's 533.
Not applicable here: the `.scene` sidecar check and the SPT-vs-live change statistics. Limits: location caps are the scene's own, no EFT-side buffer sizes are available offline.

**Open.** (1) Iceberg's duplicate routes (released in 0.5.0): leave or de-duplicate, a user/lead decision. (2) Winter-named tone in a summer set and the two Metal-classed rooms: ear checks in Phase C. (3) Item 12 carries the csproj staging rows and the client arrays listed above.

## 2026-11-11 - Phase B item 11: Light, flares and shaders offline

Scope: offline only (UnityPy read-only over the installed Arena 0.6.2 and `D:/SPT41/EscapeFromTarkov_Data`; package `work/extract/autoservice-native-v2`). Autoservice stays Catalog-only. Commit `73ba581` on `phase-b/light`. No client row was written (see decisions); every value the promotion needs is listed in the hand-off facts.

### 1. MultiFlare

- `tools/extract/extract_multiflare.py`: `SCENES["arena_autoservice"] = {62 Arena_AutoService, 65 DesignStuff, 66 Light, 69 Scripts, 136 TeamFight_DesignStuff}` (the same five names as the sidecar's `all_scene_names`, which the exporter and `test_multiflare_extraction` compare) and `EXPECTED_CENSUS = (21, 8, 77, 32)`.
- `python tools/extract/extract_multiflare.py --map arena_autoservice --output D:/ArenaMapPort/work/extract/multiflare/arena_autoservice` -> `MULTIFLARE VERIFIED: arena_autoservice: 8/21 active lights, 32/77 active flares, 2 original textures` (7 s). The census matched on the first run; no schema, distance-order or scale rule had to change (0 negative scales, no inverted ranges).
- Census of the flare components over levels 62/63/65/66/69/112/135/136 and the winter set 243-246 (measured): FlareLight 21 in level66 only, FlareSceneSettings 1 in level69 (summer Scripts). Level 243 has a second settings object and level 244 35 more lights: they are not part of the map, so the table owns exactly one settings object (the exporter's rule).
- Lights: 8 active (`OO/ARMSTRONGS/armstrong_light emissive (4..11)/!gaslamp_NewMultiFlareLight_gas (2)`, 4 elements each, world positions x -400..-347 (-400.0, -398.1, -396.2, -394.2, -351.0, -350.4, -348.4, -347.1), z 481..510, y about -1.1), 10 `Wall_light_factory_lamp-source` flares (3 elements) under an inactive ancestor, 3 `glowstick_yellow` lights with the component disabled (5 elements). The inactive ones stay in the package as inactive entries and are never consumed.
- Released maps unchanged: the HEAD copy of the script and the changed script were run against the same installed Arena for Equator, Bay5 and Bowl into scratch folders (`work/tmp/autoservice-phase-b/light/{orig,mf}/`): `diff -r` empty for all three (byte-identical `flares.json` and textures). (The staged `work/extract/multiflare/arena_*` folders differ from a fresh export only because they predate the 0.6.2 Arena build, e.g. `resources.assets_2812` vs `_2969`; not touched.)
- Tests: `test_multiflare_extraction.AutoservicePinTests` (scene table, census, winter/AI/sound scenes not owned, scene ownership, released pins, and a real-data check of the exported `flares.json` under `ARENAMAPPORT_WORK_DIR`). Not done (item 12): `FLARE_MAP_KEYS`, csproj staging rows, `NativeMultiFlare` list, `test_validate_distribution` fixture.

### 2. Shaders (28 names of package v2)

- Scan: UnityPy over every `level*`, `sharedassets*.assets`, `resources.assets`, `globalgamemanagers`/`.assets` of `EscapeFromTarkov_Data` (1,120 files) plus the global `StreamingAssets/Windows/shaders` bundle, reading each Shader's `m_ParsedForm.m_Name` (`work/tmp/autoservice-phase-b/light/shscan.json`). Result (measured): **all 28 names exist**. 24 are in `globalgamemanagers.assets` and in the global shaders bundle. Four are in scene shared assets only: `Custom/Billboard_FogSheet_Simple` -> `sharedassets13.assets` (custom_Light), `Cloth/ClothShader` and `Cloth/ClothShader_backface` -> `sharedassets5.assets` (custom_multiScene), `p0/Reflective/Bumped Specular SMap Baked` -> `sharedassets216.assets` (`City_NE_02_buildings_back`, Streets of Tarkov). (`Standard` also in `resources.assets`; `Legacy Shaders/Transparent/Cutout/Bumped Specular` also in `sharedassets221`.)
- Is the scene-only shader loaded at raid time? Dependency scan of the externals of all `level*`/`sharedassets*` files (`ext.json`): `Reserve_Base_Scripts` (level139, the donor rig scene every ported raid loads) lists `sharedassets5` and `sharedassets15` as externals; `sharedassets13` is listed by other Reserve_Base scenes (Light, HQ, ...), not by Scripts itself; **no Reserve_Base scene, no MenuUIScene (level48) and no MainScene lists `sharedassets216`**, which is referenced only by scenes of City, Factory_Rework, Labyrinth and Sandbox. The 2026-10-12 AirPit host finding (fog sheet, cloth and rock resolved in process, the loading scene undetermined) therefore has a plausible offline cause for cloth/rock (**inferred**: a file's external list is not proof of load). For `SMap Baked` the shader file is **inferred not loaded** on a ported raid (needs a host raid).
- Decisions: fog sheet (80 slots) and both cloth variants (92 slots) keep AirPit's safety-net approach (host-proven resolved on AirPit; rows stay); `SMap Baked` (1 material, 1 slot, `Factory_Building_04_Outdoor_LODLAST`, last LOD of a background building) needs the existing Bay5 row `p0/Reflective/Bumped Specular SMap Baked` -> `p0/Reflective/Bumped Specular SMap` (already in `ShaderStandinPolicy.Table`, shipped for Bay5). So Autoservice's allow-list is those four names. **Not written now**: the allow-list lives in the descriptor mirrored by the sidecar and `test_shader_fallback_names` fixes which sidecars may carry it; and `ShaderStandinPolicy.FallbacksActive` has a switch only for Bay5/AirPit/Bowl, so activation needs a new config key and ShaderRebind wiring that cannot be compiled in this worktree. All of it is in the item 12 hand-off with exact names and the one open design point (target of `ClothShader_backface`: Table row `p0/Cutout/Bumped Diffuse` vs AirPit's two-sided cutout).
- Tests: `test_autoservice_light` (28 names, 1 Baked material, 24 in `globalgamemanagers.assets`, the four in their files, `level139` externals contain 5 and 15 and not 216, `level48` does not contain 216, the Baked row present in `ShaderStandinPolicy.cs`).

### 3. Lighting defaults

- Zero level (measurement): the 20 `SpawnPointMarker`s of package v2 (both pockets) stand at y -3.84 .. -3.72 (floor -3.8; the capture point is at -3.80). `MapLightingDefaults` rule = lowest marker floor minus 4.4 (Equator 0.03 -> -4.4, Iceberg 0.73 -> -3.7, Bay5 floor -3.8 -> -8.2): -3.84 - 4.4 = -8.24, **ZeroLevel -8.2** for `arena_autoservice` (identical to Bay5's row, same floor height). A start value, to be tuned on the host through `ZeroLevelOverride`. Pinned in `test_autoservice_light`.
- Bloom: nothing to write. `BloomTuning` binds -1/-1 (the camera's own value, the donor rig's 2.10 / 0.35) for every map in `All` that is not Equator, Bay5, AirPit or Iceberg - Bowl's case. The strongest emissive (3.883) is below Bay5's 4.128, so the donor pair is the right start.
- Emission cap: **none**, confirmed. Recomputed with the `EmissionCapModel` rules (`test_iceberg_emission_cap.level/census`, 15 emissive materials used by renderers): Material #37 3.883 (p0 Emissive SMap, power x visibility, 11 uses), Wall_light_factory_E 3.60 (60), stones_pack_rebake_D 2.738 (Standard), shopping_mall_lamp_lum_ceiling_emissive 2.59, Old_Bunker_Light_Emissive 2.49, armstrong_light_emissive 2.0, AreaLightSource 1.92, ... Released strongest: Equator 3.932, Bay5 4.128, AirPit 3.62, Bowl 3.932 (Iceberg 67.794). **Deviation from the plan text:** eight Standard materials DO carry `_EMISSION` (all used; 2.738 the strongest), the answer is unchanged.
- Client rows not written: `WeatherRigDefaultsTests` pins "rig-active == All" and nine parked Neutral rows; a row for a Catalog-only key would break it. Exact values are in the hand-off.

### 4. Light budget census (risk 12)

Counted in Arena's own level files (62/65/66/136 and the Scripts scene 69), measured: 189 + 12 Lights; CullingLightObject 184, VolumetricLight 78, FlareLight 21, LightFlicker 0 (covered). Not covered by any exporter: **CullingAdvancedLightObject 17** (all active, on AreaLight objects), **AreaLight 29** (17 in the Light scene: `GENEBURN_GARAGE/AreaLight*` 5, `LUM_LAMPS/lab_lamp_lam_ceiling_destroyable (N)/AreaLight (1)` 10, `GARAGE_TRANSIT/AreaLight*` 2, at x -377..-359, z 467..497, y -3.75..-0.9; 12 in level136 `DOORS/LIGHT_1..4/{RED,GREEN}/AreaLight*` at the pocket gates x -403 and -344, 23 of 29 active), **StencilShadow 6** (Paz_STENSIL, Village_WareHouse_01/stencil, two budka_tamozhya_reserve STENCIL, STENCIL_TEMP, STENCIL SHADOW; 5 inside the play box), **AnalyticSource 2** (`ambient_portal (80)`, `PORTALS/ambient_portal_medium`), PerfectCullingLightGroupPreProcess 1, one `MultiFlare_Autoservice` manager (every map has one). The excluded Scripts scene holds the renderers (AreaLightRenderer, TubeLightRenderer, SceneLights, ShadowMaskExtractor, TodLightSetter, AmbientLight). The released maps carry the same classes uncovered (Equator AreaLight 13, StencilShadow 7, AnalyticSource 19; Bay5 StencilShadow 45, AnalyticSource 37; Bowl AreaLight 31, CullingAdvanced 31, StencilShadow 25, AnalyticSource 37; AirPit AreaLight 47, CullingAdvanced 45), so the numbers are within precedent. **Inferred** (host to confirm): the garage and lab ceiling lamps and the colour glow of the gate lamps will lack their area-light contribution; the lamp meshes (emissive), the 184 culling lights and the 8 gas-lamp flares still light the pockets; stencil overlays and ambient portals are unconverted dressing. Pinned in `test_autoservice_light.LightComponentCensusTests`.

### Tests and environment

- `test_multiflare_extraction` 29 OK; `test_autoservice_light` 11 OK (no skips); `test_validate_distribution` 61 OK (226 s); `test_crowdless_maps` 12, `test_shader_fallback_names` 6, `test_iceberg_emission_cap` 21, `test_autoservice_audio` 22: OK; `test_native_contracts` 98 OK with 0 skips after `package_dir` got the `ARENAMAPPORT_EXTRACT_ROOT` override (it hardcoded `REPO/work`, so seven real-data tests had skipped in any worktree, the Autoservice v1/v2 ones included).
- `dotnet test ArenaMapPort.Server.Tests -c Release`: 3227 passed, 0 failed (one earlier run failed `CrowdReactionModelTests.ReactAndDrainAllocateNothing`, 984 B against the 512 B bound under parallel load; green in isolation twice and in the full rerun; no C# changed). A worktree cannot run the Server csproj's `DeployToDist` target (needs `work/extract/map-descriptions`, writes `dist/`): build once, then `-p:BuildProjectReferences=false` / `--no-build`.

### Open

Host: does `Factory_Building_04_Outdoor_LODLAST` render magenta (the shader file inferred unloaded)? Are the garage/lab lamps and gate lamps visibly dark? ZeroLevel -8.2 is a start value. Design point for item 12: the Autoservice stand-in switch and the `ClothShader_backface` target.

### Review fixes

One low finding (light-data-F1) was confirmed: the implementer's hand-off gave the gas-lamp flare x range as about -394..-351, but the exported `flares.json` puts the 8 active gas-lamp lights at x -400.0, -398.1, -396.2, -394.2, -351.0, -350.4, -348.4 and -347.1 (z 480.8..510.1, y -1.1 for all eight). The range is x -400..-347. This record already said so; the hand-off facts now agree. No code or tracked doc carried the narrow range (`git grep` on `phase-b/light` is empty), so no commit was needed and the branch stays at `73ba581`. The reviewer re-checked the 21/8/77/32 census, the 8/10/3 split of active, inactive-ancestor and disabled lights with 4/3/5 elements, the zero level of -8.2, and the pins in `test_autoservice_light` and `test_multiflare_extraction` (40 tests OK, none skipped). The reviewer did not re-derive the shader census, the sharedassets216 dependency or the light-budget numbers, and did not check the released-map byte identity or the .NET suite.

## 2026-11-11 - Phase B merged; gates on the merged state

The workflow `wf_97f6e985-1c1` ran the six workstreams above in their own worktrees (22 agents; each workstream implemented, then
two review lenses, a verifier per finding and a fixer). Two low findings were confirmed and fixed: the mine-point note in the
`extract_ai_anchors` docstring (`d97bea8`) and the gas-lamp x range of the light hand-off (hand-off text only, the record above was
already right). The worktrees had started on `main`; each agent moved its branch onto `bd64fc4` before working, so every branch
sat on `feature/autoservice` (item 8 on top of item 7, which it needed for the door rows).

The lead merged them with `--no-ff` in the order modes, doors, navmesh, identity, acoustics, light (6d06986, 83ed5eb, 5f0264f,
1374b46, f58e1d4, 1763d69); no conflicts (the descriptor and `tools/maps/autoservice.json` hunks of items 8 and 9 do not
overlap). Gates in the main checkout on `1763d69`:

- `python tools/validate/validate_map_descriptors.py`: OK, 14 sidecars, 14 catalog entries, All membership, `EquatorDefault()`.
- `dotnet build ArenaMapPort.Server -c Release`: 0 warnings, 0 errors (stages the five-entry description pack into `dist/`; the
  server DLL built here accepts it, the released 0.5.0 DLL would discard it, so the pack never goes out without this DLL).
- `dotnet test ArenaMapPort.Server.Tests -c Release`: 3,243 of 3,243 (3,227 at Phase A).
- `python -m unittest discover -s tools/tests -p "test_*.py"` with `PYTHONUTF8=1` from PowerShell: 3,119 tests OK, 68 skipped
  (2,913 at Phase A). The two worktree-only failures the workstreams reported (no client DLL under `dist/`, CRLF sidecars in a
  worktree checkout) do not occur in the main checkout.

Open after Phase B, carried into item 12 and the host: the exit names against the locale key; no bot row in the yard's
south-east; the faction split per pocket is a guess; the equator shutter's collider leads its LODs during a drive; the lamp group
`DOORS` with RED rows that turn off on release (a first for the client); the Equator roller doorway's 1.87 m clearance; the
tribune decks (islands in the model); the area lights no exporter covers; `Factory_Building_04_Outdoor_LODLAST` (magenta
without the new stand-in); the winter-named indoor room tone in the summer set; Iceberg's duplicate routes (released, see the
acoustics section).

## 2026-11-11 - Item 12: lock-step promotion into ArenaMapRegistry.All

Commit `b2fd6e3` on `feature/autoservice` (68 files, +1,623 / -319), modelled on Iceberg's promotion `1bd5868`: one row per file of that commit and per row of plan item 12 (the checklist is in the hand-off of this item). Autoservice (`arena_autoservice`, "Chop Shop") is now the sixth map of `ArenaMapRegistry.All`, after Iceberg. It is registered and staged, NOT released and NOT deployable: the Unity bundles do not exist (U1 is item 13), no key exists (item 14), nothing is built by Unity, tagged or pushed.

What changed
- Descriptor and sidecar in one commit: `AutoService` gains `Exits` (Chop Shop East Gate / West Gate, equal to `autoservice.exits.json`), `DoorPoses` (the 23 literals from `extract_door_poses.py --csharp`: 19 clip rows of the four gates, 4 switch rows of the DOORS lamps), `ScoreMonitors` (4, scene `Arena_AutoService_DesignStuff`, type words 0/0/2/2, all active) and `ShaderFallbackNames` (`Custom/Billboard_FogSheet_Simple`, `Cloth/ClothShader`, `Cloth/ClothShader_backface`, `p0/Reflective/Bumped Specular SMap Baked`); `tools/maps/autoservice.json` gets `shader_fallback_names`. `db/arena_autoservice/base.json` was regenerated with `make_base_json.py --map tools/maps/autoservice.json` (Name 'Arena: Chop Shop', 4 player + 7 bot spawn points = 11, 5 waves, 4 PMC squads, 2 exits, BotMax 8).
- Both csproj files: description, bundles, shader sidecar (property, Error, Copy, Message), AI sidecar, acoustics (Error condition, item, hint), gate sounds, flares (Error and item: Autoservice HAS a flare package, 8 active gas-lamp flares), key-create guard and its property, the stage Exec, and the server's base.json rows. No DynamicMaps and no water row (no overview, no sea).
- Client rows: `AmbientAudioProfiles` (the staged row moved into `Table`, `Staged` is empty), `MapLightingDefaults` (Reserve rig, ZeroLevel -8.2, rest neutral like Bowl/Iceberg), `NativeDecalRepairs`, `NativeSpatialAudio`, `PrecipitationAudio`, `RoomReverb`, `NativeMultiFlare`, and the explicit crowdless case in `ArenaSpectators.DesignScene`; the remaining per-map files (`ReverbAudioProbe`, `SpatialAudioRepairs`, `TimeOfDayRepairs`, `WeatherTestOverride`, `HdrImageTuning`, `EquatorNightNeon`, `FixedRaidTimeSelection`, `LoadingScreenArt`, `AiAnchors`, `Bay5AmbientAudio`, `PuddleFootstepRepairs`) are descriptor- or `All`-driven and only got their map lists in comments and description texts.
- Shader stand-ins: new developer-only key `MaterialFixes/AutoServiceShaderStandins` (default true, `ShaderStandinPolicy.AutoServiceStandinsDefault`), wired into `FallbacksActive` (optional fifth parameter) and `ShaderRebind`. Targets: fog sheet -> `Unlit/Transparent Colored` (generated soft texture, the capture is not map-gated), `Cloth/ClothShader` -> `p0/Cutout/Bumped Diffuse`, `Cloth/ClothShader_backface` -> `Legacy Shaders/Transparent/Cutout/Bumped Diffuse 2 side` (AirPit's precedent: the table row is Cull Back in all three passes, so a backface cloth would vanish from behind), `p0/Reflective/Bumped Specular SMap Baked` -> `p0/Reflective/Bumped Specular SMap`. `UsesExtendedStandins` now includes Autoservice so the cloth gets AirPit's fix (normal map slot `_NormalMap1` -> `_BumpMap`, `_Cutoff` 0: the albedo alpha carries gloss, the cutoff would discard the mesh). `IsKnownGap` and `HidesFogSheets` stay AirPit-only. Inferred, not seen: the fog sheet and cloth are expected to resolve to EFT's own loaded shader as on AirPit; the Baked shader (sharedassets216) is inferred not loaded, so its row is the live one.
- Pipeline: `RELEASE_MAP_NAMES/KEYS`, `CROWDLESS_MAP_KEYS` (Iceberg, Autoservice), `FLARE_MAP_KEYS` (+ arena_autoservice); `PENDING_MIRROR_KEYS` is the empty tuple (mechanism kept), the `PENDING_MAPS` entry of `test_bundle_crypt_tool.py` is gone, `park-unreleased-dist.ps1` no longer parks `autoservice`. `OVERVIEW_MAP_NAMES`, `WATER_MAP_KEYS`, the deploy script's `-DynamicMapsMap` ValidateSet and the packer rules did not change (the packer is data-driven: part `autoservice`, no spectator rule). `docs/dist-manifest.md`: dated paragraph (383 files once staged, part autoservice 24, 378 in dist today).
- Inputs: `extract_banners.py` and `extract_loadscreens.py` were run (banner + 5 loading images); the acoustics, gate sounds, flares and AI sidecar existed from Phase B and the DllOnly client build staged them.
- Tests: the pending pins were inverted or folded into the released-set tests (descriptions: the pack entry is now mandatory by itself, the generic pending rule of `ArenaMapDescriptions.Load` is covered against a registry without Autoservice; ambient profile `Staged` empty; score-monitor/door-pose mirror; bake-input omitted-keys pin), every five-map pin became six, and `AutoServiceMapTests.cs` (8 tests) was added.

How released maps were shown unchanged: every new row is keyed on `arena_autoservice` / `ArenaMapRegistry.AutoService`; `make_base_json.py` reproduces the committed base.json of Equator, Bay5, AirPit, Bowl and Iceberg byte for byte (`cmp`); `git diff` touches no other map's sidecar; `ShaderStandinPolicyTests` pin that no other map's `FallbacksActive`, `TryGetFallback`, `UsesExtendedStandins` or `FixFor` result changed (including Bay5's and AirPit's backface rows); the client's new config entries are developer-only (no key added to `CompactConfiguration.IsPublic`/`MapConfigVisibility.IsPlayerOption`; `ReleaseConfigurationTests` pins the new `Lighting (arena_autoservice)` section as hidden for players).

Gates (main checkout, from PowerShell)
- `validate_map_descriptors.py`: OK, 14 sidecars, 14 catalog entries, All membership, `EquatorDefault()`.
- `dotnet build` Server and Client (`-c Release`, Client with `-p:ArenaMapPortDllOnly=true`): 0 warnings, 0 errors each. The DllOnly build needed no Autoservice input that only Unity produces (the shader sidecar is read by the non-DllOnly target only); no placeholder file was made.
- `dotnet test ArenaMapPort.Server.Tests -c Release`: 3,268 of 3,268 (baseline 3,243).
- `PYTHONUTF8=1 python -m unittest discover -s tools/tests -p "test_*.py"`: Ran 3,121 tests, OK (skipped=71) (Phase B state: 3,119 OK, 68 skipped). One CRLF working-copy artifact had to be fixed locally for it (`autoservice.monitors.json` as LF, see the hand-off).
- `tools/deploy-to-spt.ps1 -SptRoot 'D:\SPT41'` (dry run, no `-Confirm`) REFUSES the whole-dist deploy, as intended: "[deploy-to-spt] FATAL: dist/ is incomplete for a registered map -- the server mod stages db/<map> but the client plugin lacks its files: arena_autoservice (missing bundles/arena_autoservice_scenes.bundle); arena_autoservice (missing bundles/arena_autoservice_navmesh.bundle); arena_autoservice (missing bundles/arena_autoservice_preset.bundle); arena_autoservice (missing keys/arena_autoservice.json). Finish the map's build and run a client Release build without DllOnly (or park its files) before a whole-dist deploy; nothing was copied." `make_release_zips.py --plan` likewise stops with 'incomplete dist: part autoservice lacks' the three bundles, the key file and the shader sidecar.

After this item no deploy of any kind goes to a host until U1 (item 13) and the first stage (item 14); a `-DllOnly` deploy would log `Required map file is missing` and raise the incomplete-install notice (Iceberg rule 6).

## 2026-11-11 - Item 12: review passes and fixes

A first review pass of `b2fd6e3` (three lenses: completeness, released-map safety, data) returned no finding, but two of its three
reviewers made only 4 and 13 tool calls, so the lead ran a second, deep pass (workflow `wf_9986c84e-e31`, 42 agents): five lenses
(client runtime 40 files read, server and data 12, test quality 25, pipeline and deploy 24, completeness sweep 41), three skeptics per
finding, a fixer. 12 findings, all low; 2 confirmed by at least two of three skeptics and fixed in `18e21df`:

- `client-2`: AirPitFogSheetOpacity also scales AutoService's generated fog textures.
- `sweep-2`: Usable-rows theory lists five released maps, Autoservice missing.

Refuted (10): `client-1`, `tests-1`, `tests-2`, `pipeline-1`, `pipeline-2`, `sweep-1`, `sweep-3`, `sweep-4`, `sweep-5`, `sweep-6`. Among them are notes for the release step, not defects of the
promotion: the packer's generated README prose names the maps by their registry display name ('AutoService', not 'Chop Shop'), says
that only Iceberg has no crowd and that only Iceberg's ambient mix is provisional, and `ENCRYPTION_TEXT` names the Arena builds of the
five older maps (`tools/release/make_release_zips.py`, lines about 678-782); `tools/build_remaining.ps1`'s synopsis still lists five
released maps (behaviour right); `ArenaMapBanners`' class comment still says five maps. Those texts move with the release texts
(Phase D), which are the user's.

Fix commit `18e21df`: the developer key `MaterialFixes/AirPitFogSheetOpacity` also scales Autoservice's generated fog textures
(`UsesExtendedStandins` covers both maps), so its description now names both maps (name and default unchanged; the generated fog
texture is used only when EFT's own fog shader is not loaded, and on the first host raid it was loaded, see item 14);
`ScoreMonitorModelTests.OnlyTheActiveRowsWithAFaceAreUsable` pins Autoservice at 4 of 4; a repository `.gitattributes` with
`tools/maps/*.monitors.json text eol=lf` keeps those sidecars LF under `core.autocrlf=true`, which `test_score_monitors` reads as
bytes (a fresh clone or worktree had them CRLF). Gates after the fix: `validate_map_descriptors` OK, server and client (DllOnly)
Release builds 0 warnings, `dotnet test -c Release` 3,269 of 3,269, Python `unittest discover` 3,121 OK (71 skipped), deploy dry run
refused as required. No bake input changed. The deep pass's host predictions that the first raid could check are answered in item 14.

## 2026-11-11 - Item 13: U1 build

`pwsh -Command "& 'D:/ArenaMapPort/tools/build_all.ps1' -MapKey arena_autoservice -MapDescriptor D:/ArenaMapPort/tools/maps/autoservice.json
-PackageDir D:/ArenaMapPort/work/extract/autoservice-native-v2 -FullMap -OutputDir D:/ArenaMapPort/work/build/autoservice -BakeNavMesh $true
-Verify $true -SkipGameRefsSync"` on `b2fd6e3`, 11:17-11:36, first attempt: `[build_all] OK -- build + mandatory identity patch + all
validators passed`. Scenes bundle 961,635,684 B (960,078,276 before the identity patch; the plan's estimate was 0.93-0.95 GB), 10,871
LZ4HC blocks, NavMesh bundle 61,151 B; `validate_bundle_blocks` clean for both, `validate_bundle_roundtrip` OK with one non-blocking
warning (460 materials in the bundle against 459 package materials, Unity's default), `validate_preset` OK (5 of 5 scene keys, CAB name
unique), `validate_map_descriptors` OK. `validate_extract.py` reports the package's known 116 warnings.

Bake log: `bake inputs: bounds from the descriptor (bake_bounds), layer mask 0x60042A00 (navmesh_bake_layers), 0 navmesh_bake_exclusions,
4 opened_barriers (4 with door poses: 19 moved subtree(s))`; the four DOORS/LIGHT switch rows `move nothing and drop nothing from the
bake`; 30 ignoreFromBuild markups and 7 volume sources; `sources=2344` (the oracle predicted 2,346 before the modifiers it does not
model). `navmesh_diff.py --map autoservice --descriptor tools/maps/autoservice.json --doors --points <bots> --points <exits>
--live-core-points --compare-bundle work/build/autoservice/arena_autoservice_navmesh.bundle`: 1,150 polygons, main component 1,924.8 m2
(Arena 1,915.6, port model 1,914.6), covering 47,564 of Arena's 47,951 samples; all 20 spawn markers, the capture point, both exits, the
7 core points and 58 of 59 patrol points lie in the main component. The one outside is `hub2.5` at (-385.37, -3.79, 483.4), which sits
on the edge of Arena's own mesh (border clearance 0.0) and is eroded away by the agent radius. Smaller islands: decks above the hall
(371 m2 at y 1.7-2.35, 131 m2 at y 1.9-2.2) and a 98 m2 strip east of the east pocket (x -338..-331, z 465..501) behind the pocket wall.

The generator rewrote the stand-in shader stubs from this package (21 changed, three new: `p0/Reflective/Bumped Specular SMap Baked`,
`p0/Reflective/Bumped Color Specular SMap`, `Transparent/DepthZwriteDithered`), committed as `1f5dbe8` like Bowl's and Iceberg's (every
build regenerates them from its own package, so a released map's rebuild is not affected).

## 2026-11-11 - Item 14: first stage, keys, deploy, first raids (GPU-2)

Key store before: five `.key` files, none for Autoservice. `dotnet build ArenaMapPort.Client -c Release
-p:ArenaMapPortCreateReleaseKeys=true -p:ArenaMapPortCreateKeyMap=arena_autoservice` on `18e21df`: `CREATED ...\work\release-keys\
arena_autoservice.key (the content key of arena_autoservice): BACK UP the key store folder now`, then `OK stage arena_autoservice: 3 bundles
encrypted (3 written), key file keys/arena_autoservice.json: new wrap for Arena 0.6.2.0.48473, 1 wrap(s) in the file (written), 3 files,
3,145,728 of 464,942,688 B sampled, self-check unlocks with wrap 0`; the other five maps `3 bundles encrypted (0 written)`, key files
unchanged. The user was asked to back the key store up off the VM at once.

`verify-keys` of the Autoservice key file: OK against the VM's Arena 0.6.2.0.48473, GPU-2's 0.6.1.0.48206
(`\\192.168.178.109\C$\Battlestate Games\Escape from Tarkov Arena`) and MASCHINE11, which now runs **0.6.2.0.48598**, a newer Arena build
than the VM's. The five released key files (0.5.0) also unlock against 0.6.2.0.48598 (wrap 0, built for 0.6.1.0.48206): the hotfix left
their sampled resource streams unchanged.

`validate_distribution.py`: `DISTRIBUTION_OK 6 maps 18 bundles 6 sidecars encrypted`. `validate_port.py --map autoservice --distribution
--package work/extract/autoservice-native-v2`: `PORT_VALIDATED` (479 materials, 4,270 meshes, 950 textures, 15 NavMesh tiles, 1,946
triangles, scenes bundle 961,635,684 B, 3 verified starts, 116 warnings). Deploy dry run: 383 files, 19 new, 15 to overwrite (the
Autoservice files, both DLLs, the six roar beds and `spectator-audio.json` of `52d0a4d`, which had not been deployed before, Autoservice's
`base.json`, the description pack); `-Confirm` copied them. `gpu-run deploy-wait --server` (111 s to identical files, SPT server
restarted), `gpu-run start` (62 s, 16/16 identical).

Startup (12:33): server `Arena: Chop Shop is available in location selection.`, banner wired, APBS `6 Arena map key(s)`; client
`Version 0.5.0 loaded. Maps: ... Arena: Iceberg, Arena: Chop Shop`, `[Notice] availability: arena=verified, offered=6/6`.

Raid 1, PMC, no bots (12:34): `log since start: warnings 0; errors 0`, `load arena_autoservice: location 5.1 s (diff 1.3), game started
30.6 s`. Lines: `[Bundles] stream 'arena_autoservice_scenes.bundle' (raid start): 956.1 MiB decrypted in 957 chunks ... crypto 664 ms`;
`[NavMesh] added navmesh for 'arena_autoservice': 4246 vertices, 1946 triangles`; `[SpawnGates] gates open (navmesh installed): 23 part(s)
registered, 2 park carver(s)`; the four lamp parts parked with `1 switch(es) in the open state, no collider to carve`; all four stand-in
candidates `resolved to EFT's own loaded shader` (fog sheet, both cloth shaders and `SMap Baked`: nothing renders magenta, the stand-in
switch is a safety net only); `[MultiFlare] arena_autoservice enabled: sourceLights=21 activeLights=8 flares=32`; `[NativeDecals] ...
source=304 active=296 registered=296`; `[Monitors] arena_autoservice: 4 of 4 monitor row(s) can show a face`; `[Exfil] registered 2
exit(s)` (`Chop Shop East Gate` and `West Gate`, 8 s, smoke lit); `[LevelSettingsOverride] map=arena_autoservice ZeroLevel=-8.20`;
`[RoomReverb] arena_autoservice: native room controller active; 26 original parameters`; `[NativeAudio] ready players=38 started=33
seasonalSilent=5 season=AutumnLate` (the server season is AutumnLate; five season players have no bank for it); `[IndoorSurface] ...
elapsedMs=921`, the known one-time classification stall (the raid's `max 994 ms` frame). Spawn in the west pocket (-409.5, -3.7, 495.6).
`uibridge_teleport dryRun` from there: capture point 42.2 m, east pocket 77-81 m, both exits and the yard points complete; from the east
pocket marker (-337.8, 485.6): capture 44.0 m, three west markers 75-84 m, both exits. One own test point (-360, 470) lies behind border
cube 230 (outside the play space). Performance over 10 s: 120 fps (the cap), p95 8.3 ms, max 10.0 ms. Screenshots: the west pocket with
its roller shutter parked open, the hall at the capture point, the open equator shutter seen from the east shop.

Raid 2, PMC, bots medium (12:36): `warnings 0; errors 0`, `location 3.6 s`. `zone 'BotZone_Main' authored 1 real patrol way(s) from
ai/autoservice.ai.json (5 hub(s), 54 sub point(s))`, `authored 7 real AI core point(s)`, `BotZone.Init 'BotZone_Main': markers=7
onNavMesh=7`, `wave fired ... side=Savage type=assault`, `ActivateBotCallback: bot='Bot1' ... registered`; the bot walked at least 27.5 m
in 25 s on `PatrolAssault`. Spawn in the east pocket (-337.3, 502.7), so both pockets have been used. The hideout opened afterwards
(5.9 s; four `light_CellPlafonds_holder` LODGroup warnings and one disabled audio source from EFT's own hideout scene, as on every host).

Not checked yet (item 15 and the user): a hold and release (the lamp group whose RED rows go dark, the equator shutter's collider
leading its LODs, the gate sounds), the tribune decks, rain F8, a cross-map raid, the faction split, and the listening tests.

## 2026-11-11 - Item 15, first host iteration (GPU-2): hold and release, cross-map raid

Same build and deploy as item 14. `Spawns/SpawnGateHold` = true and `SpawnGateHoldSeconds` = 45 were set in the running game only
(`uibridge_config_set persist=false`, the host's .cfg kept false/30; both set back to false/30 afterwards).

Raid 3, PMC, no bots, held gates (12:41): `warnings 0; errors 0`. `spawn barrier 'big_gate_metal' ...: 1 part(s) held closed (23 part(s)
registered; developer test hold 45 s)`, the six-node shutters likewise; `[GateSounds] prepared 4/4 clip(s) of arena_autoservice for drive
scale 0.50`; `[SpawnGates] navmesh installed: 5 carver(s) over 5 movement collider(s) of 23 gate part(s) ...; 3 doorway(s) already
disconnected on the map's own navmesh (not judged)`. Spawn in the west pocket; during the hold `uibridge_teleport dryRun` to the capture
point and to an east marker ended `path_partial` at (-402.8, -3.7, 499.5) and (-402.8, -3.7, 479.5), the two techlight shutters: the pocket
is sealed for bots. Screenshot: the west roller shutter closed. Release at 12:42:43: `[GateSounds] released 19 part(s): 4 sound row(s), 2
playing, 2 out of reach or without a free source, 0 failed` (the two techlight clips at -15 dB, 30 m, `OneShotPropagation`; the two east
gates are about 66 m and 67 m from the listener, beyond their 30 m / 35 m reach, as designed), `one frame later: 2/2 playing`; the four lamp parts
`1 switch(es) in the open state ... (released)` in the same frame; the equator shutter's six nodes parked 2.23 s later (4.5 s x 0.5).
Screenshot 1 s after the release: the west shutter half-way up. After the drive all three dry runs were complete again (42.2, 80.9, 77.0 m).

Raid 4, the same hold, the player teleported into the yard in front of the east gates (12:43): the extraction panel lists `EXFIL01 Chop Shop
East Gate` and `EXFIL02 Chop Shop West Gate`; the east exit's smoke burns beside the big gate. The four `DOORS/LIGHT_n` lamps (y 4.13 at
x -345.15) sit behind the tarp `Sniper_defence_tarps_04` on the upper floor and could not be seen from the yard; their switch rows are
logged, not looked at. Release 12:44:43: the big gate's `gate` node parked open 4.0 s later (8.0 s x 0.5), its clip played (`volume 0.178
(-15.0 dB), reaches 35 m`).

Raid 5, Equator right after the Chop Shop raids (12:45, cross-map): `warnings 0 from mods, 4 Unity Log` (Equator's known
`refrigerator_drink` LODGroup lines); `load arena_equator: location 4.3 s`; crowd `load total=2253 ms`; `[NavMesh] added navmesh for
'arena_equator': 10395 vertices, 4567 triangles`; 103 fps over 10 s, the slowest frame being the known `[IndoorSurface] ... elapsedMs=1061`
classification.

The three doorways that the hold reports as "already disconnected on the map's own navmesh (not judged)" are the big gate and the two
techlight shutters: the doorway probe (`SpawnGates.ProbePath`) samples its two points at the height of the CENTRE of the closed bounds,
which for these 4.4-4.7 m tall gates lies about 2.3 m above the floor, beyond `SpawnGateModel.ProbeSampleRadius` (1.5 m); the null result
of `ProbePath` then counts as "disconnected" in `EnsureBaseline`. Only the 2.8 m equator shutter (centre 0.7 m above its floor) is judged,
`PathPartial` while held and `PathComplete` after the release. The sealing itself is right (the dry runs above); the probe is a Debug
verdict. Fix (all maps, so with its own host check): probe near the bottom of the bounds and count a null baseline as inconclusive.

Still open: whether the equator shutter's
collider leading its LODs is visible from the east shop; the tribune decks (islands in the bundle, climbable by a player?); rain F8; a
raid with ArenaMode; and the user's look and listen (gate sounds, the ambient start mix, the roar beds at -26 dBFS, lamps).

## 2026-11-11 - Item 15: the doorway probe fix (all maps)

The probe cause named in the first host iteration is fixed in `a3b6ca3` (branch `fix/spawngate-floor-probe`): the probe points sit 0.5 m above
the bottom of a gate's closed bounds (`SpawnGateModel.ProbePoints`), and a probe that finds no navmesh is inconclusive, never "disconnected"
(`SpawnGateModel.BaselineOf`). On GPU-2 the same day Chop Shop's hold judges all four doorways (`every probed gate sealed (4 probed)`, the big
gate, the equator shutter and both techlight shutters `PathPartial`; `every probed gate open (4 probed)` after the release, all `PathComplete`).
The six-map host record is in [door-poses.md](door-poses.md) "2026-11-11 - The doorway probe samples near the floor".

## 2026-11-11 - Phase C: Autoservice gets Arena's own spectator crowd (branch `phase-c/crowd`, commit c0c99e1; offline, no deploy, no host)

Task (lead, option (a) of the crowd survey and its check): give `arena_autoservice` the 30 fans of Arena's `Arena_AutoService_TeamFight_DesignStuff` (level 136, active root `SCAV_SPECTATORS`) with the existing spectator pipeline. The premise of PLAN-AUTOSERVICE risk 6 ("full player-body rigs, an incompatible layout") does not hold: the 30 `ArenaFanEntity` are the same rigs, the same controller (sharedassets40:338), the same 15 appearances, 39 meshes and 33 materials that Equator, Bay5, AirPit and Bowl already export. This reverses the user's 2026-11-08 "crowdless passt" for this map and needs the user's go.

Changes: `extract_spectator.MAP_SCENES['autoservice'] = 'Arena_AutoService_TeamFight_DesignStuff'` (the one list the CLI choices, `validate`, `stage_spectators`, `test_crowdless_maps` and the `validate_distribution` partition follow); `ArenaSpectators.DesignScene` returns that scene for `arena_autoservice` (it is in the descriptor's AllSceneNames, so `IsOwnedScene` passes; the doors, monitors and start gates already live in the same scene); `validate_map_descriptors.CROWDLESS_MAP_KEYS = ("arena_iceberg",)`; the packer and validators derive the spectator rule and the required file from that list and needed no logic change. The client already disables an ACTIVE original `SCAV_SPECTATORS` after building the replacement (the Bay5 path), which also removes the 12 bodiless headgear MeshRenderers and the layer-8 colliders the port imported today. Tests and docs pins follow (test_crowdless_maps, test_map_pipeline, test_spectator, test_stage_spectators, test_validate_distribution, SpectatorCrowdBedModelTests, AutoServiceMapTests comment, csproj/deploy comments, dist-manifest.md, spectators-allmaps.md section).

Cubemap decision: the two cubemaps (`patron_cubemap_metall`/`_matte`) have ids of their own on this map, faces byte-equal. Stage the 12 duplicates (249,714 B, shared set 123 -> 135) instead of remapping by content: no code, the existing same-name/different-bytes refusal covers it, and a remap would add a second id space to extractor, graphs and the staging check and touch the four released crowds to save 0.25 MB.

Evidence: real extraction into `D:/ArenaMapPort/work/extract/spectator-autoservice`: `extracted 30 original spectators, 39 shared meshes, 33 materials, 103 textures, 2 shared animations`, `SPECTATOR_PACKAGE_OK`. Staging of all five packages into a scratch folder (not `dist/`): 135 unique textures, `validate_staged` passed. Against the existing `dist/` spectators: the shared manifest gains 12 rows, no existing PNG differs, `arena_autoservice/spectator.json.gz` is new (6,283,124 B), and the four released graphs differ in exactly one field, `texture_manifest_sha256` (graphs and manifest ship together). C# real-crowd test run on the scratch staging: Autoservice forms 4 stands within the 25 m rule. Python: the five touched suites 367 OK; the full suite showed only two environment failures of the worktree (no `dist/` DLL for test_dll_versions; a CRLF in a regenerated sidecar text for test_score_monitors), none from this change. dotnet test 3268/3269, the one failure being the CNG throughput threshold under parallel load (passes alone).

Not measured: load time and texture memory on this map (computed: about 2.1 s main-thread PNG decode and about 536 MB of uncompressed textures per raid, as on the four crowd maps), placement/facing/clipping on the decks, visibility from the yard, loading and `GetSceneByName` of the TeamFight scene at crowd time, how the unoccluded murmur and roar beds sound inside the garage hall. Expected counts once staged: dist 396 files (383 + 13), core 242, autoservice 25 (computed).

## 2026-11-11 - User feedback after the first raids: crowd, marker, AirPit board; merged, staged, deployed, host-checked (GPU-2)

User (2026-11-11 evening, after playing on GPU-2): "sieht ganz gut aus, die zuschauer fehlen aber noch oder? die position des markers auf
der map muesste auch nochmal verschoben werden", plus a missing object on AirPit (the lead's AirPit record, airpit-release.md, 2026-11-11).

**Crowd.** A read-only survey with a skeptic (workflow `wf_4ef44eb9-f09`) found that the premise of the 2026-11-08 crowdless decision was wrong:
Autoservice's 30 `ArenaFanEntity` are the same rigs the four crowd maps already carry (the section above). The user's question was taken as the
go; the lead told the user that the decision rests on a corrected premise and is reversible. `phase-c/crowd` (`c0c99e1`) was reviewed
(three low findings, all refuted by three skeptics each) and merged as `b7876d3`. Released maps: their graphs change only in
`texture_manifest_sha256`; the AirPit raid below loaded its crowd from the re-hashed graph (`99 original fans ... load total=2159 ms`).

**Marker.** On EFT's region map (PMC flow, GPU-2 1920x1080) the toggle `ARENA: CHOP SHOP` at the measured 407/471 covered the `CUSTOMS` label
(Chop Shop dot x 594, y 424; Customs dot x 706, y 432). ArenaMode's own location screen does not list Chop Shop yet (its catalog has no
`arena_autoservice` entry; the ArenaMode hand-off, plan item 19). `79a1bb5` displays the marker at 870/428 (`markers.json` keeps the
measurement and gets a `display` block like Iceberg's; descriptor, sidecar and base.json carry 870/428; the 120-unit rule between released
pins holds, 124 to Equator). Host after the deploy (22:17): `toggle "ARENA: CHOP SHOP" click 1087,457 [949,441 275x32]`, beside `CUSTOMS`
`[696,415 163x33]` (90 px), `INTERCHANGE` `[783,494 215x33]` (21 px) and `ARENA: EQUATOR` `[884,367 248x33]` (41 px); the test
`ChopShopIsDisplayedClearOfTheCustomsInterchangeAndEquatorLabels` pins those boxes.

**Build and deploy.** Merged state `79a1bb5` plus the measured boxes: server Release build, `dotnet test -c Release` 3,285 passed (2 skipped),
client Release build WITHOUT DllOnly and without key creation: `OK stage arena_airpit: 3 bundles encrypted (2 written)`, every other map `0 written`,
all key files unchanged; `SHARED_SPECTATOR_PACKAGE_OK 135 unique textures, 5 maps`. `validate_distribution.py`: `DISTRIBUTION_OK 6 maps 18
bundles 6 sidecars encrypted`; `validate_port.py --map airpit --distribution --package work/extract/airpit-native-v10`: `PORT_VALIDATED` (555 materials,
3,474 meshes, 40 NavMesh tiles, 5,433 triangles); `make_release_zips.py --plan`: 396 files, 6,083.4 MiB. Deploy: 13 new, 10 overwritten (both
DLLs, AirPit's scenes and NavMesh bundle, the five crowd graphs, the 12 cubemap PNGs, the shared manifest, Autoservice's base.json);
`deploy-wait --server` 11.6 s, `gpu-run start` 66 s, 16/16 identical.

**Chop Shop raid** (22:19, PMC, no bots): `warnings 0; errors 0`, `location 4.6 s`, 107 fps over 10 s (the slowest frame the known
`[IndoorSurface]` classification). `[Spectator] arena_autoservice: 30 original fans, 150 renderers; shared 39 meshes, 33 materials; idle/cheer
staggered; load total=2081 ms (wait=0, hash=61, parse=1663, textures=2033, ...)`; `[SpectatorAudio] ... 13 participating clips, 30 positions`,
`70 reaction clips, kinds mask 62`; `[CrowdBed] arena_autoservice: 4 stands (radii 15.6/14.8/11.4/13.9 m, centres (-393,5,475) (-390,5,508)
(-361,6,468) (-360,6,510)) ...`; `[ReactionBeds] ... 6 roar beds (3 cheer, 3 angry), 4 stands` (the survey's stand plan, exactly).
`uibridge_inspect find`: the 30 replacements under `ArenaMapPort_Spectators_arena_autoservice`, the originals under `SCAV_SPECTATORS` inactive
(so the 12 bodiless hats and mustaches and the layer-8 colliders are gone). Fans read at y 4.38-4.50 on the north gallery (z about 510.9) and
y 2.04 on the west ledge. Screenshot from the yard (-384, -3.8, 494): fans visible on the container roof behind the sandbags and on the east
gallery; from straight below the gallery parapets hide them, which is Arena's placement. Not listened to.

Python gate on the merged state (`79a1bb5`, PowerShell, `PYTHONUTF8=1`): `python -m unittest discover -s tools/tests -p "test_*.py"` ran 3,121 tests, OK (68 skipped).
