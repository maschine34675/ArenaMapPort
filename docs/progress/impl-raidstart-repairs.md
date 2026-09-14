# impl-raidstart-repairs — raid-start crash fixes for arena_equator

Implements the repairs identified by the LOAD-CHAIN AUDIT (`audit-load-chain.md`) and REFERENCE
MAPPING (`audit-reference-repairs.md`) so the 2026-09-14 test's abort
(`SpatialAudioSystem.Initialize` NRE in `TarkovApplication.LoadMapAndData`) and the predicted
follow-on crashes are fixed. All code changes are in `ArenaMapPort.Client`; the Unity pipeline was
not touched, per the task's own instruction.

## New files

- `ArenaMapPort.Client/SpatialAudioRepairs.cs` — item (1): the observed crash + its two follow-ons.
- `ArenaMapPort.Client/EnvironmentRepairs.cs` — item (2): `EnvironmentManager`/`ObservedCullingManager`.
- `ArenaMapPort.Client/EffectsControllerRepairs.cs` — item (3): `FrostbiteEffect`.
- `ArenaMapPort.Client/BotsDataRepairs.cs` — new findings from `audit-reference-repairs.md`:
  `AIVoxelesData.VoxelsList` null + missing `AIStationaryController`.

## Modified files

- `ArenaMapPort.Client/HarmonyPatches.cs` — 7 new `InstallXxx` methods wired into `InstallAll`,
  following the file's existing `AccessTools.Method` + `Verify()` convention (one exception: the
  `ProcessSourceOcclusion` guard resolves its 3 overloads by reflection-over-name, see below, rather
  than 3 hard-coded parameter-type arrays — more robust against a future overload change, not less).
- `ArenaMapPort.Client/ShaderRebind.cs` — item (4): `RunPass` now logs an immediate, synchronous
  per-scene summary (`[ArenaMapPort][RebindShaders] <scene>: pass complete — N/M rebound`) instead of
  only the 3s-delayed `ReportSummary()`, so a crash in the first few seconds of a raid still leaves
  evidence the rebind pass ran. `Rebind` now returns `bool` (whether it performed the rebind) to
  support the per-pass count; the 3s sweep is unchanged.
- `THIRD-PARTY-NOTICES.md` — new "ManimalIcebreaker (impl-raidstart-repairs)" section attributing the
  4 patches adapted from `RaidFixPatches.cs`, and a note in the existing InterchangeRework section that
  `HarmonyPatches.cs`'s new wiring is original (the adapted logic itself is attributed per-file above).

## Fixes, in raid-load order

### 1. `SpatialAudioSystem.Initialize` NRE (the observed, confirmed crash)

**Decision: SKIP `Initialize`'s body on our map**, not "fix `OcclusionSettings` and let the real init
run against BSG's `empty_info` fallback". Evidence (decompiled, `D:/SPT41/Development/_decompiled`):

- Fixing only `OcclusionSettings` (`SpatialAudioSystem.cs:184-195` `method_0`) does not make
  `Initialize` safe to complete: the very next steps construct a `SpatialAudioDataLoader` and call
  `LoadDataAsync` (`SpatialAudioDataLoader.cs:83-107`), which reads a bake file from
  `Path.Combine(Application.streamingAssetsPath, _locationInfo.relativeBakeDataPath)` via a
  coroutine (`AudioBakeDataSerializer.ReadAndWriteToDataContainerAsync`) whose behaviour for the
  `empty_info` bake path we do not ship and have never inspected is untested — and this sits on the
  exact same unguarded, no-try/catch await chain (`TarkovApplication.cs:2779-2790`) that produced the
  original crash. Any surprise there reproduces "raid aborts", just relocated and harder to diagnose.
- Every consumer that matters gates on the static `Initialized` flag already (`CanUpdate`,
  `SpatialAudioSystem.cs:107-117`, used by `Update()`/`LateUpdate()`) — leaving `Initialized=false`
  makes those automatically safe no-ops. The one exception, `ProcessSourceOcclusion` (all 3
  overloads), gets its own guard (below).
- Matches ManimalIcebreaker's own production-tested fallback branch for a from-scratch scene set with
  no acoustics sidecar data (same 0.16.9.5 build).

Implementation (`SpatialAudioRepairs.cs`):

