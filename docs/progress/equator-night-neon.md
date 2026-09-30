# Equator night/rain neon experiment — 2026-09-30

## Scope and result

User-requested visual experiment on SPT-GPU-2 using existing live ArenaMapPort
controls and EFT's built-in PostFX. No runtime code, assets or default changes.
No build or deployment was needed. The accepted settings were restored afterward.

Cool cyan architecture, saturated shop signs and glossy floors work well. The
vivid variant adds pink horizontal flares; it is attractive from the central
comparison position, but close-up Equator letters produce excessive streaks.
The balanced variant reduces bloom and almost removes those streaks, preserving
more material detail. Neither variant adds new neon signs or recolors individual
lights. Dark foregrounds remain dark; this is a night treatment, not a visibility aid.

## Reproducible settings

Start a normal PMC raid on Equator at **00:00 / PAST**, with test weather **Rain**.
The current debug buttons select the daytime variant, so they were not used.
Use the accepted custom Filmic curves `(7, 0.35, 0.7)` and `(-0.02, 0.18, 0)`.
Keep `NightSunMultiplier=0`, `IndoorReflectionStrength=0.95` and other defaults.

`Lighting (arena_equator)`:

| Setting | Original | Vivid | Balanced |
|---|---:|---:|---:|
| AmbientIntensity | 0.5 | 0.3 | 0.3 |
| GlobalLightIntensityMultiplier | 0.5 | 0.8 | 0.8 |
| ExposureCompensationEV | 0 | 0.3 | 0.3 |
| ReflectionIntensity | 0.85 | 1 | 1 |
| IndoorFloorSmoothness | 0.5 | 0.8 | 0.8 |
| BloomIntensity | 0.35 | 0.6 | 0.5 |
| BloomThreshold | 2.1 | 1.6 | 1.8 |
| AnamorphicFlareIntensity | 0.4 | 0.4 | 0.3 |
| AnamorphicFlareLength | 3.5 | 4.2 | 4.2 |
| AnamorphicFlareThreshold | 0.155 | 0.155 | 0.17 |
| AnamorphicFlareTint (RGBA hex) | ABDCFFBF | FF268FCC | FF268FCC |
| NativeMultiFlareIntensity | 0.5 | 0.6 | 0.6 |

`LightingLab (arena_equator)`: `AmbientOverride=Custom`,
`AmbientCustomColor=02131AFF` for both variants. Original: `Off`, `808080FF`.

EFT PostFX for both: enabled, **Chillwave**, filter intensity **50**, saturation
**35**, colorfulness **45**; brightness, clarity, both sharpen controls and
colorblind intensity remain zero, colorblind mode None. PostFX is global, not
scoped to Equator. Original PostFX was disabled with Jason/50 and other values zero.

Local screenshots and full configuration/player metadata are in the ignored
`analysis/screenshots/equator-neon-2026-09-30/` folder. Each numbered PNG has a
JSON sidecar. PostFX metadata was recorded separately from the native UI.
`neon-vivid-recipe.json` and `neon-balanced-recipe.json` contain manual reference
recipes; they are not installed preset files.

| Capture | Purpose |
|---|---|
| `00-original-menu-settings` | Original mod configuration before tuning |
| `01-night-rain-baseline` | Original night/rain image, central comparison position |
| `02-postfx-original-settings` | Original disabled PostFX (mod tuning already started) |
| `03-cyan-chillwave` | Rejected: excessively strong cyan flare |
| `04-magenta-blue` | Purple ambient / pink-flare intermediate variant |
| `05-teal-magenta` | Vivid center view |
| `06-teal-magenta-side` | Vivid side view |
| `07-teal-magenta-reverse` | Vivid close-up: excessive letter flares |
| `08-teal-magenta-techlight` | Vivid TECHLIGHT view |
| `09-neon-balanced-center`, `10-neon-balanced-reverse` | Intermediate reduction, flare threshold 0.2 |
| `11-neon-final-reverse`, `12-neon-final-center`, `13-neon-final-techlight` | Final balanced variant, threshold 0.17 |
| `14-neon-postfx-settings` | Native UI readback of the experimental PostFX values |
| `15-restored-techlight` | Restored image controls in the same raid |
| `16-restored-postfx-settings` | Saved original PostFX, reopened after extraction |

Center: position `(0, 0.1, 14)`, yaw 180, pitch -10. Reverse: same position,
yaw 0, pitch 0. TECHLIGHT: `(-2, 0.1, 36)`, yaw 87.6, pitch 1.6.
Images are unedited native 1920x1080 captures. Automatic exposure and rain vary
over time; these are visual comparisons, not a fixed-frame rendering benchmark.

## Host evidence and cleanup

- 2026-09-30, 06:14–06:30 CEST: SPT-GPU-2 instance `9440e5bf`.
  All 24 maschine plugins matched VM build, host disk and loaded process.
  ArenaMapPort client SHA256:
  `4A5238EA96D2770737340D939ABA8F37F12236C46AED642CBEF9066DD18B2D7F`.
- `#2870 06:18:02.594`: `selected=PAST start=00:00 fixedMenu=True`.
  `#2871 06:18:02.600` and `#3137 06:18:14.055`: applied `Rain`.
  `#2895–2896 06:18:03.663`: both fake-sun lights factor/target/live zero.
  Falling rain is visible in the atrium captures. No new rain-cover validation claimed.
- Bot amount None, bosses disabled. No godmode/fire hooks. Positions/view changed
  through UiBridge. An independent visual review agreed that close-up vivid flares
  were too strong and that the TECHLIGHT cyan/red contrast retained good readability.
- Regular North Gate extraction before raid expiry. Returned to MainMenu,
  `inRaid=false`; local menu profile reported 440/440 HP, energy 68, hydration 74.
- All **76 originally captured config entries** match after restoration. Original
  weather Clear restored for subsequent raids. Four audio entries first bound
  during raid load were excluded from the recipe; no audio tuning was performed.
  Native PostFX saved, reopened and verified disabled, Jason/50, other values zero.
  See local `restore-verification.json` and capture 16.
- No ArenaMapPort warnings/errors since raid setup through log sequence 3428.
  The host remains at MainMenu; no diagnostic raid remains active.

The only persistent project changes are this record and its documentation links.
Any future automatic night-neon preset should scope/restore PostFX explicitly,
or implement map-local color grading, before offering it as a normal user option.
