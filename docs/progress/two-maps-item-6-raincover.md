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

## Round 3 (2026-09-25 evening) — Equator's atrium, and why Bay5 has no rain-reactive puddles

User report after round 2: rain still falls in Equator's main hall (visible as falling drops; easy to miss at low
graphics settings), Bay5 is fine, and Equator's puddles show a "rain hitting the water" animation that Bay5 lacks.

**Equator atrium.** Two F8 readbacks under the dome (18:36 at (1.77, 1.88, 9.50) and 18:37 at (11.02, 1.55,
19.72), forced rain) read cover at 25.85 m and 25.07 m — beams. The offline scene data explains the drops: the only
roof renderers over the atrium footprint are `equator_roof_LOD0` (the frame: decor_plastic/metall_02/brown_wood,
active) and `equator_roof_Glass_LOD0` (material `arena_equator_broken_glass_atlas_roof`, shader "Global
Fog/Transparent Reflective Specular") which Arena ships **inactive**; the four `FogSheetSimple` billboards at
19–24 m are excluded by the material rule. So the dome is an open lattice: what looks like glass from below is the
overcast sky through the cells, the depth photo has rain-sized holes between the beams, and the rain falls through
exactly as it would in Arena if it ever rained there. `CoverIncludesGlass` cannot help (the pane mesh never
renders); the 18 included glass renderers are the elevator glazing.

Fix (client only): new key `Lighting (arena_equator) › FullyIndoorCover` (default true on Equator, false
elsewhere) — `RainCoverRepairs.RenderObstaclesPrefix` draws the RainCoverLab CoverAll quad over the whole
RainBounds footprint, so the photo reads covered everywhere: no falling rain and no wetness anywhere in the mall
(the whole playable area is one IndoorTrigger). Trade-off: with it the atrium's `puddle2` decals no longer get
rained on; set it to false to keep rain through the broken roof and the wet, rippling atrium floor. **Not yet
host-verified** (the host server was offline when the build was ready; the user is replacing the test machine) —
verify with F8 at the atrium: `height(reversedZ)` must read ≈ RainBounds.max.y − 1 (≈ 47 m) and no drops.

**Bay5 puddles.** Ripples/splashes on puddles come from the `Decal/Water Deferred Decal` puddle decals with the
`USERAIN` keyword (`_USERAIN=1`): Equator has 21 `puddle2` decals with that material at ground level. Bay5's package
contains only two puddle materials and both are the non-reactive kind (`puddle benz` on `puddle_oil` /
`puddle2_indoornoripple` objects inside the halls, and the shared `puddle indoor static`) — Arena's Bay5 never had
outdoor rain puddles; its wet yard look is the vertex-painted ground shader. Forcing `USERAIN` onto `puddle benz`
would animate indoor oil puddles, so nothing was changed. Adding real rain puddles to Bay5 would mean authoring new
decals (or testing the ground shader's own rain response) — a separate item, not a bug in the port.

## Round 3 verified on SPT-GPU-2 (2026-09-26) — decision: let it rain

User question after the first look at the new test machine: was the atrium rain intended (broken glass roof)? With
`FullyIndoorCover=true` the drops were gone but the puddle animation stayed — "vielleicht lassen wir es einfach regnen?"

**What the data says (offline, read-only pass: three evidence agents plus two adversarial re-checks).** Arena's
Equator ships the frame `equator_roof_LOD0` active and the pane mesh `equator_roof_Glass_LOD0` inactive with material
`arena_equator_broken_glass_atlas_roof` (both under `SBG_Arena_Equator / SOO_LOD0 / equator_roof`). The inactive flag is
Arena's authored state: a raw UnityPy read of the Arena install's `level51` shows `m_IsActive=False` on that GameObject
(path_id 13506); the object carries only Transform/MeshFilter/MeshRenderer (no LODGroup, no MonoBehaviour — Arena's
`WindowBreaker` sits on five other glass objects); the glass ballistic collider stays active with penetration 1.0 /
fragmentation 0.0; and a dedicated `Glass_Shards` debris prop (materials "glass shards" / "glass shards pile") has four
of its six placements 1–6 m from the puddles under the dome, all six inactive as well. So the roof is a deliberately
built "broken glass" art pass (finished or abandoned), and the pane mesh, were it active, would cover the whole lattice
(262 triangles, one pane per opening). The puddles are a weak signal on their own: of the 21 rain-reactive `puddle2`
decals (`Decal/Water Deferred Decal`, keyword `USERAIN`) 12 lie inside the glass footprint and 9 outside (5 of them at
outdoor `Respawns_both` spawns), and the 17 non-reactive `puddle2_indoornoripple` decals split 10/7 the same way with a
material shared verbatim with Bay5 — generic mall-wide wet-floor dressing, not placement against the hole. The only
`USERAIN` material in the package is that puddle decal. Arena has no per-map weather preset for Equator
(`analysis/equator_tdm_preset.json` is a scene table; the Scripts scene carries one `WeatherController`/`RainController`/
`RippleController` each), and whether Arena's own modes ever rain on Equator is not decidable from static data; the port
runs the Reserve donor rig with server weather. Conclusion: the open, broken roof is Arena's authored geometry, not a
porting defect, and rain through the lattice is its faithful rendering whenever the weather rains — the user's call to
keep it.

**Why the puddles ripple under the CoverAll quad (correction of the Round 3 paragraph above).** The sentence "with it
the atrium's `puddle2` decals no longer get rained on" was wrong. The decal ripple is driven by `RippleController.Update`
(decompile): `WaterCucle.Update(deltaTime, Intensity)` advances with the raw `Time.deltaTime` every frame and
`SetShaderValues` publishes `_WaterRippleCycle`, `_WaterRippleConfig`, `_RippleTexture`, `_RainIntensityRippleScale` … as
process-wide `Shader.SetGlobal*` values; `Intensity` is `RainController.Intensity`, the map-wide rain amount
(`AbstractStateSummer.SetIntensity`). Nothing in that path reads `_WeatherDepthMap`/`_MapStart`/`_MapScale`, and
`IsCameraUnderRain` only gates the separate `EnvironmentCucle` plus the screen/weapon wetness FX (`ScreenWater`,
`RainScreenDropsPlacer`, `RainCondensator`). So the depth photo — and with it `FullyIndoorCover` — can only remove the
falling drops; a `USERAIN` puddle ripples wherever it was placed as long as the weather has rain, which is exactly what
the user saw. Vanilla avoids the mismatch by authoring non-reactive puddles indoors, as Arena did with
`puddle2_indoornoripple`.

**Host evidence (SPT-GPU-2, EFT instance cd73afd7, 18:04–18:06, forced rain, max graphics settings).**

| Spot | FullyIndoorCover | F8 `height(reversedZ)` | Screenshot |
|---|---|---|---|
| atrium (2.08, 0.42, 11.13), 18:05:22 | true | 47.51 on all 9 samples (= RainBounds top 48.41 − 1, y-quantised) | — |
| atrium (1.95, 1.84, 9.36), 18:05:50 | false (set live, F8 re-render) | 23.10–23.35 (beam) | streaks visible against the elevator tower and the dark upper walls |

The `re-rendered depth photo (… CoverAll=False)` line printed the RainCoverLab value only while the photo was fully
covered by `FullyIndoorCover`; the line now carries `FullyIndoorCover=<bool>` too.

**Decision and change.** The default of `Lighting (arena_equator) › FullyIndoorCover` moved to false (everywhere); the
key stays as an opt-in dry mall. Because BepInEx persists the value, `DefaultsRevision` 3 moves a persisted `true` back
to `false` once (only that value, the same rule as revisions 1–2). Verified after the EFT restart (instance 95e625d7,
18:11): `uibridge_config` reads `FullyIndoorCover = false (default false)` and `DefaultsRevision = 3` (the re-seed line
itself is logged before UiBridge starts recording, so the config read is the check); the 18:12 raid logged
`RoofDepthBlack: drew 7580 elevated mesh(es) (…, CoverAll=False)` and `re-rendered depth photo (DrawTransform=None,
CoverAll=False, FullyIndoorCover=False) [automatic raid bootstrap]`. Deployed to `D:\SPT41` (18:08) and, host-verified,
to `D:\SPT41vanilla` (18:15; its server restart is the user's). Bay5's puddle content gap (above) is unchanged.
**Status: item 6 VERIFIED on both maps; Equator's atrium rains by design.**


## 2026-11-21 - rain drops on the screen: the camera-under-rain flag from the depth photo

**Request (user, 2026-11-21):** "auch mal wegen den Regentropfen auf dem Bildschirm schauen" (have a look at the rain drops on the screen). Status: **unit-tested, host check pending**.

**Cause (read from the EFT 0.16.9.5 decompile, not host-run).** `RainController.Update` sets the static `RainController.IsCameraUnderRain` every frame from
`DepthPhotograper.CheckIfCameraUnderRain`: one ray from 100 m above the camera, 100 m long, against the one `MeshCollider` of `WeatherObstacle.Instance`; "under rain" means no hit, and the method
returns false while no WeatherObstacle exists. No ported map has one (the deferral "Goal 3(c)" of `RainCoverRepairs`, section 12.4(c) of donor-settings-camera-weather.md), so the flag was false for ever.
Host evidence of 2026-11-21 (Yard, standing in the open yard in a storm): `[Footsteps] ... environment=Outdoor underRoof=True cameraUnderRain=False`. The falling rain is a different mechanism and
was never affected: its shader culls against the depth photo (`_WeatherDepthMap`), which `RainCoverRepairs` feeds.

**Design (`RainUnderRoofRepairs.cs` glue, `RainUnderRoofModel.cs` Unity-free core, design note in work/tmp/yard-rain/handoff/design.md).** The flag is answered from the very photo the rain shader uses, so
the drops on the screen and the falling streaks agree by construction, in every RainCover mode, with `FullyIndoorCover` and with the lab switches.
- After every `DepthPhotograper.Render()` (Harmony postfix) the repair keeps that photo's parameters (`_MapStart`, `_MapScale`, dimension, the depth camera's top and far clip, reversed-Z) and bumps a
  generation. While the camera moves it requests a window of at most 7 x 7 texels (about +-0.25 m) around the camera's texel with `AsyncGPUReadback` (no stall; at most two requests in flight; only when the
  texel changed). The window's highest photographed surface is the top surface at the camera.
- A postfix on `CheckIfCameraUnderRain` (per frame; one static bool read when nothing is armed) sets the flag to "under rain" when no surface was photographed there or the highest one is not higher than
  the camera minus 0.1 m (`RainUnderRoofModel.CoverMargin` = -0.1: a surface at head height or a little below it still counts as a roof; the review of 2026-11-21 found that the first margin of +0.2 m, copied from
  `RainExposurePolicy.IsOverhead`, leaned the WRONG way for a thin overhang just above the head). Latency is 2 to 3 frames; a result older than 2 m of camera travel (respawn, teleport) is not used.
- Not ready, in every one of these cases the flag stays vanilla's false ("under roof", today's behaviour, the harmless error): no photo, no result yet, stale result, camera outside the photo, readback error,
  the key off, and any photo that carries no cover (`RainCoverMethod` Off or the legacy RoofDepth, an unfinished scan, zero cover meshes). The window maximum, the negative margin (the conversion ignores the 0.1 m near plane and so reports surfaces up to 0.1 m too high, which leans the same way) and the not-ready state all lean
  towards "under roof"; a false "under rain" indoors would be the harmful error.
