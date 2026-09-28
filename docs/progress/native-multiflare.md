# Original per-light MultiFlare — 2026-09-28

## Result

Equator and Bay5 now render their original per-light flare sprites and lens noise
with EFT's native MultiFlare renderer. This is separate from Equator's existing
camera-wide BloomAndFlares horizontal streaks. New live switch:
`Lighting (<map>) / NativeMultiFlare=true`. Existing bloom, HDR, light strength,
weather and audio values retain their meaning. No scene bundle rebuild.

| Map | Source lights | Active lights | Source flare elements | Active elements |
|---|---:|---:|---:|---:|
| Equator | 28 | 25 | 48 | 33 (31 Normal, 2 Noise) |
| Bay5 | 25 | 24 | 116 | 111 (96 Normal, 15 Noise) |

Inactive source objects remain inactive. The old PLAN-TWO-MAPS claim that private
Flare[] data was unrecoverable is superseded by this verified extraction.

## Source recovery and compatibility

`tools/extract/extract_multiflare.py` reads the user's Arena installation. Lights
are in level55 (Equator) and level83 (Bay5); scene settings are level54:1865 and
level85:7756. Every component is fully consumed. All 53 lights are independently
decoded using the original X400 weapon bundle's type tree and matching
MonoScript properties hash.

Arena adds `_cullDistance` between total alpha and Flare[] (500 m on every source
light). Other element fields match EFT: scale, alpha, color, type, distance range,
near/far scale and alpha, atlas index. Component state, inherited activity and
hierarchical transforms are preserved.

Shared ProFlareAtlas: sharedassets4.assets:21, ten UV rectangles. Original
materials :5/:6/:4 use `Custom/multiFlare` and `Custom/ShitOnScreen`. Installed EFT
shaders have the corresponding properties/uniforms. Saved uniforms absent from
ShaderLab Properties must still be set, especially `_FadeDepthOffset`; filtering
solely by Material.HasProperty would incorrectly discard them.

Raw textures preserve compression, color space and sampling:

- Atlas2: BC7, 1024 x 1024, one mip, 1,048,576 bytes;
  SHA256 `d04a4cfb624e6a2a86d928074b117b26abb55cf9d691c9922fd6aff5d0d95170`.
- Noise: BC7, 512 x 512, one mip, 262,144 bytes;
  SHA256 `4a2028a2380645814b49acdc259ac5ef99eea888e1625a06daf0edcacb76601d`.

Arena's atlas is not pixel-identical to EFT's atlas, which also has eleven mips.
Using the generic installed flare bundle would change the source look. Each
ignored JSON records source/component hashes, schema bundle provenance, atlas,
materials and texture hashes. No proprietary data is committed. Six new files
are staged across both maps.

Explicit compatibility limit: Arena's scene settings end with an additional
float `1` absent from EFT. Its semantic field name was not identified. It is
retained as `source_extra_scalar`; values other than the verified 1 are rejected.
No behavior is invented for it. Exact Arena visual parity beyond the native
mapped fields is not claimed.

Regenerate with:

```powershell
python tools/extract/extract_multiflare.py --map arena_equator --output work/extract/multiflare/arena_equator
python tools/extract/extract_multiflare.py --map arena_bay5 --output work/extract/multiflare/arena_bay5
dotnet build ArenaMapPort.Client/ArenaMapPort.Client.csproj -c Release -p:ArenaMapPortDllOnly=true
```

Release builds stage plugin `flares/<map>/`. Distribution validation checks the
graph, exact file set and texture lengths/hashes.

## Native rendering and ownership

`NativeMultiFlare` owns LightStorage, native Normal/Noise/FlashLight batches,
source materials/textures and transform-only inactive proxies. It adds no native
FlareLight, FlareSceneSettings or FlareScheduler component: their lifecycle uses
or resets the global manager, including foreign weapon lights.

Fixed local light data and transforms feed the native jobs/GPU buffers. The
empty native FlashLightBatch remains so its position-job dependency completes
although neither map contains FlashLight elements. GPU tests confirmed this
empty path works. Original paths resolve to existing map objects: all 25/25 and
24/24 positions match within 2 cm. Their inherited activity participates in the
enable state.

One GameCameraRenderer per actual main/active optic camera uses EFT's SSAA target
and AfterForwardAlpha before image effects. Polling handles camera replacement
without relying on CameraManager's cleared events. Native shaders use camera
depth for occlusion; physics rays do not substitute visibility. Own settings
follow native night vision and player blindness protection, explicitly reset to
zero on player changes.

