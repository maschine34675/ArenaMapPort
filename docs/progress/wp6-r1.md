# WP6 (validation) — r1 — progress log

Status: **both validators implemented, both run for real. `validate_extract.py` passes cleanly
against WP1's S1 package (including a real, independent reproducibility re-run of the
extractor). `validate_bundle_roundtrip.py` correctly and cleanly FAILS, because WP2 has not
produced anything for it to validate yet** (`work/build/s1/` has no `arena_equator_scenes.bundle`,
no `arena_equator_navmesh.bundle`, and no `editor_verify.json` — consistent with WP2's own
handoff, which reports the S1 pipeline blocked on a native Unity Editor crash before it reaches
`AuthorScriptsScene`/`VerifyBuild`). No WP1/WP2 code was modified. No git commit made (O5).

## What was implemented

- `tools/validate/validate_extract.py` — SS B.WP6 table row 1. Validates a WP1 package
  (`--package`, default `work/extract/equator_s1` per O7): manifest shape, `errors == []`, id
  format, every scene's component references (mesh/material ids, `parent_id`, `LODGroup`
  renderer_object_ids resolving to an object that actually carries a renderer — the exact
  precondition SS B.WP2.5's two-pass `SceneAssembler` depends on), every `materials/*.json`'s
  shader/texture references, an **independent** re-parse of every `meshes/*.mesh.bin` against SS
  C.2's byte layout (not by importing `tools/extract/mesh_bin.py` — a second decoder written from
  the spec table), every `textures/*.png`'s dimensions via Pillow, an orphan-reference check, and
  — unless `--skip-reproducibility` — a **real** reproducibility check: it re-invokes
  `tools/extract/extract_equator.py` as a subprocess **twice**, with the exact `--scenes`/
  `--slice-roots` recovered from the package's own `manifest.json` (via `extract_equator.py`'s own
  `SCENE_REGISTRY`, imported read-only), diffs the two fresh runs against each other (pure
  determinism), and diffs one fresh run against the checked-in package (package-freshness/drift).
