# Remaining Arena maps: source coverage and port progress

Verified: 2026-09-15, 20:34 CEST. The task extends the existing Equator/Bay5 geometry spike to every other independent Arena layout with locally available source scenes. **All twelve additions are extracted, built, validated with current schema-2 receipts and staged in `dist/`.** All twelve `validate_port.py --distribution` checks passed. The full 14-map distribution, Release DLL hashes, baseline preservation and deployment dry run also passed. Live deployment and GPU raid validation have not been performed.

## Source inventory

Read-only source root:

```text
D:/Battlestate Games/Escape from Tarkov Arena/EscapeFromTarkovArena_Data
```

The census read 18 Arena preset bundles and resolved their full scene paths against the local `globalgamemanagers` BuildSettings registry. Where a path has multiple compiled copies, the source plans select the full-data copy rather than matching only a scene basename. `tools/extract/plans/<stem>.json` stores the selected preset/mode, exact scene paths and indices, anchor scenes, NavMesh reference scenes and explicit exclusions.

The result is 12 additional eligible independent layouts plus the two existing maps: 14 location definitions in `ArenaMapRegistry.All`. An entry in this registry establishes identity and routing, not a successful map build.

| Additional location key | Source preset | Selected source mode | Terrain grids | Imported scene count, excluding authored Scripts |
|---|---|---|---:|---:|
|`arena_airpit`|`arena_airpit_preset.bundle`|TeamFight|0|5|
|`arena_airpit2`|`arena_airpit_2.0_preset.bundle`|CheckPoint|0|4|
|`arena_autoservice`|`arena_autoservice_preset.bundle`|TeamFight|0|4|
|`arena_bowl`|`arena_bowl_preset.bundle`|TeamFight|0|7|
|`arena_prison`|`arena_prison_preset.bundle`|TeamFight|0|6|
|`arena_railwaystation`|`arena_railwaystation_preset.bundle`|TeamFight|0|6|
|`arena_yard`|`arena_yard_preset.bundle`|TeamFight|0|6|
|`arena_iceberg`|`arena_iceberg_preset.bundle`|CheckPoint|0|6|
|`arena_factory`|`arena_factory_preset.bundle`|ShootOutDuo|0|4|
|`arena_custom`|`arena_custom_preset.bundle`|LastHero|2|5|
|`arena_shoreline`|`arena_shoreline_preset.bundle`|LastHero|3|5|
|`arena_woods`|`arena_woods_preset.bundle`|LastHero|2|5|

AirPit2.0 has its own complete layout and source identity `Arena_AirPit2`. Customs, Shoreline and Woods use the Arena-specific `Arena_NY_*_LH` adaptations; these are not the ordinary EFT location presets. A selected mode supplies the relevant environment/dressing and source anchors; Arena match rules are not implemented by selecting that mode.

### All other preset dispositions

| Preset | Disposition |
|---|---|
|`arena_equator_tdm_02_preset.bundle`|Existing Equator port. Preserve `arena_equator`, MongoId and existing bundle/sidecar/base-JSON paths.|
|`arena_bay5_preset.bundle`|Existing Bay5 port. Preserve `arena_bay5`, MongoId and existing artifacts.|
|`arena_bay5_tournament_2x2_preset.bundle`|Bay5 ShootOutDuo mode child referenced externally by Bay5; no new independent environment.|
|`arena_draft_preset.bundle`|Mixed placeholder containing Factory and Iceberg/Sanatorium mode references; LastHero also points at missing `Arena_TEMP`. No separate location.|
|`arena_preset.bundle`|Generic test preset with no ServerName/GameModes, not a canonical playable map preset.|
|`arena_saw_preset.bundle`|Preset metadata exists, but its source scenes are unavailable locally. Keep outstanding; do not replace it with the separate Woods adaptation.|

### Saw: exact unavailable source

Saw references 13 scene entries containing 12 unique missing full paths. None resolves in the local BuildSettings registry, and the source maps directory has no corresponding scene bundles. All 12 are under `Assets/Content/Locations/Arena_Woods/`:

