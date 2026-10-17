# AirPit team boxes: what is still missing (offline analysis, 2026-10-17)

ArenaMode's Team DM spawns both teams into Arena's original TeamFight enclosures ("team boxes") and opens the gates when
the fight starts. Equator was first; Bay5 was accepted on 2026-10-17 (ArenaMode `cfa3f55`, `docs/BAY5-TEAM-BOXES-REVIEW.md`
there). This log records what AirPit still lacks, on both sides, as found by a read-only pass over ArenaMode (its
`ArenaTeamBoxCatalog`, mutator, runtime and the Bay5 write-up), this repo's AirPit data and code, the extracted package
`work/extract/airpit-native-v8` and the built NavMesh `work/build/airpit/arena_airpit_navmesh.bundle`. Nothing below was
observed in-game on AirPit; the GPU host was in use by another session, so the SpawnGates hold run on AirPit is still open.

## How Bay5 was fed (the recipe an AirPit formation follows)

- ArenaMode's catalog points equal `tools/maps/<map>.modes.json` `spawn_markers` in file order (north[0..9] then
  south[0..9]); Bay5 and Equator: 20 of 20 points match to 1e-5 m and 5e-5 deg. The first ten points carry the USEC flag.
  The exact digits come from the package scene transform; `modes.json` rounds to 1e-4 and writes tiny negative yaws as 0.0.
- The two `Bounds` are the `SPAWN_BOUND_FIGHTER` interiors (x/z trimmed inward by 0.01 m, y rounded); `InsideBooth` and the
  held-gate path exception use them, so wider boxes are more permissive.
- `Donor` must be an existing base.json bot record with an exactly equal position, zone `BotZone_Main`, core 0, delay 4,
  categories {Bot, BotPmc}; `Destination` was a host-surveyed point that snaps to the NavMesh within 0.3 m; the two
  anchors are box points reachable from every other point of the same box by a complete local path while held.
- `Infiltration` is the map's infiltration name; `BoothExits` are ArenaMode's own labels for the gate rays; `ModeId`
  `teamdeathmatch`; `CapacityPerTeam` 10; `ArenaParticipantPolicy` caps a team-box map at 20 (`Applies`).
- Acceptance trial on GPU-2: departures of both teams, respawns, gate logs, Equator regression.

## AirPit data that exists today

Source: `tools/maps/airpit.modes.json` (schema in `airpit-release.md`), checked against the package scene transforms.

| Box | World x | World y | World z | Size | Centre |
|---|---|---|---|---|---|
| North `SPAWN_BOUND_FIGHTER` | -36.4535 .. 13.5058 | -0.6160 .. 4.9724 | 69.1574 .. 85.1583 | 49.96 x 5.59 x 16.00 | (-11.4738, 2.1782, 77.1579) |
| South `SPAWN_BOUND_FIGHTER` | -22.8132 .. -0.4916 | -0.6160 .. 4.9724 | -80.4125 .. -64.4116 | 22.32 x 5.59 x 16.00 | (-11.6524, 2.1782, -72.4121) |

Catalog form by the Bay5 rule: north `Bounds(-36.45, 13.50, -0.62, 4.97, 69.16, 85.15)`, south
`Bounds(-22.81, -0.50, -0.62, 4.97, -80.41, -64.42)`. Every marker lies inside its own box (smallest margins: north
y 0.72 m, south y 0.70 m, z 5 m or more).

