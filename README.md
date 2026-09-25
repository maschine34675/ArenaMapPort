# ArenaMapPort

ArenaMapPort imports locally installed Escape from Tarkov: Arena environments as separate SPT locations. The server dependency pin is SPT 4.1.5; current native acceptance uses SPT 4.1.6 / EFT 0.16.9.5-40743 and Unity 2022.3.43f1.

The port is a **geometry spike**: scene geometry, materials, collision, lighting, LOD groups, a freshly baked NavMesh, and an authored player start. It does not implement Arena match rules or full visual parity.

## Current status

**Target: ArenaMode**, not ordinary SPT raid play. These maps are meant to be played through the user's ArenaMode mod (arena deathmatch with respawns); a plain SPT raid from the menu is only the test vehicle. No loot is authored or extracted on any map, and no scav-raid support (spawn categories, exits, hostility) is implemented — both are permanent decisions, not open gaps. `dist/` (BSG-derived bundles included) is distributed the way other SPT map mods do it; the user carries the licensing grey zone ("a valid EFT installation/licence"). See [docs/SPEC.md](docs/SPEC.md), "Decisions 2026-09-20".

The registry contains Equator and Bay5 plus 12 additional source-backed map definitions: AirPit, AirPit2.0, AutoService, Bowl, Prison, RailwayStation, Yard, Iceberg, Factory, Customs, Shoreline and Woods. All twelve additions were built, validated with schema-2 receipts and staged in `dist/` (verified 2026-09-15, 20:34 CEST; receipt layer simplified 2026-09-17). The complete distribution contains 14 maps, 42 bundles, 14 shader sidecars, 14 server base JSON files and both Release DLLs: 72 files, about 11.05 GB. Validation passed 86 Python tests, eight PowerShell log tests and 228 server tests, as of 2026-09-15/17. The server suite has grown with the bots/exits/PMC/stage-1 work: 267 tests green on 2026-09-20.

**Equator and Bay5.** Both maps have four player starts, ten separate bot markers and two ordinary test exits. Both are integrated with the user's ArenaMode for individual Deathmatch and Gun Game; Tactical and team modes are excluded. Native tests have verified mode admission, bot replenishment, combat respawns and protected profile return. See [Equator acceptance](docs/progress/equator-arenamode-integration.md) and [Bay5 integration](docs/progress/bay5-arenamode-integration.md). The latter separates the first native pass from subsequent geometry and exit corrections. The remaining twelve maps still await their gameplay integration and first native acceptance.

**Equator reflections.** F12 → maschine-ArenaMapPort → Lighting (arena_equator) → `ReflectiveSpecularCap` sets a map-wide ceiling on `p0/Reflective` materials, including uncovered props and mesh decals. Default 30%; 100% restores the input materials. Existing indoor settings remain separate. [Reflection evidence](docs/progress/equator-specular-cap.md) records native A/B views and the repeated-raid camera lifecycle repair.

**Debugging.** F12 shows map-specific options for Equator and Bay5 only; the other maps retain their saved settings and behavior. The separate ArenaMapDebug mod provides **EQUATOR DEBUG** and **BAY5 DEBUG** main-menu buttons for immediate PMC test raids, with no selection screens or starting countdown. Disconnect returns directly to the menu with the existing living-player health protection. See [debug/config verification](docs/progress/bay5-shadows-menu.md).

**Equator materials and ambience.** The native-feature build adds 2,892 original ballistic components and 25 original ambient players / 65 clips, preserving 23 active players and two inactive dogs. Geometry, navigation and authored start behavior are retained; two reflection cubemaps now use their original Linear color space. Build, deployment and Equator–Bay5–Equator native tests passed on 2026-09-22: actual material hits, audio playback/routing and clean repeated teardown were verified. The muted host was not auditioned; audible balance remains a user check. See [Equator feature acceptance](docs/progress/equator-native-features.md).

**Bay5 exterior brightness.** F12 → maschine-ArenaMapPort → Lighting (arena_bay5) → `VertexGroundDiffuseMultiplier` adjusts layered ground colors live. Default 1 restores the original; the local test installation uses 2. Native comparisons show clearer yard/north-lane ground while reflection inputs remain unchanged. Bay5–Equator–Bay5 verified restoration and repeat loading. See [ground visibility evidence](docs/progress/bay5-concrete-lighting.md).

