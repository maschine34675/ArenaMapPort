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
every map by `BoothDoorRepairs`) — since 2026-10-17 every such part is registered with `SpawnGates`, so a mode plugin can hold the enclosures closed and release them later (docs/progress/spawn-gates.md), `SPAWN_BOUND*` boxes (generic — `SpawnPlacementRepairs`) and, on Equator,
one static roller shutter. Anything map-specific goes into `opened_barriers` as a hierarchy path (object
names repeat), mirrored by `ArenaMapDescriptor.OpenedBarriers` (test `OpenedBarriers_DescriptorMirrorsSidecar`).
`BakeNavMesh` excludes the same objects from the NavMesh sources, so a new entry means a bundle rebuild.
Find candidates with the in-game F10 inspector (aim at the obstacle) and the scene dump. A path may be a single
segment: a bare name addresses the scene root object itself (AirPit: `BUS_DOORS`, `PASSPORT_CONTROL_DOORS`).

Two optional spawn-gate keys (2026-10-18; mirrored by `ArenaMapDescriptor.StartGates` / `HeldGates`; a map that declares
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
--points tools/maps/<stem>.bots.json --points tools/maps/<stem>.exits.json` (AirPit adds `--live-core-points`; the Arena
NavMesh source defaults to AirPit's, see `--assets`/`--path-id`). Its greedy list names the smallest set of props whose
exclusion joins all probe points; `--descriptor` applies the barrier and exclusion lists above (held gates included;
`bake_bounds` is not modelled).

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

1. `[ArenaMapPort][BotSpawn] wave fired` and `ActivateBotCallback` lines; `uibridge_bots` shows brain layers
   (`PatrolAssault`/`Utility peace` = patrolling; a null layer after 30 s = activation failed, turn on
   `[Bots] ActivationDiagnostics` and read the finalizer lines).
2. Patrol-way line: `zone '...' had no patrol ways — built N way(s) ... in C NavMesh-connected component(s)` —
   more than one component means the spawn points sit on NavMesh islands; move them or fix the bake.
3. `spawn openings:` block — every player spawn's rays `clear` or hitting a known prop.
4. Extract through each gate once (`outcome: RUNNER` in the server console).
5. Note anything the map needs that Equator did not — that is the next repair.