- `tools/validate/validate_bundle_roundtrip.py` — SS B.WP6 table row 2 + O1. Validates
  `work/build/s1/arena_equator_scenes.bundle` (+ optional navmesh bundle + `editor_verify.json`)
  with UnityPy: target-platform header (`StandaloneWindows64`, risk #12), GameObject/Mesh/
  Texture2D/MeshFilter/MeshRenderer/MeshCollider/BoxCollider/SphereCollider/CapsuleCollider/
  Light/LODGroup counts against the package (exact match; Material/Shader allowed to exceed by a
  couple for Unity's own built-in per-scene defaults, over-count WARNs rather than FAILs, under-
  count still FAILs), per-mesh vertex_count/submesh_count by name (`MeshImporter.cs` names assets
  by `mesh_id`, confirmed by reading that file), per-material shader name is the **O1-prefixed**
  stand-in (`"Hidden/ArenaMapPort/" + original`) whose suffix matches the package's declared
  original shader — never a bare/unprefixed name, cross-checked against
  `material-shaders.json`, convex/non-convex `MeshCollider` aggregate counts, "no `LODGroup` has a
  null renderer slot", the NavMesh bundle's presence/tile sanity (optional for S1 unless
  `--require-navmesh`), the 5 SS D EFT `MonoBehaviour`s' `m_Script→MonoScript` identity
  (namespace/assembly, re-confirmed this pass by direct grep of the decompiled source — see
  below) and serialized byte size, risk #11's `BotZone.PatrolWays`/`SpawnPointMarkers`
  null-vs-empty distinction where a typetree is available, and a full cross-check of
  `editor_verify.json` against the package (including the exact `LocationScene.Awake` NRE-site
  field and risk #11 again, this time via `VerifyBuild.cs`'s own live-read numbers).

Both scripts: standalone (neither imports the other or any WP1/WP2 code), exit 0 with an "OK"
summary or exit 1 with a specific, itemized list of `FAIL:`/`WARN:` lines — never a bare
stack trace, per SS B.WP6's own acceptance criterion for validators.

## Fact-finding this pass (before writing any code)

Read `docs/SPEC.md` in full (§0/§A–§H, ~1300 lines) and both WP1's and WP2's handoffs/progress
docs, then read real data before writing checks against it:
`work/extract/equator_s1/manifest.json` + a scene JSON + two material JSONs (confirmed the real
field shapes: `MeshRenderer.material_ids`, `LODGroup.lods[].renderer_object_ids` as **local scene
ids** not content-hash ids, `MeshCollider.convex`/`is_trigger`), `work/build/s1/`'s actual
contents (17 Unity run logs + `material-shaders.json`, confirmed by direct `find` that **no
`.bundle` and no `editor_verify.json` exist anywhere under `work/build/s1/`** — the only real
`.bundle` files in the whole repo are WP3's preset ones under `work/build/preset/`), all 7 of
WP2's Editor C# files under `Editor/**` (confirmed `mesh.name = meshId`/`mat.name = matId` in
`MeshImporter.cs`/`MaterialImporter.cs` — load-bearing for how `validate_bundle_roundtrip.py`
matches bundle objects back to package ids by name), and `VerifyBuild.cs` in full (its exact
`editor_verify.json` JSON shape, reproduced field-for-field in `validate_editor_verify()`).
Re-confirmed SS D's five component namespaces directly against
`D:/SPT41/Development/_decompiled` (`grep -n "^namespace\|^public class"` on each of
`LevelSettings.cs`/`LocationScene.cs`/`BotZone.cs`/`EFT/LocationExportInfo.cs`/
`EFT.Game.Spawning/SpawnPointMarker.cs`) rather than trusting the spec prose alone — confirmed
`LevelSettings`/`LocationScene`/`BotZone` are genuinely in the **global** namespace (no
`namespace` statement in the file), matching what WP2's C# already assumed (`global::LocationScene`,
`global::BotZone`).

## `validate_extract.py` — real run against `work/extract/equator_s1`

```
[validate_extract] validating D:\ArenaMapPort\work\extract\equator_s1 (schema_version=1)
[validate_extract]  ok : manifest.json has all SS C.1 required top-level keys
[validate_extract]  ok : manifest.json.errors == []
...
[validate_extract]  ok : scenes/Arena_Equator.json: 63 objects, all component references resolve
[validate_extract]  ok : scenes/Arena_Equator_Lighting.json: 1 objects, all component references resolve
[validate_extract]  ok : all 12 materials/*.json parse, shader fields match manifest, every non-null tex_envs texture_id resolves
[validate_extract]  ok : all 47 meshes/*.mesh.bin independently re-parse per SS C.2, vertex/submesh counts match manifest, index buffer length matches submesh totals exactly
[validate_extract]  ok : all 25 textures/*.png open cleanly, dimensions match manifest
[validate_extract]  ok : no orphaned mesh/material/texture entries
[validate_extract] reproducibility: re-running extractor twice into <tmp> (scenes=[51, 55], slice={51: [...], 55: [1261]}) ...
[validate_extract]  ok : reproducibility run #1 of extract_equator.py exits 0
[validate_extract]  ok : reproducibility run #2 of extract_equator.py exits 0
[validate_extract]  ok : two independent fresh extractor runs produce a byte-identical package -- 0 diff(s)
[validate_extract]  ok : the checked-in --package matches what the current extractor actually produces from today's source data -- 0 diff(s)

  scenes: 2, objects: 64
  meshes: 47, materials: 12, textures: 25
  MeshRenderer: 35, SkinnedMeshRenderer: 0, MeshCollider: 16 (convex 2 / non-convex 14), BoxCollider: 4,
  SphereCollider: 0, CapsuleCollider: 0, Light: 1, LODGroup: 3, ReflectionProbe: 0
  distinct shaders: 6, normal-map textures: 9

[validate_extract] OK -- all checks passed for D:\ArenaMapPort\work\extract\equator_s1, no warnings
```

