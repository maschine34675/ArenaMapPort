# Verification of ref-interchange/F4 (Bundle stamp check gates the preset swap)

## Finding under review
> Bundle validity is guarded by a stamp file checking mod version major.minor, exact byte count,
> and exact 18-scene-name set before the swap proceeds; a failed check reverts to vanilla scenes.

Cited evidence: `InterchangeRework.Client/PresetSwap.cs:63-89` (EnsureBundle/BundleStampCheck.Compare);
`InterchangeRework.Shared/BundleStamp.cs:33-58` (Compare logic).

Files read (read-only, on disk under `D:/sptmaps/_sources/SPT-InterchangeRework-main`):
- `InterchangeRework.Client/PresetSwap.cs`
- `InterchangeRework.Shared/BundleStamp.cs`
- `InterchangeRework.Shared/SceneNames.cs`

## Verdict: CONFIRMED, with two citation/precision corrections

### What holds up
1. **Called before the swap.** `PresetSwap.BeforePresetLoad` (Harmony prefix on
   `LoadScenesFromPresetOperation.LoadPresetAsync`) calls `EnsureBundle()` at line 45 and only calls
   `RewriteEntries(preset)` (the actual swap) at line 46 if it returned `true`. Order is exactly as
   claimed: check first, swap second.
2. **Major.minor version check.** `BundleStampCheck.Compare`, `BundleStamp.cs:39-40`:
   `if (MajorMinor(stamp.ModVersion) != MajorMinor(modVersion)) return StampResult.Fail(...)`.
   `MajorMinor()` (lines 64-68) truncates `"0.1.3"` to `"0.1"` — patch version is deliberately
   ignored, only major+minor must match. Matches the finding.
3. **Exact byte count.** `BundleStamp.cs:43`: `if (stamp.BundleBytes != actualBytes) return
   StampResult.Fail(...)`. `actualBytes` is `new FileInfo(bundlePath).Length` (PresetSwap.cs:80) —
   a plain equality on file size, no hash. Matches the finding as stated (it does not claim a
   cryptographic hash, just "exact byte count", which is accurate).
4. **Exact 18-scene-name set.** `SceneNames.BaseNames` (SceneNames.cs:15-35) is a hardcoded array of
   18 entries (the file's own comment at line 9 says "The 18 Shopping_Mall scenes"); I counted the
   array literally and it is 18. `BundleStampCheck.Compare` (BundleStamp.cs:47-55) builds
   `requiredScenes = SceneNames.BaseNames.Select(SceneNames.ToIr)` (18 `_IR`-suffixed names) and then
   fails if `distinctStampScenes.Count != requiredScenes.Count`, or if anything is missing from or
   extra in the stamp relative to that required set. So yes: the stamp must name exactly those 18
   scenes, no more, no fewer, no duplicates.
5. **Failure reverts to vanilla.** In `BeforePresetLoad`, `EnsureBundle() == false` (which is what a
   failed `Compare` produces, PresetSwap.cs:81-86: on `!check.Ok` it logs, unloads the bundle, sets
   `_bundle = null`, returns `false`) leads straight to `RevertEntries(preset, "bundle unavailable")`
   at line 45. `RevertEntries` rewrites any `_IR`-suffixed scene path in the preset back to the native
   name (SceneNames.cs `RevertScenePath`). On a first-ever load (the common case where the stamp
   check fails) there is nothing to revert yet since paths are still native, and the code says so
   explicitly ("nothing to revert, the preset already names the native scenes", PresetSwap.cs:159) —
   net effect is the same as claimed: vanilla Interchange scenes load.

### Corrections to the finding as written
- **Citation range is imprecise for the second file.** The actual `Compare` method body is
  `BundleStamp.cs:35-61` (signature at 35, closing `return StampResult.Pass()` at 60, closing brace
  61), not `33-58`. The offered range starts one line early (at a comment, not the method) and stops
  three lines short, cutting off the final `distinctStampScenes` vs `actualSceneNames` cross-check
  (line 57-58 does appear, but the `Pass()` return at line 60 is outside the cited range). Minor, but
  the range should be corrected to `35-61` for anyone re-reading it.
- **"18-scene-name set" is actually a two-layer check, not one.** The finding's phrasing implies the
  stamp is checked against "the" 18 scenes as if that were a single comparison. In fact there are two
  independent comparisons chained: (a) `stamp.Scenes` (distinct) must equal the *hardcoded*
  `SceneNames.BaseNames` list compiled into the currently-running client plugin DLL — so a stamp
  built against an older/newer `SceneNames.BaseNames` list (e.g. if a future version renames or
  adds/removes a scene) fails here even if it is internally consistent; and (b) separately,
  every name in `stamp.Scenes` must also be present in `actualSceneNames` — the scene names actually
  found inside the loaded `.bundle` file via `AssetBundle.GetAllScenePaths()` (BundleStamp.cs:57-58).
  A stamp that lists the correct 18 names but ships next to a bundle that is missing one of them
  (e.g. corrupted/truncated build) still fails, independently of the byte-count check. This is a
  sharpening, not a refutation — the end-user-visible behavior ("exact 18-scene set required") is
  correctly described, but the mechanism is stronger/more layered than a single set-comparison.
- **Two additional required fields not mentioned by the finding** (not a contradiction, just
  incomplete): `stamp.GitSha` and `stamp.BuiltUtc` must both be non-empty (`BundleStamp.cs:41-42`)
  or the check fails too. These aren't compared against anything else, just required to be present.
- **The byte-count/version/scene checks are one-shot per process.** `_bundleTried` (PresetSwap.cs:23,
  67) is set to `true` before the check runs and is never reset on failure, so a failed stamp check
  disables the swap for the rest of the game process (all raids), not just the current raid attempt —
  worth knowing if reproducing/testing this, but doesn't change the finding's correctness.

## Bottom line
The finding is accurate on all four specific claims (major.minor match, exact byte count, exact
18-scene set, revert-to-vanilla-on-failure) and on the ordering (check gates the swap). The only
issues are a slightly-off line range for the `BundleStamp.cs` citation and an incomplete picture of
how many independent sub-checks make up the "18-scene-name set" gate — both are refinements, not
refutations.
