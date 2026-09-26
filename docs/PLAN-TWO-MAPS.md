# Plan: Two Maps First — Equator and Bay5 to Release Quality

Source: `docs/progress/native-content-inventory.md` (2026-09-24 census) + seven read-only feasibility spikes run
2026-09-25 against the live Arena install (`D:/Battlestate Games/Escape from Tarkov Arena/...`) and the EFT
0.16.9.5 decompile, then a critic pass that re-ran the key reads (counts, byte-exact typetree reads) and whose
corrections are folded in. No repo file was changed, no Unity/dotnet build was run to produce this plan.
Status column: nothing below is implemented yet; update this file (or HANDOFF.md) as items land.

## 0. Decisions and scope

- **First release = Equator + Bay5 only.** The other twelve maps stay built and staged (`work/build/*`,
  `dist/`) but ship in no package (HANDOFF.md 2026-09-25, README.md:33-35, `docs/SPEC.md` "Decisions
  2026-09-20" addendum).
- **Map quality before ArenaMode features.** Everything below is about Equator/Bay5 fidelity: geometry,
  collision, physics, weather, audio, ambience. AI-behavior/pathing work that only pays off once TDM/Tactical
  or richer bot routing exists is ranked lower or dropped (§3).
- **No loot, no scav raids** — unchanged, out of scope for every item below; nothing here touches
  `DisabledForScav` or item spawns.
- **TDM/Tactical + pickups are wanted later**, but belong to the ArenaMode repo (`ArenaAreaCatalog`, F12
  pickup capture) and are read-only context here, not actionable in ArenaMapPort.
- **Glass stays deferred** (user decision; Arena itself ships all panes inactive on both maps too).
- **Bay5 DynamicMaps is already installed** (the user's own pack) — not an open item.

## 1. Ordered work items

Ordered by player-facing value on Equator/Bay5 divided by effort. Items 4–6 share one Equator rebuild and
one Bay5 rebuild (see the shared build step at the end of §1.6) so the ≈15 min-per-map Unity/NavMesh bake
only runs once per map for this whole plan.

### 1. Ambient audio gain (both maps) — no rebuild — VERIFIED 2026-09-25 10:25 (`AmbientGainDb` + `AmbientVolumeOffset` per map; +6 dB on Bay5 logged `adjusted 24 player(s)`, key reset to 0; docs/progress/two-maps-items-1-3.md)

**What.** Add a per-map, additive ambient-gain config key so quiet sources stop randomly going silent.

**Verified facts.** `BaseAmbientSoundPlayer.GetBaseVolume() = _volume + Random.Range(-_deltaVolume, 0)`
(`Audio.AmbientSubsystem/BaseAmbientSoundPlayer.cs:213-220`) — a pure downward draw. On Equator 13/25 authored
players have `volume < deltaVolume` (e.g. volume 0.050, delta 0.089 → range **[-0.039, 0.050]**, negative
picks clamp to silence; count re-verified by the critic against `native-audio.json`); Bay5 has none (`work/extract/{equator,bay5}-native-dust/audio/native-audio.json`).
A uniform dB **multiply** does not fix this (the negative-draw fraction `delta/(delta+volume)` is scale
invariant); only an **additive** offset to `_volume` (raising the floor) does. No gain key exists today
(`ArenaMapPort.Client/Bay5AmbientAudio.cs:33,36` only has `PlaybackDiagnostics`). `_volume`/`_deltaVolume`
are public fields; `Bay5AmbientAudio.Driver.TryStart()` already iterates `_activePlayers` right before
`Play()` — the natural, minimal-risk edit site.

**Steps (client only).**
1. `Plugin.Instance.Config.Bind("Audio (arena_equator|arena_bay5)", "AmbientGainDb", 0f, "Additive gain (dB)
   applied to imported ambient sources' base volume; 0 = original Arena mix. Takes effect on next map load.")`
   next to `PlaybackDiagnostics`.
2. In `Driver.TryStart()`, before the `Play()` loop: `player._volume = Mathf.Clamp01(player._volume +
   LinearFromDb(gainDb))` — never touch `_deltaVolume`.
3. Log the before/after per-player volume once (reuse the existing `Report()`-style logging).

**Effort.** S — one config key, one method edit, no scene/bundle touch.

**Host test.** EQUATOR/BAY5 DEBUG raid, enable `PlaybackDiagnostics`, `uibridge_log` filter
`[ArenaMapPort][NativeAudio]`, confirm the new per-player volume line; user listens at a few `AmbientGainDb`
values.

**Done when.** Config key exists with neutral default, log line shows the applied gain, user confirms
ambience is audible without exceeding 1.0.

### 2. Bay5 barbed wire (Bay5 only) — no rebuild — VERIFIED 2026-09-25 10:23 (192 triggers attached on Bay5; a jump inside `bruno_helix (6)` cost HP on six body parts; docs/progress/two-maps-items-1-3.md)

**What.** Attach the real `EFT.Interactive.BarbedWire` component to the harbor fence trigger volumes so
contact damage + sprint penalty work as in vanilla EFT.

**Verified facts.** 246 `BarbedWire` MonoBehaviours exist in Bay5's source, all with a sibling collider (224
`BoxCollider isTrigger=true` — the functional subset; 21 solid `BoxCollider` + 1 `MeshCollider`, harmless
no-ops). These exact trigger colliders are **already present in the built `bay5-native-dust` bundle** (262
`bruno_helix*` objects, 224 with `is_trigger:true`, in `work/extract/bay5-native-dust/scenes/*.json`) — the
geometry work is done, this is pure client-side component attachment. `DamageTrigger`
(`EFT.Interactive/DamageTrigger.cs:8,34-38,50-84`) needs only its own `Collider` and no per-instance BSG
field; `BarbedWire.PlaySound`'s `SoundBank` field is unrecoverable but only affects an optional sound.

**Steps (client only).** New `BarbedWireRepairs.cs` (same shape as `EnvironmentRepairs.cs`): for
`arena_bay5` only, at raid start walk owned scenes for `BoxCollider`/`MeshCollider` with `isTrigger==true`
and no existing `BarbedWire`, `gameObject.AddComponent<EFT.Interactive.BarbedWire>()`. Verify the
`SoundBank`-overload of `BetterAudio.PlayAtPoint` tolerates null first (or wrap `PlaySound` defensively).

**Effort.** S-M.

**Host test.** `uibridge_teleport` to a harbor fence line, walk/sprint into it, confirm HP loss and
`AddStateSpeedLimit(BarbedWire)` in the log, no exception.

**Done when.** Walking into a Bay5 fence line costs HP and slows the player; no other map affected.

### 3. Drop "(spike)" locale text (both maps) — server rebuild, no Unity rebuild — VERIFIED 2026-09-25 10:27 (map-select screen shows `ARENA: EQUATOR` / `ARENA: BAY 5`; the twelve deferred maps keep `(SPIKE)`; docs/progress/two-maps-items-1-3.md)

**What.** Equator/Bay5 still say `"Arena: Equator (spike)"` / `"Internal test build..."` in the map-select
screen (`ArenaMapPort.Shared/ArenaMapDescriptor.cs:162-163,217-218`), contradicting their status as the
flagship release maps.

**Steps.** The map-select screen reads `"<_Id> Name"` / `"<_Id> Description"` from the locale table, which the
server fills from the descriptor's `LocaleName`/`LocaleDescription` at load (`ArenaMapRegistration.cs`, doc
comment near line 298) — not from `base.json`. So: edit the four strings in `ArenaMapDescriptor.cs` and the
matching `locale_name`/`locale_description` in `tools/maps/equator.json` and `bay5.json` (the descriptor
validator compares them), regenerate both `base.json` for consistency (`make_base_json.py`), rebuild the server
(`dotnet build ArenaMapPort.Server -c Release`), deploy, restart the host server. No Unity rebuild.

