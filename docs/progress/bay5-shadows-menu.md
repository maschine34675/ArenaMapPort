# Bay5 normal textures, hands highlight and focused debug UI

## 2026-09-22 — request and diagnosis

Baseline `7147e8e`, branch `feature/bay5-shadows-menu`. The user accepted the brighter exterior
ground, then supplied four screenshots under ignored `analysis/screenshots/schatten/` and
`analysis/screenshots/schleier/`: dark ground seemed not to receive shadows, cardboard boxes and
the held knife had a pale veil. They also requested only Equator/Bay5 settings in F12 and a Bay5
copy of the Equator direct-start button.

### Confirmed import defect

The correct GenBaseCo boxes are `pallet_cardboard_box_P (8)/model_LOD0`, inspected near
`(106,-3.7,62)`, yaw 225 / pitch -5. Their shader is `p0/Reflective/Bumped Specular`, not the SMap
shader on a different nearby pallet. The original normal PNGs remain DXT5nm packed: normal X is
stored in alpha, while red is 255. Importing these PNGs again as Unity NormalMap incorrectly uses
their red as X and writes 255 throughout output alpha. The runtime shader decodes X as
`2 * R * A - 1`; flat-ish source normals therefore become sideways normals.

| Source texture | Role | Source alpha mean | Old bundle alpha |
|---|---|---:|---:|
| `41c4c6c0e6c55dca` | cardboard_box_n_new |130.277|255 everywhere|
| `f3047100df028425` | pallete_combo_n |125.309|255 everywhere|
| `87903b22649df886` | road layer0 |127.11|255 everywhere|
| `cf4f8b3d2cea6ce6` | road layer1 |122.85|255 everywhere|
| `ddb8f105f208ae75` | road layer2 |127.38|255 everywhere|

The ground shader (`Custom/Vert Paint Shader Solid`) also decodes packed R*A/G. Its mesh
`9e8f809ed2f8a182` has 2,221 vertices; original, extracted and built mesh normals/tangents match
exactly. This is a texture-channel defect, not missing mesh normals. These measurements come
from decoded source assets and actual staged bundles, with shader DXBC checked independently.

Earlier native isolation confirmed that the donor sun casts real geometric shadows (the player's
shadow disappears when Light.shadows is None and returns with Soft). Disabling cloud shadows,
rain, untextured dust, local cubemaps and specular response did not explain the correct boxes.
The temporary diagnostic controls were removed from the production change. No global ambient,
sun, SSR or reflection override is retained from those experiments.

The classic box shader also writes Unity-SH diffuse into GBuffer3; its vertex colors are unused
in that pass. `_ReflectColor.alpha` does not provide an uncapped local-reflection term there.
Both facts matter when interpreting failed reflection-only experiments with corrupted normals.

### Separate held-item tuning

The ER BAYONET is a stock SPT asset, outside the map texture importer. The donor applies a
separate ambient highlight to the Hands stencil. `HandsAmbientHighlightMultiplier` scales only
its captured min/max values, with neutral default 1 and local Bay5 value 0.35. World/character
highlight and world ambient light are unchanged. Native 1 / 0 / 0.35 comparisons reduce the broad
highlight on the blade; a bright metallic edge remains. This is optional tuning, not restoration
of missing Arena highlight data: Arena and donor original ranges already match.

The component caches original fields, preserves independent changes, waits for the raid camera,
and restores through cached references on map teardown. Source cleanup received independent
review. Final-build native evidence follows below when available.

### Focused configuration

`MapConfigVisibility` sets `Browsable(false)` metadata after all configuration binds for sections
whose exact map-key suffix belongs to the twelve unworked maps. Equator and Bay5 remain visible.
Saved values, ranges, defaults, descriptions and runtime binding for every map are retained.
The real BepInEx/ConfigurationManager harness passed; native F12 with advanced settings and a
search for `arena_bowl` showed no Bowl entries. New entries also pass through this filter.

### Bay5 direct start

The separate `D:/SPT41/Development/ArenaMapDebug` repository contains commit `86f9524` on
`feature/bay5-debug`, version 0.1.0. Installed DLL SHA-256:
`B670184624E49C49E0181FBB72F075946EB47C478070B8F23E05D46B01B278A7`.
Its 38 tests passed. At 1920x1080 the Bay5/Equator buttons have a 15px gap and both pass native
hit testing. Other resolutions have not been claimed as native-tested.

