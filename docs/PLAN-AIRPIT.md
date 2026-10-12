# Plan: AirPit — Third Release Map at Equator/Bay5 Quality

Source: three read-only surveys run 2026-10-11 (extraction/validation pipeline, client/server/editor/release
plumbing, map content and gameplay data), each followed by an independent verifier pass whose corrections are
folded in below; "drop" verdicts were removed. Probes used UnityPy 1.25.2 read-only on the live Arena install
(0.6.0.47752, Unity 6000.3.6f1) and on `work/extract/airpit`; no repo file, SPT install or Arena file was
changed to produce this plan. Evidence is quoted as file:line, counts, or object paths; guesses are marked
**(guess)**. Effort scale: S = up to half a day, M = 1-2 days, L = 3-5 days, XL = more (all rough, agent plus host
time). Status: **nothing below is implemented yet**; update this file (or HANDOFF.md) as items land.

AirPit today (2026-09-15 spike): package `work/extract/airpit` (0.5.4 source, manifest without `native_features`),
bundles `work/build/airpit` (scenes 664,670,450 B, navmesh 460,078 B, preset 6,859 B), `db/arena_airpit/base.json`
with 0 spawn points / 0 exits / 0 waves / `BotMax 0` / name "Arena: Air Pit (spike)", never played on the host,
registered only in `ArenaMapRegistry.Catalog` (`ArenaMapDescriptor.cs:301-315`), parked out of `All`
(`ArenaMapDescriptor.cs:508`) and out of `dist/` (`tools/park-unreleased-dist.ps1:29`).

## 0. Decisions and scope

- **AirPit becomes the third released map** (user request 2026-10-11: "continue with AirPit"). Equator and Bay5
  stay exactly as released: their accepted `*-native-v5` packages, bundles and hashes must remain byte-identical
  (baseline in `docs/dist-manifest.md`). Nothing here refactors `extract_equator.py` or `extract_bay5.py`.
- **Quality bar = Equator/Bay5 as shipped** (PLAN-TWO-MAPS items 1-10 plus decals, MultiFlare, spatial audio):
  native ballistics, ambient audio, NavMesh modifiers, rain cover, light culling, decals, AI anchors, spatial
  acoustics, menu art and text. Where AirPit has nothing to port (physics props: 0; MultiFlare: 0 active flares;
  water: none) the item is recorded as a no-op, not built.
- **Source = the live Arena 0.6.0.47752 install, re-extracted** (recommended; confirm in §4.1). Reasons: (1) every
  native extractor reads the live install, so the 0.5.4 package cannot gain native features without a
  re-extract; (2) the content delta is tiny: 4 of 5 scenes are identical, `Arena_AirPit_ShootOut` differs in
  214 of 20,252 objects (Fence_Armored_1/2 and tarpaulin props), 53 of 2,487 mesh ids are re-keyed (31 texture
  ids re-keyed, 731 textures pixel-identical), object counts, warnings (240) and errors (0) are unchanged;
  (3) BuildSettings indices and the plan digest (`0a6b5a96...`) are unchanged in 0.6.0
  (`docs/progress/arena-update-20261003.md`). Consequence: AirPit is the first map built from 0.6.0 content while
  Equator/Bay5 stay 0.5.4; the old `work/build/airpit` bundle is stale and is overwritten, never patched.
  The SPT bundle target stays Unity 2022.3.43f1.
- **Generic, plan-driven pipeline so maps 4-14 reuse it.** Native-feature extraction moves into one shared
  module driven by the source plan (scene roles) and per-map contract data files; the Equator/Bay5 literals in
  validators are converted to data with a parity test. Plans of the other eleven maps stay untouched (adding keys
  changes a plan's digest and would invalidate their packages).
- **Package naming:** `work/extract/airpit-native-v5` (v5 = feature level: adds static decals; source version
  0.6.0 is recorded in the manifest provenance). Build output `work/build/airpit`.
- **Release promotion has two stages.** Client loops run over `ArenaMapRegistry.All`, so AirPit must be in `All`
  to be testable on the host. The lock-step registry change (item 9) therefore lands on the `feature/airpit`
  branch early; the public release (version, docs, ZIP) is item 25. Minimal literal edits, not new frameworks:
  the proposed `ReleasedMaps.props` generator, `RuntimeFeatures` vocabulary and `BotProfile` descriptor group
  were rejected (client csproj `:276-280` deliberately keeps explicit per-map lists so "exactly N files" checks
  stay meaningful; `test_park_unreleased_dist.py` copies only the script into a temp root).
- **ArenaMode boundary.** ArenaMapPort owns map identity, bundles, location registration, NavMesh, spawn
  markers, exits, visuals, audio, time/weather. ArenaMode (`D:/SPT41/Development/ArenaMode`, the user's mod,
  read-only for agents) owns admission and everything keyed on the map id; it has no AirPit reference today.
  The checklist for the user is in item 25. A plain SPT raid from the menu stays only the test vehicle.
- **Unchanged:** no loot, no scav raids (`DisabledForScav = true`), glass stays deferred, bundles never in git,
  BSG-derived texts/art only under ignored `work/` and `dist/`, deploy only through `tools/deploy-to-spt.ps1`.
- **Mode: TeamFight composition only** (base ShootOut/Light/DesignStuff scenes plus TeamFight, TeamFight_DesignStuff;
  indices 324/325/40/29/28; roles Scripts 36, Sound 39, AI 33, Culling 30 are excluded). Air Pit 2.0
  (`airpit2.json`, CheckPoint mode, scenes 346-352, its own audio bake `arena_airpit_2.0_sound.audiobakedata`) is a
  different map: keep every lookup keyed unambiguously to the scenes above.

**Top risks (ranked).** (1) Navigation: Arena's own NavMesh walks through ~190 base-scene prop instances that the
port bakes as solids, so connectivity needs data-driven fixes and likely 2-3 Unity iterations (item 8). (2) Lighting:
509 of 813 lights in the Light scene are saved inactive (Equator: 0 of 453), and nobody has established what, if
anything, enables them in Arena at runtime (item 18). (3) Shaders: 127 `Custom/Billboard_FogSheet_Simple` renderers
(Equator 17, Bay5 10) plus 10 `Airpit_mountain` rock renderers have no SPT shader and render magenta (item 11).
(4) Size: projected ZIP about 1.897 GB against a 2 GiB asset limit (item 24). (5) Config persistence after release:
any default changed later needs a `DefaultsRevision` migration, so settle AirPit defaults first (item 10).

## 1. Ordered work items

Sequence: Phase A (generic pipeline, offline, items 1-5) -> Phase B (a playable raid, items 6-12) -> Phase C
(native features in Equator/Bay5 order, items 13-23) -> Phase D (release, items 24-25). Items 6 and 7 can run in
parallel after item 5; items 14-23 are mostly independent once the first host raid (item 12) works.

**Which items share a Unity rebuild** (each full build is about 15 min; the NavMesh bake is the cost):

| Build | Contains | Items |
|---|---|---|
| U1 first playable | fresh 0.6.0 native package (ballistics, decals, light-culling components, NavMesh modifiers, rain-cover importer data), first-guess barriers + bake bounds | 5, 8, 14, 17, 18, 19 |
| U2..Un barrier iterations | corrected `opened_barriers`, bake exclusions, bounds; usually 2-3 builds | 8 |
| URC release candidate | adds native ambient audio (Editor importer), final lists; clean rebuild, all validators | 13, 24, 25 |

Everything else is client, server or sidecar only (new DLL needs an EFT restart; a rebuilt bundle needs an EFT
restart too because loaded bundles stay cached; server DLL hash is checked on the host before the server restart).

### Phase A - Generic extraction plumbing (offline, no Unity, no host)

### 1. Plan scene roles — no rebuild

**What/why.** Feature extractors each carry their own per-map scene table (`extract_raincover.py:54`
`{equator:54,bay5:85}`, `extract_bay5_audio.py:144`, `extract_spatial_audio.py:73`, `extract_multiflare.py:24-33`,
`extract_ai_anchors.py:98-103`). Give the plan the roles so the next map needs no extractor edit.
**Evidence.** AirPit roles verified against the 388-entry BuildSettings: Scripts 36, Sound 39, AI 33, Culling 30,
each path occurs exactly once (no Bay5-style duplicate index trap). `source_plan.py:18`
`SCENE_GROUPS = (scenes, anchor_scenes, navmesh_scenes)` loops over lists, so a dict-valued `feature_scenes` block
does not fit; the cheaper design is a `role` key on the existing `excluded_scenes` records (they already get exact
path-drift checks in `verify_scene_mapping`, and `validate_plan` has no key whitelist). Loading Scripts/Sound via
`unique_records` would make every census load them, so do not add them there.
**Files.** `tools/extract/plans/airpit.json` (role on indices 36/39/33/30), `tools/extract/source_plan.py`
(role vocabulary {scripts, sound, ai, culling}, unique per role, `ai` must also be in `navmesh_scenes`, roles never
on imported `scenes`; helper `scene_for_role(plan, role)`), `tools/tests/test_map_pipeline.py`.
Keep the old per-map dicts for Equator/Bay5 compatibility. Editing `airpit.json` changes only AirPit's digest (no
test pins a digest); `-FullMap` still requires descriptor `all_scene_names == [scripts] + manifest scenes in order`,
which `tools/maps/airpit.json` already satisfies, so do not reorder scenes.
**Effort.** S. **Host test.** None; `python -m unittest ...` plus `extract_map.py --plan ... --census-only` (8 s).
**Done when.** New tests pass (including negative cases: duplicate role, role on imported scene), census-only
still passes.

