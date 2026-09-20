# Exfils — stage 2: a raid you can leave on foot

Goal (user, 2026-09-20): after stage 1 (scavs spawn and roam) the next gap for "wirklich spielbar" is that a raid
only ends by timer, death or disconnect — never "Survived". This stage authors exfiltration points at runtime.

## How EFT builds exits (decompile, verified by a read-only workflow + adversarial critique, 2026-09-20)

- `ExfiltrationController.InitAllExfiltrationPoints(MongoID, BackendExitTriggerSettings[] settings, …)`
  (CommonAssets.Scripts.Game/ExfiltrationController.cs:222-262), called from `BaseLocalGame.vmethod_6:793` right
  after `Profile.Info.EntryPoint` is set from the spawn point's Infiltration: collects every scene
  `ExfiltrationPoint` via `LocationScene.GetAllObjects<ExfiltrationPoint>()`, matches each by exact
  `Settings.Name == settings[].Name`, then `LoadSettings` (timing, chance, `EntryPoints` lowercased →
  `EligibleEntryPoints`) and a chance roll; unmatched points become `NotPresent`. Server exits without a scene
  object simply do not exist.
- `ExfiltrationPoint.Awake` (EFT.Interactive/ExfiltrationPoint.cs:167-173) sets layer Triggers and dereferences
  `GetComponent<BoxCollider>()` — the collider must exist before the component is added. `Settings` is
  field-initialized; `ExitTriggerSettings.Load` never copies `Name`, so the name is set by the author.
- `InfiltrationMatch` compares `EligibleEntryPoints` with `Profile.Info.EntryPoint.ToLower()` — i.e. base.json
  `EntryPoints` must be the map's infiltration name ("ArenaEquator"), not the location key.
- Trigger → raid end: `OnTriggerEnter → Proceed → OnStartExtraction` → `EndByExitTrigerScenario` counts
  `ExfiltrationTime` down → `BaseLocalGame.Stop(profileId, Survived|Runner, exitName)`. A fresh short raid yields
  **Runner** (`SurvivedExpRequirement`/`SurvivedTimeRequirement`), which is still the exit chain working.
- SPT's `EndLocalRaidAsync` passes the exit name through; nothing validates it against `LocationBase.Exits`.
- `LocationScene` keeps a private `Dictionary<Type, Array> _typeToContainer` filled once in its Awake
  (`Register<T>` throws on a second registration), and `GetAllObjects<T>` reads it filtered by
  `isActiveAndEnabled`. Patching the generic `GetAllObjects<T>` was rejected: Mono shares one body across
  reference-type instantiations, a postfix typed for ExfiltrationPoint would run for every T.

## What was built

- **Data** — `tools/maps/equator.exits.json` (new sidecar, same discovery pattern as the bots sidecar) and the
  compiled twin `ArenaMapRegistry.Equator.Exits` (`ArenaMapPort.Shared/ArenaMapDescriptor.cs`,
  `ArenaExitDescriptor`: name, floor position, box size, yaw). EFT never carries exit positions from the server,
  so the client needs its own table; `BaseJsonTests.Exits_DescriptorMirrorsSidecar` keeps both identical. Two
  exits: **Equator North Gate** (-8.0, 0.5, 53.0) beyond the Usec/All spawns and **Equator South Gate**
  (5.0, 0.5, -38.0) beyond the Bear spawn, both for every side, 8 s, 100 %, Individual, no requirement.
- **Server** — `make_base_json.py apply_exits` emits Factory-shaped `exits[]` entries (`EntryPoints` =
  infiltration name); `ApplyExitLocale` adds identity locale entries (vanilla exit names are their own keys);
  `ExitNameMismatches` warns at startup when base.json and the descriptor disagree. Tests:
  `Exits_ShipTheDescriptorsExits`, `Exits_DescriptorMirrorsSidecar`, `ApplyExitLocale_…`, `ExitNameMismatches_…`.
