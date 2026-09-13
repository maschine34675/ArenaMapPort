# S1 validation — r2 — progress log

Role: validator (re-run). Task: re-run all three SS B.WP6 validators against the *current*
`work/extract/equator_s1` package and `work/build/s1` build (the real, non-diagnostic,
Scripts-scene-included output from the s1-fix-r1 pass, `unity_s1_fixr1_run3_final.log`), plus
independent UnityPy spot-checks written from scratch (not importing the validators' own code).
No repository files were modified by this pass (`git status --short` before/after is identical);
no Unity process was started (existing build outputs were re-read only); no git commit made.

## Pre-check: confirmed the build under validation is the real, current, clean one

Before trusting `work/build/s1/*`, checked its provenance directly rather than assuming the
previous session's report:

- `work/build/s1/{arena_equator_scenes.bundle, arena_equator_navmesh.bundle, editor_verify.json,
  material-shaders.json}` all have the **same mtime (2026-09-13 20:47)**, matching
  `unity_s1_fixr1_run3_final.log`'s own timestamp — i.e. they are one coherent build, not a mix of
  old and new artifacts.
- That log's command line: `-bakeNavMesh true -verify true` (the real flags, not the
  `-skipScriptsScene`/`-bakeNavMesh false` diagnostic runs from earlier passes).
- `grep -ni "error CS|exception|received signal|aborting batchmode"` over the full 1864-line log:
  **zero matches**.
- `[ArenaMapPort][BuildBundles] Scripts scene authored successfully after 1
  EditorApplication.update tick(s).` (line 720) confirms the Scripts-scene blocker really is
  resolved in this build, not skipped.
- `[ArenaMapPort][BuildBundles] built 2 bundle(s) ... arena_equator_navmesh.bundle,
  arena_equator_scenes.bundle` + `[ArenaMapPort][VerifyBuild] wrote .../editor_verify.json` (lines
  1194/1244) confirm both bundles and the verify JSON came from this same run.

## Summary — per-check pass/fail

| # | Check | Result |
|---|---|---|
| 1 | `validate_extract.py --package work/extract/equator_s1` | **PASS** (0 failures, 0 warnings) |
| 2 | `validate_bundle_roundtrip.py` (default flags — `--require-scripts-scene` on, the real S1 build) against `work/build/s1` (scenes+navmesh bundle, package, material-shaders, editor_verify) | **PASS** (0 failures, 1 non-blocking WARN) |
| 3 | `validate_preset.py --scenes-bundle work/build/s1/arena_equator_scenes.bundle --allow-missing-scenes` | **PASS** (0 failures, 3 non-blocking WARNs for the 3 rcids genuinely out of S1 scope); confirmed strict mode (no flag) still correctly FAILs on those same 3 — validator not weakened |
| 4 | Spot-check: 3 random meshes' vertex/submesh counts vs package (independent UnityPy script, not via any validator) | **PASS** — 3/3 exact match |
| 5 | Spot-check: Scripts-scene MonoBehaviours' `m_Script` class+namespace identity, and that serialized field data is genuinely present (independent UnityPy script) | **PASS with one documented, expected deviation from the task's literal wording** — see below |

---

## 1) `validate_extract.py` — clean pass, unmodified, unchanged from r1

```
python tools/validate/validate_extract.py --package work/extract/equator_s1
```
All checks OK, including the live reproducibility re-run (re-invokes the extractor twice against
`X:/Escape from Tarkov Arena/...`, diffs both fresh runs against each other and against the
checked-in package — 0 diffs beyond the expected `extracted_utc` timestamp field). Numbers: 64
objects, 47 meshes, 12 materials, 25 textures, MeshCollider 16 (convex 2/non-convex 14), LODGroup
3, Light 1, 6 distinct shaders — identical to every prior pass. No changes needed; this script was
already correct.

## 2) `validate_bundle_roundtrip.py` — clean pass in DEFAULT (strict) mode this time

