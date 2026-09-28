# Room-dependent mixer reverb — 2026-09-28

## Scope and source evidence

Follow-up to the accepted native spatial graph (`0b841c1`), requested by the user.
Both release maps now restore the missing original ambient mixer controller.
This is an addition to native propagation and room tones, not a replacement for
the existing gameplay indoor/outdoor trigger system.

| Original object | Equator | Bay5 |
|---|---|---|
| AmbientAudioSystem | level56:910 | level188:1263 |
| MixerEffectsDataSO | sharedassets39.assets:105 | same |
| ReverbMixerDataSO | sharedassets39.assets:106 (13 parameters) | same |
| DelayMixerDataSO | sharedassets39.assets:102 (5 parameters) | same |
| Send parameters | sharedassets39.assets:103 (8 parameters) | same |
| Volume parameters | sharedassets39.assets:104 (empty array) | same |

All raw objects are fully consumed. Enum dictionaries serialize separate string
key and float value arrays; mismatched lengths, duplicates, unknown categories,
nonfinite numbers, unsupported handlers and parameters are rejected. No curves
are present. `room_reverb.py` integrates with the existing spatial extractor;
the `mixer_effects` field is additive to its schema-1 graph. The same six package
files are staged, with no scene-bundle rebuild. Object hashes, source asset hash
and original unmodified values are in the ignored package JSON. Source-owned
assets/data remain outside git.

The common tables contain only IndoorGeneric (4), IndoorIsolated (8), Outdoor
(67). Native dictionary lookups are exact: unchanged dictionaries would match
only 1/21 active Equator rooms and 1/40 Bay5 rooms. The port maps each active
authored room's exact type to its isolated/outdoor/indoor category. Inactive
rooms are excluded; conflicting mappings fail before any mixer write.
EmptyAudioRoom is explicitly outdoor, including Equator's uncovered north spawn.
This compatibility translation does not invent separate concrete/brick/metal
presets; both maps share the original three-category table.

## Actual audio routes

Source and target MasterMixer tables independently confirm all 26 parameter
names and their effect/group destinations (CRC32 exposed-name lookup):

- AmbientReverb parameters control Unity reverb on Returns/TarkovReverb.
- Delay parameters control TarkovDelay and its low-pass filter.
- Ambient effect sends feed these returns. CommonAmbInEffectsSend instead feeds
  the separate Returns/Reverb used by gameplay.
- The existing EnvironmentManager/IndoorTriggers call BetterAudio's native
  Indoor/Outdoor snapshots. The gameplay return's stored snapshot volume is
  -20 dB inside / -80 outside. This volume and its DSP are not exposed for
  GetFloat: these are offline metadata, not runtime readbacks. The new controller
  does not write this return or replace those triggers.
- MetaXR reflection processing and SurroundAudio are separate. KeepReverb and
  all other third-party settings remain unchanged. WorldReverbLevel belongs to
  the hearing-debuff effect and is also untouched.

Read-only research evidence is under `work/room-reverb-research/`: raw decoder
outputs and independent source/target mixer wiring reports. Runtime contracts
were checked against the installed EFT 0.16.9.5 decompile and independently
reviewed by a second agent.

## Runtime and controls

`Audio (<map>) / NativeRoomReverb = true` is a new live setting for Equator and
Bay5. It requires their original `NativeSpatialAudio` graph. The adapter uses
the native AudioMixerParamsController and MyPlayerRoomChangedEvent; all 26 names
and complete source categories must validate against the actual mixer first.
It starts after GameStatus.Started, independently of room-tone readiness, and
reacquires on audio reset. A native AmbientAudioSystem already owning the tables
causes a safe refusal to install a competing controller.

Off/unload unsubscribes first and calls ClearFloat only for values still equal
to this controller's last write, returning those parameters to snapshot control.
Writing back an old number would leave an override blocking future snapshots.
Changed external values are yielded and preserved. Unity exposes no reliable
way to identify earlier script overrides or an external writer of the identical
value; this is the ownership limit. The new code never acquires gameplay/global
hearing parameters or indoor bus volume.

