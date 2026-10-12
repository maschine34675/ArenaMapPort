# AirPit source alternatives — Air Pit 2.0, `Arena_Airpit_new`, and the Bowl pink material (2026-10-12)

Three read-only source audits made on 2026-10-12 while AirPit (1.0) was in host testing. The user had read that BSG
intended Air Pit as a test map and later replaced it by "Air Pit 2.0", our 0.5.4 spike export of which was entirely
pink, and asked (a) whether 2.0 can be fixed so that 1.0 can be skipped, (b) what the unreferenced
`Arena_Airpit_new/*` scenes (BuildSettings 207–212, PLAN-AIRPIT's watch item) are, and (c) whether Bowl's single pink
material has the same cause. Source: Arena 0.6.0.47752 (`D:\Battlestate Games`, read-only), the parked 0.5.4 spike
packages under `work/extract/`, and the SPT 4.1.5 shader bundle (555 shaders, 530 distinct names; the census from
PLAN-AIRPIT item 11). No tracked file, build or host state was changed by the audits; scratch scripts live in the
session scratchpad only.

**Decision (user, 2026-10-12 midday, "Airpit 1.0 bleibt"): AirPit 1.0 stays the third map.** Neither alternative is a
playable replacement. In the same message the user dropped the 2 GiB single-ZIP constraint: the release will exceed
2 GB in any case, the accepted answer is a multi-part package (PLAN-AIRPIT item 24 / decision 7 updated).

## 1. Air Pit 2.0 (`Arena_AirPit2`, scenes 346–352)

- **The pink is one shader name.** `Custom/WorldTriplanarFresnelPro` is the only one of the package's 17 shader names
  that SPT does not ship. It sits on 12 `Triplanar_*` materials (White, Yellow, Dark_Green, Beige, Wood, Gray, GrayDark,
  Red, Green, Blue, BlueLight, Orange), all sharing one 512² near-white grid texture (`GridBox_Default`) and differing
  by tint. 1,509 of 3,620 MeshRenderers carry it, about 90 % of the active visible renderers and roughly 95 % of the
  visible surface area: the `ORANGE` block set (131/131), the `PREFABS` blocks (1,297/2,834) and half of
  `AIRPIT_BLOCKOUT_PREFABS`. The remaining 16 names resolve.
- **The fix would be small but is not worth it.** `ShaderStandinPolicy.Table` already carries the row
  `Custom/WorldTriplanarFresnelPro → p0/Reflective/Bumped Specular SMap` (the triplanar materials carry the full p0
  property set, so colours and floats carry over by name), but `FallbacksActive` knows only Bay5 and AirPit, `AirPit2`
  has no `ShaderFallbackNames` and is in `Catalog` only, not in `ArenaMapRegistry.All`, so the client never rebinds it.
  Flag + branch + registry would remove the pink. What remains is a grey-box blockout.
- **2.0 is an unfinished dev map, not a release.** Its preset (`arena_airpit_2.0_preset.bundle`, dated 2026-08-18,
  unchanged by 0.6.0) registers modes CheckPoint and OverRun only (no TeamFight/ShootOut), its DesignStuff scene carries
  `PrisonArenaLevelSettings`, its AI scene (352) is a copy of Bay5's TeamFight AI (2,208 identical nav points, Jaccard
  0.98) whose `NavMeshSurface` points at Bay5's NavMeshData, it has no locale entry, banner, loading screen or map
  points (`MapPoints` for `Arena_AirPit_2.0` is empty) and only a 45 KB sound bake. Its layout is a different,
  east-west terminal, not a redress of 1.0. The Air Pit 1.0 preset (`Arena_AirPit`: TeamFight, ShootOut Solo/Duo/Trio,
  Coop, LastHero) is unchanged in 0.6.0 and keeps the only locale, banner and loading screen set.
- Spike inputs: `work/extract/airpit2` (0.5.4, 91 materials, 839 meshes, four scenes 5,687/68/2/1,177 objects),
  bundles parked under `work/release-parked/20261010T193022881Z-*/Client/bundles/arena_airpit2_*`.

## 2. `Arena_Airpit_new` (scenes 207–212)

