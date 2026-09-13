# s1-fix-r2 — re-verifying "S1 is not clean" and fixing the NavMesh 0-sources root cause — progress log

Role: fixer/re-verifier, task label `s1-fix-r2`. Task handoff: "S1 is not clean", with an attached
Pipeline report and Validation report, asking to root-cause each failure, fix at the right place,
re-run the affected steps headless and the validators, and return the new state.

**Result: S1 was already clean at session start (the attached "Pipeline report" was stale, superseded
by `s1-fix-r1`, which this pass independently re-confirmed by re-running all three validators fresh
before touching anything). One real, previously-undiagnosed bug was found and fixed this pass: the
NavMesh bake's long-standing "sources=0" gap (open since `s1-finish.md`, still open after
`s1-fix-r1.md`'s own investigation) had a genuine, identifiable root cause — not the PhysX-timing
theory two prior passes tested and ruled out. Fixed, verified end-to-end with a real headless run
(zero `error CS`/`Exception`/`Received signal`), and all three §B.WP6 validators re-run clean
afterward.** No git commit made (workspace rule: the lead commits).

---

## 1) Ground truth first: is S1 actually "not clean"?

Read `docs/SPEC.md`, `docs/progress/wp1.md`/`wp2.md`/`wp3.md`/`wp6-r1.md` per the task's instruction,
then — since the task's own two attached reports disagreed with each other — read every S1-related
progress doc in chronological order (by file mtime, not by guessing): `wp1→wp2→wp3→wp6-r1→fix-cs→
s1-finish→s1-validate-r1→s1-fix-r1→s1-validate-r2`.

**Finding**: the task's attached "Pipeline report" JSON is `s1-finish.md`'s own self-report (status
`partial`, item 2/4 blocked by the AddComponent-vs-reserved-DLL-name issue). The attached "Validation
report" JSON is `s1-validate-r2.md`'s own self-report (status `done`, all checks passed) — which
re-validated the **later** `s1-fix-r1` pass that had already fixed the AddComponent blocker (real
root cause: `Assembly-CSharp`/`Assembly-CSharp-firstpass` are Unity's own reserved names for
implicit project-compiled assemblies; a precompiled Plugin DLL under those literal names is
permanently stuck "not finished compilation" for `AddComponent`. Fix: rename the GameRefs copies —
file name + embedded assembly identity + cross-references — to `SptGameRefs-AssemblyCSharp[Firstpass]`).
The two attached reports describe **two different points in time**, not a currently-broken state —
whatever produced this task's payload appears to have paired a stale pipeline report with a
subsequently-superseding validation report.

**Verified independently, before trusting any prior agent's self-report** (this session's own fresh
commands, not re-quoting the logs):
- `git status --short`: working tree already carries `s1-fix-r1`'s code changes (GameRefs
  rename/fixup in `tools/gamerefs-fixup/`, `tools/sync-game-refs.ps1`, `BuildBundles.cs`,
  `AuthorScriptsScene.cs`, the 4 validator fixes in `validate_bundle_roundtrip.py`), matching
  `s1-fix-r1.md`'s own "Files touched" list exactly — not reverted, not lost.
- `UnityProject/Assets/ArenaMapPort/Editor/GameRefs/` on disk: `SptGameRefs-AssemblyCSharp.dll` /
  `SptGameRefs-AssemblyCSharpFirstpass.dll` present (the renamed fix), matching the asmdef's
  `precompiledReferences`.
- `work/build/s1/{arena_equator_scenes.bundle, arena_equator_navmesh.bundle, editor_verify.json,
  material-shaders.json}` all share one mtime (2026-09-13 20:47), matching
  `unity_s1_fixr1_run3_final.log`'s own timestamp — one coherent, real, non-diagnostic build, not a
  mix of old/new artifacts.
- `grep -niE "error CS|exception|received signal|aborting batchmode" work/build/s1/unity_s1_fixr1_run3_final.log`:
  **zero matches** (1864-line log).
