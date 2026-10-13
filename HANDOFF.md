# HANDOFF — dated working state

Snapshot for whoever continues (human or agent). Verify the commits named here against `git log`; this file
is updated by hand at each handover, it is not an automatic record. Read [AGENTS.md](AGENTS.md) first.

## State on 2026-10-12 (night) — AirPit Phase B built, deployed and host-tested; portal gates opened

Branch `feature/airpit`, head after `03e945e` (+ AGENTS/HANDOFF touch-ups). PLAN-AIRPIT Phase B is in: lock-step registry
promotion (`ArenaMapRegistry.All = { Equator, Bay5, AirPit }`, 11 maps parked), weather/time (donor `Reserve_Base_Scripts`,
ZeroLevel -4.4, server time), shader stand-ins (fog sheet, cloth, rock), descriptor bake inputs (`bake_bounds` x -80..56 / y -2..8
/ z -96..92, `navmesh_bake_layers` 9/11/13/18/30), AI anchors, acoustics, decals, light culling, banners/loadscreens, server
registration. Two Unity builds: U1c (`unity_build_all_20261012_020615.log`, after two texture-policy fixes `232ea72`/`d52c898`) and
**U2 (`unity_build_all_20261012_025338.log`, bake 10264 vertices / 4684 triangles, 2025 sources, 4 opened barriers, 0 exclusions)**,
the latter staged, validated (`PORT_VALIDATED`, `DISTRIBUTION_OK 3 maps 9 bundles 3 sidecars`) and deployed to `D:\SPT41` (host hashes
checked over `\\192.168.178.109\C$`). `validate_port.py` takes `--package` now (the accepted package was `airpit-native-v5`;
since 2026-10-12 afternoon it is `airpit-native-v6` = v5 + native ambient audio, item 13 phase 1; scenes/meshes/materials/textures
byte-identical, and the AirPit contract now REQUIRES audio, so a build from v5 fails the validators).

