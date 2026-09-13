# SPT 4.1 minimal location/scene contract — client load chain, hard requirements, spawn logic, NavMesh, server DB, ArenaMode hooks

Scope: `key spt-location-contract`. All client citations are `file:line` inside
`D:/SPT41/Development/_decompiled` (paths below are relative to that root). All
server citations are against `SPTarkov.Server.Core` **4.1.0** (net10.0), decompiled
with `ilspycmd 11.0.0.9375` from
`<user-home>/.nuget/packages/sptarkov.server.core/4.1.0/lib/net10.0/SPTarkov.Server.Core.dll`
(the exact package ArenaMode.Server itself references — see its `.csproj`). The
decompiled server dumps used below are saved at
`D:/ArenaMapPort/analysis/decompiled-server/*.cs` for re-inspection. Tags:
**[VERIFIED]** = read the exact code; **[INFERENCE]** = reasoned from adjacent
verified code, not itself executed/traced; **[OPEN]** = not traced this pass.

---

## 1. Client load chain (menu click → scenes active → GameWorld ready)

Entry point for a **local/offline raid** is `TarkovApplication.LocalGameMatching`
(`EFT/TarkovApplication.cs:2518`). Sequence, with the exact hand-off points:

1. **`ScenePresetLoadConfig` built from `_raidSettings.SelectedLocation.Scene`**
   (`EFT/TarkovApplication.cs:2534`). `SelectedLocation` is a
   `JsonType.LocationSettings.Location` (the client's mirror of the server's
   `base.json`, deserialized into `JsonType/LocationSettings.cs:27-434`). Its
   `Scene` field is an `EFT.ResourceKey` (`EFT/ResourceKey.cs:10`): `path` (bundle
   file, e.g. `"maps/develop_preset.bundle"`) + `rcid` (asset name inside that
   bundle, e.g. `"develop.scenespreset.asset"`). **[VERIFIED]**
2. **`LoadMapAndData(scenePreset, ...)`** (`EFT/TarkovApplication.cs:2659-2793`):
   - `GameWorld.Create<ClientLocalGameWorld>(new GameObject("GameWorld"), ...)`
     then `clientGameWorld.InitLevel(...)` (`:2675, :2682`) — this runs **before**
     any map scene loads. `GameWorld.InitLevel` (`EFT/GameWorld.cs:1193-1244`)
     only touches engine-global state (effects bundle, ballistics prewarm, the
     "Raid" prefab-pool bundles) — **no dependency on the target location's
     content**, so nothing here can fail because of a custom map. **[VERIFIED]**
   - `Singleton<GameWorld>.Create(...)`, `Singleton<IGameLevel>.Create(...)`
     register the GameWorld singleton (`:2684-2685`).
   - `Assets.Manager.LoadScenesFromPreset(scenePreset, loadFirstAsSingle:true,
     loadInParallelMode:false, allowSceneActivation:true, ...)`
     (`:2717-2724`) — this is where the actual `ResourceKey` → bundle → scenes
     resolution happens (see §1b below).
   - **On success**: `Singleton<LevelSettings>.Instance.OnPostLoadingScene()`
     (`:2731`) — see §2 (hard requirement: exactly one `LevelSettings` in the
     loaded scene set, or this line NREs on a stale/null singleton).
   - `PerfectCullingCrossSceneSampler.InitializeAutoCulling(...)` (`:2761`) —
     occlusion culling bring-up; not independently traced for NRE-on-missing-data
     this pass. **[OPEN]**
   - `PhysicsExtensions.Worlds.Create()` (`:2773`), then
     `SpatialAudioSystem`/`BetterAudio` init (`:2779-2790`).