### 2. Native-feature orchestration in `extract_map.py` — no rebuild

**What/why.** `extract_map.py:146-228` stops after meshes/materials/textures; native packaging exists only inline in
`extract_equator.py:996-1099` (ALL5-gated, literal `arena_equator` at `:1086`) and `extract_bay5.py:275-398`.
**Evidence (dry run, 25 s, zero errors).** Applying the generic augmenters per scene gives BallisticCollider
3,548; StaticDeferredDecal 481; NavMeshModifier 20 + NavMeshModifierVolume 12; SmallPhysicsObject 0;
CullingLightObject 533; VolumetricLight 130; LightFlicker 0; ballistic contract loads from
`D:/SPT41/EscapeFromTarkov_Data/resources.assets` (read-only). A full plain 0.6.0 extraction takes 4m38s
(40,719 objects, 2,487 meshes, 380 materials, 762 textures, 0 errors, 240 warnings).
**Files.** New `tools/extract/native_features.py` holding what `extract_bay5.py:283-308/386-398` does
(`load_contract` once, per-scene augment loop in Equator/Bay5 order, post-write steps navmesh_areas / raincover /
native_audio, manifest blocks `native_ballistics`, `native_audio`, `native_features{schema_version,required,
navmesh_areas,raincover,light_culling,static_decals}`); `--native-features` and `--target` on
`extract_map.py` (store `source.target_data_root`; `source.map_key` is already written by `provenance()`);
`validate_extract.run_extractor` (`:1465-1480`) must pass the same flags for generic packages that carry
`native_features`, otherwise the freshness check (`diff_trees(out2, package)` at `:1550`) can never pass.
`native_features.required` must list `['ballistics', <audio feature>]`; see item 3 for audio-optional.
**Effort.** M. **Risk.** Output must be deterministic (sorted keys, fixed timestamp already supported);
`augment_ballistics` hard-fails on unreviewed payload variants (AirPit passed); the augmenter order must match
Equator/Bay5. **Host test.** None (offline: extraction, then `validate_extract.py` with reproducibility).
**Done when.** `python tools/extract/extract_map.py --plan tools/extract/plans/airpit.json --native-features
--target D:/SPT41/EscapeFromTarkov_Data --output <scratch>` produces the counts above and passes
`validate_extract.py` including reproducibility; Equator/Bay5 extractor outputs untouched.

### 3. Data-driven native contracts and validators (+ optional audio) — no rebuild

**What/why.** `validate_native_features.py` hard-fails any native manifest whose key is not in its `MAPS` table
(`:34-52`, `map_contract :144-156`), requires `native_features`+`native_ballistics`+`native_audio` together
(`:160-170`), and has an Equator-only audio branch (`:224-234`). A third map should add data, not code.
**Files.** New `tools/extract/native_contracts/<key>.json` for equator, bay5, airpit (scene census per index,
audio players/support/banks/clips, expected group mixers, inactive players) read by `validate_native_features.py`;
a golden test proving the Equator/Bay5 data files reproduce the legacy `MAPS` values exactly, then remove the
literals; make the Equator-only audio checks conditional on a contract flag; allow an explicit "audio absent"
contract (`audio: null`) so a ballistics-only package validates (needed because the Sound scene is excluded
today and native audio is item 13); source-recheck by scene index (`:408-442`) stays. AirPit pins:
ballistics `{324:2856, 325:512, 40:28, 29:94, 28:58}` (sum 3,548); decals 481 (derived from package,
`:194-202`); light culling 533/130/0; raincover indoor triggers 2; NavMesh modifiers 20 + volumes 12; audio (when
item 13 lands) schema 2, `level39`, players `AmbientSoundPlayer:26`, support `AmbientSoundPlayerGroup:2`,
banks 9, clips 61.
**Tests.** Negative tests (wrong census must fail; missing feature must fail). Python discovery currently finds
690 cases (docs say 665/669; re-measure in item 0 of the first step), plus new cases.
**Effort.** M. **Risk.** A weakened pin silently passes a wrong package: keep fail-closed behaviour.
**Host test.** None. **Done when.** All three maps validate from data; `validate_bundle_roundtrip` path unchanged.

### 4. Per-map extractor tables read the plan roles — no rebuild

**What/why.** Make the four extractors AirPit needs map-agnostic and run them once.
- `extract_raincover.py` (`:54`, `:285`, `:398-400`): take the Scripts index from role `scripts`. AirPit result:
  2 IndoorTrigger, 0 DryPlane, no WeatherObstacle (`Bus_IndoorTrigger` baked centre (-13.05,1.58,-66.58) size
  13.8x2.4x3.1, whose live transform is about 155 m away so the baked bounds are the right ones; `IndoorTrigger 11`
  centre (-15.18,8.5,41.79) size 221.2x60x177.1 = z -46.8..130.3, i.e. the north ~79 percent of the play length;
  the south apron z -82..-47 is covered only by the Bus trigger).
- `extract_spatial_audio.py` (`:73`, `:135`, `:196`): role `sound` -> `level39`, add a `--map` option, output
  `work/extract/spatial-audio/arena_airpit/`. Dry run (4.4 s): 24 rooms, 94 portals, 78 audio trigger areas,
  bake `arena_airpit_sound.audiobakedata` 481,738 B (276 room pairs, 16,304 routes), 1 room-tone clip, reverb
  `mixer_effects` decoded.
- `extract_ai_anchors.py` (`:470-474`): add `"airpit": (33, "Arena_Airpit_TeamFight_AI")`; writes git-tracked
  `tools/maps/airpit.ai.json` (19 core points, 3 hubs 12/12/11 = 35 subpoints, 38 PatrolPoints all consumed,
  self-validation passes; Equator 14/2/21, Bay5 11/4/43).
- `extract_decals.py` refresh CLI (`:109-121`): allow generic packages (only matters if that path is used).
**Effort.** S each, M in total. **Host test.** None. **Done when.** The four commands run for `arena_airpit`
with the counts above and their tests pass. Staging in the client csproj is added by the consuming items
(20, 21), not here (Release builds error on a listed-but-missing file).

### 5. Re-extract AirPit from Arena 0.6.0 — no rebuild yet

**What/why.** Produce the accepted input and record the content delta.
**Commands.** `python tools/extract/extract_map.py --plan tools/extract/plans/airpit.json --native-features
--target D:/SPT41/EscapeFromTarkov_Data --output work/extract/airpit-native-v5`, then
`python tools/extract/refresh_native_volumes.py work/extract/airpit-native-v5` (no-op: zero Tex3D, 23 Cube + 739
Tex2D), then `python tools/validate/validate_extract.py --package work/extract/airpit-native-v5` (first
`--skip-reproducibility`, then the full run).
**Checks.** Object counts per scene 20,252 / 3,833 / 15,349 / 638 / 647 (reconcile one open discrepancy: a raw
GameObject count of `level40` read 15,357, 8 more than the extracted 15,349; most likely an extractor skip common
to old and new packages, confirm); native counts as in items 2-3; record the 214-object / 53-mesh delta in
`docs/progress/airpit-source-060.md`; texture size grows from native decal materials (435 materials vs 380).
**Effort.** S. **Host test.** None. **Done when.** Package validates with reproducibility; delta documented;
`work/extract/airpit` (old) is left untouched but no longer referenced.

