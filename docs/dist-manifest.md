# Current distribution contract: 14 Arena locations

Verified: 2026-09-15, 20:34 CEST. **All twelve additions have current schema-2 full-map receipts and passed staged validation.** Client/server Release outputs are staged, and the exact 14-map distribution and both DLL hash pairs passed verification. The server Release run passed all 228 tests; the client completed with zero warnings/errors. The deployment dry run passed without copying files into SPT. The per-map results and scope are maintained in [remaining-maps.md](progress/remaining-maps.md).

The earlier Equator-only distribution log remains unchanged at [progress/dist-manifest.md](progress/dist-manifest.md); Bay5's result remains at [progress/bay5-port.md](progress/bay5-port.md). Their older artifact totals are historical.

## Verified distribution layout

```text
dist/
  BepInEx/plugins/ArenaMapPort/
    maschine-ArenaMapPort.Client.dll
    material-shaders.json                       # Equator compatibility name
    material-shaders.arena_<stem>.json           # one for each of the other 13 maps
    bundles/
      arena_<stem>_preset.bundle
      arena_<stem>_scenes.bundle
      arena_<stem>_navmesh.bundle
  SPT_Runtime/user/mods/ArenaMapPort/
    maschine-ArenaMapPort.Server.dll
    db/
      base.json                                 # Equator compatibility path
      arena_<stem>/base.json                     # one for each of the other 13 maps
```

The 14 stems are `equator`, `bay5`, `airpit`, `airpit2`, `autoservice`, `bowl`, `prison`, `railwaystation`, `yard`, `iceberg`, `factory`, `custom`, `shoreline`, `woods`. The verified set is **42 bundles, 14 shader sidecars, the two Release DLLs and 14 map base JSON files**: **72 files, 11,054,200,717 bytes** (about 11.05 GB). Saw, the Bay5 tournament variant, Draft and the generic test preset add no separate output set.

## Sources used for staging

| Map group | Scenes/NavMesh/sidecar source | Preset source | Sidecar destination |
|---|---|---|---|
| Equator | `work/build/full/` | `work/build/preset/arena_equator_preset.bundle` | `material-shaders.json` |
| Bay5 | `work/build/bay5/` | `work/build/preset/arena_bay5_preset.bundle` | `material-shaders.arena_bay5.json` |
| Each additional map | `work/build/<stem>/` | `work/build/preset/arena_<stem>_preset.bundle` | `material-shaders.arena_<stem>.json` |

The client project's existing explicit Equator/Bay5 item lists remain authoritative. The 12 additional maps are explicitly listed in `tools/maps/AdditionalMapBuilds.props`; Release staging calls `tools/validate/validate_port.py --map <stem>` before accepting their outputs. A recursive search across `work/build/` is not a valid substitute: diagnostic/S1 bundles can share filenames with the full map.

Server Release build staging copies the server DLL and its `db/` files into the repository's `dist/` tree. Server tests must also use `-c Release`, because their referenced server project stages its own build output.

## Validation contract and recorded evidence

For each additional map, `work/build/<stem>/full-map-validation.json` must have schema 2 and come from a successful `tools/build_all.ps1 -FullMap` run. It records hashes for the source plan, descriptor, extraction manifest, scene and NavMesh bundles, preset bundle, shader sidecar, editor verification, server base JSON, `unity-run.json`, the actual Unity log and `validation-inputs.json`. Generic map receipts also include `target-physics.json` (read-only SPT collision-matrix snapshot) and `spawn-placement.json`. `validate_port.py` rechecks recorded files, exact scene membership/order contracts, source plan identity, nonempty NavMesh tiles/bounds and server preset references.

