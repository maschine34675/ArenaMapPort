# PLAN-TWO-MAPS item 6 — rain-cover geometry (DryPlane / IndoorTrigger / WeatherObstacle)

Scope: [docs/PLAN-TWO-MAPS.md §6](../PLAN-TWO-MAPS.md) ("Rain-cover geometry — DryPlane + IndoorTrigger").
Branch `feature/native-dust`, worktree of this task. **Offline only — no Unity build, no dotnet build of
the client/server beyond compile+test, no host test, no deploy.** The lead runs the Unity build and the
host verification; see "What the lead must do" at the end.

## What

Author Arena's own `EFT.EnvironmentEffect.EnvironmentManager`/`TriggerGroup`/`IndoorTrigger`
(both maps) and `WeatherObstacle`/`DryPlane` (Bay5 only) geometry from the Scripts scene, replacing
`RainCoverRepairs.cs`'s depth-photo heuristic and `EnvironmentRepairs.cs`'s bare-manager fallback with
ground-truth data for Equator and Bay5.

## Verified facts (this pass, against the live Arena install)

Re-ran the 2026-09-25 spike's reader
(`…/scratchpad/rain-cover-spike/raincover_extract.py`) as `tools/extract/extract_raincover.py`
(attribution kept in its module doc comment) against the real source
(`D:/Battlestate Games/Escape from Tarkov Arena/EscapeFromTarkovArena_Data`), scratch output only:

| | Bay5 (level85) | Equator (level54) |
|---|---|---|
| DryPlane | 669 | 0 |
| IndoorTrigger | 30 | 1 |
| TriggerGroup | 2 | 1 |
| WeatherObstacle | 1 | 0 |
| DryPlane↔MeshFilter.m_Mesh full-corpus cross-check | 669/669 match | n/a |
| baked-Bounds vs live-Transform-recomputed mismatches | 1 (`IndoorTrigger 117 (12)`, path_id 8095) | 0 |

Matches the spike exactly. Ground truth for the mismatch (decompile,
`EFT.EnvironmentEffect/IndoorTrigger.cs`): `Awake()` only calls `Reinit()` (which would recompute
`Bounds` from the live `Transform`) when the serialized `Bounds.size.magnitude < Mathf.Epsilon`. Every
real `IndoorTrigger` ships with a non-zero baked `Bounds`, so `Reinit()` never runs and `Check(pos)`
(`Bounds.Contains(pos)`) always uses the baked field — path_id 8095's `Bounds` is byte-identical to a
different sibling's (path_id 6796, same center/extent), almost certainly an editor-time duplicate whose
cache was never refreshed, while its own Transform sits ~65 m away. **The extractor always emits the
baked `Bounds`, never a Transform recompute** — that is what the shipped game itself uses.

Three sample `IndoorTrigger` entries from the real run (`bounds_center`/`bounds_size`, world units):

```
Bay5   "IndoorTrigger 117"            center=[137.73, 0.30, 71.15]  size=[16.64, 13.43, 61.43]  area_auto_id=116
Bay5   "container (1)_IndoorTrigger"  center=[104.86, 0.10, 40.00]  size=[3.41, 2.50, 6.39]      area_auto_id=0
Equator "IndoorTrigger 11"            center=[1.85, 13.20, 9.63]    size=[99.71, 70.27, 112.60]  area_auto_id=10 (the whole mall)
```

