# fix-lighting-movement — dark raid + stuck-at-spawn fix (client runtime, no bundle rebuild)

Implements the 2026-09-14 17:56 playtest's four symptoms per the investigator/verifier report set:
(1) extremely dark, silhouettes-only even with a flashlight, (2) player cannot move, (3) shooting
looks normal (unaffected, no action needed), (4) a thrown grenade drops at the player's own feet.
(2)+(4) share one root cause (spawn colliders authored solid). (1) has no single confirmed root
cause — the verifiers **retracted** the lead investigator claim that RenderSettings is never
authored (a real `LevelSettings` component turned out to already exist and run) and left several
"plausible, medium-confidence" candidates open — so the lighting fix is a small set of independently
tunable, safe-by-default brightness multipliers rather than one targeted patch.

All changes are runtime (`ArenaMapPort.Client`) plus two source-only comments/authoring lines for a
future bundle rebuild. No bundle was rebuilt, no Unity editor was launched, no git commit was made.

## Modified / new files

- **`ArenaMapPort.Client/SpawnColliderRepairs.cs`** (new) — Harmony postfix that flips each
  `SpawnPointMarker`'s collider to `isTrigger = true` at raid start.
- **`ArenaMapPort.Client/ArenaLightingRepairs.cs`** (new) — gated ambient/light brightness repair +
  its 6 `BepInEx` `ConfigEntry`s.
- **`ArenaMapPort.Client/LightingDiagnosticProbe.cs`** (new) — once-per-raid bounded diagnostic dump.
- **`ArenaMapPort.Client/HarmonyPatches.cs`** — one new `InstallSpawnColliderFix` method wired into
  `InstallAll`, following the file's existing `AccessTools.Method` + `Verify()` convention.
- **`ArenaMapPort.Client/Plugin.cs`** — `ArenaLightingRepairs.Install()` and
  `LightingDiagnosticProbe.Install()` added next to the existing `SceneDiagnostics.Install()` /
  `ShaderRebind.Install()` calls.
- **`UnityProject/Assets/ArenaMapPort/Editor/Import/AuthorScriptsScene.cs`** (source only, no
  rebuild) — spawn `BoxCollider`s now also author `isTrigger = true`, with a comment citing the
  root-cause chain.
- **`UnityProject/Assets/ArenaMapPort/Editor/NavMesh/BakeNavMesh.cs`** (source only, comment only) —
  addendum note confirming the NavMesh source filter's correctness never depended on the spawn
  colliders' trigger flag, so the isTrigger fix above does not change bake behavior.

## 1. Spawn collider fix (movement + grenade-at-feet)

**Root cause (high confidence, directly re-read this pass):** `AuthorScriptsScene.cs:118-121`
authored each `SpawnPointMarker`'s `BoxCollider` as solid (enabled, non-trigger, 1x2x1) exactly at
the spawn position — the player spawns physically embedded in it. BSG's own runtime code can never
fix a pre-existing collider:

- `SpawnPointParamsExtension.TryAddSpawnCollider`
  (`EFT.Game.Spawning/SpawnPointParamsExtension.cs:22-36`): early-returns any collider that already
  exists, untouched.
- `AddSpawnCollider` (same file, `:38-58`) — the only place `isTrigger = true` is ever set — is
  unreachable once a collider already exists.
- `SpawnPointMarker.FixCollider` (`SpawnPointMarker.cs:257-318`), the server-params reconciliation
  pass, only ever adjusts `center`/`size`/`enabled` — never `isTrigger`.

**Fix:** a Harmony postfix on `SpawnPointMarker.Start()` (`SpawnPointMarker.cs:96-116`, the exact
method that already validates `_collider` right after resolving it), gated on
`ArenaEquatorRaid.IsLoaded()`, sets `isTrigger = true` if it is currently `false`. This is the
runtime fix that ships without a bundle rebuild. `AuthorScriptsScene.cs` was also updated to author
`isTrigger = true` directly, for the next rebuild.

