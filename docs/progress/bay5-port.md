# bay5-port — Arena_Bay5 (TeamFight) ported as `arena_bay5` (progress log)

Role: builder, task label `bay5-port`. Task: port Arena_Bay5 (TeamFight) through the generalized
multi-map pipeline (`docs/progress/multimap-generalize.md`) per `PLAN.md` §2-§5 with the critic
amendments, alongside the already-working `arena_equator`, without touching Equator's shipped
bundles. No git commit made (workspace rule: the lead commits).

**Result: done.** `arena_bay5` is extracted (8 scenes, 52,332 objects), built (Unity, fresh NavMesh
bake), patched (MonoScript identity + preset), registered server-side (`db/arena_bay5/base.json`),
and staged into `dist/` alongside Equator. All mandatory `tools/build_all.ps1` validators pass.
Equator's 3 dist bundle hashes are unchanged from the given baseline. Full command log below.

---

## 0) Critic-amendment corrections applied before any extraction

1. **Locale name** — `tools/maps/bay5.json` and `ArenaMapPort.Shared/ArenaMapDescriptor.cs`'s
   `ArenaMapRegistry.Bay5` both had `LocaleName: "Arena: Bay5 (spike)"` (no space) from the prior
   generalization pass — corrected to the task's exact spec, `"Arena: Bay 5 (spike)"` (with space).
   Verified no test/tool hardcodes the old string (`grep -rn "Bay5 (spike)"` → only those 2 hits);
   `dotnet test ArenaMapPort.Server.Tests` still 48/48 after the edit.
2. **Icon position** — `IconX=150, IconY=830` was already set correctly by the prior pass (below
   Equator's `150,760`); reused as-is, confirmed non-overlapping.
3. **SpawnPointMarker anchor assertion (critic amendment #6)** — verified BEFORE authoring anything,
   via a direct per-file UnityPy breakdown (not assumed): the source game has 30
   `EFT.Game.Spawning.SpawnPointMarker` objects total, but only **20** live in
   `Arena_Bay5_TeamFight_DesignStuff` (level88, **imported** this pass); the other 10
   (`"p1 (N)"`-named) live in `Arena_Bay5_TeamFight_AI` (level78, **excluded** — bots/AI out of
   scope for both maps per `multimap-generalize.md`). `extract_bay5.py`'s own manifest-build step
   asserts this explicitly and fails loudly (`sys.exit(2)`) if `anchors_reference` comes back empty
   or shifts off `level88` — this is exactly the "previous failure mode was silent" case the
   amendment warns about, now a hard, non-silent check. Confirmed by the real extraction run: `OK:
   20 SpawnPointMarker anchor(s) resolved from ['level88']`.
4. **Duplicate-BuildSettings-index resolver** — `extract_bay5.py`'s `resolve_scene_indices()` reads
   `BuildSettings.scenes` fresh from `globalgamemanagers` and, for any of the 8 target scenes with
   more than one entry, prefers the index whose own `sharedassets<N>.assets` is larger (the
   non-stripped copy) rather than hardcoding indices — even though none of the 8 extracted scenes
   actually has a duplicate this pass (only the excluded `TeamFight_AI`/`Sound` do), so this is
   cheap insurance for a future extraction, not dead code exercised by luck.

## 1) Descriptor

`tools/maps/bay5.json` / `ArenaMapRegistry.Bay5` (both already existed from the multimap-generalize
pass, corrected per §0 above):

| Field | Value |
|---|---|
| Key | `arena_bay5` |
| MongoId | `f4f851a5fee201ad20a257dc` |
| Locale name | `Arena: Bay 5 (spike)` |
| Icon | `150, 830` (Equator: `150, 760` — no overlap) |
| Scripts scene | `Arena_Bay5_Scripts` (authored fresh) |
| All scene names (rcid load order) | `Arena_Bay5_Scripts, Arena_Bay5_Main, Arena_Bay5_Light, Arena_Bay5_DesignStuff, Arena_Bay5_TeamFight, Arena_Bay5_TeamFight_DesignStuff, Arena_Bay5_DEPOT, Arena_Bay5_WAREHOUSE, Arena_Bay5_HANGAR` |
| Infiltration name | `ArenaBay5` |
| `db_base_json_path` | `arena_bay5/base.json` |

Scene order mirrors Equator's own SPEC.md §B.WP3-step-3 convention (Scripts first/Single, then main
geometry, then the lighting-equivalent scene, then DesignStuff, then the TeamFight-mode overlay
scenes) with the 3 extra sub-building scenes (DEPOT/WAREHOUSE/HANGAR) appended after. Not
playtest-verified this pass (no in-game session run — see §9); per `ArenaLightingRepairs.cs`'s own
already-generalized design, scene RenderSettings/fog are never consumed for lighting anyway (the
Scripts scene always authors fixed values, and the client's own Harmony patch overrides them again
at raid start), so load order has no correctness bearing on lighting — only a cosmetic one, to be
confirmed by playtest.

## 2) Extraction — `tools/extract/extract_bay5.py` (new)

New script, reusing `tools/extract/extract_equator.py`'s engine (scene-graph walk,
mesh/material/texture dedup+write, spawn-anchor computation, the same exhaustive
`COMPONENT_BUILDERS` allowlist) by importing it as a library and monkey-patching its
`SCENE_REGISTRY` global for the duration of the run — the same technique `extract_equator.py`'s own
`compute_spawn_anchors()` already uses on `census.BASE`. **`extract_equator.py` itself is not
edited** — Equator's byte-reproducibility guarantee is untouched.

8 scenes extracted (idx → name): `84→Main, 83→Light, 81→DesignStuff, 89→TeamFight,
88→TeamFight_DesignStuff, 80→DEPOT, 86→WAREHOUSE, 82→HANGAR`. Excluded: `Background(79)` (0
walkable colliders, doubles footprint), `Scripts(85)`/`Scripts_Winter(247)` (authored fresh),
`TeamFight_AI(78)`/`Sound(188)` (bots/audio out of scope), `TeamFight_Culling(90)` (PerfectCulling
data, out of scope). Winter geometry confirmed absent from the TeamFight scene set (matches PLAN.md
§2).

External file dependencies: no `analysis/scene_externals.json` entry exists for Bay5 (that file is
Equator/AutoService-only, `analysis/` is read-only) — `compute_scene_externals()` computes each of
the 8 scenes' externals directly via UnityPy's own `SerializedFile.externals` instead. Found **38**
unique external files (independently reproducing the session's own earlier scout sample of 39,
which included the "Library/unity default resources" built-in sentinel this script correctly
filters out) — 28 of them shared Arena prop-pool files outside Bay5's own 8, including
`sharedassets51.assets` (Equator's own) and `sharedassets62.assets` (AutoService's), confirming Bay5
draws from the same shared prop library the other ported maps use.

