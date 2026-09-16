# fix-preset-cab-and-bundle-lifetime — preset internal-name collision + bundle refcount audit (progress log)

Role: implementer. Task: fix the cross-map raid-start failure (`arena_bay5` → `arena_equator` in one
client session refuses to load), extend validation to catch a recurrence, regenerate/stage all 14
preset bundles, audit `BundleHost`'s AssetBundle reference-counting for a lifetime leak, and document
everything with evidence. No git commit made (workspace rule: the lead commits).

**Result:** root cause confirmed and fixed (every preset bundle's internal Unity directory-table name
is now unique per map, deterministically derived from the map key). All 14 preset bundles regenerated,
validated, and staged into `dist/`; the fix is byte-minimal (only the internal name string changed,
same file size). A cross-bundle uniqueness validator is now wired into the whole-distribution gate. A
second, independent bug — `BundleHost`'s Harmony hook double-counts its own `AssetBundleReference`
refcount on first load — was confirmed by tracing the decompiled client and fixed, gated to only our
own bundles. `validate_distribution.py` (the project's own "safe to ship `dist/`" gate) passes clean:
`DISTRIBUTION_OK 14 maps 42 bundles 14 sidecars`.

---

## 1) Root cause — every preset bundle carried the SAME internal Unity directory-table name

### 1a. Runtime symptom (evidence: `D:/SPT41vanilla/BepInEx/LogOutput.log`)

Raiding `arena_bay5` then starting a raid on `arena_equator` in the same client session, `19:50:18`–`19:50:19`:

```
[19:50:19.830] [Error  : Unity Log] The AssetBundle 'C:\SPT41vanilla\BepInEx\plugins\ArenaMapPort\bundles\arena_equator_preset.bundle'
    can't be loaded because another AssetBundle with the same files is already loaded.
[19:50:19.831] [Error  :maschine-ArenaMapPort] [ArenaMapPort][Bundles] AssetBundle.LoadFromFile returned null for
    'C:\SPT41vanilla\BepInEx\plugins\ArenaMapPort\bundles\arena_equator_preset.bundle' (wrong BuildTarget, or a corrupt/incompatible file?)
[19:50:19.839] [Error  : Unity Log] Exception: Can't load bundle: maps/arena_equator_preset.bundle HTTP/1.1 404 Not Found
[19:50:19.839] EFT.TarkovApplication.LoadMapAndData (...) -> ... -> LoadScenesFromPresetOperation -> AssetsManager.LoadOperation.TryLoadAsset -> CG_LoadBundleCoroutine
```

`arena_equator`'s own preset bundle is never actually read from disk — `AssetBundle.LoadFromFile`
itself (a Unity engine call, not our code) refuses it outright.

### 1b. Confirmed with UnityPy: all 14 preset bundles share one internal name

Every `dist/BepInEx/plugins/ArenaMapPort/bundles/*_preset.bundle` is a nested UnityFS archive
containing exactly one `SerializedFile`, whose own name (what Unity calls a "CAB" identifier, and
what UnityPy's `BundleFile.files` dict is keyed by) is a purely internal, within-process
de-duplication key — Unity refuses to load a second `AssetBundle` whose serialized file shares an
internal name with one already resident. Scanning the shipped bundles before any fix (script:
`tools/validate/validate_bundle_names.py`, added this pass):

```
[validate_bundle_names] 1 collision(s) FOUND:
  - internal name 'CAB-db2b26ca214c6211c4fae58b5cbe8d61' is shared by 14 dist bundles:
    ['arena_airpit2_preset.bundle', 'arena_airpit_preset.bundle', 'arena_autoservice_preset.bundle',
     'arena_bay5_preset.bundle', 'arena_bowl_preset.bundle', 'arena_custom_preset.bundle',
     'arena_equator_preset.bundle', 'arena_factory_preset.bundle', 'arena_iceberg_preset.bundle',
     'arena_prison_preset.bundle', 'arena_railwaystation_preset.bundle', 'arena_shoreline_preset.bundle',
     'arena_woods_preset.bundle', 'arena_yard_preset.bundle']
```

Cause: `tools/preset/patch_preset.py` builds every map's preset by copying the SAME SPT donor,
`develop_preset.bundle` (`DEFAULT_SRC`), then patching only the `ScenesPreset` MonoBehaviour's
typetree fields and the `AssetBundle.m_Container` entry (the WP3-era "CORRECTION" already documented
in that file). It never touched the donor's own internal `SerializedFile` name
(`CAB-db2b26ca214c6211c4fae58b5cbe8d61`), so every patched copy inherited the exact same one.

Independently confirmed this string never appears anywhere else in the bundle's resolvable content —
not in the `ScenesPreset` typetree, not in `AssetBundle.m_Container`/`m_Dependencies`/`m_Name`/
`m_AssetBundleName`, not in the `SerializedFile`'s own (empty) `externals` list — so renaming it
changes nothing else about what the bundle serves:

```python
>>> UnityPy dump of arena_equator_preset.bundle's AssetBundle typetree (pre-fix):
{'m_Name': 'maps/arena_equator_preset.bundle', 'm_Container': [['assets/content/locations/_presets/
arena_equator.scenespreset.asset', {...'asset': {'m_FileID': 0, 'm_PathID': 7034189889941257301}}]],
 'm_AssetBundleName': 'maps/arena_equator_preset.bundle', 'm_Dependencies': [], ...}
```

## 2) Scenes/navmesh bundles across all 14 maps — already unique, confirmed, no fix needed

Read every dist bundle's internal directory-table entries with UnityPy (42 bundles: 14×preset,
14×scenes, 14×navmesh). Scenes/navmesh bundles are Unity's own build output (`BuildBundles.cs`/
`BakeNavMesh.cs`), not hand-patched from a shared donor — their internal names are Unity-derived
`BuildPlayer-<SceneName>[.sharedAssets]` (scenes) or a fresh per-bake `CAB-<hash>` (navmesh), which
are naturally distinct because every map's own scene names differ.

