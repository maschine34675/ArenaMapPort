# fix-enabled-state — disabled-component import fix (progress log)

Role: builder, task label `fix-enabled-state`. Task: fix the 2026-09-14 20:09 playtest finding
("many large flat WHITE untextured blocks stand in walkways and block movement") at its root —
the extractor/importer never honored Unity's per-component `m_Enabled`/`.enabled` flag, so
anything Arena itself ships *disabled* came back *enabled* in our bundle.

**Result: done.** Extractor and importer now round-trip `enabled` for every component type that
has one. Two new validator checks (extraction-vs-Arena, bundle-vs-package) both pass with an exact
per-scene, per-type match. Full pipeline re-run from scratch (extraction → Unity build → mandatory
MonoScript-identity patch → all 3 validators → client Release rebuild) is green end to end, and
`dist/` now carries the rebuilt bundles. No git commit made (workspace rule: the lead commits).

---

## 1) Root cause

`tools/extract/extract_equator.py`'s `build_meshrenderer`/`build_meshcollider`/etc. never read a
component's own `m_Enabled` typetree field, and
`UnityProject/.../Editor/Import/SceneAssembler.cs`'s `AttachMeshRenderer`/`AttachMeshCollider`/etc.
never set `component.enabled` — every component came in at Unity's `AddComponent` default
(`enabled = true`), regardless of what Arena itself authored. Confirmed directly against Arena's
own `level51/53/55/57/58` via UnityPy (the playtest finding's own root-cause dump,
`disabled_components.json`/`count_disabled.py`):

| Scene | Disabled components |
|---|---|
| `Arena_Equator` (level51) | `MeshRenderer` 4 (`colider 46`/`45`/`2`, `PKTS-6281_Admiral_opened_STENCIL`), `MeshCollider` 25 (`colider 46`/`45`, many `ballistic`) |
| `Arena_Equator_DesignStuff` (53) | `MeshRenderer` 2 (`Graffiti_Art_21` x2), `MeshCollider` 6 (`mustache_LOD1`), `BoxCollider` 380 (mannequin gear, e.g. `CR_ctacticall_body_lod0`) |
| `Arena_Equator_Lighting` (55) | `MeshRenderer` 25 (`*_stencil` ceilings/floors, `Cube` x19), `BoxCollider` 19 (`Cube`) |
| `Arena_Equator_TeamFight_DesignStuff` (57) | `MeshRenderer` 49 (`LevelBorder (N)` — the TeamFight play-area borders; their `BoxCollider`s stay enabled in Arena, i.e. intentional invisible walls) |
| `Arena_Equator_TeamFight` (58) | none |

`Light`s and `LODGroup`s: none disabled anywhere. `GameObject.active` flags were already extracted
and applied correctly (this bug is specifically about *component*-level `enabled`, distinct from
GameObject-level `active`).

## 2) Changes