Sample `DryPlane` (Bay5, world-space): `"Quad (662)"` position `[-226.59, 11.30, 423.29]`, scale
`[6.75, 3.10, 1.00]` — a rectangular roof panel, not a degenerate/zero-size quad, confirming the full
TRS parent-chain composition (position **and** rotation **and** scale, unlike
`census.world_transform_of_transform`'s own simplified position/rotation-only walk) is correct. One
independent cross-check: this module's `IndoorTrigger` `world_position` values are byte-identical to
the original spike's own (simpler) `census`-based `world_pos` values for every sampled instance —
confirms no parent-rotation skew anywhere in this hierarchy and gives confidence in the new full-matrix
composition this module adds for `DryPlane`'s scale.

`WeatherObstacle.Awake()` (decompile) combines every child `DryPlane`'s mesh via
`transform.localToWorldMatrix` into one `MeshCollider`, then **destroys the `DryPlane` children** —
existing, unmodified engine behavior; this importer only needs to author the children with the right
`Mesh`/transform before that `Awake()` runs at real scene load. Equator authors **zero**
`DryPlane`/`WeatherObstacle` in Arena's own data — the extractor and importer both hard-fail if a future
Arena patch ever changes that, rather than silently inventing one.

## How

**Extractor** (`tools/extract/extract_raincover.py`, new): reads the Scripts scene (`54`/`85`, via
`analysis/scripts/common.scene_files`/`census.py`, same machinery `census.py` itself uses) with
hand-built typetree readers for `DryPlane`/`IndoorTrigger`/`TriggerGroup`/`WeatherObstacle`, adapted with
attribution from the spike. Writes `<package>/raincover/raincover.json`:
`{"schema_version":1, "indoor_triggers":[{name, bounds_center[3], bounds_size[3], is_bunker, fade_time,
bunker_depth, bunker_low_pass, exposure_speed, exposure_offset, rain_volume, area_auto_id,
world_position[3], world_rotation[4]}], "dry_planes":[{name, world_position[3], world_rotation[4],
world_scale[3]}], "weather_obstacle": bool}`. Fails closed (raises) on: unsupported `map_key`, a
`DryPlane` whose `Mesh` PPtr disagrees with its own `MeshFilter.m_Mesh`, Equator carrying any
`DryPlane`/`WeatherObstacle`, Bay5 carrying `DryPlane` with no `WeatherObstacle` root (or vice versa),
or zero `IndoorTrigger` on either map.

Hooked into the existing `--native-features` flow with a 3-line addition right after each engine's
existing `manifest["native_features"] = {...}` line: `tools/extract/extract_equator.py` and
`tools/extract/extract_bay5.py` each call `export_raincover(args.source, args.output, "<map_key>")` and
fold the summary into `manifest["native_features"]["raincover"]`
(`{schema_version, source_scene, indoor_trigger_count, dry_plane_count, weather_obstacle,
bounds_transform_mismatches}`). All logic lives in the new module; the two hook sites are additive only.

`tools/validate/validate_native_features.py`'s `feature_enabled()` did a **strict** key-equality check
on `manifest["native_features"]` (`compare()`'s dict branch) — adding the new `raincover` key would have
broken it for every future Equator/Bay5 package. Fixed by popping the optional `raincover` key before
that compare and validating its own shape separately (schema_version==1 + `indoor_trigger_count`
present); packages without it (all twelve deferred maps) are unaffected. `validate_extract.py`'s
`KNOWN_COMPONENT_TYPES`/`ENABLED_COMPONENT_TYPES` whitelists were **not** touched — those only cover
content-scene per-object components (`scenes/*.json`), and raincover data never appears there; it lives
entirely in the separate `raincover/raincover.json` file, exactly like `native_audio`/`native_ballistics`
already do.

**Importer** (`UnityProject/Assets/ArenaMapPort/Editor/Import/RainCoverImporter.cs`, new; one call added
in `AuthorScriptsScene.cs` right before the existing `LocationScene` block, matching the existing
`Bay5AudioImporter.Build(pkg, root)` call shape): reads `package.Manifest["native_features"]?["raincover"]`
(returns/no-ops for the twelve deferred maps) then `raincover/raincover.json`, and authors
`EnvironmentManager > TriggerGroup > N IndoorTrigger` (class defaults on `EnvironmentManager`; each
`IndoorTrigger`'s `Bounds` written directly from `bounds_center`/`bounds_size`), plus, on Bay5,
`WeatherObstacle > N DryPlane` on one shared `Resources.GetBuiltinResource<Mesh>("Quad.fbx")` (Unity's
built-in unit quad — the same asset the extractor already proved every real `DryPlane` references).
`AuthorScriptsScene.AddComponentOrThrow<T>` was widened from `private` to `internal` so this sibling
importer can reuse it (no behavior change).

Key design point, worth restating: **the authored `TriggerGroup`'s own `Bounds` is deliberately never
assigned** (stays the C# default, zero). `TriggerGroup.Awake()` (decompile) only calls `Reinit()` — which
rebuilds `_childrenTriggers`/`_childrenGroups` via `GetComponentsInChildren<...>()` **and** recomputes its
own encompassing `Bounds` — when its serialized `Bounds` is near-zero. Parenting every `IndoorTrigger`
directly under the authored `TriggerGroup` and leaving its `Bounds` untouched means `Reinit()` does all
child-list wiring itself the first time the real scene loads, from the `Bounds` this importer already
wrote onto each `IndoorTrigger`. No manual `List<IndoorTrigger>` wiring code was needed at all — simpler
and less error-prone than hand-authoring the two-level nested-`TriggerGroup` structure Arena's own source
happens to use (an `EnvironmentHelperTrigger` wrapper with an encapsulating outer `TriggerGroup` plus an
inner one holding the real children) — functionally identical, since
`EnvironmentManagerBase.InitIndoors()` finds a `TriggerGroup` anywhere in its children via
`GetComponentInChildren`, at any depth.

`WeatherObstacle`'s own runtime-created `MeshCollider` (built at real scene load, from combining the
`DryPlane` children) lives in the **Scripts** scene, never a content scene, and only starts existing
partway through raid start — it plays no part in this project's own NavMesh bake (baked once at Unity
import time from content-scene colliders only) and needs no `BakeNavMesh.cs` change.

**Client**: `EnvironmentRepairs.cs`'s existing `EnvironmentManagerBase.Instance == null` guard already
skips creating the bare fallback once the authored `EnvironmentManager`'s own `Awake()` has run (which
happens during `TarkovApplication.LoadMapAndData`, strictly before `Player.Init` ever fires — the same
ordering argument that file's own doc comment already establishes) — **no logic change needed there**,
only doc-comment/log-message updates recording why. `RainCoverRepairs.cs` gets the new
`ApplyForCurrentMap(..., bool authoredRainCover = false)` parameter: when true, it forces this map's
`CoverState.Mode = Off` (reusing the already-tested Off path — `SetupDepthCameraPostfix`'s §13
camera-diagnostics caching, itself a no-op for every mode including Off, still runs unchanged) instead of
running the `RainBounds` scan / cullingMask widening / elevated-mesh black-draws, so nothing here
double-draws on top of vanilla's own now-real `WeatherObstacle` draw inside `DepthPhotograper.RenderObstacles()`
(decompile: it already draws `WeatherObstacle.Instance.MeshCollider.sharedMesh` once `Instance` is
non-null). `WeatherRig.cs` binds the new key `[Lighting (<map>)] AuthoredRainCover` (bool, defaults **true**
only for `arena_equator`/`arena_bay5` — reusing the existing `rigDefaults` split this file already
computes for other per-map defaults — **false** for every other, deferred map, so their existing heuristic
is completely unaffected) and passes it into the `ApplyForCurrentMap` call.

Deliberately **not** changed: `RainWetnessRepairs.cs` (its `Instance==null`-style guards, e.g.
`WeatherRig.PreflightClear()`'s `EnvironmentManagerBase.Instance == null`, already become correct no-ops
automatically — once our authored `EnvironmentManagerBase`-derived component's own `OnDestroy()` runs at
scene unload, `Instance` returns to null exactly like a vanilla raid's own `EnvironmentManager`, the same
lifecycle every other map already depends on); `BakeNavMesh.cs` (see above);
`SceneAssembler.cs` (raincover data is Scripts-scene-only, never a content-scene per-object component, so
its Pass-1c switch is untouched).

## Offline evidence

`python -m unittest discover -s tools/tests -p "test_*.py"`: **262 passed, 27 skipped** (was 249/27
before this task — 13 new tests in `tools/tests/test_raincover.py`: TRS-matrix compose/decompose round
trips against synthetic fixtures — including a rotated-parent case exercising the parent-rotation-aware
composition `census.world_transform_of_transform` deliberately simplifies away — the exact typetree
node field order/shapes copied from the spike, `export()`'s `map_key` guard, and three regression tests
proving `validate_native_features.feature_enabled()` still accepts a manifest with/without the new
`raincover` key and rejects a malformed one). `python tools/validate/validate_map_descriptors.py`: OK (14
descriptors, unaffected — this task adds no descriptor/sidecar data). `dotnet build`
(Client, `-p:ArenaMapPortDllOnly=true`) and (Server): both clean, 0 warnings/errors beyond pre-existing
ones. `dotnet test ArenaMapPort.Server.Tests`: **356 passed**, 0 failed (unaffected by this task — no
descriptor changes).

Real extractor run (scratch output, `python` calling `extract_raincover.export()` directly — **not** the
accepted `work/extract/*-native-dust` packages, which this task must not modify): exact counts above,
plus a spot-checked `raincover.json` (`dry_planes[0]` = `Quad (662)` above; `indoor_triggers[0..2]` =
the three samples above) confirming the schema matches the task's spec field-for-field.

Unity Editor C# (`RainCoverImporter.cs`, the `AuthorScriptsScene.cs`/`Bay5AudioImporter.cs`-style
edits) **could not be compiled in this worktree** (Unity is not run here, per this task's own hard
rule) — written by mirroring `Bay5AudioImporter.Build`'s exact shape (manifest-entry-null-check ->
`ResolvePath`/`File.ReadAllText`/`JObject.Parse` -> author), reusing only APIs already used elsewhere in
this Editor project (`AddComponentOrThrow<T>`, `JObject`/`JArray` indexing incl. the `?[...]` null-conditional
chaining already used in `VerifySpawnPlacement.cs`/`Bay5AudioImporter.cs`, `Resources.GetBuiltinResource<Mesh>`),
and double-checked against the `EFT.EnvironmentEffect` decompile for every field name and class shape used.

