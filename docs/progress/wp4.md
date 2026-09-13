# WP4 — Client plugin (progress log)

Status: **code complete, compiles clean (Debug + Release), staged into `dist/`. NOT tested in-game
(S4 is a separate, user-run step per O4/§G.S4). Two of the three bundles + material-shaders.json are
not yet in `dist/` because WP2 has not produced them yet** — see "Known gaps" below.

## What was built

`D:/ArenaMapPort/ArenaMapPort.Client/` — BepInEx 5 plugin, `netstandard2.1`, assembly name
`maschine-ArenaMapPort.Client` (§0), plugin GUID `com.maschine.ArenaMapPort`. References mirror
`ArenaMode.Client.csproj` (Assembly-CSharp[-firstpass], Comfort[.Unity], Newtonsoft.Json, the UnityEngine
module set, BepInEx.Core/Analyzers/PluginInfoProps) plus `UnityEngine.AssetBundleModule` (needed for
`AssetBundle`/`AssetBundle.LoadFromFile`, which none of ArenaMode's own references happened to need
directly — build failed without it, see "Verification" below) and `UnityEngine.AIModule` (already in
ArenaMode's list, needed for `NavMesh`).

`D:/ArenaMapPort/ArenaMapPort.Shared/ArenaEquatorLocation.cs` — **created by this WP4 pass**, not WP5.
WP5 had not run yet when this pass started and `ArenaMapPort.Client` needs this file to link and compile
(Owns line: "links `ArenaMapPort.Shared/ArenaEquatorLocation.cs`"). Contains exactly §0's identity
constants and nothing else — no behaviour, no WP5-specific fields. **This turned out fine**: WP5 ran
concurrently with this session and its `ArenaMapPort.Server` compiled successfully against this exact
file, unmodified (confirmed: the file's mtime predates every `ArenaMapPort.Server/*.cs` file by several
minutes, and `dotnet build` for the server half succeeded — `dist/SPT_Runtime/user/mods/ArenaMapPort/
maschine-ArenaMapPort.Server.dll` exists). WP5 should still treat this as the file it owns going forward;
flag here in case the lead wants it re-reviewed.

Files, one concern each:

| File | §, purpose |
|---|---|
| `Plugin.cs` | `BepInPlugin` entry. Awake(): config (`Logging.Verbose` — the one `LogLevel`-style toggle §F.5 allows), startup banner (logs which of the 3 bundles + material-shaders.json are present at the plugin dir, non-fatal if missing), installs Harmony patches via `HarmonyPatches.InstallAll`, installs `SceneDiagnostics`/`ShaderRebind`. |
| `ArenaEquatorRaid.cs` | The gate every hook uses: `IsLoaded()` (Scripts scene present) and `IsOwnedScene(name)` (one of the 6 §E.1 scenes). |
| `BundleHost.cs` | §F.1. `BundleHostPrefix.Prefix` + `BundleHost.TryServe` — pre-populates `BundlesManager._bundles` before `BundleLoadOperation.Run` checks it. Verified public `_bundles`/`AssetBundleReference` on this decompiled build — no `AccessTools` reflection needed for the *field access itself* (still resolved via `AccessTools.Method` for the *patch target*, per the brief). |
| `NavMeshInstaller.cs` | §F.2. Near-verbatim port of `InterchangeRework.Client/NavMeshExtension.cs` (MIT, attributed) — prefix on `BotsController.Init`. Loads `arena_equator_navmesh.bundle` directly via `AssetBundle.LoadFromFile` (not through `BundlesManager` — nothing in the vanilla load chain requests it by served key since it isn't in `_scenesResourceKeys`, matching the reference implementation's own approach). |
| `ShaderRebind.cs` | §F.3 + **LEAD OVERRIDE O1**. Rebinds every material whose current shader name starts with `Hidden/ArenaMapPort/` to the original name, exact-name lookup among loaded shaders first, then `Shader.Find`, never to a prefixed shader. Restores per-material renderQueue/keywords from `material-shaders.json` (keyed by material name, `" (Instance)"` suffix stripped). Two passes: immediate on each owned scene's `sceneLoaded`, plus a 3s-delayed sweep over all currently-loaded owned scenes (not just the scene that triggered the schedule — see "Bug I caught and fixed myself" below). Logs `[RebindShaders] Nx <name>` per shader and lists unresolved names, per O1. |
| `PerfectCullingSkip.cs` | §F.6. Prefix on `PerfectCullingCrossSceneSampler.InitializeAutoCulling` — skips the original body for our raid. **Async-Task caveat handled explicitly**: the target is `async Task`, so the prefix takes `ref Task __result` and sets `Task.CompletedTask` before returning `false` — a bare `return false` would hand the caller's `await` a null Task and NRE, turning the "cheap, harmless" mitigation into the exact crash it exists to prevent. |
| `HarmonyPatches.cs` | Central installer. Per the brief ("use AccessTools with null checks and log clearly if a target is missing"): each of the 3 targets is resolved via `AccessTools.Method` and null-checked individually, rather than an attribute-scanned `Harmony.PatchAll(assembly)` — one broken target (future game/SPT update) logs a specific error and disables only that one hook instead of risking the whole batch. |
| `SceneDiagnostics.cs` | §F.5 "scene load confirmation per scene name" — logs for our 6 scenes only. |

## Harmony target verification (as required by the brief)

All three read directly from `D:/SPT41/Development/_decompiled` this pass, not assumed from SPEC.md's
prose:

- `EFT.AssetsManager.BundlesManager.LoadBundleAsync(string, bool = true)` — instance method, confirmed
  not overloaded. `_bundles` (`Dictionary<string, AssetBundleReference>`) and the nested
  `AssetBundleReference` class are both `public` on this build (`BundlesManager.cs:14-25,113`).
- `EFT.BotsController.Init(IBotGame, IBotCreator, BotZone[], ISpawnSystem, BotLocationModifier, bool,
  bool, bool, bool, bool, IPlayersCollection, string, LocationSettings.Location.BotLocationEvents)` —
  instance method, synchronous `void`, confirmed not overloaded (`BotsController.cs:168`).
- `Koenigz.PerfectCulling.EFT.PerfectCullingCrossSceneSampler.InitializeAutoCulling(CancellationToken,
  IProgress<float> = null)` — **static**, `async Task`, confirmed not overloaded
  (`PerfectCullingCrossSceneSampler.cs:500`). Confirmed this type is compiled into `Assembly-CSharp.dll`
  itself (the entire `_decompiled` tree is one `Assembly-CSharp.csproj`) — no separate PerfectCulling DLL
  exists under `EscapeFromTarkov_Data/Managed`, so no extra reference was needed.
- Confirmed the catch-and-rethrow this hook exists to route around:
  `TarkovApplication.cs:2761-2770`, `catch (Exception) { UnityEngine.Debug.LogException(e); throw new
  Exception("Loading data file failed"); }` around the awaited call.

## Bug I caught and fixed myself

First draft of `ShaderRebind`'s sweep scheduling captured the *one* `Scene` value passed to
`OnSceneLoaded` at schedule time and only re-swept that scene 3s later. Since `Arena_Equator_Scripts`
loads first (`LoadSceneMode.Single`, §D) and is nearly empty (five components, no real geometry), the
sweep would have re-checked almost nothing — all the real renderers are in the 4 geometry/lighting/
TeamFight scenes that load *after* it. Fixed by having the sweep iterate every currently-loaded owned
scene (`SceneManager.GetSceneAt` + `ArenaEquatorRaid.IsOwnedScene`) instead of the one scene captured at
schedule time. Also dropped a `_seen`-material HashSet I'd initially added to avoid reprocessing — that
would have *blocked* the sweep from ever retrying a material whose target shader simply wasn't loaded yet
on the first pass, defeating the whole point of the second pass. The rebind is naturally idempotent
without it: a successful rebind changes the material's shader away from the `Hidden/ArenaMapPort/` prefix,
so a later pass skips it on the `StartsWith` check alone.

## Verification performed

- `dotnet build -c Release` and `-c Debug`: **0 warnings, 0 errors**, both configurations, from a clean
  `bin`/`obj` (deleted and rebuilt to confirm — not just an incremental no-op).
- First build attempt failed with `CS1069`/`CS0012` on `AssetBundle` — `UnityEngine.AssetBundleModule`
  wasn't in the reference list I copied from `ArenaMode.Client.csproj` (that project apparently never
  touches `AssetBundle` directly). Added the reference; rebuilt clean.
