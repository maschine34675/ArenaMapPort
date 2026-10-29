# Occasional original spectator calls — 2026-10-27

User accepted the restored crowds and requested their original audio. This pass adds occasional
spatial calls to Air Pit, Equator day/night, Bay5 and Bowl. It does not connect kill/round-win events
or change ArenaMode. No postmatch crowd loop and no new setting.

## Source and export

`tools/extract/extract_spectator_audio.py extract` reads the installed Arena source (0.6.1.0.48206)
without changing it. `fan_speaker_voice.bundle` has 136 embedded AudioClips. Its `Participating`
bank, trigger 1123, references 13 embedded clips plus nine external entries. Only those 13 embedded
clips are selected, by actual bank references; name checks additionally reject category drift.
Other banks include event reactions such as `Cheer`, `Angry`, `StartRound` and `EndRound`.
`Participating` is not the separate `Neutral` bank. No transcript or language/content listening
audit is claimed, and fan numbers are not assumed to identify a matching visual actor variant.

The shared ignored package is `work/extract/spectator-audio`: 13 WAV files and
`spectator-audio.json`. PCM16, mono, 44.1 kHz, 31.918889 s in total, 2,815,818 WAV bytes. Original
samples are resampled where needed and receive constant gain only: target RMS -22 dBFS, maximum
boost +6 dB, peak ceiling -3 dBFS. Independent reconstruction matched within one PCM16 step. The
actual gains are -9.92 to +2.72 dB; resulting peaks -10.93 to -5.06 dBFS. No compression or loop.

Release and DLL-only builds stage these 14 files beside the plugin, in `spectator-audio/`.
The release classifier assigns them once to core, requires the manifest and refuses other audio
categories, backup files and nested paths. Distribution validation checks the exact file set,
format, samples, levels, hashes and byte equality with the extracted input. The source bank is
checked against the protected key-source filenames before extraction; no encrypted map bundle or
key derivation changes. Extracted art/audio remains ignored and is not committed.

## Runtime and ownership

`SpectatorAudio` belongs to the existing owned crowd root. It receives the original world-space
actor positions with a 1.6 m head offset. A single scheduler serves the whole crowd, with an initial
12–20 s delay after a living local player's raid starts. Each clip is followed by 16–28 s silence.
It chooses nearby spectators independently of view direction, avoids immediate actor/clip repeats
when alternatives exist, and retries after three seconds if no listener/source/candidate is ready.
The native rolloff distance is 90 m, or 150 m for Bowl's distant stands; candidate range is 80% of
that and selection prefers positions within 15 m of the closest spectator. Gain is 0.5.

One explicitly owned `Environment` BetterSource uses EFT's mixer, volume/headphones and native
`Combined` obstruction/room propagation. Playback waits for SpatialAudioSystem initialization.
The source handle is retained before setup, and the propagation container is paired with its
emitter GameObject. Teardown unregisters that container and releases the source, including the
native reverb helper. It never enters BetterAudio's automatic release queue, creates an AI/player,
or emits a player speech event. Shared audio presets are untouched.

Raid exit, death and disabling the component stop playback. Scene destruction frees clips and
unsubscribes the audio-device callback. Audio resets discard generated clips and rebuild them from
retained PCM after a fresh start delay. A missing/damaged bank leaves the visual crowd available
and issues one actionable warning; normal playback details use Debug. Read-only runtime counters
expose clip/speaker, timings, native occlusion, source volume, confirmed playback and source-output
sample peaks for validation, without a player-facing setting.

## Validation

Evidence lives under ignored `work/spectator-audio/`. Eight Python package/negative/audio-processing
fixtures, five pure C# timing/selection/package tests and 183 release-packaging fixtures pass.
The final Release client builds with zero warnings/errors; full staged distribution validates.
An independent read-only review checked source membership, decoded PCM, package identity and
current EFT audio signatures/ownership. It identified the startup fallback without occlusion;
the final implementation waits for the native room system instead.

Native evidence is appended below after host verification.

## GPU-2 acceptance — 2026-10-27

Sanctioned full deployment copied 623 files after a dry run (14 new audio files; client and server
DLL replacements; other data identical). The unchanged server source was rebuilt by the linked
test project, updating its revision metadata. `gpu-run deploy-wait --server` restarted the otherwise
idle server to release its old DLL; all 16 selected client/audio/server files matched on GPU-2.
EFT then started fresh as process `0d9c548b`, with client MVID
`8a58fde8-089f-42e3-bb14-9e792a4169aa` matching build, host disk and loaded assembly. Client SHA-256:
`32A02476F6CDE879FA3026B4044F3F2A744D7E8100DAB7897ACC99F9D3FE621E`.

