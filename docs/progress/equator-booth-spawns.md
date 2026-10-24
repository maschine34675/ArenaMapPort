# Equator booth spawns, the closed FCK shutter and Bay5's gates (2026-10-23)

Status: done and host-verified on GPU-2 on 2026-10-24 (commits f3fe11e + c699420; the raids are quoted at the end of this file
and in [door-poses.md](door-poses.md) "2026-10-24 - host verification"). Both NavMesh bundles were rebaked and staged on 2026-10-24. User request (German, 2026-10-23): "habe mir equator nochmal angeschaut, da haben
wir offenbar die spawnpunkte falsch gesetzt, statt im FCK-laden müssten die auch hinter den booth-türen sein. das FCK-tor wieder zu
machen. zu bay5, ich würde alle tore aufmachen, auch wenn ich mir grad nicht sicher bin welche das sind. das timing kann gerne
schneller sein als im original, die hälfte reicht vermutlich."

Decisions taken from it: (1) Equator's player spawns stand on Arena's own TeamFight markers behind the booth doors; (2) the FCK
roller shutter is closed again (a wall, as in Arena); (3) Bay5 opens every gate Arena moves, with Arena's motion; (4) the timing is
half of Arena's (the client key `Spawns/SpawnGateDriveScale`, not part of this data).

## What Arena says

All numbers from `tools/maps/equator.modes.json` (composed from the extracted `Arena_Equator_TeamFight_DesignStuff`) and the package
`work/extract/equator-native-v5`; the door facts from [door-animations-survey.md](door-animations-survey.md).

- Arena's 20 `ScavPMCspawn` markers lie in the two booths and nowhere else: 10 north (x -7.34..6.52, z 44.49..48.59, yaw 180, one
  at 133), 10 south (x -7.22..6.51, z -41.99..-38.37, yaw 0, one at 38). The two `SPAWN_BOUND_FIGHTER` boxes enclose them: north
  centre (0.05, 2.16, 47.38) size 20.2 x 4.3 x 16.3 (z 39.2..55.5), south centre (0.05, 2.16, -39.95) size 20.2 x 4.3 x 13.4
  (z -46.6..-33.2).
- The booth doors stand at z 40.02 (north) and z -35.05 (south), two assemblies per booth, each opening about 2.6 m between its
  open leaves (north centres x -4.33 and 4.54, south x -4.46 and 4.40; `equator.doors.json`).
- The FCK roller shutter `SBG_Arena_Equator/SOO_LOD0/equator_roller_shutters_02 (9)` (slab x -9.69..-9.61, z 33.92..38.09, collider
  layer 18) has no script, no clip and no open twin. `LEVEL-BORDERS TF/LevelBorder (83)` (x -9.86..-9.51, z 33.76..38.11) wraps it
  exactly, and borders 79..82 close the hall's front at z 33.8..34.5: the FCK hall is outside Arena's play area.
- The port's Usec spawn at (-11.7, 48.6) was a port choice inside that hall ([exfils-stage2.md](exfils-stage2.md), "Spawn
  barriers"), and the shutter was opened for it. The other three spawns were not in the hall either: All, Bear and South_All stood on
  the aprons 1.5 m in FRONT of the booth doors, not behind them. All four move.

## The spawn table (`tools/maps/equator.bots.json`, ids and names unchanged)

| id | sides | old (x, y, z) yaw | new (x, y, z) yaw | Arena marker | behind the door line | mesh (shutter closed) |
|---|---|---|---|---|---|---|
| `arena_equator-player-all` (`SpawnPoint_Player_All`) | All | (-4.5, 0.1, 38.5) 180, apron 1.5 m before the north west door | (-4.897, 0.08, 45.44) 180 | north `ScavPMCspawn (19)` | 5.4 m, in line with the west door | main component, 3.40 m clearance |
| `arena_equator-player-usec` | Usec | (-11.7, 0.1, 48.6) 180, inside the FCK hall | (6.52, 0.08, 48.589) 180 | north `ScavPMCspawn (6)` | 8.6 m, beside the east door | main, 1.71 m |
| `arena_equator-player-bear` | Bear | (5.0, 0.1, -33.5) 0, apron 1.55 m before the south east door | (4.974, 0.08, -39.087) 0 | south `ScavPMCspawn (15)` | 4.0 m, in line with the east door | main, 2.53 m |
| `arena_equator-player-south-all` | All | (-5, 0.1, -33.5) 0, apron | (-4.346, 0.08, -39.087) 0 | south `ScavPMCspawn (12)` | 4.0 m, in line with the west door | main, 3.35 m |