### Phase B - Content for a playable raid

### 6. Spawns, bots, exits sidecars — server rebuild only

**What/why.** `db/arena_airpit/base.json` has no spawn points, `BotMax 0`, no exits/waves, and
`tools/maps/airpit.bots.json` / `airpit.exits.json` do not exist. Without them a raid has no explicit starts, no
PMC waves and ends by timer.
**Evidence.** 20 original markers `SPAWNS/ScavPMCspawn (1..20)` in `Arena_Airpit_TeamFight_DesignStuff`
(= the 20 `SpawnPointMarker` anchors): north 10 at z 75.18..80.07, x -22.4..0.3, yaw 180; south 10 at
z -74.58..-71.70, x -18.1..-6.0, yaw 0; world y 0.08..0.14; `SPAWN_BOUND_FIGHTER` x2 at (-13.94,1.69,76.87) /
(-13.94,1.69,-72.70). Draft values are in Appendix A: 4 player spawns, 14 bot spawns, 2 exits, zone `BotZone_Main`,
`max_bots 8`, Equator's 5 waves and 4 PMC boss waves. All drafted spawns and both exit boxes lie inside Arena's own
single NavMesh component (8,168 m2); tightest clearance 0.30 m at bot spawn east-north (15.14,26.76) and 0.75 m
at west-south (-45.88,-25.91) - move those inward if a dry run fails.
**Files.** `tools/maps/airpit.bots.json`, `tools/maps/airpit.exits.json`, `ArenaMapDescriptor.AirPit.Exits`
(mirror, with a `Bay5MapTests.ExitTransformsMatchTheSidecar...`-style test), regenerate `base.json` with
`tools/server/make_base_json.py` (or `build_all.ps1 -SkipUnityBuild`). Identity text is item 23.
**Effort.** S. **Risk.** Positions come from offline analysis, not a live NavMesh; the faction-to-side split (Usec
north, Bear south) copies Equator's pattern **(guess)**. **Host test.** MAP-RECIPE section 5: wave-fired lines,
`uibridge_bots` brain layers, `uibridge_teleport dryRun=true` for every marker, extraction through both gates.
**Done when.** `make_base_json.py` output contains the 4 starts / 2 exits / waves; mirror tests pass.

### 7. Arena NavMesh oracle and TeamFight composition probe — offline, no Unity

**What/why.** Arena's own source NavMesh is parseable and is the best oracle for navigation: `sharedassets33.assets`
path 2 (AI scene, 68 non-empty tiles, 3,853 polys, Detour variant with a 72-byte tile header: magic `DNAV`,
version 16, then x/y/agentTypeID/polyCount/vertCount/detail counts/bvNodeCount/bmin/bmax/quant; verts float3 at
+72, polys 32 B with 6 x u16 verts, 6 x u16 neighbours, flags, vertCount, area). It is ONE 8,168 m2 component
(x -56..32, z -78..85, y 0..6.46) that contains all spawns, both exits, both portals, both bus-door rows, the
capture point (-13.16,-0.14,1.33) and all 19 AI core points. The LastHero bake (`sharedassets140`) gives the same
answer. Build a `tools/validate` NavMesh-diff script: it compares the port NavMesh (offline collider-grid model
first, the actual baked NavMesh triangulation after each Unity build) against Arena's, and lists solids that Arena
walked through, instead of guessing barrier names.
**Open mystery the probe must answer.** About 190 base-scene prop instances (346 collider objects: SOO sandbags,
barricades, med-camp walls, tennis fences, `Passenger_ladder_02`, `DonB_Ramen_closed` x2, `Airport_Shop_BS_closed`,
`TH_Roll_door_01/02/03_closed`, the two capture-zone `Prison_Metal_Fence_01/(1)`) have colliders inside
Arena-walkable agent columns. Either the excluded Scripts scene (index 36, class census via
`analysis/scripts/census.resolve_script_name`) hides them in TeamFight, or Arena's bake ignores them. Read the
Scripts scene and the other presets (Tournament, LastHero) for a mode/variant toggler. Without the probe, item 8
is guesswork.
**Environment note.** scipy is not installed; use numpy/BFS. UnityPy MonoBehaviour typetrees only read via
`census.resolve_script_name`.
**Effort.** M. **Host test.** None. **Done when.** The diff tool runs on the collider model and reports components
and the ranked list of blocking hierarchy clusters; the probe states which mechanism hides the props (or that it
is unknown).

### 8. Round-start barriers, bake bounds, prop exclusions — Unity rebuild (U1, then U2..Un)

**What/why.** Arena's TeamFight starts with closed gates; the port bakes every collider. Ground truth: Arena's
`GateEntity` components sit on exactly `BUS_DOORS`, `BUS_LIGHT`, `PASSPORT_CONTROL_DOORS`,
`PASSPORT_CONTROL_EMISSIVE`.
**Evidence.** No `Door_Sliding_Respawn` exists in any AirPit scene. `BUS_DOORS` (TeamFight_DesignStuff): 6
`Apron_bus_MAZ_doors` assemblies (223 objects, COLLIDER layer 18 + BALLISTIC layer 12 per leaf) at z -65.0 and
-68.2 in front of the south spawn pocket (134 m2, x -20.1..-4.1, z -77.7..-66.1). `PASSPORT_CONTROL_DOORS`:
16 `Passport_control_01_Door` leaves at z 66.28 / 69.12; each leaf collider is 1.09 x 1.35 x 0.1 m = the full
lane width, the NavMesh passes the lane only as 8 strips about 0.55 m wide, so the doors DO seal the north spawn
field (~480 m2). Both roots are mandatory (an earlier offline model that said the passport doors did not matter was
wrong). Lane door names repeat (`Passport_control_01_Door (4)` x4), so only the root path works. The two portal
fences `SBG_Arena_AirPit_ShootOut/SOO/Prison_Metal_Fence_01 (2)` at (-23.50,0,50.54) and `(3)` at (-0.38,0,50.54)
are NOT connectivity-critical (the z 50.5 facade has 4 NavMesh openings); list them for gameplay flow only.
Opening the `PASSPORT_CONTROL_DOORS` root also removes the RED/GREEN fog indicators (and about 90 of the 127
fog-sheet renderers become moot, see item 11). Single-segment root paths are supported by
`BoothDoorRepairs.FindByPath` and `BakeNavMesh.ResolveOpenedBarriers`. Bounds: the spike bake used the renderer
union (Center (-64.43,44.61,-507.17), Extents (2132.5,125.69,1604.49), 5,309 sources, 29,596 vertices / 13,586
triangles vs Equator 10,404 / 4,572, `work/build/airpit/unity_build_all_20260915_160411.log:33323,33335`) because
AirPit's far scenery (mountains to +-1.6 km, runway, forecourt) sits in the main scene. AGENTS.md says the current
bake volume is y -8..12; re-read `BakeNavMesh.cs:281-294` first and confirm which clip applies.
**Steps.** (a) Add `opened_barriers` `["BUS_DOORS","PASSPORT_CONTROL_DOORS", <two fence paths>]` to
`tools/maps/airpit.json` and `ArenaMapDescriptor.AirPit.OpenedBarriers`; relax
`MapDescriptorConsistencyTests.cs:57-75` (it asserts `Assert.Contains('/')` and `Assert.Empty` for every map
except Equator/Bay5, which fails for root paths and for AirPit) and add an AirPit
`OpenedBarriers_DescriptorMirrorsSidecar` test. (b) Add a descriptor-driven `bake_bounds` (x/z box, optional y)
read by `ArenaPaths.Map` and `BakeNavMesh.Bake` in place of the renderer union (about 6 lines plus a test). Proposed
x -80..56, z -96..92 (Arena component is x -56..32, z -78..85; `SPAWN_BOUND` AABB reaches z 85.2); y per §4.3.
(c) Apply the item 7 result: prop clusters that Arena's mode hides become more `opened_barriers` entries (same
runtime-deactivate plus bake-exclude behaviour); if sibling-duplicate names make a path ambiguous, list the parent
root or add an indexed-path syntax to both `FindByPath` and `ResolveOpenedBarriers`. (d) Any other object the client
disables at runtime must also be added to `BakeNavMesh.RoundOpenedReason`. Fallback candidates if a dry run still
fails: `TH_Roll_door_01/02/03_closed` (-43.3,-43.5; -36.8,-26.8; -44.0,-10.1), `Airport_Shop_BS_closed` (17.6,23.8),
`DonB_Ramen_closed` x2, the two capture-zone fences.
**Already generic, verify only:** `LevelBorders` (67 cubes, layer 29, in the BASE scene: a ShootOut-mode bound, not
the TeamFight bound; `MapBorderRepairs` removes layer-29 objects by default and the bake excludes layer 29);
`SPAWN_BOUND_FIGHTER` (disabled by `SpawnPlacementRepairs`, bake excludes the `SPAWN_BOUND` prefix); the 20
`NavMeshModifier ignoreFromBuild` spawn markers.
**Effort.** L (Unity iterations). **Rebuild.** Yes. **Host test.** Log `[ArenaMapPort][RaidFix] spawn openings:`
shows the barriers deactivated and every player-spawn ray clear; `[ArenaMapPort][NavMesh] added navmesh ... N
vertices` identifies the live bake after an EFT restart; `uibridge_teleport dryRun=true` from both spawn strips to
the capture point and to all 19 Arena core points and 38 patrol points must be `reachable=true`; walk north spawn
-> portal and south spawn -> bus -> apron once each.
**Done when.** One NavMesh component covers spawns, capture point, both exits and all 19 core points; patrol-way log
reports one connected component; the NavMesh diff against Arena lists no unexplained solids.

