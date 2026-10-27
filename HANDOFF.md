# HANDOFF — dated working state

Snapshot for whoever continues (human or agent). Verify the commits named here against `git log`; this file
is updated by hand at each handover, it is not an automatic record. Read [AGENTS.md](AGENTS.md) first.

## Update 2026-10-27 - complete AirPit crowd (feature/airpit-spectator; deployed and tested on GPU-2)

User approved expanding the prototype to every original spectator. The schema-2 sidecar restores
all 99 source placements / 15 appearances / 495 renderer parts. One `AirPitSpectator` scene manager
shares 39 meshes, 33 materials, 103 textures and the two original idle/cheer clips. Each source
skeleton has independent timing; visible poses update at 30 Hz (15 Hz beyond 45 m), off-screen
figures resume at their elapsed phase. No new setting, gameplay AI, collider, audio or match reaction.

166 source renderer slots have null meshes. The extractor recovers their matching current Arena
head/body/hat meshes and maps the exact mesh bone hashes, including implicit single-influence
weights. The legacy lower-face cloth is absent from the current Arena installation: its 27 slots
use Arena's current Shroud half-mask, with the correct material. This is a documented substitution.

Verified: 28 focused tests; independent source census and CPU deformation of all 15 variants in
435 poses; clean Release build; full four-map distribution validation. GPU-2 fresh process
`e4fe774b` loaded matching files, then completed two AirPit raids, both with 99 fans and no mod
warnings/errors. No duplicates; zero crowd managers and zero owned spectator materials after each
exit. Three steady 10 s windows measured 120 FPS / p95 8.33 ms at the host's frame cap. Early raid
windows still contain the previously observed ~1.9 s startup hitch; no isolated performance-cost
claim. Gallery close-ups use test-only teleports; the playable area and DynamicMaps stay unchanged.

GPU-2 is in the main menu, healed. Changes are deployed to `D:\SPT41` through the authorized script,
with all 125 client/package files verified on the host. No release/upload/push. Details:
[airpit-spectator.md](docs/progress/airpit-spectator.md); local evidence `work/spectators-crowd/`.

## Update 2026-10-27 - one original AirPit spectator (feature/airpit-spectator; deployed and tested on GPU-2)

User approved one spectator with original idle and cheer before expanding the crowd. `AirPitSpectator`
creates one purely decorative original fan at `(9.81, 8.51, 13.93)`, using four skinned parts and two
original clips, cycling automatically. `extract_spectator.py` exports owner-sourced art and animation
to ignored `work/extract/spectator-airpit`; client Release builds stage `spectators/arena_airpit`.
The original imported Euler order is preserved (XYZ, not Unity's default ZXY). No gameplay scripts,
colliders or match-event coupling. Existing map bundles, navigation and DynamicMaps are unchanged.

Verified: 14 focused tests, 184 existing door-pose tests, clean Release build, full distribution
validation; two fresh AirPit raids on GPU-2 with exactly one actor, visible idle/cheer, zero warnings
or errors on both starts, and zero actors after both exits. Close-ups were taken with a test-only
teleport onto the gallery. The original position is partly obscured from ground level by its railing
and corrugated panels. GPU-2 was left in the main menu, healed. No release/push. Details and evidence:
[airpit-spectator.md](docs/progress/airpit-spectator.md). Remaining crowd, match reactions and audio
are outside this prototype; first let the user judge this one.

## Update 2026-10-25 - exit lamps, gate sounds, score monitors, spawn and puddle fixes (committed 490e227, 5342400, d13f074, cc2f024; deployed; host-verified on GPU-2)

User request (2026-10-25, after the survey of open map work, `work/reports/offene-kartenarbeit-2026-10-25.md`, German, untracked): "werde mir die looks
nochmal ingame anschauen/anhoeren. setze bitte schonmal punkte 4,5,7 um. wenn wir die score-monitore auf allen maps fuer die anzeige des punktestandes
im team-dm nutzen koennen waere das super." Points 4, 5 and 7 of that list are the exit lamps (Equator's exit signs, Air Pit's `BUS_LIGHT`), the gate
sounds, and the small data fixes (spawn points on the NavMesh edge, puddle footsteps on Equator, Air Pit and Bowl, Bowl's guard towers). All of it is on
`main`; the tree was clean at cc2f024.

What was built:
- **490e227 - exit lamps, spawns, puddles.** Equator's four `EXIT_LIGHT_EMISSIVE` lamps and Air Pit's three `BUS_LIGHT` lamps get `switch` rows from
  Arena's own gate-script data (Equator 18 door rows, Air Pit 36; Bay5 and Bowl unchanged); `BoothDoorRepairs.OpenLamps` registers them as gate parts
  (on at raid start, dark during a hold, lit in the first frame of a release) and `ArenaLightingRepairs.SyncSwitchedOn` re-applies the map's light
  multiplier after a late `CullingLightObject.Awake`. No bake input changes. Four spawn rows moved 1.28 to 1.50 m inboard (Equator `bot-south-west` and
  `bot-south-east`, Bay5 `player-north-west` and `bot-east-lane`; `base.json` of both regenerated; the message of 490e227 says 0.8 to 1.5 m, the coordinates give 1.28 to 1.50 m). Puddle footsteps on Equator, Air Pit and Bowl are
  driven by the descriptor (`PuddleWaterMaterialNames`, new `PuddleExtraSurfaces`: Bowl counts its soil floor) with a bounded cache rebuild
  (`PuddleCacheGate`). Records: [door-poses.md](docs/progress/door-poses.md), [equator-booth-spawns.md](docs/progress/equator-booth-spawns.md),
  [puddle-footsteps-all-maps.md](docs/progress/puddle-footsteps-all-maps.md).
- **5342400 - gate and door sounds.** `extract_gate_sounds.py` decodes Arena's gate sound players into `tools/maps/<stem>.gatesounds.json` (25 rows) and nine
  clips with precomputed seam fits (ignored `work/extract/gate-sounds`, staged to `dist/.../gate-sounds/<key>/`); Air Pit's two clips are pitch-baked so no
  shipped file copies a stage-2 key-source file. `GateSounds` plays a release's rows through `BetterAudio` at Arena's -15 dB level, silent in a plain raid.
  Developer keys only (`Spawns/GateSounds`, `GateSoundVolume` 0 to 8, `GateSoundFit`, `GateSoundRolloffScale`). Record: [gate-sounds.md](docs/progress/gate-sounds.md).
- **d13f074** `test_changelog.py` accepts a leading `## Unreleased` section.
- **cc2f024 - score monitors.** Arena's TV props show a mode's timer, team score and alive counts: tracked holder rows `tools/maps/<stem>.monitors.json`,
  a nine-sprite artwork package (ignored `work/extract/monitors`, staged to `dist/.../monitors/`), the public append-only contract
  `ArenaMapPort.Client.ScoreMonitors` (ContractVersion 1) and the developer demo `Monitors/Demo = Cycle`. ArenaMode itself is not changed; the binder and the
  Team Deathmatch mapping are code blocks in [score-monitors.md](docs/progress/score-monitors.md). `dist/` now holds 113 files (90 before) and every Release
  client build, DllOnly included, needs `extract_gate_sounds.py clips` and `extract_monitor_templates.py` first (AGENTS.md "Build, test, deploy").
- **Bowl's guard towers: no change.** A probe batch showed both big towers on the connected NavMesh (the 2026-10-18 "west tower has no mesh" note was the
  probe radius) and the two small towers as islands outside Arena's play area: [bowl-release.md](docs/progress/bowl-release.md) "Correction 2026-10-25".

Host evidence (GPU-2, 2026-10-25 17:03 to 17:22; build cc2f024, client DLL 1,160,192 B and server DLL 156,672 B built 17:03; deploy 17:05 through
`deploy-to-spt.ps1 -Confirm`, plan 113 files, 23 new, 4 overwritten, 86 identical; 2300 files identical on the host after 141.1 s, server started; EFT
started 17:08:21, 16/16 identical; test values `Monitors/Demo` = Cycle and a 20 s developer hold, `persist=false`; five raids on four maps and a hideout open;
0 Warning and 0 Error lines from the mod in the whole process, the only Error is Unity's pre-existing `MenuScreen` coroutine message):
- **Exit lamps.** Equator: four `park:` lines, `exit lamps 'EXIT_LIGHT_EMISSIVE': 4 lamp part(s), 8 switch(es), 4 on`; the lamp light inactive during the hold, 8
  switches at the release, `float_1` 0.875 (= 1.75 x 0.5, the multiplier survived), intensity 0.867 at 8.5 m, visible. Air Pit: 8 switches at the release and the
  six `BUS_LIGHT` lights active afterwards. No still of a lit sign was taken.
- **Spawns.** Equator `all spawn points already free` (before, `bot-south-west` was moved 1.5 m on every raid); Bay5 spawned on the moved `player-north-west`
  point, the same line.
- **Gate sounds** (logged, not listened to; the host is muted). Prepared clips with their seams on every map (Equator -1.303 s, Bay5 -1.205 and -1.999 s, Air
  Pit whole, Bowl -2.900, -2.403, -3.001 and -2.301 s); the release lines: Equator 5 rows / 2 playing, Air Pit 11 / 3, Bowl 4 / 2, each `one frame later` all
  playing, 0 failed; Bay5 5 rows / 0 playing (all beyond 30 m from the spawn). The Equator plain raid played nothing, as designed.
- **Score monitors.** `[Monitors]` 9 of 11 (Equator), 8 of 8 (Bay5), 6 of 8 (Air Pit), 6 of 16 (Bowl); font `Jovanny Lemonad - Bender Normal SDF`; the big sprites
  load in 16 to 24 ms each, one per frame; surfaces built 3 per frame in 0.5 to 4.0 ms (Equator, Bay5). Stills: the score face on Equator, Bay5 and Air Pit,
  Equator's atrium row with score, timer and alive, and Bowl's timer board, readable at 65 m with no z-fight. Negative control: `Monitors/ScreenLift` = 0 turns the screens dark grey, 0.01 brings them back. The
  Equator north booth screen is read from inside the exfil box (extracted after 8 s).
- **Puddles.** After a walk `cached water surfaces=40 masks=2` (Air Pit) and `surfaces=13 masks=1` (Bowl), one 55.5 ms frame for Bowl's one-time build.
  Equator's line is not in the evidence (no walk is recorded in its raids).
- **Done signal** ([spawn-gates.md](docs/progress/spawn-gates.md)): Equator `gates open` 14 parts (was 10, the lamps count), Bay5 7, Bowl 4, and Air Pit `gates open
  (released): 42 part(s) registered, 28 park carver(s) in place`, the first Air Pit record.
- Frame time 119.7 to 119.8 fps, mean 8.4 ms; maxima of 33.0 ms (Equator window, not attributed) and 55.5 ms (Bowl, the puddle cache).

Open:
- **The user's look and listening pass** (his words above). The gate sounds were never heard: loudness (`Spawns/GateSoundVolume` goes up to 8 since today, default 1
  = Arena's -15 dB), reach (`GateSoundRolloffScale`), seam audibility (`GateSoundFit`, Equator's is the marginal one) and the passport level; Bay5's sounds never
  played in the session (out of reach), so a run next to a roller is needed. The lit exit signs and the monitor faces by eye. The Bowl and Bay5 looks (day and night,
  lamps by day) of the open-work list belong to the same pass.
- **ArenaMode adoption, by the user's other agent** (we never edit ArenaMode): the score-monitor binder and the Team Deathmatch mapping, after which host check B of
  score-monitors.md runs; and `SpawnGates.GatesOpened` instead of the fixed 13.5 s (now host-recorded on all four maps).
- **Not host-checked:** rain-driven puddles (`USERAIN`) and the F10 probe at the documented points, Equator's puddle cache line, a bot raid (bots at the moved spawns, in
  the booths, through the bus doors), Air Pit's plain-raid lamp lines, `SpawnGateDrive` false, a Hold during a drive, a Hold that stops running sounds, and items 4 to 8
  of monitor check A (puddle and monitor records list what is missing).
- **Release.** The next release number and notes are the user's decision. CHANGELOG.md "Unreleased" now lists the five changes of today (and Codex's `NO_CAUSE` fix);
  0.3.0's "Known limitations" line that gates move without Arena's gate sounds is history for the next note. The ZIP is recut with `make_release_zips.py` (113 files).
  Test counts after cc2f024 are not recorded in the docs (2296 dotnet tests passed for the tree at 490e227): run all suites before a release.
- **Housekeeping, unchanged:** 0.2.3 is to be deleted (user), the Forge entry (user), an off-VM backup of `work/release-keys`, the deploy to `D:\SPT41vanilla`, the v9
  plan-digest drift of `validate_extract`.

## Update 2026-10-24 - do not follow an EFT load error with an unsubstantiated map-error dialog

The reported Bay5 attempt was refused by ArenaMode's PFT protection, but MapPort subsequently
showed `NO_CAUSE` despite recording zero map failures. The native error observer now marks only
the open load attempt; that marker suppresses only the no-cause fallback. Specific map failures,
the original EFT error and all loading/cancellation behavior stay intact. A retry resets it.
Client Release build: zero warnings/errors. Full .NET suite: 2,280 passed, zero skipped.
Client-only deployment through `deploy-to-spt.ps1`; no map/server/config changes. This failure
sequence has not been deliberately repeated in-game after the fix; automated scheduler/wiring
coverage and the exact original log are recorded in [map-notices.md](docs/progress/map-notices.md),
the final 2026-10-24 section.