The coordinates equal the markers of `equator.modes.json` (the unit test finds each spawn on a marker within 1 cm, with the marker's
yaw and height). Yaw 180 north and 0 south is Arena's own marker yaw and faces the doors. Radius 5 is kept. Why these four of the
twenty: one per door pair of each booth (a West and an East spawn per booth, so respawns spread over both openings), none within
2.5 m of an exit box (the nearest is Usec, 2.59 m from the North Gate box; the other markers inside or beside the boxes, N7, N8,
N16, N17, S4, S5, are left out), and none at the walls with less than 1.7 m of mesh clearance. Side semantics are unchanged: the Usec
profile can take All or Usec points, Bear takes All or Bear, so each side still sees both ends (`test_player_respawns_offer_both_ends_to_both_pmc_sides`).

`ScavPMCspawn (19)` sits on a large flat NavMesh polygon whose interpolated height is 0.16 (the other three 0.06); the marker's own y
0.08 is the floor.

## Bot spawns

Only `arena_equator-bot-north-hall` stood in the FCK hall; it moves from (-11.5, 0.1, 37.0) to **(-5.5, 0.1, 33.0)** (yaw 180 kept)
on the open field south of the north doors: main component, 3.70 m of clearance, 12.5 m from the nearest player spawn, 11.4 m from
`bot-north-booth-east`, 7 m before the north door line as `south-west` / `south-east` are before the south one. A scan of the main
component between x -9..9 and z 24..37 picked it from the points with 3.5 m or more clearance.

`arena_equator-bot-north-booth-east` (4.5, 0.1, 38.5) stays: it lies on the apron in front of the east north door, inside Arena's
play area (the nearest border, `LevelBorder (56)`, stands at x 9.8), on the main component with 1.63 m of clearance. The task text
named it as a candidate; the data does not support moving it. A bot point 1.5 m before a booth door is inside the 25 m exclusion of a
player in that booth, so it is only used when the north team is absent, as the apron points always were.

The ArenaMode coverage rule still holds (`test_three_distant_bot_points_cover_playable_ground_envelope`: the fewest distant
points over the whole x -25..25 / z -46..50 envelope is 3, at (-12, 9)), no bot point is within 2 m of a player spawn (the closest
pair is 10.3 m), and categories, zone and radii are unchanged. Two older points sit on the mesh border (`south-west` (-9, -28) with 0.00 m, `south-east` (8.7, -28.4) with
0.05 m clearance): host-verified in 2026-09, untouched.

## Exits

`equator.exits.json` is unchanged: North Gate (2.0, 0.1, 46.0) and South Gate (5.0, 0.1, -44.0) lie inside the booths, on the main
component (4.18 m and 1.20 m of clearance). They were 10 m behind the old aprons; now they share the booth with the spawns
(the centres are 5.2 m and 4.9 m from the nearest spawn, the box edges 2.59 m and 2.91 m). Extraction needs 8 s inside the 4 x 4 m box, so no
spawn extracts by standing still; the descriptor's `Exits` comment ("Usec/All spawn north at z 38-49 ... 10 m from the nearest
spawn") is out of date (handoff edit below). Moving the boxes was not part of the decision.

## The FCK hall and the closed shutter

