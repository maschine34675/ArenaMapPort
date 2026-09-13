# Reverse-read: SPT-InterchangeRework (SPT 4.1)

Sources: `D:/sptmaps/_sources/SPT-InterchangeRework-main` (repo root has: `Directory.Build.props`,
`InterchangeRework.Client/`, `InterchangeRework.Server/`, `InterchangeRework.Shared/`,
`InterchangeRework.Tests/`, `LICENSE`, `README.md`, `SPT-InterchangeRework.slnx`, `bundles/`, `docs/`,
`media/`, `scripts/`). Release: `D:/sptmaps/SPT-InterchangeRework-1.0.0-SPT4.1`.

All paths below use `/`. Line numbers refer to the files as read on 2026-09-13.

---

## 1. Geometry source and Unity-side build

**Fact, not inference:** the repo contains **no Unity project at all** — no `Assets/`, no `.unity`
scenes, no editor scripts, nothing that builds an AssetBundle. The README says this outright:

> "This repository is the mod's source: the client plugin, the server mod, the shared rules and the
> tests. The map itself is rebuilt Escape from Tarkov data and is not here. It travels in the release
> archive, and the tooling that produces it from a retail installation is not published."
> — `README.md:62-64`

`scripts/Package-Release.ps1:6-9` confirms the same split at the packaging layer: "Three payloads never
live in git: the two scene bundles **the SDK builds**, and the packed culling grid... They travel here
instead." It calls the bundle-producing tool "the SDK" without naming or shipping it. The footer the
script writes into the release's `README.txt` (`scripts/Package-Release.ps1:93-94`) again says "Source,
tooling and the method for rebuilding the archive from a retail install" live at the GitHub URL — i.e.
even the published repo does not carry the Unity/build side, only a promise that it exists somewhere.

So: **geometry comes from a rip of retail EFT 1.1**, not an authored/backport Unity project checked
into this repo. Provenance is stamped in `bundles/bundle-stamp.json`:
```
"RipSource": "interchange-export-115", "RipBuild": "1.1.5.47242"
```
(`bundles/bundle-stamp.json:9-10`, mirrored in the `BundleStamp` C# type at
`InterchangeRework.Shared/BundleStamp.cs:9-21`). This is the retail **1.1 Shopping_Mall** export
(18 scenes), rebuilt into one bundle — same rip-then-rebuild methodology the README's Credits section
attributes to Manimal's Icebreaker/Labs backports (`README.md:78-80`): "the scene swap, the sidecar
restore, the StreamingAssets redirect. This port follows that method throughout."

**Unity version:** not stated anywhere in the repo (no `ProjectVersion.txt`, no comment). The only
Unity-adjacent version evidence is indirect: the client project compiles against
`$(SptRoot)\EscapeFromTarkov_Data\Managed` (`InterchangeRework.Client/InterchangeRework.Client.csproj:8,
22-44`), i.e. SPT 4.1's own Managed folder (Unity 2022.3.43f1, matching our project context), and the
bundle must obviously have been built by a Unity Editor whose AssetBundle format that Mono/2022.3
client can load — but the repo never says which Editor version produced it, and does not need to
(BuildInfo/tooling is external).

**What the two bundles contain, evidenced by the code that consumes them:**
- `interchange_rework.bundle` (3.2 GB per `README.md:34`, `3186795571` bytes per
  `bundles/bundle-stamp.json:6`) — the **18 `Shopping_Mall_*_IR` scenes** (geometry, materials, audio
  objects, etc.), loaded as an `AssetBundle` and its scene paths swapped into the vanilla
  `ScenesPreset` (`InterchangeRework.Client/PresetSwap.cs:63-89`, scene names enumerated in
  `bundles/bundle-stamp.json:5-22` and `InterchangeRework.Shared/SceneNames.cs:14-30`).
- `interchange_rework_navmesh.bundle` — **only** a `NavMeshData` asset, loaded by type (not name) via
  `bundle.LoadAllAssets(typeof(NavMeshData))` (`InterchangeRework.Client/NavMeshExtension.cs:68-89`).
  It is a **separate bundle** from the scene bundle, loaded independently and only when
  `BotsController.Init` is about to run (see §5).

**BuildAssetBundleOptions / bundle naming:** not visible — that's inside the unpublished "SDK". What
is visible is the **consumer-side naming convention and file layout the SDK's output must match**:
`PresetSwap.BundleFile = "interchange_rework.bundle"`, `PresetSwap.StampFile = "bundle-stamp.json"`
(`InterchangeRework.Client/PresetSwap.cs:19-20`), `NavMeshExtension.BundleName =
"interchange_rework_navmesh.bundle"` (`InterchangeRework.Client/NavMeshExtension.cs:18`), all expected
directly beside the plugin DLL (`Plugin.PluginDir`, set in `InterchangeRework.Client/Plugin.cs:42`).