```text
Arena_Woods_Scripts.unity
Arena_Woods_Main.unity
Arena_Woods_Terrain.unity
Arena_Woods_Roads.unity
Arena_Woods_Light.unity
Arena_Woods_DesignStuff.unity
Arena_Woods_Sound.unity
Arena_Woods_TeamFight_DesignStuff.unity
Arena_Woods_TeamFight_Culling.unity
Arena_Woods_TeamFight_AI.unity
Arena_Woods_LastHero_AI.unity
Arena_Woods_LastHero_DesignStuff.unity
```

The TeamFight culling path is referenced twice. The present `Arena_NY_Woods_LH` source does not fill these missing Saw paths. Saw requires additional source data before it can be assessed for a port.

## Build progress and evidence

All twelve current packages report zero extraction errors and have successful Unity run records. Every addition has a schema-2 receipt containing fourteen required artifact hashes, plus successful current and staged `PORT_VALIDATED` results with three verified authored player starts. This includes all seven terrain grids across Customs, Shoreline and Woods. Earlier schema-1 results remain historical evidence and cannot authorize current resume or staging.

AirPit2.0's material-slot refresh preserved 1,066 of 1,068 package files byte for byte; only its affected scene JSON and manifest changed. Its corrected Unity rebuild and full schema-2 validation passed, as did Customs' corresponding refreshed output. Yard completed the same mandatory validation sequence before client staging and the final distribution checks.

| Stem | Extraction | Unity build | Schema-2 receipt | Staged validation | GPU raid |
|---|---|---|---|---|---|
|`autoservice`|Complete package, errors 0|Complete|Verified: `PORT_VALIDATED`|Passed|Not run|
|`airpit`|Complete package, errors 0|Complete|Verified: `PORT_VALIDATED`|Passed|Not run|
|`airpit2`|Complete package, material slots refreshed|Corrected rebuild complete|Verified: `PORT_VALIDATED`|Passed|Not run|
|`bowl`|Complete package, errors 0|Complete|Verified: `PORT_VALIDATED`|Passed|Not run|
|`prison`|Complete package, errors 0|Complete|Verified: `PORT_VALIDATED`|Passed|Not run|
|`railwaystation`|Complete package, errors 0|Complete|Verified: `PORT_VALIDATED`|Passed|Not run|
|`yard`|Complete package, errors 0|Complete|Verified: `PORT_VALIDATED`|Passed|Not run|
|`iceberg`|Complete package, errors 0|Complete|Verified: `PORT_VALIDATED`|Passed|Not run|
|`factory`|Complete package, errors 0|Complete|Verified: `PORT_VALIDATED`|Passed|Not run|
|`custom`|Complete package, material slots refreshed|Complete|Verified: `PORT_VALIDATED`|Passed|Not run|
|`shoreline`|Complete package, errors 0|Complete|Verified: `PORT_VALIDATED`|Passed|Not run|
|`woods`|Complete package, errors 0|Complete|Verified: `PORT_VALIDATED`|Passed|Not run|

Per-map evidence is in schema-2 `work/build/<stem>/full-map-validation.json`, `validation-inputs.json`, `editor_verify.json`, the complete Unity build log and the current `validate_port.py` results. The final local summary is `.unlazy/remaining-maps/reports/final-map-validation.json`; staged outcomes are recorded in `work/logs/remaining-maps/final-gates.log`. Receipts bind exact content hashes. Changing package, consumed source, relevant code/settings or toolchain inputs invalidates acceptance even if an older receipt or successful log remains present. Generated evidence remains local under ignored `work/` and `.unlazy/`; a fresh checkout must produce its own evidence.

The final Python suite passed **86 tests** (`work/logs/remaining-maps/final-python-tests.log`). Final gates also passed all **eight PowerShell Unity-log tests**, the two BC6H checks and the ten input-snapshot tests; the snapshot tests were rerun after the final narrow input guard. The BC6H/snapshot cases are part of the Python coverage, not additional distinct tests to add to the suite total. All 17 executable final gates passed in `work/logs/remaining-maps/final-gates.log`, covering baseline preservation, distribution, the twelve staged maps and these focused checks.