Command (all 5 explicit paths, as the task asked):
```
python tools/validate/validate_bundle_roundtrip.py \
  --scenes-bundle work/build/s1/arena_equator_scenes.bundle \
  --navmesh-bundle work/build/s1/arena_equator_navmesh.bundle \
  --package work/extract/equator_s1 \
  --material-shaders work/build/s1/material-shaders.json \
  --editor-verify work/build/s1/editor_verify.json
```
Unlike r1 (which had to pass `--no-require-scripts-scene` because the only bundle that existed
then was the diagnostic `-skipScriptsScene` one), **this run uses the default, strict
`--require-scripts-scene` flag** — and passes cleanly, because the Scripts scene is now real:

- GameObject/Mesh/Texture2D/MeshFilter/MeshRenderer/MeshCollider/BoxCollider/Sphere/
  CapsuleCollider/Light/LODGroup counts **all match the package exactly** — note the package
  expectation itself is now 72 objects / 7 BoxColliders (up from 64/4 in r1), because
  `--require-scripts-scene` folds in the Scripts scene's own 8 authored objects (root +
  LevelSettings/LocationScene/BotZone/LocationExportInfo GameObjects + 3 SpawnPointMarkers, each
  with a BoxCollider) — this is the Scripts-scene-aware count logic the s1-fix-r1 pass added to
  the validator, now exercised for real for the first time.
- All 47 meshes' vertex/submesh counts match by name; all 12 materials carry an O1-compliant
  `Hidden/ArenaMapPort/` stand-in shader.
