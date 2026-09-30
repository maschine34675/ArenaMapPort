# Equator night atmosphere — 2026-09-30

## Scope and controls

Extend the optional Equator midnight neon installation with local moving haze
and shallow drifting smoke. These are an artistic addition, not recovered Arena
effects. The existing `Enabled`/Equator/00:00 gate owns their entire lifecycle;
12:00 and other locations remain unchanged. Existing fog, PostFX, lamp values
and graphics settings are not rewritten.

Two new live controls in `Night neon (arena_equator)`:

| Control | Initial default | Range | Effect |
|---|---:|---:|---|
| `HazeStrength` | 0.12 | 0–1 | Scattering in the 12 added neon spotlights; 0 removes the components and buffers. Requires native Graphics -> Volumetric Light. |
| `SmokeDensity` | 0.65 | 0–2 | Opacity of 8 shallow particle banks at the low neon mounts; 0 immediately clears their particles. |

These are initial tuning values; append the GPU result and any final defaults
below. The smoke is decorative: it does not create a grenade, audio, damage,
collider, network object or AI smoke volume.

## Native sources and ownership

Local EFT 0.16.9.5 sources under `D:/SPT41/Development/_decompiled`:

- `VolumetricLight.cs`: `Awake`, `CheckIntensity`, `SetDefaultValues`,
  `SetDefaultLightValues`, `SetDynamicLightValues`, `SetupSpotLight`, lifecycle.
- `VolumetricLightRenderer.cs`: native graphics gate, static registration,
  camera renderer lifecycle. Existing `CameraEffectsRepairs` supplies it.

Each neon Light receives its own native `VolumetricLight` after Light type,
angle and range are set. The fields are assigned, then native cached material
and geometry values are refreshed. Existing 130-degree spots yield broad haze.
The initial recipe uses 16 samples, animated noise and scattering controlled
independently from actual light intensity. Added haze fades before the native
shadow-distance cutoff to avoid its unshadowed distant fallback. Idempotent
registration handles the camera renderer clearing the shared light list.

Native `OnDisable` clears a command buffer and unregisters; `OnDestroy` destroys
the material but does not release either private command-buffer allocation.
The owned wrapper detaches both applicable buffers, disposes both allocations
and nulls the native fields before destroying its component. It does not touch
original scene lights or the shared camera renderer.

Particle assets come from EFT's installed
`assets/content/location_objects/lootable/prefab/scontainer_crate.bundle` through
the existing `ExfilSignals.EnsureHarvested` asset loader. The `Flare_Smoke`
renderer references `CyclesSmoke32_red`; its animated atlas is 8x8 with the
authored frame-over-life curve. A new material removes the red signal tint and
uses a low neutral/cool ambient floor instead of the exfil signal's white floor.
The original material, texture, template, exfil colors and sound are untouched.
No game assets are copied into Git or added to the distribution.

Eight independent, shallow particle systems derive their placements from the
existing low neon fixtures (columns and TECHLIGHT, excluding elevated signs).
Each emits two particles/second, lives 8–11 seconds and caps at 32 particles;
all eight together cap at 256. Their horizontal drift, stretched billboard size
and fade-in/fade-out produce slow banks rather than a fast vertical signal plume.

Asset loading is polled from the existing main-thread runner. No asynchronous
continuation creates objects; discarded raid owners cannot spawn late effects.
All particle GameObjects and the cloned material are owned by the neon root.

## Verification before GPU tuning

Initial Release client build passed with zero warnings/errors. Distribution
validation passed: 14 maps, 42 bundles, 14 sidecars. Shared map/time/fade policy
is unchanged from the preceding tested implementation. GPU appearance, control
and lifecycle checks remain pending here; append their concrete evidence below.

## Initial GPU tuning — 2026-09-30, 11:15–11:21 CEST

SPT-GPU-2 process `f4eca17b` loaded the initial client SHA256
`CB0D5F7BF97D525A0B4271CFCF1CCA58F681173E4A0C08A6ACA062C3316B35A3`;
UiBridge reported all 24 plugins identical (VM/disk/loaded). The existing native
Volumetric Light setting already allowed the effect; no graphics changes were
needed. Ordinary PMC Equator 00:00/Rain, bots None and bosses off.

At 11:15:05 the log confirmed 12 native volumes, then 8 smoke banks using
`Global Fog/Alpha Blended Lighted`, 8x8 atlas, maximum 256 particles. Typical
steady count was 151–154. Setting HazeStrength=0 released all 12 native volumes;
SmokeDensity=0 cleared particles immediately. Both controls worked independently.
Full Enabled off/on disposed and recreated the whole installation without errors.

Final tuning is **HazeStrength=0.16**, **SmokeDensity=0.65**. The new haze key's
pre-test default 0.12 is superseded by 0.16 in the final build. Smoke remained at
its original trial default. The 0.30 haze trial made the near cyan facade much
brighter; 0.16 keeps darker structure and depth while showing localized scattering.
The elevated sign lights are now visibly luminous from the atrium center. Smoke
is a shallow moving bank, not an opaque map-wide fog wall.

