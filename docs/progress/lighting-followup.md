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

Python gate on `e5fe721` (PowerShell, `PYTHONUTF8=1`): `python -m unittest discover -s tools/tests -p "test_*.py"` ran 3,121 tests, OK (68 skipped); .NET 3,294 of 3,294.

## 2026-11-12 (evening) - Iceberg: the interior shadow proxies outside any LODGroup (no patch on GPU-2, nothing changed)

Follow-up of the census note above: Iceberg (`arena_iceberg`, package `work/extract/iceberg-native-v2`, released in 0.5.0) has storey-sized
ShadowsOnly proxies that no LODGroup references. Question: can the sun reach the interior through them beyond the cascades, and is a patch
visible on GPU-2? **Result: no patch is visible on GPU-2, and Iceberg's descriptor, client and bundles are unchanged** (`DistantShadowCasterPaths`
stays empty, nothing was deployed). The reason is structural: the building's roof and facades ARE in EFT's list, so the interior shells add
nothing against a sun from above. One residue exists only in the model: low morning sun through the cafe's east glazing (08:00-10:00), which
could show for a client with a shorter cascade than GPU-2's; it is described under "Open" and is not a finding of the host check.

### The cascade distance is not a constant 55 m (read from the 0.16.9.5 decompile, confirmed on GPU-2)

`EnvironmentManager.Update` writes `QualitySettings.shadowDistance = GetLongShadowCorrectionFactor() * OutdoorShadowDistance` every frame.
`OutdoorShadowDistance = max(ShadowMinDistance 30, Graphics.Settings.ShadowDistance)` and `ShadowDistance = GetMaxShadowVisibility(ShadowsQuality)`,
which is 30, 40, 60 or 80 m for the shadow quality 0 to 3. The factor is 1 for a player inside an IndoorTrigger and outside two intervals; for an
outdoor player it rises from 1/3 to 1 over `ShadowInterval1` (5, 10) and falls from 1 to 1/3 over `ShadowInterval2` (17, 23)
(`ShadowDecreaseFactor` 3). Both maps' EnvironmentManager carry these class defaults (read on the host).

Measured on GPU-2 (shadow quality 3): Iceberg 80.0 at 15:30 outdoors and indoors, 58.67 at 08:00 outdoors (east pocket, plaza), 53.33 at
07:30 on the plaza, 80.0 at 08:00 inside the cafe and inside the east pocket's gate building (Indoor); Chop Shop 80.0 at 15:30 (pocket
Outdoor, hall Indoor). Inferred: the 55.33 of the Chop Shop record above is 80 x 0.6917, the outdoor value at 07:41 or 19:46 game time, not
the value of a 15:30 raid. A client with shadow quality 0, 1 or 2 has 30, 40 or 60 m (times the same factor outdoors, so down to 10 m).

The cascades hand over to the distant map INSIDE that nominal distance. Chop Shop control repeated at a nominal 80 m (19:23 raid, clock
15:30, from (-382.5, 1.9, 469.5), yaw 20, pitch -12, player Indoor): `Lighting (arena_autoservice)/RoofDistantShadows` false (`persist=false`,
19:24:16, `[RoofShadow] disabled; removed owned LOD groups=2`) and the banner wall, about 48.5 m away and about 33 degrees right of the view
axis, is sun-lit again; true (19:24:37, `original roof proxies registered: invalidated=1`) and it is dark. Where exactly the hand-over lies is
in EFT's deferred shader and was not read; "within the nominal distance" is therefore no proof that a surface is cascade-shadowed.

Sun: `TOD_Sky` has latitude 46, longitude 84, UTC 7.24 and the fixed cycle date 2018-08-01 on both maps. Readings of `SunDirection`:
(0.93, 0.086, -0.358) at 07:00, (0.949, 0.257, -0.18) at 08:00, (-0.409, 0.819, 0.403) at 15:30 (+x east, +z south). A plain solar model
(declination 18 degrees, solar noon 13.81 h) reproduces all three within 0.004: sunrise about 06:32, sunset about 21:05, highest elevation
62 degrees. After a clock jump by `uibridge_hook time` the light's transform eases to the new direction over about a minute
(`Components.LightTransform.forward.y` went from -0.171 to -0.250 in 65 s): wait for it before judging a screenshot.