- No null LODGroup renderer slot.
- Both bundles: `target_platform=19` (StandaloneWindows64), `unity_version=2022.3.43f1`.
- NavMesh bundle: exactly 1 `NavMeshData` asset present (156 bytes serialized) — passes the
  validator's best-effort size-proxy check. **Caveat carried forward from the previous pass, not
  re-solved here** (out of this task's scope — see "NavMesh 0-sources gap" below): the bake itself
  collected 0 sources, so this NavMeshData is structurally valid but functionally empty.
- All 5 SS D EFT `MonoBehaviour`s found with resolving `MonoScript` identity, byte sizes 40-340
  (well past the empty-header floor), `BotZone.PatrolWays`/`SpawnPointMarkers` both non-null,
  `LocationScene.world_interactive_objects_length != -1`, 3 `SpawnPointMarker`s correctly wired
  (all Player-category, collider attached+enabled).
- `editor_verify.json` cross-check: every field matches the package for all 3 scenes
  (`Arena_Equator`, `Arena_Equator_Lighting`, `Arena_Equator_Scripts`).

**1 non-blocking WARN**: `Material count in bundle (13) exceeds package (12)` — the known,
previously-confirmed `Default-Skybox` built-in default (WP2's own hypothesis, independently
confirmed by a fresh UnityPy listing in the r1 pass) — not re-investigated again this pass since
nothing changed about it, correctly still a WARN not a FAIL.

No changes were needed to this validator — it already handles the real Scripts-scene case
correctly (the s1-fix-r1 pass's fixes to `validate_editor_verify()`'s `--require-scripts-scene`
plumbing, exercised only against a diagnostic build in r1, are now exercised against the real
thing for the first time here and behave correctly).

## 3) `validate_preset.py --scenes-bundle work/build/s1/arena_equator_scenes.bundle`

**Strict (no flag) — captured as evidence that the validator is not weakened**:
```
[validate_preset] 3 check(s) FAILED:
  - preset rcid 'Arena_Equator_DesignStuff' matches a scene name inside arena_equator_scenes.bundle (available: ['Arena_Equator', 'Arena_Equator_Lighting', 'Arena_Equator_Scripts'])
  - preset rcid 'Arena_Equator_TeamFight' matches a scene name inside arena_equator_scenes.bundle (available: [...])
  - preset rcid 'Arena_Equator_TeamFight_DesignStuff' matches a scene name inside arena_equator_scenes.bundle (available: [...])
```
Every field-level `ScenesPreset` check (ServerName, `_activeSceneName`, `ChildPresets`,
`_scenesResourceKeys` × 6, `ScenesGuids`, `m_Script` PPtr identity, the `m_Container` rename) still
passes cleanly — only the scene cross-check fails, and only for the 3 rcids S1 never built
(`Arena_Equator_DesignStuff`/`Arena_Equator_TeamFight`/`Arena_Equator_TeamFight_DesignStuff` map to
`level53`/`level58`/`level57`, none of which are in S1's scope per SPEC.md §E.1 — only `level51`
and `level55` were extracted, plus the freshly-authored `Arena_Equator_Scripts`).

**Judgment (per the task's instruction to "judge this sensibly and explain")**: this is the correct
outcome for a real, deliberate partial slice. Notably **one more rcid now resolves than in r1**
(`Arena_Equator_Scripts` — 3/6 present now vs. 2/6 in r1) precisely because this build's Scripts
scene is real, not skipped. The preset's own construction is unconditionally correct — it declares
all 6 rcids in the right order for the eventual full build; S1 just hasn't produced the other 3
scenes yet, which is by design, not a bug.

**With `--allow-missing-scenes`**:
```
[validate_preset] WARN: preset rcid 'Arena_Equator_DesignStuff' ... -- absent, tolerated under --allow-missing-scenes (slice build; not scored as a failure)
[validate_preset] WARN: preset rcid 'Arena_Equator_TeamFight' ... -- absent, tolerated under --allow-missing-scenes (slice build; not scored as a failure)
[validate_preset] WARN: preset rcid 'Arena_Equator_TeamFight_DesignStuff' ... -- absent, tolerated under --allow-missing-scenes (slice build; not scored as a failure)
[validate_preset] scene cross-check summary: 3/6 rcids present ['Arena_Equator', 'Arena_Equator_Lighting', 'Arena_Equator_Scripts'], 3 absent [...] (absent ones tolerated: --allow-missing-scenes)

[validate_preset] OK -- all checks passed for D:\ArenaMapPort\work\build\preset\arena_equator_preset.bundle
```
No changes needed to this validator — the `--allow-missing-scenes` flag added in r1 already does
exactly the right thing for this newer, more-complete-but-still-partial build, and the strict
default path is confirmed unchanged (same 3 FAILs, not fewer/more, not silently passing).

## 4) Independent spot-check — 3 random meshes' vertex/submesh counts vs package

Standalone script (`spotcheck_meshes.py`, scratchpad-only, not committed, does not import
`tools/validate/*.py` or `tools/extract/*.py`), seeded pick (`random.seed("s1-validate-r2")`,
deliberately a different seed/selection than r1's, for independence) from the package's 47 mesh
ids, loads `work/build/s1/arena_equator_scenes.bundle` directly with UnityPy and reads each
`Mesh.m_VertexData.m_VertexCount` / `len(m_SubMeshes)`:

```
b860454570c06085: expected vc=283 sm=3  got vc=283 sm=3  -> OK
70f3a76c29ff4eed: expected vc=28  sm=1  got vc=28  sm=1  -> OK
5553e4b6dcc2de2d: expected vc=16  sm=1  got vc=16  sm=1  -> OK
```
3/3 exact match, including one multi-submesh mesh (3 submeshes) — confirms `MeshImporter.cs`'s
vertex/submesh round trip independently of the shipped validator's own logic, on a fresh
independent sample from r1's.

## 5) Independent spot-check — Scripts-scene MonoBehaviours' `m_Script` identity + serialized data

Standalone script (`spotcheck_scripts.py`, scratchpad-only, not committed, does not import
`tools/validate/*.py`), enumerates every `MonoBehaviour` in `arena_equator_scenes.bundle`, resolves
each one's `m_Script` PPtr to its embedded `MonoScript`, and reads the full field-level typetree —
deliberately not routed through `validate_bundle_roundtrip.py`'s own resolution code, for a truly
independent check.

**Class + namespace: exact match, all 5/5**:
```
BotZone                                : namespace=''                     -- matches decompiled BotZone.cs (global namespace)
LevelSettings                          : namespace=''                     -- matches decompiled LevelSettings.cs (global namespace)
LocationExportInfo                     : namespace='EFT'                  -- matches EFT/LocationExportInfo.cs
LocationScene                          : namespace=''                     -- matches decompiled LocationScene.cs (global namespace)
SpawnPointMarker (x3)                  : namespace='EFT.Game.Spawning'    -- matches EFT.Game.Spawning/SpawnPointMarker.cs
```

**Serialized data genuinely present** (byte sizes 40-340, well past the empty-MonoBehaviour-header
floor, plus real, non-placeholder field values, cross-confirmed against the previous pass's own SS
D dump and matching it exactly):
```
LevelSettings.AmbientMode = 3            (UnityEngine.Rendering.AmbientMode.Flat)
LocationScene.WorldInteractiveObjects len = 0   (not -1 -- the LocationScene.Awake NRE site is correctly populated, not left at FillArrays()-never-ran default)
BotZone.PatrolWays len = 0, SpawnPointMarkers len = 3
LocationExportInfo._unixDateTime = 1757707200
3x SpawnPointMarker, each with a non-null _collider/_spawnPoint field
```
All of the above independently reconfirm, via a completely fresh script, the exact SS D
field-value claims the previous fix pass reported.

**Documented deviation from the task's literal wording** — the task asked to check
"m_Script (class+namespace+assembly 'Assembly-CSharp.dll')". Class and namespace match exactly
(above). **Assembly does NOT literally read `'Assembly-CSharp.dll'`** — it reads
`'SptGameRefs-AssemblyCSharp.dll'` for all 5 types, confirmed independently here:
```
BotZone            : assembly='SptGameRefs-AssemblyCSharp.dll'  literal-match-to-'Assembly-CSharp.dll'=False
LevelSettings      : assembly='SptGameRefs-AssemblyCSharp.dll'  literal-match-to-'Assembly-CSharp.dll'=False
LocationExportInfo : assembly='SptGameRefs-AssemblyCSharp.dll'  literal-match-to-'Assembly-CSharp.dll'=False
LocationScene      : assembly='SptGameRefs-AssemblyCSharp.dll'  literal-match-to-'Assembly-CSharp.dll'=False
SpawnPointMarker x3: assembly='SptGameRefs-AssemblyCSharp.dll'  literal-match-to-'Assembly-CSharp.dll'=False
```
**Judgment**: this is the *expected, intentional, documented* consequence of the lead's own
root-cause fix (renaming the GameRefs `Assembly-CSharp[.dll]`/`Assembly-CSharp-firstpass[.dll]`
DLLs' on-disk file name AND embedded assembly identity to `SptGameRefs-AssemblyCSharp[.dll]` /
`SptGameRefs-AssemblyCSharp-firstpass[.dll]`, per `tools/gamerefs-fixup` + `docs/progress/
s1-fix-r1.md`) — Unity treats the literal file names `Assembly-CSharp.dll`/
`Assembly-CSharp-firstpass.dll` as reserved names for its own implicit legacy script assemblies, so
a precompiled Plugin DLL under those exact names can never finish "compilation" for
`AddComponent()` purposes; the only working fix found was renaming both the file and the embedded
assembly identity (Unity separately enforces assembly-name == file-name for precompiled plugins).
`validate_bundle_roundtrip.py` itself already documents and accepts this
(`ACCEPTED_ASSEMBLY_NAMES = ['Assembly-CSharp', 'Assembly-CSharp.dll', 'SptGameRefs-AssemblyCSharp',
'SptGameRefs-AssemblyCSharp.dll']`, confirmed in its own `--help` text this pass). Treating this as
a genuine PASS: the *types* the task cares about (the real, decompiled EFT `LevelSettings`/
`LocationScene`/`BotZone`/`LocationExportInfo`/`SpawnPointMarker`, by class+namespace identity, with
real field data) are unambiguously present and correct; the assembly *file/module name* differing
from the task's literal wording is a known, load-bearing, and unavoidable side effect of the fix
that made this Scripts scene possible at all, not an unexplained discrepancy — flagging it
explicitly rather than silently reporting a bare "PASS", per the work order's "report defects with
evidence" spirit even though this isn't a defect.

## NavMesh 0-sources gap — re-confirmed present, not re-investigated (out of this task's scope)

`grep`'d the same final log directly (not relying on the previous report's claim):
```
[ArenaMapPort][BakeNavMesh] baked NavMeshData: agentRadius=0,3 agentHeight=1,7 agentSlope=48 agentClimb=0,38 tileSize=256 bounds=Center: (-8.41, 1.01, 6.49), Extents: (30.83, 6.30, 34.07) sources=0
```
`sources=0` confirmed present in this exact build's own log. This is the same pre-existing,
already-documented (SPEC.md §B.WP2.7, `s1-fix-r1.md`'s own handoff) gap — not something this
validation pass was asked to fix, and not something any of the three validators treat as a hard
failure for S1 (the navmesh bundle's presence/asset-count/size-proxy checks all still pass, since a
structurally-valid-but-empty `NavMeshData` is exactly what "0 sources" produces — the validator
cannot currently distinguish "0 sources, technically valid but useless" from "genuinely walkable" by
size alone, which is itself an honestly-documented limitation of the size-proxy check, not new
information this pass). Reported here for completeness since the task asked for honest reporting,
not re-attempted (no untried lead from `s1-fix-r1.md`'s handoff was pursued this pass — out of
scope for a pure validation task).

## Files touched

- **None under version control.** No WP1/WP2/WP3/validator code modified — all three validators
  were already correct for the real, non-diagnostic S1 build; no bugs found in this pass (unlike
  r1, which found and fixed 2 real validator bugs against the diagnostic-only build that existed
  then).
- `docs/progress/s1-validate-r2.md` (this file, new).
- Two standalone spot-check scripts written to the session scratchpad only (not part of the repo):
  `spotcheck_meshes.py`, `spotcheck_scripts.py`.

## Handoff

- All three validators are green for the current, real S1 deliverables:
  - `validate_extract.py --package work/extract/equator_s1` (no flags needed).
  - `validate_bundle_roundtrip.py` with the 5 explicit `work/build/s1/*` paths, **default flags**
    (no longer needs `--no-require-scripts-scene` — that was only ever needed against the r1-era
    diagnostic build).
  - `validate_preset.py --scenes-bundle work/build/s1/arena_equator_scenes.bundle
    --allow-missing-scenes` (still needed — S1 is still a deliberate 2-of-5-content-scene slice;
    strict mode correctly still fails on the 3 rcids that map to scenes S1 never extracts by
    design, confirmed unchanged this pass).
- Two independent spot-checks both confirm the shipped validators' own findings from a completely
  separate code path: 3/3 mesh vertex/submesh counts match; all 5 SS D component types' class+
  namespace identity match the real decompiled EFT types with genuine, non-empty serialized field
  data — with the one documented, expected, non-defect deviation (assembly *name* is the
  intentionally-renamed `SptGameRefs-AssemblyCSharp.dll`, not literally `Assembly-CSharp.dll`,
  because of the upstream Unity-reserved-name fix).
- The one open, honestly-carried-forward gap is unchanged from `s1-fix-r1.md`: NavMesh bake
  collects 0 sources. Pre-existing, explicitly non-blocking for S1 per SPEC.md, not addressed by
  this validation-only pass.