Three host raids on SPT-GPU-2 (all evidence in docs/progress/airpit-release.md, "Host raid 1..3"): every raid-start repair logs as
expected (415 stand-in materials rebound, no magenta, 533 culling lights, acoustics 24 rooms / 94 portals, 471 decals, rain cover
8748 elevated meshes, 19 core points + 3 hubs), bots spawn and run (`BotZone_Main markers=14 onNavMesh=14`, 2 PMCs on SAIN + 3 scavs,
no `ActiveFail`). **Raid 1 found the north spawn field sealed:** the four bake-only `navmesh_bake_exclusions` (shop shutters) had put
NavMesh through solids (walk 283.8 m for 41 m, stuck in the `DonB_Ramen_closed` shutter) and the z 50.5 facade is closed by two
`Prison_Metal_Fence_01` portal gates (closed fence active, swung-open halves inactive = Arena's ShootOut/TeamFight gate). Fix
`5f0ac2b`: both gates are `opened_barriers`, the exclusion list is empty (tests updated). Raid 2 verified it: walk Usec spawn -> core5
= 40.8 m along the navmesh, arrived; every dry-run teleport passes. `d23efda`/`3bd7780`: the RainBounds scan is clipped to the
descriptor play space (216 x 268 m instead of 4.3 x 3.2 km; Equator/Bay5 unchanged, they carry no BakeBounds).

Morning of 2026-10-12 (user look at the map, host raids 4-6, commits `2ebe94c`..`0419ab1`, U3/U4 rebakes): Arena bounds AirPit's
TeamFight area by a data polygon, not colliders (`ExcludeLevelBorders`, 12 vertices, in the culling scene) - the passport-hall
facade has a 32 m opening into the unlit entrance hall and the apron opens onto the runway. New descriptor/sidecar input
`authored_borders` + `AuthoredBorderRepairs`: twelve invisible movement-layer walls along that outline (north edge on the facade
line), excluded from the ballistic-twin pass; `bake_bounds` x -57..37, z -83..70. User decisions: the six shop shutters stay closed
("dahinter ist nichts"); the passport-control gates stay closed as the look (`PASSPORT_CONTROL_DOORS` is no opened barrier, red
lane lights stay) - they are waist-high turnstiles with open floor between them, NOT a barrier (raid 5 walk), so the facade wall is
the real limit; the four north spawn points moved south of the gate line (`test_airpit_spawns.MOVED_FROM_MARKER`), AI core point 7
stays as a documented island. Current bake U4: 8193 vertices / 3775 triangles. Evidence and the GateEntity notes for ArenaMode in
docs/progress/airpit-release.md ("Host raid 4", "Host raid 5").

Midday 2026-10-12 (source audits, no code change, `docs/progress/airpit-source-alternatives.md`): the user asked whether BSG's
"Air Pit 2.0" could replace 1.0 (our 0.5.4 export was all pink) and what `Arena_Airpit_new` (scenes 207-212) is. 2.0's pink is a
single blockout shader (`Custom/WorldTriplanarFresnelPro`, ~95 % of the visible area; the stand-in row exists but is gated to
Bay5/AirPit and AirPit2 is not in `All`), and 2.0 is an unfinished CheckPoint/OverRun dev map on Bay5's AI with no locale/banner/
loadscreen. `Arena_Airpit_new` is bound by no preset: a 2024-01 BlastGang-era art pass (two copies of the level, 76 % dead grass,
Prison AI stub without NavMeshData, Bay5 light, Train settings, 1.0 Scripts copy, bomb sites and defender spawns over void).
User decision (midday): AirPit 1.0 stays; the release may exceed 2 GB and is split into several ZIP parts (item 24 superseded).
Side finding for the next candidate: Bowl's one pink material is `Tarp_Tunnel` on
`p0/Reflective/Bumped Specular_TwoSided` (plus the two cloth shaders AirPit already substitutes) - a fallback-table job.

Afternoon of 2026-10-12 (raids 7 and 8, commits `171c278`, `e56980a` + follow-ups; evidence in docs/progress/airpit-release.md): (1) the
rain check is DONE: new developer-only key `Weather (test)/ApplyOnServerTimeMaps` forces the test preset on Bay5/AirPit; F8 at the
accepted RainBounds gives 44.3 m (passport hall) / 45.2 m (hall) / 0.03 (apron) with streaks only on the apron; rain audio follows
the acoustic rooms. (2) The fog-sheet A/B is moot: EFT 0.16.9.5 carries `Custom/Billboard_FogSheet_Simple`, both cloth shaders and
the rock shader itself (Customs' scene shared assets), they are loaded in-process, so the item 11 stand-ins never ran and the
haze is Arena's own look; `Hidden` now recognises both shader names, the log says `resolved to EFT's own loaded shader`. (3) The
airstairs are reachable on the runtime mesh (`uibridge_walk` 7.9 m to y 3.4): closed. (4) The hall-centre cover detour stays as
accepted (bots path 194 m). NEW: on U4 the bot spawns `north-hall-west`, `west-south` and `east-south` (and core points 16/17/18)
sit in the sealed shop pockets behind the closed shutters (dry runs: navmesh island / through a wall) - the three bot spawns moved
onto the main component (`9bf4f89`: sidecar + base.json + tests; raid 9 on the deployed base.json: all three pass `dryRun` from the
south spawn, three scavs spawn and patrol 50-70 m across the hall and mezzanine in 30 s, no `ActiveFail`), the core points stay as
documented islands. Status audit of
Phase C (eight read-only agents, 2026-10-12): items 14-21 and 23 are implemented and log-verified. Item 13 native ambient
audio is IN and host-verified (evening 2026-10-12): phase 1 (contract-driven extractor, AirPit contract, package
`airpit-native-v6`, `601074d`), phase 2 (profile-driven importer + client driver with two bus leases, `AmbientAudioProfiles`,
provisional AirPit defaults 4 / 0.4 / 3, `f20a79f`), phase 3 = **U5** (`work/logs/airpit-u5.log`: importer authored 26/9/61/2,
bake unchanged 8193/3775, scenes bundle 855,037,777 B), staged, `PORT_VALIDATED`/`DISTRIBUTION_OK`, deployed; raid 10
`[NativeAudio] ready players=26 started=26 ... mixers=CommonAmbOutEffects,CommonAmbInEffects`, raids 11-13 Equator/Bay5
unchanged and AirPit again after the map change. Open for the user: the listening pass (mix, Out/In crossfade by room: options in
the progress doc), the ballistics shooting check. Incident on the way: a held developer archive threw out of `Plugin.Awake` and
killed the plugin for one session (bundle 404) - fixed in `438f179`, `ConfigRetirementCheck` covers it.
Evening user feedback (progress doc "User feedback (2026-10-12 evening)"): (a) the north-east shop at `east-north` is an empty shell
(interior removed by Arena, open to the north through Arena's own breach, no door object anywhere) - spawn moved to (8.0, 26.5),
room left open (sealing it = new importer/bake mechanism, user's call); (b) the hall glass was near-black because the rebind forced
the stand-in's Geometry queue onto the transparent shader - `ShaderStandinPolicy.EffectiveRenderQueue` restores the shader default
on AirPit (host check: F10 `renderQueue=3000` on `Airpit_Building_Glass_LOD0`, screenshot from the mezzanine (-3.9, 5.1, 4.0) facing
south); (c) "hall-centre cover" = the plywood/fence/container cordon under the airliner (photos 27/28). User decisions (2026-10-12 late): the hall-centre cover STAYS (no rebake), the glass looks good after the queue fix, the rain is fine. New question from the user: all four shops should be walkable in Arena - where did the interiors go? Answer (progress doc "Where the four shop interiors are"): every Arena mode loads the same hall `Arena_AirPit_ShootOut` (324) whose shops BSG stripped (interiors, doors, physics props, lights) and sealed with static `*_closed` meshes before 0.5.4; the furnished shops exist only in the unreferenced legacy scene `Arena_Airpit_Main_hall` (38) + Light (37); the AI/NavMesh/design data (2025 bakes, menu art) still describe the furnished hall. Walkable shops = a new slice import from level38/37 into the generic pipeline + shell swap + rebake (L). **User decision (2026-10-12 late): DO IT - import the furnished shops from level38 (+ lights from 37); also check whether other decoration/decals were lost elsewhere.** Phase 0 done (progress doc "Phase 0 results"): partial-scene design, lost-decoration census, bake consequences; phase 1 DONE (2026-10-12 23:45, workflow `wf_fe3c0bde-c1f`; progress doc "Phase 1 - partial scenes"): generic partial-scene import (`tools/extract/scene_select.py`, plan `select` records, manifest/contract `partial_scenes`, `tools/validate/partial_scope.py`), package `airpit-native-v7` = v6 + 7118 Main_hall objects (1446 units, 85 carriers, 544 twins skipped, 4 hidden video-player BallisticCollider orphans) + 962 Light objects (187 units, 156 lights); the five v6 scenes/meshes/materials/textures byte-identical; decals 683, ballistics 4455, culling lights 680, volumetrics 139; descriptor `AllSceneNames` +2 and `OpenedBarriers` +6 (the `*_closed` shells) mirrored in C#, python 1274 / dotnet 727 tests green, validate_extract OK with reproducibility. Not yet built or host-tested. Phase 1b census done (2026-10-13 00:40, progress doc "Phase 1b - census"): the decoration box is effectively the whole level38; 245 prop instances (632 units) of the old hall-centre cover would duplicate 324's prefab platforms and nudged props -> new select key `exclude` (208 entries); the root-only twin rule loses 20 child lights in level37 -> new select key `twin_descendants: import_missing`; ring lamps are L12 (no NavMesh islands); spawn check 0 hits. Phase 1b DONE (2026-10-13 04:00, workflow `wf_409814f7-455` + lead; progress doc "Phase 1b - package v8"): select keys `exclude` (208 level38 entries, 632 units; 9 level37 entries for parts level325 replaced on purpose) and `twin_descendants: import_missing` (22 level37 + 4 level38 imports, bare twin carriers), counting contract `sum(selected)+sum(descendants_added)-sum(excluded) == units`; accepted package `airpit-native-v8` (55,263 objects, Main_hall 13,006 / Light 1,538; ballistics 5282, decals 722, culling lights 722, volumetrics 154; five full scenes byte-identical to v6/v7, v7 a subset); validate_extract OK (1800 s timeout default now), decal refresh identical, spawn check 0 hits, bake pins 7922/7775/3036; python 1377 tests green; descriptor/sidecar unchanged. U6 BUILT, DEPLOYED AND HOST-VERIFIED (2026-10-13 03:05-04:00, progress doc "U6 host raids 15-18"): bake 3048 sources / 11332 vertices / 5212 triangles, scenes bundle 992 MB, validate_port + validate_distribution OK, `navmesh_diff --compare-bundle` 187,767 of 204,204 Arena samples (U5 187,410); the four bot spawns are back on Arena's anchors (`89a072a`, built-mesh clearance 1.50 / 2.18 / 1.89 / 1.80 m, test_airpit_spawns pins the back-room boxes). Host raid 15 found the south-west shop still walled: three layer-0 blockout panels `SBG_Arena_AirPit_ShootOut/SOO/Cube (10) Group` on the roll-door lines (bake mask ignores layer 0, player collides) -> tenth `opened_barriers` entry (`62d7739`, no rebake). Raids 16-17: every shop probe, core 16/17/18, hubs and the reverted spawns reachable, back rooms islands like Arena's mesh, walks through the SW shop and west-south -> east-south (86.6 m) arrive, 1050 lights / 722 culling lights / 705 decals active, F7 481 culling lights, 116 fps, bots 14/14 markers on the mesh and active (no ActiveFail); raids 18-19 Equator/Bay5 unchanged. Screenshots in `work/screenshots/airpit-2026-10-13/`. Open: a listening pass of the shops' ambience, a night look, the 13 recovered SE-shop lights and the 12 soft-shadow passport-hall spots by eye, item 25 text sweep, release 0.2.0 ZIPs. User extended the decision (21:15): afterwards import everything else that can be taken (passport-hall clutter and ring lamps, stalls, apron litter, floor damage overlays, all decals) = phase 1b -> v8; then descriptor/sidecars (`AllSceneNames` +2, `opened_barriers` +6), U6 build, deploy, host dry runs, spawn revert to Arena's anchors.
Item 24 multi-part packager is in (`606f00e`: `tools/release/make_release_zips.py`, core + one ZIP per map, 4 parts, largest
755 MiB; the real build needs a clean tree and a scratch dir for `--extract-check`). Item 25 (0.2.0 text sweep, night look-test, Equator/Bay5 regression raids,
AirPit -> Equator -> AirPit sequence, bots raid on U4, map-select screenshots, bloom -1/-1 decision, light-parity signage look).
Host config: `General/DeveloperMode` is on; `Weather (test)/Weather` = `Server`, `ApplyOnServerTimeMaps` = `false`.

## State on 2026-10-11 (evening) — AirPit chosen as the third map; plan written

Branch `feature/airpit` from `d7773e5`. The user picked AirPit (`arena_airpit`, TeamFight layout) as the next
map. Three read-only surveys plus verification produced [docs/PLAN-AIRPIT.md](docs/PLAN-AIRPIT.md): 25 ordered
items in four phases (generic extraction plumbing with plan scene roles and data-driven native contracts;
a playable raid with sidecars, Arena NavMesh oracle, round-start barriers and the lock-step registry promotion;
native features in the Equator/Bay5 order; size gate and release). Key facts: AirPit is a 2026-09-15 spike with
no spawns/exits and no native features; Arena 0.6.0 keeps its scene indices (content delta 214 of 20,252
objects in the main scene); 509 of 813 lights are saved inactive; 127 fog-sheet renderers need a shader stand-in;
the three-map ZIP projects to about 1.9 GB. Control-point data (capture anchor, SPAWN_BOUND) ride along as a
generic sidecar when AirPit is extracted (user decision 2026-10-11). The user took every recommendation of the
plan's section 4 (re-extract from 0.6.0, server-time day/night like Bay5, bake y -2..8, native audio now, 0.2.0
single ZIP if under ~1.95 GB). Phase A (items 1-7) is done on `feature/airpit` (2026-10-11, evening): plan
scene roles, `tools/extract/native_features.py` + `extract_map.py --native-features`, data-driven
`tools/extract/native_contracts/<map>.json` (audio optional), the four feature extractors read plan roles, the
generic `tools/maps/<map>.modes.json` sidecars (capture point, SPAWN_BOUND boxes, TeamFight spawns) for all three
maps, `work/extract/airpit-native-v5` extracted from Arena 0.6.0 and validated with reproducibility
(40,719 objects, 3,548 ballistic colliders, 481 decals, 533 culling lights, 32 NavMesh modifiers, 2 indoor
triggers), `tools/maps/airpit.{bots,exits,ai}.json` + descriptor exits, and the Arena NavMesh oracle
(`tools/validate/navmesh_oracle.py` / `navmesh_diff.py`: Arena's play component 8,168 m2, 190 prop instances the
port bakes as solids). Log: docs/progress/airpit-release.md, delta: docs/progress/airpit-source-060.md.
Known: `validate_extract.py` on the accepted 0.5.4 Equator/Bay5 packages reports documented 0.6.0 source drifts
(preset offset 5708 != 5716, Bay5 disabled renderers 302 != 305) - unchanged output is the parity gate
(`work/airpit-parity/parity_gate.py`); `dotnet test -c Release` re-stages the server DLL into dist/.

## Package update on 2026-10-11 — v0.1.0 muzzle-flare correction

The refreshed v0.1.0 ZIP contains the accepted client from `1893272` and an updated
package README. The other 49 entries, including all server/map files and notices,
are byte-identical to the first published archive. Total: 51 files,
1,231,754,560 bytes; SHA256
`85007889b01b8d625255f50dad00bca0962e9879f373ee238cb03f5cc3113494`.
The original ZIP is retained locally under `work/releases`; refreshed artifacts
and verification records are under `work/release-muzzle-update`.

The existing `v0.1.0` source tag remains at `edd7dbd`; updated release notes link
the corrected client source explicitly. This packaging task does not rebuild or
redeploy binaries. SPT41vanilla still has the earlier client until updated.
See [package refresh](docs/progress/muzzle-flare-exclusion.md#package-update--2026-10-11)
and [release notes](docs/releases/v0.1.0.md).

## Follow-up on 2026-10-11 — muzzle flares and player documents

Equator now excludes native muzzle-emission regions from the restored anamorphic
flare seed. Ordinary muzzle flash/bloom and separately positioned lamp streaks
remain intact. Native GPU-2 Mk17 firing and Equator -> menu -> Bay5 control passed;
Bay5 keeps no active mask or tracked shots. Release build has zero warnings/errors.
Final client SHA256 is `b5a6919020e243fc9f0a8fa2ecf031d75d5c594f3dc31b4d2d94bd69afbb1eb1`;
it is deployed to the main synchronized test install only. GPU-2 loaded 19/19
matching identities. The server and map assets are unchanged; SPT41vanilla and
the published v0.1.0 ZIP retain the accepted release build.

README is now player-oriented and `FORGE.md` provides a compact English mod
description. See [render scope, evidence and limits](docs/progress/muzzle-flare-exclusion.md).
The final test logged the existing missing Books impact bank for a physics shoe;
there were no flare-filter errors. No launcher/modded-weapon catalogue or identical
same-weapon A/B comparison is claimed.

## Follow-up on 2026-10-11 — first release and client folder

Release 0.1.0 uses `BepInEx/plugins/maschine-ArenaMapPort`; the server remains at
`SPT_Runtime/user/mods/ArenaMapPort`. Build/deploy/export/validation paths follow
that contract. The explicit deployment `-RenameClientFolder` mode preserves the
complete old client folder and requires EFT to be closed on synchronized hosts.
The accepted client/server DLLs and all 48 runtime files remain byte-identical to
test3; only ZIP paths and its README change. The final archive has 51 entries.
37 Python tests, 21 rename checks, 10 DLL-only cases, 99 DynamicMaps deployment cases
and the real distribution validator pass. No new gameplay behavior is introduced.
Local pre/post installation and publication evidence is under ignored
`work/first-github-release/`; see [release record](docs/progress/client-folder-release.md)
and [release notes](docs/releases/v0.1.0.md).

## Publication decision on 2026-10-11

The user chose a new independent `maschine34675/ArenaMapPort` repository containing
the cleaned history. `ArenaMapPort-dev` must stay private: GitHub still serves old
removed content there by commit ID despite the successful history rewrite.
Use the new repository as the publishing target and future `origin`. Preserve the
ignored local exports and original-history backups. The publication checks cover
a fresh remote clone and rejection of old content on the new repository before
changing its visibility. See [publication cleanup](docs/progress/publication-cleanup.md).

## Follow-up on 2026-10-11 — source publication cleanup

The user accepted the release on SPT41vanilla and authorized removal of raw exports
and private machine/session details from the full Git history before publication.
Seven original analysis JSONs are preserved byte-identically in ignored
`work/local-analysis/`; original history is backed up under ignored
`work/publication-cleanup/`. Equator/Bay5 now read source metadata directly from
the chosen Arena installation. Both live census runs and 54 focused tests pass.
Accepted runtime binaries, bundles and test3 ZIP remain unchanged.
Historical build IDs below describe the pre-cleanup history; the local Git bundle
and `.git/filter-repo/commit-map` retain that correspondence after rewriting.
See [scope and verification](docs/progress/publication-cleanup.md).

## Follow-up on 2026-10-11 — debug helper retired and branches consolidated

`664564c` adds the sanctioned `-RemoveDebugHelper` migration and retires the
standalone helper from both installs. All four main-menu shortcuts are gone;
GPU-2 `25d3d20a` verified the clean menu and ordinary PMC map selection, 19/19
identities matching. Debug DLLs are backed up under ignored work/deployment-backups;
config and other plugins stay untouched. Runtime MapPort binaries and test3 ZIP
are unchanged. Vanilla needs an EFT restart only if it was open; GPU-2 is at MainMenu.

All development history is consolidated into `main`, now the pushed GitHub default.
26 merged local branches and both obsolete remote feature branches were deleted.
Only D:/ArenaMapPort remains as a worktree; required ignored assets/build inputs
were retained. Use main for subsequent work. See [execution and verification](docs/progress/release-finalize.md).

## Earlier on 2026-10-11 — official map descriptions

Implementation `fa4a484` extracts original English/German/Russian descriptions from
the installed Arena locale TextAssets and stages one owned server locale JSON.
Location info and banner descriptions share the selected text; other languages
fall back to original English. No BSG text is tracked. Server builds now require
`python tools/extract/extract_map_descriptions.py` once before staging.
Release build, C#627, extractor13, distribution12 and real-package validation pass.
GPU-2 `208f2f74` visually verified both English descriptions, 20/20 loaded identities,
no owned startup warnings/errors. Both installations updated; vanilla needs restart.
The client remains `e17198d`, the server is `fa4a484`; GPU-2 is at MainMenu.
Test3 ZIP contains 51 files / 1,231,750,103 bytes, fully CRC/SHA256 verified.
See [description provenance and acceptance](docs/progress/official-map-descriptions.md).

## Earlier on 2026-10-11 — lean test release and map labels

Implementation `e17198d` omits unused arena-audio/arena-ui from builds/releases,
moves Equator DB to `db/arena_equator/base.json`, and aligns PMC/ArenaMode anchors:
Equator 700/290, Bay5 320/820 on the northern coast. ArenaMode's installed assembly
stays unchanged; a soft integration preserves the native server positions.
Release build/C#615, cleanup/distribution/migration tests and exact ZIP verification
pass. GPU-2 `c9548184` verified both maps/selectors and PMC -> Arena -> PMC, 20/20
identities matching, no owned startup errors. Both installs updated via script;
legacy loose DB backed up and removed, vanilla needs a user restart. Local test2
ZIP is 1,231,747,934 bytes / 50 files. See [release cleanup](docs/progress/release-cleanup.md).

## Earlier on 2026-10-11 — hotkeys and naming metadata

The four diagnostic hotkeys F7–F10 already have developer-only component creation;
normal player mode does not poll them. Restart after changing DeveloperMode.
Client metadata generation is enabled so Company and Version actually reach the DLL;
server Company/Url and GUID casing now follow the naming convention. DLL names,
folders, map IDs and version 0.1.0 remain unchanged. See the
[audit and verification](docs/progress/hotkeys-and-metadata.md).
Implementation **cd51df4** passed the Release build, 615 tests and compiled metadata
checks. GPU-2 **0d555a71** reached MainMenu with 20/20 loaded identities; direct
plugin-host inventory confirms all four diagnostic pollers absent in player mode.
Server validator accepted the corrected GUID. Both installations have matching
client/server DLLs; vanilla needs a game/server restart. GPU-2 game closed, server running.

## Earlier on 2026-10-11 — release logging and decal-copy warnings

Runtime Info is limited to the startup/map-availability summaries; technical progress
is Debug and expensive diagnostic work additionally requires DeveloperMode. Warnings
and errors remain visible. Native decal normal maps release unused readable CPU bytes
before the vanilla atlas is created, preserving uploaded pixels/mips. InitWeather is
patched through its declaring closed generic type. See [logging audit](docs/progress/release-logging.md)
for the source-backed CopyTexture explanation and remaining LOD/MenuScreen/other-mod findings.
Implementation **c102b6c** passed the 615-test C# suite and warning-free client build.
GPU-2 verified Equator -> Bay5 -> Equator, correct 346/468 decal counts and visuals,
zero CopyTexture/owned Harmony warnings, and developer F9 Debug output plus live
decal off/on reuse. Full normal log: one own Info, 546 Debug, no own warnings/errors.
Server output was separately verified after synchronization/restart: two Info and
four Debug. Both DLLs are deployed and hash-matched on SPT41 and SPT41vanilla;
vanilla still needs the user's game/server restart. Final GPU-2 DeveloperMode=false,
two public options, EFT closed, verified server running. Known source LOD warnings
and the pause-menu coroutine error remain documented, not suppressed.

## Earlier on 2026-10-11 — owned overview-map integration

Equator/Bay5 overview data now stages under ArenaMapPort/dynamicmaps. The optional
DynamicMaps 1.2.1 integration loads it before native image precaching; accepted map
files remain byte-identical. The separate sanctioned `-RemoveLegacyDynamicMaps`
migration validates both replacements and the client DLL before removing only our
two old packages. See [implementation and acceptance](docs/progress/dynamicmaps-owned.md)
and [delivery commands](tools/dynamicmaps/README.md). Implementation `efbbceb`;
Release/C# suite, 99 deployment cases, 43 exporter and 10 distribution tests pass.
GPU-2 `d8713da3` rendered both maps from their new absolute paths, exactly one
selectable definition each; 13 total including eleven unchanged maps. MainMenu,
20/20 loaded identities matching, no integration warnings/errors. Both installs
received verified DLLs and packs. Four old files removed from SPT41; all 90 other
DynamicMaps files and config unchanged. Vanilla had no old pack and needs a restart.

## Earlier on 2026-10-11 — advanced developer switch and rounded ambience

General/DeveloperMode is now visible under F12's Advanced settings filter; it still
requires a restart. The compact main CFG retains two entries. Both maps use
AmbientVolumeOffset=0.4. Audio defaults revision 2 rounds only the prior accepted
offsets, preserving other saved values and the original whole-neutral-mix migration.
In normal mode the archived values stay untouched on disk; runtime migration supplies
the rounded offsets. See [configuration follow-up](docs/progress/compact-player-config.md).
DynamicMaps alternate-loader feasibility was checked against installed 1.2.1;
the current optional packages still target DynamicMaps/Maps. No integration moved yet.
Implementation `2f0ff31`; 610 C# tests, real-BepInEx harness and Release build pass.
GPU-2 `85aa779c` verified Advanced on/off visibility and both runtime offsets=0.4;
20/20 loaded identities match. Both installs received verified DLLs; vanilla needs
a restart. Main CFG still has two entries, no new developer archive in normal mode.

## Earlier on 2026-10-10 — compact configuration and natural midnight

Player F12 now exposes only `Appearance/NaturalEquatorNight`. It selects Equator's
natural Day appearance at midnight and removes neon additions on the next raid;
time, rain and SAIN night identity stay unchanged. Day and Bay5 are unaffected.
The normal CFG has two entries; hidden General/DeveloperMode is edited manually
with the game closed. Existing full tuning is archived as `.developer.cfg` and
remains active. Fresh player installs never create a full developer file.
All functional bindings use Plugin.Settings; never explicitly save this read-only
normal-mode ConfigFile. Developer mode restores its values into the primary file
for F12. See [compact-player-config.md](docs/progress/compact-player-config.md).
Implementation `88c5d45`; build, complete C# suite and real-BepInEx persistence
checks pass. GPU-2 accepted natural midnight -> neon midnight -> Bay5 and restart.
Main CFG shrank from 124333 to 784 bytes; all 293 tuning values survive unchanged
in the archive. Final process `0fc7d88f`, MainMenu, 20/20 loaded identities match.
DLL-only deployment to both installs verified; vanilla needs a user restart.
NaturalEquatorNight is false (neon default). No release was published.

## Earlier on 2026-10-10 — first release scope and player options

Equator and Bay5 are the only runtime and staged maps. All fourteen descriptors
remain in Catalog; the twelve parked maps retain their sidecars, source/extract
and build inputs. Release staging archives exact stale parked outputs outside dist.
Accepted ambient defaults are frozen (+5 dB / .4037559 / ×2.5 Equator;
+4 dB / .399061 / ×4 Bay5); whole-neutral-tuple migration preserves custom mixes.

F12 player mode now has ten controls; General/DeveloperMode requires a restart
and exposes 238 supported-map settings plus diagnostics. Internal/legacy markers
stay hidden. The effective mode is latched so visibility and diagnostic execution
cannot disagree mid-session. Functional repairs/presets remain active in both modes.

Release client/build, 606 C# tests, real BepInEx persistence, 669 Python tests
(44 skipped), eight PowerShell cases and exact two-map distribution validation pass.
GPU-2 verified the two-map menu, player/developer visibility, Equator Day/Clear,
Bay5 native time/weather/audio, Equator Night/Rain, F10 gating and raid cleanup.
Verified DLLs are deployed to both installs; the vanilla install needs its own
restart. No publishing/tagging/version change. See
[first-release-prep.md](docs/progress/first-release-prep.md) for hashes, screenshots,
configuration preservation, limits and the final host state. Final post-commit
GPU-2 process: `27186010`, player mode, MainMenu, 20/20 binary identities matching.

## Follow-up on 2026-10-10 — Equator open atrium rain

User confirms the indoor rain output repair is audible. Fixed the remaining
muffled sound in the open atrium: precipitation now reads actual roof exposure
from the existing donor depth photo, independently of the indoor acoustic room.
Lattice intersections remain open; continuous roofs select the indoor bed.
Room reverb, Bay5's selection, rain levels and all saved settings are preserved.

GPU-2 final process `4f109668` passed lounge lattice -> covered corridor -> open
atrium, FullyIndoorCover on/off with same-RT cache invalidation, and Day/Clear with
no rain source or snapshot. Both raids cleaned up; MainMenu and 20/20 loaded-build
matches confirmed. Release tests/build and distribution validation pass; both
installs have the verified DLL pair. All 891/804 saved config assignments match.
See [rain output follow-up](docs/progress/equator-rain-output.md#follow-up-2026-10-10--open-atrium-precipitation)
for source rationale, native measurements, artifact hashes and evidence limits.

## Follow-up on 2026-10-09 — audible indoor rain output

The user's listening test rejected the earlier rain acceptance: source PCM was
present, but the native PrecipitationAmbientIn child is muted (-80 dB) in the
effective Outdoor mixer snapshot, independently of imported acoustic rooms.
The two owned indoor sources now use its effect-free immediate AmbientIn parent;
all downstream hearing-protection/master processing and room gains remain.
Outdoor rain and the accepted chemlight layout are unchanged.

GPU-2 process `2632a9ff` compared an identical quiet 1500 Hz pulse at the final
listener output in the same Equator indoor room. Old path: no lift over background;
repaired path: peak 0.018062 vs baseline 0.0000071652; old-path repeat again silent.
This proves the previously missed mixer gate. Native rain playback and room
transition also passed. All 891 host and 804 vanilla saved settings remain.
The existing opt-in ReverbPulseBus now offers both paths for diagnostics.
Release build, 577 tests and distribution validation pass. Clear Day has no
playing rain source; both raids cleaned up. The verified DLL pair is deployed
to both installs; final client SHA256
`3DB8476449317F9F72D71951573303E76938665B47FA96C69DF93CE43BE2FFE6`.
GPU-2 is at MainMenu with 20/20 loaded identities matching. Restart the user's
client for this repair. See [follow-up evidence](docs/progress/equator-rain-output.md).

## State on 2026-10-09 — rain playback and wall-adjacent night chemlights

Equator/Bay5 now play the native precipitation bank through their original
indoor/outdoor mixer routes, following actual rain, season and acoustic rooms.
The cloned Weather root never included the separate Sound scene blenders.
New live Audio / RainVolume defaults to 1 on each map; shared ambient-bus leases
preserve scenery/room-tone cleanup order. F10 includes rain source signal probes.

Eight Equator night chemlights moved toward wall/cover edges; the van-side green
one was already close. All nine default to strength 1.6, with a selective once-only
migration from exactly 1. Custom/disabled values remain. Native review corrected
the north-west light to (-18,14), yaw 0 in front of the accessible sandbag end.

GPU-2 accepted Equator Night/Rain -> Bay5 native light rain -> Equator Day/Clear,
including native nonzero sound signals, room selection, live mute/restore,
all nine visual placements, day exclusion and raid cleanup. Final process
`f4e0303e` is at MainMenu, 20/20 loaded identities match. One known inactive
MenuScreen coroutine error occurred during debug exit; no rain/light exception.
This was playback/signal validation, not an audition of subjective loudness.

Release client has 0 warnings/errors; 577 .NET tests and distribution check pass.
Both sanctioned installs received the verified DLL-only pair. Client SHA256
`1817D2FBD5578D60785E85FBD6F941B5B96017EC1C46B2EE27A99EDF2FEDC38F`;
server `5D2AD68B07110B9DAFCA07B3591ED2F93266273537D4CBF741B2E38234BC0F2C`.
Only the intended host accent default changed; all other existing host and
vanilla settings were preserved. Restart vanilla client/server. Evidence,
controls and limits: [rain and accents](docs/progress/equator-rain-accents.md).

## State on 2026-10-07 — independent Equator Day/Night profiles

Equator keeps one `arena_equator` identity with 12:00/00:00 selection. Each time
now has 43 independent appearance controls: `Lighting (arena_equator Day/Night)`
and `LightingLab (arena_equator Day/Night)`. Day starts from accepted realistic
source defaults; Night from the user's GPU tuning captured on 2026-10-07. The
separate Bots sections default to SAIN Factory / FactoryNight (Factory_Night is
accepted as an input alias). Legacy entries remain saved/hidden. Common repairs,
audio, geometry, Bay5 native time/weather and ArenaMode/DynamicMaps IDs remain.

Release client build, full regression suite and distribution validation passed,
with independent parent verification. GPU-2 process `56d3c1e6` passed Day -> Night
-> Day -> Bay5, including live SAIN aliases, camera values, inactive-profile edits,
night-neon independence and F12 HDR preset isolation. All temporary edits restored.
The existing 800 host / 804 vanilla config assignments remain unchanged; host has
88 additional profile entries, all matching the intended defaults.

Both sanctioned installs received the tested DLL-only build. Client SHA256
`FB2E8D6D1B5F2B0D12C0D398CFE75E16E704B01BF3853B2CD013F9CB518E0E97`;
server `85C4E852AC497D84107464944A6BFAE82ABC6B52E8A3E3E6EAE450C19EE987EC`.
GPU-2 is at MainMenu, 24/24 loaded identities match. Restart vanilla client/server
to load the update and create its profile entries. Existing texture/LOD warnings
and one previously documented inactive MenuScreen coroutine error occurred; no
profile/SAIN/render exception. See [full evidence](docs/progress/equator-profiles.md).

## State on 2026-10-03 — Arena 0.6 exporters and restored Bay5 weather

**Current policy:** Equator 12:00 always Clear; 00:00 retains optional neon rain.
Bay5 uses the two native server clocks twelve hours apart, the advancing raid
clock and dynamic backend weather. Saved fixed-hour/test-weather settings cannot
override Bay5. This supersedes the fixed Bay5 times in the earlier section below.

GPU-2 acceptance passed in process `0126ddc9` (20:06:12 start): native Bay5 menu
clocks and Next/Back selection, a PAST raid with TimeFactor=7 and unlocked,
advancing sky, and changing backend rain/cloudiness over a 54-key curve.
Equator Day remained Clear even with temporary Weather(test)=Rain, including
the backend re-sync. Midnight's rain menu icon and day sun icon both passed.
No new midnight raid or full 24-hour cycle was observed in this test round.
Both botless PMC raids ended via normal extraction. No Error/Fatal in the
captured session. GPU-2 is back at MainMenu with server/client running.

Release DLLs deployed through sanctioned `-DllOnly` to D:/SPT41 and then
D:/SPT41vanilla. Client SHA256
`F60F1181589E992F6AE445477AD7501F6161A3D6352617DC8E96D15DDC585DE1`;
server `D17E1A9D213C4526A23C938309D051FCFFFDD2D8472A99EFC86726250D07E040`.
All 800 host and 804 vanilla saved MapPort assignments remain identical.
The user's vanilla server/client need restarting to load these DLLs.
Client build, 505 .NET tests and existing distribution validation passed.
See [native evidence](docs/progress/map-weather-restoration.md).

Arena source is now **0.6.0.47752 / Unity 6000.3.6f1**. Existing UnityPy works;
fixed banner/loading IDs, UI sprite tier selection and seven generic source
plans. Full fresh Equator/Bay5 extraction and native structural validation,
auxiliary exports and all twelve plan mappings passed; Python: 665 tests run,
44 optional skips. Accepted packages/bundles/art/audio remain unchanged.
Fresh audit packages are not accepted build inputs; no new-source bundle or
visual acceptance was performed. Three additional source Bay5 decals were
recorded, not promoted. Keep SPT's Unity 2022 target. See
[export audit](docs/progress/arena-update-20261003.md).

## Earlier state on 2026-10-03 — Bay5 time choices and Equator rain preview

Bay5 now uses native 12:00/00:00 choices, with matching actual raid start time and
server night-equipment selection. Equator midnight previews the existing optional
neon rain in the native weather icon, restoring the original icon/visibility on
day selection. Saved time/lighting/weather values are unchanged; only internal
FixedTimeSelectionRevision advances to 2 on next client start.

The coordinating ArenaMode task reverified 487 tests, the clean client Release
build and ten DLL-only fixtures, then deployed source `1cb4572` (menu `aa0a8c1`)
locally and to GPU-2. Native 1080p menu acceptance passed in process `52a36096`:
Bay5 keeps night after Next/Back; Equator immediately switches rain/sun on time
selection and restores native Factory weather; the ordinary PMC screen is intact.
Independent reviews found no open concrete correctness issue. No raid: Bay5's
night lighting, in-raid clock and bot gear were not natively tested here.

Client SHA256 `794B5AE286A30F64F0440B6315B1ACEED168322668C54A596E3050B5BD27CABA`;
server `3D54A40A79174F894BEDA0475F4F746482C6DDB961BB80E11FAF08D6AFF34E7F`.
No bundles were rebuilt/copied, prior saved tuning was preserved, and game/server
are stopped again. Report, evidence paths and the later parallel UiBridge disk
update are documented in [map-time-weather.md](docs/progress/map-time-weather.md).
This acceptance follow-up changes documentation only; installed DLLs remain from
`1cb4572`, with no build after the follow-up commit.
The sanctioned deploy script now offers `-DllOnly` for exactly the existing
client/server DLL pair, dry-run by default and exclusive-lock/hash/rollback
protected. Do not use the default whole-dist mode for a two-DLL update. See the
same progress note for the tempfixture verification and failure limits.

## State on 2026-10-01 — chemlights moved into darker areas

After user acceptance of the night effects, six F10 marks identified chemlights
only 2.81–3.60 m in front of the added cyan column lights. Those six now occupy
shadowed passages, crate/cover sides and the southern atrium floor, selected in
an actual GPU-2 midnight/Rain raid. Three unmarked accents remain; nine total,
same brightness/range/rendering cost and unchanged saved settings. See current
positions in `tools/maps/equator.accents.json`; `east-lane` is now `atrium-south`.

GPU-2 process `b4e1e179`, 24/24 loaded identity, all six native placement views
accepted (screenshots 13–18 under `analysis/screenshots/equator-accent-layout-2026-10-01`).
Release client clean, 471 tests and distribution validation pass. Full evidence:
[placement follow-up](docs/progress/equator-night-accents-wet-floor.md#placement-follow-up--2026-10-01).
Both sanctioned installs deployed and hashes verified; both configs byte-identical.
GPU-2 is back at MainMenu, no raid. Restart vanilla EFT to see the moved sticks.

## State on 2026-10-01 — Equator night chemlights and wet floor

The optional Equator 00:00 neon installation now adds nine native decorative
chemlights (cyan/magenta/amber/green), each with a small shadowed point light.
Live F12 `Night neon (arena_equator)`: `AccentLightIntensity=1` (0 removes them)
and `WetFloorStrength=0.8` (0 restores the saved floor baseline).

Important source finding: native SMap shader field names are inverted.
`_Specularness` writes actual G-buffer smoothness; `_Glossness` contributes to
specular RGB. The new night effect adjusts only the verified SMap floor/detail
materials through the existing material owner. Existing day controls and their
historical behavior remain unchanged. See the progress note before touching them.

GPU-2 final process `dec34a68`, 24/24 loaded identity: stronger/sharper floor
reflections, all nine accent placements, off/on cleanup and exact material
restoration in the subsequent 12:00/Clear raid passed. Release client builds
cleanly; 471 tests and distribution validation pass. All 798 existing host config
assignments stay unchanged, including the user's latest strong haze tuning.
Vanilla config remains byte-identical; restart vanilla EFT after delivery.
Details, hashes and native comparisons:
[equator-night-accents-wet-floor.md](docs/progress/equator-night-accents-wet-floor.md).

## State on 2026-10-01 — Equator night distance haze and temporary PostFX

The optional midnight neon installation now owns a subtle full-map native distance
fog pass (`DistanceHaze=0.002`, 0=off) and an automatic balanced camera-only Chillwave
preset (`PostFx=true`). Both are live F12 controls in `Night neon (arena_equator)`.
The saved native PostFX group is never modified by the feature; native settings and
preview temporarily regain control, and current user settings return on off/exit.
The earlier 14 experimental ArenaMapPort image values are preserved and still
affect both Equator times; this change does not migrate those values into night-only
controls. The user's current `FlickerFrequency=3` also stays unchanged.

GPU-2 accepted the final build in Equator 00:00/Rain and a subsequent 12:00/Clear
raid: visible restrained haze, live haze/PostFX/master off/on, native settings
suspension, saved user edits, and zero owned fog components after exit/day.
Client Release: zero warnings/errors; 443 policy tests and distribution checks pass.
Both sanctioned installs receive the verified build; vanilla EFT needs a restart.
Details, hashes, evidence and test limits:
[equator-night-haze-postfx.md](docs/progress/equator-night-haze-postfx.md).

## State on 2026-10-01 — Bay5 DynamicMaps source consolidated

The user requested integration of the completed `feature/bay5-dynamicmaps` work
and retirement of `D:/ArenaMapPort-DynamicMapsBay5`. Both source commits (`dd0dce0`,
`308636b`) are integrated with the current `feature/hdr-image-controls` branch.
The exporter supports both maps; the newer labeled north compass and explicit
`-DynamicMapsMap Bay5` deployment selection are retained. Historical Bay5 host
acceptance is now in [bay5-dynamicmaps.md](docs/progress/bay5-dynamicmaps.md).

Continue from `D:/ArenaMapPort`. The old checkout's complete file snapshot is
preserved under ignored `work/archives/ArenaMapPort-DynamicMapsBay5-20261001/`
with a SHA256 manifest; its operational data and native evidence are also copied
under `work/dynamicmaps/`. These local game-derived files are not in GitHub.
The old receipt paths remain historical evidence; use the archive to resolve
paths that named the retired checkout. No runtime source, game installation,
configuration or accepted map package was changed by this source integration.

## State on 2026-09-30 — selected Equator neon lights flicker

The user requested a trial. Three fixtures now have independent, occasional
short faults: north-west column, right TECHLIGHT strip and left south-sign strip.
The existing midnight-only gate and other nine steady lights remain. New live
F12 controls in `Night neon (arena_equator)`: **FlickerStrength=0.8** (20% minimum;
1 permits brief blackouts), **FlickerFrequency=1** (about one burst per 20 seconds
per fixture, without changing pulse duration). Either 0 restores steady light.
One envelope drives the actual light and per-strip material override; native
haze visibility recovers in the same frame. No new lights or rendering passes.

Client SHA256 `2A9A157ECC72D092C64829B0463F7698FAA557DF3B8F1549C66C394B6B7EC18B`;
server `49CE01FEDBCADD137A347D159437A28998D963CE53396D4376D693FEC16DB48C`
(shared descriptor rebuilt; no server behavior/database change). Client Release
clean, 443 tests passed, distribution validation passed. GPU-2 `1ac521ec`, 24/24
loaded identity: four 40-second native samples verified defaults, full blackouts
and both zero/off controls with zero light/emission error or haze mismatch.
Native captures07/08 show the actual blackout and recovery. Full off/on and
regular extraction released owned resources; subsequent day raid had zero neon
objects and Clear weather. No ArenaMapPort warnings/errors; the existing Unity
inactive MenuScreen coroutine error reappeared on debug pause, documented separately.

Both sanctioned installs deployed, 2239 files each; hashes match dist. All 794
existing host assignments retained, exactly two new keys; vanilla config byte-identical.
User game/server restart needed there. Final GPU state: MainMenu, no raid, 440/440 HP.
Quiet default/off samples stayed at 120 FPS cap, 8.3 ms mean/p95; uncapped cost is
not measured. The user's judgment of the rhythm remains pending. Native evidence:
`analysis/screenshots/equator-night-flicker-2026-09-30/` (ignored).
Details: [equator-night-flicker.md](docs/progress/equator-night-flicker.md).

## State on 2026-09-30 — local haze and smoke for Equator night

The user accepted the initial neon look and requested fog/smoke. The existing
optional Equator 00:00 installation now owns 12 native volumetric spotlights
and 8 shallow drifting smoke banks (maximum 256 particles). Two new live F12
controls under `Night neon (arena_equator)`: **HazeStrength=0.16** and
**SmokeDensity=0.65**, each independently disabled with 0. Haze respects native
Graphics -> Volumetric Light; smoke uses a cloned native EFT material/atlas and
has no grenade/AI behavior. No original asset, global fog or graphics change.

Final client SHA256 `361A61C60CE7F1369BA39778A06ACA00BF98E5849C1A0101A52AC7D1FCAA8C86`.
Release build clean; distribution validation passed. GPU-2 process `c692bb08`
loaded identity verified (24/24). Night controls/off-on/recreation and normal
extraction passed; attached light-buffer count fell from 12 to 0 on release.
Next Equator day raid: hour 12, zero neon roots/lights, Clear weather, no atmosphere.
No ArenaMapPort warnings/errors; one Unity inactive MenuScreen coroutine
error occurred on debug pause and is recorded in the progress log. Final host is
MainMenu, no raid, 440/440 HP. Quiet samples stayed at the 120 FPS cap with effects
off/on; uncapped incremental cost and exhaustive motion/sorting are not measured.

Both sanctioned installs deployed (2239 files each); client/server hashes match
dist. All 792 existing host config assignments retained, exactly two new keys;
vanilla config byte-identical. User EFT restart needed on vanilla. No bundle or
server rebuild; this client-only follow-up did not rerun the previous 420 tests.
User visual acceptance is pending. Native PNGs and settings/player metadata:
`analysis/screenshots/equator-night-atmosphere-2026-09-30/` (ignored).
Details: [equator-night-atmosphere.md](docs/progress/equator-night-atmosphere.md).

## State on 2026-09-30 — optional Equator night light installation

Following the image-only experiment, Equator now has an optional installation
of 12 cyan/magenta emissive strips with 12 real, shadowed spotlights. It activates
only for confirmed Equator **00:00**. **12:00** retains the realistic appearance;
other maps are excluded. New F12 group `Night neon (arena_equator)` has Enabled,
LightIntensity **1.5**, StripBrightness **0.6**, CyanColor/MagentaColor and Rain.
Rain defaults on for the enabled night variant only. Existing image settings,
native PostFX and original lighting baselines are retained. No bundles rebuilt.

Release build: zero warnings/errors. All **420 tests** and distribution validation
pass. Client SHA256 `C024E13AB6A4F5409B0623F3380F52BD9C312787C5A1DC5CE0B0AF9659B86C4E`.
GPU-2 instance `7560c40a` loaded identity verified. Night live off/on, colors and
independent light/emission controls checked; final process checked day -> Bay5 ->
night -> day with expected weather and zero owned objects outside night. Scene
unload released the installation. No ArenaMapPort warning/error in the final
process's retained log. Regular night raids used North Gate extraction; final
state MainMenu, no active raid, 440/440 HP. The capped 120 FPS samples do not
measure uncapped GPU cost. User visual acceptance remains open.

Both sanctioned deployments completed (2239 files each), client/server hashes
match dist on both installs. All 786 pre-existing host config assignments remain
unchanged; six keys added. Vanilla config is byte-identical. User game/server
restart required there. Native screenshots and matching config/player JSON:
`analysis/screenshots/equator-night-lights-2026-09-30/` (ignored).
Testing: normal PMC -> Equator -> **00:00**; debug shortcuts still choose day.
Details: [equator-night-lights.md](docs/progress/equator-night-lights.md).

## State on 2026-09-30 — reversible Equator night/neon experiment

On `33abe0d`, GPU-2 instance `9440e5bf`: tried cyan/purple/pink treatments on
Equator 00:00/Rain with the existing live controls and EFT PostFX. Vivid and
balanced recipes plus native comparisons are saved under ignored
`analysis/screenshots/equator-neon-2026-09-30/`. Close-up letters overflare in the
vivid version; balanced reduces this. All 76 original captured config entries
and original PostFX were restored and verified. Regular North Gate extraction,
MainMenu, 440/440 HP, no active raid. No code/default changes, build or deploy.
Details: [equator-night-neon.md](docs/progress/equator-night-neon.md).

## State on 2026-09-29 — accepted Equator defaults; Bay5 streetlamp source audit

Follow-up on `aa3d953`, branch `feature/hdr-image-controls`. The user's accepted
Equator tuning is now the actual fresh/F12-reset default: custom Filmic
`(7,0.35,0.7)` / `(-0.02,0.18,0)`, ambient 0.5, reflection 0.85, indoor reflection
0.95 and anamorphic strength 0.4. Global lamp 0.5 and bloom 0.35 / 2.1 were already
defaults. WeatherRig revision 5 plus hidden HDR/flare revision 1 migrate exact
legacy defaults once; the four tone entries migrate only as a complete neutral
tuple. Tuned values remain intact. Bay5 defaults and native preset buttons retain
their existing behavior.

Bay5 audit: 15 original streetlamp roots (11 `fonar_stolb`, 4 `street_lamp2`),
157 descendant source objects; no Light, lamp controller or flare component in
those hierarchies. Principal materials have no emission texture. Other native
lights are separate and already ported. No original Arena nighttime run was made,
so deliberate difficulty is not established. No Bay5 lighting changes requested
or made. Source evidence is in ignored `work/bay5-streetlights-audit/`.

Client SHA256 `4A5238EA96D2770737340D939ABA8F37F12236C46AED642CBEF9066DD18B2D7F`.
Release build clean; 403 tests pass; distribution validation passes. GPU-2
`a06cb840` loaded identity verified; Equator Day/Clear image and shader parameters
confirmed (22:50–22:51 CEST), no ArenaMapPort warnings/errors. Returned to MainMenu,
no active raid/test hooks. Local screenshot + metadata under
`analysis/screenshots/equator-defaults-2026-09-29/`.

Both sanctioned deployments completed (2239 files each). Saved host values are
unchanged except internal revision 4 to 5 and two new migration markers; vanilla
config remains byte-identical. User EFT restart required for the new DLL/defaults.
Independent SHA256 comparison matches all 2239 staged files in each installation.

Details: [equator-defaults-bay5-lights.md](docs/progress/equator-defaults-bay5-lights.md).

## State on 2026-09-29 — Equator floor-detail gloss

Follow-up on `aa18519`. The user's Equator tuning now includes ReflectionIntensity
0.85, IndoorReflectionStrength 0.95 and AnamorphicFlareIntensity 0.40, applied via
UiBridge and added to the comparison record. No additional program defaults changed.

Small floor tiles/shards inherit the actual supporting indoor floor's classification,
with one decision per LODGroup. Descriptor/sidecar opt-in is Equator-only. All 512
details now use IndoorFloorSmoothness; authored material/texture differences remain.
Ground scanning caches each material once; indoor baselines are independent of ground
settings at startup and reclassification. Bay5's existing surface partition is retained.

Client SHA256 `A5B95A4D2A1DC0AEC250E64151DA4CB24C81456EC9D8CC4DFCB9E7F92B63454A`.
Release build clean; 403 tests pass; distribution validation passes. GPU-2 `73ab5e5e`
loaded identity verified; Equator -> Bay5 -> Equator checked (22:13–22:22 CEST), including
zero/half ground factors, live indoor values, reclassification, all three formerly
inconsistent shard LODs and parquet preservation. No ArenaMapPort warning/error.
Both sanctioned installs match all 2239 staged files by independent SHA256 checks;
vanilla saved config remains byte-identical. User EFT restart required.
GPU-2 is back at MainMenu; no raid/test hooks remain. Current user global light
multiplier 0.5 was preserved (the previous screenshot recommendation used 0.6).

Details: [equator-floor-gloss.md](docs/progress/equator-floor-gloss.md).
Local original screenshots and settings:
`analysis/screenshots/equator-floor-gloss-2026-09-29/VERGLEICH.md`.

## State on 2026-09-29 — tonemap sliders and Equator image comparison

Follow-up on `652755e`, same feature branch: six X/Y/Z sliders plus precise number
boxes per supported map, keeping the original Vector3 keys and live renderer.
F12 Advanced must be enabled. `Use <profile> preset` copies both native vectors
and enables custom tuning. Fixed ConfigurationManager metadata type-name matching.
Bloom defaults now 0.35 intensity / 2.1 threshold on Equator/Bay5, with hidden
once-only migration of exact legacy defaults.

Client SHA256 `D7B2605649934A234132ABFFBBAFAED12F2D620A94A0C570AABECA8607BDC278`.
Release build/distribution check passed; GPU-2 `c5b90dcb` loaded identity verified.
All six UI axes, numeric input, profile copy, Reset and live shader upload checked.
Equator Day/Clear comparisons at `(0,0.1,14)`, yaw 180/pitch -10, plus TECHLIGHT.
Recommended GPU-2 settings: custom Filmic `(7,0.35,0.7)` / `(-0.02,0.18,0)`,
ambient 0.5, global lamps 0.6, anamorphic intensity 0.18, normal bloom; other
lighting values at reset defaults. This is saved artistic tuning for user review,
not additional source defaults. Night/rain appearance has not been evaluated.

Local screenshots and exact live values:
`analysis/screenshots/hdr-tuning-2026-09-29/VERGLEICH.md`.
Implementation and acceptance: [hdr-sliders-tuning.md](docs/progress/hdr-sliders-tuning.md).
Both sanctioned installs contain all 2239 files, independently SHA256-matched.
GPU-2 restart `7e235171` preserved the recommendation and a temporary native bloom
fallback, proving migration runs once; Bay5 intensity then restored to 0.35.
Host is at MainMenu. User EFT restart required to load the deployed client.

## State on 2026-09-29 — HDR image controls

Branch `feature/hdr-image-controls`, based on `c23fea6`. Seven new live controls
per map (Equator/Bay5): relative exposure target, exposure limits, adaptation
speed, Original/Filmic/RomB/ACES/Off and three advanced custom-curve entries.
Equator adds flare length, threshold and RGBA tint. All 17 defaults preserve
the previous look. Existing settings and optional Arena audio/UI assets remain.

`HdrImageTuning` stages the current frame's values after EFT's environment
overwrite, restores component fields after material upload and changed global
tone keywords after rendering. Exact main-camera/map gating excludes optics,
master-dependent cameras and other locations. F7 distinguishes native inputs,
effective material values and observed shader keyword state. Exposure bounds
scale about zero: a midpoint-based candidate blacked out at normal lower values
with EFT's asymmetric [-10,0.9] interval and was corrected before acceptance.

Final client SHA256 `F1B6CCB57A16BF36CDAC96C8525792A88CC84452DDECA4469FD536A244ECA878`.
GPU-2 `9c899e03`, 19:04–19:11 CEST: Equator -> Bay5 -> hideout -> Equator,
live controls, native indoor/outdoor transitions, independent map values,
outside-camera exclusion, repeated camera creation and neutral restoration.
Earlier same-day candidate covered all tone profiles/custom fallback and flare
length/tint/threshold/alpha/disable-reenable comparisons. No ArenaMapPort warnings
or errors. Host returned to MainMenu. Existing host values 865/865 preserved;
17 new entries neutral. Details and evidence limits:
[hdr-controls.md](docs/progress/hdr-controls.md).

Release client built; 397 server tests, 639 pipeline tests (28 expected skips),
eight PowerShell log cases and distribution validation passed. No Unity rebuild.
The package now contains 2239 files including prior Arena audio/UI extraction,
so older 103-file deployment totals below are historical.
Both sanctioned deployments to `D:\SPT41` and `D:\SPT41vanilla` completed;
independent SHA256 verification matched all 2239 files in each installation.
All 804 saved vanilla assignments are unchanged. Restart any already running
user EFT instance; GPU-2 has the verified client loaded and is idle at MainMenu.

## State on 2026-09-29 — Arena content survey and Arena audio extraction for ArenaMode

Branch `feature/arena-audio`, based on `baa5913` (`cleanup/obsolete-config-options`). A read-only survey of
EFT Arena beyond the maps (seven areas, per-area verification, critic pass) is documented in
[docs/ARENA-CONTENT-FOR-ARENAMODE.md](docs/ARENA-CONTENT-FOR-ARENAMODE.md): worthwhile are mainly audio, a few
UI sprites and map-side data for ArenaMode's planned Control Point mode; Arena code is IL2CPP with encrypted
metadata and rule numbers are server-side; Arena bundles are Unity 6000.3.6f1 (skinned cosmetics are out).

First slice: `tools/extract/extract_arena_audio.py` + `tools/extract/arena_audio_catalog.json` extract the
English commentator (categories classified by the bank's own event tags, validated at every run), EFT's own
dormant `arena_start/win/lose` stingers and small Arena clip groups into `work/extract/arena-audio` (1224 OGG,
56.6 MiB, one silent source clip skipped); the client csproj stages it optionally into
`dist/.../ArenaMapPort/arena-audio/`. ArenaMapPort never plays it: the manifest is a contract for ArenaMode
([docs/progress/arena-audio.md](docs/progress/arena-audio.md), with a suggested cue mapping). Integration is
ArenaMode's own work (that repo is read-only here). Next on this side: map-side Control Point data
(capture anchor, SPAWN_BOUND boxes) once ArenaMode builds Control Point slice 0.

Later the same day (user request): the audio group `other` exports everything unassigned (15 excluded
commentator categories + 318 Arena-only clips such as menu UI, popups, music, MVP and Blast Gang SFX; 751 clips,
93.7 MiB; RU/Geneburn/Tagilla voice banks, about 1.6 GiB, only on request), and
`tools/extract/extract_arena_ui.py` exports 159 Arena UI sprites to `work/extract/arena-ui` (PNG, manifest,
`previews/` contact sheets; staged without previews into `dist/.../ArenaMapPort/arena-ui/`), see
[docs/progress/arena-ui.md](docs/progress/arena-ui.md). The survey's "contested" rating of the killfeed icons
was wrong (they are weapon/kill-method icons, not faction icons) and is corrected in the survey doc.

## State on 2026-09-28 — configuration cleanup

Branch `cleanup/obsolete-config-options`, based on `695bb5e`. All 39 config-binding
classes audited. Removed 46 ineffective entries (10 formerly visible): old rain
cover flags/threshold, inert custom ambient intensity, nonexistent Bay5 authored
sun controls, nonexistent Equator layered-ground control, and unused Verbose.
Internal default revisions remain stored but hidden. Functional fallback controls
stay, with clearer descriptions. See [config-cleanup.md](docs/progress/config-cleanup.md).

Startup cleanup removes only explicitly retired orphaned entries through the
plugin's own BepInEx Config.Save. GPU-2 comparison: 911 -> 865 saved assignments;
all 865 remaining values identical. Bound entries 811 -> 765 after lazy audio
binding; Equator 67 -> 63, Bay5 64 -> 59. User light/audio tuning is preserved.

Client SHA256 `AEDD90206509B7B3A1278B48286D090D2E6D7BB47F8B94F3087DF653FFBF33EA`.
GPU-2 `55e2916a`, 20:16–20:21 CEST: exact loaded build, both botless raids,
roof/cover/shadow/flare/decal checks, no ArenaMapPort warnings/errors. Returned to
MainMenu. Clean Release build; BepInEx cleanup integration check, 479 Python tests
(28 expected skips), 397 .NET server tests and distribution validation passed.
No Unity rebuild; accepted v5 inputs remain current. Saved-key cleanup runs on
game startup, so the user's own install needs an EFT restart.

Both sanctioned deployments completed; independent hashes match all 103 staged
files in each install. Vanilla config untouched by deployment, pending its next
game startup for automatic saved-key retirement. GPU control is free.

## State on 2026-09-28 — lighting follow-up

Branch `fix/lighting-controls-shadows`, based on `432d22b`. New per-map
`NativeMultiFlareIntensity=0.5` (0–1, live) for Equator and Bay5. Automatic
exposure is already active on both maps. Equator's reported Techlight sun leak
needed native two-sided materials AND DistantShadow enrollment on three existing
ceiling proxies (`RoofDistantShadows=true`). Bay5's adjacent `(1)` sand-filled
barricade now receives the same original shadow restoration as its neighbor.

SmajlecLights Apply/Restore writes are excluded on all fourteen registered ports;
the mod remains installed and operates outside them. BrighterInteriors is left
alone: both current maps have zero active native stencil overlays. F7 reports
compatibility counters, stencil inventory and actual roof-cache membership.

Final client SHA256 `23243C89490BC68FA1B7943976FFBDAB7D6DF42E47AFAC0BAA7BA511DD26A7DF`.
GPU-2 `5eec1f91`, 18:55–19:07 CEST: exact roof on/off/on causality, both
barricade shadows, Bay5 flare 1/0/0.5, Equator -> Bay5 -> hideout -> Equator,
outside-port positive control, and material cleanup passed. Equator slider and
Smajlec Apply/Restore toggles were also tested in preceding candidate `44d8cf4b`.
All 47 plugins/five libraries match actual loaded code; no ArenaMapPort warnings
or errors. Build: zero warnings/errors; 397 server tests; 479 pipeline tests
(28 expected skips); distribution 14 maps/42 bundles/14 sidecars valid.

Both installs receive the tested 103-file package via the helper. Host returned
to MainMenu. Sliders=0.5, roof fix=true, prior settings preserved, Smajlec enabled.
Six user ambient adjustments received during testing were retained. Restart the
user's game. No Unity rebuild; keep current v5 inputs. Exact source causes, test
coordinates, evidence limits and deployment notes:
[lighting-followup.md](docs/progress/lighting-followup.md).

## State on 2026-09-28 — original MultiFlare

Branch `feature/native-multiflare`, based on accepted room-reverb `6164695`.
Both maps now use original per-light flares: Equator 25 active lights / 33
elements, Bay5 24 / 111. Original inactive lights stay inactive. New live
`Lighting (<map>) / NativeMultiFlare=true` is independent of camera bloom/HDR.

Ignored inputs: `work/extract/multiflare/arena_equator` and `arena_bay5`, generated
by `tools/extract/extract_multiflare.py --map <key> --output <dir>`.
Six files stage under plugin `flares/`, including the original BC7 atlas (EFT's
atlas differs). No v5 bundle rebuild. Source provenance and the one pinned,
semantically unidentified settings scalar are documented in the progress note.

The native renderer owns separate storage/batches/camera buffers; it never resets
the global weapon-light manager. F7 includes completed-frame native flare counts.
Client SHA256 `856E40A9B0E06E8964C590AC8E1C0702DA2DA4C0C1466858ADC742F7CF2955EF`.
Build 0 warnings/errors; 478 pipeline tests (28 expected skips); distribution
validation passed. GPU-2 `236edef3`, 16:42–16:52 CEST: Equator -> Bay5 -> Equator,
visible off/on, depth occlusion and all three raid cleanups passed. Empty native
FlashLight batch works; no new flare exceptions. Optic/NVG/eyewear paths are
source-reviewed but lack dedicated equipped visual comparisons.

Both installs receive the tested 103-file package via the deploy helper. Final
host state MainMenu / control free, all 23 plugin identities identical. Both
flare toggles on; all prior 11 changed ArenaMapPort settings preserved. The
user's own game needs a restart. See [native-multiflare.md](docs/progress/native-multiflare.md).

## State on 2026-09-28 — room-dependent ambient reverb

Branch `feature/room-reverb`, based on spatial-acoustics commit `0b841c1`.
Both maps now use the original 26 room-dependent ambient reverb/delay/send
parameters. Exact native room types are mapped to source indoor/isolated/outdoor
categories. `Audio (<map>) / NativeRoomReverb=true` is live and requires the
original spatial graph. The additive `mixer_effects` data is extracted into the
existing graph JSON; still six acoustic files, no bundle rebuild.

Important compatibility finding: EFT permanently mutes the original
Returns/Reverb group used by Equator ambience (Arena leaves it unmuted).
Only Equator's 23 imported active ambient players use the functioning original
AmbCommonEffects route while enabled, with their dry path preserved and guarded
restoration. Bay5 keeps CommonAmbOutEffects. Gameplay/MetaXR/SurroundAudio are
unchanged; this work does not claim restored gun/step reverb.

Final client SHA256 `963EFD5A0060DFFB77673B9C8F661F61C38620787107189C24394638F0D972D0`.
Build zero warnings/errors; 461 pipeline tests (28 expected skips), distribution
validation passed. GPU-2 `c09a0c2b`: Equator -> Bay5 -> Equator passed 15:48–15:55
with actual map-bus pulse tails, live off/on, original source routing, outdoor
and detailed-room transitions, complete teardown and no new audio errors.
All 97 package files hash-match both installs; all 23 host plugin identities match.

Both hall switches on; diagnostics off/default bus, prior 11 changed settings
and all SurroundAudio settings preserved. Host MainMenu/control free. Digital
output is verified; subjective balance remains a listening check (Windows
output muted). Restart the user's game. Full evidence and limits:
[room-reverb.md](docs/progress/room-reverb.md).

## State on 2026-09-28 — native spatial acoustics

Branch `feature/spatial-audio`, based on accepted water commit `12b0363`.
Equator/Bay5 now load their original acoustic graph and propagation bake into
EFT's native spatial system: 21/40 active rooms, 50/85 portals, 210/706 room
pairs and 31,676/5,924 complete routes. Native gain/filter application is active.
Source indoor room tones are restored; existing ambient players remain intact.
Full room-dependent mixer reverb and outdoor ambience blending remain separate.

Inputs: ignored `work/extract/spatial-audio/<map>`, generated by
`tools/extract/extract_spatial_audio.py`. Six files stage under plugin `acoustics/`
even on DllOnly builds. No v5 scene-bundle rebuild. Do not commit source audio/data.
F12 `Audio (<map>) / NativeSpatialAudio=true` applies next raid. Missing/invalid
packages fall back safely; native initialization has a 30-second timeout.

Important runtime detail: **AudioClip.Create/SetData must happen after raid start**,
not merely playback. The game's AudioSettings.Reset discards procedural clip data.
Owned clips are recreated on later audio-configuration changes, on the main thread.
Bay5 temporarily opens the indoor bus with guarded restoration on unload.

Final client SHA256 `2207A864D75DC72ADC8850B63BF111DFC6767800A9F7CF3DA03B860467079098`.
Client: zero warnings/errors; Python pipeline 445 tests (existing expected skips),
including 20 new spatial tests; full distribution validator passed.
GPU-2 `a0b19549`, all 23 VM/host/loaded plugins identical: Bay5 -> Equator -> Bay5
passed 13:01-13:08 CEST, actual clip playback/sample advance, indoor/outdoor
classification and real native attenuation. All three teardowns returned
Initialized false and group count zero. Only the known MenuScreen coroutine
message appeared; no new audio errors. Host output was muted: listening balance
remains a user check. Original volume coverage is retained; one Equator north
spawn is outside authored rooms. Do not fill such gaps with invented data.

Both switches remain true; silent diagnostics are off and their position is zero.
Saved lighting and Clear weather settings are preserved. Final installation
hash verification and menu state: [spatial-audio.md](docs/progress/spatial-audio.md).
Restart the user's game to load the new client DLL; server code is unchanged.

## State on 2026-09-28 — Bay5 harbor water and puddle footsteps

Branch `feature/bay5-water`, based on the accepted native-decals build `17e3d91`.
Bay5's white night harbor was the old bare SimpleWater4 placeholder. The client
now transfers original WaterContainer settings plus four raw texture mip chains
to EFT's native deferred water renderer. Day/night live comparisons on GPU-2
confirmed waves/reflections and appropriately dark night water. One owned renderer
is cleaned up completely on toggle and raid unload; original state is restored.

Bay5's reviewed water-puddle material now selects native Puddle footsteps using
its real triangle/UV/red-channel mask. No new collider or audio file is created.
Dry holes, oil and other base materials remain unchanged. Current historical
comparison found no lost water collider; the earlier perceived sound change is
not conclusively explained. GPU-2 confirmed actual walk_puddle/sprint_puddle banks
on wet points, Concrete on dry points, and Soil/Metal preservation. Rain alone
still chose Concrete when the new translation was switched off.

New F12 controls: `Water (arena_bay5) / NativeHarborWater` and `PuddleFootsteps`,
both true and live. F10 includes water lifecycle and detailed footstep diagnostics.
Current v5 scene bundles are retained. Additional input is ignored
`work/extract/bay5-water`, regenerated with `tools/extract/extract_water.py` and
staged even on DllOnly builds. Do not commit its BSG-derived settings/textures.

Final client SHA256 `A0BE94D2F86319CAD390C2238421B2A61072538C8E10999C8C08F90D916F4440`.
Build: no client warnings/errors. Tests: Python 425 (28 skipped), server 396 passed.
GPU-2 process `8afea561`: repeated Bay5 raids, live off/on, then Equator smoke test
with unchanged native Concrete bank and no new errors. Visual quality and actual
sound-bank selection verified; subjective listening remains for the user.

Saved Clear weather and Bay5 legacy hour 0 restored. Both new switches stay true.
Both installs received 91 staged files, all independently hash-matched. GPU-2
is in MainMenu with control free; last raid health 440/440. Restart Vanilla's
game/server before testing. No scene bundle rebuild was needed for this change.
See [bay5-water.md](docs/progress/bay5-water.md) for final host/menu/deploy evidence.

## State on 2026-09-28 (00:28) — native projection decals on both release maps

Branch `feature/native-decals`. The original StaticDeferredDecal material references are
recoverable from their own serialized component; the older MeshRenderer-based deferral
was incomplete. Source-backed v5 packages add all 348 Equator / 499 Bay5 components,
retaining inactive originals. Native active registrations: 346 / 468. All existing v4
scene data, asset files and native features were independently compared and preserved.

The Unity importer authors real native components and normalizes the atlas pixel bounds
against the imported texture dimensions. Client shader rebinding now includes component
materials. One owned native renderer handles each raid; live off/unload frees its own
materials, buffers and callbacks. Per-map live comparison: `Decals (<map>) / Enabled=true`.

Both complete bundle pipelines passed, including serialized decal field comparisons.
Final tests: Python 417 (28 skipped), server 395 passed, client Release no warnings/errors.
GPU-2 accepted Equator twice in `f2ac85fd`, then Bay5 -> Equator in fresh `4d438dd4`:
visible original wall/tarp graffiti, live off/on, exact instance counts and repeat/unload
cleanup. No new rendering errors; the known pause-menu coroutine message remains.
Native acceptance is representative, not an inspection of every decal or a performance benchmark.

Both installs received the verified artifacts through the owned deploy helper. GPU-2 is
back in MainMenu, control free, health last verified 440/440. Both decal switches remain
true; the temporary Bay5 test hour 12 was restored to its original 0. Clear weather and
saved lighting/flare tuning are retained. Restart Vanilla's server and game before testing.
See [native-decals.md](docs/progress/native-decals.md) for timestamps, hashes and limits.

## State on 2026-09-27 (22:42) — Equator fixed day/night and artificial sun

Branch `feature/equator-day-night`. Equator's normal map picker now offers fixed start
times 12:00/00:00, including correct confirmation and native weather initialization.
Server night equipment follows the chosen variant. Bay5 retains its existing behavior.
The two exact FAKE_SUN / FAKE_SUN_VOLUME lights follow the native sky, with new live
`Lighting (arena_equator) / NightSunMultiplier=0` (off at night, 0.1=10%, 1=old behavior).
User light/flare tuning is preserved. Legacy TimeOfDay Enabled/ArenaFixedHour values
remain saved for other maps; Equator always uses the new choice (migration revision 1).

Release client and 395 server tests plus Python checks passed. GPU-2 `dd071c9b` tested
Rain night → day → Clear day → Clear night, repeated normal selection and live dimming.
Actual 00/12 hours and both light intensities verified; ordinary lamp values unchanged.
Server required a second restart at 22:23:34 after sync; its new startup hook was then
confirmed in the native console. Existing MenuScreen coroutine error remains.
Weather test setting is now Clear at the user's request. Debug buttons still select day;
night is available through the normal menu. Full evidence and hashes:
[equator-day-night.md](docs/progress/equator-day-night.md).

Both installs received matching client/server files via the owned deploy helper.
GPU-2 is back in MainMenu with HP 440/440; Vanilla needs both server and game restarted.

## State on 2026-09-27 (late) — Equator horizontal flare/HDR and raid-camera lifecycle

Branch `feature/equator-anamorphic-hdr`. The earlier UltimateBloom threshold fix restored a halo,
not the original horizontal streak. `AnamorphicFlareTuning` restores the existing native
BloomAndFlares pass and runs it before Prism converts HDR to LDR (the old fallback did this;
the donor had it after Prism). Equator-only live controls: AnamorphicFlare/HDR=true,
AnamorphicFlareIntensity=0.25. Equator diffuse ambient defaults to 0.85 for slightly deeper shade;
DefaultsRevision 4 migrates only legacy 1. Existing broad-bloom adjustments are preserved.

`CameraRepairHooks` uses SetCamera instead of subscribing once to a CameraManager that is disposed
between raids. Bloom, exposure, scattering and volumetric repairs now apply on later raids too.
State is restored/released on teardown; Bay5's appearance is unchanged. F7 emits separate effect lines.
Release build/tests passed; GPU-2 fresh process `f307691a`, 20:30–20:42: Equator → Bay5 → Equator →
fallback Equator, automatic settings on each camera, visible stripe, live disable restores original
fields, fallback exposure now true. Only the known MenuScreen coroutine error appeared, no render errors.
Deployed to `D:/SPT41` and `D:/SPT41vanilla`; the latter needs the user's EFT restart.
The host is back at the main menu with the donor camera reselected. See
[equator-anamorphic-hdr.md](docs/progress/equator-anamorphic-hdr.md) for hashes, config and native evidence.

## State on 2026-09-27 (evening) — deploy-screen art (10b) and plan item 8 (AI anchors) verified

Deploy-screen art: `tools/extract/extract_loadscreens.py` stages five Arena images per map (loading screen + four
main-menu backgrounds) into `dist/.../ArenaMapPort/loadscreens/` only; `LoadingScreenArt.cs` draws one as the first
child of EFT's `MatchmakerTimeHasCome` screen (caption, progress line and BACK stay on top) and hides it on the
screen controller's `OnClose` (raid visible) — host 17:40–18:09, both maps, debug start and normal menu flow. Plan
item 8: `tools/maps/{equator,bay5}.ai.json` (Arena's real core points, hubs, subpoints) are staged as
`ai/<map>.ai.json`; the client authors them plus the id-0 alias, bots patrol the real hubs (host 17:40–17:56).
The extractor must have run before a deploy, or the maps load behind EFT's default background (no error).
Floodlight glare (item 7 follow-up) fixed: the donor camera's live bloom is `UltimateBloom` (threshold 2.10);
`BloomTuning.cs` sets `Lighting (<map>) › BloomThreshold` per map (Equator 1.25, others -1 = camera's own), verified
18:24 (halo back, letters readable). Next: plan item 9 (release packaging; hide the twelve spike maps on location select).

## State on 2026-09-26 — overview-map north markers clarified

Branch `feature/dynamicmaps-north-marker`, based on `783d065`. Both installed DynamicMaps
drawings now have a visible N and a gold/gray compass needle at upper left. North is world
+Z / map up. Only the old marker was replaced; all other SVG bytes and both JSONC definitions
are unchanged. Equator's generator emits the new marker; the separate updater stages it on
the existing Bay5 pack. The data-only deploy now explicitly supports Equator or Bay5.

27 exporter tests, 69 deploy fixture cases, browser visual review and installed-byte checks
passed. Installed to D:/SPT41 only. Native rendering was not retested: UiBridge-2 is available,
but windows-gpu-2 is absent from this session's tools. EFT remains idle at its main menu and
needs a restart to refresh cached map sprites. See the appended
[north marker evidence](docs/progress/equator-dynamicmaps.md#north-marker-clarification--2026-09-26).

## State on 2026-09-27 (afternoon) — floodlight culling fix and plan item 10 (banners) verified

Commit `364ddb0`: CullingLightObject component schema 2 exports the culling sphere (`_radius`/`_shift`); Equator's atrium
floodlights (Spot, frustum-culled, authored radius 24 m) no longer switch off when the lamp leaves the view (F7 probe
15:39: `Spotlight (13) visible=True intensity=3.76` where it read 0 before). Plan item 10: Arena's `Matchmaker_Banner_*`
art is extracted by `tools/extract/extract_banners.py` into `dist/` only and served by the server mod through ImageRouter
(runtime Banners entry only when the file exists, base.json keeps `Banners: []`); both pictures show on the
location-select screen (host 16:03, server log `location-select banner wired` x2, no `[UNHANDLED]`). The location-select
map stacks all fourteen "ARENA: ..." labels — hide the twelve deferred maps in item 9. Deployed to `D:\SPT41` and
`D:\SPT41vanilla` (vanilla server restart = user; `extract_banners.py` must have run before a deploy, or the maps just
show the default picture). Next: plan item 8 (AI hubs), then 9 (release).

## Notes 2026-09-27 — Arena 0.6.0, puddle footsteps

- **Arena patch 0.6.0 (2026-09-18, arena.tarkov.com/news/patch-060):** no changes to Equator or Bay 5 (they appear only
  in task texts); new maps The Lab Replica / The Lab tactical map, Fort and Iceberg reworked (Iceberg is one of the
  twelve deferred maps), BlastGang reworked, new hit/kill sounds; nothing on engine, lighting or audio systems. The
  local install `D:\Battlestate Games\Escape from Tarkov Arena` is still 0.5.4.0 (files dated 2026-08-18) and every
  accepted package was extracted from it. If the user updates it, new maps can shift the `levelNN` indices the
  extractors and census use (Equator 51–59, Bay5 78–188) — re-verify the scene mapping before any re-extraction.
- **Puddle footsteps (user report, deferred by the user):** Arena's puddle decals have no collider, so they sound like
  the floor under them; EFT has no other puddle/wetness step mechanism. The user still recalls splash steps on Bay5 —
  Bay5's only Water-material collider is the harbor `Plane` (layer Water). Authoring WaterPuddle colliders would be new
  content (layer trade-offs: Water blocks prone, HighPolyCollider needs a NavMesh-bake exclusion).

## State on 2026-09-26 (late) — plan item 7 built, verified and deployed (light culling / volumetrics / flicker)

Branch `feature/native-dust`. Item 7 went the extractor + importer route (Arena tunes fade distances and
`_maxLightIntensity` per light, so client-side class defaults would have flattened it): `tools/extract/extract_light_culling.py`
decodes `CullingLightObject` / `VolumetricLight` / `EFT.Visual.LightFlicker` from the raw bytes (three trailing
`AnimationCurve` ints; the `CullingObject` lists are consumed, counts only), `LightCullingImporter` attaches them in three
ordered passes, validators/roundtrip/tests extended (345 Python tests). New accepted packages `work/extract/{equator,bay5}-native-v4`,
both maps rebuilt. Client: `CullingManager` fallback (`EnvironmentRepairs`), `VolumetricLightRenderer` on the raid camera
(`CameraEffectsRepairs`; the donor Cam2 already carries one), and `ArenaLightingRepairs` now takes the authored baseline
from `_maxLightIntensity` and writes `CullingLightObject.float_1` alongside `Light.intensity` — without that the culling
loop erases the brightness uplift, and 79 Equator / 107 Bay5 lights that Arena saved at intensity 0 would stay dark
(they light up now; expect both maps brighter, `GlobalLightIntensityMultiplier` may want re-tuning). Batch-import trap:
the three classes are `[ExecuteInEditMode]`, their Awake/OnEnable exceptions are logged by Unity and fail the build's log
scan — editor stubs (stand-in shader `Sandbox/VolumetricLight`, a `FlickerSystem` on a HideAndDontSave object) keep it
clean. Host-verified 22:15–22:37 (Lighting summary `cullingLights=472/186`, fallback lines, no errors) and re-checked
2026-09-27 in bot-free raids (new menu buttons `EQUATOR|BAY5 / NO BOTS`) with the new read-only F7 `LightCullingProbe`:
every light inside its fade start at full intensity, every light beyond its fade end at zero, the window ramps;
`FAKE_SUN_VOLUME` active with the renderer on the camera and a visible glow in clear weather; flicker samples 0.05–0.97. Deployed to `D:\SPT41` and `D:\SPT41vanilla`. Details: docs/progress/two-maps-item-7-light-culling.md.
Next: plan items 8 (AI hubs), 10 (banners), then 9 (release).

## State on 2026-09-26 — round 3 verified on the new host SPT-GPU-2; Equator's atrium rains by design

Branch `feature/native-dust`. The GPU test host is now **SPT-GPU-2** (MCP servers `uibridge-2` / `windows-gpu-2`; same
click points, raid start ≈ 9 s, `D:\SPT41` still mirrors it). Round 3 verified there (EFT `cd73afd7`, 18:04–18:06,
forced rain, max settings): F8 at the atrium reads 47.51 m with `FullyIndoorCover=true` and 23.1 m (a beam) with
false, streaks visible on the screenshots; `AmbientRangeMultiplier` set live mid-raid logs `live settings applied …
re-targeted 23 playing source(s)`. User decision: the atrium's broken glass roof is Arena's authored geometry (pane mesh
`m_IsActive=false` in the raw level, "broken glass atlas" material, inactive glass-shard debris under it, no toggling
script; the puddles are generic mall dressing and prove nothing either way), so it rains — `FullyIndoorCover` defaults to false everywhere (DefaultsRevision 3 re-seed, `DefaultsRevision = 3` read
back after the restart) and the key stays as an opt-in dry mall. Correction: the puddle ripple follows the global rain
value (`RippleController.WaterCucle`), never the depth photo, so no cover setting removes it. Deployed to `D:\SPT41`
and `D:\SPT41vanilla` (vanilla server restart = user). Details: item-6 doc "Round 3 verified on SPT-GPU-2", items-1-3
doc tail. Next: plan items 7 (light culling), 8 (AI hubs), 10 (banners), then 9 (release).

## State on 2026-09-25 (afternoon) — plan items 4–6 built, verified and deployed

Branch `feature/native-dust`. The three parallel agent branches (NavMesh modifiers, physics props, rain cover)
are merged with the review fixes; both maps were re-extracted into `work/extract/equator-native-v3` /
`bay5-native-v3` (now the accepted inputs, AGENTS.md) and rebuilt. What the first real build/raid taught, all
fixed in the same commit: bake-only `NavMeshModifier`/`Volume` components must be stripped before the bundle
build (`NavMeshModifierImporter.StripBakeOnlyComponents`, else `patch_monoscript_identity` rejects the bundle);
the bake's Triggers/LevelBorder layer rule must not apply to markups (112/113 Bay5 overrides sit on layer 13);
the roundtrip validator needs the authored rain-cover objects (`raincover_extra_counts`); imported
`SmallPhysicsObject`s need Arena's `SoundType` (decoded from the raw bytes) plus a null-bank guard
(`PhysicsPropSoundRepairs`). Host raids 13:09–14:35 on EFT `9f0fe0dc`/`70aeac2c`: Bay5 NavMesh 22117/10213
(was 22190/10248), Equator 10404/4572 unchanged; 391/413 and 18/18 props active, touch without exception;
dry floor under Bay5's warehouse roof in forced rain. Evidence: the three `docs/progress/two-maps-item-{4,5,6}-*.md`
"Lead integration"/"Host evidence" sections. Deployed to `D:\SPT41`; `D:\SPT41vanilla` gets the same build
(user restarts its server). Known noise: one `SmallPhysicsSystem.Update` NRE per raid end (vanilla teardown
order), `Books` props are silent (no bank on this SPT build).

**Evening correction (user report: rain indoors on both maps, ambience only audible next to the source).**
Item 6's rain part was wrong: `AuthoredRainCover=true` had switched the depth-photo heuristic off, Equator has
no WeatherObstacle, and Arena's 669 Bay5 DryPlanes are EFT-Lighthouse leftovers (root `Lighthouse_DryPlanes`,
a square kilometre off the map). `extract_raincover.select_dry_planes` now drops foreign sets (Bay5: 669 → 0,
`weather_obstacle=false`), the client keeps the heuristic armed on every map (a real WeatherObstacle only adds to
the photo), Bay5 was rebuilt. Ambience: new `Audio (<map>) › AmbientRangeMultiplier` (default 1) stretches the
authored max distances before the graph's Awake bakes the rolloff curve. `PhysicsPropTeardownRepairs` silences
the raid-end `SmallPhysicsSystem.Update` NRE. Host-verified 17:49–18:02 (F8 depth readbacks: Equator mall roof
5.2 m, Bay5 warehouse 8.0 m, container 1.4 m, outdoor control = ground; range line `x3.00 … 25 player(s)`;
clean teardown). Details: item-6 doc "Correction", items-1-3 doc "Item 1 follow-up", item-5 doc.

**Round 3 (evening, not host-verified at the time — verified 2026-09-26, see the section above; the default of
`FullyIndoorCover` has since moved to false):**
Equator's atrium roof is an open lattice in Arena's data (`equator_roof_Glass_LOD0` ships inactive), so rain fell
through the cells → new key `Lighting (arena_equator) › FullyIndoorCover` (default true) draws the CoverAll quad;
the three ambient keys now apply live (`ApplyUserSettingsLive`). Bay5 has no rain-reactive puddle decals in
Arena's data (content gap, documented, nothing changed). Built and deployed to `D:\SPT41` only; `D:\SPT41vanilla`
waits for the host verification (F8 at the atrium ≈ 47 m, live-apply log line). Next: plan items 7 (light culling), 8 (AI hubs), 10 (banners), then 9 (release).

## State on 2026-09-25 — plan items 1–3 verified and deployed

Branch `feature/native-dust`, commit `a947cce` (+ docs). Ambient gain/offset keys per map (neutral default),
`BarbedWireRepairs` on Arena's concertina wire (192 triggers on Bay5, damage verified), release names
"Arena: Equator" / "Arena: Bay 5" (server log + map-select screen). Host raids 10:22–10:27 on EFT `027b491c`;
deployed to `D:\SPT41` and `D:\SPT41vanilla` (vanilla also received the native-dust scene bundles it was
missing; its server must be restarted by the user for the new names/base.json). Evidence:
[two-maps-items-1-3.md](docs/progress/two-maps-items-1-3.md). Next: items 4–6 (one rebuild per map).

## State on 2026-09-24 — inventory of remaining native content (no code change)

Question answered: what of Arena's Equator/Bay5 is still not ported. A class census over all source scenes
of both maps (including the excluded TeamFight_AI, Sound, Culling and Scripts scenes; Bay5 had none before)
is in [native-content-inventory.md](docs/progress/native-content-inventory.md) with a ranked candidate list:
ambient gain, Arena's own AI anchors (PatrolPoint 23/47, AICorePoint 14/11), Bay5's 669 DryPlane + 30
IndoorTrigger rain-cover boxes, physics props (391 SmallPhysicsObject + 413 Rigidbody on Equator), NavMesh
modifiers (124+93 / 304+75), barbed wire (246, Bay5), decals, flares; ArenaMode-side: pickups, lighting
director allowlist, team starts. Not worth it / blocked: glass (user decision), crowd/monitors/gates
(Arena-only classes), PerfectCulling, impostors, mannequins. Nothing was built or deployed; GPU host untouched.

Scope decision 2026-09-25: the first release contains Equator and Bay5 only; the other twelve maps are
deferred (still built/staged, no further work). Map quality on the two comes before ArenaMode features; the
ordered plan is docs/PLAN-TWO-MAPS.md (feasibility spikes of 2026-09-25).

User answers 2026-09-25: Team Deathmatch/Tactical AND pickups are wanted on both maps later — faction spawn
pools and pickup surveys are therefore real map-side work, not optional polish (inventory §7). Bay5 DynamicMaps
is installed (`Maps/ArenaMapPort_Bay5/ArenaMapPort_Bay5.jsonc`, user's own pack) and must not be listed as open.

## State on 2026-09-23 — original Equator and Bay5 dust verified

Branch `feature/native-dust`, based on `29ea1be`. Both maps now import the original lossless
Alpha8 Texture3D and reuse SPT's native dust animation with the authored particle meshes.
The runtime restores padded bounds and the native transparent queue on one owned material;
unload restores renderer state and releases that material. No lighting or audio gain changed.

Both full bundle pipelines and independent byte/binding comparisons passed, including all
519 normal textures. Navigation bundles and shader sidecars remain identical. Checks:
356 .NET passes, 222 Python passes /27 expected skips, eight PowerShell cases and the complete
14-map distribution. Exactly four of72 staged files changed: both DLLs and the two scene bundles.
Sanctioned deployment and final installed hashes passed.

Fresh EFT `3e1ca2da` verified Equator → Bay5 → Equator with visible moving dust, correct native
speeds, unchanged meshes, queue3000 and three clean material releases. One existing MenuScreen
coroutine message appeared on the first Escape; identical pre-change archive entries are recorded
in [the native evidence](docs/progress/native-dust.md). No dust errors occurred.

Current accepted extraction inputs are `work/extract/equator-native-dust` and
`work/extract/bay5-native-dust`; canonical built outputs remain `work/build/full` and
`work/build/bay5`. Use absolute descriptor paths for Unity builds. Older native-feature packages
lack the new raw volume payload and must not replace these accepted inputs.

Bay5 already has a user-installed DynamicMaps map. Its JSONC/SVG and ArenaMapPort's complete
configuration were verified unchanged. Final profile: 440/440 HP,100 energy,100 hydration;
all145 inventory records preserve IDs, templates, parent relationships and slots. All three
tests were botless and short. EFT closed normally around11:54; server running, GPU host free.
The remaining quiet-ambience balance is a separate follow-up; glass remains deferred.

## State on 2026-09-23 — remaining Bay5 barrier and shadow-cache spam fixed

Branch `feature/bay5-barrier-followup`, based on `d842ef8`. The one reported unsuffixed
`Military_sandbag_barrier_03` had all three separate shadow models inactive already in Arena's
source. A descriptor-scoped runtime repair activates its existing ShadowsOnly LOD proxies.
The archived 20,583 null-material exceptions came from DistantShadow keeping retired material
references after live lighting changes. Clone replacement/restoration now resets its pending
work and schedules a native snapshot rebuild. F10 also avoids reading absent `_MainTex` slots.

Build and checks passed (353 .NET tests, 190 Python passes /27 expected skips). Only client and
server DLLs changed among72 installed files; bundles are unchanged. Fresh EFT `71873626`
verified repeated Bay5 live changes, Equator, two Bay5 reloads and the exact barrier's visible
shadow (F10 07:21:06.699). The reported exception did not recur. See
[source, deployment and native evidence](docs/progress/bay5-barrier-followup.md).

The user's current Bay5 lighting settings had been reset before this turn: ground multiplier1,
hands multiplier1, specular cap1. All temporary tests were restored to these current values;
do not restore older milestone preferences automatically. Equator cap remains0.3.

One repeated Bay5 visit re-enabled bots; the test character was killed and recovered through
the existing Redeploy UI. Final check after a botless repeat: 440/440 HP,100 energy,100 hydration,
all non-structural inventory items preserved. No profile files or third-party configs were
edited. Set bot amount None and bosses off again before each diagnostic visit, including after
a map switch. EFT closed normally at about07:22; server running, GPU host free.

## State on 2026-09-23 — Bay5 shadows, veil and debugging controls verified

Branch `feature/bay5-shadows-menu`, based on `7147e8e`. All four requested parts have passed
build, deployment and native checks. ArenaMapDebug commit `86f9524` is installed;
F12 shows per-map options only for Equator and Bay5 while keeping other maps' saved values.
The final client also retains a map-specific hands ambient-highlight scale (neutral 1;
local Bay5 0.35), restoring original fields on teardown.

Source and bundle comparisons identified an actual packed-normal defect behind incorrect
lighting: PNG normal-map import repacked an already encoded alpha/X channel to opaque.
Two PNG-import workarounds passed a small fixture but failed full-map checks and were rejected.
The accepted importer uses native Texture2D assets, preserving source channels, color space,
compression, complete mipmaps, filtering and stable GUIDs. Full builds and independent checks
passed all 519 normals across Equator/Bay5. Only the client and those two scene bundles changed
among the 72 installed files; no failed candidate bundle was deployed. Fresh EFT instance
`453175d3` verified Bay5 → Equator → Bay5: coherent exterior shadows, natural cardboard instead
of the blue-white veil, reduced knife highlight, neutral Equator hands, repeated Bay5 settings
and clean restoration. Both buttons skip selection/countdown and return directly with living
HP protection. Final control point: 440/440 HP; EFT closed normally at about 00:33 CEST.
See [complete evidence](docs/progress/bay5-shadows-menu.md).

Current inputs must remain `equator-native-features` and `bay5-native-features`. CPU-readable
compressed normal copies total about 178 MiB for Equator / 305 MiB for Bay5; the actual increase
in total game memory has not been measured. Local Bay5 ground brightness remains 2 and hands
highlight 0.35. The ineffective BuildBundles startup workaround was removed. The GPU host is free.

During an earlier long diagnostic raid, time expired and normal MIA penalties applied.
The test character was healed and replenished through normal game UI, reaching 440/440 HP,
100 energy and 100 hydration before EFT was closed at 22:24. The MIA record and consumed
medical/food items remain; no profile-file edits were made. Keep later test raids short.

## State on 2026-09-22 — Bay5 exterior ground brightened and tested

Branch `feature/bay5-concrete-lighting`, based on `cfbbf16`. The user's clarified problem was
dark exterior ground. The sampled Concrete collider covers an original layered asphalt material;
no missing-texture defect was found. New live `Lighting (arena_bay5)/VertexGroundDiffuseMultiplier`
scales only that shader family's layer RGB. Neutral default 1; local Bay5 setting 2. Reflection
inputs and other lighting experiments are restored/preserved. F10 now emits complete numbered
parts so UiBridge can retrieve later material slots.

Release client C02C98D130616F9CA96ECD45870ED193A2852B2C49C318FA30F507EDC330ABDC was deployed
through the sanctioned script. All 72 installed identities match staging; only the client changed.
Fresh process `09cca5c0` verified Bay5 → Equator → Bay5, visible yard/north-lane improvement,
absolute 1 → 2 → 1 scaling, unchanged reflection fields, neutral Equator, saved 2 on revisit,
and nine-tint cleanup on both Bay5 exits. Health 440/440 and ammunition 37+1 at final extraction.
The user's preferred brightness is still a visual check; bright authored wet patches remain.
See [ground visibility evidence](docs/progress/bay5-concrete-lighting.md).

EFT was closed normally after the final menu return; the GPU host is free.

The user has now listened: impacts fit, ambience is audible but very quiet. Only the ground
complaint was explicitly assigned to Bay5. Audio gain was not changed; source-level volume and
randomization findings plus a scoped gain follow-up are documented in the same progress entry.
The DynamicMaps prototype and prior native-feature work below remain intact.

## State on 2026-09-22 — Bay5 DynamicMaps installed and natively tested

The user released the shared host. Candidate `dd0dce0` was installed with the scoped
two-file DynamicMaps deploy from `D:/ArenaMapPort-DynamicMapsBay5`. No client/server
build or full deployment was performed. The lighting session's installed client from
main-checkout commit `7147e8e` was retained.

Fresh EFT process `e93a8a10` (19:38:54 CEST) loaded the Bay5 definition and SVG.
Inventory → MAP selected it automatically. Four positions, all cardinal directions,
raised warehouse ground and both live exits matched. Moving while the map was open
updated the player marker. Normal north extraction took 9.4 s; MAIN MENU from the
result screen returned in 30.2 s, with 440/440 HP. No test shots; ammunition 37+1.
No DynamicMaps warnings/errors and none from any source from first map opening
through menu return. All 46 plugins and five libraries matched VM/host/loaded code.

EFT was closed normally and the host is free. Native rendering passed; actual
tessellated vertex count was not instrumented. Source remains on the isolated
`feature/bay5-dynamicmaps` branch; the lighting checkout was not modified.
See [native evidence and preservation audit](docs/progress/bay5-dynamicmaps.md).

## State on 2026-09-22 — Bay5 DynamicMaps prepared; shared-host hold

**Staged locally, not deployed or natively accepted.** The user accepted Equator and
requested Bay5, then explicitly held gameplay tests until release of the shared host.
No production writes during the hold. Work lives in the isolated
`D:/ArenaMapPort-DynamicMapsBay5`, branch `feature/bay5-dynamicmaps`, base `cfbbf16`.
The main checkout's concurrent lighting work must remain untouched.

The generated pack is `work/dynamicmaps/bay5/pack`: exact `arena_bay5` identity,
108 x 92 m view, 13,737 geometry points. It follows varying native floor heights;
native projected ground takes priority over false floor-derived obstacle slabs.
43 exporter tests, 80 deployment cases and independent offline geometry/visual
review passed. The accepted Equator JSON/SVG/preview remain byte-identical.

The real two-file deployment dry run passed. Deployment and native player/direction/
exit checks remain pending; recheck host ownership and the recorded live NavMesh
hash after release. Use this worktree's scoped data-only deploy, not its base DLLs.
See [Bay5 checkpoint](docs/progress/bay5-dynamicmaps.md) and
[reproduction](tools/dynamicmaps/README.md). All generated data remain ignored.

## State on 2026-09-22 — Equator DynamicMaps prototype installed and tested

Branch `feature/equator-dynamicmaps`, based on `2c673e2`. The optional Equator SVG/JSON
pack is generated under ignored `work/dynamicmaps/equator/` and installed through the new
`deploy-to-spt.ps1 -DynamicMapsPackage ... -Confirm` mode. Existing map bundles and mod DLLs
were not rebuilt. Standard deployment remains the original 72-file mapping.

Fresh EFT process `96ebdca3` (11:36:40 CEST), DynamicMaps 1.2.1, SPT 4.1.6: inventory → MAP
automatically selected Equator. Four positions, all four cardinal arrow directions and both
live exits matched. Normal South Gate extraction and main-menu return passed; final HP 440/440,
ammo 37+1 unchanged. No DynamicMaps warnings/errors. EFT is closed and the host is free.

27 exporter tests, 59 deploy fixture cases and independent format/native review passed.
Two full exports were byte-identical. Generated data stay out of git. This is a simplified
ground plan, not exact walkability; upper decoration and extensive labels are absent.
Bay5 is a future follow-up, not included. See [acceptance](docs/progress/equator-dynamicmaps.md)
and [reproduction](tools/dynamicmaps/README.md).

## State on 2026-09-22 — Equator native features deployed and tested

The user released the GPU host, lifting the earlier hold. Candidate `a728428` on
`feature/equator-native-materials-audio` is now deployed through the sanctioned script.
All 72 installed hashes match staging; exactly the client and Equator scenes bundle differ
from the immutable Bay5 baseline. Their SHA-256 values remain those listed below. No further
production change was needed during native acceptance.

Fresh EFT process `adbbe116` (10:33:05 CEST) confirmed all 25 maschine plugins identical across
VM, host disk and loaded code before and after Equator → Bay5 → Equator. Ordinary PMC raids
used the menu's bot amount None. Real WoodThin, MetalThick and Concrete hits selected the original
values with `defaultFallback=False`. Both Equator visits initialized 23 of 25 authored sources;
the two original inactive dogs stayed uninitialized. Samples advanced, nearby electricity/wind
produced nonzero output, and the indoor mixer route was correct. Each Equator exit stopped 23
players and released 23 empty clips with zero failures; Bay5 retained its 24-source Summer graph,
moving shoreline and 24/24 cleanup. No native audio lifecycle exception appeared through final
menu return. Existing F10 shader-inspection and other startup/UI errors are recorded separately.

Equator's saved reflection controls and 10,404-vertex / 4,572-triangle NavMesh remain unchanged.
The matched atrium view has no obvious visual regression; this is not a universal reflection
quality claim. All three test diagnostics were restored to false. Final health was 440/440;
EFT was exited normally and the host is available. Test shots consumed ammunition normally.

**Remaining subjective check:** listen to the environmental mix. The OS was muted, so the
technical playback evidence does not establish audible balance. Original random volume ranges
can also produce silent individual sources for a raid; no gain override was introduced. Glass
remains deferred. See the appended [native acceptance](docs/progress/equator-native-features.md).

## State on 2026-09-22 — Equator native features staged; GPU hold

**Built and staged, not deployed or natively accepted.** The user chose original Equator materials
and environmental audio, then explicitly asked to wait for their release before further GPU tests
because other sessions are using the host. Do not take over the host or install this candidate
before that release. Branch `feature/equator-native-materials-audio`, based on `2862da7`.

The opt-in `extract_equator.py --native-features --scenes all5` package adds 2,892 original ballistic
components and 25 authored ambient players / 11 banks / 65 clips. Original activation is preserved:
23 active grouped players, two inactive dogs. Runtime uses the native indoor mixer and keeps cleanup
away from uninitialized players. Glass, crowd/match events and full room acoustics remain excluded.

Unity retry `unity_build_all_20260922_094952.log`, mandatory component-identity patch, two independent
fresh exports, original-source/bundle comparisons and distribution checks passed. Tests: 182 Python,
348 .NET, eight PowerShell; final client Release build has zero warnings/errors. Bay5's actual source
and accepted bundle still pass the shared validator, and a fresh default Bay5 audio export is
byte-identical to all 107 accepted files. Generated game assets stay ignored.

All original Equator scene objects and 3,645 source asset files are unchanged. The NavMesh is
byte-identical (10,404 vertices / 4,572 triangles); authored start behavior is identical apart from
the existing fresh-GUID-per-build policy. Two of 18 cubemaps now retain their original Linear sampling.
Adding Equator's map identity had exposed an unintended generic spawn-placement path; the bounded
Equator-only guard preserves its legacy start authoring and bundle build option.

Only two of 72 staged files differ from the installed baseline:

- Client SHA-256: `74A17BB99DF33F897691730AD484F4B776353D7FA1E08AF593F8A92ACE711A96`.
- Equator scenes SHA-256: `02E687EEE5DB21D9188FDC899F98C9A1E663D6578C13113B63B598C5DA956A7F`.

All 72 installed MapPort files still match the pre-task baseline. The server, other maps, NavMesh,
presets and shader sidecars are unchanged in the candidate. Resume with the sanctioned deploy script,
fresh EFT identity check, and Equator–Bay5–Equator tests after host release. Required evidence includes
real original material hits, 25/23/two-inactive Equator audio, sample/RMS/spatial/mixer behavior,
23-player/clip teardown with no native Fader errors, Bay5 regression control and the saved central
reflection view. A muted-host technical signal test cannot establish audible balance.

Full provenance, build issue, artifact receipts and exact acceptance checklist:
[Equator native features](docs/progress/equator-native-features.md). The previous Bay5 acceptance below
remains the installed state; this Equator checkpoint does not supersede its runtime evidence.

## State on 2026-09-22 — Bay5 original materials and harbor ambience

**Built, deployed and natively verified.** Branch `feature/bay5-native-materials-glass-audio`,
based on `aa5e3cc`. User explicitly deferred glass: all 147 original panes are inactive, and no
glass activation or WindowBreaker port was added. The task restores 6,322 original ballistic
components and 24 native ambient sources / 13 banks / 106 original PCM clips. Three Linear
cubemaps retain their original color space. Original geometry, navigation and reflection knobs
are preserved. Independent source/package/bundle comparisons and reproducibility passed;
152 Python tests, 348 .NET tests and eight PowerShell cases passed. Final client build: zero
warnings/errors. Original assets remain ignored; proprietary audio is not a cleared release asset.

Only two of 72 installed files changed; all other maps, the server and NavMesh are byte-identical:

- Client SHA-256: `24F9753F97D160CD59380EE2442C7EF15A233A5C2350A7ADFC660D68BB1AB015`.
- Bay5 scenes SHA-256: `3957E836C63C0F76AD8433DDC2236706C76E4E36893A945AA98B9A382E58E261`.

Final process `90bc532c` (08:29:03 CEST) confirmed VM = host disk = loaded. Real WoodThin,
MetalThick and Concrete projectile hits selected original values, with no default fallback on
those targets. Native Summer audio started 23 players and retained the one missing seasonal
bank as silence. Water was correctly placed before any movement and followed the native shoreline;
sample advancement, nonzero source RMS and both mixer routes were verified. Two SPT shoreline
initialization defects were corrected only inside the owned Bay5 graph. The host OS output is
muted: this is technical playback evidence, not an auditory mix-quality approval.

The Bay5–Equator–Bay5 sequence passed. Both Bay5 exits stopped 24 players, released 24 generated
clips and reported zero cleanup failures; the revisit retained exactly 24 sources. Equator had
no Bay5 audio and kept its accepted reflection settings. Both Bay5 teardowns restored 642
renderers / 1,008 clones; Equator restored 607 / 1,103. Bay5 used its ordinary South exfil;
the Equator Debug disconnect remains scoped to debug-started sessions. Final profile health
was 440/440, diagnostics were restored to false, and EFT was exited normally around 08:44.

Full source/runtime reasoning, native timestamps, caveats, screenshot paths and local receipts:
[Bay5 native features](docs/progress/bay5-native-features.md). Next: user hearing/visual approval,
then one further map through the existing map recipe. Dust/extra decals, full room acoustics
and glass are separate follow-ups; no additional feature expansion is implicit.

## State on 2026-09-21 — Bay5 covered-floor reflections accepted at 22:13 CEST

**Bay5's covered Hangar floor now has independent reflection and smoothness controls, built,
deployed and tested in-game.** Branch `feature/bay5-indoor-materials`, based on `9f8911e`.
The render-only partition corrects covered floor portions inside mixed building meshes; original
wall/decal/outdoor slots are retained. All four Hangar LODs follow the same route. F12 section
`Lighting (arena_bay5)`: `IndoorSurfaceRepair=true`, `IndoorSurfaceReflectionStrength=0.20`,
`IndoorSurfaceSmoothness=0.50`. Existing Bay5 cap `0.2957746` and legacy settings are preserved.
Other maps default to the new surface path disabled.

Final client SHA-256:
`8F8E79C808BDF7D1054C68180443DFE6F82E61CE4F3AD7A84BDB223AF3593CBA`.
Of 72 deployed artifacts only that client changed; 71 remain byte-identical to the accepted
background delivery. No scene/NavMesh/server/ArenaMode changes. Release build had zero
warnings/errors; 348 tests passed and independent review found no blocking issue.

Fresh GPU process `851020e4` (21:53:32) verified matching VM, host and loaded identities. Bay5
automatically partitioned 32 render meshes including LODs / 21,032 covered triangle fragments.
Enabled classification took 602–642 ms in six observed passes. Same-view material and independent
knob controls, exterior on/off control, glass false/true plus F8, and the Bay5–Equator–Bay5 sequence
passed. Both final Bay5 teardowns restored 642 renderers / destroyed 1,008 owned material clones.
Equator retained 607 renderers / 1,103 clones with the new path off. No MapPort warnings/errors in
the recorded final process. EFT returned to MainMenu and was exited normally at about 22:13.

The covered concrete is less reflective. Original puddles still reflect strongly, and small
material transitions can remain around roof openings: cover is sampled, not exact polygon
clipping. No new interior-reflection renderer or exhaustive all-distance LOD walk is claimed.
Full evidence, local screenshot paths, algorithm limits and receipts are in
[Bay5 covered-floor reflections](docs/progress/bay5-indoor-materials.md).

The next gameplay step remains one additional map through [the map recipe](docs/MAP-RECIPE.md).
For further Bay5 visual tuning, use the two new controls independently; authored puddles are
outside this repair. Historical gameplay/profile acceptance below was not repeated by this task.

## Previous acceptance on 2026-09-21 — Bay5 harbor background at 11:56 CEST

**Bay5's original harbor background is built, deployed to the GPU host and tested in-game.**
The native views show the northern gantry, southeast portal crane and original container stacks.
Bowl was left unchanged after the user confirmed its stadium is present. Equator's accepted
reflection/lifecycle behavior below remains the baseline; its bundles and base are byte-identical.

| Repository / artifact | Current accepted implementation |
|---|---|
| MapPort | `feature/bay5-background`: background `4f2025e`, legacy preset guard `2ba4d71`, bot marker `b6e0692` plus its generated server base |
| Bay5 Unity bundle | `unity_build_all_20260921_104434.log`; 10 loaded scenes, 22,190 NavMesh vertices / 10,248 triangles |
| ArenaMode | Unchanged implementation `75a7430`; no files edited in this task |
| Final receipts | `D:/.unlazy/bay5-background/distribution-after.json`, `distribution-delta.json`, `install-final-verification.json`, `native-final.json` |

Client SHA-256: `6E12B25466F156A977F39DF3ABA44FA8AF61A6D54D9BCFEE789942B46B8140FD`.
Bay5 NavMesh SHA-256: `CD7A3308E9D21F54F4FCF19242114B97E3952FB452F2E1F6ADAF0082D7DE64F3`.
Bay5 base SHA-256: `CEAA30A95521FB80FEE39A623B43E69FF5FA5D32F70914A1A4DF50993A76775C`.
All 72 deployed files matched their staged hashes; seven intended files changed and 65 remained
byte-identical. The vanilla test install was not updated.

Fresh process `d1121cb1` (11:48:44 CEST) confirmed all 25 plugins plus Redeploy Core identical
across VM, host disk and loaded code. At 11:52:47, the ten bot markers formed **one connected
NavMesh component, zero isolated/off-mesh points**. The northeast marker is now three metres
south at (106.267, -3.8574, 61.7033), avoiding an automatic move onto an isolated container-side
patch. Four player starts and all ten effective bot positions passed full path and standing-room
checks. Deathmatch bots moved, fought and replenished; protected return preserved the full saved
profile except the background hideout timestamp, including the final flush. EFT was quit at 11:56.

Both ordinary exits completed in process `23dee6d0` on the **same scene/NavMesh bundles** before
the final single bot-position change. RainBounds and indoor classification stayed unchanged.
Background colliders participate in navigation, while distant renderer bounds do not enlarge
the bake volume or weather scans. The skyline is included, but floor-view visibility was not
established. No new Gun Game ladder, long-session or performance acceptance is claimed.

Full build, native evidence and the separate ArenaMode HUD warning are recorded in
[Bay5 background acceptance](docs/progress/bay5-background.md). That warning comes from
`GrenadeInventoryCounter.CollectSummary` during kit setup; this task does not modify ArenaMode.
Next gameplay work remains one additional map through [the map recipe](docs/MAP-RECIPE.md).

## Previous acceptance on 2026-09-21 — 08:32 CEST (historical artifact identities)

**Bay5 ArenaMode integration and Equator's reflection cap are deployed and host-tested.
The final MapPort repeat also passed: corrected Bay5 NavMesh, both ordinary exits, and automatic
Equator indoor classification before and after Hideout in the same restarted process.**

| Repository / artifact | Accepted implementation |
|---|---|
| MapPort | `feature/bay5-and-equator-reflections`, code `d896214` |
| Bay5 Unity bundle | Built from `c95bdca`, `unity_build_all_20260921_074006.log`; 14,754 vertices / 6,816 triangles, natively verified at 08:27:48 |
| ArenaMode | `feature/bay5-map-integration`, code `75a7430`; `eb3537d` adds first-pass native evidence only |
| Artifact receipt | `D:/.unlazy/bay5-reflections/mapport-accepted-candidate-artifacts.json` (08:07 CEST; accepted by the later native repeat) |

The artifact receipt records the complete hashes. Client SHA-256 is
`9957897262AA1F5C0AE9E2BFFB2DA6B1FB80B1EE54F8DD711DDBCE63FFF92D34`; Bay5 NavMesh SHA-256 is
`5BA00920DFA3A5E9E5D33E05A3A08CD3B9B9959C31C83A87D2EE5FDCC5C00DEB`.
Fresh GPU process `c2a37398` (started 08:11:03) verified identical VM/host/loaded identities for all
25 maschine plugins and Redeploy Core. Equator automatically classified **607 indoor renderers /
1,103 clones** at 08:13:48 and again at 08:25:40 after an intervening Hideout visit; both raids
capped **7,022 material slots**, without F8. Hideout took roughly three minutes to become responsive,
then recovered without killing/restarting the process. Final Bay5 probes and both ordinary exits
passed afterward. The client was quit at 08:32 for log flushing; it is not left in a raid.

**Current implementation:** Equator and Bay5 each have four player starts, ten separate bot markers
in `BotZone_Main`, and two ordinary test exits. ArenaMode admits both maps independently for
Deathmatch/Gun Game with an optional MapPort dependency and guarded revision-2 default migration;
custom lists, disabled settings and intentional removals survive. Tactical and team modes are excluded.
MapPort's complete Python/server suites and distribution oracle passed after integration; the full
distribution remains 14 maps / 42 bundles / 14 shader sidecars / 14 server base files plus two DLLs.
The focused reflection/lifecycle oracle passed **55 tests** and a zero-warning/error client build.
ArenaMode's independent verifier passed **691 server tests, one optional APBS skip**, its native
source-linked harnesses and five fault controls. Offline checks, the first ArenaMode pass and the
final MapPort repeat have separate evidence; the final ordinary raids did not repeat DM/GG.

### Native history that remains valid

- **Equator ArenaMode, 2026-09-20:** both modes admitted, PMCs replenished from the atrium, real combat
  deaths respawned (two Deathmatch, one Gun Game), and protected return restored the entire saved
  profile except the background hideout timestamp. Gun Game reached tier 1/24 only. See
  [equator-arenamode-integration.md](docs/progress/equator-arenamode-integration.md).
- **Bay5 first pass, 07:29–07:45 on 2026-09-21:** the older **15,045 / 6,945** NavMesh passed all
  fourteen authored spawn-point probes and connected the northern depot opening and warehouse.
  South extraction completed after eight seconds at 07:33:45. Native Deathmatch and Gun Game
  replenishment, combat respawns (0.63 / 0.62 seconds) and protected return passed. Entire normalized
  before/after profiles matched after the final persistence flush; Gun Game remained tier 1/24.
  Those tests precede the new bake and north-exit correction. See the append-only
  [Bay5 integration checkpoint](docs/progress/bay5-arenamode-integration.md).
- **Equator reflections, 06:52–07:04:** same-view cap 1 → 0.30 → 1 reduced/restored the armored van
  and sandbag sky sheen; the already-damped lounge parquet stayed unchanged. F12 section
  `Lighting (arena_equator)` / `ReflectiveSpecularCap` defaults to 0.30; 1 restores uncapped inputs.
  Bay5 defaults to 1 (disabled). See [equator-specular-cap.md](docs/progress/equator-specular-cap.md).

### Fixes confirmed in the final loaded-build repeat

- Bay5's original spawn boxes remained non-trigger on native `Triggers` layer 13: physically ignored
  by the player but baked into ghost NavMesh islands. `c95bdca` excludes those collider owners;
  exactly 20 old Bear/Usec boxes are affected, no mesh floors. The new **14,754 / 6,816** bake passed
  the old depot sample (52.7, -3.8, -6.3), all fourteen authored spawn points, adjusted runtime bot
  positions, both exits and additional depot/warehouse connectivity and standing-room probes.
- The first north-exit runtime trigger sampled an upper floor 5.6 m above its authored point.
  `fce614a` prefers a nearby floor before the legacy wide search; `30c3e3d` moves north to the
  clear loading door at (96, -2.9252, 61), with descriptor-compatible decimal spelling in `d896214`.
  Both exit triggers sampled the nearby floor with 0.0 m logged displacement. North and south
  completed ordinary extraction at 08:28:58 and 08:31:03 respectively, reaching ExitStatus and then
  the menu. The duplicate eastern depot gate remains closed; interior connectivity passed.
- Rain-camera subscription reattachment alone did not fix every reused-donor startup path.
  `26dd9f2` adds a raid-ready cover bootstrap; `d514957` reuses successful camera initialization
  instead of allocating rain resources again on that camera. Both automatic Equator classifications
  passed around the Hideout transition in one fresh process, without a manual diagnostic render.

The final results are recorded in [Bay5 integration](docs/progress/bay5-arenamode-integration.md),
[Equator reflection evidence](docs/progress/equator-specular-cap.md) and ignored
`D:/.unlazy/bay5-reflections/native-c2a37398-evidence.json`. Navigation used coordinate-assisted
probes, not a complete walk-through. Gun Game remains a tier-1 smoke test; no full ladder,
long-session or performance acceptance is claimed.

**Other twelve maps:** built, validated and staged in `dist/`, but still without authored gameplay
bots/exits and without ArenaMode acceptance. Customs, Shoreline and Woods also await terrain runtime
validation. Earlier "inherited exclusions" describe the original build pass; additional gameplay
integration proceeds one map at a time through [docs/MAP-RECIPE.md](docs/MAP-RECIPE.md).

**Installs:** `D:\SPT41` is the deployment mirror of the GPU host and holds the accepted build.
`D:\SPT41vanilla` was not updated in this Bay5/reflection pass and retains its earlier stage-1 build;
verify its artifacts separately before testing there. Use the authorized deployment script, not
manual copying. Keep the first-pass history and final-repeat evidence separate when continuing.

## Decisions (user, 2026-09-20) — see docs/SPEC.md "Decisions 2026-09-20"

ArenaMode is the target; no loot; no scav raids; bundles are distributed as built; Equator first.
The full gap analysis behind the stages below lives in the user's session notes; the tiers are:
(1) combat/death correctness and the NavMesh bake on Equator, (2) the other maps through the Equator
recipe, (3) parity only where ArenaMode needs it, (4) release hygiene.

## Stage 1 — done on the host (2026-09-20 evening), one item left for the vanilla install

| Item | Status | Evidence / where |
|---|---|---|
| `EffectsController.OnHealthDied` null-guard on the Cam2 camera (player death) | built; installed and harmless on the host (death is intercepted by Redeploy there), the Cam2 path itself needs a raid on D:\SPT41vanilla | `EffectsControllerRepairs.DeathScreenEffectsGuard`, `HarmonyPatches.InstallDeathEffectsGuard` |
| Bullets ignore LowPolyCollider-only props (13 on Equator: racks, rubble, fences, booth revolving doors) | verified 20.09. 19:03 (13 twins added, `stage1-combat.md`) | `BallisticTwinRepairs.cs` (`[Combat] BallisticTwins`), runs from `SpawnInfiltrationRepairs`' postfix |
| NavMesh bake kept booth doors, `SPAWN_BOUND*`, the FCK shutter and the 41 TeamFight `LevelBorder` walls → islands for bots | verified 20.09. 19:33: ground floor incl. booths and FCK hall is one component (`stage1-combat.md` host test 2) | `BakeNavMesh.RoundOpenedReason`, `ArenaMapDescriptorData.opened_barriers`, `tools/maps/equator.json` |
| Host raid: death path, PMC hostility in combat, bot pathing into the booths | done 20.09. (PMCs hostile via SAIN combat layer, killed the player; booths reachable) | `docs/progress/stage1-combat.md` |
| Extraction UI look during the countdown | not inspected (de-prioritised, ArenaMode target) | — |
| Survived/KIA post-raid screens, insurance | de-prioritised (ArenaMode target) | — |

Next gameplay work: continue one additional map through [docs/MAP-RECIPE.md](docs/MAP-RECIPE.md),
then tackle a terrain map. Bay5's current acceptance is complete; do not extend it to the other
twelve maps or to untested full Gun Game ladders and prolonged sessions.

Release (stage 4, not started): `dist/` is what gets shipped, zipped as-is (BepInEx/ + SPT_Runtime/ trees);
no packaging script, CHANGELOG, LICENSE file or version bump exists yet — `PluginVersion`/`ModMetadata.Version`
are 0.1.0.

## Pending user actions

- Restart the `D:\SPT41vanilla` server after a server-DLL/base.json deploy (client-only changes need only an
  EFT restart).
- Decide when to bump `PluginVersion` (still 0.1.0) and add a LICENSE/CHANGELOG (stage 4).

## Environment facts an agent cannot infer

- GPU host `WIN-Q470OL2A14A`, EFT via SPT launcher, profile `ArenaGPU20260908`; Parsec keeps the A2000
  display alive — do not switch displays. Host server terminal: closing it is how the server restarts.
- The GPU host runs SPT 4.1.6 / EFT 0.16.9.5-40743 for the documented 20/21 September acceptance.
  The source server dependency pin still targets 4.1.5; the vanilla test bed was not verified this pass.
- The user's `maschine-ArenaMapDebug.dll` (EQUATOR DEBUG button, penalty-free disconnect) is outside this
  repo and outside version control; host tests rely on it.