**Effort.** Trivial.

**Host test.** Server restart, confirm the map-select screen shows the new names.

**Done when.** Neither map's UI text mentions "spike" or "internal test build".

### 4. NavMesh modifiers → real collision at Bay5 pump stations (Bay5 primary; Equator no-op) — VERIFIED 2026-09-25 14:32 (Bay5 bake 22190/10248 → 22117/10213 with 81 live overrides + 134 volumes; Equator 10404/4572 unchanged; bake-only components stripped before the bundle build; docs/progress/two-maps-item-4-navmesh-modifiers.md)

**What.** Extract and bake `NavMeshModifier`/`NavMeshModifierVolume`, currently dropped at three separate
pipeline layers, so Bay5's `bruno_helix` pump-station machinery is genuinely solid instead of walkable.

**Verified facts.** Arena's build is Unity 6000.3.6f1; the post-2022.2 `NavMeshModifier` layout was
confirmed byte-exact against all 369 instances via `check_read=True` (a deliberately-wrong pre-2022.2 layout
falsified: "Expected to read 64 bytes, but only read 48 bytes"). All 561 modifier/volume/`AdditionalNavmeshBlock`
instances round-trip with zero failures. Of 369 `NavMeshModifier`, **256 are pure `ignoreFromBuild` markers on
trigger colliders** (audio zones, bot spawn spheres, decorative plant triggers) — already very likely excluded
from today's bake by Unity's own PhysicsColliders-skips-triggers behavior, so respecting them is a
correctness improvement, not a visible change. The one instance that **is** currently a real gap: Bay5's
`bruno_helix` prop family — 113 `NavMeshModifier` overrides (`area=1 Not Walkable`) + all 134
`NavMeshModifierVolume` instances, and because the marking `BoxCollider` is itself a trigger, nothing blocks
this machinery today. Equator has **zero** override instances anywhere — this item gives Equator no benefit.
`NavMeshProjectSettings` area names/costs are readable natively and numerically match `BakeNavMesh.cs`'s
hardcoded constants (independent cross-check, no change needed there).

