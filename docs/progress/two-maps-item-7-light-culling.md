# PLAN-TWO-MAPS item 7 — light culling, volumetric light, flicker (both maps)

Status: IN PROGRESS (2026-09-26). Design decided after a read-only pass over the EFT 0.16.9.5 decompile, the
repo pipeline and Arena's raw level files; implementation, rebuild and host test follow below.

## What vanilla has, what the ported scenes lack

Arena's lights carry three MonoBehaviours the port dropped with every other script: `CullingLightObject`
(472 on Equator, 186 on Bay5 — distance fade of intensity and shadows), `VolumetricLight` (17 / 1 — god-rays,
needs a `VolumetricLightRenderer` on the camera) and `EFT.Visual.LightFlicker` (2 on Equator). Two more pieces
sit outside the imported content scenes: `CullingManager` (one scene object in each map's Scripts scene —
Equator level54, Bay5 level85 — with a single serialized field `_debugCamera`, null on both maps) and
`VolumetricLightRenderer`, which vanilla never hosts in a scene at all: it is baked onto the `Cam2*` camera
prefabs in `resources.assets` and only fetched with `GetComponent` (`CameraManager.cs:536`).

## Verified facts (2026-09-26, read-only; UnityPy against the Arena install, decompile cross-check)

- Arena's compiled field layout of all three classes matches EFT 0.16.9.5's declarations byte-exact
  (658 + 18 + 2 instances decode with zero leftover bytes). MonoBehaviour header 32 B; `AnimationCurve` =
  key count + 28-B keyframes + THREE trailing ints (`m_PreInfinity`, `m_PostInfinity`, `m_RotationOrder`) —
  the third int is the trap.
- The per-light values are tuned per instance, not uniform: 39–49 distinct `_maxLightIntensity` values per map,
  6–9 distinct fade-distance pairs (Arena mostly 55/65 m instead of the class defaults 50/80), three shadow-fade
  pairs on Bay5. `_useLightIntensityFromEditor` is true on 470/472 Equator and 178/186 Bay5 instances and
  `_maxLightIntensity` equals the sibling Light's authored intensity in every sample. `_fadeCurve` is the
  uniform `AnimationCurve.EaseInOut(0,1,1,0)` on all 658 instances. 8 Equator instances carry non-empty
  `CullingObject` component/object lists (unverified element layout — extraction fails closed on them).
- `CullingLightObject.Awake` tolerates a missing `CullingManager.Instance` (subscribes to `OnInstanceCreated`),
  caches `float_1 = _useLightIntensityFromEditor ? _maxLightIntensity : _light.intensity` once and keeps writing
  `_light.intensity = float_1 * fade` for the whole raid; it caches `GetComponent<LightFlicker>()` /
  `GetComponent<VolumetricLight>()` once in Awake, so those two must exist first.