- Confirmed via `grep` that none of the three Harmony targets has a same-name overload (would make
  `AccessTools.Method(type, name)` ambiguous without explicit parameter types).
- Confirmed the `ArenaMapPortDistCopy` target only ever writes under this repo's own `dist/` — no
  `$(SPTPath)`/`D:/SPT41`/`X:/` write destination anywhere in the csproj (grepped the file; the only
  `D:/SPT41` reference is `$(EftManagedDir)`, read-only `HintPath`s for compiling against).
- Did **not** run the plugin in-game — that's §G.S4, explicitly a separate user-approved step (O4),
  and would currently be pointless anyway since 2 of 3 bundles + material-shaders.json don't exist yet
  (below).

## Known gaps — honest status

1. **`arena_equator_scenes.bundle` and `arena_equator_navmesh.bundle` do not exist yet** (WP2 has not
   produced them — `UnityProject/Assets/ArenaImport` is empty, `work/build/` only holds WP3's preset
   bundle). `BundleHost`/`NavMeshInstaller` will log a clear `[ArenaMapPort][Bundles]`/`[NavMesh]` error
   naming the missing file if a raid is attempted before WP2 delivers — this is by design (§B.WP4: "log
   clearly if a target is missing"), not a silent failure.
2. **`material-shaders.json` does not exist yet either** (WP2 output, §B.WP2.3/O1). `ShaderRebind` logs a
   warning at first load and simply skips the renderQueue/keyword-restore step — the shader identity
   rebind itself doesn't depend on the sidecar at all (derived from the stand-in shader's own name), so
   this alone would not block rendering once real materials exist, only their exact renderQueue/keyword
   fidelity.