**Scene naming convention:** every one of the 18 retail scene names gets an `_IR` suffix
(`InterchangeRework.Shared/SceneNames.cs:16,19`), e.g. `Shopping_Mall_Terrain` →
`Shopping_Mall_Terrain_IR`. One scene, `Shopping_Mall_Scripts`, is deliberately **not** swapped
(`SceneNames.NativeSceneNames`, `InterchangeRework.Shared/SceneNames.cs:31-33`) — it stays the client's
own vanilla scripts scene ("Adding a name here is the rollback for any scene"). This is also where the
map's `BLOCKER` boundary colliders live (see §4) — proof the mod keeps some vanilla scenes in play
side-by-side with the ripped ones rather than replacing 100% of the preset.

---

## 2. Client loading: how the bundle scene is loaded and activated

Single Harmony patch, no LocationScene/GameWorld/ScenesPreset subclassing:

```
[HarmonyPatch(typeof(LoadScenesFromPresetOperation), nameof(LoadScenesFromPresetOperation.LoadPresetAsync))]
[HarmonyPrefix]
static void BeforePresetLoad(ScenesPreset preset)
```
(`InterchangeRework.Client/PresetSwap.cs:32-34`)

Mechanism, in order:
1. **Gate on preset identity.** `preset.ServerName == "interchange"` (`SceneNames.PresetServerName`,
   `InterchangeRework.Shared/SceneNames.cs:15`); anything else returns immediately
   (`PresetSwap.cs:40`). This is how it "coexists with vanilla loading" — every other map's preset load
   goes through the same prefix and is a no-op.
2. **Resolve the player's choice.** `SessionChoice` is asked once per raid via
   `VariantSync.HostChoice()` (`PresetSwap.cs:42`, see §6/Fika below) or falls back to
   `Plugin.DefaultIsRework` (the BepInEx config default) if unset (`PresetSwap.cs:43`).
3. **Decline path:** if the player chose vanilla, or the bundle can't be loaded/validated,
   `RevertEntries` rewrites any already-`_IR` scene keys in the preset back to native names
   (`PresetSwap.cs:44-45,136-160`) — this is the "coexistence" mechanism at the data level: the
   `ScenesPreset` object is **mutated in place and cached for the process lifetime**
   (`PresetSwap.cs:41` comment, `:158-159`), so every subsequent raid this session must be explicitly
   reverted if the map is skipped, not just left alone.
4. **Load & validate the bundle.** `EnsureBundle()` (`PresetSwap.cs:63-89`): `AssetBundle.LoadFromFile`
   beside the DLL, reads `bundle-stamp.json`, and calls
   `BundleStampCheck.Compare(stamp, BuildInfo.Version, actualBytes, actualSceneNames)`
   (`InterchangeRework.Shared/BundleStamp.cs:33-58`) — checks mod-version major.minor match, exact byte
   count, and that the bundle's actual scene-path list is **exactly** the 18 required `_IR` names (no
   more, no fewer). A failed stamp check aborts the swap and reverts (`PresetSwap.cs:81-86`).