- The two real `LightFlicker` instances carry 24-key authored curves with `m_PreInfinity = m_PostInfinity = 1`
  (Unity's internal code for Loop; every default curve serializes 2 = ClampForever) and `FullRandomCurve=false`;
  `FlickerSystem` is an always-on application singleton (`AbstractApplication.CreateTechnicalSystems`), so the
  plan's `FullRandomCurve=true` fallback is unnecessary.
- `VolumetricLight.Awake` and `VolumetricLightRenderer.Awake` self-destroy when the player's Graphics →
  Volumetric Light setting is off; no repair-side gating needed.
- All five classes are `[ExecuteInEditMode]`: their Awake also runs inside the batch import (transient effects
  only; the bundle serializes the fields regardless).

## Design (decided 2026-09-26)

Mixed route. The per-light components are extracted from Arena's raw bytes and attached by the Unity importer
(real components inside the bundles, like items 4–6): new `tools/extract/extract_light_culling.py`, package
schema `light_culling` (manifest `native_features.light_culling` with the three counts; component dicts
`CullingLightObject` / `VolumetricLight` / `LightFlicker`, `schema_version` 1), `LightCullingImporter.cs` with
three ordered `SceneAssembler` passes (VolumetricLight, LightFlicker, then CullingLightObject), validators and
tests extended, new packages `work/extract/{equator,bay5}-native-v4`. The two pieces the packages cannot carry
are client repairs: a `CullingManager` fallback next to the existing `EnvironmentManager` /
`ObservedCullingManager` fallbacks (`EnvironmentRepairs`), and `VolumetricLightRenderer` on the raid camera
next to `TOD_Scattering` (`CameraEffectsRepairs`). A third client change coordinates with the shipped
brightness uplift: `ArenaLightingRepairs.ApplyLights` also writes `CullingLightObject.float_1` (reflection) to
the same target it writes to `Light.intensity`, otherwise the culling loop would erase the
`GlobalLightIntensityMultiplier` / `SunIntensityMultiplier` uplift continuously.

Rejected: client-side `AddComponent` with class defaults (flattens the per-light authoring); porting the Scripts
scene for a null `_debugCamera`; `FullRandomCurve=true`.

## Implementation (2026-09-26)

- `tools/extract/extract_light_culling.py` (new): byte decoders for `CullingLightObject` (base `CullingObject`
  fields first; the three `List<>` fields are consumed — element sizes 12/16/12 B — and only their counts are
  exported as `turn_off_component_count` / `turn_off_object_count`), `VolumetricLight` (14 fields, 60 B) and
  `EFT.Visual.LightFlicker` (`Flicker`'s nine fields, then LightFlicker's own `_light` PPtr last; 732 B with the
  24-key curve). Every decoder asserts the payload is consumed exactly; the fade curve must be the uniform
  `EaseInOut(0,1,1,0)` (in/out weights excluded — Arena stores 0 or 1/3, never read with `weightedMode=0`);
  unknown curve wrap codes fail closed. `augment_scene` tags the owners in `scenes/*.json`; wired into
  `extract_equator.py` (`--native-features`) and `extract_bay5.py` next to the physics-prop pass; manifest
  `native_features.light_culling = {schema_version, culling_light_object_count, volumetric_light_count,
  light_flicker_count}`.
- Validators: `validate_extract.py` knows the three component types; `validate_native_features.py` checks the
  manifest shape; `validate_bundle_roundtrip.py` counts the three MonoBehaviour classes per scene in the built
  bundle against the package and compares the LightFlicker curve key count and pre/post-infinity ints (the
  wrap-mode mapping check).
- Tests: `tools/tests/test_light_culling.py` (35 tests: decoders incl. the 188/200/204/228-byte CullingObject
  list shapes and a 24-key flicker curve, hard-fail paths, `augment_scene` tagging/duplicates, manifest shape),
  one test in `test_native_features_validation.py`. Suite: 345 tests OK (28 skipped).
- Importer: `UnityProject/Assets/ArenaMapPort/Editor/Import/LightCullingImporter.cs` (new) with
  `AttachVolumetricLight` / `AttachLightFlicker` / `AttachCullingLightObject`; `SceneAssembler` defers the three
  types in Pass 1c and attaches them in Pass 4a (VolumetricLight), 4b (LightFlicker), 4c (CullingLightObject) so
  `CullingLightObject.Awake`'s one-time `GetComponent` cache finds its siblings. All three classes are
  `[ExecuteInEditMode]`, so `AddComponent` runs their Awake in the batch import: `AddNativeComponentTolerant`
  keeps the component when Awake throws (missing `FlickerSystem`/`ShadersFinder` in the editor), and
  `AttachCullingLightObject` captures the sibling Light's enabled/intensity/shadowStrength/shadows before
  `AddComponent` (Awake forces `_light.enabled = true`, `OnDisable` would write the stale cached intensity),
  sets `enabled` last and restores the Light. Wrap codes: 1 → `WrapMode.Loop`, 2 → `ClampForever`, else throw.
- Client: `EnvironmentRepairs` creates a `CullingManager` when `Instance` is null (key `Environment ›
  EnableCullingManagerFallback`, default true, next raid; `CullingObject.Start` registers through
  `CullingManager.AddEarlyObject` when no instance exists yet, so creation order is free); `CameraEffectsRepairs`
  adds `VolumetricLightRenderer` to the raid camera in `OnCameraChanged` (key `Camera ›
  EnableVolumetricLightRendererFix`, default true; both volumetric classes self-destroy when the player's
  Volumetric Light graphics setting is off); `ArenaLightingRepairs.ApplyLights` writes
  `CullingLightObject.float_1` (reflection, logged-and-skipped if missing) to the same target it writes to
  `Light.intensity` in both branches (key `SyncCullingLightIntensity`, default true, live) — otherwise the
  culling loop's `_light.intensity = float_1 * fade` erases the brightness uplift continuously. Known gap: the
  two Equator lights with an enabled `LightFlicker` take their intensity from the flicker formula instead.
- Editor-time stubs (first two Equator builds, 21:00–21:20): Unity logs an exception per instance when an
  `[ExecuteInEditMode]` Awake/OnEnable throws inside `AddComponent` (the caller's try/catch never sees it), and
  `build_all`'s log scan treats every `Exception` line as fatal — 57 lines on the first run. `VolumetricLight.Awake`
  builds its private material from `ShadersFinder.Find("Sandbox/VolumetricLight")`, which outside play mode is plain
  `Shader.Find`: `Assets/ArenaMapPort/EditorStubShaders/Sandbox_VolumetricLight.shader` is a never-rendered stand-in
  of that name (the material is not serialized). `Flicker.OnEnable` registers with
  `Singleton<IComponentSystem<Flicker>>`: `LightCullingImporter.EnsureEditorStubs` creates a `FlickerSystem` on a
  `HideAndDontSave` GameObject through `ComponentSystem<Flicker, FlickerSystem>.Register` and calls its public
  `Awake` (a plain MonoBehaviour gets no Awake in edit mode, so `Register(T)` NRE'd on the second run). Third run:
  all five Equator scenes assembled without an exception (448+16+8 CullingLightObject, 1+8+8 VolumetricLight,
  2 LightFlicker).
- Review (adversarial agent, 2026-09-26): byte layouts re-derived by hand, member names checked against the
  decompile; found the `enabled`-before-fields ordering defect in the importer (fixed as above) and the missing
  24-key test (added). Client Release build: 0 warnings, 0 errors.

### Finding while preparing the host test (2026-09-26 21:50): Arena saves culled lights dark

`_useLightIntensityFromEditor` is true on 470/472 (Equator) and 178/186 (Bay5) instances, and it means what it
says: the CullingLightObject owns the intensity and the Light component is saved in whatever state the culling
left it in. In the v4 packages **79 active Equator lights** (66 `BasePoint`, glowsticks, `MainSpot`, spotlights)
and **107 active Bay5 lights** (`SupportLight`, `MainSpot`, glowsticks, point lights) have `m_Intensity = 0` while
their `_maxLightIntensity` is 0.5–1.75. Every build before v4 shipped those lights dark; with the component attached,
`Awake` caches `float_1 = _maxLightIntensity` and the culling update lights them. Consequence for the client:
`ArenaLightingRepairs.CaptureLightsFromScene` now takes the authored baseline from `_maxLightIntensity` when the
component owns it (otherwise it would have baked 0 into the multiplied target and the `float_1` sync and kept
them dark). Expect both maps to look noticeably brighter; the `GlobalLightIntensityMultiplier` tuning (Equator
0.5 default) may need a second look by the user.

## Host evidence (SPT-GPU-2, 2026-09-26 22:15–22:37, EFT instances 1ec07132 / aace3d7d, forced rain)

Both maps rebuilt from the v4 packages (Equator 21:20 build: 5 scenes assembled without an exception, roundtrip
`light-culling component counts in bundle match package exactly` per scene, `LightFlicker curve wrap-mode mapping
round-trips (2 instance(s))`; Bay5 21:30 build likewise, 183 + 3 CullingLightObject, 1 VolumetricLight), staged into
`dist/` by the client Release build, deployed to `D:\SPT41`; NavMesh bakes unchanged (Equator 10404/4572, Bay5
22117/10213 in the raid log).

| Check | Equator (22:16) | Bay5 (22:35) |
|---|---|---|
| `[Lighting] … applied` summary | `477 light(s) … cullingLights=472, editorIntensityBaselines=470` | `189 light(s) … cullingLights=186, editorIntensityBaselines=178` |
| `[Environment] created missing CullingManager singleton` | once per raid | once per raid |
| `[Camera] VolumetricLightRenderer` | already present on `FPS Camera` (the donor Cam2 prefab ships one; line added 22:20, seen on the Bay5 raid) | `already present on 'FPS Camera' — no changes made` |
| Errors in the BepInEx log | none | none |
| Visual | glowstick light (`glowstick light`, fade 15/30 m, authored 1.75) lights its floor pool from 3.8 m; the two `LightFlicker` lights are the arcade cabinet's screen glow at (−25.4, 0.3–1.4, 3.2), lit; spawn-corridor ceiling spots lit | warehouse interior screenshot (daylight through the windows, no lamp in view) |

Not settled visually: the distance fade (every far viewpoint hit a barricade or the elevator grate; the near view was
bright) and the volumetric shafts of `FAKE_SUN_VOLUME` (point light 51.6 m above the atrium; looking up through the
lattice at rain/overcast showed no visible shaft). Flicker variation is not provable from two stills. The
`SceneInspector` F10 hits mesh colliders, not lights, so per-light intensities were not logged. What is proven: the
components are in the bundles and live (counts), the fallbacks run, the float_1 sync runs on 472/186 lights with the
authored baselines, and no exception surfaced. **Status: item 7 VERIFIED (log + bundle evidence; distance fade and
god-rays to be judged by eye on the user's install).** Deployed to `D:\SPT41vanilla` afterwards.

## Re-check without bots (2026-09-27 12:54–12:58, SPT-GPU-2, EFT instance 3875f58f)

The host menu now has `EQUATOR / NO BOTS` and `BAY5 / NO BOTS` buttons, so the checks the bots cut short on
2026-09-26 were redone. New read-only diagnostic `LightCullingProbe` (key `Diagnostics › LightCullingProbeHotkey`,
F7): it groups every active CullingLightObject of the loaded map by its distance to the camera into *inside fade
start* / *fade window* / *beyond fade end* and counts full (intensity ≥ 99 % of `float_1`), partial and zero
intensity, lists the nearest eight, reports the volumetric state and samples each enabled LightFlicker ten times
over one second.

| Probe | inside fade start | fade window | beyond fade end |
|---|---|---|---|
| Equator spawn (−11.8, 1.6, 48.5), 456 active lights, 255 visible | 162 full, 0 partial, 0 zero | 6 full, 83 partial, 4 zero | 0 full, 0 partial, 199 zero |
| Bay5 spawn (56.6, −2.3, 1.9), 107 active, 40 visible | 27 / 0 / 0 | 1 / 12 / 0 | 0 / 0 / 67 |
| Bay5 warehouse (137.9, 1.5, 71.1), 107 active, 51 visible | 27 / 0 / 0 | 0 / 22 / 3 | 0 / 0 / 55 |

The distance fade works exactly as authored: every light nearer than its fade start is at full intensity, every
light beyond its fade end is at zero, and the window in between ramps (the "full"/"zero" entries in the window sit
at its edges). The bands move with the camera (Bay5 spawn vs. warehouse). Nearest-light lines show
`float_1 = intensity` at `multiplier=1.00` with the uplift applied (e.g. Equator `BasePoint` authored 0.5 →
`float_1=0.25` at `GlobalLightIntensityMultiplier` 0.5; Bay5 `MainSpot` 2.94, `SupportLight` 0.50 — two of the
names Arena saved at intensity 0).

Volumetric: `setting=True components=9 activeAndEnabled=1 [FAKE_SUN_VOLUME] renderer on 'FPS Camera'=enabled`
(Equator; the other eight VolumetricLights are disabled in Arena's own data), Bay5 `components=1 activeAndEnabled=1
[MainLight]`. With the test weather set to Clear for one raid (12:55, restored to the user's Rain afterwards), the
view straight up from the atrium shows a bright haze centred over the dome where `FAKE_SUN_VOLUME` sits (51.6 m
up), distinct from the sun glare at the edge — the volumetric glow renders.

Flicker (Equator, camera 47 m away, `cullingCoef=1.00`): `'Point Light (11)' min=0.05 max=0.97 samples=[0.05 0.60
0.73 0.60 0.58 0.97 0.78 0.73 0.95 0.30]`, `'Point Light (8)' min=0.65 max=1.07` — the looping 24-key curves
animate (the wrap-mode mapping 1 → Loop holds at runtime too). Bay5 has no LightFlicker.

**Status: item 7 fully VERIFIED on both maps** (distance fade, volumetric glow and flicker now measured, not
inferred). Deployed to `D:\SPT41` and `D:\SPT41vanilla`.

## Follow-up 2026-09-27: atrium floodlights switch off when the lamp leaves the view

User report: the construction floodlights in Equator's atrium used to dazzle, now they look off, and their floor
cone disappears as soon as the lamp is out of view. Host check (14:12, Equator NO BOTS, F7 probe from (0.4, 1.6,
13.9)): `'Spotlight (13)' d=7.87 fade=25.00..50.00 visible=True multiplier=1.00 float_1=3.76 intensity=3.76` while
looking at it; after turning to the floor next to it (lamp out of view) `'Spotlight (13)' d=7.59 visible=False
multiplier=0.00 float_1=3.76 intensity=0.00`, and three lights inside their fade start read zero. The lamp is
`CAPTURE_ZONE/LIGHT/Projector_portable_mask_rezerv (1)` (scene `Arena_Equator_TeamFight`); its lights live in
`Arena_Equator_TeamFight_DesignStuff` (`Spotlight (13)` ×2 Spot, max intensity 7.52; `Spotlight (14)`/`(15)` Point)
and are the only CullingLightObjects on the map with `cull_by_distance_only = false`.

Cause (decompile): `CullingManager` registers `BoundingSphere(transform.position + _shift, _radius)` per culling
object; `VisibilityData.IsObjectVisible()` returns `!IsCulledByDistance` when `CullingByDistanceOnly`, otherwise
`!IsCulledByDistance && InFpsFrustum` (the CullingGroup's frustum test on that sphere). `CullingLightObject.Awake`
replaces `_radius` with `Light.range` only for Point lights. The schema-1 extractor skipped `_radius`/`_shift`
("Awake overwrites / editor-only" — wrong for Spot lights), so the floodlights got the class default sphere of
1 m at the lamp and were frustum-culled whenever the lamp left the screen. Fix: `CullingLightObject` component
schema 2 exports `radius` and `shift`; `LightCullingImporter` sets `_radius`/`_shift` (schema 1 still accepted);
packages re-extracted and both maps rebuilt.

Rebuilt 2026-09-27 15:05–15:35 from re-extracted v4 packages (all 472 / 186 CullingLightObjects schema 2; Equator
4 and Bay5 6 frustum-culled lights with an authored sphere > 5 m); the roundtrip validator now compares the
(radius, shift) multiset between package and bundle: `CullingLightObject culling spheres (radius, shift) round-trip
(472 instance(s), 48 distinct)` / `(186 instance(s), 73 distinct)`. Host check (15:39, EFT 0ccf8d56, Equator NO BOTS,
same spot and view as 14:13, lamp out of view): `'Spotlight (13)' d=7.60 fade=25.00..50.00 visible=True
multiplier=1.00 float_1=3.76 intensity=3.76` (was `visible=False … intensity=0.00`); "inside fade start, zero" dropped
from 3 to 1. **Floodlight fix verified.**