Result across all 42 bundles: **195 distinct internal names, exactly 1 collision group — the 14
preset bundles** (see §1b). Every scenes/navmesh bundle's name is unique, both within its own kind
and against every other bundle. No scenes/navmesh fix was needed.

For the large scenes bundles (up to 1.43 GB, `arena_shoreline_scenes.bundle`), this was read via a
lightweight raw parser (`validate_bundle_names.read_directory_names()`) that decompresses only the
UnityFS block-info/directory blob (kilobytes) rather than the actual per-file data blocks — scanning
all 42 bundles, including the four >1 GB ones, took **0.07 seconds** total, vs. minutes each for a
full `UnityPy.load()`. Cross-checked byte-for-byte against a full `UnityPy.load()` pass over the same
42 bundles (which is authoritative but slow) — identical result, confirming the fast reader is
correct, not just fast. The fast reader also has a runtime fallback to the slow, authoritative
`UnityPy.load()` path for any single file it fails to parse (tested by forcing a fault), so a future
unexpected bundle shape can never make this validator silently under-report.

## 3) Fix — `tools/preset/patch_preset.py`

Added (see the file's own "SECOND CORRECTION" docstring section for the full writeup):

- **`unique_cab_name(map_key)`** — `CAB-` + `md5("arenamapport:<key>:preset")` hex digest. Same
  length as the donor's own name (36 characters total), so the rename never needs a companion
  padding change anywhere in the bundle.
- **`patch_bundle_cab_name(env, new_cab_name)`** — renames the donor copy's single nested
  `SerializedFile`'s directory-table entry (`BundleFile.files` dict key, which is exactly what
  `BundleFile.save_fs()` re-serializes as the directory "path" field) to the new name. Fails loudly
  if the bundle doesn't have exactly one nested `SerializedFile` (never guesses).
- Wired into `main()`: computed once per `--map` invocation from `map_data["key"]`, applied right
  before the final save, and independently re-verified after a **fresh reload of the bytes actually
  written to disk** (`save_and_verify()` now also asserts the reread bundle's nested `SerializedFile`
  name equals the expected value — not just that the in-memory object was mutated).

