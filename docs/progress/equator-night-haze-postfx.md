# Equator night distance haze and temporary PostFX — 2026-10-01

## Scope

The user requested a subtle haze throughout the night map and asked for temporary
mod-controlled PostFX. Both belong to the existing optional Equator 00:00 neon
installation. Day, other maps and the menu retain the user's native PostFX.

F12, `Night neon (arena_equator)`:

| Setting | Default | Effect |
|---|---:|---|
| `DistanceHaze` | `0.002` | Cool native distance fog across the map, starting beyond 5 m; 0 disables this addition. Range 0–0.01, live. |
| `PostFx` | `true` | Temporary balanced Chillwave camera preset; false restores current saved native PostFX. Live. |

These are separate from local light-cone `HazeStrength`, `SmokeDensity` and
flicker. They share the master neon switch and confirmed midnight gate.

## Native implementation and review

The donor camera has `TOD_Scattering` and `MBOIT_Scattering`. An initial density-only
trial correctly changed the rendered density from 0.004076 to 0.006076, but mostly
darkened distant surfaces at midnight. Screenshots 01–08 document that rejected
trial; they are not acceptance evidence for the shipped haze. Disassembly of the
native scattering shader (globalgamemanagers.assets object 280) and MBOIT compute
(resources.assets object 2609) confirms that their color depends on the night sky.

The final `NeonDistanceHaze` instead owns an additional native
`UnityStandardAssets.ImageEffects.GlobalFog` component on the active raid camera.
Its existing `Hidden/GlobalFog` shader (globalgamemanagers.assets object 129) renders
distance-only pass 1 after opaque geometry, before transparents. Exponential
density 0.002, a 5 m start and a dim cool tint (0.15, 0.20, 0.23) provide a gradual
veil independent of TOD. This is one extra native pass, with no new texture/bundle.

Only the owned component's synchronous render temporarily changes
`RenderSettings.fogMode/fogDensity/fogColor`; postfix/finalizer restore those
fields and the entry `unity_FogColor` global. Existing fog components are untouched.
An owned but inactive call blits unchanged until upkeep removes it. Off, camera
replacement and scene teardown disable the component immediately, then destroy
it and its cached private material, including after its camera was destroyed.
Native `CheckResources` material capture is necessary because GlobalFog itself
does not dispose the material. No camera depth flags are cleared.

`NeonNightPostFx` uses native `PostFxSettingsGroup.Clone()` and
`PostFxSettingsController` on `CameraManager.PostFX`. A detached clone receives
enabled PostFX, brightness 0, saturation 35, clarity 0, colorfulness 45, both
sharpen controls 0, Chillwave/50. Colorblind settings stay the user's own.
No writes go to `SettingsManager.PostFx.Settings` or its provider/save methods.

The override suspends while native Settings or PostFX Preview is open and resumes
after closing. Restoration uses the latest global user settings, including edits
saved during the raid. A stored comparison clone detects delayed native saves
after closing settings and reapplies the night camera preset. Camera replacement,
master off, scene unload and destruction release the previous override.

Source references: local EFT 0.16.9.5 firstpass `GlobalFog`/`PostEffectsBase`,
`TOD_Scattering`, `MBOIT_Scattering`,
`EFT.Settings.PostFx/PostFxSettingsController`, `PostFxSettingsGroup`,
`Bsg.GameSettings/BaseSettingsController`, `EFT.UI.Settings/SettingsScreen`,
`EFT.UI.Screens/PostFXPreviewScreen`, and `EFT.CameraControl/CameraManager`.
Independent source reviews caught and resolved delayed-save and shader-cleanup
edge cases before host acceptance. The final fog review also corrected a one-tick
inactive fallback and retained material ownership after camera destruction.

## Relation to the earlier manual experiment

The 14 experimental ArenaMapPort image settings manually re-enabled on October 1
are not rewritten or migrated by this feature. Those existing Equator settings
still affect both day and night. Only native PostFX and the new distance haze are
automatically scoped here. The earlier manual global PostFX activation is restored
through the native UI to the backed-up pretrial state before testing the automatic
night override; that is test setup, not a settings migration in the shipped mod.