The shared path selects the exact PMC location, preserves the existing offline settings,
skips selection/confirmation and sets deployment countdown 0. A living debug disconnect saves
as Runner and returns directly to the menu. Actual death behavior is unchanged. Native evidence:

- Equator, instance `7f038b39`: start 21:18:24.613, countdown 0 at 21:18:59.651,
  protected disconnect 21:19:26.969, direct menu 21:19:31.563.
- Bay5, instance `dfb5857f`: start 21:29:51.916, countdown 0 at 21:30:55.656,
  protected disconnect 21:39:19.512, direct menu 21:39:24.290.
- Bay5 repeat, instance `8bd0d416`: start 21:45:01.434, countdown 0 at 21:46:05.030,
  protected disconnect 21:51:00.406, direct menu 21:51:05.428.

### Test-profile incident and remaining import gap

A long diagnostic raid expired at 21:16:38 while analysis continued. Normal SPT MIA behavior
reduced the character's health. This was not a protected disconnect. The test consumed one
Car kit 112/220 and two AI-2 kits 100/100 through normal HealAll, then one MRE and one bottle of
water through normal UseAll. Remaining health regenerated in the menu. No profile-file edits
or third-party configuration changes were used; this session does not claim an unchanged profile.
Final health/state will be recorded with final verification. Subsequent comparisons are short.

The source DUST effect is also missing a proper 16x16x8 Texture3D import. Its exported flattened
PNG is not a faithful alpha-volume decode. Disabling the one incomplete renderer did not remove
the reported box veil, so no dust suppression or speculative volume asset is shipped here.
That is a separate port-fidelity follow-up; glass remains deferred as requested earlier.

## Rebuild and final native acceptance

Pending: corrected source-preserving importer, built-normal validation, scoped deployment and
fresh-process Bay5/Equator comparison. Do not treat the diagnosis above as final visual acceptance.

Local regression checks at 22:06–22:07: Python suite 209 total, 182 passed and 27 skipped;
.NET server/shared suite 348 passed, 0 failed, 0 skipped. Four existing nullable warnings remain in
the test compilation. The final hands/config client build passed with 0 warnings and 0 errors.
The exact importer-policy and rebuilt-asset checks are additional acceptance requirements below.

### Corrected importer and repeatable build inputs

`TextureImportDriver` now retains the source PNG as a Default Texture2D with its original
color space and alpha. It uses explicit DXT5/BC7/DXT1 Windows compression at quality 100;
no normal swizzle, alpha dilation or resizing is performed. `NormalTextureImportPolicy`
checks source format, actual channel evidence and material slots. Unknown or conflicting
encoding is rejected. The two source-authored sRGB Cloth normal/mask exceptions on Equator
retain their source data and color space. Cubemap and generated terrain-array paths are unchanged.

The production policy passed 38 regression cases and all 559 source normal-flagged PNGs:
Bay5 has 327 packed textures; Equator has 230 packed textures and two Cloth exceptions.
Only 307 Bay5 and 212 Equator textures are reachable from imported renderers; each package's
other 20 belong solely to explicitly skipped skinned renderers.

The mandatory roundtrip validator now compares every required normal texture's decoded RGBA,
source color space, dimensions and compression family. Fixed recompression tolerances are
MAE <= 3 bytes per channel and at most 1% of pixels with error > 16 bytes. It separately catches
collapsed alpha and missing/duplicate required textures. Eight synthetic positive/negative
tests passed in both the author and independent parent runs. An independent source-to-bundle
vector comparator also checks normal directions; it does not import the production policy.

Build inputs must be the current `work/extract/bay5-native-features` and
`work/extract/equator-native-features` packages. The older base Bay5 package lacks the background;
the FullMap guard rejected an initial stale-path invocation before Unity ran. All five target
normal PNGs are identical in old and current packages, so their earlier defect evidence remains
valid. The current build command in AGENTS.md is updated to preserve later native features.

