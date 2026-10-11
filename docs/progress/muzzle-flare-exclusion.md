# Muzzle flare exclusion and player documentation

## 2026-10-11 — scope and implementation

The restored Equator `BloomAndFlares` pass sees every bright camera pixel,
including muzzle emission. Turning down its intensity also removes the desired
lamp streaks; the native vignette texture is not used by its anamorphic branch.

`MuzzleFlareMask` filters the native quarter-resolution flare seed immediately
before horizontal stretching. Its transpiler requires exactly one verified
`hollywoodFlaresMaterial` pass-2 call. It leaves the original camera image,
world lighting and the separate broad-bloom buffer intact.

- Native `MuzzleManager.Shot` tracks the selected muzzle jets for the duration
  of their emission curves. Projected bounds include particle size, random/axis
  shifts, the native axial extension and the preceding blur footprint.
- Shared native muzzle-particle systems use their live renderer bounds. Long
  particle bounds are enclosed in camera space rather than inflated to a sphere.
- The filter runs only for cameras restored by ArenaMapPort on Equator. There
  are no extra player settings, per-frame scene scans or GPU readbacks.
- GL state is restored in `finally`. Raid teardown releases the material,
  tracking lists and acceptance counters. A binding mismatch or runtime failure
  leaves an actionable warning and disables this exclusion.

This is a screen-space exclusion: a lamp directly behind a muzzle flash can
briefly share the masked pixels. It does not suppress lighting reflected from
nearby surfaces, impact effects, or the weapon's ordinary flash, smoke and bloom.
Other mods' custom flash renderers and unusual looping emission curves are not
certified by this change.

## Verification

- Independent source review checked the native `MuzzleManager`, `MuzzleJet`,
  `BloomAndFlares` and camera relocation paths. Its axial-extension finding was
  fixed; the oversized spherical bounds for long particle effects were tightened.
- Release client build: zero warnings and errors. Client-only deployment uses
  the sanctioned script; server DLL, map assets and configuration stay unchanged.
- GPU-2 Equator Day firing tests exercise the native shot hook and masked render
  path. Mk17 captures show the muzzle plume without a long horizontal streak,
  while the separately positioned construction lamp retains its streak during
  firing. Ordinary muzzle lighting, smoke and weapon rendering remain visible.
- A separate run with the accepted release DLL confirms the new type is absent.
  Its AKM loadout differs from the candidate Mk17; these captures are a control
  run, not an identical-frame or same-weapon A/B measurement.
- The first candidate recorded 5,142 masked render passes without a filter
  failure. At the menu, mask material was null, both tracking lists were empty,
  and all counters had reset. No ArenaMapPort warnings/errors were observed.

Final candidate SHA256:
`b5a6919020e243fc9f0a8fa2ecf031d75d5c594f3dc31b4d2d94bd69afbb1eb1`.
The final client restart reported 19/19 matching VM/host/loaded identities.
The later compact capture status reports 18/19 globally and does not retain the
per-plugin difference; it is not used to assert that every other plugin remained
unchanged. The deployed ArenaMapPort file still matches the final build hash.
The final Mk17 test
recorded 332 masked passes, `_failed=false` and a largest individual region of
0.0075123 viewport area. Teardown again left zero counters, empty lists and no
material. The subsequent Bay5 MCX burst consumed 20 rounds while all mask state
remained empty/zero, confirming the Equator-only guard after a map transition.

The final Equator run logged one existing physics warning: no native Books
impact SoundBank for `shoe_sport_Single1_Physic`. There were no flare-filter
warnings/errors. That unrelated sound fallback was not changed here.

Local screenshots and runtime reports are under ignored `work/muzzle-flare/`.
This is ordinary-firearm coverage, not a complete catalogue of weapons, optics,
launchers or third-party visual effects.

## Player documents and release boundary

README now describes the released Equator/Bay5 variants, actual dependencies,
prefixed client folder, compact configuration, optional DynamicMaps integration
and gameplay scope. Obsolete development tuning and debug-button instructions
were removed. `FORGE.md` is a compact English description for players.

Both documents were checked against current source and all relative README
links resolve. This follow-up does not change the published v0.1.0 release ZIP.
