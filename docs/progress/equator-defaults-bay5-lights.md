# Accepted Equator defaults and Bay5 streetlight audit

## 2026-09-29 — scope and implementation

The user accepted the current Equator image after `aa3d953` and requested it as
the actual map default. They also asked whether Bay5's streetlights illuminate
the original map. Bay5 lighting behavior is unchanged by this task.

### Equator

Fresh bindings and F12 Reset now use:

| Setting | Default |
|---|---|
| TonemapProfile / TonemapCustomCurve | Filmic / true |
| TonemapCurve | `(7, 0.35, 0.7)` |
| TonemapSecondaryCurve | `(-0.02, 0.18, 0)` |
| AmbientIntensity | 0.5 |
| ReflectionIntensity | 0.85 |
| IndoorReflectionStrength | 0.95 |
| AnamorphicFlareIntensity | 0.40 |

Existing defaults retained: global lamp multiplier 0.5, bloom 0.35 / 2.1,
indoor floor smoothness 0.5, ground factors 1, and neutral relative exposure.
The native Filmic preset button still copies the native shader preset;
the ordinary per-entry Reset uses the map's accepted custom defaults.

Saved-config migration runs before change handlers are subscribed:

- WeatherRig `DefaultsRevision=5`: exact old ambient 0.85, reflection 1 and
  indoor reflection 0.3 move individually to the new defaults.
- `AnamorphicDefaultsRevision=1`: exact old intensity 0.25 moves to 0.4.
- `HdrDefaultsRevision=1`: only the complete old tuple Original / false /
  `(-1,2.72,0.15)` / `(0,0,0)` migrates. Any customized tuple, including only
  one changed coefficient, is retained in full.
- After migration, selecting an old value again persists across restarts.
  A deliberately saved value equal to a previous default is indistinguishable
  from an untouched legacy value on this first migration.
- Bay5 keeps its original defaults and acquires neither new migration marker.

### Bay5 original streetlights — read-only finding

The accepted `work/extract/bay5-native-v5` scene package contains 15 streetlamp
roots: 11 `fonar_stolb` concrete-pole instances and 4 `street_lamp2` instances.
All are hierarchy-active. The seven poles under the Light scene's
`STREET_LIGHT` root are also meshes, despite the root and scene names.

Cross-checking the original Arena `level79`, `level80`, `level83` and `level84`
files with UnityPy found 157 objects in these lamp hierarchies. Their components
are exactly Transform (157), MeshFilter (83), MeshRenderer (60), LODGroup (26),
MeshCollider (45) and BallisticCollider (26). There is **no Light component,
light controller or flare script in these hierarchies**. This is an original
source observation, not just an extractor allowlist result.

The principal materials `fonar_stolb` (`d068d32023586ee5`) and `street_lamp_d`
(`da8876bd6280defb`) use ordinary `p0/Reflective/Bumped Specular`, with no
emission texture. The pole material's emission color is black; the lamp material
has no emission color. Its leftover `_EmissionPower` / `_EmissionVisibility`
properties do not establish an emissive shader path. No source MultiFlare entry
is parented to either streetlamp family (25 entries exist elsewhere on Bay5).

Do not confuse this with Bay5's other lights: the complete imported scene set
contains 189 Light components, of which 109 have an active hierarchy, enabled
component and positive authored intensity. Many serialize `Light.intensity=0`
because culling saved them dark; the actual intensity is held in
`CullingLightObject._maxLightIntensity`. That mechanism was already restored
by [item 7](two-maps-item-7-light-culling.md). `CaptureLightsFromScene` reads
that authored value and `ApplyLights` synchronizes the culling baseline.

The importer retains Light enabled state and source hierarchy activity;
`SceneAssembler.AttachLight` creates real-time lights for actual source Light
components. `LightCullingImporter` restores the serialized state after editor
callbacks. There is no missing streetlamp Light component to transfer from the
inspected hierarchies. Independent nearby point lights can still illuminate
these props; they are not evidence of a switchable light inside each pole.

Conclusion: the source supports unlit streetlamp props, with no identified
streetlamp-specific porting omission. It does **not** prove the designers intended
night play to be harder. Arena itself was not launched for an original-night
comparison. Adding working streetlamp lights would be a new authored enhancement,
which the user has not requested in this turn.

Ignored reproducible evidence: `work/bay5-streetlights-audit/audit.py`,
`source-audit.json` and `streetlamps.json`. No original asset data is committed.

## Native acceptance — SPT-GPU-2, 2026-09-29 22:48–22:52 CEST

Client SHA256 `4A5238EA96D2770737340D939ABA8F37F12236C46AED642CBEF9066DD18B2D7F`.
EFT instance `a06cb840`: all 24 maschine plugins matched VM build, host disk
and loaded code. Equator botless Day/Clear raid, central comparison view
`(0,0.1,14)`, yaw 180 / pitch -10. UiBridge confirmed the accepted values equal
the actual ConfigEntry defaults; changed-only output contains only the four
internal revision markers. Bay5 still reports Original / custom false and its
original vectors.

- F7 at 22:50:39, log #3276–3277: uploaded tone `(7,0.35,0.7)` /
  `(-0.02,0.18,0)`, Filmic mask expected/observed 1, render transaction closed.
- Native automatic exposure stays enabled, grey 0.140001, range [-10,0.9],
  speed 4. It was not replaced by fixed exposure.
- #3207: all 512 floor details inherit supporting floor settings, smoothness
  0.50 / reflection 0.95.
- #2877: native anamorphic strength 0.400, HDR on, threshold 0.155 and length
  3.50; #3121 confirms rendering before Prism. #3301 restores the camera on exit.
- No ArenaMapPort warning/error in the recorded startup/raid buffer.
- Native screenshot and full live settings/player metadata:
  `analysis/screenshots/equator-defaults-2026-09-29/equator-accepted-defaults.png`
  and adjacent JSON. Returned to MainMenu without an active raid.

Release client builds with zero warnings/errors. All 403 server tests pass
(four existing nullable warnings in test-linked sources). Distribution validation
passes for 14 maps / 42 bundles / 14 sidecars. No Unity rebuild was required.
The guarded legacy migration branches were source-reviewed; the native upgrade
specifically verifies preservation of the already accepted saved tuning, not a
fresh installation or every legacy tuple.

## Deployment and settings preservation

Both sanctioned helpers completed with 2239 files, first `D:\SPT41`, then
`D:\SPT41vanilla` after native acceptance. No installed config was edited by hand.
The host config comparison contains 784 previous assignments, 786 afterwards:
only `DefaultsRevision` changes 4 to 5, and the two new hidden markers equal 1.
All previous user-facing assignments, including every Bay5 setting, remain intact.
The vanilla config remains byte-identical with 804 assignments until the user
starts EFT and its own guarded migration runs. Already running user EFT instances
need a restart for the new client/default metadata.

Independent SHA256 verification matched all 2239 staged files in each installation.
