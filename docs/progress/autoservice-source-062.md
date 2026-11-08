# Autoservice source delta: the 0.5.4 spike package versus the Arena 0.6.2 native package

Read-only comparison of `work/extract/autoservice` (extracted 2026-09-15 from Arena 0.5.4, plan digest `3e9de53e...`, no native
features) with `work/extract/autoservice-native-v1` (extracted 2026-11-08 13:41 from Arena 0.6.2.0.48473 with `--native-features`,
plan digest `a893914d...` after the scene roles were added). Same role as [iceberg-source-061.md](iceberg-source-061.md) for Iceberg
and [bowl-source-060.md](bowl-source-060.md) for Bowl. Survey S1 of [autoservice-release.md](autoservice-release.md) produced the
expectations; where the extraction differs, this file states it. Plan: [PLAN-AUTOSERVICE.md](../PLAN-AUTOSERVICE.md) items 1 to 3.

## Scene content

No level index moved between Arena 0.5.4 and 0.6.2 (62, 63, 65, 66, 69, 112, 135, 136 and 243 are the same). Every object, mesh and
texture id is rehashed (the ids derive from the source file name and path id), so the spike package and v1 share no ids, and the
spike's receipt is void against 0.6.2. Names with Cyrillic characters are real UTF-8 in v1 (extracted with `PYTHONUTF8=1`; the
spike has mojibake of them, so a few hierarchy paths differ in spelling only).

| Scene | index | spike objects | 0.6.2 objects | note |
|---|---|---|---|---|
| Arena_AutoService | 62 | 25,678 | 25,692 | +14: two `metal_sheet_hanging_01_rusted` props with their children (survey S1) |
| Arena_AutoService_DesignStuff | 65 | 124 | 497 | +373: the 372 layer-29 `LevelBorder` cubes of `Arena_AutoService_LevelBorder` (disabled renderers, enabled solid BoxColliders) and one more object |
| Arena_AutoService_TeamFight_DesignStuff | 136 | 4,710 | 4,710 | identical in count |
| Arena_AutoService_Light | 66 | 1,198 | 1,198 | identical in count |

Measured here: the spike's four scenes hold 31,710 objects in all and v1 holds 32,097 (+387 = 14 + 373); every object name of the
spike still exists in v1 (0 names disappear) and 372 names are new (the cubes, inferred).

