# Bots, stage 1 — scavs spawn and roam on Equator (2026-09-19)

User decision after the lighting round: make one map really playable, bots first; Equator is the map,
tests on the GPU host with SAIN present (the vanilla testbed has no SAIN).

## Where we started (survey, read-only workflow + critic, 2026-09-19)

Bots were out of scope by SPEC decision #8; every bot-related client patch only stops
`BotsController.Init` from throwing on empty data. Gates that were all closed at once:

| Gate | State before | Evidence |
|---|---|---|
| `waves` / `BossLocationSpawn` | `[]` | `tools/server/make_base_json.py` EMPTY_LISTS |
| `SpawnPointParams` | key removed | same, `REMOVE` |
| `OpenZones` | `""` | same, line 99 |
| `BotMax` | 0, never overridden | `PostDbLoadService.AdjustLocationBotValues` writes only keys in `configs/bot.json` maxBotCap (no `arena_*`) |
| scene | `BotZone_Main` with 3 Player-only markers, `PatrolWays` empty, no cover/voxel data | `AuthorScriptsScene.cs:113-181` |
| SAIN | `LocationClass.parseLocation` knows only vanilla ids → `ELocation.None`, error spam | SAIN.dll decompile |
| Waypoints | no bundle for `arena_*` (harmless) | plugin folder |
| exits | `[]` and `AllExtracts = []` forced | `ArenaMapRegistration.cs:136` (stage 2) |

## Mechanics that decide the design (EFT 0.16.9.5 decompile)

- `BaseLocalGame.cs:526`: `SpawnPointsCollection.CreateFromScene(LocalDateTimeFromUnixTime(location.UnixDateTime), location.SpawnPointParams)`.
- `SpawnPointsCollection.cs:152-245`: with params present and `IsValid` (scene `LocationExportInfo.DateTime <= backend UnixDateTime` —
  both are `BuildBundles.BuildUnixDateTime` 1789257600), `FixSpawns` DELETES scene markers whose Id is not in the params (our three
  GUID-named authored markers, logged as "Warning!!!") and CREATES a marker for every param without a scene twin via
  `SpawnPointMarker.Create`.
- `SpawnPointMarker.cs:349-376`: `Create` copies Id/Sides/Categories/Infiltration/Delay, and when `BotZoneName` equals a
  `BotZone` GameObject NAME it adds the marker to that zone's `SpawnPointMarkers` — the list waves spawn from.
- `BotSpawner.cs:404, 645`: a wave's `SpawnPoints` string is resolved as `zone.name == wave.SpawnAreaName`; our zone is
  `BotZone_Main`. `BotsController.Init` (`BotsController.cs:168-236`): `_canSpawn = botEnable && any zone has markers`.
- `LocalGame.cs:176-190`: the alive-bot cap comes from the raid's `EBotAmount` (15-35), not from `Location.BotMax`;
  `WavesSpawnScenario.Run` only with `OldSpawn && waves.Length != 0` (Equator: `OldSpawn = true`, like Factory).
- `BotZone.Init` (`BotZone.cs:113-170`): empty `PatrolWays` is a 0-iteration loop; markers off the navmesh are grounded.
  Without patrol ways vanilla bots have nothing to patrol along — with SAIN, movement comes from SAIN's own layers.

## Stage-1 change (server only, no bundle rebuild)

- `tools/maps/equator.bots.json` (new sidecar; the descriptor itself stays untouched because Unity's JsonUtility reads it):
  zone `BotZone_Main`, `max_bots` 8, the three player spawns at their in-game positions (UiBridge `places`, 2026-09-19),
  seven Bot-category spawn spheres (r 20-30 m) at navmesh-verified points (atrium centre, west corridor, east, north-east,
  north-west, south-west, ring floor 2 west), five assault waves (10-30 s, 90-150, 240-330, 420-540, 600-720; 1-3 slots).
- `make_base_json.py`: emits `SpawnPointParams`, Factory-shaped `waves`, `OpenZones`, `BotMax` when the sidecar exists;
  every other map's output stays byte-identical.
- `ArenaMapRegistration.ApplyBotCap`: `BotConfig.MaxBotCap[key] = BotMax` for bot-enabled maps (the server's own
  `AdjustLocationBotValues` then writes `Location.BotMax` from it — it runs at OnLoadOrder 200000, after our 190000).
- Tests: `BaseJsonTests` now asserts the player + bot spawn points, unique ids and waves on the zone (the old
  "SpawnPointParams must be absent" invariant is gone by design).

## SAIN shim (client, soft dependency)

`SainLocationShim.cs`: SAIN's `LocationClass.findLocation()` (private, per tick) is prefixed while one of our maps is
loaded; it writes `[Bots (<key>)] SainLocation` (Equator → Interchange, Bay5 → Customs, others → Factory) into
`Location`, flips `_foundLocation`, and skips the original — SAIN gets its per-location difficulty multipliers and the
per-tick `parseLocation` error line stops. Everything is resolved via `AccessTools.TypeByName`; without SAIN one info
line, no patch. `Plugin` carries a soft `BepInDependency` on `me.sol.sain` so SAIN's assembly is loaded first. SAIN's
own movement does not read `BotZone.PatrolWays` (0 references in its decompile) — empty patrol ways are fine with SAIN;
vanilla-brained assault bots would stand still (and `PatrolAssaultLayer.EndSimplePatrol` can NRE on `Way == null`),
and `EBotAmount.Horde` must be avoided (`WavesCountControlScenario` indexes patrol zones).

## Review (three-lens workflow + refutation, 2026-09-19)

Ten agents; the EFT-spawn lens found nothing, six findings survived refutation and are fixed: the shim's fallback
`Enum.Parse` was unguarded (now `ParseOrNull`, names validated once at install), the prefix wrote through reflection
every tick (now mirrors SAIN's own `_foundLocation` guard), `_loggedForMap` never reset (scene-unload hook),
`CurrentMap` read twice, `ApplyBotCap` had no tests (two theories over every map) and stored the raw key while
`BotController.GetBotCap` lower-cases its lookup (now normalised). Server tests: 256 green.

## Host test

Not run yet: at 19:3x three other sessions (two Codex, one Claude) were active on the GPU host and the user chose
to wait rather than restart the SPT server underneath them. Build `61aaedd7` (client) / `503e95ad` (server) is
deployed to D:\SPT41; the host already holds the client DLL and base.json, the server DLL follows once the server
process releases it (Syncthing cannot replace a loaded mod DLL). D:\SPT41vanilla deliberately not deployed: it has no
SAIN, so vanilla-brained scavs would stand still without patrol ways and `PatrolAssaultLayer.EndSimplePatrol` can
NRE on `Way == null` — not a meaningful test bed for this stage.

Test plan (host, once free): restart SPT.Server (mod DLL + base.json), then EFT; Equator, Clear, bot amount
Low/Medium (never Horde, see above). Expect in the log: `Warning!!! 3 SpawnPointMarker will be deleted`,
`Warning!!! 10 SpawnPointMarker will be added`, no `Can't spawn wave cause can't find zone`, our
`[ArenaMapPort][SAIN] arena_equator: SAIN location set to Interchange`, and no SAIN `parseLocation` error lines;
in game `uibridge_player` shows `Bots alive` > 0 within ~30 s (wave 0), positions changing over time.
