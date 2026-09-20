# HANDOFF — dated working state

Snapshot for whoever continues (human or agent). Verify the commits named here against `git log`; this file
is updated by hand at each handover, it is not an automatic record. Read [AGENTS.md](AGENTS.md) first.

## State on 2026-09-20 (evening)

**Equator (`arena_equator`) — what is verified on the GPU host:**
- Raid loads from the vanilla menu and from the user's EQUATOR DEBUG button; weather rig, lighting and
  indoor-floor repairs active (`docs/progress/donor-settings-camera-weather.md`, `daytime-and-visuals.md`).
- Scavs spawn, activate and patrol (`bots-stage1.md`, commits `b2fbf96`..`e514779`, `97c8091`); PMC squads
  via `BossLocationSpawn` (`bots-stage3.md`, `cdc83b5`). Six silent gaps had to be closed for that — read
  stage 1's "what it took" before touching bot code.
- Two runtime-authored exfils with green smoke signals, raid end via extraction verified (`exfils-stage2.md`,
  `9cf4b3f`, `cdc83b5`).
- Spawn enclosures opened at raid start: five sliding-door assemblies and the FCK roller shutter
  (`0514d02`, `e2218f7`; descriptor `OpenedBarriers`).
- The Usec spawn is a niche in the FCK hall (walls north/east, a sheet south, open west into the hall).

**Other 13 maps:** built, validated and staged in `dist/` (`docs/progress/remaining-maps.md`); Bay5 was raided
for visuals only; nine were never loaded in a raid; no bots/exits sidecars → BotMax 0, no waves, no exits.
Terrain maps (Custom, Shoreline, Woods) still need their first runtime validation. `remaining-maps.md`'s
"inherited exclusions" (no bots, no cover, no match rules) describe that build pass, not a boundary: stage 2
adds bots/exits per map through [docs/MAP-RECIPE.md](docs/MAP-RECIPE.md).

**Installs:** `D:\SPT41` (mirror of the GPU host; deploy target) and `D:\SPT41vanilla` (user's test bed).
Both hold the stage-1 build (client + rebaked Equator bundles); see `git log` for the commit.

## Decisions (user, 2026-09-20) — see docs/SPEC.md "Decisions 2026-09-20"

ArenaMode is the target; no loot; no scav raids; bundles are distributed as built; Equator first.
The full gap analysis behind the stages below lives in the user's session notes; the tiers are:
(1) combat/death correctness and the NavMesh bake on Equator, (2) the other maps through the Equator
recipe, (3) parity only where ArenaMode needs it, (4) release hygiene.

## Stage 1 — done on the host (2026-09-20 evening), one item left for the vanilla install

| Item | Status | Evidence / where |
|---|---|---|
| `EffectsController.OnHealthDied` null-guard on the Cam2 camera (player death) | built; installed and harmless on the host (death is intercepted by Redeploy there), the Cam2 path itself needs a raid on D:\SPT41vanilla | `EffectsControllerRepairs.DeathScreenEffectsGuard`, `HarmonyPatches.InstallDeathEffectsGuard` |
| Bullets ignore LowPolyCollider-only props (13 on Equator: racks, rubble, fences, booth revolving doors) | verified 20.09. 19:03 (13 twins added, `stage1-combat.md`) | `BallisticTwinRepairs.cs` (`[Combat] BallisticTwins`), runs from `SpawnInfiltrationRepairs`' postfix |
| NavMesh bake kept booth doors, `SPAWN_BOUND*`, the FCK shutter and the 41 TeamFight `LevelBorder` walls → islands for bots | verified 20.09. 19:33: ground floor incl. booths and FCK hall is one component (`stage1-combat.md` host test 2) | `BakeNavMesh.RoundOpenedReason`, `ArenaMapDescriptorData.opened_barriers`, `tools/maps/equator.json` |
| Host raid: death path, PMC hostility in combat, bot pathing into the booths | done 20.09. (PMCs hostile via SAIN combat layer, killed the player; booths reachable) | `docs/progress/stage1-combat.md` |
| Extraction UI look during the countdown | not inspected (de-prioritised, ArenaMode target) | — |
| Survived/KIA post-raid screens, insurance | de-prioritised (ArenaMode target) | — |

Next after stage 1: Bay5 through [docs/MAP-RECIPE.md](docs/MAP-RECIPE.md) (bots/exits sidecars, host raid),
then one TeamFight map, then a terrain map.

Release (stage 4, not started): `dist/` is what gets shipped, zipped as-is (BepInEx/ + SPT_Runtime/ trees);
no packaging script, CHANGELOG, LICENSE file or version bump exists yet — `PluginVersion`/`ModMetadata.Version`
are 0.1.0.

## Pending user actions

- Restart the `D:\SPT41vanilla` server after a server-DLL/base.json deploy (client-only changes need only an
  EFT restart).
- Decide when to bump `PluginVersion` (still 0.1.0) and add a LICENSE/CHANGELOG (stage 4).

## Environment facts an agent cannot infer

- GPU host `WIN-Q470OL2A14A`, EFT via SPT launcher, profile `ArenaGPU20260908`; Parsec keeps the A2000
  display alive — do not switch displays. Host server terminal: closing it is how the server restarts.
- SPT 4.1.6 was released 2026-09-18; both installs and the server mod pin are still 4.1.5.
- The user's `maschine-ArenaMapDebug.dll` (EQUATOR DEBUG button, penalty-free disconnect) is outside this
  repo and outside version control; host tests rely on it.