5. **Rewrite scene keys by reflection.** `ScenesPreset._scenesResourceKeys` (private field, found via
   `AccessTools.Field`, `PresetSwap.cs:94`) is an `IList` of key objects each with private `path` and
   `rcid` string fields (`PresetSwap.cs:104-105`). For every base-name path that matches
   `SceneNames.SwappedBaseNames`, `path` and `rcid` are rewritten from `.../Shopping_Mall_X.unity` to
   `.../Shopping_Mall_X_IR.unity` (`SceneNames.RewriteScenePath`,
   `InterchangeRework.Shared/SceneNames.cs:64-70`). Unity's `SceneManager` then finds a scene of that
   name in **any loaded AssetBundle** — no explicit `SceneManager.LoadSceneAsync` call is needed,
   because the vanilla engine code that follows this prefix does the loading itself using the now-
   rewritten preset (comment at `SceneRegistry.cs` top and `PresetSwap.cs:14-15`: "SceneManager finds a
   scene in any loaded bundle by name").
6. **Per-scene activation hook.** `Plugin.cs:45-55` subscribes to `SceneManager.sceneLoaded` globally
   once at `Awake()`, filters to `SceneNames.IsIrScene(scene.name)`, and fires an internal
   `Plugin.IrSceneLoaded` event that ~15 independent repair modules subscribe to (list at
   `Plugin.cs:111-165`, e.g. `AiCoverBakeRepair.Install` at `Plugin.cs:131`). Each subscriber runs in
   its own try/catch (`Plugin.cs:50-54`) so one repair failing does not break another.
7. **A companion Harmony prefix, `LocationSceneAwakeGuard`** (on `LocationScene.Awake`,
   `InterchangeRework.Client/LocationSceneAwakeGuard.cs:9-21`), strips null entries from
   `EventSoundContentChangers` before the vanilla `Awake()` runs, because a null there (an artifact of
   the rip) throws and aborts everything after that line in vanilla `Awake`.
8. **Scene-object registry rebuild** (`InterchangeRework.Client/SceneRegistry.cs`): after each `_IR`
   scene loads, it rebuilds `LocationScene`'s internal typed-object arrays
   (`_typeToContainer`, found via reflection at `SceneRegistry.cs:18`) from
   `GetComponentsInChildren` over the scene roots (`SceneRegistry.cs:81-104`), because those arrays are
   normally populated once at `Awake()` time on the *original* scene content and the rip leaves them
   empty/stale. It also assigns deterministic `Id`/`NetId` to every `WorldInteractiveObject` via
   `InteractiveIdAssigner` (`SceneRegistry.cs:42-48`, policy in `InterchangeRework.Shared/`).
9. **Bundle unload.** `PresetSwap.UnloadIfIdle()` unloads the AssetBundle once no `_IR` scene remains
   loaded (`PresetSwap.cs:162-169`) — not shown to be called from a specific hook in the files read, but
   present as public API for raid-end cleanup.

**Coexistence with vanilla:** every other location's preset load hits the same prefix and returns at
line 40 (`ServerName` mismatch) — zero cost, zero side effect. Within Interchange itself, the vanilla
`Shopping_Mall_Scripts` scene (never swapped) stays loaded alongside the 17 swapped `_IR` scenes,
meaning the mod treats the ripped content purely as an **art/geometry override layered under** the
existing scripts/gameplay scene, not a full scene-list replacement.

---

## 3. Shaders

**`material-shaders.json`** (shipped at `plugin-data/material-shaders.json`, read by
`InterchangeRework.Client/MaterialShaderRebind.cs:80,92-127`) is a **per-material sidecar recorded at
rip time**, shape:
```json
{ "<material name>": { "Shader": "<original shader name>", "Textures": { "<property>": "<texture name>" } }, ... }
```
It exists because a ripped material that references a shader the target Mono client doesn't have loads
onto Unity's `Hidden/InternalErrorShader` (magenta) and **forgets its own intended shader name** — the
JSON is the only place that name survives (`MaterialShaderRebind.cs:480-485`, `IntendedName`).

**Runtime rebind — not editor stand-in shaders, actual game shaders:**
1. On every `_IR` scene load, all currently-loaded `Shader` objects in the process are indexed by name
   into `_byName` (`Resources.FindObjectsOfTypeAll<Shader>()`,
   `MaterialShaderRebind.cs:316-326`) — i.e. shaders come from **the running EFT client's own shader
   set** (matches our project-context finding that all 17 Equator/Arena shader names exist in SPT's own
   shaders bundle — same principle applies to Interchange's retail shader names existing in EFT's own
   shader set).
2. For each material, `RebindNow` (`MaterialShaderRebind.cs:487-541`) determines the *intended* shader
   name (the material's own shader name if it isn't the error shader, else the JSON lookup), then:
   - looks for a loaded shader of that exact name that has a drawing pass (`ShaderPasses.CanDraw`,
     `InterchangeRework.Client/ShaderPasses.cs:18-30`, checks for `DEFERRED`/`FORWARDBASE`/etc.
     `LightMode` tags since EFT renders deferred) — assigns it directly
     (`MaterialShaderRebind.cs:509-519`);
   - else tries **`Shader.Find(intended)`** to pull it from Unity's "always included" set
     (`MaterialShaderRebind.cs:522-533`) — this **is** the `Shader.Find` mechanism the task asked about;
   - else falls back to a hard-coded `Substitutes` table (`MaterialShaderRebind.cs:28-76`) mapping a
     handful of known-untranslatable shader names (e.g. `Custom/MountainDeferred`,
     `ANGRYMESH/PBR Rocks/PBR BlendTopDetail (Legacy)`, `Particles/Explosion`,
     `Cloth/ClothShader_backface`, `Legacy Shaders/Diffuse`) onto a close analog already in the game
     (e.g. `p0/Reflective/Bumped Specular SMap`, `MK4/Rock`), with per-substitute texture-slot remapping,
     fill textures, float/color defaults (`MaterialShaderRebind.cs:146-267`).
   - MicroSplat terrain shaders are a special case: a **generated per-tile shader name**
     (`MicroSplat/Slice_\d+_\d+`, `GeneratedShader` regex, `MaterialShaderRebind.cs:14-16`) identifies a
     specific tile's compiled program, not a reusable shader — the code explicitly **does not** rebind a
     name-matching generated shader if the ripped copy can already draw, because a same-named shader in
     the base game belongs to a different terrain tile (`MaterialShaderRebind.cs:499-507`).
3. A `Sweeper` coroutine re-scans at +5s and +25s into the raid for materials still on the error shader
   that a scene-graph walk missed (dynamically instantiated, e.g. from `WireGenerator` clones —
   `RebindWireClones`, `MaterialShaderRebind.cs:400-447`).
4. `AdoptDonorSettings` (`MaterialShaderRebind.cs:189-240`) copies authored float/color/vector/texture
   property values from **an already-correctly-shaded material found live in the scene** ("a donor") onto
   the rebound material, because assigning `.shader` resets undeclared properties and the rip carries no
   authored values for properties the new shader expects.

**No editor-side stand-in shaders are shipped or referenced** — the whole scheme assumes the rip
captures shader *names* only (not compiled programs) and repairs entirely by matching against the
target game's own compiled shader set at runtime.

---

## 4. Physics/ballistics: collider layers and BallisticCollider/material data

**Key finding: there is no dedicated collider-layer or `BallisticCollider`/physics-material repair
module anywhere in the client or shared code.** Searched broadly
(`grep -rn -i "\.layer\b|gameObject\.layer|LayerMask|physicMaterial"` across
`InterchangeRework.Client` and `InterchangeRework.Shared`) — zero matches. No `layer name -> index`
mapping table exists in this codebase.

Inference (not directly stated, but strongly supported): because the map is a **binary rip of the exact
same EFT codebase/class library** (same `Assembly-CSharp`, same layer table, same
`BallisticCollider`/`InteractableCollider`/physic-material component types) rather than a hand-authored
scene in a different engine, every `Collider`'s `gameObject.layer` and its `PhysicMaterial`/ballistic
data come through the AssetBundle **already correct** — a GameObject exported from EFT 1.1 that was on
layer "HighPolyCollider" (or whatever EFT calls it) keeps that same integer layer index when its data is
serialized into the new bundle and loaded by the EFT-based SPT 4.1 client, because both are the same
Unity project's layer table by construction (this mirrors the project context's own finding for the
Arena rip: "same codebase/class names as EFT"). No runtime remap is needed or attempted.

