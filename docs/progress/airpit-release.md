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
