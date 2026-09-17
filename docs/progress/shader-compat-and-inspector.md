# shader-compat-and-inspector — Bay5 ground/lighting value fixes + F10 scene inspector (progress log)

Role: builder. Task: implement `PLAN.md` (synthesizing `shader-compat-audit`, `ground-chain`,
`light-levels`, `black-square-and-inspector`) **with the adversarial critic's amendments applied**, for
the 2026-09-17 14:54 Bay5 playtest symptom: blown-white ground with large soft-edged black patches,
extreme prop contrast, a blown white-cyan horizon band, and a floating black square over the sea.

**Result: implemented and built (0 new warnings), dist DLL hash confirmed == bin DLL hash. Not yet
playtested.** All changes are client-only (`ArenaMapPort.Client`) — no `tools/`, `UnityProject Editor`,
`Shared`, `Server`, or `base.json` file was touched, so none of the 12 maps'
`full-map-validation.json` receipts are invalidated. No git commit made (workspace rule).

---

## 0) Why this needed a critic pass first — two bugs that made the original plan a no-op

The plan's flagship fix (a1) was reviewed against the *live* repo and the *built* `dist` bundle
(not just its own report citations) before implementation, and both of these were caught and fixed
before any code was written:

1. **Wrong map-key literal.** The plan's gate was `map.Key != "bay5"`. Bay5's real
   `ArenaMapDescriptor.Key` is `"arena_bay5"` (`ArenaMapPort.Shared/ArenaMapDescriptor.cs:119`,
   verified again directly in this pass). `"bay5" != "arena_bay5"` for every raid including Bay5's own,
   so the guard would have returned unconditionally — a permanent no-op that would still log nothing
   and change nothing. **Fixed** by gating on `ArenaMapRegistry.Bay5.Key` (the registry field, not a
   hand-typed string literal — structurally rules out this exact class of typo) instead of a literal.
2. **Wrong material-identity check.** The plan matched `MaterialKey(material.name) ==
   "Bay5_Vertex_Paint_Ground"`. Materials are pipeline-generated and named by their extraction hash at
   import time — confirmed directly at `UnityProject/Assets/ArenaMapPort/Editor/Import/MaterialImporter.cs:60`
   (`mat.name = matId;`) — so `material.name` at runtime is `"be005d9cd5d9384e"`, never the human-readable
   source name. Independently confirmed: `work/extract/bay5/materials/be005d9cd5d9384e.json` has
   `"source_name": "Bay5_Vertex_Paint_Ground"` and `"floats"."_BlendStrength": 5.0`; the sidecar
   (`dist/.../material-shaders.arena_bay5.json`) is keyed the same hash way and carries no human-name
   field. **Fixed** by matching on the hash (`Bay5GroundMaterialHash` const in `ShaderRebind.cs`), with a
   drift guard (§2) since a re-extraction can reassign the hash.

Both bugs were independent — either alone would have made `ApplyKnownValueFixes` fire on nothing, for
every raid, silently.

---

## 1) Files changed

- **`ArenaMapPort.Client/ShaderRebind.cs`** — new `ApplyKnownValueFixes` step (§2, plan a1, amended) run
  from `Rebind()` right after the sidecar renderQueue/keywords restore, before the `_reboundCounts`
  update; new `[MaterialFixes]` config section (5 keys, bound in `Install()`); new shader-name fallback
  table + insertion point in `Rebind()` (§3, plan a2, amended with a map-scope guard).
- **New: `ArenaMapPort.Client/SceneInspector.cs`** — F10 live diagnostic hotkey (§4, plan b), adopted
  from the pre-written draft with one build-breaking API-shape bug fixed (§4.1) and its "design draft,
  not wired" header replaced.
- **`ArenaMapPort.Client/Plugin.cs`** — `SceneInspector.Install();` added to `Awake()`, after the
  existing `DaytimeVisualsProbe.Install();` call (order doesn't matter; no dependency on the other
  repairs).
