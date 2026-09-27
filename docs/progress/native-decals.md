# Native projection decals — Equator and Bay5

## Source recovery and implementation — 2026-09-27

The earlier exclusion in PLAN-TWO-MAPS section 3 looked for material references on
MeshRenderers. StaticDeferredDecal has its own serialized material reference; a missing
MeshRenderer is expected for a projected decal, not evidence that its data is absent.

The original Arena 0.5.4 player files contain 348 components on Equator and 499 on Bay5.
Every material pointer resolves to a Material with shader
`Decal/DeferredDecalDiffuseNormalsDynamicEditor`, also present in installed EFT
0.16.9.5-40743. No material assignment or decal placement is guessed.

### Verified serialized layout

After the standard variable-length MonoBehaviour header, each component has exactly
72 bytes: LowerLeftPixel (Vector2), UpperRightPixel (Vector2), LastTexSize (Vector2),
material PPtr (int32 file + int64 path), queue (int32), normal clip/fade and edge clip/fade
(four floats), tint (Color). All 847 current source records are 104 bytes including
their empty-name header. Arena places LastTexSize before the material pointer; EFT's
managed class declares it later. Import assigns named fields, never copies that payload.

The parser rejects unknown sizes, nonfinite fields, invalid enums/clipping, unresolved or
wrong-type references, unexpected shaders and unexpected texture dimension ratios.
Material/Texture2D type resolution and original dimensions provide independent layout
checks. Raw provenance, texture dimensions and original pixel boundaries remain in the
ignored extracted packages.

Five Equator and nine Bay5 `City_Graffiti_road_overlay` decals retain a 2048x1024
authoring size while the shipped texture is 1024x512. Their material tiling is 1/1,
offset 0/0. EFT divides pixels directly by actual texture size; the importer therefore
scales both pixel corners by target size / authored LastTexSize. It preserves negative
and slightly oversized boundaries rather than clamping the original atlas region.
This preserves the source-authored region; Arena's own IL2CPP runtime adjustment has
not been decompiled.

### Packages and integration

`tools/extract/extract_decals.py` integrates with both full extractors and can augment a
complete accepted package into a new output directory. Candidate packages are
`work/extract/equator-native-v5` and `work/extract/bay5-native-v5`, based on v4.

Independent v4/v5 comparison: after stripping only StaticDeferredDecal components,
all scene JSONs are semantically identical, including hierarchy, transforms, activation,
and other components. Every pre-existing non-scene/non-manifest file has identical SHA-256;
existing manifest material/texture entries are unchanged. Additions:

| Map | Components | Hierarchy-active and enabled | Materials | Additional texture files |
| --- | ---: | ---: | ---: | ---: |
| Equator | 348 | 346 | 60 | 49 |
| Bay5 | 499 | 468 | 67 | 63 |

Inactive source objects remain inactive. No geometry, colliders, lighting settings,
existing material tuning or gameplay configuration is changed by this feature.

`StaticDecalImporter` authors the real native components. `ShaderRebind` now also visits
their own material references. `NativeDecalRepairs` waits for the owned scene set and
shader sweep, then creates one native StaticDeferredDecalRenderer and explicitly builds
its instance buffers. It refuses to create a competing manager. Existing vanilla scenes
are untouched. Old bundles with no decal components remain compatible.

New live comparison controls: `Decals (arena_equator) / Enabled` and
`Decals (arena_bay5) / Enabled`, initially true. This does not change the older,
default-off `Decals / HideProjectorDecals` diagnostic for the distinct Ultra/Water shaders.

The native manager's camera callback ignores component.enabled; live off destroys the
owned manager. ReInitialize is avoided because it duplicates native decal event subscriptions.
Owned managers use the direct native instanced draw path; both QuadTree modes are disabled
before Awake (imported textures do not use streaming mipmaps). Native OnDestroy clears
compute buffers/texture arrays but leaves some material clones/command-buffer wrappers;
the client retains and disposes those owned resources too, including during scene unload.

### Validation before host test

