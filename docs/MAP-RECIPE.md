# Map recipe — bringing a built map to Equator's level

Every one of the 14 maps is built and staged (`docs/progress/remaining-maps.md`). What Equator has on top
is authored per-map data plus a host raid per step. The client repairs (AI holders, voxel grid, patrol ways,
brain tables, APBS/SAIN shims, spawn placement, booth doors, ballistic twins) are map-agnostic and need no
code change; they read the descriptor and the sidecars below.

## 0. Prerequisites

- The map is in `ArenaMapRegistry` (Shared) and has `tools/maps/<stem>.json`; its bundles are in
  `work/build/<stem>/` (Equator: `work/build/full/`) and staged in `dist/`.
- A first plain raid on the host has been done (visual/collision baseline; watch for the solid-white-block
  collision class from `docs/progress/fix-enabled-state.md`). Terrain maps (Custom, Shoreline, Woods) still
  owe this first raid.

## 1. Spawn markers — `tools/maps/<stem>.bots.json`

Read by `tools/server/make_base_json.py` (`apply_bots`, `apply_boss_waves`) into the map's `base.json`
(`SpawnPointParams`, `waves`, `BossLocationSpawn`, `BotMax`, `OpenZones`). Positions are world coordinates
from the scene dump (`work/extract/<stem>/scenes/*.json`; compute world positions through the parent
chain, see `AGENTS.md` Layout) or from `uibridge_player` while standing there.

```json
{
  "_comment": "why these points; date; host test that confirmed them",
  "zone": "BotZone_Main",
  "max_bots": 8,
  "player_spawns": [
    { "id": "arena_<stem>-player-all", "name": "SpawnPoint_Player_All", "sides": ["All"],
      "position": [-4.5, 0.1, 38.5], "rotation": 180.0, "radius": 5 }
  ],
  "bot_spawns": [
    { "id": "arena_<stem>-bot-atrium", "position": [0.0, 0.2, 2.0], "radius": 30, "categories": ["Bot", "BotPmc"] }
  ],
  "waves": [ { "time_min": 10, "time_max": 30, "slots_min": 2, "slots_max": 3 } ],
  "boss_waves": [
    { "boss_name": "pmcUSEC", "escort_type": "pmcUSEC", "boss_zone": "BotZone_Main", "boss_chance": 50,
      "escort_amount": "0,0,0,0,2,2,2,1,1,1,1,1,0,2,3" }
  ]
}
```

- `player_spawns`: one per side you want (`All`, `Usec`, `Bear`); the client stamps the map's
  `InfiltrationName` onto Player-category points (`SpawnInfiltrationRepairs`). Keep them 10 m or more from
  any exit box (an exit within ~5 m extracts a fresh spawn) and out of enclosed props — `SpawnPlacementRepairs`
  moves a boxed-in point but cannot judge a narrow niche; walk it.
- `bot_spawns`: the points the patrol-way builder chains into ways (`PatrolWayRepairs`, one way per
  NavMesh-connected component, up to 8 points per way). Six well-spread ground-floor points gave visible
  patrols on Equator; more points = denser patrol graph. `categories` must include `BotPmc` for PMC squads.
  Do not put points on decorative floors outside the NavMesh volume (Equator's upper rings).
- `waves`: scav waves (Savage/assault), `slots_*` bots per wave, `time_*` seconds after raid start.
- `boss_waves`: PMC squads are `BossLocationSpawn` entries with `BossName` pmcUSEC/pmcBEAR (Factory's
  shape, `Time -1`, `IgnoreMaxBots true` — they do not count against `max_bots`); `boss_zone` names the
  zone explicitly. Leave the list empty for scavs only.

## 2. Exits — `tools/maps/<stem>.exits.json`

Read by `make_base_json.py` (`apply_exits`) into `base.json` `exits`; the client authors the matching
`ExfiltrationPoint` objects at raid start (`ExfilRepairs`) and lights a smoke signal on each
(`ExfilSignals`). The descriptor's `Exits` array must mirror this file (test
`BaseJsonTests.Exits_DescriptorMirrorsSidecar` for Equator; add the same test for the new map).

```json
{ "exits": [
  { "name": "<Map> North Gate", "position": [2.0, 0.1, 46.0], "box_size": [4.0, 3.0, 4.0], "rotation": 0.0,
    "exfiltration_time": 8, "chance": 100, "type": "Individual" }
] }
```

