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

## 2026-11-12 - Chop Shop: sun through the hall roof beyond the cascades

User report (2026-11-12): on Chop Shop (`arena_autoservice`) "a strange round shadow on the walls that moves with the view direction, grows and
shrinks, on all four walls" (screenshot `D:/SPT41/Development/Screenshot 2026-11-12 164520.png`, right half). Measured by the lead on GPU-2 the same
day (raid clock 15:30, weather clear): `QualitySettings.shadowDistance` 55.33, `shadowProjection` StableFit, 4 cascades; TOD sun direction
(-0.409, 0.819, 0.403), intensity 0.8, soft shadows; EFT's `FPS Camera/DistantShadow` enabled (`_renderList.Count` 1082, `_objectsToRenderCount` 332). The
two hall proxies `.../Arena_AutoService_OUTDOOR/AutoService_Building_OUTDOOR_SHADOW_LOD0` and `.../Arena_AutoService_INDOOR/AutoService_Building_INDOOR_SHADOW_LOD0`
are active ShadowsOnly renderers with the `shadow` shader, their parents carry no LODGroup, and a sun-lit patch with a curved edge shows on the far east
wall from (-382.5, 1.9, 469.5), yaw 20, pitch -12.

### Mechanism (read from `D:/SPT41/Development/_decompiled/DistantShadow.cs`, 0.16.9.5)

`DistantShadow.InvalidateRenderers()` builds `_renderList` from `FindObjectsOfTypeAll<LODGroup>()` only. Per group it takes the LOD array, skips the LAST
LOD (`num = Length - 1`, then `num = max(0, num - 1)`), and walks down from there to the first LOD that has at least one renderer with
`shadowCastingMode != Off`; of that LOD's renderers it keeps those that are not null, cast (not Off), have a `sharedMaterial`, are `activeInHierarchy`, have a
MeshFilter with a mesh and a `bounds.size` maximum edge of at least `ObjectSizeFilter` (1 m), and adds one draw record per submesh. A renderer that no
LODGroup references is therefore never drawn into the distant map, and neither is a renderer that only sits in a LOD the walk does not pick. The list is
rebuilt only when both per-cycle counters are zero and `InvalidateRenderersOnNextUpdate` is set, which happens at start and after
`DistantShadowCache.Invalidate`. The distant map covers what lies beyond the Unity cascades; Unity's own cascades draw every ShadowsOnly renderer
regardless of LODGroups. The hall (58.4 x 47.7 m) is wider than the 55 m StableFit sphere, so the far walls lie where only the distant map can keep the sun out.
This is the Equator defect (see "Source diagnosis" above) in a second map. Measured: the lead's numbers above. Inferred: that the round patch is the
sphere edge of the cascade (the curved edge, its growth and shrinkage with view direction); the mechanism is consistent with every measured value, no A/B has been run.

### Offline census (package `work/extract/autoservice-native-v2`, all four scene files)

Method: world transform through the parent chain (rotation, scale, translation), hierarchy-active and enabled MeshRenderer with MeshFilter, `cast_shadows != Off`,
maximum bounds edge >= 1 m, and not a member of the LOD that `DistantShadow` would pick (rules above). Control: the same script on `equator-native-v5` returns exactly the three
`equator_floor_0N_SHADOW_LOD0` proxies the repair already lists (74 x 1.3 x 75 m, 74 x 1.0 x 71.4 m, 74 x 1.0 x 71.4 m).

