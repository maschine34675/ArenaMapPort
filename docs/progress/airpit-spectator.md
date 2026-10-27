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