## What the lead must do

1. **Rebuild** — this task's extractor hooks only ran in a scratch dir; the accepted
   `work/extract/equator-native-dust`/`work/extract/bay5-native-dust` packages do not yet carry
   `raincover/raincover.json` (this task must not write into them). Re-run the same `--native-features`
   extraction that produced today's accepted packages (see AGENTS.md's Build section) — the new hook
   runs automatically inside that flow and needs no new command-line flag. Then, per
   PLAN-TWO-MAPS §6's own "Shared build step for items 4–6": one Equator `build_all.ps1` run (no
   `-FullMap`, per AGENTS.md) and one Bay5 run (`-FullMap -OutputDir work/build/bay5`), one client
   Release build (without `DllOnly`) to stage both bundles, one `deploy-to-spt.ps1 -Confirm`.
2. **Host test** (`WeatherTestOverride` to force rain, per AGENTS.md's host-testing recipe):
   - `uibridge_teleport` to 2–3 of the `IndoorTrigger` centers above (Bay5: e.g. `[137.73, 0.30,
     71.15]`/`[104.86, 0.10, 40.00]`; Equator: `[1.85, 13.20, 9.63]`, the mall) and confirm an
     indoor/outdoor exposure and ambience-audio transition right at the boundary
     (`EnvironmentManager.SetTriggerForPlayer`/`Environment` — no depth-photo/RainCoverRepairs
     involvement at all for this check, it is pure `Bounds.Contains`).
   - Bay5 only: confirm on-screen rain drops stop under a `DryPlane`-covered roof
     (`DepthPhotograper.CheckIfCameraUnderRain`) and that the recurring
     `Debug.LogError("WeatherObstacle.Instance is null")` no longer appears in the log.
   - Confirm ground wetness dries under a roofed Bay5 area, and that Equator's mall gains indoor
     exposure/audio with ground wetness visually unchanged outside it.
   - `uibridge_log` filter `[ArenaMapPort][RainCover]`: expect `AuthoredRainCover=true — skipping the
     depth-photo heuristic` for both maps, and `[ArenaMapPort][RainCoverImporter]` in the Unity Editor's
     own build log (not the runtime log) confirming the authored counts (`IndoorTrigger=30`/`DryPlane=669`
     on Bay5, `IndoorTrigger=1`/`DryPlane=0` on Equator).
3. **Done when**: both maps show correct indoor/outdoor rain behavior at the recovered trigger
   boundaries in a live raid, the old heuristic is confirmed silent on these two maps (per the log line
   above), and no new exceptions appear.

## Hunks added to shared files (conflict discipline)

- `tools/extract/extract_equator.py` (+3 lines, right after the existing
  `manifest["native_features"] = {...}` line):
  ```python
          # PLAN-TWO-MAPS item 6 -- rain-cover geometry (DryPlane/IndoorTrigger) from the Scripts scene.
          from extract_raincover import export as export_raincover
          manifest["native_features"]["raincover"] = export_raincover(args.source, args.output, "arena_equator")
  ```
- `tools/extract/extract_bay5.py` (+3 lines, right after its own equivalent manifest-literal block,
  before the file is written): same shape, `"arena_bay5"`.
- `tools/validate/validate_native_features.py`'s `feature_enabled()`: replaced the single strict
  `compare(manifest['native_features'], {...}, ...)` line with a pop-then-compare (see "How" above) — not
  a pure addition, but the smallest change that keeps the existing check exact for every manifest without
  a `raincover` key while accepting one that has it.
- `UnityProject/Assets/ArenaMapPort/Editor/Import/AuthorScriptsScene.cs`: one new call line
  (`RainCoverImporter.Build(pkg, root);`) before the `LocationScene` block, plus widening
  `AddComponentOrThrow` from `private` to `internal` (no behavior change).

All other logic lives in this task's own new files (`tools/extract/extract_raincover.py`,
`UnityProject/Assets/ArenaMapPort/Editor/Import/RainCoverImporter.cs`,
`tools/tests/test_raincover.py`) and in files this task owns outright
(`ArenaMapPort.Client/RainCoverRepairs.cs`, `WeatherRig.cs`, `EnvironmentRepairs.cs` — not named in the
task's shared-file list, but reviewed against parallel items 4/5's likely scope: physics props/AI anchors
touch `extract_equator.py`'s `COMPONENT_BUILDERS`/`SceneAssembler.cs`'s content-scene switch and their own
new modules, never these weather/rain-cover client files).