- `SpatialAudioInitSkipPrefix` — prefix on `SpatialAudioSystem.Initialize`, sets
  `__result = Task.CompletedTask; return false;` when `ArenaEquatorRaid.IsLoaded()` (same
  async-Task-state-machine pattern already proven by `PerfectCullingSkipPrefix`).
- `SpatialAudioCreateDefaultsPostfix` — postfix on `CreateSpatialAudioSystem`, assigns
  `ScriptableObject.CreateInstance<AudioOcclusionSettings>()`/`new SpatialAudioPoolsConfig()` if null.
  Defense-in-depth per the task's literal ask — a no-op in practice once the skip prefix is in place
  (Initialize's body, where those fields would be read, never runs on our map).
- `SpatialAudioOcclusionGuard` — prefix (`!Initialized` → `__result=-1; return false`) + finalizer
  (swallow any exception, `__result=-1`, log first 5) on every public instance overload of
  `ProcessSourceOcclusion`, found by reflection (`GetMethods().Where(m.Name==...)`, 3 overloads in
  this build) rather than 3 hard-coded signatures. **Gating**: the prefix is intentionally NOT
  map-gated (matches upstream) — it only branches on `Initialized`, which is a strict improvement on
  every map, not just ours (see the doc comment in the file for the full argument); the finalizer IS
  map-gated, since swallowing an exception is the riskier half and must not mask a new bug elsewhere.

### 2. `EnvironmentManager`/`ObservedCullingManager` missing (next abort after #1, at `Player.Init`)

`EnvironmentManager.Instance` is dereferenced unguarded at `EFT/Player.cs:28251-28252`, for every
player. Fix (`EnvironmentRepairs.cs`): `EnsureEnvironmentPrefix` on `Player.Init` (single anchor — no
Fika-specific path to also cover for this spike), creates a bare `EnvironmentManager` and a bare
`ObservedCullingManager` if missing, gated on `ArenaEquatorRaid.IsLoaded()`. `ObservedCullingManager`
was independently verified safe to create bare (`ObservedCullingManager.cs`: `Awake()` only
allocates its own list and hooks `Camera.onPreCull`; `Update()` loops 0 objects when none registered)
even though every existing call site already null-guards it — creating it restores real observed-body
culling instead of losing it, per the task brief's explicit item (2).

### 3. `EffectsController`/`FrostbiteEffect` NRE (spams + breaks screen effects, does not abort)

Cam2 (our camera fallback, since `LevelSettings.CameraPrefab` is null) predates `FrostbiteEffect`.
Fix (`EffectsControllerRepairs.cs`): `EffectsControllerFrostbitePrefix` on `EffectsController.Awake`
adds a bare `FrostbiteEffect` if missing, gated on `ArenaEquatorRaid.IsLoaded()`. Verified safe:
`FrostbiteEffect.Awake()` only does `GetComponent<SSAAPropagator>()`, and `EffectsController.Init()`
disables it again on the very next line once it stops NREing.

### 4. New findings (audit-reference-repairs.md) — both inside `BotsController.Init`, unconditional

- `AIVoxelesData.RestoreData`'s `VoxelsList` field has no initializer, and `AICoversData.CreateOrFind`
  always `AddComponent`s a fresh `AIVoxelesData` when the scene has none (our case) — a freshly
  `AddComponent`'d MonoBehaviour gets no scene-deserialization pass, so the field stays null → NRE on
  `VoxelsList.Count(...)`. Fix (`BotsDataRepairs.cs`): `AIVoxelesDataGuardPrefix` on
  `AIVoxelesData.RestoreData` ensures a non-null empty list before the original runs — verified the
  rest of the method (`VoxelesArray = new NavGraphVoxelSimple[0,0,0]`, empty foreach loops,
  `FindObjectsProxy.FindUnityObjectsOfType<NavMeshDoorLink>()` on our door-free scenes) then completes
  cleanly with zero voxels/cover data, the correct outcome for a bots-out-of-scope spike. This is an
  original fix, not a port — simpler than Icebreaker's own `Patch_CoversRestore` (a broad
  exception-swallowing Finalizer that reconstructs *partial* recovered bake data; we have none to
  preserve, so preventing the exception outright is both simpler and sufficient).