| idx | Marker (`SPAWNS/ScavPMCspawn (n)`) | Side | x | y | z | yaw (modes.json) |
|---|---|---|---|---|---|---|
| 0 | 6 | N | -12.5900 | 0.102 | 79.2100 | 180.15 |
| 1 | 7 | N | -15.1087 | 0.102 | 76.0892 | 180.15 |
| 2 | 8 | N | -17.1914 | 0.102 | 80.0678 | 180.15 |
| 3 | 9 | N | -22.4150 | 0.102 | 79.6166 | 180.15 |
| 4 | 10 | N | -19.1300 | 0.102 | 76.2600 | 180.15 |
| 5 | 16 | N | 0.3263 | 0.102 | 78.2969 | 180.15 |
| 6 | 17 | N | -2.8136 | 0.102 | 75.1826 | 180.15 |
| 7 | 18 | N | -4.8963 | 0.102 | 79.1612 | 180.15 |
| 8 | 19 | N | -10.2440 | 0.102 | 76.7390 | 180.15 |
| 9 | 20 | N | -6.8350 | 0.102 | 75.3534 | 180.15 |
| 10 | 1 | S | -12.57 | 0.080 | -74.48 | 0.0 |
| 11 | 2 | S | -15.22 | 0.121 | -74.40 | 0.0 |
| 12 | 3 | S | -18.14 | 0.093 | -74.29 | 35.44 |
| 13 | 4 | S | -9.94 | 0.123 | -74.40 | 0.0 |
| 14 | 5 | S | -7.27 | 0.137 | -74.58 | 0.0 |
| 15 | 11 | S | -11.34 | 0.080 | -71.89 | 0.0 |
| 16 | 12 | S | -13.99 | 0.121 | -71.81 | 0.0 |
| 17 | 13 | S | -16.91 | 0.093 | -71.70 | 0.0 |
| 18 | 14 | S | -8.71 | 0.123 | -71.81 | 0.0 |
| 19 | 15 | S | -6.04 | 0.137 | -71.99 | 323.50 |

Exact values for a catalog entry come from the package scene (`work/extract/airpit-native-v8/scenes/Arena_Airpit_TeamFight_DesignStuff.json`,
`SPAWNS/ScavPMCspawn (n)`), not from this table: the north yaw is -179.85196 deg (`NativeYaw` 180.14804), the south
yaws of the straight markers are -0.0000017 deg, which `Point.NativeYaw` keeps negative on purpose and `modes.json` prints
as 0.0. Never flatten y; the south markers sit 0.08 .. 0.137 m above a floor collider 0.06 .. 0.12 m below them.

