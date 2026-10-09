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
