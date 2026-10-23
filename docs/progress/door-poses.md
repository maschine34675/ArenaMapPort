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

## 2026-10-22 - host evidence (GPU-2)

Verified in two raids of the 736d620 build (the full quotes are in `bowl-release.md`, section "host verification of the door poses"):
a plain raid parks all four parts at raid start with 24 park carvers after the NavMesh install and the parked BTR's rear closes its
slot's west opening (still `bowl-park-btr-slot`), the carved edge lies at x 50.5 like Arena's own mesh edge, the pockets stay
connected to the field; the developer hold re-closes the parts (28 hold carvers, every probed gate sealed, the field unreachable
from the pocket) and the release drives them to their poses in 7.7 / 8.3 / 10.0 / 13.5 s with `drive frames: 1620 frame(s) in
13.5 s, mean 8.3 ms, max 11.2 ms`, after which every probed gate is open and the field reachable again. 0 warnings; the hideout
opened once afterwards. Open: a hold during a drive, `SpawnGateDrive = false`, a player in a body's path.

## 2026-10-23 - DotAnimator poses for Equator and Bay5 (sidecar schema 2)

Status: data, descriptor, decoder and checks done on the build VM (Arena 0.6.1.0.48206). The client does not read the new rows yet
(`BoothDoorRepairs` / `SpawnGates` / `DoorPoseModel` are the runtime step of the same change set); this section is the data side
and the API the runtime reads. Research and the per-door table: [door-animations-survey.md](door-animations-survey.md); user
decisions of 2026-10-23 (German, "Spawnpunkte hinter den booth-Türen", "alle Tore aufmachen", "Timing die Hälfte"): Equator's booth
doors and Bay5's gates open with Arena's own motion, at half Arena's durations (a client key, not part of this data). This section
supersedes two lines above: "Other maps have nothing to replay" (the "Open" list of the 2026-10-22 record; Equator and Bay5 do have
data, in the DotAnimator) and the "schema 1" / `DoorPosesOf(barrier, part)` wording of "Handoff" (the sidecars are schema 2 now and
the lookup has position-aware overloads; Bowl's rows and its 2-argument lookup behave as before).

### The DotAnimator (what the extractor reads)

The sliding booth doors, Bay5's warehouse rollers and its depot gates have no Animator and no clip. Arena moves them with the
MonoBehaviour `CommonAssets.Scripts.Game.Modes.Data.DotAnimator` (DOTween based): per element one target GameObject, an end value,
a duration and an ease. `GateEntity` (44 bytes: id, float, int) sits on the gate group root and holds no motion. The tail is read
from the raw bytes (a player-built script class has no typetree):

| Part | Position | Content |
|---|---|---|
| MonoBehaviour head | 32 bytes + name | m_GameObject PPtr (int32 + int64), m_Enabled (4), m_Script PPtr, m_Name (int32 length, bytes, 4-aligned) |
| Header | 5 words | 0, 1, 0, flag, element count |
| Element | 53 words + 7 per curve key | one entry per target |
| words 0-2 | PPtr | the element's target GameObject (file 0, path id) |
| word 3 | float | -1 / -2 / 0, meaning unknown |
| word 4 | float | duration in seconds (the sidecar keeps it unscaled) |
| word 5 | int | kind: 1 move to a LOCAL position, 2 rotate to LOCAL euler angles, 3 switch (lamps, skipped) |
| word 7 | int | DOTween ease: 1 Linear, 16 InOutQuint, 37 custom curve (anything else stops the tool) |
| word 11 | int | n curve keys; then n x 7 words (time, value, inSlope, outSlope, weightedMode, inWeight, outWeight), the words 2, 2, 4, and a 38-word payload whose words 1-3 are the TARGET vector |

A decode counts only when the offset after the last field equals the object's byte length (`decode_dot_animator` raises
otherwise), the three wrap words are (2, 2, 4), the header starts 0, 1, 0, every kind is known and every number finite. On the
installed Arena every DotAnimator of the five surveyed levels passes: level 57 12 objects / 20 elements, 61 12 / 20, 88 11 / 11, 28
21 / 27, 70 4 / 4, 82 elements in all (`RealArenaDotAnimatorTests.test_the_layout_holds_on_every_dotanimator_of_the_surveyed_levels`;
the six misfits of the survey are in the tutorial level 321, not read here).

Rules the tool applies:

