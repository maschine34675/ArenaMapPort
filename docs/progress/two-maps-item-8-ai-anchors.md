# PLAN-TWO-MAPS item 8 — Arena's own AI patrol hubs / core points (2026-09-27)

Scope: [docs/PLAN-TWO-MAPS.md](../PLAN-TWO-MAPS.md) §3 item 8, "Arena's own AI patrol hubs / core points
(both maps) — no rebuild". This pass implements the extractor, the two sidecars, the client authoring path
and both test suites, and **corrects a wrong factual claim** in the plan's own "Verified facts" (see below).
Offline only: real extractor runs against the live Arena source, `python -m unittest discover -s
tools/tests`, `dotnet build`/`dotnet test` on the server project. **No Unity build, no client build (another
session was concurrently building `ArenaMapPort.Client.csproj`/`HarmonyPatches.cs`/`LoadingScreenArt.cs` —
this pass never touches those), and no host raid were run.**

## What

`AICorePointFallback`/`PatrolWayFallback` (bots-stage1) synthesize a single id-0/group-0 `AICorePoint` and
NavMesh-derived patrol ways from the Bot spawn markers, because Arena's own `PatrolWay`/`AICorePointHolder`
data lives only in the excluded `TeamFight_AI` scene (fresh bot support is authored for SPT, not ported
geometry — see extract_equator.py/extract_bay5.py's own module docstrings). This item reads that scene
directly and feeds the real topology to the client instead, keeping the existing synthesis as the fallback
for every map that has no sidecar (all twelve deferred maps) or where authoring the real net throws.

## Correction to docs/PLAN-TWO-MAPS.md's "Verified facts"

The plan previously said: *"Critical gap: `ConnectionsAtNet` (the graph edges `BotPathFinderCorePoints`
walks) is unrecoverable custom data — every authored core point ships with an empty edge list... the
behavioral payoff over today's synthesis is unproven without a host test."*

**That "unrecoverable" claim was wrong.** `AICorePoint._connectionsAtNetId`/`ConnectionsAtNet` decode
exactly like every other custom field this project already raw-decodes (`_connectionsAtNetId: List<int>` +
`ConnectionsAtNet: List<PPtr<AICorePoint>>`, both fully self-validating — implemented and exercised in
`extract_ai_anchors.py`'s `decode_aicorepoint`). What's actually true, checked against the decompile this
pass:

- `AICorePoint.Restore(List<AICorePoint>)` is the **only** method that ever populates `ConnectionsAtNet` at
  runtime (`AICorePoint.cs:89-105`).
- Its **only** caller anywhere in the 0.16.9.5 decompile is `AICorePointHolder.DrawInner()`
  (`AICorePointHolder.cs:151-161`), called only from `OnDrawGizmos`/`OnDrawGizmosSelected`
  (`AICorePointHolder.cs:135-149`) — Unity Editor Scene-view callbacks that **never fire in a built EFT
  client or a live raid** (Gizmos are stripped from player builds).
- So `ConnectionsAtNet` stays empty at runtime **regardless of what we author there** — not because the
  bytes are unreadable, but because nothing ever reads what would be written. Every authored core point's
  connections are therefore deliberately left empty (`AICorePoint.ClearConnections()`), which is the exact
  same runtime shape as today's single synthetic point — by design, not by data-recovery limitation.
- `BotPathFinderCorePoints.CG_FindPathOfCores` (`BotPathFinderCorePoints.cs:625-631`) and
  `FullMapPatrolLayer`/`LootPatrolLayer`/`ObdolbosEventPatrolLayer`/`PeaceZryachiyPatrol` (their own
  `_lastCore.ConnectionsAtNet.Count > 0` checks) DO walk `ConnectionsAtNet` at runtime, but see it empty on
  every map, ported or vanilla, given the point above — so this item's real payoff was never "long-range
  core-point-graph pathfinding" (that code path stays inert with or without this change); it is the
  hub/subpoint **patrol** hierarchy `PatrolPointChooserBasic`/`PatrolAssault` walk every raid (see
  `docs/progress/bots-stage1.md`'s own "Host test 9" — this is the exact gap that made bots stand still in
  peace), plus giving `SpawnPointParams.CorePointId` real per-spawn core-point/connection-group identity
  instead of one universal id-0/group-0 alias.

docs/PLAN-TWO-MAPS.md's item 8 section itself has been updated in place with this correction (the doc's own
convention for its still-open items list, distinct from this append-only progress log).

