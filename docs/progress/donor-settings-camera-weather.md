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