### 9. Lock-step registry promotion on the branch — no Unity rebuild

**What/why.** AirPit must be in `ArenaMapRegistry.All` for the client loops to bind it, and the park scripts move
AirPit's dist files out on every Release build until the park list changes (client csproj `:307`, server `:104`).
Everything below changes in one commit or the build archives AirPit's files / validators fail.
**Files.**
- `ArenaMapDescriptor.cs:508` `All = {Equator, Bay5, AirPit}` (keep the `new[] { A, B }` shape: the regex in
  `validate_map_descriptors.py` only parses that).
- `validate_map_descriptors.py:336-337` `RELEASE_MAP_NAMES/KEYS`; `tools/park-unreleased-dist.ps1:29` (drop
  `airpit`; 11 parked); `tools/validate/validate_distribution.py` (key sets derive from `RELEASE_MAP_KEYS`; the
  overview-folder (`:32-41`) and flare (`:53`) lists stay two-map, allow AirPit without them).
- Client csproj `ArenaMapPortStageWpOutputs` (`:276-280`): AirPit rows (scenes + navmesh from `work/build/airpit`,
  `work/build/preset/arena_airpit_preset.bundle`, `material-shaders.json` staged as
  `material-shaders.arena_airpit.json`) with `<Error>` rows; server csproj `:89` Content and `:113-114` DbFiles.
- Description pack lock-step: `ArenaMapDescriptions.cs:32` does `maps.SetEquals(values.Keys)` against `All` and
  discards the whole pack on mismatch, so ALL maps would fall back to the "Internal test build" text. Add
  `arena_airpit` to `DESCRIPTION_KEYS` (item 23) and re-run `extract_map_descriptions.py` in the same change.
- `MapConfigVisibility.cs:56-58` hides every Catalog section not literally `arena_equator`/`arena_bay5`, so AirPit's
  tuning stays invisible in developer mode: use `!ArenaMapRegistry.All.Any(m => m.Key == map.Key)`.
- Tests that flip: `ReleaseConfigurationTests.cs:74-76` (3 keys, 11 parked; extend: bind `Lighting (arena_airpit)`
  visible in developer mode, `Lighting (arena_bowl)` hidden in both); `test_park_unreleased_dist.py:41-43,52,57`
  (11 stems, 60 -> 55 rows; its fixtures use airpit at `:70,:93-99`, switch to a still-parked map);
  `ArenaMapBannersTests.cs:64-70,:78,:213` and `LocationRegistrationTests.cs:289-291,:320-321,:339-340` (AirPit as
  the "no banner/empty exits" fixture -> Bowl); `test_validate_distribution.py`, `test_banners.py`,
  `test_loadscreens.py`, `test_map_descriptions.py`; `FixedRaidTimeWeatherTests.cs:25-68` per item 10.
**Effort.** M. **Risk.** A stale pin only fails later; Equator/Bay5 dist hashes must stay identical.
**Host test.** Offline gate: `dotnet test -c Release`, `python -m unittest discover`, park test,
`validate_distribution.py` -> three maps. **Done when.** All suites green with 3 released / 11 parked and the
Equator/Bay5 artifact hashes unchanged.

### 10. Runtime defaults for the first look: weather rig, sun keys, time policy, saved config — no Unity rebuild

