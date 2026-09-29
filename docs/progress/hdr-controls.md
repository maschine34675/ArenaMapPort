# HDR image and Equator flare controls

## 2026-09-29 — implementation

User requested all proposed HDR controls. Branch `feature/hdr-image-controls`,
based on `c23fea6`; existing optional Arena audio/UI exports remain part of the
package. This is internal image processing, not HDR display output.

### Controls

Both `Lighting (arena_equator)` and `Lighting (arena_bay5)` expose:

| Key | Default | Range / choices | Meaning |
|---|---|---|---|
| ExposureCompensationEV | 0 | -4 to +4 | Multiply the current native middle-grey target by 2^EV. |
| ExposureRangeMultiplier | 1 | 0.25 to 2 | Scale both native limits about zero, preserving their signs. |
| ExposureSpeedMultiplier | 1 | 0.1 to 4 | Multiply current native image-adaptation speed. |
| TonemapProfile | Original | Original, Filmic, RomB, ACES, Off | Native tonemapping selection and matching parameters. |
| TonemapCustomCurve | false | advanced toggle | Use the two custom vectors with an explicit enabled profile. |
| TonemapCurve | (-1, 2.72, 0.15) | advanced Vector3 | Raw native primary X/Y/Z parameters. |
| TonemapSecondaryCurve | (0, 0, 0) | advanced Vector3 | Raw native secondary X/Y/Z parameters. |

Original preserves native enable state, enum and both vectors exactly. It does
not replace them with the RomB reset preset. Custom vectors are ignored for
Original and Off. Disable the custom toggle to return to the selected profile's
native reset values. Both custom defaults describe RomB, regardless of which
explicit profile is selected later.

Equator additionally exposes `AnamorphicFlareLength=3.5` (0–10),
`AnamorphicFlareThreshold=0.155` (0–0.99), and `AnamorphicFlareTint` with the old
RGBA `(0.670588, 0.862745, 1, 0.75)`. Existing flare enable/HDR/intensity controls
stay unchanged. Tint alpha also multiplies intensity; zero length is not the
same as disabling the flare. BepInEx's default color round-trip is recognized
so reloading its 8-bit RGBA serialization retains the exact former constants.

### Native behavior and isolation

`HdrImageTuning` patches installed `PrismEffects.OnRenderImageInternal` and
`UpdateShaderValues`, checking exact signatures through `HarmonyPatches.Verify`.
EFT writes `EnvironmentManager.PrismExposureOffset/Speed` immediately before the
shader update. The new prefix captures these fresh values, stages the selected
adjustment, and restores component fields after their material upload. Rendering
continues with the uploaded material values. Native exposure enable state stays
untouched. IndoorTrigger and EnvironmentManager interpolation continue normally.

This placement prevents a static camera-creation snapshot from overriding later
indoor/outdoor changes and prevents cumulative multiplication on repeated frames.
Neutral settings take no-op branches. No environment, trigger, shared preset,
exposure-master relationship or optic effect is modified.

Only the enabled primary Prism component on CameraManager's exact current Game
camera is enrolled, with a MainPlayer on Equator/Bay5. Child/master-dependent
Prism components are excluded. Camera replacement and map exit release owned
state. Native exceptions propagate; finalizers provide cleanup. Field restoration
preserves distinct intervening writes from another patch.

Native shader update clears local keyword sets before selecting the tone mode.
Selected material uniforms/keywords remain until the render finishes. If the
new control changed the tone mode, its three global keywords are then restored
to the native state this camera would have produced. A nested owned render
retains the active outer transaction's selection. No generic exposure keywords
are patched.

Normal frames reuse a pooled scope and avoid diagnostic material readbacks.
Sampling occurs at 2 Hz and following a setting change. F7 reports current
component fields, captured native versus effective values, observed material
uniforms, actual local/global tone-keyword masks and sample age. It distinguishes
restored fields from the effective rendered values.

### Source evidence and bounds

Installed EFT 0.16.9.5 `Assembly-CSharp.dll` SHA256:
`EE25CEE1259777B38ED8B3E7841FDC2DB3C98540B1469FA539B1FF183476E436`.
Local decompile at `D:/SPT41/Development/_decompiled/PrismEffects.cs`:

