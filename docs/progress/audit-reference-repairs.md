# Audit: reference-repair applicability (Icebreaker / InterchangeRework / Labs)

Scope: read `RaidFixPatches.cs` (4663 lines) + `IcebreakerCameraDonor.cs` (475 lines) +
`IcebreakerAcoustics.cs` (958 lines, the class `Patch_SpatialAudioInit`/`Patch_EnsureEnvironmentManager`
call into) from ManimalIcebreaker-main; `MaterialShaderRebind.cs` from SPT-InterchangeRework-main;
`LabsBoilerCullingStrip.cs`/`LabsBoilerLocationSceneFix.cs`/`LabsBoilerAcoustics.cs` from
ManimalLabs-main. `ManimalLighthouse-master` was not opened (no license, excluded per workspace rule).
Every "applies" verdict below was checked against **our own decompiled sources**
(`D:/SPT41/Development/_decompiled`), not just against the donor mods' own comments.

## Headline finding

The 2026-09-14 test only proves the **first** blocker. Decompiling the exact call chain
(`TarkovApplication.cs:2715-2790`, `BotsController.cs:168-219`, `Player.cs:28251`) shows the raid
would hit **three more unconditional NREs in sequence**, all inside code paths that run on *every*
local raid regardless of bot count / weather / doors — none of these were previously in
`docs/SPEC.md` or `docs/reviews/SPEC-review-spt-runtime.md` except the one line about
`AICoversData.CreateOrFind`'s zero-`BotZone` case (already avoided by our authoring). Predicted
order, once the current blocker is fixed:

1. **`SpatialAudioSystem.Initialize` → `method_0()`** — `OcclusionSettings.Apply(...)` on a null
   `OcclusionSettings`. **Confirmed, this is the observed crash.** (`SpatialAudioSystem.cs:139-189`)
2. **`Player.cs:28251`**: `EnvironmentManager.Instance.GetPlayerCurrentEnvironmentType(...)` — no
   null check (unlike the two other `EnvironmentManager.Instance` call sites in the same file, which
   both null-check). We ship no `EnvironmentManager`. **Next blocker, not yet observed** (raid never
   reached `Player.Init` in this log).
3. **`BotsController.cs:173-174`**: `_coversData.RestoreData()` →
   `AIVoxelesData.RestoreData` (`AIVoxelesData.cs:52-57`) does
   `int num = VoxelsList.Count(...)` on `VoxelsList`, a **field with no inline initializer**
   (`AIVoxelesData.cs:32`, `public List<NavGraphVoxelSimple> VoxelsList;`) — null on any freshly
   `AddComponent`'d instance. `AICoversData.CreateOrFind` (`AICoversData.cs:345-352`) always
   `AddComponent`s a fresh `AIVoxelesData` when the scene has none, which is our case (cover/voxel
   data is explicitly out of scope, SPEC line 6). **New finding, not in any prior review.**
4. **`BotsController.cs:185-186`**: `StationaryWeapons = FindObjectsProxy.FindUnityObjectOfType<AIStationaryController>(); StationaryWeapons.Init(...)` —
   no null check on the find result. We ship no `AIStationaryController`. **New finding, not in any
   prior review.**
5. **`EffectsController.cs:1364-1365`** (reached once camera setup runs): `_frostbiteEffect =
   base.gameObject.GetComponent<FrostbiteEffect>(); _frostbiteEffect.enabled = false;` — unconditional
   dereference. Applies to us because our `LevelSettings.CameraPrefab = null` (SPEC §D) makes the
   game fall back to the same built-in "Cam2" camera Icebreaker's whole camera-donor story is built
   around, and Cam2 predates `FrostbiteEffect`.

Items 2-4 all live inside methods (`Player.Init`-region, `BotsController.Init`) that run
**unconditionally on every local raid**, independent of bot count/weather/doors — so "out of scope
for the spike" (SPEC line 6-7) does not mean these call sites are skipped; it only means we never
authored data to satisfy them. Recommend fixing 1 → re-test → expect 2, fix → re-test → expect 3,
fix → re-test → expect 4, in that order (each currently masks the next).

## Table