## Lead integration 2026-09-25 (merge, review fix, build evidence, first host raid)

- Review fix applied: `RainCoverImporter.Build` throws when `ScriptsRoot` is active in the hierarchy (the same
  guard as `Bay5AudioImporter`), so `IndoorTrigger`/`TriggerGroup` never run their `OnValidate`/`Awake`
  against the Bounds being written.
- `validate_bundle_roundtrip` counts the authored rain-cover objects: `validate_native_features.
  raincover_extra_counts()` adds `2 + indoor_trigger_count (+ 1 + dry_plane_count with weather_obstacle)`
  GameObjects/Transforms from `manifest.native_features.raincover` (Equator +3, Bay5 +702; the first Equator
  build failed with `GameObject count in bundle (39438) == package (39435)`). Tests:
  `RaincoverExtraCountsTests` in `tools/tests/test_raincover.py`.
- Build logs: Equator `[RainCoverImporter] authored EnvironmentManager (TriggerGroup=1, IndoorTrigger=1),
  WeatherObstacle=0`; Bay5 `… IndoorTrigger=30), WeatherObstacle=1 (DryPlane=669)`. The `IndoorTrigger`,
  `TriggerGroup` and `EnvironmentManager` MonoScripts are patched to `Assembly-CSharp` in
  `BuildPlayer-Arena_Equator_Scripts.sharedAssets`.