F10 logs room/category, ambient return/mix/sends and player Environment. Gameplay
return is explicitly reported as unexposed. Opt-in `Audio diagnostics / ReverbPulse`
plays one quiet 120 ms synthetic 1500 Hz pulse with no baked tail. Its bus is
Ambient (native AmbCommonEffects), Gameplay (native CommonSounds), or MapAmbience
(Equator CommonAmbInEffects / Bay5 CommonAmbOutEffects). Listener
output is warmed up, then tone-frequency energy is measured before the pulse,
during it and after dry playback. Results include background audio, so numerical
tails demonstrate processing rather than subjective balance. The trigger resets
itself; the temporary source and procedural clip are destroyed afterwards.

## First native comparison (15:28–15:33 CEST)

Candidate client SHA256 `727D83B3FE63F935FD2A351F94AB125489E4F82A69F1A43F784FEB33B10D5222`;
GPU-2 instance `e7744d67`, all 23 VM/host/loaded plugins identical after restart.
Bay5 room 17 (metal hall) -> 37 (outdoor) -> 40 (brick room); actual profile
changes matched the original tables. Full 26-parameter live off/on release passed.

Tone-frequency output amplitude (background remains included):

| Bay5 measurement | Before pulse | Early tail | Late tail |
|---|---:|---:|---:|
| Room 17, new ambient path on | 1.3427e-5 | 5.2575e-4 | 2.1545e-4 |
| Room 37 outside, on | 4.8863e-6 | 4.0008e-4 | 5.4161e-5 |
| Room 37 outside, off | 5.8693e-6 | 3.9083e-4 | 4.4199e-5 |
| Room 40 inside, off | 4.1345e-5 | 2.9447e-5 | 3.6111e-5 |
| Same room 40, on | 4.4328e-5 | 4.4685e-4 | 2.0889e-4 |

All reported dryPlaying=false after the pulse. The same-position indoor comparison
shows the real change; outdoor snapshots already provide a similar ambient wet
path, so off does not promise silence outdoors. The gameplay pulse on CommonSounds
showed normal dry output but no tail distinguishable from background in this
candidate test. Do not claim that the new ambient tables restore or strengthen
gun/footstep hall, or that the gameplay snapshot metadata proves audible output.

Off cleared 26 own overrides. During raid teardown native audio code had already
changed 24 values; cleanup preserved those and released the remaining 2. Graph
teardown completed with Initialized=false / groups=0. Subsequent final build
clarifies the unexposed gameplay diagnostic, adds MapAmbience pulse routing,
handles probe audio resets and logs any rejected ClearFloat operation.

Final host acceptance and installation identity checks follow below.

## Actual Equator path: muted target return and scoped compatibility route

The next candidate (`926A43DBE35A69C45FCA3B459F686BAA1119135779AD01F255E7A546A9E956E1`,
instance `be414123`) tested the actual CommonAmbInEffects bus in Equator room 4:
15:38:58, baseline 1.1305e-5 / early tail 8.3487e-6 / late tail 1.3715e-5.
This did **not** prove useful reverb on the actual map, despite the generic wet
path test succeeding. Further source/target analysis found the exact cause:

- Arena Returns/Reverb group 66: `mute=false`.
- EFT Returns/Reverb group 62: `mute=true`.
- All 14 DSP values match; indoor decay is 1 second. Parents are unmuted.
  The failure was not merely a tail shorter than the measurement window.
- Unity's player AudioMixerGroup exposes no mute setter, even via the installed
  managed assembly's internal APIs. Global mixer replacement is unnecessary.

Source mixer SHA256 `5688ca492aafbf95b64086b39b6b4995017fc0e94b2d1a3c83ac7015c9db03c8`;
target `94714bd556218b6cedd46c44184f54e889a792bc9f144bf3c630605ba7cf4c6a`.
Ignored evidence: `returns-reverb-mute-difference.json` and
`equator-dry-route-comparison.json` in the research directory above.

The compatibility fix routes **only the existing imported Equator ambient
group** through the original, functioning AmbCommonEffects bus when the new
controller is active. Native SetMixerGroup updates each running source without
restarting playback or drawing a new random delay. Off, failure and unload
restore the original group. The pulse's MapAmbience option now reads this actual
driver group instead of assuming the old route.