Five consecutive no-bot PMC raids passed: Air Pit, Bowl, Equator day, Equator night and Bay5.
`native-calls.json` contains 17 call starts (3/3/3/2/6) with native occlusion enabled throughout.
No adjacent clip or spectator repeated within a raid. Actual silence after a completed clip ranged
from 16.114 to 25.905 seconds. Runtime samples on every map confirmed `AudioSource.isPlaying` and
nonzero source-output PCM, beyond merely having scheduled a clip. Each raid also naturally ran
through an audio configuration reset before playback and rebuilt its generated clips successfully.

Representative observed samples (source-output peaks are not a calibrated device loudness reading):

| Location | Distance | Native occlusion gain | Source-output peak |
| --- | ---: | ---: | ---: |
| Air Pit, covered spawn | 16.4 m | 0.0574 | 0.00403 |
| Air Pit, open playfield | 20.9 m | 0.6801 | 0.02296 |
| Bowl, distant eastern stands | 69.9 m | 0.6077 | 0.00659 |
| Equator day, edge/spawn | 32.3 m | 0.1040 | 0.00147 |
| Equator day, atrium | 23.6 m | 0.6427 | 0.03237 |
| Equator night, spawn | 25.2 m | 0.0972 | 0.00385 |
| Bay5, distant spawn | 55.5 m | 0.6936 | 0.00068 |
| Bay5, near the stands | 28.6 m | 0.6539 | 0.02301 |

Every return left zero `SpectatorAudio` managers and zero generated spectator AudioClips; subsequent
raids played again. The final host state is MainMenu with the test profile healed. `check-evidence.py`
checks the captured timings, repeats, confirmations and cleanup. All eight acceptance gates are met.

There were zero ArenaMapPort warnings/errors in the complete process log. Separate existing Unity
messages remain: eight duplicate refrigerator LODGroup warnings across the two Equator raids and
one inactive `MenuScreen` coroutine error on the first return; all menu returns succeeded. Steady
samples were about 120 FPS, but the raid-start windows still include 0.48–2.18 s loading hitches;
this is not an isolated audio overhead benchmark or a loading-stutter fix.

Limits: the actual output device was not listened to, so spoken content and subjective balance
against combat remain for a hearing test. No bots, multiplayer, forced death or mid-call device
unplug were exercised. Native pool/propagation ownership and interruption paths were additionally
reviewed against the installed EFT assembly. No release archive, upload or push was made.

## Denser crowd after hearing feedback — 2026-10-28

The user clarified that the isolated voice disappearing in the mix calls for many more voices,
not merely more gain. A gain-only draft was built but never deployed. Per-voice gain remains 0.5;
the original PCM and attenuation ranges remain unchanged. A new call starts every 0.8-1.6 seconds
after an initial 2-5 second delay, overlapping the existing clips. The shared scheduler caps active
voices at four and starts at most one per frame, with no catch-up burst after a stall. Given the
mean clip duration of 2.455 seconds, the expected steady overlap is about two voices.

Each of four reusable voice slots owns a distinct emitter GameObject, BetterSource and native
propagation-container ID. A shared emitter would incorrectly merge source positions and let one
release remove the other voices' occlusion. Active spectators, active clips and other recordings
from the same active voice family are excluded from the next draw. The family label is taken from
the original clip name, not inferred from a spectator model. The clip cache remains shared.

Each slot releases independently on its DSP end time, measured after Play as in the native audio
path. Failure during one start releases only that slot. Reset, death, disable and scene destruction
stop all slots before discarding the shared clips. Individual-call Debug logs now require developer
mode; read-only counters expose active/playing/peak counts and the active speaker/clip names.

Seven pure C# tests cover dense cadence, restart delay, exclusions, absent candidates, package
validation, and a ten-minute simulation with overlap but never more than four voices. Release client
build and the unchanged 13-clip package verification pass. Independent read-only review confirms
native Environment pool capacity (32 pre-created sources), separate ownership and cleanup.
Deployment uses the sanctioned client-DLL-only path; no server, audio asset or config deployment.

