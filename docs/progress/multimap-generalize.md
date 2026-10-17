# multimap-generalize — arena_equator -> multi-map abstraction (progress log)

Role: builder, task label `multimap-generalize`. Task: land PLAN.md §1's generalization refactor
(with critic amendments) so `arena_bay5` can be registered as a second location alongside the
already-working `arena_equator`, **without** rebuilding, re-extracting, or otherwise touching
Equator's already-shipped bundles/behavior, and **without** extracting or building Bay5 itself this
pass. Refactor-only.

**Result: done for the scope asked.** Shared descriptor + registry, client raid gate, every
`*Repairs.cs` gate/config section, server registration + `OnLoadAsync`, server tests, and the
Editor/Python/PowerShell tooling entry points are now map-parameterized. `arena_bay5` is registered
in `ArenaMapRegistry.All` (proving the abstraction) but has no extracted scenes/built bundles/
`db/arena_bay5/base.json` yet — its own registration attempt fails gracefully (logged, caught,
Equator unaffected) until a follow-up session extracts/builds it. Equator's 3 dist bundles are
confirmed byte-identical to the recorded baseline hashes after this refactor; `dist/.../db/base.json`
content is confirmed unchanged. No git commit made (workspace rule: the lead commits).

---

## 1) The ordering hazard (critic amendment #2) and how it was actually fixed

The plan's own draft sketch for `ArenaMapRaid`/`ArenaEquatorRaid`'s replacement cached `CurrentMap`
in a field, set/cleared by the class's own `SceneManager.sceneLoaded`/`sceneUnloaded` subscriptions.
That is unsafe: `SceneManager`'s events are C# multicast delegates, invoked in subscription order.
Half a dozen other files (`NavMeshInstaller`, `ShaderRebind`, `MapBorderRepairs`,
`ArenaLightingRepairs`, `LightingDiagnosticProbe`) also subscribe to `sceneUnloaded` to reset their
own per-raid state, gated on "did the Scripts scene I own just unload". If `ArenaMapRaid`'s own
handler happened to run *before* one of theirs (or, on the load side, *after* one of theirs), that
handler would read a stale/null `CurrentMap` depending purely on install order — a genuine, easy-to-
reintroduce bug, not a hypothetical one (it's exactly why Equator's original `ArenaEquatorRaid` never
had this problem: it had no mutable state to race, just two bare bool/string comparisons).

