# Spawn gates: closed enclosures until a mode releases them (2026-10-17)

User question (2026-10-16): the team spawn boxes' gates are removed for good; could the original behaviour serve ArenaMode,
gates opening only after the preparation time, and would bots inside cope? Assessment and implementation 2026-10-17.

## What exists, what Arena had

- Only Equator has sliding booth doors (`Door_Sliding_Respawn`, four TeamFight front doors under `SPAWN_DOORS` and door (3)
  of base-scene `Respawn_2`). Bay5's enclosures are warehouse gates and a hinged depot gate, AirPit's the `BUS_DOORS` row
  (descriptor `opened_barriers`). All of them are plain `LODGroup` hierarchies: the leaves/parts carry no component of
  their own; their children hold the meshes, a layer-18 `_COLLIDER` (movement) and a layer-12 ballistic collider.
- Arena's gate script (`GateEntity`, an Arena MonoBehaviour) is unreadable by the extractor's rules; only the closed pose
  is in the data. The slide used here is an inference from the frame geometry: each leaf is 1.38 m wide and the frame
  pocket next to it is 1.44 m deep, so a slide of 1.38 m along the leaf's local x axis (L towards +x, R towards -x)
  tucks it into the frame. Bay5's depot gate and AirPit's portal fences carry authored inactive open variants instead.
- `BoothDoorRepairs` switched every part off once, from the `SpawnPointsCollection.CreateFromScene` postfix (twice per
  raid, idempotent), with no timer and no way back. The NavMesh bake leaves the parts out (`BakeNavMesh.RoundOpenedReason`),
  so the baked mesh runs through every opening.
- EFT bots path only on the NavMesh (`NavMesh.CalculatePath`, area mask -1, no NavMeshAgent); colliders never enter path
  planning. A closed leaf alone is therefore a physical wall bots do not plan around: SAIN-driven bots press against it
  (stuck check, re-plan, jump/vault loops), vanilla-mover bots are snapped along the path through it. Keeping bots inside
  needs the NavMesh cut at the doorway, which EFT itself (`NavMeshDoorLink` carvers), Waypoints 1.9.0 (exfil/switch door
  blockers) and ArenaMode's own area enclosure (Box carvers, `carveOnlyStationary` true, time to stationary 0) all do
  with carving `NavMeshObstacle`s on native meshes; none had done it on a ported `NavMeshData` before.
- ArenaMode (read-only): only Team DM on Equator spawns into the booths (20 box points). Its phases are Assembling (no
  fixed length; ends when every bot is Active) -> 3 s Countdown -> Live; bots are already in the boxes and free to move
  during both, damage is blocked. Respawns and bot replacements re-enter the boxes throughout Live, so the gates must
  stay open after the release, as in Arena. ArenaMode has no public "fight starts" event (the TDM phase flip is private)
  and its box admission requires a complete NavMesh path from each box point to the atrium, which a carved doorway
  turns Partial: the mode side has to call the API below and tolerate that.

## Implementation

`ArenaMapPort.Client/SpawnGates.cs` (public static class, the reflection contract for a mode plugin) and
`SpawnGateModel.cs` (pure rules, linked into the server tests):

- `BoothDoorRepairs` offers every part to `SpawnGates.Offer` before switching it off: the part is registered with its
  closed pose either way; while a hold is on, it stays active instead.
- `Hold(reason)`: before the opening, in the menu (next owned raid), or after it (parts come back in their closed pose,
  an opening in progress is cancelled). `Release(reason)`: Equator's leaves slide for `SpawnGateSlideSeconds`, then every
  part switches off and the carvers go. `IsHeld`, `ClosedParts`. All on the Unity main thread.
- Carvers: one Box `NavMeshObstacle` per movement collider (layers 18 and 0) of every held part, sized from the world
  AABB plus 0.2 m per horizontal side and 0.5 m of height, created when `NavMeshInstaller.Installed` fires (the ported
  mesh exists only from `BotsController.Init` on) or at once for a later hold. A NavMesh path probe across each gate's
  thin axis (1.5 m either side, sampled within 1.5 m) logs the verdict 0.5 s later (again at 2.5 s on a contradiction).
- Timers: the developer test hold (`Spawns/SpawnGateHold`, `SpawnGateHoldSeconds`, live) releases after N seconds of raid
  time; every hold releases after `SpawnGateMaxHoldSeconds` (default 300, counted from the hold or from the raid timer)
  with a Warning. `SpawnGateCarve` (default on), `SpawnGates` (feature switch). Everything resets on the owned Scripts
  scene's unload.
