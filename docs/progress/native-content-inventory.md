# Native content inventory — what of Arena's Equator and Bay5 is still not ported (2026-09-24)

Question (user): "was sich bei den beiden Maps noch portieren lässt". Method: a fresh MonoBehaviour class
census over BOTH maps' source scenes with `analysis/scripts/census.py` (read-only over the local Arena
install; Bay5 had no class census before — the script pattern is in the session scratchpad, rerun it via
`census.component_census` with the scene dict from `tools/extract/extract_bay5.py`), including the scenes the
extractor deliberately leaves out (TeamFight_AI, Sound, TeamFight_Culling, Scripts), cross-checked against the
EFT 0.16.9.5 decompile (`grep -rl "class <Name>"`) to see which classes EFT itself has, plus a five-reader
workflow over docs/progress since 2026-09-21, the client's runtime substitutes and the ArenaMode repo (read-only).
Rule that decides feasibility (docs/SPEC.md §C.7): from shipped Arena data only class name, world transform
and engine components (colliders, Light, Rigidbody, …) are recoverable; custom MonoBehaviour field data is not,
unless the class is a public package with a known layout.

## 1. Census — what the source scenes contain

Counts are MonoBehaviour instances per class (engine components in brackets). "imported" = in the accepted
packages `equator-native-dust` (5 scenes) / `bay5-native-dust` (9 scenes); "excluded" = scenes the extractor
skips on purpose.

