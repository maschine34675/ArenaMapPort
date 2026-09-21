# HANDOFF — dated working state

Snapshot for whoever continues (human or agent). Verify the commits named here against `git log`; this file
is updated by hand at each handover, it is not an automatic record. Read [AGENTS.md](AGENTS.md) first.

## State on 2026-09-21 — native acceptance completed at 08:32 CEST

**Bay5 ArenaMode integration and Equator's reflection cap are deployed and host-tested.
The final MapPort repeat also passed: corrected Bay5 NavMesh, both ordinary exits, and automatic
Equator indoor classification before and after Hideout in the same restarted process.**

| Repository / artifact | Accepted implementation |
|---|---|
| MapPort | `feature/bay5-and-equator-reflections`, code `d896214` |
| Bay5 Unity bundle | Built from `c95bdca`, `unity_build_all_20260921_074006.log`; 14,754 vertices / 6,816 triangles, natively verified at 08:27:48 |
| ArenaMode | `feature/bay5-map-integration`, code `75a7430`; `eb3537d` adds first-pass native evidence only |
| Artifact receipt | `D:/.unlazy/bay5-reflections/mapport-accepted-candidate-artifacts.json` (08:07 CEST; accepted by the later native repeat) |

The artifact receipt records the complete hashes. Client SHA-256 is
`9957897262AA1F5C0AE9E2BFFB2DA6B1FB80B1EE54F8DD711DDBCE63FFF92D34`; Bay5 NavMesh SHA-256 is
`5BA00920DFA3A5E9E5D33E05A3A08CD3B9B9959C31C83A87D2EE5FDCC5C00DEB`.
Fresh GPU process `c2a37398` (started 08:11:03) verified identical VM/host/loaded identities for all
25 maschine plugins and Redeploy Core. Equator automatically classified **607 indoor renderers /
1,103 clones** at 08:13:48 and again at 08:25:40 after an intervening Hideout visit; both raids
capped **7,022 material slots**, without F8. Hideout took roughly three minutes to become responsive,
then recovered without killing/restarting the process. Final Bay5 probes and both ordinary exits
passed afterward. The client was quit at 08:32 for log flushing; it is not left in a raid.

**Current implementation:** Equator and Bay5 each have four player starts, ten separate bot markers
in `BotZone_Main`, and two ordinary test exits. ArenaMode admits both maps independently for
Deathmatch/Gun Game with an optional MapPort dependency and guarded revision-2 default migration;
custom lists, disabled settings and intentional removals survive. Tactical and team modes are excluded.
MapPort's complete Python/server suites and distribution oracle passed after integration; the full
distribution remains 14 maps / 42 bundles / 14 shader sidecars / 14 server base files plus two DLLs.
The focused reflection/lifecycle oracle passed **55 tests** and a zero-warning/error client build.
ArenaMode's independent verifier passed **691 server tests, one optional APBS skip**, its native
source-linked harnesses and five fault controls. Offline checks, the first ArenaMode pass and the
final MapPort repeat have separate evidence; the final ordinary raids did not repeat DM/GG.

### Native history that remains valid

- **Equator ArenaMode, 2026-09-20:** both modes admitted, PMCs replenished from the atrium, real combat
  deaths respawned (two Deathmatch, one Gun Game), and protected return restored the entire saved
  profile except the background hideout timestamp. Gun Game reached tier 1/24 only. See
  [equator-arenamode-integration.md](docs/progress/equator-arenamode-integration.md).
- **Bay5 first pass, 07:29–07:45 on 2026-09-21:** the older **15,045 / 6,945** NavMesh passed all
  fourteen authored spawn-point probes and connected the northern depot opening and warehouse.
  South extraction completed after eight seconds at 07:33:45. Native Deathmatch and Gun Game
  replenishment, combat respawns (0.63 / 0.62 seconds) and protected return passed. Entire normalized
  before/after profiles matched after the final persistence flush; Gun Game remained tier 1/24.
  Those tests precede the new bake and north-exit correction. See the append-only
  [Bay5 integration checkpoint](docs/progress/bay5-arenamode-integration.md).
