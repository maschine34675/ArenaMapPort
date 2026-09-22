# Bay5 DynamicMaps ground plan — 2026-09-22

**Latest status: installed and natively tested after host release.** The initial
staging record below is retained; see the appended native acceptance section.

## Status and scope

**Locally generated and reviewed; not deployed or natively tested.** The user accepted
Equator and requested the equivalent Bay5 pack, then explicitly asked to wait for
release of the shared GPU host before Bay5 gameplay testing. No production writes
or host control occurred during this hold. The Bay5 destination is still absent.

Work is isolated on `feature/bay5-dynamicmaps`, base `cfbbf16`, in
`D:/ArenaMapPort-DynamicMapsBay5`. The main checkout is occupied by independent
lighting changes on `feature/bay5-concrete-lighting` and was not edited by this task.
Do not switch or overwrite that checkout to resume this work.

Only the offline exporter, data-only deployment mode, tests and documentation change.
There are no new scene bundles, client/server DLLs or third-party settings. Generated
game-derived data remain ignored under `work/dynamicmaps/`; they are not committed.

## Geometry and coordinates

The source is the existing local `D:/ArenaMapPort/work/extract/bay5-native-features`
package. Eight content scenes supply **5,271 eligible colliders**: 5,073 mesh,
193 box and five capsule colliders. Full parent transforms, original active state,
disabled/trigger filtering and the established round-open door/shutter exclusions
are retained. The harbor background scene is excluded.

An isolated Unity 2022.3.43f1 batch project read the existing Bay5 NavMesh bundle
through `ExportMapTriangulation.Run`: **22,190 vertices / 10,248 triangles**.
The bundle was not rebuilt. Its SHA-256 is
`cd7a3308e9d21f54f4fcf19242114b97e3952fb452f2e1f6adaf0082d7de64f3`.
The local triangulation receipt records the same hash.

The reviewed native ground band is world Y **-4.5..-2.5 m**, with projected component
anchor X/Z **(90,30)**. This includes the main low ground, raised warehouse floors
and ramps. Common container tops near -1.25 m are excluded. The selected simplified
component covers approximately **4,076.285 m²**; the much larger total source/native
areas in the receipt include off-map pieces and must not be read as playable area.
Projected polygon membership does not establish native path connectivity.

| Property | Value |
|---|---|
| Map identity | `arena_bay5` |
| World X / Z bounds | `41..149` / `-18..74` |
| SVG viewBox | `0 0 108 92` |
| World-to-SVG | `(u,v) = (x-41,74-z)` |
| World-to-DynamicMaps | `(x,y,z) -> (x,z,y)` |
| Rotation / layer / default level | `0 / 0 / 0` |

Both authored exits and all spawn areas fit within the drawing. Three authored
point centres are 0.025–0.086 m outside exact native triangle edges; this small
bake/authoring difference is not evidence of a blocked route. DynamicMaps supplies
live player/exit markers; the two static exit markers are hidden during raids.

Bay5 samples local reference height on the nearest native ground triangle, clamping
to its boundary and interpolating its vertex heights. Floor/obstacle classification
therefore follows the varying floor elevations. The independent review found that
widely spaced samples could tilt a large flat admin-building floor into a false
diagonal obstacle. Bay5 now gives projected native ground priority over obstacle
overlays. Real obstacle holes remain visible; overhangs above that ground are omitted.

Obstacle contours use 18 cm simplification, with 4.5 cm native-ground simplification
and 1 mm topological quantization before serialization. Small residual edge overlaps
remain after simplification (about 2.55 m² low / 0.01 m² high across the map).
This is a readable ground plan, not exact collision, line-of-sight or walkability data.

## Offline verification

- **43 exporter tests passed**: 27 existing tests plus 16 Bay5 cases. The new cases
  cover sloped/local ground sampling, boundary clamping, invalid input, height bands,
  identity/axes/exits, background exclusion, empty polygons and reproducibility.
- The large flat-floor regression checks final serialized SVG: native ground stays
  clear in the fixture while at least 95% of a real obstacle inside a native hole
  remains. Disabling the correction in memory makes the regression fail.
- **80 deployment fixture cases passed**: 14 positive and 66 rejection cases,
  including both install orders. Only the selected map's exact JSON/SVG pair can
  be copied to its allowlisted directory. The standard deployment body is unchanged.
- Parent re-verification passed both suites. Independent review accepted the height
  band/anchor, source exclusions, final preview, coordinate contract and serialized
  geometry: **1,172 valid rings**, five valid filled layers and **13,737 map points**,
  plus four north-arrow points. The limit remains 16,000 map points.
- The final generator reproduces the accepted Equator JSON, SVG and preview byte
  for byte. Its existing profile, drawing order and 12 cm obstacle tolerance remain.

Two complete Bay5 exports produced identical JSON, SVG, receipt and preview.
The final local verification passed all **816 Bay5 input hashes**, current generator
and output hashes, the unchanged input bundle, and the absent live Bay5 destination.
See the ignored `work/dynamicmaps/verify-offline.py` and the task's gate evidence.

| Candidate file | Bytes | SHA-256 |
|---|---:|---|
| `ArenaMapPort_Bay5.jsonc` | 1,704 | `b0f5f02a14ce5b4578241ec7908664f6eefed72fe88aee2f9b78b2dfd69fafae` |
| `Layers/Ground.svg` | 206,981 | `98fc0b08635cb022f5aba76824b3113325acd09739c12fdcc3d679885fe2e980` |

Generator SHA-256:
`12f46346a6eb391a0b546806eb45449e7b0f2aa35aa71ccc7b0db95672933f77`.
The [generation guide](../../tools/dynamicmaps/README.md) records the reusable recipe.

## Resume after host release

