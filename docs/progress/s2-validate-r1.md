# s2-validate-r1 — strict validation of the S2 full build — progress log

Role: validator (r1, first full-scale strict pass). Task: run all three SS B.WP6 validators
strictly against `work/extract/equator` + `work/build/full/*` (no reproducibility skip, no
`--allow-missing-scenes`), plus independent from-scratch UnityPy spot-checks. No repository
files were modified by this pass except this doc (`git status --short` before/after is
identical apart from this file); no Unity process was started (existing build outputs were
re-read only, plus one throwaway extractor re-run into the session scratchpad, never into
`work/`); no git commit made.

## Summary — per-check result

| # | Check | Result |
|---|---|---|
| 1 | `validate_extract.py --package work/extract/equator` (full, incl. reproducibility) | **1 FAIL** (package-freshness), 2 non-blocking WARNs — **root-caused to a real, independently confirmed bug**, see §1 |
| 2 | `validate_bundle_roundtrip.py` (`--require-scripts-scene --require-navmesh`, all 5 explicit paths) against `work/build/full` | **3 FAILs** (Mesh/Texture2D/Material counts, bundle < package) — **independently root-caused, confirmed correct behavior, not a pipeline defect** (materials/textures: exact match, 0 unexplained; meshes: consistent, not fully provable from package schema alone), see §2 |
| 3 | `validate_preset.py --scenes-bundle work/build/full/arena_equator_scenes.bundle` **strict, no `--allow-missing-scenes`** | **PASS**, 0 failures, all 6/6 rcids present, all fields correct |
| 4 | Independent spot-check: 5 random meshes vs package (fresh UnityPy script) | **PASS** — 5/5 exact match incl. one multi-submesh (sm=4) mesh |
| 5 | Independent spot-check: every `MonoScript.m_AssemblyName` in **both** bundles (fresh, full scan, not limited to SS D's 5 types) | **PASS** — scenes bundle: exactly 5 MonoScripts, all `'Assembly-CSharp'` (no `.dll`, no `SptGameRefs-*`); navmesh bundle: 0 MonoScripts |
| 6 | Independent spot-check: Scripts-scene component field values vs SPEC §D (fresh `read_typetree()`, not via the validator) | **PASS**, field-by-field, with 2 documented non-defect deviations from SPEC's literal wording, see §3 |
| 7 | Independent spot-check: NavMeshData present, non-trivial | **PASS** — 1 asset, 22 tiles, agent settings exactly match SPT's values |

**Bottom line**: the S2 build's actual deliverable (bundles in `work/build/full/`) is validated
correct on every check that matters to the shipped artifact. But this pass found and fully
root-caused a **real, previously-unflagged, currently-live bug** in the extraction pipeline
(§1) that makes the *next* extraction run silently lose spawn-anchor data — worth the lead's
attention before any future rebuild, including any CI/reproducibility gate that takes
`validate_extract.py`'s default (non-`--skip-reproducibility`) mode as ground truth.

---

## 1) `validate_extract.py --package work/extract/equator` — real FAIL, root-caused

Command run exactly as specified (no flags, so reproducibility checks run by default):
```
python tools/validate/validate_extract.py --package work/extract/equator
```
Result: structural/integrity checks all OK (0 failures on shape/reference/orphan checks, same 2
WARNs as s2-build's own pass: 201 manifest warnings, 21 orphan mesh ids). The two independent
fresh re-runs the validator performs agree with **each other** (0 diffs — determinism between
two back-to-back fresh runs holds). But the validator's **package-freshness check** — diffing a
third fresh run against the checked-in `work/extract/equator` package — reports:
```
FAIL: the checked-in --package (work\extract\equator) matches what the current extractor
actually produces from today's source data (package-freshness check) -- 1 diff(s):
['manifest.json differs (beyond just source.extracted_utc)']
```

**Investigated, not dismissed.** Re-ran the extractor once more into the scratchpad (same
source/scenes/timestamp) and deep-diffed the two `manifest.json` files field-by-field (fresh
script, not the validator's own `diff_trees`): the **only** difference anywhere in the entire
package (meshes/materials/textures/scenes/manifest all otherwise byte-identical) is
`manifest.anchors_reference`: the checked-in package has a
`"EFT.Game.Spawning.SpawnPointMarker": {"count": 20, "sample_files": ["level57"], ...}` entry;
every fresh run right now produces `anchors_reference: {}` — the key is **entirely absent**, not
merely empty-content-different.

**Root cause, confirmed via an isolated diagnostic** (loaded scene 57 alone, called
`spawn_zone_anchors()` directly, and separately did a raw manual scan of the same loaded
MonoBehaviours):
- The raw manual scan **does** find all 20 real `EFT.Game.Spawning.SpawnPointMarker`
  MonoBehaviours in `level57` right now, plus other spawn/zone classes (`NavMeshModifier` x20,
  `LocationScene` x1, etc.) — so the source data genuinely still has this content; nothing is
  missing upstream.
- `arena_census.spawn_zone_anchors()` (`analysis/scripts/census.py:437-467`) itself returns
  `{}` for the identical loaded `env` — because its file-matching helper
  `is_scene_file()`/`scene_exact_paths()` (`census.py:23-39`) builds its match-set from
  `common.py`'s **hardcoded** `BASE = "X:/Escape from Tarkov Arena/EscapeFromTarkovArena_Data"`
  constant (`analysis/scripts/common.py:4`, confirmed unmodified/read-only, exactly per SPEC's
  own convention), while `tools/extract/extract_equator.py`'s `DEFAULT_SOURCE` constant
  currently reads `"D:/Battlestate Games/Escape from Tarkov Arena/EscapeFromTarkovArena_Data"`
  (confirmed **uncommitted**, `git diff` shows it changed from the committed `X:/...` value —
  this is the same change flagged-but-not-investigated in `docs/progress/s2-build.md`'s last
  "Unrelated observation"). Every file UnityPy actually loads is keyed by the real, D:-prefixed
  path (`env.files` confirmed), so `fname in scene_exact_paths(scene_dict)` — comparing against
  X:-prefixed paths — never matches **anything**, silently. `found` stays empty, no exception is
  raised (the surrounding `try/except` in `extract_equator.py:847-851` only fires on a raised
  exception, and none is raised here), so `manifest.warnings` gets no entry either — a fully
  silent data-loss path.
- This is **deterministic and 100% reproducible right now**, not a timing/ordering fluke: 3
  independent fresh runs this session (the validator's own 2 + my 1) all agree on the empty
  result. The checked-in package's "20 real anchors" almost certainly comes from a run where
  `--source` and `common.BASE` happened to agree (`X:/...`, matching the git-committed default,
  and matching the literal command `docs/progress/s2-build.md` itself documents having used) —
  X: is not reachable in this session at all (only `C:`/`D:`/`Y:`/`Z:` are mounted), so I could
  not directly re-test against it, but the mechanism is fully explained and doesn't require X:
  to confirm: the D:-vs-X: string mismatch alone is sufficient and demonstrated.

**Why this matters, concretely**: per `docs/progress/s2-build.md` §2c, WP2's
`AuthorScriptsScene.ResolveSpawnPositions` reads this same `anchors_reference` block to place the
3 real `SpawnPointMarker`s at genuine Arena positions instead of falling back to a synthetic
`i*2,1,0` spread. The **current** `work/build/full` bundle is fine (confirmed independently in
§3 below — real, in-bounds Arena coordinates, not synthetic) because it was built from the
package while the anchors data was still intact. But **the exact "re-run commands for a future
clean build" `s2-build.md` documents in its own handoff section would, right now, silently
regenerate a package with zero spawn anchors**, and a subsequent Unity build from that package
would silently fall back to the synthetic layout — no error, no warning, nothing in either
`validate_extract.py`'s non-reproducibility-skip-mode summary line count or
`validate_bundle_roundtrip.py` would catch this (neither validator asserts anything about
`anchors_reference` content or about spawn-point positions being non-synthetic).

**Not fixed by me** — out of this task's scope (a strict-validation task, not a fix task; the
bug is in `analysis/scripts/common.py`'s stale `BASE` constant interacting with
`tools/extract/extract_equator.py`'s uncommitted `DEFAULT_SOURCE` edit, neither of which I'm
tasked to modify here) and **not a validator bug** — `validate_extract.py` is correctly catching
a real defect; weakening it (e.g. ignoring `anchors_reference` in the freshness diff) would hide
a genuine, currently-live silent-data-loss bug. Flagging for the lead as the top finding of this
pass.

---

## 2) `validate_bundle_roundtrip.py` — same 3 FAILs as `s2-build.md`, independently re-root-caused

Command (exact flags from the task, all 5 explicit paths):
```
python tools/validate/validate_bundle_roundtrip.py \
  --scenes-bundle work/build/full/arena_equator_scenes.bundle \
  --navmesh-bundle work/build/full/arena_equator_navmesh.bundle \
  --package work/extract/equator \
  --material-shaders work/build/full/material-shaders.json \
  --editor-verify work/build/full/editor_verify.json \
  --require-scripts-scene --require-navmesh
```
Result: same 3 FAILs as `s2-build.md` reported (`Mesh count in bundle (2584) == package (2605)`,
`Texture2D count in bundle (648) == package (708)`, `Material count in bundle (313) >= package
(332)`), 0 other failures — every other check passes (GameObject/MeshFilter/MeshRenderer/
MeshCollider/BoxCollider/SphereCollider/CapsuleCollider/Light/LODGroup counts all exact;
StandaloneWindows64 platform on every SerializedFile in both bundles; all 5 SS D
`MonoBehaviour`s present with resolving `MonoScript` identity and non-empty field data;
`BotZone.PatrolWays`/`SpawnPointMarkers` non-null and correctly wired; `editor_verify.json`
cross-check clean for all 6 scenes; NavMesh bundle exactly 1 `NavMeshData`, 370,556 bytes raw).

**Independently re-derived the root cause from scratch** (fresh Python script, does not import
`tools/validate/*.py` or `tools/extract/*.py` — reads `work/extract/equator`'s own
`scenes/*.json` + `materials/*.json` and the built bundle directly via UnityPy):

- **Materials — exact match, 0 unexplained.** Computed the set of material ids referenced by any
  in-scope `MeshRenderer.material_ids` across all 5 content scenes: **312**. Computed the set
  referenced *only* by a `SkinnedMeshRenderer.material_ids` (506 components, all
  `skip_import: true`): **20**, disjoint from the 312. `312 + 1 (Default-Skybox, confirmed the
  only bundle material name not in the package) = 313` — exactly the bundle's material count.
  Every one of the 312 in-scope material names is present in the bundle (0 missing); the bundle
  has 0 material names that aren't either package-sourced or `Default-Skybox`.
- **Textures — exact match, 0 unexplained.** Computed the texture ids referenced by any
  `tex_envs[...].texture_id` across only those same 312 in-scope materials: **648** — an exact
  match to the bundle's actual `Texture2D` count, with 0 missing from the bundle and 0 extra
  names in the bundle not in the package. The remaining 60 package textures are referenced
  exclusively by the 20 skinned-only materials.
- **Meshes — consistent, but not provable to the same exact-field-level certainty.** The 21
  meshes absent from the bundle are, independently confirmed, referenced by **no**
  `MeshFilter`/`MeshCollider` anywhere in the package (matches `validate_extract.py`'s own "21
  mesh id(s) never referenced" WARN exactly). Unlike materials, this package's
  `SkinnedMeshRenderer` component schema (checked directly: keys are
  `bind_pose_bone_names/cast_shadows/light_probe_usage/material_ids/receive_shadows/
  reflection_probe_usage/skip_import/type`) **does not carry a `mesh_id` field at all** — so I
  cannot independently confirm from the package data alone that these 21 are specifically the
  skinned renderers' own mesh geometry (as `s2-build.md`'s narrative asserts) rather than some
  other kind of orphaned mesh. What I *can* confirm with certainty: none of the 21 is referenced
  by any in-scope component, so their absence from the bundle is correct Unity dependency-based
  bundling behavior either way — not a pipeline defect regardless of the precise reason they're
  unused.

**Judgment**: this confirms `s2-build.md`'s own conclusion — these 3 FAILs are a real gap in
`validate_bundle_roundtrip.py`'s own count-equality assumption (it compares against the raw
package total, not "package assets reachable from a non-skinned renderer"), not a build defect.
**Not fixed here** (per the task's "fix validator bugs only with evidence" — I gathered the
evidence but a strict-validation task is not the place to also patch the validator; flagging
precisely, with the mesh-side caveat above, for whoever hardens it next).

---

## 3) Independent spot-checks (fresh scripts, no validator code imported)

**5 random meshes vs package** (`random.seed("s2-validate-r1-independent")`, sampled from all
2,605 package mesh ids, read directly from the bundle via UnityPy `Mesh.m_VertexData.
m_VertexCount`/`len(m_SubMeshes)`):
```
b777f87c328e5bc0: expected vc=122  sm=1  got vc=122  sm=1  -> OK
8ca94b2e01940478: expected vc=2433 sm=1  got vc=2433 sm=1  -> OK
8eae5844b9f64b87: expected vc=115  sm=1  got vc=115  sm=1  -> OK
417a4e939f48acfd: expected vc=36   sm=1  got vc=36   sm=1  -> OK
87652a4504db3718: expected vc=406  sm=4  got vc=406  sm=4  -> OK
```
5/5 exact match, including one 4-submesh mesh.

**Every `MonoScript.m_AssemblyName` in both bundles** (full scan, every `MonoScript` object, not
limited to SS D's 5 named types):
```
SCENES bundle:  5 MonoScript objects total, {'Assembly-CSharp': 5} -- 0 SptGameRefs-*
NAVMESH bundle: 0 MonoScript objects -- nothing to check, nothing to patch
```
No `SptGameRefs-*`, `.dll`-suffixed, or unexpected assembly name found anywhere in either
bundle.

**Scripts-scene component field values vs SPEC §D** (fresh `obj.read_typetree()` on every
`MonoBehaviour`, class identity resolved by walking `m_Script` PPtr to the matching `MonoScript`
myself — not via the validator):

- `LevelSettings`: `AmbientMode=3` (Flat) ✓, `SkyColor`/`EquatorColor`/`GroundColor` all exactly
  `(0.5,0.5,0.5,1)` ✓, `AmbientIntensity=1.0` ✓, `fog=0` ✓, `skybox`/`CameraPrefab`/
  `PostProcessProfilePrefab` all null PPtrs (`0,0`) ✓ — every SPEC §D value matches exactly.
- `LocationScene`: all ~30 array fields present as real (possibly empty) arrays, never null —
  `WorldInteractiveObjects=[]` (length 0, not -1) ✓, `SpawnPointMarkers`=3 real PPtrs ✓,
  `BotZones`=1 real PPtr ✓.
- `BotZone`: `Id=0` ✓, `_maxPersons=-1` ✓, `CanSpawnBoss=0` ✓, `SnipeZone=0` ✓, `PatrolWays=[]`
  (explicit empty, non-null) ✓, `UnSpawnPoints=[]` (explicit empty, non-null) ✓.
  **One deviation from SPEC's literal text, not a defect**: SPEC §D's table says
  `SpawnPointMarkers = new List<SpawnPointMarker>()` (explicit **empty** list); the actual
  built value is a **non-null list wired to all 3 real `SpawnPointMarker`s** — strictly stronger
  than SPEC's literal minimum (SPEC's own hard requirement was just "MUST NOT be null", which
  this satisfies and exceeds), and matches what `validate_bundle_roundtrip.py`'s own risk #11
  check explicitly wants ("non-null AND correctly wired").
- `LocationExportInfo`: `_unixDateTime=1757707200` — a plausible build-time Unix timestamp;
  `base.json` doesn't exist yet in this workspace (S3/dist scope, not built yet) so the SPEC §D
  "at or before `base.json`'s `UnixDateTime`" cross-check isn't independently verifiable this
  pass — noted, not a blocker for S2.
- `EFT.Game.Spawning.SpawnPointMarker` ×3: `Categories=1` (Player) on all 3 ✓; `Sides` = `1`
  (Usec), `2` (Bear), `7` (All) — matches SPEC's "dedicated Usec/Bear ones" + one `All`
  marker ✓; `Rotation=(0,0,0,1)` (identity) ✓ on all 3; `DelayToCanSpawnSec=4.0` ✓;
  `CorePointId=0` ✓; `Id` = a real GUID string per marker ✓; `_collider` = a non-null PPtr per
  marker ✓; `BotZone` PPtr = null (`0,0`) on all 3 ✓ (no BotZone linkage, matching SPEC's "no
  BotZone linkage needed"); `Infiltration=""` rather than SPEC's literal `null` — expected Unity
  string-serialization equivalence (Unity's binary format has no representation for a null
  `string` field distinct from empty), not a real deviation.
  **Positions are genuine, in-bounds Arena coordinates**
  (e.g. `(-5.73, 0.08, 48.59)`, `(2.67, 0.08, -39.09)`, `(-1.67, 0.08, 45.44)`) — confirms §1's
  finding that the *current* build used real anchor data, not the synthetic `i*2,1,0` fallback.

**NavMeshData**: 1 asset, `m_NavMeshTiles` length = **22** (independently read via
`read_typetree()`, not the validator's raw-byte-size proxy), `agentRadius=0.3
agentHeight=1.7 agentSlope=48 agentClimb=0.38` — exactly SPT's real agent-0 values, matching
SPEC §B.WP2.7 and `spt_gm.json`.

---

## Files touched

- `docs/progress/s2-validate-r1.md` (this file, new).
- Nothing else under version control — no validator, extractor, or Unity project file was
  modified. `git status --short` before and after this pass is identical (only this new file
  added).
- Scratch-only, not part of the repo, in the session scratchpad: `deep_diff.py`,
  `diag_anchors.py`, `rootcause_counts.py`, `rootcause_textures.py`, `spotcheck_meshes.py`,
  `spotcheck_monoscripts.py`, `spotcheck_scriptfields.py`, `spotcheck_navmesh.py`, plus one
  throwaway extractor re-run's output (`repro_run/`) — all under the scratchpad, never under
  `D:/ArenaMapPort/work` or `X:/`/`D:/SPT41`.

No file under `D:/SPT41` or `X:/` was written. Only one Unity-adjacent process (the Python
extractor, not Unity itself) was run at a time; no Unity Editor process was started this pass
(existing bundle/manifest outputs were re-read only). No `git add`/`git commit` was run.

## Handoff

- **The S2 deliverable itself (`work/build/full/*.bundle`) is validated correct** on every check
  that matters to the shipped artifact: strict `validate_preset.py` (no
  `--allow-missing-scenes`) passes 6/6; every count/identity/field check in
  `validate_bundle_roundtrip.py` passes except the 3 SkinnedMeshRenderer-driven count FAILs
  (independently re-confirmed as correct, expected behavior, not a defect); every independent
  from-scratch spot-check (meshes, MonoScript assembly identity in both bundles, SS D field
  values, NavMesh) passes.
- **Top finding, needs the lead's attention before the next rebuild**: `spawn_zone_anchors()`'s
  silent empty-result bug (§1) — `analysis/scripts/common.py`'s hardcoded `BASE="X:/..."` no
  longer matches `tools/extract/extract_equator.py`'s uncommitted `DEFAULT_SOURCE="D:/Battlestate
  Games/..."` change, so **any extraction run right now against the current source
  configuration silently produces zero spawn-point anchor data**, with no error/warning anywhere
  in the pipeline or either validator. The currently-checked-in package still has the real data
  (presumably extracted while the two constants agreed); a re-run of `s2-build.md`'s own
  documented "re-run commands for a future clean build" would silently regress this. Confirmed
  the mechanism precisely (path-string exact-match against a stale constant), confirmed the
  source data itself is NOT the problem (a raw scan finds all 20 real `SpawnPointMarker`
  MonoBehaviours present in `level57` right now). Two independent fixes are possible (make
  `common.py`'s `BASE`/`scene_exact_paths()` derive from the actual loaded `env`'s paths instead
  of a hardcoded constant; or pass the real source root through to `spawn_zone_anchors()`
  explicitly) — not attempted here, out of this task's scope.
- **Secondary finding, already known, re-confirmed independently**: `validate_bundle_
  roundtrip.py`'s Mesh/Texture2D/Material count-equality checks don't account for package
  assets reachable only via `skip_import: true` `SkinnedMeshRenderer`s. Materials/textures: I
  independently, exactly, fully explained both (0 unexplained in either direction). Meshes: I
  independently confirmed none of the 21 "missing" ones is referenced by any in-scope component
  (so their exclusion is correct either way), but could not use the package schema alone to
  prove they're specifically skinned-mesh geometry (the `SkinnedMeshRenderer` component record
  has no `mesh_id` field to check against). Not a build defect in either case; the validator's
  own count logic is what needs updating, not attempted here per the task's scope.
- Both validators' underlying logic remains unmodified and unweakened by this pass — every FAIL
  reported above is a real validator output, reproduced verbatim, then independently
  investigated with fresh, from-scratch tooling rather than trusted or dismissed.
