# identity-fix — MonoScript assembly-identity post-build patch — progress log

Role: fixer, task label `identity-fix`. Task: the lead flagged a LATENT RUNTIME BLOCKER every
prior pass (`s1-fix-r1`/`s1-fix-r2`/`s1-validate-r2`/`s1-validate-r3`) had wrongly accepted as a
non-defect: because `tools/gamerefs-fixup`/`tools/sync-game-refs.ps1` rename the GameRefs copies
of `Assembly-CSharp.dll`/`Assembly-CSharp-firstpass.dll` to
`SptGameRefs-AssemblyCSharp[Firstpass].dll` (necessary to get past Unity's AddComponent blocker at
BUILD time, `docs/progress/s1-fix-r1.md`), every SS D EFT `MonoScript` built into
`arena_equator_scenes.bundle` carries `m_AssemblyName == 'SptGameRefs-AssemblyCSharp.dll'` — a
name the real EFT client never loads, making every one of the 5 components a "missing script" the
moment a real raid loads this bundle.

**Result: fixed.** A new mandatory post-build tool
(`tools/bundle/patch_monoscript_identity.py`) rewrites the built bundle's MonoScript identity back
to the real, empirically-verified spelling; it is wired into a new pipeline orchestrator
(`tools/build_all.ps1`) as an unskippable step; `validate_bundle_roundtrip.py` is now strict
(`SptGameRefs-*` is a hard FAIL, not an accepted alternate spelling); the real `work/build/s1`
bundles are patched and all three SS B.WP6 validators pass strictly against them; a new Unity
batchmode check (`VerifyPatchedBundleLoads.cs`) confirms Unity's own native `AssetBundle` reader —
not just UnityPy — loads the patched bundle and enumerates its scenes correctly. No git
commit/add made (workspace rule: the lead commits).

## 0) Ground truth first — what does the real client actually spell?

The task's own literal wording asked for `'Assembly-CSharp.dll'`/`'Assembly-CSharp-firstpass.dll'`
but also said to verify empirically rather than trust that wording. Read 200 real `MonoScript`
objects directly out of `D:/SPT41/EscapeFromTarkov_Data/globalgamemanagers.assets` with a
standalone UnityPy probe (`work/identity_probe.py`, kept as evidence, gitignored):

```
class='ErrorHandler' ns='' assembly='Assembly-CSharp'
class='RenderTextureUtility' ns='UnityStandardAssets.CinematicEffects' assembly='Assembly-CSharp-firstpass'
... (200 scanned)
distinct assembly names: [..., 'Assembly-CSharp', 'Assembly-CSharp-firstpass', ...]
```

