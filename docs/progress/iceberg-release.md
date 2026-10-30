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

## 2026-10-30 - ambient audio and package v2 (PLAN-ICEBERG item 5)

- **Census of level259** (Arena_Sanatorium_Sound, plan role `sound`; scratch `census.py` on the exporter's own parsers, Arena
  0.6.1.0.48206): 104 players (52 `AmbientSoundPlayer`, 22 `SeasonAmbientSoundPlayer`, 30 `LoopAmbientSoundPlayer`), 4
  `AmbientSoundPlayerGroup` in source order (`CommonAmbientInGroup` 46 and `LampsAmbientInGroup` 20 on `CommonAmbInEffects`,
  `CommonAmbientOutGroup` 37 on `CommonAmbOutEffects`, `AmbientSplineEmitterWaterGroup` 1 on `CommonAmbOutBypass`), 1
  `BezierSpline`, 1 `AmbientSplineEmitterController`, 1 `SoundAmbientZoneCalculator`, 1 `AmbientPlayerSplineMappedEmitter`, 28
  `SoundBank` (27 names), 237 clips (172,510,104 B of 16-bit WAV). Every player is grouped exactly once (`all_players`, grouped
  104). Inactive in the source: `river` (level259:1751), `river (2)` (1617), `sparks (2)` (1886). None disabled, none plays on
  awake or forces a mixer, and no AudioSource has `Loop` set (the 30 loop players switch it on in `Awake`), so `loops` is `{}`.
  Season banks: Summer 22, Spring 22, Autumn 20, SpringEarly 17, AutumnLate 13, Winter 4 (19 of the 22 players lack at least one
  key). Sibling-name renames: `cracles~2`, `far_misc~2`, `soft_metal_derbis (3)~2`. The survey's numbers all hold.
- **minDistance 0, reviewed rule** (`extract_bay5_audio.parse_player`): 21 loop players (20 `neon_lamp*` in `LampsAmbientInGroup`,
  `projector` in `CommonAmbientInGroup`) author `minDistance` 0 with `maxDistance` 3, 5 or 7 m; their AudioSource components
  carry `MinDistance` 1, the same max and `rolloffMode` 2 (Custom). EFT reads the player's `_minDistance` once, in
  `BaseAmbientSoundPlayer.Awake` (decompile line 252, `Source.minDistance = _minDistance`), and the same `Awake` then forces
  `AudioRolloffMode.Custom` with the player's `_rolloffCurve`; no other EFT code in `Audio.AmbientSubsystem` reads the field (the port's own range multiplier clamps `maxDistance` to at least `_minDistance + 0.1`, which holds for 0). The
  rule is `0 <= minDistance <= maxDistance` and `maxDistance > 0`; volume and spatialBlend within 0..1 stay, and a negative or NaN
  minDistance, `maxDistance` 0 and `maxDistance < minDistance` still raise `player bounds`. `Bay5AudioImporter` and
  `validate_native_features` check no bounds (the importer copies the field into a plain serialized float, the validator only
  re-encodes the payload byte for byte), so neither needed the rule; `test_bay5_audio` pins accept/reject cases and that the
  validator's encoder reproduces a zero-min payload.
- **Latent fault found and fixed: the sibling-name rule renamed components of one GameObject.** The first dry-run census gave
  `AmbientSplineEmitterWaterGroup~2` and `LoopAmbientSoundPlayer_spline_emitter~2`: the controller and the group sit on one
  GameObject, the calculator and the mapped emitter on another, and `disambiguate_sibling_paths` (Bowl, `9c6bc17`) treated the
  second component of each pair as a same-named sibling. Bay5's accepted v5 graph has exactly this shape (GameObjects 183 and 205
  of level188). The export would have failed closed at `audio group membership changed` for Iceberg (schema 2 pins group
  membership) and silently produced a broken water group for a Bay5 re-export (schema 1 has no such pin). The rule now keys on the
  GameObject (`gameObject` header pointer in the exporter, the source component's own GameObject in the validator's recheck);
  records without a key behave as before. Evidence: Iceberg's three renames are exactly the players above, no support record is
  renamed; a Bay5 audio re-export from the live Arena (scratch) has the same 24 players, 6 support records, ids and paths as
  `bay5-native-v5` (the only differences are Arena build pointers: mixer and script path ids of 0.5.4 against 0.6.1, and the
  scene hash); the Bowl, AirPit and Equator re-exports likewise have the ids, paths and `sourceLeafName` values of
  `bowl-native-v2` (`indoor_wind~2` kept), `airpit-native-v9` and `equator-native-v5`; `validate_audio_source` passes on the
  Iceberg, Bay5, Bowl and Equator re-exports. `test_audio_sibling_names` covers the
  rule (commit `963492d`).
