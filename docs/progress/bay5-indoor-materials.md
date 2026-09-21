# Bay5 covered building-floor reflections

## 2026-09-21 — source finding and implementation

The user reported that the approximately 30% map-wide specular cap improved Bay5, but left
some surfaces too reflective. The F10 view at 20:30:20.352 (following F9 at 20:30:17.995)
identified the Hangar floor. Offline ray reconstruction resolved the concrete slot of
`SBG_Arena_Bay5_HANGAR/SOO LOD0/Hangar/Hangar_01_LOD0`: the original material has
`_SpecColor=0.198113`, `_Specularness=4.33`, `_Glossness=6`, effective specular about 0.858.

The existing indoor classifier rejected the whole renderer because other slots used decals.
Moving that test to material slots alone would still affect outdoor concrete aprons and slab
edges: the same concrete slot includes 604.041 m² of upward floor and 207.516 m² of other faces.
All four LODs use the same floor footprint; LOD3 lacks decals and must follow the same route.
The wanted floor is 1.208 m above the mesh's foundation minimum.

### Controls and ownership

Three new per-map settings preserve all existing saved keys and their meanings:

| Setting | Default | Meaning |
|---|---|---|
| `IndoorSurfaceRepair` | Bay5 on, other maps off | Separates covered building-floor portions for material correction; live reclassification on toggle |
| `IndoorSurfaceReflectionStrength` | 0.20 | Linear multiplier of original specular color, retaining specular intensity; also scales cubemap tint |
| `IndoorSurfaceSmoothness` | 0.50 | Independent multiplier of the selected floor's gloss/smoothness |

The outer `ReflectiveSpecularCap` is applied afterwards. Its saved Bay5 value, 0.2957746,
is retained. The older `IndoorReflectionStrength` still scales both specular factors and
therefore keeps its established nonlinear meaning. Equator's new surface path defaults off.

Only active gameplay scenes participate. Large mixed shells need an eligible `p0/Reflective/`
material; decals are not eligible. Upward faces near the foundation are classified against the
existing roof-depth photo, with 2.5–60 m clearance. Walls, ceilings and ineligible material slots
retain their original channels/materials. The adapter owns render-only mesh and material copies;
collision meshes, navigation data and source assets stay unchanged. The outer cap is suspended
before any restore/reclassification, then reapplied from stable inner material inputs.

RainCover always caches the stable source mesh rather than the disposable render copy. This
prevents glass-setting rescans from keeping a mesh that a later reclassification destroys.
Cleanup destroys only owned materials, restores the original MeshFilter, and removes ownership
mapping. A missing/unreadable/malformed eligible shell fails closed.

### Native first-pass findings

Process `150d2ac7`, started 21:31:08 CEST, loaded the first candidate identically on VM, host
disk and in EFT. At 21:35:13 the original conservative whole-triangle implementation classified
all four Hangar LODs. Live on/off comparisons showed the concrete reflection responding, but
also exposed large triangle-shaped transitions where partially covered source triangles stayed
original. This candidate was **not accepted as the final visual result**.

The same run exposed an approximately eight-second reclassification stall from many individual
GPU readbacks and unnecessary vehicle candidates. The final implementation therefore restricts
the new mixed-shell path to building-sized footprints, subdivides roof boundaries, and uses a
single CPU-readable depth snapshot for each classification pass. The snapshot is disposed after
the pass. No GPU upload is required because it is never rendered; see Unity's
[ReadPixels documentation](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/Texture2D.ReadPixels.html).

Repeated glass false/true and F8 rerenders succeeded in the first candidate. South exit completed
at 21:42:40; cleanup restored 434 renderers and destroyed 551 owned material clones. These counts
belong to the superseded first candidate, not the final geometry implementation.

### Evidence boundary

The correction changes material response to the existing reflection source. It does not add a
local interior reflection renderer. Authored puddles and separate decals can remain visibly
reflective. Roof classification is a sampled approximation, not exact clipping against every
roof polygon. Final build, subdivision, artifact and native acceptance are recorded below.

