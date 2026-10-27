# One AirPit spectator — 2026-10-27

User-approved prototype: one decorative spectator on AirPit's inaccessible gallery, using original
idle and cheer animations. No crowd expansion, gameplay AI, reactions to match events or crowd audio.

## Source and implementation

`tools/extract/extract_spectator.py extract` reads the owner's Arena `level40` and
`sharedassets40.assets` into ignored `work/extract/spectator-airpit`. It selects GameObject 5434,
`Arena_NewFans_08 (6)`, at its original position `(9.81, 8.51, 13.93)` and rotation
`(0, -0.78977275, 0, 0.61339962)`. Source root `SCAV_SPECTATORS` remains inactive in the map bundle.
Only a new owned actor is enabled; the other 98 figures remain absent.

- 149 transforms, four active skinned parts, 4,333 vertices. Body, head, trousers and rig use the
  original LOD1 meshes (the selected actor has no higher active LOD). No valid static accessory.
- Bone weights and indices come from modern vertex channels 12/13 (`MeshHandler`), not the empty
  legacy `m_Skin` array. Bindpose matrices retain row/column order and the renderer's bone order.
- Original native `p0/Reflective/Bumped Specular SMap_Decal` materials, 12 normal/diffuse/specular
  textures and two cubemaps. Packed normal channels remain unchanged, linear normal maps and sRGB
  colour maps retain their colour space. Cubemap faces are exported separately in Unity face order.
- Clips 284 `ArenaFan_Idle_000_loop` (1.8 s, authored looping) and 311
  `ArenaFan_Idle_Cheer_001` (2.433 s, one-shot) are sampled at 60 Hz. Three idle cycles, one cheer,
  then idle again, with 0.3 s transitions. This test sequence is independent of the match.
- Source Animator has no Avatar and `ApplyRootMotion=false`; its seven root-motion scalar channels
  are not applied. All present Transform targets resolve by the original relative-path CRC.
  Exactly one target hash, `1390682545`, is absent from every fan and every relative Transform
  path in `level40`. Its three P/R/S channels are listed as `absent_optional_tracks`; the identity
  of that absent object is unknown. Every other unknown target or unsupported binding fails export.
