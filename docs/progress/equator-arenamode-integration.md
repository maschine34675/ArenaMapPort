# Equator ArenaMode integration (2026-09-20)

## Map data and distance coverage

The existing six bot markers all lay within 21 m of the atrium centre. ArenaMode's
direct injector rejects markers within 25 m of the player, so ordinary PMC waves
working on Equator did not prove continuous ArenaMode replenishment.

Added four ground-floor bot markers, retaining the 25 m minimum in ArenaMode:

| Marker | Position (x, y, z) |
|---|---|
| north-booth-east | (4.5, 0.1, 38.5) |
| north-hall | (-11.5, 0.1, 37.0) |
| south-west | (-9.0, 0.1, -28.0) |
| south-east | (8.7, 0.1, -28.4) |

The new All-side player marker at (-5.0, 0.1, -33.5) gives both PMC sides a
southern entry. There are now **10 bot markers and 4 player markers**. Existing
ordinary-raid waves, PMC squads, BotMax 8, exits and PMC-only/no-loot rules remain.
`tools/server/make_base_json.py` generated only additional SpawnPointParams.

### Native placement evidence

On the GPU host, SPT 4.1.6 / EFT 0.16.9.5 / Unity 2022.3.43f1, UiBridge instance
`92eb81cc` loaded the current MapPort client and the Equator NavMesh with 10,404
vertices / 4,572 triangles. The new points passed NavMesh, hazard-zone, complete
walking-path and standing-room checks from the USEC niche and atrium. All final
coordinates snap 0.0 m at the displayed precision. The original south-east trial
(9, 0.1, -28) snapped about 0.5 m; the accepted coordinates above were rechecked.

An initial atrium check happened after the test PMC died; those rejected requests
are not placement evidence. After a normal Redeploy respawn, the atrium checks
were repeated successfully before returning the player to the niche. This was
the ordinary EQUATOR DEBUG path, not an ArenaMode lifecycle acceptance test.

### Independent review and regression

Review rejected four other NavMesh-valid candidates that exactly overlapped
player respawn markers: ArenaMode's full-map respawn selector does not exclude
living bots at the selected coordinate. Those candidates were removed. Every
authored bot coordinate is at least 2 m from every player coordinate; this avoids
the deterministic overlap and does not claim to prevent a roaming bot from
approaching a player marker later.

The injector requests **two bots per burst**; `BurstMinAliveBots=4` is the desired
minimum live population. The regression checks at least **three distinct bot
points >=25 m away** at every 1 m sample in the conservative ground envelope
x=-25..25, z=-46..50. That envelope includes non-walkable samples; it proves
distance headroom, while the native probes above prove placement and paths.
The old data failed the coverage and both-side southern-entry assertions. Tests
also reject bot/player coordinate overlaps and stale generated server fields.

## ArenaMode boundary

The companion change in the ArenaMode repository admits `arena_equator` for
Deathmatch and Gun Game when ArenaMapPort is loaded and the server has an enabled
location. Its zone is `BotZone_Main`, BotMax 8, live-population floor 4. Tactical,
Team Deathmatch and PitFireTeam stay unavailable until their team starts are
authored and validated. The ordinary EQUATOR DEBUG button does not arm ArenaMode.

## Offline checks

- Final MapPort Release server tests: 267 passed.
- Final Python suite: 86 passed, including 4 placement-specific tests.
- Full distribution: 14 maps, 42 uniquely named bundles, 14 shader sidecars; staged database and DLL checks passed.
- Distribution-sidecar repair is documented in [distribution-sidecars.md](distribution-sidecars.md).

## Deployment and integrated GPU acceptance

Pending at this checkpoint; no ArenaMode runtime acceptance is implied by the
ordinary placement probes or the isolated builds.
