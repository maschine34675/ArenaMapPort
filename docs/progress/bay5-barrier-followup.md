# Bay5 barrier shadow and live-material cache follow-up — 2026-09-23

Baseline `d842ef8`, branch `feature/bay5-barrier-followup`. The user accepted the preceding
normal-texture correction, reported one remaining barrier without a shadow, and supplied
`analysis/screenshots/schatten/Screenshot 2026-09-23 063604.png` / `063614.png`, the live log
timestamp 06:43:22.826, and the prior session's repeated exception from 06:33:30.276.

## Barrier: an authored inactive shadow proxy

06:43:22.826 is the lighting snapshot; the related F10 inspection begins at 06:43:23.884.
It hits `SBG_Arena_Bay5_Main/SOO/PROPS/Military_sandbag_barrier_03`, at approximately
(61.625, -3.893, 8.659). Visible LOD0–3 have shadow casting Off. This is normal for objects
with separate ShadowsOnly proxy meshes, but all three proxies on this particular assembly
are inactive. An independent read compared original Arena `level84`, the current extracted
package and the installed/staged Bay5 bundle: this is already authored that way in Arena,
not lost during extraction or normal import.

Source root25426 / LODGroup95271 references:

| LOD | Visible object | Shadow object | Shadow source state |
|---|---|---|---|
| 0 | 14549 | 23900 | inactive, renderer enabled, ShadowsOnly |
| 1 | 14643 | 11947 | inactive, renderer enabled, ShadowsOnly |
| 2 | 12110 | 15629 | inactive, renderer enabled, ShadowsOnly |
| 3 | 19486 | none | source-authored absence |

`ShadowCasterRepairs` restores only the existing disabled direct shadow children listed in
that assembly's original LODGroup. It requires a valid mesh and the actual `shadow` shader;
the native shader has a real ShadowCaster pass. The exact assembly path is declared in
`ArenaMapDescriptor.RestoredShadowCasterRoots` and mirrored in `tools/maps/bay5.json`.
All other maps have empty repair lists. No visible meshes, colliders, materials, navigation,
LOD membership or far LOD3 behavior are changed. Scene unload destroys the instances normally;
the repair keeps no cross-raid scene references. Both data mirror and native result are checked.

## Repeated exception: retired material references in DistantShadow

The actual archived path is `D:/SPT41/BepInEx/logs/LogOutput-2026-09-23_06-38-23.log`
(one filename, not a dated subdirectory). SHA-256:
`3AF85230F2B6E756309FD7790821E5C36690954EEBFB948B58BD6EFA20D8C1A5`.
It contains 20,583 `DistantShadow.Update` / `CommandBuffer.DrawMesh` exceptions for null
`material`, from 06:33:30.276 to 06:36:32.151. The first follows an indoor reflection live
reapply at 06:33:30.004 and specular-layer refresh at 06:33:30.198.

Local SPT decompilation establishes the mechanism: `DistantShadow.InvalidateRenderers` takes
its own snapshot of mesh/material references. `Update` later submits those cached references
without another null check. Our live material/mesh replacement restores renderer slots and
destroys owned clones, leaving that snapshot stale. The public invalidation method cannot
safely be called directly mid-cycle: it also submits draws and does not reset pending indices.
Setting only its dirty flag is insufficient while either pending counter remains nonzero.

The scoped `DistantShadowCache` helper resets both verified pending counters, clears queued
geometry commands and asks the native update to rebuild its snapshot. Notifications cover
reflective replacement/restoration, indoor material and split-mesh replacement/restoration,
and the newly restored barrier proxies. It runs only while the corresponding owned map is
current, checks the exact field types, skips assets/unloaded objects and preserves enabled
state. No global Update patch, swallowed exception or disabled shadow system is used.

Four regression tests exercise the actual helper and reflective lifecycle against a small
native-scheduling model, including a negative dirty-flag-only case that reproduces the retired
material exception, pending command clearing, repeated/nested changes and ownership exclusions.
These tests supplement the real-game checks; they are not evidence of Unity rendering behavior.

F10's material summary also checks `_MainTex` before reading it: layered-ground and shadow
shaders lack that property and Unity otherwise prints diagnostic-only errors without throwing.
All actual texture slots remain listed in the detailed inspection.

## Build and package checks

