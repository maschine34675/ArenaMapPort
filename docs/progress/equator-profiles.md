# Independent Equator day/night profiles — 2026-10-07

## Requested behavior and identity

Equator's existing 12:00 and 00:00 choices select independent visual profiles.
Day uses the previously accepted realistic defaults. Night starts with the
current GPU test installation's tuned appearance. SAIN receives Factory by day
and FactoryNight at night. The latter is the actual installed SAIN enum spelling.

Native Factory presents one tile with two times and swaps its internal location
IDs. Equator preserves its `arena_equator` runtime identity and uses explicit
profiles behind the same selection. This preserves ArenaMode admission,
DynamicMaps, scene ownership, bundles and the server's existing map registration.
Bay5 retains its native advancing server clocks and backend weather.

## Settings provenance

Read-only snapshots were taken before implementation from both installations.
The GPU mirror had 800 saved assignments; vanilla had 804. There were 104
differences across their combined keys. The GPU mirror contains the current
night tuning; vanilla predates several night features and is not its source.
Ignored evidence: `work/equator-profiles/settings-audit/`.

Of 90 Equator-specific keys, 43 appearance controls receive independent Day and
Night entries. Fourteen existing night-only controls keep their section and
saved values. Six audio controls and 26 repair/geometry/migration controls remain
shared. The remaining key is the legacy SAIN mapping, replaced by two profile
mappings while preserving its old saved value.

New visible sections are `Lighting (arena_equator Day/Night)`,
`LightingLab (arena_equator Day/Night)` and `Bots (arena_equator Day/Night)`.
The old appearance and SAIN entries remain saved but hidden. New entries use
their own saved values after first creation; switching raids never rewrites them.
Day defaults come from the accepted source defaults. Night defaults come from
the captured GPU recipe, rather than copying an arbitrary installation's legacy
values. F12 Reset therefore returns each entry to the appropriate recipe.

The 13 appearance differences between the recipes are:

| Control | Day | Night |
|---|---|---|
| ReflectionIntensity | 0.85 | 1 |
| AmbientIntensity | 0.5 | 4 |
| IndoorReflectionStrength | 0.95 | 1 |
| ReflectiveSpecularCap | 0.3 | 1 |
| BloomThreshold | 2.1 | 2 |
| AnamorphicFlareIntensity | 0.4 | 0.3 |
| AnamorphicFlareLength | 3.5 | 4.2 |
| AnamorphicFlareThreshold | 0.155 | 0.17 |
| AnamorphicFlareTint (RGBA hex) | ABDCFFBF | FF268FCC |
| ExposureCompensationEV | 0 | 0.3 |
| NativeMultiFlareIntensity | 0.5 | 0.6 |
| LightingLab / AmbientOverride | Off | Custom |
| LightingLab / AmbientCustomColor (RGBA hex) | 808080FF | 02131AFF |

Both retain BloomIntensity 0.35 and the accepted custom Filmic curve. Existing
night-only tuning is also the default for absent keys, including HazeStrength
0.2398122, SmokeDensity 1, FlickerFrequency 3 and DistanceHaze 0.01. Existing
saved controls still take precedence. Shared audio tuning is not reseeded.

## Lifecycle and isolation

The selected profile is captured from the authoritative raid settings before
`TarkovApplication.LoadMapAndData` loads scenes, then confirmed at InitWeather.
It does not depend on an advancing sky clock or the optional neon master switch.
Canceled/failed loads, scene unload and new non-Equator loads clear the selection.
Rendering/material state still has one owner per physical map, retaining existing
restoration paths. Live config subscriptions cover both profile entries and read
the active recipe when applying a change. HDR preset buttons resolve the section
being edited, so editing the inactive profile cannot target the active one.