| idx | Scene | Size (level bytes) | What it is |
|---|---|---|---|
| 207 | `Arena_Airpit_new_AI` | 1.09 MB | Stripped Prison AI scene: 2,696 `base_navmesh_point`, only **3** AICorePoints (x 101..112, z −27..−26, y −3.7, no floor under them), 2 patrol hubs / 23 points, 3 BornPositions in the same 20 m patch, 44 `AdditionalNavmeshBlock`, 42 `NavMeshModifier`. `NavMeshSettings.m_NavMeshData` is null; the `NavMeshSurface` (AgentType 0, CollectObjects=Volume 144×122×130 m, layer mask 0x40042A00 = 9/11/13/18/30) points at **Prison's** NavMeshData (`sharedassets99`, 1,943 polygons, z −110..3). AI calculation stamp: 2024-01-16, branch `arena/gameplay/art_gameplay`; never recalculated. |
| 208 | `Arena_Airpit_new` (main) | 55.0 MB + 13.2 MB sharedassets + 42 MB `.resS` | 113,046 objects. Two full copies of the level: root `V2` (inactive, 56,268 objects) and `V2.1` (active, 56,681). 75.9 % of all objects are inactive `GRASS` (42,901 per copy, GPUInstancer grass with no manager in any scene). Live: ~11,700 objects, 5,883 renderers, 8,606 colliders, 4,604 ballistic colliders, 53 spot lights, **no directional light**, 106 `CullingLightObject`. 2,329 of Air Pit 2.0's 3,097 renderers (75 %) recur here with the same name and position; on top, real AirPit 1.0 art (Main_hall 157 meshes, TeamFight_DesignStuff 27, Background 38, Tournament 23, Building 7; `Passport_control` 368 objects; `Apron_bus_MAZ`, `airport_seats`, `Baggage_cart`, `aviation_container`) and 110 new meshes (Airbus a320 art, platforms, carts, `Chain_fence_01`, chairs/tables). Missing: `airport_floor`, `DonB_Ramen`, `Prison_Metal_Fence`, `Entrance_portal`. Shaders: 18 names, missing from SPT are `Custom/WorldTriplanarFresnelPro` (10 tinted `Triplanar_*` materials, ~80 % of live slots), `GPUInstancer/Foliage_Simple` (dead grass) and `p0/ShockWave` (2 renderers in 212). |
| 209 | `Arena_Airpit_new_DesignStuff` | 1.9 KB | 2 objects; the `ArenaLevelSettings` object is named `TrainArenaLevelSettings`. |
| 210 | `Arena_Airpit_new_Light` | 56 KB | 107 objects, a Bay5 lighting copy (`MultiFlare_Bay5`, 14 FlareLight, 4 `CullingLightObject`, 4 `VolumetricLight`). |
| 211 | `Arena_Airpit_new_Scripts` | 11.7 MB | A copy of Air Pit 1.0's Scripts scene (63 of its 66 objects; 3 Occlusion Areas missing). Its rain-cover triggers are the 1.0 ones in the old frame (`Bus_IndoorTrigger` 151 m, `IndoorTrigger 11` 105 m away from any live geometry), so the data is stale. |
| 212 | `Arena_Airpit_new_BladstGang_DesignStuff` (sic) | 0.79 MB | Full **BlastGang** (5v5 plant/defuse) mode data: 15 `SpawnPointMarker` with Bay5 names (5 "Usec house" at x 11..18 / z 14..18 inside `SPAWN_BOUND_FIGHTER`, 10 "Bear house" at x 33..60 / z −104..−99 inside `SPAWN_BOUND_DEFENSE`), `BOMB_POINT_A` (−5.9, 5.9, −33.6) and `_B` (55.4, 1.0, 6.8) with `BombActivatedPointEntity` / `PlaceItemTrigger` / `BlastGangPointProjectionSelector`, `BombSpawnPointEntity` (52.1, 5.6, −97.9), one `GateEntity` `DOORS` with 6 `Sliding_Doors`, a 26-object `Capture_Zone`, 2 spectator cameras, 4 partial `LevelBorder` box walls (15.2 / 7.2 / 2.8 / 8.7 m, no enclosure), 24 `StaticDeferredDecal`, 102 `BallisticCollider`, 20 `VolumetricLight`, 4 `LightFlicker`. No `ExcludeLevelBorders` data polygon anywhere in the six scenes. |

