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
