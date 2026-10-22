# Door poses: Arena's gate animations as end poses (2026-10-22)

Status: data and descriptor done (2026-10-22, build VM); the client applies them since the Runtime step of the same task (parking,
driving, park carvers: see "Runtime" below; built and unit-tested, not yet run in a raid on the host). Arena 0.6.1.0.48206 is the build
every number was read from.

## Why

`docs/progress/bowl-release.md` (2026-10-21, "the missing wall is the BTR's parked pose") found that Arena does not hide the Bowl
gate parts at the opening, it animates them: the BTR reverses out of its slot, the ambulance drives out of its bay, the army gate's
leaves swing open, the second gate's leaf slides 3.82 m sideways onto its rail (the 2026-10-21 note called this "sinks into the
ground"; that was a misreading of a local y value, see "Corrections to the data record" below). The port's raid-start opening (`BoothDoorRepairs.OpenBarriers`, and
`SpawnGates.Release` on a mode plugin's release) deactivates the four direct children of `DOORS_SPAWN_A` and `DOORS_SPAWN_B`, so both
vehicles vanish and leave an opening that only invisible border cubes close. User decision 2026-10-22 ("ja gerne in Endstellung
parken"): park the parts in their open end pose instead of hiding them. A second remark of the same day, for later: look at
recreating the various door animations (the motion itself) instead of only hiding or parking.

This record covers the data side: a generated sidecar `tools/maps/bowl.doors.json`, its typed twin `ArenaMapRegistry.Bowl.DoorPoses`
(`ArenaDoorPose`, `ArenaMapDescriptor.DoorPosesOf`) and the checks between them. The client side that applies the poses is recorded
where it lands (`spawn-gates.md`).

## What Arena has

Level48 (`Arena_Bowl_TeamFight_DesignStuff`) holds four `Animator` components, one per gate part. Each points (PPtr file 2) at an
`AnimatorController` in `sharedassets48.assets`, which lists its Mecanim `AnimationClip`s. The clips animate Transform curves only
(every binding has type id 4; no active-flag, material or float curve exists in these clips).

| Animator on (path below the scene root) | Controller | Clips | Open clip (rule below) |
|---|---|---|---|
| `DOORS_SPAWN_A/BTR_82_closed` | `BTR_82_closed` | `BTR_82_Respawn_Animation` (7.683 s, 42 curves), `..._closed` (constant) | `BTR_82_Respawn_Animation` |
| `DOORS_SPAWN_A/Bowl_Gates_Respawn_01` | `Bowl_Gates_Respawn_01` | `Bowl_Gates_Respawn_01_Animation` (13.5 s, 6 curves) | the same |
| `DOORS_SPAWN_B/Gates_Army_01/Gates_Army` | `Gates_Army` | `Gates_Army_02_Animation` (10 s, 6 curves), `..._closed` (constant) | `Gates_Army_02_Animation` |
| `DOORS_SPAWN_B/Bowl_Respawn_Ambulance_Animated Group/Bowl_Respawn_Ambulance_Animated` | `Bowl_Respawn_Ambulance_Animated` | `Closed` (empty), `Opened` (8.3 s, 365 streamed + 875 constant curves) | `Opened` |

Each controller's name table lists a closed state, a transition "closed -> open clip" and the parameters `play` and `reset`; the
extractor finds that transition in all four ("transition into open clip yes"). Presumably the `GateEntity` script on the barrier
root (the descriptor already calls these two roots GateEntity roots) fires `play`; the port has no such script and needs none, the
end pose of the open clip is what matters.

## Method (tools/extract/extract_door_poses.py)

```powershell
python tools/extract/extract_door_poses.py --map arena_bowl --arena auto --package work/extract/bowl-native-v2 --output tools/maps/bowl.doors.json
python tools/extract/extract_door_poses.py ... --csharp      # additionally prints the ArenaDoorPose literals for the registry
python tools/validate/validate_map_descriptors.py            # sidecar schema + the 1:1 mirror with ArenaMapRegistry.Bowl.DoorPoses
```

UnityPy reads `level48` and `sharedassets48.assets` (read-only; the scene index comes from `tools/bundle/key-sources.json`, the
installation from the launcher's registry entry, the build from `ConsistencyInfo`). Names and numbers only leave the tool.

1. **Which Animators are gates.** An Animator whose GameObject lies below an `opened_barriers` entry of `tools/maps/bowl.json`. The
   barrier is the longest listed prefix of its hierarchy path, the PART the next segment (the direct child of the barrier root that
   `BoothDoorRepairs` deactivates; a part whose name contains "frame" would never be hidden and is skipped), the rest is the
   Animator's own sub path. An Animator whose controller carries no clip is reported and ignored (AirPit's bus doors).
2. **Open clip.** Of the controller's clips, the one whose name does not end in "closed" (case-insensitive). Exactly one must remain,
   otherwise the tool stops. The `*_closed` clip is only a cross-check.
3. **Hashed paths.** A clip binding names a node by the CRC-32 of its transform path relative to the Animator's GameObject: zlib
   CRC-32 of the UTF-8 bytes of `Child/Grandchild` (names joined by "/", no leading slash); 0 is the Animator's own GameObject. The
   tool hashes every descendant of the Animator and looks each binding up. An unresolved or ambiguous hash stops the tool.
4. **Curve layout.** Bindings run in order, 3 curves for a position, 4 for a quaternion rotation, 3 for a scale, 3 for a euler
   rotation (the binding's `attribute` is 1, 2, 3, 4). Curve indices below the clip's streamed curve count belong to the StreamedClip,
   then the DenseClip, then the ConstantClip (875 constant values in `Opened`).
5. **StreamedClip.** Per frame a float32 time and a uint32 key count, per key a uint32 curve index and four float32 coefficients
   (a, b, c, d): `value(t) = a dt^3 + b dt^2 + c dt + d` after the key, d the value at the key. Each curve starts with a key at
   -FLT_MAX; the stream ends with a +inf frame. A node's open value is the curve at the clip's stop time.
6. **Closed pose.** The scene's own local transform of the node (euler: the clip's raw branch, see below). Cross-checks: the
   `*_closed` clip equals the open clip's first key exactly (BTR 14 channels, army 2: max difference 0), and both agree with the
   scene within the tolerances below.
7. **Rotations.** A node whose clip stores euler curves (BTR, wheels of the BTR, army leaves, the gate) keeps the clip's RAW euler
   values (multi-turn spins survive: the BTR wheels end at about -553 degrees). A node with quaternion curves (the ambulance) is
   followed through the whole clip: every 1/240 s the quaternion is converted to Unity's ZXY euler and moved to the branch nearest to
   the previous sample, so a spin of several turns is kept; the closed euler is the scene quaternion converted and centred in
   (-180, 180]. Where the clip's raw closed euler differs from the scene's rotation by more than 0.05 degrees, the sidecar carries the
   scene's rotation on the clip's branch, so the part is exactly where the scene has it at s = 0.
8. **Kept.** A node enters the sidecar when its open pose differs from the closed pose by more than 1 cm or 1 degree (euler clips:
   or by more than 1 degree in the raw triple, which keeps whole-turn wheel spins). Constant curves therefore drop out unless they
   disagree with the scene.

Thresholds: clip start against the scene 2 cm / 1 degree is a warning beyond that and a stop beyond 10 cm / 5 degrees (a wrong hash
or frame convention would be metres or tens of degrees off); the typed numbers are rounded to 3 decimals and checked for byte-stable
output (two runs, with `--arena auto` and with the folder, give the same SHA-256).

## Verification (2026-10-22 14:20, build VM, Arena 0.6.1.0.48206, repo e54b292 + the uncommitted files of this task)

```
scene Arena_Bowl_TeamFight_DesignStuff = level48 + sharedassets48.assets, Arena 0.6.1.0.48206, 684 transforms, 4 animators (4 below opened barriers)
    decode self-check: 162 key spans, each cubic reaches the next key (worst error 1.9e-04)          BTR_82_Respawn_Animation
    decode self-check: 24 key spans, each cubic reaches the next key (worst error 7.0e-07)           Bowl_Gates_Respawn_01_Animation
    decode self-check: 493785 key spans, each cubic reaches the next key (worst error 2.1e-07)       Opened
    decode self-check: 12 key spans, each cubic reaches the next key (worst error 9.6e-06)           Gates_Army_02_Animation
package check: 19 node(s) exist once in bowl-native-v2 with Arena's closed transform
```

- **Hash resolution.** 136 node bindings: 134 hashed paths (BTR 8, gate 1, army 2, ambulance 123) and the Animator itself twice (BTR,
  ambulance). Every one resolves to exactly one descendant; no two descendants of an Animator share a hash (descendant counts: BTR 119,
  gate 21, army 22, ambulance 124). Rule checked on the army leaves (`Gates_Army_door_01` = 1174491159, `Gates_Army_door_02` =
  3741843885) and the gate node (`Bowl_Gates_Respawn_01_Gate` = 4194129352); the hashes of all 19 kept nodes are in the unit test or
  reproducible with `E.crc32_path`.
- **Decode self-check.** The cubic of a key, evaluated at the next key's time, reaches the next key's value on all 493,983 key spans
  (worst absolute error 1.9e-4 on the BTR curve that runs to -553 degrees, 2.1e-7 on `Opened`): the coefficient order and the curve
  indices are right. The tool stops if one span is off by more than 1e-4 + 1e-5 x the value.
- **Closed pose against the clip.** Gates_Army: 0.369 degrees on `door_01` (the clip's closed z), nothing else; gate: 1.92 cm in y
  (scene 1.99, clip 1.971) and 0.509 degrees in x; BTR: wheels 0.31-0.33 degrees in x, and two warnings: `BTR_82_wheel_L_03` 3.7 cm
  and `BTR_82_wheel_R_03` 3.1 cm in z (the clip's closed z is 0.666 / 0.660, the scene's 0.629: an authoring offset of the wheel
  suspension, not a resolution error, since the same wheels match everywhere else); ambulance: 0 (exactly the scene).
- **Package.** All 19 kept nodes exist exactly once in `work/extract/bowl-native-v2/scenes/Arena_Bowl_TeamFight_DesignStuff.json`
  under `barrier/part/node`, with Arena's local position within 1 mm and rotation within 0.05 degrees of the closed pose; that is
  the name the client addresses at runtime.
- **Tests.** `python -m unittest discover -s tools/tests -p "test_door_poses.py"` ran 55 tests OK (the real-data class re-extracts the
  sidecar from the installed Arena and compares the poses; it skips without Arena or UnityPy). The whole Python suite ran 1950 tests,
  OK (skipped=55). `dotnet test ArenaMapPort.Server.Tests -c Release`: 1480 passed, 0 failed (`BowlMapTests`: mirror, lookup,
  only-Bowl). `python tools/validate/validate_map_descriptors.py` prints OK (the mirror is part of it; the unit tests mutate a
  number, a clip name, a node, the order, the schema and the barrier and see an error each time).

## Decoded table: what entered the sidecar (19 poses)

Positions are local to the node's parent, euler angles are `localEulerAngles` (raw clip values for euler clips). "Moves" is the time
window in which a channel of the node leaves its start value until it stops changing (sampled at 60 Hz, 1e-3 tolerance), inside the
clip length. In the sidecar every node of a part carries the part's clip name and length. A local axis is not a world axis: the
`Bowl_Gates_Respawn_01_Gate` row below moves in local y, which is a horizontal direction in the world because its parent is rotated
-90 degrees about x (world positions of the gate leaf: (38.104, 0.233, -3.279) -> (41.311, 0.233, -1.197), see "Corrections").

| Barrier | Part | Node | Local position closed -> open | Euler closed | Euler open | Clip | Moves (s) |
|---|---|---|---|---|---|---|---|
| `DOORS_SPAWN_A` | `BTR_82_closed` | `(the part itself)` | (51.182, -0.035, -21.498) -> (45.61, -0.04, -22.14) | (-90, 0, 83.511) | (-90.3, 266.474, -182.962) | 7.683s | 0.02-7.62 |
| `DOORS_SPAWN_A` | `BTR_82_closed` | `BTR_82_wheel_L_01` | (1.334, 2.25, 0.629) -> (1.334, 2.25, 0.629) | (0, 3, 0) | (-553.313, 3, 0) | 7.683s | 0.02-7.68 |
| `DOORS_SPAWN_A` | `BTR_82_closed` | `BTR_82_wheel_L_02` | (1.307, 0.7, 0.63) -> (1.307, 0.7, 0.63) | (0, 3, 0) | (-553.313, 3, 0) | 7.683s | 0.02-7.68 |
| `DOORS_SPAWN_A` | `BTR_82_closed` | `BTR_82_wheel_L_03` | (1.334, -1.224, 0.629) -> (1.334, -1.224, 0.659) | (0, 3, 0) | (-553.313, 3, 0) | 7.683s | 0.02-7.68 |
| `DOORS_SPAWN_A` | `BTR_82_closed` | `BTR_82_wheel_L_04` | (1.307, -2.737, 0.63) -> (1.307, -2.737, 0.633) | (0, 3, 0) | (-553.313, 3, 0) | 7.683s | 0.02-7.68 |
| `DOORS_SPAWN_A` | `BTR_82_closed` | `BTR_82_wheel_R_01` | (-1.342, 2.25, 0.629) -> (-1.342, 2.25, 0.629) | (0, -3, 0) | (-542.205, -3, 0) | 7.683s | 0.02-6.02 |
| `DOORS_SPAWN_A` | `BTR_82_closed` | `BTR_82_wheel_R_02` | (-1.342, 0.7, 0.629) -> (-1.342, 0.7, 0.629) | (0, -3, 0) | (-537.425, -3, 0) | 7.683s | 0.02-6.00 |
| `DOORS_SPAWN_A` | `BTR_82_closed` | `BTR_82_wheel_R_03` | (-1.342, -1.224, 0.629) -> (-1.342, -1.224, 0.636) | (0, -3, 0) | (-540.698, -3, 0) | 7.683s | 0.02-6.02 |
| `DOORS_SPAWN_A` | `BTR_82_closed` | `BTR_82_wheel_R_04` | (-1.342, -2.737, 0.629) -> (-1.342, -2.737, 0.651) | (0, -3, 0) | (-551.416, -3, 0) | 7.683s | 0.02-7.68 |
| `DOORS_SPAWN_A` | `Bowl_Gates_Respawn_01` | `Bowl_Gates_Respawn_01_Gate` | (-0.179, 1.99, 0.119) -> (-0.179, -1.833, 0.119) | (0, 0, 0) | (-0.089, 0, 0) | 13.5s | 0.03-13.33 |
| `DOORS_SPAWN_B` | `Bowl_Respawn_Ambulance_Animated Group` | `Bowl_Respawn_Ambulance_Animated` | (0, 0, 0) -> (4.773, 0, 0) | (-90, 0, 0) | (-90, 0, 0) | 8.3s | 0.18-6.07 |
| `DOORS_SPAWN_B` | `Bowl_Respawn_Ambulance_Animated Group` | `Bowl_Respawn_Ambulance_Body_HOOD` | (-2.034, 0.022, 1.306) -> (-2.034, 0.022, 1.306) | (-0.364, -3.069, -6.75) | (-0.584, -4.935, -6.734) | 8.3s | 0.28-6.67 |
| `DOORS_SPAWN_B` | `Bowl_Respawn_Ambulance_Animated Group` | `Bowl_Respawn_Ambulance_Body_tros_02` | (6.276, 0.006, 0.556) -> (0.724, 0.006, 2.199) | (0, 0, 0) | (0, 0, 0) | 8.3s | 4.58-4.60 |
| `DOORS_SPAWN_B` | `Bowl_Respawn_Ambulance_Animated Group` | `Bowl_Respawn_Ambulance_Wheel_L` | (-1.71, 0.826, 0.331) -> (-1.71, 0.826, 0.331) | (0, 0.029, 0) | (0, 406.884, 0) | 8.3s | 0.07-6.33 |
| `DOORS_SPAWN_B` | `Bowl_Respawn_Ambulance_Animated Group` | `Bowl_Respawn_Ambulance_Wheel_L_Back` | (1.2, 0.814, 0.331) -> (1.2, 0.814, 0.331) | (0, 0.029, 180) | (0, 406.884, 180) | 8.3s | 0.07-6.33 |
| `DOORS_SPAWN_B` | `Bowl_Respawn_Ambulance_Animated Group` | `Bowl_Respawn_Ambulance_Wheel_R` | (-1.71, -0.804, 0.331) -> (-1.71, -0.804, 0.331) | (0, 0.032, 0) | (0, 406.884, 0) | 8.3s | 0.07-6.30 |
| `DOORS_SPAWN_B` | `Bowl_Respawn_Ambulance_Animated Group` | `Bowl_Respawn_Ambulance_Wheel_R_Back` | (1.2, -0.824, 0.331) -> (1.2, -0.824, 0.331) | (-0.015, 0.032, 0) | (0, 406.884, 0) | 8.3s | 0.07-6.33 |
| `DOORS_SPAWN_B` | `Gates_Army_01` | `Gates_Army_door_01` | (1.95, -0.001, 1.1) -> (1.95, -0.001, 1.1) | (0, 0, 0) | (0, 0, 142.922) | 10s | 0.03-10.00 |
| `DOORS_SPAWN_B` | `Gates_Army_01` | `Gates_Army_door_02` | (-1.95, -0.001, 1.1) -> (-1.95, -0.001, 1.1) | (0, 0, 0) | (0, 0, -161.335) | 10s | 0.05-10.00 |

The Node column shows the last path segment; the sidecar carries the full path below the part: `Gates_Army/Gates_Army_door_0N` for
the army leaves, `Bowl_Respawn_Ambulance_Animated` for the ambulance root and
`Bowl_Respawn_Ambulance_Animated/Bowl_Respawn_Ambulance_Body/Bowl_Respawn_Ambulance_Body_HOOD` (and `..._tros_02`),
`Bowl_Respawn_Ambulance_Animated/Bowl_Respawn_Ambulance_Wheel_L` (and the other three wheels) for the rest.

Operator cross-check (decoded by hand on 2026-10-21): BTR end position (45.61, -0.04, -22.14) and euler (-90.3, 266.474, -182.962);
ambulance root local x 4.773; army leaves z 142.922 / -161.335; gate y -1.833. All reproduced exactly.

- The BTR body ends in a different euler triple of the same heading (x = -90 is a gimbal lock, y + z = 83.51 on both sides): the
  two rotations differ by 0.30 degrees, the body moves 5.61 m west. Along the whole clip the raw triple keeps y + z = 83.51 (x within
  0.4 degrees of -90; sampled at tenths: y 0, 82, 155, 220, 84, 84, 84, 85, 107, 266, 266 with z the complement), so the rotation
  never leaves 0.65 degrees of the closed one: the "swing" is a gimbal-compensated no-op. Interpolating the raw triple component-wise
  (Unity's own behaviour for euler clips) keeps y + z constant too, so the BTR does not turn on the way.
- The BTR wheels spin about local x: the four left wheels end at -553.3 degrees, the right ones at -542.2 / -537.4 / -540.7 / -551.4
  (2 to 16 degrees short); four wheels also gain height, `L_03` +3.0 cm, `R_04` +2.2, `R_03` +0.7, `L_04` +0.3 (suspension travel).
  The left wheels and `R_04` move until the clip ends, `R_01`..`R_03` stop at about 6 s.
- The ambulance root travels 4.773 m along its parent's x axis; its parent `Bowl_Respawn_Ambulance_Animated Group` is rotated -45
  degrees about y, so the world motion is (-42.09, -69.82) -> (-38.72, -66.44). Its four wheels turn 406.9 degrees as the clip plays
  them (1.13 turns for 4.773 m, evaluated from 11 quaternion keys 0.2-1.6 s apart; that is less than a rolling wheel would make,
  but it is what the stored keys give and so what Arena shows).
- `tros_02` (a tow rope) jumps 5.79 m inside 0.02 s at t = 4.58 s: from (6.276, 0.006, 0.556) to (0.724, 0.006, 2.199) in the body's
  frame. The end pose is the attached one.
- The ambulance `HOOD` ends 1.88 degrees off its closed pose (the suspension settling); that is above the 1 degree threshold, while
  the body's 0.57 degrees is below it.

## What was dropped, and what is not carried

- **Dropped nodes.** BTR 0 of 9, gate 0 of 1, army 0 of 2; ambulance 117 of 124. Of the ambulance's 117, 114 never leave 1 cm / 1
  degree (LOD, shadow, collider and ballistic children with constant or one-key curves); three move during the clip and end inside
  the thresholds: `Body` (peak 1.9 degrees, ends 0.57), `Door_R` (peak 1.2, ends 0.79) and `Side_Door` (peak 1.8 cm / 1.4 degrees, ends
  0.2 cm / 0.41). The ambulance's doors do not open in `Opened`: the clip drives it out.
- **The path of the motion.** Only start and end are carried. The clips ease: progress at tenths of the clip length is BTR position
  0, .04, .16, .31, .50, .68, .83, .94, .98, 1.01 (a 1 % overshoot at 0.9), 1; respawn gate local y 0, .03, .11, .24, .38, .54, .70, .83, .94,
  1.0, 1; army leaf 0, .02, .08, .18, .31, .46, .62, .76, .89, .97, 1; ambulance root x 0, .01, .06, .18, .39, .64, .87, .99, 1, 1, 1
  (it stops at 0.7 of the clip, 5.8 s). The BTR body's raw y/z swing (y up to 220 degrees at 0.3 of the clip, z the complement) is the
  gimbal-compensated no-op described above. The ambulance's hood, body, `Door_R` and `Side_Door` rock by 1-3 degrees while it
  rolls.
- **Scale curves.** No scale curve changes a node by more than 1 % (counted, zero on all four gates).
- **Controller logic.** Parameters, other states and `GateEntity` are not read; the open pose is the end of the open clip.
- **Colliders.** The parts carry their colliders (BTR: `BTR_82_exterior_closed_COLLIDER` layer 18, ballistic layer 12, and the layer-29
  `Cube (205)`; ambulance likewise), which move with the parked pose. The NavMesh and the border cubes around the parked BTR are a
  separate step (`bowl-release.md`, 2026-10-21).
- **Other maps.** A scan of every scene that `tools/bundle/key-sources.json` lists for the four maps (Equator 51, 53, 55, 57, 58;
  Bay5 79 to 89; AirPit 28, 29, 37, 38, 40, 324, 325; Bowl 41, 43, 45 to 48, 95) for Animators below the map's `opened_barriers` found
  exactly the four of Bowl's level48 with clips. Equator and Bay5 have no Animator under an opened barrier at all (their DesignStuff
  scenes hold the spectator crowd, `SCAV_SPECTATORS/Arena_NewFans_*`: 99 and 88 Animators, as do AirPit's and Bowl's, 99 each; none
  of them a gate). AirPit's three `BUS_DOORS/Apron_bus_MAZ_doors` Animators (level28) use a controller with no clip at all, so
  there is nothing to park or play; the six Animators of the apron bus in level38 and level324 are scenery outside any barrier. The
  generic booth doors (`Door_Sliding_Respawn*`) are not animators. The extractor reports such an Animator as ignored; a map joins
  the tool by a row in `MAP_SOURCES` once it has one with clips.

## Handoff

- Sidecar `tools/maps/bowl.doors.json` (schema 1, generated), descriptor `ArenaMapRegistry.Bowl.DoorPoses` (`ArenaDoorPose[]`, typed
  from the sidecar, same 3-decimal numbers), `ArenaMapDescriptor.DoorPosesOf(barrier, part)` (the poses of one gate part in sidecar
  order, the part itself first). Every other map has an empty array.
- Open-state interpolation for the client: position by `Vector3.Lerp`, rotation as `Quaternion.Euler(Vector3.Lerp(closedEuler,
  openEuler, s))` (component-wise, as Unity animates euler clips: the BTR wheels turn 1.5 revolutions, the BTR body keeps its
  heading), `s` eased over `Seconds`. A hard park (`s = 1` at once) is the same call.
- Addressing: the part is a direct child of the barrier root with exactly the sidecar's name (the ambulance's contains a space); the
  node is the path below the part (`""` = the part itself). The closed numbers are Arena's scene values; the package's scene has
  the same local transforms, so a part can also be restored to `s = 0` from them.

## Open

- Playing the motion (mid-clip shapes, the ambulance's rocking, the rope) is a later step; the sidecar would need a sampled curve per
  node for that (the tool already evaluates every curve at any time).
- Re-run the extractor after an Arena update: the clips are in `sharedassets48.assets`, which a hotfix could rewrite; the real-data test
  fails if the installed Arena yields other poses than the tracked sidecar.
- The two wheel offsets (3.7 / 3.1 cm) are warnings by design; if a stricter tolerance is wanted, the wheels' closed z could be taken
  from the clip instead of the scene.

## Runtime (2026-10-22, client: `BoothDoorRepairs`, `SpawnGates`, `DoorPoseModel`)

Status update: the client now applies the poses. Build `dotnet build ArenaMapPort.Client -c Release -p:ArenaMapPortDllOnly=true` is
clean (0 warnings, 0 errors), `dotnet test ArenaMapPort.Server.Tests -c Release` passes 1565 tests (1480 before, 85 new in
`DoorPoseModelTests.cs`: model rules, Bowl's real pose data through the model, wiring read as text). Not yet run in a raid: the host
check below is the open item. The per-frame cost of the drive was modelled offline in Unity 2022.3.43 (section "Cost of the drive"
below) and the release now logs the frame time of its own drive window. Rules and the carver reasoning are in [spawn-gates.md](spawn-gates.md) ("Follow-up 2026-10-22").

### What the client does

- **Raid-start opening** (`BoothDoorRepairs.OpenBarriers`, both passes, idempotent). For every active, non-frame part of an opened
  barrier: `SpawnGates.NotePoses(part, map.DoorPosesOf(barrier, part.name))` first (resolves the animated nodes below the part with
  `Transform.Find`, compares each with the descriptor's closed pose, measures the closed movement colliders), then the existing
  `OfferHeld` / `Offer` (a hold keeps the part closed), then `SpawnGates.Park(part)`: the nodes snap to the open pose, the part
  stays active, `Physics.SyncTransforms()` makes the colliders follow. A part without poses goes the old way (`SetActive(false)`).
  The spawn-openings line gains `, parked N part(s) at the open pose`.
- **Hold** puts every parked or driving part back in its closed pose (the part stays active; its park carvers go, the normal hold
  carvers seal it). **Release** drives each part from closed to open over its clip's seconds (BTR 7.683 s, ambulance 8.3 s, army
  leaves 10 s, respawn gate 13.5 s) with a smoothstep ease, position by lerp and euler angles component-wise (the BTR wheels turn
  1.5 revolutions, not 0.5). One coroutine moves them all; each part finishes on its own clip: its hold carvers go and its park
  carvers come. A part with poses is never switched off.
- **Config** (new key, `Spawns/SpawnGateDrive`, bool, default true, "next release"): false snaps the parts to the open pose at the
  release instead of driving. `Spawns/SpawnGateCarve` (default unchanged, description extended) also governs the park carvers;
  `Spawns/OpenSpawnBarriers` (description extended) still governs whether the barriers are opened at all.
- **Park carvers**: a carving `NavMeshObstacle` box (the hold carvers' size rule: bounds + 0.2 m per side + 0.5 m of height) over
  every movement collider (layers 0 and 18) that Arena MOVED, i.e. that sits on or below a pose node, and that rises more than 0.3 m
  above the part's floor line. They are created once the NavMesh is installed (raid start without a hold) or when a part finishes
  its drive, and stay for the raid. Colliders outside every pose node stay uncarved (see below).
- **Probe after a release**: unchanged (`navmesh probe: every probed gate open`), except that a doorway whose probe segment runs
  through a parked body is listed as "not judged" instead of being judged.
- **Failure** never reaches EFT: a node that is not found, or whose scene pose is more than 5 cm / 2 degrees off the data's closed
  pose, or malformed data, logs one Warning (`door pose of '<part>' not applied (...); the part switches off as before`) and the
  whole part is switched off like before.

### Which colliders get a park carver (and why not all)

Bowl's four parts have 28 movement colliders (the number the 2026-10-18 hold logged): per part, counted in the package scene
(layers 0 and 18, enabled, not a trigger, active in the hierarchy), `BTR_82_closed` 10, `Bowl_Gates_Respawn_01` 2 (the leaf and its
`_RAIL_COLLIDER`), `Gates_Army_01` 5 (two leaves, the frame, two `Telfer_Help` pillars), the ambulance group 11; so the hold seals
12 under `DOORS_SPAWN_A` and 16 under `DOORS_SPAWN_B`. Park carvers: **24** (10 / 1 / 2 / 11). The four static ones
are not carved: `Bowl_Gates_Respawn_01_RAIL_COLLIDER`, `Gates_Army/Gates_Army_COLLIDER` (the frame) and the two `Telfer_Help`
pillars. The army frame is two posts whose world AABB (x -53.99..-49.37, z -55.06..-54.25) spans the whole doorway the leaves
open; a box over it would seal the gate for bots.

Offline check on the port's built NavMesh (`work/build/bowl/arena_bowl_navmesh.bundle`, rasterised at 0.25 m, boxes cut exactly
out of the walkable cells, agent radius 0 and 0.3 m, ground level; the three carver sets compared):

```
                      east pocket spawns -> field | west pocket spawns -> field | army gate doorway | respawn gate doorway
no carvers            reachable                     reachable                     reachable           reachable
hold carvers (closed) not reachable (2 of 2)        not reachable (2 of 2)        not reachable       not reachable
park carvers (movers) reachable (2 of 2)            reachable (2 of 2)            reachable           reachable
carve ALL colliders   reachable                     reachable                     NOT reachable       reachable
```

(east spawns (50.2, -3.2) and (56.0, -5.8), west spawns (-51.6, -63.4) and (-55.3, -69.1), field (2.5, -21.1); the doorway rows
flood only inside a window around the gate, so a detour does not count.) The hold row reproduces the 2026-10-18 host result (the
dryRun from the east spawn refused), the movers row is the design, the last row is why the frame is not carved.

### Corrections to the data record (found while sizing the carvers)

- **The respawn gate does not sink, it slides.** The parent `Bowl_Gates_Respawn_01` is rotated -90 degrees about x, so the node's
  local y is a horizontal world direction: the leaf (`Bowl_Gates_Respawn_01_Gate`) moves 3.82 m diagonally onto its static
  `Bowl_Gates_Respawn_01_RAIL` collider. World AABB of its `_COLLIDER`: x 35.67..40.55 -> 38.87..43.76, z -4.89..-1.68 ->
  -2.81..0.40, y unchanged 0.23..2.82; the rail's own collider (static) is x 38.99..43.91, z -2.76..0.51. So it is not equivalent
  to hiding it: it stays visible and solid beside the doorway, which is why it gets a park carver. (Corrected on 2026-10-22 in the
  "Why" section above and in `docs/MAP-RECIPE.md` 3a; `bowl-release.md` is an append-only log and carries a dated correction note at
  its end instead of a rewrite of the 2026-10-21 lines.) Recomputed from the package scene
  (`work/extract/bowl-native-v2`, parent chain `DOORS_SPAWN_A` -> `Bowl_Gates_Respawn_01` at (39.871, 0.114, -2.345) with rotation
  (-0.621, 0.337, 0.337, 0.621) -> the leaf): world pivot (38.104, 0.233, -3.279) closed, (41.311, 0.233, -1.197) open, so 3.823 m
  horizontally and 0.000 m vertically.
- **The parked BTR body** (collider `BTR_82_exterior_closed_COLLIDER`, open pose): x 41.39..49.95, z -24.03..-20.17, y 0.41..2.54;
  its nose reaches 0.45 m past the port's mesh edge at x 49.5 (0.65 m with the carver's margin). The ambulance parks at x -41.05..-36.14, z -68.77..-63.86 (off the
  mesh), the army leaves at x -49.55..-47.61 and -55.77..-53.81.
- No server spawn point lies within 5.6 m of a parked body (nearest: the east bot point (51.9, -29.5) to the BTR).

### Log lines to expect on the host (Debug, source `maschine-ArenaMapPort`)

Plain raid, bots none, Bowl (no hold), both passes of the opening, then the NavMesh install:

```
[SpawnGates] park: 'BTR_82_closed' snapped to the open pose (9 node(s), clip BTR_82_Respawn_Animation); park carvers follow the navmesh install
[SpawnGates] park: 'Bowl_Gates_Respawn_01' snapped to the open pose (1 node(s), clip Bowl_Gates_Respawn_01_Animation); ...
[SpawnGates] park: 'Gates_Army_01' snapped to the open pose (2 node(s), clip Gates_Army_02_Animation); ...
[SpawnGates] park: 'Bowl_Respawn_Ambulance_Animated Group' snapped to the open pose (7 node(s), clip Opened); ...
[RaidFix] spawn openings:
  spawn barrier 'DOORS_SPAWN_A' at (0.0, 0.0, 0.0): deactivated 0 part(s), 0 already off, kept 0 frame part(s), parked 2 part(s) at the open pose
  spawn barrier 'DOORS_SPAWN_B' at (0.0, 0.0, 0.0): deactivated 0 part(s), 0 already off, kept 0 frame part(s), parked 2 part(s) at the open pose
[SpawnGates] parked 'BTR_82_closed': 9 node(s) at the open pose (clip BTR_82_Respawn_Animation), 10 park carver(s) (navmesh installed)
[SpawnGates] parked 'Bowl_Gates_Respawn_01': 1 node(s) at the open pose (clip Bowl_Gates_Respawn_01_Animation), 1 park carver(s) (navmesh installed); 1 collider(s) Arena does not move stay uncarved
[SpawnGates] parked 'Gates_Army_01': 2 node(s) at the open pose (clip Gates_Army_02_Animation), 2 park carver(s) (navmesh installed); 3 collider(s) Arena does not move stay uncarved
[SpawnGates] parked 'Bowl_Respawn_Ambulance_Animated Group': 7 node(s) at the open pose (clip Opened), 11 park carver(s) (navmesh installed)
```

(the `park:` lines come once per part, the second pass is silent; the barrier line appears on both passes.) With the developer hold
(`Spawns/SpawnGateHold = true`, `SpawnGateHoldSeconds = 60`): no `park:` lines, `held closed`, `navmesh installed: 28 carver(s) over
28 movement collider(s) of 4 gate part(s)`, `navmesh probe: every probed gate sealed (...)`; at the release the four
`parked '<part>': ... N park carver(s) (released)` lines come 7.7 s (BTR, 10 carvers), 8.3 s (ambulance, 11), 10 s (army, 2) and 13.5 s
(gate, 1) after the release, and the last of them is followed by
`released (developer test hold, 60.x s after the raid timer started): 0 leaf(s) slid 1.38 m in 1.0 s, 0 gate part(s) switched off, 28 carver(s) removed, drove 4 part(s) to the open pose in 13.5 s, 24 park carver(s) in place`
(`snapped` instead of `drove` with `SpawnGateDrive = false`), directly followed by
`drive frames: N frame(s) in 13.5 s, mean X ms, max Y ms (the whole frame, not the poses alone; Physics.autoSyncTransforms False)`
(not logged for a snap), then 0.5 s later `navmesh probe: every probed gate open (N probed...)`
with `'Bowl_Gates_Respawn_01' ...: the probe segment runs through a parked body, not judged` and `1 doorway(s) occupied by a parked body
not judged`. A Hold after a parking logs `hold requested (...): ... 4 parked part(s) back in the closed pose`.

### Cost of the drive (review finding, offline model, 2026-10-22)

A reviewer asked what re-posing the parts every frame costs: a release drives 19 nodes, which with their descendants are 272
transforms, 72 non-Rigidbody `MeshCollider` / `BoxCollider` (static colliders moved by transform) and 178 mesh renderers, for up
to 13.5 s, in a 55k-object scene, and proposed a kinematic `Rigidbody` on each node root as the mitigation (Unity advises that for moving
colliders). It was measured instead of assumed, in a scratch Unity 2022.3.43f1 project (batch mode, `-nographics`, outside the
repository; the script is not kept, the set-up is below), then the mitigation was **not** adopted because it is not cheaper.

Set-up: a synthetic Bowl (the real 19 nodes and about the reviewer's counts: BTR 9 nodes / 30 colliders / 119 transforms,
ambulance 7 / 31 / 117, army gate 2 / 6 / 10, respawn gate 1 / 3 / 5 = 251 transforms and 70 colliders, no renderers;
non-convex mesh colliders of 2000, 1200 and 416 triangles plus boxes), a static world, and per
frame: pose all 19 nodes (euler and position, interpolated), `Physics.Simulate(1/60)`, N raycasts. `Physics.simulationMode =
Script` because that is how EFT runs its physics (`PhysicsExtensions.Simulation`, decompiled client); `Physics.autoSyncTransforms`
False, then True as the worst case. 600 frames per variant after a 30-frame warm-up, ms per frame (simulate + raycasts + poses):

```
                                              run 1: 19,881 boxes,    run 2: 60,025 boxes + 2,000
                                              20 rays                 non-convex meshes, 200 rays
                                              mean    median          mean    median
V0  no movement                               0.348   0.295           0.473   0.355
V1  static colliders, localPosition+Euler     0.430   0.376           0.699   0.535     (repeat 0.434 / 0.398   0.673 / 0.527)
V2  static, SetLocalPositionAndRotation       0.454   0.402           0.668   0.530
V3  kinematic Rigidbody on the 5 node roots   0.509   0.472           0.871   0.640     (repeat 0.546 / 0.475   0.915 / 0.689)
V4  kinematic, SetLocalPositionAndRotation    0.534   0.462           0.759   0.617
autoSyncTransforms = True (run 2):  no movement 0.434 / 0.338, static 0.745 / 0.575 (two writes) and 0.680 / 0.537 (one write),
                                    kinematic 0.754 / 0.590 and 0.774 / 0.594
```

- Re-posing the 70 static colliders costs about **+0.1 ms (run 1) to +0.2 ms (run 2) per frame** over no movement, 0.6 to 1.2 % of
  a 16.7 ms frame. Every variant shows p99 values of 4 to 5.7 ms (run 2) and maxima of 5 to 15 ms, the no-movement ones included
  (V0 max 7.2 and 5.1 ms), so those spikes are the editor's, not the movement's. Three times the colliders and ten times the
  raycasts raised the baseline by 0.12 ms and the difference by 0.1 ms.
- A kinematic body on the topmost nodes (BTR root, ambulance root, the two army leaves, the gate leaf) is **0.08 to 0.24 ms
  slower** than the static colliders in all four pairings with `autoSyncTransforms` False, and +0.01 / +0.09 ms with it True. Attaching the 5 bodies costs 1.3 to 2.6 ms once (with the first simulate), detaching 0.7 to 1.0 ms.
  So the mitigation would add code (attach, detach on finish, on hold, on unload) and per-frame cost; `SpawnGates` keeps static
  colliders and pins that in `DoorPoseWiringTests` (no `AddComponent<Rigidbody>`, no `isKinematic`).
- One call (`SetLocalPositionAndRotation` instead of two writes) is within the noise of the two-write version, so `ApplyPose`
  stays as it is.
- Pitfall found on the way, in case bodies are ever added: `AddComponent<Rigidbody>()` followed by `isKinematic = true` in the same
  frame on a hierarchy with non-convex `MeshCollider` logs nothing (collider on the same object, on a child, or disabled around the
  call: all silent); a body left dynamic for one `Physics.Simulate` logs `Non-convex MeshCollider with non-kinematic Rigidbody is
  no longer supported since Unity 5.` (the control run).
- Limits of the model: Unity Editor, not EFT; synthetic colliders; no bots, no players, no CharacterController overlap recovery.
  EFT's `autoSyncTransforms` value is not visible in the decompile (only a helper that syncs when it is False).

The host check is therefore cheap to read: the release logs `drive frames: N frame(s) in 13.5 s, mean X ms, max Y ms
(...; Physics.autoSyncTransforms <value>)`, the whole frame while any part is still posed. Accept when the max stays within the
host's usual spikes (about 8 ms, `uibridge_status` `max`) and the mean is the raid's usual frame time; if not, `SpawnGateDrive =
false` snaps (and removes the wait), or shorten the clips.

### Host recipe (GPU-2, client DLL deployed and EFT restarted)

0. `uibridge_status` (loaded DLL = VM build), then `uibridge_help topic=overview` as usual. Bowl, bots None, clear weather.
1. **Plain raid.** `uibridge_log` filtered `[SpawnGates]`: the lines above, 0 warnings. Stand in the east pocket at about (51.5, 0.1, -22.0)
   looking west (yaw 270): the BTR's rear end fills the slot opening (its body reaches x 49.95; (49.5, -22) is inside the parked body,
   a teleport there lands inside the body or is refused). `uibridge_look`/F10 west: the hit is `BTR_82_exterior_closed_COLLIDER` (or a wheel),
   not `Arena_Bowl_LevelBorders/Cube (30)` as on 2026-10-21. Screenshot. West pocket: the ambulance stands at about (-38.7, -66.4)
   (`uibridge_inspect gameObject` on `Bowl_Respawn_Ambulance_Animated`, world position), the two army gate leaves are swung out
   (z rotations 142.9 and -161.3 degrees); the respawn gate leaf stands over its rail at (41.3, -1.2). `uibridge_teleport dryRun`
   from each pocket's spawn to the field (2.5, 0.0, -21.1): reachable (the park carvers do not seal an exit); `dryRun` to (49.8, 0.1, -22.0)
   (the BTR's nose): not on the mesh any more.
2. **Developer hold.** `Spawns/SpawnGateHold = true`, `SpawnGateHoldSeconds = 60`, new raid. Before 60 s: the BTR is back in its
   closed pose (x 47..55.5), the ambulance in its bay at about (-42.1, -69.8), the army gate shut, the respawn gate across its
   doorway; `dryRun` from the east spawn to the field refused. At 60 s: the BTR drives west for 7.7 s, the ambulance east/north for
   8.3 s, the army leaves swing for 10 s, the gate slides for 13.5 s (screenshots at +4 s and at the end); the lines above; 
   `uibridge_teleport dryRun` passes; read the `drive frames:` line (mean and max of the whole frame while a part is posed) and
   `uibridge_status` `max` for the same window (19 nodes move, static colliders are moved by transform; the offline model says
   +0.1 to +0.2 ms a frame, see "Cost of the drive"; the host has not confirmed it yet). Expected: mean at the raid's usual frame
   time, max within the usual spikes (about 8 ms); `Physics.autoSyncTransforms` in the line is EFT's real value. Then the same
   raid with `SpawnGateDrive = false` as the reference if the numbers look high.
3. `SpawnGateDrive = false` (live, no restart): another hold/release cycle snaps all four at once (`snapped 4 part(s) ...`).
4. Leave the raid and open the hideout once (host acceptance of a client build). Return the developer keys to their defaults.

### Open

- **The motion is a stand-in.** Position and euler angles move between the end poses under a smoothstep; Arena's curves differ per
  part (the BTR is steep in the first half, the ambulance stops at 70 % of its clip, the wheels turn at their own rate, the tow
  rope jumps at 4.58 s). Replaying the real clips (user remark 2026-10-22, "Tuerananimationen nachgestellt") needs the sidecar to
  carry a sampled curve per node (the extractor already evaluates every curve at any time); the runtime would swap the end-pose
  lerp for the sampled curves, the carvers and the state machine stay. Other maps have nothing to replay: Equator's sliding
  leaves are inferred from geometry (no Animator), Bay5 has no Animator under an opened barrier, AirPit's bus doors use a
  controller without a clip.
- **Waiting time.** With a mode plugin's Release, the last doorway (the respawn gate) opens for bots 13.5 s later, the BTR slot
  7.7 s later; `SpawnGateDrive = false` removes the wait. ArenaMode has to expect it (or choose the snap).
- **A body that drives onto a player.** The BTR, ambulance and leaves move through space that a player or bot may stand in at the
  release (the BTR's destination is the slot west of the pocket). Static colliders moved by transform push nobody out; a
  `CharacterController` inside a collider is depenetrated by EFT's movement. Not tested.
- **Carver shape.** One AABB box per mover: the respawn gate's diagonal leaf over-covers its corner, the doorway loses about 0.4 m of
  its 3.8 m (offline model: still passable, agent radius 0.3 included). Oriented boxes would be tighter; not needed so far.
- The wording "the gate sinks" was corrected on 2026-10-22 in this record ("Why", the table note, "Corrections") and in
  `docs/MAP-RECIPE.md` (3a); `docs/progress/bowl-release.md` keeps its 2026-10-21 lines as written (append-only log) and has a
  correction note and the 2026-10-22 section at its end; `HANDOFF.md`'s Bowl follow-up paragraph was corrected by the integrator
  the same day. The data in the sidecar and the descriptor is correct as it is.
