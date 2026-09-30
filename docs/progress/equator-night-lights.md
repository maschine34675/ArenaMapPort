# Equator night light installation — 2026-09-30

## Scope

The user wants two visual versions of Equator: realistic Tarkov-style lighting
at **12:00**, and a more cyberpunk appearance at **00:00**. This first installation
adds actual cyan and magenta light sources, with visible emissive strips, to the
existing nighttime map. It uses the existing time choice; it does not create a
second location or rebuild the map bundles.

This follows the [night/rain image experiment](equator-night-neon.md). That
experiment used global PostFX and image adjustments. This implementation instead
adds local illumination to nearby floors, walls and characters. Existing PostFX,
saved image tuning, original lamp colors and their intensity baselines are retained.

## Activation and controls

The installation is enabled by default, but activates only when the current map
is `arena_equator` and its confirmed fixed starting hour is **00:00**. Daytime,
another map, or an unknown starting hour leaves it inactive. Turning it off
removes its owned objects; turning it on during an eligible night raid recreates
them. It is an experimental artistic addition, not recovered Arena content.

Settings are grouped under **`Night neon (arena_equator)`**:

| Setting | Default | Effect |
|---|---|---|
| `Enabled` | `true` | Enables the nighttime installation. Live. |
| `LightIntensity` | `1` | Multiplies the added lights illuminating surfaces and characters; range 0–3. Live. |
| `StripBrightness` | `1` | Multiplies the visible strips' emission independently of nearby illumination; range 0–3. Live. |
| `CyanColor` | RGB `(0.02, 0.7, 1)` | Color of the atrium and side-lane strips and lights. Live; alpha ignored. |
| `MagentaColor` | RGB `(1, 0.025, 0.3)` | Color of the tower and TECHLIGHT strips and lights. Live; alpha ignored. |
| `Rain` | `true` | Requests rain for the enabled Equator 00:00 variant. Next raid. |

`Rain=true` takes priority over `Weather (test)/Weather` only for this enabled
midnight variant. For example, a saved `Clear` choice still produces rain on the
neon night variant; the saved choice itself is not rewritten. Disabling this rain
option uses the existing weather selection. Daytime and other maps continue to
use that selection as before. The existing weather override reapplies the chosen
mode when SPT supplies its later weather update.

For a first comparison, start Equator through the ordinary PMC selection and
choose **00:00**. The existing Equator debug buttons select daytime. Keep the
accepted image settings initially, then compare `Enabled` off/on from one fixed
viewpoint. Raising `StripBrightness` makes the tubes glow more; raising
`LightIntensity` increases the colored light they cast. These are separate effects.

## Installation and source geometry

The initial layout contains **12 vertical strips and 12 shadow-casting spotlights**:

| Area | Strips | Color | Height | Light range / base intensity |
|---|---:|---|---:|---|
| South Equator tower, beside the lettering | 2 | Magenta | 6.4 m | 12 m / 1.8 |
| North Equator tower, beside the lettering | 2 | Magenta | 6.4 m | 12 m / 1.8 |
| Four atrium columns | 4 | Cyan | 3.4 m | 9 m / 3.0 |
| TECHLIGHT entry, beside the shutter | 2 | Magenta | 2.5 m | 7 m / 2.4 |
| West and east side-lane columns | 2 | Cyan | 3.4 m | 9 m / 3.0 |

The reviewed authored mounts are in
[`tools/maps/equator.neon.json`](../../tools/maps/equator.neon.json), mirrored by
`ArenaNeonLayout.Equator` in `ArenaMapPort.Shared/ArenaNeonFixture.cs`. They are
world-space placements for new geometry, independent of ambiguous repeated object
names in the imported hierarchy.

Placement research used `work/extract/equator-native-v5/scenes` and its extracted
mesh binaries. The audit composed each object's complete parent translation,
rotation and scale chain before calculating world mesh bounds. For the tower
backing and TECHLIGHT facade, ray/triangle intersections checked the mounting
surface and outward normal at the bottom, middle and top of each proposed strip.
Column mounts sit just outside the actual source mesh bounds. Runtime props may
still obscure a mount or its light; that needs an in-game visual check.

The reproducible evidence remains ignored under
`work/equator-night-lights-layout/`: `audit.py`, `source-geometry.json`, `probe.py`,
and `layout-candidates.json`. No original meshes or textures are added to Git.