## Update 2026-10-24 - all four DynamicMaps overviews refreshed for original gate poses (data-only deployed)

Equator, Bay5, AirPit and Bowl regenerated from current native triangulation and `.doors.json` open combat poses.
The exporter preserves parked moving colliders and unanimated gate structure; all 66 pose rows resolve, all SVGs remain
below 16,000 points. Bay5's automatic north drawing margin increases by 1 m (Z74 to75); exits/world axes are unchanged.
Accepted `work/dynamicmaps/release`, `dist` and `D:\SPT41` contain matching map data. No DLL/map rebuild; the parallel
gate-completion API work is separate. 61 Python tests, 122 deployment fixture cases, exact SVG render review and four
acceptance gates passed; 91 third-party DynamicMaps files/configs unchanged. Native EFT retest not run; restart EFT to
clear cached maps. Full record and source/output receipts: [dynamicmaps-gate-refresh-20261024.md](docs/progress/dynamicmaps-gate-refresh-20261024.md).

## Update 2026-10-24 (evening) - 0.3.0 RELEASED: done signal for mode plugins, changelog and release texts (a673568 + e1f5428 + 67b75b1 + 00f6914, deployed, host-verified, published)

User decisions: 0.3.0 is to be released; a CHANGELOG.md from 0.2.3 on; README/FORGE updated; and, quoting a Codex review of ArenaMode (its
team-box catalog waits a fixed 13.5 s on Bowl while the drives now take at most 6.75 s), a public done signal in `SpawnGates`:
`IsOpening`, `GatesOpen`, `OpeningSecondsRemaining` and the event `GatesOpened` (contract in the class doc and spawn-gates.md "the done
signal"; ArenaMode is to wait for it, with its path checks, instead of the constant). Host-verified on GPU-2 the same evening (spawn-gates.md
"Host evidence of the done signal"): Equator `gates open (navmesh installed)` once, Bowl `gates open (released)` in the frame the 6.8 s drive
ends, Air Pit with bots (path checks through the bus doors pass; a bot walking through was not observed), hideout once, 0 Warnings.
Release texts: CHANGELOG.md (tested by `test_changelog.py`), docs/releases/v0.3.0.md (Download block filled at the cut and at publication), README, FORGE,
the package README in `make_release_zips.py`. Codex (the user's other agent) committed the refreshed DynamicMaps overviews (e652fc9) and
deployed them. The cut is done (2026-10-24 14:28, `make_release_zips.py --single --force --extract-check`: `maschine-ArenaMapPort-0.3.0.zip`
3,647,749,962 B = 3.40 GiB, SHA-256 `9ed79ac488a9cd1659199fb87244e9df203ba3b6c284c87614a007012c9b559b`, 94 entries, extract-check OK against dist, built from `67b75b1`; the DLLs were built at 14:10 from the a673568 tree,
five minutes before that commit, so their informational version reads `0.3.0+e652fc9`; VirusTotal 0 malicious, 0 suspicious for both
DLLs, 70 and 71 of 75 engines returned a verdict), and the Download block of docs/releases/v0.3.0.md holds these values;
**Published 2026-10-24** (user decision "ja, gerne veröffentlichen. 0.2.3 wird später gelöscht"): Pixeldrain list https://pixeldrain.com/l/UtjWQemL
(ZIP https://pixeldrain.com/u/J1ajrRA8, checksum https://pixeldrain.com/u/RTaSMHNm), tag `v0.3.0` on the publication commit, GitHub release "ArenaMapPort 0.3.0" with the
`.sha256` asset and the notes as body; 0.2.3 stays until the user deletes it. Still the user's: the Forge entry (FORGE.md), deleting 0.2.3
later, and ArenaMode's switch from its fixed 13.5 s to `SpawnGates.GatesOpened`. Open: an off-VM backup of `work/release-keys`; `D:\SPT41vanilla`; BUS_LIGHT lamps,
fences, gate sounds (BUS_LIGHT lamps and gate sounds done 2026-10-25, see the update at the top); the v9 plan-digest drift of `validate_extract`.

## Update 2026-10-24 (later) - AirPit door motions and Bay5 `Door_01 (3)` shut again (committed fa76bc0, deployed to D:\SPT41, host-verified on GPU-2)

Host verification 2026-10-24 13:04-13:17 (four raids, hideout once, 0 Warnings in the whole process; quotes in [door-poses.md](docs/progress/door-poses.md)
"2026-10-24 - host verification on GPU-2 of fa76bc0"): AirPit plain raid parks the three front bus doors at the back row's pose (hinge 127 degrees),
the back row stands, the passport leaves stay closed and carved (16 carvers, probe sealed, the parked bus doors "not judged"), the south pocket's
exits stay open for agents (dryRun path 152.7 m) and a walk passes both bus door rows; the developer hold + release drives 21 parts in 1.5 s
(max 8.4 ms), flips RedFog/GreenFog, opens every probed doorway, and a walk from the south pocket reaches the north pocket through a lane; the
green arrows show on the booths' NORTH face (the pocket side), the hall side carries only the X quad by Arena's data (an earlier "green haze"
was the North Gate exit smoke). Bay5: `(3)` is a wall again (walk 2.5 m partial). Open: the 0.3.0 ZIP (superseded), vanilla, BUS_LIGHT lamps,
fences, gate sounds (BUS_LIGHT lamps and gate sounds done 2026-10-25, see the update at the top), the v9 plan-digest drift of `validate_extract`.

User request (2026-10-24): Bay5's roller `Door_01 (3)` (tarp and carton stack in its doorway, no motion and no sound in Arena) goes shut again, then AirPit the same way as Equator and Bay5. Implemented on the working tree (uncommitted): `tools/maps/bay5.doors.json` 7 rows (the `(3)` row, its `opened_barriers` entry and its descriptor literal removed) and the new `tools/maps/airpit.doors.json` (30 rows: 12 borrowed rows that open the three front bus assemblies to the pose of the standing-open back row `(3)` at the port's 3.0 s, 16 passport leaves from the DotAnimator at 1.7 s, and two `switch` rows that flip the RedFog / GreenFog lamps); sidecar schema 2 gained the optional `switch` object (`ArenaDoorPose.ClosedActive/OpenActive`), repeated part names are told apart by the closed position, the passport gate opens its leaves with the hold release, and the sealed navmesh probe skips a part parked at its open pose (it raised a false "gate is NOT sealed" Warning on every plain AirPit raid); the bake (`BakeNavMesh`, `ArenaPaths`) drops only the moved subtrees and warns when a descriptor lists barriers but its sidecar is missing; two lines of `test_lamp_fill.py` follow the new sidecar lookup. Built and checked on the VM: dotnet 2261/2261, Python 2119 OK (55 skipped), Bay5 rebaked (triangulation 22141/10223 -> 22130/10220, only the `(3)` slab footprint lost, the other five gates cell-for-cell as walked on 2026-10-24), AirPit rebaked (11787/5423 -> 11811/5433; 1.81 m2 lost around the six back-row leaves, nothing gained, all 28 spawn and exit points on the main component, routes from the south spawns to the field exist, the three back-row doorways keep 0.5 m of centre line), `dist/` restaged encrypted (`DISTRIBUTION_OK 4 maps 12 bundles 4 sidecars`; four files changed), deploy DRY RUN = 6 files (both DLLs and the Bay5 / AirPit scenes and NavMesh bundles, about 1.9 GB), `base.json` and the presets unchanged. Nothing was deployed to `D:\SPT41`. Host test (GPU-2) pending: AirPit plain raid, AirPit developer hold, Bay5 `(3)` check, Bowl untouched, recipe in [door-poses.md](docs/progress/door-poses.md) "2026-10-24 - integration"; the item to watch first is the south pocket's three front bus doorways with the park carvers (`uibridge_teleport dryRun`, `uibridge_walk`). Known and not caused by this change: AirPit's `validate_extract.py` stops at "source plan digest drift" (commit `275b668` corrected one date inside the level38 `select.reason`, the v9 manifest still holds the old digest); the operator decides between re-pinning the manifest and re-extracting.

## Update 2026-10-24 - Equator and Bay5 gate motions, Equator spawns in the booths, drive scale (committed f3fe11e + c699420, deployed to D:\SPT41, host-verified on GPU-2)

User request (2026-10-23): Equator's spawns behind the booth doors instead of the FCK shop and the FCK shutter shut again; every Bay5 gate open
with Arena's own motion at half the original time. Implemented from the DotAnimator decode of [door-animations-survey.md](docs/progress/door-animations-survey.md):
sidecar schema 2 with `tools/maps/equator.doors.json` (10 rows) and `bay5.doors.json` (8 rows), `BoothDoorRepairs`/`SpawnGates`/`DoorPoseModel` park, hold and
drive the booth leaves, the Bay5 rollers and both depot gates (instance by world position), new `Spawns/SpawnGateDriveScale` (default 0.5, Bowl included),
Equator's four player spawns on Arena's booth markers and `opened_barriers` empty. Both NavMesh bundles were rebaked on 2026-10-24 (Equator 10404/4572
-> 10395/4567 triangulation, the FCK hall is a cut-off 95-polygon pocket; Bay5 22117/10213 -> 22141/10223, `Door_01 (2)`, `One_man_Door` and depot gate 822
now open), the scenes bundles are content-equal, `dist/` is restaged encrypted (`DISTRIBUTION_OK`), dotnet 2215/2215, Python 2044 OK, deploy dry run =
7 files (both DLLs, 4 bundles, Equator `base.json`; ~1.6 GB). Deploy order and host recipe: [door-poses.md](docs/progress/door-poses.md) "2026-10-23 - Equator and Bay5
door motions ..." and [equator-booth-spawns.md](docs/progress/equator-booth-spawns.md) (restart the SPT server before EFT). Bay5's unnumbered `Door_01` and `Door_01 (1)`
stay shut on purpose (static twin, planks, fence). The 0.3.0 candidate ZIP of 2026-10-18 is superseded (old NavMesh bundles, old Equator database, old DLLs).
Committed as `f3fe11e`; deployed on 2026-10-24 (7 files, server restarted before EFT). The first host raid found the one defect: `SpawnPlacementRepairs`'
booth-avoidance rule of 2026-09-14 pushed the four booth spawns 6 to 7.5 m out again (Usec into the revolving entrance door, cut off from the floor);
removed in `c699420` (deployed, EFT and server restarted). Then host-verified in five raids plus a hideout open, 0 Warnings in the whole process
([door-poses.md](docs/progress/door-poses.md) "2026-10-24 - host verification"): Equator spawns on marker 6 behind the parked-open booth door, walk out
through the door, FCK hall unreachable and the shutter a wall; developer hold seals the booth and the release drives 10 leaves to x +-2.0 in 3.5 s
(max 8.3 ms); Bay5 parks the four rollers (y 3.222 / 3.181 / 3.213 / 3.323, 0 carvers, slabs above the walking band) and both depot gates (leaves
162.4 / -141.8 and 138.8 / -99.0 degrees, Telfer posts standing, 6 park carvers), walks straight through all six doorways on the new mesh, hold
and release in 3.5 s; Bowl at the new default: 6.8 s, 23 park carvers, max 12.2 ms. Known: the NavMesh probe cannot judge Bay5's root-posed
rollers and hinged depot leaves ("not judged", Debug only); `D:\SPT41vanilla` not deployed; the 0.3.0 ZIP is not recut.

## Update 2026-10-23 - server not-found line in red, launcher advice once

User request: the server's failure line in red, without the doubled text. The shared reasons of the registry causes no longer carry
advice (each side adds its own once), and the server logs the not-found verdict with `LogWithColor(..., Color.Red)` at Info level.
Verified on GPU-1 (no Arena): red in a console started from the `SPT.Server` shortcut; a console started by gpu-run's host helper shows no
colours at all (also SPT's own green lines), see AGENTS "Release configuration". Record: [arena-install-check.md](docs/progress/arena-install-check.md)
("Red not-found line, advice once"). GPU-2's server still runs the previous server DLL until its next restart (it is verified there, so
its line stays white either way).

## Update 2026-10-22 - in-game map notices: why the Arena maps are missing or failed to load (host-verified)

User request (2026-10-22): show a notice in the game when the maps cannot be loaded, with the reason; a player had written only "I have Arena,
the maps do not show up". Design, texts (English and German) and host evidence: [docs/progress/map-notices.md](docs/progress/map-notices.md).
- **Client:** a modal at the first main menu when the maps are not available (the client's own Arena check; the server's reason via the new
  read-only route `GET /arenamapport/status`; missing package files), a modal at the next main menu after a failed Arena raid start naming the
  cause (Arena check, unlock-file causes, damaged bundle), toasts for the location selection, a pending check and a raid without NavMesh. Log tag
  `[Notice]`, one Info line per shown notice with a ref code. New public entry `General/ShowMapNotices` (default on; hides the main-menu notices
  only, never the check, and never the answer to a failed raid start): the public file now has four entries, F12 three normal controls.
- **Server:** `ArenaServerStatus` records the gate and per-map outcomes, `ArenaMapPortStatusRouter` serves them as codes (no paths, no reason
  text, no hash). `ArenaInstallationResult.Cause` (enum `ArenaInstallCause` in `ArenaMapPort.Shared/ArenaStatusContract.cs`); when no registry
  entry is found the client logs one extra `[Arena] no registry entry; Uninstall subkeys matching Arena or Tarkov: ...` line for support.
- **Verified (2026-10-22, 21:07-21:27, DLLs 0.3.0+0dd0b2a plus the tree):** GPU-2 positive (no notice, `offered=4/4`, Bowl raid, hideout, 120 fps);
  GPU-1 without Arena (`ClientNoArena` modal at the first menu, the selection toast); GPU-2 with a server override to a missing folder
  (`ServerNoArena` with the server's cause through the route, 200 in 16 ms); GPU-2 with a Bowl key file of one wrong size (`KeyDiffers` modal 17 s
  after the refused raid). Not run on a host: V4 (client override, the config is mirrored to every machine), V6-V8. dotnet test 2123/2123, Python
  1950 OK (55 skipped; `test_door_poses` needs `PYTHONUTF8=1` on this console, unrelated). The stage-2 timing test
  `CryptoThroughputTests.Cng_ReachesAtLeast300MiBPerSecond...` failed once in seven runs under parallel load (236 MiB/s), unrelated and flaky.
- **Left on the installs:** `SPT_Runtime/user/mods/ArenaMapPort/config/arena-install.json` = `{ "installPath": "" }` (explicit "no override", no
  warning) on every machine the mirror reaches, from the V3 restore; the user may delete it.
- **Next:** the notices reach players with the next release; the reporter is on 0.2.3, so a 0.2.4 backport of the stage-1 part is the faster
  route (map-notices.md "Release vehicle"), the user's decision. Ask the reporter for the `[Arena]` log lines (the new registry line will name a
  non-standard launcher entry such as a Steam one).