Client Release: zero warnings/errors. Server/shared/client-lifecycle suite: 353/353 passed,
including four cache regressions and the barrier descriptor guard; four pre-existing nullable
test warnings remain. Python: 217 tests, 190 passed / 27 expected skips. Descriptor validation
and full distribution validation passed: 14 maps, 42 bundles, 14 sidecars. Only the client and
server DLLs change among the 72 distribution files; the latter includes the additive shared
descriptor data. All bundles and server database files remain byte-identical.
Receipts are under `work/verify/bay5-barrier-followup/` (ignored local evidence).

## Deployment and native acceptance — 07:03–07:22 CEST

The sanctioned deployment script copied the 72 staged files; final manifest comparison found
exactly two changed identities and all installed hashes equal to staging. Client SHA-256:
`4F19A4F53C9CCF44BDE696ED007E738115F76DE933213EBC22769787C08C67B6`;
server: `5AA79EAACAC8C98D9EFD31CBD2B1160F63BA37ABF4B315AE09E3B66AA36CAAE7`.
The server and EFT were restarted normally. Fresh EFT instance `71873626`, started 07:03:50,
reported all 23 maschine plugins and Redeploy.Core identical across VM, host disk and loaded code.

- **Exact barrier:** all three ShadowsOnly proxies were restored at 07:07:43.916–917,
  again at 07:16:46.522 and 07:20:40.636–637. F10 at 07:17:10.976 reproduced the user's
  unsuffixed target and showed active native shadow proxies. The final botless comparison
  at 07:21:06.699 used player (54, -3.9, 8), yaw85.1 / pitch5 and again hit that exact collider.
  `barrierExactTarget.png` shows the barrier casting a solid shadow onto the adjacent ground.
  The neighboring `(1)` assembly is unchanged. An earlier northern angle hit that neighbor,
  so `barrierAngleFinal.png` is not used as proof for the repaired assembly.
- **Archived trigger repeated:** Bay5 cap1 → 0.3, indoor reflection1 → 0.87, cap0.3 → 1,
  another cap1 → 0.3, cover50 → 51 → 50 (full material/mesh reclassification), indoor
  reflection0.87 → 1 and cap0.3 → 1. Native cache rebuilds were logged after restoration
  and replacement. Equator subsequently loaded and passed cap0.3 → 0.4 → 0.3; Bay5 loaded
  again twice. Across the sequence there were 25 cache-invalidation log entries and no
  `DistantShadow.Update`, `DrawMesh` or null-material errors. F10 produced no missing
  `_MainTex` errors, including inspections of the native `shadow` shader.
- **Settings preserved:** the user's current Bay5 values were already back at defaults on
  entry. Final cap1, indoor reflection1, cover50, hands multiplier1 and ground multiplier1
  match that starting state; Equator cap remains0.3. Previous milestones' local0.35 hands /
  ground2 / reduced Bay5 cap values were not reapplied. No new config setting is needed.
- **Log boundary:** this was not an entirely error-free EFT session. There were three other
  errors: microphone initialization at07:04:28, AsyncUploadManager close at07:05:34 (before
  the first raid), and an inactive MenuScreen coroutine at07:10:11 during menu return.
  Those messages differ from the reported repeated shadow exception; none repeated during
  the later map/load tests. A deliberate RespawnApiProbe subscriber exception also appears
  as a warning during the recovery below. These unrelated diagnostics were not modified.

### Test incident and recovery

The second Bay5 visit unexpectedly reset the normal offline bot selection to online defaults.
A PMC bot killed the test character at07:17:17.249. This was disclosed immediately; the existing
Redeploy UI respawned the character at07:17:53.950 and the debug disconnect returned to the menu.
No profile files or third-party settings were edited. A final visit explicitly selected bosses
off and bot amount None through the ordinary menu; UiBridge confirmed zero living bots.

Final in-raid and saved-profile checks: 440/440 HP, 100 energy, 100 hydration and the original
knife/secure container. Read-only comparison against the server's pre-incident backup found
all 142 non-structural inventory item IDs preserved. Redeploy regenerated the equipment/pockets
roots and the next raid regenerated the dogtag; those automatic identities were excluded from
the item comparison. Gameplay history was not rewritten. The final raid ended normally;
EFT exited through EXIT/YES at about07:22, with bridge unreachability confirmed. The server
remains running and the GPU host is free.

Native receipts: `finalStatus.json`, `finalConfig.json`, `barrierNativeLog.json`,
`targetInspection.json`, `nativeErrors.json`, `barrierFinalPlayer.json`, `profile-check.json`
and `barrierExactTarget.png` under the ignored evidence directory. The close-view PNG is a
target-identification aid; the wider, F10-confirmed final image supplies the shadow evidence.