- Strict original-data recovery succeeded on both complete map scene sets.
- Both full package validators passed; existing source/legacy warnings remain.
- Parser tests exercise malformed/truncated data, null and wrong-type pointers, enabled
  state, idempotency and unsupported resolution changes.
- Bundle checks compare native script identity, hierarchy/TRS, active/enabled state,
  material identity, normalized UVs, queue, tint and projection parameters. Negative
  fixtures prove missing components and changed fields fail validation.
- Client Release build and 395 server tests passed; pipeline unit tests passed.

Build and GPU evidence will be appended after completion; extraction alone is not native acceptance.

### Build verification — 2026-09-27, 23:52

- Final Python discovery: 417 tests, 28 skipped, no failures (18.438 s).
  Log: `work/decals/final-python-tests.log`.
- Equator complete build passed Unity verification, native script identity patching,
  package/bundle roundtrip (including all decal fields), NavMesh and map registry checks.
  Log: `work/decals/build-equator-retry.log`; Unity log:
  `work/build/full/unity_build_all_20260927_233134.log`.
- The first Equator batch run encountered an initial import-order compiler error for the
  newly added importer, then recompiled successfully. The mandatory log gate rejected that
  run. After Unity registered the new source asset, the full clean retry above passed;
  no log-gate exception was added.
- Equator was staged separately while the Bay5 build was still running: only its two
  validated bundles and material shader sidecar were copied from `work/build/full` to
  `dist`. The already built client was deployed through `deploy-to-spt.ps1`.
  Bay5's candidate output was not staged during its build.

### GPU-2 — first Equator acceptance, 2026-09-27, 23:55–23:59

Fresh EFT instance `f2ac85fd` (23:55:41); UiBridge 0.6.8 reported all 23 machine
plugins identical across VM, host disk and loaded process. SPT server restarted before
launch. Client SHA-256 `928B1555C59B6A30E7539EFB985AFFDB5C3C00A8FEB072B2E417689A77985283`;
Equator scenes `14EED6F8AE675C54F1306A34CBE1FDD3F1BA2A53A92A2285ED65E512FD13F796`.

- 23:56:56.660: `source=348 active=346 registered=346 materials=59 groups=23
  nativeEnabled=True camera=DeferredShading`. Two source objects remain inactive.
- At `(6.5,0.1,-28.95)`, yaw 90, the original layered wall graffiti is visible. Live
  off at 23:57:29.746 removes it completely, leaving the original lit wall. Live on
  at 23:57:42.980 restores it with exactly 346 registered instances.
- At `(0,0.1,52)`, looking toward `(-0.979,1.05,54.576)`, a second original black
  wall motif is correctly projected behind the foreground cover. Looking back toward
  `(-0.09,0.1,46.019)` shows multiple large graffiti motifs across the mall shopfronts.
  Off/on removes/restores these, with existing green extraction smoke, floor details
  and lamp lighting retained. Some floor footprints already exist as other content and
  remain visible with this feature off; they are not claimed as new decals here.
- Disable and raid unload both report release of 23 owned materials and 25 command
  buffers, including native callbacks. Final unload: 23:59:40.571.
- No rendering/texture exceptions. The already documented `MenuScreen is inactive`
  coroutine error occurred when opening the pause menu at 23:59:18.576.
- Botless raid; player health remained full. Returned to MainMenu with Enabled=true.

Screenshots were inspected directly through the Windows GPU-2 MCP tool in this task.

### Equator repeat raid — 2026-09-28, 00:00–00:02

The same process entered Equator again after the first unload. At 00:00:43.204,
the new renderer again registered exactly 346 instances / 23 groups. The same wall
graffiti remained visible without manual reactivation. No new errors; teardown at
00:01:37.941 again released 23 materials and 25 command buffers. Returned to MainMenu,
then closed EFT gracefully for the later Bay5 bundle deployment.

### Bay5 build verification — 2026-09-28, 00:14

`work/decals/build-bay5.log` completed successfully, including clean Unity log checks,
mandatory native script identity rewriting, all 499 decal field/hierarchy/material/UV
comparisons, existing native-feature validators, NavMesh, preset and registry checks.
The final scenes bundle is 785,870,418 bytes. Unity log:
`work/build/bay5/unity_build_all_20260927_234122.log`.
The only bundle roundtrip warning on each map is the expected one extra built-in material.
Source package warnings remain the existing legacy/unreferenced-asset diagnostics.