Native verification: fresh GPU-2 process `3c9c8a11` loaded client MVID
`5989165c-bc20-40bd-b3eb-ab05adfa097b`, identical to build and host disk (SHA-256
`29920C46634F784BD3C35FBD4868D0B3FFD0AE1C9ABAB34AB63C2E938647D38F`). An Equator day no-bot raid
recorded 135 calls at 0.825-1.595 s intervals. `PeakConcurrentVoices` reached four from actual
`AudioSource.isPlaying` checks. Captures confirmed distinct active speaker IDs and voice families,
and nonzero output PCM. One simultaneous native read showed separate source positions
`(-17.025, 7.01, -20.698)` and `(17.986, 12.376, 10.839)` with container IDs 114 and 115;
the other two idle slots had null sources and container -1. The audio reset at raid startup was
handled and room occlusion remained active.

After exit, inspectors found zero managers, zero spectator AudioClips and zero voice emitters.
The test profile was healed. The complete process log had no ArenaMapPort warnings/errors;
four previously known refrigerator LOD warnings and one inactive MenuScreen coroutine error
occurred separately. Read-only steady samples were near the 120 FPS cap; no isolated performance
benchmark or subjective hearing claim is made. Evidence: `work/spectator-volume/`, with
`check-evidence.py` checking cadence, caps, captured exclusions, cleanup and mod log.

Other sessions started ArenaMode/Redeploy updates before the additional Bay5 raid. No conflicting
action was forced. The user explicitly accepted the Equator test as sufficient for this change;
Bay5 and the other maps were not retested with the denser cadence. All four gates for that agreed
scope are met. The initial gain-only draft was superseded; the delivered change increases the
number of voices, retaining per-voice gain 0.5. No release/upload/push.

## Final voice selection and three-voice cap — 2026-10-28

The user preferred the fan recordings after hearing the nine externally referenced Scav clips.
Those references remain intentionally excluded: the package still contains exactly the 13 original
embedded Participating clips. No new audio or event reactions are added. `MaxConcurrent` is now three;
the native emitter-slot array uses that same constant. Gain 0.5, source range, initial delay,
candidate/family exclusions and the 0.8-1.6 second scheduling interval are unchanged. A full set of
three occupied slots delays the next start until a slot is free, so actual intervals can be longer.
The existing ten-minute simulation now asserts a peak of three and refusal at three active voices.

The filename audit read AudioClip metadata from all 636 `/audio/` bundle entries in the installed
Arena's `Windows.json`: 35,618 names and zero read errors. No `arena_fan1` through `arena_fan6`, or
`arena_fan14`, occurs in that inventory. All numbered fan clips found are in `fan_speaker_voice.bundle`:

| Voice | All recordings | Embedded Participating recordings |
| --- | ---: | ---: |
| fan7 | 8 | 2 |
| fan8 | 11 | 1 |
| fan9 | 9 | 2 |
| fan10 | 7 | 1 |
| fan11 | 14 | 2 |
| fan12 | 50 | 3 |
| fan13 | 31 | 2 |
| fan15 | 6 | 0 |
| Total | 136 | 13 |

The numbering alone does not explain why earlier numbers are absent. It is not evidence of a
failed export. `ambient_voice.bundle` additionally has seven stereo crowd beds (three Angry,
three Cheer and one Neutral), not additional numbered individual voices. This was a metadata
audit, not a listening/transcription review. Evidence: ignored
`work/spectator-finalize/fan-name-audit.json`.

The earlier reference audit resolved all nine external Participating slots to `scav1.bundle`
(six enemy-contact names, one session-late, one enemy-USEC and one enemy-sniper name). The decoded
preview and reference table remain local under `work/spectator-references/`; they are not staged
or shipped. Event integration remains future work: ArenaMode can publish confirmed kills and
round results through an optional adapter, but shot activity and true team wipes need additional
notifications. No ArenaMode files were changed. The current living-local-player guard also means
crowd playback stops during death, including a death camera.