1. **`tools/extract/extract_equator.py`** — `build_meshrenderer`, `build_skinnedmeshrenderer`,
   `build_meshcollider`, `build_boxcollider`, `build_spherecollider`, `build_capsulecollider`,
   `build_light`, `build_lodgroup` each now emit `"enabled": bool(tt.get("m_Enabled", 1))`. Default
   `true` when the field is absent (`tt.get(..., 1)`'s own default) — additive, backward-compatible
   schema change; `schema_version` left at `1` (no validator requires a bump for an additive,
   default-true field). `ReflectionProbe` unchanged (never imported, `skip_import: true`
   unconditionally); `MeshFilter` unchanged (not a `Behaviour`, has no `m_Enabled` of its own).

2. **`UnityProject/Assets/ArenaMapPort/Editor/Import/SceneAssembler.cs`** — new private
   `ReadEnabled(JObject comp)` helper (`comp["enabled"]` missing or `null` → `true`, matching the
   extractor's own default). `AttachMeshRenderer`/`AttachMeshCollider`/`AttachBoxCollider`/
   `AttachSphereCollider`/`AttachCapsuleCollider`/`AttachLight`/`AttachLodGroup` each now set
   `component.enabled = ReadEnabled(comp)` right after their existing field assignments.

3. **`UnityProject/Assets/ArenaMapPort/Editor/NavMesh/BakeNavMesh.cs`** — confirmed (comment added,
   no behavior change was needed): `NavMeshBuilder.CollectSources(null, ...)` reads *live* component
   state from every currently-open scene at the moment it is called, and
   `BuildBundles.FinishPipeline` only calls `BakeNavMesh.Bake()` after every content scene's
   `SceneAssembler.Assemble()` (which now sets `.enabled`) has already run, all within the same
   synchronous batchmode process — scenes are saved but never closed/reopened in between. So a
   disabled `MeshCollider`/`BoxCollider`/etc. is already excluded by the time the bake runs, for
   free, with no code change to the bake itself required. Confirmed by real numbers this pass (§5).

4. **`UnityProject/Assets/ArenaMapPort/Editor/Import/AuthorScriptsScene.cs`** — `LevelSettings` now
   also authors `fogMode = FogMode.ExponentialSquared`, `fogDensity = 0f`,
   `fogColor = (0.62, 0.66, 0.70, 1)`, matching the values the client's runtime
   `ArenaLightingRepairs.cs` Harmony postfix already forces at raid start (fix-lighting-movement).
   The authored default (`fogMode = default(FogMode) = 0`/Linear, `fogColor` black) made EFT's
   `GlobalFog` image effect — which only computes a fog factor for modes 1–3 — treat every non-sky
   pixel as fully-fogged black. Authoring the same values at source means a freshly rebuilt bundle
   is correct standalone, without depending on the client-side runtime patch ever running. The
   already-present spawn-marker `isTrigger = true` and `Infiltration` authoring were left untouched.

5. **`docs/SPEC.md` §C.6** — documented the new `"enabled": bool` field (which component types carry
   it, the `m_Enabled` source field, the default-true/back-compat contract, and which two types
   don't have one and why).

6. **`tools/validate/validate_extract.py`** — new `validate_disabled_component_counts()`: for every
   scene, counts components with an explicit `"enabled": false` in the extracted package, compares
   against Arena's own ground truth. Ground truth is **recomputed live** from Arena's own
   `level<N>` files via UnityPy (`count_disabled_in_arena()` — component records live entirely in
   `level<N>`, confirmed against `extract_equator.py`'s own `build_scene_graph()`/
   `build_components()`) whenever the source tree is reachable, falling back to a hardcoded
   reference table (`KNOWN_ARENA_DISABLED_COUNTS`, from this task's root-cause investigation) only
   when it isn't — same SKIP-not-FAIL pattern as the pre-existing reproducibility check. For a
   `--slice-roots` package a lower package count is a WARN (objects outside the slice), not a FAIL;
   a full (`--scenes all5`) package is expected to match exactly.

7. **`tools/validate/validate_bundle_roundtrip.py`** — new
   `validate_disabled_component_counts_bundle()`: per-scene disabled counts in the *built bundle*
   vs the *package*. Reads each scene's own nested `BuildPlayer-<SceneName>` `SerializedFile`
   directly (confirmed empirically this pass that Unity's multi-scene `AssetBundleBuild` gives each
   content scene its own nested file holding exactly that scene's `GameObject`/`Transform`/
   component data — e.g. `Arena_Equator`'s nested file's own object-type counts, 13143
   `MeshRenderer`/4730 `MeshCollider`/etc., match Arena's own per-scene totals exactly; the sibling
   `.sharedAssets` file holds only shared `Mesh`/`Material`/`Texture`/`Shader` dependencies, no
   component data) — so no ambiguity about which components belong to which scene.

## 3) Extraction re-run

```
python tools/extract/extract_equator.py --source "D:/Battlestate Games/Escape from Tarkov Arena/EscapeFromTarkovArena_Data" \
  --scenes all5 --output D:/ArenaMapPort/work/extract/equator
```
39,398 objects, 2,605 meshes, 332 materials, 708 textures, **0 errors**, 219 warnings (same shape as
every prior session: 200 benign "no mesh assigned" + 18 Cubemap-strip + 1 Texture3D-flatten
informational notes). Wall time **~2m16s** (20:32:14 → 20:34:30), matching the prior session's
~2m17s.

## 4) Anomaly: `build_all.ps1` reported FATAL despite Unity actually succeeding

`pwsh tools/build_all.ps1 -PackageDir D:/ArenaMapPort/work/extract/equator -OutputDir D:/ArenaMapPort/work/build/full -SkipGameRefsSync`
aborted at step 2 with:
```
[build_all] FATAL: Unity BuildBundles.Run failed (exit=0, 1 suspicious log line(s)) -- see ...
```
Investigated rather than forced through blindly: Unity's own process **exited 0**, and its log
shows the pipeline actually completed end to end —
`[ArenaMapPort][BuildBundles] built 2 bundle(s) -> .../full : arena_equator_navmesh.bundle,
arena_equator_scenes.bundle` and `[ArenaMapPort][BuildBundles] DONE packageDir=... outputDir=...`
(import → stand-in shaders → material/mesh import → all 5 scene assemblies → Scripts scene
authoring → NavMesh bake → bundle build → `VerifyBuild.Run` all ran and finished). The one
"suspicious log line" `build_all.ps1`'s `Select-String -Pattern "error CS|Exception|Aborting batchmode|Received signal"`
matched is a single benign Unity Editor **startup** warning, logged during Editor initialization
*before* any ArenaMapPort code runs at all (at `Application.AssetDatabase Initial Refresh Start`):
```
warn: Unity.AspNetCore.NamedPipeSupport.NamedPipeServer[0]
      Exception occured while accepting client connection: System.IO.IOException: Die Pipe wird gerade geschlossen.
         at System.IO.Pipes.NamedPipeServerStream.WaitForConnectionCoreAsync(...)
         ... (Unity's own internal named-pipe IPC infrastructure, e.g. Hub/Accelerator/IDE comms)
```
Confirmed: exactly 1 total match for `"Exception"` in the whole 20k+-line log, 0 matches for
`"error CS"`/`"Aborting batchmode"`/`"Received signal"`; the same grep against the prior session's
successful build log (`work/build/full/unity_build_all_20260914_090141.log`) finds **zero**
matches — this is a new, intermittent, environment-level Editor-startup artifact unrelated to this
task's code (it fires before script compilation even starts, so none of this task's C#/Python
changes can be its cause). Treated as a false positive in `build_all.ps1`'s own log-scanning
heuristic (worth tightening upstream — e.g. exclude `Unity.AspNetCore.NamedPipeSupport` noise, or
only match a real C# build/runtime exception from our own assemblies — flagged, not fixed here,
out of this task's scope) and proceeded manually with the mandatory steps 3+4 against the bundles
Unity did successfully build, running the exact same commands `build_all.ps1` itself would have.

## 5) Unity build results (manual step 2, since `build_all.ps1` stopped early)

Ran directly: `Unity.exe -batchmode -nographics -projectPath UnityProject -executeMethod
ArenaMapPort.Editor.BundleBuild.BuildBundles.Run -packageDir .../equator -outputDir .../full
-bakeNavMesh true -verify true` (identical arguments `build_all.ps1` itself uses). Wall time
**~5.5 min** (20:35:54 → ~20:41), matching the prior session. Zero `error CS`, zero
`Aborting batchmode`, zero `Received signal`.

Scene assembly renderer counts match Arena's own `MeshRenderer` totals exactly per scene (both
enabled *and* disabled renderers are still attached as components — only `.enabled` differs, so
these counts are unaffected by this fix): `Arena_Equator` 13143, `Arena_Equator_DesignStuff` 1045,
`Arena_Equator_Lighting` 324, `Arena_Equator_TeamFight_DesignStuff` 423,
`Arena_Equator_TeamFight` 22.

**Direct evidence the fix changed real behavior** — `BakeNavMesh` comparison against the prior
(pre-fix) full build's own log:

| | Pre-fix (`unity_build_all_20260914_090141.log`) | Post-fix (this pass) |
|---|---:|---:|
| `CollectSources(null,...)` total sources | 4193 | 4169 |
| Sources kept after content-root filter | 4190 | 4169 |
| Triangulation vertices | 11,311 | 10,227 |
| Triangulation triangles | 5,047 | 4,485 |

Fewer NavMesh sources and triangles post-fix — disabled colliders (the ~25 duplicate
`colider N`/`ballistic` `MeshCollider`s in `Arena_Equator`, the 380/19 disabled `BoxCollider`s in
`Arena_Equator_DesignStuff`/`Arena_Equator_Lighting`, etc.) no longer leak into the bake as bogus
blocking obstacles, exactly as `BakeNavMesh.cs`'s existing `enabled && !isTrigger && activeInHierarchy`
filter (§2 item 3) predicts once `.enabled` is finally set correctly upstream.

`AuthorScriptsScene` built cleanly (`LevelSettings=1, LocationScene=1 (BotZones=1,
SpawnPointMarkers=3), BotZone=1 (SpawnPointMarkers=3), LocationExportInfo=1, SpawnPointMarker=3`) —
the new fog-authoring lines did not disturb the existing `[ExecuteInEditMode]`-hazard-safe
build-inactive-then-activate sequence.

## 6) Mandatory MonoScript identity patch

```
python tools/bundle/patch_monoscript_identity.py --bundle .../full/arena_equator_navmesh.bundle   # 0 MonoScripts needed patching
python tools/bundle/patch_monoscript_identity.py --bundle .../full/arena_equator_scenes.bundle
```
Scenes bundle: patched all 5 SS D `MonoScript`s (`LocationScene`/`BotZone`/
`EFT.Game.Spawning.SpawnPointMarker`/`EFT.LocationExportInfo`/`LevelSettings`),
`SptGameRefs-AssemblyCSharp.dll` → `Assembly-CSharp`. Fast-verify restricted the byte-identity scan
to the 1 nested file that owns a patched `MonoScript`
(`BuildPlayer-Arena_Equator_Scripts.sharedAssets`, 3096 → 3016 bytes, the −75-byte aggregate
string-length delta from the 5 patches); every other nested file's object count and byte size
confirmed unchanged. Bundle: 312,325,841 → 308,912,270 bytes. Wall time **~5m40s**
(20:42:28 → ~20:48), dominated by `UnityPy.load()` of the ~312 MB bundle, matching the prior
session's "a few minutes" fast-verify timing.

## 7) Validators (strict/default flags, exactly as `build_all.ps1` step 4 runs them)

**`validate_extract.py`** (full run, reproducibility included) — **0 failures**, 2 non-blocking
warnings (the pre-existing 200 "no mesh assigned" + 21 "orphaned mesh" notes, folded together with
this session's 219-warning manifest). Reproducibility: two independent fresh extractor runs are
**byte-identical to each other and to the checked-in package** (0 diffs both ways). Wall time
**~5m26s** (20:48:11 → ~20:53).

New disabled-component check — **every scene matches Arena exactly**, ground truth recomputed live
from Arena's own `level<N>` files (source was reachable):
```
ok : scenes/Arena_Equator.json: disabled-component counts match Arena exactly ({'MeshCollider': 25, 'MeshRenderer': 4})
ok : scenes/Arena_Equator_DesignStuff.json: disabled-component counts match Arena exactly ({'BoxCollider': 380, 'MeshCollider': 6, 'MeshRenderer': 2})
ok : scenes/Arena_Equator_Lighting.json: disabled-component counts match Arena exactly ({'BoxCollider': 19, 'MeshRenderer': 25})
ok : scenes/Arena_Equator_TeamFight_DesignStuff.json: disabled-component counts match Arena exactly ({'MeshRenderer': 49})
ok : scenes/Arena_Equator_TeamFight.json: disabled-component counts match Arena exactly ({})
ok : every scene's disabled-component counts match Arena exactly (recomputed fresh from Arena's own level<N> files)
```

**`validate_bundle_roundtrip.py`** — **0 failures**, 1 non-blocking warning (pre-existing: +1
built-in Skybox Material, documented in earlier sessions, unrelated to this task). Every
pre-existing check passes (GameObject/Mesh/Texture2D/Cubemap/MeshFilter/MeshRenderer/
MeshCollider/BoxCollider/SphereCollider/CapsuleCollider/Light/LODGroup counts, convex/non-convex
split, Mesh vertex/submesh detail, 312 Material stand-in-shader names, 0 null LODGroup renderer
slots, 931-slot texture-dimension consistency, all 5 SS D `MonoScript` identities/namespaces, both
risk-#11 non-null checks, full `editor_verify.json` cross-check for all 6 scenes).

New bundle-side disabled-component check — **every scene's bundle count matches the package
exactly**, read independently from each scene's own nested `BuildPlayer-<SceneName>`
`SerializedFile`:
```
ok : Arena_Equator: disabled-component counts in bundle match package exactly ({'MeshCollider': 25, 'MeshRenderer': 4})
ok : Arena_Equator_DesignStuff: disabled-component counts in bundle match package exactly ({'BoxCollider': 380, 'MeshCollider': 6, 'MeshRenderer': 2})
ok : Arena_Equator_Lighting: disabled-component counts in bundle match package exactly ({'BoxCollider': 19, 'MeshRenderer': 25})
ok : Arena_Equator_TeamFight_DesignStuff: disabled-component counts in bundle match package exactly ({'MeshRenderer': 49})
ok : Arena_Equator_TeamFight: disabled-component counts in bundle match package exactly ({})
ok : every scene's disabled-component counts in the built bundle match the extracted package exactly
```
Wall time **< 2 min** combined with `validate_preset.py` below (20:53:37 → 20:54).

**`validate_preset.py`** — **0 failures**. `ScenesPreset` fields all correct
(`ServerName='arena_equator_tdm'`, `_activeSceneName='Arena_Equator_Scripts'`,
`ChildPresets=[]`), all 6/6 `_scenesResourceKeys` rcids present and matching real scene names
inside the freshly patched `arena_equator_scenes.bundle`, embedded `MonoScript` resolves to
`EFT.ScenesPreset`/`Assembly-CSharp`, `m_Container` key correct. (The preset bundle itself,
`work/build/preset/arena_equator_preset.bundle`, is untouched by this task — it encodes scene
*names*/rcids, not component enabled-state — so this check simply re-confirms it still lines up
with the newly rebuilt scenes bundle.)

## 8) Client Release rebuild (dist/ staging)

```
dotnet build ArenaMapPort.Client/ArenaMapPort.Client.csproj -c Release
```
**0 Fehler, 0 Warnung(en)**, elapsed 00:00:02.49. `ArenaMapPortStageWpOutputs` re-staged
`dist/BepInEx/plugins/ArenaMapPort/` from `work/build/full`/`work/build/preset`:
```
[ArenaMapPort] deployed maschine-ArenaMapPort.Client.dll -> dist/BepInEx/plugins/ArenaMapPort
[ArenaMapPort] staged arena_equator_scenes.bundle, arena_equator_navmesh.bundle, arena_equator_preset.bundle -> dist/.../bundles/
[ArenaMapPort] staged material-shaders.json -> dist/.../ArenaMapPort/
```

### `dist/` — final file listing (this pass)

| File | Size (bytes) | Timestamp | SHA-256 | Changed this pass? |
|---|---:|---|---|---|
| `dist/BepInEx/plugins/ArenaMapPort/maschine-ArenaMapPort.Client.dll` | 69,120 | 2026-09-14 20:54 | `34fb5a17ca4bd8b80345b2cf4f096a610630738e90ff26542329316a7b73d8c2` | rebuilt (this task did not edit `ArenaMapPort.Client` source — same 69,120-byte size as the other agent's 20:23 build, recompiled here only to re-run the dist-staging MSBuild targets) |
| `dist/BepInEx/plugins/ArenaMapPort/material-shaders.json` | 43,237 | 2026-09-14 20:37 | `0724b11c2e97b4b1710384d1498af2a2de061e21d1ab98bc1da711c142b1ad17` | no-op (byte-identical to the pre-fix session's hash — unaffected by component enabled-state) |
| `dist/BepInEx/plugins/ArenaMapPort/bundles/arena_equator_scenes.bundle` | 308,912,270 | 2026-09-14 20:47 | `adb8c5916da001f068fe345d3c18d167ec74357d003b9853ff3e16df64fb1035` | **yes** — rebuilt with the enabled-state + fog fix |
| `dist/BepInEx/plugins/ArenaMapPort/bundles/arena_equator_navmesh.bundle` | 72,842 | 2026-09-14 20:38 | `25d6165c3c2535e4ae3ae77f4d08b4a3963f9addcfb275b546543d51fea10f6b` | **yes** — re-baked with disabled colliders excluded (§5) |
| `dist/BepInEx/plugins/ArenaMapPort/bundles/arena_equator_preset.bundle` | 6,891 | 2026-09-13 17:29 | `f12cf0bfbff60e5ec299f211253656d87650d63fb8a3a899b957bf4ee06dfe36` | no (untouched, out of scope) |
| `dist/SPT_Runtime/user/mods/ArenaMapPort/maschine-ArenaMapPort.Server.dll` | 19,968 | 2026-09-14 12:10 | — | no (untouched, out of scope) |
| `dist/SPT_Runtime/user/mods/ArenaMapPort/db/base.json` | 10,968 | 2026-09-14 08:14 | — | no (untouched, out of scope) |

Both changed bundle hashes cross-checked byte-for-byte identical between `dist/` and
`work/build/full/` (the validated source), confirming `dist/` carries exactly the validated build,
not a re-derived copy.

## 9) Timings summary

| Step | Wall time |
|---|---|
| Extraction (`extract_equator.py`, full 5-scene run) | ~2m16s |
| Unity import + 5x scene assembly + Scripts scene + NavMesh bake + bundle build + verify | ~5.5min |
| MonoScript identity patch (navmesh: instant; scenes: incl. `UnityPy.load()` of ~312MB) | ~5m40s |
| `validate_extract.py` (incl. 2x full re-extraction for reproducibility) | ~5m26s |
| `validate_bundle_roundtrip.py` + `validate_preset.py` | < 2min |
| Client rebuild (`dotnet build -c Release`) | ~2.5s |
| **Total (this pass, manual steps standing in for `build_all.ps1`'s own false-positive-aborted run)** | **~19min** |

## 10) Interaction with the other agent's runtime patch (`ArenaMapPort.Client/MapBorderRepairs.cs`)

A separate agent added `MapBorderRepairs.cs` (client-side Harmony postfix) earlier today as an
immediate-relief workaround for the currently-*deployed* bundle (built before this importer fix):
it disables the 41 TeamFight `LEVEL-BORDERS TF` `MeshRenderer`s/`BoxCollider`s and does narrow
name-based matching (`_stencil` suffix, `colider <N>`, `Cube` in `Arena_Equator_Lighting`) for the
rest, entirely at runtime. That file's own doc comment already notes it is "deliberately narrow and
name-based rather than a general `m_Enabled` fix ... every match here becomes a no-op once the
bundle is rebuilt from an importer that honors `m_Enabled`". With this pass's rebuilt
`arena_equator_scenes.bundle` now shipping in `dist/`, every one of those objects already comes in
`.enabled = false` at load time, so `MapBorderRepairs.cs`'s runtime patch is now effectively a
no-op safety net rather than the active fix — left as-is (not part of this task's scope to remove;
`ArenaMapPort.Client` was only rebuilt, not edited, per this task's own instruction).

## Files touched

- `tools/extract/extract_equator.py` — `"enabled"` field on 8 component builders.
- `UnityProject/Assets/ArenaMapPort/Editor/Import/SceneAssembler.cs` — `ReadEnabled()` helper +
  `component.enabled` assignment on 7 attach methods.
- `UnityProject/Assets/ArenaMapPort/Editor/Import/AuthorScriptsScene.cs` — `LevelSettings` fog
  authoring (3 lines).
- `UnityProject/Assets/ArenaMapPort/Editor/NavMesh/BakeNavMesh.cs` — confirmation comment only, no
  behavior change.
- `docs/SPEC.md` — §C.6 `"enabled"` field documentation.
- `tools/validate/validate_extract.py` — `validate_disabled_component_counts()` +
  `count_disabled_in_package()`/`count_disabled_in_arena()` + `KNOWN_ARENA_DISABLED_COUNTS`/
  `ENABLED_COMPONENT_TYPES`.
- `tools/validate/validate_bundle_roundtrip.py` — `validate_disabled_component_counts_bundle()` +
  `count_disabled_in_package_scenes()`/`count_disabled_in_bundle_scenes()` + `ENABLED_COMPONENT_TYPES`.
- `work/extract/equator/**`, `work/build/full/**`, `dist/**` — regenerated by this pass (all
  gitignored, not repo files, listed here for completeness).
- `docs/progress/fix-enabled-state.md` — this file, new.

No file under `D:/SPT41`, `D:/SPT41vanilla`, `C:/SPT41*`, `D:/sptmaps`,
`D:/Battlestate Games`, or `D:/SPT41/Development/ArenaMode` was written. No BSG-derived assets were
committed (`work/`/`dist/` stay gitignored). No `git add`/`git commit` was run — per workspace
rule, the lead commits.

## Verification fix-ups

A verifier reviewed the deployed fix and flagged one defect in `MapBorderRepairs.cs` (the other
agent's runtime safety net, §10 above):

**Finding: Item 2's doc-comment overstates its coverage.** The class doc-comment claimed Item 2
"targets exactly the other disabled-in-Arena components the playtest's root-cause dump named
outside the TeamFight borders." That is false. Item 2 only matches three narrow name shapes
('Cube' in `Arena_Equator_Lighting`, a `_stencil`/`_STENCIL` name suffix, 'colider &lt;N&gt;').

Re-verified directly against `work/extract/equator/scenes/*.json` (name + `enabled` fields per
component, same source the original root-cause dump used):

| Scene | Disabled components | Matched by Item 2's 3 patterns | Unmatched |
|---|---:|---:|---:|
| `Arena_Equator` | 29 | 6 | 23 — all 21 `'ballistic'`-named `MeshCollider`s + 2 `'cigarette_musor4_LOD0'` `MeshCollider`s |
| `Arena_Equator_DesignStuff` | 388 | 0 | 388 — all of it (`'Loot'` x143, `'CR_ctacticall_body_lod0'` x26, `'mask_0_lod1'` x26, `'AR_6B2_lod0/1'` x38, `'Graffiti_Art_21'`, `'mustache_LOD1'`, etc.) |
| `Arena_Equator_Lighting` | 41 | 38 | 3 — `'mall_ceiling_01_center_stencil (1)'`/`(2)`, `'mall_ceiling_01_stencil (1)'` (Unity's duplicate-name `" (N)"` suffix defeats the `EndsWith("_stencil")` check) |
| `Arena_Equator_TeamFight_DesignStuff` | 49 | 41 (Item 1's border check, not Item 2) | 8 — `'AreaLight'` x4, `'AreaLight_GreenExit_small (1)'` x4 |
| `Arena_Equator_TeamFight` | 0 | 0 | 0 |
| **Total** | **507** | **85 (41 via Item 1 + 44 via Item 2)** | **≈422 via neither item** |

Confirmed real-world impact is nil today: this task's rebuilt `arena_equator_scenes.bundle`
(`dist/`, SHA-256 `adb8c591...df64fb1035`) already ships every one of these ≈422 objects with the
correct `enabled=false` from the importer fix itself (§7 above, exact per-scene match), so all of
them are already correct independent of Item 2's pattern coverage. The doc-comment's overstatement
was latent risk only (would mislead a future reader relying on Item 2 as full coverage if this
client build were ever paired with an older, un-rebuilt bundle), not an active bug.

**Fix applied** (`ArenaMapPort.Client/MapBorderRepairs.cs`, doc-comment only — no C# logic changed,
no bundle content affected): rewrote the Item 2 doc-comment to state its real, narrow scope (the
exact 3 name shapes it matches, including the `" (N)"` duplicate-suffix gap in the stencil check)
and to explicitly list the ≈400 disabled-in-Arena objects it does *not* cover (DesignStuff mannequin
gear, `'ballistic'`-named colliders in `Arena_Equator`, `'AreaLight'`-named renderers in
`Arena_Equator_TeamFight_DesignStuff`), rather than claiming exhaustive coverage. Left the matching
logic itself untouched — broadening it was considered and rejected: the pipeline-level fix already
makes every one of these objects correct from the bundle itself, so adding ~20 more name patterns to
a client-side runtime patch would add risk (e.g. false-positive matches against unrelated objects
sharing a common name like `'Loot'`) for zero behavioral gain.

Verified via `dotnet build ArenaMapPort.Client/ArenaMapPort.Client.csproj -c Release` — 0 errors,
0 warnings, 69,120-byte DLL (same size as before; only embedded PDB/source-line metadata differs, no
IL change from a comment-only edit). Bundle files re-checked byte-for-byte unchanged from §8's table
(`arena_equator_scenes.bundle`/`arena_equator_navmesh.bundle`/`arena_equator_preset.bundle` all same
SHA-256 as before this fix-up) — correctly no pipeline re-run was needed since nothing but a C#
doc-comment changed. No git commit made (workspace rule: the lead commits).

### `dist/` — final file listing (after this fix-up)

| File | Size (bytes) | Timestamp | SHA-256 | Changed by this fix-up? |
|---|---:|---|---|---|
| `dist/BepInEx/plugins/ArenaMapPort/maschine-ArenaMapPort.Client.dll` | 69,120 | 2026-09-14 21:07 | `f452c118c6b8c8d5fe4de45db877d2dc290c6f2a5be551026d9c8a3c0fad0554` | rebuilt (doc-comment-only source edit; same size, new hash from source/PDB metadata) |
| `dist/BepInEx/plugins/ArenaMapPort/material-shaders.json` | 43,237 | 2026-09-14 20:37 | `0724b11c2e97b4b1710384d1498af2a2de061e21d1ab98bc1da711c142b1ad17` | no |
| `dist/BepInEx/plugins/ArenaMapPort/bundles/arena_equator_scenes.bundle` | 308,912,270 | 2026-09-14 20:47 | `adb8c5916da001f068fe345d3c18d167ec74357d003b9853ff3e16df64fb1035` | no |
| `dist/BepInEx/plugins/ArenaMapPort/bundles/arena_equator_navmesh.bundle` | 72,842 | 2026-09-14 20:38 | `25d6165c3c2535e4ae3ae77f4d08b4a3963f9addcfb275b546543d51fea10f6b` | no |
| `dist/BepInEx/plugins/ArenaMapPort/bundles/arena_equator_preset.bundle` | 6,891 | 2026-09-13 17:29 | `f12cf0bfbff60e5ec299f211253656d87650d63fb8a3a899b957bf4ee06dfe36` | no |
| `dist/SPT_Runtime/user/mods/ArenaMapPort/maschine-ArenaMapPort.Server.dll` | 19,968 | 2026-09-14 12:10 | — | no (untouched, out of scope) |
| `dist/SPT_Runtime/user/mods/ArenaMapPort/db/base.json` | 10,968 | 2026-09-14 08:14 | — | no (untouched, out of scope) |

### Files touched by this fix-up

- `ArenaMapPort.Client/MapBorderRepairs.cs` — Item 2 class doc-comment corrected (no logic change).
- `docs/progress/fix-enabled-state.md` — this section.
- `dist/BepInEx/plugins/ArenaMapPort/maschine-ArenaMapPort.Client.dll` — recompiled (comment-only
  source change; bundles untouched).