- A real `WeatherObstacle` with a collider always wins (the postfix returns before it writes). The vanilla "WeatherObstacle.Instance is null" LogError stays (cosmetic).
- Raid end: `WeatherRig.OnSceneUnloaded` calls `RainUnderRoofRepairs.Release()`: disarm, new generation (late readbacks are dropped), and `RainController.IsCameraUnderRain` back to false when this repair ever wrote
  true (the donor RainController is deactivated at raid end and would leave its last value for the menu, the hideout and the next raid). Vanilla raids and the hideout only ever pay the one static read.
- The depth-to-height conversion and the texel mapping that the F8 readback logs are now the model's functions (`RainCoverRepairs.ReconstructHeightReversedAware`, `IsNothingPhotographed`, `WorldXZToTexel`
  forward to them), so the tested math is the logged math.
- The readback's row order is not assumed: once per session a 4 x 4 RFloat probe (a Texture2D with sixteen different texels, `Graphics.Blit`ed into an RFloat target, the way the photo is written) is read back
  through the SAME region overload the windows use (one row, two columns) and classified by the logical row that came back: row 0 = the region's y is the texture's row, last row = the region's y counts flipped
  native rows, anything else = unusable (`RainUnderRoofModel.ClassifyRegionProbe`; the first version classified a whole-texture copy, which cannot tell what a region's y means). Unusable keeps the repair off with
  one Warning. The probe restores `RenderTexture.active` after its Blit.
- A failed or short readback of the current photo asks for the camera's texel again on the next frame (`RainUnderRoofState.ForgetRequest`); a readback of an ended photo or after the raid counts neither as a
  result nor as an error, so two short raids cannot switch the repair off for the session.

**Why not the lead's first preference (a CPU copy of the whole photo, downsampled).** The photo is 4096 x 4096 RFloat (64 MiB). A whole-photo readback is a 64 MiB copy plus a 16.7 M sample reduction on the main
thread at every camera change (every respawn in ArenaMode), the synchronous `CaptureDepthSnapshot` of the Equator-only `PrecipitationCover` stalls, a GPU downsample needs a max-filter shader the plugin does not
have, and a mip chain averages roofs with open sky. The window needs none of that. **Why not a synthetic WeatherObstacle:** a combined non-convex MeshCollider would have to be cooked on the main thread at raid start,
needs readable meshes, a downward ray only hits up-facing triangles and vanilla would draw the mesh into the photo a second time. **Why not a physics raycast:** a different truth than the photo (drops and
streaks could disagree), invisible layer-29 border boxes and movement lids would count as roofs, and a roof without a collider on the chosen layers would give drops INDOORS.

**Key.** `Lighting (<map key>)/RainUnderRoofCheck` (bool, default true, per map, not public, so `CompactConfiguration.IsPublic` and `MapConfigVisibility.IsPlayerOption` are unchanged). Off = vanilla (false) at once;
switching it on photographs the depth again (`RainCoverRepairs.RescanAndRerender`).

**Every other effect of the flag on a ported map (all read from the decompile; none host-checked).** The flag turns true wherever the photo shows no cover above the camera:
- `RainScreenDropsPlacer.Update`: the screen drops (intended). They need `Intensity > 0.15` AND the flag, and their rate is scaled by how far the camera looks up (`(dot(forward, up) + 1) * 0.6`, no drops when that is 0.2 or less),
  so there are no drops when the camera looks down steeper than about 42 degrees (`dot(forward, up) < -0.667`), a level view gives a factor of 0.6, looking up the most; light rain gives fewer drops. That is
  vanilla's design, not a fault of the repair. **Test with the camera level and tilted up, not only tilted up.**
- `ScreenWater.OnRenderImage`: with glasses (`_hasGlases`) the distortion amount ramps up while the flag is true and down when it is false (`_distortionCooldownTime`); so glasses now fog or distort in the open in rain.
- `RippleController.Update`: the environment ripple cycle (`EnvironmentCucle`) advances only with the flag, so the environment ripples (the ripples on wet ground that is not a water puddle) move outdoors in rain
  for the first time on ported maps. The water cycle never depended on it (see "Round 3" above).
- `RainCondensator.UpdateValues`: renderers with a condensator take the falling rain vector in the open instead of 0.9 x their stored direction.
- `Player.IsUnderRoof` (= `!flag` unless `_useSimpleUnderRoofCheck`): `Player.method_35` (called when a step surface is checked outdoors and when the season layer is built) hands it to
  `PlayerStepLayerAudioController.UpdateUnderRoofStatus` and to `SendUnderRoofStatus`. `PlayerStepLayerAudioController.Play` returns without a sound when `Environment == Indoor` and, outdoors, plays the season
  step layer (`bank.Play(...)`) only when `_isUnderRoof` is FALSE. Until now it was true everywhere, so the season step layer never played on a ported map; with the repair it plays outdoors in the open (and
  stays silent under a roof) **if** a season movement set exists for the current season status (`InitSeasonStepLayerController` returns early with "Can't find movement sound set for season" otherwise, and the
  footstep probe's `seasonLayerRun=` / `layerUnderRoof=` fields say which). If the Summer set has such a layer this is an audible change in every step outdoors; I could not tell which season sets carry banks.
  When `UseSimpleUnderRoofCheck` of the map's AmbientAudioSystem is true the flag is not read at all (the player's Environment decides).
  Other players' `ObservedPlayerAudioController` take the status from the network message, so bots are unaffected.

**Log lines (Debug, tag `[RainUnderRoof]`).**
- `[Harmony] patched DepthPhotograper.CheckIfCameraUnderRain (...)` and `... DepthPhotograper.Render (...)` at start.
- `[RainUnderRoof] map=<key> armed on a new photo: generation=N dim=4096 texel=0.xxxm window=7x7 depthTop=.. far=.. reversedZ=True coverDraws=N margin=-0.10 orientation=<AsWritten|FlippedY|Unusable>` after every photo.
- `[RainUnderRoof] readback orientation probe: AsWritten|FlippedY|Unusable` once per session.
- `[RainUnderRoof] map=<key> cameraUnderRain=True|False camera=(x, y, z) photoTop=<h|nothing> margin=-0.10 generation=N` on every change of the answer (at most one line per 0.5 s).
- `[RainUnderRoof] not armed for map=<key>: <why>` (RainCoverMethod, unfinished scan, no cover meshes) and `... released at the end of the raid`.
- One Warning at most: `[RainUnderRoof] the rain-drops check cannot work (<why>): the screen drops stay at vanilla's setting ...`.
- The decision's inputs also ride on the F8 readback line (`rainUnderRoof[armed=.. key=.. generation=.. orientation=.. margin=-0.10 lastCameraY=.. lastPhotoTop=.. atPosition: photoTop=.. cameraY=.. cameraUnderRain=..] (disarmed: `notReady=disarmed lastWhy=.. lastDisarm=<reason>`)`) and on the footstep
  probe's `environment=... underRoof=... cameraUnderRain=...` line.

**Tests.** `RainUnderRoofModelTests` (conversion in both depth conventions, texel mapping and its edges, window radius, clipping and flipped rows, top surface, the negative margin, region-probe classification and its plan, the engagement gate (`CarriesCover`), state: not ready,
hall ceiling, open sky, ground, standing on a roof, one request per texel, a failed request asked again, a re-render replacing the photo and dropping in-flight results, new parameters, ordering, stale result, reset) and
`RainUnderRoofSourcePinTests` (one static read first, no scene search, no string work, no allocation and no log in the frame path, the logging helper rate-limited, the real-obstacle early return, try/catch on every hook,
Unity-free core, wiring, release at raid end, key through `Settings` and not public, the engagement gate asked before arming, the late-readback order, the region probe, the postfix signatures). The GPU glue (`AsyncGPUReadback`, the probe) is not covered by any unit test.

**Blast radius, for the author (the repair is ON by default on all seven maps).** Five of them are released (Equator, Bay5, Air Pit, Bowl, Chop Shop in 0.6.0; Iceberg and Yard are not), so a released map changes in rain
outdoors: drops run down the screen, glasses (if worn) distort, the environment ripples on wet ground move, and the outdoor step sound may gain the season layer (open question above). Under a roof nothing changes.
What to look at and listen to: the screen drops (level and tilted up), the glasses, the ripples on the ground, and outdoor footsteps with the key off, then on. Whether the repair goes into a release, and with
what wording, is the user's decision. Host check pending.

**What the lead must check on the host (nothing of this is verified).**
0. First look at `[RainUnderRoof] readback orientation probe:` (once per session, `AsWritten` or `FlippedY`; `Unusable` = repair off with the one Warning). The FlippedY branch is exercised by unit tests only: if
   the probe says FlippedY, confirm in a hall and in the open that the flag is right (a mirrored row would put roofs at the opposite side of the photo).
1. Yard, open yard, forced rain (`uibridge_hook` weather/rain as in the earlier rain checks), PMC at the south spawn: the log has the `armed on a new photo` line with `orientation=AsWritten` or `FlippedY` (not Unusable) and
   `cameraUnderRain=True` at the open spot; screenshot with the camera tilted up 30 to 60 degrees for a few seconds: drops on the screen. Then walk or teleport into a house or a team pocket: `cameraUnderRain=False`
   and no new drops; the F8 line shows `photoTop` above `cameraY` there.
2. One hall map (Equator main hall or the Air Pit hall) in rain: under the roof no drops, and the F8 `height(reversedZ)` of the centre texel agrees with `photoTop`; Equator's open atrium lattice follows the photo
   (beams are covered, gaps are not, so the flag may flicker there: the 7 x 7 window leans towards roof).
3. The step audio: walk on the open yard and listen, and read the footstep probe's `seasonLayerRun=`, `layerUnderRoof=` and `underRoof=` fields before and after (key off then on: `Lighting (arena_yard)/RainUnderRoofCheck`).
4. After leaving the raid: `[RainUnderRoof] released at the end of the raid`, then the hideout once (it must load) and a vanilla raid if one is at hand: no drops without rain, nothing logged by the repair.
5. A/B: key off in the same spot, the drops stop at once and `cameraUnderRain` reads False again; frame time: `uibridge_status` `max` and ModProfiler `max_ms_in_frame` while walking (the repair issues at most one
   small readback per frame while the camera moves).
6. A respawn or teleport across the map: for 2 to 3 frames the flag is vanilla's false, then it follows.

## 2026-11-21 - rain drops on the screen: the lead's acceptance and the host check (GPU-2, 17:44 to 17:50, EFT instance 11e5bf1a)

**Read and run by the lead.** Both new files, the wiring and the reviews; `dotnet build` of the client (DllOnly, Release) 0 warnings; `dotnet test ... -c Release`: `Bestanden! Fehler: 0, erfolgreich: 3519`. Deployed with `-DllOnly`, server and EFT restarted (`16/16 identical`). The section above says "host check pending" throughout: this section is that check. One raid on Yard (not released) and one on Equator (released), both without bots, storm set at raid start (`rain 1`, clouds 1, wind 0.9), season AutumnLate; then the hideout.

- **Hooks and probe:** `[Harmony] patched DepthPhotograper.CheckIfCameraUnderRain ...`, `... DepthPhotograper.Render (rain drops on the screen: new photo, new generation)` (HarmonyX lists two postfixes on `Render`, ours beside `RainCoverRepairs.RenderPostfix`); `[RainUnderRoof] readback orientation probe: AsWritten`. **The FlippedY branch therefore never ran on a host**; it is unit-tested only.
- **Yard, open (north pocket, the spawn):** `[RainUnderRoof] map=arena_yard armed on a new photo: generation=1 dim=4096 texel=0.059m window=7x7 depthTop=60.70 far=68.58 reversedZ=True coverDraws=12049 margin=-0.10 orientation=Unusable` (the probe answers a moment later), then `cameraUnderRain=True camera=(-7.28, 7.68, 146.61) photoTop=6.16`: the same 6.16 the F8 readback of the morning gave there. Screenshot with the camera tilted up 35 degrees: drops running on the screen and falling streaks.
- **Yard, south team room:** `cameraUnderRain=False camera=(-12.76, 8.94, 9.97) photoTop=28.65` (F8 of the morning: 28.60); screenshot, tilted up: no drop. North pocket open and south room covered are not mirror images of each other in the photo, so this pair also rules out a swapped row order.
- **Yard, yard middle, camera level:** drops on the screen (screenshot). Sprinting and walking 36 m through the rain: `120.0 fps (mean 8.3 ms, p95 8.3 ms, max 10.1 ms ... over 1200 frames / 10.0 s)`, the figures of the baseline without the repair.
- **Key A/B, live** (`uibridge_config_set ... persist=false`): off: `[RainUnderRoof] key switched off (disarmed)`, F10 `underRoof=True cameraUnderRain=False rainUnderRoof[armed=False key=off/none ... lastDisarm=key switched off]`; on: `[RainCover] ... re-rendered depth photo ... [RainUnderRoofCheck switched on]`, `armed on a new photo: generation=4 ... orientation=AsWritten`, `cameraUnderRain=True camera=(-14.93, 7.72, 39.53) photoTop=6.19`, F10 `underRoof=False cameraUnderRain=True`.
- **The step layer:** the footstep probe printed no `seasonLayerRun=` line in either state, so the local player has no season step-layer controller on Yard at AutumnLate and outdoor steps cannot have changed there. Other seasons are not checked.
- **Raid end:** `cameraUnderRain=False camera=(0.10, -9997.48, 0.12) photoTop=28.15` (EFT parks the camera far below the map; the answer there is "roof", the harmless side), `[RainUnderRoof] released at the end of the raid`.
- **Equator (released), south booth (the spawn):** `armed on a new photo: generation=6 dim=4096 texel=0.038m ... coverDraws=7672 ... orientation=AsWritten`, `cameraUnderRain=False camera=(-4.25, 1.60, -38.96) photoTop=5.23`.
- **Equator, atrium under the open lattice:** `cameraUnderRain=True camera=(0.10, 1.72, 1.93) photoTop=0.13`; screenshot, tilted up 40 degrees: drops on the screen, streaks falling through the lattice (rain through the lattice is the map's decided look). Walking west out of the atrium the answer changed several times within five metres (`photoTop=26.09`, `3.14`, `25.65`: the lattice's beams and a prop), as predicted; nothing of it is visible as flicker in the drops, which only stop spawning under a beam.
- **Equator, mall corridor under the roof at (-25, 0.1, 0):** `cameraUnderRain=False ... photoTop=24.65`; screenshot, tilted up: no drop. Frame time over the walk: `120.0 fps (mean 8.3 ms, p95 8.3 ms, max 9.6 ms ... over 2400 frames / 19 s)`.
- **Hideout:** opened once after the raids (`screen=Hideout` after 5.5 s), no line of the repair there. Warnings of the session from ArenaMapPort: only the known `[DynamicMaps] DynamicMaps API differs from verified 1.2.1`.

**One diagnostic corrected after the run.** The footstep probe handed the player's FEET to `RainUnderRoofRepairs.Describe`, where the photographed floor itself counts as cover (`atPosition: photoTop=6.19 cameraY=6.20 cameraUnderRain=False` beside a true flag); it now hands over the camera's position. The flag itself always used the camera. This change is built and unit-tested, not host-run.

**Verified on the host:** the cause, the flag in the open and under a roof on two maps, the screen drops, the live key, the release at raid end, the hideout, frame time while moving. **Not checked:** glasses (`ScreenWater`), the environment ripples and the condensators by eye; any season other than AutumnLate for the step layer; Bay5, Air Pit, Bowl, Iceberg and Chop Shop (same code, their own photos); light rain (the drops need intensity above 0.15); a vanilla raid after a ported one; a respawn in ArenaMode. **The author has not seen it.** It changes five released maps in rain; whether and how a release text names it is the user's decision.