### Offline census (`tools/validate/distant_shadow_census.py`, new; six scene files)

The census of the Chop Shop section is now a tool. It mirrors `DistantShadow.InvalidateRenderers` (picked LOD, casting, material, hierarchy
activity, mesh, bounds edge of 1 m, one record per submesh) and counts **1,485 renderers = 1,571 draw records**, exactly the
`DistantShadow._renderList.Count` 1571 read on GPU-2 in the Iceberg raids. 6,168 LODGroups; 981 casters sit in a LOD the walk does not
pick (rocks, mountains, the crane: by design); **59 are referenced by no LODGroup**, all ShadowsOnly (58 with the `shadow` shader, the tunnel box
with `Standard`), 28 with a bounds edge of 3 m or more, 19 of 5 m or more: the cafe (`Sanatorium_Cafe_second_floor` 42.7 x 4.8 x 19.5 m,
`_ground_floor` 33.6 x 4.7 x 23.3 m, `_constructions` 31.4 x 0.4 x 13.8 m, the bar, pillars, arches, columns), `Sanatorium_hallway_big_01`
33.8 x 4.8 x 21.7 m and `_02`, the library (`_second` 19.4 x 6.2 x 16.1 m, `_ground_floor`, the dome 10.4 x 4.7 x 10.4 m, railing, ladder,
bookshelf), `Sanatorium_Shower_Room_03`, `Sanatorium_Ventilation_Shaft`, and `STENCIL/STENCIL_TUNNEL` (a 15.4 x 9.7 x 10.4 m box at
(127.2, -5.3, -89.8) in the Light scene); the remaining 31 are ceiling lamps, TV and wall-mount shadow meshes, plywood boards and signs under 3 m.

Listed, in single-LOD groups whose LOD 0 the walk picks: `MAIN_BUILDING/sanatorium_roof` (98.1 x 0.6 x 99.3 m at y 13.5, over the whole
building, the dome included), the facades `sanatorium_facade_01`, `_02`, `_03`, `_05`, `_06` (each with an `_indoor` shell) and `_entrance`, the
pool room, the theatre ceilings and walls, the reception, the balcony slabs. The cafe, hallway and library shells are the storeys UNDER that roof.

### Coverage: which candidate is the first blocker of a sun ray that no listed caster stops

Method (same tool): for a sun direction everything is projected across the sun into depth buffers at 0.25 m; a candidate texel is uncovered
when it is the first blocker of its ray and the next listed caster lies at least 0.5 m behind it or nowhere. Faces count as the shaders cull
them (`shadow` by front faces, `shadow_backside` by back faces, an enrolled two-sided proxy by both). Controls: Chop Shop's outdoor hull is
3,150 of 3,288 m2 uncovered at 15:30 without its sidecar rows and nothing is uncovered with them; Equator's three floor proxies are 426 to
1,224 m2 uncovered without their rows and gone with them (two-sided). Areas are measured across the sun; rows of 1 m2 or more.

| raid clock | sun elevation | building proxies, total | of that the cafe | tunnel box |
|---|---|---|---|---|
| 07:00 | 4.8 | 20.0 m2 | 20.0 | 102.2 |
| 07:30 | 9.7 | 19.8 | 17.7 | 125.4 |
| 08:00 | 14.8 | 43.4 | 42.2 | 117.9 |
| 09:00 | 25.1 | 49.9 | 39.8 | 93.1 |
| 10:00 | 35.5 | 33.6 | 27.2 | 40.1 |
| 12:00 | 54.1 | 9.1 | 4.6 | 0 |
| 13:48 | 62.0 | 19.1 | 7.6 | 1.6 |
| 15:30 | 55.0 | 4.4 | 0 | 1.4 |
| 17:00 | 41.7 | 1.1 | 1.1 | 9.2 |
| 18:00 | 31.6 | 2.5 | 0 | 11.1 |
| 19:00 | 21.2 | 19.4 | 0 | 2.8 |
| 20:00 and later | under 11 | 0 | 0 | 0 |