A normal client Release build then staged both validated map outputs into `dist`
(0 warnings, 0 errors). The repeated final gate run passed both runnable gates;
Python discovery remained 417 tests with 28 skipped, and the client SHA-256 did not change.

### GPU-2 — Bay5 acceptance, 2026-09-28, 00:16–00:22

Fresh instance `4d438dd4` (00:16:29), all 23 machine plugins identical VM/disk/loaded.
Both map bundles were deployed before this restart. The server DLL and regenerated
Bay5 base JSON were byte-identical to the already restarted host, so another server
restart was unnecessary. Bay5 scenes SHA-256:
`3529722DD19E9F896A210CF0ADC13FC498CF8DBF1260D235B87D657D52DCCC3D`.

- Botless Bay5 raid. For this comparison only, the existing TimeOfDay/ArenaFixedHour
  was changed from 0 to 12 through UiBridge. After exit it was restored to 0 and read
  back. Clear weather and saved lighting/flare settings were retained.
- 00:18:07.220: `source=499 active=468 registered=468 materials=65 groups=36
  nativeEnabled=True`. The renderer initialized before Camera.main existed; rendering
  correctly followed the later camera through the native callback.
- At `(124,-3.9,45.2)`, looking toward `(129.4,-1.4,45.8)`, the black illustrated
  graffiti is visible on the warehouse entrance's hanging tarp; a second graffiti
  is visible on the rear corrugated wall. Both disappear on live off (00:19:44.131)
  and return on live on (00:20:11.936). Lighting, cover and ordinary surface details
  remain. Re-enable again reports exactly 468 instances, 36 groups, DeferredShading.
- Live off and final unload at 00:22:29.202 each released 36 materials and 38 command
  buffers. No renderer, shader or texture exceptions. Only the previously known pause
  menu coroutine error appeared (00:21:59.235).
- The attempted ground-decal viewpoint near `(118.457,-3.9,41.43)` was occluded by
  containers/concrete cover, so that particular decal is not visually certified.
  Its serialized UVs/settings passed the same complete bundle comparison.

### Cross-map lifecycle — 2026-09-28, 00:24–00:25

In the same `4d438dd4` process, Bay5 was followed by Equator. At 00:24:09.651:
`source=348 active=346 registered=346 materials=59 groups=23 nativeEnabled=True
camera=DeferredShading`. The original wall graffiti at `(6.5,0.1,-28.95)` was visible
again. No new raid/render errors; health 440/440. Final unload at 00:25:33.826 released
23 materials and 25 buffers. GPU-2 returned to MainMenu; both Decals Enabled settings
remain true, and TimeOfDay/ArenaFixedHour is restored to its original 0.

Acceptance covers representative visual comparisons, complete serialized field checks,
two Equator raids, and Bay5-to-Equator reuse in a fresh process. It is not an exhaustive
visual inspection of all 814 active decals or a performance/memory benchmark.

### Final deployment — 2026-09-28, 00:28

Only after both maps passed native tests, `tools/deploy-to-spt.ps1 -SptRoot
D:/SPT41vanilla -Confirm` promoted the staged build to Vanilla. SHA-256 readback
confirmed **all 86 staged files identical in both D:/SPT41 and D:/SPT41vanilla**.
Deployment logs: `work/decals/deploy-both-gpu.log`, `work/decals/deploy-vanilla.log`;
selected final hashes: `work/decals/final-hashes.json`.

Client SHA-256 remains `928B1555C59B6A30E7539EFB985AFFDB5C3C00A8FEB072B2E417689A77985283`.
Server SHA-256 is `405E4239B6E16E97EA8C7180A2D613E2F91B9F8FF42E0777481A4698B35B3807`
(rebuilt during the 395-test run; no server source changes in this feature).
The host is idle in MainMenu with control free. Vanilla needs a server/game restart
before the user tests; no subjective user acceptance is implied by the GPU checks.