**Every number matches WP1's own handoff exactly** (64 objects, 47/12/25, convex 2/non-convex 14,
6 shaders). The reproducibility check is a **real** one, not a trust exercise: it re-invoked
`X:/Escape from Tarkov Arena/EscapeFromTarkovArena_Data` twice (source drive is reachable from
this session, ~19s/run) and confirmed byte-for-byte: two fresh runs match each other, and a fresh
run matches the checked-in package (after normalizing `source.extracted_utc`, the one field that
legitimately differs run-to-run by design). Independently, a full `diff -rq` of a fresh run
against the checked-in package showed **only** `manifest.json` differing, and only in that one
timestamp field — every mesh/material/texture/scene file byte-identical.

## `validate_bundle_roundtrip.py` — real run against `work/build/s1`

```
[validate_bundle_roundtrip] package expectations loaded: 64 objects, 47 meshes, 12 materials, 25 textures
[validate_bundle_roundtrip]  ok : material-shaders.json found and parses

=== arena_equator_scenes.bundle ===
[validate_bundle_roundtrip] FAIL: arena_equator_scenes.bundle exists at D:\ArenaMapPort\work\build\s1\arena_equator_scenes.bundle
[validate_bundle_roundtrip] FAIL: GameObject/Mesh/Material/collider/LODGroup/EFT-component checks run (SKIPPED -- no bundle to read)

=== arena_equator_navmesh.bundle ===
[validate_bundle_roundtrip] WARN: arena_equator_navmesh.bundle not found -- SS G.S1 allows skipping the
NavMesh bake for the S1 slice, treating as informational, not a failure

=== editor_verify.json cross-check ===
[validate_bundle_roundtrip] FAIL: editor_verify.json exists at D:\ArenaMapPort\work\build\s1\editor_verify.json
(WP2's VerifyBuild.cs has not produced one)

[validate_bundle_roundtrip] 3 check(s) FAILED, 1 warning(s)
```

This is the **correct, honest** result given the actual repo state (confirmed by `find . -iname
"*.bundle"` — only `work/build/preset/arena_equator_preset.bundle` and its donor copy exist
anywhere in the repo; `find . -iname editor_verify.json` — zero matches). It is not this script
failing to work; there is nothing yet for it to validate. See "Defect / gap found" below.

## Self-verification (since there is no real bundle to run the bulk of the logic against)

`validate_bundle_roundtrip.py`'s object-count/mesh/material/collider/LODGroup/EFT-MonoBehaviour
logic (~400 lines) has never executed against real WP2 output, and per **O6 this session must
not run Unity** (only a WP2 agent may, and never two at once) — so it cannot be exercised
end-to-end here. To avoid shipping ~400 lines of unverified logic, every non-trivial check
function was self-tested this pass against hand-built fixtures exercising both the "correct" path
and every specific defect it claims to catch, run from this session's scratchpad (not part of the
delivered tool, not committed anywhere):

- **`validate_target_platform`/`iter_serialized_files`** — run for real (not mocked) against the
  one real bundle available in the repo, WP3's `work/build/preset/arena_equator_preset.bundle`:
  correctly read `target_platform=19` (StandaloneWindows64), `unity_version=2022.3.43f1`.
