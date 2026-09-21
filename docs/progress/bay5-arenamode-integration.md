# Bay5 ArenaMode integration

## 2026-09-21 — implementation and geometry checkpoint

The user accepted Equator and requested Bay5 next. The companion ArenaMode change
`75a7430` admits `arena_bay5` for individual Deathmatch and Gun Game, with an optional
MapPort dependency, independent availability from Equator, and guarded revision-2
migration of the exact previous default map lists. Custom lists remain unchanged.
Team modes are outside this map's current acceptance scope. No loot or Scav raids.

### Map data and round-open geometry

`bay5.bots.json` provides four All-side player points and ten separate Bot/BotPmc
points in `BotZone_Main`, a cap of eight, five scav waves and four PMC wave entries.
Coordinates were derived from the source AI bearing points and checked through
UiBridge on the old installed map before the rebuild. These checks establish candidate
ground positions, not acceptance of the new bake or bot combat.

The 2,208 source AI bearing points define a combat envelope of x=44.415..146.295 and
z=-14.037..71.174. Sampling the containing rectangle at 1 m leaves at least six bot
points 25 m away; the regression requires at least three distinct candidates for a
two-bot replenishment burst. Closest player-to-bot spacing is 9.31 m and closest
player-to-exit spacing is 29.78 m. Standing room and connectivity remain native checks.

The old NavMesh (15,420 vertices / 7,142 triangles) disconnected the BEAR depot spawn
from the central yard. Bay5 has 200 active, non-trigger source colliders on numeric
layer 29, named `Cube` rather than `LevelBorder`. Both Arena and installed EFT name
that layer `LevelBorder`; the importer preserves numbers, while the Unity project's
TagManager leaves that slot unnamed. Runtime removal worked but name-only bake
filtering retained those invisible barriers. The bake now excludes the collider's
own layer 29, matching runtime ownership rather than inheriting its parent's layer.

Three descriptor paths are shared by runtime opening and NavMesh exclusion:

- `WAREHOUSE_DOORS (1)/Bay5_Warehouse_Gate_Door_01 (3)`
- `WAREHOUSE_DOORS (1)/Bay5_Warehouse_Gate_Door_01 (4)`
- `OLD_WAREHOUSE_DOORS/Gates_Depot_Frame`

The warehouse paths are unique. The depot contains two siblings of the same name;
both existing resolvers select the first, northern assembly at approximately
(50.574, -3.895, 7.525). Its eastern duplicate remains closed. Frame geometry is kept.
The final host test must demonstrate travel through the selected openings.

### Ordinary test exits

Two always-available, eight-second individual exits are mirrored in the descriptor
and sidecar, each with a 4 x 3 x 4 m trigger:

| Exit | Position |
|---|---|
| Bay5 North Loading Bay | (91.2427, -2.9252, 65.142) |
| Bay5 South Container Lane | (99.029, -3.8574, -2.8513) |

These are ordinary-raid test vehicles. ArenaMode uses its protected session return.
The initial ordinary geometry raid was disconnected through the native result and
treatment screens; the character was healed there before subsequent acceptance.
Take a fresh profile baseline for protected ArenaMode tests after ordinary tests.

### Independent review and offline verification

Independent source/data review found no P0-P2 issue. It checked numeric-layer owner
parity, frame retention, duplicate-path behavior, 14 unique spawn IDs, exact generated
zone/category/infiltration fields, spacing and coverage. New Python tests cover the
distance and generated-data contracts; C# tests cover bake/descriptor barriers and
exit transform/eligibility parity.

ArenaMode's full verifier passed 691 server tests (one optional APBS skip), source-linked
client harnesses and five compiled fault controls. The parent reran both leaf commands;
all four leaf gates passed. Equator's 34 material-cap tests and native client Release
build passed independently. Deployment and native acceptance are appended below when
completed; this checkpoint alone does not claim a playable release.

## 2026-09-21 07:17–07:45 CEST — first native Bay5 integration pass

