# Plan: Iceberg — Fifth Release Map at AirPit Quality

Source: four read-only surveys run 2026-10-30 (spike state and source delta; gameplay objects, modes and navigation;
environment, rendering, audio and identity; pipeline gap against the Bowl precedent), a synthesis and a critic pass. The
probes used the repo's own parsers read-only on the live Arena install (0.6.1.0.48206, the VM's build) and on
`work/extract/iceberg-native-v1`. Evidence is quoted as file:line, counts or object paths; inferences are marked
**(inferred)**. Status lives in [HANDOFF.md](../HANDOFF.md) and the progress log
[docs/progress/iceberg-release.md](progress/iceberg-release.md) (survey record, dated work, host evidence); the 0.5.4 to 0.6.1
delta is [docs/progress/iceberg-source-061.md](progress/iceberg-source-061.md). This file is the plan, not the record. Work
happens on the branch `feature/iceberg`. The user's request of 2026-10-30, "dann würde ich gerne Iceberg als nächste map
angehen", is a go without further constraints, as Bowl's "so wie du es am sinnvollsten erachtest" was: the decisions below
are the agent's unless marked **(user)**.

Iceberg is Arena's `Arena_Sanatorium` (preset `arena_iceberg_preset.bundle`, ServerName `Arena_Iceberg`, map key
`arena_iceberg`, MongoId `2e2d6dadf4ebb29e53a69aae`): a resort hotel on a canyon with a street, a yard, a pool hall and a
lobby. Its preset offers BlastGang, CheckPoint and OverRun (OverRun reuses CheckPoint's preset); there is no TeamFight, TDM or
ShootOut set, no spectator crowd and no official map description in Arena's locales.

Iceberg before 2026-10-30: the September spike (package `work/extract/iceberg`, Arena 0.5.4, no native features; bundles
`work/build/iceberg`, scenes 1,124,068,269 B, a renderer-union bake of 362,998 vertices over about 1.2 x 1.1 km because the
sea `Plane` and `Lighthouse_Mountain_01` sit far outside the play area), `db/arena_iceberg/base.json` a placeholder
("Arena: Iceberg (spike)", `BotMax 0`, donor description), registered only in `ArenaMapRegistry.Catalog`
(`ArenaMapDescriptor.cs:1395-1410`), never raided. Arena 0.6.0 reworked the CheckPoint layout (barricades, spawns, border
cubes) and moved the CheckPoint indices by +9 (366-369 to 375-378).

## 0. Decisions (agent, 2026-10-30)

- **Source = the VM's Arena 0.6.1.0.48206**, packages `work/extract/iceberg-native-v<N>`: v1 = native features without
  ambient audio (extracted and validated 2026-10-30), v2 = v1 + ambient audio. The spike package is never an `--output` again;
  `work/build/iceberg` is archived as `work/build/iceberg-spike-20260915` before the first Unity run. MASCHINE11 runs Arena
  0.6.2.0.48473 now (survey S4): Iceberg's four key-source files are equal there by size and sampled content, while level256/258/375
  and sharedassets376/377 differ with unchanged object counts. The stage-2 wrap is verified with `verify-keys --arena` against
  every build at hand (VM 0.6.1, MASCHINE11 0.6.2); no 0.6.0 install is left to check against.
- **Mode = CheckPoint alone.** It is the only mode with an AI scene and a NavMesh, has two initial pockets of 10 markers, three
  capture points (A, B, C) that match ArenaMode's Domination points, a stage graph for respawns and three resupply crates.
  BlastGang (372-374: 9/7 markers, different cover on about 2,000 of 3,609 objects, no AI scene) is not imported and gets no
  plan record (Bowl's plan lists no other mode either, and a record would change the plan digest for nothing).
- **Time and weather = `UsesServerTime`**, as Bay5, AirPit and Bowl: the imported scenes carry no Directional light, sky, fog or
  lightmap; Arena's sun (a fixed dusk at about 9 degrees elevation) lives in the excluded Scripts scene. The WeatherRig donor
  row is Reserve like the other maps; the zero level is a host-tuned start value. A fixed dusk clock like Arena's art is a look
  choice for later **(user)**.
- **No snow and no winter work.** Sanatorium has no Winter scenes and no `SeasonObject`; only the ambient audio is season-aware.
- **Grass is not ported** (GPUInstancer, `ArenaGrassPrefabs_Sanatorium.pcl`), as on Bowl. Trees (1,145 `TreeWind`, 272 impostor
  data objects, 271 `TreeInteractive`), billboards (30, all disabled) and particles (42) stay as the extractor leaves them.
- **Borders = keep the 365 layer-29 `LevelBorder` cubes** of `Arena_Sanatorium_ChP_LevelBorders/LevelBorders` (level376), Bowl's
  policy: `keep_level_borders: true` and layer 29 in `navmesh_bake_layers` (`ArenaPaths.cs:100-102`), i.e. `[9, 11, 13, 18, 29,
  30]` (Arena's own surface mask 0x40042A00 plus 29). The 366th layer-29 object is a solid `KillTrigger` box under the play area
  (y about -11..-6); the floor of the bake bounds keeps it out of the mesh. The `ExcludeLevelBorders` polygon is the coarse
  culling outline (cube centres a median 17 m inside it), so `authored_borders` is not used.
- **Bake bounds** from the border cubes' AABB (x -60.1..119.5, z -128.9..-34.9) plus a margin, y from just below the lowest
  floor (Arena's main component spans y -1.8..6.15) to above the hotel's upper walkways; the oracle on the live package decides
  the final box. The renderer-union box is unusable.
- **Team starts = the 20 initial markers** (Bear 5-14 in `SPAWN_BOUND_FIGHTER`, east pocket x 89..98; Usec 5-14 in
  `SPAWN_BOUND_DEFENCE`, west pocket x -44.5..-37). Sides are named by stem and bound, not by mean z. The 50 stage markers stay
  live in the scene (respawn points for ArenaMode) and are carried with team, stage and linked capture point in the modes
  sidecar; spawn-placement checks cover all 70, including the six Bear stage markers that lie off Arena's mesh.
- **Gates.** `opened_barriers` = `DOORS/GATES_A` (two sliding gates, west side of the Bear pocket) and `DOORS/GATES_B` (three
  sliding gates and the rotating `Sanatorium_CheckPoint_Gate`, east side of the Usec pocket); `start_gates` = both;
  `held_gates` = none. Door poses come from the 11 DotAnimators (scan root `DOORS`, depth 3, rows told apart by `at`); drives
  2.0-5.0 s. The two `Sanatorium_Electric_engine` props stay standing.
- **Gate sounds** = the six `EntitySoundPlayer`s under `ARENA_SANATORIUM_GATES` (level259). `Sliding_door_2` needs a per-map
  join margin (0.33 m to its gate, 3.83 m to the next; the global `JOIN_MARGIN_M` is 4.0).
- **Score monitors**: six `ArenaMonitorHolder`s, four with type word 0 and two with 13. Word 13 stays dark (no known face) until
  its meaning is known; the four word-0 holders get the timer face.
- **No spectator crowd and no murmur bed**: Iceberg has no crowd objects. The packer, the distribution validator and the client
  staging get an explicit per-map crowdless rule instead of a missing-file failure. A port-only crowd would be an addition
  **(user)**.
- **Description = the port's own words.** Arena's en/ge/ru locales have no Iceberg name or description row (only
  `EArenaBackgroundType/Iceberg`). `ArenaMapDescriptions.Load` (`ArenaMapPort.Server/ArenaMapDescriptions.cs:21,32`) discards the
  whole official pack unless its keys equal `All`; it changes to "the pack covers the released maps that have an official text",
  so the four released maps keep theirs and Iceberg uses the descriptor's `LocaleDescription`.
- **Identity art**: banner `Matchmaker_Banners_Iceberg` (resources.assets path 1482, 769 x 460), loading screen
  `AllScreens_Loadingscreen-Iceberg` (path 1183, 1920 x 1080). Whether `Arena_MainMenu_Paralax_Resort-1..4` are Iceberg's
  parallax images is settled by looking at them before the loading-screen row is written. The selection marker (110, 608) exists.
- **Ambient audio** (package v2): 104 players (52 ambient, 30 loop, 22 season), four groups including the new
  `LampsAmbientInGroup`, 28 banks, 237 clips (about 172 MB of WAV). The exporter accepts `minDistance` 0 (21 neon-lamp and
  projector loops) under a reviewed rule.
- **Spatial acoustics are required** (the client staging, the packer and the wiring tests demand them for every released map;
  Bowl's promotion carried them). The 10 mirrored `AudioTriggerArea` volumes and the 533 of 17,751 baked routes that fail the
  strict checks are fixed or reviewed before the promotion; a per-map exemption is the fallback only if the route data cannot be
  explained, and it is then written as an explicit rule in every place that demands the files.
- **Shaders**: `Cloth/ClothShader` (+ backface), `ANGRYMESH/PBR Rocks/...` and `Custom/Billboard_FogSheet_Simple` resolve in
  process from Customs' shared assets (AirPit host evidence). Unproven: `MK4/Rock Simple` (1 material, same Customs file as the
  ANGRYMESH shader, so likely loaded) and `Nature/SpeedTree8` (4 materials, 16 renderers: the CAFE olive tree; it lives in
  Streets' `City_Scripts` shared assets and deliberately has no stand-in). Both are checked on the first host raid; the olive
  tree is then hidden or given a reviewed stand-in, with evidence.
- **Release** number and form **(user)**: the next release would be 0.5.0 with five maps; the single ZIP grows by roughly
  1.1-1.3 GB to about 5 GB. Until the user decides, nothing is tagged, uploaded or pushed for Iceberg.
- **Unchanged**: no loot, no scav raids, encrypted bundles in `dist/` and never in git, content keys never committed or logged
  (`work/release-keys` backed up off the VM after the first Iceberg stage), no switch for the Arena check, deploy only through
  `tools/deploy-to-spt.ps1`, ArenaMode read-only, the released maps' dist hashes untouched.

## 1. Ordered work items

Phase A (package) -> Phase B (a playable raid) -> Phase C (native features and polish) -> Phase D (release). U = Unity build
(the spike took 13 min; Bowl needed four U1 attempts for importer-policy fixes, so budget several), H = host raid.

### Phase A - Package

1. **Plan roles** (done, `817eed0`): scripts 255, sound 259, ai 377, culling 378; the reviewed ballistic pair Gravel/Concrete
   (eight cement bags) and the lamp-glass hit role (eleven Glass colliders with `EHitType.Lamp`, network id 0).
2. **Package v1** (done): `work/extract/iceberg-native-v1`, 49,861 objects, 5,370 meshes, 823 materials, 1,407 textures, 0
   errors; `refresh_native_volumes.py` (no Tex3D); `validate_extract.py` OK with both reproducibility runs. Source note
   `iceberg-source-061.md`.
3. **Native contract** (done, `0f72aed`, `12e1151`): ballistics 6,077/0/801/11/337/61, decals 215, culling lights 1,126,
   volumetric 276, flicker 7, modifiers 228, rain cover 23, `audio: null`.
4. **Housekeeping**: archive `work/build/iceberg` to `work/build/iceberg-spike-20260915`; `build_remaining.ps1` refuses
   Iceberg like Bowl and drops it from its defaults.
5. **Ambient audio and package v2**: reviewed `minDistance` 0 rule in `extract_bay5_audio.parse_player` (+ the Unity importer
   and the validator), `LampsAmbientInGroup` in `Bay5AudioImporter.GroupMixers`, `AUDIO_EXPORTERS["arena_iceberg"]`, the
   contract's audio block, re-extract as v2, validate. Precedent: Bowl `9c6bc17`, AirPit v6.
6. **Stage-2 key-source rows**: the six Iceberg scenes in `tools/bundle/key-sources.json` (256, 258, 375, 376 with their
   `.resS`, 257 and 260 empty; 560,470,032 B) with `test_bundle_crypt_tool.py` and `BundleCryptoTests.cs`. The first stage (key
   creation) runs only after a U1 bundle passed every validator.

### Phase B - A playable raid

7. **Mode objects and spawn sidecars**: `extract_mode_objects.py` learns a bound rule (team = the unsuffixed spawn bound that
   contains a marker; no equal-pocket requirement) and emits the three capture points and the stage table; `iceberg.modes.json`,
   `iceberg.bots.json` (player spawns from the pockets, bot zones from the 11 AI core points, waves and PMC squads after AirPit),
   `iceberg.exits.json` (two boxes on the main NavMesh component outside the gates: east about (72, 2.0, -94), west about
   (-30, 2.9, -86)), `iceberg.ai.json` (11 core points; the 4 off-map patrol hubs and the 10 off-map bot markers are dropped).
8. **Doors, gate sounds, score monitors**: `DOT_SOURCES["arena_iceberg"]` and `iceberg.doors.json` (11 rows, 3 barrier paths),
   `extract_gate_sounds` row with a per-map join margin, `gate_sound_fit.json` rows for the six clips, `iceberg.gatesounds.json`;
   `extract_score_monitors` with word 13 dark, `iceberg.monitors.json`.
9. **NavMesh oracle and bake inputs, sidecar + descriptor in ONE commit**: `navmesh_diff.py` `ARENA_SOURCES` row
   (`sharedassets377.assets` path 2, mask 0x40042A00, identity placement), the oracle on v1/v2, `bake_bounds`,
   `navmesh_bake_layers` [9, 11, 13, 18, 29, 30], `keep_level_borders`, `opened_barriers`, `start_gates`, `uses_server_time`,
   puddle material names (from the 30 puddle decals), a bake-input audit (layer-0/12 solids in the play space, the cube-top
   island area). Precedent: Bowl `4cea40b`.
10. **Identity and descriptions**: banner and loading-screen rows (`extract_banners`, `extract_loadscreens`, `ArenaMapBanners`),
   the description-pack rule in `ArenaMapDescriptions.Load` and its tests, the own-words `LocaleDescription`, a real name.
11. **Spatial acoustics**: `extract_spatial_audio` accepts mirrored trigger volumes (the client's volume test checked for
   negative scale), the 533 routes explained and repaired or reviewed, the graph JSON, bake and room tone staged; room reverb
   and rain audio rows.
12. **Lock-step promotion into `ArenaMapRegistry.All`**, every list in one commit (Bowl `3ef5132`): descriptor, sidecar,
   base.json (`make_base_json.py`), client and server csproj rows, the per-map client arrays (`NativeSpatialAudio`, `RoomReverb`,
   `PrecipitationAudio`, `NativeDecalRepairs`, `AmbientAudioDefaults`, `AmbientAudioProfiles`, `MapLightingDefaults`,
   `ShaderStandinPolicy` as needed), the crowdless rule (packer, `validate_distribution`, client staging), the parked lists
   (`park-unreleased-dist.ps1`, `AdditionalMapBuilds.props`), the release validators and every test that pins the released set.
   The gate is the full `dotnet test -c Release` and `unittest discover` (run from PowerShell: the Bash tool's console encoding
   breaks two subprocess tests).
13. **Build U1** (`pwsh -Command "& 'tools/build_all.ps1' -MapKey arena_iceberg -MapDescriptor
   D:/ArenaMapPort/tools/maps/iceberg.json -PackageDir D:/ArenaMapPort/work/extract/iceberg-native-v2 -FullMap -OutputDir
   D:/ArenaMapPort/work/build/iceberg -BakeNavMesh \$true -Verify \$true -SkipGameRefsSync"`), then by hand any validator the
   script did not reach (`validate_bundle_blocks.py`, `validate_bundle_roundtrip.py`, `validate_preset.py`,
   `validate_map_descriptors.py`). Regenerated stand-in shader stubs are committed after checking that they do not change a
   released map's build.
14. **First stage, deploy, first raid**: client Release build without DllOnly with `-p:ArenaMapPortCreateReleaseKeys=true` once
   (back up `work/release-keys` off the VM at once), `verify-keys` against the VM's and MASCHINE11's Arena, server build,
   `validate_distribution.py`, `deploy-to-spt.ps1 -Confirm` (close EFT first: about 1.2 GB more through Syncthing), EFT restart,
   `gpu-run raid --map iceberg` without bots, log baseline (NavMesh line, spawn openings, warnings, shader resolution of
   `Nature/SpeedTree8` and `MK4/Rock Simple`), `uibridge_teleport dryRun` from both pockets to A, B and C, a raid with bots, the
   hideout once, max frame time.
15. **Host iteration U2..Un**: gates and pockets, borders, props the oracle flags, rain F8, bots, cross-map raids.

### Phase C - Native features and polish

16. Host verification of the native features (ballistics F10, decals, light culling F7 with 1,126 culling and 276 volumetric
    lights, rain cover), the frame budget (about 1,280 realtime lights) and the `IndoorFloorSmoothness` freeze.
17. MultiFlare (79 `FlareLight`; four with a negative total scale need a reviewed rule), water (the `WaterForSSrv3` pool and the
    canyon `SimpleWater4` planes), DynamicMaps ground-floor overview, shot-acoustics room classes, reflection-probe look,
    vegetation decisions (`TreeWind`, impostors), the minefields (17 `ArenaMinefield`; ArenaMode data).

### Phase D - Release

18. Release texts, version, dist manifest, cut, VirusTotal, key backup, tag and upload only after the user's decision on number
    and form **(user)**.
19. **ArenaMode hand-off (user)**: catalog entry and `OptionalMapsRevision`, `DominationMapLayout` row for A/B/C, a 10+10
    formation from the two initial bounds (`ArenaTeamBoxCatalog`), the stage markers for respawns, the three refill crates and
    the two spawn-protection triggers. The binder contracts (`SpawnGates.GatesOpened`, `ScoreMonitors`) are map-agnostic;
    `SpectatorCrowd.IsAvailable` stays false on Iceberg.

## 2. Iceberg-specific risks

1. **Navigation**: the spike's bake was unusable; the gate colliders sit on layers 12 and 18 and Arena's mesh was baked with
   them open; the motor gate rotates; six Bear stage markers lie off Arena's mesh.
2. **Borders**: 365 solid, rendered cubes. A cube on a legitimate route shows up as a NavMesh cut in the oracle or a wall on the
   host.
3. **Unequal spawn sets** (40 Bear, 30 Usec markers, 12 bounds): the pocket rule and ArenaMode's two-sides-of-10 formation both
   need the bound rule.
4. **Strict audio checks**: mirrored trigger volumes, failing baked routes and `minDistance` 0 each stop an exporter today.
5. **Light and frame budget**: 1,126 culling lights against AirPit's 722 and Bowl's 106; 28 `CullingAdvancedLightObject` and 24
   `AnalyticSource` are not covered by the light-culling exporter.
6. **Size**: a scenes bundle of about 1.1-1.3 GB **(inferred)**; the single ZIP reaches about 5 GB.
7. **Key stability**: four resource streams (560 MB) are the key source; Arena 0.6.0 rewrote them once. Checked against 0.6.1 and
   0.6.2 only.
8. **Text coupling**: the official description pack must not be dropped for the released maps when Iceberg joins without one.
9. **Shaders**: `Nature/SpeedTree8` may render magenta; the cloth (280 renderers) and rock shaders depend on Customs' shared
   assets being loaded, as on AirPit.

## 3. Deliberately not in this plan

BlastGang and OverRun, GPUInstancer grass, SpeedTree wind and impostor trees, Arena's minefield logic, a port-only spectator
crowd, Arena's fixed dusk clock, the Sea container far outside the play area, and every ArenaMode change (the user's repository).
