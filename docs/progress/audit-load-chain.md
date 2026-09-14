# Audit: load chain TarkovApplication.LoadMapAndData -> first raid seconds (label audit-load-chain)

Traced in `D:/SPT41/Development/_decompiled`. Reference (MIT, same EFT/SPT build 0.16.9.5):
`D:/sptmaps/_sources/ManimalIcebreaker-main/icebreaker-client/RaidFixPatches.cs` (+ `IcebreakerCameraDonor.cs`).
None of the fixes below exist yet in `ArenaMapPort.Client` (grep confirmed 2026-09-14).

## Crash chain, in raid-load order

### 1. SpatialAudioSystem.Initialize -> method_0 NRE (ABORTS raid) — given, confirmed
- `Audio.SpatialSystem/SpatialAudioSystem.cs:286-296` `CreateSpatialAudioSystem()` (hit because our
  scenes carry no `SpatialAudioSystem` component, so `MonoBehaviourSingleton<SpatialAudioSystem>.Instantiated`
  is false — see `EFT/TarkovApplication.cs:2787-2789`): `AddComponent<SpatialAudioSystem>()`, sets only
  `_locationInfo` (`Resources.Load<SpatialAudioLocationInfo>("SpatialAudioLocationInfo/empty_info")`).
  `OcclusionSettings` (ScriptableObject field) is **never assigned** -> stays null. `poolsConfig` is a plain
  `[Serializable]` class field with no initializer on the field itself, but Unity's serializer auto-`new()`s
  plain-serializable (non-UnityEngine.Object) fields on any instantiated object, so it comes up with its own
  class-default values (30/30/30/5) — not a crash source, ScriptableObject refs get no such treatment.
- `Audio.SpatialSystem/SpatialAudioSystem.cs:139-182` `Initialize()` calls `method_0()` at line 143
  (before anything else, incl. before `_containerData`/`_listenerTransform`/`_fastOccluder` are set).
- `Audio.SpatialSystem/SpatialAudioSystem.cs:184-195` `method_0()`: `OcclusionSettings.Apply(...)` (line 189)
  -> NRE, gated on `Singleton<ClientSettingsConfig>.Instance?.AudioSettings != null` (true; comes from the
  SPT backend config, not scene content).
- Call site: `EFT/TarkovApplication.cs:2779-2790`, inside `LoadMapAndData`, **outside every try/catch** in that
  method. The `Initialize()` Task faults before its first await, so the fault surfaces the instant it's awaited.
- Propagation: uncaught through `LoadMapAndData` -> caught by `LocalGameMatching`'s catch-all
  (`EFT/TarkovApplication.cs:2563-2567`) -> cancels the token -> `LocalGameMatching` rethrows
  `OperationCanceledException` at line 2575 -> raid aborts, `LocalGameCreate`/`GamePrepare` never reached.
- Fix: earliest safe hook = Harmony postfix on `SpatialAudioSystem.CreateSpatialAudioSystem` (static,
  `Audio.SpatialSystem/SpatialAudioSystem.cs:286`), gated on `ArenaEquatorRaid.IsLoaded()`, assign
  `spatialAudioSystem.OcclusionSettings = ScriptableObject.CreateInstance<AudioOcclusionSettings>()`
  (`Audio.SpatialSystem.Data/AudioOcclusionSettings.cs` — plain ScriptableObject, all nested settings classes
  self-default, safe blank instance) before `Initialize()` is awaited.