**Steps.**
- *Extractor* (`tools/extract/extract_equator.py`, shared by `extract_bay5.py`): resolve MonoBehaviours via
  `census.resolve_script_name()`, dispatch `NavMeshModifier`/`NavMeshModifierVolume` through the hand-built
  typetree node lists proven in this spike; emit `{override_area, area, ignore_from_build,
  apply_to_children}` / `{area, size, center}`.
- *Importer* (`SceneAssembler.cs` Pass-1c switch): `AddComponent<Unity.AI.Navigation.NavMeshModifier>()` /
  `<NavMeshModifierVolume>()` (package already vendored at
  `UnityProject/Library/PackageCache/com.unity.ai.navigation@1.1.5`), set public properties from JSON.
- *Bake* (`BakeNavMesh.cs`): replace the always-empty `new List<NavMeshBuildMarkup>()` at the `Bake()` call
  site with a markup list built from `NavMeshModifier.activeModifiers`, filtered to this bake's own
  `geometryRoots`; append each in-scope `NavMeshModifierVolume` as its own `NavMeshBuildSource` (read
  `NavMeshSurface.CollectSources()` in the vendored package as the reference for the box-source math, don't
  re-derive it).

**Effort.** S-M (~40 extractor lines, ~20 importer lines, ~15-20 bake lines) — the cheapest of the three
pipeline items, do it first in the shared rebuild.

**Host test.** `uibridge_teleport dryRun=true` around the pump-station machinery's known world positions
before/after; confirm Equator's bake vertex/triangle count is unchanged (it has no override instances).

**Done when.** Bay5's pump-station machinery blocks movement; Equator's NavMesh is numerically identical to
today's bake.

### 5. Physics props — Rigidbody + SmallPhysicsObject (both maps) — VERIFIED 2026-09-25 14:33 (391/413 on Equator, 18/18 on Bay5 active by default; SoundType now extracted per prop after the first raid's null-bank NRE; `PhysicsPropSoundRepairs` guard; docs/progress/two-maps-item-5-physics-props.md)

**What.** Attach `Rigidbody` (engine component) and `EFT.SmallPhysicsObject` (native class) to Equator's 391
and Bay5's 18 movable props (cups, bottles, boxes) so they can be kicked and shot instead of being static
dead geometry.

**Verified facts.** `Rigidbody` is 100% typetree-readable but the shipped field names are
`m_LinearDamping`/`m_AngularDamping`, **not** `m_Drag`/`m_AngularDrag` (a prior scratch attempt used the
wrong names and silently got `drag: null` for all 409 instances — re-verified this session). `Rigidbody` has
no `m_Enabled` field. `SmallPhysicsObject`'s own custom fields are unreadable (typetree read fails
"Expected 116 bytes, got 32"), but class name + owner GameObject resolve cleanly via
`census.resolve_script_name()`/`parse_monobehaviour_head()` — the same recipe already proven for
`BallisticCollider`. `SmallPhysicsObject.Awake()` self-wires from `BallisticCollider`s (already native since
09-22) and `Rigidbody`s via `GetComponentsInChildren`, sleeps every Rigidbody before registering, and needs no
unrecoverable field data to work. **Equator's entire physics layer ships hierarchy-disabled in Arena's own
data** (`PHYSIC` GameObject, `activeSelf=False`, no toggler found anywhere in the decompile) — Bay5's 18 are
fully active. `SmallPhysicsObject` is a plain (non-namespaced) Assembly-CSharp class, not in
`_gamerefs-excluded.txt`, same reachability shape as the already-working `BallisticCollider`.
`SmallPhysicsSystem` only updates props within 5 m of a player, budget-limited by measured FPS — not an
unbounded per-frame cost for 413 instances.

