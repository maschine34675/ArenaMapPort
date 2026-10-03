# Progress log index

This folder is the project's append-only build/fix/validate history: one file per milestone, audit,
fix or validation round, each with host-test or command-output evidence. Files are **appended to, never
rewritten** — a later pass that changes something adds a new dated section or a new file rather than
editing an old conclusion out. [HANDOFF.md](../../HANDOFF.md) is the one place that holds the current
dated state in one screen; read it before this index if you only have a minute.

## Current — read these for the area you touch

- [map-time-weather.md](map-time-weather.md) — Bay5 native 12:00/00:00 choices
  through client raid time and server gear, plus Equator midnight rain preview
  with native icon restoration (2026-10-03). Deployed locally/GPU-2; native 1080p
  menu acceptance passed, including Next/Back and ordinary PMC restoration.
  No raid; Bay5 night lighting and bot gear are not natively certified here.

- [equator-night-accents-wet-floor.md](equator-night-accents-wet-floor.md) — nine
  multicolor native chemlights and a night-only floor gloss control; verified
  SMap shader field semantics, native comparisons and saved-setting preservation
  (2026-10-01). Same-day follow-up moves the six column-front accents into
  native-night shadow gaps; all six placements verified on GPU-2.

- [equator-night-haze-postfx.md](equator-night-haze-postfx.md) — subtle map-wide
  native distance haze and temporary balanced camera PostFX for Equator midnight,
  preserving saved settings and tested across night/off/menu/day (2026-10-01).

- [bay5-dynamicmaps.md](bay5-dynamicmaps.md) — Bay5 ground-plan generation and
  original native acceptance; source integration and preservation of the retired
  isolated checkout appended on 2026-10-01.

- [equator-night-flicker.md](equator-night-flicker.md) — occasional flicker on
  three midnight neon fixtures, synchronized strips/lights/haze and independent
  live strength/frequency controls (2026-09-30).

- [equator-night-atmosphere.md](equator-night-atmosphere.md) — local colored haze
  and shallow smoke for Equator's optional midnight neon installation, independent
  live controls and native lifecycle checks (2026-09-30).

- [equator-night-lights.md](equator-night-lights.md) — optional Equator 00:00
  cyan/magenta light installation, separate live controls and scoped night rain (2026-09-30).

- [equator-night-neon.md](equator-night-neon.md) — reversible Equator 00:00/Rain
  neon experiment, vivid/balanced recipes and native screenshots; original values restored (2026-09-30).

- [equator-defaults-bay5-lights.md](equator-defaults-bay5-lights.md) — accepted
  Equator image defaults and guarded migration; original Bay5 streetlamp source audit (2026-09-29).

- [equator-floor-gloss.md](equator-floor-gloss.md) — coherent gloss controls for small
  Equator tile pieces, unique ground baselines and user-refined reflection/flare tuning (2026-09-29).

- [hdr-sliders-tuning.md](hdr-sliders-tuning.md) — six native tonemap sliders per map,
  normal bloom defaults/migration and repeatable Equator Day/Clear screenshots (2026-09-29).

- [hdr-controls.md](hdr-controls.md) — live Equator/Bay5 exposure and native tonemapping,
  advanced curve vectors, Equator flare length/threshold/tint, neutral defaults,
  GPU-2 comparisons and saved-tuning preservation (2026-09-29).

- [arena-audio.md](arena-audio.md) — optional Arena audio for ArenaMode: the English commentator bank
  classified by BSG's own 30 event tags (54 of 69 categories kept, Blast Gang and PurifiedTeamAppearance out),
  EFT's dormant arena_start/win/lose stingers, crowd, camera glitch, capture, round signal, result music;
  OGG + manifest.json contract staged into the plugin folder, never git; suggested ArenaMode cue mapping.
  Group `other` (2026-09-29) adds the unassigned clips: the 15 excluded commentator categories and every
  Arena-only clip EFT lacks (751 clips, 93.7 MiB); the RU/Geneburn/Tagilla voice banks stay on request.
- [arena-ui.md](arena-ui.md) — Arena UI sprites for ArenaMode as PNG + manifest + contact-sheet previews:
  killfeed weapon/kill-method/death icons with labels, K/D/A glyphs, mode logos, kit-class medallions,
  capture markers/progress art, scoreboard backgrounds with the team palette, the 36-frame MVP medal.
