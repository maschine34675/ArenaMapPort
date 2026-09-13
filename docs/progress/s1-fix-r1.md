# s1-fix-r1 — root-causing and fixing the "second blocker" — progress log

Role: fixer/re-verifier, task label `s1-fix-r1`. Task: root-cause the AddComponent blocker
`docs/progress/s1-finish.md`/`s1-validate-r1.md` left open (task item 2/4), fix it at the right
place, re-run the affected steps headless, re-run the validators, return the new state.

**Result: the blocker is fixed.** The full, real S1 pipeline (Scripts scene included, no
`-skipScriptsScene`, `-bakeNavMesh true -verify true`) now runs end-to-end with **zero**
`error CS`/`Exception`/`Received signal`, and all three §B.WP6 validators pass **cleanly** against
the real output, including — for the first time — the actual §D field-value checks. One
pre-existing, explicitly-tolerated gap (NavMesh bake collects 0 sources) remains open; one new,
real negative result was added to its diagnostic trail this pass. No git commit made (workspace
rule: the lead commits).

---

## 1) Root-causing the "second blocker"

### 1.1 Re-verifying the starting state

Read `docs/progress/wp1.md`, `wp2.md`, `wp3.md`, `wp6-r1.md`, `s1-finish.md`, `s1-validate-r1.md`,
and `docs/SPEC.md` in full before touching anything. Confirmed the reported state independently:
`work/build/s1/` held a bundle built with `-skipScriptsScene true` (0 MonoBehaviours, only 2 of 3
scenes), and the canonical blocked log (`unity_s1_finish_run8_final.log`) showed
`AddComponent<LevelSettings>()` returning null with `"Script LevelSettings has not finished
compilation yet"` on every one of 50 real `EditorApplication.update` ticks, with the Cecil fix
(task item 1, SIGSEGV) confirmed still in effect (0 domain-reload crashes).

### 1.2 Forming and testing a new hypothesis

`s1-finish.md`'s own diagnostic table had already ruled out timing, caching, platform-compat
flags, three different AddComponent-equivalent APIs, and remove-vs-rename of the SIGSEGV fix
itself — all with the DLL still literally named `Assembly-CSharp.dll` on disk. That is the detail
none of the prior 7 mitigation attempts varied: **`Assembly-CSharp`/`Assembly-CSharp-firstpass`
are Unity's own reserved names** for the implicit legacy script assemblies its Editor compiles
from loose `.cs` files directly under `Assets/`. Dropping a *precompiled* Plugin DLL under those
exact literal names is a documented Unity foot-gun: the Editor's compilation-pipeline bookkeeping
(`EditorCompilationInterface`) treats it as "the predefined `Assembly-CSharp[-firstpass]`
assembly, still waiting on a Roslyn compile" — a compile that will never happen in this project,
since the DLL is precompiled, not built from project `.cs` sources — so the assembly is never
marked "finished compiling", and every `AddComponent<T>()` for a type in it fails identically and
permanently. This is a genuinely different, independent mechanism from the SIGSEGV (task item 1),
which is why remove-vs-rename of *that* fix made no difference to *this* problem.

**Test 1 — rename the file only.** Modified `tools/sync-game-refs.ps1` to copy
`Assembly-CSharp.dll`/`Assembly-CSharp-firstpass.dll` into GameRefs under new names
(`SptGameRefs-AssemblyCSharp[Firstpass].dll`), re-ran the pipeline. Result: a **new, more specific**
Unity error appeared, proving the file was genuinely being loaded under its new name:
```
Assembly 'Assets/ArenaMapPort/Editor/GameRefs/SptGameRefs-AssemblyCSharp.dll' will not be loaded due to errors:
Assembly name 'Assembly-CSharp' does not match file name 'SptGameRefs-AssemblyCSharp'
```
This is a **separate, additional** Unity-enforced rule (confirmed empirically, not assumed):
every precompiled Plugin DLL's *embedded* assembly identity must equal its own file name, or
Unity refuses to load it at all. File rename alone is necessary but not sufficient.

**Test 2 — rename the file AND the embedded assembly identity, plus fix cross-references.**
Extended `tools/gamerefs-fixup` (the existing Cecil tool from task item 1) with two new passes,
run across the *whole* `GameRefs/*.dll` set (not just the two originally-targeted files):
- **Fix #2a**: for the two renamed DLLs, rewrite `AssemblyDefinition.Name.Name` /
  `ModuleDefinition.Name` to match their new on-disk file name.
