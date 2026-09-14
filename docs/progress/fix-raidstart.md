# fix-raidstart — apply verified review findings (label fix-raidstart)

Task: re-verify the two `ok:false` findings from the adversarial review of `impl-raidstart-repairs`
(itself fixing the 2026-09-14 raid-start abort), apply fixes, rebuild Release, re-run validators if
bundles changed.

## Finding 1 — `SpatialAudioSystem.ProcessInteractiveObjectOcclusion` unguarded (real, dormant gap)

**Re-verified independently against `D:/SPT41/Development/_decompiled`, confirmed correct:**

- `Audio.SpatialSystem/SpatialAudioSystem.cs:554-557`:
  ```
  public void ProcessInteractiveObjectOcclusion(string targetID, BetterSource source, Vector3 position, EOcclusionTest test)
  {
      _interactiveObjectOccluder.CheckOcclusion(targetID, source, position, test);
  }
  ```
  Single overload (confirmed via grep — no other declaration of this name in the file).
  `_interactiveObjectOccluder` is assigned exactly once, at `SpatialAudioSystem.cs:172`
  (`_interactiveObjectOccluder = new InteractiveObjectOccluder(this);`), inside `Initialize`'s real
  body — which `SpatialAudioInitSkipPrefix` permanently `return false`s on `arena_equator`. So the
  field is null for the entire raid on our map.
- Callers, both confirmed:
  - `EFT.Interactive/WorldInteractiveObject.cs:1249-1253` `ProcessOcclusion(BetterSource, Vector3)` —
    base class for doors/switches/levers (`class WorldInteractiveObject : InteractableObject, ...`).
  - `EFT.Interactive/WindowBreaker.cs:932,945` — breakable glass.
- `SpatialAudioOcclusionGuard`'s reflection query in `HarmonyPatches.cs` filters
  `m.Name == nameof(SpatialAudioSystem.ProcessSourceOcclusion)` — a different, literal name; it does
  not and cannot match `ProcessInteractiveObjectOcclusion`. Confirmed no other patch/gate touches it
  (grepped the whole client project for the method name before this fix — zero hits outside the
  decompile).
- Practical risk this milestone: **dormant**. `tools/extract/extract_equator.py` authors only
  `LevelSettings`/`LocationScene`/`BotZone`/`LocationExportInfo`/`SpawnPointMarker` — never a
  `WorldInteractiveObject` or `WindowBreaker` — so no instance of either exists in the shipped scenes.
  It is a live gap for the next milestone that adds a door or breakable window.

**Fix applied** (`ArenaMapPort.Client/SpatialAudioRepairs.cs`, `ArenaMapPort.Client/HarmonyPatches.cs`):

- New `SpatialAudioInteractiveOcclusionGuard` static class: `Prefix()` returns
  `SpatialAudioSystem.Initialized` (false skips the original body — no occluder to call yet);
  `Finalizer(Exception __exception)` swallows any exception when `ArenaEquatorRaid.IsLoaded()`,
  degrading to "no interactive-object occlusion sound" the same way `SpatialAudioOcclusionGuard`
  degrades `ProcessSourceOcclusion` to `-1`. Unlike that guard, the target method is `void`, so
  neither the prefix nor the finalizer needs `ref int __result` plumbing — confirmed this compiles
  and Harmony can bind it (a `ref int __result` parameter is only valid when the patched method
  actually returns `int`; using the wrong signature here would have failed to patch or thrown at
  install time — avoided by writing the void-specific signature instead of reusing the int one).
- New `InstallSpatialAudioInteractiveOcclusionGuard` in `HarmonyPatches.cs`, wired into `InstallAll`
  right after `InstallSpatialAudioOcclusionGuard`. Target resolved by
  `AccessTools.Method(typeof(SpatialAudioSystem), nameof(SpatialAudioSystem.ProcessInteractiveObjectOcclusion))`
  (name-only, same convention as `InstallEnsureEnvironment` — safe since there is exactly one overload).
  Gating matches `SpatialAudioOcclusionGuard`'s own documented reasoning: prefix branches only on the
  static `Initialized` flag (safe on every map, not just ours); finalizer is map-gated.
- Also corrected `SpatialAudioRepairs.cs`'s own doc comment, which previously asserted
  `ProcessSourceOcclusion` was "the ONE public entry point that does NOT check Initialized itself" —
  now documents both entry points and points at the new guard.
- `docs/progress/impl-raidstart-repairs.md` updated with a follow-up section recording this fix.

## Finding 2 — truncated SHA-256 hashes in `impl-texture-dimensions.md` (cosmetic doc defect)

**Re-verified independently**: recomputed `sha256sum` on all 4 files the table cites
(`work/build/full/arena_equator_scenes.bundle`, `arena_equator_navmesh.bundle`,
`material-shaders.json`, `work/extract/equator/manifest.json`). Every recorded hash was exactly 63
hex characters — one short of a real SHA-256's 64 — and each recomputed hash matched the recorded
string for its first 63 characters plus exactly one more trailing hex digit (e.g. recorded
`...1200db1f9`, real `...1200db1f9b`). File byte sizes in the table were already correct and
unaffected. Confirms this is a transcription/formatting defect in the progress note only, not a
stale or wrong artifact.

**Fix applied**: replaced all 4 hash strings in `docs/progress/impl-texture-dimensions.md`'s
artifact-hash table with the freshly recomputed, full 64-character values, and added a one-line note
explaining the correction.

## Build

`dotnet build -c Release` in `ArenaMapPort.Client` — 0 warnings, 0 errors. The
`ArenaMapPortDistCopy`/`ArenaMapPortStageWpOutputs` MSBuild targets re-staged the DLL and re-copied
bundles/`material-shaders.json` from `work/build/full`/`work/build/preset` into `dist/` as part of the
same build (confirmed via the build's own console output and `dist/` file mtimes).

## Validators — not re-run (correctly, per task instruction)

Neither fix touched the Unity pipeline, the extractor, or any bundle content — finding 1 is a
client-side (`ArenaMapPort.Client`) Harmony-patch addition, finding 2 is a documentation-only
correction. `dist/BepInEx/plugins/ArenaMapPort/bundles/*.bundle` and `material-shaders.json` are
byte-identical before and after this task's build (re-staged from the same unchanged
`work/build/full` source; confirmed by file size/mtime — bundle mtimes predate this task's build,
only the DLL's mtime is new). Per the task's own instruction ("re-run validators if bundles
changed"), they did not change, so `validate_extract.py`/`validate_bundle_roundtrip.py`/
`validate_preset.py` were not re-run.

## Not done / next steps

No in-game raid test was run this pass (matches `impl-raidstart-repairs.md`'s own "Not done" section
— an actual raid-start test on `arena_equator` is still the natural next step, now covering both the
original 3 crash-chain fixes and this pass's dormant interactive-occlusion guard, which has no way to
be exercised in-game until a later milestone adds a door or breakable window to the extraction
pipeline).