The focused C# tests and Release client build passed, as did the audio fixtures, release-packaging
fixtures, changelog checks, original 13-clip verification and full staged distribution validation.
The existing native evidence above is not evidence for this final three-voice build;
the earlier all-map audio test used one voice, and the dense Equator test used up to four. No new
raid or subjective hearing check was performed in this finalization. The sanctioned client-only
deployment updated `D:\SPT41` after its dry run; candidate SHA-256
`C281D2B094AE47F05353D6BE23420909C2EEE9DFC67B7FFB50B011A329841E6B` (built from the modified
`5e13840` tree). Server, audio, settings and bundles were not deployed. The host synchronization
wait did not complete: a read-only Syncthing check found the local `D:\SPT41` folder paused,
while GPU-2 itself is connected and unpaused. No system error was reported. Its disk still has
the prior four-voice DLL (`29920C46634F784BD3C35FBD4868D0B3FFD0AE1C9ABAB34AB63C2E938647D38F`).
The pause was left unchanged because parallel sessions share this installation. Resume synchronization
when those sessions allow it, verify the new DLL hash, and restart EFT before a native check.

## Intermittent noise investigation — 2026-10-28 (unresolved)

The user reported a short intermittent noise newly noticed after spectator calls were added,
with no established relation to a call start/end. The identified session is Equator TDM on
MASCHINE11, `LogOutput-MASCHINE11-2026-10-28_19-26-57.log`. It loaded the development client
still labelled 0.3.0, before the shared-texture 0.4.0 deployment. The log records 254 calls
between 19:31:33.861 and 19:36:53.905, never more than three voices. The three long pauses
match player deaths/respawns. No spectator, MetaXR or other audio runtime failure is logged;
the sole Error is UiBridge binding at startup. This absence does not rule out an audible glitch.

An independent PCM audit and root recheck freshly decoded the 13 original Arena clips and
reproduced all shipped WAVs exactly, including their normalization. Source exports and staged
files/manifest are byte-identical. No clipped samples were found in originals or exports;
export peaks are -10.93 to -5.06 dBFS, RMS -22 dBFS. First/last samples are at most 15/32768
(about -66.8 dBFS), with no large boundary step. Original recordings contain brief high-frequency
speech passages; without a recording/time of the reported noise these cannot be labelled faults.
No filter, normalization or asset change is justified by these measurements.

The native `Audio/prewarm_sound` was independently decoded twice: all 2,756 mono samples at
44.1 kHz are zero (62.5 ms of digital silence). Environment's current server preset also has
`enabledPrewarm=false`. The native warm-up *sample* therefore is not a noise recording; this
does not establish the behavior of the native audio plugin under every state transition.
Pool release, pitch/Doppler reset, container cleanup and DSP expiry follow native patterns.
Registering occlusion before Play was considered but not changed: Combined propagation defers
its actual calculation to update/late-update, so simply reordering the calls is not a proven fix.

The user's log confirms **SurroundAudio 1.0.0 with 5.1 output at 48 kHz**. Its source changes
the presets/spatialization and routes unspatialized direct voices through the separate MetaXR
reverb helper when KeepReverb is enabled. This differs from the previous GPU playback tests;
it is a testable lead, not a proven incompatibility. The user was asked for a live comparison:
F12 -> maschine-SurroundAudio -> General -> KeepReverb=false, otherwise unchanged Equator TDM,
then restore the setting. That disables the optional MetaXR helper share, not all EFT reverb.
The result is pending. No external mod, user setting, playback code or release archive was altered.

Evidence: ignored `work/spectator-noise/clip-audit/` (reproducible source/PCM analysis and native
silent prewarm sample) and `work/spectator-noise/log-audit.py` / `log-audit.json`. GPU-2 remains
reserved for the user's separate ArenaMode startup recovery; no new raid was started here.

## Two-voice cadence and negative reverb comparison — 2026-10-29

The user heard the noise three more times on Equator with SurroundAudio `KeepReverb=false`,
then left immediately after the last occurrence. AirPit afterwards had no noticed occurrence.
This does not support the optional reverb helper as the sole cause; no playback-order, filter
or asset fix has been made on that hypothesis.

The new MASCHINE11 log `LogOutput-MASCHINE11-2026-10-29_10-55-45.log` loads client 0.4.0 and
contains 320 Equator calls and 318 AirPit calls, both using all 13 clips and at most three voices.
The last 30 seconds before the first Equator menu indicator at **11:04:45.454** contain 24 calls
and no warning/error. The last room change is 11:04:19.195 (hall update 19.207), then no further
room/audio-reset event in the window. Last pre-menu starts are fan10_01 at 42.213, fan9_02 at
43.681 and fan12_04 at 44.679. Native Stop(Left) is **11:04:47.061**, after the reported noise,
so exit cleanup cannot explain that pre-exit occurrence. The single whole-log Error is UiBridge
startup, not audio. Log analysis is reproducible in `work/spectator-cadence-20261029/log-audit.py`.

