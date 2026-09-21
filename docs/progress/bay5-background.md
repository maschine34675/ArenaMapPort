# Bay5 original harbor background

## Source and implementation checkpoint — 2026-09-21

The user confirmed that Bowl already has its stadium, and asked to add Bay5's missing
background. This change selects the original `Arena_Bay5_Background` (source `level79`)
after the existing eight extracted Bay5 scenes. It contains three-dimensional harbor
and city geometry, not a replacement skybox texture.

The original scene contributes 7,090 objects, including 4,055 MeshRenderers and 1,026
LODGroups. Source hierarchy activity, renderer flags and collider states are preserved;
Arena-only gameplay/culling scripts remain excluded by the existing component allowlist.
The fresh package is `work/extract/bay5-background`: 59,422 objects, 3,911 meshes,
453 materials and 945 textures, zero extraction errors. Source level79 SHA-256:
`F959EF0B92374EF7CE888E923F6FE0A199BEB0EB3F7D60AF0694DC9D292AF5C0`.

`background_scene_names` is mirrored by Shared `BackgroundSceneNames`, with empty
defaults for other maps. The descriptor validator rejects unloaded/duplicate names,
the authored Scripts scene and JSON/runtime disagreement. The preset loads the
background and runtime ownership includes it for rendering, shader repair and teardown.

Background renderer bounds are excluded from the NavMesh bake volume and from
rain-cover/indoor-floor classification. Its original colliders remain NavMesh sources:
some shipping containers occupy real space along the south lane. Omitting those sources
would let bots plan paths through visible solid objects. The distant skyline reaches
roughly 1.7 km southwest and must not enlarge the bake or roof-camera footprint.

An independent source and production review found no functional P0–P2 defects.
All four player starts, nine bot starts and north exit have no conservative body-box
overlap with source background colliders. The southeast bot and south exit are near
container walls (about 0.422 m and 0.341 m from their vertical body axes); these source
measurements require native navigation, standing-room and extraction checks.

Before-build reference views from the old loaded process `c48f7fdd`:

- `analysis/screenshots/bay5-background/before-north-lane.png`: (112.518, -3.8574,
  15.134), yaw 0, pitch -30.
- `analysis/screenshots/bay5-background/before-gantry.png`: (77.7537, -3.5284,
  61.7423), looking toward (82, 18, 115).

These images show the old open sky. They are comparison evidence, not acceptance of
the new bundles. The original south exit completed during this reference raid.
Build, deployed identity and native acceptance are recorded in the appended checkpoint.

## Build guard correction — 2026-09-21

The first background build passed Unity, component-identity patching and the strict
geometry/material roundtrip, then correctly failed preset validation: the scene bundle
had all ten scenes, while the old preset still listed nine. `build_all.ps1` incorrectly
conditioned full-map base/preset generation on `manifest.source.map_key`, which the
legacy Bay5 extractor does not emit. That inner guard is removed; descriptor identity,
any supplied manifest identity and exact scene-order checks remain mandatory. Equator
without `-FullMap` is unaffected. An independent review confirmed this correction.

The existing Unity output is revalidated with `-FullMap -SkipUnityBuild`, regenerating
the cheap base/preset outputs rather than rebuilding geometry. The first failure is
retained in `D:/.unlazy/bay5-background/build-all-first-stale-preset.log`; the final
pipeline receipt is `D:/.unlazy/bay5-background/build-all.log`.

## Native candidate and northeast bot clearance — 2026-09-21

Process `23dee6d0` (started 11:26:17 CEST) loaded the matching client and the new
22,190-vertex / 10,248-triangle NavMesh. At 11:29:03 the background loaded additively;
all 72 remaining stand-in materials in that scene rebound to real shaders. The northern
gantry and southeast portal crane were visibly present, with no pink surfaces or new
Warning/Error lines during the inspected raid. Screenshots are under
`analysis/screenshots/bay5-background/after-*.png`.