- **`ArenaMapPort.Client/CameraEffectsRepairs.cs`** — `[Camera] ExposureUpperLimit` default changed
  `1.3f` → `4.0f` (§5, plan c1), doc comment amended to remove the plan's incorrect "retry live in the
  same raid" claim (§5.1, critic-refuted).

No file under `tools/`, `Shared`, `Server`, `UnityProject/.../Editor/`, or `base.json` was touched.

---

## 2) (a1) Bay5 ground `_BlendStrength`/smoothness correction — evidence and confidence

**Mechanism (verified, high confidence).** Disassembly of the shader's pixel program
(`Custom/Vert Paint Shader Solid`, cited from the critic's `disasm_pixel_2.txt` re-derivation) shows
`weight_i = (Heights_i · vertexColor_i)^BlendStrength / Σ` — raising `_BlendStrength` genuinely sharpens
vertex-paint layer selection toward winner-take-all, which is consistent with large, soft-edged,
near-binary white/black patches.

**"Statistical outlier" framing (refuted, do not repeat).** The original plan called Bay5's
`_BlendStrength=5.0` "the highest `_BlendStrength` in either dataset." This is contradicted by data
already collected in this investigation:
- `vanilla_by_shader.json` (5 real SPT-shipped ground/wall materials on this shader): min=4.0,
  **median=5.0 — exactly Bay5's value**, max=17.45.
- `arena_real_value_stats.json` (77 of Arena's own port-wide materials on this shader): min=1.0,
  median=1.5, max=17.6.
- `spt_shader_defs.json`'s own declared range for `_BlendStrength`: default 1.0, min 0.1, **max 30.0** —
  17+ is inside the designed range, not an overflow.

So Bay5's 5.0 sits at the vanilla population's *median*, not its maximum; the road material
(`_BlendStrength=2.0`) is *lower* than three of the five broad vanilla samples. **Confidence: this value
is a plausible contributor to the sharpening mechanism above, not a confirmed anomaly.** The fix below
is a value-tuning experiment, not a correction of a demonstrably-wrong number — the config description
says this explicitly so a future reader doesn't re-inherit the overclaim.

**Smoothness/Fresnel — elevated to co-primary (amendment).** Directly measured on the ground material
(`be005d9cd5d9384e.json`): `_Smoothness0/1/2 = 0.853/0.871/0.870` — near-mirror on *all three* layers
simultaneously, vs. vanilla's broad-sample medians of 0.31/0.24/0.27 (max 0.75), combined with
`_ReflectionStrength=1.0` and nonzero `_Specular0-2`. The road's `_ReflectionStrength=0.0` and
`_Specular0-2=0,0,0` plausibly suppress the same effect there (left untouched, matches the original
plan's own dismissal). The original plan shipped this OFF as a "second-tier, less-evidenced" follow-up;
the critic's review points out that once the identity bugs above are fixed, there is **no isolation
cost** to testing both together in the same playtest, and recommends elevating it to co-primary rather
than a follow-up experiment — implemented that way: **default ON**, alongside `_BlendStrength`.

**Competing/complementary factor — documented, not fixed this pass.** Vertex-color data pulled directly
from the *built* ground mesh (`Bay5_Grass_LOD0`, 305 verts, from `dist/.../arena_bay5_scenes.bundle`)
shows the blue channel (layer-2 weight) is `min=0.0, max=0.0` — constant zero across all 305 vertices.
Layer 2 contributes nothing anywhere on this mesh regardless of `_Heights`/`_BlendStrength`, and 305
vertices covering "the whole harbor yard" is coarse by construction — an independent, mesh-density/
import-side contributor to the "large" part of the symptom. This is an Editor-import-pipeline matter
(receipt-invalidating) and is listed under deferred fixes (§7); it is **not** something a client-side
material-value patch can address, and is called out here so it isn't mistaken for something (a1)
already fixes.

**Drift guard (new, not in the original plan).** Because `material.name` is a pipeline-generated
extraction hash (§0.2) rather than a stable identifier, `ApplyKnownValueFixes` now also compares the
*pre-fix* `_BlendStrength` reading against the value (`5.0`) that was measured when the hash
`be005d9cd5d9384e` was identified as `Bay5_Vertex_Paint_Ground`. If a future re-extraction ever
reassigns that hash to a different material, this logs a loud, one-time
`[ArenaMapPort][MaterialFixes]` warning instead of silently mis-applying the fix to the wrong material a
second time — the exact failure class the critic's review caught once already, now made self-detecting.

**Road material (`Bay5_road_asphalt_01`, hash `c0cee7841b421fb1`) — intentionally NOT touched by a1.**
Its `_BlendStrength=2.0` sits inside vanilla's own observed range; no evidence it needs correction. Its
`_Smoothness0-2≈0.9`/`_FresnelSpecular2=0.77` are still worth a visual check in the playtest (checklist
step 6) — if still visibly blown, extending the same mechanism to it is a follow-up, not this pass.

---

## 3) (a2) Missing-shader fallback table — real bug, tentative mapping, OFF by default

Separately real, independent bug (not part of the ground symptom): 14 Arena shader names have no
compiled SPT shader at all; `ShaderRebind.Rebind`'s `FindLoadedExact(originalName) ?? Shader.Find(...)`
returns null for them, and the material stays on the stand-in shader, whose fragment program is a
hardcoded magenta (`UnityProject/.../Shaders/StandinShaderGenerator.cs:404`,
`return fixed4(1,0,1,1);`). Affects 162 materials / 6,942 renderers (~2.7%/1.6% of the port).

| Original (no SPT shader) | Fallback (real SPT shader) | Renderers | Confidence |
|---|---|---|---|
| `Custom/WorldTriplanarFresnelPro` | `p0/Reflective/Bumped Specular SMap` | 2,544 | inferred, loses triplanar |
| `ANGRYMESH/PBR Rocks/PBR BlendTopDetail (Legacy)` | `MK4/Rock` | 1,898 | inferred |
| `Cloth/ClothShader` | `p0/Cutout/Bumped Diffuse` | 1,141 | inferred, loses cloth sim |
| `Cloth/ClothShader_backface` | `p0/Cutout/Bumped Diffuse` | 444 | inferred, loses cloth sim |
| `Custom/Billboard_FogSheet_Simple` | `Unlit/Transparent Colored` | 280 | inferred |
| `p0/Reflective/Bumped Specular SMap Baked` | `p0/Reflective/Bumped Specular SMap` | 32 | high (near-identical name) |
| `Nature/SpeedTree8` | *(omitted — see below)* | 52 | low; not shipped |

`Nature/SpeedTree8` deliberately has **no** table entry: its only SPT analog (`Nature/SpeedTreeEFT`) has
a materially different property set, and an honestly-wrong magenta stand-in beats a plausible-looking
wrong tree.

**Amendment — map-scope guard (critic finding, implemented, not just documented).** The original plan's
insertion point had no `map.Key` filter. `shader_compat_raw.json`'s own per-map shader-usage lists show
`Cloth/ClothShader`, `Cloth/ClothShader_backface`, and `Custom/Billboard_FogSheet_Simple` are **also**
used on `arena_equator` — the one map this investigation explicitly called "already acceptable" and
never re-evaluated against these unverified substitutes. Flipping `EnableShaderFallbacks=true` as
originally written would have silently reassigned Equator's cloth/fog-sheet materials too. **Fixed**:
the fallback lookup in `Rebind()` is now additionally gated on `map.Key == ArenaMapRegistry.Bay5.Key`,
so enabling the switch this pass can only ever affect `arena_bay5`; widening it to other maps needs a
dedicated review, not a side effect of one global toggle.

Shipped `EnableShaderFallbacks = false` (default OFF), per the original plan's own recommendation — real
bug, but unrelated to the ground/lighting symptom this pass targets, and its mappings are explicitly
inferred/unverified. Ready to flip on (Bay5-only) in a dedicated follow-up pass.

---

## 4) (b) F10 scene inspector

Adopted from the pre-written draft at
`<local-scratchpad>/ground\SceneInspector.cs.draft`
into `ArenaMapPort.Client/SceneInspector.cs`, wired via `SceneInspector.Install()` in `Plugin.cs
Awake()`. No map gate — works on any raid, arena or vanilla.

### 4.1 Build-breaking bug found and fixed during this pass (not in the plan or the critic review)

The draft called the **static, newer-Unity-style** shader-reflection API:
`Shader.GetPropertyCount(sh)` / `Shader.GetPropertyName(sh, i)` / `Shader.GetPropertyType(sh, i)`. This
project's actual referenced Unity build (`D:\SPT41\EscapeFromTarkov_Data\Managed\UnityEngine.CoreModule.dll`,
Unity 2022.3.43f1/Mono) exposes these as **instance** methods on `Shader` with no such static overload
— confirmed by loading the assembly and reflecting `Shader`'s methods directly (all `GetProperty*`
members are instance, e.g. `Int32 GetPropertyCount()`, `String GetPropertyName(Int32
propertyIndex)`). The draft failed to compile with `CS1501` (no overload takes N arguments) at
`SceneInspector.cs:300,307,308`. **Fixed** by calling `sh.GetPropertyCount()`, `sh.GetPropertyName(i)`,
`sh.GetPropertyType(i)` (instance form) instead.

### 4.2 Usage

Press **F10** (configurable) while in a raid. Fires a center-screen ray from the active camera, finds
the nearest `MaxResults` (default 5) renderers whose world-space AABB (`Renderer.bounds`) the ray
intersects — no collider required — within `MaxDistance` (default 2000 m), and logs one
`[ArenaMapPort][Inspector]` block per hit to `BepInEx/LogOutput.log`: hierarchy path, scene, layer,
active flags, mesh name/vertex count/readability/vertex-color presence, shadow/lightmap/reflection-probe
settings, and per-material `name`/`shader`/`renderQueue`/`mainTexture`/keywords plus every
Color/Float/Range shader property (Vector/Int/Texture properties too when `VerboseShaderProperties=true`).
Also runs a `Physics.Raycast` for comparison when `AlsoPhysicsRaycast=true` (default) — a "no hit" there
against a bounds-scan hit is itself a useful signal (most decorative/collider-less renderers legitimately
have no collider).

Never throws out of `Update()` or the inspection itself — every renderer's block and the physics
comparison are individually try/caught, matching this plugin's existing hook-safety convention.

---

## 5) (c) Exposure default change

`[Camera] ExposureUpperLimit` default raised `1.3` → `4.0` in `CameraEffectsRepairs.cs`. Cam2's own
baked value (1.3) is a severe, log-confirmed ceiling on how far `PrismEffects` auto-exposure can darken
a bright frame (≈0.093 minimum multiplier at 1.3 vs. ≈0.0019 at the script's own class default of 6 —
roughly 50× less headroom); raising it is zero pipeline risk and directly addresses a confirmed
mechanism. `4.0` is a first test value, not asserted as final — `6.0` (the script default) is the
next value to try if the ground/props are still blown.

`EnableTODScatteringFix` (`[Camera]`, default `true`, pre-existing) is left as a diagnostic A/B toggle
only, unchanged this pass — see playtest step 5. `[Lighting (arena_bay5)]`
`AmbientColorMultiplier`/`GlobalLightIntensityMultiplier`/`SunIntensityMultiplier` are explicitly **not**
touched — ambient was measured already at its un-multiplied authored baseline at blowout time
(`RenderSettings.ambientLight=(0.5,0.5,0.5,1)` exactly), so a non-ambient-driven blowout can't be fixed
there, and raising them would fight the exposure fix.

### 5.1 Refuted claim removed (critic finding)

The plan's prose said the exposure value could be retried at `6.0` "in the same raid" because config is
"read every apply, no rebuild needed." This is wrong and has been removed from the config's own doc
comment: `ApplyExposureFix` (`CameraEffectsRepairs.cs`) early-outs once `prism.useExposure=true` for a
given camera, and — unlike `ArenaLightingRepairs.cs:184-190`, which wires `SettingChanged` on each of
its `ConfigEntry`s — **`CameraEffectsRepairs.cs` has no `SettingChanged` wiring at all**. Changing
`ExposureUpperLimit` via F12 mid-raid does not take effect; each value needs a fresh raid load. The
config's own bound description now says this explicitly so nobody burns a playtest cycle on it again.

---

## 6) (d) Black square — unchanged, no code fix this pass

Confirmed by the static sweep (23,141 renderers across Bay5's own 8 owned scenes) that nothing matches
"isolated, unlit-black, currently rendering, high over the sea" — ruling out the TOD dome, decal-
projector boxes, `Standard`-shader cube markers, and empty-material `DecalGraffiti` quads. Leading but
unconfirmed hypothesis: a non-dome sibling under `WeatherRig`'s harvested Reserve hierarchy keeping the
donor scene's raw world coordinates (`WeatherRig.cs:118-122,373-406`) — weak fit, since the same square
reportedly appeared in the very first (pre-rig) Bay5 test too, which would rule this out entirely if
confirmed. **Identification, not a fix, is this pass's deliverable** — use F10 (§4) on the square during
the next playtest to get a definitive scene/path/shader/mesh reading (playtest checklist step 7).

---

## 7) Deferred pipeline fixes (out of scope this pass — receipt impact noted)

All of these touch `tools/`, `UnityProject/Assets/.../Editor/`, or `base.json`, which invalidates all 12
map validation receipts (~4 h refresh) — **not implemented this pass**.

| Fix | File(s) | Evidence | Receipt impact |
|---|---|---|---|
| Author `LevelSettings.HeightFalloff`/`ZeroLevel` per map from that map's real world-space ground/sea Y, instead of leaving bare class defaults (Bay5's horizon-band root cause) | `UnityProject/Assets/ArenaMapPort/Editor/Import/AuthorScriptsScene.cs:74-104` | light-levels F4; zero occurrences of either field anywhere in `work/extract/bay5`; `TOD_Scattering.cs:165-176`'s consumption of these fields | Editor Import → all 12 receipts |
| Bay5 ground mesh vertex-color/density gap — `Bay5_Grass_LOD0`'s blue channel (layer-2 weight) is constant 0.0 across all 305 vertices (§2, this doc) | Editor import / source mesh | measured directly against `dist/.../arena_bay5_scenes.bundle` this pass | Editor Import → all 12 receipts |
| Texture sRGB import should consult the extracted per-texture `color_space` field for every texture, not just `generated_terrain_control` | `TextureImportDriver.cs:113-114` | ground-chain F8; not implicated in the current ground bug, but a real generic gap for the other 13 maps | Editor Import → all 12 receipts |
| Port/author real replacement shaders for the 14 no-SPT-shader cases, or promote the client-side fallback table (§3) into a permanent pipeline-level reassignment once verified | `tools/` material pipeline, possibly `base.json` | shader-compat-audit F2 | tools/ change → all 12 receipts (only if/when promoted) |
| `Vert Paint Shader Solid` mask-texture property gaps (`_UseSmoothnessMask`/`_UseTintMask` Arena-side, `_UseMergedTextures` SPT-side) | n/a | 0/84 materials port-wide set these nonzero | **No action needed** — documented only |
| `p0/Reflective/Bumped Specular SMap`'s `_UseCutoff` gap | n/a | 0/2,695 materials set this nonzero | **No action needed** — documented only |
| Deeper vanilla-value sampling; full DXBC reconstruction of post-blend lighting/Fresnel/reflection-probe math | n/a (research) | ground-chain | No receipt impact — time-budget only |
| Time-of-day lamp switching parity (Bay5's 109 active lights never gated by time-of-day) | would be client-only (`ArenaLightingRepairs.cs`) if pursued | light-levels F5 | **No receipt impact if implemented** — out of scope this pass, low priority |

---

## 8) Config keys (all in `com.maschine.ArenaMapPort.cfg`, generated on first launch after deploy)

**`[MaterialFixes]` — new section, `ShaderRebind.cs`:**

| Key | Type | Default | Notes |
|---|---|---|---|
| `FixBay5GroundBlendStrength` | bool | `true` | §2. Plausible contributor, not a confirmed-wrong value. |
| `Bay5GroundBlendStrengthValue` | float | `2.0` | Applied to hash `be005d9cd5d9384e` only. Fresh raid load to change. |
| `FixBay5GroundSmoothness` | bool | `true` | Elevated to co-primary (amendment, was OFF in the original plan). |
| `Bay5GroundSmoothnessScale` | float | `0.6` | Multiplies `_Smoothness0-2`/`_FresnelSpecular2`. |
| `EnableShaderFallbacks` | bool | `false` | §3. When enabled, applies ONLY to `arena_bay5` (map-scope guard, amendment). |

**`[Camera]` — existing section, `CameraEffectsRepairs.cs`:**

| Key | Type | Default (was) | Notes |
|---|---|---|---|
| `ExposureUpperLimit` | float | `4.0` (was `1.3`) | §5. Needs a fresh raid load to take effect — not live-tunable. |
| `EnableTODScatteringFix` | bool | `true` (unchanged) | Diagnostic A/B toggle for the horizon band; real fix is deferred (§7). |

**`[Inspector]` — new section, `SceneInspector.cs`:**

| Key | Type | Default | Notes |
|---|---|---|---|
| `Hotkey` | KeyboardShortcut | `F10` | |
| `MaxResults` | int | `5` | Nearest bounds-ray hits logged per keypress. |
| `MaxDistance` | float | `2000` | Meters. |
| `VerboseShaderProperties` | bool | `false` | `true` also logs Vector/Int shader properties. |
| `AlsoPhysicsRaycast` | bool | `true` | Comparison `Physics.Raycast` alongside the bounds scan. |

---

## 9) Test instructions

Single raid session, `arena_bay5`, noon, weather rig active (same conditions as the 2026-09-17 14:54
test). Same camera vantage as the existing screenshots
(`Screenshot-2026-09-17-083744.png`, `Screenshot-2026-09-17-145751.png`) where possible.

**Before loading the raid**, deploy this build (already done — see §10) and, in
`com.maschine.ArenaMapPort.cfg` or via in-raid F12 ConfigurationManager, confirm/set:
- `[Camera] ExposureUpperLimit = 4.0` (now the shipped default)
- `[MaterialFixes] FixBay5GroundBlendStrength = true`, `Bay5GroundBlendStrengthValue = 2.0` (shipped default)
- `[MaterialFixes] FixBay5GroundSmoothness = true`, `Bay5GroundSmoothnessScale = 0.6` (shipped default, elevated to co-primary)
- `[Camera] EnableTODScatteringFix = true` for the primary run; A/B to `false` only in a second raid load

**In raid, capture (screenshot + F10 log each time):**
1. Baseline screenshot from roughly the same angle as `Screenshot-2026-09-17-145751.png`, before
   touching anything else.
2. Aim at the open white/glowing ground, press F10. Expect material `be005d9cd5d9384e` (not the human
   name — see §0.2), shader `Custom/Vert Paint Shader Solid` (confirms rebind worked), `_BlendStrength`
   reading **2.0** (confirms a1 applied — was 5.0 pre-fix), `_Smoothness0-2` reading **≈0.51/0.52/0.52**
   (0.853/0.871/0.870 × 0.6 — confirms the smoothness fix applied), mesh `Bay5_Grass_LOD0`
   (305 verts — note the coarse vertex-color density flagged in §2/§7).
3. Aim at a dark/black patch of ground nearby, press F10. Compare against step 2: same
   renderer/material with the same post-fix values is expected (confirms it's the same vertex-paint
   blend, just a different layer winning locally); a different renderer/material entirely would
   contradict the diagnosis and reopen the investigation.
4. Aim at a representative prop (crate/container wall) showing pale-sunlit/near-black-shadow contrast,
   press F10. Expect shader `p0/Reflective/Bumped Specular SMap`. Judge visually whether
   `ExposureUpperLimit=4.0` alone tamed the contrast.
5. Look at the sky/horizon band (visual only). Note whether it's still a blown white-cyan band. If a
   second raid load is feasible, toggle `EnableTODScatteringFix=false`, reload, compare — if the band
   changes, prioritize the deferred `AuthorScriptsScene.cs` fix (§7) next pass. **Do not expect this to
   change live** — see §5.1, it needs a reload either way.
6. Aim at the road/asphalt area (`c0cee7841b421fb1`, untouched by a1), press F10, capture
   `_Smoothness0-2`/`_FresnelSpecular2` — sanity-check whether it's still visibly blown; informs whether
   to extend a1 there next pass.
7. Find the floating black square (right side of frame, above the sea, same as both prior screenshots),
   aim, press F10 (`AlsoPhysicsRaycast=true` default). Record scene name (`Arena_Bay5_*` = our own
   content, anything else = confirms the WeatherRig/Reserve-leftover hypothesis), full hierarchy path,
   shader, mesh name/vertex count, and whether the physics raycast got a hit.
8. After-screenshot from the same angle as step 1.
9. Grep `BepInEx/LogOutput.log` for `[ArenaMapPort][MaterialFixes]`, `[ArenaMapPort][Inspector]`,
   `[ArenaMapPort][Camera]`, `[ArenaMapPort][Diag]`/`[DaytimeVisualsProbe]` — confirm
   `prism.exposureUpperLimit` logs `4.0` and the `_BlendStrength` line shows `5.0 -> 2.0`. If a
   `[ArenaMapPort][MaterialFixes] material 'be005d9cd5d9384e' found ... but its pre-fix
   _BlendStrength=X, not the expected 5.0` warning appears, the extraction hash has drifted — stop and
   re-identify the material before trusting the rest of the log (§2 drift guard).

**Success signal:** ground no longer reads as a single blown-white sheet with hard-edged black patches
(some contrast from the vertex-paint blend is expected and correct — the goal is softening the
near-binary winner-take-all look, not flattening it to uniform grey). Step 7's inspector reading turns
the black square from "unidentified" into an actionable next-pass fix.

---

## 10) Build result

```
dotnet build D:\ArenaMapPort\ArenaMapPort.Client\ArenaMapPort.Client.csproj -c Release -p:ArenaMapPortDllOnly=true
```
Result: **Build succeeded — 0 Warnung(en), 0 Fehler** (0 warnings, 0 errors).

- `bin` DLL: `ArenaMapPort.Client\bin\Release\maschine-ArenaMapPort.Client.dll`
- `dist` DLL: `dist\BepInEx\plugins\ArenaMapPort\maschine-ArenaMapPort.Client.dll`
- SHA-256 (both, confirmed identical): `e6d0ef48b3a47090a9b8896e105e123633262a27f6eb46357372654874f91495`
- Baseline dist DLL hash before this pass's changes (for reference): `75957c483f0d0c613fce3bec4c012bd0db821596b69816cea78d7c910e77086d`
