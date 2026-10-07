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
