# Blurred optics on ported maps: the missing ScopeMaskRenderer (2026-10-16)

User report (2026-10-16, after the 0.2.0 release): looking through a magnifying optic on our maps gives a blurred
image. Reproduced, root-caused and repaired the same day on GPU-2 (`ScopeMaskRepairs.cs`).

## Symptom, reproduced

- Test profile SPT-GPU-2, EFT 0.16.9.5, graphics as on the host (TAA, anisotropic filtering ForceEnable, texture
  streaming off, LOD bias 4). Weapon M4A1 from the stash with an ELCAN SpecterDR 1x/4x (`57ac965c24597706be5f975c`,
  given in raid through the UiBridge `giveItem` hook, carry handle uninstalled, installed through the context menu).
- AirPit, 14:06 raid time, botless PMC raid, ADS at 4x on the hall wall a few metres away: the whole sight picture is
  soft, geometry edges and rivets included, only the reticle overlay is sharp. Still
  `work/screenshots/scope-mask-2026-10-16/01-airpit-elcan-4x-before.png`.
- Factory, same scope on an SR-25 (the M4A1 was lost to the disconnect), same ELCAN at 4x on a rusty wall at a
  similar distance: sharp. Still `02-factory-elcan-4x.png`; side-by-side crop `03-compare-airpit-vs-factory.png`.
- Sharpness metric on the 400 x 400 px scope interior (variance of the Laplacian, grayscale): AirPit 2.3, Factory
  79.5. The scenes differ, so the ratio is not a measurement of the blur itself; the crops are the evidence.

## Ruled out first (live inspection with uibridge_inspect, both raids)

- The optic camera is EFT's own `BaseOpticCamera(Clone)` prefab on both maps with the same component set
  (OpticCullingMask, PostProcessLayer, TOD_Scattering, MBOIT_Scattering, Undithering, Fisheye, VolumetricLightRenderer,
  ThermalVision, ChromaticAberration, NightVision, Tonemapping, UltimateBloom, CC_FastVignette, OpticComponentUpdater,
  OpticRetrice, BloomOptimized, AreaLightManager, PostProcessVolume, StreamingController, CameraLodBiasController).
  With the ELCAN at 4x: FOV 6, Fisheye off, BloomOptimized off, ChromaticAberration on (the ELCAN's own ScopeData),
  Tonemapping off, MBOIT off, TOD_Scattering copied on, CameraLodBiasController off, streaming bias 0, PostProcessLayer
  TAA with jitterSpread 1.0 / stationaryBlending 0.97 / sharpness 0.07 copied from the main camera, identical values on
  both maps. Both PostProcessVolume profiles hold only ScreenSpaceReflections.
- The main camera is the same "FPS Camera" component set on AirPit (our Cam2 fallback plus the plugin's additions) and
  on Factory: no component difference that concerns optics.
- The plugin touches no optic camera component (only NativeMultiFlare reads the optic camera, to add its own flare
  command buffer, as on the main camera).
- No `PostProcessVolume`/`PostProcessLayer` in any extracted scene; Arena's extracts hold no DoF either.
- Source textures: AirPit v8 988 PNGs, Equator v5 757, Bay5 v5 1008, none larger than 2048 px on a 2D texture (the
  512 x 3072 strips are the cubemap faces); the importer's default `maxTextureSize` 2048 therefore cuts nothing.
  Aniso 1 on imported textures is irrelevant with the host's ForceEnable.

## Root cause

Every vanilla location's Scripts scene carries a `TechnicalManagers` root. On Factory (`Factory_Rework_Day_Scripts`)
its children are `ScopeMaskRenderer`, `WindowBreakerManager`, `WaterRenderer`, `lightManager`,
`OnRenderObjectManager`, `ImpostorsRenderer` and `GrassTrampler`. `ScopeMaskRenderer`
(decompile `ScopeMaskRenderer.cs`) hooks `Camera.onPreCull`, and for the main camera draws the active optic's lens
renderer in red and the collimator meshes in blue/green into a camera-sized mask render texture in a BeforeGBuffer
command buffer, then publishes it as the global shader texture `_ScopeMask`. The screen effects that must leave the
sight picture alone, the aiming depth-of-field above all, read that mask. The class has no code references (it is a
serialized scene object), `Hidden/ScopeMask` and `CW FX/Collimator` are in `ShadersFinder`'s pre-registered list.

Arena's scenes ship no TechnicalManagers root (the extracts hold no `ScopeMaskRenderer`, `WindowBreakerManager`,
`ImpostorsRenderer` or `GrassTrampler`), and the plugin so far provided only `OnRenderObjectManager` (WeatherRig) and the
CullingManager fallback (EnvironmentRepairs). `uibridge_inspect find type=ScopeMaskRenderer`: 0 on AirPit, 1 on Factory
at `TechnicalManagers/ScopeMaskRenderer`. Without it `_ScopeMask` is never written, the lens region counts as ordinary
frame, and the aiming depth-of-field blurs the magnified image together with the periphery.

## Repair

`ScopeMaskRepairs.cs` (installed from `Plugin.Awake` right after `CameraEffectsRepairs.Install()`): on the owned Scripts
scene's `sceneLoaded` it creates "ArenaMapPort ScopeMaskRenderer" (DontDestroyOnLoad host, like ShaderRebind's sweep
host) with EFT's own `ScopeMaskRenderer` component, unless `FindObjectOfType<ScopeMaskRenderer>()` already finds one
(one scan at scene load, never periodic). The component's own Awake hooks `Camera.onPreCull` and resolves its two
shaders; its `CameraRender` only draws for `CameraManager.Instance.Camera`, so the cold CameraManager before the raid
camera exists is harmless. On that Scripts scene's `sceneUnloaded` the host is destroyed; the component's OnDestroy
unhooks the callback and destroys the mask texture, as vanilla's own teardown does. Key
`Camera/EnableScopeMaskFix` (default true, next raid). Log `[ArenaMapPort][ScopeMask] added ScopeMaskRenderer for
'<scripts scene>' ...` at Debug; a failure to add it logs a Warning and leaves vanilla behaviour (blurred optics).