The source map already uses the **Standard** shader with `_EMISSION` for
`arena_equator_emissive_01` on its tower lettering. The runtime installation creates
two separate Standard materials and its own box mesh; it does not modify that
original material. Strip cross section is 5.5 cm, with HDR emission scaled by
`2.5 × StripBrightness`. The geometry has no colliders and casts no shadows.

## Runtime ownership and cost

`EquatorNightNeon` waits for the player, camera and all Equator content scenes,
after the original lights have been captured and scene shaders rebound. It owns
one `ArenaMapPort_NightNeon` root in Equator's scripts scene, one shared mesh, two
materials, and the twelve lights. Disable, scene unload and component destruction
release those resources. Configuration callbacks only signal a refresh; Unity
objects are updated from the main-thread runner.

Each light is a realtime 130-degree spotlight, offset 30 cm outward from its strip
midpoint and aimed outward/downward. Soft shadows use a **512-pixel shadow map**,
with no bounce contribution. Intensity remains full within **35 m** of the camera,
fades linearly from **35–50 m**, and disables the light at **50 m**. The visible
strips remain ordinary rendered geometry. This bounds the active light distance;
it is not yet a measured frame-time budget.

F7 includes an `[ArenaMapPort][NightNeon]` probe reporting the map, confirmed hour,
owned root/strip/light counts, currently lit count, intensity, emission and colors.
Creation and cleanup are logged separately. Weather evidence includes
`nightNeonRain=true` when the midnight rain override applies.

## Verification status before host testing

The implementation and source placement evidence have been inspected. Tests in
`NeonNightTests.cs` cover strict map/time gating, transitions between raid choices,
distance fade, bounded fixture data, and the compiled-layout/JSON mirror. Their
execution result, Release build, deployment and loaded-client identity are recorded
separately by the integration pass.

**GPU validation is pending at this point.** Required observations are nighttime
off/on views, close-up mount placement, actual colored illumination and shadows,
live controls, rain priority, resource cleanup, and a following daytime/other-map
raid without the installation. This section does not claim a visual result or
performance acceptance. Append the live evidence below after those checks.

## Initial night verification and final tuning — 2026-09-30

SPT-GPU-2 instance **`d8826b0b`** loaded the initial implementation client
(SHA256 prefix `00B6DD`). The regular PMC menu selected Equator **00:00**;
nighttime rain was active. The runtime probe confirmed **one owned root,
12 strips and 12 lights**. These observations cover the initial client with live
tuning; they do not establish that the subsequently rebuilt client is loaded.

The final visual tuning is **`LightIntensity=1.5`** and
**`StripBrightness=0.6`**, now also set as the program defaults. The earlier
settings table records the pre-test `1 / 1` defaults. Colors, `Enabled=true` and
`Rain=true` remain as documented above. Existing image tuning and native PostFX
were not changed for this installation test.

The strong trial (`LightIntensity=2`, `StripBrightness=1.5`) generated broad
magenta flare bands over the TECHLIGHT shutter. A `0.45` strip setting removed
that veil but made the tubes appear comparatively flat. The final `0.6` setting
keeps visible emission while retaining shutter detail in the close view. Actual
colored illumination is visible on the floor, barrel, nearby panels and knife;
occluding objects produce shadows. The cyan column view likewise retains
concrete and corrugated-metal detail. This is a visual evaluation, not a claim
that every mount or every shadow has been inspected.

Live checks covered `Enabled` off/on with removal and recreation, a zero light
multiplier with the strips still glowing, and zero strip emission with nearby
surfaces still lit. Temporarily changing cyan to orange recolored the installation;
cyan was then restored. A normal North Gate extraction completed at approximately
**09:59 CEST**, leaving the test raid cleanly.

Unedited native screenshots and matching configuration/player JSON are stored
under ignored `analysis/screenshots/equator-night-lights-2026-09-30/`:

| Capture | Time CEST | Evidence |
|---|---|---|
| `00-menu-before-night` | 09:43:35 | Configuration before the night raid |
| `01-neon-night-center` | 09:48:59 | Initial center view |
| `02-neon-techlight-strong`, `03-neon-techlight-close` | 09:49:57–09:50:31 | Rejected strong glow; broad flare bands |
| `04-neon-off-techlight-close` | 09:50:56 | Installation disabled at the same facade |
| `05-neon-techlight-balanced-close` | 09:51:36 | `1.5 / 0.45` intermediate tuning |
| `06-neon-column` | 09:52:34 | Side column mounting and local illumination |
| `07-neon-column-orange`, `08-neon-column-cyan` | 09:56:51–09:57:15 | Live color change and restoration; cyan view at `1.5 / 0.6` |
| `09-neon-strip-only` | 09:57:35 | `LightIntensity=0`; visible emission retained |
| `10-neon-light-only` | 09:57:46 | `StripBrightness=0`; actual illumination retained |
| `11-neon-night-final-center`, `12-neon-off-center`, `13-neon-night-final-center` | 09:58:00–09:58:27 | Final tuning and repeat off/on center comparison |
| `14-neon-techlight-final-close` | 09:58:47 | Final `1.5 / 0.6` magenta close view, without the strong trial's veil |

