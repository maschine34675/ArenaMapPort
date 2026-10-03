# Equator Clear day and Bay5 native time/weather — 2026-10-03

This supersedes Bay5's fixed 12:00/00:00 policy in `map-time-weather.md`.
User request: Equator Day always Clear; Bay5 uses the ordinary two server-time
choices, an advancing day/night cycle and dynamic backend weather. Equator's
accepted midnight neon/rain behavior and existing saved tuning remain intact.

## Source evidence and implementation

- Installed EFT reference under `D:/SPT41/Development/_decompiled/`:
  `JsonType/LocationSettings.cs:426` and
  `EFT.UI.Matchmaker/MatchMakerSelectionLocationScreen.cs:75` contain independent
  whitelists for displaying time toggles and retaining the chosen CURR/PAST
  variant. Both maps still join those boundaries. Bay5 now has descriptor and
  sidecar `UsesServerTime` / `uses_server_time`, with no fixed hours.
- `EFT.UI.Matchmaker/LocationConditionsPanel.cs` owns the normal live clock,
  weather sprite and toggle callbacks. Bay5 keeps `_needTimerUpdate=true`,
  current session time and the second time 12 hours apart. Only Equator changes
  the labels to 12:00/00:00. Reusing a panel clears its old Equator weather
  context before native `Set`, including callbacks raised within `Set`.
- `EFT/BaseLocalGame.cs:489-517`: `InitWeather` uses `_backendDateTime.Calculate()`
  for CURR and adds 12 hours for PAST when no special resolver entry exists.
  It creates the raid `GameDateTime` with the backend `TimeFactor`, passes the
  same object to the world and sky, then initializes weather. Bay5 bypasses
  legacy `Enabled/ArenaFixedHour` regardless of their saved values and does
  not seed `_specialTimeResolver`. Its local `TimeAndWeatherSettings` disables
  random time/weather, so the selected server variant is respected. Saved
  configuration is never rewritten. Equator retains the special resolver.
- `EFT/LocalGame.cs:219-224` re-synchronizes the complete backend weather node
  sequence in PVE_OFFLINE after initial weather setup. The existing sink hook
  on `WeatherController.SetWeatherNodes` now retains that sequence unchanged
  on Bay5, including later calls and despite legacy `Weather (test)=Clear`.
- Equator Day replaces nodes with the existing Clear preset (rain 0, cloudness
  -0.5); midnight retains optional neon Rain. The selected variant determines
  this policy even as the raid clock advances. Menu preview and actual weather
  share `ModeForSelection`; midnight with neon rain disabled retains the saved
  test/backend fallback. Missing optional sprites retain native fallback state.
- `FixedRaidTimeWeather` already scopes the server bot-equipment override to
  descriptor fixed hours. Removing Bay5's fixed hours restores native
  `WeatherHelper.IsNightTime`: actual backend clock plus selected 12-hour offset,
  including its native night boundaries. Equator remains selection-based.

## Clock/sky audit

`EFT/GameDateTime.Calculate` advances real elapsed time using `TimeFactor` and
`TimeFactorMod`. `TOD_Time.FixedUpdate` advances when `LockCurrentTime=false`;
with a raid clock assigned its `AddSeconds` path uses
`GameDateTime.CalculateTaxonomyDate`. No ArenaMapPort writer to the clock factor,
`LockCurrentTime`, `DayLengthInMinutes` or `Cycle.Hour` exists.

`WeatherRig.ApplySkyboxMode` only hides dome renderer objects for the optional
FrozenStatic mode, leaving the time, lighting and weather components running.
The installed Bay5 setting is Dome; no sky/lighting configuration was changed.
Native acceptance must still inspect the donor's live lock and advancing sky
hour. The source audit alone is not an in-game day/night-cycle certificate.

## Configuration and diagnostics

No migration of existing user values. The existing internal revision stays 2.
The legacy fixed-hour controls now explain that neither released map uses
them and are hidden from F12 with `BrowsableAttribute.No`. Weather (test)
remains useful for Equator midnight when neon Rain is disabled and now explains
the two map-specific policies. The neon Rain description also reflects them.

F9 diagnostics report `serverTimes` and configured versus effective weather,
instead of incorrectly expecting replacement nodes whenever a saved test
setting is non-Server. No global or third-party mod configuration was edited.

## Offline validation

- Release client DLL-only build: **0 warnings, 0 errors**.
- Focused menu/runtime/server helper tests: **32 passed**.
- Full Release suite: **505 passed**, no failures or skips.
- `git diff --check` for the edited client/shared/test/sidecar scope: passed.

