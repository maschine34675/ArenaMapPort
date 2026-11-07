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
- **Word 13: an ALL POINT STATUS board (an inference, softened 2026-10-30).** Census of all 388 level files (467 holders, 0 errors), prop names per word: 0 TIMER/SIDE, 1 SCORE, 2 ALIVE, 11 CAPTURE POINT (6 levels),
  12 POINT STATUS A/B/X (level 228 only), 13 in three levels: `Arena_Yard_TDM_DesignStuff` 228 (four holders, all called ALL POINT STATUS), `Arena_Location_TEMP_
  CheckPoint_DesignStuff` 345 (TIMER (1), (2), inactive) and Iceberg's 376; 14 in 219 (`Bowl_TDM`, ALL POINT STATUS x2) and 346 (`AirPit_2.0_CheckPoint`, TIMER x5). The
  CheckPoint design scenes seem to carry their capture-point boards as 13 or 14 on copied timer TVs. That reading is an inference from the prop names of 228 and 219
  (ALL POINT STATUS) and from the places the words occur; nothing in Arena's code was read that says what word 13 shows, and the prop names of 345, 346 and Iceberg's 376 are copies that do
  not follow the words. The port has no capture-point face: `TYPE_WORDS[13] = (None, False, "none")`
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
  opened barriers and start gates instead (they are, see the integration section of 2026-10-30). The "same 42 colliders" claim that stood here is replaced:
  with door rows the bake drops only the 22 MOVED colliders below the two roots; the other 20 (rails and top panel of each sliding gate) stay standing,
  so the bake has 5,145 sources instead of 5,135 and the oracle numbers do not move (rails 12 triangles at |x| 1.74..1.90, opening 3.48 local, free height 2.86 local).
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

## 2026-10-30 - promotion into the released set and the review fixes (PLAN-ICEBERG item 12)

Integration on `feature/iceberg` (lead decisions of the six-workstream review; no Unity, no `build_all.ps1`, no non-DllOnly client build, no deploy, no
push; nothing under `D:/SPT41`). The commit trailer is `Co-Authored-By: Claude Sonnet 5.5` (the task text named Opus 5.5; the integrating agent is Sonnet 5.5
and the earlier workstreams were Sonnet too). Iceberg is now in `ArenaMapRegistry.All`, which is the lock-step moment of Bowl's `3ef5132`; the bundles do not
exist yet, so every gate that needs them is listed under "Pending U1" below.

### What the promotion changed

- **Registry and descriptor**: `ArenaMapRegistry.All` = Equator, Bay5, AirPit, Bowl, Iceberg. The descriptor carries the identity (name "Arena: Iceberg" and an
  own-words description), the exits, the three unit-level gate paths, the bake inputs (`BakeBounds`, `NavMeshBakeLayers`, `KeepLevelBorders`), the puddle
  materials, `UsesServerTime`, the 11 `DoorPoses` rows and the 6 `ScoreMonitors` rows; `tools/maps/iceberg.json`, `iceberg.modes.json` (regenerated from
  `iceberg-native-v2`) and `ArenaMapPort.Server/db/arena_iceberg/base.json` mirror it (`IcebergMapTests`, `MapDescriptorConsistencyTests`,
  `validate_map_descriptors.py`).
- **Both csproj files** stage Iceberg's inputs (acoustics, gate sounds, AI sidecar, load screens, shader sidecar, banner; the content key only on the first
  stage with `-p:ArenaMapPortCreateReleaseKeys=true -p:ArenaMapPortCreateKeyMap=arena_iceberg`), and every per-map client table has its Iceberg row where a released map needs one: ambient audio
  profile and mix defaults, `MapLightingDefaults`, native decals, spatial audio, reverb, precipitation audio, puddle footsteps.
- **Release plumbing**: `RELEASE_MAP_KEYS/NAMES`, the new `CROWDLESS_MAP_KEYS`, `make_release_zips.py` (a sixth part, `iceberg`, 20 files), `validate_distribution.py`,
  `deploy-to-spt.ps1`, `park-unreleased-dist.ps1`, `stage_spectators.py`, `extract_spectator.py`, the extractor tables (`extract_score_monitors.py` MAPS, the
  package pins `iceberg-native-v1` -> `iceberg-native-v2` everywhere) and the test scaffolding that kept Iceberg out of the released set.
- **Prose**: `AGENTS.md`, `docs/PLAN-ICEBERG.md`, `docs/dist-manifest.md` (a dated paragraph, "PENDING: not built, not staged, not released"; the published
  256-file contract stays), `BakeNavMesh.cs` (comment only). No release text (README, FORGE, CHANGELOG, release notes) was written; the README's "four maps" stays.

### Lead decisions and their reasons

1. **Gate paths**: `opened_barriers` and `start_gates` are the three unit-level paths that `iceberg.doors.json` carries (`DOORS/GATES_A/sanatorium_resp_gate`,
   `DOORS/GATES_B/sanatorium_resp_gate`, `DOORS/GATES_B/Sanatorium_CheckPoint_Gate`), `held_gates` none. The string must equal a row's `barrier` exactly, and
   a barrier with rows drops only the moved subtrees at bake time, so the client and the bake agree on what stays standing. The group-level strings
   (`DOORS/GATES_A`, `DOORS/GATES_B`) of the item 9 analysis match no row, so the bake would have dropped all 42 colliders below them (the legacy rule) while the
   client leaves the 20 unposed ones standing. The offline model follows `ResolveOpenedBarriers` now (below).
2. **Word 13** is a known type word that names no face (`SCORE_MONITOR_TYPE_WORDS[13]` dark, like word 10): the validator no longer fails on Iceberg's two
   CheckPoint boards (the red tip of review 5). "ALL POINT STATUS" is softened to an inference in this log and in the extractor's text (the evidence is prop names
   on other scenes, no code that says what word 13 shows).
3. **`iceberg-native-v1` -> `iceberg-native-v2`** in every pin (extractor tables, tests, docs that pin a package); `iceberg.modes.json` regenerated, which changes
   only the package name and the extraction timestamp (`extract_mode_objects.py ... --arena-data` reproduces the tracked file byte for byte).
4. **Ambient audio**: the profile row is in (104 players, 101 active, three inactive, four groups in source order, the Lamps group on the original interior
   mixer without the reverb reroute, kind counts 22 season / 30 loop). Defaults **3 dB / offset 0 / range 2**, provisional until a listening pass and NOT Bowl's
   3 dB / 0.4 / 3: the mix is `volume * gain + offset`, so Bowl's offset would lift the lamp loops from the authored 0.25 to about 0.75 (an audible hum in every
   interior) and flatten the interior players (median 0.10) to about 0.54; range 2 still carries the exterior sources (10 to 100 m) and 17 of the 20 lamps
   (authored reach 3 m) reach 6 m. The driver's strict original-activation check accepts the three player kinds only where the profile demands their counts
   (`SeasonKindPlayers`, `LoopKindPlayers`, 0 for the released maps, so a season or loop-kind player appearing there still fails closed). The 20 lamps and 1
   projector have `minDistance` 0 and are pinned by id and leaf name in the contract (`states.zero_min_distance_players`); the released contracts pin none.
5. **Own words**: name "Arena: Iceberg" and the description (the hotel with its rotunda, the CheckPoint layout with two team pockets behind sliding gates and capture points A, B and C) are the port's text, in the descriptor, `tools/maps/iceberg.json` and `base.json`; Arena's
   locales have no Name or Description row for Iceberg, so the pack rule is "the released maps that have an official text": `ArenaMapDescriptions.OfficialTextMapKeys`
   plus the maps without one must be exactly `All` (`The_released_maps_without_an_official_text_are_exactly_iceberg` fails when a promotion forgets the list).
6. **Crowdless, data-driven**: Iceberg's scenes hold no crowd objects (12 `FansAmbient` sound entries only). `validate_map_descriptors.CROWDLESS_MAP_KEYS` is the one
   Python list; the packer drops the spectator rule and the required file for it (a stray package is an unclassified file), `validate_distribution.py` requires the crowd
   maps (`extract_spectator.MAP_SCENES`) and the crowdless maps to partition `RELEASE_MAP_KEYS`, `stage_spectators.py` stages exactly the crowd maps, and
   `ArenaSpectators` returns no crowd scene for the key (no crowd, so no murmur bed either). `test_crowdless_maps.py` pins all four against each other.
7. **`UsesServerTime` true** (no Directional light in the imported scenes; Arena's dusk sun, sky and fog sit in the excluded Scripts scene, the WeatherRig supplies
   them), and the **Reserve rig row** (donor `Reserve_Base_Scripts`, the neutral tuning of AirPit and Bowl; `WeatherRigDefaultsTests`): `MapLightingDefaults` `ZeroLevel` -3.7 = the lowest
   spawn-marker floor (0.73; the 70 markers stand at y 0.73..4.99, modal 3.2) minus 4.4, a start value marked host-tuned. Alternatives for the
   first raid via `ZeroLevelOverride`: -6.2 (from the lowest walkable ground, y -1.8) and -1.2 (from the modal marker floor 3.2).
8. **No `shader_fallback_names`**: stated in the descriptor and above in this log ("Shaders"); the first host raid decides `MK4/Rock Simple` and
   `Nature/SpeedTree8` from the `[RebindShaders]` lines.
9. **Not yet, by design (Phase C)**: no MultiFlare package (79 FlareLights, four with a negative total scale), no water package (the `WaterForSSRv3` pool and the
   canyon `SimpleWater4` planes), no DynamicMaps overview. `OVERVIEW_MAP_NAMES`, `FLARE_MAP_KEYS`, `WATER_MAP_KEYS` are explicit "has it" lists and Iceberg is in none; the
   client csproj stages no Iceberg row for them (`test_crowdless_maps.NotYetRulesTests`).

### Review fixes

**The offline gate model follows the bake (review 3, the blocking gate concern).** `tools/validate/port_colliders.py` gained the door-row model of
`BakeNavMesh.ResolveOpenedBarriers` (`load_door_rows`, `resolve_opened_barriers`, `mark_barriers(cset, paths, door_rows)`: a row applies to the barrier path
that equals its `barrier` string exactly, an `at` row to the instance whose root stands within 0.05 m of it, a row with a closed position to the child whose
closed position matches within 0.05 m; only the moved subtrees are blocked, as self-or-ancestor; a path without rows keeps the legacy rule, so
every other map is unchanged), and `navmesh_diff.py` takes `--doors [PATH]` (bare = the descriptor's `<stem>.doors.json`; the report shows "(N door row(s))").
The geometry, measured in `test_iceberg_bake_inputs.PackageGateTests` for all five units in the unit's own local frame (GATES_A's east pair is the same
prefab scaled 0.8 x 0.832 x 0.866, so world lengths are the local ones times the scale):
- 42 colliders sit below the two gate roots; the door rows move 22 of them (the Check-Point gate 2, `gate_A` 10, `gate_B` 10) and the other **20 stay standing**
  (5 x `sanatorium_resp_gate_COLLIDER` rails, 5 x top panel `gate_C_COLLIDER`, and the layer-12 ballistic twin of each). Of the 20, the 10 on layer 18 are bake
  sources: **5,145** bake sources, not the 5,135 of the group-level model.
- The rails are **rails only**: `sanatorium_resp_gate_COLLIDER` is 12 triangles (three faces of two posts 0.16 x 0.27 m), every vertex at |x| 1.74..1.90, height
  0..4.26, so the doorway between them is **3.48 local units** wide (2.78 m on GATES_A, 3.48 m on GATES_B) and stays free.
- Leaves A and B (3.6 x 1.44) slide up in the rails and **stack on the static top panel C** (the leaf poses of the doors sidecar end exactly at its bottom edge,
  within 0.03), whose collider starts **2.86** above the gate root: the free height under the stack is 2.86 local units (2.48 m on GATES_A, 2.86 m on GATES_B), more
  than the agent (1.7 m) and the player (1.8 m) need. So the bake cannot be sealed by the colliders that stay.
- Re-run with the door-row semantics (`navmesh_diff.py --descriptor tools/maps/iceberg.json --doors`, 119,688 samples at 0.2 m): 12,255 colliders, **5,145** bake
  sources, 22 dropped by the barriers, no unresolved barrier; port component 0 **4,515.3 m2 (94.3 %)** of Arena's 4,787.6 m2, **82 of 84** located probes in it, the
  same two outside (`initial_spawn:Bear house (12)` and `stage_spawn:Bear house (29)`), RESPAWN_OUTDOOR joins the 83rd. Identical to the group-level run (5,135 sources),
  which is the point: the 10 extra sources change nothing. The "same 42 colliders" sentence of the "Bake inputs decided" paragraph above is replaced by this
  (edited in place) and the `DECIDED_*` constants of the test carry the three unit-level paths.