For comparison the building proxies' sun-facing area is 1,400 to 2,500 m2: 96.7 % of it lies under a listed caster at 08:00 and 09:00,
98.3 % at 10:00, 99 % or more from noon to 18:00 and 98.9 % at 19:00; the largest single proxy residue from noon on is 6.5 m2 (slivers at
wall openings). A sweep over 60 directions (every 30 degrees of azimuth at 20 to 80 degrees of elevation, real or not) gives the same
picture: every storey shell at least 98 % covered on average, worst single case the cafe's second-floor shell with 39.5 m2 for a sun in the
east at 35 degrees.

The morning residue is one mechanism: the sun shines through the window bays of `sanatorium_facade_entrance` (x about 41, z -82 to -72) and
meets the cafe shell's slab tops at y 7.2 and y 11.0 between x 31 and 41, in stripes between the mullions. In the cascades those slabs stop
it. In the distant map the rays go on into the storey below; projected onto the floors (until the next listed caster): 08:00 ground floor x 5
to 24, z -74.5 to -65 (about 60 m2 of floor) and first floor x 20 to 27 (45 m2); 09:00 ground floor x 21 to 33, z -82.5 to -72 (40 m2) and
first floor x 29 to 33 (35 m2); 10:00 x 33 to 36 (25 and 19 m2). Enrolling three rows (`.../SOO_LOD0/CAFE/Sanatorium_Cafe_second_floor/
Sanatorium_Cafe_second_floor_SHADOW_LOD0`, `.../SOO_LOD0/CAFE/Sanatorium_Cafe_ground_floor/Sanatorium_Cafe_ground_floor_SHADOW_LOD0`,
`.../OO/CAFE/PROPS/Sanatorium_Cafe_constructions/Sanatorium_Cafe_constructions_SHADOW_LOD0`, all under `SBG_Arena_Sanatorium_Main`, Arena's
one-sided material: the stopping faces are slab tops) removes the cafe rows from the table in the model; what is left is at most 6.9 m2.
This is computed, not seen.

`STENCIL_TUNNEL` is no part of the building: the box fills the tunnel under the east terrace (y -10 to -0.5, the terrace is at 0.8). Low
morning sun enters it from the open east side. A ray from the plaza toward it meets the ground slab after 12.8 m (host raycast, 21 hits), from
the east pocket's parapet it is 25 to 40 m away: no sightline from the play space reaches it beyond the cascades of shadow quality 3.

### Host check on GPU-2 (2026-11-12, 19:04 to 19:45 host time; `feature/autoservice` 8dcb6c9, 16/16 identical, nothing deployed)

EFT instance f2bdd2c4 (started 17:56:52), shadow quality 3, `shadowProjection` StableFit, 4 cascades, camera FOV 50. Every raid by
`gpu-run raid --mode pmc --map iceberg --bots none --clock <t> --clock-speed 0 --weather clear`, each `warnings 0; errors 0`;
`gpu-run log --level warning --source ArenaMapPort`: 0 entries for the session. Raids are 15 minutes, so four were needed. Screenshots:
ignored `analysis/screenshots/iceberg-distant-shadow-2026-11-12/` (13 files, numbered as below).

- **15:30** (raid 19:04:41; sun (-0.409, 0.819, 0.403), shadowDistance 80.0, `_renderList.Count` 1571): cafe ground floor from
  (20, 3.2, -72), yaw 270 (01, player Indoor) and library from (-14, 2.8, -72.7), yaw 90, pitch -25 (02): lamp light only, no sun patch. The model
  has at most 3.4 m2 per proxy uncovered at this sun. Raid 19:42:39, plaza (78, 1.9, -92), Outdoor 80.0: the east facade is in shade, the cafe front
  centred (yaw 286.1, 12) and 35 degrees off the axis (yaw 257, 13) shows lamp-lit rooms, no sun patch.
- **07:30** (raid 19:15:42, clock set by hook; plaza (78, 1.9, -92), Outdoor, shadowDistance 53.33): the east facade is in the shadow of the
  eastern rocks (sun 9.7 degrees), nothing reaches the glazing (03).
