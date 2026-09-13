# Arena Data Census — Arena_Equator vs Arena_AutoService (TDM/TeamFight)

Task key: `arena-census`. Goal: census the built-player data of both candidate maps to design the
extractor and pick the spike map. All data below is read directly from
`X:/Escape from Tarkov Arena/EscapeFromTarkovArena_Data` (read-only) via UnityPy 1.25.2, either from
the small `StreamingAssets/Windows/maps/*_preset.bundle` files (Editor-built, still carry typetrees)
or from the shipped Player data (`level<N>` / `sharedassets<N>.assets[.resS]`, no typetrees — see
Method notes). Unity build: **6000.3.6f1** (confirmed via `BuildSettings.m_Version` in
`globalgamemanagers`), Mono-style serialized layout (PPtr path IDs are 64-bit), IL2CPP scripting
backend per prior session notes.

All helper scripts are in `D:/ArenaMapPort/analysis/scripts/` (`common.py`, `census.py`,
`run_equator.py`, `run_autoservice.py`, plus the one-off probes `01_read_presets.py`,
`02_probe_gm.py`, `03_tagmanager_navsettings.py`, `04_externals.py`). Raw JSON outputs:
`equator_census.json`, `autoservice_census.json`, `equator_tdm_preset.json`,
`autoservice_preset.json`, `arena_gm.json`, `spt_gm.json`, `scene_externals.json`.

## TL;DR recommendation

**Arena_Equator, TeamFight (TDM) mode, is the recommended spike map.** It is smaller on every axis
we measured (≈295 MB of own scene data vs ≈554 MB for AutoService; 880k vs 2.85M vertices; 232 vs
346 own textures; 170 vs 228 own materials) while exercising exactly the same techniques
AutoService does (no static batching on either map, no baked lightmaps on either map, same custom
NavMesh bake pipeline, same spawn/AI marker classes). AutoService is also markedly more vertical
(NavMesh source-bounds Y-extent 60.99 vs Equator's 10.05 — multi-storey building vs a mostly flat
courtyard), which would add elevation/NavMesh-link complexity the spike doesn't need to solve yet.
Full reasoning in the final section.

---

## 1. Mode → scene resolution (preset bundles)

Read via `read_typetree()` on the small Editor-built bundles (these retain typetrees, unlike the
Player data — see §3.2 in Method notes below).

- `X:/.../StreamingAssets/Windows/maps/arena_equator_tdm_02_preset.bundle` — root object
  `Arena_Equator.ScenesPreset` (`ServerName: "Arena_equator_TDM_02"`). Its `GameModes` array maps
  `TeamFight` → child preset `Arena_Equator_TeamFight.ScenesPreset` (also used for `Coop`),
  `LastHero` → `Arena_Equator_LastHero.ScenesPreset` (also `OverRun`), `ShootOutSolo/Duo/Trio` →
  `Arena_Equator_Tournament.ScenesPreset`.
  - Root's own `_scenesResourceKeys` (always loaded, 5 scenes): `Arena_Equator_Scripts.unity`,
    `Arena_Equator.unity` (main geometry), `Arena_Equator_Lighting.unity`,
    `Arena_Equator_DesignStuff.unity`, `Arena_Equator_Sound.unity`.
  - `TeamFight` child's `_scenesResourceKeys` (mode-specific, 4 scenes): `Arena_Equator_TeamFight.unity`,
    `Arena_Equator_TeamFight_DesignStuff.unity`, `Arena_Equator_TeamFight_Culling.unity`,
    `Arena_Equator_TeamFight_AI.unity`.
  - **TDM total = union of 9 scenes.** Evidence:
    `D:/ArenaMapPort/analysis/equator_tdm_preset.json` lines 1–333.
- `X:/.../StreamingAssets/Windows/maps/arena_autoservice_preset.bundle` — root
  `Arena_AutoService.ScenesPreset` (`ServerName: "Arena_AutoService"`). `GameModes`: `ShootOutSolo/
  Duo/Trio` → `Arena_AutoService_ShootOut.ScenesPreset`; `TeamFight`/`Coop` →
  `Arena_AutoService_TeamFight.ScenesPreset`; `LastHero` → `Arena_AutoService_LastHero.ScenesPreset`.
  - Root's base scenes (6, always loaded): `Arena_AutoService_Scripts.unity`,
    `Arena_AutoService_Scripts_Winter.unity`, `Arena_AutoService.unity` (main geometry),
    `Arena_AutoService_AI.unity`, `Arena_AutoService_DesignStuff.unity`, `Arena_AutoService_Sound.unity`.
  - `TeamFight` child's mode-specific scenes (3): `Arena_AutoService_TeamFight_DesignStuff.unity`,
    `Arena_AutoService_Light.unity`, `Arena_AutoService_Culling.unity`.
  - **TeamFight total = union of 9 scenes** (symmetric with Equator). Evidence:
    `D:/ArenaMapPort/analysis/autoservice_preset.json` lines 1–299.

Scene name → BuildSettings index was resolved via `globalgamemanagers.BuildSettings.scenes` (a flat
array; the array index *is* the `level<N>`/`sharedassets<N>.assets` file number):