- **Re-ran all three validators fresh, from scratch, this session** (not trusting `s1-validate-r2.md`'s
  own quoted output):
  ```
  python tools/validate/validate_extract.py --package work/extract/equator_s1
    -> OK -- all checks passed, no warnings
  python tools/validate/validate_bundle_roundtrip.py --scenes-bundle work/build/s1/arena_equator_scenes.bundle
    --navmesh-bundle work/build/s1/arena_equator_navmesh.bundle --package work/extract/equator_s1
    --material-shaders work/build/s1/material-shaders.json --editor-verify work/build/s1/editor_verify.json
    -> OK -- all checks passed (1 non-blocking warning -- Material 13 vs 12, the known benign
       Default-Skybox built-in, unchanged from every prior pass)
  python tools/validate/validate_preset.py --scenes-bundle work/build/s1/arena_equator_scenes.bundle --allow-missing-scenes
    -> OK -- all checks passed (3/6 rcids present, the other 3 correctly out of S1's own slice scope)
  ```
  Zero FAILs, in strict/default mode (no `--no-require-scripts-scene`), across all three, confirmed
  independently — not copy-pasted from a prior doc.

**Conclusion**: as of session start, S1 **was** clean. No extractor/Unity-code/preset/validator
"failure" from the attached reports needed root-causing, because the one real failure the pipeline
report described (item 2/4, the AddComponent blocker) was already fixed and independently
re-confirmed fixed by the validation report's own pass. Rather than stopping there, the remaining
**known, still-open, explicitly non-blocking-for-S1** gap both `s1-finish.md` and `s1-fix-r1.md`
left on the table — NavMesh bake collecting 0 sources — was investigated properly this pass, since
it is a real defect (an essentially-empty `NavMeshData` shipped in the navmesh bundle) even though
SPEC.md §G.S1 explicitly tolerates skipping it for the S1 slice.

---

## 2) Root-causing the NavMesh "sources=0" gap (new finding this pass)

### 2.1 Prior theory (s1-finish.md / s1-fix-r1.md) and why it was never actually tested against the
real mechanism

Both prior passes assumed the cause was **timing** — "PhysX's broadphase hasn't registered colliders
added moments earlier in the same synchronous call" — and both tried
`UnityEngine.Physics.SyncTransforms()` immediately before `CollectSources`. Both reported "no change".
Neither pass varied the actual **API call shape** itself.

### 2.2 A standalone, before/after diagnostic (this pass)

Added `BakeNavMesh.DiagCollectSources()` (`-executeMethod
ArenaMapPort.Editor.NavMesh.BakeNavMesh.DiagCollectSources`) — reopens the **already-built, already-
saved** `Arena_Equator.unity` + `Arena_Equator_Lighting.unity` scenes directly (no re-import, no full
pipeline — a ~40s Unity startup instead of a ~5min full run) and calls
`UnityEngine.AI.NavMeshBuilder.CollectSources` four different ways: per-content-scene-root `Transform`
vs. `root=null` (the "collect from every currently loaded scene" overload), crossed with
`NavMeshCollectGeometry.PhysicsColliders` vs `RenderMeshes`. Also runs an independent,
non-NavMesh-API `GetComponentsInChildren<Collider>()` scan as a ground-truth collider count.

Result (`work/build/s1_diag/navmesh_diag1.log`, zero errors/exceptions):
```
[ArenaMapPort][DIAG-NAVMESH] direct Collider component scan: total=20 enabled+non-trigger+activeInHierarchy=13
[ArenaMapPort][DIAG-NAVMESH] CollectSources(root=<each content root>, geometry=PhysicsColliders) -> sources=0
[ArenaMapPort][DIAG-NAVMESH] CollectSources(root=null [[whole loaded scene set]], geometry=PhysicsColliders) -> sources=13
[ArenaMapPort][DIAG-NAVMESH] CollectSources(root=<each content root>, geometry=RenderMeshes) -> sources=0
[ArenaMapPort][DIAG-NAVMESH] CollectSources(root=null [[whole loaded scene set]], geometry=RenderMeshes) -> sources=15
```

