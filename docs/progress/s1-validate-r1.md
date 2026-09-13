# S1 validation — r1 — progress log

Role: validator. Ran all three §B.WP6 validators against the real S1 deliverables
(`work/extract/equator_s1`, `work/build/s1`, `work/build/preset/arena_equator_preset.bundle`),
plus independent UnityPy spot-checks not routed through the validators' own code. Two real bugs
found and fixed in `tools/validate/*.py` (not in WP1/WP2/WP3's own code) — both are "the validator
was wrong for the case it's now actually being asked to check," not weakenings; evidence for each
below. No git commit made.

## Summary — per-check pass/fail

| # | Check | Result |
|---|---|---|
| 1 | `validate_extract.py --package work/extract/equator_s1` | **PASS** (0 failures, 0 warnings) |
| 2 | `validate_bundle_roundtrip.py` against `work/build/s1` (scenes+navmesh bundle, package, material-shaders, editor_verify) | **PASS** (0 failures, 2 non-blocking WARNs — see below) |
| 3 | `validate_preset.py --scenes-bundle work/build/s1/arena_equator_scenes.bundle` | **PASS** (0 failures) — required a validator fix, see "Fix 2" |
| 4 | Spot-check: 3 random meshes' vertex counts vs package (independent UnityPy, not via the validator) | **PASS** — 3/3 exact match |
| 5 | Spot-check: Scripts-scene MonoBehaviours' `m_Script` identity + serialized-data presence | **FAIL / N-A — genuine gap, reported honestly, not papered over** — see below |

---

## 1) `validate_extract.py` — clean pass, unmodified

```
python tools/validate/validate_extract.py --package work/extract/equator_s1
```
All checks OK: manifest shape, id formats, both scenes' component references resolve, all 12
materials/47 meshes/25 textures parse and cross-reference cleanly, no orphans, and the **real**
reproducibility check (re-invokes the extractor twice against `X:/Escape from Tarkov Arena/...`,
diffs both fresh runs against each other and against the checked-in package) — 0 diffs beyond the
expected `extracted_utc` timestamp field. Numbers match WP1's own handoff exactly (64 objects, 47
meshes, 12 materials, 25 textures, MeshCollider 16 convex 2/non-convex 14, LODGroup 3, Light 1, 6
distinct shaders). No changes made to this script — it was already correct for what it validates.

## 2) `validate_bundle_roundtrip.py` — clean pass after one fix

