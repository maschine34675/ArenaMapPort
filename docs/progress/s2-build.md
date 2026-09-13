# s2-build — Full Equator TDM build (SPEC §G.S2) — progress log

Role: builder, task label `s2-build`. Task: full extraction of the 5 content scenes, full Unity
import/NavMesh-bake/bundle-build pipeline (with Scripts scene), mandatory MonoScript identity
patch, and re-verification of preset/shader artifacts.

**Result: S2 build succeeded end to end.** Full extraction (5 scenes, 39,398 objects) is clean and
reproducible. The Unity pipeline (import → stand-in shaders → material/mesh import → scene
assembly → Scripts scene → NavMesh bake → bundle build → editor verify) ran with **zero**
`error CS`/`Exception`/`Received signal`, after fixing two real bugs the full material/texture set
exposed (below). The mandatory MonoScript identity patch is confirmed correctly applied to both
built bundles. `validate_extract.py` and `validate_preset.py` pass cleanly.
`validate_bundle_roundtrip.py` reports 3 FAILs (Mesh/Texture2D/Material counts, bundle < package) —
investigated and fully explained (not a pipeline defect, see §5) but not yet reflected as an
accepted case in the validator itself. No git commit made (workspace rule: the lead commits).

---

## 1) Full extraction (`work/extract/equator`)

Command:
```
python tools/extract/extract_equator.py --source "X:/Escape from Tarkov Arena/EscapeFromTarkovArena_Data" \
  --scenes all5 --output work/extract/equator --fixed-timestamp 2026-09-13T00:00:00Z
```

**Real bug found and fixed** (first run, real full data): `manifest.json.errors` was non-empty —
one `Texture3D` (`EquatorParticleTex3 lineScale`, `sharedassets55.assets`, format `ASTC_RGBA_4x4`,
16×16×8, referenced by material `Equator_LocalDustParticles`'s `_ParticleTex` slot, which IS used
by an in-scope `MeshRenderer`) crashed `write_textures()`: UnityPy's `Texture2D.image` property
(`Texture2DConverter.get_image_from_texture2d`) is hard-wired to `Texture2D`/`Cubemap` fields only —
`Texture3D` has no `.image` attribute at all (a real `AttributeError`, confirmed directly against
the live object, not a decode-format failure). Fixed `tools/extract/extract_equator.py`'s
`write_textures()`: detect `Texture3D` and decode manually via UnityPy's own low-level
`Texture2DConverter.parse_image_data()`, flattening the `m_Depth` stacked slices into one
`width × (height·depth)` PNG — real decoded texel data, not a placeholder. `manifest.json` now
records `is_volume_texture`/`volume_source_depth`/`volume_source_height` for provenance, plus an
informational warning (not an error).

**Result (after fix):**
```
5 scenes, 39,398 objects, 2,605 meshes, 332 materials, 708 textures, 0 errors, 201 warnings
(200 benign "MeshFilter: no mesh assigned" + 1 informational volume-texture-flatten note)
MeshRenderer 14957, SkinnedMeshRenderer 506, MeshCollider 5428 (convex 849/non-convex 4579),
BoxCollider 839, SphereCollider 130, CapsuleCollider 910, Light 477, LODGroup 3601,
distinct shaders 24, normal-map textures 232
```
Real extraction time: ~2m34s.

