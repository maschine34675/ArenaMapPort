# Equator anamorphic flare and camera lifecycle — 2026-09-27

## Request and diagnosis

Restore the long horizontal construction-light streak from the user's
`Screenshot-2026-09-19-092831.png`, investigate HDR, and reduce Equator's flat lighting.
SPT-GPU-2 is the test host. No bundle rebuild or source Arena asset change is required.

The earlier item-7 conclusion was incomplete: lowering UltimateBloom's threshold restores
a broad halo, not the historical anamorphic flare. Both camera paths already allow HDR;
the donor's UltimateBloom uses HDR and Prism already uses exposure/tonemapping.

Direct extraction from the installed EFT camera assets found:

| Field | Early Cam2 (resources.assets/40780) | Donor (sharedassets15.assets/1185) |
|---|---|---|
| BloomAndFlares enabled | true | true |
| lensflares / mode | true / Anamorphic | false / Anamorphic |
| hdr | On | Off |
| bloom threshold / spread / iterations | 0.77 / 1.89 / 2 | 0.46 / 4.3 / 3 |
| flare intensity / threshold / width | 0.1 / 0.155 / 3.5 | 1 / 0.3 / 3.5 |
| position | before Prism | after Prism |

The shaders are identical references, including `Hidden/MultipassHollywoodFlares`.
Portable-light material `165546dbf4ea570b` and its emission/albedo textures are unchanged
between the old extraction and accepted v4 package. MultiFlare was never present in the
early port and cannot explain its lost streak.

The **order matters**: Prism.OnRenderImage calls GetSourceDestinationHDRToLDR. SSAAPropagator
then returns LDR buffers to later effects. Enabling HDR buffers on the later BloomAndFlares
does not recover the lost highlights. Initial live A/B at 20:12–20:15 confirmed that simply
copying the old fields (including intensity 0.1 versus 1) did not restore the desired stripe.

## Implementation

- `CameraRepairHooks` patches the verified `CameraManager.SetCamera(Camera)` method after
  native initialization. The old one-time event subscriptions stopped working when
  CameraManager.Dispose cleared the event and singleton. Bloom and exposure/scattering/
  volumetric repairs now run on every owned raid camera, including later raids.
- `AnamorphicFlareTuning` uses Equator's existing enabled anamorphic BloomAndFlares pass;
  the disabled ghosting component stays untouched. It restores the old flare color,
  thresholds, blur and stretch while retaining the donor's broad-bloom gain.
- On the donor, a Prism prefix invokes that native pass **before** HDR-to-LDR conversion.
  Its normal later invocation is bypassed only after a successful same-frame render.
  The native SSAA HDR buffer swap occurs exactly once; the bypass writes Unity's ordinary
  destination without swapping SSAA again. No new shader, effect component or texture is
  allocated by the repair. The old fallback already has the correct order and is not moved.
- UltimateBloom's competing anamorphic pass is disabled while this restoration is active.
  Broad bloom controls retain their existing semantics and saved values.
- All modified flare/bloom fields are captured by component reference, restored on disable
  or scene unload, and the dictionaries cleared. Other maps retain their camera look.
- F7 now emits separate camera, exposure, UltimateBloom and BloomAndFlares log entries,
  keeping the useful details below UiBridge's per-entry text limit.

New Equator-only live settings: `AnamorphicFlare`, `AnamorphicFlareHDR`, and
`AnamorphicFlareIntensity` (0.25; the historical 0.1 remains available). Disabling the HDR option restores the camera's own HDR
mode; on the old fallback that original is already On. This is internal HDR processing,
not Windows/monitor HDR output. Prism still produces an LDR image.

## Native validation

