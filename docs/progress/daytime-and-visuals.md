# daytime-and-visuals — deterministic daytime + Bay5 visual-symptom cleanup (progress log)

Role: builder. Task: implement `DAYTIME-VISUALS-PLAN.md`'s Priority 1 (F1-F7), amended per the
critic's review of F1, for the 2026-09-17 playtest's two symptoms:

- **A)** raid time of day cannot be selected in the menu; the player spawned at night. With a
  third-party mod forcing 12:00 the map shows real daylight (sun, shadows, sky dome "looks good", rain
  works).
- **B)** at 12:00, overhead: the ground/asphalt looks almost white with large dark/black blotches
  across the yard; the sea is a flat uniform grey plane to the horizon; a black square/quad floats in
  the air on the right side; the overall image looks over-bright/low-contrast vs. vanilla maps.

**Result: implemented and built (0 new warnings), dist DLL hash confirmed == bin DLL hash. Not yet
playtested.** All changes are client-only (`ArenaMapPort.Client`) — no `tools/`, `Server`, or
`base.json` file was touched, so none of the 12 maps' `full-map-validation.json` receipts are
invalidated. No git commit made (workspace rule: the lead commits).

---

## 1) Files changed

- **New: `ArenaMapPort.Client/TimeOfDayRepairs.cs`** — F1. Harmony prefix on `EFT.LocalGame.InitWeather`
  (patched via the concrete closed type, not the open generic `BaseLocalGame<TPlayerOwner>` — see §2).
- **New: `ArenaMapPort.Client/CameraEffectsRepairs.cs`** — F2 (Cam2 auto-exposure) + F3
  (`TOD_Camera`/`TOD_Scattering` on Cam2), both hooked off `CameraManager.OnCameraChanged`.
- **New: `ArenaMapPort.Client/DecalRepairs.cs`** — F4, disables renderers using an unsupported
  deferred-decal shader.
- **New: `ArenaMapPort.Client/SeaPlaceholderRepairs.cs`** — F6, placeholder water material for
  empty-material "Water"-layer renderers.
- **New: `ArenaMapPort.Client/DaytimeVisualsProbe.cs`** — F7, consolidated once-per-raid
  `[DaytimeVisualsProbe]`-tagged log block covering F1-F6's live post-fix state.
- **`ArenaMapPort.Client/ArenaLightingRepairs.cs`** — F5, one new gate (`ownAmbientColor`) in
  `ApplyAmbient`, same pattern as the existing `ownFogColor` gate.
- **`ArenaMapPort.Client/HarmonyPatches.cs`** — one new `InstallTimeOfDayFix` method wired into
  `InstallAll`, following the file's existing `AccessTools.Method` + `Verify()` convention.
- **`ArenaMapPort.Client/Plugin.cs`** — `TimeOfDayRepairs.Install()` (before `HarmonyPatches.InstallAll`,
  since its Harmony prefix reads the config it binds) and `CameraEffectsRepairs.Install()` /
  `DecalRepairs.Install()` / `SeaPlaceholderRepairs.Install()` / `DaytimeVisualsProbe.Install()` (after
  the existing repairs) added.

---

## 2) F1 — deterministic daytime for `arena_*` raids

**Symptom A's root cause** (lead's pre-check, confirmed): `EFT.UI.Matchmaker`'s location-selection
screens don't block a time-of-day choice for arena maps specifically — the real cause is
`BaseLocalGame<TPlayerOwner>.InitWeather` (`EFT/BaseLocalGame.cs:489-517`) picking a time from
`IsRandomTime`/the `_specialTimeResolver` fallback, and only `factory4_day`/`factory4_night` are
pre-seeded in that dictionary (`BaseLocalGame.cs:327-336`) — every arena map falls through to
`_backendDateTime.Calculate()` or, when `IsRandomTime` is true, a fully random hour.

**Plan amendment (critic review, applied as written):** the original draft proposed a Harmony prefix
on `InitWeather` that writes `_localDateTime` directly and returns `false`, skipping the method body.
That would have been actively worse than today's bug: `InitWeather`'s body also sets
`GameWorld.GameDateTime`, `TODSkyProvider.Instance.CurrentTime.GameDateTime`, and calls
`WeatherController.Instance.SetWeatherNodes(...)` — all skipped by a body-skip. For `arena_bay5`
specifically, `WeatherRig` activates its harvested rig (setting `WeatherController.Instance`) *before*
`InitWeather` runs, so that gate is entered every raid today; skipping it would leave
`TOD_Time.GameDateTime` null, and `TOD_Sky`'s own `FixedUpdate` free-runs its internal day/night cycle
from the donor's baked cycle position whenever that field is null — trading "sometimes spawns at
night" for "the sky runs on its own clock, decoupled from the server date", on the one map this pass
targets.