## Final native acceptance — SPT-GPU-2

Final process `1b7de081`, started 2026-10-01 18:12:48 CEST, EFT 0.16.9.5 / SPT 4.1.6,
1920x1080. UiBridge confirmed 24/24 plugin builds equal between VM, disk and loaded
assemblies. Client SHA256:
`6BF9D9F3C0BA72E69AA2AAF94EF0377978EC63FB42654B360510CB616FCFBBEA`.
Server SHA256 (unchanged behavior):
`982D9E562F14E14BA1FBDFFAFFE66A533B6F8DD2F00FA6C5D02695CFE5EA9895`.

- 18:15:30–31, lines 2871/2898/2901: Equator 00:00, Rain, balanced Chillwave applied
  while saved settings remain disabled, Jason/50, saturation/colorfulness 0.
- 18:15:41, line 3149: owned `Hidden/GlobalFog` distance pass created.
- 18:16:09, line 3280: active density 0.002; native settings outside the render
  remain ExponentialSquared, density 0, native weather color (not the cool fog tint).
- Screenshots 09/10/11: same center position `(0,0.1,14)`, yaw 180, pitch -10,
  comparing 0.002 / 0 / 0.006. Default slightly lifts distant dark surfaces while
  retaining foreground contrast; 0.006 is visibly stronger. Independent visual
  review found no obvious veil, hard boundary or new artifact. Local smoke, neon
  cones, emissive strips and wet-floor reflections remain visible.
- 18:17:44, lines 3335–3369: PostFx off restores disabled Jason; on restores
  Chillwave. Master off removes the owned GlobalFog (runtime find: zero); on
  recreates one component and camera preset.
- 18:18:09, lines 3373–3374: opening native settings suspends the preset; screenshot
  12 shows the unchanged disabled Jason settings. A deliberate native edit to
  enabled Jason, brightness 1, was saved; closing settings reapplied Chillwave.
  Turning the mod PostFx off restored the new enabled Jason state (lines 3377/3382),
  and reopening settings confirmed brightness 1. The test then restored disabled
  Jason, brightness 0 through the native UI; line 3393 confirms the preset resumes
  against that baseline. No test-only native change remains.
- Regular North Gate extraction returned to the menu; runtime find: zero GlobalFog.
  Subsequent debug day raid: lines 3526/3550/3786 confirm 12:00/Clear and inactive
  neon; inspector finds zero GlobalFog, GradingPostFX disabled with Jason filter.
  Screenshot 13 documents that day view. Both raids ended through regular extracts.
- Through line 3899: zero ArenaMapPort warnings/errors. One Unity `MenuScreen`
  inactive-coroutine error occurred when opening settings (line 3372); settings
  and all restoration tests still completed. No cause is attributed to this change.

Evidence is ignored game/test data:
`analysis/screenshots/equator-night-haze-2026-10-01/` (PNG plus capture metadata),
`work/equator-night-haze/native-log.json`, build/test/deploy logs in the same work
folder. Earlier screenshots 01–08 cover the rejected TOD trial only.

Limits: acceptance covers the tested GPU-2 graphics configuration and observed
views, not every DLSS/SSAA/optic/thermal combination. This native opaque fog pass
does not add full participating-media scattering to every transparent material.
Arbitrary camera replacement and exception cleanup were source-reviewed; natural
raid teardown and subsequent camera creation were exercised live.

## Build, configuration and delivery

Release client: zero warnings/errors. Existing Release policy suite: **443 passed**.
Distribution validator: 14 registered maps, 42 bundles, 14 sidecars, unique bundle
names. No map assets or bundles were rebuilt for this change.

Final host deployment used `tools/deploy-to-spt.ps1 -SptRoot D:/SPT41 -Confirm`;
the accepted build was promoted to `D:/SPT41vanilla` with the same sanctioned script.
Each deployment copies 2,239 owned files and never edits config files. All **796**
pre-existing host config assignments were preserved; only the two new keys were
added by BepInEx. The vanilla config SHA256 remains
`603A82FD0C45F61DC3744EC354EF6CB7792C25FF61B3CD6F55A06AA84B1001C7`.
The user must restart their vanilla EFT process to load the new client DLL.