- 549–565: exact native profile vectors (table below).
- 1069–1071: local shader-keyword reset.
- 1171–1175: six tone-vector components uploaded.
- 1201–1214: exposure material values and enable keyword.
- 1274–1300: local/global tone selection.
- 1553–1590: luminance/adaptation passes consume materials.
- 1642–1648: environment overwrite followed by shader upload.
- 1766–1785: later native exposure enable/master branch, preserved by this change.

| Native reset profile | Primary vector | Secondary vector |
|---|---|---|
| Filmic | (6.2, 0.5, 1.7) | (0.004, 0.06, 0) |
| RomB | (-1, 2.72, 0.15) | (0, 0, 0) |
| ACES | (2.51, 0.03, 2.43) | (0.59, 0.14, 0) |

Constructor secondary values differ from the RomB reset. Original therefore
preserves the actual camera values. The inspected C# does not establish native
shader equations: raw curve controls are not labelled independent contrast,
shadow or highlight sliders. All six coefficients must be finite and within
[-16,16]; invalid inputs retain the selected preset. In-range custom combinations
can still give extreme output. Original is the simple recovery path.

Exposure interval units are not called EV; only the relative target multiplier
has a stop-based definition. Non-neutral results are finite and bounded, with
ordered exposure endpoints. Invalid scalar settings fall back to neutral values
without rewriting preferences. The flare threshold stays below 1 because native
`BloomAndFlares` calculates `1 / (1 - threshold)`.

### Automated checks

397 .NET server tests passed; the test project reported its four existing
nullability warnings. Python pipeline: 639 tests, 28 expected skips. PowerShell
Unity log-scan tests: eight cases passed. No Unity bundle rebuild was required.

### Initial native comparison and range correction

GPU-2 instance `2b23b1c5`, 18:54–19:01 CEST, loaded candidate SHA256
`C080A60477AE3F143CF835A93A8F726292256F4AF0FD237B9C0FA352D6A81808`.
Loaded/host/local identities matched before the raid. A preceding startup had
loaded a stale file during synchronization and was restarted before acceptance.