- Review (three lenses, adversarially verified) found the real gaps that shaped the final shape: a hold after a normal
  opening had nothing to re-close, a re-close left slid leaves in the pocket, the safety limit counted from raid start,
  a hold during the slide was lost, and the post-release probe had no bounds to probe. All fixed before the host runs
  below.

## Host evidence (GPU-2, 2026-10-17, Equator, PMC raid, bots as online, developer test hold)

First build (11:08, before the review fixes):

- Raid start, both passes of the opening: `respawn doors: deactivated 0 sliding-door leaf(s) on 5 door assembly(s),
  10 leaf(s) held closed (15 part(s); developer test hold 600 s)`, `spawn barrier 'equator_roller_shutters_02 (9)':
  deactivated 0 part(s), 1 already off, kept 6 frame part(s), 5 part(s) held closed`; the raycast report says BLOCKED
  for the booth doors, as intended.
- `navmesh installed: 11 carver(s) over 11 movement collider(s) of 15 gate part(s)` right after the NavMesh was added,
  then `navmesh probe: every gate sealed`: all ten leaves and the shutter answer `PathPartial` across the doorway
  (e.g. `'Door_Sliding_Respawn_L' at (-3.6, 1.1, 40.0): (-3.6, 0.1, 38.5) -> (-3.6, 0.1, 41.5) = PathPartial`).
  **Carving works on the ported NavMeshData.** `uibridge_inspect find type=UnityEngine.AI.NavMeshObstacle`: 11, all
  under `ArenaMapPort SpawnGates`.
