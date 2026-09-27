# PLAN-TWO-MAPS item 10b — loading-screen art for ported maps (Equator + Bay5)

Status: SOURCE-VERIFIED, offline-built and unit-tested (2026-09-27). Not yet host-tested — this
pass had no `uibridge*`/`windows-gpu*` access (main-session-only per this task's own hard rules);
see "Host check" at the end for what the next SPT-GPU-2 pass should do.

Trigger: user feedback in chat — the atrium light-culling fix holds (lamps stay lit, no glare;
compared against the user's own early screenshot `D:\SPT41\Development\Screenshot-2026-09-19-092831.png`
from the first playable version) — plus a request to also carry over the two maps' own loading
screens, referencing art already exported into this session's scratchpad
(`…\scratchpad\loadscreen\export_allscreens\AllScreens_Loadingscreen-{Equator,Bay5}.png`, produced by
this session's own research pass). The screenshot documents lighting feedback, not a loading screen,
and needed no code change; only the loading-screen request is this file's subject.

## What vanilla does, what the ported maps lack

EFT 0.16.9.5's own "Deploying in progress (NN%)" deploy screen
(`EFT.UI.Matchmaker.MatchmakerTimeHasCome.Show(IEftSession, RaidSettings, MatchmakerPlayersController)`,
decompile `EFT.UI.Matchmaker/MatchmakerTimeHasCome.cs:209`) is shown **before** any raid scene or
bundle loads and stays up through the whole load
(`TarkovApplication.LocalGameMatching` → `RunFilesChecking` → `LoadMapAndData` →
`LoadBundlesAndCreatePools`, whose progress reporting drives the same `_deployingText` this screen
shows — confirmed in the decompile). At the exact call into `Show`, `raidSettings.SelectedLocation`/
`LocationId` are already populated (`RaidSettings.cs:74-85`, the `LocationId` setter stays in sync
with `SelectedLocation.Id`) — but the screen paints no location-specific image anywhere:
`LocationSettings.Location.Preview` (`JsonType/LocationSettings.cs`) is a declared field nothing in
this path (or `tools/server/make_base_json.py`) ever reads or populates. So every ArenaMapPort raid
has always deployed behind whichever generic background EFT's own client ships by default — nothing
Arena-specific, unlike PLAN-TWO-MAPS item 10's separate `Banners[]`/`MatchmakerBannersPanel`
mechanism (a small carousel strip on this SAME screen, server-side, already shipped — see
`docs/progress/two-maps-item-10-banners.md`). The two are independent and coexist: banner strip
small, this repair's overlay full background.

The other candidate screen researched this session, `PreloaderUI.ShowRaidStartInfo` →
`RaidStartIntro.ShowIntro` (`EftBattleUIScreen.cs:140`, the "Glitch_BG_Loop" screen PLAN-TWO-MAPS
item 10 already found), fires **after** spawn/BattleUI is already showing, is ~8s and text-only, and
its background object could not be located in any classic `.assets` file this session could scan
with UnityPy (streamed from a runtime asset bundle) — scoped out of this pass per that research;
`ShowRaidStartInfo` is used here only as the primary HIDE trigger (see below), not as a second
show surface.

Arena ships one purpose-built, already-vignetted (dark corners baked in) loading image per playable
location in its own `resources.assets`: `AllScreens_Loadingscreen-Equator` (path_id 1558) and
`AllScreens_Loadingscreen-Bay5` (path_id 804), both 1920×1080 `Texture2D` (DXT5/BC3) — re-verified
this pass with a live UnityPy read against
`D:\Battlestate Games\Escape from Tarkov Arena\EscapeFromTarkovArena_Data\resources.assets`
(read-only): both objects really are `Texture2D`, really carry that exact name, and really decode to
1920×1080. A third object, `AllScreens_Loadingscreen-Bay5_NY` (path_id 791, `RGB24`), is an unwanted
Christmas/New-Year reskin of the Bay5 shot and is intentionally excluded. (Arena's wider,
non-vignetted `Arena_MainMenu_Paralax_<Map>-1..4` layers, 2048×1024, were also compared this
session — no baked text, but designed as scrolling menu-parallax layers rather than a single hero
shot, and needing a 2:1→16:9 crop that throws away more of the image; not used here, but the
naming scheme below leaves room for them later without any code change — see "What was built".)

## What was built

**`tools/extract/extract_loadscreens.py`** (new, mirrors `extract_banners.py`'s own structure and
conventions). Reads the two `Texture2D` objects above with UnityPy, verifies each one's own
`m_Name` against the expected `path_id` (path_id drift would otherwise silently point at the wrong
Arena asset) and its decoded size against 1920×1080, then writes `arena_<key>_<n>.jpg` (default
`--ext jpg`, quality 95; `--ext png` also supported), `n` 1-based, straight into the **client**
plugin's deployed folder: `dist/BepInEx/plugins/ArenaMapPort/loadscreens/` — a sibling of that same
folder's `bundles/`, exactly the directory `ArenaMapPort.Client.csproj`'s existing
`ArenaMapPortDistCopy` target already copies the plugin DLL into, so `LoadingScreenArt.cs`'s own
`PluginDir`-relative lookup finds it with no csproj change. Idempotent (re-running overwrites the
same files with the same source data) and BSG art never touches git (`dist/` is gitignored; a
`--output` override should point at `work/`, never a tracked path). Fails loudly
(`HardExtractError`) on a missing install, wrong object type, name mismatch at the expected
path_id, or a decoded size other than 1920×1080.

Naming is deliberately numbered per map (`arena_equator_1.jpg`, `arena_bay5_1.jpg` today) rather
than a single fixed filename: the client (see below) probes `_1`, `_2`, ... contiguously and stops
at the first missing index, so a second/third variety image later (one of the Parallax layers, or
a future extra `AllScreens`-style shot) is a drop-in — no client rebuild, no naming change, just an
extra file next to `_1`.

```
$ python tools/extract/extract_loadscreens.py
OK: arena_bay5[1] -> D:\ArenaMapPort\dist\BepInEx\plugins\ArenaMapPort\loadscreens\arena_bay5_1.jpg (795062 bytes)
OK: arena_equator[1] -> D:\ArenaMapPort\dist\BepInEx\plugins\ArenaMapPort\loadscreens\arena_equator_1.jpg (850516 bytes)
```

**`ArenaMapPort.Client/LoadingScreenArt.cs`** (new). A plugin-owned, `DontDestroyOnLoad` overlay
(`Canvas` + `Image` + `AspectRatioFitter`, `AspectMode.EnvelopeParent` — Unity's own "cover"/aspect-fill
fit) built once and toggled by four entry points, all gated so a vanilla (non-Arena) raid's
`LocationId` never matches any `ArenaMapDescriptor.Key` and is therefore a complete no-op:

- **SHOW (primary)** — Harmony postfix on
  `MatchmakerTimeHasCome.Show(IEftSession, RaidSettings, MatchmakerPlayersController)`. Resolves the
  map purely from `raidSettings.LocationId` against `ArenaMapRegistry.All`'s own `Key` — no scene is
  loaded yet at this point, so `ArenaMapRaid.CurrentMap` is not usable here.
- **SHOW (failsafe)** — a plain `SceneManager.sceneLoaded` subscription (this plugin's own
  established pattern, see `ArenaMapRaid`'s doc comment for the other subscribers): if the debug
  instant-start flow (`maschine-ArenaMapDebug.dll`) turns out to skip past the deploy screen
  entirely, showing on the raid's own Scripts scene load still covers whatever load time remains.
  Idempotent no-op if the primary hook already showed this map's art.
- **HIDE (primary)** — Harmony postfix on
  `EFT.UI.EftBattleUIScreen.RaidBattleUIScreenController.ShowRaidStartInfo()` — fires exactly once
  per raid, right as the player's own BattleUI first shows (spawn already done).
- **HIDE (failsafe #1)** — Harmony prefix on `TarkovApplication.ComebackToMainMenu()` —
  unconditional, fires on every abort/error/return-to-menu path in that method; a cheap no-op when
  the overlay is not visible.
- **HIDE (failsafe #2)** — a plain `SceneManager.sceneUnloaded` subscription on any of our maps'
  Scripts scenes, independent of both Harmony hide hooks above ever being silently disabled by a
  future EFT update (`HarmonyPatches.Verify`'s log-and-skip) — belt-and-braces against the overlay
  getting stuck over a live main menu forever instead of just never showing.

Textures load lazily from `Path.Combine(PluginDir, "loadscreens", "arena_<key>_<n>.jpg")` (same
`PluginDir` convention as `ShaderRebind.cs`/`BundleHost.cs`/`NavMeshInstaller.cs`) via
`File.ReadAllBytes` + `ImageConversion.LoadImage` — plugin-local files, never a `UnityWebRequest`
fetch — decoded once per map and cached forever; a raid load picks uniformly at random among
whichever indices exist on disk (today exactly one per map). Every entry point is wrapped in
try/catch so a failure here can never throw into EFT code, only log and skip.

New config key: `[LoadingScreen] ShowArenaArt` (default `true`, "takes effect at the next raid
load") — a new key, per AGENTS.md's rule against silently repurposing an existing one.

**Why `AccessTools.TypeByName`, not `typeof(...)`, for the three EFT.UI/EFT Harmony targets.** This
is the first menu/UI Harmony target this plugin has ever patched — every existing repair edits
already-loaded raid scene geometry. `MatchmakerTimeHasCome`, `EftBattleUIScreen` and their nested
controller types all ultimately derive from Odin's `SerializedMonoBehaviour`
(`EFT.UI.Screens.UIScreen` → `UIInputNode` → `InputNode` → `InputNodeAbstract` →
`SerializedMonoBehaviour`), which lives in `Sirenix.Serialization` — an assembly this csproj does
not (and should not) reference. A compile-time `typeof(EftBattleUIScreen.RaidBattleUIScreenController)`
failed the build with `CS0012` (`SerializedMonoBehaviour` in an unreferenced assembly); resolving
each declaring type at runtime via `AccessTools.TypeByName("...")` (a string, reflection-only —
the same mechanism `SainLocationShim.cs` already uses for SAIN's soft-dependency type) sidesteps
this, because the live game process already has `Sirenix.Serialization.dll` loaded. `TarkovApplication`
was included in the same fix for consistency even though it did not itself trigger `CS0012`.
`RaidSettings`, `IEftSession` and `MatchmakerPlayersController` stayed plain `typeof(...)` — none of
them derive from that chain (`RaidSettings : IRaidSettings<RaidSettings>`,
`MatchmakerPlayersController : BaseMatchmakerController<RaidSettings> : IMatchmakerController`, both
plain non-`MonoBehaviour` classes/interfaces).

**`ArenaMapPort.Client.csproj`** (edited). Added the `UnityEngine.ImageConversionModule` reference
(same `HintPath`/`Private=false` convention as the ~20 existing entries) — needed for
`ImageConversion.LoadImage`. `UnityWebRequestTextureModule` was deliberately not added: the art
ships as plugin-local files, never fetched over HTTP.

**`ArenaMapPort.Client/HarmonyPatches.cs`** (edited). One line, `LoadingScreenArt.Install(harmony);`,
added at the end of `InstallAll`.

**`tools/tests/test_loadscreens.py`** (new, mirrors `test_banners.py`). Unity-independent: the
`LOADSCREENS` table (map key → asset name/path_id, and that the unwanted NY variant is excluded),
`output_filename`/`output_path` (default index 1, out-of-range/zero index raises, unknown map key
raises), `validate_dimensions`, and `--check`'s own logic (empty directory, zero-byte file still
counts as missing, both present → nothing missing) — no BSG data in any of these. One additional
real-source test (skipped automatically when the Arena install is unreachable, same idiom
`test_banners.py`/`test_navmesh_modifiers.py` already use) decodes both objects for real and checks
they land on disk with non-zero size.

## Verification run (2026-09-27, no host access this pass)

```
$ python tools/extract/extract_loadscreens.py
OK: arena_bay5[1] -> D:\ArenaMapPort\dist\BepInEx\plugins\ArenaMapPort\loadscreens\arena_bay5_1.jpg (795062 bytes)
OK: arena_equator[1] -> D:\ArenaMapPort\dist\BepInEx\plugins\ArenaMapPort\loadscreens\arena_equator_1.jpg (850516 bytes)

$ python tools/extract/extract_loadscreens.py --check
OK: 2 loadscreen(s) present under D:\ArenaMapPort\dist\BepInEx\plugins\ArenaMapPort\loadscreens

$ python -m unittest discover -s tools/tests -p "test_*.py"
Ran 386 tests in ~13s
OK (skipped=28)
# includes tools/tests/test_loadscreens.py's own 20 cases (19 offline + 1 real-source, not skipped
# on this machine since the Arena install is present).

$ dotnet build ArenaMapPort.Client/ArenaMapPort.Client.csproj -c Release -p:ArenaMapPortDllOnly=true
Der Buildvorgang wurde erfolgreich ausgeführt. 0 Warnung(en), 0 Fehler.
# stages the new client DLL (with LoadingScreenArt.cs compiled in) into dist/; -p:ArenaMapPortDllOnly=true
# skips re-staging bundles (unrelated to this change).
```

`ArenaMapPort.Server`/`ArenaMapPort.Server.Tests` and the Unity import pipeline are untouched by
this change (client + Python only, per this task's own file scope) — not rebuilt/retested this pass.

## Host check the next SPT-GPU-2 pass should do

1. Deploy: this pass already built the client DLL and staged the two loadscreen JPEGs into
   `dist/BepInEx/plugins/ArenaMapPort/loadscreens/`; run
   `pwsh -NoProfile -File tools/deploy-to-spt.ps1 -SptRoot 'D:\SPT41' -Confirm` (not run this pass —
   out of this subagent's scope) and restart EFT (client DLL changed).
2. Start an Equator raid **through the normal menu flow** (map/side select → confirm), not the
   `EQUATOR DEBUG` button first — this exercises the primary `MatchmakerTimeHasCome.Show` hook.
   Screenshot the "Deploying in progress (NN%)" screen: expect Arena's own Equator atrium/"EQUATOR"
   art filling the background behind the player model/banner strip/percentage text, not EFT's
   default. `uibridge_log` filtered on `[ArenaMapPort][LoadingScreen]`: expect one
   `installed (ShowArenaArt=True)` line at startup and one `show arena_equator (1920x1080)` line
   per raid start.
3. Let the raid load through to spawn: expect one `hide (ShowRaidStartInfo)` line at or just before
   BattleUI appears, and the overlay gone from screen (confirm via screenshot) — no residual image
   over gameplay.
4. Repeat with Bay5 through the normal menu flow; expect `arena_bay5` art and the same show/hide
   pair.
5. Then start a raid via **EQUATOR DEBUG** / **BAY5 DEBUG** (the biggest open question from this
   session's research: whether the debug mod calls through `TarkovApplication.LocalGameMatching`,
   so the primary hook still fires, or skips straight to loading, so only the `sceneLoaded` failsafe
   shows it — and later than ideal). Check the log for either `show arena_<key>` line and note which
   path actually fired.
6. Abort a raid early (disconnect) and confirm a `hide (...)` line appears and no overlay is stuck
   over the main menu afterward; also confirm a plain vanilla SPT raid (any non-Arena map) never
   logs an `[ArenaMapPort][LoadingScreen]` show/hide line at all.
7. Append the dated host-test result (log lines + screenshots) to this file and add it to
   `docs/progress/INDEX.md` (both left to the next pass with host access, per this task's file
   scope).

## Doubts / open questions for the main session

- Canvas `sortingOrder = 100` for the overlay is an untested guess (documented as such in
  `LoadingScreenArt.cs`'s own comment) — `MatchmakerTimeHasCome`'s real Canvas/sortingOrder
  structure could not be found in any classic `.assets` file this session could scan with UnityPy
  (its prefab is streamed from a runtime asset bundle); step 2 above's screenshot is the actual
  test. If the art ends up drawing OVER the "Deploying NN%" text/banner strip instead of behind it,
  lower `sortingOrder` (or move the whole overlay to a lower value) rather than reworking the
  hook — the show/hide wiring itself does not depend on the exact number.
- Whether the debug-start button (`maschine-ArenaMapDebug.dll`) calls through
  `MatchmakerTimeHasCome.Show` at all is unverified (decompile-free per this session's own scope);
  step 5 above answers it. If it never fires the primary hook, the art will only appear from
  Scripts-scene load onward — still an improvement over never, but shorter than the full deploy
  screen's duration; worth deciding whether that is good enough or the debug mod itself needs a
  look (out of this file's scope either way — `AGENTS.md` lists it as the user's own other tooling,
  read-only where touched at all).
- The unrelated lamp/glare feedback in the triggering chat message
  (`D:\SPT41\Development\Screenshot-2026-09-19-092831.png`, confirming the atrium floodlights fix
  from item 7 holds and shows no glare) needed no action and none was taken here — flagged back to
  the main session rather than acted on, per this task's own file scope (this doc/these files only).

## Host test 2026-09-27 (SPT-GPU-2) — VERIFIED after two fixes

Five images per map are staged (`arena_<key>_1.jpg` = Arena's `AllScreens_Loadingscreen-<Map>` 1920x1080,
`_2.._5` = its four `Arena_MainMenu_Paralax_<Map>-1..4` main-menu backgrounds, 2048x1024); a raid load picks one
at random, never the one shown last time.

1. **Hide came too late.** First run (build of 17:20): Equator `show` 17:24:43, `hide (ShowRaidStartInfo)` 17:25:15,
   about 14 s after the raid timer started; on Bay5 a screenshot after `raidStarted=true` still showed the art over
   the raid. Fix: the SHOW postfix subscribes to the deploy screen controller's `OnClose` event (reflection;
   `ScreenManager.ScreenController.Closed` setter), which EFT fires when FinalCountdown/BattleUI replace the screen.
   `ShowRaidStartInfo` stays as a late fallback. Host 17:40 (instance 660b5523): Equator `show` 17:40:19.759,
   `hide (deploy screen closed)` 17:40:50.937, screenshot at timer 0:14:58 shows the raid; Bay5 `show` 17:44:59.510,
   `hide (deploy screen closed)` 17:45:36.252, screenshot at 0:14:57 clean. Normal menu flow (ESCAPE FROM TARKOV →
   PMC → ARENA: EQUATOR → NEXT → NEXT → READY): `show` 17:58:29.730, `hide (deploy screen closed)` 17:58:41.472.
2. **The art covered EFT's deploy UI.** A mid-load screenshot showed only the image: no "DEPLOYING TO LOCATION"
   caption, no location name, no "Loading map... (NN%)" line, no BACK button (the overlay canvas used
   `sortingOrder = 100`, the doubt noted above). Fix: the art is now the first child of the
   `MatchmakerTimeHasCome` screen itself (full-rect Image, envelope fit, `raycastTarget = false`); the plugin
   overlay canvas remains only for the sceneLoaded failsafe. The first show per EFT process logs the screen layout:
   `deploy screen 'Matchmaker Time Has Come' rect=(1920.00, 1080.00) children: CaptionsHolder, Logo, Banner Panel,
   Location Name Panel, PlayerModelView, PartyInfoPanel, Back Button Panel, Deploying Caption, Loader` (EFT's dark
   blurred background is not a child of the screen, so the art replaces exactly that). Host 18:07 (instance
   b63696f2): `show arena_equator (2048x1024, arena_equator_2.jpg, behind the deploy screen's own UI)` 18:07:20.655,
   screenshot at "Loading data files... (66%)" shows caption, location name, key hints, player model, progress
   line and BACK over the art; `hide (deploy screen closed)` 18:07:52.914, raid screenshot clean. Second raid in the
   same process: `show arena_bay5 (2048x1024, arena_bay5_5.jpg, behind the deploy screen's own UI)` 18:08:26.859,
   same layering, `hide (deploy screen closed)` 18:09:03.580. No LoadingScreen warnings or errors.

The debug buttons go through `MatchmakerTimeHasCome.Show` (the `hide (deploy screen closed)` line only exists on
that path), so the sceneLoaded failsafe never had to show the art in these runs. White key-hint text is harder to
read on the bright Bay5 sky than on EFT's dark default background; dimming the art is a possible follow-up.
