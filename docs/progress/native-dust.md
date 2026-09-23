# Original ambient dust — 2026-09-23

## Scope and source findings

Restore the original moving dust on Equator, then Bay5. The user clarified that a Bay5
DynamicMaps map already exists; the installed `ArenaMapPort_Bay5` JSONC and `Layers/Ground.svg`
were also verified read-only. No map pack is created or overwritten in this task. Quiet
ambient audio remains a separate follow-up. No lighting/config defaults are changed.

Original Arena source and actual SPT shader serialization establish that both dust materials
use `Custom/LocalDustParticlesLighted`. Their compiled D3D11 pixel programs are identical:
opacity comes from the alpha channel of `_ParticleTex`; color comes from the authored mesh.
The old flattened preview was decoded using the wrong format enumeration. Texture3D
`m_Format=54` is the internal Alpha8 GraphicsFormat, not the Texture2D ASTC enum value.
A standalone Unity 2022.3.43f1 fixture confirms Alpha8 serializes as format54 / Linear and
samples `(1,1,1,alpha)`. R8 and RGBA32 controls serialize as different formats.

| Original data | Equator | Bay5 |
|---|---|---|
| Scene / exact owner | Arena_Equator_Lighting / EQUATOR_DUST | Arena_Bay5_Light / DUST |
| Source scene / parent component | level55 /4205 | level83 /1858 |
| Authored generated particles /vertices | 1,845 /7,380 | 1,534 /6,136 |
| Native TimeSpeed /Size | 0.09 /0.03 | 0.07 /0.04 |
| QuadTileSetSide /UvTilePadding | 1 /1 | 1 /0 |
| Source volume | sharedassets55.assets #19 | sharedassets6.assets #4 |

Both volumes are 16×16×8, one mip, 2,048 original alpha bytes, bilinear, clamp on all axes,
anisotropy0, mip bias0. Both payloads have SHA256
`fed03e9c0853ba52b4c51bbb84fa35a0187ef85d69a1320bcc5c6e7208e8ea38`.

Fresh source mesh exports are byte-identical to the accepted packages. Their positions,
tangents, vertex colors and both UV channels already encode every particle's placement,
billboard shape and motion phase. All source owners/components and their generator children
are active. The native generator components do not need to be imported or rerun.

## Implementation

The extractor writes a versioned `native_volume` manifest record and exact `.volume.bin`
payload. The PNG remains an inspection preview. Import validates provenance, dimensions,
mip offsets, byte count, SHA256, Linear color space and sampler settings before creating a
native Unity Alpha8 Texture3D asset. No color conversion, alpha processing, axis flip,
compression or mip generation is applied. Existing unsupported legacy volumes remain
unassigned. Historical texture IDs remain stable.

`NativeDustRepairs` attaches SPT's existing `LocalDustParticlesParent` only to the exact
descriptor owner with its valid original material, mesh and Texture3D. The owner is briefly
inactive while parameters are assigned. Empty generator children make native Awake bind
the isolated material and return before randomization or mesh replacement. Native Update
then supplies the game's original motion. Source padded bounds are restored on the renderer,
without changing the imported shared mesh. Unload/owner destruction releases the one owned
material and restores renderer state. Both dust renderers have shadows off and no LODGroup;
they cannot enter DistantShadow's cached LOD renderer list.

The first native Equator visit exposed a second defect: the opaque stand-in's queue2000 had
been written into the material sidecar and reapplied after rebinding to the real shader.
Original materials use custom queue-1, and the real dust shader uses Transparent/3000 with
ZWrite off. `NativeDustRepairs` resets only its owned clone to queue-1, restoring the native
shader default so later opaque geometry cannot overdraw particles. No other material's queue
or shader handling is changed.

Independent bundle validation compares original Arena bytes directly with extracted and
built Texture3D bytes, dimensions, format, color space and sampler state. It also requires
the material's volume slot to resolve to that exact Texture3D; merely including an unused
texture cannot pass. Negative controls cover alpha loss, reversed slices, wrong channels,
sampler/mip drift and missing or incorrect material bindings.

## Evidence and acceptance

Source audits, the standalone Unity probe, package preservation comparisons and build/native
receipts live under ignored `work/verify/native-dust/`.

Both complete build pipelines passed: mandatory MonoScript identity patch, extraction,
source-to-bundle comparison, preset scene references and registry consistency. All 519 packed
normal textures still match (212 Equator /307 Bay5). Each new scene bundle contains the exact
original Alpha8 volume and a verified dust-material binding. Navigation bundles and shader
sidecars are byte-identical to the preceding accepted build. The distribution validator passes
all 14 maps, 42 bundles and 14 shader sidecars.

Regression checks: 356 .NET tests passed; Python ran 249 cases, with 222 passes and 27 expected
skips; all eight PowerShell Unity-log tests passed. The final Release client build has zero
warnings and zero errors. Independent code review found no remaining correctness blocker.

