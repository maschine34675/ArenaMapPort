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
