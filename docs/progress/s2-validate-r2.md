# s2-validate-r2 — strict validation of the S2 full build (post s2-fix-r1) — progress log

Role: validator (r2). Task: strict validation of the S2 full build against `work/build/full`
(scenes+navmesh bundles) and `work/extract/equator`, using the exact three validators + explicit
flags the task specified, plus independent from-scratch UnityPy spot-checks. No repository files
were modified by this pass (`git status --short` before/after: 57 lines both times, all
pre-existing from `s2-build`/`s2-fix-r1`, none new); no Unity process was started (existing build
outputs were re-read only); no git commit made.

## Summary — per-check result

| # | Check | Result |
|---|---|---|
| 1 | `validate_extract.py --package work/extract/equator` (full, incl. reproducibility+freshness) | **PASS** — 0 failures, 2 pre-existing non-blocking WARNs |
| 2 | `validate_bundle_roundtrip.py` (`--require-scripts-scene --require-navmesh`, all 5 explicit paths) against `work/build/full` | **PASS** — 0 failures, 1 pre-existing non-blocking WARN |
| 3 | `validate_preset.py --scenes-bundle work/build/full/arena_equator_scenes.bundle` **strict, no `--allow-missing-scenes`** | **PASS** — 0 failures, 6/6 rcids present |
| 4 | Independent spot-check: 5 random meshes vs package (fresh UnityPy script) | **PASS** — 5/5 exact match, incl. one 5-submesh mesh |
| 5 | Independent spot-check: every `MonoScript.m_AssemblyName` in **both** bundles | **PASS** — scenes bundle: 5 MonoScripts, all `'Assembly-CSharp'`; navmesh bundle: 0 MonoScripts; no `SptGameRefs-*` anywhere |
| 6 | Independent spot-check: Scripts-scene component field values vs SPEC §D (fresh `read_typetree()`) | **PASS** — every §D field matches |
| 7 | Independent spot-check: NavMeshData present, non-trivial | **PASS** — 1 asset, `m_NavMeshTiles` len=22, raw size 370,556 bytes |

**Bottom line: S2 is clean.** Both real bugs `s2-fix-r1.md` fixed (the extractor's silent
spawn-anchor data loss, and `validate_bundle_roundtrip.py`'s SkinnedMeshRenderer-blind count
checks) are confirmed fixed and holding, independently re-verified this pass with fresh commands
and fresh, from-scratch scripts — not by re-reading that report.

---

## Ground truth checked before trusting the previous report

- `git status --short`: 57 lines, unchanged from what `s2-fix-r1.md`'s "Files touched" section
  describes (the carried-forward, pre-existing modified/untracked files under
  `UnityProject/Assets/ArenaMapPort/Editor/**`, `tools/**` — none new this pass).
- `work/build/full/*` mtimes (scenes/navmesh/editor_verify/material-shaders bundles, ~22:41-22:52
  same session) predate `work/extract/equator/manifest.json`'s mtime (00:00) by design — the
  package was re-extracted in place by `s2-fix-r1` *after* the bundles were built, and that pass's
  own `validate_extract.py` freshness/reproducibility check already proved the re-extraction is
  byte-identical (0 diffs) to what the bundles were built from. Independently re-confirmed the
  same thing fresh this pass (below), not just trusted.
- `tools/validate/validate_bundle_roundtrip.py`'s `ACCEPTED_ASSEMBLY_NAMES` set (grep'd directly):
  `{"Assembly-CSharp", "Assembly-CSharp-firstpass"}` — strict, no `SptGameRefs-*` variants
  accepted, matching this task's own instruction. Not modified this pass (nothing needed fixing).

## 1) `validate_extract.py --package work/extract/equator` (full strict)