**What/why.** With `Donor=Off` (the default for any map not named in `WeatherRig.cs:307-314`) `IsActiveForCurrentMap`
is false and 12 client files skip: no sky/sun/rain/TOD clock, no donor camera/exposure, no rain-cover depth photo,
flat stub ambient. 293 of AirPit's 380 materials (77 percent; Equator 65, Bay5 67) are `p0/*` deferred shaders whose
ambient comes from the rig, and AirPit has no Directional light (0 in all five scenes), so `Off` reproduces the
2026-09-14 "dark silhouettes" failure (rig-fed ambient evidence:
`docs/progress/donor-settings-camera-weather.md` sections 12, 16, 18, 20).
**Steps.**
- Weather rig: add AirPit to the `rigDefaults`/ZeroLevel branch (one boolean plus one ternary): donor
  `Reserve_Base_Scripts` (Full), `ZeroLevel -4.4` (the 20 spawn markers sit at y 0.08-0.14, same "4.4 m below
  walkable ground" rule as Equator; **a rule of thumb to be measured on the host, not a measurement**),
  `HeightFalloff 0.025`, `ReserveTier`, `UseDonorCameraPrefab true`. Non-Equator maps already bind the neutral
  values (ambient/reflection 1.0, `IndoorCoverPercent 50`); the tuned Equator values (`AmbientIntensity 0.5`,
  `ReflectionIntensity 0.85`, `IndoorReflectionStrength 0.95`, `ReflectiveSpecularCap 0.3`,
  `IndoorCoverPercent 30`) are `equatorDefaults`-only. No `DefaultsRevision` needed. A `WeatherDonor` /
  `WeatherZeroLevel` descriptor refactor is optional, not required. Add a test pinning the bound defaults of
  Equator and Bay5 so this cannot change them. `Off` stays selectable as the A/B fallback.
- Sun keys: `ArenaLightingRepairs.cs:191` binds `SunIntensityMultiplier`/`SunColorWhiteBlend` unless the map is
  Bay5; condition them on the descriptor's sun data instead (`SunPaths.Length > 0 || map == Equator`) before the
  first AirPit bind so no `RetiredConfiguration` entry is needed. Off-mode fog uses the constant
  `FogColor` (0.62,0.66,0.70) not the sidecar `fog_color` (0.099,0.116,0.122); only matters with `Donor=Off`.
- Time policy (decision §4.2): as registered, AirPit has neither `FixedDayHour` nor `UsesServerTime`, so it falls
  into the hidden legacy path (fixed 12:00, `TimeOfDay/Enabled`, backend weather, no menu toggle;
  `TimeOfDayRepairs.cs:143-200`, `WeatherTestOverride.cs:68-74`). Smallest change if chosen: `UsesServerTime: true`
  in the descriptor plus sidecar `uses_server_time`, then flip `FixedRaidTimeWeatherTests.cs:39-41` and
  `:53-68`, and update the option text at `TimeOfDayRepairs.cs:79-91` ("either released map").
- Persisted config: grep both test installs (`D:/SPT41`, `D:/SPT41vanilla`, read-only) for `arena_airpit` in
  `com.maschine.ArenaMapPort.cfg` and `.developer.cfg` before freezing defaults (host archive today: 0 hits in
  9,849 B and 809 B); any stale `Donor=Off` section on a tester install makes AirPit look dark there.
**Effort.** M. **Host test.** DeveloperMode raid: `[WeatherRig] arena_airpit: activated 'Reserve_Base_Scripts' (Full)`,
`[LevelSettingsOverride] map=arena_airpit ZeroLevel=-4.40`, camera takeover line, `[RainCover] RainBounds ...`; A/B
with `Donor=Off`; quick Equator Day raid for non-regression. **Done when.** Rig active on AirPit, defaults
documented, Equator/Bay5 defaults pinned by test.

### 11. Shader stand-ins for FogSheet and mountain rock — no Unity rebuild (runtime first)

**What/why.** The fallback table in `ShaderRebind.cs:83-91` runs only when `EnableShaderFallbacks` is on AND
`map.Key == Bay5` (`:316-318`); the stand-in fragment is hard-coded magenta (`StandinShaderGenerator.cs:404`).
**Evidence.** AirPit has 11 of 380 materials without an SPT shader (5 `Custom/Billboard_FogSheet_Simple`, 4
`Cloth/ClothShader` + 1 `_backface`, 1 `ANGRYMESH/PBR Rocks/PBR BlendTopDetail (Legacy)`), but the renderer census
is what matters: 127 FogSheet MeshRenderers (73 in the Light scene under Volumetrics/FogSheets/Airport_lamp
groups, all active; 54 in TeamFight_DesignStuff RedFog/GreenFog, 36 active) vs Equator 17 and Bay5 10; 140 Cloth
renderers (Equator 78, Bay5 112); 10 active `Airpit_mountain_LOD0` renderers (AirPit-only rock shader). Unfixed,
the airport lamps carry magenta quads and the horizon magenta mountains.
**Steps.** Per-map allow-list for the fallback table (descriptor flag or sidecar field, default false, Bay5 as
today); AirPit on; A/B on the host: FogSheet -> `Unlit/Transparent Colored` (inferred, may over-brighten), rock ->
a lit substitute; if unacceptable, deactivate the fog-sheet renderers with a runtime repair (cheaper than an
importer change, no rebuild). Both substitutes are inferred and unverified (`docs/progress/shader-compat-and-inspector.md`
says widening needs review). The two Bay5-only fixes (ground hash `ShaderRebind.cs:382`, sea placeholders) stay
Bay5-only.
**Effort.** M. **Host test.** `[RebindShaders]` summary lists unresolved names; screenshots of lamp areas and the
horizon with fallback off/on. **Done when.** No magenta in the three screenshots; chosen policy documented.

### 12. Build U1 and the first host raid (gate) — Unity rebuild

**What/why.** Everything needed to look at the map once: items 1-9 plus first-guess barriers.
**Commands (PowerShell prompt; give absolute descriptor paths).**
`pwsh -NoProfile -Command "& 'D:/ArenaMapPort/tools/build_all.ps1' -MapKey arena_airpit -MapDescriptor D:/ArenaMapPort/tools/maps/airpit.json -PackageDir D:/ArenaMapPort/work/extract/airpit-native-v5 -OutputDir D:/ArenaMapPort/work/build/airpit -FullMap -BakeNavMesh \$true -Verify \$true -SkipGameRefsSync"`,
then a client Release build WITHOUT `DllOnly` (stages bundles), server Release build and tests, then
`pwsh -NoProfile -File tools/deploy-to-spt.ps1 -SptRoot 'D:\SPT41'` (dry run), then with `-Confirm`.
The Unity import must not see `native_audio` yet (the importer returns early when absent; item 3 makes the
validator accept that).
**Host test (SPT-GPU-2, `uibridge-2` + `windows-gpu-2`).** `uibridge_status` (VM build = host disk = loaded, server
DLL hash checked, EFT restarted for new bundles); the debug buttons are retired, so start AirPit through the
ordinary PMC selection flow and confirm click points with `uibridge_snapshot`; use bot amount None for visual
checks. Record: spawn placement, openings log, NavMesh vertex count, dryRun reachability, one screenshot each at
north spawn, hall centre, south apron, a magenta census, F7/F8 baselines. Evidence goes into
`docs/progress/airpit-first-raid.md` (append-only, timestamps, build id, log quotes).
**Effort.** M. **Done when.** Raid starts; both spawn strips reach the capture point; no `[ArenaMapPort]` errors;
a defect list for items 8, 10, 11, 18 exists.

### Phase C - Native features in Equator/Bay5 order

### 13. Ballistic materials and native ambient audio

**Ballistics (rides U1, S).** 3,548 BallisticCollider decoded with no error (2,856 / 512 / 28 / 94 / 58;
Equator 2,892, Bay5 6,322). Host test: fire at concrete, metal and glass on a no-bots raid; ricochet/penetration
and footstep sounds differ per surface; `validate_bundle_roundtrip` -> `validate_native_features.validate_bundle`.
**Ambient audio (L, rebuild, ships in URC).** `Arena_Airpit_Sound` (level39): 26 `AmbientSoundPlayer` (all enabled
and active, no Loop/Season/playOnAwake, no mixer override), 2 `AmbientSoundPlayerGroup` (CommonAmbientOutGroup ->
`CommonAmbOutEffects` with 4 players; CommonAmbientInGroup -> `CommonAmbInEffects` with 22), 9 SoundBanks, 61 clips,
decoded and cross-validated in a dry run. AirPit is the first map with BOTH an Out and an In group, so the three
code layers cannot keep their equator/bay5 booleans:
- Extractor: `extract_bay5_audio.export` takes the scene from role `sound` and schema 2, census pins from the item 3
  contract file (current pins at `:143-146, 223-232, 248`; Equator-only state checks `:248-271` become conditional).
- Editor: `Bay5AudioImporter.cs` throws unless the key is Equator/Bay5 (`:28-29`), pins counts (`:33-41`), hard-codes
  the mixer rule (`:181`) and Equator membership checks (`:211-232`): accept `arena_airpit` (schema 2, directory
  `NativeAudio/AirPit`, root `ArenaMapPort_AirPitNativeAudio`), take counts from the package contract and the
  per-group mixer from the package `mixerName` against the allowlist {CommonAmbInEffects, CommonAmbOutEffects,
  CommonAmbOutBypass}.
- Client: `Bay5AmbientAudio.cs` (map whitelist `:81`, counts `:190-213`, mixer pick `:287-293`) needs a per-group
  mixer choice; `AmbientAudioDefaults.cs:14-16` gets an AirPit tuple (it must keep throwing for unlisted keys);
  new `Audio (arena_airpit)` section so no migration issue.
- Also check the Equator problem class: in `native-audio.json` count players with `volume < deltaVolume` (the
  downward-only random draw can silence quiet sources, fixed on the other maps by `AmbientGainDb`).
**Host test.** `[ArenaMapPort][NativeAudio]` shows 26 players / 2 groups adjusted; walk between hall, apron and
passport hall and confirm both groups audible. **Done when.** Contract validates audio (not null); audio audible in
both zones; Equator/Bay5 audio unchanged on a smoke raid.

### 14. NavMesh modifiers — rides U1

12 `NavMeshModifierVolume` in `Arena_AirPit_ShootOut` (all area 1 "Not Walkable", on `SOO/bruno_helix` at y 4.12:
6 near x +1.4 z 28-31, 6 near x -24.7 z -23.7..-26.4) and 20 `NavMeshModifier` in TeamFight_DesignStuff (all
`ignoreFromBuild`, area 0, matching 1:1 the 20 `ScavPMCspawn` markers, inferred from the equal count). The AI-scene
modifiers (101) are deliberately not ported, same as Equator/Bay5. `NavMeshModifierImporter.StripBakeOnlyComponents`
must remove them before bundle build (the `patch_monoscript_identity.py` trap). Expect a fresh bake count and
record it like Bay5's 22190/10248 -> 22117/10213; confirm the 20 owners are not double-counted with
`RoundOpenedReason`. **Effort.** S. **Host test.** Bake log counts; `dryRun` across the two `bruno_helix` areas.
**Done when.** Bake counts recorded, no `Unity.AI.Navigation` MonoScript in the scenes bundle.

### 15. Barbed wire

26 `EFT.Interactive.BarbedWire` (all in ShootOut; 12 `bruno_helix` trigger BoxColliders at y about 4, plus
`Fence_metall_barbwire_2` x5 props). `BarbedWireRepairs` is name-driven; check its map gate (PLAN-TWO-MAPS item 2
was Bay5-only) and enable it for AirPit; confirm the trigger colliders survive in the bundle.
**Effort.** S. **Host test.** Teleport to a `bruno_helix` volume, step in: HP loss and speed limit in log.
**Done when.** Contact damage works on AirPit and nothing else changed.

### 16. Physics props — no-op

0 `SmallPhysicsObject` and no Rigidbody in any imported scene (Scene 38 `Main_hall` has 334 but is in no AirPit
preset). `PhysicsPropImporter` runs harmlessly. Record in the progress doc; no work.

### 17. Rain cover

The extraction (item 4) gives 2 IndoorTrigger. Behaviour question, not a technical one: `IndoorTrigger 11` makes
the north ~79 percent of the play length indoor (rain/ambience/muffling); the south apron is covered only by the
tiny Bus trigger; AirPit has no authored roof planes, so `RainCoverRepairs`' depth-photo heuristic stays the only
roof source (the glass-and-truss roof: `airport_floor_outer_glass_panels` x -74..53, z 45..88). `RainCoverImporter`
is generic and optional. **Effort.** S. **Host test.** F8 depth readback plus a screenshot of falling rain in the
hall vs on the apron (floor wetness is not evidence); log shows `indoor_trigger_count=2` and the Bus trigger at its
baked centre. **Done when.** Rain readback and screenshots recorded; decide whether any `FullyIndoorCover`-style
default is wanted (§4).

### 18. Light culling and lighting character

**Evidence (corrected).** Components: 533 CullingLightObject (515 in the Light scene; only 298 in an active
hierarchy), 130 VolumetricLight (only 22 active; 108 on inactive objects, so the "7x Equator" GPU worry is largely
moot while inactive stays inactive), 0 LightFlicker. Light scene: 813 lights (469 Point, 344 Spot); 304 active in
hierarchy (248 Point, 56 Spot) and 509 inactive (groups `LIGHT_Disabled`, `ShopsExterior_Disabled`, FANZONE,
Siren; name mix MainSpot 215, Base 140, GI 78, Point light 47). 282-292 of the 304 active lights are saved at
intensity 0 (two counts disagree by 10; reconcile) and 292 carry a CullingLightObject, so restoring
`_maxLightIntensity` through light culling is what makes the map lit. TeamFight_DesignStuff adds 12 lights
(capture-point lamps). No Directional light anywhere. Arena's banner and loading art show overcast daylight through
a glass roof, the Airbus fuselage inside the hall, "NORVAIR" signage and an escalator **(art read, taste call)**.
**Steps.** Ships with the native package (U1). Investigate (S, read-only) what activates the 509 inactive lights:
class census of the Scripts scene (36) and the `_Disabled` names; default policy = leave originally inactive objects
inactive (as MultiFlare does). Unported, parity with Equator/Bay5 only for AreaLight: 31 AreaLight (22 in the Light
scene; 20 `PLANE_AREA_HIGHLIGHTS` are MeshRenderer glow quads), 20 CullingAdvancedLightObject (AirPit only), 7
StencilShadow, 5 AnalyticSource, 1 DistortRenderer; decode them only if the host shows dark signage (§3).
**Effort.** M (S for the package, M for tuning). **Host test.** F7 `LightCullingProbe` (fade bands, volumetric
state), Lighting summary line (expect live counts), screenshots at the capture point, a terrace and the passport
hall against the banner art; screenshots alone are not evidence. **Done when.** Lit hall verified by F7; inactive
lights documented; light-class parity accepted by the user.

### 19. Static projection decals

481 StaticDeferredDecal (423 ShootOut, 4 DesignStuff, 54 TeamFight_DesignStuff; Equator 348, Bay5 499), shaders
already work on the released maps (`Decal/Ultra Deferred Decal Of God 3000`, 3x `Decal/Water Deferred Decal`).
Package carries 435 materials vs 380. Only one native decal manager may exist per raid; destroy the owned manager
on live off. **Effort.** S. **Host test.** `[Decals]` line count 481; AirPit -> Equator -> AirPit sequence shows one
manager; `validate_decals.bundle_errors` inside the bundle roundtrip. **Done when.** Cross-map raid sequence clean.

### 20. AI anchors

Generate `tools/maps/airpit.ai.json` (item 4), add it to the client csproj AI item list (`:186-187`; the sidecar is
staged as `ai/airpit.ai.json`, the `arena_` prefix is stripped by `AiAnchors.Load`) only now that the file exists.
Core points span x -46..26, z -74..77; highest y 6.39, so Arena's bots never patrol levels 2/3. The three hubs sit
around x -33..10, z -11..27: bots may under-patrol the spawn ends **(gameplay observation)**. Runtime only.
**Effort.** S. **Host test.** WITH-bots raid: client log "authored 19 real AI core points, 3 hubs / 35 subpoints" plus
the id-0 alias, `uibridge_bots` Active under `PatrolAssault`, no `ActiveFail`, all anchors `dryRun` reachable.
**Done when.** Bots walk between the real hubs. Bot stand-ins (server brain `factory4_day`, APBS `factory4_day`, SAIN
`Factory`) already work as defaults; change `ArenaMapRegistration.cs:235`, `ApbsCompat.cs:38`,
`SainLocationShim.cs:56-58` only if host bot tests show wrong weapons or difficulty (Interchange for a hall is a
**guess**).

### 21. Spatial acoustics, room reverb, rain audio and per-map client arrays

Spatial graph from item 4 (24 rooms, 94 portals, 16,304 routes; room-tone clip starts only after `GameStatus.Started`;
portal endpoints wired before room initialization; `RoutesDataCount` counts pair ranges). Client edits, each adding
AirPit only because its input now exists: `NativeSpatialAudio.cs:65`, `RoomReverb.cs:44` (original ambient-mixer
reverb via the graph's `mixer_effects`), client csproj `ArenaMapPortStageAcoustics` (`:198-204`, errors if a listed
graph is missing, so add AirPit together with the generated input), `PrecipitationAudio.cs:39` (without it AirPit
shows rain with no rain audio; `PrecipitationCover.cs:19` roof sampling is Equator-only, so everything not covered
by IndoorTrigger classifies Outdoor), `HdrImageTuning.cs:243-244` and `BloomTuning.cs:43` (AirPit currently binds
-1 = keep camera value, which under the rig equals the released 2.10/0.35 defaults). `DynamicMapAssets`,
`NativeMultiFlare`, `Bay5AmbientAudio` literals stay as they are. **Effort.** M. **Host test.** Log shows rooms=24
portals=94 routes_count=16304 before room init; walk through hangar/terminal doors and hear occlusion/reverb change;
`PrecipitationAudio` runs on rain. **Done when.** Equator/Bay5 behaviour unchanged, AirPit acoustics verified.

### 22. MultiFlare — dropped

All 4 FlareLights (20 flares) sit on objects inactive in Arena's own hierarchy (0 of 4 active lights, 0 of 20 active
flares), so there is nothing to render. Skip extraction, staging and wiring. Residue: `NativeMultiFlare` already
tolerates a missing map (`ContainsKey`); the Release/DllOnly staging and `validate_distribution.py` flare loop must
simply stay two-map (handled in item 9). Revisit only if item 18 finds that the 509 inactive lights are meant to be
switched on at runtime.