- Euler binding `customType` is the original rotation order. These clips use 0 = XYZ, while
  `Quaternion.Euler` assumes ZXY. The exporter converts to normalized quaternion samples using
  the original order, then the runtime interpolates quaternions. This was caught before deployment:
  the default order made the figure lie horizontally. References:
  [AssetRipper conversion](https://github.com/AssetRipper/AssetRipper/blob/master/Source/AssetRipper.Processing/AnimationClips/AnimationClipConverter.cs),
  [Unity RotationOrder](https://github.com/Unity-Technologies/UnityCsReference/blob/master/Runtime/Transform/ScriptBindings/Transform.bindings.cs).

`AirPitSpectator.Create` runs from the owned raid's spawn setup and only for `arena_airpit`. It
refuses duplicate actor roots. No Collider, Rigidbody, Player, Animator, loot, interaction or combat
scripts are copied. The design scene owns the actor. Meshes, textures and materials are disposed
on scene teardown, and explicitly on a failed build before first activation. Failure emits one
actionable Warning and leaves the raid playable. Normal success/cleanup is Debug.

The Release client build stages `spectators/arena_airpit/` beside the DLL, including DLL-only
builds. This is derived art/animation data, not an AssetBundle; it follows the existing plaintext
sidecar policy. Existing encrypted map bundles and Arena installation checks are unchanged.
`validate_distribution.py` validates this package and compares every file with the extracted source.

## Offline evidence

- Fourteen focused tests: valid positive control, malformed weights/indices/matrices, hierarchy,
  geometry and animation arrays, rotation-order regression and the locally extracted package.
- Client Release: zero warnings, zero errors. Full distribution validation passes: four maps,
  12 encrypted bundles, plus the new spectator package.
- Independent CPU skinning: exported idle frame 0 differs from the direct source XYZ evaluation
  by at most 0.000000481 m; cheer frame 37 by 0.000000506 m. Idle AABB:
  min `(9.577953, 8.506471, 13.613264)`, max `(9.988947, 10.351312, 14.264227)`.
- Sanctioned deployment via `deploy-to-spt.ps1`: 138 files, 25 new, one changed (client DLL).
  All 25 spectator files and the client DLL were independently hash-matched on GPU-2 before start.

## Native evidence — SPT-GPU-2, 2026-10-27

Fresh EFT process started 18:02:59, instance `4e70dc61`, SPT-GPU-2 test profile, EFT
0.16.9.5 / Unity 2022.3.43f1. All 16 local/host/loaded plugin identities matched. Two plain
PMC AirPit raids, bots None, clear weather and stopped 12:00 clock.

- Both starts: zero warnings and zero errors. Exactly one `AirPitSpectator` instance in
  `Arena_Airpit_DesignStuff`; original position and rotation retained. Head renderer used the
  native shader, original `Head_BOSS_Sanitar_d`, 716 vertices and three bones.
- Visual check first from the ground, then a **test-only teleport onto the inaccessible gallery**
  for close inspection. This does not make that level playable or change the DynamicMaps layout.
  The selected original position is partly hidden by corrugated metal from some ground angles.
- 18 named game frames over 10.7 s show idle, cheer and return to idle, with inspector state
  beside them (`work/spectators/animation-evidence.json`). The body remains upright; hands,
  arms and torso visibly move; clothing, face and equipment textures render normally.
  `work/spectators/screenshots/idle-cheer-comparison.jpg` combines unchanged crops of frames
  00, 05 and 08. Full frames remain beside it.
- Both exits: cleanup lines at 18:10:23.065 and 18:13:22.638, zero actor instances in the menu.
  Second raid: exactly one new instance, fresh transition counter, animations running again.
  No duplicate; no ArenaMapPort Warning/Error throughout the process. Left in the main menu.
- Steady second-raid window: 119.6 fps, mean 8.4 ms, p95 8.3 ms. Both early windows also
  contained a roughly 1.9 s load/startup hitch; this was not isolated against a baseline and
  is not evidence of the spectator's performance cost. No claim of a crowd-scale benchmark.
- The 184 existing door-pose tests also pass with `PYTHONIOENCODING=utf-8` (the first invocation
  hit an existing subprocess output-encoding mismatch, not an animation assertion).

No release or upload was made. The deployed prototype is on branch `feature/airpit-spectator`.

## 2026-10-27 - restore the complete original AirPit crowd

The user approved all original spectators after the single-fan prototype. This supersedes the
prototype's one-fan scope; the earlier findings and evidence above remain historical.

### Source and package

- Fresh `level40` / `sharedassets40.assets` census: 99 children of `SCAV_SPECTATORS`, 15 appearance
  variants, five active first-LOD renderer slots per fan: 495 total (455 skinned, 40 static).
  Every source ID, placement, rotation, scale and renderer path was compared independently with
  the output. The crowd container itself is disabled in the source; its individual fans are active.
- 329 slots contain a usable mesh; 166 mesh references are null. Matching meshes are recovered
  from the owner's current Arena character/equipment bundles. The base heads' additional bones
  are existing spine, shoulder, neck and head nodes, not new facial bones. All skin bindings use
  `Mesh.m_BoneNameHashes` in bindpose order; scene renderer bone arrays contain obsolete/extra
  entries on some jackets. Single-influence skins such as ushanka/pidorka omit their weight
  channel in Unity; the explicit single blend index implies weight 1, then channels are padded
  to four influences for the target Unity version.
- Recovered head, Wild_Body_3, cowboy and Kotton materials were checked against the current
  bundles: their decoded diffuse/normal/specular pixels match the scene textures. The current
  leather cap needs its paired material. The old `mask_0_lod1` lower-face cloth could not be
  found in current Arena raw assets, candidate equipment bundles or previous extracted inputs.
  Its 27 instances (variants 03, 05, 11 and 12) use the current original Shroud half-mask and its
  paired p0 material. This is a visual substitution, not a claim of exact legacy-mesh recovery.
  The alternative full-head balaclava was rejected because it changes the intended face coverage.
- Schema 2 stores all placements/skeletons and shares 39 meshes, 33 materials, 103 textures and
  two animation clips. The original idle/cheer clips and XYZ Euler conversion are retained.
  Both clips have the same ordered path/kind bindings, explicitly validated for blending.
  Owner-derived art stays in ignored `work/extract/spectator-airpit` and staged `spectators/arena_airpit`;
  no new asset bundle, source assets in git, or change to the installation/encryption gate.
- The package is 124 files / 153,821,690 bytes. Estimated uncompressed RGBA8 texture storage with
  mipmaps is about 512 MiB; textures are shared once across the entire crowd.

### Runtime and offline checks

`ArenaMapPort_AirPitSpectators` is one manager in `Arena_Airpit_DesignStuff`. Each original fan has
its own transform hierarchy, shared renderer assets and deterministic independent animation phase
and idle interval. No global random state is changed. Visible poses update at 30 Hz, or 15 Hz beyond
45 m; frustum culling uses a fixed whole-person envelope independent of the renderer's last pose.
Coming back into view forces an immediate pose update from elapsed time. Source root motion stays
off. Meshes/materials/textures are disposed once with the scene, including the inactive-build
failure path. There are no bots, physics/colliders, loot, added sounds or match-event reactions.

Validation results:

- 28 focused tests: shared resources with distinct placements; missing/duplicate actors; bad mesh,
  texture and animation references; swapped but in-range bones; implicit weights; geometry and
  quaternion validity; the owner's complete export.
- Independent NumPy audit: exact source census match and all 15 variants at 29 poses each,
  including all-frame-selected cheer landmark extrema (435 pose checks). Heights 1.7915–1.8771 m;
  maximum root-bone-local extent 0.981 m and renderer-local extent 1.872 m, safely inside the
  conservative 6 m skin bounds. No invalid vertices or escaped actor-frustum bounds.
- Release client build: zero warnings/errors. Full distribution validator: four maps, twelve
  encrypted bundles and exact spectator package/staging equality. Existing bundles unchanged.

Local evidence: `work/spectators-crowd/source-census.json`, `cpu-audit.json`, the independent
`source_census.py` / `cpu_audit.py` helpers, `GATES.md`, `distribution.txt`, deploy dry-run and log.

### GPU-2 acceptance

The sanctioned deploy script copied the reviewed distribution to `D:\SPT41`; gpu-run verified all
125 client/package files on GPU-2 before restarting EFT. Fresh instance `e4fe774b`, started
19:01:27 local, reported 16/16 matching plugin identities. Two plain PMC AirPit raids, bots None,
clear weather, stopped 12:00 clock:

- Both starts: 99 fans / 495 renderers / 39 shared meshes / 33 shared materials; exactly one
  manager. Original idle/cheer phases vary between fans. Native material search confirmed 33,
  not a copy per figure. Both raid recipes reported zero warnings and zero errors.
- Inspected the east/west galleries, multiple upper levels from below, and the playable ground.
  The galleries were reached only by test teleport; no navigation or DynamicMaps changes.
  Recovered heads, hats, vest and the half-mask render normally. Turning 180 degrees away and
  back restores current animated poses without disappearing or frozen figures.
- Twelve stills over roughly ten seconds record varied animation; further close-ups show the
  recovered mask and vest. `screenshots/airpit-crowd-gallery-west.png`,
  `screenshots/airpit-crowd-halfmask-front.png`, `screenshots/airpit-crowd-from-playfield.png`,
  and `animation-evidence.json` are under the local evidence directory.
- Three steady 10 s windows (west gallery, playfield, second-raid spawn): 120 FPS, mean 8.34 ms,
  p95 8.33 ms, maxima 10.31–10.88 ms. This is the host's capped steady result, not an isolated
  spectator overhead measurement. Early raid windows still include ~1.9 s startup hitches,
  as already seen with the single prototype. Game-start times were 34.4 s and 26.4 s.
- Cleanup at 19:10:34.154 and 19:13:27.804: zero crowd managers and zero `[spectator]` materials
  in the menu after each exit. The second raid creates 99 anew with a fresh transition counter.
  No ArenaMapPort Warning/Error during the whole process. `first-raid-counts.json`,
  `second-raid-counts.json`, `perf-*.json`, `host-status.json` and `final-cleanup.json` retain evidence.

GPU-2 was left in the main menu with the profile healed. This change remains on
`feature/airpit-spectator`; no release, upload or push was performed.
