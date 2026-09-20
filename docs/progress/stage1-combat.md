# Stage 1 — combat and death correctness, NavMesh bake (2026-09-20)

Goal (HANDOFF.md, user decisions of 2026-09-20): the three confirmed correctness gaps a deathmatch on Equator
would hit, fixed and host-verified before any other map gets bots.

## 1. Bullets ignore LowPolyCollider-only props

`docs/progress/fix-combat.md` deferred "~1,700 active colliders on layer 18 excluded from
`BallisticsCalculatorConstants.HitMask`". Counted against `work/extract/equator/scenes/Arena_Equator.json`
(active chain, collider component, layer 18 = LowPolyCollider, layer 12 = HighPolyCollider): 1,705 LowPoly
colliders, 2,175 HighPoly colliders, and only **13** LowPoly colliders whose parent subtree has no HighPoly
collider — Arena authors a `_BALLISTIC_<material>` twin for practically every prop, exactly as EFT expects
(LowPoly = movement collision, HighPoly = ballistics). The 13: `rack_module12` ×4 (shop shelves),
`heap_conc_frag_6` ×2 (rubble near Elevator 2), `RESPAWN_1/doors_spining*/door/collider` ×2 (revolving-door
leaves of the north booth), `reserve_fence_ema_nocurt2` ×2, `metall_fence_2`, `fence_ema_nocurt2`,
`fence_ema_nocurt2_fix` (thin fences, BoxCollider scaled to 1 cm).

Fix: `BallisticTwinRepairs.cs` (`[Combat] BallisticTwins`, default on), run from the same spawn postfix as
the booth doors. For every enabled, non-trigger LowPolyCollider collider whose parent subtree has no
HighPolyCollider collider it adds a child object `<name>_BALLISTIC_twin` on HighPolyCollider with a copy of
the collider (Mesh/Box/Sphere/Capsule). A bare collider on that layer is what all 2,175 ported ballistic
colliders are (the import drops BSG's `BallisticCollider` MonoBehaviour); fix-combat's warning about a
bare `BaseBallistic` component (its `Get()` returns null) is respected — no component is added. Idempotent:
the second `CreateFromScene` pass finds the twins.

## 2. Player death on the Cam2 camera

`EffectsController.OnHealthDied(EDamageType)` (EffectsController.cs:1488-1497) dereferences `_deathFade`
and `_blur`, both fetched by unguarded `GetComponent` at :1288-1289 and null on the Cam2 fallback camera the
ported maps run with. fix-combat deferred it ("fires at most once per raid"); with bots on the map the
player dies routinely. `DeathScreenEffectsGuard` (prefix, `HarmonyPatches.InstallDeathEffectsGuard`) runs
whichever of the two effects exists and skips the original when either is missing; vanilla behaviour when
both exist (every real map).

## 3. NavMesh bake with the round-start obstacles removed

The Equator bake included `SPAWN_BOUND*` boxes, the `Door_Sliding_Respawn_L/_R` leaves and the FCK roller
shutter, all of which the client now removes at raid start (`SpawnPlacementRepairs`, `BoothDoorRepairs`), so
the booth interiors and the FCK hall were NavMesh islands (bots could not follow the player in or out;
`uibridge_teleport` reported `reachable=false`). `BakeNavMesh.RoundOpenedReason` now drops those sources:
name prefix `SPAWN_BOUND`, the two leaf names, and every non-frame descendant of the paths in the map
sidecar's new `opened_barriers` list (`ArenaMapDescriptorData.opened_barriers`; `tools/maps/equator.json`;
mirrored from `ArenaMapDescriptor.OpenedBarriers` by `BaseJsonTests.OpenedBarriers_DescriptorMirrorsSidecar`).
The bake logs one line per exclusion reason. Rebuilt with
`tools/build_all.ps1 -MapKey arena_equator ... -BakeNavMesh $true -Verify $true -SkipGameRefsSync` (Equator's
package predates `-FullMap`; from bash go through `pwsh -Command`, see AGENTS.md).

## Host test 1 (2026-09-20 19:03, client build 18:57, first rebake, EFT restarted)