Dry paths are equivalent while AmbientInVolume=0, which the existing driver owns:
both groups are 0 dB / pitch 1; the skipped AmbientIn parent has no filters,
effects or sends. All later mixer parents, including headphone effects, are
shared. User gain/offset/range remains per player and unchanged. If an external
writer makes the indoor bus non-neutral, the compatibility route reverts to
honor that gain. Foreign changes to group or individual source routing are
preserved. Native room-tone sources and all gameplay sources retain their routes.

This is an explicit compatibility route using original effect parameters, not
an identical restoration of Arena's muted return. Bay5 already reaches the live
TarkovReverb through CommonAmbOutEffects and requires no rerouting.

## Final-build verification

Client SHA256 `963EFD5A0060DFFB77673B9C8F661F61C38620787107189C24394638F0D972D0`.
Release build has zero warnings/errors. Pipeline: 461 tests, 28 expected skips,
including 16 new decoder/malformed-input/coverage tests. Distribution validation
passes for the existing 14-map staged package (42 bundles, 14 sidecars, 97 files).

GPU-2 instance `c09a0c2b`, started 15:47:31; all 23 VM build/host disk/loaded
plugins identical. Final Equator check 15:48–15:50:

- 23 active sources use AmbCommonEffects; both original disabled dogs remain
  inactive and uninitialized. F10 confirms real original clips playing on this
  bus. Existing room-tone source remains on AmbientIn.
- Actual MapAmbience pulse, room 5: on baseline 3.8784e-5 / early tail 4.1853e-4 /
  late tail 2.0221e-4; off on restored CommonAmbInEffects: baseline 4.3782e-5 /
  early 5.1809e-5 / late 5.1490e-5. Dry source was stopped in both tail windows.
- Off restores all 23 routes and clears all 26 overrides; on reacquires them.
- Room 5 -> EmptyAudioRoom (-1, north spawn) -> room 2 translates correctly to
  indoor -> outdoor -> indoor. The separate native Player.Environment can still
  say Indoor at the north spawn: original trigger systems have different bounds;
  this port preserves both authored geometries.
- Exit restores the original ambient route, stops 23 players with zero cleanup
  failures, then releases the graph (Initialized=false, groups=0).

Final Bay5 check 15:51–15:54, same process/build:

- OutdoorCommon room 14 -> OutdoorGeneric room 37 -> IndoorBrick room 40,
  all mapped to the correct original category with 26 owned parameters.
- Actual CommonAmbOutEffects bus, outdoors: baseline 4.1639e-6 / early tail
  2.2684e-4 / late 2.8133e-5. Indoors: baseline 5.3868e-6 / early 3.9908e-6 /
  late 4.5292e-6. The outdoor ambience wet send is intentionally suppressed
  indoors by the original table, not a failed hall test.
- Live off clears all 26 values; on reacquires. All 24 ambient players stop
  with zero cleanup failures; graph releases to Initialized=false / groups=0.

Return to Equator 15:55 proves fresh initialization and the correct 23-player
compatibility route after Bay5; exit again restores the original route and
releases all graph resources. No new ArenaMapPort warnings/errors occurred.
The one Unity error was the previously known inactive MenuScreen coroutine
when opening the pause menu; no new audio exception or repeated error.

Both installations received the final host-verified build using the approved
deploy helper. A separate SHA256 comparison verified **97/97 files** in both
`D:/SPT41` and `D:/SPT41vanilla` against `dist/`. No server or bundle changes.
New switches default on; pulse is off and its bus restored to Ambient. The
original 11 changed ArenaMapPort settings match the before-test snapshot,
including saved lighting and Clear weather. All seven SurroundAudio values
also match. No config file was manually edited or deployed.

Final GPU-2 state: MainMenu, no raid, control free, all 23 plugin identities
match VM/disk/loaded. The host's Windows output remains muted; digital processing
and routing are verified, subjective listening balance remains the user's check.
Restart the user's game to load this client. In-game testing on the user's
vanilla install remains separate from its verified deployment.