Meaningful coverage includes actual production Harmony hooks against narrow
native boundary doubles; native body execution and clock/world/sky assignment;
Bay5 CURR/PAST progression despite all old time settings; backend node array
identity across re-syncs for Clear/Rain/Fog/Overcast saved modes; Equator
Clear/Rain and disabled-neon fallback; recursion guard recovery after a thrown
replacement; Equator-to-Bay5-to-vanilla lifecycle; native timer progression,
Next/Back selection, single-time summary, hidden/missing sprites and map reuse.
The real SPT `WeatherHelper` is patched in a separate test: Bay5's native result
is preserved for both variants, while Equator's selection override still works.

Evidence under ignored `work/map-weather-restoration/`: `focused-tests.log`,
`full-tests.log`, `client-build.log`, plus the installed server helper decompile.
Client/server builds stage their own DLLs in dist; no bundles were rebuilt.
Deployment, GPU acceptance and exact loaded-DLL evidence are integration work
recorded separately below by the coordinating task.

## GPU-2 acceptance and deployment — 2026-10-03, 20:06–20:21 CEST

Parent review and independent re-verification passed all 505 Release tests,
the client DLL-only build and distribution validation (14 maps / 42 bundles /
14 shader sidecars). The sanctioned `-DllOnly -Confirm` path updated only the
client/server DLLs on D:/SPT41 and, after native acceptance, D:/SPT41vanilla.
Both installations' readback hashes match the staged build:

| Artifact | SHA256 |
|---|---|
| Client | `F60F1181589E992F6AE445477AD7501F6161A3D6352617DC8E96D15DDC585DE1` |
| Server | `D17E1A9D213C4526A23C938309D051FCFFFDD2D8472A99EFC86726250D07E040` |

GPU-2 server and client were restarted. UiBridge process `0126ddc9`, started
20:06:12, reported all 24 maschine plugins identical across VM, host disk and
loaded code both before and after testing. SPT 4.1.6 / EFT 0.16.9.5-40743,
1920x1080. Ordinary PMC raids used no bots; both ended by normal extraction,
with the test host returned to MainMenu.

### Bay5

- Native menu showed 09:54:23 / 21:54:23 and continued advancing. PAST stayed
  selected after Next/Back (09:59:45 / 21:59:45 on return).
- `20:10:37.168`: native server time/weather branch logged. Actual raid
  `StatedGameDateTime` was 22:14:07, matching the later selected PAST clock.
- `20:11:05.746`: `activeFixedHour=` empty, `fixedMenu=False`,
  `serverTimes=True`, `liveHour=22`. Saved Enabled=true / ArenaFixedHour=0
  remained intact and did not force midnight.
- `20:11:05.752`: configured Clear, effective Server. No Bay5 replacement
  weather nodes were applied. The live curve contained **54 cloudiness keys**,
  over a 25h15m range rather than the test override's two equal nodes / 10m.
- Read-only samples at 20:12:44 and 20:13:14 confirmed sky hour
  `22.498333 -> 22.5436115`, rain `0.29943803 -> 0.2343446`, cloudiness
  `0.2180894 -> -0.401614428`. GameDateTime TimeFactor=7, Locked=false;
  TOD_Time.LockCurrentTime=false. This proves live progression and dynamic
  interpolation, not observation of an entire 24-hour cycle.

### Equator

For a negative control, the existing Weather (test) setting was temporarily
changed through UiBridge from Clear to Rain. The native selection screen still
showed a sun for 12:00, and rain for 00:00. Selecting Day and starting the raid
produced `selected=CURR start=12:00` at 20:18:09.258. Clear was applied at
20:18:09.259 and again after SPT's weather re-sync at 20:18:14.464.

At 20:18:31.368 the diagnostic agreed: configured Rain, effective Clear.
Live curve readback at 20:19:42: Rain=0, Cloudiness=-0.5, Fog=0.001.
The daytime screenshot was inspected; midnight rain was checked in the menu
and in runtime policy tests, without an additional midnight raid this round.
The temporary test setting was restored to Clear after extraction.

No Error/Fatal entries occurred in the full captured client session. Evidence:
`work/update-weather-native/gpu2-log.json` (4,028 entries; four pre-existing long
entries truncated by bridge output limits), `bay5-sample{1,2}.json`,
`equator-day-sample.json`, and both deployment logs. Screenshot files:
`analysis/screenshots/map-weather-restoration-2026-10-03/01`–`07`.
Saved setting comparison and final delivery receipt are in the same work folder.
The final comparison found all **800 host / 804 vanilla** saved MapPort
assignments identical to their pre-task snapshots, including the restored Clear
test setting. Vanilla server/client restart remains the user's next step.