3. **Bundle/scene resolution** (`EFT/AssetsManagerExtension.cs`,
   `EFT/LoadScenesFromPresetOperation.cs`, `EFT/ScenesPreset.cs`):
   - `ScenePresetLoadConfig.key` (the `ResourceKey` from step 1) is loaded via
     `Assets.Manager.LoadAssetAsync(preset.key)` inside
     `LoadScenesFromPresetOperation.LoadPresetFromConfigAsync`
     (`EFT/LoadScenesFromPresetOperation.cs:105-131`) — this is what turns the
     bundle+asset name into a live `EFT.ScenesPreset` `ScriptableObject`
     (`EFT/ScenesPreset.cs:10`). `ScenesPreset` fields:
     `ActiveSceneGuid`, `ServerName`, `ScenesGuids`, `ChildPresets`,
     `_activeSceneName` (→ `ActiveSceneName`), `_scenesResourceKeys` (→
     `ScenesResourceKeys`, filtered by `ShouldLoadScene`/`onlyOffline`).
     **[VERIFIED, `EFT/ScenesPreset.cs:53-98`]**
   - `LoadPresetAsync` (`EFT/LoadScenesFromPresetOperation.cs:133-231`) loads
     every entry of `preset.ScenesResourceKeys` via
     `ScenesLoadCoroutine`→`_assetsManager.LoadScene(...)`. The **first** scene
     loads `LoadSceneMode.Single` if `loadFirstAsSingle` (true for local raids,
     see step 2), **every subsequent scene loads `LoadSceneMode.Additive`**
     (`:237-238, :261, :283`). After all scenes are loaded, if
     `preset.ActiveSceneName` is set, `SceneManager.SetActiveScene(...)` is
     called (`:215-222`) — this is "the active scene" the task asked about; it
     is **not** derived from `ActiveSceneGuid` at this call site, it is the
     plain string `_activeSceneName` baked into the `ScenesPreset` asset.
     `preset.ChildPresets` (non-null entries) are then recursively loaded the
     same way (`:188-199`) — this is how a map's day/night or TDM/Tournament
     variant sub-presets attach.
   - `AssetsManagerExtension.LoadScene(ResourceKey)` (`:73-78`):
     `sceneName = resourceKey.ToAssetName()` (rcid if set, else derived from
     `path`, lowercased — `EFT/ResourceKey.cs:22-39`), `path = resourceKey.path`;
     calls `manager.LoadScene(path, sceneName, loadSceneMode, ...)`. The actual
     bundle-path resolution (StreamingAssets-first, then SPT mod bundles) lives
     deeper in `IAssetsManager`/`BundlesManager` and was **not** re-traced to a
     specific line this pass — this is the exact behavior the Icebreaker
     Backport Playbook documents independently and empirically
     (`D:/sptmaps/_sources/ManimalIcebreaker-main/docs/MAP-BACKPORT-PLAYBOOK.md`
     Phase 5.2: "the client resolves bundle keys against
     `EscapeFromTarkov_Data\StreamingAssets\Windows\` FIRST, and only uses SPT's
     mod-bundle system for keys with no local file"). Treat as **[INFERENCE,
     corroborated by a second independent source]** rather than directly
     re-verified against `BundlesManager.cs` in this pass.
4. **`GamePrepare()`** (`EFT/TarkovApplication.cs:2795+`) loads
   `Scenes.GameUIScene` additively — engine UI, unrelated to map content.
5. **`LocalGameCreate` → `BaseLocalGame<T>.Create` → `LocalGame.Create`**
   (`EFT/BaseLocalGame.cs:412-454`, `EFT/LocalGame.cs:139-153`): builds
   `WavesSpawnScenario`/`BossSpawnScenario`/`NonWavesSpawnScenario` straight from
   `location.waves` / `location.BossLocationSpawn` (server data; empty arrays are
   fine — no crash evidence found for empty wave/boss data).
6. **`BaseLocalGame.Run`** (`EFT/BaseLocalGame.cs:519-608`):
   - `SpawnPointsCollection spawnPoints = SpawnPointsCollection.CreateFromScene(
     DateTimeExtensions.LocalDateTimeFromUnixTime(location.UnixDateTime),
     location.SpawnPointParams)` (`:526`) — see §3 for the full mechanics.
   - `ISpawnSystem spawnSystem = SpawnSystemFactory.CreateSpawnSystem(settings,
     ..., spawnPoints)` (`:529`).
   - Player spawn selection happens later in the same method:
     `spawnSystem.SelectSpawnPoint(ESpawnCategory.Player, Profile.Info.Side, ...)`
     (`:576`).
7. **`LocalGame.vmethod_1`** (`EFT/LocalGame.cs:161-201`) — bot controller
   bring-up, the last stage before a raid is actually playable:
   - `BotZone[] botZones = LocationScene.GetAllObjects<BotZone>().ToArray()`
     (`:172`) — aggregates every `LocationScene.BotZones` array across all
     currently-loaded `LocationScene` components (registry-based, empty-tolerant,
     see §2/`LocationScene.cs`).
   - `BotsController.Init(this, botCreator, botZones, spawnSystem, ...)` (`:184`)
     → `EFT/BotsController.cs:168-219`, see §2 for the two hard-crash sites this
     call reaches (`AICoversData.CreateOrFind`, `BotZone.Init`).
   - `_wavesSpawnScenario.Run(...)` / `_nonWavesSpawnScenario.Run()` /
     `_bossSpawnScenario.Run(...)` fire depending on `location.OldSpawn` /
     `location.NewSpawn` (`:190-199`).

---

## 2. Hard requirements — what NREs/aborts if missing, and the earliest safe hook

| Component | Where it's looked up | What happens if missing | Hard or soft | Earliest safe hook to create it at runtime |
|---|---|---|---|---|
| **`LevelSettings`** (one per loaded scene set) | `Singleton<LevelSettings>.Instance.OnPostLoadingScene()`, called right after scene-load succeeds — `EFT/TarkovApplication.cs:2731` | `Singleton<LevelSettings>.Instance` is null (or a stale instance from a previous location if the old one's `OnDestroy`/`Awake` ordering raced) → **NRE** on `.OnPostLoadingScene()`. `LevelSettings.Awake()` (`LevelSettings.cs:166-171`) is what registers the singleton (`Singleton<LevelSettings>.Create(this)`), so it must be attached to a `GameObject` in one of the scenes the preset just loaded, active, with `Awake()` having already run (Unity runs `Awake` before the awaiting code resumes). | **HARD** | A plugin could `AddComponent<LevelSettings>` on any object in the loaded scene inside a postfix on `LoadScenesFromPresetOperation`/before the `TarkovApplication.LoadMapAndData` continuation runs — but simplest is to just author it into the Unity scene (SDK "Author" pattern, per the Icebreaker Playbook Phase 4). |
| **`LocationScene`** (per loaded design scene) | `LocationScene.Awake()` (`LocationScene.cs:319-389`), runs automatically when its scene activates | `Awake()` unconditionally does `Register(StaticLoot)`, `Register(LootableContainers)`, `Register(WorldInteractiveObjects)` (`:322-324`) — safe even if those fields are C# `null` (a dictionary entry with a null value). **But** lines `381-388` unconditionally do `WorldInteractiveObjects.Length` in a loop — if that field is left `null` (as opposed to an empty array) this is a **straight NRE**. All other array fields (`NavMeshLinks`, `SpawnPointMarkers`, `BotZones`, `ExfiltrationPoints`, …) are passed to `Register()` without a null check too, but `Register()` itself tolerates a null array reference (only breaks whichever caller later assumes non-null). Fields that ARE explicitly null-guarded before `Register`: `SyncAbles`, `TriggerEntities`, `ControlledLampGroups`, `AudioSources`, `BroadcastPlayers`, `EventSoundContentChangers` (`:325-370`). | **HARD** (for `WorldInteractiveObjects` specifically) / soft-but-silently-broken for the rest | Author the component with every array field initialized to `Array.Empty<T>()` at minimum (SDK build-time is the natural place — `LocationScene.FillArrays()`/`FillArraysFrom(Scene)`, `:423-464`, is the SDK-side populator used by the "Author" editor tooling). |
| **At least one `BotZone`** in the loaded scene(s) | `AICoversData.CreateOrFind(...)` (`AICoversData.cs:288-373`), called from `BotsController.Init` (`EFT/BotsController.cs:173`), itself called from `LocalGame.vmethod_1` (`EFT/LocalGame.cs:184`) | `BotZone botZone = UnityEngine.Object.FindObjectOfType<BotZone>();` (`AICoversData.cs:290`) — if the scene has **zero** `BotZone` components, `botZone` is `null`. The very next block (`AIManualPointsHolder` creation, `:292-299`) unconditionally does `obj.transform.SetParent(botZone.gameObject.transform);` (`:297`) → **NRE**, and the raid never finishes starting. This is exactly the "AICoversData.CreateOrFind NRE" the user's own memory notes reference. | **HARD** | `AICoversData.CreateOrFind` is called from `BotsController.Init`, which in turn is called from `LocalGame.vmethod_1` right after `LocationScene.GetAllObjects<BotZone>()` is read (`EFT/LocalGame.cs:172-184`) — a Harmony **prefix on `LocalGame.vmethod_1`** (or on `BotsController.Init` itself) that does `if (FindObjectOfType<BotZone>() == null) { var go = new GameObject("BotZone"); go.AddComponent<BotZone>(); }` before the original body runs is the earliest *code* hook. The cleaner fix for a real map is simply authoring one `BotZone` into the Unity scene (even empty of spawn points/patrol ways), which is what the SPIKE should do. |
| **`BotZone.PatrolWays`** (per zone) | `BotZone.Init(BotLocationModifier, BotsController)` (`BotZone.cs:113-154`), called once per zone from `BotsController.Init`'s `for (int i...) botZones[i].Init(...)` loop (`EFT/BotsController.cs:215-218`) | `PatrolWay[] patrolWays = PatrolWays; for (...patrolWays.Length...)` (`BotZone.cs:138-139`) — **NRE** if the serialized `PatrolWays` field is `null` rather than an empty array. | **HARD** | Same as above: author the field as `[]` in the SDK, not left unset. |
| **`LocationExportInfo`** (one per loaded scene set) | `SpawnPointsCollection.IsValid(...)` (`EFT.Game.Spawning/SpawnPointsCollection.cs:172-193`), called from `CreateFromScene` only when the server actually sent `SpawnPointParams` (always true for a normal raid — see §3) | If absent: `FindObjectsProxy.FindUnityObjectOfType<LocationExportInfo>()` returns `null` → `IsValid` logs `"Can't get LocationExportInfo from scene."` and returns `false` → `CreateFromScene` logs `"Received spawn points will not be set."` and falls back to whatever raw `SpawnPointMarker`s already exist in the scene, **without merging/creating/fixing anything from the backend `base.json`**. Not a crash — but it silently defeats the reconciliation in §3, so any spawn point present only in `base.json` (not pre-placed in the scene) will simply never exist. | **SOFT (logged), but silently breaks server-authored spawn points** | Author one `LocationExportInfo` component with `_unixDateTime` ≤ the `base.json`'s `UnixDateTime`. |
| **Exfiltration points** (`ExfiltrationPoint` in-scene) | `ExfiltrationController.InitAllExfiltrationPoints(...)` (`CommonAssets.Scripts.Game/ExfiltrationController.cs:222-286`), sourced via `LocationScene.GetAllObjects<ExfiltrationPoint>().ToArray()` (`:224`) | All downstream loops (`:225-284`) operate on whatever array comes back, including an **empty** one — no crash evidence. A raid with zero exits simply cannot be extracted from (still endable by timer/MIA). | **SOFT** — contrary to the task's premise, this is *not* a hard NRE requirement for raid start. | N/A for the SPIKE. |
| **`EnvironmentManagerBase`/indoor triggers** | `EnvironmentManagerBase.Init()`/`InitIndoors()` (`EnvironmentManagerBase.cs:33-59`) | `_rootTriggerGroup = GetComponentInChildren<TriggerGroup>()` may be `null`; every consumer (`TryFindTriggerByPos`, `TryFindEnvironmentIdByPos`) is guarded by a `_haveRoot` bool (`:52, :100-118`) — fully graceful. | **SOFT** | N/A. |
| **Camera / PostProcessing prefab** (`LevelSettings.CameraPrefab` etc.) | `CameraManager.SetCameraFromSettings(ISettings settings)` (`EFT.CameraControl/CameraManager.cs:322-326`) | `if (settings != null && settings.CameraPrefab != null) { ... }` — guarded; a null `CameraPrefab` on `LevelSettings` simply skips the custom-camera branch (falls back to whatever default the else-branch does, not traced further). | **SOFT** | N/A for the SPIKE, but real maps do want a proper `LevelSettings.CameraPrefab`/`PostProcessProfilePrefab` for correct look. |
| **`LevelPhysicsSettings`** | `Awake()` (`LevelPhysicsSettings.cs:16-25`) applies MBP broadphase regions; nothing outside this file was found asserting it must exist. | Simply never runs `Physics.RebuildBroadphaseRegions(...)`; Unity's default physics broadphase still functions. | **SOFT [OPEN — not exhaustively traced for hidden global dependents]** | N/A. |
| **`CullingManager`**, occlusion/`PerfectCullingCrossSceneSampler` | `PerfectCullingCrossSceneSampler.InitializeAutoCulling(...)` (`EFT/TarkovApplication.cs:2761-2770`) | Wrapped in try/catch that rethrows as `"Loading data file failed"` on **any** exception (`:2766-2770`) — so if this system *does* require baked culling data that a fresh SDK scene lacks, it becomes a hard abort of the whole `LoadMapAndData`. Not traced deeper than this call site this pass. | **[OPEN — potential hard requirement, unverified]** | Flagged as a risk to test early in the SPIKE; the Icebreaker Playbook independently notes occlusion culling had "ZERO" measured effect on their map and they run a self-hosted Perfect Culling runtime — worth reading their Phase 7/"self-hosted Perfect Culling" note if this throws. |
| **Doors / `WorldInteractiveObject`** | Covered by `LocationScene.WorldInteractiveObjects` (see row 2 above) — `Door : WorldInteractiveObject`, and `LocationScene.Awake()` additionally does `DoorsCollisionColliders.AddRange(door.CollisionColliders)` for every `Door` in that array (`LocationScene.cs:381-388`) | Same NRE as row 2 if the array itself is null; an **empty** array (zero doors) is completely fine — no separate hard requirement for doors specifically. | **HARD only via the `WorldInteractiveObjects` null case already covered** | N/A beyond row 2. |
| **NavMesh data** | Not a load-time gate at all — see §4. `BotZone.Init` calls `IsOnNavMesh(spawnPointMarker.transform.position)` per spawn marker (`BotZone.cs:154`, body not shown above) but this was not seen to abort the zone if false, only to (inferred from method name/usage pattern) log/flag. **[OPEN — return value's effect not traced]** | Bots would still get created but fail to path if the whole scene lacks NavMesh coverage; raid start itself is not gated on this. | **SOFT for raid START**, hard for playable bots (out of scope for the SPIKE). | N/A for the SPIKE (geometry/colliders/NavMesh first, bots later per project plan). |

---

## 3. Local player spawn point selection & `SpawnPointParams` reconciliation

Core method: `SpawnPointsCollection.CreateFromScene(DateTime? locationExportDateTime,
SpawnPointParams[] parameters)` — `EFT.Game.Spawning/SpawnPointsCollection.cs:152-170`.
Called exactly once, from `BaseLocalGame.Run`:

```
SpawnPointsCollection spawnPoints = SpawnPointsCollection.CreateFromScene(
    DateTimeExtensions.LocalDateTimeFromUnixTime(location.UnixDateTime),
    location.SpawnPointParams);
```
(`EFT/BaseLocalGame.cs:526`) — `location` here is the client's `JsonType.LocationSettings.Location`
(server `base.json` mirror); `UnixDateTime` and `SpawnPointParams` are both plain
fields on it (`JsonType/LocationSettings.cs:357, :359`).

Mechanics (`SpawnPointsCollection.cs`):
1. `SpawnPointMarker[] array = FindObjectsProxy.FindUnityObjectsOfType<SpawnPointMarker>();`
   and `BotZone[] zones = FindObjectsProxy.FindUnityObjectsOfType<BotZone>();`
   (`:155-156`) — a **global** Unity object search across all loaded scenes, **not**
   routed through `LocationScene`'s registry. So spawn-point/bot-zone discovery at
   raid start works even if a scene's `LocationScene.SpawnPointMarkers`/`BotZones`
   arrays weren't wired — but `LocationScene.GetAllObjects<BotZone>()` is *also*
   used later by `LocalGame.vmethod_1` (`EFT/LocalGame.cs:172`) for the array
   handed to `BotsController.Init`, so both paths should agree in practice.
2. If `parameters != null` (i.e. the server sent `SpawnPointParams` — true for
   every normal raid, since `base.json` always has that array, even if empty):
   - `IsValid(logger, locationExportDateTime)` (`:172-193`) requires a
     `LocationExportInfo` component in the scene (see §2) **and**
     `locationExportInfo.DateTime <= locationDateTime` (i.e. the scene's baked
     export timestamp must be **no newer** than what the backend/`base.json`
     claims — `:185-192`). This is the exact "DateTime rule" the task asked
     about: it is a **staleness guard**, not a feature gate — its purpose is to
     force you to re-export `base.json`'s `SpawnPointParams`/`UnixDateTime`
     whenever the scene's spawn markers change in the SDK, so the two never
     silently drift.
   - If valid: `FixSpawns(logger, array, zones, parameters)` (`:196-250`)
     reconciles scene vs. backend:
     - Deletes any scene `SpawnPointMarker` whose `Id` is **not** present in
       `parameters` (`:199-211`, `DestroyImmediate`).
     - **Procedurally creates** a new `SpawnPointMarker` via
       `SpawnPointMarker.Create(spawnPointParams)` (`:217-223`) for every
       backend `SpawnPointParams` entry that has **no** matching scene marker —
       i.e. the scene does **not** need every spawn point hand-placed in the
       SDK; anything present only in `base.json`'s `SpawnPointParams` array
       gets instantiated at runtime (position/rotation come from
       `SpawnPointParams` itself, since there is no scene transform to read
       from — confirms `SpawnPointParams` carries a `Position`/`Rotation`,
       matching the server-side `SpawnPointParam.Position` field already seen
       in ArenaMode's own model).
     - For every remaining marker, `marker.FixParams(param, logger)` syncs its
       fields (Categories/Sides/BotZoneName/etc.) to the backend record
       (`:226-243`).
     - `ClearBotZonesSpawnMakers(zones)` (`:244, :252-258`) purges any
       now-null marker references from each `BotZone.SpawnPointMarkers` list.
   - If **invalid** (missing/newer `LocationExportInfo`): a single error is
     logged and the raw scene markers are used as-is, unmodified — see the
     "silently breaks server-authored spawn points" note in §2's table.
3. `CalculateCanSpawnPmcBots(zones)` (`:168, :260-266`) sets
   `BotZone.HasPmcBotSpawns` from whichever markers ended up assigned to each
   zone.
4. Result: `new SpawnPointsCollection(array.ToDictionary(marker => marker.SpawnPoint,
   marker => marker), logger)`.

**Practical implication for the SPIKE**: for a brand-new map with no
`base.json` `SpawnPointParams` authored yet, the simplest correct path is (a)
place `SpawnPointMarker`s directly in the Unity scene with their
`SpawnPointParams` baked at author-time (category `Player`, side `All`/`Usec`/
`Bear`, valid `Id`), and (b) keep `base.json`'s own `SpawnPointParams` array
**either empty** (so `parameters == null` is false but an empty array still
triggers the `IsValid`/`FixSpawns` path — every scene marker not in the
(empty) backend list would then be **deleted** at `:199-211`, so an empty
backend array is actually dangerous) **or exactly mirroring** the scene
markers' `Id`s once the map is stable. Safer for early iteration: omit/leave
`base.json`'s `SpawnPointParams` genuinely `null` so the whole reconciliation
branch is skipped and the scene's own baked markers are used verbatim — this
is the same "let scene markers stand alone" fallback the invalid-datetime case
uses.

---

## 4. NavMesh: how EFT bots actually use it

**[VERIFIED]** Grep across the full decompiled tree for `NavMeshAgent` returns
only `EFT/PlayerSpiritBase.cs` and `UnityUtils.cs` — **no** AI/bot code uses a
`NavMeshAgent` component. Bot movement (`BotMover.cs`) instead calls the
**static** `UnityEngine.AI.NavMesh` query API directly:
- `NavMesh.CalculatePath(_owner.Transform.position, position, -1, navMeshPath)`
  (`BotMover.cs:460`) — area mask `-1` = `NavMesh.AllAreas`, no named/costed
  area usage found anywhere in the bot movement/pathing files inspected
  (`BotMover.cs`, `BotCalcGoal.cs`, `BaseLogicLayer.cs`, `AICorePointHolder.cs`
  — no `agentTypeID`/area-name hits in any of them).
- `NavMesh.SamplePosition(pos, out hit, radius, -1)` used repeatedly
  (`BotMover.cs:464, 636, 640, 644, 648, 655, 659, 702, 763`) to snap a
  candidate position onto the nearest walkable navmesh surface.
- `BotZone.cs` also `using UnityEngine.AI;` and calls an `IsOnNavMesh(...)`
  helper per spawn marker inside `Init()` (`BotZone.cs:154`) — the return
  value's downstream effect was not traced this pass. **[OPEN]**

Actual bot **pathfinding decisions** (which cover/waypoint to move to) are
driven by BSG's own baked graph — `AICoversData`/`AITriangleData`/`GroupPoint`/
`NavGraphVoxelSimple` (`AICoversData.cs`) — a proprietary system distinct from
Unity's NavMesh, populated by the SDK's "AI bake" tool (per the Icebreaker
Playbook, "AI bake (covers/voxels/patrols)" is one of the systems it had to
recover from a retail rip). Unity's `NavMesh` is used only as a **generic
walkability oracle** (can I path here / snap this point onto walkable ground) —
not as the primary AI graph.

**No occurrence of `NavMesh.AddNavMeshData` anywhere in the decompiled
Assembly-CSharp.** This means EFT never explicitly registers `NavMeshData` at
runtime from code — it relies on Unity's classic behavior where a baked
`NavMeshData` object embedded directly in a *scene* (the pre-NavMeshComponents
workflow — "Window > AI > Navigation", baked and saved with the scene, not a
loose asset) becomes automatically active as soon as that scene loads, with no
explicit API call needed. This matches the user's own prior finding (Arena
map-port memory note) that the Equator/AutoService `_AI` scenes each contain a
`NavMeshData` object directly (41 / 30 tiles respectively).

**Answering the task's specific sub-question** — "whether a NavMeshData added
via `NavMesh.AddNavMeshData` at runtime works for bots": **yes, functionally**
**[INFERENCE, grounded in verified API usage]**. Since every bot-facing call
(`NavMesh.CalculatePath`, `NavMesh.SamplePosition`) goes through Unity's
*global* static NavMesh world, and Unity's runtime does not distinguish
NavMesh tiles that were scene-baked from ones added via
`NavMesh.AddNavMeshData()` (both just add walkable-surface data to the same
runtime world), a BepInEx plugin that loads a `NavMeshData` asset (e.g.
out of the map's own bundle) and calls `NavMesh.AddNavMeshData()` before any
bot pathing runs would satisfy every call site found in this pass. This has
**not** been runtime-tested — only reasoned from the API surface actually
called — so treat as a plausible, testable hypothesis for the SPIKE rather
than a proven fact.

---

## 5. Server side (SPT 4.1 C# server) — location contract, DB shape, map list

### 5.1 `base.json` — required vs. optional

The client's full field list is `JsonType.LocationSettings.Location`
(`JsonType/LocationSettings.cs:27-434`, ~90 public fields — see file for the
complete list). None of the fields are marked `[Required]`/non-nullable at the
C# level (plain JSON deserialization, missing fields just default: `0`/`false`/
`null`/empty). From the actual load-chain trace above, the fields that matter
for a raid to **start without erroring** are:
- `Scene` (`ResourceKey`) — **required**, empty `path`/`rcid` means no scene
  loads at all (confirmed empty on the dormant `suburbs`/`town` stub slots —
  see 5.3).
- `UnixDateTime` + `SpawnPointParams` — see §3; both can be left at defaults
  (`0` / `null`) to skip server-authored spawn reconciliation entirely and rely
  on scene-baked `SpawnPointMarker`s only.
- `Id`/`_Id` — used for exfil MongoID derivation
  (`ExfiltrationController.InitAllExfiltrationPoints`, `locationId.Add(...)`)
  and localization keys (`_Id + " Name"` / `" Description"` — per the
  Icebreaker Playbook's "map-select card" section, independently verified by
  them against `EFT.UI.LocationInfoPanel`, not re-verified in this pass).
- `waves`, `BossLocationSpawn`, `MinMaxBots`, `BotLocationModifier` — all
  consumed as plain arrays/objects with empty-tolerant loops throughout
  `LocalGame.Create`/`vmethod_1` (§1 steps 5, 7) — **optional**, empty is fine.
- `looseLoot.json`/`staticLoot.json`/`staticContainers.json`/`statics.json`/
  `staticAmmo.json`/`allExtracts.json` are **server-side sibling files**, not
  part of `base.json` — each becomes a `LazyLoad<T>`-wrapped field on
  `SPTarkov.Server.Core.Models.Eft.Common.Location` (`LooseLoot`,
  `StaticLoot`, `StaticContainers`, `StaticAmmo`, `AllExtracts` — see
  `D:/ArenaMapPort/analysis/decompiled-server/Location.cs:9-56`,
  **[VERIFIED against the 4.1.0 binary]**). They are all optional/nullable at
  the C# level; a map with none of them still passes `Location` construction
  fine, it just generates zero loot.

### 5.2 The DB shape is a **closed set** — you cannot add a new location id

`SPTarkov.Server.Core.Models.Spt.Tables.LocationTable`
(`D:/ArenaMapPort/analysis/decompiled-server/LocationTable.cs`, **[VERIFIED]**)
is a C# `record` with **hardcoded** `[JsonPropertyName]` properties, one per
known map id — `Bigmap`, `Develop`, `Factory4Day`, `Factory4Night`, `Hideout`,
`Interchange`, `Laboratory`, `Lighthouse`, `PrivateArea`, `RezervBase`,
`Shoreline`, `Suburbs`, `TarkovStreets`, `Labyrinth`, `Terminal`, `Town`,
`Woods`, `Sandbox`, `SandboxHigh` (19 properties) plus `Base` (a
`LocationsBase`, itself basically just a `Paths` list — its own `Locations`
sub-object is a pure `[JsonExtensionData]` bag with **no** typed members,
`Locations.cs`). `GetDictionary()`
(`LocationTable.cs`, `HydrateDictionary()`) builds its lookup **only** from
`typeof(LocationTable).GetProperties().Where(p => p.PropertyType ==
typeof(Location) && p.Name != "Item")` — i.e. reflection over the fixed
property list above, nothing else. `GetLocation(name)` first tries this
dictionary via `GetMappedKey(name)` (a static `_locationMappings` dict mapping
both the lowercase folder-name spelling, e.g. `"bigmap"`, and the PascalCase
property name, e.g. `"Bigmap"`, to the property name) and only falls back to
`ObjectExtensions.GetByJsonProperty<Location>(this, name.ToLowerInvariant())`
(still reflection over the same fixed properties) if that fails.

**Consequence**: dropping a new folder `database/locations/<newid>/` on disk
does **nothing** — there is no property for it, so it never reaches
`GetDictionary()`/`GetLocation()`, and every consumer that matters
(`LocationController.GenerateAll`, `LocationLifecycleService
.GenerateLocationAndLoot`, ArenaMode's own `ArenaMapCatalog`) is built on top
of those two methods. This is **independently confirmed** by the Icebreaker
Backport Playbook (written against SPT 4.0, i.e. an earlier server build):
*"SPT's `Locations` record is closed to new ids. Pick a dormant shipped stub
(Icebreaker rides Suburbs)."* — the 4.1.0 binary shows the exact same
closed-property-set design, just refactored into a different class name
(`LocationTable` vs whatever 4.0 called it) and reflection-driven instead of
switch-driven.

**Where a per-raid clone is actually produced** (this is the hook ArenaMode's
own `GenerateLocationAndLootPatch` targets):
```
public LocationBase GenerateLocationAndLoot(MongoId sessionId, string name, bool generateLoot = true)
{
    Location location = locationTable.GetLocation(name);
    LocationBase locationBase = cloner.Clone(location.Base);
    ...
```
(`SPTarkov.Server.Core.Services.InRaid.LocationLifecycleService`, dumped at
`D:/ArenaMapPort/analysis/decompiled-server/LocationLifecycleService.cs:260-263`,
**[VERIFIED]**). `name` is whatever location id string the client sent — it
must resolve through the closed set above or `GetLocation` returns `null` and
`location.Base` NREs on the next line.

### 5.3 Practical consequence for the SPIKE: which slot to ride

Directly inspected every currently-empty/nullable `LocationTable` property's
on-disk `base.json`:

| id | `Scene` | `SpawnPointParams` | `exits` | `Enabled` | `Locked` | Verdict |
|---|---|---|---|---|---|---|
| `suburbs` | `{path:"", rcid:""}` | 0 | 0 | `false` | `true` | **Fully empty stub — no client bundle exists either (no `*_preset.bundle` under `StreamingAssets/Windows/maps` references it). Best candidate.** |
| `town` | `{path:"", rcid:""}` | 0 | 0 | `false` | `true` | **Same — fully empty, no client bundle. Second candidate.** |
| `privatearea` | not directly checked for `Scene`, but `IsSecret:true`, `Locked:true`, `Enabled:false` | — | — | `false` | `true` | Plausible third candidate, not scene-verified this pass. **[OPEN]** |
| `develop` | `{path:"maps/develop_preset.bundle", rcid:"develop.scenespreset.asset"}` | 11 | 6 | `false` | `false` | **NOT a clean stub** — this is SPT's own dev/arena test map with real content (`Name:"Arena"`). Avoid — you'd be fighting existing scene/spawn data. |
| `terminal` | not checked | — | — | `true` | `true` | Already `Enabled:true` for something; avoid. |
| `hideout` | not applicable (special-cased, `Location.IsHideout` gate in `BaseLocalGame.Run`) | — | — | `false` | `true` | Do not touch — the hideout has dedicated non-raid load logic (`TarkovApplication.cs:242, :273`, `Scenes.HideoutScenesPreset`). |

No `*_preset.bundle` exists under
`EscapeFromTarkov_Data/StreamingAssets/Windows/maps/` for `suburbs`, `town`, or
`privatearea` (the full list of shipped preset bundles was enumerated directly:
`arena_preset`, `city_preset`, `customs_preset`, `develop_preset`,
`factory_day_preset`, `factory_night_preset`, `laboratory_preset`,
`labyrinth_preset`, `lighthouse_preset`, `rezerv_base_preset`,
`sandbox_high_preset`, `sandbox_preset`, `shopping_mall`, `shoreline_preset`,
`woods_preset`) — confirming `suburbs`/`town` have **zero** content on either
side of the fence, matching Icebreaker's documented approach exactly.
**Recommendation: ride `suburbs` or `town`.**

### 5.4 How a location appears in client map selection

`LocationController.GenerateAll` (`client/locations` route,
`D:/ArenaMapPort/analysis/decompiled-server/LocationController.cs:24-47`,
**[VERIFIED]**) iterates `locationTable.GetDictionary()` and returns **every**
entry that has a non-null `Base` — it does **not** filter by `Enabled`/
`Locked` server-side (only a `Debug`-level log line if `Base` itself is null).
The client-side `JsonType.LocationSettings.Location.Enabled`/`.Locked` booleans
(`JsonType/LocationSettings.cs:247, :253`) are therefore what the menu UI must
filter on — this happens somewhere in the client's map-select screen code,
which was **not traced in this pass** (out of scope: no "MatchmakerSelect…"
class was opened). **[OPEN — UI filter class not identified by name/line]**.
Practical effect either way: flipping `suburbs`'s/`town`'s `base.json`
`Enabled: true` (and `Locked: false`) is necessary and, per the server-side
code just read, sufficient — the server will hand the location out to the
client regardless of the flags; it's purely a client-rendered gate.

### 5.5 Static ID reference — `JsonType.LocationSettings.Location.AvailableMaps`

`JsonType/LocationSettings.cs:231-235` — a client-side constant array:
`"develop", "Woods", "factory4_day", "factory4_night", "bigmap", "Shoreline",
"Interchange", "RezervBase", "laboratory", "Lighthouse", "TarkovStreets",
"Sandbox", "Sandbox_high"` (13 ids). Notably this does **not** include
`"labyrinth"`, `"suburbs"`, `"town"`, `"privatearea"`, `"terminal"`, or
`"hideout"` — yet ArenaMode's own `ArenaMapCatalog.LabyrinthId = "labyrinth"`
is documented as shipped and playtested. This proves `AvailableMaps` is **not**
a raid-start gate (a non-member id clearly still works) — it is very likely
just a legacy/display list consumed by some narrower UI surface (the array's
only other use found in this pass is `NightTimeAllowedLocations`, `:426`, a
day/night helper). **[INFERENCE — not all consumers of `AvailableMaps` were
traced]**. Do not treat membership in this array as required for the SPIKE.

---

## 6. ArenaMode integration points for a new map id (read-only — no edits made)

ArenaMode never registers a brand-new `LocationTable` slot — consistent with
§5.2, it only **mutates the per-raid clone of an existing vanilla location**.
The exact files/methods a new map id would need to touch, purely by reading
the existing pattern:

- **`D:/SPT41/Development/ArenaMode/ArenaMode.Shared/ArenaMapCatalog.cs`**
  - `FactoryDayId`/`FactoryNightId`/`LabyrinthId`/`LaboratoryId`/`CustomsId`
    (`:12-16`) — the five supported vanilla ids today, all pre-existing
    `LocationTable` slots (`factory4_day`, `factory4_night`, `labyrinth`,
    `laboratory`, `bigmap`).
  - `IsSupportedMap(locationId)` (`:26-31`) — the master allow-list check; a
    new id needs a new `IsSupportedMap`/`IsFactoryVariant`-style branch.
  - `DefaultClientAllowlist`/`AllowlistContains` (`:18-52`) — the
    BepInEx-config-facing allow-list string a new id would be appended to.
  - `ShouldDisplayNativeLocation`/`CanSelectNativeLocation`/
    `AnyMapAvailable` (`:64-101`) — map-select UI gating logic keyed off the
    same id constants.
- **`D:/SPT41/Development/ArenaMode/ArenaMode.Shared/ArenaAreaCatalog.cs`** —
  per-map sub-area geometry (`ArenaAreaDefinition.MapId`), `TryResolve`
  (`:472-497`) ties a bounded-area id to one of the `ArenaMapCatalog` ids. A
  new full-map port wouldn't need a new entry here (areas are optional
  sub-regions), only a new map port that also wants a bounded sub-arena would.
- **`D:/SPT41/Development/ArenaMode/ArenaMode.Server/Services/ArenaLocationMutator.cs`**
  — `Mutate(LocationBase location, ArenaMapConfig map, ArenaModeConfig config, ...)`
  (`:44-157`) is the per-raid mutation entry point (raid timer, bot caps, wave
  generation, boss spawns, loot stripping) — a new map id would need a
  matching `ArenaMapConfig` entry (looked up by name in `ArenaContext.Config
  .Maps`, a `config.json`-backed dictionary **not read in this pass**
  — `[OPEN]`, referenced only through
  `GenerateLocationAndLootPatch.cs:45` below).
- **`D:/SPT41/Development/ArenaMode/ArenaMode.Server/Patches/GenerateLocationAndLootPatch.cs`**
  — the actual Harmony/SPT-reflection hook:
  `GetTargetMethod()` (`:18-23`) resolves
  `LocationLifecycleService.GenerateLocationAndLoot` (confirmed identical to
  §5.2's decompiled method) via reflection; the `[PatchPostfix] Postfix(MongoId
  sessionId, string name, LocationBase __result)` (`:25-84`) checks
  `ArenaContext.Config.Maps.TryGetValue(name.ToLowerInvariant(), out var
  mapConfig) && mapConfig.Enabled` (`:44-51`) before calling
  `ArenaLocationMutator.Mutate(__result, mapConfig, ...)` — this is the
  postfix pattern (fires on the SAME per-raid clone `LocationLifecycleService
  .GenerateLocationAndLoot` produces, see §5.2) any new-map integration would
  extend, not replace.

No file read in `ArenaMode.Server`/`ArenaMode.Shared` touches `DatabaseService`,
`LocationTable`, or any DB-registration API directly — the whole mod is built
on "reuse an existing vanilla slot's per-raid clone," which is the same
constraint this report's §5.2 independently derives from the closed
`LocationTable` record. A genuinely new (not-yet-existing-in-SPT) location id
is therefore **out of reach for a server-side C# mod under SPT 4.1** the same
way it was for Icebreaker under 4.0 — the SPIKE should plan to ride
`suburbs`/`town` (§5.3), exactly as ArenaMode rides `bigmap`/`factory4_day`/
`labyrinth`/`laboratory` today, rather than trying to invent a new id.

---

## 7. Minimal viable scene/location checklist, ordered by touch sequence

1. **Pick a dormant `LocationTable` slot** — `suburbs` or `town` (§5.3). Do
   not invent a new id (§5.2/§6).
2. **`base.json` for that slot**: set `Scene.path`/`Scene.rcid` to your new
   bundle+asset; set `Enabled:true`, `Locked:false`, a real `Name`; leave
   `SpawnPointParams`/`UnixDateTime` at defaults (`null`/`0`) for the first
   pass so scene-baked markers are used verbatim (§3) — sync them for real
   once the scene stabilizes.
3. **Unity scene, minimum components to avoid an NRE at load** (§2):
   - Exactly one `LevelSettings` (required — TarkovApplication NREs on its
     `Singleton` right after scene load if absent).
   - Exactly one `LocationScene` with **every array field assigned to a real
     (possibly empty) array, never left `null`** — `WorldInteractiveObjects`
     specifically will NRE `LocationScene.Awake()` if null.
   - At least one `BotZone`, with its `PatrolWays` field set to `[]` (not
     null) — required or `AICoversData.CreateOrFind` NREs the whole raid start.
   - One `LocationExportInfo` with a sane `_unixDateTime` (only matters once
     you start shipping non-null `SpawnPointParams` in `base.json`).
   - At least one `SpawnPointMarker` with category `Player` and a side your
     test profile matches, or the local player has nothing to spawn on
     (logical dead-end, not a single crash line — §2/§3, **[INFERENCE]**).
4. **NavMesh**: bake it into the scene at Editor build time (classic
   Window>AI>Navigation workflow) so it's auto-active on scene load; a runtime
   `NavMesh.AddNavMeshData()` fallback from a plugin is plausible per §4 but
   untested. Not required for raid **start**, only for bots to path once you
   add them (later milestone per the project's own plan).
5. **Everything else** (doors beyond an empty array, exfiltration points,
   environment/indoor triggers, camera/post-process prefab, physics broadphase
   bounds, occlusion culling) is **soft** — confirmed to degrade gracefully
   rather than crash, with the single flagged exception of
   `PerfectCullingCrossSceneSampler.InitializeAutoCulling` which is wrapped in
   a catch-and-rethrow and was not traced deep enough to rule out a hard
   dependency on baked culling data (**[OPEN]** — test this early).
6. **Server-side loot files** (`looseLoot.json`, `staticLoot.json`, etc.) can
   all be omitted/empty for the geometry-only SPIKE — they're
   `LazyLoad<T>`-wrapped and optional at the model level (§5.1).