Command:
```
python tools/extract/extract_bay5.py --output work/extract/bay5
```
Result (`work/logs/bay5-extract-20260915.log`): **DONE. 52,332 objects, 3,315 meshes, 381
materials, 826 textures. 0 errors, 187 warnings.** Duration: 05:12:42–05:16:06 UTC (**~3m24s**).
Warnings are all benign (`MeshFilter: no mesh assigned` — placeholder/marker objects, same pattern
Equator's own extraction produces; 13 texture notes for Cubemap/Texture3D real-data-not-placeholder
handling, same mechanism already proven for Equator).

**Known limitation, verified benign**: 8 `ArenaMonitorHolder` GameObjects in
`Arena_Bay5_TeamFight_DesignStuff` are `RectTransform`-rooted (a UI/Canvas hierarchy, presumably a
match-info display prop) rather than plain-`Transform`-rooted — `build_scene_graph()` only walks
plain `Transform` objects, so these 8 are silently absent from the extracted scene (not mis-parented
or mis-positioned — genuinely absent). Confirmed via direct inspection: all 8 are **leaf** nodes (0
children), so nothing collision/visual-relevant is lost underneath them. Matches this project's
existing "Cam2 screen-effect guards"/UI-out-of-scope precedent for Equator.

## 3) `validate_extract.py` generalization (critic amendment #3)

`tools/validate/validate_extract.py`'s disabled-component ground-truth check and reproducibility
re-run both used to hardcode `import extract_equator as _ex` — meaning, unmodified, they would have
silently found 0 of Bay5's scene names in Equator's own `SCENE_REGISTRY` and either produced
misleading empty-`expected` comparisons (disabled-count check) or refused to run at all
(reproducibility). Fixed via `extractor_script_for(manifest)`: reads
`manifest.source.extractor_script` (a field every extractor now writes) and dynamically imports
*that* module for its `SCENE_REGISTRY`, falling back to `extract_equator.py` for older packages
that predate the field — so Equator's own behavior is unchanged. `extract_bay5.py` exposes a static
`SCENE_REGISTRY` module attribute (alias of its `BAY5_SCENE_NAMES`) for exactly this. `run_extractor()`
omits `--scenes`/`--slice-roots` for `extract_bay5.py` (which has neither flag — a fixed,
unparameterized 8-scene set) while keeping them for `extract_equator.py`. Verified both ways:
Equator's own package still validates identically (`OK`, same 2 pre-existing warnings) after the
change.

**Also found and fixed, same file**: one real over-strict check. `validate_extract.py`'s LODGroup
cross-check treated ANY `renderer_object_id` pointing at an object with no `MeshRenderer`/
`SkinnedMeshRenderer` component as a hard `FAIL` — but Bay5's `Arena_Bay5_Main` genuinely has one
such case in the *source game's own data*: `Land_Cruiser_close`'s LOD0/LOD1 both reference its
`exhause_particles` child, whose real Arena renderer is a `ParticleSystemRenderer` — a type outside
`COMPONENT_BUILDERS`' exhaustive allowlist by design (critic amendment #4: gameplay/particle
renderers are already dropped, no new skip logic needed). `SceneAssembler.AttachLodGroup` already
treats an unresolved LOD renderer id as a graceful, logged, non-fatal omission (its own code
comment: "intentionally omits unresolved ids rather than inserting null entries") — confirmed live
during the actual Unity build (§5). Downgraded this one case from `FAIL` to `WARN` to match the
pipeline's actual (already-correct) runtime behavior; the genuinely-dangling-reference case (an id
not present in the scene at all) is still a hard `FAIL`, unchanged. Re-verified against Equator's
package: zero occurrences there, so this is a no-op for Equator (still `OK`, same 2 warnings).

