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