The same north-lane view now shows the gantry where the old screenshot showed sky.
The second comparison, originally aimed toward the gantry from (77.75, -3.53, 61.74),
now correctly shows a nearby original container stack blocking that view. A southeast
view from (89, -3.85, 15) shows the original portal crane. Distant skyline visibility
was not established from the southwest floor viewpoint because the arena buildings
occluded it; inclusion in the package is independently verified.

All fourteen authored starts passed full NavMesh/zone/reachability/standing-room probes.
Depot and warehouse checks also passed, and both eight-second ordinary exits completed.
RainBounds and indoor classification retained the exact previous values: 641 indoor
renderers, 1,112 material clones, RainBounds center (24.55, 5.89, -157.84), size
(351.63, 97.94, 726.13).

Checking the actual runtime-adjusted bot positions exposed one additional issue:
the original northeast marker (106.267, -3.8574, 64.7033) was automatically moved to
(107.33, -3.84, 65.76), now a disconnected patch beside the added background containers.
The native patrol graph reported two components and one isolated point; the strict
UiBridge path check independently rejected that adjusted position. The east adjusted
point remained connected.

The northeast marker is moved three metres south in `bay5.bots.json`, to
(106.267, -3.8574, 61.7033). That point and four offsets of half a metre each passed
the full native checks, and the player stood there safely. This only regenerates the
server base; the validated scene/NavMesh bundles and the other starts/exits are unchanged.
Final deployment and the native patrol-graph repeat follow below.

## Final acceptance — 2026-09-21, 11:56 CEST

Implementation commits are `4f2025e` (background), `2ba4d71` (legacy full-map preset
generation) and `b6e0692` (northeast bot sidecar). The final generated base contains
exactly one structural change: `/SpawnPointParams/8/Position/z`, 64.7033 to 61.7033.
An independent review confirmed the backend position is applied to existing runtime
markers by EFT's `SpawnPointMarker.FixParams`; no further Unity bake is needed.

### Artifacts and deployment

The full pipeline passes extraction, mandatory MonoScript identity patching, strict
bundle roundtrip, ten-scene preset validation and distribution validation. The original
eight extracted scene JSON hashes are unchanged. The final bake has 22,190 vertices,
10,248 triangles and 221 tiles (previously 198); its binary bake bounds are exactly
unchanged. It retains 1,114 additional active background colliders as physical sources.
The source has no replacement skybox material, baked lightmaps or usable NavMesh data.

