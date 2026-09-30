# Selective Equator neon flicker — 2026-09-30

## Scope and controls

The user requested a trial of irregular faulty lights in the existing Equator
midnight neon installation. Three authored fixtures now flicker independently:
`north-column-west` (cyan, seed 11), `techlight-right` (magenta, seed 29), and
`south-sign-left` (magenta, seed 47). The remaining nine strips/lights stay steady.
The original arcade cabinet's two native LightFlicker components are untouched.
The existing Enabled/Equator/00:00 gate owns this addition; no extra light,
shadow map, volume, mesh or texture is introduced.

New live controls in `Night neon (arena_equator)`:

| Key | Default | Range | Meaning |
|---|---:|---:|---|
| FlickerStrength | 0.8 | 0–1 | Depth of each brightness dip; 0 restores steady light, 1 allows brief complete blackouts. |
| FlickerFrequency | 1 | 0–3 | 1 schedules about one burst per 20 seconds per fixture; controls the gaps, not pulse duration. 0 restores steady light. |

## Local source and implementation

Local EFT 0.16.9.5 `EFT.Visual/LightFlicker.cs` evaluates a curve or Perlin noise
and writes Light.intensity each frame. Simply attaching it would race the owned
neon runner's intensity writes. `EmissionFlicker` also targets `_EmissionVisibility`,
which differs from the Standard strip shader's `_EmissionColor`.

Instead, one pure clock-based envelope drives each selected light and its strip.
The descriptor/sidecar stores selection and independent seeds. Seeded bursts have
short eased edges, a dimmed plateau and optionally a weaker second dip. Frequency
changes event spacing while preserving pulse lengths; four initial steady seconds
avoid a blackout on installation. There is no Unity global random state or
frame-count accumulation. The renderer receives a MaterialPropertyBlock so the
two shared source materials and the other strips are not changed.

Scene/distance/atmosphere upkeep remains at 10 Hz. Only the three envelopes,
renderer emission and corresponding native haze visibility update every frame.
`VolumetricLight.SetupSpotLight` reads the final source Light color/intensity;
`CheckIntensity` caches `NeedToPrerender`. Full-blackout transitions therefore
refresh native enabled/NeedToPrerender state in the same LateUpdate as the light.
No native buffers/materials are recreated just because a lamp flickers.

F7 starts one bounded 40-second diagnostic sample. It reads actual Light intensity,
renderer property blocks and native haze activation after LateUpdate, reports
min/max factors, dip/blackout frames and synchronization mismatches. Changes to
FlickerStrength or FlickerFrequency interrupt the sample; root disposal stops it.
This complements screenshots, which
alone cannot establish timing or synchronized transient effects.

Initial client Release build passed with zero warnings/errors. Final unit tests,
independent review, exact loaded-build GPU checks and promotion follow below.

## Build and source verification — 2026-09-30

Final client Release build: zero warnings/errors, SHA256
`2A9A157ECC72D092C64829B0463F7698FAA557DF3B8F1549C66C394B6B7EC18B`.
All **443 tests passed** (23 additional cases), covering selection/sidecar mirrors,
bounded brightness, full blackouts, initial steady time, independent phases,
60/144-FPS/skipped/reversed evaluation and unchanged pulse durations at higher
frequency. The test build reported four existing nullable warnings in BaseJsonTests
and linked ArenaMapRaid; no new flicker test warning. Distribution validation:
14 maps, 42 bundles, 14 shader sidecars.

The shared descriptor is compiled into the server too; its Release build stages
SHA256 `49CE01FEDBCADD137A347D159437A28998D963CE53396D4376D693FEC16DB48C`.
There is no server behavior or database change, and no scene bundle rebuild.
Independent client lifecycle review found no blocker, including per-frame native
haze recovery, coroutine stop/disposal order and material isolation.

GPU deployment completed through the sanctioned helper (2239 files). Both game
and server were stopped on the host. `gpu-run start --host host2 --via direct`
started them in 63 seconds; process `1ac521ec`, 13:27:34 CEST, confirmed all 24
plugins identical across VM/disk/loaded. Fresh PMC Equator 00:00/Rain, bots None,
bosses off. Existing native graphics/PostFX settings retained.

## GPU timing and synchronization — 2026-09-30, 13:29–13:35 CEST