What *is* present, and is physics-adjacent:
- **`BlockerRepair.cs`** (`InterchangeRework.Client/BlockerRepair.cs:11-13`): "Replaces SPT's map
  boundary with retail's from `plugin-data/blockers.json`. The BLOCKER colliders live in a scene that
  stays native, so they are **disabled rather than destroyed**." This confirms the map-boundary
  colliders are **vanilla SPT `BLOCKER`-layer colliders in the untouched `Shopping_Mall_Scripts` scene**,
  matched against retail box positions from a sidecar (`Box` class with Position/Rotation/Size,
  `BlockerRepair.cs:19-31`) and toggled — not a rip-carried collider set at all for this particular
  purpose.
- **`BotCoverBoundsRepair.cs`** (`InterchangeRework.Client/BotCoverBoundsRepair.cs:7-9,24-39`) rebinds
  each `BotCoverBounds.Collider` reference to the sibling `BoxCollider` on the same GameObject when the
  rip left the reference null — a **component-reference** repair, not a layer/physics-material fix.
- **`DoorRewire.cs`** and **`WheelDriveRepair.cs`** exist (interactive/vehicle component wiring,
  `Plugin.cs:157-158`) but were not read in full; based on their names and the `Install()` call sites
  they are also reference-rewiring, not collider-layer remaps.

**Conclusion for the port:** ballistics/footsteps/hit-detection apparently need **no explicit
layer-index or physic-material handling** when the source is a same-codebase rip — this is a materially
different situation from a cross-engine/cross-project port and should be validated directly against the
Arena rip (Arena is IL2CPP/Unity 6, still "same codebase/class names as EFT" per our project context,
so the same assumption likely holds, but colliders should be spot-checked for layer index once meshes
are in the SPT client).

