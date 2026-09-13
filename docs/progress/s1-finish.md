# s1-finish — progress log

Status: **item 1 (Cecil fix for the SIGSEGV) done and verified — the crash is fixed.** Item 3
(`stripUnusedMeshComponents = false`) done. Item 2 (complete S1 pipeline incl. Scripts scene) is
**blocked by a second, separate, previously-undiscovered Unity Editor limitation**, found and
exhaustively diagnosed this pass (not the SIGSEGV — that's fixed). Everything in the pipeline
*except* Scripts-scene authoring was run for real against the S1 slice, cleanly, zero errors,
independently re-verified with `tools/validate/validate_bundle_roundtrip.py`. Item 4 (checking
`editor_verify.json` against §D) cannot be completed until item 2's new blocker is resolved — see
"What was NOT achieved" below for the honest gap. No git commit made (O5).

## 1) The Cecil fix — implemented, applied, verified (task item 1)

**Root cause, re-confirmed against real evidence before touching anything**: read
`D:/SPT41/Development/_decompiled/UnitySourceGeneratedAssemblyMonoScriptTypes_v1.cs` directly —
confirmed it carries Unity's own `[GeneratedCode("Unity.MonoScriptGenerator.MonoScriptInfoGenerator", null)]`
attribute and a `public static MonoScriptData CG_smethod_0()` method where Unity's own emitted
source (this is a real Unity-Editor-generated file, not BSG's) would name it `Get`. Grepped the
*entire* decompile for `CG_smethod_0` to rule out a shared/global naming scheme: five unrelated
hits, each a **different class's own independent local counter starting at 0**
(`CG_Class3645.CG_smethod_0`, `DressItemPanel.CG_smethod_0`, `GridView.CG_smethod_0`, etc.) — none
reference `UnitySourceGeneratedAssemblyMonoScriptTypes_v1` at all, confirming the type is never
called from any decompiled C# source (it exists purely for the Editor's own native reflection
scrape).

**Tool built**: `tools/gamerefs-fixup/` (new, `GameRefsFixup.csproj` + `Program.cs`, `net9.0`,
referencing the local read-only `D:/SPT41/BepInEx/core/Mono.Cecil.dll` via `HintPath` — no NuGet
download, per the task's instruction). For each target DLL it:
1. Reads the assembly into memory (never keeps the source file locked), with a
   `DefaultAssemblyResolver` searching the DLL's own directory (GameRefs) **and**
   `D:/SPT41/EscapeFromTarkov_Data/Managed` (read-only lookup only, for resolving cross-assembly
   type references like `Unity.Burst`/`UnityEngine.CoreModule` that Cecil needs to parse/write
   custom-attribute and constant-type metadata — without this, both `ReadAssembly` and `Write`
   throw `AssemblyResolutionException`; found and fixed this pass).
2. Finds `UnitySourceGeneratedAssemblyMonoScriptTypes_v1` (+ its nested `MonoScriptData` struct,
   collected transitively).
3. Scans the **entire module** (every other type's base type, interfaces, fields, properties,
   events, generic constraints, custom attributes, and every method's return type/parameters/
   locals/exception handlers/IL instruction operands, plus module- and assembly-level attributes
   and exported types) for any reference to the target type — a real, from-scratch metadata walk,
   not a guess.
4. If zero references: **removes** the type (and nested types go with it automatically, since
   Cecil's `ModuleDefinition.Types` only lists top-level types — nested ones are removed once
   their declaring type is removed). If references exist: **falls back to renaming** the
   `CG_smethod_0` method back to `Get`, leaving the type in place, and logs exactly why.
5. Writes the modified assembly back to the same path (via an in-memory `MemoryStream`, so the
   original file is never held open while being replaced).

**Verified working** (`D:/ArenaMapPort/work/tmp_fixup_test`, then for real via `sync-game-refs.ps1`):
```
[GameRefsFixup] Assembly-CSharp: removed 'UnitySourceGeneratedAssemblyMonoScriptTypes_v1' (+ 1 nested type(s)) — verified zero other references in the module.
[GameRefsFixup] Assembly-CSharp: wrote 16,161,792 bytes -> .../GameRefs/Assembly-CSharp.dll (was 16,233,472 bytes).
[GameRefsFixup] Assembly-CSharp-firstpass: removed 'UnitySourceGeneratedAssemblyMonoScriptTypes_v1' (+ 1 nested type(s)) — verified zero other references in the module.
```
Re-running the tool against its own output is idempotent (correctly reports "no such type present
— nothing to do" the second time), and the patched DLLs re-load cleanly with Cecil afterward
(proof they're still valid PE/CLR images, not corrupted).

**Integrated** into `tools/sync-game-refs.ps1` as step 6, right after the asmdef
`precompiledReferences` regeneration — builds `GameRefsFixup` if needed, then runs it against
`GameRefs/Assembly-CSharp.dll` and `GameRefs/Assembly-CSharp-firstpass.dll`, failing the whole
script (`throw`) if the fixup exits non-zero.

**Hypothesis verified in the Unity log before continuing, as instructed**: ran the full headless
pipeline (multiple times, including after a complete `Library/{Bee,ScriptAssemblies,ArtifactDB,
ArtifactDB-lock,SourceAssetDB,SourceAssetDB-lock,ScriptMapper,Artifacts,TempArtifacts,APIUpdater}`
wipe for a truly clean rebuild). Every run's Domain Reload Profiling block now shows:
```
ScanForSourceGeneratedMonoScriptInfo (0ms)
```
completing cleanly — **zero SIGSEGV, zero "Received signal" in any of the 9+ runs performed this
pass** (`work/build/s1/unity_s1_finish_run*.log`, `unity_s1_finish_diag*.log`), where every prior
WP2 run with the unmodified DLL crashed at exactly that point. The crash is fixed.

**A rename-vs-remove experiment, and why the final choice is removal (task item 1's actual
answer)**: partway through diagnosing a *second*, separate issue (below), I hypothesized that
removing the type (rather than renaming the method in place) might be the cause of that second
issue — reasoning that the type's own `MonoScriptData.FilePathsData`/`TypesData` payload might be
exactly what `MonoScriptInfoScraper` uses to populate an AddComponent-eligibility registry for the
assembly's MonoBehaviours. **Tested directly**: switched the tool to rename-only, wiped Library for
a clean rebuild, re-ran the same AddComponent probe — **identical failure**, byte-for-byte the same
as with removal. This disproves the hypothesis (see full detail in §2 below) — remove vs. rename
makes no difference to the second issue either way. Reverted `tools/gamerefs-fixup/Program.cs` to
its original, task-literal logic: **remove when the zero-reference check passes (the case here);
rename only as the true fallback if a reference is ever found.** The currently-shipped GameRefs
state (after the final `sync-game-refs.ps1` run this pass) is the **removal** fix, matching the
task's stated preference, with accurate code comments (the earlier, disproven "concrete reason"
reasoning was corrected, not left in place).

## 2) Second, separate blocker — found, exhaustively diagnosed, NOT resolved

With the SIGSEGV fixed, the pipeline gets much further than any prior WP2 run, but hits a **new**
wall at `AuthorScriptsScene.Build`'s very first `AddComponent<LevelSettings>()` call:
```
Script LevelSettings has not finished compilation yet. Please wait until compilation of the script has finished and try again.
```
`AddComponent<T>()` returns **null** (does not throw) — the original code's very next line
dereferenced that null, producing the `NullReferenceException` seen in the earliest post-fix runs.

This is a real, independent problem from the SIGSEGV — it only became visible once the SIGSEGV
stopped hiding it, and SPEC.md's own §H risk #1/#8 already flagged this exact class of risk as
"unproven... no citation or precedent" for referencing `Assembly-CSharp.dll` as a non-"Any
Platform" Editor plugin.

### Every mitigation attempted, and the result (all runs logged under `work/build/s1/unity_s1_finish_*.log`)

| # | Attempt | Result |
|---|---|---|
| 1 | Retry `AddComponent<T>()` up to 80× with `Thread.Sleep(250ms)` between attempts (20s total), all on the same synchronous `-executeMethod` call stack | **0/80 succeeded.** Every single attempt returned null identically. |
| 2 | Full `Library` wipe (`Bee`/`ScriptAssemblies`/`ArtifactDB`/`SourceAssetDB`/`ScriptMapper`/`Artifacts`/`TempArtifacts`/`APIUpdater`) — rules out stale-cache staleness | Same failure, unchanged. |
| 3 | Switch `GameRefsPostprocessor`'s `PluginImporter` compatibility from "Editor + StandaloneWindows64" to Editor-only | Same failure, unchanged (WP2's run 13 only ever tested this flag against the SIGSEGV, not this issue — now tested against this issue too, still no effect; reverted to the original dual-platform setting since it's evidence-neutral). |
| 4 | Restructure `BuildBundles.Run()` to **not** call `AuthorScriptsScene.Build` synchronously — instead schedule it via `EditorApplication.update` (a real editor-loop tick, not a thread sleep), removing `-quit` from the Unity command line so the process stays alive to actually tick, with `BuildBundles` now owning `EditorApplication.Exit()` on every path | First test (run6) showed exactly **one** "not finished compilation" failure before a *different*, self-inflicted bug (a dangling unsaved scene from the failed attempt blocking the next `NewScene(..., Additive)` call) took over — looked promising. |
| 4b | Fixed the dangling-scene bug (close the unsaved active scene between retry attempts when in Additive mode) and re-ran with the *real* tick-based retry unmasked, budget raised to 2000 ticks | **0/2000 ticks succeeded.** Every real `EditorApplication.update` tick, not just elapsed time, still returns null. This is the most conclusive negative result — genuine engine-loop ticking does not help either. |
| 5 | Isolated diagnostic (`BuildBundles.TestAddComponentOnly`, a **new, tiny `-executeMethod` entry point**): call `AddComponent<LevelSettings>()` as the *literal first and only thing* the Editor does after startup — nothing else in the pipeline runs first, ruling out "our own heavy asset-import activity earlier in the session is somehow the cause" | **Still returns null**, immediately, in complete isolation. Conclusively rules out interference from the rest of the pipeline. |
| 6 | Tried three different AddComponent-equivalent code paths in one diagnostic run: `GameObject.AddComponent<LevelSettings>()`, `UnityEditor.ObjectFactory.AddComponent(go, typeof(LevelSettings))` (the API the Editor's own "Add Component" menu uses), and `go.AddComponent(reflectedType)` after independently confirming via `AppDomain.CurrentDomain.GetAssemblies()` that the CLR **has** already loaded the real `LevelSettings` type from this exact assembly (`GetType("LevelSettings")` resolves to a genuine `System.Type`, proving the type is NOT "still compiling" in any ordinary CLR sense) | All three fail identically. `ObjectFactory.AddComponent` **throws** the exact same message text as an `ArgumentException` rather than returning null, but it's the same underlying check. |
| 7 | Rename-vs-remove (see §1 above) | No difference either way. |

**Conclusion**: this is a genuine, 100%-reproducible limitation of registering a MonoBehaviour type
from a precompiled, non-"Any Platform" `Assembly-CSharp.dll` Plugin DLL for `AddComponent` purposes
in this exact Unity 2022.3.43f1 Editor build. It is not a timing issue (ticks and real time both
fail), not a caching issue (clean Library fails identically), not a platform-compatibility-flag
issue, not an API-choice issue, and not affected by whether the crash-causing type is removed or
renamed. No workaround was found from ArenaMapPort's own C# editor code.

**Recommendation for the lead** (this needs an architecture decision, same as WP2's own
recommendation #3, now with much stronger evidence behind it): typetree-stub cloning for just the
five §D types (`LevelSettings`, `LocationScene`, `BotZone`, `LocationExportInfo`,
`EFT.Game.Spawning.SpawnPointMarker`/`SpawnPoint`) instead of referencing `Assembly-CSharp.dll`
directly for AddComponent purposes — the Icebreaker/Labs precedent SPEC.md's §B.WP2.1 originally
reasoned past. A stub MonoBehaviour compiled as part of *this* project's own source (not a
precompiled plugin) would go through Unity's normal `MonoImporter`/script-compilation path, which
demonstrably works fine in this same Editor (`ArenaMapPort.Editor.dll` itself compiles and its
types are freely `AddComponent`-able — confirmed throughout every run: `PhysicMaterialCache`,
`SceneAssembler`, etc. are our own types and never hit this issue). This is the most concretely
actionable path, at the cost of re-introducing the typetree-fidelity risk O2 specifically wrote
this design to avoid — a real trade-off for the lead to weigh, not something to decide here.

### What WAS achieved despite the blocker

To maximize real, verified progress rather than leaving everything blocked, ran the complete rest
of the pipeline with the pre-existing `-skipScriptsScene true` diagnostic flag (unchanged from
WP2), confirming the SIGSEGV fix, item 3's fix, and the entire mesh/material/texture/scene/
NavMesh/bundle-build/verify pipeline all work cleanly end-to-end:

```
D:\ArenaMapPort\work\build\s1\unity_s1_finish_run9_restonly.log  (real command below)
```
```
Unity.exe -batchmode -nographics -projectPath D:\ArenaMapPort\UnityProject
  -executeMethod ArenaMapPort.Editor.BundleBuild.BuildBundles.Run
  -packageDir D:/ArenaMapPort/work/extract/equator_s1 -outputDir D:/ArenaMapPort/work/build/s1
  -bakeNavMesh true -verify true -skipScriptsScene true
  -logFile D:\ArenaMapPort\work\build\s1\unity_s1_finish_run9_restonly.log
```
(note: **no `-quit`** — `BuildBundles.Run()` now owns process exit via `EditorApplication.Exit()`,
required for the Scripts-scene update-tick retry machinery even when that step is skipped, since
the same code path is shared.)

Result: **zero `error CS`/`Exception`/`Received signal` in the entire log.** Produced for real:
- `work/build/s1/arena_equator_scenes.bundle` (17.8 MB) — `StandaloneWindows64`, both content
  scenes present.
- `work/build/s1/arena_equator_navmesh.bundle` — `StandaloneWindows64`, one `NavMeshData` asset
  (see the NavMesh caveat below).
- `work/build/s1/editor_verify.json` — both content scenes' real counts (see below).
- `PlayerSettings.stripUnusedMeshComponents: False -> false` logged explicitly (task item 3 — set
  unconditionally right before `BuildAssetBundles`, as required).

**Independently re-verified** with WP6's own `tools/validate/validate_bundle_roundtrip.py`
(unmodified, run fresh against this real output):
```
ok : GameObject count in bundle (64) == package (64)
ok : Mesh count in bundle (47) == package (47)
ok : Texture2D count in bundle (25) == package (25)
ok : MeshFilter/MeshRenderer/MeshCollider/BoxCollider/SphereCollider/CapsuleCollider/Light/LODGroup counts all match package exactly
ok : convex (2) / non-convex (14) MeshCollider counts match
ok : all 47 meshes' vertex_count/submesh_count match; all 12 materials carry an O1-compliant "Hidden/ArenaMapPort/" stand-in shader name
ok : no LODGroup renderer slot is null
ok : every SerializedFile's target platform == StandaloneWindows64 (19), unity_version == 2022.3.43f1
ok : editor_verify.json's per-scene game_object_count/mesh_filters/renderers/lod_groups all match the package exactly, for both scenes
2 check(s) FAILED (exactly and only the two expected from skipping the Scripts scene):
  FAIL: none of the 5 SS D EFT components found (expected — Scripts scene not authored this run)
  FAIL: editor_verify.json missing scene 'Arena_Equator_Scripts' (expected, same reason)
1 WARN: Material count 13 vs package 12 (a benign +1 built-in Skybox/RenderSettings default per scene, same as WP2's own prior partial-pipeline finding — not a defect)
```

**NavMesh bake — ran, but produced 0 sources** (task explicitly allows this: "failure to bake a
tiny slice is acceptable if explained"): `BakeNavMesh.Bake()` logged
`agentRadius=0.3 agentHeight=1.7 agentSlope=48 agentClimb=0.38 tileSize=256 ... sources=0` — SPT's
correct agent-0 values were fed into the bake (per §B.WP2.7), the bake call itself completed
without error and produced a valid (if geometry-less) `NavMeshData` asset that the bundle build
included correctly, but `UnityEngine.AI.NavMeshBuilder.CollectSources` found zero collider sources
from `contentSceneRoots` despite the Arena_Equator scene genuinely having 16 MeshColliders + 4
BoxColliders at that point. Not diagnosed further this pass (time budget went to the far more
severe AddComponent blocker) — a plausible, untested lead: `CollectSources` may need
`UnityEngine.Physics.SyncTransforms()` called first so PhysX's broadphase has actually registered
colliders added moments earlier in the same frame, though `CollectSources` is documented as a
pure hierarchy walk rather than a physics-broadphase query, so this is a guess, not a confirmed
cause — flagged honestly for whoever picks this up next, not fixed here.

### What was NOT achieved (honest gap against the task's own acceptance bar)

- **The Scripts scene (`Arena_Equator_Scripts`) is not authored** in any real, non-diagnostic run —
  blocked by the issue in §2. `editor_verify.json`'s `eft_components` block therefore correctly
  shows all-zero counts for `LevelSettings`/`LocationScene`/`BotZone`/`LocationExportInfo`/
  `SpawnPointMarker` in the one real artifact that exists (`work/build/s1/editor_verify.json`,
  produced via the `-skipScriptsScene true` run) — **task item 4's §D field-value check cannot be
  completed** until item 2's blocker is resolved. `AuthorScriptsScene.cs`'s actual field-by-field
  correctness (§D's exact values) therefore also remains unverified by a live run, same as WP2 left
  it — only reasoned from the decompiled source.
- The canonical (non-`-skipScriptsScene`) run (`unity_s1_finish_run8_final.log`) is kept as the
  honest record of the current, still-blocked state: reaches `AuthorScriptsScene.Build`, retries it
  across 50 real `EditorApplication.update` ticks (all fail identically), then exits cleanly with a
  clear, specific error message and non-zero exit code — no crash, no silent hang, no misleading
  success.
- NavMesh tile count could not be meaningfully reported (0 sources collected — see above).

## Files touched

- `tools/gamerefs-fixup/GameRefsFixup.csproj`, `tools/gamerefs-fixup/Program.cs` — new, the Cecil
  fix tool (task item 1).
- `tools/sync-game-refs.ps1` — integrated the fixup as step 6; updated header doc comment.
- `UnityProject/Assets/ArenaMapPort/Editor/BundleBuild/BuildBundles.cs` — task item 3
  (`stripUnusedMeshComponents = false`); restructured Scripts-scene authoring to retry across real
  `EditorApplication.update` ticks instead of a synchronous call (best-effort, does not currently
  succeed — see §2); added the `TestAddComponentOnly` diagnostic entry point (harmless, not part of
  the real pipeline); the whole method now owns `EditorApplication.Exit()` since the Unity command
  line no longer passes `-quit`.
- `UnityProject/Assets/ArenaMapPort/Editor/Import/AuthorScriptsScene.cs` — simplified
  `AddComponentRetrying` → `AddComponentOrThrow` (fail-fast; the outer tick-based retry in
  `BuildBundles.cs` is what actually retries now).
- `UnityProject/Assets/ArenaMapPort/Editor/GameRefsSetup/GameRefsPostprocessor.cs` — comment-only
  net diff (documents the Editor-only compatibility experiment and its negative result; logic
  reverted to O2's original "Editor + StandaloneWindows64").
- `work/build/s1/unity_s1_finish_run*.log`, `unity_s1_finish_diag*.log` — every run this pass, kept
  as evidence (gitignored, not part of the repo).
- `work/build/s1/arena_equator_scenes.bundle`, `arena_equator_navmesh.bundle`, `editor_verify.json`,
  `material-shaders.json` — real output from the `-skipScriptsScene true` run (gitignored).

## Handoff

1. **Item 1 is done and safe to build on** — the SIGSEGV fix is real, verified, and integrated into
   the normal `sync-game-refs.ps1` setup step; no further action needed there.
2. **Item 2/4 need a lead decision** on §2's recommendation (typetree-stub cloning for the 5 §D
   types) before the Scripts scene can be authored and checked against §D — this is now a
   well-evidenced architecture question, not an open-ended debugging task.
3. Re-run `python tools/validate/validate_bundle_roundtrip.py` once a real
   `Arena_Equator_Scripts` scene exists in the bundle — it is already correctly wired to check
   exactly the §D fields the task asks about (namespace/assembly identity, `PatrolWays`/
   `SpawnPointMarkers`/`UnSpawnPoints` null-vs-empty, spawn marker Player category + collider,
   etc.) and needs no changes itself.
4. The NavMesh `sources=0` lead (`Physics.SyncTransforms()`) is a cheap thing to try first the next
   time someone is in `BakeNavMesh.cs`.
