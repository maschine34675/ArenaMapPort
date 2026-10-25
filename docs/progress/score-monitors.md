# Score monitors (2026-10-25)

Arena hangs TV props in its design scenes (`Arena_Home_theater`) and fills them, while a mode runs, with the mode's timer, the team score or the alive
count. The ported maps carry the TV props but no screen content: every one of them is a dark grey quad. This item gives the TVs of all four released maps
their Arena faces again, fed by ArenaMode (Team Deathmatch score and clock) through a small public contract.

Status 2026-10-25: **both steps are implemented and unit-tested on the build machine; nothing of it has run on a host.** Step 1, the data side, holds the
holder extractor and its tracked sidecars, the registry mirror with its validator and tests, the artwork extractor and its staged package (csproj target,
release packager rule, distribution validator) and the Unity-free layout of the three faces. Step 2, the client ("The client (step 2)" at the end), adds the public
contract `ScoreMonitors`, the hub, the Unity view, the developer demo and keys, the ArenaMode binder with the Team Deathmatch mapping, and the host checks.

## What Arena does, and what the port lacks

Under each TV prop sits a GameObject `ArenaMonitorHolder`: a RectTransform with one MonoBehaviour, `Game.Modes.Data.Monitors.ArenaMonitorHolder`, 40 bytes
(the 28-byte MonoBehaviour head, an empty name, then two int32 words: a scene-local holder id and the TYPE WORD). Arena's mode scripts instantiate the
monitor template that the type word names (`MonitorTemplatesHolder` of sharedassets1.assets: 13 template fields) as a world-space canvas on the holder and
set its texts. The importer skips RectTransform objects, so neither the holders nor the templates are in the extracted packages; the TV props are.