**Implemented instead:** `TimeOfDayRepairs.Prefix` is a Harmony prefix on `EFT.LocalGame.InitWeather`
(patched via `typeof(LocalGame)`, not the open generic `BaseLocalGame<TPlayerOwner>` — `InitWeather` is
inherited, never overridden, and an open generic is never a runtime-patchable Harmony target). For any
raid whose `Location.Id` starts with `"arena_"`, it:

1. Sets `timeAndWeather.IsRandomTime = false` via a `ref` parameter (`TimeAndWeatherSettings` is a
   struct — `ref` is required for the mutation to reach `InitWeather`'s own execution, not a
   prefix-local copy).
2. Seeds `_specialTimeResolver[Location.Id] = backendDateTime.Calculate().Date.AddHours(ArenaFixedHour)`
   via reflection (`_specialTimeResolver` is a `private readonly`, **per-instance**, freshly-constructed-
   per-raid `Dictionary<string, DateTime>` — the exact same mechanism BSG's own code already uses for
   `factory4_day`/`factory4_night`, not a static/JIT-order-sensitive field as the original draft
   assumed).

`InitWeather`'s own body then runs to completion unmodified, picking up our seeded value through its
existing `_specialTimeResolver.TryGetValue(Location.Id, out _localDateTime)` branch — every downstream
side effect (`GameWorld.GameDateTime`, `TODSkyProvider.CurrentTime.GameDateTime`,
`WeatherController.SetWeatherNodes`) still runs exactly as today, just seeded with our date.

**Config** (`[TimeOfDay]` section):

| Key | Default | Effect |
|---|---|---|
| `Enabled` | `true` | Master switch. `false` = vanilla behaviour for every map, including arena. |
| `ArenaFixedHour` | `12` | The in-game hour (0-23) every `arena_*` raid is forced onto. 12 = noon, matching the playtest's own third-party-mod daylight reference. |

---

## 3) F2 — Cam2 auto-exposure

Our maps have no `LevelSettings.CameraPrefab`, so `CameraManager.SetCameraFromSettings` always falls
through to the fallback camera ("Cam2") — and `SetPrismPreset`/`SetPostProcessProfile`
(`CameraManager.cs:322-360`) are only ever called when a `CameraPrefab` supplies one, so Cam2's own
baked `PrismEffects` preset never gets `useExposure` turned on (log-confirmed:
`useExposure=False exposureMiddleGrey=0.230 exposureLowerLimit=-10.0 exposureUpperLimit=1.3`).

`CameraEffectsRepairs` hooks `CameraManager.OnCameraChanged` (the same event `WeatherRig` already
uses) and, for any `ArenaMapRaid.IsLoaded()` raid, flips `PrismEffects.useExposure = true` on the live
camera's own component, applying the three exposure fields below (pre-seeded from Cam2's own baked
values, not `PrismEffects`' generic field defaults of 0.12/-6/6). `PrismEffects.HandleExposure` is
self-contained when `m_MasterEffectExposure == null` (the expected state on Cam2), so no other wiring
is needed.

**Config** (`[Camera]` section):

| Key | Default | Effect |
|---|---|---|
| `FixExposure` | `true` | Master switch for this fix. |
| `ExposureMiddleGrey` | `0.23` | Applied to `PrismEffects.exposureMiddleGrey`. |
| `ExposureLowerLimit` | `-10.0` | Applied to `PrismEffects.exposureLowerLimit`. |
| `ExposureUpperLimit` | `1.3` | Applied to `PrismEffects.exposureUpperLimit`. |

---

## 4) F3 — `TOD_Camera` + `TOD_Scattering` on Cam2

Log-confirmed absent on the FPS camera (`TOD_Scattering present=False Sky-set=False`). Gated on
`WeatherRig.IsActiveForCurrentMap()` (no rig, no `TOD_Sky` to wire onto). Same
`CameraManager.OnCameraChanged` hook as F2:

- **`TOD_Camera`** — added if missing, `sky` wired to `MonoBehaviourSingleton<TOD_Sky>.Instance` (the
  active rig's own sky; guaranteed non-null while `IsActiveForCurrentMap()` is true).
  `TOD_Camera.OnPreCull` re-centers/rescales the sky dome onto the camera every frame
  (`DoDomePosToCamera`/`DoDomeScaleToFarClip`) — the leading hypothesis for the playtest's floating
  black square (a dome left at whatever position the donor scene's own camera last set it to).
- **`TOD_Scattering`** — added if missing, `Sky` wired the same way, shipped with
  `FromLevelSettings=true` unchanged this pass (reads our own map's authored
  `LevelSettings.HeightFalloff`/`.ZeroLevel`, not the rig's — not hand-tuned blind). Its shader
  (`"Hidden/Time of Day/Scattering"`) is one of `ShadersFinder.SHADERS`' own pre-registered entries — a
  real, expected game shader — and `TOD_ImageEffect.CreateMaterial` degrades gracefully (disables
  itself, does not throw) if it were ever missing.

**Config** (`[Camera]` section, same as F2):

| Key | Default | Effect |
|---|---|---|
| `EnableTODCameraFix` | `true` | Adds/wires `TOD_Camera`. |
| `EnableTODScatteringFix` | `true` | Adds/wires `TOD_Scattering`. |

Both only ever act while a rig is active for the current map (i.e. `arena_bay5` today, per
`WeatherRig`'s own per-map `Donor` default) — every other map is untouched.

---

## 5) F4 — disable unsupported deferred-decal renderers

**Symptom B's ground/blotches, root cause confirmed directly** in
`work/extract/bay5/materials/*.json`: e.g. `0cad361327fe76c5.json`
(`source_name: "Bay5_Decal_dirt_dark"`) — `shader: "Decal/Ultra Deferred Decal Of God 3000"`,
`_SrcBlend=1 _DstBlend=0 _ZWrite=1` (fully **opaque**, not blended — the deferred-decal projector pass
these shaders expect never runs in our scenes) with `_Color≈(0.40,0.40,0.40,0.62)` — drawn as a plain
opaque `MeshRenderer` quad this is a near-white/grey overwrite box on top of the real ground, matching
the symptom exactly (~1583 affected objects across the bundle).

`DecalRepairs` runs one pass per raid, 4 seconds after the raid's Scripts scene loads — timed to fire
*after* `ShaderRebind`'s own 3-second sweep has already turned each material's
`Hidden/ArenaMapPort/<name>` stand-in shader back into its real name (checking shader names before
that completes would only ever see stand-ins). Any `Renderer` (any owned scene, any map — not
Bay5-specific) whose real shader name is `Decal/Water Deferred Decal`,
`Decal/Ultra Deferred Decal Of God 3000`, or ends `_Decal` is disabled outright.

**Config** (`[Decals]` section):

| Key | Default | Effect |
|---|---|---|
| `DisableUnsupportedDecalRenderers` | `true` | Master switch. |

**Deferred** (Priority 2, receipt-invalidating): authoring these as real `StaticDeferredDecal`
projectors is a `tools/`-side change — not attempted here.

---

## 6) F5 — ambient double-write fix

**Confirmed bug:** `ArenaLightingRepairs.ApplyAmbient` wrote `LevelSettings.SkyColor`/
`EquatorColor`/`GroundColor` unconditionally, with no `WeatherRig.IsActiveForCurrentMap()` gate — while
`ownFogColor` (same method) and the fill light (`ApplyAll`) already gated the same way. Live-confirmed
as exactly 0.5x the authored base ambient (`ambientLight=(0.25,0.25,0.25)`, matching this repair's own
`AmbientColorMultiplier` default of `0.5`) while a `WeatherRig`'s own `AmbientLight`/`_SHAr` was
*also* feeding ambient every frame (`ToDController.Update` → `AmbientLightScript.SetSH`) — a genuine
double-write, not merely a redundant one.

**Fix:** the three `LevelSettings` color writes are now gated behind
`ownAmbientColor = !WeatherRig.IsActiveForCurrentMap()`, same pattern as the existing `ownFogColor`
gate right above it. The baseline capture (`state.BaseSkyColor` etc.) still runs unconditionally —
only the *write* is skipped. No config — this is a bugfix, not a tunable.

---

## 7) F6 — sea placeholder material

**Confirmed directly** in `work/extract/bay5/scenes/Arena_Bay5_Main.json`: the "Sea" > "Plane" object
(id `19568`) has `"material_ids": []` and `"layer": 4` (Unity's built-in "Water" layer) — an authored
water surface whose real water material never made it through the port, rendering as Unity's default
fallback material (flat grey), matching the playtest's "flat uniform grey plane to the horizon"
symptom.

`SeaPlaceholderRepairs` runs one pass per raid, 2 seconds after the raid's Scripts scene loads (margin
for every owned scene to finish loading). Generic detection (any owned scene, any map): any `Renderer`
with an empty `sharedMaterials` array on `LayerMask.NameToLayer("Water")`. `Shader.Find("FX/SimpleWater4")`
is tried first (proven loadable via `arena_factory`'s own binding per the investigation reports); if
that ever fails, the renderer is disabled instead of left drawing the default-material grey plane —
either result is strictly better than the symptom, never worse.

**Config** (`[Sea]` section):

| Key | Default | Effect |
|---|---|---|
| `EnableWaterPlaceholder` | `true` | Master switch. |

**Deferred** (Priority 2, receipt-invalidating): capturing the map's real authored water material
(`WaterContainer`/`WaterRenderer`) is `tools/`-side work — not attempted here; this is a cosmetic
stopgap only.

---

## 8) F7 — `[DaytimeVisualsProbe]` diagnostic

`DaytimeVisualsProbe` fires once per raid (waits for `GameStatus.Started`, then a further 3s margin —
comfortably after `DecalRepairs`' 4s and `SeaPlaceholderRepairs`' 2s scene-load-relative sweeps have
both finished). Every line is tagged `[ArenaMapPort][DaytimeVisualsProbe]`, so
`grep '\[DaytimeVisualsProbe\]' LogOutput.log` finds the whole block. Six lines, one per fix:

- **F1 TimeOfDay** — `enabled`, `fixedHour`, `appliesToThisMap`, and (only when a rig is active, since
  that's the only case with a `TOD_Sky` to read a live clock off of) `liveHour`.
- **F2 Exposure** — `fixEnabled` plus a live readback of `useExposure`/`middleGrey`/`lower`/`upper` off
  the active camera's own `PrismEffects`.
- **F3 TOD** — `rigActive`, both sub-fix `Enabled` flags, `TOD_Camera`/`TOD_Scattering`
  presence+sky-wired flags, and (while a rig is active) the camera/dome/sun **world positions** — the
  direct check for the floating-black-square hypothesis.
- **F4 Decals** — `enabled` plus `DecalRepairs.LastDisabledCount` (the renderer count from this raid's
  own pass).
- **F5 Ambient** — `rigActive` (== whether the F5 gate skipped the ambient-color write this raid) plus
  the live `RenderSettings.ambientLight`.
- **F6 Sea** — `enabled` plus `SeaPlaceholderRepairs.LastResult` (what this raid's own pass did).

No config — this is diagnostics only.

---

## 9) Build

```
dotnet build D:\ArenaMapPort\ArenaMapPort.Client\ArenaMapPort.Client.csproj -c Release
```

Result: **succeeded, 0 warnings, 0 errors.** Full pipeline ran to completion, including the
`ArenaMapPortStageAdditionalMaps` target's `validate_port.py` pass over all remaining maps (this is
why the build takes several minutes — it re-validates every additional map's full-map bundle, not a
new cost this pass introduced).

Dist DLL hash confirmed identical to the freshly built bin DLL:

```
sha256: 53feab07cf5079123f46788aa576c4d5e04b84b3c1040be607016b3715d83fe5
  ArenaMapPort.Client/bin/Release/maschine-ArenaMapPort.Client.dll
  dist/BepInEx/plugins/ArenaMapPort/maschine-ArenaMapPort.Client.dll
```

No `tools/`, `Server`, or `base.json` file was changed — **zero receipts invalidated**, no
`dotnet test ArenaMapPort.Server.Tests` run needed (nothing server-side changed).

---

## 10) What to test

1. `arena_bay5` raid with **no** third-party time mod — confirm daytime (noon) on its own (F1).
2. Grep `LogOutput.log` for `[DaytimeVisualsProbe]`, confirm all six lines (F1-F6) show the expected
   post-fix state (see §8 for what each line reports).
3. Overview screenshot: ground (no white/black blotches — F4), sea (no longer flat grey — F6), floating
   black square (gone or tracking the camera — F3), overall brightness/contrast vs. vanilla (F2).
4. Clean log check: no new exceptions from the `InitWeather` prefix, the `AddComponent<TOD_Camera>`/
   `AddComponent<TOD_Scattering>` calls, or the decal/sea material-assignment code paths.
5. Explicitly out of scope this pass (per the plan's own Priority 2 / Deferred sections): rain/DryPlane
   coverage, full water fidelity (real `WaterContainer`/`WaterRenderer` capture), proper
   `StaticDeferredDecal` authoring, AM/PM menu toggle, reflection-probe repositioning, the full
   camera-prefab harvest (deferred — `CameraManager.SetSSR`'s unchecked `TryGetSettings<
   ScreenSpaceReflections>` NRE risk on a donor profile lacking that override, independently confirmed
   by the critic review).

## 11) Config summary (all new this pass)

| Section | Key | Default |
|---|---|---|
| `TimeOfDay` | `Enabled` | `true` |
| `TimeOfDay` | `ArenaFixedHour` | `12` |
| `Camera` | `FixExposure` | `true` |
| `Camera` | `ExposureMiddleGrey` | `0.23` |
| `Camera` | `ExposureLowerLimit` | `-10.0` |
| `Camera` | `ExposureUpperLimit` | `1.3` |
| `Camera` | `EnableTODCameraFix` | `true` |
| `Camera` | `EnableTODScatteringFix` | `true` |
| `Decals` | `DisableUnsupportedDecalRenderers` | `true` |
| `Sea` | `EnableWaterPlaceholder` | `true` |

---

## 12) Review fix-ups

Role: fix-up implementer. One low-severity finding from the review of this pass, verified against
`ArenaMapPort.Client/TimeOfDayRepairs.cs` and `ArenaMapPort.Client/HarmonyPatches.cs`, then fixed.

### Finding: `TimeOfDayRepairs`'s reflection-field failure path silently no-ops with an incorrect comment

**Verdict: CONFIRMED, fixed.**

**Verified as described.** `TimeOfDayRepairs.ResolverField`/`BackendDateTimeField` (lines 62-65) are
resolved by this class's own `AccessTools.Field` calls, entirely independent of
`HarmonyPatches.Verify`. Grepping the repo for both field names confirmed the only references anywhere
are their own declaration and the one null-check in `Prefix()` (line 113, pre-fix) — nothing else ever
inspects them. `HarmonyPatches.InstallTimeOfDayFix` (HarmonyPatches.cs:74-86) calls `Verify(...)` only
on the `LocalGame.InitWeather` `MethodInfo`; `Verify`'s signature (`MethodBase method, Type
declaringType, string methodName`, line 431) cannot accept a `FieldInfo` at all. So the pre-fix comment
on `Prefix()`'s early-return (*"reflection target moved — HarmonyPatches.Verify already logged this at
install time; don't half-apply"*) was factually wrong, and — the real defect — a future rename of
either field would make `Prefix()` silently no-op every `arena_*` raid with **zero log output
anywhere**: no install-time error (nothing in `HarmonyPatches.cs` checks these fields), no runtime
warning, and `DaytimeVisualsProbe.ProbeTimeOfDay` (F1's own diagnostic line) reports `Enabled`/
`FixedHour`/`appliesToThisMap`/`liveHour` but never whether the reflection targets resolved — so even
the dedicated per-raid probe would not surface this failure mode. F1 (the fix for the playtest's
symptom A, arena raids stuck at night) would silently stop working.

**Fix applied**, in `TimeOfDayRepairs.cs`:

- `Install()` now logs a `Plugin.Log.LogError` (matching `HarmonyPatches.Verify`'s own message
  style/tag, `[ArenaMapPort][Harmony] target not found: ...`) once, immediately after the config
  binds, if `ResolverField` and/or `BackendDateTimeField` is null — naming exactly which field(s)
  failed to resolve and stating the consequence (`TimeOfDayRepairs (daytime-and-visuals F1) is DISABLED
  for this session: arena_* raids fall back to InitWeather's own random/night-prone time selection`).
  `Install()` runs after `Plugin.Log` is assigned (`Plugin.cs` `Awake()`: `Log = base.Logger;` at line
  38, `TimeOfDayRepairs.Install()` at line 51), so the log call is safe.
- `Prefix()`'s early-return comment (line ~135) now reads *"reflection target moved — `Install()`
  already logged this once at plugin startup; don't half-apply"*, correctly attributing the log to
  `Install()` instead of the nonexistent `HarmonyPatches.Verify` call.

No config added — this is a diagnostics-only bugfix, not a tunable, same convention as F5's fix in §6.

**Rebuild:** `dotnet build ArenaMapPort.Client/ArenaMapPort.Client.csproj -c Release` — succeeded, 0
warnings, 0 errors (elapsed 00:10:21). Dist DLL hash confirmed identical to the freshly built bin DLL:

```
sha256: ebe14c0f4fef25eae6b16f914113672116a200ccbb3fd2451eff898ee03e8965
  ArenaMapPort.Client/bin/Release/maschine-ArenaMapPort.Client.dll
  dist/BepInEx/plugins/ArenaMapPort/maschine-ArenaMapPort.Client.dll
```

No `tools/`, `Server`, or `base.json` file changed — zero receipts invalidated. No git commit made
(workspace rule unchanged: the lead commits).