### 23. Identity text, menu art, official description

- Descriptor + sidecar: `LocaleName` "Arena: Air Pit" (drop "(spike)"), `LocaleDescription` in own words, a menu icon
  slot clear of Equator (700,290) and Bay5 (320,820) (spike slot 295/760 is about 65 units from Bay5). Regenerate
  `base.json` (`Banners: []` stays).
- Original description: `DESCRIPTION_KEYS["arena_airpit"] = "61e800b841ee214fbf1f0e53 Description"` (Arena's
  location id, NOT the sidecar `id_field` `b7ed1752...`; English 83 chars, German 119, Russian 116; Fr/Jp have
  none, as for the other maps); re-run `extract_map_descriptions.py` before the server build
  (`DeployToDist` errors if the pack is missing); update `ArenaMapDescriptionsTests`, `test_map_descriptions.py`.
  Same change as item 9.
- Banner: `Matchmaker_Banner_AirPit` (769x460) via `extract_banners.py` `BANNERS` (lookup by unique name; path ids
  moved in 0.6.0, keep path_id only as a historical hint), server `ArenaMapBanners.cs:49-53` row
  (`arena_airpit_banner`), `test_banners.py`, `ArenaMapBannersTests`.
- Loading art: `AllScreens_Loadingscreen-AirPit` (1920x1080) plus `Arena_MainMenu_Paralax_AirPit-1..4` (2048x1024) in
  `extract_loadscreens.py` `LOADSCREENS`; `-Alt-1..4` exist, whether they belong in the rotation is §4.
- Banners and loadscreens are written by their extractors straight into `dist/` and are neither staged by a csproj
  target nor checked by `validate_distribution.py`: add an exact-set check for AirPit's art.