Fix actually shipped (deviates from the plan's sketch, on purpose):

- `ArenaMapRaid.CurrentMap` is a **computed property**, not a cached field — it loops
  `ArenaMapRegistry.All` and asks `SceneManager.GetSceneByName(map.ScriptsSceneName)` fresh on every
  access. `ArenaMapRaid` itself has **no** `sceneLoaded`/`sceneUnloaded` subscription at all. Unity
  registers a scene with `SceneManager` before firing `sceneLoaded` for it, so this resolves
  correctly even during the Scripts scene's own load callback.
- Every `OnSceneUnloaded` reset handler across the plugin (`NavMeshInstaller`, `ShaderRebind`,
  `MapBorderRepairs`, `ArenaLightingRepairs`, `LightingDiagnosticProbe`) was switched from comparing
  `scene.name == ArenaEquatorLocation.ScriptsSceneName` to
  `ArenaMapRaid.MapForScriptsScene(scene.name)` — a second, equally stateless helper that resolves
  "which registered map (if any) owns this Scripts scene name" purely from the scene name argument
  Unity's own event already hands it, with zero dependency on any other handler's state or install
  order.

Net effect: no handler in the plugin depends on subscription order relative to any other handler,
anywhere. This is a stronger invariant than "install `ArenaMapRaid` first/last" would have been, and
was verified by reading every `sceneLoaded`/`sceneUnloaded` subscriber in `ArenaMapPort.Client` (all
9 files) and confirming each one now resolves its map statelessly.

## 2) Shared: `ArenaMapDescriptor` / `ArenaMapRegistry`

New file `ArenaMapPort.Shared/ArenaMapDescriptor.cs`. `ArenaEquatorLocation.cs` is **untouched** —
`ArenaMapRegistry.Equator`'s every field is read directly off `ArenaEquatorLocation`'s own
consts/statics (`Key: ArenaEquatorLocation.Key`, etc.), not re-declared as a second literal, so
byte-for-byte drift between the two is structurally impossible, not just unit-tested against (a
`MapDescriptorConsistencyTests` fact still exists, per the plan's ask, as a regression guard and to
match the project's own "diff a duplicated table against the built artifact" idiom).

`ArenaMapDescriptor` is a **plain class with a constructor**, not a C# 9 positional `record`:
`ArenaMapPort.Client` targets `netstandard2.1`, which has no
`System.Runtime.CompilerServices.IsExternalInit` — a record's compiler-generated `init` accessors
fail to compile there (`CS0518`) without a polyfill. Discovered by actually building the client
(`dotnet build ArenaMapPort.Client -c Release` failed with 14 `CS0518`s the first time) rather than
assumed; plain get-only properties sidestep the whole question and nothing here needed record
value-equality or `with`-expressions anyway. Constructor parameters are (deliberately unusually)
PascalCase so every call site can still pass them as named arguments matching the property names
1:1, reading like the positional-record syntax the plan originally sketched.

Per critic amendment #2, **no `TeamFightDesignStuffSceneName` field was added** — re-reading
`MapBorderRepairs.cs`'s Item 1 confirmed it never gates on a specific scene name (it walks every
owned scene matching on the `LevelBorder` layer/name-prefix), so the field would have been unused.

One field beyond the plan's original sketch: `DbBaseJsonPath`. Equator's is `"base.json"` (the
existing, unmoved path — `ArenaMapPort.Server/db/base.json`); Bay5's is `"arena_bay5/base.json"`.
This is how the server keeps Equator's current db path working (see §5) without needing an
Equator-special-case `if` anywhere in `ArenaMapPortMod.OnLoadAsync`.

`ArenaMapRegistry.All = { Equator, Bay5 }`.

## 3) Client (`ArenaMapPort.Client`)

- **`ArenaEquatorRaid.cs` → `ArenaMapRaid.cs`** (renamed). `IsLoaded()`/`IsOwnedScene()` now loop
  `ArenaMapRegistry.All`; `CurrentMap`/`MapForScriptsScene` are new (§1). All 21 pre-existing call
  sites across 12 files were mechanically renamed `ArenaEquatorRaid.` → `ArenaMapRaid.` (verified
  by grep: zero `ArenaEquatorRaid` references remain outside `ArenaMapRaid.cs`'s own "formerly"
  doc-comment).
- **`BundleHost.cs`** — `OurBundles()` now loops `ArenaMapRegistry.All`, populating all 3 served
  keys per map instead of 3 Equator literals.
- **`NavMeshInstaller.cs`** — every cache field (`_data`/`_loadTried`/`_instances`) is now
  `Dictionary<string, T>` keyed by `map.Key`. `OnBotsControllerInit` reads `ArenaMapRaid.CurrentMap`;
  `OnSceneUnloaded` resolves the map via `MapForScriptsScene` (§1) and drops only that map's cached
  instance.
- **`Plugin.cs`** — startup banner loops `ArenaMapRegistry.All`, logging each map's 3 bundle
  filenames + its shader sidecar filename (see next bullet); the "N owned scenes" summary line sums
  across all registered maps.
- **`ShaderRebind.cs`** — Equator's sidecar keeps its exact pre-generalization filename
  (`material-shaders.json`, already deployed at
  `dist/BepInEx/plugins/ArenaMapPort/material-shaders.json` — renaming it would have broken the
  live install without a redeploy). Every other map gets its own file
  (`material-shaders.<key>.json`) instead of the plan's "merge two files" idea — simpler, and
  correct because only one map's scenes are ever loaded during a given raid, so there is nothing to
  merge. `_sidecar` became `_sidecarByMap : Dictionary<string, Dictionary<string, SidecarEntry>>`,
  loaded lazily per map key.
- **`SpawnInfiltrationRepairs.cs`** — `InfiltrationName` const removed; reads
  `ArenaMapRaid.CurrentMap.InfiltrationName` at the point `SpawnPointsCollection.CreateFromScene`
  fires. Equator's value is unchanged (`"ArenaEquator"`); Bay5's is `"ArenaBay5"`.
- **`MapBorderRepairs.cs`** — Item 1 (TeamFight LevelBorder) generalized: one `MapState` (border
  list + its own `RemoveTeamFightBorders` `ConfigEntry<bool>`) per registered map, config section
  `"Map (<key>)"` (Equator's is therefore still exactly `"Map (arena_equator)"`). Item 2 (the narrow
  name-based safety net for the *pre-fix-enabled-state* deployed bundle) is **left Equator-only on
  purpose**, per critic amendment #2 and the plan: it still gates on
  `scene.name == ArenaEquatorLocation.LightingSceneName` directly, never generalized to Bay5 — a
  freshly-built map goes through the already-fixed `m_Enabled`-aware importer from the start and
  should never need it (Bay5's own disabled-component surface is ~10-40x Equator's, with confirmed
  name-pattern counter-examples per the scout data, so a name-based net could never safely cover it
  anyway).
- **`ArenaLightingRepairs.cs`** — per critic instruction: Equator keeps its exact config section
  name `"Lighting (arena_equator)"` and its existing tuned defaults (0.5/0.5/0.2/0.1/0/0.2); Bay5
  gets `"Lighting (arena_bay5)"` with the **same** defaults (no Bay5-specific tuning exists yet — a
  playtest-based re-tune is explicitly future work, same as the plan flagged). Every captured
  per-raid baseline (LevelSettings colors, per-Light baselines, fill-light GameObjects, the
  ambient-fallback-written flag) is now a `MapState` keyed per map instead of one unkeyed instance.
- **`LightingDiagnosticProbe.cs`** — diagnostic-only; made map-aware the same way (stateless
  `MapForScriptsScene` resolution for install/reset, `ArenaMapRaid.CurrentMap` for the probe body).
- **Fully generic already, mechanical rename only** — `SpawnColliderRepairs.cs`,
  `SpawnPlacementRepairs.cs`, `EnvironmentRepairs.cs`, `BotsDataRepairs.cs`,
  `EffectsControllerRepairs.cs` (5 guard classes), `SpatialAudioRepairs.cs` (4 patch classes),
  `PerfectCullingSkip.cs`, `SceneDiagnostics.cs` — confirmed by direct re-read, matches the plan's
  §1.2.8 classification exactly.

## 4) Server (`ArenaMapPort.Server`)

- **`ArenaEquatorRegistration.cs` → `ArenaMapRegistration.cs`** (renamed). `EnsureAvailable`,
  `Register`, `BuildLocation`, `ApplyLocationConfig`, `ApplyLocale` all now take an
  `ArenaMapDescriptor map` parameter instead of reading class-level Equator-only consts.
  `TemplateKey = "factory4_day"` stays a shared constant (the loot-multiplier/ScavRaidTimeSettings
  donor for every map, not itself Equator-specific — unchanged from the original file).
- **`ArenaMapPortMod.OnLoadAsync`** — loops `ArenaMapRegistry.All`; each map's
  read-base.json/EnsureAvailable/BuildLocation/ApplyLocationConfig/Register sequence is wrapped in
  its own `try/catch` so one map's failure (missing/bad `base.json`) logs a clear per-map error and
  the loop continues to the next map, rather than aborting the whole mod's `OnLoadAsync` (this is
  the actual, load-bearing behavior change the multi-map loop introduces — see §7 for how it's
  tested). Equator reads from `Path.Combine(modDir, "db", "base.json")` exactly as before (via
  `ArenaMapRegistry.Equator.DbBaseJsonPath == "base.json"`); Bay5 would read from
  `db/arena_bay5/base.json`, which does not exist yet this pass — its iteration logs
  `"[ArenaMapPort] arena_bay5: could not load ...db\arena_bay5\base.json — location NOT enabled"`
  and moves on. Locale-transformer registration stays a single loop over
  `localeTable.Global × ArenaMapRegistry.All`, outside the per-map try/catch (matches the plan's own
  pseudocode; harmless to register a locale transformer for a map that failed to register in
  `LocationTable`, since nothing reads that locale entry unless the map itself is selectable).