---

## 5. NavMesh + bots

**Navmesh bundle build:** not in this repo (same "SDK" black box as §1). The release ships
`interchange_rework_navmesh.bundle` containing exactly one `NavMeshData` asset
(loaded by type, `NavMeshExtension.cs:81`, comment: "By type, not name: the asset is called whatever the
editor named it at bake time" — confirms it was Unity's ordinary `NavMeshSurface`/`NavMeshBuilder` bake
workflow, done in an editor project outside this repo, then extracted into its own bundle).

**Runtime application — `NavMeshExtension.cs`:**
```
[HarmonyPatch] static MethodBase TargetMethod() => AccessTools.Method(typeof(BotsController), nameof(BotsController.Init));
static void Prefix() { ... NavMesh.RemoveAllNavMeshData(); _instance = NavMesh.AddNavMeshData(data); ... }
```
(`InterchangeRework.Client/NavMeshExtension.cs:25-53`) — a Harmony **prefix on `BotsController.Init`**
(not a `LocationScene`/`GameWorld` patch). Sequence: gate on `IrRaid.IsLoaded()` (any `_IR` scene
currently loaded, `IrRaid.cs:10-18`); lazily load the navmesh bundle once per process
(`NavMeshExtension.cs:63-100`, kept alive across raids via `HideFlags.DontUnloadUnusedAsset`); on every
raid, first `Drop()` any previously-added instance, then **`NavMesh.RemoveAllNavMeshData()` followed by
`NavMesh.AddNavMeshData(data)`** — comment explains the ordering is load-bearing ("Remove first, then
add; the reverse drops ours along with theirs") and that this fully **replaces** the map's own (partial)
navmesh with a full-map bake, logging vertex/triangle counts before/after
(`NavMeshExtension.cs:41-53`). Cleanup is wired to `SceneManager.sceneUnloaded` for `_IR` scenes
(`NavMeshExtension.cs:35-39`). This is exactly `NavMeshData.AddNavMeshData` as the task hypothesized.

**`aibake.bin` format** (`InterchangeRework.Client/AiCoverBakeRepair.cs`) — a small custom binary sidecar
for `AICoversData` (bot cover points), **not** the navmesh itself:
- Header: magic bytes `'I','R','A','I'` + `uint32` version (currently `1`)
  (`AiCoverBakeRepair.cs:14-15,84-88`).
- `uint32 pointCount`, `uint32 wayCount` (`:89-90`).
- Per point (`:99-149`): `int32 id, index, neighborType, coverLevel, coverType, special,
  environmentType, idEnvironment, connectionGroup, tiltType, parentNavPoint, corePointId, placeId`,
  `byte flags` (bitflags `AlwaysGood=1, CanLookLeft=2, CanLookRight=4, BordersLightHave=8,
  IsGoodInsideBuilding=16`, `:17-21`), then 5×`Vector3` (`Position` implicit via constructor,
  `FirePosition`, `WallDirection`, `LeftBorderLight`, `RightBorderLight`, `:116-120`), a `float`
  `_defenceLevel` and `int32 distanceCheckSum` (`:121-122`), then a `uint16` neighbour-way-id count
  followed by that many `int32` way ids (`:142-145`).
- Per way (`:154-166`): `int32 id, int32 idTarget, float dist`; ways whose target id isn't in the point
  set are dropped with a warning rather than throwing (`AICoversData.RestoreData` indexes unguarded).
- Applied to the live scene's `AICoversData` component only if it's empty (bundle-carried data always
  wins, `:44-50`) — i.e. this is a **fallback fill**, not an override.
- Generation tooling for `aibake.bin` is not in this repo (consistent with §1's "SDK" boundary).

**`spawn-probe.json`** (`InterchangeRework.Client/NavMeshProbe.cs`) is a **diagnostics-only** sidecar,
`[{"Zone": "...", "Position": [x,y,z]}, ...]` (parsed at `NavMeshProbe.cs:108-117,212-230`), used purely
to log (a) whether each injected spawn point samples onto the loaded navmesh
(`NavMesh.SamplePosition`, `:219`) and (b) zone-to-zone path connectivity via
`NavMesh.CalculatePath`/a raw `NavMeshQuery` for the 4096-node-limited API's partial results
(`:123-202`). It does not feed any gameplay system.

**BotZone handling:** no `BotZone`-named file/class appears in the client tree. Bot spawn-point
"CorePointId" linkage instead happens **server-side**: `Spt5LocationMod.CorePointsFromOurScene`
(`InterchangeRework.Server/Spt5LocationMod.cs:158-170`) reads `db/spawns.json`'s
`SpawnPointParams[].Id -> CorePointId` map and `MergeSpawnPoints`
(`Spt5LocationMod.cs:172-188`) stamps that `CorePointId` onto every bot-capable spawn point taken from
the SPT 5.0-shaped `base.json`, falling back to "5.0's id, which our scene may not have" with a
client-side nearest-point fallback noted as `StartCorePointRepair` (file present,
`InterchangeRework.Client/StartCorePointRepair.cs`, not read in full).

**`LocationExportInfo`:** not found under that name anywhere in the repo (grep found no hits) — either
this project doesn't use/need that type, or SPT 4.1's API renamed/removed it. Not conclusively resolved
here.

---

## 6. Server side: new location vs. replacing an existing one

**Two full `Location` objects, live-swapped by reference — not two location ids.**
`InterchangeRework.Server/Variants/VariantRegistry.cs`:
- At `PostLoad`, `Register("interchange")` (`:36-51`) reads SPT's existing `interchange` `Location` via
  `locationTable.GetLocation(locationId)`, keeps it untouched as `_vanilla["interchange"]`, and
  `cloner.Clone()`s a second copy into `_reworked["interchange"]` (`:45-46`).
- `Install(locationId, variant)` (`:61-70`) picks the wanted dictionary and does
  `locationTable.GetDictionary()[locationTable.GetMappedKey(locationId)] = location;` — i.e. it
  **replaces the object the live location table points to**, in place, per raid.
- Crucially, per `docs/EXTENDING.md:12-16`, **both variants report the same `locationId`
  ("Interchange")** deliberately, so "quests, insurance and statistics see one map." This is the
  documented design rule ("never mutate the shared location table directly",
  `docs/EXTENDING.md:26-40`) — content must be added to the `Reworked()`/`Vanilla()` accessor objects,
  never the live table, or it leaks onto whichever variant happens to be installed at that moment.
- `AdoptSpawnLists` (`VariantRegistry.cs:74-96`) re-clones `Waves`/`BossLocationSpawn` from vanilla into
  the reworked object at install time if another mod (e.g. a spawn-rate mod) has since replaced vanilla's
  lists by reference — so third-party spawn mods still apply to the reworked variant.

**Which DB files, and what they feed:** `InterchangeRework.Server/Spt5LocationMod.cs` (full read, §above)
is an `IOnLoad` at `PostLoad + 100` that reads from `db/spt5/interchange/` — `base.json` (an **SPT 5.0
shaped** `LocationBase`, per the class comment at `Spt5LocationMod.cs:35-37`: "SPT 5.0's Interchange
database" is reused "since 5.0 targets the same map build as the bundle"), `provenance.json`
(`{tag, commit}`), `looseLoot.json`, `staticContainers.json`, `statics.json`, `staticLoot.json`,
`allExtracts.json`, `exit-names.json` — and merges/filters them into the cloned `Location` object
returned by `VariantRegistry.Reworked("interchange")`. Filtering exists because SPT 5.0's item table has
templates SPT 4.1's doesn't (`Spawnable()` check, `Spt5LocationMod.cs:199-200`, applied throughout loose-
and static-loot filtering, `:247-308`). `db/spawns.json` supplies the authored scene `Id -> CorePointId`
map (§5). `db/statics-groups.json` fills in static-container group membership 5.0's data omits
(`RegisterMissingGroups`, `Spt5LocationMod.cs:105-149`). Loose-loot density is scaled by
`config/interchange-rework.json`'s single `LooseLootDensity` field (`Spt5LocationMod.cs:26-27,77`).

**Route layer / per-raid selection:** `InterchangeRework.Server/Variants/VariantRoutes.cs` and
`VariantRaidHooks.cs` exist (not read line-by-line, but named and wired) to serve the
`/interchangerework/variant` (POST player's choice), `/interchangerework/hostvariant` (GET, for a
joining Fika client to ask what the host picked, `headless=1` query flag supported —
`InterchangeRework.Client/VariantSync.cs:14-16,182-209`), and
`/interchangerework/location` (GET, refetch the selected `Location` after the player answers the prompt,
since `/client/locations` was already cached before the prompt — `VariantSync.cs:59-125`) routes, and to
call `VariantRegistry.Install`/`RestoreVanilla` at the right point in the raid-creation lifecycle.

**Mod manifest / SPT version gate:** `InterchangeRework.Server/ModMetadata.cs:14` declares
`SptVersion = new("~4.1.3")` — "SPT itself refuses to load it outside that range" (`README.md:27-28`).

---

## 7. Lighting / culling / water / audio

- **Lighting:** no dedicated lighting-repair module found (no `Lightmap`/`LightProbe`/`Reflection
  Probe` repair class in the file listing). Not directly evidenced either way in this pass; likely the
  rip carries baked lightmap data through in the scene/mesh renderer data itself (out of scope for the
  files read) — flag as an open question rather than a confirmed finding.
- **Occlusion culling — two parallel systems, both explicitly EFT/retail-specific, not generic Unity:**
  1. **Perfect Culling** (`Koenigz.PerfectCulling.EFT` namespace — this is **EFT's own shipped
     occlusion-culling asset/plugin**, already present in the SPT 4.1 client, not something this mod
     brings in). `PerfectCullingRevival.cs:1-79`: the packed culling grid retail bakes
     (`StreamingAssets/Culling_Data`-equivalent) is redirected via a Harmony prefix on
     `PackedCullingGridData.GetPackedFilePathForGrid` (`:42-51,73-77`) to a file shipped in
     `plugin-data/culling/<hash>_packed_cull.bytes` (441 MB per `README.md:35`, confirmed on disk at
     `.../plugin-data/culling/3f8c141e1ca84602a218c54bf4429508_packed_cull.bytes`). The grid hash must
     match the bundle's `CullingGridHash` stamp or the feature silently stands down
     (`PerfectCullingRevival.cs:33-40`, stamped value `3f8c141e1ca84602a218c54bf4429508` at
     `bundles/bundle-stamp.json:16`).
  2. **Unity's own occlusion culling** is explicitly turned **off** for the reworked raid
     (`OcclusionCullingRepair.cs:7-8,45-52`) because "the rebuilt scenes carry no occlusion data, so with
     it on the camera culls against a visibility set that does not exist and drops enabled, in-view
     renderers — the player's own weapon included." This runs every frame via a `MonoBehaviour.
     LateUpdate` runner (`:43-53`) and only while `Plugin.DisableOcclusionCulling.Value` is true (default
     true, config comment at `Plugin.cs:74-77`).
  3. `CullingStrip.cs` (not read in full) is installed instead of `PerfectCullingRevival` on a headless
     (no camera, `Plugin.cs:119-122`) — presumably strips the culling components rather than reviving
     them.
- **Water:** `WaterRestore.cs:12-17` — retail's newer water component (`WaterForSSR` family) doesn't
  exist in SPT's client; the rebuilt Terrain scene's lake meshes/colliders carry through but with a null
  script. The mod adds SPT's own `WaterForSSRv3` component fresh at runtime and re-applies its authored
  settings from `plugin-data/water.json` (a sidecar with `settings` and `textures` sub-objects,
  `:45,58-59`), resolving textures **by name** against whatever the running game already has loaded, and
  falling back to PNGs shipped in `plugin-data/water/` when the game doesn't have that texture loaded
  (comment at `WaterRestore.cs:16-17`; files present: `Foam_01.png`, `circles.png`,
  `detailedWavesMap_hd2.png`, `Static_Voronoi_-_Tileable_-_NM2.png`, `index.json`).
- **Audio:** spatial/room audio is restored from `plugin-data/audio/spatial-audio.json` by
  `SpatialAudioRestore.cs`, matching by hierarchy path then nearest position
  (`SpatialAudioRestore.cs:15-16,19-21`) and reflectively re-creating/re-linking
  `SpatialAudioRoom`/`Audio.SpatialSystem.SpatialAudioPortal`/`MultiWindowPortal`/`AudioTriggerArea`/
  `SpatialAudioSystem` components (class-name table at `:24-31`) — i.e. **EFT's own spatial-audio
  component system**, not Wwise/FMOD banks. A separate binary,
  `plugin-data/audio/shopping_mall_sound.audiobakedata`, ships alongside (present on disk; format not
  examined — likely EFT's own baked-culling-per-room audio format, consumed by
  `AudioCullingRepair.cs`/`AudioCullingRepair`-named module, not read in full this pass). ~90 raw
  `.wav` clips ship in `plugin-data/audio/` as replacements for sounds `GameAssets.cs` couldn't find
  already loaded in the running game or in any loaded AssetBundle (`GameAssets.cs:9-11,28-73`, logged via
  `GameAssets.Report`, `:124-136`) — i.e. **audio clips the rip couldn't carry are shipped as loose
  files and injected by name**, exactly parallel to the water-texture fallback.

---

## 8. License

`LICENSE` at repo root, full text read: **MIT**, `Copyright (c) 2026 LennoxP90`
(`D:/sptmaps/_sources/SPT-InterchangeRework-main/LICENSE:1-3`). Confirmed again in
`InterchangeRework.Server/ModMetadata.cs:19` (`License = "MIT"`) and `README.md` ("## Licence — MIT.").
**Reusable as a template/reference without restriction** (attribution appreciated but not legally
required beyond preserving the notice if code is copied verbatim).

---

## What's directly reusable as a template for the ArenaMode Arena spike

The **client-side "repair swarm" architecture** is the most transferable pattern, independent of the
unpublished Unity/rip tooling:

- `InterchangeRework.Client/Plugin.cs:38-165` — the `Awake()` → `BindConfig()` → `PatchEachType()` (per-
  type `harmony.PatchAll`, one try/catch per type so one bad patch doesn't kill the rest,
  `:99-108`) → `InstallSystems()` (ordered `Safe(name, install)` calls, `:111-165,185-189`) skeleton is a
  clean, copyable shape for a bots-later Arena-map plugin.
- `InterchangeRework.Client/PresetSwap.cs` — the entire scene-preset-rewrite-by-reflection mechanism
  (`_scenesResourceKeys`, `path`/`rcid` fields) is EFT-version-specific reflection but the **pattern**
  (gate on `preset.ServerName`, load bundle, validate a stamp, rewrite entries, revert on failure) is
  exactly what an Arena-map-as-new-location mod would need, adapted to Arena's own preset/location id.
- `InterchangeRework.Shared/BundleStamp.cs` + `bundles/bundle-stamp.json` — a good, ready-to-copy pattern
  for guarding against a stale/mismatched bundle (version, byte count, exact scene-name set).
- `InterchangeRework.Client/NavMeshExtension.cs` — the `BotsController.Init` prefix +
  `NavMesh.RemoveAllNavMeshData()`/`AddNavMeshData()` pair is a direct, minimal template for getting a
  separately-baked `NavMeshData` bundle into a raid, independent of anything Interchange-specific.
  For a from-scratch new location this may not even be necessary if the location's own `ScenesPreset`
  can carry a `NavMeshSurface`/baked data natively — worth checking against how ManimalIcebreaker's
  playbook does it.
- `InterchangeRework.Client/MaterialShaderRebind.cs` + `ShaderPasses.cs` — a solid, general-purpose
  "find/Find/substitute" shader-rebind engine keyed off a JSON sidecar; directly reusable for Arena
  materials once Arena shader names are enumerated (project context already confirms all 17 Equator
  shader names exist in SPT's own shaders bundle, so the `Shader.Find` path alone may suffice for a
  first spike, without needing the `Substitutes` table).
- `InterchangeRework.Server/Variants/VariantRegistry.cs` — **not needed** for the Arena spike as scoped
  (a new standalone location, not a variant swap of an existing one), but the "clone the Location object,
  install by dictionary-key replacement" pattern is the right reference if the spike ever needs to offer
  a location choice.
- `docs/EXTENDING.md` — the single clearest piece of prose in the repo on the "why" of the
  clone-and-swap approach and its failure modes (`_encyclopedia` preset collisions, late item
  registration, orphaned trader ids) — worth reading in full before writing the ArenaMode server mod,
  even though its variant-specific advice doesn't apply to a brand-new location.

**Not reusable / out of scope:** the actual Unity-side rip→bundle pipeline (§1) is not in this repo at
all — for the spike's own Unity 2022.3.43f1 project at `D:/ArenaMapPort/UnityProject`, the
`ManimalIcebreaker-main/docs/MAP-BACKPORT-PLAYBOOK.md` (per project context, already identified as the
one *actual* pipeline document available to us) remains the primary reference for that half of the work,
not this repo.

---

## Open questions / not conclusively resolved this pass

- Lighting (baked lightmaps vs. realtime) — no repair module found under that name; likely carried
  through in scene data without needing a fix, but not directly evidenced.
- `LocationExportInfo` — no occurrence anywhere in the repo; either unused by this SPT 4.1 API surface or
  named differently. Needs checking against SPT 4.1's actual `SPTarkov.Server.Core` types if this
  matters for the spike.
- Exact `BuildAssetBundleOptions` and Editor version used for the two bundles — genuinely unpublished;
  cannot be determined from this repo. If needed, would have to be inferred from the bundle files' own
  binary headers (out of scope for this task's read-only source review).
- `AudioCullingRepair.cs` and the `.audiobakedata` binary format were not read/decoded in this pass.
