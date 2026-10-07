# ArenaMapPort

ArenaMapPort imports locally installed Escape from Tarkov: Arena environments as separate SPT locations. The server dependency pin is SPT 4.1.5; current native acceptance uses SPT 4.1.6 / EFT 0.16.9.5-40743 and Unity 2022.3.43f1.

The port is a **geometry spike**: scene geometry, materials, collision, lighting, LOD groups, a freshly baked NavMesh, and an authored player start. It does not implement Arena match rules or full visual parity.

## Current status

**Time and weather.** Equator retains 12:00/00:00 starts: Day always uses Clear,
while midnight keeps the optional neon rain. Bay5 offers the two normal server
times twelve hours apart, with a progressing day/night cycle and dynamic backend
weather. Legacy fixed-hour/test-weather settings do not override Bay5.
See [time/weather verification](docs/progress/map-weather-restoration.md).

**Equator profiles.** The same 12:00/00:00 selection now chooses independent
day/night appearance settings. F12 exposes `Lighting (arena_equator Day)` and
`Lighting (arena_equator Night)`, with corresponding `LightingLab` and `Bots`
sections. Day starts from the accepted realistic defaults; Night retains the
GPU test installation's tuning captured on 2026-10-07. SAIN uses `Factory` by day
and `FactoryNight` at night. Shared geometry, repair and audio options remain
in the original Equator sections. Existing legacy values remain saved and hidden;
changing a profile does not rewrite the other profile. The physical map ID,
ArenaMode integration and DynamicMaps map stay `arena_equator`.
See [profile implementation and verification](docs/progress/equator-profiles.md).

**Arena source update (2026-10-03).** Exporters now support the installed Arena
0.6.0.47752 / Unity 6000.3.6f1 source. Updated art selectors and seven scene plans
pass current-source checks; fresh Equator/Bay5 exports validate successfully.
Accepted map bundles still use the previous verified content and Unity 2022 target.
See [export compatibility audit](docs/progress/arena-update-20261003.md).

**Target: ArenaMode**, not ordinary SPT raid play. These maps are meant to be played through the user's ArenaMode mod (arena deathmatch with respawns); a plain SPT raid from the menu is only the test vehicle. No loot is authored or extracted on any map, and no scav-raid support (spawn categories, exits, hostility) is implemented — both are permanent decisions, not open gaps. `dist/` (BSG-derived bundles included) is distributed the way other SPT map mods do it; the user carries the licensing grey zone ("a valid EFT installation/licence"). See [docs/SPEC.md](docs/SPEC.md), "Decisions 2026-09-20".

The registry contains Equator and Bay5 plus 12 additional source-backed map definitions: AirPit, AirPit2.0, AutoService, Bowl, Prison, RailwayStation, Yard, Iceberg, Factory, Customs, Shoreline and Woods. All twelve additions were built, validated with schema-2 receipts and staged in `dist/` (verified 2026-09-15, 20:34 CEST; receipt layer simplified 2026-09-17). The complete distribution contains 14 maps, 42 bundles, 14 shader sidecars, 14 server base JSON files and both Release DLLs: 72 files, about 11.05 GB. Validation passed 86 Python tests, eight PowerShell log tests and 228 server tests, as of 2026-09-15/17. The server suite has grown with the bots/exits/PMC/stage-1 work: 267 tests green on 2026-09-20.

**Equator and Bay5.** Both maps have four player starts, ten separate bot markers and two ordinary test exits. Both are integrated with the user's ArenaMode for individual Deathmatch and Gun Game; Tactical and team modes are excluded. Native tests have verified mode admission, bot replenishment, combat respawns and protected profile return. See [Equator acceptance](docs/progress/equator-arenamode-integration.md) and [Bay5 integration](docs/progress/bay5-arenamode-integration.md). The latter separates the first native pass from subsequent geometry and exit corrections. The remaining twelve maps still await their gameplay integration and first native acceptance.

**Equator reflections.** F12 → maschine-ArenaMapPort → Lighting (arena_equator Day/Night) → `ReflectiveSpecularCap` sets a map-wide ceiling on `p0/Reflective` materials, including uncovered props and mesh decals. Day defaults to 30%, Night to 100%; 100% restores the input materials. Existing indoor settings remain separate. [Reflection evidence](docs/progress/equator-specular-cap.md) records native A/B views and the repeated-raid camera lifecycle repair.

**Equator light streaks.** F12 → maschine-ArenaMapPort → Lighting (arena_equator Day/Night): `AnamorphicFlare` restores the thin horizontal streak around bright lamps; `AnamorphicFlareIntensity` defaults to 0.40 by day and 0.30 at night and `AnamorphicFlareHDR` is on. This is internal HDR processing, not HDR monitor output. `AmbientIntensity` defaults to 0.5 by day and 4 at night. Broad glow remains separately adjustable through `BloomThreshold`/`BloomIntensity`. [Native comparison and camera lifecycle checks](docs/progress/equator-anamorphic-hdr.md).

