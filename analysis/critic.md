# Completeness critic — spike: "Arena map geometry+colliders+navmesh loads as a new SPT 4.1 location and the player can walk around"

Reviewed: `ref-interchange.md`, `ref-manimal.md`, `arena-census.md`, `spt-location-contract.md`, all four
verify-passes (`ref-interchange-verify-F3/F4/F7.md`, `verify-ref-interchange-f2.md`), and the structured
REPORTS/VERDICTS JSON supplied with the task. One direct read-only check was run against the live SPT
install to resolve the most important contradiction found (below). New evidence file produced by this
pass: `D:/ArenaMapPort/analysis/decompiled-server-415/SPTarkov.Server.Core.Models.Spt.Tables.LocationTable.decompiled.cs`.

---

## 1. Contradiction found and resolved: the server DB is NOT a closed set — `spt-location-contract`'s central recommendation is wrong

**The claim in `spt-location-contract.md`** (§5.2, lines 305–338, and the checklist at §7 step 1):
*"The DB shape is a closed set — you cannot add a new location id"* → recommendation: ride the dormant
`suburbs` or `town` `LocationTable` slot, do not invent a new id.

**The claim in `ref-manimal.md`** (§1, lines 14–44): Icebreaker **v1.1.0** (current, MIT-licensed, dated
2026-09-12 — one day before this spike) registers a genuinely **new** location id at runtime via
`table.GetDictionary().Add(Key, location)`, and its own doc explicitly states this *supersedes* the
"closed record, ride a dormant stub" guidance for SPT 4.1.5: *"The older project comments saying the
closed `Locations` record forces reuse of Suburbs do not describe the available registration path in the
installed 4.1.5 implementation."*

These two reports directly disagree about a load-bearing architectural fact. **I re-derived this from the
source myself rather than trusting either report's characterization**, because the answer changes the
spike's entire server-side plan (own new id vs. hijacking `suburbs`).

**Resolution — confirmed in ref-manimal's favor; `spt-location-contract`'s "closed set" framing is a
mistaken inference, not a version issue:**

- `spt-location-contract.md` decompiled the **4.1.0** NuGet package
  (`sptarkov.server.core/4.1.0/lib/net10.0/SPTarkov.Server.Core.dll`) — the compile-time reference
  ArenaMode.Server.csproj uses (`ExcludeAssets="runtime"`, confirmed:
  `D:/SPT41/Development/ArenaMode/ArenaMode.Server/ArenaMode.Server.csproj:41`).
- The **actually-running** server at `D:/SPT41/SPT_Runtime/SPTarkov.Server.Core.dll` is a **different
  build**: `FileVersion 4.1.5`, `ProductVersion 4.1.5-RELEASE+7d7add5.20260905...`, 5,660,160 bytes vs the
  NuGet DLL's 5,636,608 bytes, SHA-256 mismatch (confirmed via `Get-Item`/`Get-FileHash` this pass) — i.e.
  genuinely not byte-identical to what `spt-location-contract.md` read. This matches Icebreaker's own
  doc targeting "4.1.5" exactly, not the older 4.1.0 reference build.
- I decompiled `LocationTable` fresh from the **actually-deployed 4.1.5 DLL** with `ilspycmd 11`
  (output: `D:/ArenaMapPort/analysis/decompiled-server-415/SPTarkov.Server.Core.Models.Spt.Tables.LocationTable.decompiled.cs`)
  and compared it line-by-line against the 4.1.0 dump `spt-location-contract.md` already cites
  (`D:/ArenaMapPort/analysis/decompiled-server/LocationTable.cs`). **The `GetDictionary()`/
  `HydrateDictionary()` logic is byte-identical in both versions**:
  ```csharp
  public Dictionary<string, Location> GetDictionary() {
      if (_locationDictionaryCache == null) { HydrateDictionary(); }
      return _locationDictionaryCache;
  }
  private void HydrateDictionary() {
      // reflection over the fixed record properties — this only SEEDS the dictionary
      _locationDictionaryCache = /* ...ToDictionary(...) */;
  }
  ```
  (4.1.0: lines 153–178; 4.1.5: lines 158–187 of the respective files.)
