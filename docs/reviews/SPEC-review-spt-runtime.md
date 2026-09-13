# SPEC runtime-correctness review — SPT 4.1 / EFT 0.16.9.5 client + server load chain

Reviewer lens: adversarial, SPT 4.1 / EFT 0.16.9.5 runtime correctness. Every claim below was checked
directly against `D:/SPT41/Development/_decompiled` (client) and
`D:/ArenaMapPort/analysis/decompiled-server-415/` + `D:/ArenaMapPort/analysis/decompiled-server/` (server),
not re-derived from `spt-location-contract.md`/`critic.md`/`ref-manimal.md` without independent verification.
`docs/SPEC.md` was **not** edited. Citations are `file:line` against the exact files read this pass.

Scope covered: Harmony targets/field names (§F), the `BundlesManager`/`AssetsManager` bundle-and-scene
resolution chain, the preset-bundle patch (§B.WP3/§E.3), the five required EFT components (§D), timing of
`LevelSettings`/`LocationScene`/`BotsController`/`SpawnPointsCollection` relative to `LoadMapAndData`,
`PerfectCullingCrossSceneSampler`, and the server-side registration mechanism (§B.WP5, `critic.md` §1).

---

## Verdict

**Needs changes before implementation.** One confirmed **blocker**: the SPEC's own §D authoring table tells
WP2 to leave `BotZone.SpawnPointMarkers` `null`, and directly asserts this is "verified null-safe" — it is
not. Two separate, unconditional call sites in the decompiled client dereference that exact field without a
null check, on every raid start, before the player ever spawns. This would abort raid start for the spike's
literal success criterion ("the player can walk") regardless of how correct everything else in the pipeline
is. Everything else checked (BundlesManager hook, NavMesh timing, LevelSettings/LocationScene NRE table,
server-side registration mechanism, base.json loot shape) verified sound on independent re-read, with one
evidence-integrity issue in §B.WP3's preset-patch field list and one overstated risk (PerfectCulling) that
should be down-weighted in the risk register.

---

## Finding 1 — BLOCKER: leaving `BotZone.SpawnPointMarkers` null (as §D instructs) crashes raid start, twice, before `BotsController.Init` even matters

**SPEC's claim** (`docs/SPEC.md` §D, `BotZone` row): *"`SpawnPointMarkers` may be left `null` — **verified
null-safe** this pass: `BotZone.cs:149` is `if (SpawnPointMarkers != null && SpawnPointMarkers.Count > 0)`."*
And in the `SpawnPointMarker` row: *"`BotZone` linkage is not required for the player-spawn success
criterion... player spawn selection is global, not BotZone-scoped."*

**What SPEC verified**: only that `BotZone.Init()` itself (the method the SPEC's own citation points at)
guards the field. That citation is accurate as far as it goes — `BotZone.cs:149` really does guard it inside
`Init()`. But `BotZone.Init()` is not the only place in the load chain that reads `BotZone.SpawnPointMarkers`,
and the other two read sites are **not** guarded, and both run on **every** local raid start, before bots or
`BotsController` are even reached:

**1a. `EFT.Game.Spawning/SpawnPointsCollection.cs:264`, inside `CalculateCanSpawnPmcBots`:**
```csharp
public static void CalculateCanSpawnPmcBots(BotZone[] zones)
{
    foreach (BotZone obj in zones)
    {
        obj.HasPmcBotSpawns = obj.SpawnPointMarkers.Any((SpawnPointMarker marker) => marker.SpawnPoint.Categories.ContainBotPmcCategory());
    }
}
```
Called unconditionally from `SpawnPointsCollection.CreateFromScene` (`SpawnPointsCollection.cs:168`), **after**
the `if (parameters != null) { ... }` reconciliation block — i.e. it runs regardless of whether
`base.json`'s `SpawnPointParams` is `null` (SPEC's own plan, §E.4/`critic.md` §6) or populated. `zones` here
is `FindObjectsProxy.FindUnityObjectsOfType<BotZone>()` (`SpawnPointsCollection.cs:156`) — with the spike's
one authored `BotZone`, `zones.Length == 1`, so this loop runs and dereferences `obj.SpawnPointMarkers.Any(...)`
on the very `BotZone` §D tells WP2 to leave with `SpawnPointMarkers == null`. `List<T>.Any()` on a null
`List<T>` reference throws `NullReferenceException` immediately (it's an extension method invoked on a null
receiver via `null.Any(...)`, which is a direct NRE the moment the extension method's body touches the
instance — `List<T>` has no null-tolerant LINQ path).