- Host raid 1 (Bay5, 13:09, instance 9f0fe0dc): `[ArenaMapPort][RainCover] map=arena_bay5 AuthoredRainCover=
  true — skipping the depth-photo heuristic (RainBounds scan, cullingMask widening, elevated-mesh draws); this
  map's own EnvironmentManager/WeatherObstacle geometry is used instead (RainCoverImporter.cs)`; no
  `WeatherObstacle.Instance is null` line in the whole session log (0 matches for "WeatherObstacle" at error
  level); the only errors were the pre-existing microphone line and the item-5 sound NREs above. Config keys
  live under `Lighting (<map>) › AuthoredRainCover` (true on both maps).

### Host evidence 2026-09-25 (v3 bundles, EFT instance 70aeac2c, `Weather (test) › Weather = Rain`)

- Bay5 14:32: `[WeatherTestOverride] applied 'Rain' (map=arena_bay5)` (twice, as the probe expects), then
  `[RainCover] map=arena_bay5 AuthoredRainCover=true — skipping the depth-photo heuristic …`. Outdoors at the
  spawn the floor is wet and reflective (screenshot 14:32). `uibridge_teleport` to the IndoorTrigger
  `117 (12)` centre (137.73, 0.30, 71.15) lands inside the warehouse: the floor there is matte/dry while the
  outdoor floor seen through the open gate stays wet, and no rain drops render (screenshot 14:33) — the
  authored `WeatherObstacle`/`DryPlane` roof and the `IndoorTrigger` bounds do what the depth-photo heuristic
  approximated. `WeatherObstacle.Instance is null` does not appear in either session's log.