### 2. EnvironmentManager missing entirely (ABORTS raid, at first Player.Init) — new finding
- No scene GameObject carries `EnvironmentManager`/`EnvironmentManagerBase`
  (`EnvironmentManagerBase.cs:29-54`: `_instance` only gets set from that component's own `Awake()->Init()`).
  `EnvironmentManager.Instance` (`EFT.EnvironmentEffect/EnvironmentManager.cs:69`) is therefore `null` for the
  whole raid unless something creates one.
- **`EFT/Player.cs:28251-28252`, inside `Player.Init(...)`, unguarded:**
  ```
  Environment = EnvironmentManager.Instance.GetPlayerCurrentEnvironmentType(ProfileId);
  EnvironmentManager.Instance.OnPlayerEnvironmentChanged += SetEnvironment;
  ```
  Runs for **every** player, local and AI, the moment a player is created — this is the very next abort once
  #1 is fixed (`LocalPlayer.Create`/`Player.Init` is called right after `LoadMapAndData` succeeds).
- `EFT.NextObservedPlayer/ObservedPlayerAudioController.cs:373-374` (`Initialize(ObservedPlayerView)`),
  same unguarded pattern — fires the first time any bot/other player becomes observed (visible).
- `EFT.Vaulting/ObservedVaultingAudioController.cs:23-24` — same pattern, fires on first observed vault.
- `Audio.SpatialSystem/AudioRoomStorage.cs:360` — unguarded, only reachable after SpatialAudioSystem itself
  is fixed and doing occlusion work (secondary to #1/#2).
- `EffectsController.cs:640` — unguarded (`if (EnvironmentManager.Instance.Environment == ...)`); context not
  fully traced for call frequency, flagged for follow-up.
- Contrast (NOT a bug): `ObservedCullingManager` (`ObservedCullingManager.cs`) is missing too, but every call
  site (`ObservedCullingObject.cs:118,126,166`) is guarded by `Singleton<ObservedCullingManager>.Instantiated`
  — degrades to "no culling optimization", never crashes. Confirms EnvironmentManager is the one that matters.
- Fix: earliest safe hook = Harmony prefix on `Player.Init` (or on `GameWorld.OnGameStarted`/
  `BaseLocalGame.Run`, before any player is created), gated on `ArenaEquatorRaid.IsLoaded()`:
  `if (EnvironmentManagerBase.Instance == null) new GameObject("EnvironmentManager(ArenaFallback)").AddComponent<EnvironmentManager>();`
  Matches Icebreaker's `Patch_EnsureEnvironmentManager`/`Patch_EnsureEnvBeforeAnyPlayerInit`.

### 3. EffectsController.Init() -> FrostbiteEffect NRE (SPAMS + breaks screen effects, does NOT abort) — new finding, corroborated
- Root cause: our `LevelSettings.CameraPrefab` is null (task brief) -> `EFT.CameraControl/CameraManager.cs:322-331`
  `SetCameraFromSettings` falls to `SetCameraFromPrefab()` with no prefab -> line 338
  `UnityResourcesProxy.Load("Cam2")` (built-in fallback camera). This part is already null-safe/graceful.
- **`EffectsController.cs:1364-1365`, inside `Init()` (called from `Awake()` at line 1219):**
  ```
  _frostbiteEffect = base.gameObject.GetComponent<FrostbiteEffect>();
  _frostbiteEffect.enabled = false;   // NRE if Cam2 lacks FrostbiteEffect
  ```
  This is the *only* `GetComponent` in this ~90-line init routine without a `?? AddComponentCopy(...)`
  fallback — every sibling effect (DepthOfField, CC_FastVignette, CC_DoubleVision, CC_HueFocus,
  CC_RadialBlur, CC_Wiggle, MotionBlur, CC_Sharpen, ChromaticAberration, ThermalVision, lines 1294-1363) uses
  `base.gameObject.GetComponent<T>() ?? base.gameObject.AddComponentCopy(_effectsPrefab.GetComponent<T>())`.
  `_effectsPrefab` (`EffectsController.cs:1141`) is a field baked into EffectsController's own prefab data,
  not scene-provided, so it resolves fine regardless of our scenes.
- Independently confirmed for the *same* EFT/SPT build (0.16.9.5) by
  `ManimalIcebreaker-main/icebreaker-client/RaidFixPatches.cs:371-389` ("Cam2 predates FrostbiteEffect") and
  fixed there by adding a bare `FrostbiteEffect` component before `EffectsController.Awake` runs.
- Blast radius: `EffectsController.Awake()` (line 1217-1227) calls `Init()` **before** subscribing to
  `PlayerCameraController.OnPlayerCameraControllerCreated`/`OnPlayerCameraControllerDestroyed` (lines
  1221-1222) and before the sharpen-settings binding (line 1220) — since `Init()` throws partway through,
  none of those run either. Net effect: no hard abort (Unity's native Awake wrapper logs the NRE and keeps
  going — this is NOT on the LoadMapAndData async-Task chain like #1/#2), but the whole first-person screen
  effects system (blood, vignette, DOF, chromatic aberration, damage/hydration/pain accumulators, night/
  thermal vision wiring further down `Init()`) stays unwired for the entire raid, once per raid load.
- Trigger timing: fires synchronously during `UnityEngine.Object.Instantiate(cameraPrefab)`
  (`CameraManager.cs:345`), called from `CameraManager.SetCameraFromPrefab` <-
  `PlayerCameraController.Construct` (`EFT.CameraControl/PlayerCameraController.cs:158`) <-
  `PlayerCameraController.Create(player)` — i.e. right after local-player Init, during camera setup.
- Fix: earliest safe hook = Harmony prefix on `EffectsController.Awake`, gated on `ArenaEquatorRaid.IsLoaded()`:
  `if (__instance.GetComponent<FrostbiteEffect>() == null) __instance.gameObject.AddComponent<FrostbiteEffect>();`
  (matches Icebreaker's `Patch_EffectsControllerFrostbite`, which also notes `FrostbiteEffect.Awake` only
  caches a component ref, so a bare `AddComponent` is safe and `Init()` disables it again anyway).

### 4. InvalidOperationException "transition a task to a final state" — verified: consequence, not independent
- `EFT/TarkovApplication.cs:2568-2575` (`LocalGameMatching`'s cancellation branch, reached only because #1's
  exception hit the catch-all at line 2563): sets matching status, then `timeHasComeScreenController?.CloseScreen()`
  at line 2573.
- `CloseScreen()` -> `TryCloseScreen()` -> `EFT.UI.Screens/ScreenManager.cs:336-379` `TryCloseScreen(forced, order)`.
  Non-forced path creates `_closeAwaiter = new TaskCompletionSource<bool>()` (line 346), awaits
  `Task.WhenAny(CloseScreenInterruption(...), _closeAwaiter.Task)` (line 347), then — guarded only by a single
  `if (_closeAwaiter.Task.IsCompleted) return false;` check (line 348) — calls the non-`Try` `_closeAwaiter.SetResult(result)`
  (line 352). The `forced` branch (`CloseScreenForced`, lines 358-365) can independently complete the *same*
  `_closeAwaiter` via `SetResult(false)` (line 363) with its own, separately-timed `IsCompleted` check —
  i.e. two branches can each pass their own guard before either has completed the TCS, then both call
  `SetResult` on it. This is exactly the message's signature (double `SetResult` on one `TaskCompletionSource`).
  This code path only gets exercised because #1's abnormal, un-awaited abort forces `LocalGameMatching` down
  the cancellation/`CloseScreen()` route instead of the normal success path (`LocalGameCreate`, which never
  touches `TryCloseScreen` on this controller at all — the screen closes exactly once, via the ordinary
  matching-UI teardown).
- Verdict: **consequence, not a scene-content bug.** It is vanilla `ScreenManager` code with a pre-existing,
  narrow reentrancy gap that normal raid starts/aborts never hit; it should stop reproducing once #1/#2 let
  `LoadMapAndData`/`LocalGameMatching` complete or cancel through their ordinary paths. Not something our
  client repair patches should touch.

## Non-issues checked (no crash, degrade gracefully)
- **Weather/TOD/season**: `EFT/BaseLocalGame.cs:489-517` `InitWeather` is fully guarded
  (`WeatherController.Instance != null || MonoBehaviourSingleton<TODSkySimple>.Instance != null`) — with
  neither present (our scenes have none) the whole block is skipped. `EnvironmentManager.GetLongShadowCorrectionFactor`
  (`EnvironmentManager.cs:232-254`) also guards via `TODSkyProvider.IsAvailable`.
- **Exfiltration/transit/BTR points**: `CommonAssets.Scripts.Game/ExfiltrationController.cs:224`
  `LocationScene.GetAllObjects<ExfiltrationPoint>()` and `EFT/BaseLocalGame.cs:790-816` (`vmethod_6`) both
  handle a zero-length scene result fine (empty-array foreach/LINQ) — no exits/BTR configured is a gameplay
  gap (nobody can extract), not a crash.
- **Spawn points**: `EFT.Game.Spawning/SpawnPointsCollection.cs:152-170` `CreateFromScene` — with only 3
  `SpawnPointMarker`s and `location.SpawnPointParams` from the backend not matching, the mismatch path just
  logs errors (`IsValid`/`FixSpawns`) rather than crashing; too few spawn points is a gameplay/spam concern,
  not an NRE.
- **Camera prefab fallback itself**: `CameraManager.cs:322-344` (`SetCameraFromSettings`/`SetCameraFromPrefab`)
  and `GetComponent<VisorEffect/NightVision/HysteresisFilter/ThermalVision>` at lines 518-521 are fine — Cam2
  ships those (Icebreaker's own analysis, same build, only flags FrostbiteEffect/TOD_Camera/TOD_Scattering/
  PostProcessResources as gaps on Cam2, not the vision/visor components).

## Side finding: "[RebindShaders] Nx <name>" summary never logged — pipeline is fine, just never got to report
- `ArenaMapPort.Client/ShaderRebind.cs:70-81` `OnSceneLoaded` runs `RunPass(scene)` (line 79) **synchronously,
  immediately**, per owned scene — this already ran and already rebound stand-in materials before the abort.
- The `"[RebindShaders] Nx <name>"` lines only come from `ReportSummary()` (line 313), which is only called
  from the **3-second-delayed** `Sweeper` coroutine (`ScheduleSweep`/`Sweeper.RunAfter`, lines 104-122). Since
  #1 aborts the raid well under 3s after scene load, that coroutine's `WaitForSeconds(3f)` never elapses (and
  `OnSceneUnloaded` at line 83-102 destroys `_sweepHost` on scene teardown before it can). Not a bug: rebind
  ran, only its deferred log summary was preempted by the abort. No action needed beyond fixing #1.
- Unrelated, separate issue (not part of this crash chain, flagged only for the pipeline notes): the >150
  "Error assigning 2D texture to CUBE/3D texture" errors are texture-type import mismatches from the
  extractor (Cubemap/Texture3D sources imported as 2D), not a shader stand-in / scene-singleton problem.

## Priority order for fixes
1. SpatialAudioSystem.OcclusionSettings (aborts every raid, first thing hit)
2. EnvironmentManager (aborts every raid, next thing hit once #1 is fixed)
3. EffectsController/FrostbiteEffect (doesn't abort, but silently kills all screen effects every raid — fix
   before relying on any visual QA of hits/vision/stims)
4. No fix needed for the InvalidOperationException or the RebindShaders summary — both resolve once #1/#2 land.