3. **Added an `ArenaMapPortStageWpOutputs` MSBuild target** (csproj) that copies whatever exists under
   `work/build/**/*.bundle` (excluding `_donor_*.bundle`) and `work/build/**/material-shaders.json` into
   `dist/BepInEx/plugins/ArenaMapPort/bundles/` and the plugin root respectively, on every Release build —
   this is the "deploy step WP4 owns" §B.WP2.8 names explicitly. It already picked up WP3's
   `arena_equator_preset.bundle` correctly (confirmed present in `dist/` after the last build). **Caveat**:
   I don't yet know WP2's actual output filenames/subfolder (it hasn't run), so this is best-effort — if
   WP2 lands its bundles under a different name than `arena_equator_scenes.bundle`/
   `arena_equator_navmesh.bundle`, this target will still copy the *file* into `bundles/`, but
   `BundleHost`/`NavMeshInstaller` look for those exact names and will log "missing" until either the
   filenames line up or `ArenaMapPortStageWpOutputs` is taught to rename on copy. Flagging this as the one
   coordination point between WP2 and WP4 worth a quick sync, same category of risk as §B.WP3's `rcid`
   typo risk.
4. **§G.S4 (in-game test) not attempted** — correctly out of scope for this pass per O4/decision #9 (a
   separate, explicit, user-approved step), and there's nothing to test against yet per gap 1.

## Current `dist/` contents (this pass)

```
dist/BepInEx/plugins/ArenaMapPort/maschine-ArenaMapPort.Client.dll
dist/BepInEx/plugins/ArenaMapPort/bundles/arena_equator_preset.bundle   (staged from WP3's work/build/preset/)
```

Missing for a complete §G.S3 layout: `bundles/arena_equator_scenes.bundle`,
`bundles/arena_equator_navmesh.bundle`, `material-shaders.json` — all WP2 outputs, pending.

## Third-party attribution

Appended a WP4 section to `D:/ArenaMapPort/THIRD-PARTY-NOTICES.md` (which already had WP5's and WP3's
entries from concurrent work this session — appended, did not overwrite): SPT-InterchangeRework-main
(MIT, LennoxP90) for `NavMeshInstaller.cs`'s near-verbatim mechanism and `ShaderRebind.cs`'s structural
pattern (shader index, sweep-after-delay, sidecar JSON convention) — explicitly noted that the rebind
*algorithm* itself is original (O1's prefixed-name scheme replaces their error-shader/Substitutes-table
scheme, and is simpler because it doesn't need either). Confirmed the InterchangeRework repo's `LICENSE`
file directly (MIT, copyright LennoxP90) before citing it.

## For the next agent touching this (WP2, or a WP4 follow-up)

- Once WP2's scene/navmesh bundles and material-shaders.json exist under `work/build/`, just re-run
  `dotnet build -c Release` in `ArenaMapPort.Client/` — the staging target picks them up automatically
  (subject to gap 3's filename caveat above).
- The plugin has never been loaded in a live game process. §G.S4's checklist order (bundles → scenes →
  PerfectCulling → NavMesh → shaders → walk test) is exactly the order this plugin's own log lines will
  appear in `D:\SPT41\BepInEx\LogOutput.log`, by design, so triage should be straightforward once someone
  runs the promotion step.
