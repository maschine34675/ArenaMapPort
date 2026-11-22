# DynamicMaps 1.3.0 owned overview integration — 2026-11-20

The user requested adapting both ArenaMapPort's overviews and ArenaMode's Domination
overlay to DynamicMaps 1.3.0, retaining the overview files in ArenaMapPort's own
folder. This record covers only the ArenaMapPort loader. ArenaMode is a separate
repository and implementation.

## Why the loader changed

DynamicMaps 1.3.0 changed `MapDef.LoadFromPath(string)` to the two-argument overload,
validates image existence inside that method, and removes previously registered
definitions absent from every new directory scan. The old integration would skip
it at the API guard. Merely changing the reflected signature would still resolve
SVGs too late, and the old recursive per-package scans would discard earlier maps.

## Implementation

`DynamicMapsIntegration` retains the verified 1.2.1 path unchanged. For 1.3 it binds
`DynamicMapsOwnedCatalog`, checks the required native types/members, and patches
`LoadMapDefsFromPath("Maps")` with a prefix/finalizer scope. Within that scope,
a prefix on the first native `RegisterMapPreferences` call merges the owned maps.

The adapter reads each exact allowlisted owned JSON with Newtonsoft.Json into the
native `MapDef` type, validates the required fields and exactly one ground layer,
and resolves only the existing `Maps/ArenaMapPort_<name>/Layers/Ground.svg` token
to its matching owned file. Static marker paths, geometry, bounds, labels and
images are unchanged. It merges the resulting definitions into the native catalogue
without clearing/re-scanning any other map. A weak per-dropdown cache preserves
unchanged definition objects across scans so retained zoom, position and floor survive.
Only its own keys are replaced or removed. Failed packages produce a warning and
do not prevent other packages.

Stable `ArenaMapPort/<name>` selection IDs and the native addon flag allow native
map preferences to recognize these choices. Native preference registration and
filtered dropdown refresh run after the merge; the current location filter stays
intact. DynamicMaps can persist its own preferences as usual. ArenaMapPort itself
does not write third-party files or configuration.

## Offline evidence

- Actual release client DLL contracts checked: 1.2.1 SHA256
  `C41507EB1EDB6D6C8101314FEDF9EB52C874C980E757CBC8348F084D2231B1B8`,
  1.3.0 SHA256
  `87878B34A7BB4D50DCF05DAA6CD9A93A27543B0C26AA38FA213C40053A6DB19B`.
- Production-linked tests cover all six owned packs, retained vanilla/addon object
  identity, active filtering, repeated native scans, deletion and recovery,
  malformed/incomplete definitions, incorrect image paths, unrelated requests and
  missing API members. Existing 1.2.1 asset/reentry tests remain.
- Three contract negative controls remove actual loader/preference methods in memory.
  Eight source mutants erase foreign maps, omit image resolution, break preference
  ordering/object identity, or remove request scoping/finalization;
  each must reach and fail its named assertion, followed by a passing clean baseline.
- `tools/verify-dynamicmaps.ps1` runs these checks. Its no-staging target file only
  affects the explicit offline invocation; normal release staging is unchanged.

## Native acceptance

Passed on SPT-GPU-2 on 2026-11-20, in fresh EFT instance
`6b4593a1e0134aea8cdc58729e1bfc09`. The VM payload, host disk and loaded MVIDs
matched the actual tested builds:

| Component | Source | SHA256 | Loaded MVID |
|---|---|---|---|
| ArenaMapPort.Client | `b7fb819` | `1D2B9D3AD148119EF593B14E6AF1C7A4AE07E1F4F3929E3DE1500CC5434AE8A8` | `0f3dd719-c0d9-40ec-894b-ea67a094776a` |
| ArenaMode.Client | `794453e` | `9FFB7C339C46A0268B7D20B25CC8576281A58DDC442DE63D00E1AAB0D7CB1103` | `4128ce9d-9caa-462c-9036-c07b1aa945fc` |
| DynamicMaps 1.3.0 | official release | `87878B34A7BB4D50DCF05DAA6CD9A93A27543B0C26AA38FA213C40053A6DB19B` | `c3dde04b-4ada-4109-925d-43266af7ffb9` |

Only the owned MapPort client and ArenaMode client/BigBrain companion DLLs were
deployed. The already-installed DynamicMaps 1.3.0 was not replaced. Core, server
binaries and map assets were not redeployed.

- The native catalogue contained 17 definitions: all 11 baseline maps plus all six
  owned packs (Equator, Bay5, AirPit, Bowl, Iceberg and AutoService/Chop Shop).
  Their exact JSON paths remained under `maschine-ArenaMapPort/dynamicmaps`.