**Steps.**
- *Extractor*: add `build_rigidbody()` to `COMPONENT_BUILDERS` reading `m_Mass, m_LinearDamping,
  m_AngularDamping, m_UseGravity, m_IsKinematic, m_Interpolate, m_Constraints, m_CollisionDetection`. Add a
  `extract_physics_props.py` module (shape of `extract_ballistics.py`'s `augment_scene()`) that tags owner
  GameObjects with `{"type":"SmallPhysicsObject","schema_version":1,...}` via `resolve_script_name()`.
- *Importer*: `case "Rigidbody"` in `SceneAssembler.cs`'s Pass-1c switch (mirror `AttachBoxCollider`); confirm
  the target 2022.3.43f1 Editor's exact property names (`.drag`/`.angularDrag` vs `.linearDamping` alias) with
  a 2-line compile check first. New `SmallPhysicsObjectImporter.cs` (Pass 2, next to
  `BallisticColliderImporter.cs`): `AddComponent<global::SmallPhysicsObject>()` with class defaults — first
  do a trivial compile check that `typeof(global::SmallPhysicsObject)` resolves.
- *Client (small)*: `Config.Bind("Physics","EnablePhysicsProps",true,...)` doing `physicRoot.SetActive(true)`
  on Equator's `PHYSIC` container (Bay5's is already active), gated `ArenaMapRaid.IsOwnedScene()`, default
  **true** (user decision 2026-09-25: activate — the check below found no duplicates and no offline reason not
  to). One diagnostic log line (active-vs-found `SmallPhysicsObject` count).

**Effort.** M.

**Host test.** Shoot/walk into a known Bay5 prop first (baseline, e.g. `plasticCup_2_Physic (14)` at
`[143.78,-2.89,53.66]`), confirm hit + touch reactions; repeat on Equator with
`ForceEnableEquatorProps=true`, check no clip/pop on wake and flat FPS when a compound prop (6-fragment
`Cactus_4`) wakes several rigidbodies at once.

**Done when.** Props on both maps react to shots/touches by default; no clipping, no props sinking through
shelves at raid start, no FPS drop observed.

### 6. Rain-cover geometry — DryPlane + IndoorTrigger (Bay5 primary; Equator's one atrium trigger) — CORRECTED 2026-09-25 evening: IndoorTriggers authored (30 / 1); Arena's Bay5 DryPlanes turned out to be EFT-Lighthouse leftovers and are dropped at extraction, so the roof work stays with the depth-photo heuristic on both maps (the 14:33 "verified" only checked floor wetness; user saw rain indoors) — docs/progress/two-maps-item-6-raincover.md "Correction" — VERIFIED 2026-09-26 on SPT-GPU-2 (F8 readbacks + rain screenshots; Equator's atrium rains by design, `FullyIndoorCover` default false, puddle ripples follow the global rain value) — item-6 doc "Round 3 verified"

**What.** Author Arena's own `EFT.EnvironmentEffect.DryPlane`/`IndoorTrigger` geometry, replacing
`RainCoverRepairs.cs`'s ~1400-line depth-photo heuristic and the missing `WeatherObstacle` with ground-truth
data.

**Verified facts.** All 669 Bay5 `DryPlane` + 30 Bay5 `IndoorTrigger` + Equator's 1 `IndoorTrigger` + all 3
`TriggerGroup` instances round-trip byte-exact (`check_read=True`) through hand-built typetrees. **Every**
`DryPlane` mesh is Unity's built-in Quad primitive (never custom) — no mesh extraction needed, one shared
asset suffices. Neither `DryPlane` nor `IndoorTrigger` carries a `BoxCollider` (an assumption in the original
inventory this spike disproves) — `IndoorTrigger`'s cover volume is its own serialized `Bounds`, written
directly. Equator has **zero** `DryPlane`/`WeatherObstacle` in Arena's own data — its one big `IndoorTrigger`
(the mall) is the whole story there; do not invent a `WeatherObstacle` for Equator. `IndoorTrigger.AreaAutoId`
also feeds bot AI (`AIData.cs:886`, `PlaceForCheck.cs:88`).