- [../ARENA-CONTENT-FOR-ARENAMODE.md](../ARENA-CONTENT-FOR-ARENAMODE.md) — 2026-09-29 survey of everything
  in EFT Arena beyond the maps that ArenaMode could use: tiers, evidence, owners, rejected dead ends.
- [config-cleanup.md](config-cleanup.md) — complete configuration audit,
  46 ineffective entries retired, user values preserved and both maps smoke-tested (2026-09-28).

- [lighting-followup.md](lighting-followup.md) — live MultiFlare strength,
  verified exposure, Techlight ceiling and adjacent Bay5 barricade shadows,
  optional SmajlecLights exclusion and BrighterInteriors assessment (2026-09-28).

- [native-multiflare.md](native-multiflare.md) — original Equator/Bay5 lamp sprites,
  source atlas, isolated native rendering and visible/occluded off/on comparisons (2026-09-28).

- [room-reverb.md](room-reverb.md) — original room-dependent ambient mixer tables,
  detailed room-type compatibility, separate gameplay hall path and listener-output tests (2026-09-28).

- [spatial-audio.md](spatial-audio.md) — original Equator/Bay5 acoustic rooms, portals and propagation
  bakes, native attenuation and room tones; reverb/blending boundary (2026-09-28).

- [bay5-water.md](bay5-water.md) — source harbor water textures/settings on the native deferred renderer,
  night/day comparison, shader-mask-based puddle footsteps and historical investigation (2026-09-28).

- [native-decals.md](native-decals.md) — original projection decals on Equator and Bay5,
  source material recovery, native renderer lifecycle and atlas-coordinate validation (2026-09-27).

- [equator-day-night.md](equator-day-night.md) — Equator-only 12:00/00:00 selection,
  night artificial-sun dimming, bot night classification and Clear/Rain host comparisons (2026-09-27).

- [equator-anamorphic-hdr.md](equator-anamorphic-hdr.md) — horizontal flare versus broad bloom,
  HDR/tonemapping order, camera repairs across raids, and Equator ambient contrast (2026-09-27).

