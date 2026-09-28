# Bay5 harbor water sidecar

Regenerate into a **new, ignored output directory**:

```powershell
python tools/extract/extract_water.py --output work/extract/bay5-water
python -m unittest tools.tests.test_water_extraction -v
```

The extractor refuses an existing output directory. For a comparison export, use
another name and compare the five output files. Do not commit these files: they
contain data from the user's own Arena installation. The source is read-only.

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
