# Equator night accent lights and wet floor — 2026-10-01

## Scope and controls

The user requested more small light sources, multicolor chemlights and a wetter,
more reflective floor. These additions belong only to the enabled Equator 00:00
neon variant. F12, `Night neon (arena_equator)`:

| Setting | Default | Effect |
|---|---:|---|
| `AccentLightIntensity` | `1` | Nine decorative native chemlights and small shadowed light pools, four colors; 0 removes them. Range 0–2, live. |
| `WetFloorStrength` | `0.8` | Raises native SMap floor gloss toward 2.2, preserving higher values and texture variation. 0 uses the saved baseline. Range 0–1, live. |

The saved `IndoorFloorSmoothness=0.5` remains 0.5. The new control operates on the
verified native shader gloss field through the existing material owner, without
changing global reflection strength, exposure, ambient light or water.

## Native assets, placements and ownership

Nine authored positions are mirrored in `tools/maps/equator.accents.json` and
`ArenaNeonLayout.EquatorAccents`, with a policy test for equality. They cover the
south/north approaches, east/west lanes, cover and TECHLIGHT entrance. Colors are
cyan, magenta, amber and green. The original five chemlights and twelve neon strips
are unchanged; their flicker/volumetric-light indexing is preserved.

Runtime borrows the mesh and texture references from already-loaded
`Arena_Equator_Lighting`, exact path
`SBG_Arena_Equator_Light/OO/LIGHT/glowstick_yellow/chemlight_LOD0`.
The local Arena v5 extraction identifies mesh `e1be0e9ada8fd8ed` (346 vertices)
and material `1d7e832d8cd5ee1d` (`chemlight_d`, Standard emission/normal maps).
The source scale 1.886 produces a roughly 26 cm model lying on the floor;
its mesh bounds determine a 4 mm clearance over the source floor at Y=0.03087.
Each point light sits 10 cm above that floor, has a 3 m range and 256-pixel soft
shadows, and fades out between 20 and 30 m. No extra volumetric pass is added.

Four owned material clones retain the source albedo/normal/emission masks. The
objects have only decorative renderer/filter/light components: no colliders,
loot, pickup, `ChemLight`, culling controller, physics or AI effects. Cleanup
disables the owned root immediately and destroys its objects/materials; borrowed
mesh/textures remain owned by the loaded map. No new extracted asset is shipped
or committed. Provenance remains the user's local BSG Arena source and existing
map bundle; this implementation grants no new redistribution rights to BSG data.

## Floor lifecycle and review

The initial trial increased the existing `_Glossness` multiplier 0.5 to 0.9.
GPU-2 F10 verified the material writes, but screenshots 01/02, 06/16 and 17/18
showed little visible improvement. Temporarily disabling the rain film with
`LightingLab.RainOff` also did not resolve it (19/20; setting restored). This
trial is not the shipped floor behavior.

Read-only native D3D11 shader disassembly found that SMap's property names are
inverted: `_Specularness` (offset 240) multiplies `_SpecMap.r` into GBuffer1 alpha,
the true smoothness channel. `_Glossness` (offset 244) instead contributes to
specular RGB. Shader property descriptions confirm the opposite names. Source:
`D:/SPT41/EscapeFromTarkov_Data/globalgamemanagers.assets`, Shader path_id 19,
SubShader 0 / Pass 2 DEFERRED, fragment blob 10646, parameter blob 10526, compressed
segment 8. The disassembly was inspected in memory, not committed.

The final `NeonWetFloor` therefore adjusts `_Specularness` only for the exact
`p0/Reflective/Bumped Specular SMap` shader on floor/ground/asphalt/parquet/lounge
renderers or already-classified small floor details. The wider legacy indoor
classification includes ordinary props, which are excluded from this addition.
Existing day controls retain their historical meaning; renaming or correcting
them globally is outside this change.

The owner's immutable reflection-float originals and specular-cap suspension
remain authoritative. The formula is `base + (max(base, 2.2) - base) * strength`,
where `base = original * saved IndoorReflectionStrength`; higher values remain.
At the user's saved strength 1, dark tiles change 1.39 to 2.038 and gray tiles
1.51 to 2.062. Their specular texture still varies gloss per pixel. Off restores
1.39/1.51, including after repeated changes. `_Glossness`, reflection colors and
ground multipliers are no longer changed by the new control.
Ownership starts only with the active midnight runner and clears before restore,
including on error while the master setting is still enabled. Guarded
refreshes let the remaining neon cleanup continue on failure;
failed refreshes retry once per second without flooding the log, including while
the runner is stopped after an error.

Independent source review checked asset reuse, placement floor triangles,
shader semantics, material ownership, baseline preservation and cleanup.

