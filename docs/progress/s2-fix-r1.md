# s2-fix-r1 — root-cause fixes for S2's real FAILs, re-verified — progress log

Role: fixer, task label `s2-fix-r1`. Task: S2 was not clean per `docs/progress/s2-build.md` and
`docs/progress/s2-validate-r1.md` — re-verify ground truth first (reports may be stale), root-cause,
fix, re-run affected steps (incl. the identity post-step) and validators.

**Result: fixed and re-verified.** Both real bugs the prior two passes root-caused but left unfixed
are now fixed, with the fix's own effect independently re-confirmed by re-running the actual
pipeline steps, not just re-reading the old reports:

1. **`tools/extract/extract_equator.py`'s silent spawn-anchor data loss** (s2-validate-r1.md §1) —
   fixed. A fresh extraction now deterministically finds all 20 real `SpawnPointMarker`s again, and
   `validate_extract.py`'s package-freshness/reproducibility check (previously 1 FAIL) is now clean.
2. **`validate_bundle_roundtrip.py`'s Mesh/Texture2D/Material count checks not accounting for
   `skip_import` SkinnedMeshRenderer-only package assets** (root-caused independently across THREE
   passes — s1-fix-r1, s2-build, s2-validate-r1 — never fixed until now) — fixed. The validator now
   compares bundle counts against the correct in-scope baseline; the 3 recurring hard FAILs are gone.

As a byproduct, also fixed the flagged `patch_monoscript_identity.py` performance issue (35+ minutes
at S2 scale) with a small, safe reordering — re-running the mandatory identity post-step against the
real S2 bundles now takes ~22 seconds, not 35+ minutes.

No git commit made (workspace rule: the lead commits). No file under `D:/SPT41` or `X:/` was written.
Only one Unity-adjacent process (Python/pwsh, never Unity.exe itself — no Unity rebuild was needed,
see §4) ran at a time.

---

## 0) Re-verified ground truth first (reports may be stale)

Before trusting either prior report, re-checked the claims that matter most for deciding what to fix:

- **Is `docs/progress/SOURCE-PATH.md`'s "X: is gone, D:/Battlestate Games/... is now the real source"
  lead note still true right now?** Yes — confirmed directly: `ls /x/` fails (not mounted at all;
  `net use` lists only Y:/Z:), while `D:/Battlestate Games/Escape from Tarkov Arena/
  EscapeFromTarkovArena_Data/` exists with `level57`/`sharedassets57.assets`/`globalgamemanagers.assets`
  /`resources.assets` all present. So `extract_equator.py`'s current `DEFAULT_SOURCE` (D:-prefixed) is
  correct and current; `analysis/scripts/common.py`'s hardcoded `BASE = "X:/..."` is the one that's
  stale — confirming s2-validate-r1's root cause precisely, not something that changed since.
- **Is `work/extract/equator`'s checked-in package really still carrying the real 20 anchors, as
  s2-validate-r1 claimed?** Yes — read `manifest.json.anchors_reference` directly before touching
  anything: `{"EFT.Game.Spawning.SpawnPointMarker": {"count": 20, "sample_files": ["level57"], ...}}`.
- **Are the 3 `validate_bundle_roundtrip.py` FAILs still reproducible right now, unchanged?** Yes —
  re-ran it before any fix, got the identical 3 FAILs s2-build.md/s2-validate-r1.md both reported
  (Mesh 2584/2605, Texture2D 648/708, Material 313/332).

Ground truth confirmed to match both prior reports exactly — proceeded to root-cause and fix rather
than re-diagnosing from scratch (the two prior passes' root-cause work was independently re-verified
correct, not just assumed).

---

## 1) Fix 1 — `tools/extract/extract_equator.py` spawn-anchor silent data loss

