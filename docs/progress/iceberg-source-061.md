# Iceberg source delta: the 0.5.4 spike package versus the Arena 0.6.1 native package

Read-only comparison of `work/extract/iceberg` (extracted 2026-09-15 from Arena 0.5.4, plan digest `a027640a...`, no native
features) with `work/extract/iceberg-native-v1` (extracted 2026-10-30 14:24-14:30 from Arena 0.6.1.0.48206 with
`--native-features`, plan digest `01797a77...` after the scene roles were added). Same role as
[bowl-source-060.md](bowl-source-060.md) for Bowl. Survey S1 of [iceberg-release.md](iceberg-release.md) produced the numbers.

## Scene content

Arena 0.6.0 moved the CheckPoint scenes from indices 366-369 to 375-378 (+9); 255-260 are unchanged.

| Scene | index (spike -> live) | spike objects | 0.6.1 objects | note |
|---|---|---|---|---|
| Arena_Sanatorium | 256 | 42,251 | 39,815 | -2,436: OO/SOO trees re-split per room, `PREFAB_VARIANTS` 1,832 -> 1,306, balcony fence 03 (94 sets) replaced by fence 06 (134 sets), `NO_CULLING` moved to CheckPoint |
| Arena_Sanatorium_Grass | 260 | 4 | 4 | GPUInstancer manager objects (grass is not ported) |
| Arena_Sanatorium_Light | 258 | 4,661 | 4,496 | -165; Lights 1,089 -> 1,035; new root `REWORK` (37 objects) at (40.6, 5.5, -79.4) |
| Arena_Sanatorium_DesignStuff | 257 | 77 | 78 | +1 |
| Arena_Sanatorium_CheckPoint | 366 -> 375 | 1,910 | 4,167 | +2,257: the old barricade groups are gone, `BARRICADE_03..37` and an `OO` container (884) are new |
| Arena_Sanatorium_CheckPoint_DesignStuff | 367 -> 376 | 1,257 | 1,301 | border cubes 346 -> 366 layer-29 objects, `SPAWNS` 89 -> 92, `MINES_SIGN` 84 -> 98, three new minefield roots |

The live census counts 1,307 GameObjects in CheckPoint_DesignStuff; the extractor skips 6 Canvas RectTransform objects in both
versions.

Player spawn markers: 64 -> 70 (45 identical, 11 moved by less than 3 m, 8 removed, 14 new, 5 of them in the far south-east at
x 50..65, z -125..-111). The 20 initial pocket markers and the 20 Usec stage markers are unchanged since 0.5.4; the Bear stage
markers moved. The AI scene's NavMeshData bounds are unchanged (center (18.7, 4, -73), extent (90, 40, 90), 64 tiles).

Package totals: meshes 5,467 -> 5,370 (mesh ids partly differ: path-id churn from re-serialization **(inferred)**); materials
772 -> 823 (the native decal materials, as on Bowl); textures 1,347 -> 1,407; errors 0 -> 0; warnings 67 -> 59 (the "no mesh
assigned" family and 24 cubemap notices). The same 11 compressed meshes (`BRDM_2_crash_*`, sharedassets256) decode through
`mesh_compat`. No Terrain or TerrainCollider. Source-null MeshColliders: 7 -> 5 (the two `arena_sandbag_fortification_07` left
with the old barricades). Two BC6H textures (`Sanatorium_Probe_1`, `Sanatorium_Probe_7_Reflect`) and 43 BC7.

## Native blocks of v1 (absent from the spike)

BallisticCollider 7,287 (256: 6,077, 260: 0, 258: 801, 257: 11, 375: 337, 376: 61), StaticDeferredDecal 215, NavMeshModifier
228 + NavMeshModifierVolume 0, CullingLightObject 1,126, VolumetricLight 276, LightFlicker 7, SmallPhysicsObject 0, rain cover
23 IndoorTrigger / 0 DryPlane / no WeatherObstacle (Scripts scene level255), NavMesh areas Walkable / Not Walkable / Jump /
Sitdown / Danger. Ambient audio is not in v1 (`required = ["ballistics"]`, contract `audio: null`).

Two authorings needed a reviewed rule (`817eed0`): the eight `bag_cement_block*/concrete` colliders of Arena_Sanatorium carry
the preset Gravel over the material Concrete, and eleven Glass colliders (the head and tail lights of `Ford_Focus_closed_off`
and `Kamaz_4310_police_off (3)` in level256, three `Lamp_Industrial_01_BALLISTIC_glass` in level258) carry `EHitType.Lamp`
with network id 0. Both import as authored materials; every other BallisticCollider satisfies the unchanged rules.