This pass verified ArenaMode's first Bay5 lifecycle on the previously deployed map.
It also found two map-placement defects that require a new bundle/client acceptance
pass. The results below do not certify the later trigger-layer bake, relocated north
exit, nearby-floor exfil sampling, or Equator rain-cover bootstrap.

The restarted GPU client ran SPT 4.1.6 / EFT 0.16.9.5-40743 / Unity 2022.3.43f1,
UiBridge instance `aed4e141` (started 07:17:51), with ArenaMode code `75a7430`.
UiBridge confirmed all 25 maschine plugins and Redeploy Core identical across the
VM build, host disk and loaded process. The first Bay5 NavMesh installed at
07:29:21.319 contained **15,045 vertices / 6,945 triangles**; the same counts were
logged during the following Deathmatch load at 07:38:11.029.

Evidence is retained outside git under `D:/.unlazy/bay5-reflections/`:
`native-aed4e141-evidence.json` records loaded identity, navigation probes and native
menu admission; `native-aed4e141.log`, refreshed after shutdown through 07:45:43,
contains the ordinary raid, Deathmatch and Gun Game. An earlier mirrored log copy
ended before Gun Game; only the refreshed copy corroborates that later test.

### Geometry, spawn probes and ordinary exits

From the central yard at approximately (89.159, -3.693, 35.203), all four authored
player points, ten bot points and both original authored exit positions passed
UiBridge's NavMesh, zone, reachable-on-foot and standing-room checks. This is
navigation/placement evidence, not proof that an exit's runtime trigger chose the
same floor. The warehouse control at (139, -2.9, 45) also passed. Root's additional
native controls found both sides of the northern depot opening at approximately
(50.6, -3.9, 10) and (50.6, -3.9, 5) connected, together with depot interior and
eastern-yard points. The warehouse leaves were opened; the northern depot opening kept its frame.
There is no evidence requiring the duplicate eastern depot gate to be opened.

The old depot sample (52.7, -3.8, -6.3) remained unreachable even though nearby
interior points were connected. Source and final-bundle inspection found the
original `SPAWNS/Bear house (9)` box at (52.183, -3.815, -6.296), size
(2.2, 2.04, 2.14), enabled and non-trigger, with its original spawn script absent.
It is on numeric layer 13 (`Triggers`), which the installed Player collision mask
ignores but the old bake included. The real depot floor exists below it at
y=-3.8151. This is a nonphysical spawn-box NavMesh artifact, not a disconnected
depot or missing floor. Bay5 has exactly 20 affected active non-trigger colliders
on this layer: ten Bear and ten Usec spawn boxes, no mesh floors.

Commit `c95bdca` excludes the collider owner's layer 13 during baking, preserving
solid children of trigger-layer parents and the existing frame/door rules. Two
focused Python tests passed: the actual C# source-filter method passed 48 cases,
and removing the layer-13 exclusion failed the expected assertion. The subsequent
Unity build (`unity_build_all_20260921_074006.log`) was still an integration
candidate at this checkpoint; these offline results do not certify its new NavMesh.

The original north exit at (91.2427, -2.9252, 65.142) demonstrated why the authored
point probe alone is insufficient. At 07:29:39.564, the runtime's six-metre NavMesh
search placed the trigger at (91.2, 2.6, 65.1), **5.6 m above the authored point**.
The floor-level slot was also narrow between props. Root inspected the clear loading
door at (96, -2.9252, 61); a native placement probe at 07:33:37.054 passed there.
Commit `30c3e3d` relocates the north exit to that door, and `fce614a` makes exfil
placement try the nearby floor before the legacy wide search. Neither the new north
trigger nor the revised sampling was loaded in this first pass.

**The south exit completed an ordinary eight-second extraction.** UiBridge entered
`Bay5 South Container Lane` at 07:33:37.088; the post-extraction save began at
07:33:45.108 (8.020 s later), followed by `RaidEnded` at 07:33:46.238 and Scripts
scene unload at 07:33:49.507. This was an ordinary raid, separate from the protected
ArenaMode comparisons below.