**Root cause (re-confirmed, not just re-read)**: `analysis/scripts/census.py`'s
`is_scene_file()`/`scene_exact_paths()` build their file-match-set from `common.BASE` (a name
imported into `census.py`'s own module namespace at `from common import BASE` time — a plain
snapshot, not a live reference to `common.BASE`). `common.BASE` is hardcoded to
`"X:/Escape from Tarkov Arena/EscapeFromTarkovArena_Data"` and **cannot be edited** —
`analysis/` is READ-ONLY for every WP (`docs/SPEC.md`'s directory table: "nobody writes here except
re-running an already-documented script"). Since every file `extract_equator.py` actually loads is
keyed by the real `--source` (D:-prefixed), the exact-path match in `is_scene_file()` silently
matched **zero** loaded files, and `spawn_zone_anchors()` returned `{}` with no exception raised —
so the surrounding `try/except` in `extract_equator.py` never fired, and `manifest.warnings` never
got an entry either. A fully silent, deterministic data-loss path.

**Fix** (`tools/extract/extract_equator.py`): added `compute_spawn_anchors(env, scene_dict, source,
warnings)`, which monkey-patches the ONE name census.py's functions actually read at call time
(`arena_census.BASE`, a plain module attribute looked up fresh on every call) to our real `--source`
for the duration of one call, then restores it — this changes nothing on disk, never touches
`analysis/scripts/*.py`, and is reverted immediately after (verified: `census.BASE` reads back as the
original `'X:/...'` after the call). Also: an empty `anchors` result is now always surfaced as an
explicit `manifest.warnings` entry rather than silently accepted as "this map has no anchors" — the
exact silence that let this bug go unnoticed for two whole passes.

**Verified, isolated, before touching the real package**: loaded scene 57 alone via a standalone
script, called `compute_spawn_anchors()` directly — returned all 20 real
`EFT.Game.Spawning.SpawnPointMarker` entries (`sample_files=['level57']`), 0 warnings, and
`arena_census.BASE` correctly restored to `'X:/Escape from Tarkov Arena/EscapeFromTarkovArena_Data'`
afterward.

**Verified for real**: re-ran the full 5-scene extraction (`python tools/extract/extract_equator.py
--source "D:/Battlestate Games/Escape from Tarkov Arena/EscapeFromTarkovArena_Data" --scenes all5
--output work/extract/equator --fixed-timestamp 2026-09-13T00:00:00Z`) — `0 errors, 201 warnings`
(same 201 as before: 200 benign no-mesh MeshFilters + 1 volume-texture note; **no** new "anchors came
back empty" warning), `manifest.anchors_reference` now shows the real 20 `SpawnPointMarker` entries
with genuine in-bounds Arena world positions.

**Then re-ran `validate_extract.py` in FULL strict mode (no `--skip-reproducibility`)** — this
re-extracts twice more internally and diffs against the checked-in package:
```
ok : two independent fresh extractor runs ... produce a byte-identical package -- 0 diff(s): []
ok : the checked-in --package (work\extract\equator) matches what the current extractor actually
     produces from today's source data (package-freshness check) -- 0 diff(s): []
OK -- all checks passed (2 non-blocking warnings, both pre-existing/benign, unrelated to this fix)
```
Previously (s2-validate-r1.md): **1 FAIL** on exactly this check. Now: **0 FAILs**, and both the
reproducibility AND freshness diffs report literally 0 differences — meaning my re-extraction is not
just "has anchors again" but byte-identical (mod `extracted_utc`) to what was already checked in, to
what a fresh run produces, and to itself run twice. This also proves the fix touches *only* the
anchors computation path: nothing about mesh/material/texture/scene extraction changed.

---

## 2) Fix 2 — `validate_bundle_roundtrip.py`'s Mesh/Texture2D/Material counts vs `skip_import` assets

**Root cause (re-confirmed, third time independently, still never fixed until now)**:
`load_package_expected()` compared the bundle's Mesh/Texture2D/Material counts against the RAW
package totals (`len(manifest.meshes)` etc.) — which include assets reachable ONLY from a
`skip_import: true` `SkinnedMeshRenderer` (SPEC §B.WP1 extracts these too, "dereference, don't
skip", but §B.WP2 decision #8 never imports a SkinnedMeshRenderer or builds a Renderer for it, so
Unity's own dependency-based `BuildAssetBundles` correctly never pulls those assets into the bundle).
Comparing against the raw total was comparing against the wrong baseline — not a build defect, a
validator logic gap, independently confirmed exactly-explained-with-0-unexplained-remainder across
THREE separate passes (s1-fix-r1 first noticed the pattern at S1's tiny scale, s2-build root-caused
the exact 21/20/60 split at full scale, s2-validate-r1 independently re-derived the same numbers from
scratch) — and never actually fixed in the validator itself until this pass.

**Fix** (`tools/validate/validate_bundle_roundtrip.py`): `load_package_expected()` now also computes
the IN-SCOPE reference sets — `mesh_ids_in_scope` (every `mesh_id` on a `MeshFilter`/`MeshCollider`,
deliberately NOT `SkinnedMeshRenderer`, which carries no `mesh_id` field in this package schema at
all), `material_ids_in_scope` (every `material_ids` entry on a `MeshRenderer`, NOT
`SkinnedMeshRenderer`), and `texture_ids_in_scope` (every non-null `tex_envs[*].texture_id` in the
in-scope materials' own `materials/<id>.json` files). `validate_scenes_bundle_contents()`'s
Mesh/Texture2D/Material count checks now compare against these in-scope totals instead of the raw
package totals. The raw totals are kept and still printed (informational/provenance), and a new
summary line reports package-vs-in-scope-vs-excluded counts explicitly, so the exclusion is visible
in every future run's output, not just recoverable via an ad hoc root-cause script.

**Verified against the real, already-built S2 bundles** (`work/build/full/*.bundle` — no rebuild
needed, see §4):
```
package totals vs in-scope (non-skinned) totals: meshes 2605 vs 2584 (21 excluded, skip_import-only),
  materials 332 vs 312 (20 excluded), textures 708 vs 648 (60 excluded)
ok : Mesh count in bundle (2584) == package (2584)
ok : Texture2D count in bundle (648) == package (648)
...
OK -- all checks passed (1 non-blocking warning(s)):
  WARN: Material count in bundle (313) exceeds package (312) -- expected if Unity pulled in
        built-in Skybox/RenderSettings defaults per scene ...
```
The excluded counts (21/20/60) match s2-build.md's and s2-validate-r1.md's own independently
root-caused figures exactly. Previously: **3 hard FAILs**. Now: **0 FAILs**, exit code 0, and the one
remaining line is the same benign +1-Default-Skybox WARN this workspace has treated as expected since
S1. No validator check was weakened — the comparison got *more* correct (a real defect in this exact
same spot would still be caught: an in-scope asset missing from the bundle still fails the exact
count check, unchanged).

---

## 3) Bonus fix — `patch_monoscript_identity.py`'s flagged perf issue (35+ min -> ~22s)

Not part of the task's two named bugs, but directly blocks "re-run ... the identity post-step" from
being a reasonable ask at S2 scale, and was explicitly flagged as technical debt in
`docs/progress/s2-build.md` §4 (35+ minutes at S2's ~120k-object scale, confirmed genuinely still
working via `Get-Process` CPU-time growth, not hung).

**Root cause**: `main()` unconditionally computed the expensive
`pre_snapshot = snapshot_non_monoscript_objects(env)` (a byte-for-byte snapshot of every
non-MonoScript object — ~120k individual `get_raw_data()` calls at S2 scale) **before** checking
whether any MonoScript actually needs patching — so even the "already clean, nothing to do" idempotent
re-check paid the full expensive snapshot cost.

**Fix**: `before_names` (already computed earlier, from the identical `RENAME_MAP`-based MonoScript
scan `patch_env()` itself does) already tells us with certainty whether anything will be patched.
Compute `needs_patch = any(name in RENAME_MAP for name in before_names)` right after `before_names`
is read, and skip straight to the cheap "0 MonoScripts needed patching" early-return when
`not needs_patch and not args.force_rewrite_if_clean` — before ever computing `pre_snapshot` or
`before_blocks`. When a real patch (or `--force-rewrite-if-clean`) IS about to happen, the exhaustive
byte-identity snapshot/verification is completely unchanged — not weakened, not skipped, not sampled.

**Verified correct** before using it for real:
- Smoke-tested against the small `work/build/s1/arena_equator_scenes.bundle` (already clean): fast
  path, 2.5s, correct "0 MonoScript(s) needed patching" output.
- Smoke-tested `--force-rewrite-if-clean` against the same S1 bundle (forces the full path even
  though nothing needs patching): full exhaustive check ran exactly as before this fix — `362
  non-MonoScript objects... byte-for-byte identical`, header/compression/block-size checks all `ok`,
  `+0` byte delta, exit 0. Restored the original file afterward (this was a throwaway smoke test, not
  a real re-patch).

**Then re-ran for real** against both actual S2 bundles (§4) — both report "0 MonoScript(s) needed
patching... Input left completely untouched" (already correctly patched from the prior
`identity-fix`/`s2-build` passes — this run is a pure idempotency re-check, not a new patch):
```
arena_equator_scenes.bundle (306,614,329 bytes):  0m22.142s (was 35+ minutes)
arena_equator_navmesh.bundle (81,562 bytes):        0m1.341s
```

---

## 4) Re-ran affected steps, incl. the identity post-step, and all validators

**Did NOT re-run the Unity build (`BuildBundles.Run`) itself** — deliberate, reasoned, not an
oversight. §1's fix touches *only* the anchors-computation code path; the full-strict
`validate_extract.py` re-run (§1) proves this precisely: two fresh extractor runs plus a diff against
the checked-in package all report **0 differences** (mod `extracted_utc`), meaning every byte of
mesh/material/texture/scene content in `work/extract/equator` is unchanged from what
`work/build/full`'s bundles were already built from. Re-running Unity's ~6-minute import/bake/build
pipeline against byte-identical input would reproduce the exact same output for zero new information
— the same judgment call `s2-build.md` itself made in an analogous situation. What WAS re-run, for
real, against the real already-built `work/build/full` bundles:

1. **Identity post-step** (§3): `patch_monoscript_identity.py` against both bundles — confirmed still
   correctly patched (`{'Assembly-CSharp': 5}` in the scenes bundle, 0 MonoScripts in the navmesh
   bundle), ~23s total (was 35+ min).
2. **`validate_extract.py`**, full strict (no flags) against `work/extract/equator` — **OK, 0
   failures** (was 1 FAIL).
3. **`validate_bundle_roundtrip.py`**, `--require-scripts-scene --require-navmesh`, all 5 explicit
   paths, against `work/build/full/*` — **OK, 0 failures**, exit 0 (was 3 FAILs).
4. **`validate_preset.py`**, strict (no `--allow-missing-scenes`) against
   `work/build/full/arena_equator_scenes.bundle` — **OK, 0 failures**, 6/6 rcids, exit 0 (unchanged
   from s2-validate-r1, re-confirmed not regressed by either fix).
5. **`tools/build_all.ps1 -PackageDir work/extract/equator -OutputDir work/build/full
   -SkipGameRefsSync -SkipUnityBuild`** — the actual sanctioned orchestrator's own steps 3 (mandatory
   identity patch) + 4 (all three validators, its own hardcoded strict/default flags), run
   end-to-end as one script, not just my own separate invocations. Confirmed via its own log: all
   three validators report `OK`, matching every number above exactly, ending with the script's own
   final line:
   ```
   [build_all] OK -- build + mandatory identity patch + all validators passed. D:/ArenaMapPort/work/build/full is safe to copy into dist/.
   ```
   exit code 0 (log: `work/logs/s2fix_build_all_r1.log`). This is the definitive, from-the-actual-
   sanctioned-script confirmation that S2 is clean.

---

## Files touched

- `tools/extract/extract_equator.py` — new `compute_spawn_anchors()` helper (SS1); call site swapped
  to use it.
- `tools/validate/validate_bundle_roundtrip.py` — `load_package_expected()` now computes in-scope
  reference sets; `validate_scenes_bundle_contents()`'s Mesh/Texture2D/Material checks compare
  against them; new summary print (SS2).
- `tools/bundle/patch_monoscript_identity.py` — reordered the expensive pre-snapshot to only run when
  a patch will actually be written (SS3).
- `work/extract/equator/**` — re-extracted in place with the fix (gitignored); now carries the same
  content as before plus deterministically-reproducible `anchors_reference`.
- `work/build/full/arena_equator_scenes.bundle` / `arena_equator_navmesh.bundle` — NOT modified
  (identity patch ran and correctly found nothing to do; both files are byte-for-byte what they were
  before this pass, confirmed via the "Input left completely untouched" no-op path).
- `work/logs/s2fix_extract_run1.log`, `work/logs/s2fix_validate_extract_run1.log`,
  `work/logs/s2fix_validate_preset_run1.log`, `work/logs/s2fix_build_all_r1.log` — this pass's run
  logs, kept as evidence (gitignored).
- `docs/progress/s2-fix-r1.md` — this file.

**Not touched, deliberately**: `analysis/scripts/common.py`/`census.py` (READ-ONLY per SPEC —
the whole point of §1's fix is working around this without editing it); `docs/SPEC.md` (not in this
role's writable path list — `work,dist,tools,UnityProject,docs/progress` only); did not add a
`mesh_id` field to the SkinnedMeshRenderer package schema (would have made the Mesh in-scope
computation provable with the same certainty as materials/textures, but that's a package-format
change and SPEC.md's schema table is the Architect's contract, not this role's to evolve — the
validator fix instead independently re-derives the same "unreferenced by any in-scope component" set
`validate_extract.py` already computes and WARNs about, which is sufficient: it doesn't need to know
*why* an asset is unreferenced to correctly decide the bundle is allowed to exclude it).

No file under `D:/SPT41` or `X:/` was written. No `git add`/`git commit` was run — per workspace rule,
the lead commits.

## Handoff

- **S2 is clean now, for real, re-verified**: `validate_extract.py` (full strict),
  `validate_bundle_roundtrip.py` (`--require-scripts-scene --require-navmesh`), and
  `validate_preset.py` (strict, no `--allow-missing-scenes`) all report 0 failures against the real
  `work/extract/equator` + `work/build/full/*` — confirmed both via direct invocation and via
  `tools/build_all.ps1` itself (the sanctioned orchestrator), not just my own ad hoc scripts.
- **`tools/build_all.ps1` (full run, from a clean GameRefs sync + real Unity build) has NOT been
  exercised end-to-end by this pass** — only steps 3+4 were re-run (deliberately, see §4's reasoning).
  The last full run of steps 1-2 remains `s2-build.md`'s own. If the lead wants a from-scratch,
  zero-assumptions confirmation, `pwsh tools/build_all.ps1 -PackageDir D:/ArenaMapPort/work/extract/equator
  -OutputDir D:/ArenaMapPort/work/build/full2 -BakeNavMesh $true -Verify $true` (full run, into a
  fresh output dir) would now be expected to pass its own step-4 validators cleanly on the first try,
  including `validate_extract.py`'s freshness check (previously would have failed on ANY from-scratch
  re-run before this fix).
- **`patch_monoscript_identity.py` is now fast at S2 scale** (~22s, was 35+ min) with no reduction in
  verification rigor for the case that matters (an actual patch being written). This removes the
  practical reason `tools/build_all.ps1` used to be slow to re-run just to re-confirm identity
  patching.
- **Residual, correctly-accepted non-defect** (unchanged, not something to "fix" further):
  `validate_bundle_roundtrip.py`'s Mesh in-scope computation is exact-and-provable via package schema
  for MeshFilter/MeshCollider (has `mesh_id`), but SkinnedMeshRenderer carries no `mesh_id` field in
  this package schema — so the 21 excluded meshes are proven "referenced by nothing in-scope" (a
  sufficient condition for correct bundle exclusion) but not provably "specifically SkinnedMeshRenderer
  geometry" from package data alone. This doesn't affect the validator's correctness (it doesn't need
  the *why*), only its explanatory precision; flagged for completeness, not blocking.