- **Registration and contract** (commit `6c0c5b9`): `AUDIO_EXPORTERS["arena_iceberg"] = ("ambient_audio", export)`, contract
  `audio` block (schema 2, `level259`, root `ArenaMapPort_IcebergNativeAudio`, `group_coverage` `all_players`, states as above;
  `group_mixers` in source order) plus two notes. `Bay5AudioImporter`: `IcebergRootName`, `RootNameFor`/`AssetFolderFor`
  ("Iceberg") and `GroupMixers["LampsAmbientInGroup"] = "CommonAmbInEffects"`.
- **Client side, checked not promoted.** (1) `Bay5AmbientAudio.ValidateGraph` demanded `AmbientSoundPlayer` for every player under
  original activation; Iceberg is the first graph that combines that contract with season and loop players, so a profile row alone
  would have ended in `[NativeAudio] invalid imported graph ... original activation/loop contract differs` on the first raid. The
  check accepts the three exporter kinds now (Equator, AirPit and Bowl author random players only). (2) `AmbientAudioProfiles`,
  `AmbientAudioDefaults` (`Values`, `PreviousValues`, `HasLegacyMix` throw for an unlisted key) and the tests that pin them to
  `ArenaMapRegistry.All` (`TheTableNamesExactlyTheReleasedMaps`, `OnlyMapsWithAcceptedMixesHaveDefaults`) keep Iceberg out until
  the promotion; `IcebergAmbientAudioTests` builds the planned profile row from the contract values through the profile's own
  constructor instead. Two interior groups share `CommonAmbInEffects` and one `AmbientInVolume` lease; the effects/reverb-reroute
  route is `CommonAmbientInGroup`, the Lamps group stays on the original interior mixer. Per-group authored levels for the
  listening pass: lamps volume 0.25, max 3 m (two at 5 and 7 m), 20 loops of `amb_arena_neon_lamp`; Common-in volume 0.03-0.50
  (median 0.10), max 5-40 m; Out volume 0.10-0.565, max 10-100 m; water emitter 0.388, 60 m. The provisional 3 dB / 0.4 / 3 mix
  would raise the lamp loops to about 0.75 and 9 m, which may be audible as a hum.
- **Package v2** (`work/extract/iceberg-native-v2`, `extract_map.py --plan tools/extract/plans/iceberg.json --native-features
  --target D:/SPT41/EscapeFromTarkov_Data`, 15:29:30-15:36:18, run from the feature worktree): `required = ["ballistics",
  "ambient_audio"]`, 49,861 objects, 5,370 meshes, 823 materials, 1,407 textures, 59 warnings (as v1), `native_audio`
  players 104, banks 28, clips 237, schema 2, `native-audio.json` sha `91974adf...27322ba` (equal to the audio-only dry run), 238
  files under `audio/` (173,729,973 B). `refresh_native_volumes.py`: no Tex3D, manifest untouched. `validate_extract.py --package
  work/extract/iceberg-native-v2 --extractor-timeout 1800`: `OK -- all checks passed`, 32 non-blocking warnings (the same
  manifest and particle-LOD warnings as v1), source/clip/reference contract validates, both reproducibility runs exit 0 and the
  package matches a fresh extraction with 0 diffs; 15:36:48-15:54:55.
- Tests: `python -m unittest discover` from PowerShell 2,453 tests, 1 failure
  (`test_score_monitors.SidecarTests.test_the_sidecar_text_is_what_the_cli_writes`: this worktree's checkout carries CRLF in
  `tools/maps/equator.monitors.json`, unrelated to this work), 131 skipped (packages absent in the worktree); `dotnet test -c
  Release` 2,683 tests, 1 failure, `AmbientAudioProfilesTests.TheImporterMirrorsTheRootNamesFolderNamesAndGroupMixerPairing`:
  the importer now lists `LampsAmbientInGroup` and the client table has no Iceberg route yet (resolved by the promotion). The
  client plugin compiles (`dotnet build -c Release -p:ArenaMapPortDllOnly=true`: no CS error; the run stops at the spectator
  staging step because the worktree has no `work/extract/spectator-*`).
- Not verified: no Unity build, no host raid. First U1 must show `[ArenaMapPort][NativeAudio] authored inactive stock graph
  map=arena_iceberg players=104 banks=28 clips=237 support=8`; the built-bundle recheck compares the player fields and the
  AudioSource `MinDistance` (1) read back from the bundle; the first raid must show `[NativeAudio] ready players=104
  started=101 ... sourceInactive=3`.