The server Release test run passed **228/228 tests, with zero failures or skips** (`work/logs/remaining-maps/final-server-release-tests.log`). The final pass used direct `dotnet test` for the server tests in Release, followed by direct client `dotnet build -c Release`. The client checked all twelve current receipts before copying and finished with **zero warnings and errors** (`final-client-release.log`). Per-map staged validation, the exact file set and both Release DLL SHA256 comparisons then passed. The documented `build_remaining.ps1 -Stage` offers the same workflow; this final pass used the direct commands.

The completed `dist/` contains **72 files, totaling 11,054,200,717 bytes**. The deployment dry run exited successfully and planned 72 new files, zero overwrites and zero already-identical files; it copied nothing into `D:/SPT41` (`work/logs/remaining-maps/final-deployment-dry-run.log`).

Equator/Bay5 retain their baseline artifacts from earlier passes. The final baseline check confirmed all eight original artifacts are byte-identical and the six checked descriptor/base/extractor files match their pre-expansion content. Their output paths and identity constants remain preserved. [Earlier Bay5 results](bay5-port.md) and [Equator distribution history](dist-manifest.md) remain unchanged.

## Build all additional maps

The resumable runner now implements the complete sequence below. Invoke it directly from PowerShell so `-Maps` receives an array rather than a comma-containing native-process argument.

```powershell
Set-Location D:/ArenaMapPort
./tools/build_remaining.ps1 -Stage
```

Its default map order is `autoservice, airpit, airpit2, bowl, prison, railwaystation, yard, iceberg, factory, custom, shoreline, woods`. It defaults to `-ReproTimeoutSeconds 1800`, reuses a map only when its current schema-2 receipt and all recorded input bytes validate, and runs only one Unity build at a time. A schema-1 receipt does not qualify for resume; the runner executes the build/validation pipeline again. `-Stage` requires all 12 registered additions to validate, runs server tests in Release (also staging the server), builds/stages the client Release, then verifies every added map with `--distribution`. Before reporting success, it runs `validate_distribution.py` to check the exact bundle/sidecar/database set and both built-to-staged Release DLL hashes. It never performs live deployment. Per-map logs are written to `work/logs/remaining-maps/<stem>-pipeline.log`.

For a selective bundle rebuild, reusing existing complete extraction packages:

```powershell
./tools/build_remaining.ps1 -Maps autoservice,airpit -Rebuild
```

`-Rebuild` rebuilds bundles; it does not overwrite or refresh a completed extraction package. An incomplete nonempty extraction directory is rejected for inspection instead of silently overwritten. Even with a selected map subset, `-Stage` requires the complete 12-map addition set to validate.

## Reproduce an additional map

Run from `D:/ArenaMapPort` in PowerShell 7. Use the existing local Python environment with UnityPy/numpy/Pillow and the installed Unity 2022.3.43f1 editor. Build inputs from Arena and SPT are read-only. `build_all.ps1` holds the named mutex `Local\ArenaMapPort.UnityBatch` across both GameRefs synchronization and the Unity editor run, releasing it in cleanup. Independent map validation can proceed concurrently after that protected section. Direct editor or reference-sync launches must not bypass this shared-project coordination.

The generic commands below use `autoservice` as an example. Substitute any stem from the 12-map table. The extraction plan and map descriptor are different inputs: the plan selects source content; the descriptor defines permanent SPT identity and scene order.

### 1. Extract selected source content

```powershell
Set-Location D:/ArenaMapPort
$stem = 'autoservice'
$sourceRoot = 'D:/Battlestate Games/Escape from Tarkov Arena/EscapeFromTarkovArena_Data'
python tools/extract/extract_map.py --plan "tools/extract/plans/$stem.json" --source $sourceRoot --output "work/extract/$stem"
if ($LASTEXITCODE -ne 0) { throw 'Extraction failed' }
```

