# Original crowds on Equator, Bay5 and Bowl — 2026-10-27

User accepted the complete [AirPit crowd](airpit-spectator.md) and requested the same treatment
on the other three maps. This extends the existing decorative crowd; it adds no gameplay AI,
colliders, sounds, match-event reactions or settings. Equator day/night share the source scene.

## Source census and placement

Current Arena BuildSettings, not historical level indices, identify the design scenes:

| Map | Design level | Fans | LOD0 renderer parts | Null meshes recovered |
| --- | --- | ---: | ---: | ---: |
| AirPit | 40 | 99 | 495 | 166 |
| Equator | 53 | 99 | 495 | 165 |
| Bay5 | 81 | 88 | 440 | 148 |
| Bowl | 43 | 99 | 495 | 166 |

Independent scans of all map-associated BuildSettings entries found one real `SCAV_SPECTATORS`
group per map. All maps reuse the same 15 appearances and original shared animation controller
(`Parapet=false`, generic Animator, root motion disabled). The accepted idle/cheer clips and XYZ
Euler conversion remain unchanged. Missing meshes use the previously verified AirPit recovery
rules. The absent legacy lower-face cloth uses the documented current Arena Shroud half-mask:
27 instances on AirPit, 26 on Equator, 23 on Bay5 and 27 on Bowl.

Bay5 differs: its source crowd is active and translated by
`(104.146446228, 6.381745338, -16.224702835)`. The other containers are disabled scene roots with
identity transforms. Schema 3 therefore requires the source scene and container transform;
individual fan placements remain local to that container. Non-root containers and scaled roots
fail extraction until reviewed. The runtime applies container placement before building skeletons
and world-space visibility bounds. Wrong-map and old-schema packages are rejected.

The imported Bay5 root still contains static accessories and 1,056 colliders. After the replacement
has loaded successfully, `ArenaSpectators` disables only the old `SCAV_SPECTATORS` root in that
owned design scene. If construction fails, the original group stays as it was. Equator, Bowl and
AirPit originals remain disabled. Existing gameplay geometry and navigation are unchanged.

## Shared runtime and distribution

One `ArenaSpectators` manager per loaded map owns 39 meshes, 33 source materials, 103 textures,
two sampled animations and the individual skeletons. Names include the map key and repeat creation
is guarded in the design scene. Deterministic independent phases, 30/15 Hz visible pose updates,
off-screen time progression, bounds and scene-owned disposal are retained from AirPit.

Each map has a validated owner-extracted package under `work/extract/spectator-<stem>` and is staged
under `spectators/arena_<stem>`. The full file graph, map/scene identity, exact texture set and staged
bytes are checked. Art remains ignored; encrypted bundles and the Arena installation gate are
unchanged. Release classification now includes the exact spectator manifest and texture filename
shapes in each map's part, requires each manifest, and rejects stray/parked-map files. No release
archive or publication is part of this change.

## Offline evidence

- Focused validation covers map/scene mismatch, stale schema, translated container, invalid TRS,
  reordered BuildSettings, source count, skeleton/mesh/clip references and every real package.
- Independent raw-source and NumPy audits passed on all four packages: exact world placements,
  all source renderer slots and native mesh channels, 15 appearances and 435 deformed poses per
  map within the existing bounds. All 44,376 sampled animation values per map match native curves
  with independently composed rotation matrices (maximum error approximately 1.33e-15).
- Negative controls detect an omitted Bay5 container (104.146 m error) and wrong Euler order
  (matrix error 0.284). They modify only in-memory audit fixtures.
- Release client build, full distribution validation and release packaging fixtures pass. The
  classification smoke test covers all 609 staged files with none unclassified.

Ignored evidence: `work/spectators-allmaps/GATES.md`, per-map source census/audit reports,
`package_audit.py`, `*/independent-audit/`, and `audit-negative-controls.json`.

## GPU-2 acceptance

The sanctioned deploy script copied 609 distribution files to `D:\SPT41`; gpu-run verified all
497 client DLL/crowd files on GPU-2. The unchanged server DLL also matched before the server start.
Fresh game instance `d19997e6` loaded client MVID `183ff357-4e67-46f2-b5c1-0d1edcb6e23c`, matching
the built/deployed DLL (SHA-256 `A327A640BE94A1C77848FB2E575CA8331E579A16B31E844B310BACB6648DF08C`).
The final status still reported 16/16 matching plugin identities.

Five consecutive plain PMC raids, bots None:

| Raid | Fans / renderers | Shared source materials | Steady FPS / p95 | Maximum frame in that 10 s window |
| --- | ---: | ---: | ---: | ---: |
| Equator day | 99 / 495 | 33 | 120 / 8.33 ms | 9.36 ms |
| Equator night | 99 / 495 | 33 | 120 / 8.33 ms | 10.32 ms |
| Bay5 | 88 / 440 | 33 | 120 / 8.33 ms | 9.11 ms |
| Bowl | 99 / 495 | 33 | 120 / 8.33 ms | 9.81 ms |
| AirPit regression | 99 / 495 | 33 | 120 / 8.33 ms | 9.60 ms |

Equator used the real 12:00/00:00 menu selections; the other maps used a stopped 12:00 test clock
and clear weather. Each raid had exactly one manager; idle/cheer phases differed between figures
and transition counters advanced. Bay5 reported one original active group disabled; the inspector
confirmed its root inactive. Equator day's existing SpecularCap repair additionally creates 41
owned accessory material clones (74 total including the 33 shared originals); all of those are
removed on exit as well. No material-policy override was added for spectators.

Each of the five returns to the main menu left zero crowd managers and zero `[spectator]`
materials. The profile was healed after every disconnect. No ArenaMapPort warning/error in the
whole process. Engine messages were separate: Equator's refrigerator has duplicate LODGroup
warnings, and one `MenuScreen` inactive-coroutine error occurred during a menu return; all returns
completed. Raw evidence is retained in `engine-warnings.json`, `final-mod-warnings.json` and the
per-raid recipe outputs. This is not a claim that the entire engine log is empty.

The 120 FPS figures are capped steady observations, not isolated crowd overhead. Initial sampling
windows contained loading hitches of roughly 0.48–2.65 s; steady windows exclude them. No claim
that this work removes raid-start hitches. No bots or multiplayer performance was exercised.

Screenshots: `equator-day.png`, `equator-night.png`, `bay5.png`, `bowl-east-stands.png`,
`bowl-east-stands-2.png`, `airpit.png` in the evidence directory. Bowl's crowd is much farther away
and often occluded by authored canopies/cover. A further independent comparison confirmed every
parent/TRS in its accepted main/design/building scenes and all 356 stadium meshes match the current
Arena source; no placement correction is justified. The eastern stands are visible from the
playfield near `(25.3, 0, -19.6)`, looking towards `(86.19, 19.2, -25.55)`. This pass used no gallery
teleport or navigation changes on Bowl.

36 spectator fixtures and 182 release packaging fixtures pass; all 11 acceptance gates are met.
GPU-2 is in the main menu. Changes remain on `feature/airpit-spectator`; no release/upload/push.