**Surface lighting and shadows.** Equator/Bay5 now preserve the original packed normal textures instead of having Unity encode them a second time. All 519 normals passed source-versus-bundle checks; native Bay5 comparisons show continuous exterior shadows and remove the blue-white glaze from the reported cardboard boxes. `HandsAmbientHighlightMultiplier` separately reduces the donor's hands/held-item highlight (neutral default 1; local Bay5 0.35). Compressed CPU-readable normal copies currently total 178 MiB for Equator / 305 MiB for Bay5; total game-memory impact is unmeasured. See [visual acceptance and limits](docs/progress/bay5-shadows-menu.md).

**Bay5 background.** The original harbor scene is now included: cranes and container stacks are visible in-game. Native acceptance confirmed connected bot starts, Deathmatch movement/replenishment and both ordinary test exits. Distant scenery does not enlarge weather scans or the NavMesh bake volume. See [background acceptance](docs/progress/bay5-background.md).

**Bay5 materials and ambience.** Original ballistic material data now cover 6,322 source components. The port also restores 24 original harbor ambient sources with 106 clips and native shoreline positioning. Three Linear reflection cubemaps now retain their original color space. Build, deployment and a Bay5–Equator–Bay5 native sequence passed on 2026-09-22; audio playback/routing was measured, but the muted host was not auditioned. Glass remains deferred by user choice. See [native feature acceptance](docs/progress/bay5-native-features.md).

Equator/Bay5 retain their existing identities and artifact locations. The local Saw preset is present, but its 12 distinct referenced scene paths are absent; Saw cannot be ported from this source installation. Bay5's tournament preset is a mode variant, and Draft/the generic test preset are not additional independent maps.

See [remaining maps: source inventory, results and build steps](docs/progress/remaining-maps.md) for the per-map evidence. [Current distribution contract](docs/dist-manifest.md) records the verified file set, baseline preservation, Release hashes and deployment boundary.

Release scope (2026-09-25): the first release ships Equator and Bay5 only; the other twelve maps remain built
and staged but are deferred. Agents: start at [AGENTS.md](AGENTS.md), then [HANDOFF.md](HANDOFF.md) for the dated
working state.

**DynamicMaps:** An optional Equator ground-plan pack is generated from local geometry and
the existing NavMesh. Deployment and native player/direction/exit checks passed on 2026-09-22.
Open inventory → **MAP** in an Equator raid. This is a simplified ground-floor prototype;
The user also has a Bay5 DynamicMaps map; it is separate from this Equator generator.
See [acceptance](docs/progress/equator-dynamicmaps.md)
and [generation/deployment](tools/dynamicmaps/README.md).

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

**Ambient dust on Equator and Bay5.** Both now use the original Alpha8 volume texture,
authored particle meshes and native animation. Source-to-bundle checks and an Equator → Bay5 →
Equator native sequence passed on 2026-09-23, including visible motion and complete cleanup.
See [dust acceptance](docs/progress/native-dust.md).

## Terrain and runtime limits

Customs, Shoreline and Woods contain seven real terrain heightfields. The converter preserves their height samples and holes and reconstructs the tiled 12-layer MicroSplat surface from source texture arrays, control maps, layer settings and height blending. All three terrain maps have passed the schema-2 full-map checks. Their live runtime validation remains pending.

Active GPUInstancer grass is excluded from the implemented spike. Source investigation found about 7.1 million cached normal-camera grass candidates; native detail data alone would miss part of this vegetation. Existing ordinary tree geometry is retained, and disabled native tree records are not blindly instantiated again. Dynamic grass/wind, secondary terrain systems, source weather/time-of-day parity, full room/portal acoustics and audio on other maps, loot, general interactive doors, native bot-cover baking, PerfectCulling and Fika remain outside the implemented scope. Equator/Bay5 spawn systems and ArenaMode integration are documented above.

Offline validation, staged file hashes, live installation and a GPU raid are separate evidence. Only the explicitly documented Equator/Bay5 runs establish native acceptance; they do not validate the other twelve maps. `pwsh tools/deploy-to-spt.ps1` is a dry run by default; an authorized install uses `-Confirm`.

## Earlier work

[Original spike specification](docs/SPEC.md), [Equator distribution history](docs/progress/dist-manifest.md), [Bay5 port history](docs/progress/bay5-port.md), and [multi-map generalization](docs/progress/multimap-generalize.md) retain their original results. Their historical counts describe those earlier passes; use the current inventory and receipts for this expansion.