At 13:29:34, the loaded client selected exactly the three intended fixtures.
Four complete 40-second F7 samples at the fixed north-west column viewpoint
(-7, 0.1, 22; yaw 330/pitch 0) checked actual native values after LateUpdate:

| Strength / frequency | Finish / sequences | Frames per fixture | Observed factors | Dips (south / north / TECHLIGHT) |
|---|---|---:|---|---|
| 0.8 / 1 (defaults) | 13:30:54, #3305–3307 | 4756 | 0.200–1.000 | 3 / 3 / 3 |
| 1 / 3 (blackout stress) | 13:31:43, #3348–3350 | 4731 | 0.000–1.000 | 11 / 9 / 10 |
| 0 / 3 (strength off) | 13:32:33, #3386–3388 | 4767 | 1.000–1.000 | 0 / 0 / 0 |
| 0.8 / 0 (frequency off) | 13:33:24, #3428–3430 | 4766 | 1.000–1.000 | 0 / 0 / 0 |

Every complete sample reported **maxLightError=0, maxEmissionError=0 and
hazeMismatches=0** for all three fixtures. Full-blackout frames were respectively
109 / 70 / 104 in the stress sample, with correct recovery afterward. This proves
the live state timing and agreement; it is not a GPU timestamp/frame-pacing trace.
Additional screenshot-probe samples were deliberately interrupted by live config
changes; their logs label them interrupted instead of claiming a full interval.

The quiet 300-frame samples after default and strength-off checks both showed
120 FPS, 8.3 ms mean/p95 (max 8.4 / 8.5 ms). The host remains capped at 120; this does
not establish uncapped incremental GPU cost. No lights or volumetric passes added.

Native unedited PNGs plus settings/player metadata are ignored under
`analysis/screenshots/equator-night-flicker-2026-09-30/`. `01-defaults` shows the
normal scene. Initial timed captures 02–06 missed the short blackout; these are
not evidence of its appearance. Anchoring the timing helper to the local F7 action
instead of assuming matching VM/host wall clocks produced **07-blackout-early**:
the cyan strip, its floor illumination and blue haze disappear together, while
the surrounding scene and other lighting remain. **08-blackout-late** and
**09-recovered** show recovery. This comparison uses strength 1/frequency 3;
the delivered defaults are 0.8/1.

Full Enabled=false at 13:33:59 released the root, material overrides and volumes
with zero remaining attached light buffers. Enabled=true at 13:34:54 rebuilt three
flicker owners, 12 volumes and 8 smoke banks, with elapsed time reset to 0. Defaults
were restored before regular North Gate extraction. Scene unload at 13:35:24
(#3568–3570) again released all owned effects with zero attached source buffers.

## Subsequent day raid and final state — 2026-09-30, 13:36–13:37 CEST

The following debug Equator day raid applied Clear with `nightNeonRain=False`
(13:36:18 / 13:36:23). F7 at 13:36:26, sequence 3959, reported hour 12 and zero roots,
strips and lights. No flicker owner or atmosphere was created. Protected debug
disconnect returned normally to MainMenu. Final profile copy: 440/440 HP,
energy 85/hydration 88; no active raid. Final status still reports 24/24 loaded identity.

No ArenaMapPort warnings/errors appeared in the retained process log. A Unity
menu error occurred during debug pause at 13:36:40.163 (#4041): `Coroutine couldn't
be started because the the game object 'MenuScreen' is inactive!`. This also
occurred in the preceding atmosphere task; it is recorded separately from the
effect checks, without claiming a globally error-free game.

All 794 pre-existing host config assignments match the before-task snapshot.
Only FlickerStrength=0.8 and FlickerFrequency=1 were added. Vanilla config is
byte-identical. No original graphics/PostFX option was changed. Source review
and live sample/capture evidence support the implementation; the user's own
judgment of the final rhythm remains pending.

## Final promotion — 2026-09-30

Both sanctioned deployments completed: 2239 files each to `D:/SPT41` and, after
GPU acceptance, `D:/SPT41vanilla`. Client and server SHA256 match the staged
builds above on both installations. Vanilla config still matches its pre-task
snapshot byte-for-byte after deployment. Restart EFT and the server on that
installation to load the promoted binaries. No user config file was overwritten.

Independent source and screenshot review passed; the two successful comparison
images also have identical viewpoint and strength/frequency metadata. Working
artifacts and verification transcripts remain ignored under
`work/equator-night-flicker/` and the screenshot folder above.