Implementation build and visual acceptance are recorded separately below after final testing.
The first test process was `5ba50e4b`; VM build = host disk = loaded. Automatic SetCamera
application was visible at 20:12:29 (#2857–2861). This also discovered the user's current
saved broad-bloom settings: threshold 1.611502, intensity -0.01408451 (negative means
native intensity). These replace the earlier diagnosis's baseline values and are preserved.

The first parameter-only experiment is **not** visual acceptance. The render-order fix is
required; see the final evidence appended below.

## Render-order A/B — 20:21–20:25, instance 47843d72

Loaded DLL matched VM and GPU disk. At 20:21:24 (#2861) SetCamera applied the flare; at
20:21:35 (#2887) the first native pre-Prism render completed. At the fixed atrium viewpoint
`(1.5,0.4,12)`, yaw180/pitch-8, intensity1 produced an unmistakable long blue-white
horizontal stripe across the lights and map board. Intensity0.25 retained a subtle stripe;
0.1 was difficult to see at that distance. Switching HDR off removed the useful stripe,
then restoring On brought it back. The EQUATOR lettering remained readable.

AmbientIntensity 1 versus 0.85 was compared live under the existing rainy noon conditions.
0.85 gives modestly deeper shade while retaining floor, planter and hand detail. The reverse
atrium view was checked too. This is a restrained contrast change, not a tone-map replacement.
Equator now defaults to 0.85; DefaultsRevision 4 moves only the exact legacy 1 value and leaves
other user adjustments intact. Bay5 remains 1. Lighting intensity/reflections are unchanged.

No render errors occurred. On opening the pause menu at 20:25:15 (#3024), Unity emitted the
previously documented `MenuScreen is inactive` coroutine message (also present in the pre-change
native-dust evidence). This is not claimed as a clean global log; no flare/camera exception appeared.

## Final build and native acceptance — 20:29–20:42

Final client SHA256: `98028C31BC87B90FE8E112A1BB857E06540CBC72CFE27AFF9CC08AB21893DB9C`.
Release client build succeeded with zero warnings/errors; Release server/shared tests passed.
Sanctioned deployment reported 86 files, only the client differing on the final pass; all map
assets stayed identical. No Unity/bundle build was necessary.

Fresh EFT process **f307691a**, started 20:29:07, verified VM build = host disk = loaded for
all 23 maschine libraries/plugins. The following four botless raids ran without another restart:

| Check | Native evidence |
|---|---|
| Migration | Before restarting, AmbientIntensity was explicitly restored to legacy 1 with revision 3. After restart: 0.85, revision 4; saved threshold 1.611502 untouched. |
| Equator donor | 20:30:37 #2857–2861: exposure/scattering/volumetric checks; automatic bloom 2.10→1.61; flare 0.25/HDR On. 20:30:47 #2887: native pass before Prism. Fixed atrium screenshot shows the horizontal streak. |
| Live disable/re-enable | F7 20:32:40 #3016–3017: original donor BF HDR Off, lensflares false, spread 4.3, threshold 0.46, iterations 3; Ultimate anamorphic true. Re-enable #3020/#3022 restores the tuned pass and render route. |
| Bay5 after Equator | 20:34:47 #3130–3132: camera repairs on the new manager. F7 20:35:09 #3327–3328: native Ultimate threshold 2.10, anamorphic true; BF HDR Off/lensflares false. No Equator flare override. |
| Equator again | 20:37:26 #3423–3427: automatic bloom and flare on a new component ID. #3447 pre-Prism render; same atrium screenshot confirms the streak without touching any setting. |
| Equator fallback | UseDonorCameraPrefab=false for this raid only. 20:40:22 #3646: FixExposure actually enables exposure; #3647 adds scattering. F7 #3775: useExposure true, limits -10..1.30. The old camera keeps its already-correct pass order. |

Every raid returned to the main menu. The only Error-level entry in this final process,
#3024 at 20:33:14, was the same pre-existing inactive MenuScreen coroutine message.
No camera/flare errors or repeating render exceptions appeared through #3810.
UseDonorCameraPrefab was restored to true; AnamorphicFlare/HDR are true, intensity 0.25,
AmbientIntensity 0.85. Existing weather, time, broad-bloom and Bay5 settings remain intact.

The visual comparison used the existing rainy noon setup, 1920x1080 on SPT-GPU-2. This is
native rendering acceptance for those conditions, not a benchmark, HDR-monitor test or
an assertion that every weather/time combination has been visually approved by the user.

The host-verified build was also deployed through the sanctioned helper to `D:/SPT41vanilla`:
86 files, two DLLs differed, all 84 other files already identical. Final client hashes match in
dist, `D:/SPT41` and `D:/SPT41vanilla`. Vanilla requires the user's EFT restart to load it;
it was not used for this session's native test. GPU-2 remains at MainMenu, 440/440 HP,
energy 65 and hydration 74 (UiBridge local profile readback).