The manifest records the source plan path/digest and exact scene registry. Extraction errors are hard failures. Complete terrain maps use the implemented terrain extension; nine other new maps have no native Terrain components.

### 2. Prepare or inspect the SPT preset and server base

The full-map orchestrator now regenerates these inexpensive outputs automatically before validation. The standalone commands remain useful to prepare or inspect them independently:

```powershell
python tools/preset/patch_preset.py --map "tools/maps/$stem.json"
if ($LASTEXITCODE -ne 0) { throw 'Preset generation failed' }
python tools/server/make_base_json.py --map "tools/maps/$stem.json"
if ($LASTEXITCODE -ne 0) { throw 'Server base generation failed' }
```

These tools use copies/templates from the local SPT installation. Outputs are `work/build/preset/arena_<stem>_preset.bundle` and the descriptor's path under `ArenaMapPort.Server/db/`. A generic `build_all.ps1 -FullMap` regenerates the base and preset from the current donors after bundle identity repair, including with `-SkipUnityBuild`; callers do not need a separate preparatory run. The subsequent validators check and hash the regenerated outputs.

### 3. Build and validate the full map

```powershell
pwsh tools/build_all.ps1 -MapKey "arena_$stem" -MapDescriptor "tools/maps/$stem.json" -PackageDir "D:/ArenaMapPort/work/extract/$stem" -OutputDir "D:/ArenaMapPort/work/build/$stem" -FullMap -ReproTimeoutSeconds 1800
if ($LASTEXITCODE -ne 0) { throw 'Full-map build or validation failed' }
python tools/validate/validate_port.py --map $stem
if ($LASTEXITCODE -ne 0) { throw 'Current full-map receipt verification failed' }
```

`build_all.ps1` synchronizes the editor GameRefs, runs Unity, scans the log, applies the mandatory MonoScript assembly-identity repair, and runs extraction reproducibility, bundle roundtrip, preset and whole-registry descriptor checks. `-FullMap` requires NavMesh baking and editor verification, enforces the authored Scripts scene followed by the exact extraction scene list, and forbids the slice workflow's missing-scene allowance. The 1800-second value increases each extraction reproducibility run's allowance; it does not weaken its comparisons or prove an exact build duration.

Only after every required check and the final input comparison passes does the script write schema-2 `full-map-validation.json`. The receipt includes `validation-inputs.json`, `unity-run.json` and the actual Unity log; generic full-map builds also include the read-only SPT collision-matrix snapshot `target-physics.json` and the generated `spawn-placement.json` evidence. Spawn-grounding/clearance and fresh NavMesh checks must describe the actual authored starts; the corresponding runtime raid check remains separate.

#### Schema-2 input binding and revalidation

`tools/validate/port_inputs.py` captures `validation-inputs.json` after Unity, mandatory bundle identity repair and base/preset regeneration, immediately before the mandatory validators. It records paths, sizes and SHA256 hashes of complete files in these groups:

- Every file in the extraction package, including scene JSON, materials, texture data and mesh data.
- The Arena serialized dependency closure for the selected source plan, plus the resource streams used by its mesh/texture objects. Complete source files are hashed, not selected object metadata or file timestamps; unrelated files elsewhere in the Arena installation are not consumed inputs.
- The actual SPT Managed dependency closure, collision settings, preset and server-base donors, and explicit GameRefs fixup references.
- Relevant repository extractor, validator, bundle, preset/base, PowerShell, shared/client/server and Unity importer/shader code; source plans, descriptors and build settings. File-set membership is checked as well as file contents, so additions and removals are visible.
- Unity project/package settings and the selected installed package implementations, plus the actual Unity/Python executables, closure tool and installed Python dependency code/binaries and versions.

After extraction/source/reproducibility, bundle roundtrip, preset and registry checks pass, the orchestrator rehashes and compares these inputs. Any changed byte or applicable file-set change prevents a new accepted receipt. `validate_port.py`, including resume and staged checks, rehashes the inputs again. It reuses the proven Arena dependency list only after rehashing every recorded source file; changed parent, plan or code bytes cannot silently retain acceptance. Documentation, tests and generated import caches are outside the declared validation input sets; changing relevant product code, even a comment, changes its file hash.

