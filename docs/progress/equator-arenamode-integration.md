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

### Completed host acceptance, 2026-09-20 23:02-23:18 CEST

This later checkpoint supersedes the pending deployment status above. MapPort
`9547b0b` and ArenaMode `22ff21655c4620d429c5b4585074baa273b7ef9b`
were built cleanly, deployed to `D:\SPT41`, mirrored to the GPU host at
`<gpu-test-host>:C:\SPT41`, and both server and EFT restarted. MapPort deployment
used `tools/deploy-to-spt.ps1 -SptRoot D:/SPT41 -Confirm`. ArenaMode promotion
changed only its client/server DLLs; 123 other inventoried files were unchanged.
No config was manually edited. UiBridge instance `4a8bd804`, started 23:02:19,
reported all 25 maschine plugins and Redeploy Core identical across VM, host
disk and loaded assemblies. Host: SPT 4.1.6 / EFT 0.16.9.5-40743.

| Artifact | SHA256 |
|---|---|
| MapPort client, unchanged | `D736A39BD4DACFC982BB9DE12A7E8162F61FDC25A52A9EEA9149E562CAAD8292` |
| MapPort server | `73D51278422D0C814A0D4F2DF9D2ED76828CE82237BCF98B381F2371214C8D6B` |
| Equator generated base.json | `8C2D5F007F52C6BA79FD2AE47B4304CFFEF55C76E413963675978585EF5D0830` |
| ArenaMode client | `3E9E1DE4B96402EECBE18FD029280C67F282C410576117713BF8A750DCDD2266` |
| ArenaMode server | `F0F33D562B9F5339B9E85A0B20E110B382FCD1EA72ED1DC98D7532C7BC920A0E` |

Both tests used the native ESCAPE FROM TARKOV -> ARENA -> Equator flow.
Deathmatch and Gun Game show Equator. Switching a selected Equator to Tactical
hides/deselects it and removes NEXT; Team DM also hides it. Switching back to
an admitted mode restores the tile. PitFireTeam rejection is covered offline.

- **Deathmatch:** armed 23:05:43.994 (#2908), active 23:06:13.682 (#3019),
  round 1 started 23:06:15. With the player moved to the atrium (0, 0.2, 2),
  injector plans found four legal distant points and dispatched two PMCs per
  burst at 23:06:39 and 23:06:42 (#3564-3586). Up to eight bots were alive,
  with active combat/pathing. Natural combat deaths at 23:07:14.941 (#3616)
  and 23:07:39.542 (#3670) respawned in 0.52 s and 0.16 s with new kits,
  full 440 HP and death counters 1/2. Further replenishment was requested at
  23:08:01 and dispatched at 23:08:04 (#3741-3750). Native Stop(Left) at 23:08:35 returned through
  ExitStatus to MainMenu at about 23:09:22.
- **Gun Game:** armed 23:14:40.895 (#3885), active 23:14:57.483 (#3967),
  native HUD tier 1/24 with PB pistol. Active bots and another two-PMC burst
  from four legal atrium candidates at 23:15:53 (#4142-4149). Natural death
  at 23:16:24.478 (#4179) respawned in 0.51 s with 440 HP and the same PB
  ladder rung. Stop(Left) at 23:16:47.869 (#4227), MainMenu about 23:17:21.
  This proves start, replenishment, death/respawn and return; no kill-driven
  ladder advancement or completed ladder was tested.

The loaded map reported ten zone markers, all ten on NavMesh, 98 patrol
subpoints, one connected component and zero off-mesh points. The existing
`SpawnPlacementRepairs` moves the authored south-west bot point from
(-9, 0.1, -28) to (-7.5, 0.05, -28) to clear its expanded booth bounds. This
was stable across respawns; the repaired point also passed UiBridge NavMesh,
zone, complete-path and standing-room checks. It is a runtime adjustment,
not evidence that every authored coordinate remains unchanged.

### Persisted profile protection

Compared the entire saved profile JSON, not just the HUD health. SPT backups
for profile `6a9fc7d99347b1276407f8ad` before Deathmatch (`21-05-44` UTC),
after Deathmatch (`21-08-36` UTC), before Gun Game and after Gun Game
(`21-16-49` UTC), plus the live profile saved at 23:19:22 after returning to MainMenu,
all match except `/characters/pmc/Hideout/sptUpdateLastRunTimestamp` (the
ordinary background hideout timestamp). Canonical JSON with only that exact
field omitted has SHA256
`7f5c0e9da100ef9dfa8f785c20b37f91377e85c94604edc2e6a20c1bbc2fde37`.
Inventory, skills, quests, XP, health, currency, PMC and Scav statistics match:
440 HP, level 2 / 1500 XP, 446169 roubles, 149 inventory entries, zero kills.
The UI's 55 raids/32 AWOL combine 54/31 PMC and 1/1 Scav.

The client initially reports `restored:false, deferred:true, state:InRaid`
before native raid-end finishes; this is not the final persistence result.
The local server log was buffered, so final restoration evidence is the
fresh timestamped before/after saved profiles. The installed diagnostic
`RespawnApiProbe.AlwaysThrow(8.5)` intentionally threw and was caught during
body replacement; existing PerfectCulling cleanup warnings were also present.
Neither blocked these tests. This is not a claim of a warning-free mod stack.

### Boundary and next work

Equator is deployed and smoke-tested for individual Deathmatch and Gun Game.
Team starts, long-session performance and full Gun Game ladder progression
remain separate acceptance work. ArenaMode retains its existing two-second
deploy countdown and ExitStatus screen; the separate EQUATOR DEBUG shortcut
continues to own its ordinary-raid shortcuts. The other 13 maps have not gained
ArenaMode admission or runtime acceptance through this change. Next map: Bay5
with the same geometry, spawn-distance, replenishment and protected-return
checks. `D:\SPT41vanilla` was not updated by this integration.