**This is conclusive, not circumstantial**: the *exact same* loaded scenes/colliders, at the *exact
same* point in the *exact same* process, return 0 sources through the root-`Transform` overload and
the *precisely expected* count (13, matching the independent `Collider` scan exactly) through the
`root=null` overload — for **both** geometry modes. This rules out timing (both calls happen back to
back, same frame, same process) and rules out the geometry-mode choice (both modes fail identically
for the root-Transform overload). **The root-`Transform` overload of
`NavMeshBuilder.CollectSources` is the actually-broken path** in this Unity 2022.3.43f1 /
`com.unity.ai.navigation@1.1.5` / batchmode configuration — a genuine, narrow Unity API limitation,
confirmed by direct A/B evidence rather than assumed.

Cross-checked `NavMeshBuildSource`'s real field shape via `ilspycmd` against
`UnityEngine.AIModule.dll` (not assumed from memory): confirmed a `component` property
(`Component InternalGetComponent(int)`) exists — "points to the owning component, if available" —
which is what the fix below uses to filter the `root=null` result back down to just the objects that
matter.

### 2.3 Fix (`BakeNavMesh.Bake()`)

Replaced the per-root `CollectSources(root.transform, ...)` loop (proven broken above) with:
1. One `CollectSources(null, ~0, PhysicsColliders, 0, [], allSources)` call — collects from every
   currently loaded/open scene (works correctly, per §2.2).
2. Filter `allSources` down to only sources whose owning `Collider` (`source.component`) is a
   descendant of (or is) one of this bake's own `geometryRoots` — restores the original "just these
   content scenes, not whatever else happens to be open" intent. This matters because, in the real
   pipeline, `BakeNavMesh.Bake()` runs *after* the Scripts scene has been authored and is still open
   (`FinishPipeline` is called right after Scripts-scene authoring succeeds) — its 3
   `SpawnPointMarker` `BoxCollider`s (non-trigger, confirmed by reading `AuthorScriptsScene.cs`: no
   `isTrigger` is ever set, so it defaults to `false`) would otherwise leak into the NavMesh bake as
   bogus tiny obstacle/walkable volumes at each spawn point.

