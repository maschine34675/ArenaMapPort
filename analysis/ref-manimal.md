# Reverse-read of Manimal's map mods (Icebreaker / Lighthouse / Labs) for a new standalone small map

Sources read: `D:/sptmaps/_sources/ManimalIcebreaker-main`, `D:/sptmaps/_sources/ManimalLighthouse-master`,
`D:/sptmaps/_sources/ManimalLabs-main`. All paths below are relative to `D:/sptmaps/_sources/` unless
given in full. Evidence is file path + line number(s); anything not directly quoted from code/docs is
marked **[inference]**.

Icebreaker is the closest analog to the ArenaMapPort SPIKE (small, genuinely new standalone location).
Lighthouse and Labs are NOT standalone-location backports — they patch/replace content on EXISTING SPT
locations. That distinction matters a lot and is called out throughout.

---

## 1. Standalone location registration on the SPT 4.1 server

**Icebreaker is the only one of the three that actually does this.** It shipped TWO generations:

- v1.0.0 rode the dormant `Suburbs` stub (per the playbook's Phase 0 guidance — see below).
- **v1.1.0 (current, 2026-09-12) is a genuinely independent location**, documented in
  `ManimalIcebreaker-main/docs/STANDALONE-LOCATION.md`. This supersedes the playbook's Phase-0 advice for
  SPT 4.1.5 and is the pattern to copy for ArenaMapPort.

### The registration mechanism (v1.1.0)

`icebreaker-server/IcebreakerLocationRegistration.cs:11-29`:
```csharp
public static void EnsureAvailable(LocationTable table) {
    var locations = table.GetDictionary();
    if (locations.ContainsKey(Key)) throw ...;
    foreach (var pair in locations)
        if (pair.Value?.Base is { } existing && (existing.IdField.ToString() == Id || IcebreakerLocation.Matches(existing.Id)))
            throw ...;
}
public static void Register(LocationTable table, Location location) {
    if (location.Base?.Id != Key || location.Base.IdField.ToString() != Id) throw ...;
    EnsureAvailable(table);
    table.GetDictionary().Add(Key, location);   // SPT 4.1.5: GetDictionary() returns persistent storage
}
```
`docs/STANDALONE-LOCATION.md:13-24` explains why this works in 4.1.5: `LocationTable.GetDictionary()`
returns the live, case-insensitive dictionary (not a copy); `GetLocation(name)` queries it first;
`LocationController.GenerateAll()` enumerates it and keys each base by `IdField` for `/client/locations`.
So `Dictionary.Add("icebreaker", newLocationObject)` during mod load is sufficient — **no location-registry
reflection patch needed in 4.1.5** (doc explicitly flags this must be rechecked per SPT version).

Caller: `icebreaker-server/IcebreakerMod.cs`, `[Injectable(TypePriority = OnLoadOrder.Preload + 90000)]`
class `IcebreakerMod : IOnLoad`, `OnLoadAsync` (`IcebreakerMod.cs:97-313`):
1. Deserialize `db/base.json` into `LocationBase` (`:100-107`).
2. `IcebreakerLocationRegistration.EnsureAvailable(locationTable)` then stamp `newBase.Id`/`IdField` from
   the shared identity constants (`:109-112`).
3. Set `botConfig.MaxBotCap[key]` explicitly — unlisted maps silently fall back to
   `MaxBotCap["default"] = 18` (`:114-127`, comment explains the symptom: late bot waves truncated).
4. `newBase.DisabledForScav = true` (`:130`) — same lever Labs uses to keep a map PMC-only.
5. Populate `StaticAmmo`, `AllExtracts = []` (v1 PMC-only), `StaticLoot`/`StaticContainers` as
   `LazyLoad<T>` wrapping the INNER model type (`:139-229`, see trap list item 3 below).
6. `LooseLoot` as `LazyLoad<LooseLoot>(factory, cacheValue: false)` — **must be `cacheValue:false`**
   because SPT's generator mutates spawnpoint templates in place during generation; a cached instance
   degrades pool variety across raids (`:184-204`, comment at `:186-191`).
7. Clone a real map's `ScavRaidTimeSettings` entry into `locationConfig.ScavRaidTimeSettings.Maps[key]`
   (`:232-235`); copy `StaticLootMultiplier`/`LooseLootMultiplier` from `"suburbs"` (`:236-239`);
   add the key to `locationConfig.NonMaps` (only affects seasonal-hostility rewriting per the comment,
   NOT `GetLocation`/map list) (`:240-243`).
8. Register locale keys via `localeTable.Global` `AddTransformer` for every language: `"<Id> Name"`,
   the raw key, `"<Id> Description"` (read by the map-select card, NOT `Base.Description` — see the
   `LocationInfoPanel` section below), and per-exit-name locale keys (exfil panel reads
   `Settings.Name.Localized()`, not the raw exit id) (`IcebreakerMod.cs:246-276`).
9. Wire loading-screen banners via `ImageRouter.AddRoute("/files/banners/<name>", fullPath)` for every
   entry in `newBase.Banners` — **nothing serves `/files/banners` on its own** (`:286-308`).
10. `IcebreakerLocationRegistration.Register(locationTable, icebreaker)` (`:310`).

### Required base.json fields / DB files (from the code above + `db/` contents)
`icebreaker-server/db/`: `base.json`, `staticContainers.json`, `staticLoot.json`, `looseLoot.json`,
`maplock.json`. `base.json.Id`/`IdField` get overwritten at load time from a shared
`Directory.Build.props`-generated identity (`STANDALONE-LOCATION.md:38`), so the on-disk file can start
from any donor's shape. **No `Transits` entries are populated** — `analysis/make_icebreaker_base.py`
never touches a transits field, and `AllExtracts = []`; this is a single-map, no-cross-map-transit setup,
which matches a first SPIKE map.

### Map-menu visibility / client requirements
`docs/STANDALONE-LOCATION.md:26-34`: `MatchMakerSelectionLocationScreen.ShowInternal()` builds
`LocationButton`s from `session.LocationSettings.locations.Values`; any enabled location gets a button
except the special paired Factory-night/Ground-Zero-high cases. `LocationButton` label is `"<_Id> Name"`;
map-screen icon position comes from `IconX/1000`, `IconY/1000` on the base. The scene/preset keys
(`Scene.path` in base.json) are a SEPARATE concern from registration — for a genuinely new map you still
need a scene bundle + preset (§2) but registering the location does not by itself require a new preset.

`docs/STANDALONE-LOCATION.md:390-431` (`LocationInfoPanel`, decompiled, not guessed): the map-select
card reads `"<_Id> Name"` / `"<_Id> Description"` locale keys (never `Base.Name`/`Base.Description`),
`AveragePlayTime + " MIN"` for the timer (NOT `EscapeTimeLimit`, which is the real raid length — set
both), `MinPlayers`-`MaxPlayers`, `Area + " KM2"`, and `location.Banners.First()` only — **a ripped
base.json ships retail's `Banners` array with MongoIds nothing serves; the server logs `[UNHANDLED]` and
the client shows an empty error dialog at the menu** unless you empty/replace it (LANDMINE, `:410-415`).
Difficulty label is computed from `AveragePlayerLevel - selfLevel`, not authored (`:417-431`).

Client-side hard map lock and per-profile unlock/visit tracking (`IcebreakerMod.cs:681-765`,
`IcebreakerVisitLedger`) are Icebreaker-specific quest-gating, not required for a bare SPIKE.

### Playbook's older guidance (superseded, keep as context only)
`docs/MAP-BACKPORT-PLAYBOOK.md:52-55` (Phase 0, dated to the original 4.0 backport): *"SPT's `Locations`
record is closed to new ids. Pick a dormant shipped stub (Icebreaker rides Suburbs) — every native lookup
... then resolves with zero patching."* **This is now factually superseded by the mod's own 1.1.0 work**
(`STANDALONE-LOCATION.md:11`: *"The older project comments saying the closed `Locations` record forces
reuse of Suburbs do not describe the available registration path in the installed 4.1.5
implementation."*). Do not follow Phase 0 as written for a 4.1.5 target; follow §1's mechanism instead.

---

## 2. Bundle hosting and ScenesPreset

Three different mechanisms across the three mods — pick per how invasive you want to be.

### Icebreaker: hijack `BundlesManager`'s in-memory dictionary (no StreamingAssets writes)
`icebreaker-client/IcebreakerBundleHost.cs` (current implementation; replaces an older
"materializer" that copied the bundle into `EscapeFromTarkov_Data/StreamingAssets` at startup — see
`:10-16` for why that was abandoned: it defeated the "install nothing into the game folder" packaging
rule in spirit even though the zip itself was clean).

Mechanism (`:18-34`, `:51-93`, `:146-182`):
- Every file under `BepInEx/plugins/<mod>/streamingassets/Windows/**` is scanned at Harmony-patch-init
  time and keyed by its lowercased, forward-slash relative path (`ScanPayload`, `:98-112`).
- Harmony **prefix** on `EFT.AssetsManager.BundlesManager.LoadBundleAsync` (`BeforeLoadBundle`,
  `:146-182`): if the requested bundle name matches one of ours, `AssetBundle.LoadFromFile` it (memory
  mapped — a 1.8 GB bundle costs a fraction of a copy) and insert a cache entry directly into the
  manager's private `_bundles` dictionary (found via `AccessTools.Field`, entry type is the manager's
  nested `AssetBundleReference`/`Class3523`, built via reflection with a ctor-then-field-write fallback,
  `:120-144`) **under every spelling the game might ask for** (case-sensitive dict with
  `StringComparer.OrdinalIgnoreCase`, because `LoadAssetAsync` lowercases while `LoadScene` doesn't,
  `:44-46`) — so the game's own lookup hits BEFORE it ever falls through to the URL/manifest fetch that
  otherwise 404s. `FindBundle` reads the same dictionary, so nothing else needs patching.
- The scene itself then loads **by name** via `SceneManager.LoadSceneAsync`, which works for any loaded
  bundle regardless of who loaded it — no `Windows.json` manifest entry needed (`:30-34`).
- `CleanLegacyStreamingAssets()` (`:189-224`) deletes any leftovers the OLD materializer wrote, matched
  strictly by relative path against the mod's own payload — install-hygiene for upgraders.

`analysis/patch_icebreaker_preset.py` (§ referenced by `STANDALONE-LOCATION.md:32`) authors the
`ScenesPreset` asset by **patching retail's own preset bundle**, not authoring one from scratch in the
SDK: loads retail's `maps/icebreaker.bundle`, finds the `MonoBehaviour` whose typetree name contains
`ScenesPreset`, filters `_scenesResourceKeys` down to the kept scene list (drops Sound/Culling/cutscene),
rewrites every kept entry's `path` to the single scene-bundle key and `rcid` to the scene name, trims
`ScenesGuids` to match, and re-saves the bundle (`analysis/patch_icebreaker_preset.py:9-51`). The
MonoBehaviour's script reference is left as BSG's own — it binds to `EFT.ScenesPreset` by class identity
in the SPT client (`:6-8`), so this is NOT a custom-authored ScriptableObject, it's retail's asset object
with its keys list rewritten. **[inference: for ArenaMapPort's target map, since retail Arena presets are
`.bundle` files too (per project context), the same "load retail preset bundle with UnityPy, filter/
rewrite `_scenesResourceKeys`" approach is directly applicable — same UnityPy technique, same object
type name to search for.]**

`icebreaker-server/bundles.json` (`:1-25`) only lists SMALL, mod-key-only bundles: `icebreaker_keypad*`,
`icebreaker_postit`, `torch_container` (props/UI, each with `dependencyKeys` into engine-global bundles
like `shaders`). **The scene + scenespreset bundles are NOT in `bundles.json`** — they load exclusively
through `IcebreakerBundleHost`'s dictionary hijack. This matches the playbook's landmine note
(`docs/MAP-BACKPORT-PLAYBOOK.md:160-166`, "THE BIG ONE": the client resolves bundle keys against
`StreamingAssets/Windows/` FIRST and only falls through to SPT's mod-bundle system for keys with no local
file) — but the CURRENT code sidesteps that landmine entirely rather than following the playbook's
"deploy scene+preset bundles into StreamingAssets" instruction. **The playbook is stale on this point
too; `IcebreakerBundleHost.cs` is the up-to-date mechanism.**

### Lighthouse: intercept the PRESET LOAD, not the bundle dictionary; content-manifest gated
Lighthouse doesn't add a new location — it **replaces every scene of the existing, native `Lighthouse`
location** at runtime. Two Harmony prefixes:
- `LoadPresetAsyncPatch` → `LighthouseSceneLoader.Prefix` on
  `LoadScenesFromPresetOperation.LoadPresetAsync` (`Patches/SceneLoader/LoadPresetAsyncPatch.cs:9-21`,
  `LighthouseSceneLoader.cs:23-38`): only engages when `preset.ServerName == "Lighthouse"` AND a
  `lighthouse-content.json` manifest file exists next to the plugin.
- `LoadScenePatch` → `LighthouseBundledScenes.BeforeLoad` on `AssetsManagerExtension.LoadScene`
  (`Patches/BundledScenes/LoadScenePatch.cs`, `LighthouseBundledScenes.cs:12-26`): for any scene name the
  loader owns, builds a custom `LoadSceneOperation` that calls `SceneManager.LoadSceneAsync` directly.

`LighthouseSceneLoader.LoadReplacement` (`LighthouseSceneLoader.cs:39-260`) is a full **content-pack**
pipeline, not a one-shot preset edit:
1. Deserialize + **validate** the manifest (`ManifestRules.Validate`, see below) — schema, target client
   build pin (`"0.16.9.40743"`), exact scene count (29 for Lighthouse), scene-path shape, mode enum.
2. `Mode` gates (`"development"|"probe"|"rework"|"test"`) with per-mode client toggles
   (`Plugin.AllowTest`/`Plugin.AllowProbe`), and `Ready` flag semantics enforced in `Validate` (dev/test
   content can never claim `Ready`).
3. **Client/server capability handshake**: `GET /manimal/lighthouse/capability` (own server route) with a
   15s timeout race against `_cancellationToken`; the response is checked against the manifest's own
   SHA-256 (`ManifestRules.CheckCapability`, `:227-236`) — refuses to load if server and client content
   don't match.
4. **Every bundle and sidecar file is SHA-256 verified** against the manifest before any load
   (`ManifestRules.VerifyFile`, `:215-225`; called from `LighthouseSceneLoader.cs:96-106`).
5. Bundles loaded via plain `AssetBundle.LoadFromFile` (no BundlesManager dictionary trick) and kept in a
   static `List<AssetBundle>` for later unload (`:121-129`).
6. `native._scenesResourceKeys` (the REAL Lighthouse preset asset, untouched on disk) is walked
   index-by-index; each entry's `path`/`onlyOffline` must match the manifest's recorded original exactly
   (`:158-173`, throws `"Lighthouse preset order/flags do not match the audited build."` otherwise); a
   **clone** of the native preset (`Object.Instantiate(native)`, `:194`) gets its `_scenesResourceKeys`
   replaced with the mapped replacement paths — the original asset is never mutated.
7. Optionally keeps native donor scenes (Scripts + `Lighthouse_Sound`) in the list when
   `manifest.UseNativeEnvironment` — i.e. reuse BSG's own weather/camera/ambient-audio scenes instead of
   shipping replacements (`:180-185`, `ContentManifest.cs:15-17`).
8. Actually loads via `Patches.SceneLoader.LoadPresetAsyncReversePatch.LoadOriginal(operation,
   _ownedPreset)` — a **reverse patch** that calls the ORIGINAL (un-prefixed) `LoadPresetAsync` body on
   the swapped-in preset clone (`:201`; see `LoadPresetAsyncReversePatch.cs`), so the native loader still
   does the real work, just against different scene keys.
9. On any failure at any step, `UnloadPartialReplacement()` unloads every scene it started loading
   (`:242-260`, `:261-289`) — this is a much more defensive load path than Icebreaker's.

`ContentManifest`/`ManifestRules` live in `lighthouse-shared/` (`ContentManifest.cs:1-25`,
`ManifestRules.cs:1-237`) — shared between client and (implicitly) whatever authoring tool emits
`lighthouse-content.json`; that authoring tool is **not in this repo** (see §3). Schema 2 adds
`NativeFiles`: bundles may borrow objects straight out of the PLAYER'S OWN installed
`sharedassets#.assets`/`resources.assets` files, verified in place and never shipped
(`ContentManifest.cs:22-24`, `ManifestRules.cs:113-125`) — a materially different asset strategy from
Icebreaker's "ship everything in one big bundle."

`lighthouse-server/bundles.json` exists too (small UI/prop bundles, same pattern as Icebreaker's) — the
big rendering/water/grass bundles are NOT in it, consistent with the client loading them straight from
`Plugin.Root` via the manifest.

### Labs (LabsBoiler): Harmony-prefix the INSTANCE preset-consumption method directly
Labs is the smallest-footprint mechanism of the three and is NOT a new/replaced location at all — it's a
**partial scene swap inside the existing, working `laboratory` location** (one scene, `level74` /
`Laboratory_Office_Above_Boiler_Room_floor_1`, gets substituted). Documented decision trail in
`docs/memory/labs-expansion-surgery.md:27-33` (own paraphrase, not literal quote — see file for exact
wording): native maps load scenes through `ScenesPreset._scenesResourceKeys` consumed by (in the
version they profiled) `GClass2287.method_1`; Harmony-prefixing that INSTANCE method (not the `smethod_1`
static one, which only handles child presets) when `preset.ServerName == "laboratory"` swaps one entry.
Distribution needs **no StreamingAssets writes and no `bundles.json` entry**: `BundlesManager.LoadBundleAsync`
404s silently for a built-in scene path (error stored, never thrown) and then `SceneManager.LoadSceneAsync`
finds the scene "in ANY AssetBundle in memory regardless of who loaded it" — so the plugin just
`AssetBundle.LoadFromFile`s its own bundle next to the DLL first, then lets the native call proceed
against the substituted preset entry. The bundled replacement scene needs a name DISTINCT from any
build-settings scene name, or `LoadSceneAsync` by name is ambiguous.

The Labs bundle is **not tracked in git** (100 MB+) and not present in the local checkout at all (`find
-iname "*.bundle"` in `ManimalLabs-main` returns nothing) — release packaging pulls it from the live SPT
install at build time (`docs/memory/labs-expansion-surgery.md`, packaging paragraph): `PackageFullRelease`
is gated behind `-p:FullRelease=true` and warns if the bundle is missing rather than failing the normal
build.

### `bundles.json` — the consistent rule across all three
In every repo, `bundles.json` in the server mod folder is used ONLY for small, mod-owned-key bundles
(UI prefabs, keypad/note/prop meshes) that legitimately have no local file for the client to find first.
The big scene/terrain/rendering bundles never go through it — each mod instead intercepts the loader at a
different layer (BundlesManager dictionary / preset-load prefix / instance-method prefix) specifically to
avoid `bundles.json`'s dependency-resolution path for multi-hundred-MB payloads.

---

## 3. Shader stand-in + runtime rebind; SDK/editor tooling

### Shader forensics method (shared doctrine, written up once in the Icebreaker playbook)
`docs/MAP-BACKPORT-PLAYBOOK.md:269-310` ("Shader stand-in forensics", Icebreaker's own hard-won section):
read the REAL BSG shader's property strings via UnityPy's `ShaderConverter` before writing any stand-in
math (property annotations document semantics even without DXBC disassembly); vertex-color blend drivers
are fragile against `Optimize Mesh Data` static-batch stripping; `uv_TexName` auto-declares `_TexName_ST`
in Surface Shaders but that declaration isn't user-referenceable; transparent/forward stand-ins flicker in
EFT's deferred renderer (prefer ambient-SH-lit alternatives for decals); verify texture alpha survived
the rip before trusting alpha-driven effects; vertex animation must displace along a UNIFORM direction
(never the per-vertex normal, or duplicated seam vertices tear apart). Point 7 (`:296-310`) is the
critical runtime-binding lesson: **the runtime name-rebind swaps materials onto whatever shader with that
NAME exists in ANY loaded location, including the client's own always-loaded
`EscapeFromTarkov_Data/sharedassets*.assets`** — four identical-looking "fix attempts" produced the same
bug because none of them ever actually rendered; the `[RebindShaders] Nx <name>` log line is the only
ground truth for what shader is live.

### Icebreaker's rebind implementation
`icebreaker-client/RaidFixPatches.cs`:
- `CaptureSceneMaterials(scene)` (`:1230-1242`) walks every renderer's `sharedMaterials` at scene-load
  time and records their `Material` instance IDs into a `HashSet<int> _ourMaterials` — the global rebind
  pass is fenced to only these, so vanilla-map materials are never touched.
- `RebindShadersToGame()` (referenced `:874-877`, driven by F5 and by two automatic passes at ~2s/~6s
  into the raid — `:766-776` — "bulk geometry loaded" then "catch streamed-in stragglers") walks captured
  materials and does `Shader.Find(name) ?? ShadersFinder.Find(name)`, swapping `material.shader` when a
  live game shader of the same name exists, EXCEPT names in a `RebindExclude` set ("bundle stand-in
  stays") (`:1255-1274`, `RebindShadersUnder` — the scoped variant used for anything the mod spawns into
  a VANILLA map, e.g. a hovercraft prop on Shoreline).
- A periodic retry (`_aliasRetryNeeded`, every 300 frames) re-walks only the still-pending list for
  bundle materials that load late (`:788-793`).

### Lighthouse's rebind implementation — more elaborate, with shipped fallback shaders
`lighthouse-client/LighthouseShaderRebind.cs`:
- `CaptureNativeShaders()` (`:61-89`) snapshots every currently-loaded `Shader` via
  `Resources.FindObjectsOfTypeAll<Shader>()`, PLUS a fixed name→Shader registry `ShadersFinder.SHADERS`,
  PLUS three hardcoded `Hidden/AreaLight`/`Hidden/Shadowmap`/`Hidden/BlurShadowmap` lookups
  (`AreaLight`-specific, used by `BeforeAreaInit` at `:204-236` to wire `AreaLight`'s three private
  shader fields directly by reflection-free field assignment, plus AddComponent a `MeshFilter`/
  `MeshRenderer` pair if missing).
- `RegisterRenderingBundle(bundle)` (`:15-49`) additionally loads exactly **9 "compiled shader donor"**
  assets from the bundle at paths `shaders/donor_*`, and only inserts each into the `NativeShaders` map
  if a native one under that name wasn't already found live (`:35-38`) — i.e. the bundle SHIPS real
  compiled shaders as a fallback for cases the live client might not have loaded yet (e.g. terrain/
  MicroSplat variants not used by any other currently-loaded map). Throws if the count isn't exactly 9
  (`:43-46`) — a hard content-integrity check, consistent with the manifest-verification philosophy.
- `Rebind(material)` (`:106-144`) preserves `renderQueue` and `shaderKeywords` across the swap (Icebreaker
  doesn't show that explicitly in the excerpt read, but does the same shader-name-only approach).
  `LighthouseMaterialRepairs.Repair` runs first for materials needing structural repair beyond a shader
  swap.
- `SceneLoaded(scene, mode)` (`:146-202`) is the actual entry point, hooked from Unity's
  `SceneManager.sceneLoaded` event (gated to `LighthouseSceneLoader.Owns(scene.name)`): restores
  MicroSplat terrain bindings, grass bindings, water bindings, then rebinds every renderer's
  `sharedMaterials` and every `AreaLight.SourceMaterial` in the scene.

Both mods converge on the same core trick — **author placeholder shaders with the REAL BSG shader's name
in the SDK build (so materials keep valid property layouts), then at runtime `Shader.Find`/name-table
lookup back onto the game's own compiled shader** — Lighthouse additionally ships a small set of verified
"donor" compiled shaders in the bundle as a safety net when the live client hasn't loaded a same-name
shader from anywhere else yet.

### SDK / editor project — NOT checked into any of the three repos
No `Editor/` folder, no `.unity` files, no Unity project exists in `ManimalIcebreaker-main`,
`ManimalLighthouse-master`, or `ManimalLabs-main` (`find -iname "*Editor*"` returns nothing in Icebreaker;
same for the other two). The playbook is explicit that the Unity SDK project is an EXTERNAL prerequisite:
`docs/MAP-BACKPORT-PLAYBOOK.md:14-16` ("give the assistant ... the locations of ... your Unity SDK
project"), `:32` ("The WTT map SDK Unity project (matching Unity editor version, e.g. 2022.3.43f2)"), and
throughout the analysis scripts, hardcoded local paths like `<reference-workspace>/WTT-SDK-2022
Public/Assets` (`analysis/generate_camera_stubs.py:26`, `perfect-culling-runtime/PerfectCullingRuntime.csproj:21`
`<PcScripts><reference-workspace>/WTT-SDK-2022 Public\Assets\Koenigz\Perfect Culling\Scripts`) and
`docs/memory/labs-expansion-surgery.md` ("built via the Icebreaker SDK pipeline", i.e. Labs reuses the
SAME external WTT-SDK-2022 project, not its own). The `IcebreakerTools/Editor/IcebreakerRetailDoors.cs`
canonical example cited by the playbook (`:135`) lives in that external project — **you will need your
own copy of a matching-Unity-version WTT/BigBrain-style map SDK project; none of these repos ships one.**
The user's project context already notes a Unity project is being created at
`D:/ArenaMapPort/UnityProject` — that is the correct role to fill; nothing here supersedes it.

What IS in-repo, and reusable as-is, is the **Python/UnityPy analysis-and-authoring layer** that runs
OUTSIDE Unity against raw level files and built bundles — `analysis/*.py` in Icebreaker (extraction,
generation, and bundle-patching scripts) is the single richest source of concrete UnityPy patterns:
`extract_scene_list.py` (BuildSettings scene enumeration — matches the technique already established in
project context), `patch_icebreaker_preset.py` (ScenesPreset rewrite), `extract_levelsettings.py` /
`extract_levelphysics.py` (typetree-drift-tolerant MonoBehaviour recovery via `TypeTreeGenerator` +
hand-edited flat trees), `analyze_static_batching.py` / `analyze_bundle_batching.py` (ground-truth static
batch inspection), `gen_static_containers.py` / `gen_static_loot.py` / `gen_loose_loot.py` (server DB
generation from bundle contents), `generate_camera_stubs.py` (typetree-exact C# stub generation for the
SDK, all-or-nothing per class — refuses rather than half-emits).

### Bundle build options
Not directly visible (the SDK project isn't in-repo), but inferable from consumer-side evidence:
- Icebreaker's scene bundle is a SINGLE bundle containing all kept scenes (`patch_icebreaker_preset.py`
  keeps 11 scenes under one `BUNDLE_KEY`, `:14-16,33`) — matches playbook Phase 5.1's explicit
  instruction ("Build ONE scene bundle containing all the map's scenes ... no cross-bundle dependencies").
- `PerfectCullingRuntime.csproj` comment (`:1-8`) states the bundle's culling data is baked by the
  EDITOR-compiled Perfect Culling assembly, then read back by a PLAYER build compiled WITHOUT
  `UNITY_EDITOR` — i.e. the SDK project bakes occlusion data with the normal Unity editor build of
  Perfect Culling; only the RUNTIME companion DLL is a separately-compiled, non-Unity .NET assembly
  (see §4).
- Lighthouse's manifest enforces the target Unity/client BUILD PAIR explicitly
  (`TargetClientBuild = "0.16.9.40743"`, `ManifestRules.cs:28-31`) and the SDK's Unity editor version is
  therefore whatever matches that client build — **[inference, not read directly]**: 2022.3.43f1/f2, same
  as Icebreaker's documented `2022.3.43f2` (project context already has 2022.3.43f1 for the SPT 4.1
  client, matching).

---

## 4. Static batching, lightmaps, LOD, occlusion culling (PerfectCullingRuntime.dll)

### Static batching — read as GROUND TRUTH from the built retail level file, never guessed
`analysis/analyze_static_batching.py:1-4` (comment): a built PLAYER scene strips `m_StaticEditorFlags`
(why an AssetRipper export shows everything as non-static) but KEEPS the batching RESULT on every
renderer via `m_StaticBatchInfo{firstSubMesh, subMeshCount}` — `subMeshCount > 0` means Unity's build-time
static batcher already combined that renderer. The script reads this directly off `MeshRenderer` objects
in the raw `levelNNN` file (`:47-60`) — no heuristics, no guessing. The playbook's performance section
records the outcome: **bundles ship ~91% pre-batched at build time**, and a RUNTIME static-batch pass was
tried and retired (only caught ~2.2k leftovers, "no measured win") — confirmed in code:
`icebreaker-client/RaidFixPatches.cs:1458-1459` ("(runtime StaticBatch retired 08-09: the bundle ships
91% pre-batched and the runtime pass only ever caught ~2.2k leftovers with no measured win)"). Labs
inherited the same conclusion implicitly by never adding a runtime batcher.

### Lightmaps — Labs found Labs has NONE; both mods treat baked lighting as generally lost on rip
`docs/memory/labs-expansion-surgery.md`: "Labs has ZERO baked lighting (user asserted, verified: 0
lightmaps, 0 probes, 0 lightmap indices in level74 + LIGHT scene both builds; all 746 lights are realtime
in the separate LIGHT scene)" — so for Labs the swapped scene needed NO lightmap handling at all; the
native realtime LIGHT scene relights it automatically. For Icebreaker, the playbook (`:79-80`) states
rip-lost lighting is the general rule ("Lighting data is lost → interior lamps serialize at intensity 0
→ fix via runtime lamp revival"), but the performance-playbook doctrine (`:333-339`) REINTERPRETS this:
"EFT Lights serialize intensity=0 BY DESIGN (verified by hexdump)... the authored intensity lives in the
wrapper: `CullingLightObject._maxLightIntensity` ... revival must source intensity from the wrapper data,"
not from the Light component. Icebreaker's lamps are therefore realtime deferred lights driven by BSG's
`CullingLightObject` fade system (distance fade window 25–80 m clamped to 25 m shipped default,
`docs/MAP-BACKPORT-PLAYBOOK.md:333-339`), not baked lightmaps. **[inference for ArenaMapPort]**: expect
Arena maps to be in the same boat — realtime lighting driven by wrapper components, not `.lightmap`
assets; verify per-map with the same lightmap-count check Labs used before assuming revival work is
needed.

### LOD
`docs/MAP-BACKPORT-PLAYBOOK.md:324-332` (performance playbook, "LOD bias is THE lever on ripped maps"):
EFT's Object-LOD quality slider hard-clamps to Unity `lodBias` `[2,4]` (verified 1:1 against
`GraphicsSettingsClass`'s validator); BSG's `LODGroup` CULL thresholds assume bias ≥ 2, and MOST map
LODGroups are single-LOD (Icebreaker: 79.8k of 81.8k groups; Customs: 50k of 67k, cited as a cross-check)
— i.e. LODGroups on these maps function as a CULL system, not a detail-swap system. Icebreaker's fix,
`icebreaker-client/IcebreakerLodCullFloor.cs`, runs a **cell-tiered cull floor**: a protective floor near
the camera and an aggressive floor beyond it, split indoor/outdoor via `EnvironmentManager`, applied over
a sliced coroutine walk of every `LODGroup` (`RaidFixPatches.cs:1462`: `StartCoroutine(
IcebreakerLodCullFloor.Apply())`; `IcebreakerLodCullFloor.cs:34,59-75` builds a cell index over
`FindObjectsOfType<LODGroup>()`). Shipped defaults: bias 0.7, far floor 0.1, near floor 0.006, radii
27.1 m / 19.49 m (`docs/MAP-BACKPORT-PLAYBOOK.md:331-332`). The doc explicitly warns: **verify a lever
actually ENGAGES before trusting an A/B** — three knobs (LODGroup.enabled toggling, an early
LightCullDistance, CellCull) were inert on first test and a "no effect" measurement on a dead knob proved
nothing (`:348-351`).

### Occlusion culling — retail's own bake was EXONERATED; the runtime is the real cost
`docs/MAP-BACKPORT-PLAYBOOK.md:340-347` explicitly lists occlusion culling as **exonerated, do not
re-litigate**: "retail's own 84k-cell bake made ZERO difference — dense views are open sightlines." The
actual PerfectCulling investment is about making the SYSTEM run at all on a ripped/backported map, not
about it being the fps lever:
- `perfect-culling-runtime/PerfectCullingRuntime.csproj` compiles the Koenigz Perfect Culling SOURCE
  (`$(PcScripts)` pointed at the external SDK's `Koenigz\Perfect Culling\Scripts`) as a STANDALONE .NET
  `net471` assembly named exactly `PerfectCullingRuntime` (must match for bundle binding by name,
  `:1-9`), referencing the SPT client's own `UnityEngine*.dll`s directly (`:24-30`) — explicitly WITHOUT
  `UNITY_EDITOR` defines, because the editor-compiled version has editor-only code that NREs in the
  player AND a different serialized class layout than what the bundle's stripped-typetree data expects
  (`:1-8`). It also re-adds the Unity-version `#define`s the editor build had (`UNITY_2022_3_OR_NEWER`
  etc., `:16-19`) — without them, version-gated code inside Perfect Culling falls back to an ancient path
  (`ToggleRenderer` using `renderer.enabled` instead of `forceRenderingOff`). Post-build target copies the
  DLL straight into `BepInEx/plugins/<mod>/PerfectCullingRuntime.dll` (`:32-34`).
- `icebreaker-client/RaidFixPatches.cs` self-hosts a Perfect Culling volume system from a packed retail
  bake (`.pcbake` files under `plugin-data/culling/`) with runtime rehydration of renderer references by
  path (`:1668,1688-1751,1872-1917` — "the game's built-in PC is BSG's fork missing the vanilla volume
  class, so we self-host"), plus a hard per-frame apply BUDGET (`:` referenced generally; the playbook's
  own trap list states 350 toggles/frame was smooth where 1500 froze, `:253-254`).
- Labs took the OPPOSITE, much cheaper approach for its swapped scene: `labs-client/
  LabsBoilerCullingStrip.cs` DESTROYS every `Koenigz.PerfectCulling*` component on the swapped scene at
  `sceneLoaded` (before `Start` runs) rather than trying to make PerfectCulling work on new,
  non-map-wide-baked content — "office renderers just always-on" (`docs/memory/labs-expansion-surgery.md`,
  ROUND 17 entry). This is a legitimate, much simpler fallback for a small new area: skip occlusion
  entirely on the new geometry and eat the (small, localized) draw-call cost.

**[inference for ArenaMapPort]**: for a SPIKE (geometry + colliders + NavMesh first, no bots/perf work
yet), Labs' "just strip PerfectCulling, run everything unculled" posture is the right scope match — the
full PerfectCullingRuntime.dll recompilation is real, working, but substantial standing infrastructure
that only earns its cost once the map is otherwise playable and dense enough to need it.

---

## 5. Minimal scene contents for a raid to start without an NRE

This is best evidenced by the concrete list of things Icebreaker/Labs had to ADD or REPAIR at runtime
because the ripped/rebuilt scenes did not carry them — i.e. this IS the "what EFT needs" list, reverse
engineered from what breaks when it's absent.

### `LocationScene` — its arrays are NOT reliably populated by an SDK rebake; two different repair strategies
The playbook flags this as a MUST-DO authoring step (`docs/MAP-BACKPORT-PLAYBOOK.md:150-151`: "Wire the
`LocationScene` component's arrays (LootableContainers, WorldInteractiveObjects, ...) — the game
enumerates scene content through them") and as a trap (`:249-250`: "`LocationScene.GetAllObjects<T>`
returns EMPTY (not an error) for types missing from its hardcoded cache list"). In practice, NEITHER
shipped mod fully solved this in the SDK — both patch it at runtime instead:

- **Labs** rebuilds `LocationScene` directly and CORRECTLY, in-place, via reflection
  (`labs-client/LabsBoilerLocationSceneFix.cs:13-77`, hooked from `OnSceneLoaded` for the swapped scene
  by name): `GetComponentsInChildren<WorldInteractiveObject>`/`<LootableContainer>` over the scene roots,
  assign deterministic `Id`/`NetId` to any component that rip left blank (so host/client Fika tables
  agree, `:29-36`), write the arrays onto the (first) `LocationScene` component AND into its private
  `_typeToContainer` dictionary via `AccessTools.Field` (`:38-57`), then walk EVERY array-typed field on
  `LocationScene` (reflectively, not just the two named ones) and every dictionary value, compacting out
  null/destroyed slots (`Compact`, `:87-105`) — because rip-damaged registry arrays have literal null
  entries that NRE unguarded iterators (mod compat) even where a value exists.
- **Icebreaker** does NOT wire `LocationScene`'s arrays at all; instead it works around the consequence
  for doors specifically. `RaidFixPatches.cs:1466-1506` (`HealDoorRegistry`): comment states plainly
  *"on this backported map the world build misses the doors entirely (built from LocationScene
  registration our ripped scenes never perform)"* — so at raid init it does
  `FindObjectsOfType<WorldInteractiveObject>()`, sorts deterministically (id, then position, so every
  machine picks the same object on an id collision), and calls `World.RegisterWorldInteractionObject`
  directly on every object missing from `World`'s private `_interactiveObjectsDictionary` (found via
  `AccessTools.Field`). This fixes Fika door/keycard sync (which resolves purely through that
  dictionary) but does NOT fix anything that specifically reads `LocationScene.LootableContainers` etc. —
  **Labs' approach is the more complete fix and directly reusable.**

### `LevelSettings` / `LevelPhysicsSettings` — required singletons, both recoverable only from retail data
`analysis/extract_levelsettings.py:1-6` (comment): the Scripts scene ships a
`'---Icebreaker_levelsettings---'` HUSK after an AssetRipper strip — "the missing singleton that NREs
`NightVision.OnPreCull`." The script recovers real serialized values (`AmbientMode`, `AmbientIntensity`,
night-vision ambient fields) from the raw retail level files via `TypeTreeGenerator` + a hand-flattened
typetree, keyed off a `LevelSettings`-named `MonoBehaviour` search across every GameObject in the Scripts
scene, and also inventories EVERY OTHER MonoBehaviour on Scripts-scene roots as a side effect
(`:90,107-118`) — worth reusing as a general "what components does the donor Scripts scene carry"
census tool.

`analysis/extract_levelphysics.py:1-9` (comment): `LevelPhysicsSettings.Awake` calls
`Physics.RebuildBroadphaseRegions(bounds, subdivisions)` (PhysX Multibox Pruning grid setup) — **missing
this is invisible on a normal raid start but FATAL on a map TRANSIT**: the previous map's physics regions
survive, every collider on the new map lands in the single catch-all overflow region, and that region
caps at 64K colliders. The script additionally captures the HOST GameObject's own world position
(`:96-104`, "the runtime does `transform.TransformPoint(_mbpBounds.center)`, so the host's own position
has to travel with the values or the grid lands in the wrong place"). **This is a concrete "you won't see
it until you test transits" trap** — directly relevant since ArenaMapPort is explicitly scoped to skip
transits for now, but should be remembered before ever adding one.

### `EnvironmentManager` and `ObservedCullingManager` — created at runtime if the singleton never spawns
`RaidFixPatches.cs:221-267` (`Patch_EnsureEnvironmentManager`, prefix on `LocalPlayer.Create`, also
re-anchored onto `FikaPlayer.Create` — comment: *"fika's player never goes through `LocalPlayer.Create`,
so this prefix alone left coop raids with no EnvironmentManager — `Player.Init` NRE'd on it and loading
froze at 25%"*):
- `Player.Init` dereferences `EnvironmentManager.Instance`, which is null when the ripped scene's
  EnvironmentManager MonoBehaviour was stripped to a missing-script husk and removed. Fallback chain:
  build the FULL retail indoor-trigger hierarchy if available (`IcebreakerAcoustics.TryBuildEnvironmentTriggers()`),
  else `new GameObject(...).AddComponent<EnvironmentManager>()` bare (always-Outdoor fallback, `:258-263`).
- `ObservedCullingManager` (drives observed-body visibility for bots/remote players) is similarly
  created if missing, and specifically BEFORE any player body builds on every peer — an earlier version
  created it only in `BotsController.Init`, which only runs on the raid HOST, so Fika CLIENTS got
  invisible-but-geared "ghost" observed bodies until this was fixed (`:244-256`).

### `SpawnPointMarkers` / spawn point identity — server params and scene markers must share GUIDs
`analysis/make_icebreaker_base.py:11-36` (`extract_spawns`, comment): *"the server `SpawnPointParams` is
the source of truth: EFT DELETES scene markers not in this list and CREATES markers for entries not in
the scene, so we regenerate params from the authored positions"* — and critically, *"the marker's
authored `SpawnPoint.Id` — server params MUST reuse it, or the merge deletes the scene marker and the
`BotZone` list dangles on dead objects"* (`:24-25`). This means the base.json `SpawnPointParams` GUIDs and
the in-scene `SpawnPointMarker.SpawnPoint.Id` values must match exactly, or the client-side merge silently
destroys and recreates markers each load, breaking anything (BotZone patrol wiring, forced spawns) that
held a reference to the original object. Separately, `RaidFixPatches.cs:205-219`
(`Patch_SpawnPmcScan`) swallows an NRE in `SpawnPointsCollection.CalculateCanSpawnPmcBots` caused by ANY
marker in the categorized list having a null `SpawnPoint` — comment: *"HasPmcBotSpawns only matters for
bot-PMC spawning ... our player spawns are zone-less Player points, so swallow it"* — i.e. for a
geometry/NavMesh-first SPIKE with no bot spawns authored yet, this specific NRE is safe to blanket-swallow
rather than fix.

### `BotZone` / patrol data — required for bots to do anything but stand still, not for raid start itself
`icebreaker-client/IcebreakerAIBake.cs:121-184`: AssetRipper stripped every `PatrolPoint`/`PatrolWay` in
the AI scene, leaving `BotZone`'s serialized `PatrolWays` references dangling — "zones had zero ways, so
a spawned bot had nowhere to walk ... held its spawn heading forever." Recovery re-adds the components
(`AddComponent` if missing, `:143,150`), fills their fields from a JSON bake (`icebreaker_aibake.json`),
then `SnapPatrolPointsToNavMesh` (`:169`, comment `:159-166`: authored positions target RETAIL's navmesh;
a backport's own separate bake can leave points just off-mesh, and `PatrollingData` DISCARDS the
`NavMeshPathStatus` from `GoToPoint` and reports `"go"` regardless — a bot sent to an unreachable point
silently stands still forever, observed as "patrol=go with moving=False in 96% of samples"). `BotZone`
itself is noted as having DRIFTED its serialized layout between versions, so way-membership was recovered
by raw reference-scan rather than typetree parse (`:129-130`). `AIVoxelesData` (cover voxels) is rebuilt
the same fill-from-JSON way (`:186-`). **None of this blocks raid START** — it only matters once
bots/AI are in scope, which the ArenaMapPort SPIKE explicitly defers.

### Camera — do NOT ship one in the bundle; graft components onto the game's own fallback camera
`icebreaker-client/IcebreakerCameraDonor.cs:13-35` (comment, extensive): the bundle-authored-camera route
was tried TWICE and killed both times — bundle binding needs exact compiled layouts (stripped typetrees)
and the rip's serialized asset refs (shaders/materials/curves) are dead on arrival, crashing
`Awake`/`Start` and specifically breaking `CameraManager.SetSSR` inside `PlayerCameraController.Create`
(no spawn at all). The working replacement: let the game's own fallback chassis camera (`Cam2`) stand,
and at runtime ADD-ONLY graft specific extra components from a JSON dump of a REAL vanilla map's live
camera (`camera/camera_donor.json`, produced once via a dev-mode dumper on any vanilla raid,
`:110-178`), resolving every asset reference BY NAME against the CURRENT map's already-loaded assets
(`:417-473`). Grafting is add-only (never overwrites the chassis's own components — overwriting produced
a black screen with zero exceptions, `:241-246`), happens with the GameObject INACTIVE so `Awake` doesn't
fire on half-filled fields (`:256-263`), and only a hand-verified ALLOWLIST of components actually grafts
(`DesaturateEffect`, `Antialiasing`, `Tonemapping`, `PerfectCullingCamera` — `:85-88`); everything else is
either self-withheld when its asset refs don't resolve, or permanently poisoned (documented per-effect,
`:41-108`) because a component with null shader/material refs blits nothing SILENTLY — no exception, just
a black screen. `analysis/generate_camera_stubs.py` exists for the OPPOSITE, abandoned approach (typetree-
exact stub generation for SDK authoring) and documents WHY that path was hard (stripped player typetrees
require byte-exact field order/type, `:1-11`) — kept as the "if you must bundle a camera" reference, but
the shipped solution avoids it entirely.

### Exfils
Not deeply covered by the files read; base.json's `exits` array is populated from a donor map's exit
template with the name/type/position overwritten (`analysis/make_icebreaker_base.py:114-132`), one exit
for the v1 PMC-only, single-extraction-method map. `docs/MAP-BACKPORT-PLAYBOOK.md:180` warns the game
LOWERCASES the player's entry point before matching, so `EligibleEntryPoints` in an exit's data must be
lowercase or the exit never becomes eligible.

---

## 6. Licenses

| Repo | License file | Terms |
| --- | --- | --- |
| `ManimalIcebreaker-main` | `LICENSE` (root) | MIT, "Copyright (c) 2026 danauraborealis" — full text present, standard MIT permissions (use/copy/modify/merge/publish/distribute/sublicense/sell), no-warranty disclaimer. |
| `ManimalLabs-main` | `LICENSE` (root) | MIT, same copyright holder/text as Icebreaker. |
| `ManimalLighthouse-master` | **none found** | No `LICENSE`, `LICENSE.md`/`.txt`, or `README` anywhere in the repo (`find -iname LICENSE*` and `find` for any README both return nothing). Also confirmed via `docs/memory/labs-expansion-surgery.md`'s aside about the SEPARATE ManimalLabs repo being "git repo, user init'd, MIT license" — implying Lighthouse's licensing status was never set the same way. **Default copyright applies: no license = no permission granted to use/copy/modify/redistribute, notwithstanding that its sibling repos are MIT.** Do not treat any Lighthouse code as reusable without asking the user (who is also the likely rights-holder, but the repo itself carries no grant). |
| `SPT-InterchangeRework-main` (read opportunistically, not part of the core ask) | `LICENSE` (root) | MIT, "Copyright (c) 2026 LennoxP90" — different author than the Manimal repos, still MIT. |

**Practical implication for ArenaMapPort**: Icebreaker's and Labs' C#/Python source (server registration
pattern, `IcebreakerBundleHost.cs`, the entire `analysis/*.py` toolkit, `LabsBoilerLocationSceneFix.cs`,
etc.) can be copied/adapted under MIT (attribution + license notice retained per the MIT text).
Lighthouse's code (the manifest system, shader rebind, terrain/grass/water bindings) has **no license
grant on record** — reuse only with the user's explicit go-ahead, since he is likely also the Lighthouse
author but the repo itself doesn't say so.

---

## Key differences: Icebreaker vs. Lighthouse vs. Labs

| Axis | Icebreaker | Lighthouse | Labs (LabsBoiler) |
| --- | --- | --- | --- |
| What kind of map | NEW standalone location, independently registered (v1.1.0) | REPLACES all scenes of the EXISTING native `Lighthouse` location | Swaps ONE scene inside the EXISTING native `laboratory` location |
| Registration | `LocationTable.GetDictionary().Add(key, location)` — see §1 | None needed — location already exists in SPT; only its scene content is swapped | None needed — same |
| Bundle-load hook point | `BundlesManager.LoadBundleAsync` prefix, dictionary hijack (§2) | `LoadScenesFromPresetOperation.LoadPresetAsync` prefix + `AssetsManagerExtension.LoadScene` prefix, swaps a CLONED `ScenesPreset` (§2) | `GClass2287.method_1(ScenesPreset)` instance-method prefix, mutates one entry in the SAME preset object in place (§2) |
| Content integrity / versioning | None beyond MSBuild-generated identity constants | Formal `ContentManifest` + `ManifestRules`: schema version, exact scene-count check, SHA-256 per file, client/server capability handshake over HTTP (§2) | None — ad hoc, bundle pulled from the live install at package time, not even repo-tracked |
| Asset sourcing | Everything self-contained in one big authored bundle | Schema-2 manifests can borrow objects straight from the player's OWN installed `sharedassets#.assets`/`resources.assets`, verified in place (§2) | Reuses the native LIGHT scene wholesale (no lighting authoring needed at all) |
| Shader stand-in | Name-lookup rebind only (`Shader.Find`/`ShadersFinder.Find`), scene-load material capture fence (§3) | Name-lookup rebind PLUS 9 shipped "donor" compiled shaders as a verified fallback (§3) | Not covered in files read — likely reuses native Labs shaders directly since it's swapping into an existing, still-native-shaded map |
| Occlusion culling | Full self-hosted `PerfectCullingRuntime.dll` (separately compiled, non-editor .NET assembly) + packed `.pcbake` rehydration (§4) | Not covered in files read | Strips `Koenigz.PerfectCulling*` components outright on the new content — no occlusion attempted (§4) |
| `LocationScene` repair | Left unwired; only the door/interactive registry is patched around at runtime via `World`'s private dictionary (§5) | Not covered (native Lighthouse likely already has this wired since only scenes/materials are swapped, not authored from scratch) | Directly rebuilt in-place via reflection, including generic array-field/`_typeToContainer` compaction — the more complete, more reusable pattern (§5) |
| Repo size / footprint | Largest by far: full client plugin (~50 files), full analysis toolkit (~50 scripts), server mod, Fika addon, verification harness | Mid-sized: focused client patch set + shared manifest library + server mod | Smallest: ~8 client files, ~3 server files, no analysis scripts of its own (reused Icebreaker's) |
| License | MIT | **None on record** | MIT |

**For a genuinely new small Arena map (the ArenaMapPort SPIKE), Icebreaker is the load-bearing reference**
— it's the only one of the three actually solving "new standalone location + new bundle + new preset from
nothing," and it happens to also carry the only complete, generically-applicable UnityPy extraction/
authoring toolkit. Labs is the best secondary reference specifically for the `LocationScene` repair
pattern and for its more defensive/minimal occlusion-culling posture (skip it, don't rebuild it, for a
first pass). Lighthouse's manifest/capability-handshake architecture is over-engineered for a SPIKE but is
the right model to study later if ArenaMapPort ever needs versioned, integrity-checked content packs
across multiple maps or public distribution.