## 2026-10-30 - spatial acoustics (PLAN-ICEBERG item 11): mirrored volumes, 533 routes, graph + bake + room tone

Workstream branch `worktree-wf_451c0467-c1b-2` (fast-forwarded to `feature/iceberg` `d366c5c` first). Source: the VM's Arena
0.6.1.0.48206, `Arena_Sanatorium_Sound` = level259 (the `sound` role of `plans/iceberg.json`, `817eed0`).

### The two failures, reproduced

`python tools/extract/extract_spatial_audio.py --map arena_iceberg --output <scratch>` stopped in `validate_graph` with
`ValueError: invalid transform`. With that check lifted, `parse_bake` stopped on `route does not reach pair endpoint`.
Counts of the scene: 46 rooms (45 IndoorGeneric, one OutdoorCommon), 89 portals (23 to outdoor), 120 trigger areas, 209
colliders, 258 nodes, one room tone `amb_sanatorium_indoor_tone_big` (stereo, 705,600 frames = 16 s), 1,003 room pairs, 17,751
routes, bake 622,301 B.

### Mirrored trigger volumes (10 of 120)

- All ten are the own node of an `AudioTriggerArea` in two rooms: `corridor_2_1` (room 5, 3 of 4 areas, X negative, e.g. node 28
  scale (-1.467, 5.239, 5.634)) and `coffe_hall` (room 35, 7 of 7 areas, Z negative, e.g. node 296 (19.507, 3.213, -7.17)). One
  mirrored axis each, rotation unit, no child node, nothing else on the node; every collider is a unit box with centre (0, 0, 0)
  (the shape comes from the scale). No portal, room, group or parent node is mirrored.
- Arena's own records agree with the hierarchy: over all 120 areas its cached OBB centre equals ours to 8.3e-6 m and its cached
  extent, SIGNED `lossyScale x size / 2` (the mirrored axis is negative there too; the old comparison failed on exactly these ten
  with the sign as the only difference), to 7.7e-7 m; and the cached `_bounds` of all 46 rooms equal the union of the area AABBs to
  7.8e-6 m, rooms 5 and 35 included (computed offline from the exported graph; Arena stored the numbers Unity computed in the
  editor).
- What EFT does with a volume (decompile 0.16.9.5): `SpatialAudioRoom.GetRoomBounds` = union of `Collider.bounds`;
  `AudioRoomStorage.CheckPositionInsideRoom` = `Bounds.Contains` of `Collider.bounds`; `ServerRoomColliderArea` raises the enter
  and exit events from the physics trigger and then asks `AIUtility.PointInOABB`, which maps the point with `InverseTransformPoint`
  and compares it with the unsigned half size around the local centre, so the sign cancels. A portal is different:
  `BaseSpatialAudioPortal` (line 323) multiplies the collider size by the signed `localScale`; portals stay refused.
- The CLIENT would have refused the graph: `NativeSpatialAudio.BuildGraph` required `localScale >= 0` ("negative node scale"), which
  makes `Prepare` fall back to unoccluded playback for the whole map. Fixed (below).
- Rule (exporter): `extract_spatial_audio.mirrored_volume_nodes` = the own node of an area when no room, portal, group or system sits
  on it and nobody has it as a parent. In `validate_graph` such a node may keep a negative scale only when (a) its collider centre
  is 0 on every mirrored axis, (b) Arena's cached OBB of an active room carries the same signed extent (0.005 m), (c) for a room with
  a mirrored area the cached room bounds equal the union of the area AABBs (0.005 m), (d) the area belongs to an active room. Every
  other negative scale, a flat volume, a non-unit rotation and non-finite numbers stay refused; released maps compare with sign +1
  everywhere, so their checks are unchanged.
- Rule (client): `AcousticVolumeRules` (new, Unity-free, linked into the tests) builds the same node set from the JSON's `areas`,
  `rooms`, `portals`, `groups`, `systems` and parent ids and `NativeSpatialAudio.BuildGraph` passes every node through
  `ScaleProblem`; the one Debug line `[Acoustics] arena_iceberg: 10 mirrored trigger volume(s) keep their negative scale` reports it.
- Room classification: with the AABB sizes the 46 rooms are 44 Shop, 1 Hall (`coffe_2`, 42.8 m) and 1 Outdoor for the shot
  acoustics, and types 4 / 64 map to the mixer categories 4 / 67 for the room reverb (no conflict): both are generic, no per-map
  row. `IcebergAcousticsTests` pins the 46-room table.