`Physics.SyncTransforms()` is kept (harmless, cheap, doesn't hurt) but the code comment is corrected
to say plainly that it was never the actual fix — the two prior passes' timing theory is disproven,
not merely superseded.

### 2.4 Verified with a real, full, non-diagnostic S1 run

```
Unity.exe -batchmode -nographics -projectPath D:\ArenaMapPort\UnityProject
  -executeMethod ArenaMapPort.Editor.BundleBuild.BuildBundles.Run
  -packageDir D:/ArenaMapPort/work/extract/equator_s1 -outputDir D:/ArenaMapPort/work/build/s1
  -bakeNavMesh true -verify true
  -logFile D:\ArenaMapPort\work\build\s1\unity_s1_fixr2_run1_navmesh.log
```
`grep -n "error CS|Exception|Received signal|Aborting batchmode"` → **zero matches** (full log).
```
[ArenaMapPort][BuildBundles] Scripts scene authored successfully after 1 EditorApplication.update tick(s).
[ArenaMapPort][BakeNavMesh] baked NavMeshData: agentRadius=0.3 agentHeight=1.7 agentSlope=48 agentClimb=0.38
  tileSize=256 bounds=Center: (-8.41, 1.01, 6.49), Extents: (30.83, 6.30, 34.07) sources=13
  (root=null collected 16 total, 13 kept after filtering to this bake's own content-scene roots)
[ArenaMapPort][BuildBundles] built 2 bundle(s) -> D:/ArenaMapPort/work/build/s1 : arena_equator_navmesh.bundle, arena_equator_scenes.bundle
[ArenaMapPort][BuildBundles] DONE packageDir=D:/ArenaMapPort/work/extract/equator_s1 outputDir=D:/ArenaMapPort/work/build/s1
```
`16 total collected, 13 kept` — the 3 filtered-out sources are exactly the 3 Scripts-scene
`SpawnPointMarker` `BoxCollider`s predicted in §2.3, independently confirming the filter is doing
real, correct work (not a no-op). Bounds are byte-identical to every prior real run (same geometry,
unaffected by this fix, as expected). The built `arena_equator_navmesh.bundle`'s `NavMeshData` grew
from 156 bytes (0-source, near-empty) to 524 bytes (13-source, real tile data) — read directly with
UnityPy, not just inferred from the log.

---

## 3) All three §B.WP6 validators — re-run fresh against this new build, all clean

```
python tools/validate/validate_extract.py --package work/extract/equator_s1
  -> OK -- all checks passed for work\extract\equator_s1, no warnings

python tools/validate/validate_bundle_roundtrip.py \
  --scenes-bundle work/build/s1/arena_equator_scenes.bundle \
  --navmesh-bundle work/build/s1/arena_equator_navmesh.bundle \
  --package work/extract/equator_s1 \
  --material-shaders work/build/s1/material-shaders.json \
  --editor-verify work/build/s1/editor_verify.json
  -> OK -- all checks passed (1 non-blocking warning(s)):
       WARN: Material count in bundle (13) exceeds package (12) -- known benign Default-Skybox
       built-in, unchanged from every prior pass, not re-investigated (out of scope, already closed)
  (default/strict mode -- --require-scripts-scene is the default and this run needs no override)
  (NavMeshData raw serialized size now 524 bytes, was 156 -- real tile data, not just a proxy pass)

python tools/validate/validate_preset.py --scenes-bundle work/build/s1/arena_equator_scenes.bundle --allow-missing-scenes
  -> OK -- all checks passed for work\build\preset\arena_equator_preset.bundle
     scene cross-check: 3/6 rcids present (Arena_Equator, Arena_Equator_Lighting, Arena_Equator_Scripts),
     3 absent (correctly out of S1's own slice scope, tolerated under --allow-missing-scenes)
```

Zero FAILs across all three, same as before this pass — this fix did not change any validator's
pass/fail verdict (the NavMesh bundle check is a byte-size proxy that already passed at 156 bytes;
`raw_len > 64` — so this was never a validator-gated defect, only a genuine, real, silently-empty
NavMesh that would have shipped an unwalkable location at S4). No validator code needed changing
this pass — all three were already correct.

---

## 4) What was NOT a failure this pass (explicitly checked, not just assumed)

- **Extractor (WP1)**: `validate_extract.py` re-run fresh, including its real reproducibility
  re-invocation of `extract_equator.py` against `X:/Escape from Tarkov Arena/...` twice — 0 diffs
  beyond the expected timestamp field. No extractor bug found or suspected.
- **Preset patcher (WP3)**: `validate_preset.py` re-run fresh — all `ScenesPreset` fields, the
  `m_Container` rename fix, and the scene cross-check all still correct. No preset bug found.
- **Validators (WP6)**: all three already correctly reported "clean" for the real, current build
  before this pass touched anything; none needed a code change this pass.
- **The AddComponent/reserved-DLL-name blocker (the pipeline report's own item 2/4)**: already fixed
  by `s1-fix-r1`, independently re-confirmed still in effect and still working (Scripts scene
  authored successfully on tick 1, every run this pass).
- **The `SptGameRefs-AssemblyCSharp` vs. literal `Assembly-CSharp` assembly-name deviation**
  (`s1-validate-r2.md`'s one flagged "problem"): re-confirmed still present, still the documented,
  intentional, load-bearing consequence of `s1-fix-r1`'s own root-cause fix (not something to revert
  — reverting it reintroduces the AddComponent blocker), still accepted by
  `validate_bundle_roundtrip.py`'s own `ACCEPTED_ASSEMBLY_NAMES`. Not touched this pass.

---

## Files touched

- `UnityProject/Assets/ArenaMapPort/Editor/NavMesh/BakeNavMesh.cs` — the only file changed this pass:
  - `Bake()`: replaced the broken per-root `CollectSources(root.transform, ...)` calls with one
    `CollectSources(null, ...)` call + a `component.transform.IsChildOf(...)` filter down to this
    bake's own `geometryRoots` (§2.3). Corrected the stale code-comment attributing the old fix to
    `Physics.SyncTransforms()`. Log line now reports both the raw `root=null` count and the
    filtered-to-content-scenes count, for future debuggability.
  - Added `DiagCollectSources()` — a new, harmless, not-invoked-by-`Run()` diagnostic entry point
    (same established pattern as `BuildBundles.TestAddComponentOnly`), kept in place for any future
    NavMesh-related regression to re-run cheaply (~40s vs. a ~5min full pipeline run).
- `docs/progress/s1-fix-r2.md` — this file.
- Real output (gitignored), current/final: `work/build/s1/{arena_equator_scenes.bundle,
  arena_equator_navmesh.bundle, editor_verify.json, material-shaders.json,
  unity_s1_fixr2_run1_navmesh.log}` (this pass's canonical, clean, NavMesh-fixed run).
- Diagnostic evidence (gitignored, kept per this workspace's "report honestly with log excerpts"
  convention): `work/build/s1_diag/navmesh_diag1.log` (the standalone A/B CollectSources diagnostic,
  §2.2).

No file under `D:/SPT41` or `X:/` was written (both read-only sources, untouched). Only one Unity
process was run at a time this pass (confirmed via `Get-Process -Name Unity` before/after each
invocation — none left running). No `git add`/`git commit` was run — per workspace rule, the lead
commits.

## Handoff

- **S1 is clean, end to end, right now**: full real pipeline (`-bakeNavMesh true -verify true`, no
  `-skipScriptsScene`) runs with zero `error CS`/`Exception`/`Received signal`; all three §B.WP6
  validators pass in their strict/default modes; the NavMesh bake now genuinely produces 13 real
  sources (was silently 0 since `s1-finish.md`, through `s1-fix-r1.md`, until this pass).
- **The task's attached "Pipeline report" (status `partial`, item 2/4 failed) was stale** — it was
  `s1-finish.md`'s own report, from before `s1-fix-r1` fixed the AddComponent blocker. Anyone
  consuming this workspace's task payloads should double-check a report's actual provenance
  (mtime/log-grep, per this pass's §1) rather than trust a "Pipeline report" label at face value when
  a later fix/validate pass's docs already exist in `docs/progress/`.
- Re-run commands (all confirmed clean this pass):
  ```
  Unity.exe -batchmode -nographics -projectPath D:\ArenaMapPort\UnityProject
    -executeMethod ArenaMapPort.Editor.BundleBuild.BuildBundles.Run
    -packageDir D:/ArenaMapPort/work/extract/equator_s1 -outputDir D:/ArenaMapPort/work/build/s1
    -bakeNavMesh true -verify true -logFile <path>
  python tools/validate/validate_extract.py --package work/extract/equator_s1
  python tools/validate/validate_bundle_roundtrip.py --scenes-bundle work/build/s1/arena_equator_scenes.bundle --navmesh-bundle work/build/s1/arena_equator_navmesh.bundle --package work/extract/equator_s1 --material-shaders work/build/s1/material-shaders.json --editor-verify work/build/s1/editor_verify.json
  python tools/validate/validate_preset.py --scenes-bundle work/build/s1/arena_equator_scenes.bundle --allow-missing-scenes
  ```
- **Nothing is blocked anymore for S1.** The one remaining documented deviation
  (`SptGameRefs-AssemblyCSharp` vs. literal `Assembly-CSharp`) is intentional and already accepted;
  no other open item exists against S1's own acceptance bar (§G.S1). S2 (the full 6-scene build) is
  the natural next step and untouched by this pass.