**HDR image tuning.** Equator has live controls in its separate Day/Night sections;
Bay5 uses `Lighting (arena_bay5)`. The descriptions and defaults below refer to
Equator Day. Night uses `BloomThreshold=2`, `ExposureCompensationEV=0.3`,
`ReflectionIntensity=1`, `IndoorReflectionStrength=1`, `ReflectiveSpecularCap=1`,
`AmbientIntensity=4` and a custom dark cyan ambient color. Both use
`BloomIntensity=0.35` and the accepted custom Filmic curve.

| Setting | Default | Effect |
|---|---|---|
| `ExposureCompensationEV` | 0 | Relative target brightness: +1 doubles the native automatic-exposure target, -1 halves it. |
| `ExposureRangeMultiplier` | 1 | Moves both native exposure limits toward zero (below 1) or away from zero (above 1). |
| `ExposureSpeedMultiplier` | 1 | Changes how quickly automatic exposure adapts. |
| `TonemapProfile` | Equator: Filmic; Bay5: Original | Original camera settings, Filmic, RomB, ACES, or Off. |

Exposure defaults preserve native indoor/outdoor adaptation. Advanced settings
add `TonemapCustomCurve`, `TonemapCurve` and `TonemapSecondaryCurve`: raw native X/Y/Z parameters,
used only with an explicit Filmic/RomB/ACES profile. Set the custom toggle off to return to the
selected profile, or select Original to recover the camera's own parameters. Equator now defaults to
custom Filmic `(7, 0.35, 0.7)` / `(-0.02, 0.18, 0)`; Bay5 retains Original with custom tuning off
and the saved RomB vector defaults. These are not independent shadow/highlight sliders. Enable the F12 Advanced
filter to see six X/Y/Z sliders with precise number boxes. Their response is finer near zero.
`Use <profile> preset` copies both native vectors and enables custom tuning; the ordinary Reset
buttons restore each vector's config default.

The accepted Equator defaults also use `ReflectionIntensity=0.85`,
`IndoorReflectionStrength=0.95` and the existing `GlobalLightIntensityMultiplier=0.5`.
Startup migrates exact previous float defaults once; tonemapping migrates only when all four
old tone settings still form the complete neutral preset. Other tuning is preserved.
See [accepted defaults and Bay5 streetlight source audit](docs/progress/equator-defaults-bay5-lights.md).

Equator's broken tile patches and floor shards now inherit the indoor floor supporting them,
including their LODs. `IndoorFloorSmoothness` controls both intact and damaged indoor tiles;
their authored textures and small material differences remain. Ground multipliers control
the remaining ground materials independently, without repeated scaling of shared materials.

Equator also has `AnamorphicFlareLength` (3.5), `AnamorphicFlareThreshold` (0.155) and
`AnamorphicFlareTint` (the previous pale-blue RGBA color). Tint alpha multiplies flare strength.
All changes work during a raid. See [HDR controls and verification](docs/progress/hdr-controls.md).

**Debugging.** F12 shows map-specific options for Equator and Bay5 only; the other maps retain their saved settings and behavior. The separate ArenaMapDebug mod provides **EQUATOR DEBUG** and **BAY5 DEBUG** main-menu buttons for immediate PMC test raids, with no selection screens or starting countdown. Disconnect returns directly to the menu with the existing living-player health protection. See [debug/config verification](docs/progress/bay5-shadows-menu.md).

**Configuration cleanup.** Obsolete rain-cover, custom ambient-intensity and unused map/logging controls are removed. Startup also deletes their old saved entries while preserving active tuning. Internal migration markers are hidden. Conditional camera/ambient/water fallback controls remain and explain when they apply. [Audit and verification](docs/progress/config-cleanup.md).

**Equator materials and ambience.** The native-feature build adds 2,892 original ballistic components and 25 original ambient players / 65 clips, preserving 23 active players and two inactive dogs. Geometry, navigation and authored start behavior are retained; two reflection cubemaps now use their original Linear color space. Build, deployment and Equator–Bay5–Equator native tests passed on 2026-09-22: actual material hits, audio playback/routing and clean repeated teardown were verified. The muted host was not auditioned; audible balance remains a user check. See [Equator feature acceptance](docs/progress/equator-native-features.md).

**Bay5 exterior brightness.** F12 → maschine-ArenaMapPort → Lighting (arena_bay5) → `VertexGroundDiffuseMultiplier` adjusts layered ground colors live. Default 1 restores the original; the local test installation uses 2. Native comparisons show clearer yard/north-lane ground while reflection inputs remain unchanged. Bay5–Equator–Bay5 verified restoration and repeat loading. See [ground visibility evidence](docs/progress/bay5-concrete-lighting.md).