- Room reverb: `mixer_effects` extracts (the same `sharedassets39.assets` MixerEffects asset as the released maps: reverb 13, delay
  5, sends 8, volumes 0 = the 26 parameters, room types 4 / 8 / 67). An offline run of `RoomReverb.TableHandler`'s checks over the
  five graphs (allowed names from the source text, no duplicate, finite values, one category per room type) passes for Iceberg
  (`{4: 4, 64: 67}`) as for the four released maps.

### The 533 routes

Evidence (scratch scripts in the session scratchpad, counts reproduced by `test_spatial_audio_iceberg.IcebergLiveTests`):

- 533 of 17,751 routes fail (476 `disconnected`, 57 `wrong endpoint`) in 157 of the 1,003 pairs, 3 of them with every route failing.
  **All 533 pass through portal 66; no other portal is involved.** Decoder and counts are consistent with the file (`location_info`
  agrees on rooms, portals, pairs, routes; the portal and room ID sets equal the bake's; all 89 portal IDs are unique and contiguous
  1..89), and the other 17,218 routes follow the graph, so the decoder and the RoutesDataCount semantics are not at fault.
- Portal 66 in the shipped scene is `AudioPortal_FROM_elevators_TO_boiler (2)` joining rooms 27 (elevators) and 24 (boiler (2)),
  centre (29.60, 4.32, -83.09). 343 routes use 66 exactly so and are valid. The 533 failing routes need 66 to join rooms 27 and 11
  (boiler): from the routes' other portals the room before and after the 66 is (27, 11) in 496 single-use routes, and 37 routes use
  66 twice in a row (24 -> 27 -> 11), which one portal cannot do. The only portal that joins rooms 27 and 11 in the scene is
  **portal 30**, `AudioPortal_FROM_elevators_TO_boiler`, centre (33.28, 4.32, -83.09), 3.68 m from 66 on the same line.