At Equator Techlight `(-2,0.1,36)`, looking at `(9.9,1.2,36.5)`, native exposure
was enabled, target 0.14, bounds [-10,0.9], speed 4. Original used Filmic with
camera-authored vectors `(7,0.3,0.3)` / `(-0.05,0.25,0)`. The neutral image and
all native/effective/uploaded values agreed (18:54:41, #3276–3281).

- EV +1 uploaded 0.28, EV -4 uploaded 0.00875; the latter visibly darkened the
  scene. +1 made little visible difference at this view, consistent with the
  native exposure cap; raising the cap later visibly brightened it. Returning to 0
  recovered the original image. Native fields stayed at 0.14 throughout.
- Speed 0.1 uploaded 0.4; speed 4 uploaded 16; native speed remained 4. These
  establish live shader consumption and restoration, not measured adaptation
  half-times. The native adaptation pass was source-reviewed separately.
- An initial midpoint-based range formula produced [-5.9125,-3.1875] at 0.25
  and an unusably black image (#3391–3396). The final implementation scales
  about zero instead, preserving endpoint signs. The final-build retest is
  recorded below; the midpoint candidate is not the shipped behavior.
- Filmic, ACES, RomB and Off each selected the expected material keyword mask
  (1,4,2,0) and native profile constants. After render, the observed global
  mask returned to the camera's native Filmic mask 1. Custom ACES primary X=1
  visibly altered output; X=17 triggered preset fallback with guardedInput=true
  (#3565–3570). Original ignored the enabled invalid custom input and restored
  both camera vectors and its image exactly (#3593–3598).
- At the atrium `(1.5,0.4,12)`, yaw 180, pitch -8: flare length 1 versus 7
  visibly shortened/lengthened the streak. Saturated-red tint changed its color;
  threshold 0.99 removed it and 0 strengthened it. Alpha 0 removed the streak
  with the pass still enabled. Disable/re-enable restored and re-enrolled the
  camera. All new flare defaults and existing intensity 0.25 were restored.

The initial candidate produced no ArenaMapPort warnings/errors. Filtered
evidence is retained locally in `work/hdr-controls/gpu-candidate-2b23b1c5.log`;
native screenshots were reviewed in the task. Tonemap/flare code is unchanged
by the range correction.

### Final native acceptance

Final client SHA256:
`F1B6CCB57A16BF36CDAC96C8525792A88CC84452DDECA4469FD536A244ECA878`.
GPU-2 instance `9c899e03`, started 19:03:09 CEST; local/host/loaded identity
verified before testing. Equator -> Bay5 -> hideout -> Equator, 19:04–19:11.

- Equator at the same Techlight view: range 0.25 uploaded [-2.5,0.225], darkening
  the scene while preserving visible output (#3272–3277); range 2 uploaded
  [-20,1.8] and visibly brightened it (#3301–3306). Native bounds stayed
  [-10,0.9]. Combined EV +1 / range 2 / speed 2 / ACES applied 0.28 / [-20,1.8]
  / 8 with material tone mask 4, after-render native global mask 1 (#3331–3336).
- Those non-neutral Equator values remained selected when Bay5 loaded.
  Bay5's independent neutral settings produced native/effective target 0.23,
  speed 2, bounds [-10,0.9], original Filmic vectors (#4066–4071).
- Bay5 at `(55,-3.9,6)`, looking at `(61,-2.9,9.5)`: range 0.25 visibly darkened
  output with [-2.5,0.225] (#4090–4095). EV +1 / range 2 / speed 0.1 applied
  0.46 / [-20,1.8] / 0.2 and visibly brightened output (#4115–4120).
- Moving to the brick room `(90.91,-2.9,74.24)` showed EFT's native transition
  in progress, then target 0.14 and speed 4. EV +1 / speed 2 applied 0.28 / 8;
  custom ACES vectors reached the materials and visibly changed the image
  (#4168–4173). Back outside at `(94.6,-3.9,88.8)`, EV -1 / speed 2 applied
  0.115 / 4 from native 0.23 / 2 (#4222–4227). No accumulated scaling appeared.
- Leaving both maps' non-neutral settings selected, the hideout camera reported
  outside-port, owned=false, no transaction and no owned upload sample. Its
  native exposure stayed disabled, target 0.259, bounds [-10,1.3], speed 2,
  Filmic `(6.2,0.05,0.8)` / `(0.004,0.06,0)`, global tone mask 1 (#4274–4277).
- After resetting the new controls, a third raid loaded Equator with a different
  flare component (-1442822 versus -1261896). The prior flare constants,
  intensity 0.25 and pre-Prism ordering returned. F7 native/effective/uploaded
  exposure and tone values were identical again (#4738–4743); visual output
  remained normal. The new color's saved round-trip retained exact default
  alpha 0.750 in both fresh raid logs.

No ArenaMapPort warnings/errors occurred in the final native run. The setting
audit compared all saved assignments: host 865 existing -> 882 total (17 new),
all 865 preexisting values unchanged. Every new control was returned to its
neutral default, including both advanced vectors. Vanilla still had its 804
original assignments before deployment; installation does not overwrite config.
Filtered final evidence: `work/hdr-controls/gpu-final-9c899e03.log`.

These tests establish live control behavior, native value preservation, actual
shader uploads and camera/map isolation. They do not measure adaptation half-times,
validate every arbitrary custom curve, or claim equipped optic/NVG/thermal testing.
Those camera exclusions and cleanup exception paths were source-reviewed.

### Build and deployment closure

The final Release client build, 397 server tests, 639 pipeline tests (28 expected
skips), eight PowerShell log-scanner cases and distribution validation passed.
Distribution validation covered 14 maps, 42 bundles and 14 sidecars. No Unity
rebuild was required.

The sanctioned deployment helper copied all 2239 package files to both
`D:\SPT41` and `D:\SPT41vanilla`. A separate full-file SHA256 comparison matched
every deployed file against `dist` in both installations. Saved preferences were
preserved in both installs, including all 804 vanilla assignments. Restart EFT
in an installation that was already running to load the new client.

Final GPU-2 status: main menu, no raid, control free, and all 24 maschine plugins
identical across local build, host disk and loaded process. Deployment logs and
the saved-setting comparison are retained under `work/hdr-controls/`.