## Host verification (GPU-2, 2026-10-16 14:07-14:12, client build from the working tree on top of 4ddd321)

- Deployed through `tools/deploy-to-spt.ps1 -SptRoot 'D:\SPT41' -Confirm` (67 files, only the client DLL changed),
  EFT restarted (`gpu-run restart --force`), `uibridge_status`: 19 maschine plugins identical (VM build = host disk =
  loaded), 43 plugins loaded.
- AirPit botless PMC raid, Mk 16 with the same ELCAN SpecterDR (given in raid, installed in place of the Geissele
  mount), same spawn and view as the "before" still (-16.9, 0.0, -71.7, yaw 0). Log at scene load:
  `[ArenaMapPort][ScopeMask] added ScopeMaskRenderer for 'Arena_AirPit_Scripts' (vanilla: TechnicalManagers/
  ScopeMaskRenderer); _ScopeMask is written again, so the sight picture stays sharp under the aiming depth-of-field`.
  `uibridge_inspect find type=ScopeMaskRenderer`: 1, `ArenaMapPort ScopeMaskRenderer` (DontDestroyOnLoad).
- ADS at 4x: the sight picture is sharp (rivets, panel edges, the reticle's range ladder digits), the periphery blurred
  by the aiming depth-of-field as on vanilla. Still `04-airpit-elcan-4x-after.png`, before/after crop
  `05-compare-airpit-before-after.png`. Laplacian variance of the same 400 x 400 px scope interior: before 2.3,
  after 709.0 (Factory's different view 79.5).
- No `[ArenaMapPort]` warning or error in the raid; 120 fps, max frame 8.6 ms over 300 frames while aiming (the mask
  is one command buffer draw per frame, as on every vanilla map).
- After the disconnect the Scripts scene's unload removed the host object (log `removed ScopeMaskRenderer with
  'Arena_AirPit_Scripts'`, `find type=ScopeMaskRenderer`: 0 in the menu), and the hideout opened normally afterwards.

Evidence stills: `work/screenshots/scope-mask-2026-10-16/` (ignored; the comparison crops are 01-05).

Second pass with the shipped build (14:15-14:20, after the review-driven hardening: both shaders are resolved through
`ShadersFinder.Find` before the component is added, a missing one logs a Warning and skips the repair): same log line at
scene load, one instance, MDR with the ELCAN at 4x from another spawn (-1.0, 0.0, 58.5, yaw 180): sharp
(`06-airpit-elcan-4x-after-shipped-build.png`), no plugin warning, object removed on the disconnect, hideout opened.