### Native ArenaMode admission and lifecycle

The native map selector displayed Bay5 and Equator separately for Deathmatch and
Gun Game. Tactical and Team Deathmatch displayed neither optional map. This pass
did not exercise PitFireTeam natively; its rejection remains covered offline.

| Mode | Observed lifecycle |
|---|---|
| Deathmatch | Armed 07:38:05.838; session active 07:38:22.785; round 1 began 07:38:24.006. A real combat death at 07:39:08.405 completed its respawn in 0.63 s at 07:39:09.039. The replenishment plan at 07:39:23.899 found six preferred and three distant candidates; two pmcUSEC arrivals were dispatched by 07:39:23.947. Native `Stop(Left)` committed at 07:40:28.588, followed by the observed return to MainMenu. |
| Gun Game | Match started 07:42:11.460 with a 24-rung ladder. Two-pmcUSEC and two-pmcBEAR replenishment bursts dispatched at 07:43:11.839 and 07:43:13.824. A real combat death at 07:43:42.363 completed its respawn in 0.62 s at 07:43:42.981. Native `Stop(Left)` committed at 07:44:06.257; raid end at 07:44:07.393 reported tier 1/24 and zero completed ladders, followed by the observed return to MainMenu. |

Bot status logs show living PMCs in SAIN's combat layer moving through the yard.
The existing placement repair moved the east-lane and north-east-lane bot markers
1.5 m during setup; the runtime logs retain their adjusted positions. Gun Game was
a start/replenishment/death/return smoke test, not a kill-driven tier-advance or
completed-ladder test. These short runs are not long-session or performance proof.

### Protected profile return

A fresh baseline was captured after the ordinary extraction. Independent comparison
re-read the complete captured profiles for the baseline, after Deathmatch and after
the final Gun Game persistence flush. The capture helper removes only
`/characters/pmc/Hideout/sptUpdateLastRunTimestamp`, then serializes sorted compact
UTF-8 JSON. All three final snapshots are identical and have SHA-256:

`eda7e20adae0fe7a7a62ae3102e0f5cfbb3b0168ed0f89002fe68660855ac3a7`

The retained normalized snapshots are `arena-before.profile.json`,
`arena-after-dm.profile.json` and `arena-after-gg-flushed.profile.json` in the evidence
directory. The intermediate `arena-after-gg.profile.json` differed only in
`PointsEarnedDuringSession` for StressResistance, Perception and Charisma, temporarily
reset to zero; it was not accepted as the final restored profile. The flushed snapshot
matches the baseline without excluding those skill fields or weakening the comparison.

### Required follow-up after the new build

Server and game were closed after this first pass. A restarted process must load the
new Bay5 bundle, then recheck the formerly unreachable depot point, connected spawn
coverage and the relocated north exit through an actual countdown/extraction. The
nearby-floor sampling and new donor rain-cover bootstrap also require their own
loaded-client checks. In particular, two Equator raids must classify indoor coverage
automatically without an F8 diagnostic render. Those outcomes remain pending here;
the first Bay5 ArenaMode lifecycle and south extraction must not be presented as
final acceptance of these subsequent fixes.

## 2026-09-21 08:11–08:32 CEST — final MapPort repeat passed

The follow-up required above is complete. The restarted SPT 4.1.6 / EFT
0.16.9.5-40743 client, UiBridge instance `c2a37398` (started 08:11:03), loaded
MapPort code `d896214` and the Bay5 bundle built from `c95bdca`. Both the initial
and final identity checks reported all 25 maschine plugins and Redeploy Core
identical across VM build, host disk and loaded process. ArenaMode remained
`75a7430`; its prior DM/GG lifecycle/profile-return acceptance is unchanged.

The exact artifact receipt is
`D:/.unlazy/bay5-reflections/mapport-accepted-candidate-artifacts.json`.
Its filename denotes the staged candidate; the native evidence recorded here
establishes acceptance of those bytes. MapPort client SHA-256:
`9957897262AA1F5C0AE9E2BFFB2DA6B1FB80B1EE54F8DD711DDBCE63FFF92D34`.
Bay5 NavMesh SHA-256:
`5BA00920DFA3A5E9E5D33E05A3A08CD3B9B9959C31C83A87D2EE5FDCC5C00DEB`.