At the fixed center view, separate **300-frame** off/on samples both reported
approximately **120 FPS**, **8.3 ms mean** and **8.3 ms p95** frame time. The game
was capped at 120 FPS. This establishes that both samples remained at the cap;
it cannot measure the installation's incremental GPU cost or predict uncapped
performance elsewhere.

After selecting the final defaults, the Release build completed with **zero
warnings and zero errors**, **420 tests passed**, and distribution validation
passed. Final deployment, loaded identity, the following daytime/Bay5 raids and
a repeated night raid remain separate integration checks at this point. Append
their results below; the initial night evidence above does not substitute for them.

## Final client and cross-map checks — 2026-09-30

The final defaults build has client SHA256
`C024E13AB6A4F5409B0623F3380F52BD9C312787C5A1DC5CE0B0AF9659B86C4E`.
The sanctioned GPU deploy completed with 2239 files. After restarting EFT,
GPU-2 instance `7560c40a` (10:02:25 CEST) reported all 24 plugins identical
between VM build, host disk and loaded process. No bundles were rebuilt.

Equator day (12:00) retained Clear weather at both initialization and re-sync
(`#2860`, `#3124`). F7 at 10:03:46 reported `active=False`, `roots=0`,
`strips=0`, `lights=0` despite the feature being enabled. The center screenshot
`15-final-day-center` shows the realistic daytime appearance without added strips.
After returning to the menu, Bay5 also retained Clear (`#3387`, `#3823`);
F7 at 10:05:20 reported zero owned roots/strips/lights on `arena_bay5`, even
with its reported hour at zero. Capture `16-final-bay5` records that raid.
Both debug raids ended through their protected disconnect, back at MainMenu.

All **786 original host config assignments** matched the pre-task snapshot;
only the six new Night neon keys were added. This comparison includes persisted
orphan entries, not just currently bound controls. The vanilla config was
byte-for-byte unchanged before its final deployment. Existing image/PostFX
controls were not used to obtain the neon effect.

The next regular PMC Equator **00:00** raid in the same process restored exactly
one installation: `#4145/4146` at 10:09:19, 12 strips/12 lights, final `1.5 / 0.6`
values and original cyan/magenta colors. Rain was reapplied at both weather calls
(`#4121`, `#4380`). `17-final-build-night-techlight` confirms the final DLL's
close-up appearance. North Gate extraction unloaded the scene; `#4516` at
10:10:26 explicitly records release of the owned lights/materials/mesh.

Independent source review found no remaining blockers after moving the config
refresh signal to `Interlocked` and consuming it in the main-thread runner.
An independent image review preferred `1.5 / 0.6`; its requested magenta close-up
was checked in captures 14 and 17. This remains a first, localized art pass:
the center is less saturated than the nearby columns/shopfront, and the existing
global PostFX remain unchanged. User visual acceptance is still open.

For a direct night-to-day regression check in the same process, the following
Equator debug raid returned to 12:00/Clear: `#4587/#4847` weather calls and
`#4911` at 10:11:50 showing zero roots/strips/lights. This specifically checks
that night rain and lights do not survive into the next daytime raid.

Both sanctioned deployments finished successfully (2239 files each); the final
client hashes match `dist`, `D:\SPT41` and `D:\SPT41vanilla`. Vanilla's config
still matches its pre-task file byte-for-byte after deployment. Its next EFT
start creates the six new keys from the defaults. Restart the user's game and
server before testing that installation; this task restarted GPU-2, not the
user's vanilla server.

Final host state: MainMenu, no active raid, 440/440 HP (UiBridge's refreshed local
profile copy). Native Settings -> PostFX still reports `Enable PostFX=false`;
the settings screen was closed without changes. The retained ArenaMapPort log
contains no warnings/errors for the final test process (earliest retained seq 22
at the last check; startup before UiBridge recording is not covered).
