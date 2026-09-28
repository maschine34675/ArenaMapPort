# Flare strength, ceiling shadows and lighting compatibility — 2026-09-28

## Result

- Equator and Bay5 have independent live `Lighting (<map>) /
  NativeMultiFlareIntensity` sliders, range 0–1, default 0.5. Zero hides these
  lamp effects; one restores their original strength. Each frame uses the saved
  source alpha, so repeated adjustments cannot compound. Actual lamp intensity,
  camera bloom and Equator's anamorphic streak have separate controls.
- Automatic exposure is already active on both gameplay cameras. No new
  exposure override was necessary. F7 reports the live Prism state.
- Equator's reported Techlight ceiling leak is repaired with existing shadow
  geometry and native two-sided shadow materials. New live
  `Lighting (arena_equator) / RoofDistantShadows=true` allows direct comparisons.
- Bay5's second adjacent sand-filled barricade now uses its original shadow
  proxies, like its previously repaired neighbor.
- SmajlecLights remains installed but its camera writes are excluded on all
  fourteen registered ports. Ordinary locations and hideout retain its behavior.
  BrighterInteriors remains untouched; the current two ports have no active
  stencil-shadow overlays for it to alter.

No Unity bundle rebuild or source asset change was needed. Per-map target paths
are mirrored in `ArenaMapDescriptor` and the JSON sidecars, with .NET and Python
mirror checks (including a deliberately changed JSON negative control).

## Source diagnosis

### Equator

User reference: `D:/SPT41/Development/Screenshot-2026-09-28-172229.png`.
Exact native view: player `(-2,0.1,36)`, looking at `(9.9,1.2,36.5)` —
west-facing `roller_shutters_techlight_01`, blue barrel on its left.

The three `equator_floor_01/02/03_SHADOW_LOD0` meshes below
`SBG_Arena_Equator/SOO_LOD0/STRUCTURES/Floors` are active original ShadowsOnly
renderers, but have no LODGroup. Local EFT `DistantShadow.InvalidateRenderers`
enumerates LODGroups only, omitting these meshes from its direct draws.

Registration alone increased the native cache from 142 to 145 without removing
the reported stripe. Reducing only real TOD sun intensity from 1 to 0 removed it;
the artificial sun was unchanged. The ray from floor `(4,0.1,36)` toward the
observed sun direction `(.42,.81,.40)` crosses the existing floor01 soffit near
`(6.095,4.140,37.995)`, but every intersected face points downward. The source
`shadow` shader culls back faces. Native `shadow_2sided` uses Cull Off in all
four passes, including pass 0 used by DistantShadow and the ShadowCaster pass.
Changing renderer casting mode alone would not fix the direct draw pass.

The repair therefore adds owned single-level LODGroups and clones only these
three renderers' materials onto the native two-sided shader. Meshes, collision,
floor04's authored LODs and intentional atrium openings are unchanged. Disabling
removes owned groups, restores still-owned material slots, invalidates native
draw records before destroying clones, and unload explicitly releases clones.
An independent source review checked the cache/command-buffer lifecycle.

Original Arena level51, exported triangle rays and parsed source/native shaders
were read independently. Detailed ignored evidence is in
`work/lighting-shadow-research/REPORT.md`, `ray-corridor-tod.txt`,
`ray-corridor-fake.txt` and `*-shadow*-shader.json`. No BSG-derived evidence enters Git.

### Bay5

User reference: `D:/SPT41/Development/Screenshot-2026-09-28-094859.png`.
The added root is
`SBG_Arena_Bay5_Main/SOO/PROPS/Military_sandbag_barrier_03 (1)` at
`(60.445,-3.893,10.210)`, original level84 object25415. The previously repaired
unsuffixed neighbor is object25426 at `(61.625,-3.893,8.659)`.

Original Arena and v5 exports agree: visible meshes do not cast shadows, and
all three dedicated LOD0/1/2 shadow children are inactive. The existing targeted
`ShadowCasterRepairs` restores those original children through the expanded
descriptor list. Native far LOD3 still has no shadow proxy; this task does not
change far-distance authored behavior.

### Optional lighting mods

Installed SmajlecLights 1.3.0 matches the supplied reference. Its generic camera
patch adds LightingFixer; Start unconditionally calls Apply, even when Enabled
was saved false. Apply writes Prism tonemapping, exposure upper limit, vignette,
TOD scattering, sharpen and vintage. Restore can overwrite current tuning too.
The optional compatibility prefixes suppress both methods while a registered
ported scripts scene is loaded. Start/OnDestroy subscriptions remain intact,
and every call outside a port returns to the original method. No hard assembly
dependency, global disabling or persistent third-party config edits are needed.

Smajlec's default Exposure=0.3 raises the camera upper limit from 0.9 to 1.2.
It does not enable/disable automatic exposure or write middle-grey/lower/speed.
Before the guard, Equator's live upper limit was 1.2; afterward it remained 0.9.

