# Door animations survey: what Arena moves on Equator, Bay5 and AirPit, and what the port can replay (2026-10-23)

Status: research record, no code changed. Sources: four read-only investigations of Arena 0.6.1.0.48206 (UnityPy over the level
and sharedassets files of the three maps, the EFT 0.16.9.5 decompile and EFT's own data files) and the repository at main
(4d14536). Names, class names and numbers only; nothing of Arena's text or assets is stored here. The question (user, 2026-10-23,
German): recreate the door animations on the other maps too, "and what about all the roller shutters, can we do those as well?"
This record corrects "Other maps" and "Open" in [door-poses.md](door-poses.md) and one paragraph of [spawn-gates.md](spawn-gates.md)
(section "Corrections to earlier records" at the end).

## Short answer

- Arena moves the gates of Equator, Bay5 and AirPit with its own tween script, not with Mecanim: the Arena MonoBehaviour
  `CommonAssets.Scripts.Game.Modes.Data.DotAnimator` (Assembly-CSharp, built on DOTween, which EFT also ships as `Managed/DOTween.dll`).
  It stores the END value, the duration and the ease of one move or rotation per target. The result is the same kind of data as
  Bowl's clip end poses (Bowl itself has no `DotAnimator`: its level 48 holds 2 `GateEntity`, 4 `EntityAnimator` and 4 `Animator`
  with clips, and no Arena level carries a `DotAnimator` next to an `Animator`, so no clip can validate the script's semantics; the
  duration and ease words are plausible and self-consistent, not proven), so one pose table serves all four maps; there is no clip to sample (approaches A and A2 do not exist here) and EFT ships
  no clip for any of these prefabs (approach C does not exist).
- Authored and portable now: Equator's 8 booth-door leaves (slide to local x +2.0 / -2.0 in 5 s, one leaf 7 s, InOutQuint), Bay5's
  three warehouse roller gates (lift 3.09 to 3.17 m in 4 s, linear) and its depot gate leaves (rotate to 98.97 to 162.43 degrees in
  7 s along a stored curve), AirPit's 16 passport leaves (turn to euler (-90, +-90, 0) in 1.7 s, InOutQuint).