Place the box on real floor (the client samples the NavMesh within 6 m and logs `NotPresent` if it fails),
on the far side of a spawn zone, 10 m from the nearest spawn. `name` doubles as the locale key
(`ApplyExitLocale`). ArenaMode does not need exits; they are the way to end a test raid on foot.

## 3. Barriers the client must open — `opened_barriers` in `tools/maps/<stem>.json`

Arena closes spawn areas per round with sliding booth doors (`Door_Sliding_Respawn*`, generic — opened on
every map by `BoothDoorRepairs`) — since 2026-10-17 every such part is registered with `SpawnGates`, so a mode plugin can hold the enclosures closed and release them later (docs/progress/spawn-gates.md), `SPAWN_BOUND*` boxes (generic — `SpawnPlacementRepairs`). Equator's FCK roller shutter
(`equator_roller_shutters_02 (9)`) was an `opened_barriers` entry until 2026-10-23 and is not any more: Arena never opens it (it walls
it off with `LevelBorder (83)`), the spawns stand on Arena's own markers behind the booth doors and the shutter is a baked wall
([equator-booth-spawns.md](progress/equator-booth-spawns.md)), so Equator's list is empty. Anything map-specific goes into
`opened_barriers` as a hierarchy path (object
names repeat), mirrored by `ArenaMapDescriptor.OpenedBarriers` (test `OpenedBarriers_DescriptorMirrorsSidecar`).
`BakeNavMesh` excludes the same objects from the NavMesh sources, so a new entry means a bundle rebuild.
Find candidates with the in-game F10 inspector (aim at the obstacle) and the scene dump. A path may be a single
segment: a bare name addresses the scene root object itself (AirPit: `BUS_DOORS`, `PASSPORT_CONTROL_DOORS`).