- Note for a Bowl oracle re-run: `navmesh_diff.py --descriptor tools/maps/bowl.json` takes `keep_level_borders` from the descriptor and models Bowl's kept layer-29 cubes
  as solids; a run with flags only models what the flags say. `BowlInvarianceTests` pins that no Bowl object is called `LevelBorder*` (Bowl's are `Cube`), so the
  name exemption of `RoundOpenedReason` changes nothing for it. Compare like with like.

**Audio (review 1).** `native_contract_loader`, `extract_bay5_audio.validate_graph` and the source recheck of `validate_native_features` compare the players with
`minDistance` 0 against `states.zero_min_distance_players` (21 for Iceberg, none for the four released contracts: a zero that an Arena update introduces there fails
closed; the contract's `audio` note says 17 players reach 3 m, three 5 m and one 7 m). The ambient driver's kind check is gated on the profile (decision 4;
`IcebergAmbientAudioTests` pins the profile counts against the contract, the released maps at 0 and the code strings of the check). Corrected statements are listed below.

**Spatial acoustics (review 2).** `extract_spatial_audio.REVIEWED_MIRRORED_VOLUMES = {'arena_iceberg': 10}` with `check_reviewed_mirrored_volumes` (called in `export`): the number of
mirrored trigger volumes of a graph must be the accepted one for that map (none for every other), so an Arena update that mirrors another volume stops the export
until a human has looked at it (`test_spatial_audio_iceberg`); `IcebergAcousticsTests` read the exported graph (`work/extract/spatial-audio/arena_iceberg`, or `ARENAMAPPORT_SPATIAL_AUDIO_DIR`): the 46 room rows against
the package and the client's mirrored-node rule against the real graph (they return early where the ignored package is not reachable). The repair rewrites only the portal ID of 533 routes (533 bytes differ from
Arena's file); the stored heuristic cost and traverse distance of those routes stay as Arena baked them: they belong to the doorway that was meant, which is 3.68 m from
the one in the scene (the twin routes differ by a median 0.07 and at most 0.36 in heuristic cost). By the decompile reading above `PropagationJob.Execute` measures from
the portal data and not from these two fields (not host-tested).

**Spawn placement probe (review 4).** `spawn_placement_probe.py` modelled `Physics.CheckCapsule(feet + 0.45, feet + 1.45, 0.35)` (it covers 0.10 to 1.80 m above the
feet) with 16 rays at only 0.5 and 1.2 m, so a 0.3 m curb 0.2 m from the point passed although the capsule's lower hemisphere touches it, and it measured the ceiling
from the floor, not from the spawn height (the capsule is checked at the spawn's own y). Now: seven rings (0.12, 0.25, 0.5, 0.85, 1.2, 1.55, 1.7 m), each as long as the
capsule's slice at that height (0.35 m on the cylinder, 0.117 and 0.287 m on the lower cap, 0.335 and 0.245 m on the upper), and `Verdict.headroom` = floor + ceiling - max(spawn y, floor).
It stays an UPPER bound on validity (a surface that slants between two rays, or lies between two rings, is not seen); the host's `spawn placement:` line is the authority.
Every verdict `test_iceberg_spawns` pins is unchanged under the stricter model (all player and bot rows pass, 69 of the 70 markers pass, `Bear house (38)` touches `LevelBorder (238)`),
and the synthetic tests of `test_spawn_placement_probe` pin the curb, the cylinder wall, the ring reaches and the headroom.
`AiAnchorsTests` ties `extract_ai_anchors.PLAY_AREA` (x -61..121, z -130..-33) to the descriptor's bake box (x -62..121, z -131..-33) and puts all 11 core points inside the box in all three axes.

**Score monitors (review 5).** Word 13 is in `validate_map_descriptors.SCORE_MONITOR_TYPE_WORDS` (dark): the red tip (13 validator errors) is gone with the promotion plus this word.

### Corrections to earlier sections of this log (the sections themselves stay as written)

- Ambient section: the lamp loops reach 17 at 3 m, **2 at 5 m, 1 at 7 m** (the projector, the 21st zero-min player, also 5 m); "two at 5 and 7 m" was ambiguous. The 30 loop-kind
  players are **20 lamps, 6 interior loops (Air, electricity, pool, sparks, sparks (1), projector), 3 exterior loops (river, river (2), sparks (2): the three inactive ones) and the water
  emitter's loop**; "the neon lamps, the projector and the water loops" and "20 lamps, the projector, six more interior..." (the projector counted twice) are replaced in the code comments.
- `[NativeAudio] ready players=104 started=101 ... sourceInactive=3` holds only in a season where every one of the 22 active season players has a bank: `started` = 101 minus
  `seasonalSilent`. Season banks: Summer 22, Spring 22, Autumn 20, SpringEarly 17, AutumnLate 13, Winter 4, so Summer or Spring show `started=101 seasonalSilent=0`, Autumn 99,
  SpringEarly 96, AutumnLate 92, Winter 83 (`Bay5AmbientAudio` counts the silent ones and skips them).
- Item 7 / item 9: the AI scene's patrol points lie **38 to 57 m** beyond the play area's north edge (z -33) and its ten Savage/Bot markers **20 to 98 m** ("40 to 60 m outside the border cubes"
  and "30 m or more outside the play area" were each right for one group only).
- Item 7: with `patrol_ways: []` the client builds NavMesh-derived ways from the Bot spawn markers the way it does for a map without a sidecar, **but the 11 real AI core points are authored
  instead of the single synthesized core point** that a map without a sidecar gets; "exactly like a map without a sidecar" in the AI paragraph was too strong.
- Item 8: "Word 13 = ALL POINT STATUS board" is an inference (edited in place, as asked).
- `extract_bay5_audio.py`: "byte-identical to before" is "identical to the accepted packages"; `native_features.py` named only AirPit as a registered audio exporter (AirPit, Bowl and Iceberg are).

### Facts that did not need a change

- `fog_color` (0.5, 0.5, 0.5) in `tools/maps/iceberg.json` is not a spike placeholder: it is `m_FogColor` of `Arena_Sanatorium_Scripts` (level255), whose `RenderSettings` has fog off,
  exponential-squared mode and density 0.01. `AuthorScriptsScene` writes it into the freshly authored Scripts scene; with `UsesServerTime` the WeatherRig owns fog at raid time
  (`ArenaLightingRepairs.ownFogColor = !WeatherRig.IsActiveForCurrentMap()`), so it is inert in play. Left as it is.
- `AdditionalMapBuilds.props` lists `iceberg` already (with the parked maps) and nothing imports the file, so it is unchanged: the staging rows are the explicit ones in the client csproj, as for Bowl.

### Not done / judgment calls

- Narrowing `RoundOpenedReason`'s `LevelBorder*` name exemption to layer-29 owners (review 3 N1) needs the C# harness or Unity; not done. It is harmless today: every `LevelBorder (N)` of Iceberg is a
  layer-29 cube that the map keeps anyway, and Bowl has none.
- The generated header texts that several released sidecars share, the "TeamFight_AI" wording of `iceberg.ai.json` and the "(positions only)" text of `extract_mode_objects` were left
  alone (touching them would change released files or generated output for no behaviour).
- Iceberg's key-source sample draws about 35 % of its bytes from streams of the CheckPoint-mode scenes; not changed (`KeySourcesTests` pins the table against the installed Arena).
- MASCHINE11 `verify-keys` for the four released maps was not run in this step; it belongs to the U1 sequence below, with the new Iceberg key file.

### Gate results (2026-10-30, VM, this tree; `Release`)

| Gate | Result |
|---|---|
| `dotnet build ArenaMapPort.Server -c Release` | 0 warnings, 0 errors |
| `dotnet build ArenaMapPort.Client -c Release -p:ArenaMapPortDllOnly=true` | 0 warnings, 0 errors (32.6 s; stops nowhere, the spectator inputs exist) |
| `dotnet test ArenaMapPort.Server.Tests -c Release` | **2,756 passed, 0 failed, 0 skipped** (10 s); the run before the last edits was 2,752 passed / 1 failed (`DoorPoseSidecarTests`, fixed) |
| `python -m unittest discover -s tools/tests -p "test_*.py"` | **2,718 tests, OK, 66 skipped** (593 s); the 27 modules that mention Iceberg, run verbosely afterwards: 1,208 tests, 3 skipped |
| skips that wait for the not-yet-built Iceberg bundles | **3 of the 66**: `test_puddle_water_census` for `map='iceberg'` (`test_listed_plus_excluded_is_exactly_the_water_shader_materials_of_the_package`, `test_the_rain_driven_water_material_keeps_its_keyword`) need the staged `material-shaders.arena_iceberg.json`, and `test_release_zips.test_real_dist_classifies_completely` needs the real `dist/` (5 required files missing, the first `arena_iceberg_scenes.bundle`). The other 63 are optional dependencies absent here (numpy, shapely, Pillow: DynamicMaps and cartography 26 + 18, Bay5 9 + 7, a full fixture 1), one AirPit v8 plan skip and one "package already filtered" skip, all older than this work. |
| `python tools/validate/validate_map_descriptors.py` | OK, 14 descriptors, 14 catalog entries, release `All` membership, `ArenaPaths.cs` default |
| `pwsh -NoProfile -File tools/tests/test_unity_log_scan.ps1` | `UNITY_LOG_TESTS_OK 8 cases` |
| generators in `--check` mode / regenerated and compared | `extract_gate_sounds.py rows --check`: five maps current (airpit 11 rows, bay5 5, bowl 4, equator 5, iceberg 6); `extract_score_monitors.py --check`: all five current; `extract_door_poses.py`: all five regenerate byte-identical; `extract_ai_anchors.py`: equator, bay5, airpit, bowl and iceberg identical; `extract_mode_objects.py` (iceberg, with `--arena-data`): identical; `extract_banners.py --check` 5 banners, `extract_loadscreens.py --check` 24 load screens, `extract_map_descriptions.py --check` ok; the spatial-audio export of the four released maps was re-run earlier in this integration and is unchanged |
| offline oracle with door rows | 5,145 bake sources, 22 dropped, 4,515.3 m2, 82 of 84 probes (83 with RESPAWN_OUTDOOR): identical to the group-level run |

### Pending U1 (needs Unity, the key store or the host; nothing of this was run)

1. **Build the Iceberg bundles**: `pwsh -Command "& 'tools/build_all.ps1' -MapKey arena_iceberg -MapDescriptor D:/ArenaMapPort/tools/maps/iceberg.json -PackageDir D:/ArenaMapPort/work/extract/iceberg-native-v2 -FullMap
   -OutputDir D:/ArenaMapPort/work/build/iceberg -BakeNavMesh \$true -Verify \$true -SkipGameRefsSync"` (absolute descriptor path; never two maps at once; the `[bool]` parameters only parse through
   `pwsh -Command "& ..."` from bash, and without `-SkipGameRefsSync` the script first re-runs `tools/sync-game-refs.ps1` against `D:/SPT41`, which touches the GameRefs of the released maps; corrected 2026-10-30, second pass). `tools/build_remaining.ps1` refuses Iceberg like Bowl.
2. **Validators on the result** (step 4 of the script stops at `validate_extract.py` for accepted packages; run the rest by hand): `validate_bundle_roundtrip.py`, `validate_preset.py`,
   `validate_bundle_blocks.py` (a block with a compression flag and csize >= usize must never ship), `validate_native_features.py` (package v2 against the contract, including the 21 zero-min players),
   `navmesh_diff.py --compare-bundle` with `--descriptor tools/maps/iceberg.json --doors` (expect **5,145** bake sources and the 4,515.3 m2 / 82 of 84 picture), `validate_map_descriptors.py`.
3. **First stage** with `-p:ArenaMapPortCreateReleaseKeys=true -p:ArenaMapPortCreateKeyMap=arena_iceberg` (a client Release build WITHOUT DllOnly; `ls work/release-keys` first, the four released keys must be there; the create flag reaches only Iceberg's stage command since 2026-10-30): creates `work/release-keys/arena_iceberg.key` (copy `work/release-keys` outside the repo at once),
   writes the three AMPB bundles and `keys/arena_iceberg.json`; the key-source row is already in `tools/bundle/key-sources.json` (`0e7a94a`). Then `validate_distribution.py` (expect
   `DISTRIBUTION_OK 5 maps 15 bundles 5 sidecars encrypted`), `validate_port.py --map iceberg --package D:/ArenaMapPort/work/extract/iceberg-native-v2 --distribution` (without `--package` it defaults to `work/extract/iceberg`, the superseded 0.5.4 spike package, and fails the source-plan and scene-order checks), `make_release_zips.py --plan` (core 154, equator 24, bay5 30, airpit 22, bowl 26, **iceberg 20**; the
   dist-manifest paragraph states the expected 276 files) and `verify-keys --arena` against every other Arena build at hand (the user's 0.6.0 on MASCHINE11, UNC path from PowerShell).
4. **Re-run the two puddle-census cases** and the `[RebindShaders]` reading below once `material-shaders.arena_iceberg.json` exists.
5. **Release texts** (README, FORGE, CHANGELOG, release notes, the packer's player texts, the "four maps" wording) are the lead's, after U1; none was written here. `docs/dist-manifest.md` keeps the 256-file contract as the published one.
6. **Deploy** only through `tools/deploy-to-spt.ps1` with EFT closed (the first Iceberg deploy moves a new encrypted bundle set through Syncthing; wait for the sync before restarting EFT).

### Host-check list for the first Iceberg raid (GPU-2; after U1)

- Start: `[NativeAudio] authored inactive stock graph map=arena_iceberg players=104 banks=28 clips=237 support=8`, then `ready players=104 started=101 seasonalSilent=0 ... sourceInactive=3` in Summer or Spring (corrections above);
  the built bundle's player fields and the AudioSource `MinDistance` (1) read back from it.
- Bots: patrol without any `[AiAnchors]` / `[RaidFix]` warning (the 11 real core points authored, NavMesh-derived ways, 0 hubs); the spawn placement line (`spawn placement:`) shows how many rows the repair moved (the probe says none for
  the players and bots, and `Bear house (38)` for the markers); the NavMesh line (`[NavMesh] added navmesh ...`) names the new bake.
- Gates: held-open at raid start, the 20 unposed colliders (rails and top panel) leave the doorway free for the player and for bots; gate sounds are silent by design without a hold (`Spawns/SpawnGateHold`), so listen with one.
- Acoustics: the 10 mirrored trigger volumes (corridor_2_1 and coffe_hall) put the listener in the right room; the lamp hum is audible within 6 m of 17 of the 20 lamps (10, 10 and 14 m for the other three) and not beyond; judge the loudness of the mix against the defaults 3 dB / 0 / 2.
- Shaders: the six names of "Shaders" (`Cloth/ClothShader`, `Cloth/ClothShader_backface`, `ANGRYMESH/PBR Rocks/PBR BlendTopDetail (Legacy)`, `Custom/Billboard_FogSheet_Simple` from Customs; `MK4/Rock Simple`, `Nature/SpeedTree8` unproven) in the `[RebindShaders]` lines.
- Lighting: the ZeroLevel -3.7 start value (try -6.2 and -1.2 through `ZeroLevelOverride`), the fog and the sky of the WeatherRig on a map whose Scripts scene is excluded.
- Released maps after the shared-path changes: one raid on a released map and one hideout visit (`NativeSpatialAudio.BuildGraph`'s shared path, the generic-class Harmony trap), the `max` frame time of `uibridge_status`.
- Not built on purpose (Phase C): no overview, no flares, no water, no crowd.

## Second integration review of the promotion (2026-10-30, second pass)

Two reviewers read `1bd5868` and found **no blocking defect**: the released maps' tracked rows, sidecars and `db/` files have no diff, `make_base_json.py` regenerates all five `base.json` byte for byte, the
modes, AI, door-pose and gate-sound sidecars regenerate identically, `dist/` equals the published 0.4.0 package except the two DLLs plus exactly 15 added Iceberg files (271 of the 276 the contract expects), and the
packer classifies all 271 with 0 unclassified. This pass fixes every non-blocking item that was a real defect and cheap, and records the decision for the rest.

### Changed

- **Content-key creation is per map (hardening, the reviewers' first operational item).** `-p:ArenaMapPortCreateReleaseKeys=true` used to add `--create-key` to the `stage` command of all five maps, so Iceberg's
  first stage would also have recreated a released map's key, silently, had `work/release-keys/arena_<map>.key` gone missing (all four exist today, dated 2026-10-20). The client csproj now has
  `ArenaMapPortCreateKey_<map_key>` per map (the flag AND `-p:ArenaMapPortCreateKeyMap=<that map>`), each `stage` Exec carries its own only, and the flag without a map or with a name outside the five maps is an Error
  before anything is staged. First stage: `-p:ArenaMapPortCreateReleaseKeys=true -p:ArenaMapPortCreateKeyMap=arena_iceberg`. Verified on a scratch project built from the csproj's own lines (no flag: nothing; flag alone: the
  Error; flag + `arena_iceberg`: `--create-key` for Iceberg only; a typo: the Error); pinned by `test_bundle_crypt_tool.BuildWiringTests.test_staging_creates_a_content_key_only_on_request`. Texts: AGENTS.md,
  bundle-encryption.md, dist-manifest.md, PLAN-ICEBERG item 14 and the recipe below. The off-VM backup of `work/release-keys` right after the first stage is still the user's.
- **Deploy guard (the pre-U1 deploy hazard).** `tools/deploy-to-spt.ps1` (whole-dist mode, dry run too) fails when the staged server mod has `db/<map_key>` and the client plugin lacks `bundles/<key>_{scenes,navmesh,preset}.bundle` or
  `keys/<key>.json`; `-DllOnly`, the DynamicMaps mode and the migrations never reach it. Today's `dist/` is refused with `arena_iceberg (missing bundles/arena_iceberg_scenes.bundle); ...` (checked against the real dist
  with a dry run to a non-existent install root, nothing written). `test_dllonly_deploy.ps1` gained the cases (refused in dry run and with `-Confirm` with the install unchanged, `-DllOnly` unaffected, a missing key file alone
  named, a complete map passes); the four other deploy test scripts still pass (client folder 22 checks, shader sidecar, equator db, DynamicMaps 122 cases). What the guard cannot cover: a `-DllOnly` deploy of this tree to a host without
  Iceberg's files still logs four `Required map file is missing` warnings (`Plugin.cs` `LogStartupBanner`) and the notices system counts five maps; do not deploy this tree before U1, or park Iceberg's files first.
- **Door-row model, the two oracle divergences.** `validate_map_descriptors.door_pose_errors` now refuses an `opened_barriers` path without rows that contains a posed barrier or sits inside one (the bake's legacy
  drop-all rule would then bypass the rows; Iceberg's first group-level form was exactly that; checked clean on all five maps, and `test_door_poses.MirrorValidatorTests` mutates Iceberg's descriptor both ways). It does NOT
  demand rows for every opened barrier: AirPit's portal-gate halves and shop shells have none by design. `navmesh_diff.py --descriptor` without `--doors` stays on the legacy model (changing the default would move the
  recorded Bowl and AirPit oracle numbers), but now prints a note on stderr when the doors sidecar has rows for one of the descriptor's barriers ("pass --doors to follow the bake"); `test_navmesh_diff` pins both cases.
  Iceberg's U1 oracle command carries `--doors` (below).
- **.NET test gaps (gate sounds and small ones).** `GateSoundModelTests` now include Iceberg: 6 rows, 11 sounding gate keys (every key hits exactly one row), the gain table (-0.9 dB for all six rows by map, not by
  clip: its check-point clip is Equator's `arena_equator_old_slide_doors`, -15.0 dB there), the real seam picks at scale 0.5 from the per-map fit (`arena_san_gates_blue` 1.198 s removed, event lands 1.352; `red` 2.499 / 2.651;
  `arena_equator_old_slide_doors` 1.297 / 1.313; they pass against the extracted package), the staged-WAV loop (12 files, 11 distinct clip ids) and `GateSoundWiringTests` (the csproj row of `arena_iceberg`).
  `DoorPoseModelTests` "rows need no position to tell parts apart" loops Iceberg too, `AmbientAudioProfilesTests` forbids an `ArenaMapRegistry.Iceberg` literal in `Bay5AmbientAudio`, and the stale test name
  `NavMeshBakeInputs_ExistForAirPitAndBowlOnly` is now `..._ExistForAirPitBowlAndIcebergOnly`.
- **Text that omitted Iceberg although the code is descriptor-driven** (strings only, no key, default or meaning changed): `WeatherTestOverride` (`ApplyOnServerTimeMaps`), `HdrImageTuning` (tonemap profile),
  `ReverbAudioProbe` (second map list), `GateSounds` (`GateSounds` key), `SpawnGates` (`GatesOpened` doc: Iceberg's longest drive is 5.0 s x 0.5 = 2.5 s; `SpawnGateDrive` and `SpawnGateDriveScale` descriptions:
  gate leaves 2 to 5 s, check-point gate 3 s), `GateSoundModel.Gain` doc (-0.9 dB). Stale "prepared ahead of its promotion" comments in `test_gate_sounds.py`, `BundleCryptoTests.cs`, `extract_gate_sounds.py` and
  `extract_score_monitors.py`.
- **Docs.** HANDOFF.md has a first section for the branch state; INDEX.md describes the log as it is now; `tools/build_remaining.ps1` names Iceberg among the released maps (and `iceberg-native-v2`); the head paragraph of
  AGENTS.md no longer says only four maps are registered; AGENTS.md "Iceberg promotion rules" (6) to (8) carry the guard, the nesting check and the .NET pins. **Recipe corrections to the "Pending U1" list above:** the
  build command carries `-SkipGameRefsSync` (without it `build_all.ps1` first re-runs `sync-game-refs.ps1` against `D:/SPT41`) and goes through `pwsh -Command "& ..."` with escaped `\$true`; `validate_port.py` needs
  `--map iceberg --package D:/ArenaMapPort/work/extract/iceberg-native-v2 --distribution` (its default package is `work/extract/iceberg`, the superseded 0.5.4 spike package, and fails the source-plan checks); the first
  stage names the map.

### Decided, not changed (reasons)

- **Narrowing the `LevelBorder*` name exemption of `RoundOpenedReason` to layer-29 owners** (`BakeNavMesh.cs`): needs the Unity harness to prove, and is harmless today (Iceberg: 365 layer-29 cubes plus the layer-0
  `LevelBorders` group; Bowl none). Stays open as in the first pass.
- **`STALE_SPIKE_BUNDLES` for Iceberg's spike scenes bundle** (1,124,068,269 B plaintext): not added, as for Bowl ([bowl-release.md](bowl-release.md)): `work/build/iceberg` does not exist (the spike is archived as
  `iceberg-spike-20260915`), so the csproj stops at the first missing bundle before anything is staged. Add the hashes only if the spike ever lands in `work/build/iceberg` again.
- **Score monitor faces of Iceberg.** Word 0 (`SCORE`, `SIDE`, `ALIVE (3)`) gets the timer face and word 13 (`SCORE (1)`, `TIMER (1)`) stays dark although the prop names suggest otherwise: the prop names of
  Iceberg's holders are copies that do not follow the words (item 8 above), word 0's timer meaning rests on Bowl's naming (the sidecar's `confirmed` flag is no more than that), and a dark board is the safe failure; whether word 13 is the timer in CheckPoint is decided on the
  first host raid (`Monitors/Demo = Cycle`), together with a face for it.
- **Release number and cut preconditions** (the user's): both csproj still carry `<Version>0.4.0</Version>` and `make_release_zips.py` reads it, so a five-map build would be named and versioned like the published
  0.4.0. Bump both together before the cut and before any deploy that must be told apart by version; nothing here touched the number.
- **`SainLocationShim`** gives Iceberg the generic `Factory` SAIN location like AirPit and Bowl (config key `Bots (arena_iceberg)/SainLocation`): a tuning choice for the first bot raid, not a gap.
- **Spatial-audio provenance of the released maps.** A re-export of the four released maps gives identical bake and room-tone WAV, but `spatial-audio.json` differs in the Arena-build provenance fields
  (`source_asset_sha256`, `raw_sha256`, `source_scene_sha256`, `systems[0].location_info.id`): the work copies of 2026-09-28 to 2026-10-18 predate Arena 0.6.1. Not a regression of this branch; expect those
  fields to change the next time a released map's acoustics are re-staged.
- **Phase C "not yet" lists**, so the later promotion is one lock-step edit: DynamicMaps overview (`DynamicMapAssets.cs` `Maps`, `validate_distribution.OVERVIEW_MAP_NAMES`, the csproj `ArenaMapPortStageDynamicMaps`,
  `deploy-to-spt.ps1` ValidateSet and map switch, `DynamicMapAssetsTests`, `test_dynamicmaps_deploy.ps1`), MultiFlare (`NativeMultiFlare.cs`, `validate_distribution.FLARE_MAP_KEYS`, the csproj flare rows,
  `extract_multiflare.py`), water (`WATER_MAP_KEYS`, Bay5 only); `test_crowdless_maps.NotYetRulesTests` pins the absences, so adding one edits that test too.

### Gate results (2026-10-30, VM, this tree; `Release`)

| Gate | Result |
|---|---|
| `dotnet build ArenaMapPort.Server -c Release` | 0 warnings, 0 errors |
| `dotnet build ArenaMapPort.Client -c Release -p:ArenaMapPortDllOnly=true` | 0 warnings, 0 errors (39 s) |
| `dotnet test ArenaMapPort.Server.Tests -c Release` | **2,756 passed, 0 failed, 0 skipped** (11 s); the first run of this pass was 2,755 passed / 1 failed (`TheRealRowSidecarsParseWithTheirClipsAndEveryRowPlaysAtPitchOne` counted 9 distinct clip ids, 11 with Iceberg's two own ones: fixed) |
| `python -m unittest discover -s tools/tests -p "test_*.py"` (PowerShell, verbose) | **2,719 tests, OK, 66 skipped** (734 s); one test more than the first pass (the nesting check of `door_pose_errors`). The same **3 of the 66** wait for the not-yet-built Iceberg bundles (`test_puddle_water_census` for `map='iceberg'` twice, needs the staged `material-shaders.arena_iceberg.json`; `test_release_zips.test_real_dist_classifies_completely`, 5 required files missing, the first `arena_iceberg_scenes.bundle`); the other 63 are optional dependencies (numpy, shapely, Pillow) and two older plan/package skips, none Iceberg-related |
| `python tools/validate/validate_map_descriptors.py` | OK, 14 descriptors, 14 catalog entries, release `All` membership |
| `pwsh -NoProfile -File tools/tests/test_unity_log_scan.ps1` | `UNITY_LOG_TESTS_OK 8 cases` |
| deploy test scripts | `test_dllonly_deploy.ps1` 48 cases passed (with the new guard cases), `test_client_folder_deploy` 22 checks, `test_equator_db_deploy`, `test_shader_sidecar_deploy`, `test_dynamicmaps_deploy` 122 cases, all passed |
| generators in `--check` mode | `extract_gate_sounds.py rows --check`: five maps current (airpit 11 rows, bay5 5, bowl 4, equator 5, iceberg 6); `extract_score_monitors.py --check`: all five current; `extract_banners.py --check` 5 banners, `extract_loadscreens.py --check` 24, `extract_map_descriptions.py --check` ok. The docstring edits of the two extractors changed no output. The door-pose, AI-anchor, mode-object and spatial-audio generators were not touched in this pass (their first-pass regeneration results stand) |
| `deploy-to-spt.ps1` dry run against the real `dist/` (non-existent install root) | refused by the new guard with the five missing Iceberg files named; nothing written |
| MSBuild logic of the create flag | scratch project with the csproj's own lines: no flag, flag alone, flag + `arena_iceberg`, a typo, `arena_iceberg` without the flag: as listed above |

### Pending U1 (unchanged by this pass except the recipe lines corrected above)

The list "Pending U1" and the host-check list above stand. Additions: `ls work/release-keys` before the first stage (arena_equator, arena_bay5, arena_airpit, arena_bowl) and back the folder up off the VM at once afterwards;
the oracle command for the built bundle is `navmesh_diff.py --map iceberg --descriptor tools/maps/iceberg.json --doors --compare-bundle <the built navmesh bundle>` (without `--doors` the run prints the note
above and models the legacy rule); do not deploy before the first stage is done.

## 2026-10-30 evening - U1: two import fixes, the first stage, deploy and the first Iceberg raids on GPU-2

- **U1 attempt 1** (19:28, 1 min): `BuildBundles` failed closed on `Normal texture 03737f828087638b rejected: invalid dimensions/pixels or unsupported/mixed
  material usage` (`RockGr22_LOD_n`). A replica of `NormalTextureImportPolicy` over all 482 normal textures of package v2 (scratch `normal_policy_check.py`)
  found 18 rejects, all bound by EFT's `MK4/Rock` / `MK4/Rock Simple` shaders on `_Normalmap`, `_DetailBump` and `_MaskBump`; all 18 are ordinary packed
  DXT5 normals (R 255 everywhere, X in alpha, Y in green, |G-B| <= 6). Fix `e0729f8`: those slots join the normal readers (shader-scoped); harness 64 checks;
  the replica then finds 0 rejects (461 PackedRaG/DXT5, 21 PackedRaG/BC7). No released map uses an MK4 shader.
- **U1 attempt 2** (19:32-19:58, 30 min): Unity build, NavMesh bake, identity patch, `fix_stored_blocks.py`, `make_base_json.py`, `patch_preset.py`,
  `validate_bundle_blocks.py` and `validate_extract.py` passed; `validate_bundle_roundtrip.py` failed with `native feature bundle validation: Native features:
  bank name missing/ambiguous sharedassets112.assets:110`: the identical `autoservice_wind.SoundBank` exists in sharedassets112 and as a copy in sharedassets259,
  each referenced by its own players, and the validator looked banks up by name. Fix `46941b7`: a shared name is resolved through the built players that
  reference the source bank (exactly one same-named candidate, no two source banks on one built bank). The same commit carries the stand-in shader stubs the
  build regenerated from package v2 (15 changed, 6 new: MK4/Rock, MK4/Rock Simple, Nature/SpeedTree8 and three p0 variants), as every build does for its package.
- **Validators by hand** on the U1 output (`work/build/iceberg`): `validate_bundle_roundtrip.py ... --require-navmesh` `OK -- all checks passed` (1 non-blocking
  warning), `validate_preset.py` OK (7/7 scenes), `validate_map_descriptors.py` OK. Sizes: scenes bundle 1,505,248,944 B (larger than the 1.1-1.3 GB estimate),
  NavMesh bundle 324,566 B (NavMeshData 705,132 B raw; 18,703 vertices, 8,599 triangles, 40 tiles).
- **Offline NavMesh comparison** (`navmesh_diff.py --map iceberg --descriptor tools/maps/iceberg.json --doors --points iceberg.bots.json --points
  iceberg.exits.json --compare-bundle`): the built mesh covers 115,767 of 119,688 Arena-walkable samples (96.7 %), one component; all 15 probes (4 player
  spawns, 9 bot core points, 2 exits) inside it. The largest Arena-walkable groups without port floor are 13.2 m2 at (46.7..50.7, 2.5, -88.8..-80.0) and
  12.4 m2 at (-6.4..-1.8, 5.0, -96.0..-91.6).
- **First stage** (client Release build without DllOnly, `-p:ArenaMapPortCreateReleaseKeys=true -p:ArenaMapPortCreateKeyMap=arena_iceberg`, 20:08, 80 s):
  `CREATED ... work\release-keys\arena_iceberg.key`; the four released keys untouched (dates of 2026-10-20). The key store was copied at once to
  `C:\Users\Administrator\ArenaMapPort-release-keys-backup\20261030-iceberg-first-stage\` (five files, byte-identical); an off-VM copy stays open, as before.
  `validate_distribution.py`: `DISTRIBUTION_OK 5 maps 15 bundles 5 sidecars encrypted`; `validate_port.py --map iceberg --package ...iceberg-native-v2
  --distribution`: `PORT_VALIDATED` (823 materials, 5,370 meshes, 1,407 textures, 40 NavMesh tiles, 8,599 triangles, 3 verified starts); `make_release_zips.py
  --plan`: Iceberg part 20 files (+1 generated), 1,516,090,987 B raw; total 276 dist files, raw 5,370,658,093 B, estimated single ZIP 5,349,682,234 B (about
  5.0 GiB, against 3.57 GiB for 0.4.0).
- **verify-keys**: `OK verify-keys arena_iceberg: wrap 0 (built for 0.6.1.0.48206) unlocks the store's content key against Arena 0.6.1.0.48206 at
  \\192.168.178.109\C$\Battlestate Games\Escape from Tarkov Arena (4 files, 3,224,800 of 560,470,032 B sampled, 106 ms, crypto: CNG)` (GPU-2's own install,
  the same build as the VM's), Bowl alike. MASCHINE11 (Arena 0.6.2.0.48473) was offline, so the check against a second Arena build is still open.
- **Deploy** (`deploy-to-spt.ps1 -SptRoot 'D:\SPT41' -Confirm`): 276 files, 15 new, 7 overwritten (both DLLs, Iceberg's three bundles over the parked
  September plaintext copies on the host, its shader sidecar and base.json); server DLL on the mirror = the build (`BDD19FCE...`). `gpu-run deploy-wait
  --server`: 2,458 files identical on the host after 139.9 s, server started; `gpu-run start`: MainMenu, 16/16 plugins identical (VM build = host disk =
  loaded). Startup: `[Arena] Escape from Tarkov: Arena 0.6.1.0.48206 at C:\Battlestate Games\Escape from Tarkov Arena (registry; 15,351 files, 76.1 GB verified)`
  and `Version 0.4.0 loaded. Maps: Arena: Equator, Arena: Bay 5, Arena: Air Pit, Arena: Bowl, Arena: Iceberg.`; no ArenaMapPort warning.
- **Raid 1** (20:19, PMC, no bots, 12:00 clear; `gpu-run raid --map iceberg`, the tile reads `ARENA: ICEBERG`): `load arena_iceberg: location 6.5 s (diff 1.7),
  game started 32.0 s`, 0 warnings, 0 errors. Lines: `[NavMesh] added navmesh for 'arena_iceberg': 18703 vertices, 8599 triangles`; `[SpawnGates] gates open
  (navmesh installed): 11 part(s) registered, 5 park carver(s) in place` (the ten sliding leaves and the rotating gate parked at Arena's open pose; the
  `_A` leaves and the rotating gate report their collider above the 1.8 m walking band); `[RaidFix] arena_iceberg TeamFight borders: 366 object(s) matched ...
  RemoveTeamFightBorders=False`; `[Acoustics] arena_iceberg: 10 mirrored trigger volume(s) keep their negative scale` and `native initialized; valid
  rooms=46, portals=89, pairs=1003, routes=17751`; `[NativeAudio] ready players=104 started=99 seasonalSilent=2 season=Autumn` (as predicted for Autumn);
  `[Lighting] arena_iceberg applied ... 1279 light(s) ... cullingLights=1126`; `[Monitors] arena_iceberg: 4 of 6 monitor row(s) can show a face`;
  `[GateSounds] prepared 3/3 clip(s) of arena_iceberg for drive scale 0.50`; `[Exfil] registered 2 exit(s)` (East Gate (72.0, 2.0, -94.0), West Gate
  (-30.5, 2.8, -85.0), 8 s); `[PuddleFootsteps] cached water surfaces=27 masks=2`; `[IndoorSurface] map=arena_iceberg enabled=False ... elapsedMs=1024` (the
  known classification freeze, here 1.0 s about 10 s after the start). `[RebindShaders]`: every pass `N/N stand-in material(s) rebound` (737 in
  Arena_Sanatorium), among them `14x MK4/Rock`, `1x MK4/Rock Simple`, `4x Nature/SpeedTree8`, `10x ANGRYMESH`, `3x Cloth/ClothShader`, `1x
  Cloth/ClothShader_backface`, `1x Custom/Billboard_FogSheet_Simple`; no failure line, so no `shader_fallback_names` row is needed so far (the olive tree
  itself was not looked at).
  Spawn at (95.3, 0.7, -89.6) in the Bear pocket. `uibridge_teleport dryRun` from there: A 83.8 m, B 87.0 m, C 94.4 m, Usec spawn 152.8 m, both exits,
  bot core point 01: every path complete, no wall at 1.3 m. `uibridge_walk` through the GATES_A doorway to (78.0, 2.0, -90.0): arrived (19.4 m along the
  navmesh). Frame time over 30 s: 119.7 fps, mean 8.4 ms, max 61.2 ms. Screenshot: the hotel front (stairs, fountain, ivy) renders correctly; two very
  bright glow blobs sit in the two lobby doorways behind the fountain, in both raids (open look item, see below).
- **Raid 2** (20:20, PMC, bots Medium, 19:30): load 4.7 s, game started 23.6 s, 0 warnings. `uibridge_bots observe=20`: two USEC PMCs followed, 104.6 m and
  90.6 m of path in 20 s, idle 0 %, layers PatrolAssault / PatrolFollower; a third joined later. No ArenaMapPort warning in the session (the only Error line is
  EFT's `Coroutine couldn't be started because the the game object 'MenuScreen' is inactive!` at 20:20:56, from the menu between the raids).
- **Hideout** opened from the menu (`hideout-open.json`: screen Hideout, HideoutGameWorld, no ArenaMapPort error) and back.
- **Equator** (20:23, released map, no bots; the shared `NativeSpatialAudio.BuildGraph` path changed on this branch): `[Acoustics] arena_equator: native
  initialized; valid rooms=21, portals=50, pairs=210, routes=31676`, `[NavMesh] added navmesh for 'arena_equator': 10395 vertices, 4567 triangles` (unchanged),
  `[NativeAudio] ready players=25 started=23`, crowd `load total=2110 ms`, `[CrowdBed] arena_equator: 4 stands`; 0 mod warnings.
- **Open after this round**: the two glow blobs in the lobby doorways (which lights, and whether Arena shows them so); the olive tree and the canyon rocks
  looked at in the game; a listening pass (ambient 3 dB / offset 0 / range 2, the 20 lamp loops, gate sounds at -0.9 dB with a held gate); `ZeroLevel`;
  the verify-keys check against Arena 0.6.2 on MASCHINE11; the Phase C items (flares, water, overview); the release number and form (the single ZIP grows to
  about 5.0 GiB).

## 2026-10-30 evening - the glow blobs are the respawn-hall chandeliers under bloom; Iceberg bloom defaults 4.0/0.2

- **Finding** (GPU-2, 20:27-20:32, botless raid, Bear spawn (95.2, 2.25, -89.4) looking west at yaw 273.4): the two disc-shaped glare blobs of the
  first raids sit on the camera rays through the screen points (360, 140) and (1310, 140) (FOV 50, computed offline: scratch `lights_on_ray.py`) at about
  12 m, where `SBG_Arena_Sanatorium_Main/OO/RESPAWN_OUTDOOR/Chalet_chandelier_01` hangs twice (raycast: `Chalet_chandelier_01_BALLISTIC_glass` at 11.06 m;
  pivot (84.68, 7.52, -93.70), mesh bounds y 5.49..7.53). Its LOD0 binds `Sanatorium_Emissive_Orange` on `p0/Reflective/Bumped Emissive Specular SMap`
  (`_EmissionPower` 10, `_EmissiveColor` (0.68, 0.58, 0.10)). The KPP point and spot lights under it read intensity 1.0 at run time (range 2.28-8.2).
  Live A/B with `persist=false`: `GlobalLightIntensityMultiplier` 0.2 and bloom 2.10/0.35 (= the camera's own donor-rig values) leave the blobs
  unchanged; bloom threshold 8 / intensity 0 removes them and shows two candle chandeliers; 4.0/0.2 shows the candles with a soft glow.
- **Fix** (`15a13d8`): `BloomTuning` gives Iceberg the defaults 4.0/0.2 (AirPit's 1.2/0.25 pattern), and the AirPit pair migration is generalised
  (`MigratePairDefaults`): a saved -1/-1 pair moves once to the map's pair (`BloomDefaultsRevision` 1), any other pair stays. Client DLL-only deploy,
  `gpu-run deploy-wait --server --game` (2 files identical after 2 s, game restarted, 16/16 identical): `[Bloom] arena_iceberg: config re-seeded to bloom
  defaults revision 1 (2 value(s) moved; threshold=4.00, intensity=0.20)`. Raid at 19:30 (20:35, botless, 0 warnings): the chandeliers render as candles
  with a soft glow, no disc. .NET 2,756 passed.

## 2026-10-31 - Per-map emission cap; Iceberg's bloom back to the normal 2.10/0.35

Workstream of the lead's list (worktree branch off `feature/iceberg`, not host-run yet). Evidence from the lead (GPU-2 F10 inspections by the user, 2026-10-31): the two lobby crystal lamps
(`SBG_Arena_Sanatorium_Light/OO/INDOORS/LOBBY/Crystal_Lamp_1` and `Crystal_Lamp_1 (1)`, `Crystal_Lamp_1_LOD0` submesh 2) use `Emission_Yellow` (`367e4a61931e682b`, `Standard`, keyword `_EMISSION`,
`_EmissionColor` (67.794, 17.392, 0.0)) and stay fully over-exposed unless `BloomIntensity` is about 0.001; the respawn-hall candle chandeliers (`Chalet_chandelier_01` x2, `Sanatorium_Emissive_Orange`
`d52be16e9d5d8091`, p0 emissive, `_EmissionPower` 10) bloom into discs at 2.10/0.35. The 4.0/0.2 bloom of 2026-10-30 hid the second symptom for the whole map and did nothing for the first.

### What EFT's shaders read (the rule table)

The emission rules are not guessed from the material names: EFT's own `StreamingAssets/Windows/shaders` bundle (378 MB, read-only, UnityPy, 4.6 s) declares, per shader
(`EftShaderFactsTests` in `tools/tests/test_iceberg_emission_cap.py` re-reads it; skipped without UnityPy or the install):

| Shader | Declared emission properties | Cap rule |
|---|---|---|
| `Standard`, `Standard (Specular setup)` | `_EmissionColor`, `_EmissionMap` (no power, no visibility); the colour is read only with keyword `_EMISSION` | Color family: max RGB of `_EmissionColor` <= cap, one scale for the three channels, alpha kept |
| `p0/Reflective/Bumped Emissive Specular SMap` | `_EmissionMap`, `_EmissiveColor` (Color), `_EmissionVisibility` (Range 0..1), `_EmissionPower` (Range 0..10, so Iceberg's 10 is the shader's own maximum); **no `_EmissionColor`** | Power family: `_EmissionPower` x `_EmissionVisibility` x max RGB of `_EmissiveColor` <= cap, scaled through `_EmissionPower` |
| `p0/Reflective/Bumped Emissive Specular`, `p0/Reflective/Bumped Animated Emissive Specular SMap` | `_EmissionMap`, `_EmissionVisibility`, `_EmissionPower` (+ the two animation maps/colours of the Animated one); **no `_EmissiveColor`, no `_EmissionColor`** | Power family with colour peak 1 (the colour comes from the map) |
| `p0/Cutout/Bumped Diffuse Emissive` | `_EmissionMap`, `_EmissionColor` (HDR flag 16), `_EmissionAlphaCutoff`; no power, no visibility | Color family |

So the leftover `_EmissionColor` that most package materials carry (383 of Iceberg's 400 `p0/Reflective/Bumped Specular SMap` materials carry one, and those shaders are not emissive at all) is never read by the p0 emissive shaders, and a leftover
`_EmissiveColor` on a shader without one is ignored (the glue asks the material `HasProperty` for every property and the model decides by the real shader name). The product form for the power
family is the one EFT's own scripts imply (`MaterialEmission.TurnLights` and `EmissionFlicker` drive `_EmissionVisibility` between 0 and the material's value, `GarlandSwitcher` tweens
`_EmissionPower` to 0 and back; a power of 0 is a lamp that is off, e.g. `Airpit_emissive_01_off`), but how the compiled fragment combines the factors with the map was NOT read. The scaling is
therefore linear in one property, `_EmissionPower`, the one nothing animates per renderer: `_EmissionVisibility` is overridden through a `MaterialPropertyBlock` by those scripts (a material write
would be defeated), and `_EmissiveColor` is the tint. Stand-in shaders (`Hidden/ArenaMapPort/...` that ShaderRebind has not rebound) match nothing. None of the three emission-driving EFT
components (`MaterialEmission`, `EmissionFlicker`, `GarlandSwitcher`) is in Iceberg's scenes.

### Census (package `iceberg-native-v2` and the four released maps, renderer-referenced materials, level as defined above)

Iceberg: 37 emissive materials in use (23 Power, 14 Color; shaders: 22 `Bumped Emissive Specular SMap`, 12 `Standard`, 2 `Cutout/Bumped Diffuse Emissive`, 1 `Animated Emissive`), 18 above 1.0 and
**six above 4.0**:

| Material (id) | Family | Level | Scale at cap 4.0 | Written | Renderer uses |
|---|---|---|---|---|---|
| `Emission_Yellow` (`367e4a61931e682b`) | Color | 67.794 | x0.0590 | `_EmissionColor` (4.000, 1.026, 0.000) | 4 (the two crystal lamps) |
| `Prison_Emission_red` (`cb70cc186da35b5a`) | Power | 10.000 | x0.4000 | `_EmissionPower` 4.000 | 37 |
| `Prison_Emission_passive` (`1a52de9137dff10f`) | Power | 7.736 | x0.5171 | 5.171 | 37 |
| `Sanatorium_Emissive_Orange` (`d52be16e9d5d8091`) | Power | 6.792 | x0.5889 | 5.889 | 165 (the two chandeliers and 157 floor spot lamps among them) |
| `Prison_Emission_blue` (`3a2454d56d07054f`) | Power | 6.226 | x0.6424 | 6.424 | 37 |
| `Plafond_lamp_02_source_E_red` (`6a24a59ea530fe5a`) | Power | 4.277 | x0.9353 | 5.340 (power 5.71) | 15 |

The rest of Iceberg stays as authored (next: `Searchlight_03_source_E` 2.925, `Sanatorium_Emissive_white` 2.83, `shopping_mall_ceiling_light_emission` 2.333). Released maps' strongest emissive
materials: Bay5 `02 - Default` (Standard) **4.128**, Equator and Bowl `Projector_portable_Albedo_emissive` (p0) **3.932**, Bowl/Equator `Material #37` 3.883, Air Pit `Airpit_emissive_02` 3.62 and
`Airport_Lamp_emissive_neon` (Standard) 3.277; only Bay5's 4.128 is above 4.0, and the lead's earlier figure (8.28 x 0.469 = 3.9 for `Material #37`) is this formula without a colour factor (that
material's colour peak is 1). Scratch scripts: `hdr_emission_census.py`, `emissive_census.py` (lead), `cap_census.py` and `shader_props.py` of this pass.

### Implementation

- **Descriptor and sidecar.** `ArenaMapDescriptor.EmissionCap` (float, constructor default 0 = off) mirrored by `emission_cap` in `tools/maps/<stem>.json` (absent = 0). Iceberg: `4.0f` / `4.0`; the four released maps
  carry neither (their sidecars are byte-identical to before). `validate_map_descriptors.py` checks the shape (finite, 0..100) and the equality (`emission_cap mismatch`); the Editor ignores the key (JsonUtility), so
  no NavMesh or bundle rebuild follows.
- **`EmissionCapModel.cs`** (Unity-free, linked into the tests): `Classify` (shader name + keyword + declared properties -> family), `ColorLevel`, `PowerLevel`, `DesiredScale` (cap / level, 1 when off, at or within 1e-5 of the cap,
  non-finite or 0), `EmissionEntry` (the captured originals and the scale written), `EmissionLedger<TMaterial>` (`TryAdd` refuses a known material, `Apply(surface, cap)`, `Restore` = `Apply(0)`) over `IEmissionSurface`.
  The cap is a ceiling, so it is idempotent by nature (re-capturing a capped value changes nothing), and the ledger always re-targets from the original: a live change of the cap, cap 0 and a second raid with the bundle
  still cached cannot compound. A property that another controller moved after the cap wrote it is left alone (counted as foreign) and is not restored over; a destroyed material is dropped.
- **`EmissionCapRepairs.cs`** (the glue, one repair per file): `Install()` binds `Lighting (<key>)/EmissionCap` for every map in `ArenaMapRegistry.All` on `Plugin.Instance.Settings`
  (developer-only like every tuning key: not in `IsPlayerOption`; default = the descriptor value, so Iceberg 4.0 and the others 0; range 0..100; live). A map whose cap is 0 schedules and scans nothing. Every owned scene's
  `sceneLoaded` (re)starts a 4 s `WaitForSecondsRealtime` coroutine (a later scene restarts it, so the pass runs 4 s after the LAST owned scene loaded: after ShaderRebind's 3 s sweep, behind the loading screen and
  long before `GameStatus.Started`; never in Update). The pass: `ReflectiveSpecularRepairs.Suspend(map)` (the specular cap's renderer-local clones are built from these very materials and are rebuilt from the capped inputs
  on dispose), then one scan of `GetComponentsInChildren<Renderer>(true)` over the map's loaded scenes with a shared `List<Material>` (`GetSharedMaterials`), each distinct material classified once, then `Apply`. Materials
  are changed IN PLACE (no clone, no `.material`): the ledger is the undo. `sceneUnloaded` of the Scripts scene writes the originals back and drops the state. Live key change: `Apply` over the ledger without a rescan
  (a first enable on a map that was off scans once). Logging: one Debug line per pass `[EmissionCap] map=... cap=... (reason): N emissive material(s), M capped (largest original X 'id' -> cap), W written, F left alone
  (changed elsewhere), G gone; R renderer(s) scanned in T ms`, one line at release, the per-material list only with `DeveloperOptions.Enabled`; Warning only if the pass cannot be scheduled, Error on a failed pass (after a
  restore attempt).
- **Cap value 4.0.** It is the level of the released maps' strongest lamps (4.13 and 3.93, accepted in play under the same 2.10/0.35 bloom) and about twice the bloom threshold, so a capped lamp still reads as lit; it takes
  the crystal lamps from 67.8 to 4.0 (an orange of (4.0, 1.03, 0)), the chandeliers from 6.8 to 4.0 and leaves every other Iceberg lamp alone. The number cannot be verified offline: the lead tunes it live with the key
  (suggested steps: 4.0 -> 3.0 -> 2.5 for the chandeliers if discs remain; 0 is the A/B control that brings the blow-out back).
- **Bloom.** `BloomTuning`: Iceberg's defaults are the donor pair 2.10/0.35 like Equator and Bay5 (the `IcebergThreshold/IcebergIntensity` constants are gone); `BloomDefaultsRevision` **2** for Iceberg
  (`BloomDefaultsModel.IcebergPairMoves`, `MigrateIcebergDefaults`): a saved pair that is exactly 4.0/0.2 (what revision 1 wrote; the lead's host config) moves once to 2.10/0.35 at startup, any other pair stays (-1/-1
  already means the camera's own 2.10/0.35; a single hand-tuned value protects the other). AirPit keeps its revision-1 pair migration (`MigratePairDefaults`) alone. Key names, AirPit's 1.2/0.25 and Equator's/Bay5's
  defaults are unchanged; the key descriptions and comments no longer send Iceberg to 4.0 or blame the camera for the chandeliers. Expected log on the first start of the host with the reset config:
  `[Bloom] arena_iceberg: config re-seeded to bloom defaults revision 2 (2 value(s) moved; threshold=2.10, intensity=0.35)` (Debug).

### Tests

New .NET: `EmissionCapModelTests` (classification table incl. stand-ins and leftovers, scale math, the crystal lamp and the chandelier, below-cap untouched, idempotence, live re-target from the original, cap 0 bit-exact
restore, no recapture, a second raid, six raids without a restore, foreign write, destroyed material, unusable values, the six-of-sixteen table), `BloomDefaultsModelTests`, `EmissionCapDescriptorTests` (sidecar mirror, only
Iceberg has a cap), `EmissionCapSourcePinTests` (one scan, nothing per frame, no material instantiation, scheduling, restore on unload, Suspend around read and write, Settings key with the descriptor default and not
public, Debug-level logging, install order, BloomTuning wiring). New Python: `tools/tests/test_iceberg_emission_cap.py` (17 tests: sidecar/descriptor pins, validator accept/reject/drift, the rule lists equal the C# model,
the Iceberg package has exactly the six over-cap materials with their levels, the released maps' maxima, EFT's shader facts). `validate_map_descriptors.py` gained `emission_cap_errors` and the `emission_cap` mirror check.

### Limits and what is not covered

- Not Unity-run: the glue (`EmissionCapRepairs.cs`) compiles and is pinned as text; everything it decides is in the tested model. First proof is the host raid below.
- Materials cloned by `IndoorFloorSmoothnessRepairs` (off on Iceberg) copy the value at their creation and are not re-targeted by a later live change; irrelevant for the default cap.
- The cap also dims every other renderer of a capped material (157 floor spot lamps share the chandeliers' material).
- Particle and decal materials with other shader families are not touched; none of the six materials is one.

### Host checks for the lead (after a full client Release build in the main checkout and a client-only deploy; close/restart EFT as usual)

1. First start with the reset config: Debug line `[Bloom] arena_iceberg: config re-seeded to bloom defaults revision 2 (2 value(s) moved; threshold=2.10, intensity=0.35)`; `uibridge_config` `Lighting (arena_iceberg)` BloomThreshold 2.1 /
   BloomIntensity 0.35, `BloomDefaultsRevision` 2, `EmissionCap` 4.0 (DeveloperMode on to see it).
2. Botless Iceberg raid (Bear spawn, as on 2026-10-30): about 4 s after the last scene loaded, before the raid starts: `[EmissionCap] map=arena_iceberg cap=4.00 (owned scenes loaded): ~37 emissive material(s), 6 capped (largest
   original 67.79 '367e4a61931e682b' -> 4.00), 6 written, 0 left alone (changed elsewhere), 0 gone; N renderer(s) scanned in T ms` (T should be a few ten ms; N about 22,700+ with the inactive LODs; with DeveloperMode the six
   `'<id>' (Color|Power) level A -> 4.00 (xS)` lines of the table above). No `[EmissionCap]` Warning/Error.
3. Look: the two chandeliers in the respawn hall (screen points (360,140) and (1310,140) from the Bear spawn at yaw 273, ~12 m) no longer a disc, still lit; the lobby crystal lamps (F10 them or teleport into the lobby) a bright
   orange, not a white-hot blob. If the chandeliers still glare, step the key live (`uibridge_config_set` `Lighting (arena_iceberg)/EmissionCap` 3.0, 2.5; expected line `... (setting changed)` with `W written` = the entries whose
   scale changed); `0` = A/B control: `cap off (setting changed): 6 material(s) restored to their originals` and the blow-out is back at 2.10/0.35. Persist the final value in the descriptor/sidecar (`EmissionCap`, `emission_cap`).
4. Leave the raid and start a second one on Iceberg without restarting EFT (the bundle stays cached): `released 37 material(s) (raid ended), restored 6 to their originals`, then the same capped line again with 6 written (no
   compounding; the first line of the second raid reads the same largest original 67.79).
5. Control on a released map (Equator or Bay5, one raid, plus the hideout once): no `[EmissionCap]` line at all, bloom unchanged (`[Bloom]` lines as before), no new Warning/Error; frame max unchanged.

### Gates (this worktree; the staging inputs and the map-description file exist only in the main checkout)

| Gate | Result |
|---|---|
| `dotnet build ArenaMapPort.Client -c Release -p:ArenaMapPortDllOnly=true` | C# compile clean (0 errors, 0 warnings; `maschine-ArenaMapPort.Client.dll` produced); the build then stops in the staging step `tools/extract/stage_spectators.py stage` (csproj line 193) because the worktree has no `work/extract/spectator-airpit/spectator.json.gz` (ignored data, main checkout only) |
| `dotnet build ArenaMapPort.Server -c Release` | compiles (the Shared descriptor change is in it); `DeployToDist` stops with "Missing original map descriptions" (`work/extract/map-descriptions` exists only in the main checkout), so the tests were run with `-p:BuildProjectReferences=false` against that DLL |
| `dotnet test ArenaMapPort.Server.Tests -c Release -p:BuildProjectReferences=false` | **2,830 passed, 0 failed, 0 skipped** (2,756 before this pass + 74 new) |
| `python -m unittest discover -s tools/tests -p "test_*.py"` (PowerShell, `ARENAMAPPORT_WORK_DIR=D:/ArenaMapPort/work`) | 2,736 tests, 135 skipped (the worktree has no `work/`), **2 failures, both environment-only**: `test_dll_versions.test_actual_candidate_and_stale_binary_control` (the worktree's `dist/` has no server DLL because `DeployToDist` stopped, see above) and `test_score_monitors.SidecarTests.test_the_sidecar_text_is_what_the_cli_writes` (the worktree checks `tools/maps/*.json` out with CRLF, `core.autocrlf=true`; the test wants the LF the generator writes; the file is untouched by this pass) |
| `python tools/validate/validate_map_descriptors.py` | OK, 14 descriptors, 14 catalog entries, release `All` membership |
| `python -m unittest tools/tests/test_iceberg_emission_cap.py` | 17 tests OK (with `ARENAMAPPORT_WORK_DIR`; the package, released-map and EFT-shader groups skip without their inputs) |

## 2026-10-31 - native sea water for Iceberg (the Bay5 harbor mechanism, generalised; offline, not host-checked)

Workstream of the Iceberg integration (worktree branch `worktree-wf_d144dcf2-1e2-2`, three code commits and this record on top of `bd39a41`). The lead's evidence stands: level256 holds two `EFT.Water.WaterContainer`s, `WaterForSSrv3` with one empty target
and `Sea` with the one target `Sea/Plane`; the generic `SeaPlaceholderRepairs` put `FX/SimpleWater4` on that plane and the host showed the dry sand bed of the canyon (y -22..-26) instead of the sea. This pass gives Iceberg the native deferred
water renderer of Bay5's harbor ([bay5-water.md](bay5-water.md)). Everything below is offline (Arena 0.6.1.0.48206 read-only, unit tests, a scratch MSBuild project); the look is the lead's host check (list at the end).

### What the source says (new facts)

- **Iceberg's sea is Bay5's sea.** The `Sea` container of `level256` carries settings and four textures that are identical to those of `level84` (`water.json` `settings` and `textures` compare equal, the four `.bin` files are hash-identical:
  `foam` 3dc315d5..., `normals` 57164451..., `normalsDetails` fce89782..., `rippleTexture` fb92af89...; 2,271,980 B), and so is the plane: world scale 599.68 x 690.19 on Unity's 10 x 10 built-in Plane (about 6.0 x 6.9 km, x -2272..3725,
  z -4596..2306, computed through the parent chain of `iceberg-native-v2` and `bay5-native-v5`), same x/z position (726.25, -1144.95); only y differs: Iceberg -18.9, Bay5 -8.65. A 6 x 7 km plane is therefore not new to the native renderer. The
  play area (x -60..120, z -129..-35) lies inside it. Both packages differ in identity only (`map_key`, scene `Arena_Sanatorium`, the path ids of the Arena build, the raw container hash).
- Exactly one root `Sea` in `Arena_Sanatorium` with exactly one child `Plane` (active, layer 4, id 23230): the client's `SingleOrDefault` root lookup and `Transform.Find` resolve `Sea/Plane` unambiguously.
- **No reflection probes on either map**: Iceberg's 35 `ReflectionProbe` components (`Arena_Sanatorium_Light`) are `skip_import`, Bay5 has none, so the water reflects the default environment the weather rig sets, as the harbor does.
- Nothing else in the native setup is Bay5's own: `WaterRendererv3` draws with `CommandBuffer.DrawMesh` (no frustum culling, so the plane's bounds do not matter), only `MainCamera`/`OpticCamera` draw it, the four-output G-buffer path (`DisableSnowMask(true)`) is the right one for
  Iceberg's Autumn (`RainController`'s summer, spring, autumn states set the same flag; a Winter season is the same open point as on Bay5: the runner sets it once, 4 s after the load), and the placeholder replacement/restore is generic
  (`_oldMaterials` is whatever `SeaPlaceholderRepairs` put there). The seven other `FX/SimpleWater4` surfaces of Iceberg (canyon `Water_Plane_01` at y -14.7 and -17.1, two fountains, the utility-room basin, a puddle) are not
  `WaterObject`s and keep their imported materials; the two canyon planes sit above the sea, so they draw over it.
- **Stage-2 check of the textures.** The shipped water textures are plaintext. The normals, the details and the foam are fragments of `sharedassets51.assets.resS` (207,867,856 B), which is a stage-2 key-source file of Equator; the ripple
  texture is inline in `sharedassets36.assets` (no key reads it). None of the three stream ranges touches a sampled block (block 65,536 B, 16 blocks at `k * (size - 65,536) // 15`): the nearest block is 4.77 MB (normals), 0.92 MB (details) and 0.71 MB (foam) away.
  `extract_water.py` now proves this on every export and refuses a texture on a sampled block or equal to a whole key-source file (the gate-sound trap). The Bay5 textures shipped since 0.2.x pass the same check, so nothing already published is affected.

### What changed (2 + 1 commits: `44d35e9`, `6631c86`, `996b7ac`)

- `44d35e9` **`tools/extract/extract_water.py`**: one table `MAPS` (`WaterMap`: key, stem, BuildSettings level, scene, container and surface path, census levels), `--map arena_bay5|arena_iceberg` (key or stem), `--output` defaults to `work/extract/<stem>-water`.
  Every fail-closed check stays; new: BuildSettings must still put the scene at its level index, the other level files of the map are scanned (Bay5 nine, Iceberg nine: 255, 257, 258, 259, 260, 375, 376, 377, 378, 0 containers found, no MonoBehaviour with an unreadable script; level377 has 2,303 slots whose script is simply missing, which the scan accepts) and any
  `WaterContainer` with a real target there stops the export, and the key-source guard above. `select_sea` is a function now: the empty `WaterForSSrv3` is accepted, a second real surface is refused (synthetic tests for both, plus no/two Seas, disabled/inactive
  Sea, zero or two surfaces). **Bay5's output is unchanged**: the export of the new code is byte-identical to the one the unchanged code writes from the same Arena (5 files, SHA-256; `water.json` `585f0060...`). Against the old `work/extract/bay5-water` (made from
  Arena 0.5.4) the four textures are hash-identical and `water.json` differs only in the Arena 0.6.1 path ids (`component_id` 100550 -> 100565, `game_object_id` 19568 -> 19571, `mesh_renderer_id` 63361 -> 63370, `mesh_filter_id` 81541 -> 81553) and
  `raw_sha256`; the client reads none of them. **Iceberg export written to `D:/ArenaMapPort/work/extract/iceberg-water`** (water.json 5,661 B + 4 textures; `WATER VERIFIED: arena_iceberg: 2 source containers, 1 surface, 4 textures, 2271980 native bytes, 9 other levels scanned (0 empty container(s))`).
- `6631c86` **client + descriptor**: `ArenaMapDescriptor.HasNativeWater` (Bay5 and Iceberg true; sidecar key `has_native_water` in `bay5.json` and `iceberg.json`, same commit; `validate_map_descriptors.native_water_errors` holds sidecar, descriptor and the new
  `NATIVE_WATER_MAP_KEYS` equal). `NativeWaterModel.cs` (Unity-free, linked into the tests) holds the switch rows, the package directory, the identity rule of `water.json` and the texture file name rule; `NativeWaterRepairs.cs` has no map literal left (a test strips the comments and
  greps for `Bay5`, `Iceberg`, both key names and `"arena_`). The runner takes its map from `ArenaMapRaid.MapForScriptsScene` of the Scripts scene that loaded. **Bay5**: section `Water (arena_bay5)`, key `NativeHarborWater`, text and default unchanged
  (a test pins the text), package `water/arena_bay5`. **Iceberg**: section `Water (arena_iceberg)` (the section that already carries `PuddleFootsteps`), key `NativeSeaWater`, default true, live, package `water/arena_iceberg`. The `[Water] enabled:` Debug line now also names the map,
  the plane's world y and its extent. `ArenaMapPortStageWater` stages both packages (ten explicit files) in Release **and DllOnly** builds with the same `Missing native water input ...; run tools/extract/extract_water.py --map <key>` Error (checked on a scratch project made
  of the csproj's own target text: all present -> the ten files land in `dist/.../water/<key>/`, one missing texture or json -> Error in both modes). The text of `Sea / EnableWaterPlaceholder` names both switches (its key, default and meaning are unchanged).
- `996b7ac` **release lists**: `validate_distribution.WATER_MAP_KEYS` = `NATIVE_WATER_MAP_KEYS` = (`arena_bay5`, `arena_iceberg`); `validate_water` also requires a package's `target.scene` and `hierarchy_path` to be the extractor's row of its own map (Bay5's package copied into
  Iceberg's folder is refused, and the other way round); the packer has a `water` rule only for those maps and REQUIRES `water.json` plus the four textures of them (it was optional; a water folder of Equator, AirPit or Bowl is now unclassified). `docs/dist-manifest.md`: dist 281 files,
  parts core 154, equator 24, bay5 30, airpit 22, bowl 26, **iceberg 25** (+5 water files, +2,277,641 B raw). `test_crowdless_maps.NotYetRulesTests` now pins water as present and overview/flares as still absent.

### Tests and gates (worktree, VM, Release)

| Gate | Result |
|---|---|
| `dotnet build ArenaMapPort.Client -c Release -p:ArenaMapPortDllOnly=true` | **compiles, 0 warnings, 0 errors** (`maschine-ArenaMapPort.Client.dll` built); the build then stops at the first staging target, `ArenaMapPortStageSpectator`: `work/extract/spectator-airpit/spectator.json.gz` exists only in the main checkout (environment-only, nothing of this change); the water target runs after it and was exercised in the scratch project |
| `dotnet test ArenaMapPort.Server.Tests -c Release` | **2,785 passed, 0 failed** (2,756 + 29 new in `NativeWaterTests`); the worktree needed a copy of the 1.8 KB `work/extract/map-descriptions/map-descriptions.json` in its own ignored `work/` for the server project's dist staging |
| `python -m unittest discover -s tools/tests -p "test_*.py"` | **2,766 tests, 1 failure, 135 skipped** (600 s, PowerShell, `ARENAMAPPORT_WORK_DIR=D:\ArenaMapPort\work`; the lead's 2026-10-30 run was 2,719 tests). The one failure is environment-only and untouched by this branch: `test_score_monitors.SidecarTests.test_the_sidecar_text_is_what_the_cli_writes` asserts that the tracked `tools/maps/*.monitors.json` have no CR, and this worktree's checkout (`core.autocrlf=true`) wrote them with CRLF (`git ls-files --eol`: `i/lf w/crlf`); it passes where the sidecars are LF. The skips are the real-data tests that need the main checkout's ignored `work/` (the worktree has none) plus the usual optional dependencies; the water tests that need the export ran through `ARENAMAPPORT_WORK_DIR` |
| `python tools/validate/validate_map_descriptors.py` | OK (14 descriptors, 14 catalog entries, release `All` membership; now also `has_native_water` against `HasNativeWater` and `NATIVE_WATER_MAP_KEYS`) |
| `validate_distribution.validate_water` on the real packages (Bay5 re-export + Iceberg, staged like the csproj does) | OK, 10 files; Bay5's package under Iceberg's key is refused |
| `test_water_extraction` with the real Arena and the real export | 37 tests, OK (`ARENAMAPPORT_WORK_DIR=D:\ArenaMapPort\work` makes the export-dependent tests run in a worktree; without it they skip) |

### For the lead: merge and build

1. Merge the branch; conflicts to expect are textual only (`ArenaMapDescriptor.cs` at the Bay5/Iceberg `UsesServerTime` lines, `docs/dist-manifest.md` head, the end of this file).
2. `D:/ArenaMapPort/work/extract/iceberg-water` already exists (written by this pass, ignored). Regenerate it only with a new directory name (the extractor refuses an existing one).
3. A client Release build (DllOnly is enough) stages `water/arena_iceberg` next to `water/arena_bay5`; `validate_distribution.py` then demands both. **Deploy with the full `deploy-to-spt.ps1 -Confirm` (not `-DllOnly`)**: the five new files (2.28 MB) travel with `dist/`; the new DLL needs the EFT restart.
4. Suggested edits of the lead's own files (not touched here): AGENTS.md "Iceberg promotion rules" (1) now says Iceberg is in none of the overview/flare/water lists: water is in (the sentence "Harbor water" in the Traps list can say "Bay5 and Iceberg"); the file counts there (256 -> 281 once Iceberg is staged; iceberg part 25);
   HANDOFF/INDEX entries.

### Host checks for the lead (GPU-2)

1. After the deploy and an EFT restart: `uibridge_status` (VM build = host disk = loaded), `uibridge_config` shows the new key `Water (arena_iceberg) / NativeSeaWater` = true, and Bay5's `Water (arena_bay5) / NativeHarborWater` still true.
2. **Iceberg raid, botless** (`gpu-run raid --map iceberg`). Debug lines expected, in this order, about 6 s after the Scripts scene loads (the `[Sea]` placeholder line first, at +2 s, then the native start at +4 s or when all seven scenes are loaded):
   `[Sea] 'Plane' (scene='Arena_Sanatorium') — assigned placeholder water material`
   `[Water] enabled: map=arena_iceberg target=Arena_Sanatorium/Sea/Plane textures=4 shader=Hidden/WaterForSSR renderer=<id> camera=DeferredShading worldY=-18.90 extent=5997x6902 m`
   and NO `[Water] initialization failed; preserving placeholder: ...` (Error). With DeveloperMode on, F10 (SceneInspector) adds `[Water] probe enabled=True buffers=1 textures=4 target=Plane forceOff=True targets=1 drawReady=True camera=DeferredShading`.
   On leaving the raid: `[Water] stopped; released buffers=1; restored original renderer`.
3. **Looks** (the reason for the pass): from the south promenade `(7, 4.4, -126.3)` looking south and down, and from the bridge near `(-43, 3.2, -89)` into the canyon: a lit, moving water surface (waves, foam at the walls and rocks, dark-green deep colour and lighter shallows
   from the source settings) at y -18.9 over the sand bed, instead of dry sand. Compare the same view with `Water (arena_iceberg)/NativeSeaWater` off (live): the old placeholder, i.e. the dry bed. Day and night (the water should darken with the world light); rain once.
   The two canyon planes at y -14.7/-17.1 and the fountains must look as in the last raid (they are forward-rendered SimpleWater4 above the sea).
4. If the sea shows but looks wrong: first the reflection (no probes are imported on either map, so it is the default environment), then the depth fade against the canyon floor (`_depthFade`/`_borderFade` are the source values, 2.0), then the snow-mask state (`DisableSnowMask`, only if the season were Winter).
5. **Bay5 once** (`gpu-run raid --map bay5`, botless): the harbor from `(146.5, -3.4, 20.6)` yaw 90 pitch 10 as in bay5-water.md: dark native water by night, `[Water] enabled: map=arena_bay5 target=Arena_Bay5_Main/Sea/Plane textures=4 ... worldY=-8.65 extent=5997x6902 m`, no initialization failure, live off -> the white placeholder
   and on -> native again. Then Iceberg again in the same EFT process (cross-map: one runner at a time, `buffers=1`).
6. Frame time on Iceberg with the sea (per frame the native renderer blits G-buffer 0 and 2 and draws the plane twice, a G-buffer pass and an alpha-restore pass, exactly as on Bay5): `uibridge_status` `max` frame time against the 61.2 ms of the last Iceberg raid.

## 2026-10-31 night - user feedback (bloom, water), the crystal lamps, emission cap and native sea host-verified on GPU-2

- **User feedback** (German, 2026-10-31): the bloom looks odd, two lights stay completely over-exposed until the bloom is turned down to about
  0.001; is there water on the map that is missing? His MASCHINE11 log (00:06-00:30) shows the bloom keys swept from 4.0/0.2 down to 2.10/0.02.
- **The two lights** (the user stood in front of them on GPU-2 and pressed F10, 00:57:44 and 00:57:54): `SBG_Arena_Sanatorium_Light/OO/INDOORS/
  LOBBY/Crystal_Lamp_1` and `Crystal_Lamp_1 (1)` (camera at (-32.23, 4.23, -89.26) and (-32.21, 4.24, -73.96)), not the chandeliers of the
  2026-10-30 fix. Their LOD0 submesh 2 binds `Emission_Yellow` (package id 367e4a61931e682b, `Standard`, `_EMISSION`, `_EmissionColor`
  (67.794, 17.392, 0.0)). A census of every accepted package (scratch `hdr_emission_census.py`) finds no other emission colour channel above
  4.13 (Bay5 `02 - Default`). The chandelier material `Sanatorium_Emissive_Orange` (p0 emissive, power 10, visibility 1.0) is the second case.
- **Missing water**: Iceberg's level256 has two `EFT.Water.WaterContainer`: `WaterForSSrv3` with an empty target (renders nothing) and `Sea`
  driving `Sea/Plane` (layer 4, no material, about 6.0 x 6.9 km at y -18.9) - the same container and plane as Bay5's harbor. The generic
  SimpleWater4 placeholder (`[Sea] 'Plane' ... assigned placeholder water material`) is effectively invisible: from the south promenade
  (7, 4.4, -126.3) the canyon showed a dry sand bed at y -22..-26 (coverage raster: scratch `sea_holes.py`). The seven other water surfaces
  (canyon planes at -14.7/-17.1, fountains, basin, puddle) use EFT's FX/SimpleWater4 and are present.
- **Built** (workflow `wf_d144dcf2-1e2`, two worktrees and two reviews, merged as `308bbd6` and `712b999`, review fixes `4a46a54`, `f431c9d`):
  the per-map emission cap (`EmissionCapRepairs` / `EmissionCapModel`, descriptor `EmissionCap` 4.0 for Iceberg, developer key
  `Lighting (<key>)/EmissionCap`), Iceberg's bloom back to 2.10/0.35 (`BloomDefaultsRevision` 2 moves exactly a saved 4.0/0.2 pair), and native
  sea water for Iceberg (`extract_water.py --map arena_iceberg`, `work/extract/iceberg-water`, descriptor `HasNativeWater`, key
  `Water (arena_iceberg)/NativeSeaWater`; Bay5's export byte-identical). A failed cap pass now restores the originals inside the
  ReflectiveSpecular suspend scope (review). Gates: .NET 2,859 passed; Python 2,783 with one stale constructor pin fixed (`f431c9d`, 31 tests of
  the two modules OK afterwards); `DISTRIBUTION_OK 5 maps 15 bundles`; `validate_map_descriptors.py` OK; dist 281 files.
- **Host** (GPU-2, full deploy 281 files, 5 new and 2 overwritten; 2,463 files identical after 137.6 s; EFT started 02:14:59):
  - `[Bloom] arena_iceberg: config re-seeded to bloom defaults revision 2 (2 value(s) moved; threshold=2.10, intensity=0.35)`.
  - Iceberg raid 02:17 (botless, 12:00, 0 warnings): `[Water] enabled: map=arena_iceberg target=Arena_Sanatorium/Sea/Plane textures=4
    shader=Hidden/WaterForSSR ... worldY=-18.90 extent=5997x6902 m`; `[EmissionCap] map=arena_iceberg cap=4.00 (owned scenes loaded): 37
    emissive material(s), 6 capped (largest original 67.79 '367e4a61931e682b' -> 4.00), 6 written ...; 22693 renderer(s) scanned in 20 ms`
    (Power 6.79/7.74/6.23/10.00/4.28 -> 4.00, Color 67.79 -> 4.00).
  - Looks at bloom 2.10/0.35: the lobby crystal lamp (from the user's F10 position) shows its glass column and glowing crystals, nothing
    blown out; the respawn-hall chandeliers show their candles with a soft glow, no disc. From the south promenade the canyon is a dark green,
    moving sea with cliff reflections and the shallow sand showing through; `NativeSeaWater` off live (`[Water] stopped; released buffers=2;
    restored original renderer`) brings back the dry sand bed, on again re-enables it. 119.2 fps over 20 s (max 76.5 ms at the re-enable).
  - Raid end: `[EmissionCap] map=arena_iceberg released 37 material(s) (raid ended), restored 6 to their originals`.
  - Bay5 raid 02:18 (botless): `[Water] enabled: map=arena_bay5 target=Arena_Bay5_Main/Sea/Plane ... worldY=-8.65`, the harbor renders as
    before; no `[EmissionCap]` line (cap 0). Hideout opened and closed (scenario, no ArenaMapPort error).
- **Open**: the user's look and listen at bloom 2.10/0.35 (his shared config was reset to the defaults); the sea at night, in rain and from
  the bridge; the cap value 4.0 is a first choice, tunable live with `Lighting (arena_iceberg)/EmissionCap`.

## 2026-11-01 - Emission floor, the ArenaMode marker, Arena 0.6.2

- **User feedback** (German, 2026-11-01): the water looks good; the bloom is not right yet, "every light source seems to need its own
  slider, you never get them all to one level"; the ICEBERG marker must move right so that ArenaMode's panel no longer hides it; Arena was
  updated to 0.6.2. The user's MASCHINE11 logs of the morning (sessions 11:12 and 11:25) show the search: threshold swept 0.04-4.0,
  intensity 0.24-0.52, `EmissionCap` 0.05-1.46; the shared config ended at threshold 0.0385, intensity 0.2373, cap 1.4585.
- **Why one slider could not work.** UltimateBloom runs in Threshold mode on the port's camera (the donor rig, 2.10/0.35): only the part of
  a pixel above the threshold glows. Iceberg's 37 emissive materials (census scratch `emissive_census.py`) range from 0.58 (Armstrong wall
  lamps, 64 renderers), 0.60 (senator fluorescents, 55), 0.89 (street lights, 54) and 2.33-2.83 (mall ceiling lights 151, balcony lamps 248)
  to the six capped at 4.0. With the threshold above a lamp it never glows; lowered below it, the capped lamps and lit walls wash out.
- **Arena's own cameras** (read-only, UnityPy, scratch `bloom_decode.py`, decoding UltimateBloom with EFT 0.16.9.5's field order, checked on EFT's own
  resources): Arena's Iceberg LevelSettings names no camera prefab of its own (its first pointer is the PrismPreset `sharedassets255.assets`
  path 5); the UltimateBloom cameras in Arena's `resources.assets` are `Cam2_ARENA_FIX` and `Cam_Spectator` (threshold 1.918, intensity 0.07,
  Threshold mode, HDR on), `Cam_Spectator3` (1.918 / 0.346) and `BaseOpticCamera`; `Cam2_ARENA_3` has no UltimateBloom at all. Which one
  Arena's player uses is IL2CPP code and was not read. `Cam2_ARENA_FIX` with the same values is also in EFT's own resources. Arena's bloom is
  Threshold mode as well, so the same lamps stay dark there; the port keeps the donor pair.
- **Built: the emission floor.** `EmissionCapModel.DesiredScale(level, floor, cap, liftable)`: a lamp whose original level lies between
  `LiftMinimum` 0.25 and the floor is scaled up onto the floor (hue kept, `_EmissionColor` RGB or `_EmissionPower`, the same ledger, written
  back at raid end), never a level below 0.25 (glass tints 0.06, dark spa bars 0.07, the 0.20 ceiling-light variant) and never an animated
  emissive shader (`lab_glow` on the projection screen); a floor above the cap counts as the cap. Descriptor `EmissionFloor` (Iceberg 3.0,
  the released maps 0 = off), sidecar `emission_floor`, `validate_map_descriptors.py` mirrors it, developer key
  `Lighting (<key>)/EmissionFloor` (live). At floor 3 / cap 4: 6 capped, 23 lifted, 8 untouched of the 37.
- **Built: the marker.** ArenaMode's mode panel spans x 56..416 at the 1080-unit reference; on GPU-2 (1920x1080) the ICEBERG toggle at
  Arena's measured `IconX` 110 spanned x 349..502 (ArenaMode's own report: only "BERG" visible). The toggle's left edge moves 0.79 px per
  anchor unit, so Iceberg is displayed at `IconX` 215 (`IconY` 608 unchanged): `tools/maps/selection/markers.json` keeps the measurement and
  carries a `display` block with reason and decision, the descriptor, sidecar and base.json say 215, `SelectionMapPositionTests` checks the
  shown anchor against catalog, sidecar and database, the measurement against the screenshot, and the 12 px clearance.
- **Gates**: client Release (DllOnly) 0 warnings; .NET 2,887 passed; `test_iceberg_bake_inputs` + `test_iceberg_emission_cap` +
  `test_gate_sounds` 128 OK against Arena 0.6.2; `validate_map_descriptors.py` OK; `DISTRIBUTION_OK 5 maps 15 bundles 5 sidecars encrypted`.
- **Host** (GPU-2; deploy 283 files, 3 overwritten: both DLLs and Iceberg's base.json; `deploy-wait --server --game`, identical after 78.3 s,
  build 16/16 identical; EFT started 12:03:46):
  - ArenaMode location screen: `toggle "ICEBERG" click 508,315 [431,298 154x33]` (before: `[349,298 153x33]`); the still shows the whole label
    beside the panel.
  - Iceberg raid 12:04 (botless, 21:30, clock stopped, clear; the shared config's 0.0385/0.2373/1.4585 active): `[EmissionCap]
    map=arena_iceberg cap=1.46 floor=3.00 (owned scenes loaded): 37 emissive material(s), 15 capped (largest original 67.79 '367e4a61931e682b'
    -> 1.46), 14 lifted (smallest original 0.29 '37f74d666c37579d'), 29 written, ...; 22693 renderer(s) scanned in 23 ms` (the floor clamped to
    the user's cap). With cap 4 / floor 3 set live (`persist=false`): `6 capped (... -> 4.00), 23 lifted (smallest original 0.29
    '37f74d666c37579d'), 23 written ... in 0 ms`; floor 0 restores the 23, floor 3 lifts them again.
  - Stills at the default bloom 2.10/0.35 (north corridor wall lamp, hotel core with the red bunker lamp, street): without the floor the wall
    lamp is a plain white strip with no glow, with it a soft halo like the capped red lamp's; the street lights gain a small halo, no wash-out.
    Arena's 1.918/0.07 gives a barely visible glow; 1.918/0.20 sits between. 120.0 fps over 10 s (max 11.7 ms); 0 ArenaMapPort warnings.
    Hideout opened and closed (scenarios `hideout-open`, `hideout-back`). The user's saved values were not touched (`persist=false`).
- **Arena 0.6.2**: keys, file diff and tests in [arena-update-20261101.md](arena-update-20261101.md): the maps are unchanged, 0.4.0 and Iceberg
  open on 0.6.2, no re-extraction.
- **Open**: the user's look at floor 3 / cap 4 / bloom 2.10/0.35 (the shared config still holds the morning's test values, see HANDOFF.md);
  whether the floor should also come to the released maps (their own lamps are not in this census).

## 2026-11-07 - Emission defaults set by the user: cap 4.8, floor 1.9

- **User decision** (German, 2026-11-07): "vorher noch die defaults fuer Iceberg anpassen: EmissionCap = 4.8, EmissionFloor = 1.9". The user tuned both by eye on their own PC;
  the 4.0 (2026-10-31) and 3.0 (2026-11-01) of the sections above were the agent's start values. Neither new value is backed by a measurement.
- **Files.** `tools/maps/iceberg.json` (`emission_cap` 4.8, `emission_floor` 1.9), the Iceberg entry of `ArenaMapPort.Shared/ArenaMapDescriptor.cs` (`EmissionCap: 4.8f`, `EmissionFloor: 1.9f`, comments
  keep the old reasoning and say who set the values), the two key descriptions in `ArenaMapPort.Client/EmissionCapRepairs.cs` ("Iceberg 4.8", "Iceberg 1.9"), a comment in `BloomTuning.cs`,
  `tools/tests/test_iceberg_emission_cap.py` (constants, pins, and the census tests: five materials above the cap, 15 lifted, the 4.28 lamp added to the "left alone" list, no released-map material above 4.8),
  `ArenaMapPort.Server.Tests/EmissionCapDescriptorTests.cs` (1.9f, 4.8f), AGENTS.md Traps "Emission cap", HANDOFF.md.
- **No re-seed.** The keys `Lighting (arena_iceberg)/EmissionCap` and `EmissionFloor` have never been in a release: Iceberg is unreleased and is not in `ArenaMapRegistry.All` of 0.4.0, so no player has them.
  Changing the defaults therefore needs no `DefaultsRevision` step; a value saved in a developer's .cfg stays as BepInEx keeps it (the user's own .cfg may hold other values and wins over the default).
- **New banding, computed from the package census (`work/extract/iceberg-native-v2`, the 37 emissive materials, the same rule as the sections above), not measured on a host.**
  At 4.8 / 1.9: **5 capped** (level above 4.8), **15 lifted** (0.25 <= level < 1.9, no animated shader), **17 untouched**. At 4.0 / 3.0 it was 6 / 23 / 8.
  - Capped, now onto 4.8 instead of 4.0: Emission_Yellow (67.79), Prison_Emission_red (10.0), Prison_Emission_passive (7.74), Sanatorium_Emissive_Orange (6.79), Prison_Emission_blue (6.23).
  - Capped to untouched: Plafond_lamp_02_source_E_red (4.28, 15 renderers) is under 4.8 and keeps its authored level.
  - Lifted to untouched (their level lies above 1.9, so they keep the authored level instead of being lifted onto 3.0): Searchlight_03_source_E (2.93), Sanatorium_Emissive_white (2.83, 248 renderers),
    shopping_mall_ceiling_light_emission (2.33, 151), Spot_lamp_01_source_E (2.33), Floor_lamp_01-03_source_E (2.31), Preset_refill_container_emission_green (2.12), armstrong_light_emissive (2.00),
    AreaLightSource (1.92). These eight now sit above the new floor by their own level; they were lifted before and are not any more.
  - Still lifted, now onto 1.9 instead of 3.0 (15): Preset_refill_container_emission_white (1.74), chemlight_d (1.44), projector_emission (1.32), Plafond_lamp_02_E (1.13), Track_lamp_01-03_E_black (1.00),
    Chandlier_03_04_emissive2 (1.00), Streetlight_lamp_01_source_E (0.89, 81), Armstrong_lamp_03_B_source_E (0.80), 04_emision (0.71), Fluorescent_lamp_01_source_E (0.70), Plafond_lamp_02_E_red (0.65),
    senator_light_emissive (0.60, 55), Armstrong_lamp_03_A_source_E (0.60, 160), Train_Emissive_White (0.44), Lift_emissive_draft (0.29). The wall lamps now rise to 1.9 where they rose to 3.0 before.
  - Untouched as before (8): lab_glow (animated screen), Sanatorium_Metal_ceiling_light (0.20), Airpit_lamp_emissive_grey, Glass_broken_new, Sanatorium_Glass_broken_new, Split_system_diffuse,
    Concrete_Clean, tile_floor_diffuse.
  - The band is now [1.9, 4.8] for the lifted and capped lamps, with a gap from 1.9 to 4.8 where the authored levels stand (2.0 to 4.28).
- **Host check pending.** The lead's next Iceberg raid should log `[EmissionCap] map=arena_iceberg cap=4.80 floor=1.90 ...` with the measured counts (the computed ones are 5 capped, 15 lifted);
  the user's by-eye judgement is the acceptance, the counts only confirm the numbers reached the host.

### Host evidence for 4.8 / 1.9 (GPU-2, 2026-11-07, 16:51-16:53; measured before the default change was built)

The host's own `.cfg` (mirrored from the user's PC by Syncthing) already held `Lighting (arena_iceberg) › EmissionCap = 4.8` and `EmissionFloor = 1.9` as SAVED
values (`uibridge_config` showed them against the then defaults 4 and 3), so a botless PMC raid on Iceberg with the `e3e4290` build ran the pass at exactly the new
pair: `16:52:15.699 [EmissionCap] map=arena_iceberg cap=4.80 floor=1.90 (owned scenes loaded): 37 emissive material(s), 5 capped (largest original 67.79
'367e4a61931e682b' -> 4.80), 15 lifted (smallest original 0.29 '37f74d666c37579d'), 20 written, 0 left alone (changed elsewhere), 0 gone; 22693 renderer(s)
scanned in 20 ms`. Capped: 67.79, 10.00, 7.74, 6.79 and 6.23 (`x0.071` to `x0.771`); `Plafond_lamp_02_source_E_red` at 4.28 is below the cap and untouched.
Lifted onto 1.90: fifteen materials from 0.29 to 1.74 (`x6.616` down to `x1.091`), among them `'d11e1839e73fc3d2' 1.13`, `'1d7e832d8cd5ee1d' 1.44`,
`'01c2e30a5bef1425' 1.74`; the 17 others (between 1.9 and 4.8, the screens, the tints below 0.25) are left as authored. Against floor 3 / cap 4 (6 capped,
23 lifted, 8 untouched) that is one capped lamp fewer and eight lifted lamps fewer. 0 ArenaMapPort warnings; `SpectatorCrowd.ReactionKinds = 0`,
`IsAvailable = false`, `SpectatorReactionBeds.Current = null`, `SpectatorCrowdBed.Current = null` (no crowd, no murmur, no roar layer on Iceberg, by construction).
The measured line matches the counts computed from the package census above (5 capped, 15 lifted, 17 untouched). The default itself was checked after the rebuild: with the
client DLL of this change deployed (`-DllOnly`, EFT restarted at 17:17, 16/16 plugins identical), `uibridge_config changed=true` lists no entry of `Lighting (arena_iceberg)` besides the
hidden `BloomDefaultsRevision`: the saved 4.8 and 1.9 no longer differ from the defaults. A raid with that build was not run yet (the host was taken by another session at 17:18); the pass is
the same code with the same values, so the 16:52 line stands as the measured banding.

## 2026-11-07 - 0.5.0 host run: gate hold, extraction, bots (GPU-2, 18:32-18:37)

With the 0.5.0 build (`a18ecd7`), two plain PMC raids: (1) bots None with `Spawns/SpawnGateHold = true` and `SpawnGateHoldSeconds = 20` (`persist=false`): the
spawn fell into the WEST pocket (-39.8, 3.2, -91.2), `[SpawnGates] navmesh installed: 11 carver(s) ...`, the probe found every probed gate sealed, and 20.0 s after the
raid timer the release drove all 11 parts to the open pose in 2.5 s (`drive frames: 298 ... max 20.4 ms`) with four gate sounds playing (`arena_san_gates_blue` x3,
`arena_equator_old_slide_doors`; the two east `arena_san_gates_red` rows beyond 30 m); `uibridge_teleport` to `exfil:pmc:Iceberg West Gate` ended the raid as an
extraction ("EARLY EXTRACTION", "Run Through", 00:00:31). (2) bots Medium: two PMC USEC and one Scav spawned within 45 m, moved on the NavMesh (the Scav 19.3 m in a
20 s observation), SAIN combat layers, one bot killed another; 119.9 fps, max 12.7 ms. `[EmissionCap] ... cap=4.80 floor=1.90 ... 5 capped ... 15 lifted ... 23 ms` in
both. 0 warnings. Record: [release-preparation-20261107.md](release-preparation-20261107.md). Still not seen: the east pocket as the player's side, a long bot raid,
the olive tree and the rocks by eye, the sounds by ear.