## 2026-09-21 — final subdivision and native material acceptance

### Final implementation and automated checks

Branch `feature/bay5-indoor-materials`, based on accepted `9f8911e`. The final Release client
built without warnings/errors; **348/348 tests passed**, including 20 new pure-geometry cases.
They cover partial cover, a narrow roof strip missed by the old samples, internal roof gaps,
slanted boundaries, winding/area conservation, affine vertex-channel interpolation, canonical
shared-edge vertices, invalid data and bounded-work exhaustion. Independent source review found
no remaining blocking issue. These tests do not substitute for execution of Unity's mesh API.

The render-only partition discovers cover with maximum 0.75 m triangle edges and seven samples
per cell. Mixed cells refine to maximum 0.1875 m edges; their centre chooses the material.
Uniform subtrees collapse back to their parent triangles. Original channels are preserved;
derived vertices interpolate position, normal, tangent, color and every present UV0–7 channel.
No normal/tangent recalculation is performed. Original slots retain their order; covered floor
fragments use appended material slots. Source collision and navigation meshes are not changed.

Input/output fragments (65,536), nodes (131,072), samples (524,288) and depth (28) are bounded.
Exhaustion rejects the entire partition, without publishing a partial result or falling back
to whole-shell scaling. Static readable triangle meshes only are eligible. A one-component UV
channel expands to two components with a zero second value because of the managed Unity API.

### Delivered artifact

Final client SHA-256:
`8F8E79C808BDF7D1054C68180443DFE6F82E61CE4F3AD7A84BDB223AF3593CBA` (358,400 bytes).
The authorized deployment script delivered all 72 staged files; **only the client DLL changed,
71 files remained byte-identical**, and every installed hash matched staging. This preserves
the accepted server, all scene/preset/NavMesh bundles, shader sidecars and map bases. No bundle
rebuild or server change was needed. Receipts live under `D:/.unlazy/bay5-indoor-materials/`:
`distribution-before.json`, `distribution-after.json`, `distribution-delta.json`, `deploy-final.log`.

Fresh GPU process **851020e4**, started **21:53:32 CEST**, confirmed VM build = host disk = loaded
code for all 25 maschine plugins and Redeploy Core. All native evidence below uses that final
process, not the superseded first candidate.

### Bay5 automatic classification and same-view controls

At **21:56:25.698**, automatic classification selected **32 render meshes including LODs** and
**21,032 final covered triangle fragments**, at reflection 0.20 / smoothness 0.50. Together with
the legacy floor path this owned 642 renderers / 1,008 material clones. The new count represents
neither 32 buildings nor 21,032 newly created triangles.

| Hangar LOD | Selected triangle fragments | Selected source slots | Material slots before/after | Vertices before/after |
|---|---:|---:|---:|---:|
| LOD0 | 4,852 | 3 | 11 / 14 | 10,022 / 16,060 |
| LOD1 | 3,783 | 3 | 11 / 14 | 7,870 / 12,657 |
| LOD2 | 3,783 | 3 | 10 / 13 | 3,653 / 8,440 |
| LOD3 | 3,541 | 2 | 5 / 7 | 1,775 / 6,342 |

Independent knob checks on the concrete logged these uncapped values while the outer cap was
suspended for reapplication:

| Reflection multiplier | Smoothness multiplier | Effective specular | Glossness |
|---:|---:|---:|---:|
| 0.20 | 0.49 | 0.1716 | 2.94 |
| 0.20 | 0.50 | 0.1716 | 3.00 |
| 0.30 | 0.50 | 0.2573 | 3.00 |
| 0.20 | 1.00 | 0.1716 | 6.00 |
| 1.00 | 1.00 | 0.8578 | 6.00 |