- **Client** — `ExfilRepairs.cs`: prefix on `InitAllExfiltrationPoints` authors one GameObject per descriptor exit
  (BoxCollider first, isTrigger, layer Triggers, then `ExfiltrationPoint`, then `Settings.Name`), NavMesh-samples
  the floor point (6 m), and puts the points into the first loaded `LocationScene`'s container for
  `ExfiltrationPoint` (replacing the entry, so BSG's own lookup finds them). Rebuilt on every call, never cached
  across raids. A postfix logs each point's `Status` and `EligibleEntryPoints` after init — `NotPresent` means
  the name matched no server exit.

## Host test 1 (2026-09-20 13:07, client cfcc8f6a, server 847fbbbd) — RUNNER via the south gate

Server restart: all 14 maps registered, no `exit names differ` warning. Raid (via the user's EQUATOR DEBUG button):

```
[Exfil] authored exit 'Equator North Gate': box (4.00, 3.00, 4.00) at floor (-8.0, 4.3, 53.0) (NavMesh-sampled, 3.8 m from authored)
[Exfil] authored exit 'Equator South Gate': box (4.00, 3.00, 4.00) at floor (5.0, 0.1, -38.0) (NavMesh-sampled, 0.4 m from authored)
[Exfil] registered 2 exit(s) with LocationScene 'Arena_Equator_Scripts' (2 ExfiltrationPoint(s) in its container now)
[Exfil] exit 'Equator North Gate' after init: status=RegularMode eligibleEntryPoints=[arenaequator] chance=100 time=8s
[Exfil] exit 'Equator South Gate' after init: status=RegularMode eligibleEntryPoints=[arenaequator] chance=100 time=8s
```

`uibridge_places kind=exfil` lists both as `exfil:pmc:…, status RegularMode, entryPoints arenaequator`. Teleport into
the south box (`reachable=false`, see below) → 8.6 s later the raid was over; the server logged
`Raid: arena_equator.Pmc … outcome: RUNNER` (`/client/match/local/end`) — Runner, not Survived, because a
two-minute raid with no XP fails `SurvivedExpRequirement`/`SurvivedTimeRequirement`, exactly as predicted. The
whole chain (trigger → EndByExitTrigerScenario → Stop → SPT) works with runtime-authored points.

Two position findings from `uibridge_teleport` dry runs:
- The **spawn booths are NavMesh islands**: from the All spawn every destination is "not reachable on foot", from
  the atrium every spawn is. Physically the booths are open (barriers disabled at raid start), the bake kept the
  barrier walls. Fine for triggers and players, but bots cannot path into the spawn zones (and `uibridge_teleport`
  needs `reachable=false` for them).
- The floor ends at z ≈ 49.6 in the north (the authored (-8, 0.5, 53) sampled onto a ledge at y 4.3) and at
  z ≈ -44 in the south. A box within ~5 m of a spawn point would extract a fresh spawn (spawn radius 5). Gates
  moved to **(2.0, 0.1, 46.0)** and **(5.0, 0.1, -44.0)** — both on the floor per dry run, 10 m from the nearest
  spawn.

## Host test 2 (13:14, client ec14a37b) — RUNNER via the north gate

Both gates now sample onto the floor with 0.0 m offset (`(2.0, 0.1, 46.0)` / `(5.0, 0.1, -44.0)`), both
`RegularMode` with `eligibleEntryPoints=[arenaequator]`. Teleport into the north box → raid over after 8.8 s, server
`outcome: RUNNER`. Stage 2 reached: a raid on Equator can be left on foot through either gate. The user's
EQUATOR DEBUG button skips the exit screens, so the result is read from the server log.

Open: the extraction list/timer UI was not inspected (IMGUI-free, UGUI — a `uibridge_snapshot` during the
countdown would show it); Survived vs Runner needs a longer raid with XP; the spawn-booth NavMesh islands
(bots cannot path into the spawn zones) belong to the NavMesh bake, not to this stage.

## Respawn booths (2026-09-20, user report: "spawn liegt in einer Box, aus der man nicht rauskommt")

Arena's spawn booths close with sliding doors — five assemblies "Door_Sliding_Respawn (n)" in
`Arena_Equator.json` (LODGroup root with the frame; leaves "Door_Sliding_Respawn_L"/"_R" carrying their LOD meshes,
a `_COLLIDER` on LowPolyCollider and a `_BALLISTIC_metalthin` MeshCollider). Arena's round script slides them open;
it is not ported. `BoothDoorRepairs.cs` (config `[Spawns] OpenRespawnDoors`, default on) deactivates both leaves of
every assembly in our scenes from the same postfix that places the spawns, and logs a horizontal raycast from each
player spawn to its nearest door. Host test (13:51): `deactivated 10 sliding-door leaf(s) on 5 door assembly(s)`
at (-4.3, 0, 40), (4.5, 0, 40), (-4.5, 0, -35), (4.4, 0, -35), (-3.8, 0, -45.9); `spawn 'arena_equator-player-all'
-> door 1.5 m: clear`, `spawn 'arena_equator-player-bear' -> door 1.7 m: clear`; the Usec spawn (-11.7, 48.6) is 11 m
from the nearest door and its ray hits `FCK_border_01_COLLIDER` (a barricade prop, `SOO/Barricades`) at 2.4 m — that
spawn sits among road barriers, not in a booth; whether it is walkable needs the user's feet. (The postfix runs twice
per raid — EFT calls `CreateFromScene` twice — the second pass finds the leaves already off.)

Still open: the NavMesh was baked with the leaves in place, so the booth interiors stay islands for bots and for
`uibridge_teleport`'s reachability check; excluding `Door_Sliding_Respawn*` and `SPAWN_BOUND*` from the bake is a
Unity rebuild (~19 min) for a later pass.

## Smoke signals (2026-09-20, user: "konnte die exfils noch nicht finden")

Vanilla exfils get their smoke from scene art under `ExfiltrationPoint._root`; no BSG component draws one. The only
always-on smoke EFT ships is the airdrop crate's signal (`AirdropSynchronizableObject.AirdropFlare`, toggled by
`ClientAirDrop` with SetActive + Play). `ExfilSignals.cs` retains that bundle
(`assets/content/location_objects/lootable/prefab/scontainer_crate.bundle`), takes the `Flare_Smoke` material
(`CyclesSmoke32_red`) and colour curve and the crate's `FlareSound` clip (`flare_loop`, read by reflection — the
component derives from Sirenix' SerializedMonoBehaviour), and builds a Light, a quiet 3D AudioSource and a
ParticleSystem (`AlwaysSimulate`, world space, 15/s) on a Default-layer child of each authored exit — the approach
RaiRaiTheRaichu's HiddenCaches uses, re-implemented from the decompile (THIRD-PARTY-NOTICES.md). Config
`[Exfils] SmokeSignals` / `SmokeSignalSound` / `SmokeSignalColor` (green). The bundle is retained lazily in the
signal's `Start`, never inside a Harmony patch, and the harvested references are dropped at every raid start.
Host test (13:59): `signal assets from the airdrop crate: material=CyclesSmoke32_red curve=True clip=flare_loop`,
`smoke signal lit at 'Equator North Gate' ((2.0, 0.4, 46.0))` and South Gate — a green column visible from the All
spawn 10 m away (screenshot). A NotPresent exit gets its signal switched off in the init postfix.