| Class (EFT has it?) | Equator | Bay5 | Where | Ported today |
|---|---|---|---|---|
| `EFT.Ballistics.BallisticCollider` (yes) | 2,892 | 6,322 | imported | **yes** (native ballistics, 09-22) |
| `LocalDustParticles` (yes) | 20 | 13 | imported | **yes** (native dust, 09-23) |
| `Audio.AmbientSubsystem.AmbientSoundPlayer` (yes) | 25 | 17 (+6 season) | Sound scene | **yes** (native audio graph, 09-22) |
| `EFT.Visual.StaticDecal.StaticDeferredDecal` (yes) | 348 | 499 | imported | no — renderers hidden (`DecalRepairs`) |
| `SmallPhysicsObject` (yes) + `[Rigidbody]` | 391 + [413] | 18 + [22] | imported | no — props are static |
| `HotObject` (yes, thermal) | 305 | 348 | imported | no |
| `EFT.Interactive.WindowBreaker` (yes) | 5 | 147 | imported | no — user deferred glass (all panes inactive in Arena too) |
| `EFT.Interactive.BarbedWire` (yes) | 0 | 246 | imported | no — wire is decoration |
| `CullingLightObject` (yes) | 472 | 186 | imported | no (light culling) |
| `MultiFlare.FlareLight` (yes) + `LightFlicker` | 28 + 2 | 25 | imported | no |
| `VolumetricLight` / `AreaLight` (yes) | 17 / 13 | 1 / 0 | imported | no |
| `DoorHandle` (yes), `BrokenDoor` | 9 | 11, 1 | imported | no — doors static/opened |
| `Unity.AI.Navigation.NavMeshModifier` (+Volume) | 124 (+93 in AI scene) | 170 + 134 (+75 in AI) | imported / AI | no — bake ignores them (bay5-port.md's "379") |
| `EFT.Interactive.TreeInteractive` (yes) | 11 | 39 | imported | no |
| `EFT.Interactive.ExperienceTrigger` (yes) | 0 | 28 | imported | no (XP, irrelevant) |
| `EFT.Interactive.ExfiltrationPoint` / `Scav…` (yes) | 0 | 5 / 3 | imported | no — exits are hand-authored |
| `EFT.Water.WaterContainer` (yes) | 0 | 2 | imported | substituted (Bay5 native shoreline placement) |
| `Game.Modes.Data.ArenaFanEntity` / `…FanSpeakerEntity` (NO) | 99 / 20 | 88 / 14 | imported / Sound | no — Arena-only crowd |
| `Game.Modes.Data.Monitors.ArenaMonitorHolder` (NO) | 11 | 8 | imported | no — Arena-only screens |
| `…GateEntity` (NO), `ArenaSpawnBound` (NO) | 5 | 2 / 2 | imported | substituted (`BoothDoorRepairs`, `SpawnPlacementRepairs`) |
| `…EmissiveAnimator` / `DotAnimator` (NO) | 4 / 12 | 0 / 11 | imported | no |
| `Koenigz.PerfectCulling.*` (yes) | 7 groups (+16 in Culling) | 8+20+… (+40 in Culling) | imported / Culling | substituted (`PerfectCullingSkip`) |
| `AmplifyImpostor` (yes) | 11 | 39 | imported | no (baked atlas not shipped) |
| `[SkinnedMeshRenderer]` + `Diz.Skinning.*`, `PlayerBody`, `LimbIK` | 506 | 449 | imported | skipped by design (mannequins) |
| `PatrolPoint` / `AICorePoint` / `BotZone`+`PatrolWay` (yes) | 23 / 14 / 1 | 47 / 11 / 1 | **AI scene (excluded)** | substituted (`PatrolWayRepairs`, `AICorePointRepairs`) |
| `AIMinePoint` / `AIPlaceInfo` / `AIStationaryController` | 61 / 2 / 2 | 65 / 2 / 2 | AI scene | no |
| `AICoversData`, `AIDangerPlacesHolder`, `AIDoorsHolder` (holders) | 1 each | 1 each | AI scene | substituted (empty holders) |
| `Audio.SpatialSystem.SpatialAudioPortal` / `AudioTriggerArea` / `SpatialAudioRoom` (yes) | 55 / 51 / 23 | 89 / 69 / 44 | Sound scene | substituted (`SpatialAudioRepairs` skips occlusion) |
| `ServerSpatialPortal` / `ServerRoomColliderArea` / `BaseSpatialRoom` | 39 / 50 / 22 | 73 / 63 / 40 | AI scene | no |
| `EFT.EnvironmentEffect.DryPlane` / `IndoorTrigger` (yes) | 0 / 1 | **669 / 30** | **Scripts scene (never extracted)** | substituted (`RainCoverRepairs` depth photo; `WeatherObstacle` null) |
| `EFT.Airdrop.AirdropPoint` | 0 | 602 | Scripts | no (no airdrops) |
| TOD/weather rig (`TOD_Sky`, `WeatherController`, `WetRenderer`, …) | 1 each | 1 each | Scripts | substituted (donor rig, `WeatherRig`) |
| `[ReflectionProbe]` / `[WindZone]` / `[OcclusionArea]` | 1 / 1 / 3 | 1 / 1 / 3 | Scripts | no |
| `PostProcessProfile` (+ effect classes) | 3 | unknown | imported | substituted (donor camera) |
| `[ParticleSystem]` | 3 (+1 Scripts) | 17 (+1) | imported | no |
| `[Animator]` | 99 (mannequins) | unknown | imported | no |

Not present on either map (confirmed 0 by the census scan): Terrain, LightProbeGroup, NavMeshAgent/Obstacle,
baked lightmaps, mainline `CoverPoint`/`GroupPoint` (Arena never used them).

## 2. Ported since 2026-09-20 (do not propose again)

Native ballistic materials (2,892 / 6,322), native ambient audio graphs (25 players/11 banks/65 clips;
24 sources/13 banks/106 clips), original PBR materials, packed-normal import (519 normals), original dust
volumes (Alpha8 Texture3D + SPT's animation), Bay5 harbour background scene (7,090 objects), specular cap and
covered-floor partition, cubemap colour space, Bay5 barrier shadow proxies + DistantShadow cache fix, ArenaMode
base integration on both maps (4 player starts, 10 bot markers, 2 exits, Deathmatch/Gun Game admission,
respawn, protected return), Equator DynamicMaps ground plan. Evidence: HANDOFF.md 2026-09-21..23 and the docs
listed in INDEX.md.

## 3. Candidates — ranked by value for ArenaMode play over effort

1. **Ambient gain (S, both maps).** The user already listened on 2026-09-22: impacts fit, ambience "audible
   but very quiet". Root cause documented (low authored base gain × random per-raid deltas,
   bay5-concrete-lighting.md §user-audio-feedback). Add a per-map ambient gain override (neutral default),
   audition +6 dB, user listens again.
2. **Arena's own AI anchors from the excluded TeamFight_AI scenes (M).** 23/47 PatrolPoints, 14/11
   AICorePoints, one BotZone/PatrolWay per map — class + world position are recoverable
   (`census.spawn_zone_anchors` already does it for SpawnPointMarker). Feed them into `PatrolWayRepairs` /
   `AICorePointRepairs` instead of the 6/10 spawn-marker chains: denser patrols, real core points. Links,
   cover facing and the holder lists stay synthetic.
3. **Arena's rain-cover volumes for Bay5 (S–M).** The never-extracted Scripts scene holds 669 `DryPlane`
   and 30 `IndoorTrigger` boxes (BoxCollider bounds are engine data → recoverable). Authoring them replaces
   the depth-photo cover heuristic and the missing `WeatherObstacle` with Arena's own answer to "where does
   rain not fall". Equator has one `IndoorTrigger` (the whole mall) — check whether it covers the atrium.
4. **Physics props (M).** 391 `SmallPhysicsObject` + 413 `Rigidbody` on Equator (18 + 22 on Bay5): cups,
   bottles, boxes, monitors that Arena lets you kick and shoot. `Rigidbody` fields (mass, drag, constraints)
   are engine data; `SmallPhysicsObject` needs class + position only (its hit-sound enum is a nice-to-have).
   Verify settling at raid start (props dropping through geometry) in a host raid.
5. **NavMesh modifiers (M).** 124 + 93 (Equator) / 304 + 75 (Bay5) `NavMeshModifier(Volume)` mark hazards,
   ledges and machinery. The component is a public Unity package (stable layout), so area/cost fields are
   readable with a hand-built typetree; `BakeNavMesh` would then carve them. Read the values first — if they
   are all "not walkable" it is a one-line markup change.
6. **ArenaMode-side items** (the user's other repo, read-only here): pickup layout per map
   (PICKUPS-REVIEW.md: only Factory has built-ins; F12 capture tool exists), the round-state lighting
   director allowlist (ARENA-LIGHTING-REVIEW.md: Labs/Factory/Labyrinth only), and — the biggest ceiling —
   faction team-start pools for TDM/Tactical (`ArenaAreaCatalog.TryResolve` rejects imported maps outside
   Deathmatch/Gun Game; Customs needed 28 surveyed positions for 5v5).
7. **Spawn variety (S).** 61 (Equator) / 30 (Bay5) authored SpawnPointMarker positions are recorded in
   `anchors_reference`; ~14 per map are used. Curate more, verify each with `uibridge_teleport dryRun`.
8. **Locale names (S).** Both maps still say "(spike)" (`tools/maps/*.json` `locale_name`); base.json
   regeneration only.
9. **Barbed wire on Bay5 (M).** 246 `EFT.Interactive.BarbedWire` — contact damage exists in EFT; needs the
   trigger collider (present) plus default damage fields; gameplay-relevant near the harbour fences.
10. **Decals (L).** 348 / 499 `StaticDeferredDecal` renderers are hidden because the decal shader renders
    opaque; authoring the component with the imported mesh/material might make the deferred path work, but
    per-instance fields are unknown — a spike, not a plan.
11. **Flares and flicker (M, cosmetic).** 28 / 25 `FlareLight`, 2 `LightFlicker`, 17 `VolumetricLight` on
    Equator; classes exist in EFT; curves/textures per instance are not recoverable.
12. **Spatial audio rooms (L).** 23 / 44 rooms, 55 / 89 portals, 51 / 69 trigger areas with BoxCollider bounds
    in the Sound scenes; portal links and the acoustic bake file are not — check first whether Arena ships
    the bake at all (`SpatialAudioDataLoader` path).
13. **Arena's Bay5 exits (S, low).** 5 `ExfiltrationPoint` + 3 scav points exist in the source; positions could
    replace the hand-placed gates, but ArenaMode does not use exits.
14. **Reflection probe (S, low), PostProcessProfile (L, unverified spike).**

## 4. Not worth it or blocked

Breakable glass (user decision; 147 panes inactive in Arena as well). Crowd, fans, speakers, monitors, gates,
emissive/dot animators (`Game.Modes.Data.*` — not in EFT, would be a full re-implementation for decoration).
PerfectCulling (proprietary bake; no measured frame problem). Amplify impostors (atlas not shipped).
Mannequins/skinned rigs (no clips, scoped out). Arena's TOD/weather field data (donor rig works). Upper rings
(decision). `HotObject` (thermal only), `AirdropPoint`, `ExperienceTrigger`, `AIMinePoint` (no mines),
`TreeInteractive` (11 / 39 bushes), wind (1 zone), water renderer (procedural material).

## 5. Untested rather than missing

Ambient balance after any gain change (user's ears); lighting tuning values (user's eyes); full Gun Game
ladder; PMC crowding/BotMax on long sessions; Bay5's DynamicMaps pack provenance (installed by the user, not
produced by `tools/dynamicmaps`).

## 6. Open questions for the user

Which of the ArenaMode modes beyond Deathmatch/Gun Game are wanted on these maps (drives the team-start
survey)? Are pickups wanted on Equator/Bay5? Is bot quality (2, 5) worth a bake + AI-scene extraction round
before the next map? Should the Bay5 DynamicMaps pack be reproduced through the repo tooling?