Final staging changes exactly four of 72 files relative to the pre-task snapshot: client DLL,
server DLL and the two scene bundles. The other 68 files are byte-identical.

The candidate inputs are `work/extract/equator-native-dust` and `bay5-native-dust`, copied
from their accepted `*-native-features` predecessors and refreshed with
`python tools/extract/refresh_native_volumes.py <candidate-package>`.
Independent hashing finds exactly two changed files (manifest and volume preview) plus one
new `.volume.bin` per map: 3,716 Equator and 5,425 Bay5 existing files are byte-identical.
The manifests are identical after removing the volume extension and format-label correction.

Use absolute descriptor paths: Unity resolves relative paths from its project directory,
so the initial relative-path attempt stopped during script-scene authoring and was discarded.
That attempt was never staged or deployed.

```powershell
& tools/build_all.ps1 -MapKey arena_equator -MapDescriptor D:/ArenaMapPort/tools/maps/equator.json -PackageDir D:/ArenaMapPort/work/extract/equator-native-dust -OutputDir D:/ArenaMapPort/work/build/equator-dust -BakeNavMesh $true -Verify $true -SkipGameRefsSync
& tools/build_all.ps1 -MapKey arena_bay5 -MapDescriptor D:/ArenaMapPort/tools/maps/bay5.json -PackageDir D:/ArenaMapPort/work/extract/bay5-native-dust -OutputDir D:/ArenaMapPort/work/build/bay5-dust -FullMap -BakeNavMesh $true -Verify $true -SkipGameRefsSync
```

## Deployment and native acceptance — 2026-09-23, 11:43–11:54 CEST

Both candidate pipelines completed before promotion to the canonical build directories
(`work/build/full` and `work/build/bay5`) and `dist/`. Deployment used only
`tools/deploy-to-spt.ps1 -SptRoot D:/SPT41 -Confirm`, after its dry run. The server was
restarted after its DLL changed. The final client and Bay5 bundle were installed with EFT
closed; Syncthing was given time to finish before launching the final process. The four
changed installed files match the staged SHA256 values (`installed-final.json`).

Fresh EFT instance `3e1ca2da` started at 11:43:47. UiBridge reported all 23 maschine plugins
identical across VM build, host disk and loaded code. The final client SHA256 is
`20B0278914A6F895AA939D49C8062A9408579A5285890477973550D502C08C60`.
Bot amount was set to None and bosses off through the normal menu before each of three
short visits; every playable raid reported zero bots.

| Visit | Initialization /animation evidence | Native visual check | Unload |
|---|---|---|---|
| Equator | 11:46:10.168 /11:46:15.431; queue3000, Alpha8, 7,380 vertices; time advances across76 frames, same mesh | Original soft gray motes move against dark stalls at (-19.5,0.1,23), yaw0 | 11:47:14.438; one owned material released, remaining0 |
| Bay5 | 11:49:13.604 /11:49:17.136; queue3000, Alpha8, 6,136 vertices; time advances across101 frames, same mesh | Warm bright motes move in the hall at (60.6,-3.8,-9), yaw0 | 11:50:03.982; one owned material released, remaining0 |
| Equator revisit | 11:51:45.477 /11:51:48.900; correct original speed and queue, cached source mesh preserved across96 frames | Animation visible again at the same viewpoint after the Bay5 visit | 11:52:30.597; one owned material released, remaining0 |

The early animation log's `visible=False` is recorded while the map is still loading, before
the playable camera exists. Native visibility is established by the subsequent paired
screenshots, not by treating that loading-time flag as a visibility test. Original frame
pairs are saved as `eqFinalA/B.png`, `bayFinalA/B.png` and `eqRevisitA/B.png` under the evidence
directory. `native-receipts.json` records the relevant UiBridge state and logs.

No dust errors, missing-volume errors or lifetime failures occurred. One unrelated Unity
message appeared when Escape opened the first pause menu at 11:46:58.584:
`Coroutine couldn't be started because the the game object 'MenuScreen' is inactive!`
The identical message exists before this change in archived logs at 00:27:55.965 and
07:10:11.729 (`LogOutput-2026-09-23_00-32-46.log` and `_07-22-32.log`). It did not recur in
the other two visits. No global error-free claim is made; this existing menu issue was not
changed by the dust work.

All three disconnects returned directly to the menu. Final profile snapshot: 440/440 HP,
100 energy, 100 hydration; all 145 inventory records retain their IDs, templates, parent
relationships and slots. Other profiles' inventory records are unchanged. The ArenaMapPort
configuration is byte-identical to its pre-task copy, as are the installed Bay5 DynamicMaps
JSONC and SVG. EFT exited normally; the bridge confirmed the process was gone. Server remains
running and the GPU host is free. No profiles or third-party configuration files were edited.

This acceptance covers the original ambient particle effect and its map lifecycle. It does
not establish complete Arena lighting/weather parity or a measured performance comparison.
Audio gain remains unchanged; the user's quiet-ambience observation is a separate follow-up.