| Map | Scene role | index | Player files |
|---|---|---|---|
| Equator | main geometry (`Arena_Equator.unity`) | 51 | level51, sharedassets51.assets(+.resS) |
| Equator | TeamFight_AI (NavMesh) | 52 | level52, sharedassets52.assets |
| Equator | DesignStuff (base dressing) | 53 | level53, sharedassets53.assets |
| Equator | Scripts | 54 | level54, sharedassets54.assets(+.resS) |
| Equator | Lighting | 55 | level55, sharedassets55.assets(+.resS) |
| Equator | Sound | 56 | level56, sharedassets56.assets(+.resource) |
| Equator | TeamFight_DesignStuff | 57 | level57, sharedassets57.assets(+.resS) |
| Equator | TeamFight (mode overlay) | 58 | level58, sharedassets58.assets |
| Equator | TeamFight_Culling | 59 | level59, sharedassets59.assets |
| AutoService | main geometry (`Arena_AutoService.unity`) | 62 | level62, sharedassets62.assets(+.resS) |
| AutoService | AI (NavMesh) | 63 | level63, sharedassets63.assets |
| AutoService | DesignStuff | 65 | level65, sharedassets65.assets |
| AutoService | Light (mode-specific) | 66 | level66, sharedassets66.assets(+.resS) |
| AutoService | Sound | 112 | level112, sharedassets112.assets(+.resource) |
| AutoService | Culling (mode-specific) | 135 | level135, sharedassets135.assets |
| AutoService | TeamFight_DesignStuff (mode-specific) | 136 | level136, sharedassets136.assets |
| AutoService | Scripts | 69 | level69, sharedassets69.assets |
| AutoService | Scripts_Winter | 243 | level243, sharedassets243.assets |

Evidence: `D:/ArenaMapPort/analysis/scripts/common.py` (`EQUATOR`/`AUTOSERVICE` dicts), cross-checked
against `arena_gm.json` → `BuildSettings.scenes[51]=="Assets/Content/Locations/Arena_Equator/Arena_Equator.unity"`
etc. (verified programmatically for all 18 indices, all matched — see transcript).

**On-disk size of the full 9-scene TDM set** (own files only, not counting the cross-scene shared
pools listed in §2): **Equator ≈294.7 MB** (dominated by `sharedassets51.assets.resS` = 198.2 MB),
**AutoService ≈553.7 MB** (dominated by `sharedassets62.assets.resS` = 437.5 MB +
`sharedassets62.assets` = 54.6 MB). AutoService is ~1.9× heavier on disk.

---

## 2. Object counts per scene-file & external file references

### 2.1 Object type totals (own scene files only; MonoScript/globalgamemanagers/resources excluded)

| Type | Equator TDM (9 scenes) | AutoService TeamFight (9 scenes) |
|---|---:|---:|
| GameObject | 41,737 | 36,356 |
| Transform | 41,726 | 36,352 |
| MeshFilter | 17,069 | 18,095 |
| MeshRenderer | 15,419 | 16,556 |
| MonoBehaviour | 11,385 | 10,434 |
| MeshCollider | 5,429 | 5,303 |
| LODGroup | 3,601 | 3,085 |
| Mesh | 1,441 | 2,671 |
| BoxCollider | 1,421 | 1,940 |
| CapsuleCollider | 910 | 270 |
| SkinnedMeshRenderer | 506 | 152 |
| Light | 479 | 203 |
| Rigidbody | 413 | 134 |
| Texture2D | 232 | 346 |
| SphereCollider | 171 | 69 |
| Material | 170 | 228 |
| Animator | 99 | 34 |
| AudioClip | 66 | 110 |
| AudioSource | 33 | 56 |
| OcclusionPortal | 21 | 0 |
| ParticleSystem | 4 | 22 |
| OcclusionArea | 3 | 3 |
| ReflectionProbe | 1 | 2 |
| NavMeshData | 1 | 1 |
| OcclusionCullingData | 1 | 1 |
| Camera | 1 | 2 |
| Cubemap | 0 | 1 |

(9 each of RenderSettings/LightmapSettings/NavMeshSettings/PreloadData — one per scene file, as
expected.) Full per-file breakdown (e.g. `level51`=83,348 objects incl. 22,034 GameObjects/13,143
MeshRenderers is the overwhelming majority of Equator's content; `level58` TeamFight-mode overlay is
only 245 objects/59 GameObjects/22 MeshRenderers, confirming it's a thin logic/blocker layer, not new
geometry) is in `equator_census.json`→`object_counts_per_file` and
`autoservice_census.json`→`object_counts_per_file`.

**Geometry complexity: AutoService has ~3.4× Equator's vertices/triangles** (§4) despite having
*fewer* GameObjects/MeshRenderers overall — its meshes are simply denser per object.

### 2.2 External file references (what the extractor must also pull in)

`level<N>.m_Externals` lists every other SerializedFile a scene's objects can point into. Read via
`env.files[...].externals` (not `read_typetree` — this is header-level Unity Fbx/asset dependency
data). Full dump: `D:/ArenaMapPort/analysis/scene_externals.json`.

- **Every scene** references `globalgamemanagers.assets`, `resources.assets`, and the built-in
  `Library/unity default resources` — expected (engine defaults, shared MonoScript pool, common
  resources).