**`BakeNavMesh.cs` check (per this task's instruction):** the file's existing comment says the 3
Scripts-scene spawn colliders "would otherwise leak into the navmesh as bogus tiny obstacles without
this filter" — that filter (`BakeNavMesh.cs:91-107`) keeps only NavMesh sources whose `Transform` is
a child of one of the *content*-scene roots (`Arena_Equator`/`_Lighting`/etc.) passed into `Bake()`;
the Scripts scene (where the 3 markers live) is never one of those roots, regardless of the
colliders' trigger flag. So the isTrigger change does not affect NavMesh bake correctness — an
addendum comment was added to `BakeNavMesh.cs` documenting this explicitly, since the original
comment's wording ("are non-trigger") would otherwise read as stale/contradicted after this fix.

## 2. Lighting brightness repair

**What the report set actually established (see the two verifier passes' rankings for full
detail):**

- **Confirmed, high confidence:** a real `LevelSettings` component exists in the built
  `Arena_Equator_Scripts` scene (`AuthorScriptsScene.cs:74-86`), byte-for-byte matching what the
  built bundle ships. Its `Awake()`/`OnPreCullCallback` (decompiled `LevelSettings.cs:166-171,
  240-263`) unconditionally push `RenderSettings.ambientMode = Flat`,
  `RenderSettings.ambientLight = SkyColor (0.5,0.5,0.5)` into the live `RenderSettings` every camera
  pre-cull, the whole raid. The "LevelSettings is absent" premise the project context and one
  investigator report started from is **refuted** for the current source.
- **New finding this pass, high confidence:** `RenderSettings.ambientIntensity` has **no effect**
  while `ambientMode == Flat` (confirmed against Unity's own scripting reference: *"Unity scales the
  ambient light only when ambientMode is Skybox... in the other ambient modes, Unity reads the
  ambient colors directly, so changing this value has no effect"*). This means a naive
  "multiply `AmbientIntensity`" fix — which several of the investigator/verifier reports' proposed
  `ConfigEntry`s implicitly assumed — would have silently done **nothing**. The repair instead scales
  the actual `SkyColor`/`EquatorColor`/`GroundColor` fields, which do drive
  `RenderSettings.ambientLight`/`ambientEquatorColor`/`ambientGroundColor` under Flat mode.
- **Confirmed, high confidence:** a real Directional light ("FAKE_SUN" per the lighting-data
  investigator's UnityPy census) exists, enabled, intensity 1.7 — but with a dim/desaturated authored
  color (~0.18, 0.18, 0.20; net effective brightness ≈0.3 per channel).
  `TOD_Sky`/`TODSkySimple`/`WeatherController`/`AmbientLight` are confirmed absent (no code path other
  than those drives this light's intensity, so it stays at its static authored value all raid).
  Directional-light intensity/color IS always effective in Unity's normal per-light lighting,
  independent of any of the ambient-mode uncertainty above.
- **Plausible, medium confidence, genuinely unresolved by this task:** whether Equator's dominant
  `p0/Reflective/Bumped Specular[SMap]` shader family (53.5% of materials) reads Unity's ambient
  probe / EFT's custom `_SHAr`/`_EFT_Ambient` globals at all. It is a compiled shader with no
  available source; one verifier's bytecode string-scan found neither in it, but flagged the scan
  itself as inconclusive (LightMode/pass-tag strings weren't found for *any* of the 3 shaders it
  checked, including a known-working one). **This is exactly why the repair does not rely on the
  ambient fix alone** — see below.

**Fix (`ArenaLightingRepairs.cs`):** gated on `ArenaEquatorRaid.IsLoaded()`, applied once per owned
scene load and again on every live config change (`SettingChanged`), always computed from a baseline
captured once per raid (never compounds across repeated edits):

1. **Ambient** — if `Singleton<LevelSettings>.Instance` exists (confirmed case), its
   `SkyColor`/`EquatorColor`/`GroundColor` are scaled by `AmbientColorMultiplier` from their pristine
   authored values; `LevelSettings.OnPreCullCallback` then keeps re-pushing the scaled colors into
   `RenderSettings` every frame on its own — no need to fight it with a competing per-frame write. If
   the singleton is somehow absent (defensive-only fallback, should not happen), `RenderSettings`
   ambient fields are written directly instead, with a `LogWarning` flagging the unexpected state.
   `RenderSettings.fog` is written directly in both branches, since `OnPreCullCallback` never
   re-asserts fog (only `ApplySettings()`, called at `Awake`/`OnPostLoadingScene`, touches it).
2. **Lights** — every `Light` actually found by walking each *owned scene's own root GameObjects*
   (the same technique `ShaderRebind.cs` already uses for materials, so the player's own weapon
   flashlight/muzzle-flash lights — which live under a different, DontDestroyOnLoad/player hierarchy
   — are never touched) has its baseline intensity/color captured once, then:
   - the one `LightType.Directional` light ("FAKE_SUN") gets `SunIntensityMultiplier` applied to
     intensity and is blended toward white by `SunColorWhiteBlend`;
   - every other light gets `GlobalLightIntensityMultiplier` applied to intensity.

**Why brighten both ambient AND lights, given the shader-sampling uncertainty:** if the dominant
shader turns out not to read ambient at all, the light-intensity multiplier alone still brightens the
scene (real-time point/spot/directional lights always contribute via Unity's normal per-light
lighting, independent of ambient/GI). If it does read ambient, the ambient fix also helps. Either way
the raid gets brighter without depending on an unconfirmed claim, and each lever is independently
tunable live so a tester can see which one actually mattered.

**Defaults are a modest, safe uplift, not a pure no-op**, per this task's brief ("brighten a black
scene to a playable level without blowing out a correctly lit one") — see the config table below.

## 3. Live tuning — `ConfigEntry`s (section "Lighting (arena_equator)")

All six apply only during an `arena_equator` raid (`ArenaEquatorRaid.IsLoaded()`); every other map's
raid is completely untouched regardless of these values. Editable live via BepInEx
ConfigurationManager (F12) — changes re-apply immediately via `SettingChanged`, no scene reload
needed.

| Name | Default | Range | What it does |
|---|---|---|---|
| `Enabled` | `true` | bool | Master switch for every repair below. |
| `AmbientColorMultiplier` | `2.0` | `0.2` – `6.0` | Multiplies `LevelSettings`' authored ambient colors (Sky/Equator/Ground, each 0.5 grey) before they reach `RenderSettings`. `1.0` = authored dim value; `2.0` = full-bright white flat ambient. |
| `GlobalLightIntensityMultiplier` | `1.5` | `0.2` – `6.0` | Multiplies every non-directional (point/spot) `Light`'s own authored intensity in the Equator scenes. `1.0` = authored value. |
| `SunIntensityMultiplier` | `1.6` | `0.2` – `8.0` | Multiplies the one Directional light's ("FAKE_SUN") own authored intensity (1.7). `1.0` = authored value. |
| `SunColorWhiteBlend` | `0.5` | `0.0` – `1.0` | Blends the Directional light's dim/desaturated authored color (~0.18,0.18,0.20) toward white. `0` = authored color unchanged, `1` = pure white. |
| `FogEnabled` | `false` | bool | Toggles `RenderSettings.fog`. Off matches the current authored value (no-op default). **Note:** the authored `fogDensity` is currently `0`, so enabling this has no visible effect until a future bundle rebuild also authors real fog density/color — kept as a placeholder knob rather than requiring another client change later. |

## 4. Diagnostic probe (`LightingDiagnosticProbe.cs`)

Fires exactly once per raid, 5 seconds after the `Arena_Equator_Scripts` scene loads (gated on
`ArenaEquatorRaid.IsLoaded()`), as one `Plugin.Log.LogInfo` block tagged `[ArenaMapPort][Diag]`. Every
section is bounded (never a per-object dump of the ~39k GameObjects / ~477 lights) and each maps to a
specific open question from the report set:

| Section | Logs | Confirms / refutes |
|---|---|---|
| player | position, ground raycast hit (name/layer/distance), colliders overlapping an approximate player capsule (name/layer/isTrigger, capped at 10) | whether the player is actually stuck in a solid collider *this raid* — the direct empirical check for the spawn-collider fix |
| spawn markers | each `SpawnPointMarker`'s `_collider.isTrigger`/`enabled` | the spawn-collider fix's postfix actually ran and stuck |
| RenderSettings | ambientMode/ambientLight/ambientIntensity/ambient*Color, fog*, skybox, defaultReflectionMode, customReflectionTexture-bound | what is *live* right now, after the lighting repair already ran at least once |
| ambient globals | live `Shader.GetGlobalVector("_SHAr")`, `GetGlobalFloat("_EFT_Ambient")`, `_MyGlobalReflectionProbe`/`_MySimpleGlobalReflectionProbe` bound? | RC1/RC2 (AmbientLight.cs's custom globals) — read back directly instead of inferred from a shader-bytecode scan |
| camera | active camera name/tag/renderingPath, `PrismEffects` toggles + exposure fields if present | RC5 (Cam2's own baked exposure/tonemap preset) |
| lighting rig presence | `LevelSettings.Instantiated` + whether it matches the actual scene instance, its AmbientMode/SkyColor/CameraPrefab-set/AmbientType/fog, `TODSkyProvider.IsAvailable`, `TOD_Sky`/`TODSkySimple`/`WeatherController` instantiated? | "levelsettings-exists-and-is-spec-compliant" vs "levelsettings-not-actually-executing", and the TOD/WeatherController-absence premise RC1-RC3 build on |
| lights | count by type + active/enabled count, top 5 by intensity (name/type/intensity/range/color/enabled) | RC3 (sun presence/intensity/color) and whether lights are active at all |
| sample materials | up to 3 already-rebound materials: shader name, `_Color`/`_ReflectColor`, `_MainTex` bound? | materials lens M1/M7/M8 — plausible, non-black data at runtime, not just in the extraction JSON |

**What each outcome would mean**, per the open questions above:

- If `RenderSettings.ambientLight` and the top-5 lights' intensities are visibly higher than the
  bundle's raw authored values (0.5 grey; ≤8.0 max intensity) but the raid still looks dark in a
  screenshot/playtest, that is strong evidence the dominant shader genuinely does not sample either
  lever the way expected — pointing back at the still-open "does p0/Reflective read ambient/lights at
  all" question, or at the Cam2 exposure/tonemap preset (check the `PrismEffects` line).
- If `_SHAr`/`_EFT_Ambient` read back as visibly non-zero despite `AmbientLight`/`TOD_Sky` both being
  confirmed absent from our scenes, that would be a surprise worth its own follow-up (something else
  is setting these globals) — not expected, but the probe would catch it.
- If a spawn marker's `isTrigger` still reads `false`, or a collider still overlaps the player capsule
  with `isTrigger=false`, the Harmony patch did not apply this raid (check the Harmony
  `[ArenaMapPort][Harmony] target not found` log line for `SpawnPointMarker.Start`).
- If `LevelSettings.Instantiated=false`, everything this task's ambient fix relies on falls back to
  the defensive direct-`RenderSettings` path (still logged, via a `LogWarning`) — worth investigating
  why the previously-confirmed component stopped existing.

## Build result

```
dotnet build ArenaMapPort.Client/ArenaMapPort.Client.csproj -c Release --no-incremental
...
Der Buildvorgang wurde erfolgreich ausgeführt.
    0 Warnung(en)
    0 Fehler
```

0 new warnings, 0 errors. Deployed to `dist/BepInEx/plugins/ArenaMapPort/maschine-ArenaMapPort.Client.dll`
(and the usual bundle/sidecar staging) by the existing `ArenaMapPortDistCopy`/
`ArenaMapPortStageWpOutputs` MSBuild targets — unchanged.

## Deferred to a future bundle rebuild (not performed here — hard rule)

- `AuthorScriptsScene.cs`'s `LevelSettings` ambient fields stay at the flat neutral-grey placeholder
  (`SkyColor=EquatorColor=GroundColor=(0.5,0.5,0.5,1)`, `AmbientIntensity=1.0`, `fog=false`) — only the
  *runtime* multiplier changes what's live in a raid. Once live tuning on a real playtest identifies
  good target values, bake them into this file directly (candidates already extracted from Arena's
  own level51/level55 data by the lighting-data investigator report:
  `AmbientMode=Flat`, `SkyColor=black`, `EquatorColor≈(0.206,0.267,0.300)`,
  `GroundColor≈(0.08,0.08,0.08)`, `fog=true`/`FogMode.Exponential`/`fogDensity≈0.04`).
- The FAKE_SUN Directional light's own authored color/intensity in the *bundle* is unchanged — only
  the runtime multiplier/blend brightens it live. A rebuild could bake a genuinely bright/white sun
  color directly into the `_Lighting` scene instead.
- No `AmbientLight`/`TODSkySimple` component pair was authored (would give the EFT-custom `_SHAr`/GI
  pipeline a real source instead of leaving it permanently zero) — flagged by the report set as a
  larger, out-of-scope-for-this-pass effort requiring careful field-packing verification against
  `AmbientLight.cs`.
- No `ReflectionProbe` wiring for the 18 already-imported Cubemap assets (materials report M3/
  lighting-data's `reflectionprobes-out-of-scope`) — one verifier's own re-check suggests most `_Cube`
  slots are likely already bound per-material rather than orphaned, so this is lower priority than
  originally ranked, but still unconfirmed.
- No shader-property remap for `Global Fog/Transparent Reflective Specular` (materials M2, confirmed
  schema mismatch vs Arena's Unity 6 water-style shader of the same name) — affects 16 glass/window
  materials only, unrelated to the two symptoms this task addresses.
- No exposure/tonemap override for the Cam2 fallback camera's own `PrismEffects` preset (RC5) — the
  diagnostic probe now logs its live toggle/exposure values so a real playtest can show whether this
  is even worth pursuing before writing a speculative override.
- The GameObject layer-index verbatim-copy question (`lighting-data`'s `layer-indices-copied-verbatim`)
  was not investigated further — out of scope for a lighting-brightness/movement fix.

## Review fix-ups

One reviewer finding on the uncommitted `ArenaLightingRepairs.cs` diff, verified and fixed:

**Finding: direct `RenderSettings.fog`/`ambient*` writes bypass the scene-reload safety net and are
never explicitly reset on raid end.** CONFIRMED, real defect (low impact under current defaults).
Re-checked directly against the decompile: `LevelSettings.ApplySettings()`
(`D:\SPT41\Development\_decompiled\LevelSettings.cs:194-218`) is the *only* place that writes
`RenderSettings.fog`, and it only runs from `Awake()`/`OnPostLoadingScene()` (`:166-171`, `:173-185`)
— never from the per-camera `OnPreCullCallback` (`:240-263`), which re-pushes only the four ambient
fields, exactly as this file's own existing comment already said. So a `RenderSettings.fog` (or, on
the defensive fallback path, `RenderSettings.ambient*`) value our mod writes stays live as literal
global engine state until *something else* overwrites it — normally the next raid's own
`LevelSettings.Awake()`, but not necessarily whatever scene loads immediately after ours (e.g. a
results/menu scene with no `LevelSettings` of its own). Confirmed `FogEnabled`'s default (`false`)
matches the authored `LevelSettings.fog = false` (`AuthorScriptsScene.cs:83`), so today's default
config is a no-op in practice — the gap only bites if `FogEnabled` is toggled on live, or if the
"should not happen" fallback path ever actually runs.

**Fix applied (`ArenaMapPort.Client/ArenaLightingRepairs.cs`):** `OnSceneUnloaded` (fires once per
raid, on the Scripts scene's own teardown, right where `_levelSettingsBaselineCaptured`/`_lights` were
already being reset) now also explicitly restores `RenderSettings.fog = false` unconditionally, and —
gated on a new `_ambientFallbackWritten` flag set only when `ApplyAmbient()`'s defensive fallback
branch actually ran this raid — restores `RenderSettings.ambientMode`/`ambientLight`/
`ambientSkyColor`/`ambientEquatorColor`/`ambientGroundColor` to the authored flat grey
(`AuthorScriptsScene.cs:78-83`). The primary (LevelSettings-found) path was left writing
`RenderSettings.fog` directly during the raid, unchanged — per the existing comment, that is still
required for the `FogEnabled` toggle to take live effect, since `OnPreCullCallback` never re-asserts
fog itself; only the post-raid leak is new behavior.

**Build:** `dotnet build ArenaMapPort.Client/ArenaMapPort.Client.csproj -c Release --no-incremental`
→ `Der Buildvorgang wurde erfolgreich ausgeführt.` — 0 Warnung(en), 0 Fehler. Deployed to
`dist/BepInEx/plugins/ArenaMapPort/maschine-ArenaMapPort.Client.dll` (bundles/sidecars re-staged
unchanged by the existing MSBuild targets). Not in-game tested this pass (no bundle/gameplay change,
runtime-only cleanup on a code path — the fallback branch — that has not been observed to fire).

## Lead follow-ups (after the workflow)

- **Fill light** (`ArenaLightingRepairs.ApplyFillLight`, config `FillLightIntensity`, default 0.4, range 0–3): a shadowless
  directional pair (down, plus half intensity up) created inside the Scripts scene. Reason: the dominant `p0/*` shaders get
  ambient from EFT's `AmbientLight` "Custom Ambient" command buffer (`AmbientLight.cs:311`, fed by `SetSH` from
  `ToDController.cs:265` / `TODSkySimple.cs:217`). That rig is part of the level's weather/TOD setup, which our scenes do
  not have, so LevelSettings/RenderSettings ambient alone never reaches those materials. Direct lights do.
- `Enabled=false` now applies neutral values (multipliers 1, blend 0, fill off) instead of freezing the last tuned state.
- Spawn triggers: second pass `SpawnColliderRepairs.EnsureAllTriggers()` from the `CreateFromScene` postfix, in case
  `SpawnPointMarker.Start` ran before `ArenaEquatorRaid.IsLoaded()` turned true.
- Probe: waits for `AbstractGame.Status == Started` (max 180 s) before the 5 s delay; reads `_EFT_Ambient` as a vector
  (`AmbientLight.cs:677`); logs the camera's component list and its AfterLighting command buffer names (a
  `'Custom Ambient'` entry would mean an AmbientLight pass exists after all).