**Steps.**
- *Extractor*: new script (pattern of `census.py`) reading Bay5 level85 + Equator level54 Scripts scenes
  (never extracted before), emitting `tools/maps/<key>.raincover.json`: `DryPlane` world transforms,
  `IndoorTrigger` world Bounds + `IsBunker/FadeTime/BunkerDepth/BunkerLowPass/ExposureSpeed/ExposureOffset/
  RainVolume/AreaAutoId`. **Spot-check recovered world-space Bounds against the already-ported scene's own
  coordinate frame first** — one sampled instance (path_id 8095) showed a baked-Bounds/live-recomputed
  mismatch.
- *Importer*: author `EnvironmentManager > EnvironmentHelperTrigger > TriggerGroup > N IndoorTrigger`
  children (Bounds written directly, skipping the Transform-derived `Reinit()` path); Bay5 only, also author
  `WeatherObstacle > 669 DryPlane` children on one shared unit-quad mesh asset.
- *Client*: gate `RainCoverRepairs.cs`'s heuristic **off** for these two maps once real data exists, so the
  heuristic and the authored geometry don't double-draw into the same depth photo;
  `EnsureEnvironmentPrefix`/`RainWetnessRepairs.cs`'s dampers need no code change (their `Instance==null`
  guards already become no-ops).

**Effort.** M.

**Host test.** Force rain (`WeatherTestOverride`), `uibridge_teleport` to 2-3 recovered `IndoorTrigger`
centers per map, confirm indoor/outdoor exposure+audio transition at the boundary, screen rain-drops stop
indoors on Bay5, ground wetness dries under roofed Bay5 areas, `WeatherObstacle.Instance is null` no longer
logs, Equator gains indoor exposure/audio inside the mall with ground wetness visually unchanged.

**Done when.** Both maps show correct indoor/outdoor rain behavior at the recovered trigger boundaries; the
old heuristic is silent on these two maps.

**Shared build step for items 4–6.** Implement all three extractor/importer/bake changes together, then:
`pwsh tools/build_all.ps1 -MapKey arena_equator -MapDescriptor .../equator.json -PackageDir
.../equator-native-v3 -OutputDir work/build/full -BakeNavMesh $true -Verify $true -SkipGameRefsSync` (done 2026-09-25; the
packages are the re-extracted `*-native-v3`, see AGENTS.md)
(Equator, **without** `-FullMap` per AGENTS.md), then the Bay5 equivalent **with** `-FullMap -OutputDir
work/build/bay5`. One client Release build (without `DllOnly`) stages both into `dist/`, one
`deploy-to-spt.ps1 -Confirm`, one combined host raid exercising the checks from §1.4–1.6 in a single session
per AGENTS.md's host-testing recipe (restart EFT once, walk both debug raids).

### 7. Light culling, volumetric light, flicker (both maps, mostly Equator) — no rebuild — VERIFIED 2026-09-26 on SPT-GPU-2 (built the extractor+importer route instead of the "no rebuild" AddComponent assumption because Arena tunes the values per light: packages `*-native-v4`, 472/186 CullingLightObject + 17/1 VolumetricLight + 2 LightFlicker live per the Lighting summary line; `CullingManager` and `VolumetricLightRenderer` are client fallbacks; 79 Equator / 107 Bay5 lights that Arena saved at intensity 0 now light up through `_maxLightIntensity`; distance fade and god-rays still to be judged by eye) — docs/progress/two-maps-item-7-light-culling.md

**What.** Author `CullingLightObject` (472/186 instances) on already-imported `Light`s for distance-based
fade, `VolumetricLight` (17/1) for god-rays, `LightFlicker` (2, Equator) — all client-side `AddComponent`
repairs on engine components already in the built bundles.

**Verified facts.** All three classes have only primitive fields with sane compiled defaults and need only a
sibling `Light` (already ported). `CullingLightObject._useLightIntensityFromEditor` defaults **true** and
would overwrite already-correct authored intensities with the flat default `_maxLightIntensity=5` — must be
explicitly set `false`. `VolumetricLight` builds its own material from `ShadersFinder.Find("Sandbox/
VolumetricLight")`, a real pre-registered shader; it also needs a `VolumetricLightRenderer` on the active
camera (`Cam2`), the same category of gap `CameraEffectsRepairs.cs` already fixed once for
`TOD_Camera`/`TOD_Scattering`. Both `CullingLightObject` and `VolumetricLight` depend on a `CullingManager.
Instance` singleton (same un-wrapped-singleton shape as `EnvironmentManager`/`ObservedCullingManager`,
already fixed with a "create a fallback if Instance is null" pattern in `EnvironmentRepairs.cs`) — **not
confirmed present or absent on a live raid in this read-only pass**, host-check first.