- Iceberg rendered its owned SVG in Main, Mini and Peek views. A/B/C labels,
  native world-to-map projection, neutral and both faction colours matched the
  live objectives. Closing/reopening retained the same MapDef object, centre,
  zoom and ground level. The real time-limit transition removed owned objectives
  while native map markers remained; leaving returned to MainMenu with no overlay.
- Factory provided the vanilla-map regression and native floor-filter case.
  This random round put A on level 1, B on -1 and C on 0. At selected level 1,
  only A was visible; the other two were inactive with image alpha zero.
  Main/Mini/Peek, retained view, contested yellow and manual-leave cleanup passed.
  One asynchronous Factory ownership sample straddled a 476 ms state transition
  and is explicitly excluded from colour acceptance. The later coherent contested
  pair and all Iceberg faction comparisons passed strict colour checks.
- Native reports, screenshots and the independent read-only validator are kept in
  the ignored `work/dynamicmaps130-impl/evidence` directory. The validator rejects
  25 deliberately faulty evidence fixtures; both parent and independent runs pass.

Temporary settings were RAM-only and restored, including the original map hotkey,
zoom, player marker and minimap settings. The server's generated settings mirror
was refreshed through its existing native menu callback. Its six previously stale
values now match the unchanged client CFG; the three Domination test values were
restored. Six native map-preference additions already present on GPU-2 propagated
to the VM through Syncthing. This is DynamicMaps' normal preference registration,
not a write into its installation folder.

All 93 captured DynamicMaps installation files, 12 owned overview files and 49
PitFireTeam records per host stayed byte-identical. Client setting values stayed
unchanged apart from those new native map-selection entries. Profiles changed only
their normal health/hideout clock fields. Final state is MainMenu with test hooks
off and no active test lease.

Limits: 1.2.1 passed production-linked and official-binary checks, but was not
reinstalled for a fresh native raid. The other five owned maps passed registration
and package checks; the fresh ported-map raid was Iceberg. ArenaMode's separate
corpse-cleanup adapter is unchanged and retains its 1.3 version warning. One native
`BotFirstAid`/MedsController null reference occurred during Iceberg teardown; no
changed map adapter appears in that stack. It is recorded separately and not
diagnosed as part of this change. Scoped adapter warning/error queries were empty.

The commits following these tested source commits contain documentation only;
the tested binaries were not rebuilt solely to update their commit suffix.

## Independent-review corrections — 2026-11-20

The first implementation (`f3f4ca6`, not deployed) passed its initial 22 cases but
the parent review found two omissions: a postfix merge happens after native preference
registration has already clamped an owned selection to Automatic when another addon
shares that location; reparsing unchanged definitions also breaks the native reference
equality check and resets the retained view when the map reopens.

The revised adapter merges in a prefix on `RegisterMapPreferences`, within a per-dropdown
`LoadMapDefsFromPath("Maps")` scope established by a prefix and removed by a finalizer.
There is no second preference registration or manual UI refresh. Nested unrelated scans
temporarily suppress the scope, and native exceptions retain their original propagation.
The first native preference rebind now sees all choices. A weak per-dropdown cache keeps
the same MapDef object while JSON/SVG timestamps and lengths match; changed, missing or
invalid inputs invalidate only that package. This preserves retained viewport/floor state.

Five additional production-linked cases cover native preference clamping, reference-based
view retention, nested/exceptional scope cleanup, and the actual production Install hooks.
The latter two use real Harmony on minimal native API fixtures, including a lowest-priority
skip-original prefix after our scope prefix. The original native exception is preserved.
Eight source mutants now include the
exact early-preference/late-merge failure, forced reparsing and leaked scope. The actual
1.3 DLL contract also checks that map-dictionary mutations finish before its one preference
call and that dropdown refresh follows; removing that call is a third metadata negative
control. The integration mutants move the actual installed preference prefix to a postfix
and remove the installed finalizer; the real-patching tests must reject both.

## 2026-11-22 - Lead's notes after the merge