SAIN remains an optional reflected integration. It receives the selected alias
without changing EFT's world location ID. If the Equator profile is unresolved,
the shim defers the lookup without setting SAIN's found flag. Source ordering
places InitWeather before BotsController.Init activates SAIN's location lookup.
Installed-build decompilation and source audit:
`work/equator-profiles/compat-audit/`.

## Verification

Implementation and native acceptance are in progress. Build, native evidence and
deployment results will be appended after verification.

## Completed acceptance — 2026-10-07, 10:25–10:43 CEST

The root independently re-ran both leaf checks and the full Release regression
suite, reviewed their source/diffs and passed distribution validation (14 maps,
42 bundles, 14 shader sidecars). The client compiles against the installed native
assemblies. New behavior coverage exercises every captured recipe, independent
saved values and events, HDR sibling selection, the actual Harmony loader prefix,
failure/cancellation/overlap cleanup, optional SAIN reflection and its found latch.
The host tests below complete the earlier pending native verification.

GPU-2 process `56d3c1e6`, started at 10:25:50, reported all 24 maschine plugins
identical across VM build, host disk and loaded assembly before and after testing.
SPT 4.1.6 / EFT 0.16.9.5-40743, 1920x1080. Three botless Equator raids followed
Day -> Night -> Day without restarting the process. The first two used the normal
PMC selection and extraction; the third used the Equator debug shortcut and
normal extraction. A final botless Bay5 debug raid ended by its protected
disconnect. GPU-2 is back at MainMenu, with no raid running.

### Selection, rendering and SAIN

| Raid | Early capture / confirmation | Native SAIN | Render evidence |
|---|---|---|---|
| Day | 10:28:54.624 / 10:29:14.965 | Factory at 10:29:26.934, also read from the live SAIN Location property | Bloom 2.1/0.35, streak 0.40/3.50, MultiFlare 0.50; uploaded exposure grey 0.14 and accepted Filmic vectors |
| Night | 10:33:11.362 / 10:33:15.379 | FactoryNight at 10:33:20.641, also read from the live SAIN Location property | Bloom 2/0.35, streak 0.30/4.20, MultiFlare 0.60; uploaded exposure grey 0.172361 (native 0.14 × 2^0.3) |
| Day repeat | 10:35:52.170 / 10:35:56.199 | Factory at 10:36:01.293 | Bloom 2.1/0.35, streak 0.40/3.50, native and uploaded grey both 0.140001; daytime appearance restored |
| Bay5 control | 10:40:26.324 native server-time/weather branch | Customs at 10:40:32.103 | EquatorProfile=None; Bloom 2.1/0.35, MultiFlare 0.50 and unchanged native HDR vectors |

Both Equator profiles uploaded `(7,0.35,0.7)` / `(-0.02,0.18,0)` tone vectors.
Their HDR transactions closed and restored the original component/global state.
Screenshots 01/02/03 use the same center position `(0,0.1,14)`, yaw 180, pitch -10;
all were inspected. Day is realistically lit, Night retains cyan/magenta light,
haze and wet-floor sheen, and the second Day returns to the first appearance.
This is targeted profile acceptance, not a new complete map visual audit.

### Live isolation and UI

- During Day, changing Night BloomIntensity 0.35 -> 0.6 left the actual
  UltimateBloom at 0.35 (10:30:47.307). Changing Day to 0.45 applied immediately
  (10:30:53.459). Both were restored to 0.35.
- During Night, disabling the optional neon master left the explicit profile
  `Night`, live SAIN `FactoryNight`, and the 0.3 EV / night flare settings intact.
  The neon switch was restored to true.
- F12 showed separate Day, Night and Bay5 HDR curve controls. Clicking **Use Filmic
  preset** in the Night section during the repeated Day raid changed only Night's
  two vectors to the native preset. Day and hidden legacy vectors stayed at the
  accepted custom values. Both Night vectors were restored through UiBridge;
  the search filter was cleared and F12 closed. Screenshot 04 and
  `native/hdr-preset-readback.json` record this test.