- **The critical fact `spt-location-contract.md` states correctly but concludes wrongly from**: yes, the
  dictionary is *seeded* only from the fixed `[JsonPropertyName]` record properties (`Bigmap`, `Suburbs`,
  `Town`, … 19 of them) via reflection. But `_locationDictionaryCache` is hydrated **lazily, exactly once**,
  and then returned **by reference** on every subsequent call — it is a perfectly ordinary mutable
  `Dictionary<string, Location>`. Nothing stops any code (Icebreaker's `IOnLoad` mod, or an equivalent
  ArenaMapPort one) from calling `.Add("arena_equator", newLocation)` on that same returned reference once,
  after which **every later `GetDictionary()`/`GetLocation()` call in the process — including
  `LocationController.GenerateAll` (`/client/locations`) and `LocationLifecycleService
  .GenerateLocationAndLoot`, both already traced as reading through `GetDictionary()` in
  `spt-location-contract.md` §5.2/§5.4 itself — sees the new entry**, because the cache is never rebuilt.
  The record's fixed properties constrain what you can deserialize *into* `LocationTable` from JSON; they
  do not constrain what the live dictionary can hold at runtime.

**Correction to apply before writing code**: do **not** ride `suburbs`/`town`. Register a genuinely new
location id following Icebreaker v1.1.0's pattern (`icebreaker-server/IcebreakerLocationRegistration.cs`,
cited in `ref-manimal.md` §1) — it is simpler (no fighting over a dormant slot's possibly-nonzero residual
`base.json` semantics, no ambiguity for saves/profiles about what "suburbs" now means) and is the
better-evidenced, version-matched approach (Icebreaker's own doc targets 4.1.5; this project's server *is*
4.1.5). Everything else in `spt-location-contract.md`'s client-side load chain, hard-NRE table, and spawn
reconciliation logic (§1–§4) is unaffected by this correction and remains reliable — only §5.2/§5.3's
"closed set, ride a dormant stub" conclusion and the corresponding §7 checklist step 1 need to change.

**One real trade-off this correction introduces, not previously flagged by either report as a task item**:
registering a genuinely new id inherits *nothing* for free. Icebreaker's own registration code
(`ref-manimal.md` §1 point 3) had to explicitly set `botConfig.MaxBotCap[key]` because unlisted map ids
silently fall back to `MaxBotCap["default"] = 18`, and separately had to clone `ScavRaidTimeSettings`/
`StaticLootMultiplier`/`LooseLootMultiplier` from a donor map and add itself to `locationConfig.NonMaps`.
None of this blocks the geometry-only "player can walk around" spike (bots/loot are out of scope per the
project plan), but it is a concrete punch-list for whoever adds bots later, and nobody has produced the
SPT-4.1.5-specific equivalent of this inventory (Icebreaker's list is 4.1.5-current per its own doc, so it
is very likely directly reusable, just not yet cross-checked against ArenaMode's own config surface).

---

## 2. Unresolved blockers — things that could still stop "player can walk around" from working, not settled by any report

### 2a. The Unity-version / IL2CPP geometry+NavMesh round-trip is completely untested (the single biggest risk)
Every report's optimism rests on an unproven assumption: that a mesh, collider, or `NavMeshData` object
read out of Arena's **Unity 6000.3.6f1, IL2CPP, encrypted-metadata** Player build via UnityPy can actually
be re-imported into the target **Unity 2022.3.43f1** Editor project and rebuilt into an AssetBundle the
**Mono** SPT 4.1 client loads correctly.
- `ref-interchange.md` and `ref-manimal.md`'s entire evidentiary base (Icebreaker, Labs, InterchangeRework)
  is a **same-Unity-family, Mono→Mono, un-encrypted** retail-EFT rip — a categorically easier case than
  Arena's IL2CPP/encrypted-metadata/different-major-Unity-version rip. `ref-interchange-verify-F7.md`'s own
  residual-gap note says this outright: *"the pipeline-fidelity check used a retail-EFT (Mono) rip, not an
  actual Arena (IL2CPP) rip, which doesn't exist yet in this environment."*
- `arena-census.md` only reads Arena's **serialized binary data** via UnityPy's format-agnostic parser — it
  never attempts an Editor import or bundle rebuild.
- I confirmed this pass that `D:/ArenaMapPort/UnityProject/Assets` is still empty (freshly created, default
  `Library` cache only, no content, no round-trip test artifacts anywhere under `D:/ArenaMapPort/`) — i.e.
  this is not yet a "we assume it works, mostly proven" situation, it is a **completely open** one.
- NavMeshData specifically: `arena-census.md` §10 picks Equator partly *because* its NavMesh is simpler
  (41 tiles, flat), but nobody has verified a Unity-6-baked `NavMeshData` object can be carried into a
  2022.3.43f1 project at all — tile-count/complexity is irrelevant if the binary format itself doesn't
  round-trip.

**Before writing extraction/import code at scale**: do a single, minimal round-trip test — take ONE small
mesh (or even a synthetic placeholder) plus Equator's `NavMeshData` object, get them into the
`D:/ArenaMapPort/UnityProject` Editor project, build a bundle, and load it in a throwaway BepInEx test
target — before investing in a full extractor for 880k vertices / 41 NavMesh tiles / 232 textures. This is
the one prerequisite no report has performed and no report can substitute for by more reading.

### 2b. `PerfectCullingCrossSceneSampler.InitializeAutoCulling` — flagged OPEN by `spt-location-contract.md` itself, still open
`spt-location-contract.md` §2 (table row) and §7 step 5 both explicitly flag this as the one call in the
whole load chain that could **hard-abort** `LoadMapAndData` for a scene with no baked occlusion data
(wrapped in a catch that rethrows as `"Loading data file failed"`), and recommend testing it "early." No
other report tested it. `arena-census.md` confirms Arena's own scenes *do* carry an `OcclusionCullingData`
object (2.76 MB on Equator) — but that is Arena's own PVS bake, in Arena's own coordinate/scene-hash space;
whether SPT's `PerfectCullingCrossSceneSampler` will accept an occlusion bake carried over into a
newly-authored 2022.3.43f1 scene (vs. one baked by that exact Editor for that exact merged scene set) is
unverified. **Not resolved by this pass either** (would require an actual load attempt) — carry forward as
the top runtime risk to test first, before or alongside 2a.

### 2c. Custom-field data (Category/Side on spawn points, PatrolWays on BotZone, etc.) is not actually extractable from Arena — confirmed this pass, not previously stated plainly
Project context already established "Arena custom MonoBehaviour field data is NOT readable via UnityPy (no
typetrees)." `arena-census.md`'s own Method notes independently reconfirm this (§ Method notes point 2: a
raw `read_typetree()` on an Arena-custom MonoBehaviour throws; the workaround only recovers the **class
name**, not the fields). I verified directly this pass that `equator_census.json`'s
`spawn_zone_anchors → EFT.Game.Spawning.SpawnPointMarker` entry contains **only `count`, `sample_positions`,
`sample_files`** — no `Category`, `Side`, `Id`, or `BotZoneName`. The same is true for `BotZone`'s
`PatrolWays`, `AICorePoint`'s parameters, etc.