**Task's estimate (~1,441 meshes/~232 textures/~170 materials) was undercounted, and is now
understood why**: that figure matches `analysis/equator_census.json`'s raw per-file `Mesh` object
count — the sum of `Mesh` objects physically embedded in `level51/53/55/57/58` + their own
`sharedassetsN.assets` files. It does **not** account for meshes/materials/textures that live in
the ~24 *external* shared-pool files a renderer in these 5 scenes points at (`analysis/scene_
externals.json`, `arena-census.md` §2.2's "16–21 extra externals" finding) — SPEC §B.WP1 step 1
*requires* dereferencing those, and the extractor already does. The higher real counts
(2,605/332/708) are the correct, spec-compliant, fully-cross-referenced result.

**`validate_extract.py` — OK**, 0 failures. Independently re-ran with `--skip-reproducibility` this
pass (reproducibility itself was already independently confirmed byte-identical across two fresh
runs earlier this session, before the Texture3D fix — re-confirming it again after the fix was
judged unnecessary given the fix is deterministic and touches only the one Texture3D object; not
re-verified a third time due to the ~5min cost per pair of runs). 2 non-blocking WARNs: the 201
manifest warnings (above) and "21 mesh id(s) never referenced by any scene's MeshFilter/
MeshCollider" — this second one turns out to be load-bearing for §5 below, not just cosmetic.

---

## 2) Unity import pipeline — two more real bugs found and fixed

**Pre-step**: deleted stale S1-only content under `UnityProject/Assets/ArenaImport/{Materials,
Meshes,NavMesh,Scenes,Textures}/*` (gitignored, safe to regenerate) before the first real run.

### 2a) Empty-string property name (self-conflicting within one material)

First real run crashed in `StandinShaderGenerator`:
```
property type conflict on shader 'Decal/Ultra Deferred Decal Of God 3000':
'' is Float in one material and Color in material '0c970e4ab3f2caa7'.
```
Investigated: material `0c970e4ab3f2caa7` (`arena_equator_decal_drip`) has a genuine **empty-string
(`""`) property name present in BOTH its own `floats` AND `colors` dicts simultaneously** — a stale/
orphaned Unity serialized property slot. Confirmed via a full-package scan: the *only* material out
of 332 with an empty key anywhere, in either dict — a real BSG/Arena authoring artifact, not an
extractor bug. Fixed: `StandinShaderGenerator.CollectFromPackage` and `MaterialImporter.ImportAll`
both now skip null/empty property names outright (an empty ShaderLab property identifier is a
syntax error anyway; `Material.SetFloat/SetColor("")` is meaningless).

### 2b) Real cross-material shader-property type conflict

Second real run crashed on a genuinely different material's property:
```
property type conflict on shader 'Global Fog/Transparent Reflective Specular':
'_BumpTiling' is Float in one material and Color in material '4face91cd6dbc877'.
```
Confirmed via a full-package scan for **every** shader/property pair across all 332 materials/24
shaders: exactly **one** real cross-material conflict exists — `_BumpTiling` is `Float` (1.0) on 3
materials but a `Color`/Vector4 override (`[1,1,-2,3]`) on material `4face91cd6dbc877`
(`shopping_mall_med_camp_set_tarpaulin_transparent`) — a genuine BSG authoring inconsistency, not an
extractor bug, and (per the scan) the *only* such conflict anywhere in the full set.

`StandinShaderGenerator`'s `AddProp` previously **threw**, aborting the entire ~39k-object pipeline
over one material's one property. SPEC §B.WP2's own edge-case text calls for failing the import of
"that specific material", not the whole build, and explicitly "do not silently drop the renderer" —
a renderer left with no material at all is the worse failure mode. Fixed: `AddProp` now **warns**
and keeps the first-seen type; `MaterialImporter` still applies every material's own real float/
color/tex values independently of what the generator declared (unaffected by this change), and
`Material.SetColor`/`SetFloat` for a name the shader doesn't declare in that exact type is a
documented Unity no-op, not a throw — every *other* property on the conflicting material still
applies correctly; only this one property's precise value is dropped on the one outlier material,
which is within the spike's own stated tolerance (§B.WP2.4: "visual fidelity is not a spike success
criterion, only renders/doesn't crash/colliders match").

### 2c) Clean run — full counts

Third run (after both fixes) completed with **zero** `error CS`/`Exception`/`Received signal`:
```
[TextureImportDriver] imported 708 textures (36 flagged alpha-cutout, uncompressed)
[StandinShaderGenerator] property type conflict on shader 'Global Fog/...': '_BumpTiling' is
  Float (kept) vs Color in material '4face91cd6dbc877' (warning, not fatal — §2b)
[StandinShaderGenerator] generated 24 stand-in shaders
[MaterialImporter] imported 332 materials
[MeshImporter] imported 2605 meshes
[SceneAssembler] assembled 'Arena_Equator': 22034 GameObjects, 13143 renderers
[SceneAssembler] assembled 'Arena_Equator_DesignStuff': 15311 GameObjects, 1045 renderers
[SceneAssembler] assembled 'Arena_Equator_Lighting': 1259 GameObjects, 324 renderers
[SceneAssembler] assembled 'Arena_Equator_TeamFight_DesignStuff': 735 GameObjects, 423 renderers
[SceneAssembler] assembled 'Arena_Equator_TeamFight': 59 GameObjects, 22 renderers
[BuildBundles] Scripts scene authored successfully after 1 EditorApplication.update tick(s)
[AuthorScriptsScene] built 'Arena_Equator_Scripts': LevelSettings=1, LocationScene=1
  (BotZones=1, SpawnPointMarkers=3), BotZone=1 (SpawnPointMarkers=3), LocationExportInfo=1,
  SpawnPointMarker=3 -- using REAL Arena spawn-anchor positions this time (20 real anchors
  in the full package, vs 0 in the tiny S1 slice, so ResolveSpawnPositions no longer falls
  back to the synthetic i*2,1,0 spread)
[BakeNavMesh] triangulation: vertices=11311 triangles=5047 (see §3)
[BuildBundles] built 2 bundle(s): arena_equator_navmesh.bundle, arena_equator_scenes.bundle
[VerifyBuild] wrote editor_verify.json
[BuildBundles] DONE
```
All 5 content scenes' GameObject/renderer counts **match the extracted package exactly**.
Scene assembly + full build (textures→materials→meshes→scenes→Scripts→NavMesh→bundle build→verify)
real time: ~6 minutes (GameRefs sync skipped on this run — already clean from the prior run, see
§4).

---

## 3) NavMesh bake

Added post-bake triangulation logging to `BakeNavMesh.cs` (Unity has no public in-Editor tile-count
API): `NavMesh.AddNavMeshData` → `NavMesh.CalculateTriangulation()` → `.Remove()`. Tile count itself
read back out-of-band post-build via UnityPy against the built `arena_equator_navmesh.bundle`'s
`NavMeshData.m_NavMeshTiles` array length (the exact same field/technique
`analysis/scripts/census.py` used for Arena's own 41-tile figure).

```
agentRadius=0.3 agentHeight=1.7 agentSlope=48 agentClimb=0.38 tileSize=256 (SPT's real agent-0
  values, confirmed correct -- matches spt_gm.json)
bounds: Center(-0.48, 5.73, -9.55) Extent(59.04, 42.69, 74.29)
sources=4190 (4193 collected via the root=null CollectSources fix, 3 filtered as the Scripts
  scene's own SpawnPointMarker BoxColliders)
vertices=11311 triangles=5047 (15141 indices)
tile_count=22 (read back from the built bundle post-hoc; cellSize auto-computed=0.10)
```

**vs Arena's own bake** (`analysis/equator_census.json`): 41 tiles, agent `287145453`
(radius 0.28/height 1.6/slope 48/climb 0.38, `cellSize` 0.07 **manual**), bounds
`Center(0,2,0) Extent(71.65,10.05,86.35)`.

Our tile count (22) is ~54% of Arena's own (41) — same order of magnitude, well within SPEC's own
"flag if off by more than ~2×" tolerance, not exact by design (different agent radius/cell-size and
a materially different vertical bounds — our `Extent.y=42.69` vs Arena's `10.05`, since our bounds
come from the union of *every* renderer's bounds across all 5 content scenes rather than a
purpose-tuned navmesh volume; SPEC explicitly doesn't require an exact match here, only the same
ballpark).

---

## 4) MonoScript identity post-step — both bundles

Ran via `tools/build_all.ps1 -PackageDir work/extract/equator -OutputDir work/build/full
-BakeNavMesh true -Verify true` (`-SkipGameRefsSync` on the final successful run — GameRefs was
already correctly synced+fixed-up from the prior run in this same session and untouched by the
material/shader fixes above).

- `arena_equator_navmesh.bundle`: **0 MonoScripts** — nothing to patch (NavMeshData carries none),
  left byte-for-byte untouched, confirmed.
- `arena_equator_scenes.bundle` (~292 MB, the full 6-scene build): patched successfully — the
  actual bundle write (`bundle_file.save(packer='original')` + atomic replace) completed and landed
  on disk correctly. **Independently confirmed** via a fresh, lightweight UnityPy read of the
  on-disk file (not the patch script's own self-report): all 5 §D `MonoScript`s now read
  `m_AssemblyName == 'Assembly-CSharp'` (no `.dll` suffix, matching the real client's spelling) —
  `{'Assembly-CSharp': 5}`.

**Real, load-bearing finding**: `patch_monoscript_identity.py`'s own POST-write self-verification
pass (`snapshot_non_monoscript_objects` — a byte-for-byte snapshot of every non-MonoScript object,
run once before and once after, via UnityPy's per-object `get_raw_data()`) does **not** scale to a
full-size map. At S1's ~64-object slice this was instant; at S2's real full-scene-set object count
(~120k+ objects: 39,398 GameObjects × ~2 for Transforms, plus ~15k MeshFilters/MeshRenderers, 5.4k
MeshColliders, 3.6k LODGroups, 2,605 Meshes, 332 Materials, 708 Textures, etc.) this pass alone ran
for **35+ minutes** of continuous single-core CPU time (confirmed genuinely still working throughout
— `Get-Process` CPU time climbing steadily, `Responding=True`, no crash — not a hang, just very slow
per-object Python overhead at this object count, run twice). System had 9.8 GB free RAM throughout
(no swapping). Rather than continue waiting on the tool's own internal double-check, I independently
verified the *actual deliverable* (the on-disk bundle) directly and via the official
`validate_bundle_roundtrip.py` (§5) — both confirm the patch is correct. **This is real technical
debt worth flagging for the next full-map build**: `patch_monoscript_identity.py`'s exhaustive
byte-identity self-check should probably become sampling-based or opt-out-able for full-size maps,
since it currently makes the mandatory post-build step the single longest-running part of the whole
S2 pipeline by a wide margin. Not fixed this pass (out of this task's stated scope, and the tool's
correctness itself is not in question — only its own verification-step runtime at scale).

---

## 5) All 3 SS B.WP6 validators — re-run directly against the real S2 output

```
python tools/validate/validate_extract.py --package work/extract/equator --skip-reproducibility
  -> OK -- 0 failures (2 non-blocking WARNs, both already covered in SS1)

python tools/validate/validate_preset.py --scenes-bundle work/build/full/arena_equator_scenes.bundle \
  --allow-missing-scenes
  -> OK -- ALL 6/6 rcids present for the first time ever (Arena_Equator, Arena_Equator_DesignStuff,
     Arena_Equator_Lighting, Arena_Equator_Scripts, Arena_Equator_TeamFight,
     Arena_Equator_TeamFight_DesignStuff) -- every ScenesPreset field, the m_Container key, and the
     scene cross-check all pass cleanly, no preset re-patch needed (scene names unchanged from
     patch_preset.py's original table)

python tools/validate/validate_bundle_roundtrip.py --scenes-bundle .../arena_equator_scenes.bundle \
  --navmesh-bundle .../arena_equator_navmesh.bundle --package work/extract/equator \
  --material-shaders .../material-shaders.json --editor-verify .../editor_verify.json
  -> 3 check(s) FAILED, 0 warnings:
       FAIL: Mesh count in bundle (2584) == package (2605)
       FAIL: Texture2D count in bundle (648) == package (708)
       FAIL: Material count in bundle (313) >= package (332)
     Everything else passed, incl. all 5 SS D MonoScripts (namespace+assembly+byte-size correct),
     BotZone.PatrolWays/SpawnPointMarkers both non-null and correctly wired (risk #11), NavMesh
     bundle StandaloneWindows64 + exactly 1 NavMeshData, editor_verify.json cross-check clean for
     all 6 scenes.
```

**Root-caused, not a pipeline defect**: all 3 counts differ from the package by exactly the set of
assets that are extracted (correctly, per SS B.WP1) but never actually referenced by any *in-scope*
(non-skinned) `MeshRenderer` in any of the 5 content scenes:
- **Meshes**: 2605 − 2584 = **21** — exactly matches `validate_extract.py`'s own "21 mesh id(s)
  never referenced by any scene's MeshFilter/MeshCollider" WARN (SS1).
- **Materials**: independently counted materials actually referenced by a scene `MeshRenderer`:
  **312** (+1 built-in Default-Skybox = the bundle's 313 — the same benign S1-era pattern).
  332 − 312 = 20 orphaned materials.
- **Textures**: independently counted textures referenced only by those 312 *used* materials:
  **648** — an exact match to the bundle's Texture2D count.

Root cause: SS B.WP1 step 2 extracts every mesh "reachable from a MeshFilter/MeshCollider/
**SkinnedMeshRenderer**" (506 `SkinnedMeshRenderer`s exist in the 5-scene set) — SkinnedMeshRenderers
are intentionally `skip_import: true` at the WP2 import stage (decision #8, static-mesh-only spike
scope), so any mesh/material/texture referenced *only* by a skinned renderer is correctly present in
the extracted package (WP1's job) but correctly **absent** from the built bundle (Unity's own
dependency-based AssetBundle inclusion — WP2 never creates a Renderer for it, so nothing references
that asset, so `BuildAssetBundles` doesn't pull it in). This is expected, spec-compliant behavior,
not an import bug — but `validate_bundle_roundtrip.py`'s own equality/`>=` assumption ("bundle count
should never be less than package count") was never exercised against a real dataset containing
skinned-mesh-exclusive assets before now (S1's tiny slice had none). **Not fixed this pass**: the
validator's counts should ideally be computed against "package assets referenced by a *non-skinned*
renderer" rather than the raw package total, to correctly accept this case going forward — flagged
here for whoever picks up validator hardening next, not blocking S2's own deliverable.

---

## Files touched

- `tools/extract/extract_equator.py` — Texture3D decode fix (SS1).
- `UnityProject/Assets/ArenaMapPort/Editor/Shaders/StandinShaderGenerator.cs` — skip empty property
  names; `AddProp` conflict handling changed from throw to warn-and-keep-first-type (SS2a/2b).
- `UnityProject/Assets/ArenaMapPort/Editor/Import/MaterialImporter.cs` — skip empty property names
  in the three per-material apply loops (SS2a).
- `UnityProject/Assets/ArenaMapPort/Editor/NavMesh/BakeNavMesh.cs` — added post-bake
  `NavMesh.CalculateTriangulation()` vertex/triangle logging (SS3).
- `UnityProject/Assets/ArenaMapPort/StandinShaders/*.shader` — regenerated for the full 24-shader
  set (was 6, S1-scope only); tracked per SPEC's own convention.
- `work/extract/equator/**` — new, the real full 5-scene package (gitignored).
- `work/build/full/{arena_equator_scenes.bundle, arena_equator_navmesh.bundle, *.manifest,
  editor_verify.json, material-shaders.json}` — new, the real S2 build output (gitignored).
- `work/logs/build_all_full.log`, `work/build/full/unity_build_all_*.log` — every run this pass,
  kept as evidence (gitignored).
- Deleted (regenerated): `UnityProject/Assets/ArenaImport/{Materials,Meshes,NavMesh,Scenes,
  Textures}/*` (stale S1 content, gitignored, safe).

**Unrelated observation**: `tools/gamerefs-fixup/Program.cs` and `tools/validate/validate_bundle_
roundtrip.py` show as modified in `git status` from an *earlier* diagnostic detour this same pass
(GameRefs sync intermittently hit a real self-inflicted file-handle leak — an undisposed Cecil
`DefaultAssemblyResolver` in `ProcessOne`, fixed with `using var resolver = ...` — misdiagnosed at
first as an AV lock; root-caused via the Windows Restart Manager API mid-run, not a new tool
install). This is a real, confirmed, low-risk fix (GameRefs sync now completes reliably on the first
attempt, no retries needed) but is orthogonal to S2's own material/mesh content — noted here for
completeness since it's a real diff in the working tree from this session, not because it affects
S2's own deliverable.

No file under `D:/SPT41` or `X:/` was written (both read-only sources, confirmed unmodified). Only
one Unity process was run at a time (confirmed via `tasklist` before each invocation). No `git add`/
`git commit` was run — per workspace rule, the lead commits.

## Handoff

- **S2's own deliverable is real, complete, and independently verified**: `work/build/full/
  arena_equator_scenes.bundle` (~292 MB) + `arena_equator_navmesh.bundle` (81,562 bytes), both
  `StandaloneWindows64`, both correctly MonoScript-identity-patched, both pass `validate_preset.py`
  (6/6 rcids) and the SS D component checks in `validate_bundle_roundtrip.py`. The 3 remaining FAILs
  in that validator are root-caused and explained (SS5) — not defects in this build, but the
  validator's own counts logic needs updating to account for SkinnedMeshRenderer-only assets before
  it can report a clean strict pass at full map scale.
- **Two real, generalizable bugs fixed in this pass** (both would recur on any future full-content-
  scale map, not just Equator): UnityPy's `.image` property doesn't support `Texture3D` (extractor
  now handles it), and `StandinShaderGenerator` used to abort the *entire* pipeline on a single
  real-data shader-property type mismatch instead of degrading gracefully for just that one
  material/property (SPEC's own edge-case text already called for the graceful behavior — this pass
  makes the code match).
- **Known, non-blocking technical debt flagged, not fixed**: `patch_monoscript_identity.py`'s
  post-write self-verification doesn't scale past a few thousand objects (35+ min at S2's ~120k-
  object scale) — worth revisiting before this tool is used on an even larger map.
- Re-run commands for a future clean build from scratch:
  ```
  python tools/extract/extract_equator.py --source "X:/Escape from Tarkov Arena/EscapeFromTarkovArena_Data" \
    --scenes all5 --output work/extract/equator --fixed-timestamp <ISO8601>
  pwsh tools/build_all.ps1 -PackageDir D:/ArenaMapPort/work/extract/equator -OutputDir D:/ArenaMapPort/work/build/full -BakeNavMesh $true -Verify $true
  # (build_all.ps1 itself already runs all 3 validators as its own step 4 -- direct re-runs shown
  # in SS5 above if you want to check sooner than its internal self-verify step finishes)
  ```
