# HANDOFF — dated working state

Snapshot for whoever continues (human or agent). Verify the commits named here against `git log`; this file
is updated by hand at each handover, it is not an automatic record. Read [AGENTS.md](AGENTS.md) first.

## State on 2026-09-22 — Bay5 original materials and harbor ambience

**Built, deployed and natively verified.** Branch `feature/bay5-native-materials-glass-audio`,
based on `aa5e3cc`. User explicitly deferred glass: all 147 original panes are inactive, and no
glass activation or WindowBreaker port was added. The task restores 6,322 original ballistic
components and 24 native ambient sources / 13 banks / 106 original PCM clips. Three Linear
cubemaps retain their original color space. Original geometry, navigation and reflection knobs
are preserved. Independent source/package/bundle comparisons and reproducibility passed;
152 Python tests, 348 .NET tests and eight PowerShell cases passed. Final client build: zero
warnings/errors. Original assets remain ignored; proprietary audio is not a cleared release asset.

Only two of 72 installed files changed; all other maps, the server and NavMesh are byte-identical:

- Client SHA-256: `24F9753F97D160CD59380EE2442C7EF15A233A5C2350A7ADFC660D68BB1AB015`.
- Bay5 scenes SHA-256: `3957E836C63C0F76AD8433DDC2236706C76E4E36893A945AA98B9A382E58E261`.

Final process `90bc532c` (08:29:03 CEST) confirmed VM = host disk = loaded. Real WoodThin,
MetalThick and Concrete projectile hits selected original values, with no default fallback on
those targets. Native Summer audio started 23 players and retained the one missing seasonal
bank as silence. Water was correctly placed before any movement and followed the native shoreline;
sample advancement, nonzero source RMS and both mixer routes were verified. Two SPT shoreline
initialization defects were corrected only inside the owned Bay5 graph. The host OS output is
muted: this is technical playback evidence, not an auditory mix-quality approval.

The Bay5–Equator–Bay5 sequence passed. Both Bay5 exits stopped 24 players, released 24 generated
clips and reported zero cleanup failures; the revisit retained exactly 24 sources. Equator had
no Bay5 audio and kept its accepted reflection settings. Both Bay5 teardowns restored 642
renderers / 1,008 clones; Equator restored 607 / 1,103. Bay5 used its ordinary South exfil;
the Equator Debug disconnect remains scoped to debug-started sessions. Final profile health
was 440/440, diagnostics were restored to false, and EFT was exited normally around 08:44.

Full source/runtime reasoning, native timestamps, caveats, screenshot paths and local receipts:
[Bay5 native features](docs/progress/bay5-native-features.md). Next: user hearing/visual approval,
then one further map through the existing map recipe. Dust/extra decals, full room acoustics
and glass are separate follow-ups; no additional feature expansion is implicit.

## State on 2026-09-21 — Bay5 covered-floor reflections accepted at 22:13 CEST

**Bay5's covered Hangar floor now has independent reflection and smoothness controls, built,
deployed and tested in-game.** Branch `feature/bay5-indoor-materials`, based on `9f8911e`.
The render-only partition corrects covered floor portions inside mixed building meshes; original
wall/decal/outdoor slots are retained. All four Hangar LODs follow the same route. F12 section
`Lighting (arena_bay5)`: `IndoorSurfaceRepair=true`, `IndoorSurfaceReflectionStrength=0.20`,
`IndoorSurfaceSmoothness=0.50`. Existing Bay5 cap `0.2957746` and legacy settings are preserved.
Other maps default to the new surface path disabled.

Final client SHA-256:
`8F8E79C808BDF7D1054C68180443DFE6F82E61CE4F3AD7A84BDB223AF3593CBA`.
Of 72 deployed artifacts only that client changed; 71 remain byte-identical to the accepted
background delivery. No scene/NavMesh/server/ArenaMode changes. Release build had zero
warnings/errors; 348 tests passed and independent review found no blocking issue.