The outer map-wide cap still applies afterwards. With both new multipliers at 1.00, the partitioned
mesh visually matched the original baseline at the same view; this did not reveal a new UV/normal
appearance defect. At 0.20 / 0.50, on/off screenshots from `(129.73, -3.8574, 7.73)`, yaw 294.5,
pitch 12 showed substantially less reflection on the covered concrete. The large source-triangle
transition from the first candidate was removed. A second interior view around `(129, -3.8574, 18)`
showed intact floors/walls/textures. This is not an exhaustive walk through every LOD distance.

Exterior control at `(129, -3.8574, -4)`, yaw 270, pitch 35 showed the same outdoor apron with the
repair enabled and disabled. F10 at **22:00:19.618** hit the retained original Hangar concrete slot.
All test teleports used the full NavMesh path, hazard and standing-room checks.

Five enabled classification passes took **621, 642, 624, 640 and 634 ms**, compared with roughly
eight seconds for the rejected first candidate. This measures classification at startup/live
reclassification, not per-frame rendering cost. A single CPU depth snapshot replaces repeated
GPU readbacks. Glass false/true and F8 cover rerenders at **21:59:32–21:59:35** retained stable
32-mesh / 21,032-fragment results. Live off/on restore/reclassification also passed twice.

The final saved Bay5 settings are `IndoorSurfaceRepair=true`, reflection **0.20**, smoothness
**0.50**, `CoverIncludesGlass=true`; legacy smoothness/reflection remain 0.50 / 1.00 and
`ReflectiveSpecularCap=0.2957746`. South extraction completed; cleanup at **22:02:10.555** restored
642 renderers and destroyed all 1,008 owned material clones.

Local screenshots, deliberately ignored by git, are in
`analysis/screenshots/bay5-indoor-materials/`: `before-final.png`, `after-final.png`,
`geometry-identity.png`, `exterior-on.png`, `exterior-off.png`.

### Remaining visual limits

Authored puddles and excluded decal/VertPaint shaders remain reflective. This changes material
response, not the reflection source. Fine material transitions can remain at roof openings.
The 18.75 cm subdivision edge is not a guarantee of mask accuracy: roof-depth resolution and the
0.75 m discovery grid also matter, and smaller cover features can be missed. Adjacent original
triangles can have nonconforming subdivisions (T-junctions); no complete seam-free or pixel-exact
clipping claim is made. The tested exterior control and four Hangar LOD assignments passed, but
this is not visual acceptance of every surface on every map.

### Equator control after Bay5 teardown

In the same process, Equator started through EQUATOR DEBUG. At **22:07:26.529–.532** its new
surface path was disabled with zero partitioned meshes; legacy classification remained
**607 renderers / 1,103 material clones**, matching the accepted baseline. The new setting was
confirmed `IndoorSurfaceRepair=false`. Debug return completed; at **22:08:08.814** cleanup
restored those 607 renderers and destroyed 1,103 clones. No Equator settings were changed.

### Final Bay5 repeat and shutdown

After the Equator control, a second ordinary PMC Bay5 raid in the same final process automatically
classified **32 meshes / 21,032 selected fragments** at **22:10:17.239**, with the same four Hangar
LOD assignments and **602 ms** elapsed. The target floor view was rechecked at the same position
and view angles; the reduced concrete reflection and remaining original puddles were stable.
No F8 or manual reclassification was needed. Reflection/smoothness remained 0.20 / 0.50.

South extraction completed again. At **22:12:03.043**, cleanup restored **642 renderers** and
destroyed **1,008 material clones**, matching the first final-build raid. MainMenu returned;
the final status again confirmed all 25 plugins plus Redeploy Core identical. UiBridge reported
**zero ArenaMapPort Warning/Error/Fatal lines** across the recorded final process (#1–#4412).
EFT was then exited normally at approximately **22:13 CEST**. Native selected log/status/config
responses are retained in `D:/.unlazy/bay5-indoor-materials/native-final.json`.

This acceptance covers the reflection repair, live settings, glass-cover rescans, map switching,
repeat raid and cleanup. It does not repeat ArenaMode Deathmatch/Gun Game or profile-preservation
acceptance; those historical results and unchanged map artifacts remain documented separately.