`CreateFromScene` itself is called from `BaseLocalGame.Run:526`
(`SpawnPointsCollection spawnPoints = SpawnPointsCollection.CreateFromScene(DateTimeExtensions.LocalDateTimeFromUnixTime(location.UnixDateTime), location.SpawnPointParams);`)
— confirmed exact line via `spt-location-contract.md` §1 step 6 and independently re-read this pass
(`SpawnPointsCollection.cs:151-169` for the method body). This is **step 6** of the raid-start sequence,
**before** step 7 (`LocalGame.vmethod_1` → `BotsController.Init`).

**1b. `EFT/BotsController.cs:221-229`, inside `BotsController.Init` (called from `LocalGame.vmethod_1:184`,
step 7):**
```csharp
List<BotZone> list = new List<BotZone>();
BotZone[] array = botZones;
foreach (BotZone botZone in array)
{
    if (botZone.SpawnPointMarkers.Count != 0)   // <- line 225, unguarded
    {
        list.Add(botZone);
    }
}
```
Same field, same null-dereference pattern, a second independent crash site — reached only if 1a somehow
didn't fire first (it will).

**Root cause of the SPEC's mistake**: `LocationScene.FillArrays()`/`FillArraysFrom()` (§D's mechanism for
"every array field ends up non-null for free") populates **`LocationScene`'s own** `SpawnPointMarkers`
field (`LocationScene.cs:441`, `SpawnPointMarkers = CG_FillArraysFrom<SpawnPointMarker>(...)`) — a
**different field on a different component**. `BotZone.SpawnPointMarkers` (`BotZone.cs:43`,
`public List<SpawnPointMarker> SpawnPointMarkers;`) is never touched by `FillArrays()` at all. SPEC's §D
table conflates "LocationScene's array is auto-filled" with "so BotZone's doesn't need attention," which
is the actual gap.

**Corroborating evidence this field is meant to be non-null**: BSG's own runtime code assumes it —
`SpawnPointMarker.Create(SpawnPointParams, GameObject)` (`SpawnPointMarker.cs:349-378`) does
`botZone.SpawnPointMarkers.Add(spawnPointMarker);` unconditionally once a matching `BotZone` is found
(`:373`), with no null-init anywhere in that call path either — every authored/shipped BSG scene apparently
always has this field pre-populated (empty `List`, not `null`) by the level SDK's own AI-bake step, which
this project explicitly does not run (`critic.md` §2c/§2d).

