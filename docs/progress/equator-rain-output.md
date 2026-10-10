# Indoor rain output follow-up — 2026-10-09

## User report and corrected diagnosis

After `f75b9f1`, the user still heard no Equator rain after a restart in a rainy
midnight raid. The current MASCHINE11 log (21:53:45 session) confirms Night/Rain,
four precipitation sources ready at 21:58:34, and no rain exception. RainVolume=1.
The earlier source PCM checks did not establish that rain survived the mixer.

The installed EFT MasterMixer contains an additional hidden gate:

| Group | Parent | Effects | Indoor snapshot | Outdoor snapshot | Bunker snapshot |
|---|---|---:|---:|---:|---:|
| PrecipitationAmbientIn | AmbientIn |0|0dB|-80dB|0dB|
| AmbientIn | Ambient |0|0dB|0dB|0dB|
| PrecipitationAmbientOut | AmbientOut |0|0dB|0dB|0dB|

The indoor child's gain (index 82) is not exposed. A source may have valid PCM,
volume1 and a neutral AmbientInVolume parent yet be effectively silent. The
acoustic room and gameplay-trigger/snapshot state are separate systems. Native
BetterAudio.TransitToEnvironment selects the snapshot, while imported rooms
select the precipitation bed. A player's Indoor field alone does not prove the
current effective mixer state.

Root and independent reviewer both decoded the installed mixer. Reproducible
ignored evidence: `work/equator-rain-output/mixer_probe.py`, `native-mixer.json`,
`mixer-evidence.json`. Decompiled references: BetterAudio.cs:1173-1183;
EFT.EnvironmentEffect/EnvironmentManager.cs:176-210;
EFT.ActiveHeadphones/ActiveHeadphonesDefaultConfigurator.cs:93-102.

## Repair

Only the two owned indoor-rain sources use the immediate native AmbientIn parent.
Both skipped child and destination have no effects. The path after that remains
AmbientIn -> Ambient -> Main -> World -> InGame -> Master, including all native
hearing protection, compression, global ambient gain and master processing.
Room gain and live RainVolume still control our sources. Outdoor routing,
scenery, reverb, weather, lights and saved tuning remain unchanged.

Existing opt-in ReverbPulseBus gains IndoorRain and LegacyIndoorRain diagnostics.
They send the same quiet 1500Hz pulse through the new and old route without
changing mixer values. F10 now also reports AmbientVolume/HeadphonesMixerVolume;
these are diagnostic only and never forced neutral.

## Native output comparison

GPU-2 process `2632a9ff`, started 22:11:22, 20/20 loaded plugin identities match.
Ordinary no-bot Equator midnight/rain, stationary at (0,.1,14), acoustic room 19
IndoorConcrete. RainVolume temporarily 0 removes rain from the diagnostic bed.
The existing probe warms listener output and measures 1500Hz energy before and
during an identical pulse; it observes final Unity output, not source PCM.

| Route | Baseline amplitude | Pulse peak | Conclusion |
|---|---:|---:|---|
| Legacy indoor child |1.0418e-4|8.8088e-6|No distinguishable pulse|
| Repaired parent |7.1652e-6|1.8062e-2|Clear output above background|
| Legacy child repeated |5.5721e-5|2.3662e-5|No distinguishable pulse|

The before/after/before route comparison establishes a real mixer-output defect.
The tiny remaining old-path numbers are background audio, not an audible rain
measurement. RainVolume restored to 1 and probe route restored to Ambient;
ReverbPulse resets itself false. This does not certify the user's audio device
or constitute a listening assessment.

Normal rain checks after restoring RainVolume=1:

- Room 19/IndoorConcrete: `amb_rain_indoor_strong`, volume 1, route AmbientIn,
  source RMS 0.012246, final listener RMS 0.010452.
- EmptyAudioRoom outside: `amb_rain_outdoor_strong`, volume 0.5, original outdoor
  route, source RMS 0.022036, listener RMS 0.017709; indoor source volume 0.
- Next Equator 12:00/Clear raid: actual rain 0/None, all four sources stopped with
  no clip. The first raid cleaned up its four sources and bus leases on exit.

## Build, configuration and delivery

Release client build: 0 warnings/errors. Release suite: 577 passed (existing
nullable warnings in source-linked test stubs); distribution: 14 maps, 42 bundles,
14 sidecars valid. Root and independent agent reviewed the narrow code change.
No bundles or map assets rebuilt. All 891 host and 804 vanilla config assignments
remain identical, including restored diagnostic values; no extra config key.

Both installs received the final pair via sanctioned dry-run then DLL-only deploy:

