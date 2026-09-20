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
every map by `BoothDoorRepairs`), `SPAWN_BOUND*` boxes (generic — `SpawnPlacementRepairs`) and, on Equator,
one static roller shutter. Anything map-specific goes into `opened_barriers` as a hierarchy path (object
names repeat), mirrored by `ArenaMapDescriptor.OpenedBarriers` (test `OpenedBarriers_DescriptorMirrorsSidecar`).
`BakeNavMesh` excludes the same objects from the NavMesh sources, so a new entry means a bundle rebuild.
Find candidates with the in-game F10 inspector (aim at the obstacle) and the scene dump.

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