Other fields: `Infiltration` = `ArenaAirPit` (sidecar `infiltration_name`, the base.json Player records and both
exits); faction split by analogy with Equator and the base.json Player spawns (USEC at (-1, 58.5), BEAR at
(-9.94, -74.4)): USEC north, BEAR south, not pinned by any Arena datum. Same-side marker spacing: north 3.41 m,
south 2.63 m (ArenaMode's occupancy radius is 1.5 m). `BoothExits`: none of the two exit boxes touches a team box
(north gate box z 60..64 lies south of the north box, south gate box z -60..-56 north of the south box). Donor
candidates among the stable base.json bot records: `bot-center-east` (-3.21, 0.1, -12.09), `east-mid` (19.12, 0.1, -2.63),
`west-mid` (-40.86, 0.1, 3.22), `plane-south` (-15.51, 0.1, -37.64), `south-apron` (-14.72, 0.1, -59.28); the points
moved repeatedly in `airpit.bots.json` are a poor choice because the mutator pins the position. Destination candidates
on the main NavMesh component with border clearance (needs an in-game `uibridge_teleport dryRun` survey, and must
avoid every carver footprint while held): hall origin (0, 0.1, 0) 1.8 m, `plane-south` 4.5 m, `west-mid` 3.8 m,
`south-apron` 3.9 m. The capture point (-13.21, -0.05, 1.29) is off the mesh (the airliner stands there).

## South box: data-ready, sealed by BUS_DOORS alone (offline)

- `BUS_DOORS` holds six door assemblies with four layer-18 colliders each (24 carvers). The three arena-side assemblies on
  the z -65.0 face are authored closed, the three pocket-side assemblies on the z -68.16 face authored open.
- Offline carve model (collider AABB + 0.2 m per side, + 0.5 m up, three independent grids): a separate pocket of about
  130 .. 139 m2 (x -20.0 .. -4.2, z -77.7 .. -65.2) with all ten south markers inside and no cell outside the native
  box; the arena-side doors do the sealing, dropping any one of them opens the leak, the pocket-side doors alone do not seal.
- Markers on the shipped mesh: main component, surface y 0.05, border clearance 1.15 .. 3.15 m (tightest marker 3).
- Predicted false warning: `SpawnGates.ProbeOnce` probes +-1.5 m from each part's union centre; for the three open
  assemblies the centre falls into the uncarved gap between two leaves (about 1.3 m, the carve adds 0.2 m per side), so the
  probe will likely report "a gate is NOT sealed" although the pocket is sealed overall. Check on the first AirPit hold.
- Throughput: on the built mesh the only crossings of each bus row are three strips of about 0.9 m, two doors in series;
  ten bots plus the player leave single file.

## North box: two blockers on this side

1. **No NavMesh.** `bake_bounds` is `[-57, 37, -2, 8, -83, 70]` (sidecar and descriptor), the shipped mesh ends at z 70.0,
   the ten north markers lie at z 75.18 .. 80.07: no polygon within 5 .. 10 m. This follows the 2026-10-12 decision
   (`2a1e0ce`: the closed passport control is the north limit; the PMC north spawns moved south of it). Arena's own mesh
   covers all twenty markers. A rebake needs the z limit raised to at least 83.6 (the `authored_borders` reach that; the
   north box ends at 85.16, the `north-facade-gap` wall stands at z 82.5), sidecar + C# descriptor in one commit, the bake
   pins and tests, `build_all.ps1 -FullMap` (about 15 min), restaged bundles, an EFT restart, then `dryRun` of all ten
   markers. AGENTS.md's "z -83..83" was stale and is corrected with this log.
2. **No gate.** `PASSPORT_CONTROL_DOORS` (16 `Passport_control_01_Door` leaves, layer 18, 1.09 x 1.37 m each, two rows at
   z 66.24 .. 66.35 and 69.07 .. 69.17 in eight lanes: x [-21.18,-20.09], [-17.77,-16.68], [-16.22,-15.12], [-12.81,-11.72],
   [-11.25,-10.16], [-7.85,-6.76], [-6.29,-5.20], [2.08,3.17]) was taken out of `opened_barriers` by that decision, so
   `BoothDoorRepairs` never offers it and `SpawnGates` can neither hold nor release it. With the leaves present a player
   in the north box is locked in (walk model: 0 cells outside the box, pocket 924 m2); without them the pocket opens
   through exactly the eight lanes. Arena's own mesh carries eight lane strips through z 66 .. 69.4 (27 polygons),
   contrary to `airpit-release.md`'s "no polygons in that band". The Arena-faithful design: leaves excluded from the bake,
   registered as a start gate (held + carved per leaf collider until Release) through a separate "team gate" list,
   because `opened_barriers` entries vanish at raid start in every mode. Open with it: in non-TDM raids the leaves either
   stay solid on a mesh that runs through them (the shutter-trap picture) or must be carved permanently; and the leaves
   are only 1.35 m high, so a vault out of the box during Assembling is possible unless EFT's vault limit is lower.

## Hold scope is map-wide

`SpawnGates.Hold` re-closes every offered `opened_barriers` part: on AirPit 37 parts and 35 carvers (BUS_DOORS 6/24, the
two `Prison_Metal_Fence_01` portal gates 1/1 each, `Airport_Shop_BS_closed_closed` 6/1, `DonB_Ramen_closed` 4/1 and its
twin 4/1, `TH_Roll_door_01..03` 4/1 each, `Cube (10) Group` 3/3). During a hold the three shop shells reappear over the
furnished shops with whole-footprint carvers (about 1,200 .. 1,700 m2 of NavMesh removed), the portal fences close and
the blockout plates rise; everything pops at Release. Not a crash, but wrong for Team DM and it would invalidate any
destination or anchor inside those footprints. Fix direction: a per-descriptor subset of start gates (AirPit: BUS_DOORS
plus the north gate; Equator and Bay5 keep "all"), honoured in `Offer`/`Hold`, sidecar + descriptor in one commit; the
reflection API stays.

## Decisions the user owns

1. North start: the original north box (rebake, north gate, hold subset; reverses the 2026-10-12 north-limit decision)
   or a non-original box on the existing passport-hall mesh (z 56 .. 66, where the Player spawns sit), or an asymmetric
   interim (ArenaMode checks both teams when `Applies` is true, so that is not data-only).
2. Whether the extended mesh may stay reachable in every mode (the same bundle serves Deathmatch, Gun Game, Tactical).
3. Hold subset vs the map-wide hold.
4. Native bounds (Bay5 rule) or pocket-fitted bounds (south x -20.2 .. -3.9, z -77.8 .. -65.2; north x -31.5 .. 8.8,
   z 69.5 .. 82.2 as an approximate suggestion).
5. Donor, destination and anchors after a `dryRun` survey.
6. Sequencing: ArenaMapPort changes and a new version first (SpawnGates `93afc19` is on main but not in `v0.2.1`; without
   the type ArenaMode's shim returns null and the boxes run ungated), then the formation, tests and the GPU-2 trial. With a
   formation enabled before the mesh and gate exist, every AirPit Team DM start aborts ("USEC box has 0/N safe places").

## Still to observe on the host

- A plain AirPit raid with `Spawns/SpawnGateHold = true`: registration (10 entries, 37 parts), 35 carvers, the probe
  verdicts (the predicted bus-row warning), the release probe, whether Unity widens the carve by the agent radius.
- After the rebake: `dryRun` of the ten north markers and the anchor; isolation while held, connection after release
  (one reader predicts a join through the east-flank corridor x 10.2 .. 16.5, z 66 .. 70, another found a wall at
  x 8.6 .. 9.0); the vault test over the 1.35 m leaves; floor and headroom in the furnished north hall.
- Equator and Bay5 regression, hideout once, frame time.

## Follow-up 2026-10-18: the original north box implemented (user decision)

Implemented and host-verified the same evening (HANDOFF 2026-10-18; log quotes in `spawn-gates.md`'s follow-up).
- Blocker 1 (no mesh): `bake_bounds` z max 83 again, rebake U7 (11771 vertices / 5415 triangles). All ten north markers
  and the eight lane strips sit on the main component of the raw bake (`test_airpit_spawns.py`).
- Blocker 2 (no gate): `PASSPORT_CONTROL_DOORS` + `PASSPORT_CONTROL_EMISSIVE` are `held_gates`: closed at raid start in
  every mode, carved from the NavMesh install on, opened only by `SpawnGates.Release` after a hold. The worry above that
  the passport line could be walked around was wrong for package v8: the furnished booth row (`PASSPORT_CONTROL/
  Passport_control_01 (n)`, layer-18 colliders x -31.35..8.37) fills the floor between the lanes; host raid 5 of
  2026-10-12 saw open floor there on package v6. The 16 turnstiles are the only openings.
- Hold scope: `start_gates` ["BUS_DOORS"]; the shop shells, portal fences and blockout panels are plain openings again.
- Host: plain raid 16 carvers, `every probed gate sealed (16 probed)`, north marker and lane strip unreachable; test hold
  40 carvers, sealed (22 probed), the release opens everything, the north marker reachable, a 22.5 m walk through a lane
  arrived. The south box's three authored-open inner bus doorways stay passable inside the sealed pocket; the probe
  verdict is per enclosure now, so they are listed as Debug, not warned about.
- Still open for ArenaMode: the formation (the numbers above stay valid), the GPU trial, the vault question.