All 47 holders of the four maps (43 in the scenes the maps import; Equator's Tournament scene 61 holds the other four) share ONE pose below their prop
(`ArenaScoreMonitorMount`): local position (0, 0, 0.369285), rotation (-0.5, 0.5, 0.5, -0.5), uniform scale 0.00054, rect 1930 x 1163 canvas pixels,
pivot (0.5, 0.5). The canvas is readable from the prop's +X side (its forward, the holder's +Z, is the prop's -X). A TV of prop scale s shows a canvas of
s x 1.042 m x s x 0.628 m (3.127 x 1.884 m at scale 3; 20.5 x 12.4 m on Bowl's stadium boards at 19.68).

### Type words

| word | template | evidence | face of the port |
|---|---|---|---|
| 0 | MonitorTimer | confirmed: Bowl's TeamFight props are called TIMER | timer |
| 1 | MonitorTeamsCommonRoundScore | confirmed: SCORE | score |
| 2 | MonitorAlivePlayers | confirmed: ALIVE | alive |
| 6 | MonitorTeamsFinalRoundScore | inferred from the template field order (Equator's TeamFight boards); its template is identical to word 1 | score |
| 7 | MonitorPlayerboard | inferred (Equator's two small TeamFight boards) | none (dark) |
| 10 | no name | Bowl's two inactive base-set props; most likely a respawn-side panel | none (dark) |

The 2026-10-25 research correlated the type words with the prop names of 49 Arena levels (about 600 holders): 0, 1, 2 hold everywhere; the words above 7
do NOT follow the template field order (11 = props called CAPTURE POINT, 12 = POINT STATUS, 13 and 14 = ALL POINT STATUS), the enum has gaps, so the
sidecar stores the raw word, a template name only where it is known, and the extractor stops on any word it has not been told about. Arena's own TDM
scenes (Bowl_TDM 219, Bay5_TDM 221, Prison, Yard) show what its "TDM" is: a timer plus capture-point boards, no kill score. There is no Arena prefab for a
kill-based team score; the score face (words 1 and 6: 11 active screens on the four maps, among them the two in Equator's booths) is the carrier for ArenaMode's TDM.

## Data side

### Holder extractor and tracked sidecars

`python tools/extract/extract_score_monitors.py [--map arena_bowl ...] [--check] [--csharp] [--no-package]` reads the raw level files of the scenes that
tools/bundle/key-sources.json lists for a map AND the map imports (Equator 51, 53, 55, 57, 58; Bay5 79 to 89; AirPit 28, 29, 37, 38, 40, 324; Bowl 41 to
48 and 95; about 20 s for all four) and writes `tools/maps/<stem>.monitors.json` (schema 1, one row per line, byte-stable; `--check` compares and writes
nothing; `--csharp` prints the registry literals). It fails closed (exit 2, nothing written) when a holder is not 40 bytes, is not called
ArenaMonitorHolder, is inactive, has no parent prop or no RectTransform, deviates from the shared mount, sits below a prop with a non-uniform scale, carries
an unknown type word, or repeats another row's (scene, path, position). With the accepted package present (`work/extract/equator-native-v5`, `bay5-native-v5`,
`airpit-native-v9`, `bowl-native-v2`) it also finds every prop exactly once in the package scene, at the row's place and with the row's activity.

Row: `scene`, `path` (the PROP's hierarchy path), `at` (the holder's world position, the canvas centre; tells apart a path that repeats, as Bowl's two
`... SCORE` props and Equator's two DesignStuff scenes do), `arena_type`, `template`, `confirmed`, `face`, `active_in_scene`, `prop_scale`, `world_size_m`,
`viewer_yaw`. **`viewer_yaw` is the compass yaw (0 = +Z, 90 = +X) of the direction from the screen towards the reader**, the side the canvas is
readable from; the canvas forward points the other way (the first research table listed that one and would have sent a tester to the back of every TV).
Only the active rows get a face (decision of the lead): AirPit's two team-box screens `(9)` and `(11)` are inactive in the scene data, and so are Bowl's ten
base-set props (the TeamFight set stands at the same spots); they stay dark.

| map | holders | active | faces of the active rows |
|---|---|---|---|
| Equator | 11 | 11 | 2 timer, 5 score (3 x word 1, 2 x word 6), 2 alive, 2 dark (word 7) |
| Bay5 | 8 | 8 | 3 timer, 2 score, 3 alive |
| AirPit | 8 | 6 | 2 timer, 2 score, 2 alive |
| Bowl | 16 | 6 | 2 timer, 2 score, 2 alive |

### Registry mirror, validator, tests

`ArenaMapRegistry.<Map>.ScoreMonitors` (named argument after each map's `DoorPoses`) mirrors `scene`, `path`, `at`, `arena_type` and `active_in_scene` of the
sidecar number for number; the types are `ArenaScoreMonitor` (with `AppliesTo(x, y, z)`, the 0.05 m match of a loaded prop's mount point) and
`ArenaScoreMonitorMount` (the shared pose). `validate_map_descriptors.py` parses the literal (`parse_score_monitor_literal`, the only bool it reads is its
last argument), checks the sidecar (`score_monitor_errors`: schema, key, source block, mount, exact row keys, scenes of the map, type word with its template /
confirmed / face, finite numbers, canvas size against `prop_scale`, uniqueness) against the registry in order, refuses a registry row without a sidecar, a
sidecar without its map descriptor and a released map without a sidecar. Tests: `tools/tests/test_score_monitors.py` (60: the extractor rules on hand-made
scenes, the package cross-check, the tracked sidecars and counts, one negative test per validator error class, the installed Arena re-extraction),
`ArenaMapPort.Server.Tests/ScoreMonitorSidecarTests.cs` (the registry against the sidecar number for number, the mount, the face of every word, the counts).

### Artwork package

`python tools/extract/extract_monitor_templates.py [--check]` exports nine sprites of `sharedassets1.assets` as PNG plus `monitors.json` into the ignored
`work/extract/monitors` (2,646,533 B of PNG, 4,702 B of manifest): Background, Timer_Glow_White, Vignette (9-slice, border 80), Icon_Glow,
Icon_Background, Score_Icon_Scull (a trophy, in spite of its name), Status_Icon_Scull, Status_Toggle_On and Status_Toggle_Off. Every sprite is exported as the whole texture,
which for these nine equals the sprite rect (checked) and carries transparent pixels around the tight mesh of the trophy, the skull, the glow and the toggle-off
disc (a test pins the alpha bounds): drawing the PNG over the rect is what Unity draws, and Unity's preserve-aspect fit uses the rect. The pins are the
SHA-256 of the DECODED RGBA pixels (in the tracked extractor, like the selection map's), never a hash of an Arena file: sharedassets1.assets and its
`.resS` stream are no stage-2 key source, and the extractor, `validate_distribution.py` and a test each refuse a sprite whose asset or stream is one.
Left out on purpose: the three 1920 x 1080 noise sprites (13 MB, an animated overlay) and the scrolling interference texture; the faces are static.
The manifest names, per sprite, file, size, pivot (0.5, 0.5), 100 pixels per unit, border, `pixels_sha256`, the PNG's length and SHA-256, the faces that draw it
and its source ids. Tests: `tools/tests/test_monitor_templates.py` (24: pins, PNG determinism, package validation and tamper cases, the layout agreement, the
Arena key-source guard, real-data export; the real-data class skips without Arena).

### Staging

The client csproj target `ArenaMapPortStageMonitors` (Release and DllOnly, after `ArenaMapPortDistCopy`; an Error naming the extractor when
`work/extract/monitors/monitors.json` is missing) copies the manifest and `*.png` to `BepInEx/plugins/maschine-ArenaMapPort/monitors/` (plaintext like the
selection artwork; the bundles are the only encrypted files). `make_release_zips.py`: rule `score monitors` (core part, `monitors.json` or a leaf `.png`),
`monitors.json` required, the core part's text names the sprites. `validate_distribution.py`: `validate_monitors` (the extractor's own `validate`: exact file set,
reviewed table in order, lengths, SHA-256 and pinned pixels; byte equality with `work/extract/monitors`; no key-source asset). Fixtures and negative tests in
`test_release_zips.py` and `test_validate_distribution.py` (five new tests). Sizes: [dist-manifest.md](../dist-manifest.md), "Score monitor artwork addition".

## The layout of the three faces

`ArenaMapPort.Client/ScoreMonitorLayout.cs` (Unity-free, linked into the server tests) lists the elements of the faces back to front with the numbers decoded
from Arena's templates; `ScoreMonitorLayoutTests.cs` carries its own copy of the decoded values. Anchors are fractions of the parent box (the canvas), origin
bottom-left. Arena's animated overlays (noise, interference) and its Mask (a stencil clip to the background's alpha, which is opaque) are not drawn.

* **Timer** (word 0): Background sprite (full canvas, white), Timer_Glow_White (full canvas, preserve aspect, colour (0.47059, 0.90196, 0.58824, 0.19608)), a
  text row (0.08, 0.2) to (0.92, 0.8) holding MM, ":" and SS in Color32 (120, 230, 150), right / centre / left aligned, TextMeshPro auto-size 0.2 to 580, character
  spacing 2.7, line spacing -10, wrapping on; the three widths are what Arena's HorizontalLayoutGroup assigns (680 / 150 / 680 preferred, flexible 10 / 1 / 10:
  `TimerColumns` reproduces Arena's saved 728.95 / 154.90 / 728.95 in its reference row); Vignette (sliced, alpha 0.6).
* **Score** (words 1 and 6): black ground; side A's block (0, 0) to (0.492, 1) in Arena's red (0.93725, 0.00784, 0.22745) and side B's (0.508, 0) to (1, 1) in its
  blue (0.2549, 0.30196, 0.89804), both alpha 0.4902; the two scores in white (0.04, 0.2) to (0.45, 0.8) and (0.55, 0.2) to (0.96, 0.8), centred, auto-size,
  character spacing 20 on the left and 0 on the right (Arena's values), overflow Ellipsis; Icon_Glow (0, 0.2) to (1, 0.8) at alpha 0.29804; a dark disc
  (Icon_Background, (0.09804 x 3, 1)) at (0, 0.331) to (1, 0.664) with the trophy inside it ((0, 0.15) to (1, 0.87) of the disc), all three preserve aspect; Vignette.
* **Alive** (word 2): the same ground, blocks, glow, disc and Vignette, the skull instead of the trophy, and in each block a column of toggles, (0.17, 0.02) to
  (0.325, 0.98) for side A and (0.675, 0.02) to (0.83, 0.98) for side B: one cell of 200 canvas pixels per team member, centred top-first (`ToggleCenterOffset`),
  the Off sprite always, the On sprite over it while the member is alive. Arena's teams are five a side and fit the column (1000 of 1116 pixels); the contract
  allows 16 per side, so `ToggleSize` shrinks the cell to fit (69.8 pixels at 16).

Colours: `TeamColor(rgb, sideA)` is 0xRRGGBB at alpha 0.4902, or Arena's red (A) / blue (B) for -1 and any other out-of-range value. A colon in Arena's own font
(Bender Underlay Green Shine) is drawn with Bender Normal, the one font EFT ships of the family.

## Facts the client step stands on

* The TV props are plain meshes (`TV_LOD0`, mesh c5973fdf39f35287, the same on all four maps) whose OPAQUE screen quad lies 0.0071 prop-local units in front of
  Arena's canvas plane (21 mm at scale 3, 140 mm on Bowl's 19.68 boards); the bezel front is at 0.00864. A canvas at Arena's mount pose is hidden behind it, so the
  surface is a child of the prop at the mount pose lifted toward the reader by `Monitors/ScreenLift` prop-local units (0.0100 default, 0.0075 to 0.03): that is the
  lead's decision 5; a host negative control (lift 0 must show the dark screen) belongs to the first host check.
* Row selection at run time: the prop of a row is found by its path in the scene of that name, and the row is the one whose `AppliesTo(prop.TransformPoint(mount
  position))` holds within 0.05 m; only rows with `ActiveInScene` and a face other than none get a surface.
* Never a scene-wide search in periodic code, no extra camera, layer 0, no collider, shadows off (decision 5); fonts by name from EFT's own `UI/Fonts`.

## Verification (build machine, 2026-10-25)

`validate_map_descriptors.py` OK with the four registry blocks; `extract_score_monitors.py --check`: 4 of 4 sidecars current, 43 holders found once in the
four packages with the same activity; `extract_monitor_templates.py --check` OK; `dotnet build` client (DllOnly, 0 warnings, the target staged 10 files) and server
OK; `validate_distribution.py` DISTRIBUTION_OK with the monitors folder; `make_release_zips.py --plan`: core 15 files (was 5), 113 files in all. Suites: see the
final report of the change. **Not host-verified.**

## Hand-off to step 2

(Done on the same day: see "The client (step 2)" below. The text that follows is what step 1 handed over.)

Step 2 appends here: the public contract `ArenaMapPort.Client.ScoreMonitors` (ContractVersion 1, `Claim` / `Release` / `SetSides` / `SetScore` / `SetClock`),
the hub and the view, the developer keys `Monitors/Enabled`, `Monitors/ScreenLift`, `Monitors/Demo`, the demo, the ArenaMode binder and the Team Deathmatch
mapping, and the host checks. What it takes from step 1: `ArenaMapDescriptor.ScoreMonitors`, `ArenaScoreMonitor.AppliesTo`, `ArenaScoreMonitorMount`,
`ScoreMonitorLayout` (faces, elements, sprite table, toggle and timer helpers, `TeamColor`), the staged `monitors/monitors.json` and PNGs.

### Rows for the host checks

`stand` is a point in front of the screen on its reading side (x, z; the height is the player's), `look yaw` the heading that faces the screen from there.
Bowl's boards are read from the pit at about 25 m (about 100 m from the far stands).

#### Equator (11 holders, 11 active)

| scene | prop | type | face | active | canvas centre (x, y, z) | size m | read from yaw | stand (x, z) | look yaw |
|---|---|---|---|---|---|---|---|---|---|
| Equator | `TV/Arena_Home_theater` | 1 | score | yes | (-17.8, 10.3, -1.5) | 3.13 x 1.88 | 90 | (-13.8, -1.5) at 4 m | 270 |
| Equator | `TV/Arena_Home_theater (1)` | 0 | timer | yes | (-17.8, 10.3, 3.2) | 3.13 x 1.88 | 90 | (-13.8, 3.3) at 4 m | 270 |
| Equator | `TV/Arena_Home_theater (2)` | 2 | alive | yes | (-17.8, 10.3, 7.8) | 3.13 x 1.88 | 90 | (-13.8, 7.8) at 4 m | 270 |
| Equator | `TV/Arena_Home_theater (3)` | 2 | alive | yes | (17.3, 10.4, -1.3) | 3.13 x 1.88 | 270 | (13.3, -1.3) at 4 m | 90 |
| Equator | `TV/Arena_Home_theater (4)` | 0 | timer | yes | (17.3, 10.3, 7.7) | 3.13 x 1.88 | 270 | (13.3, 7.7) at 4 m | 90 |
| Equator | `TV/Arena_Home_theater (5)` | 1 | score | yes | (0.1, 2.2, 40.7) | 1.35 x 0.81 | 0 | (0.1, 44.7) at 4 m | 180 |
| Equator | `TV/Arena_Home_theater (6)` | 1 | score | yes | (0.6, 2.2, -35.1) | 1.35 x 0.81 | 180 | (0.6, -39.1) at 4 m | 0 |
| Equator_TeamFight | `TV/Arena_Home_theater (5)` | 7 | none | yes | (17.7, 11.6, 4.0) | 1.25 x 0.75 | 91 | (21.7, 4.0) at 4 m | 271 |
| Equator_TeamFight | `TV/Arena_Home_theater (6)` | 6 | score | yes | (27.2, 12.4, 1.6) | 2.08 x 1.26 | 250 | (23.4, 0.2) at 4 m | 70 |
| Equator_TeamFight | `TV/Arena_Home_theater (7)` | 7 | none | yes | (-18.3, 11.8, 1.4) | 1.25 x 0.75 | 270 | (-22.3, 1.3) at 4 m | 90 |
| Equator_TeamFight | `TV/Arena_Home_theater (8)` | 6 | score | yes | (-26.4, 12.7, 4.9) | 2.08 x 1.26 | 90 | (-22.4, 4.9) at 4 m | 270 |

#### Bay5 (8 holders, 8 active)

| scene | prop | type | face | active | canvas centre (x, y, z) | size m | read from yaw | stand (x, z) | look yaw |
|---|---|---|---|---|---|---|---|---|---|
| Bay5_TeamFight | `TV/Arena_Home_theater (5)` | 0 | timer | yes | (105.8, 10.4, -16.7) | 3.13 x 1.88 | 9 | (106.4, -12.8) at 4 m | 189 |
| Bay5_TeamFight | `TV/Arena_Home_theater (7)` | 0 | timer | yes | (67.2, 5.1, 24.2) | 2.08 x 1.26 | 90 | (71.2, 24.2) at 4 m | 270 |
| Bay5_TeamFight | `TV/Arena_Home_theater (9)` | 1 | score | yes | (130.6, 1.8, 71.1) | 2.08 x 1.26 | 133 | (133.5, 68.4) at 4 m | 313 |
| Bay5_TeamFight | `TV/Arena_Home_theater (14)` | 0 | timer | yes | (61.8, 5.4, 67.8) | 3.13 x 1.88 | 180 | (61.8, 63.8) at 4 m | 0 |
| Bay5_TeamFight | `TV/Arena_Home_theater (16)` | 2 | alive | yes | (98.2, 6.8, -14.2) | 3.13 x 1.88 | 0 | (98.2, -10.2) at 4 m | 180 |
| Bay5_TeamFight | `TV/Arena_Home_theater (18)` | 2 | alive | yes | (61.2, 4.2, 44.5) | 3.13 x 1.88 | 90 | (65.2, 44.5) at 4 m | 270 |
| Bay5_TeamFight | `TV/Arena_Home_theater (22)` | 2 | alive | yes | (113.4, 2.3, 35.7) | 2.08 x 1.26 | 270 | (109.4, 35.7) at 4 m | 90 |
| Bay5_TeamFight | `TV/Arena_Home_theater (24)` | 1 | score | yes | (67.6, -1.1, -10.1) | 2.08 x 1.26 | 311 | (64.6, -7.4) at 4 m | 131 |

#### AirPit (8 holders, 6 active)

| scene | prop | type | face | active | canvas centre (x, y, z) | size m | read from yaw | stand (x, z) | look yaw |
|---|---|---|---|---|---|---|---|---|---|
| Airpit | `TV/Arena_Home_theater` | 2 | alive | yes | (-33.4, 7.4, -6.4) | 3.13 x 1.88 | 51 | (-30.3, -3.9) at 4 m | 231 |
| Airpit | `TV/Arena_Home_theater (1)` | 0 | timer | yes | (-43.9, 8.9, 2.2) | 3.13 x 1.88 | 90 | (-39.9, 2.2) at 4 m | 270 |
| Airpit | `TV/Arena_Home_theater (2)` | 1 | score | yes | (-34.5, 7.4, 14.8) | 3.13 x 1.88 | 131 | (-31.5, 12.1) at 4 m | 311 |
| Airpit | `TV/Arena_Home_theater (3)` | 2 | alive | yes | (9.4, 7.4, 5.3) | 3.13 x 1.88 | 236 | (6.1, 3.1) at 4 m | 56 |
| Airpit | `TV/Arena_Home_theater (4)` | 0 | timer | yes | (20.0, 8.9, -3.3) | 3.13 x 1.88 | 270 | (16.0, -3.3) at 4 m | 90 |
| Airpit | `TV/Arena_Home_theater (5)` | 1 | score | yes | (10.3, 7.4, -16.3) | 3.13 x 1.88 | 306 | (7.1, -14.0) at 4 m | 126 |
| Airpit | `TV/Arena_Home_theater (9)` | 1 | score | no | (-31.7, 4.0, 77.7) | 4.05 x 2.44 | 90 | (-26.7, 77.7) at 5 m | 270 |
| Airpit | `TV/Arena_Home_theater (11)` | 1 | score | no | (-12.6, 1.7, -68.4) | 1.12 x 0.68 | 178 | (-12.5, -72.4) at 4 m | 358 |

#### Bowl (16 holders, 6 active)

| scene | prop | type | face | active | canvas centre (x, y, z) | size m | read from yaw | stand (x, z) | look yaw |
|---|---|---|---|---|---|---|---|---|---|
| Bowl | `TV/Arena_Home_theater` | 1 | score | no | (-83.8, 32.8, 25.3) | 20.51 x 12.36 | 139 | (-67.3, 6.5) at 25 m | 319 |
| Bowl | `TV/Arena_Home_theater (1)` | 0 | timer | no | (-84.5, 32.3, -95.3) | 20.51 x 12.36 | 47 | (-66.3, -78.2) at 25 m | 227 |
| Bowl | `TV/Arena_Home_theater (2)` | 2 | alive | no | (82.3, 32.4, -96.0) | 20.51 x 12.36 | 317 | (65.1, -77.8) at 25 m | 137 |
| Bowl | `TV/Arena_Home_theater (3)` | 0 | timer | no | (85.2, 31.1, 26.6) | 20.51 x 12.36 | 218 | (69.8, 6.9) at 25 m | 38 |
| Bowl | `TV/Arena_Home_theater (4)` | 1 | score | no | (90.2, 32.5, -34.8) | 20.51 x 12.36 | 270 | (65.2, -34.7) at 25 m | 90 |
| Bowl | `TV/Arena_Home_theater (5)` | 2 | alive | no | (-92.9, 32.3, -35.4) | 20.51 x 12.36 | 91 | (-67.9, -35.8) at 25 m | 271 |
| Bowl | `TV/Arena_Home_theater (6)` | 1 | score | no | (41.8, 1.7, -12.1) | 1.26 x 0.76 | 46 | (44.7, -9.4) at 4 m | 226 |
| Bowl | `TV/Arena_Home_theater (7)` | 1 | score | no | (-45.3, 1.7, -58.8) | 1.26 x 0.76 | 232 | (-48.5, -61.3) at 4 m | 52 |
| Bowl | `TV/Arena_Home_theater (8)` | 10 | none | no | (43.8, 1.7, -14.2) | 1.26 x 0.76 | 46 | (46.7, -11.4) at 4 m | 226 |
| Bowl | `TV/Arena_Home_theater (9)` | 10 | none | no | (-43.5, 1.7, -61.1) | 1.26 x 0.76 | 232 | (-46.7, -63.5) at 4 m | 52 |
| Bowl_TeamFight | `TV_Wires_TeamFight/Arena_Home_theater ALIVE` | 2 | alive | yes | (-84.4, 32.3, -95.2) | 20.51 x 12.36 | 47 | (-66.1, -78.1) at 25 m | 227 |
| Bowl_TeamFight | `TV_Wires_TeamFight/Arena_Home_theater ALIVE` | 2 | alive | yes | (85.0, 31.1, 26.4) | 20.51 x 12.36 | 218 | (69.6, 6.7) at 25 m | 38 |
| Bowl_TeamFight | `TV_Wires_TeamFight/Arena_Home_theater SCORE` | 1 | score | yes | (-83.8, 32.8, 25.3) | 20.51 x 12.36 | 139 | (-67.3, 6.5) at 25 m | 319 |
| Bowl_TeamFight | `TV_Wires_TeamFight/Arena_Home_theater SCORE` | 1 | score | yes | (82.3, 32.4, -96.0) | 20.51 x 12.36 | 317 | (65.1, -77.8) at 25 m | 137 |
| Bowl_TeamFight | `TV_Wires_TeamFight/Arena_Home_theater TIMER` | 0 | timer | yes | (-92.6, 32.3, -35.4) | 20.51 x 12.36 | 91 | (-67.6, -35.8) at 25 m | 271 |
| Bowl_TeamFight | `TV_Wires_TeamFight/Arena_Home_theater TIMER` | 0 | timer | yes | (89.8, 32.5, -34.8) | 20.51 x 12.36 | 270 | (64.8, -34.7) at 25 m | 90 |

## The client (step 2, 2026-10-25)

Status: **implemented and unit-tested on the build machine; nothing of it has run on a host.** The client DLL gains six files and two call sites; no bundle, key file,
server file, NavMesh or package changes (the `monitors/` folder of step 1 is the only staged input and its file set is unchanged).

| file | what |
|---|---|
| `ScoreMonitors.cs` | the public contract `ArenaMapPort.Client.ScoreMonitors` (Unity-free, nine members, `ContractVersion` 1) |
| `ScoreMonitorHub.cs` | claim semantics and the snapshot behind it (Unity-free, one lock) |
| `ScoreMonitorModel.cs` | clamps, the clock, number text, `ScoreMonitorFrames` (what a face shows for a snapshot), `UsableRows` (Unity-free) |
| `ScoreMonitorDemo.cs` | the developer demo cycle (Unity-free, pure) |
| `ScoreMonitorLayout.cs` | step 1: the elements, colours and sprite table of the three faces |
| `ScoreMonitorView.cs` | the only Unity file: scene events, tick, sprites, font, canvases, presenting, the demo driver, the three developer keys |
| `HarmonyPatches.cs`, `Plugin.cs` | `ScoreMonitorView.Install()` (after `GateSounds.Install()`) and `ScoreMonitorView.Tick()` (in `LateUpdate`, before the gate poll: `MapNoticeWiringTests` pins `MapNotices.Tick()` as the last statement) |

There is no Harmony patch (the generic-class trap of AGENTS.md does not apply) and nothing calls `ArenaInstallationGate`.

### The surface in the scene

A claim builds one surface per USABLE row of the map (`ScoreMonitorModel.UsableRows`: active in the imported scene AND a type word with a face; Equator 9 of 11, Bay5 8 of 8,
AirPit 6 of 8, Bowl 6 of 16). The prop is found by its path in the scene of the row (`BoothDoorRepairs.FindAllByPath`, 44 to 82 scene roots per map) and by
`ArenaScoreMonitor.AppliesTo(prop.TransformPoint(mount position))` within 0.05 m, retried twice a second while props are missing, until 20 s have passed since the claim AND since the last owned scene finished loading (review fix 2026-10-25: the Scripts scene loads first and the content scenes up to tens of seconds later, so a claim made at once, as the developer demo does, must not give up before they arrive); one Warning after that.
A prop that is inactive at run time is skipped (Debug line) and never switched on. The surface is

* a child of the prop at Arena's mount pose (local rotation (-0.5, 0.5, 0.5, -0.5), uniform scale 0.00054, rect 1930 x 1163, pivot 0.5), plus the lift below;
* a world-space UGUI `Canvas` on layer 0 (the camera's Default layer; ArenaMode's boundary grid renders there too), no `GraphicRaycaster`, no collider, no extra camera and no
  render texture (an offscreen camera would trigger every global `Camera.onPreCull` subscriber: EFT's culling manager, the native decals, the scope mask renderer);
* built from `ScoreMonitorLayout.ElementsOf(face)` back to front: `Image`s (solid fills, Arena's sprites, the vignette sliced with its border 80 at 100 pixels per unit) and
  `TextMeshProUGUI` texts with Arena's settings (auto-size 0.2 to 580 inside the rect, middle line, its horizontal alignment, character spacing 2.7 / 20 / 0, line spacing
  -10 / 0, overflow Ellipsis on the scores). `TextAlignmentOptions.Left / Center / Right` are 513 / 514 / 516 in EFT's TMP: horizontal | vertical Middle (512), exactly
  Arena's "halign x valign 512".

**The TV geometry finding (why the lift exists).** All 43 props use one mesh (`TV_LOD0`, c5973fdf39f35287, material "Reflective/Bumped Specular SMap", opaque, ZWrite on). In
PROP-LOCAL units its +X-facing planes are: the screen quad at x = +0.00710 (y +-0.5232, z 0.0547 to 0.6835, a uniform dark grey of about RGB 41, front-facing for a viewer on
+X) and the bezel front at +0.00864 (a ring OUTSIDE the canvas rect). Arena's canvas plane (the mount pose) is x = 0, i.e. 0.0071 x prop scale BEHIND an opaque quad: at
Arena's pose the canvas is hidden. (How Arena shows it through the glass is not recoverable: its IL2CPP metadata is encrypted.) The surface is therefore lifted toward the reader
(prop +X, the canvas faces -X) by `Monitors/ScreenLift` prop-local units, default 0.0100:

| prop scale | maps | screen quad | bezel front | canvas at lift 0.0100 | clearance over the quad | in front of the bezel front |
|---|---|---|---|---|---|---|
| 3.000 | Equator, Bay5, AirPit atria | 21.3 mm | 25.9 mm | 30.0 mm | 8.7 mm | 4.1 mm |
| 2.000 | Bay5, Equator TeamFight | 14.2 mm | 17.3 mm | 20.0 mm | 5.8 mm | 2.7 mm |
| 1.296 | Equator booth screens | 9.2 mm | 11.2 mm | 13.0 mm | 3.8 mm | 1.8 mm |
| 3.887 | AirPit pocket (dark) | 27.6 mm | 33.6 mm | 38.9 mm | 11.3 mm | 5.3 mm |
| 19.682 | Bowl stadium boards | 139.7 mm | 170.1 mm | 196.8 mm | 57.1 mm | 26.8 mm |

The remaining scales (1.2, 1.212, 1.078) follow the same proportions. The window between the quad and the bezel front is only 0.0071 to 0.0086 prop-local; 0.0078 (its middle) leaves 2.6 mm at scale 3 and would z-fight at 30 m and beyond,
0.0100 floats 4 mm over the bezel front where nothing overlaps (the bezel is outside the canvas rect). The key is LIVE (changing it moves every built surface the next frame) and
its range is 0 to 0.03: values below 0.0071 are the NEGATIVE CONTROL of the host check (the TV must stay dark, which proves the finding), 0.0075 to 0.03 is the working window.
The sidecar and the descriptor keep Arena's authored pose; the lift is a client constant.

### Faces, setters, and what is not drawn

| face (type words) | fed by | shows |
|---|---|---|
| timer (0) | `SetClock` | MM, ":" and SS in Color32 (120, 230, 150), the glow, the vignette; 00:00 until a clock is set; 99:59 is the cap |
| score (1, 6) | `SetSides`, `SetScore` | the two scores in white (0..9999), side A's block left, side B's right, the trophy disc |
| alive (2) | `SetSides`, `SetScore` | the same blocks, the skull disc and per side `teamSize` toggles (200 canvas pixels, shrunk to fit: 69.8 at 16), the first `min(alive, teamSize)` from the top lit (On sprite over the Off sprite); unknown alive (-1) lights none |
| none (7, 10, anything new) | | no surface, the TV stays dark |

Deviations from Arena, all deliberate: no animated noise or interference overlays and no `Mask` (Arena's background is opaque); the colon uses Bender Normal instead of
"Bender Underlay Green Shine"; word wrapping is OFF (Arena's is on) so that a number never breaks into two lines, auto-size shrinks it instead; the toggles shrink to fit the
contract's 16 per side. Missing sprites degrade the faces to plain colour blocks and text (the glow, vignette, disc and icons are left out; toggles become plain squares) with
ONE Warning; no font gives ONE Warning and no text.

### The public contract

`ArenaMapPort.Client.ScoreMonitors`, a public static class in the plugin `com.maschine.ArenaMapPort`, found by reflection. **Members are APPEND-ONLY**: a member is never removed or
changed in meaning without raising `ContractVersion` (a binder written for 1 refuses any other version); a new member does not raise it. `ScoreMonitorContractTests` lists the nine
members and fails on a tenth until it is added deliberately. Text is ASCII digits only.

| member | meaning |
|---|---|
| `int ContractVersion` | 1 |
| `bool IsAvailable` | `Monitors/Enabled` is on, an owned raid is loaded and the map has usable monitors; false in a menu, on a vanilla map, on a map without such rows |
| `bool IsClaimed` | a plugin or the developer demo holds the monitors |
| `int Count` | surfaces built for the current claim (diagnostic, 0 until the first frames after the claim) |
| `int Claim(string owner)` | a handle above 0, or 0 when not possible (blank owner, disabled, no raid, nothing usable, another owner holds them); the same owner claiming again gets the SAME handle and keeps the content; a developer-demo claim never blocks a plugin, the plugin's claim takes over (fresh content) |
| `bool Release(int claim)` | the monitors go dark (the surfaces are destroyed); false for a stale handle, nothing changes |
| `bool SetSides(int claim, int colorA, int colorB, int teamSize)` | colours 0xRRGGBB, or -1 (any negative or above 0xFFFFFF value) for Arena's red (A, the left block) and blue (B, the right block), drawn at Arena's alpha 0.4902; `teamSize` 0..16 clamped, 5 until set |
| `bool SetScore(int claim, int scoreA, int scoreB, int aliveA, int aliveB)` | scores 0..9999 clamped; alive 0..16 clamped or -1 unknown (the lit toggles are the smaller of the count and the team size) |
| `bool SetClock(int claim, int mode, float seconds)` | 0 none (00:00); 1 frozen at `seconds` (whole seconds, rounded up like a countdown); 2 counting DOWN to the absolute `UnityEngine.Time.time` `seconds` (rounded up, like ArenaMode's HUD); 3 counting UP from the absolute Time.time `seconds` (rounded down); clamped at 0 and 99:59; the monitors tick by themselves; false for an unknown mode (nothing changes), a non-finite value counts as 0 |

Rules: any thread may call any member (state only, one lock; the Unity objects are touched by the plugin's own main-thread tick) and none throws. A setter is idempotent and
last-write-wins; an unchanged call costs a lock and a compare and bumps nothing, so a mode may push every frame. Handles are never reused within a process: a stale or foreign
handle (released, replaced by another claim, or from an earlier raid) makes every setter return false and change nothing, so a mode that lost its claim notices at its next call. The claim
ends with the owned Scripts scene's unload. Side A is the left block and side B the right one; which team is which is the caller's choice. Time base: the simulation clock
`UnityEngine.Time.time` for the modes 2 and 3 (if ArenaMode ever moves to an unscaled clock, the absolute values it sends move with it). A claim made before the props are found
shows them as soon as they exist.

### Developer keys (`Plugin.Instance.Settings`, never public)

| key | default | takes effect | what |
|---|---|---|---|
| `Monitors/Enabled` | true | next claim | off: a claim is refused (`IsAvailable` false) |
| `Monitors/ScreenLift` | 0.0100 | live | how far in front of Arena's mount pose the surface floats, in units of the prop; 0 to 0.03 (see the finding above) |
| `Monitors/Demo` | Off | live | Cycle: the demo claims as owner `developer-demo` in an owned raid and runs a 70 s cycle; a plugin's claim takes it over |

No public key (`CompactConfiguration.IsPublic` and `MapConfigVisibility.IsPlayerOption` are untouched; `ScoreMonitorSourcePinTests` pins that). The demo cycle
(`ScoreMonitorDemo`): DIGITS 10 s (five slots of 2 s: scores 0, 9, 99, 999, 9999 against 0, 7, 88, 777, 9999; alive 5/5, 4/3, 2/5, 0/1 and unknown; the clock frozen at 0, 59,
600, 7200 which clamps to 99:59, and 3599), MATCH 5 25 s (teams of 5 in Arena's red and blue, scores climbing, alive counts dipping, the clock counting down to the end of the
section), MATCH 16 25 s (teams of 16 in the cyan 0x7FDBFF and red 0xFF6B6B of ArenaMode's HUD, the clock counting up), RESULT 10 s (final scores, everybody alive, the clock
frozen at 0).

### Life cycle and cost

* `Install` (once): the three keys, the hub's three queries (plain volatile fields, never a `ConfigEntry` read from another thread), `sceneLoaded` / `sceneUnloaded`.
* Owned Scripts scene loaded: `[Monitors] <map>: N of M monitor row(s) can show a face` (Debug); the hub now answers `IsAvailable`.
* Idle: `Tick` returns on `!_installed || (!_raidLoaded && _claim == 0 && _demoClaim == 0)`, one flag test per frame (vanilla maps, menus, the hideout); in an owned raid without a claim it
  reads the demo key and two volatile fields (`HasLog`, `Version`).
* A claim (or a version change): the font is resolved once per raid (`Resources.LoadAll<TMP_FontAsset>("UI/Fonts")`, "Bender Normal" first, never a "DON'T USE" or "Overlay"
  entry; a negative result is cached), the sprites the pending faces need are loaded one PNG per frame (`ImageConversion.LoadImage`, non-readable, mip-mapped, size checked against
  the layout table), then at most 3 monitors are built per frame inside a 6 ms budget. Memory with all nine sprites: about 45 MB of texture, destroyed with the raid.
  A plugin that takes the monitors over from the demo keeps the built surfaces.
* After the build only a changed snapshot or a new clock second touches a UI object, and only the texts, colours and toggles that differ from what is shown (the clock is
  formatted once per second and only when a timer face exists).
* The raid ends (Scripts scene unloaded): claims are refused, the canvases are destroyed (a content scene may unload after the sprites below), then the sprites and textures,
  then the claim is voided. Every built object is Unity-null checked (a content scene may unload first: the monitor is dropped with a Debug line). An exception in `Tick` is one
  Warning (`the score monitors stopped for this claim`), tears the claim's surfaces down and does not retry that claim.
* Logging, tag `[Monitors]`: Debug for the map line, the claim, the font, each sprite with its load time, `built N monitor(s) in X ms`, removals; Warnings for sprites missing or
  unreadable (one line, the names), no font, props not found 20 s after the claim and the last owned scene load, the tick exception; nothing at Info.

### The ArenaMode binder

The mode side. We never edit ArenaMode; this is the file for its agent (namespace `ArenaMode.Compat`, pattern of `ArenaMapPortSpawnGates.cs`). It was compiled and run against the
real core on 2026-10-25 (33 checks: an older MapPort, another contract version, an incompatible signature, a map without monitors, a claim, an unchanged frame sends nothing, a
changed score sends only its group, the demo cannot take a plugin's claim, a push after the raid ended returns false).

```csharp
using System;
using System.Reflection;
using BepInEx.Bootstrap;

namespace ArenaMode.Compat;

/// <summary>
/// Optional MapPort contract: the score monitors, the TV props of the four ported maps (ArenaMapPort.Client.ScoreMonitors, ContractVersion 1). Bound by reflection with exact
/// signature checks, like <see cref="ArenaMapPortSpawnGates"/>; nothing here throws into the mode, and a MapPort without the type (an older build), a missing plugin, another
/// contract version, a map without monitors or an incompatible member simply yields no session.
///
/// Use: <c>var session = ArenaMapPortScoreMonitors.TryClaim("ArenaMode Team Deathmatch");</c> once per match after the raid started, <c>session.Push(frame)</c> every frame (groups
/// that did not change are not sent, and MapPort's setters are idempotent anyway), <c>session.Release()</c> on every exit path. Push returns false once the claim is gone
/// (the raid ended, or another owner took the monitors): stop pushing then.
/// </summary>
internal static class ArenaMapPortScoreMonitors
{
    private const string PluginId = "com.maschine.ArenaMapPort";
    private const string TypeName = "ArenaMapPort.Client.ScoreMonitors";
    private const BindingFlags Declared = BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly;

    /// <summary>Everything the monitors show. Side A is the LEFT block (Arena's red), side B the RIGHT block (Arena's blue).</summary>
    internal readonly struct Frame
    {
        internal Frame(int colorA, int colorB, int teamSize, int scoreA, int scoreB, int aliveA, int aliveB, int clockMode, float clockSeconds)
        {
            ColorA = colorA; ColorB = colorB; TeamSize = teamSize;
            ScoreA = scoreA; ScoreB = scoreB; AliveA = aliveA; AliveB = aliveB;
            ClockMode = clockMode; ClockSeconds = clockSeconds;
        }
        internal int ColorA { get; }        // 0xRRGGBB, or -1 for Arena's red
        internal int ColorB { get; }        // 0xRRGGBB, or -1 for Arena's blue
        internal int TeamSize { get; }      // toggles per side on the alive faces, 0..16
        internal int ScoreA { get; }        // 0..9999
        internal int ScoreB { get; }
        internal int AliveA { get; }        // lit toggles, or -1 = unknown (all off)
        internal int AliveB { get; }
        internal int ClockMode { get; }     // 0 none (00:00), 1 frozen at ClockSeconds, 2 counting down to the absolute Time.time ClockSeconds, 3 counting up from it
        internal float ClockSeconds { get; }
    }

    internal static Session TryClaim(string owner)
    {
        if (!Chainloader.PluginInfos.TryGetValue(PluginId, out var plugin) || plugin.Instance == null) return null;
        return TryClaimContract(plugin.Instance.GetType().Assembly.GetType(TypeName), owner);
    }

    // Kept separate for the production-linked optional-contract checks, like ArenaMapPortSpawnGates.TryHoldContract.
    internal static Session TryClaimContract(Type type, string owner)
    {
        if (type == null) return null; // an older MapPort: no monitors, no message
        try
        {
            var version = Getter<int>(type, "ContractVersion");
            var available = Getter<bool>(type, "IsAvailable");
            var claim = Bind<Func<string, int>>(type, "Claim", typeof(int), typeof(string));
            var release = Bind<Func<int, bool>>(type, "Release", typeof(bool), typeof(int));
            var setSides = Bind<Func<int, int, int, int, bool>>(type, "SetSides", typeof(bool),
                typeof(int), typeof(int), typeof(int), typeof(int));
            var setScore = Bind<Func<int, int, int, int, int, bool>>(type, "SetScore", typeof(bool),
                typeof(int), typeof(int), typeof(int), typeof(int), typeof(int));
            var setClock = Bind<Func<int, int, float, bool>>(type, "SetClock", typeof(bool),
                typeof(int), typeof(int), typeof(float));
            if (version == null || available == null || claim == null || release == null || setSides == null || setScore == null || setClock == null)
            {
                Plugin.ModLogger.LogWarning("ArenaMapPort score monitors are present but their contract is incompatible; the monitors stay off.");
                return null;
            }
            // A new contract version means a removed or re-defined member: this binder knows version 1 only.
            if (version() != 1 || !available()) return null; // not an owned raid, a map without usable monitors, or the feature is off
            int handle = claim(owner);
            return handle > 0 ? new Session(handle, release, setSides, setScore, setClock) : null;
        }
        catch (Exception ex)
        {
            Plugin.ModLogger.LogWarning("ArenaMapPort score monitors unavailable: " + ex.GetBaseException().Message);
            return null;
        }
    }

    private static Func<T> Getter<T>(Type type, string name)
    {
        var getter = type.GetProperty(name, Declared)?.GetGetMethod();
        if (getter == null || getter.ReturnType != typeof(T) || getter.GetParameters().Length != 0 || getter.ContainsGenericParameters) return null;
        return (Func<T>)Delegate.CreateDelegate(typeof(Func<T>), getter);
    }

    private static TDelegate Bind<TDelegate>(Type type, string name, Type returns, params Type[] args) where TDelegate : Delegate
    {
        MethodInfo method;
        try { method = type.GetMethod(name, Declared, null, args, null); }
        catch (AmbiguousMatchException) { return null; }
        if (method == null || method.ReturnType != returns || method.ContainsGenericParameters) return null;
        return (TDelegate)Delegate.CreateDelegate(typeof(TDelegate), method);
    }

    internal sealed class Session
    {
        private readonly int _claim;
        private readonly Func<int, bool> _release;
        private readonly Func<int, int, int, int, bool> _setSides;
        private readonly Func<int, int, int, int, int, bool> _setScore;
        private readonly Func<int, int, float, bool> _setClock;
        private Frame? _sent;
        private bool _dead;

        internal Session(int claim, Func<int, bool> release, Func<int, int, int, int, bool> setSides,
            Func<int, int, int, int, int, bool> setScore, Func<int, int, float, bool> setClock)
        { _claim = claim; _release = release; _setSides = setSides; _setScore = setScore; _setClock = setClock; }

        internal bool Alive => !_dead;

        /// <summary>Sends the groups of the frame that changed since the last push. False: the claim is gone or a call failed; the session is dead.</summary>
        internal bool Push(in Frame f)
        {
            if (_dead) return false;
            try
            {
                var last = _sent;
                bool ok = true;
                if (last == null || last.Value.ColorA != f.ColorA || last.Value.ColorB != f.ColorB || last.Value.TeamSize != f.TeamSize)
                    ok &= _setSides(_claim, f.ColorA, f.ColorB, f.TeamSize);
                if (ok && (last == null || last.Value.ScoreA != f.ScoreA || last.Value.ScoreB != f.ScoreB
                    || last.Value.AliveA != f.AliveA || last.Value.AliveB != f.AliveB))
                    ok &= _setScore(_claim, f.ScoreA, f.ScoreB, f.AliveA, f.AliveB);
                if (ok && (last == null || last.Value.ClockMode != f.ClockMode || last.Value.ClockSeconds != f.ClockSeconds))
                    ok &= _setClock(_claim, f.ClockMode, f.ClockSeconds);
                if (!ok) { _dead = true; return false; }
                _sent = f;
                return true;
            }
            catch (Exception ex)
            {
                _dead = true;
                Plugin.ModLogger.LogWarning("ArenaMapPort score monitors stopped: " + ex.GetBaseException().Message);
                return false;
            }
        }

        /// <summary>Gives the monitors back; safe to call twice and after the raid ended (a stale handle changes nothing).</summary>
        internal void Release()
        {
            if (_dead) return;
            _dead = true;
            try { _release(_claim); }
            catch (Exception ex) { Plugin.ModLogger.LogWarning("ArenaMapPort score monitors release failed: " + ex.GetBaseException().Message); }
        }
    }
}
```

### The Team Deathmatch mapping

Recommendation (the ArenaMode agent decides): the TVs show the FACTIONS the way their in-game colours read, A = BEAR (Arena's red, the left block) and B = USEC (Arena's blue, the right
block), whoever the local player is; colours -1 (Arena's own). The alternative, A = friendly in the HUD's cyan 0x7FDBFF and B = opponent in 0xFF6B6B, is the same code with
`SetSides(0x7FDBFF, 0xFF6B6B, ...)` and no swap. Also compiled and run in the 33 checks above (the pure function).

| monitor input | source in `TeamDeathmatchMode` (2026-10-25 tree) |
|---|---|
| claim | end of `OnMatchStart`, after the identity checks passed (a `Fail` there claims nothing); owner `"ArenaMode Team Deathmatch"` |
| `teamSize` | `TeamSize` (`_population.TeamSize`, 5 by default) |
| scores | `_match.Scoreboard.Totals(ArenaScoreboardSide.Friendly / Opponent).Kills`, swapped by `_population.LocalUsec`; after the result `_rules.FriendlyKills` / `OpponentKills` (cache by `Scoreboard.Version` if profiling asks; the push is idempotent either way) |
| alive | friendly `CountBots(localUsec)` + 1 while the local player is alive, opponent `CountBots(!localUsec)` (near full under continuous respawn: Arena's ALIVE meaning, kept) |
| clock, Assembling | frozen at the time limit (mode 1), or none (mode 0) without a limit |
| clock, Countdown | mode 2 to `_gateCountdownEndsAt` (reads 00:03, 00:02, 00:01; 00:00 while the spawn gates open) |
| clock, Live | mode 2 to `_rules.StartedAt + TimeLimitSeconds`, or mode 3 from `_rules.StartedAt` without a limit |
| clock, Finished | frozen (mode 1) at the time that was left (`TimeLimitSeconds - PlayingElapsed`), or the time played; stays for the 15 s result |
| push | every frame from `Tick`, right after `CheckMatchLimits()` and BEFORE the `HasResult` branch, OUTSIDE the 1 Hz `try` whose `catch` calls `Fail("roster could not be validated")` (a TV must never end a match) |
| release | `OnMatchEnd`, the first `ObserveNativeStop` that sets `_nativeStopObserved`, and `Fail` |

```csharp
using System;
using ArenaMode.Compat;

namespace ArenaMode.Modes;

/// <summary>
/// What Team Deathmatch puts on the ArenaMapPort score monitors (TV props). Pure: numbers in, one <see cref="ArenaMapPortScoreMonitors.Frame"/> out, so it is unit-testable
/// without a raid. Side A (the LEFT block, Arena's red) is BEAR and side B (the RIGHT block, Arena's blue) is USEC, whoever the local player is: the TVs show the factions
/// the way their in-game colours read, not "friendly" and "opponent".
/// </summary>
internal static class TeamDeathmatchMonitors
{
    internal static ArenaMapPortScoreMonitors.Frame FrameFor(
        bool localUsec, int teamSize, TdmMatchPhase phase, int timeLimitSeconds, float startedAt, float countdownEndsAt, float playingElapsed,
        int friendlyKills, int opponentKills, int friendlyAlive, int opponentAlive)
    {
        int bearKills = localUsec ? opponentKills : friendlyKills, usecKills = localUsec ? friendlyKills : opponentKills;
        int bearAlive = localUsec ? opponentAlive : friendlyAlive, usecAlive = localUsec ? friendlyAlive : opponentAlive;
        int mode;
        float seconds;
        switch (phase)
        {
            case TdmMatchPhase.Assembling: // the match length stands still until the teams are ready
                mode = timeLimitSeconds > 0 ? 1 : 0;
                seconds = timeLimitSeconds;
                break;
            case TdmMatchPhase.Countdown: // 00:03, 00:02, 00:01 (and 00:00 while the spawn gates open)
                mode = 2;
                seconds = countdownEndsAt;
                break;
            case TdmMatchPhase.Live: // the time left, or the time played when the match has no limit
                if (timeLimitSeconds > 0) { mode = 2; seconds = startedAt + timeLimitSeconds; }
                else { mode = 3; seconds = startedAt; }
                break;
            default: // Finished: frozen where the match ended
                mode = 1;
                seconds = timeLimitSeconds > 0 ? Math.Max(0f, timeLimitSeconds - playingElapsed) : playingElapsed;
                break;
        }
        // -1: Arena's own red and blue; unknown alive counts (-1) show every toggle off, so an unreadable roster is never drawn as a wiped team.
        return new ArenaMapPortScoreMonitors.Frame(-1, -1, teamSize, bearKills, usecKills, bearAlive, usecAlive, mode, seconds);
    }
}
```

The wiring (a new partial `TeamDeathmatchMode.Monitors.cs`, three one-line call sites; not compiled here):

```csharp
namespace ArenaMode.Modes;

public sealed partial class TeamDeathmatchMode
{
    private ArenaMapPortScoreMonitors.Session _monitors;

    // End of OnMatchStart, after the identity and roster checks passed.
    private void ClaimMonitors() => _monitors = ArenaMapPortScoreMonitors.TryClaim("ArenaMode Team Deathmatch");

    // Tick(): right after CheckMatchLimits(), before the HasResult branch, outside the 1 Hz try block.
    private void PushMonitors()
    {
        var monitors = _monitors;
        if (monitors == null || !monitors.Alive || _rules == null || _match == null || _population == null) return;
        try
        {
            bool localUsec = _population.LocalUsec;
            int friendly = HasResult ? _rules.FriendlyKills : _match.Scoreboard.Totals(ArenaScoreboardSide.Friendly).Kills;
            int opponent = HasResult ? _rules.OpponentKills : _match.Scoreboard.Totals(ArenaScoreboardSide.Opponent).Kills;
            int friendlyAlive = CountBots(localUsec) + (_local?.HealthController?.IsAlive == true ? 1 : 0);
            int opponentAlive = CountBots(!localUsec);
            var frame = TeamDeathmatchMonitors.FrameFor(localUsec, TeamSize, _rules.Phase, _rules.TimeLimitSeconds, _rules.StartedAt,
                _gateCountdownEndsAt, PlayingElapsed, friendly, opponent, friendlyAlive, opponentAlive);
            if (!monitors.Push(frame)) _monitors = null; // the claim is gone (the raid ended or another owner took the monitors): stop
        }
        catch (Exception ex)
        {
            _monitors = null;
            Plugin.ModLogger.LogWarning("Team Deathmatch score monitors stopped: " + ex.GetBaseException().Message);
        }
    }

    // OnMatchEnd, the first ObserveNativeStop that sets _nativeStopObserved, and Fail.
    private void ReleaseMonitors()
    {
        var monitors = _monitors;
        _monitors = null;
        monitors?.Release();
    }
}
```

Before it lands, re-check the line numbers of the 2026-10-25 reading (ArenaMode HEAD `0833cb3`, uncommitted edits elsewhere): `TeamDeathmatchMode.cs` `OnMatchStart` (factions, `_population`),
`Tick` (`CheckMatchLimits()` before the `HasResult` branch, the 1 Hz block with `Fail("roster could not be validated")`), `ObserveNativeStop`, `Fail`, `OnMatchEnd`;
`TeamDeathmatchMatchFlow.cs` (`TickMatchStart` sets `_gateCountdownEndsAt`, `PlayingElapsed`, `HasResult`); `TeamDeathmatchMatchPolicy.cs` (`StartedAt`, `TimeLimitSeconds`).
ArenaMode's `HudStyle` looks for a font named "BN SDF" that EFT's `UI/Fonts` does not have and may fall back to the first font of the list (a note for its agent; the monitors never use it).

### Tests

* `ScoreMonitorHubTests` (30): claim and handle semantics (same owner, other owner, blank, disabled / no raid / nothing usable, the demo takeover both ways, release, stale and foreign
  handles, a new raid, handles never reused), idempotent setters, every clamp, an unknown clock mode, the owner text, and two parallel tests (no torn snapshot, one holder).
* `ScoreMonitorModelTests` (51): the clock table (all modes, rounding, the 99:59 cap, non-finite values, an absolute time in the hours, a countdown that never skips or repeats a second),
  minutes and seconds text, the lit toggles, the frame, the usable rows of the four maps, and the demo cycle (sections, digit widths, team sizes, absolute clock targets, determinism).
* `ScoreMonitorContractTests` (9): the public surface is exactly the nine members, bound the way the binder binds it (a whole match through the delegates), nothing throws outside a raid or with a
  broken hub, a demo claim never blocks a plugin, and the doc's binder block still carries the exact bind lines.
* `ScoreMonitorSourcePinTests` (14): the core is Unity-free; the view has no scene-wide search, no camera, no collider, no Info line, no Harmony; three developer keys on Settings, none public;
  the lift is in prop-local units and live; the canvas is world space on layer 0 under the prop; the font rules; the tick's early return and budgets; guarded scene handlers and the end of the
  raid; Unity-null checks; the demo; prop lookup; the two call sites.
* Outside the repo: the binder block and the mapping function above were compiled and run against the real core (33 checks, `impl1\impl-mon-client\smoke`).

### Host checks (not run)

Deploy: close EFT, `tools/deploy-to-spt.ps1 -SptRoot 'D:\SPT41'` dry run, then `-Confirm`: the client DLL and the `monitors/` folder (10 files, 2.6 MB; nothing else changes), restart EFT,
confirm the MVID with `uibridge_status`. `Monitors/Demo = Cycle` is a developer key (mind the `uibridge_config_set` lease trap: set it before `gpu-run`, not during).

**A. Without ArenaMode, per map, NO BOTS, `Monitors/Demo = Cycle`.** Stand on the READING side of each screen at the rows above (`viewer_yaw`; the canvas forward points the other way).

1. Log (`uibridge_log`, filter `[Monitors]`): `<map>: N of M monitor row(s) can show a face`, `claim 1 by 'developer-demo'`, `font 'Jovanny Lemonad - Bender Normal SDF ...'`, nine
   `sprite ... loaded in X ms` lines, `built K monitor(s) in X ms` (K = 9 / 8 / 6 / 6), and NO Warning. `ScoreMonitors.Count` equals K.
2. Negative control first: `Monitors/ScreenLift = 0` must leave the TVs dark grey (the screen quad hides the canvas). Then 0.0100, 0.0078 and 0.02 at 5, 20 and 40 m, and from the
   pit on Bowl's 20.5 x 12.4 m boards; keep the value that z-fights nowhere.
3. Look at every face: Equator atrium rows (-17.8, 10.3, -1.5) score, (-17.8, 10.3, 3.2) timer, (-17.8, 10.3, 7.8) alive and the east atrium; the booth screens (0.1, 2.2, 40.7) read from z about 44.7
   and (0.6, 2.2, -35.1) read from z about -39.1; the TeamFight final-score boards (word 6, score face) at (27.2, 12.4, 1.6) and (-26.4, 12.7, 4.9); Bay5 (98.2, 6.8, -14.2) alive, (61.2, 4.2, 44.5) alive,
   (67.6, -1.1, -10.1) score; AirPit (-33.4, 7.4, -6.4) alive, (-34.5, 7.4, 14.8) score, (-43.9, 8.9, 2.2) timer; Bowl boards (-83.8, 32.8, 25.3) score, (-84.4, 32.3, -95.2) alive, (-92.6, 32.3, -35.4) timer.
4. DARK on purpose: AirPit's two pocket TVs, Bowl's ten base-set props, Equator's two small TeamFight boards (word 7).
5. Over the 10 s digit section read 0, 9, 99, 999, 9999 and the clock 00:00, 00:59, 10:00, 99:59, 59:59 on a score and a timer face; the 5 and 16 toggle columns (cell 200 px against 69.8 px); the
   alive counts 5, 4/3, 2/5, 0/1 and all toggles off; the Arena red / blue against the cyan / red of the second match section; the countdown (down) and the count-up; the result.
6. Frame time: `uibridge_status` `max` and ModProfiler `max_ms_in_frame` while the surfaces are built (the three big PNGs load one per frame) and in play; the 1 Hz clock tick must not show.
7. A second raid on the same map and one on another map: nothing left over (`Debug: N monitor(s) removed (raid ended)`), `Count` 0. A plain raid with `Demo = Off`: only the map line. Open the hideout once.
8. If the screens look dim (EFT's exposure and tone mapping apply to the unlit UI shader), report it before touching colours: the options are a cloned UI material with `_Color` above 1; UGUI vertex
   colours cannot exceed 1.0.

**B. With ArenaMode**, after its agent lands the binder and the mapping above: Team Deathmatch on all four maps. The score moves within a frame of a kill, a forfeit is counted, the countdown reads
00:03 to 00:01, the clock runs down and freezes at the result, the result stays up for its 15 s, the release on every exit (match end, native stop, `Fail`), the AirPit pocket screens stay dark, and
Bowl's boards read from the pit.

### Open points for the lead

* `Monitors/ScreenLift` has the range 0 to 0.03, not 0.0075 to 0.03: the negative control of the host check needs 0, and a BepInEx range clamps a smaller value.
* Word wrapping is off on the numbers (Arena's is on); a frozen clock rounds up like the countdown; before `SetSides` the team size is 5 (Arena's), a team size of 0 draws no toggle; lit toggles
  are the first ones from the top.
* Rendering in EFT is unverified in three places the host check answers: that the FPS camera draws a world-space canvas on layer 0, how EFT's exposure treats the unlit UI shader, and the z-fight
  window at distance. Fallback if canvases do not draw: TMP 3D text and a `Sprites/Default` quad with the same core, layout and contract.
* The texture cost (about 45 MB, 10 to 40 ms per big PNG) is paid per raid and only after a claim. Compressing them (a readable load, `Compress`) would cost more time than it saves.