Command used (S1's actual paths, Scripts scene deliberately not required — see below):
```
python tools/validate/validate_bundle_roundtrip.py \
  --scenes-bundle work/build/s1/arena_equator_scenes.bundle \
  --navmesh-bundle work/build/s1/arena_equator_navmesh.bundle \
  --package work/extract/equator_s1 \
  --material-shaders work/build/s1/material-shaders.json \
  --editor-verify work/build/s1/editor_verify.json \
  --no-require-scripts-scene
```

Result: **`OK — all checks passed (2 non-blocking warning(s))`**. Every GameObject/Mesh/Texture2D/
MeshFilter/MeshRenderer/MeshCollider/BoxCollider/Sphere/CapsuleCollider/Light/LODGroup count
matches the package exactly; convex(2)/non-convex(14) MeshCollider split matches; all 47 meshes'
vertex/submesh counts match by name; all 12 materials carry an O1-compliant `Hidden/ArenaMapPort/`
stand-in shader matching `material-shaders.json`; no null LODGroup renderer slot; both bundles'
every SerializedFile reports `target_platform=19` (StandaloneWindows64) / `unity_version=2022.3.43f1`;
the NavMesh bundle has exactly 1 `NavMeshData` asset; `editor_verify.json`'s per-scene counts match
the package for both real scenes (`Arena_Equator`, `Arena_Equator_Lighting`).

The 2 WARNs, investigated rather than dismissed:
- **`Material count in bundle (13) exceeds package (12)`** — independently listed every `Material`
  object in the bundle via a fresh UnityPy script (not the validator's own code): the 13th is
  literally named `Default-Skybox` (Unity's own built-in per-scene default), confirming — not just
  assuming — WP2's own hypothesis in `docs/progress/wp2.md`. Correctly a WARN, not a defect.
- **`none of the 5 SS D EFT components found`** — real, honest, and expected: see item 5 below. This
  is the direct consequence of the still-open Scripts-scene blocker `docs/progress/s1-finish.md`
  documents (§2, the `AddComponent<LevelSettings>()` "has not finished compilation yet" wall,
  exhaustively diagnosed there across 7 mitigation attempts, none successful) — this run was built
  with `-skipScriptsScene true`, so there genuinely is no Scripts scene in this bundle to find
  components in. `--no-require-scripts-scene` is the correct flag for validating *this* build; a
  real S2/full build must be validated without that flag (default `--require-scripts-scene`, which
  still hard-fails on this — confirmed, see below).

### Fix 1 — `validate_editor_verify()` ignored `--require-scripts-scene` (found and fixed this pass)

**Evidence of the bug**: ran the validator with `--no-require-scripts-scene` first (before any fix)
and it still hard-**FAILED** with `editor_verify.json covers every expected scene (missing:
['Arena_Equator_Scripts'])`, even though the CLI flag's own help text says its purpose is exactly
"fail if none of the 5 SS D EFT MonoBehaviours are found (default: on)" — i.e. the flag is meant to
control precisely this class of check. Root cause: `validate_editor_verify()`
(`tools/validate/validate_bundle_roundtrip.py`, was line 571) unconditionally added
`"Arena_Equator_Scripts"` to the required-scene set regardless of the flag; the flag was only ever
plumbed into the separate `validate_eft_scripts()` (bundle-MonoBehaviour) check, not this one. Two
checks nominally gated by the same flag disagreed with each other — a real inconsistency, not
intended behavior (nothing in SS B.WP6/§G.S1 says the editor_verify scene-coverage check should be
stricter than the bundle-MonoBehaviour check it's cross-checking against).

**Fix**: `validate_editor_verify()` now takes `require_scripts_scene: bool = True`; only adds
`Arena_Equator_Scripts` to the required set when true, and emits an explicit WARN (not silence) when
false and the scene is genuinely absent, naming exactly why. Default behavior (flag omitted, i.e. a
real S2/full-build validation) is **unchanged** — still hard-fails if the Scripts scene is missing.
Re-verified: running the exact same command *without* `--no-require-scripts-scene` still correctly
reports the Scripts-scene-related failures (both the bundle-MonoBehaviour WARN-turned-FAIL and the
editor_verify FAIL) — the fix only changes behavior when the caller explicitly opts out.

## 3) `validate_preset.py --scenes-bundle work/build/s1/arena_equator_scenes.bundle`

**Baseline run (before any fix), captured as evidence**:
```
[validate_preset] 4 check(s) FAILED:
  - preset rcid 'Arena_Equator_Scripts' matches a scene name inside arena_equator_scenes.bundle (available: ['Arena_Equator', 'Arena_Equator_Lighting'])
  - preset rcid 'Arena_Equator_DesignStuff' matches a scene name inside arena_equator_scenes.bundle (available: ['Arena_Equator', 'Arena_Equator_Lighting'])
  - preset rcid 'Arena_Equator_TeamFight' matches a scene name inside arena_equator_scenes.bundle (available: ['Arena_Equator', 'Arena_Equator_Lighting'])
  - preset rcid 'Arena_Equator_TeamFight_DesignStuff' matches a scene name inside arena_equator_scenes.bundle (available: ['Arena_Equator', 'Arena_Equator_Lighting'])
```
Every field-level `ScenesPreset` check (ServerName, `_activeSceneName`, `ChildPresets`,
`_scenesResourceKeys` × 6, `ScenesGuids`, `m_Script` PPtr identity/path_id, the `m_Container`
rename) passed cleanly — only the scene cross-check failed, and only for the 4 scenes S1's own
scope (§E.1: only `level51`/`level55` extracted for S1) never produced.

**Judgment (per the task's own instruction to "judge this sensibly and explain")**: this is exactly
the situation flagged in the work order — the preset itself is correctly built for the *full* 6-scene
design (all 6 rcids present, correctly ordered, correctly pathed), but S1 is a deliberate partial
slice that never built 4 of those 6 scenes. A validator that hard-fails here cannot distinguish
"intentional slice" from "a real typo/regression that silently dropped a scene" — both look
identical from the bundle's contents alone. The right fix is not to weaken the check (a full/S2 build
still needs every rcid to resolve, or the raid genuinely cannot start — WP3's own progress doc
`docs/progress/wp3.md` documents exactly this failure mode for a bad container-key rename) but to
make the "this is a known partial build" fact an explicit, opt-in caller decision.

**Fix 2**: added `--allow-missing-scenes` (default off). When passed, a rcid with no matching scene
in `--scenes-bundle` becomes a WARN naming the rcid and the bundle's actual contents, not a FAIL; a
rcid that *is* present is still asserted to match exactly (this doesn't weaken typo detection for
scenes that do exist). Also added an explicit summary line
(`N/6 rcids present [...], M absent [...]`) so a human doesn't have to count WARN lines. Re-run with
the flag:
```
[validate_preset] WARN: preset rcid 'Arena_Equator_Scripts' ... -- absent, tolerated under --allow-missing-scenes
[validate_preset]  ok : preset rcid 'Arena_Equator' matches a scene name inside arena_equator_scenes.bundle ...
[validate_preset]  ok : preset rcid 'Arena_Equator_Lighting' matches a scene name inside arena_equator_scenes.bundle ...
[validate_preset] WARN: preset rcid 'Arena_Equator_DesignStuff' ... -- absent, tolerated under --allow-missing-scenes
[validate_preset] WARN: preset rcid 'Arena_Equator_TeamFight' ... -- absent, tolerated under --allow-missing-scenes
[validate_preset] WARN: preset rcid 'Arena_Equator_TeamFight_DesignStuff' ... -- absent, tolerated under --allow-missing-scenes
[validate_preset] scene cross-check summary: 2/6 rcids present ['Arena_Equator', 'Arena_Equator_Lighting'], 4 absent [...] (absent ones tolerated: --allow-missing-scenes)

[validate_preset] OK -- all checks passed for D:\ArenaMapPort\work\build\preset\arena_equator_preset.bundle
```

**Confirmed the default (strict) behavior is unchanged** — re-ran without the flag afterward,
still the same 4 FAILs as the baseline, byte-for-byte identical messages. The fix is additive and
opt-in only; a real full-build validation run (no flag) still catches every missing-scene case as a
hard failure, exactly as before.

## 4) Independent spot-check — 3 random meshes' vertex counts vs package

Picked 3 mesh ids at random from `work/extract/equator_s1/manifest.json`'s 47 entries (seeded,
reproducible pick: `c311ef98e9d984f0` "metal", `b860454570c06085`
"plywood_wall_01_decals_LOD0", `f1b2c2ff50d943a7` "left_door_shadow_LOD1"), then wrote a
standalone script (not importing `validate_bundle_roundtrip.py` or any of its helpers) that loads
`work/build/s1/arena_equator_scenes.bundle` with UnityPy directly and reads each `Mesh` object's own
`m_VertexData.m_VertexCount` / `len(m_SubMeshes)`:

```
c311ef98e9d984f0 (metal): expected vc=28 sm=1  got vc=28 sm=1  -> OK
b860454570c06085 (plywood_wall_01_decals_LOD0): expected vc=283 sm=3  got vc=283 sm=3  -> OK
f1b2c2ff50d943a7 (left_door_shadow_LOD1): expected vc=23 sm=1  got vc=23 sm=1  -> OK
```
3/3 exact match, including the one deliberately-picked multi-submesh mesh (3 submeshes) — confirms
`MeshImporter.cs`'s vertex/submesh round trip independently of the shipped validator's own logic.

## 5) Spot-check — Scripts-scene MonoBehaviours' `m_Script` identity + serialized data

**This check cannot pass, and is reported as a genuine gap, not silently skipped or waved through.**
Wrote a standalone UnityPy script (again, not reusing the validator) to enumerate every
`MonoBehaviour` in `work/build/s1/arena_equator_scenes.bundle`, resolve each one's `m_Script` PPtr to
its embedded `MonoScript` (class/namespace/assembly), and match against the 5 §D types
(`LevelSettings`, `LocationScene`, `BotZone`, `LocationExportInfo`, `SpawnPointMarker`/`SpawnPoint`):

```
Total MonoBehaviour objects in bundle: 0
MonoBehaviours matching SS D 5-type set: 0
scene entries in container: ['Assets/ArenaImport/Scenes/Arena_Equator.unity',
                              'Assets/ArenaImport/Scenes/Arena_Equator_Lighting.unity']
```

Zero `MonoBehaviour` objects of *any* kind exist in this bundle, and only the two content scenes are
present — there is no `Arena_Equator_Scripts` scene in this build at all. This is fully consistent
with, and independently confirms, `docs/progress/s1-finish.md`'s own account: the bundle in
`work/build/s1/` was produced by the `-skipScriptsScene true` diagnostic run (§"What WAS achieved
despite the blocker"), because `AuthorScriptsScene.Build`'s very first `AddComponent<LevelSettings>()`
call reproducibly fails with `"Script LevelSettings has not finished compilation yet"` in this exact
Unity Editor build when `LevelSettings` comes from a precompiled, non-"Any Platform"
`Assembly-CSharp.dll` Plugin DLL — a second, separate blocker from the (now-fixed) SIGSEGV, diagnosed
across 7 independent mitigation attempts (timing, cache, platform flags, API choice, tick-based
retry, isolation, rename-vs-remove), none of which succeeded. **Item 4/§D field-value verification
therefore remains blocked on the same lead-decision item `s1-finish.md` already flags**
(typetree-stub cloning for just the 5 §D types, instead of referencing `Assembly-CSharp.dll` directly
for `AddComponent` purposes) — nothing in this validation pass changes that recommendation or
resolves it; this section exists to confirm, independently and from a fresh angle, that the gap is
real and exactly as previously reported, not to re-attempt fixing it (out of this task's scope).

## Files touched

- `tools/validate/validate_bundle_roundtrip.py` — Fix 1 (`validate_editor_verify()` now honors
  `--require-scripts-scene`/`--no-require-scripts-scene` consistently with `validate_eft_scripts()`).
- `tools/validate/validate_preset.py` — Fix 2 (new `--allow-missing-scenes` opt-in flag for
  `validate_scenes_cross_check()`; default/strict behavior unchanged).
- No WP1/WP2/WP3 code touched. No `work/`/`dist/` output written by this pass (the spot-check
  scripts ran from the session scratchpad, not committed anywhere in the repo).

## Handoff

- All three validators are green for the real S1 deliverables, using the flags documented above
  (`--no-require-scripts-scene` for `validate_bundle_roundtrip.py`, `--allow-missing-scenes` for
  `validate_preset.py`) — both flags exist specifically to validate an intentionally partial S1
  slice without weakening what either validator catches for a real S2/full build (re-confirmed: both
  default to the original strict behavior).
- The one real, substantive gap is unchanged from `docs/progress/s1-finish.md`: the Scripts scene is
  not yet authored in any non-diagnostic build, blocked on a Unity Editor `AddComponent` limitation
  that needs the lead's architecture decision (typetree-stub cloning for the 5 §D types) before it
  can be resolved and re-validated.