**Surface lighting and shadows.** Equator/Bay5 now preserve the original packed normal textures instead of having Unity encode them a second time. All 519 normals passed source-versus-bundle checks; native Bay5 comparisons show continuous exterior shadows and remove the blue-white glaze from the reported cardboard boxes. `HandsAmbientHighlightMultiplier` separately reduces the donor's hands/held-item highlight (neutral default 1; local Bay5 0.35). Compressed CPU-readable normal copies currently total 178 MiB for Equator / 305 MiB for Bay5; total game-memory impact is unmeasured. See [visual acceptance and limits](docs/progress/bay5-shadows-menu.md).

**Bay5 background.** The original harbor scene is now included: cranes and container stacks are visible in-game. Native acceptance confirmed connected bot starts, Deathmatch movement/replenishment and both ordinary test exits. Distant scenery does not enlarge weather scans or the NavMesh bake volume. See [background acceptance](docs/progress/bay5-background.md).

**Bay5 streetlights.** The 15 inspected original streetlamp hierarchies have no
Light or lamp-control component. Their original meshes/materials are ported;
working streetlamp illumination would require additional authoring. This source
audit does not establish Arena's intended nighttime difficulty.
See [source evidence](docs/progress/equator-defaults-bay5-lights.md).

**Original projection decals.** Equator and Bay5 import the original graffiti, dirt and markings from
Arena's `StaticDeferredDecal` components, including their own material references and atlas regions.
Source-disabled objects remain disabled. Each map has a live `Decals (<map>) / Enabled` comparison
switch. Existing mesh-based decals keep their separate behavior. See [implementation and native
verification](docs/progress/native-decals.md).

**Bay5 materials and ambience.** Original ballistic material data now cover 6,322 source components. The port also restores 24 original harbor ambient sources with 106 clips and native shoreline positioning. Three Linear reflection cubemaps now retain their original color space. Build, deployment and a Bay5–Equator–Bay5 native sequence passed on 2026-09-22; audio playback/routing was measured, but the muted host was not auditioned. Glass remains deferred by user choice. See [native feature acceptance](docs/progress/bay5-native-features.md).

**Spatial acoustics.** Equator and Bay5 now use their original acoustic rooms,
openings and baked sound paths for native sound attenuation, plus their original
indoor room tones. F12: `Audio (<map>) / NativeSpatialAudio` (next raid).
Original room-dependent ambient reverb/delay tables are also restored:
`Audio (<map>) / NativeRoomReverb` applies live. Gameplay hall retains native
indoor/outdoor snapshots; outdoor ambience blending remains separate work.
See [spatial acoustics](docs/progress/spatial-audio.md) and
[room reverb](docs/progress/room-reverb.md).

Equator/Bay5 retain their existing identities and artifact locations. The local Saw preset is present, but its 12 distinct referenced scene paths are absent; Saw cannot be ported from this source installation. Bay5's tournament preset is a mode variant, and Draft/the generic test preset are not additional independent maps.

See [remaining maps: source inventory, results and build steps](docs/progress/remaining-maps.md) for the per-map evidence. [Current distribution contract](docs/dist-manifest.md) records the verified file set, baseline preservation, Release hashes and deployment boundary.

Release scope (2026-09-25): the first release ships Equator and Bay5 only; the other twelve maps remain built
and staged but are deferred. Agents: start at [AGENTS.md](AGENTS.md), then [HANDOFF.md](HANDOFF.md) for the dated
working state.

**DynamicMaps:** Optional Equator and Bay5 ground-plan packs are generated from local geometry
and each map's existing NavMesh. Deployment and native player/direction/exit checks passed on 2026-09-22.
Open inventory → **MAP** in an Equator or Bay5 raid. Both ground-plan packs are now
installed and natively tested, including player position, direction and active exits.
These are simplified ground-floor prototypes. See [Equator acceptance](docs/progress/equator-dynamicmaps.md),
[Bay5 acceptance](docs/progress/bay5-dynamicmaps.md)
and [generation/deployment](tools/dynamicmaps/README.md).

## Build inputs

**Equator day/night:** the normal map selector offers fixed start times **12:00** and
**00:00**. Its artificial sun and sun volume turn off at night; `NightSunMultiplier`
in the selected Equator profile allows some remaining light (0.1 = 10%, default 0 = off).
Each time uses its own saved appearance profile. Bay5 keeps its native time behavior.
Equator's debug shortcuts still start the day variant. See [native verification](docs/progress/equator-day-night.md).

