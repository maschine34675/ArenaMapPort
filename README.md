# ArenaMapPort

ArenaMapPort imports locally installed Escape from Tarkov: Arena environments as separate SPT locations. It targets the project's SPT 4.1.5 installation and Unity 2022.3.43f1.

The port is a **geometry spike**: scene geometry, materials, collision, lighting, LOD groups, a freshly baked NavMesh, and an authored player start. It does not implement Arena match rules or full visual parity.

## Current status

The registry contains Equator and Bay5 plus 12 additional source-backed map definitions: AirPit, AirPit2.0, AutoService, Bowl, Prison, RailwayStation, Yard, Iceberg, Factory, Customs, Shoreline and Woods. **All twelve additions are built, validated with schema-2 receipts and staged in `dist/` (verified: 2026-09-15, 20:34 CEST).** Each passed the staged artifact checks. The complete distribution contains 14 maps, 42 bundles, 14 shader sidecars, 14 server base JSON files and both Release DLLs: 72 files, about 11.05 GB.

Validation passed 86 Python tests, eight PowerShell log tests and 228 server tests. The client Release build completed with zero warnings/errors; both staged DLLs match their Release outputs by SHA256. The deployment dry run passed and copied no files into SPT. Live installation and GPU raid validation remain separate steps.

Equator/Bay5 retain their existing identities and artifact locations. The local Saw preset is present, but its 12 distinct referenced scene paths are absent; Saw cannot be ported from this source installation. Bay5's tournament preset is a mode variant, and Draft/the generic test preset are not additional independent maps.

See [remaining maps: source inventory, results and build steps](docs/progress/remaining-maps.md) for the per-map evidence. [Current distribution contract](docs/dist-manifest.md) records the verified file set, baseline preservation, Release hashes and deployment boundary.

## Build inputs

- Repository: `D:/ArenaMapPort`.
- Read-only Arena source: `D:/Battlestate Games/Escape from Tarkov Arena/EscapeFromTarkovArena_Data`.
- Read-only SPT references and donor data: `D:/SPT41`.
- Unity Editor: `C:/Program Files/Unity/Hub/Editor/2022.3.43f1/Editor/Unity.exe`.
- PowerShell 7, Python with UnityPy/numpy/Pillow, and the .NET SDK required by the checked-in projects. The client targets `netstandard2.1`; the server targets `net10.0` for this local SPT 4.1.5 build.

Generated game assets belong in the ignored `work/`, `dist/` and Unity import/reference directories. The Arena installation and historical `analysis/` evidence remain read-only. Game-derived binaries are not committed to this repository. Reused code attribution is in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

From PowerShell in this repository, `./tools/build_remaining.ps1 -Stage` skips maps whose outputs already validate, builds missing maps one at a time and stages the complete additional-map set. See the [reproducible build steps](docs/progress/remaining-maps.md#build-all-additional-maps) for options and the individual commands. Full-map builds run through `tools/build_all.ps1 -FullMap`. A named Unity lock protects both reference synchronization and the editor run. The script applies the mandatory MonoScript identity repair and regenerates the preset/server base from current local SPT donors, including when reusing existing Unity bundles.

Each bundle build ends with the content validators (extraction, bundle round trip incl. MonoScript identity, preset, map descriptors). `tools/validate/validate_port.py` checks a map's build outputs for consistency, and Release staging finishes with `tools/validate/validate_distribution.py`. The earlier hash receipts (`full-map-validation.json`, `port_inputs.py`) were removed on 2026-09-17: they guarded against local tampering that does not happen in this single-developer workflow and cost minutes per client build and hours per refresh. The extraction reproducibility check is opt-in (`build_all.ps1 -CheckReproducibility`).

The generic extractor includes scoped compatibility for source BC6H textures, compressed and genuinely empty meshes, and mixed material arrays with explicit null positions. It also verifies emitted null mesh-collider references against the source. Unsupported layouts fail explicitly; BC6H export retains the inherited 8-bit texture scope. Generic imports refresh mesh data while preserving asset references, and bundle validation compares actual geometry channels and indices with the extracted package. See [source format compatibility](docs/progress/remaining-maps.md#source-format-compatibility) for the limits.

## Terrain and runtime limits

Customs, Shoreline and Woods contain seven real terrain heightfields. The converter preserves their height samples and holes and reconstructs the tiled 12-layer MicroSplat surface from source texture arrays, control maps, layer settings and height blending. All three terrain maps have passed the schema-2 full-map checks. Their live runtime validation remains pending.

Active GPUInstancer grass is excluded from the implemented spike. Source investigation found about 7.1 million cached normal-camera grass candidates; native detail data alone would miss part of this vegetation. Existing ordinary tree geometry is retained, and disabled native tree records are not blindly instantiated again. Dynamic grass/wind, secondary terrain systems, source weather/time-of-day parity, audio/acoustics, loot, interactive doors, bot cover/spawn systems, PerfectCulling, ArenaMode integration and Fika remain outside the accepted scope.

Offline validation, staged file hashes, live installation and a GPU raid are separate evidence. No new-map live raid pass is claimed. Deployment is a separate reviewed step; `pwsh tools/deploy-to-spt.ps1` is a dry run by default.

## Earlier work

[Original spike specification](docs/SPEC.md), [Equator distribution history](docs/progress/dist-manifest.md), [Bay5 port history](docs/progress/bay5-port.md), and [multi-map generalization](docs/progress/multimap-generalize.md) retain their original results. Their historical counts describe those earlier passes; use the current inventory and receipts for this expansion.