An explicit full-map attempt archives the prior receipt and input snapshot under superseded names before proceeding. Failure therefore leaves no current accepted receipt. Schema-1 receipts are rejected; neither editing the schema number nor capturing hashes alone upgrades them. The entire mandatory validation sequence must run against the current inputs.

When the existing Unity output is suitable for complete revalidation, use the same full-map command with the two explicit skip switches:

```powershell
pwsh tools/build_all.ps1 -MapKey "arena_$stem" -MapDescriptor "tools/maps/$stem.json" -PackageDir "D:/ArenaMapPort/work/extract/$stem" -OutputDir "D:/ArenaMapPort/work/build/$stem" -FullMap -SkipGameRefsSync -SkipUnityBuild -ReproTimeoutSeconds 1800
if ($LASTEXITCODE -ne 0) { throw 'Complete revalidation of existing Unity output failed' }
python tools/validate/validate_port.py --map $stem
if ($LASTEXITCODE -ne 0) { throw 'Current schema-2 receipt verification failed' }
```

This path checks the prior Unity run provenance and log, reapplies mandatory identity repair, regenerates base/preset outputs, captures the current inputs and reruns every mandatory validator before the final input comparison and new receipt. It does not merely refresh hashes. If the existing bundles no longer satisfy the current inputs or checks, rebuild them through the normal command.

### 4. Finish every additional map, then stage Release outputs

The client Release target stages all 12 additional maps from explicit entries in `tools/maps/AdditionalMapBuilds.props` and validates each receipt first. Complete every additional map's steps 1-3 before using Release staging as the final distribution step. Equator's `work/build/full`, Bay5's `work/build/bay5`, and their existing presets/sidecars must also remain available.

```powershell
dotnet build ArenaMapPort.Client/ArenaMapPort.Client.csproj -c Release
if ($LASTEXITCODE -ne 0) { throw 'Client Release or map staging failed' }
dotnet build ArenaMapPort.Server/ArenaMapPort.Server.csproj -c Release
if ($LASTEXITCODE -ne 0) { throw 'Server Release staging failed' }
dotnet test ArenaMapPort.Server.Tests/ArenaMapPort.Server.Tests.csproj -c Release
if ($LASTEXITCODE -ne 0) { throw 'Server tests failed' }
```

Server test builds can stage the referenced server project; keep `-c Release` for that command too. All of these outputs stay inside the repository's `dist/` tree.

### 5. Verify staged artifacts and preview deployment

```powershell
$additionalMaps = @('airpit', 'airpit2', 'autoservice', 'bowl', 'prison', 'railwaystation', 'yard', 'iceberg', 'factory', 'custom', 'shoreline', 'woods')
foreach ($map in $additionalMaps) {
    python tools/validate/validate_port.py --map $map --distribution
    if ($LASTEXITCODE -ne 0) { throw "Staged map verification failed: $map" }
}
python tools/validate/validate_distribution.py
if ($LASTEXITCODE -ne 0) { throw 'Distribution file set or Release DLL hash check failed' }
pwsh tools/deploy-to-spt.ps1 -SptRoot D:/SPT41
if ($LASTEXITCODE -ne 0) { throw 'Deployment dry run failed' }
```

`validate_distribution.py` requires exactly 42 bundles, 14 shader sidecars and the registered server database set. It obtains each project's effective Release `TargetPath` from MSBuild and compares that DLL with its staged copy by SHA256; a missing or stale client/server DLL fails the check. Also reconcile the unchanged Equator/Bay5 artifact hashes as described in the [current distribution contract](../dist-manifest.md). The deployment command above has no `-Confirm`, so it only prints the copy plan. Actual live promotion is a separately reviewed final step; it is not performed by the build sequence.

## Supported scope and fidelity

### Scene and identity contract