```powershell
& ./tools/build_all.ps1 -MapKey arena_bay5 -MapDescriptor D:/ArenaMapPort/tools/maps/bay5.json -PackageDir D:/ArenaMapPort/work/extract/bay5-native-features -OutputDir D:/ArenaMapPort/work/build/bay5 -FullMap -BakeNavMesh $true -Verify $true -SkipGameRefsSync
& ./tools/build_all.ps1 -MapKey arena_equator -MapDescriptor D:/ArenaMapPort/tools/maps/equator.json -PackageDir D:/ArenaMapPort/work/extract/equator-native-features -OutputDir D:/ArenaMapPort/work/build/full -BakeNavMesh $true -Verify $true -SkipGameRefsSync
```

The first Unity process refreshed its old compilation graph before discovering the new policy
source, logged CS0246, then compiled the updated graph successfully and imported all 327 Bay5
normals. The strict log gate still rejects any compiler error. A clean repeat is required;
the error is not allowlisted and the validator is not bypassed.

At 22:24 the normal health UI confirmed 440/440 HP, energy 100/100 and hydration 100/100;
currency remained 382,144 roubles. Screenshot `work/verify/bay5-shadows-menu/smProfileRestored.png`.
EFT was exited normally while builds continued. Consumed medical/food items and the MIA record
were not restored by file edits. The expanded Python suite passed: 217 total, 190 passed,
27 skipped, no failures (receipt `python-tests-final.txt`).

### Editor-startup reimport regression

The rejected first Bay5 build was inspected independently before staging. Of 307 reachable
normals, 259 had correct source channels and 48 still had opaque alpha. The three yard ground
layers passed with mean direction errors 0.02035, 0.01058 and 0.000086 degrees, but the two
reported cardboard/pallet normals remained almost 90 degrees wrong. All 48 failures were shared
with Equator. Source PNG bytes and on-disk importer metadata were already correct.

The build log and Library artifact contents locate a second defect: exactly those 48 assets
were reimported again after the initial synchronous pipeline returned and Unity announced
`[Project] Loading completed`. The late artifact restored TextureType NormalMap and usageMode 3,
overwriting the new Default/usageMode 0 result. The two serialized importer artifacts differ
only in those two fields; this is not a missing resource stream or a guessed lighting issue.
The normal-channel guard prevents this intermediate bundle from being deployed. A clean
full-map repeat and a startup-order correction are required below.

The clean Equator repeat was also rejected by the new mandatory channel guard: 48 of 212
required textures remained corrupted. The independent direction comparator agreed exactly.
The redundant old-sequence Bay5 repeat was intentionally stopped at 22:47; its missing-DONE
receipt fails closed. Neither run was staged or deployed.

`BuildBundles.Run` now schedules the import prefix after real `delayCall` cycles with at least
0.5 seconds free of compilation/import activity. A 120-second startup watchdog and an explicit
domain-reload failure avoid silently hanging batchmode. Callbacks detach before the prefix
starts once; the existing Scripts-scene update phase remains intact.

The startup regression uses the separate `equator_s1` package (25 textures, nine normals),
with those nine ignored import metadata files deliberately set to the legacy NormalMap type
while Unity is stopped. Seven of the nine belong to the 48 failing full-map textures. Source PNG
bytes and all other metadata fields are retained. The fixture receipt records the original
metadata and source hashes. Its output is `work/build/normal-startup-s1`, never a staging input.

### Minimal import-order fix (supersedes the startup hypothesis)

The delayed-start S1 experiment still failed all nine normal-channel checks. Its log proves
project startup had completed before imports began, while the unwanted third imports still
occurred after the prefix yielded. Startup ordering alone was therefore ruled out.

The successful next A/B explicitly calls `WriteImportSettingsIfDirty` followed by
`ImportAsset(ForceUpdate | ForceSynchronousImport)` after configuring each preserved normal.
This replaces the default-flags `SaveAndReimport` at that one boundary. The independent
comparator passed all nine textures (mean direction error 0.003–0.298 degrees, maximum P99
1.801 degrees); the late third imports disappeared. The experimental startup deferral was
then removed, restoring the original `BuildBundles` orchestration for a final minimal test.

The intermediate S1 wrapper also exposed an unrelated fixture issue: its historical package
contains three single-face, square cube images with no dimension metadata. Shared import
metadata left by current full-map builds still described these as Cube, so the old fixture
lost three Texture2D references. For the final isolated test only, these ignored importer
shapes are reset to the fixture's original Texture2D semantics, alongside resetting the nine
normals to the legacy NormalMap type. No package files, validator tolerance or current full-map
import logic was weakened. Full maps explicitly restore their real Cube shapes on their next build.