**Steps.** Host-check `CullingManager.Instance` presence first (F10/log). If null, add the same fallback
pattern as `EnvironmentRepairs.cs`. Extend `CameraEffectsRepairs.cs`'s `OnCameraChanged` hook to add
`VolumetricLightRenderer` to `Cam2`. New raid-start repair authoring `CullingLightObject`
(`_useLightIntensityFromEditor=false`) and `VolumetricLight`/`LightFlicker` (`FullRandomCurve=true` to avoid
a null-`Curve` NRE) on existing Lights, class defaults otherwise.

**Effort.** S-M.

**Host test.** Compare light brightness up close (unchanged) vs. at fade distance (dimmed); visually confirm
volumetric shafts; confirm the player's own volumetric-light graphics setting still disables it correctly
(expected).

**Done when.** Distant lights fade/cull, Equator's god-rays render, no authored light intensity regresses.

### 8. Arena's own AI patrol hubs / core points (both maps) — no rebuild

**What.** Extract the excluded TeamFight_AI scene's real `PatrolWay→PatrolPoint` hub/subpoint hierarchy and
`AICorePointHolder→AICorePoint` net, feed them to the client at raid start instead of the current
single-centroid/marker-chain synthesis.

**Verified facts.** Counts match the census exactly (Equator: 23 PatrolPoint/14 AICorePoint; Bay5: 47/11).
`PatrolWay`'s direct children **are** the real hub points, and each hub's own children are the real
subpoints (already placed by Arena, not regenerated rings) — 2+21=23 (Equator), 4+43=47 (Bay5), matching
exactly. `AICorePoint.Id`/`ConnectionGroupId` are recoverable from the GameObject **name** string
(`"AICore ID:{id} CG:{cg}"`, baked at author time). **Critical gap: `ConnectionsAtNet` (the graph edges
`BotPathFinderCorePoints` walks) is unrecoverable custom data** — every authored core point ships with an
empty edge list, identical to today's single synthetic point in that respect; the behavioral payoff over
today's synthesis is **unproven without a host test**. `common.py`'s `scene_files(dict, base=BASE)` binds its
default `base=` at **import time**; reassigning `common.BASE` later silently produces empty results — always
pass `base=` explicitly.

**Steps.** New `tools/extract/extract_ai_anchors.py` (or extend `census.py`) emitting
`tools/maps/<key>.ai.json` per map (hub/subpoint positions, core-point id/cg/position, `BoundsAll` composed
with correct world scale — **not** the byte-identical `BoundMinor`/`BoundDown` template leftovers). New
client runtime path (embedded JSON resource, parsed with the already-referenced Newtonsoft.Json) that
authors the real topology in place of `PatrolWayFallback`/`AICorePointFallback`'s synthesis, keeping the
synthesis as fallback for the twelve deferred maps. NavMesh-validate every point with `uibridge_teleport
dryRun=true` before trusting it — Equator's 2 elevated "near:BotZone" core points (y 3.18/3.45) are a known
off-current-NavMesh risk.

**Effort.** M, but **lowest-confidence payoff** in this plan (empty edge graph, zero cover/GroupPoint data on
either map already). Do this last among quality items; it sits closer to bot-AI polish than to visual/
collision "map quality," so it's the natural item to defer first if time runs short.

**Host test.** Compare bot behavior near patrol hubs/core points before/after in a host raid; specifically
check whether the extra points produce any measurable difference given the missing edges.

**Done when.** Real hub/subpoint/core-point data replaces the synthetic anchors on both maps and a host raid
shows no regression (behavioral improvement is a bonus, not a gate, given the known edge-data gap).

### 9. Release packaging (gating item, not map quality) — no rebuild

See the full checklist in §2. Runs after items 1–8 land (or after whichever subset the user wants in v1),
since it packages whatever `dist/` currently holds for Equator/Bay5.

**Effort.** S-M (new script + validator).

**Done when.** A packaged Equator+Bay5-only zip validates clean and a host server started from just that
2-map `db/` subset shows exactly two Arena locations in the map-select screen.

### 10. Location-select banner from Arena's own art (both maps) — no rebuild (added 2026-09-25)

