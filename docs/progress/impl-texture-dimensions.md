# impl-texture-dimensions — texture dimension import fix (progress log)

Role: builder, task label `impl-texture-dimensions`. Task: fix the ">150 Unity errors: Error
assigning 2D texture to CUBE texture property _Cube: Dimensions must match" + 1 `_ParticleTex`
Texture3D error observed at scene load on 2026-09-14 — a Cubemap/Texture3D source decoded and
imported as a plain 2D texture, then assigned to a shader property the real BSG shader declares
Cube/3D.

*(This file is being filled in as the pipeline runs; see the end for the final summary once the
Unity build + validators complete.)*

---

## 1) Root cause, confirmed against real data

Investigated the real Equator package (5 content scenes) directly via UnityPy before touching any
code: **18 distinct `Cubemap` objects** (all referenced only via the `_Cube` TexEnv slot, across 8
shader names — every `p0/Reflective/Bumped Specular*` family member, `Decal/Water Deferred Decal`,
`Custom/TextureGlitch`) and the **1 known `Texture3D`** (`EquatorParticleTex3 lineScale`,
`_ParticleTex`, already partially handled by an earlier pass — see below). Reference counts across
materials: the 18 Cubemaps are referenced by 1–62 materials each, summing to ~220 material
references — matching the reported ">150 Unity errors" (one property-assignment mismatch per
renderer instance using an affected material).

Confirmed via UnityPy that `Cubemap.image_data` is 6 consecutive faces, each `m_CompleteImageSize`
bytes (the FULL per-face mip chain) — `6 * m_CompleteImageSize == len(image_data)` exactly for
every one of the 18 real Cubemaps. `UnityPy`'s own `.image` property (what the extractor's `else`
branch used before this fix) calls `get_image_from_texture2d`, which reads only `m_Width x
m_Height` bytes from the FRONT of that blob — silently decoding face 0 alone as an ordinary
rectangular PNG, with no indication anywhere in the package that a Cubemap's data was truncated
this way. That PNG then got imported as a plain `Texture2D` asset and `Material.SetTexture`-
assigned into the `_Cube` slot — the exact origin of the "2D texture assigned to CUBE property"
runtime error, compounded by the stand-in shader itself declaring `_Cube` as ShaderLab type `2D`
(not `Cube`) unconditionally for every TexEnv property, regardless of the actual referenced
texture's real class.

Confirmed the compiled-shader ground truth directly out of a previously-built bundle
(`work/build/full/arena_equator_scenes.bundle`, pre-fix): a texture property's declared dimension
lives at `Shader.m_ParsedForm.m_PropInfo.m_Props[].m_DefTexture.m_TexDim` — `2`=2D, `3`=3D,
`4`=Cube, `5`=2DArray (`UnityEngine.Rendering.TextureDimension`). Before this fix, `_Cube` read
`m_TexDim==2` (wrong); this is now the validator's own ground truth for the new cross-check (§3).

## 2) Fix — extractor (`tools/extract/extract_equator.py`)

- Added `dimension` field to every `manifest.json` texture entry (§C-compatible extension):
  `"Tex2D"` (default), `"Cube"`, `"Tex3D"`, `"Tex2DArray"` (recorded defensively; none observed).
- Cubemap: decode all 6 faces for real (top mip level, via the same low-level
  `Texture2DConverter.parse_image_data` the rest of the pipeline already uses — no new decode
  path), stitch into one **vertical 6-frame strip PNG** (`width x height*6`, face order
  `+X,-X,+Y,-Y,+Z,-Z`, the standard Unity strip-layout/CubemapFace order), record
  `cube_layout`/`cube_face_order`/`cube_face_width`/`cube_face_height` alongside `dimension`.
  Verified structurally (not pixel-exact, per spike tolerance): all 6 decoded faces of a sampled
  real Cubemap are full, coherent, non-garbage 256x256 images.