Other candidates remain unproven. Equator has original electricity/cracles ambient banks absent
from AirPit, with independent random scheduling; individual ambient starts are not logged here.
Equator's weather probe shows no rain, and dust has no audio playback. A one-time missing stun
screen-flash warning at 10:59:35.792 proves a stun effect occurred, but supplies no connection
to the noise immediately before leaving. A Molotov at 11:04:25.851 has a logged six-second burn,
well before the menu. None of these facts justifies removing or altering an unrelated sound.

Independently requested behavior change, committed as `d8b51d7`: **two simultaneous spectator
voices**, with **1.6-3.2 seconds between call starts**, twice the previous 0.8-1.6 interval. The
2-5 second initial/restart delay and three-second failed-selection retry are unchanged; those
are not the cadence between calls. Gain 0.5, the original 13 clips, family/speaker exclusions
and native room processing remain unchanged. The ten-minute simulation reaches two voices,
never exceeds two, and verifies the reduced start count and doubled interval.

Added `Audio diagnostics/SpectatorCalls`, default true, for a controlled hearing comparison.
Only DeveloperMode can honor a false value. It is not added to the compact player configuration.
Off releases all active spectator sources and pauses their schedule; on uses the normal initial
delay. Visible spectators, map ambience, other sounds and third-party configuration remain intact.
The setting change is logged at Debug in developer mode so the next log can establish the comparison.

Seven focused C# tests, 13 changelog/version fixtures, the Release client build (zero warnings/errors)
and full distribution validation passed. Independent code review found no concrete issue. Sanctioned
client-only dry-run and deployment updated exactly one DLL in `D:\SPT41`; built and installed SHA-256:
`40056EA3DF2C55BCFF6305FFBD6B49FE16014CE91340C93D7DCE74C56B165FA4`.
No game was started/restarted and no fresh native acceptance is claimed. Evidence is under
`work/spectator-cadence-20261029/`. The 2026-10-28 release ZIP still contains the earlier three-voice
build and must be rebuilt after final audio acceptance; it was not uploaded or published.

## NoiseProbe diagnostic (2026-10-29)

Status: implemented and unit-tested offline; **not run on any host, nothing was recorded or heard**. It exists because the
intermittent Equator noise has no timestamp in any log and nobody has ever measured the mixed listener output during a real match
(the earlier GetOutputData probes read channel 0 of one voice, pre-mixer, 256 samples every 0.2 s). The offline synthesis that
designed it ranks the candidates (Equator's boosted ambient one-shots, the spectator onset overshoot, physics-prop impacts, the
death hard-cut, a fault below Unity) and states that none exceeds about 25 %; the probe is how those are separated in one or two
sessions. It is developer-only, off by default, and changes nothing that is played unless an explicit A/B switch is turned on.

**Files.** `ArenaMapPort.Client/NoiseProbeModel.cs` (Unity-free: block statistics, detector, event ring, report text, self-test,
PCM ring, WAV writer, capture policy; linked into the tests), `NoiseProbe.cs` (config, taps, driver, mark key, census, audio
filter tap, export). One-line taps, each behind `if (NoiseProbe.Armed)`, sit in `SpectatorAudio.cs`, `Bay5AmbientAudio.cs`
(driver poll), `PhysicsPropSoundRepairs.cs`, `RoomReverb.cs`, `RoomShotAcoustics.cs`, `GateSounds.cs`, `PuddleFootstepRepairs.cs`
and `PrecipitationAudio.cs`; `Plugin.cs` has one `NoiseProbe.Install()` line after `SpectatorAudio.Install()`. Tests:
`NoiseProbeModelTests` (synthetic signals) and `NoiseProbeSourcePinTests` (the rules of the glue, read as text).

**Keys** (section `Audio diagnostics`, `Plugin.Instance.Settings`, none public, all honoured only with DeveloperMode, all live):