Installed BrighterInteriors 1.1.1 only changes `AmbientLight.DrawStencilShadow`.
Both accepted v5 packages and live raids contain zero active StencilShadow
components. Equator's original seven are not yet ported; mesh names ending in
`_stencil` alone do not register this effect. Reassess this compatibility if native
stencil overlays are added later. Detailed installed DLL hashes and method
analysis: ignored `work/lighting-mod-research/findings.md`.

## GPU-2 acceptance

All times CEST on 2026-09-28, SPT 4.1.6 / EFT 0.16.9.5 / Unity 2022.3.43f1.
Both lighting mods remained installed throughout.

First candidate `AC76B8CA…`, instance `44d8cf4b`:

- 18:35–18:43: initial LOD-only experiment established cache membership but did
  not fix the exact stripe. Sun isolation and triangle winding established the
  second repair above. This candidate was not promoted to the user's vanilla install.
- 18:37–18:38: changing Smajlec Enabled false then back to its saved true value
  produced blockedRestore=1 and blockedApply=2; camera exposure remained unchanged.
- 18:46–18:48: Equator flare strength 1 -> 0 -> 0.5, same view at
  `(-4.5,0.1,38.5)` toward `(-3.006,4.413,28.805)`. Original warm halos became
  invisible then returned dimmed; lamp illumination remained present. F7 confirmed
  each multiplier and 25 lights / 31 normal + 2 noise elements.

Final client SHA256:
`23243C89490BC68FA1B7943976FFBDAB7D6DF42E47AFAC0BAA7BA511DD26A7DF`.
Restarted instance `5eec1f91`, started 18:54:34; all 47 plugin identities and
five library identities matched VM build, host disk and loaded process.

| Time | Native observation |
|---|---|
| 18:55:34 | All three roof targets registered with shader=shadow_2sided. |
| 18:56:01 | Exact Techlight view: sun stripe absent; targets=3, ownedGroups=3, materialSets=3, cache=145, one cached draw per proxy. |
| 18:56:24–31 | Live roof off: stripe returns; clones released=3/remaining=0, groups=0, original shadow materials restored, cache=142, zero cached draws per proxy. |
| 18:56:46–18:57:05 | Roof on: stripe disappears again; three groups/material sets/draws restored. Atrium view retains light and original openings. |
| 18:58:02 | Leaving Equator releases all three owned material clones. |
| 18:59:03–40 | Bay5 restores six shadow proxies across both adjacent barriers. Ground shadow now spans both halves, observed from (55,-3.9,6) toward (61,-2.9,9.5). |
| 18:59:16 | Bay5 live exposure=true, middleGrey=0.230, lower=-10, upper=0.9. |
| 19:00:39–19:01:30 | Bay5 flare strength 1 -> 0 -> 0.5 at (133,-2.9,40.5), looking toward (134.330,6.070,50.670). Same-view halos disappear and return dimmed; F7 confirms 24 lights / 96 normal + 15 noise elements. |
| 19:03:12 | Hideout camera created; entered hideout as an outside-port positive control. |
| 19:06:12 | Repeat Equator raid: blockedApply=3, allowedOutside=1, proving hideout Apply ran and exclusion resumed on reentry. Roof groups/material sets=3, cache=145; no accumulation. |
| 19:07:08 | Repeat Equator unload releases three clones, remaining=0. Back at MainMenu. |

Equator live exposure remained true, middleGrey=0.140, lower=-10, upper=0.9.
F7 on both maps reported BrighterInteriors present, activeStencilShadows=0
(all loaded and owned). Final process log contained no ArenaMapPort warnings or
errors. Filtered evidence is saved as ignored
`work/lighting-followup-{first,final}-gpu-log*.json`.

Limits: the outside-port positive control is hideout, not a visual sweep of
every ordinary map. All fourteen exclusions use the same registry-based guard;
native visual testing is Equator/Bay5 only. The slider comparisons use ordinary
unaided views; previous optic/NVG/eyewear evidence limits remain unchanged.

## Offline checks and installation

- Release client: zero warnings/errors; server tests: 397 passed (four existing
  nullable warnings).
- Pipeline: 479 tests, 28 expected skips, passed after adding the descriptor
  negative-control test. Distribution validates 14 maps / 42 bundles / 14 sidecars.
- Final candidate promoted only through `tools/deploy-to-spt.ps1`; independent
  SHA256 comparison confirms all 103 package files match both `D:/SPT41` and
  `D:/SPT41vanilla`. Client restart is required for the user's
  running EFT process. No server behavior changed in this follow-up.
- Both flare intensities end at 0.5 and RoofDistantShadows at true. The initial
  eleven non-default ArenaMapPort settings are preserved. Six additional ambient
  tuning values appeared during the session and were left as received; final live
  configuration has seventeen non-default entries. Smajlec remains Enabled=true,
  Brightness=1, Exposure=0.3, matching its starting settings.

The gate ledger and detailed command fingerprints remain ignored under
`.unlazy/lighting-followup/`.
