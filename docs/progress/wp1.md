# WP1 — Extractor — progress log

Status: **S1 slice extracted and validated.** Full 5-scene dry census run (structural code-path
proof). Reusable extractor implemented per §B.WP1 / §C.

## What was built

`tools/extract/`:
- `common_ids.py` — content-hash id derivation (§C intro), Unity enum → schema-string maps
  (cast shadows, light-probe usage, light type, shadow type, physic-material combine, texture
  color space/format names), tag-index resolution, `HardExtractError`.
- `mesh_bin.py` — `write_mesh_bin()`: Mesh → `meshes/<id>.mesh.bin` per §C.2's binary layout, built
  on `UnityPy.helpers.MeshHelper.MeshHandler` (the same internal machinery UnityPy's own OBJ
  exporter uses) rather than re-implementing vertex-stream unpacking, but bypassing the OBJ text
  format entirely to keep Unity's raw un-flipped coordinates and every channel §C.2 wants. Hard-fails
  (`HardExtractError`) on `m_MeshCompression != 0` per §B.WP1 step 2.
- `extract_equator.py` — the CLI + full algorithm: environment loading (exact-full-path scene-file
  matching + `scene_externals.json`-driven external loading, reusing `analysis/scripts/common.py`'s
  `EQUATOR` table), GameObject/Transform graph build + slice-root subtree selection, all nine §C.6
  component builders, the mesh/material/texture dedup-and-write pass, `anchors_reference` (reuses
  `analysis/scripts/census.py`'s `spawn_zone_anchors()` verbatim, per §B.WP1's explicit instruction),
  and `manifest.json` assembly.

CLI:
```
python extract_equator.py --source <ArenaData dir> --scenes <51,53,55,57,58|all5>
    [--slice-roots "SCENE_IDX:id1,id2,..."]...  --output <package dir>
    [--fixed-timestamp <ISO8601>]   # for byte-identical determinism checks
python extract_equator.py --source <dir> --scenes all5 --census-only   # dry counts only
```
`--slice-roots` accepts either a Transform's or a GameObject's own path_id for convenience; a scene
index with no `--slice-roots` entry is extracted in full — the same code path serves S1 and the
future full S2 run (§B.WP1: "make sure the full 5-scene run is supported by the same code").

## S1 slice — object selection and why

Package: `D:/ArenaMapPort/work/extract/equator_s1/`.

Arena's `level51` hierarchy turned out to be **shallow and flat**, not "one root Transform per
prop": only **6 true root Transforms** exist in the whole 22,034-object file, each a huge regional
container (thousands of descendants). A literal single "~50-object contiguous cluster" therefore
does not exist in the source data at the requested scale — I picked the closest honest
interpretation: **one primary cluster (an interior subtree, not a top-level root) plus three small,
self-contained supplementary picks**, chosen specifically to cover every feature type the task asked
for, exactly as instructed ("not cherry-picked to avoid every hard case, deliberately include...").
This is flagged here for the lead's awareness rather than silently reinterpreting "contiguous".

`Arena_Equator` (level51), 63 objects from 4 roots:
| Root (Transform id) | Object | Size | Covers |
|---|---|---:|---|
| 24802 | `refrigerator_2door_glass (1)` | 40 | LODGroup (3 LODs), non-convex MeshCollider ×10, BoxCollider ×4, normal map (`refrigerator_2door_NRM`), 3 shaders (incl. `Global Fog/Transparent Reflective Specular` glass) |
| 40710 | `bottle_baltika_Physic (77)` | 5 | convex MeshCollider |
| 32841 | `plywood_wall_01_(ARENA)` | 11 | multi-submesh mesh (`plywood_wall_01_LOD2`, 2 submeshes) |
| 22122 | `metall_fence_2` | 7 | `p0/Cutout/Bumped Diffuse` shader + alpha-cutout texture (`chain_fence`/`chain_fence_nrm`), its own small LODGroup |

`Arena_Equator_Lighting` (level55), 1 object: a single `Point Light` leaf (id 1261) — level51 itself
has **zero** `Light` objects (confirmed via per-file object counts), exactly the case §B.WP1
anticipated ("take it from level55 if level51 has none nearby"). All 208 of level55's lights sit as
flat siblings under one `POINT_LIGHTS` container, so pulling a single leaf light was the minimal
correct pick rather than dragging in that whole container.