| Key | Default | Meaning |
|---|---|---|
| `NoiseProbe` | `Off` | `Off`, `Detect` or `Capture`. Armed only inside a raid; everything stops at raid end. |
| `NoiseProbeMarkKey` | `Pause` | KeyCode polled with `Input.GetKeyDown` (works while WASD is held; `Mouse3`/`Mouse4` allowed). |
| `NoiseProbeSelfTest` | `false` | Request; starts 4 s into the next armed raid (or at once), then resets to `false`. |
| `NoiseProbeMuteAmbient` | empty | Comma list of exact ambient player names (`electricity,cracles,cracles (1),cracles (2)`), `AudioSource.mute`, reversible, works without `NoiseProbe`. |
| `NoiseProbeMuteProps` | `false` | Every physics-prop impact bank is skipped (the 0.5 s collision timer is kept). |
| `NoiseProbeSpectatorFade` | `false` | 150 ms fade-in at the start of a call; an early stop (death, calls switched off) fades out over 80 ms and the source is released afterwards. Ended calls, resets and failed setups are never faded. |

Existing live keys the experiments use: `SpectatorCalls`, `Audio (arena_equator)/AmbientGainDb`, `AmbientVolumeOffset`,
`AmbientRangeMultiplier`, `PuddleFootsteps`, `RoomShotAcoustics`, `NativeRoomReverb`, `Spawns/GateSounds`, `Physics/EnablePhysicsProps`.

**Detect** (main thread, no managed code on the audio thread): at most every 12 ms in `LateUpdate`,
`AudioListener.GetOutputData(buf1024, ch)` for every channel of `AudioSettings.GetConfiguration().speakerMode` (6 for 5.1). Per
block: peak, rms over all channels, D1 (largest first difference), HF (rms of the first difference / (rms x sqrt 2): white noise 1.0,
1 kHz sine 0.09, 6 kHz 0.54), per-channel peaks, count of samples at full scale, non-finite samples. The baseline is the median of
the last 64 blocks. Kinds and starting thresholds (`NoiseThresholds`, to be calibrated on the first minutes): PEAK = peak >= 0.03
(-30.5 dBFS) and >= 15 dB over the baseline peak and >= 10 dB over the baseline rms and HF >= 0.30 (LOUD from 0.25: counted, at most
4 lines a minute); CLICK = D1 >= 0.04 and >= 8 x median D1 and >= 0.5 x peak and peak < 0.25 (a block that qualifies as PEAK, or
lies within 150 ms of one, is not also a CLICK); DROPOUT = two or more all-zero blocks over an rms floor of 3e-4; GAP = the DSP
clock stepped behind real time by more than 60 ms against the minimum of the last second (the brief's "60 ms over two frames" cannot
exceed 20 ms at 100 fps, so the step form is used; a main-thread hitch moves both clocks and is not a gap), or, in Capture, a gap
between two audio filter callbacks of more than three buffers; CLIP = a sample at full scale or non-finite. Refractory 150 ms per
kind, consecutive blocks are one event (logged when it ends, `dur` = its length), PEAK/CLICK are ignored for 3 s after arming or an
audio reset, at most 12 lines per kind per minute (the rest is counted as `suppressed`). Level alone cannot separate speech sibilants
from noise below about -30 dBFS: the real classifiers are the ORPHAN flags and the mark WAV.

