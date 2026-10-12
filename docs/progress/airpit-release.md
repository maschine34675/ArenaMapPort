# AirPit release - Phase A running log

Running log for [PLAN-AIRPIT.md](../PLAN-AIRPIT.md) Phase A (generic extraction plumbing, items
1-5). Append-only: each item adds its own section with date, commands and counts. Phase B-D
host evidence goes to the `airpit-*.md` files named in the plan.

## Baselines before item 1 (2026-10-11, about 19:10-19:20 local)

Taken on `feature/airpit` at HEAD `2723d88` ("Plan AirPit as the third released map", parent
`d7773e5`), working tree clean (the plan, HANDOFF and INDEX edits had just been committed).
Logs are in the agent scratchpad, not in the repository.

| Check | Result |
|---|---|
| `git status --short` | clean |
| `python -m unittest discover -s tools/tests -p "test_*.py"` | **690 tests, OK, 44 skipped**, 39.5 s (docs said 665/669; plan item 3 already measured 690) |
| `dotnet test ArenaMapPort.Server.Tests/ArenaMapPort.Server.Tests.csproj -c Release` | **627 passed, 0 failed, 0 skipped**, 1 s (docs baseline 606 in `first-release-prep.md`; 21 tests were added since) |
| `dist/` file inventory | 48 files (6 bundles, 2 shader sidecars, 2 `base.json`, 2 DLLs, acoustics, AI, flares, water, loadscreens, banners, DynamicMaps packs, map descriptions) |

### Equator / Bay5 artifact baseline

`docs/dist-manifest.md` carries layout and byte totals but no per-file hashes for the current
two-map distribution; the per-file SHA256 of the shipped 0.1.0 package is the manifest of the
refreshed ZIP, `work/release-muzzle-update/maschine-ArenaMapPort-0.1.0.zip.manifest.json`
(ZIP SHA256 `85007889...3494`, 1,231,754,560 B, 51 entries, source commit `1893272`). It is the
baseline used here. Method: SHA256 of every file under `dist/`, compared by relative path with
the manifest's `files` map. The three manifest-only entries are the packaged `README.txt` and
the two `THIRD-PARTY-NOTICES.txt`, which are not staged in `dist/`.

- **Before the baseline test runs: 48 of 48 `dist/` files are byte-identical to the 0.1.0
  manifest** (sizes and SHA256), including the client DLL
  `b5a69190...1eb1` and the server DLL `7fff7da7...4012`.
- The older `work/first-release-prep/release-inventory.json` (pre-muzzle-fix, pre-rename client
  path) agrees for every bundle, shader sidecar, acoustics, flares, water, loadscreen, AI and
  banner file; it differs only for the client DLL (muzzle fix) and the Bay5 `base.json`
  (changed later in the 0.1.0 preparation), so it is not the baseline.
- **Side effect of the specified baseline command:** `dotnet test -c Release` rebuilds the
  server project, whose staging step redeploys the server DLL into `dist/`. After it,
  47 of 48 files still match; only `maschine-ArenaMapPort.Server.dll` differs
  (`7fff7da7...4012` -> `f4811357...d3aa`, same 65,536 B). This is the known build-identity
  change (`first-release-prep.md`, "Post-commit build identity follow-up"), not a source
  change; all bundles, `base.json`, sidecars and sibling payloads were unchanged. **Restored
  afterwards (review follow-up below):** the shipped `7fff7da7...4012` DLL was put back into
  `dist/` from the 0.1.0 ZIP, so `dist/` is 48 of 48 identical to the manifest again. Any later
  server build or `dotnet test` stages the DLL again (see the follow-up for why), so repeat the
  restore or compare the two DLLs separately after such a run.

Key Equator/Bay5 hashes (identical before and after the baseline runs):

| File (under `dist/`) | Bytes | SHA256 |
|---|---:|---|
| `bundles/arena_equator_scenes.bundle` | 431,645,444 | `14eed6f8ae675c54f1306a34cbe1fdd3f1ba2a53a92a2285ed65e512fd13f796` |
| `bundles/arena_equator_navmesh.bundle` | 72,968 | `0c32649698dcf85633818c5a9077f579816a25c3e1db0ed17318d44848f17e1a` |
| `bundles/arena_equator_preset.bundle` | 6,891 | `67c555323c07818c6b538acd7ce71682a846488f2a3bce0e506e1fe0c453fefa` |
| `bundles/arena_bay5_scenes.bundle` | 785,870,418 | `3529722dd19e9f896a210cf0adc13fc498cf8dbf1260d235b87d657d52dccc3d` |
| `bundles/arena_bay5_navmesh.bundle` | 189,450 | `9cf2f78b82d13de7e03eaefd63567457c4ea6d9b7a648caf322e86b9c85d6620` |
| `bundles/arena_bay5_preset.bundle` | 7,107 | `37c4e5d79b3ddb44be5a4a83245ee698840d42380f94ff833d8c6bb53130a2ca` |
| `material-shaders.json` (Equator) | 51,757 | `01d1f25987923c90da0206eebd501317693fc002ee177fa2340348b417d3b730` |
| `material-shaders.arena_bay5.json` | 68,876 | `9c1b48fb2083c744daaa2ed82767545d2c11dfe8958c50adea06e167669c2275` |
| `db/arena_equator/base.json` | 25,567 | `1370010854a10c37917189b46b0a4347226676a4c9ba82509e78aedf6e018ef0` |
| `db/arena_bay5/base.json` | 25,671 | `f10dc9f5248d3d2c7342ea9947d6bc8b94c05a1f189534034f5771e93e4d35b3` |
| `maschine-ArenaMapPort.Client.dll` | 709,632 | `b5a6919020e243fc9f0a8fa2ecf031d75d5c594f3dc31b4d2d94bd69afbb1eb1` |

(Paths are relative to `BepInEx/plugins/maschine-ArenaMapPort/` for the client files and to
`SPT_Runtime/user/mods/ArenaMapPort/` for `db/`.)

### Accepted-package validator state (pre-existing, not caused by Phase A)

`python tools/validate/validate_extract.py --package work/extract/equator-native-v5
--skip-reproducibility` and the same for `bay5-native-v5` currently **fail** against the live
Arena install, with and without the item 1 change:

- both packages: `Native features: independent named preset provenance.source_presets.offset:
  5708 != 5716`;
- Bay5 additionally: `scenes/Arena_Bay5_Main.json: disabled MeshRenderer count (302) == Arena's
  own authored count (305)`.

These checks re-read the live Arena source, which is 0.6.0 since 2026-10-03, while the accepted
v5 packages hold the 0.5.4 extraction (measured, not only inferred: see "Item 1 review
follow-up" below; `arena-update-20261003.md` says old packages keep their original provenance and
the fresh 0.6.0 audit extractions pass). Evidence that item 1 is not the cause: the validator was
run from a pristine `git archive HEAD` copy of `tools/` and `analysis/scripts` and from the
working tree; after normalising path spelling and line order the two outputs are identical for
both packages. Consequence for later items: the guarantee "the accepted packages must still
validate" is checked as "validator output unchanged by the change" with the parity gate described
in the follow-up (no 0.5.4 source copy exists on this machine; the validator is not weakened).

## Item 1 - plan scene roles (done 2026-10-11)

**What changed.**

- `tools/extract/plans/airpit.json`: a `role` key on the four `excluded_scenes` records, inserted
  after `index`: 36 `scripts` (Arena_Airpit_Scripts), 39 `sound` (Arena_Airpit_Sound), 33 `ai`
  (Arena_Airpit_TeamFight_AI), 30 `culling` (Arena_Airpit_TeamFight_Culling). Nothing else in the
  plan changed. Plan digest `0a6b5a969ddb6ce5...` -> `398f5d706c84f032...` (AirPit only). The
  spike package `work/extract/airpit` records the old digest in `manifest.json`, so
  `validate_manifest_plan` now reports "source plan digest drift" for it; it is superseded by
  the item 5 re-extraction and no longer referenced, as the plan states. Nothing in the
  repository pins the old digest outside the plan text.
- `tools/extract/source_plan.py`: `SCENE_ROLES = ("scripts", "sound", "ai", "culling")`; new
  `scene_roles(plan)` (validation plus `{role: record}`) called from `validate_plan`; new
  `scene_for_role(plan, role)` returning a copy of the `{name, asset_path, index}` record and
  raising `PlanError` for an unknown role or a plan without that role (no silent fallback to a
  per-map index). Rules, all fail-closed: role must be a string from the vocabulary; each role
  at most once; a scene index carries at most one role (added, not in the plan text: two roles
  on one scene would make `scene_for_role` ambiguous); `ai` must also be in `navmesh_scenes` as
  the identical record; a role is never valid on an imported `scenes` index, and a `role` key on
  any `scenes` / `anchor_scenes` / `navmesh_scenes` record is rejected. Plans without roles
  (the other eleven) validate unchanged and return `{}`. `unique_records`,
  `verify_scene_mapping`, `provenance` and the digest function are untouched, so Scripts, Sound
  and Culling are still never loaded by a census or extraction, and excluded role records keep
  their exact path-drift check.
- `tools/tests/test_map_pipeline.py`: new `SceneRoleTests` (14 cases) and a `roles_plan()`
  fixture. Positive: all four roles resolve to the exact record, returned record is a copy,
  role-less plan stays valid and `scene_for_role` raises, partial role sets, every repository
  plan validates and each role names its excluded record, AirPit declares exactly
  36/39/33/30 with the expected names, roles leave `unique_records` at the five imported scenes
  plus the AI scene, and an installed-Arena test that each role scene's path occurs exactly once
  in BuildSettings at its index. Negative: unknown / mis-cased / empty / null / non-string role,
  duplicate role, two roles on one scene, role on an imported scene, `role` key in each of the
  three loaded groups, `ai` missing from `navmesh_scenes` or listed with a different path, and
  path drift of a role scene.

**Verification (after the change).**

| Check | Result |
|---|---|
| `python -m unittest discover -s tools/tests -p "test_*.py"` | **704 tests, OK, 44 skipped** (690 + 14 new), 38.4 s |
| `python tools/extract/extract_map.py --plan tools/extract/plans/airpit.json --census-only` | exit 0, 8 s, 130 source files loaded; per-scene GameObject counts 20,252 / 3,833 / 15,357 / 638 / 647 |
| census output vs pristine `git archive HEAD` copy | identical (all five scene lines), so plain `extract_map.py` loading is unchanged |
| Other eleven plans vs HEAD | byte-identical files and identical digests (airpit2, autoservice, bowl, custom, factory, iceberg, prison, railwaystation, shoreline, woods, yard) |
| `test_all_repository_plans_match_installed_arena` | passes with the roles present (all twelve plans, including the excluded role scenes, still map to the installed 0.6.0 BuildSettings) |
| installed BuildSettings | each of 36, 39, 33, 30 occurs exactly once at its index (no Bay5-style duplicate-index trap), asserted by the new gated test |
| `dotnet test` | not rerun (no C# touched) |
| accepted package validators | unchanged, see the pre-existing-failure section above |

**Observation for item 5 (the 8-object discrepancy).** The census reports `Arena_Airpit_DesignStuff
(level40)` with 15,357 GameObjects but 15,349 `Transform` objects, plus 8 `RectTransform`, 8
`Canvas` and 8 `CanvasRenderer`. 15,357 - 15,349 = 8 = the RectTransform count, and
`extract_equator.py:228` builds its transform map only from type name `Transform`
(`elif tn == "Transform"`), so the 8 UI canvas GameObjects are skipped, as for Equator and Bay5.
This matches the plan's "most likely an extractor skip common to old and new packages"; it is an
inference from counts and code, to be confirmed with the extracted level40 object count in
item 5.

## Item 1 review follow-up (2026-10-11, about 19:40-19:50 local)

Two minor review findings, both confirmed. Neither needed a change to the item 1 code.

### 1. "The accepted packages must still validate" cannot pass against 0.6.0

Re-ran `validate_extract.py --package work/extract/<pkg> --skip-reproducibility` for both
accepted packages: exit 1 with exactly the failures recorded above (Equator 1 FAIL, Bay5 2 FAIL).
The cause is now measured instead of inferred:

- **Preset offset (both packages).** The validator re-reads `source.arena_data_root` (the live
  Arena install, `resources.assets` last written 2026-10-03). The 3,128-byte `EFTHardSettings`
  preset table is identical in the manifest and the live file (same SHA256 `d8ca2c1b693d5ff9...`,
  same names and values); only its offset inside the component moved, 5708 recorded vs 5716 live,
  and the containing component hash differs (`fc997bc5...` vs `75e83b4c...`). So the ballistic
  data did not change; 0.6.0 shifted the serialized layout by 8 bytes. The check compares the
  offset literally, hence the FAIL.
- **Bay5 disabled MeshRenderers.** The accepted package has 302 disabled MeshRenderers in
  `Arena_Bay5_Main.json` (26,037 objects, 15,232 MeshRenderers); the 0.6.0 audit extraction
  `work/arena-update-audit/bay5` has 305 (26,040 objects, 15,235 MeshRenderers). The three extra
  renderers are the "three additional decal/render objects" that `arena-update-20261003.md`
  records for the new Bay5 source. The validator's expected count comes from the live level file
  (305), the package is the 0.5.4 extraction (302).
- No 0.5.4 source copy was found (`arena-src/` holds one unrelated asset file and no
  `resources.assets` exists under `work/` to depth 3), so the validator cannot be pointed at the
  old source, and it is deliberately not weakened or its provenance edited
  (`arena-update-20261003.md`: old packages are not made to look unchanged).

**Parity gate for items 2-5 (replaces "must still validate").** The Equator/Bay5 guarantee is
"the validator's whole output for both accepted packages is unchanged by the change, and the FAIL
set is exactly the two documented 0.6.0 failures". Tooling, kept under ignored `work/`:

```
python work/airpit-parity/parity_gate.py check work/airpit-parity/baseline_head_2723d88.json
```

It runs `validate_extract.py --skip-reproducibility` for `equator-native-v5` and `bay5-native-v5`,
normalises the package path spelling and line order, and compares the line set, exit code and
SHA256 with a baseline recorded from a pristine `git archive 2723d88` copy of `tools/` and
`analysis/scripts` (`record <baseline> --repo <copy>` regenerates it). Baseline: Equator exit 1,
48 non-empty lines, 1 FAIL, normalised SHA256 `677fc289136a08ea...`; Bay5 exit 1, 72 lines, 2
FAIL, `7e7a7c51eb9c8b08...`. Result for the item 1 working tree: both packages UNCHANGED, FAIL
set as documented, exit 0. Sensitivity check: the same gate run against a copy of HEAD whose
`validate_native_features.py` message text was altered reports both packages CHANGED with the
changed lines, exit 1, so the gate does fail when output moves. Items that rewrite
`validate_native_features.py` (plan item 3) must keep this output byte-for-byte (the golden test);
if a deliberate change ever moves it, record a new baseline and say why in this log.

### 2. `dist/` server DLL replaced by the baseline `dotnet test`

Confirmed: after the baseline run, `dist/.../maschine-ArenaMapPort.Server.dll` was
`f4811357...d3aa` (mtime 2026-10-11 19:13) while the 0.1.0 manifest lists `7fff7da7...4012`.
Cause: `ArenaMapPort.Server.csproj` runs `DeployToDist` after every Build, and
`ArenaMapPort.Server.Tests` references the server project, so `dotnet test` rebuilds and stages the
DLL (the copy uses `SkipUnchangedFiles`, which compares timestamp and size, so a restored file
with a newer timestamp is overwritten again by the next build).

Fixed: the single DLL entry was read from `work/release-muzzle-update/maschine-ArenaMapPort-0.1.0.zip`
(read-only), checked against the manifest (65,536 B, SHA256 `7fff7da7ec3485477b3213d22fb7358943a792547ec3cc26cdc79d91b3984012`)
and copied over the `dist/` file; script `work/airpit-parity/restore_server_dll.py`, which refuses on
any mismatch. Afterwards `work/airpit-parity/dist_vs_manifest.py` reports 48 of 48 `dist/` files
identical to the manifest by relative path and size (the three manifest-only entries remain the
packaged `README.txt` and two `THIRD-PARTY-NOTICES.txt`). Nothing outside `dist/` was touched. Rule
for later items: do not run `dotnet build`/`dotnet test` for the server without a reason (item 1
needs no C#); after any such run restore with the script above, or compare the client and server
DLLs separately from the other 46 files.

### Checks rerun after the fixes

| Check | Result |
|---|---|
| `python -m unittest discover -s tools/tests -p "test_*.py"` | **704 tests, OK, 44 skipped**, 36.2 s (unchanged from the item 1 result) |
| `extract_map.py --plan tools/extract/plans/airpit.json --census-only` | exit 0; log byte-identical to the pre-change census (20,252 / 3,833 / 15,357 / 638 / 647 GameObjects) |
| `git diff --stat HEAD -- tools/extract/plans/` | only `airpit.json` (4 insertions); the other eleven plans untouched |
| `extract_equator.py`, `extract_bay5.py`, `validate_extract.py`, `validate_native_features.py` | no diff against HEAD |
| parity gate (both accepted packages) | UNCHANGED vs the HEAD baseline, FAIL set as documented |
| `dist/` vs 0.1.0 manifest | 48 of 48 identical |

## Item 2 - native-feature orchestration in `extract_map.py` (done 2026-10-11)

**What changed.**

- New `tools/extract/native_features.py`: the sequence `extract_equator.py:996-1099` and
  `extract_bay5.py:275-398` run inline, for any plan-driven map. `NativeFeatureExtraction.for_plan(plan,
  source, target)` resolves the plan's `scripts` scene role (`scene_for_role`), checks that `--target`
  holds `resources.assets` and `globalgamemanagers.assets`, and reads the ballistic preset contract
  (`extract_ballistics.load_contract`) exactly once. `augment_scene(scene, file, ctx, label)` runs the five
  augmenters per scene in the order ballistics, NavMesh modifiers, static decals, SmallPhysicsObject, light
  culling and accumulates totals. `manifest_blocks(env, output)` runs the post-write steps and returns the
  top-level manifest keys: `native_ballistics` (the contract), `native_features` (`schema_version` 1,
  `required`, `navmesh_areas`, `raincover`, `light_culling`, `static_decals`) and, only when the map has a
  registered exporter in `AUDIO_EXPORTERS`, `native_audio`. `source_fields()` adds `source.target_data_root`
  (`source.map_key` was already written by `provenance()`).
- `tools/extract/extract_map.py`: `--native-features` and `--target` (parser split into `build_parser` /
  `parse_args` for tests). `--native-features` requires `--target`, `--target` is only valid with
  `--native-features`, and `--native-features` cannot combine with `--census-only` (all exit 2). `extract()`
  gained a trailing optional `native_target`; the native run is built before the environment load, so a plan
  without a `scripts` role or a bad `--target` fails in under 2 s instead of after a 5 minute extraction
  (checked: `bowl.json` -> "declares no scene with role 'scripts'"; `D:/nonexistent` -> "resources.assets
  missing"; neither creates an output directory). Augmentation happens before each scene JSON is written, the
  post-write steps after meshes/materials/textures/terrain and before the manifest (the completeness marker).
- `tools/extract/extract_raincover.py`: `export(source, package, map_key, scripts_scene_index=None)`. A given
  index replaces the `SCRIPTS_SCENE_INDEX` lookup (validated as a non-negative int); omitted, the Equator/Bay5
  table applies unchanged. This is the minimal wiring item 4 may refine (it can drop the table and keep this
  parameter).
- `tools/validate/validate_extract.py` `run_extractor`: for `extract_map.py` with `native_features`, passes
  `--native-features --target <source.target_data_root>`; a generic native package without a recorded target
  dies instead of rerunning without the flags. Plain generic and legacy Equator/Bay5 commands are unchanged.
- New `tools/tests/test_native_features_orchestration.py` (25 cases): augmenter order and totals, contract read
  once, bad target, manifest blocks with and without an audio exporter, `static_decals` only when decals exist,
  zero ballistics is a hard failure, rain cover receives the plan's index (AirPit plan -> 36), CLI rules,
  fail-early order in `extract()` and no native path on a plain run, the generic reproducibility command, and a
  golden test that feeds the accepted `equator-native-v5` and `bay5-native-v5` values through
  `manifest_blocks` and requires their exact `native_features` / `native_audio` / `native_ballistics` blocks
  (skipped when those ignored packages are absent; both ran here).

**Design decisions.**

- Augmenter order: decals sit between NavMesh and physics (Equator order; Bay5 runs them first). The two legacy
  orders differ only for an owner with a decal plus another native component. Measured on the accepted
  packages: no such owner exists (only light-culling pairs, ballistics+NavMesh pairs); on AirPit the only
  multi-component owners are 128 `VolumetricLight` + `CullingLightObject` pairs. Ballistics-before-NavMesh and
  the light-culling pair order are the same under both legacy orders.
- Audio: `AUDIO_EXPORTERS` is empty. `extract_bay5_audio.export` raises for any key but Equator/Bay5, and
  AirPit's exporter is item 13. A map without one gets `required == ["ballistics"]` and no `native_audio` key
  (the Unity importer already returns early on a missing `native_audio`). **Hand-off to item 3:** the
  validator's `feature_enabled` still demands all three of `native_features`, `native_ballistics`,
  `native_audio`, and the `required` list equal to `["ballistics", <contract feature>]`.
- `static_decals` is omitted when a map has none (the validator requires a non-empty census whenever the key is
  present and none when absent); Equator/Bay5 always have decals.
- A native package with zero `BallisticCollider` fails extraction (`required` lists ballistics).

**Dry run (AirPit, Arena 0.6.0 source, `D:/SPT41/EscapeFromTarkov_Data` target, `--fixed-timestamp
2026-01-01T00:00:00Z`).** Output in the agent scratchpad (`airpit-a/item2/airpit-native`), not under `work/`.

```
python tools/extract/extract_map.py --plan tools/extract/plans/airpit.json --native-features \
  --target D:/SPT41/EscapeFromTarkov_Data --output <scratch>/airpit-native --fixed-timestamp 2026-01-01T00:00:00Z
```

Exit 0 in 258 s (plain extraction: 244-248 s). `DONE arena_airpit: 40719 objects, 2487 meshes, 435 materials,
805 textures, 240 warnings`, errors 0.

| Native component | Total | Per scene (ShootOut / Light / DesignStuff / TeamFight / TeamFight_DesignStuff) |
|---|---:|---|
| BallisticCollider | 3,548 | 2,856 / 512 / 28 / 94 / 58 |
| StaticDeferredDecal | 481 | 423 / 0 / 4 / 0 / 54 |
| NavMeshModifier | 20 | 0 / 0 / 0 / 0 / 20 |
| NavMeshModifierVolume | 12 | 12 / 0 / 0 / 0 / 0 |
| CullingLightObject | 533 | 2 / 515 / 0 / 4 / 12 |
| VolumetricLight | 130 | 1 / 125 / 0 / 2 / 2 |
| LightFlicker | 0 | 0 / 0 / 0 / 0 / 0 |
| SmallPhysicsObject | 0 | 0 / 0 / 0 / 0 / 0 |

Manifest: `source.target_data_root` `D:/SPT41/EscapeFromTarkov_Data`, `native_features.required ["ballistics"]`,
`navmesh_areas` 5 entries (Walkable, Not Walkable, Jump, Sitdown, Danger), `raincover` `indoor_trigger_count 2`,
`dry_plane_count 0`, `weather_obstacle false`, `bounds_transform_mismatches 1` (the known `Bus_IndoorTrigger`:
baked centre (-13.05, 1.58, -66.58) is used, live transform is 155.2 m away, 125.76 m on the largest single axis), source scene `Scripts (level36)`
(from the plan role, not a table), `light_culling` 533/130/0, `static_decals` 481; no `native_audio` key.

**Native package vs plain package of the same plan** (`native_vs_plain.py`, scratchpad): every scene has the same
object ids and order; the only differences are appended native components (3,548 + 481 + 130 + 533 + 12 + 20,
nothing else); no original component changed; the only files not in the plain package are 55 decal materials,
43 decal textures and `raincover/raincover.json` (3,636 -> 3,735 files = 380 -> 435 materials, 762 -> 805
textures); every shared mesh/material/texture/navmesh file is byte-identical.

**Verification.**

| Check | Result |
|---|---|
| `python -m unittest discover -s tools/tests -p "test_*.py"` | **729 tests, OK, 44 skipped** (704 + 25 new), 44 s |
| Plain `extract_map.py` byte identity | plain extraction of `airpit.json` from a pre-change snapshot of `tools/` + `analysis/scripts` vs the changed tree, both `--fixed-timestamp 2026-01-01T00:00:00Z`: `diff -r` empty over all 3,636 files (manifest included) |
| Parity gate for the accepted packages (`work/airpit-parity/parity_gate.py check ...baseline_head_2723d88.json`) | `equator-native-v5` UNCHANGED (48 lines, 1 FAIL, sha256 `677fc289136a08ea`), `bay5-native-v5` UNCHANGED (72 lines, 2 FAIL, `7e7a7c51eb9c8b08`); the FAILs are the two documented 0.6.0 live-source drifts, not caused by this item |
| `git diff` for `extract_equator.py`, `extract_bay5.py`, `validate_native_features.py`, the eleven other plans | none |
| `validate_extract.py --package <airpit-native> --skip-reproducibility` | every structural, scene, mesh, texture and disabled-component check passes; exactly 1 FAIL: `native feature package validation: Native features: incomplete required feature contract` (no `native_audio`, no AirPit entry in `MAPS`; item 3) |
| `validate_extract.py --package <airpit-native> --extractor-timeout 900` (full, reproducibility) | the two reruns exit 0 (the rerun command now carries `--native-features --target D:/SPT41/EscapeFromTarkov_Data`), "two independent fresh extractor runs produce a byte-identical package": 0 diffs, "package-freshness check": 0 diffs; same single native-contract FAIL, exit 1 |
| Forecast for item 3 (`forecast_item3.py`, in-process, nothing written) | with a stub `native_audio`, `required ["ballistics","ambient_audio"]` and a `MAPS` entry, `feature_enabled` returns True on the real package, so the raincover / navmesh_areas / light_culling / static_decals shape rules already accept the generic block |

**Open for later items.** Item 3: audio-optional contract and the AirPit contract data (above); expected
decals 481 derive from the package. Item 4: `extract_raincover` can drop `SCRIPTS_SCENE_INDEX`. Item 5: the
real `work/extract/airpit-native-v5` run is this command with `--output work/extract/airpit-native-v5`
followed by `refresh_native_volumes.py`; the fixed timestamp used here is only for the determinism check
(item 5 may use a real timestamp). `validate_bundle_roundtrip` branches on
`extractor_script == extract_map.py` and calls `validate_native_features` for native manifests, so it needs the
same item 3 change before a Unity build can be verified.

### Item 2 review follow-up (2026-10-11)

1. **Native-contract FAIL in `validate_extract.py` (disputed as an item 2 defect, no code change).** The one FAIL on
   the AirPit dry-run package is `feature_enabled` in `validate_native_features.py` requiring `native_audio` and
   `required == ["ballistics", <contract feature>]`. PLAN-AIRPIT item 2 itself says "see item 3 for audio-optional",
   and item 3 lists the `audio: null` contract and the AirPit contract data. Making item 2 emit a stub
   `native_audio` or an `ambient_audio` requirement would ship a false manifest, and editing
   `validate_native_features.py` here would break the "validator untouched until item 3" boundary. So item 2's
   literal done-when ("passes `validate_extract.py` including reproducibility") is met for everything item 2
   owns (both reruns exit 0, byte-identical, freshness 0 diffs) and closes fully when item 3 lands; the lead
   should not mark item 2 verified end to end before that.
2. **Fixed: exporter `ValueError` escaped as a traceback (exit 1).** `extract_raincover.export` (ten `raise
   ValueError` sites) and `extract_bay5_audio.export` (dozens) fail closed with a bare `ValueError`, and
   `extract_map.main` maps only `PlanError`/`HardExtractError`/`OSError` to `[extract_map] FAILED` / exit 2.
   Confirmed by reading the raise sites and `main()`. `native_features._fail_closed(label, call, ...)` now wraps
   both post-write exporter calls and re-raises `ValueError` as `HardExtractError("native_features: <label>
   export failed: <original>")` with the cause chained; a `HardExtractError` from an exporter passes through
   unchanged. The fix lives in the generic orchestration only: `extract_raincover.py`, `extract_bay5_audio.py`,
   `extract_equator.py`, `extract_bay5.py` and `main()` are untouched, so Equator/Bay5 behaviour and plain
   `extract_map.py` runs cannot change (`_fail_closed` is reachable only from `manifest_blocks`). Four new tests
   (rain-cover and audio `ValueError` wrapped with cause, `HardExtractError` pass-through, and an `EX.main()`
   run that prints `[extract_map] FAILED: native_features: raincover export failed` and returns 2); the three
   that exercise the wrapping fail when `_fail_closed` is replaced by a plain call. Real-code check:
   `export(..., scripts_scene_index=-1)` through the wrapper gives `HardExtractError: native_features: raincover
   export failed: raincover: invalid Scripts scene index -1 for 'arena_airpit'`.
   Rechecks: `python -m unittest discover -s tools/tests -p "test_*.py"` 784 tests OK, 44 skipped (the working
   tree now also holds the item 7 suites; this file has 29 cases, the four new ones included);
   `parity_gate.py check` Equator and Bay5 UNCHANGED; `git diff` of `extract_equator.py`, `extract_bay5.py`,
   `validate_native_features.py` and the other eleven plans empty.

## Item 7 - Arena NavMesh oracle and TeamFight composition probe (2026-10-11, about 19:20-20:40 local)

Offline only: no Unity, no host, no write outside the repository and the ignored scratch area. Inputs: the
live Arena 0.6.0.47752 install (read-only) and the **0.5.4** package `work/extract/airpit` (the
`airpit-native-v5` package of item 5 does not exist yet; re-run the commands below on it as soon as it does,
see "Open points").

### New tools

| File | Purpose |
|---|---|
| `tools/validate/navmesh_oracle.py` | Parses Arena's Detour tiles (`NavMeshData.m_NavMeshTiles`) into polygons, rebuilds tile-to-tile links geometrically, labels connected components, locates points, border clearance, lattice sampling. Also wraps an `ExportMapTriangulation` receipt or a built `*_navmesh.bundle` as a NavMesh. |
| `tools/validate/port_colliders.py` | World-space collider triangles of a package (`scenes/*.json` + `meshes/*.mesh.bin`) with the `BakeNavMesh.cs` source rules: active chain, enabled, non-trigger, layers 4/13/29, `SPAWN_BOUND*` / `LevelBorder*` / `Door_Sliding_Respawn_*`, `opened_barriers` (Transform.Find semantics, frame rule, the barrier object itself kept). |
| `tools/validate/walk_model.py` | numpy voxel column model: conservative xz rasterisation (0.1 m cells, 0.1 m y bits), floor/solid bit sets, disc dilation, agent body test, blocker attribution. |
| `tools/validate/navmesh_diff.py` | The diff: samples Arena's mesh, runs the model with Arena's agent and the port agent, ranks blocking prop clusters, simulates exclusion sets, greedy cutter list, probe-point reachability, optional comparison with a built NavMesh (`--compare-bundle`, `--compare-triangulation`). |
| `tools/validate/navmesh_probe_points_airpit.json` | Draft probe points (plan Appendix A spawns/exits, both capture points) until item 6 writes `airpit.bots.json` / `airpit.exits.json`; `--live-core-points` adds the 19 AI core points and 38 patrol points read from the live AI scene (existing `extract_ai_anchors.extract_map`, nothing written). |
| `tools/validate/probe_mode_toggler.py` | Read-only probe of the mode toggler question (presets, class census of the mode scenes, decoded `NavMeshSurface`, metadata readability). |
| `tools/tests/test_navmesh_oracle.py`, `tools/tests/test_navmesh_diff.py` | 58 tests on synthetic tiles, synthetic packages, the presence hole test and a synthetic corridor end to end. |

### Arena's NavMesh as parsed (sharedassets33.assets path 2)

168 tiles (17.92 m = 256 x 0.07 m), 68 non-empty, **3,853 polygons** (2,217 triangles, 1,121 quads, 329
pentagons, 186 hexagons), 6,338 vertices, no off-mesh links. In-tile neighbour links are reciprocal (0
asymmetric); 199 portal pairs matched geometrically, 18 portal edges found no partner. Those 18 edges sit on
18 polygons (16 in the main component, one each in components 1 and 12): 15 touch the neighbouring tile's
edges only at a corner (zero overlap along the shared border, so they are ordinary walkable-area borders that
happen to lie on a tile boundary), 3 overlap an edge at another level (height difference 4.8, 4.8 and
9.9 m). They do not change the component structure: loosening the match to 0.1 m / 1.5 m height difference
adds no link, 0.25 m / 3.0 m adds 16 links without merging any component (still 34 components, main component
8,168.2 m2); only an unphysical height tolerance (0.5 m / 6 m, or 0.05 m / 10 m) merges one further 31.0 m2
component into the main one (all-pairs matcher, see "Item 7 review follow-up"). Build settings: radius 0.28, height 1.6,
climb 0.38, slope 48 deg, cell 0.07. **34 components, 9,462.6 m2 in total**:

| # | polygons | area xz | x | y | z |
|---|---:|---:|---|---|---|
| 0 (play space) | 2,890 | **8,168.2 m2** (8,244.4 m2 along the surface) | -56.14..32.41 | 0.02..6.46 | -77.70..84.77 |
| 1 | 145 | 326.2 | -104.2..-54.5 | 25.1..43.4 | 28.9..48.0 |
| 2 | 127 | 197.7 | 19.2..59.3 | 35.6..45.9 | 37.5..47.2 |
| 3-33 | 691 in total | 770.5 in total (largest 91.6) | all outside the play volume or on upper levels (lowest point y 2.19) | | |

The LastHero bake (`sharedassets140`, 100 tiles, 3,568 polygons, main 8,009.6 m2 plus a second 2,442.4 m2
component) is a different bake but covers 203,181 of the 204,204 TeamFight samples (99.5 %): both ignore
the same props.

### Model and calibration

Query points are a 0.2 m lattice over component 0 (**204,204 samples**, 8,168.2 m2). The model asks, for each
sample, whether a port collider triangle intrudes into the agent body (cylinder from y + climb + 0.15 to
y + height, radius = agent radius - 0.10 slack) and whether a walkable port surface supports it (+-0.6 m).
The port bake rules are applied exactly as `BakeNavMesh.cs` does.

- **Source count check:** with no exclusions the loader finds **5,309** enabled, non-trigger, active colliders;
  the spike bake logged "5,309 sources" (`work/build/airpit/unity_build_all_20260915_160411.log:33323`). The
  loader finds the same number of sources as Unity's `CollectSources` did.
- **Calibration against the spike's real bake** (`work/build/airpit/arena_airpit_navmesh.bundle`: 8,005
  polygons, agent 0.30/1.7/0.38, no exclusions, parsed with the same reader). Command:
  `python tools/validate/navmesh_diff.py --package work/extract/airpit --bake-rules none --compare-bundle work/build/airpit/arena_airpit_navmesh.bundle`
  (75 s). The spike mesh covers 197,992 of 204,204 Arena samples. Model verdict vs bake: both
  passable 196,006; both blocked 5,410; model passable but not baked 802; model blocked but baked 1,986;
  **agreement 98.63 %** (blocked recall 87 %, precision 73 %). Sweep that fixed the defaults: slack 0 / floor
  margin 0.05 / support +-0.3 m gave 96.8 %; slack 0.10, margin 0.15 gave 98.4 %; widening the floor support
  to +-0.6 m (stair treads) gave 98.6 %. The model is therefore good for ranking and for deciding whether a
  route is cut, not for sub-decimetre claims; the real bake after each Unity build remains the verdict
  (`--compare-bundle` reads it directly with UnityPy).
- Limits: connectivity is measured inside Arena's footprint only (a lower bound: detours through places
  Arena considers unwalkable do not count); no convex-hull handling of `convex` MeshColliders; spheres and
  capsules are coarse tessellations; no Recast region merging or `minRegionArea` island removal.

### (b) Solids of the port that Arena walks through

Run B (planned barriers `BUS_DOORS`, `PASSPORT_CONTROL_DOORS` applied; port bake rules; package 0.5.4):
`python tools/validate/navmesh_diff.py --package work/extract/airpit --points tools/validate/navmesh_probe_points_airpit.json --live-core-points --opened-barrier BUS_DOORS --opened-barrier PASSPORT_CONTROL_DOORS --json-out <file>` (85 s).

- Port colliders 5,309; bake sources 5,148 (67 `LevelBorder` layer 29, 2 `SPAWN_BOUND*`, 92 under the two
  barrier roots dropped). **190 prop instances with 426 collider objects intrude into Arena-walkable agent
  columns** (186 colliders on layer 18, 17 on layer 30, 220 on layer 12, 3 on layer 0) - the plan's "about
  190". Without the two barrier roots: 209 instances / 476 colliders (the extra 19 are the 16 passport lane
  doors and 3 bus-door assemblies, the cross-check of the plan's "both roots are mandatory").
- Hierarchy clusters (prop root = highest ancestor with at most 120 objects; container = its parent path):
  `SBG_Arena_AirPit_ShootOut/SOO` 118 instances (102.0 m2 of blocked Arena samples),
  `.../SOO_LOD0/BUILDING` 9 (68.2 m2), `.../OO/PROPS/INDOOR/BACK` 16 (13.8 m2), `.../OO/PROPS/INDOOR/FRONT` 16
  (11.8 m2), `VARIANT` 10 (8.4 m2), `.../OO/OO_Building` 2 (5.8 m2), `SBG_Arena_AirPit_TeamFight/OO/PROPS/INDOOR/FRONT`
  8 (3.2 m2), `PROPSnoCulling` 3, others 1-2.
- Top instances by blocked Arena area (m2): `SOO_LOD0/BUILDING/Airbus_wing_L` 25.4, `Airbus_wing_R` 12.0,
  `SOO_LOD0/BUILDING/DonB_Ramen_closed` 10.6, `DonB_Ramen_closed (1)` 9.0, `SOO/Flowerbed_steel_01` 8.6,
  `OO/PROPS/INDOOR/BACK/Passenger_ladder_02` 7.8, `SOO_LOD0/BUILDING/Airport_Shop_BS_closed_closed` 6.0,
  `SOO/Cube (10) Group` 5.8 (layer 0 only), `OO/OO_Building/Airport_flowerbed_01` 5.8, `SOO/polywood_stairs` 5.2
  and `(1)` 4.2, `SOO/Sandbag_03 (2)` 4.6 and `(1)` 4.3, `SOO/tarpaulin_wall_03` 3.8,
  `SOO_LOD0/BUILDING/TH_Roll_door_02_closed` 3.5, `VARIANT/Sandbag_04_(ARENA)` 3.4, `OO/PROPS/INDOOR/AIRBUS` 3.2.
  By family: Airbus_wing_L 25.4, Sandbag_03 22.7 (15 instances), DonB_Ramen_closed 19.6 (2), Airbus_wing_R 12.0,
  polywood_stairs 10.6 (3), Passenger_ladder_02 10.1 (2), Flowerbed_steel_01 8.6, Fence_metall_barbwire_2 7.9
  (11), tarpaulin_wall_03 7.0 (3), reserve_woodBox_medium 7.0 (6), Airport_Shop_BS_closed_closed 6.0.
  22 of the 190 instances carry duplicate sibling names (`Transform.Find`/`FindByPath` resolve them to the
  first sibling): they need the parent path or an indexed syntax (full list in the JSON, field `ambiguous`).
- **Arena does not ignore everything:** of 2,092 prop instances with bake-source colliders, 190 are walked
  through, 1,275 lie out of reach of Arena's footprint (no sample within 1.2 m), and the remaining 627 stand
  near Arena-walkable space without being walked through. Proximity alone does not show that Arena's bake
  honoured a prop (a prop against a wall or an edge qualifies), so the survey splits those 627 with a hole test
  (`ring_survey`): **86 are `respected`** = a hole in open floor (at least 12 of 16 points on a circle of half
  the bounding-box diagonal + 0.6 m around the prop are Arena-walkable, its footprint centre is not), the other
  **541 are `near-edge`** (no evidence either way; 394 of them have fewer than 8 ring points walkable, 39 have a
  walkable footprint centre). Only 10 instances are fully surrounded holes (ring 15 or 16 of 16, centre not
  walkable). Totals: 190 walked-through / 86 respected / 541 near-edge / 1,275 unknown. So Arena does honour
  some props, and the split is per instance, not per prefab family and not per container: 15 families have
  walked-through and respected instances side by side (`reserve_woodBox_medium` 6 walked / 6 respected / 25
  near-edge, `cardbox_vinyl` 3 / 3 / 26, `Barricade_Metal_List_02` 3 / 3 / 10, `med_camp_operating_room_wall`
  4 / 3 / 9, `plywood_wall_02` 1 / 2 / 5, `Airport_shop_pillar_01` 1 / 4 / 8), while `Sandbag_03` (15 walked, 8
  near-edge, 37 unknown) and `Barricade_Fanera_01` (7 / 23 / 25) show no respected instance. By container (walked /
  respected / near-edge / unknown): `SOO` 118 / 24 / 138 / 16, `OO/PROPS/INDOOR/FRONT` 16 / 12 / 84 / 111,
  `VARIANT` 10 / 20 / 100 / 212, `SOO_LOD0/BUILDING` 9 / 11 / 24 / 6. The walk-through also hits props of the
  TeamFight scene itself (10 of its 63 instances, 8 of the 24 under `SBG_Arena_AirPit_TeamFight/OO/PROPS/INDOOR/FRONT`).
  The hole test is a heuristic (bounding circle, lattice lookup within 0.15 m, level window -1.0..+0.6 m around
  the prop's lowest point): the reviewer's independent version of it counted 7 surrounded holes instead of 10
  and showed the same family picture. Per-instance rows with ring counts are in the JSON (`presence.rows`).
- **Layer mask finding (new):** Arena's own `NavMeshSurface` collects only layers 9, 11, 13, 18, 30
  (DoorLowPolyCollider, Terrain, Triggers, LowPolyCollider, TransparentCollider); the port bakes every layer but
  4/13/29, including 3,180 HighPolyCollider (12) colliders and the Default (0) ones. With Arena's mask the
  blocked-sample count drops from 4,795 to 4,180 (13 %), and 22 of the 190 instances (those whose colliders
  sit only on layers 12/0, e.g. `SOO/Cube (10) Group`, `.../polywood_stairs_BALLISTIC_Woodthin`) disappear.
  The other 168 are visible to Arena's mask and still walked through.

### (c) Reachability of the probe points

79 probe points (4 player spawns, 14 bot spawns, 2 exits, 2 capture points, 19 core points, 3 hubs, 35
patrol points; 78 have a lattice sample within 0.8 m, the base CAPTURE_ZONE has none) against Arena's mesh and the
port model:

- **Arena:** every spawn, exit, core point and patrol point lies inside component 0 (polygon contained); the
  two capture points do not: Capture_Zone (-13.16,-0.14,1.33) is 0.68 m and the base CAPTURE_ZONE
  (-11.44,-0.07,3.27) 1.18 m from the nearest walkable lattice sample (inside the fenced structure's footprint);
  they snap into component 0 only. Tightest border clearances: hub1.11 0.00 m, hub1.5 0.16 m, bot spawn
  east-north = core point 2 (15.14,26.76) 0.30 m, core point 4 0.39 m, west-south = core 16 0.75 m (the plan's
  two tight spots).
- **Port model**, three scenarios (anchor = Capture_Zone, 78 probes with a sample):

| Scenario | Opened barriers | Model components | Largest | Probes in the anchor's component |
|---|---|---:|---:|---:|
| A (current descriptor) | none | 60 | 3,826.5 m2 | 57 after the first greedy step, 73 of 78 after five |
| B | `BUS_DOORS`, `PASSPORT_CONTROL_DOORS` | 50 | 3,965.7 m2 | 61 after step 1, **78 of 78 after four** |
| C | B plus the four cutters below | 45 | **7,924.6 m2** (97.0 % of Arena's 8,168) | **78 of 78** |

  In A the north spawn field is the 487.8 m2 component (plan: "about 480 m2"); in B the north (1,650.6 m2) and
  the south component still differ. The four route cutters of scenario B, found by the greedy pass (each is
  the single cluster whose exclusion joins the most probes at that step):
  1. `SBG_Arena_AirPit_ShootOut/SOO/fence_ema_01_white (18)` (12 samples, unique name)
  2. `SBG_Arena_AirPit_ShootOut/SOO_LOD0/BUILDING/DonB_Ramen_closed` (313 samples)
  3. `SBG_Arena_AirPit_ShootOut/SOO_LOD0/BUILDING/TH_Roll_door_01_closed` (64 samples)
  4. `SBG_Arena_AirPit_ShootOut/SOO_LOD0/BUILDING/DonB_Ramen_closed (1)` (307 samples)
  All four have their colliders on child objects (so `opened_barriers` semantics, which keep the barrier
  object itself, exclude them) and none has a duplicate sibling name. Re-run C confirms the list: same model
  with these six `--opened-barrier` entries puts all 78 probes in one component and leaves 186 instances
  (417 colliders) that Arena walks through but that cut nothing. Because `opened_barriers` also deactivates
  every non-frame child at raid start (visuals included, `BoothDoorRepairs`), a bake-only exclusion list
  would avoid removing the shop/door visuals; that is the item 8 / §4.5 decision.
- Arena-walkable but without port floor: 98 samples (0.12 %), six groups, the largest 1.2 m2 at
  x 10.65..10.85, z 37.85..40.85 (y 0.6); negligible.

### Probe: how does Arena hide the props in TeamFight? (`probe_mode_toggler.py`, 11 scenes)

**Result: the mechanism is unknown; there is no mode toggler in the data.** What was established:

1. `arena_airpit_preset.bundle` lists scenes per mode only: base Scripts, ShootOut, ShootOut_Light, Sound,
   DesignStuff; TeamFight (and Coop) add TeamFight, TeamFight_DesignStuff, TeamFight_Culling, TeamFight_AI;
   the three ShootOut modes add Tournament_DesignStuff/AI/Culling (no TeamFight scene); LastHero adds
   LastHero_DesignStuff/AI, TeamFight and TeamFight_Culling. No object lists, no variant data.
2. Scripts scene (36, 117 MonoBehaviours in `level36` + `sharedassets36`, 109 classes): all standard EFT
   environment/rendering classes (weather, TOD, post effects, `LocationScene`, `EnvironmentManager`); no class
   named like a toggler (the only name hit is the weather `TriggerGroup`). The same holds for the TeamFight
   (29), TeamFight_DesignStuff (28), DesignStuff (40), Tournament_DesignStuff (114), LastHero_DesignStuff
   (139) and AI scenes (33/116/140). The base ShootOut scene (324) has 17 component classes, none custom
   beyond ballistics, decals, barbed wire, modifier volumes, culling and animator helpers.
3. `Arena.CustomCulling.DynamicCullingDisabler` (6 in the base scene, 3 in TeamFight, 2 in both DesignStuff
   scenes) sits on `VARIANT`, `OO`, `SOO_LOD0`, `BUILDING`, `PROPSnoCulling`, `BUS_DOORS`,
   `PASSPORT_CONTROL_DOORS` and similar roots - but it is a field-less marker (empty body, 32 bytes): it
   excludes objects from the culling system, it carries no toggle data. `LocationScene` is only a registry.
4. IL2CPP `global-metadata.dat` (32.6 MB) holds only about 1,300 readable identifiers and no "VARIANT"
   literal: not searchable on 0.6.0.
5. **Decoded `NavMeshSurface` of the three AI scenes** (new): `CollectObjects Volume` (208.4 x 122.0 x 228.6
   m around (0,2,0)), `PhysicsColliders`, **layer mask `0x40042A00` = layers 9, 11, 13, 18, 30**, voxel 0.07
   m, min region 2, ignore agent/obstacle on, NavMeshData PPtr (5, 2) in TeamFight and LastHero; the
   Tournament surface has the same mask and **no baked NavMeshData** (PPtr 0).
6. Evidence against a runtime hide: the walked-through set is partial inside prefab families and inside
   containers (15 families have both walked-through instances and instances with a hole in open floor, e.g.
   `reserve_woodBox_medium` 6 walked / 6 respected; the 86 respected instances are a hole-test result, not mere
   proximity, see (b)), it includes props of the TeamFight scene itself, and the separate LastHero bake covers the
   same 99.5 % of the TeamFight samples, prop footprints included. A stale or partial editor bake fits the data; it cannot be proven from files.

Consequence for item 8 / §4.5: nothing justifies deactivating the 190 instances at runtime as "mode-hidden".
The measured alternatives are (1) exclude only the four route cutters (bake-only, ideally; they are the
shops/door props `DonB_Ramen_closed` x2, `TH_Roll_door_01_closed`, `fence_ema_01_white (18)`), (2) in addition
collect only Arena's layers in the port bake (explains 22 instances), (3) accept that bots path around the
rest like players do. Fallback candidates of the plan that are NOT needed to connect the 78 probe points: `TH_Roll_door_02/03`,
`Airport_Shop_BS_closed`, the capture-zone `Prison_Metal_Fence_01` pair, the two portal fences (they may still
matter for rooms that hold no probe).

### Verification

- `python -m unittest tools.tests.test_navmesh_oracle tools.tests.test_navmesh_diff`: 58 tests OK (1.0 s; 51
  before the review follow-up, +7 for the presence hole test).
- `python -m unittest discover -s tools/tests -p "test_*.py"`: **791 tests, OK, 44 skipped** (38 s; includes
  the concurrent Phase A additions; this item's share is +58).
- `validate_extract.py --package work/extract/equator-native-v5 --skip-reproducibility` and the Bay5
  equivalent: exit 1 with exactly the documented pre-existing FAIL sets (Equator: preset provenance offset
  5708 != 5716; Bay5: the same plus the disabled MeshRenderer count 302 != 305); no existing file was edited
  by this item, so the sets cannot have moved.
- Run times (12-core host, 21 GB): 72-85 s per full diff run, of which the occupancy build (12 M fragments) is
  13-17 s; memory was not measured (the bit arrays are 26 MB each, fragments stream in chunks of 1.5 M
  candidate cells).

### Open points

- Re-run on `work/extract/airpit-native-v5` once item 5 has produced it. Known 0.5.4 vs 0.6.0 deltas in the
  base scene are `Fence_Armored_1/2` and tarpaulin props; `SOO/Fence_Armored_2` and
  `tarpaulin_wall_03` appear in the walk-through list, so their rank may change.
- After every Unity bake: `python tools/validate/navmesh_diff.py --package <pkg> --descriptor tools/maps/airpit.json --points <bots/exits sidecars> --live-core-points --compare-bundle work/build/airpit/arena_airpit_navmesh.bundle`
  gives the real coverage of Arena's footprint, the model agreement and the component of every probe in the built
  NavMesh (no Unity needed to read the bundle).
- Decision for the lead: runtime deactivation vs bake-only exclusion for the four cutters, and whether to
  collect only Arena's layers in `BakeNavMesh` (item 8).

### Item 7 review follow-up (2026-10-11)

Three review findings, all confirmed and fixed; scripts in the ignored scratch area, numbers re-measured on
the live Arena mesh and the 0.5.4 package (no new Unity or host run).

1. **Portal statement corrected.** The log said the 18 unmatched portal edges are "none inside the main
   component". Re-parsing `sharedassets33` path 2 with the tool's own reader (instrumented copy of
   `_match_portals`, same tolerances): 401 portal edges, 0 off the tile border, 18 without partner, on 18
   polygons (16 in component 0, one in component 1, one in component 12; the review counted 17 polygons, the
   substance is the same). Causes: 15 edges have zero overlap with any same-line edge of a neighbouring tile
   (corner contacts, several of them longer than a sliver, up to 2.80 m: a walkable border that happens to lie on a
   tile boundary) and 3 overlap an edge at another level (4.8, 4.8 and 9.9 m). Loosening the match with an
   all-pairs matcher: 0.1 m / 1.5 m adds 0 links; 0.25 m / 3.0 m adds 16 links, still 34 components and main
   8,168.2 m2; 0.5 m / 6.0 m and 0.05 m / 10 m add 51 / 70 links and merge one 31.0 m2 component into the main
   one (8,199.2 m2, 33 components), which no agent with climb 0.38 could do. The component figures stand.
   Side finding, not changed: `_match_portals` buckets border coordinates at 0.05 m and tests the same and the
   next bucket, which is complete only for `tol <= 0.05` (default 0.02); passing a larger `tol` through
   `build_navmesh` would miss pairs (that is why the check above used an all-pairs matcher).
2. **Component table row corrected.** `3-33` read "1,294.5 in total (largest 326.2)", which are the totals of
   components 1-33 (component 1 is the 326.2 m2 one). Components 3-33: 691 polygons, 770.5 m2 (the sum of the
   per-component areas; 1,294.5 - 326.2 - 197.7 = 770.6 by rounding), largest 91.6 m2, lowest point y 2.19. The
   grand total 9,462.6 = 8,168.2 + 1,294.5 and the chat summary were right.
3. **"Respected" no longer means "hole".** `classify_presence` called a prop respected when it stood within 1.2 m
   of Arena-walkable samples and none lay inside it - a proximity test, so a prop against a wall qualified and
   `Sandbag_03` "8 respected" proved nothing. The docstring and the log said "Arena's mesh has a hole around
   it". Fix in code: `navmesh_diff.ring_survey` adds the hole test (16 points on a circle of half the bounding-box
   diagonal + 0.6 m, at least 12 walkable, footprint centre not walkable) and `classify_presence` now returns four
   states, `walked-through`, `respected` (hole in open floor), `near-edge` (near walkable space, no evidence either
   way) and `unknown`. Seven new unit tests (`PresenceTests`: hole in open floor, hole at the footprint edge with
   9 of 16 ring points, walkable centre, walked/unknown, no ring survey, level window, summaries) and an extended
   end-to-end assertion. Result of run B on the 0.5.4 package (80 s, report with per-instance rows in the
   scratch area): **190 walked-through / 86 respected / 541 near-edge / 1,275 unknown** (the old 627 are 86 + 541;
   the review's independent version of the test found 7 strict holes where this one finds 10 with ring >= 15). The
   walked-through count, the ranking and the reachability results are unchanged by this. The "partial inside a
   family" conclusion survives on a sounder basis (15 families with both classes), `Sandbag_03` leaves the
   examples (0 respected) and `reserve_woodBox_medium` (6 walked / 6 respected / 25 near-edge) replaces it. The
   TeamFight-scene sentence is now measured: 10 of its 63 instances are walked through, 8 of the 24 under
   `.../TeamFight/OO/PROPS/INDOOR/FRONT` (the previous "8 of 21" could not be reproduced). Text corrected in (b)
   and in probe point 6.

Rechecks: `python -m unittest discover -s tools/tests -p "test_*.py"` 791 tests OK (44 skipped);
`validate_extract.py --skip-reproducibility` on `equator-native-v5` (1 FAIL) and `bay5-native-v5` (2 FAIL) give
the same documented pre-existing FAIL sets as before; no tracked file was edited by this follow-up.

## Item 6 - spawns, bots, exits sidecars (2026-10-11, about 20:30-21:00 local)

Offline only: no Unity, no host, no deploy. Server build/test only (the `dist/` server DLL was restored
afterwards, see Verification).

**What changed.**

- New `tools/maps/airpit.bots.json` (schema of `equator.bots.json`): zone `BotZone_Main`, `max_bots 8`,
  4 player spawns, 14 bot spawns, Equator's 5 scav waves and 4 PMC boss waves (2 `pmcUSEC`, 2 `pmcBEAR`,
  chance 50, `IgnoreMaxBots`). New `tools/maps/airpit.exits.json` (schema of `bay5.exits.json` plus a
  `_comment`): `Air Pit North Gate` (-11.0, 0.1, 62.0) and `Air Pit South Gate` (-3.0, 0.1, -58.0), 4 x 3 x 4 m,
  `Individual`, 8 s, chance 100. Both carry a `_comment` with date and evidence.
- `ArenaMapPort.Shared/ArenaMapDescriptor.cs`: `AirPit.Exits` mirrors the exits file (two
  `ArenaExitDescriptor`). Identity text, icon, `OpenedBarriers` and `All` are untouched (items 8, 9, 23).
- `ArenaMapPort.Server/db/arena_airpit/base.json` regenerated with
  `python tools/server/make_base_json.py --map tools/maps/airpit.json` (template: the read-only SPT
  `factory4_day` base). Diff against the committed spike file: only `BossLocationSpawn` (0 -> 4), `BotMax`
  (0 -> 8), `OpenZones` ("" -> `BotZone_Main`), `exits` (0 -> 2), `waves` (0 -> 5) changed and `SpawnPointParams`
  (18) appeared (98 -> 99 keys); `Name` still reads "Arena: Air Pit (spike)" (item 23).
- `ArenaMapPort.Server.Tests/AirPitMapTests.cs` (3 tests): `ExitTransformsMatchTheSidecarAndServerEligibility`
  (Bay5-style: descriptor = sidecar = shipped `exits[]`, entry point, type, chance, time),
  `SpawnPointsWavesAndSquadsMatchTheBotsSidecar` (every spawn id/position/rotation/sides/categories/zone/
  infiltration, `BotMax`, `OpenZones`, waves, squads, no loot, scav off), `ShippedBaseJsonParsesAsTheServerLocationModel`
  (18 / 4 / 14 / 2 / 5 / 4 / 8 through SPT's own `LocationBase` and converters).
- `ArenaMapPort.Server.Tests/LocationRegistrationTests.cs`: the two "no authored exits" fixtures moved from
  AirPit to Bowl (item 9 would have done this; AirPit now has exits, so they could not stay) and one assertion
  pins AirPit's two descriptor-only names.
- `tools/tests/test_airpit_spawns.py` (13 tests): sidecar shape against Equator/Bay5, generated `base.json`
  fields are current (regenerated in memory and compared), policy keys, finite ground-floor points, ArenaMode
  burst coverage (three bot points >= 25 m away from every cell of x -57..33, z -79..86; the minimum is 10),
  no bot within 2 m of a player marker, every player >= 10 m from every exit, both PMC sides get both ends;
  gated on the ignored extract: the 7 marker-derived points equal the original marker world transforms;
  gated on the installed Arena: the 11 core-point-derived spawns equal Arena's AI core points except the two
  moved ones, and every spawn and exit lies in Arena's play-space component with its clearance.

**How the positions were recomputed (not copied from Appendix A).**

- The 20 original `SPAWNS/ScavPMCspawn (n)` markers of `Arena_Airpit_TeamFight_DesignStuff` were resolved
  through the parent chain (`port_colliders.Scene.world`): north pocket markers 6-10 and 16-20 at z 75.18..80.07,
  yaw 180.15; south pocket 1-5 and 11-15 at z -74.58..-71.70, yaw 0 (markers 3 and 15 face 35.4 and 323.5
  degrees; the sidecar gives marker 15's point rotation 0.0 like the rest of the south pocket). Player spawns: All = marker 19 (-10.24, 76.74), Usec = 16 (0.33, 78.30),
  Bear = 4 (-9.94, -74.40), South_All = 13 (-16.91, -71.70); rotation 180.0 / 0.0 (the markers' 180.15 is not
  kept). Bot spawns from markers: 9 (-22.42, 79.62), 17 (-2.81, 75.18), 15 (-6.04, -71.99). All 18 coordinates
  match Appendix A to the 2nd decimal.
- Bot spawns from Arena's own AI core points (live `Arena_Airpit_TeamFight_AI`, `extract_ai_anchors.extract_map`,
  read-only): ids 6, 5, 17, 2, 1, 18, 15, 16, 3, 12, 13 -> north-approach, north-hall, north-hall-west,
  east-north, east-mid, east-south, west-mid, west-south, center-east, plane-south, south-apron (all match
  Appendix A).
- Source check: the same 20 markers read straight from the live Arena 0.6.0.47752 `level28` differ from the 0.5.4
  package scene `work/extract/airpit` by at most 0.00006 m, so deriving from the 0.5.4 package is exact for this
  scene (the plan's 214-object delta is in `Arena_AirPit_ShootOut` only). `airpit-native-v5` did not exist yet;
  the gated test prefers it when item 5 produces it.
- Y: 0.1 for every spawn and exit (markers 0.08-0.14, Arena NavMesh surface 0.02-0.11 at these points).

**Item 7 corrections applied (markers with < 1 m clearance moved inward).** Arena-mesh border clearance
(`navmesh_oracle.NavMesh.clearance`, 3,853 polygons) of the draft points: east-north (core 2) 0.30 m and west-south
(core 16) 0.75 m were the only spawn/exit points under 1 m. Moved: **east-north (15.14, 26.76) -> (16.6, 26.8)**
(1.5 m east, clearance 1.76 m; Arena's walkable area ends at x about 15.0 there, the clearance grows to 2.0 m at
x 17) and **west-south (-45.88, -25.91) -> (-40.1, -27.7)** (6.0 m east into the open floor, clearance 2.24 m; the
old point lies on a narrow strip along x about -45.8 whose best clearance is about 1.0 m and that joins the open
floor only through a passage near z -26.5, so no point within 4 m reaches 1.2 m). Final clearances of all 20 points: 1.38 m (south-end) ... 5.53 m; the exit boxes'
centres 3.81 m and 4.25 m (half box 2 m). All 20 lie in Arena's component 0 (8,168.2 m2) on the ground level
(surface y 0.024-0.106).

**Port-collider model (item 7 tool, 0.5.4 package, barriers `BUS_DOORS` + `PASSPORT_CONTROL_DOORS`).**
`python tools/validate/navmesh_diff.py --package work/extract/airpit --points tools/maps/airpit.bots.json --points
tools/maps/airpit.exits.json --live-core-points --opened-barrier BUS_DOORS --opened-barrier PASSPORT_CONTROL_DOORS`
(several minutes while other agents loaded the CPU): every one of the 18 spawn points and 2 exits is "passable" for
the port agent and for Arena's agent (floor present, no solid in the agent column). They fall in several model
components, as in item 7's scenario B (comp 0: south pocket, south apron, south gate, east-mid, west-mid,
center-east; comp 1: north pocket, north-approach, east-north, north gate; comp 2: north-hall, plane-south; comps
3, 4, 5: east-south, west-south, north-hall-west), and the tool's greedy list (`DonB_Ramen_closed`, `fence_ema_01_white (18)`,
`TH_Roll_door_01_closed`, `DonB_Ramen_closed (1)`) joins all 77 located probes to the anchor, unchanged from item 7.
So the sidecar points themselves need nothing from item 8 except the connectivity the plan already assigns to it;
none of them sits on a cutter.

**Counts in the regenerated `base.json`.** `SpawnPointParams` 18 (4 `Player` with infiltration `ArenaAirPit`, 14 `Bot`
with zone `BotZone_Main`, categories `Bot` + `BotPmc`), `exits` 2, `waves` 5, `BossLocationSpawn` 4, `BotMax` 8,
`OpenZones` `BotZone_Main`, `Banners` [], `Loot` [], `DisabledForScav` true.

**Verification.**

| Check | Result |
|---|---|
| `python -m unittest tools.tests.test_airpit_spawns` | 13 tests OK, 1.7 s (none skipped: package and Arena install present) |
| sensitivity | the Appendix A draft (east-north 0.30 m) fails the NavMesh test; a moved bot spawn makes the "base.json current" test fail |
| `python -m unittest discover -s tools/tests -p "test_*.py"` | **852 tests, OK, 45 skipped** (34 s); +13 are this item, the rest are concurrent Phase A work |
| `dotnet test ArenaMapPort.Server.Tests/ArenaMapPort.Server.Tests.csproj -c Release` | **630 passed**, 0 failed, 0 skipped (627 baseline + 3 new) |
| `python tools/validate/validate_map_descriptors.py` | OK (14 descriptors, 14 catalog entries, release `All` membership) |
| `work/airpit-parity/parity_gate.py check ...baseline_head_2723d88.json` | Equator and Bay5 validator output UNCHANGED (48 / 72 lines, FAIL sets as documented) |
| `dist/` after `dotnet test` | the build restaged the server DLL (`761b5807...`); restored with `work/airpit-parity/restore_server_dll.py`, `dist_vs_manifest.py`: 48 of 48 files identical to the 0.1.0 manifest |

**Open for later items.** Item 12 host raid (MAP-RECIPE section 5): `uibridge_teleport dryRun=true` from each of
the 18 points and both exit centres to the capture point; `[BotSpawn] wave fired` lines; `uibridge_bots` brain
layers; extraction through both gates. The north gate and the north pocket are on opposite sides of the passport
lanes and the bus apron/south gate is beyond the bus doors, so extraction on foot needs the item 8 barriers.
Observation: the north-approach bot marker (Arena core 6) lies 1.5 m from the north gate centre; harmless (bots do
not extract and the 25 m sphere decides the real spawn position), move it only if the host shows bots idling in the
box. `tools/validate/navmesh_probe_points_airpit.json` (item 7 draft points) now duplicates these sidecars and can
be dropped in favour of `--points tools/maps/airpit.bots.json --points tools/maps/airpit.exits.json`.
Item 9 still has to add AirPit to the server csproj `Content` / `DbFiles` lists; until then the shipped
`dist/` does not carry `arena_airpit/base.json` (the test project copies every `db/**/*.json`, which is why the new
tests read it).

## Item 3 - data-driven native contracts and validators (done 2026-10-11, about 20:00-21:10 local)

Offline only: no Unity, no host, nothing written outside the repository and the ignored scratch area.

**What changed.**

- New `tools/extract/native_contracts/{equator,bay5,airpit}.json` (named like `tools/maps/<stem>.json`,
  `map_key` = `arena_<stem>`): scene set and BallisticCollider count per scene index, `require_unsliced`,
  `implicit_scene_identity` (Bay5 only: released schema-1 packages predate `source.map_key`), optional census
  pins `features`, and `audio` (`null` or the ambient-audio census: feature name, schema, source scene, owned
  root, players, support, banks, clips, `group_coverage`, `states`). Equator keeps its Equator-only audio
  branch as data: 23 grouped players, inactive `level56:970 far_dogs2` / `level56:975 far_dogs1`, mixer
  `CommonAmbInEffects`, loop `level56:965`. `notes` strings carry the provenance, including why AirPit's
  `audio` is `null` (item 13 fills it).
- New `tools/extract/native_contract_loader.py`: the only reader (`load_contracts`, `load_contract`,
  `parse_contract`). Strict and fail-closed: unknown or missing keys, duplicate JSON keys, bool-as-int, non
  canonical scene indices, `ballistics` keys different from `scenes`, a zero ballistics total, `map_key` not
  matching the file stem, an empty or missing directory, schema-2 audio without `states` (schema 1 must use
  `null`), `group_mixers` not one per `AmbientSoundPlayerGroup`, and `grouped_players` not matching the player
  census under the coverage mode. `NativeContractError` is both a `HardExtractError` (extractors halt) and a
  `ValueError` (the validators report that as a failed check). Item 13's extractor reads its census from the
  same loader.
- `tools/validate/validate_native_features.py`: the `MAPS`, `SCENES` and `AUDIO_ROOT` literals are gone; `MAPS =
  load_contracts()`. `map_contract` takes the implicit Bay5 identity and the unsliced rule from flags;
  `feature_enabled` requires `native_features` + `native_ballistics` always and `native_audio` exactly when the
  contract has `audio`, and `native_features.required` is `["ballistics"]` (+ the contract's audio feature);
  `load_audio` is generic (`check_audio_group_states` implements coverage mode, grouped count, inactive and
  disabled players, per-group mixer route in source order, group enabled state and loop players from the pins);
  `validate_package`/`validate_bundle` skip the audio proofs when `audio` is `null`, and the bundle proof then
  rejects any native audio component (`reject_undeclared_audio`); `extra_counts` returns the rain-cover objects
  only, with `AudioSource` 0, for such a map (the importer authors no audio root without `native_audio`).
  `validate_ballistic_source` (source recheck by scene index) and every other proof are unchanged.
- Census pins (`features`, AirPit only): `static_decals` 481, `light_culling` 533 / 130 / 0, `raincover`
  2 IndoorTrigger / 0 DryPlane / no WeatherObstacle, `navmesh_modifiers` 20 + 12, `small_physics_objects` 0.
  They are checked against the manifest summaries (a missing entry fails) and, when scenes are passed, against
  the actual component census of the scene JSON (`native component census`), on top of the existing
  decal-count-versus-scenes rule. Strict equality: `True` is not `1`.
- Tests: new `tools/tests/test_native_contracts.py` (48 cases, 1 skipped when `work/extract/airpit-native-v5`
  is absent); `test_native_features_validation.py`, `test_raincover.py` and `test_navmesh_modifiers.py` follow
  the nested `contract['audio'][...]` shape (3 + 1 + 1 edits, no behaviour change).

**Design decisions.**

- `features` is `null` for Equator and Bay5 on purpose. Their optional entries keep shape-only validation
  exactly as before. Pinning them would be a behaviour change and would reject one of the two sources: Bay5's
  `static_decals` is 499 in the accepted 0.5.4 package and 502 in the 0.6.0 audit extraction. When the 0.6.0
  re-extraction becomes the accepted package, the numbers can be pinned in a reviewed change.
- AirPit gets `require_unsliced: true` (generic packages record `slice: {}`), and `small_physics_objects: 0`
  although the task text did not list it (the item 2 dry-run table does).
- Audio state checks are generalized, not special-cased: the per-group mixer list replaces `support[0]`, the
  disabled-player list replaces "all enabled", and Equator's checks run unchanged in order. Only message text
  changed for those failures (see below). AirPit's two-group layout (Out/In with different mixers) is
  expressible without code (`group_mixers` in source order, `group_coverage: all_players`); a test builds
  exactly that shape.

**Verification.**

| Check | Result |
|---|---|
| `python -m unittest discover -s tools/tests -p "test_*.py"` | **839 tests, OK, 45 skipped**, 38 s (the working tree also holds the concurrent items 6/7 suites; this item added 48, 1 of them skipped) |
| Golden test (`GoldenLegacyTests`) | frozen copy of the pre-item-3 `MAPS`/`SCENES`/`AUDIO_ROOT` literals equals the data files value for value, scene and ballistics iteration order included; Equator's 23 / dogs / mixer / loop literals equal `states`; sums 2,892 / 6,322 / 3,548 |
| Negative tests | wrong census fails (decals 480/482, each light-culling key, indoor triggers 3, dry planes 1, weather obstacle, bool-for-int, scene components +-1 per type), missing feature fails (`static_decals`, `light_culling`, `raincover`, `native_features`, `native_ballistics`), audio entry rejected for AirPit, `required` must be exactly `["ballistics"]`, Equator/Bay5 still require `native_audio`, loader schema and file-level mutations (unknown/missing/duplicate keys, bad indices, zero totals, stem mismatch, states versus schema and census, empty directory) |
| Mutation check (scratchpad `mutation_check.py`) | with `check_feature_pins`, the scene census, `check_audio_group_states`, `reject_undeclared_audio` or the strict `same()` replaced by a no-op/loose version the new tests fail (14 / 2 / 11 / 5 / 2 failures) |
| Differential harness, old versus new validator (scratchpad `diff_harness.py`, legacy tree snapshot) | 484 cases over the accepted v5 and the 0.6.0 audit packages (manifest mutations x with/without scenes, audio-index mutations of players/groups/banks/clips/census/state, `map_contract`, `extra_counts`, `load_audio`): **0 outcome differences**; 458 identical, 26 message-only differences, all the retired "Equator ..." wording (`Equator active audio group coverage` -> `audio active group coverage`, `... indoor group route/state` -> `audio group mixer routes[0]: ...` / `audio group route/state`, `... electricity loop` -> `original looping audio players ...`, `Equator native features require unsliced scenes` -> `arena_equator ...`) |
| Full validator on the fresh 0.6.0 audit packages (`work/arena-update-audit/{equator,bay5}`, `--skip-reproducibility`, these exercise the source recheck by scene index and the whole audio source proof) | legacy tree and changed tree: exit 0 and **byte-identical output** for both (sha256 of the legacy Equator output `56d4d572...`, Bay5 `4bf1c15a...`) |
| Accepted v5 packages, `validate_extract.py --package work/extract/{equator,bay5}-native-v5 --skip-reproducibility` (via `work/airpit-parity/parity_gate.py check ...baseline_head_2723d88.json`) | Equator UNCHANGED (48 lines, 1 FAIL, `677fc289136a08ea`), Bay5 UNCHANGED (72 lines, 2 FAIL, `7e7a7c51eb9c8b08`); FAIL sets are exactly the two documented 0.6.0 live-source drifts. These stop at the preset-offset check, which is why the audit packages above carry the real parity proof |
| Accepted v5 packages, manifest/scene/audio-index gates (`AcceptedPackageTests`) | `feature_enabled(manifest, scenes)`, `map_contract`, `load_audio` pass; `extra_counts` Equator `{GameObject 32, Transform 32, AudioSource 25}`, Bay5 `{63, 63, 24}`, equal to the legacy validator's values |
| AirPit dry-run package (item 2 output in the scratchpad), `validate_extract.py --package <it> --skip-reproducibility` | **exit 0**; the single item 2 FAIL (`incomplete required feature contract`) is gone: "optional native feature source/clip/reference contract validates" (live Arena 0.6.0 source recheck of 3,548 ballistic components by scene index, all pins matched) |
| Same package, full run (`--extractor-timeout 900`, both reproducibility reruns) | **exit 0**: both reruns exit 0, "two independent fresh extractor runs ... byte-identical package" 0 diffs, package-freshness check passes. This closes PLAN-AIRPIT item 2's "passes `validate_extract.py` including reproducibility" end to end |
| `git diff` for `extract_equator.py`, `extract_bay5.py`, `validate_bundle_roundtrip.py`, the eleven other plans | none; `extra_counts` and `validate_bundle` keep their signatures |

**Notes for later items.**

- Item 13 (audio): replace `"audio": null` in `airpit.json` with schema 2, `level39`, `AmbientSoundPlayer` 26,
  `AmbientSoundPlayerGroup` 2, banks 9, clips 61, a root name, `group_coverage: all_players`, and `states`
  (`grouped_players` 26, `group_mixers` in source order, `inactive_players` `{}`, `disabled_players` `[]`,
  `loop_players` `[]`); the loader cross-checks those numbers. Register the exporter in
  `native_features.AUDIO_EXPORTERS`. The extractor, importer and client literals are untouched here
  (`extract_bay5_audio.py:143-146, 223-232, 248`, `Bay5AudioImporter.cs`, `Bay5AmbientAudio.cs`).
- Item 5 (real 0.6.0 package): `AcceptedPackageTests.test_airpit_native_package_when_present` starts running as
  soon as `work/extract/airpit-native-v5` exists; any census drift against the pins fails there first.
- `validate_bundle_roundtrip` for AirPit: `extra_counts` yields rain-cover objects (4 for 2 IndoorTrigger) and
  `AudioSource` 0; the package has no `AudioSource` component, so the roundtrip's AudioSource and Transform
  equality checks hold. Not run against a bundle (none exists before item 12).

## Item 4 - per-map extractor tables read the plan roles, plus the mode-object sidecar (done 2026-10-11, about 21:00-21:55 local)

Offline only: no Unity, no host, no deploy. Inputs: the live Arena 0.6.0.47752 install (read-only) and, for
geometry, the ignored packages `equator-native-v5`, `bay5-native-v5` and the old `work/extract/airpit`. No
csproj staging row was added (items 20/21 own those; a listed-but-missing file fails Release builds).

### What changed

| File | Change |
|---|---|
| `tools/extract/extract_raincover.py` | `scripts_scene_index_for_plan(plan)` (role `scripts`, no table fallback), `export_for_plan`, and a CLI (`--plan` or legacy `--map`, `--output`); `SCRIPTS_SCENE_INDEX` kept for Equator/Bay5. `export(..., scripts_scene_index=)` itself is item 2's wiring, unchanged. The CLI runs `verify_scene_mapping` first, so a moved Scripts scene fails closed. |
| `tools/extract/extract_spatial_audio.py` | `--map` (repeatable; default stays `arena_equator`, `arena_bay5`), `scene_for_map(key)` (legacy table, else the plan's `sound` role from `tools/extract/plans/<stem>.json`), `export(..., scene=None)`, `plan_path`. `validate_graph`'s identity check accepts any well-formed `arena_<stem>` key instead of the two literals (still fail-closed on a malformed one). A plan-driven map is checked with `verify_scene_mapping` before export. |
| `tools/extract/extract_ai_anchors.py` | `MAPS["airpit"] = (33, "Arena_Airpit_TeamFight_AI")`, `ai_scene_for_plan` / `map_key_for_plan`, `--plan` (reads the `ai` role, so the next map needs no table entry). `DEFAULT_MAPS = ("equator", "bay5")`: running without `--map` still writes only the two shipped sidecars. |
| `tools/extract/extract_decals.py` | The refresh CLI (`--package`, `--output`) accepts generic `extract_map.py --native-features` packages: `package_kind`, `open_source` (plan taken from the repository via `validate_manifest_plan`, never from the package), `refresh`, `dump_json`. Generic output keeps `extract_map.write_json` formatting and writes textures under `bc6h_rgb_decoder`; the legacy Equator/Bay5 path is the old code (same loader, same registry, plain `json.dumps`). A package without `native_features` now fails before the expensive work with a clear message instead of a late `KeyError`. |
| `tools/extract/extract_mode_objects.py` (new) | Mode-object sidecar extractor, schema below. |
| `tools/maps/airpit.ai.json` (new, tracked) | 19 core points, 1 PatrolWay, 3 hubs with 12 / 12 / 11 sub points, 38 PatrolPoints all consumed. |
| `tools/maps/{equator,bay5,airpit}.modes.json` (new, tracked) | Mode-object sidecars (positions only). |
| `tools/tests/test_extractor_plan_roles.py` (new, 25 cases), `test_mode_objects.py` (new, 34), `test_ai_anchors.py` (+10) | See Verification. |

`extract_equator.py`, `extract_bay5.py`, `validate_native_features.py`, `extract_map.py`, the other eleven plans and
`airpit.json` were not touched by this item.

### The four commands for `arena_airpit` (live 0.6.0 source)

| Command | Result |
|---|---|
| `python tools/extract/extract_raincover.py --plan tools/extract/plans/airpit.json --output <scratch>` | 2 IndoorTrigger (`Bus_IndoorTrigger` baked centre (-13.05, 1.58, -66.58) size 13.83 x 2.42 x 3.06; `IndoorTrigger 11` centre (-15.18, 8.5, 41.79) size 221.2 x 60 x 177.1), 0 DryPlane, `weather_obstacle` false, `source_scene` `Scripts (level36)`, 1 baked-vs-live Bounds mismatch (the Bus trigger; the log line printed 125.76 m, which is the largest single-axis offset, the Euclidean distance is 155.2 m and matches the plan's "about 155 m"; corrected in the review follow-up of item 5). `raincover.json` is byte-identical to the one inside item 2's native package. 1.9 s. |
| `python tools/extract/extract_spatial_audio.py --map arena_airpit` | `work/extract/spatial-audio/arena_airpit/` (ignored): source scene `level39`, **24 rooms, 94 portals, 78 trigger areas**, bake `source.audiobakedata` **481,738 B** (276 room pairs, **16,304 routes**, SHA-256 `4be00976...a6bd9`), **1 room tone** (`sharedassets39.assets_9.wav`, 2,822,444 B), `mixer_effects` decoded, `validate_graph` passes. 3.6 s. |
| `python tools/extract/extract_ai_anchors.py --map airpit` (equal to `--plan tools/extract/plans/airpit.json`, byte-identical output) | `tools/maps/airpit.ai.json`: **19 core points** (ids 1..19), **3 hubs with 12 / 12 / 11 = 35 sub points** (hub core point ids 1, 9, 17), 38 PatrolPoints all consumed. |
| `python tools/extract/extract_decals.py --package <item 2 native package> --output <scratch>` | `DECALS VERIFIED: arena_airpit: 481 instances, 55 materials, 52 textures`, exit 0; the refreshed package is **byte-identical** to its input (`diff -rq` over 1.4 GB, no difference), i.e. the generic path reproduces what `extract_map.py --native-features` wrote. |

### Parity evidence (Equator / Bay5 unchanged)

* Spatial audio: the HEAD script (pristine `git archive` copy) and the new script, default arguments, write identical
  trees for `arena_equator` and `arena_bay5` (`diff -r` empty, bake sizes 1,052,882 B / 191,304 B).
* Spatial audio vs the accepted `work/extract/spatial-audio/arena_{equator,bay5}`: a fresh export from the live
  0.6.0 install has byte-identical bakes and room-tone WAVs; `spatial-audio.json` differs in exactly nine leaves per
  map, all provenance: `source_scene_sha256`, the clip `source_asset_sha256`, six `mixer_effects` hashes
  (`source_asset_sha256`, `system_raw_sha256` and the `raw_sha256` of the delay / effects_sends / effects_volumes /
  reverb handlers) and `systems[0].location_info.id`. The graph, rooms, portals, areas and routes are the same. **Do not run
  `extract_spatial_audio.py` without `--map` for the release**: the default still rewrites the two accepted
  directories with those 0.6.0 provenance values (AirPit is the only map written by this item).
* Rain cover: `--map arena_equator` and `--map arena_bay5` (legacy table) write a `raincover.json` byte-identical to the
  accepted `equator-native-v5` / `bay5-native-v5` files.
* AI anchors: `--map equator` and `--map bay5` regenerated from the live 0.6.0 install are byte-identical to the
  tracked `tools/maps/equator.ai.json` / `bay5.ai.json`.
* Decals refresh CLI, legacy path: the HEAD script and the new script run on the accepted `equator-native-v5` write
  byte-identical output trees (`DECALS VERIFIED: arena_equator: 348 instances, 60 materials, 54 textures`). On
  `bay5-native-v5` both fail the same way (`StaticDeferredDecal: conflicting existing decal`). Against the live 0.6.0
  install neither reproduces the accepted packages (Equator: `manifest.json`, three materials and three extra
  textures differ; Bay5: the source has three more decal/render objects, see `arena-update-20261003.md`), so the
  legacy refresh is not a way to update the accepted 0.5.4 packages; this is source drift, identical before and after
  this item.
* `extract_map.py --census-only` on `airpit.json`: output identical to a pristine HEAD copy (20,252 / 3,833 / 15,357 /
  638 / 647 GameObjects); the plain extraction path is not touched.
* `work/airpit-parity/parity_gate.py check baseline_head_2723d88.json`: `equator-native-v5` UNCHANGED (48 lines, 1 FAIL,
  `677fc289136a08ea`), `bay5-native-v5` UNCHANGED (72 lines, 2 FAIL, `7e7a7c51eb9c8b08`); the FAILs are the two documented
  0.6.0 live-source drifts (see "Accepted-package validator state" above), not caused by this item.

### Mode-object sidecar (`tools/maps/<stem>.modes.json`, user decision 2026-10-11)

Generic over any extracted package (`scenes/*.json`, full hierarchy with local transforms): world positions are
composed through the parent chain (translate * rotate * scale, unit quaternions, no shear), rounded to 1e-4 m /
degree, `-0.0` written as `0.0`, UTF-8 with LF, so the file is byte-reproducible.

```
python tools/extract/extract_mode_objects.py --package work/extract/equator-native-v5
python tools/extract/extract_mode_objects.py --package work/extract/bay5-native-v5
python tools/extract/extract_mode_objects.py --package work/extract/airpit
```

The map key comes from `manifest.source.map_key`, else from the extractor script (the two legacy packages), else
`--map-key`; the output defaults to `tools/maps/<stem>.modes.json`. A hard failure (exit 2) is a parent id that names
no object, a parent cycle, a malformed vector, a spawn bound without a BoxCollider or a marker exactly on z == 0;
missing categories only warn and leave an empty list or `null`.

Schema (`schema_version` 1, keys sorted):

| Key | Content |
|---|---|
| `map_key` | `arena_<stem>` |
| `source_package` | `{name, extracted_utc, extractor_script, scenes[]}` of the package the data was composed from (no absolute paths) |
| `counts` | `capture_zones`, `spawn_bounds`, `spawn_markers_north`, `spawn_markers_south` |
| `capture_point` | `{position [x,y,z], method, lights[{scene, path, position}]}` or `null` |
| `capture_zones[]` | `{scene, path, position, at_origin, projection_position, anchor_position, distance_to_capture_point}` for every `Capture_Zone` / `CAPTURE_ZONE` object |
| `spawn_bounds[]` | `{scene, path, name, center, size, rotation [x,y,z,w], yaw, side, active}`: world box of each `SPAWN_BOUND*` BoxCollider (collider size times lossy scale) |
| `spawn_markers.north[]` / `.south[]` | `{scene, path, name, stem, index, position, yaw, side, active}`: direct children of the `SPAWNS` object other than the bounds, grouped by the sign of z, sorted by scene, stem, index |

Conventions: all names are compared stripped and case-folded (Arena has `SPAWN_BOUND_FIGHTER ` and
`EQUATOR_CAPTURE_POINT_LIGHT ` with trailing blanks); `yaw` is the heading of the object's +z axis in degrees [0, 360)
(Unity `eulerAngles.y` for yaw-only rotations; 0 looks along +z, 90 along +x); `index` is the `name (n)` suffix or
`null`; `active` is active-in-hierarchy; north is z > 0.

**Which marker is the capture point.** The `CAPTURE_POINT` container is at the origin on all three maps (local 0, 0, 0)
and its position says nothing. Its direct children named `*CAPTURE_POINT_LIGHT*` do: Equator 1, Bay5 1, AirPit 2.
`capture_point.position` is the mean of those lights' world positions (`method` `capture_point_light_centroid`), the
only rule that exists on all three maps. The `Capture_Zone` objects are kept as evidence, not as alternatives:

| Map | `capture_point` | Capture_Zone evidence |
|---|---|---|
| Equator | (1.28, 0.25, 4.22), 1 light | TeamFight `CAPTURE_ZONE` root at the origin, no PROJECTION child: no anchor |
| Bay5 | (91.26, -4.366, 29.44), 1 light | `Capture_Zone` and its PROJECTION child both at the origin: no anchor (its decals cluster around (91, -3.9, 29)) |
| AirPit | (-13.21, -0.05, 1.29), 2 lights at (-45.38, 0, 3.82) and (18.96, -0.1, -1.24), 64 m apart | TeamFight `Capture_Zone` root (-13.1639, -0.1395, 1.3311), 0.109 m away (0.06 m horizontally); DesignStuff `Capture_Zone` root (-186.1, 18.0, -23.6) whose PROJECTION child lands on the same (-13.1639, -0.1395, 1.3311); the ShootOut base-scene `CAPTURE_ZONE` (-11.4393, -0.074, 3.2678) is 2.65 m away and belongs to the other mode |

AirPit therefore has one capture point between two lights, not two capture points (the item 7 probe file carries the
TeamFight zone and the ShootOut zone as `capture_teamfight` / `capture_base`; they are the same point, 2.65 m apart in
the two modes' authoring).

**Counts and ranges.** All three maps: 2 spawn bounds (one per side) and 10 markers per side (20 in total).

| Map | North markers (z > 0) | South markers (z < 0) | Bounds (centre, size) |
|---|---|---|---|
| Equator | 10, `ScavPMCspawn (6..10, 16..20)`, z 44.5..48.6, yaw 180 (one 133.1) | 10, `(1..5, 11..15)`, z -42.0..-38.4, yaw 0 (one 37.9) | N (0.05, 2.16, 47.38) 20.15 x 4.28 x 16.34; S (0.05, 2.16, -39.95) 20.15 x 4.28 x 13.41 |
| Bay5 | 10 `Usec house` / `Usec house (1..9)`, yaw 270 | 10 `Bear house` / `Bear house (1..9)`, yaw about 0 (360) to 90 | N (139.30, -1.10, 54.62) 20.59 x 4.28 x 29.69; S (56.16, -1.94, -6.46) 20.59 x 4.28 x 16.00 |
| AirPit | 10, `ScavPMCspawn (6..10, 16..20)`, z 75.2..80.1, yaw 180.148 | 10, `(1..5, 11..15)`, z -74.6..-71.7, yaw 0 / 35.4 / 323.5 | N (-11.47, 2.18, 77.16) 49.96 x 5.59 x 16.00; S (-11.65, 2.18, -72.41) 22.32 x 5.59 x 16.00 |

Bay5's markers are `Bear house` / `Usec house` BoxColliders with NavMeshModifier, not `ScavPMCspawn` spheres; the extractor
takes whatever sits directly under `SPAWNS`, so `stem` tells them apart. Nothing in the data pairs markers with factions.

**Independent check.** The AirPit sidecar's 20 marker positions, read again from the live 0.6.0 `level28` with
`census.world_transform_of_transform` (UnityPy), deviate by at most 6e-5 m; Equator `level57` 2e-5 m and Bay5 `level88`
1e-5 m. The two AirPit light children equal their live world positions exactly. (The 0.5.4 packages and the 0.6.0 install
therefore agree on every marker and capture light. `airpit.modes.json` was composed from the old 0.5.4 package
`work/extract/airpit`, as instructed; the tracked-sidecar test regenerates from the package named in
`source_package.name`, so after item 5 rerun the command on `airpit-native-v5` and commit the new file: only
`source_package` should change.)

### Verification

| Check | Result |
|---|---|
| `python -m unittest discover -s tools/tests -p "test_*.py"` | **921 tests, OK, 45 skipped** (852 before this item, same tree; +69 = 34 `test_mode_objects` + 25 `test_extractor_plan_roles` + 10 `test_ai_anchors`) |
| `tools/tests/test_extractor_plan_roles.py` | Raincover: index from the role, no fallback for a plan without it, `export_for_plan` arguments, CLI rules and exit 2 paths, live AirPit counts. Spatial audio: legacy table unchanged, `level39` from the role, missing role or plan or mismatching plan key is an error, malformed keys refused, `validate_graph` identity for the three maps and four malformed keys, `main` default and AirPit flows with the plan check, live AirPit counts (24 / 94 / 78, 481,738 / 276 / 16,304, 1 clip). Decals: `package_kind`, plan-drift refusal, formatting per package kind, BC6H decoder only for generic, refusal of an existing output / a package without `native_features` / an incomplete scene set / an empty census. |
| `tools/tests/test_mode_objects.py` | Synthetic scenes: scaled and rotated parent chains, yaw range and `-0.0`, hierarchy failures (missing parent, cycle, repeated id, malformed vectors), light centroid, zone anchors and fallback, null capture point, bound centre/size under lossy scale and rotation, marker grouping/ordering/index parsing, z == 0 and missing BoxCollider failures, order independence, package round trip, determinism, exit codes; tracked sidecars equal a fresh extraction from the package they name (skips without the ignored packages), plus AirPit's measured values and the item 6 player-spawn markers 19, 16, 4, 13. |
| `test_ai_anchors.py` | Three-map table, default maps, plan `ai` role equals the table entry, plan without a role is an error, CLI `--map` / `--plan` selection and exit 2 paths (extract patched), live AirPit counts 19 / 12-12-11, tracked `airpit.ai.json` equals a live extraction. |
| Item-specific commands | The four commands above, parity checks above, `extract_map.py --census-only` unchanged. |

### Open points

1. The default of `extract_spatial_audio.py` (no `--map`) rewrites the accepted Equator/Bay5 inputs with 0.6.0
   provenance hashes; it was left as it was. Name the map when regenerating (item 21 should say `--map arena_airpit`).
2. `ArenaMapPort.Server.Tests/AiAnchorsTests.cs` lists `equator.ai.json` and `bay5.ai.json` explicitly (`InlineData`);
   item 20 should add `airpit.ai.json` with 19 / 3 / 35.
3. `extract_decals.py refresh` re-reads the live install. `validate_manifest_plan` checks the plan digest, the scene
   order and the BuildSettings mapping, not the Arena version, so refresh a generic package only against the install it
   was extracted from (the AirPit check above: package and install were both 0.6.0).
4. Whether ArenaMode wants the capture point at the light centroid (-13.21, -0.05, 1.29) or at the TeamFight
   `Capture_Zone` (-13.16, -0.14, 1.33) is moot for AirPit (0.11 m apart); for Equator and Bay5 the lights are the only
   locating marker. Nothing here verifies the points on the host (no raid was run).

### Item 4 review follow-up (2026-10-11, after the first review)

Three findings, all minor.

| Finding | Verdict | What changed |
|---|---|---|
| `airpit.modes.json` names the superseded 0.5.4 package `airpit` | Verified, **not fixable in item 4**; guarded | A fresh extraction from the item-2 0.6.0 package (agent scratchpad, fixed timestamp) differs from the tracked file only in `source_package` (`name`, `extracted_utc`); positions, bounds, markers and the capture point are identical. The accepted package `work/extract/airpit-native-v5` does not exist before item 5, and regenerating from the scratch copy would name a package that exists nowhere under `work/` and silently disable the freshness test. New `TrackedSidecarTests.test_airpit_sidecar_names_the_accepted_package_once_it_exists` skips now and **fails as soon as `airpit-native-v5` appears** until the sidecar is regenerated, with the command in the message. **Item 5 step:** `python tools/extract/extract_mode_objects.py --package work/extract/airpit-native-v5`, then commit the new `tools/maps/airpit.modes.json` (only `source_package` changes). The test logic was exercised against a fake tree (fails on the old name, passes on the new one). |
| `extract_spatial_audio.py` without `--map` rewrites the accepted Equator/Bay5 inputs | Verified, code behaviour kept; warning added | A bare run still means equator + bay5 (the parity guarantee and AGENTS.md's regeneration wording depend on it), and HEAD already behaved this way. The module docstring and the `--map` help now say that the bare form overwrites those two directories and that `--map arena_airpit` (or `--output`) is the safe form. Re-measured the difference of a fresh 0.6.0 export against the accepted inputs: exactly nine leaves per map, and the log above miscounted the `mixer_effects` ones (**six**, not five: `source_asset_sha256`, `system_raw_sha256` and the `raw_sha256` of delay, effects_sends, effects_volumes, reverb), corrected there. When AGENTS.md / `docs/dist-manifest.md` are next edited, name `--map arena_airpit` and warn against the bare invocation (outside this agent's write scope). |
| `airpit.ai.json` `_comment` says "the twelve deferred maps" | Confirmed, fixed | `extract_ai_anchors.missing_file_note(map_key)`: Equator and Bay5 keep the released wording byte-for-byte (`LEGACY_COMMENT_MAP_KEYS`), every other map gets "any map without a sidecar". `airpit.ai.json` regenerated from the live 0.6.0 install: identical to the previous file except `_comment`. Regenerated `equator.ai.json` / `bay5.ai.json` are byte-identical to the tracked files (`cmp`). Module docstring wording changed the same way (no count). |

Tests: `CommentWordingTests` (4) in `test_ai_anchors.py` pins the legacy wording, the generic wording and the tracked
comments of all three sidecars (offline); the live-source test `test_tracked_airpit_sidecar_matches_the_live_source`
already compares the full JSON including `_comment`. Full suite after the fixes: **926 tests, OK, 46 skipped** (921 / 45
before; +4 comment tests, +1 forcing test that skips until item 5). `parity_gate.py check baseline_head_2723d88.json`:
`equator-native-v5` UNCHANGED (48 lines, 1 FAIL, `677fc289136a08ea`), `bay5-native-v5` UNCHANGED (72 lines, 2 FAIL,
`7e7a7c51eb9c8b08`). `extract_equator.py`, `extract_bay5.py`, `extract_map.py` and all plans are untouched by this
follow-up (`extract_equator.py` / `extract_bay5.py` are clean against HEAD; `extract_map.py` carries item 2's change only;
`plans/` differs from HEAD only in `airpit.json`, item 1).

## Item 5 - re-extract AirPit from Arena 0.6.0 (done 2026-10-11, about 22:10-22:35 local)

Offline only: no Unity, no host, no deploy. The content delta, the native counts and the `level40` reconciliation are in
[airpit-source-060.md](airpit-source-060.md); this section keeps the commands, timings and the verification.

**Commands and results** (logs in the agent scratchpad, comparison scripts kept in ignored `work/airpit-parity/item5_*.py`).

| Step | Result |
|---|---|
| `python tools/extract/extract_map.py --plan tools/extract/plans/airpit.json --native-features --target D:/SPT41/EscapeFromTarkov_Data --output work/extract/airpit-native-v5` | exit 0, 22:11:58-22:16:22 (4 min 24 s). `DONE arena_airpit: 40719 objects, 2487 meshes, 435 materials, 805 textures, 240 warnings`, 0 errors. Objects per scene 20,252 / 3,833 / 15,349 / 638 / 647. Native: BallisticCollider 3,548 (2,856 / 512 / 28 / 94 / 58), StaticDeferredDecal 481 (423 / 0 / 4 / 0 / 54), NavMeshModifier 20 + NavMeshModifierVolume 12, CullingLightObject 533, VolumetricLight 130, LightFlicker 0, SmallPhysicsObject 0, rain cover 2 IndoorTrigger. No fixed timestamp (`extracted_utc` `2026-10-11T20:16:21Z`). |
| `python tools/extract/refresh_native_volumes.py work/extract/airpit-native-v5` | `NATIVE_VOLUMES_REFRESHED count=0` (23 Cube + 782 Tex2D, no Tex3D). Not a byte no-op at the time (it appended a trailing CRLF to `manifest.json`); fixed in the review follow-up below, see open point 1. |
| `python tools/validate/validate_extract.py --package work/extract/airpit-native-v5 --skip-reproducibility` | exit 0, 72 s, 60 output lines, no FAIL, 4 non-blocking warnings (240 manifest warnings, two `barrel_fire` LODGroup slots, 21 unreferenced meshes; all three also hold for the 0.5.4 package contents). Native contract line: `optional native feature source/clip/reference contract validates`. |
| `python tools/validate/validate_extract.py --package work/extract/airpit-native-v5 --extractor-timeout 900` (background, polled) | exit 0 (started 22:18:18, done before 22:28:49). Reproducibility run #1 and #2 exit 0 (with `--native-features --target D:/SPT41/EscapeFromTarkov_Data` derived from the manifest), "two independent fresh extractor runs ... produce a byte-identical package": 0 diffs; package-freshness check against today's source: 0 diffs. |
| `tools/maps/airpit.modes.json` regenerated (`extract_mode_objects.py --package work/extract/airpit-native-v5`) | the item 4 follow-up step. `diff` against the previous file: only `source_package.extracted_utc` and `source_package.name` (`airpit` -> `airpit-native-v5`). The forcing test `test_airpit_sidecar_names_the_accepted_package_once_it_exists` now runs and passes. |
| `python -m unittest discover -s tools/tests -p "test_*.py"` | **926 tests, OK, 44 skipped** (the count is unchanged and the skips fell from 46 to 44: the two tests gated on `airpit-native-v5`, `test_airpit_native_package_when_present` and the modes-sidecar test, now run and pass), 46 s. |
| Parity: `work/airpit-parity/parity_gate.py check baseline_head_2723d88.json` and `validate_extract.py --package work/extract/{equator,bay5}-native-v5 --skip-reproducibility` | `equator-native-v5` UNCHANGED (48 lines, 1 FAIL, `677fc289136a08ea`), `bay5-native-v5` UNCHANGED (72 lines, 2 FAIL, `7e7a7c51eb9c8b08`); the FAILs are the two documented 0.6.0 live-source drifts (preset offset 5708 != 5716; Bay5 disabled MeshRenderer 302 vs 305). No tool code was changed by this item, so this is a confirmation, not a new result. |
| Untouched | `work/extract/airpit` (read only), `extract_equator.py`, `extract_bay5.py`, the eleven other plans, `docs/PLAN-AIRPIT.md`, `INDEX.md`, `HANDOFF.md`, `AGENTS.md`, `README.md`. The only tracked-tree change is `tools/maps/airpit.modes.json` plus the two progress documents. |

**Findings (details in `airpit-source-060.md`).**

- The 0.5.4 -> 0.6.0 delta is a pure id re-keying: 4 of 5 scenes are identical; ShootOut has 214 objects that
  reference re-keyed ids, and 0 objects differ after remapping the ids by content. 55 mesh bodies (53 vanished ids plus
  two ids that now hold other geometry), 31 textures (PNG byte-identical) and 3 materials moved to new ids; 731 textures,
  377 materials and 2,432 meshes keep id and bytes. Textures +43 and materials +55 are the native decal assets.
- The `level40` gap of 8 is closed: eight world-space `ArenaMonitorHolder` canvases (`RectTransform` GameObjects under
  the `Arena_Home_theater` props), skipped by the `Transform`-only scene walker in both package versions.
- The plan's "about 155 m" for the `Bus_IndoorTrigger` mismatch is correct. The extractor log's 125.76 m is the
  largest single-axis offset (`max(abs(centre - position))`), not a distance; `raincover.json` has `bounds_center`
  (-13.0512, 1.5760, -66.5804) and `world_position` (112.7104, 1.5760, 24.3988), Euclidean (and horizontal) distance
  155.2199 m, Chebyshev 125.7616 m. The earlier "125.76 m" statements as a distance (item 2 and item 4 sections of this
  log, `airpit-source-060.md`, the `extract_raincover.py` module docstring) were wrong and are corrected in the item 5
  review follow-up; the extractor's mismatch log line now prints both values (stderr text only, no package byte changes).

**Open points.**

1. `refresh_native_volumes.py` rewrites `manifest.json` even when it finds 0 volumes: it uses `json.dumps(...) + "\n"`
   through `write_text`, so the file gains one trailing CRLF (894,426 -> 894,428 B; semantically identical, checked
   against the item 2 dry-run manifest). The validators compare manifests as JSON, so the reproducibility and freshness
   checks pass; only a byte-level hash of the manifest would see it (Equator/Bay5 manifests have no trailing newline).
   For a package without Tex3D the refresh step can be skipped; whether to make the script return early when
   `count == 0` is the lead's call (not changed here).
   **Resolved in the item 5 review follow-up below:** the script now returns early without writing when the manifest
   has no Tex3D texture, and the package manifest was restored to the fresh-extraction bytes.
2. Mesh ids are not stable across Arena versions (two ids were reused for other geometry). Any list of mesh ids taken
   from the 0.5.4 package must not be applied to the new one; key by name plus content hash instead.
3. The accepted Equator/Bay5 packages still hold 0.5.4 data and still fail the two live-source checks above; AirPit is
   the first map whose accepted package passes the validator against the installed 0.6.0 source in full.

## Item 5 review follow-up (2026-10-11, about 22:45-23:25 local)

Three review findings on item 5, all verified against the files and fixed or corrected; offline only (no Unity, no host,
no deploy, no `D:/SPT41` write).

| Finding | Verdict | Change |
|---|---|---|
| 1. `Bus_IndoorTrigger` mismatch quoted as 125.76 m | Confirmed. `extract_raincover.py` prints `max(abs(center[i] - pos[i]))`, a per-axis maximum. From `raincover.json`: `math.dist` 155.2199 m, Chebyshev 125.7616 m, horizontal 155.2199 m. The plan's "about 155 m" is the Euclidean distance and is right. | `docs/PLAN-AIRPIT.md` untouched. Reworded the four doc statements (`airpit-source-060.md` table, this log: item 2 manifest paragraph, item 4 rain-cover row, item 5 finding) and the `extract_raincover.py` module docstring. The mismatch log line now reads `disagree by 125.76m on the largest axis (155.22m apart)`; the gate (`> 1.0` on the per-axis maximum), the counter and `raincover.json` are unchanged. `test_airpit_against_the_live_install` additionally pins 125.76 / 155.22 / one mismatch / the log wording. |
| 2. Tools still pointing at the 0.5.4 package | Confirmed (3 places). | `extract_map.py` usage docstring: `--output <package-dir>` plus a sentence naming `work/extract/airpit-native-v5` as the accepted native output and the old package as never an `--output`. `extract_mode_objects.py` CLI example: `--package work/extract/airpit-native-v5`. `test_airpit_spawns.package_dir()`: no `"airpit"` fallback, returns the accepted package or `None` (the test skips; it runs now). Remaining mentions of `work/extract/airpit` are historical measurement logs or the sentences that say it is superseded. |
| 3. `refresh_native_volumes.py` rewrote `manifest.json` for 0 Tex3D | Confirmed (builder open point 1). One correction to the finding's fix note: Equator and Bay5 do each hold one Tex3D (`Counter` of manifest texture dimensions: Equator Tex2D 738 / Cube 18 / Tex3D 1, Bay5 986 / 21 / 1), so they take the unchanged refresh path; only a package with no Tex3D takes the new early return. | `refresh()` returns 0 before reading the source or writing anything when no Tex3D is wanted, prints `NATIVE_VOLUMES_REFRESHED count=0 package=... (no Tex3D textures, manifest untouched)`. New `tools/tests/test_refresh_native_volumes.py` (3 cases): zero-Tex3D manifest bytes identical after the call; empty textures likewise; a Tex3D entry still fails closed with `Missing source volumes` and leaves the manifest unchanged. The ignored package manifest was restored to the fresh-extraction bytes (below). |

**Byte-level proof for finding 3.** A fresh extraction with the original command plus `--fixed-timestamp 2026-10-11T20:16:21Z`
(the package's `extracted_utc`) into the agent scratchpad finished exit 0 (`DONE arena_airpit: 40719 objects, 2487 meshes,
435 materials, 805 textures, 240 warnings`). `filecmp.cmp(shallow=False)` over all 3,735 files of both trees: exactly one
difference, `manifest.json` (894,428 vs 894,426 B), and `accepted[:-2] == fresh` with the accepted file ending in `
`. The
accepted manifest was rewritten with the fresh bytes; the same comparison now reports **0 differences in 3,735 files**.
`validate_extract.py --package work/extract/airpit-native-v5 --skip-reproducibility` after the restore: exit 0, no FAIL, the same
4 non-blocking warnings. (The earlier full reproducibility/freshness result still applies: nothing but the trailing CRLF changed.)

**Parity and tests after the follow-up.**

- `python -m unittest discover -s tools/tests -p "test_*.py"`: **929 tests, OK, 44 skipped** (926 + 3 new, 42.6 s).
- `parity_gate.py check baseline_head_2723d88.json`: `equator-native-v5` UNCHANGED (48 lines, 1 FAIL, `677fc289136a08ea`),
  `bay5-native-v5` UNCHANGED (72 lines, 2 FAIL, `7e7a7c51eb9c8b08`); the FAILs are the two documented 0.6.0 live-source drifts.
- `extract_raincover.export` re-run for `arena_equator` and `arena_bay5` against the live install: `raincover.json`
  byte-identical to the accepted packages' copies (Equator 1 trigger / 0 mismatches, Bay5 30 / 1; the Bay5 line now reads
  `disagree by 65.51m on the largest axis (65.60m apart)`).
- `extract_equator.py`, `extract_bay5.py` clean against HEAD; `tools/extract/plans/` differs from HEAD only in `airpit.json`.

**Not changed, for the lead.** `tools/build_remaining.ps1` derives the package as `work/extract/$stem` and still lists `airpit`
among its twelve maps, so `build_remaining.ps1 -Maps airpit -Rebuild` would rebuild the stale 0.5.4 package. It is outside the
three findings and the plan builds AirPit with an explicit `-PackageDir work/extract/airpit-native-v5` in item 12 (never with this
script), but either that item or a guard in the script should settle it before the first AirPit Unity build.

## Item 8 - round-start barriers, bake bounds, bake-only exclusions, bake layers (done 2026-10-11, about 23:10-23:55 local)

Offline only: no Unity, no host, no deploy, no write outside the repository and the agent scratchpad. The Unity bake
(U1) is the lead's next step; everything below that touches Unity-only API (`JsonUtility` array fields, `Bounds.SetMinMax`,
the `Bake()` wiring) is **unverified until that run**. The pure helpers are executed from the production text, see
"Equator and Bay5 stay unchanged".

### What changed

| File | Change |
|---|---|
| `tools/maps/airpit.json` | `opened_barriers` `["BUS_DOORS", "PASSPORT_CONTROL_DOORS"]`, `navmesh_bake_exclusions` (the four cutters), `bake_bounds` `[-80, 56, -2, 8, -96, 92]`, `navmesh_bake_layers` `[9, 11, 13, 18, 30]` |
| `ArenaMapPort.Shared/ArenaMapDescriptor.cs` | `AirPit.OpenedBarriers` plus three new optional properties `NavMeshBakeExclusions`, `BakeBounds`, `NavMeshBakeLayers` (empty for every other map); same values, same order as the sidecar |
| `UnityProject/.../Editor/Common/ArenaPaths.cs` | `ArenaMapDescriptorData.navmesh_bake_exclusions` (`string[]`), `bake_bounds` (`float[]`), `navmesh_bake_layers` (`int[]`); `EquatorDefault()` untouched (null = absent) |
| `UnityProject/.../Editor/NavMesh/BakeNavMesh.cs` | `ResolveOpenedBarriers` became `ResolveHierarchyPaths(roots, paths, out unresolved)` (same body, now shared); new pure helpers `BakeOnlyExclusionReason`, `SourceLayerMask`, `UsesExplicitBakeBounds`; collider loop, markup loop and volume loop apply the exclusion; `Bake()` takes the bounds box and the layer mask from the descriptor when present; new log line `bake inputs: bounds ..., layer mask 0x..., N navmesh_bake_exclusions, M opened_barriers` |
| `tools/validate/validate_map_descriptors.py` | `bake_input_errors` (shape of the four keys, exclusion not inside a barrier) and sidecar-vs-`ArenaMapRegistry` drift checks for all four (build_all runs this validator, so a mismatch stops a build) |
| `tools/validate/port_colliders.py` | `mark_bake_exclusions`, `layers_to_mask`, reason `navmesh_bake_exclusions`, `ColliderSet.unresolved_exclusions` |
| `tools/validate/navmesh_diff.py` | `analyse(..., bake_exclusions, port_layer_mask)`; CLI `--bake-exclusion` (repeatable), `--port-layer-mask`, `--ignore-descriptor-layers`; `--descriptor` now applies `opened_barriers`, `navmesh_bake_exclusions` and `navmesh_bake_layers`; new `reachability` block (probes in the anchor's component) in the report and text |
| `ArenaMapPort.Server.Tests/MapDescriptorConsistencyTests.cs` | the barrier test is relaxed (root paths allowed; renamed `OpenedBarriers_WalkedMapsNamePaths_OtherMapsNone`, AirPit pinned) and `NavMeshBakeInputs_ExistForAirPitOnly` is new |
| `ArenaMapPort.Server.Tests/AirPitMapTests.cs` | `OpenedBarriersAndBakeInputsMatchTheSidecar` (sidecar == descriptor, Equator/Bay5 sidecars free of the new keys) |
| `tools/tests/test_airpit_bake_inputs.py` (new, 16 tests) | validator shape and drift, sidecar facts, and (gated on `work/extract/airpit-native-v5`) path resolution, barrier-root shape, layer census |
| `tools/tests/test_navmesh_diff.py` (+11), `tools/tests/test_navmesh_source_layers.py` (+1, cases 53 -> 175) | model and C#-helper coverage, see below |
| `tools/build_remaining.ps1` | `airpit` removed from the default list and from `$allMaps`; `-Maps airpit` is refused with the reason; help text and example updated |
| `docs/MAP-RECIPE.md` | section 3 documents the four keys and the model command |

`tools/extract/extract_map.py` needed nothing: its docstring already names `work/extract/airpit-native-v5` and calls
`work/extract/airpit` "never an --output" (item 5 follow-up). No tracked file mentions the 0.5.4 package as a build input any more
except the historical measurement logs.

### Decisions

1. **`opened_barriers` only for the two real gate groups** (user decision 4.5, since the item 7 probe found no mode toggler).
   `BUS_DOORS` and `PASSPORT_CONTROL_DOORS` are scene roots of `Arena_Airpit_TeamFight_DesignStuff`, each with exactly one
   match. A single segment is a root path in both resolvers (`BoothDoorRepairs.FindByPath`: `rest == null` returns the root;
   `BakeNavMesh.ResolveHierarchyPaths`: same), so no resolver change was needed. Checked on the 0.6.0 package: `BUS_DOORS`
   has 6 direct children (`Apron_bus_MAZ_doors` assemblies, 223 objects, 60 live colliders: 24 on layer 18, 36 on layer 12),
   `PASSPORT_CONTROL_DOORS` has 18 (the `GREEN` and `RED` fog sheets plus 16 `Passport_control_01_Door`, 229 objects, 32 live
   colliders: 16 + 16), no collider on either root object, **no object whose name contains "frame" anywhere below**. That
   matters because the two consumers use different frame rules: the client deactivates the DIRECT children whose own name
   lacks "frame", the bake drops every descendant with no frame-named object on its chain. With no frame name they agree
   exactly (0 descendants disagree); `test_barrier_roots_are_scene_roots_without_frame_parts` pins it. 92 colliders leave the
   bake (60 + 32), the same 92 as in item 7. Side effect already known: the client also deactivates the `GREEN`/`RED` fog
   sheets (item 11).
2. **Bake-only exclusion for the four route cutters, visuals kept.** `navmesh_bake_exclusions` drops the whole subtree of each
   listed object, the object itself included, from collider sources AND from `NavMeshModifier`/`NavMeshModifierVolume`
   markups (symmetric with `RoundOpenedReason`: a Not-Walkable markup under an excluded prop would cut the route the exclusion
   exists to keep; the four have no markup, pinned by a test). Nothing is deactivated at runtime, so shop fronts, roll door and
   fence stay visible and solid for players; bots path through them like through Arena's mesh. Unlike a barrier there is no
   frame exception, because the client keeps nothing standing or opening. All four paths resolve on the 0.6.0 package to exactly
   one object each, in `Arena_AirPit_ShootOut`, at the world positions the model named ((-23.21, -44.19), (-46.24, 29.41),
   (-43.27, -43.47), (22.11, -29.85)); each carries a layer-18 collider (so Arena's layer mask still sees them) and 2-3 live
   colliders (9 in total). `SOO_LOD0` holds two siblings named `BUILDING` (52 and 8 children); `Transform.Find` takes the first,
   which holds all three BUILDING paths; the model resolves paths with the same first-sibling rule (test
   `test_exclusion_paths_survive_a_duplicated_building_sibling`). An unresolved exclusion path **fails the build**
   (`InvalidOperationException` after an Error log): a silently missing exclusion would ship a cut route. (Review fix, same
   day: an unresolved `opened_barriers` path now fails the build the same way, see "Review fixes" below; the first version
   kept the old warning for it.) The AGENTS-style rule stays in the code comment next to the resolution: anything
   the client DISABLES at runtime belongs to `RoundOpenedReason`, anything that merely must not shape the mesh belongs here.
3. **Bake bounds.** Format `[min_x, max_x, min_y, max_y, min_z, max_z]` (flat `float[6]`, same family as `fog_color`, so
   `JsonUtility`, the Shared descriptor and the validator's `new[] { ... }` parser all handle it without a nested type). The box
   is used as given (no 10 m expansion). Absent or empty: the renderer-union code runs unchanged (it moved into an `else`
   block, text-pinned). Present but malformed: `InvalidOperationException`, because falling back to the 4.3 km renderer union
   would silently bake the map the field exists to avoid. **Which clip applies today** (the plan asked to confirm):
   `BakeNavMesh.cs` has **no y clip**; the volume is the union of all non-Water renderer bounds of the non-background roots,
   expanded by 10 m. Logged bakes: Equator (`work/build/full/unity_build_all_20260927_233134.log`) Center (-0.48, 5.73, -9.55)
   Extents (59.04, 42.69, 74.29), 4,104 sources, 10,404 vertices / 4,572 triangles; Bay5 Center (24.55, 3.39, -157.84)
   Extents (170.82, 51.47, 358.07), 10,501 sources; AirPit spike Center (-64.43, 44.61, -507.17) Extents (2132.5, 125.69,
   1604.49), 5,309 sources, 29,596 / 13,586. So AGENTS.md "Bake volume is y -8..12" (and the `AiAnchorsTests` comment) describes
   where the walkable ground floors are, not an applied clip; AirPit's y -2..8 is the first real vertical limit.
4. **Layer mask `navmesh_bake_layers` = Arena's NavMeshSurface mask (9, 11, 13, 18, 30).**
   - Implementation: `SourceLayerMask(layers, waterLayer)`: no layers = `~0 & ~(1 << water)` (the replaced expression, proven equal
     for every water index -1..31 by the harness); layers = their OR, Water always removed, out-of-range entries ignored with a
     warning, an empty result throws.
   - **Triggers (13) stays in the mask AND the owner rule stays.** Arena's mask names layer 13, the port's
     `NonPhysicalLayerReason` drops non-trigger colliders on 13 (stripped spawn-marker boxes made invisible raised islands on
     Bay5) and on 29 (LevelBorder). Both stay, because the rule is a port fact (installed EFT's Triggers layer) independent of
     which layers a map collects, and because it costs AirPit nothing: the 0.6.0 package has **no live non-trigger collider on
     layer 13** (census of the 5,309 live collider owners: layer 0: 5, 12: 3,180, 18: 1,978, 22: 2, 29: 67, 30: 77; the 20
     `NavMeshModifier ignoreFromBuild` markups sit on Triggers-layer owners whose own colliders are triggers or disabled). The
     wire-markup case (a `NavMeshModifier` on a Triggers-layer owner) is untouched: markups never see the layer rule
     and the mask only filters colliders.
   - Why this mask for AirPit: it is what Arena's own bake collected (decoded in item 7). In the 0.16.9.5 decompile EFT's
     `LayersMaskController.PlayerCollisionTestMask` is `LowPolyCollider + DoorLowPolyCollider + Terrain + Player`, while
     `HighPolyCollider` (12) is the layer of the ballistic raycast masks (`HighPolyWithTerrainMask`, `GrenadeObstaclesColliderMask`).
     So the 3,180 layer-12 colliders most likely add obstacles that players do not physically meet (inference: I did not read the
     project's physics collision matrix). Item 7 found 22 of the 190 walked-through instances
     exist only on layers 12/0.
   - **Cost found by the model:** one group of 193 - 98 = 95 more Arena-walkable samples (3.8 m2) loses its port floor, at
     x -7.55..-6.55, z -30.15..-25.55, y 0.64..2.71: the **`Passenger_ladder_02` airstairs** (the detailed treads are layer-12
     ballistic meshes, about 2,400 triangles; the layer-18 `_COLLIDER` has 29 triangles and the model finds no floor from it
     in that group). The bake may or may not walk it; check the built NavMesh there (`--compare-bundle`). If it matters, the switch is
     one deletion: remove `navmesh_bake_layers` from `tools/maps/airpit.json` and `NavMeshBakeLayers` from the descriptor (the
     validator keeps both in step), then every other result of this item still holds (S1 below).
5. **Why the Shared descriptor carries Editor-only data.** Nothing in the client reads the three bake keys; they are mirrored
   because the one per-map table should stay complete and because the validator and `AirPitMapTests` then catch a sidecar edit
   that forgets its twin. No runtime cost beyond three empty arrays.

### Offline model on the 0.6.0 package (`work/extract/airpit-native-v5`)

Probe points: `airpit.bots.json` (18) + `airpit.exits.json` (2) + the two capture points of the item 7 draft (scratch JSON) + 19
live AI core points + 3 hubs + 35 patrol points = 79 given, **78 located** (`capture_base` has no lattice sample, as in item 7).
Anchor = `capture_teamfight`. Commands (scratch wrapper `run_model.sh <label> <extra args>`; all with
`--package work/extract/airpit-native-v5 --points tools/maps/airpit.bots.json --points tools/maps/airpit.exits.json
--points <captures.json> --live-core-points --json-out <label>.json`):

| Run | Extra arguments | Bake sources | Port components (largest) | Probes in the anchor's component | Walked-through instances | No-floor samples | Time |
|---|---|---:|---|---|---:|---:|---:|
| S0 barriers only | `--opened-barrier BUS_DOORS --opened-barrier PASSPORT_CONTROL_DOORS` | 5,148 | 50 (3,965.7 m2) | **9 of 78** in the anchor's component #2 (52 sit in #0, 11 in #1, 2+2+2 in #3-#5); the greedy pass then joins 61 / 74 / 76 / 78 with the four cutters | 190 (426 colliders) | 98 | 82 s |
| S1 final lists, all layers | S0 + `--bake-exclusion` x4 | 5,139 | 45 (**7,924.6 m2**, 97.0 % of Arena's 8,168.2) | **78 of 78 (component 0)** | 186 (417) | 98 | 77 s |
| S2 final lists + Arena's layers | S1 + `--port-layer-mask 0x40042A00` | 2,011 | 36 (**7,957.1 m2**, 97.4 %) | **78 of 78 (component 0)** | 164 (199) | 193 | 34 s |
| S3 `--descriptor tools/maps/airpit.json` | (barriers, exclusions and layers from the sidecar) | 2,011 | identical to S2 line for line | 78 of 78 | 164 | 193 | 25 s |

- S0 reproduces item 7's scenario B on the new package, and the tool's own greedy pass proposes exactly the four paths of the
  sidecar in the same order with the same gains (12, 313, 64, 307 samples; probes joined 61, 74, 76, 78). S1 equals item 7's
  scenario C (45 components, 7,924.6 m2, 186 instances / 417 colliders left over).
- **Rank changes of the `Fence_Armored_*` / tarpaulin props: none.** A second S0 run on the superseded 0.5.4 package
  (`PKG=work/extract/airpit`, 74 s) gives the identical report: 190 instances, 426 colliders, 4,795 blocked samples, 50
  components (3,965.7 / 1,650.6 / 1,544.5 m2), the same ranks and areas to 0.01 m2 for every instance (e.g. `tarpaulin_wall_03` #14
  3.80 m2, `Fence_Armored_2 (1)` #32 2.08 m2, `Fence_Armored_2` #36 1.92 m2, `tarpaulin_wall_03 (1)` #42 1.72 m2, `(2)` #45 1.52 m2,
  `Fence_Armored_1 (1)` #109 0.52 m2), no instance present in only one run. That is consistent with the source audit (the delta
  is a pure id re-keying).
- The 186 instances (S1) that stay walked-through cut nothing between probe points: `Airbus_wing_L/R`, `Flowerbed_steel_01`,
  `Passenger_ladder_02`, `Airport_Shop_BS_closed_closed`, `TH_Roll_door_02_closed`, the sandbag families and so on. The
  plan's fallback candidates (`TH_Roll_door_02/03`, `Airport_Shop_BS_closed`, capture-zone fences, portal fences) are **not** needed
  for the 78 probes; they remain a gameplay question for the host (bots path around them, like players).
- The port model's 36 / 45 components are all inside Arena's footprint: the small ones are sub-10 m2 pockets (7.6, 3.3, 1.7 ... m2);
  every probe is in component 0. The model is a lower bound (see item 7); the real Unity bake remains the verdict.

### Equator and Bay5 stay unchanged

- Their sidecars carry none of the new keys and their descriptors none of the new arguments (`AirPitMapTests`,
  `MapDescriptorConsistencyTests.NavMeshBakeInputs_ExistForAirPitOnly`, `test_airpit_bake_inputs.test_released_maps_keep_their_bake_unchanged`,
  `test_no_other_sidecar_uses_the_new_keys`). Their `opened_barriers` are untouched and still resolve through the same function body.
- `test_navmesh_source_layers.py` now compiles and runs the production text of `BakeOnlyExclusionReason`, `SourceLayerMask` and
  `UsesExplicitBakeBounds` next to the old functions (175 cases): with an empty exclusion list the composed collider-source
  expression equals the pre-item-8 `NonPhysicalLayerReason ?? RoundOpenedReason` for all 32 owner layers; `SourceLayerMask(null)` and
  `SourceLayerMask(empty)` equal the replaced expression for water index -1..31; `UsesExplicitBakeBounds(null | empty)` is false. A
  text test pins how `Bake()` wires them (the three-way `??` chain in the collider loop, the markup loop, the volume loop, the
  resolver calls, and that the renderer-union branch with `Expand(10f)` and the 200 x 50 x 200 fallback box is still there once).
  What is not proven offline is Unity itself: a rebuild of Equator's NavMesh was not run (their bundles are not rebuilt).
- `dist/`: 46 of 48 files identical to the 0.1.0 manifest; the two that differ are the client and the server DLL (the required
  `dotnet build`/`dotnet test` stage them). No bundle, `base.json`, sidecar, acoustics or description file changed.

### Expected U1 log lines (for the lead's AirPit build)

`[BakeNavMesh] navmesh_bake_exclusions: 4 object(s) resolved ...`; `excluded 1 ... navmesh_bake_exclusions:<name>` once per cutter
and `excluded 24 ... opened_barriers:BUS_DOORS` / `excluded 16 ... opened_barriers:PASSPORT_CONTROL_DOORS` (under the mask Unity only
sees the layer-18 colliders of these objects; the 9 / 60 / 32 of the model table above are counted over all layers); `bake inputs: bounds from the descriptor (bake_bounds), layer mask 0x40042A00
(navmesh_bake_layers), 4 navmesh_bake_exclusions, 2 opened_barriers`; `bounds=Center: (-12.00, 3.00, -2.00), Extents: (68.00, 5.00,
94.00)`; `sources=` about 2,011 plus the volume sources (the collider part equals the model's 2,011 if Unity's `CollectSources`
counts as it did for the spike, 5,309 = 5,309). The layer-29 and `SPAWN_BOUND` colliders never reach the loop under the mask, so
their "excluded" lines are absent; that is not a loss. No `opened_barriers path not found` line may appear (since the review fix it is an Error that stops the build). Afterwards:
`python tools/validate/navmesh_diff.py --package work/extract/airpit-native-v5 --descriptor tools/maps/airpit.json --points
tools/maps/airpit.bots.json --points tools/maps/airpit.exits.json --live-core-points --compare-bundle
work/build/airpit/arena_airpit_navmesh.bundle` gives the real coverage and the probe components in the built mesh.

### Verification

| Check | Result |
|---|---|
| `dotnet build ArenaMapPort.Client/ArenaMapPort.Client.csproj -c Release -p:ArenaMapPortDllOnly=true` | 0 warnings, 0 errors |
| `dotnet build ArenaMapPort.Server/ArenaMapPort.Server.csproj -c Release` | 0 warnings, 0 errors |
| `dotnet test ArenaMapPort.Server.Tests/ArenaMapPort.Server.Tests.csproj -c Release` | **632 passed**, 0 failed, 0 skipped (630 + 2: `NavMeshBakeInputs_ExistForAirPitOnly`, `OpenedBarriersAndBakeInputsMatchTheSidecar`; the relaxed barrier test was renamed, not added) |
| `python -m unittest discover -s tools/tests -p "test_*.py"` | **957 tests, OK, 44 skipped** (929 + 28: 16 new file, +11 `test_navmesh_diff`, +1 `test_navmesh_source_layers`), 40 s |
| `python tools/validate/validate_map_descriptors.py` | OK (14 descriptors, 14 catalog entries, release `All` membership, `EquatorDefault()`), now including the four new keys |
| `pwsh -NoProfile -File tools/tests/test_unity_log_scan.ps1` | `UNITY_LOG_TESTS_OK 8 cases` |
| `pwsh -NoProfile -File tools/build_remaining.ps1 -Maps airpit` / `-Maps nosuchmap` | both throw before any work (the first with the new message); the default map list was not run (it would start Unity builds) |
| `work/airpit-parity/dist_vs_manifest.py` | 46 of 48 identical, the two DLLs differ (see above) |

### Open points and questions for the lead

1. **Layer mask on or off for U1?** On is implemented and set (as asked). The model sees one real cost, the 3.8 m2 airstairs group
   at x about -7, z about -28; the gain is about 14 % fewer blocked samples (port agent 5,505 -> 4,760), 9 fewer components
   (45 -> 36) and a bake with 2,011 instead of 5,139 sources. If the built mesh lacks the airstairs, delete the key from sidecar and descriptor.
2. ~~**Unresolved `opened_barriers` stays a warning** (Equator/Bay5 behaviour must not change).~~ Resolved by the review fix: it is
   an error now, see "Review fixes" below.
3. **AGENTS.md trap "Bake volume is y -8..12"** is not what the code does (no y clip; renderer union + 10 m). Not edited (not in
   my remit); worth a one-line correction after U1.
4. **dist/ DLLs** now differ from the 0.1.0 manifest (client and server), which the required builds cause. `restore_server_dll.py`
   restores the server one; the client DLL has no restore helper.
5. The plan's 78 probes need the two capture points, which no sidecar holds; they were fed from a scratch JSON (positions from item 7).
   Whether `airpit.bots.json` should carry them (the host teleport test of item 12 reads them) is the lead's call.
6. `tools/maps/AdditionalMapBuilds.props` and `docs/dist-manifest.md` still describe twelve additional maps; item 9 touches both.

### Review fixes (item 8, 2026-10-11, after the independent review)

Two findings, both verified before any change.

1. **Doc: S0 reachability cell (confirmed, fixed).** The "Probes in the anchor's component" cell of the S0 row mixed two numbers:
   the greedy progression and the new reachability headline. A fresh S0 run (`navmesh_diff.py --package work/extract/airpit-native-v5
   --points tools/maps/airpit.bots.json --points tools/maps/airpit.exits.json --points <captures.json> --live-core-points
   --opened-barrier BUS_DOORS --opened-barrier PASSPORT_CONTROL_DOORS`, scratch `fix8/s0.txt`) prints `probe reachability: 9 of 78
   located probes (79 given) in the anchor's port-model component 2; per component {0: 52, 1: 11, 2: 9, 3: 2, 4: 2, 5: 2}` and then the
   greedy lines 61 / 74 / 76 / 78 (identical to the builder's `s0_barriers_only.txt`). The anchor (`capture_teamfight`) sits in a small
   component, not in the largest one; with barriers only 52 of 78 probes share component 0. The S0 cell now says exactly that. S1, S2
   and S3 were re-read from their saved reports (78 of 78 in component 0 each) and were right. The item 7 table (scenario B, "61 after
   step 1") is a greedy count by its own wording and stays as logged.
2. **Code: unresolved `opened_barriers` fails the build (confirmed, fixed).** The review's evidence holds, so the stated reason for
   keeping the warning did not: no accepted Equator or Bay5 build log contains `opened_barriers path not found`
   (`grep -c` over every `.log` in `work/build/full` and `work/build/bay5`: no file with a non-zero count), and both logs show their
   barriers resolving (`excluded 2 ... opened_barriers:equator_roller_shutters_02 (9)`; Bay5 `Gate_Door_01 (3)`, `(4)`,
   `Gates_Depot_Frame`). Offline cross-check with the model's resolver (`port_colliders.resolve_barriers`, scratch
   `fix8/resolve_released.py`) on the accepted packages: Equator v5 1 of 1 path resolved, Bay5 v5 3 of 3, AirPit 0.6.0 2 of 2, no
   unresolved path. A scan of `tools/maps/*.json` shows only these three sidecars list `opened_barriers`; the parked maps list none.
   An error therefore changes no successful build of any map; it changes only the failure case, which is exactly the one that must
   not be silent (a missing `BUS_DOORS` bakes the bus doors as walls and re-seals the south spawn pocket while the client opens them).
   `BakeNavMesh.cs`: new `FailOnUnresolvedPaths(descriptorKey, unresolved)` logs one Error per missing path and throws
   `InvalidOperationException` (`... N opened_barriers path(s) not found in the content scenes`); both resolver calls use it, so
   barriers and exclusions now behave identically, with the exclusion message text unchanged apart from the shared template. The
   text test `test_bake_composes_the_item8_inputs_in_the_documented_order` pins both calls, the single definition, the throw, and that
   the old `LogWarning` is gone. Unity-only API is unchanged (`Debug.LogError`, `InvalidOperationException` were already used in the
   same block), but like the rest of this item it has not run in Unity. Also corrected: the stale name `ResolveOpenedBarriers` in the
   `resolve_barriers` docstring (`port_colliders.py`), and the three statements of this section that said the barrier stays a warning
   (decision 2, expected U1 log lines, open point 2). Builder question 2 is answered: error, for every map.

## Item 9 + item 23 (identity and art) - lock-step registry promotion (done 2026-10-12, about 23:40-00:20 local)

Offline only: no Unity, no host, no deploy, nothing written outside the repository, ignored `work/`/`dist/` and the agent
scratchpad. AirPit is now a released map in every list that names the release. The first host raid therefore shows "Arena: Air
Pit" with its own banner, description, menu slot and loading art. The bundles it stages are still the stale 2026-09-15 spike
bundles (see "Stale bundles" below).

### What changed

| File | Change |
|---|---|
| `ArenaMapPort.Shared/ArenaMapDescriptor.cs` | `All = new[] { Equator, Bay5, AirPit }` (shape kept for the validator regex); AirPit `LocaleName` "Arena: Air Pit", `LocaleDescription` rewritten, `IconX/IconY` 500/300 |
| `tools/maps/airpit.json`, `ArenaMapPort.Server/db/arena_airpit/base.json` | same name, description and slot; base.json regenerated with `python tools/server/make_base_json.py --map tools/maps/airpit.json`, diff against the previous file is exactly `Name`, `IconX`, `IconY` (99 keys, 18 spawn points, 2 exits, 5 waves, 4 boss waves, `BotMax 8`, `Banners []`, `Loot []`, `DisabledForScav true` unchanged) |
| `tools/validate/validate_map_descriptors.py` | `RELEASE_MAP_NAMES` / `RELEASE_MAP_KEYS` carry AirPit |
| `tools/park-unreleased-dist.ps1` | `airpit` dropped from the park list: 11 stems (55 rows) |
| `tools/validate/validate_distribution.py` | key set (via `RELEASE_MAP_KEYS`) = 3 maps; the overview-pack and flare loops are now the named constants `OVERVIEW_MAP_NAMES` / `FLARE_MAP_KEYS`, both still two-map with a comment why; new `validate_art`: exact set of banner files (server mod `banners/`) and loadscreen files (client `loadscreens/`) for the released maps, each non-empty with a JPEG start marker, and the two extractor tables must list exactly the released keys (plan item 23 last bullet) |
| `ArenaMapPort.Client/ArenaMapPort.Client.csproj` | `ArenaMapPortStageWpOutputs`: `ArenaMapPortAirPitBuildDir` = `work/build/airpit`; scenes + navmesh from it, `work/build/preset/arena_airpit_preset.bundle`, `material-shaders.json` staged as `material-shaders.arena_airpit.json`; one `<Error>` row for the bundles (message names all three maps) and a separate AirPit shader-sidecar `<Error>` row, like Bay5; staging message added |
| `ArenaMapPort.Server/ArenaMapPort.Server.csproj` | `Content` and `DeployToDist` rows for `db/arena_airpit/base.json` |
| `ArenaMapPort.Client/MapConfigVisibility.cs` | `IsPermanentHidden`: a Catalog section is hidden only when the map is not in `ArenaMapRegistry.All` (was the literal `arena_equator`/`arena_bay5` pair) |
| `ArenaMapPort.Server/ArenaMapBanners.cs` | row `arena_airpit_banner` / stem `arena_airpit`; doc comment now "released maps" / "eleven parked" |
| `tools/extract/extract_banners.py` | `BANNERS["arena_airpit"]` = `Matchmaker_Banner_AirPit` (path_id hint 1584 from 0.6.0) |
| `tools/extract/extract_loadscreens.py` | `LOADSCREENS["arena_airpit"]`: `AllScreens_Loadingscreen-AirPit` + `Arena_MainMenu_Paralax_AirPit-1..4` (the four `-Alt-` images stay out, user decision 10) |
| `tools/extract/extract_map_descriptions.py` | `DESCRIPTION_KEYS["arena_airpit"]` = `61e800b841ee214fbf1f0e53 Description` |
| Tests | `ArenaMapBannersTests` (AirPit row, own-stem resolve, Bowl as the "no banner" fixture, parked-map list), `ArenaMapDescriptionsTests` (three-map fixture, two-map pack and one-locale-missing pack are discarded wholesale, `All` is exactly the three), `ReleaseConfigurationTests` (3 keys / 11 parked; `Lighting (arena_bay5)` and `(arena_airpit)` hidden for players and visible in developer mode, `(arena_airpit2)` and `(arena_bowl)` hidden in both), `LocationRegistrationTests` (AirPit remaps to the `factory4_day` stand-in), `AirPitMapTests.ReleasedIdentityMatchesTheSidecarAndBaseJsonAndKeepsItsOwnMenuSlot` (new: descriptor = sidecar = base.json for name/description/slot, no "spike" or "Internal test build" wording on any released map, released menu slots at least 120 units apart and AirPit inside Equator/Bay5's box, banner id), `test_park_unreleased_dist.py` (11 stems, 55 rows, AirPit files preserved, the fixtures that used AirPit use Bowl), `test_validate_distribution.py` (three-map fixture with art, AirPit accepted without overview pack or flares and rejected if either appears unlisted, missing/stray/corrupt art and mismatching extractor tables rejected), `test_banners.py`, `test_loadscreens.py` (three maps, five-image rotation, no `-Alt-`), `test_map_descriptions.py` (three keys, Arena's id is not the sidecar `id_field`, a pack without AirPit is rejected), `test_map_pipeline.py` (membership test for three released names) |

`FixedRaidTimeWeatherTests` and `UsesServerTime` are untouched (item 10). `tools/maps/AdditionalMapBuilds.props` still lists
`airpit` among the "additional" maps; nothing imports that file any more (only an archived csproj under `work/archives/`
does), so it was left alone (see open points).

### Decisions

1. **Menu slot (500, 300).** The native UI divides `IconX/IconY` by 1000 (`analysis/ref-manimal.md`, `LocationButton.Show`); Equator
   (700, 290) is "immediately south-east of Interchange", Bay5 (320, 820) on the northern coast (`ArenaPortedMapPresentation.cs` in
   ArenaMode, read-only). Vanilla icons were read from the installed SPT database (read-only): Customs (550, 460), Factory (470,
   510), Interchange (660, 360), Reserve (360, 410), Woods (460, 660), Shoreline (220, 300), Terminal (280, 160) and so on.
   Search: every integer slot of the box spanned by Equator and Bay5 (x 320..700, y 290..820), at least 120 from both, maximising
   the distance to the nearest vanilla icon. (500, 300) is 200 from Equator, 550 from Bay5 and at least 167 from every vanilla icon
   (nearest Customs 168, Interchange 171, Reserve 178); the best box point scores 177. The old spike slot (295, 760) was 65 from Bay5.
   A map-select label is the only user of the number, so the slot is cosmetic and can be moved by editing sidecar and descriptor
   (the validator and the new test keep them in step); I could not look at the real map-select screen offline.
2. **Description text (own words).** "The Air Pit airport terminal from EFT Arena: a large hall with an airliner inside, passport
   lanes to the north and a bus apron to the south. Ground floor and mezzanine are the main play space." Checked against the
   extracted banner and loading art (an airliner under the glass roof of a terminal hall) and the object names behind the
   barriers (`PASSPORT_CONTROL_DOORS` at the north, `BUS_DOORS` at the south). It is only the fallback: when the original description
   pack loads, the three BSG texts win in en/ge/ru.
3. **Original description key.** `61e800b841ee214fbf1f0e53 Description` exists in all three locale TextAssets
   (`TestBackendLocaleEn` / `Localege` / `LocaleRu`); the matching `... Name` key reads "Air Pit" in English and German (the Russian
   Name was not printed, the console codepage cannot show it; the Description decoded in all three). The extractor was re-run:
   `MAP_DESCRIPTIONS_OK 3 locales 3 maps`; texts are 83 / 119 / 116 characters (en / ge / ru), as the plan said. The Equator and Bay5
   entries are identical to the previous pack (compared string by string), so only AirPit rows are new. The BSG text is not
   quoted here or anywhere tracked.
4. **Art lookup is by unique name.** UnityPy read of the live 0.6.0.47752 install: `Matchmaker_Banner_AirPit` (resources.assets,
   path_id 1584, 769x460), `AllScreens_Loadingscreen-AirPit` (resources.assets, 2704, 1920x1080) and
   `Arena_MainMenu_Paralax_AirPit-1..4` (sharedassets1.assets, 1472 / 438 / 403 / 936, 2048x1024); each name occurs exactly once;
   the `-Alt-1..4` images exist (624 / 1642 / 1293 / 994) and are deliberately not in the table. `Popup_background_airpit` also exists
   and is not used. The path ids are 0.6.0 values recorded as hints only, the Equator/Bay5 rows keep their 0.5.4 ids.
5. **The art extractors write straight into `dist/`, so Equator and Bay5 were protected first.** Both extractors were run into a scratch
   directory, and the 3 banners and 15 loadscreen JPEGs hashed against the files already in `dist/`: all 12 Equator/Bay5 files (2 banners, 10 loadscreens) were
   byte-identical (the extraction is deterministic), only the 6 AirPit files were new. Then the default-output runs wrote
   `dist/` (`--check`: 3 banners, 15 loadscreens present); the Equator/Bay5 files are unchanged (re-hashed after).
   AirPit files: `banners/arena_airpit.jpg` 183,175 B (769x460 RGB); `loadscreens/arena_airpit_1.jpg` 804,292 B (1920x1080) and
   `_2.._5` 760,941 / 801,786 / 581,224 / 725,844 B (2048x1024). The banner and the first loading screen were looked at: an airliner
   in a terminal hall with "NORVAIR" signage.
6. **`validate_distribution.py` checks the art for all released maps, not only AirPit.** An exact set for AirPit alone would have been
   an odd special case; the extractor tables (`BANNERS`, `LOADSCREENS`) must list exactly `RELEASE_MAP_KEYS`, so the next
   promotion cannot ship without its art. The file format check is only the three-byte JPEG start marker, because sizes are
   already enforced at extraction time.
7. **The release-list edits are literal on purpose** (plan section 0): no `ReleasedMaps.props`, no generator. The client csproj keeps
   its explicit per-map lists so "exactly N files" stays meaningful.

### Stale bundles (read this before trusting `dist/` or a deploy)

`work/build/airpit` still holds the 2026-09-15 spike build (scenes 664,670,450 B, navmesh 460,078 B, preset 6,859 B, shader sidecar
49,576 B). The full client build (`dotnet build ... -c Release` without `DllOnly`) staged those into `dist/`, because the new rows
point at `work/build/airpit`. That was requested for the validator run, but it means **`dist/` now contains an AirPit that has
0 native features, 0.5.4 content, the old first-guess NavMesh and no barrier or bake-bounds inputs**. The U1 build must be followed
by another non-`DllOnly` client build (it overwrites these four files with `SkipUnchangedFiles=false`) before any deploy;
`validate_distribution.py` cannot tell the difference, because bundle contents are not part of its checks (the bundle roundtrip
validators of the U1 build are what covers them).

### Verification

| Check | Result |
|---|---|
| `dotnet build ArenaMapPort.Client/ArenaMapPort.Client.csproj -c Release -p:ArenaMapPortDllOnly=true` | 0 warnings, 0 errors (`PARKED_DIST_OK Client 0 files`) |
| `dotnet build ArenaMapPort.Client/ArenaMapPort.Client.csproj -c Release` (staging rows) | 0 warnings, 0 errors; staged `arena_airpit_{scenes,navmesh,preset}.bundle` and `material-shaders.arena_airpit.json` besides the Equator/Bay5 files |
| `dotnet build ArenaMapPort.Server/ArenaMapPort.Server.csproj -c Release` | 0 warnings, 0 errors (`PARKED_DIST_OK Server 0 files`); `dist/.../db/arena_airpit/base.json` and `locales/map-descriptions.json` equal their sources (sha256) |
| `dotnet test ArenaMapPort.Server.Tests/ArenaMapPort.Server.Tests.csproj -c Release` | **637 passed**, 0 failed, 0 skipped (632 before; +1 AirPit banner row, +1 AirPit resolve test, +3 description tests, +1 AirPit identity test, -1 because the "no banner" theory lost AirPit's row) |
| `python -m unittest discover -s tools/tests -p "test_*.py"` | **968 tests, OK, 44 skipped** (957 before: +11; the banner/loadscreen tests run their real-source passes against the live Arena install, including AirPit) |
| `python tools/validate/validate_map_descriptors.py` | OK (14 descriptors, 14 catalog entries, release `All` membership, `EquatorDefault()`) |
| `python tools/validate/validate_distribution.py` | **`DISTRIBUTION_OK 3 maps 9 bundles 3 sidecars`**; `validate_bundle_names` "9 bundle(s), every internal directory name is unique". It passes with the stale AirPit bundles, so it is a file-set gate, not a content gate |
| park script | `PARKED_DIST_OK Client 0 files` and `Server 0 files` during the builds (no AirPit file archived); `test_park_unreleased_dist.py`: 11 stems, 55 rows, AirPit files preserved |
| `work/airpit-parity/dist_vs_manifest.py` (dist vs the shipped 0.1.0 manifest) | 59 files: **45 identical**, 3 different (client DLL, server DLL, `locales/map-descriptions.json` which now has the third map; its Equator/Bay5 strings are unchanged), 11 new AirPit files (3 stale bundles, 1 shader sidecar, 5 loadscreens, 1 banner, `db/arena_airpit/base.json`). All six Equator/Bay5 bundles, both shader sidecars, both `base.json`, sidecars, acoustics, flares, water, loadscreens, banners and DynamicMaps files are among the 45 |
| not run | `tools/tests/test_unity_log_scan.ps1` (no editor code touched); the Equator/Bay5 `parity_gate.py` (no extractor/validator it covers was touched); no Unity, no host, no deploy |

New dist hashes (for the lead's deploy comparison): client DLL `7ed62955...8895`, server DLL `23f5e174...2663`, stale AirPit scenes
bundle `31100570...95aa`, navmesh `635762d0...9687`, preset `f1cc7743...5dde`, `material-shaders.arena_airpit.json` `6d70ad11...150d`.

### Open points and questions for the lead

1. **Stale AirPit bundles are in `dist/`** (above). Rebuild after U1 with a full client build; do not deploy before that unless the
   spike map is acceptable.
2. **Menu slot (500, 300)** is chosen offline; one look at the map-select screen on the host decides whether it is a good place.
3. **`tools/maps/AdditionalMapBuilds.props` and `docs/dist-manifest.md`** still describe twelve additional maps (item 25 covers the
   doc; the props file is imported by nothing today, so I did not touch it; deleting AirPit's eight lines there is a one-minute
   edit if you want it consistent).
4. **AI sidecar not staged yet.** `tools/maps/airpit.ai.json` exists but `ArenaMapPortStageAiSidecars` still lists only Equator and
   Bay5 (plan item 20); the same for acoustics (item 21). Neither is checked by `validate_distribution.py` for AirPit yet.
5. **A pre-existing stale-pack trap stays real for every future promotion:** `ArenaMapDescriptions.Load` discards the whole pack when
   the key set differs from `All`. The new tests pin it (`two-map-pack`), and `validate_distribution.py` fails with "Stale map
   descriptions" if `work/extract/map-descriptions` and the staged pack differ, but nothing regenerates the pack automatically.
6. AirPit now runs through every client loop over `ArenaMapRegistry.All` with neutral defaults (no `UsesServerTime`, `Donor=Off`,
   no AirPit rows in `AmbientAudioDefaults`, `NativeSpatialAudio`, `RoomReverb`, `PrecipitationAudio`). That is the state items 10,
   13 and 21 start from; I found no loop that throws for an unlisted AirPit (the C# suite constructs the config and visibility paths),
   but no client startup was executed offline.

## Item 9 review fixes (2026-10-12, about 00:20 local)

Offline only, same rules as the section above. Two review findings, both about what item 9 left misleading.

### Finding 1: `validate_distribution.py` could not tell that `dist/` carries the spike bundles (fixed)

Confirmed. The real `dist/` AirPit scenes bundle is 664,670,450 B with sha256 `31100570...95aa` and the navmesh bundle 460,078 B with
`635762d0...9687`, which are exactly the hashes in `work/build/airpit/full-map-validation.json` of the 2026-09-15 spike build, and the
gate printed `DISTRIBUTION_OK` for them.

Change: `tools/validate/validate_distribution.py` now has `STALE_SPIKE_BUNDLES` (file name -> size, sha256 for the scenes and the
navmesh bundle) and `validate_not_spike_build()`, called right after the exact bundle-set check. It compares the size first, so the
665 MB scenes bundle is hashed only when it could be the stale file (about 0.6 s on this machine, nothing otherwise), and fails with
`Stale AirPit bundle: <file> is the 2026-09-15 spike build ...` plus the remedy. The preset bundle is deliberately not listed: a correct
rebuild from unchanged sidecars may reproduce its 6,859 bytes, and listing it could fail a good rebuild. The scenes and navmesh bundles
cannot survive the Phase B Unity build unchanged (0.6.0 content, new bake inputs).

Test: `test_known_stale_airpit_spike_bundles_are_rejected_by_their_bytes` (table shape; fixtures with other bytes pass; a fixture whose
size and hash are patched into the table is rejected by name; the same name and size with one byte changed is accepted).

Consequence the lead must expect: **the real `python tools/validate/validate_distribution.py` is now red until U1 and a full client
build have replaced the spike files**:

```
DISTRIBUTION_FAILED: Stale AirPit bundle: arena_airpit_scenes.bundle is the 2026-09-15 spike build (Arena 0.5.4 content, no native features). ...
```

That is the intended state of the gate, not a regression of anything else (the check runs after the file-set checks, so every other
item still passes first; the builder's `DISTRIBUTION_OK 3 maps 9 bundles 3 sidecars` is what it prints once the bundles are fresh).
It supersedes the sentence "it passes with the stale bundles" in the section above.

### Finding 2: wording that still said "Equator and Bay5 only" / "twelve maps" (fixed, comments and text only)

Every cited place was checked and was wrong or misleading now that `ArenaMapRegistry.All` has three maps; no code path changed.

| Place | Fix |
|---|---|
| `BloomTuning.cs` | the local `releasedMap` (true only for Equator/Bay5, so AirPit, a released map, was "unreleased") is renamed `donorDefaults` and its comments say what it means: only Equator and Bay5 carry the donor defaults and the migration, every other map in the registry binds -1. The ConfigDescription texts ("Equator and Bay5 default to ...") were already true and are unchanged; so are the bound defaults and the migration. Giving AirPit the explicit defaults stays plan item 21 |
| `LoadingScreenArt.cs` | class doc: every map in `ArenaMapRegistry.All` with staged art (the loop at `:190` already iterates `All`); the 2026-09-27 verification note stays as evidence and now says AirPit's images still await a host raid |
| `Plugin.cs` | "keep F12 focused on the released maps" |
| `EnvironmentRepairs.cs` | no more "twelve maps": the fallback stays for every map whose package authors no `EnvironmentManager` (the parked maps); a package with a `raincover` entry, such as the 0.6.0 AirPit package (2 IndoorTrigger), does not need it |
| `AICorePointRepairs.cs`, `PatrolWayRepairs.cs` | "only maps with a staged AI sidecar: Equator and Bay5 today, AirPit once its sidecar is staged (item 20)" and "the behavior of every map without a sidecar" instead of "the twelve deferred maps" |
| `tools/build_remaining.ps1` | header and the `-Stage` refusal message name the released maps (Equator, Bay5, AirPit); no test pins either text |
| `tools/extract/extract_loadscreens.py` | docstring only: "both objects"/"those two objects" now describe the table (a loading screen plus four backgrounds per map), "third object" became "further object", "one file per map today" became five |

Left alone on purpose: `extract_ai_anchors.py:109-118` and `test_ai_anchors.py` keep the shipped Equator/Bay5 sidecars' own
"twelve deferred maps" comment string byte for byte (a pinned legacy value, tested); statements that are still true about two maps
("both maps" for physics props, Bay5/Equator ambience, light-culling counts); `LoadingScreenArt`'s `:79` verification date. Docs
(`docs/dist-manifest.md`, `AdditionalMapBuilds.props`) stay with item 25.

### Verification

| Check | Result |
|---|---|
| `dotnet build ArenaMapPort.Client/ArenaMapPort.Client.csproj -c Release -p:ArenaMapPortDllOnly=true` | 0 warnings, 0 errors (`PARKED_DIST_OK Client 0 files`) |
| `dotnet build ArenaMapPort.Server/ArenaMapPort.Server.csproj -c Release` | 0 warnings, 0 errors |
| `dotnet test ArenaMapPort.Server.Tests/ArenaMapPort.Server.Tests.csproj -c Release` | 637 passed, 0 failed |
| `python -m unittest discover -s tools/tests -p "test_*.py"` | 969 tests, OK, 44 skipped (968 before, +1 test above) |
| `python tools/validate/validate_map_descriptors.py` | OK (14 descriptors, 14 catalog entries, release `All` membership) |
| `python tools/validate/validate_distribution.py` | exit 1 with the stale-spike message above, as intended until U1 plus a full client build |
| `work/airpit-parity/dist_vs_manifest.py` | unchanged: 59 files, 45 identical to the 0.1.0 manifest, 3 different (client DLL, server DLL, `map-descriptions.json`), 11 new AirPit files |
| `build_remaining.ps1` parse check | 0 errors |
| not run | `test_unity_log_scan.ps1` (no editor code touched), Unity, host, deploy |

## Item 10 - runtime defaults for the first look: weather rig, sun keys, time policy, saved config (done 2026-10-12, about 00:00-00:40 local)

Offline only: no Unity, no host, no deploy; nothing written outside the repository, the ignored `dist/` (client and server DLL rebuilt by
the Release builds) and the agent scratchpad. User decisions applied (PLAN-AIRPIT 4.2, 2026-10-11): Bay5-style `UsesServerTime`, donor rig
`Reserve_Base_Scripts` (Full), `Off` stays an F12 A/B.

### What changed

| File | Change |
|---|---|
| `ArenaMapPort.Client/MapLightingDefaults.cs` (new) | Pure table of the map-dependent first-run defaults of the rig section (`Rig(mapKey)`) and the sun-key predicate (`BindsAuthoredSunKeys(map)` = `map.SunPaths.Length > 0`). No Unity/EFT/BepInEx types, so the test project links the file as is |
| `ArenaMapPort.Client/WeatherRig.cs` | `BindConfig` reads Donor, ZeroLevel, HeightFalloff, TextureBudgetTier, UseDonorCameraPrefab, ReflectionIntensity, AmbientIntensity, IndoorFloorSmoothness, IndoorCoverPercent, IndoorReflectionStrength, IndoorSurfaceRepair and ReflectiveSpecularCap from `MapLightingDefaults.Rig`; the three local booleans are gone except `equatorDefaults`, which still gates the five `DefaultsRevision` migrations (untouched). `OffDonor` is the shared constant. Comments only otherwise |
| `ArenaMapPort.Client/ArenaLightingRepairs.cs` | `if (map != ArenaMapRegistry.Bay5)` became `if (MapLightingDefaults.BindsAuthoredSunKeys(map))` before the first AirPit bind: `SunIntensityMultiplier`, `SunColorWhiteBlend` (and the `EquatorProfiles.Subscribe` calls) now exist only for Equator |
| `ArenaMapPort.Shared/ArenaMapDescriptor.cs`, `tools/maps/airpit.json` | AirPit: `UsesServerTime: true` / `"uses_server_time": true` (no fixed hours, no sun) |
| `ArenaMapPort.Client/TimeOfDayRepairs.cs` | the two hidden legacy option descriptions and the revision-2 migration log line: "Equator uses 12:00/00:00; Bay5 and AirPit use the two native server times. Has no effect on any released map (Equator, Bay5, AirPit)". Text only |
| `ArenaMapPort.Client/FixedRaidTimeSelection.cs` | class doc only ("Bay5 and AirPit ... keep the backend clock"). `AllowNight` / `FindSelectable` already derive from `HasTimeSelection` over `All`, so no code change: `NightTimeAllowedLocations` gains `arena_airpit` by itself |
| `ArenaMapPort.Client/HdrImageTuning.cs` | `Bind(Equator); Bind(Bay5);` became `foreach (var map in ArenaMapRegistry.All) Bind(map);`. AirPit gets the seven image-tuning keys with the neutral defaults Bay5 uses (Original tonemap, 0 EV, range/speed 1); doc and description text name AirPit |
| `ArenaMapPort.Server.Tests/WeatherRigDefaultsTests.cs` (new), `...Tests.csproj` | 6 tests: pinned rows for Equator and Bay5 (the values their released builds bind), AirPit's rows, neutral row for the 11 parked maps and unknown keys, "only the released maps are rig-active", sun-key predicate (only Equator), AirPit `Lighting`/`LightingLab`/`RainCoverLab` sections hidden for players and visible in developer mode |
| `FixedRaidTimeWeatherTests`, `FixedRaidTimeSelectionTests`, `MapWeatherRuntimeTests` | AirPit asserted as a server-time map (descriptor = sidecar, `HasTimeSelection`, no fixed hours, `NightTimeAllowedLocations` = bigmap, equator, bay5, airpit, native decision for `arena_airpit` CURR/PAST); the Bay5 menu-clock and backend-weather theories now run for both Bay5 and AirPit; Equator -> AirPit transition clears the fixed hour |

The pointer ":53-68" of the task needed no edit in `FixedRaidTimeWeatherTests`: that block is the `else` branch for every non-Equator map and already asserts "no
fixed hours, no sun scene or paths in descriptor and sidecar", which is exactly AirPit's state.

### Defaults, documented (`Lighting (arena_airpit)`)

| Key | Equator (unchanged) | Bay5 (unchanged) | AirPit (new) | Parked / unknown |
|---|---|---|---|---|
| Donor | Reserve_Base_Scripts | Reserve_Base_Scripts | **Reserve_Base_Scripts** | Off |
| ZeroLevelOverride | -4.4 | -8.2 | **-4.4** (rule of thumb: spawn markers at y 0.08..0.14, to be measured) | 0 |
| HeightFalloffOverride | 0.025 | 0.025 | **0.025** | 0.029 |
| TextureBudgetTier | ReserveTier | ReserveTier | **ReserveTier** | Stub |
| UseDonorCameraPrefab | true | true | **true** | false |
| IndoorFloorSmoothness | 0.5 | 0.5 | **0.5** (comes with the rig-active set, see Decisions) | 1 |
| ReflectionIntensity / AmbientIntensity | 0.85 / 0.5 | 1 / 1 | 1 / 1 | 1 / 1 |
| IndoorCoverPercent | 30 | 50 | 50 | 50 |
| IndoorReflectionStrength / ReflectiveSpecularCap | 0.95 / 0.3 | 1 / 1 | 1 / 1 | 1 / 1 |
| IndoorSurfaceRepair | false | true | false | false |
| DefaultsRevision | migrates 1..5 | 0 | 0 (no migration) | - |

Same for every map (not touched): SkyboxMode Dome, KeepFillLight false, UnityAmbient Donor, RainCoverMethod RoofDepthBlack, FullyIndoorCover false,
CoverIncludesGlass true, GroundSmoothness 1, IndoorSurfaceSmoothness 0.5, IndoorSurfaceReflectionStrength 0.2, RainBounds 0 / 1000x220x1000,
NorthDirectionOverride 0; `ArenaLightingRepairs`: Enabled true, AmbientColorMultiplier 0.5, GlobalLightIntensityMultiplier 0.5, FogDensity 0,
FillLightIntensity 0.2 (forced to 0 under a rig unless KeepFillLight), SyncCullingLightIntensity true; `BloomTuning`: BloomThreshold / BloomIntensity -1 for AirPit
(it iterates `All`; no change needed, -1 keeps the donor camera's own 2.10/0.35 under the rig); `HdrImageTuning`: neutral. AirPit binds no
`SunIntensityMultiplier`, `SunColorWhiteBlend` or `NightSunMultiplier` (no sun). The F12 sections are visible in developer mode only; the new
test covers the section-suffix rule of `MapConfigVisibility` for AirPit (the rule itself was fixed in item 9).

### Saved configuration (read-only grep, 2026-10-12 about 00:15)

- `D:/SPT41/BepInEx/config/com.maschine.ArenaMapPort.cfg` (809 B, sections `[Appearance]`, `[General]`): 0 hits for `airpit`.
- `D:/SPT41/BepInEx/config/com.maschine.ArenaMapPort.developer.cfg` (9,849 B, 668 lines, 41 sections, none for AirPit): 0 hits.
- `D:/SPT41vanilla/BepInEx/config`: no ArenaMapPort cfg at all (the folder holds BepInEx.cfg, FixPluginTypesSerialization, ConfigurationManager, Freecam,
  LogTools; the plugin folder `BepInEx/plugins/ArenaMapPort` exists, the plugin never wrote a config there).
- The only `arena_airpit` string under either install's config tree is `CombatLog/raids/index.jsonl` on D:/SPT41, and those two lines are `arena_airpit2`
  (Air Pit 2.0 raids of 2026-09-21 and 09-25), a different, parked map.

Conclusion: no stale `Donor=Off` section or any other AirPit key exists on a tester install, so the first AirPit defaults need no `DefaultsRevision`
migration, no `RetiredConfiguration` entry, and no key was renamed. A host that ran an AirPit build with AirPit in `All` and the old `Donor=Off` default (none did: neither cfg carries an AirPit
section) would have persisted `Donor = Off` and look dark; `uibridge_config` on `Lighting (arena_airpit)` / `Donor` is the first host check.

### Fog under the rig

- The bundle carries the sidecar colour: `AuthorScriptsScene.Build` writes `LevelSettings.fogColor = ArenaPaths.Map.FogColorOrDefault()` (AirPit's
  `fog_color` 0.099 / 0.116 / 0.122), `fogMode` ExponentialSquared, `fogDensity` 0, `fog` false. The U1 build uses the sidecar as is.
- `ArenaLightingRepairs.ApplyAmbient`: `ownFogColor = !WeatherRig.IsActiveForCurrentMap()`. The constant `FogColor` (0.62, 0.66, 0.70) is therefore
  written only with `Donor=Off`. Under the rig it writes mode and density only (FogDensity default 0, so `fog` stays false and the colour has no
  visible effect); the colour that reaches `RenderSettings` is the sidecar value from the single `ls.ApplySettings()` of
  `ApplyDonorLevelSettingsOverrides` (`LevelSettings.cs` ApplySettings pushes fog, fogColor, fogDensity, fogMode), and the donor's `TOD_Sky.UpdateFog` overwrites
  `RenderSettings.fogColor` every frame where its Fog mode is Color/Directional. Identical to Bay5 (sidecar 0.132/0.148/0.138) and Equator.
- Verdict: not a bug, no change. Open evidence the host answers (not AirPit specific): whether the Reserve donor's `TOD_Sky.Fog.Mode` is ever None
  (`LightingDiagnosticProbe` comment; its rig section prints `fogColor(live)` beside the constant).

### Decisions

1. **`rigDefaults` semantics, full join.** AirPit takes every rig-active default, which includes `IndoorFloorSmoothness 0.5` (the plan names only Donor,
   ZeroLevel, HeightFalloff, TextureBudgetTier and UseDonorCameraPrefab). Reason: the key's own comment defines 0.5 as the baseline for rig-active maps, and
   both released maps ship it for the same sky-mirror problem class (AirPit: 293 of 380 materials are p0 deferred shaders, a glass roof over polished floors).
   It only acts on floors the RoofDepthBlack depth photo classifies as indoor. One value to confirm on the host.
2. **A table instead of booleans.** WeatherRig and ArenaLightingRepairs cannot be compiled into the test project (EFT/Unity types), so a pin on the *bound*
   defaults needs the values outside them. `MapLightingDefaults` is that table (same pattern as `AmbientAudioDefaults`). The pinned Equator/Bay5 rows were
   derived from the HEAD expressions (`git show HEAD:...WeatherRig.cs`), not from the new code. Limit: the test pins the table, not the individual `Bind(...)` call
   sites; a future literal at a call site would escape it (review rule: map-dependent defaults go through the table).
3. **Sun keys use the descriptor only.** The plan's `SunPaths.Length > 0 || map == Equator` is redundant (Equator's `SunPaths` is `FAKE_SUN`,
   `FAKE_SUN_VOLUME`, pinned by `FixedRaidTimeWeatherTests`); the predicate is `SunPaths.Length > 0` and a test asserts that Equator is the only Catalog map for which it
   holds. Only Equator is affected in practice (`BindConfig` runs over `All`), so the 11 parked maps' sections are not changed either.
4. **Night is offered on AirPit as soon as this lands.** `HasTimeSelection` is the single switch for both whitelists (Bay5-style), so the PAST toggle
   appears; the plan's "night look-test before offering night" is therefore a host check, not a gate in code (a gate would need a new descriptor flag, which
   Bay5 does not have either). The item-10 host raid must include one PAST raid (checklist 9).
5. **HdrImageTuning loops over `All`** instead of adding a third literal, so the next promoted map needs no edit there. BloomTuning needed nothing.
6. **ZeroLevel -4.4 stays a rule of thumb** until measured; it is only a config default (live-tunable on the next raid), so a wrong value costs one raid, not a rebuild.

### Verification

| Check | Result |
|---|---|
| `dotnet build ArenaMapPort.Client/ArenaMapPort.Client.csproj -c Release -p:ArenaMapPortDllOnly=true` | 0 warnings, 0 errors (`PARKED_DIST_OK Client 0 files`) |
| `dotnet build ArenaMapPort.Server/ArenaMapPort.Server.csproj -c Release` | 0 warnings, 0 errors (`PARKED_DIST_OK Server 0 files`) |
| `dotnet test ArenaMapPort.Server.Tests/ArenaMapPort.Server.Tests.csproj -c Release` | **655 passed**, 0 failed (637 before: +6 `WeatherRigDefaultsTests`, +2 `arena_airpit` rows in `UnsupportedIdentityOrVariant`, +2 menu-clock rows, +4 + 4 runtime-weather rows) |
| `python -m unittest discover -s tools/tests -p "test_*.py"` | **969 tests, OK, 44 skipped** (unchanged: no Python file touched; the airpit.json edit is accepted by the sidecar validators) |
| `python tools/validate/validate_map_descriptors.py` | OK (14 descriptors, 14 catalog entries, release `All` membership) |
| `python tools/validate/validate_distribution.py` | exit 1 with the intended `Stale AirPit bundle: arena_airpit_scenes.bundle is the 2026-09-15 spike build` message until U1 and a full client build; nothing else is reported before it |
| not run | `test_unity_log_scan.ps1` (no editor code touched), Unity, host, deploy |

New `dist/` DLL hashes for the lead's deploy comparison: client `5ab244a3...9c30` (712,704 B), server `2855dcda...8ec3` (67,584 B). Equator and Bay5 bundles,
sidecars and base.json files were not touched.

### Host-test checklist for item 10 (SPT-GPU-2, after U1 and a full client build; DeveloperMode on, BepInEx Debug lines enabled)

1. `uibridge_status`: loaded client MVID = VM build, server DLL hash `2855dcda...8ec3` before the server restart, EFT restarted for the new bundles.
2. `uibridge_config` before any raid, section `Lighting (arena_airpit)`: Donor `Reserve_Base_Scripts`, ZeroLevelOverride -4.4, HeightFalloffOverride 0.025,
   TextureBudgetTier ReserveTier, UseDonorCameraPrefab true, IndoorFloorSmoothness 0.5, ReflectionIntensity 1, AmbientIntensity 1, IndoorCoverPercent 50,
   IndoorSurfaceRepair false, DefaultsRevision 0, BloomThreshold/BloomIntensity -1, TonemapProfile Original; **no** SunIntensityMultiplier, SunColorWhiteBlend
   or NightSunMultiplier key. Equator unchanged (ZeroLevelOverride -4.4, AmbientIntensity 0.5, ReflectionIntensity 0.85, IndoorCoverPercent 30), Bay5 unchanged
   (ZeroLevelOverride -8.2, IndoorSurfaceRepair true). A stale `Donor = Off` here explains a dark map; set it back.
3. Map-select: AirPit shows the CURR/PAST toggles with the native live clock and weather icon (not 12:00/00:00); Next/Back keeps the chosen variant.
4. Start AirPit (day, bot amount None) and look for, in order:
   - `[ArenaMapPort][TimeOfDay] arena_airpit: native server time and weather; legacy fixed-hour override bypassed, native weather initialization retained.`
   - `[ArenaMapPort][WeatherRig] arena_airpit: activated 'Reserve_Base_Scripts' (Full) for this raid. SkyboxMode=Dome.`
   - `[ArenaMapPort][LevelSettingsOverride] map=arena_airpit ZeroLevel=-4.40 (was -78.64) HeightFalloff=0.025 (was 0.029) TextureBudget=ReserveTier ...`
   - `[ArenaMapPort][CameraPrefabTakeover] assigned donor camera for map=arena_airpit camera='...'` (a `UseDonorCameraPrefab=true but the harvested rig has no donor CameraPrefab` line is the failure form)
   - `[ArenaMapPort][RainCover] map=arena_airpit RainBounds computed from N renderer(s) (excluded M oversized): center=... size=...` and the `RoofDepthBlack scan:` line.
     Expected size: the play footprint (Arena's walkable component x -56..32, z -78..85) plus 10 m padding. A size in the hundreds of metres or more means the far
     scenery that AirPit keeps in its main scene (mountains, runway; only single renderers over 600 m extent are excluded) inflated the bounds and the depth photo is
     coarse: that is a plan item 17 finding, not an item 10 failure.
   - no `[WeatherRig] ... not harvested in time`, no `activation failed`, no `LevelSettings singleton not found`.
5. The two developer-mode probes run by themselves about 5 s after the raid loads: the lighting probe (`-- weather rig (WeatherRig.cs) --` section: `active-this-raid=True`,
   the TODSkyProvider GameDateTime-set tripwire line, `LevelSettingsOverride (live): ZeroLevel=-4.40`, `fogColor(live)` beside `ArenaLightingRepairs.FogColor`; with the rig the
   live colour is not the constant) and `[ArenaMapPort][DaytimeVisualsProbe]` F1 `serverTimes=True appliesToThisMap=False`. F8 (RainCoverLab RerenderHotkey) depth readback plus a
   screenshot of falling rain is the rain-cover half (item 17 evidence).
6. Look: hall centre, north spawn, south apron: lit, not dark silhouettes; sky/horizon without a seam; floors without a white sky-mirror veil. Tune on the spot
   (all live or next-raid): ZeroLevelOverride (sky dome height; the measured value replaces the rule of thumb), ReflectionIntensity (Equator needed 0.85),
   IndoorFloorSmoothness, AmbientIntensity. Record final values; a changed default after release needs a `DefaultsRevision` (AGENTS.md).
7. A/B `Donor=Off` for the next raid: no `activated` line, the 2026-09-14 flat-ambient look (dark silhouettes) is the expected result; set it back to `Reserve_Base_Scripts`.
8. Non-regression: one Equator Day raid and one Bay5 raid show their usual `[WeatherRig]` / `[LevelSettingsOverride]` lines (`map=arena_equator ZeroLevel=-4.40`,
   `map=arena_bay5 ZeroLevel=-8.20`); sequence AirPit -> Equator -> AirPit shows the rig deactivating and re-activating without warnings.
9. One night raid (PAST) on AirPit: record which lamps are on (F7 `LightCullingProbe`), whether the hall is readable, the rain/weather node behaviour. Night is
   uncertified (509 of 813 lights are saved inactive); if it is not acceptable the options are a descriptor flag that hides the toggle or accepting it as on Bay5.

### Open points and questions for the lead

1. `IndoorFloorSmoothness 0.5` for AirPit rides on the `rigDefaults` join (decision 1). Say if AirPit should start at 1 instead.
2. The PAST toggle is offered immediately (decision 4); a "night after look-test" gate would need a new descriptor flag. Keep Bay5-style?
3. `MapLightingDefaults` pins the table, not the call sites (decision 2). If you want the call sites pinned too, the cheapest step is a source-text test over
   `WeatherRig.cs`; I did not add one.
4. `validate_map_descriptors.py` does not mirror `uses_server_time` (neither for Bay5 nor AirPit); only `FixedRaidTimeWeatherTests` does. Adding it to
   `JSON_TO_CS_FIELD` is a one-line change if you want the Python gate to cover it.
5. `BloomTuning` stays at -1 / -1 for AirPit (camera value = donor 2.10/0.35 under the rig). Giving AirPit the explicit 2.10/0.35 now (add it to `donorDefaults`) would only
   matter if the AirPit host test finds the Cam2 fallback in use; plan item 21 owns it.
6. `dist/` carries the rebuilt client and server DLLs plus the stale spike AirPit bundles (item 9 note): do not deploy before U1 and a full client build.

### Item 10 review fixes (2026-10-12)

Two review findings, both checked against the code before acting.

1. **Stale F12 text for `Weather (test)/Weather` (confirmed, fixed).** `WeatherTestOverride.ModeForSelection` returns `Server` for every map with
   `UsesServerTime` (line 70), and AirPit has the flag since item 10, so the description that said only Bay5 uses backend weather and that the key applies
   to "other experimental Arena maps" now told a developer the opposite. Fixed: the description of `WeatherTestOverride.cs` now reads "Bay5 and AirPit
   (descriptor UsesServerTime) always use dynamic SPT/backend weather ... applies to Equator night when Night neon/Rain does not request rain, and to
   unreleased Arena maps". Same omission found in the sibling `Night neon (arena_equator)/Rain` description in `EquatorNightNeon.cs` ("Bay5 always uses
   dynamic backend weather"); now "Bay5 and AirPit". Description strings only: the keys, their defaults and their types are
   untouched (and neither bind has an acceptable-value range to change), so no `DefaultsRevision` and no `RetiredConfiguration` entry. A grep for the old wording (`Bay5 always`, `dynamic SPT/backend`,
   `experimental Arena maps`) finds no other occurrence in code or docs. The client DLL changed: `dist/` client is now `d8078a33...28b6` (712,704 B, was
   `5ab244a3...9c30`); the server DLL is unchanged (`2855dcda...8ec3`).
2. **Night toggle offered before the look-test (disputed as a code defect; documented as a release gate).** Evidence for keeping the code as is:
   - PLAN-AIRPIT 4.2 option (a) says "night uncertified (as for Bay5): run a night look-test before offering it". Bay5 offers PAST today with no code gate (no
     descriptor flag for it exists), so "Bay5-style" and the look-test are a verification step, not a feature.
   - The PAST toggle is the only way to start a night AirPit raid in the ordinary menu flow (the debug buttons were retired). A code gate that hides it
     would block the very look-test it waits for, unless it also had a developer-mode override. That needs a new descriptor field, a sidecar key, a validator
     mirror and tests, and one more U1/DLL round after the look-test to flip it: scope the plan does not contain.
   - Nothing reaches players first: `dist/` still holds the 2026-09-15 spike AirPit bundles and `validate_distribution.py` refuses them (`DISTRIBUTION_FAILED:
     Stale AirPit bundle`, re-run today), so no release or deploy can happen before U1 plus the host raid whose checklist item 9 is the look-test.
   Decision recorded: **checklist item 9 is a release gate for AirPit.** AirPit is not released, and `D:\SPT41vanilla` is not given the build, until the
   PAST raid has been recorded (lamps via F7, hall readability, weather-node behaviour). If night fails, the options are (i) a descriptor flag that withholds
   `arena_airpit` from `NightTimeAllowedLocations`/`FindSelectable` (one-line effect in `AllowNight`, plus the Shared/sidecar/validator plumbing above), or (ii)
   accepting it as on Bay5. The lead can still ask for the flag now; it is then a self-contained change and does not touch Equator or Bay5.

Verification after the fixes (all run today, same commands as the item 10 table): client Release `-p:ArenaMapPortDllOnly=true` 0 warnings / 0 errors; server
Release 0 warnings / 0 errors; `dotnet test -c Release` **655 passed**, 0 failed (the test project's own nullability warnings predate the change, e.g.
`ResourcesCache.Clear = null` was already at HEAD line 174); `python -m unittest discover -s tools/tests -p "test_*.py"` **969 tests, OK, 44 skipped**;
`validate_map_descriptors.py` OK; `validate_distribution.py` exit 1 with the intended stale-spike message. `test_unity_log_scan.ps1` not run (no editor code
touched).


## Item 11 - per-map shader stand-ins for fog sheets, cloth and mountain rock (done 2026-10-12, about 00:15-01:05 local)

Runtime only, no Unity build. Equator and Bay5 bundles and behaviour are untouched; the client DLL changed. *(After the review fixes below: `ArenaMapPort.Shared` gained `ShaderFallbackNames`, so the server DLL changed too although the server never reads it, and `tools/maps/bay5.json` / `airpit.json` gained the `shader_fallback_names` key, which no bundle, Python tool or Unity importer reads.)*

### What changed

| File | Change |
|---|---|
| `ArenaMapPort.Client/ShaderStandinPolicy.cs` (new, pure) | The substitute rows, the switches and the per-map rules for the fallback table (the allow-list itself is `ArenaMapDescriptor.ShaderFallbackNames`, see "Review fixes" below). The six-row table moved here unchanged. Bay5: all six rows behind the legacy `MaterialFixes/EnableShaderFallbacks` (default off), plain swap, as shipped. AirPit: only `Custom/Billboard_FogSheet_Simple`, `Cloth/ClothShader`, `Cloth/ClothShader_backface` (double-sided substitute, see "Review fixes"), `ANGRYMESH/PBR Rocks/PBR BlendTopDetail (Legacy)`, behind its own key. Every other map (Equator, all parked): never. Also holds the AirPit texture/float fixes, the fog-sheet texture arithmetic and the unresolved-summary formatting. Keyed on `ArenaMapRegistry.Bay5.Key` / `.AirPit.Key` for the switch wiring, no literals. |
| `ArenaMapPort.Client/ShaderStandins.cs` (new) | The Unity side, AirPit only: the three new keys, capture of texture slots and fog colour before the swap, the post-swap slot copies / float override / generated fog texture, and `TryHideFogSheet` for the Hidden mode. Class doc has the usual vanilla / ported-scene / repair / host-test paragraphs. |
| `ArenaMapPort.Client/ShaderRebind.cs` | `ShaderNameFallbacks` and the `map.Key == Bay5` test replaced by `ShaderStandinPolicy.FallbacksActive` + `TryGetFallback`; capture/apply hooks around the shader swap (only when `UsesExtendedStandins`, i.e. AirPit); `TryHideFogSheet` at the top of the per-renderer loop; `ReportSummary(map)` logs the unresolved names as one Info line. `ApplyKnownValueFixes` (ground hash, Bay5 only) and `SeaPlaceholderRepairs` are not touched. |
| `ArenaMapPort.Server.Tests/ShaderStandinPolicyTests.cs` (new, 17 tests) + `.csproj` (one `Compile Include`) | Pins the per-map gating (see Verification). |

New F12 keys, all in `MaterialFixes`, all bound through `Plugin.Instance.Settings` (hidden for players, visible with DeveloperMode); AirPit has never shipped with saved keys, so no `DefaultsRevision`:

| Key | Type / default | Meaning |
|---|---|---|
| `AirPitShaderStandins` | bool, **true** | AirPit only. true = the four names use SPT substitutes; false = they stay magenta (the baseline for the A/B). |
| `AirPitFogSheets` | `StandIn` (default) / `Hidden` | AirPit only. `StandIn` = fog sheets use `Unlit/Transparent Colored` with a generated texture (needs `AirPitShaderStandins`). `Hidden` = the fog-sheet renderers are disabled at raid start (independent of the other key). Next raid. |
| `AirPitFogSheetOpacity` | float 0..4, default 1 | Multiplies the alpha of the generated fog texture. Live (SettingChanged refreshes the textures). |

The existing `EnableShaderFallbacks` key keeps its default and meaning ("applies only to arena_bay5" is still true); I did not edit its description.

### Offline evidence (read-only)

1. **SPT shader census.** `list_shaders.py` (scratchpad `airpit-b/item11/`, UnityPy over `EscapeFromTarkov_Data/StreamingAssets/Windows/shaders`, 378 MB): 555 Shader objects, 530 distinct names. All three targets exist: `Unlit/Transparent Colored` (one property `_MainTex`, default texture "black", queue Transparent, Cull Off, ZWrite Off, ZTest LEqual, Blend SrcAlpha / OneMinusSrcAlpha, Offset -1,-1, no tint property, so the colour comes from the texture times the vertex colour), `MK4/Rock` (22 properties incl. `_Aldebo`, `_Normalmap`, `_AO`, `_Color`), `p0/Cutout/Bumped Diffuse` (`_Color`, `_MainTex`, `_BumpMap`, `_Cutoff`). Arena's `Custom/Billboard_FogSheet_Simple`, `Cloth/ClothShader*` and the ANGRYMESH rock shader are not in the bundle (the only "cloth" hit is `Hidden/Amplify Motion/ClothVectors`). `Unlit/Transparent Colored` is also what Equator's and Bay5's `boot_shadow` materials already rebind to natively, so it is known to resolve in this EFT.
2. **AirPit renderer census** (`census_renderers.py` over `work/extract/airpit-native-v5`): FogSheet 127 renderers (73 in `Arena_AirPit_ShootOut_Light`, 54 in `Arena_Airpit_TeamFight_DesignStuff`); Cloth 112 + 22 backface + 6 = 140; mountain rock 10, all in `Arena_AirPit_ShootOut`. Of the 54 TeamFight sheets, 36 sit under `PASSPORT_CONTROL_DOORS` (the opened-barrier root, deactivated at raid start) and 18 under `PASSPORT_CONTROL_EMISSIVE`, so about 91 fog sheets are live in a raid. All fog-sheet objects are a `MeshFilter` + `MeshRenderer` + `LODGroup` with no children; the mesh (`5076d95c884c0d37`) is an 11 x 11 vertex plane with normals, tangents and UV0 and **no vertex colours**.
3. **Why a plain shader swap is not enough** (measured on the 0.6.0 package):
   - Fog sheets: all five materials have `_MainTex` empty (`texture_id: null`); the look is procedural. `Unlit/Transparent Colored` would sample its "black" default or an empty slot, i.e. invisible or opaque white quads depending on how Unity treats a cleared slot. `_Color` is e.g. (0.65, 0.91, 1.0, 0.949), `_OpacityIntensity` 0.1, render queue 5000 (restored from the sidecar after the swap, still sorted last, ZWrite off in the target).
   - Cloth: the albedo alpha is not coverage. Tarpaulin albedo alpha 0..232 (mean 69/255), `fence_ema_01_white_D` 0..203 (mean 39), `City_Military_Tarp_01_D` constant 31. The materials carry `_Cutoff` 0.5 / 0.67 for the original shader's separate `_CutoutMask`; through `p0/Cutout/Bumped Diffuse` that cutoff would discard every texel (invisible cloth, not magenta). The normal map lives in `_NormalMap1`.
   - Rock: `Lighthouse_Mountain` keeps base albedo (alpha = smoothness, mean 74/255) and base normal under `_BaseAlbedoASmoothness` / `_BaseNormalMap`; `MK4/Rock` reads `_Aldebo` / `_Normalmap` (default white / bump), so the rock would be white. The material's "AO + noise mask" slot holds the same texture id as its detail albedo (`Grange_detail_D`), so it is not copied to `_AO`.
4. Water-layer check for the Bay5-only sea placeholder: AirPit has 0 renderers on layer 4, so that generic repair does nothing there.

### Decisions

1. **Allow-list in a pure client class, not a descriptor field.** *(Superseded by the review fix below: the allow-list is now `ArenaMapDescriptor.ShaderFallbackNames` plus a `shader_fallback_names` sidecar key, mirrored by the validator and tests. The first-pass reasoning was that a descriptor flag + `tools/maps` key + validator mirror + mirror tests would touch the files items 8-10 were editing and the `All = new[] { ... }` regex shape.)* The test project links the policy file and pins the gating.
2. **AirPit has its own switch (default on); Bay5's legacy switch is not widened.** AirPit never consults `EnableShaderFallbacks` and Bay5 never consults the new key (`FallbacksActive`, tests).
3. **The table is unchanged.** The plan's targets are the existing rows (fog sheet -> `Unlit/Transparent Colored`, rock -> `MK4/Rock`, cloth -> `p0/Cutout/Bumped Diffuse`, the same cloth substitute Bay5 uses; the only AirPit difference, the backface cloth, is explained in "Review fixes"); the census confirms they exist. Bay5 stays a plain swap: `captured` stays null there (`UsesExtendedStandins` is AirPit only), so its code path, order of operations and logging of the swap are the same as before.
4. **Extra material work for AirPit only** (`ShaderStandins`), all captured before the swap because a property the new shader does not declare cannot be read afterwards: rock `_BaseAlbedoASmoothness -> _Aldebo`, `_BaseNormalMap -> _Normalmap` (with scale/offset); cloth `_NormalMap1 -> _BumpMap` and `_Cutoff = 0`; fog sheet: a generated 64 x 64 RGBA32 texture per material (constant `_Color.rgb`, alpha `(1 - r^2)^2` falling to 0 at the circle, peak = `_Color.a * _OpacityIntensity * 2 * AirPitFogSheetOpacity`, about 0.19 for the main sheet, 0.04 for the dark one). The peak gain 2 and the falloff are inferred (the original shader's alpha terms are not known); the opacity key is the lever for the host, live.
5. **Hidden mode** disables the `Renderer` (not the GameObject: the objects carry a `LODGroup`), only when every material of the renderer is a fog-sheet material, and skips those renderers in the rebind so they do not appear as unresolved. Fog-sheet materials are recognised by the stand-in shader name or, after a StandIn raid in the same process, by a persistent instance-id set, so StandIn <-> Hidden works in both directions without restarting EFT.
6. **Cached bundle materials outlive the raid** (the AGENTS trap), so the generated textures are not destroyed at raid end (`DontUnloadUnusedAsset`; released only when their material is gone). Consequence for the A/B: going from on back to `AirPitShaderStandins = false` needs an EFT restart; the other direction and the fog mode switch do not.
7. **Logging.** `[ArenaMapPort][RebindShaders]` now emits ONE Info line per raid for the known gaps that stayed unresolved, e.g. `arena_airpit: 4 shader name(s) without an SPT shader keep the flat stand-in: ANGRYMESH/..., Cloth/ClothShader, Cloth/ClothShader_backface, Custom/Billboard_FogSheet_Simple`; the per-name text moved to Debug. *(Scope narrowed by the review fix below: only AirPit's four names while `AirPitShaderStandins` is false use Info + Debug. Equator, Bay5, parked maps, AirPit with the switch on and every other name keep the Error line exactly as at HEAD, so no log level changes on Equator or Bay5.)* The per-material application line (slots copied, overrides, fog peak alpha) is Debug.
8. `StandinShaderGenerator` still emits the magenta fragment; nothing in Unity or `tools/` changed.

### Verification (2026-10-12, all run after the last edit)

| Command | Result |
|---|---|
| `dotnet build ArenaMapPort.Client/ArenaMapPort.Client.csproj -c Release -p:ArenaMapPortDllOnly=true` | 0 warnings, 0 errors; client DLL `0c0203fbf094fd2755b529d4eb7bdc9ffeaf5d2ecedc9eae7f3b7f40f907b8b5` (722,944 B), bin == dist |
| `dotnet build ArenaMapPort.Server/ArenaMapPort.Server.csproj -c Release` | 0 warnings, 0 errors |
| `dotnet test ArenaMapPort.Server.Tests/ArenaMapPort.Server.Tests.csproj -c Release` | **672 passed**, 0 failed (655 after the item 10 fixes + the 17 new tests); no warning comes from the new files |
| `python -m unittest discover -s tools/tests -p "test_*.py"` | 969 tests, OK, 44 skipped (unchanged by this item) |
| `python tools/validate/validate_map_descriptors.py` | OK (nothing in Shared or the sidecars changed) |
| `python tools/validate/validate_distribution.py` | the intended `DISTRIBUTION_FAILED: Stale AirPit bundle` (U1 not built yet), same as item 10 |
| `test_unity_log_scan.ps1` | not run (no editor code touched) |

The 17 tests in `ShaderStandinPolicyTests` pin: only Bay5 and AirPit use the table at all (Equator and every parked Catalog map do not, nor does an empty or null key); Bay5 follows only the legacy switch and AirPit only its own; no other map is active for any switch combination; Bay5 keeps the six shipped rows exactly (and SpeedTree8 has none); AirPit may use exactly its four names with the Bay5 targets and gets nothing for the other two rows; the extended handling (`FixFor`, `UsesExtendedStandins`) is AirPit only and empty for Bay5 / Equator / parked; the rock and cloth fixes (slots, `_Cutoff = 0`); Hidden applies only to AirPit; the defaults (on / StandIn / 1.0); the fog peak-alpha arithmetic and clamps; the generated pixels (constant colour, soft symmetric disc, zero at corners and rim, zero for a zero peak); the known-gap rule and the one-line summary format.

### Host-test checklist for item 11 (SPT-GPU-2, after U1 and a full client build; DeveloperMode on, BepInEx Debug lines enabled)

1. Raid A, defaults (`AirPitShaderStandins` true, `AirPitFogSheets` StandIn, opacity 1). Expect Debug `[RebindShaders] Nx` lines for the four names (5 fog-sheet, 4 + 1 cloth, 1 rock materials on the 0.6.0 package), the per-material `... slot(s) copied ... fog texture colour=... peak alpha=...` lines, and **no** `without an SPT shader` Info line and no Error `unresolved:` line (all four resolved; a name that only resolved on the 3 s sweep is no longer reported). Screenshots: an airport-lamp area, the horizon with the mountains, a tarp / flag wall / ramen sign, a white cloth fence; F10 on a mountain, a fog sheet and a `Facade_FlagWall_*` object to confirm the shader names (`MK4/Rock`, `Unlit/Transparent Colored`, `Legacy Shaders/Transparent/Cutout/Bumped Diffuse 2 side` for the backface cloth). **View the flag wall from behind**: it must draw from both sides; if the 2-side shader looks wrong, the one-line alternative is the `AirPitTargets` row in `ShaderStandinPolicy` (back to `p0/Cutout/Bumped Diffuse`, single-sided).
2. Fog judgement: too bright or too flat? Move `AirPitFogSheetOpacity` live (0.25 / 0.5 / 2); the Debug line `fog-sheet opacity ... refreshed` confirms the refresh.
3. Hidden A/B: set `AirPitFogSheets = Hidden`, next raid (no restart): Debug `AirPitFogSheets=Hidden, N fog-sheet renderer(s) disabled this raid` (expect up to 127: every fog-sheet renderer whose own flag was on, including the 36 under the deactivated `PASSPORT_CONTROL_DOORS` group; about 91 of them are live), lamp areas without sheets.
4. Baseline for the screenshots "fallback off": restart EFT, set `AirPitShaderStandins = false` (and `AirPitFogSheets = StandIn`): the horizon and lamps are magenta again and the Info line lists the four names.
5. Equator and Bay5 quick raid for non-regression (their cloth / fog sheets stay as before: Equator magenta, Bay5 per its legacy switch).
6. Done when no magenta remains in the three screenshots and the policy is chosen; record it here and keep the losing mode as an F12 option.

### Open points and questions for the lead

1. **Substitutes remain inferred.** I could verify only that the shaders exist and which properties they read, not how they look. `MK4/Rock` may need more than albedo + normal (its top/detail masks are unused); `p0/Reflective/Bumped Specular SMap` (the workhorse) is the fallback candidate for the rock if the host dislikes it, a one-line change in the table for AirPit.
2. **Fog texture is my addition to the plan's "Unlit/Transparent Colored".** Without it the sheets cannot render sensibly (empty `_MainTex`, no tint property). The peak gain 2 is a guess; `Hidden` is the escape hatch. The sheets are flat horizontal planes (11 x 11 vertex plane, Cull Off), not billboards, so they are visible from both sides but not camera-facing.
3. **Cloth is opaque.** `_Cutoff = 0` and the `_CutoutMask` is not used, so the chain-link-style `fence_ema_01_white` cloth renders as a solid panel; `ClothShader_backface`'s culling behaviour in the substitute was unknown at first pass and is now answered (see "Review fixes": the front cloth is single-sided Cull Back, the backface cloth uses a double-sided substitute). Both are visual only (colliders are separate).
4. **Equator and Bay5 cloth / fog sheets** are outside this item and stay as they are (by the code path: Equator has no fallback, so its 78 cloth + 17 fog renderers per the plan's census stay on the magenta stand-in; Bay5 stays magenta unless the legacy switch is on). If a tester does turn the Bay5 switch on, its cloth is likely to vanish rather than render: Bay5's `City_Military_Tarp_01` has constant albedo alpha 31/255 against `_Cutoff` 0.5, and `fence_ema_01_white_cloth` alpha 0..203 (mean 39) against 0.5 (measured on `bay5-native-v5`). The AirPit-only `_Cutoff = 0` fix would apply there too, but widening needs its own review.
5. **Log levels** of the known-gap lines changed on all maps (Error -> Info + Debug). *(Resolved by the review fix: `IsKnownGap` takes the map key and the switch; Equator and Bay5 keep Error.)*
6. The `dist/` client DLL is the item 11 build above; if another item rebuilds it afterwards, its hash supersedes this one. *(Superseded: the review fix changed the client DLL and, through `ArenaMapPort.Shared`, the server DLL too; hashes below.)*


### Review fixes for item 11 (2026-10-12)

Five review findings, all verified against the code; four fixed, one fixed with a recorded judgement (finding 4). Nothing was run in Unity or on the host.

| # | Finding | Verdict | What changed |
|---|---|---|---|
| 1 | Unresolved-shader log level lowered on every map | Confirmed | `IsKnownGap(mapKey, name, airPitSwitchOn)` is true only for AirPit's four allowed names while `AirPitShaderStandins` is false (the deliberate A/B baseline). That case keeps the single Info line plus Debug detail. Equator, Bay5, every parked map, AirPit with the switch on (a substitute that could not be resolved) and any name outside the allow-list (`Nature/SpeedTree8` included) log the per-name Error line exactly as at HEAD. The old `else Debug "no known-gap ..."` line and the `SpeedTree8` constant are gone. |
| 2 | Backface cloth substitute is single-sided | Confirmed, fixed with option (a) | See "Backface cloth" below. |
| 3 | Hidden-mode count expectation (about 91) is wrong | Confirmed | `RunPass` walks `GetComponentsInChildren<Renderer>(true)` and `TryHideFogSheet` counts any renderer whose own `enabled` flag is on, so the 36 sheets under the deactivated `PASSPORT_CONTROL_DOORS` group are disabled and counted: up to 127 (73 + 54). Checklist step 3 now says so, and the Debug line ends with "(inactive hierarchies included)". Behaviour is deliberate: a sheet under a group the barrier repair deactivates stays hidden if the group is ever reactivated. |
| 4 | Allow-list is a key test in a client class, not a descriptor field | Judgment call; fixed (the stated reason is gone, the plan and the conventions ask for descriptor data) | `ArenaMapDescriptor.ShaderFallbackNames` (new optional constructor argument, default empty) plus `shader_fallback_names` in `tools/maps/bay5.json` (the six table names) and `tools/maps/airpit.json` (the four names). `ShaderStandinPolicy.AllowedNames/TryGetFallback/UsesFallbackTable` read it; Equator and all parked maps have no list and therefore never substitute. What stays in code on purpose: the original -> substitute rows, which switch governs which map (`FallbacksActive`, Bay5 legacy key vs the AirPit key), the AirPit texture/float work (`FixFor`) and the AirPit backface override. `validate_map_descriptors.py` gained `shader_fallback_errors` (shape: nonempty, unpadded, no duplicates) and a descriptor-vs-sidecar mirror check; `tools/tests/test_shader_fallback_names.py` (6 tests) and `ShaderStandinPolicyTests.TheAllowListsMirrorTheSidecarsAndOnlyBay5AndAirPitHaveOne` pin them. `UnityProject/.../ArenaPaths.cs` reads sidecars with `JsonUtility`, which ignores the new key like `puddle_water_material_names`. |
| 5 | `_unresolved` is add-only, so a name resolved on the sweep is still reported | Confirmed (code path; no host failure demonstrated) | `Rebind` now calls `_unresolved.Remove(originalName)` right after a successful `material.shader = target`. A name whose materials still fail is re-added by the next failing material, so only names that are genuinely unresolved at the 3 s summary are reported. Reachable because `_byName` is built once per raid while `Shader.Find` sees later-loaded shaders. This is bookkeeping on a `HashSet` inside the Unity-bound class, so it has no xunit test; the checklist step 1 ("no Info line and no Error line") now rests on it. |

#### Backface cloth (finding 2, open question 3 answered)

Read-only dump of `D:\SPT41\EscapeFromTarkov_Data\StreamingAssets\Windows\shaders` (UnityPy, scratchpad `airpit-b/fix11/cull2.py`; nothing written to the game folder):

| Shader | Properties | Passes (all three: FORWARDBASE, FORWARDADD, DEFERRED; LOD, queue) | Cull |
|---|---|---|---|
| `p0/Cutout/Bumped Diffuse` | `_StencilType, _Color, _MainTex, _BumpMap, _Cutoff, _SpecVals, _DefVals, _Temperature, _Factor, _Units` | queue AlphaTest, LOD 0; fallback `Transparent/Cutout/Diffuse` | **2 (Back)** in all three; no `_Cull` property |
| `Legacy Shaders/Transparent/Cutout/Bumped Diffuse` | `_Color, _MainTex, _BumpMap, _Cutoff` | queue AlphaTest, LOD 300 | 2 (Back) |
| `Legacy Shaders/Transparent/Cutout/Bumped Diffuse 2 side` | `_Color, _MainTex, _BumpMap, _Cutoff` | queue AlphaTest, LOD 300; fallback `Legacy Shaders/Transparent/Cutout/Diffuse` | **0 (Off)** in all three |

So the shared `p0/Cutout/Bumped Diffuse` substitute would make the 22 `Cloth/ClothShader_backface` renderers (for example the `Facade_FlagWall_*` objects with `City_Military_Tarp_01`) vanish from behind, and a material-level override is impossible. AirPit's `ClothShader_backface` now rebinds to `Legacy Shaders/Transparent/Cutout/Bumped Diffuse 2 side` (`ShaderStandinPolicy.AirPitTargets`, `TwoSidedClothShader`); it declares `_Color`, `_MainTex`, `_BumpMap` and `_Cutoff`, so the existing cloth fix (`_NormalMap1 -> _BumpMap`, `_Cutoff = 0`) applies unchanged. Bay5's table row (and its behaviour) is untouched: `ShaderStandinPolicyTests.OnlyAirPitsBackfaceClothUsesTheDoubleSidedSubstitute` pins both. The front cloth (`Cloth/ClothShader`, the other 118 of the 140 cloth renderers) stays on `p0/Cutout/Bumped Diffuse`, single-sided; whether Arena's front cloth is double-sided is not known. Caveats: the legacy shader lacks EFT's `_Temperature` and stencil terms, so thermal vision and the stencil effects will not see it, and that "backface" means "double sided" is inferred from the name. Host checklist step 1 now includes a look at the flag wall from behind and names the one-line revert.

#### Verification after the review fixes (2026-10-12)

| Command | Result |
|---|---|
| `dotnet build ArenaMapPort.Client/ArenaMapPort.Client.csproj -c Release -p:ArenaMapPortDllOnly=true` | 0 warnings, 0 errors; client DLL `38252bf33976c596fdb12ab557cae3622769c23b963094ee34af6e9de8a0d787` (723,456 B), bin == dist |
| `dotnet build ArenaMapPort.Server/ArenaMapPort.Server.csproj -c Release` | 0 warnings, 0 errors; server DLL `4ecc06f1966ba50a9e029c0b979ff400f853543504bf66396b55e1aaa79c50fe`, bin == dist. It changed only because `ArenaMapPort.Shared` gained the property; no server behaviour changed, but a deploy replaces it, so verify the host hash and restart the server as usual |
| `dotnet test ArenaMapPort.Server.Tests/ArenaMapPort.Server.Tests.csproj -c Release` | **675 passed**, 0 failed (672 before: one known-gap test replaced by two, plus the sidecar mirror test and the double-sided cloth test); no warning comes from `ShaderStandinPolicy.cs` or its tests (the warnings the test build prints come from other items' linked files) |
| `python -m unittest discover -s tools/tests -p "test_*.py"` | 975 tests, OK, 44 skipped (969 + the 6 in `test_shader_fallback_names.py`) |
| `python tools/validate/validate_map_descriptors.py` | OK (14 sidecars, 14 catalog entries, release `All`, `ArenaPaths.EquatorDefault()`), now including the `shader_fallback_names` mirror |
| `python tools/validate/validate_distribution.py` | the intended `DISTRIBUTION_FAILED: Stale AirPit bundle` (U1 not built yet), unchanged |
| `test_unity_log_scan.ps1` | not run (no editor code touched) |


## Items 13 (ballistics check), 15, 17-19 (genericity audit), 20 and 21 - client and csproj wiring for the first AirPit raid (done 2026-10-12, about 01:00-01:50 local)

Offline only: no Unity, no host, no deploy. Wrote only repository files, the ignored `dist/` (the Release client build rebuilt the DLL and staged the two new AirPit inputs) and the agent scratchpad (`airpit-b/wiring/`: read-only census scripts and a scratch re-extraction). Reads of the live Arena install were read-only (UnityPy on `level324`). Bundles are untouched; Equator and Bay5 bundles, sidecars and `base.json` files did not change. The client DLL changed (and `dist/` holds it); the server DLL is byte-identical to the one the item 11 review recorded.

### What changed

| File | Change |
|---|---|
| `ArenaMapPort.Client/ArenaMapPort.Client.csproj` | `ArenaMapPortStageAiSidecars`: row for `tools/maps/airpit.ai.json` (staged as `ai/airpit.ai.json`, DllOnly builds included). `ArenaMapPortStageAcoustics`: row for `work/extract/spatial-audio/arena_airpit` (graph JSON, original bake, room-tone WAV -> `acoustics/arena_airpit/`), and the missing-input `<Error>` now also fires when `arena_airpit\spatial-audio.json` is absent (message names `--map arena_airpit`). Flare, water, DynamicMaps and bundle rows untouched (AirPit has none of those, plan items 22/3) |
| `ArenaMapPort.Client/NativeSpatialAudio.cs`, `RoomReverb.cs`, `PrecipitationAudio.cs` | the per-map `new[] { Equator, Bay5 }` table gets `ArenaMapRegistry.AirPit` (new sections `Audio (arena_airpit)`: `NativeSpatialAudio` true, `NativeRoomReverb` true, `RainVolume` 1; all bound through `Plugin.Instance.Settings`, hidden for players by the existing section-suffix rule). Comments name why the three lists must stay equal (RoomReverb indexes its own table with the acoustic state's `MapKey`). `PrecipitationAudio` class doc says "released maps" instead of "the two supported maps" |
| `ArenaMapPort.Client/NativeDecalRepairs.cs` | the only real gate found by the item 17-19 audit: `new[] { Equator, Bay5 }` gets AirPit (new section `Decals (arena_airpit)`: `Enabled` true). Without it AirPit would render none of its 481 decals and log nothing |
| `ArenaMapPort.Client/PrecipitationCover.cs` | doc only: roof sampling stays Equator-only on purpose, and says what AirPit/Bay5 use instead (see item 21) |
| `ArenaMapPort.Client/BarbedWireRepairs.cs` | doc only (item 15): there is no map gate to widen, with the AirPit census |
| `ArenaMapPort.Client/FootstepSurfaceProbe.cs` | the F10 footstep diagnostic (DeveloperMode only) had `map.Key != "arena_equator" && != "arena_bay5"` -> return. It now accepts every released map (`CurrentMap` only resolves over `ArenaMapRegistry.All`), so the item 13 footstep check works on AirPit. Equator/Bay5 output is identical |
| `ArenaMapPort.Client/BloomTuning.cs` | comment only: records the decision that AirPit stays at -1/-1 (see Decisions 1) |
| `ArenaMapPort.Server.Tests/AiAnchorsTests.cs` | AirPit rows in the four theories (counts 19 / 3 / 35, unique ids, hub ids exist, no connection fields) and a new test that every AirPit anchor sits inside the descriptor's bake bounds (x -80..56, y -2..8, z -96..92) |
| `ArenaMapPort.Server.Tests/AirPitWiringTests.cs` (new, 7 cases) | source-text pins for what cannot be compiled into the test project: the four per-map tables name exactly `ArenaMapRegistry.All` (4 cases, one per file); the csproj stages one acoustics package per released map and its missing-input guard names the same maps; every released map that has a `tools/maps/<stem>.ai.json` has a csproj row and every row has a file; `BarbedWireRepairs.cs` and `BallisticTwinRepairs.cs` contain no map literal outside comments |

### Gate audit

| Item | What was checked | Result |
|---|---|---|
| 20 AI anchors | `AiAnchors.Load` | `ShortKey` strips `arena_`, so `arena_airpit` reads `ai/airpit.ai.json`. `AICorePointFallback.InitPrefix` and `PatrolWayFallback` gate on `ArenaMapRaid.IsLoaded()` only; the only other thing that sees the sidecar is the csproj list. Nothing else gates on Equator/Bay5 (grep over `ArenaMapPort.Client/*.cs` for registry members, `"arena_*"` literals and `Key ==`). The sidecar (git-tracked, item 4) has 19 core points (ids 1..19), 1 patrol way with 3 hubs and 35 sub points; core points y 0.02..6.39, hub and sub points y 0.003..0.13, all inside the bake bounds. Only the csproj row was missing |
| 21 acoustics | `Prepare`, `BuildGraph`, `RoomReverb.Tick`, `PrecipitationAudio.Update`, `SpatialAudioRepairs` | table entries were the gates (fixed). The AirPit package passes the code's own requirements: schema 1, `map_key` `arena_airpit`, exactly 1 system and 1 group, rooms 24, portals 94. `NativeSpatialAudio.cs:323` (the `AmbientInVolume` lease) is Bay5-only on purpose and AirPit has no native ambient yet (item 13 audio, later). `Bay5AmbientAudio.Ensure` ignores every other map (`mapKey != bay5 && != equator`). `DynamicMapAssets`, `NativeMultiFlare`, `Bay5AmbientAudio` left as they are |
| 21 HDR / bloom | `HdrImageTuning.Install`, `BloomTuning.Install` | both already iterate `ArenaMapRegistry.All` (items 9/10), so AirPit has `Lighting (arena_airpit)` sections: HDR neutral (Original tonemap, 0 EV, range 1, speed 1), bloom -1/-1. No code change needed |
| 15 barbed wire | `BarbedWireRepairs`, `SpawnInfiltrationFixPostfix`, `ArenaMapRaid` | the plan's "Bay5-only" is not what the code says: gates are `IsLoaded()` and `IsOwnedScene`, both over `All`, name prefix `bruno_helix`. No code change. Live census (UnityPy read-only on `level324`): 26 `EFT.Interactive.BarbedWire` components = 12 on active `bruno_helix` objects (BoxCollider, trigger, enabled) + 14 on inactive `Bruno_LOD0` mesh children whose only collider is a MeshCollider. The package has exactly the 12 `bruno_helix` trigger boxes, all active in hierarchy, all in `Arena_AirPit_ShootOut`. The repair attaches 12 |
| 13 ballistics | `BallisticColliderImporter`, `SceneAssembler`, `BallisticTwinRepairs`, `BallisticMaterialDiagnostics`, `validate_native_features`, `validate_bundle_roundtrip` | generic everywhere: importer takes component dicts from the package; twins and the `LogMaterialHits` diagnostic work on `IsOwnedScene`; the roundtrip calls `validate_native_features.validate_bundle`, whose contract for AirPit is `tools/extract/native_contracts/airpit.json` (324: 2,856, 325: 512, 40: 28, 29: 94, 28: 58 = 3,548). `ShaderRebind` is a material rebind and is unaffected. Only gate: the F10 footstep probe (fixed above). Offline: `validate_native_features.validate_package(airpit-native-v5)` passes (live-source recheck by scene index); `extra_counts` = GameObject 4, Transform 4, AudioSource 0 (rain-cover objects only, no audio root) |
| 17 rain cover | `RainCoverImporter`, `RainCoverRepairs` | importer is package-driven: `weather_obstacle` false and zero dry planes is a legal shape, 2 IndoorTrigger required non-zero. Client side gates on `IsLoaded()` + `WeatherRig.IsActiveForCurrentMap()`, and the AirPit rig is active by default since item 10. No Equator/Bay5 literal. `PrecipitationCover` is the only Equator-only piece and stays so (documented) |
| 18 lights | `LightCullingImporter`, `ArenaLightingRepairs`, `EnvironmentRepairs`, `LightCullingProbe` | no map literal in any of them; `ArenaLightingRepairs.Install` binds and captures per `All` map. Package census: 533 `CullingLightObject` (all 533 on a Light; 298 in an active hierarchy: 292 in `Arena_AirPit_ShootOut_Light`, 6 in `Arena_Airpit_TeamFight_DesignStuff`; 2 in `ShootOut` and 4 in `TeamFight` are inactive), 130 `VolumetricLight`, 0 `LightFlicker` |
| 19 decals | `StaticDecalImporter`, `NativeDecalRepairs`, `validate_decals` | importer and validators generic; the client had the literal pair (fixed). Package census: 481 `StaticDeferredDecal` (423 ShootOut, 4 DesignStuff, 54 TeamFight_DesignStuff), 471 enabled with an active owner (none sits under `BUS_DOORS` or `PASSPORT_CONTROL_DOORS`, so barrier deactivation does not change that), 55 distinct materials, 54 of them on active decals, material shaders a subset of what Equator and Bay5 already render (material counts: `Decal/DeferredDecalDiffuseNormalsDynamicEditor` 55, `Ultra Deferred Decal Of God 3000` 35, `Water Deferred Decal` 3, plus the `p0/Reflective/Bumped Specular SMap_Decal` variant 31) |

### Decisions

1. **Bloom stays -1/-1 for AirPit.** The task said "like Bay5 (-1 keep camera)", while Bay5 actually ships the explicit 2.10/0.35 plus a `BloomDefaultsRevision` marker. I kept the literal reading: nothing to change, no marker key is created for a map that never shipped. Under the donor rig the camera's own value is the donor 2.10/0.35, so the on-screen result is the same as explicit defaults; they differ only if the donor camera takeover fails (the host check in item 10 already looks for that). Switching to the explicit values later is one token (`donorDefaults` includes AirPit) and, because a stored -1 equals the legacy value, `MigrateDefaults` would move any saved -1 once. Open question 1.
2. **Explicit per-map arrays, not `ArenaMapRegistry.All`.** A future promoted map that has no acoustics package or no decals must add itself deliberately; the new test then fails first instead of the feature failing silently on the host.
3. **No BarbedWire code.** The audit found the repair already generic; adding a gate or a list would be new code. The doc comment records the census so the next reader does not repeat it.
4. **Footstep probe widened, ballistics otherwise untouched** (item 13 asked for code only if a gate blocks AirPit; this one blocks the plan's own footstep check).
5. **`validate_distribution.py` not extended.** It still has no exact-set check for `ai/` or `acoustics/` (item 9 noted this); the plan's release checklist item 3 expects one. Left alone because item 9's art check landed in the same file this session; see open question 2.
6. **`deploy-to-spt.ps1` needs nothing**: it copies the recursive file set of both dist roots (line 675), so `ai/airpit.ai.json` and `acoustics/arena_airpit/*` travel with the ordinary deploy.

### Offline evidence

| Check | Result |
|---|---|
| `python tools/extract/extract_spatial_audio.py --map arena_airpit --output <scratch>` (3.1 s) | `rooms 24, portals 94, areas 78, clips 1`, bake `{bytes 481738, room_pairs_count 276, routes_count 16304, sha256 4be00976...a6bd9}`; `diff -r` against `work/extract/spatial-audio/arena_airpit` (item 4 output) is empty, so the input was not regenerated in place and is current |
| staged copies in `dist/` after the DllOnly build | `ai/airpit.ai.json` sha256 `094f55c0...eab382`, identical to `tools/maps/airpit.ai.json`; `acoustics/arena_airpit/{spatial-audio.json 167fefb8..., source.audiobakedata 4be00976..., sharedassets39.assets_9.wav 1c67ed9a...}` equal to the `work/extract` files; equator/bay5 acoustics and ai files untouched |
| live read-only census, `level324` (UnityPy) | 26 BarbedWire = 12 `bruno_helix` (active, trigger BoxCollider, enabled) + 14 `Bruno_LOD0` (inactive, MeshCollider) |
| package census scripts (scratchpad) | numbers in the audit table |

### Verification (2026-10-12, after the last edit)

| Command | Result |
|---|---|
| `dotnet build ArenaMapPort.Client/ArenaMapPort.Client.csproj -c Release -p:ArenaMapPortDllOnly=true` | 0 warnings, 0 errors (`PARKED_DIST_OK Client 0 files`); client DLL `40708c8c7f04b1b8e167a9e2c09b5b72332387f60b681a58ac777026c8082a2f` (723,456 B), bin == dist |
| `dotnet build ArenaMapPort.Server/ArenaMapPort.Server.csproj -c Release` | 0 warnings, 0 errors; server DLL `4ecc06f1966ba50a9e029c0b979ff400f853543504bf66396b55e1aaa79c50fe` (unchanged from the item 11 review), bin == dist |
| `dotnet test ArenaMapPort.Server.Tests/ArenaMapPort.Server.Tests.csproj -c Release` | **687 passed**, 0 failed (675 before: +5 in `AiAnchorsTests`, +7 in `AirPitWiringTests`) |
| `python -m unittest discover -s tools/tests -p "test_*.py"` | 975 tests, OK, 44 skipped (no Python file touched) |
| `python tools/validate/validate_map_descriptors.py` | OK (14 descriptors, 14 catalog entries, release `All` membership, `EquatorDefault()`) |
| `test_unity_log_scan.ps1`, Unity, host, deploy | not run (no editor code touched) |

### Host-test checklist (SPT-GPU-2, after U1, a full non-DllOnly client build and an EFT restart; DeveloperMode on, BepInEx Debug lines enabled)

Log tags below are the ones the code writes; the task text's "NativeAudio/spatial" is `[ArenaMapPort][Acoustics]`.

**Item 21 - acoustics, room reverb, rain audio (any AirPit raid, bots optional)**
1. Before room initialization: `[ArenaMapPort][Acoustics] arena_airpit: prepared rooms=24, portals=94, bake pairs=276, routes=16304, sha256=4be00976...`. A `Preparation rejected` Error or `package hash` message means the staged file differs from `work/extract`.
2. `native initialized; valid rooms=24, portals=94, pairs=276, routes=16304; room tones wait for raid start`, then after the raid is `Started`: `room-tone controller started after audio reset`.
3. `[ArenaMapPort][RoomReverb] arena_airpit: native room controller active; 26 original parameters, detailed room types mapped; gameplay snapshots retained`.
4. `[ArenaMapPort][RainAudio] arena_airpit: ready after raid audio reset; native bank='...', four owned 2D sources`, and on leaving `released four sources and ambient bus leases`. F10 (DeveloperMode) logs the whole stack in one press: `[Acoustics]` room list and `room-tone clip=... playing=... indoorBusDb=... mixer=...`, `[RoomReverb] room=... active=True`, `[RainAudio] map=arena_airpit ready=True ... room=<id>/<type> environment=...`, plus `[Footsteps]` (item 13). In rain, `room=`/`environment=` should change between hall and apron.
5. Listen: walk hangar/terminal doors (muffling and reverb change), hear the room tone (one clip, `airpit_amb_indoor_tone_`, indoor rooms; F10 shows `playing=True`). **If the room tone is silent** (`playing=True` but inaudible, `indoorBusDb` very low), the candidate one-liner is the Bay5-style `AmbientBusLease.Acquire(mixer, "AmbientInVolume")` at `NativeSpatialAudio.cs:323` (currently keyed on Bay5 only; Equator plays its tones without it).
6. Rain under the glass roof and in the passport hall: with no `PrecipitationCover` sampling the bed follows the acoustic room (`rainCover=room-fallback` in the Report line). Judge whether a listener under the roof hears the outdoor bed; if so the cover sampler needs an AirPit decision (it is Equator-only today).
7. `uibridge_config` on `Audio (arena_airpit)`: `NativeSpatialAudio` true, `NativeRoomReverb` true, `RainVolume` 1; `Lighting (arena_airpit)` `TonemapProfile` Original, `BloomThreshold` / `BloomIntensity` -1.
8. Non-regression: AirPit -> Equator -> AirPit and a Bay5 raid still show their own `prepared rooms=23, portals=55` / `rooms=44, portals=89` lines.

**Item 20 - AI anchors (WITH-bots raid)**
1. `[ArenaMapPort][RaidFix] arena_airpit: authored 19 real AI core point(s), 3 patrol hub(s), 35 sub point(s) from ai/airpit.ai.json, plus the synthetic id-0/group-0 alias (SpawnPointParams.CorePointId is always 0)` and and per bot zone `[ArenaMapPort][RaidFix] zone '<name>' authored 1 real patrol way(s) from ai/airpit.ai.json (3 hub(s), 35 sub point(s))`.
2. Absent: `[AiAnchors] no sidecar at ...` (Debug) and `failed to load/parse` (Warning), `authoring real patrol ways ... failed` (Warning).
3. `uibridge_bots`: bots Active under `PatrolAssault`, no `ActiveFail`; `uibridge_teleport dryRun=true` to each core point is `reachable=true` (the port model put 78 of 78 probe points in one component only with the item 8 barriers and cutters, so a miss here points at item 8, not at the sidecar).

**Item 19 - decals**
1. `[ArenaMapPort][NativeDecals] map=arena_airpit enabled: source=481 active=471 registered=471 materials=54 groups=... nativeEnabled=True`. `released N compressed source CPU copies before atlas creation` precedes it.
2. Sequence AirPit -> Equator -> AirPit: `renderer removed; released ...` between raids and exactly one manager (a `refusing a second manager` Error is the failure form). `Decals (arena_airpit)/Enabled` off/on live comparison.
3. `validate_bundle_roundtrip` (U1 `-Verify`) prints `native decal identities, hierarchy, materials, UVs and settings roundtrip` as a pass.

**Item 18 - lights**
1. `[ArenaMapPort][Lighting] arena_airpit applied (Enabled=True): ... cullingLights=533, editorIntensityBaselines=N` (533 = every CullingLightObject, all sit on a Light).
2. F7 (`LightCullingProbe`): `[LightProbe] arena_airpit ... cullingLights=` counts active objects only (expect up to 298, less where a barrier group is off), the `volumetric:` line `components=` depends on the player's volumetric setting (130 in the package, 22 active in hierarchy), `flicker: no enabled LightFlicker`. Counts 533 / 130 / 0 are confirmed offline by `validate_light_culling_counts_bundle` in the roundtrip.
3. Whether the 509 saved-inactive lights stay dark in a way that looks wrong is the plan item 18 judgement; this wiring changes nothing there.

**Item 17 - rain cover**
1. Unity build log (not the client log): `[ArenaMapPort][RainCoverImporter] authored EnvironmentManager (TriggerGroup=1, IndoorTrigger=2), WeatherObstacle=0`.
2. Client: `[ArenaMapPort][RainCover] map=arena_airpit RainBounds computed from N renderer(s) ...` and the `RoofDepthBlack scan:` line; F8 depth readback and a screenshot of falling rain in the hall and on the apron (item 10 checklist step 4 already asks for the bounds size).

**Item 15 - barbed wire**
1. `[ArenaMapPort][RaidFix] barbed wire: attached 12 BarbedWire trigger(s) (0 already present)` (the spawn postfix runs twice per raid, the second line reads `attached 0 ... (12 already present)`; the first five carry `'bruno_helix (n)' at (...)`).
2. Teleport into a volume near (1.4, 4.1, 28..31) and one near (-24.7, 4.1, -26..-24) (positions from the plan item 14 survey), step in: HP loss, sprint off and a speed limit (`Combat/BarbedWire` true). A teleport across the two NavMeshModifierVolume areas of item 14 uses the same objects.

**Item 13 - ballistics**
1. U1 `-Verify`: the roundtrip's `native material/audio fields, hierarchy, clips and script identities roundtrip` passes with 3,548 BallisticCollider (2,856 / 512 / 28 / 94 / 58).
2. Host, no bots: fire at concrete, metal, wood and glass; with `Combat/LogMaterialHits` on, `[ArenaMapPort][MaterialHits] scene=Arena_AirPit_... collider='...' ballistic=... defaultFallback=False` (a `defaultFallback=True` row is a collider without a native ballistic material). F10 on the floor of the hall, apron and a carpet/stair area: `[ArenaMapPort][Footsteps]` shows different native/result surfaces (now works on AirPit).
3. `[ArenaMapPort][RaidFix] ballistic twins: added N HighPolyCollider twin(s)` is expected non-zero or zero, no count is pinned for AirPit.

### Open points and questions for the lead

1. **Bloom for AirPit**: kept -1/-1 (decision 1). Say if you want the explicit 2.10/0.35 like Bay5 (adds a `BloomDefaultsRevision` marker for the AirPit section; one-token change plus a comment).
2. **Distribution gate**: `validate_distribution.py` still checks neither `ai/` nor `acoustics/` as exact sets (and `test_validate_distribution.py` has no fixture for them). A stray or missing AirPit file would pass it. Adding both is small; I left it to avoid colliding with the other items editing that file.
3. **Room tone bus**: whether AirPit needs Bay5's `AmbientInVolume` lease can only be answered by ear (checklist 21.5).
4. **Rain under the glass roof**: AirPit and Bay5 classify rain audio by acoustic room only (checklist 21.6); the Equator roof sampler would need a separate decision to reuse (its depth photo comes from the rig, which AirPit has).
5. `dist/` carries this client DLL (`40708c8c...`), the unchanged server DLL and, until U1 plus a full client build, the stale spike AirPit bundles (item 9 note): do not deploy yet.
6. `tools/maps/AdditionalMapBuilds.props` is still not touched (item 9 open point 3).

### Review fixes for items 13, 15, 17-19, 20 and 21 (2026-10-12, about 01:40-02:10 local)

Three minor findings. All three were checked against the code; all three are confirmed, one sub-point of finding 2 is disputed. Offline only again: no Unity, host or deploy step; Equator and Bay5 bundles, sidecars and `base.json` files are untouched (only the client DLL changed).

| # | Finding | Verdict | What changed |
|---|---|---|---|
| 1 | Comments and option text still say Equator/Bay5 only for AI anchors and the reverb probe | Confirmed, plus three more spots of the same kind | Comment/text only, no behaviour: `AICorePointRepairs.cs` class doc and the `AiAnchors.Load` line comment, `PatrolWayRepairs.cs` class doc, `AiAnchors.cs` class doc (not in the finding, same wording: "every map besides Equator/Bay5"), `SpatialAudioRepairs.cs` class doc ("owned Equator/Bay5 graphs", AirPit now initializes the same way). `ReverbAudioProbe`: the `ReverbPulse` option text now says it needs a map with native acoustics (Equator, Bay5, AirPit) and that the map-ambience bus needs the imported ambient driver (Equator, Bay5). The probe's failure for that bus on AirPit is exactly as described: `Bay5AmbientAudio.CurrentEffectsMixer("arena_airpit")` is null (`Ensure` ignores every other map), the probe throws and its catch logs the Error and resets the switch. The exception text now names the bus and the map instead of "Map ambience route is not ready". The option text is a description string only (key, default and meaning unchanged; nothing in tests or docs quotes it). Left alone on purpose: the `_comment` strings inside `tools/maps/equator.ai.json` and `bay5.ai.json` ("the twelve deferred maps") are staged data; editing them would change Equator and Bay5 dist bytes for no behaviour |
| 2 | Room-tone bus reasoning (open question 3, checklist 21.5) incomplete and line number stale | Confirmed; one sub-point disputed | See "Room-tone bus, corrected" below |
| 3 | Nothing in `validate_distribution.py` checks `ai/` or `acoustics/` | Confirmed | `validate_ai_sidecars` and `validate_acoustics` added and wired after `validate_art`; two tests; see "Distribution gate" below. The reason it was deferred (item 9 editing the same file) is gone: item 9 and its review fixes are finished above, and this edit adds two functions and two calls without touching the existing checks |

#### Room-tone bus, corrected (supersedes the "Gate audit" cell for item 21 and checklist step 21.5)

Read-only trace of every `AmbientBusLease.Acquire` caller in the client:

| Holder | Bus | Maps | Where |
|---|---|---|---|
| `Bay5AmbientAudio.Driver.AcquireAmbientVolume` | `AmbientInVolume` on Equator (`_equator`), `AmbientOutVolume` on Bay5 | Equator, Bay5 (`Ensure` returns for any other map) | `Bay5AmbientAudio.cs` 487-499, called at 299 |
| `NativeSpatialAudio` room-tone controller | `AmbientInVolume` | Bay5 only (`state.MapKey == ArenaMapRegistry.Bay5.Key`) | `NativeSpatialAudio.cs:325` (the finding is right: it was `:323` before the comment lines were added; the earlier "Gate audit" cell and 21.5 carry the stale number) |
| `PrecipitationAudio.Driver.TryStart` | `AmbientOutVolume` and `AmbientInVolume` | all released maps, AirPit included | `PrecipitationAudio.cs` 212-213 |

So the earlier statement "Equator plays its tones without it" was wrong: Equator holds the interior bus through its ambient driver (and Bay5 through the spatial controller plus its driver's exterior lease). AirPit has no ambient driver, so its only holder of `AmbientInVolume` is `PrecipitationAudio`. That lease is taken as soon as the raid is `Started`, the audio listener exists, `Seasons.Controller` is set and the native precipitation bank is loaded. It does not wait for rain: `TryStart` never reads the rain intensity. `AmbientBusLease.Acquire` only raises the bus from the reset value (at or below -79.99 dB) and yields to any other writer. Consequence: AirPit's room tones depend on the rain-audio driver staying healthy, not on the weather. `ClearPlayback` releases both leases when the driver is `_failed` (the `precipitation stopped for this raid` Error), `_foreign` (a native `AmbientAudioSystem` or an active `PrecipitationAmbientBlender` exists; `native ambient owner present` Debug line); an audio-device refresh releases them too but the driver re-acquires on the following frames.

Disputed sub-point: the finding suggests checking with `RainVolume=0`. `RainVolume` only multiplies the source gain inside `Tick` (`PrecipitationAudio.cs:268-271`); `TryStart` and the leases never read it, so a zero volume keeps the lease and is not a failure mode. It is harmless to try, but the two real failure modes are `_failed` and `_foreign`.

Replacement for checklist step 21.5 (the other steps stand):

5. Room tone in dry weather first (weather None or clear): F10 prints `[Acoustics] room-tone clip=airpit_amb_indoor_tone_ ... playing=True ... indoorBusDb=0.0` and, in the same press, `[RainAudio] map=arena_airpit ready=True foreign=False failed=False ... ambientInDb=0.00`. Walk the hangar and terminal doors and listen for the tone. Then repeat in rain. The tone must not depend on the rain.
   - `ambientInDb` and `indoorBusDb` near -80 with `playing=True`, or `foreign=True` / `failed=True`, means the interior bus is closed on AirPit: nobody holds the lease. Then the candidate one-line change is at `NativeSpatialAudio.cs:325`: take the interior lease on every map that has no ambient driver (or on all maps; leases are reference counted per mixer and parameter, so a second holder is safe). No code change before the host shows this.
   - `ReverbPulseBus = MapAmbience` cannot be used on AirPit (no imported ambient driver); use `Ambient`, `IndoorRain` or `Gameplay`.

#### Distribution gate (finding 3)

`tools/validate/validate_distribution.py` gained two functions, called right after `validate_art`:

- `validate_ai_sidecars(plugin, keys)`: `ai/` holds exactly one `<key without arena_>.ai.json` per released map (`equator`, `bay5`, `airpit`), each byte-equal to its git-tracked source `tools/maps/<stem>.ai.json` (a sidecar edited after the last client build is "Stale AI sidecar"), and the JSON's `map_key` equals the stem (a copy between csproj rows is refused).
- `validate_acoustics(plugin, keys)`: for every released map `acoustics/<key>/` holds exactly `spatial-audio.json` plus the files that graph lists (`bake.file` and each `clips[].decoded.file`); the package must be schema 1 and name its own map; every listed file must have the recorded length and SHA-256. A parked map's directory or any stray file is "Acoustics file set mismatch". The check is self-consistent on purpose (like the flare check): the recorded hashes tie each file to the package JSON, and the csproj copy ties the package JSON to `work/extract/spatial-audio`.

`tools/tests/test_validate_distribution.py`: the fixture now stages both payloads for all three released maps, plus two tests (AI sidecar: missing, stray, stale against its source, wrong map key; acoustics: missing package, missing bake or WAV, stray file, parked-map directory, same-length changed bake, grown bake, wrong map key, wrong schema). 18 tests in that file, up from 16. On the real `dist/` the new checks pass (called directly, outside the full validator): `ai/` = equator, bay5, airpit; `acoustics/` = the three packages with intact hashes. The full validator still ends with the one expected failure, `Stale AirPit bundle` (U1 not built yet).

#### Verification after the review fixes (2026-10-12)

| Command | Result |
|---|---|
| `dotnet build ArenaMapPort.Client/ArenaMapPort.Client.csproj -c Release -p:ArenaMapPortDllOnly=true` | 0 warnings, 0 errors; client DLL `6ce2826b6f40513a8bcf2959a2b402de9d8de4b436419c7eed74044f6d062648` (723,968 B), bin == dist. It replaces `40708c8c...` of the section above; only comments and the probe's option/exception text changed |
| `dotnet build ArenaMapPort.Server/ArenaMapPort.Server.csproj -c Release` | 0 warnings, 0 errors; server DLL `4ecc06f1966ba50a9e029c0b979ff400f853543504bf66396b55e1aaa79c50fe`, unchanged, dist == bin (also after the test run) |
| `dotnet test ArenaMapPort.Server.Tests/ArenaMapPort.Server.Tests.csproj -c Release` | **687 passed**, 0 failed (no C# test changed) |
| `python -m unittest discover -s tools/tests -p "test_*.py"` | **977 tests**, OK, 44 skipped (975 + the 2 new distribution tests) |
| `python tools/validate/validate_map_descriptors.py` | OK (14 descriptors, 14 catalog entries, release `All`, `EquatorDefault()`) |
| `python tools/validate/validate_distribution.py` | the intended `DISTRIBUTION_FAILED: Stale AirPit bundle`, now after passing the new `ai/` and `acoustics/` checks |
| `test_unity_log_scan.ps1`, Unity, host, deploy | not run (no editor code touched) |

Open point 2 above is closed (the gate exists). Open point 3 is refined by "Room-tone bus, corrected": the question is no longer "does AirPit need Bay5's lease" but "does the rain-audio lease keep the interior bus open in every weather", and checklist step 21.5 was replaced.

## Item 12 - build U1 and the first host raid (2026-10-12, lead)

Phase B code was committed as `f4e4505` (977 Python / 687 .NET tests). The first two U1 attempts stopped in Unity's
texture import, both in `NormalTextureImportPolicy.Classify` (fail-closed by design); the offline model and the
host were not involved.

1. `unity_build_all_20261012_014538.log:10261`: `Normal texture 0e098c935f973254 rejected: unverified normal encoding:
   format=DXT1, colorSpace=sRGB, ...`. The texture is `mask2` (512x512, radial grey falloff R=G=B, alpha 255), the
   Cloth `_NormalsMask` of `Lighthouse_tarp_02` on the `tarpaulin_wall_01/03` renderers. The policy knew the Cloth
   mask exception only as DXT5. Fix `232ea72`: a DXT1 sRGB variant with the same preservation (alpha 255,
   |G-B| <= 16), four harness cases.
2. `unity_build_all_20261012_015015.log:2669`: `Normal texture 4f803b16461865d9 rejected: invalid dimensions/pixels or
   unsupported/mixed material usage` (`Detail_03_N`, `_DetailNormalMap` of the ANGRYMESH rock shader). Instead of a
   third 20-minute build, the production policy was run offline over every normal-flagged texture:
   `work/airpit-parity/normal_policy_inputs.py <package> <json>` (usages from `tex_envs`/`DXT1_NM` exactly like
   `TextureImportDriver.CollectNormalTextureUsages`, channel bounds from the PNGs) and
   `work/airpit-parity/normal_policy_check.ps1 <json>` (Add-Type on the production C#, synthetic pixels that reproduce
   the measured bounds). Equator control: 0 of 239 rejected (1 ClothNormalsMask DXT5, 14 PackedRaG BC7, 223 PackedRaG
   DXT5, 1 RgbNormal DXT1). AirPit: 4 of 258 rejected - the rock shader's `_BaseNormalMap`/`_TopNormalMap`/
   `_DetailNormalMap` (packed DXT5 Linear normals of `Lighthouse_Mountain`) and `Passenger_ladder_02_part_02_G`, a
   packed `_BumpMap` texture that the same SMap material binds a second time as `_SpecMap` (mixed usage). Fix
   `d52c898`: the three rock slots count as normal readers of that shader only, the SMap `_SpecMap` co-usage is
   neither normal nor unsupported (the preserved bytes are what Arena reads in both slots); nine harness cases.
   After the fix AirPit classifies 0 of 258 rejected (1 ClothNormalsMask DXT1, 10 PackedRaG BC7, 246 PackedRaG DXT5,
   1 RgbNormal DXT1); Equator unchanged.

Third U1 run: `work/logs/airpit-u1c.log` (results appended below).

### U1c result (2026-10-12 02:06-02:15, Unity log `work/build/airpit/unity_build_all_20261012_020615.log`)

`[build_all] OK -- build + mandatory identity patch + all validators passed.` Key lines, in build order:

| Stage | Log line (abridged) |
|---|---|
| Textures | `[TextureImportDriver] imported 805 textures (33 flagged alpha-cutout uncompressed, 23 real Cubemap assets, ... 256 packed normals / 1 RGB normals / 1 Cloth masks saved as native source-encoded Texture2D assets)` - the two fixes above account for the DXT1 Cloth mask and the rock-shader normals |
| Stand-in shaders | `[StandinShaderGenerator] generated 22 stand-in shaders` - two new files (`ANGRYMESH_PBR_Rocks_PBR_BlendTopDetail__Legacy_`, `p0_Reflective_Bumped_Emissive_Specular`) and 11 regenerated property unions; committed as generated, like after every earlier build |
| Materials | `[MaterialImporter] imported 435 materials`; roundtrip validator: `all 415 id-named Material objects in the bundle carry the declared stand-in or exact owned terrain shader (0 mismatch(es))` |
| Rain cover | `[RainCoverImporter] authored EnvironmentManager (TriggerGroup=1, IndoorTrigger=2), WeatherObstacle=0` |
| NavMesh inputs | `[BakeNavMesh] bake inputs: bounds from the descriptor (bake_bounds), layer mask 0x40042A00 (navmesh_bake_layers), 4 navmesh_bake_exclusions, 2 opened_barriers`; `navmesh_bake_exclusions: 4 object(s) resolved (their colliders and markups are not NavMesh sources, they stay active at runtime)` |
| NavMesh exclusions | `excluded 16 ... opened_barriers:PASSPORT_CONTROL_DOORS`, `excluded 24 ... opened_barriers:BUS_DOORS`, one source each for `TH_Roll_door_01_closed`, `DonB_Ramen_closed (1)`, `DonB_Ramen_closed`, `fence_ema_01_white (18)` |
| NavMesh modifiers | `NavMesh modifiers: 20 markup(s) in bake scope (0 area-override, 20 ignoreFromBuild), 12 volume source(s) added; volume area distribution: area1=12` |
| NavMesh bake | `baked NavMeshData: agentRadius=0,3 agentHeight=1,7 agentSlope=48 agentClimb=0,38 tileSize=256 bounds=Center: (-12.00, 3.00, -2.00), Extents: (68.00, 5.00, 94.00) sources=2023 (root=null collected 2055 total, 2023 kept after filtering to this bake's own content-scene roots, Water layer(4) excluded from sourceLayerMask)`; **`triangulation: vertices=10295 triangles=4685 (indices=14055)`** - the signature the live `[ArenaMapPort][NavMesh] added navmesh ...` line must repeat |
| Strip | `[NavMeshModifierImporter] stripped bake-only components before the bundle build: 20 NavMeshModifier, 12 NavMeshModifierVolume; re-saved 5 content scene(s)` |
| Bundles | `arena_airpit_scenes.bundle` 824,643,390 B, `arena_airpit_navmesh.bundle` 146,566 B, preset 6,859 B with the 6 `Arena_AirPit_*`/`Arena_Airpit_*` rcids; `base.json` regenerated (4 player + 14 bot spawn points, 5 waves) |
| validate_extract | 0 errors, 240 warnings of the known kinds (`MeshFilter ...: no mesh assigned`, two `barrel_fire` LODGroup renderer references, 21 unreferenced mesh ids) |

**Built mesh against Arena's own NavMesh** (`tools/validate/navmesh_diff.py ... --compare-bundle work/build/airpit/arena_airpit_navmesh.bundle`,
output `work/logs/airpit-navmesh-diff-u1.log`): the built triangulation has 2,805 polygons and covers 199,896 of 204,204 Arena
samples; offline-model agreement passable&covered 198,548 / passable-not-covered 851 / blocked-covered 1,348 / blocked-not-covered
3,457. Probes inside the built mesh: **76 of 77, all in component 0** (4 player spawns, 14 bot spawns, 2 exits, 19 core points,
3 hubs, 35 sub points); the tool names only the count, the likely odd one is `hub1.11` at (5.51, 0.13, 7.19) with Arena border
clearance 0.0 - checked on the host with `uibridge_teleport dryRun=true`. Arena-walkable floor the port lacks: the 3.8 m² airstairs
strip at x -7.55..-6.55, z -30.15..-25.55 (item 8's `navmesh_bake_layers` question) and seven groups of 1.3 m² or less.

**validate_port** reported `PORT_FAILED: source plan digest drift: package was extracted from a different plan` because it hard-coded
`work/extract/<stem>` - the pre-item-1 AirPit package, extracted under the previous plan. It now takes `--package`;
`python tools/validate/validate_port.py --map airpit --package work/extract/airpit-native-v5 --distribution` →
`PORT_VALIDATED {"distribution": true, "map_key": "arena_airpit", "materials": 435, "meshes": 2487, "navmesh_tiles": 56,
"navmesh_triangles": 4685, "scene_bundle_bytes": 824643390, "scenes": 6, "terrain_count": 0, "textures": 805, "verified_starts": 3,
"warnings": 240}`.

**Staging and deploy.** Client Release build (no DllOnly) staged the three AirPit bundles and `material-shaders.arena_airpit.json`;
server Release build 0/0; `dotnet test` 687 passed; Python suite 977 OK; `validate_distribution.py` → `DISTRIBUTION_OK 3 maps 9 bundles
3 sidecars`. Deploy 2026-10-12 (dry run, then `-Confirm`): 63 files, 1975.8 MB - 10 new (AirPit spatial audio `spatial-audio.json`,
`source.audiobakedata`, `sharedassets39.assets_9.wav`, `ai/airpit.ai.json`, five `arena_airpit_N.jpg` loadscreens, the
`arena_airpit.jpg` banner), 7 overwritten (client DLL, `material-shaders.arena_airpit.json`, both AirPit bundles - the mirror still held
the spike-era AirPit bundles -, server DLL, `db/arena_airpit/base.json`, `locales/map-descriptions.json`). Host2 before the deploy:
helper reachable, "server not running · game not running". All 14 deployed AirPit files (server DLL, base.json, map-descriptions,
client DLL, three bundles, material-shaders sidecar, acoustics trio, `ai/airpit.ai.json`, loadscreens, banner) hashed identical on
`\\192.168.178.109\C$\SPT41` before the server was started.

### Host raid 1 (SPT-GPU-2, 2026-10-12 02:32-02:44, build d29bad4, bots None)

`gpu-run start --host host2` (73 s, server + EFT, 19/19 DLLs identical), `General/DeveloperMode` set through `uibridge_config_set` and
EFT restarted (`gpu-run restart --force`, 64 s; the lease of this session counted as "another session"), then
`gpu-run raid --mode pmc --map "AIR PIT" --bots none` (37 s to `raidStarted`: the SelectLocation tile reads `ARENA: AIR PIT`).
`uibridge_config` lists the full `Lighting (arena_airpit)` section with the item 10 defaults (Donor `Reserve_Base_Scripts`,
ZeroLevelOverride -4.4, HeightFalloffOverride 0.025, ReserveTier, UseDonorCameraPrefab true, IndoorFloorSmoothness 0.5,
IndoorCoverPercent 50, Bloom -1/-1, TonemapProfile Original), `Map (arena_airpit)/RemoveTeamFightBorders` true,
`MaterialFixes/AirPitFogSheets` StandIn, `Audio (arena_airpit)` NativeSpatialAudio/NativeRoomReverb true.

**Raid-start log (all expected lines present, no ArenaMapPort warning or error; the three HarmonyX warnings are other mods'):**

| Check | Log (abridged) |
|---|---|
| Shaders | `[RebindShaders] material-shaders.arena_airpit.json: 435 material entries`; per scene 351/24/20/9/11 stand-in materials rebound (= 415), second pass 0/0; census 183x `p0/Reflective/Bumped Specular SMap`, 55x `Decal/DeferredDecalDiffuseNormalsDynamicEditor`, 40x `p0/Reflective/Bumped Specular`, 35x `Decal/Ultra Deferred Decal Of God 3000`, 22x `p0/Cutout/Bumped Diffuse`, 15x `Global Fog/Transparent Reflective Specular`, 13x `Standard`, 11x `SMap_Decal`, 9x `SMap Transparent Cutoff`, 5x `shadow`, 5x `Custom/Billboard_FogSheet_Simple`, 4x `Cloth/ClothShader`, 1x `ClothShader_backface`, 1x `ANGRYMESH/PBR Rocks/PBR BlendTopDetail (Legacy)`, 2x `Unlit/Transparent Colored`, 2x `Custom/Vert Paint Shader Solid`, ... (no magenta in any screenshot) |
| Weather/time | `[WeatherRig] arena_airpit: activated 'Reserve_Base_Scripts' (Full) for this raid. SkyboxMode=Dome.`; `[LevelSettingsOverride] map=arena_airpit ZeroLevel=-4.40 (was -78.64) HeightFalloff=0.025 (was 0.029) TextureBudget=ReserveTier ...`; `[TimeOfDay] arena_airpit: native server time and weather; legacy fixed-hour override bypassed` (02:32 local = night raid) |
| Rain cover | `[RainCover] map=arena_airpit RoofDepthBlack armed`, `RainBounds computed from 9498 renderer(s) (excluded 1 oversized): center=(-64.43, 47.33, -507.17) size=(4274.99, 245.95, 3218.99)`, `RoofDepthBlack scan: keptForBounds=9498 consideredForCover=8832 elevated=8812 ... included(glass)=25`, `drew 8812 elevated mesh(es) (11795 submesh draw call(s))`, automatic F8 readback at the spawn: `depthTextureDimension=4096 ... nothingPhotographed=False` (**open point: 4.3 x 3.2 km bounds from the far scenery = about 1 m per depth pixel; compare with Equator/Bay5 and consider the manual `RainBoundsCenter/Size` before the rain check**) |
| Lighting | `[Lighting] arena_airpit applied ... 831 light(s) (globalMultiplier=0.50 ...) cullingLights=533, editorIntensityBaselines=533` after the last scene |
| Borders/spawns | `[RaidFix] arena_airpit TeamFight borders: 67 object(s) matched (layer 'LevelBorder'=29 ...)`; `spawn placement: disabled 2 SPAWN_BOUND barrier collider(s) ... all spawn points already free`; `spawn openings: spawn barrier 'BUS_DOORS' at (-17.6, 5.8, -69.3): deactivated 6 part(s)`, `'PASSPORT_CONTROL_DOORS' at (-16.1, 3.9, 63.2): deactivated 18 part(s)`; the second `CreateFromScene` pass reports `deactivated 0 part(s), 6/18 already off` (idempotent) |
| Ballistics/wire | `ballistic twins: added 43 HighPolyCollider twin(s) ... (1895 already had one)`; `barbed wire: attached 12 BarbedWire trigger(s)` (then `0 ... (12 already present)`) |
| NavMesh | **`[NavMesh] added navmesh for 'arena_airpit': 10295 vertices, 4685 triangles`** = the U1c bake |
| AI | `AI covers holders: created 8 missing component(s) ...`; `AIVoxelesData ... authored an empty 20x9x29 grid`; `zone 'BotZone_Main' authored 1 real patrol way(s) from ai/airpit.ai.json (3 hub(s), 35 sub point(s))`; `authored 19 real AI core point(s), 3 patrol hub(s), 35 sub point(s)` |
| Acoustics | `[Acoustics] arena_airpit: prepared rooms=24, portals=94, bake pairs=276, routes=16304, sha256=4be00976...`; `native initialized; valid rooms=24 ...; room tones wait for raid start`; `room-tone controller started after audio reset`; `[RoomReverb] arena_airpit: native room controller active; 26 original parameters` (room 6 IndoorBrick at the spawn) |
| Decals | `[NativeDecals] map=arena_airpit enabled: source=481 active=471 registered=471 materials=54 groups=22 nativeEnabled=True` |
| Camera (F7) | `[LightProbe] ... hdr=True path=DeferredShading ... UltimateBloom m_BloomIntensity=0.35 m_BloomThreshhold=2.10`, `PrismEffects ... useBloom=False`, `BloomAndFlares hdr=Off ... lensflares=False` |
| Places | `uibridge_places`: `exfil:pmc:Air Pit North Gate (-11.0, 1.1, 62.0)`, `exfil:pmc:Air Pit South Gate (-3.0, 1.0, -58.0)`, both `RegularMode`, entry point `arenaairpit` |

Screenshots: north spawn (passport hall, globe sign, red wall lights, a bright green smoke column near the west wall - a particle
effect, not identified yet, F10 cannot hit particles; **open point**) and the hall centre from (-3.2, 0.1, -12.1) looking north
(airliner with the ORVAIR sign, mezzanine, crates, plywood walls, lit and complete). 117 fps at the hall centre.

**NavMesh connectivity - the finding of this raid.** From the Usec spawn (0.33, 0, 78.3) `uibridge_teleport dryRun=true` accepted
only the north gate (-11, 62), the north-approach spawn (-11, 60.5) and core2 (15.1, 26.8); every hall, mezzanine and south
point failed with "the walk there passes through a wall or fence" (the same points pass with `reachable=false`, i.e. they are on
the mesh, only the path is wrong). `uibridge_walk` to core5 (-1.23, 37.5), 40.8 m away, reported **283.8 m along the navmesh**,
went west and stopped after 60 s "45.8 m short (stepped right)" at (-46.2, 0, 43.4) facing yaw 214 - a closed roll shutter filling
the screen; the F10 inspector names it `SOO_LOD0/BUILDING/DonB_Ramen_closed/DonB_Ramen_closed_BALLISTIC_metalthin` at 0.28 m.
That is one of the four bake-only `navmesh_bake_exclusions`: the mesh runs through it, the player (and every bot) cannot.

Offline (scratch scripts over `arena_airpit_navmesh.bundle` via `navmesh_diff.load_bundle_mesh` and `port_colliders`), with the
four excluded objects treated as the solids they are at runtime and the mesh clipped to the play space, the 77 probe points fall
into four regions: **north field + east wing** (both player spawns, north gate, core2/core4/core7; 3,008 m2), **hall south/west +
apron + south spawns** (core3, core10, core13-15, core17, core18, core1, core14, south gate, both south spawns; 5,516 m2), the
**airliner block** (core5, core8, core11, core12, core19; 1,590 m2 - probably joined to the hall over the mezzanine at (-11.7, 4.9,
5.8), where the offline tile matching shows a 9 cm seam without any collider) and the **core16 pocket** (641 m2 behind
`TH_Roll_door_02/03_closed`). Every frontier between the north/east region and the hall is a real solid on layer 18:
`SOO/Prison_Metal_Fence_01 (2)/Prison_Metal_Fence` at (-23.5, 50.5) and `(3)` at (-0.4, 50.5) inside the two `Entrance_portal`
metal detectors of the z 50.5 facade, `Airport_Shop_BS_closed_closed` (x 13 / z 7.8 shutter lines of the corner shop), the
`DonB_Ramen_closed` shutters, `Wall_landing_01`. The plan's "the z 50.5 facade has 4 NavMesh openings" counted the two portals
(cut by the gates), the x -40 corridor (ends at the DonB_Ramen shutter) and the x 15..20 opening (leads into the east wing, which
reaches the hall only through the BS shop). Arena's own mesh crosses all of them; the coverage metric of `navmesh_diff.py` never
showed it because `coverage_of` dilates by one cell and these cuts are one cell wide (without dilation the built mesh misses
7,171 of 204,204 Arena samples in 659 groups, the largest 32 m2 around the escalators).

The two portal gates are the mechanism the item 7 probe did not find: each `Prison_Metal_Fence_01 (2)/(3)` holds an **active
closed fence** (`Prison_Metal_Fence`, colliders on 18 + 12) and **two inactive swung-open halves** (`Prison_Metal_Fence_PART_L`,
`_PART_R`) - a gate closed for the ShootOut area (the `LevelBorders` cubes run along the same z 50.5 line) and open for TeamFight,
where the spawns sit north of it. The shop shutters have no open variant (`DonB_Ramen_closed`, `TH_Roll_door_0x_closed`,
`Airport_Shop_BS_closed_closed` are static closed props), so Arena's mesh through them is the stale-bake artefact the probe
suspected.

**Decision (lead, 2026-10-12, within item 8; to be confirmed by the user):** `opened_barriers` gains the two portal gates
(`SBG_Arena_AirPit_ShootOut/SOO/Prison_Metal_Fence_01 (2)` and `(3)`: the client deactivates the closed fence, the open halves
stay inactive, the portal frame stands; the bake drops the fence colliders) and `navmesh_bake_exclusions` is emptied, so every
solid is baked and no NavMesh runs through a shutter. core16 (-45.9, -25.9) keeps its sealed pocket for now (the client snaps it
to the nearest mesh; a bot that draws it finds no path and re-rolls) - open point whether to drop it from `airpit.ai.json`.
Offline check with the new inputs: both gate paths resolve to exactly one object each, bake sources 5,144 of 5,309 owners (was
5,139), 2,013 under Arena's layer mask (was 2,011). Tests: `test_airpit_bake_inputs.py` rewritten around `GATES`/`ROOT_BARRIERS`
(portal gate shape test: closed fence active, halves inactive, colliders 12+18, no frame name), `MapDescriptorConsistencyTests`
expects four barriers and no exclusion; `dotnet test` 687 passed, Python suite OK. The raid was left at 02:44 (`gpu-run leave`, healed)
before the offline analysis. Rebuild U2: `work/logs/airpit-u2.log`.

### U2 (2026-10-12 02:53-03:04, `unity_build_all_20261012_025338.log`) and host raid 2 (03:09-03:14, build 5f0ac2b + d23efda)

U2: `[build_all] OK`; `bake inputs: ... 0 navmesh_bake_exclusions, 4 opened_barriers`; `excluded 1 ... opened_barriers:Prison_Metal_Fence_01 (2)`
and `(3)` (one source each: the closed fence collider), BUS_DOORS 24 / PASSPORT_CONTROL_DOORS 16 as before; `sources=2025`;
**`triangulation: vertices=10264 triangles=4684`**. Offline on the new bundle (scratch `airpit_check_u2.py`: inside-only components
of the mesh clipped to the play space, centroid Dijkstra): one component of 8,707 m2 now holds both north spawns, the north gate, core2/core4/core7,
core5, the hall, every mezzanine point, the apron and the south spawns; `usec_spawn -> core5` is 49 m of centroid path for 41 m straight.
Still apart in that clipped view: core16 (642 m2, `TH_Roll_door_02/03_closed`), core17 (923 m2, inside the `DonB_Ramen_closed`
block) and core18 (523 m2, inside `DonB_Ramen_closed (1)`), and `core3_hall -> core19` (12 m straight) is a 305 m centroid path
over the north field, because the ShootOut cover around the airliner (`plywood_wall_02`, `med_camp_operating_room_wall`,
`shopping_mall_med_camp_tarpaulin_wall`, boards, barrels) walls the hall centre off from the airliner block. `validate_port ...
--distribution` → `PORT_VALIDATED` (4684 triangles, 824,643,005 B scenes bundle); `DISTRIBUTION_OK 3 maps 9 bundles 3 sidecars`;
deploy: 4 files overwritten (client DLL, server DLL, both AirPit bundles + preset, `material-shaders.arena_airpit.json`); `gpu-run server
stop` so Syncthing could replace the server DLL (host hash = dist after the stop), `gpu-run restart --force` (88 s, 19/19 identical).

Raid 2 (`gpu-run raid --mode pmc --map "AIR PIT" --bots none`, spawned at the **south** `SpawnPoint_Player_South_All` (-16.9, 0, -71.7)):

- `[NavMesh] added navmesh for 'arena_airpit': 10264 vertices, 4684 triangles` (the U2 bake is live after the restart).
- `spawn openings: ... spawn barrier 'Prison_Metal_Fence_01 (2)' at (-23.5, 0.0, 50.5): deactivated 1 part(s), 2 already off, kept 0
  frame part(s)   spawn barrier 'Prison_Metal_Fence_01 (3)' at (-0.4, 0.0, 50.5): deactivated 1 part(s), 2 already off` (second pass
  `0 ... 3 already off`); BUS_DOORS 6 / PASSPORT_CONTROL_DOORS 18 as in raid 1; no ArenaMapPort warning or error.
- `uibridge_teleport dryRun=true` from the south spawn: core5, core3, the south gate, core10 (mezzanine, y 6.4), hub1.11 and core19 all
  pass `navmesh, zone, reachable, room`. From (-0.4, 55) at the end: core16, core17 and core18 pass too - Unity's runtime mesh reaches
  the three shop pockets (presumably round the outside of the building, which the clipped offline view drops); not measured.
- **Walk `usec_spawn -> core5`: "40.8 m away (40.8 m along the navmesh)", arrived** (raid 1: 283.8 m, stuck at the shutter).
- Walk `core3 -> core19`: "11.9 m away (193.9 m along the navmesh)" - the hall-centre cover detour, see the U2 paragraph.
- Screenshots: the south spawn looking north through the opened bus (`BUS_DOORS` leaves gone, the airliner behind), the hall centre
  looking north-west (escalators, `Прибытие` sign, tarpaulin walls, crates; a yellow-green fog-sheet haze at the pallet fence - the
  item 11 StandIn/Hidden A/B is still open), core5 looking north (the east portal: open plywood-lined passage, red passport-hall lights
  beyond) and the east portal from the north side (open, the airliner nose and the NORVAIR sign through it).
- The bright green column of raid 1 is EFT's own exfil marker: `uibridge_inspect find type=ParticleSystem` lists `Air Pit North Gate
  signal` / `Air Pit South Gate signal` next to Arena's flare/fire/explosion systems; `PASSPORT_CONTROL_DOORS/GREEN` and `/RED` are
  off, the 18 red fog sheets under `PASSPORT_CONTROL_EMISSIVE` are on (red lane lights). Not a port defect.
- RainBounds with the play-space set filter (d23efda): `computed from 9434 renderer(s) (excluded 1 oversized, 50 outside the play
  space): center=(-0.95, 26.24, 278.20) size=(779.38, 60.50, 1648.26)` - better than 4.3 x 3.2 km but the runway/forecourt still
  reach in and run on; follow-up 3bd7780 intersects each renderer's bounds with the box before encapsulating (deployed for raid 3).
  F8 under the hall roof at (-1.08, 1.57, 38.30): `height(reversedZ)=45.08` (roof 45 m up, `nothingPhotographed=False`); on the apron
  at the south spawn `height(reversedZ)=0.03` (open sky).
- `Weather (test)/Weather` set to `Rain` for raid 3.

Left at 03:14 (`gpu-run leave`, healed).

### Host raid 3 (03:14-03:19, build 3bd7780, `--bots low`, godmode)

EFT restarted for the clipped-bounds client DLL (`gpu-run restart --force`, 65 s, 19/19 identical). `gpu-run raid --mode pmc --map "AIR PIT"
--bots low`, spawned at the Usec spawn, `uibridge_hook godmode` right after `raidStarted`, in-game clock `Day 11 11:44 AM` (daylight).

- **Bots:** `[BotSpawn] BotZone.Init 'BotZone_Main': markers=14 onNavMesh=14 points=14 maxPersons=-1 patrolWays=1`; `wave fired:
  zone='BotZone_Main' count=3 side=Savage type=assault difficulty=normal time=16.0s`; `TryToSpawnInZoneInner ... -> STARTED`. `uibridge_bots`
  at +60 s: 2 PMCs (`pmcBEAR` at (-50.9, 0, 46.4) then (-37.9, 0, 53.3), `pmcUSEC` at (11.7, 0, 0.6) then (0.4, 4.1, 2.1) on the mezzanine),
  both `PatrolAssault` then `SAIN : Combat Layer`; at +90 s three scavs (`AssaultHaveEnemy`, moving, around (-23..-31, 0, 32..34)). Five bots
  `state Active`, nobody `ActiveFail`, no ArenaMapPort warning or error.
- **RainBounds (3bd7780):** `computed from 9434 renderer(s) (excluded 1 oversized, 50 outside the play space): center=(-12.00, 26.24,
  -2.00) size=(216.00, 60.50, 268.00)` = the play space plus margins, 4096 px → about 6.5 cm per depth pixel (raid 1: 4.3 x 3.2 km);
  `RoofDepthBlack: drew 8748 elevated mesh(es) (11659 submesh draw call(s))`, `depthCamTopY 56.49 farClip 60.50`.
- **Test weather is inert on server-time maps:** `[DaytimeVisualsProbe] WeatherTestOverride: configured='Rain' effective='Server'
  map=arena_airpit serverTimes=True — native backend weather nodes retained`. The rain check (falling streaks + F8) therefore still waits
  for server rain or a Bay5-style override; the key was reset to `Server` afterwards.
- Screenshots: the north spawn hall by day with both exfils listed (`EXFIL01 Air Pit North Gate`, `EXFIL02 Air Pit South Gate`) and the
  open "A" portal; the apron from the south spawn looking south (bus doors open, airliner tail, containers, floodlight mast, overcast sky).

Left at 03:19 (`gpu-run leave`, healed). `General/DeveloperMode` stays on for the test host.

### Open points after raids 1-3 (for the user)

1. **Shop shutters / ShootOut cover (U3 question).** Arena's core points core16 (-45.9, -25.9), core17 (-42.9, 33.8) and core18 (19.6,
   -29.6) sit inside the `TH_Roll_door_02/03_closed`, `DonB_Ramen_closed` and `DonB_Ramen_closed (1)` shop blocks, and the hall centre
   is walled off from the airliner block by the plywood/med-camp/tarpaulin cover (core3 -> core19: 12 m straight, 194 m on the navmesh).
   Together with the portal gates this reads as Arena's ShootOut layout (small area, shops shut, cover added) versus the TeamFight layout
   the spawns, bots and NavMesh belong to. Candidates for `opened_barriers`: the five shop shutters and `Airport_Shop_BS_closed_closed`
   (static closed props without an open variant, so deactivating them opens the shop fronts). The wider "190 walked-through props" set is
   the plan's rejected option (user decision 4.5); the shutters alone would be a middle way. Needs a U3 rebake (~20 min) and a bots raid.
2. `Weather (test)` has no effect on UsesServerTime maps (see raid 3); a rain verification on AirPit needs server rain.
3. Fog sheets: the StandIn haze looks strong in the hall (yellow-green at the pallet fence); A/B with `AirPitFogSheets = Hidden`.
4. `navmesh_bake_layers`: the 3.8 m2 airstairs strip is still the only Arena-walkable floor the port lacks; not checked on the host.
5. core16's pocket, core17's and core18's shops are reachable on the runtime mesh (dry runs pass) but only by long routes.

### Host raid 4 (2026-10-12 09:16-09:35, build bf9d371): the user's spawn report, shutter photos, outline analysis

User report (2026-10-12 morning, after a look at the map): "sieht gut aus", but at the player spawn (-10.24, 76.74) the huge hall
behind it is walkable although it obviously should not be, and red lights float in the air there, presumably from the barrier that
used to stand there. Second request: screenshots of the shutters named in open point 1.

Two raids (`--bots none`, the second with `--time past` and `Lighting (arena_airpit)` ambient raised to 4 / 6 for legible photos,
reset afterwards), 13 screenshots under `work/screenshots/airpit-2026-10-12/` (ignored), the user got sets 07-13 (shutters) and
01/03/05 (spawn, north hall). Findings:

- **North hall.** Facing north from `SpawnPoint_Player_All` the player looks into an unlit hall with a black glass facade; `uibridge_teleport
  dryRun` to (-10, 0.1, 92) passes `navmesh, zone, reachable, room` - the built mesh (bake box to z 92) and the geometry are open.
  Offline (three read-only agents, 20 min): all 67 layer-29 cubes belong to `Arena_AirPit_ShootOut_LevelBorders` and ring the
  ShootOut corridor (northernmost `Cube (9)` at z 50.5); no `LevelBorder*` object exists anywhere; the passport-hall facade
  (`airport_floor_outer_glass_panels`, z 82.47) has a **32.15 m opening at x -28.02..4.13**; the only solid across it is
  `SPAWNS/SPAWN_BOUND_FIGHTER` (layer 0 box, x -36.5..13.5, z 69.2..85.2), which `SpawnPlacementRepairs` must disable because the
  players spawn inside it. Arena's TeamFight NavMesh (component 0, 8,168 m2) ends at z 84.77 and its outline is a **data polygon**:
  `ExcludeLevelBorders` (component `LevelBorder`, `Points[]`) in `Arena_Airpit_TeamFight_Culling` (level30), 12 vertices
  (-2.15, 89.86), (8.84, 83.60), (36.24, 41.15), (36.24, -48.30), (1.44, -70.11), (0.00, -81.96), (-21.03, -82.54), (-26.22, -72.60),
  (-56.03, -41.66), (-56.81, 44.28), (-33.86, 74.16), (-32.41, 88.34) - it contains all 10,511 mesh vertices, all 78 anchors and all
  67 cube centres (12,839 m2). The EFT decompile knows it only as the PerfectCulling sampling outline; Arena's own bounds logic is
  not in the data. A coarse 2D flood model also showed that capping the facade opening alone does not seal the map: the apron is
  open to the runway and the terminal can be walked around.
- **Red lights.** `PASSPORT_CONTROL_EMISSIVE` (root, GateEntity host, no collider) holds exactly 18 `FogSheetSimple_Volumetric_red_small`
  sheets at y 2.79, z 66.05, x -34.3..9.4 (2.1 x 1.4 m quads, `_Color` (1.0, 0.19, 0.21)); nothing else near the spawn is red. The
  client deactivates `PASSPORT_CONTROL_DOORS`' 18 children (RED/RedFog, GREEN/GreenFog, 16 door leaves), so these were the only
  indicators left and they hang over the empty lane line. Arena's `DotAnimator`s (decoded, unnamed fields) switch `RedFog` off and
  `GreenFog` on when the gate opens; `BUS_LIGHT` is three wall-mounted exit signs like Equator's accepted `EXIT_LIGHT_EMISSIVE` and
  stays.
- **Shutters** (photos 07-13): all six are static closed fronts without an open variant: `DonB_Ramen_closed` (west shop, north side
  at z 43, top torn open), `TH_Roll_door_03` (three roll doors, west shop front to the hall at z -10), `TH_Roll_door_02` (two roll
  doors at x -37 behind a tape barrier and a leaning steel sheet), `TH_Roll_door_01` (south wall z -43.5 under the window band),
  `DonB_Ramen_closed (1)` (the "LOUI PEET" shop in the south-east) and `Airport_Shop_BS_closed_closed` (the brick-pillared corner
  shop "БУ..." at x 13-27, z 8-36, with tarpaulin walls in front). **User decision 2026-10-12: keep them closed** ("da sich hinter
  den Shuttern nichts befindet: zulassen"); open point 1 is closed, no U3 for them.

**Fix (lead, 2026-10-12):**

1. `authored_borders` (new descriptor/sidecar input, `AuthoredBorderRepairs.cs`): twelve invisible walls along Arena's outline, the
   north edge pulled from (-32.4, 88.3)-(-2.2, 89.9)-(8.8, 83.6) onto the facade line so the wall stands in the doorway
   (`north-facade-gap` (-28.5, 82.5)-(4.6, 82.5)); each a 0.4 m BoxCollider on `LowPolyCollider` spanning y -1..12, raised in
   `SpawnInfiltrationRepairs`' postfix after the openings, idempotent by object name, parented into an owned scene;
   `Map (arena_airpit)/AuthoredBorders` (default on). Bullets use the ballistic layer and pass.
2. `bake_bounds` x -57..37, z -83..83 (the walls' AABB): bots get no mesh in the hall, on the runway or the forecourt (U3,
   `work/logs/airpit-u3.log`).
3. `opened_barriers` gains `PASSPORT_CONTROL_EMISSIVE` (the 18 red sheets off; no green cue - `GreenFog` would hang in the same
   air, agent recommendation not to add activation code for it).
4. Validator: `authored_borders` shape + sidecar/descriptor mirror (`ArenaBorderDescriptor` literals parsed); tests
   `AuthoredBorders_FormAirPitsClosedOutline_OtherMapsNone`, `AuthoredBordersMatchTheSidecar`, `test_authored_borders_form_a_closed_outline_inside_the_bake_box`.

**Revised the same morning (user: "lässt sich die Passkontrolle nicht wiederherstellen (statt unsichtbarer Wand)?").** A top-down
plot of the area (scratch `airpit_passport_map.py`) shows the layout: the "passport control" is the door line at z 69.4 - eight
lanes, each closed by a pair of `Passport_control_01_Door` leaves at z 66.3/69.1 - with the hall's curved glass walls running from
(-48, 43) and (26, 43) up to the facade at (-31.9, 82) / (9.5, 82); the spawn pocket lies between the door line and the facade,
and the facade opening behind it has no geometry at all. So the real passport control IS the only thing that can close the
north - provided it stays closed. Decision (commit `2a1e0ce`):

- `PASSPORT_CONTROL_DOORS` is no opened barrier any more (closed doors, lane lights red = closed; nothing floats, the sheets sit
  above the door frames); `PASSPORT_CONTROL_EMISSIVE` stays as it is. The 32 door colliders are NavMesh sources again (bake
  sources 2029 under the mask).
- The four points of the north pocket moved south of the door line onto open Arena floor with >= 2 m border clearance:
  `SpawnPoint_Player_All` (-21.5, 0.1, 61.5), `SpawnPoint_Player_Usec` (-1.0, 0.1, 58.5), bots `north-end-west` (-23.5, 0.1, 62.0)
  and `north-end-east` (8.0, 0.1, 62.0); both are >= 10 m from the North Gate exfil box. AI core point 7 (-8.35, 0.02, 76.81)
  inside the pocket stays in `airpit.ai.json`: the sidecar is the extractor's reproducible output
  (`test_tracked_airpit_sidecar_matches_the_live_source`), and an island core point is a known, harmless case (Equator's 13/14).
- `bake_bounds` end at z 70 (x -57..37, z -83..70): no mesh in the pocket or the entrance hall. The authored outline walls stay
  for the apron/runway sides (`north-facade-gap` and its two neighbours are now behind the closed doors and harmless).
- For ArenaMode: the spawn pocket and the doors remain Arena's round-start gate; `Spawns/OpenSpawnBarriers` no longer lists
  the doors, so a mode that wants the pocket as a team start has to open `PASSPORT_CONTROL_DOORS` (18 direct children,
  none named frame) itself and may switch `GREEN/GreenFog` on - see the GateEntity notes above.
- Tests: `test_airpit_spawns.py` records the four moves against the original markers (`MOVED_FROM_MARKER`, south of
  `DOOR_LINE_Z` 66), `MapDescriptorConsistencyTests` three barriers and the z 70 box.
  U4 rebake: `work/logs/airpit-u4.log` (`unity_build_all_20261012_095714.log`: box Center (-10, 3, -6.5) Extents (47, 5, 76.5),
  **`triangulation: vertices=8193 triangles=3775`**, 3 opened barriers; `PORT_VALIDATED` 3775 triangles, `DISTRIBUTION_OK`).

### Host raid 5 (2026-10-12 10:12-10:19, build a262375 + U4, bots None, daytime) - and what the passport control really is

Deploy + `gpu-run server stop` + host hash check (server DLL, client DLL, both bundles, base.json, ai sidecar all MATCH) + EFT
restart (the chained `restart` failed once because the backend was down; `uibridge_quit` + `gpu-run start`, 72 s, 19/19 identical).

- Spawned at the moved `SpawnPoint_Player_Usec` (-1.0, 0, 58.5), facing the open "A" portal 8 m south (photo 14). Log:
  `[NavMesh] added navmesh for 'arena_airpit': 8193 vertices, 3775 triangles`; `spawn openings: BUS_DOORS ... deactivated 6`,
  `Prison_Metal_Fence_01 (2)/(3) ... deactivated 1 part(s), 2 already off` (no passport entry, second pass idempotent);
  **`authored borders: created 12 wall(s) on layer 18 ('LowPolyCollider'), 0 already present, 0 degenerate,
  scene=Arena_AirPit_Scripts`** (second pass `created 0 ... 12 already present`); `RainBounds ... center=(-10.00, 26.24, -6.50)
  size=(174.00, 60.50, 233.00)`; no ArenaMapPort warning or error.
- `uibridge_teleport dryRun`: (-10, 92) and the old spawn (-10.24, 76.74) → `no navmesh within 2 m` (pocket and hall without
  mesh); core5 and the south gate reachable.
- **The passport control is not a barrier.** Facing north from the spawn (photo 15) and from 2 m before the line (16, 17): the
  16 `Passport_control_01_Door` leaves are waist-high turnstile gates, 1.09 x 1.35 m, in eight lanes (x -21.2..-20.1, -17.8..-16.7,
  -16.2..-15.1, -12.8..-11.7, -11.2..-10.2, -7.8..-6.8, -6.3..-5.2, 2.1..3.2 at z 66.3 and 69.1); between the lanes the floor is
  open (gaps of 2 m, and 7-13 m at x -34..-21, -5..2, 3..11.5). The only other collider on the line is the hall's curved glass
  wall (`airport_passport_control_COLLIDER`, two vertical bands at x -41..-34 and 18.7..11.7). Dry runs at (-1, 66..69.6),
  (-10.7, 67.7) and (-14, 67.7) pass; `uibridge_walk` to (-1, 69.6): "5.6 m along the navmesh", arrived, the pocket ahead (photo
  18). Arena's own mesh has no polygons in the band z 66..69.8 (its bake predates these gates or used a geometry that is not in
  the data), which is where the plan's "the doors DO seal the north" came from. So the closed gates are the passport-control
  LOOK (red lane lights above the posts, nothing floats), and the north is closed by the authored facade wall: F10 from the
  spawn hits `ArenaMapPort_Border_north-facade-gap/..._BALLISTIC_twin` at 23.7 m.
- That F10 hit also showed that `BallisticTwinRepairs` had cloned the invisible walls onto the ballistic layer; the twin pass now
  skips `ArenaMapPort_Border_*` (movement-only walls, bullets pass).
- Left at 10:19 (`gpu-run leave`, healed).

Raid 6 (10:23, build `0419ab1`, after a server stop for the DLL swap - host server DLL hash = dist - and an EFT restart): spawned at
the moved `SpawnPoint_Player_All` (-21.5, 0, 61.5); `authored borders: created 12 wall(s)`, **`ballistic twins: added 43
HighPolyCollider twin(s) ... (1909 already had one)`** (the 12 walls no longer counted), `added navmesh ... 8193 vertices, 3775
triangles`, no warning. Left at 10:25.

### Host raid 7 (2026-10-12 13:32-13:40, build 171c278-era client = `e1e27e7` DLL, bots None, daytime): airstairs, fog sheets, the real shaders

Purpose: the open points "airstairs strip" (4) and "fog sheets StandIn vs Hidden A/B" (3), with the U4 build still loaded.

- **Airstairs: closed.** `uibridge_teleport dryRun` at (-7, -30) and (-7, -26) snap onto mesh at y 1.8 and 3.4 (the offline note said the
  3.8 m2 `Passenger_ladder_02` tread strip is missing from the bake); `uibridge_walk` from the foot (-7, 0, -33) to (-7, 2.7, -25.8):
  **"7.7 m away (7.9 m along the navmesh)", arrived at (-7.0, 3.4, -26.4)**. The runtime mesh carries the climb, so bots reach the top;
  `navmesh_bake_layers` stays as it is.
- **Fog sheets: there is nothing to A/B, the sheets render on Arena's own shader.** Offline (`scratchpad/airpit_fogsheets.py`): 127
  fog-sheet renderers, 109 active at raid time (73 Light-scene lamp sheets at y 6.42 / 14.06 / misc, 18 `PASSPORT_CONTROL_EMISSIVE`
  and 18 `RED/RedFog` lane sheets at z 66.05 / 69.4; the 18 `GREEN` ones inactive). The yellow-green haze the user saw is the sheet
  at (19.1, 7.4, -3.0) hanging under the arrivals board in front of Burger Spot (photo 20, from (14, 0, -20) yaw 30). Setting
  `AirPitFogSheetOpacity` 1 -> 0.5 logged **`fog-sheet opacity 0.50: 0 generated texture(s) refreshed`** and changed nothing: no
  material went through the substitute path. The whole raid has no `has no SPT shader — using fallback` line and no per-material
  `fog texture colour=` line; the rebind summary still counts `5x Custom/Billboard_FogSheet_Simple, 4x Cloth/ClothShader, 1x
  Cloth/ClothShader_backface, 1x ANGRYMESH/PBR Rocks/PBR BlendTopDetail (Legacy)` because `_reboundCounts` counts every rebind.
  `uibridge_inspect` on `PASSPORT_CONTROL_EMISSIVE/FogSheetSimple_Volumetric_red_small` -> MeshRenderer -> sharedMaterial
  `396807b58e197f49` -> **`shader = Custom/Billboard_FogSheet_Simple`** (no `Hidden/ArenaMapPort/` prefix, `mainTexture = null`).
  So `ShaderRebind.Rebind`'s direct lookup (`FindLoadedExact(originalName) ?? Shader.Find`) found the REAL shader and the item 11
  stand-ins never ran - on this raid and, by the same missing log lines, on raids 1-6 as well (the item 11 "verification" table
  only quoted the summary counts).
- **Where EFT has them.** UnityPy scan of `EscapeFromTarkov_Data/*` (1,143 files, 200 s, `scratchpad/find_shader_source.py`):
  `sharedassets13.assets` (= BuildSettings 13 `custom_Light`) carries `Custom/Billboard_FogSheet_Simple`, `sharedassets5.assets`
  (`custom_multiScene`) `Cloth/ClothShader` and `Cloth/ClothShader_backface`, `sharedassets15.assets` (`custom_Scripts`)
  `ANGRYMESH/PBR Rocks/PBR BlendTopDetail (Legacy)`; the global `StreamingAssets/Windows/shaders` bundle (the item 11 census) has
  none of them, which is why the census called them missing. Unity stores a shader shared by several scenes in the first scene's
  shared-asset file and later scenes reference it, so these three are loaded whenever any scene that uses them is loaded; they
  were present before the ShootOut rebind pass (13:32:08) in a process whose only raid was this one (EFT's `ShadersFinder` table
  does not list them). Which loaded scene pulls them in (menu/hideout content or the `Reserve_Base_Scripts` donor rig, activated
  at 13:32:07.213, one second before the pass) was not determined; both run on every ArenaMapPort raid.
- Consequences: (1) the fog sheets, cloth and rock on AirPit are Arena's own look, the haze is authored and stays (nothing to decide);
  (2) `AirPitFogSheetOpacity` and the StandIn/Hidden switch were inert in this state - `IsFogSheetMaterial` only knew the stand-in
  name, so `Hidden` would have failed on a cached bundle. Fix (same day): `ShaderStandinPolicy.IsFogSheetShaderName` accepts the real
  and the prefixed name (test `FogSheetMaterialsAreRecognisedByTheRealAndTheStandinShaderName`), the opacity key's description says
  when it has no effect, and `ShaderRebind` logs once per name `'<name>' resolved to EFT's own loaded shader; the stand-in substitute
  is not used` so a process where the substitutes DO run is visible. The stand-in rows stay as the safety net.
- Also in this raid: `barbed wire: attached 12 BarbedWire trigger(s)` (item 15 live on AirPit), `NativeDecals ... source=481 active=471`
  (the 10 missing decals of the plan's 481 sit on inactive objects), `RainAudio arena_airpit: ready after raid audio reset` (item 21).
- Left at 13:40 (`gpu-run leave`, healed). Screenshots `work/screenshots/airpit-2026-10-12/20..22` (ignored).

Same afternoon, without a raid: the `Weather (test)` inertia on server-time maps (open point 2) gets a developer-only opt-in,
`Weather (test)/ApplyOnServerTimeMaps` (default false, read through `DeveloperOptions.Enabled` inside `ModeForSelection`, so the
`DaytimeVisualsProbe` `effective=` line stays truthful; commit `171c278`, six new rows in `MapWeatherRuntimeTests`). The AirPit rain
check (F8 readbacks at the accepted RainBounds plus streak screenshots) is the next host raid.

### Host raid 8 (2026-10-12 13:52-13:56, build e56980a, bots None, forced rain, TOD 14:00): rain cover verified, sealed pockets found

Deploy of `e56980a` (client + a rebuilt but unchanged server DLL; `gpu-run server stop`, hashes over `\\192.168.178.109\C$` equal dist,
`gpu-run restart`, 19/19 identical). Before the raid: `Weather (test)/Weather = Rain`, `Weather (test)/ApplyOnServerTimeMaps = true`
(developer mode on). `gpu-run raid ... --time past` still gave `liveHour=14` (the backend clock, no night); both keys were reset to
`Server` / `false` after the raid.

- **The opt-in works on a server-time map:** `[WeatherTestOverride] applied 'Rain' (map=arena_airpit; nightNeonRain=False)` at
  13:52:06.121 (InitWeather) and 13:52:17.925 (the PVE_OFFLINE re-sync); `[DaytimeVisualsProbe] WeatherTestOverride: configured='Rain'
  effective='Rain' map=arena_airpit serverTimes=True`; `[TimeOfDay]` unchanged (`native server time and weather`).
- **Shader finding reproduced in a fresh process:** four `[RebindShaders] '<name>' resolved to EFT's own loaded shader; the stand-in
  substitute is not used` lines (both cloth variants, the rock, the fog sheet) at 13:52:04.99-05.06, before any content material was
  rebound.
- **Rain cover (item 17), F8 at the accepted RainBounds `center=(-10, 26.24, -6.5) size=(174, 60.5, 233)`, `depthTextureDimension=4096`,
  `nothingPhotographed=False` everywhere:**

  | Spot | Position | `height(reversedZ)` centre (3x3 spread) | Streaks |
  |---|---|---|---|
  | passport hall, Usec spawn | (-1.08, 1.52, 58.37) | **44.34** (44.29..44.38) = glass-and-truss roof | none against the ceiling (photo 23, pitch -35) |
  | south apron | (-14.80, 1.59, -59.24) | **0.03** = ground, uncovered | falling streaks against the overcast sky (photo 24) |
  | hall centre | (-1.16, 1.62, 38.34) | **45.18** (45.15..45.19) = hall roof | none against the roof trusses (photo 25) |

  So the depth-photo heuristic covers the terminal and leaves the apron open, and the shader culls the streaks accordingly. No
  `FullyIndoorCover`-style default is needed for AirPit: the glass roof is a closed roof in the data and reads as cover. Floor wetness
  shows everywhere (global rain value), as on the other maps.
- **Rain audio (item 21), F10:** apron `room=21/OutdoorGeneric environment=Outdoor playerEnvironment=Outdoor rainCover=room-fallback
  ... ambientOutDb=0.00`, `source='Outdoor rain 1' clip='amb_rain_outdoor_strong' playing=True volume=0.5000 route=PrecipitationAmbientOut`;
  hall `room=17/IndoorConcrete environment=Indoor playerEnvironment=Indoor rainCover=room-fallback ... headphonesDb=4.53
  listenerRms=0.010794` with `Indoor rain 1` (`amb_rain_indoor_strong`) playing. The room classification follows the acoustic graph,
  as designed; whether the indoor bed under the glass roof is wanted stays the audibility question of item 21 (host output is muted).
- **Sealed pockets on U4 (new, from the items 14-20 audit):** `uibridge_teleport dryRun` from the Usec spawn: core16 (-45.88, -25.91),
  core17 = bot spawn `north-hall-west` (-42.94, 33.84) and bot spawn `west-south` (-40.1, -27.7) -> **`not reachable on foot ... navmesh
  island`**; core18 = bot spawn `east-south` (19.59, -29.58) -> **`the walk there passes through a wall or fence`**. On U2 raid 2 had
  reached them "round the outside of the building"; that route lies outside Arena's outline and the U4 box/walls removed it on
  purpose. The three bot spawns sit inside the closed shop fronts (user decision: shutters stay closed) and must move onto the main
  component; the three core points stay as documented islands (sidecar = extractor output). Fix in progress, see "Sealed-pocket
  anchors on U4".
- Barbed-wire contact (item 15) was not testable: the 12 `bruno_helix` boxes sit at y 3.9 on top of the mezzanine fence; the nearest
  walkable mesh is 2 m away ((1.4, 3.9, 29.5) and (1.4, 3.9, 31.8) answer `no navmesh within 2 m`, (1.4, 3.9, 27.3) snaps to (0, 3.2,
  26.1)). Attach stays verified (12 triggers every raid); contact is EFT's own `BarbedWire` behaviour.
- Left at 13:56 (`gpu-run leave`, healed). Screenshots `work/screenshots/airpit-2026-10-12/23..25` (ignored).

### Sealed-pocket anchors on U4 (2026-10-12)

Why: raid 8 (above) answered four bot-spawn / core-point probes with "navmesh island" or "passes through a wall or fence". A bot that
spawns in a sealed pocket stays there for the raid, so the three bot spawns involved moved onto the main component; the three core
points stay as documented islands. Offline work on the built U4 bundle (`work/build/airpit/arena_airpit_navmesh.bundle`, read-only,
8193 vertices / 3775 triangles = 2209 Detour polygons in **90 connected components**); no rebake, no descriptor, bake-input or
`airpit.ai.json` change, the NavMesh is unchanged.

**How the points were matched.** `tools/validate/navmesh_diff.load_bundle_mesh` + `navmesh_oracle` (adjacency incl. cross-tile portals,
components ranked by xz area). Each point (y 0.1, core points 0.02, hubs 0.003, mezzanine core points 3.6-6.4) is matched to the
polygon whose xz footprint contains it, among fan triangles whose surface height at that spot lies within +-1.0 m of the point's y
(nearest footprint by xz distance when none contains it; the result was identical with +-2.5 m). Every point matched at 0.00 m
horizontal distance except core 7 (6.81 m from the nearest polygon = off the mesh, as documented). Because Arena's authored outline
walls are runtime colliders the raw bundle components do not know, a second labelling applies them: polygons outside the 12-vertex
outline are dropped and every portal that a wall segment crosses is cut ("effective" components).

| Component | Polygons | Area (xz) | Extent | Holds |
|---|---|---|---|---|
| raw 0 = **main** | 1551 | 8814.8 m2 | x -54.9..37, y 0..6.4, z -77.7..70 | hall, both aprons, mezzanine decks, and the strip at the box edge beyond the east outline wall (x 36.1..37) |
| raw 4 | 35 | 846.2 m2 | x -57..-35.7, z 15.9..70 | north-west shop front (DonB_Ramen_closed) and the wedge beside it |
| raw 5 | 31 | 643.3 m2 | x -57..-37, z -44.5..-10.4 | south-west shop front (TH_Roll_door_02/03) |
| raw 1, 3 | 92 / 15 | 4406.3 / 1923.4 m2 | y 8.55 | roof decks of the terminal |
| raw 2 | 120 | 2025.8 m2 | y 0.05-0.10, z -83..-46 | ground south of the building, 1675 m2 of it outside the outline (the rest on the flanks of the south apron) |
| raw 6-89 | 365 (84 components) | 0.0-149.4 m2 each | y 0..6.7 | small decks, enclosed floors, treads and slivers; no point of this audit sits on any of raw 1-3 or 6-89 |
| effective **main** | 1474 | 7730.0 m2 | same box | raw 0 minus 1084.7 m2 that lie outside the outline or behind a wall |
| effective SW / NW / SE pocket | | 641.8 / 589.8 / 532.5 m2 | SE: x 11.6..37, z -44.5..-16.4 | the three closed shop fronts; the SE one (DonB_Ramen_closed (1), "LOUI PEET") hangs on raw main only by a 1.35 m link that runs outside the east wall |

| Group | Points | On main (raw) | On main (effective) | Not on main |
|---|---|---|---|---|
| bot spawns | 14 | 12 | 11 | `north-hall-west` raw 4; `west-south` raw 5; `east-south` effective SE pocket |
| player spawns | 4 | 4 | 4 | none |
| AI core points | 19 | 16 | 15 | core 17 raw 4; core 16 raw 5; core 18 effective SE pocket; core 7 off the mesh (6.81 m) |
| patrol hubs / sub points | 3 / 35 | 3 / 35 | 3 / 35 | none (all at 0.00 m, y 0.05-0.30) |

The 14 bot spawns (raw component, border clearance in U4): north-end-west 0, 4.48 m; north-end-east 0, 6.09; north-approach 0, 5.44;
north-hall 0, 2.34; **north-hall-west 4**, 5.03; east-north 0, 3.19; east-mid 0, 2.04; **east-south 0 (effective pocket)**, 5.19;
west-mid 0, 3.81; **west-south 5**, 2.70; center-east 0, 1.61; plane-south 0, 4.45; south-apron 0, 5.32; south-end 0, 1.39. Player spawns:
all 4.40, usec 7.04, bear 2.98, south-all 3.19 m. Core points 8-11 sit on the mezzanine decks of the main component (surface y 3.67, 4.25,
6.29, 3.65); core 4 (clearance 0.32 m) and core 9 (0.62 m) are tight but connected.

**Cross-check with the raid 8 dry runs** (13:52, `uibridge_teleport dryRun` from the Usec spawn (-1, 0, 58.5)):

| Host answer | Point | This analysis |
|---|---|---|
| "not reachable on foot (navmesh island)" | core16 (-45.88, 0.1, -25.91) | raw 5, 643 m2 island |
| same | core17 = bot `north-hall-west` (-42.94, 0.1, 33.84) | raw 4, 846 m2 island |
| same | bot `west-south` (-40.1, 0.1, -27.7) | raw 5 |
| "the walk there passes through a wall or fence" | core18 = bot `east-south` (19.59, 0.1, -29.58) | raw main, but its only route (148 m offline) crosses the east outline wall (x 36.24) through a 1.35 m link at (36.55, 0.05, -0.50), returns inside the wall near (35, -13) and follows the strip east of the shop (x 34-36) down to z -44.3 before turning into it; the player-height linecast (1.3 m, `TeleportPolicy.WalkCheckHeight`) over the layer 18/30 bake colliders plus the 12 authored walls hits `BORDER/east` twice (out and back) |

The offline replica of the dry run (polygon corridor, funnel corners, linecast at 1.3 m against the layer 18/30 bake sources and the
authored walls) reproduces all four answers and returns "reachable, 0 crossings" from the Usec spawn for the 11 other bot spawns, the
15 other core points (core 7 is off the mesh) and all 38 hub and sub points. East-north (16.6, 26.8) lies in the
Airport_Shop_BS_closed front, which is shuttered on its west and south sides but open to the north: effective main, offline path from
the Usec spawn 43 m, no crossing. U2's "round the outside of the building" reach (raid 2: bigger box, no outline walls) was a route
of this kind; it ran outside Arena's outline and was removed on purpose by the U4 box and the walls, and the east-south link above is
the remnant at the box edge. The sealed shop fronts are the closed shutters (user decision: they stay closed; shop table in "Host
raid 4").

**Moves** (ids unchanged; chosen on the effective main component, >= 2.0 m U4 border clearance, >= 10 m from both exit-box centres,
>= 8 m (xz) from every other bot and player spawn, ground floor, no solid collider of the 0.4-1.7 m band within 4 m, inside the 12-vertex
outline with >= 20 m margin, inside Arena's own component 0 with >= 1 m of Arena-mesh clearance; offline dry run from both player spawns
without a wall crossing):

| Spawn | Old (pocket) | New | Move | U4 clearance | Arena-mesh clearance | Nearest exit centre | Nearest other spawn |
|---|---|---|---|---|---|---|---|
| `north-hall-west` | (-42.94, 0.1, 33.84), north-west shop | (-32.69, 0.1, 30.15) = Arena's patrol point hub2.7 | 10.9 m | 4.20 m | 4.22 m | 38.5 m (North Gate) | west-mid 28.1 m |
| `west-south` | (-40.1, 0.1, -27.7), south-west shop | (-31.0, 0.1, -25.5) | 9.4 m | 5.35 m | 5.15 m | 42.9 m (South Gate) | plane-south 19.7 m |
| `east-south` | (19.59, 0.1, -29.58), south-east shop | (6.0, 0.1, -40.5) | 17.4 m | 4.85 m | 4.92 m | 19.7 m (South Gate) | plane-south 21.7 m |

Roles kept: west/north-west of the hall (open floor about 4 m east of the shop front, on Arena's own patrol point), south-west (open
floor 6 m east of the roll-door shop, clear of the plane), south-east (floor south-east of the aircraft tail, west of the "LOUI PEET"
front, 5.6 m from its curved shutter line). Arena has no marker or core point on the main component in the south-west or south-east (its 20 spawn
markers all sit in the two end pockets), so those two are open-floor picks; the north-west one is Arena's own patrol point. Considered
and rejected: the strip north of the south-east shop ((25, -11), 3.6 m clearance) is 10 m from east-mid and a duplicate of its role;
hub0's points are 12 m from center-east and east-mid; points along the pocket fronts clear 2.0 m only just. Offline path from the
Usec spawn: 64 / 118 / 188 m (straight 43 / 89 / 99 m); from the Bear spawn: 171 / 151 / 43 m. The south-west floor is a 151 m walk from
the Bear spawn (straight 53 m), over uibridge's detour rule (2.5 x straight + 15 m), so a dry run from THAT spawn would answer "only
reachable by a long detour"; it is the hall's topology (plane-south, unchanged, is 341 m from the Bear spawn by the same model), not a
seal. Replaying all 14 bot spawns and the 4 player spawns (new positions) from both player spawns finds no wall crossing and no
other detour. Start the host dry runs at the Usec spawn.

**Island decision for core 16 / 17 / 18.** `airpit.ai.json` is the extractor's reproducible output (pinned by
`test_tracked_airpit_sidecar_matches_the_live_source`) and an AI core point on an island is a known harmless case (Equator 13/14,
AirPit core 7): the three core points stay unedited; what matters is that no spawn sits on them any more. All three still lie inside the
bake volume, so `AiAnchorsTests.AirPitAnchorsSitInsideTheDescriptorBakeBounds` holds unchanged (a comment now names them).

**Files.** `tools/maps/airpit.bots.json` (three positions, the moves recorded in `_comment`), `ArenaMapPort.Server/db/arena_airpit/base.json`
(regenerated with `python tools/server/make_base_json.py --map D:/ArenaMapPort/tools/maps/airpit.json`; the diff is exactly those three
positions), `tools/tests/test_airpit_spawns.py`, `ArenaMapPort.Server.Tests/AiAnchorsTests.cs` (comment only). Test changes:
`MOVED_FROM_CORE_POINT` now records the four core-point-derived moves (east-north 1.6 m, west-south <= 15.0 m, north-hall-west <= 11.0 m,
east-south <= 17.5 m); new `SEALED_POCKETS` boxes (four boxes for the three pockets: the effective north-west pocket is 589.8 m2 at
z 16.0..55.5 and its wedge beside the shop front, x -51.2..-44.2 / z 43.6..59.2, got its own box x -51.2..-48.0, z 43.6..56.2 after
review, which covers 24.6 of the wedge's 38.2 m2; the other ~1 m sliver along the main-component edge (main floor starts at x -47.7)
is left to the bundle test, and the south-west and south-east boxes cover their pockets completely) and `SEALED_POCKET_ORIGINS` (old position, move bound, role side);
`test_no_spawn_stands_in_a_sealed_shop_pocket`, `test_sealed_pocket_moves_keep_the_role_the_distances_and_the_exits` (>= 5 m moved, role
side, >= 10 m from both exit centres, >= 8 m from every other spawn) and `test_every_spawn_lies_on_the_main_component_of_the_built_navmesh`
(skipped without the ignored bundle or UnityPy: every spawn on raw component 0, <= 0.4 m off the ground floor, >= 1.0 m border
clearance, >= 2.0 m for the three moved). The old positions fail these guards (north-hall-west raw 4, west-south raw 5, east-south in the
south-east box). Results: `python -m unittest discover -s tools/tests -p "test_*.py"` Ran 1071 tests, OK (skipped=44) when the pockets were first added, Ran 1113 tests, OK (skipped=44) after the review fixes (the wedge box
and a corrected table row; the count difference is other work in the same tree, the fixes add no test);
`dotnet test ArenaMapPort.Server.Tests -c Release` 702 passed, 0 failed. `docs/MAP-RECIPE.md` documents no AirPit positions (generic recipe
only), so it is unchanged; `docs/PLAN-AIRPIT.md` Appendix A and `tools/validate/navmesh_probe_points_airpit.json` keep the 2026-10-11 draft
positions as history (they were already stale after the north moves).

**Not done / next.** Nothing is deployed: `base.json` is read at server start, so the next host check needs the deploy script, a server
restart and (as usual) the hash comparison. Then `uibridge_teleport dryRun=true` from the Usec spawn to the three new points must answer
"reachable" without the wall message, and one raid with bots should show `[BotSpawn]` wave lines and `uibridge_bots` positions on the hall
floor (none inside the three shop fronts).

### Host raid 9 (2026-10-12 14:41-14:45, build 9bf4f89 base.json, bots low): the moved spawns on the deployed U4 mesh

Deploy of the regenerated `base.json` (server stopped, hashes equal dist, `gpu-run restart`, 19/19 identical), `gpu-run raid --bots low`,
spawned at `SpawnPoint_Player_South_All` (-16.9, 0, -71.7).

- `uibridge_teleport dryRun` from the south spawn to the three new points: **(-32.69, 30.15) -> `would teleport to (-32.7, 0.1, 30.1)`,
  (-31.0, -25.5) -> `(-31.0, 0.0, -25.5)`, (6.0, -40.5) -> `(6.0, 0.1, -40.5)`**, each `snapped <= 0.1 m · checks navmesh, zone,
  reachable, room` - no island, no wall message (the old points answered `navmesh island` / `passes through a wall or fence` in raid 8).
- Bots on U4 (the first bots raid since the U3/U4 rebakes; raid 3 ran on U2): `[BotSpawn] wave fired: zone='BotZone_Main' count=3
  side=Savage ... providedValid=1/1`, three `BotOwner.Create -> 'Bot1..3' startCorePoint=0` + `ActivateBotCallback ... bot registered`;
  `uibridge_bots` at 14:42:40: all three `Active, PatrolAssault` at (-24, 0, 57)..(-23, 0, 60) (the `north-end-west` spawn); 30 s later at
  (9.8, 0, -11.2), (-2.7, 4.7, 2.1) and (-16.7, 5.4, 4.4) - 50-70 m across the hall and up the mezzanine. No `ActiveFail`, no
  ArenaMapPort warning or error. Left at 14:45 (`gpu-run leave`).

## Item 13 phase 1 - native ambient audio: extractor, contract, package v6 (2026-10-12, 14:00-15:30)

Done by one implementing agent plus two adversarial reviewers and a fix pass (workflow `wf_cd72c8a7-b60`); the lead re-ran the
suites and switched the package pins. No Unity, no host.

- **Extractor generalised.** `tools/extract/extract_bay5_audio.py` no longer carries the Equator/Bay5 whitelist, the `equator`
  boolean and the literal census/identity/state pins: supported maps, source scene, schema, census (players/support by kind, banks,
  clips), group coverage and the schema-2 state pins (inactive/disabled/loop players, per-group mixer list in source order) come from
  the native contract through `native_contract_loader` (`find_audio_contract`, `audio_contract`, `expected_group_members`,
  `validate_audio_states` replaces `validate_equator_states`). Helper names/signatures (`Reader`, `deref`, `script`, `identity`,
  `hierarchy`, `validate_wav`, `script_hash`, `export`, `validate_graph`) unchanged; `room_reverb.py`, `extract_multiflare.py`,
  `extract_spatial_audio.py` still import. **Equator/Bay5 output byte-identical** (HEAD exporter vs new exporter on the live Arena
  0.6.0 source: Equator 66/66 files, Bay5 107/107 files identical; `work/airpit-parity/parity_gate.py check` UNCHANGED). The
  review's one finding (the Equator electricity loop was pinned by leaf name only in the retired literal) is fixed: the contract
  `loop_players` now carries id + leaf name for Equator and `validate_native_features.check_audio_group_states` checks both.
- **Registration.** `tools/extract/native_features.py`: `AUDIO_EXPORTERS['arena_airpit'] = ('ambient_audio', extract_bay5_audio.export)`;
  `for_plan` cross-checks the plan's `sound` role against the contract's scene/feature before the multi-minute extraction (Equator and
  Bay5 keep calling `export()` directly from their own extractors).
- **Contract.** `tools/extract/native_contracts/airpit.json` `audio`: feature `ambient_audio`, schema 2, scene `level39`, root
  `ArenaMapPort_AirPitNativeAudio`, players `{AmbientSoundPlayer: 26}`, support `{AmbientSoundPlayerGroup: 2}`, banks 9, clips 61,
  `group_coverage all_players`, states `{grouped_players 26, disabled [], inactive {}, group_mixers [CommonAmbOutEffects,
  CommonAmbInEffects], loop []}`.
- **Package `work/extract/airpit-native-v6`** (accepted input from now on; ignored): `extract_map.py --plan tools/extract/plans/airpit.json
  --native-features --target D:/SPT41/EscapeFromTarkov_Data --output work/extract/airpit-native-v6`, `refresh_native_volumes.py` (no-op),
  `validate_extract.py --package ... --extractor-timeout 900` -> `OK -- all checks passed` incl. two reproducibility runs and the
  package-freshness check (0 diffs, 939 s). Manifest `native_audio {schema_version 2, players 26, banks 9, clips 61, sha256 fa50a114...}`,
  `native_features.required = [ballistics, ambient_audio]`; `audio/` = `native-audio.json` + 61 WAV (44,058,104 B). **Diff against v5:**
  scenes 5/5, meshes 2487/2487, materials 435/435, textures 805/805, navmesh_ref, raincover byte-identical; only `manifest.json`
  differs (the audio block, the `required` list, `extracted_utc`). Data facts reproduced: groups Out (4 players, `CommonAmbOutEffects`,
  far_* sources with 50-100 m ranges) then In (22 players, `CommonAmbInEffects`), 0 of 26 players with `volume < deltaVolume`
  (Equator's problem class is absent), all enabled and active, no Loop/playOnAwake/mixer override.
- **Tests.** `test_airpit_audio.py` (new, 13: synthetic 26-player graph, swaps/reorders/omissions/state and census drift, CLI),
  `test_native_contracts.py` (AirPit audio golden pins, the ballistics-only branch kept alive on a patched contract copy, v5 asserted
  rejected, real v6 checked), `test_native_features_orchestration.py` (registry, `for_plan` cross-check, v6 block reproduction),
  `test_equator_audio.py` (frozen-literal test pins the retired numbers), `test_bay5_audio.py`. Whole suite 1113 tests OK.
- **Pins switched to v6 (lead):** `test_airpit_bake_inputs.py`, `test_airpit_spawns.py`, `test_mode_objects.py` + regenerated
  `tools/maps/airpit.modes.json` (only `source_package` changed), `navmesh_diff.py` default, `build_remaining.ps1`, docstrings,
  AGENTS/HANDOFF/PLAN. Consequence: an AirPit build from v5 now fails `validate_native_features` ('incomplete required feature
  contract'); a build from v6 needs phase 2 (importer + client) first.
- **Phase 2 (done 2026-10-12 15:30-17:40, workflow `wf_782fcb51-70b`: implementer, three reviewers, fix pass; 721 .NET tests, client
  Release DllOnly build 0 warnings):**
  - `ArenaMapPort.Client/AmbientAudioProfiles.cs` (new, pure C#, linked into Server.Tests): one profile per released map (root name,
    player/active/controller counts, shoreline/seasons flags, original-activation pins, ordered group routes `GroupName -> bus/mixer`,
    which route carries the room-reverb reroute). Equator 25/23, one In group (effects + reroute, dogs inactive, `electricity` loop);
    Bay5 24/24, `AmbientPlayersGroup` -> AmbientOut/`CommonAmbOutEffects` + the water group -> `CommonAmbOutBypass`, shoreline, seasons;
    AirPit 26/26, `CommonAmbientOutGroup` (4) -> AmbientOut/`CommonAmbOutEffects`, `CommonAmbientInGroup` (22) -> AmbientIn/
    `CommonAmbInEffects` (effects + reroute). `AmbientAudioProfilesTests` (27 AmbientAudio tests in total) pin the table, the invariants
    and the importer's root-name/case pairing.
  - `Bay5AmbientAudio.cs`: map via the profile table (no key whitelist; returns silently when the bundle carries no audio root, i.e. the
    pre-U5 AirPit bundle), `BindConfig` loops the table (Equator/Bay5 key names, defaults, ranges and texts byte-identical; new
    `Audio (arena_airpit)` keys PlaybackDiagnostics, AmbientGainDb, AmbientVolumeOffset, AmbientRangeMultiplier, hidden
    AmbientDefaultsRevision), profile-driven `ValidateGraph`, seasons only with `UsesSeasons`, shoreline only with `HasShoreline`,
    **one `AmbientBusLease` per bus** (AirPit: Out then In, both 0 dB; the lease entry is refcounted per (mixer, parameter), so
    PrecipitationAudio and NativeSpatialAudio coexist without a fight), reverb reroute on the profile's effects route (Equator In,
    AirPit In; Bay5 none). `NativeSpatialAudio` takes the room-tone In lease only when the driver does not (`LeasesBus`), unchanged for
    Equator/Bay5. `AmbientAudioDefaults`: `arena_airpit = 4 / 0.4 / 3` **provisional (to be ear-tuned by the user)**, `Migrate` never
    rewrites an AirPit mix (no legacy), still throws for unlisted keys; `ConfigRetirementCheck` covers the new block.
  - `Bay5AudioImporter.cs` (Unity, compiled only by the U5 build): Equator/Bay5 gate removed; root name and asset folder per key
    (`ArenaMapPort_AirPitNativeAudio`, `NativeAudio/AirPit`), schema/identity from the package manifest, counts from `native_audio`
    (no literals), `preserveStates = (schema == 2)`, per-group mixer pairing table (`CommonAmbientInGroup -> CommonAmbInEffects`,
    `CommonAmbientOutGroup -> CommonAmbOutEffects`, `AmbientPlayersGroup -> CommonAmbOutEffects`, water -> `CommonAmbOutBypass`) checked
    against the package `mixerName`, generic membership validation. Equator/Bay5 authoring unchanged.
  - Expected U5/host lines: Unity `[ArenaMapPort][NativeAudio] authored inactive stock graph map=arena_airpit players=26 banks=9 clips=61
    support=2`; raid `[NativeAudio] exterior bus leased neutral=True`, `interior bus leased neutral=True`, `user range x3.00 ... 26
    player(s)`, `user mix gainDb=4.0 offset=0.400`, **`ready players=26 started=26 ... mixers=CommonAmbOutEffects,CommonAmbInEffects
    shoreline=none`**, `[RoomReverb] AirPit owned ambient route=... players=22`.
  - **Open decision (user): Out/In crossfade by acoustic room.** Today both AirPit buses stay at 0 dB for the whole raid (the same
    compromise Equator/Bay5 ship with one bus): the four far outdoor sources (50-100 m ranges) are audible inside within range, the
    interior sources outside. EFT's own mechanism is `EnvironmentSoundBlendSystem` + `AmbientSoundBlender` (AirPit's Sound scene has one
    of each; room data: 24 rooms, 22 of 23 indoor rooms with `OutdoorAmbientVolume` 0.0, one 0.5, 1 s fades). Options: A port the blender
    (extractor + importer + client, largest); B client-only: follow `ListenerCurrentRoom` (as PrecipitationAudio does) and blend the two
    bus leases with the room's fade; C leave as is. Decide after the host listen test.
  Phase 3 next: Unity `-FullMap` rebuild from v6 (U5, `work/logs/airpit-u5.log`; rebake expected unchanged since audio objects carry no
  colliders), client Release build (stages bundles + acoustics), validators, deploy, host listen test, Equator/Bay5 smoke raids.

### Item 13 phase 3 - U5 build, deploy and the dead-plugin session (2026-10-12, 16:52-17:40)

- **U5** (`work/logs/airpit-u5.log`, `unity_build_all_20261012_165255.log`, 17 min): the importer compiled and authored the graph on the
  first run - **`[ArenaMapPort][NativeAudio] authored inactive stock graph map=arena_airpit players=26 banks=9 clips=61 support=2`**;
  bake inputs unchanged (`3 opened_barriers`, mask 0x40042A00, box from the descriptor), **`triangulation: vertices=8193 triangles=3775`**
  (identical to U4), modifiers 20/12 stripped, MonoScript patch 15 (now incl. `EFT.SoundBank`, `AmbientSoundPlayer`,
  `AmbientSoundPlayerGroup`), `validate_bundle_roundtrip` incl. the audio proofs OK, scenes bundle **855,037,777 B** (+30.4 MB for the
  61 PCM clips; navmesh 120,516 B unchanged). Client Release build staged it; `validate_port --map airpit --package
  work/extract/airpit-native-v6 --distribution` -> `PORT_VALIDATED ... scene_bundle_bytes 855037777`, `DISTRIBUTION_OK 3 maps 9 bundles
  3 sidecars`. Deploy: client DLL, scenes bundle and the (rebuilt, unchanged) server DLL; host hashes equal dist.
- **Session 17:15 died at plugin start, nothing to do with the audio:** the raid load aborted with `Can't load bundle:
  maps/arena_airpit_preset.bundle HTTP/1.1 404 Not Found` and `uibridge_config` showed 0 ArenaMapPort keys. The host's live BepInEx log
  (`\\192.168.178.109\C$\SPT41\BepInEx\LogOutput-SPT-GPU-2-2026-10-12_17-15-38.log`, the file sits in the BepInEx root until UiBridge
  moves it to `logs/` at exit) has, right after `Loading [maschine-ArenaMapPort 0.1.0]`: **`IOException: Die Datei, die ersetzt werden soll,
  kann nicht entfernt werden`** from `System.IO.File.Replace` <- `CompactConfiguration.SaveArchive` <- `Initialize` <- `Plugin.Awake`. The
  developer archive was held by another process for a moment (Syncthing is the candidate: the cfg had just been written by the raid 8
  key resets) and the exception escaped Awake, so no Harmony patch and no `BundleHost` existed - EFT asked the server for the preset
  bundle and got 404. Fix `438f179`: `SaveArchive` retries the replace, falls back to an in-place copy and otherwise keeps the merged
  values in memory with one `[ArenaMapPort][Config]` warning (retried at the next start); `Plugin.Awake` degrades to the public file if
  `Initialize` still throws. `ConfigRetirementCheck`'s locked-archive case (which until now ASSERTED the IOException) expects the warning
  and the in-memory merge instead. Redeployed (client + server DLL, hashes equal), EFT restarted.

### Host raid 10 (2026-10-12 17:24-17:27, build 438f179 + U5, bots None): native ambient audio live on AirPit

- `[ArenaMapPort][NativeAudio] waiting for raid listener and native mixers; map=arena_airpit players=26 active=26` (17:24:59), then at
  17:25:25 **`exterior bus leased neutral=True`**, **`interior bus leased neutral=True`**, `user range x3.00: max distance scaled on 26
  player(s); e.g. 'indoor_wind_howl' 30->90 m; 'indoor_wind (4)' 5->15 m; 'water_drops' 3->9 m`, `user mix gainDb=4.0 offset=0.400:
  adjusted 26 player(s), dip capped on 0; e.g. 'metal_squeak_deep_1' 0.200-0.126->0.717-0.200`, **`ready players=26 started=26
  seasonalSilent=0 season=Summer mixers=CommonAmbOutEffects,CommonAmbInEffects shoreline=none listener=(-16.82, 1.54, -71.58)
  sourceInactive=0`**, `[RoomReverb] AirPit owned ambient route=AmbCommonEffects, players=22, original indoor bus=0.00dB` (the In group
  rerouted, the 4 Out players stay on `CommonAmbOutEffects`). `uibridge_config`: the eight `Audio (arena_airpit)` keys exist
  (`AmbientGainDb 4`, `AmbientVolumeOffset 0.4`, `AmbientRangeMultiplier 3`, `AmbientDefaultsRevision 2`, PlaybackDiagnostics, plus the
  earlier NativeSpatialAudio/NativeRoomReverb/RainVolume). No ArenaMapPort warning or error.
- F10 at (-1.1, 0.6, 20) in the hall: 26 `[NativeAudio] source=` rows, 22 on `mixer=AmbCommonEffects` and the 4 far_* on
  `CommonAmbOutEffects`, `indoor_wind (8)` playing at 29.5 m, the others between their random draws (`playing=False virtual=True`, as
  on Equator/Bay5); `[RainAudio] ... ambientInDb=0.00 ambientOutDb=-0.27` (both buses open; rain 0.063 Low from the backend).
- Audibility is the user's listening pass (host output is muted); the provisional mix 4 / 0.4 / 3 and the Out/In crossfade question
  stay open for that pass. Left at 17:27.

### Host raids 11-13 (2026-10-12 17:26-17:31, same process): Equator and Bay5 regression, AirPit -> Equator -> Bay5 -> AirPit

Same EFT process as raid 10 (bundle cache in play), bots None each, `gpu-run leave` between raids, no ArenaMapPort warning or error in
any of them:

| Raid | Map | Evidence |
|---|---|---|
| 11 | Equator | `[Acoustics] arena_equator: prepared rooms=21, portals=50, bake pairs=210, routes=31676` (21/50 = the three earlier host sessions; the graph file holds 23/55 before the client drops invalid rooms), `[NavMesh] added navmesh for 'arena_equator': 10404 vertices, 4572 triangles`, `[NativeDecals] map=arena_equator enabled: source=348 active=346 registered=346`, `[RoomReverb] arena_equator: native room controller active`, `Equator owned ambient route=AmbCommonEffects, players=23`, **`[NativeAudio] ready players=25 started=23 seasonalSilent=0 ... mixers=CommonAmbInEffects shoreline=none map=arena_equator sourceInactive=2`** (unchanged: the two dogs inactive) |
| 12 | Bay5 | `[Acoustics] arena_bay5: prepared rooms=40, portals=85` (as before), `[NativeDecals] map=arena_bay5 enabled: source=499 active=468`, `[Water] enabled: target=Arena_Bay5_Main/Sea/Plane textures=4 shader=Hidden/WaterForSSR`, `[NavMesh] ... 22117 vertices, 10213 triangles`, shoreline `range origin aligned ... radius=75.00` + `initial calculation`, **`[NativeAudio] ready players=24 started=23 seasonalSilent=1 season=Summer mixers=CommonAmbOutEffects,CommonAmbOutBypass shoreline=stock map=arena_bay5`** - byte-identical to the three earlier Bay5 sessions in the mirrored logs |
| 13 | AirPit again | `[RebindShaders] Arena_AirPit_ShootOut: pass complete — 351/351` (the bundle was reloaded after the two other maps), `[Acoustics] arena_airpit: prepared rooms=24, portals=94`, `[NavMesh] ... 8193 vertices, 3775 triangles`, `[NativeDecals] map=arena_airpit enabled: source=481 active=471 registered=471 ... renderer=-1511394` (a fresh manager, no `refusing a second manager`), **`[NativeAudio] ready players=26 started=26 ... mixers=CommonAmbOutEffects,CommonAmbInEffects map=arena_airpit`** |

So the profile-driven driver leaves Equator and Bay5 exactly where they were, and the AirPit graph starts again after a map change.
Item 13 is log-verified on the host; what remains is the user's listening pass (mix 4 / 0.4 / 3, Out/In crossfade by room) and the
ballistics shooting check (item 13a) that no raid has done yet.

## User feedback (2026-10-12 evening): the north-east shop shell, the hall glass, the hall-centre cover

The user walked the map and reported: (1) the shop at the `east-north` spawn is empty and "apparently not meant to be entered",
presumably a roll door toward the entrance hall was removed (the NW shop has one); (2) the hangar panes are still too dark, almost
black and opaque; (3) what is the "hall-centre cover"? Two read-only agents (workflow `wf_8e6adfe2-89f`) and host screenshots
(`work/screenshots/airpit-2026-10-12/27..33`, ignored; 30/31 are the user's own daytime view of the south glass wall from the
mezzanine at (-3.9, 5.1, 4.0)).

### North-east shop (Burger Spot): an empty shell, not a sealed room

- Interior x 13.25..32.98, z 7.9..43.0 (about 20 x 35 m), floor `airport_floor` at y 0, **no prop at all** (only four graffiti decals,
  the inactive `Graffiti_Art_21 (3)`, `AirpitArenaLevelSettings` at (21.6, 0.4, 23.2) and the active grenade tray `Grenades_Points (2)`
  at (14.29, 1.38, 35.58)). West wall = `Airport_Shop_BS_closed_closed` (five lowered shutter fields between brick pillars, solid at
  y 0.3..2.8, tarpaulin/plywood in front), south wall closed, east wall = `Airport_shop_upper_part_02` x 32.98..33.62. **North side
  open in two places:** Arena's own breach x 17.64..22.22 through `airport_passport_control_broken_glass_wall` (jagged glass edge at
  y 1.6..3.0, broken frames (6)/(7)/(8)) and the unclosed NE corner x 26.54..33.6 (into the pocket east of the passport hall).
- **No roll door was removed on the north side**: no door-like object, active or inactive, exists within x 10..40, z 5..52 in the five
  package scenes, in the raw `level324`, nor in the old furnished `level38` (`Arena_Airpit_Main_hall`, in no preset) - that scene has
  the same breach plus tape fences and glass shards. What Arena did remove is the shop INTERIOR: the old `Airport_Shop_BS` had 1,448
  props and 4 `Inside_Door_Wood_11_R` + 2 `KitchenDoors_1`; the current scene has 105 objects and an empty `DOORS/BURGER_SPOT`
  container. The west bays that were open in the old collider (z 8.3..12.9, 36.8..42.5) are shuttered now. The NW shop
  (`DonB_Ramen_closed`) has its roll fronts on the hall side AND an opening to the north (x -57.4..-50.2) into the sealed wedge; its
  interior is equally empty (1,226 -> 95 objects).
- Arena's TeamFight mesh covers 267 of 442 m2 of the room (its holes trace the old interior walls, 39 % of hole cells within 0.45 m of
  an old wall vs 3 % of covered cells) and crosses the breach at (20.0, 0.02, 42.8); core points 2 (15.14, 26.76) and 4 (26.15,
  8.56) are inside. The built U5 mesh covers 439 m2 and leaves the room only through the breach (3.9 m) and the NE corner (6.1 m);
  blocking the breach alone would cut 1,869 m2 (room + pocket) off the main component.
- **Decision (lead, 2026-10-12): leave the room open, move the spawn.** The breach is Arena's own doorway, the room is a dead end on the
  main component and holds an active pickup tray and two core points. Sealing it properly would need either a cloned shutter prop
  (new "added props" importer mechanism + rebake) or an authored wall plus a bake-only NotWalkable box (new `BakeNavMesh` list +
  rebake); both are available if the user wants the shell closed. `east-north` moved (16.6, 26.8) -> **(8.0, 0.1, 26.5)** (7.1 m from
  core point 2; built-mesh clearance 4.4 m, Arena-mesh 4.5 m, 14.4 m from `north-hall`, about 40 m from the North Gate; sidecar +
  `base.json` + `test_airpit_spawns.py`: `MOVED_FROM_CORE_POINT`, a fourth box in the no-spawn list, `SEALED_POCKET_ORIGINS`).
  Core points 2 and 4 stay in `airpit.ai.json` (the room is reachable, they are not islands).

### Hall glass: the rebind forced the stand-in's render queue onto the transparent shader

- The user's view (photo 30/31, from the mezzanine at (-3.9, 5.1, 4.0) facing south): the whole south glass wall of the hall and the
  skylight band read 12-13/255 at noon although the exterior behind them is 180-198/255 and the authored alpha is about 0.63.
- Materials: AirPit's hall envelope is ONE single-LOD mesh `Airpit_Building_Glass_LOD0` (28,368 m2 per side: side walls x -75.5 /
  +52.9, south facade z -45.5 y 12..28, skylight band z 42.8..50 y 37..46.5) on `cf1419e2` `Airpit_glass_dirty_inside_top`
  (`Global Fog/Transparent Reflective Specular`, `_AlphaOffset -0.33`, `_DirtStrength 0.6`, `_GlobalReflectionStrength 2.0`); the
  passport control, stair and elevator glass use `99230e6d` `Airpit_glass_dirty`; the south facade also carries the shard decal
  `77832cd9` `Airpit_broken_glass_atlas_01` (explicit queue 3000). The DXBC of EFT's and Arena's shader is identical apart from fog
  and thermal: `alpha = saturate(MainTex.a ^ _DirtStrength + _AlphaOffset)`, `_Color.a`/`_ReflectColor`/`_Cube` are never read, blend
  SrcAlpha/OneMinusSrcAlpha, ZWrite off, **queue Transparent (3000) by default**. The properties reach the bundle intact.
- **The defect:** the four hall-glass materials have `render_queue -1` (shader default) in the package; `MaterialImporter.cs:162`
  records the queue the STAND-IN shader reports (2000) and `ShaderRebind.cs:381-383` forced that onto the real shader after the swap,
  so the transparent glass drew in the opaque phase (before the sky/clouds at 2510-2530 and before the screen-space inputs the shader
  samples exist). Equator's three and Bay5's two `Global Fog` materials with queue -1 carry the same sidecar value; on Equator the
  affected roof glass has alpha about 0.07, so it was never visible there.
- **Fix (client, no rebuild):** `ShaderStandinPolicy.EffectiveRenderQueue(mapKey, recorded, shaderDefault)` - on AirPit a recorded
  Geometry queue (2000) on a shader whose own default differs is the stand-in's value, so the material gets `renderQueue = -1`
  (shader default); explicit queues stay; Equator/Bay5 keep the recorded value (accepted look). `ShaderRebind` logs once per name
  `sidecar queue 2000 is the stand-in default, shader default is 3000; using the shader default`. Test
  `AirPitRestoresTheShaderDefaultQueueWhereTheSidecarCarriesTheStandinDefault`. Host check: F10 on the south wall must show
  `renderQueue=3000` for `Airpit_Building_Glass_LOD0`, screenshot at the user's spot; if the panes stay dark, the next step is a
  developer-only `GlassRepairs` with `AlphaOffsetDelta` / reflection / tint knobs (not built yet). Host raid 14 (19:16, build
  `e225182`, night on the backend clock): the rebind now logs the restored default for FIVE names - `Global Fog/Transparent Reflective
  Specular` 2000 -> 3000, `p0/Cutout/Bumped Diffuse` -> 2450, `p0/SoftCutoutBumpedFresnel` -> 2454, `p0/Reflective/Bumped Specular
  SMap Transparent Cutoff` -> 2450, `Decal/Ultra Deferred Decal Of God 3000` -> 2005 (all were drawn at 2000 before; -1 is what Arena
  authored for them, so the cutouts and decals now sit in their shaders' own phases too); F10 on the facade decals shows
  `renderQueue=2450` / `3000`. The daylight look of the south wall is still to be checked (the backend clock gave night; the UiBridge
  time selection is being fixed by the user). The new `east-north` (8.0, 26.5) passes `dryRun` from the Usec spawn. The opaque north facade
  (`airport_floor_outer_glass_panels`, `Airpit_Airport_Glass_Tiles_01` on an opaque `p0` shader with an interior `_Cube`) is dark by
  authoring; making it see-through would be a design change (it would reveal the facade opening).

### "Hall-centre cover"

The plywood / chain-link / container cordon under the airliner between the east half (core3) and the west half (core19) of the hall:
photos 27 (from (3, 0.6, -17) facing north-west: plywood wall and fence under the fuselage) and 28 (from core19 facing south-east:
NORVAIR container, corrugated panels, plywood). Bots path 194 m around it for 12 m of straight line (analysis in "Open points after
raids 1-3" and the hall-cover audit: options A leave, B open `tarpaulin_wall_03`, C also `plywood_wall_02 (1)`; each option is a
rebake). **User decision (2026-10-12 late): the cover stays** - no rebake; the 194 m bot detour is accepted. In the same message the user confirmed the glass after the queue fix ("Scheiben sehen gut aus") and the rain ("Regen passt"), and asked why the four shops are closed and empty when they should be walkable - investigation `wf_c92d33bb-843` (preset scene binding per mode, interior census 0.5.4 / 0.6.0 / `Arena_Airpit_Main_hall`).

### Where the four shop interiors are (2026-10-12 late, three read-only agents, workflow `wf_c92d33bb-843`)

- **Preset binding (all 36 preset bundles parsed, typetrees complete, identical to the 0.5.4 dump):** `arena_airpit_preset.bundle` holds
  the root `Arena_AirPit.ScenesPreset` (Scripts 36, ShootOut 324, ShootOut_Light 325, Sound 39, DesignStuff 40) plus three children:
  TeamFight (29, 28, 30, 33; used by TeamFight AND Coop), Tournament (32/114, 116, 106/115; used by ShootOut Solo/Duo/Trio) and
  LastHero (139, 29, 140, 30). **Every mode loads the same hall, `Arena_AirPit_ShootOut` (324).** `Arena_Airpit_Main_hall` (38),
  Building (35), Background (34), Light (37), Tournament (31/113), LastHero (138/137) and `Arena_Airpit_new` (207-212) are referenced by
  no preset, no child preset, no bundle and no code string (binary scan of 783 files). The port's five content scenes plus the four
  role scenes are exactly the TeamFight set; the 2026-09-15 census chose "root + TeamFight child" at mode level, no hall was compared.
- **The interiors were removed by BSG, not hidden:** level324 is Building 35 (96.4 % identical) merged with a reduced Main_hall 38 in
  the same frame (roots at (-186.107, 18.038, -23.594); 10,212 of 38's 27,764 objects coincide with 324 to 0.01 m). Shop subtrees:
  `OO/PROPS/INDOOR/SHOPS` 5,487 objects in 38 vs 300 in 324 (Burger Spot 1,391 -> 77, Tonni Hellfinger 1,541 -> 78, Don B Ramen
  1,179 -> 79, Loui Peeton 1,375 -> 65: signs, pillars, tape and decals only); the shop `DOORS` (1,140 objects, 8 `Inside_Door_Wood`,
  2 `KitchenDoors`) are empty containers in 324; the `PHYSIC` root (1,865 physics props) does not exist in 324; the open shells
  `Airport_Shop_BS`, `DonB_Ramen`, `LP_Shop`, `TH_Roll_door_01..03` were swapped for static `*_closed` meshes (SE got a copy of the
  Ramen shell where `LP_Shop` stood); `TH_Shop_indoor` (SW interior mesh + colliders) is still in 324 but inactive. Interior light
  groups `ChinaPlace*`/`BurgerSpot*`/`LouiPeeton*`/`HELLFINGER` (118 lights) exist in level37 and are 0-2 lights in 325. The 0.5.4 spike
  package already had the closed shells (0 differences to 0.6.0 in all five scenes), so this predates 0.5.4.
- **Why it looks "meant to be walkable":** the AI and design data were authored for the furnished hall: TeamFight AI (33, bake stamp
  2025-08-11) and LastHero AI (140, 2025-10-02) NavMeshes cover 36-55 % of each shop footprint with holes tracing the OLD interior walls
  (41-59 % of hole cells within 0.4 m of a level38 wall vs 8-11 % near a 324 wall) and cross the closed shutters in 4/6/5/8 places; core
  points 2, 4 (NE), 17 (NW), 18 (SE), 16 (SW); 8 of 23 Savage spawn markers, 36 mine points and 24 cover places inside shops; the
  spatial-audio rooms `ServerRoom_AudioRoom_{Burger_Spot, Loui_Peeton, Don_Bulyon, German}`; the TeamFight pickup tray in the NE shop;
  six LastHero player spawns inside shops; culling exclude volumes on old fixtures; and the menu parallax images 3/4 show the FURNISHED
  Don B Ramen interior (counter, neon, lanterns, ink panels), which only level38 holds. The preset binding (2026-08-18) is about ten
  months younger than the AI bakes. Nothing in the TeamFight scenes opens the shops (GateEntity/DotAnimator only on the bus and
  passport gates), so Arena's own TeamFight on this build most likely plays with closed, empty shops too (inference; the live game was
  not observed). The ShootOut border ring excludes all four shops, which is presumably why they were emptied.
- **What walkable shops would need (estimate L):** a slice import from the unreferenced level38 (+ lights from 37) into the generic
  pipeline (`extract_map.py` refuses slices today; only `extract_equator.py` has `--slice`): `OO/PROPS/INDOOR/SHOPS/*` (5,486),
  `SOO_LOD0/DOORS/*` (1,139), `PROJECTION/INDOOR/SHOPS/*` (220), the old open shells and the hall-prop groups inside the footprints;
  de-duplicate 324's own 300 shop objects and 106 projection decals (88 % are a subset); deactivate/replace the six `*_closed` shells
  (`opened_barriers`), activate `TH_Shop_indoor`; the 118 shop lights with their CullingLightObjects; new ballistic/decal pins (+about
  370 bake colliders, +9.7k triangles); rebake (the sealed pockets and the three moved bot spawns would become moot, Arena's own
  anchors would fit again). Then host raids. **User decision (2026-10-12 late): "ja, bau die shops aus level38 ein"; also check for other lost decoration ("es fehlen zumindest einige Decals").** Phase 0 = three read-only agents (full diff 38/37 vs 324/325 incl. decals, slice design for the generic pipeline, bake/gameplay consequences), then implementation in phases.
  Figure: `work/screenshots/airpit-2026-10-12/34-shops-topdown-324-vs-054-vs-level38.png` (ignored).