The minimal test without startup deferral still failed normal checks. This demonstrates an
interaction: startup deferral is insufficient alone, but remains necessary together with the
explicit synchronous import. The earlier hypothesis was narrowed, not fully discarded. The
reviewed startup code was restored unchanged for the combined final S1 regression. Neither
failing intermediate is a staging candidate.

At 22:59 the combined final S1 wrapper passed all mandatory steps, including normal channels,
all 25 Texture2D references, script identities, NavMesh, preset slice checks and all 14 descriptor
mirrors. Final production source hashes frozen for the full builds:

- `BuildBundles.cs`: `9D2576056DF6C944A3109788754096F8F118BAD78C950BD7F2FEDB6C9A71FA40`
- `TextureImportDriver.cs`: `234C472310332D8D25783CF7D269655B43BB718DC11F9A63B1CB0EC0B081AA35`

The reduced candidate's independent exact result was five failed and four passed normals,
not nine failed. Both controls are retained. No full-map build is accepted based on the S1
result alone; complete package and independent vector gates still apply to each final map.

Independent final S1 proof also passed 9/9, no missing/duplicate textures, stable bundle
SHA-256 `5bc261c33a7db15b17b82365c515d4b43fd69217e975150db7b25a027e08326b`.
The mean direction errors are 0.003–0.298 degrees and highest P99 is 1.801 degrees (BC7).
Receipt: `normal-combined-s1-independent.json`. This validates the combined final pipeline
against intentionally legacy importer state, separately from later full-map/native acceptance.

The .NET test build had re-staged the server DLL with a newer informational Git revision.
Independent decompilation of both binaries showed exactly that assembly-attribute difference
(`2ba4d71` versus `7147e8e`), with identical code. The installed accepted server hash was checked
against the immutable baseline, then that unchanged binary was copied back to staging only.
No server implementation/deployment change or server restart is part of this task.

### Full-scene regression and native texture assets

The combined PNG-import safeguards also failed the full Equator build: 162 of 212 normals
passed and 50 failed the independent channel/vector checks. Two additional failures appeared
beyond the original 48. The rejected bundle hash is
`3c6074fe7c4175353851fd1cc855c75cac15494215a5d835d8ae5685bf2e3555`;
`normal-equator-final-independent.json` is a failure receipt despite its historical filename.
The concurrent Bay5 attempt was deliberately stopped at 23:20 before staging.

Installed UnityEditor decompilation identifies the automatic caller:
`ModelImporterPostProcessor.OnPostprocessAllAssets` checks imported materials through
`BumpMapSettings.PerformBumpMapCheck`, then queues `PerformUnmarkedBumpMapTexturesFixing`
on a later editor update. This explains why correct initial PNG imports were changed again
after the scene/material phase yielded. The source textures themselves were unchanged.

The replacement imports validated normals as native `Texture2D` `.asset` files, which have
no PNG TextureImporter to reclassify. It retains the existing source encoding policy, source
color space and DXT5/BC7/DXT1 compression, generates full mipmaps before compression, and
preserves asset GUIDs on repeat imports. Filtering remains Bilinear, anisotropy 1, Repeat and
mip bias 0: all 466 unique current normal metadata files had this baseline. The ineffective
startup workaround is removed; `BuildBundles` uses its original sequence again. Native
textures retain a compressed CPU-readable copy until serialization-safe discard is proven.
Small and full artifact gates remain required before this candidate can be deployed.

The first native S1 compile ran, but a too-strict RGBA32-only decode check rejected Unity's
valid PNG ARGB32 output. The corrected driver accepts either lossless eight-bit RGBA layout
without swizzling. Frozen SHA-256:
`C18D48647986EBD93DDD84202F3E080CCA4E89E25B2AE8E92B8A2E3F3F55ED00`.
Root reran 38 policy and eight channel-regression tests successfully.