Unedited native PNGs plus contemporaneous settings/player JSON are ignored under
`analysis/screenshots/equator-night-atmosphere-2026-09-30/`:

| Capture | State |
|---|---|
| 01-both-techlight | Initial 0.12 haze / 0.65 smoke |
| 02-smoke-only-techlight | Haze off, smoke retained |
| 03-atmosphere-off-techlight | Both effects off, neon lights still present |
| 04-haze-strong-techlight | Haze 0.30, smoke off |
| 05-cyan-strong | Haze 0.30 / smoke 1.00 |
| 06-cyan-balanced | Haze 0.16 / smoke 1.00 |
| 07-cyan-atmosphere-off | Both effects off, same column viewpoint |
| 08-cyan-final | Final 0.16 / 0.65 |
| 09-center-final | Final settings, atrium center |

At the fixed cyan-column viewpoint, separate quiet 300-frame samples measured
**120 FPS / 8.3 ms mean / 8.3 ms p95** with both effects off and with final effects
on. An earlier strong sample was 119.9 FPS. All were limited by the 120 FPS cap;
this is not a measurement of incremental GPU cost. Samples immediately after
capture/teleport/config inspection contained 30–43 ms spikes and are not used
as quiet-state performance evidence.

Regular North Gate extraction completed; 11:21:13 logged volume-buffer release
and smoke/material disposal. No ArenaMapPort warnings/errors were reported, and
the game-wide error query after sequence 3266 (tuning/toggles/extraction) was empty.
Final build/restart/next-day verification and promotion remain separate below.

## Final build and GPU acceptance — 2026-09-30, 11:27–11:33 CEST

Final Release client build passed with zero warnings/errors, SHA256
`361A61C60CE7F1369BA39778A06ACA00BF98E5849C1A0101A52AC7D1FCAA8C86`.
This includes the 0.16 default, cleanup for a partially attached native component
if Awake fails, and diagnostics for command buffers attached to owned lights.
Independent source review found no remaining blockers. Distribution validation
passed again: 14 maps, 42 bundles, 14 shader sidecars. No shared policy, server,
scene bundle or original game asset changed; server tests were not rerun for
this client-only effect addition.

The documented `gpu-run restart --host host2 --via direct --timeout 300` recipe
completed in 63 seconds. Process `c692bb08`, started 11:27:33, reported all 24
plugins identical across VM build, host disk and loaded code. A fresh ordinary
PMC Equator 00:00 raid used None bots and bosses off.

- 11:29:45, sequences 2897–2903: 12 volumes, 8 smoke banks, final defaults.
- 11:30:15, sequence 3274: 151 particles and 12 attached source-light buffers.
- 11:30:22, sequences 3303/3305: HazeStrength=0 detached all 12 buffers;
  `remainingSourceLightBuffers=0`, smoke continued independently.
- 11:30:42, sequences 3308–3316: restoring haze recreated 12; disabling the
  whole neon installation then released it with zero remaining attached buffers.
- 11:30:50, sequences 3319–3324: enabling recreated all effects successfully.
- 11:31:21, sequences 3346–3348: regular North Gate extraction and scene unload
  released the atmosphere and neon root, again with zero attached buffers.
- The subsequent debug Equator day raid applied Clear weather with
  `nightNeonRain=False` at 11:31:58. F7 at 11:32:15 (sequence 3796) confirmed
  `hour=12 roots=0 strips=0 lights=0`. No atmosphere was created in that raid.

The diagnostics measure attachment counts, not native memory allocations;
explicit disposal of both captured native buffers is also verified in source.
Final-build captures `10-final-build-cyan`, `11-final-build-cyan-later` and
`12-day-after-night` extend the same ignored screenshot folder. The two night
captures show changing smoke shapes at the fixed viewpoint without obvious
square sprite edges; they do not establish all-angle sorting or long-run quality.

No ArenaMapPort warning/error appeared in this process's retained log. The
game-wide query did include one Unity menu error on the debug raid's pause:
11:32:26.699, sequence 3826, `Coroutine couldn't be started because the the game
object 'MenuScreen' is inactive!`. No atmosphere error accompanied it and the
protected debug disconnect completed normally. It is recorded separately,
without claiming a globally error-free game or changing unrelated menu code.

Final state: MainMenu, no active raid, profile health 440/440; energy 90/100,
hydration 94/100 (UiBridge's local backend-session copy). Both new controls are
left at 0.16 / 0.65 and the existing neon Enabled setting remains true.

## Promotion and preservation — 2026-09-30

The sanctioned deployment helper completed for GPU mirror `D:/SPT41` and then,
after final host acceptance, `D:/SPT41vanilla`: 2239 files each. Both installed
client hashes match the final dist hash above; server binaries remain unchanged.
Deploy transcripts are ignored under `work/equator-night-atmosphere/`.

Comparison against the pre-task snapshots found **all 792 existing host config
assignments unchanged**, with only HazeStrength and SmokeDensity added. Vanilla
config remains byte-identical. No original graphics/PostFX settings were edited.
Vanilla needs an EFT restart to load the new client; no server change requires
a restart for this addition. User visual acceptance remains open.