- Client SHA256: `3DB8476449317F9F72D71951573303E76938665B47FA96C69DF93CE43BE2FFE6`
- Server SHA256: `3F07395D5E8496DC6A1E9D2F84113F18A0A563013B557A4C517AD9E46668097B`

Server source is unchanged; the existing Release test build refreshed its artifact.
The user must restart the client to load the rain repair. GPU-2 remains at MainMenu.

Final log export `work/equator-rain-output/native/final-2632a9ff.json` contains
4,122 lines and both raid cleanup messages. Three long stored lines were truncated.
The known inactive MenuScreen coroutine error occurred during debug exit; no
rain or probe error. Final status still matches 20/20 loaded plugin identities.

## Follow-up 2026-10-10 — open atrium precipitation

The user confirmed that the output repair is audible, but Equator's open atrium
still used the muffled indoor bed. The original imported acoustic room 19/Main
is IndoorConcrete even below the broken lattice roof. Room type alone therefore
does not describe precipitation exposure. Native IsCameraUnderRain is unsuitable:
it tests the authored WeatherObstacle collider, which this port does not have.

Equator rain now consults the existing repaired roof depth photo at the audio
listener. Nine points across a 1.2 m footprint distinguish overhead cover from
the ground; <=5 covered samples selects outdoor, >=8 selects indoor, and the
middle band holds the previous selection. Existing source gain fades smooth the
transition. Indoor room-specific clip presets cannot override the exterior bed.
The acoustic room, room reverb and Bay5's audible selection remain unchanged.

A completed donor-owned Render publishes a fresh revision even when the native
render texture is reused. One disposable CPU snapshot is captured per revision
during precipitation, then sampled at 5 Hz without further GPU reads. Re-render,
dry weather, camera reset and raid teardown invalidate/release it. FullyIndoorCover
is respected through the same photo. Missing coverage data or positions beyond
the depth image fall back to the room. F10 reports exposure, covered samples and
snapshot capture count. No new setting or migration is introduced.

Twenty policy cases cover roof/ground/sentinel/invalid heights, footprint
edges, hysteresis and incomplete samples. Release build/tests and distribution
validation pass. Independent source review prompted matching photographer-owner
guards and a listener-reset fallback before native validation.

Native tuning found a real lattice intersection at (1.34,.4,7.19): five of nine
samples hit beams, although the roof is visibly open. The initial 3/6 thresholds
could hold indoor rain after arriving from a corridor. The final 5/8 thresholds
correct that case while a complete roof remains nine of nine. This is a local
cover heuristic, not an import of authored precipitation portal blending.

Final GPU-2 process `4f109668` (20:30:09), loaded/host/VM matching 20/20:

| Point / state | Covered samples | Rain bed | Active source gain | Listener RMS |
|---|---:|---|---:|---:|
| Lounge lattice (1.34,.4,7.19), room 19 |5/9|outdoor strong|0.5|0.016031|
| Corridor (-4.5,.1,38.5), room 5 |9/9|indoor strong|1.0|0.012793|
| Atrium (0,.1,14), room 19 |0/9|outdoor strong|0.5|0.019840|
| Same atrium, FullyIndoorCover=true + F8 |9/9|indoor strong|1.0|0.013403|
| Same atrium, restored false + F8 |0/9|outdoor strong|0.5|0.041008|

Each opposite bed reached volume zero. The acoustic room remained 19 throughout
the atrium cover toggle; snapshot captures advanced 2 -> 3 -> 4, proving same-RT
cache invalidation. The two early captures are initial rendering and the existing
raid bootstrap. Screenshots under `analysis/screenshots/equator-rain-exposure-2026-10-10`
were visually inspected. This verifies clip selection and native output, not a
subjective listening assessment.

The subsequent 12:00/Clear debug raid had rain=0/None, four stopped empty sources
and zero depth captures. Both final raids logged released sources and bus leases.
The only error was the previously known inactive MenuScreen coroutine on debug
exit; no precipitation errors or roof snapshot warnings. Final export:
`work/equator-rain-exposure/final-4f109668.json` (4,403 lines, three unrelated long
stored lines truncated). GPU-2 is at MainMenu with all 20 plugin identities matching.

Both installations received the verified DLL-only pair through dry-run then
explicit sanctioned promotion. All 891 host and 804 vanilla config assignments
match their pre-test baselines; FullyIndoorCover=false is restored.

- Client SHA256: `DED3071738B9CF8840CF3BD0D804FA6A5F445D21C10BE78D66193FFA22EECD74`
- Server SHA256: `F7DFF01F7B7E8D004037F80D694F83CB00CAA9A4D86521686F22FAAC0B431E97`

Server source is unchanged; tests refreshed its Release artifact. No bundles,
materials, layout, general volume or visual settings changed. Restart EFT to load.
