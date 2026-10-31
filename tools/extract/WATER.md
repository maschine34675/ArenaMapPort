# Native sea water sidecars (Bay5's harbor, Iceberg's sea)

Regenerate into a **new, ignored output directory** (one run per map; `--map` takes the key or the stem, default `arena_bay5`;
`--output` defaults to `work/extract/<stem>-water`):

```powershell
python tools/extract/extract_water.py --output work/extract/bay5-water
python tools/extract/extract_water.py --map arena_iceberg --output work/extract/iceberg-water
python -m unittest tools.tests.test_water_extraction tools.tests.test_native_water -v
```

The extractor refuses an existing output directory. For a comparison export, use
another name and compare the five output files. Do not commit these files: they
contain data from the user's own Arena install. The source is read-only.
(The sections below were written for Bay5; Iceberg's facts are in the last section.)

`water.json` contains the complete authored `WaterSettings`, source provenance,
one target addressed by scene and hierarchy path, and four texture descriptors.
Each descriptor gives the relative raw file, SHA-256, byte count, original asset
file/path ID, native Unity texture format, full mip count, color space and sampler
settings. Load with `Texture2D.LoadRawTextureData`, retaining all supplied mips;
do not decode normal maps as ordinary RGB images or generate replacement mips.

## Source recovery evidence — 2026-09-28

The nine accepted Bay5 content scenes contain two `EFT.Water.WaterContainer`
components, both in `level84` / `Arena_Bay5_Main`:

| Component | Owner | Targets |
| --- | --- | --- |
| 100550 | `Sea` (19569) | MeshRenderer 63361 / MeshFilter 81541 on `Sea/Plane` (19568) |
| 100549 | `WaterForSSrv3` (1586) | One null/null target pair; no surface |

Both components are enabled and their owners active. Their 344-byte settings
blocks are identical. The null-target object is unrelated to puddles. The actual
sea mesh is already in the accepted v5 map package, on Water layer 4, with an
empty material array and an existing native Water ballistic collider.

The 404-byte component has a 32-byte MonoBehaviour header, a 344-byte settings
payload matching the installed EFT `WaterSettings` declaration order, a target
count, and Renderer/Filter pointer pairs. References are type-checked; the
exporter checks the target owner, hierarchy, layer and material state. Unknown or
truncated payloads, bad references and incomplete mip chains fail extraction.

| Settings field | Source reference | Native texture format | Color space |
| --- | --- | --- | --- |
| `_rippleTexture` | `sharedassets36.assets:17`, `circles` | ARGB32 (5) | sRGB |
| `_normals` | `sharedassets51.assets:354`, `Static Voronoi - Tileable - NM2` | DXT5 (12) | Linear |
| `_normalsDetails` | `sharedassets51.assets:257`, `detailedWavesMap hd2` | DXT5 (12) | Linear |
| `_foam` | `sharedassets51.assets:297`, `Foam 01` | DXT1 (10) | sRGB |

All four textures are 512 × 512 with ten mip levels; their full data is 2,271,980
bytes. The generated manifest records content hashes. The original normal-map
packing is preserved exactly by exporting compressed bytes.

## Runtime mapping and limits

Arena's newer `EFT.Water` renderer is absent in the installed EFT assembly. The
compatible target is `WaterSSR.WaterForSSRv3` plus `WaterSSR.WaterRendererv3`, with
native `Hidden/WaterForSSR` (installed `globalgamemanagers.assets`, Shader 260;
D3D11 variant and expected settings properties verified). Arena constructs its
water material in the water system; there is no missing material PPtr to copy.

Use the existing mesh and a runtime `WaterObject` linking its MeshFilter,
MeshRenderer and GameObject. The native renderer draws through a camera command
buffer, rather than an ordinary MeshRenderer material. The original targetless
container and two newer water-renderer components in Arena's Scripts scene are
not imported. The old SimpleWater placeholder must relinquish this surface.

This offline evidence establishes a recoverable source and compatible settings
contract. It does not establish visual equivalence with Arena's newer renderer;
day/night, reflection, shoreline and unload behavior need GPU acceptance.

Lifecycle notes from the installed decompile: `WaterRendererv3.OnDisable`
unsubscribes events and destroys its material. `CommandBufferManager.DestroyBuffers`
removes buffers from cameras but does not dispose them. Owned buffers therefore
need explicit disposal, and the owned renderer should unregister from
`SnowRenderer` on teardown. Avoid creating competing native water renderers.

## Iceberg's sea (2026-10-31)

`Arena_Sanatorium` (`level256`, BuildSettings index 256) holds the same two containers as Bay5's `level84`: `Sea` (component 144056, one target,
the mesh `Sea/Plane`, layer 4 Water, no materials) and the unused `WaterForSSrv3` (component 144055, its single target is a null/null pair). The export
accepts the empty second container and still refuses any other container with a real target, in the same level and in the nine other level files of the map
(255, 257, 258, 259, 260, 375, 376, 377, 378: none holds one). The `Sea` settings and the four textures are byte-for-byte the harbor's (Arena's shared sea
prefab); only the identity differs (`map_key`, scene `Arena_Sanatorium`, the Arena path ids and the raw container hash). The plane has the same
scale as Bay5's (599.68 x 690.19 on Unity's 10 x 10 Plane, about 6.0 x 6.9 km, x -2272..3725, z -4596..2306) and sits at y -18.9 (Bay5: -8.65), below the
play area (y > -3) and above the sand bed of the canyon (y -22..-26).

The extractor's table `MAPS` (`WaterMap`: key, stem, level index, scene, container and surface path, census levels) is the only per-map data; BuildSettings
must still put the scene at its index. The three stream textures (normals, details, foam) are fragments of `sharedassets51.assets.resS`, which is also a
stage-2 key-source file of Equator, and the ripple texture sits inline in `sharedassets36.assets`. The export therefore checks every texture against
`tools/bundle/key-sources.json` (block 65,536 B, 16 blocks at `k * (size - block) // 15`): a range on a sampled block or a byte copy of a key-source file
stops it. Margins in Arena 0.6.1: 4.77 MB (normals), 0.92 MB (details) and 0.71 MB (foam) to the nearest sampled block.