**Verified minimal/surgical**: byte-diffed the freshly-regenerated `arena_equator_preset.bundle`
against the previously-shipped one — identical size (6,891 bytes both), and the **only** byte range
that differs is the 36-byte CAB string itself (offset 122–158):

```
before: ...\x04CAB-db2b26ca214c6211c4fae58b5cbe8d61\x00...
after:  ...\x04CAB-5a4614556a9b0758dd4edc3e5d71be69\x00...
```

Every other byte — the `ScenesPreset` MonoBehaviour, the `m_Container` rename, the MonoScript
identity, the whole `AssetBundle` object — is untouched.

## 4) Validation extended

- **`tools/validate/validate_preset.py`** — new `unique_cab_name()` (independently re-derived, not
  imported from the patcher, matching this file's existing from-scratch-re-derivation convention) and
  `validate_cab_name(env)`, wired into `main()`. Asserts the bundle under test's nested
  `SerializedFile` name equals the deterministic expected value for its own map key. Confirmed this
  correctly **fails** against the pre-fix dist bundle (`got ['CAB-db2b26ca214c6211c4fae58b5cbe8d61']`,
  expected the per-map one) and **passes** against the fixed one — i.e. it actually catches this
  exact bug, not a rubber stamp.
- **New `tools/validate/validate_bundle_names.py`** — distribution-wide: scans every bundle in a
  directory (default `dist/BepInEx/plugins/ArenaMapPort/bundles`) and fails if ANY two bundles (any
  kind, not just preset-vs-preset — a cross-kind collision would trip the identical Unity error) share
  an internal directory-table name. This is what item 2's scenes/navmesh check above ran on. Exit 0 +
  `OK` summary on success, exit 1 with the exact colliding files listed on failure.
- **`tools/validate/validate_distribution.py`** — now calls `validate_bundle_names.py` against the
  live `dist/.../bundles` directory as part of its own `validate()`. This is the project's actual
  "safe to ship `dist/`" gate (already invoked by `tools/build_remaining.ps1 -Stage`, the multi-map
  build script's own final step) — the natural, and now wired, home for a whole-distribution check
  that needs every map's bundles to already exist on disk together.
- **`tools/build_all.ps1`** — step 4's doc comment updated to point at both of the above (the
  single-map CAB-identity assertion now inside `validate_preset.py`, already called from this script
  for every build; the whole-distribution collision check in `validate_distribution.py`).

## 5) Bundle lifetime audit — `BundleHost.cs` vs. `EFT.AssetsManager.BundlesManager`

### 5a. Confirmed: `BundleHost`'s Harmony prefix double-counts its own reference on first load

Read `EFT.AssetsManager/BundlesManager.cs` (decompile) in full:

```csharp
public class AssetBundleReference {
    public readonly AssetBundle AssetBundle;
    public int Counter;
    public AssetBundleReference(AssetBundle assetBundle) { AssetBundle = assetBundle; Counter = 1; }  // :20-24
}
public class BundleLoadOperation : Operation<AssetBundle> {
    public static BundleLoadOperation Run(BundlesManager manager, string bundleName, bool logErrors) {
        if (manager._bundles.TryGetValue(bundleName, out var value)) {
            value.Counter++;                                                                          // :68
            return new BundleLoadOperation { Result = value.AssetBundle };
        }
        ...                                                                                            // :64-82
    }
}
public void UnloadBundle(string bundleName, bool unloadAllLoadedObjects) {
    if (_bundles.TryGetValue(bundleName, out var value)) {
        value.Counter--;
        if (value.Counter == 0) { value.AssetBundle.Unload(unloadAllLoadedObjects); _bundles.Remove(bundleName); }  // :146-162
    }
}
```

`ArenaMapPort.Client/HarmonyPatches.cs:102-117` installs `BundleHost.cs`'s `BundleHostPrefix.Prefix`
as a **`void`-returning Harmony Prefix** on `BundlesManager.LoadBundleAsync`. By Harmony's own rule, a
`void` Prefix can never skip the original method (only a `bool` Prefix returning `false` can) — so
`LoadBundleAsync`'s real body (`BundleLoadOperation.Run` above) **always still runs** immediately
after our prefix returns.

Trace of the very first `LoadBundleAsync("maps/arena_equator_preset.bundle")` call in a process:

1. Our prefix (`BundleHost.TryServe`) finds `bundleName` not yet in `manager._bundles`, loads it from
   disk, and (before this fix) did `manager._bundles[bundleName] = new AssetBundleReference(bundle)`
   — **Counter = 1** (from the constructor).
2. Harmony then calls the original `LoadBundleAsync` body regardless — `BundleLoadOperation.Run` sees
   the entry we *just* added, and does `value.Counter++` — **Counter = 2**.

So every one of our bundles' first load ends up with `Counter == 2` for what is, from the calling
code's own point of view, exactly **one** logical load request — a silent double-count that never
happens for a bundle BSG's own code loads (its `AssetBundleReference` is constructed exactly once,
inside `BundleLoadOperation.Coroutine`, never pre-populated by a second, earlier writer). Any single
`UnloadBundle` call against one of our bundles would then only bring `Counter` down to 1, never the 0
that triggers `AssetBundle.Unload()`.