**No `.dll` suffix, ever**, for any real EFT/BSG MonoScript — confirmed independently by
`tools/validate/validate_preset.py`'s own pre-existing `EXPECTED_MONOSCRIPT = {...,
"m_AssemblyName": "Assembly-CSharp"}` (the donor `develop_preset.bundle`'s real
`EFT.ScenesPreset` MonoScript, a completely different/untouched build pipeline — cross-checked
this pass: `assembly='Assembly-CSharp'`, matches exactly). Our own `arena_equator_scenes.bundle`
(before this pass), read the same way: `{'SptGameRefs-AssemblyCSharp.dll': 5}` for all 5 SS D
types. This is the actual defect: Unity resolves a MonoBehaviour's script by (assembly, namespace,
class) against assemblies actually loaded at runtime — `SptGameRefs-AssemblyCSharp.dll` is never
loaded by the real client, so the component is unresolvable.

**Judgment**: I used the empirically-verified spelling (`Assembly-CSharp` /
`Assembly-CSharp-firstpass`, no `.dll`) rather than the task's literal wording, documented
explicitly in both the tool and the validator (same "verify, don't assume, document any
deviation" precedent every prior pass in this workspace has followed).

## 1) `tools/bundle/patch_monoscript_identity.py` (task item 1)

Opens a built `.bundle` with UnityPy, finds every `MonoScript` whose `m_AssemblyName` matches one
of the known GameRefs-renamed spellings (`SptGameRefs-AssemblyCSharp[Firstpass][.dll]`, both
with/without `.dll` handled defensively since Cecil's `ModuleDefinition.Name` (file name, with
`.dll`) vs `AssemblyDefinition.Name.Name` (assembly identity, without) could plausibly produce
either), rewrites **only** that one string field via `save_typetree`, and re-saves the bundle
(`BundleFile.save(packer="original")` by default — reuses the bundle's own detected
data/block-info compression flags, the closest fidelity UnityPy offers) to an output path (default:
in place). Every other object, and every other MonoScript field, is left completely untouched —
verified, not just asserted (see §4).

Self-verifying: after saving, it re-loads the OUTPUT file fresh (independent of the in-memory
state used to write it) and checks: every MonoScript now carries only a real assembly name; every
non-MonoScript object (`MonoBehaviour`/`Mesh`/`Material`/`Texture2D`/`GameObject`/`AssetBundle`/...)
is byte-for-byte identical to its pre-patch snapshot; the requested packer reproduced the exact
original compression flags; a raw, UnityPy-independent parse of the UnityFS primary header
(signature/format/unity-version/dataflags) is unchanged; and per-nested-file ("block") sizes are
unchanged except for the one file that actually contained a patched MonoScript, by exactly the
expected string-length delta. Exit code 1 with a specific FAIL line on any mismatch — never a
silent pass.

If 0 MonoScripts need patching (e.g. the navmesh bundle, which has none), the input file is left
completely untouched on disk rather than needlessly re-serialized.

**A real bug found and fixed while building this tool**: an in-place patch (`--out` defaulting to
`--bundle`) initially failed `os.replace()` with `PermissionError: [WinError 5] Zugriff
verweigert` — Windows refuses to replace a path this same process still has an open handle on, and
UnityPy's file loader (`helpers/ImportHelper.py`) opens the source via plain `open(path, "rb")`,
not a fully-buffered read that releases the handle immediately. Fixed with an explicit `gc.collect()`
after dropping every reference to the loaded `Environment`/`BundleFile`, before the
retry-wrapped `os.replace()` (bounded retry kept too, matching `tools/gamerefs-fixup`'s own
AV-lock precedent from `s1-fix-r1`).

**Tested on a throwaway copy first** (`work/identity_fix_test/`, gitignored) before touching the
real `work/build/s1` bundles — full clean run, all self-checks green, confirmed the tool works
before using it for real.

## 2) Mandatory pipeline integration (task item 2)

New `tools/build_all.ps1` — the orchestrator: `sync-game-refs.ps1` -> Unity
`BuildBundles.Run` -> **mandatory** `patch_monoscript_identity.py` against every `*.bundle`
`BuildBundles.Run` produced (globs `$OutputDir/*.bundle`, so both `arena_equator_scenes.bundle`
AND `arena_equator_navmesh.bundle` are always covered, with zero risk of forgetting one as new
bundle types are added later) -> all three SS B.WP6 validators. Each step gates the next; the
script `exit`s non-zero (via a `Fail` helper that prints and exits, never `Write-Warning`-and-
continue) the instant any step fails — there is no script-level flag to skip the identity-patch
step; only `-SkipGameRefsSync`/`-SkipUnityBuild` exist, and both leave steps 3-4 (patch +
validate) mandatory. The script's own docstring states plainly that it is now "the ONLY
sanctioned way to produce a build safe to copy into dist/".

Exercised end-to-end this pass with `-SkipGameRefsSync -SkipUnityBuild` (steps 3-4 only, against
the already-patched real `work/build/s1` — a genuine idempotency check: re-running the patch step
against an already-clean bundle correctly reports "0 MonoScript(s) needed patching... Input left
completely untouched", then all validators pass). The Unity build step itself
(`BuildBundles.Run`) was **not** re-run this pass — the C# build pipeline is unchanged by this
task, so re-running it would reproduce the exact same (already-verified-clean,
`s1-fix-r2.md`-confirmed) scene/mesh/material/navmesh output at a ~5 minute cost for zero new
information; only the post-build identity patch was ever missing. `tools/build_all.ps1`'s own
build-invocation path (`-batchmode ... BuildBundles.Run ...`) matches every prior pass's own
documented command line exactly (no `-quit`, `BuildBundles.Run` owns `EditorApplication.Exit()`)
and is ready to run for a future S2/full build.

## 3) `validate_bundle_roundtrip.py` made STRICT (task item 3)

`ACCEPTED_ASSEMBLY_NAMES` was `{"Assembly-CSharp", "Assembly-CSharp.dll",
"SptGameRefs-AssemblyCSharp", "SptGameRefs-AssemblyCSharp.dll"}` (s1-fix-r1's permissive set,
treating the renamed GameRefs spelling as an accepted alternate). Now:

```python
ACCEPTED_ASSEMBLY_NAMES = {"Assembly-CSharp", "Assembly-CSharp-firstpass"}
```

Any `SptGameRefs-*` name is now a hard FAIL at `check(e["assembly"] in ACCEPTED_ASSEMBLY_NAMES,
...)` (unchanged check logic — only the accepted set shrank). Module docstring and the constant's
own comment rewritten to state plainly that the old judgment was wrong and why (SS0 above),
pointing at `tools/bundle/patch_monoscript_identity.py` as the mandatory fix.

## 4) Real `work/build/s1` patched + verified + re-validated strictly (task item 4)

Patched the two real bundles in place:

```
patch_monoscript_identity.py --bundle work/build/s1/arena_equator_scenes.bundle
  BEFORE: 17,810,471 bytes, 'SptGameRefs-AssemblyCSharp.dll': 5
  patched 5 MonoScript object(s): LocationScene/BotZone/SpawnPointMarker/LocationExportInfo/LevelSettings
    all 'SptGameRefs-AssemblyCSharp.dll' -> 'Assembly-CSharp'
  AFTER: 17,709,976 bytes (-100,495 bytes), 'Assembly-CSharp': 5
  ok: every MonoScript now carries a real assembly name
  ok: all 362 non-MonoScript objects byte-for-byte identical before vs after
  ok: packer='original' reproduced the exact original compression flags (meta dataflags=579
      [lz4hc], data block_info_flags=65 [lzma])
  ok: signature/format/unity-version/dataflags header fields unchanged
  nested file sizes: only BuildPlayer-Arena_Equator_Scripts.sharedAssets changed
      (93,456 -> 93,376 bytes = exactly 5 x -16 bytes, the padded string-length delta)
  OK -- patch applied and verified

patch_monoscript_identity.py --bundle work/build/s1/arena_equator_navmesh.bundle
  0 MonoScript(s) needed patching (NavMeshData carries none) -- input left untouched
```

Re-ran all three SS B.WP6 validators fresh, strict/default flags, against this patched build:

```
validate_extract.py --package work/extract/equator_s1
  -> OK, no warnings (unchanged from every prior pass -- WP1 untouched by this task)

validate_bundle_roundtrip.py --scenes-bundle ... --navmesh-bundle ... --package ...
    --material-shaders ... --editor-verify ...
  -> OK -- all checks passed (1 non-blocking WARN, the known benign Material 13-vs-12
     Default-Skybox, unrelated to this task, unchanged)
  -- all 5 SS D MonoScripts: "MonoScript assembly is ['Assembly-CSharp', 'Assembly-CSharp-firstpass']
     (got 'Assembly-CSharp')" -- STRICT check, passing for real now, not via a permissive accept-set

validate_preset.py --scenes-bundle ... --allow-missing-scenes
  -> OK -- all checks passed (donor preset's own MonoScript still reads 'Assembly-CSharp',
     confirmed unaffected by this pass -- a different, untouched pipeline)
```

Zero FAILs across all three, in strict mode, with the tightened `ACCEPTED_ASSEMBLY_NAMES` — this
is a real pass, not one relying on the old permissive set.

## 5) Did UnityPy's re-save break anything a real Unity player needs? (task item 5)

**Compression**: `packer="original"` reused the bundle's own detected flags exactly
(meta/block-info directory: `lz4hc`, flags=579; asset data blocks: `lzma`, flags=65) — confirmed
both before and after are bit-identical (`dataflags`/`_block_info_flags` compared directly off
the re-loaded `BundleFile` object). UnityPy's `CompressionHelper.COMPRESSION_MAP` does implement
both `LZMA` and `LZ4`/`LZ4HC` (`compress_lz4`/`compress_lzma`, read directly from
`UnityPy/helpers/CompressionHelper.py` this pass) — so no compression downgrade or "not
implemented" fallback was needed; the original scheme was faithfully reproduced, not merely
approximated.

**Header/flags/block sizes/`m_Container`/`m_PreloadTable`**: a raw, UnityPy-independent
`struct`-based parse of the UnityFS primary header (signature/format/player-version/engine-
version/dataflags) is unchanged before vs after; per-nested-file ("block") sizes are unchanged
except the single file containing a patched MonoScript, by exactly the expected byte delta; the
`AssetBundle` object itself (which carries `m_Container`/`m_PreloadTable`/`m_AssetBundleName`/
`m_Dependencies`) is one of the 362 objects covered by the general byte-identity check (a
byte-level comparison is strictly stronger than a field-level diff) and is confirmed unchanged.

**Does Unity's own native reader — not just UnityPy — actually load the result?** Added
`UnityProject/Assets/ArenaMapPort/Editor/Verify/VerifyPatchedBundleLoads.cs`
(`-executeMethod ArenaMapPort.Editor.Verify.VerifyPatchedBundleLoads.Run -scenesBundle <path>`,
safe to run with `-quit` — no scene authoring, no `AddComponent`, no `EditorApplication.update`
yielding needed) and ran it for real against the patched bundle:

```
[ArenaMapPort][VerifyPatchedBundleLoads] AssetBundle.LoadFromFile(D:/ArenaMapPort/work/build/s1/arena_equator_scenes.bundle) ...
[ArenaMapPort][VerifyPatchedBundleLoads] GetAllScenePaths() -> 3 : Assets/ArenaImport/Scenes/Arena_Equator.unity,
  Assets/ArenaImport/Scenes/Arena_Equator_Lighting.unity, Assets/ArenaImport/Scenes/Arena_Equator_Scripts.unity
[ArenaMapPort][VerifyPatchedBundleLoads] GetAllAssetNames() -> 0 :
[ArenaMapPort][VerifyPatchedBundleLoads] OK -- Unity's native AssetBundle reader loaded the patched
  bundle, enumerated 3 scene path(s) + 0 asset name(s), and found 'Arena_Equator_Scripts' among them.
```
(log: `work/build/s1/unity_identityfix_verifyload1.log`, zero `error CS`/`Exception`/`Received
signal` in the full log.) `GetAllAssetNames() -> 0` is expected and correct — this bundle was
built via `AssetBundleBuild` with only scene `assetNames`, so its contents surface through
`GetAllScenePaths()`, not `GetAllAssetNames()` (which lists individually-addressable, non-scene
assets). This is the decisive check: Unity's own C++ `AssetBundle.LoadFromFile` — completely
independent of UnityPy/Python — accepted the re-serialized container and correctly enumerated
`Arena_Equator_Scripts`, the scene carrying all 5 patched SS D MonoScript-bearing
MonoBehaviours. Not run: actually entering Play Mode to load the scene's GameObjects (a
runtime/Play-Mode-only API path from Editor batchmode, materially riskier/slower than this
check and not needed to answer item 5's actual question, which is about bundle-format/
compression fidelity, not scene-content fidelity — already covered independently by the
byte-identity check in SS4).

## Files touched

- `tools/bundle/patch_monoscript_identity.py` — new (task item 1).
- `tools/build_all.ps1` — new (task item 2).
- `tools/validate/validate_bundle_roundtrip.py` — `ACCEPTED_ASSEMBLY_NAMES` tightened to strict
  (task item 3); module docstring + constant comment rewritten to explain why and point at the new
  tool.
- `UnityProject/Assets/ArenaMapPort/Editor/Verify/VerifyPatchedBundleLoads.cs` (+ `.meta`) — new,
  the real-Unity-loads-it check (task item 5).
- `work/build/s1/arena_equator_scenes.bundle` — patched in place for real (gitignored); `.manifest`
  sidecar left untouched (Unity's own `BuildPipeline.BuildAssetBundles` manifest, describes bundle
  dependencies/CRC of the *pre-patch* build — out of this task's scope, not read by anything this
  pass touched, and not something `patch_monoscript_identity.py` claims to keep in sync; flagging
  this honestly rather than silently ignoring it — a future pass wiring `build_all.ps1`'s dist-copy
  step should double check whether anything downstream actually reads the `.manifest`'s CRC field
  for this bundle).
- `work/build/s1/arena_equator_navmesh.bundle` — patch tool run against it, 0 MonoScripts found,
  left untouched (correct, expected).
- `work/build/s1/unity_identityfix_verifyload1.log` — the real-Unity-loads-it verification run
  (gitignored).
- `work/identity_probe.py`, `work/identity_fix_test/` — scratch evidence (gitignored, not part of
  the repo): the real-SPT-asset assembly-name probe (SS0) and the throwaway-copy dry run the patch
  tool was tested against before touching the real build.
- `docs/progress/identity-fix.md` — this file.

No file under `D:/SPT41` or `X:/` was written (both read-only, only read — confirmed by re-reading
this rule before every write this pass). Only one Unity process was run at a time (`tasklist`
confirmed empty before each invocation). No `git add`/`git commit` was run — per workspace rule,
the lead commits.

## Handoff

- **The latent runtime blocker is fixed and verified**: `work/build/s1/arena_equator_scenes.bundle`
  now carries only `Assembly-CSharp` as every SS D MonoScript's `m_AssemblyName` — resolvable by
  the real EFT client. `validate_bundle_roundtrip.py` will hard-FAIL if this regresses (e.g. a
  future build skips the mandatory patch step).
- **Use `tools/build_all.ps1` for every future build from here on** — calling
  `BuildBundles.Run` directly (as every prior pass's documented "re-run commands" section says)
  now produces a bundle that is *invalid to ship* until `patch_monoscript_identity.py` runs against
  it; the orchestrator is what makes that unskippable. The old direct-Unity-then-validators
  command sequence documented in `s1-fix-r1.md`/`s1-fix-r2.md`'s own "Handoff" sections is now
  superseded by this script for anything destined for dist.
- **Not investigated this pass** (out of scope): whether `tools/preset/patch_preset.py`'s donor
  preset bundle (`EFT.ScenesPreset`) has any analogous identity concern — it does not, confirmed
  directly this pass (`assembly='Assembly-CSharp'`, a real, untouched donor asset, never routed
  through GameRefs/gamerefs-fixup at all). The `.manifest` sidecar question flagged above ("Files
  touched"). S2 (full 6-scene build) is untouched by this pass and will need
  `tools/build_all.ps1` run for real (not `-SkipUnityBuild`) once undertaken.