This matters concretely for the spike's own success criterion: `spt-location-contract.md` §3 establishes
that the local player needs **at least one `SpawnPointMarker` with `Category=Player` and a `Side` the test
profile matches**, or there is nothing to spawn on. **Arena's own 61 authored Equator spawn points cannot
be bulk-harvested with correct category/side data by the pipeline demonstrated in any of the four
reports** — only their existence, class, and world position are recoverable. This is not fatal (hand-
authoring one fresh `SpawnPointMarker` in the SDK scene, which `spt-location-contract.md`'s own checklist
already recommends as the safe path, sidesteps the need entirely), but no report states plainly that
*automatic* spawn-point porting is currently infeasible — worth being explicit about so the plan doesn't
silently assume Arena's spawn layout can be reused wholesale later without new tooling (IL2CPP-aware
typetree/field recovery, which none of the Manimal/Interchange precedents needed since they all rip
Mono/un-encrypted retail EFT).

### 2d. SDK-side authoring of EFT-typed components (`LocationScene`, `BotZone`, `LevelSettings`, `LocationExportInfo`) is referenced but never concretely resolved for Arena
`spt-location-contract.md`'s checklist (§7) says to "author" these components directly in the Unity SDK
scene with every array field non-null. `ref-manimal.md` §3 notes the mechanism precedent
(`analysis/generate_camera_stubs.py`, "typetree-exact C# stub generation for the SDK, all-or-nothing per
class") but only for a camera, and explicitly as the **abandoned** approach for that specific case (the
shipped fix for cameras is add-only runtime grafting instead, precisely because bundled/stubbed component
authoring is fragile). No report walks through actually generating matching stub types for
`LocationScene`/`BotZone`/`LevelSettings`/`LocationExportInfo`/`SpawnPointMarker` so the
`D:/ArenaMapPort/UnityProject` Editor can attach them at all (these are `EFT`-namespace types internal to
the game's own assembly, not a published SDK). This is very likely solvable (Icebreaker/Labs clearly did
it for their own component sets) but is presently an unresolved **mechanism**, not just an authoring
checklist item — settle which of (a) typetree-exact stub generation, or (b) Labs' fully-reflective
post-load runtime repair (`LabsBoilerLocationSceneFix.cs`, which rebuilds `LocationScene`'s arrays
entirely at `sceneLoaded` time rather than authoring them in the SDK at all) the spike will use, since it
changes what the SDK scene itself needs to contain.