The record above is the state of 2026-11-20 (and the branch's own later sections); it is unchanged. The merge into `feature/yard` is `4587e5b` (user, 2026-11-22: "die beiden anderen zweige können gemerged werden, Codex hat dazu auch irgendwas verfasst"); the lead added `3dcf98d`. The full record is [release-preparation-20261121.md](release-preparation-20261121.md) "2026-11-22 - The two DynamicMaps branches merged, the review, the gates and the host run of the merged build".

- **Six or seven packs.** The statement of "six owned packs" (about line 45) is the state of 2026-11-20. Block is the seventh owned pack since `feature/block-dynamicmaps`; `DynamicMapAssets.Maps` lists seven names and the tests loop over seven.
- **Model and real DLL (review TEST-2).** The xunit tests prove the adapter against a MODEL of DynamicMaps 1.3 (`DynamicMapsNativeTestDoubles.cs`). Only `tools/tests/test_dynamicmaps_contracts.ps1` (signatures and call order in the real DLLs) and a native raid touch the real DLL. The three PowerShell checks (`test_dynamicmaps_contracts.ps1` and `test_dynamicmaps_mutants.ps1`, both run by `tools/verify-dynamicmaps.ps1`, and `test_dynamicmaps_deploy.ps1`) are run by hand; no automated suite calls them. Not tested by the xunit tests: the 1.2.1 branch through `Install`, and the fallback path of `Install` (bind failure, unpatch, one warning).
- **Contract check rerun, 2026-11-22.** `test_dynamicmaps_contracts.ps1` against the real DLLs (1.2.1 from the other session's ignored `work/dynamicmaps-130-audit/release-1.2.1/...`, 1.3.0 from the mirror): `DynamicMaps 1.2.1 actual DLL contract passed`, `DynamicMaps 1.3.0 actual DLL contract passed`, `DYNAMICMAPS_REAL_CONTRACTS_PASSED`; `test_dynamicmaps_mutants.ps1`: eight source faults rejected, `DYNAMICMAPS_SOURCE_MUTANTS_REJECTED`. No tracked file names a 1.2.1 DLL.
- **Known limit CODE-1 (confirmed by review, not changed).** After DynamicMaps 1.3.0 fails to load an owned overview and calls its `RemoveUnavailableMap`, the next catalogue scan re-adds the same cached definition (native packs stay removed until their file changes). A persistently failing owned overview is therefore tried again each time the map screen is opened, with one more DynamicMaps error line each time. No shipped pack fails. A follow-up.
- **`3dcf98d` (CODE-2).** `Plugin.Awake` wraps `DynamicMapsIntegration.Install(_harmony)` in try/catch: one Warning `[DynamicMaps] Optional overview integration could not be installed (...)`; the installs after it still run.
- **EV-1, a fact for the user.** The native acceptance section says the ArenaMode client and BigBrain companion DLLs were deployed to GPU-2 for that test. It does not say by which path. This project's deploy script (`tools/deploy-to-spt.ps1`) has no mode for ArenaMode's files, and the lead has not established how those two files were written.
- **EV-2, a fact for the user.** That work created git worktrees of the user's ArenaMode repository. `git -C D:/SPT41/Development/ArenaMode worktree list` (read-only, lead, 2026-11-22) lists `D:/ArenaMapPort/work/dynamicmaps130-impl/ArenaMode` on a branch `fix/dynamicmaps-130` and `ArenaMode-vw1` to `-vw6` (detached) under the same folder, beside the user's own entries. Neither this record nor `yard-dynamicmaps.md` mentioned it. The first paragraph of this record says the user asked that session for the ArenaMode adaptation too. The lead changed nothing in the ArenaMode repository and reports it to the user; whether the entries stay is the user's decision.
- **Seven overviews on the merged build.** On `0.7.0+3dcf98d`, GPU-2, 2026-11-22 (about 09:46 to 09:56, one EFT process): each of the seven overviews opened once in a plain PMC raid (Block at two positions), with DynamicMaps 1.3.0; 0 Warnings from `maschine-ArenaMapPort` and from DynamicMaps. The Warning "DynamicMaps API differs from verified 1.2.1" of earlier host sessions is gone. Not checked there: the minimap and peek views, ArenaMode and its Domination overlay, DynamicMaps 1.2.1 in game, a rescan after a pack file changed. The native loader evidence of this record (Iceberg and Factory, 2026-11-20) is unchanged; the author has not looked at any overview.

## 2026-11-22 - CODE-1 fixed: an owned overview DynamicMaps could not load stays out until its files change

Follow-up of the known limit CODE-1 above (brief by the lead). Written and verified on `feature/yard` at `3dcf98d` in the worktree `.claude/worktrees/cool-mclean-d3f849` (branch `claude/heuristic-torvalds-934d8f`), then fast-forwarded to `d3609ff` (the lead's texts and records, no code), on which the change sits; **not committed, not deployed, not host-checked.** The bullet "Known limit CODE-1" above stays as written (append-only); this section supersedes it.

**The defect.** DynamicMaps 1.3.0 calls `MapSelectDropdown.RemoveUnavailableMap(MapDef)` from exactly one place, the catch block of `ModdedMapScreen.ChangeMap` after `MapView.LoadMap` threw (upstream source and the release DLL; it logs `Failed to load map ...`, removes the definition, hides the display and unloads the view). `RemoveUnavailableMap` removes every `_mapDefs` key holding that object and keeps `_writeTimes`, so the next native scan skips the unchanged file and a native pack stays out until its file changes. `PreparePreferences` removed its seven keys from both dictionaries on every scan and re-added the cached object whenever the JSON/SVG stamps matched, so an owned overview that DynamicMaps cannot load (a damaged `Ground.svg`) came back at the next scan (each opening of the map screen while it is not shown), was selected again by `LoadPreferredMap` and failed again, with one more DynamicMaps error line and a hidden display each time. DynamicMaps 1.2.1 has no `RemoveUnavailableMap` (Cecil on the release DLL), so the 1.2.1 path is not affected and unchanged.

**Shape: (a), a postfix on `RemoveUnavailableMap`.** It sees DynamicMaps' own decision about the exact object, at the moment it is made. Shape (b) (no new patch; at the next scan start, a definition the adapter had added that is missing from `_mapDefs` counts as rejected) would infer the event: in 1.3.0 only `LoadMapDefsFromPath` and `RemoveUnavailableMap` remove definitions, but a native scan that throws after its pruning and before `RegisterMapPreferences` removes our keys without the merge re-adding them, so (b) needs a per-scan "added" flag to avoid marking a definition DynamicMaps never rejected, and any later DynamicMaps that removes for another reason would be misread the same way. The price of (a) is one more bound native member, checked like the others.

**Implementation** (`DynamicMapsOwnedCatalog.cs`, `DynamicMapsIntegration.cs`):
- The catalogue binds `RemoveUnavailableMap(MapDef)` (public instance, `void`) in its constructor beside the other members. A DynamicMaps without it fails the bind and the 1.3 path is skipped with the existing single Warning `Optional map loader unavailable; expected DynamicMaps 1.2.1 or 1.3.0 API: ...` (the install's catch also unpatches the new postfix).
- `Install` patches it with `CatalogUnavailablePostfix`, wrapped like the other hooks (an exception becomes one WarnOnce `Could not record an unavailable owned map: ...`, nothing reaches DynamicMaps).
- `MarkUnavailable(dropdown, definition, warn)` looks only in that dropdown's weak cache (`TryGetValue`, never creates one) for the owned entry whose value is exactly the removed object (`ReferenceEquals`), sets its `Unavailable` flag and warns once per map: `[DynamicMaps] DynamicMaps could not load the owned <map> overview; it stays out of the map list until one of its files changes.` Foreign definitions, another dropdown's object, a dropdown without a cache and null change nothing. The postfix writes nothing into `_mapDefs` or `_writeTimes`.
- `PreparePreferences` still removes its own key from both dictionaries first, checks both files and the stamps, and then leaves a flagged entry out (`if (cached.Unavailable) continue;`). A changed stamp (time or length of the JSON or the SVG) builds a new cache entry without the flag, and a missing or invalid file evicts the entry as before, so DynamicMaps can try a repaired file again: the native rule, with the SVG stamp included because the owned definition depends on it.
- While an owned overview is left out, DynamicMaps' `RegisterMapPreferences` resets a player's choice of it to Automatic, as it does for a native pack it removed; after a repair the player chooses it again. Unchanged native behaviour, not touched.

**Tests** (production-linked, against the MODEL of 1.3 in `DynamicMapsNativeTestDoubles.cs`):
- The model gained `RemoveUnavailableMap` with 1.3.0's semantics (removes every key holding the object, keeps `_writeTimes`, refreshes). Its `Scan` now ends the scan scope before selecting, as `ModdedMapScreen` does, and then models `LoadPreferredMap` (the preferred choice, then each choice still in the catalogue, until one stays) and `ChangeMap` (the same reference keeps the view; a display name in `Unloadable` fails: `LoadFailures` + 1, `RemoveUnavailableMap`, the installed postfix, the view unloaded). A 1.3-shaped dropdown without `RemoveUnavailableMap` lives in `DynamicMaps.UI.Controls.WithoutRemoval`. The older catalogue and integration cases pass unchanged on the revised model.
- `DynamicMapsOwnedCatalogTests`: `ApiBindingRefusesADropdownWithoutRemoveUnavailableMap`; `AnOwnedDefinitionDynamicMapsCouldNotLoadStaysOutUntilOneOfItsFilesChanges` (a damaged owned Equator beside a third-party Equator addon and a vanilla map: the first opening fails once and falls back to the addon, the write time is kept; reopening with unchanged files leaves it out with no second failure, all foreign objects and the six other owned maps unchanged; a changed SVG offers a new object again and, still damaged, fails again; a repaired SVG is loaded and selected); `ARejectedForeignOrOtherDropdownDefinitionLeavesTheOwnedCatalogueAlone` (a rejected native map, another dropdown's object, a dropdown without a cache and null mark nothing; the other dropdown's own rejection stays in that dropdown).
- `DynamicMapsIntegrationTests`: `InstalledUnavailableHookKeepsARejectedOwnedDefinitionOutUntilItsFilesChange` runs production `Install` with real Harmony: after `RemoveUnavailableMap` on the owned Equator object the next `LoadMapDefsFromPath("Maps")` leaves it out (six of seven), and a changed SVG brings a new object back.
- `test_dynamicmaps_mutants.ps1`: four new source mutants, twelve in all: `readd-unavailable-owned-map` (the skip removed) and `keep-unavailable-after-file-change` (the flag carried into the new entry) must fail the stays-out case, `mark-any-removed-definition` (identity check removed) the foreign case, and the integration mutant `omit-unavailable-hook` (postfix not installed) the real-Harmony case.
- `test_dynamicmaps_contracts.ps1`, 1.3.0 only: the signature `RemoveUnavailableMap(DynamicMaps.Data.MapDef)` (public, `void`) and that `DynamicMaps.UI.ModdedMapScreen.ChangeMap` calls it; a fourth in-memory negative control renames the method and must be rejected with a contract mismatch naming it.

**Runs** (VM, 2026-11-22, in the worktree on `3dcf98d`; no other dotnet process was running at the start):
- `dotnet test ArenaMapPort.Server.Tests/ArenaMapPort.Server.Tests.csproj -c Release`: the worktree has no `work/`, so the server build's staging step stops at `Missing original map descriptions`; rerun with `-p:CustomAfterMicrosoftCommonTargets=tools/tests/DynamicMapsNoStage.targets` (no dist staging) and `ARENAMAPPORT_WORK_DIR=D:\ArenaMapPort\work` (real-data tests read the main checkout's inputs): 3,830 of 3,831 passed at about 10:18; the one failure was `CryptoThroughputTests.Cng_ReachesAtLeast300MiBPerSecond_EncryptingAndDecrypting64MiB` (`CNG encrypts at 294 MiB/s, below 300` under the parallel suite), which passed when run alone right after. No DynamicMaps code is involved in it.
- `pwsh -NoProfile -File tools/verify-dynamicmaps.ps1 -DynamicMaps121 D:\ArenaMapPort\work\dynamicmaps-130-audit\release-1.2.1\BepInEx\plugins\mpstark-dynamicmaps\DynamicMaps.dll -DynamicMaps130 D:\SPT41\BepInEx\plugins\mpstark-dynamicmaps\DynamicMaps.dll` (10:18 to 10:20), exit 0: the three xunit groups 32 of 32 (`DynamicMapAssetsTests`, `DynamicMapsOwnedCatalogTests`, `DynamicMapsIntegrationTests`); `DynamicMaps 1.2.1 actual DLL contract passed: C41507EB...B1B8`, `DynamicMaps 1.3.0 actual DLL contract passed: 87878B34...B19B` (the hashes of the release DLLs recorded above; the 1.3.0 file of the mirror and of `work/dynamicmaps-130-audit/release-1.3.0` are identical), the four negative controls rejected, `DYNAMICMAPS_REAL_CONTRACTS_PASSED`; twelve `Rejected source fault:` lines, each at its named assertion, the clean baseline passing, `DYNAMICMAPS_SOURCE_MUTANTS_REJECTED`; the DllOnly client build without staging, 0 warnings; `DYNAMICMAPS_MAPPORT_VERIFIED`. The mutant results are under the worktree's ignored `work/dynamicmaps-checks/`.
- `test_dynamicmaps_deploy.ps1` was not run (no deploy code changed).

**Not checked.** No host run: the check with a deliberately damaged copy of one owned SVG needs the lead and the user's test host (expected: one `Failed to load map` error from DynamicMaps and one ArenaMapPort Warning at the first opening, none at later openings while the files are unchanged, where a raid on that map then shows no overview, DynamicMaps' path for a location without a map, and the overview back after the file is restored). The test model is not DynamicMaps; only the contract check reads the real DLL. A DynamicMaps update that renames or removes `RemoveUnavailableMap` now skips the whole 1.3 integration (one Warning) instead of only losing this memory; that is the project's rule for unknown versions.