## Native acceptance — SPT-GPU-2

The accent geometry was accepted in process `98ab3b5b` (19:41:22 CEST). The final
native-gloss implementation was then tested in process `dec34a68`, started
2026-10-01 20:11:03 CEST, EFT 0.16.9.5 / SPT 4.1.6, 1920x1080. UiBridge confirmed
24/24 plugins identical between local files, host disk and loaded assemblies.
Final client SHA256:
`9EBD3143B792A467488A7EE00B0B04EDFCB6A15AFE4204D69F4E594F4561A390`.
Server SHA256 (shared declarations only; no server behavior/database change):
`7A9FA9B3D065E17302596B8F4845D406AD79ABD82EFBF1B9327541C2A3515691`.

- Nine native models, nine point lights and four materials created. Independent
  visual review accepted all nine placements: screenshots 03, 05–10, 13–14.
  No visible hovering, clipping, lost caps/hooks or decal interference. The cyan
  and green west placements remain clean over the original shadow decals. Shots
  04, 11 and 12 have obstructed test cameras; the later free views replace them.
- `AccentLightIntensity=0` removed all nine models/lights and four owned materials
  (initial process lines 3383/3389, screenshot 15); restoring 1 recreated them.
  The final process again created nine/four at 20:17:54, lines 2902/2909.
- Final same-view comparisons: screenshots **22/23** (center, strength 0.8/0) and
  **24/25** (south floor, 0/0.8) visibly sharpen and extend white/red/cyan floor
  reflections. Tile joints, broken edges and rough patches remain visible. An
  independent visual reviewer confirmed the wetter appearance without a general
  brightness increase. SSR was enabled on the tested native graphics setup.
- F10 actual main-floor material comparison: final process inspections 1/2,
  `_Specularness` dark 1.39 to 2.038 and gray 1.51 to 2.062; `_Glossness` stays
  0.65/0.465, `_SpecColor` stays 0.5 and `_ReflectColor` stays 0.03. Lines
  3470/3471 record the enabled result at 20:21:12.
- Master off at 20:23:10 restores saved native disabled Jason PostFX and removes
  owned accents; F10 inspection 3 at 20:23:34, lines 3550/3551, restores the
  exact floor baseline 1.39/1.51. Re-enabling recreates the installation and
  temporary Chillwave preset. Existing distance/local haze still operate.
- Regular North Gate extraction, followed by a debug 12:00/Clear raid. Neon is
  inactive (line 3707); F10 inspection 4, lines 4113/4114, again reports 1.39/1.51.
  Screenshot 26 records the day floor. After the second regular extraction the
  runner's `_accents` is null and the host is at MainMenu, no raid.
- No ArenaMapPort warnings/errors in either test process. Exception/retry paths
  were source-reviewed; no deliberate runtime failure was injected.

Evidence remains ignored native game/test data:
`analysis/screenshots/equator-night-details-2026-10-01/` (PNG plus settings/player
metadata) and `work/equator-night-details/` (initial/final native logs, build/test,
configuration and deployment receipts). Screenshots 01–21 cover the initial
floor approach or diagnostics, not acceptance of the final wet-floor behavior.

Limits: nine small shadowed point lights are distance-faded; their uncapped GPU
cost was not benchmarked. The floor change adds no render pass. Native acceptance
covers these views and host graphics settings, not every optic/thermal/upscaler
combination. It changes reflective appearance, not puddle geometry or footsteps.

## Build, configuration and delivery

Release client: zero warnings/errors. **471 Release tests pass**, including new
night ownership, baseline restoration, bounds, floor selection, distance fade and
layout checks. Distribution validation passes: 14 maps, 42 bundles, 14 sidecars,
unique internal bundle names. No Unity bundle rebuild was needed.

All **798** pre-existing host assignments remain unchanged. Exactly the two new
keys are added at 1 and 0.8. In particular, the user's newer `DistanceHaze=0.01`,
`HazeStrength=0.2398122`, `SmokeDensity=1`, `FlickerFrequency=3`, ambient/reflection
values and `BloomThreshold=1.583568` remain. All temporary test toggles returned
to their original values. Vanilla configuration remains byte-identical to its
own initial snapshot; it receives the new defaults when the new client starts.

Delivery used only `tools/deploy-to-spt.ps1 -Confirm` for each sanctioned install,
2239 files each. Both client/server artifact hashes match dist after copying.
The receipts are `deploy-host-native-gloss.log`, `deploy-vanilla.log` and
`delivery-hashes.json` in the evidence folder. Restart vanilla
EFT to load the new client; vanilla native acceptance remains the user's test.