Package totals: meshes 4,270 -> 4,270 (the same count, rehashed ids); materials 401 -> 479 and textures 872 -> 950 (the
survey's scratch re-extract of the unchanged plan without native features has 402 materials and 872 textures; the native
decals add 77 materials of the shader `Decal/DeferredDecalDiffuseNormalsDynamicEditor` and 78 textures); errors 0 -> 0; warnings
116 -> 116 (97 `MeshFilter ... no mesh assigned, component skipped` and 19 cubemap notices; the survey's "116 no mesh assigned" counted both). The survey's "about 32,097 objects, 4,270 meshes, 402 materials, 872 textures" is the
extraction's 32,097 objects and 4,270 meshes exactly; the material and texture counts differ only by the native decal assets.

## Native blocks of v1 (absent from the spike)

All numbers equal the survey expectations of the plan (nothing differs):

| Feature | Total | By scene (level) |
|---|---|---|
| BallisticCollider | 3,117 | 62: 2,897, 65: 18, 136: 8, 66: 194 |
| StaticDeferredDecal | 304 | 62: 299, 65: 3, 136: 2, 66: 0 |
| NavMeshModifier | 30 | 62: 10, 136: 20 |
| NavMeshModifierVolume | 7 | 62: 6, 66: 1 |
| CullingLightObject | 184 | 66: 172, 136: 12 |
| VolumetricLight | 78 | 66: 68, 136: 10 |
| LightFlicker | 0 | |
| SmallPhysicsObject | 134 | all in level 62: SoundType 0 x14 (Books), 4 x34 (GlassBottle), 6 x6 (MediumGeneric), 8 x2 (Monitor), 9 x19 (SmallBox), 10 x19 (SmallPaper), 11 x40 (SmallPlastic) |
| Rain cover | 1 IndoorTrigger, 0 DryPlane, no WeatherObstacle | Scripts scene level69, 0 baked-vs-live Bounds mismatches |

NavMesh areas Walkable / Not Walkable / Jump / Sitdown / Danger. Ambient audio is not in v1 (`required = ["ballistics"]`,
contract `audio: null`; package v2 adds it, plan item 4).

One authoring needed a reviewed rule: six Rubber colliders carry a preset that names another material. They are the `rabble`
children of the `balistic_col` of the `logan_wheel` props of Arena_AutoService
(`SBG_Arena_AutoService/OO/Props/Autoservice_Little_Office/logan_wheel (1)`, `.../logan_wheel (3)`, `(4)`,
`.../Autoservice_Main_Office/logan_wheel (1)`, `(2)`, `(3)`; level62, layer 12, one shared mesh `114d5f6f2581e54c`, a MeshCollider
that is not convex) and carry the preset **Pebbles** over the material **Rubber**; the names say the author meant rubble on a
wheel prop. Decision: import as authored, like Bowl's Chainfence/MetalThin and Iceberg's Gravel/Concrete pairs
(`REVIEWED_PRESET_MATERIAL_PAIRS` in `extract_ballistics.py` gains `("Pebbles", "Rubber")`, pinned in `test_ballistics.py`; the
importer has no pair list and needs no change). Hit role and network id of these colliders are the plain 0/0, surface sound 0, so
`reviewed_hit_role` needed no new case; no Glass collider with a lamp role occurs. Every other BallisticCollider satisfies the unchanged
rules.

## Shader census of v1

28 distinct shader names over the 479 materials of the package (`materials/*.json`, field `shader`). The survey's scratch extract without native features has 27 names over 402 materials; the one added name is the decal
shader. The survey's earlier figures of 33 names over 409 materials and the still earlier 34 are not reproducible; 28 is the
number and `AutoserviceContractTests` pins it (with the 77 decal materials and the single `p0/Reflective/Bumped Specular SMap
Baked` material). Counts: `p0/Reflective/Bumped Specular SMap` 155, `Decal/DeferredDecalDiffuseNormalsDynamicEditor` 77,
`p0/Reflective/Bumped Specular` 67, `Decal/Ultra Deferred Decal Of God 3000` 50, `p0/Cutout/Bumped Diffuse` 15, `Standard` 12,
`Nature/SpeedTreeEFT` 9, `Global Fog/Transparent Reflective Specular` 22, `p0/Reflective/Bumped Specular SMap_Decal` 31, the Cloth
pair (1 + 2), `Custom/Billboard_FogSheet_Simple` 2 and 16 more names with at most 6 materials each. That the shaders exist in EFT
0.16.9.5 (the survey's finding) is not re-proven here; this item re-runs only the census.

## Commands

- Housekeeping: `work/build/autoservice` moved to `work/build/autoservice-spike-20260915` (target did not exist); plan roles scripts
  69, sound 112, ai 63, culling 135; `tools/build_remaining.ps1` drops `autoservice` from `$Maps` and `$allMaps` and throws for it.
- First extraction attempt (13:2x, 11.7 s): `FAILED: Ballistics: preset/material disagreement` (the six Pebbles/Rubber colliders); the
  rule above was added, the partial output removed, then
  `python tools/extract/extract_map.py --plan tools/extract/plans/autoservice.json --native-features --target D:/SPT41/EscapeFromTarkov_Data --output work/extract/autoservice-native-v1`
  (3 min 50 s): `DONE arena_autoservice: 32097 objects, 4270 meshes, 479 materials, 950 textures, 116 warnings`, exit 0.
- `python tools/extract/refresh_native_volumes.py work/extract/autoservice-native-v1`: `NATIVE_VOLUMES_REFRESHED count=0 ... (no Tex3D
  textures, manifest untouched)`.
- Native contract `tools/extract/native_contracts/autoservice.json` (pins above, `audio: null`); `AutoserviceContractTests` in
  `test_native_contracts.py` pins the contract, the plan roles, the package manifest and the shader census.
- `validate_extract.py --package work/extract/autoservice-native-v1 --extractor-timeout 1800`: `OK -- all checks passed` with 5
  non-blocking warnings (the manifest warnings, the `barrel_fire_wfire (3)` LODGroup slots whose renderer is a particle system,
  21 meshes no scene references); both reproducibility runs exit 0, byte-identical, 0 diffs, and the package-freshness check
  0 diffs; 10 min 56 s (13:54-14:05). A first validator run (13:43-13:53) ended at `unsupported or missing native map identity`:
  it started while the contract file was new and the loader of another agent's audio work was mid-edit; the same
  `validate_package` call passed seconds later and the repeat run above is the record.

### Review fix 2026-11-08

- F1 (low): the shader census sentence said "19 more names"; recounted from `materials/*.json` it is 16 (28 names over 479 materials, 12 named, 16 left, each with at most 6 materials). Corrected to "16 more names"; no pin or package change.