```
python tools/validate/validate_extract.py --package work/extract/equator
```
Ran to completion (~7 min — two internal re-extractions + diffs). Result: **0 failures.**
```
ok : two independent fresh extractor runs ... produce a byte-identical package -- 0 diff(s): []
ok : the checked-in --package (work\extract\equator) matches what the current extractor actually
     produces from today's source data (package-freshness check) -- 0 diff(s): []
OK -- all checks passed for work\extract\equator (2 non-blocking warning(s)):
  WARN: manifest.json.warnings is non-empty (201) -- the 200 benign "MeshFilter: no mesh
        assigned" + 1 informational Texture3D-flatten note, unchanged since s2-build
  WARN: 21 mesh id(s) never referenced by any scene's MeshFilter/MeshCollider -- the same,
        already-explained SkinnedMeshRenderer-only set (s2-build/s2-fix-r1)
```
Summary: 5 scenes, 39,398 objects, 2,605 meshes, 332 materials, 708 textures — identical to every
prior S2 pass. The reproducibility+freshness FAIL `s2-validate-r1.md` found (stale `common.BASE`
vs. the real `--source`) is gone — confirmed via a real fresh re-run this pass, not a stale claim.

## 2) `validate_bundle_roundtrip.py` — strict, both bundles, full 5-path invocation

