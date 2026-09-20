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

## Host test 1 (2026-09-19 19:5x, build 61aaedd7 / 503e95ad)

Server restarted with the new mod DLL + base.json (all 14 maps registered, no server errors), EFT
restarted, Equator, Clear, AI amount "As in online". Results:
- Client: `[RaidFix] set Infiltration="ArenaEquator" on 7 SpawnPointMarker(s)` — exactly our seven Bot
  points, i.e. `FixSpawns` accepted the params and `SpawnPointMarker.Create` built them (the three player
  spawns already carried the infiltration). `[ArenaMapPort][SAIN] arena_equator: SAIN location set to
  Interchange`, and not one SAIN `parseLocation` error line all raid.
- Server: `/client/game/bot/generate` was requested at raid load, but every single generation failed —
  1320 × `Failed to generate bot #n (…): Map 'arena_equator' not found.` thrown from
  `ProgressiveBotSystem.Models.MapRangeWeights.get_Item` (APBS, the host's progressive-bot mod) inside
  its `GenerateInventoryPatch` prefix on SPT's `BotInventoryGenerator.GenerateInventory`. So no bot profile
  ever reached the client: `Bots alive: 0` for the whole raid. The waves themselves fired (the generate
  requests prove it) — the block is entirely in APBS's compiled per-map switch.
- Also on the host: acidphantasm's Bot Placement System (client + server). Its client patches were read:
  unknown maps get `_maxCount = 0` (`SetMaxBotCountPatch`, which BotSpawner.CheckOnMax treats as "no
  cap"), a 10 m scav spawn-distance fallback, and no hot-zone lookups — nothing that blocks vanilla waves;
  its server part logged no errors. Not shimmed for now.

Fix: `ApbsCompat.cs` — a soft server-side Harmony prefix (SPT `AbstractPatch`, resolved by type name)
on `MapRangeWeights.get_Item` that rewrites an Arena key to a vanilla stand-in (Equator → Interchange,
others → factory4_day). No-op without APBS; unit test on the remap table.

## Host tests 2–4 (2026-09-19 20:1x–21:2x, server a6dc5768 with the APBS patch)

Server restarted with `ApbsCompat` (log: `APBS compatibility: patched ProgressiveBotSystem.Models.MapRangeWeights
indexer — 14 Arena map key(s) …`); `/client/game/bot/generate` now returns 45 assault + marksman profiles, 0
failures. In the raid: `BotZone.Init 'BotZone_Main': markers=7 onNavMesh=7 points=7 maxPersons=-1 patrolWays=0`,
every wave fired (`_canSpawn=True`), `TryToSpawnInZoneInner … -> STARTED`, and still `Bots alive: 0` for the
whole raid — with a scav BODY standing motionless at the atrium point (screenshot; RespawnLeakProbe:
`bodiesEverExisted=3 registeredPlayers=2`). Every later point read `valid=False`: the un-hidden body sits inside
the spawn spheres and `IsNotCollided` counts it as a non-AI player. Nothing in BepInEx, Player.log, errors.log or
traces.log. The finalizer probes (`BotSpawnProbe`, build 26acf9d2) on `BotCreatorClient.CreateBot`, `method_3`,
`BotOwner.Create`, `PreActivate` logged nothing either: `BotOwner.Create ->` never appeared, so the async chain
stopped between `LocalPlayer.Create` (the body) and `BotOwner.Create`.

Root cause (decompile): `BotCreatorClient.CreateBot` (BotCreatorClient.cs:135-147) does
`CoversData.AICorePointsHolder.GetCorePoint(bornInfo.CorePointId)` → `CorePoints.FirstOrDefault(...)`
(AICorePointHolder.cs:112). Our scenes ship no `AICorePointHolder`; `AICoversData.CreateOrFind`
(AICoversData.cs:300-308) AddComponents a fresh one whose `public List<AICorePoint> CorePoints;` (line 61) has no
initializer → `ArgumentNullException` inside an `async Task` observed only by `.HandleExceptions()` (an extension
outside Assembly-CSharp; it logs nowhere we can see). A Harmony finalizer on the stub of an async method never sees
exceptions thrown after the first `await` — they land in the Task. Hence the silence.

## Host test 5 (21:30, client 4a03e66d — core-point fallback)

`AICorePointRepairs.cs`: a `BotsController.Init` prefix creates the holder, its list, and one synthetic
`AICorePoint` (id 0 / group 0 = the `CorePointId` our SpawnPointParams carry) at the centroid of the Bot markers;
`AICorePointHolder.GetCorePoint` gets a null-list guard; a `BotOwner.Create` postfix assigns the nearest core when
the id lookup returns null (adapted from Icebreaker's `Patch_BotStartCorePoint`, MIT). Log:
`AICorePointHolder had no core points … added synthetic AICore ID:0 CG:0 at (-6.9, 1.6, 2.8) (centroid of 7 Bot
spawn marker(s))`, then 20 s into the raid `BotOwner.Create -> 'Bot1' startCorePoint=0` — and the next link threw:
`BotOwner.PreActivate THREW for 'Bot1': NullReferenceException at KhorovodBotGameEvent..ctor` —
`CoversData.AIPlaceInfoHolder.Places` (AIPlaceInfoHolder.cs:19), the same null-list shape on the next holder.

Generalized: `EnsureCoversHolders()` creates all eight holder components `CreateOrFind` would otherwise
AddComponent (AIManualPointsHolder, AICorePointHolder, AIMinesPositionsHolder, AIDangerPlacesHolder,
AIPlaceInfoHolder, AIPatrolsData, AIVoxelesData, AICoversData) before `BotsController.Init` and fills every null
`List<>`/array/`Dictionary<,>` instance field with an empty instance by reflection — the authored-but-empty shape
BSG's scene deserialization would produce. (`AIVoxelesData.VoxelsList` keeps its dedicated guard.)

## Host test 6 (21:39, client d1fd94de — generic holder ensure) — FIRST LIVING BOTS

The generic fill tripped on its seventh holder: `AIVoxelesData.VoxelesArray` is `NavGraphVoxelSimple[,,]` and
`Array.CreateInstance(elementType, 0)` makes a rank-1 array → `ArgumentException`, which aborted the ensure before
`AICoversData` and the synthetic core point (`core-point fallback failed: … cannot be converted to type
'NavGraphVoxelSimple[,,]'`). The six holders before it were filled, and that was already enough:
`BotOwner.Create -> 'Bot1' startCorePoint=NULL` (our postfix warned: no core points at all) and then, for the
first time, `ActivateBotCallback: bot='Bot1' role=assault … registered with BotsController` at 14 s, `Bot2` at
99 s, `uibridge_player`: **`Bots alive: 2 · nearest: … (assault) 47.8m hidden, … (assault) 52.3m hidden`**.
No `PreActivate THREW`, no NRE from the null `StartCorePoint` within the two minutes observed (SAIN drives these
bots; BSG's cover/path readers of `StartCorePoint.ConnectionGroupId` were evidently not hit yet — the synthetic
core point stays in as the guard for them). Fixed: rank-aware `Array.CreateInstance(elementType, new
int[rank])`, and each holder's fill is wrapped so one odd field cannot stop the rest.

## Host test 7 (21:46 + 21:57, clients 0f3b2e7c / 45527e75) — alive but frozen, then the reason

With the rank-aware fill and the synthetic core point: `AI covers holders: created 8 missing component(s) …
initialized 10 null collection field(s)`, `BotOwner.Create -> 'Bot1' startCorePoint=0`, `ActivateBotCallback`
for Bot1 (14 s) and Bot2 (99 s), no warnings — and both bots stayed at their spawn points for the whole raid
(`uibridge_player` distances 47.8 m / 60.0 m, unchanged over three samples two minutes apart).

Why: `BotOwner.method_10` — the real activation, ~45 sub-system `Activate()` calls after
`ActivateBotCallback` — is wrapped in `catch (Exception) { BotState = EBotState.ActiveFail; }`
(BotOwner.cs:940-1000). One throw and the bot is a statue, silently. A finalizer on `method_10` itself sees
nothing (the catch is inside), so `BotSpawnProbe` now reads `method_10`'s IL (`PatchProcessor.ReadMethodBody`)
and puts a finalizer on every method it calls (46 targets) plus a postfix that logs `BotState`/active layer.
Second raid: `activation step BotMover.Activate THREW (instance BotMoverImpostor): IndexOutOfRangeException at
BotVoxelesPersonalData.GetVoxelSafe` and `method_10 done: 'Bot1' state=ActiveFail … patrolWays=0`. The voxel
grid is `NavGraphVoxelSimple[0,0,0]` (`AIVoxelesData.RestoreData` builds it from `MaxVoxelesValues` = 0) and
`Mathf.Clamp(i, 0, MaxX - 1)` returns -1 for an empty axis.

Fix: `EnsureVoxelGrid` authors an empty coarse grid before `BotsController.Init` — BSG's own 10 × 5 × 10 m
cells over the spawn markers' bounds plus a 60/20/60 m margin (Equator: roughly 17 × 11 × 21 cells), one
`NavGraphVoxelSimple` per cell with `HaveNavMesh` and no covers; `RestoreData` then builds the 3-D array from it
exactly as for a baked map. `IsRestored()` (`Length > 1`) has no callers; `BotPointControl.SetWay(null, …)` is a
no-op, so zero patrol ways do not throw. The activation-step finalizers stay in the probe (off with
`[Bots] SpawnDiagnostics = false`).

## Host test 8 (22:05, client 1f93c34a — voxel grid) — Active, still frozen

`AIVoxelesData had no voxels … authored an empty 18×12×22 grid (4752 cells of 10×5×10 m) from
min=(-90.0, -19.9, -93.5)`, then `BotOwner.method_10 done: 'Bot1' state=Active pos=(0.0, 0.1, 2.0)
activeLayer=null`, no activation-step exception, no warnings — and Bot1/Bot2 still did not move (36.5 → 36.7 m,
28.4 → 29.3 m over four minutes). New in the log, from SPT itself:
`[ModulePatch] Error running CustomAiPatch PatchPrefix(): Bots were refreshed from the server but the assault
cache still doesn't contain data` — spt-custom's `AIBrainSpawnWeightAdjustment.GetAssaultScavWildSpawnType`
(AIBrainSpawnWeightAdjustment.cs:55-66) looks the map id up in SPT's `bot.json` `assaultBrainType` /
`playerScavBrainType` tables (keys: bigmap, factory4_day, …, lowercase) before `StandartBotBrain.Activate`; an
unknown map throws inside the prefix, the brain activates with the raw role, the postfix restores it. Whether
that freezes the bot is unproven, but it is a real defect on our maps. Fix (server): `ApplyBrainTable` copies the
stand-in row (Equator → interchange, others → factory4_day) into both tables for every Arena key; two unit tests.
Client probe: a 15-s status dump per living bot (state, position, BigBrain active layer, StandBy type, mover
flags) to tell "SAIN layer running, no path" from "vanilla PatrolAssault with zero patrol ways".

## Host test 9 (22:19 + 22:30, server 67196bab, client 5ff52336) — the reason they stand

Server restarted with `ApplyBrainTable`: `[ModulePatch] Updated assault bot … to use: assault brain` — SPT's
CustomAiPatch is happy. Bots still frozen (48.0 → 47.8 m, 50.1 → 51.0 m). The status dump then said it plainly:
`'Bot1' assault state=Active alive=True pos=(0.0, 0.1, 2.0) layer=PatrolAssault standBy=active peace=True
moving=False sprint=False pause=False`, identical every 15 s. So: not asleep, not ActiveFail, no SAIN layer —
BSG's own **PatrolAssault** layer is in charge (SAIN only takes over in combat and leaves peaceful roaming to the
vanilla layers), and it walks `BotZone.PatrolWays`, of which our zone has none (`patrolWays=0`;
`PatrolPointChooserBasic.ChooseStartWay` → `SetWay(null)` is a silent no-op). DrakiaXYZ-Waypoints 1.9 does not add
ways (it only patches `BotPathFinderCorePoints.FindPath` onto the NavMesh).

Fix: `PatrolWayRepairs.cs` — in the same `BotsController.Init` prefix, for every zone without ways: the zone's Bot
spawn markers sampled onto the NavMesh, kept when the first can reach them (complete NavMesh path),
nearest-neighbour chained, cut into `PatrolWay`s of up to 4 `PatrolPoint`s (`PatrolType.patrolling`,
unlimited persons), each point's sub points from BSG's own `PatrolPoint.CreatSubs` at the editor's three radii.
`BotZone.Init` then does its usual `Restore`/`InitPoints` on them. A proper patrol-route import (Arena's own
bot data, upper floors) stays a later stage; this gives stage 1 its roaming scavs.

## Host test 10 (22:38, client 98f49c21 — first patrol ways)

`zone 'BotZone_Main' had no patrol ways — built 1 way(s) with 1 point(s) and 11 sub point(s) from 7 Bot spawn
marker(s) (dropped: 6 unreachable from the first, 0 off the NavMesh)`: the builder took the first marker
`FindObjectsOfType` returned as the origin, and that one sits on a NavMesh island (the upper-floor ring point at
y 10.8), so every ground point counted as unreachable. Bot1 (at (-27.3, 0.0, 4.5), ground level) stayed put —
one unreachable point is no way. Changed to NavMesh-connected components (union-find over pairwise complete
paths), ways per component, isolated points as one-point ways.

## Host test 11 (22:46, client e1ac9298, server 67196bab) — SCAVS ROAM

`zone 'BotZone_Main' had no patrol ways — built 3 way(s) with 7 point(s) and 66 sub point(s) from 7 Bot spawn
marker(s) in 2 NavMesh-connected component(s) (1 isolated single point(s), 0 marker(s) off the NavMesh)` — the six
ground markers form one component (ways of 4 + 2 points), the upper-floor ring point its own one-point way. The
status dump, 15 s apart:

```
'Bot1' assault state=Active pos=(-11.9, 0.0, -7.9) layer=PatrolAssault standBy=active peace=True moving=False
'Bot1' assault state=Active pos=(  1.6, 0.0, -8.5) layer=PatrolAssault standBy=active peace=True moving=True
'Bot1' assault state=Active pos=( 10.1, 0.0, -6.5) layer=PatrolAssault standBy=active peace=True moving=False
'Bot1' assault state=Active pos=( 10.1, 0.0, -6.5) …
'Bot1' assault state=Active pos=(  4.8, 0.0, -7.5) …
```

The scav walks from the west point across the atrium to the east point, pauses, comes back — vanilla PatrolAssault
on our ways, SAIN standing by for combat. No warnings or errors all raid. Stage 1 ("scavs spawn and roam on
Equator") is reached on the GPU host.

## 2026-09-20 — ground floor only

The user confirmed that only the lower area of Equator is playable; every upper ring is decoration. The
`arena_equator-bot-ring2-west` spawn (y 10.8, the NavMesh island that became a one-point way in host test 11) is
removed from `tools/maps/equator.bots.json`: 6 Bot spawns, one NavMesh component, base.json regenerated. The
3-minute deaths of the test profile were self-inflicted: leaving a raid and skipping the health-treatment screen
instead of confirming it sets the profile's HP to 0. The user added a mod with a main-menu button (bottom left)
that starts Equator directly and a disconnect that skips every confirmation and the 0-HP penalty — use it for host
tests instead of the eight-click menu flow.

## Stage 1 — what it took (summary)

Everything a ported scene lacks that BSG's bot stack assumes, each found by instrumenting the next silent failure:

| Layer | Symptom | Cause | Fix (file) |
|---|---|---|---|
| server | 1320 × `Map 'arena_equator' not found` | APBS per-map weapon-range switch | `ApbsCompat.cs` prefix → stand-in map |
| server | `CustomAiPatch … assault cache still doesn't contain data` | SPT `bot.json` brain tables keyed by map | `ApplyBrainTable` (interchange / factory4_day rows) |
| client | body spawns, never a bot, nothing logged | `AICorePointHolder.CorePoints` null → throw in async `CreateBot` | `AICorePointRepairs.EnsureCoversHolders` + synthetic core point |
| client | `PreActivate` NRE in `KhorovodBotGameEvent` | `AIPlaceInfoHolder.Places` null | same generic null-collection fill (8 holders) |
| client | `state=ActiveFail`, frozen | `BotMover.Activate → GetVoxelSafe` on a 0×0×0 grid | `EnsureVoxelGrid` (empty 10×5×10 m cells) |
| client | `state=Active`, `layer=PatrolAssault`, frozen | zero `BotZone.PatrolWays` | `PatrolWayRepairs` (ways from Bot spawn markers, BSG sub points) |
| client | (diagnostics) | — | `BotSpawnProbe` (`[Bots] SpawnDiagnostics`, default on: wave/zone/point verdicts, activation-step finalizers, 15-s status dump) |

Open for later stages: authored patrol routes (Arena's own AI data, upper floors, more than one zone), exfils /
raid end, PMC bots, covers. The GPU profile (`ArenaGPU20260908`) has near-zero HP/energy and dies ~3 min into every
raid — bots keep running and `uibridge_player` still reports them, so tests are still usable, but healing the profile
would give longer observation windows.

D:\SPT41vanilla deliberately not deployed with bot builds: it has no SAIN, so vanilla-brained scavs would stand
still without patrol ways and `PatrolAssaultLayer.EndSimplePatrol` can NRE on `Way == null` — not a meaningful test
bed for this stage.