- 1,214 renderers are not in a picked LOD; 1,200 of them are LOD0/LOD1/LOD3 members of groups that draw a different LOD by design. Only **14** are referenced by no LODGroup at all:
  - `AutoService_Building_OUTDOOR_SHADOW_LOD0` - ShadowsOnly, 48 vertices / 58 triangles, bounds 58.4 x 24.1 x 47.7 m, lowest corner (-402.6, -4.8, 465.4). **IN.**
  - `AutoService_Building_INDOOR_SHADOW_LOD0` - ShadowsOnly, 3,631 vertices / 5,821 triangles, bounds 58.4 x 21.6 x 47.7 m. **IN** (the lead's pair; see the geometry below for what it adds).
  - `SBG_Arena_AutoService/SOO/Background/Buildings/warehouse01/Shadow_LOD1` - 20.6 x 13.6 x 39.2 m at (-355, 3, 446), south of the hall. **OUT:** the group `warehouse01` has one LOD
    holding `Shadow_LOD0`, which the walk picks; `Shadow_LOD1` is the unused lower variant and it is background, not part of the play space.
  - `STENCIL SHADOW` (scene `Arena_AutoService_Light`, a root with a BoxCollider) - 22.7 x 4.8 x 2.9 m, 24 vertices, y -5.7..-0.9 inside the hall. **OUT:** a low stencil box that no LODGroup of Arena's data references; it is no roof or wall.
  - `vandalized_vehicle_01_closed_SHADOW_LOD3` (4.3 x 1.4 x 1.7 m, last LOD of a group), `DOORS/big_gate_metal/gate/Vishel_Plane` (On, a flat 4.4 x 4.4 m plane), and eight TV/wall-mount shadow meshes of `Arena_AutoService_DesignStuff` (0.2 to 3.3 m). **OUT:** props, none keeps sun out of the play space.
- **The pocket buildings are not in the 14**: `Autoservice_Spawn_Warehouse_Outdoor/Indoor`, `Autoservice_Office_Main_Outdoor/Indoor`, `Autoservice_Little_Office_Outdoor/Indoor` and `Autoservice_Shop_Outdoor/Indoor` each
  sit under a LODGroup whose picked LOD holds a ShadowsOnly proxy (for example Spawn_Warehouse_Indoor picks `SHADOW_LOD1`, Little_Office_Indoor `SHADOW_LOD2`, Spawn_Warehouse_Outdoor `SHADOW_LOD0`). They are already in the native list, so they need no row.
- Roof- or wall-sized casters elsewhere in the package (Background `City_construction_base_concrete`, `Building_P68`, `Ceh_Complex_6`, tribune scaffolding) all belong to groups and are drawn natively.

### Geometry of the two hall hulls (face normals from the winding, Unity's `cross(b - a, c - a)` as the front side; the shadow shader culls back faces)

| | OUTDOOR | INDOOR |
|---|---|---|
| signed volume / bbox volume | +59,200 / 67,124 (outward) | -72,126 / 60,136 (inward) |
| share of area whose normal points away from the bbox centre | 100 % | 7 % |
| faces with n.y > 0.7 (up) / n.y < -0.7 (down) | 2,784 m2 / 0 m2 (the whole roof: 58.4 x 47.7 = 2,786 m2) | 3,116 m2 / 5,688 m2 (floor up, ceiling down: 5,517 m2 at y 10..20) |
| mesh edges used by one triangle only (openings and the open bottom) | 44, at y -4.81, -1.7, 0.88, 9.7, 11.3, 11.4 | 1,763 |
| area facing the lead's sun direction | 69 % | 41 % |

The OUTDOOR hull is a one-sided outward shell with a closed roof: against a sun from above or from any side, the faces toward the sun are the front faces, so the original back-face-culling shader blocks the sun
correctly. It is also the shape that, inferred from the user seeing the patch only on the far walls, already shades the near side of the hall in the cascades. The INDOOR hull is the inverted inner shell: its ceiling faces down, so for a sun from above
those faces are back faces and the stock shader draws nothing there; it is inert against the roof sun and adds only floor and detail faces that lie under the OUTDOOR hull. Equator needed `shadow_2sided` because its soffit
faces point only down and nothing else shades them; Chop Shop's roof is covered by an outward hull.

### Decision

Enrolled both hall hulls in owned LODGroups, **without** the two-sided material (`DistantShadowTwoSided` false): the distant map then draws exactly what the cascades draw, from Arena's own shader. Measured: the geometry above. Inferred, for the host check: that the stock material
suffices (the OUTDOOR hull is the roof blocker, the INDOOR hull is redundant but harmless and matches the lead's list). If the patch persists with the key on, flip the descriptor flag and re-test; the openings in the OUTDOOR walls (44 open edges) are Arena's own and stay.
Nothing else is enrolled. Other released maps are untouched (report below).

### Implementation

`EquatorRoofShadowRepairs.cs` is now `RoofShadowRepairs.cs` (git rename): it installs for every `ArenaMapRegistry.All` map with a non-empty `DistantShadowCasterPaths`, binds
`Lighting (<key>)/RoofDistantShadows` (default true, developer-only; Equator's key, default and text are unchanged, the other maps get a generic text), keeps the owner map for deferred cleanup and logs with `[RoofShadow]`. `ArenaMapDescriptor` gains
`DistantShadowTwoSided` (Equator true, default false) and the Chop Shop paths (mirrored in `tools/maps/autoservice.json distant_shadow_caster_paths`). A path that resolves nowhere once every scene of the map is loaded is one Warning per raid
(`'<path>' not found in any loaded scene of <key>; not registered`); a path that resolves but is inactive or already under a LODGroup is skipped silently, as before. No scene-wide search outside the developer probe.

### Tests

`DistantShadowCasterTests` (7): descriptor rows (Equator three paths and two-sided, Chop Shop two paths and one-sided, all other catalog maps empty), the sidecar mirror, a real-data test on the extracted package (skips when absent: each path exists once, is
active up the chain, is an enabled ShadowsOnly MeshRenderer with a MeshFilter, has no LODGroup ancestor and is referenced by no group), source pins (descriptor-driven, one Bind with the unchanged key, default and Equator text, tag, install and probe calls, key absent from `CompactConfiguration` and
`MapConfigVisibility`, scene-wide searches only inside the developer probe, no Update or coroutine). `Bay5MapTests.DistantCeilingCastersMatchSidecar` allows the Chop Shop rows. Release client build 0 warnings; server tests 3,294 passed, 0 skipped
(baseline 3,285 + 2 skipped; the two Arena-dependent tests ran on this checkout); `validate_map_descriptors.py` OK for 14 descriptors.

### Other released maps (report only, nothing changed; same census on the accepted packages, casters referenced by no LODGroup, bounds edge >= 5 m)

- Equator (control): only the three listed floor proxies (plus `SHADOW_PLUG`, a flat On plane).
- Bay5 (`bay5-native-v5`): `Bay5_floor_SHADOW_LOD0`, 311.6 x 39.2 x 358.8 m, 50 vertices, y -43..-4: a ground-level slab, not a roof; open question whether its absence from the distant map matters.
- AirPit (`airpit-native-v10`): none beyond the known layer-0 white ShootOut `Cube (10) Group` panels (On, 14 x 3.3 x 0.2 m and two 12.7 m ones).
- Bowl (`bowl-native-v2`): none roof-sized; eight TV/wall-mount shadow meshes whose world bounds read 9 to 21 m (a parent scale, to be checked) and 40 `log_01_SHADOW_LOD0` variants of 6.5 x 0.2 x 0.2 m.
- Iceberg (`iceberg-native-v2`): the open question. Unreferenced ShadowsOnly proxies of building-interior size in `Arena_Sanatorium`: `Sanatorium_Cafe_second_floor` 42.7 x 4.8 x 19.5 m, `Sanatorium_hallway_big_01` 33.8 x 4.8 x 21.7 m,
  `Sanatorium_Cafe_ground_floor` 33.6 x 4.7 x 23.3 m, `Sanatorium_Cafe_constructions` 31.4 x 0.4 x 13.8 m (roof deck), `Sanatorium_hallway_big_02` 19.5 x 4.9 x 9.3 m, `Sanatorium_library_second_floor` 19.4 x 6.2 x 16.1 m,
  `Sanatorium_library_ground_floor` 18.7 x 5.2 x 15.4 m, `Sanatorium_library_dome` 10.4 x 4.7 x 10.4 m, `Sanatorium_Ventilation_Shaft` 3.5 x 19.2 x 1.7 m, a 15.4 x 9.7 x 10.4 m `STENCIL/STENCIL_TUNNEL` box and railing/fence meshes. Whether any of them lets sun through a ceiling
  beyond 55 m needs the same normals analysis and a host look; they are candidates, not findings.

### Host check on GPU-2 (2026-11-12, build `e5fe721`, both DLLs deployed, 16/16 identical)

Before (the previous build, raid clock 15:30, weather clear, sun direction (-0.409, 0.819, 0.403)): from (-382.5, 1.9, 469.5), yaw 20, pitch -12 the far
right of the east wall and its banner are sun-lit with a curved edge; `DistantShadow._renderList.Count` 1082.

After, same raid settings (`gpu-run raid --map "chop shop" --bots none --clock 15:30 --clock-speed 0 --weather clear`, `warnings 0; errors 0`):
`[RoofShadow] registered '.../Arena_AutoService_OUTDOOR/AutoService_Building_OUTDOOR_SHADOW_LOD0' mesh=8e145d0004cb92d8 shader=shadow bounds=(-373.39, 7.25,
489.25)/(58.41, 24.11, 47.67)` and the INDOOR line (`mesh=9abe5334daa96264 shader=shadow bounds=(-373.39, 5.99, 489.25)/(58.41, 21.60, 47.67)`);
`_renderList.Count` 1084, `_objectsToRenderCount` 884 falling to 0 within about 8 s. Same position and view: the east wall is as dark as the rest, the
banner grey, only the wall lamp's own glow is left.

Control, live: `Lighting (arena_autoservice)/RoofDistantShadows` = false (`persist=false`) logs `[RoofShadow] disabled; removed owned LOD groups=2`, the
list is 1082 again and after the rebuild the sun patch is back on the same wall; true again logs `[DistantShadowCache] map=arena_autoservice original roof
proxies registered: invalidated=1; native shadow cache rebuild queued.` and the patch goes. So the one-sided stock material is enough here
(`DistantShadowTwoSided` stays false) and the patch was the cascade sphere's edge: both inferences of the section above are confirmed. The west wall
(yaw 300) shows no patch either.

Equator regression (17:59): three `[RoofShadow] registered ... equator_floor_0N_SHADOW_LOD0 ... shader=shadow_2sided` lines with the bounds of before,
`warnings 0 from mods, 4 Unity Log` (the known `refrigerator_drink` lines). The hideout opened (6.7 s). No Warning from the mod in the session.

Not checked: the openings of the OUTDOOR hull at other sun angles, and the Iceberg interior proxies the census lists (report only).