The extension follows the inherited spike: ordinary geometry, materials, existing collision, lights, LOD groups, minimal SPT scene components, an authored player start and a fresh SPT-compatible NavMesh. Source `Scripts` runtime is replaced by the authored Scripts scene. Source sound, AI/culling data and mode-specific runtime components have explicit exclusions in each source plan. Disabled source GameObjects and supported components retain their state.

Permanent map keys/MongoIds, preset routes and base-JSON paths come from `tools/maps/*.json` and `ArenaMapPort.Shared/ArenaMapDescriptor.cs`. The whole-registry validator checks both. The existing Equator/Bay5 identities and the Equator compatibility filenames remain unchanged.

### Source format compatibility

The generic extractor applies scoped compatibility adapters and restores the previous decoder, mesh writer and component builder after success or failure. Equator/Bay5's existing extractor entrypoints remain unchanged.

- **BC6H textures:** UnityPy 1.25.2 requests an RGBA mode that Pillow's BC6H decoder does not support. The adapter selects its supported RGB mode for this format, preserving the established base-mip and vertical orientation conventions. Export still uses the spike's 8-bit texture representation; it does not preserve the source HDR radiance range.
- **Compressed meshes:** source PackedBitVector data supplies positions, triangle indices and every supported present normal, tangent, color and UV channel. Float colors are already normalized and are not divided by 255 again. Source investigation covered eleven Iceberg meshes with 77,411 vertices and 234,387 indices; independent reconstruction matched every exported float32 channel and index. Unsupported layouts fail explicitly, including skin/blendshape payloads, non-triangle topology, nonzero base vertices, non-2D UVs and three or more packed UV channels affected by the installed decoder's offset bug.
- **Empty source meshes:** the source census identifies 31 package records across Customs (16) and Shoreline (15), representing 16 distinct source Mesh assets. They have no vertex/index or baked collision payload and zero bounds. They remain empty AMPM meshes with their source component references. The adapter records source object hashes and conversion facts in the manifest's `mesh_compatibility` entries.
- **Mixed material arrays:** the selected sources contain exactly two affected renderers: AirPit2.0 `level343` renderer 12834 / GameObject 2215 has nine real materials followed by null (slot 9, ten submeshes); Customs `level360` renderer 200008 / GameObject 40907 has one real material followed by null (slot 1, two submeshes). The generic wrapper preserves the full source order, including these trailing nulls, and adds `material_slot_preservation` provenance only for affected renderers. A fresh source census checks both the provenance and emitted arrays; deleted, shifted or reassigned null slots fail. Other arrays retain the inherited extraction behavior.

The extractor also emits 38 source-null MeshCollider references across Bowl (2), Iceberg (7), Prison (8), RailwayStation (2) and Yard (19). Validation accepts them only when `mesh_id` is explicitly present as JSON null and a fresh source census proves the exact scene/GameObject and count. Missing keys, nonempty unresolved mesh IDs and unproven nulls remain errors. Assembly retains those collider components with null `sharedMesh`, their flags and physics material; bundle validation checks them against the live source. This does not expand the inherited omission of source-null MeshFilters or all-null material arrays.

These source and adapter checks establish format fidelity for the inspected assets. They do not replace each map's completed extraction, Unity import, roundtrip and schema-2 receipt checks. Material-slot package refreshes require corresponding current built output before their final receipts can pass.

### Mesh import and bundle verification

A mesh asset ID identifies its source file and path ID, not a geometry hash. Generic imports therefore decode every current package mesh, including an existing ID, and update existing Unity assets while retaining their GUIDs and references. Creation is batched with balanced editor cleanup and saving. This prevents a warm import from silently retaining obsolete geometry; the original Equator/Bay5 import path keeps its prior behavior.

For every in-scope generic mesh, the bundle roundtrip validator compares actual built positions, normals, tangents, colors, UV channels, submesh topology and indices with an independent AMPM package reader. It checks channel presence and dimensions, numeric values and exact indices, rather than accepting matching IDs or vertex counts alone. Source-empty assets must remain exactly empty. Numeric comparisons allow serialization tolerance; nonfinite values fail except for exact signed UV infinities already present in the inspected source/package data. New infinities, NaNs and nonfinite geometry are rejected.