- **`validate_eft_scripts`**'s MonoScript-resolution mechanics — also run for real against that
  same preset bundle (it has one real, live `MonoBehaviour`→`MonoScript` pair, for
  `EFT.ScenesPreset`): resolved cleanly, correctly reported "none of the 5 SS D components found"
  (true — it isn't the Scripts scene) with no crash, proving the PPtr/path_id resolution code
  works against genuine serialized data.
- **`validate_scenes_bundle_contents`** (mesh/material/collider/LODGroup logic) — 9 fixture cases
  against mocked UnityPy objects: the clean/good case (0 failures); a mismatched mesh
  vertex_count; a mismatched submesh_count; a material shader **missing the O1 prefix entirely**
  (the exact bug O1 exists to prevent — confirmed caught); a stand-in shader correctly prefixed
  but pointing at the **wrong** original shader name; a null `LODGroup` renderer slot; a
  convex/non-convex `MeshCollider` count drift; a `GameObject` under-count (must FAIL); a
  `Material` over-count (must WARN only, not FAIL, since WP2's own prior partial-pipeline run
  legitimately saw a benign +1 from a scene's built-in Skybox default). All 9 behaved as intended.
- **`validate_eft_scripts`** (namespace/assembly/byte-size/risk-#11 logic) — 8 fixture cases:
  clean all-5-present; risk #11 with `PatrolWays` key missing from the typetree; risk #11 with
  `SpawnPointMarkers` explicitly `null`; a wrong `MonoScript` namespace; a wrong assembly name; a
  header-only byte size (the empty-round-trip failure mode SS G.S1 is actually worried about) on
  all 5 simultaneously; none-of-5-present with `--require-scripts-scene` (must FAIL); same with
  the flag off (must WARN only). All 8 behaved as intended.
- **`validate_editor_verify`** — 9 fixture cases built from the *real* S1 package's actual
  per-scene object/component counts (63 objects in `Arena_Equator`, 1 in `Arena_Equator_Lighting`,
  44 `MeshFilter`/35 `MeshRenderer`/16 `MeshCollider`/4 `BoxCollider`/3 `LODGroup` in
  `Arena_Equator`): clean pass; risk #11 `PatrolWays`; risk #11 `SpawnPointMarkers`; the
  `LocationScene.Awake` NRE-site field (`world_interactive_objects_length == -1`); a renderer with
  a null material slot; a null `LODGroup` renderer entry; a `game_object_count` drift; a missing
  Player spawn marker; a missing scene entry outright. All 9 behaved as intended.

37 fixture cases total, all passed on the first complete run of the self-test suite (after fixing
two bugs in the self-test harness itself, not in the validators — see below). This is not a
substitute for running the real thing once WP2 unblocks, and the log says so explicitly; it is the
strongest verification available without violating O6.

## Defect / gap found (reported per the work order: "report defects with evidence")

**WP2 has produced no `arena_equator_scenes.bundle`, no `arena_equator_navmesh.bundle`, and no
`editor_verify.json` at `work/build/s1/`.** This is not a surprise — WP2's own handoff and
`docs/progress/wp2.md` are explicit that the pipeline is blocked on a reproducible native Unity
Editor SIGSEGV (`MonoScriptInfoScraper::ScanForSourceGeneratedMonoScriptInfo`) the moment
`Assembly-CSharp.dll` is loaded as an Editor-compatible precompiled plugin, and that the one real
bundle it *did* build during diagnosis (a 17.8MB `arena_equator_scenes.bundle`, with
`AuthorScriptsScene`/`Assembly-CSharp.dll` deliberately removed to isolate the crash) was not kept
as a `work/build/s1/` deliverable — the session restored everything to the final, still-blocked,
spec-compliant state before finishing. Evidence this pass, independent of WP2's own account:

```
$ find . -iname "*.bundle"
./dist/BepInEx/plugins/ArenaMapPort/bundles/arena_equator_preset.bundle
./work/build/preset/arena_equator_preset.bundle
./work/build/preset/_donor_develop_preset.bundle
$ find . -iname "editor_verify.json"
(no output)
$ ls work/build/s1/
material-shaders.json  unity_s1_run1.log  unity_s1_run10.log  unity_s1_run10.rc  ... (run logs only)
```

`validate_bundle_roundtrip.py` reports this correctly and specifically (not a crash, not a silent
skip) and is otherwise ready to run the moment a real bundle exists — no changes needed to the
validator itself. This blocks S1's own pass condition (§G.S1: "a human look ... showing
recognizable, correctly-shaped geometry with no console errors") regardless of what WP6 does; it
is an architecture-level Unity Editor problem WP2's own doc already flags for the lead's decision
(IL-preprocess `Assembly-CSharp.dll`, typetree-stub-clone just the 5 SS D types, try a different
Editor patch version, or get a debugger-level repro), not something a validator can route around.

## Two self-inflicted bugs found and fixed while writing the self-test harness (not in the shipped validators)

Both were in the throwaway scratch test scripts, not in `tools/validate/*.py`:
- The first manual reproducibility test wrote its output to bash's MSYS root (`/wp6_repro1`)
  instead of the actual scratchpad, because an unset `$SCRATCH` shell variable silently expanded
  to empty inside a quoted path — bash tool calls don't share shell state, so a variable set in
  one call is gone in the next. Fixed by using the literal resolved path in each call and by
  making `validate_extract.py` itself use `tempfile.mkdtemp()` rather than any caller-supplied
  path fragment, so this class of mistake can't leak into the shipped tool.
- `UnityPy.load()` (really just Python's own `open()`) does not understand git-bash's `/d/...`
  MSYS-style absolute paths on this machine — it silently produced a 0-object environment instead
  of erroring. Confirmed by direct A/B test (`/d/ArenaMapPort/...` → 0 objects, `D:/ArenaMapPort/...`
  or a relative path from the shell's actual cwd → 3 objects, same file). Both shipped validators'
  default paths are hardcoded `D:/ArenaMapPort/...` (Windows-style) specifically because of this,
  not just for consistency with `ArenaPaths.cs`'s own constants.

## Not done / honest limitations

- `validate_bundle_roundtrip.py`'s bundle-reading logic is verified by construction (self-tests)
  and by partial real-data runs (target-platform + MonoScript resolution against the real preset
  bundle), but **has never read a real `arena_equator_scenes.bundle`** — none exists. Re-run it
  the moment WP2 produces one; expect it to either pass cleanly or surface a real, specific defect
  (not a crash) given how it behaved on every fixture and every piece of real data available.
- `validate_preset.py` (WP3's own validator, pre-existing, not touched this pass) was read but not
  re-run or re-reviewed in depth — out of this work order's scope (WP6 r1's task was specifically
  `validate_extract.py` + `validate_bundle_roundtrip.py`).
- `validate_server_db.py`/`validate_spawn_authoring.py` (the other two SS B.WP6 table rows) are
  not in scope for this pass either — WP5's `dist/SPT_Runtime/.../db/base.json` and the exported
  `Arena_Equator_Scripts.unity` scene are separate future WP6 passes per the table.
- The NavMesh-bundle tile-count check is a documented best-effort byte-size proxy, not a real
  `tile_count` field read — no baked `NavMeshData` sample was ever available (this pass or any
  prior one) to confirm the actual typetree field name for tile data.

## Handoff

- Run `python tools/validate/validate_extract.py` any time WP1's S1 (or, once it exists, full)
  package changes — takes ~20s including the live reproducibility re-run, needs `X:/Escape from
  Tarkov Arena/EscapeFromTarkovArena_Data` reachable (auto-skips that one check with a clear
  message if not, everything else still runs).
- Run `python tools/validate/validate_bundle_roundtrip.py` the moment WP2 produces a real
  `work/build/s1/arena_equator_scenes.bundle` (+ ideally `editor_verify.json`) — no flags needed
  for the default S1 paths; `--require-navmesh` / `--no-require-scripts-scene` exist for when the
  navmesh bake is deliberately skipped or the Scripts-scene gate is being tested independently.
  Right now it will correctly and specifically report what's missing rather than passing or
  crashing — that is the intended behavior until WP2 unblocks, not a bug in this deliverable.
- Both scripts are fully self-contained (no shared imports between them, no dependency on the
  other's having run first) and never write outside `tools/validate/` — no `work/`, no `dist/`, no
  git commands were run by this session.
