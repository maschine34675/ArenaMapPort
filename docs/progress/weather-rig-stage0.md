# weather-rig-stage0 — donor Weather-rig harvest/activation (progress log)

Role: builder. Task: implement `WEATHER-RIG-SPEC.md`'s Stage 0 (Bay5 prototype) — harvest a vanilla
SPT built-in `"<Donor>_Scripts"` scene's `Weather`/`Weather_Simple` rig, deep-clone it onto a
`DontDestroyOnLoad` host, and toggle the clone active/inactive per raid so `WeatherController`/
`TOD_Sky`/`AmbientLight` feed `arena_bay5`'s ambient/sky/fog instead of the flat `LevelSettings`
ambient `ArenaLightingRepairs` stands in for on every other map. Client-only change; no git commit
made (workspace rule: the lead commits).

**Result: implemented and built (0 new warnings), not yet playtested.** Every other map (including
`arena_equator`) still defaults to `Donor = "Off"` — a complete no-op, byte-identical to today's
behavior. Bay5's Donor defaults to `"Reserve_Base_Scripts"`, so the rig activates automatically the
first time someone queues into Bay5 after this DLL is deployed and the harvest has had ~3s of main-menu
time to complete. See §6 for exactly what to test and §5 for two real bugs found and fixed while
implementing (not present in the input spec's own reasoning, caught by checking the exact decompiled
API surface this build ships).

---

## 1) Files changed

- **New**: `ArenaMapPort.Client/WeatherRig.cs` — the whole mechanism: pre-flight gate, harvest
  coroutine, `OnRenderObjectManager` provision, per-raid activation/deactivation, `SkyboxMode`
  (Dome/FrozenStatic), and the small accessors `LightingDiagnosticProbe` reads.
- **`ArenaMapPort.Client/Plugin.cs`** — `WeatherRig.Install()` added, called before
  `ArenaLightingRepairs.Install()`. Both subscribe to the same static `SceneManager.sceneLoaded`
  event; C# multicast delegates invoke subscribers in subscription order, and
  `ArenaLightingRepairs.OnSceneLoaded` reads `WeatherRig.IsActiveForCurrentMap()` on the same
  dispatch that just loaded this raid's Scripts scene — it must see this raid's own activation, not a
  stale `false` left over from before this raid started.
- **`ArenaMapPort.Client/ArenaLightingRepairs.cs`** — three changes, all no-ops when
  `WeatherRig.IsActiveForCurrentMap()` is false (i.e. byte-identical behavior for every `Donor="Off"`
  map):
  - `ApplyAll`: the fill light's effective intensity is forced to 0 while a rig is active, unless
    `KeepFillLight` says otherwise. The `FillLightIntensity` ConfigEntry itself is untouched (still
    live-tunable, still defaults to 0.2) — only the applied value is overridden.
  - `ApplyAmbient`/`ApplyFog`: `RenderSettings.fogColor`/`LevelSettings.fogColor` are skipped while a
    rig is active (`TOD_Sky.UpdateFog` writes `RenderSettings.fogColor` every `LateUpdate` once the
    rig is active, so our own one-shot write is silently overridden the very next frame regardless of
    call order — narrowed to *only* `fogColor`, since `TOD_Sky.UpdateFog` and
    `LevelSettings.OnPreCullCallback` were both re-read directly from the decompile this pass and
    confirmed to touch no other fog/ambient field between them; `fogMode`/`fogDensity`/the `fog`
    enabled flag and every ambient color field stay under this repair's control regardless of rig
    state).
  - `FogColor` changed from implicitly-`private` to `internal` so `LightingDiagnosticProbe` can log it
    side-by-side with the live `RenderSettings.fogColor`.
  - The light/sun intensity multipliers (`ApplyLights`) are **untouched** — `CaptureLightsFromScene`
    only ever walks the current map's own owned scenes (`scene.GetRootGameObjects()`), and the
    WeatherRig clone lives under its own `DontDestroyOnLoad` host in a different scene, so it is
    naturally, structurally excluded already; no gating needed.