- **Absolute targets.** The target vector is the node's END value in its parent's frame, not an offset: a kind-1 pose is
  `closed position -> target`, a kind-2 pose `closed euler -> target euler` (the node's other component keeps its closed value).
  Evidence: Bay5's roller targets keep the node's x and z to four decimals and change only y; Equator's +-2.0 as an offset would push
  each leaf 0.58 m out of its frame. A target that differs from the closed value on all three axes gets a WARN line.
- **Closed pose** is the scene's own local transform, the euler triple taken from the quaternion in (-180, 180] and checked to
  reproduce it within 0.05 degrees (Bay5's roller (2) sits at the gimbal lock, scene euler (270, 270, 0)).
- **Skipped, with a note:** kind-3 elements (lamps), a DotAnimator whose GameObject is inactive in the hierarchy (Bay5's four
  `Gates_PTOR_medium` door leaves: variant twins that move nothing), a node whose end pose is within 1 cm / 1 degree of its closed pose.
- **Borrowed gates.** Arena authors no motion for `Door_Sliding_Respawn (3)` (Equator, level 51, under `Respawn_2`) and `Door_01 (3)`
  (Bay5). The sidecar gives them a neighbour's (source `borrowed`; the tool stops if the borrower carries a DotAnimator itself):
  Equator's (3) takes the absolute x = +-2.0 and the 5.0 s of `Door_Sliding_Respawn (1)`, Bay5's (3) the 3.089 m lift and 4.0 s of
  `Door_01 (4)` added to its own closed height (0.092 + 3.089 = 3.181).
- **Barrier / part / node** follow a per-map path depth (`DOT_SOURCES` in the tool): the first `depth` path segments are the barrier
  root, the next one the part, the rest the node. `part` is `""` when the moving node is the barrier root itself (Bay5's rollers).
- **`at`** is the WORLD position of the barrier root instance (rotation and scale of the parents included), recorded where a
  hierarchy path repeats: Equator's two unnumbered `SPAWN_DOORS/Door_Sliding_Respawn` assemblies, Bay5's two
  `OLD_WAREHOUSE_DOORS/Gates_Depot_Frame` roots, and (so a leaf can be matched by its parent's path and position) every Equator booth
  pose. The tool stops when a (barrier, part, node) occurs twice without distinct `at`.

### Schema 2 (all three sidecars; Bowl was migrated, its 19 poses are number for number the same)

Per pose: `barrier, part, node, clip, source, seconds, ease, curve, at, closed{position,euler}, open{position,euler}`.

| Key | Meaning |
|---|---|
| `source` | `clip` (Bowl, a Mecanim clip), `dotanimator` (Arena's script), `borrowed` (a neighbour's data) |
| `clip` | the clip name (Bowl), `DotAnimator`, or `borrowed from <lender barrier>` |
| `seconds` | Arena's duration, UNSCALED. The client multiplies it by the new key `Spawns/SpawnGateDriveScale` (default 0.5) |
| `ease` | `smoothstep` (the client's stand-in for Bowl's clips, only for `source: clip`), `linear`, `inOutQuint`, `curve` |
| `curve` | null, or for `ease: curve` the Hermite keys `[time, value, inSlope, outSlope]` on a 0..1 timeline from value 0 to 1 (unweighted) |
| `at` | null or the barrier root's world position; a client matches an instance within 0.05 m (`ArenaDoorPose.AtTolerance`) |

Ease formulas (k = elapsed / duration, clamped to 0..1; the same in `ease_fraction` of the tool): linear `k`; smoothstep
`k*k*(3-2k)`; inOutQuint (DOTween) with `t = 2k`: `t < 1 ? 0.5*t^5 : 0.5*((t-2)^5 + 2)`; curve: Unity `AnimationCurve.Evaluate(k)`,
a cubic Hermite between the two neighbouring keys with the left key's out slope and the right key's in slope (slopes are per unit
of the 0..1 timeline), the last value after the last key. Rotations of `dotanimator` / `borrowed` poses are meant as a quaternion
slerp between `Quaternion.Euler(closed)` and `Quaternion.Euler(open)` at the eased fraction (Bowl's clip poses keep the component-wise
euler lerp: their angles are raw clip values, including multi-turn wheel spins). A move is `closed + (open - closed) * fraction`.

### Descriptor API (`ArenaMapPort.Shared/ArenaMapDescriptor.cs`, read by the client)

`ArenaDoorPose` gained `Source`, `Ease`, `Curve` (`float[]?`: a FLAT array, 4 floats per key in the order time, value, inSlope,
outSlope; null unless `Ease == "curve"`), `At` (`float[]?`, 3 floats or null), `CurveKeyCount`, `AppliesTo(x, y, z)` (At null = every
instance, otherwise within `AtTolerance` = 0.05 m) and the string constants `SourceClip/SourceDotAnimator/SourceBorrowed`,
`EaseSmoothstep/EaseLinear/EaseInOutQuint/EaseCurve`. The constructor takes them as four optional trailing parameters (defaults
`"clip"`, `"smoothstep"`, null, null), so Bowl's nine-argument rows are unchanged. Lookup:

| Call | Returns |
|---|---|
| `DoorPosesOf(barrier, part)` | the poses of that part that hold for EVERY instance of the path (`At == null`); never a pose with an `At` |
| `DoorPosesOf(barrier, part, x, y, z)` | the poses of that part for the barrier root instance standing at the world position: `At == null` or within 0.05 m. `part == ""` asks for the barrier root itself |
| `DoorPosesOfBarrier(barrier)` / `DoorPosesOfBarrier(barrier, x, y, z)` | every pose of the barrier, any part, same filter; non-empty means Arena animates something of this barrier (its parts without a pose of their own are structure that stays) |

All return sidecar order, empty when nothing matches. For Equator a booth leaf is matched by its parent's hierarchy path plus the
parent's world position (`DoorPosesOf(rootPath, leaf.name, root.position.x, .y, .z)`); the booth roots are not `OpenedBarriers`
entries (the validators know this exception, below).

### Equator: 10 rows (`tools/maps/equator.doors.json`, scene `Arena_Equator_TeamFight_DesignStuff` = level 57, one row from level 51)

All `ease: inOutQuint`, `part` = the leaf, `node` = "", local x to +-2.0 (y, z unchanged), world y of every `at` 0.048.

| Barrier (`at` = the root's world position) | Leaf | closed x -> open x | seconds | source |
|---|---|---|---|---|
| `SBG_Arena_Equator/SOO/Respawn_2/Door_Sliding_Respawn (3)` (-3.753, 0.048, -45.870) | L / R | 0.683 -> 2.0 / -0.686 -> -2.0 | 5.0 / 5.0 | borrowed from `SPAWN_DOORS/Door_Sliding_Respawn (1)` |
| `SPAWN_DOORS/Door_Sliding_Respawn` (-4.328, 0.048, 40.020) | L / R | 0.717 -> 2.0 / -0.709 -> -2.0 | 5.0 / 5.0 | dotanimator |
| `SPAWN_DOORS/Door_Sliding_Respawn` (4.535, 0.048, 40.020) | L / R | 0.709 -> 2.0 / -0.722 -> -2.0 | 5.0 / **7.0** | dotanimator |
| `SPAWN_DOORS/Door_Sliding_Respawn (1)` (-4.460, 0.048, -35.045) | L / R | 0.711 -> 2.0 / -0.722 -> -2.0 | 5.0 / 5.0 | dotanimator |
| `SPAWN_DOORS/Door_Sliding_Respawn (2)` (4.403, 0.048, -35.045) | L / R | 0.714 -> 2.0 / -0.706 -> -2.0 | 5.0 / 5.0 | dotanimator |

Travel 1.278 to 1.294 m (borrowed 1.314 / 1.317). The 7.0 s leaf is the R leaf of the assembly at x 4.535 (north booth, east side;
GateEntity 24002 in the survey). The north booth is the z = 40 pair, the south booth the z = -35 pair; door (3) stands at the far
end of the south booth.

### Bay5: 8 rows (`tools/maps/bay5.doors.json`, scene `Arena_Bay5_TeamFight_DesignStuff` = level 88)

Rollers lift their whole root (part `""`, `ease: linear`, 4.0 s, only local y changes, `at` null):

| Barrier | closed y -> open y | lift | source |
|---|---|---|---|
| `WAREHOUSE_DOORS (1)/Bay5_Warehouse_Gate_Door_01 (2)` | 0.130 -> 3.222 | 3.092 | dotanimator |
| `WAREHOUSE_DOORS (1)/Bay5_Warehouse_Gate_Door_01 (3)` | 0.092 -> 3.181 | 3.089 | borrowed from `(4)` |
| `WAREHOUSE_DOORS (1)/Bay5_Warehouse_Gate_Door_01 (4)` | 0.124 -> 3.213 | 3.089 | dotanimator |
| `WAREHOUSE_DOORS (1)/One_man_Door` | 0.150 -> 3.323 | 3.173 | dotanimator |

The depot gates (`OLD_WAREHOUSE_DOORS/Gates_Depot_Frame`, two instances; barrier = the frame root, part = `Gates_Depot_Door_L` /
`_R`, node = `<part>_Leaf`; 7.0 s, `ease: curve`, position unchanged, rotation about the hinge: local euler z only):

| `at` (frame root) | Leaf | closed z -> open z | curve value / slope at t = 0.4 |
|---|---|---|---|
| (50.574, -3.895, 7.525) | L | 3.8 -> 162.432 | 0.79929 / 0.89601 |
| (50.574, -3.895, 7.525) | R | 0.0 -> -141.759 | 0.80000 / 0.88513 |
| (69.045, -3.883, -6.317) | L | 0.0 -> 138.837 | 0.80016 / 0.99404 |
| (69.045, -3.883, -6.317) | R | -2.042 -> -98.974 | 0.80194 / 0.88599 |

Every curve is `(0, 0, slope 2), (0.4, ~0.8, slope s), (1, 1, slope 0)`: about 80 % of the swing in the first 40 % of the time
(2.8 s of 7.0), then a long ease-out. The Telfer_Help posts and the frame stay (a barrier with pose data keeps every part without a
pose standing).

### Verification (build VM, 2026-10-23)

- `python tools/extract/extract_door_poses.py --map arena_equator --package work/extract/equator-native-v5 --output tools/maps/equator.doors.json --csharp`
  -> `decode self-check: 8 DotAnimator object(s), 8 element(s); every decode ends exactly at the object's byte length ...`,
  `package check: 10 pose(s) exist in equator-native-v5 with Arena's closed transform (repeated paths told apart by 'at')`,
  `poses 10: 8 dotanimator, 2 borrowed`. Bay5: `decode self-check: 11 DotAnimator object(s), 11 element(s)` (all 11 of level 88: three
  rollers, the four depot leaves and the four inactive PTOR leaves), `package check: 8 pose(s) exist in bay5-native-v5 ...`,
  `poses 8: 7 dotanimator, 1 borrowed`. Bowl, regenerated with the same tool: 19 poses, equal to the 2026-10-22 sidecar apart from the
  four new default fields.
- Byte-stable: a second run and a run without `--package` write identical text (`RealArenaDotAnimatorTests`).
- The C# rows are the tool's `--csharp` output pasted into `ArenaMapRegistry.Equator` / `.Bay5` (a test pins that the registry
  contains `render_csharp(poses)` verbatim); `validate_map_descriptors.py` and `DoorPoseSidecarTests` check sidecar and registry
  number for number, curve and `at` included.
- Validator rules (`door_pose_errors`): schema 2; `source` / `ease` / `curve` / `at` as above (smoothstep only for clips; a curve
  exactly when ease is `curve`, 0..1 in time and value); `part` may be "" but a node needs a part; no `frame` part; duplicates and
  repeated paths need distinct `at` (> 0.05 m), and one barrier either has `at` on all its poses or on none; **every pose barrier is
  an `opened_barriers` entry, except Equator's booth assembly roots** (`SPAWN_DOORS/Door_Sliding_Respawn[ (n)]` and the one under
  `Respawn_2`), which BoothDoorRepairs opens by the NAME of the leaves.

### Limits

- Not in the data: the trigger, the closing motion (reverse or a second tween) and word 3 / words 8-10 of an element. The duration and
  ease words are plausible and self-consistent (no Arena level has a DotAnimator next to an Animator that could confirm them); the
  absolute reading of the targets is fixed by geometry, not by a second source. A host look at the final leaf positions is the check
  (Equator +-2.0 local x, Bay5 roller y 3.213).
- The two borrowed Equator leaves and Bay5's `Door_01 (3)` are OUR choice built on the neighbours' numbers, marked as such.
- Re-run the tool after an Arena update; the real-data tests fail when the installed Arena yields other poses than the sidecars.
- `Door_01 (2)` and `One_man_Door` are poses only until they are `opened_barriers` entries (bake and resolvers); the duplicate
  depot gate (go 822) likewise needs the NavMesh bake and the resolvers to address both instances.

## 2026-10-23 - Runtime 2: the client drives the DotAnimator poses (Equator, Bay5) and every drive runs at a timing scale

Status: built and unit-tested on the build VM (client `dotnet build -c Release -p:ArenaMapPortDllOnly=true`: 0 warnings, 0 errors;
`dotnet test ArenaMapPort.Server.Tests -c Release`: 2215 passed, 0 failed), NOT yet run in a raid. The data side (sidecar schema 2,
the rows, the lookup API) is the section above; the NavMesh bake side (which parts the bake drops) is the map-data change of the same
change set. User decisions 2026-10-23: Equator's spawns behind the booth doors and the FCK shutter shut again, all Bay5 gates open with
Arena's own motion, "the timing can be faster than the original, half is probably enough".

### What the client does now (files: `BoothDoorRepairs`, `SpawnGates`, `DoorPoseModel`, `SpawnGateModel` doc only)

- **Lookup by instance.** A pose row with an `at` belongs only to the barrier-root instance whose WORLD position is within 0.05 m
  (`ArenaDoorPose.AtTolerance`); rows without an `at` belong to every instance. The client always asks with the position:
  `map.DoorPosesOf(path, part.name, at.x, at.y, at.z)`, `DoorPosesOf(path, "", at.x, at.y, at.z)` for the root and
  `DoorPosesOfBarrier(path, at.x, at.y, at.z)` for "does Arena animate anything here". The 2-argument overloads (Bowl's, no position)
  never return a row that has an `at`.
- **Every instance of an `opened_barriers` path opens.** `BoothDoorRepairs.FindAllByPath` resolves the path with
  `DoorPoseModel.ResolveAll` (all children of all matches at every segment, in tree order) instead of `Transform.Find` (first match per
  level). Bay5's `OLD_WAREHOUSE_DOORS/Gates_Depot_Frame` exists twice (world (50.574, -3.895, 7.525) and (69.045, -3.883, -6.317)); both
  open now, the Debug line says `2 instance(s) share this path`. Offline check on the packages: every other `opened_barriers` path of
  AirPit and Bowl has exactly one instance (AirPit 12 of 12, Bowl 2 of 2), so nothing there changes. `FindByPath` (first match) stays for
  `LampFillRepairs`.
- **Booth leaves (Equator).** `OpenDoors` still finds `Door_Sliding_Respawn_L/_R` by name; the leaf's parent is the door assembly, whose
  hierarchy path (`HierarchyPath`: scene-root name down, no scene name) plus world position select the row (`SPAWN_DOORS/Door_Sliding_Respawn`
  exists twice, told apart by x -4.328 / 4.535). The leaf then goes the Bowl way: `NotePoses` (while closed), `Offer` (a hold keeps it
  closed), `Park` (snaps to the open pose, stays active), `SetActive(false)` only for a leaf without a row. A leaf without a row on a map
  that has poses is one Warning (`N sliding-door leaf(s) have no door pose in the descriptor ...`), then the old switch-off. All ten
  Equator leaves have rows, so the 1.38 m slide (`SpawnGateModel.SlideDistance`, `Spawns/SpawnGateSlideSeconds`) now moves no leaf of the
  shipped maps; it stays as the fallback and the key keeps its default and its meaning (for a pose-less leaf), only the description says so.
- **Root poses (Bay5's rollers).** A row with `part == ""` moves the barrier root itself: `OpenBarriers` calls `NotePoses(barrier,
  rootPoses)`, asks `HasPoses(barrier)` (false when the data did not fit, one Warning), then `OfferHeld` / `Offer` / `Park` with the root as
  the part, and leaves the root's children alone (`for (...; !rootDone; ...)`). A hold re-closes the root, a release drives it.
- **Kept parts.** A barrier that has ANY row for this instance keeps every part without a row of its own standing and untouched
  (`standing` counter, Debug line `N part(s) without a pose left standing`): Bay5's `Telfer_Help` posts and the frame meshes of the depot
  gates. A barrier without rows keeps the old rule (hide every non-frame part: AirPit, Bowl's un-posed siblings do not exist). A root
  whose pose failed to apply falls back to the old rule for that barrier. Bowl: every direct child of its two barriers has rows, so
  `standing` is 0 and the opening is unchanged.
- **Eases.** `DoorPoseModel.EaseOf(ease, curve, k)`: `smoothstep` (Bowl, as before), `linear`, `inOutQuint` (DOTween's: `t = 2k`, `0.5 t^5`
  below 1, `0.5 ((t-2)^5 + 2)` above) and `curve` (Unity AnimationCurve evaluation of the stored keys: cubic Hermite with the left key's out
  slope and the right key's in slope, first/last value outside). Both ends are exact for every ease (0 and 1 are the data bit for bit),
  `NotePoses` refuses an ease it cannot evaluate (`EaseIsUsable`: known name, a curve of at least two finite keys with ascending times) with
  the usual one Warning and the switch-off.
- **Rotation.** `source` `dotanimator` / `borrowed`: `Quaternion.Slerp(Quaternion.Euler(closed), Quaternion.Euler(open), k)` (precomputed
  per node; exact endpoints), and a node whose two euler triples are equal is not written at all (Bay5's rollers sit at the gimbal lock
  scene euler (270, 270, 0) and only lift; the Equator leaves only slide). `source` `clip` (Bowl): the component-wise euler lerp, written
  as before, so the BTR's wheels keep their 1.5 revolutions. The data test pins that every DotAnimator row turns about ONE axis by less
  than 180 degrees (the four depot leaves: 158.6, 141.8, 138.8, 96.9), where a slerp and an euler lerp are the same turn.
- **Timing.** New key `Spawns/SpawnGateDriveScale` (float, default 0.5, range 0.1 to 1.0): the drive seconds of every part are
  `row.seconds x scale`, read once when a release starts (`PosedPart.DriveSeconds`), for EVERY map, Bowl included (its verified 7.7 / 8.3 /
  10.0 / 13.5 s become 3.8 / 4.2 / 5.0 / 6.75 s). `Spawns/SpawnGateDrive = false` still snaps. Equator's leaves take 2.5 s (the slow
  leaf 3.5 s), Bay5's rollers 2.0 s, its depot gates 3.5 s. The release line gains `(scale 0.50)`.
- **Park carvers.** A moved collider gets a carving box only if it occupies the walking band: its top above the part's floor line + 0.3 m
  (as before) AND its bottom below the floor line + 1.8 m (`DoorPoseModel.OccupiesWalkingBand`, new). The skipped ones are counted in the
  `parked '<part>'` line (`N collider(s) above the 1.8 m walking band skipped`). The probe logic is unchanged.
- **Config descriptions** (meaning kept, no default changed): `OpenRespawnDoors`, `OpenSpawnBarriers`, `SpawnGateCarve`,
  `SpawnGateSlideSeconds`, `SpawnGateDrive`. New keys: `SpawnGateDriveScale` only.

### Offline prediction of the park carvers (world AABB of every movement collider at the open pose, from the extract packages)

Script and method: the Bowl numbers reproduce the 2026-10-22 host count (10 / 1 / 11 / 2 = 24) with the old rule, so the model is the
host's; the table is the same model with the new rule (floor line = the lowest closed movement collider of the part).

```
Equator  10 booth leaves     1 collider each, 0 .. 2.05 m above the floor line      -> 10 park carvers (1 per leaf)
         open leaf boxes: x -3.01..-1.64 / -7.02..-5.65 for the assembly at (-4.33, 40.02): 2.64 m of doorway between the pair
Bay5     rollers (2) (3) (4)  1 collider each, 3.09 .. 6.56 m above the floor line   -> 0 carvers, "1 collider(s) above the 1.8 m walking band skipped"
         One_man_Door         2 colliders, 3.17 .. 4.75 and 4.72 .. 6.65 m          -> 0 carvers, "2 collider(s) ... skipped"
         depot gate 821  L    leaf 0 .. 4.22 m + wicket 0.08 .. 2.55 m (x 52.9..55.3, z 7.75..8.57) -> 2 carvers
                         R    leaf 0 .. 4.37 m (x 46.18..48.28, z 7.58..9.30)       -> 1 carver
         depot gate 822  L    leaf + wicket (x 69.3..70.9, z -10.5..-8.6)           -> 2 carvers
                         R    leaf (x 69.1..71.6, z -4.05..-3.42)                   -> 1 carver     (6 park carvers on Bay5)
Bowl     BTR 10 -> 9 (BTR_82_turret_COLLIDER lies 2.09 .. 3.02 m above the floor line: above everyone's head), respawn gate 1,
         ambulance 11, army gate 2                                                   -> 23 park carvers (24 before)
```

The Bowl change (24 to 23) is the one visible difference to the host-verified 2026-10-22 behaviour besides the timing scale; the turret
box was floating over the body's own box, no route changes.

### Log lines to expect on the host (Debug, source `maschine-ArenaMapPort`)

Equator, plain raid (no hold): ten `park:` lines (`'Door_Sliding_Respawn_L'` / `_R`, `1 node(s), clip DotAnimator`, two of them
`clip borrowed from SPAWN_DOORS/Door_Sliding_Respawn (1)`), `[RaidFix] spawn openings:` with `respawn doors: deactivated 0 sliding-door
leaf(s) on 5 door assembly(s), parked 10 leaf(s) at the open pose` and NO `spawn barrier` line (Equator lists no barrier any more), then
after the NavMesh install ten `parked 'Door_Sliding_Respawn_L': 1 node(s) at the open pose (clip DotAnimator), 1 park carver(s) (navmesh
installed)` lines. No Warning (a `have no door pose in the descriptor` Warning would mean an assembly's position or path differs).
Developer hold (`Spawns/SpawnGateHold = true`, `SpawnGateHoldSeconds = 60`): `held closed`, `navmesh installed: 10 carver(s) over 10 movement
collider(s) of 10 gate part(s)`, then at the release `released (developer test hold, 60.x s after the raid timer started): 0 leaf(s) slid 1.38
m in 1.0 s, 0 gate part(s) switched off, 10 carver(s) removed, drove 10 part(s) to the open pose in 3.5 s (scale 0.50), 10 park carver(s) in place`
(nine leaves finish at 2.5 s with their `parked ... (released)` line, the 7 s leaf at 3.5 s) and `drive frames: ...`.

Bay5, plain raid: eight `park:` lines (`'Bay5_Warehouse_Gate_Door_01 (2)'` ... `'One_man_Door'`, `'Gates_Depot_Door_L'` x2, `'Gates_Depot_Door_R'` x2),
`spawn barrier 'WAREHOUSE_DOORS (1)/...Gates_Depot_Frame': 2 instance(s) share this path - opening all of them`, one `spawn barrier '<name>' at
(x, y, z): deactivated 0 part(s), 0 already off, kept N frame part(s), M part(s) without a pose left standing, parked 2 part(s) at the open pose` per
depot gate (a roller: `... parked 1 part(s) at the open pose`), then the `parked` lines with the carver counts of the table above.

### Host recipe (GPU-2, client DLL deployed and EFT restarted; the Equator and Bay5 bundles of the same change set deployed)

1. `uibridge_status` (loaded DLL = VM build), `uibridge_help topic=overview`. Equator, bots None, clear weather. Hideout opened once at the end.
2. **Equator plain raid.** The lines above, 0 Warnings. `uibridge_inspect gameObject` on the leaves of `SPAWN_DOORS/Door_Sliding_Respawn`
   (world (-4.328, 0.048, 40.020)): `localPosition.x` = 2.0 (L) and -2.0 (R), the same on the assembly at (4.535, 0.048, 40.020), on
   `Door_Sliding_Respawn (1)` / `(2)` (z -35.045) and on `Respawn_2/Door_Sliding_Respawn (3)`. Spawn behind the booth doors: walk out
   (`uibridge_walk` to the hall), screenshot of an open door.
3. **Equator drive.** `SpawnGateHold = true`, `SpawnGateHoldSeconds = 60`, new raid: before 60 s the leaves stand closed (x 0.683 .. 0.722 /
   -0.686 .. -0.722), the hold carvers seal the booth (`uibridge_teleport dryRun` out of the booth refused); at 60 s read the leaf x at +1.25 s
   (half of 2.5 s: InOutQuint(0.5) = 0.5, so x is midway: L 0.717 -> 1.3585) and at +2.5 s (9 leaves exactly +-2.0; the R leaf of the assembly
   at x 4.535 at +3.5 s); `released ... drove 10 part(s) ... in 3.5 s (scale 0.50)`; the dryRun passes. Then `SpawnGateDriveScale = 1.0`
   (live, next release): another cycle takes 7.0 s (`scale 1.00`).
4. **Bay5 plain raid.** Roller `localPosition.y` (inspect `WAREHOUSE_DOORS (1)/Bay5_Warehouse_Gate_Door_01 (4)`): 3.213; (2) 3.222; (3) 3.181;
   `One_man_Door` 3.323; world bottom of (4) about y 0.21. Screenshot at the slab against the wall (the open slab overlaps the wall plane and
   rises about 2.8 m above the frame header; look for clipping through the ceiling). Depot gates: both `Gates_Depot_Door_L_Leaf` / `_R_Leaf`
   `localEulerAngles.z` = 162.432 / -141.759 (gate at x 50.574) and 138.837 / -98.974 (gate at x 69.045), `Telfer_Help` posts still active.
   `uibridge_teleport dryRun` through each doorway (rollers, depot) once the new Bay5 NavMesh bundle is live.
5. **Bay5 drive.** Developer hold as in 3: the release logs `drove 8 part(s) to the open pose in 3.5 s (scale 0.50), 6 park carver(s) in place`;
   screenshots at +1 s and +3.5 s of a depot gate (80 % of the swing happens in the first 40 % of the time) and of a roller at +1 s.
6. **Bowl regression at the new default.** Plain raid: `park:` x4, `parked 'BTR_82_closed': 9 node(s) ... 9 park carver(s) ...; 1 collider(s) above the
   1.8 m walking band skipped`, 23 park carvers in all; developer hold: `drove 4 part(s) to the open pose in 6.8 s (scale 0.50), 23 park carver(s) in place`,
   `drive frames: ...` (mean at the raid's usual frame time, max within the usual spikes).
7. Return the developer keys to their defaults; `SpawnGateDrive = false` once: `snapped N part(s)` on each map.

### Open risks

- Nothing of this has run in a raid: the DOTween ease numbering and the absolute-target reading are the decoder's (plausible and
  self-consistent, fixed by geometry), the Hermite evaluation and the slerp are unit-tested but not looked at on screen.
- The Equator leaves park in their frame pockets and each gets a park carver (box = bounds + 0.2 m per side). The offline model leaves 2.64
  m of doorway between the two open leaves of an assembly; the post-release probe and a `dryRun` through a booth door are the check.
- The two leaves of an Equator assembly finish at different times in one case (R leaf 3.5 s, L leaf 2.5 s), and a door counts as open for
  bots only when both hold-carver sets are gone.
- A body driving through space where a player or bot stands pushes nobody (depot leaves swing over 2 m of floor, 3.5 s at the default).
- Bowl's drive times halve with the new default (user decision "Bowl included"); its pocket exits open sooner in a mode plugin's release.

## 2026-10-23 - NavMesh bake (BakeNavMesh, ArenaPaths)

Status: code and tests done on the build VM; no Unity run yet (the integrator rebuilds the Equator and Bay5 NavMesh bundles), no host
evidence. Files: `UnityProject/Assets/ArenaMapPort/Editor/NavMesh/BakeNavMesh.cs`, `Editor/Common/ArenaPaths.cs`,
`tools/tests/test_navmesh_source_layers.py`, `tools/tests/test_airpit_bake_inputs.py`.

### Why

The bake dropped every non-frame descendant of an `opened_barriers` path, because the client used to switch those parts off. Since the
door-pose change set the client parks or drives only the POSED parts and keeps every other part standing, so for the barriers whose
parts Arena moves the old rule was wrong in four places:

- Bay5's depot gate posts (`Telfer_Help`, a Collider on layer 18 and a ballistic twin on layer 12 each) are solid for the player but were air
  for bots (the survey's B7).
- The second depot gate (`Gates_Depot_Frame` at world (69.045, -3.883, -6.317)) was never found: `Transform.Find` takes the first child
  of a name, so only the gate at (50.574, -3.895, 7.525) was opened and baked open (bay5-arenamode-integration.md).
- `Door_01 (2)` and `One_man_Door`, which Arena opens, were not listed and so baked as walls.
- Equator's FCK hall shutter, which the user wants shut again, was dropped from the bake.

### The rule

`BakeNavMesh.ResolveOpenedBarriers` splits `opened_barriers` using the rows of `tools/maps/<stem>.doors.json` (`ArenaPaths.DoorRows`):

1. **A path without rows** keeps today's rule (`RoundOpenedReason`): every non-frame descendant goes, the barrier object stays. Nothing
   changes for AirPit (it has no doors sidecar, so its 2026-10 barriers behave exactly as before).
2. **A path with rows** drops only the subtrees that move, the object itself and everything below it whatever it is called: per
   barrier root instance and per applicable row, the barrier root itself for `part == ""` (Bay5's rollers and the one-man door), else the
   barrier root's direct child `part`. Several rows on one part (a vehicle's wheels) count once. Every other part is baked as the
   wall it is, frame-named or not: Bay5's posts and frames.
3. **Instances.** A row without `at` holds for every instance of its path and the path is resolved as before (`Transform.Find`, the
   first child per level, in every content-scene root of that name). If any row of a path has `at`, ALL instances of the path are
   enumerated (`FindAllInstances`: every root of the first name, every child of the next name per level, inactive ones included) and a
   row applies to an instance whose WORLD position is within 0.05 m (Euclidean; `ArenaDoorPose.AtTolerance`) of the row's `at`.
   Bay5's depot path therefore opens both gates: four moved parts, `Gates_Depot_Door_L` and `_R` of each.
4. **Failures.** A path that resolves to nothing, a row whose `at` matches no instance and a `part` the instance does not have all
   fail the build (the list goes through `FailOnUnresolvedPaths("opened_barriers", ...)`, like a missing path since 2026-10-11): a
   row the bake skipped would leave the part baked as a wall while the client moves it. An instance that no row applies to (a third
   depot gate the sidecar does not know) is left as a wall and logged as a Warning; it is neither moved nor legacy-dropped.
5. **Rows of unlisted barriers are not read.** Equator's booth assemblies have rows (`SPAWN_DOORS/Door_Sliding_Respawn`, ...) but are
   not `opened_barriers` entries; their leaves are dropped by NAME (`Door_Sliding_Respawn_L/_R`, unchanged: 20 colliders, five
   assemblies x two leaves x COLLIDER + BALLISTIC). The leaves are never bake sources, whether the client keeps them closed (a hold:
   the hold carvers seal the doorway) or parks them at the open pose (the park carvers seal the parked bodies).
6. **Equator has no opened barrier any more** (`equator.json`: `"opened_barriers": []`; `EquatorDefault()` in `ArenaPaths.cs` lists
   none either). The FCK shutter `SBG_Arena_Equator/SOO_LOD0/equator_roller_shutters_02 (9)` is therefore an ordinary wall; its two
   colliders that used to be dropped (`roller_shutters_02_COLLIDER` on 18, `roller_shutters_02_BALLISTIC_metalthin` on 12) are sources
   again. No code change was needed for that, only the empty list; the bake logs it (below).

**Per part, not per node.** The finer rule (drop only the animated nodes, bake what else sits inside a posed part) is not used on
purpose: it would be exact for Bay5 and Equator, but for Bowl it would turn the army gate's frame, its two `Telfer_Help` posts and
the respawn gate's rail (all inside posed parts, none of them animated) into walls. Every direct child of `DOORS_SPAWN_A/B` is a posed part, so
the per-part rule drops EXACTLY the owners the old non-frame rule dropped (75 of 75, checked on `bowl-native-v2` by
`test_bowl_keeps_exactly_the_colliders_it_dropped_before`), and the host-verified Bowl NavMesh is not changed by a future rebuild.

`ArenaPaths.DoorRows` loads `<descriptor stem>.doors.json` next to the `-mapDescriptor` file (`bay5.json` -> `bay5.doors.json`; nothing
without the flag or the file), requires schema 2 and the descriptor's own `key`, keeps `barrier`, `part`, `node` and `at`, and throws on
a malformed file (a bake that silently ignored its rows would drop a different set than the client moves). It uses Newtonsoft
(`JObject`), because `JsonUtility` cannot read the sidecar's nested curve arrays and its treatment of `null` (`"at": null`) is not
something to bet a 15-minute bake on.

### Expected log lines of the rebuilds (Unity log; the leading `[ArenaMapPort]` of every line is left out)

Equator (`build_all.ps1`, no `-FullMap`, `equator.json`; the counts are the 2026-10-20 build's, `opened_barriers` line gone):

```
[ArenaPaths] loaded 10 door pose row(s) from D:/ArenaMapPort/tools/maps/equator.doors.json
[BakeNavMesh] opened_barriers: none; only SPAWN_BOUND*/LevelBorder objects and the booth leaves are dropped by name, so every shutter and gate collider (Equator's FCK hall shutter included) is baked as a wall
[BakeNavMesh] excluded 2 nonphysical or round-open source(s): SPAWN_BOUND*
[BakeNavMesh] excluded 41 nonphysical or round-open source(s): LevelBorder layer 29
[BakeNavMesh] excluded 10 nonphysical or round-open source(s): Door_Sliding_Respawn_L
[BakeNavMesh] excluded 10 nonphysical or round-open source(s): Door_Sliding_Respawn_R
        (no "opened_barriers:equator_roller_shutters_02 (9)" line: that was 2)
[BakeNavMesh] NavMesh modifiers: 31 markup(s) in bake scope (0 area-override, 31 ignoreFromBuild), 0 volume source(s) added ...
[BakeNavMesh] baked NavMeshData: ... sources=4106 (root=null collected 4169 total, 4106 kept ...)        (was 4104)
[BakeNavMesh] bake inputs: bounds from the renderer union, layer mask 0xFFFFFFEF (every layer but Water), 0 navmesh_bake_exclusions, 0 opened_barriers (0 with door poses: 0 moved subtree(s))
```

Bay5 (`-FullMap`, `bay5.json`; `collected` stays 10583 plus nothing new, `kept` rises by the net -8):

```
[ArenaPaths] loaded 8 door pose row(s) from D:/ArenaMapPort/tools/maps/bay5.doors.json
[BakeNavMesh] opened_barriers: 5 path(s); 5 with door poses (8 row(s) loaded) drop 8 moved subtree(s) [Bay5_Warehouse_Gate_Door_01 (2), Bay5_Warehouse_Gate_Door_01 (3), Bay5_Warehouse_Gate_Door_01 (4), One_man_Door, Gates_Depot_Door_L, Gates_Depot_Door_R, Gates_Depot_Door_L, Gates_Depot_Door_R] and bake every other part as a wall; 0 without door poses drop their non-frame parts
[BakeNavMesh] excluded 200 ...: LevelBorder layer 29            (unchanged)
[BakeNavMesh] excluded 2 ...: SPAWN_BOUND*                      (unchanged)
[BakeNavMesh] excluded 2 ...: door_poses:Bay5_Warehouse_Gate_Door_01 (2)      (new)
[BakeNavMesh] excluded 2 ...: door_poses:Bay5_Warehouse_Gate_Door_01 (3)      (was opened_barriers:..., 2)
[BakeNavMesh] excluded 2 ...: door_poses:Bay5_Warehouse_Gate_Door_01 (4)      (was opened_barriers:..., 2)
[BakeNavMesh] excluded 4 ...: door_poses:One_man_Door                         (new)
[BakeNavMesh] excluded 8 ...: door_poses:Gates_Depot_Door_L                   (two gates x leaf + wicket panel x 2 colliders)
[BakeNavMesh] excluded 4 ...: door_poses:Gates_Depot_Door_R                   (two gates x 2 colliders)
        (no "opened_barriers:Gates_Depot_Frame" line: that was 10)
[BakeNavMesh] NavMesh modifiers: 109 markup(s) in bake scope (81 area-override, 28 ignoreFromBuild), 134 volume source(s) added ...   (unchanged)
[BakeNavMesh] baked NavMeshData: ... sources=10493 (root=null collected 10583 total, 10493 kept ...)        (was 10501)
[BakeNavMesh] bake inputs: bounds from the renderer union, layer mask 0xFFFFFFEF (every layer but Water), 0 navmesh_bake_exclusions, 5 opened_barriers (5 with door poses: 8 moved subtree(s))
```

The order of the per-reason lines follows the scene order of the collected sources (a `Dictionary`), so only the set of lines and the
numbers are fixed. The moved-subtree names are printed in `opened_barriers` order, depot instances in scene order. No Warning
`matches no door pose row` may appear; if the reconstructed log lists a different set, the sidecar or the package drifted.
Triangulation counts change by the few polygons around the new doorways and the closed shutter; the old ones (Equator
10404 / 4572, Bay5 22117 / 10213) are the 2026-10-20 builds, not a pin.

### What changes, collider by collider (offline model over the extracted packages)

The model (`BarrierBake` in `tools/tests/test_airpit_bake_inputs.py`, `port_colliders` underneath) reproduces the last real logs for the
old configuration: Equator 4169 collected / 4104 sources and the five per-reason counts of the 2026-10-20 build, Bay5 10367 sources
+ 134 modifier volumes = the log's 10501 and 14 barrier colliders (2 + 2 + 10).

Bay5, the five listed barriers (colliders = enabled, non-trigger, active):

| Barrier / part | Colliders | Old bake | New bake |
|---|---|---|---|
| `Door_01 (2)` (root moves) | 2 (L18 COLLIDER, L12 BALLISTIC) | wall (not listed) | dropped |
| `Door_01 (3)`, `Door_01 (4)` (roots move) | 2 + 2 | dropped | dropped |
| `One_man_Door` (root moves; top and bottom panel) | 4 (2 x L18, 2 x L12) | wall (not listed) | dropped |
| depot gate 821 (x 50.574): `Gates_Depot_Door_L`, `_R` | 4 + 2 (leaf, wicket panel and ballistic twins; right leaf) | dropped | dropped |
| depot gate 821: `Telfer_Help (2)`, `(3)` posts | 4 (2 x L18, 2 x L12) | dropped | **wall** |
| depot gate 821: `Gates_Depot_Frame_COLLIDER` (L18) and `_BALLISTIC_metalthick` (L12) | 2 | wall (frame rule) | wall |
| depot gate 822 (x 69.045): `Gates_Depot_Door_L`, `_R` | 4 + 2 | wall (instance never resolved) | dropped |
| depot gate 822: `Telfer_Help`, `(1)` posts and the frame | 4 + 2 | wall | wall |

Totals: barrier colliders dropped 14 -> 22; newly walls 4 (the 821 posts), newly dropped 12 (822 leaves 6, `Door_01 (2)` 2,
`One_man_Door` 4); model sources 10367 -> 10359. The eight posts and both frames (12 colliders) are sources in the new bake.
No `NavMeshModifier` or volume sits under any moved subtree. Equator: 4104 -> 4106 sources, the two shutter colliders above.

### Evidence (build VM, 2026-10-23)

- `python -m unittest discover -s tools/tests -p "test_navmesh_source_layers.py"`: 7 tests OK. The harness compiles the PRODUCTION text of
  `NonPhysicalLayerReason`, `RoundOpenedReason`, `BakeOnlyExclusionReason`, `SourceLayerMask`, `UsesExplicitBakeBounds`,
  `DoorRowAppliesTo`, `FormatDoorPosition`, `FindAllInstances`, `ResolveOpenedBarriers`, `ResolveHierarchyPaths` and `ArenaDoorRow`
  (from `ArenaPaths.cs`) on a stand-in hierarchy and prints `NAVMESH_SOURCE_FILTER_PASSED cases=292` (179 before). New cases: the
  depot (two instances, `at` told apart, posts and frames stay, a frame-named object inside a moved leaf goes), rollers with part "",
  a vehicle with several rows per part, mixed lists, the old rule next to the new, an instance without a row (Warning), stale rows,
  a missing part, a missing path, `at` tolerance 0.03 m in / 0.0566 m out, nested duplicate paths, Equator's empty list with the
  shutter as a wall and the booth leaves by name. Negative controls (mutating the production text must turn the run red): the
  moved-subtree loop removed, the `at` match always true; a scratch run also checked dedup, the uncovered-instance Warning, the
  part lookup and `FindAllInstances` replaced by `Find` (each fails a named case).
- `python -m unittest discover -s tools/tests -p "test_airpit_bake_inputs.py"`: 36 tests OK, including 15 new ones (the sidecar
  pins, the Python mirror on a synthetic depot, and the real-package runs: model = last real logs, Equator, Bay5 and the Bowl
  no-change check).
- The loader (`DoorSidecarPath`, `ParseDoorRows`, production text) was run once outside the repo with the .NET SDK's own
  `Newtonsoft.Json.dll` standing in for the editor's: it reads all three real sidecars (Equator 10 rows, all with `at`; Bay5 8 rows, 4
  with `at`, 4 rollers with part ""; Bowl 19 rows, none with `at`) and rejects a wrong key, schema 1, a missing schema, a non-array
  `poses`, an empty barrier, a null part, a missing node, a two-number `at` and a string `at` with a message naming the file.
- `validate_map_descriptors.py` prints OK with the changed `EquatorDefault()`.
- Spawn markers (a check for the booth decision): all 20 of Arena's TeamFight markers (tools/maps/equator.modes.json) lie on the
  current `work/build/full/arena_equator_navmesh.bundle` (10 of 10 north, 10 of 10 south) and in its largest component; the booth
  interiors need no bake change, and `equator.bots.json` has no spawn west of the shutter plane.
- Not run: Unity itself. The extracted C# compiles in the harness project (Unity types stubbed, `LangVersion 9.0` = the language level of
  the 2022.3 editor, set in `run_filter`), and `ParseDoorRows` compiled at the same level against the SDK's Newtonsoft; the first
  Unity batch is the compile check of the Editor assembly (`Newtonsoft.Json.Linq` was already referenced by `ArenaPackage.cs`).

### Handoff: rebuilding the two NavMesh bundles

Both builds stop in step 4 at `validate_extract.py` (the known drift of the v5 packages against the installed Arena, see AGENTS.md).
That is BEFORE `validate_bundle_roundtrip.py`, the only guard against the 2026-10-20 mesh-id collision (Bowl geometry in 228 Bay5 slots,
[bundle-encryption.md](bundle-encryption.md) "Found on the way"), so `build_all.ps1` never runs it by itself: a green-looking build is
not a checked build, and the steps marked BY HAND below are not optional. Never two Unity builds at once.

**Step 0, before the first build: the 2026-10-20 NavMesh bundles are kept (done 2026-10-24 on the build VM).** Unity overwrites
`work/build/full` and `work/build/bay5`, and the plaintext NavMesh bundles exist nowhere else (`dist/` holds them encrypted), so without a
copy there is no before/after diff. The copies, with their `.manifest` files, are in the ignored `work/tmp/old-navmesh/`:

| File | Size | SHA-256 |
|---|---|---|
| `arena_equator_navmesh.bundle` | 137,136 B | `888c5e765acd919bd5a3540e3c6d07a5dd11ddc8a5508f84622fbd4516e47fa6` |
| `arena_bay5_navmesh.bundle` | 347,902 B | `5d051485e8da669cab3a7084dff4828ec8a9539061253f9e43abb79de0c0d79e` |

`ArenaMapPort.BundleCrypt verify --map-key arena_equator|arena_bay5 --plain work/tmp/old-navmesh/<file> --enc dist/BepInEx/plugins/maschine-ArenaMapPort/bundles/<file> --key-store work/release-keys`
reports both as exactly the plaintext of the staged (and deployed) bundles. Check the hashes before building; the Equator copy must not be
overwritten by a later copy of an already rebuilt bundle (use `cp -n`).

**Step 1, the two builds** (one after the other; from a PowerShell prompt, the `[bool]` parameters do not parse through `pwsh -File` from
bash). Each ends with a red `validate_extract.py` line, after the bundles are built and identity-patched; for Bay5 `make_base_json.py` and
`patch_preset.py` ran before that (nothing in them depends on the doors: `git status -- ArenaMapPort.Server/db/arena_bay5` stays clean):

```powershell
# Equator: WITHOUT -FullMap, into work/build/full
& ./tools/build_all.ps1 -MapKey arena_equator -MapDescriptor D:/ArenaMapPort/tools/maps/equator.json -PackageDir D:/ArenaMapPort/work/extract/equator-native-v5 -OutputDir D:/ArenaMapPort/work/build/full -BakeNavMesh $true -Verify $true -SkipGameRefsSync
# Bay5: WITH -FullMap, into work/build/bay5
& ./tools/build_all.ps1 -MapKey arena_bay5 -MapDescriptor D:/ArenaMapPort/tools/maps/bay5.json -PackageDir D:/ArenaMapPort/work/extract/bay5-native-v5 -OutputDir D:/ArenaMapPort/work/build/bay5 -FullMap -BakeNavMesh $true -Verify $true -SkipGameRefsSync
```

**Step 2 BY HAND, after each build: the three validators that step 4 of `build_all.ps1` would have run** (arguments exactly as the script
passes them; from `D:\ArenaMapPort`; the mesh comparison of the first one is the collision guard):

```powershell
# Equator (WITHOUT --require-navmesh, WITH --allow-missing-scenes)
python -u tools/validate/validate_bundle_roundtrip.py --scenes-bundle work/build/full/arena_equator_scenes.bundle --navmesh-bundle work/build/full/arena_equator_navmesh.bundle --package work/extract/equator-native-v5 --material-shaders work/build/full/material-shaders.json --editor-verify work/build/full/editor_verify.json --scripts-scene-name Arena_Equator_Scripts
python tools/validate/validate_preset.py --map D:/ArenaMapPort/tools/maps/equator.json --scenes-bundle work/build/full/arena_equator_scenes.bundle --allow-missing-scenes
python tools/validate/validate_map_descriptors.py
# Bay5 (WITH --require-navmesh, no --allow-missing-scenes)
python -u tools/validate/validate_bundle_roundtrip.py --scenes-bundle work/build/bay5/arena_bay5_scenes.bundle --navmesh-bundle work/build/bay5/arena_bay5_navmesh.bundle --package work/extract/bay5-native-v5 --material-shaders work/build/bay5/material-shaders.json --editor-verify work/build/bay5/editor_verify.json --scripts-scene-name Arena_Bay5_Scripts --require-navmesh
python tools/validate/validate_preset.py --map D:/ArenaMapPort/tools/maps/bay5.json --scenes-bundle work/build/bay5/arena_bay5_scenes.bundle
python tools/validate/validate_map_descriptors.py
```

Pass picture: `[validate_bundle_roundtrip] OK -- all checks passed (1 non-blocking warning(s))`, the one warning being the known material
count (Equator 373 against 372, Bay5 501 against 500). A line like `Mesh '<id>': bundle vertex_count (a) == package (b)` is the collision
(176 of them on 2026-10-20): stop, do not stage, look at `Assets/ArenaImport/Meshes`. The same three commands were run on 2026-10-24 on the
UNCHANGED 2026-10-20 builds and passed (Equator, Bay5 with `--require-navmesh`, `validate_preset` and `validate_map_descriptors` OK on both),
so the command lines are known-good and any red after a rebuild is the rebuild's.

**Step 3 BY HAND: the Unity log** (`work/build/<full|bay5>/unity_build_all_*.log`, the newest file; the leading `[ArenaMapPort]` of every
line is left out below) against the "Expected log lines" block above, and these pins:

- **The first batch is also the compile check** of `ArenaPaths.cs` and `BakeNavMesh.cs` (nothing has run in Unity yet): no `error CS####`
  line, and the `[ArenaPaths] loaded 10 / 8 door pose row(s)` line exists (the 2026-10-20 logs have none, so its absence means a stale log).
- **`MeshImporter ... refreshed N existing asset(s)`**: 2605 (Equator) and 3911 (Bay5), as on 2026-10-20.
- **The bake box did not move**: `baked NavMeshData: ... bounds=Center: (...), Extents: (...)` is Equator (-0.48, 5.73, -9.55) / (59.04, 42.69,
  74.29) and Bay5 (24.55, 3.39, -157.84) / (170.82, 51.47, 358.07), because the doors do not change the renderer union. The collision
  of 2026-10-20 grew Bay5's box to extents (390, 225, 420) and its NavMesh to 29,586 vertices, so this is the cheap tripwire that needs no validator.
- `sources=` 4106 / 10493, `collected` 4169 / 10583, modifiers `0 area-override, 31 ignoreFromBuild` / `81 area-override, 28 ignoreFromBuild,
  134 volume`, no `matches no door pose row` Warning, no `path not found in the content scenes` Error.
- `triangulation: vertices= triangles=` moves a little (the closed shutter, the new doorways); it was 10404 / 4572 and 22117 / 10213.

**Step 4: before and after.** `cmp`/`sha256sum` of the new NavMesh bundles against the table in step 0: they MUST differ (the shutter
closes, the gates open), the sizes stay in the same order of magnitude (137 KB, 348 KB). The structural diff (polygons, area, components, and
the lattice cells that were walkable and are not, or the other way round, clustered with centre and box) needs `navmesh_diff.load_bundle_mesh`
(`tools/validate/navmesh_diff.py`) on both files, then `NavMesh.locate` of one file's `sample_polygons` lattice in the other; two identical
files give 0 lost and 0 gained (checked 2026-10-24: Equator's backup against the unchanged build, Bay5's backup against the September
LZMA bundle, whose NavMeshData is the same; Equator 2916 polygons / 14,697 m2 / 67 components, Bay5 5952 polygons / 123,664 m2 / 572
components, the numbers a rebuild is compared with). Expected after the rebuilds: Equator loses the cells where the FCK shutter
closes and gains none; Bay5 gains the doorways of `Door_01 (2)`, `One_man_Door` and the second depot gate (gate 822 at x 69.045; the other
rollers and gate 821 were already open) and loses the footprints of gate 821's two `Telfer_Help` posts. A lost or gained cluster far
from every gate is a drift to explain before staging.

Notes: the descriptor path must be absolute (Unity resolves a relative one from its project directory) and sit beside its `.doors.json`.
A later client Release build without `ArenaMapPortDllOnly` stages and encrypts the new bundles; `validate_distribution.py` and
`validate_port.py --distribution` run after that, not before.

### Open risks

- **Lifted rollers and park carvers (runtime).** The bake leaves the doorway open; a roller parked 3.09 m above its closed place hangs
  its slab above the doorway. A park carver built on the parked collider's bounds must not seal the floor below it. The post-release
  probe through the doorway is the check.
- **Static gates stay walls.** `WAREHOUSE_DOORS (1)` also holds `Bay5_Warehouse_Gate_Door_01`, `(1)`, `Door_Top_02/Bottom_02` and
  `Door_Top_03/Bottom_03` (no DotAnimator, closed in Arena's TeamFight): all are sources before and after, as are the other
  `Gates_PTOR_*` and `Outside_Door_*` leaves of the other Bay5 scenes. "All gates" in the user's sentence means the five Arena moves
  plus `(3)`.
- **Equator FCK hall.** With the shutter shut the hall is reachable only through whatever other opening it has (not measured; the
  2026-09-20 note says the shutter doorway was its way in); no sidecar spawn lies there any more. A bot another mod spawns inside it
  could be stuck.
- **Offline model drift.** `port_colliders.mark_barriers` (used by `navmesh_diff.py`) still implements the old rule only, so
  `navmesh_diff.py --descriptor tools/maps/bay5.json` treats the depot posts as dropped. `BarrierBake` in
  `test_airpit_bake_inputs.py` is the door-pose-aware version; lifting it into `port_colliders.py` is a small follow-up.
- **`ParseDoorRows` and the Unity compile are unrun** inside Unity (see Evidence); a typo-level problem would show in the first batch
  compile, before the 15-minute bake starts.

## 2026-10-23 - Equator and Bay5 door motions, spawns into the booths, drive scale

Status (build VM, 2026-10-24 00:40-01:30, repo 5521169 plus the uncommitted change set): implemented, built, both NavMesh bundles rebaked,
bundles staged encrypted into `dist/`, distribution validator green, deploy DRY RUN only. NOT deployed, no host raid yet. This section is
the integration record of the change set whose parts are described above: the DotAnimator decode and sidecar schema 2 ("DotAnimator poses
for Equator and Bay5"), the client ("Runtime 2"), the NavMesh bake ("NavMesh bake"), and the Equator spawn move
([equator-booth-spawns.md](equator-booth-spawns.md)). User request (2026-10-23): Equator's spawns belong behind the booth doors (not in the
FCK shop) and the FCK shutter is shut again; on Bay5 every gate opens with Arena's motion; "the timing may be faster than the original, half
is probably enough" (new key `Spawns/SpawnGateDriveScale`, default 0.5, every map, Bowl included).

### Builds and tests

| Step | Result |
|---|---|
| `dotnet build ArenaMapPort.Client -c Release -p:ArenaMapPortDllOnly=true` | 0 warnings, 0 errors |
| `dotnet build ArenaMapPort.Server -c Release` | 0 warnings, 0 errors |
| `dotnet test ArenaMapPort.Server.Tests -c Release` | Passed 2215, Failed 0, Skipped 0, Total 2215 |
| `python -m unittest discover -s tools/tests -p "test_*.py"` | Ran 2044, 55 skipped, 1 failure (see below), then OK |
| `python tools/validate/validate_map_descriptors.py` | OK, 14 descriptors, 14 catalog entries, `EquatorDefault()` agrees |

The one red Python test was a stale pin: `test_lamp_fill.ValidatorTests.test_a_sidecar_lamp_fill_without_a_registry_literal_is_reported`
passed a bare `Path("bay5.json")` to `check_map_against_registry_entry`, and the validator now looks for `bay5.doors.json` next to
the descriptor it is given, so it searched the working directory ("registry DoorPoses has 8 entries but bay5.doors.json does not exist").
Both arguments are now `REPO / "tools" / "maps" / "bay5.json"` (the file's own `REPO`); the file runs 20 tests OK.

### Unity rebuilds (serial; `tools/build_all.ps1` from a `.ps1` caller; both stop in step 4 at `validate_extract.py`, the known v5 drift:
`Native features: independent named preset provenance.source_presets.offset: 5708 != 5716`, after the bundles are built and identity-patched)

Logs `work/build/full/unity_build_all_20261024_004736.log` (Equator, no `-FullMap`, 00:47-00:57) and
`work/build/bay5/unity_build_all_20261024_005914.log` (Bay5, `-FullMap`, 00:59-01:11). The compile of `ArenaPaths.cs` and `BakeNavMesh.cs`
was clean (no `error CS`), `ParseDoorRows` read the rows, and every pin of "Expected log lines of the rebuilds" matched (the leading
`[ArenaMapPort]` left out):

```
Equator
[ArenaPaths] loaded 10 door pose row(s) from D:/ArenaMapPort/tools/maps/equator.doors.json
[BakeNavMesh] opened_barriers: none; only SPAWN_BOUND*/LevelBorder objects and the booth leaves are dropped by name, so every shutter and gate collider (Equator's FCK hall shutter included) is baked as a wall
[BakeNavMesh] excluded 2 ...: SPAWN_BOUND*      excluded 41 ...: LevelBorder layer 29      excluded 10 ...: Door_Sliding_Respawn_L      excluded 10 ...: Door_Sliding_Respawn_R
[BakeNavMesh] NavMesh modifiers: 31 markup(s) in bake scope (0 area-override, 31 ignoreFromBuild), 0 volume source(s) added
[BakeNavMesh] baked NavMeshData: ... bounds=Center: (-0.48, 5.73, -9.55), Extents: (59.04, 42.69, 74.29) sources=4106 (root=null collected 4169 total, 4106 kept ...)
[BakeNavMesh] bake inputs: ... 0 navmesh_bake_exclusions, 0 opened_barriers (0 with door poses: 0 moved subtree(s))
[BakeNavMesh] triangulation: vertices=10395 triangles=4567 (indices=13701)
[MeshImporter] read current package geometry for every mesh; refreshed 2605 existing asset(s) with stable references

Bay5
[ArenaPaths] loaded 8 door pose row(s) from D:/ArenaMapPort/tools/maps/bay5.doors.json
[BakeNavMesh] opened_barriers: 5 path(s); 5 with door poses (8 row(s) loaded) drop 8 moved subtree(s) [Bay5_Warehouse_Gate_Door_01 (2), ... (3), ... (4), One_man_Door, Gates_Depot_Door_L, Gates_Depot_Door_R, Gates_Depot_Door_L, Gates_Depot_Door_R] and bake every other part as a wall; 0 without door poses drop their non-frame parts
[BakeNavMesh] excluded 200 ...: LevelBorder layer 29   2: SPAWN_BOUND*   2: door_poses:Door_01 (2)   2: (3)   2: (4)   4: door_poses:One_man_Door   8: door_poses:Gates_Depot_Door_L   4: door_poses:Gates_Depot_Door_R
        (no "opened_barriers:" reason line any more; the depot frame, the Telfer posts and both frames are bake sources)
[BakeNavMesh] NavMesh modifiers: 109 markup(s) in bake scope (81 area-override, 28 ignoreFromBuild), 134 volume source(s) added
[BakeNavMesh] baked NavMeshData: ... bounds=Center: (24.55, 3.39, -157.84), Extents: (170.82, 51.47, 358.07) sources=10493 (root=null collected 10583 total, 10493 kept ...)
[BakeNavMesh] bake inputs: ... 5 opened_barriers (5 with door poses: 8 moved subtree(s))
[BakeNavMesh] triangulation: vertices=22141 triangles=10223 (indices=30669)
[MeshImporter] ... refreshed 3911 existing asset(s)
```

No `matches no door pose row` Warning, no unresolved path. The bake box is the 2026-10-20 box (the mesh-id-collision tripwire).

The by-hand validators, arguments as `build_all.ps1` step 4 passes them: `validate_bundle_roundtrip.py` OK on both ("all checks passed (1
non-blocking warning(s))", the known material count, 373 against 372 and 501 against 500; Bay5 with `--require-navmesh`),
`validate_preset.py` OK on both (Equator with `--allow-missing-scenes`), `validate_map_descriptors.py` OK.

| | Equator | Bay5 |
|---|---|---|
| NavMesh bundle bytes, 2026-10-20 -> now | 137,136 -> 137,103 | 347,902 -> 348,394 |
| NavMesh bundle SHA-256 (now) | `71508782123e...` | `90720288b514...` |
| `triangulation:` vertices / triangles | 10404 / 4572 -> 10395 / 4567 | 22117 / 10213 -> 22141 / 10223 |
| polygons / vertices (loaded mesh) | 2916 / 4854 -> 2914 / 4851 | 5952 / 12175 -> 5959 / 12179 |
| walkable area xz | 14697.3 -> 14694.4 m2 | 123664.1 -> 123671.0 m2 |
| components | 67 -> 68 | 572 -> 572 |
| lattice cells (1 m) lost / gained | 3 / 0 (the shutter doorway, centre (-10.0, 0.1, 36.0)) | 1 / 7 (gained: 4 at depot gate 822, centre (69.0, -3.8, -6.5); 3 at `Door_01 (2)`, (142..144, 40.0); the one lost cell (65.0, -3.6, -4.0) is a polygon-edge sample on the 822 apron, the same ground is covered by new polygons) |
| scenes bundle (plaintext bytes) | 576,350,403 built, 577,488,350 identity-patched | 1,044,553,906 patched |

The scenes bundles are rebuilt but content-equal to the deployed ones: decrypting the deployed bundles and comparing, per nested file and
object type, the multiset of (size, crc32) for Mesh/Texture/Material/Shader/AudioClip/AnimationClip/TextAsset and of sizes for the
reference-bearing types (Unity allocates path ids differently on every build, so a per-id comparison is meaningless) gives 0 differing
objects on both maps (Equator 13 nested files, 129k objects; Bay5 21 nested files). Their byte size moved by -119 B (Equator) and +441 B
(Bay5) only. The decrypted copies were deleted again.

### What the new meshes show (read-only checks on the loaded NavMeshData)

- **Equator, FCK hall.** The shutter doorway is gone: the mesh has no polygon on the shutter line (-9.65, 0.1, 36.0), 2 fewer polygons
  straddle x -9.9..-9.4, z 32.5..39.5 (8 -> 6), and the hall floor behind it is a separate pocket (component 10, 95 polygons, x -35.0..-10.0,
  z 34.5..49.6) that holds the OLD Usec spawn (-11.7, 48.6) and the old `bot-north-hall` (-11.5, 37). The hall's polygons are still
  there (a baked floor behind a closed wall); what is gone is every connection to the play area. A player or bot placed there by another
  mod would be walled in. The old play area (949 polygons) lost the hall pocket (95) and the two doorway polygons: 852 polygons / 2783 m2 are left.
  Because the hall dropped out, the play area is no longer the largest component by area: component 0 is now the upper floor
  (965 polygons, 2897 m2) and the ground-floor play area is component 1. Any check that assumes "label 0 = play area" must use the
  component of the atrium spawn instead.
- **Equator, spawns.** All 14 spawn points of `equator.bots.json` (4 player, 10 bot) are on the mesh and in the SAME component, the play
  area (the four player spawns are the booth markers (-4.897, 0.08, 45.44), (6.52, 0.08, 48.589), (4.974, 0.08, -39.087), (-4.346, 0.08,
  -39.087), clearance 3.40 / 1.71 / 2.53 / 3.35 m). `bot-south-west` (-9, -28) and `bot-south-east` (8.7, -28.4) sit 0.00 and 0.05 m from a
  border edge, unchanged from the old mesh.
- **Equator, booth doorways.** The booth leaves are excluded from the bake by name as before, so all four assemblies (two per booth) are
  passages on the old and the new mesh (polygon adjacency BFS between points 3 m either side, the path within 0.2 m of the assembly).
- **Bay5, doorways** (polygon-adjacency passage test from each gate's own collider box, 2 m beyond the slab either side, BFS in a 7 m
  disk, path within 1.5 m of the slab centre):

  | Gate | old mesh | new mesh |
  |---|---|---|
  | `Door_01 (2)` (x 140.8..145.1, z 40.0) | not joined | **open** |
  | `Door_01 (3)`, `Door_01 (4)` (x 129.4, z 41.0..50.3) | open | open |
  | `One_man_Door` lower panel (x 129.4, z 68.3..70.6) | not joined | **open** (the upper panel is above the floor and has no mesh) |
  | depot gate 821 (x 50.57) | open (single polygon spans it) | open |
  | depot gate 822 (x 69.04), leaf L and leaf R | not joined | **open** |

  A cruder test from the gate root pivots said the same for 822 and `One_man_Door` and missed `Door_01 (2)`, whose root pivot sits at the
  edge of the doorway (145.2 against a slab centred on x 143).
- **Bay5, spawns.** 12 of 14 spawns are in the play component 2 (1406 polygons, 4433 m2, x 44.3..145.9). `player-north-west` (54.2, -3.86,
  53.8) and `bot-east-lane` (117.89, -3.86, 43.1) are 0.64 m and 0.76 m from the nearest vertex but outside every polygon, identically
  on the 2026-10-20 mesh; EFT snaps spawns to the nearest NavMesh position (both were used in every earlier raid), not caused by this change.
- **Bay5, left shut on purpose.** `Bay5_Warehouse_Gate_Door_01` (unnumbered) and `Door_01 (1)` stay walls: no located pair beyond them,
  0 passages on both meshes. The map-data agent's findings (above, "NavMesh bake"): the unnumbered gate has an always-loaded static twin in
  the WAREHOUSE scene, six plywood planks in its plane and a pallet in front; `(1)` has a fence and a board in its doorway; Arena's own
  NavMesh stops at x 145.9. Opening them would lift a slab and show the obstacle. A decision for the user if he still wants "all".

### base.json

`python tools/server/make_base_json.py` (Equator, defaults) writes a file byte-identical to the working-tree
`ArenaMapPort.Server/db/arena_equator/base.json` (new sidecars: 4 player + 10 bot spawn points); `--map tools/maps/bay5.json` writes a file
byte-identical to the committed Bay5 `base.json`, and the `-FullMap` build left it untouched (`git status` clean for `db/arena_bay5`).
Only the Equator database changes.

### Staging, distribution, deploy plan

`dotnet build ArenaMapPort.Client -c Release` (no `DllOnly`): 0 warnings, 0 errors, 21 s, no key created (`wrap present for Arena
0.6.1.0.48206 ... (unchanged)` on all four maps):

```
arena_equator_scenes.bundle: 577,488,350 B plaintext, 577,506,078 B encrypted, 551 chunks, written ... arena_equator_navmesh.bundle: 137,103 B -> 137,231 B, written; preset unchanged
OK stage arena_equator: 3 bundles encrypted (2 written), key file keys/arena_equator.json: wrap present for Arena 0.6.1.0.48206, 1 wrap(s) in the file (unchanged) ...
arena_bay5_scenes.bundle: 1,044,553,906 B -> 1,044,585,906 B, 997 chunks, written ... arena_bay5_navmesh.bundle: 348,394 B -> 348,522 B, written; preset unchanged
OK stage arena_bay5: 3 bundles encrypted (2 written), ... (unchanged)
OK stage arena_airpit: 3 bundles encrypted (0 written)      OK stage arena_bowl: 3 bundles encrypted (0 written)
```

`ArenaMapPort.BundleCrypt verify` on all six Equator and Bay5 bundles: "is exactly the encryption of" the `work/build` plaintext.
`python tools/validate/validate_distribution.py`: `DISTRIBUTION_OK 4 maps 12 bundles 4 sidecars encrypted`.

`pwsh -NoProfile -File tools/deploy-to-spt.ps1 -SptRoot D:/SPT41` (dry run): "plan: 90 file(s), 3492.1 MB total -- 0 new, 7 to overwrite, 83 already
identical". The seven: `maschine-ArenaMapPort.Client.dll`, `maschine-ArenaMapPort.Server.dll`, `arena_equator_navmesh.bundle`,
`arena_equator_scenes.bundle`, `arena_bay5_navmesh.bundle`, `arena_bay5_scenes.bundle` and the Equator `base.json`. Bay5's `base.json`, every
`keys/*.json` (the wraps did not change), both presets and every Airpit/Bowl file are identical, so about 1.6 GB moves through Syncthing.
(Client DLL SHA-256 `f6d6be6152aa...`, server DLL `2d8ae1c0bd65...`.)

### Host recipe (GPU-2)

Order matters; the details of the combinations are in [equator-booth-spawns.md](equator-booth-spawns.md) "Deploy order (Equator)".

1. Close EFT. `pwsh -NoProfile -File tools/deploy-to-spt.ps1 -SptRoot 'D:\SPT41'` (dry run), then with `-Confirm`. Wait for Syncthing
   (~1.6 GB). Compare the server DLL and `arena_equator/base.json` hashes on the host before touching the server.
2. Restart the SPT server (close its window, start the desktop shortcut) BEFORE EFT: an old database serves the old Usec spawn
   (-11.7, 48.6), which the new NavMesh and the shut shutter wall in.
3. `node gpu-run.mjs restart --host host2`; `uibridge_status` (loaded MVID = VM build), `max` frame time.
4. Equator raid, ordinary PMC flow, NO BOTS first: `uibridge_player` at start must read one of (-4.897, 0.08, 45.44),
   (6.52, 0.08, 48.589), (4.974, 0.08, -39.087), (-4.346, 0.08, -39.087). In a plain raid the booth leaves are parked at the open pose from
   the start; with a Hold (ArenaMode) they stay closed until the Release and then slide in 2.5 s / 3.5 s (half of 5 s / 7 s), with the park
   carvers (10 by the offline model) sealing the parked bodies. Debug lines: the `park:` / `[RaidFix] spawn openings:` / `parked ...` lines of "Log lines to expect
   on the host" in the Runtime 2 section (ten `park:` lines on Equator, eight on Bay5, no Warning). The FCK shutter must stay shut (walk west along z 36 from (-8, 36): a wall at x -9.65) and the hall
   must have no NavMesh connection (`uibridge_teleport dryRun=true` to (-11.7, 0.1, 48.6), the old Usec spawn in the pocket, is
   `reachable=false`).
5. Bay5 raid: rollers `Door_01 (2)`, `(3)`, `(4)` and `One_man_Door` lift 3.1-3.3 m in 2.0 s; the depot leaves of BOTH gates (50.57, 7.53) and
   (69.04, -6.32) swing in 3.5 s while the Telfer posts and frames stay standing; `uibridge_teleport dryRun=true` through the
   `Door_01 (2)` and 822 doorways is reachable. The unnumbered `Door_01` and `(1)` stay shut (see above).
6. Hideout once (the generic-class patch trap), `Spawns/SpawnGateDriveScale` at 1.0 for a comparison with Arena's own timing, Bowl once
   (its drives are now halved too, 7.7-13.5 s -> about 3.9-6.8 s; its park carvers 24 -> 23 per the runtime section).

### Open

- Everything is unrun in a raid: that four spawns start behind CLOSED booth doors and the player walks out through the opened ones, that
  the south-west spawn clears the depot-821 L leaf, that the Bay5 rollers (slab bottoms 3.1 m above the floor line) do not get park
  carvers and the depot leaves do, that the post-release NavMesh probe does not raise a false Warning, and that Bowl still behaves
  after the halved drive and the walking-band rule.
- `Door_01` and `Door_01 (1)` on Bay5 are shut by design (a user decision if "all gates" includes them).
- The 2026-10-18 0.3.0 candidate ZIP is superseded: it carries the old Equator/Bay5 NavMesh bundles, the old Equator `base.json` and the
  old DLLs. A new ZIP needs a new staging pass (nothing in the cut step has run).
- The ad-hoc checks used here (validators plus log pins, mesh checks, content diff) are not committed; the log pins and the validator
  commands are in "Handoff: rebuilding the two NavMesh bundles" above.