Result: **64 objects total**, somewhat over the "~50" figure once every required feature type is
deliberately covered — noted honestly rather than trimmed at the cost of missing a required case.

## Verification performed (this pass)

- **Full pipeline run, 0 errors, 0 warnings**: `errors: []`, `warnings: []` in `manifest.json`.
- **Feature-coverage check** (scripted, not eyeballed): multi-submesh meshes present (9, up to 3
  submeshes), convex MeshCollider ×2, non-convex MeshCollider ×14, BoxCollider ×4, LODGroup ×3 (all
  with non-empty, correctly resolved `renderer_object_ids`), Light ×1, 6 distinct shaders
  (`p0/Reflective/Bumped Specular SMap`, `p0/Reflective/Bumped Specular`, `p0/Cutout/Bumped Diffuse`,
  `Global Fog/Transparent Reflective Specular`, `Decal/Ultra Deferred Decal Of God 3000`, `shadow`),
  9 normal-map textures correctly flagged, alpha-cutout texture (`chain_fence`, DXT5) via the Cutout
  shader.
- **Referential integrity**: every `mesh_id`/`material_id` referenced from a scene's components
  resolves under `meshes/`/`materials/`; every `texture_id` referenced from a material resolves
  under `textures/`. Zero dangling refs (scripted check).
- **Determinism (§B.WP1's required "run twice" check)**: ran the exact same command twice with
  `--fixed-timestamp` fixed and diffed the two output trees — **`diff -rq` reports zero differences**,
  every `.mesh.bin`/`.json` file byte-identical. Without `--fixed-timestamp`, only
  `manifest.json`'s `source.extracted_utc` provenance field differs between runs (by design — a
  wall-clock timestamp, not content); everything else is still byte-identical. **Spec note**: the
  §C.1 `manifest.json` example includes `extracted_utc`, which is inherently non-reproducible; added
  `--fixed-timestamp` so WP6's `validate_extract.py` (or a human) can get a true byte-for-byte proof —
  otherwise it should diff everything *except* that one field.
- **Mesh binary deep round-trip**: wrote a standalone reader for the §C.2 format and (a) structurally
  validated all 47 `.mesh.bin` files against `manifest.json` (vertex/submesh counts, index bounds),
  and (b) for one multi-submesh mesh, independently re-decoded the *same* source object straight from
  a fresh `UnityPy`/`MeshHandler` load (no shared code with the extractor's own write path) and
  confirmed positions, submesh `index_start`/`index_count`, and raw index-buffer values match exactly.
- **Texture sanity**: all 25 PNGs decoded to their declared width/height, none are the magenta
  placeholder (no decode failures hit).
- **Full 5-scene code-path proof**: `--census-only --scenes all5` successfully loaded all 5 content
  scenes + every external `scene_externals.json` lists (33 files) and printed per-file object-type
  counts matching `arena-census.md`'s own per-file breakdown exactly (e.g. level51: 22,034
  GameObjects; level55: 453 Lights/281 LODGroups). Additionally ran one **real, non-census**
  extraction (not just counting) on a small slice of `level58` (`Arena_Equator_TeamFight`, 9 objects,
  root `SBG_Arena_Equator_TeamFight`) to prove the full component/mesh/material/texture pipeline
  — not just the loader — also works cleanly on a scene outside 51/55: 0 errors, 0 warnings, 5
  meshes/7 materials/15 textures written correctly. (This ad hoc extra scratch run was written to a
  temp dir and deleted afterward — not part of the S1 deliverable package.)

## Ground-truth field-shape findings (this pass, by direct UnityPy probing — not assumed)

These correct/extend prior analysis-doc claims where they turned out to not quite match the actual
bytes:

- **`GameObject.m_Tag` is an int index, not a string** (`m_TagString`) for this Unity 6000.3.6f1
  build — contradicts an inference in `arena-census.md` §7. Empirically confirmed (scanned all of
  level51's `m_Tag` values): `0` = `"Untagged"`; custom project tags
  (`arena_gm.json` → `TagManager.tags[]`) start at a fixed base of **20000**, i.e.
  `m_Tag == 20000 + array_index` (e.g. `m_Tag=20000` → `tags[0]` = `"piercesHeavy"`). Implemented in
  `common_ids.resolve_tag()`.
- **Vertex-color channel normalization**: `UnityPy.helpers.MeshHelper.MeshHandler`'s modern
  (`m_VertexData.m_Channels`-based) decode path returns **raw, unnormalized** integer components for
  a UNorm8/SNorm8/UNorm16/SNorm16-format channel (only the legacy `Color32` array path divides by
  255 internally) — `mesh_bin.py` normalizes every channel itself from its own declared
  `VertexFormat` code rather than assuming "colors are always ÷255". None of the S1 slice's own
  meshes happened to carry a vertex-color channel (all observed `pos/normal/tangent/uv0`), so this
  path is implemented from Unity's documented `VertexFormat` semantics, not cross-checked against a
  second direct sample this pass — worth a spot-check once S2's full run hits one of the ~80
  vertex-painted meshes `arena-census.md` §4 counts.
- **`Texture2D.m_ColorSpace`**: confirmed empirically (`0` on a known normal map, `1` on a known
  diffuse/albedo texture) → `0 = Linear`, `1 = sRGB`.
- Field shapes for `MeshRenderer`/`MeshCollider`/`Box|Sphere|CapsuleCollider`/`Light`/`LODGroup`/
  `PhysicMaterial`/`Material` (incl. `m_SavedProperties.m_TexEnvs`/`m_Floats`/`m_Colors`) were all
  probed directly against real Equator data before being coded against (see
  `common_ids.py`/`extract_equator.py` module docstrings and inline comments for the exact values
  observed) rather than assumed from the schema prose alone.

## Spec inconsistency flagged for the lead

`docs/SPEC.md` §B.WP2.7 (NavMeshModifier handling) says WP1 records `NavMeshModifier`/
`NavMeshModifierVolume` MonoBehaviours' presence "per §C.6's generic-MonoBehaviour-passthrough rule"
— but §C.6 itself explicitly states the opposite: *"WP1 does not need a generic passthrough
mechanism for the spike... any MonoBehaviour not covering a required-component role... is not
recorded at all."* I followed §C.6 (the actual binding package-format contract) as ground truth: **no
MonoBehaviours, including NavMeshModifier, are recorded by WP1.** This is consistent with §B.WP2.7
already treating correct NavMeshModifier area-cost translation as "a stretch goal, not a blocker", so
it shouldn't block WP2, but the two sections should be reconciled (drop the passthrough-rule
reference in §B.WP2.7, or explicitly add a minimal MonoBehaviour-presence record to §C.6 if WP2
actually wants it later).

## Known limitations / not yet exercised

- Vertex-color channel normalization (see above) has no direct-sample cross-check yet.
- `SkinnedMeshRenderer` and `ReflectionProbe` builders are implemented per §C.6 but not exercised by
  the S1 slice (none present in the chosen clusters) — logic follows the same probed field-shape
  approach as everything else but should get a real hit during the S2 full run (Equator has 506
  `SkinnedMeshRenderer`s across all 9 source scenes, mostly in scenes outside WP1's 5-scene scope
  per `arena-census.md` §2.1, and exactly 1 `ReflectionProbe`).
- Non-triangle submesh `topology` (`!= 0`) and out-of-range `NavMeshModifier` area overrides are
  handled defensively (warned, not hard-failed) but never observed in the census (0 occurrences
  expected) — untested by construction.
- `D:/ArenaMapPort/arena-src/sharedassets51.assets` (a 25 MB leftover copy from an earlier analysis
  pass, already flagged as safe-to-delete-but-unneeded in `arena-census.md` Method note 4) was left
  alone — outside WP1's owned paths, not touched.

## Files

- `tools/extract/common_ids.py`, `tools/extract/mesh_bin.py`, `tools/extract/extract_equator.py`
  (new, tracked).
- `work/extract/equator_s1/**` (gitignored deliverable): `manifest.json`, 47× `meshes/*.mesh.bin`,
  12× `materials/*.json`, 25× `textures/*.png`, `scenes/Arena_Equator.json`,
  `scenes/Arena_Equator_Lighting.json`, `navmesh_ref/equator_source_navmesh.json`. ~45 MB total
  (mostly lossless PNG texture payload).
