# Equator fixed day/night starts and artificial sunlight — 2026-09-27

## Scope

Equator now offers exactly two fixed **start** times, 12:00 and 00:00, through the
normal raid selection and confirmation screens. Native time progression and weather
initialization continue after the raid starts. Bay5 and the other maps retain their
existing time behavior. This change does not alter optics; the user's seven custom
lighting/bloom/flare values were preserved.

Equator's two authored artificial-sun lights can now turn off or dim at night.
Ordinary lamps are excluded. No Arena asset or bundle rebuild is required.

## Implementation and source evidence

- `ArenaMapDescriptor` carries nullable `FixedDayHour` / `FixedNightHour`, computed
  `UsesFixedStartTimes`, and exact `SunScene` / `SunPaths` metadata. Only Equator is
  configured: 12 / 0, scene `Arena_Equator_Lighting`, paths `FAKE_SUN` and
  `FAKE_SUN_VOLUME`. `tools/maps/equator.json` mirrors these values.
- `FixedRaidTimeSelection` extends the native location time-selection allowlist for
  configured maps and prevents `SelectionLocationScreenController.UpdateSelectedDateTime`
  from resetting their selection. `LocationConditionsPanel.Set` retains the existing
  CURR/PAST toggle listeners and weather icon, stops its advancing menu timer, and
  displays the fixed times. The confirmation panel displays the selected variant.
  There is still one Equator location identity.
- `TimeOfDayRepairs` reads BaseLocalGame's already assigned `_selectedDateTime`, sets
  the selected fixed hour through the native resolver, and lets native `InitWeather`
  run. This keeps GameDateTime, sky, and weather initialization together.
- Server `FixedRaidTimeWeather` patches the installed
  `WeatherHelper.IsNightTime(DateTimeEnum, string)` signature. Only descriptor-backed
  fixed-time maps return CURR = day / PAST = night; other maps and invalid inputs
  retain the original helper. This covers both raid classification and bot night
  equipment, whose callers consult this helper separately.
- `ArenaLightingRepairs` applies a daylight factor only to the two exact authored
  sun paths. The factor follows the active native sky's `SunVisibility`, with the
  selected start hour as an initialization fallback. A small 0.5-second update tracks
  sky changes without repeatedly searching the scene. Updates also synchronize
  `CullingLightObject.float_1` and the volumetric light so native culling cannot restore
  the old daylight intensity.

## Configuration compatibility

- Existing `[TimeOfDay] Enabled` and `ArenaFixedHour` values are retained. They remain
  the legacy override for maps without fixed-time descriptors, including Bay5.
  Equator's selected 12:00 / 00:00 always wins, including when legacy `Enabled` is false.
- Hidden `FixedTimeSelectionRevision = 1` records a one-time explanatory migration
  log. It does not reseed the old time or lighting settings.
- Equator's new `NightSunMultiplier` defaults to **0** (off at night). **0.1** leaves
  10% at night; **1** restores the previous day/night-independent behavior. Existing
  daytime intensity multipliers retain their meanings. This option applies live
  while the map's lighting repairs are enabled.
- Flare, bloom, exposure, and the user's saved tuning are outside this change.

## Build and automated validation

- Release client/server builds passed. Client SHA-256:
  `79061772DAD2DD6EE482F534A28836CB192B3431D7A6B03245DC4A34EAA05629`.
- Server build and packaged DLL SHA-256:
  `CCC966B8DE135B870E3EE5DD1F5360B5D1D9A02849D8E15E5F0739AC682A0225`.
- Server tests: **395 passed, 0 failed, 0 skipped**. Evidence:
  `work/tests/fixed-raid-time/fixed-raid-time.trx`. Existing nullable warnings remain.
  The Python checks also passed; their count is not recorded here.
- New tests exercise the actual server helper and Harmony patch with a deliberately
  opposite native clock result. Equator changes to its selected variant; all 13 other
  Arena maps, vanilla examples, and unknown/invalid inputs preserve native behavior.
  Unpatching restores the native result. Descriptor/sidecar tests cover all 14 maps
  and verify the Equator-only hours and exact sun paths.