- Standing inside the north booth: the doors are closed and opaque (the glass meshes are inactive in Arena's data).
  Note for testers: both booths hold the map's extraction boxes (4 x 4 m at (2, 46) and (5, -44)); a player inside the
  box extracts after 8 s, so stand outside it, e.g. (-6, 50) / (-6, -38).
- Release (`SpawnGateHoldSeconds` set live to 1): `released (developer test hold, 64.2 s after the raid timer started):
  10 leaf(s) slid 1.38 m in 1.0 s, 15 gate part(s) switched off, 11 carver(s) removed`; the doorway in front of the
  player opened visibly. The post-release probe reported nothing useful on that build (inactive parts have no collider
  bounds): fixed by caching the bounds while sealing.
- Bots: SAIN PMCs and scavs 40-70 m away reacted to MP5 bursts from inside the booth (combat layers) but held their
  distance with the gate closed and with it open alike, so their behaviour at the carved doorway could not be observed
  in a plain PMC raid. The code path is known from the sources (a Partial path makes SAIN idle or walk to the last
  corner, nothing teleports or kills a bot) and the direct observation belongs to ArenaMode's Team DM, where bots start
  inside the boxes; that needs the mode-side wiring first.
- No `[ArenaMapPort]` warning in any of the raids; the carvers cost nothing measurable (120 fps, max frame 8.6 ms with
  the carvers standing).

Final build (11:28-11:32, after the review fixes and the baseline probe; this is the committed code):

- Opening passes: `10 leaf(s) held closed (10 part(s) registered; developer test hold 600 s)`, second pass
  `(15 part(s) registered)` (the FCK shutter's five parts join on the barrier step); both passes idempotent.
- `navmesh installed: 11 carver(s) over 11 movement collider(s) of 15 gate part(s); 2 doorway(s) already disconnected
  on the map's own navmesh (not judged)`: the baseline probe before the first carve finds door (3) of the south booth
  Partial on the shipped mesh (Arena fences the compartment behind it; `exfils-stage2.md`), so its two leaves are carved
  but left out of the verdicts.
- `navmesh probe: every probed gate sealed (9 probed)`: the four TeamFight doors' eight leaves and the shutter answer
  PathPartial across the doorway.
- Automatic release: `released (developer test hold, 60.2 s after the raid timer started): 10 leaf(s) slid 1.38 m in
  1.0 s, 15 gate part(s) switched off, 11 carver(s) removed`, and 0.5 s later `navmesh probe: every probed gate open
  (9 probed)`, each `PathComplete, 2 corners`. Un-carving is therefore effective within half a second.
- No `[ArenaMapPort]` warning; 119.8 fps, max frame 12.5 ms over 300 frames while the carvers stood; hideout opened
  after the raid. The host's developer config was returned to `SpawnGateHold = false`, `SpawnGateHoldSeconds = 30`.

## What the mode side has to do (ArenaMode, the user's mod)

1. Call `ArenaMapPort.Client.SpawnGates.Hold("tdm assembling")` for the raid (from its own CreateFromScene postfix, which
   already runs after ArenaMapPort's, or earlier) and `Release("tdm live")` when the fight starts; `Release` on every
   cancel path of a menu-time hold. Find the type with `AccessTools.TypeByName`, validate the signatures, skip silently
   on a mismatch (the pattern of ArenaModeMapPositionIntegration).
2. Let the box admission accept a Partial NavMesh path to the atrium while `IsHeld` is true (or run it before the hold),
   and expect bots inside a sealed box to idle or stand at the carved edge until the release.
3. Watch the first native Team DM with the gates held: bots inside the boxes during Assembling, the slide at Live, bots
   leaving afterwards. That is the observation this plugin side could not produce in a plain PMC raid.

## Follow-up 2026-10-17: held gates, start-gate subset, AirPit's north box

- Descriptor `StartGates` / `HeldGates` (sidecar `start_gates` / `held_gates`, subsets of `opened_barriers`;
  `ArenaMapDescriptor.GateRoleOf`): a Hold closes only the start gates (AirPit: BUS_DOORS; Equator and Bay5 declare
  nothing, which means every barrier, as before). Held gates stay closed at raid start in every mode, get their carvers at
  the NavMesh install without a hold (`SpawnGates.OfferHeld`, `HeldGatesClosed`, `SealHeldGates`), and open only with a
  Release that follows a hold; `Hold` re-arms them for a second round. `SpawnGates=false` or `SpawnGateCarve=false`
  opens them at raid start (an uncarved closed leaf on a meshed lane is the shutter trap). Carvers are incremental
  (`_carved`): the held gate's come with the install, a later Hold adds the start gates' ones.
- Probe verdict per enclosure (`SpawnGateModel.EnclosureContradicts`): AirPit's bus pocket has an authored-open inner
  row behind the closed outer row; the per-part verdict logged a false `a gate is NOT sealed` Warning for its three
  inner doorways (GPU-2 18:45). An enclosure now contradicts a hold only when none of its probed doorways is sealed, and
  a release when any doorway stays closed; the passable inner doorways are listed as Debug. A single leaking outer door
  next to sealed ones would not be caught by this; the dryRun teleport across the enclosure is the stronger check.
- GPU-2 evidence (AirPit, bots None, build of this commit, 18:41-18:52): plain raid `navmesh installed: 16 carver(s)
  over 16 movement collider(s) of 42 gate part(s) (16 carver(s) in place, 36 held-gate part(s))`, `navmesh probe: every
  probed gate sealed (16 probed)` (PathPartial across every leaf), `uibridge_teleport dryRun` to marker (6)
  (-12.59, 79.21) and to a lane strip (-17.23, 67.7): not reachable, the hall (-10, 60) reachable; no warning. Test
  hold 60 s: `40 carver(s) over 40 movement collider(s) of 42 gate part(s)`, `every probed gate sealed (22 probed)`
  without a Warning, `released (developer test hold, 60.0 s after the raid timer started): 0 leaf(s) slid 1.38 m in
  1.0 s, 42 gate part(s) switched off, 40 carver(s) removed`, `every probed gate open (22 probed)`, dryRun to marker
  (6) ok, `uibridge_walk` to it: 22.5 m along the navmesh through a lane, arrived. Equator regression raid: the
  raid-start opening unchanged (10 leaves, the FCK shutter), no SpawnGates activity, no warning.

### Follow-up (recorded 2026-10-18, not started): the passport lamps' green arrow

User question: Arena flips the passport booths' red X to the green arrow when the gate opens; can the port do that? The
data says yes, with a client-only change. The booths in `Arena_Airpit_Main_hall` carry two always-lit lamp quads per lane
(`Passport_control_01_Emissive_controller_RED_close_LOD0` = X, `..._GREEN_open_LOD0` = arrow, 10 each incl. the
`_02` variant). The state comes from dark cover quads under the held gate root `PASSPORT_CONTROL_DOORS`:
`RED/RedFog` (active) holds 10 `Passport_control_01_Emissive_controller_GREY (n)` LODGroups with `_open_LOD0` active
(covering the arrows) plus 18 `FogSheetSimple_Volumetric_red_small` sheets; `GREEN/GreenFog` (inactive) holds the same
10 controllers with `_close_LOD0` active (covering the X) plus 18 green sheets. Both roots are in the shipped bundle
(SceneAssembler sets the saved activity), the green fog material (`Custom/Billboard_FogSheet_Simple`, id b647a5fb...)
is in the shader sidecar, the controller material (`Standard`, `_EMISSION`, id 8ef39b0b...) is shared with RED.
Smallest correct design: a new sidecar/descriptor list `held_gate_indicators` (AirPit:
`PASSPORT_CONTROL_DOORS/GREEN/GreenFog`), `BoothDoorRepairs.OpenBarriers` skips `GREEN` as a gate part (counts 42/36 ->
41/35), `SpawnGates` keeps `_indicators`, switches them on at the end of `Open` when the held gates opened, off at the end
of `Hold` and on scene unload, no new config key, Debug log only. Host check: dev hold (`Spawns/SpawnGateHold`), then
`uibridge_inspect` of `GreenFog` and a still from inside the north box at about (-26, 0.1, 72). Risks: the covers are
coplanar with the lamps (z-fighting possible), the green sheets were never active in a host raid, a plain raid shows no
change (the held gate opens only after Hold then Release), `AirPitFogSheets=Hidden` would hide the green sheets too.

## Host evidence (GPU-2, 2026-10-18, Bowl, developer test hold 60 s)

Bowl's `DOORS_SPAWN_A` (east pocket: gate + BTR) and `DOORS_SPAWN_B` (west pocket: army gate + ambulance) are `start_gates`.
With `Spawns/SpawnGateHold = true`, `SpawnGateHoldSeconds = 60`: both passes of the spawn openings reported `2 part(s) held
closed` per barrier (4 registered), `navmesh installed: 28 carver(s) over 28 movement collider(s) of 4 gate part(s)`, the probe
`every probed gate sealed (1 probed, 2 inconclusive)` (`Gates_Army_01` PathPartial; the BTR and the ambulance have no navmesh within
1.5 m of their probe points) and a `uibridge_teleport dryRun` from the east spawn to the field was refused as unreachable. At
60.0 s: `released (developer test hold ...): 0 leaf(s) slid, 4 gate part(s) switched off, 28 carver(s) removed`, `every probed gate
open (3 probed)` with PathComplete for all three, and the same dryRun passed. Details in bowl-release.md (raid 6).

## Follow-up 2026-10-22: parked parts (Arena's animated end poses)

User decision 2026-10-22 ("ja gerne in Endstellung parken"; for later: recreate the door animations instead of hiding them).
Arena does not hide Bowl's BTR, ambulance, army gate leaves and respawn gate leaf, it animates them to an end pose; the port
switched them off. The data (descriptor `DoorPoses`, sidecar `tools/maps/bowl.doors.json`) is recorded in
[door-poses.md](door-poses.md), together with the log lines and the host recipe of this change (section "Runtime"). The rules:

- **A part with poses is never switched off.** `BoothDoorRepairs.OpenBarriers` calls `SpawnGates.NotePoses` (resolve nodes, check
  them against the scene, measure the closed colliders), then the existing `OfferHeld` / `Offer`, then `SpawnGates.Park`; only a
  part without usable poses reaches `SetActive(false)`. `Park` snaps the nodes to the open pose (positions and euler angles from
  the data, bit for bit, so the second pass changes nothing) and calls `Physics.SyncTransforms()`.
- **Roles.** A Start-role part (Bowl: both barriers) is registered either way, so `Hold` can bring it back and `Release` can drive
  it; a Plain-role part with poses would be parked but never touched by a hold. A Held-role part with poses stays closed until a
  release that follows a hold, then drives like the others.
- **Hold**: every parked or driving part returns to its closed pose (data), loses its park carvers, an opening in progress is
  cancelled as before, and `CreateCarvers` seals it (it skips parts that are parked or driving, so a mixed state is fine).
- **Release**: parts without poses go the old way (Equator's leaves slide for `SpawnGateSlideSeconds`, then `SetActive(false)` and
  their carvers go, at the end of the slide, not of the drive). Parts with poses drive over their own clip's seconds with
  `DoorPoseModel.Ease`; `Spawns/SpawnGateDrive = false` snaps. Each part finishing: pose snapped to the data, its own hold carvers
  destroyed (`_carversOf`), park carvers created. The `released` line gains `, drove N part(s) to the open pose in X s, M park
  carver(s) in place`; the probe follows 0.5 s after the last part.
- **Park carvers**: boxes over the movement colliders (layers 0 and 18) that sit on or below a pose node
  (`DoorPoseModel.IsMoved`, path below the part) and rise more than 0.3 m over the part's floor line
  (`ParkedBodyStands`; nothing in Bowl is that low, the guard keeps a pose that really sinks from carving its doorway shut). They are
  made when the NavMesh is installed or when a part finishes its drive, are kept for the raid, die with the host object on the
  Scripts scene's unload, and follow `SpawnGateCarve`. Colliders no pose node contains (the army frame, the Telfer pillars, the
  respawn gate's rail) stay uncarved. Why: the army frame is two posts whose AABB spans the doorway; carving every collider at
  the open pose seals the army gate (offline raster model of the built Bowl NavMesh: pocket side to field side reachable
  without carvers, not reachable with the hold carvers, reachable with the movers' park carvers, NOT reachable when the frame
  is carved too; table in door-poses.md).
- **Probe**: the post-release probe still expects every probed doorway open, across the CLOSED bounds of the part. A doorway whose
  probe segment (`ProbeSegment`: 1.5 m either side of the closed bounds' centre along the thin axis) runs through a park carver
  is reported as `not judged` (`DoorPoseModel.SegmentTouchesBox`): offline the respawn gate's segment (x 39.79, z -3.69..-0.69)
  touches its leaf's box (x 38.67..43.96, z -3.01..0.60), the BTR's (x 51.23, z -22.97..-19.97) clears the body's (x 41.19..50.15),
  the army's (x -51.68) clears both leaves' boxes. A hold's `expectSealed` probe is unaffected (the park carvers are gone then).
- **Baseline**: `EnsureBaseline()` (factored out of `CreateCarvers`) runs before the first hold or park carver of a raid, over every
  part whose closed bounds are known; parked parts have theirs from `NotePoses`, so a part parked at raid start is judged against the
  map's own NavMesh like any other.
- **Fallback, errors**: pose data that does not fit the scene (node missing, scene pose more than 5 cm / 2 degrees off the data's
  closed pose, a malformed triple) logs one Warning per part and the part switches off like before; `NotePoses`, `Park` and
  `SealParked` catch everything, the Harmony postfix of the raid start never sees an exception.
- **Mode contract unchanged.** `Hold`, `Release`, `IsHeld`, `ClosedParts` keep their signatures; the new members are `internal`.
  `ClosedParts` counts the parked Start-role parts too (registered either way).

Pure rules in `ArenaMapPort.Client/DoorPoseModel.cs` (no Unity types, linked into the tests as `SpawnGateModel` is): `Progress`,
`Finished`, `Ease`, `Lerp` / `Lerp3` (exact endpoints), `IsMoved`, `ParkedBodyStands`, `SegmentTouchesBox`, `PoseMatchesScene`.
Tests: `ArenaMapPort.Server.Tests/DoorPoseModelTests.cs` (model rules; Bowl's real pose data through them; wiring of
BoothDoorRepairs and SpawnGates read as text). `SpawnGateModelTests.cs` is unchanged and still passes (the refactor of
`CreateCarvers` kept every string it pins).

Cost of the drive (review finding, door-poses.md "Cost of the drive"): the parts are re-posed by transform every frame and their
colliders stay static. A scratch Unity 2022.3.43 model of the 19 nodes / 70 colliders measured +0.1 to +0.2 ms a frame in worlds
of 20k to 62k static colliders (EFT's Script simulation mode, `autoSyncTransforms` False and True), and a kinematic `Rigidbody` on
the node roots, the usual advice for moving colliders, measured 0.08 to 0.24 ms slower, so none is added (the wiring test pins
that). The release logs `drive frames: N frame(s) in 13.5 s, mean X ms, max Y ms (...; Physics.autoSyncTransforms <value>)` at
Debug, the whole frame while any part is still posed, so the host check is one log line.

Not done / not measured: nothing of this has run in a raid yet (host check: door-poses.md "Host recipe"); the 13.5 s of the
respawn gate is the waiting time of the last doorway after a mode's Release (`SpawnGateDrive = false` removes it); players or
bots standing where a body drives are not pushed out by the port.