## Spawn barriers — the FCK roller shutter (2026-09-20 evening, user: "noch ein weiteres Rolltor beim Spieler-Spawn")

The user's F10 inspection (D:\SPT41vanilla log 16:01:36, camera (-11.47, 1.61, 36.84) looking east) hit
`SBG_Arena_Equator/SOO_LOD0/equator_roller_shutters_02 (9)/roller_shutters_02_BALLISTIC_metalthin` at 1.9 m: the roller
shutter that closes the FCK hall's corner exit. The Usec spawn (-11.7, 48.6) is inside that hall (FCK columns x -20..-12,
z 37..50; `FCK_border` barricades at z 46.7). The shutter is plain static geometry — LODGroup root at (-9.7, 0, 36.0),
yaw -135°, children `roller_shutters_02_LOD0..2`, `_SHADOW_LOD0`, `_COLLIDER` (LowPolyCollider), `_BALLISTIC_metalthin`
(HighPolyCollider) and the same set for `roller_shutters_frame_02` — no door component, no script; Arena opens it per
round by a script that is not ported. Equator has 20 shutter assemblies (13 `_02`, 6 `_01`, 1 `_adik`); 19 line the outer
walls at x ≈ ±28 and the south shop row z -26.8 (decoration in front of unmodelled interiors — opening them would expose
nothing), only (9) closes a spawn area. Object names repeat (`equator_roller_shutters_02 (4)` exists twice), so the
descriptor names the one to open by hierarchy path: `ArenaMapDescriptor.OpenedBarriers` (Equator only; test
`OpenedBarriers_EquatorNamesTheFckShutterByPath_OtherMapsNone`). `BoothDoorRepairs.OpenBarriers` resolves the path
over our loaded scenes' root objects (`Transform.Find` for the rest), deactivates every child whose name does not
contain "frame" (the shutter meshes, shadow mesh and both colliders; the frame stays) and warns if the path is not
found. Config `[Spawns] OpenSpawnBarriers` (default on, new key — `OpenRespawnDoors` keeps its meaning). The raycast
report now covers every opening within 20 m of each player spawn (doors and barriers). The NavMesh still has the
shutter baked in, as with the booth doors.

Host test (2026-09-20 17:13, client 17:08 build, EFT restarted): `spawn barrier 'equator_roller_shutters_02 (9)' at
(-9.7, 0.0, 36.0): deactivated 5 part(s), 1 already off, kept 6 frame part(s)` (LOD0-2, `_COLLIDER`, `_BALLISTIC_metalthin`
off; `_SHADOW_LOD0` was already inactive; the six frame parts stay); `spawn 'arena_equator-player-all' ->
'equator_roller_shutters_02 (9)' 5.7 m: clear`. Screenshot from the All spawn: the FCK entrance is open with the frame
standing, the hall (red columns, metal `FCK_border` sheets) visible behind it. Teleported to the Usec spawn (-11.7, 48.6):
brick wall to the north and east, an `FCK_border` sheet 1.8 m to the south, open to the west into the hall — the way out
is west along the hall, then out through the shutter at the hall's south-east corner (the rays from that spawn hit the
sheet/hall wall at ~2 m, which is the niche, not a closed exit). Second `CreateFromScene` pass: `deactivated 0 part(s),
6 already off`. No warnings. Deployed to D:\SPT41 and D:\SPT41vanilla.