Mixed material slots and emitted null colliders are also compared in the actual built scene at their exact source object identity and hierarchy. Aggregate renderer or collider counts alone cannot establish these reference checks.

### Terrain ground and surface

Customs/Shoreline/Woods contain seven 1025x1025 source height grids, each covering 700x700m with 180m vertical scale. Conversion reconstructs height, holes, mesh collision and continuous per-terrain UVs. The surface uses actual 12-layer source diffuse-height and packed NormalSAO arrays, all three 1024x1024 control maps per terrain, source UV/brightness/contrast settings and strongest-four height blending. Source migration-stub TerrainLayer tiling contains an infinite value and is not a valid substitute for those MicroSplat settings.

The four source surface arrays are 1024x1024x12. Diffuse RGB is sRGB with height in alpha; NormalSAO is linear with normalXY in A/G, smoothness in R and AO in B. The generated `ArenaMapPort/Terrain` shader is preserved through importer and client shader-rebinding stages. Bundle validation requires executable D3D11 vertex and pixel programs, decompresses their serialized blobs and checks the actual DXBC programs with Microsoft's disassembler; a shader name or property list alone cannot pass. Converter tests/data inspection do not replace full-map Unity validation or an in-game GPU comparison. Secondary MicroSplat effects, engine lighting/reflection behavior, weather and seasonal behavior are not full source-parity claims.

### Vegetation limitation

Source probing found active GPUInstancer detail managers for all seven terrain grids: 182 normal-camera prototypes and **7,109,368 cached candidate counts before distance culling and density masks**. All 14 normal/optic managers are enabled and active in their source hierarchies. This grass is not implemented by the geometry spike, including on Woods where native detail prototypes/coverage are empty but GPU caches are populated. Preserving terrain ground and surface therefore does **not** preserve all vegetation or establish full visual parity.

Native Terrain tree rendering is disabled. Of 34075 retained native tree records, 32551 have position matches in the ordinary source geometry scenes. Extracted ordinary tree geometry is retained; blindly reinstantiating the native records would duplicate thousands of trees. Unmatched disabled native records are not automatically additional active objects that the port should invent.

A separate grass implementation would need source density/prototype extraction, instancing and culling, mask evaluation and suitable material/placement behavior. An offline bake of all 7.1 million candidate clumps as two crossed quads would add about 28.4 million triangles before masks or LOD and would still differ in wind, procedural variation and optic behavior. The accepted expansion retains the inherited runtime-grass exclusion explicitly.

### Other inherited exclusions and acceptance

Arena match rules, loot, interactive doors, audio/acoustics, bot spawn/cover systems, source PerfectCulling, weather/time-of-day parity, ArenaMode integration and Fika remain outside this port. Fresh NavMesh presence does not imply Arena AI or combat gameplay support. Unhandled source animation/runtime systems remain limitations of the geometry spike.

Final acceptance has separate layers: extraction/identity checks; actual Unity scene/material/NavMesh/spawn evidence; receipt-bound artifacts; matching Release distribution; reviewed live deployment; and live GPU raid validation. The 12 new-map raid checks have not been run. A build success, source-state probe or deployment dry run must not be reported as gameplay/runtime proof.

## Local source evidence

The implementation was informed by `work/remaining-maps-census/resolved-inventory.json`, `map-summary.json`, `scene-metadata.json`, the 12 checked-in source plans and targeted `work/terrain-material-probe/` evidence. The terrain probe includes actual Arena shader disassembly, texture-array/control mappings, foliage cache metadata and source-to-source tree-position matches. `work/compressed-mesh-probe/conversion-evidence.json` supplies independent channel/index comparisons; `work/material-slot-probe/source-evidence.json` records the two mixed arrays and the selected-source null-collider census. These generated/source-derived investigation files stay out of git; the tracked plans, converter/validator code and this documented scope describe how to reproduce the work.