- Reading: when Arena baked the Sanatorium sound graph two different portals carried the ID 66; the shipped scene has one. In 510 of
  the 533 failing routes the same pair also stores the twin route with 30 in place of 66 (heuristic cost differs by a median 0.07,
  at most 0.36, and traverse distance by a median 1.1 m, at most 4.9 m: a neighbouring doorway, not the same one), 23 have no
  stored twin. (Inference: the second 66 was a doorway that is no longer in
  the scene or was renumbered; Arena's own runtime has the same ID table and plays these routes through the other doorway too.)
- What the native loader does with them (decompile): `AudioBakeDataReader` reads the pairs and routes; `RoomPair.Route.Read` only
  maps each ID through `TryGetPortalById`; `FinalizePortalIndicesData` maps IDs to portal buffer indices; `PropagationJob.Execute`
  measures the polyline emitter -> closest point on each listed portal -> listener from the portal data. Nothing walks the room
  chain, so the unrepaired file LOADS and plays, but a source in boiler (room 11) heard from the elevators side is propagated
  through the doorway of boiler (2), 3.7 m away, in 3.0% of the routes.

Decision (agent): repair under a reviewed, pinned rule, because the strict check stays the only judge for every other file and the
loader cannot tell the difference. `spatial_audio_bake.repair_portal_ids(data, graph, portal_ids)`: only the IDs the rule names may
stand for another portal; a failing route is read as the walk with the fewest substitutions in which a substituted ID is replaced by
THE portal that joins the two rooms the walk is between (exactly one such portal, parallel doorways are a guess); the reading must be
unique, else `RouteGraphError`; file size, structure, route lengths, costs and the route index are untouched (only portal-ID shorts
change), and the result must pass the unchanged `parse_bake`. `extract_spatial_audio.REVIEWED_BAKE_REPAIRS['arena_iceberg']` pins
ID 66, Arena's bake SHA-256 `58995949130c5a2766c7a98f16850e3bbd934702902d42a25b2fd0db946ffd3f`, 533 routes, `{'66->30': 533}` and the
result `e7650b6e673a76216d89eab8ea7612a50a49a5f44ea139d364da53dfce775169`; a different bake (an Arena update) or outcome stops the
export until a human reviews it. `parse_or_repair_bake` runs the strict `parse_bake` first and repairs only after a
`RouteGraphError` for a map with a rule. The export records the repair under the top-level `bake_repair` key of the graph JSON (rule,
original and repaired size and SHA-256, 533 rewrites); `bake` keeps exactly `parse_bake`'s result, the file keeps the name
`source.audiobakedata`, so csproj staging, `validate_distribution.validate_acoustics` (re-run on the package: OK, and a tampered bake
is refused) and the release packer need no change. 533 bytes differ from Arena's file (one low byte per rewritten ID).

### Outputs and checks

- `python tools/extract/extract_spatial_audio.py --map arena_iceberg --output D:/ArenaMapPort/work/extract/spatial-audio` wrote
  `work/extract/spatial-audio/arena_iceberg/` only: `spatial-audio.json` 536,167 B, `source.audiobakedata` 622,301 B (SHA-256
  `e7650b6e...5169`, 1,003 pairs, 17,751 routes, strict check passed), `sharedassets259.assets_6.wav` 2,822,444 B (SHA-256
  `67a98379...62ce3`, 2 ch, 44.1 kHz). Two runs are byte-identical. The other four folders were not touched.
- Released maps unchanged: the HEAD tool (git archive) and the new tool, both run against the live 0.6.1 install for Equator, Bay5,
  AirPit and Bowl into scratch folders, produce 12 of 12 byte-identical files (graph JSON, bake, WAV). Against the accepted inputs
  in `work/extract/spatial-audio/` the bakes and WAVs are byte-identical and the graph JSON differs only in the documented
  provenance leaves (source asset hashes, `systems[0].location_info.id`, `mixer_effects` hashes) that an Arena update changes.
- Tests: `tools/tests/test_spatial_audio_iceberg.py` (45 tests: synthetic controls for the mirrored rule and the repair, the pinned
  rule, and live/real-data tests that skip without the Arena install or the packages); `AcousticVolumeRulesTests` and
  `IcebergAcousticsTests` (xunit). `ARENAMAPPORT_SPATIAL_AUDIO_DIR` points the real-package tests at another checkout's
  `work/extract/spatial-audio` (a worktree has none).
- Gates run from PowerShell in the workstream worktree: `dotnet test ArenaMapPort.Server.Tests -c Release` 2,701 passed, 0 failed;
  `python -m unittest discover -s tools/tests -p "test_*.py"` 2,488 tests in 518 s, 131 skipped (no `work/` in a worktree), 1
  failure that is not from this change: `test_score_monitors.SidecarTests.test_the_sidecar_text_is_what_the_cli_writes` finds a
  carriage return in `tools/maps/equator.monitors.json`, which this worktree checked out with CRLF (`git ls-files --eol`: index
  lf, working copy crlf) and which no commit here touches. A client Release build with `-p:ArenaMapPortDllOnly=true` compiles
  `NativeSpatialAudio.cs` and `AcousticVolumeRules.cs` without a warning (the build then stops in the post-build spectator staging,
  whose ignored inputs a worktree lacks).

### Client rows Iceberg needs after its promotion

`NativeSpatialAudio.Install`, `RoomReverb.Install` and `PrecipitationAudio.Install` each bind their per-map keys in one
`foreach (var map in new[] { ArenaMapRegistry.Equator, ... })` that `AirPitWiringTests` pins to `ArenaMapRegistry.All`: add
`ArenaMapRegistry.Iceberg` to all three in the promotion commit. Rain audio has no other per-map data (the room precipitation
values are all 1.0, no room has a season preset; the depth-photo roof sampling stays Equator's). The client csproj stages
`acoustics\arena_iceberg\**` in `ArenaMapPortAcousticFile` and the Error condition (`AirPitWiringTests.TheThreeAudioTablesAnd...`).
Not part of this item: the ambient profile has two interior groups on `CommonAmbInEffects` (`CommonAmbientInGroup` 46 and
`LampsAmbientInGroup` 20) while `AmbientAudioProfile` takes exactly one `effects` route and `Bay5AmbientAudio.RefreshReverbRouting`
reroutes one group to the compatible reverb bus; the lamp group would stay on the muted original return unless that is extended.

### Open (host)

- Native initialization on the host: `[Acoustics] arena_iceberg: prepared rooms=46, portals=89, bake pairs=1003, routes=17751` and
  `native initialized; valid rooms=46, portals=89, pairs=1003, routes=17751`; the Debug line about the 10 mirrored volumes.
- Mirrored volumes in play: a listener at (-18.0, 2.2, -83.2) should classify as room 5 and at (3.4, 3.2, -71.1) as room 35
  (`NativeSpatialAudio.Probe`, F10). Both points also lie in the outdoor room 31's volume, which the smallest-room rule outranks.
- All 46 rooms carry `wall_occlusion` 1.0 and all portals `transmission` 0.0 (original data; other maps use graded values), so
  Iceberg's walls block more than on the released maps; not tuned, judge by ear.

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