- **`ArenaMapPort.Client/LightingDiagnosticProbe.cs`**:
  - New `ProbeWeatherRig(sb, map)` section, called from `RunProbe` right after the existing
    `ProbeLightingRigPresence` — every existing probe line is unchanged.
  - `ProbeAmbientGlobals` gets one added line: `_SHAr.magnitude` (the Stage-0 PASS bar for "is a rig
    actually feeding ambient").
  - Camera resolution (`CameraManager.Instance?.Camera ?? Camera.main`, with its try/catch) factored
    out of `ProbeCameraAndPostEffects` into a shared `ResolveActiveCamera(StringBuilder sb)` helper,
    reused by the new `ProbeWeatherRig` — no behavior change to the existing method.
- **`THIRD-PARTY-NOTICES.md`** — new entry crediting `ManimalIcebreaker`'s `IcebreakerSky.FreezeSkybox`
  (`icebreaker-client/IcebreakerSky.cs:65-83`, MIT) as the technique `WeatherRig.ApplySkyboxMode`'s
  `FrozenStatic` mode adapts (disable the dome's painter GameObjects only — the retail-skybox-material
  assignment and the JSON-reflection-apply half of that file are explicitly NOT ported).

## 2) Config keys and defaults

Three new `ConfigEntry`s per map, bound into the same `"Lighting (<map.Key>)"` section
`ArenaLightingRepairs` already uses (so Bay5's new keys land next to its existing lighting-repair
keys, not a new section):

| Key | Type | Bay5 default | Every other map's default |
|---|---|---|---|
| `Donor` | string | `"Reserve_Base_Scripts"` | `"Off"` |
| `SkyboxMode` | `Dome` \| `FrozenStatic` | `Dome` | `Dome` (inert while `Donor="Off"`) |
| `KeepFillLight` | bool | `false` | `false` (inert while `Donor="Off"`) |

`Donor = "Off"` is a complete no-op — `WeatherRig.OnSceneLoaded` returns before ever setting
`_activeMap`, so `IsActiveForCurrentMap()` is `false` for that map by construction and every gated
branch in `ArenaLightingRepairs` collapses back to today's exact code path.

`Donor`/`SkyboxMode`/`KeepFillLight` all take effect **on the next raid only** — none of them are
hot-applied mid-raid (re-toggling a live `WeatherController`/`TOD_Sky` while `LateUpdate` is running is
not supported). Changing `Donor` between raids does pre-warm the harvest for the newly-typed donor
name via a `SettingChanged` hook, so a tester can type a new donor into
`BepInEx/config/com.maschine.ArenaMapPort.cfg` (or ConfigurationManager) without restarting the game.

## 3) What the probe logs and how to read PASS/FAIL

`LightingDiagnosticProbe`'s once-per-raid log (fires ~5s after raid start) now has a `-- weather rig
(WeatherRig.cs) --` section. First two lines are always present:

```
  configured donor for arena_bay5='Reserve_Base_Scripts' active-this-raid=True skyboxMode=Dome
  harvest: loaded=True cloned=True unloaded=True validated=True keptComponents=<N>
  post-unload identity unchanged: LevelSettings=True LocationScene=True EnvironmentManager=True
```

If `active-this-raid=False`, check the line above it and the `[ArenaMapPort][WeatherRig]` warning
logged at Scripts-scene load — either `Donor` is `"Off"` for this map, or the donor wasn't harvested in
time (still pending/permanently failed this session); either way `ArenaLightingRepairs`' own workaround
is what's running for this raid.

When `active-this-raid=True`, the rest of the section (and one added line in the existing `-- EFT-custom
ambient/reflection shader globals --` block) is the **Stage-0 exit criteria** from the implementation
spec, one log line per item:

| # | Log line | PASS bar |
|---|---|---|
| 1 | `WeatherController.Instance-scene='...'` | a `DontDestroyOnLoad` scene name, never `Arena_Bay5_*` |
| 2 | `AmbientLight found=... shaderFieldsSet=...` + `_SHAr.magnitude=...` (in the ambient-globals block) | both `True`, magnitude non-zero |
| 3 | `AfterLighting command buffers:` (existing line, `-- camera --` block) | contains `'Custom Ambient'` |
| 4 | `OnRenderObjectManager present=...` | `True` |
| 5 | `TODSkyProvider.CurrentTime.GameDateTime-set=...` | `True` — see §5.3, this is also the ordering tripwire |
| 6 | harvest line's `validated=...` + the three `...Unchanged=...` flags | all `True` |
| 7 | (not logged — visual) | a screenshot showing a lit sky/ground, no black sky/flat-dark ground |

Any of 1-6 `False` (or `validated=False`/an identity flag `False` on the harvest line) is the signal to
leave `arena_bay5`'s `Donor` on `"Off"` and treat this as still-open — no other map is affected either
way, since every other map already defaults to `Off`.

Secondary lines (useful for root-causing a FAIL, not part of the PASS bar): `CloudController.Settings-
set`, the `sun:` line (`TOD_Sky.LightObject`'s name/intensity/color/forward), `FPS camera TOD_Scattering
present=.../Sky-set=...` (amendment 2's specific concern), `ReflectionProbe count=...`, and the
`fogColor(live)=... ArenaLightingRepairs.FogColor=...` side-by-side (if these differ while a rig is
active, `TOD_Sky` is correctly winning the write, matching §1's fog-gating change).

## 4) What to deploy

Only the client DLL: `ArenaMapPort.Client/bin/Release/maschine-ArenaMapPort.Client.dll` ->
`<SPT install>/BepInEx/plugins/ArenaMapPort/maschine-ArenaMapPort.Client.dll`. No bundle, no server
change, no new file to copy — `WeatherRig` loads its donor scenes straight out of the game's own
BuildSettings (`Reserve_Base_Scripts` is a vanilla SPT scene, already present in every SPT 4.1.5
install), and every config key is bound fresh into the existing `.cfg` on first launch after the
update (BepInEx appends new keys to an existing section without disturbing the tester's already-tuned
`ArenaLightingRepairs` values in that same section).

Verified: `dist/BepInEx/plugins/ArenaMapPort/maschine-ArenaMapPort.Client.dll` sha256
`80ba1c0bf3d275fb0a91eea64e91a5ef0e5d562ce9f6d6823ff0c333101c3f7b`, identical to
`ArenaMapPort.Client/bin/Release/maschine-ArenaMapPort.Client.dll` (see report for the build log; the
`ArenaMapPortStageAdditionalMaps` bundle-staging step failed as expected — `validate_port.py --map
airpit` — because the background receipt-revalidation job currently has the 12 additional maps' work
in flight; that step never touches the DLL itself, which is already staged by the earlier
`ArenaMapPortDistCopy`/`ArenaMapPortStageWpOutputs` targets by the time it runs).

## 5) Known risks

> **Superseded by §7 "Review fix-ups" below** — risks #1 and #2 here contain two factual errors caught by
> an adversarial review (`CameraManager.Instance` cannot be null; the try/catch cannot catch a child Awake
> exception) and are now fixed in code. Left as originally written here per this doc's append-only log
> convention — read §7 for the corrected analysis and the actual fix.

1. **`CameraManager.Instance` may be null when `WeatherController.Awake()` runs.**
   `WeatherController.Awake()` (`EFT.Weather/WeatherController.cs`) does
   `CameraManager.Instance.OnCameraChanged += OnCameraChanged;` with **no null check** —
   `EFT.CameraControl.CameraManager.instance` is a plain `[NonSerialized] public static` field, not a
   `Comfort.Common.Singleton<T>`, and its lifetime across the main-menu -> raid-load transition was not
   independently re-verified this pass. If it is ever null at the moment `OnSceneLoaded` calls
   `rig.Root.SetActive(true)`, `WeatherController.Awake()` throws, which is caught by `OnSceneLoaded`'s
   own try/catch — logged as `"activation threw"`, the rig deactivates itself, and the raid falls back
   to `ArenaLightingRepairs`' workaround. This is a safe failure mode (no crash), but it would show up
   as PASS-bar item 1/5 failing outright in the probe log; if that happens, the exact exception message
   in the `[ArenaMapPort][WeatherRig] ... activation threw` log line tells you whether this was the
   cause.
2. **`OnRenderObjectManager.OnRenderObject()` also dereferences `CameraManager.Instance` with no null
   check** (`instance.Camera`/`instance.OpticCameraManager.Camera`, `OnRenderObjectManager.cs`). Unlike
   risk 1, this component is added to `_host` right after the **first successful harvest** — which, per
   the pre-flight gate, happens at the main menu, well before any raid. If `CameraManager.Instance` is
   null at the main menu and Unity still calls `OnRenderObject()` on `_host` every frame (this fires on
   any enabled `MonoBehaviour`, not just ones with a `Renderer`), this would spam a `NullReferenceException`
   in the log continuously from harvest time onward, not just once. Watch the log immediately after the
   first `"harvested '...'"` line for repeating NREs; if present, the fix is to defer
   `EnsureOnRenderObjectManager()`'s `AddComponent` to the first successful raid activation instead of
   harvest time (not implemented — this file is a fixed, un-owned donor class, so the only lever
   available on our side is *when* we add it).
3. **The `sceneLoaded`-reuse ordering argument (no new Harmony prefix) is unverified beyond a decompile
   read.** §7 of the implementation spec, and this file's own `OnSceneLoaded` doc comment, walk through
   why `BaseLocalGame.InitWeather`'s gate should always see a non-null `WeatherController.Instance` by
   construction — but this has not been exercised against a live raid yet. `ProbeWeatherRig`'s
   `TODSkyProvider.CurrentTime.GameDateTime-set` line is the designed tripwire: if it is ever `False`,
   the ordering assumption was wrong, and the documented (not implemented) fallback is a Harmony prefix
   on `BaseLocalGame<TPlayerOwner>.Create<T>` that activates the rig even earlier.
4. **Two bugs in the input spec's own literal code, fixed during implementation (not present in the
   shipped file):**
   - `SceneManager.LoadScene(donorName, LoadSceneMode.Additive)` does not compile against this exact
     build — verified directly against the decompiled `UnityEngine.CoreModule.dll`: the
     `(string, LoadSceneMode)` overload returns `void`, not `Scene`, on this Unity version; only the
     `(string, LoadSceneParameters)` overload returns the loaded `Scene`. Fixed by constructing a
     `LoadSceneParameters(LoadSceneMode.Additive)` explicitly.
   - `UnityEngine.Object.DontDestroyOnLoad(clone)` was ordered *after* `clone.transform.SetParent
     (_host.transform, ...)` in the spec's own code — `DontDestroyOnLoad` only accepts a **root**
     GameObject; called on an already-reparented (non-root) object it logs a Unity error and does
     nothing, every single harvest. Fixed by swapping the order: `DontDestroyOnLoad` first (while
     `clone` is still root, immediately after `Instantiate`), `SetParent` second (a child of an
     already-`DontDestroyOnLoad` object persists with it regardless, so this ordering achieves the same
     end state without the spurious error).
5. **`Laboratory_Scripts`' `"Weather_Simple"` path (`RigKind.Simplified`) is designed into the data
   model but not exercised by anything yet** — Stage 0 only ever harvests `Reserve_Base_Scripts`. The
   optional Equator A/B test in §6 is the first real exercise of that code path.
6. **Physics broadphase revert uses a generic large bounds, not Bay5's actual bounds** — carried over
   from the spec unchanged (`AuthorScriptsScene.cs` authors no `LevelPhysicsSettings` of its own to read
   real bounds from). Acceptable for a prototype; revisit if physics behaves oddly in the Bay5 playtest.

## 6) What to test

### Primary: Bay5, `Donor` at its default (`Reserve_Base_Scripts`)

1. Deploy the DLL (§4). Start SPT, reach the main menu, and **wait at least ~10s** before queuing
   (the harvest coroutine polls every 3s and only harvests once the pre-flight gate — no raid of any
   kind loaded — is clear; the very first couple of ticks right after boot may still find the game
   mid-bootstrap).
2. Check the log for `[ArenaMapPort][WeatherRig] harvested 'Reserve_Base_Scripts' (Full) — ...` before
   queuing. If it hasn't appeared yet, wait longer — activation silently falls back to
   `ArenaLightingRepairs`' workaround if the harvest hasn't finished in time (logged as a `Donor='...'
   configured but not harvested in time` warning at Scripts-scene load, not an error).
3. Queue into `arena_bay5`, play until the raid actually starts (past the countdown).
4. Visually confirm: lit sky/ground, no black sky, ground no longer flat-dark (PASS-bar item 7).
5. Read the `[ArenaMapPort][Diag]` log line ~5s+ after raid start; walk PASS-bar items 1-6 from §3.
6. If everything passes, optionally play a **second raid** on Bay5 in the same session (no game
   restart) — this exercises `SetActive(false)`/`SetActive(true)` cross-raid reuse (§9's "not
   implemented" `OnCameraChanged` re-invocation backstop) and the `TOD_Scattering.Sky-set` line
   specifically, since `Cam2`/the FPS camera is a fresh prefab instance each raid.

### Optional A/B: Equator on `Laboratory_Scripts` (exercises the Simplified/`Weather_Simple` path)

Set, in `arena_equator`'s own `"Lighting (arena_equator)"` section:

```
Donor = Laboratory_Scripts
```

(leave `SkyboxMode`/`KeepFillLight` at their defaults — `SkyboxMode` is a no-op for a `Simplified` rig,
since `ApplySkyboxMode` returns immediately when `rig.Kind != RigKind.Full`). Restart or wait for the
harvest, then queue into `arena_equator` and read the same probe log — expect `harvest: ... (Simplified)`
in the harvest-time log line and `AmbientLight found=True`/`_SHAr.magnitude` non-zero as before, but no
dome/sun lines to speak of (Laboratory's `Weather_Simple` has no `TOD_Components` dome/sun rig per
LIGHTING-DESIGN.md). This is purely exploratory — Stage 0's own exit criteria (§10 of the implementation
spec) are Bay5-only; a Laboratory-donor Equator raid is not required to pass anything to consider Stage
0 done, but a full crash/NRE here would be worth flagging before Stage 1 builds on the Simplified path
for real.

Revert `Donor` back to `Off` for Equator afterward if you don't want the rig active there going forward.

## 7) Review fix-ups

An adversarial review of the Stage 0 implementation raised six findings. All six were independently
re-verified against fresh `ilspycmd -t` decompiles (`Comfort.dll`'s `Singleton<T>`,
`EFT.CameraControl.CameraManager`, `EFT.Weather.WeatherController`, `TOD_Sky`, `TODSkySimple`,
`TOD_ImageEffect`, `OnRenderObjectManager`, `MonoBehaviourSingleton<T>`) before touching any code. All
six were confirmed real; all six are now fixed in `WeatherRig.cs` (`LightingDiagnosticProbe.cs` for one).
**This also corrects two factual errors in this doc's own §5 "Known risks" #1 and #2, written before this
pass — see the per-finding notes below.**

Rebuilt and verified: `dist/BepInEx/plugins/ArenaMapPort/maschine-ArenaMapPort.Client.dll` sha256
`9de0e61cf52ebd1ef9af588ca133b660b1e9a91392e926a75bfe508ab1b1281f`, identical to
`ArenaMapPort.Client/bin/Release/maschine-ArenaMapPort.Client.dll` — 0 build warnings. The
`ArenaMapPortStageAdditionalMaps`/`validate_port.py --map airpit` step failed again as expected (the
background receipt-revalidation job for the 12 additional maps was still in flight); that step never
touches the DLL, which is already staged by `ArenaMapPortDistCopy`/`ArenaMapPortStageWpOutputs` by the
time it runs.

### 1. try/catch around `SetActive(true)` cannot catch a child Awake exception — HIGH, confirmed, fixed

Re-verified: Unity's own message dispatch (Awake/OnEnable/Start) try-catches each `MonoBehaviour`
callback internally and logs to console; it never lets the exception propagate to the C# caller of
`SetActive()`/`Instantiate()`. §5 "Known risks" #1 in this doc (and this file's old doc comments) were
wrong to say a failed `WeatherController.Awake()` "is caught by `OnSceneLoaded`'s own try/catch" — it
is not.

**Fix**: `WeatherRig.VerifyFirstActivation(rig)` — called from `OnSceneLoaded` immediately after
`SetActive(true)`, but only on a rig's first-ever activation (Awake does not run again after that).
Re-reads `WeatherController.Instance == rig.Controller` and `rig.Sky.Initialized` (Full), or
`Singleton<TODSkySimple>.Instance == rig.SimpleSky` (Simplified) — fields Awake() is responsible for
setting — and throws a real, catchable `InvalidOperationException` if they don't look right, which the
existing try/catch now genuinely catches (because the exception originates in our own code, not inside
Unity's swallowed message dispatch).

### 2. "`CameraManager.Instance` may be null" is wrong; the real risk is a per-frame exception storm — HIGH, confirmed, fixed

Re-decompiled `EFT.CameraControl.CameraManager.cs:150`: `public static CameraManager Instance => instance
?? (instance = new CameraManager());` — auto-vivifying, can never be null. §5 "Known risks" #1 and #2 in
this doc were wrong on this point; correcting it here rather than editing that section, per this file's
own append-only convention for this log.

The real, freshly-confirmed risk: `OnRenderObjectManager.OnRenderObject()` (vanilla, un-owned) does
`if (current == instance.Camera || current == instance.OpticCameraManager.Camera)` with **no**
`CameraManager.Exist` guard — unlike every other vanilla call site checked this pass (`AmbientLight.cs`,
`AudioSourceContainer.cs`, `TODSkySimple.cs`, `SoundSuspensionController.cs`, `AudioUtils.cs`,
`GamePlayerOwner.cs`, all gate on `CameraManager.Exist` first). `CameraManager`'s `OpticCameraManager` is
a get-only auto-property with no discoverable constructor in the decompile, so a freshly auto-vivified
"cold" `CameraManager` (exactly what existed when `EnsureOnRenderObjectManager()` used to run — right
after the first harvest, at the main menu, before any raid) very likely has `OpticCameraManager == null`.
`OnRenderObject()` fires every rendered frame once the component is added, so this would have been a
continuous per-frame NRE storm from harvest time onward, not the "intermittent, watch for repeating NREs"
risk originally described.

**Fix**: `EnsureOnRenderObjectManager()` now gates its `AddComponent` on `CameraManager.Exist` (the
idiomatic vanilla pattern) and is called a second time, from `OnSceneLoaded` on every raid activation, so
it still gets added once a real (non-"cold") `CameraManager` exists, rather than only ever being retried
at harvest time.

### 3. Fog hand-off assumes the donor's `TOD_Sky.Fog.Mode` is never `None` — LOW, plausible, diagnostic added (not independently confirmed)

`TOD_Sky.UpdateFog()` only writes `RenderSettings.fogColor` for `Fog.Mode` `Color`/`Directional`; `None`
writes nothing. `ArenaLightingRepairs.ApplyAmbient/ApplyFog` skip their own `fogColor` write whenever a
rig is active, on the assumption `TOD_Sky` is winning it instead. Whether `Reserve_Base_Scripts`' authored
`TOD_Sky.Fog.Mode` is ever `None` was **not** independently verified this pass either (would require
opening the donor scene in Unity — out of scope, no Unity run per this task's hard rules) — this remains
a labeled inference, not a confirmed defect.

**Fix**: `LightingDiagnosticProbe.ProbeWeatherRig` now logs `TOD_Sky.Fog.Mode` side-by-side with the
existing `fogColor(live)`/`ArenaLightingRepairs.FogColor` comparison, so the very first playtest confirms
or refutes this directly instead of it going unnoticed.

### 4. Stale `TOD_Sky`/`WeatherController` singletons leak across raids and across ALL other maps — HIGH, confirmed, fixed

Re-decompiled and confirmed exactly as reported:
- `Comfort.Common.Singleton<T>.Create(instance)` is `_instance = instance;` — an unconditional overwrite,
  no guard. Only `MonoBehaviourSingleton<T>.OnDestroy()` calls the matching `TryRelease`.
- The clone is `SetActive(false)`'d for reuse, **never** `Destroy()`'d — so `OnDestroy` (and therefore
  `TryRelease`) never fires for `TOD_Sky`/`TODSkySimple` after a raid ends.
- `EFT.Weather.WeatherController.Instance` is a plain `public static` field (not a `Comfort.Common.
  Singleton<T>`), set only in `Awake()`, cleared only in `OnDestroy()` — same problem, worse in one way:
  `WeatherRig.PreflightClear()` itself gates on this field being `null`, so once left stale it also
  **permanently blocks harvesting any further donor for the rest of the session**.
- `WeatherController.Awake()`/`TODSkySimple.Awake()` both subscribe to `CameraManager.Instance.
  OnCameraChanged` and are never unsubscribed by `SetActive(false)` (only by the `OnDestroy` that never
  runs). Confirmed this subscription is not merely a passive leak: `WeatherController.OnCameraChanged`
  **explicitly** does `tod_Scattering_0.Sky = MonoBehaviourSingleton<TOD_Sky>.Instance;` on every camera
  change — and `CameraManager` fires `OnCameraChanged` on every raid's fresh `Cam2`, not just the first —
  so the stale Bay5 sky gets actively re-assigned onto every subsequent raid's FPS camera on every OTHER
  map, not merely picked up passively via `TOD_ImageEffect.Awake()`'s own null-coalescing fallback as
  originally described (an even more certain failure path than reported).

**Fix**: `OnSceneUnloaded` now explicitly, before `SetActive(false)`: unsubscribes `CameraManager.
Instance.OnCameraChanged` from the rig's controller/sky, releases `Singleton<TOD_Sky>`/
`Singleton<TODSkySimple>` (identity-guarded), and clears `WeatherController.Instance` if it's still this
rig's. `OnSceneLoaded`'s new `ReestablishSingletons(rig)` puts all of it back (Comfort singleton
`Create`, `WeatherController.Instance =`, and an exactly-once `-=`/`+=` re-subscription) on every
activation — a no-op in effect on the very first activation (Awake already did the same writes),
load-bearing from the 2nd activation onward since Awake never fires again on an already-woken clone.

**Not fixed, flagged as a residual, lower-priority follow-up** (outside what was reported): `TODSkyProvider
.Instance` (a separate plain static, distinct from `Singleton<TOD_Sky>.Instance`) is also only ever set
from `Awake()`/harvest time and is not cleared by this fix. The two concrete failure paths this finding
evidenced (`TOD_ImageEffect`'s fallback and `WeatherController.OnCameraChanged`'s explicit assignment)
both read `MonoBehaviourSingleton<TOD_Sky>.Instance`, not `TODSkyProvider.Instance`, so they're covered;
`TODSkyProvider.IsAvailable` (which some other vanilla code may consult) also now correctly reflects
`Singleton<TOD_Sky>.Instantiated`, i.e. `false` once deactivated. `TODSkyProvider.Instance` itself staying
stale was not part of the reported evidence chain and is left as-is for a future pass.

### 5. `OnSceneUnloaded` re-derives the rig from the live Donor config instead of tracking what was activated — HIGH, confirmed, fixed

Confirmed: `OnSceneLoaded` activated whichever rig `cfg.Donor.Value` named at load time, but
`OnSceneUnloaded` re-read `cfg.Donor.Value` **again, fresh, at unload time** to find the rig to
deactivate. `Donor`'s own doc comment asks testers not to edit it mid-raid, but nothing in the code
enforces that (`ConfigurationManager`/F12 can edit it live) — an edit mid-raid would make unload look up
the wrong (or a not-yet-harvested) rig, leaving the actually-active clone's `WeatherController`/`TOD_Sky`/
`AmbientLight` running forever afterward: live, continuing `LateUpdate` writes into the main menu and
every following raid, not merely a dangling singleton reference.

**Fix**: new `static HarvestedRig _activeRig` tracks the rig object `OnSceneLoaded` actually activated
(set alongside `_activeMap`). `OnSceneUnloaded` deactivates `_activeRig` directly and clears both fields
unconditionally at the end, regardless of what `cfg.Donor.Value` currently reads.

### 6. Post-unload identity checks computed but never enforced — MEDIUM, confirmed, fixed

Confirmed: `lsUnchanged`/`locUnchanged`/`envUnchanged` (the amendment-4 checks re-verifying `LevelSettings`
/`LocationScene`/`EnvironmentManagerBase` actually released after `UnloadSceneAsync`) were computed,
stored on `HarvestedRig`, and logged — but `HarvestOne` unconditionally set `ValidationOk = true` and
added the rig to `_harvested` regardless of their values, so a donor whose singletons genuinely leaked
past unload would still be marked fully successful and made eligible for per-raid activation.

**Fix**: `HarvestOne` now routes through `Fail(donorName, ...)` (destroying the clone first) when any of
the three is `false`, instead of constructing a `HarvestedRig` — same retry/permanent-failure bookkeeping
as every other harvest failure path.