**Fix**: `AuthorScriptsScene.cs` (§B.WP2.6) must explicitly set `BotZone.SpawnPointMarkers = new
List<SpawnPointMarker>()` (empty is fine — bots are out of scope, decision #8, so `_canSpawn` ending up
`false` via `BotsController.cs:230-233`'s `list.Count <= 0` check is the correct/expected outcome) when
authoring the one required `BotZone`. Same one-line-fix category as `PatrolWays = new PatrolWay[0]`, which
§D already gets right for the same class — this is the one sibling field it misses. Recommend also adding
this exact case to WP6's `validate_spawn_authoring.py` (§B.WP6 table) as a non-null assertion, since it is
the single highest-severity, easiest-to-miss authoring gap found in this whole pass.

**Severity**: blocker. Without this fix, `docs/SPEC.md`'s own §G.S4 step 3 checklist would hit an unhandled
`NullReferenceException` inside `BaseLocalGame.Run` before any of the log lines it tells the user to watch
for even have a chance to print, and the spike's core "player can walk" criterion is unreachable.

---

## Finding 2 — MAJOR: §B.WP3's "confirmed" donor field list cites a `GameModes` field that does not exist on `EFT.ScenesPreset`

**SPEC's claim** (`docs/SPEC.md` §B.WP3 intro, lines ~413-424): *"Confirmed live on this exact install via
direct UnityPy read this pass: one MonoBehaviour named `develop.ScenesPreset`... `ChildPresets:[]`,
`GameModes:null`, `_scenesResourceKeys`: 3 entries..."* — and step 6 instructs: *"`tt['ChildPresets'] = []`,
`tt['GameModes'] = []`."*

**Verification**: read the full, current `EFT.ScenesPreset` class
(`D:/SPT41/Development/_decompiled/EFT/ScenesPreset.cs:10-99`, entire file). Its complete field list is:
`ActiveSceneGuid` (nested `Guid` type), `ServerName`, `ScenesGuids` (`List<Guid>`), `_disableServerScenes`,
`ChildPresets` (`ScenesPreset[]`), `_activeSceneName`, `_scenesResourceKeys` (`List<SceneResourceKey>`).
**There is no `GameModes` member anywhere in this class, and no subclass of `ScenesPreset` exists anywhere
in the decompiled tree** (`grep -rln ": ScenesPreset"` across the full decompile returns zero hits; `grep
-rln "GameModes"` across the full decompile returns exactly one hit, in the unrelated `EFT/RaidSettings.cs`).
This is the exact class the donor bundle's `MonoBehaviour.m_Script` resolves against per WP3's own stated
rationale (*"the MonoBehaviour's script reference stays BSG's own ... binds to `EFT.ScenesPreset` by class
identity in the SPT client"*), and it is the same client install (`D:/SPT41`) the decompile was taken from.

**Consequence**: either (a) the WP3 evidence-gathering pass mis-transcribed a field that isn't actually part
of the donor's typetree (most likely — no other explanation fits: no alternate class, no `[JsonExtensionData]`-
style bag exists on a Unity `MonoBehaviour` typetree), in which case `tt['GameModes'] = []` in the patch
script sets a dict key with no corresponding typetree node, or (b) the field genuinely is present in the raw
typetree for some reason not explained by the current decompile, in which case the current decompile is
itself stale relative to the exact binary that baked `develop_preset.bundle`. Either way this is a real gap
in what §B.WP3 presents as directly-confirmed ground truth. Operationally the blast radius is probably small
— UnityPy's `save_typetree()` typically writes only nodes present in the object's actual typetree and
silently ignores unrecognized dict keys, so `tt['GameModes'] = []` most likely becomes a silent no-op rather
than a corrupt save — but this should be verified (re-dump the donor's raw typetree with
`obj.read_typetree()` and diff its key set against this exact field list) before `patch_preset.py` ships,
since WP3's own acceptance criteria (§B.WP3) never re-checks for a `GameModes` key after the round trip and
would not catch a silent write failure here.

**Severity**: major (evidence-integrity + a script instruction that writes to a field that doesn't exist),
not blocker (unlikely to corrupt the save given typical UnityPy behavior, but unverified).

---

## Finding 3 — MINOR: `ScenesGuids` donor length was never confirmed; "trim to 6" can be a silent no-op, and the field is not actually "unused by the load chain"

**SPEC's claim** (§B.WP3 step 7): *"`tt['ScenesGuids'] = tt['ScenesGuids'][:6]` (trim the parallel list to
the same count — donor guids are unused by every traced load-chain call site...)"* and §E.3's summary table:
*"`ScenesGuids` | trimmed to 6 entries (donor values, unused by the traced load chain)."*

**Verification**: `ScenesPreset.GetTotalSceneCount()` (`ScenesPreset.cs:95-98`):
```csharp
public int GetTotalSceneCount()
{
    return ScenesGuids.Where(ShouldLoadScene).Count() + ChildPresets.Sum((ScenesPreset x) => x.GetTotalSceneCount());
}
```
is called from `LoadScenesFromPresetOperation.LoadPresetFromConfigAsync`
(`EFT/LoadScenesFromPresetOperation.cs:116`, `_totalScenesToLoad = scenesPreset.GetTotalSceneCount();`) and
used as the denominator for the loading-progress callback (`:117-122`,
`_scenesLoaded / (float)_totalScenesToLoad`). So `ScenesGuids` **is** read by the traced load chain — just
not to gate or resolve scene identity (that part of SPEC's claim is correct), only for the progress bar.

Two compounding issues: (1) SPEC's own "confirmed" field dump for the donor (§B.WP3 intro) never states how
many entries `ScenesGuids` actually has — only `_scenesResourceKeys: 3 entries` is reported. (2) Python
`list[:6]` on a list shorter than 6 is a silent no-op (no `IndexError`, no padding) — if the donor's
`ScenesGuids` is length 3 (a reasonable guess, paralleling its 3 `_scenesResourceKeys`), the patch script's
`[:6]` slice leaves it at 3, not 6, silently under-counting `GetTotalSceneCount()` relative to the 6 real
scenes actually being loaded.

**Consequence**: cosmetic only, not a load-chain gate — the progress bar can overshoot 100% (e.g. reads
`4/3`, `5/3`, `6/3`) partway through scene loading, but nothing in `LoadPresetAsync`/`ScenesLoadCoroutine`
(`LoadScenesFromPresetOperation.cs:151,233-291`) uses `_totalScenesToLoad`/`ScenesGuids` to decide which or
how many scenes to load — that's driven entirely by `preset.ScenesResourceKeys` (the 6-entry
`_scenesResourceKeys` list). No crash risk identified (`float` division by a small positive int, or even by
zero, does not throw in C#).

**Fix**: before assuming `[:6]` is correct, print the donor's actual `ScenesGuids` length; if it's less than
6, either pad with placeholder `Guid` entries (cheap, matches "guids unused for identity" reasoning) or
accept the cosmetic progress-bar drift explicitly rather than by accident. Correct the §E.3/§B.WP3 language
from "unused by the load chain" to "unused for scene identity/gating, but read for progress-bar totals."

**Severity**: minor.

---

## Finding 4 — Risk-register correction (favorable): `PerfectCullingCrossSceneSampler.InitializeAutoCulling` does not appear to hard-abort for an occlusion-data-free scene set — the `[OPEN]` risk carried since `spt-location-contract.md` should be down-weighted

**SPEC's claim** (`docs/SPEC.md` §F.6, §H risk #2): flags this as *"[OPEN, potential hard requirement]"*,
*"Medium-High for the un-mitigated case,"* carried forward unverified from `critic.md` §2b and
`spt-location-contract.md` §2/§7 step 5 (both explicitly say "not traced this pass"/"[OPEN]"). §F.6 proposes
a Harmony prefix that unconditionally skips the method body for our scenes as the primary mitigation.

**Verification**: read the full method body,
`Koenigz.PerfectCulling.EFT/PerfectCullingCrossSceneSampler.cs:500-522`:
```csharp
public static async Task InitializeAutoCulling(CancellationToken token, IProgress<float> progress = null)
{
    PerfectCullingLog.Debug("Initializing autoculling");
    if (PerfectCullingCrossSceneVolume.AllRuntimeCrossGroupVolumes.Count > 0)
    { PerfectCullingLog.Throw("Previous autoculling was not cleared properly"); }
    foreach (PerfectCullingCrossSceneGroup allCrossGroup in PerfectCullingCrossSceneGroup.AllCrossGroups)
    { allCrossGroup.OnPostLevelLoaded(); }
    PerfectCullingAdaptiveGrid instance = PerfectCullingAdaptiveGrid.Instance;
    if (instance != null) { await instance.InitializeGridAsync(token, progress); }
    if (!token.IsCancellationRequested)
    {
        smethod_9();
        ScreenDistanceSwitcher.AllowRuntimeUpdate = true;
        _analSources = UnityEngine.Object.FindObjectsOfType<AnalyticSource>();
    }
}
```
For a scene set with **zero** baked PerfectCulling data (our case, decision #8): `AllRuntimeCrossGroupVolumes`
is empty (the `Throw` guards a *stale-state* case, not an absence case, and doesn't fire here);
`PerfectCullingCrossSceneGroup.AllCrossGroups` is empty so the `foreach` is a no-op; critically,
`PerfectCullingAdaptiveGrid.Instance` is `null` with no baked grid registered, so the entire
`InitializeGridAsync` branch — the only part of this method that plausibly touches baked-data files — is
skipped via the `if (instance != null)` guard. The remaining tail (`smethod_9()`,
`ScreenDistanceSwitcher.AllowRuntimeUpdate = true`, `FindObjectsOfType<AnalyticSource>()`) has no dependency
on baked culling data. I found no code path in this method that would throw specifically *because* the
scene set lacks baked occlusion data — the try/catch at the call site
(`EFT/TarkovApplication.cs:2756-2765` this pass, cited as `:2761-2770` in `spt-location-contract.md` — line
numbers shifted slightly but the same wrapper: `catch (Exception exception2) { ...; throw new
Exception("Loading data file failed"); }`) is real, but nothing in the method itself appears to trip it for
an empty-culling-data scene set.

**Recommendation**: keep §F.6's Harmony prefix as a harmless defensive measure (it costs nothing and a
future scene set could behave differently), but do **not** treat PerfectCulling as the most likely raid-start
blocker in `docs/SPEC.md` §H's risk ranking or in the §G.S4 triage order — Finding 1 above is a confirmed,
much higher-probability blocker that would abort the raid earlier in the sequence (step 6, `BaseLocalGame.Run`)
than `PerfectCullingCrossSceneSampler.InitializeAutoCulling` even runs (it's called from `LoadMapAndData`,
which completes before `BaseLocalGame.Run`) — so in practice Finding 1 would be hit and fixed first regardless.

**Severity**: minor / informational (corrects a carried-forward "[OPEN]" risk assessment with new evidence;
does not itself indicate a defect in the SPEC's design).

---

## What was independently re-verified and found accurate (no discrepancy — listed so the calling agent
doesn't re-litigate these)

- **`BundlesManager` hook (§F.1)**: `_bundles` is a public `Dictionary<string, AssetBundleReference>`,
  `AssetBundleReference` has a public `(AssetBundle)` constructor (`BundlesManager.cs:14-25,113`), confirmed
  by full-file read. `BundleLoadOperation.Run` (`:64-82`) really does check
  `manager._bundles.TryGetValue(bundleName, ...)` as its first line, so a Harmony **Prefix** on
  `BundlesManager.LoadBundleAsync` that pre-populates `_bundles[bundleName]` before the original body runs
  (which calls `BundleLoadOperation.Run`) works exactly as designed — traced the call graph from both the
  preset-asset load path (`AssetsManager.cs`'s `LoadOperation.Run` → `LoadAssetOperation`, line 169:
  `manager.BundlesManager.LoadBundleAsync(BundleName)`) and the scene-bundle load path
  (`EFT.AssetsManager.AssetsManager.LoadSceneOperation.Coroutine`, line 363:
  `await manager.BundlesManager.LoadBundleAsync(bundleName, logErrors: false);`) — **both** of the SPEC's
  three served bundle types actually route through `LoadBundleAsync`, confirming the single hook point
  covers everything it needs to.
- **Scene-name resolution is bundle-agnostic** (§B.WP2.5's "name resolves in any loaded bundle" claim):
  confirmed — `AssetsManagerExtension.LoadScene` (`EFT/AssetsManagerExtension.cs:73-78`) uses
  `resourceKey.path` purely as the *bundle-to-load* key and `resourceKey.ToAssetName()` (i.e. `rcid`) purely
  as the *Unity scene name* passed to `SceneManager.LoadSceneAsync` (`AssetsManager.cs`'s
  `LoadSceneOperation.Coroutine:365`), which finds a scene of that name in any currently-loaded bundle
  regardless of which bundle string loaded it.
- **`ScenesPreset`/`SceneResourceKey` field shapes** (§B.WP3/§E.3 table, minus Finding 2/3 above):
  `_scenesResourceKeys: List<SceneResourceKey>`, `SceneResourceKey : ResourceKey, ISceneResource` with a
  serialized `_onlyOffline` bool (`EFT/SceneResourceKey.cs`, `EFT/ISceneResource.cs`, full files read) —
  matches SPEC's `{path, rcid, _onlyOffline}` shape exactly. `ActiveSceneName` is genuinely just
  `_activeSceneName` (no `ActiveSceneGuid` involvement) per `ScenesPreset.cs:71` and confirmed consumed that
  way at `LoadScenesFromPresetOperation.cs:215-222`.
- **`LevelSettings` hard-NRE + field list** (§D row 1): `Awake()` really is
  `Singleton<LevelSettings>.Create(this)` at the cited lines (`LevelSettings.cs:166-171`, exact match), and
  `OnPostLoadingScene()` (`:173-185`) is called only `if (loadScenesFromPresetOperation.Succeed)` right after
  scene load (`TarkovApplication.cs`, confirmed this pass at the equivalent of the cited `:2731`). Field names
  (`AmbientMode`, `SkyColor`, `EquatorColor`, `GroundColor`, `AmbientIntensity`, `fog`, `skybox`,
  `CameraPrefab`, `PostProcessProfilePrefab`) all match exactly.
- **`LocationScene.Awake()` hard-NRE on `WorldInteractiveObjects`** (§D row 2): confirmed at the exact cited
  lines (`LocationScene.cs:322-324` unconditional `Register(...)` calls, `:381-388` the unconditional
  `worldInteractiveObjects.Length` loop that NREs if the field is `null`). `FillArrays()`/`FillArraysFrom()`
  (`:423-464`) does populate every one of `LocationScene`'s own ~28 array/list fields via
  `FindAllObjectsOnScene<T>` as claimed — **except** `treeWinds`/`treeWindSettingsPresets`, which are
  populated by a separate `FillTreeWinds()`/`OnPreProcess()` path (`:466-490`) that `FillArrays()` does not
  call and that §B.WP2.6's `AuthorScriptsScene.cs` does not invoke either. This is very likely harmless for
  the spike — `LocationScene.GetObjects<T>()` (`:278-285`) uses a `value is T[] result` pattern match that
  returns `Array.Empty<T>()` for a null-valued dictionary entry, so `LevelSettings.OnPostLoadingScene()`'s
  `LocationScene.GetAllObjects<TreeWind>()` call degrades gracefully — but note it for completeness since it
  is a field `FillArrays()` does **not** actually cover, contrary to the "every one of the ~30 array fields"
  framing in §D's `LocationScene` row.
- **`BotZone.Init` hard-NRE on `PatrolWays`** (§D row 3): confirmed exact match at `BotZone.cs:138-139`
  (unconditional `patrolWays.Length` loop) and the `SpawnPointMarkers != null && SpawnPointMarkers.Count > 0`
  guard at `:149` — accurate as far as it goes (see Finding 1 for what this citation misses).
  `AICoversData.CreateOrFind`'s NRE-if-zero-`BotZone`s claim also confirmed (`AICoversData.cs:288-297`,
  unconditional `botZone.gameObject.transform` after `FindObjectOfType<BotZone>()`) — SPEC's plan to author
  exactly one `BotZone` correctly avoids this specific one.
- **`LocationExportInfo`** (§D row 4): trivial one-field class exactly as described
  (`EFT/LocationExportInfo.cs`, full file).
- **`SpawnPointMarker`/`SpawnPoint` field shapes** (§D row 5): `Start()` logs-not-crashes on a missing/
  disabled collider (`SpawnPointMarker.cs:96-116`), confirmed. `SpawnPointsCollection.CreateFromScene`/
  `IsValid`/`FixSpawns` (`SpawnPointsCollection.cs:151-259`) match `spt-location-contract.md` §3's mechanics
  exactly, including the "empty `SpawnPointParams` array deletes every scene marker, `null` skips
  reconciliation entirely" behavior `critic.md` §6/SPEC §E.4 rely on — `FixSpawns:199-211` really does
  `DestroyImmediate` any scene marker whose `Id` isn't in a non-null `parameters` array, confirming
  `SpawnPointParams` must stay genuinely absent/`null`, not `[]`, exactly as SPEC's §E.4 already concludes.
- **Server-side registration mechanism** (`critic.md` §1, restated SPEC §B.WP5 step 8): independently
  re-derived from `analysis/decompiled-server-415/SPTarkov.Server.Core.Models.Spt.Tables.LocationTable.decompiled.cs`
  (full file read). `GetLocation(name)` does `GetDictionary().TryGetValue(GetMappedKey(name), out value)`
  first (`:145-150`) — since `_locationMappings` (`:98-136`) has no entry for `"arena_equator"`,
  `GetMappedKey` returns the key unchanged, and the dictionary `.Add("arena_equator", ourLocation)` performed
  once during `OnLoadAsync` is found on every subsequent `GetLocation`/`GetDictionary()` call thereafter
  (the cache is a mutable object returned by reference, never rebuilt after first hydration,
  `:158-187`) — confirms the whole "ride `GetDictionary().Add(...)`, don't fight the closed record" design is
  sound, independent of `critic.md`'s own account.
- **Loot `LazyLoad` shape** (§B.WP5 step 3): `Location`'s actual field types
  (`analysis/decompiled-server/Location.cs:22-50`) — `LooseLoot: LazyLoad<LooseLoot>?`,
  `StaticLoot: LazyLoad<Dictionary<MongoId, StaticLootDetails>>?`,
  `StaticContainers: LazyLoad<StaticContainerDetails>?`,
  `StaticAmmo: Dictionary<string, IEnumerable<StaticAmmoDetails>>` (**not** `LazyLoad`-wrapped),
  `AllExtracts: IEnumerable<AllExtractsExit>` (**not** `LazyLoad`-wrapped) — all match SPEC's code snippet's
  types exactly, including the two fields SPEC correctly does *not* wrap in `LazyLoad`.
- **Bundle-name case handling** (§F.1's "case-insensitive dict" note): confirmed necessary/correct —
  `AssetsManager.LoadAssetAsync(ResourceKey)` lowercases `bundleName` before calling
  `BundlesManager.LoadBundleAsync` (`AssetsManager.cs:519-527`), while the scene-loading path
  (`AssetsManager.LoadScene:546-549` → `LoadSceneOperation.Coroutine:363`) does **not** lowercase it — so the
  two bundle types genuinely can reach the hook with different casing conventions if the served-key strings
  ever contained uppercase; SPEC's §0 keys are already all-lowercase and the dict is explicitly documented as
  case-insensitive, so this is correctly defended against, not a gap.

---

## Items explicitly out of this pass's scope (not re-verified, flagged already by the SPEC itself)

- `StaticContainerDetails`'s exact 4.1.5 field shape (SPEC §B.WP5 edge cases / §H risk #9) — SPEC already
  flags this as unverified and a compile-time-catchable risk; not independently re-diffed this pass.
- The client-side map-select UI filter class (`MatchMakerSelectionLocationScreen`/equivalent) that reads
  `Enabled`/`Locked` — SPEC's §E.6 already marks this `[OPEN — not independently re-traced]`, consistent with
  `spt-location-contract.md` §5.4; not chased down this pass either since `LocationController.GenerateAll`'s
  server-side behavior (which *was* re-verified, unconditionally returns every entry with non-null `Base`)
  is the part that actually gates whether the spike's location reaches the client at all.
- The full Unity-6-IL2CPP → 2022.3.43f1-Mono geometry/NavMesh round-trip (`critic.md` §2a, SPEC §H risk #1) —
  outside this review's lens (SPT-runtime correctness of the *client/server contract*, not the asset
  pipeline); S1's smoke test remains the right gate for that risk regardless of this review's findings.