- **Arena binds it nowhere.** None of the 36 `StreamingAssets/Windows/maps/*.bundle` presets (863 distinct scene
  paths) references a 207–212 scene; a binary scan of all 15,356 install files finds the name only in the
  BuildSettings (12 hits), `level208` (14 `WindowBreaker` ids), `level210` (1 `LootableContainer` id) and `level348`
  (5 `window_Arena_Airpit_new_*` WindowBreaker ids in the Air Pit 2.0 main scene). No locale key, `MapPoints`,
  `Zone_*` bot-zone asset, `*_sound_info`, `.audiobakedata`, Sound scene or Culling scene exists for it. It is one of
  71 BuildSettings scenes under `Locations` that no preset uses (14 of them in the Airpit folder).
- **The set is not spatially coherent.** The 208 art sits at z 0..113 (34,000 renderers in z 20..40); the BlastGang
  points sit at x −16..69 / z −104..21. Bomb site A has 0 renderers of 208 within 15 m but 586 of Air Pit 1.0's ShootOut
  scene; the defender pocket (10 of 15 markers), bomb A and the bomb spawn float over void (no renderer or collider in
  x 25..70 / z −125..−85 / y −5..15); the AI grid (x −49..51, z −52..49) half-overlaps the art.
- **Reading.** An abandoned or parked January-2024 BlastGang-era "V2" art pass on the Air Pit location, assembled
  from copies of other maps' scenes (Prison AI, Bay5 light, Train settings, Air Pit 1.0 scripts) and never bound,
  localized, culled or sound-baked. Air Pit 2.0 reuses its props and coordinates as a greybox (its `WindowBreaker`
  ids were authored in Airpit_new first) and is the line that received a preset and a 2025-10-03 AI rebake. Which came
  first is an inference from ids and dates, not stated in the data.
- **Port feasibility (dry run only).** The stock extractor accepts a scratch plan (scripts=211, ai=207, no sound /
  culling role) and extracts it in 138 s once the two `Texture2DArray` grass textures (`GrassTexArray`,
  `GrassNormalArray` in `sharedassets95`) are tolerated instead of fatal: 113,904 objects, 682 meshes, 101 materials,
  154 textures, 423 MB package, empty `navmesh_ref`. Estimated scenes bundle 0.25–0.35 GB pruned (0.30–0.52 GB with the
  dead grass and the inactive `V2` copy). As a fourth map it breaks the 2 GiB ZIP budget (Equator + Bay5 + AirPit =
  2.0565 GB, 91 MB headroom; the AirPit scenes bundle is 824.6 MB, not the 664.7 MB PLAN-AIRPIT quoted). Effort per
  PLAN-AIRPIT item: 1–5 S, 6/7/9/11 M, 8 and 12 L. Everything that made AirPit 1.0 portable in two days is absent:
  no Arena NavMesh oracle, no border polygon, no usable AI anchors, spawns and bomb sites to re-author, 80 % of the
  look on a stand-in shader with no sun. Not pursued.

## 3. Bowl's single pink material

Same mechanism as AirPit's PLAN item 11 (shader names EFT 0.16.9.5 does not ship), checked against the 0.5.4 spike
package `work/extract/bowl` (381 materials, 7 scenes):

| Missing shader | Materials | Notes |
|---|---|---|
| `Cloth/ClothShader` | 2 | AirPit already maps it to `Legacy Shaders/Transparent/Cutout/Bumped Diffuse 2 side` (`ShaderStandinPolicy.TwoSidedClothShader`) |
| `Cloth/ClothShader_backface` | 2 | same substitute row |
| `p0/Reflective/Bumped Specular_TwoSided` | 1 | **new**: the `Tarp_Tunnel` material on 12 renderers (`Tarp_Tunnel` ×2, `Tarp_Tunnel_Door`), the one pink object the user saw; needs a new table row (the single-sided `p0/Reflective/Bumped Specular SMap`, or the two-sided cutout substitute if the back faces matter) |

Fix when Bowl is released: `shader_fallback_names` on the Bowl descriptor/sidecar, a Bowl branch in
`FallbacksActive`, one new table row, and a fresh 0.6.0 package with `--native-features` (the spike package predates
every native feature). Not done; Bowl is not in scope.

## Could not determine

- Whether Arena's live servers offer `Arena_AirPit2` or BlastGang on Air Pit; only the install was read.
- How closely the p0 stand-in matches `Custom/WorldTriplanarFresnelPro` (the Arena shader was not decompiled).
- The 0.5.4 content of 207–212 (that install is gone); the 0.6.0 scene files all carry the install date.
- The `V2` vs `V2.1` intent and whether anything enables the GPUInstancer grass at runtime.