**Event ring** (1024 entries, no allocation per event: structs and interned labels). Tokens, in the order of the mark dump:
`spec+` (clip, call number, distance, occlusion factor, voices), `spec-` (reason `ended|dead|reset|slot|stopall|toggle|notstarted|noservices|disable`,
ms played, clip time left), `spec.on` (source.volume and `OcclusionVolumeFactor` at frames 1, 2, 3, 5, 8, 13 after Play), `amb+`,
`amb~` (clip changed or `timeSamples` rewound while playing, a loop wrap excluded) and `amb-` from the `isPlaying` edges of the owned
active ambient players (polled in the existing driver, no scene search), `prop` (name, SoundType, distance, `played|skipped|muted`),
`room` (id/type), `roomReverb` (mixer category, changed 0/1), `roomShot` (room, class, changed 0/1), `gate`, `puddle` (you/bot, distance),
`rain` and `rain.play`, `audioConfig` (device changed), `reset`, `life` (alive/dead flips), `hitch` (>= 50 ms), `gc`, `MARK`, `burst`,
`selftest`. A burst is **ORPHAN** (`orphan=1`) when no cause event (everything above except `spec.on`, `amb-`, `gc`, `MARK`, `burst`) lies in
[-400 ms, +50 ms] of its start and no spectator call with source volume >= 0.01 is playing; `evOrphan=1` ignores the voice test.
Not knowable from the plugin: EFT gunshots, footsteps, bot voices, explosions, and ArenaMode's announcer, hitmarker, PMC voice and Molotov
(join those by wall clock with ArenaMode's own Debug lines).

**Log lines** (all Debug, tag `[NoiseProbe]`, BepInEx adds `maschine-ArenaMapPort`; local wall clock so ArenaMode lines can be joined; one
Warning if the probe cannot arm or stops, none otherwise):

```
armed mode=Detect map=arena_equator speakers=Mode5point1 driver=Stereo ch=6 rate=48000 dsp=1024 voices=196/256 calls=on mix=g2.0/o0.40/r2.50 roomShot=on roomReverb=on tap=readback ring=none
BURST #12 15:04:12.481 kind=peak lvl=MID pk=-24.1@ch3 rms=-39.0 bg=-58.2 over=+19.1 overPk=+22.0 d1=0.021 hf=0.62 dur=21ms ch=[-30,-29,-27,-24,-33,-41] pos=(x,y,z) room=19/IndoorConcrete fps=118 voices=1 orphan=0 evOrphan=0 | spec=[fan9_02#74@11.0m occ0.64] amb=[cracles (1)@14.2m age120ms] prop=[Glass@6.2m age80ms] ev=[spec+@-0.32s,room@-0.90s] census=[pool=143 playing=9 AmbientIn:3 Environment:2 ownedAmbient=3 ownedSpectator=1]
BURST #3 15:04:15.020 kind=gap lvl=MID lag=87ms src=dsp          (src=tap: audio filter callback gap; kind=clip adds clipped=n nonfinite=m)
MARK #3 15:04:13.002 frame=48213 pos=(x,y,z) room=19/IndoorConcrete fps=118 voices=1 (the reaction lag is 0.3-1.5 s)
  -7.412s spec+ fan10_01#5 12.5m occ0.31 voices=2          (one line per ring event of the last 10 s, at most 200)
  hits in window: BURST #12 (-0.84s, +19.1 dB)
  voices: [...] census=[...]
capture wav=<path> 12.0s 6ch 6.9MB                          (Capture only, written ~2 s after the mark)
minute map=arena_equator blocks=n cover=% bursts peak=a click=b dropout=c gap=d clip=e loud=f suppressed=g refractory=r orphan=h evOrphan=i bgMedian=-dBFS hitch50=i gc=g0/g1/g2 spec=+x/-y amb=+z restarts=r props=p rooms=n resets=n probeMs=avg/max
calib minute=1 pk50=-62.1 pk99=-41.2 pk999=-30.5 over50=+0.4 over99=+9.8 over999=+16.2   (first five minutes: the calibration percentiles)
selftest -24dBFS PASS latency 96 ms over +22.1 dB        selftest -36dBFS MISS block peak -37.2 dBFS, background peak -64.0 dBFS
selftest done 3/4 detected; weakest detected -30dBFS     (in Capture each line also ends with tap=<peak seen by the filter>dBFS)
```

Channels are numbered from 1 in the lines (`@ch3` is the third channel). `cover` is the share of real time covered by readback windows
(windows overlap above ~47 fps and leave gaps below); `probeMs` is the probe's own cost per frame (average / maximum).

**Self-test.** Four 10 ms Hann-windowed white-noise bursts (deterministic, the largest sample is exactly the level) at -18, -24, -30 and
-36 dBFS, 1.2 s apart, through an owned 2D `AudioSource` on the Environment mixer group; each must be answered by a PEAK/CLICK hit within
0.5 s. Offline, with the model's thresholds on a -60 dBFS bed, -18/-24/-30 are found every time and -36 never (below the 0.03 peak floor):
that is the sensitivity a negative host result can claim, and the host line shows what the real mixer chain does to the level. In Capture the
line's `tap=` also proves (or refutes) that the `OnAudioFilterRead` tap on the listener object sees mixer-routed content; if it does not,
stay with Detect.

**Capture.** A `NoiseTap` component on the `AudioListenerConsistencyManager` object (it must carry the `AudioListener`, else Capture stays off
with one Warning) keeps the last 15 s as PCM16 in a preallocated ring (6 channels at 48 kHz: 8.6 MB). `OnAudioFilterRead` allocates nothing, takes
no lock, calls no Unity API, and a catch-all switches it off; it also records the largest gap between callbacks. A mark (or an ORPHAN
MID/CLICK burst, at most 3 per raid) exports 12 s (10 s before, 2 s after) from a thread-pool task: canonical 44-byte header,
`noise-<yyyyMMdd-HHmmss>-mark<n>.wav` (`-auto<n>` for bursts) plus a `.txt` with the log block, in `BepInEx/logs/ArenaMapPort-noise/`
(fallback: `diagnostics/` beside the plugin). Limits: 6 per raid, 5 s apart (a mark within 5 s of the pending one shares its capture), at
least 500 MB free (else one Warning and Capture is off for the raid), 64 MB kept (oldest deleted first). `PcmWav.Read` reads mono and stereo
only; the 6-channel file is for Audacity and for `NoiseWav.TryReadHeader`. A noise that is in the WAV is in the Unity mix; reliable marks with a
clean WAV point below Unity (device, driver, 5.1 path). Caveat: the filter changes the DSP graph and adds managed code on the audio thread (Mono's GC
can block it); use Capture for the marking run and compare the GAP counts with Detect.

**Cost.** Off: one static bool read per tap and, in DeveloperMode, two static reads per frame (no component exists in player mode). Detect: at
most 83 readbacks of 6 x 1024 samples per second (estimate 0.05-0.10 ms each; `probeMs` measures it). The census (`GetComponentsInChildren<AudioSource>`
on BetterAudio's transform) runs only at a logged burst or a mark, at most every 250 ms; nothing else searches a scene (`NoiseProbeSourcePinTests`).

**Host recipe** (GPU-2; `uibridge_status` first; DeveloperMode on; `uibridge_config_set` leases block `gpu-run` for ~90 s, set keys before the restart or
accept the wait). P0: `NoiseProbe=Detect`, `NoiseProbeSelfTest=true`, Equator NO BOTS raid, 10 s: expect `armed`, four `selftest` lines and the
summary, no Unity error about `GetOutputData` in `Player.log`; repeat with `Capture` and read `tap=`. P1: 10 min, `calib` lines, target < 1 false hit a minute in
quiet idle (calls off, bots off); adjust `NoiseThresholds` from the percentiles. P2: Equator TDM through ArenaMode with `SpectatorCalls` ABAB inside a raid
(on 4 min, off 4, on 4, off 4), the same on Air Pit, and a plain PMC raid with bots. P3 if still open: `NoiseProbeMuteAmbient`, `AmbientVolumeOffset=0`,
`NoiseProbeMuteProps`, `RoomShotAcoustics`/`NativeRoomReverb` off, `NoiseProbeSpectatorFade`, each 2 x 6 min. Analyse with a script under ignored
`work/`: orphan bursts per minute by condition and kind, the nearest preceding event kind in [-50, +300] ms against a time-shifted control, LOUD blocks and
the 400 ms after them excluded. Decision rules: calls on >= 2x calls off (>= 10 events per arm) = spectators, then read `spec.on` and the `spec-` reasons;
>= 50 % of orphans within [-50, +300] ms of one player's `amb+`/`amb~` = ambient, confirm with the mute list; alignment with `room`/`roomShot` = room
parameter jumps; zero orphans in >= 60 min of Equator = the Unity signal chain is clean above the self-test sensitivity, then Capture or a WASAPI loopback
recording on MASCHINE11 (marks must be pressed 0.3-1.5 s after the noise).

**Unverified assumptions (host checks).** `GetOutputData` on channels 2-5 of a 5.1 mix without a Unity error; the `OnAudioFilterRead` tap on the
listener object receiving the post-mixer sum; the thresholds on real material; `Pause` (or a mouse side button) being free in the user's setup; the
Syncthing sync of the `ArenaMapPort-noise` subfolder (BepInEx/logs is not excluded in `.stignore`); `VolumeFadeIn`/`VolumeFadeOut` on a
`ReverbSimpleSource` (the Meta reverb helper source is not faded) and no audible change from the fade code with the key off.

**Incidental changes in the same commit.** `SpectatorAudio` hashes its 13 WAVs with the CNG provider (`CryptoProviders.Select(null).Sha256`,
the managed fallback when CNG is unavailable) instead of `SHA256.Create()`; the check stays fail-closed. `AudioChanged` now logs one Debug line in
DeveloperMode. `SpectatorAudio.Current` (the live crowd) and `Bay5AmbientAudio.MixSummary()` / `RoomShotAcoustics.IsOn()` were added for the probe's
armed line and census. Voices are released through `BetterSource.Release()` exactly as before unless `NoiseProbeSpectatorFade` is on.