The sanctioned **dry run** accepted the real candidate and planned exactly two new
files beneath `BepInEx/plugins/mpstark-dynamicmaps/Maps/ArenaMapPort_Bay5/`.
It copied nothing. Equator's existing installed pack remains separate.

After the user releases the host, recheck session ownership and compare the live
Bay5 NavMesh bundle with the recorded input hash. Regenerate if navigation changed.
From the isolated checkout, review the dry run again, then install the candidate
with the sanctioned script's `-Confirm` switch. Restart EFT so DynamicMaps reloads
definitions and sprites. Do not deploy the worktree's unrelated base client DLLs.

In a Bay5 PMC raid, open inventory → **MAP** and check:

1. Successful automatic selection/rendering and no SVG/tessellation errors.
2. Player location at several points, including the raised northern warehouse.
3. All four cardinal arrow directions (+Z up, +X right).
4. Both active exits against their authored locations and no duplicate static markers.
5. Normal extraction/menu return with profile health preserved.

These native checks remain **pending**, including the actual Unity tessellated
vertex count/limit. Offline SVG point counts and the PNG preview do not prove the
native renderer. Extensive landmark labels, roofs and higher container surfaces
are outside this ground-plan milestone.

## Native acceptance after user release — 2026-09-22 (CEST)

The user released the host with "die andere session ist durch, kannst übernehmen".
The other session had completed the exterior-ground brightness change on main
commit `7147e8e`. Its installed client SHA-256
`c02c98d130616f9ca96ecd45870ed193a2852b2c49c318fa30f507edc330abdc`
was retained. The live local-mirror Bay5 NavMesh still matched the triangulation's
`cd7a3308…64f3` input hash, so regeneration was unnecessary.

The sanctioned script installed exactly the two files from candidate `dd0dce0`;
the JSON/SVG hashes remain those in the table above. A fresh EFT process
**e93a8a10**, started **19:38:54**, reported no other UiBridge session. All **46 plugin
DLLs and five libraries** matched VM build, host disk and loaded code before and
after testing. Environment: DynamicMaps 1.2.1, UiBridge 0.6.1, SPT 4.1.6,
EFT 0.16.9.5-40743, Unity 2022.3.43f1.

DynamicMaps logged the Bay5 definition at **19:39:16.433**, successful display-name
load at **19:39:16.459**, SVG precache at **19:39:16.518**, and MapScreen selection
at **19:42:57.663**. The plain PMC test used bot amount None. In-game navigation
reported the matching **22,190 vertices / 10,248 triangles**.

| Probe | World X/Y/Z | Yaw | Observed map result |
|---|---|---|---|
| Southwest start | 56.40 / -3.78 / 1.90 | 45° | Northeast arrow in the southwest start area |
| Central yard | 90 / -3.88 / 30 | 0°, 90°, 270° | Up, right and left at the same point |
| Raised northeast warehouse | 140 / -2.88 / 60 | 180° | Down at the correct raised-floor position |
| North approach | 96 / -3.83 / 57 | 270° | Four metres south of the north exit; label partly overlaps arrow |

The player's displayed X/Z/height matched every probe. Position updates worked
while MAP remained open. The initial southwest view was centred on that player
position and cropped the north edge; reopening MAP from the central yard showed
the full extent. This is normal viewport positioning, not missing geometry.

Both runtime exit icons matched: North Loading Bay at X/Z **(96,61)** and South
Container Lane at **(99.029,-2.8513)**. There were no duplicate static exit icons.
Independent screenshot review measured the full-map rectangle at approximately
pixels `[471,121]..[1549,1040]`, with the yard player around `(960,560)`, raised-floor
player `(1459,261)`, and exits `(1020,251)` / `(1050,889)`, matching the world transform
within approximately two pixels. UiBridge point probes passed NavMesh, hazard-zone,
reachability and room checks; exfil-place probes passed NavMesh, zone and room checks.

Normal **North Loading Bay extraction completed in 9.4 s**. After the ordinary
result screen, MAIN MENU returned in **30.2 s**. The PMC selector confirmed
**440/440 HP**. No test shots were fired; the last in-raid reading retained **37 + 1**
rounds. EFT was closed normally and UiBridge confirmed it gone (unreachable for 8 s).

DynamicMaps emitted **zero Warning-or-worse entries** throughout this process.
Across all sources, **zero Warning-or-worse entries** occurred between the first map
opening and final menu return. Earlier unrelated startup messages are outside the
latter claim. Native SVG rendering passed without tessellation errors; the actual
tessellated vertex count was not instrumented and is not the SVG path-point count.

Evidence remains ignored under `work/dynamicmaps/bay5/evidence/`: `native.json`,
six `dm-*.png` views, `native-baseline.json`, `native-preservation.json` and
`independent-native-review.md`. The most useful full-map view is `dm-centre-east.png`.
Acceptance covers this botless PMC test and these probes, not every ArenaMode setting,
higher prop surface, or future third-party version.

### Post-test preservation

The independent baseline audit confirms **164/164 original files unchanged**, no
removals, and exactly the two expected Bay5 JSON/SVG additions with matching hashes.
This includes the installed lighting client, map bundles, server data, Equator pack
and third-party DynamicMaps files. DynamicMaps' configuration is byte-identical too.

ArenaMapPort's config differs only in **170 bytes of BepInEx comment metadata** for
Equator's `PlaybackDiagnostics` setting: this Bay5-only process removed its description,
type and default-value comments. Reinserting those comments **in memory only** reproduced
the exact pre-test file hash and length. Every actual setting/value, including the
other session's Bay5 brightness setting, is unchanged. No config restoration or manual
production write was performed. The audit separates raw-byte inequality from proven
setting-value preservation; neither should be confused with a changed lighting value.