- `BotsController.Init` calls `FindObjectsProxy.FindUnityObjectOfType<AIStationaryController>()` with
  no null check, then `.Init(...)` on the result — we author no stationary weapons, so this always
  NREs. Fix: `AIStationaryControllerEnsurePrefix`, an additional prefix on `BotsController.Init`
  alongside §F.2's existing `NavMeshInstallerPrefix` (Harmony supports multiple independent prefixes
  per target), creates a bare `AIStationaryController` if `FindObjectOfType` finds none. Verified
  `AIStationaryController.Init` is safe on an empty instance: `Weapons` has an inline
  `= Array.Empty<StationaryWeaponLink>()` initializer in this SPT build (the one place our decompile
  diverges from Icebreaker's own, per the audit — no Finalizer backstop needed).

### Shader-rebind diagnostic gap (item 4, not a functional bug)

`ShaderRebind.RunPass` already ran correctly before the abort (registered in `Plugin.Awake()`, long
before any scene load) — only its summary log was deferred 3s past when the raid aborted. Fixed: an
immediate, synchronous per-scene log line at the end of every `RunPass`, matching
InterchangeRework's own pattern; the 3s `ReportSummary()` sweep (for stragglers/unresolved shaders)
is unchanged.

### Not touched (per the audits' own conclusions)

- `LocalGameMatching`'s `InvalidOperationException` — verified as a consequence of #1's abnormal abort
  forcing `ScreenManager.TryCloseScreen`'s cancellation branch; not an independent scene-content bug.
  Should stop reproducing once #1 lets the raid proceed normally — flagged for re-test, not fixed.
- The >150 `_Cube`/`_ParticleTex` texture-dimension errors and the duplicate-`LODGroup` warnings —
  pipeline/import defects (WP1/WP2's extractor), not raid-init crashes a Harmony patch can fix; no
  reference mod carries a runtime patch for either. Out of scope for this task (the pipeline is
  explicitly another agent's work).

## Build

`dotnet build -c Release` in `ArenaMapPort.Client` — 0 warnings, 0 errors, DLL staged to
`dist/BepInEx/plugins/ArenaMapPort/maschine-ArenaMapPort.Client.dll` (confirmed via the
`ArenaMapPortDistCopy`/`ArenaMapPortStageWpOutputs` MSBuild targets' own log output). Ran clean twice
(once after the initial implementation, once after a doc-comment-only follow-up edit).

## fix-raidstart follow-up (re-review findings applied)

A review pass re-verified this implementation against the decompiled sources and found one real gap
and one doc-only defect; both fixed — see `docs/progress/fix-raidstart.md` for the full writeup.

- **`SpatialAudioSystem.ProcessInteractiveObjectOcclusion` was a second, unguarded NRE entry point.**
  `SpatialAudioOcclusionGuard` (above) only covers `ProcessSourceOcclusion` by exact name; this sibling
  method (called from `WorldInteractiveObject.ProcessOcclusion`/`WindowBreaker`, i.e. doors and
  breakable glass) also dereferences state only set inside the skipped `Initialize` body. Dormant this
  milestone (the pipeline authors no interactive objects yet) but would NRE the instant one is added.
  Fixed: new `SpatialAudioInteractiveOcclusionGuard` (prefix + finalizer, void-method variant — no
  `__result` needed) in `SpatialAudioRepairs.cs`, wired via `InstallSpatialAudioInteractiveOcclusionGuard`
  in `HarmonyPatches.cs`. The doc comment's now-corrected "ONE entry point" claim above item 1's list is
  also fixed in `SpatialAudioRepairs.cs` itself.
- The 4 SHA-256 hashes in `docs/progress/impl-texture-dimensions.md`'s artifact table were each
  missing their final hex digit (63 chars instead of 64) — re-copied from a fresh `sha256sum` run.
  Cosmetic only; the real `dist/`/`work/build/full` artifacts were already correct.

## Not done

- **No in-game raid test was run this pass.** All fixes are evidence-based against the decompiled
  source and cross-checked against ManimalIcebreaker's own production-tested code for the same build,
  but the task only asked to build Release; an actual raid-start test on arena_equator (repeating the
  2026-09-14 test, watching for the predicted item-2/3/4 crashes in sequence disappearing one by one,
  and independently checking whether the `LocalGameMatching InvalidOperationException` still
  reproduces) is the natural next step and was not performed here.