## Update 2026-10-22 - door poses: Bowl's BTR, ambulance and gate leaves park in Arena's end pose (committed 736d620, deployed to D:\SPT41, host-verified on GPU-2)

User decision 2026-10-22 ("ja gerne in Endstellung parken") on the 2026-10-21 finding: Arena animates Bowl's four door parts to an end
pose, the port hid them. Data: the clips were decoded from `sharedassets48.assets` (read-only) into 19 poses (BTR body + 8 wheels, the
respawn gate leaf, the two army leaves, the ambulance root, hood, tow rope and 4 wheels), sidecar `tools/maps/bowl.doors.json`
(generated by `tools/extract/extract_door_poses.py`, names and numbers only) mirrored 1:1 by `ArenaMapRegistry.Bowl.DoorPoses`
(`ArenaDoorPose`, `DoorPosesOf`) and checked by `validate_map_descriptors.py`, `BowlMapTests` and `tools/tests/test_door_poses.py`;
the respawn gate leaf slides 3.8 m onto its rail (a local-y misreading said "sinks", corrected in the docs). Runtime:
`BoothDoorRepairs.OpenBarriers` parks every part that has poses in its open pose instead of `SetActive(false)`
(`SpawnGates.NotePoses` / `Park`), a hold puts them back, `SpawnGates.Release` drives them over their clips' 7.7 / 8.3 / 10 / 13.5 s
(smoothstep stand-in, new key `Spawns/SpawnGateDrive`, default true, false snaps) and 24 park carvers keep bots out of the
bodies (the army frame and the gate rail stay uncarved); parts without poses (Equator and the other maps) behave as before.
Built and tested on the VM (client and server Release 0 warnings, 1,565 xunit, 1,950 Python with 55 environment skips, descriptor
validator OK). Committed as `736d620` (with the guarded `SealParked("released")` in `SpawnGates.Open`, `Carved` set before the
collider loop and the softened sidecar comment; client DLL sha256 `e94b581b...`, server DLL `6703abe3...`), deployed to `D:\SPT41`
and host-verified on GPU-2 the same day (raids 8 and 9 in [bowl-release.md](docs/progress/bowl-release.md)): plain raid parks all
four parts with 24 park carvers, the parked BTR's rear closes the user's "missing wall" (carved edge at x 50.5 = Arena's), the
developer hold seals the pockets with 28 carvers and the release drives the parts in 7.7 / 8.3 / 10 / 13.5 s at 120 fps (`drive
frames` max 11.2 ms), probes open, field reachable, hideout once, 0 warnings. `D:\SPT41vanilla` not deployed. Still open from the
reviews: a second `Hold` after parking snaps the BTR back in one frame (ArenaMode round restarts; nobody is pushed out), `IsHeld`
turns false while the doorways stay sealed for up to 13.5 s (ArenaMode should expect it or use `SpawnGateDrive = false`), the
layer-29 boxes under the parts are active at the parked pose (0.17 m2 of footprint, harmless), a hold during a drive and
`SpawnGateDrive = false` are untested on the host. Record: [door-poses.md](docs/progress/door-poses.md), [spawn-gates.md](docs/progress/spawn-gates.md)
("Follow-up 2026-10-22"), [bowl-release.md](docs/progress/bowl-release.md) (2026-10-22).

Later (user remark 2026-10-22): replay the door animations of every map instead of hiding them (Equator's booth doors, Bay5's
warehouse/depot gates, AirPit's bus doors, shop shutters, portal gates) with the same pose table; the Equator slide
(`SpawnGateModel.SlideDistance` 1.38 m, hand-measured) is the first candidate to migrate to clip data. Facts for that step: the
sidecar carries end poses only and the client moves between them with a smoothstep, Arena's curves differ per part (the BTR is steep
in the first half, the ambulance stops at 70 % of its clip, the tow rope jumps at 4.58 s, the wheels turn at their own rate), so
replaying needs a sampled curve per node (the extractor already evaluates every curve at any time); the sidecar's closed pose is the
port scene's transform, not Arena's `*_closed` clip; only Bowl has Animators with clips under an opened barrier today (Equator's
slides are inferred from geometry, Bay5 has none under a barrier, AirPit's three bus-door Animators use a controller with no clip),
so the other maps need their motion measured or authored first.

## Update 2026-10-20 (evening) - key source v2: the map keys come from sampled resource streams (deployed, host-verified on GPU-2)

The user's PC on Arena 0.6.0.0.47752 could not open Air Pit (`[BundleKeys] ... have the listed sizes but do not unlock the key`): the 0.6.0 to
0.6.1 hotfix rewrote all 28 level files of the four maps with the same sizes, while all 26 raw resource streams of their scenes
(`sharedassets<N>.assets.resS` / `.resource`) stayed byte-identical. Each map's key-wrapping key is now derived from a fixed sample of those
streams (16 blocks of 64 KiB per file at size-only offsets, small files whole, path, size and sample bound into each digest, labels `v2`);
`K_map` and all twelve bundles are unchanged (staging said `unchanged`, SHA-256 equal), the key files are schema 2 with a `sample` object, the
client refuses schema 1 (never released), the key store is schema 2 and keeps the retired level-file wraps (backups
`20261020-before-sample-v2` and `20261020-sample-v2` next to the first one). The tool takes `--sources` (`--levels` remains an alias) and
`bench --keys-dir`. Proof: `verify-keys` unlocks all four shipped key files against the VM's 0.6.1 and the user's 0.6.0 install on the
read-only share (11 to 55 ms local, 60 to 180 ms over SMB); GPU-2 (18:38 to 18:45, 17/17 identical) loaded Equator, Bay5, Air Pit and Bowl with
`[BundleKeys] key for '<map>' derived in 29 / 55 / 37 / 43 ms (... MB sampled of ... MB, ... crypto: CNG)`, unchanged NavMesh counts, `diff`
1.28 to 1.46, hideout open, no Warning or Error. Fixed on the way: a file changing size during the key read was reported as `ContentDiffers`.
Tests: 1,475 xunit, 1,895 Python (55 skipped), 8 log-scan; `DISTRIBUTION_OK`; secrets scan 0 hits. Record:
[bundle-encryption.md](docs/progress/bundle-encryption.md) "Key source v2"; design: [PLAN-ENCRYPTION.md](docs/PLAN-ENCRYPTION.md) section 6.
Does not survive a Unity upgrade, re-imported textures or meshes, or shifted scene indices: those need a staging build on the new Arena and a
keys update. `D:\SPT41vanilla` has not received this build. Not pushed.

## Update 2026-10-20 (afternoon) - Equator and Bay5 rebuilt with chunked LZ4 (deployed, host-verified on GPU-2); mesh id collision found and fixed

`BuildBundles.cs` builds every map with `ChunkBasedCompression` now. Equator and Bay5 were rebuilt from their accepted v5 packages:
6,943 and 11,807 LZ4HC blocks of at most 128 KiB instead of one LZMA block, uncompressed content byte-equal to the September builds
(909,949,416 and 1,547,454,232 B), NavMeshData byte-identical to the released bundles (10404/4572, 22117/10213), presets and `base.json`
unchanged. Staged into `dist/` (the four bundles rewritten, key files unchanged, `DISTRIBUTION_OK 4 maps 12 bundles 4 sidecars encrypted`,
`verify` OK for all four, dist +145.9 MB and +258.8 MB), tests green (1,893 Python, 1,440 xunit, 8 log-scan).
Deployed to `D:\SPT41` at 15:49 (90 files, 6 changed: the four bundles plus the rebuilt client and server DLLs of the same source; the
user released the hosts first) and host-verified on GPU-2 at 15:51 to 15:57 (commit `53590de`, 17 of 17 plugins identical): Equator
`[Bundles] loaded ... 551 chunks` 21 ms after the preset line (was 15.5 s), EFT `LocationLoaded:3.31 real:4.68 diff:1.36` (was
`diff:16.8`), 37 s to the raid (was 45 s); Bay5 `loaded ... 997 chunks` 33 ms after the preset (was 28.1 s), `LocationLoaded:3.71 real:5.14
diff:1.42` (was `diff:29.56`), 31 s to the raid (was 59 s); NavMesh 10404/4572 and 22117/10213, keys derived in 39 and 48 ms (CNG), 120 fps
with max frame times of 11.1 and 8.3 ms, no Warning or Error from `[BundleKeys]`, `[Bundles]` or `[NavMesh]`, both scenes render (stills
`equator-lz4-20261020`, `bay5-lz4-20261020`). Record with the quoted lines: [bundle-encryption.md](docs/progress/bundle-encryption.md)
"LZ4 rebuild" and "LZ4 rebuild, GPU-2 run" (after E5). The plan's timing budget now holds for all four maps.

Found on the way and fixed: mesh ids (`sha1(file:path_id:type)[:16]`) collide across packages (Bay5 and Bowl: 228 of 798 shared ids name
different geometry), and `MeshImporter.cs` reused the shared `Assets/ArenaImport/Meshes` store for a package without `source.map_key`
(Bay5's legacy manifest), so the first Bay5 rebuild after Bowl's 2026-10-18 import carried Bowl meshes in 228 slots (bake 29,586 vertices,
`validate_bundle_roundtrip.py` 176 mismatches). Every package now reconstructs every mesh; the second Bay5 build is the staged one. The
released September Bay5 bundle audited clean (0 of 3,890 meshes differ). `build_all.ps1` for both accepted packages stops at
`validate_extract.py` on the known Arena-0.6.1 drift; the remaining validators were run by hand (AGENTS.md "Build, test, deploy"). The
Unity build regenerated 14 stand-in shader stubs (committed with the build, as for Bowl and Air Pit).

Next: the 0.3.0 cut as planned below (the release ZIP grows by about 405 MB with the LZ4 bundles); `D:\SPT41vanilla` has not received
this build.

## Update 2026-10-20 - stage 2 host-verified on GPU-2 and GPU-1 (evidence E3 to E7); one LZMA finding for Equator and Bay5

Stage 2 is committed as `c55ef54` (2026-10-19, late) and was deployed to `D:\SPT41` the same night (90 files; the `deploy-wait --server
--force` after it restarted the SPT server on GPU-2 while the user's own session was in a raid there, reported). On 2026-10-20 (12:58 to 13:45) both
hosts ran the DLLs of that commit, record in [bundle-encryption.md](docs/progress/bundle-encryption.md) (E3 to E7, each with the quoted lines):
GPU-2 (Arena 0.6.1.0.48206) loaded Bowl (cold 40 s to `raidStarted`, warm 32 s), Air Pit (31 s), Equator (45 s) and Bay5 (59 s) through the
encrypted bundles, plus a raid with bots on Bowl and on Air Pit and a hideout open; `[BundleKeys] key for '<map>' derived in 45..58 ms (... crypto:
CNG)` once per map and session, `loaded ... encrypted AMPB v1, 960 chunks` for Bowl's 1 GB scene bundle through `AssetBundle.LoadFromStream` (risk
R2 answered, 1.27 x the plaintext read by raid start, 785 ms of crypto), NavMesh counts unchanged (12704/5890, 11787/5423, 10404/4572,
22117/10213), EFT's own `LocationLoaded` lines within a second of the plaintext samples for Bowl and Air Pit, 120 fps, no Warning or Error from
`[BundleKeys]`/`[Bundles]`/`[NavMesh]`. Negative tests through the deploy script and an EFT restart each: N1 (size + 1) and N2 (check digit) gave
the planned `[BundleKeys] key for 'arena_bowl' unavailable: ...` line, the preset bundle was refused, EFT logged `Can't load bundle ... 404` and was
back on the menu in 14 s, Air Pit still worked; N3 (a payload byte, a header byte of the NavMesh bundle) gave the planned `[NavMesh]` lines and the
raid ran without a NavMesh; restore from the byte-identical backups, dry run `0 to overwrite`, Bowl raid clean. GPU-1 (no Arena): the stage-1
not-found lines, `Maps: none available`, no key, bundle or crypto line, hideout and a vanilla raid fine. E4 (a timed plaintext baseline before the
deploy) was skipped by mistake; EFT's `LocationLoaded` lines in the hosts' earlier logs serve as the baseline.

Finding outside stage 2: Equator's and Bay5's scene bundles are single-block LZMA (`BuildBundles.cs` keeps `BuildAssetBundleOptions.None` for
those two entrypoints), so Unity decompresses the whole bundle when it opens it: 16.8 s (Equator) and 29.6 s (Bay5) of EFT's `diff` against 1.3 to
1.6 s on the chunked LZ4HC bundles of Bowl and Air Pit, the same before stage 2 (Equator 2026-09-19: `diff:18.32`). A rebuild of both with
`ChunkBasedCompression` is the fix (new bundles, so staging plus host verification); user decision.

Next: the user's Bowl tests, then the 0.3.0 cut (`make_release_zips.py --single --extract-check`, the real ZIP size and SHA-256 into the Download
block), upload, VirusTotal, tag, Forge. Open decisions unchanged: D7 (server does not check keys), D9 (off-machine key backup), D10 (keys-only
update ZIP), D15 (stage 3 for art and audio).

## Update 2026-10-19 (stage 2) - bundle encryption: integrated and verified on the build VM (uncommitted), host runs open

User decision (2026-10-19, "Stufe 2", right after the merge below): the shipped map bundles are encrypted, and the content key of a
map is derived from the bytes of the player's own Arena level files. Design, work split and host plan:
[docs/PLAN-ENCRYPTION.md](docs/PLAN-ENCRYPTION.md); the record with its evidence placeholders:
[docs/progress/bundle-encryption.md](docs/progress/bundle-encryption.md). It is a stronger requirement than stage 1 and **not copy
protection**: any Arena owner can derive the key or decrypt the bundles, the key is tied to files and not to a purchase, and the art,
audio and derived data in the ZIP stay plaintext. README, FORGE, `docs/releases/v0.3.0.md` and AGENTS.md say so in the same words.

Decided (plan section 0): `AMPB` container (1 MiB chunks, AES-256-CTR, SHA-256 per ciphertext chunk, header HMAC-SHA256); a random
`K_map` per map in the ignored `work/release-keys/<map_key>.key`; the key-encryption key is HKDF-SHA256 over the SHA-256 of the map's
Arena level files (Equator 5 files, Bay5 9, Air Pit 7, Bowl 7, 21.8 to 41.1 MB per map; all 28 exist on this VM's Arena
0.6.1.0.48206); the shipped `keys/<map_key>.json` lists paths and sizes and never a hash; the client decrypts through Windows CNG
(managed fallback, one Warning) behind `AssetBundle.LoadFromStream`, verifying each chunk lazily; staging encrypts into `dist/` through
the new net10.0 tool `tools/bundle/ArenaMapPort.BundleCrypt` and the Python validators read through it. A later Arena build adds a
wrap per map to the key store (same `K_map`, bundles unchanged), so only `keys/*.json` change.

State at integration (23:20, nothing committed; HEAD `cc3a5b0` plus the tree): the CORE, TOOL, CLIENT and DOCS work was integrated and
verified on this VM, record and quoted evidence in [bundle-encryption.md](docs/progress/bundle-encryption.md) "Integration":
`dotnet test` 1440 of 1440 (0 skipped), Python `unittest` OK (55 skipped), client (full), server and tool builds 0 warnings; a full client
Release build staged twelve `AMPB` bundles and four `keys/<map_key>.json` (the first build created the four content keys in the ignored
`work/release-keys`, copied outside the repo at once), a second full build left all 16 files byte-identical; `validate_distribution.py` ends
`DISTRIBUTION_OK 4 maps 12 bundles 4 sidecars encrypted`; the tool's `inspect --verify-chunks` passes on all twelve, `verify` proves each
equals the encryption of its `work/build` bundle and `verify-keys` unlocks each map's key against this VM's Arena; no dist file starts with
`UnityFS`; no Arena level hash or content key appears in any repo or dist file; the deploy dry run to `D:\SPT41` plans 4 new (the key files),
14 to overwrite (12 bundles, both DLLs), 72 identical. One review finding was fixed at integration: the staging build no longer creates a
content key by default (`-p:ArenaMapPortCreateReleaseKeys=true` for the first stage of a map), so a lost key store is restored, never
silently recreated. **Nothing has run in EFT**: the Mono P/Invoke path, `AssetBundle.LoadFromStream` on a roughly 1 GB scene bundle (risk
R2) and the timing budget are the GPU-2 run's questions.

Consequences for 0.3.0: the candidate cut of 2026-10-18 (`work/releases/0.3.0/maschine-ArenaMapPort-0.3.0.zip`, 3,078,043,845 B,
SHA-256 `8f9b6591...ddeb8b`, from `26b9216`) is superseded: all twelve bundles change and a `keys` folder is new (it already predated the
log-prefix change and the 0.2.3 merge). The Download block of `docs/releases/v0.3.0.md` now holds placeholders only (size, SHA-256,
commit), to be filled from the encrypted cut. Ciphertext does not compress and the packer stores the bundles: dist was 3,256,631,057 B
before staging (86 files; bundles 3,216,202,258 B, everything else 40,428,799 B) and is 3,256,734,280 B in 90 files now (the container adds
99,616 B over the twelve bundles), and `make_release_zips.py --plan --single` estimates the single ZIP at 3,242,902,036 B (3.02 GiB), 5.4
percent above the 3,078,043,845 B cut; the real cut is not made. The 0.3.0 keys cover Arena
0.6.1.0.48206 only (one wrap per map, the build on this VM); README, FORGE and the release notes name it. The first
encrypted deploy to `D:\SPT41` moves about 3.2 GB: close EFT first. `D:\SPT41vanilla` is untouched.

Open, in order (plan sections 2.1 to 2.6; steps 1 and 2 are done, see above): (3) a plaintext baseline on GPU-2 (three raids per map, Bowl and
Air Pit) BEFORE the deploy, because the deploy replaces the plaintext bundles; (4) close EFT on the synchronized host, `deploy-to-spt.ps1
-SptRoot 'D:\SPT41' -Confirm` (about 3.2 GB through Syncthing; wait for the sync and check the client DLL's loaded MVID), restart EFT, then
the GPU-2 run (Bowl and Air Pit, cross-map, hideout, WITH BOTS) against the timing budget; the first Bowl raid answers risk R2, a roughly
1 GB scene bundle through `LoadFromStream`; (5) negative tests N1 to N3 (EFT restart after each, restore by a full staging build); (6)
GPU-1 without Arena: no key or crypto line; (7) the 0.3.0 re-cut (a ZIP, the Download block of the release notes) and everything after it stay
the user's. The handoff edits H1 to H5 of the record are done (validate_port through `verify`, the dist-manifest block, the install-check
pointer, the plan's ZIP estimate, the Arena build in the generated README) plus H6, the create-key default.
Open decisions for the user (recommendation in the plan, section 4): D5 lazy key resolution against a prefetch, D7 the server does not
check keys (after an Arena update a map stays listed and fails at load), D9 who backs up `work/release-keys` and the rotation policy, D10
a separate keys-update ZIP, D11 plan B for R2 (a decrypted temporary file, which weakens the claim), D15 a stage 3 for art and audio.

## Update 2026-10-19 (late) - release/0.2.3 merged into main: 0.3.0 = four maps + the Arena installation check; main pushed

User decision: "alles in main zusammenfuehren in 0.3.0 mit Bowl und pushen, anschliessend Stufe 2 der Verschluesselung".
Merge commit `12c3731` (conflicts: version 0.3.0 kept, the merged gate code and its tests use main's client tag convention
`[Arena]` / `[Bundles]` / `[NavMesh]`, the load line is `Version 0.3.0 loaded. Maps: ... Arena: found ...`, AGENTS/HANDOFF/INDEX
carry both sides, `docs/releases/v0.3.0.md` is rebased on 0.2.3). Merged main: client and server `0 Warnung(en)`, server suite
1134/1134, Python 1818 tests OK (55 skips), `DISTRIBUTION_OK 4 maps 12 bundles 4 sidecars`; DLLs rebuilt on the merge commit
(`ProductVersion 0.3.0+12c3731`) and deployed to `D:\SPT41` (3 overwrites). Hosts: GPU-2 (Arena 0.6.1) server
`(registry; 15,351 files, 76.1 GB verified)` + four `is available` lines, client `[Arena]` found line and
`Version 0.3.0 loaded. Maps: Arena: Equator, Arena: Bay 5, Arena: Air Pit, Arena: Bowl. Arena: found 0.6.1.0.48206.`, a Bowl
raid loaded its bundles (scenes 1,006,073,696 B), NavMesh 12704/5890, acoustics 11/19, flares 4/28, 0 ArenaMapPort warnings;
GPU-1 (no Arena) server and client not-found lines, `Maps: none available`. `main` is pushed; `release/0.2.3` stays as the
0.2.3 record (tag `v0.2.3`). Next: stage 2 (bundles encrypted with keys derived from the user's Arena files), plan in
`docs/PLAN-ENCRYPTION.md` once written; 0.3.0 is cut after the user's Bowl tests.

## Update 2026-10-19 - release/0.2.3: Arena installation check

User decision (2026-10-19): Escape from Tarkov: Arena is sold separately from EFT, so from 0.2.3 on the maps work only
with an installed Arena. The server registers the Arena locations only when an installation is found and verified, and
the client serves the map bundles only then. It is a good-faith gate at SPT's own level, not copy protection: no
disable switch, and two path overrides (server `config/arena-install.json`, client `General/ArenaInstallPath`) that
run the same verification as the registry path. Design, floors and host plan:
[arena-install-check.md](docs/progress/arena-install-check.md).

Branch rationale: `release/0.2.3` was cut from `a3188cd` = the published 0.2.2 (`275b668`) plus the host-tested
weather-provider rebind (`6c6a38c`, evidence `d956e2f`) and the thumbnail cleanup (`a3188cd`). The release therefore
carries exactly that and the check. **Bowl stays on `main` for 0.3.0** and is not part of this release: here
`ArenaMapRegistry.All` is Equator, Bay5 and Air Pit, and Bowl's descriptor and `db/arena_bowl/base.json` are only
the retained development identity of a parked map. The check reaches main by a forward merge (open item 3).

State when this block was written (nothing committed yet, version numbers still 0.2.2): the shared check
`ArenaMapPort.Shared/ArenaInstallation.cs` with `ArenaMapPort.Server.Tests/ArenaInstallationTests.cs` passed in
area A's own run (their report, not re-run for this block): Release builds of server and client
`0 Warnung(en)` / `0 Fehler`, full server suite `Bestanden!   : Fehler:     0, erfolgreich:  1015, übersprungen:     0, gesamt:  1015` (141 of them `ArenaInstallationTests`, including the real-install, registry and
sparse-install tests, which ran on this VM; the real installation verified in 195 ms). The two gates are in the tree
as well: the server's (`ArenaMapPort.Server/ArenaInstallationGate.cs`, called from `ArenaMapRegistration.cs`: without a
verified installation no location, locale transformer, weather hook or APBS patch is registered) and the client's
(`ArenaMapPort.Client/ArenaInstallationGate.cs`, started in `Plugin.Awake`, enforced in `BundleHost.TryServe`;
`General/ArenaInstallPath` is bound on the public config and listed in `CompactConfiguration.IsPublic`); their build
and test results belong to the integrator's record, not to this block. README.md, FORGE.md,
[docs/releases/v0.2.3.md](docs/releases/v0.2.3.md) (download and VirusTotal fields are placeholders), the progress doc
and AGENTS.md (a Hard-rules bullet, an Info-logging line) carry the requirement. No host has run it yet.

Integration (later the same day, still uncommitted): version bumped to 0.2.3 everywhere it is pinned; client and server
Release builds `0 Warnung(en)`, server suite 1095/1095, Python suite 1728 tests OK, `DISTRIBUTION_OK 3 maps 9 bundles 3
sidecars`, deploy dry run to `D:\SPT41` 0 new / 3 overwrite (both DLLs and `locales/map-descriptions.json`, whose host copy
also holds Bowl's descriptions) / 64 identical. Added on top of the areas: `General/ArenaInstallPath` in
`MapConfigVisibility.IsPlayerOption` (it is a normal F12 control), the same check in `NavMeshInstaller.LoadOnce` (that bundle
is read from disk, not through `BundleHost`), the requirement in the generated package `README.txt`
(`make_release_zips.py`, so the `readme_text` half of open item 2 is done). Evidence, DLL hashes and the host checklist:
the "Integration" section of [arena-install-check.md](docs/progress/arena-install-check.md).

Open:

1. **Host run: done** (build `ff4308c`, 2026-10-19 19:29-19:40): host 1 without Arena (server and client log the
   not-found lines, 0 Arena tiles, hideout opens), host 2 with Arena 0.6.1.0.48206 (both sides `registry; 15,351 files,
   76.1 GB verified`, 3 tiles, an Air Pit raid through the gate, hideout). Evidence section in the progress doc. Not
   measured: cold-cache duration; the override paths ran in unit tests and the VM probe only.
2. **Published 2026-10-19** (user decision "uploaden, GitHub-Release mit VirusTotal-Links, 0.2.2 restlos loeschen"):
   Pixeldrain list https://pixeldrain.com/l/9jZS5CnU (ZIP https://pixeldrain.com/u/xAdxjoW2, checksum https://pixeldrain.com/u/Uvk85ZiV), VirusTotal client
   0/75 and server 0/75 malicious, tag `v0.2.3` and the GitHub release with the `.sha256` asset;
   0.2.2 withdrawn (GitHub release, tag and Pixeldrain files deleted, see the record in `v0.2.2.md`), then 0.2.1 and 0.1.0 too
   (user decision: no package without the check stays downloadable; Pixeldrain holds only the 0.2.3 files and list, GitHub only
   the v0.2.3 release and tag). Still the user's: the Forge entry (text from FORGE.md) and whether a DLL-only package for 0.2.2
   owners is published (the notes say the full ZIP is the supported path; the DLL-only sentence is a placeholder).
3. **Forward-merge `release/0.2.3` into `main`** so 0.3.0 (Bowl, four maps) carries the check. Expect conflicts in
   `AGENTS.md`, `HANDOFF.md`, `docs/progress/INDEX.md` (both sides prepend entries), `README.md`, `FORGE.md` and the two
   csproj files; keep both sides, and put the requirement into main's 0.3.0 release texts for all four maps. Main
dropped the own `[ArenaMapPort]` prefix from client log lines (`e66d512`), so the client gate's tag
`[ArenaMapPort][Arena]` becomes `[Arena]` there.
4. **Decisions and loose ends.** Tolerances after the review (same evening): a missing file still refuses the
   installation, but `BattlEye\*` and `Logging.config` only have to exist (BattlEye rewrites its files in place, the
   config is user-editable), and a listed file whose full path reaches 260 characters is skipped and counted
   (`LongPathsSkipped`, named in the Info line) because EFT's Mono cannot stat it in a deep installation folder.
   The public config file now has three entries (`General/ArenaInstallPath` joined the two of
   `compact-player-config.md`, which still says two); a UiBridge check that expects two public entries needs the
   third. Deploying goes through `tools/deploy-to-spt.ps1` only; `D:\SPT41vanilla` gets the build only after the host
   run, and that machine needs Arena (or an override) too.

## Update 2026-10-18 (late) - client log lines without the own `[ArenaMapPort]` prefix

User decision: "in ArenaMapPort das Präfix entfernen" (log-prefix convention: BepInEx already prints the source
`maschine-ArenaMapPort` in front of every client line). Branch `feature/log-prefix`: 374 `[ArenaMapPort][<Area>]` tags in
`ArenaMapPort.Client` are now `[<Area>]`, ten bare `[ArenaMapPort] ` prefixes are gone, the load line reads
`Version <x> loaded. Maps: ...`. Server lines (shared SPT logger) and the Unity editor scripts keep `[ArenaMapPort]`;
`tools/unity-log-scan.ps1` still finds `[ArenaMapPort][BuildBundles] DONE`. AGENTS, MAP-RECIPE, SPEC, PLAN-AIRPIT and
PLAN-TWO-MAPS quote the new form; `docs/progress/` and older HANDOFF entries stay as recorded. Evidence: client compiles
(0 warnings), 903 .NET tests, Python tests OK, `UNITY_LOG_TESTS_OK 8 cases`; not deployed, no host test. The cut 0.3.0
candidate ZIP (built from `26b9216`) still has the old prefixes; a release from `main` after this merge needs a new cut.

## Update 2026-10-18 (evening) - Bowl polish integrated, host-verified and merged into `main` as the 0.3.0 candidate; passport-lamp green arrow recorded for later

User decision: "mit der Bowl-Umsetzung starten, so wie du es am sinnvollsten erachtest; den gruenen Pfeil fuer spaeter
festhalten", then "mit den offenen Punkten weitermachen". Plan and decisions in [docs/PLAN-BOWL.md](docs/PLAN-BOWL.md); the
day's log is [docs/progress/bowl-release.md](docs/progress/bowl-release.md). State: Bowl (package `work/extract/bowl-native-v2`,
Unity U1, NavMesh 12704/5890) is in `ArenaMapRegistry.All`, playable and host-verified; the afternoon added the polish items
(commits e9fe9b8, 9540cbd, f408db2, 185f8f8 and the evidence commit after them): the shot-acoustics rule "IndoorMetalThick
bodies of 15 m and more are Metal" (Bowl's Mi-26; Equator/Bay5/AirPit classes unchanged), the Bowl DynamicMaps ground overview
(`work/dynamicmaps/release/ArenaMapPort_Bowl`, profile with the hidden stadium shell, `-DynamicMapsMap Bowl`), the Bowl
MultiFlare package (4 of 28 lights active, all four city-backdrop glows 789-888 m from the field and beyond the 500 m cull, so
it binds and draws nothing; staged so the raid logs no error), the flare/overview staging rows, `FLARE_MAP_KEYS` /
`OVERVIEW_MAP_NAMES` with Bowl, version 0.3.0 in both csproj, `Plugin.cs` and `ModMetadata.cs`, both csproj descriptions, and the
release texts (README, FORGE, `docs/releases/v0.3.0.md` with placeholders for size, SHA, links, commit and VirusTotal).
Evidence: 903 .NET tests, 1818 Python tests (55 environment skips), `DISTRIBUTION_OK 4 maps 12 bundles 4 sidecars`, dist 86 files
(Bowl additions 1,676,831 B), single-ZIP estimate 2.93 GiB (Pixeldrain takes it; a 2 GiB channel needs the 5-part layout).
Deployed to `D:\SPT41` only (not `D:\SPT41vanilla`); GPU-2 process 4eeae3d5 with the 0.3.0 DLLs: Bowl raids at 00:30 (F7 flare
and light probes, the four shot-acoustics rooms, F8 in a container, DynamicMaps MAP tab with 35,530 sprite vertices) and at
12:00 in rain (F8 open sky, F10 border cube), Air Pit after them (NavMesh, acoustics, 16 sealed gate carvers), hideout open:
0 ArenaMapPort warnings, 120 fps, max 11.2 ms. The user played Bowl in ArenaMode teamdeathmatch on their own machine at 13:25
(0 warnings). The developer gate hold on Bowl sealed both pockets and released them after 60 s (raid 6); the east guard-tower
stairs are on the connected NavMesh and the BTR gate extracted a player (raid 7; the west big tower is connected too, the small towers are islands:
bowl-release.md "Correction 2026-10-25"). Not done: vault limits above 1.2 m, the Mi-26
capture point (ArenaMode), the publication of the cut candidate (`work/releases/0.3.0/maschine-ArenaMapPort-0.3.0.zip`, 3,078,043,845 B, SHA-256
`8f9b6591...ddeb8b`, built from `26b9216`: Pixeldrain, VirusTotal, tag, GitHub release, Forge; `D:\SPT41vanilla` only after the
user's decision), ArenaMode catalog/formation for Bowl (user), the green arrow below. User decision 2026-10-18 evening: `feature/bowl`
was fast-forwarded into `main` (`a3188cd` -> `eee77e2`) and deleted; `main` is not pushed.

Open follow-up (Bowl, user report 2026-10-21; implemented 2026-10-22, see the top block): the "missing wall" at the east pocket is the BTR's parked
position. Arena animates the four Bowl door parts (BTR reverses 5.6 m into the slot's west opening, the ambulance drives
4.8 m, the army gate leaves swing open, the respawn gate leaf slides 3.8 m onto its rail); the port hid all four, so both vehicles left openings
closed only by invisible border cubes. Decoded end poses and the proposed fix (park instead of hide) are in
docs/progress/bowl-release.md, section 2026-10-21; the fix (park instead of hide) is built and awaits its host test.

Open follow-up (AirPit, not started): the passport lamps' green arrow. Arena switches the booth lamps by dark cover
quads, not by material: the hall lamps `..._RED_close_LOD0` (the X) and `..._GREEN_open_LOD0` (the arrow) are always lit;
`PASSPORT_CONTROL_DOORS/RED/RedFog` (active: 10 `GREY` covers over the arrows + 18 red fog sheets) and `GREEN/GreenFog`
(inactive: 10 covers over the X lamps + 18 green sheets) swap at the gate opening. Design (review-verified 2026-10-18,
client DLL only): sidecar/descriptor list `held_gate_indicators` = ["PASSPORT_CONTROL_DOORS/GREEN/GreenFog"], GREEN no
longer a gate part (42/36 parts become 41/35), `SpawnGates` switches the indicators on when the held gates open and off
on Hold / scene unload; verify with the dev hold on GPU-2 from inside the north box. Risks: coplanar covers (z-fighting),
the green sheets were never seen live, `AirPitFogSheets=Hidden` would hide them too.

## Update 2026-10-17 - cached weather sky-provider rebind deployed and tested

ArenaMode's map-feature tests exposed repeated native weather, distant-shadow and ambient-highlight
exceptions on Equator after AirPit -> Customs -> Labyrinth in one GPU2 process. The first error occurred
12.038 seconds before ArenaMode created its Equator phase-feedback light. `WeatherRig.ReestablishSingletons`
restored the cached donor's singleton but omitted the independent `TODSkyProvider.Instance` registration.
The candidate adds the exact owned Full/Simple provider rebinds through the existing native API.
Production-method regression: 55 assertions, plus two compiled omission controls rejected at their intended
assertions. Isolated Release DllOnly build including required auxiliary staging passed (0 warnings/errors);
accepted input copies were hash-verified. Root committed `6c6a38c`, rebuilt and deployed only the client DLL
(SHA256 `E4E917381CC524AD224947CF0EF7E530021D8A88899FDC0B0141EE9D75BBAF0B`). GPU2 loaded the exact build:
fresh Equator with local respawn, Labyrinth -> Equator reuse, Customs Yard and Hideout succeeded in one process;
the final 17,512-line BepInEx Error query returned zero entries. The separate native EFT stream retains 68
other entries (material/weather managers, marker reconciliation, etc.); this is not a globally clean log claim.
Direct static provider identity was not
inspected; source and regression checks support that part of the diagnosis. All 2,239 protected non-client files
(including server/bundles) remained hash-identical. Published 0.2.2 ZIP/tag and main dist are unchanged.
See [weather-provider-rebind.md](docs/progress/weather-provider-rebind.md) for the observed chronology,
source limits and the appended deployment/native evidence.

## Update 2026-10-17 - RELEASE 0.2.2 cut again with the crates and published (Pixeldrain, VirusTotal, tag, GitHub release)

User decision: cut 0.2.2 again from the U8 dist instead of a 0.2.3. `make_release_zips.py --single --force --extract-check`
replaced the 1f7ce05 package (U7, SHA `734ce122...`, never uploaded): `work/releases/0.2.2/maschine-ArenaMapPort-0.2.2.zip`,
2,149,195,967 B (2.00 GiB), SHA-256 `d4790b666a07d2dad9257f2b128f5e52bcc6476ceaf954cb827cb4bc6f5e7944`, built from `326d71b` (release notes with the
crates and the redrawn AirPit overview), 67 files identical to dist after extraction. dist == `D:\SPT41` for all 67 files
(hash compare); `D:\SPT41vanilla` still has the previous AirPit DynamicMaps JSONC/SVG (the refresh was deployed to
`D:\SPT41` only). No C# changed since 1f7ce05, so the DLLs built at b680f7e (client) / 1f7ce05 (server) are the 0.2.2 code.
Pixeldrain (`upload_pixeldrain.py --timeout 300`, record `work/releases/0.2.2/pixeldrain.json`): list https://pixeldrain.com/l/rKL2Pt2f,
ZIP https://pixeldrain.com/u/u3Yh5xmn (direct https://pixeldrain.com/api/file/u3Yh5xmn?download), checksum https://pixeldrain.com/u/5y34D3K6. VirusTotal (record `virustotal.json`, scanned 2026-10-17):
client `eea88249211b...` and server `2501e6567e66...` both 0 malicious, 0 suspicious of 75 engines. Tag `v0.2.2` on the publishing commit and the GitHub release
(body = v0.2.2.md without its H1, asset: the `.sha256` file; no DLL-only package because AirPit's bundles changed).
Still the user's: the Forge entry (text from FORGE.md, link to the Pixeldrain list, VirusTotal links), the vanilla server
restart (new server DLL since the U8 deploy), the ArenaMode `arena_airpit` formation.

## Update 2026-10-17 - AirPit DynamicMaps refreshed for the original team boxes

Regenerated the overview from v9 colliders and fresh triangulation of the current U8 bundle;
the former U6 drawing ended before the north box. Bounds now X -60..40 / Z -86..86.
AirPit alone uses 20 cm obstacle simplification to stay within the existing complexity guard;
native ground, spectator exclusion and Equator/Bay5 settings are unchanged. Both original
boxes and all twenty team starts are covered. Only the two owned AirPit JSONC/SVG files
were updated in accepted release inputs, dist and the install, with matching GPU1 hashes.
No bundles or DLLs rebuilt; the existing 0.2.2 ZIP is unchanged.
44 export tests, 104 deployment fixtures and distribution validation passed. GPU1's actual
DynamicMaps menu renderer displayed both boxes with one layer, 37,658 sprite vertices and
no warning/error; no new raid was needed. See [the appended evidence](docs/progress/airpit-dynamicmaps.md#team-box-refresh--2026-10-17).

## Update 2026-10-17 - AirPit package v9: the crates under the tarp (user report), rebake U8

User report (standing at (-29.2, -15.5) facing south-west): a tarp hung in the air, the crate under it missing. Cause: the
tarp `OO/PROPS/INDOOR/BACK/City_Generic_Tarp_01 (1)` is level38 hall dressing (imported), the three cover props under
it (`SOO/reserve_woodBox_medium (9)`, `(10)` and `SOO/cardbox_vinyl`, at (-29.3..-30.7, -17.1..-17.6)) sit under
level38's `SOO` root that the plan leaves out as mode layout, and level324 has neither the tarp nor crates there. User
decision: import the crates. `tools/extract/plans/airpit.json` names the three as explicit `roots`; a roots path names
every same-named sibling, so the five siblings elsewhere (four `cardbox_vinyl` at (-12.6..-13.9, -24.6..-25.2), one
`reserve_woodBox_medium (10)` at (-6.4, -21.9)) are `exclude`d by position (213 entries). Package
`work/extract/airpit-native-v9` (extract 19:36-19:41, `validate_extract.py` OK): Main_hall 13035 objects / 3550 units /
246 carriers, excluded 637, ballistics 38: 1566; the five full scenes and the Light scene are byte-identical to v8.
Pins lifted in `native_contracts/airpit.json`, `test_native_contracts`, `test_scene_select` (MAIN_HALL_V9_PINS, census
variant 13026 / 3545 / 242 / 637, digest db3d1fad...), `test_airpit_bake_inputs` (owners 7929, sources 7747, mask 3023,
Main_hall [2391, 2391, 969]), the package paths of the other tests, `airpit.modes.json` regenerated (source_package only).
Rebake U8 (19:43-20:05, `-FullMap` from v9): 11787 vertices / 5423 triangles (U7: 11771 / 5415), base.json unchanged;
U7 bundles kept under `work/build/airpit-u7-backup/`. GPU-2 (20:09): `added navmesh ... 11787 vertices, 5423
triangles`, the two wooden crates on their pallet and the vinyl-wrapped box stand under the tarp again (still
`airpit-tarp-crates-u8`), no warning. The 0.2.2 package built before this (`1f7ce05`) carries the U7 bundle without the
crates; the crates go into the next package. The plan's three roots went into `b680f7e` by accident (a `git add -A`
during the 0.2.2 notes commit); the exclusions, pins and package references are in this commit.

## Update 2026-10-17 - RELEASE 0.2.2 built (not uploaded yet): SpawnGates, AirPit's original north box, renamed Equator sidecar

Version bump in both csproj, `Plugin.cs`, `ModMetadata.cs` (`1f7ce05`); `docs/releases/v0.2.2.md` self-contained (Included,
Changes since 0.2.1, Installation: the full ZIP is required because AirPit's bundles changed). Package:
`work/releases/0.2.2/maschine-ArenaMapPort-0.2.2.zip`, 2,149,191,505 B (2.00 GiB), SHA-256 `734ce122...a88c9a`, built from
`1f7ce05` (`make_release_zips.py --single --extract-check`: 67 files identical to dist). Not done, on request only:
Pixeldrain upload (`upload_pixeldrain.py --timeout 300`, record `work/releases/0.2.2/pixeldrain.json`), VirusTotal scan of
the two DLLs (`scan_virustotal.py --version 0.2.2`), tag `v0.2.2` + `gh release create`, the Forge entry (text from
FORGE.md). The dist DLLs now say 0.2.2; both installs still run the 0.2.1-versioned DLL of the same code until the next
deploy. The SOO crates under the tarp (user decision 2026-10-17) come after this package: a v9 package and a later rebake.

## Update 2026-10-17 - AirPit's original north team box: bake z 83, the passport control as a held gate, per-map start gates

User decision after the gap analysis below ("die Original-Nordbox: Rebake, Nordtor und Hold-Teilmenge"). Done and
host-verified on GPU-2 (18:41-18:55); details in [spawn-gates.md](docs/progress/spawn-gates.md) and
[airpit-team-boxes.md](docs/progress/airpit-team-boxes.md), both with a 2026-10-17 follow-up.
- Data (sidecar + descriptor in one commit): `bake_bounds` z max 70 -> 83 (the authored-borders AABB again);
  `opened_barriers` regains `PASSPORT_CONTROL_DOORS` + `PASSPORT_CONTROL_EMISSIVE` (12 entries, so the bake leaves them
  out); new keys `start_gates` ["BUS_DOORS"] (the only barrier a hold may close) and `held_gates` [both passport roots]
  (closed at raid start in every mode, carved from the NavMesh install on, opened only by a Release after a hold).
  `ArenaMapDescriptor.GateRoleOf` -> Plain/Start/Held; Equator and Bay5 declare neither key, so every barrier of theirs
  stays a start gate as before. Validator, mirror tests and the bake pins (sources 7740, mask 3020,
  TeamFight_DesignStuff [97, 3, 0], 92 barrier colliders) follow.
- Client: `BoothDoorRepairs` opens by role; `SpawnGates.OfferHeld` / `HeldGatesClosed` / `SealHeldGates`, incremental
  carvers, `_heldGatesOpened`; the probe verdict is per enclosure (`SpawnGateModel.EnclosureContradicts`), so the bus
  pocket's authored-open inner row no longer raises a false Warning. The reflection contract for ArenaMode is unchanged.
  `SpawnGates=false` or `SpawnGateCarve=false` opens the held gates at raid start (an uncarved leaf on a meshed lane
  would trap bots).
- Rebake U7 (`build_all.ps1 -FullMap`, 18:16-18:35): bounds Extents (47, 5, 83), sources 3032, 11771 vertices / 5415
  triangles (U6: 11332 / 5212); base.json unchanged; the U6 bundles are kept under `work/build/airpit-u6-backup/`. All
  ten north markers and the eight lane strips lie on the main component of the raw bake (new test in
  `test_airpit_spawns.py`); the passport booth row of package v8 fills the floor between the lanes, so the turnstiles
  are the only openings (the 2026-10-12 "open floor between them" observation was made on package v6).
- Host: plain AirPit raid `PASSPORT_CONTROL_DOORS ... 18 part(s) closed until Release`, EMISSIVE 18, `navmesh installed:
  16 carver(s) ... 36 held-gate part(s)`, `navmesh probe: every probed gate sealed (16 probed)`, dryRun to a north
  marker and to a lane strip not reachable, the hall reachable, no warning. Developer test hold 60 s: 40 carvers,
  `every probed gate sealed (22 probed)`, `released ... 42 gate part(s) switched off, 40 carver(s) removed`, `every
  probed gate open (22 probed)`, dryRun to the north marker reachable, `uibridge_walk` 22.5 m through a lane to marker
  (6): arrived. Hideout opens. Equator regression raid: opening unchanged, no SpawnGates activity, no warning.
  RainBounds grew to size z 246 (the rain play space is the bake box + 30 m).
- Both installs carry the build (`D:\SPT41`, `D:\SPT41vanilla`; the vanilla server DLL changed with Shared, its
  restart is the user's). Host dev cfg back to `SpawnGateHold=false`, `SpawnGateHoldSeconds=30`.
- Open: ArenaMode's `arena_airpit` formation and its GPU trial (bots in the north box, departures through eight
  single-file lanes); whether a player can vault the 1.35 m turnstiles (untested); the AirPit DynamicMaps pack is
  derived from the NavMesh bundle and wants a re-export (the pocket is meshed now); the crate under the tarp at
  (-31.3, -16.7) (level38 `SOO` crates, user's choice pending); a new MapPort version before ArenaMode relies on this
  (0.2.1 carries neither SpawnGates nor the U7 bundle).

## Update 2026-10-17 (evening) - AirPit team boxes: gap analysis for ArenaMode's original team boxes (offline)

User question: Bay5's original team boxes are now implemented in ArenaMode (its `cfa3f55`); what does AirPit still lack?
Answer in [airpit-team-boxes.md](docs/progress/airpit-team-boxes.md): the south bus pocket is data-ready (ten markers on the
mesh, BUS_DOORS seal it in the offline carve model); the north box is blocked on this side twice: no NavMesh beyond z 70
(`bake_bounds`, the 2026-10-12 north-limit decision) and no gate (`PASSPORT_CONTROL_DOORS` is not an `opened_barriers`
entry, so SpawnGates cannot hold or release it); and `SpawnGates.Hold` re-closes every opened barrier on AirPit (37 parts,
35 carvers incl. the shop shells), which wants a per-map start-gate subset. The formation numbers (bounds, 20 points,
infiltration, donor/destination candidates) are in the log. Nothing observed in-game yet: the GPU host was in use by
another session, so the AirPit hold run is still open. Decisions (north start, mesh in all modes, hold subset, bounds,
sequencing with a new MapPort version) are the user's.

## Update 2026-10-17 (afternoon) - Equator's shader sidecar renamed to material-shaders.arena_equator.json; feature/airpit merged into main and deleted

- Branches: `feature/airpit` (SpawnGates `93afc19` plus the user's MIT `LICENSE` commit `ab6d4bb`) was fast-forwarded into `main`,
  `main` pushed, the branch deleted locally and on origin. Everything continues on `main`. `H.npy` in the repo root was a scratch
  height raster this session's spawn-gate analysis script wrote into its working directory; deleted.
- The 0.2.1 package shipped Equator's shader sidecar under the pre-generalization name `material-shaders.json` next to
  `material-shaders.arena_bay5.json` / `.arena_airpit.json`. The mod is not on Forge yet, so the exception is gone:
  `ShaderRebind.SidecarFileName` is `material-shaders.<key>.json` for every map, the csproj stages Equator's
  `work/build/full/material-shaders.json` as `material-shaders.arena_equator.json`, and `make_release_zips.py`,
  `validate_distribution.py`, the tests, `docs/dist-manifest.md` and THIRD-PARTY-NOTICES follow. The per-map BUILD output keeps
  its name (MaterialImporter writes it; the csproj renames while staging). No other Equator file in the package lacks the key
  (bundles, db, acoustics, flares, ai, loadscreens, banner, dynamicmaps all carry it).
- Ordinary deployment never deletes, so `tools/deploy-to-spt.ps1 -RemoveLegacyShaderSidecar` (modeled on `-RemoveLegacyEquatorDb`,
  test `tools/tests/test_shader_sidecar_deploy.ps1`) backs up and removes only the old file after the renamed file and the client
  DLL match dist. Run on `D:\SPT41` and `D:\SPT41vanilla` (backups under ignored `work/deployment-backups/`).
- Host evidence (GPU-2, 14:43-14:46, client built from the working tree on `ab6d4bb`): startup banner
  `found: ...material-shaders.arena_equator.json`; Equator PMC raid without bots:
  `[RebindShaders] material-shaders.arena_equator.json: 392 material entries`, 372 stand-in materials rebound
  (328 + 11 + 20 + 3 + 10 per scene), no ArenaMapPort warning, 120 fps (max 8.5 ms), hideout opens.
- Tests: 861 .NET, 1721 Python (46 skipped), all six deploy-script tests, `DISTRIBUTION_OK 3 maps 9 bundles 3 sidecars`.
- Open: the published 0.2.1 package (GitHub release and Pixeldrain, built from `d26b7fd`) still carries the old name; the Forge
  upload needs a re-cut package (re-cut 0.2.1 or bump to 0.2.2 - SpawnGates is on `main` too, user's call). The vanilla server
  DLL on disk is a same-source rebuild (the assembly carries the commit hash, so it differs per commit); restarting that server
  is the user's job and changes nothing functionally.

## Update 2026-10-17 - spawn gates: enclosures held closed for a mode plugin, released on call (SpawnGates)

User question answered and implemented: the team spawn enclosures (Equator's booth doors, Bay5's gates, AirPit's bus
doors) can stay closed through a preparation phase and open later. `ArenaMapPort.Client/SpawnGates.cs` (public API
`Hold(reason)` / `Release(reason)` / `IsHeld` / `ClosedParts`, reflection contract in its class doc) registers every part
`BoothDoorRepairs` would switch off, keeps it closed while held, seals it for bots with carving NavMeshObstacle boxes
(EFT bots never see colliders), slides Equator's leaves 1.38 m into their frame pockets on release and switches the parts
off as the raid-start opening would. Developer keys `Spawns/SpawnGateHold` + `SpawnGateHoldSeconds` (test hold without a
caller), `SpawnGateCarve`, `SpawnGateSlideSeconds`, `SpawnGateMaxHoldSeconds` (safety release, Warning). Pure rules in
`SpawnGateModel.cs` with tests. GPU-2 evidence (Equator, PMC raid, bots as online): 15 parts held, 11 carvers, probe
sealed (9 judged, door (3) disconnected on the map itself), auto release after 60 s, probe open 0.5 s later, no warning,
120 fps; details and the ArenaMode to-do list in [spawn-gates.md](docs/progress/spawn-gates.md). What the plugin side
could not observe: SAIN bots at a sealed doorway (plain-raid bots kept 50 m away whether open or closed); that belongs to
the first Team DM with the gates held, which needs ArenaMode to call the API and to tolerate a Partial path out of a box
during the hold. Review workflow: 11 confirmed findings, all fixed before the final host run (registration of every part,
closed pose restored on a re-hold, an opening in progress cancelled by Hold, safety limit counted from the hold, probe
bounds cached, baseline probe). Not released; the next version decides.

## Update 2026-10-16 (evening) - RELEASE 0.2.1 published (first Forge version), 0.2.0 withdrawn

User decision: 0.2.0 is deleted completely (GitHub release and tag `v0.2.0` removed, Pixeldrain files a9p4qcnD/Nj6acBoa and
list Runugi6n deleted and verified gone; `docs/releases/v0.2.0.md` kept, marked withdrawn), 0.2.1 is the first version
published on the Forge. 0.2.1 = the 0.2.0 bundle set plus `ScopeMaskRepairs`; only the two DLLs are rebuilt. Source commit
`d26b7fd` (version bump in both csproj, `Plugin.cs`, `ModMetadata.cs`; `docs/releases/v0.2.1.md` self-contained with the
changes since 0.1.0). Host acceptance GPU-2 with the release DLLs (19 plugins identical, server log
`Mod: ArenaMapPort Version: 0.2.1`, client `[ArenaMapPort] 0.2.1 loaded`): AirPit PMC raid with bots as-online (7 PMCs
spawned and fought, SAIN), ELCAN 4x on an MP7A1 sharp, 119 fps, max frame 14.7 ms, no plugin warning, ScopeMaskRenderer
removed after the raid, hideout opens. Package `make_release_zips.py --single --extract-check`:
`work/releases/0.2.1/maschine-ArenaMapPort-0.2.1.zip`, 2,149,176,941 B (2.00 GiB), SHA-256
`c45ec0fdf0bc2be3509f5d1f40337185aacbefc356ea4284e240332953ab5369`, 67 files identical after extraction. Pixeldrain
(5.8 MiB/s, server-side size and hash confirmed): list https://pixeldrain.com/l/BT2M3X6y, ZIP https://pixeldrain.com/u/yzCy9P3o
(direct https://pixeldrain.com/api/file/yzCy9P3o?download), checksum https://pixeldrain.com/u/2ZNw7sxm; record
`work/releases/0.2.1/pixeldrain.json`. VirusTotal (record `work/releases/0.2.1/virustotal.json`): client
`71d95b16...`, server `22b04fa1...`, both 0 malicious / 0 suspicious of 75 engines. Extra GitHub asset: the client DLL
alone as `work/releases/0.2.1-patch/maschine-ArenaMapPort-0.2.1-client-dll-only.zip` (289,130 B, SHA-256 `8da34e1b...`)
for 0.2.0 installations. `D:\SPT41` and `D:\SPT41vanilla` carry dist 0.2.1 (the vanilla server restart is the user's job).
Still the user's: the Forge entry (text from FORGE.md, link to the Pixeldrain list, VirusTotal links).

## Update 2026-10-16 (afternoon) - blurred optics on every ported map fixed (ScopeMaskRepairs), release 0.2.0 published

**Release 0.2.0 is public:** `main` fast-forwarded to `4ddd321`, tag `v0.2.0`, GitHub release
https://github.com/maschine34675/ArenaMapPort/releases/tag/v0.2.0 (notes = `docs/releases/v0.2.0.md`, asset = the
`.sha256`; the ZIP stays on the Pixeldrain list https://pixeldrain.com/l/Runugi6n). The block below is the pre-publication state.

**Bug after the release (user report): the image inside magnifying optics is blurred on our maps.** Root cause: every
vanilla Scripts scene carries a `TechnicalManagers/ScopeMaskRenderer` object (EFT's `ScopeMaskRenderer` writes the global
`_ScopeMask` texture that the aiming depth-of-field uses to leave the sight picture alone); Arena's scenes have no
TechnicalManagers root, so `_ScopeMask` was never written and the aiming blur covered the lens too. Proven on GPU-2 with the
same ELCAN SpecterDR at 4x: AirPit soft (Laplacian variance of the scope interior 2.3), Factory sharp (79.5). Fix:
`ArenaMapPort.Client/ScopeMaskRepairs.cs` creates "ArenaMapPort ScopeMaskRenderer" (EFT's own component) on the owned
Scripts scene's load and destroys it on its unload; key `Camera/EnableScopeMaskFix` (default true). Host-verified the same
afternoon (same spot after the fix: 709.0, sharp rivets and reticle ladder, 120 fps, no warnings, object removed after the
raid, hideout opens). Details and stills: [scope-mask.md](docs/progress/scope-mask.md). NOT in the 0.2.0 package: a
0.2.1 (client DLL only; the bundles are unchanged) is the user's call. New AGENTS.md trap: the vanilla TechnicalManagers
list (ScopeMaskRenderer, WindowBreakerManager, WaterRenderer, lightManager, OnRenderObjectManager, ImpostorsRenderer,
GrassTrampler) is the first place to look when an aiming/screen effect works on vanilla but not on a ported map.

## Update 2026-10-16 - RELEASE 0.2.0 built, host-verified and uploaded to Pixeldrain (feature/airpit, not yet merged or tagged)

Source commit of the package: `4009006` (version bump, three-map texts, `docs/releases/v0.2.0.md`, `RoomShotAcoustics`
ON by default on every map = user decision after listening). Host acceptance on GPU-2 with the 0.2.0 DLLs (client and server
hashes identical on the host): AirPit raid with bots as-online (bots spawn and fight, 118 fps, max frame 47 ms at a spawn,
no plugin warnings, `[ArenaMapPort] 0.2.0 loaded. Maps: Arena: Equator, Arena: Bay 5, Arena: Air Pit`), hideout opens
in 3.6 s. `validate_distribution.py`: `DISTRIBUTION_OK 3 maps 9 bundles 3 sidecars`. Package:
`make_release_zips.py --single --extract-check` -> `work/releases/0.2.0/maschine-ArenaMapPort-0.2.0.zip`,
2,149,176,241 B (2.00 GiB), SHA-256 `6c449bdbc629c87c34f0eee7b395c1acb6f33642ad7cf848ea8c3045ab420519`, 67 dist files
identical after extraction. Upload (`upload_pixeldrain.py`, 6.1 MiB/s, server-side size and hash confirmed):
list https://pixeldrain.com/l/Runugi6n, ZIP https://pixeldrain.com/u/a9p4qcnD (direct
https://pixeldrain.com/api/file/a9p4qcnD?download), checksum https://pixeldrain.com/u/Nj6acBoa; record in
`work/releases/0.2.0/pixeldrain.json`. VirusTotal (`tools/release/scan_virustotal.py`, key `VIRUSTOTAL_API_KEY`, record `work/releases/0.2.0/virustotal.json`):
both DLLs uploaded and clean, 0 malicious / 0 suspicious of 75 engines (client `70fc84f7...`, server `aacc6e49...`).
`D:\SPT41vanilla` received the same dist (67 files; its server restart is the user's job). Still the user's call:
merge `feature/airpit` into `main`, tag `v0.2.0` (on `4009006` or the docs commit after it) and push; the Forge entry
(link to the Pixeldrain list, VirusTotal links, text from FORGE.md). The earlier block below says the switch defaulted
OFF: superseded.

## Update 2026-10-15 - per-room shot acoustics (invented data, Meta XR shoebox), release = one ZIP on Pixeldrain

**Shot acoustics (user's next step after AirPit; option A):** `RoomShotAcoustics` (switch `Audio (<map>)/RoomShotAcoustics`,
default OFF on every map) drives the Meta XR Audio plugin's one global shoebox room from the listener's acoustic room
(class Hall / Shop / Metal / Outdoor by room type and size, dims from the room bounds) and switches the plugin's
process-global model to ShoeboxRoom; outdoors, on switch-off and at raid end the shipped model (AcousticRayTracing) and
the plugin's default room come back. Every material and clutter value is INVENTED (EFT and Arena carry none) and lives
in the developer section `ShotAcousticsLab` (per class walls/ceiling/floor/clutter + `HallMaxDimension`), live. Two host
rounds on GPU-2 (`ReverbPulse` probe with the three new gunshot buses, `ReverbPulseVolume` 0.8, rain/ambience muted for
the measurement): mechanism proven on every route, first table rang the shop louder than the hall, the measured defaults
(hall Concrete walls+ceiling, CeramicTile floor, clutter 0.4, cap 50 m; shop GypsumBoard walls, AcousticTile ceiling,
CeramicTile floor, clutter 0.7) give hall late tail 7.4e-4 vs shop 9.8e-5. Details, tables and log quotes:
[shot-acoustics.md](docs/progress/shot-acoustics.md). NOTE: the shared developer config on the Syncthing mirror now holds
`Audio (arena_airpit)/RoomShotAcoustics = true` (set for the measurement, left on for the user's listening test) and the
measured lab values. Open user inputs: is the passport hall (Start1, classified Hall by the 40 m rule) wanted as a hall;
metallic character for planes/bus (Steel/Steel/Carpet, measured but untuned); Equator/Bay5 (switch exists, nothing
measured); ship ON or OFF in player mode. Verified 2026-10-15/16: hideout opens with the build (7 s); Equator and Bay5 measured in one process after AirPit (atrium vs shop 3x, Bay5 warehouse the most reverberant room so far, Bay5 outdoors untouched) - the shared developer config now has the switch ON on all three maps for the user's listening test; user decision 2026-10-16: keep the shoebox solution (no clip-set remap), enable it on all three maps.
**Release form (user decision):** ONE ZIP (about 2.03 GiB) on Pixeldrain instead of GitHub release parts
(`make_release_zips.py --single`, `tools/release/upload_pixeldrain.py`, commit `340d6e9`; API key in the VM's Machine
environment `PIXELDRAIN_API_KEY`, resolved via the registry fallback, never printed). VirusTotal links only for the two
DLLs (the tool prints them from the dist hashes). Test upload verified (53-byte file, server hash matched).

## Update 2026-10-15 - Arena-only selection artwork

The region artwork now requires ArenaMode's exact current menu selection via its optional
`ArenaMenuSelection.IsSelectedFor(RaidSettings)` contract. Ordinary PMC/Scav and installations
without ArenaMode retain the original EFT image. Restoration on close/re-entry remains unconditional;
map positions, availability and assets are unchanged. This supersedes the shared PMC/Arena artwork
scope of the 2026-10-13 implementation below. See [selection-map.md](docs/progress/selection-map.md)
for implementation and verification.
Client Release build and native GPU-2 menu checks passed: fresh PMC, Arena, Arena -> PMC/Scav,
Confirmation -> Back, MainMenu -> new PMC flow, and hideout opening. Client-only deployment preserved
the installed server/bundles/configuration; host left in MainMenu, no raid started.

## Update 2026-10-14 - verification pass items 1-6 (GPU-2)

Done and logged in [airpit-release.md](docs/progress/airpit-release.md) "Verification pass 2026-10-14": ballistics
shooting check (five SR-25 shots, metal/wood/concrete/plastic/fabric/glass incl. the partial-scene roll door and shop
racks, no `defaultFallback=True`; footsteps Concrete), extraction on foot through both gates (server `outcome: RUNNER`
= run-through, the gates work), `uibridge_teleport dryRun` on all 18 spawns + 2 exits (all pass), eye/F7 pass with
four stills (SE-shop lights, passport-hall spots, concourse floor damage, apron) - the look is the user's call,
item 6 EN texts/banner on screen. The JsonUtility `lamp_fill` proof is done (evening, after the user renewed the
Hub sign-in: scratch `build_all` into `work/build/airpit-lampfill-check`, descriptor parsed, `bake_bounds` / layer
mask / 10 `opened_barriers` consumed, triangulation identical to U6, all validators OK, not deployed; progress log
item 3). Nothing from the island scan: the offline Dijkstra pass found no island on any of the 75 high colliders and
all four shops connected (progress log, item 4). Still open from the pass: DE/RU description texts (user's client). Test profile SPT-GPU-2 had lost its weapons: `gpu-run leave` is a disconnect
(`outcome: LEFT` = MIA, PMC gear lost) - extract through a gate when the gear matters; the SR-25 equipped from the
stash was lost again in the evening when the user's own raid expired (15-min timer) during the barricade inspection.
User question, evening: the two boarded openings with gangway ramps (Hellfinger shop back wall, Burger Spot mirror)
cannot open - static props, no door component anywhere in the package, sealed by chain-link on the hall side
(progress log, "Barricades with gangway ramps").

## Update 2026-10-14 - bloom accepted for AirPit (1.2 / 0.25), next step after AirPit: per-room shot acoustics

`BloomTuning` binds AirPit's accepted `BloomThreshold 1.2` / `BloomIntensity 0.25` (user decision, item 9) with its own
once-only `BloomDefaultsRevision`; Equator/Bay5 unchanged. Progress log: "Bloom accepted".
**Planned next step once AirPit is otherwise done (user, 2026-10-14):** per-room gunshot acoustics with invented data -
Meta XR shoebox room from the acoustic room boxes/types (option A, M) with a shot-tail clip-set remap by room as the
cheap fallback (option B); probe first (Meta XR acoustic model state, Gunshots-bus pulse hall vs shop). Open user
inputs: is "hall" only Main or also the passport hall; metallic character for planes/bus; Equator/Bay5 too; ship ON or
OFF in player mode. Full analysis in the session notes (workflow wf_fbef2f20-d8d) - to be written up when the work
starts.

## Update 2026-10-14 - ambience mix accepted: tuned gains are the defaults (AmbientDefaultsRevision 3)

The user's listening pass settled the map ambience mix: Equator 2 / 0.4 / 2.5, Bay5 5 / 0.4 / 4, AirPit 3 / 0.4 / 3
(gain dB / volume offset / range multiplier) are now `AmbientAudioDefaults`; the AirPit mix is no longer provisional.
Revision 3 of the migration moves a saved mix that still equals the old default to the new gain and preserves every
custom mix. Details: [airpit-release.md](docs/progress/airpit-release.md), "Ambience mix accepted". Hall-to-shop
question answered and measured (progress log "Hall to shop"): by Arena's data hall and shops share tone, reverb category
and rain bed, the listener room does switch on a walk (Main -> AudioRoom_Burger_Spot_3, identical reverb numbers), the
only native differences are the muffling of other rooms' sounds (live) and EFT's outdoor-ambience distance fade (not
ported; 0 -> 0 for the north shops anyway). Open user decision: C (leave) / B (building <-> apron bus crossfade) /
A-lite (south-gate distance fade). Remaining listening items: room tone audible indoors (F10 shows
`playing=True indoorBusDb=0.0`), muffling of a hall source heard from a shop, Equator's atrium rain bed (by design).

## Update 2026-10-14 - 1 Hz hitch on every ported map (PrecipitationAudio), fixed on feature/airpit for 0.2.0

User report with two recordings: the picture freezes briefly every second on the ported maps. Frame-diffing the clips
shows one duplicated frame at exactly every 60th frame; ModProfiler on the user's PC and on GPU-2 attributes a 30 ms
call once per second to `PrecipitationAudio.Driver.Update`: its foreign-owner check ran
`Object.FindObjectsOfType<PrecipitationAmbientBlender>()` every second, ~30 ms on the ported scenes (AirPit 55k
objects). Now scanned once per raid (and after an audio reconfiguration); the per-second check keeps only the cheap
`AmbientAudioSystem` singleton flag. Host GPU-2: `max` frame time 35 ms -> 8.8-12 ms, ModProfiler `Driver.Update` 31 ms -> 0.18 ms max, no 1 Hz spike
in 90 s of capture. Details and the measurement recipe:
[equator-rain-accents.md](docs/progress/equator-rain-accents.md), "1 Hz hitch". (The 410 `StaticDeferredDecal.OnDisable`
NullReferenceExceptions at the end of the user's log came from closing the game with Alt+F4 mid-raid, not from a
cleanup bug - user clarification 2026-10-14.)

## Update 2026-10-14 - hideout loading regression (TimeOfDayRepairs), fixed on feature/airpit for 0.2.0

User report: with ArenaMapPort installed the hideout never finishes loading. Root cause (host GPU-2, EFT's own
`Logs/<session>/errors.log`): `TimeOfDayRepairs.Prefix` on `BaseLocalGame<EftGamePlayerOwner>.InitWeather` also runs
for `HideoutGame : BaseLocalGame<HideoutPlayerOwner>` (Mono shares the generic body) and, since `04be98c` (2026-10-07,
Equator day/night profiles; contained in v0.1.0), read `_selectedDateTime` through a FieldInfo of the closed
EftGamePlayerOwner type BEFORE its `arena_` gate: `ArgumentException` out of InitWeather, `HideoutGame.smethod_6`
faults, the loading screen never closes, nothing in BepInEx's log. Fix: `Prefix(object __instance, ...)` type-tests
`is BaseLocalGame<EftGamePlayerOwner>` first (foreign owner types are left inert), the body moved to `Apply`, try/catch
that restores the original settings so a repair failure degrades to vanilla; four tests in `MapWeatherRuntimeTests`
(hideout double through the shared detour, Fika-shaped subclass, seeded vanilla reset, failure injection). Two side findings of the same review hardened on request: `HdrImageTuning.Release()` walks its
scope chain bounded instead of spinning on a finished scope, and the two spatial-audio occlusion prefixes are
map-gated (vanilla/menu/hideout run the native bodies). User decision: no 0.1.1 hotfix (0.1.0 is not on Forge yet), the fix ships with 0.2.0 - the 0.2.0
release notes must name it. Host verification GPU-2 2026-10-14: hideout opens in 7.5 s / 8.1 s (before: never), no EFT errors.log entry, Equator day
and AirPit server-time raids log the same Profiles/TimeOfDay lines as before. Details:
[equator-profiles.md](docs/progress/equator-profiles.md), "Regression: hideout loading".

## Update 2026-10-13 - AirPit DynamicMaps

AirPit now ships an optional ground-floor overview from its own plugin directory.
The upper spectator area is intentionally excluded (user clarification).
Built, deployed through the sanctioned script and natively accepted on GPU-1:
four positions/headings, both exit markers, one layer, 38,141 sprite vertices,
no map-renderer warnings/errors. North Gate exit and return to MainMenu passed.
Sources, package paths and test evidence: [airpit-dynamicmaps.md](docs/progress/airpit-dynamicmaps.md).
No scene bundles changed; existing Equator/Bay5 overview bytes are preserved.

## Update 2026-10-13 - original Arena selection map

Follow-up: user-supplied selection screenshots now establish displayed original pin positions. Applied
13 measured positions to the catalog, map sidecars and server JSON; Labs Replica is recorded separately
for future work, AirPit 2.0 remains unmeasured. Source record: `tools/maps/selection/markers.json`.
No parked maps are enabled. See the new dated section in [selection-map.md](docs/progress/selection-map.md)
for the coordinate table and acceptance evidence; it supersedes the unresolved-position status below.
Follow-up acceptance: all 738 .NET tests and distribution validation pass; deployed to D:\SPT41, five
changed files verified on GPU-1 before server/client restart. PMC Equator/AirPit/Bay5 and ArenaMode
Equator/Bay5 selection verified at their new anchors, previous PMC label overlap gone. No ArenaMapPort
warnings; host left in MainMenu. Native screenshots: `work/map-marker-audit/{pmc,arena}.png`.

Added Arena's original region-selection artwork on `feature/airpit`, independently of scene bundles.
`SelectionMapArtwork` swaps only the native map Image sprite in PMC/ArenaMode and restores it on close.
New required ignored build input: `python tools/extract/extract_selection_map.py` produces
`work/extract/map-selection/{region-map.png,manifest.json}`; Release/DllOnly builds stage it in the
client's own `map-selection` folder. The multipart release packager includes both exact files in Core.
Accepted artwork framing/content is pinned; no BSG-derived bytes are tracked. Original Arena marker
positions remain unproven and existing anchors are unchanged. Source/provenance and limitations:
[selection-map.md](docs/progress/selection-map.md).

Build/distribution passed and sanctioned deployment to D:\SPT41 completed. GPU-1 was explicitly released
by the user: loaded client matches staged candidate, PMC (Equator/Bay5/AirPit) and ArenaMode (Equator/Bay5)
selection, re-entry and sprite restoration verified; no ArenaMapPort warnings. Host left in MainMenu.
GPU-2 was not controlled/restarted. No server or map asset content changed, no release ZIP/upload,
no vanilla deployment. The AirPit work and remaining acceptance items below retain their status.

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
south); (c) "hall-centre cover" = the plywood/fence/container cordon under the airliner (photos 27/28). User decisions (2026-10-12 late): the hall-centre cover STAYS (no rebake), the glass looks good after the queue fix, the rain is fine. New question from the user: all four shops should be walkable in Arena - where did the interiors go? Answer (progress doc "Where the four shop interiors are"): every Arena mode loads the same hall `Arena_AirPit_ShootOut` (324) whose shops BSG stripped (interiors, doors, physics props, lights) and sealed with static `*_closed` meshes before 0.5.4; the furnished shops exist only in the unreferenced legacy scene `Arena_Airpit_Main_hall` (38) + Light (37); the AI/NavMesh/design data (2025 bakes, menu art) still describe the furnished hall. Walkable shops = a new slice import from level38/37 into the generic pipeline + shell swap + rebake (L). **User decision (2026-10-12 late): DO IT - import the furnished shops from level38 (+ lights from 37); also check whether other decoration/decals were lost elsewhere.** Phase 0 done (progress doc "Phase 0 results"): partial-scene design, lost-decoration census, bake consequences; phase 1 DONE (2026-10-12 23:45, workflow `wf_fe3c0bde-c1f`; progress doc "Phase 1 - partial scenes"): generic partial-scene import (`tools/extract/scene_select.py`, plan `select` records, manifest/contract `partial_scenes`, `tools/validate/partial_scope.py`), package `airpit-native-v7` = v6 + 7118 Main_hall objects (1446 units, 85 carriers, 544 twins skipped, 4 hidden video-player BallisticCollider orphans) + 962 Light objects (187 units, 156 lights); the five v6 scenes/meshes/materials/textures byte-identical; decals 683, ballistics 4455, culling lights 680, volumetrics 139; descriptor `AllSceneNames` +2 and `OpenedBarriers` +6 (the `*_closed` shells) mirrored in C#, python 1274 / dotnet 727 tests green, validate_extract OK with reproducibility. Not yet built or host-tested. Phase 1b census done (2026-10-13 00:40, progress doc "Phase 1b - census"): the decoration box is effectively the whole level38; 245 prop instances (632 units) of the old hall-centre cover would duplicate 324's prefab platforms and nudged props -> new select key `exclude` (208 entries); the root-only twin rule loses 20 child lights in level37 -> new select key `twin_descendants: import_missing`; ring lamps are L12 (no NavMesh islands); spawn check 0 hits. Phase 1b DONE (2026-10-13 04:00, workflow `wf_409814f7-455` + lead; progress doc "Phase 1b - package v8"): select keys `exclude` (208 level38 entries, 632 units; 9 level37 entries for parts level325 replaced on purpose) and `twin_descendants: import_missing` (22 level37 + 4 level38 imports, bare twin carriers), counting contract `sum(selected)+sum(descendants_added)-sum(excluded) == units`; accepted package `airpit-native-v8` (55,263 objects, Main_hall 13,006 / Light 1,538; ballistics 5282, decals 722, culling lights 722, volumetrics 154; five full scenes byte-identical to v6/v7, v7 a subset); validate_extract OK (1800 s timeout default now), decal refresh identical, spawn check 0 hits, bake pins 7922/7775/3036; python 1377 tests green; descriptor/sidecar unchanged. U6 BUILT, DEPLOYED AND HOST-VERIFIED (2026-10-13 03:05-04:00, progress doc "U6 host raids 15-18"): bake 3048 sources / 11332 vertices / 5212 triangles, scenes bundle 992 MB, validate_port + validate_distribution OK, `navmesh_diff --compare-bundle` 187,767 of 204,204 Arena samples (U5 187,410); the four bot spawns are back on Arena's anchors (`89a072a`, built-mesh clearance 1.50 / 2.18 / 1.89 / 1.80 m, test_airpit_spawns pins the back-room boxes). Host raid 15 found the south-west shop still walled: three layer-0 blockout panels `SBG_Arena_AirPit_ShootOut/SOO/Cube (10) Group` on the roll-door lines (bake mask ignores layer 0, player collides) -> tenth `opened_barriers` entry (`62d7739`, no rebake). Raids 16-17: every shop probe, core 16/17/18, hubs and the reverted spawns reachable, back rooms islands like Arena's mesh, walks through the SW shop and west-south -> east-south (86.6 m) arrive, 1050 lights / 722 culling lights / 705 decals active, F7 481 culling lights, 116 fps, bots 14/14 markers on the mesh and active (no ActiveFail); raids 18-19 Equator/Bay5 unchanged. Screenshots in `work/screenshots/airpit-2026-10-13/`. User feedback 2026-10-13 morning: night look, shop sound and the south-west roll doors (three north modules: closed / open / half open behind sandbags) accepted. Open question from the user: the passport-hall ceiling "should be seamlessly full of lamps" - the census (progress doc "Passport-hall ceiling lamps") shows Arena 0.6.0 has exactly 45 ring lamps there (43 lit, 2 authored dark via `Airpit_emissive_01_off`), nothing deactivated, no inactive copies anywhere; the dense ceiling is an older build (0.12.12.15 beta, visible only in the baked passport cubemaps). User decision: fill it, every added lamp dark -> `LampFillRepairs` (`dc8ea36`): `ArenaLampFillDescriptor` / sidecar `lamp_fill`, 82 cells (all flat-ceiling cells of the 17 x 9 lattice without an authored lamp; the review had dropped (-30.37, 73.13) because its disc rim dips 0.13 m into the western round column, the user put it back at midday: the lattice runs just as close past the eastern column and looks fine), raid-time copies of `Airport_lamp_02_disabled` under key `Map (arena_airpit)/PassportHallLampFill` (default true); host raid 20: `81 copied`, no warnings, 117 fps, screenshot 07; host raid 21 with the 82nd cell (14:32): `82 copied, 0 skipped`, no warnings, 116 fps, screenshot 08. Open: the next headless build_all must show JsonUtility skips the nested `lamp_fill` sidecar key; the copies keep their shadow proxies (no frame-time cost measured). Still open: the 13 recovered SE-shop lights and the 12 soft-shadow passport-hall spots by eye, item 25 text sweep, release 0.2.0 ZIPs. User extended the decision (21:15): afterwards import everything else that can be taken (passport-hall clutter and ring lamps, stalls, apron litter, floor damage overlays, all decals) = phase 1b -> v8; then descriptor/sidecars (`AllSceneNames` +2, `opened_barriers` +6), U6 build, deploy, host dry runs, spawn revert to Arena's anchors.
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