- Ballistic twins: `added 13 HighPolyCollider twin(s) for LowPolyCollider-only colliders (1756 already had one,
  0 unsupported collider type(s))` — the 13 paths match the offline count exactly (racks at (-23.2,1.1,-7.4),
  (-22,1.1,-3.2), (18.6/17.1,1.1,-26.5); booth revolving-door leaves at (±4.4,1.2,54.2); rubble at (±4,0.4,-18.2);
  fences). Not shot at (bayonet-only test profile); the twins are bare MeshColliders/BoxColliders on
  HighPolyCollider like the 2,175 imported ballistic colliders.
- New NavMesh loaded: `added navmesh for 'arena_equator': 10305 vertices, 4529 triangles` (was 10227/4485).
  `uibridge_teleport dryRun` from the atrium (0,0.2,2): the All booth spawn (-4.5,0.1,38.5) and the Bear booth
  spawn (5,0.1,-33.5) are **reachable** — the booth islands are gone. One patrol way over 6 markers in one
  NavMesh component, a scav patrolling (`PatrolAssault`, moving).
- FCK hall still an island: from the Usec spawn the hall centre (-15,42) and its south-east corner (-11.5,37)
  are reachable, (-8.5,35) outside the shutter is not; from the atrium (-9.3,36) snaps to (-9.2,36),
  (-9.65,36) snaps 0.5 m to (-9.2,36), (-10.0,36) is unreachable — a 0.8 m NavMesh gap centred on the shutter
  plane. Offline (mesh.bin triangles of every collider in the doorway volume): floor colliders are continuous
  across the plane; the frame colliders are posts and lintel only; the hall's `_BALLISTIC_metalthin` has an
  11 cm rail there; and `Arena_Equator_TeamFight_DesignStuff` has `LEVEL-BORDERS TF/LevelBorder (83)`, a
  BoxCollider on layer 29 spanning (-9.9,-0.4,33.8)..(-9.5,5.4,38.1) — exactly the doorway. The client removes
  all 41 LevelBorder colliders at raid start (`MapBorderRepairs`, `RemoveTeamFightBorders`), the bake did not.
  Fix: `"LevelBorder"` added to `BakeNavMesh.ExcludedNamePrefixes`; second rebake.
- Death: teleported next to a scav, killed within a minute (chest 0/85). No exception in the log. On this host
  death is intercepted by the user's Redeploy/KillcamReplay mods ("Suppressing death fade during replay",
  "Death intercepted. Respawns used: 0/5", Redeploy's "You died" dialog) — `DeathScreenEffectsGuard` logged
  nothing, i.e. either both effect components exist on the camera or `OnHealthDied` was never raised. The guard
  is therefore *installed and harmless* here, not exercised; a raid on D:\SPT41vanilla (no respawn mods) is the
  test that would show the Cam2 path.
- Third-party noise unchanged: Bot Placement System's `TryToSpawnInZonePatch EXCEPTION` once per wave.

## Host test 2 (2026-09-20 19:33, second rebake, EFT restarted)

A first attempt without restarting EFT loaded the previous bake again (`10305 vertices` — a map bundle stays
cached in the process; AGENTS.md "Traps"). After the restart: `added navmesh for 'arena_equator': 10404
vertices, 4572 triangles`; the bake had excluded `41 source(s) ... LevelBorder*` on top of the 24 from the first
rebake. `uibridge_teleport dryRun` from the All spawn (-4.5,0.1,38.5): atrium (0,0.2,2), Bear booth spawn
(5,0.1,-33.5), FCK hall corner (-11.5,0.1,37), the shutter plane (-9.65,0.1,36) and the Usec niche
(-11.7,0.1,48.6) are all **reachable** — the ground floor including both booths and the FCK hall is one NavMesh
component. Patrol way: `6 point(s) and 59 sub point(s) ... in 1 NavMesh-connected component(s)`.

PMC hostility: a BEAR squad spawned (Katto, Lave). Teleported to 6 m from Katto: both PMCs in
`SAIN : Combat Layer` immediately (`uibridge_bots`), the player (low HP from the previous death) was killed by
Katto 6 s later (`Death replay: killer=Katto`). No exception in the log. Death handling on this host is
Redeploy's (dialog, "End raid"); `DeathScreenEffectsGuard` again logged nothing (see host test 1).

Stage 1 result: ballistic twins and the NavMesh bake are verified on the host; the death guard is installed
but only exercisable on an install without Redeploy/KillcamReplay (D:\SPT41vanilla — the user's raid).