- Texture3D: kept the existing (already-working, prior-session) flatten-to-stacked-slices decode;
  only added `dimension: "Tex3D"` metadata and updated the warning text to reflect the new
  never-assign policy (§3).
- `tools/validate/validate_extract.py`: added dimension-field validation (must be one of the 4
  known values; Cube entries must carry a consistent `cube_layout`/`cube_face_order`/face
  dimensions whose product matches the PNG's own width/height).

Re-ran the full 5-scene extraction against the (relocated) source
`D:/Battlestate Games/Escape from Tarkov Arena/EscapeFromTarkovArena_Data`:
same counts as the prior session (39,398 objects / 2,605 meshes / 332 materials / 708 textures),
**0 errors**, 219 warnings (200 benign "no mesh assigned" + 1 Texture3D-flatten note + 18 new
Cubemap-strip notes). `validate_extract.py --skip-reproducibility`: **0 failures**. Extraction wall
time: ~2m17s.

## 3) Fix — Unity importer (C#)

- `ArenaPackage.cs`: added `TextureDimension(texId)` (defaults to `"Tex2D"` for back-compat) +
  `TextureCubeFaceWidth/Height`.
- `TextureImportDriver.cs`: branches on dimension. `"Cube"` → `TextureImporterShape.TextureCube` +
  `TextureImporterGenerateCubemap.AutoCubemap` (auto-detects the 1:6 strip WP1 emits), loads the
  result as a real `Cubemap` asset. `"Tex3D"`/`"Tex2DArray"` → imported as an ordinary, unused 2D
  texture (kept only for inspection; never looked up by MaterialImporter) — task's explicit "if a
  correct import is not feasible, do not assign" fallback, since building a real Texture3D/
  Texture2DArray Unity asset from a flattened PNG is out of scope for the spike's one real
  occurrence. Return type changed `Dictionary<string,Texture2D>` → `Dictionary<string,Texture>`
  (base class — a Cube entry is a `Cubemap`, not a `Texture2D`).
- `MaterialImporter.cs`: for a TexEnv with a non-null `texture_id`, checks the resolved dimension —
  `Tex3D`/`Tex2DArray` are logged and left **unassigned** (shader default applies); `Tex2D`/`Cube`
  assign the real, correctly-shaped asset TextureImportDriver built. Never binds a 2D texture to a
  Cube/3D-declared property.
- `StandinShaderGenerator.cs`: `ArenaPropType` split `Tex` → `Tex2D`/`TexCube`/`Tex3D`/`Tex2DArray`,
  resolved per TexEnv from the manifest's `dimension` field (two-pass collection so a real
  resolution always wins over the "declared but every reference is unassigned" Tex2D fallback).
  Stand-in ShaderLab declarations now use the real `Cube`/`3D`/`2DArray` property types instead of
  declaring every texture slot `2D` unconditionally (task item 2's "check the stand-in shader
  property declarations use the right types").
- `BuildBundles.cs`: updated the one local variable's type to match TextureImportDriver's new
  return type.

## 4) Fix — validator (`tools/validate/validate_bundle_roundtrip.py`)

- `load_package_expected()`: in-scope texture ids now split by dimension
  (`texture_count_in_scope_2d` / `texture_count_in_scope_cube` /
  `texture_ids_in_scope_never_assigned`) instead of one flat "every in-scope texture becomes a
  Texture2D" total.
- `validate_scenes_bundle_contents()`: `Texture2D` bundle-object-count check now compares against
  the Tex2D-only in-scope total; added a parallel `Cubemap` count check against the Cube-only
  in-scope total.
- New `validate_texture_dimension_consistency()` (task item 3): for every `Material` in the built
  bundle, reads its own compiled `Shader`'s per-property declared dimension
  (`m_ParsedForm.m_PropInfo.m_Props[].m_DefTexture.m_TexDim`) and asserts every assigned texture's
  actual UnityPy class matches it — an independent, bundle-only re-proof that no material
  references a `Texture2D` on a property its (stand-in) shader declares Cube/3D/2DArray, i.e. a
  direct re-check of the reported failure mode. Wired into `main()`.

## 5) Perf follow-up — `tools/bundle/patch_monoscript_identity.py` (system-prompt-flagged)

docs/progress/s2-build.md SS4 flagged the mandatory post-build MonoScript-identity patch's own
exhaustive byte-identity self-verification as the single longest step in the whole S2 pipeline
(35+ minutes at ~120k objects) — this task's brief explicitly said "fine to add a faster mode but
keep correctness checks." Added a default **fast-verify** mode: `patch_env()` now records which
nested SerializedFile ("block") owns each patched MonoScript; the expensive
`snapshot_non_monoscript_objects()` byte-identity scan (task item 4's own requirement) is
restricted to just those blocks (plus, defensively, any block whose total serialized byte size
changed unexpectedly) instead of every object in the whole bundle. This is a full guarantee, not a
sample: `patch_env()` never calls `save_typetree()` on anything but a matching `MonoScript`, so a
block outside that set was never written to — its byte size and object count are still
cross-checked (cheap, no per-object read) as a second net. `--full-verify` opts back into the old
exhaustive whole-bundle scan for extra paranoia.

---

## 6) Full pipeline re-run — bundle build, validators, client rebuild

Ran the real, mandatory pipeline end to end: `pwsh tools/build_all.ps1 -PackageDir
D:/ArenaMapPort/work/extract/equator -OutputDir D:/ArenaMapPort/work/build/full
-SkipGameRefsSync` (GameRefs sync skipped — untouched by this task, already correctly synced from
a prior session). **Result: `[build_all] OK -- build + mandatory identity patch + all validators
passed.`**

### Unity import → build (`BuildBundles.Run`)
```
[TextureImportDriver] imported 708 textures (36 flagged alpha-cutout uncompressed,
  18 imported as real Cubemap assets, 1 Tex3D/Tex2DArray source(s) never assignable
  to any material property)
[StandinShaderGenerator] generated 24 stand-in shaders (1 pre-existing, unrelated
  _BumpTiling Float/Color conflict warning, same as prior sessions)
[MaterialImporter] material 85b63b99b60cf3f1 prop _ParticleTex: texture b882ed2e01e28b73
  has dimension 'Tex3D' ... leaving the property unassigned rather than binding a 2D
  texture to it.
[MaterialImporter] imported 332 materials (1 Tex3D/Tex2DArray texture slot(s) left
  unassigned, impl-texture-dimensions §2)
[SceneAssembler] assembled all 5 content scenes; GameObject/renderer counts match the
  package exactly (22034/15311/1259/735/59)
[AuthorScriptsScene] Scripts scene authored (LevelSettings=1, LocationScene=1,
  BotZone=1, LocationExportInfo=1, SpawnPointMarker=3)
[BakeNavMesh] agentRadius=0.3 agentHeight=1.7 agentSlope=48 agentClimb=0.38 tileSize=256
  (SPT's real agent-0 values, unchanged from prior sessions — NavMesh bake is
  collider-geometry-driven, untouched by this task)
[BuildBundles] built 2 bundle(s): arena_equator_navmesh.bundle, arena_equator_scenes.bundle
```
Zero `error CS`/`Exception`/`Received signal` anywhere in the Unity log. Real wall time for the
Unity step (process start to `EditorApplication.Exit(0)`): **~5.5 minutes**.

### MonoScript identity patch (`patch_monoscript_identity.py`) — fast-verify mode exercised for real
```
arena_equator_navmesh.bundle: 0 MonoScript(s) needed patching -- left untouched.
arena_equator_scenes.bundle:
  fast verify: restricting the byte-identity scan to 1 nested file(s) that own a
    patched MonoScript (['BuildPlayer-Arena_Equator_Scripts.sharedAssets'])
  patched 5 MonoScript(s): LevelSettings/LocationScene/BotZone/LocationExportInfo/
    SpawnPointMarker, all SptGameRefs-AssemblyCSharp[.dll] -> Assembly-CSharp
  ok : every MonoScript now carries a real assembly name
  ok : all 1 non-MonoScript objects in 1 affected nested file(s) are byte-for-byte
    identical before vs after the patch
  ok : AssetBundle object (1 found) lives in a nested file the fast-verify scan didn't
    need to cover (covered by the coarse count cross-check instead)
  ok : nested-file block sizes unchanged except BuildPlayer-Arena_Equator_Scripts.sharedAssets
    (3096 -> 3016 bytes, the -75-byte aggregate string-length delta from the 5 patches)
  OK -- patch applied and verified (5 MonoScript(s) patched, 1 other object(s)
    confirmed byte-identical)
```
**The fast-verify optimization worked exactly as designed**: only 1 object (down from the
~120k+ objects the old exhaustive scan touched at S2 scale, `docs/progress/s2-build.md` SS4's
35+-minute cost) needed the byte-identity re-check, because only 1 of the bundle's ~12 nested
SerializedFiles actually owns a patched MonoScript. The whole patch step (UnityPy load of the
~300MB bundle + patch + save + reload + verify) completed in a few minutes, dominated entirely by
the unavoidable `UnityPy.load()` cost of a bundle this size — not by the verification step this
task's perf fix targeted.

### Validators (strict/default flags)

**`validate_extract.py`** — 0 failures, 2 non-blocking warnings (the pre-existing 200 "no mesh
assigned" + 21 "orphaned mesh" notes, plus this task's own 18 Cubemap-strip / 1 Texture3D-flatten
informational lines folded into the warnings list). Reproducibility check (re-running the
extractor twice from scratch, strict/default — not skipped): **byte-identical across both fresh
runs, and byte-identical to the checked-in package** — 0 diffs both ways.

**`validate_bundle_roundtrip.py`** — 0 failures, 1 non-blocking warning (pre-existing: +1 built-in
Skybox Material, documented in earlier sessions, unrelated to this task). The load-bearing results:
```
package totals vs in-scope totals: ... in-scope split by dimension: 629 Tex2D, 18 Cube,
  1 Tex3D/Tex2DArray (the last group is MaterialImporter's explicit never-assign fallback,
  correctly absent from the bundle)
ok : Texture2D count in bundle (629) == package (629)
ok : Cubemap count in bundle (18) == package (18)
ok : 931 assigned Material texture slot(s) cross-checked against their shader's own
  compiled property dimension -- no material references a Texture2D on a property
  whose shader declares Cube/3D/2DArray (or any other dimension mismatch)
```
This is the task's own item 3 requirement, independently re-verified: **every one of the 18
Cubemap textures is a real `Cubemap` object in the built bundle (never a `Texture2D`), and across
all 931 non-null texture assignments on all 312 in-scope materials, none mismatches its shader's
own compiled property dimension.** All other pre-existing checks (Mesh/GameObject/MeshRenderer/
collider counts, MonoScript identity/namespace/assembly for all 5 SS D components, LODGroup
null-renderer, `editor_verify.json` cross-check for all 6 scenes) also pass cleanly.

**`validate_preset.py`** — 0 failures, all 6/6 scene rcids present, `EFT.ScenesPreset` MonoScript
identity and `m_Container` key correct.

### Client rebuild (task item 4)

`dotnet build ArenaMapPort.Client/ArenaMapPort.Client.csproj -c Release` — 0 warnings, 0 errors.
The `ArenaMapPortStageWpOutputs` MSBuild target re-staged `dist/BepInEx/plugins/ArenaMapPort/`
straight from `work/build/full`/`work/build/preset` (never touched `D:/SPT41`, per the workspace
rule — `tools/deploy-to-spt.ps1` was not run):
```
staged arena_equator_scenes.bundle, arena_equator_navmesh.bundle, arena_equator_preset.bundle
  -> dist/BepInEx/plugins/ArenaMapPort/bundles/
staged material-shaders.json -> dist/BepInEx/plugins/ArenaMapPort/
```

### Final artifact hashes (SHA-256) and sizes

| File | SHA-256 | Size |
|---|---|---|
| `work/build/full/arena_equator_scenes.bundle` | `25b7fea7a7db5e5fa55ae366274620fa6b497262ab63b86be36e1a1200db1f9b` | 308,887,703 bytes |
| `work/build/full/arena_equator_navmesh.bundle` | `fc83c8dd46b7613931bba603ed60f6da508a2b3624b5aabad95f72989bfc0455` | 81,562 bytes (byte-identical to the prior session's bake — NavMesh depends only on collider geometry, untouched by this task) |
| `work/build/full/material-shaders.json` | `0724b11c2e97b4b1710384d1498af2a2de061e21d1ab98bc1da711c142b1ad17` | 43,237 bytes |
| `work/extract/equator/manifest.json` | `9d1663014b1dcedea18c5cf041ba0710f66acc11ad0b31139f5e558a9e3bec9f` | 804,883 bytes |

(fix-raidstart: re-copied from a fresh `sha256sum` run — the previous table truncated every hash by
its final hex digit, e.g. `...1200db1f9` instead of `...1200db1f9b`; re-verified byte sizes and the
first 63 hex chars matched the real files exactly, so this was a transcription defect only, not a
stale/wrong artifact.)

(The scenes bundle shrank from the pre-fix session's 306,614,329 bytes to 308,887,703 bytes — the
`+2.27MB` net delta is the real, correctly-shaped 18 Cubemap assets replacing 18 wrong single-face
Texture2D assets, mip chains included, roughly netting out against the removed 200 KB flattened-
Texture3D asset's now-unused import; not independently decomposed further, not required by the
task.)

### Timings summary

| Step | Wall time |
|---|---|
| Extraction (`extract_equator.py`, full 5-scene run) | ~2m17s |
| Unity import + scene assembly + NavMesh bake + bundle build | ~5.5min |
| MonoScript identity patch (incl. UnityPy load of the 300MB bundle) | ~5min (fast-verify itself: seconds, not the 35+min prior exhaustive cost) |
| `validate_extract.py` (incl. 2x full re-extraction for reproducibility) | ~5min |
| `validate_bundle_roundtrip.py` + `validate_preset.py` | ~2min |
| Client rebuild (`dotnet build -c Release`) | ~1s |
| **Total (build_all.ps1 pipeline only)** | **~17m20s** |

## Summary

Root cause confirmed and fixed at both ends of the pipeline: the extractor was silently decoding
only face 0 of every Cubemap (and never handling Texture2DArray) as a plain 2D PNG, and the
importer/stand-in-shader-generator always declared every texture property `2D` regardless of the
source's real shape — together producing a `Texture2D` object bound to a shader property the real
game declares `Cube`, the exact "Error assigning 2D texture to CUBE texture property: Dimensions
must match" failure. Fixed by (1) extending the WP1 package format with a `dimension` field and
real all-6-face Cubemap decoding, (2) making WP2's `TextureImportDriver`/`MaterialImporter`/
`StandinShaderGenerator` dimension-aware (Cube → real `Cubemap` asset; Tex3D/Tex2DArray → never
assigned, matching the task's explicit fallback), and (3) adding an independent bundle-side
validator cross-check. Full pipeline re-run from scratch (extraction → Unity build → mandatory
identity patch → all 3 validators → client rebuild) is **green end to end**, with the specific
new checks (`Cubemap count in bundle (18) == package (18)`, the 931-slot dimension-consistency
scan) both passing. `docs/SPEC.md` §C.3 updated to document the new manifest fields.