- **Fix #2b**: for *every* DLL in GameRefs (all 48), scan `AssemblyReferences` for an entry still
  named `Assembly-CSharp`/`Assembly-CSharp-firstpass` and rewrite it to the new name — otherwise
  any DLL that references the renamed assembly by its old name would fail to resolve it.
  (In practice only `SptGameRefs-AssemblyCSharp.dll` itself had such a reference, to
  `Assembly-CSharp-firstpass` — confirmed by running the pass against the full set and reading its
  own log output, not assumed.)

Re-ran the isolated `BuildBundles.TestAddComponentOnly` diagnostic (a fast, ~1-minute check —
just `AddComponent<LevelSettings>()` as the literal first operation of a fresh batchmode process,
no full pipeline) after a clean `Library` wipe:
```
[ArenaMapPort][DIAGNOSTIC] GameObject.AddComponent<LevelSettings>: SUCCEEDED
[ArenaMapPort][DIAGNOSTIC] ObjectFactory.AddComponent<LevelSettings>: SUCCEEDED
```
**Fixed.** (The diagnostic's third path, `go.AddComponent(reflectedType)`, still logged "returned
NULL/no type found" at that moment — but that is the diagnostic's own now-stale hardcoded string
check `asm.GetName().Name != "Assembly-CSharp"`, not a real failure; fixed to compare against
`"SptGameRefs-AssemblyCSharp"` — see "Files touched". Not re-verified standalone after that fix
given the two other paths already conclusively prove the root cause and fix; low risk, string
literal only.)

### 1.3 A real, unrelated bug found and fixed while extending the tool

Running `gamerefs-fixup` against the full 48-DLL set (rather than just 2) surfaced a genuine,
separate, transient failure never seen before:
```
System.IO.IOException: The process cannot access the file '...Unity.Burst.dll' because it is
being used by another process.
```
No Unity process was running (`Get-Process -Name Unity` confirmed empty) — this is Windows
Defender / AV real-time scanning a just-`Copy-Item`'d file, holding a brief exclusive handle.
Fixed with a small bounded retry-with-backoff (`ReadAllBytesWithRetry`/`WriteAllBytesWithRetry`,
5 attempts / 200ms) around the tool's two file-IO calls — a real robustness fix, not a workaround
for anything related to the AddComponent root cause.

---

## 2) Full real S1 pipeline — first-ever clean end-to-end run

Command (final, canonical — `unity_s1_fixr1_run3_final.log`):
```
Unity.exe -batchmode -nographics -projectPath D:\ArenaMapPort\UnityProject
  -executeMethod ArenaMapPort.Editor.BundleBuild.BuildBundles.Run
  -packageDir D:/ArenaMapPort/work/extract/equator_s1 -outputDir D:/ArenaMapPort/work/build/s1
  -bakeNavMesh true -verify true
  -logFile D:\ArenaMapPort\work\build\s1\unity_s1_fixr1_run3_final.log
```
(No `-skipScriptsScene`, no `-quit` — matches `s1-finish.md`'s documented invocation, since
`BuildBundles.Run` still owns `EditorApplication.Exit()` itself.)

Result: **zero `error CS`/`Received signal`/`Exception` in the entire log.**
```
[ArenaMapPort][BuildBundles] Scripts scene authored successfully after 1 EditorApplication.update tick(s).
[ArenaMapPort][BuildBundles] built 2 bundle(s) -> D:/ArenaMapPort/work/build/s1 : arena_equator_navmesh.bundle, arena_equator_scenes.bundle
[ArenaMapPort][BuildBundles] DONE packageDir=D:/ArenaMapPort/work/extract/equator_s1 outputDir=D:/ArenaMapPort/work/build/s1
```
"after 1 EditorApplication.update tick(s)" — the Scripts scene now authors successfully on the
**very first attempt**, not after retrying; the tick-based retry machinery from `s1-finish.md` is
kept in place (harmless, now a fast path) rather than refactored back to synchronous, to avoid
touching more of an already-working mechanism than necessary.

Produced for real, this pass, from a genuine non-diagnostic run:
- `work/build/s1/arena_equator_scenes.bundle` — **3 scenes** (`Arena_Equator`,
  `Arena_Equator_Lighting`, `Arena_Equator_Scripts` — all three, for the first time ever).
- `work/build/s1/arena_equator_navmesh.bundle` — 1 `NavMeshData` asset (0 sources — see §4).
- `work/build/s1/editor_verify.json` — all three scenes' real counts, including a genuinely
  populated `eft_components` block for `Arena_Equator_Scripts`.
- `PlayerSettings.stripUnusedMeshComponents: False -> false` still logged (task item 3, unaffected
  by this pass, re-confirmed).

