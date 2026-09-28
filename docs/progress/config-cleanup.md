# Configuration cleanup

## 2026-09-28 — semantic audit and removal

Requested scope: remove configuration options that no longer have an effect.
Baseline `695bb5e`, branch `cleanup/obsolete-config-options`. The audit follows
bound values through actual consumers and runtime guards, including the accepted
Equator/Bay5 v5 packages. A disabled feature, fallback or diagnostic is not dead
merely because it does nothing with the current settings.

### Removed entries

| Section / key | Count | Why it was ineffective |
|---|---:|---|
| `Lighting (<map>) / CoverMinHeight` | 14 | The rain-cover scan explicitly discarded its argument. The clearance filter was removed earlier so whole building shells could provide cover. |
| `Lighting (<map>) / AuthoredRainCover` | 14 | Only gated explanatory logs. Native weather obstacles and geometric cover already combine independently of this flag. |
| `LightingLab (<map>) / AmbientCustomIntensity` | 14 | Wrote Unity ambient intensity, which does not affect the maps' authored `AmbientMode.Flat`. Custom ambient color remains functional. |
| `Lighting (arena_bay5) / SunIntensityMultiplier`, `SunColorWhiteBlend` | 2 | Only affect authored directional lights. Bay5 v5 has 189 lights: 163 point, 26 spot, zero directional. The real donor sun's **LightingLab** multiplier remains. |
| `Lighting (arena_equator) / VertexGroundDiffuseMultiplier` | 1 | No material uses its target shader `Custom/Vert Paint Shader Solid`. Bay5 has three such materials/nine active renderers, so its control remains. |
| `Logging / Verbose` | 1 | Field/getter/binding had no consumers. Existing diagnostics use their own working options. |

Total: **46 bindings**, including **10 formerly visible options** across the two
supported maps and shared settings. The other 36 belong to map categories already
hidden in F12. `DefaultsRevision` remains stored, but is now hidden: it prevents
old default migrations from repeating. This hides two additional visible internal
entries without deleting migration state.

Rain-cover draws, roof classification and water behavior are unchanged. Custom
ambient mode uses its former default intensity 1; its color remains adjustable.
Bay5's absent authored sun fields use neutral defaults internally; there is no
directional target to alter. Other maps' unreviewed sun/ground controls remain.

### Saved configuration

Unbinding alone is insufficient: BepInEx saves unknown keys as orphaned entries.
`RetiredConfiguration` removes only the exact retired definitions from BepInEx's
orphan dictionary after startup bindings, then calls the plugin's own `Config.Save`.
Any live binding wins over the retirement list. Unknown orphans, other-map tuning,
active values and revision markers are preserved. Lazy ambient-audio bindings are
not on the retirement list and keep their saved values until their map loads.
The cleanup is repeatable; no removed entries means no cleanup save.

The private orphan-property layout was checked against installed BepInEx 5.4.23.5.
An unsupported layout logs a warning and skips saved-key removal; it does not stop
plugin startup. No install config or third-party config was edited by a script.

### Complete audit coverage and retained controls

All **39 classes containing configuration bindings** were reviewed in three
independent groups, followed by parent source review and two implementation reviews.

| Group | Classes reviewed |
|---|---|
| Lighting/weather | WeatherRig, ArenaLightingRepairs, LightingLab, RainCoverRepairs |
| Materials/camera | ShaderRebind, CameraEffectsRepairs, RainWetnessRepairs, DecalRepairs, SeaPlaceholderRepairs, NativeWaterRepairs, AnamorphicFlareTuning, BloomTuning, HandsAmbientHighlight, VertexGroundDiffuseRepairs, EquatorRoofShadowRepairs, NativeMultiFlare, NativeDecalRepairs |
| Audio/gameplay/diagnostics | BallisticMaterialDiagnostics, BallisticTwinRepairs, BarbedWireRepairs, Bay5AmbientAudio, BoothDoorRepairs, BotSpawnProbe, EnvironmentRepairs, ExfilSignals, LightCullingProbe, LoadingScreenArt, MapBorderRepairs, NativeSpatialAudio, PhysicsPropRepairs, Plugin, PuddleFootstepRepairs, ReverbAudioProbe, RoomReverb, SainLocationShim, SceneInspector, SpatialAudioProbe, TimeOfDayRepairs, WeatherTestOverride |

Important retained paths:

- `FixExposure` and its three values repair a camera whose exposure is off;
  they do not override an already active donor exposure. Descriptions now say so.
- `AmbientColorMultiplier` is a no-donor fallback; `FillLightIntensity` needs
  `KeepFillLight` alongside a donor. Descriptions now identify those conditions.
- `Lighting / Enabled` only gates authored-light scaling, fallback ambient/fill
  and fog. Its old claim to be a master switch for the entire section was false.
- The sea placeholder remains useful when native harbor water is off/unavailable;
  its description now explains this relationship.
- Old indoor-floor settings still affect the whole-renderer branch when triangle
  splitting is disabled or inapplicable. They were not deleted.