Method: the built `work/build/full/arena_equator_navmesh.bundle` (baked with the shutter's slab excluded, so the doorway is open)
loaded with `navmesh_diff.load_bundle_mesh`; the polygons that touch the slab's strip (x -9.75..-9.55, z 33.85..38.15) are removed
(3 polygons) and the components of the rest are recounted over the polygon adjacency. This models the rebake with the slab as a
wall; the real bake decides the final outline.

| | main ground floor | FCK hall |
|---|---|---|
| shutter open (the built bundle) | 949 polygons, 3,034 m2, x -35..30.7, z -47.5..53.9 | part of the main component |
| shutter closed (model) | 853 polygons, 2,784 m2 | 92 polygons, 247 m2, x -35..-10, z 34.6..49.6, a separate component |

The old Usec spawn and `bot-north-hall` were both in the hall component. After the change all 14 spawn points (4 player, 10 bot) and both exits lie
on the main component and none in the hall (`final_check` of the session, quoted in the evidence below; unit test
`test_nothing_stands_in_the_fck_hall` encodes the hall as x < -9.6 and z > 33.9).

## The barrier lists

**Equator: `opened_barriers: []`** in `tools/maps/equator.json` and no `OpenedBarriers` argument in `ArenaMapRegistry.Equator` (a
comment says why). The shutter is not listed, so the bake keeps it as a wall and the client leaves it standing. The booth doors are
not barriers: `BoothDoorRepairs` opens them by the names of their leaves, and `equator.doors.json` carries their poses.

**Bay5: five paths, six gates.**

```
WAREHOUSE_DOORS (1)/Bay5_Warehouse_Gate_Door_01 (2)     roller, Arena moves it (new in the list)
WAREHOUSE_DOORS (1)/Bay5_Warehouse_Gate_Door_01 (3)     roller, Arena keeps it shut; borrows (4)'s lift (kept, user)
WAREHOUSE_DOORS (1)/Bay5_Warehouse_Gate_Door_01 (4)     roller, Arena moves it
WAREHOUSE_DOORS (1)/One_man_Door                        roller, Arena moves it (new in the list)
OLD_WAREHOUSE_DOORS/Gates_Depot_Frame                   depot gate, TWO sibling instances (go 821 and 822), listed once
```

Every path resolves in `work/extract/bay5-native-v5`: the four roller paths once each, the depot path twice, at
world (50.574, -3.895, 7.525) and (69.045, -3.883, -6.317), both with their `Telfer_Help` posts (`(2)`, `(3)` on 821, `(1)` and the
unnumbered one on 822), frame, L and R leaf.

Decision on the repeated path: **one entry that means every instance**, not a second string and not a new instance syntax. Reasons:
`MapDescriptorConsistencyTests` and `validate_map_descriptors.py` forbid duplicate strings; the pose table already tells the
instances apart by `at` (`DoorPosesOf(barrier, part, x, y, z)`), and a door-pose test requires every pose barrier to be an
`opened_barriers` entry; and the runtime and bake agents of this change set resolve all instances for such a barrier
(`BoothDoorRepairs.FindAllByPath`, `BakeNavMesh.FindAllInstances` when a row carries `at`). I resolved the opened-barrier
paths of the four packages with both rules (Equator's old shutter path, Bay5's old three, AirPit's 12, Bowl's 2; the two new Bay5
roller paths were found once each by the footprint script): only the depot path has a different count (1 first-match against 2
all-instances), so no other map changes. The rule is recorded in `docs/MAP-RECIPE.md` section 3.

Role of the gates: `StartGates` is empty for Bay5, so every opened barrier is a Start gate (a mode plugin's hold closes all five
paths and the release drives them open); nothing is held. A barrier that has pose rows keeps its parts without a pose standing:
for the depot gates that is the four `Telfer_Help` posts and the frame meshes, which the old rule hid.

### Bay5 spawns against the parked parts

The open-pose footprints (colliders of each posed node moved by the `bay5.doors.json` rows; the survey's depot numbers reproduce to
0.1 m, which also checks the Euler convention used here):

| part (world) | x | y | z |
|---|---|---|---|
| depot 821 L leaf (53, 7.5) | 52.93..55.26 | -3.98..0.39 | 7.59..8.56 |
| depot 821 R leaf | 46.28..48.23 | -3.98..0.39 | 7.60..9.30 |
| depot 822 L leaf (69, -6.3) | 69.11..70.92 | -3.97..0.43 | -10.49..-8.61 |
| depot 822 R leaf | 69.12..71.60 | -3.97..0.43 | -4.05..-3.58 |
| roller (2) | 140.81..145.12 | 0.22..3.69 | 39.99..40.02 |
| roller (3) | 129.36..129.40 | 0.18..3.65 | 41.04..45.36 |
| roller (4) | 129.36..129.40 | 0.21..3.68 | 45.99..50.31 |
| One_man_Door | 129.34..129.37 | 0.29..3.77 | 68.34..70.60 |

Nearest spawn per part, xz distance: `player-south-west` (56.4, 1.9) 5.80 m from the 821 L leaf (the only player spawn within 12 m
of any part; its radius 5 does not touch it), `bot-east-lane` 11.5 m from roller (3), `player-north-east` 11.9 m from roller (4),
`bot-south-west-lane` 13.1 m, `bot-west-lane` 16.5 m, `bot-south-lane` 19.1 m; the exits are 27 m and 34 m away. No Bay5 spawn
moves, `bay5.bots.json` and `bay5.exits.json` are unchanged and `ArenaMapPort.Server/db/arena_bay5/base.json` regenerates byte for
byte (the opened barriers are not part of base.json). `test_no_spawn_stands_where_an_opened_gate_part_parks` pins the footprints
(rounded outward to 0.1 m) with 5 m for players and 3 m for bots.

Note for the Bay5 bundle that is built now (`work/build/bay5`, gates (3), (4) and depot 821 opened): its players and bots lie on
the 4,426 m2 playfield component (x 44..146, z -14..71) except `player-north-west`, `bot-east-lane` and the South Container Lane
exit, which the locate call misses by a mesh border (0.03, 0.09 and 0.03 m of clearance; nothing of this change moves them). Opening
more gates only adds mesh; the rebake decides.

## base.json

`python tools/server/make_base_json.py --map tools/maps/equator.json` (and `--map tools/maps/bay5.json`) with the template
`D:/SPT41/SPT_Runtime/SPT_Data/database/locations/factory4_day/base.json`. The regeneration of the unchanged sidecars reproduced the
committed files byte for byte before the edit (baseline `cmp`). After the edit:

- `arena_equator/base.json`: 5 changed `SpawnPointParams` positions, nothing else: the four player points (x/y/z as in the table;
  `Rotation`, `Sides`, `Infiltration`, `Id`, radius unchanged) and `arena_equator-bot-north-hall` (-11.5, 0.1, 37 to -5.5, 0.1, 33.0).
  `git diff` shows 14 insertions and 14 deletions, all `x`/`y`/`z` lines.
- `arena_bay5/base.json`: no change.

## Tests and checks

- `python -m unittest discover -s tools/tests -p "test_equator_arena_spawns.py"`: 11 tests, OK (7 new: markers, yaw and booth bounds,
  facing, two openings per booth, exit boxes, no point in the FCK hall, `north-hall` moved, empty `opened_barriers`).
- `... -p "test_bay5_arena_spawns.py"`: 5 tests, OK (2 new: the five paths equal the pose barriers, the parked-footprint distances).
- `... -p "test_airpit_bake_inputs.py"`: the Equator/Bay5 pin now reads `[]` and the five Bay5 paths.
- `python tools/validate/validate_map_descriptors.py`: `OK - 14 tools/maps descriptors, all 14 catalog registry entries, release All
  membership, and ArenaPaths.cs's EquatorDefault() all agree.` (it includes the pose-barrier check that was red before the Bay5 list).
- `dotnet test ArenaMapPort.Server.Tests/ArenaMapPort.Server.Tests.csproj -c Release`: `Bestanden! Fehler: 0, erfolgreich: 2215,
  übersprungen: 0, gesamt: 2215` (`MapDescriptorConsistencyTests.OpenedBarriers_WalkedMapsNamePaths_OtherMapsNone` updated).
- Full Python suite: `Ran 2027 tests ... FAILED (failures=1, skipped=55)`; the one failure is outside this data
  (`test_lamp_fill.ValidatorTests.test_a_sidecar_lamp_fill_without_a_registry_literal_is_reported`: it runs the validator with
  `Path("bay5.json")`, so `bay5.doors.json` is looked up in the working directory; see the handoff edit below).

  Fix round: the test file is untouched (not in this change set's file list); the exact two-line edit (both `Path("bay5.json")`
  arguments of that test become `REPO / "tools" / "maps" / "bay5.json"`) was run on a scratch copy: `Ran 20 tests ... OK`. The
  validator is right (a doors sidecar is looked up next to the descriptor it checks); the test passed a bare relative path.

Session scripts (scratch, not in the repo): `nav_probe.py`, `candidates.py`, `final_check.py`, `bay5_footprints.py`, `paths.py` under
the session scratchpad `doors2\mapdata\`.

## Deploy order (Equator)

The new `arena_equator/base.json` and the new client belong together, and a half-deployed pair is the one state that can trap a
player. The spawn table is read by the SPT server at start-up; the shutter is a standing wall from the client descriptor (and, once
rebuilt, from the NavMesh bundle) on:

| server database | client | result |
|---|---|---|
| new `base.json` | old | spawns behind the booth doors, the old client still opens the shutter: playable |
| new | new, old NavMesh bundle | playable; bots may still path to the closed shutter (the old mesh has the doorway) until the bundle is live |
| **old (server not restarted)** | **new** | the Usec point (-11.7, 0.1, 48.6) and `bot-north-hall` (-11.5, 0.1, 37) sit in the FCK hall, which the new client seals: **the player is walled in** |

So: close EFT, dry-run `tools/deploy-to-spt.ps1 -SptRoot 'D:\SPT41'`, run it with `-Confirm`, compare the server DLL and
`arena_equator/base.json` hashes on the host, **restart the SPT server** (close its window, double-click the desktop shortcut;
Syncthing only replaces the server DLL once the process is closed) and then EFT. Check on the first Equator raid with
`uibridge_player`: the spawn position must be one of the four markers (-4.897, 0.08, 45.44), (6.52, 0.08, 48.589),
(4.974, 0.08, -39.087), (-4.346, 0.08, -39.087). A position of (-11.7, 48.6) means the server still serves the old database.
Bay5 has no spawn-related ordering: its `base.json` did not change and no spawn moved (a client deployed before the rebuilt Bay5
NavMesh bundle only means bots still treat the newly opened gates as walls and the depot posts as air).

## Open

1. Rebuild both NavMesh bundles (Equator without `-FullMap`, Bay5 with it; Equator's bake then has the shutter as a wall, Bay5's
   has five open gates with the depot posts as walls) and the client; until then the deployed Equator mesh still has the doorway
   and a bot could path to the closed shutter.
2. Host look (GPU-2), after the rebuild: a raid on each spawn (All, Usec on the north side; Bear, South_All south) starts behind the
   closed booth doors and the player walks out through the opened doors; the FCK shutter stays shut; the F10 inspector on the
   shutter hits the slab. On Bay5: the four rollers lift (`localPosition.y` 3.213 / 3.222 / 3.181 / 3.323), both depot gates swing,
   `Telfer_Help` stays standing, the south-west spawn is not touched by the 821 L leaf.
3. Not verified: whether Arena's booth spawns were meant to be released one pair at a time; whether the exits should leave the
   booths (they now share them with the spawns).
4. If the DynamicMaps pack of Equator is exported again, `tools/dynamicmaps/export_equator.py` hides `opened_barriers`; the FCK
   shutter is no longer hidden there.

## 2026-10-24 - host verification on GPU-2, and the one defect it found

The first raid of build f3fe11e put the player at (3.65, 0.09, 55.52): inside the mall's revolving entrance door, cut off from the
floor. `SpawnPlacementRepairs` (2026-09-14, "moves our player spawn points out of Arena's own spawn enclosures") collected the bounds
of every solid collider named like the booths (the `Equator_Respawn_NN` walls and the `Door_Sliding_Respawn` leaves, 45 colliders on
Equator) and moved every spawn whose feet stood inside one of them to the nearest free spot outside: Usec 7.5 m north into the
revolving door, All 6.0 m west into the FCK hall behind the shut shutter, Bear and South_All 6.0 m north out of the south booth
(`[RaidFix] spawn placement: ... 45 respawn-booth collider(s) avoided; ...`). The rule predates `BoothDoorRepairs` opening the
booth doors (2026-09-20) and had never fired before, because no spawn stood inside a booth. Commit c699420 removes it (the
SPAWN_BOUND disabling and the capsule, floor and spacing checks stay; a wiring test pins the removal). Open item 1 of the list
above (the rebuild) is done; item 2 reads as follows after the second raid (the quotes are in door-poses.md):

- Plain raid: `spawn placement: disabled 2 SPAWN_BOUND barrier collider(s); 'arena_equator-bot-south-west' ... (1.5 m)` and no player
  spawn moved; `uibridge_player` (6.5, 0.1, 48.6) yaw 180 = marker 6 (Usec), inside the north booth facing the parked-open door;
  the walk to the atrium went through the door (29.3 m away, 33.3 m along the navmesh); the FCK shutter is a wall (still, and a walk
  toward (-12, 36) ends 2.6 m short on the corridor side); the hall is unreachable (`dryRun` to (-11.7, 0.1, 48.6) refused).
- Developer hold: the player starts behind the CLOSED leaves (still `equator-booth-held-closed`), the booth is sealed for bots (10
  carvers, every probed doorway PathPartial, `dryRun` out refused), and the release drives all ten leaves to x +-2.0 in 3.5 s at the
  default scale (`drove 10 part(s) ... (scale 0.50)`, `drive frames ... max 8.3 ms`), after which every probed doorway is PathComplete.
- Only the Usec and the north All marker were started from (the profile is USEC; EFT picks among the side's points). The Bear and
  South_All points stand on the same kind of marker inside the south booth and were not started from in person.
- Item 3 stays open (one pair at a time; the exits in the booths), item 4 too (DynamicMaps export).