**Equator neon night (experimental):** choosing **00:00** adds cyan/magenta light
strips at the Equator signs, atrium columns and TECHLIGHT entrance, with actual
colored illumination and shadows. **12:00** keeps the realistic daytime appearance.
F12 → `Night neon (arena_equator)` offers a live switch, separate light/strip
brightness controls and two colors. Its `Rain` option defaults on for this night
variant only; changes to weather apply on the next raid. Original image settings
remain yours. The temporary night PostFX option is described below.
See [light controls and test evidence](docs/progress/equator-night-lights.md).

The same section now includes independent live atmosphere controls:
`HazeStrength=0.2398122` adds moving colored haze around the neon lights and requires
the game's **Volumetric Light** graphics option; `SmokeDensity=1` adds shallow,
drifting smoke banks. Set either value to 0 to turn that effect off. Both follow
the existing neon switch and activate only on Equator at **00:00**. The smoke is
decorative and does not change AI visibility. See [night atmosphere controls and
verification](docs/progress/equator-night-atmosphere.md).

`DistanceHaze=0.01` adds a subtle cool haze throughout the night map, beginning
beyond 5 metres; 0 disables it. `PostFx=true` temporarily applies the balanced
Chillwave preset to the night camera. It pauses in the game's settings/preview
and restores your saved PostFX on exit or disable, without saving the preset over
your settings. Both follow the midnight neon switch and can change live.
See [distance haze and temporary PostFX](docs/progress/equator-night-haze-postfx.md).

Nine additional decorative chemlights in cyan, magenta, amber and green add small
shadowed light pools around the lanes, cover and entrances. Live
`AccentLightIntensity=1` controls these lights independently (0 removes them).
`WetFloorStrength=0.8` adds floor sheen while retaining authored surface variation
and the saved daytime values; 0 restores the configured baseline. Both controls
share the midnight neon switch. See [accent lights and wet floor](docs/progress/equator-night-accents-wet-floor.md).

Three fixtures in the optional midnight installation also have occasional,
independently timed flicker: the north-west column, right TECHLIGHT strip and
left south-sign strip. Their visible emission, illumination and haze change
together. In the same F12 section, live `FlickerStrength=0.8` leaves 20% brightness
at the deepest dip; 0 restores steady light and 1 permits complete blackouts.
Live `FlickerFrequency=1` gives roughly one short burst per 20 seconds per selected
fixture; the captured night recipe defaults to 3. It changes the gaps between bursts, not their length; 0 restores steady
light. See [selective night flicker](docs/progress/equator-night-flicker.md).

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

**Original lamp flares on Equator and Bay5.** `Lighting (<map>) / NativeMultiFlare`
restores the source per-lamp halos and lens effects (25/24 active lights), with
native depth occlusion and a live on/off switch. `NativeMultiFlareIntensity`
sets strength independently per map: 0 hides the effects, 0.5 is the new default,
1 restores original strength. Original inactive lights stay inactive. This is
independent of the Equator anamorphic streak settings.
See [MultiFlare recovery and native tests](docs/progress/native-multiflare.md).

**Lighting compatibility and shadows.** SmajlecLights camera adjustments are
automatically excluded on ported maps; it can stay installed for ordinary maps.
BrighterInteriors currently has no active stencil overlays to affect on Equator
or Bay5. Both maps retain automatic exposure. Targeted repairs restore Equator's
solid ceiling sun shadows and both adjacent Bay5 sand-filled barricades. See
[lighting follow-up and GPU tests](docs/progress/lighting-followup.md).

## Terrain and runtime limits

Customs, Shoreline and Woods contain seven real terrain heightfields. The converter preserves their height samples and holes and reconstructs the tiled 12-layer MicroSplat surface from source texture arrays, control maps, layer settings and height blending. All three terrain maps have passed the schema-2 full-map checks. Their live runtime validation remains pending.

Active GPUInstancer grass is excluded from the implemented spike. Source investigation found about 7.1 million cached normal-camera grass candidates; native detail data alone would miss part of this vegetation. Existing ordinary tree geometry is retained, and disabled native tree records are not blindly instantiated again. Dynamic grass/wind, secondary terrain systems, source weather/time-of-day parity, outdoor ambience blending, spatial acoustics and audio on other maps, loot, general interactive doors, native bot-cover baking, PerfectCulling and Fika remain outside the implemented scope. Equator/Bay5 spawn systems and ArenaMode integration are documented above.

Offline validation, staged file hashes, live installation and a GPU raid are separate evidence. Only the explicitly documented Equator/Bay5 runs establish native acceptance; they do not validate the other twelve maps. `pwsh tools/deploy-to-spt.ps1` is a dry run by default; an authorized install uses `-Confirm`.

## Earlier work

[Original spike specification](docs/SPEC.md), [Equator distribution history](docs/progress/dist-manifest.md), [Bay5 port history](docs/progress/bay5-port.md), and [multi-map generalization](docs/progress/multimap-generalize.md) retain their original results. Their historical counts describe those earlier passes; use the current inventory and receipts for this expansion.