```
python tools/validate/validate_bundle_roundtrip.py \
  --scenes-bundle work/build/full/arena_equator_scenes.bundle \
  --navmesh-bundle work/build/full/arena_equator_navmesh.bundle \
  --package work/extract/equator \
  --material-shaders work/build/full/material-shaders.json \
  --editor-verify work/build/full/editor_verify.json \
  --require-scripts-scene --require-navmesh
```
Result: **0 failures.** The 3 hard FAILs from `s2-build`/`s2-validate-r1` (Mesh 2584/2605,
Texture2D 648/708, Material 313/332) are gone — the validator's own new in-scope accounting now
reports:
```
package totals vs in-scope (non-skinned) totals: meshes 2605 vs 2584 (21 excluded, skip_import-
  only), materials 332 vs 312 (20 excluded), textures 708 vs 648 (60 excluded)
ok : Mesh count in bundle (2584) == package (2584)
ok : Texture2D count in bundle (648) == package (648)
```
Every other check passes too: all 9 component-type counts (GameObject 39406, MeshFilter 16407,
MeshRenderer 14957, MeshCollider 5428, BoxCollider 842, SphereCollider 130, CapsuleCollider 910,
Light 477, LODGroup 3601) match the package exactly; convex/non-convex MeshCollider split
(849/4579) matches; all 2584 meshes' vertex/submesh counts match; all 312 in-scope materials
carry an O1-compliant `Hidden/ArenaMapPort/` stand-in shader; no null LODGroup slot; both bundles
`StandaloneWindows64`/`2022.3.43f1` on every SerializedFile; all 5 §D `MonoBehaviour`s found with
correct namespace/assembly identity and byte sizes well past the empty-header floor;
`BotZone.PatrolWays`/`SpawnPointMarkers` both non-null and correctly cross-wired (risk #11);
NavMesh bundle: exactly 1 `NavMeshData`, 370,556 bytes raw; `editor_verify.json` cross-check clean
for all 6 scenes. **1 non-blocking WARN** (unchanged since S1): `Material count in bundle (313)
exceeds package (312)` — the known, previously-confirmed benign `Default-Skybox` built-in.

## 3) `validate_preset.py` — strict, all 6 scenes required

```
python tools/validate/validate_preset.py --scenes-bundle work/build/full/arena_equator_scenes.bundle
```
(no `--allow-missing-scenes`). Result: **0 failures.** Every `ScenesPreset` field
(`ServerName`, `_activeSceneName`, `ChildPresets`, `_scenesResourceKeys` ×6 in the exact §B.WP3
order, `ScenesGuids`, `m_Script` PPtr identity → `EFT.ScenesPreset`/`Assembly-CSharp`,
`m_Container` key ending `arena_equator.scenespreset.asset`) passes, and the scene cross-check
resolves **6/6 rcids** against the real scenes bundle (`Arena_Equator`, `Arena_Equator_DesignStuff`,
`Arena_Equator_Lighting`, `Arena_Equator_Scripts`, `Arena_Equator_TeamFight`,
`Arena_Equator_TeamFight_DesignStuff`) — no `--allow-missing-scenes` needed, unlike every S1-era
run.

## 4) Independent spot-check — 5 random meshes vs package

Fresh, standalone script (scratchpad-only, does not import `tools/validate/*.py` or
`tools/extract/*.py`), `random.seed("s2-validate-r2-independent")` (deliberately different from
every prior pass's seed) over the package's 2,605 mesh ids, read directly from
`arena_equator_scenes.bundle` via UnityPy:
```
6d3b57218b4fef06: expected vc=8    sm=1  got vc=8    sm=1  -> OK
8930104d11a77e8e: expected vc=131  sm=1  got vc=131  sm=1  -> OK
0cf5cc933b7a11fd: expected vc=120  sm=2  got vc=120  sm=2  -> OK
4a5306c009217e4b: expected vc=48   sm=2  got vc=48   sm=2  -> OK
c79f7f7943e79ea6: expected vc=2476 sm=5  got vc=2476 sm=5  -> OK
```
5/5 exact match, including one 5-submesh mesh.

## 5) Independent spot-check — every `MonoScript.m_AssemblyName` in both bundles

Fresh, standalone script, full scan of every `MonoScript` object (not just SS D's 5 named types),
in **both** built bundles:
```
SCENES bundle: 5 MonoScript object(s)
  class='LocationScene'      ns=''                    assembly='Assembly-CSharp'
  class='BotZone'            ns=''                    assembly='Assembly-CSharp'
  class='SpawnPointMarker'   ns='EFT.Game.Spawning'    assembly='Assembly-CSharp'
  class='LocationExportInfo' ns='EFT'                  assembly='Assembly-CSharp'
  class='LevelSettings'      ns=''                     assembly='Assembly-CSharp'

NAVMESH bundle: 0 MonoScript object(s)
```
All 5 are `'Assembly-CSharp'` exactly (no `.dll` suffix, no `SptGameRefs-*`, no unexpected
package/Unity assembly) — matches the task's exact requirement.

## 6) Independent spot-check — Scripts-scene component field values vs SPEC §D

Fresh script, walks every `MonoBehaviour`'s `m_Script` PPtr to its `MonoScript` for identity, then
`read_typetree()`s the real fields — does not import the validator's own resolution code:

- **`LevelSettings`**: `AmbientMode=3` (Flat) ✓, `SkyColor`/`EquatorColor`/`GroundColor` all
  `(0.5,0.5,0.5,1)` ✓, `AmbientIntensity=1.0` ✓, `fog=0` ✓, `skybox`/`CameraPrefab`/
  `PostProcessProfilePrefab` all null PPtrs `(0,0)` ✓.
- **`LocationScene`**: `WorldInteractiveObjects` len=0 (real empty array, not -1/null — the exact
  NRE site) ✓, `SpawnPointMarkers` len=3 ✓, `BotZones` len=1 ✓.
- **`BotZone`**: `Id=0` ✓, `_maxPersons=-1` ✓, `CanSpawnBoss=0` ✓, `SnipeZone=0` ✓,
  `PatrolWays=[]` (non-null empty) ✓, `UnSpawnPoints=[]` (non-null empty) ✓, `SpawnPointMarkers`
  non-null, 3 real entries wired to the 3 authored markers (path_ids 29/24/25) — stronger than
  SPEC's literal "empty list" minimum, matching its actual hard requirement ("MUST NOT be null")
  and risk #11's own check, same documented non-defect deviation every prior pass has noted.
- **`LocationExportInfo`**: `_unixDateTime=1757707200` ✓ (matches `BuildBundles.BuildUnixDateTime`,
  unchanged since S1).
- **`SpawnPointMarker` ×3**: `Categories=1` (Player) on all 3 ✓; `Sides` = `1`(Usec)/`2`(Bear)/
  `7`(All) ✓; `Rotation` identity `(0,0,0,1)` on all 3 ✓; `DelayToCanSpawnSec=4.0` ✓;
  `CorePointId=0` ✓; `BotZone` PPtr null `(0,0)` on all 3 ✓ (no linkage needed, per spec);
  `_collider` a real non-null PPtr per marker ✓; `Infiltration=''` (Unity's null↔empty-string
  serialization equivalence, not a real deviation, same as every prior pass). Positions are real,
  in-bounds Arena coordinates — `(-5.73, 0.08, 48.59)`, `(2.67, 0.08, -39.09)`,
  `(-1.67, 0.08, 45.44)` — **byte-identical to `s2-validate-r1.md`'s own independently-read
  values**, confirming the built bundle is unchanged since that pass and still uses the real
  anchor data (not the synthetic fallback), consistent with `s2-fix-r1.md`'s own claim that the
  re-extraction it ran was byte-identical to what these bundles were already built from.

## 7) Independent spot-check — NavMeshData present, non-trivial

```
NavMeshData path_id: 1751848640953621949
raw serialized size: 370556
  m_NavMeshTiles: len=22
  m_SourceBounds: len=2
Total NavMeshData objects: 1
```
1 asset, 22 real tiles (matches `s2-build.md`'s own post-hoc tile-count read), 370,556 bytes raw —
clearly non-trivial, not a near-empty 0-source bake.

---

## Files touched

- **None under version control.** No extractor, validator, or Unity project file was modified —
  all three validators and the real S2 build were already correct/clean; no bugs found this pass.
- `docs/progress/s2-validate-r2.md` (this file, new).
- Four standalone spot-check scripts written to the session scratchpad only (not part of the
  repo): `spotcheck_meshes.py`, `spotcheck_monoscripts.py`, `spotcheck_scriptfields.py`,
  `spotcheck_navmesh.py`.

No file under `D:/SPT41` or `X:/` was written or read this pass (only `work/extract/equator` and
`work/build/full/*`, both already-produced ArenaMapPort outputs, were read; the extractor
subprocess `validate_extract.py` itself spawns for reproducibility re-reads
`D:/Battlestate Games/Escape from Tarkov Arena/...`, a read-only source, unchanged). No Unity
process was started. No `git add`/`git commit` was run — per workspace rule, the lead commits.

## Handoff

- **S2 is clean, confirmed independently, right now**: all three §B.WP6 validators pass with 0
  failures in their strictest modes (`validate_extract.py` full incl. reproducibility+freshness;
  `validate_bundle_roundtrip.py` with `--require-scripts-scene --require-navmesh`;
  `validate_preset.py` with no `--allow-missing-scenes`, 6/6 rcids). All 4 independent,
  from-scratch UnityPy spot-checks the task asked for also pass, including the
  no-`SptGameRefs-*` assembly-identity check across **both** bundles (not just the scenes one).
- Nothing needed fixing this pass — `s2-fix-r1`'s two real fixes (extractor spawn-anchor
  monkey-patch, validator in-scope accounting) are confirmed holding under a fresh, independent
  re-run, not just re-read from that report.
- Re-run commands (all confirmed clean this pass):
  ```
  python tools/validate/validate_extract.py --package work/extract/equator
  python tools/validate/validate_bundle_roundtrip.py \
    --scenes-bundle work/build/full/arena_equator_scenes.bundle \
    --navmesh-bundle work/build/full/arena_equator_navmesh.bundle \
    --package work/extract/equator \
    --material-shaders work/build/full/material-shaders.json \
    --editor-verify work/build/full/editor_verify.json \
    --require-scripts-scene --require-navmesh
  python tools/validate/validate_preset.py --scenes-bundle work/build/full/arena_equator_scenes.bundle
  ```
- Same residual, non-blocking, already-documented items as `s2-fix-r1.md`'s own handoff (not
  re-investigated, out of a pure validation pass's scope): the Mesh in-scope computation is
  provable-by-exclusion but not provably-specifically-skinned-geometry from package schema alone
  (no `mesh_id` field on `SkinnedMeshRenderer`); `tools/build_all.ps1`'s steps 1-2 (GameRefs sync +
  real Unity `BuildBundles.Run`) have not been re-exercised since `s2-build`'s own run — this
  pass, like `s2-fix-r1`, only re-validated the already-built artifacts, which is sufficient given
  `validate_extract.py`'s own 0-diff freshness proof that nothing about the input has changed.