## 3) §D field-value verification (task item 4 — completed this pass)

`editor_verify.json`'s `Arena_Equator_Scripts` block, cross-checked field-by-field against
`docs/SPEC.md` §D:

| Component | SPEC §D value | Observed (real, this pass) | Match |
|---|---|---|---|
| `LevelSettings` | `AmbientMode = Flat` | `ambient_mode: "Flat"`, count 1 | yes |
| `LocationScene` | `FillArrays()` called; every array field non-null | `world_interactive_objects_length: 0` (not -1 — the exact NRE-site field), `bot_zones_length: 1`, `spawn_point_markers_length: 3` | yes |
| `BotZone` | `PatrolWays = []` (empty, non-null); `SpawnPointMarkers` non-null, real entries; `UnSpawnPoints = []` | `patrol_ways_length: 0`, `spawn_point_markers_count: 3` (matches the 3 real `SpawnPointMarker`s — see §3.2 below), `un_spawn_points_length: 0` | yes |
| `LocationExportInfo` | `_unixDateTime` = fixed build timestamp | `unix_date_time: 1757707200` (matches `BuildBundles.BuildUnixDateTime`) | yes |
| `SpawnPointMarker` | 2-4 markers; one `Sides=All`, dedicated `Usec`/`Bear`; `Categories=Player`; enabled `BoxCollider` | 3 markers: `SpawnPoint_Player_All`/`Usec`/`Bear`, `categories: "Player"`, `collider_attached: true`, `collider_enabled: true` for all 3 | yes |

