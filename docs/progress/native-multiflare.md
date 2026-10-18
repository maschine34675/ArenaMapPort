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


## Bowl (arena_bowl) - 2026-10-18 (PLAN-BOWL item 17)

Bowl joins the exporter, the packaged data and `NativeMultiFlare.Install` (new keys `Lighting (arena_bowl) /
NativeMultiFlare` = true and `NativeMultiFlareIntensity` = 0.5; Equator's and Bay5's keys and defaults are untouched).
Not host-verified: no Bowl raid has run this build. No scene bundle rebuild.

### Census (Arena 0.6.0.47752, read-only UnityPy over levels 41-48, 95, 96, 111, 128)

- One `FlareSceneSettings`: `level44:229` (`TechnicalManagers/lightManager` of `Arena_Bowl_Scripts`), enabled, active,
  `source_extra_scalar` 1. Atlas `sharedassets4.assets:21` (ten rectangles, equal to Equator's), batches 2048 / 512 / 256,
  materials `:5` MultiFlare, `:6` ShitOnScreen, `:4` FlashLightFlares with floats, colors, keywords and render queue equal
  to Equator's. Textures: the same two BC7 chains (atlas `sharedassets4.assets:11`, SHA256 `d04a4cfb...`; noise now
  `resources.assets:2969`, was `:2812` in the 0.5.4 data, SHA256 `4a2028a2...` unchanged).
- Every `FlareLight` is in `level45` (`Arena_Bowl_Light`, ids `6702`-`6729`): 28 lights, 130 elements (108 Normal,
  22 Noise, no FlashLight). Levels 41, 42, 43, 44 (apart from the settings), 46, 47, 48, 95, 96, 111 and 128 hold none.
- Activity (component enabled AND every ancestor active, the rule `NativeMultiFlare` applies):

| Group | Lights | Elements |
|---|---:|---:|
| Active: `BACK_FLARES/Flare_City_background` (4 Normal + 1 Noise each, total alpha 1) | 4 | 20 (16 Normal, 4 Noise) |
| Component disabled, chain active: `street_lamp1_destroyable*/StreetLampFlareMain` | 6 | 30 |
| Component enabled, chain inactive: `StreetLampFlareDistant` x6, `Lum_lamp_destroyable*` x3, one glowstick | 10 | 41 |
| Component disabled and chain inactive: glowsticks x7, one `Lum_lamp_destroyable` | 8 | 39 |
| Total | 28 | 130 |

So the pin is `(28, 4, 130, 20)`. PLAN-BOWL's statement is confirmed and sharpened: exactly six lights fail a require,
all `StreetLampFlareDistant` (`level45:6720`-`6725`, the object itself is inactive), each on its fourth element only
(the Noise element, `min_dist` 50 > `max_dist` 15, i.e. `invalid distance range`). The other 22 lights pass every require
unchanged; the settings require ("inactive source settings") does not fail.

### Inactive-light rule

Inactive lights stay in the package as entries (as before on Equator/Bay5: 3 and 1). `validate_light(d, in_hierarchy)`
demands `min_dist < max_dist` only for a light whose component is enabled and whose chain is active; for any other light
it still demands both distances finite and non-negative, and every other rule (scale/alpha/culling, vectors,
color/alpha, type 0 or 2, non-negative and non-zero native divisors, atlas index) stays for all lights.
`validate_graph` derives the flag from the light's own `active_in_hierarchy` after proving it equals the transform chain.
Why this is exact: `NativeMultiFlare.StartFlares` skips a light with `!enabled || !active_in_hierarchy` as the first
statement of its loop, before the scene check, the transform proxies, `Resolve` and `ReadFlare`; only `ReadFlare` carries
the native division guard (`maxDist > minDist`, `minScale > 0`, `minAlpha > 0`) that the six inverted lights would trip.
A future runtime change that activates source-inactive lights must first re-apply the ordering requirement.
`export` also compares against a per-map `EXPECTED_CENSUS` table now; the former `else` branch would have used Bay5's
tuple for any third map.

### Regression guard (Equator, Bay5)

The accepted `work/extract/multiflare/arena_equator|arena_bay5` were exported from Arena 0.5.4 and are untouched. The
install is 0.6.0 now, so a byte comparison against them cannot be the guard: it differs by provenance alone (43 and 45
JSON paths: `source_raw_sha256` of settings, atlas, material and every light, `source/files[].sha256`, and the noise
texture id/file name `resources.assets:2812` -> `:2969`; Bay5 additionally `settings.source_id`). Every light value,
transform, flare element, material value, batch setting and texture byte (SHA256 of both `.bin` files) is equal.
The decisive comparison runs the committed (HEAD) extractor and the patched one on the same 0.6.0 install:

| Map | flares.json SHA256, HEAD extractor = patched extractor | textures |
|---|---|---|
| arena_equator | `6ee7507bcc381093d723f08f7cba73e3dc07e857a7eb91d576447dfc7a45dcf3` | both `.bin` identical |
| arena_bay5 | `8013a1be671d633eef6d71d50bbe05b4f6c896a3f9ac2650c174f6e12ae21f92` | both `.bin` identical |

