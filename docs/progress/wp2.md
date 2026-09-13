# WP2 — Unity importer + stand-in shaders + NavMesh bake + bundle build — progress log

Status: **code complete, most of the pipeline verified working end-to-end via a real headless Unity
run against the S1 slice. Blocked on one specific step (§D's Scripts-scene authoring) by a
reproducible native Unity Editor crash, root-caused this pass and isolated as narrowly as tooling
allows.** Full details, evidence and recommendations below. No git commit made (O5).

## Summary for the lead (read this first)

- **Working, verified against real S1 data this pass**: `TextureImportDriver`, `StandinShaderGenerator`
  + material-shaders.json sidecar (O1), `MaterialImporter`, `MeshImporter`, `SceneAssembler` (full
  two-pass hierarchy/collider/light/LODGroup assembly), `BuildBundles`' orchestration and
  `BuildPipeline.BuildAssetBundles(..., BuildTarget.StandaloneWindows64)` call. A real
  `arena_equator_scenes.bundle` was built from the S1 package and independently re-read with UnityPy
  (see "Partial-pipeline verification" below) — counts match WP1's package exactly, target platform
  header is `StandaloneWindows64` (19), Unity version `2022.3.43f1`.
- **Blocked**: `AuthorScriptsScene.cs` (§B.WP2.6/§D) cannot currently run to completion, because
  **loading `Assembly-CSharp.dll` as an Editor-compatible precompiled plugin DLL reproducibly
  crashes this Unity Editor (2022.3.43f1) with a native SIGSEGV** inside
  `MonoScriptInfoScraper::ScanForSourceGeneratedMonoScriptInfo` during the post-compile domain
  reload — before any of our own `-executeMethod` code even runs. This is a genuine Editor-level
  bug/limitation, not a bug in this repo's C# (see the isolation matrix below). It blocks §B.WP2.1's
  entire "reference the real Assembly-CSharp.dll directly" design as currently specified, for the
  one thing that actually needs it (§D's five EFT components).
- **Two real bugs found and fixed in already-existing code this pass** (both were previously
  believed correct but had never been run against a live Unity process): `tools/sync-game-refs.ps1`'s
  exclusion filter was wrongly excluding two required assemblies, and `SceneAssembler`/
  `AuthorScriptsScene` didn't handle the very first scene of a batchmode session correctly. Both are
  fixed, both are independently confirmed fixed (compile/behaviour verified), neither relates to the
  blocking crash.
- **Everything below §D/AuthorScriptsScene in the pipeline (NavMesh bake driver, editor_verify.json)
  is implemented but NOT yet exercised against real data**, since S1 never got that far — the
  NavMesh bake is explicitly optional for S1 per SPEC.md §G.S1 ("skip ... unless trivial to
  include"), and `VerifyBuild.cs`'s own logic could not be tested for the same reason
  `AuthorScriptsScene.cs` couldn't (it also needs Assembly-CSharp's EFT types to read back
  `LevelSettings`/`LocationScene`/`BotZone`/etc.).

**Recommended next step for the lead**: this needs an architecture decision, not just more
debugging time — see "Recommendations" at the end.

---

## What was implemented (all under `UnityProject/Assets/ArenaMapPort/Editor/**`, tracked;
`tools/sync-game-refs.ps1`, tracked)

A prior session in this same workspace had already produced `ArenaPaths.cs`, `ArenaPackage.cs`,
`MeshImporter.cs`, `MaterialImporter.cs`, `TextureImportDriver.cs`, `StandinShaderGenerator.cs`,
`GameRefsPostprocessor.cs`, the `ArenaMapPort.Editor` asmdef, and a working `tools/sync-game-refs.ps1`
+ `tools/gamerefs-closure` (System.Reflection.Metadata-based closure computation, per O2). This pass
reviewed all of that against SPEC.md/O1-O3 (found it correct as written), then implemented the rest
of §B.WP2 and ran the whole thing for real:

| File | New this pass? | Purpose |
|---|---|---|
| `Editor/Common/PhysicMaterialCache.cs` | Yes | §B.WP2.5 "dedup by value" PhysicMaterial cache shared across the whole build. |
| `Editor/Import/SceneAssembler.cs` | Yes | §B.WP2.5 two-pass hierarchy assembly (GameObjects → components except LODGroup → LODGroups against a completed id→Renderer map). |
| `Editor/Import/AuthorScriptsScene.cs` | Yes | §B.WP2.6/§D — the fresh-authored `Arena_Equator_Scripts` scene: `LevelSettings`, `LocationScene`+`FillArrays()`, `BotZone` (non-null `PatrolWays`/`SpawnPointMarkers`/`UnSpawnPoints`), `LocationExportInfo`, 3 `SpawnPointMarker`s (All/Usec/Bear) each with an enabled `BoxCollider`. See "ExecuteInEditMode hazard" below for a real, fixed authoring-order bug found this pass. |
| `Editor/NavMesh/BakeNavMesh.cs` | Yes | §B.WP2.7 NavMesh bake driver (SPT's agent-0 values from `spt_gm.json`, option (b) — bypasses `NavMeshSurface`, calls `NavMeshBuilder.BuildNavMeshData` directly). Compiles cleanly (fixed two real API-shape bugs — see below); **not exercised against real geometry this pass** (optional for S1 per §G.S1, and S1 never got past the Scripts-scene blocker to reach it in a real run). |
| `Editor/BundleBuild/BuildBundles.cs` | Yes | §B.WP2.8 orchestrator + `-executeMethod` entry point. Runs the whole pipeline in order (execution-order note honoured: textures → shaders → materials → meshes → scenes → Scripts scene → [navmesh] → bundle build → verify), all paths parameterised (O7), `BuildTarget.StandaloneWindows64` passed explicitly (§H risk #12). |
| `Editor/Verify/VerifyBuild.cs` | Yes | Task-instructed headless verification pass — reopens every built scene fresh via `EditorSceneManager.OpenScene` and writes `work/build/<slice>/editor_verify.json` with GameObject/component/EFT-component counts. **Not yet exercised** (needs the Scripts scene, same blocker). |
| `tools/sync-game-refs.ps1` | Fixed (pre-existing) | Two real bugs fixed — see below. |

### ExecuteInEditMode hazard (found and fixed this pass, not previously documented anywhere)

`LocationScene` and `SpawnPointMarker` (both real `Assembly-CSharp` types) carry `[ExecuteInEditMode]`.
Unity invokes their `Awake()`/`Start()` **synchronously the moment such a component is attached to
an active-in-hierarchy GameObject, even outside Play mode** — confirmed by direct read of the
decompiled source (`D:/SPT41/Development/_decompiled/LocationScene.cs`,
`EFT.Game.Spawning/SpawnPointMarker.cs`). `LocationScene.Awake()` unconditionally dereferences
`WorldInteractiveObjects.Length` with **no null guard** — the exact NRE site SPEC.md's §D table
already flags for the **runtime** case, but it fires just as surely at **author time** if the field
is still null when Awake runs, which it is on the very first `AddComponent<LocationScene>()` call,
before `FillArrays()` has had a chance to run.

Fix (`AuthorScriptsScene.Build`): build the entire hierarchy — `LevelSettings`, `LocationExportInfo`,
`BotZone`, all `SpawnPointMarker`s, `LocationScene` — under one root `GameObject` that is
`SetActive(false)` **before any child/component is created**. Since `activeInHierarchy` is false for
every descendant regardless of their own `activeSelf`, no `ExecuteInEditMode` callback fires while
wiring fields. `LocationScene.FillArrays()` is called last (still while inactive — its internal
`GetComponentsInChildren<T>(includeInactive: true)` scans work regardless of active state), and only
then is `root.SetActive(true)`, firing every `Awake()`/`Start()` exactly once, against fully-wired
data — matching what a real scene load does. This is *not* a workaround for a symptom; it's the
correct authoring order regardless of the platform-compatibility crash below.

### Two real GameRefs exclusion bugs found and fixed (`tools/sync-game-refs.ps1`)

The first real headless run surfaced two false positives in the exclusion filter's coarse
prefix-match heuristic (`UnityEngine*`/`System*` = "editor-provided", per O2's own literal wording)
— via the exact Editor-log text O2 says to check for:

```
Assembly 'Assets/.../GameRefs/Assembly-CSharp.dll' will not be loaded due to errors:
  Unable to resolve reference 'UnityEngine.UI'.
  Unable to resolve reference 'System.Runtime.CompilerServices.Unsafe'.
(same 'UnityEngine.UI' failure also broke Assembly-CSharp-firstpass.dll, Cinemachine.dll,
Unity.TextMeshPro.dll, DOTween.Modules.dll, which all reference it too)
```

Both are real files under the Managed dir and already flow through the closure computation; the
prefix filter alone was wrong.

- **`System.Runtime.CompilerServices.Unsafe`** — carved out of the `System*` exclusion prefix, now
  copied into GameRefs like any other required dependency. No further issue.
- **`UnityEngine.UI`** — this one turned out to be load-bearing for the crash investigation (see
  below). Two fixes were tried:
  - Route it through Unity's own `com.unity.ugui@1.0.0` built-in package instead of copying SPT's
    DLL — **reverted**: that exact locally-cached package version fails to compile against this
    Editor install with a real, reproducible error (`Library/PackageCache/com.unity.ugui@1.0.0/
    Runtime/UI/Core/MaskUtilities.cs` etc.: `CS0117 'ListPool<Canvas>' does not contain a definition
    for 'Get'/'Release'`) — a genuine bug/version-skew in this machine's offline package cache,
    unrelated to this project, not something fixable here.
  - Carve it out of the exclusion prefix like `System.Runtime.CompilerServices.Unsafe` and copy
    SPT's own DLL — **kept**, since (see below) it turned out not to be the actual crash trigger
    after all; a synthetic empty stub assembly named `UnityEngine.UI` was swapped in for one test and
    the crash persisted identically, proving the DLL's *content* was never the issue, only whether
    the *name* resolves.

Current state: 48 DLLs copied (was 46), asmdef `precompiledReferences` regenerated to match.

---

## S1 run history and the crash diagnosis

17 real headless Unity runs this pass (`work/build/s1/unity_s1_run*.log`, kept for the record).
Summarised:

| Run(s) | Finding | Fix |
|---|---|---|
| 1–2 | `BakeNavMesh.cs` compile errors: `NavMeshBuildSettings.manualCellSize` doesn't exist in this Unity version (real API-shape difference from what was assumed); `NavMeshBuilder` is ambiguous between `UnityEditor.AI.NavMeshBuilder` and `UnityEngine.AI.NavMeshBuilder` (both genuinely exist; only `UnityEngine.AI.NavMeshBuilder` has `CollectSources`/`BuildNavMeshData` on this Unity version — confirmed by a real `CS0117` on the other one). | Removed the bad field; fully-qualified to `UnityEngine.AI.NavMeshBuilder`. |
| 3–4 | Compiled clean, but `-executeMethod` reported `class 'BuildBundles' could not be found`. Root cause found via a wider log grep: `ArenaMapPort.Editor.dll` itself was failing to load (`will not be loaded due to errors`) because ITS OWN precompiled reference `Assembly-CSharp.dll` couldn't resolve `UnityEngine.UI`/`System.Runtime.CompilerServices.Unsafe` — a cascading failure, not a separate/transient issue as first suspected. | See "GameRefs exclusion bugs" above. |
| 5–6 | First appearance of the native SIGSEGV crash (`MonoScriptInfoScraper::ScanForSourceGeneratedMonoScriptInfo`), reproducible twice in a row, the moment both missing DLLs were added and `Assembly-CSharp.dll` finally resolved+loaded. | Investigated below. |
| 7 | Tried routing `UnityEngine.UI` through the `com.unity.ugui` package instead — that package's own bundled source fails to compile on this machine (unrelated pre-existing issue, see above). Reverted. |
| 8 | Full `Library/Bee` + `ScriptAssemblies` + `ArtifactDB` + `SourceAssetDB` wipe (forces a true clean reimport, confirmed via `"Rebuilding Library because the asset database could not be found!"`) — **crash still occurs**. Rules out incremental-cache corruption as the cause. |
| 9 | Swapped `UnityEngine.UI.dll` for a hand-built, completely empty stub assembly (same assembly name, no members) — **crash still occurs, identically**. Rules out `UnityEngine.UI.dll`'s *content* entirely; only its *presence as a name Assembly-CSharp.dll can resolve* matters. |
| 10 | Removed all `ValueTuple`/tuple-deconstruction usage from `AuthorScriptsScene.cs` (a plausible Roslyn-source-generator edge-case theory) — **crash still occurs**. Rules out that theory. |
| 11–12 | **Decisive isolation test**: physically removed `Assembly-CSharp.dll` + `Assembly-CSharp-firstpass.dll` from GameRefs (and from the asmdef's `precompiledReferences`) entirely. Project open (`-quit`, no `-executeMethod`) — **no crash**, only the expected `CS0246`/`CS0234` compile errors from our own code legitimately needing those types. This is the conclusive result: **the crash requires `Assembly-CSharp.dll` to be present and successfully Editor-loadable; it is not present in any form when that DLL is absent, regardless of every other GameRefs DLL.** |
| 13 | With `Assembly-CSharp.dll` restored, tried `GameRefsPostprocessor` set to Editor-only (StandaloneWindows64 compatibility OFF) instead of O2's "Editor + StandaloneWindows64 from the start" — **crash still occurs**. Rules out the platform-compatibility-flag combination as a factor; reverted to O2's literal instruction since it doesn't change the outcome either way. |
| 14–15 | **Partial-pipeline verification** (see next section) — `AuthorScriptsScene.cs`/`VerifyBuild.cs` temporarily moved out of `Assets/` (both need EFT types) alongside the two Assembly-CSharp DLLs, `SceneAssembler`/`BuildBundles` exercised for real against the S1 package. Run 14 hit a real, separate, now-fixed bug (see below); run 15 succeeded completely, `UNITY_RC=0`. |
| 16–17 | Everything restored to the final, spec-compliant state (real `AuthorScriptsScene.Build` call, `Assembly-CSharp.dll` present, all diagnostic file-moves undone) — confirms the crash reproduces one final time in the fully-restored codebase, run 17 done after a full clean Library wipe for a trustworthy final read. |

**Also independently confirmed this pass**: the game's own `globalgamemanagers` file embeds its
build Unity version in plaintext — `2022.3.43f1`, **byte-identical** to this Editor install (checked
directly, not assumed). This rules out an Editor-vs-game Unity version skew as the cause; the crash
is a genuine incompatibility/bug when loading BSG's real, large (~16MB), heavily-typed
`Assembly-CSharp.dll` as an Editor-compatible plugin in this exact Editor build, independent of
version matching.

### Crash signature (unchanged across every reproduction)

```
Received signal SIGSEGV
0x... (mono-2.0-bdwgc) do_runtime_invoke
0x... (mono-2.0-bdwgc) mono_runtime_invoke
0x... (Unity) scripting_method_invoke
0x... (Unity) ScriptingInvocation::Invoke
0x... (Unity) MonoScriptInfoScraper::GetAssemblyScrapedMonoScripts
0x... (Unity) MonoScriptInfoScraper::ScanForSourceGeneratedMonoScriptInfo
0x... (Unity) MonoManager::AnalyzeDomain
0x... (Unity) MonoManager::LoadAllAssembliesAndSetupDomain
0x... (Unity) MonoManager::ReloadAssembly
... (LoadDomainAndUserAssemblies -> LoadInitAssemblies -> DomainReloadIfAllowed ->
     CompileAndDomainReload -> RefreshInternalV2 -> ... -> Application::InitializeProject -> WinMain)
```

Happens during the **very first post-compile domain reload of the whole Editor session** (part of
ordinary project-open/asset-refresh, not specific to invoking `-executeMethod`) — i.e. it happens
before any of this repo's own code runs at all, purely as a consequence of `Assembly-CSharp.dll`
being present and Editor-compatible.

---

## Partial-pipeline verification (runs 14–15) — what IS proven to work

To avoid the whole rest of the pipeline being an unverified guess just because of the Scripts-scene
blocker, `AuthorScriptsScene.cs` and `VerifyBuild.cs` (the only two files needing EFT types) were
temporarily moved out of `Assets/`, `Assembly-CSharp[-firstpass].dll` pulled from GameRefs, and
`BuildBundles.Run` was given a diagnostic `-skipScriptsScene true` flag (kept in the final code,
default off, clearly commented — harmless, and useful if the lead wants to re-run this exact check)
to skip just that one step. Run 15 (`UNITY_RC=0`, clean `"Exiting batchmode successfully now!"`):

```
[ArenaMapPort][TextureImportDriver] imported 25 textures (2 flagged alpha-cutout, uncompressed)
[ArenaMapPort][StandinShaderGenerator] generated 6 stand-in shaders
[ArenaMapPort][MaterialImporter] imported 12 materials; sidecar -> .../material-shaders.json
[ArenaMapPort][MeshImporter] imported 47 meshes
[ArenaMapPort][SceneAssembler] assembled 'Arena_Equator': 63 GameObjects, 35 renderers
[ArenaMapPort][SceneAssembler] assembled 'Arena_Equator_Lighting': 1 GameObjects, 0 renderers
[ArenaMapPort][BuildBundles] built 1 bundle(s) -> D:/ArenaMapPort/work/build/s1 : arena_equator_scenes.bundle
```

Every one of these counts matches WP1's S1 package (`work/extract/equator_s1/manifest.json`) exactly
(25 textures, 12 materials, 47 meshes, 64 objects total across the two content scenes). An
independent UnityPy re-read of the built 17.8 MB `arena_equator_scenes.bundle` (this session, not
part of WP6's own tooling) confirms:

```
GameObject 64, Transform 64, Mesh 47, MeshFilter 44, MeshRenderer 35, Texture2D 25,
MeshCollider 16, Material 13, Shader 7, BoxCollider 4, LODGroup 3, Light 1
target_platform = 19 (StandaloneWindows64), unity_version = 2022.3.43f1
```

(13 Material/7 Shader vs. the logged 12/6 is one extra built-in default each, almost certainly a
Skybox/RenderSettings dependency pulled in automatically per scene — not a defect.) This confirms,
independent of the blocking crash: `MeshImporter`'s exact-order `SetVertices`→`subMeshCount`→
`SetNormals/...`→`SetIndices`→`RecalculateBounds` sequence; `TextureImportDriver`'s normal-map/
sRGB/alpha-cutout handling; `StandinShaderGenerator`'s O1-compliant stand-in shaders (real property
declarations, `Hidden/ArenaMapPort/` prefix); `MaterialImporter`'s full property/keyword/render-queue
application + sidecar emission; `SceneAssembler`'s two-pass hierarchy/renderer/collider/light/LODGroup
assembly; and `BuildBundles`' explicit `BuildTarget.StandaloneWindows64` bundle build — **all work
correctly against real S1 data**.

A real, separate bug was found and fixed during this partial run (run 14, before the fix): `
EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive)` throws
`InvalidOperationException: Cannot create a new scene additively with an untitled scene unsaved` the
very first time it's called in a fresh batchmode session, because the default startup scene is
itself untitled/unsaved. Fixed: both `SceneAssembler.Assemble` and `AuthorScriptsScene.Build` now
take an `isFirstScene` parameter (`BuildBundles.Run` tracks and passes it) — `NewSceneMode.Single`
for the first scene of a run, `NewSceneMode.Additive` for every one after it. This was previously
untested code (no prior session had run the pipeline against a live Editor), not a regression.

Everything was restored to the final, spec-compliant, non-diagnostic state after this test (files
moved back, DLLs moved back, `asmdef` regenerated via `tools/sync-game-refs.ps1`, `BuildBundles.cs`'s
commented-out real code sections restored) and re-confirmed to still hit the (expected, documented)
crash in that exact final state (run 17).

---

## What this means for §B.WP2.1 / O2

O2's core premise — "this project's Unity Editor is the exact same version as the target Mono
client... typetree-exact stub generation is unnecessary and strictly worse than referencing the real
types" — is correct about version matching (independently re-verified this pass) but the specific
mechanism (`Assembly-CSharp.dll` as an Editor-compatible precompiled Plugin DLL) hits a real,
reproducible Unity Editor crash that no code-level change in this repository can work around: not
the DLL's content (stub test), not caching, not platform-compatibility flags, not our own C# syntax.
SPEC.md's own §H risk #1/#8 flagged this exact class of thing as "unproven... no citation or
precedent," and §G.S1 exists specifically to catch it before the full 6-scene build — it did its job.

## Recommendations (for the lead's decision, not implemented here)

1. **Report this to Unity / search Unity's issue tracker** for `MonoScriptInfoScraper` +
   `ScanForSourceGeneratedMonoScriptInfo` + SIGSEGV — if this is a known, patched bug, a Unity
   Editor point-release bump might fix it outright (only 2022.3.43f1 is installed on this machine;
   no alternate local version was available to test against).
2. **Try IL-level preprocessing of `Assembly-CSharp.dll` before it lands in GameRefs** — e.g. strip
   whatever custom/generated metadata the scraper trips on (if it can be identified via a decompile
   diff) using a tool like Mono.Cecil, producing a GameRefs copy that's byte-different from SPT's
   shipped DLL but type-identical. Not attempted this pass (would need to first identify exactly
   what the scraper is choking on, which requires Unity source or a debugger neither available here).
3. **Fall back to typetree-stub cloning for just the five §D types** (`LevelSettings`, `LocationScene`,
   `BotZone`, `LocationExportInfo`, `SpawnPointMarker`/`SpawnPoint`) instead of referencing
   `Assembly-CSharp.dll` directly — the Icebreaker/Labs precedent SPEC.md's §B.WP2.1 explicitly
   reasoned past. This is the most concretely actionable path with tools already in this workspace's
   reach (`ilspycmd`, the Icebreaker/Labs reference sources), at the cost of re-introducing the
   typetree-fidelity risk O2 specifically wrote this design to avoid.
4. **A debugger-attached interactive (non-batchmode) repro**, or requesting a native crash dump from
   Unity support, would likely pinpoint the exact offending type/member in `Assembly-CSharp.dll` far
   faster than further black-box isolation from this side — not attempted (risk of an unbounded-time
   hang in this headless environment was judged not worth it against the remaining time budget).

## Honest gaps / not yet done

- `BakeNavMesh.cs` compiles cleanly but has **never run against real geometry** (S1 never reached it;
  optional for S1 per SPEC.md).
- `VerifyBuild.cs` compiles cleanly (confirmed as part of every successful compile in this pass
  outside the two isolation tests) but its actual JSON output has **never been produced or eyeballed**
  — it needs the Scripts scene, which needs the blocked step. No `editor_verify.json` exists yet.
- `AuthorScriptsScene.cs`'s actual field-by-field correctness (does `_spawnPoint`/`_collider`/
  `BotZone.SpawnPointMarkers` etc. really come out right) is **unverified by a live run** — only
  reasoned from the decompiled source. The `ExecuteInEditMode` ordering fix is believed correct by
  the same reasoning but likewise unconfirmed live.
- Neither `-bakeNavMesh true` nor a run against the full (non-slice) package was attempted — S1-only
  per the work order, and blocked besides.