- Partly authored: AirPit's bus doors have an animator without clips, but the three back-row assemblies stand in the open pose, so
  the end pose is known and the motion between is not. Arena does intend them to open: `Arena_Airpit_Sound` (level 39) carries
  `EntitySoundPlayer` objects for exactly the three front-row assemblies, yet the whole install holds no bus or apron clip, no
  override controller and no bundle for it (the skeptic's scan of 779 serialized files and the StreamingAssets manifest).
- Roller shutters: Bay5's are exact. AirPit's shop shutters have an authored OPEN twin (a different mesh with the rolled-up header)
  that the port already shows; Arena has no motion for them. Equator's FCK shutter has nothing at all (see "Roller shutters").
- Not authored anywhere: the closing motion, the trigger, AirPit's fence halves (purpose unknown) and the Equator FCK shutter.

## Method and what was verified how

Each level of the three maps was read with UnityPy: transforms, Animators, controllers, clips, and the raw bytes of every
MonoBehaviour whose MonoScript names a gate class (class names come from the MonoScript table; the IL2CPP `global-metadata.dat`
is encrypted, so no field names exist and every field below is named by position and checked by value). A decode counts only
when the offset after the last field equals the object's byte length. This synthesis re-ran the Equator booth table
(`equator/booth_table.py`: same numbers as below) and re-read the Bay5 decode (`bay5/bay5_level88_dotanimators.json`: 11 objects)
and the sidecars `tools/maps/{equator,bay5,airpit}.json`; everything else is the investigators' evidence. Two investigator
reports reached the synthesis cut off (Bay5 after "Barrier C", AirPit after the shop table); the gaps were filled from their
scratch outputs where the evidence exists there and are marked "not in hand" where it does not.

Level numbers (Arena 0.6.1): Equator 51 Arena_Equator, 53 DesignStuff, 54 Scripts, 55 Lighting, 57 TeamFight_DesignStuff,
58 TeamFight (60/61 are the Tournament copy, not part of the map). Bay5 79 to 90, the gate scene is 88; AirPit 28 is the gate
scene (`Arena_Airpit_TeamFight_DesignStuff`), 38 Main_hall, 324 ShootOut. `GateEntity` exists only in the
TeamFight_DesignStuff level of each map's own scenes (Equator 57, Bay5 88, AirPit 28, Bowl 48) and `DotAnimator` in those of
Equator, Bay5 and AirPit (Bowl's level 48 has none; its gates are Mecanim); Equator's Tournament (61) and AutoService (70) levels and Bay5's ShootOut levels (92, 94) carry copies or other placements.

## Findings

### F1. The mechanism: GateEntity names the gate, DotAnimator moves it

- `GateEntity` (44 bytes: 32 head + int32 id, float32, int32 1) sits on the group root and holds NO motion data and no PPtr. Ids share
  one number space with `CapturePointEntity`: Bowl 16001/16002; Equator 24002 to 24005 (the four booth doors) and 24006
  (`EXIT_LIGHT_EMISSIVE`); Bay5 7001 (`WAREHOUSE_DOORS (1)`) and 7002 (`OLD_WAREHOUSE_DOORS`); AirPit 3001 (`PASSPORT_CONTROL_DOORS`),
  3004 (`BUS_DOORS`), 3005 (`BUS_LIGHT`), 3006 (`PASSPORT_CONTROL_EMISSIVE`). Its float is -2.0 on the four Equator doors and 0.0
  everywhere else (meaning unknown). Nothing in the scenes references it or the DotAnimators by PPtr: the open command comes from the
  game mode, not from the scene.
- `DotAnimator` sits on the node that moves (the leaf, the slab, the passport flap). Layout of its tail (all values confirmed on
  the 82 elements of levels 57, 61, 28, 88 and 70, and on 268 of the 274 `DotAnimator` objects of the whole install; the 6 misfits
  are all in level 321 `Arena_Tutorial`, with 162 or 215 unread bytes and kinds other than 1, 2 and 3, none on a ported map;
  word = int32 counted from the element start):

| Part | Position | Content | Confidence |
|---|---|---|---|
| Header | 5 words | 0, 1, 0, flag, element count | high |
| Element | 212 bytes (53 words) with an empty curve, plus 28 bytes per curve key | one entry per target | high (offset == length on every object) |
| word 0 to 2 | PPtr | the element's own GameObject (fileID 0) | high |
| word 3 | float | -1.0 on the depot gates, -2.0 on Bay5 rollers, 0.0 on Equator and AirPit passports | meaning unknown |
| word 4 | float | duration in seconds | medium-high |
| word 5 | int | kind: 1 = move to a LOCAL position, 2 = rotate to a LOCAL euler, 3 = switch (SetActive with a flag in the payload) or emissive colour | high for 1 and 2 |
| word 7 | int | DOTween Ease number (values read from EFT's `DOTween.dll`: 1 Linear, 16 InOutQuint, 17 InExpo, 37 INTERNAL_Custom = use the stored curve) | high |
| word 8 to 10 | ints | 0, 1, -1 on doors (look like loop or rotate-mode settings) | unknown |
| word 11 | int | curve key count n; then n x 7 words (time, value, inSlope, outSlope, weightedMode, inWeight, outWeight), then 2, 2, 4 | high |
| payload | 38 words | word 0 = 0, words 1 to 3 = the TARGET vector, the rest defaults (colour 1,1,1,1; scale 1,1,1) | high |

- The targets are ABSOLUTE local values in the parent's frame, not offsets. Evidence: Bay5's roller targets keep the node's current
  local x and z to four decimals and change only y; Equator's +-2.0 as an offset would push each leaf 0.58 to 0.59 m out of its
  frame (leaf outer edge 0.125 to 0.128 m inside the frame when absolute, 0.578 to 0.594 m outside when relative; re-computed in
  this synthesis); rotation targets differ per leaf from different closed angles (depot leaves 0 to 138.84, 3.8 to 162.43).
- Not in the data: the trigger, the closing motion (reverse play or a second tween) and what the unnamed words 8 to 10 mean.

### F2. What does not exist (so approaches A, A2 and C are closed for these maps)

- No Mecanim clip, AnimatorController or legacy `Animation` below any opened barrier of the three maps. Equator levels 51 to 61
  hold only the 99 `ArenaFan` spectators as Animators. Bay5 levels 78 to 90: only the 88 spectators plus one Animator each in the
  ShootOut scenes 92 and 94. AirPit: the three `BUS_DOORS` Animators use the controller `Apron_bus_MAZ_doors` (sharedassets28 path
  100): two states (InitialState speed 1.0, Open speed 0.8), triggers `play` and `reset`, no clips (a stub), plus an `EntityAnimator`
  (36 bytes, zero tail) that probably fires the triggers.
- Door-like Mecanim clips exist only for gates that are not in these maps' TeamFight scenes: Equator's AutoService scene
  (sharedassets70: roller shutters `_01`, techlight, big metal gate), Bay5's ShootOut gate `Gates_Respawn_01_Bay5` (sharedassets92,
  Opened and Closed clips), AirPit's `Arena_Airpit_new` SlidingDoors (sharedassets208, not decoded).
- EFT ships nothing to borrow. 153 AnimationClips and 29 AnimatorControllers in all of EFT's global, resources and 559
  sharedassets files; the door-like ones are the Labs plastic doors and Labyrinth props only. EFT's `StreamingAssets/Windows` lists
  6,570 bundles (items, weapons, characters, audio, hideout, grass): no gate or door bundle. EFT contains some of Arena's prefabs as
  STATIC geometry (`Gates_Army`, `Gates_PTOR_medium` with `_Door_L/_Door_R`, `Telfer_Help`, the `Rollete_Gate_Opened/Closed` and
  `roller_shutters_01/02` family, `Chalet_Roll_Door_open/close`); `Rollete_Gate_Door_01` and `Rollete_Gate_Frame_01` (the split
  into slab and frame) occur only in Arena. Not found in EFT at all: `Gates_Depot`, `Bay5_Warehouse_Gate_Door_01`, `One_man_Door`,
  `TH_Roll_door`, `Airport_Shop_BS`, `DonB_Ramen`, `Apron_bus_MAZ`, `Passport_control_01`, `Door_Sliding_Respawn`,
  `Prison_Metal_Fence`.
- EFT's own door family (`EFT.Interactive.Door`, `SlidingDoor`, `WorldInteractiveObject`) is not Animator-driven (angle or position
  lerp on a curve). `Door` writes `transform.rotation` and would overwrite an authored pose; reusing it on Arena leaves buys
  nothing. The one generic EFT pose lerper is `ExfiltrationDoor` (DoorTransform[] with Open/Close pose and an AnimationCurve), tied
  to an exfil status. The client's own pose driver (`SpawnGates` / `DoorPoseModel`, Bowl) is the better base.

### F3. Equator (levels 51 and 57)

- Booth doors. The four animated doors are the assemblies under `SPAWN_DOORS` in level 57 (not under `Respawn_1`/`Respawn_2` as the
  task brief assumed; spawn-gates.md already says so). Each assembly: root with LODGroup and `GateEntity`, frame meshes, COLLIDER
  (layer 18), BALLISTIC (layer 12), inactive glass; leaves `_L` and `_R` each with a `DotAnimator` (kind 1, ease 16). Sound is not on
  the door: level 56 `ARENA_GATES_SOUND/Door_Sliding_Respawn*` (`Audio.Arena.EntitySoundPlayer`), clip `arena_equator_old_slide_doors`,
  3.569 s. A fifth door, `Door_Sliding_Respawn (3)` under `Respawn_2` in level 51, has the same structure with no script.
- Today's port: slide 1.38 m along the leaf's local x (inferred from geometry), 1.0 s default, then the parts switch off
  (`SpawnGateModel.SlideDistance`, `Spawns/SpawnGateSlideSeconds`). Authored travel is 1.278 to 1.294 m: the estimate overshoots by
  0.09 to 0.10 m and is 4 to 6 s too fast compared with Arena (the speed is a port choice for ArenaMode).
- FCK roller shutter `SBG_Arena_Equator/SOO_LOD0/equator_roller_shutters_02 (9)` (level 51 GameObject 12249, world (-9.653, -0.030,
  36.001)): Transform and LODGroup only; 12 children (slab LOD0 to LOD2, COLLIDER layer 18, inactive SHADOW_LOD0, BALLISTIC layer 12,
  and the same set for the frame). No script, no twin, no `_open` object. Nothing references it except culling and ballistic lists.
  All 20 shutter assemblies of level 51 (6 `_01`, 13 `_02`, 1 `_adik`; plus six `roller_shutters_techlight_01..06` roots) look
  the same, and level 60 repeats them.
- Why Arena never opens it: `LEVEL-BORDERS TF/LevelBorder (83)` (level 57, layer 29, world AABB x -9.86..-9.51, y -0.35..5.38,
  z 33.76..38.11) wraps the shutter slab exactly (slab x -9.693..-9.613, z 33.917..38.085), and all 20 Arena spawn markers lie in
  the two booths (z 44 to 49 and -42 to -38). The FCK hall is outside Arena's play area; the Usec spawn there is a port choice.

### F4. Bay5 (level 88; the warehouse gate slabs' meshes come from sharedassets86)

- All 11 DotAnimators of level 88 (decode in `bay5/bay5_level88_dotanimators.json`):

| Node (path below the scene root) | go | kind | ease | seconds | closed local | open target (local) |
|---|---|---|---|---|---|---|
| `WAREHOUSE_DOORS (1)/Bay5_Warehouse_Gate_Door_01 (4)` | 504 | 1 move | 1 Linear | 4.0 | (-6.2269, 0.1240, -21.5615) | y 3.2130 (+3.089 m) |
| `WAREHOUSE_DOORS (1)/Bay5_Warehouse_Gate_Door_01 (2)` | 503 | 1 | 1 | 4.0 | (9.8217, 0.1300, -27.7051), euler (-90,-90,0) | y 3.2220 (+3.092 m) |
| `WAREHOUSE_DOORS (1)/One_man_Door` | 268 | 1 | 1 | 4.0 | (-6.0763, 0.1500, 0.8585) | y 3.3230 (+3.173 m) |
| `OLD_WAREHOUSE_DOORS/Gates_Depot_Frame/Gates_Depot_Door_R/Gates_Depot_Door_R_Leaf` (gate at world x 50.6, go 847) | 847 | 2 rotate | 37 curve | 7.0 | z euler 0.0 | z -141.759 |
| same, `Gates_Depot_Door_L_Leaf` (go 451) | 451 | 2 | 37 | 7.0 | z 3.8 | z 162.432 |
| same two leaves of the second gate at world x 69.0 (go 846 / 452) | 846 / 452 | 2 | 37 | 7.0 | z -2.042 / 0.0 | z -98.974 / 138.837 |
| inactive `Gates_PTOR_medium (4)` and `(5)` `_Door_R` / `_Door_L` (go 385, 383, 418, 423) | | 2 | 37 | 7.0 | | same targets as the second and the first depot gate |

  The depot curve is three keys on a 0..1 timeline: (0, 0, slope 2), (0.4, about 0.80, slope 0.886 to 0.994), (1, 1, slope 0): 80 %
  of the swing in the first 2.8 s, then a long ease-out. `Door_01 (3)`, the unnumbered `Door_01`, `(1)`, `Door_Top_02/Bottom_02` and
  `Top_03/Bottom_03` carry no DotAnimator.
- Sound (`ARENA_GATES_SOUND` in the current Bay5 sound scenes 188, 225 and 341, the same set as the old level 87) exists exactly
  for `(2)`, `(4)`, `One_man_Door` (clip `arena_bay5_roller_door_full`, 5.18 s)
  and the two depot frames (`arena_bay5_big_gates`, 6.10 s): Arena moves those five and keeps `(3)` shut in TeamFight.
- The port lists `Door_01 (3)`, `Door_01 (4)` and `Gates_Depot_Frame` in `opened_barriers`. `(3)` is the one Arena does not move;
  `(2)` and `One_man_Door`, which Arena opens, are not listed. The depot name exists twice (go 821 at world (50.574, -3.895, 7.525)
  and go 822 at (69.045, -3.883, -6.317)); `Transform.Find` in `BoothDoorRepairs` and `BakeNavMesh` returns the first, so only
  gate 821 is opened (host: "the duplicate eastern depot gate stayed closed", bay5-arenamode-integration.md). Arena animates both.
- `BoothDoorRepairs` keeps parts whose name contains "frame" and hides the rest: the two `Telfer_Help` posts of the opened depot
  gate (go 821; the other two belong to gate 822, which the port does not open) are hidden although Arena leaves them standing; on a roller all 8 direct children go, colliders included.
- Roller geometry: closed slab `Rollete_Gate_Door_01_LOD0` (mesh id baed2ce520ce7655, 2,522 vertices) is 4.32 m wide, 3.47 m high,
  0.104 m thick; ids in sharedassets86: LOD1 d8b9ea4a39497772, LOD2 92d8cd0fddd7a9ff, LOD3 ade451e0e5831311, SHADOW_LOD0
  9fb854dad6c7ef9f, BALLISTIC 8ec1c96d358f4ad4, COLLIDER 052bff09d8dcbdb8. The frame (`Rollete_Gate_Frame_01`, 4.47 x 3.83 m,
  sharedassets80 path 194) stays in level 86 (`SBG_Arena_Bay5_WAREHOUSE/OO/GATES/FOR_ANIMATIONS/Bay5_Warehouse_Gate_Frame_01..(4)`).
  There is no open twin of the slab: the open state IS the translated slab. For `(4)` the open slab spans world y 0.21..3.68, 3.09 m
  of clearance, and rises about 2.8 m above the frame header (y 0.92) along the inside face of the wall (the indoor mesh starts at x 129.35,
  the slab is at x 129.33..129.43). Level 86 also holds static `Rollete_Gate_Open_01/_02` (frame plus a roll-up curl, 914 and 724
  vertices) and `Closed_01` at other openings.
- Depot open footprints (world x, z): gate 821 R leaf x 46.2..48.3 z 7.6..9.3, L leaf x 52.9..55.3 z 7.6..8.6; gate 822 R leaf x
  69.11..71.61 z -4.07..-3.42, L leaf x 69.10..70.93 z -10.60..-8.56. The L leaf carries a wicket door (`Gates_Depot_Door_02_L`, MeshCollider
  layer 18) that moves with it. Both leaves swing toward frame-local -y; on gate 821 that is away from the BEAR pocket.

### F5. AirPit (level 28 for the gates; 324 and 38 for the shops and fences)

- Passport doors `PASSPORT_CONTROL_DOORS` (GateEntity 3001, `held_gates`): 16 leaves `Passport_control_01_Door (N)` (names repeat,
  `(4)` four times), 7 children each. One DotAnimator element each: kind 2, 1.7 s, ease 16, target euler (-90, +90, 0) for row 1 (z 66.284)
  and (-90, -90, 0) for row 2 (z 69.123). The scenes' euler is (270, 179 or 181, 0) or (270, 0, 0): a pure turn of 89 to 91 degrees about
  the vertical axis. Closed collider 1.09 m wide, 1.35 m high; open, the leaves lie along z (row 1 z 66.34..67.43, row 2 z 67.98..69.07)
  and their tips stay 0.55 m apart. The eight row-2 leaves carry an AudioSource plus `EntitySoundPlayer` (clip `arena_airpit_metal_gates`,
  4.0 s; an int32 pair table and 7 floats unnamed). Leaf x origins in both rows: -21.235 (179/181), -16.627 (0), -16.271 (179/181),
  -11.663 (0), -11.308 (179/181), -6.699 (0), -6.344 (179/181), +3.224 (0).
- Passport lamps: `RED` and `GREEN` roots each carry a kind-3 element: `RedFog` (active at load) switches off, `GreenFog` (inactive at
  load) switches on. That is exactly the red X to green arrow flip recorded in spawn-gates.md ("held_gate_indicators", 2026-10-18, not
  started); the data confirms the design. `PASSPORT_CONTROL_EMISSIVE` (GateEntity 3006, 18 red fog sheets) has no animator; what it
  does at open is unknown.
- Bus doors `BUS_DOORS` (GateEntity 3004, `start_gates`): 6 assemblies `Apron_bus_MAZ_doors` and `(1)` to `(5)`, each a LODGroup with the
  hinge arms `..._L_01_part_01` and `..._L_02_part_01` and under them the leaves `..._L_01` and `..._L_02`. Only the front row
  (`Apron_bus_MAZ_doors`, `(1)`, `(2)`, z -65.000) has an Animator (path ids 2201, 2200, 2202) and an `EntityAnimator`; the back row
  (`(3)`, `(4)`, `(5)`, z -68.160, rotated 180 degrees) is static and identical in all three. Closed vs the back row's pose (local):

| Node | Closed | Back-row pose |
|---|---|---|
| hinge `L_01_part_01` | euler 0 | euler z +127.0 |
| hinge `L_02_part_01` | euler 0 | euler z -127.0 |
| leaf `L_01` | pos (0.010, -0.404, -1.780), euler 0 | pos (0.076, -0.328, -1.780), euler (0.24, -0.18, -127.0) |
| leaf `L_02` | pos (0.010, 0.403, -1.780), euler 0 | pos (0.076, 0.328, -1.780), euler (0, 0, +127.0) |

  Hinge positions stay (-0.129, +0.729 / -0.728, 2.152). Applied to the front row each leaf moves 0.65 to 0.66 m apart and 0.2 m
  toward the field; the clear gap between the leaf colliders becomes 1.32 m. The south team spawn markers sit at z -74.6..-71.7,
  behind the back row, which in Arena stands open. The port hides all six assemblies (every non-"frame" child). Duration is not
  authored (state speed 0.8, transition 0.25 s); the proposal file keeps `seconds: null`
  (`airpit/airpit_door_poses_proposal.json`, a PROPOSAL, not an accepted sidecar). The scenery bus (levels 324 and 38) uses the same
  stub controller and holds six inactive door groups `Apron_bus_MAZ_door_L_01..06` / `R_01..06` in a different, folded pose (low
  confidence as an open-state source).
- Bus lights `BUS_LIGHT` (GateEntity 3005): 3 DotAnimators of kind 3 (two point lights and `Exit1..3` on at open) plus three
  `EmissiveAnimator`; cosmetic, not in `opened_barriers`.
- Fences `SBG_Arena_AirPit_ShootOut/SOO/Prison_Metal_Fence_01 (2)` at (-23.499, -0.010, 50.544) and `(3)` at (-0.382, -0.010, 50.544)
  (level 324): LODGroup root, no script, no Animator. The closed fence is one thin plane 2.79 m wide, 2.96 m high (x -24.89..-22.10 for
  `(2)`); `PART_L` and `PART_R` are INACTIVE twins, each with its own LOD, COLLIDER and BALLISTIC: a 0.05 x 0.9 x 1.51 m leaf standing
  perpendicular at each jamb (x -22.63..-22.58 and -24.41..-24.36, z 50.64..51.54, y -0.01..1.50), about 1.7 m clear between them. They are
  half the height of the closed fence, so they are not a swung copy of it, and nothing in any AirPit scene toggles them (the culling
  tables list only the closed fence). Two more fences mid-map (`Prison_Metal_Fence_01` and `(1)`) have the same twins and are not
  in `opened_barriers`. Mesh ids (sharedassets60): closed LOD0 d280a6ac84e25dc1, closed COLLIDER f55cc9ce722a1aeb, PART_L LOD0
  2b9a6c2bbe50137e, COLLIDER 4d05b430d4f4fae1, PART_R LOD0 c9a4fb070d34742c, COLLIDER 775f16ec96b7799f.
- Shop shutters (level 324 `SOO_LOD0/BUILDING/*_closed`: LODGroup with LOD0, SHADOW, COLLIDER, BALLISTIC; no script, no Animator). The OPEN
  counterparts are active, differently built objects in level 38 (`SOO_LOD0/TH_Roll_door_01`, `_02`, `_03`, `DonB_Ramen`, `LP_Shop`,
  `Airport_Shop_BS`, plus `TH_Shop_indoor`) at the same world positions (within 0.02 m), already contained in `airpit-native-v9`. So the port already shows the
  authored open state once the `_closed` shells are off. The open state is a different mesh (a header box with the rolled-up shutter above
  the doorway), not a transform of the closed slab; measured from front-view rasters of the colliders:

| Door | Closed slab (level 324) | Open shell (level 38) |
|---|---|---|
| `TH_Roll_door_01` at (-43.272, -0.010, -43.468) | 10.00 x 2.96 x 0.18 m; LOD0 1b0a1ed73bd2813c, COLLIDER 9c32769d1ecd5869 | one opening 5.94 m wide, clear height 1.82 m; LOD0 04f927ce21775451, COLLIDER fa8f6399194b35c0 |
| `TH_Roll_door_02` at (-36.774, -0.010, -26.813) | 32.76 m long, 2.97 m high; LOD0 f999745d7b3a0427, COLLIDER d5ba3f3f8b765ac6 | openings 5.68 m (clear 2.30), 3.78 m (2.40), 1.39 m (2.40); LOD0 f4ac27ae49c735b2, COLLIDER 8051dd31a5f2db3a |
| `TH_Roll_door_03` at (-44.022, -0.010, -10.073) | 13.56 x 2.96 m; LOD0 a21e8bdb60385c2c, COLLIDER 28fe4c7bf9f4cf52 | one opening 3.86 m (clear 2.39); LOD0 acba01cbe38f06b6, COLLIDER 8e89d7d1f54393be |

  `DonB_Ramen_closed` (and `(1)`), `Airport_Shop_BS_closed_closed` follow the same closed/open pairing (not measured in hand).
  `SBG_Arena_AirPit_ShootOut/SOO/Cube (10) Group` (three 13 x 3.3 x 0.16 m white blockout boxes on layer 0) is no door; it stays hidden.

### F6. Cross-map facts from the same decode

- AirPit-new (level 212) sliding doors: kind 1, 2.0 s, end (0, +-0.993, 0); Bay5 ShootOut `Door_01 (3)` (levels 92/94, inactive object):
  local (-27.026, -0.876, -57.530) to (-26.92, 1.463, -57.53) in 4 s, linear (+2.34 m up): a different placement of a gate named like
  Bay5's `(3)`, usable only as a hint for how Arena lifts a roller.
- Equator's AutoService roller shutters (sharedassets70; an Animator on the root, scale 0.851): clip `1_equator_roller_shutters_01_animation`
  (and its `..._clossed` twin), 4.5 s, the slab nodes (LOD0, LOD1, LOD2, COLLIDER, SHADOW, BALLISTIC) move in local z from 0.981 to
  3.008 (about 2.03 local units; the closed clip has 0.978 on COLLIDER, SHADOW and BALLISTIC), the frame stays, no scale or rotation curve.
  `roller_shutters_techlight_01` lifts 2.713 over 6.733 s. There is no clip for `roller_shutters_02` or for the FCK shutter.

## Per-door table

Legend. Approach: A clip end pose driven like Bowl; A2 sampled clip curves; B toggle authored twin objects; C EFT-shipped clip;
D numbers derived from the script (DotAnimator end value, duration, ease/curve; or the static sibling pose); E nothing authored:
keep the hide, or hand-authored motion. Effort per door once the shared steps P1 to P4 below exist: S a data row plus a few client
lines and tests; M extractor or client change with a host check; L touches the NavMesh bake (a new NavMesh bundle) or invents
content. Risk: low / medium / high with the reason.

| # | Door (path) | What Arena has | What defines the open state | Approach | Effort | Risk |
|---|---|---|---|---|---|---|
| Bowl | `DOORS_SPAWN_A/B` BTR, ambulance, army gate, respawn gate | Animator + clips (reference) | clip end pose, in the port since 2026-10-22 | A (done) | done | host-verified |
| E1 | Equator booth doors, 4 assemblies under `SPAWN_DOORS` (level 57), 8 leaves, GateEntity 24002 to 24005; handled by name in `BoothDoorRepairs`, not in `opened_barriers` | `DotAnimator` per leaf, kind 1; no Animator | local x -> +2.0 (L) / -2.0 (R), travel 1.278 to 1.294 m; 5.0 s (R leaf of 24002: 7.0 s); ease 16 InOutQuint | D | S | low: absolute reading fixed by geometry; closing, trigger and 7 s leaf unexplained; Arena's 5 to 7 s vs today's 1 s is a gameplay choice |
| E2 | `Door_Sliding_Respawn (3)` under `Respawn_2` (level 51), world (-3.753, 0.048, -45.870) | same prefab, no script, leaf pivots local +-0.683 | nothing authored; borrow the siblings' absolute +-2.0 (travel 1.317 m) or keep the 1.38 m slide | D (borrowed) | S | low: door (3) of the south booth is disconnected on the map's own NavMesh anyway |
| E3 | `equator_roller_shutters_02 (9)` (FCK), the only Equator `opened_barriers` entry | Transform + LODGroup only; no script, clip or twin; walled off by `LevelBorder (83)` in Arena | nothing | E | S (keep frame-only) / M (hand-authored lift) | low for play; not Arena if animated; what is above the opening is unchecked |
| E4 | the other 19 shutter assemblies (6 `_01`, 12 further `_02`, 1 `_adik`; and the 6 techlight roots) | static decoration | n/a, not barriers | n/a | none | none |
| E5 | `EXIT_LIGHT_EMISSIVE` (GateEntity 24006) | `DotAnimator` 688 B + `EmissiveAnimator` | cosmetic lamp, not decoded | D (later) | M | low; skip |
| B1 | Bay5 `Door_01 (4)` (go 504, world (129.150, -2.878, 45.916)), listed | `DotAnimator` kind 1, ease 1 | local y 0.124 -> 3.213 (+3.089 m), 4.0 s linear | D | S (pilot) | medium: open slab overlaps the wall plane and rises 2.8 m above the frame (host look); park-carver rule for a lifted collider |
| B2 | Bay5 `Door_01 (3)` (go 505, world (129.150, -2.910, 40.966)), listed | static in TeamFight (no DotAnimator, no sound) | Arena keeps it shut | E | decision | the port opens it for bot access; its ShootOut namesake lifts +2.34 m in 4 s linear (other placement) |
| B3 | Bay5 `Door_01 (2)` (go 503, (145.199, -2.872, 39.772)); `One_man_Door` (go 268, (129.301, -2.852, 68.336)); NOT listed | `DotAnimator` kind 1, ease 1 | y 0.130 -> 3.222 (+3.092 m); y 0.150 -> 3.323 (+3.173 m); 4.0 s | D | L (adding them to `opened_barriers` changes the bake) | medium: new routes for bots |
| B4 | Bay5 depot gate, first instance (go 821, world (50.574, -3.895, 7.525)), listed (the one that opens) | leaves with `DotAnimator` kind 2, ease 37 curve; wicket on the L leaf | R leaf euler z -> -141.759, L leaf z 3.8 -> 162.432; 7.0 s curve (80 % at 2.8 s); sound 6.10 s | D | M | medium: leaves up to 162 degrees; open footprints outside the BEAR pocket need park carvers; duplicate-name addressing (P2) |
| B5 | Bay5 depot gate, second instance (go 822, (69.045, -3.883, -6.317)), closed today | same, targets -98.974 / 138.837 | as B4 | D | L (bake and resolvers must pick it) | medium |
| B6 | Bay5 `Gates_PTOR_medium (4)` / `(5)` (inactive variant twins under `OLD_WAREHOUSE_DOORS`) | same DotAnimator targets as 822 / 821 | n/a | none | none | none |
| B7 | Bay5 `Telfer_Help`, `(1)` to `(3)` posts of the depot frame | static solids Arena leaves standing | stay standing | B (stop hiding) | S | medium: the bake excluded the whole barrier, so standing posts are unmeshed solids (carvers or a rebake) |
| A1 | AirPit `BUS_DOORS` front row `Apron_bus_MAZ_doors`, `(1)`, `(2)` (z -65.000), `start_gates` | Animator + stub controller (`play`, `reset`, no clip) + `EntityAnimator` | open pose = the static back-row pose: hinge euler z +-127, leaf local position shifted by about 0.07 m (table in F5); no duration | D (sibling pose) | M | medium: path between closed and open unknown; seconds are ours |
| A2 | AirPit `BUS_DOORS` back row `(3)`, `(4)`, `(5)` (z -68.160) | static, already open | stay as they are | B (stop hiding) | S | medium: standing open leaves in the south team's walkway; 1.32 m gaps; bake excluded them |
| A3 | AirPit `PASSPORT_CONTROL_DOORS`, 16 leaves, `held_gates` | `DotAnimator` kind 2, ease 16 | euler (-90, +90, 0) row 1, (-90, -90, 0) row 2, 1.7 s; sound 4.0 s | D | M | medium: names repeat (P2); euler -90 vs scene 270 needs a Slerp or an unwrapped branch (P3) |
| A4 | AirPit `RED/RedFog` off, `GREEN/GreenFog` on; `PASSPORT_CONTROL_EMISSIVE`; `BUS_LIGHT` lamps | kind-3 elements (SetActive) | RedFog off / GreenFog on at open; lamps on | B | S (the 2026-10-18 `held_gate_indicators` design) | low: z-fighting of covers, green sheets never active in a host raid |
| A5 | AirPit `Prison_Metal_Fence_01 (2)` and `(3)` (+ two mid-map fences not listed) | closed fence active, `PART_L` / `PART_R` inactive twins; nothing toggles them | the twins are the only open-shaped data; purpose unproven | B (candidate) | S | medium: half height, perpendicular leaves; may be something else |
| A6 | AirPit shop shutters `TH_Roll_door_01/02/03`, `DonB_Ramen_closed` (+ `(1)`), `Airport_Shop_BS_closed_closed` | closed slab shells (324) and authored open shells (38) | the open shell, already shown | B (done) | none for state; motion not authored | none |
| A7 | AirPit `Cube (10) Group` | layer-0 blockout boxes | not a door | E | none | none |

Shared steps (paid once, then each row above is its stated effort):

- P1 (M). A `DotAnimator` decoder in the extractor (extend `tools/extract/extract_door_poses.py` or add a sibling) writing
  `tools/maps/<stem>.doors.json` schema 2 (adds `ease` and an optional Hermite curve), mirrored in `ArenaMapRegistry.<Map>.DoorPoses`
  and `validate_map_descriptors.py`, with a pinned decode test: offset after the last field equals the byte length on all 82
  elements of the five levels; golden values from this record; sibling ordinals must follow the EXTRACT/runtime order, which
  differs from Arena's own `m_Children` order for Bay5's depot gates (Arena lists 822 first, the extract 821), so address repeated
  names by world position; the layout does not survive an Arena rewrite of the levels without a re-run.
- P2 (S to M). Address repeated names by sibling ordinal or world position (Bay5 depot roots, AirPit passport leaves, Equator
  `Door_Sliding_Respawn` appears twice), as `partial_scope.py`'s `at` rule does; `Transform.Find` by name returns the first.
- P3 (M). Client: per-pose ease (Linear, InOutQuint, 3-key Hermite), quaternion Slerp or an unwrapped euler for rotations (the
  smoothstep and component-wise euler of Bowl's `DoorPoseModel` would swing a passport leaf through 360 degrees from 270 to -90), the
  park-carver rule for a lifted slab (`ParkedBodyStands` fires on any collider top above floor + 0.3 m) and for a leaf tucked in a
  pocket, and a timing key for Arena's seconds against the fast default (a new key, never a changed default).
- P4 (S). Host check per pose: after a release read the local transforms with `uibridge_inspect` and compare with the sidecar.

## Roller shutters

"Bekommen wir die Rolltore auch hin?" per shutter:

| Shutter | Authored open state? | If not, what a hand-authored roll-up would be | Honest to Arena? |
|---|---|---|---|
| Bay5 `Door_01 (4)`, `(2)`, `One_man_Door` | yes: slab local y +3.089 / +3.092 / +3.173 m in 4.0 s, linear (DotAnimator) | none needed | yes, exact numbers; the open slab ends above the frame header, overlapping the wall plane |
| Bay5 `Door_01 (3)` | no: Arena keeps it shut in TeamFight | borrow the neighbours' +3.09 m / 4 s, or keep it hidden as today | the lift is borrowed, not authored for this placement |
| AirPit `TH_Roll_door_01/02/03`, `DonB_Ramen`, `Airport_Shop_BS` | yes, as a STATE: a separate open shell with the rolled-up header (level 38) | a closed-to-open swap at a release could toggle the two authored shells; any in-between motion (sliding the slab into the header) would be invented | the open state is Arena's own and already in the port; a swap is honest, a tween is not |
| Equator FCK `equator_roller_shutters_02 (9)` | no; no twin, no script; Arena walls it off | (a) frame-only state (hide the 6 slab children, keep the frame; today's behaviour); (b) translate the 6 slab children up in the frame's local axis, precedent: AutoService's `_01` lift of about 2.03 local units (root scale 0.851) in 4.5 s with the frame staying; the `_02` slab is 2.913 m tall under a 3.07 m frame, so a lift that clears the opening (about 3.1 m) puts the slab about 3 m above the frame header into whatever is there; (c) scale the slab to zero height: no Arena precedent (no scale curve exists on any gate clip), do not use | (a) is the most honest to the data (nothing is authored); (b) is our design built on a sibling prop's numbers and must be labelled so; (c) is not Arena |

Arena's open roller look in the loose `Rollete_Gate_Open_01/_02` prefabs is frame plus a roll-up curl above the opening; a plain
translated slab (Bay5) or a vanished slab (Equator today) shows no curl.

## Recommended order of work

1. P1 to P3 as one change set (extractor, sidecar schema 2, client ease/rotation), pinned by tests, with Equator as its first map.
   Rationale: the Equator booths are the only gates ArenaMode players meet at every match start (Team DM spawns into the booths),
   the data is the smallest (8 translation rows, one ease), and the existing 1.38 m slide is the part least faithful to Arena.
   First concrete step: decode level 57 to `equator.doors.json` (expect 8 leaf rows: L +2.0 / R -2.0, 5.0 s except the R leaf of
   GateEntity 24002 at 7.0 s, ease 16), then replace `SpawnGateModel.SlideDistance` with the per-leaf pose and keep `SpawnGateSlideSeconds`
   as the fast default with a new key for Arena's timing. Host: release and read the leaf `localPosition.x` (expect +-2.0).
2. Bay5. First step: the pilot row for `Door_01 (4)` (kind 1, linear, 4.0 s, y 0.124 to 3.213), host look at the slab against the
   warehouse wall (x 129.33..129.43 vs indoor mesh at 129.35) and the park-carver result, then the depot gate 821 (rotation plus the
   7 s curve), then `Telfer_Help`. Rows for `(2)`, `One_man_Door` and gate 822 only after the user decides whether the bake may change.
3. AirPit. First step (S): stop hiding the back-row bus assemblies `(3)`, `(4)`, `(5)` (they are already the open pose) and check the
   walkway and carvers on the host; then the passports (16 rows, P2/P3 dependent) together with `held_gate_indicators` (A4), then
   the front-row bus doors from the back-row pose with our own seconds.
4. Last: fences (host look at `PART_L/R` first), Equator FCK (user decision on a roll-up), cosmetic lamps, and Arena's gate
   sounds (clip names and lengths are in F3, F4 and F5; the audio export is a development input only today).

## Open questions (host look or user)

1. Host: is the `DotAnimator` target absolute local on the host as well? Release one Equator booth and compare the final leaf
   `localPosition.x` with +-2.0; on Bay5 compare the slab y with 3.213. Ease numbering and word 3 stay unverified until then.
2. User: authentic timing (5 / 7 s, 4 s, 7 s, 1.7 s) or the fast default? A release already waits 7.7 to 13.5 s on Bowl; ArenaMode has
   to expect it or choose the snap (`SpawnGateDrive = false`).
3. User: Bay5 `(3)` is opened by the port and shut in Arena; `(2)` and `One_man_Door` are opened by Arena and shut in the port; the east
   depot gate (822) is shut in the port. Changing any of them changes `opened_barriers` and the NavMesh bake. Why `(3)` was chosen is
   not recorded beyond the 2026-09-21 shared-path note: not in hand.
4. Host: Bay5 roller `(4)` open: does the slab clip visibly through the wall or ceiling, and does the park carver of a lifted slab cut
   anything at floor level?
5. Host: AirPit back-row bus doors left standing open: does the 1.32 m gap keep the south team's routes passable?
6. Host: AirPit fence `PART_L` / `PART_R` activated at open: what do they look like, and are they a gate or something else?
7. User and host: Equator FCK shutter, roll-up wanted at all (Arena never opens it), and what is above the opening (ceiling mesh)?
8. Not decoded: the 688-byte `DotAnimator` of the emergency lights (Equator `EXIT_LIGHT_EMISSIVE`, AirPit `BUS_LIGHT`), the unnamed words
   3 and 8 to 10, the `EntitySoundPlayer` tables, closing behaviour and trigger, `PASSPORT_CONTROL_EMISSIVE`'s role.
9. After any Arena update: rerun the decoder; the layout is pinned by byte length, the scene order by `tools/bundle/key-sources.json`.

## Corrections to earlier records

- door-poses.md "Other maps" and "Open": "Equator's sliding leaves are inferred from geometry (no Animator), Bay5 has no Animator under an
  opened barrier ... nothing to replay" is true for Animators and false for the script: `DotAnimator` carries end pose, duration and ease
  for 8 Equator leaves, 3 Bay5 rollers, 8 depot leaves (plus 4 on the inactive PTOR twins) and 16 AirPit passport leaves.
- spawn-gates.md "What exists": "Arena's gate script (`GateEntity`) is unreadable ... only the closed pose is in the data": `GateEntity` holds
  an id; the motion is on `DotAnimator` children. "Bay5's depot gate ... carry authored inactive open variants instead": the depot leaves
  are animated, the inactive `Gates_PTOR_medium` twins are a variant with the same targets; the fence twins are unproven.
- Task brief: the booth doors are under `SPAWN_DOORS` in level 57 (not `Respawn_1/2`); the client opens ONE depot gate, not both; AirPit has
  3 bus-door Animators, not 6, and they have no clips.
- 2026-10-25 (door-poses.md "Exit lamps"): E5 ("cosmetic lamp, not decoded", "D (later)"), A4's `BUS_LIGHT` lamps and open question 8
  ("Not decoded: the 688-byte `DotAnimator` of the emergency lights") are closed. The lamp `DotAnimator`s decode with the layout of this survey (Equator 4
  objects / 12 elements, AirPit 3 / 9, all kind 3, duration 0, each switching the lamp's two `Point light` objects and the sibling glow node on); the
  port carries the two Point lights of every lamp as switch rows (Equator 8 rows, AirPit 6) and skips the glow node. What stays open: Arena's
  `AreaLight` wash below the glow node (EFT has its own AreaLight classes; not ported), the `EmissiveAnimator` of the sign meshes (the port keeps
  the sign always lit) and the trigger (GateEntity 24006 / 3005 may fire at another moment than the doors; not in the data).

## Scripts written

Scratch only, under `C:\Users\ADMINI~1\AppData\Local\Temp\claude\D--ArenaMapPort\33214a56-0439-4be8-a5f4-e346be9ee131\scratchpad\survey\`:
`equator\` (`lib.py`, `census.py`, `find_scripts.py`, `barrier_dump.py`, `hexdump_mb.py`, `dot_all.py`, `booth_table.py`, `bounds.py`,
`names_scan.py`, `refscan.py`, `clip_dump.py`, `clip_scan_all.py`; outputs `clip_scan_all.txt`, `dot_others.txt`, `shutter_names_51.txt`,
`buildsettings_scenes.json`); `bay5\` (`s1_barriers.py` to `s25_json.py`, `bay5_level88_dotanimators.json`, `eft_rollete_grep.txt`);
`airpit\` (`s1_census.py` to `s25_pose_proposal.py`, `s9_all_clips.tsv`, `airpit_door_poses_proposal.json`); `mechanisms\`
(`dot_decode.py`, `gate_class_census.py`, `anim_comp_census.py`, `eft_rawscan*.json`, `dot28/57/88.json`, `arena_only_classes.txt`).
This synthesis added no scripts; it re-ran `equator\booth_table.py` and read `docs/progress/{door-poses,spawn-gates,bay5-arenamode-integration}.md`,
`tools/maps/{equator,bay5,airpit,bowl}.json`, `BoothDoorRepairs.cs` and `DoorPoseModel.cs`. Nothing in Arena's install, `D:\SPT41*`, or the
repository other than this file was written.

## Skeptic's verification (2026-10-23)

An independent re-derivation (own UnityPy scripts, read-only) confirmed the six decision-relevant claims: no clip, Animator or
legacy Animation below any opened barrier of Equator, Bay5 or AirPit and nothing in EFT to borrow (1,627 Arena clips, 74
controllers, 153 EFT clips and 29 EFT controllers censused); the `DotAnimator` layout and every decoded number (Equator 8 leaves
+-2.0 / 5 s / 7 s / ease 16; Bay5 rollers y 3.213 / 3.222 / 3.323 in 4 s linear; depot leaves -98.974 / 138.837 / -141.759 /
162.432 in 7 s on the three-key curve; AirPit 16 passport leaves (-90, +-90, 0) in 1.7 s); the targets as ABSOLUTE local values
(an offset reading would put Equator's leaves 0.58 m outside their frames); Bay5's `Door_01 (3)` without motion or sound while
`(2)`, `(4)`, `One_man_Door` and both depot frames have both; AirPit's three front-row bus Animators with the clip-less controller
and the static open back row; the FCK shutter with nothing authored and `LevelBorder (83)` wrapping it. The corrections it asked
for (Bowl has no `DotAnimator`; 82, not 74, elements; the bus doors' sound players; 13 `_02` shutters; two, not four, hidden
`Telfer_Help` posts; the current Bay5 sound scenes; Arena's child order vs the extract's) are applied above.
