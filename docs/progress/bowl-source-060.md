# Bowl source delta: the 0.5.4 spike package versus the Arena 0.6.0 native package

Read-only comparison of `work/extract/bowl` (extracted 2026-09-15 from Arena 0.5.4, plan digest `bf6dc15a...`, no
native features) with `work/extract/bowl-native-v1` (extracted 2026-10-18 08:47 UTC from Arena 0.6.0.47752 with
`--native-features`, plan digest `1dd38529...` after the scene roles were added). Same role as
[airpit-source-060.md](airpit-source-060.md) for AirPit.

## Scene content

| Scene | index | spike objects | 0.6.0 objects | note |
|---|---|---|---|---|
| Arena_Bowl | 41 | 39,911 | 39,911 | identical count |
| Arena_Bowl_Building | 46 | 12,380 | 12,380 | identical count |
| Arena_Bowl_Grass | 95 | 3 | 3 | GPUInstancer manager objects, components dropped (grass is not ported) |
| Arena_Bowl_Light | 45 | 1,738 | 1,738 | identical count |
| Arena_Bowl_DesignStuff | 43 | 15,639 | 15,654 | +15 layer-29 `Cube (N)` under `Arena_Bowl_LevelBorders` (0.6.0 added border cubes; two more moved) |
| Arena_Bowl_TeamFight | 47 | 9,814 | 9,814 | identical count |
| Arena_Bowl_TeamFight_DesignStuff | 48 | 678 | 678 | identical, object-for-object (survey 2026-10-18) |

Package totals: meshes 3,721 -> 3,721; materials 381 -> 467 (the native decal materials, as on Equator/Bay5 v5 and
AirPit v5); textures 823 -> 905; warnings 239 -> 239 (the same "MeshFilter: no mesh assigned" family and 16 cubemap
notices), errors 0 -> 0. The live census (`extract_map.py --census-only`) shows 10 RectTransform UI objects in
DesignStuff and 6 in TeamFight_DesignStuff that the extractor skips in both versions (15,664 / 684 GameObjects in the
level files).

## Native blocks of v1 (absent from the spike)

BallisticCollider 9,215 (41: 5,048, 46: 2,375, 95: 0, 45: 269, 43: 29, 47: 1,421, 48: 73), StaticDeferredDecal 766,
NavMeshModifier 39 + NavMeshModifierVolume 76, CullingLightObject 106, VolumetricLight 4, LightFlicker 8,
SmallPhysicsObject 341, rain cover 10 IndoorTrigger / 0 DryPlane / no WeatherObstacle (Scripts scene level44), NavMesh
areas Walkable / Not Walkable / Jump / Sitdown / Danger. Ambient audio is not in v1 (`required = ["ballistics"]`,
contract `audio: null`); v2 adds it (PLAN-BOWL item 4).

Two BallisticColliders of Arena_Bowl (`Guardian_Tower_Fance_BALLISTIC_MetalThin`) carry the named preset `Chainfence`
over the material `MetalThin`; `extract_ballistics` imports the reviewed pair as authored
(`REVIEWED_PRESET_MATERIAL_PAIRS`, 2026-10-18). Every other BallisticCollider satisfies the unchanged rules.
