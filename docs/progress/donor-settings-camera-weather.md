# donor-settings-camera-weather — donor-consistent lighting/weather fix (progress log)

Role: builder. Task: implement `PLAN.md` ("ArenaMapPort — Donor-Consistent Lighting/Weather Fix —
Implementation Plan") with the critic's amendments, client-only (`ArenaMapPort.Client`).

**Result: implemented and built (0 new warnings), dist DLL hash confirmed == bin DLL hash. Not yet
playtested.** All changes are client-only — no Unity, no bundle rebuild, no `tools/`/`Server` change.
No git commit made (workspace rule: the lead commits).

Lead's theory this pass addresses: we run a vanilla weather rig (harvested from Reserve) on top of a
non-vanilla camera (Cam2 fallback) and a stub `LevelSettings`, so wetness/reflection/scattering/exposure
all execute with parameters the rig was never tuned against. Strategy: make rig + `LevelSettings` fields
+ camera a **consistent, donor-sourced set** for any map whose rig is active (today only `arena_bay5`),
while a no-rig map (`Donor="Off"`, e.g. `arena_equator`) stays byte-for-byte on today's code path.

---

## 1) Files changed

- **`ArenaMapPort.Client/WeatherRig.cs`** — the bulk of this pass. New `TextureBudgetTier` enum; six new
  per-map `ConfigEntry`s on `RigConfigBundle` (§A) plus `UseDonorCameraPrefab` (§B); three new fields on
  `HarvestedRig` (`CameraPrefab`/`PrismPreset`/`PostProcessProfile`); donor camera/preset capture inside
  `HarvestOne` (§B.1, capture-only per the critic's amendment — see §3 below); `RenderSettingsSnapshot`
  extended to capture/restore four `Shader.SetGlobal*` process-wide globals (critic amendment 3); two new
  methods `ApplyDonorLevelSettingsOverrides`/`ApplyDonorCameraPrefab` called from `OnSceneLoaded` right
  after the rig itself activates; the existing failure `catch` now also resets
  `LevelSettings.CameraPrefab`/`PrismPresetPrefab`/`PostProcessProfilePrefab` to null defensively; two new
  accessors (`ActiveRig`, `UseDonorCameraPrefabForCurrentMap`) for the diagnostic probes.
- **New: `ArenaMapPort.Client/RainWetnessRepairs.cs`** — §C.3 (`WetRendererIntensityMultiplier`, a Harmony
  postfix on `RainController.AbstractStateSummer.SetWetting`) + §C.4 (`MaxLyingWaterOverride`, a plain
  field set on the harvested rig's own `RainController.PuddleWaterLevel`, called once per raid from
  `WeatherRig.OnSceneLoaded` — simpler than the plan's originally-suggested Harmony postfix on
  `vmethod_15`; see §3 below for why).
- **New: `ArenaMapPort.Client/WeatherTestOverride.cs`** — §D. Harmony postfix on
  `EFT.Weather.WeatherController.SetWeatherNodes`, gated `ArenaMapRaid.IsLoaded()` FIRST per the critic's
  amendment (see §3), with an explicit try/finally reentrancy guard.
- **`ArenaMapPort.Client/ShaderRebind.cs`** — §C.1. Restores the ground-material wetness fix (removed in
  `0792f53` after a confounded test) under new config keys (`GroundMaterialWetnessFixEnabled`,
  `GroundSmoothnessMultiplier`).
- **`ArenaMapPort.Client/DecalRepairs.cs`** — unchanged (lead decision, see §10): `HideProjectorDecals`
  stays a diagnostic switch, default false.
- **`ArenaMapPort.Client/CameraEffectsRepairs.cs`** — §B.4. Both `ApplyExposureFix`'s and
  `ApplyTodFixes`'s existing "component already present" branches now log a `source=donor|fallback` tag;
  `TOD_Camera` destruction now logs too (confirms the critic's finding that the donor camera never
  carries one, so this destroy is a safe no-op on the donor path).
- **`ArenaMapPort.Client/LightingDiagnosticProbe.cs`** — `ProbeWeatherRig` extended with four new lines:
  live `LevelSettingsOverride` readback, `CameraPrefabTakeover` state, the §C wetness-mitigation config
  summary, and live `PuddleWaterLevel`/`WetRenderer.Intensity` readback.
- **`ArenaMapPort.Client/DaytimeVisualsProbe.cs`** — one new `ProbeWeatherTestOverride` line reporting the
  configured `Weather (test)` mode.
- **`ArenaMapPort.Client/HarmonyPatches.cs`** — two new installers (`InstallWeatherTestOverride`,
  `InstallWetRendererDamper`), following the file's existing `AccessTools.Method` + `Verify()` convention.
- **`ArenaMapPort.Client/Plugin.cs`** — `RainWetnessRepairs.Install()` / `WeatherTestOverride.Install()`
  added before `HarmonyPatches.InstallAll` (their Harmony hooks read config bound in `Install()`).
- **`ArenaMapPort.Client/ArenaMapPort.Client.csproj`** — one new reference,
  `Unity.Postprocessing.Runtime.dll` (defines `PostProcessProfile`, needed for the new `HarvestedRig`
  field; `PrismPreset` is a BSG type already reachable via `Assembly-CSharp.dll`).

---

## 2) Evidence this pass relied on (re-verified against the decompile, not re-copied from the plan)

- `LevelSettings.cs` (D:/SPT41/Development/_decompiled/LevelSettings.cs): `ZeroLevel` (-78.64 default,
  class default = Laboratory's own authored value), `HeightFalloff` (0.029 default), `MaxMapTextureMemory`
  (a **`Vector4`**, not 4 raw floats — `new Vector4(1024,1024,1024,1024)` default), `RainBounds` (a
  `Bounds`, default center (0,0,0) size (1000,220,1000)), `_northDirection` (a **public** field despite
  `[SerializeField]`, default 0), `CameraPrefab`/`PrismPresetPrefab`/`PostProcessProfilePrefab` (all
  public, all null in our own authored scenes). `Awake()` calls `ApplySettings()` once;
  `TarkovApplication.cs:2731` calls `Singleton<LevelSettings>.Instance.OnPostLoadingScene()` (which also
  calls `ApplySettings()`) right after `LoadScenesFromPresetOperation` succeeds — this happens strictly
  AFTER our Scripts scene's own `sceneLoaded` fires (the Scripts scene loads first, `LoadSceneMode.Single`,
  inside that same operation), so a field we set inside `WeatherRig.OnSceneLoaded` is picked up by this
  LATER natural `ApplySettings()` call too — we also call `ApplySettings()` ourselves directly (belt and
  braces, and the only way `MaxMapTextureMemory`'s streaming-budget refresh and the four `Shader.
  SetGlobal*` writes take effect on the SAME raid without relying on that second, indirect call path).
- `TomlTypeConverter`/`LazyTomlConverterLoader` (D:/SPT41vanilla/BepInEx/core/BepInEx.dll, decompiled):
  confirmed `Vector3`/`Vector4`/`Color` all have BepInEx config converters (Unity JSON-serialized for
  Vector types, hex string for Color) — `Bounds` does NOT, hence `RainBoundsCenter`/`RainBoundsSize` as
  two separate `Vector3` entries rather than one `Bounds` entry.
- `WeatherController.cs`/`RainController.cs`/`WeatherNode.cs`/`WeatherCurve.cs`: `SetWeatherNodes` called
  from both `BaseLocalGame<TPlayerOwner>.InitWeather` (BaseLocalGame.cs:514) and `LocalGame.vmethod_5`
  (LocalGame.cs:223, gated `_raidSettings.mode == ELocalMode.PVE_OFFLINE`) — confirms §D's target choice.
  `RainController.PuddleWaterLevel` (default `(0, 0.95)`) and `_wetRenderer` are both **public fields**
  directly on `RainController`, not private/reflection-gated — confirmed §C.4 needs no Harmony patch at
  all. `WeatherCurve.Rain`'s property getter is `Mathf.Clamp01(rainCurve.Evaluate(...))` — it can never
  read back above 1.0 regardless of the authored `WeatherNode.Rain` value; corrects the plan's
  "`Clamp01(InverseLerp(1,5,Rain))=1.0`" framing (see `WeatherTestOverride.cs`'s own doc comment for the
  full correction) without changing the practical outcome (Rain=5 still reliably pins the live value at
  1.0, still at/above the 0.83 peak the 2026-09-17 playtest observed).
- `git show aae5a50` — confirms the exact prior ground-material fix (§C.1) this pass restores: material
  identified by extraction hash `be005d9cd5d9384e` (`Bay5_Vertex_Paint_Ground`), `_BlendStrength` 5->2,
  `_Smoothness0-2`/`_FresnelSpecular2` scaled by a configurable multiplier (0.6 default), plus a drift
  guard that warns if a material with that hash no longer looks like the one the hash was captured
  against.

## 3) Where this pass deviates from the plan (and why)

1. **§B.1 camera harvest — capture-only, per the critic's amendment (implemented as specified).** The
   plan's original design would `Instantiate()` and `SetActive(false)` the donor's OWN
   `LevelSettings.CameraPrefab`/etc before cloning — mirroring the "Weather" root technique. The critic
   confirmed Reserve/Laboratory/Customs' `CameraPrefab` all resolve to the identical
   `m_PathID:691` object in `sharedassets15.assets`, a persistent, cross-map, process-wide shared asset
   that survives `SceneManager.UnloadSceneAsync` on its own — unlike "Weather", which IS scene-local and
   needs the clone-before-unload dance. Mutating the original would have been a real, session-wide side
   effect on shared BSG asset state. Implemented as specified: `HarvestOne` now just reads the three
   references directly off the donor's `LevelSettings` singleton while the donor scene is still loaded,
   with NO `Instantiate()`/`SetActive()` call anywhere near them; `CameraManager.SetCameraFromPrefab`'s own
   `Instantiate()` (invoked from `ApplyDonorCameraPrefab` assigning them onto OUR raid's `LevelSettings`)
   does the real per-raid work, exactly as a real Reserve raid already does.
2. **§C.4 puddle-height clamp — plain field set instead of a Harmony postfix.** The plan suggested a
   Harmony postfix on `RainController.AbstractState.vmethod_15` to clamp the `PuddleWaterLevel.y` lerp
   ceiling. Since `PuddleWaterLevel` is a plain public `Vector2` FIELD (not computed, not private) directly
   on the harvested rig's own `RainController` instance — which this plugin already holds a reference to
   (`rig.Controller.RainController`) — setting it once per raid activation achieves an identical effect
   with no Harmony/reentrancy/timing surface at all. Kept as a deliberate simplification, not a scope cut;
   the config surface (`MaxLyingWaterOverrideEnabled`/`MaxLyingWaterOverride`) and default (disabled) match
   the plan exactly.
3. **§D reentrancy + gate — implemented exactly as the critic amended it.** `ArenaMapRaid.IsLoaded()` is
   the literal first line of `WeatherTestOverride.Postfix`, before the reentrancy check or anything else —
   not "defense-in-depth" as the plan's original text framed it. The reentrancy flag (`_applying`) is set
   inside a `try { ... } finally { _applying = false; }` around the recursive `SetWeatherNodes` call, so a
   thrown exception can never leave it stuck.
4. **§B.4 — mostly verification, not new code.** Every existing guard the plan asked to "audit" (`Rain
   ControllerCameraGuard`, `EffectsControllerFrostbitePrefix`, `EyeBurnGuardPrefix`/
   `EyeBurnPauseGuardPrefix`, `CombatScreenEffectsGuard`, `FlashAccumulatorGuardPrefix`) already gates on
   "is the component/field actually missing", confirmed by re-reading each one this pass — they degrade to
   a clean no-op on the donor-camera path with zero code changes needed. `CameraEffectsRepairs.
   ApplyExposureFix`/`ApplyTodFixes` likewise already had the right gate (`if (prism.useExposure) return;`
   / `bool added = scattering == null;`); the only change made was adding the `source=donor|fallback` log
   tag the plan asks for, so a playtest's log can prove which path fired.
5. **Not attempted this pass:** the plan's own "explicitly not attempted" diagnostic additions
   (`Camera.farClipPlane`/`GlobalFog.height` logging via reflection, a `ReflectionProbe` mode/size/boxSize
   dump) — `GlobalFog`'s exact type wasn't quickly locatable in the available decompile within this pass's
   scope; deferred alongside the plan's own §E items (see §6 below).

---

## 4) Config keys (all under each map's existing `Lighting (<key>)` section unless noted)

### §A — donor-consistent `LevelSettings` overrides (new keys; applied only while a rig is active)

| Key | Type | Default (Bay5 / other maps) | Effect |
|---|---|---|---|
| `ZeroLevelOverride` | float | `-8.2` / `0` | `LevelSettings.ZeroLevel` — per-map geometry tuning, NOT a donor copy |
| `HeightFalloffOverride` | float | `0.025` / `0.029` | `LevelSettings.HeightFalloff` — real donor copy (Reserve/Customs both author 0.025) |
| `TextureBudgetTier` | enum (`Stub`/`ReserveTier`/`CustomsTier`/`LaboratoryTier`) | `ReserveTier` / `Stub` | `LevelSettings.MaxMapTextureMemory`; Customs/LaboratoryTier fall back to Stub with a log warning (not measured yet) |
| `RainBoundsCenter` | Vector3 | `(0,0,0)` (both) | `LevelSettings.RainBounds.center` — deliberate no-op until real Bay5 coordinates are measured |
| `RainBoundsSize` | Vector3 | `(1000,220,1000)` (both) | `LevelSettings.RainBounds.size` — same no-op reasoning |
| `NorthDirectionOverride` | float, 0-360 | `0` (both) | `LevelSettings._northDirection` — deliberate no-op, current value already tests correct |

### §B — donor camera/preset takeover (new key)

| Key | Type | Default (Bay5 / other maps) | Effect |
|---|---|---|---|
| `UseDonorCameraPrefab` | bool | `true` / `false` | Assigns the harvested donor's `CameraPrefab`/`PrismPresetPrefab`/`PostProcessProfilePrefab` onto this raid's `LevelSettings`; false = always Cam2 |

### §C — Tier-2 wetness mitigations (new/renamed keys; all logged on apply)

| Section | Key | Type | Default | Effect |
|---|---|---|---|---|
| `MaterialFixes` | `GroundMaterialWetnessFixEnabled` | bool | **false** (lead decision, §10) | Restores the `_BlendStrength`/smoothness fix on Bay5's ground material (§C.1) |
| `MaterialFixes` | `GroundSmoothnessMultiplier` | float | 0.6 | Multiplier applied when the above is on |
| `Decals` | `HideProjectorDecals` | bool | **false** | Unchanged diagnostic switch (§10) |
| `Wetness` | `WetRendererIntensityMultiplier` | float, 0-1 | **1.0 (off)** | Scales `RainController._wetRenderer.Intensity` every frame it ramps — §C.3, least-tested |
| `Wetness` | `MaxLyingWaterOverrideEnabled` | bool | **false** | Enables the puddle-height ceiling clamp — §C.4 |
| `Wetness` | `MaxLyingWaterOverride` | float, 0-0.95 | 0.6 | Replacement `PuddleWaterLevel.y` when the above is on |

### §D — deterministic test weather (new section `Weather (test)`)

| Key | Type | Default | Effect |
|---|---|---|---|
| `Weather` | enum (`Server`/`Clear`/`Overcast`/`Rain`/`Fog`) | `Server` (no change) | Forces a deterministic weather via a postfix on `WeatherController.SetWeatherNodes`, reapplied on every call (including SPT's own PVE_OFFLINE server re-sync) |

All new keys use NEW names throughout (never reusing an old key verbatim with a changed default) per
the project's hard rule: BepInEx persists a saved config value under its old key even after the code
default changes, so a stale value from an earlier build could otherwise silently resurrect behavior that
no longer means what it used to.

---

## 5) Diagnostics and how to read them

Every apply/skip is logged; a single `LogOutput.log` read (after a raid) answers "what ran, with what
values":

- `[ArenaMapPort][WeatherRig] harvested '<donor>' (...) ... donor camera captured=<bool> (name='...')
  prism=<bool> postfx=<bool>` — once, at harvest time (main menu). `captured=false` means
  `UseDonorCameraPrefab` will fall back to Cam2 regardless of its own value.
- `[ArenaMapPort][LevelSettingsOverride] map=... ZeroLevel=... (was ...) HeightFalloff=... (was ...)
  TextureBudget=... (was ..., now ...) RainBounds=... NorthDirection=<unchanged|value>` — once per raid,
  right after rig activation. Confirms §A applied and with what numbers.
- `[ArenaMapPort][CameraPrefabTakeover] assigned donor camera for map=... camera='...' prism=<bool>
  postfx=<bool>` — confirms §B assigned successfully. Its ABSENCE (or an info line saying "no donor
  CameraPrefab") means the Cam2 fallback is silently in use — check the visuals accordingly.
- `[ArenaMapPort][Camera] ... source=donor (already present, no changes made)` vs.
  `source=fallback (added)` for both the exposure fix and `TOD_Scattering` — confirms §B.4's no-double-
  apply guard took the expected branch for this raid's actual camera.
- `[ArenaMapPort][MaterialFixes] '<hash>' (Bay5_Vertex_Paint_Ground): _BlendStrength ... _Smoothness0-2/
  _FresnelSpecular2 scaled by ...` — §C.1, once per material rebind pass.
- `[ArenaMapPort][Decals] pass complete — N/M renderer(s) disabled` — §C.2, unchanged log line, now firing
  by default.
- `[ArenaMapPort][WetRendererDamper] intensity scaled by x...` — §C.3, logged once per SESSION (not per
  raid — matches this codebase's existing `_logged`-once convention for hot per-frame hooks), only when
  `WetRendererIntensityMultiplier < 1.0`.
- `[ArenaMapPort][RainClamp] map=... MaxLyingWater clamped 0.95 -> 0.60` — §C.4, once per raid activation,
  only when `MaxLyingWaterOverrideEnabled=true`.
- `[ArenaMapPort][WeatherTestOverride] applied '<mode>' (map=...)` — §D. Per the plan's own reasoning,
  this line should appear **twice** per raid when active (once from `InitWeather`'s own call, once from
  `vmethod_5`'s PVE_OFFLINE server re-sync) — that duplication is the direct, observable proof the
  override actually beats the two-call-site race; a single occurrence followed by drift back toward
  server weather would mean one call site was missed.
- `LightingDiagnosticProbe`'s `-- weather rig --` block (once per raid, ~5s after `GameStatus.Started`)
  now additionally prints: live `LevelSettingsOverride` readback, `CameraPrefabTakeover` state (three
  booleans that should read true together for the donor path to actually be live), the full §C
  wetness-mitigation config summary, and live `PuddleWaterLevel`/`WetRenderer.Intensity` readback.
- `DaytimeVisualsProbe`'s consolidated block now also prints the configured `Weather (test)` mode.
- A no-rig map (`arena_equator`) should show **none** of the `LevelSettingsOverride`/
  `CameraPrefabTakeover`/`WeatherTestOverride` lines at all in the same session — proves the gates
  correctly scope to rig-active maps only.

---

## 6) Test instructions (adapted from the plan's §F — one session, two-plus raids)

**Raid 1 — `Weather (test) = Clear`, defaults otherwise.** Checks the "dry" baseline is sane once §A/§B
land, with the rain/wetness pipeline (§C) mostly out of the picture:
1. Confirm `[ArenaMapPort][LevelSettingsOverride]` shows the NEW numbers (`ZeroLevel=-8.20`, not
   `-78.64`; `HeightFalloff=0.025`, not `0.029`).
2. Confirm `[ArenaMapPort][CameraPrefabTakeover] assigned donor camera ... prism=... postfx=...` fired —
   if absent, check the harvest log line for `donor camera captured=false` (harvest problem) or check
   `UseDonorCameraPrefab` wasn't turned off.
3. Confirm `[ArenaMapPort][Camera] ... source=donor (already present, no changes made)` for BOTH
   the exposure fix and `TOD_Scattering` — proves the donor camera path was taken this raid, not the
   Cam2-fallback path.
4. Confirm exactly TWO `[ArenaMapPort][WeatherTestOverride] applied 'Clear'` lines.
5. Visual check: horizon/zenith brightness split should be far less extreme than the 2026-09-17 playtest;
   ground should show no organic white/black patches under Clear weather. If the ground is STILL wrong
   under Clear, §C.1/§C.2 (material/decal) are implicated independent of rain — worth toggling on for
   raid 2 below even though they already default on.

**Raid 2 — `Weather (test) = Rain`, defaults otherwise.** Checks the worst-case wet-ground symptom at
maximum deterministic rain (`WeatherCurve.Rain` pinned at 1.0):
1. Same four checks as raid 1 (stable across weather presets).
2. Judge wetness at a FIXED elapsed raid time (recommend ~60s after spawn) — `RainController.Wetting`
   ramps in over real time, so "still white immediately at spawn" is expected, not diagnostic.
3. If the ground is still wrong, try enabling `Wetness/WetRendererIntensityMultiplier` (e.g. 0.5) and/or
   `Wetness/MaxLyingWaterOverrideEnabled=true` for a THIRD raid, one at a time, to isolate which
   mitigation (if any) actually matters. `MaterialFixes/GroundMaterialWetnessFixEnabled` is the fourth
   switch to try (off by default, §10).

**Regression check (either raid, or a third).** Play `arena_equator` in the same session:
`LevelSettingsOverride`/`CameraPrefabTakeover`/`WeatherTestOverride` log lines should be completely
absent, and the map should look pixel-for-pixel like it did before this change.

---

## 7) Deferred (unchanged from the plan's §E, plus one addition from §3.5 above)

- Authoring real per-map `LevelSettings` values (Editor-side) instead of runtime config overrides.
- Authoring a real `WeatherObstacle` per map so `DepthPhotograper` gets genuine cover data — §C.4's
  clamp is the client-only stopgap.
- Compiled-shader disassembly of `Hidden/Time of Day/Scattering` and
  `Decal/Ultra Deferred Decal Of God 3000`'s real deferred-compositing pass.
- Investigating official BSG Arena-specific camera prefabs (`Cam2_ARENA`/`Cam2_ARENA_FIX`/
  `Cam2_ARENA_2/3`) as a possible better-fit donor than Reserve's tactical-raid camera.
- `WeatherEventController`/`SetWeatherForce` (Halloween-event weather path) is not intercepted by §D.
- Real per-map `RainBounds` measurement from actual Bay5 terrain data.
- `Camera.farClipPlane`/`GlobalFog.height`/`heightDensity` diagnostic logging and a `ReflectionProbe`
  mode/size/boxSize dump (plan's own "cheap, worth doing" diagnostics, not attempted this pass — see §3
  point 5).
- `TextureBudgetTier.CustomsTier`/`LaboratoryTier` — enum values exist but their real `MaxMapTextureMemory`
  numbers haven't been measured yet; selecting either falls back to `Stub` with a log warning.

---

## 8) Build

```
dotnet build D:\ArenaMapPort\ArenaMapPort.Client\ArenaMapPort.Client.csproj -c Release -p:ArenaMapPortDllOnly=true
```
Clean rebuild (removed `bin`/`obj` first): **0 warnings, 0 errors.**

dist DLL hash (SHA-256) == bin DLL hash, both:
`7f53521c1196bed59edd62625823aff1ebeeb53a8782d2c4888fae79e17a7722`

---

## 9) Review fix-ups

**#1 — §C.4 `ApplyPuddleClamp` never restored `RainController.PuddleWaterLevel` when
`MaxLyingWaterOverrideEnabled` was toggled back off (medium-high, confirmed).**

Evidence confirmed by reading `RainWetnessRepairs.cs` and `WeatherRig.cs`:
- `RainController.PuddleWaterLevel` is a plain public `Vector2` field, initialized once to
  `(0, 0.95)` (`_decompiled/RainController.cs:417`), with no other in-game writer.
- `ApplyPuddleClamp`'s disabled branch was a bare early return — it wrote nothing back.
- The `RainController` it mutates lives on `WeatherRig`'s harvested rig clone, which is only ever
  `SetActive(true)`/`SetActive(false)`'d, never `Destroy()`'d, and is reused for every raid played on
  the same donor for the rest of the session (`OnSceneUnloaded`: `rig.Root.SetActive(false); // NOT
  Destroy — reused next raid`).

Net effect: raid 1 with `MaxLyingWaterOverrideEnabled=true` clamps `PuddleWaterLevel.y` to (e.g.) 0.6;
raid 2 in the same session with the config set back to `false` left `PuddleWaterLevel.y` stuck at 0.6
instead of the vanilla 0.95 — directly undermining the A/B playtest workflow this same doc's §6
recommends for isolating which §C mitigation matters.

Fix applied:
- `WeatherRig.HarvestedRig` gained a new field, `OriginalPuddleWaterLevelY`, captured once at harvest
  time (from the clone's own `RainController.PuddleWaterLevel.y`, before any raid can mutate it;
  defaults to the vanilla constant `0.95f` defensively).
- `RainWetnessRepairs.ApplyPuddleClamp` gained a third parameter, `originalPuddleWaterLevelY`, and no
  longer early-returns on the disabled path — it now always computes a target
  (`enabled ? MaxLyingWaterOverride : originalPuddleWaterLevelY`) and writes it, restoring the
  harvest-time original the first time the override is turned back off. A same-value guard (`before.y
  == target`) skips the redundant write/log on every later raid once the field is already at rest,
  so steady-state behavior (override left off, or left on at the same value) is unchanged.
- `WeatherRig.OnSceneLoaded`'s call site now passes `rig.OriginalPuddleWaterLevelY` alongside
  `rig.Controller`.
- New log line for the restore path: `[ArenaMapPort][RainClamp] map=... MaxLyingWater restored
  ... -> ... (override disabled)`, distinct from the existing "clamped" line, so a restore is
  visible in `LogOutput.log` and not silently indistinguishable from "no clamp ever applied".

Files touched: `ArenaMapPort.Client/WeatherRig.cs` (`HarvestedRig.OriginalPuddleWaterLevelY` field +
its capture at harvest time + the `OnSceneLoaded` call-site update), `ArenaMapPort.Client/
RainWetnessRepairs.cs` (`ApplyPuddleClamp` signature + always-write body, doc comments).

### Build (after fix-up #1)

```
dotnet build D:\ArenaMapPort\ArenaMapPort.Client\ArenaMapPort.Client.csproj -c Release -p:ArenaMapPortDllOnly=true
```
Clean rebuild (removed `bin`/`obj` first): **0 warnings, 0 errors.**

dist DLL hash (SHA-256) == bin DLL hash, both:
`4a949d5f84b591abe39c126bf2e9e4e9599777083972bd66e4e32560427f013f`

## 10) Lead decisions after the workflow

- **Decal hiding stays off, key unchanged.** The 14:54 test hid every all-slot decal renderer (and more)
  and the ground looked the same; that part of the test does not depend on the sun direction. The
  16:57 overview screenshot shows Bay5's floor mesh decals (cracks, potholes, asphalt patches —
  textures `65741a60a45ae3bd`, `27b58747eb0bf287`, `16843b42da911ff6`) rendering alpha-shaped, not as
  opaque boxes. Hiding them removes legitimate detail.
- **Ground material tweak is off by default.** Arena renders the same material with the same shader
  correctly, so the values fit a correct lighting environment. This pass tests the vanilla-consistent
  set (rig + donor camera + donor level values) on its own; the tweak remains a comparison switch.
- **Still open:** Arena's reflection probes are skipped at import (`extract_equator.py`,
  `"ReflectionProbe": skip_import`), so smooth surfaces (ground smoothness 0.90-0.98) reflect the sky
  cubemap instead of their surroundings. If the ground stays too bright with the donor camera, porting
  the probes (realtime probes at the Arena positions, or an editor bake) is the next candidate.

## 11) Lighting Lab (2026-09-18)

Role: builder. Task: build a "Lighting Lab" — live F12 toggles, one per candidate mechanism — so a
tester can isolate which one causes the grazing-angle white/veil ground bug the 2026-09-18 06:19/06:23
playtest screenshots confirm (`analysis/screenshots/clear-spiegelung1-2.png`,
`regen-spiegelung1-2.png`): from a steep angle the yard is dark matte asphalt with a genuine mirror
puddle reflecting the brick building; from a grazing angle (same yard, opposite direction) the *entire*
ground is a blown-out white/grey veil with no discernible mirror at all — nearly identical between Clear
and Rain (including at sun intensity 0 in Rain/fog), the signature of a Fresnel-driven specular/
reflection term reading a bright, non-black environment rather than sun glint or rain. Verified directly
by opening the four screenshots this pass: the described bug is exactly what they show.

**Result: implemented and built (0 new warnings), dist DLL hash confirmed == bin DLL hash. Not yet
playtested — this pass is diagnostic tooling, not a fix.** Client-only (`ArenaMapPort.Client`). No git
commit made (workspace rule: the lead commits).

### 11.1) Files changed

- **New: `ArenaMapPort.Client/LightingLab.cs`** — all seven candidates (A-G), their config, per-map
  captured baselines/material cache, and the F9 snapshot hotkey. See the file's own class doc comment
  for the full per-candidate rationale and three deliberate adjustments from the implementation spec
  this file was built from (noted there, summarized in §11.3 below).
- **`ArenaMapPort.Client/HarmonyPatches.cs`** — two new installers, `InstallLightingLabReflectionOverride`
  (postfix on `AmbientLight.method_2`, candidate B) and `InstallLightingLabWaterLevelOverride` (postfix on
  `RainController.AbstractState.vmethod_15`, candidate D), following the file's existing
  `AccessTools.Method` + `Verify()` convention, called from `InstallAll` right after
  `InstallWetRendererDamper`/`InstallWeatherTestOverride`.
- **`ArenaMapPort.Client/RainWetnessRepairs.cs`** — `Postfix` gained one added check
  (`LightingLab.IsRainOffForCurrentMap()`) ahead of its own existing `WetRendererIntensityMultiplier`
  logic: candidate D's `RainOff` forces `wet.Intensity=0` and wins over the multiplier. A small, additive
  change to this already-reviewed file rather than a duplicate Harmony hook on the same target.
- **`ArenaMapPort.Client/Plugin.cs`** — `LightingLab.Install()` added right after
  `ArenaLightingRepairs.Install(); LightingDiagnosticProbe.Install();` and before `MapBorderRepairs.Install()`
  — after both WeatherRig and ArenaLightingRepairs (subscription-order requirement, same reasoning
  WeatherRig.cs's own doc comment already documents for itself vs. ArenaLightingRepairs), before
  SceneInspector.

### 11.2) Decompile findings this pass relied on (re-verified, not re-copied from the task brief)

All paths under `D:/SPT41/Development/_decompiled/`.

- `AmbientLight.cs`: `RenderDelay` (line 127, default 0.05f), `method_2()` (line 389) — called every
  `LateUpdate` (line 293/294) but only actually re-renders/re-binds `_MyGlobalReflectionProbe` on frames
  its own throttle allows (line 391); a Harmony **postfix** still runs on every call regardless (postfixes
  run after the whole method executes, including an early return inside it), so overriding the global
  texture in the postfix wins every frame irrespective of `RenderDelay`. `method_2` is `public`, not
  `private` as the task brief's own decompile citation assumed — resolves identically either way via
  `AccessTools.Method`, no code difference, noted for the record. `ReflectionIntensity`/
  `SetReflectionIntensity` (lines 123, 237-241) confirmed public, and `SetReflectionIntensity` calls
  `method_3()` immediately — sticks live with zero Harmony needed. `_MySimpleGlobalReflectionProbe`
  (`EFT.Weather/TODSkySimple.cs:29,237`) confirmed written only by a Simplified rig's own
  `TODSkySimple.Initialize()` — never bound during Bay5's Full-rig raid, not a factor here.
- `LevelSettings.cs`: `ApplySettings()` (line 194) confirmed the only place `_SSRFactor` (line 217,
  `Shader.SetGlobalFloat`) is ever written anywhere in the whole decompile (grepped the full tree). Field
  defaults re-verified: `SkyColor=Color.black` (line 35), `EquatorColor=GroundColor=new
  Color(0.6039216f, 54f/85f, 0.6509804f)` (lines 37/39 — literally the untouched C# class defaults, since
  Reserve never overrides them), `AmbientIntensity` uninitialized float default 0 (line 41).
  `OnPreCullCallback` (line 240) re-applies `RenderSettings.ambient*` FROM these `LevelSettings` fields
  every camera pre-cull — confirms every A-candidate write must go through `LevelSettings`, never
  `RenderSettings` directly (a direct write would not survive the next pre-cull).
- `RainController.cs`: `AbstractState.vmethod_15` (line 142, `public virtual`) writes `_WaterLevel` via
  `Mathf.LerpUnclamped(PuddleWaterLevel.x, PuddleWaterLevel.y, laingWater)` (line 146).
  `AbstractStateSummer.vmethod_15` (line 214) explicitly calls `base.vmethod_15(...)` first (line 216, a
  non-virtual base-call) — confirms patching the BASE method body (`RainController.AbstractState`, not
  `AbstractStateSummer`) catches every season state, since no `AbstractStateWinter` subclass overrides it
  either. `AbstractState.Controller` (line 37) confirmed `public { get; set; }`.
- `WetRenderer.cs`: `Update()` (its own `MonoBehaviour.Update`, unconditional) pushes
  `Shader.SetGlobalFloat("_SurfaceWetIntensity", Intensity)` every frame on its own, independent of
  whoever last set the `Intensity` field — confirms the existing `WetRendererIntensityMultiplier` knob
  (`RainWetnessRepairs.cs`) already covers the "SurfaceWetIntensity multiplier" this task's own goal
  section asked for; not duplicated.
- `TOD_Sky.cs` / `TOD_DayParameters.cs`: `Day`/`Night` (lines 30/32, plain public fields),
  `TOD_DayParameters.LightIntensity` (line 85, plain public `float` on a plain `[Serializable]` non-Unity-
  object class — freely mutable, no reflection needed). `LightIntensity` (property, line 173,
  `Components.LightSource.intensity`) confirmed the correct live readback.

### 11.3) Where this pass deviates from the implementation spec it was built from

1. **F9's `SnapshotHotkey` — one global `ConfigEntry`, not one per map.** The spec's own pseudocode put it
   inside each map's per-map `LabConfigBundle`. Implemented instead as a single entry under section
   `"LightingLab"` (no map suffix), matching `SceneInspector.cs`'s own F10 hotkey convention exactly — only
   one map's raid can ever be loaded at a time regardless, so N per-map copies of the identical binding
   would only add confusion (which one is "live"?) with no benefit.
2. **`WeatherRig.ActiveRig`/`WeatherRig.ActiveRig.Sky` accessed directly instead of a hypothetical
   `rig.Ambient` field.** `WeatherRig.HarvestedRig` has no `Ambient` field; the spec's own pseudocode named
   one that doesn't exist. The real accessor, confirmed against `ToDController.cs:165`
   (`public AmbientLight AmbientLightScript;`), is
   `WeatherRig.ActiveRig?.Controller?.TimeOfDayController?.AmbientLightScript`.
3. **"CaptureSunBaseline" folded directly into `ApplySunIntensity`** (captures lazily on the first call
   this raid) rather than kept as its own separate method — identical information, one fewer moving part.
4. **`WeatherRig.OnSceneUnloaded` runs BEFORE `LightingLab.OnSceneUnloaded` in the same dispatch**
   (`WeatherRig.Install()` subscribes first) and already clears `WeatherRig.ActiveRig`/`_activeRig` by the
   time `LightingLab.OnSceneUnloaded` runs — so candidates B and F cache their own direct component
   references (`LabState.CachedAmbient`/`CachedSky`) at apply time instead of re-reading
   `WeatherRig.ActiveRig` at unload time, which would already be null. Not a spec contradiction, just a
   detail the spec's pseudocode didn't spell out.
5. **"No per-frame cost when every lab value is at its default" interpreted as "no allocation/scan cost",
   not literally zero CPU instructions** — the two Harmony postfixes (candidates B/D) still do the
   project's own mandatory `ArenaMapRaid.IsLoaded() || !WeatherRig.IsActiveForCurrentMap()` gate check
   every frame they're called, same as every other per-frame Harmony postfix already in this codebase
   (`RainWetnessRepairs.Postfix`, `WeatherTestOverride.Postfix`) — matches the spec's own more precise
   framing elsewhere ("genuinely zero extra cost beyond one enum comparison per call when the Lab is
   untouched").

### 11.4) Config keys (all new; section `LightingLab (<map.Key>)` per map, plus one global `LightingLab`
section for the F9 hotkey — deliberately separate from `Lighting (<map.Key>)`, WeatherRig/
ArenaLightingRepairs' own section, so a key-name collision is structurally impossible)

**Superseded by §12 (2026-09-18):** the playtest below confirmed Candidate A=`DonorBlack` fixes the
outdoor veil, so §12 moved that write into `WeatherRig` as the new default behavior (key renamed
`AmbientPreset`→`AmbientOverride`, enum `Stub`→`Off` as the new true-no-op default, `StubGrey` added) —
see §12 for the current key table. The row below documents what candidate A meant AT THE TIME this Lab
was built, for historical accuracy; it is no longer the live key name.

| Candidate | Key | Type | Default | Effect |
|---|---|---|---|---|
| A | `AmbientPreset` | enum (`Stub`/`DonorBlack`/`Custom`) | `Stub` | Written into live `LevelSettings.SkyColor/EquatorColor/GroundColor/AmbientIntensity` + `ApplySettings()` + `DynamicGI.UpdateEnvironment()`. `Stub` = today's behavior untouched. |
| A | `AmbientCustomColor` | Color | `(0.5,0.5,0.5,1)` | Sky/Equator/Ground used when `AmbientPreset=Custom`. |
| A | `AmbientCustomIntensity` | float, 0-4 | `1.0` | `LevelSettings.AmbientIntensity` used when `AmbientPreset=Custom` (inert under `AmbientMode.Flat`). |
| B | `GlobalReflection` | enum (`Rig`/`Black`/`Grey`/`OffStrength`) | `Rig` | `Rig` = today's behavior. `Black`/`Grey` override `_MyGlobalReflectionProbe` every frame with a tiny generated Cubemap. `OffStrength` zeroes `AmbientLight.ReflectionIntensity` directly (a narrower, second lever). |
| C | `SSRFactor` | float, 0-1 | `1.0` | Live `Shader.SetGlobalFloat("_SSRFactor", ...)`. `1.0` = authored no-op. |
| D | `WaterLevelOverrideEnabled` | bool | `false` | Hard-overrides `_WaterLevel` every frame (Harmony postfix), independent of the existing `Wetness/MaxLyingWaterOverride*` ceiling clamp. |
| D | `WaterLevelOverrideValue` | float, 0-0.95 | `0.6` | Replacement `_WaterLevel` value when the above is enabled. |
| D | `RainOff` | bool | `false` | Forces `_WaterLevel=0` (wins over `WaterLevelOverrideEnabled`) and `RainController._wetRenderer.Intensity=0`. Does NOT touch rain VFX. |
| E | `GroundSmoothnessMultiplier` | float, 0-2 | `1.0` | Live multiplier on every discovered Float/Range shader property named `*smooth*`/`*gloss*`/`*shin*` on materials matched by shader name (`Custom/Vert Paint Shader Solid` or `p0/Reflective*`) AND renderer name (`*floor*`/`*ground*`/`*asphalt*`). Scanned once, 5s after scene load. |
| F | `SunIntensityMultiplier` | float, 0-4 | `1.0` | Multiplies the rig's own `TOD_Sky.Day/Night.LightIntensity` (captured once per raid as baseline). Only has an effect while a Full rig is active. |
| G | `SnapshotHotkey` (global section `LightingLab`) | KeyboardShortcut | `F9` | Logs one `[LightingLab][Snapshot]` block — see §11.5. |

Existing keys this pass **reuses, does not duplicate**: `Wetness/WetRendererIntensityMultiplier` and
`Wetness/MaxLyingWaterOverride*` (`RainWetnessRepairs.cs`) already cover "SurfaceWetIntensity multiplier"
and the puddle-ceiling clamp respectively — see §11.2's `WetRenderer.cs` finding.

**All defaults equal today's exact behavior** (`Stub`/`Rig`/`1.0`/`false`/`1.0`/`1.0`) — this pass writes
nothing different from what `WeatherRig`/`ArenaLightingRepairs`/`RainWetnessRepairs` already do until a
tester flips one value live via F12.

### 11.5) F9 snapshot format

One `[ArenaMapPort][LightingLab][Snapshot]` block, tag `grep '\[LightingLab\]\[Snapshot\]'`:

```
[ArenaMapPort][LightingLab][Snapshot] ==== arena_bay5 ====
  camera: pos=(x, y, z) fwd=(x, y, z)
  A UnityAmbient: preset=... custom=(RGBA(...), intensity=...)
  B GlobalReflection: preset=...
  C SSRFactor: configured=... live=...
  D Wetness: WaterLevelOverrideEnabled=... WaterLevelOverrideValue=... RainOff=... live _WaterLevel=... live _SurfaceWetIntensity=...
  E GroundSmoothnessMultiplier: configured=... cachedMaterials=N
  F SunIntensityMultiplier: configured=...
  RenderSettings: ambientMode=... ambientLight=RGBA(...) ambientIntensity=... ambientEquatorColor=... ambientGroundColor=...
  G reflection-probe faces (type=... size=WxH mip=m/M): PositiveX=RGBA(...) NegativeX=RGBA(...) PositiveY=RGBA(...) NegativeY=RGBA(...) PositiveZ=RGBA(...) NegativeZ=RGBA(...)
```

The last line falls back to a "not feasible, logging metadata only" message (type/format/size) if the
bound texture isn't Cube-dimension, isn't yet created this raid, or the `Graphics.CopyTexture` readback
throws — per the task's own escape hatch.

### 11.6) Test procedure

1. Load `arena_bay5`, stand at the spot `clear-spiegelung2.png` was taken from (grazing angle, looking
   toward the fence/gate, ground filling most of the frame) — this is the worst-case view of the veil.
2. Open the F12 configuration manager, find the `LightingLab (arena_bay5)` section(s).
3. Flip **one** toggle at a time, in this order (cheapest/most isolating first, matching the spec's own
   recommendation):
   - `AmbientPreset = DonorBlack` alone first — isolates the implicit Unity ambient-derived reflection
     path (no `ReflectionProbe` exists anywhere in these scenes).
   - `GlobalReflection = Black` alone next (after reverting A back to `Stub`) — isolates
     `_MyGlobalReflectionProbe` specifically.
   - Only combine A+B if neither alone kills the veil.
   - Then, one at a time as needed: `GlobalReflection = OffStrength`, `SSRFactor = 0`,
     `GroundSmoothnessMultiplier` lowered (e.g. `0.3`), `RainOff = true` (rule out wetness even though the
     screenshots already show the bug present at sun intensity 0 in Rain).
4. After each flip, press **F9** and note the resulting snapshot block — in particular whether the veil
   visually disappeared AND which line changed.
5. Revert the toggle back to its default before trying the next candidate, so results don't compound.
6. Report back: which single toggle (or combination) removes the white veil while leaving the correct
   dark-asphalt-with-mirror-puddle look (as in `clear-spiegelung1.png`) intact from both camera angles.

### 11.7) Build

```
dotnet build D:\ArenaMapPort\ArenaMapPort.Client\ArenaMapPort.Client.csproj -c Release -p:ArenaMapPortDllOnly=true
```
Clean rebuild (removed `bin`/`obj` first): **0 warnings, 0 errors.**

dist DLL hash (SHA-256) == bin DLL hash, both:
`1e9f1eeed8433c83d15553d7ece6f12eff50b69d0b1e049e13c70d2c1f4daa7c`

No `tools/`, `Server`, or `base.json` file changed — zero receipts invalidated. No git commit made
(workspace rule unchanged: the lead commits).

### 11.8) Review fix-ups

**#1 — `ApplyAmbient`'s `ls.ApplySettings()` call silently clobbers Candidate C's (and D's) live shader-
global overrides (High, confirmed).**

`LevelSettings.ApplySettings()` doesn't just push `RenderSettings.ambient*` — it also unconditionally
re-asserts four `Shader.SetGlobal*` process-wide globals from `LevelSettings`' own fields, not from
whatever was most recently written live (`LevelSettings.cs:213-217`: `_DirectionLightShadow`,
`_WaterLevel` ← `LevelSettings.LyingWater`, default `0`; `_MinAmbientColor`; `_SSRFactor` ←
`LevelSettings.SSRFactor`, default `1`). `ApplyAmbient` calls `ls.ApplySettings()` not just once at raid
start but on every live F12 change to `AmbientPreset`/`AmbientCustomColor`/`AmbientCustomIntensity`
(`SettingChanged` bindings), including §11.6 step 5's own "revert to default before trying the next
candidate" instruction — so a tester who sets `SSRFactor` (Candidate C) away from `1.0` and then reverts
`AmbientPreset` back to `Stub` silently resets the live `_SSRFactor` shader global back to `1.0` while
`SSRFactorOverride`'s own config value (and the F9 snapshot's `configured=` field) still reads the
tester's chosen value — config and rendered state silently disagree, undermining the exact "isolate which
single toggle works" methodology this whole tool exists for. `WeatherRig.RenderSettingsSnapshot`
(`WeatherRig.cs:746-819`) already exists specifically to bracket this same set of globals around
`WeatherRig`'s own one-shot `ApplySettings()` call at harvest time; `ApplyAmbient` didn't apply the same
bracket.

Fix applied: `ApplyAmbient` now snapshots `Shader.GetGlobalFloat`/`GetGlobalColor` for all four globals
(`_SSRFactor`, `_WaterLevel`, `_DirectionLightShadow`, `_MinAmbientColor`; two new cached property IDs,
`_directionLightShadowId`/`_minAmbientColorId`, added alongside the existing `_ssrFactorId`/
`_waterLevelId`) immediately before `ls.ApplySettings()`, and writes them straight back immediately
after — same bracket pattern `RenderSettingsSnapshot` already uses, scoped to just the four globals (not
`RenderSettings.ambient*` itself, which `ApplyAmbient` is deliberately changing). Only runs on the already-
gated non-Stub-or-previously-applied path, so no new per-frame cost at Lab defaults.

**#2 — Candidate D's live `_WaterLevel` override had no explicit restore on scene unload, unlike
Candidate C's explicit reset (Low, confirmed).**

`OnSceneUnloaded` explicitly reset Candidate C (`Shader.SetGlobalFloat(_ssrFactorId, 1f)`) and its own
summary log line claimed a complete restore ("restored SSRFactor=1.00, ... N ground material(s)"), but
`_WaterLevel` — the same kind of process-wide global, written by the same `WaterLevelPostfix` mechanism —
was absent from both. Low-impact in practice: `RainController.AbstractState.vmethod_15`
(`RainController.cs:142-147`, driven every weather `Update` via `WeatherController.ControlRain`)
recomputes `_WaterLevel` from `PuddleWaterLevel`/`laingWater` unconditionally on the very next frame of
ANY raid, vanilla or ported, so a stale value from a `WaterLevelOverrideEnabled=true`/`RainOff=true`
session self-heals almost immediately regardless.

Fix applied: `OnSceneUnloaded` now also does `Shader.SetGlobalFloat(_waterLevelId, 0f)` (mirroring
`LevelSettings.LyingWater`'s own compiled field default, the same convention the SSRFactor reset to `1`
already uses for `LevelSettings.SSRFactor`'s default), and the summary log line now lists
`WaterLevel=0.00` alongside `SSRFactor=1.00` so the log's claim matches what the method actually restores.

Files touched: `ArenaMapPort.Client/LightingLab.cs` only (`_directionLightShadowId`/`_minAmbientColorId`
fields, the snapshot/restore bracket in `ApplyAmbient`, the `_WaterLevel` reset + log-line update in
`OnSceneUnloaded`).

### Build (after review fix-ups)

```
dotnet build D:\ArenaMapPort\ArenaMapPort.Client\ArenaMapPort.Client.csproj -c Release -p:ArenaMapPortDllOnly=true
```
Clean rebuild (removed `bin`/`obj` first): **0 warnings, 0 errors.**

dist DLL hash (SHA-256) == bin DLL hash, both:
`0bf2843c8ec5759d194f8781d422ec824d6d05c16973c0f4c72f59e0b6879415`

No `tools/`, `Server`, or `base.json` file changed — zero receipts invalidated. No git commit made
(workspace rule unchanged: the lead commits).

---

## 12) Ambient and rain cover fix (2026-09-18)

Role: builder. Task: implement the §11.6 playtest's own findings — the Lighting Lab isolated the exact
mechanism behind the grazing-angle white ground veil (outdoors) and a second, structurally different veil
(indoors) — as the new default behavior, client-only (`ArenaMapPort.Client`). No git commit made
(workspace rule unchanged: the lead commits).

### 12.1) Evidence — 2026-09-18 14:52-14:59 playtest

User playtest, log `D:\SPT41vanilla\BepInEx\LogOutput.log` lines 1474-1760, screenshots
`analysis/screenshots/spiegelung2.png`, `spiegelung-innen1.png`, `spiegelung-innen2.png`:

- **Outdoors** the white ground veil disappears with LightingLab `AmbientPreset=DonorBlack` (as it was
  named before this pass — see §11.4's own note above). The yard then looks like vanilla wet asphalt with
  correct puddle reflections. Cause: Unity's flat ambient (and the default environment reflection derived
  from it with no `ReflectionProbe` anywhere in these scenes) — vanilla maps run with a black Unity
  ambient because EFT's own `AmbientLight`/rig provides the real ambient on top; our stub ships 0.5 grey /
  intensity 1 instead.
- **Indoors** (warehouse) the veil stayed with `AmbientPreset=DonorBlack` and only disappeared with
  `GlobalReflection=Black` (Candidate B): the indoor floor read fully wet (F9 snapshot: live
  `_WaterLevel=0.76` while standing indoors) and mirrored the bright sky cubemap. Root cause: on ported
  maps the rain cover system has no data. `DepthPhotograper.Render()` (decompile
  `D:/SPT41/Development/_decompiled/DepthPhotograper.cs`, called from
  `RainController.AbstractState.CameraChanged` on every camera change — `RainController.Awake()`
  subscribes `CameraManager.Instance.OnCameraChanged` directly, a separate subscription from
  `WeatherController.Awake()`'s own) photographs a top-down orthographic depth of ONLY layer "Terrain"
  plus `WeatherObstacle.Instance`'s combined `DryPlane` mesh, drawn in black onto that same layer. Our
  maps author no `WeatherObstacle`/`DryPlane` and no Terrain-layer geometry, so the depth map comes back
  empty and every point reads "uncovered" — an indoor floor gets treated exactly like open sky.
  `LevelSettings.RainBounds` on our maps was the class default, center `(0,0,0)` size
  `(1000,220,1000)` (Reserve authors `(400,110,400)` at origin).
- SSR (Candidate C, `_SSRFactor`) showed no visible change in this playtest. `AmbientLight.IsSSREnable`
  reads `CameraManager.Instance.GetSSREnabled()` — EFT's own Graphics-options SSR toggle, a separate
  mechanism from `_SSRFactor` — and when it's on, `AmbientLight.method_3` pins the "Custom Ambient"
  screen-space reflection compositing term to `1.0` regardless of wetness. Whether the user's install has
  this on was not logged anywhere before this pass — §12.4 below adds it to the diagnostics.

### 12.2) Goal 1 — real ambient fix (`WeatherRig.cs`)

`WeatherRig.ApplyDonorLevelSettingsOverrides` now writes Reserve's own authored ambient
(`SkyColor=Color.black`, `EquatorColor=GroundColor=(0.6039216, 0.635294, 0.6509804)`,
`AmbientIntensity=0`) into the live `LevelSettings` before its existing `ls.ApplySettings()` call, gated
on a new per-map `UnityAmbient` key (default `Donor`). The values live in one place —
`WeatherRig.GetDonorAmbient` — and `LightingLab`'s own Candidate A now reads them from there instead of
keeping a duplicate copy.

Two things confirmed safe against the decompile before landing this:
- **`ArenaLightingRepairs` never overwrites it.** Its own `ApplyAmbient`'s `SkyColor/EquatorColor/
  GroundColor` write is gated `ownAmbientColor = !WeatherRig.IsActiveForCurrentMap()`
  (`ArenaLightingRepairs.cs`) — already false whenever this fix's write matters, both at initial
  `sceneLoaded` (it runs right after `WeatherRig` in the same dispatch — `Plugin.cs`'s install order) and
  on every later live `AmbientColorMultiplier` F12 change (same gate, re-checked every call). Its
  `OnSceneUnloaded` restore only fires if `AmbientFallbackWritten` was set, which only happens on the
  defensive `LevelSettings == null` fallback path — never true while a rig is active. **No code change
  needed in `ArenaLightingRepairs.cs`.**
- **`LevelSettings.OnPreCullCallback`** (BSG code, decompile `LevelSettings.cs:240-263`) re-pushes
  `RenderSettings.ambient*` from these exact fields every camera pre-cull whenever
  `AmbientType == AmbientType.Default` — confirmed nothing in this codebase ever sets `ls.AmbientType`
  away from its C# default, so a one-time field write here is durable for the whole raid with zero extra
  per-frame work of our own.

`ApplySettings()` (decompile `LevelSettings.cs:194-218`) also unconditionally re-asserts four
`Shader.SetGlobal*` process-wide globals (`_SSRFactor`/`_WaterLevel`/`_DirectionLightShadow`/
`_MinAmbientColor`) from `LevelSettings`' own fields — the same globals `LightingLab.ApplyAmbient`'s own
review fix-up #1 (§11.8) already brackets around its own `ApplySettings()` call. This pass adds the same
narrow bracket (4 cached `Shader.PropertyToID`s, capture-before/restore-after) around
`ApplyDonorLevelSettingsOverrides`'s call too, so a live LightingLab Candidate C/D override can't be
silently clobbered by this fix. **Deliberately not the full `RenderSettingsSnapshot` struct** — that
would also reset `RenderSettings.ambient*` back to its pre-write value on `Restore()`, undoing the very
ambient write this goal makes; `RenderSettingsSnapshot` stays scoped to `HarvestOne`'s own one-time
donor-scene load/unload bracket, as it already was. `DynamicGI.UpdateEnvironment()` runs after, same
mechanism Candidate A already used.

### 12.3) Goal 2 — Lighting Lab candidate A rename (`LightingLab.cs`)

`AmbientPreset` (enum `{Stub, DonorBlack, Custom}`, key `AmbientPreset`) is now `AmbientPreset` (enum
`{Off, DonorBlack, StubGrey, Custom}`, key **`AmbientOverride`**). BepInEx `ConfigEntry<T>` serializes an
enum by name, so renaming the type is safe on its own — but the key name itself also changed, per this
project's hard rule that a key is never reused with new semantics. **`AmbientPreset` is now orphaned** in
any tester's existing `BepInEx.cfg` — harmless (BepInEx just leaves it in the file unread) but worth
knowing if grepping a config for it turns up nothing live.

`Off` replaces `Stub` as the new true no-op default: since goal 1 made `WeatherRig` own the real ambient
fix (defaulting to `Donor`), "Off" here means "don't override what that already wrote" — not "force the
stub". The existing "capture this raid's baseline on first `ApplyAmbient` call, restore it when the
preset reads Off but was previously applied" mechanism (`state.AmbientBaselineCaptured`/
`state.AmbientOverrideApplied`) already satisfies "switching back to Off live re-applies WeatherRig's own
values" with **no new code** — the baseline is captured from the live `LevelSettings` fields the first
time `ApplyAmbient` runs each raid, which is strictly after `WeatherRig.ApplyDonorLevelSettingsOverrides`
in the same `sceneLoaded` dispatch (confirmed install order, `Plugin.cs`), so it already equals whatever
goal 1 just wrote.

`StubGrey` is new: forces the pre-goal-1 stub (`(0.5,0.5,0.5,1)` / intensity 1, `AuthorScriptsScene.cs`'s
own authored values) even while `WeatherRig`'s own `UnityAmbient=Donor` default is active, for A/B
isolation against the new default.

### 12.4) Goal 3 — rain cover for ported maps (new `RainCoverRepairs.cs`)

New per-map key `RainCover` (enum `{RoofDepth, Off}`, default `RoofDepth`), two independent pieces both
driven once per raid from `WeatherRig.OnSceneLoaded` (`RainCoverRepairs.ApplyForCurrentMap`, called right
after `ApplyDonorLevelSettingsOverrides`, inside the same `try` block — a throw here falls back cleanly
via that method's own existing failure handling):

- **(a) `LevelSettings.RainBounds` from static geometry.** Walks every enabled `MeshRenderer` in the
  map's own owned scenes (`ArenaMapRaid.IsOwnedScene`), excluding layers `Water`/`Sky`/`LevelBorder`
  (resolved by name, never hardcoded indices — tolerant of `-1`, since this project's own `TagManager`
  leaves `LevelBorder` unnamed at layer 29), renderers under the local player's own body
  (`GetComponentInParent<EFT.Player>()`, a hierarchy check, not a layer check), and any renderer whose
  bounds extents exceed 600m (the sea placeholder plane / sky dome). Pads the result 10m horizontally,
  extends the Y range from min to max+5m, and writes it into `LevelSettings.RainBounds` — confirmed safe
  to overwrite `WeatherRig`'s own default-value write from the same dispatch: `RainBounds` is never
  referenced by `LevelSettings.ApplySettings()` (decompile `LevelSettings.cs:194-218` never mentions it),
  it is read fresh, later, directly off the live singleton by `DepthPhotograper.Render()`
  (`DepthPhotograper.cs:59`). The existing `RainBoundsCenter`/`RainBoundsSize` config keys still work as a
  manual override — if either is set away from the class default, the scan still runs (for its own log
  line) but the manual values win.
- **(b) Depth-camera `cullingMask` widening.** A Harmony postfix on `DepthPhotograper.SetupDepthCamera`
  (verified `DepthPhotograper.cs:117`, public, no args, single overload) ORs in every layer the bounds
  scan found (never narrows the original `1 << Terrain` bit) when `ArenaMapRaid.IsLoaded()` and
  `RainCover=RoofDepth`. Timing: `SetupDepthCamera()` runs inside `Render()` before
  `RenderObstacles()`/`_depthCamera.Render()` and before the camera GameObject is
  `DestroyImmediate`'d at the end of the same call, so widening the mask in this postfix is read by that
  very same render. `DepthPhotograper._depthCamera` is a private field, read via `AccessTools.Field`
  (this project's stated preference over Harmony's `___fieldName` injection, `EffectsControllerRepairs.cs`).
- **(c) A synthetic `WeatherObstacle` for `DepthPhotograper.CheckIfCameraUnderRain`** (the separate
  "rain drops on screen indoors" mechanism — a physics raycast against `WeatherObstacle.MeshCollider`,
  structurally independent of the depth-map coverage (a) and (b) target) — **deferred, not implemented
  this pass.** It is a real, separate scope item (building and keeping in sync a combined roof mesh +
  `MeshCollider` at runtime) for a purely cosmetic screen-space effect; `WeatherObstacle.Instance` stays
  null on every ported map, so `DepthPhotograper.RenderObstacles()`'s own
  `Debug.LogError("WeatherObstacle.Instance is null")` still fires once per camera-change event —
  cosmetic log noise, not a per-frame cost, left alone (a Harmony transpiler removing just that
  `ldstr`+`call Debug.LogError` IL pair is the documented, not implemented, mechanism if it's ever wanted
  — this codebase already has that exact precedent in the `QuietBrainLog-Mod` project, per the lead's own
  memory).

### 12.5) Goal 4 — diagnostics (`DaytimeVisualsProbe.cs`)

One new line, `ProbeRainCoverAndSSR`, added to the existing once-per-raid probe block:
`CameraManager.Instance.GetSSREnabled()` (EFT's own PostProcessing-v2 SSR toggle, the Graphics-options
"SSR" dropdown), `AmbientLight.IsSSREnable` (reads the SAME toggle — confirmed
`AmbientLight.cs:225-235` — and per `AmbientLight.method_3`, `AmbientLight.cs:434-438`, pins the "Custom
Ambient" screen-space reflection compositing term to `1.0` whenever it's on, independent of wetness), the
live `RainBounds`, the depth-camera `cullingMask` actually applied this raid (cached at postfix time —
`DepthPhotograper._depthCamera`'s GameObject is long destroyed by the time this once-per-raid probe runs,
seconds later), and `RenderSettings.ambient*` after every override this raid.

**If `PostFxSSR` reads `false` in this line, enable SSR in EFT's own graphics settings (the SSR
dropdown, `Off/Low/Medium/High/Ultra`) for indoor reflections to be dominated by the real screen-space
raymarch instead of the wetness-gated cubemap term** — this is well-documented standard Unity
PostProcessing-v2 SSR behavior (it raymarches against the depth/color buffer of what's actually
rendered), corroborated by but not directly provable from this decompile beyond `AmbientLight.method_3`'s
own behavior above (PPv2's own raymarch implementation is a Unity package, not part of this decompile).

### 12.6) Config keys (new/changed this pass)

| Section | Key | Type | Default | Notes |
|---|---|---|---|---|
| `Lighting (<map.Key>)` | `UnityAmbient` | enum (`Donor`/`Stub`) | `Donor` | Goal 1. Unconditional default — harmless no-op for a no-rig map (`Donor="Off"` never reaches this code). |
| `Lighting (<map.Key>)` | `RainCover` | enum (`RoofDepth`/`Off`) | `RoofDepth` | Goal 3. Same unconditional-default reasoning. |
| `LightingLab (<map.Key>)` | `AmbientOverride` (was `AmbientPreset`) | enum (`Off`/`DonorBlack`/`StubGrey`/`Custom`) | `Off` (was `Stub`) | Goal 2 rename — `AmbientPreset` orphaned. |

Every other §11.4 Lighting Lab key (`AmbientCustomColor`, `AmbientCustomIntensity`, `GlobalReflection`,
`SSRFactor`, `WaterLevelOverrideEnabled`, `WaterLevelOverrideValue`, `RainOff`,
`GroundSmoothnessMultiplier`, `SunIntensityMultiplier`, `SnapshotHotkey`) is unchanged by this pass.

### 12.7) Log lines to check

- `grep '\[LevelSettingsOverride\]'` — extended this pass: now also reports
  `UnityAmbient=<Donor|Stub> SkyColor=... (was ...) EquatorColor=... (was ...) GroundColor=... (was ...)
  AmbientIntensity=... (was ...)` alongside the existing `ZeroLevel`/`HeightFalloff`/`TextureBudget`/
  `RainBounds`/`NorthDirection` fields.
- `grep '\[RainCover\]'` — new this pass. One block per raid: `RainCover=<mode>` (or `RainCover=Off — no
  bounds/mask computed`), then either `RainBounds manual override in use ...` or `RainBounds computed from
  N renderer(s) (excluded M oversized): center=... size=...`, then `depth-camera cover layers=0x...`, then
  — once the depth camera is actually set up (first camera change) — `depth camera cullingMask=0x...
  (added 0x...)`. **This is the line with the FINAL RainBounds/mask, not `[LevelSettingsOverride]`** —
  `RainCoverRepairs` overwrites `WeatherRig`'s own default-value `RainBounds` write later in the same
  `sceneLoaded` dispatch.
- `grep '\[DaytimeVisualsProbe\] RainCover/SSR:'` — new this pass, once per raid: RainCover mode, live
  RainBounds, the applied depth-camera cullingMask, both SSR readbacks, and `RenderSettings.ambient*`.
- `grep '\[Harmony\] patched DepthPhotograper.SetupDepthCamera'` — confirms the new Harmony hook
  installed (logged once at plugin `Awake()`).

### 12.8) Test steps

1. Load `arena_bay5` (Clear weather).
2. **Outdoor** — stand at the spot `spiegelung2.png` was taken from. Expect: no white veil, correct dark
   wet-asphalt look with a genuine mirror puddle — **without touching any Lighting Lab toggle** (Lab stays
   at its own defaults, `AmbientOverride=Off`/everything else default — the fix now lives in `WeatherRig`).
3. **Indoor** — stand at the spot `spiegelung-innen1.png` was taken from (warehouse). Expect: dry-looking
   floor, no bright sky-cubemap mirror, no veil — again with the Lab untouched.
4. Read the log lines in §12.7 to confirm: `UnityAmbient=Donor` with the expected donor color values;
   `RainCover=RoofDepth` with a computed `RainBounds` noticeably smaller than the old class default
   `(1000,220,1000)` and a non-zero `cullingMask`; the `DaytimeVisualsProbe` summary line agrees.
5. If the indoor floor still looks wet/mirror-bright, check `PostFxSSR` in the probe line — if `false`,
   enable SSR in EFT's graphics settings (§12.5) and re-test before concluding the rain-cover fix itself
   needs more work.
6. Sanity-check a no-rig map (`arena_equator`) still looks byte-for-byte like before this pass — `Donor`
   stays `"Off"` for it, so none of goals 1-3's code ever runs.

### 12.9) Build

```
dotnet build D:\ArenaMapPort\ArenaMapPort.Client\ArenaMapPort.Client.csproj -c Release -p:ArenaMapPortDllOnly=true
```
Clean rebuild (removed `bin`/`obj` first): **0 warnings, 0 errors.**

dist DLL hash (SHA-256) == bin DLL hash, both:
`d770531b8f522bf129a7ffc9be5cda04a29f35e49bb3b99cc9b7951d73a00a3e`

Files touched: `WeatherRig.cs` (goals 1 + 3a config/call-site), `LightingLab.cs` (goal 2 rename/dedup),
`HarmonyPatches.cs` (one new installer, goal 3b), new `RainCoverRepairs.cs` (goal 3a/3b logic),
`DaytimeVisualsProbe.cs` (goal 4). No `Plugin.cs` change needed — `RainCoverRepairs` has no config of its
own to bind early, and `HarmonyPatches.InstallAll` already runs before `WeatherRig.Install()`, which is
fine since patching doesn't depend on any per-raid state being populated yet. No `tools/`, `Server`, or
`base.json` file changed — zero receipts invalidated. No git commit made (workspace rule unchanged: the
lead commits). Not yet playtested against this exact build — the evidence in §12.1 is from the pre-fix
Lighting Lab session; this pass turns those findings into the new default behavior.

### 12.x) Review fix-ups (lead)

- **Scan timing.** The reviewer found that `RainCoverRepairs` scanned the map's geometry from
  `WeatherRig.OnSceneLoaded`, which fires on the Scripts scene — the first scene of the raid to load,
  before any geometry scene exists — so the scan would have found 0 renderers and silently fallen back
  to the class-default bounds. The scan now runs in a Harmony prefix on `DepthPhotograper.Render()`
  (first camera change of the raid, all scenes loaded); `ApplyForCurrentMap` only arms it. An empty
  scan is retried on the next camera change, at most 3 times.
- **LevelBorder exclusion.** `LayerMask.NameToLayer("LevelBorder")` is -1 in this project, so the scan
  now also excludes renderers named `LevelBorder*` (MapBorderRepairs' own fallback). In practice those
  renderers are already disabled by MapBorderRepairs before the first camera change.
- Log lines to look for: `[RainCover] ... armed`, then on camera change
  `[RainCover] ... RainBounds computed from N renderer(s) ... center=... size=...`,
  `[RainCover] ... depth-camera cover layers=0x...`, `[RainCover] ... depth camera cullingMask=0x...`.

## §13) Rain cover v2 — vanilla-faithful roof depth photo (2026-09-18)

Role: builder. Task: the 2026-09-18 20:20 playtest (log `D:\SPT41vanilla\BepInEx\LogOutput.log` lines
1485-1680, screenshots `analysis/screenshots/außen.png`/`innen.png`) confirmed §12's outdoor ambient fix
works (dark wet asphalt, correct puddle mirrors, no veil) but the §12.4 `RoofDepth` cullingMask-widening
mechanism did not fix the indoor warehouse floor (still full wetness: puddles mirroring the pillars, a
bright white sheen between them). This pass implements the mechanism vanilla actually uses — a Harmony
prefix on `DepthPhotograper.RenderObstacles` that draws every elevated static mesh in black onto the
depth camera, the same way a donor's `WeatherObstacle`/`DryPlane` roofs do — plus a first-class depth-map
readback diagnostic. Client-only, no git commit made (workspace rule unchanged: the lead commits).

### §13.1) Evidence — why `RoofDepth` (§12.4) plausibly drew nothing

`DepthPhotograper.SetupDepthCamera` (decompile, `DepthPhotograper.cs:131`) sets the depth camera's
`renderingPath = RenderingPath.Forward` unconditionally. If this map's own p0/deferred/Vert-Paint-style
shaders author only a `LightMode=Deferred` pass plus a `ShadowCaster` pass (this project's own
`ShaderRebind.cs`/`material-shaders.json` pipeline — not re-audited this pass, taken from the task's own
premise), a Forward-path camera skips their color/depth-writing pass entirely: widening the depth camera's
`cullingMask` to include those renderers' layers (§12.4(b)) very plausibly draws nothing at all, matching
the playtest's own "ran mechanically, floor still wet" symptom (`RainBounds`/`cullingMask` both computed
and logged correctly, the F9 snapshot's own `_WaterLevel=0.76`/`PostFxSSR=True`/`AmbientLight.IsSSREnable=
True` unchanged — DepthPhotograper's actual coverage photo is the only piece never independently verified
until this pass' own goal 1 diagnostic).

A second, independent finding from the same investigation (full write-up: the task's own SPEC, reproduced
in `RainCoverRepairs.cs`'s class doc comment and this section) — the *confirmed* `_WeatherDepthMap`-
consuming shader (`WetRenderer`'s `CameraEvent.BeforeReflections` screen overlay, matched by its own
`_SurfaceWetBias`/`_SurfaceWetIntensity`/... property IDs turning up together with `_CameraDepthTexture`/
`_CameraGBufferTexture2` in `globalgamemanagers.assets`' own shader property tables) was measured at
`_SurfaceWetIntensity=0.00` during the very playtest that showed the bug. That doesn't mean rain-cover
correctness stopped mattering — it means the 20:20 playtest's own indoor veil may ALSO still be driven by
`GlobalReflection` (§12.1's own, earlier, already-confirmed Candidate B fix — a Harmony postfix on
`AmbientLight.method_2` overriding `_MyGlobalReflectionProbe`), which the "everything at defaults" test
run left at its own default (`Rig`, not `Black`). **§13.6's test steps below therefore keep the
`GlobalReflection=Black` A/B check alongside the new depth readback**, so a playtest can tell which
mechanism actually cleared the veil before concluding this pass succeeded or failed on its own.

### §13.2) Goal 1 — depth-map readback diagnostic

`RainCoverRepairs.BuildDepthReadback(Vector3 worldPos)`, called from `LightingLab`'s existing F9 snapshot
hotkey at the live camera position (extends the snapshot block rather than adding a new hotkey — task's
own stated preference). Reads:

- `DepthPhotograper.Instance._yBias`/`._depthTextureDimension`/`._depthRT` directly — all three are public
  fields (`DepthPhotograper.cs:10-27`) and the component itself outlives any one `Render()` call (only its
  transient `_depthCamera` GameObject/`_cameraRT` are destroyed every camera change, `DepthPhotograper.cs:
  79-80`), so no caching is needed for these.
- `_MapStart`/`_MapScale` via `Shader.GetGlobalVector` (process-wide globals `DepthPhotograper.
  SetShaderValues` publishes, `DepthPhotograper.cs:174-182`) — same reasoning, safe to read live.
- The transient depth camera's own top-Y (`_rainBounds.max.y`) and far-clip (`_rainBounds.size.y`) DO need
  caching — the GameObject itself is long gone by the time an F9 keypress runs. Cached in the SAME
  `SetupDepthCameraPostfix` Harmony postfix §12.4(b) already installs, now for EVERY `RainCover` mode
  (including `Off`), not just `RoofDepth`.
- The RFloat readback itself: `_depthRT` is a color-format `RenderTexture` (`RenderTextureFormat.RFloat`),
  not a native depth buffer (`DepthPhotograper.CreateDepthRT`, `DepthPhotograper.cs:96`), so it reads back
  like any other render texture — `RenderTexture.active` + a reusable 1x1 `Texture2D(..., TextureFormat.
  RFloat, false, true).ReadPixels(...)`, the same reusable-scratch-texture pattern `LightingLab.
  AppendReflectionProbeFaces` already established for its own G candidate.
- UV → texel: derived directly from `SetShaderValues`' own math (`_MapStart`/`_MapScale`'s exact meaning),
  not guessed — `px = floor((worldPos.x - mapStart.x) * mapScale.x * dim)`, same for `z`/`py`. The center
  texel plus its 3x3 neighbourhood (8 more, edge-clamped) are all read back, so a roof edge is visible.
- Height reconstruction, BOTH formulas logged every time (`SystemInfo.usesReversedZBuffer` says which one
  is real on this machine, also logged): `height = camTopY - raw*farClip` (non-reversed) and
  `height = camTopY - (1-raw)*farClip` (reversed-Z, expected true on this D3D11 desktop build).
- `nothingPhotographed`: the depth camera's `CameraClearFlags.Color` clears color AND depth every render,
  and the cleared depth value in shader-space is always the platform's own far sentinel — `0` under
  reversed-Z, `1` otherwise; `raw` equal to that sentinel (within `1e-4`) means nothing drew into this
  texel at all.
- Ground Y under the query point: `Physics.Raycast` straight down on `HighPolyCollider`/`LowPolyCollider`/
  `Terrain`/`Default` (resolved by name, tolerant of any one being absent from this project's TagManager).

### §13.3) Goal 2 — `RainCoverMode.RoofDepthBlack` (new default)

New enum VALUE on the existing `RainCover` key (not a new key — safe per this project's hard rule: BepInEx
serializes an enum `ConfigEntry` by name, both old values `RoofDepth`/`Off` still parse byte-for-byte from
an existing saved config, only the *default* changes). `WeatherRig.RainCoverMode` is now
`{ RoofDepth, RoofDepthBlack, Off }`, default `RoofDepthBlack`.

Mechanism, mirroring vanilla exactly (`DepthPhotograper.RenderObstacles`'s own `WeatherObstacle` draw call,
decompile lines 152-161, is the direct template):

- A new Harmony **prefix** (void, not bool — the original body still needs to run its terrain-lower/
  restore loop and its own `_depthCamera.Render()` call) on `DepthPhotograper.RenderObstacles()` —
  `RainCoverRepairs.RenderObstaclesPrefix`, installed by `HarmonyPatches.InstallRainCoverDepthMask`
  alongside the existing two §12 hooks. Confirmed valid target: `public void RenderObstacles()`, no
  parameters, single overload (`DepthPhotograper.cs:138`); `SetupDepthCamera()` already ran earlier inside
  `Render()` (line 75, before `RenderObstacles()` at line 76), so `_depthCamera` (read via the existing
  `AccessTools.Field`, same as §12.4(b)) already exists with its final position/cullingMask/RT.
- For every cached elevated mesh: `Graphics.DrawMesh(mesh, localToWorld, blackMaterial, terrainLayer,
  depthCamera, submeshIndex)` — the camera-specific overload, confirmed present in this build's
  `UnityEngine.CoreModule.dll` — once per submesh (`Mesh.subMeshCount`). The **layer** argument passed is
  always `LayerMask.NameToLayer("Terrain")`, never the mesh's own GameObject layer — `Graphics.DrawMesh`'s
  `layer` parameter is matched against the CAMERA's `cullingMask` (still `1 << Terrain` only in this mode,
  never widened), not the object's own layer; vanilla's own call makes the identical choice
  (`DepthPhotograper.cs:155`) regardless of `WeatherObstacle`'s real layer. `blackMaterial` is our OWN
  persistent `Material` from `ShadersFinder.Find("Hidden/Black")`, created once and kept — NOT
  `DepthPhotograper`'s own `_blackMaterial` field, which is deliberately created fresh and
  `DestroyImmediate`'d every single `Render()` call (`DepthPhotograper.cs:66,78`) and would be a
  use-after-destroy hazard to reuse.
- Draws queued in a prefix on `RenderObstacles` are still consumed by the ORIGINAL method's own
  `_depthCamera.Render()` call a few lines later in the same synchronous call stack — vanilla's own
  `WeatherObstacle` draw call proves this exact pattern already works (queued via `Graphics.DrawMesh`, no
  `Camera` argument even, several lines before `_depthCamera.Render()` in the same method body,
  `DepthPhotograper.cs:155-161`); a Harmony prefix just moves the queuing earlier in the same stack, no
  frame boundary in between either way.
- Does **NOT** widen the depth camera's `cullingMask` — stays Terrain-only, exactly like vanilla. §13.1
  above is why: if the mask-widening approach (`RoofDepth`) really was drawing nothing due to the Forward-
  path/deferred-shader mismatch, widening it here would add zero value; if it turns out to matter after
  all, that's a follow-up investigation, not assumed necessary by this pass.

**Elevated-mesh filter** (so the ground doesn't photograph itself — mirrors vanilla, where only roofs are
ever authored as `DryPlane`s in the first place): computed once per raid, in the SAME renderer walk
`RainCoverRepairs.ScanStaticGeometry` already does for `RainBounds` (§12.4(a), unchanged) — a second pass,
after the full map's own bounds (hence its true min.y) are known:

- Candidates are every renderer that already survived the existing Water/Sky/LevelBorder/player/oversized
  filter (§12.4(a), untouched).
- **Material exclusion** (new): any renderer with a material whose shader name contains `"Transparent"`,
  `"Glass"`, or `"Fence"` (case-insensitive), or whose `renderQueue >= 2450`, is dropped before the
  elevation check — fences/glass/decals/foliage cards should never become black roof cover.
- **Elevation check** (new, config `CoverMinHeight`, default 2.2m — this task's own starting value;
  §13.4's own donor measurement below is a rough sanity check only, not a per-plane ground-clearance
  measurement): `Physics.Raycast` straight down from `(bounds.center.x, bounds.min.y - 0.05, bounds.
  center.z)` on `HighPolyCollider`/`LowPolyCollider`/`Terrain`/`Default`, max 200m. Elevated if
  `(bounds.min.y - hit.y) >= CoverMinHeight`; with no hit, elevated only if `bounds.min.y > (map's own
  min.y + CoverMinHeight)`.
- Surviving renderers' `MeshFilter.sharedMesh` + `transform.localToWorldMatrix` are cached (no
  `MeshFilter`/null `sharedMesh` → excluded, logged separately — e.g. a bare `SkinnedMeshRenderer`).
- One consolidated log line per raid: kept-for-bounds / considered-for-cover / elevated / excluded-by-
  reason (oversized, material, not-elevated, no-mesh) counts — see §13.5.

**`RainBounds` computation itself is unchanged** (§12.4(a) still applies byte-for-byte) — the new material
filter only narrows which renderers become black-draw CANDIDATES, never which ones contribute to
`RainBounds`/the legacy `cullingMask` layer set. Two small, defensive additions to the shared bounds math:
`size.y` is floored to at least 10m (`DepthPhotograper._MapScale.y = 1/size.y`, `DepthPhotograper.cs:178`
— a near-zero divisor was never actually observed, but is now provably impossible) and the top of the
bounds already includes the existing +5m headroom (§12.4(a), unchanged) above the highest surviving
renderer.

### §13.4) Investigation — settled from the decompile + game data (see also `RainCoverRepairs.cs`'s own
class doc comment)

- **Harmony target validity**: `RenderObstacles` is `public void RenderObstacles()`, no parameters, single
  overload (`DepthPhotograper.cs:138`) — an ordinary, unambiguous prefix target, same pattern as the
  existing `Render`/`SetupDepthCamera` hooks.
- **`_yBias`/`_depthTextureDimension` on Reserve** (`level139`, UnityPy `TypeTreeGenerator`, the ONE
  `DepthPhotograper` instance in the level, `path_id=4661`): `_yBias=0.0`, `_depthTextureDimension=4096` —
  both equal the plain C# class defaults. Reserve does not author either away from default, so this pass'
  own diagnostics/math don't need to special-case a non-zero `_yBias` for parity with a real donor.
- **DryPlane statistics on Reserve**: 637 `DryPlane` instances (`EFT.EnvironmentEffect.DryPlane`, fingerprint-
  matched by exact field-set — confirmed unique against every other class with a bare `public Mesh Mesh`
  field in the decompile), all sharing one unit-quad mesh asset, individually transformed (non-identity
  rotation/non-uniform scale per instance — confirmed on a sampled instance). Approximate world-Y spread
  across all 637 (2-level hierarchy, verified effectively flat for the sample): min≈-9.16, p10≈-4.97,
  median≈-0.87, p90≈8.06, max≈50.62 — a rough 5-13m spread above whatever local reference height, roughly
  consistent with roof/overhang placement. This is a sanity check for `CoverMinHeight`'s default, NOT a
  precise per-plane ground-clearance measurement (that needs a per-plane downward raycast against Reserve's
  own collision meshes, not attempted this pass).
- **Wetting shader's `_WeatherDepthMap` comparison direction**: inconclusive (compiled DXBC, not
  disassembled — out of the 20-minute timebox this investigation step was given). What WAS confirmed: a raw
  string search of `globalgamemanagers.assets`' shader property tables found exactly one shader block
  containing BOTH `WetRenderer.cs`'s own property IDs (`_SurfaceWetBias`/`_SurfaceWetDiffuseColor`/
  `_SurfaceWetIntensity`/`_SurfaceWetSpecColor`) AND `_CameraDepthTexture`/`_CameraGBufferTexture2` — this
  is `WetRenderer`'s own screen-space compositing shader, confirmed as the "wetting shader" the task set
  out to find. `SetShaderValues` never publishes the depth camera's `nearClipPlane`/`farClipPlane` as
  shader globals anywhere (only `_MapStart`/`_MapScale`/`_WeatherDepthMap`), which makes it more likely the
  shader treats `_WeatherDepthMap` as a binary "was anything drawn here" signal than a magnitude comparison
  against a reconstructed roof height — an inference from available evidence, not a confirmed shader read.
  Separately (see §13.1): the exact global name `RainController.cs` writes every frame, `"_WaterLevel"`,
  does not appear as a string anywhere in `globalgamemanagers.assets`/`resources.assets` — the two largest,
  most-likely-resident shader data files (a per-map shader variant elsewhere was not ruled out).

### §13.5) Config keys (new/changed this pass)

| Section | Key | Type | Default | Notes |
|---|---|---|---|---|
| `Lighting (<map.Key>)` | `RainCover` | enum (`RoofDepth`/`RoofDepthBlack`/`Off`) | `RoofDepthBlack` (was `RoofDepth`) | §13 goal 2 — new enum VALUE on the existing key; old saved `RoofDepth`/`Off` values still parse. |
| `Lighting (<map.Key>)` | `CoverMinHeight` | float (meters), range 0-50 | `2.2` | §13 goal 2 — new key. Only read when `RainCover=RoofDepthBlack`. |

Every other §12.6 key (`UnityAmbient`, `RainBoundsCenter`/`RainBoundsSize`, and the `LightingLab
(<map.Key>)` section's own keys) is unchanged by this pass.

### §13.6) Log lines to check

- `grep '\[RainCover\]'` — extended this pass:
  - `RainCover=<RoofDepth|RoofDepthBlack|Off> armed — ...` (armed message now names the mode and, for
    `RoofDepthBlack`, mentions "the elevated-mesh draw cache").
  - The existing `RainBounds computed from N renderer(s) ...`/`depth-camera cover layers=0x...` lines —
    unchanged, still logged for both `RoofDepth` and `RoofDepthBlack`.
  - **New**, `RoofDepthBlack` only: `RoofDepthBlack scan: keptForBounds=... consideredForCover=...
    elevated=... excluded(oversized)=... excluded(material)=... excluded(notElevated)=...
    excluded(noMesh)=... CoverMinHeight=...` — once per raid, right after the bounds/layer lines.
  - **New**, `RoofDepthBlack` only, once per raid (first camera change that actually draws or would draw):
    either `RoofDepthBlack: drew N elevated mesh(es) (M submesh draw call(s)) onto the depth camera
    (Terrain layer, Hidden/Black material).` or, if the scan found zero elevated meshes, `RoofDepthBlack: 0
    elevated mesh(es) to draw this raid ...`.
  - `depth camera cullingMask=0x... (added 0x...)` — unchanged, `RoofDepth` only (never logged for
    `RoofDepthBlack`, which deliberately never widens the mask).
- `grep '\[Harmony\] patched DepthPhotograper'` — now three lines (`SetupDepthCamera`, `Render`,
  `RenderObstacles`), confirming all three §12/§13 hooks installed.
- The F9 `[LightingLab][Snapshot]` block — new line, no letter prefix (it's a passive readback, not a
  toggleable candidate): `RainCover depth readback @ (x, y, z): cameraY=... groundY=... yBias=...
  depthTextureDimension=... mapStart=... mapScale=... usesReversedZBuffer=... depthCamTopY(cached)=...
  farClip(cached)=... [center px=... py=... raw=... nothingPhotographed=... height(linear)=...
  height(reversedZ)=...] [(-1,-1) ...] ... [(1,1) ...]` (9 texels total, center + its 3x3 neighbourhood).

### §13.7) Test steps

1. Load `arena_bay5`, Clear weather, everything at config defaults (`RainCover=RoofDepthBlack`,
   `CoverMinHeight=2.2`, `AmbientOverride=Off` — i.e. don't touch the Lighting Lab). **Caveat found in
   review (§13.10.1): if `com.maschine.ArenaMapPort.cfg` already has a saved `RainCover = RoofDepth` line
   for `arena_bay5` (true as of the §12 playtest session, and BepInEx does NOT overwrite an already-saved
   value with a new code default), "config defaults" is NOT `RoofDepthBlack` until that line is either
   hand-edited to `RoofDepthBlack` or the map's `[Lighting (...)]` section is deleted so BepInEx regenerates
   it.** Check the log for the new `RainCover=RoofDepth is the LEGACY ...` warning right after load — its
   absence confirms `RoofDepthBlack` is really active before trusting any of the steps below.
2. **Outdoor** — stand at the spot `außen.png` was taken from. Expect: unchanged from the 20:20 playtest —
   dark wet asphalt, genuine mirror puddles, no veil (§12's own fix, untouched by this pass). Press F9;
   the depth readback line's `nothingPhotographed` should read `true` for the center texel and most of its
   neighbourhood (open sky above this spot — nothing SHOULD be photographed here) and `height(reversedZ)`
   (the real formula on this D3D11 build — check `usesReversedZBuffer=True` in the same line) should be
   `n/a`/meaningless whenever `nothingPhotographed=true`.
3. **Indoor** — stand at the spot `innen.png` was taken from (warehouse). Expect: dry-looking floor, no
   bright sky-cubemap mirror, no veil. Press F9; the depth readback line's center texel should now read
   `nothingPhotographed=false` with a `height(reversedZ)` close to the warehouse roof's real world Y
   (compare against `groundY` in the same line — the gap between `height(reversedZ)` and `groundY` should
   roughly match the roof's actual clearance above the floor). If `nothingPhotographed` is still `true`
   here, the roof genuinely isn't in the photo (goal 2 not working as intended) rather than a downstream
   shader/reflection issue — check the `[RainCover] RoofDepthBlack scan`/`drew N elevated mesh(es)` log
   lines from step 1's load to see whether the roof's renderer was ever found/classified elevated at all.
4. **A/B the OTHER known mechanism** (§13.1's own finding) before concluding indoor dryness alone fixed the
   veil, or before blaming goal 2 if it didn't: flip `LightingLab (arena_bay5) / GlobalReflection=Black`
   (F12, existing knob, no new code) and re-check the same indoor spot. If THAT alone also removes any
   remaining veil, the reflection/ambient pipeline is still contributing independently of rain-cover
   coverage — expected, not a regression (§13.1/§13.8 both already say dryness alone may not be sufficient
   with SSR on and `WetRenderer`'s own `_SurfaceWetIntensity` reading near zero).
5. Read the `[RainCover]` log lines (§13.6) to confirm: `RainCover=RoofDepthBlack armed`, a non-zero
   `elevated=` count in the `RoofDepthBlack scan` line, and a `drew N elevated mesh(es)` line (not the "0
   elevated mesh(es)" one) after the raid's first camera change.
6. Sanity-check `RainCover=RoofDepth` (legacy) still behaves exactly as §12 described (cullingMask widened,
   no obstacle draws, no `RoofDepthBlack scan`/`drew N elevated mesh(es)` lines) and `RainCover=Off` still
   behaves exactly as before this pass (no bounds/mask/obstacle-draw lines at all beyond the initial `Off —
   no bounds/mask/obstacle draws computed` line).
7. Sanity-check a no-rig map (`arena_equator`) still looks byte-for-byte like before this pass — no
   `DepthPhotograper` even exists on it (no rig means no Weather/RainController subtree), so none of §12/
   §13's code ever runs; the F9 depth readback line should read "DepthPhotograper.Instance is null".

### §13.8) Is dryness alone enough? (goal 4 of the task's own spec)

Stated honestly, not assumed: making the depth map correctly mark the warehouse floor "covered" only
visibly helps if some ACTIVE shader branches on that coverage. The one confirmed consumer (`WetRenderer`'s
screen overlay) measured `_SurfaceWetIntensity=0.00` during the very playtest that showed the bug — so it
wasn't the culprit THAT time, independent of whether this pass' fix is otherwise correct. Even with
`_SurfaceWetIntensity` at zero, `PostFxSSR=True` in that same playtest means a dry (rough) floor still
picks up some cubemap/screen-space-reflection sheen — just far weaker than a wet-and-smooth one. Net
expectation for the test above: `RoofDepthBlack` is necessary for `DepthPhotograper`/`WetRenderer`-driven
wetness in general, and is the vanilla-faithful, structurally correct fix regardless — but "still faintly
bright indoors after this pass" is not automatically a goal-2 failure without first re-checking
`GlobalReflection` (step 4 above), a second, independently-confirmed mechanism capable of causing the same
visible symptom.

### §13.9) Build

```
dotnet build D:\ArenaMapPort\ArenaMapPort.Client\ArenaMapPort.Client.csproj -c Release -p:ArenaMapPortDllOnly=true
```
Clean rebuild (removed `bin`/`obj` first): **0 warnings, 0 errors.**

dist DLL hash (SHA-256) == bin DLL hash, both:
`5e1141a83c1952d10bdb68c7a09b725443f61d9f31fb277bbc2a7e0e0751cc6b`

Files touched: `WeatherRig.cs` (`RainCoverMode` enum + `RainCover`/`CoverMinHeight` config, goal 2),
`RainCoverRepairs.cs` (rewritten — goal 1's `BuildDepthReadback`, goal 2's elevated-mesh scan +
`RenderObstaclesPrefix`, §12's own three methods carried forward with minimal, targeted changes),
`HarmonyPatches.cs` (one new installer call — `RenderObstacles` prefix, alongside the existing two §12
hooks), `LightingLab.cs` (F9 snapshot extended with the depth readback line, no new candidate letter — it's
a passive readback, not a toggle). No `Plugin.cs` change needed (same reasoning as §12.9 — no new config to
bind early, `HarmonyPatches.InstallAll` already runs before `WeatherRig.Install()`). No `tools/`, `Server`,
or `base.json` file changed — zero receipts invalidated. No git commit made (workspace rule unchanged: the
lead commits). Not yet playtested against this exact build.

### §13.10) Review fix-ups (2026-09-18)

Two findings from a review pass on the §13 diff. Both verified against the actual code/config before
touching anything; both were real.

**1) HIGH — confirmed real, fixed within this project's own constraints, not the way the finding's
"fix" first suggested.** `D:\SPT41vanilla\BepInEx\config\com.maschine.ArenaMapPort.cfg` already has an
explicit `RainCover = RoofDepth` line for every one of its 13 map sections (checked directly —
`grep -n RainCover` on the live file, lines 130/238/346/454/562/670/778/886/994/1102/1210/1318/1426/1534,
all reading `RoofDepth`), written by BepInEx during the §12 playtest session when `RoofDepth` was still the
class default. `BepInEx.Configuration.ConfigEntry.Bind()` reads an already-present key from the saved file
rather than overwriting it with a new code default — moving `WeatherRig.cs`'s class default to
`RoofDepthBlack` (§13.3, unchanged by this fix-up) does nothing for a map whose section is already on disk
with an explicit `RoofDepth` value, `arena_bay5` included. §13.7 step 1's "everything at config defaults"
premise is therefore false for this exact deployment as it stood after §12 — the next playtest would
silently keep exercising the legacy, already-shown-not-to-work `RoofDepth` path while believing it was
testing `RoofDepthBlack`.

This project's own hard rule forbids writing under `D:\SPT41vanilla` (the `.cfg` lives there), so the
finding's short-term suggestion (delete/rename the file, or hand-edit the 13 lines) is not something this
client-only pass can do itself. Fix applied instead, both parts client-side:

- `RainCoverRepairs.ApplyForCurrentMap` (`RainCoverRepairs.cs`) now logs a `[ArenaMapPort][RainCover]`
  **warning**, once per raid, whenever the resolved mode for the current map is the legacy `RoofDepth` —
  naming the exact cause (an old saved `.cfg` value, not a deliberate choice) and the exact remedy (edit
  `RainCover=RoofDepthBlack` in that map's `[Lighting (...)]` section, or delete the section so BepInEx
  regenerates it). This makes the stale-config case impossible to miss in the log the next time someone
  actually looks, rather than silently reproducing the "ran mechanically, floor still wet" symptom §13 was
  written to fix.
- §13.7 step 1 below is corrected to say so explicitly, so a future playtest does not repeat the same silent
  miss this review caught.

No enum/config-key change (§13.3's "old saved values still parse, only the default changes" contract is
kept exactly as designed) and no attempt to auto-migrate the saved value from client code — an already-saved
`RoofDepth` is indistinguishable from a person's own deliberate A/B choice (§13.7 step 6 exists precisely to
exercise that choice), so overwriting it automatically would be wrong on purpose, not just disallowed by the
hard rule.

**2) LOW — confirmed real, fixed.** `RainCoverRepairs.AppendTexel` set `RenderTexture.active = rt;`, then
called `scratch.ReadPixels(...)`, `scratch.Apply()`, `scratch.GetPixel(0, 0)`, then restored
`RenderTexture.active = prevActive;` — with no `try`/`finally` around the three calls in between. The only
enclosing exception handling was in the caller, `BuildDepthReadback`'s own `try`/`catch`/`finally` around
the whole 3x3 texel loop, but that `catch` only appends an error string to the log line and that `finally`
only destroys the scratch `Texture2D`; neither restores `RenderTexture.active`. An exception partway through
the 3x3 neighbourhood readback (any single texel) would have left the engine-global
`RenderTexture.active` pointed at `DepthPhotograper`'s own `_depthRT` (an RFloat render texture) until
something unrelated happened to reassign it later — a real, if narrow, correctness hazard for anything else
in the same frame that reads `RenderTexture.active` and expects the screen/default target.

Fixed by wrapping the three calls in their own `try`/`finally` inside `AppendTexel`, restoring
`RenderTexture.active` unconditionally regardless of which one throws; `raw` is now assigned inside the
`try` and read after. No behavior change on the non-throwing path (verified by inspection — same three
calls, same order, same return value).

**Build**: clean rebuild (`bin`/`obj` removed first),
`dotnet build ArenaMapPort.Client\ArenaMapPort.Client.csproj -c Release -p:ArenaMapPortDllOnly=true` — **0
warnings, 0 errors**. dist DLL hash (SHA-256) == bin DLL hash, both:
`460c152f9859f6a1a00998cc0a930c4c357d8eee240f49b58fdc8ddf669d2dc7` (changed from §13.9's
`5e1141a83c1952d10bdb68c7a09b725443f61d9f31fb277bbc2a7e0e0751cc6b` — expected, `RainCoverRepairs.cs`
changed). Only `RainCoverRepairs.cs` touched this fix-up pass. No git commit made (workspace rule
unchanged: the lead commits). Still not playtested against this exact build.

### 13.11) Lead decision: key renamed

The saved-config trap from §13.10.1 is handled the project's usual way: the key is now
`RainCoverMethod` (default `RoofDepthBlack`); the old `RainCover = RoofDepth` line in existing configs is
orphaned and ignored. No manual config edit is needed for the next playtest.

## §14) Rain cover experiments (2026-09-18 21:17 playtest)

Role: builder. Task: the 2026-09-18 21:17 playtest (log `D:\SPT41vanilla\BepInEx\LogOutput.log` lines
1493-1701; screenshots `analysis/screenshots/außen.png`/`innen.png`/`innen-black.png`) ran `RoofDepthBlack`
for real and read back the depth photo with F9 at both spots — the first time this pass' own coverage
mechanism was checked against ground truth rather than just "did it run mechanically". Two findings, one a
confirmed bug (fixed this pass, goal 5) and one still open (an empirical question the compiled shaders
can't answer from the decompile alone, goal 1-4 below give a tester the tools to settle it in-game).
Client-only, no git commit made (workspace rule unchanged: the lead commits).

### §14.1) Evidence

- **Scan ran and drew something**: `scan: keptForBounds=15741 consideredForCover=14371 elevated=7330
  excluded(material)=1370 excluded(notElevated)=7012`, `drew 7330 elevated mesh(es) (9510 submesh draw
  call(s))` — `RoofDepthBlack` is not silently doing nothing this time (unlike §13's own `RoofDepth`
  finding).
- **Outdoors** (56.26, -0.80, 16.63): `groundY=-3.89 raw=0.4005 nothingPhotographed=False
  height(reversedZ)=-3.85` — the depth camera photographed something at *the query point's own ground
  height* (`height(reversedZ)` ≈ `groundY`), i.e. the yard's own floor mesh got classified ELEVATED by the
  §13 scan and drew itself as its own "roof". außen.png still shows puddles regardless — confirming the
  shader's own coverage test is not simply "was this pixel's floor drawn into the depth map at all" (a
  literal floor-covers-itself signal would be nonsensical as a binary "covered" flag either way — the
  puddles staying visible here is consistent with either H2 or a real height-band compare, not by itself
  proof of either).
- **Indoors** (63.14, -0.78, -10.07): `groundY=-3.82 raw=0.4917 height(reversedZ)=5.08
  nothingPhotographed=False` — something **8.9m above the floor** (a real roof, plausibly) IS in the photo
  here, unlike §13's own worry that nothing would draw at all — yet the hall floor is still fully wet with
  mirror puddles (innen.png), and staying wet even with `LightingLab GlobalReflection=Black` (innen-black.png
  — a wet, dark floor with puddles, not the dry-look §13.7 step 4's own A/B check would expect if
  `GlobalReflection` alone explained it). So neither of §13's own two candidate mechanisms (rain-cover
  dryness, `GlobalReflection`) has cleared the indoor veil on its own in this exact build/session — the
  depth map has real roof data in it now, and the floor is still wet regardless.
- **Live globals**: `_WaterLevel=0.76` even with `Weather=Clear`. Root cause traced to
  `WeatherController.cs:307`: `laingWater = Max(MinLyingWater, RainController.Wetting)` — the cloned
  Reserve rig's own `MinLyingWater` is ~0.8, so puddles on this donor sit at 80% regardless of the weather
  preset. On a real Reserve raid vanilla indoor floors are presumably kept dry by the rain-cover mechanism
  itself (or by ground materials that simply never author puddle response indoors) — this pass' own job is
  to find out empirically which one, for a PORTED map that authors neither `WeatherObstacle`/`DryPlane`s
  the way Reserve does.

### §14.2) Hypotheses (why in-game tests, not more decompiling)

The consuming ground/wetting shaders are compiled DXBC (§13.4 already hit this wall trying to disassemble
`WetRenderer`'s own compositing shader) — static analysis can name candidate mechanisms but can't prove
which one actually branches on `_WeatherDepthMap` the way the puddle shader does. Three, not mutually
exclusive:

- **H1 — orientation mismatch.** The consuming shader samples `_WeatherDepthMap` with a different
  U/V orientation (flipped, or X/Z swapped) than `DepthPhotograper.SetShaderValues`' own `_MapStart`/
  `_MapScale` math implies, so a roof's shadow on the depth map lands over the WRONG part of the ground —
  the hall's actual floor reads "uncovered" even though a roof-shaped black blob really is somewhere in the
  photo. (Worth noting for the record: `RainCoverRepairs.BuildDepthReadback`'s own UV math — `px =
  floor((worldPos.x - mapStart.x) * mapScale.x * dim)`, same for Z/`py` — is derived directly from
  `SetShaderValues`, not guessed, and a from-first-principles trace of `SetupDepthCamera`'s camera
  transform — position `(center.x, max.y, center.z)`, rotation `Euler(90,0,0)` — works out to camera-right =
  world +X, camera-up = world +Z, i.e. self-consistent with that same `_MapStart`/`_MapScale` formula. That
  only proves OUR OWN readback math agrees with vanilla's OWN photograph math, though — it says nothing
  about whether the separately-compiled WETTING shader reads the resulting texture the same way. DrawTransform
  below tests the actual drawn geometry's real-world effect empirically rather than trusting that derivation
  alone.)
- **H2 — the depth map only feeds a different effect.** `_WeatherDepthMap` might drive `WetRenderer`'s
  screen-space sheen/rain-ripple overlay only, while the ground MATERIALS' own `_WaterLevel`-driven puddle
  response ignores it entirely (would explain §13.8's own honest caveat: `_SurfaceWetIntensity` measured
  0.00 in the §13 playtest, so `WetRenderer` wasn't even active that time either).
- **H3 — a height-band compare, not a binary flag.** The consuming shader might need the photographed
  surface within some height window of the queried point (e.g. "covered only if the roof is within N
  meters", not "covered if ANYTHING was ever drawn above"), which would explain why the outdoors case
  (photographed surface = the query point's OWN height) does not, by itself, prove the shader treats
  coverage as purely binary.

### §14.3) Goal 5 — the confirmed bug: elevated-mesh filter fix (`RainCoverRepairs.ScanStaticGeometry`)

Root cause of the outdoor "ground photographs itself" symptom, worked out from the raycast math itself (no
further decompile needed — `Physics.Raycast` semantics only): the old elevation check's origin was
`(bounds.center.x, bounds.min.y - 0.05f, bounds.center.z)` — **5cm BELOW** the candidate renderer's own
bounds — then cast further DOWNWARD. For a floor-level renderer, that origin sits below the floor's own
collider surface already, and the ray points even further away from it — the floor's own collider is
never in the ray's path, so `Physics.Raycast` reports "no hit" for THE FLOOR ITSELF. The old fallback for
"no hit" was `elevated = bounds.min.y > (map's own min.y + CoverMinHeight)` — true for nearly any floor on
a map whose lowest point is tens of meters below (a basement, an underside, or simply `LevelSettings`'
class-default extents), so ordinary ground routinely got misclassified as an elevated roof.

Fix: origin moved to `(center.x, bounds.min.y + 0.02f, center.z)` — 2cm INSIDE the renderer's own bottom —
and the "no hit" fallback removed outright (`elevated = false`, not a map-min.y guess). For a floor, this
new origin still sits below/at the floor's own upward-facing collider surface, so the downward ray still
misses that SAME renderer's own geometry (identical reasoning to before) and "no hit" now correctly means
"not elevated". For a genuinely elevated mesh (a roof/overhang with open air, not its own floor, between
its underside and the real ground), the ray travels down through that open air and hits the REAL ground/
floor collider further below, and the existing `(bounds.min.y - hit.point.y) >= CoverMinHeight` formula
(unchanged) correctly classifies it. `CoverMinHeight` itself stays 2.2m — this pass doesn't re-tune it, only
fixes which candidates ever reach that comparison. Log line format (`scan: keptForBounds=... elevated=...
excluded(notElevated)=...` etc.) is unchanged — only the classification underneath it changes.

### §14.4) Goals 1-4 — `RainCoverLab (<map.Key>)` experiments (`RainCoverRepairs.cs`)

Three live-switchable knobs, all gated on `ArenaMapRaid.IsLoaded() && WeatherRig.IsActiveForCurrentMap()`
(this file's own existing "mandatory first check" convention) and re-rendering the depth photo immediately
on change so no new raid is needed between tries — same "live F12 knob, `SettingChanged` + apply while a
rig is active" pattern `LightingLab.cs` already established, extended here with an on-demand re-render
instead of a per-frame write:

- **Goal 1 — `DrawTransform` (H1).** Enum `{None, FlipX, FlipZ, FlipXZ, SwapXZ}`, default `None`.
  `RenderObstaclesPrefix` pre-multiplies every elevated mesh's `localToWorld` by a world-space mirror/swap
  about the live `LevelSettings.RainBounds` CENTER (`RainCoverRepairs.BuildMirrorMatrix`) before drawing it
  onto the depth camera: `FlipX: x' = 2*cx - x`, `FlipZ: z' = 2*cz - z`, `FlipXZ` both, `SwapXZ:
  (x'-cx)=(z-cz), (z'-cz)=(x-cx)` (a diagonal mirror — swaps which world axis maps to which depth-texture
  axis, the shape of bug H1 actually describes). Built as translate-to-center / transform / translate-back,
  so it's correct regardless of where `RainBounds` actually sits, not assumed to be world origin.
  **The cull question the task asked to verify first, settled from the game data (not guessed):** a
  UnityPy 1.25.2 read of `D:\SPT41vanilla\EscapeFromTarkov_Data\globalgamemanagers.assets` found
  `Hidden/Black` at `path_id=245`. Its `m_ParsedForm.m_PropInfo.m_Props` is **empty** — the shader exposes
  NO material properties at all, so there is no `_Cull` to `material.SetInt(...)` regardless of mode. But
  its one compiled `SubShader`'s one `Pass`'s own `m_State.culling.val` already reads `0`
  (`UnityEngine.Rendering.CullMode.Off`) — **Cull Off is baked into the shader itself**, not something a
  caller sets at all. A mirrored/winding-reversed draw therefore needs no material swap, no
  `MaterialPropertyBlock`, and no double-draw workaround — the existing persistent `_blackMaterial` is
  reused unchanged for every `DrawTransform` mode (and for `CoverAll`'s quad below). Logged once at
  `InstallLab()`.
- **Goal 2 — `CoverAll` (H2).** Bool, default `false`. Draws one extra black quad (two triangles, built at
  runtime once and reused, `RainCoverRepairs.GetOrCreateCoverAllQuadMesh`) covering the WHOLE live
  `RainBounds` footprint at `RainBounds.max.y - 1`, drawn regardless of `DrawTransform`/whether any elevated
  meshes were found this raid (so `CoverAll` alone can be tested even on a raid where the scan found 0
  elevated meshes). If the hall AND the yard both dry out with this on, the depth map genuinely drives
  ground wetness end-to-end and the remaining bug is geometry/orientation (H1/H3), not the consuming
  shader — if either stays wet with the ENTIRE map marked "covered", that shader most likely never reads
  `_WeatherDepthMap` for ground puddles at all (H2 confirmed).
- **Goal 3 — `WaterLevelZero`: not implemented, documented instead.** The task's own spec allowed skipping
  this if an existing knob is equivalent — it is: `LightingLab (<map.Key>)`'s existing
  `WaterLevelOverrideEnabled=true` + `WaterLevelOverrideValue=0` already pins the `_WaterLevel` shader
  global to 0 every frame (`LightingLab.cs`'s own Harmony postfix on `RainController.AbstractState`'s
  wetting-update method) — byte-for-byte the same effect a duplicate `RainCoverLab` knob would have. Use
  that existing pair instead; nothing new added here.
- **Goal 4 — re-render on demand (`RainCoverRepairs.RerenderNow`).** Any live change to `DrawTransform`/
  `CoverAll` (via each `ConfigEntry`'s own `SettingChanged`, scoped to the map that actually changed — a
  different map's raid loaded is a no-op, same guard `LightingLab.ReapplyIfLoaded` already uses), or a new
  global hotkey **`RerenderHotkey` (default F8)** polled the same "KeyboardShortcut every `Update`" way
  `LightingLab`'s own F9 already is, calls `DepthPhotograper.Instance.Render()` directly — the exact same
  call `RainController.AbstractState.CameraChanged` makes on every real camera change
  (`RainController.cs:100`, `Controller._depthPhotograper.Render()`). This synchronously rebuilds the
  transient depth camera, re-runs `RenderObstaclesPrefix` (reading the just-changed `DrawTransform`/
  `CoverAll` live), and re-publishes `_MapStart`/`_MapScale`/`_WeatherDepthMap` — identical to what a real
  camera change does, just invoked on demand. Logs `[RainCover] re-rendered depth photo (DrawTransform=...,
  CoverAll=...) [trigger]` immediately followed by the same depth readback line F9 appends, at the live
  camera position, so the effect (or lack of one) is visible in the log without moving or waiting for a
  camera change.

`DepthPhotograper.Instance` (a plain static field, set once by `Start()` and never cleared while the rig
object lives) is used directly rather than routing through `WeatherRig`'s own harvested-rig accessors —
this is the SAME singleton `RainCoverRepairs.BuildDepthReadback` (§13 goal 1) already trusts, with the same
pre-existing caveat that codebase already lives with: if more than one donor rig is harvested and activated
at different points in the same session, `Instance` reflects whichever rig's `DepthPhotograper` had its
`Start()` run most recently, not necessarily "the currently active map's own rig" — not a new risk
introduced by this pass, and out of scope to fix here.

### §14.5) Config keys (new this pass)

| Section | Key | Type | Default | Notes |
|---|---|---|---|---|
| `RainCoverLab (<map.Key>)` | `DrawTransform` | enum (`None`/`FlipX`/`FlipZ`/`FlipXZ`/`SwapXZ`) | `None` | Goal 1 (H1). Re-renders immediately on change. |
| `RainCoverLab (<map.Key>)` | `CoverAll` | bool | `false` | Goal 2 (H2). Re-renders immediately on change. |
| `RainCoverLab` (global, no map key — same convention as `LightingLab`'s own `SnapshotHotkey`) | `RerenderHotkey` | KeyboardShortcut | `F8` | Goal 4. |

`RainCover`/`CoverMinHeight` (§13) and every `LightingLab (<map.Key>)` key (§11/§12/§13) are unchanged by
this pass. No key renamed, no key reused with new semantics — all three above are brand new.

### §14.6) Log lines to check

- `grep '\[RainCoverLab\] installed'` — once at plugin `Awake()`, confirms the `Hidden/Black` Cull-Off
  finding and `RerenderHotkey=F8`.
- `grep '\[RainCover\] map=.* RoofDepthBlack: drew'` — the existing §13 once-per-raid line, now also
  reporting `CoverAll=<bool>` and, if non-`None`, `DrawTransform=<mode>` in the same line — the settings
  active for THIS raid's first camera change.
- `grep '\[RainCover\] map=.* re-rendered depth photo'` — goal 4's own line, once per `DrawTransform`/
  `CoverAll` change or `RerenderHotkey` press, naming the mode/flag now in effect.
- `grep '\[RainCoverLab\]'` immediately after the line above — the fresh depth readback (same format §13.6
  already documents: `cameraY=... groundY=... ... [center px=... raw=... nothingPhotographed=... height...]
  [(-1,-1) ...] ...`).
- The existing `RoofDepthBlack scan: ... elevated=... excluded(notElevated)=...` line (§13.6, format
  unchanged) — goal 5's fix changes the underlying `elevated`/`excluded(notElevated)` COUNTS for the same
  map/raid (expect `elevated` to drop and `excluded(notElevated)` to rise for the ground-level renderers
  that used to misclassify), not the line's format.

### §14.7) Test script

1. Load `arena_bay5`, Clear weather, config defaults (`RainCoverMethod=RoofDepthBlack`,
   `RainCoverLab (arena_bay5) / DrawTransform=None`, `CoverAll=false`).
2. Stand in the hall (`innen.png`'s own spot). Press **F8** — confirms the re-render path itself works and
   gives a fresh baseline readback (`nothingPhotographed`, `height(reversedZ)` for this exact spot) before
   changing anything.
3. Toggle **`CoverAll=true`** (F12) — auto re-renders. Walk to the yard (`außen.png`'s own spot) and back to
   the hall. **Is the hall AND the yard dry?**
   - Both dry → the depth map really does drive ground wetness (H2 ruled out) — remaining work is
     orientation/height-band (H1/H3), continue to step 4.
   - Either still wet with the WHOLE map covered → H2 (the ground/puddle shader does not read
     `_WeatherDepthMap` at all) — DrawTransform below is moot for THIS floor's wetness specifically; report
     this outcome as-is, no further steps needed to confirm H2.
4. Toggle **`CoverAll=false`** back (F12, auto re-renders).
5. One at a time — **`DrawTransform=FlipZ`**, F8 (or rely on the auto re-render from the `SettingChanged`
   itself; F8 is the belt-and-suspenders confirmation), look at the hall floor. Then **`FlipX`**, then
   **`SwapXZ`**. Reset to `None` between tries if it's unclear which change produced which result.
6. **Report which one (if any) dries the hall** — that identifies the exact orientation mismatch (H1) if
   one exists; if NONE of the four modes changes anything indoors, combined with step 3's own outcome, the
   depth-map mechanism is not what's driving this specific floor's wetness (points back to H2, or to H3 if
   `CoverAll` dried it but no single `DrawTransform` mode does — a height-band compare would explain drawing
   *something* correctly-oriented still not being read as "covered" if the compare's threshold is wrong for
   this roof's real height above the floor).
7. Sanity-check `RainCoverMethod=Off` and a no-rig map (`arena_equator`) are both untouched by any of this
   — `RainCoverLab`'s `SettingChanged` handlers only ever call into `RerenderNow`, which itself is gated on
   `WeatherRig.IsActiveForCurrentMap()` exactly like every other entry point in this file, so neither path
   is reachable for them regardless of what `RainCoverLab` is set to.

### §14.8) Build

```
dotnet build D:\ArenaMapPort\ArenaMapPort.Client\ArenaMapPort.Client.csproj -c Release -p:ArenaMapPortDllOnly=true
```
Clean rebuild (removed `bin`/`obj` first): **0 warnings, 0 errors.**

dist DLL hash (SHA-256) == bin DLL hash, both:
`62f1b5aaba59d7f9c5055273b122be1c75c70de38e990e543c2de20b5385699f`

Files touched: `RainCoverRepairs.cs` (goal 5's elevated-filter fix in `ScanStaticGeometry`; goals 1-4's
`RainCoverLab` config/`DrawTransform`/`CoverAll`/`RerenderNow`/`BuildMirrorMatrix`/
`GetOrCreateCoverAllQuadMesh`/`LabHotkeyRunner`, all new; `RenderObstaclesPrefix` extended to read and apply
them), `Plugin.cs` (one new `RainCoverRepairs.InstallLab()` call, after `WeatherRig.Install()` — same
ordering reasoning as `LightingLab.Install()`). No `HarmonyPatches.cs` change — `RenderObstaclesPrefix` was
already installed by §13; goals 1-2 only change what it reads at draw time, no new patch target. No
`WeatherRig.cs`/`LightingLab.cs` change — `RainCoverLab` is fully self-contained in `RainCoverRepairs.cs`,
its own section, its own `Install`. No `tools/`, `Server`, or `base.json` file changed — zero receipts
invalidated. No git commit made (workspace rule unchanged: the lead commits). Not yet playtested against
this exact build.

## 15) In-game test on the GPU host (2026-09-18 21:5x) and the ground smoothness fix

The lead ran Bay5 on the GPU host (ArenaMapPort deployed to D:\SPT41 by the user; profile ArenaGPU;
graphics raised from minimum to SSR/HBAO/shadows/textures high) and spawned inside a hall:

| Step | Live state (F9) | Hall floor |
|---|---|---|
| defaults | `_WaterLevel` 0.76, `_SurfaceWetIntensity` 0.37 | white veil |
| LightingLab `RainOff` + `WaterLevelOverride` | `_WaterLevel` 0.00, `_SurfaceWetIntensity` 0.00 | **still white** |
| LightingLab `GlobalReflection=Black` | cubemap black | **normal dark concrete** |
| LightingLab `GroundSmoothnessMultiplier` 0.47 (rig cubemap, heavy rain) | wet 1.0 | dark wet concrete, mild sheen |

So the indoor veil is not wetness: it is the sky cubemap (`_MyGlobalReflectionProbe`) mirrored by the
floor materials (smoothness 0.9-0.98). Arena authored them for its own local reflection probes; EFT has
no local reflection source for these shaders, only the sky cubemap (plus SSR where the reflected point
is on screen, which a ceiling above the camera never is).

**Fix:** new per-map key `Lighting (<key>) GroundSmoothness` (float, Bay5 default 0.5, others 1.0), a
base multiplier on the same floor/ground/asphalt materials LightingLab's candidate E scans; E's
`GroundSmoothnessMultiplier` stacks on top. Applies only while a rig is active. The F9 snapshot prints
`base(...)` and `effective`.

**Still open:** rain wetness indoors. The depth photo is correct (roof at +5 m over the hall floor in the
readback) but `WetRenderer` still wets the hall; the §14 experiments (CoverAll, DrawTransform, F8) are
the next test for that, they were not run yet.

## 16) Reflection intensity scale (2026-09-18 late)

Second host session (teleporting via the UiBridge API): the spawn hall's floor is `Depot_indoor_LOD0`
with `Custom/Vert Paint Shader Gloss Solid` (authored smoothness 0.69/0.75) — not matched by the
floor/ground/asphalt name filter, yet just as white. LightingLab `OffStrength` changed nothing: with SSR
enabled `AmbientLight.method_3` forces `_ReflectionIntensity` to 1 (AmbientLight.cs:436; the wetting
curve `EaseInOut(0,1 → 1,0)` only applies with SSR off). So per-material smoothness hacks cannot be the
fix — the reflection source strength is the single lever.

New key `Lighting (<key>) ReflectionIntensity` (0..1, default 1.0 until tuned): a postfix on
`AmbientLight.method_3` rescales the freshly written block value while a rig is active; live via
SettingChanged (calls `method_3` again). The F9 snapshot prints it as `B2`.

### 16.1) Host survey with the ReflectionIntensity knob (2026-09-18 22:5x, Clear, GroundSmoothness 1.0)

Teleported via the UiBridge API (`POST /v1/teleport` with `reachable=false`; the Bay5 NavMesh has no
walkable path between the yard and the spawn hall, so the reachability check refuses every point):

| Spot | ReflectionIntensity 1.0 | 0.5 |
|---|---|---|
| spawn hall (61,-3.8,-12), Depot_indoor floor | white veil | dark wet concrete, puddles mirror the pillars |
| yard (56,-3.8,17) | dark asphalt, sky puddles bright white | same, puddles dimmer but still sky mirrors |
| (85,-3.8,-30) and (20,-3.8,-60) | outside the play area: very dark ground, no reflections to judge | — |

Decision: `ReflectionIntensity` default 0.5 for Bay5 (1.0 for the others until they get a rig),
`GroundSmoothness` default back to 1.0 (the user's own test: the pleasing smoothness varies strongly by
spot, so a global material scalar is the wrong lever). A saved `GroundSmoothness = 0.5` from the previous
build stays in the user's config — press its Reset button in F12 once.

## 17) Rain cover closed: Bay5's puddles are authored art (host session 2026-09-19 07:2x, build 7967f80)

Defaults verified first: F9 shows `B2 Lighting.ReflectionIntensity: scale=0.49`, `GroundSmoothness`
base 1.0; the spawn hall reads as dark wet concrete with pillar-mirroring puddles without any lab knob.

Then the §14 experiments at the same spot (Clear, `_WaterLevel` 0.76):
- `CoverAll=true` (whole map photographed as covered, readback confirms the quad at +54 m): hall floor
  **unchanged**. Hypothesis H2 confirmed — the depth photo does not drive the puddles of these ground
  materials at all.
- `WaterLevelOverride` 0.39 (live `_WaterLevel` 0.39): hall floor **unchanged**; the yard's sky
  puddles keep their shapes, only marginally smaller.

So the puddle shapes on Bay5 are painted into the materials (vertex-paint layers with smoothness
0.9+ on the yard, the `Depot_indoor` gloss layer in the halls); EFT's rain system only modulates them.
Arena built Bay5 as a permanently rain-soaked harbor. Nothing to fix on the cover side; `RainCoverMethod`
stays `RoofDepthBlack` (harmless, correct depth photo for the WetRenderer sheen) and the RainCoverLab
switches remain diagnostic. What made the halls look wrong was the sky mirrored at full strength —
handled by `ReflectionIntensity` 0.5 (§16).

### 16.2) ReflectionIntensity is not a specular-only knob (host A/B 2026-09-19)

Same yard spot (56,-3.8,17), yaw 243, Clear noon, two screenshots 30 s apart: `ReflectionIntensity`
0.5 vs 1.0. At 1.0 every shadowed surface is markedly brighter — the matte steel doors, the brick wall,
the dry asphalt — not only the glossy sky mirrors. AmbientLight's Custom Ambient pass scales its whole
ambient contribution with `_ReflectionIntensity` (the SH from `ToDController` is small: `_SHAr.w` 0.04,
`_EFT_Ambient` (0.07, 0.10, 0.12) at Clear noon; the cubemap term carries most of the shadow light).
That is the user's "outdoors too dark" report. Decision: Bay5 default back to **1.0** (vanilla); the
hall-floor veil needs a per-surface fix instead — reduce the smoothness of floor-like renderers that the
RoofDepthBlack depth photo shows as covered, on per-renderer material clones so shared materials in
the yard stay untouched.