- Equator 14:29: `[RainCover] map=arena_equator AuthoredRainCover=true — skipping …`; the whole playable mall
  lies inside its single IndoorTrigger (centre (1.85, 13.20, 9.63)), so there is no outdoor/indoor boundary to
  walk — log evidence only, no exception.
- The `Weather (test)` key was restored to its previous value `Clear` afterwards.

**Status:** VERIFIED 2026-09-25.

## Correction 2026-09-25 (evening) — the "VERIFIED" above was wrong for the rain itself

The user tested on `D:\SPT41vanilla` with the same build: **it rains indoors on both maps**. The 14:32 host check
above only looked at floor wetness and one still frame; it never verified the falling rain, and the log of that
very raid already said so (`DaytimeVisualsProbe … RainCover=RoofDepthBlack … depthCameraCullingMask=0x0`, a
readback with the class-default `RainBounds`). Root causes, established by two independent read-only agent
passes with adversarial verification (decompile + repo) and an offline check of the package data:

1. **`AuthoredRainCover=true` switched the depth-photo heuristic off with nothing to replace it.**
   `RainCoverRepairs.ApplyForCurrentMap` forced `Mode=Off`; vanilla's `DepthPhotograper.RenderObstacles` only draws
   `WeatherObstacle.Instance.MeshCollider.sharedMesh`. Equator authors no WeatherObstacle at all, so its depth photo
   was empty and rain fell into the whole mall — a straight regression against the pre-item-6 heuristic.