**Effort.** M. **Host test.** Map-select screen shows ARENA: AIR PIT with banner and original description in
English/German/Russian, no `[UNHANDLED]`; deploy screen shows the art and hides it afterwards. **Done when.** No
"spike" text remains for AirPit; art present in dist and validated.

### Phase D - Release

### 24. Size gate and tracked packager

Projected ZIP = 1,231,754,560 (0.1.0) + 664,670,450 + 460,078 + 6,859 = 1,896,891,947 B, about 250 MB under a 2 GiB
(2,147,483,648 B) per-asset limit (the limit is general knowledge, not a repo document); ZIP deflate gains nothing on
bundles; native-feature rebuilds grow bundles. Equator is LZMA (431,645,444 B), Bay5 and AirPit LZ4 chunks
(`BuildBundles.cs:387-389`, Bay5 785,870,418 B). **Rule: measure first.** Build the real ZIP; only if the total
exceeds about 1.95 GB take LZMA for AirPit via a descriptor/sidecar `bundle_compression` field (instead of the
`Required()` proxy; costs load time/RAM on a 20k-object scene, measure `raid start -> player spawned` against
Bay5's ~9 s) or a separate add-on archive (needs disk-presence gating in the server mod). Move the package builder
from ignored `work/releases/create_*_zip.py` into `tools/` driven by the released list, with a size assertion.
**Effort.** S, M if LZMA/add-on is needed. **Done when.** Final ZIP size recorded and under the gate.
**Superseded (user, 2026-10-12):** the measured three-map dist is about 2.06 GB (the AirPit scenes bundle is 824.6 MB,
not the 664.7 MB above), and the user accepts that the release exceeds 2 GB: the package is split into several ZIP
parts (each under the 2 GiB per-asset limit) instead of LZMA or an add-on archive. The tracked packager builds the
parts from the released list and asserts the per-part limit.

### 25. Release candidate and promotion

- Build URC from scratch (audio included), run every validator, deploy, then the full regression: AirPit no-bots and
  with-bots raids (repeat raid and cross-map sequences for cleanup), Equator and Bay5 smoke raids, both time options,
  `uibridge_status` identity checks.
- Version (decision §4.7; 0.2.0 suggested): `Plugin.cs:30`, `ModMetadata.cs:15`, both csproj `<Version>`
  (Client `:23`, Server `:35`); write `docs/releases/v0.2.0.md`.
- Text naming "two maps": README.md (lines 3, 15-21, 73, 81), FORGE.md:3, client/server csproj `<Description>`
  (`:20`, `:28`), `docs/dist-manifest.md`, `docs/progress/INDEX.md`, AGENTS.md decision line ("AirPit becomes the third
  released map (user, 2026-10-xx)", keep "no loot, no scav raids, ArenaMode is the target"), HANDOFF.md, a new
  `docs/progress/airpit-release.md`; `TimeOfDayRepairs` text.
- ArenaMode hand-off for the user (agents never edit it): `ArenaMapCatalog.cs` (AirPit id, `IsOptionalMap` `:47`,
  `AnyMapAvailable`, default allowlist, `OptionalMapsRevision` 2 -> 3 with the Bay5-style upgrade table);
  `ArenaOptionalMapPolicy.cs:20` `IsEquator(map) ? EquatorId : Bay5Id` would validate AirPit against the Bay5 location
  (latent trap, replace with an id table) and `:32-35` client-config filtering; `ArenaModeConfig.cs:69-83` default entry
  (zone `BotZone_Main`, BotMax 8); `ConfigStore.cs:184-187`; `ArenaParticipantPolicy.cs:19-22,35`;
  `ArenaPortedMapPresentation.cs:26,40` (display name "AIR PIT"; position comes from MapPort's native
  `RelativeMapPos`); keep Tactical/TDM/PitFireTeam rejected for AirPit until certified; an `AIRPIT-MAP-REVIEW.md` like
  `BAY5-MAP-REVIEW.md`. Positions worth passing along as survey notes (one paragraph in the progress doc): TeamFight
  `Capture_Zone` (-13.16,-0.14,1.33); base `CAPTURE_ZONE` (-11.44,-0.07,3.27); grenade trays (14.29,1.38,35.58),
  (19.31,0.42,-37.74), (-6.94,-0.15,1.54); `RESET_POINT_FREE_CAMERA` (-11.39,25.55,2.32). TDM team starts from the 20
  markers are a later feature, as for Equator.
**Effort.** M. **Done when.** §2 checklist is complete and the user has accepted the host evidence.

## 2. Release checklist for the three-map package

1. One lock-step set in one commit (item 9): `All`, park list (11 parked), validator constants, csproj lists, tests
   (3 keys / 11 parked / 55 park rows), description pack (3 keys), banner table.
2. `base.json` for AirPit regenerated (name "Arena: Air Pit", real starts/exits/waves, `Banners: []`); a map's
   `base.json` must never ship without its bundles (map-select builds one button per registered location).
3. `validate_distribution.py` -> `DISTRIBUTION_OK` for 3 maps / 9 bundles / 3 shader sidecars; the exact file set holds
   AirPit's art, AI sidecar, acoustics and description pack.
4. Equator and Bay5 artifacts byte-identical to the 0.1.0 baseline (`docs/dist-manifest.md`); only the client/server
   DLLs and the new AirPit files differ.
5. Suites green: `dotnet test ... -c Release` (docs baseline 606), `python -m unittest discover -s tools/tests -p
   "test_*.py"` (docs baseline 669 with 44 skips; current discovery 690; record the new number), the PowerShell tests
   (`test_unity_log_scan.ps1`, park, deploy tests).
6. Deploy only via `tools/deploy-to-spt.ps1` (dry run first); verify the host server DLL hash before restarting SPT;
   restart EFT for any new client DLL or bundle.
7. Host evidence in `docs/progress/airpit-*.md`: AirPit raids (first-raid, bots, lighting, audio, decals, rain),
   Equator and Bay5 regression, cross-map sequence, repeat raid.
8. Size gate (item 24) and version decision (§4.7) done; `ArenaMapDebug.dll` NOT in the package; no BSG-derived data in
   git (`work/`, `dist/`, `arena-src/` stay ignored); notices for any reused MIT snippet in `THIRD-PARTY-NOTICES.md`.
9. Docs updated (item 25); working tree clean; commits authored `maschine34675`, with the co-author trailer.
10. ArenaMode checklist handed to the user.

## 3. Deliberately NOT in this plan, and why

- **MultiFlare for AirPit** — 0 active flares (item 22). **Physics props** — none exist (item 16). **Water** — no
  WaterContainer; only 3 water-decal materials (mapping them to the native Puddle footstep set is optional polish via
  the sidecar `puddle_water_material_names`). **Native dust / Texture3D** — no LocalDustParticles, no Tex3D.
  **Window breakers, trees, exits/loot classes** — none present (WindowBreaker 72 exist only in unused scene 34).
- **DynamicMaps overview pack** — optional, deferred (M, value 2): needs a `MAP_PROFILES` entry in
  `tools/dynamicmaps/export_equator.py:293-307`, a final NavMesh bundle, a user-coordinated Unity batch run, a
  `deploy-to-spt.ps1` ValidateSet edit and `validate_distribution` changes; the loader tolerates a missing pack.
- **AreaLight / CullingAdvancedLightObject decoding** (L) — only if the host shows visibly dark signage after item 18.
- **Out-of-bounds walls** at the open edges (north neck z 83, runway, apron) — NavMesh clipping only for now; invisible
  walls are new Unity authoring, add only if players leave the area (§4.4).
- **Bundle trimming** (spectator fans: `SCAV_SPECTATORS` is 15,137 of 15,349 DesignStuff objects, inactive and skipped
  at import as on Equator/Bay5; far scenery) — not a problem: the AirPit scenes bundle (664.7 MB) sits between
  Equator (431.6 MB) and Bay5 (785.9 MB).
- **Optional visual polish:** activating `GreenFog` / deactivating `RedFog` as an "open lanes" cue, bus exit lights,
  re-creating the animated gates (Arena's gate components are not EFT classes).
- **Other modes and maps:** Tournament / LastHero presets, Air Pit 2.0 (scenes 346-352), the `Arena_Airpit_new/*`
  scenes (BuildSettings 207-212, covered by no plan), the other eleven maps. Both Airpit alternatives were audited
  on 2026-10-12 (`docs/progress/airpit-source-alternatives.md`): 2.0 is a grey-box CheckPoint/OverRun dev map on a
  shader SPT lacks, `Arena_Airpit_new` an abandoned BlastGang-era art pass bound by no preset; user decision
  2026-10-12: AirPit 1.0 stays.
- **Rejected refactors:** `released` sidecar flag with `ReleasedMaps.props`, a `RuntimeFeatures` vocabulary, a
  `BotProfile` descriptor group (reasons in §0); revisit when the fourth map needs them.
- **Equator-only stacks:** neon night appearance, `EquatorProfiles`, `EquatorRoofShadowRepairs`, anamorphic flare,
  `PrecipitationCover` roof sampling; Bay5-only: water, puddle footsteps, sea placeholder, ground hash, harbor audio.
- **ArenaMode work** (admission, team starts, pickups, presentation) — user-owned; hand-off list in item 25.
- **Loot, scav raids, glass** — unchanged exclusions.

## 4. Open decisions for the user (blocking unless marked)

**Decided 2026-10-11 (user: "alles wie empfohlen übernehmen" — take every recommendation):** 1. re-extract from
Arena 0.6.0; 2. Bay5-style `UsesServerTime` (12:00/00:00 choice) with a night look-test before offering night,
donor rig `Reserve_Base_Scripts` (Full), `Off` stays an F12 A/B; 3. play space = ground floor + wing mezzanine,
bake y -2..8 (Arena parity), levels 2/3 walkable without NavMesh; 4. open edges: NavMesh clipping only;
5. prop obstruction: deactivate mode-hidden props like barriers if the item 7 probe finds the toggler, else
per-instance bake exclusion; 6. native ambient audio now (lands in URC); 7. version 0.2.0, single ZIP if the
measured size stays under about 1.95 GB; 8. light parity accepted (AreaLight & co. unported) unless the host shows
dark signage; 9. shader stand-ins decided on screenshots (A/B); 10. keep the Factory bot stand-ins, pick a free
icon slot, deploy rotation = the five non-Alt images like Equator/Bay5, no DynamicMaps pack in this cycle,
package name `airpit-native-v5`, Usec north / Bear south; 11. ArenaMode: Deathmatch/Gun Game first.
The numbered list below is kept as the record of the options.

1. **Re-extract from Arena 0.6.0 (blocking; recommended yes).** Reasons in §0: native extractors need the live
   install, content delta is about 1 percent of one scene, indices and digest unchanged. Cost: AirPit becomes the only
   map on 0.6.0 content while Equator/Bay5 stay 0.5.4. Alternative (keep 0.5.4 data plus native features) is not
   possible without re-extraction from a source that is no longer installed (inference).
2. **Time of day and weather (blocking).** (a) Bay5-style `UsesServerTime` (native 12:00/00:00 choice, progressing
   sky, backend weather) is the smallest change and recommended; (b) Equator-style fixed day/night with a profile
   system (more work, `EquatorProfiles` is Equator-only); (c) deliberately single fixed 12:00 daytime. Arena's own art
   is overcast daylight; the authored fog colour is dark blue-grey (0.099,0.116,0.122) and 509 of 813 lights are saved
   off, so night uncertified (as for Bay5): run a night look-test before offering it. Weather rig: recommended donor
   rig `Reserve_Base_Scripts` (Full), `Off` stays an F12 A/B.
3. **Which floors are play space (blocking for the bake).** Ground y 0 and wing mezzanine y 3-6.5 are Arena's own
   NavMesh (main component y 0..6.46; only 98 m2 at y 7-14, 89 m2 at y 14-24 vs 11.7k / 10.7k m2 of collider floor at
   y 8.6 (level 2) and 16.1 (level 3)). Recommended: bake y -2..8 only (Arena parity; escalators and stairs to
   levels 2/3 stay walkable for players without NavMesh, bots stay below). Alternative: include level 2 (y -2..12
   like Equator's rule) or level 3.
4. **Open edges** (non-blocking): NavMesh clipping only (recommended) vs invisible boundary walls at the north neck
   (z 83, ~35 m wide), the runway and the south apron.
5. **Prop obstruction policy** (blocking item 8, after the item 7 probe): deactivate mode-hidden props like barriers
   (preferred if the probe finds the toggler), exclude per instance from the bake only, or accept the blocked
   routes.
6. **Native ambient audio now (recommended) or later.** Now = L, touches extractor, Editor importer and client; lands
   in URC. Later = ship without ambience first (needs the optional-audio contract from item 3, already planned).
7. **Version and packaging (blocking item 25, not before):** 0.2.0 suggested; single ZIP if the measured size stays
   under about 1.95 GB, else LZMA for AirPit, else an add-on archive. **Decided 2026-10-12 (user):** the size will
   exceed 2 GB whatever we do; split the release into several ZIP parts.
8. **Light parity** (non-blocking): accept shipping without AreaLight / CullingAdvancedLightObject / StencilShadow /
   AnalyticSource support (Equator/Bay5 ship the same gaps for AreaLight) unless the host shows dark signage.
9. **Shader stand-ins** (non-blocking): accept the A/B approach for FogSheet (Unlit/Transparent Colored vs
   deactivate) and mountains, decided on screenshots.
10. **Small defaults** (non-blocking): bot stand-in (keep Factory defaults vs Interchange), menu icon slot, whether the
    four `-Alt-` parallax images belong in the five-image deploy rotation, DynamicMaps pack yes/no, `airpit-native-v5`
    naming, faction-to-side split of the starts.
11. **ArenaMode scope** (non-blocking, user's mod): Deathmatch/Gun Game first (as Bay5 was) vs also Team Deathmatch
    with the 20 original starts later.

## Appendix A - Draft sidecar values (from the content survey; scratch drafts are not kept)

`tools/maps/airpit.exits.json` (exits `Individual`, 8 s, chance 100, box 4x3x4):

| Name | Position | Nearest player spawn | Offline clearance |
|---|---|---|---|
| Air Pit North Gate | (-11.0, 0.1, 62.0) | 14.8 m | 6.0 m |
| Air Pit South Gate | (-3.0, 0.1, -58.0) | 17.8 m | 4.0 m |

`tools/maps/airpit.bots.json` (zone `BotZone_Main`, `max_bots 8`, waves and 4 PMC boss waves as in `equator.bots.json`):
player spawns (y 0.1, radius 5): All (-10.24, 76.74) rot 180; Usec (0.33, 78.30) rot 180; Bear (-9.94, -74.40) rot 0;
South_All (-16.91, -71.70) rot 0 (ids `arena_airpit-player-{all,usec,bear,south-all}`). Bot spawns (y 0.1, radius 25,
categories Bot + BotPmc, ids `arena_airpit-bot-<name>`): north-end-west (-22.42,79.62) r180; north-end-east
(-2.81,75.18) r180; north-approach (-11.01,60.46); north-hall (-1.23,37.52); north-hall-west (-42.94,33.84);
east-north (15.14,26.76); east-mid (19.12,-2.63); east-south (19.59,-29.58); west-mid (-40.86,3.22); west-south
(-45.88,-25.91); center-east (-3.21,-12.09); plane-south (-15.51,-37.64); south-apron (-14.72,-59.28) r0; south-end
(-6.04,-71.99) r0 (all ground floor; several are Arena AI core points 6, 5, 17, 2, 1, 18, 15, 16, 3, 12, 13). Two end
markers sit within 4.4 m of a player marker (inherent to the 134 m2 south pocket).

## Appendix B - Native census, three maps

| Item | Equator | Bay5 | AirPit |
|---|---|---|---|
| BallisticCollider | 2,892 | 6,322 | 3,548 |
| StaticDeferredDecal | 348 | 499 | 481 |
| CullingLightObject / VolumetricLight / LightFlicker | 472 / 17 / 2 | 186 / 1 / 0 | 533 / 130 / 0 |
| NavMeshModifier / Volume | 31 / 0 | 170 / 134 | 20 / 12 |
| SmallPhysicsObject | 391 | 18 | 0 |
| BarbedWire | 0 | 246 | 26 |
| Ambient players / groups / banks / clips | 25 / 1 / 11 / 65 | 17+6 / 1 / 13 / 106 | 26 / 2 / 9 / 61 |
| Spatial rooms / portals / routes | 23 / 55 / 31,676 | 44 / 89 / 5,924 | 24 / 94 / 16,304 |
| AI core points / hubs / subpoints | 14 / 2 / 21 | 11 / 4 / 43 | 19 / 3 / 35 |
| Active FlareLights (lights) | 25 | 24 | 0 |