Two rules since 2026-10-23 (Bay5's gates, [equator-booth-spawns.md](progress/equator-booth-spawns.md)):

- A path that exists twice (two sibling objects of one name: Bay5's `OLD_WAREHOUSE_DOORS/Gates_Depot_Frame`, the gates at world
  (50.574, -3.895, 7.525) and (69.045, -3.883, -6.317)) is listed ONCE and means EVERY instance. The client opens every instance
  (`BoothDoorRepairs.FindAllByPath`; `FindByPath` still returns the first one, for `LampFillRepairs`' template lookup). The bake
  resolves every instance (`BakeNavMesh.FindAllInstances`) when the barrier's pose rows carry `at`, and the first one (`Transform.Find`)
  for a barrier without rows, as before. The descriptor stays free of duplicate strings (`Distinct` is tested) and a door pose tells
  the instances apart by its `at` (3a). No other opened barrier of Equator, Bay5, AirPit or Bowl has a repeated path (resolved offline
  on the four packages, 2026-10-23), so nothing else changes. The offline mirror `port_colliders.resolve_barriers` still takes the
  first instance.
- A barrier that has pose data (3a) keeps every part WITHOUT a pose standing as structure (Bay5's `Telfer_Help` posts next to the
  frames) and only the posed parts move; the bake drops only the posed parts' subtrees and bakes the rest as walls. A barrier without
  any pose keeps the old rule: every part whose name does not contain `frame` is switched off and left out of the bake.

Two optional spawn-gate keys (2026-10-17; mirrored by `ArenaMapDescriptor.StartGates` / `HeldGates`; a map that declares
`held_gates` must also declare `start_gates`) and three optional bake-only keys (PLAN-AIRPIT item 8; Equator and Bay5 carry
none of the five, so their bake is unchanged), mirrored by `ArenaMapDescriptor.NavMeshBakeExclusions` / `BakeBounds` /
`NavMeshBakeLayers` and checked by `validate_map_descriptors.py` (shape and drift) and `AirPitMapTests`:

- `start_gates`: the subset of `opened_barriers` a mode plugin may hold closed through `SpawnGates` (offered to it before
  the raid-start opening; AirPit: `BUS_DOORS` only). Empty or absent = every opened barrier is offered (Equator, Bay5).
- `held_gates`: the subset of `opened_barriers` that stays CLOSED at raid start in every mode, sealed for bots by
  `SpawnGates`' carvers from the NavMesh install on, and opened only by `SpawnGates.Release` (AirPit: the passport control
  `PASSPORT_CONTROL_DOORS` + `PASSPORT_CONTROL_EMISSIVE`, the gate of its original north team box). Disjoint from
  `start_gates`. The bake drops both kinds through `opened_barriers`; sidecar and C# descriptor change in one commit.
- `navmesh_bake_exclusions`: hierarchy paths (same syntax) whose whole subtree, the listed object included, is dropped from
  the NavMesh sources while staying active at runtime. For props that stay but that Arena's own NavMesh walks through and
  that cut a route (AirPit: two closed ramen shops, a roll-door, a fence panel). Anything the client DISABLES at runtime
  (at raid start, or on a mode plugin's release for a held gate) is not listed here: it belongs in `opened_barriers`. A
  path that matches nothing fails the build.
- `bake_bounds`: `[min_x, max_x, min_y, max_y, min_z, max_z]`, the bake volume instead of the renderer union (which spans
  kilometres when far scenery sits in the gameplay scene).
- `navmesh_bake_layers`: layer indices the bake may collect instead of "every layer but Water" (AirPit: Arena's own
  NavMeshSurface mask 9, 11, 13, 18, 30). Non-trigger boxes on the Triggers layer (13) and the LevelBorder layer (29) are
  still dropped.

Find the objects with `python tools/validate/navmesh_diff.py --package work/extract/<package> --descriptor tools/maps/<stem>.json
--points tools/maps/<stem>.bots.json --points tools/maps/<stem>.exits.json` (AirPit adds `--live-core-points`). The Arena
NavMesh source defaults to AirPit's; `--map bowl` selects another row of `ARENA_SOURCES` (NavMeshData, NavMeshSurface layer
mask, AI scene, default package), `--plan tools/extract/plans/<stem>.json` takes the AI scene from the plan's `ai` role (the plan of a map that has its own row
must go with that row's `--map`; the tool refuses the mix), and
`--assets`/`--path-id`/`--arena-layer-mask` override single values. The oracle reads the NavMeshData in WORLD coordinates
(its `m_Position`/`m_Rotation` are applied; Bowl's is a 90 degree yaw, AirPit's the identity), so a new map needs nothing
beyond its table row. Its greedy list names the smallest set of props whose
exclusion joins all probe points; `--descriptor` applies the barrier and exclusion lists above (held gates included;
`bake_bounds` is not modelled).

### 3a. Door poses — `tools/maps/<stem>.doors.json` (optional: Bowl, Equator, Bay5)

Some barriers are not static in Arena: an `Animator` plays a Mecanim clip that moves the gate parts, or Arena's DOTween script
`DotAnimator` tweens them to an end value (Equator's booth doors, Bay5's rollers and depot gates; schema 2, 2026-10-23: the decode, the
`source`, `ease`, `curve` and `at` fields and the lookup API are in [door-poses.md](progress/door-poses.md)). Bowl is the clip case
(the BTR reverses out of
its slot, the ambulance drives out of its bay, the army gate's leaves swing open, the second gate's leaf slides 3.8 m sideways onto
its rail; it does NOT sink: the parent is rotated -90 degrees about x, so the clip's local y is a horizontal world direction and the
leaf keeps its world height). The raid-start opening
used to hide these parts; the sidecar carries the OPEN END pose of every node a clip moves, next to its closed pose, so the client
can park the part in its open position instead (user decision 2026-10-22). Read a pose in the frame of the node's parent, not as a
world direction: a "local y" number says nothing about up or down until the parent chain is known (compute the world pose through
the scene hierarchy before describing a motion in prose). Before adding a map, check whether it has any:
`tools/extract/extract_door_poses.py` lists the Animators and DotAnimators below the map's `opened_barriers` (AirPit's three bus doors
use a controller with no clip, so they are reported as ignored; its DotAnimator passport doors come in a later change set). Full method, table and limits:
[docs/progress/door-poses.md](progress/door-poses.md).

```powershell
python tools/extract/extract_door_poses.py --map arena_bowl --arena auto --package work/extract/bowl-native-v2 --output tools/maps/bowl.doors.json --csharp
python tools/validate/validate_map_descriptors.py       # must print OK
```

The sidecar is generated output: never edit it by hand. A new map needs a row in `MAP_SOURCES` (scene name, descriptor), the scene
in `tools/bundle/key-sources.json` (that is where the level index comes from) and a Windows install of Arena with UnityPy. Shape:

```json
{ "schema_version": 2, "key": "arena_bowl",
  "source": { "scene": "Arena_Bowl_TeamFight_DesignStuff", "level": 48, "assets": "sharedassets48.assets", "arena_version": "0.6.1.0.48206" },
  "poses": [ { "barrier": "DOORS_SPAWN_A", "part": "BTR_82_closed", "node": "", "clip": "BTR_82_Respawn_Animation", "source": "clip",
               "seconds": 7.683, "ease": "smoothstep", "curve": null, "at": null,
               "closed": { "position": [51.182, -0.035, -21.498], "euler": [-90.0, 0.0, 83.511] },
               "open":   { "position": [45.61, -0.04, -22.14],   "euler": [-90.3, 266.474, -182.962] } } ] }
```

- `barrier`: an `opened_barriers` path. `part`: the barrier root's direct child (the object `BoothDoorRepairs` deactivates; a part whose
  name contains "frame" is never hidden and never listed). `node`: the transform path below the part that the clip animates, `""` = the
  part itself. Positions and euler angles are LOCAL to the node's parent (`localPosition` / `localEulerAngles`); euler angles are the
  clip's own raw values (multi-turn spins survive), to be interpolated component-wise (a `dotanimator` or `borrowed` row is interpolated
  as a quaternion slerp between the two euler triples instead). `at` is null or the WORLD position of the barrier root instance: it tells
  repeated hierarchy paths apart (Bay5's two depot gates, Equator's booth assemblies).
- One entry per animated node whose open pose differs from its closed pose by more than 1 cm or 1 degree. The closed pose is the
  scene's own local transform (cross-checked against the clip: 2 cm / 1 degree warning, 10 cm / 5 degrees stop); the open pose is the
  clip's value at its stop time. The open clip of a controller is the one whose name does not end in "closed".
- Mirrored by `ArenaMapDescriptor.DoorPoses` (`ArenaDoorPose[]`, empty on a map without poses; `DoorPosesOf(barrier, part)` returns one part's
  poses, the part itself first; the position-aware overloads `DoorPosesOf(barrier, part, x, y, z)` and `DoorPosesOfBarrier(...)` are in door-poses.md). `validate_map_descriptors.py` checks the sidecar's schema, that every barrier is an `opened_barriers`
  entry, and that sidecar and registry carry the same poses in the same order with the same numbers; `BowlMapTests` does the same in C#
  and `test_door_poses.py` pins that the registry holds exactly the text `--csharp` prints. A map with poses must keep the sidecar and
  the C# literals in one commit. Re-run the tool after an Arena update (the real-data test fails if the installed clips changed).

## 4. Generate, test, deploy

```powershell
python tools/server/make_base_json.py --map tools/maps/<stem>.json          # writes ArenaMapPort.Server/db/arena_<stem>/base.json
dotnet test ArenaMapPort.Server.Tests/ArenaMapPort.Server.Tests.csproj -c Release
dotnet build ArenaMapPort.Server/ArenaMapPort.Server.csproj -c Release
pwsh -NoProfile -File tools/deploy-to-spt.ps1 -SptRoot 'D:\SPT41' -Confirm  # then restart the host server
```

`make_base_json.py` discovers the `.bots.json`/`.exits.json` siblings of the map file; without them it emits
`BotMax 0`, no waves, no exits (the "geometry only" state of the other 13 maps). The server mod applies the
brain-table stand-in row (`ApplyBrainTable`/`ApplyPmcBrainTables`, Equator → `interchange`, others →
`factory4_day`) and the bot cap (`ApplyBotCap`) for every registered map on load.

## 5. Host raid checklist (log evidence into `docs/progress/<stem>-bots.md`)

1. `[BotSpawn] wave fired` and `ActivateBotCallback` lines; `uibridge_bots` shows brain layers
   (`PatrolAssault`/`Utility peace` = patrolling; a null layer after 30 s = activation failed, turn on
   `[Bots] ActivationDiagnostics` and read the finalizer lines).
2. Patrol-way line: `zone '...' had no patrol ways — built N way(s) ... in C NavMesh-connected component(s)` —
   more than one component means the spawn points sit on NavMesh islands; move them or fix the bake.
3. `spawn openings:` block — every player spawn's rays `clear` or hitting a known prop.
4. Extract through each gate once (`outcome: RUNNER` in the server console).
5. Note anything the map needs that Equator did not — that is the next repair.