- **`ArenaMapPort.Server.csproj`** — confirmed already fully map-generic (`db\**\*.json` recursive
  glob, both the `<Content>` item and the `DeployToDist` target's `ArenaMapPortDbFiles` item) — **no
  change needed**, matches the plan's own claim, re-verified by reading the file this pass.

## 5) Server tests (`ArenaMapPort.Server.Tests`)

- **`LocationRegistrationTests.cs`** — every case that used to be a bare `[Fact]` against
  `ArenaEquatorRegistration`'s own constants is now a `[Theory]` + `[MemberData(nameof(AllMaps))]`
  over `ArenaMapRegistry.All` (`AllMaps()` is `public static` so `MapDescriptorConsistencyTests.cs`
  can reuse it via `MemberType`). Two new, non-parameterized facts cover the actual new behavior the
  multi-map loop introduces: `EnsureAvailable_DoesNotThrow_ForOneMap_WhenADifferentMapIsAlreadyRegistered`
  and `Register_BothMaps_Succeed_Independently` — both exercise `ArenaMapRegistration` directly
  (the pure, DI-free layer `OnLoadAsync`'s loop is built on) rather than `OnLoadAsync` itself, since
  standing up `OnLoadAsync`'s full DI surface (`ICloner`/`JsonUtil`/`ISptLogger`/`LocaleTable`/
  `LocationConfig`) with mocks was judged not worth the complexity for what these two facts already
  prove: one map's presence in `LocationTable` never blocks a *different* map's own registration —
  the exact invariant the per-map `try/catch` loop depends on for its isolation to mean anything. See
  §9 for this as an explicit open question rather than a silent gap.
- **New file `MapDescriptorConsistencyTests.cs`** — `Equator_Descriptor_MatchesArenaEquatorLocationConsts`
  (the plan's requested drift guard, §2 above), `Equator_BundleFileNames_MatchThePreGeneralizationLiterals`,
  `Equator_DbBaseJsonPath_IsTheLegacyUnnestedPath`, `Registry_HasNoDuplicateKeysOrIdFields`,
  `IdField_Is24LowercaseHexChars` (parameterized), `EveryMap_ScriptsSceneIsFirstInAllSceneNames`
  (parameterized).
- **48/48 tests pass** (`dotnet test ArenaMapPort.Server.Tests -c Release`), up from the
  pre-generalization suite's count — every case that used to run once for Equator now also runs for
  Bay5, plus the new facts above.

## 6) Editor (`UnityProject/Assets/ArenaMapPort/Editor/**`)

Cannot reference `ArenaMapPort.Shared` (separate assembly domain) — reads a
`JsonUtility`-compatible mirror instead.

- **New `ArenaMapDescriptorData` class, added to `ArenaPaths.cs`** — `[Serializable]`, flat
  snake_case public fields matching `tools/maps/<key>.json` 1:1 (JsonUtility supports only public
  fields and primitive-array values, no dictionaries — this shape is deliberately flat for that
  reason, per the plan's own note). `ArenaPaths.Map` is a lazily-loaded, cached singleton: reads
  `-mapDescriptor <path>` (same `GetArg` convention every other flag already uses) and
  `JsonUtility.FromJson`s it; if the flag is absent, falls back to `EquatorDefault()`, an in-memory
  descriptor with Equator's exact literal values — so an unparameterized `-executeMethod` invocation
  is behaviorally identical to before this change. Bundle served-key/asset-name/filename naming is
  derived by convention from `Map.key` (`<key>_scenes.bundle`, `<key>.scenespreset.asset`, etc.),
  never stored as a separate JSON field — mirrors the same convention `tools/server/make_base_json.py`
  and `tools/preset/patch_preset.py` now use (§8), so the three places can never drift from each
  other by construction.
- **`BuildBundles.cs`** — the 3 literals the plan flagged as the highest-severity Editor blocker
  (`"arena_equator.asset"` for the NavMesh asset path, `"arena_equator_scenes.bundle"`/
  `"arena_equator_navmesh.bundle"` for the two `AssetBundleBuild.assetBundleName`s) now read
  `ArenaPaths.Map.key`/`ArenaPaths.Map.ScenesBundleFileName`/`ArenaPaths.Map.NavMeshBundleFileName`.
  The Scripts-scene save path (`ArenaPaths.ScriptsSceneName` → `ArenaPaths.Map.scripts_scene_name`)
  was also switched — found by grepping for every remaining `ArenaPaths.ScriptsSceneName` use after
  the first pass, not assumed.
- **`AuthorScriptsScene.cs`** — `spawnPoint.Infiltration = "ArenaEquator"` →
  `ArenaPaths.Map.infiltration_name`; the build-summary log line's scene name too.
- **`VerifyPatchedBundleLoads.cs`** — default `-scenesBundle` and every "does the Scripts scene
  appear among the bundle's scene paths" check now read `ArenaPaths.Map.ScenesBundleFileName`/
  `ArenaPaths.Map.scripts_scene_name`.
- **`BakeNavMesh.cs`** — left as-is per the plan's own explicit allowance: only `DiagCollectSources`
  (diagnostic-only, not in the real `Run()`/`Bake()` pipeline `BuildBundles.cs` actually calls)
  hardcodes Equator scene names; the real bake path (`CollectSources(null, ...)`, reads whatever
  scenes are currently open) was already confirmed fully map-generic.
- **Confirmed no change needed** (re-read this pass, not just carried from the plan):
  `SceneAssembler.cs`, `MeshImporter.cs`, `TextureImportDriver.cs`, `StandinShaderGenerator.cs`,
  `MaterialImporter.cs` (its sidecar write is already `<outputDir>/material-shaders.json`, fully
  path-parameterized), `GameRefsPostprocessor.cs`, `ArenaPackage.cs`.
- **Not verified by an actual Unity compile this pass.** Every touched file's braces balance
  (mechanically checked) and every changed call site was traced by hand against
  `UnityEngine`/`UnityEditor` APIs already in use elsewhere in the same files, but no Editor batch-
  mode build was run — the hard rules restrict Unity/build access to one agent at a time and this
  task's own verification checklist does not list an Editor compile step. **Open item for whoever
  next opens this project in the Unity Editor: let it recompile once and check the Console for
  errors before running `build_all.ps1` for real.**

## 7) Python tooling

- **`tools/maps/equator.json` / `tools/maps/bay5.json`** — new directory, the canonical descriptor
  both the Editor (§6) and these scripts load. Equator's values are the exact pre-generalization
  literals (verified by regenerating `db/base.json` from it and diffing — see below).
- **`tools/server/make_base_json.py`** — `OVERRIDES`/`DEFAULT_OUT` (both previously Equator-only
  literals with no override) became `overrides_for(map_data)`/`--map`+`--out` (default:
  `ArenaMapPort.Server/db/<map's db_base_json_path>`). **Verified, not just reasoned about**: ran
  `python tools/server/make_base_json.py --out <tmp>` (default `--map`, i.e. no flag — equator.json)
  and diffed the output against the live `ArenaMapPort.Server/db/base.json` — **byte-identical**.
  Also ran it with `--map tools/maps/bay5.json --out <tmp>` and spot-checked the result (`Id`,
  `_Id`, `Name`, `Scene.{path,rcid}`, `IconX/Y` all correct for Bay5). Neither run touched the real
  `db/base.json` (both used `--out` pointed at a temp file).
- **`tools/preset/patch_preset.py`** — `SCENE_BUNDLE_KEY`/`PRESET_BUNDLE_KEY`/`PRESET_ASSET_NAME`/
  `SCENE_TABLE`/`SERVER_NAME`/`ACTIVE_SCENE_NAME`/`PRESET_M_NAME` are now defaults, overwritten by
  `apply_map(load_map(args.map))` at the top of `main()`; `--out`'s default becomes
  `work/build/preset/<key>_preset.bundle`. **Not actually re-run against the real donor bundle** —
  doing so would write into `work/build/preset/`, the exact intermediate output the currently-
  deployed `arena_equator_preset.bundle` was staged from; re-running it was judged an unnecessary
  risk against the "never rebuild Equator" hard rule for a file that is otherwise fully verifiable
  statically. Verified instead: `py_compile` passes, and a direct Python import + `apply_map(equator
  descriptor)` reproduces every pre-generalization constant's exact value (`SCENE_BUNDLE_KEY ==
  "arena_equator/arena_equator_scenes.bundle"`, etc. — all 7 checked).
- **`tools/validate/validate_preset.py`** — same `--map`/`apply_map()` treatment for
  `SCENE_BUNDLE_KEY`/`PRESET_ASSET_NAME`/`SCENE_TABLE`/`SERVER_NAME`/`ACTIVE_SCENE_NAME`/
  `DEFAULT_BUNDLE`. **Re-run against the real, currently-deployed Equator bundles** (before AND
  after this file's own edit, to bracket the change): `python tools/validate/validate_preset.py
  --scenes-bundle dist/.../arena_equator_scenes.bundle --bundle dist/.../arena_equator_preset.bundle
  --allow-missing-scenes` → `OK — all checks passed` both times, identical output.
- **`tools/validate/validate_bundle_roundtrip.py`** — the one genuinely map-specific literal this
  1137-line validator's logic contains (`"Arena_Equator_Scripts"`, special-cased inside
  `validate_editor_verify`'s per-scene loop for the 5 SS D EFT-MonoBehaviour checks) is now a
  `scripts_scene_name` parameter / `--scripts-scene-name` CLI flag (default: the same literal). Every
  other default in this file (`DEFAULT_PACKAGE`/`_SCENES_BUNDLE`/`_NAVMESH_BUNDLE`/etc.) was already
  fully overridable via existing flags and is not itself read by any hardcoded-per-map logic, so was
  left alone rather than adding an unused `--map` flag. **Re-run before AND after** against the real
  deployed bundles + `work/extract/equator` + `work/build/full`'s own `material-shaders.json`/
  `editor_verify.json` (the actual inputs the currently-deployed bundle was built from — the bare
  `DEFAULT_PACKAGE` default points at the S1 slice, not this full build, so it had to be passed
  explicitly either way): `OK — all checks passed (1 non-blocking warning)`, identical before/after.
- **`tools/validate/validate_extract.py`** — **not modified this pass.** Its `EXPECTED_DISABLED_COUNTS`
  hardcoded truth table is Equator-specific by design (critic amendment #3 explicitly scopes the
  "validate per imported scene against Arena" ask to *Bay5's own future extraction*, not this
  refactor pass — there is no Bay5 extraction to validate against yet). Converting it from a literal
  table to the "recompute fresh from a second independent read of Arena's own level files"
  self-consistency check the plan describes is real, non-trivial work best done alongside Bay5's
  actual extraction (so it can be validated against real data), not blind this pass. Confirmed
  unaffected: ran `validate_extract.py --package work/extract/equator --skip-reproducibility`
  unmodified — still `OK`, same 2 pre-existing non-blocking warnings as before.
- **`tools/extract/extract_equator.py`** — **not modified this pass, deliberately.** The plan's
  recommended option (a) — a new `extract_map.py --map <descriptor>` with `extract_equator.py`
  frozen as an argument-free wrapper around it — is a substantial rewrite of a 1066-line script this
  project's own docs call out as byte-reproducibility-critical for Equator
  (`docs/SPEC.md:226-227`), and there is no Bay5 extraction happening this pass to validate a
  generalized version against. Touching it now, untestable, was judged higher risk than deferring
  it. **Open item, see §9.**
- **`tools/bundle/patch_monoscript_identity.py`** — confirmed already map-agnostic (operates on
  whatever bundle path it's given; Equator strings only appear in doc-comment examples) — no change,
  matches the plan.

## 8) Build orchestration / deploy

- **`tools/build_all.ps1`** — new `-MapKey` (default `"arena_equator"`) and `-MapDescriptor`
  (default: `tools/maps/<MapKey-without-"arena_"-prefix>.json`, i.e. `tools/maps/equator.json` for
  the default `-MapKey`). Passed to `BuildBundles.Run` as `-mapDescriptor`; the two bundle filenames
  step 4's validators check (`arena_equator_scenes.bundle`/`arena_equator_navmesh.bundle`) are now
  `"${MapKey}_scenes.bundle"`/`"${MapKey}_navmesh.bundle"`; `validate_preset.py` additionally gets
  `--map $MapDescriptor`; `validate_bundle_roundtrip.py` gets `--scripts-scene-name`, read out of
  the descriptor JSON via `ConvertFrom-Json` rather than duplicated as a second PowerShell literal.
  Parse-checked with `[scriptblock]::Create(...)` (no execution — a real run needs Unity + the
  extraction package, out of scope this pass); every default with `-MapKey` omitted resolves to the
  exact pre-generalization value.
- **`ArenaMapPort.Client.csproj`**'s `ArenaMapPortStageWpOutputs` target — **not changed this pass**.
  The plan calls for a second, parallel 3-line `ItemGroup` once Bay5's own `work/build/full`-
  equivalent outputs exist; adding one now would either be dead code (no Bay5 outputs to stage) or,
  worse, a `<Error>` block that fires on every Equator-only build once Bay5 is "expected" but not
  built. Left as the exact 3-file Equator-only list until Bay5 has something to stage — see §9.
- **`ArenaMapPort.Server.csproj`** — confirmed no change needed (§4).
- **`tools/deploy-to-spt.ps1`** — confirmed no change needed: copies the whole
  `dist/BepInEx/plugins/ArenaMapPort/**` and `dist/SPT_Runtime/user/mods/ArenaMapPort/**` trees
  recursively, with no per-map logic anywhere in the script (re-read in full this pass).

## 9) Open questions / follow-up work

1. **Unity Editor compile not verified this session** (§6) — recompile in the Editor and check the
   Console before the next real `build_all.ps1` run.
2. **`tools/validate/validate_infiltration_name.py`** (the plan's WP6-style cross-assembly
   drift-checker between `AuthorScriptsScene.cs`'s authored `Infiltration` string and
   `ArenaMapPort.Client`'s compiled `InfiltrationName` value) was **not built this pass** — there is
   no built Bay5 bundle to check yet, and Equator's own two literals were confirmed to already match
   by direct code inspection. Build this alongside Bay5's actual extraction/build pass.
3. **`extract_equator.py` generalization deferred** (§7) — needed before Bay5 can actually be
   extracted; recommend the plan's option (a) (`extract_map.py` + a frozen `extract_equator.py`
   wrapper) at that time, informed by real Bay5 extraction attempts rather than done blind.
4. **`validate_extract.py`'s `EXPECTED_DISABLED_COUNTS`** (§7) generalization deferred to the same
   future pass, for the same reason (critic amendment #3 scopes it to Bay5's own extraction).
5. **`ArenaMapPort.Client.csproj`'s `ArenaMapPortStageWpOutputs` target** (§8) needs its second,
   Bay5-specific `ItemGroup` added once Bay5 has real `work/build/full`-equivalent outputs to stage
   — not before, per the plan's own "an explicit list keeps the 'exactly N files' check meaningful"
   reasoning.
6. **`OnLoadAsync`'s per-map try/catch isolation** is proven at the `ArenaMapRegistration` layer
   (§5) but not end-to-end through `ArenaMapPortMod.OnLoadAsync` itself (would need DI-mocking
   `ICloner`/`JsonUtil`/`ISptLogger`/`LocaleTable`/`LocationConfig`, or a live server run once Bay5
   has a `base.json` to deliberately corrupt for the test). The live-server case (Equator registers
   normally, Bay5 logs a clear "could not load db/arena_bay5/base.json" error and is skipped) is the
   actual state right now and should be visually confirmed against the server log the first time
   this build is deployed and started.
7. **Bay5's own scene-load order** (`ArenaMapRegistry.Bay5.AllSceneNames`) is still exactly the
   plan's own flagged-as-unvalidated proposal — re-derive it against `docs/SPEC.md` §B.WP3 step 3's
   ordering rule once Bay5 is actually authored, per the plan's own R8 risk entry.

## 10) Verification run (this pass)

| Check | Command | Result |
|---|---|---|
| Client builds | `dotnet build ArenaMapPort.Client -c Release` | 0 warnings, 0 errors |
| Server builds | `dotnet build ArenaMapPort.Server -c Release` | 0 warnings, 0 errors |
| Server tests | `dotnet test ArenaMapPort.Server.Tests -c Release` | 48/48 passed |
| Server rebuilt last | `dotnet build ArenaMapPort.Server -c Release` (after test) | 0 warnings, 0 errors |
| Python syntax | `python -m py_compile` on all 4 changed .py files | all OK |
| `validate_preset.py` vs live Equator bundles | see §7 | `OK`, unchanged before/after |
| `validate_bundle_roundtrip.py` vs live Equator bundles+package | see §7 | `OK` (1 pre-existing non-blocking warning), unchanged before/after |
| `validate_extract.py` vs live Equator package (unmodified file) | see §7 | `OK` (2 pre-existing non-blocking warnings) |
| `build_all.ps1` syntax | `[scriptblock]::Create(...)` parse-only | parses cleanly |
| Equator dist bundle hashes | `sha256sum dist/BepInEx/plugins/ArenaMapPort/bundles/*.bundle` | all 3 match the recorded baseline exactly (see table below) |
| `dist/.../db/base.json` content | `sha256sum` vs `ArenaMapPort.Server/db/base.json` | identical |
| `make_base_json.py` default output | diffed a fresh `--out <tmp>` run against the live `db/base.json` | byte-identical |

Equator bundle hashes (unchanged from the baseline the task specified, confirmed both before any
edit in this pass and again after the final Client Release rebuild):

| File | SHA-256 |
|---|---|
| `arena_equator_scenes.bundle` (308,912,270 B) | `adb8c5916da001f068fe345d3c18d167ec74357d003b9853ff3e16df64fb1035` |
| `arena_equator_navmesh.bundle` (72,842 B) | `25d6165c3c2535e4ae3ae77f4d08b4a3963f9addcfb275b546543d51fea10f6b` |
| `arena_equator_preset.bundle` (6,891 B) | `f12cf0bfbff60e5ec299f211253656d87650d63fb8a3a899b957bf4ee06dfe36` |

## Follow-up 2026-10-17: Equator's legacy sidecar name retired

The one exception this pass introduced (Equator keeps `material-shaders.json`, every other map gets
`material-shaders.<key>.json`) is gone: since 2026-10-17 `ShaderRebind.SidecarFileName` returns `material-shaders.<key>.json`
for every map, the csproj stages Equator's build output as `material-shaders.arena_equator.json`, and `make_release_zips.py` /
`validate_distribution.py` / the tests expect exactly that set. The reason the exception existed (an already deployed, already
verified file that a rename would have broken without a redeploy) no longer applies: the mod is not on Forge yet and every
install is redeployed anyway. Ordinary deployment never deletes, so `tools/deploy-to-spt.ps1 -RemoveLegacyShaderSidecar`
(modeled on `-RemoveLegacyEquatorDb`) backs up and removes the old file after the renamed file and the client DLL match dist.
The published 0.2.1 package (built from `d26b7fd`) still carries the old name; see `docs/dist-manifest.md`.