- **08:00** (same raid and raid 19:28:28; Outdoor 58.67): the facade is sun-lit. Far view from (78, 1.9, -92), yaw 286.1 (04), near view from
  (58, 2.0, -86.2), 16 m in front of the facade (05), far view with the cafe front 35 degrees off the axis, yaw 257 (06): the first-floor
  room behind the glazing and the ground-floor door show the same lighting in all three (crops 10 and 11); the one diagonal light edge on the
  first-floor side wall is in the near view too, so it is cascade-drawn sun through the window. Lobby from (38, 3.2, -73), yaw 270, Indoor 80
  (07): sun on the floor behind the glazing with mullion and leaf shadows, as the cascades draw it.
- **No vantage on this host shows the modelled stripes beyond the cascades.** Inside, the CheckPoint barricades (metal shutters right behind
  the lobby) cut the sightlines short and no standpoint on the ground floor is more than 38 m from the modelled zones; the cafe's first floor
  carries no NavMesh. From the plaza the ground floor is behind shutters and frosted glass (a line from (78, -92) to the cafe at
  (14, 4.5, -73.5) ends after 38.9 m at `SBG_Arena_Sanatorium_CheckPoint/SOO/BARRICADE_10/Metal_shutter_02 (3)`) and the first floor's floor
  cannot be seen from below.

### Decision

Nothing is changed for Iceberg: no descriptor row, no client change, no deploy, and therefore no before/after, no regression raid and no
hideout run. Measured: the list count, the cascade distances, the views above. Inferred from the model: that the roof and facades make the
interior shells redundant from 10:00 on, and that the 08:00 to 10:00 stripes exist in the distant map.

### Open (the user's decision)

- The morning stripes for a shorter cascade. With shadow quality 2, 1 or 0 the cascades end at 60, 40 or 30 m indoors and at 44 to 60, 29 to
  40 or 22 to 30 m outdoors between 08:00 and 10:00, and the hand-over lies inside those numbers; the modelled zones on the cafe floors are
  then beyond the cascades from standpoints inside the cafe. Not checked: it needs GPU-2's shadow quality lowered in EFT's graphics settings
  (a host setting, not changed without the user's word). If it shows there, the three cafe rows above are the fix, with the usual host
  before/after on `Lighting (arena_iceberg)/RoofDistantShadows`, an Equator and a Chop Shop raid and the hideout.
- The `RoofShadowRepairs` class comment and older lines of this file still say "about 55 m"; read them with the rule above.
- Bay5's ground slab `Bay5_floor_SHADOW_LOD0` and Bowl's TV shadow meshes (the earlier report) were not looked at again; the tool runs on
  every accepted package.

### Tool, tests, gates

`tools/validate/distant_shadow_census.py <package> [--map-descriptor tools/maps/<stem>.json] [--two-sided] [--hours ...] [--sun x,y,z]
[--clusters] [--fail-above M2]` prints the list count to compare with the host, the unreferenced casters, and per sun the uncovered area per
candidate (with `--clusters` where the rays enter and where they meet the next listed caster). A sidecar's `distant_shadow_caster_paths`
count as listed, resolved like `RoofShadowRepairs.Find`; a path the client would not enrol is reported. About 10 s per package and two suns.
`tools/tests/test_distant_shadow_census.py` (25 tests): the LOD pick rule, the filters, the enrolment and the coverage test on synthetic
quads, the sun and cascade model against the host readings, and on the accepted packages (skipped without `work/extract`, 7 tests) Iceberg's
1,571 records, its 59 candidates, the listed roof and facades, the covered midday, the recorded morning rows as a pin, and Chop Shop and
Equator clean with their sidecar rows and defective without. No C# file changed.

Python gate (git worktree, `PYTHONUTF8=1`, `ARENAMAPPORT_WORK_DIR` and the extract overrides pointing at the main checkout's `work/`):
`python -m unittest discover -s tools/tests -p "test_*.py"` ran 3,146 tests in 946 s, 127 skipped (inputs the worktree lacks), 1 failure:
`test_score_monitors.SidecarTests.test_the_sidecar_text_is_what_the_cli_writes` on CRLF working copies of the five older monitors sidecars,
which this checkout had written before the `.gitattributes` rule arrived by fast-forward. After a fresh checkout of those five files the
module passes (`test_score_monitors` and `test_distant_shadow_census` together: 112 tests OK). .NET tests not run: no C# file changed.