- **Equator reflections, 06:52–07:04:** same-view cap 1 → 0.30 → 1 reduced/restored the armored van
  and sandbag sky sheen; the already-damped lounge parquet stayed unchanged. F12 section
  `Lighting (arena_equator)` / `ReflectiveSpecularCap` defaults to 0.30; 1 restores uncapped inputs.
  Bay5 defaults to 1 (disabled). See [equator-specular-cap.md](docs/progress/equator-specular-cap.md).

### Fixes confirmed in the final loaded-build repeat

- Bay5's original spawn boxes remained non-trigger on native `Triggers` layer 13: physically ignored
  by the player but baked into ghost NavMesh islands. `c95bdca` excludes those collider owners;
  exactly 20 old Bear/Usec boxes are affected, no mesh floors. The new **14,754 / 6,816** bake passed
  the old depot sample (52.7, -3.8, -6.3), all fourteen authored spawn points, adjusted runtime bot
  positions, both exits and additional depot/warehouse connectivity and standing-room probes.
- The first north-exit runtime trigger sampled an upper floor 5.6 m above its authored point.
  `fce614a` prefers a nearby floor before the legacy wide search; `30c3e3d` moves north to the
  clear loading door at (96, -2.9252, 61), with descriptor-compatible decimal spelling in `d896214`.
  Both exit triggers sampled the nearby floor with 0.0 m logged displacement. North and south
  completed ordinary extraction at 08:28:58 and 08:31:03 respectively, reaching ExitStatus and then
  the menu. The duplicate eastern depot gate remains closed; interior connectivity passed.
- Rain-camera subscription reattachment alone did not fix every reused-donor startup path.
  `26dd9f2` adds a raid-ready cover bootstrap; `d514957` reuses successful camera initialization
  instead of allocating rain resources again on that camera. Both automatic Equator classifications
  passed around the Hideout transition in one fresh process, without a manual diagnostic render.

The final results are recorded in [Bay5 integration](docs/progress/bay5-arenamode-integration.md),
[Equator reflection evidence](docs/progress/equator-specular-cap.md) and ignored
`D:/.unlazy/bay5-reflections/native-c2a37398-evidence.json`. Navigation used coordinate-assisted
probes, not a complete walk-through. Gun Game remains a tier-1 smoke test; no full ladder,
long-session or performance acceptance is claimed.

**Other twelve maps:** built, validated and staged in `dist/`, but still without authored gameplay
bots/exits and without ArenaMode acceptance. Customs, Shoreline and Woods also await terrain runtime
validation. Earlier "inherited exclusions" describe the original build pass; additional gameplay
integration proceeds one map at a time through [docs/MAP-RECIPE.md](docs/MAP-RECIPE.md).

**Installs:** `D:\SPT41` is the deployment mirror of the GPU host and holds the accepted build.
`D:\SPT41vanilla` was not updated in this Bay5/reflection pass and retains its earlier stage-1 build;
verify its artifacts separately before testing there. Use the authorized deployment script, not
manual copying. Keep the first-pass history and final-repeat evidence separate when continuing.

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

Next gameplay work: continue one additional map through [docs/MAP-RECIPE.md](docs/MAP-RECIPE.md),
then tackle a terrain map. Bay5's current acceptance is complete; do not extend it to the other
twelve maps or to untested full Gun Game ladders and prolonged sessions.

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
- The GPU host runs SPT 4.1.6 / EFT 0.16.9.5-40743 for the documented 20/21 September acceptance.
  The source server dependency pin still targets 4.1.5; the vanilla test bed was not verified this pass.
- The user's `maschine-ArenaMapDebug.dll` (EQUATOR DEBUG button, penalty-free disconnect) is outside this
  repo and outside version control; host tests rely on it.