## Verified facts (this pass, against the live Arena source)

- **Counts match the plan exactly**: Equator 14 `AICorePoint` (ids 1–14, all `connection_group_id=12`
  except the two isolated ones below), 2 hubs (`PatrolWay.Points`) + 21 subpoints = 23 `PatrolPoint`
  objects total; Bay5 11 `AICorePoint` (ids 1–11, all `connection_group_id=11`), 4 hubs + 43 subpoints = 47
  `PatrolPoint` objects total. Both counts are enforced by `extract_ai_anchors.py`'s own orphan check (every
  `PatrolPoint` found by the class scan must be reachable via `PatrolWay.Points`/a hub's `subPoints`, with
  none left over) and printed on every extractor run.
- **Equator's two isolated core points**: id 13 (`connection_group_id=13`, y≈3.175) and id 14
  (`connection_group_id=14`, y≈3.449) both carry `name` `"AICore ID:{id} CG:{cg} near:BotZone"` (via
  `AICorePoint.AddToName`, editor-authored) and an empty `_connectionsAtNetId` — genuinely disconnected from
  the main `connection_group_id=12` net even before this item's own decision to drop edges entirely. Their
  elevated y is a known off-current-NavMesh risk flagged for the host-test pass below (not caused by this
  work — see docs/PLAN-TWO-MAPS.md's own note).
- **Bay5's hub `core_point_id` is not one-to-one with hubs**: two of Bay5's four hubs both carry
  `core_point_id: 10` (the same real core point, at `(87.5, -3.87, 7.75)`) — this is Arena's own authored
  data (confirmed via the PPtr/hierarchy self-checks below, not a decode artifact), and the schema/client
  code make no uniqueness assumption about it.
- **`AICorePointHolder.CorePoints`' own PPtr list matches the independent class-scan set exactly** on both
  maps (self-check in `extract_map()`) — the holder does not reference any `AICorePoint` object the scan
  missed, and vice versa.
- **Every hub's `subPoints` PPtr list agrees with the actual scene hierarchy**: for every subpoint PPtr, the
  extractor resolves the target object's own Transform and asserts its `m_Father` is literally the hub's own
  Transform (not just "some object with this path_id exists") — this passed for all 21 (Equator) + 43
  (Bay5) subpoints.
- **Bay5's AI scene has the same duplicate-BuildSettings-index trap as its geometry scene set**:
  `Arena_Bay5_TeamFight_AI` appears at BOTH idx 78 (full) and idx 222 (stripped duplicate) in
  `BuildSettings.scenes`, same shape extract_bay5.py's own `resolve_scene_indices()` already documents for
  `TeamFight_AI`/`Sound` on the geometry set. Resolved by exact full-path match + largest
  `sharedassets<N>.assets`, with a hard assertion against the pre-verified expected index (78) — confirmed
  live this pass (`2 BuildSettings entries [78, 222] ... chose idx 78`). Equator's AI scene (idx 52) has no
  such duplicate.
- **`make_base_json.py` gives every `SpawnPointParams.CorePointId` the value 0`**, while Arena's real ids
  start at 1 — confirmed by reading `tools/server/make_base_json.py`'s own `SpawnPointParams` construction
  and cross-checked against both new sidecars' `core_points[].id` ranges (1–14 / 1–11, never 0). The client
  therefore always keeps the synthetic id-0/group-0 alias **in addition to** the real net (see "How" below)
  — without it `GetCorePoint(0)` would return null and bot creation would regress exactly as it did before
  `AICorePointFallback` existed.

## How

- **`tools/extract/extract_ai_anchors.py`** (new) — raw-decodes `AICorePoint`/`PatrolPoint`/`PatrolWay`/
  `AICorePointHolder` from the AI scene (level52 Equator / level78 Bay5), following the established
  technique (`parse_monobehaviour_head()` + `struct.unpack_from` on `get_raw_data()`, fields 4-byte aligned,
  same recipe `extract_physics_props.py`'s `decode_sound_type` and the research scratchpad's
  `decode_ai.py` already prove against this exact data). Every decode is self-validating
  (`end_off == len(raw)` exactly, converting a too-short buffer into `HardExtractError` rather than an
  uncaught `struct.error`) plus the two structural cross-checks above (PPtr vs class-census, PPtr vs
  hierarchy). World positions reuse `analysis/scripts/census.py`'s own `world_transform_of_transform()` — no
  reimplementation. `ConnectionsAtNet` is parsed (required to keep the byte offset, and therefore every
  other field's self-validation, correct) but never exported — see the correction above. `base=` is always
  passed explicitly to `common.scene_files()` (its own default binds a placeholder `X:` drive at import
  time). Bay5's duplicate AI-scene index is resolved by `resolve_bay5_ai_scene_index()`, the same technique
  as `extract_bay5.py`'s own `resolve_scene_indices()`.
- **`tools/maps/equator.ai.json` / `tools/maps/bay5.ai.json`** (new, tracked sidecars — small extracted
  facts, not the bulky `work/`/`dist/` pipeline output): `{"_comment", "core_points":
  [{"id","connection_group_id","position":[x,y,z],"name"}], "patrol_ways":[{"hubs":[{"core_point_id",
  "position","sub_points":[[x,y,z],...]}]}]}`. No edges. Regenerate with
  `python tools/extract/extract_ai_anchors.py --map equator --map bay5` (writes both at their default
  paths); re-running reproduces byte-identical output (checked this pass).
- **`ArenaMapPort.Client/AiAnchors.cs`** (new) — loads `<plugin dir>/ai/<key>.ai.json` (loose file, parsed
  as a plain Newtonsoft `JObject`, exactly `ShaderRebind.cs`'s own sidecar convention — no embedded resource,
  no strongly-typed DTO). Missing file → log once at Info, keep returning null (most maps, by design);
  present-but-unparseable file → log once at Warning, also null. Cached per map's short key (the registry
  `Key` with its `"arena_"` prefix stripped, matching every other `tools/maps/` sidecar's own file stem).
- **`ArenaMapPort.Client/AICorePointRepairs.cs`** — `EnsureCorePoint` now takes the loaded `AiAnchors` and,
  when it has real core points, authors one real `AICorePoint` per sidecar entry (`ClearConnections()` +
  `SetIds(id, connection_group_id)` at the extracted world position) **plus** the same synthetic
  id-0/group-0 alias as before (factored into a shared `AddAlias` helper so both paths use exactly one place
  that creates it). Any exception while authoring the real net destroys whatever was partially added and
  falls through to the untouched single-point synthesis. `EnsureCorePoint`/`EnsurePatrolWays` are both
  `out`-parameter-friendly now so `InitPrefix` can log one combined summary line only when anchors were
  actually used (a no-op re-entry stays silent, matching the pre-existing logging behavior; the decompile
  has a single `BotsController.Init` call site per raid, `EFT/LocalGame.cs:184`, so the guard is defensive).
- **`ArenaMapPort.Client/PatrolWayRepairs.cs`** — `EnsurePatrolWays` takes the (possibly null) `AiAnchors`
  and, per zone with no already-authored ways, tries `BuildFromAnchors` first: one `PatrolWay` per sidecar
  entry, its `Points` the real hub `PatrolPoint`s (`SetCorePoint(realCorePoint)` — a hub's own
  `_corePointId` defaults to 0, which would otherwise silently resolve to the id-0 alias instead of Arena's
  intended core point), each hub's `subPoints` built directly at the sidecar's literal positions with plain
  C# defaults (`CanUseByBoss` true, `ShallSit` false, `PatrolPointType.checkPoint`, no `ActionData`/
  `PointWithLookSides` — exactly what `AddComponent<PatrolPoint>()` already gives; set explicitly for
  clarity), bypassing `CreatSubs`/`RefreshData` entirely. Any exception (e.g. a hub referencing a core point
  id the holder doesn't have) falls through to the existing NavMesh-derived synthesis for that zone,
  unchanged.

## EFT decompile members used (verified against `D:/SPT41/Development/_decompiled/`, this pass, twice)

| Member | File:line |
|---|---|
| `AICorePoint._id`, `_connectionGroupId`, `ClearConnections()`, `SetIds(int,int)`, `Id` | AICorePoint.cs:20-24, 49-60, 35 |
| `AICorePoint.ConnectionsAtNet`, `Restore(List<AICorePoint>)` | AICorePoint.cs:29-30, 89-105 |
| `AICorePointHolder.CorePoints`, `AddCorePoint(AICorePoint)`, `GetCorePoint(int)` | AICorePointHolder.cs:61, 129-133, 112-117 |
| `AICorePointHolder.DrawInner()`, `OnDrawGizmos()`, `OnDrawGizmosSelected()` | AICorePointHolder.cs:135-161 |
| `PatrolPoint._corePointId`, `Id`, `CanUseByBoss`, `ShallSit`, `PatrolPointType`, `ActionData`, `PointWithLookSides`, `SubManual`, `subPoints` | PatrolPoint.cs:10-30 |
| `PatrolPoint.SetCorePoint(AICorePoint)`, `Restore(AICorePointHolder)`, `Awake()`→`RefreshData()` | PatrolPoint.cs:320-329, 60-63, 330-393 |
| `PatrolWay.PatrolType`, `Points`, `_maxPersons`, `CoefSubPoints`, `InitPoints()` | PatrolWay.cs:10-19, 37-43 |
| `PatrolPointType.checkPoint` (= 0) | PatrolPointType.cs |
| `BotZone.Init(...)` — `point.Restore(holder)` / `patrolWays[i].InitPoints()` loops | BotZone.cs:113-168 |
| `BotPathFinderCorePoints.CG_FindPathOfCores` walking `ConnectionsAtNet` | BotPathFinderCorePoints.cs:625-631 |
| `FullMapPatrolLayer`/`LootPatrolLayer`/`ObdolbosEventPatrolLayer`/`PeaceZryachiyPatrol` `_lastCore.ConnectionsAtNet` checks | each file, own `.ConnectionsAtNet.Count > 0` guard |

None of these are obfuscated `method_N`/`vmethod_N` members (all public, human-named fields/methods on
plain-name classes) — no `HarmonyPatches.Verify()` entries needed for this item.

## Client csproj addition the main session must make

`tools/maps/equator.ai.json`/`bay5.ai.json` are tracked source, not `work/build/` output, so they stage the
same way `tools/maps/AdditionalMapBuilds.props`' own `ArenaMapPortAdditionalSidecar` items do (a direct
`tools/maps/` source, a `DistFilename` metadata item), not the `work/build/<map>/material-shaders.json`
pattern. Add to `ArenaMapPort.Client.csproj`'s `ArenaMapPortStageWpOutputs` target (or a new target run
alongside it) — this pass does not add it, since another session was concurrently editing this exact file:

```xml
<ItemGroup>
  <ArenaMapPortAiAnchors Include="$(MSBuildThisFileDirectory)..\tools\maps\equator.ai.json">
    <DistFilename>ai\equator.ai.json</DistFilename>
  </ArenaMapPortAiAnchors>
  <ArenaMapPortAiAnchors Include="$(MSBuildThisFileDirectory)..\tools\maps\bay5.ai.json">
    <DistFilename>ai\bay5.ai.json</DistFilename>
  </ArenaMapPortAiAnchors>
</ItemGroup>
<Error Text="[ArenaMapPort] expected AI anchors sidecar missing: %(ArenaMapPortAiAnchors.Identity)"
       Condition="!Exists('%(ArenaMapPortAiAnchors.Identity)')" />
<MakeDir Directories="$(ArenaMapPortDistDir)\ai" />
<Copy SourceFiles="@(ArenaMapPortAiAnchors)"
      DestinationFiles="@(ArenaMapPortAiAnchors->'$(ArenaMapPortDistDir)\%(DistFilename)')" SkipUnchangedFiles="true" />
<Message Importance="high" Text="[ArenaMapPort] staged equator.ai.json/bay5.ai.json into dist/.../ArenaMapPort/ai/" />
```

(`ArenaMapPortDistDir` is the project's own existing property; this mirrors the shape of the existing
`ArenaMapPortBay5ShaderSidecarSrc` copy two items above it in that same target.)

## Offline evidence (this pass)

```
$ python tools/extract/extract_ai_anchors.py --map equator --map bay5
[extract_ai_anchors] equator: loading level52 (Arena_Equator_TeamFight_AI) from D:/Battlestate Games/Escape from Tarkov Arena/EscapeFromTarkovArena_Data
[extract_ai_anchors] equator: 14 core point(s), 2 hub(s), 21 sub point(s) -- 23 PatrolPoint object(s) total, all consumed
[extract_ai_anchors] equator: wrote D:\ArenaMapPort\tools\maps\equator.ai.json
[extract_ai_anchors] Arena_Bay5_TeamFight_AI: 2 BuildSettings entries [78, 222] (duplicate path, same as extract_bay5.py's TeamFight_AI/Sound pattern) -- chose idx 78 (largest sharedassets<N>.assets: the full, non-stripped copy)
[extract_ai_anchors] bay5: loading level78 (Arena_Bay5_TeamFight_AI) from D:/Battlestate Games/Escape from Tarkov Arena/EscapeFromTarkovArena_Data
[extract_ai_anchors] bay5: 11 core point(s), 4 hub(s), 43 sub point(s) -- 47 PatrolPoint object(s) total, all consumed
[extract_ai_anchors] bay5: wrote D:\ArenaMapPort\tools\maps\bay5.ai.json

$ python -m unittest discover -s tools/tests -p "test_*.py"
Ran 406 tests in 13.373s
OK (skipped=28)

$ dotnet build ArenaMapPort.Server/ArenaMapPort.Server.csproj -c Release
Der Buildvorgang wurde erfolgreich ausgeführt. 0 Warnung(en), 0 Fehler.

$ dotnet test ArenaMapPort.Server.Tests/ArenaMapPort.Server.Tests.csproj -c Release
Bestanden! : Fehler: 0, erfolgreich: 390, übersprungen: 0, gesamt: 390
```

Re-running the extractor a second time reproduces byte-identical `tools/maps/*.ai.json` output (`git
status` shows no diff against the first run).

## What the lead must do

1. **Client csproj addition** — the exact hunk above, into `ArenaMapPort.Client.csproj` (not made by this
   pass; another session owned that file concurrently).
2. **Client Release build** (without `DllOnly`) to stage `ai/equator.ai.json`/`ai/bay5.ai.json` into
   `dist/BepInEx/plugins/ArenaMapPort/ai/`, then `deploy-to-spt.ps1 -Confirm`, then restart EFT (new client
   DLL + new loose files).
3. **Host-test recipe**:
   - Start an EQUATOR DEBUG raid, bot amount at the normal setting (not None — this needs live bots).
     `uibridge_log` for the new combined summary line: `[ArenaMapPort][RaidFix] arena_equator: authored 14
     real AI core point(s), 2 patrol hub(s), 21 sub point(s) from ai/equator.ai.json, plus the synthetic
     id-0/group-0 alias (SpawnPointParams.CorePointId is always 0)`. Repeat for BAY5 DEBUG, expect 11/4/43.
   - `uibridge_teleport dryRun=true` to every core point and hub position from the two sidecars (28 checks
     total: 14+2 Equator, 11+4 Bay5) — expect `reachable=true` for all except Equator's two isolated
     near-BotZone core points (ids 13/14, y≈3.18/3.45), which are a known/expected island (flagged above,
     not a regression this item introduces).
   - `uibridge_bots`: no bot reports `BotState=ActiveFail`; `BotSpawnProbe`'s own `[Bots]
     ActivationDiagnostics` log stays clean (same check `bots-stage1.md` already established).
   - Compare bot patrol behavior in peace near the hubs before/after (this item's actual payoff, given the
     `ConnectionsAtNet` correction above: the hub/subpoint walk, not core-point-graph pathfinding).
   - A second raid on the same map within the same EFT session (no restart) loads a fresh scene, so the
     combined summary line appears again, once, with no error (review 2026-09-27: the earlier "absent on the
     second call" expectation assumed a second `BotsController.Init` per raid, which the decompile does not show).
4. **Done criterion**: both maps' raid-start log shows the real counts (14/2/21 Equator, 11/4/43 Bay5) plus
   the alias, every anchor point reachable except the two known isolated ones, no `ActiveFail`, and no
   regression in the twelve deferred maps' own unchanged single-point/NavMesh-derived synthesis (no sidecar
   for them — nothing to verify beyond "still absent").

## Review follow-ups (2026-09-27, main session)

The implement-then-review workflow's reviewer found no blockers and three minor points, all fixed before the host
test: `PatrolWayRepairs.BuildFromAnchors` now destroys the `PatrolWay(Arena)` GameObjects it created before
rethrowing (mirrors `AICorePointFallback.EnsureCorePoint`); `AiAnchorsTests.CorePointIdsAreUniqueWithFinitePositionsInTheBakeVolume`
asserts every core point id is >= 1 (id 0 is the synthetic alias); the "BotsController.Init runs more than once per
raid" claim was replaced by the decompile fact (one call site, `EFT/LocalGame.cs:184`; the guard is defensive). The
csproj target `ArenaMapPortStageAiSidecars` copies `tools/maps/{equator,bay5}.ai.json` into
`dist/BepInEx/plugins/ArenaMapPort/ai/` on every Release build, DllOnly included.

## Host test 2026-09-27 (SPT-GPU-2) — VERIFIED

Instance 660b5523 (17:39), Equator NO BOTS: `[RaidFix] zone 'BotZone_Main' authored 1 real patrol way(s) from
ai/equator.ai.json (2 hub(s), 21 sub point(s))` and `[RaidFix] arena_equator: authored 14 real AI core point(s), 2
patrol hub(s), 21 sub point(s) from ai/equator.ai.json, plus the synthetic id-0/group-0 alias` (17:40:40.680),
`[BotSpawn] BotZone.Init 'BotZone_Main': markers=10 onNavMesh=10 points=10 maxPersons=-1 patrolWays=1`. Bay5 NO BOTS
17:45:31.313: `authored 1 real patrol way(s) from ai/bay5.ai.json (4 hub(s), 43 sub point(s))` and `authored 11 real AI
core point(s), 4 patrol hub(s), 43 sub point(s) ... plus the synthetic id-0/group-0 alias`.

`uibridge_teleport dryRun=true` on every anchor: Equator 14 of 16 reachable (all hubs, core points 1-12); core points
13 (-5.31, 3.18, -1.04) and 14 (7.03, 3.45, 4.56) answer `422 unsafe_position: not reachable on foot` — the two
isolated "near:BotZone" points predicted above, not a regression (no hub references them). Bay5 15 of 15 reachable
(core points 1-11, four hubs, all snapped 0.0 m).

Instance 0680b26b (17:49, review fixes in), Equator WITH BOTS: same two summary lines at 17:50:04.353. `uibridge_bots`:
Scav `Корнелий Упырь` state Active, layer PatrolAssault, first at (9.6, 0, 12.7) inside hub "core 4"'s subpoint
cluster (1.5-1.9 m from subpoints 0-3), 50 s later at (-5.4, 0, 15.2) on the way to the other hub (-14.15, 10.44),
then back at (9.6, 0, 12.7); a second Scav moving under PatrolAssault at (-14.8, 0, 0.1). Bay5 WITH BOTS 17:55:59.469
(same summary line): four PMCs, all Active and moving (PatrolAssault / PatrolFollower), two at the (99.58, 5.36) hub.
No `ActiveFail`, no `[Bots] ActivationDiagnostics` line, no ArenaMapPort warning; the only error is Bot Placement
System's known `TryToSpawnInZonePatch EXCEPTION ... GetNewValidBotZone` (bots-stage1.md: once per wave, third-party).
Each raid loads a fresh scene, so the summary line appears once per raid start (instance 0680b26b logged it for
Equator at 17:50:04 and again for Bay5 at 17:55:59, no error on the second raid).