Fresh GPU process `851020e4` (21:53:32) verified matching VM, host and loaded identities. Bay5
automatically partitioned 32 render meshes including LODs / 21,032 covered triangle fragments.
Enabled classification took 602–642 ms in six observed passes. Same-view material and independent
knob controls, exterior on/off control, glass false/true plus F8, and the Bay5–Equator–Bay5 sequence
passed. Both final Bay5 teardowns restored 642 renderers / destroyed 1,008 owned material clones.
Equator retained 607 renderers / 1,103 clones with the new path off. No MapPort warnings/errors in
the recorded final process. EFT returned to MainMenu and was exited normally at about 22:13.

The covered concrete is less reflective. Original puddles still reflect strongly, and small
material transitions can remain around roof openings: cover is sampled, not exact polygon
clipping. No new interior-reflection renderer or exhaustive all-distance LOD walk is claimed.
Full evidence, local screenshot paths, algorithm limits and receipts are in
[Bay5 covered-floor reflections](docs/progress/bay5-indoor-materials.md).

The next gameplay step remains one additional map through [the map recipe](docs/MAP-RECIPE.md).
For further Bay5 visual tuning, use the two new controls independently; authored puddles are
outside this repair. Historical gameplay/profile acceptance below was not repeated by this task.

## Previous acceptance on 2026-09-21 — Bay5 harbor background at 11:56 CEST

**Bay5's original harbor background is built, deployed to the GPU host and tested in-game.**
The native views show the northern gantry, southeast portal crane and original container stacks.
Bowl was left unchanged after the user confirmed its stadium is present. Equator's accepted
reflection/lifecycle behavior below remains the baseline; its bundles and base are byte-identical.

| Repository / artifact | Current accepted implementation |
|---|---|
| MapPort | `feature/bay5-background`: background `4f2025e`, legacy preset guard `2ba4d71`, bot marker `b6e0692` plus its generated server base |
| Bay5 Unity bundle | `unity_build_all_20260921_104434.log`; 10 loaded scenes, 22,190 NavMesh vertices / 10,248 triangles |
| ArenaMode | Unchanged implementation `75a7430`; no files edited in this task |
| Final receipts | `D:/.unlazy/bay5-background/distribution-after.json`, `distribution-delta.json`, `install-final-verification.json`, `native-final.json` |

Client SHA-256: `6E12B25466F156A977F39DF3ABA44FA8AF61A6D54D9BCFEE789942B46B8140FD`.
Bay5 NavMesh SHA-256: `CD7A3308E9D21F54F4FCF19242114B97E3952FB452F2E1F6ADAF0082D7DE64F3`.
Bay5 base SHA-256: `CEAA30A95521FB80FEE39A623B43E69FF5FA5D32F70914A1A4DF50993A76775C`.
All 72 deployed files matched their staged hashes; seven intended files changed and 65 remained
byte-identical. The vanilla test install was not updated.

Fresh process `d1121cb1` (11:48:44 CEST) confirmed all 25 plugins plus Redeploy Core identical
across VM, host disk and loaded code. At 11:52:47, the ten bot markers formed **one connected
NavMesh component, zero isolated/off-mesh points**. The northeast marker is now three metres
south at (106.267, -3.8574, 61.7033), avoiding an automatic move onto an isolated container-side
patch. Four player starts and all ten effective bot positions passed full path and standing-room
checks. Deathmatch bots moved, fought and replenished; protected return preserved the full saved
profile except the background hideout timestamp, including the final flush. EFT was quit at 11:56.

Both ordinary exits completed in process `23dee6d0` on the **same scene/NavMesh bundles** before
the final single bot-position change. RainBounds and indoor classification stayed unchanged.
Background colliders participate in navigation, while distant renderer bounds do not enlarge
the bake volume or weather scans. The skyline is included, but floor-view visibility was not
established. No new Gun Game ladder, long-session or performance acceptance is claimed.

Full build, native evidence and the separate ArenaMode HUD warning are recorded in
[Bay5 background acceptance](docs/progress/bay5-background.md). That warning comes from
`GrenadeInventoryCounter.CollectSummary` during kit setup; this task does not modify ArenaMode.
Next gameplay work remains one additional map through [the map recipe](docs/MAP-RECIPE.md).

## Previous acceptance on 2026-09-21 — 08:32 CEST (historical artifact identities)

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