- [equator-dynamicmaps.md](equator-dynamicmaps.md#north-marker-clarification--2026-09-26) — explicit
  N and two-tone compass on both existing overview maps; offline visual/deploy checks (2026-09-26).

- [two-maps-items-1-3.md](two-maps-items-1-3.md) — ambient gain/offset keys, barbed wire on Bay5, release
  names; host evidence 2026-09-25.

- [two-maps-item-4-navmesh-modifiers.md](two-maps-item-4-navmesh-modifiers.md) — NavMeshModifier/Volume extracted,
  imported and honoured by the bake (Bay5: 113 not-walkable overrides + 134 volumes on the bruno_helix wire family;
  Equator: 31 ignore-from-build markers, no overrides); host-verified 2026-09-25 (bake-only components stripped before the bundle build).
- [two-maps-item-5-physics-props.md](two-maps-item-5-physics-props.md) — Rigidbody + native
  `SmallPhysicsObject` on Equator's 391 / Bay5's 18 movable props, on by default. Extractor/importer/client
  built and offline-verified (real extractor runs against live Arena source: exact 413/391 and 22/18 counts,
  full `validate_extract.py` pass on both maps); rebuilt and host-verified 2026-09-25 — Arena's per-prop
  SoundType is extracted since the first raid's null-bank exception (`PhysicsPropSoundRepairs` guards the rest).
- [two-maps-item-6-raincover.md](two-maps-item-6-raincover.md) — rain-cover geometry: Arena's own
  EnvironmentManager/TriggerGroup/IndoorTrigger extracted from each map's Scripts scene and authored; Bay5's
  DryPlanes turned out to be Lighthouse leftovers and are dropped, so the depth-photo heuristic stays armed on
  both maps. Verified 2026-09-26 on SPT-GPU-2 (F8 readbacks + rain screenshots); Equator's atrium rains by
  design (`FullyIndoorCover` default false), puddle ripples follow the global rain value.
- [two-maps-item-7-light-culling.md](two-maps-item-7-light-culling.md) — CullingLightObject / VolumetricLight /
  LightFlicker extracted from Arena's raw bytes and attached by the importer (packages `*-native-v4`), client
  fallbacks for CullingManager and VolumetricLightRenderer, brightness-uplift coordination; verified 2026-09-26 on
  SPT-GPU-2 (472/186 components live, 79/107 dark-saved lights lit); distance fade, volumetric glow and flicker
  measured 2026-09-27 with the F7 `LightCullingProbe` in bot-free raids.

- [two-maps-item-10-banners.md](two-maps-item-10-banners.md) — location-select banners from Arena's own
  Matchmaker_Banner art (extracted into dist/ only, served via ImageRouter, runtime Banners entry only when the file
  exists); verified 2026-09-27 on SPT-GPU-2.
- [two-maps-item-8-ai-anchors.md](two-maps-item-8-ai-anchors.md) — Arena's own real `PatrolWay`/
  `AICorePointHolder` topology extracted from the excluded TeamFight_AI scene (14/2+21 core points/hubs+subs
  on Equator, 11/4+43 on Bay5) and authored by the client in place of the NavMesh-derived/single-point
  synthesis; also corrects `PLAN-TWO-MAPS.md`'s own wrong "`ConnectionsAtNet` is unrecoverable" claim.
  Verified 2026-09-27 on SPT-GPU-2 (all anchors reachable except Equator's isolated core points 13/14; bots walk
  the real hubs, no ActiveFail).
- [two-maps-item-10b-loadscreens.md](two-maps-item-10b-loadscreens.md) — deploy-screen art from Arena's own loading
  screen and main-menu backgrounds (five per map, extracted into dist/ only), drawn as the first child of EFT's
  deploy screen and hidden on its OnClose; verified 2026-09-27 on SPT-GPU-2.

- [../PLAN-TWO-MAPS.md](../PLAN-TWO-MAPS.md) — the ordered, spike-verified plan for the two-map first
  release (Equator + Bay5): nine work items, release checklist, what is deliberately out (2026-09-25).

- [native-content-inventory.md](native-content-inventory.md) — census of every Arena script/engine class
  on Equator and Bay5 (imported and excluded scenes), what is ported, and the ranked list of what can still be
  ported for ArenaMode play (2026-09-24).

- [native-dust.md](native-dust.md) — original Equator/Bay5 Alpha8 volume import, native dust
  animation, transparent render queue and lifecycle verification (2026-09-23).

- [bay5-barrier-followup.md](bay5-barrier-followup.md) — targeted authored shadow-proxy restoration
  and live-material DistantShadow cache invalidation (2026-09-23).

- [bay5-shadows-menu.md](bay5-shadows-menu.md) — shadow/veil diagnosis, packed-normal import
  repair, hands highlight, focused F12 configuration and Bay5 quick start (2026-09-22).

- [bay5-concrete-lighting.md](bay5-concrete-lighting.md) — exterior diffuse control, native A/B,
  preserved reflection inputs, map-transition cleanup and quiet-audio follow-up (2026-09-22).

- [equator-dynamicmaps.md](equator-dynamicmaps.md) — optional local Equator ground-plan pack:
  reproducible geometry/NavMesh export, scoped data-only deployment, native position/direction/exit
  acceptance, normal profile return and prototype limitations (2026-09-22).

### Pipeline (extract → import → bundle → validate)

- [SOURCE-PATH.md](SOURCE-PATH.md) — records the read-only local Arena source path
  (`D:/Battlestate Games/Escape from Tarkov Arena`) the extractor's `DEFAULT_SOURCE` and both package
  manifests point at. No open item.
- [impl-texture-dimensions.md](impl-texture-dimensions.md) — fixes Cubemap/Texture3D texture import
  (18 Cubemaps + 1 Texture3D decoded as plain 2D, causing >150 `_Cube`/`_ParticleTex` dimension-mismatch
  errors) across the extractor, Unity importer and `validate_bundle_roundtrip.py`. No open item — "Full
  pipeline re-run ... green end to end."
- [fix-enabled-state.md](fix-enabled-state.md) — extractor and importer now round-trip a component's
  `m_Enabled`/`.enabled` flag, so anything Arena itself ships disabled (the "white untextured blocks
  blocking walkways" playtest finding) comes back disabled in the bundle. No open item — "**Result:
  done.**"
- [identity-fix.md](identity-fix.md) — `tools/bundle/patch_monoscript_identity.py`, the mandatory
  post-build step (wired into `tools/build_all.ps1`) that rewrites a built bundle's MonoScript
  `m_AssemblyName` back to `'Assembly-CSharp'` so a real EFT client resolves the 5 SS D script
  components instead of treating them as "missing script". Open item: *"S2 (full 6-scene build) is
  untouched by this pass and will need `tools/build_all.ps1` run for real"* — since done, by every full
  build from `s2-build.md` onward.
- [fix-preset-cab-and-bundle-lifetime.md](fix-preset-cab-and-bundle-lifetime.md) — every preset bundle
  carried the *same* internal Unity directory-table name, so loading a second map's preset in one client
  session refused to load; fixed (name now derived per map key) and extended
  `validate_distribution.py` to catch a recurrence. Also fixes a second bug: `BundleHost`'s Harmony hook
  double-counted its own `AssetBundleReference` refcount on first load. Open item: *"§10 What the user
  must redeploy"* — the 12 additional maps' schema-2 receipts still needed a background refresh at the
  time this was written (superseded by [remaining-maps.md](remaining-maps.md)'s later staged build).

### Raid start & scene repairs

- [impl-raidstart-repairs.md](impl-raidstart-repairs.md) — implements the raid-start crash-chain fixes
  from the two audits below: `SpatialAudioSystem.Initialize` NRE (the observed, aborting crash),
  missing `EnvironmentManager`/`ObservedCullingManager`, `EffectsController`/`FrostbiteEffect` NRE, and
  two `BotsController.Init` null-data guards. Open item at the time: *"No in-game raid test was run this
  pass"* — stale; raid-start has since been host-tested repeatedly from `bots-stage1.md` onward.
- [fix-raidstart.md](fix-raidstart.md) — adds the guard for a second, sibling unguarded NRE entry point
  (`SpatialAudioSystem.ProcessInteractiveObjectOcclusion`, reachable once the pipeline authors doors/
  breakable glass) and fixes 4 truncated SHA-256 hashes in `impl-texture-dimensions.md`'s table. Open
  item: same "no in-game raid test" note as above, also now stale.
- [fix-gamestart.md](fix-gamestart.md) — fixes the raid-start hang: `AuthorScriptsScene.cs` authored
  every `SpawnPointMarker.Infiltration = null`, and with no `SpawnPointParams` shipped, nothing ever set
  it, so the entry-point NRE hung the countdown. Open item, quoted: *"`LocalGame.WeatherCurve` guard —
  deferred ... revisit when a milestone adds bots or an airdrop system to `db/base.json`"* — bots have
  since been added (`bots-stage1.md`/`bots-stage3.md`); this guard has not been revisited since.
- [fix-lighting-movement.md](fix-lighting-movement.md) — spawn colliders flipped to triggers
  (`SpawnColliderRepairs`, fixes stuck-at-spawn + grenade-dropped-at-feet) and a tunable ambient/light
  brightness multiplier (`ArenaLightingRepairs`) for maps that don't run the donor weather rig. Open
  items, quoted from *"Deferred to a future bundle rebuild"*: baking real `LevelSettings` ambient values
  instead of the flat placeholder, `ReflectionProbe` wiring for the 18 imported Cubemaps, a
  `Global Fog/Transparent Reflective Specular` shader remap for 16 glass/window materials.
- [fix-combat.md](fix-combat.md) — guards `EyeBurn`/`GrenadeFlashScreenEffect`/`BloodOnScreen`/
  `FastBlur` against Cam2 (the fallback camera) missing the screen-effect components
  `EffectsController.Init()` assumes exist. Open items, quoted from *"Deferred — not fixed"*:
  `OnHealthDied` "also dereferences `_blur` unguarded" (now built per HANDOFF's Stage 1 as
  `EffectsControllerRepairs.DeathScreenEffectsGuard`, host test pending) and `LowPolyCollider`-layer
  geometry "excluded from `BallisticsCalculatorConstants.HitMask`" (now built per HANDOFF's Stage 1 as
  `BallisticTwinRepairs.cs`, host test pending).

### Lighting/weather/visuals

- [weather-rig-stage0.md](weather-rig-stage0.md) — the donor Weather-rig mechanism itself
  (`WeatherRig.cs`): harvests a vanilla built-in `"<Donor>_Scripts"` scene's Weather/`Weather_Simple`
  rig onto a `DontDestroyOnLoad` host and toggles it active per raid, originally defaulted on for Bay5
  only (`Donor="Reserve_Base_Scripts"`), off (`"Off"`) everywhere else. Its own "Known risks" section is
  marked *"Superseded by §7 'Review fix-ups' below"*; its Bay5-only default was later extended to
  Equator too (`donor-settings-camera-weather.md` §20).
- [donor-settings-camera-weather.md](donor-settings-camera-weather.md) — the current, most detailed
  lighting/weather doc (20 dated sections through 2026-09-19): donor-consistent `LevelSettings`/camera
  takeover, the "Lighting Lab" live diagnostic A/B tool, the real ambient fix, the rain-cover
  depth-photo mechanism (`RainCoverRepairs`), and finally *"§20) Equator gets the rig"* — Equator now
  also defaults to `Donor="Reserve_Base_Scripts"`, with its own tuned `IndoorReflectionStrength=0.3`
  (Bay5 stays `1.0`). Open item, quoted (§20.3): *"Open until the next host raid: whether
  `_SpecColor × _Specularness` at, say, 0.3 actually tames the parquet ... and what Equator's default
  should be."*
- [daytime-and-visuals.md](daytime-and-visuals.md) — deterministic noon time-of-day
  (`TimeOfDayRepairs`), Cam2 auto-exposure + `TOD_Camera`/`TOD_Scattering` fixes, disabling unsupported
  deferred-decal renderers, an ambient double-write fix and a sea placeholder material. Open item,
  quoted: *"rain/DryPlane coverage, full water fidelity ... proper `StaticDeferredDecal` authoring,
  AM/PM menu toggle, reflection-probe repositioning, the full camera-prefab harvest"* deferred.
- [shader-compat-and-inspector.md](shader-compat-and-inspector.md) — Bay5 ground
  `_BlendStrength`/smoothness value correction, a missing-shader fallback table (off by default), the
  **F10 scene inspector** (`SceneInspector.cs`), and an exposure default change. Open item, quoted from
  its *"Deferred pipeline fixes"* table: per-map `HeightFalloff`/`ZeroLevel` authoring from real
  ground/sea Y (Bay5's horizon-band root cause), Bay5's ground mesh vertex-color density gap, and
  promoting the client-side shader-fallback table into the pipeline.

### Bots

- [bots-stage1.md](bots-stage1.md) — closes six silent gaps (APBS per-map lookup, SPT brain tables,
  null `AICorePointHolder`/`AIPlaceInfoHolder` lists, a 0×0×0 voxel grid, empty `BotZone.PatrolWays`) so
  scavs spawn, activate and patrol Equator's ground floor. Open item, quoted: *"Open for later stages:
  authored patrol routes (Arena's own AI data, upper floors, more than one zone), exfils / raid end, PMC
  bots, covers"* — exfils and PMC bots are done (`exfils-stage2.md`, `bots-stage3.md`); authored patrol
  routes and covers are still open.
- [bots-stage3.md](bots-stage3.md) — PMC squads via `BossLocationSpawn` (Factory-shaped waves,
  `ApplyPmcBrainTables`), no client-side change needed (the stage-1 repairs are role-agnostic). Open
  items, quoted: *"PMC squads ignore `BotMax`"* (a longer raid should show how crowded it gets) and
  "Hostility towards the player follows the inherited Factory `AdditionalHostilitySettings` ...
  untested in combat here."

### Exfils/spawns

- [exfils-stage2.md](exfils-stage2.md) — runtime-authored exfiltration points with green smoke signals
  (`ExfilSignals`), spawn-booth sliding doors opened at raid start (`BoothDoorRepairs`) and the FCK
  hall's roller shutter opened by hierarchy path (`ArenaMapDescriptor.OpenedBarriers`). Open item,
  quoted: *"the NavMesh was baked with the leaves in place, so the booth interiors stay islands for bots
  ... excluding `Door_Sliding_Respawn*` and `SPAWN_BOUND*` from the bake is a Unity rebuild (~19 min)
  for a later pass"* — this is HANDOFF's Stage 1 NavMesh bake-exclusion item.

### Multi-map & remaining maps

- [multimap-generalize.md](multimap-generalize.md) — refactors Equator's hardcoded client/server/
  tooling behind `ArenaMapDescriptor`/`ArenaMapRegistry` so a second map can register without touching
  Equator's shipped bundles. Open items, quoted: *"`extract_equator.py` generalization deferred"* and
  *"`tools/validate/validate_infiltration_name.py` ... not built this pass"* (both later done — see
  `remaining-maps.md`'s generic extractor and its own cross-assembly checks).
- [bay5-port.md](bay5-port.md) — ports Bay5 (TeamFight) through the generalized pipeline: extraction,
  Unity build with a fresh NavMesh bake, MonoScript identity + preset patch, server registration, dist
  staging. Open item, quoted: *"No in-game playtest. This pass is offline/automated"* — since partly
  closed (Bay5 was host-raided for visuals only, per HANDOFF; full gameplay walk-through still open).
- [remaining-maps.md](remaining-maps.md) — extracts, builds, validates and stages the other 12 maps
  (airpit, airpit2, autoservice, bowl, prison, railwaystation, yard, iceberg, factory, custom,
  shoreline, woods) into `dist/`, plus the generic extractor's format-compatibility adapters (BC6H,
  compressed meshes, terrain) and the explicitly out-of-scope items (vegetation/grass, Arena match
  rules, loot, bot spawn/cover systems). Carries its own update note: *"the schema-2 receipt layer ...
  has been removed; `validate_port.py` now checks build outputs directly."* Open item, quoted: *"Live
  deployment and GPU raid validation have not been performed"* / *"The 12 new-map raid checks have not
  been run"* — per HANDOFF, 9 of the 13 non-Equator maps were never loaded in a raid; terrain maps
  (Custom, Shoreline, Woods) still need their first runtime validation.

### Distribution

- [dist-manifest.md](dist-manifest.md) — assembles `dist/` from the full build, fixes a client csproj
  glob bug that could non-deterministically stage stale S1 bundles into a release build, and writes
  `tools/deploy-to-spt.ps1` (run dry-run only). Establishes the loading contract `BundleHost.cs`/
  `NavMeshInstaller.cs`/`ShaderRebind.cs` read `dist/` against. Open item: its own file count (7 files
  for Equator alone) is now stale — see `fix-preset-cab-and-bundle-lifetime.md` and
  `remaining-maps.md` for the current 14-map/42-bundle shape — but the mechanism and the deploy script
  it wrote are still what's in use.

## Historical — superseded iteration logs

These record a past round-trip in an iterative build/fix/validate cycle; read the doc named as
successor instead unless you're specifically investigating that cycle's history.

- [wp1.md](wp1.md) — original extractor (§B.WP1) log, S1 slice only; superseded by the generalized
  extractor documented in `impl-texture-dimensions.md`, `fix-enabled-state.md` and `remaining-maps.md`.
- [wp2.md](wp2.md) — original Unity importer/NavMesh/bundle-build log, blocked on a Unity Editor
  SIGSEGV; superseded by `s1-fix-r1.md` (root-caused and fixed the blocker) and every full pipeline run
  since.
- [wp3.md](wp3.md) — original preset-bundle patcher log; superseded by
  `fix-preset-cab-and-bundle-lifetime.md` (fixed an internal-name collision this version still had) and
  `remaining-maps.md`'s 14-preset build.
- [wp4.md](wp4.md) — original client plugin log, compiles but untested in-game; superseded by every
  later host-tested client pass starting with `impl-raidstart-repairs.md`.
- [wp5.md](wp5.md) — original server mod log; superseded by `multimap-generalize.md`'s map-parameterized
  server and every map's own registration since.
- [wp6-r1.md](wp6-r1.md) — original validator log, `validate_bundle_roundtrip.py` correctly failing
  because WP2 had produced nothing yet; superseded by `s1-validate-r1.md` through `s2-validate-r2.md`'s
  real runs.
- [fix-cs.md](fix-cs.md) — fixes a locale-key registration bug (wrong field, `Id` vs `_Id`) and a
  preset-patcher output-path drift found in review; both fixes are now baseline behavior, folded into
  every later pass.
- [s1-finish.md](s1-finish.md) — fixes the Cecil/SIGSEGV crash but hits a second, then-undiagnosed
  Unity Editor blocker; superseded by `s1-fix-r1.md`, which root-caused and fixed that second blocker.
- [s1-fix-r1.md](s1-fix-r1.md) — fixes the "second blocker" (an `AddComponent<LevelSettings>` Editor
  limitation), first clean end-to-end S1 run; superseded by `s1-fix-r2.md`, which found the NavMesh
  "sources=0" root cause this pass left open.
- [s1-fix-r2.md](s1-fix-r2.md) — fixes the NavMesh 0-sources bake root cause; superseded by
  `s2-build.md`'s full 6-scene build using the same fix.
- [s1-validate-r1.md](s1-validate-r1.md) — first S1 validator run; fixes 2 validator bugs, finds one
  genuine gap (Scripts-scene MonoBehaviour identity); superseded by `s1-validate-r2.md`/`r3.md` and,
  for the identity gap specifically, by `identity-fix.md`.
- [s1-validate-r2.md](s1-validate-r2.md) — re-run after `s1-fix-r1.md`, all three validators clean;
  superseded by `s1-validate-r3.md`'s fresh, from-scratch re-validation.
- [s1-validate-r3.md](s1-validate-r3.md) — fresh full re-validation of S1, confirms clean; superseded by
  `s2-build.md`'s move to the full 6-scene build.
- [s2-build.md](s2-build.md) — first full 6-scene (S2) build; 3 validator FAILs investigated but not yet
  fixed; superseded by `s2-fix-r1.md`.
- [s2-fix-r1.md](s2-fix-r1.md) — fixes the S2 FAILs (extractor spawn-anchor data loss, validator
  SkinnedMeshRenderer-blind counts); superseded by `s2-validate-r2.md`, which confirms the fixes hold.
- [s2-validate-r1.md](s2-validate-r1.md) — strict S2 validation, independently re-confirms and
  root-causes the same 3 FAILs `s2-build.md` found; superseded by `s2-validate-r2.md`.
- [s2-validate-r2.md](s2-validate-r2.md) — strict re-validation post `s2-fix-r1.md`, all checks pass;
  the last S2-specific validation doc, operationally superseded by `identity-fix.md` (adds the mandatory
  identity patch S2 didn't yet have) and every full-map build since (`bay5-port.md`,
  `remaining-maps.md`).
- [audit-load-chain.md](audit-load-chain.md) — decompile-traced raid-load NRE crash chain
  (`SpatialAudioSystem` → `EnvironmentManager` → `EffectsController`/`FrostbiteEffect`); superseded by
  `impl-raidstart-repairs.md`, which implements every fix this audit lists.
- [audit-reference-repairs.md](audit-reference-repairs.md) — maps which ManimalIcebreaker/
  InterchangeRework/Labs reference-mod repairs apply to this port; superseded by
  `impl-raidstart-repairs.md` (implements the applicable findings) and `fix-raidstart.md` (implements a
  further finding this audit flagged).

## Where is X documented?

| Question | Doc(s) |
|---|---|
| How do bots spawn (scavs and PMCs)? | `bots-stage1.md` (scavs); `bots-stage3.md` (PMC squads via `BossLocationSpawn`) |
| Why did bots not walk (stand frozen)? | `bots-stage1.md` — 0×0×0 voxel grid, empty `PatrolWays`, null `AICorePointHolder`/`AIPlaceInfoHolder` lists |
| How are exfils authored? | `exfils-stage2.md` — runtime `ExfiltrationPoint`/`ExitTriggerSettings` authoring + smoke signals |
| How is the NavMesh baked, and what are its known gaps? | `AGENTS.md` "Traps" (bake volume y −8..12); `s1-fix-r2.md` (0-sources bake root cause); `bay5-port.md` §10a (open-water walkable defect); `exfils-stage2.md` (booth doors/FCK shutter still baked-in islands, HANDOFF's Stage 1 bake-exclusion item) |
| How is a map's `base.json` generated? | `tools/server/make_base_json.py`, used from `bots-stage3.md` (`apply_boss_waves`), `remaining-maps.md` and `bay5-port.md`; `wp5.md` for the original server load side |
| How do I add a map? | `remaining-maps.md` "Reproduce an additional map" (steps 1–5), built on the abstraction in `multimap-generalize.md`; `bay5-port.md` is a worked example |
| What is "the identity patch"? | `identity-fix.md` — `tools/bundle/patch_monoscript_identity.py`, the mandatory MonoScript-assembly-name post-build step |
| How do the lighting/weather repairs work? | `donor-settings-camera-weather.md` (current, most detailed); `weather-rig-stage0.md` (base harvest/activation mechanism); `fix-lighting-movement.md` (non-rig fallback multiplier); `daytime-and-visuals.md` (time-of-day/camera/sea) |
| How do I host-test? | `AGENTS.md` "Host testing (GPU host, via MCP)"; worked examples in `bots-stage1.md`, `exfils-stage2.md`, `bots-stage3.md` |
| What is deferred in combat? | `fix-combat.md` "Deferred"; cross-check `HANDOFF.md`'s Stage 1 table — `BallisticTwinRepairs.cs` and `EffectsControllerRepairs.DeathScreenEffectsGuard` are now built, host test pending |
| What does the F10 inspector do? | `shader-compat-and-inspector.md` §4 "F10 scene inspector" (`SceneInspector.cs`) — dumps the object under the crosshair (hierarchy path, shader, material properties, mesh) to the log |
| How does Bay5 differ (from Equator)? | `bay5-port.md` (the port itself, TeamFight preset); `donor-settings-camera-weather.md` §12–§20 (Bay5 ran the donor rig by default before Equator did; its puddles are authored art, not rain-cover-driven; `IndoorReflectionStrength` defaults `1.0` vs Equator's `0.3`); `shader-compat-and-inspector.md` (Bay5-specific ground shader value fixes) |
| What is the shader sidecar? | `wp2.md` (origin: `StandinShaderGenerator` + `material-shaders.json`); `dist-manifest.md` (how it's staged into `dist/` and loaded) |
| How are bundles served to the client? | `dist-manifest.md` (`BundleHost.cs`'s expected paths); `fix-preset-cab-and-bundle-lifetime.md` (the refcount/internal-name fixes to that same class) |
| Which decisions are fixed (not up for debate)? | `AGENTS.md` "Decisions that shape every task"; `HANDOFF.md` "Decisions (user, 2026-09-20)"; both point to `docs/SPEC.md`'s "Decisions 2026-09-20" section as the source |

## Added 2026-09-20 — distribution repair and ArenaMode acceptance

- [distribution-sidecars.md](distribution-sidecars.md) — descriptor discovery accepts bot/exit
  sidecars without weakening malformed-descriptor or exact-distribution checks; six regressions.
- [equator-arenamode-integration.md](equator-arenamode-integration.md) — ten bot/four player markers,
  distant spawn coverage, independent review, deployment identities, native Deathmatch/Gun Game
  admission, replenishment, combat respawns and persisted-profile return on the GPU host.

## Added 2026-09-21 — Bay5 gameplay and Equator specular cap

- [bay5-arenamode-integration.md](bay5-arenamode-integration.md) — four player and ten bot markers,
  distant coverage, native mode admission/respawns/profile protection, two ordinary exits, numeric
  collision-layer parity in the NavMesh bake, and the final build/deployment acceptance checkpoints.
- [equator-specular-cap.md](equator-specular-cap.md) — map-wide reversible `p0/Reflective` ceiling,
  same-view native comparisons, indoor composition, and repeat-raid donor camera/cover initialization.

## Added 2026-09-21 — Bay5 original harbor background

- [bay5-background.md](bay5-background.md) — original background extraction and native crane/container
  views, bounded weather/navigation scans, legacy full-map preset regeneration, one corrected bot
  marker, connected runtime patrol graph, both ordinary exits and final Deathmatch/profile acceptance.

## Added 2026-09-21 — Bay5 covered-floor material response

- [bay5-indoor-materials.md](bay5-indoor-materials.md) — mixed Hangar mesh diagnosis, covered-floor
  subdivision, independent reflection/smoothness controls, bounded client deployment, 348 tests,
  native interior/exterior comparisons, glass-cover rescans and Bay5–Equator–Bay5 cleanup acceptance.
  Authored puddles and approximate roof-edge transitions remain explicit visual limits.

## Added 2026-09-22 — Bay5 original material response and harbor ambience

- [bay5-native-features.md](bay5-native-features.md) — original ballistic material components,
  native harbor players and shoreline audio, source and bundle validation, cubemap color-space
  correction, and explicit deferral of the originally inactive breakable-glass objects.

## Added 2026-09-22 — Equator original material response and indoor ambience

- [equator-native-features.md](equator-native-features.md) — opt-in source material/audio port,
  original active/inactive source graph, native indoor route, source/package/bundle checks,
  legacy spawn-flow preservation and two cubemap sampling corrections. This is an offline
  checkpoint: deployment and native acceptance wait for the user's shared-host release.

## Added 2026-09-22 — Equator native acceptance after host release

- [equator-native-features.md](equator-native-features.md) — appended deployment and fresh-process
  proof, actual original material hits, Equator–Bay5–Equator audio playback and teardown,
  unchanged settings/navigation, matched atrium view and final 440/440 return. Technical
  playback passed; muted-host audible balance remains a user check. Supersedes the earlier hold.

## Added 2026-09-22 — Bay5 DynamicMaps preparation

- [bay5-dynamicmaps.md](bay5-dynamicmaps.md) — local ground plan with varying floor heights,
  native ground projection, independent geometry review, Equator byte-identity regression
  and exact two-file deployment support. Staged only; host deployment and native tests
  await explicit user release.

## Added 2026-09-22 — Bay5 DynamicMaps native acceptance

- [bay5-dynamicmaps.md](bay5-dynamicmaps.md) — appended two-file deployment, fresh-process
  rendering and coordinate checks, all cardinal directions, raised warehouse ground,
  both live exits and normal north extraction with 440/440 HP. Supersedes the staging hold.