### Corrected navigation and both ordinary exits

At **08:27:48.122**, the runtime installed the new Bay5 NavMesh with **14,754
vertices / 6,816 triangles**. The zone reported ten on-NavMesh bot markers in one
connected component, two patrol ways and 96 subpoints, with no off-mesh marker
or isolated single-point component.

From the central yard (89.159, -3.693, 35.203), all 21 saved batch probes passed
NavMesh, zone, reachable-on-foot and standing-room checks: four player starts,
ten bot points, both exits, the former ghost-box point (52.7, -3.8, -6.3), two
warehouse controls and both sides of the northern depot opening. The warehouse
control requested at (143, -2.9, 57) snapped 0.9 m to (142.1, -2.9, 57) and passed;
the old ghost-box point now snapped 0.0 m and passed. Root additionally checked
the two runtime-adjusted bot positions (119.39, -3.87, 43.10) and
(107.33, -3.84, 65.76) with the same full checks; the flushed log records both
successful dry runs at 08:28:49.783 and 08:28:49.817. The two warehouse leaves and
northern depot opening were active, with the depot's five frame parts retained;
the duplicate eastern depot gate stayed closed.

The north trigger now appeared at (96, -2.9252, 61), the clear loading doorway.
Both exits used the new within-one-metre floor search and logged **0.0 m**
displacement from their authored positions on both final Bay5 loads. Their
configured countdown remained eight seconds. Actual ordinary extraction passed
separately through each trigger:

| Exit / separate ordinary raid | Entered trigger | Post-extraction save | Raid-end notification | Result |
|---|---|---|---|---|
| Bay5 North Loading Bay | 08:28:49.841 | 08:28:57.865 | 08:28:58.921 | ExitStatus, then menu |
| Bay5 South Container Lane | 08:30:54.261 | 08:31:02.286 | 08:31:03.346 | ExitStatus, then menu |

The save requests followed entry by 8.024 s and 8.025 s. The raid-end notifications
include teardown after the eight-second countdown;
the approximately nine-second entry-to-notification interval is not a changed
countdown setting. The final UiBridge identity snapshot confirmed `MainMenu`,
`inRaid=false`, and the same loaded process. Bay5's reflection ceiling remained
disabled at its default 1.00, with zero capped slots reported.

### Equator / Hideout cross-map lifecycle control

Before the final Bay5 raids, the same process completed two ordinary Equator
raids separated by a deliberate Hideout visit. First classification at
08:13:48.311 produced 607 indoor renderers / 1,103 clones; the cap reached 7,022
slots at 08:13:48.977. Hideout loading began around 08:19, remained busy for about
three minutes, then recovered at 08:22:14 without a process kill or restart;
the Hideout screen was confirmed at 08:22:52. The second Equator raid classified
607 / 1,103 automatically at 08:25:40.535 and capped 7,022 slots at
08:25:41.103. Both emitted automatic bootstrap completion, without F8. The
repeat therefore exercised donor reuse across a vanilla Hideout transition,
not just repeated manual roof rendering. Detailed reflection evidence belongs
in `equator-specular-cap.md`.

The structured evidence is
`D:/.unlazy/bay5-reflections/native-c2a37398-evidence.json`; the client was quit
at 08:32 and the complete flushed log is archived alongside it as
`native-c2a37398.log` through 08:32:31. The final
ordinary raids validate the corrected map geometry, exfils and donor lifecycle.
They do not repeat the unchanged ArenaMode DM/GG tests or create a new protected
profile comparison; those remain established by the preceding checkpoint.
Movement used coordinate-assisted probes, not an exhaustive walk-through of
every passage. Gun Game advancement/full ladders, prolonged sessions and frame-time
performance remain outside the results claimed here.