Native S1 v2 passed all nine normal channel/vector checks, with mean direction error
0.003–0.298 degrees and maximum P99 1.801 degrees. Independent serialization inspection
confirmed all nine full mip chains, original filtering, Linear color space, readable compressed
data, and exact compressed byte sizes (14,418,192 bytes total). Its wrapper still caught the
known three-cubemap legacy-fixture mismatch because the intervening full-map build had restored
those shared import shapes. Root reset only those three ignored fixture shapes to Texture2D.
Native S1 v3 then passed the entire build/patch/validation wrapper, with all nine `.asset.meta`
files byte-identical across the repeated import, preserving GUIDs. These remain fixture outputs,
never full-map staging inputs. Current Equator and Bay5 native-feature builds follow separately.

Independent native S1 v3 verification passed 9/9 with stable bundle hash
`f0d0487493ce40a1a7bddbfff7d2be524172b9360a8153d4cdb6d4e40591ef3f`.
Every normal's serialized pixel bytes, dimensions, format, mip count, filtering and readability
match v2 exactly; all nine NativeFormatImporter GUIDs match the root's before snapshot.
Receipts: `normal-native-s1-v3-independent.json` and
`normal-native-s1-v3-repeatability.json`.

### Full native-asset package checks

Equator's complete package passed the mandatory bundle round-trip checks and the independent
212/212 source-versus-bundle comparison: zero missing/duplicate normals, all previous 50 failures
corrected. Stable scene-bundle SHA-256:
`dc1ecf0f30f255d5b34627976953cd12213a12316180b3214922b9c91ee8a7b0`.
Highest mean direction error is 0.297633 degrees, highest P99 1.823919 degrees, and highest
per-channel MAE 0.4858 byte. Every complete mip chain, source format/color space and original
filtering also passed independent serialized inspection. Actual compressed bytes total
186,917,480 (178.2584 MiB), matching the per-texture source calculation exactly. These CPU-readable
copies are the deliberate memory tradeoff described above, not a measured total game-RAM increase.
Receipts: `normal-equator-native-independent.json`, `normal-equator-native-serialized.json`.

On 2026-09-23, Bay5 also passed all mandatory round-trip checks and the independent 307/307
comparison. Scene bundle: 736,355,524 bytes, stable SHA-256
`9ddc73f8e2b335421c0e31f10dd77e3b3f9575c1fc74bc63cc537065c92de432`.
Highest mean direction error is 0.297633 degrees, highest P99 2.698201 degrees, highest
channel MAE 0.4157 byte. The reported cardboard and pallet normals have mean errors
0.101 and 0.089 degrees; the three ground layers 0.020, 0.011 and 0.000086 degrees.
All 307 complete mip chains, formats/colorspace, filtering and actual compressed data sizes pass;
320,135,728 bytes (305.3052 MiB) match the source calculation. Both maps together pass 519/519.
Receipts: `normal-bay5-native-independent.json`, `normal-bay5-native-serialized.json`.
Original negative controls and rejected full-map results remain preserved. All temporary
generated shader changes were restored after Unity exited; the final runtime reflective repair
is unchanged. Source review of final hands/config/import integration also found no defect.

### Final package preparation

The final client Release staging build passed with zero warnings/errors. Distribution validation
initially detected the test build's newer server informational revision in `bin/`, while staging
correctly retained the accepted server DLL. The server's compiled C#/project/shared inputs are
unchanged since `2ba4d71` (a later external Bay5 database spawn adjustment is separate). Root
rebuilt those same inputs with their accepted `SourceRevisionId`, reproducing the installed
server DLL byte-for-byte: `EBFAD3424224693656FCFCD55431B859E0F9506BE933B32799F4981A0D97103A`.
Thus build output, staging and the installed accepted binary agree without weakening the
distribution gate, changing server behavior or requiring a server restart. Receipt:
`server-reproduce-accepted.txt`.

### Final deployment and native acceptance — 2026-09-23 CEST

All final checks passed: client Release zero warnings/errors; 348 server/shared tests;
217 Python tests (190 passed, 27 expected skips); 38 normal-policy tests; eight normal-channel
regressions; real ConfigurationManager harness; 38 ArenaMapDebug tests. Distribution validation
passed for 14 maps / 42 bundles / 14 shader sidecars. The final 72-file manifest comparison
found exactly three changed files: client DLL, Equator scenes bundle and Bay5 scenes bundle.
NavMeshes, shader sidecars, presets, server DLL/database and the other twelve maps remain
byte-identical. The sanctioned deploy completed at 00:15:04; all installed hashes match staging.
Client SHA-256: `ABBE3F9FDEB5BC9EB09982F976E17ECF834CF1089E4800A23EE136793472F2EF`.
Receipts: `distribution-final.txt`, `manifest-final-stage.txt`, `manifest-final-installed.txt`,
`final-staged.json`, `final-installed.json`, `deploy-final.txt`.