- `HideProjectorDecals` still affects mesh-rendered water/dirt decals; native
  `StaticDeferredDecal` uses a different shader/path. The obsolete class comment
  was corrected.
- Rain/wetness controls remain effective in wet weather. Clear test weather is
  not evidence of an unused setting.
- Bay5 still uses the legacy time override; Equator's fixed menu hours bypass it.
- Audio controls, probes, bounded hit logs, restart-dependent bot diagnostics,
  optional SAIN integration and physical border toggles all have real consumers.
- `EnablePhysicsProps` disables the relevant containers on both maps when false;
  its misleading Bay5 exception in the description was corrected.
- `TextureBudgetTier` keeps its original enum and migration behavior. The two
  legacy donor-labelled choices still fall back to Stub; they are not separate
  inactive settings. Changing that serialization is outside this removal.

### Build and automated checks

- Release client build against installed EFT: zero warnings/errors.
- Actual BepInEx integration check:
  `dotnet run --project tools/tests/ConfigRetirementCheck/ConfigRetirementCheck.csproj -c Release`.
  Passed exact scoped removal, active-entry protection, user values, hidden-map
  values, unknown orphans, revision retention, save/reload and repeat cleanup.
  Uses a unique temporary file; no installed settings are written.
- Python pipeline suite: 479 tests, 28 expected skips, no failures.
- .NET server suite: 397 passed. Four existing nullable warnings remain in the
  test compilation; the client build is clean.
- Distribution validator: 14 maps, 42 distinct bundles, 14 material sidecars.
  No Unity assets or bundles were rebuilt.

Native acceptance and deployment evidence follows below.

### GPU-2 acceptance, 20:16–20:21 CEST

Tested client SHA256:
`AEDD90206509B7B3A1278B48286D090D2E6D7BB47F8B94F3087DF653FFBF33EA`.
Instance `55e2916a`, started 20:16:05. UiBridge confirmed VM build = host disk =
loaded before raid testing. An earlier startup raced the file synchronization and
still loaded the old DLL; that process was discarded and was not acceptance.

Startup log at **20:16:00.907**, before UiBridge's in-memory buffer began:
`[ArenaMapPort][Config] Removed 46 retired saved options; active settings preserved.`
Saved mirror log: `D:/SPT41/BepInEx/LogOutput-SPT-GPU-2-2026-09-28_20-15-54.log`.

Checks against both pre-change saved data and live BepInEx entries:

| Measurement | Before | After |
|---|---:|---:|
| All saved assignments, including orphaned historical entries | 911 | 865 |
| Actual bound entries, after both maps' lazy audio bindings | 811 | 765 |
| Equator bound entries, including internal marker | 67 | 63 |
| Bay5 bound entries, including internal marker | 64 | 59 |
| Non-default bound entries | 17 | 16 |

The saved-key difference equals the exact 46-key retirement set: **zero additions,
zero changed values among all 865 remaining assignments**. Both map-specific live
inventories likewise retain every remaining value. The only removed non-default
was the ineffective `Logging/Verbose=true`; all six user ambient-audio adjustments,
Equator lighting/reflection/bloom tuning, Clear weather and diagnostic settings
remain. Revision markers still contain their old values and report hidden/protected.
Positive controls (actual rain-cover, Bay5 ground and donor-sun keys) remain present;
queries for retired CoverMinHeight, AuthoredRainCover, AmbientCustomIntensity and
Logging return zero bindings.

Two short **NO BOTS** raids completed, each followed by normal debug disconnect:

- **Equator, 20:17–20:18:** Techlight view from `(-2,0.1,36)` toward
  `(9.9,1.2,36.5)` visually checked. Three roof proxies remain in the native shadow
  cache. Rain cover still draws **7,580 meshes / 9,247 submesh calls**; automatic
  depth readback records the roof. MultiFlare: **25 lights / 33 elements**, strength
  0.5; **346 native decals** registered. No new light stripe in the inspected view.
- **Bay5, 20:19–20:20:** adjacent barriers viewed from `(55,-3.9,6)` toward
  `(61,-2.9,9.5)`; both shadows visible. Rain cover draws **14,307 meshes / 17,833
  submesh calls**. MultiFlare: **24 lights / 111 elements**, strength 0.5;
  **468 native decals** registered. Native harbor water initializes with four
  textures and `Hidden/WaterForSSR` after the retained placeholder fallback.
- ArenaMapPort warnings/errors: **zero** through log sequence #4044, including both
  map loads, probes and unloads. No config values were changed during these raids.
  Final state is MainMenu, outside a raid.

These are config-migration and rendering smoke tests, not a new subjective audio
review or full wet-weather/night regression pass. The full room-audio and lighting
acceptance remains in the earlier feature notes.

### Deployment

Both sanctioned helper deployments completed: **103 files** each to `D:/SPT41`
and, after GPU acceptance, `D:/SPT41vanilla`. A separate SHA256 comparison of every
staged file confirms all 103 files match in each install. The vanilla configuration
file's hash is unchanged by deployment; it will retire its own obsolete entries
when that installation next starts EFT. No direct config edits or Unity rebuild.
The GPU host is back at MainMenu with control free.