**Verified facts (read-only research, 2026-09-25).** EFT 0.16.9.5's raid-loading screen has no image slot:
`PreloaderUI.ShowRaidStartInfo → RaidStartIntro.ShowIntro` only types text (raid number, day, player,
location name) over the shared `Glitch_BG_Loop_*` background; `LocationSettings.Preview` is declared but never
read. The one per-location image slot the client shows is the location-select `Banners[]` picture
(`LocationInfoPanel` uses the first entry, `MatchmakerBannersPanel` cycles them), served by the SPT server's
`ImageRouter` as `/files/<path>`. Arena ships matching art in `resources.assets`: `Matchmaker_Banner_Equator`
(path_id 3030) and `Matchmaker_Banner_Bay5` (1361), 769×460 — vanilla banners are 764/765×460 — plus
1920×1080 `AllScreens_Loadingscreen-Equator` (1558) / `-Bay5` (804, NY variant 791) and the main-menu parallax
sets `Arena_MainMenu_Paralax_<Map>-1..4` (sharedassets1.assets, 2048×1024). Today `make_base_json.py` forces
`Banners: []` (Factory's banners must not leak), so both maps show the client's default picture.

**Steps.** Extract the two `Matchmaker_Banner_*` textures with UnityPy into `dist/SPT_Runtime/user/mods/
ArenaMapPort/banners/` (BSG art: staged, never committed); emit one `Banners` entry per map
(`{"id": "arena_<key>_banner", "pic": {"path": "banners/arena_<key>.jpg", "rcid": ""}}`); inject `ImageRouter`
into `ArenaMapPortMod` and `AddRoute("/files/banners/arena_<key>", <file>)` per map inside the existing try/catch
loop (the Icebreaker MIT reference does exactly this); banner `Name`/`Description` locale entries; server
rebuild + restart; check the location-select screen (an unregistered banner path logs `[UNHANDLED]` and pops an
empty client dialog — that is the known landmine).

**Effort.** S. **Not included:** showing the 1920×1080 loading art during the raid transition would need a
custom client overlay (own Canvas/Image on the preloader) — M, optional polish.

## 2. Release checklist for the two-map package

1. **No registry code change needed.** `ArenaMapRegistration`'s per-map load is already wrapped in its own
   try/catch (`ArenaMapPort.Server/ArenaMapRegistration.cs:333-409`) and skips a map cleanly if its
   `db/<key>/base.json` is simply absent from the package — excluding the 12 deferred maps' `base.json` is
   sufficient by itself.
2. **The map-select screen builds one button per enabled, registered location** (`docs/SPEC.md` §E.6)
   independent of whether bundles exist on disk — so a release package must never ship a map's `base.json`
   without its bundles. `tools/package-release.ps1` (new): copies exactly the Equator+Bay5 subset (2 DLLs, 6
   bundles, 2 shader sidecars, 2 `db/.../base.json`) into `dist/release/` (never back into `dist/` or
   `D:\SPT41*`), matching `dist/`'s existing `BepInEx/plugins/ArenaMapPort` + `SPT_Runtime/user/mods/
   ArenaMapPort` layout.
3. **New validator** (`tools/validate/validate_release_package.py`, or `validate_distribution.py --maps
   equator,bay5`): confirms the packaged subset contains exactly those 12 files, correct hashes, nothing else
   leaked in. Keep `validate_distribution.py`'s existing full-14-map check unchanged (it stays the dev-dist
   regression check).
4. Zip the two top-level folders; add a short end-user INSTALL note (separate from the dev-facing
   `README.md`).
5. Drop the "(spike)" locale wording (§1.3) before packaging.
6. Add a root `LICENSE` (MIT, matching `ModMetadata.cs:21`'s existing unbacked claim) and a `CHANGELOG.md`
   recording the v1 scope (Equator+Bay5 only, no loot, no scav raids, ArenaMode-only target, other 12 maps
   deferred).
7. Bump `Plugin.cs`'s `PluginVersion` and `ModMetadata.cs`'s `Version` together (currently both `0.1.0`,
   independent constants — free choice, see open decision §4.2).
8. `SptVersion="~4.1.5"` needs **no change** — already exercised successfully against the host's SPT 4.1.6
   (HANDOFF 2026-09-22; SPT's loader would refuse an out-of-range mod outright).
9. **Explicitly exclude `ArenaMapDebug.dll`** from the package — it is not staged by this repo's build today,
   but its menu-skip/no-confirm buttons would be a bad surprise in a public download; a one-line reminder for
   whoever assembles the zip by hand.
10. Run the full existing test suites (`dotnet test -c Release`, `python -m unittest discover`, the
    PowerShell log-scan tests) once the packaging script lands.
11. **Host-verify, don't just trust source reading**: start a server with only the 2-map `db/` subset present
    and confirm the map-select screen shows exactly two Arena locations before calling the release done.

## 3. Deliberately NOT in this plan, and why

- **Decals (`StaticDeferredDecal`, 348/499 instances)** — the decal shaders already work (ArenaMapPort's own
  `ShaderRebind` resolves them); the blocker is that **zero** decal GameObjects have a usable material
  reference (346/348 Equator instances have no `MeshRenderer` at all; 452/499 Bay5 instances have a
  `MeshRenderer` with a **null** material slot). Authoring them would mean inventing which of ~20 orphan
  decal materials goes where — fabricated data, not ported data.
- **Flares (`MultiFlare.FlareLight`, 28/25) and their per-flare scale/color/distance data** — same shape of
  problem: the private `Flare[]` payload is unrecoverable and no sibling Light/Renderer exists to infer it
  from.
- **Full spatial-audio room/portal import (audio Spike B "Plan B")** — Large effort, unverified importer path
  for an entire additional scene subtree, and produces **zero audible change** without also flipping
  `SpatialAudioInitSkipPrefix` back on (currently a deliberate skip, not just missing data, per
  `SpatialAudioRepairs.cs`). The narrower "Plan A" (ship the real `.audiobakedata` + a `LocationInfo` object,
  proven byte-parseable: Equator 210 room pairs, Bay5 706) is also dropped from this plan — it's an inert
  prep step with no audible payoff on its own, and the exact StreamingAssets-relative path a BepInEx mod can
  use was not resolved in the spike.
- **The other twelve maps** — user decision; nothing here generalizes to them without re-running the same
  extraction against their own Scripts/AI scenes.
- **Glass / breakable windows** — user decision (147 Bay5 panes are inactive in Arena's own data too).
- **ArenaMode-side items** (pickup position survey, TDM/Tactical faction team-start pools, round-state
  lighting director allowlist) — belong to the ArenaMode repo (`ArenaAreaCatalog`, F12 capture tool), not
  ArenaMapPort; wanted "later" per the user but not actionable here.
- **Doors (`DoorHandle` 9/11, `BrokenDoor` 1)** — the swing/open field data is unrecoverable, and the functional
  problem (players and bots blocked) is already solved by opening the booth doors and the FCK shutter at raid
  start; animated doors would be cosmetic.
- **`AdditionalNavmeshBlock`** (58 instances, Equator only) — fully readable, but has **zero call sites**
  anywhere in the EFT 0.16.9.5 decompile outside its own definition; there is no live hookup to port it into.
- **Arena's crowd/fans/speakers/monitors/gates** (`Game.Modes.Data.*`) — not EFT classes at all; porting them
  would be a full custom re-implementation for decoration, already ruled out in the prior inventory.
- **PerfectCulling changes, Amplify impostors, mannequins/skinned rigs** — already ruled out (proprietary
  bake with no measured perf problem; atlas not shipped; no animation clips).
- **Arena's own exits/scav points** — ArenaMode doesn't use exits at all.
- **SmallPhysicsObject's `SoundType` and cosmetic force-multiplier fields** — cosmetic only (every prop plays
  the class default "Books" sound); a follow-up byte-decode pass (the same technique already proven for
  `BallisticCollider`) is plausible later but not in this plan.

## 4. Open decisions for the user (blocking)

1. **Decided 2026-09-25: activate the props.** Equator's `PHYSIC` container (2,192 objects, 973 renderers:
   94 Baltika bottles, 62 plastic cups, 56 paper cups, 26 cans, …) ships `activeSelf=False` in Arena's data;
   a scene-dump check found **no** active static duplicate of any of those meshes within 0.3 m, so enabling
   adds clutter that is simply missing today, without double geometry. Bay5's `PHYSIC` root is already
   active (44 renderers). Arena's reason is almost certainly networked determinism, irrelevant offline;
   `SmallPhysicsSystem` only simulates within 5 m of a player under an FPS budget. Default on, config to
   disable.
2. **Deferred 2026-09-25: version and release channel.** The user: version and release come last, once
   everything in §1 is done. Until then `dist/` + `deploy-to-spt.ps1` remain the only delivery; §2 runs after
   item 8.
3. **Answered by 2:** the cut line is "everything in §1".