2. **Arena's 669 Bay5 DryPlanes are not Bay5's roofs.** Every one hangs under a root named `Lighthouse_DryPlanes`
   (the WeatherObstacle prefab of EFT's Lighthouse copied into the Arena scene) and they span x −383…454 /
   z −973…566 while Bay5 plays in x −60…160 / z −40…120; only 17 fall inside the map by chance and none roofs the
   warehouse or a container. Authoring them gave Bay5 a WeatherObstacle that covers nothing (and a few random
   quads). Offline coverage check: 0 % of every IndoorTrigger footprint except one room at 22 % (chance hits).
3. What actually culls the falling drops: `RainFallDrops` (decompile) has no indoor check in C#; its material
   only gets `_Intensity`/`_FallingVector`/`_RainDensity`, and the roof test happens in the shader against the
   global `_WeatherDepthMap`/`_MapStart`/`_MapScale` that `DepthPhotograper.SetShaderValues` publishes — i.e. the
   depth photo the heuristic feeds. `RainController.IsCameraUnderRain` (a raycast against
   `WeatherObstacle.MeshCollider`) only gates the camera effects (screen drops, ripples, screen water,
   `Player.IsUnderRoof`), and is `false` whenever no WeatherObstacle exists.

Fix (commit after this section):
- `extract_raincover.py`: `select_dry_planes()` keeps a DryPlane only if its xz centre lies over one of the map's
  own IndoorTrigger rooms (3 m pad, at/above the room floor); when most planes fail, the whole set is foreign
  and dropped (`raincover.json` records `dry_planes_dropped`, `dry_plane_roots`; the manifest summary carries
  `dry_planes_dropped`). Bay5: `dropped 669 (foreign set: 667 of 669 planes roof no room; roots:
  ['Lighthouse_DryPlanes']); kept 0`, `weather_obstacle=False`. Equator unchanged (0/0). Tests:
  `DryPlanePlausibilityTests`.
- Client: `ApplyForCurrentMap` no longer forces `Mode=Off` for `AuthoredRainCover=true`; the heuristic stays armed
  on both maps (RainBounds scan + RoofDepthBlack elevated-mesh draws), and a real WeatherObstacle — should a map
  ever ship genuine DryPlanes — is drawn by vanilla in addition (union of both covers, logged once). The
  `AuthoredRainCover` key keeps its default and its documented purpose (the authored EnvironmentManager/
  IndoorTrigger data is used); only its description text was corrected.
- Bay5 rebuilt from the re-exported package (Scripts scene now: EnvironmentManager > TriggerGroup > 30
  IndoorTriggers, no WeatherObstacle); Equator's bundle is unchanged (its raincover data did not change).

Host verification recipe this time (what the first check should have done): force rain, then at each test spot
press **F8** (`RainCoverLab › RerenderHotkey`: re-renders the depth photo and logs `[RainCoverLab] RainCover depth
readback @ (…)` with the sampled roof depth at the camera) and take a screenshot against a dark background
where falling streaks are visible. Spots: Equator mall (any), Bay5 warehouse (137.7, 0, 71.1) and a container
(104.9, 0.1, 40.0), plus one outdoor control.

### Host evidence after the correction (2026-09-25 17:49–18:02, EFT instances 3ee0c3e9 / bbacc900, forced rain)

Both raids logged `[WeatherTestOverride] applied 'Rain'` twice and, for the first time on these maps with
`AuthoredRainCover=true`, the heuristic's own lines: Equator `RainBounds computed from 8020 renderer(s) …
size=(102.51, 57.83, 154.24)`, `RoofDepthBlack: drew 7580 elevated mesh(es) (9247 submesh draw call(s))`; Bay5
`RainBounds computed from 15702 renderer(s) … size=(351.63, 97.94, 726.13)`, `drew 14304 elevated mesh(es)`
(the rebuilt Bay5 bundle authors `IndoorTrigger=30, WeatherObstacle=0`). F8 (`RainCoverLab › RerenderHotkey`,
reaches EFT through windows-gpu `Shortcut f8`) then read the depth photo at the camera:

| Spot | Camera y | Ground y | Photographed height | Verdict |
|---|---|---|---|---|
| Equator mall, spawn corridor (−4.91, 1.62, −33.38) | 1.62 | 0.03 | 5.23 | covered — no streaks looking up at the dark ceiling |
| Bay5 spawn yard (119.92, −2.34, 57.78), outdoor control | −2.34 | −3.89 | −3.85 (= ground) | uncovered — rain |
| Bay5 warehouse (137.77, 1.53, 70.92) | 1.53 | −0.03 | 8.00 | covered — no streaks against the hall roof |
| Bay5 container (104.72, 0.50, 40.03) | 0.50 | −1.10 | 1.39 | covered — no streaks on the container ceiling |

`nothingPhotographed=False` everywhere; the readback's `height(reversedZ)` is the roof height the rain shader
compares against, so an outdoor value equal to the ground and indoor values above the camera are exactly the
two states that matter. No `WeatherObstacle.Instance is null` line appeared. **Status: rain culling under
roofs verified on both maps (heuristic); IndoorTriggers authored; no WeatherObstacle on either map.**