`validation-inputs.json` binds acceptance to every package file and the complete consumed Arena/SPT source files, relevant code, settings and toolchain inputs. `port_inputs.py` captures them before mandatory validation, rehashes them after all checks and rehashes them again during receipt verification. Changed bytes or relevant file-set membership invalidate acceptance. Old schema-1 receipts are rejected and require the complete current validation sequence; a hash-only upgrade is not supported. The [schema-2 workflow](progress/remaining-maps.md#schema-2-input-binding-and-revalidation) describes these input groups and complete revalidation of suitable existing Unity output.

Full-map orchestration protects reference synchronization and the editor run with a named Unity mutex. It regenerates current server-base and preset outputs before validation even with `-SkipUnityBuild`. Every explicit full-map attempt archives previous receipt/snapshot files first; superseded files cannot authorize resume or staging after a failed attempt.

The full-map gates include actual built mesh-channel/index comparisons, source-empty mesh preservation and source/package/bundle checks for mixed material slots and emitted null colliders. Terrain bundles additionally require executable D3D11 vertex and pixel programs for the preserved custom shader. Final reconciliation must use the current validators and current package/bundle contents. An old receipt or successful log line does not remain valid after a package refresh.

After Release staging, run `python tools/validate/validate_port.py --map <stem> --distribution` for all 12 additions. This independently compares staged bundles, sidecars and server base JSON with their validated source artifacts. Its `PORT_VALIDATED` output is the final per-map staging evidence; a receipt alone does not prove that the staged copy matches.

Then run the complete distribution check:

```powershell
python tools/validate/validate_distribution.py
if ($LASTEXITCODE -ne 0) { throw 'Distribution file set or Release DLL hash check failed' }
```

This validator requires the exact registered bundle, sidecar and database sets, checks staged database content against the source JSON, and verifies SHA256 equality for both client/server DLLs against their actual Release build outputs. It asks MSBuild for each effective `TargetPath`, accounting for the client's different output-directory convention. A missing or stale DLL fails validation. `./tools/build_remaining.ps1 -Stage` invokes this check after all twelve per-map staged checks and before its success marker. The final check passed with `DISTRIBUTION_OK 14 maps 42 bundles 14 sidecars`, recorded by G9 in `work/logs/remaining-maps/final-gates.log`.

Equator/Bay5 use their preexisting output directories and evidence. The final baseline check passed: eight original artifacts retain their exact hashes, and six descriptor/base/extractor inputs retain their pre-expansion content. The shared DLLs include the registry expansion. The complete 42-bundle/14-sidecar set passed the check for missing and unexpected map outputs.

Final reconciliation:

| Check | Status |
|---|---|
|12 complete source extraction packages | Present; AirPit2.0 and Customs material slots refreshed |
|12 Unity builds | Complete |
|12 current schema-2 full-map receipts | Passed |
|12 `validate_port.py --distribution` passes | Passed |
|Exact 42 bundles /14 shader sidecars /14 base JSON files | Passed |
|Both built/staged Release DLL SHA256 pairs | Passed |
|Server Release and server tests | Passed: 228/228 tests, zero failures/skips |
|Client Release staging | Passed; all twelve receipts checked before copying, zero warnings/errors |
|Equator/Bay5 baseline artifact equality | Passed: eight artifacts and six descriptor/base/extractor inputs |
|Deployment dry run | Passed: 72 new files, zero overwrites, no copies performed |
|Live deployment | Not performed by this build pass |
|New-map GPU raid checks | Not performed |

The actual final Release pass used direct server `dotnet test -c Release` followed by direct client `dotnet build -c Release`, then the per-map staged checks and distribution gates. Evidence is in `work/logs/remaining-maps/final-server-release-tests.log`, `final-client-release.log` and `final-gates.log`. The local summary files `.unlazy/remaining-maps/reports/final-map-validation.json` and `final-dist-size.json` record the twelve map receipts/metrics and total staged size. The Python suite passed 86 tests; final gates additionally confirmed eight PowerShell log tests and reran the relevant BC6H/input-snapshot checks. `build_remaining.ps1 -Stage` remains the documented equivalent runner entrypoint; this pass used the direct commands.

## Deployment boundary

Builds stage only into `D:/ArenaMapPort/dist/`. The completed `pwsh tools/deploy-to-spt.ps1 -SptRoot D:/SPT41` dry run planned 72 new files, zero overwrites and zero already-identical files, and copied nothing (`work/logs/remaining-maps/final-deployment-dry-run.log`). Actual promotion remains a separate reviewed operation using the script's explicit `-Confirm` switch. A successful dry run proves the planned file mapping, not an installed mod or a playable raid.

Procedural GPUInstancer grass and the other [spike limitations](progress/remaining-maps.md#supported-scope-and-fidelity) remain excluded even when distribution validation passes. File-integrity checks do not establish full Arena visual/gameplay parity.