- **The main geometry scenes pull in a large fan-out of *other locations'* `sharedassets<N>.assets`
  files**, i.e. shared prop/material libraries that are not part of either candidate map:
  - Equator main (`level51`): 16 extra externals — `sharedassets{2,3,13,14,17,20,28,29,31,34,36,38,
    41,46,47,49}.assets`.
  - AutoService main (`level62`): 21 extra externals — `sharedassets{2,3,13,17,20,28,29,31,34,37,38,
    40,41,45,46,47,49,50,53,60}.assets` (some overlap with Equator's list, e.g. `2,3,13,17,20,28,29,
    31,34,38,41,46,47,49` — a shared "common decor/prop" pool across Arena locations).
- Smaller mode scenes reference far fewer externals — e.g. `level59` (TeamFight_Culling) references
  only `globalgamemanagers.assets` + `sharedassets2.assets`; `level135` (AutoService Culling)
  references only `globalgamemanagers.assets` + `sharedassets0.assets` + default resources.

**Extractor implication:** a renderer/collider in the main scene can legitimately point at a Mesh or
Material that physically lives in one of ~16–21 *other* locations' shared-asset files, not in the
map's own `sharedassets<N>.assets`. A faithful geometry extractor therefore needs read access to that
whole shared-pool set (`sharedassets{0,2,3,13,14,17,20,28,29,31,32,34,36,37,38,40,41,45,46,47,49,50,
53,60}.assets` at minimum, observed across both candidates), not just the map's own two files. This
was not obvious from the file layout alone and is the single most important finding for extractor
scope.

---

## 3. Static batching — **not used on either map** (contradicts the task's working hypothesis)

Checked **every** `MeshRenderer` in both 9-scene sets (15,419 for Equator, 16,556 for AutoService —
31,975 total) via `read_typetree()` reading `m_StaticBatchInfo{firstSubMesh,subMeshCount}` and
`m_StaticBatchRoot`:

- **`batched_MeshRenderer` = 0 / 15,419 for Equator, 0 / 16,556 for AutoService.** Every renderer
  sampled has `m_StaticBatchInfo == {firstSubMesh: 0, subMeshCount: 0}` and `m_StaticBatchRoot ==
  {m_FileID: 0, m_PathID: 0}` (null). Example raw dump (5 consecutive renderers from `level51`):
  `{"m_StaticBatchInfo": {"firstSubMesh": 0, "subMeshCount": 0}, "m_StaticBatchRoot": {"m_FileID": 0,
  "m_PathID": 0}, ...}` for all 5.
- **No `Mesh` object named "Combined Mesh (root: ...)" or containing "combined" exists** in either
  scene set (`combined_mesh_names` dict is empty in both `equator_census.json` and
  `autoservice_census.json`).
- Each `MeshFilter`/`MeshRenderer` pair therefore references its **own individual, per-object Mesh**
  directly — geometry does not need to be recovered from a combined/world-space batch buffer at all.
  This significantly *simplifies* the extractor compared to the Icebreaker-playbook assumption: no
  submesh-range slicing of a combined buffer, no world→local re-baking of vertex positions is needed;
  each renderer's Transform already gives the correct local→world placement for its own mesh.
- MeshColliders were spot-checked and, as expected given no batching is in play, are simply separate
  convex/non-convex collision meshes per object (§8 has the convex/non-convex counts) — no ambiguity
  about "does the collider reference the combined or the original mesh" because there is no combined
  mesh.

**Inference (marked as such):** static batching is very likely disabled project-wide for Arena's
build (`PlayerSettings` object itself failed to parse cleanly with UnityPy's built-in 6000.3 typetree
— `"Expected to read 868 bytes, but only read 864 bytes"` — so the `staticBatching` flag could not be
read directly to confirm *why*), or Arena's asset pipeline relies on GPU instancing / SRP-style
batching instead (`BuildSettings.enableDynamicBatching: true` was readable and is `true`). Either
way, the empirical renderer scan is conclusive regardless of the exact setting name.

---

## 4. Mesh features

| Metric | Equator (1,441 meshes) | AutoService (2,671 meshes) |
|---|---:|---:|
| Total vertices | 879,891 | 2,846,515 |
| Total triangles | 828,578 | 2,817,302 |
| Avg vertices/mesh | 610.6 | 1,065.5 |
| Index format | 100% `UInt16` (format 0) | 100% `UInt16` (format 0) |
| Skinned meshes (non-empty `m_BindPose`) | 0 | 0 |
| Blend-shape meshes | 0 | 0 |

(Skinning/blend-shapes live on `SkinnedMeshRenderer`s that were counted separately in §2 — 506/152 —
those reference `Mesh` assets stored in shared pools outside our own-file set, e.g. player-body
armatures pulled from a common character-asset library, not local to the map scenes.)

**Submesh-count distribution** (both similar shape, long-tailed): Equator `{1:1200, 2:135, 3:36,
4:31, 5:14, 6:15, 7:3, 9:7}`; AutoService `{1:2196, 2:266, 3:66, 4:44, 5:32, 6:27, 7:9, 8:3, 9:7,
10:2, 11:8, 12:2, 13:3, 14:3, 15:3}` — AutoService has a longer tail of multi-submesh (multi-material)
props, up to 15 submeshes on a single mesh.

**Vertex-channel signature distribution** (channel order = position, normal, tangent, color, uv0–uv7;
1=present with dimension>0, 0=absent), top signatures for Equator (`channel_signature_top`, Equator
only shown — AutoService distribution is analogous):

| Channels present | Count | Meaning |
|---|---:|---|
| pos, normal, tangent, uv0 | 887 | standard static prop (no vertex color) |
| pos, normal, tangent | 444 | untextured/collision-adjacent geometry |
| pos, normal, tangent, color, uv0 | 35 | vertex-painted props |
| pos, normal, tangent, color, uv0, uv1 | 31 | vertex-painted + a 2nd UV set (present even though no lightmaps are baked — see §8, likely a leftover UV2 slot from the authoring pipeline rather than an active lightmap UV) |
| pos, normal (no tangent), uv0 | 14 | simple unlit-normal-mapless props |
| pos, normal, tangent, color, uv0, uv1(again) | 14 | variant of the above |
| pos, normal, tangent, uv0..uv6 (7 UV sets) | 5 | likely SpeedTree/foliage or a decal-projector mesh |

Sample raw Mesh typetree (`palm_plant_01_destroyed_LOD2`, `sharedassets51.assets`): 4 submeshes,
`m_IndexFormat: 0`, vertex count 561, channels = pos(dim3,off0)/normal(dim3,off12)/tangent(dim4,
off24)/uv0(dim2,off40), all `stream:0` (single interleaved vertex stream, not multi-stream) — a
normal, un-exotic authored mesh, small `m_StreamData` inline (not offloaded to `.resS`, since it's
below Unity's streaming threshold).

---

## 5. Textures

| Metric | Equator (232, own files) | AutoService (346, own files) |
|---|---|---|
| Format distribution | DXT5(12): 142, DXT1(10): 76, BC7(25): 13, RGB24(3): 1 | DXT5(12): 275, DXT1(10): 67, BC7(25): 2, Alpha8(1): 1, DXT1Crunched(28): 1 |
| Top dimensions | 1024×1024: 106, 512×512: 62, 256×256: 14, 512×1024: 11, 1024×512: 10, 2048×2048: 7 | (similar spread, 1024×1024-dominant; see `autoservice_census.json`→`textures.dimension_dist_top`) |

**Decode test (item 5 requirement — 5 textures across different formats):** AutoService's own
texture pool spans exactly 5 distinct formats and all 5 decoded successfully via UnityPy's
`.image`/Pillow path:

| Format | Sample name | Size | Result | PNG |
|---|---|---|---|---|
| DXT5 (12) | `road_marking` | 1024×1024 | OK | `tex_samples_autoservice/tex_fmt12_road_marking.png` (2.0 MB) |
| DXT1 (10) | `Pussan_Aurel_A3_LOD3_G` | 256×256 | OK | `tex_samples_autoservice/tex_fmt10_Pussan_Aurel_A3_LOD3_G.png` (64 KB) |
| BC7 (25) | `rack_shelf_normal` | 512×1024 | OK | `tex_samples_autoservice/tex_fmt25_rack_shelf_normal.png` (577 KB) |
| Alpha8 (1) | `cookie_DirtyLamp_02_contrast` | 256×256 | OK | `tex_samples_autoservice/tex_fmt1_cookie_DirtyLamp_02_contrast.png` (38 KB) |
| DXT1Crunched (28) | `Fireball_test` | 2048×2048 | OK | `tex_samples_autoservice/tex_fmt28_Fireball_test.png` (486 KB) |

Equator's own pool only spans 4 formats locally (DXT5/DXT1/BC7/RGB24); all 4 decoded successfully
too (`tex_samples_equator/*.png`). **Decode success rate: 9/9 (100%) across both maps' locally-owned
textures**, including the crunched-DXT1 case (`DXT1Crunched`, format 28) which needs an extra
unpack step — UnityPy/`texture2ddecoder` handled it transparently. This is strong evidence the
extractor's texture path can be a straight "read → `.image` → save/convert" pipeline with no
format-specific special-casing beyond what UnityPy already does.

Note: many of a scene's *materials* reference textures that live in the external shared-asset pools
from §2.2, not in the map's own `sharedassets<N>.assets` — the 232/346 counts above are a lower bound
on the map's true visual texture footprint.

---

## 6. Materials / shaders

| Metric | Equator (170 materials, 20 distinct shaders) | AutoService (228 materials, 26 distinct shaders) |
|---|---|---|
| Dominant shader | `p0/Reflective/Bumped Specular SMap` (91, 53.5%) | `p0/Reflective/Bumped Specular SMap` (163, 71.5%) |
| 2nd | `Decal/Ultra Deferred Decal Of God 3000` (19) | `p0/Reflective/Bumped Specular` (67) |
| 3rd | `p0/Cutout/Bumped Diffuse` (12) | `Decal/Ultra Deferred Decal Of God 3000` (50) |
| Unresolved shader refs | **0** | **0** |

Both maps' shader lists are a subset/superset of standard Tarkov `p0/*`, `Decal/*`, `Global Fog/*`
and a handful of `Custom/*` / `Legacy Shaders/*` / `Hidden/Amplify Impostors/*` shaders — no
Arena-exclusive shader family was found. This is consistent with the prior session's finding that
"all 17 shader names of Equator materials exist in SPT's StreamingAssets/Windows/shaders bundle"
(this census's 20/26 counts are the full 9-scene set, a superset of the single-scene 17).

Sample property set for the dominant shader `p0/Reflective/Bumped Specular SMap`:
TexEnvs = `_BumpMap, _Cube, _MainTex, _SpecMap, _TintMask`; representative Floats =
`_BumpScale, _BumpTiling, _Cutoff, _DetailNormalMapScale, _Glossiness, _GlossyReflections,
_HeatTemp, ...`; Colors = `_BaseTintColor, _Color, _EmissionColor, _ReflectColor, _SpecColor, ...`.
(Full lists in `*_census.json` → `materials.shader_props_sample`.)

Shader keywords in use are sparse and vegetation/decal-related: Equator `{_EMISSION:3, TINTMASK:1,
_HEMI_ON:1}`; AutoService `{_HEMI_ON:6, EFFECT_BUMP:5, EFFECT_HUE_VARIATION:5, GEOM_TYPE_LEAF:4,
TRIPLANAR:3, _EMISSION:3, _NORMALMAP:2, GEOM_TYPE_BRANCH:1, GEOM_TYPE_FROND:1, TINTMASK:1,
_ALPHA_PARALLAX_ON:1, USERAIN:1}` — the `GEOM_TYPE_*`/`EFFECT_*` keywords are SpeedTree foliage
shader variants, present because AutoService's own scene set includes `TreeWind`-tagged trees (235
`EFT.SpeedTree.TreeWind` MonoBehaviours, §8) — Equator has far less vegetation.

---

## 7. Layers & Tags

Read `TagManager` from Arena's `globalgamemanagers` (`arena_gm.json`) and from SPT's
`D:/SPT41/EscapeFromTarkov_Data/globalgamemanagers` (`spt_gm.json`).

- **Layers: identical, all 32 slots, same order, same names, in both builds** — index 0 `Default` …
  8 `Player`, 9 `DoorLowPolyCollider`, 15 `Loot`, 16 `HitCollider`, 21 `Interactive`, 22 `Deadbody`,
  … 31 `Grass`. **No adjustment needed when porting `GameObject.m_Layer` values** — a layer index
  captured from Arena geometry means the same thing in SPT.
- **Tags: 19/20 match, one mismatch.** Both have the same first 18 tags (`piercesHeavy` … `Vehicle`).
  Arena's tag 18 is `AdditionalCamera` (not present in SPT at all) and tag 19 is `SmokeGrenade`; SPT's
  tag 18 is `SmokeGrenade` and tag 19 is `PortalsCollection` (not present in Arena). **Inference:**
  since modern Unity serializes GameObject tags as strings (`m_TagString`), not raw indices, this
  mismatch is unlikely to break anything on import — but any tooling that (incorrectly) treats tags
  as index-based should be flagged, and `AdditionalCamera`/`PortalsCollection` tag usages should be
  checked for during import since they don't exist on the other side.

**GameObject `m_Layer` distribution** (own scene files):

| Layer idx | Name | Equator count | AutoService count |
|---:|---|---:|---:|
| 0 | Default | 19,962 | 24,222 |
| 2 | Ignore Raycast | 5 | 3 |
| 8 | Player | 15,472 | 5,800 |
| 9 | DoorLowPolyCollider | 9 | 32 |
| 11 | Terrain | 347 | 2 |
| 12 | HighPolyCollider | 2,914 | 3,053 |
| 13 | Triggers | 235 | 107 |
| 18 | LowPolyCollider | 1,876 | 2,072 |
| 21 | CullingMask | 155 | — |
| 22 | Interactive | 415 | 186 |
| 26 | Foliage | 11 | 31 |
| 28 | Sky | 16 | 41 |
| 29 | LevelBorder | 305 | 726 |
| 30 | TransparentCollider | 15 | 81 |

(Full raw dicts: `equator_census.json`/`autoservice_census.json` → `layers`; layer index → name table
is `arena_gm.json` → `TagManager.layers`, 0-indexed array.) Layer 8 ("Player") being the single
largest non-Default bucket on both maps (15,472/5,800 GameObjects) is notable — this is almost
certainly the per-object hit-collider/render layer convention carried over from main-EFT geometry
authoring, not literally "Player" objects; worth double-checking against SPT's own usage of layer 8
before assuming 1:1 semantics on import.

---

## 8. Components

### 8.1 Built-in components

| Component | Equator | AutoService |
|---|---:|---:|
| MeshCollider | 5,429 | 5,303 |
| LODGroup | 3,601 | 3,085 |
| BoxCollider | 1,421 | 1,940 |
| CapsuleCollider | 910 | 270 |
| Light | 479 | 203 |
| Rigidbody | 413 | 134 |
| SphereCollider | 171 | 69 |
| AudioSource | 33 | 56 |
| OcclusionPortal | 21 | 0 |
| ParticleSystem | 4 | 22 |
| OcclusionArea | 3 | 3 |
| WindZone | 1 | 2 |
| ReflectionProbe | 1 | 2 |
| Terrain / TerrainCollider / LightProbeGroup / NavMeshAgent / NavMeshObstacle | 0 | 0 |

**MeshCollider convexity:** Equator 850 convex / 4,579 non-convex (15.7% convex); AutoService 336
convex / 4,967 non-convex (6.3% convex) — most collision geometry on both maps is exact
(non-convex) triangle-mesh colliders, as expected for static level geometry; convex ones are almost
certainly small movable/rigidbody props (Rigidbody counts 413/134 are in the same ballpark).

**Light types** (`m_Type`: 0=Spot, 1=Directional, 2=Point): Equator `{Point:301, Spot:175,
Directional:3}`; AutoService `{Point:115, Spot:86, Directional:2}`. **Lightmapping mode**
(`m_Lightmapping`): Equator `{4:474, 1:5}`; AutoService `{4:201, 2:2}` — value `4` (Realtime) vastly
dominant on both.

**No baked lightmaps on either map:** `LightmapSettings.m_Lightmaps` count = **0** in all 9 scene
files of both sets, and every `MeshRenderer`/`SkinnedMeshRenderer` has `m_LightmapIndex == 65535`
(Unity's "no lightmap" sentinel) — 15,925/15,419 renderers for Equator, 16,708/16,556+152 for
AutoService (100% unlit-by-lightmap). Combined with the Realtime-dominant Light modes above, this
confirms **lighting is fully dynamic/realtime on both maps — there is no lightmap bake to salvage or
port**, which removes an entire category of extractor work (no UV2 lightmap-atlas repacking needed).

`OcclusionCullingData` is a real, non-trivial precomputed PVS dataset (2.76 MB on Equator's
`sharedassets51.assets`), and `RenderSettings.m_Fog` is enabled (`true`) in most sub-scenes of both
maps (ambient mode 3/"Flat" in most scenes, one Lighting-role scene per map uses mode 1/"Trilight"
with a real skybox material assigned).

### 8.2 MonoBehaviour class census (script resolution — see Method notes for the fix required)

**Resolution rate: 100%** after fixing a UnityPy PPtr/MonoScript-resolution issue (§ Method notes):
Equator 11,385 total MonoBehaviours = 9,980 resolved into 182 distinct classes + 1,405 with a null
`m_Script` (0 unresolved). AutoService 10,434 total = 7,643 resolved into 183 distinct classes + 2,791
null (0 unresolved).

Top classes by count (both maps combined view; full lists in `*_census.json` →
`components.monobehaviour_class_census`, capped at top 400 — both maps have well under 400 distinct
classes so the lists are complete):

| Class | Equator | AutoService | Note |
|---|---:|---:|---|
| `EFT.Ballistics.BallisticCollider` | 2,893 | 3,117 | per-bone/part ballistic hit volumes |
| `BodyPartCollider` | 1,188 | 360 | |
| `EFT.Airdrop.AirdropPoint` | 0 | **602** | AutoService-only — Arena airdrop mechanic |
| `Diz.Skinning.Skin` / `Skeleton` / `LoddedSkin` | 506/198/297 | 152/60/90 | character skinning rig (from shared char-asset pool) |
| `RootMotion.FinalIK.LimbIK` | 300 | 484 | IK rig components |
| `CullingLightObject` | 472 | 184 | custom light-culling system |
| `EFT.Visual.StaticDecal.StaticDeferredDecal` | 348 | 304 | |
| `EFT.SpeedTree.TreeWind` | 49 | **235** | AutoService has far more foliage |
| `Unity.AI.Navigation.NavMeshModifier` | 124 | 94 | per-object NavMesh area overrides |
| `EFT.Game.Spawning.SpawnPointMarker` | **61** | **36** | player spawn points |
| `PatrolPoint` | 23 | 59 | |
| `AICorePoint` | 14 | 7 | |
| `AIMinePoint` | 61 | 23 | (mine/decoration anchor, not AI mines) |
| `AdditionalNavmeshBlock` | 58 | 44 | manual NavMesh carve-outs |
| `EFT.Interactive.TreeInteractive` / `ObstacleCollider` | 11/11 | small | |
| `Audio.SpatialSystem.*` (portals/trigger areas) | 55/51 | small | spatial-audio room system |

No `Terrain`/`TerrainCollider` anywhere (both maps are fully hand-modeled, not terrain-based), no
`EFT.BossLocationSpawn`-style class found (Arena doesn't use EFT's boss-spawn system) — spawn logic
is the lighter Arena-specific `SpawnPointMarker`/`AICorePoint`/`BotZone` family (§9).

---

## 9. Spawn / zone anchors (world-space positions)

Resolved by walking each marker's GameObject → its Transform (component index 0) → parent chain,
composing local pos/rot/scale up to the scene root (quaternion-based composition; scale composition
ignores parent-rotation skew, an acceptable approximation for point markers). Counts + sample world
positions (first 8 of each class):

| Class | Equator count | AutoService count | Scene file(s) |
|---|---:|---:|---|
| `EFT.Game.Spawning.SpawnPointMarker` | 61 | 36 | level52/level57 (Eq); level63/level136 (AS) |
| `AICorePoint` | 14 | 7 | level52 / level63 |
| `AICorePointHolder` | 1 | 1 | level52 / level63 |
| `BotZone` | 1 | 1 | level52 / level63 |
| `BotZoneEntranceInfo` | 2 | 1 | level52 / level63 |
| `PatrolPoint` | 23 | 59 | level52 / level63 |
| `AIMinePoint` | 61 | 23 | level52 / level63 |
| `AdditionalNavmeshBlock` | 58 | 44 | level52+level54 / level63 |

All position/zone markers for **both** maps live in the *AI scene* (level52/level63) — confirming the
AI scene is the right single target for a "spawn + zone" extractor pass, not the main geometry scene.
Sample Equator `SpawnPointMarker` world positions (X,Y,Z, meters): `(27.38, 0.12, -16.49)`,
`(-26.90, 0.08, 5.55)`, `(-8.27, 0.04, -34.40)`, `(-18.11, 0.06, -25.83)`, `(5.54, 0.04, -33.47)`,
`(-0.81, -0.02, 38.94)`, `(-26.79, 0.08, 20.67)`, `(26.19, 0.12, 18.84)` — all within the map's
NavMesh source-bounds extent (±71.65 × ±86.35, see §10), i.e. transform composition is producing
sane, in-bounds coordinates. Full lists: `*_census.json` → `spawn_zone_anchors`.

Only a single `BotZone` (world pos reported as origin — likely a zone-bounds container object placed
exactly at the scene root, not a positioning bug) and only 1–2 `AICorePointHolder`/
`BotZoneEntranceInfo` per map — Arena's AI-zone structure is much flatter/simpler than main-EFT's
multi-BotZone raid maps, consistent with Arena being a small-scale arena shooter rather than an open
raid map.

---

## 10. NavMeshData

| | Equator (`sharedassets52.assets`) | AutoService (`sharedassets63.assets`) |
|---|---|---|
| Tile count | **41** | **30** |
| `agentTypeID` | **287145453** (custom) | **0** (project default "Humanoid") |
| agentRadius / agentHeight | 0.28 / 1.60 | 0.30 / 1.70 |
| agentSlope / agentClimb | 48° / 0.38 | 48° / 0.38 |
| cellSize (manual) | 0.07 | 0.07 |
| tileSize | 256 | 256 |
| Source bounds center | (0, 2, 0) | (-370.30, 2, 486.80) |
| Source bounds extent | (71.65, **10.05**, 86.35) | (50.90, **60.99**, 40.85) |
| Height meshes / off-mesh links | 0 / 0 | 0 / 0 |

**Equator's NavMesh was baked with a bespoke agent type** (`agentTypeID 287145453`, radius 0.28,
height 1.60 — narrower/shorter than the project's default Humanoid settings of 0.30/1.70).
Cross-checked against Arena's project-wide `NavMeshProjectSettings.m_LastAgentTypeID` in
`arena_gm.json`, which is **also 287145453** — i.e. this custom agent is the single last-created
custom agent type in the whole Arena project, used specifically for this map's TeamFight-mode bake.
AutoService's bake instead uses `agentTypeID 0`, the project's stock default Humanoid settings
(identical values to SPT's own default agent 0 in `spt_gm.json`: radius 0.30, height 1.70, slope 48°,
climb 0.38 — **exact match**, so no per-agent conversion is needed if AutoService is chosen).

**Y-extent is the standout difference: Equator 10.05 vs AutoService 60.99** — AutoService's NavMesh
covers roughly 6× the vertical range, confirming it is a genuinely multi-storey/elevated structure
(matches the "AutoService" theme — a multi-floor garage/workshop building) while Equator is
essentially a single flat courtyard-ish arena. A 41-tile flat NavMesh is a much simpler first target
for validating the SPT port pipeline (baking/porting a NavMesh with elevation changes, ramps, and
inter-floor NavMeshLinks is meaningfully harder and out of scope for a "geometry+colliders+NavMesh
first" spike).

Both maps' `NavMeshProjectSettings.areas` (Walkable/Not Walkable/Jump/Sitdown/Danger + 27 unnamed
cost-1.0 slots) are identical between Arena and SPT (`arena_gm.json` vs `spt_gm.json`), so no area-cost
remapping is needed either way.

---

## 11. Recommendation: Arena_Equator, TeamFight (TDM) mode

**Choose Arena_Equator / TeamFight / `arena_equator_tdm_02_preset.bundle`.**

Reasons, in order of weight:

1. **Smaller on every measured axis, without being trivially small.** ~295 MB vs ~554 MB own-file
   size; 880k vs 2.85M vertices/triangles (Equator is ~31% of AutoService's geometry volume); 232 vs
   346 own textures; 170 vs 228 own materials; 15,419 vs 16,556 MeshRenderers. A spike whose goal is
   validating the *pipeline* (geometry → colliders → NavMesh) benefits from the smaller iteration
   loop (faster re-extraction, re-import, re-bake cycles) without losing any technique coverage.
2. **Flat, single-level NavMesh (10.05 m vertical extent, 41 tiles) vs AutoService's 6× taller,
   clearly multi-storey NavMesh (60.99 m, 30 tiles).** The task explicitly scopes the spike to
   "geometry + colliders + NavMesh first" — Equator lets that be validated without also having to
   solve inter-floor NavMeshLinks/elevation on the first attempt. AutoService is the natural
   candidate for a *second* spike once the pipeline handles elevation.
3. **No static batching and no baked lightmaps on either map** (§3, §8) — this is the most important
   simplification either way (no combined-mesh recovery needed, no lightmap-UV repacking needed), and
   it's identical for both candidates, so it isn't a differentiator, but it does mean *neither* map has
   the complexity the task's brief worried about; the choice can be made purely on size/scope grounds.
4. **Both maps' shader/material sets are already known-compatible with SPT** (0 unresolved shaders on
   either, and Equator's shader list is confirmed by the prior session to fully exist in SPT's
   `StreamingAssets/Windows/shaders` bundle) — again identical outcome, not a differentiator, but
   de-risks either choice.
5. **Both maps' spawn/AI-zone data is present, complete, and resolves cleanly** (0 unresolved
   MonoBehaviour scripts on either map after the PPtr-resolution fix) — Equator has a somewhat
   *richer* immediate spawn-point set (61 `SpawnPointMarker` vs AutoService's 36) despite its smaller
   overall footprint, which is a plus for exercising the "later bots/spawns" follow-on work mentioned
   in the roadmap.
6. **Custom NavMesh agent type on Equator (287145453, radius 0.28/height 1.60) vs AutoService's stock
   default (0, matches SPT's own default exactly).** This is arguably a point *against* Equator (one
   more thing to translate — a custom agent radius/height instead of a 1:1 default-agent match), but
   it's a single scalar-parameter difference (not a structural one) and is worth exercising once, since
   ArenaMode's later custom-agent Arena bots will likely need exactly this kind of translation anyway.

AutoService's only structural advantage is its stock-default NavMesh agent (point 6 above) and its
richer AirdropPoint/foliage set if those are ever wanted — neither is compelling enough to outweigh
the 1.9–3.4× size/complexity gap for a *first* spike.

---

## Method notes (for whoever runs the next extraction pass)

1. **UnityPy correctly parses this Unity 6000.3.6f1 build** — `env.file.version` reports `6000.3f1`,
   and reading built-in classes (`Transform`, `MeshRenderer`, `Mesh`, `Texture2D`, `Material`,
   `Light`, `GameObject`, `NavMeshData`, `TagManager`, `BuildSettings`, `NavMeshProjectSettings`, …)
   via `.read_typetree()` works out of the box with no fallback-version hacks needed.
2. **Editor-built asset bundles (`StreamingAssets/Windows/maps/*_preset.bundle`) retain typetrees**
   (so `MonoBehaviour.read_typetree()` returns full custom fields, e.g. `ScenesPreset`'s
   `GameModes`/`ScenesGuids`), **but shipped Player data (`level<N>`/`sharedassets<N>.assets`) does
   not** — a raw `obj.read_typetree()` on one of these MonoBehaviours throws
   `ValueError: Expected to read N bytes, but only read 32 bytes` because UnityPy has no field layout
   for Arena-custom classes and the generic reader only knows the common `Behaviour` header size.
   **Fix used:** `ObjectReader.parse_monobehaviour_head()` (an undocumented-but-public UnityPy method
   that parses *only* the common header — `m_GameObject`, `m_Enabled`, `m_Script`, `m_Name` — using
   `check_read=False`, tolerating unknown trailing custom-field bytes) followed by
   `mb.m_Script.read_typetree()` to dereference the `PPtr<MonoScript>` (which UnityPy resolves
   correctly across files within one `Environment`, regardless of `path_id` collisions between
   different SerializedFiles — the naive approach of building a single global
   `{path_id: class_name}` dict from `globalgamemanagers.assets` and matching by `path_id` alone is
   **wrong** and produces near-total resolution failure, since `path_id` is only unique *within* a
   file). This is implemented in `census.py`'s `resolve_script_name()`.
3. **UnityPy Environment/PPtr bug encountered and worked around:** dereferencing a PPtr whose target
   file is not registered under the exact key the `Environment` originally used to load it (our
   scenes were loaded by full `X:/...` path, but `PPtr.deref()`'s fallback `env.find_file()` path can
   register a *second*, bare-basename-keyed copy of an already-loaded `.assets` file — observed
   concretely for `sharedassets62.assets` as a side effect of decoding a `Texture2D`'s
   `.image`/streamed data) silently **double-registers** that file in `env.files` under a second key.
   Iterating `env.files.items()` while such a call happens can either raise
   `RuntimeError: dictionary changed size during iteration` or, if the resize happens to land between
   iterator steps, silently **double-count every object in that file** (observed: AutoService's own
   228 materials were counted as 445 = 228 + 217-duplicate before the fix). **Fix:** match a scene's
   own files by their **exact original full path** (not by basename) when filtering
   `env.files.items()`, and additionally snapshot both the outer `env.files.items()` and inner
   `f.objects.values()` iterables with `list(...)` before any `obj.read()`/`PPtr.deref()` call inside
   the loop. All counts in this report were re-verified to be internally consistent after this fix
   (`object_counts_total` now agrees exactly with `mesh_features.total_meshes`,
   `textures.total_textures`, `materials.total_materials`, and the MonoBehaviour
   resolved+null(+unresolved) sum, for both maps — see the cross-check output in the transcript).
   Anyone reusing `census.py` for the extractor should keep both of these guards.
4. **No local copies were needed for performance.** `X:` (the Arena client network share) turned out
   to sustain >300 MB/s in practice (a 207 MB file copied in ~0.08 s during an early timing probe),
   and `UnityPy.load()` only parses each SerializedFile's object *header table* eagerly — reading the
   full 1.04 GB `resources.assets` + `9.6 MB globalgamemanagers.assets` took 0.38 s total, and the
   full 20-file combined environment for either 9-scene map (own scenes + globalgamemanagers.assets +
   resources.assets) loads in ~1.4–1.6 s. Actual vertex/texture *payload* bytes are only read lazily
   when a specific Mesh/Texture is decoded, which is why the entire census (10 analysis passes ×
   2 maps) ran in a few seconds of wall-clock UnityPy time per map. **One file was copied** as an
   early speed-test artifact and left in place: `D:/ArenaMapPort/arena-src/sharedassets51.assets`
   (25.3 MB, copied from `X:/Escape from Tarkov Arena/EscapeFromTarkovArena_Data/sharedassets51.assets`,
   unmodified, read-only source) — it was not actually needed by the final scripts (all of which read
   `X:` directly) and can be deleted.
5. **Texture format codes** (Unity `TextureFormat` enum, for reference): 1=Alpha8, 3=RGB24, 10=DXT1,
   12=DXT5, 25=BC7, 28=DXT1Crunched.
6. **Light type codes:** 0=Spot, 1=Directional, 2=Point. **Lightmapping mode:** the dominant value 4
   corresponds to Realtime-only in Unity's `LightmapBakeType`/mixed-lighting-mode bit flags.

## File index

- Report: `D:/ArenaMapPort/analysis/arena-census.md` (this file)
- Raw census JSON: `D:/ArenaMapPort/analysis/equator_census.json`,
  `D:/ArenaMapPort/analysis/autoservice_census.json`
- Preset bundle dumps: `D:/ArenaMapPort/analysis/equator_tdm_preset.json`,
  `D:/ArenaMapPort/analysis/autoservice_preset.json`
- TagManager/NavMeshProjectSettings/BuildSettings dumps: `D:/ArenaMapPort/analysis/arena_gm.json`,
  `D:/ArenaMapPort/analysis/spt_gm.json`
- External-file-reference dump: `D:/ArenaMapPort/analysis/scene_externals.json`
- Decoded texture samples: `D:/ArenaMapPort/analysis/tex_samples_equator/*.png`,
  `D:/ArenaMapPort/analysis/tex_samples_autoservice/*.png`
- Scripts: `D:/ArenaMapPort/analysis/scripts/common.py`, `census.py`, `run_equator.py`,
  `run_autoservice.py`, `01_read_presets.py`, `02_probe_gm.py`, `03_tagmanager_navsettings.py`,
  `04_externals.py`
- Copied source (unused by final scripts, safe to delete): `D:/ArenaMapPort/arena-src/sharedassets51.assets`