Both printed their `MULTIFLARE VERIFIED` line unchanged (25/28, 33/48 and 24/25, 111/116). The accepted 0.5.4 inputs
stay the distribution inputs; they need not be regenerated for this change.

### Bowl output and runtime requires

`python tools/extract/extract_multiflare.py --map arena_bowl --output work/extract/multiflare/arena_bowl` printed
`MULTIFLARE VERIFIED: arena_bowl: 4/28 active lights, 20/130 active flares, 2 original textures`
(`flares.json` 149,472 B, SHA256 `6090e9e4...c2a2`; `textures/sharedassets4.assets_11.bin` 1,048,576 B;
`textures/resources.assets_2969.bin` 262,144 B). Replaying the data requires of `StartFlares` on the file: identity,
settings, ten atlas rectangles, texture names/hash/layout, shader names and `_MainTex`/`_ShitTex` properties, capacities
<= 8192, and for the four active lights scene membership in the descriptor list, cull distance > 0, native division,
type and atlas index (tex ids 0, 1, 8) all hold; `lamps=4 flares=20` passes the "no active flares" guard.
Offline `Resolve` against the extracted `Arena_Bowl_Light` scene (one `SBG_Bowl_Light` root): the four active lights
resolve and sit exactly (0.00 m) on their proxies, inside the runtime's 2 cm tolerance. Of the 24 inactive lights,
which the runtime never resolves, 23 also match and `level45:6712` does not (22.6 m): it shares its full path with
`6711` (two `glowstick_rezerv/NewMultiFlareLight stick` siblings), which is why a path alone cannot address it.

### Open: the four active lights are outside their own cull distance

The active lights are the "city background" glows, at world (502.8, 394.5, 514.4), (420.6, 486.5, 632.8),
(554.3, 399.8, 611.3) and (600.4, 300.1, 480.7). The nearest point of the play bounds (x -66..62, y -2..10, z -82..11)
is 771.1-868.7 m away; even a generous box around the stands (x +-100, y -10..60, z -120..50) is 699.9-798.0 m away.
Their `cull_distance` is 500 m, and `NativeMultiFlare.Update` keeps `Enabled` false for a lamp farther than that from
every camera, so with the shipped rule the feature binds and allocates on Bowl but draws nothing. (The active
elements' 0.1-40 m distance ranges are not a second limit: EFT's Flare tooltip says an element holds MaxScale and
MaxAlpha at and beyond MaxDist.) Arena is IL2CPP, so the original cull semantics could not be read and nothing is
claimed about them. A host raid shows it: the F7 probe's `[MultiFlare] arena_bowl lights=4` line and the four `lamp=...
distance=` lines. Making the glows visible would need a changed cull rule for this map, a separate decision.

### Staging rows still needed (integrator)

1. `ArenaMapPort.Client/ArenaMapPort.Client.csproj`, target `ArenaMapPortStageMultiFlare`: add

   ```xml
   <Error Condition="!Exists('$(ArenaMapPortFlareDir)\arena_bowl\flares.json')"
          Text="Missing original Bowl flare input; run tools/extract/extract_multiflare.py --map arena_bowl --output work/extract/multiflare/arena_bowl" />
   ```

   and, after the Bay5 row in the `ArenaMapPortFlareFile` item group:

   ```xml
   <ArenaMapPortFlareFile Include="$(ArenaMapPortFlareDir)\arena_bowl\flares.json;$(ArenaMapPortFlareDir)\arena_bowl\textures\*.bin">
     <MapKey>arena_bowl</MapKey>
   </ArenaMapPortFlareFile>
   ```

2. `tools/validate/validate_distribution.py`: `FLARE_MAP_KEYS = ('arena_equator', 'arena_bay5', 'arena_bowl')` and the
   comment above it (AirPit alone has no flare package). The release tooling (`make_release_zips.py` classifies
   `flares/<key>/` per map) needs no change.
3. `tools/tests/test_validate_distribution.py`: add `("arena_bowl", "Arena_Bowl_Light")` to the fixture's key/scene
   tuple and replace the Bowl assertions that no `flares/arena_bowl` may exist (`assertFalse(... "flares/arena_bowl"
   ...exists())` and the stray-package block) by "present and valid, plus a stray file is refused".

## 2026-10-18 (evening) - Bowl package on GPU-2

Raid 3 of the 0.3.0 verification (Bowl, 00:30): `[MultiFlare] arena_bowl enabled: sourceLights=28 activeLights=4 flares=20
boundOriginals=4 nativeBatches=3 originalTextures=2 isolated=true`; F7: `[MultiFlare] arena_bowl lights=4 cameras=1 ...
intensity=0.500` and the four `lamp=SBG_Bowl_Light/OO/BACK_FLARES/Flare_City_background ... distance=788.91 / 790.53 /
880.99 / 887.65`, every one beyond the 500 m cull from the east pocket, so nothing is drawn; no `disabled after error`
line in the process (three raids, hideout). The package stays staged for that reason only; a visible city glow would need
a map-specific cull rule, which remains undecided.