## Native validation on SPT-GPU-2

Client test run `dd071c9b` used the client hash above. Normal menu selection and the
00:00 confirmation screen worked. These are runtime observations, separate from the
build and automated tests.

| Time / log sequence | Observation |
|---|---|
| 22:10:40, #2854 | Rain night raid selected PAST and initialized at 00:00. |
| #2875–2876 | Default night factor produced zero targets for both authored sun lights. |
| #3024–3025 | Live `NightSunMultiplier = 0.1` produced targets 0.168 / 0.034. |
| #3116 | Next raid selected CURR and initialized at 12:00. |
| 22:17:54, #3267–3268 | Day targets/live intensities returned to 1.680 / 0.340. |
| #3029 versus #3269 | F7 ordinary-lamp values were unchanged between night and day. |
| 22:25:25, #3417–3418; #3456 | Clear daytime run after the user's 22:18 request; clear weather was confirmed again. |

Rain was the user's existing weather setting for the initial comparison. Clear weather
was selected only after the user's explicit request. The seven custom tuning values
remained preserved during these tests.

The first server restart still loaded the previous server build. The second restart
at **22:23:34** printed the new fixed-raid-times startup message. Read-only Syncthing
file availability also confirmed the current `CCC966...` server DLL on SPT-GPU-2.
No Syncthing configuration changes were made. The earlier restart alone is not evidence
that the server helper was active.

The known inactive-MenuScreen coroutine error remains; no new feature-specific error
was reported in the validation above.

### Pending final acceptance

**The final Clear-weather night raid is still pending at this recording point.**
Append its selected-time, sky/weather, and artificial-sun evidence here before claiming
the final Clear day/night comparison is complete. The completed Rain comparison and
Clear daytime observations above do not substitute for that remaining run.

### Final Clear comparison completed — 22:40–22:42

In the same `dd071c9b` process, the final normal-menu night raid retained the selected
00:00 through confirmation and actual initialization (#3911, 22:39:54.513).
Both weather applications were Clear (#3912/#3952); the independent runtime probe
read `activeFixedHour=0 liveHour=00` (#4039). Both fake-sun targets and live intensities
were zero (#3905/#3906). The preceding Clear-day raid read live hour 12 (#3544).
Screenshots of the same position (1.5, 0.4, 12), yaw 180, pitch -8 showed the daylight
roof illumination by day and its absence at night, while the floodlights and local
lamps remained visible. No error lines appeared after #3789 in the final run.

The first Clear-night attempt had correct timing/light readbacks (#3647–3669), but a
bot killed the player before the comparison screenshot. The normal menu resets bot
settings, so the final repeat explicitly unchecked bosses and selected AI amount None;
UiBridge confirmed zero living bots. The test profile was fully healed using the native
after-raid treatment (about 12.5k in-game roubles). The final run exited normally via
Equator North Gate. This is separate from the debug shortcut's protected disconnect.

Final host settings: Weather (test) = Clear, NightSunMultiplier = 0. Existing custom
AmbientIntensity 0.25, BloomIntensity 0.35, BloomThreshold 2.1, global light multiplier
0.7, IndoorReflectionStrength 0.9765258 and ReflectionIntensity 0.7934272 are retained;
DefaultsRevision stays 4. Debug Equator buttons still explicitly choose CURR (day);
use the normal map selector for the new night variant.

### Delivery verification — 22:43

The owned deploy helper completed for both `D:/SPT41` and `D:/SPT41vanilla` (86 files
each). Client and server SHA-256 match the hashes above across dist and both installs.
UiBridge reports VM build = GPU disk = loaded in `dd071c9b`, back at MainMenu,
HP 440/440, energy 96, hydration 97. The six user-tuned graphics values and revision
were read again and match the pre-change values exactly. The Vanilla test bed received
the verified files only; its server and game still need the user's restart.
