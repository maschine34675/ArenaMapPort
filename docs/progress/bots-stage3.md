# Bots — stage 3: PMC squads on Equator

Goal (user, 2026-09-20): after scavs (stage 1) and exits (stage 2), PMC bots that spawn and fight on the map.

## How SPT 4.1.5 puts PMCs into an offline raid (read-only workflow + critique, 2026-09-20)

- Not from `waves` and not from a conversion chance (bot.json/pmc.json carry no `convertIntoPmcChance` any more):
  PMCs are `BossLocationSpawn` entries with `BossName`/`BossEscortType` pmcUSEC/pmcBEAR. Factory ships four
  (`BossChance` 50, `Time` -1, `IgnoreMaxBots` true, escort amounts "0,0,0,0,2,2,2,1,1,1,1,1,0,2,3").
- `PmcWaveGenerator.ApplyWaveChangesToMap` (server, every raid start) only strips/replaces those entries for maps
  listed in pmc.json `customPmcWaves`; an Arena key is absent, so what base.json ships is what spawns.
- Client: `LocalGame.cs:199` runs the boss scenario unconditionally; `BossSpawnScenario` fires `Time -1` entries at
  raid start after the chance roll; an explicit `BossZone` name is resolved by `FindZoneWithCorrectName` over every
  BotZone, the empty-name path needs `BotZone.HasPmcBotSpawns`, which comes from spawn points with the
  `BotPmc` category (`SpawnPointsCollection.CalculateCanSpawnPmcBots`) — both are set here.
- spt-custom's `CustomAiPatch` indexes pmc.json `pmcType[pmcbear|pmcusec][map]` without a guard for PMCs
  (`GetPmcWildSpawnType`); unknown map → KeyNotFoundException per PMC, caught and logged, brain stays native.
- Hostility: `BotLocationModifier.AdditionalHostilitySettings` is inherited from the Factory template (pmcBEAR,
  pmcUSEC, assault blocks) and SPT layers pmc.json `hostilitySettings` on top by BotRole — not map-keyed.

## What was built

- `tools/maps/equator.bots.json`: `boss_waves` (2 × pmcUSEC, 2 × pmcBEAR, zone BotZone_Main, chance 50) and
  `categories: ["Bot", "BotPmc"]` on every bot spawn; `make_base_json.py apply_boss_waves` emits Factory-shaped
  `BossLocationSpawn` entries (`Time` -1, `IgnoreMaxBots` true, `SpawnMode` regular+pve, `RandomTimeSpawn` false).
- Server: `ApplyPmcBrainTables(pmcConfig.PmcType, map)` copies the stand-in row (interchange) into both PMC brain
  tables; `PmcConfig` injected. Tests: `BossLocationSpawn_ShipsPmcSquadsOnTheAuthoredZone`,
  `ApplyPmcBrainTables_WritesTheStandInRow_IntoEveryPmcTable`.
- Client: nothing — the stage-1 repairs are role-agnostic.

## Host test 1 (2026-09-20 14:06, server 30d5b357, client 1dfa2f02) — a BEAR squad at raid load

Server restart: 14 maps registered, no warning. At raid load the server generated `15 pmcBEAR(pmcBEAR) normal`
and `15 pmcUSEC(pmcUSEC) normal` bots next to the assault/marksman batches (no `Failed to generate`, no APBS
error). Before the countdown ended: `BotOwner.Create -> 'Bot1' startCorePoint=0`, `ActivateBotCallback:
bot='Bot1' role=pmcBEAR`, then Bot2 and Bot3 (`pmcBEAR`) five seconds later — one boss wave with two escorts;
the two USEC entries lost their 50 % roll this raid. No warnings, no `CustomAiPatch` error, no `can't find
zones to spawn boss`. `uibridge_player`: `Bots alive: 3 · Zersus (pmcBEAR) 36.5m, Helldiver (pmcBEAR) 39.8m,
ClaudioIonck (pmcBEAR) 64.2m`. Stage 3 reached: PMC squads spawn on Equator through the same machinery
Factory uses, with no client change.

Observed on the host: the user's own pitTeam mod patches `PmcBearLayer`/`PmcUsecLayer.ShallUseNow` and PMC
friendly fire — unrelated to this port, noted because it shapes PMC behaviour in these tests.

Open: PMC squads ignore `BotMax` (`IgnoreMaxBots`, as on Factory) — with scav waves on top a small map can get
crowded; a longer raid should show how many bots are alive at once. Hostility towards the player follows the
inherited Factory `AdditionalHostilitySettings` (Bear/Usec `ChancedEnemies`), untested in combat here.