Fresh EFT instance `453175d3`, started 00:15:55, confirmed all 23 maschine plugins and the
Redeploy library identical across VM disk, GPU-host disk and loaded process, both before and
after the final native sequence. The server terminal initially covered the game and was
minimized through its normal window control. Bosses were disabled and bot amount None selected
through the normal game settings for the visual comparison. No profile files were edited.

| Map | Debug request | Countdown 0 | Protected disconnect | Direct menu return |
|---|---|---|---|---|
| Bay5 | 00:21:52.474 | 00:22:59.898 | 00:28:05.872 | 00:28:10.396 |
| Equator | 00:29:02.451 | 00:29:40.022 | 00:30:08.245 | 00:30:12.635 |
| Bay5 repeat | 00:31:00.842 | 00:31:20.880 | 00:31:39.739 | 00:31:45.251 |

The first visual raid lasted about five minutes; the follow-ups were under 30 seconds of
play. Both buttons were independently hit-tested in the main menu. Every exit logged Runner
and suppression of the disconnect death/HP penalty. At the final in-raid check the character
had 440/440 HP, energy 86 and hydration 89, carrying the ER BAYONET. The repeat Bay5 run had
six bots despite the first comparison being botless; None is not claimed as a persistent
cross-map setting. The game was closed normally after the last menu return, freeing the host.

Native views, saved under `work/verify/bay5-shadows-menu/`:

- `smFinalBoxes.png`, at approximately (106, -3.7, 62), yaw 225 / pitch -5: cardboard is
  consistently warm brown with readable folds/texture. The broad blue-white front-face veil
  visible in `smUserAngleBase.png` is gone. Knife steel is darker with narrow bright metal edges.
- `smFinalShadowGround2.png`, around (91, -3.9, 30), yaw 225 / pitch 65: the complete character
  silhouette casts a continuous shadow across the pavement. The earlier broken dark/light
  patch pattern no longer dominates. `smFinalShadowGround1.png` confirms the same shadow at
  neutral ground brightness; the user's brighter value 2 was restored.
- `smFinalYardGround2.png`, around (56, -3.9, 16.5), yaw 243 / pitch 45: the wall casts a
  coherent diagonal shadow with ground texture visible on both sides. The formerly blotchy
  ground is coherently lit. A neutral-brightness reference is `smFinalYardGround1.png`.

An independent visual review compared these final views against the four user screenshots
and the earlier same-angle box baseline. It found no blocker and no reason to further lower
reflection globally. These observations cover the sampled places and views, not every map
surface or lighting angle; final user comparison remains welcome.

Lifecycle logs confirm Bay5 captures one Hands highlight object and changes exactly two
fields at scale 0.35; camera-ready callbacks make no additional changes. On each unload,
exactly those two fields are restored. Equator uses scale 1.00, changes/restores zero fields.
Bay5 ground tint remains 2 on the repeat visit and restores all nine owned tints on both exits.
Reflection inputs retain the accepted local cap (~0.296); the final runtime reflective repair
has no code changes. No ArenaMapPort/ArenaMapDebug warnings or errors occurred. The broader
Unity log contains a microphone initialization error at 00:16:31 and one inactive MenuScreen
coroutine error at 00:27:55 when opening the pause menu; the native disconnect and both later
menu transitions nevertheless completed correctly. These are recorded rather than claiming
an entirely clean global game log.

Evidence receipts: `smFinalDebugLog.json`, `smFinalLifecycleLog.json`, `smFinalErrorLog.json`,
`smFinalOtherErrors.json`, `smFinalPlayerRepeat.json`, `smFinalLoadedAfterRaids.json`.
Earlier MIA and consumed-item consequences documented above remain; the final sequence added
no disconnect health penalty. No consumables or profile-file edits were used in these final runs.