| Repair | Source | License | Applies to us? | Reason / adaptation |
|---|---|---|---|---|
| Skip `SpatialAudioSystem.Initialize` when unstaged | `RaidFixPatches.cs:36-51` (`Patch_SpatialAudioInit`) | MIT (danauraborealis) | **Yes — highest priority** | We have zero acoustics sidecar data (audio explicitly out of scope, SPEC line 6), so always take their "staging unavailable" branch: `Harmony Prefix` on `SpatialAudioSystem.Initialize`, gated on `ArenaEquatorRaid.IsLoaded()`, set `__result = Task.CompletedTask; return false;`. No need to port `IcebreakerAcoustics.TryPrepareSpatialAudio`'s room/portal rehydration (`IcebreakerAcoustics.cs:459-555`) — that needs a bake file + sidecar JSON we don't have and don't want for the spike. |
| Degrade `ProcessSourceOcclusion` to -1 when `!Initialized` | `RaidFixPatches.cs:60-76` (`Patch_OcclusionWhenUninitialized`) + `:133-153` (`Patch_OcclusionNeverThrows`, belt-and-braces Finalizer) | MIT | **Yes** | Direct requirement of "can walk and shoot without error spam" — `BetterAudio.PlayAtPoint` routes every gunshot/impact through `ProcessSourceOcclusion`, which will NRE on every shot once `Initialize` is skipped and `Initialized` stays `false`. Port both: a `Prefix` returning `-1`/`false` when `!SpatialAudioSystem.Initialized`, gated on our map, plus the Finalizer as backstop. |
| Ensure `EnvironmentManager.Instance` exists before `Player.Init` | `RaidFixPatches.cs:227-267` (`Patch_EnsureEnvironmentManager`) + `:275-280` (`Patch_EnsureEnvBeforeAnyPlayerInit`) | MIT | **Yes — predicted next blocker** | Confirmed unguarded deref at `Player.cs:28251`. Port only the bare-manager fallback (`new GameObject(...).AddComponent<EnvironmentManager>()`, `RaidFixPatches.cs:262-263`) — skip `IcebreakerWeather.TryBuild()` (weather out of scope) and `TryBuildEnvironmentTriggers()` (needs a sidecar we don't have). Anchor on `Player.Init` prefix directly (matches their own "universal safety net" reasoning, `:269-274`) rather than `LocalPlayer.Create`, since we don't need the Fika-path duplication reasoning — one prefix suffices for a non-Fika spike. |
| Swallow `AICoversData.RestoreData` / heal null holder collections | `RaidFixPatches.cs:3711-3884` (`Patch_CoversRestore`) | MIT | **Yes — new finding, confirmed via `AIVoxelesData.cs:52-57`** | Port only the `Finalizer` (`:3731-3760+`) — swallow the exception, then replicate its "finish RestoreData's job manually" tail (empty-but-valid `Points`/`Ways`/`Pathes`, rebuild the cover cache) so `AICoversData.CachePoints` and bot activation later don't NRE on a half-built object. Skip the `Prefix`'s retail-bake resurrection (`IcebreakerAIBake.TryFill`, `:3721-3722`) — we have no retail bake and bots are out of scope this milestone; the "coverless bots" outcome their comment accepts is fine for a spike with zero bots. |
| Swallow `AICoversData.CachePoints` | `RaidFixPatches.cs:3888-3898` (`Patch_CoversCache`) | MIT | **Yes, cheap insurance** | Companion to the row above — if our RestoreData Finalizer doesn't fully rebuild cache internals, this catches it. Trivial Finalizer, port as-is. |
| Create missing `AIStationaryController` before `BotsController.Init` body runs | `RaidFixPatches.cs:4118-4138` (the `Prefix` inside `Patch_EnsureStationaryController`, same class as the Postfix at `:3905-4139`) + `:4141-4151` (`Patch_StationaryInit` Finalizer) | MIT | **Yes — new finding, confirmed at `BotsController.cs:185-186`** | Port only the `AIStationaryController` half of the Prefix (`:4122-4126`) as a `BotsController.Init` prefix — skip the `ObservedCullingManager` half (`:4133-4137`, Fika-only observed-body visibility, not applicable, no Fika this milestone) and skip the whole `Postfix` (`:3910-3921+`: flares/sealed-doors/spawn-trigger rebuild, all bot/door features out of scope). Add `Patch_StationaryInit`'s Finalizer too, purely as a backstop — confirmed *not* strictly needed against our decompiled `AIStationaryController.cs:7` (`Weapons` has an inline `= Array.Empty<...>()` initializer in this SPT build, so `Init()`'s `foreach` is safe once the controller itself exists), version drift from Icebreaker's own decompile where `Weapons` was apparently nullable. |
| Reject the scene's own `LevelSettings.CameraPrefab` shell, fall back to Cam2 | `RaidFixPatches.cs:307-342` (`Patch_RejectShellCameraPrefab`) | MIT | **No — already true by construction** | SPEC §D authors `LevelSettings.CameraPrefab = null` directly (confirmed by `s2-validate-r2.md`'s independent spot-check: `CameraPrefab` PPtr `(0,0)`). BSG's own `SetCameraFromSettings` already falls back to Cam2 on a null `CameraPrefab` (their own patch comment: "already headed for the Cam2 fallback"), so we hit their end state with no shell-rejection code needed. |
| Add `FrostbiteEffect` to the Cam2 fallback camera | `RaidFixPatches.cs:378-389` (first half of `Patch_EffectsControllerFrostbite`) | MIT | **Yes** | Confirmed unconditional deref at `EffectsController.cs:1364-1365`. Since we land on the same Cam2 fallback Icebreaker does (row above), this is not Icebreaker-scene-specific — any mod using `CameraPrefab=null` hits it. Port only the `if (GetComponent<FrostbiteEffect>()==null) AddComponent<FrostbiteEffect>()` guard as an `EffectsController.Awake` prefix. |
| TOD_Camera / TOD_Scattering / PostProcessLayer heal (rest of `Patch_EffectsControllerFrostbite`) | `RaidFixPatches.cs:391-474` | MIT | **No / later** | All three are gated behind `Plugin.WeatherSystem.Value && IcebreakerAcoustics.IcebreakerLoaded()` even in the source mod — weather/TOD/post-processing fidelity is explicitly out of scope for the spike (SPEC line 6-7, "Defaults are acceptable"). Revisit only if a lighting-fidelity milestone starts. |
| Swallow `EffectsController.Init` (their "method_3") | `RaidFixPatches.cs:571-581` (`Patch_EffectsControllerInit`) | MIT | **Unclear — verify target exists** | Their comment implies `method_3` is a separate `Init()` distinct from `Awake()`; our decompiled `EffectsController.cs` shows all the `GetComponent`/`AddComponentCopy` work directly in `Awake()` (`:1217-1377`), no separate `Init`. Likely a naming/build drift between their obfuscated decompile and ours. If ported, resolve the target defensively via `AccessTools` + null-check (our own `HarmonyPatches.cs` pattern) rather than assuming the name — if not found, the `FrostbiteEffect` prefix on `Awake` above already covers the one confirmed unguarded deref. |
| Swallow every `CameraManager.SetFSR2/SetFSR3/SetDLSSPreset/SetAntiAliasing/SetSuperSampling/SetAspectRatio` | `RaidFixPatches.cs:3679-3703` (`Patch_CameraGraphicsSetters`) | MIT | **Likely yes — untested, same Cam2 root cause** | Their own reasoning ("camera arrives missing SSAA/SSAAImpl/VolumetricLightRenderer") is generic to any Cam2 fallback, not Icebreaker-scene-specific, so this plausibly reproduces for us too once raid start gets this far. Not yet independently confirmed against our decompile (multiple setter methods, didn't verify each). Port the multi-target Finalizer as-is; cheap and map-gated. |
| Swallow `GrenadeFlashScreenEffect.Awake` PrismEffects ref | `RaidFixPatches.cs:351-369` | MIT | **Later** | Only fires on a flashbang detonation — not required for "spawn, walk, shoot"; add when grenade testing starts. |
| Swallow `BetterValProcessor.Process` (weapon sway) | `RaidFixPatches.cs:100-106` | MIT | **Later — monitor** | Generic per-frame ProceduralWeaponAnimation safety net. Not confirmed against our decompile as map-specific; add only if observed during a shoot test. |
| Swallow `MotionEffector.FixedTracking` | `RaidFixPatches.cs:4654-4662` | MIT | **Later — monitor** | Same family as above; add only if observed. |
| Swallow `GlobalEventDispatcher.PlaySound` (bot-hearing airbag) | `RaidFixPatches.cs:4274-4290` | MIT | **No — bots out of scope this milestone** | Root cause is a dangling despawned-bot ref in the sound graph; irrelevant with zero bots. Revisit at the bots milestone. |
| `WindowBreakerManager`/`DoorSoundBoost`/`DoorTriggerEmit`/keycard-grip healing | `RaidFixPatches.cs:186-203`, `:161-184`, `:4421-4436`, `:4090-4116` | MIT | **No** | Doors/loot explicitly out of scope (SPEC line 6). |
| `SpawnPointsCollection.CalculateCanSpawnPmcBots` swallow | `RaidFixPatches.cs:209-219` | MIT | **No** | Superseded by our own fix: SPEC's §D/§H risk #11 already authors `BotZone.SpawnPointMarkers` as a real non-null (populated) list, confirmed by `s2-validate-r2.md`'s spot-check — the null-`List` NRE this patch guards against cannot occur for us by construction. |
| `BotZone.Init` prune dead `SpawnPointMarkers` | `RaidFixPatches.cs:4153-4185`ish (`Patch_BotZonePruneMarkers`) | MIT | **No** | Guards against a *server* spawn-merge destroying scene markers the *rip* didn't recognize — doesn't apply, our markers are hand-authored and match the server db 1:1 (SPEC §D). |
| `BotsGroup.IsPlayerEnemy` / `GameWorld.OnGameStarted` / `BotOwner.method_10` / `LocalGame.ModifySettings` / `BotOwner.Create` firewalls | `RaidFixPatches.cs:4211-4654` (`Patch_BotsInitFirewall`, `Patch_GameStartFirewall`, `Patch_GroupEnemyFirewall`, `Patch_BotActivationWitness/Stepwise`, `Patch_WaveSlotsAuthored`, `Patch_BotStartCorePoint`) | MIT | **No / later** | All bot-spawn/activation-specific; bots out of scope for the spike (SPEC line 6-7). Revisit as a block at the bots milestone — these look like the right reference set when that starts. |
| Weather stack (`Patch_ForceWinterSeason`, `Patch_WeatherDesatOverride`, `Patch_WeatherTickDiag`, `Patch_RainScreenOnCam2`, all `IcebreakerWeather`/`IcebreakerVolFog`/`IcebreakerSky`/`IcebreakerSnowGusts`/TOD_* code) | `RaidFixPatches.cs:282-297,477-528`, various `Icebreaker*.cs` | MIT | **No** | Weather/TOD explicitly out of scope (SPEC line 6). |
| PerfectCulling native-grid suppress/graft (`Patch_NativeCullingGate`, `Patch_GraftNativeGrid`) | `RaidFixPatches.cs:1589-1680` | MIT | **No** | We ship zero `PerfectCullingAdaptiveGrid`/cross-scene-sampler components at all (fresh scene, no bake) — nothing to gate or graft. Our own `PerfectCullingSkip.cs` already solves the actual shared problem (the `InitializeAutoCulling` hang/abort) at a different, simpler point — a `Prefix` on `PerfectCullingCrossSceneSampler.InitializeAutoCulling` itself — which needs no component-identity guessing at all. Confirmed compatible with SPEC's own down-weighted risk #2 assessment. |
| `LabsBoilerCullingStrip` (destroy PerfectCulling components at runtime) | `LabsBoilerCullingStrip.cs:1-35` | MIT (ManimalLabs, same danauraborealis license text) | **No** | Same reasoning as above, from the opposite direction (they strip components a *rip* shipped; we never emit any) — confirms our `PerfectCullingSkip.cs` approach is the right one for a from-scratch scene, nothing to adopt. |
| `LabsBoilerLocationSceneFix` (rebuild `LocationScene.WorldInteractiveObjects`/`LootableContainers`, scrub null array slots) | `LabsBoilerLocationSceneFix.cs:1-109` | MIT | **No** | Guards against rip damage to `LocationScene`'s registry arrays. SPEC's own WP4 already establishes this has **no** runtime analog for us — `LocationScene.FillArrays()` runs once at editor build time (`AuthorScriptsScene.cs`) against clean, hand-authored data, so every array is already non-null by construction (SPEC line 909, "no manual field-by-field authoring needed"). Confirmed no `LocationScene`/`BotZone` repair code exists in `ArenaMapPort.Client` today, matching this conclusion. |
| Camera donor graft (`IcebreakerCameraDonor.cs`, whole file) | `IcebreakerCameraDonor.cs:1-475` | MIT | **No — out of scope for the spike, revisit for visual-fidelity milestone** | SPEC §D: "Defaults are acceptable for the spike (decision #8 doesn't require lighting-fidelity parity)". This is a large, fragile, install-dependent mechanism (their own comments describe two prior black-screen regressions from it) solving a problem — matching a *retail* map's bloom/tonemapping/AA look — we don't have. Bare Cam2 + the `FrostbiteEffect` fix above is the correct-sized fix for "spawn, walk, shoot, no error spam". |
| `LabsBoilerAcoustics` (native `SpatialAudioRoom`-based partial-map acoustics merge) | `LabsBoilerAcoustics.cs:1-574` | MIT | **No** | Solves patching acoustics onto an *existing, mostly-intact* retail map with native `SpatialAudioRoom`s already present (`FindObjectsOfType<SpatialAudioRoom>` at `:77` assumes some exist) — inapplicable to a from-scratch scene with zero native rooms. Confirms `Patch_SpatialAudioInit`'s simpler "skip Initialize" branch (row 1) is the correct-sized fix for us, not Icebreaker's fuller room/portal rehydration and not Labs' merge-onto-native-rooms approach. |

## Shader rebind / bundle scene-load timing

**No evidence our `ShaderRebind.cs` missed the scenes.** `ShaderRebind.Install()` runs in
`Plugin.Awake()` (`Plugin.cs:53`) — BepInEx plugin startup, at game boot, long before any raid or
scene load — so `SceneManager.sceneLoaded += OnSceneLoaded` (`ShaderRebind.cs:66`) is registered
well before the first `sceneLoaded` event for any of our 6 scenes can possibly fire. The
"registered after sceneLoaded fired" hypothesis does not apply to a fresh game session.

Decompiled `TarkovApplication.LoadMapAndData` (`:2715-2790`) confirms the actual sequence:
`LoadScenesFromPreset` (fires one `sceneLoaded` per scene, synchronously, before the awaited
operation completes) → `LevelSettings.OnPostLoadingScene()` → `PerfectCullingCrossSceneSampler.InitializeAutoCulling`
(skipped by our patch) → `Task.Yield()` (one frame) → `PhysicsExtensions.Worlds.Create()` →
`AmbientLight.RuntimeOptimizePrepare()` → **`SpatialAudioSystem.Initialize`** (the abort point).
Unity's `SceneManager.sceneLoaded` event fires synchronously on scene activation, so by the time
`LoadScenesFromPresetOperation` is awaited, every one of our 6 scenes' `OnSceneLoaded` — and
therefore the immediate, synchronous `RunPass(scene)` (`ShaderRebind.cs:79`) — has already run.
**The rebind itself almost certainly succeeded**; there is just no way to see that in the log.

**Why no `[RebindShaders] Nx <name>` line appeared: our design defers ALL summary logging.**
`RunPass` (`:138-152`) does the rebind silently — no per-material or per-scene log line. The only
place `[RebindShaders] Nx <name>` is ever printed is `ReportSummary()` (`:313-328`), which only runs
inside the `Sweeper` coroutine after a real **3-second** `WaitForSeconds` delay (`:117-122`,
scheduled from `OnSceneLoaded` at `:80`). Given the sequence above, at most ~1 frame elapses between
our last scene's load and the `SpatialAudioSystem.Initialize` abort — nowhere near 3 seconds — so
the summary coroutine never got to run, regardless of whether the rebind worked. This is a genuine
**diagnostic gap**, not a functional one: right now a crash inside the first few seconds of any raid
is indistinguishable in the log from "rebind never ran" vs. "rebind ran fine, just never got to
report".

Contrast with InterchangeRework's `MaterialShaderRebind.cs`, which this file's structure was
otherwise modeled on: `OnIrSceneLoaded` (`:313-345`) logs a per-scene DEBUG summary **immediately**,
synchronously, right after that scene's own pass (`:342-344`, `"[ShaderRebind] {scene.name}: N
material(s) rebound..."`) — their 5s/25s sweeps (`:363-369`) are an *additional* pass for
error-shader stragglers only, not the sole place a summary is ever printed.

**Recommendation** (diagnostics only, not required to fix the reported crash): add an immediate,
synchronous log line at the end of each `RunPass(scene)` call in our `ShaderRebind.cs` (e.g.
`"[ArenaMapPort][RebindShaders] {scene.name}: pass complete"` plus a per-shader count), so a crash
in the first few seconds of a raid still leaves rebind evidence in the log. Keep the existing 3s
`ReportSummary()` for the unresolved-shader warnings, which do need the extra settle time.

## Texture-dimension and LODGroup warnings — not a runtime-repair matter

Grepped all of `ManimalIcebreaker-main`, `ManimalLabs-main`, and `SPT-InterchangeRework-main` for
`_Cube`/`Texture3D`/`_ParticleTex`/"more than one LODGroup" — no matches anywhere. None of the three
reference mods carry a runtime patch for either symptom, because both are pipeline/import defects
(a Cubemap or Texture3D source decoded and written out as a plain 2D texture; a duplicate-renderer
registration across two `LODGroup`s), not raid-init crashes a Harmony patch can paper over. These
belong to WP1/WP2's own extractor/importer, not this audit's scope — flagging for separate
follow-up, not included in the table above.

## `LocalGameMatching` `InvalidOperationException` — likely a downstream symptom, not independently confirmable

No file or class literally named `LocalGameMatching` exists under `_decompiled/` (searched the whole
tree), and the exact string "already completed" doesn't appear in any decompiled source — the
message is .NET's own wording for double-completing a `Task`/`TaskCompletionSource`
(`System.Threading.Tasks.Task`), not game code we can grep. Circumstantially this is consistent with
being a consequence of the primary `SpatialAudioSystem` NRE: `LoadMapAndData`'s exception unwinds
through an `async Task` chain (`GamePrepare`/matching flow) that also races a `cancellationToken`,
and a completion source getting signalled twice (once by the unwind, once by a concurrent
cancellation/timeout path) is exactly what that .NET message describes. No reference mod in any of
the three repos carries a patch for it either (nothing to adopt). Recommend simply re-testing after
the `SpatialAudioSystem` fix lands — if the `InvalidOperationException` disappears, it was this;
if it persists, it needs its own investigation (a decompiled BSG matching class, not yet located, or
a differently-obfuscated name than assumed here).

## Files read this pass

- `D:/sptmaps/_sources/ManimalIcebreaker-main/icebreaker-client/RaidFixPatches.cs` (full, 4663 lines)
- `D:/sptmaps/_sources/ManimalIcebreaker-main/icebreaker-client/IcebreakerCameraDonor.cs` (full)
- `D:/sptmaps/_sources/ManimalIcebreaker-main/icebreaker-client/IcebreakerAcoustics.cs` (partial —
  `TryPrepareSpatialAudio` and its call graph)
- `D:/sptmaps/_sources/SPT-InterchangeRework-main/InterchangeRework.Client/MaterialShaderRebind.cs` (full)
- `D:/sptmaps/_sources/ManimalLabs-main/labs-client/LabsBoilerAcoustics.cs` (partial),
  `LabsBoilerCullingStrip.cs` (full), `LabsBoilerLocationSceneFix.cs` (full)
- Our own: `ArenaMapPort.Client/{Plugin,HarmonyPatches,ShaderRebind,NavMeshInstaller,ArenaEquatorRaid}.cs`
- Decompiled cross-checks: `TarkovApplication.cs:2650-2793`, `SpatialAudioSystem.cs` (class shape,
  `Initialize`/`method_0`), `Player.cs` (`EnvironmentManager.Instance` call sites),
  `BotsController.cs:75-189`, `AICoversData.cs:280-430`, `AIVoxelesData.cs:1-60`,
  `AIStationaryController.cs` (full), `EffectsController.cs` (`Awake`, FrostbiteEffect lines)

## Not done

- Did not open `IcebreakerFikaCompat.cs`, `FikaBridge.cs`, or any Fika-specific code path — no Fika
  this milestone (workspace context), and the task scoped this to "FikaBridge-independent parts".
- Did not verify every one of the ~15 bot-activation/firewall patches' exact decompiled line numbers
  (`Patch_BotsInitFirewall` through `Patch_BotStartCorePoint`) — all are "no/later" on the same
  "bots out of scope" basis per SPEC line 6-7, so a line-level check wasn't load-bearing for this
  pass's verdict; re-open when a bots milestone starts.
- Did not implement any of the "yes" repairs — this is an audit/decision document only, per the task.