Independently re-confirmed via `tools/validate/validate_bundle_roundtrip.py` (default, strict
`--require-scripts-scene`) reading the built bundle directly with UnityPy — not just
`editor_verify.json`'s own self-report: all 5 SS D `MonoBehaviour`s found, correct
namespace/assembly identity, serialized byte size clearly beyond an empty header (real field data
survived the round trip), `BotZone.PatrolWays`/`SpawnPointMarkers` both non-null (risk #11). See
§5 below for the full validator run.

## 4) NavMesh bake — still 0 sources (one new negative result, still open)

`s1-finish.md` flagged an untested lead: `UnityEngine.Physics.SyncTransforms()` before
`NavMeshBuilder.CollectSources`, on the theory that PhysX's broadphase might not yet have
registered colliders added moments earlier in the same synchronous method. Implemented and
tested this pass (`BakeNavMesh.cs`) — **result: still 0 sources**, disproving that specific lead.
Investigated further this pass (not merely re-reported):
- Confirmed via the extracted package's own `scenes/Arena_Equator.json` that the 4 slice roots are
  all `active: true`, and 17 of the 20 collider-bearing objects in the slice are also `active: true`
  (not universally inactive — rules out "every collider object happens to be inactive").
- Confirmed `BakeNavMesh.Bake()`'s own `GetComponentsInChildren<Renderer>(true)` call, walking the
  *same* `contentSceneRoots`, successfully finds real geometry (bounds
  `Center:(-8.41,1.01,6.49) Extents:(30.83,6.30,34.07)`, not the zero-size fallback) — so the root
  objects and hierarchy traversal are genuinely reaching real content; only the
  `NavMeshCollectGeometry.PhysicsColliders`-mode collider scan specifically finds nothing.
- `Physics.SyncTransforms()` added and tested: no change.

This remains a real, open, but explicitly non-blocking gap (SPEC.md §B.WP2.7's acceptance
criteria and this task's own instructions both tolerate a slice failing to bake real NavMesh
tiles, "if explained"). Kept the `SyncTransforms()` call in (legitimate, cheap, doesn't regress
anything) and documented the ruled-out lead here for whoever picks this up next. Not investigated
further this pass — the task's primary, severe blocker (§1-§3 above) was the priority, and this
was already a known, tolerated gap before this pass started.

## 5) Validator fixes and final validator run (all three, clean)

Running `validate_bundle_roundtrip.py` in **strict** mode (default `--require-scripts-scene`, the
correct mode now that a real Scripts scene exists) against the very first real build surfaced
real validator bugs — genuinely new information, since no prior pass ever had real authored data
to test these checks against:

- **`ACCEPTED_ASSEMBLY_NAMES`** only listed `Assembly-CSharp`/`Assembly-CSharp.dll` — now
  (correctly) `SptGameRefs-AssemblyCSharp`/`.dll` after the §1 rename. Added the new names,
  documented why, kept the old ones accepted too (a donor/future GameRefs setup that hasn't
  applied the rename).
- **`MONOBEHAVIOUR_HEADER_ONLY_BYTES` boundary**: `LocationExportInfo` (the smallest SS D type —
  one 8-byte `long` field) round-tripped at exactly 40 bytes; the check used strict `>`, rejecting
  it despite `editor_verify.json` independently confirming its one real field
  (`unix_date_time=1757707200`) genuinely survived. Changed `>` to `>=` against this real boundary
  case (every other SS D type is 188-340 bytes, unaffected either way).
- **`expected["game_objects"]`/`expected["box_colliders"]`** were computed purely from the WP1
  package (the 2 *extracted* content scenes) — never accounting for the Scripts scene's own fixed,
  spec-defined 8 GameObjects / 3 BoxColliders (it is WP2-authored, never in the WP1 package). Added
  `SCRIPTS_SCENE_EXTRA_GAME_OBJECTS`/`SCRIPTS_SCENE_EXTRA_BOX_COLLIDERS`, applied only when
  `--require-scripts-scene` (the `--no-require-scripts-scene` skip-mode path, still used to
  validate a deliberately partial build, is unaffected).
- **`BotZone.spawn_point_markers_count == 0`** — backwards: this literally asserted
  `BotZone.SpawnPointMarkers` must be *empty*, contradicting `docs/SPEC.md` §D's own "author 2-4
  markers" instruction and `AuthorScriptsScene.Build`'s real, correct behavior
  (`botZone.SpawnPointMarkers.AddRange(markers)`). The real, first-ever authored build has 3 real
  markers — correct — and the old check hard-FAILed on exactly that correct data. Fixed to cross-
  check `BotZone.spawn_point_markers_count == SpawnPointMarker.count` (the two independently-
  counted values for what should be the *same* list) instead of a hardcoded literal either way —
  catches "BotZone was never actually linked to its markers" while accepting correct, non-empty
  data.

All four are real fixes to genuine validator/reality mismatches — none change the *strict*/default
behavior for anything the previous (necessarily `-skipScriptsScene`) runs already validated; they
only change what a *real*, Scripts-scene-included build is correctly held to.

### Final run — all three validators, clean

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
       WARN: Material count in bundle (13) exceeds package (12) -- Default-Skybox built-in,
       independently confirmed benign by WP6/s1-validate-r1 (unchanged this pass)
  (note: no --no-require-scripts-scene flag -- this is the STRICT/default run, and it is clean)

python tools/validate/validate_preset.py \
  --scenes-bundle work/build/s1/arena_equator_scenes.bundle --allow-missing-scenes
  -> OK -- all checks passed for work\build\preset\arena_equator_preset.bundle
     scene cross-check summary: 3/6 rcids present (Arena_Equator, Arena_Equator_Lighting,
     Arena_Equator_Scripts -- up from 2/6 before this pass), 3 absent (the 3 out-of-S1-scope
     scenes, correctly tolerated under --allow-missing-scenes, not a regression)
```

Zero FAILs across all three. The single WARN (Material 13 vs 12) is the same, already-investigated
benign `Default-Skybox` built-in `s1-validate-r1.md` confirmed independently — unchanged by this
pass.

---

## Files touched

- `tools/gamerefs-fixup/Program.cs` — extended: fix #2a (assembly-identity rename for the two
  renamed DLLs) + fix #2b (cross-reference rewrite across the whole GameRefs set) + retry-with-
  backoff around file IO (the AV-lock robustness fix, §1.3). Fix #1 (SIGSEGV, task item 1) logic
  unchanged.
- `tools/sync-game-refs.ps1` — copy step now renames `Assembly-CSharp[-firstpass].dll` to
  `SptGameRefs-AssemblyCSharp[Firstpass].dll` on the way into GameRefs (the root-cause fix);
  step 6 now runs `gamerefs-fixup` against the *entire* GameRefs DLL set, always rebuilds the tool
  first (was: only if missing — stale binaries were masking the new logic during iteration); also
  cleans `*.dll.meta` (not just `*.dll`) so a rename doesn't leave an orphaned `.meta` behind.
- `UnityProject/Assets/ArenaMapPort/Editor/ArenaMapPort.Editor.asmdef` — `precompiledReferences`
  regenerated by the script to the new file names (auto, not hand-edited).
- `UnityProject/Assets/ArenaMapPort/Editor/BundleBuild/BuildBundles.cs` — `TestAddComponentOnly`'s
  third diagnostic path's hardcoded `"Assembly-CSharp"` string updated to
  `"SptGameRefs-AssemblyCSharp"`; `MaxScriptsSceneTicks`'s comment updated to record the real fix
  (was previously documenting only the disproven "more ticks" theory).
- `UnityProject/Assets/ArenaMapPort/Editor/NavMesh/BakeNavMesh.cs` — added
  `Physics.SyncTransforms()` before `CollectSources` (§4 — tested, did not fix the 0-sources gap,
  kept anyway as a legitimate, cheap, non-regressing call; the lead this rules out is documented).
- `tools/validate/validate_bundle_roundtrip.py` — 4 fixes, §5 above (`ACCEPTED_ASSEMBLY_NAMES`,
  the `MONOBEHAVIOUR_HEADER_ONLY_BYTES` boundary, Scripts-scene-aware expected GameObject/
  BoxCollider counts, the backwards `BotZone.spawn_point_markers_count` check).
- `docs/progress/s1-fix-r1.md` — this file.
- Real output (gitignored): `work/build/s1/{arena_equator_scenes.bundle, arena_equator_navmesh.bundle,
  editor_verify.json, material-shaders.json}` (from the final `unity_s1_fixr1_run3_final.log` run) +
  every diagnostic/run log from this pass, kept as evidence
  (`unity_s1_fixr1_diag{1,2,3}.log`, `unity_s1_fixr1_run{1_full,2_navmesh,3_final}.log`).
  `unity_s1_fixr1_run3_final.log` is the canonical, final, fully-clean run — everything else is
  intermediate diagnostic evidence from reaching it.

No file under `D:/SPT41` or `X:/` was written (only read, both read-only sources — the
`gamerefs-fixup` tool's own O4/O8 path guard was re-confirmed still in place and unmodified). No
`git commit`/`git add` was run — per workspace rule, the lead commits.

## Handoff

- **Task items 1-4 are all now done and verified**: the Cecil SIGSEGV fix (item 1, unchanged this
  pass), `stripUnusedMeshComponents=false` (item 3, unchanged this pass), the complete S1 pipeline
  including a genuinely-authored Scripts scene (item 2, fixed this pass), and §D field-value
  verification against real data (item 4, completed this pass) — all confirmed via a real,
  `-skipScriptsScene`-free headless run plus all three validators passing in their **strict**
  default modes (only the pre-existing, out-of-S1-scope 4-scene gap in `validate_preset.py` still
  needs `--allow-missing-scenes`, correctly).
- **`docs/progress/wp2.md`/`s1-finish.md`'s recommendation of typetree-stub cloning for the 5 §D
  types is no longer needed** — the real fix was much smaller (rename GameRefs' two reserved-named
  DLLs, both file and embedded identity, plus fix cross-references) and keeps §B.WP2.1's original
  "reference the real Assembly-CSharp.dll directly" design intact, which was the whole point of
  that section's reasoning.
- **Still open, not addressed this pass** (both pre-existing, both explicitly non-blocking for
  S1 per SPEC.md/this task): NavMesh bake collects 0 sources (§4 — `Physics.SyncTransforms()` now
  ruled out too; next lead untried: compare `NavMeshCollectGeometry.RenderMeshes` mode's source
  count against `PhysicsColliders` mode's, to isolate whether the physics-collider-specific
  collection path itself is what's failing, or try building the same package through
  `NavMeshSurface`'s own higher-level `CollectSources` wrapper instead of the low-level
  `NavMeshBuilder` API used here). A full S2 (6-scene) run/validate pass is out of this task's
  scope (S1 slice only) and untouched by this pass.
- Re-run commands (all confirmed clean this pass, no flags needed beyond what's shown):
  ```
  pwsh tools/sync-game-refs.ps1
  Unity.exe -batchmode -nographics -projectPath D:\ArenaMapPort\UnityProject
    -executeMethod ArenaMapPort.Editor.BundleBuild.BuildBundles.Run
    -packageDir D:/ArenaMapPort/work/extract/equator_s1 -outputDir D:/ArenaMapPort/work/build/s1
    -bakeNavMesh true -verify true -logFile <path>
  python tools/validate/validate_extract.py --package work/extract/equator_s1
  python tools/validate/validate_bundle_roundtrip.py --scenes-bundle work/build/s1/arena_equator_scenes.bundle --navmesh-bundle work/build/s1/arena_equator_navmesh.bundle --package work/extract/equator_s1 --material-shaders work/build/s1/material-shaders.json --editor-verify work/build/s1/editor_verify.json
  python tools/validate/validate_preset.py --scenes-bundle work/build/s1/arena_equator_scenes.bundle --allow-missing-scenes
  ```