---

## 3. Missing facts to settle before writing code (quick, concrete, do first)

1. **Confirmed this pass, act on it**: target location id = a genuinely new id registered via
   `LocationTable.GetDictionary().Add(...)` (Icebreaker v1.1.0 pattern), not `suburbs`/`town`. Update any
   planning docs that still say "ride a dormant stub."
2. **Do the minimal Unity-6→2022.3.43f1 round-trip test (§2a) before building the full extractor.** This
   is the one fact that, if it fails, invalidates the extraction approach both `ref-manimal.md` and
   `arena-census.md` assume is straightforward.
3. **Decide how `PerfectCullingCrossSceneSampler` risk (§2b) will be probed** — e.g. attempt a raid load
   with a deliberately minimal/placeholder scene (as `spt-location-contract.md` §7 step 5 itself already
   recommends) before sinking time into full geometry import, so a hard-abort here is caught early and
   cheaply rather than after a multi-hour extraction run.
4. **Decide the SDK-authoring mechanism for the five required EFT components (§2d)** — stub-generation vs.
   Labs-style runtime reflective repair — before scene authoring starts, since it determines what, if
   anything, needs to be hand-placed in the Unity Editor scene itself vs. patched in at `sceneLoaded`.
5. **Explicitly plan for hand-authored (not extracted) placeholder `SpawnPointMarker`(s), `BotZone`
   (`PatrolWays=[]`), `LevelSettings`, and `LocationExportInfo`** for the first pass, given §2c — this
   matches `spt-location-contract.md`'s own checklist, just make it an explicit decision rather than an
   implicit fallback, since automatic porting of Arena's own spawn/zone authoring is not currently
   possible.
6. **Keep `base.json`'s `SpawnPointParams` genuinely `null` (not `[]`)** for the first pass, per
   `spt-location-contract.md` §3's own correctly-derived-but-easy-to-miss finding: an empty (non-null)
   array actively **deletes** every scene-authored `SpawnPointMarker` on load, since `FixSpawns` treats
   "present in scene but absent from the backend list" as delete-worthy regardless of whether the backend
   list is empty or populated.
7. **Not urgent for this spike, but note for the next one**: no report inventories which other per-map-keyed
   server config surfaces (`MaxBotCap`, `ScavRaidTimeSettings.Maps`, `StaticLootMultiplier`/
   `LooseLootMultiplier`, `locationConfig.NonMaps`) a brand-new id needs entries in, beyond Icebreaker's own
   4.1.5-current list (`ref-manimal.md` §1 point 3, 6, 7) — irrelevant while bots/loot are out of scope, but
   don't assume it's a non-issue once they're added.

---

## What is *not* contradicted / *not* newly at risk (for completeness, so these aren't re-litigated)

- The client load-chain and hard-NRE table in `spt-location-contract.md` §1–§2 (LevelSettings,
  `WorldInteractiveObjects` non-null, ≥1 `BotZone`, `PatrolWays=[]`) — independently corroborated by
  `ref-manimal.md`'s Icebreaker/Labs "what breaks when it's absent" findings (§5 of that report);
  consistent across both.
- Collider layer indices / `PhysicMaterial` surviving a same-codebase rip unmodified — `ref-interchange`
  F7's verdict already re-verified this directly (byte-identical `TagManager`/`PhysicsManager` between the
  SPT 4.1 client and the Arena client), and `arena-census.md` §7 independently confirms Arena's own 32-slot
  layer table is identical to SPT's. No new contradiction found here.
- No static batching, no baked lightmaps on either Arena candidate map — confirmed independently by
  `arena-census.md`'s own ground-truth renderer scan (verdict: confirmed, re-derived from source
  independently in the supplied VERDICTS). Not affected by anything in this pass.
- Arena_Equator/TeamFight as the smaller, flatter spike map — confirmed and re-verified independently
  (supplied VERDICTS). Still the right choice regardless of the §1 correction (id-registration approach)
  or the §2 open risks (which apply equally to either candidate map).
- `ArenaContext.Config.Maps` structure — checked this pass
  (`D:/SPT41/Development/ArenaMode/ArenaMode.Server/ArenaModeConfig.cs:47-48`): a plain
  `Dictionary<string, ArenaMapConfig>` keyed by lowercased map id. Adding a new key needs no structural
  change, regardless of whether the id is a dormant-stub name or a genuinely new one — resolves
  `spt-location-contract.md`'s own open question on this point.