Checks passed: 93 Python tests, 328 server/shared tests, eight PowerShell log-scanner
cases and a zero-warning/error Release client build. The distribution still has
14 maps, 42 bundles, 14 shader sidecars and 72 total files. Seven intended files
changed (Bay5's three bundles, shader sidecar and base, plus both DLLs); the remaining
65 files are byte-identical, including every other map's bundles and server base.

The authorized `deploy-to-spt.ps1 -SptRoot D:/SPT41 -Confirm` copied all 72 files.
An independent installed-hash pass matched every file. Final receipts are under
`D:/.unlazy/bay5-background/`: `distribution-after.json`, `distribution-delta.json`,
`deploy-final.log` and `install-final-verification.json`. The earlier `deploy.log`
and `install-verification.json` describe the first candidate before the marker fix.

| Final artifact | SHA-256 |
|---|---|
| Client DLL | `6E12B25466F156A977F39DF3ABA44FA8AF61A6D54D9BCFEE789942B46B8140FD` |
| Server DLL | `EBFAD3424224693656FCFCD55431B859E0F9506BE933B32799F4981A0D97103A` |
| Bay5 scenes | `347407ABF2B3F8B18E32B560C0D3368D5DCE7712358F9BB33E4F9F741953D244` |
| Bay5 NavMesh | `CD7A3308E9D21F54F4FCF19242114B97E3952FB452F2E1F6ADAF0082D7DE64F3` |
| Bay5 preset | `37C4E5D79B3DDB44BE5A4A83245EE698840D42380F94FF833D8C6BB53130A2CA` |
| Bay5 shader sidecar | `739C9DF2ABF0026B99399975D34F1C09C02B529D6120F7B409C53DE47C6F36BE` |
| Bay5 base | `CEAA30A95521FB80FEE39A623B43E69FF5FA5D32F70914A1A4DF50993A76775C` |

### Final loaded process and native navigation

After restarting server and EFT, process `d1121cb1` (started 11:48:44) reported all
25 maschine plugins plus Redeploy Core identical across VM build, host disk and
loaded code. At 11:52:39.972 the background loaded; at 11:52:39.980 all 72 remaining
stand-in materials rebound. At 11:52:46.926 the expected 22,190/10,248 NavMesh loaded.

At 11:52:47.911, the native patrol graph reported **10 points, one connected component,
zero isolated points and zero off-NavMesh markers**. The placement log moved only the
existing east-lane point to (119.39, -3.87, 43.10); the corrected northeast point needed
no move. All four player starts and ten effective bot positions passed full NavMesh,
hazard-zone, reachability and standing-room checks; maximum reported snap was 0.1 m.
`uibridge_places` returned no spawn places in this ArenaMode session, so the evidence
uses the runtime placement log, patrol graph and explicit coordinates instead.

RainBounds remained center (24.55, 5.89, -157.84), size (351.63, 97.94, 726.13).
Raid teardown restored the same 641 indoor renderers and destroyed 1,112 clones.
Both final exit triggers again sampled at 0.0 m from their authored positions.
The completed ordinary extractions in process `23dee6d0` remain applicable: north
returned through ExitStatus at 11:33, south at 11:38. Those tests used the identical
final scene/NavMesh bundles and exit data; only the northeast bot coordinate changed
afterward. They were not repeated as ordinary extractions inside the Deathmatch test.

### Deathmatch, return and limits

Deathmatch became active at 11:53:08.394; round 1 started at 11:53:10.030. Bot snapshots
showed initial PatrolFollower movement and subsequent SAIN combat activity. For example,
`openingspark860` moved from (106.4, -3.9, 13.8) to (89.1, -3.9, 6.5), and `MadDutch`
from (48.2, -3.9, 60.9) to (64.4, -3.8, 39.4). Population reached seven living PMCs;
replacement arrivals continued through request 12 at 11:54:25.178. A final native view
again showed the southeast crane during the match:
`analysis/screenshots/bay5-background/final-deathmatch-crane.png`.

Native `Stop(Left)` committed at 11:54:44.018. The background unloaded at 11:54:50.044;
the observed route was ExitStatus, its Main Menu button, then MainMenu with no raid.
EFT quit cleanly at 11:56. Full normalized profile snapshots before the match, after
return and after the final flush are byte-identical, excluding only the background
hideout timestamp. Their SHA-256 is
`6fdf7a4907c9cbbf0029562193d96b6dcd77f5a51a8573782f08eac29fbd40b7`.
Evidence: `native-final.json`, `arena-before.profile.json`, `arena-after-dm.profile.json`
and `arena-after-dm-flushed.profile.json` in the ignored evidence directory.

No ArenaMapPort warning/error was observed during the final map load. The modded test
stack did emit its intentional RespawnApiProbe exception, a stale PerfectCulling
sampler warning and an ArenaMode weapon-panel `ArrayTypeMismatchException` at
11:53:10.048 in `GrenadeInventoryCounter.CollectSummary`. These are retained in the
native receipt, not presented as a clean global log. ArenaMode was not edited.

Acceptance covers visible cranes/containers, connected combat starts, short Deathmatch
movement/replenishment, protected return and the two ordinary exits. Distant skyline
visibility, a full Gun Game ladder, long-session stability and performance remain
outside this pass. Bowl, the read-only Arena source and the vanilla test install were
untouched. The remaining maps continue individually through `docs/MAP-RECIPE.md`.