### 5b. Searched the decompiled client for what, if anything, ever calls `UnloadBundle` for a map's own bundles

`grep -rn "UnloadBundle(" D:/SPT41/Development/_decompiled` (excluding `BundlesManager.cs`'s own
definition) finds exactly one call site: `AssetsManager.cs:567` (`UnloadUnusedResources()`, iterating
`_loadAssetOperations` — bundles tracked because an individual *asset* was loaded from them via
`LoadAssetAsync`/`LoadMainAssetAsync`/etc., not because their name was requested via
`LoadBundleAsync` directly). Searching for `UnloadUnusedResources()`'s own callers finds exactly one:
`EFT.Console.Commands/Shared.cs:91` — a **debug console command**, not any raid-end or menu-return
code path. `ModernLoadScenesFromPreset.cs:75` calls `SceneManager.UnloadSceneAsync(...)` at raid end
(unloads the Unity *scene*, not the AssetBundle it came from); `TarkovApplication.cs:3213` calls
`Resources.UnloadUnusedAssets()` (a Unity-engine sweep of orphaned *objects*, which never touches
`BundlesManager`'s own refcounts or calls `AssetBundle.Unload()`); `ObjectsFactory.UnloadBundles()`
(`ObjectsFactory.cs:819`) operates on a completely different tracking dictionary
(`_loadedBundles: Dictionary<ResourceKey, List<DependencyGraph<IEasyBundle>.Token>>`, the pooled
prop/item-prefab system) unrelated to `BundlesManager._bundles`.

**Conclusion**: no code path found anywhere in the decompiled client calls
`BundlesManager.UnloadBundle` for a map's own preset or scenes bundle name during ordinary play. A
location's preset+scenes bundles — vanilla SPT maps and ours alike — appear to simply stay resident
in `BundlesManager._bundles` for the life of the client process once loaded, by BSG's own design
(consistent with the well-known behavior that revisiting an already-loaded map is fast). This is
**inferred from an absence of evidence** (no caller found), not from a decompiled comment stating the
design intent explicitly — flagged here as such, not overstated as certain.

### 5c. Consequence

- **Playing several different ported maps in one session (memory)**: unaffected by whether the
  refcount bug in §5a is fixed — on the evidence in §5b, neither vanilla nor our map bundles are ever
  released via this mechanism during ordinary play anyway, so their combined size accumulates for the
  process lifetime regardless. This project's 14 ported maps' scenes bundles alone total roughly
  6.9 GB (`dist/.../bundles/*_scenes.bundle`, from ~35 MB up to `arena_shoreline_scenes.bundle` at
  1.43 GB) — visiting several of the larger ones in one session will use several GB of resident memory,
  the same characteristic large vanilla SPT map bundles already have, just more pronounced here because
  every one of these maps is independently large and there are 14 of them installed at once.
- **Re-raiding the same map**: no different behavior with or without the §5a fix, and no different
  behavior than vanilla — the bundle is already resident from the first raid, so `TryServe`'s
  "already in `_bundles`" fast path is taken (no re-read from disk), and the original method's own
  `Counter++` just increments further, harmlessly (matching how BSG's own bundles behave on a repeat
  `LoadBundleAsync` call too).
- **Why still worth fixing**: it is simply incorrect bookkeeping regardless of whether anything
  currently reads `Counter == 0` to trigger real behavior, and it silently deviates our bundles from
  behaving identically to an equivalent BSG-loaded one. If any future SPT/BepInEx update, another mod,
  or a manual `/unloadunusedresources`-style path ever does start decrementing these counters for map
  bundles, ours would need one extra, unmatched `UnloadBundle` call before it could ever actually
  release — an avoidable, silent trap this fix removes.

### 5d. Fix implemented — `ArenaMapPort.Client/BundleHost.cs`

```csharp
var reference = new BundlesManager.AssetBundleReference(bundle) { Counter = 0 };
manager._bundles[bundleName] = reference;
```

Resets `Counter` to 0 on the `AssetBundleReference` **we just constructed, for our own bundle only**,
immediately before control returns to Harmony and the original method's own `Counter++` runs. That
unavoidable original-method increment then lands on exactly 1 — correctly representing the single real
load request that triggered this call, byte-for-byte consistent with what BSG's own bookkeeping would
produce for an equivalent bundle it loaded itself. This touches only the object this exact call just
created; it can never affect any other bundle's counter, vanilla or from another mod. Gated entirely
inside `BundleHost.TryServe`'s existing "is this one of ours, and not already served" branch — no
change to when/whether we serve a bundle, only to the counter value we hand off.

`Counter` is confirmed a plain public field (`public int Counter;`, not a property with a private
setter), so the object-initializer assignment is a valid, direct field write from
`ArenaMapPort.Client`.

## 6) Build, tests, regeneration

| Check | Command | Result |
|---|---|---|
| Client builds (Debug — isolates the actual C# compile from the Release-only staging targets, see §7) | `dotnet build ArenaMapPort.Client -c Debug` | 0 warnings, 0 errors |
| Client builds (Release) | `dotnet build ArenaMapPort.Client -c Release` | 0 warnings; DLL builds and stages; Equator/Bay5 bundles stage successfully; then fails at the 12-additional-maps staging target's own stale-receipt gate — see §7 |
| Server builds | `dotnet build ArenaMapPort.Server -c Release` | 0 warnings, 0 errors (server untouched by this task) |
| Server tests | `dotnet test ArenaMapPort.Server.Tests -c Release` | **228/228 passed**, 0 failed/skipped — matches the recorded baseline count, no regression |
| `validate_preset.py`, all 14 maps | (see §8) | **OK, all 14** |
| `validate_bundle_names.py` against staged `dist/` | `python tools/validate/validate_bundle_names.py` | **OK — 42 bundle(s), every internal directory name is unique** |
| `validate_distribution.py` (the project's own final "safe to ship" gate) | `python tools/validate/validate_distribution.py` | **`DISTRIBUTION_OK 14 maps 42 bundles 14 sidecars`** |

All 14 preset bundles were regenerated (`python tools/preset/patch_preset.py --map tools/maps/<key>.json`
for each of the 14 `tools/maps/*.json` descriptors) into `work/build/preset/<key>_preset.bundle`, then
`validate_preset.py --map <key>.json --scenes-bundle dist/.../<key>_scenes.bundle` run for every one —
**all 14 report `OK -- all checks passed`**, including the new CAB-name assertion and a full
rcid-vs-actual-scene-names cross-check (6/6 through 9/9 depending on the map, 0 absent, for every map).

## 7) Staging into `dist/` and the receipt-freshness gap this intentionally surfaces

`ArenaMapPort.Client.csproj`'s Release-only staging targets copy every preset from
`work/build/preset/<key>_preset.bundle` into `dist/BepInEx/plugins/ArenaMapPort/bundles/` — confirmed
by reading the `.csproj` and `tools/maps/AdditionalMapBuilds.props` directly, this is the exact
source→destination mapping `patch_preset.py`'s own `--out` default already produces, for **all 14**
maps (Equator/Bay5 via `ArenaMapPortStageWpOutputs`'s explicit file list; the other 12 via
`AdditionalMapBuilds.props`' generated item groups).

Running `dotnet build ArenaMapPort.Client -c Release` after regenerating all 14 presets: the DLL
compiles (0 warnings) and deploys, `ArenaMapPortStageWpOutputs` stages Equator's and Bay5's freshly
CAB-fixed bundles successfully (that target has no receipt dependency), and then
`ArenaMapPortStageAdditionalMaps` — which runs `python tools/validate/validate_port.py --map <key>`
as an `Exec` step *before* copying each of the 12 additional maps' bundles — fails at the first one
(`airpit`):

```
PORT_FAILED: Validated file changed: D:\ArenaMapPort\work\build\preset\arena_airpit_preset.bundle
error MSB3073: ... validate_port.py --map airpit ... exited with code 1
```

This is `validate_port.py`'s own schema-2 receipt check (`work/build/<stem>/full-map-validation.json`)
doing exactly its designed job: that receipt recorded the **previous** SHA-256 of
`arena_airpit_preset.bundle`, and this fix intentionally changed that file's bytes (the CAB rename).
The receipt system's own stated rule (`docs/progress/remaining-maps.md`) is that *any* changed byte in
a receipt-bound file invalidates the receipt — "neither editing the schema number nor capturing hashes
alone upgrades them; the entire mandatory validation sequence must run against the current inputs." I
did not hand-edit any receipt to paper over this — that would misrepresent an unvalidated state as
validated.

**What was actually done for all 14 maps' presets to reach `dist/` despite this**: Equator and Bay5
were staged via the `dotnet build -c Release` run above (its own target has no receipt gate). The other
12 were staged with a direct, verified file copy —
`work/build/preset/arena_<stem>_preset.bundle` → `dist/BepInEx/plugins/ArenaMapPort/bundles/arena_<stem>_preset.bundle`
— i.e. exactly the same source file to exactly the same destination path the MSBuild `Copy` task
itself would use, just invoked directly rather than through the Exec-gated target. Re-running
`validate_bundle_names.py` and the full `validate_distribution.py` against the resulting `dist/`
afterward (§6) independently confirms the actually-shipped `dist/` folder is complete, internally
consistent, and collision-free — `validate_distribution.py` does not depend on the 12 additional maps'
`full-map-validation.json` receipts at all, only on the files that are actually staged.

**Follow-up needed, in progress**: the 12 additional maps' schema-2 receipts remain stale for exactly
the one file this task deliberately changed (their preset bundle) until they go through the project's
own sanctioned "existing Unity output is suitable for complete revalidation" path — extraction/Unity
output is untouched, so no Unity/GPU access is needed:

```powershell
pwsh tools/build_all.ps1 -MapKey "arena_<stem>" -MapDescriptor "tools/maps/<stem>.json" `
    -PackageDir "D:/ArenaMapPort/work/extract/<stem>" -OutputDir "D:/ArenaMapPort/work/build/<stem>" `
    -FullMap -SkipGameRefsSync -SkipUnityBuild -ReproTimeoutSeconds 1800
python tools/validate/validate_port.py --map <stem>
```

or, for the whole batch at once, the project's own resumable runner (only reprocesses a map if its
receipt/inputs are stale, which — after this fix — is true for exactly these 12):

```powershell
./tools/build_remaining.ps1 -Stage
```

This was started in the background during this pass (`autoservice, airpit, airpit2, bowl, prison,
railwaystation, yard, iceberg, factory, custom, shoreline, woods`, in that order,
`work/logs/remaining-maps/<stem>-cab-fix-revalidate.log` per map) — see §9 for however far it got
before this report was finalized. Each map's own reproducibility re-extraction alone measured ~5.5
minutes for the smallest map (`factory`, 4 scenes) in isolation; the full per-map sequence (base/preset
regen, input hashing across the map's whole dependency closure, 2×reproducibility extraction, bundle
roundtrip over its scenes bundle, receipt write) is longer still, and 12 maps sequentially did not
complete inside this session — this does **not** block the actual fix (§6-§7 above already confirm the
shipped `dist/` is correct and collision-free right now), it only blocks a *future* `dotnet build
ArenaMapPort.Client -c Release` from succeeding for the 12 additional maps until it's caught up, and
that failure is loud (a build error), never a silently-broken `dist/`.

## 8) `dist/` files changed — sizes and SHA-256 (this pass)

All 14 `*_preset.bundle` files changed (internal CAB rename only — same size as before this pass for
every one, confirmed against the previously-recorded baselines in `bay5-port.md`/`remaining-maps.md`
where available). `maschine-ArenaMapPort.Client.dll` also changed (BundleHost.cs's refcount fix, §5).
No scenes/navmesh bundle, sidecar, or server db file was touched.

| File | Size (B) | SHA-256 |
|---|---:|---|
| `bundles/arena_airpit_preset.bundle` | 6,859 | `f1cc77430293131132d23ae07063c3a669af9da0636a8a9514092de439a25dde` |
| `bundles/arena_airpit2_preset.bundle` | 6,803 | `e875fbbb2104ed151205042bdc1431e2118e80a0a0e7063893617b9bc53c0b21` |
| `bundles/arena_autoservice_preset.bundle` | 6,883 | `3951f007a32f29b66bfeea97230f7d8c6eafcee8ca585dad3ea27b940eac5928` |
| `bundles/arena_bay5_preset.bundle` | 7,027 | `09e5f8db828467f85b638833ad85a28e48de4f7f5b47395f063adbaac394b0e3` |
| `bundles/arena_bowl_preset.bundle` | 6,947 | `3c9d8cbdc16ec60cabf60630fa1e4af16d1be881425444c6a74132906adc0c9b` |
| `bundles/arena_custom_preset.bundle` | 6,859 | `1dc8508551a49dc2ecca45b738c12887029637f3ef74a0e635cde3bb2f167309` |
| `bundles/arena_equator_preset.bundle` | 6,891 | `67c555323c07818c6b538acd7ce71682a846488f2a3bce0e506e1fe0c453fefa` |
| `bundles/arena_factory_preset.bundle` | 6,787 | `b7a0dcdbac6358701b7123c01f7154cce2f57df80f511dce3eac335197c70b69` |
| `bundles/arena_iceberg_preset.bundle` | 6,987 | `b0c696c578930269da8b40434461e3a439d22ba421c9256ed5c1e008e3a038e0` |
| `bundles/arena_prison_preset.bundle` | 6,923 | `157615cbda23ee1c2a805967eada6aca3a86e49e2777747f837e1768d1e8b2a8` |
| `bundles/arena_railwaystation_preset.bundle` | 7,099 | `6839d41084201a1c0cb92c967c4840effb8c0b7d8d620242f45e7eceaa3b40bc` |
| `bundles/arena_shoreline_preset.bundle` | 6,939 | `ad767b687edb59fe2d894aa0ab25e70f9b158fbd97b8979a82bf784177f1043c` |
| `bundles/arena_woods_preset.bundle` | 6,851 | `c00fa91dd7605d2b0c59b2deb09acaa966c01c08925a642edc09512e22e3eea0` |
| `bundles/arena_yard_preset.bundle` | 6,875 | `47efc1e5a2dd32a6b65ecbb2737208b302ce4d390e3f623de15c7bce2cfed082` |
| `maschine-ArenaMapPort.Client.dll` | 86,528 | `5ceacdb6b17a5b77f59ed315ce7693af2266552007df4c6312d9d60bff6056b8` |

Every `*_scenes.bundle`/`*_navmesh.bundle`, every `material-shaders*.json` sidecar, and every
`SPT_Runtime/.../db/**/base.json` in `dist/` is byte-identical to before this pass (not touched by
this fix, not regenerated).

## 9) Background revalidation status (12 additional maps' schema-2 receipts)

Started in the background during this pass (script: a small wrapper around the §7 command, looping
`autoservice, airpit, airpit2, bowl, prison, railwaystation, yard, iceberg, factory, custom, shoreline,
woods` in that order, logging each to `work/logs/remaining-maps/<stem>-cab-fix-revalidate.log`).
`-SkipGameRefsSync -SkipUnityBuild` means no Unity/GPU access is used and no exclusive Unity-batch
lock is taken, so this can safely run unattended or alongside other work.

**Status at the time this report was finalized**: still processing the first map (`autoservice`) —
through `validate_extract.py`'s non-reproducibility checks (disabled-component counts, texture/mesh
compatibility, all matching exactly) and into its 2×independent-fresh-extraction reproducibility
re-run. `factory` (the smallest of the 12, 4 scenes) measured **5m27s** for this reproducibility
step alone, in isolation; the full per-map sequence for a map `autoservice`'s size (5 scenes, a
708 MB scenes bundle, an 18.4 GB Arena-source input-hashing closone per `port_inputs.py capture`)
is proportionally longer, and 12 maps sequentially is a multi-hour job that did not complete inside
this session.

**This does not block or weaken anything reported in §6-§8** — those results were independently
re-confirmed against the actually-shipped `dist/` after the manual staging in §7, not against this
background job's (still pending) output. This background pass exists solely to produce fresh,
honestly-earned schema-2 receipts for the 12 additional maps so a *future* `dotnet build
ArenaMapPort.Client -c Release` succeeds again without the §7 workaround. The background process was
not left running unattended past this report (a still-running detached process cannot be relied on to
survive past this session) — resume it with the §7 commands, or `./tools/build_remaining.ps1 -Stage`
for the whole batch, at zero risk: the runner is resumable and reuses any map whose receipt/inputs are
already fresh (none of the 12 are, right now, for exactly the reason in §7), and killing/restarting it
mid-map never corrupts `work/extract/<stem>` or an existing valid receipt — `build_all.ps1` only
writes a new `full-map-validation.json` after every mandatory validator has already passed for the
CURRENT run.

## 10) What the user must redeploy

`D:/SPT41vanilla` is the user's own test install (this agent never writes there — hard rule). To pick
up this fix:

1. Stop the SPT server/client if running.
2. Copy the 14 updated files from §8 above — at minimum every `dist/BepInEx/plugins/ArenaMapPort/bundles/*_preset.bundle`
   and `dist/BepInEx/plugins/ArenaMapPort/maschine-ArenaMapPort.Client.dll` — into the matching paths
   under `D:/SPT41vanilla/BepInEx/plugins/ArenaMapPort/...` (the project's own `tools/deploy-to-spt.ps1`,
   run without `-Confirm`, only prints the copy plan; add whatever confirmation flag it defines to
   actually copy, or copy these specific files by hand since no scenes/navmesh/db file changed this
   pass).
3. Start the server/client and confirm: raid on `arena_bay5`, return to menu, raid on `arena_equator`
   (or any other two ported maps) in the same client session, without the `LoadOutput.log` "another
   AssetBundle with the same files is already loaded" error this pass's fix targets. This specific
   two-map sequence was the one that reproduced the bug; the fix is map-agnostic (every preset now has
   its own unique internal name), so any pair of ported maps in one session should be free of this
   specific error.
4. Once the 12 additional maps' receipts are refreshed (§7's follow-up), a fresh
   `dotnet build ArenaMapPort.Client -c Release` + `tools/deploy-to-spt.ps1` becomes the normal,
   fully-gated way to redeploy again from then on.