Standalone run (`--skip-reproducibility`, before the full `build_all.ps1` pass):
```
python tools/validate/validate_extract.py --package work/extract/bay5 --skip-reproducibility
```
`OK -- all checks passed for work\extract\bay5 (4 non-blocking warning(s))` — the manifest-warnings
note, the cubemap/Texture3D notes, the one Land_Cruiser LOD note (×1 after the fix, was ×2/FAIL
before), and a note that 21 emitted mesh ids are never referenced by any scene (same
dedup-pool-holds-a-few-orphans pattern Equator's own package also has). **Disabled-component counts
matched Arena exactly for all 8 scenes** (recomputed fresh from Arena's own `level<N>` files, not
the hardcoded fallback table):

| Scene | Disabled counts (type: count) |
|---|---|
| Arena_Bay5_Main | LODGroup:1, MeshCollider:16, MeshRenderer:302 |
| Arena_Bay5_Light | BoxCollider:23, MeshRenderer:28 |
| Arena_Bay5_DesignStuff | BoxCollider:336, MeshCollider:6, MeshRenderer:3 |
| Arena_Bay5_TeamFight | BoxCollider:8, MeshCollider:5, MeshRenderer:28 |
| Arena_Bay5_TeamFight_DesignStuff | MeshRenderer:243 |
| Arena_Bay5_DEPOT | MeshCollider:1, MeshRenderer:41 |
| Arena_Bay5_WAREHOUSE | LODGroup:2, MeshCollider:13, MeshRenderer:41 |
| Arena_Bay5_HANGAR | MeshRenderer:16 |

Confirms critic amendment #3 in full — disabled-component volume is "modest" as the amendment
predicted (Scripts/Scripts_Winter excluded), and every one of these round-trips correctly per the
`m_Enabled` fix already shipped for Equator (no new code needed, per PLAN.md §2's own prediction).

## 3b) Per-map authored fog color (task item 3's explicit "Bay5-appropriate color from Arena's RenderSettings")

`AuthorScriptsScene.cs` authored a single hardcoded `fogColor = (0.62, 0.66, 0.70)` for every map's
Scripts scene (Equator's own real `Arena_Equator_Scripts` fog color, reused unconditionally) —
`multimap-generalize.md` explicitly deferred giving Bay5 its own value as "optional future
enhancement, not required this pass" for *that* pass, but this pass's own task instruction is
explicit: author Bay5's Scripts scene with "Bay5-appropriate color from Arena's RenderSettings".
Implemented as a new per-map descriptor field rather than a second hardcoded literal:

- `tools/maps/equator.json` / `tools/maps/bay5.json` gained `"fog_color": [r, g, b]` — Equator's is
  `[0.62, 0.66, 0.70]` (byte-identical to the old literal); Bay5's is
  `[0.132285476, 0.147871643, 0.137986973]`, read directly off Arena's own
  `Arena_Bay5_Scripts` (level85) `RenderSettings.m_FogColor` (`m_FogMode=3`/ExponentialSquared
  there too, corroborating the task's own "mode ExponentialSquared" instruction) — a dark
  grey-green, not an arbitrary guess.
- `ArenaPaths.cs`'s `ArenaMapDescriptorData` gained `float[] fog_color` +
  `FogColorOrDefault()` (falls back to Equator's literal for any older map JSON missing the field);
  `EquatorDefault()` sets it explicitly too, so an unparameterized invocation is unaffected.
- `AuthorScriptsScene.cs` reads `ArenaPaths.Map.FogColorOrDefault()` instead of the bare literal.
  `fogMode=ExponentialSquared`/`fogDensity=0` stay unchanged (map-agnostic, matching the task's own
  spec) — only the color is now per-map.

Scope: **authored-scene value only** (what round-trips into the shipped bundle, correct-by-
construction per the surrounding code's own stated goal). `ArenaLightingRepairs.cs`'s runtime
Harmony patch still forces the SAME Equator-tuned color for both maps at raid start when
`FogDensity` is raised above its 0 default — left untouched, matching the multimap-generalize
pass's own explicit, still-current decision that a runtime per-map re-tune is playtest-driven
future work (§9 item 6), not something to guess at offline.

Rebuilt Bay5 end to end after this fix (extraction unaffected — this is Unity-Editor-only code) and
confirmed the real value round-tripped into the shipped bundle by reading it back with UnityPy:
`fogMode=3 fogDensity=0.0 fogColor={'r': 0.132285475730896, 'g': 0.14787164330482483, 'b':
0.13798697292804718, 'a': 1.0}`. NavMesh bake reproduced identical triangulation (270,734
vertices/134,766 triangles — confirms this change touched only `LevelSettings`, not geometry). Full
`build_all.ps1` re-run: **OK** again (same 4/1/0 warning counts as §8b, all checks passed) — see §8b's
numbers, updated in §8c for the final (fog-color-corrected) bundle hash.

## 4) Unity build

Two attempts; documenting both since the first is a real, instructive failure, not swept under the
rug.

**Attempt 1 (crash, environmental, not a code defect)**: `build_all.ps1 -MapKey arena_bay5
-MapDescriptor tools/maps/bay5.json -PackageDir work/extract/bay5 -OutputDir work/build/bay5`.
Pre-launch check confirmed no `Temp/UnityLockfile` and no `Unity.exe` process. Unity crashed
immediately on startup: `"It looks like another Unity instance is running with this project open"`
— but every Unity-family process (Unity.exe, ILPP.Runner, Licensing.Client, PackageManager,
AutoQuitter, CrashHandler) had start timestamps identical to this same launch, and the crash
handler was left hung (no one to dismiss its dialog in a headless session) rather than a second,
independently-started editor holding the project. Treated as a one-off lock-acquisition race (this
workspace's `D:\` is line-mapped, not a plain local NTFS volume, which is documented elsewhere in
this project as a source of file-locking flakiness), not a live conflict — no other session's
work was at risk. Cleaned up (`Stop-Process` on the 6 hung processes, confirmed all exited,
`Temp/UnityLockfile` removed) before retrying, per the hard rule that only one agent may touch
Unity at a time.

**Attempt 2 (Scripts-scene authoring failure, a real bug in this pass's own invocation)**: retried
with `-SkipGameRefsSync` (step 1 had already completed cleanly in attempt 1). Failed differently:
`Scripts-scene authoring never succeeded after 50 EditorApplication.update ticks. ... [ArenaPaths]
-mapDescriptor 'tools\maps\bay5.json' does not exist`. Root cause: **this pass's own PowerShell
invocation passed `-MapDescriptor` as a path relative to the repo root, but Unity's own working
directory during batchmode execution is not the repo root**, so `File.Exists()` inside
`ArenaPaths.cs` correctly reported it missing — not a bug in any `.cs`/`.py` file, purely an
invocation error. Every earlier pipeline stage (texture/material/mesh import, all 8 content-scene
assemblies) had already completed successfully before this failure, confirming the rest of the
generalized pipeline works.

**Attempt 3 (success)**: same command, `-MapDescriptor D:\ArenaMapPort\tools\maps\bay5.json`
(absolute path). Clean pre-launch state reconfirmed (`Test-Path UnityLockfile` → `False`, 0 Unity
processes) before launching.

```
pwsh tools/build_all.ps1 -MapKey arena_bay5 -MapDescriptor D:\ArenaMapPort\tools\maps\bay5.json `
    -PackageDir D:\ArenaMapPort\work\extract\bay5 -OutputDir D:\ArenaMapPort\work\build\bay5 -SkipGameRefsSync
```

Result: **Unity BuildBundles.Run OK**. Key numbers from the real run (`editor_verify.json` +
Unity's own log, `work/build/bay5/unity_build_all_20260915_072911.log`):

| Metric | Value |
|---|---|
| Content scenes assembled | 8/8, exactly matching the extraction package's object counts |
| Scripts scene (`Arena_Bay5_Scripts`) | 8 GameObjects: 1 `LevelSettings`, 1 `LocationScene`, 1 `BotZone`, 1 `LocationExportInfo`, 3 `SpawnPointMarker` (`SpawnPoint_Player_All/_Usec/_Bear`) — exactly the §D authoring shape, same as Equator |
| Total vertices (8 content scenes) | 14,674,250 |
| NavMesh bake | agent radius 0.30 / height 1.70 / slope 48° / climb 0.38 (SPT's own agent-0 values, same as Equator) — **270,734 vertices, 134,766 triangles** |
| `arena_bay5_scenes.bundle` (pre-identity-patch) | 516,056,973 B |
| `arena_bay5_navmesh.bundle` | 1,448,267 B |
| Land_Cruiser_close LOD0/LOD1 exhaust-particle slot | confirmed gracefully omitted at assembly time (`[ArenaMapPort][SceneAssembler] ... omitted, not nulled`), matching §3's validator finding |
| DesignStuff's ~17 SkinnedMeshRenderer-referencing LOD slots | also gracefully omitted, same mechanism — SkinnedMeshRenderer is `skip_import: True` project-wide (Equator has 506 SkinnedMeshRenderers of its own, same rule) |

## 5) Mandatory MonoScript identity patch (`tools/bundle/patch_monoscript_identity.py`)

```
python tools/bundle/patch_monoscript_identity.py --bundle work/build/bay5/arena_bay5_navmesh.bundle
python tools/bundle/patch_monoscript_identity.py --bundle work/build/bay5/arena_bay5_scenes.bundle
```
- `arena_bay5_navmesh.bundle`: 0 MonoScripts needed patching (no `MonoBehaviour` in a NavMesh
  bundle) — left byte-for-byte untouched.
- `arena_bay5_scenes.bundle`: **5 MonoScript(s) patched** (`LocationScene`, `BotZone`,
  `EFT.Game.Spawning.SpawnPointMarker`, `EFT.LocationExportInfo`, `LevelSettings` — the exact §D
  set), `'SptGameRefs-AssemblyCSharp.dll' -> 'Assembly-CSharp'`, confirmed only inside
  `BuildPlayer-Arena_Bay5_Scripts.sharedAssets` (every one of the 8 extracted content scenes'
  nested files confirmed byte-size-unchanged — they use real BSG components already, nothing to
  patch there). Re-read verification passed (header fields, per-nested-file sizes, UnityFS
  signature/format/compression flags all confirmed unchanged except the expected -75-byte string
  delta). Final size: **506,171,928 B** (down from 516,056,973 B pre-patch — shorter real assembly
  name string).

## 6) Preset bundle

```
python tools/preset/patch_preset.py --map tools/maps/bay5.json
```
`work/build/preset/arena_bay5_preset.bundle`, 7,027 B. Patched fields: 9-entry
`_scenesResourceKeys` (Scripts first, matching §1's table exactly, each `path =
"arena_bay5/arena_bay5_scenes.bundle"`), `ServerName="arena_bay5_tdm"`,
`_activeSceneName="Arena_Bay5_Scripts"`, `ChildPresets=[]`, `ScenesGuids` padded to 9,
`m_Name="arena_bay5.ScenesPreset"`; `AssetBundle` container entry renamed to
`assets/content/locations/_presets/arena_bay5.scenespreset.asset`, `m_Name`/`m_AssetBundleName` →
`maps/arena_bay5_preset.bundle`. Re-read verification passed (`m_Script` PPtr unchanged,
`m_Container` entry present).

## 7) Server registration

```
python tools/server/make_base_json.py --map tools/maps/bay5.json
```
`ArenaMapPort.Server/db/arena_bay5/base.json` (98 keys, from the `factory4_day` donor template).
Spot-checked: `Id="arena_bay5"`, `_Id="f4f851a5fee201ad20a257dc"`,
`Name="Arena: Bay 5 (spike)"`, `Scene={"path":"maps/arena_bay5_preset.bundle",
"rcid":"arena_bay5.scenespreset.asset"}`, `Enabled=true`, `DisabledForScav=true`,
`IconX/Y=150/830`, `waves/BossLocationSpawn/exits=[]`, `SpawnPointParams` absent,
`OpenZones=""`.

`ArenaMapPort.Server.csproj`'s `db\**\*.json` glob picked this file up with **zero build-file
change** (confirmed: it appeared under `dist/SPT_Runtime/user/mods/ArenaMapPort/db/arena_bay5/
base.json` automatically the next time the Server project built, byte-identical to the source —
diffed via `python -m json.tool` both sides).

`dotnet test ArenaMapPort.Server.Tests -c Release`: **48/48 passed** (unchanged count — the
generalization pass already parameterized every relevant test over `ArenaMapRegistry.All`; no new
test needed for Bay5's own registration, it's exercised by the existing `[Theory]`s automatically).

## 8) Client csproj — Bay5 dist-staging target (new, this pass)

`ArenaMapPort.Client.csproj`'s `ArenaMapPortStageWpOutputs` target only listed Equator's 3
bundles + its shader sidecar (`multimap-generalize.md`'s own open item 5: "needs its second,
Bay5-specific `ItemGroup` added once Bay5 has real build outputs to stage — not before"). Added a
second explicit `ItemGroup` (not a wildcard, for the same "keep the exactly-N-files check
meaningful" reason the first `ItemGroup` was already explicit) for
`work/build/bay5/arena_bay5_{scenes,navmesh}.bundle`, `work/build/preset/arena_bay5_preset.bundle`,
and a renaming `<Copy>` for `work/build/bay5/material-shaders.json` →
`dist/.../material-shaders.arena_bay5.json` (matching `ShaderRebind.SidecarFileName`'s actual
naming rule — `map.Key`, i.e. `"arena_bay5"`, not the short `tools/maps/` file stem `"bay5"` the
prose in `multimap-generalize.md` loosely suggested; verified by reading `ShaderRebind.cs`'s own
code, not assumed).

**Found and fixed while doing this**: the first version of this edit put a real project-file bug
past `dotnet build`'s XML parser — a `-- ` em-dash used inside an `<!-- -->` XML comment (twice, one
of them literally `--out`, referring to a CLI flag) is illegal XML (`MSB4025`, "An XML comment
cannot contain '--'"). Caught by actually building (`dotnet build ArenaMapPort.Client -c Debug`,
which skips the Release-only staging target but still parses the whole file) before relying on it
for the real Release build — not assumed correct from a visual read. Fixed by rewording, re-verified
clean via a script scanning every `<!-- -->` block for `--` in its body, then a clean Debug build.

## 8b) Full `build_all.ps1` validator results (the real, non-`--skip-reproducibility` run)

`build_all.ps1`'s own mandatory step 4 re-ran all three validators strict/default (this is the run
that actually gates "safe to copy into dist/", not the earlier standalone §3 spot-check):

- **`validate_extract.py`** (full, including the reproducibility re-run this time): **OK**, 4
  non-blocking warnings (same 4 as §3). Reproducibility: `two independent fresh extractor runs
  (same source, same --fixed-timestamp) produce a byte-identical package — 0 diff(s)` **and**
  `the checked-in --package matches what the current extractor actually produces from today's
  source data — 0 diff(s)`. Both re-invocations used `extract_bay5.py` correctly (confirmed by the
  log itself naming it, not `extract_equator.py`) — the `extractor_script_for()` generalization
  (§3) worked end-to-end, not just in isolation.
- **`validate_bundle_roundtrip.py`**: **OK**, 1 non-blocking warning (`Material count in bundle
  (362) exceeds package (361)` — the built bundle's own +1 is Unity's own implicit default
  material, the same class of harmless discrepancy already documented for Equator, not a package
  duplication). Every count check passed exactly: GameObjects 52,340/52,340, Mesh 3,294/3,294 (21
  correctly excluded as skip_import-only), Texture2D 745/745, Cubemap 20/20, MeshFilter 26,875,
  MeshRenderer 23,307, MeshCollider 9,088 (51 convex/9,037 non-convex), BoxCollider 1,750,
  SphereCollider 140, CapsuleCollider 713, Light 189, LODGroup 6,339 — all in bundle == package.
  Zero null LODGroup renderer slots in the built bundle. 1,106 material texture-slot dimension
  cross-checks passed (no Texture2D assigned to a Cube/3D/2DArray shader property). Per-scene
  disabled-component counts in the **built bundle** matched the **package** exactly for all 8
  scenes (independent confirmation, one level closer to the shipped artifact than §3's
  package-vs-Arena check). `LevelSettings`/`LocationScene` MonoBehaviours confirmed real
  (`Assembly-CSharp` identity, non-trivial serialized size — not just an empty header).
- **`validate_preset.py`**: **OK**. `m_Script` PPtr unchanged and resolves to
  `EFT.ScenesPreset`/`Assembly-CSharp`; exactly 1 `m_Container` entry points at the ScenesPreset
  MonoBehaviour and its key ends with `arena_bay5.scenespreset.asset`; all 9 preset rcids
  cross-checked against the actual scene names inside `arena_bay5_scenes.bundle` — **9/9 present,
  0 absent**.

`[build_all] OK -- build + mandatory identity patch + all validators passed. D:\ArenaMapPort\work\build\bay5 is safe to copy into dist/.`

## 8c) Client/Server Release build, dist staging, final verification

```
dotnet build ArenaMapPort.Client -c Release
dotnet test  ArenaMapPort.Server.Tests -c Release      # 48/48 passed
dotnet build ArenaMapPort.Server -c Release             # Release build last, per instruction
dotnet build ArenaMapPort.Client -c Release              # re-run after the Server rebuild, confirms a stable final dist/
```

`ArenaMapPortStageWpOutputs` (§8's new `ItemGroup`) staged all 6 bundles + both shader sidecars in
one pass, no errors. **`dist/` now holds exactly 12 files** (Equator's pre-existing 7 + Bay5's new
5: 3 bundles, 1 shader sidecar, 1 `db/arena_bay5/base.json` — `ArenaMapPort.Server.csproj`'s
`db\**\*.json` glob picked the new db file up with zero build-file change, as `multimap-
generalize.md` predicted).

**Equator's 3 dist bundle hashes, reconfirmed unchanged** (checked before this pass's first edit —
matched the task's given baseline exactly — and again now, after two separate Client Release
rebuilds sandwiching the Server rebuild):

| File | Size | SHA-256 |
|---|---|---|
| `arena_equator_scenes.bundle` | 308,912,270 B | `adb8c5916da001f068fe345d3c18d167ec74357d003b9853ff3e16df64fb1035` |
| `arena_equator_navmesh.bundle` | 72,842 B | `25d6165c3c2535e4ae3ae77f4d08b4a3963f9addcfb275b546543d51fea10f6b` |
| `arena_equator_preset.bundle` | 6,891 B | `f12cf0bfbff60e5ec299f211253656d87650d63fb8a3a899b957bf4ee06dfe36` |

**Full `dist/` listing, this pass's final state** (after the §3b fog-color fix and its full
rebuild — `arena_bay5_scenes.bundle`'s hash/size superseded the first successful build's
`506,171,928 B` / `8c35a93c...` once the fog fix was applied; everything else is unchanged from
that first build, and the whole `build_all.ps1` pipeline was re-verified green against this final
bundle too, not just re-hashed):

| File | Size (B) | SHA-256 |
|---|---|---|
| `bundles/arena_bay5_navmesh.bundle` | 1,448,267 | `05a9c66f832441406b0a0f262c97ad0a91351ef21b3900e06e3477ad69350e01` |
| `bundles/arena_bay5_preset.bundle` | 7,027 | `d538a6d00e6298486f8ca60d19393113b88afeab5e5499a048fb33694c048e8c` |
| `bundles/arena_bay5_scenes.bundle` | 506,188,951 | `09ec8ff226cd14261e22bbda5b825a184a0dee0b7a330b00a8a8f7cbc61a5fe2` |
| `bundles/arena_equator_navmesh.bundle` | 72,842 | `25d6165c3c2535e4ae3ae77f4d08b4a3963f9addcfb275b546543d51fea10f6b` |
| `bundles/arena_equator_preset.bundle` | 6,891 | `f12cf0bfbff60e5ec299f211253656d87650d63fb8a3a899b957bf4ee06dfe36` |
| `bundles/arena_equator_scenes.bundle` | 308,912,270 | `adb8c5916da001f068fe345d3c18d167ec74357d003b9853ff3e16df64fb1035` |
| `maschine-ArenaMapPort.Client.dll` | 74,240 | `cbd52f90eff4bc3d9e345b358c0a2c82246e72be820a96e5e3f4513f4cbc6b73` |
| `material-shaders.arena_bay5.json` | 50,049 | `05495ce830f8ac4256c3b8cd9a44a4f14b4046405a43f6e3b4d6aab6ffc628ac` |
| `material-shaders.json` | 43,237 | `0724b11c2e97b4b1710384d1498af2a2de061e21d1ab98bc1da711c142b1ad17` |
| `SPT_Runtime/.../db/arena_bay5/base.json` | 10,957 | `8527c5f9bd694f233c718837be3f05081da719ef1cc78ff361395f8896b4a7a9` |
| `SPT_Runtime/.../db/base.json` | 10,968 | `d85aae93540bec143714770c80b95312551964d14898adb0c0391a2b41238a49` |
| `SPT_Runtime/.../maschine-ArenaMapPort.Server.dll` | 24,064 | `ca1e79a7079e33097c8b13df781d546b3582290b4666371ba62676532518c439` |

**Not run this pass**: `tools/deploy-to-spt.ps1` — copying `dist/` into the live `D:/SPT41` install
is explicitly out of this pass's scope (hard rule: never write under `D:\SPT41`); the task's own
instruction 6 asks only for "dist stages all bundles for both maps", not deployment. The lead/user
runs the deploy step when ready.

## 9) Open items / not done this pass

1. **No in-game playtest.** This pass is offline/automated (extraction, Unity batch build,
   validators, `dotnet build`/`dotnet test`) — no SPT server or EFT client was started, consistent
   with the hard rule against writing under `D:\SPT41` and with how Equator's own spike separated
   automated-build passes from playtest passes. The user/lead should run the §7-equivalent in-game
   check PLAN.md's own §7 lists: both maps' map-select buttons appear and don't overlap, Bay5 raid
   starts, player spawns on solid ground, walks the Main/DEPOT/WAREHOUSE/HANGAR footprint,
   Equator→Bay5→Equator map-switch doesn't leave a stale `NavMeshInstaller` cache or wrong
   `ArenaMapRaid.CurrentMap` state, TeamFight `LevelBorder` toggle behaves.
2. **NavMesh water/hazard check (PLAN.md R2, critic amendment #7)** — **the open-water half of this
   was found, fixed and re-baked this pass; see §10 below.** The harbor water plane itself was
   confirmed baked in as walkable ground (not a vague "might be permissive" risk — a concrete,
   ~2,900-tile defect, root-caused and fixed). What's LEFT open, still not checkable offline: the 379
   `NavMeshModifier`/`NavMeshModifierVolume` instances Arena itself authors for finer-grained hazard
   exclusion (ledges, machinery, etc.) still aren't applied by the current bake's empty-markup call,
   and Bay5 has zero legacy `AdditionalNavmeshBlock` fallback markers (unlike Equator's 58) to
   cross-check against. Needs the in-game walk-the-shoreline-and-interior-hazards check PLAN.md's own
   R2 mitigation describes — narrower in scope now than before this pass.
3. **No Background scene** (deferred per PLAN.md §2 — 203 MB / 0 walkable colliders in-footprint).
4. **No TOD/sky, no baked lightmaps** — same non-issue as Equator (0 baked lightmaps in Bay5 either).
5. **Amplify Impostor stand-in shader** (`Hidden/Amplify Impostors/Octahedron Impostor`, 8 materials
   on Bay5 vs 1 on Equator) — same stand-in mechanism already proven for Equator, not independently
   re-verified visually this pass (no in-game session); flagged per PLAN.md R9 for a playtest spot-
   check.
6. **`ArenaLightingRepairs`'s ambient/fog multiplier defaults** are Equator-tuned values reused
   as-is for Bay5's own config section (`"Lighting (arena_bay5)"`) — no Bay5-specific re-tune, per
   the generalization pass's own explicit decision; a playtest may want to adjust them for Bay5's
   different (greyer/darker) source palette.
7. **`tools/validate/validate_infiltration_name.py`** (the cross-assembly `Infiltration` string
   drift-checker) still not built — both `AuthorScriptsScene.cs`'s authored value and
   `SpawnInfiltrationRepairs.cs`'s runtime-read value were confirmed to agree by direct code
   inspection for both maps, but no automated check exists yet.

## 10) Verification fix-ups

A verification pass (2026-09-15, after this doc's §0-9 were written) reported six defects. Each was
independently re-verified against the live repo/bundles before any fix — findings below.

### 10a) [HIGH, CONFIRMED+FIXED] Fresh NavMesh bake included the open-water plane as walkable

**Verified independently** with UnityPy directly against the (pre-fix) `arena_bay5_navmesh.bundle`
then in `dist/`: `NavMeshData.m_SourceBounds` center=(726.246, 3.394, -1144.945),
extent=(3003.385, 51.469, 3455.968), 63,685 tiles — vs. `arena_equator_navmesh.bundle`'s 22 tiles,
extent (59.04, 42.69, 74.29). Traced to `Arena_Bay5_Main`'s `Plane` GameObject: world position
(726.246, -8.650, -1144.945) — computed by walking its Transform's parent chain, not just reading
its (non-root) local position — scale (599.677, 1.0, 690.194), `m_Layer=4` (Unity's built-in
"Water" layer, confirmed via this project's own `TagManager.asset`), `MeshCollider`
`m_Enabled=true m_IsTrigger=false m_Convex=false`. The Plane's world X/Z matches the NavMesh
bounds' center X/Z, and `scale * 5` (Unity's default Plane half-extent) reproduces the reported
X/Z extents within 0.2%. Independently confirmed this is the ONLY GameObject on layer 4 anywhere in
`arena_bay5_scenes.bundle` with an enabled non-trigger collider (so the fix below has no other
target and no collateral risk), and that Equator's own scenes bundle has zero layer-4 objects at
all (the fix is a no-op for Equator even hypothetically — it was never rebuilt).

**Fix** — `UnityProject/Assets/ArenaMapPort/Editor/NavMesh/BakeNavMesh.cs`:
1. Excluded the Water layer from `NavMeshBuilder.CollectSources`'s `layerMask` (was `~0`, i.e. every
   layer; now `~0 & ~(1 << waterLayer)`, resolved via `LayerMask.NameToLayer("Water")`).
2. A **second issue found while verifying the first attempt**: `bounds` (the volume passed to
   `BuildNavMeshData`) is computed in a SEPARATE loop over every `Renderer` in `geometryRoots`,
   independent of the filtered `sources` list — excluding the Water layer from the source mask alone
   left the water plane's own (still-enabled) `MeshRenderer` inflating `bounds` to its full
   footprint. Confirmed by re-baking after fix 1 alone: sources correctly dropped to exclude the
   water collider, but `m_SourceBounds` was byte-for-byte unchanged from the original broken value.
   Applied the same Water-layer exclusion to the renderer-collection loop.

**Rebuild**: `pwsh tools/build_all.ps1 -MapKey arena_bay5 -MapDescriptor tools/maps/bay5.json
-PackageDir work/extract/bay5 -OutputDir work/build/bay5 -SkipGameRefsSync` (extraction/import
untouched — reused the existing `work/extract/bay5` package; only the navmesh bake and its
downstream scenes/navmesh bundle rebuild were affected). Full pipeline: Unity build → mandatory
`patch_monoscript_identity.py` (scenes bundle: 5 MonoScripts patched, the expected §D set, every
other nested file confirmed byte-identical before/after) → all 4 validators (`validate_extract.py`,
`validate_bundle_roundtrip.py`, `validate_preset.py`, plus the new `validate_map_descriptors.py`
from §10c) — **all passed**. Re-staged into `dist/` via `dotnet build
ArenaMapPort.Client -c Release`.

**Result, re-read from the final `dist/` bundle**:

| | Before | After |
|---|---|---|
| Tile count | 63,685 | **198** |
| `m_SourceBounds` center | (726.25, 3.39, -1144.95) | **(24.55, 3.39, -157.84)** |
| `m_SourceBounds` extent | (3003.39, 51.47, 3455.97) | **(170.82, 51.47, 358.07)** |

Cross-checked the new extent against the real playable geometry's own true bounding box (every
non-water-layer Transform's world position across all 8 imported scenes, computed independently via
UnityPy): extent (229.2, 32.2, 527.8), center (-38.9, 17.6, -332.6) — same order of magnitude
(hundreds of units, consistent with Equator's own 22-tile/extent-(59,43,74) navmesh scaled up for
Bay5's larger harbor footprint), not the thousands-of-units figure the water plane produced. This
retires PLAN.md's R2 risk for the open-water case specifically (see §9 item 2's update above) — the
narrower `NavMeshModifier`/`NavMeshModifierVolume` hazard-exclusion gap remains open, still needs an
in-game check.

Equator's three bundle hashes were **not** touched by this rebuild (never rebuilt, per the hard
rule) — reconfirmed byte-identical against the baseline at the end of this pass (§10f).

### 10b) [HIGH, CONFIRMED+FIXED] `MapBorderRepairs.cs` Item 2 fired on Bay5 objects

**Verified**: re-read `ArenaMapPort.Client/MapBorderRepairs.cs`'s `ScanScene()` — only the `Cube`
sub-check carried the `isLightingScene` gate; the `_stencil`/`_STENCIL` suffix check and the
`^colider \d+$` regex check ran unconditionally for every scene of every registered map (since
`OnSceneLoaded` gates only on the generalized `ArenaMapRaid.IsOwnedScene`, true for Bay5's scenes
too). The class's own doc comment claim ("Item 2 only ever fires while scanning Equator's own
Lighting scene") was true only for the `Cube` sub-check.

**Quantified the real impact directly against the (pre-fix) built `arena_bay5_scenes.bundle`**, not
just the scout census: cross-referenced against the census's `special_name_objects._stencil`
by-scene breakdown, 7 of Bay5's 15 `_stencil`-suffixed objects live in imported scenes
(`Arena_Bay5_Main`, `Arena_Bay5_Light`). For
`colider N`: re-ran the class's own exact regex (`^colider \d+$`, not the scout census's looser
"colider or ballistic" bucket) directly against the built bundle — **190 objects** in Bay5's
imported scenes match exactly, of which **187 carry an enabled `MeshCollider`** and 6 a
`MeshRenderer`. Pre-fix, ALL 190 would have had these force-disabled at raid start on `arena_bay5`
— 187 real collision holes across the map, not merely a cosmetic gap.

**Fix**: gated all three Item-2 sub-checks on the resolved map (`isEquator = map ==
ArenaMapRegistry.Equator`), not scene-name literals alone. Kept `Cube` scoped additionally to
Equator's own Lighting scene specifically (Arena's own 19 `Cube` objects live only there, per
`docs/progress/fix-enabled-state.md`'s own scene-attributed table) — but deliberately did **not**
narrow `_stencil`/`colider` the same way: that same table shows both patterns also present in
Equator's own `Arena_Equator` (main) scene (`PKTS-6281_Admiral_opened_STENCIL`,
`colider 46`/`45`/`2`), so gating them to `isLightingScene` alone would have silently stopped
covering Arena_Equator's own legitimate matches — a second, narrower bug the naive fix would have
introduced. Widened them to "any of Equator's owned scenes" instead. Updated the stale doc comments
(class summary already matched; the three inline per-check comments and the `_safetyNetCubeCount`
field group comment did not).

**Verification**: `dotnet build ArenaMapPort.Client -c Release` — 0 warnings, 0 errors; DLL
re-deployed to `dist/` (no bundle rebuild needed — client-code-only change). `dotnet test
ArenaMapPort.Server.Tests` — 48/48 passed.

Also confirmed (context, not itself a defect): critic amendment #2's "audit every sceneUnloaded
handler for the CurrentMap ordering hazard" is already satisfied project-wide. All 6
`sceneUnloaded` handlers in the plugin (`ArenaLightingRepairs`, `LightingDiagnosticProbe`,
`MapBorderRepairs`, `NavMeshInstaller`, `SceneDiagnostics`, `ShaderRebind`) use the stateless
`ArenaMapRaid.MapForScriptsScene(scene.name)` resolution, and `ArenaMapRaid.CurrentMap` itself is a
live-computed property (queries `SceneManager` fresh on every access, never a field any handler
caches or clears) — there is no shared-mutable-state ordering hazard anywhere in the current code.
Item 2's gating (this defect) was the one real gap the verification pass found.

### 10c) [MEDIUM, CONFIRMED+FIXED] No automated `tools/maps/*.json` ↔ `ArenaMapDescriptor.cs` check

**Verified**: grepped all of `tools/` and `ArenaMapPort.Server.Tests/` — confirmed no existing check
cross-validates `tools/maps/bay5.json` (or `equator.json`) against `ArenaMapDescriptor.cs`'s
`ArenaMapRegistry` entries, nor against `UnityProject/.../ArenaPaths.cs`'s `EquatorDefault()`
literal. `MapDescriptorConsistencyTests.cs` only covers `ArenaMapRegistry.Equator` vs
`ArenaEquatorLocation.cs`'s own consts (drift-proof by construction, unrelated to the JSON files) —
exactly the gap the review brief asked about.

**Fix**: new `tools/validate/validate_map_descriptors.py` — a regex-based C# extractor (not a full
parser; a parse failure is a hard error, never a silent skip) that:
1. Cross-checks `tools/maps/equator.json` and `tools/maps/bay5.json` against
   `ArenaMapDescriptor.cs`'s `ArenaMapRegistry.Equator`/`.Bay5` entries — resolving Equator's
   delegated `ArenaEquatorLocation.X` references through that file's own consts (including
   `AllSceneNames`, whose elements are themselves unqualified sibling-const references, resolved in
   two passes).
2. Cross-checks `tools/maps/equator.json` against `ArenaPaths.cs`'s `EquatorDefault()` fallback
   (including `fog_color`, compared approximately since the JSON carries more decimal places than
   the C# float literal).

Verified it actually catches drift (not a rubber stamp): injected a deliberate one-field mismatch
(`bay5.json`'s `icon_y` 830→831), confirmed the script FAILs with an exact per-field diagnostic,
reverted, re-confirmed OK.

Wired into `tools/build_all.ps1` as a new mandatory step-4 check, run unconditionally (it is a
whole-registry check, independent of which map `-MapKey` targets) — a failure now fails the whole
pipeline the same way the three existing WP6 validators do. Exercised for real as part of this
pass's own Bay5 rebuild (§10a) — passed.

### 10d) [LOW, CONFIRMED+FIXED] Stale "not yet built" doc comments

**Verified**: all three cited comments were stale — `ArenaMapPort.Server/db/arena_bay5/base.json`
exists on disk (10,957 B) and is the file this very pass's server registration reads.
`ArenaMapDescriptor.cs`'s `Bay5` doc comment and `AllSceneNames` inline comment, and
`ArenaMapRegistration.cs`'s `OnLoadAsync` comment, all still asserted "not extracted/built yet" /
"PROPOSAL, not yet validated".

**Fix**: updated all three to the current state (extracted/built/registered per this doc), while
keeping what was still true (the per-map try/catch isolation rationale; scene order still worth an
in-game confirm even though no longer literally "unvalidated").

### 10e) [LOW, no code change] `ArenaLightingRepairs.cs`'s hardcoded runtime `FogColor` / report wording

Two low-severity findings, both already correctly handled without a code change:

- **`ArenaLightingRepairs.cs`'s hardcoded `FogColor`** (line ~398, Equator's own
  `(0.62, 0.66, 0.70)`, used unconditionally in both `ApplyAmbient()`/`ApplyFog()` regardless of the
  loaded map): confirmed exactly as described, and confirmed already accurately disclosed in §3b
  above as a deliberate, tracked, playtest-driven-future-work decision — distinct from the
  per-scene AUTHORED fog color, which IS already correctly per-map (§3b). Closing it now would mean
  adding a new `FogColor` field to `ArenaMapPort.Shared/ArenaMapDescriptor.cs` (which has none
  today) and its own playtest-driven tuning — a real scope increase, left deferred per the finding's
  own recommendation.
- **Report-wording inconsistency** (a prior conversation-turn summary reportedly said
  "Deliberately not touched: `ArenaMapPort.Server.csproj`" while also listing it under "Modified"):
  searched this entire repository, including `docs/progress/multimap-generalize.md` in full — that
  exact phrase does not appear anywhere in any committed or working-tree file. The persisted doc's
  actual wording ("confirmed already fully map-generic ... no change needed", `multimap-
  generalize.md` lines ~154-156/~303) is a narrower, accurate claim about the `db\**\*.json` staging
  glob's BEHAVIOR, not a claim that the file was never edited — consistent with `git diff` showing
  exactly the one-line `ArenaMapDescriptor.cs` link addition. Nothing in the repository needed
  correcting; the inconsistency, if real, exists only in a prior chat-turn summary, not a durable
  artifact.

### 10f) Final Equator regression check

Re-confirmed at the end of this pass, after §10a/§10b's rebuilds:

| File | SHA-256 | Matches baseline? |
|---|---|---|
| `arena_equator_scenes.bundle` | `adb8c5916da001f068fe345d3c18d167ec74357d003b9853ff3e16df64fb1035` | yes |
| `arena_equator_navmesh.bundle` | `25d6165c3c2535e4ae3ae77f4d08b4a3963f9addcfb275b546543d51fea10f6b` | yes |
| `arena_equator_preset.bundle` | `f12cf0bfbff60e5ec299f211253656d87650d63fb8a3a899b957bf4ee06dfe36` | yes |

Equator was never rebuilt this pass — these three files are the exact same bytes as
`docs/progress/fix-enabled-state.md`'s recorded baseline throughout.

### `dist/` — final file listing (end of this pass)

| File | Size (B) | SHA-256 | Changed this pass? |
|---|---:|---|---|
| `bundles/arena_bay5_navmesh.bundle` | 130,395 | `a49d5a4a0cc8e81d62f00709a6c878313e105174708c41b102f443234458c23c` | **yes** — §10a rebake |
| `bundles/arena_bay5_preset.bundle` | 7,027 | `d538a6d00e6298486f8ca60d19393113b88afeab5e5499a048fb33694c048e8c` | no |
| `bundles/arena_bay5_scenes.bundle` | 506,170,490 | `3110f8af168fe0e5f105ebc863dc7d0cc04243b84afb639d132388246d7708d6` | rebuilt (§10a's pipeline re-ran the full Bay5 build; scene content is unaffected by the navmesh fix, hash differs from Unity's own build non-determinism — object/material counts re-verified identical by `validate_bundle_roundtrip.py`) |
| `bundles/arena_equator_navmesh.bundle` | 72,842 | `25d6165c3c2535e4ae3ae77f4d08b4a3963f9addcfb275b546543d51fea10f6b` | no |
| `bundles/arena_equator_preset.bundle` | 6,891 | `f12cf0bfbff60e5ec299f211253656d87650d63fb8a3a899b957bf4ee06dfe36` | no |
| `bundles/arena_equator_scenes.bundle` | 308,912,270 | `adb8c5916da001f068fe345d3c18d167ec74357d003b9853ff3e16df64fb1035` | no |
| `maschine-ArenaMapPort.Client.dll` | 74,752 | `ccdffb9219ab105f4c3231e0699ece8f6bedbc08bd4bead5d00c90dd6015b367` | **yes** — §10b fix (MapBorderRepairs.cs) |
| `material-shaders.arena_bay5.json` | 50,049 | `05495ce830f8ac4256c3b8cd9a44a4f14b4046405a43f6e3b4d6aab6ffc628ac` | no (byte-identical despite the rebuild — shader import untouched by §10a) |
| `material-shaders.json` | 43,237 | `0724b11c2e97b4b1710384d1498af2a2de061e21d1ab98bc1da711c142b1ad17` | no |
| `SPT_Runtime/.../db/arena_bay5/base.json` | 10,957 | `8527c5f9bd694f233c718837be3f05081da719ef1cc78ff361395f8896b4a7a9` | no |
| `SPT_Runtime/.../db/base.json` | 10,968 | `d85aae93540bec143714770c80b95312551964d14898adb0c0391a2b41238a49` | no |
| `SPT_Runtime/.../maschine-ArenaMapPort.Server.dll` | 24,064 | `ca1e79a7079e33097c8b13df781d546b3582290b4666371ba62676532518c439` | no |

No git commits made (workspace rule: the lead commits). Logs under
`work/logs/bay5-navmesh-waterfix-r2-20260915.{out,err}.log` and
`work/build/bay5/unity_build_all_20260915_090514.log`.
