# Tonemap sliders, normal bloom and Equator comparisons

## 2026-09-29 — implementation and native comparison

Requested: sliders for the existing tone vectors, bloom defaults of intensity
0.35 / threshold 2.1, and visual exploration of Equator Day/Clear from the center.
Based on `652755e`; no Unity bundle rebuild or renderer changes.

### UI and defaults

- Existing Vector3 keys remain; each uses three X/Y/Z sliders plus precise number
  boxes. Slider response is signed cubic, covering [-16,16] with finer travel near
  zero. Numeric entry supports invariant/current-culture decimals and finite
  values in the same range. No rounding or normalization on repaint.
- Invalid intermediate text remains local; losing focus returns to the last
  accepted value. Valid prefixes can apply while typing (typing `17` accepts `1`
  first, then rejects `17`), consistent with live editing.
- `Use <profile> preset` copies BOTH native vectors and enables the custom toggle.
  Original/Off disable that button and ignore custom vectors. Standard per-entry
  Reset retains the original config defaults, which describe RomB.
- Corrected the metadata class name to `ConfigurationManagerAttributes`: the
  installed ConfigurationManager checks that exact name before reading its
  public fields. The formerly intended Advanced flag now works; enable the F12
  Advanced filter to show the vectors and custom toggle.
- Equator/Bay5 bloom defaults now explicitly use 0.35 / 2.1. Hidden
  `BloomDefaultsRevision=1` migrates exact old defaults once, before subscriptions.
  It preserves independently tuned values and leaves the other maps unchanged.
  A deliberate native `-1` choice remains available after migration.

Native profile constants and the existing render-stage/restore behavior are
unchanged. No new scalar config keys duplicate the vector state. No dependency
on the ConfigurationManager assembly is introduced.

### Build and first GPU acceptance

Release build passed without warnings/errors. Distribution validation passed:
14 maps, 42 bundles, 14 sidecars and staged/built assemblies agree. Client SHA256:
`D7B2605649934A234132ABFFBBAFAED12F2D620A94A0C570AABECA8607BDC278`.

GPU-2 instance `c5b90dcb`, started 20:25:01 CEST. Local, host-disk and loaded
identities matched for all 24 maschine plugins before testing. Both released
maps reported bloom defaults/values 0.35 / 2.1 and revision 1. F7 confirmed actual
UltimateBloom fields on Equator (#3277, 20:28:22).

F12 search `Tonemap`, Advanced enabled: twelve sliders visible (six per map).
Each of Equator's six sliders was clicked and changed its correct vector axis.
Filmic preset copied `(6.2,0.5,1.7)` / `(0.004,0.06,0)` and enabled custom.
Exact numeric `1.2345` persisted; `17` was rejected, leaving the accepted prefix
`1`. Both standard Reset buttons restored the original vectors. During the raid,
a primary-X slider click wrote 1.573054; F7 observed that exact material value
with the other coefficients unchanged (#3422–3427, 20:33:20). Restored the selected
recommendation afterward. No plugin warning or failed-draw/GUI error occurred.

### Screenshot comparison

Unmodified 1920x1080 Windows-MCP screenshots are retained locally under
`analysis/screenshots/hdr-tuning-2026-09-29/`, with a German `VERGLEICH.md` index.
Each capture after the scouting image has a JSON record of actual UiBridge
settings/player values. Images and raw evidence remain ignored, not committed.

Images 01–12 use position `(0,0.1,14)`, yaw 180, pitch -10; Equator starts at
12:00 (NightSun logs confirm startHour=12) and WeatherTestOverride applied Clear
(#3126). The framing approximates the user's reference; the host resolution/FOV,
visible knife, procedural grain, light flicker and late raid timer differ.
Image 00 is a scouting view from a different position, not a direct comparison.

| Images | Comparison |
|---|---|
| 01–05 | Normal bloom, otherwise reset settings: Original, Filmic, ACES, RomB, Off. |
| 06–07 | Original with stronger lamps, reduced ambient, then deeper shadows/-0.5 EV. |
| 08–09 | ACES with range 1.15, then custom primary X=3. |
| 10 | Custom Filmic between native camera and reset preset; moderate light/ambient. |
| 11 | Stronger sun and tighter exposure range; no clear advantage at this viewpoint. |
| 12 | Selected Filmic recommendation, original flare length/threshold, intensity 0.18. |
| 13 | Same recommendation checked in the darker TECHLIGHT side area. |

The recommendation favors readable shadow detail and preserved roof/concrete
texture with a restrained sign/lamp glow. This is a reasoned visual preference,
not a monitor calibration or an objective aesthetic ranking. Screenshots cover
Day/Clear only; they do not establish a night/rain preset.

Selected Equator values: Filmic + custom; primary `(7,0.35,0.7)`, secondary
`(-0.02,0.18,0)`; AmbientIntensity 0.5; GlobalLightIntensityMultiplier 0.6;
AnamorphicFlareIntensity 0.18. Bloom is 0.35/2.1; exposure remains 0/1/1,
sun 0.2, white blend 0.1, flare length 3.5 / threshold 0.155. All other lighting
entries retain reset defaults. These artistic choices are kept on GPU-2 and
documented for review, not made new source defaults. Only bloom defaults change.

At the end of the first comparison raid, zero ArenaMapPort warnings/errors and
zero global errors were recorded. Existing Unity LOD/CopyTexture warnings occurred
during map loading. Test-only godmode ended with raid teardown; no bots were used.

### Restart and deployment closure

Restarted into GPU-2 `7e235171`, started 20:37:59 CEST. All 24 maschine plugins
still matched local/disk/loaded identities. The complete selected Equator tuning
survived. A temporary Bay5 BloomIntensity=-1 survived with revision 1, proving
the migration did not run again; restored it through UiBridge to 0.35 afterward.
The host remains at MainMenu, no raid or test hook active.

Both sanctioned deployment helpers copied 2239 files. An independent SHA256
comparison matched every staged file against both `D:\SPT41` and
`D:\SPT41vanilla`. No installed config was edited directly. The vanilla install
receives the new client/default migration on its next start; the artistic GPU-2
preset is recorded for the user rather than imposed on its saved settings.

Scope of checks: Release compilation, distribution/package hashes and the native
UI/scene tests above. Server/pipeline test suites were not rerun for these client
UI/default changes; the previous milestone's results remain historical.
