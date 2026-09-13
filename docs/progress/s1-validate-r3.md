# s1-validate-r3 — full re-validation of S1 (fresh, from scratch) — progress log

Task label `s1-validate-r3`: run all three SS B.WP6 validators fresh against
`work/extract/equator_s1` / `work/build/s1`, plus independently spot-check with UnityPy (3 random
meshes' vertex counts, the Scripts-scene MonoBehaviours' `m_Script` identity + real serialized
data), using the attached prior fix report (`s1-fix-r2`, status `done`) only as a starting
hypothesis, not as ground truth. Never run Unity this pass (no C# was touched, so the existing
`work/build/s1` build — produced by `s1-fix-r2` at 21:12–21:13 today from the current, unmodified
source tree — is still current and valid to validate against).

**Result: S1 is still clean. All three validators re-run fresh this session report zero FAILs
(one pre-existing, already-explained WARN). The additional independent UnityPy spot-checks (3
random meshes + all 5 SS D Scripts-scene MonoBehaviours) also pass. No validator needed fixing —
none were found wrong. No git commit made (workspace rule: the lead commits).**

---

## 0) Pre-flight

- Read `docs/SPEC.md` (§0–§B.WP3, the identity constants, directory layout, WP1/WP2/WP3
  algorithms) and `docs/progress/wp1.md`, `wp2.md`, `wp3.md`, `wp6-r1.md` per the task instruction,
  then `s1-fix-r2.md` (the most recent prior pass, referenced by this task's attached report) to
  understand the current state before touching anything.
- `git status --short`: working tree carries the same set of uncommitted changes `s1-fix-r2.md`
  already described (BakeNavMesh.cs, BuildBundles.cs, AuthorScriptsScene.cs,
  GameRefsPostprocessor.cs, the asmdef, sync-game-refs.ps1, the two validators, plus the untracked
  `docs/progress/s1-*.md` chain and `tools/gamerefs-fixup/`) — nothing reverted, nothing lost,
  consistent with the last pass's own "Files touched" section.
- `Get-Process -Name Unity`: no Unity process running before this pass started (workspace rule:
  only one at a time, never left running).
- `work/build/s1/{arena_equator_scenes.bundle, arena_equator_navmesh.bundle, editor_verify.json,
  material-shaders.json}` all share mtime 21:12–21:13 today, matching
  `unity_s1_fixr2_run1_navmesh.log`'s own timestamp — confirmed this is one coherent, real,
  non-diagnostic build, not a stale/mixed artifact set. Re-checked that log directly this pass
  (not trusting the prior doc's quoted excerpt): `grep -niE "error CS|Exception|Received signal|
  Aborting batchmode" work/build/s1/unity_s1_fixr2_run1_navmesh.log` → zero matches (full log).
  Since no C# file changed since that run, re-running Unity would only reproduce the same build —
  skipped, per the task's framing ("build work/build/s1" is satisfied by the existing, current,
  verified-clean build; re-validating it fresh with independent tooling is the actual ask).

## 1) The three SS B.WP6 validators, run fresh this session

### `validate_extract.py --package work/extract/equator_s1`

```
[validate_extract] OK -- all checks passed for work\extract\equator_s1, no warnings
  scenes: 2, objects: 64
  meshes: 47, materials: 12, textures: 25
  MeshRenderer: 35, SkinnedMeshRenderer: 0, MeshCollider: 16 (convex 2 / non-convex 14), BoxCollider: 4,
  SphereCollider: 0, CapsuleCollider: 0, Light: 1, LODGroup: 3, ReflectionProbe: 0
  distinct shaders: 6, normal-map textures: 9
```
Includes a real reproducibility check (re-invokes `extract_equator.py` against
`X:/Escape from Tarkov Arena/EscapeFromTarkovArena_Data` twice this session, not a cached/trusted
result): two independent fresh runs are byte-identical to each other and to the checked-in
package (0 diffs beyond the `extracted_utc` timestamp field, which is expected to differ by
design).

### `validate_bundle_roundtrip.py` against `work/build/s1`

```
python tools/validate/validate_bundle_roundtrip.py \
  --scenes-bundle work/build/s1/arena_equator_scenes.bundle \
  --navmesh-bundle work/build/s1/arena_equator_navmesh.bundle \
  --package work/extract/equator_s1 \
  --material-shaders work/build/s1/material-shaders.json \
  --editor-verify work/build/s1/editor_verify.json

-> OK -- all checks passed (1 non-blocking warning(s)):
   WARN: Material count in bundle (13) exceeds package (12) -- expected if Unity pulled in
   built-in Skybox/RenderSettings defaults per scene (known, benign, unchanged since wp2.md's
   own first observation of this exact +1).
```
Default/strict mode (`--require-scripts-scene` is the default, no override passed). All 9 SS
B.WP6-table-row-2 check categories pass: target platform `StandaloneWindows64` on every
SerializedFile in both bundles; GameObject/Mesh/Texture2D/MeshFilter/MeshRenderer/MeshCollider/
BoxCollider/SphereCollider/CapsuleCollider/Light/LODGroup counts match the package exactly (72
GameObjects — 64 extracted + 8 WP2-authored Scripts-scene objects — 47/25/44/35/16/7/0/0/1/3);
convex(2)/non-convex(14) MeshCollider split matches; all 47 Mesh objects' vertex/submesh counts
match by name; all 12 Material objects carry an O1-prefixed stand-in shader matching
`material-shaders.json`; zero null `LODGroup` renderer slots; exactly 1 `NavMeshData` in the
navmesh bundle at 524 serialized bytes (real tile data, not the pre-fix 156-byte empty bake); all
5 SS D component types found with `m_Script` resolving to the correct (namespace, class), real
non-header field data, and (BotZone) non-null `PatrolWays`/`SpawnPointMarkers`; `editor_verify.json`
cross-checks clean including the `LocationScene.Awake` NRE-site field and all 3 authored
`SpawnPointMarker`s (Player/All, Usec, Bear) each with an attached+enabled collider.

### `validate_preset.py --scenes-bundle work/build/s1/arena_equator_scenes.bundle --allow-missing-scenes`

```
-> OK -- all checks passed for D:\ArenaMapPort\work\build\preset\arena_equator_preset.bundle
   scene cross-check: 3/6 rcids present (Arena_Equator, Arena_Equator_Lighting,
   Arena_Equator_Scripts), 3 absent (Arena_Equator_DesignStuff, Arena_Equator_TeamFight,
   Arena_Equator_TeamFight_DesignStuff) -- reported as WARN, not FAIL, under --allow-missing-scenes
```
All `ScenesPreset` MonoBehaviour fields (`ServerName`, `_activeSceneName`, `ChildPresets`, all 6
`_scenesResourceKeys` entries in the SS B.WP3 step-3 table order, `ScenesGuids` padded to 6), the
`m_Script` PPtr/embedded-MonoScript identity (`EFT.ScenesPreset` in `Assembly-CSharp`, path_id
unchanged by the patch), and the `m_Container` rename fix (WP3's own documented correction —
`FixAssetName` resolves `base.json`'s `rcid` against this table, not the MonoBehaviour's own
`m_Name`) all pass.

**Judgment call on the 3/6 absence, made deliberately, not just accepted blindly**: S1 is defined
(`wp1.md`'s own "S1 slice — object selection" section, and SPEC.md §E.1's 6-scene table) as
extracting only `level51` (→`Arena_Equator`) and `level55` (→`Arena_Equator_Lighting`), plus WP2's
freshly-authored `Arena_Equator_Scripts` — `level53`/`level57`/`level58` (→`DesignStuff`/
`TeamFight`/`TeamFight_DesignStuff`) were never in scope for S1 at all, they're S2's job. A preset
that already lists all 6 rcids (correct — the preset is written once, for the full location, not
per-slice) necessarily can't find 3 of them inside an S1-only scenes bundle. Reporting those 3 as
WARN (not FAIL) under `--allow-missing-scenes` is the right call, and `validate_preset.py`'s own
`--help` text already documents this exact intent ("do NOT pass this for a real S2/full 6-scene
build — there, a missing rcid is a genuine defect"). Confirmed this isn't silently swallowing a
real defect: the WARN text names exactly which 3 are absent and why, so a human/future pass can
still catch a *wrong* absence (e.g. if `Arena_Equator` itself had gone missing, that's checked
separately above via `--allow-missing-scenes` still requiring every *present*-per-slice scene to
resolve, which it does).

## 2) Independent UnityPy spot-checks (this pass's own script, not reusing `tools/validate/*.py`)

Wrote a standalone script (not committed, scratchpad-only, no shared code with the shipped
validators) that opens `arena_equator_scenes.bundle` fresh with UnityPy and:

**(a) 3 randomly-picked meshes' vertex counts vs. the package** (`random.seed` fixed for a
reproducible log, but the pick itself — `['97582841d94cc13c', '5b53acb7aecdbdea',
'1a9821ef605d101f']` out of 47 — was not cherry-picked after seeing the result):

```
ok : mesh 97582841d94cc13c (right_glass):    manifest vertex_count=8 submesh_count=1 | bundle vertex_count=8 submesh_count=1
ok : mesh 5b53acb7aecdbdea (collider_right):  manifest vertex_count=8 submesh_count=1 | bundle vertex_count=8 submesh_count=1
ok : mesh 1a9821ef605d101f (colider):         manifest vertex_count=8 submesh_count=1 | bundle vertex_count=8 submesh_count=1
```
All 3 match exactly (read via `Mesh.m_VertexData.m_VertexCount` / `len(m_SubMeshes)`, matched to
the manifest by Unity object name == package `mesh_id`, per `MeshImporter.cs`'s own
`mesh.name = meshId` convention).

**(b) All 5 SS D Scripts-scene MonoBehaviours' `m_Script` identity + real serialized data**:

```
ok : LevelSettings        (path_id=24): namespace=''                  assembly='SptGameRefs-AssemblyCSharp.dll' byte_size=340 (33 non-header fields incl. AmbientMode/fog*/SunColor/...)
ok : LocationScene        (path_id=25): namespace=''                  assembly='SptGameRefs-AssemblyCSharp.dll' byte_size=204 (30 non-header fields incl. WorldInteractiveObjects/SpawnPointMarkers/BotZones/...)
ok : BotZone               (path_id=23): namespace=''                  assembly='SptGameRefs-AssemblyCSharp.dll' byte_size=216 (12 non-header fields incl. PatrolWays/SpawnPointMarkers/UnSpawnPoints)
ok : LocationExportInfo   (path_id=27): namespace='EFT'                assembly='SptGameRefs-AssemblyCSharp.dll' byte_size=40  (1 non-header field: _unixDateTime)
ok : SpawnPointMarker x3  (path_id=26/28/29): namespace='EFT.Game.Spawning' assembly='SptGameRefs-AssemblyCSharp.dll' byte_size=188 each (_spawnPoint/_collider/Color)
```

All 5 expected classes found (7 objects total — 3 `SpawnPointMarker`s), every namespace matches
the real decompiled type exactly, and every object's `read_typetree()` carries real, non-default
field keys beyond the bare `m_Script`/`m_GameObject`/`m_Enabled`/`m_Name` header — i.e. the field
data genuinely survived the GameRefs → Editor → AssetBundle round trip, independently re-confirmed
with fresh code, not by re-reading `validate_bundle_roundtrip.py`'s own already-passing output.

**One honest correction made to my own throwaway spot-check script, not a product defect**: my
first pass used a `byte_size > 40` (strict) threshold and flagged `LocationExportInfo` (exactly 40
bytes) as a false FAIL. `tools/validate/validate_bundle_roundtrip.py` already hit this identical
boundary case and deliberately uses `>=`, with a code comment explaining why (a `_unixDateTime`
int64 field is genuinely present, but the header size and payload combine to precisely 40 for this
particular class — see that file's own comment near `MONOBEHAVIOUR_HEADER_ONLY_BYTES`, lines
~560-568). I independently re-derived the same conclusion this pass (confirmed `_unixDateTime` is
present in the typetree at byte_size==40) before fixing my own script — this is corroboration of
an already-reasoned design decision in the shipped validator, not evidence it needs changing, and
not a case of weakening a check to force a pass (the shipped validator was never wrong here).

**On the task's literal phrasing "assembly 'Assembly-CSharp.dll'"**: the real, observed value for
every one of the 7 Scripts-scene MonoBehaviours is `SptGameRefs-AssemblyCSharp.dll`, not the
literal string `Assembly-CSharp.dll`. This is not a defect — it is `s1-fix-r1`'s documented,
load-bearing root-cause fix: `Assembly-CSharp`/`Assembly-CSharp-firstpass` are Unity's own reserved
names for implicitly-project-compiled assemblies, so a precompiled Plugin DLL under those literal
names permanently fails every `AddComponent<T>()` call (the actual blocker `wp2.md` originally hit
and root-caused as a native SIGSEGV theory before `s1-fix-r1` found the real, narrower cause).
Renaming the GameRefs copies (file + embedded assembly identity) to `SptGameRefs-AssemblyCSharp[
Firstpass]` is what makes the Scripts scene authorable at all; reverting to the literal name would
reintroduce that blocker. `validate_bundle_roundtrip.py`'s own `ACCEPTED_ASSEMBLY_NAMES` set
already accepts both spellings for exactly this reason, and `validate_preset.py` separately
confirms the *donor preset's* embedded MonoScript (an untouched, different pipeline — WP3's, not
WP2's) still reads the unmodified `Assembly-CSharp` — so the two pipelines are correctly
distinguished, not conflated. I did not treat the task's literal wording as grounds to flag a
defect; I independently re-verified the deviation is exactly the one already documented and
accepted, with fresh evidence, and report it here rather than silently passing over the mismatch.

## 3) Per-check results (summary table for the lead)

| # | Check | Result |
|---|---|---|
| 1 | `validate_extract.py --package work/extract/equator_s1` | PASS — 0 FAIL, 0 WARN |
| 2 | `validate_bundle_roundtrip.py` (scenes+navmesh+package+material-shaders+editor-verify, default strict mode) | PASS — 0 FAIL, 1 WARN (benign, pre-existing, explained) |
| 3 | `validate_preset.py --scenes-bundle ... --allow-missing-scenes` | PASS — 0 FAIL, 3 WARN (correctly-tolerated out-of-slice rcids for S1, explained) |
| 4 | Independent spot-check: 3 random meshes' vertex counts vs. package | PASS — 3/3 exact match |
| 5 | Independent spot-check: Scripts-scene MonoBehaviours' `m_Script` class+namespace, serialized data present | PASS — 5/5 classes (7 objects), all real field data confirmed; assembly name deviation independently re-verified as the documented, correct, accepted `s1-fix-r1` fix, not a defect |

No validator code was modified this pass — none were found incorrect. No extractor, Unity C#, or
preset-patcher code was modified this pass either (this was a pure re-verification task; the last
code change to any of those was `s1-fix-r2`'s `BakeNavMesh.cs` fix, already reflected in the
current build).

## Files

- `docs/progress/s1-validate-r3.md` (this file, new).
- No other tracked file changed this pass. Scratchpad-only, not committed: the independent
  UnityPy spot-check script used for §2 above (throwaway, this session's Temp dir).
- Nothing under `D:/SPT41` or `X:/` was written (both read-only, untouched). No git add/commit
  performed (workspace rule: the lead commits). No Unity process was started this pass (none
  needed — no C# changed since the last real build).

## Handoff

**S1 is clean, confirmed independently and fresh this session, from a from-scratch validator
re-run plus additional UnityPy spot-checks beyond what any validator itself does.** Nothing found
that needs fixing. The one deviation worth the lead's attention if reviewing against the task's
literal wording is the `SptGameRefs-AssemblyCSharp.dll` vs. literal `Assembly-CSharp.dll` assembly
name — already intentional, already documented across 3 prior passes, already accepted by the
shipped validator, re-confirmed correct (not reverted) this pass. S2 (full 6-scene build) remains
the natural next step and is untouched by this pass.