Off, partial failure and unload complete native batch handles, detach only owned
camera buffers and release owned resources. No global manager setup/dispose or
rendering toggle occurs. F7 reports batch/output counts from a completed-frame
cache so it cannot read NativeList data while jobs write it.

## Validation and GPU-2 evidence

Client SHA256:
`856E40A9B0E06E8964C590AC8E1C0702DA2DA4C0C1466858ADC742F7CF2955EF`.
Build: zero warnings/errors. Pipeline: 478 tests, 28 expected skips. Fifteen new
serialization/graph tests and two distribution tests cover valid controls,
layout confusion, exact consumption, invalid values/native divisors, inactive
ancestors, atlas/graph references, texture shape, missing files and tampered
texture bytes. Distribution validation passed: 14 maps / 42 bundles / 14 shader
sidecars plus new flare data. Independent source/runtime reviews preceded tests.

GPU-2 instance `236edef3`, started 16:41:14 CEST, 2026-09-28. All 23 plugin
identities matched VM / disk / loaded process. No-bot debug raids preserved Clear
weather and saved lighting.

### Equator, 16:42–16:46

- 16:42:24.488: sourceLights=28, activeLights=25, flares=33, boundOriginals=25,
  nativeBatches=3, originalTextures=2, isolated=true. FPS camera -1261582, HDR/SSAA.
- 16:42:44.365: completed GPU counts 31/31 Normal, 2/2 Noise, 0/0 FlashLight.
- Visible A/B: player (-4.5, 0.1, 38.5), lamp (-3.006, 4.413, 28.805).
  Original warm halos around elevator downlights disappear with live off and
  return with on. Existing white lamp surfaces remain visible.
- From (-3, 0.4, 24), the elevator shaft blocks that light: no flare through
  the shaft. Returning to the original position restores the halo.
- 16:43:23.842 off and 16:45:59.420 exit: ownedLights=0, ownedBuffers=0,
  cleanupFailures=0. On at 16:44:00.461 recreates the original counts.

### Bay5, 16:47–16:50

- 16:47:06.793: sourceLights=25, activeLights=24, flares=111, boundOriginals=24;
  fresh FPS camera -1342782, HDR/SSAA.
- 16:47:26.159: GPU counts 96/96 Normal, 15/15 Noise, 0/0 FlashLight.
- Visible A/B: player (133, -2.9, 40.5), lamp (134.330, 6.070, 50.670), clear
  linecast. Warehouse strip lights gain original broad warm halos. Off removes
  them at 16:48:29.909; on restores them.
- From (124, -3.9, 45), the warehouse metal wall blocks the same lamp; no flare
  through the wall. Native depth occlusion preserved.
- 16:50:28.882 exit: ownedLights=0, ownedBuffers=0, cleanupFailures=0.

### Equator again, 16:51–16:52

- 16:51:29.733: exact 25 lights / 33 elements on fresh camera -1392946.
- 16:51:57.936, after 2,488 frames: GPU counts 31/31, 2/2, 0/0, one camera.
- 16:52:34.668: complete cleanup, zero failures, after 6,665 frames.

The global manager retains its one foreign light and no renderer through all
activations, live toggles and cleanup. No new MultiFlare/Unity exception was
observed. Bay5's 390 Graphics.CopyTexture readable-compressed-data warnings also
exist in pre-change `LogOutput-SPT-GPU-2-2026-09-28_15-47-20.log`
(e.g. 15:48:41.783). Separate optic/NVG/glasses-equipped visual comparisons were
not performed; those paths are source-reviewed. Subjective strength remains the
user's visual check.

## Delivery

The approved deploy helper copied the verified build to both `D:/SPT41` and
`D:/SPT41vanilla`. Independent SHA256 comparison matched **103/103 files** in
each installation against dist. Existing v5 bundles and server behavior are
unchanged. No installation config file was edited or deployed.

Final GPU-2 state: MainMenu, no raid, control free, all 23 plugin identities
identical across VM/disk/loaded. Both NativeMultiFlare switches are on. All eleven
previously nondefault ArenaMapPort settings match the before-test snapshot,
including the user's lighting and Clear weather. The user's own game needs a
restart to load the new client; its local playthrough remains separate from
GPU-2 verification.