- The final config comparison found all 800 existing host assignments and all
  804 vanilla assignments unchanged. Host has 888 assignments: precisely 86
  appearance entries plus two SAIN entries were added. All 88 match their intended
  defaults. A native snapshot independently captured 178 Equator entries,
  including hidden legacy entries. Vanilla creates its new entries on next start.

### Delivery and evidence limits

Sanctioned DLL-only deploys updated both D:/SPT41 and D:/SPT41vanilla after reviewed
dry-run plans. Both installations' readback hashes equal the staged outputs:

| Artifact | SHA256 |
|---|---|
| Client | `FB2E8D6D1B5F2B0D12C0D398CFE75E16E704B01BF3853B2CD013F9CB518E0E97` |
| Server | `85C4E852AC497D84107464944A6BFAE82ABC6B52E8A3E3E6EAE450C19EE987EC` |

No bundles, placements or game data were rebuilt/deployed. The server has no
source behavior changes in this task. Vanilla was not launched; its client/server
need a restart to load the delivered DLLs. SAIN absence is covered by the optional
integration boundary, not a fresh native vanilla raid. Fika was not tested.

Ignored evidence is under `work/equator-profiles/native/`; screenshots are under
`analysis/screenshots/equator-profiles-2026-10-07/`. The exported log contains
5,000 entries (#209–5208); 208 early startup entries had left the ring and six long
entries were already truncated. An earlier error query covered startup and was
empty. The later export includes the existing compressed-texture/duplicate-LOD
warnings and one `MenuScreen` inactive-coroutine error when opening Bay5's pause
menu (10:42:00.706). That same error was previously recorded in
`native-dust.md` and `equator-night-atmosphere.md`. No profile selection mismatch,
SAIN unknown-location error or new profile/render exception was observed.

## Regression: hideout loading (2026-10-07 .. 2026-10-14), fixed 2026-10-14

**Symptom (user, 2026-10-14):** with the mod installed, HIDEOUT from the main menu shows the loading spinner
forever; without the mod it opens. Reproduced on GPU-2 (14:29-14:31): `HideoutGameWorld` created, main player null,
`screen=None` for minutes at 60 fps, no `[ArenaMapPort]` line and no warning in BepInEx's log, all five
`/client/hideout/*` requests answered by the server before the click. EFT's own `Logs/log_2026.10.14_12-28-59_*/
*errors.log` (and `traces.log`) carries the cause:

```
EXCEPTION: System.ArgumentException: Field _selectedDateTime defined on type EFT.BaseLocalGame`1[EFT.EftGamePlayerOwner]
is not a field on the target object which is of type EFT.HideoutGame.
  at System.Reflection.RuntimeFieldInfo.GetValue (System.Object obj)
  at ArenaMapPort.Client.TimeOfDayRepairs.Prefix (EFT.LocalGame __instance, EFT.TimeAndWeatherSettings& timeAndWeather)
  at (wrapper dynamic-method) EFT.BaseLocalGame`1[EFT.EftGamePlayerOwner]::InitWeather
  at EFT.BaseLocalGame`1[TPlayerOwner].Create[T] (...)
  at EFT.HideoutGame.smethod_6 (...)
  at EFT.TarkovApplication+HideoutController.method_7 / method_0 / HideoutSelectedHandler
```

The same signature is in the user's own client log of 14:15 (MASCHINE11): hideout requests, the hideout scene's
LODGroup warnings, `RespawnApiProbe evt=World`, then silence until EFT was closed.

**Mechanism.** `HarmonyPatches.InstallTimeOfDayFix` detours `BaseLocalGame<EftGamePlayerOwner>.InitWeather`.
`HideoutGame` is `BaseLocalGame<HideoutPlayerOwner>` (EFT/HideoutGame.cs:20); Mono compiles one body for every
reference-type instantiation of a generic class, so the detour fires for the hideout too, with the `HideoutGame` as
`__instance` (the `LocalGame __instance` parameter type is not checked by Harmony's generated stub). The three
reflected fields (`_specialTimeResolver`, `_backendDateTime`, `_selectedDateTime`) belong to the closed
EftGamePlayerOwner type. Until `04be98c` (2026-10-07) the prefix returned for every non-`arena_` location before
touching them; that commit added `EquatorProfiles.ConfirmSelection(location?.Id, (EDateTime)
SelectedDateTimeField.GetValue(__instance))` ABOVE the gate, so every hideout visit threw. `BaseLocalGame.Create<T>`
calls `InitWeather` unconditionally (BaseLocalGame.cs:443); `HideoutController.method_0` runs with
`.HandleExceptions()` (logs to EFT's errors.log only) and `LoadComplete` is never reached, while
`HideoutLoadingScreen` keeps eating input. `04be98c` is in v0.1.0, `main` and `feature/airpit`; the last documented
hideout visits before it are 2026-09-28/29 (lighting-followup.md, hdr-controls.md). No config switch helps: the
failing call precedes every `Enabled` check.

**Fix (`feature/airpit`, for 0.2.0; user decision 2026-10-14: no 0.1.1 hotfix, 0.1.0 is not published on Forge):**

- `TimeOfDayRepairs.Prefix(object __instance, ref TimeAndWeatherSettings)`: `if (!(__instance is
  BaseLocalGame<EftGamePlayerOwner> game)) return;` before any reflection - the hideout and any unknown owner type are
  fully inert, as before `04be98c` (the review's first version cleared the Equator profile there, which would race
  `LoadMapAndData`: its `BeginLoad` runs before the hideout is unloaded); `LocalGame` and Fika's `CoopGame` derive from
  the closed type and keep the full behaviour. The former body moved unchanged into `Apply(game, ...)`; the prefix
  snapshots the settings, runs `Apply` in `try/catch`, and on a failure restores the settings it was called with,
  clears `ActiveFixedHour` and logs `[ArenaMapPort][TimeOfDay] InitWeather prefix failed ...` at Error, so the native
  body runs as if the repair did not exist.
- `HarmonyPatches.InstallTimeOfDayFix` comment and the class doc comment carry the TRAP; AGENTS.md has it as a trap
  ("Harmony on generic classes").
- Tests (`MapWeatherRuntimeTests`; the first version was reviewed by workflow `wf_502f7395-11f`, which showed the
  hideout test never reached the shared detour and the vanilla test was vacuous, and reproduced the game's failure
  on CoreCLR once the fake had the real shape): the fake `BaseLocalGame<T>` now carries `Location`, `InitWeather` and
  its state like the real class (BaseLocalGame.cs:360/489), `LocalGame`, Fika-shaped `CoopGame` and
  `HideoutGame : BaseLocalGame<HideoutPlayerOwner>` only close it, and the test constructor patches the production
  target `BaseLocalGame<EftGamePlayerOwner>.InitWeather`. `HideoutGameRunsTheSharedDetourUntouched` guards the
  failure precondition (`ArgumentException` from the production FieldInfo on the double), then runs the hideout's
  `InitWeather` THROUGH the detour with an `arena_equator` location and a PAST selection: native body runs once,
  settings untouched, resolver empty, the in-flight Equator Night selection survives, no Error logged; against the
  old prefix this fails with the game's message. `AnotherSubclassOfTheClosedTypeGetsTheFullRepair` (CoopGame),
  `VanillaLocalGameClearsAStaleEquatorProfileAndKeepsRandomTime` (seeded profile) and
  `APrefixFailureLogsOnceAndHandsInitWeatherTheOriginalSettings` (an infinite backend time factor makes `Apply`
  throw after it wrote the ref struct) cover the gate's width, the vanilla reset and the catch path. 743 .NET tests
  green; client Release build 0 warnings.

**Hardening from the same review (user request 2026-10-14, "fix the two side findings too"):**

- `HdrImageTuning.Release()` walked `while (_active != null) FinishRender(_active)`; `FinishRender` returns at once for
  a scope that already finished and advances `_active` only when the scope it finishes IS `_active`, so a scope finished
  out of order (outer before inner) would have left `_active` on a finished scope and spun the main thread at the next
  camera change outside a raid (menu, hideout). Now a bounded walk (64) that steps past such a scope itself and logs a
  Warning if anything is left. Not the cause of the hideout report (no scope exists outside a configured map), a latent
  hang.
- `SpatialAudioOcclusionGuard.Prefix` / `SpatialAudioInteractiveOcclusionGuard.Prefix` skipped the native
  `ProcessSourceOcclusion` / `ProcessInteractiveObjectOcclusion` bodies wherever `SpatialAudioSystem.Initialized` was
  false, on every map, in the menu and in the hideout (ManimalIcebreaker's upstream choice, documented as benign). They
  are now gated with `ArenaMapRaid.IsLoaded()` like every other hook: outside a ported raid the native body always runs,
  inside one the `Initialized` branch is unchanged. The finalizers were map-gated already.

**Host verification (GPU-2, 2026-10-14):**

- 14:29-14:31, build `41a4c0f` (before the fix): HIDEOUT click -> `screen=None` for 2+ minutes at 60 fps,
  `Raid: no (HideoutGameWorld)`, EFT `errors.log` with the ArgumentException above; `uibridge_quit` to recover.
- 14:59-15:04, fix deployed (`deploy-to-spt -Confirm`, `gpu-run deploy-wait --server --game --force`, 20/20 plugins
  VM build = host disk = loaded): HIDEOUT -> `screen=Hideout` after **7.5 s**, the area overview renders (screenshot
  `work/screenshots/hideout-2026-10-14/01-hideout-opens-with-fix.png`), EFT wrote **no errors.log at all** for the
  session up to the raid, no `[ArenaMapPort]` warning, Escape back to MainMenu. Equator PMC day raid (bots None):
  `[Profiles] arena_equator: selected Day ... (generation 2)`, `[Profiles] arena_equator: confirmed Day at InitWeather;
  recipe locked for this raid.`, `[TimeOfDay] arena_equator: selected=CURR start=12:00 fixedMenu=True; native weather
  initialization retained.` - identical to the pre-fix raids; left via `gpu-run leave`. AirPit PMC raid: `[TimeOfDay]
  arena_airpit: native server time and weather; legacy fixed-hour override bypassed, native weather initialization
  retained.` The session's errors.log afterwards holds only EFT's usual ported-map noise (Invalid windSettings,
  WeatherObstacle.Instance is null, SpawnPointMarker counts, JetMaterial, seasons singletons), nothing from
  ArenaMapPort.
- 15:09-15:12, hardened build (bounded HDR release, map-gated occlusion prefixes): HIDEOUT -> `screen=Hideout` after
  8.1 s, no errors.log, no warnings; Equator day raid with the same three Profiles/TimeOfDay lines, left to the main
  menu (the camera change outside the raid runs the new `Release()` walk over the raid's scope chain: no
  "render scope chain" warning, 60 fps in the menu). Host left in MainMenu.
- 15:20-15:23, final build after the review follow-ups (inert hideout branch, settings restore in the catch, operand
  order in the occlusion prefixes; 743 .NET tests): HIDEOUT -> `screen=Hideout` after 8.1 s, no errors.log, no
  warnings, Escape to MainMenu; Equator PMC **night** raid (`--time past`, bots None): `[Profiles] arena_equator:
  selected Night ... (generation 1)`, `[Profiles] arena_equator: confirmed Night at InitWeather; recipe locked for this
  raid.`, `[TimeOfDay] arena_equator: selected=PAST start=00:00 fixedMenu=True; native weather initialization
  retained.`; left via `gpu-run leave`. Host left in MainMenu.
