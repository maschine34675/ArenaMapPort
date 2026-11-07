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

*Superseded late on 2026-10-29: the cause is a compressed bundle block, fixed and host-verified by numbers on 2026-10-30, see [equator-noise-bundle-block.md](equator-noise-bundle-block.md).*

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

Status: implemented and unit-tested offline; **not run on any host, nothing was recorded or heard** (superseded the same evening: it ran on GPU-2 and
on the user's MASCHINE11, see "Host run of 52c8a28 and the user's captures" at the end of this file; nobody listened on GPU-2, and the user's captures are
under analysis). It exists because the
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
`AudioListener.GetOutputData(buf1024, ch)` for every channel the DEVICE delivers (`GetOutputData` reads the device output, not the mix:
on a stereo driver under a 5.1 speaker mode channels 2-5 throw `ArgumentException: invalid channel`, found on GPU-2 on 2026-10-29, so arming
probes the channels once, reads the ones that answer and prints `ch=2/6` in the armed line; the user's 5.1 driver reads all six). Per
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
that is the sensitivity a negative host result can claim, and the host line shows what the real mixer chain does to the level [the detector is relative and the
first host read was 1 of 4: see "Host run of 52c8a28 and the user's captures" at the end of this file]. In Capture the
line's `tap=` also proves (or refutes) that the `OnAudioFilterRead` tap on the listener object sees mixer-routed content; if it does not,
stay with Detect.

**Capture.** A `NoiseTap` component on the `AudioListenerConsistencyManager` object (it must carry the `AudioListener`, else Capture stays off
with one Warning) keeps the last 15 s as PCM16 in a preallocated ring (6 channels at 48 kHz: 8.6 MB; since `36a6d12` the ring is re-created at the channel count the filter tap delivers, 2 on a stereo driver). `OnAudioFilterRead` allocates nothing, takes
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

**Stereo-driver fixes and the evidence (2026-10-30).** On the stereo-driver GPU-2 the probe needed two fixes, both committed: `52c8a28` (`AudioListener.GetOutputData`
refuses the channels 2-5, so Detect reads the device channels and the armed line says `ch=2/6`) and `36a6d12` (the `OnAudioFilterRead` tap delivers the
device format as well, 2 channels instead of the 6 mix channels, so the Capture ring adopts the count the tap reports and an empty ring is reported as empty
instead of "overwritten"); with both, its Capture WAVs carry the evidence for the Equator static before and after the bundle repair (ON 1,581 ms against 0-73 ms,
measured, not listened to), see [equator-noise-bundle-block.md](equator-noise-bundle-block.md) "Host verification".

## Investigation 2026-10-29 (lead): ranked hypotheses

The cause of the intermittent Equator noise is **not identified** (superseded late on 2026-10-29: it is the bundle block of
[equator-noise-bundle-block.md](equator-noise-bundle-block.md), fixed and host-verified by numbers on 2026-10-30). The lead ran a 39-agent investigation (with skeptic review) on top of the
reports above. No audio fault is logged in any of the 12 MASCHINE11 sessions examined, which does not rule out an audible
glitch (see 2026-10-28). After the skeptics, no hypothesis stands above about 25 %; the weights are the investigation's subjective estimates, not
measurements. What would separate them is the NoiseProbe (section "NoiseProbe diagnostic (2026-10-29)" above: keys, log lines, host recipe P0-P3,
decision rules; implemented and unit-tested, not run on any host at the time of this note).

| # | Hypothesis | Weight | Separated by |
|---|---|---|---|
| 1 | Equator's own ambient one-shots at the port's boosted mix (crackles, water-drop ticks, metal) | 20-25 % | `amb+`/`amb~` events before orphan bursts; `NoiseProbeMuteAmbient`, `AmbientVolumeOffset=0` |
| 2 | Spectator call onset overshoot (Play before the occlusion result is applied) | 15-20 % | calls on against calls off (`SpectatorCalls`), `spec.on` frames |
| 3 | Physics-prop impacts | 10-12 % | `prop` events; `NoiseProbeMuteProps` |
| 4 | Spectator hard cut at death/respawn | 8-10 % | `spec-` reasons `dead`/`toggle`/`reset`; `NoiseProbeSpectatorFade` |
| 5 | A fault below Unity on the user's 5.1 / 48 kHz path | about 8 % | marks with a clean Capture WAV (the noise is not in Unity's mix) |

Refuted or judged negligible by the investigation: room-reverb and shot-acoustics parameter jumps, stun/tinnitus, exit flare hiss, gate sounds, the
voice limits, GC and main-thread stalls, DSP starvation, and the KeepReverb helper path (the reasoning is in the lead's scratch synthesis, which is not
tracked).

Facts established on the way:

- The user already ran a calls-off/calls-on A/B in the log `2026-10-29_11-35-17`: `SpectatorCalls=false` at 11:36:28 with an Equator raid at
  11:37:08-11:40:12 and 0 calls; `true` at 11:41:15 with a raid at 11:43:04-11:45:28 and 55 calls. **Nobody recorded what he heard** in either raid.
- The `KeepReverb=false` comparison covered at most 138 s of the 10:57 Equator raid, so the negative reading in "Two-voice cadence and negative reverb
  comparison" above rests on that stretch only, and the time the setting was switched is not recorded.
- The one-time missing stun screen-flash warning of 10:59:35.792 is not a stun marker. The same section reads it as proof that a stun effect occurred;
  that reading is withdrawn.
- Equator's `Books` and `CardBoard` impact banks are both null (39 Equator props, see release-sweep-20261029.md): those props are silent when they hit
  something, so they cannot be a source, while other prop types still can (hypothesis 3).
- Exposure is unequal between the conditions (about 4-6 min of Equator without calls against 23-25 min with calls in the logs): compare rates, not counts.

**Questions for the user** (the three decisive ones, then one practical ask):

1. In which of the two runs above, calls off (11:37-11:40) or calls on (11:43-11:45), did you hear the noise: in the first only, the second only, both, or neither?
2. When exactly was SurroundAudio `KeepReverb=false` during the 10:57 raid (it covered at most 138 s of it)?
3. What does it sound like (crackle, tick, click, hiss, thump), from which direction, and how long does it last?
4. Which key is free for the NoiseProbe mark (default `Pause`; a mouse side button works)? It is pressed 0.3-1.5 s after the noise.

## Host run of 52c8a28 and the user's captures - 2026-10-29 (evening)

**`8941caa` could not arm on GPU-2.** Equator raid of EFT instance `ee010ad5`, 20:46-20:49 (`logs/LogOutput-SPT-GPU-2-2026-10-29_20-46-22.log`):
`[NoiseProbe] the probe cannot arm (ArgumentException: AudioListener.GetOutputData failed; invalid channel); audio is unaffected` at 20:48:21.876.
SurroundAudio logs `Driver capabilities: Stereo` and `Speaker mode requested: Mode5point1, active: Mode5point1 @ 48000 Hz` on that host: the mixer runs
5.1 on a stereo device, `GetOutputData` reads the device output, and the 0-based channels 2-5 (the third to the sixth) are refused. That answers the first of
the unverified assumptions above (`GetOutputData` on channels 2-5 without a Unity error) with no, on a stereo device; on the user's 5.1 driver (`driver=Mode5point1
ch=6`, below) all six channels were read and his log holds no `GetOutputData` error. `52c8a28` fixes the stereo case: `ReadAudioFormat` probes the channels once
(`ReadableChannels`), `Read` loops over `_readChannels`, arming fails only when channel 0 is refused, and the armed line prints `ch=2/6` when the read and the
mix channel counts differ (the sample armed line in the log-line block above still shows the 5.1-driver form `ch=6`). What the Capture tap records on a stereo driver
is in "Stereo-driver fixes and the evidence" (2026-10-30, above): the filter tap delivers the device format, not the mix channels. xunit: 2,619 tests pass, 56 of them NoiseProbe tests.

**Host run of `52c8a28`** (lead, GPU-2; deploy 20:55, 255 files with 2 overwritten, ProductVersion `0.4.0+52c8a281cf765bcb13473104366f5a1cd06348f8`; EFT
instance `2896f154` started 20:59:11, log `LogOutput-SPT-GPU-2-2026-10-29_20-59-01.log`; plain PMC raid on Equator at 21:00, bots None, DeveloperMode on, the
NoiseProbe values set live with `persist=false`, so the host's `.cfg` kept Capture/false). Lines of that log (the BepInEx prefix `[Debug  :maschine-ArenaMapPort]`
is left out):

```
21:00:51.036 [NoiseProbe] armed mode=Detect map=arena_equator speakers=Mode5point1 driver=Stereo ch=2/6 rate=48000 dsp=1024 voices=196/256 calls=on mix=g2.0/o0.40/r2.50 roomShot=on roomReverb=on tap=readback ring=none
21:00:54.600 [NoiseProbe] selftest started: four 10 ms noise bursts at -18, -24, -30, -36 dBFS through an owned 2D source
21:00:56.107 [NoiseProbe] selftest -18dBFS PASS latency 17 ms over +17.6 dB
21:00:57.335 [NoiseProbe] selftest -24dBFS MISS block peak -27.9 dBFS, background peak -37.0 dBFS
21:00:58.542 [NoiseProbe] selftest -30dBFS MISS block peak -30.3 dBFS, background peak -38.2 dBFS
21:00:59.742 [NoiseProbe] selftest -36dBFS MISS block peak -36.0 dBFS, background peak -37.8 dBFS
21:00:59.744 [NoiseProbe] selftest done 1/4 detected; weakest detected -18dBFS
21:01:20.345 [NoiseProbe] armed mode=Capture map=arena_equator speakers=Mode5point1 driver=Stereo ch=2/6 rate=48000 dsp=1024 voices=196/256 calls=on mix=g2.0/o0.40/r2.50 roomShot=on roomReverb=on tap=readback+filter ring=15s
21:01:51.037 [NoiseProbe] minute map=arena_equator blocks=3526 cover=98% bursts peak=1 click=0 dropout=0 gap=0 clip=0 loud=0 suppressed=0 refractory=0 orphan=0 evOrphan=0 bgMedian=-51.9 hitch50=3 gc=0/0/0 spec=+26/-25 amb=+47 restarts=5 props=0 rooms=1 resets=0 probeMs=0.031/10.194
21:01:51.039 [NoiseProbe] calib minute=1 pk50=-37.0 pk99=-27.3 pk999=-23.6 over50=+0.0 over99=+8.0 over999=+12.2
21:02:07.538 [NoiseProbe] minute map=arena_equator blocks=989 cover=100% bursts peak=0 click=0 dropout=0 gap=0 clip=0 loud=0 suppressed=0 refractory=0 orphan=0 evOrphan=0 bgMedian=-48.1 hitch50=0 gc=0/0/0 spec=+6/-6 amb=+12 restarts=3 props=0 rooms=0 resets=0 probeMs=0.033/3.220 (final)
21:02:07.538 [NoiseProbe] disarmed (mode off)
```

Reading. The probe arms and runs on a stereo driver under a 5.1 speaker mode, reading the 2 device channels of the 6 mix channels; everything below covers
those two channels only. The self-test found 1 of 4, not the 3 of 4 the design and the HANDOFF checklist expected. The detector is not changed: it is
relative (PEAK needs at least 15 dB over the baseline peak, and a block peak of at least 0.03, which is -30.5 dBFS). The background peaks of the three MISS lines
were -37.0, -38.2 and -37.8 dBFS, and the `calib` line of the first minute reads `pk50=-37.0` (the median block peak), against the -60 dBFS bed the 3/4 was
designed on. The -24 and -30 dBFS blocks reached +9.1 and +7.9 dB over their background peak (computed from the quoted levels; 15 dB are required). The -36 dBFS burst
arrived at -36.0 dBFS, below the 0.03 floor, and misses at any background level. By the peak criterion and the quoted block peaks, -24 and -30 dBFS need a baseline
peak of about -43 and -45 dBFS or quieter (the rms and HF criteria are not checked), so the "3/4" holds on a quiet background; that is arithmetic, not a host reading. `bgMedian` is another statistic (the
median of the baseline rms): -51.9 dBFS in the first minute line of this raid, -45.8 dBFS in the 20:31:20 minute line of the user's second Capture run (below), whose
final partial minute with his mark reads -39.2; these figures are quoted for scale and are not the self-test's background peak. A quiet Detect result therefore
claims only what the background level allows: read the `background peak` of the self-test lines before reading a MISS as a blind probe, and calibrate the
thresholds on the first minutes (recipe P1) before counting orphans. The HANDOFF expectation "3/4 with -36 MISS" is corrected accordingly.

**Detect to Capture.** The switch at 21:01:20 (`config ... NoiseProbe Detect -> Capture`, not saved) logged the armed line again with `tap=readback+filter ring=15s`.
`Frame()` re-attaches the tap and logs the line on a mode change; it does not reset the detector or its minute clock. The minute line of 21:01:51.037 (exactly 60 s
after the Detect arming) therefore spans 29 s of Detect, the self-test included, and 31 s of Capture; the final line of 21:02:07.538 (989 blocks, about 17 s) is all
Capture and also reads `gap=0` and `orphan=0`. The evidence records no self-test in Capture, so the `tap=` figure that shows whether the filter tap sees
mixer-routed content was not read there. `probeMs=0.031/10.194` is the probe's own cost per frame, average and worst frame (what the worst frame was is not
investigated). The three raids after it (Bay5 21:02, Air Pit 21:04, Bowl 21:05) ran with `NoiseProbe=Off` and logged no `[NoiseProbe]` line. This was a functional check
without bots: nobody listened and no noise was hunted.

**The user's captures.** On 2026-10-29 the user heard the noise in a plain PMC raid on Equator while standing still and ran `NoiseProbe=Capture` twice (log
`D:\SPT41\BepInEx\LogOutput-MASCHINE11-2026-10-29_20-21-38.log`). Build: `maschine-ArenaMapPort.Client.dll` 0.4.0 of 2026-10-29 16:41 (the `694143d`/`8941caa`
code, before the stereo fix, so the armed line has no `ch=x/y` form), Arena 0.6.0.0.47752, SurroundAudio 1.0.0, DeveloperMode on. Lines of that log (prefix left out):

```
20:25:19.611 [SpectatorAudio] arena_equator: 13 participating clips, 99 positions, up to 2 simultaneous voices
20:25:34.572 [NoiseProbe] armed mode=Capture map=arena_equator speakers=Mode5point1 driver=Mode5point1 ch=6 rate=48000 dsp=1024 voices=196/256 calls=on mix=g2.0/o0.40/r2.50 roomShot=on roomReverb=on tap=readback+filter ring=15s
20:28:45.918 [NoiseProbe] MARK #1 20:28:45.918 frame=28421 pos=(20.9,5.5,8.8) room=19/IndoorConcrete fps=82 voices=1 (the reaction lag is 0.3-1.5 s)
20:30:20.254 [NoiseProbe] armed [the same line as at 20:25:34.572]
20:31:20.260 [NoiseProbe] minute map=arena_equator blocks=3844 cover=97% bursts peak=2 click=6 dropout=0 gap=0 clip=0 loud=0 suppressed=0 refractory=7 orphan=0 evOrphan=2 bgMedian=-45.8 hitch50=3 gc=0/0/0 spec=+22/-21 amb=+41 restarts=7 props=0 rooms=4 resets=0 probeMs=0.058/1.667
20:31:47.597 [NoiseProbe] MARK #1 20:31:47.597 frame=47015 pos=(6.0,0.1,31.5) room=5/IndoorGeneric fps=139 voices=1 (the reaction lag is 0.3-1.5 s)
```

Both raids logged a "MARK #1": the first at 20:28:45.918 in the first raid (armed 20:25:34, ended 20:28:52), the second at 20:31:47.597
in the second raid (armed 20:30:20, ended 20:32:00). The four captures under `BepInEx/logs/ArenaMapPort-noise/` are `noise-20261029-202805-auto1`, `-202845-mark1`,
`-203147-mark1` and `-203156-auto1` (each 12.0 s, 6 channels, 6.6 MB). He names `noise-20261029-203147-mark1.wav` as a recording in which it is well audible; his word for
the sound is "Rauschen" (a hiss or rush of noise), direction and duration are not recorded. The mark key worked, the captures reached the mirror (the Syncthing item
of the unverified assumptions is answered) and the crowd schedule was the shipped one (`up to 2 simultaneous voices`). Both armed lines read `calls=on`. The
`bgMedian` of the first raid's minute lines is -34.7, -29.7, -29.2 and, in the final partial minute, -27.2. A forensics workflow over
the captures is in progress; its result is to be appended here, and the ranked hypotheses above stay as they are until then.

## Result of the captures: the raw bytes were the cause - 2026-10-29 (late evening)

The forensics over the four captures found the cause, and it is neither the crowd nor the mix. The loud static in `noise-20261029-203147-mark1.wav` (WAV
t 6.0-9.68 s, the chopped part 8.727-9.6805 s is what the user reacted to) is bytes of the LZ4-compressed Equator scenes bundle played as if they were PCM
samples of the ambient clip `moll_indoor_wind_04` by the owned player `indoor_wind (4)`: block 6657 of `arena_equator_scenes.bundle` has the LZ4HC flag
with csize == usize, and Unity's player evidently takes such a block as stored. The NoiseProbe recorded it (the capture holds the whole episode), but
none of its taps could name it: the event ring logs `isPlaying` edges of owned one-shots only, the detector keys on transients, and the census counts
pooled sources, while this is a continuous plateau of one non-pooled looping player. Of the ranked hypotheses above, the one-shot family (#1) named the
right area, an owned Equator ambient player at the port's boosted range, but not the mechanism; the others are refuted for this static. The whole story
with its numbers, the UnityPy origin (`chunk_based_compress` writes a chunk compressed when its LZ4HC output is exactly the chunk size), the repair, the
validator, the release consequence and what stays open (the steady hiss, the click bursts, the boosted wind bed as a taste question) is in
[equator-noise-bundle-block.md](equator-noise-bundle-block.md); its fix was host-verified by numbers on 2026-10-30 ("Stereo-driver fixes and the evidence" above) and nobody has listened to a repaired build.

## Crowd murmur bed and longer call pauses - 2026-10-30

Status: specified by the lead and built in three parallel patches (pipeline, client, texts) on 2026-10-30; **no release candidate contains it yet; it ran on GPU-2 on 2026-10-30 (the host section at the end of this document) and
nobody has listened to it.** This section is the design and the measurements it stands on; the host run appends its own section, and the
release texts (README, FORGE, CHANGELOG, `docs/releases/v0.4.0.md`) say "not listened to" until then.

**Decision (user, 2026-10-30, paraphrased from German).** Asked whether a crowd murmur fits Arena's maps, he said he has never played EFT Arena and cannot judge it
precisely; it sounds good and should be implemented, and the pauses between the normal single calls should then be lengthened again. The 0.4.0 package of `833275d` (DLLs,
cut and VirusTotal scan) is superseded once these changes are in.

### Why a bed, and where the sound comes from

The calls are 13 short shouts (1.35-3.29 s); between them the stands are silent. Arena's own four levels carry no crowd-ambience emitter: the native audio data of
the four extracted packages has no crowd-like player (Bowl has four `arena_fans_peesound` one-shots, nothing else). The only crowd-ambience recording in Arena's files is
`arena_fans_ambient_postmatch` in `resources.assets`, which [arena-audio.md](arena-audio.md) catalogues as the post-match results-screen bed (the name says
`_postmatch`) and says is meant to run under that screen, not in a live round. The user chose to use it live on the maps, so the murmur is **an addition of the port, not a sound
Arena plays in these maps**, and the texts say so. The extraction reads the clip from `resources.assets` with UnityPy, like the arena-audio extractor, and does not take the
`.ogg` that extractor wrote. `resources.assets` is not among the stage-2 key-source files (`tools/bundle/key-sources.json` names only per-scene `sharedassets` streams;
checked by file name on 2026-10-30), so shipping a decoded copy does not repeat the trap of [gate-sounds.md](gate-sounds.md).

### The clip (measured offline by the lead, 2026-10-30)

| Property | Value |
|---|---|
| Length, format | 30.313 s, stereo, 44.1 kHz |
| Loudness | integrated -36.5 LUFS, rms -40.1 dBFS, peak -22.7 dBFS, loudness range 3.2 LU |
| Spectrum | 76 % of the energy in 200-1000 Hz (centroid 943 Hz), 0 % below 200 Hz, 1 % above 4 kHz |
| Stereo | L/R correlation 0.78 |
| Loop seam, raw | last 50 ms -40.4 dB against first 50 ms -44.0 dB (a 3.6 dB step); end sample -0.0014, start sample 0.0072 (a small click) |

It is a quiet, narrow-band wash of voices. Looped as it is, the seam would step and click once every 30 s on every stand.

### Spectator positions and the stands

The positions are the actor roots of the schema-4 crowd graphs (`actors[].nodes[0].position` with parent -1), the same positions `SpectatorAudio` receives. The lead's
k-means over x/z (indicative; the model's own rule below decides the final counts):

| Map | Fans | Height (y) | Groups found |
|---|---:|---|---|
| Equator | 99 | 5.4-16.1 | 4 stands of radius 13-17 m at about (-19, 10, -10), (17, 10, -11), (17, 9, 20), (-17, 9, 23) |
| Bay5 | 88 | -5 to 13.8 | 3-4 groups: (21, 3, -6) r 21 m, (-11, -1, 1) r 14 m, (-43, -1, 85) r 13 m and a small (-43, -2, 55) r 19 m |
| Air Pit | 99 | 8.5-20 | 4 stands of radius 26-34 m |
| Bowl | 99 | 11.9-25.2 | 4 stands of radius 45-64 m; with k = 6 the radii drop to 20-47 m |

The lead's figures are indicative, and Bay5's are container-local (its crowd container sits at (104.1, 6.4, -16.2); the other three containers are at the origin with the identity
rotation and scale 1). What the model's own rule (below, radius limit 25 m, at most six stands) forms from the four staged graphs, in the world coordinates the runtime hands it (actor
root plus 1.6 m of head height, which is also what the `[CrowdBed]` line prints; the test `TheFourRealCrowdsFormTwoToSixStandsEachMeetingTheRadiusRuleOrTheCap` pins the counts):

| Map | Fans | Stands | Radii (m) | Centres (x, y, z), west to east |
|---|---:|---:|---|---|
| Equator | 99 | 4 | 17.1 / 13.3 / 14.0 / 16.2 | (-19, 11, -10) (-17, 10, 23) (17, 11, 20) (17, 11, -11) |
| Bay5 | 88 | 4 | 18.8 / 14.5 / 13.3 / 20.7 | (61, 6, 38) (61, 7, 69) (93, 7, -15) (125, 11, -22) |
| Air Pit | 99 | 6 | 19.3 / 20.5 / 18.8 / 18.3 / 22.0 / 16.7 | (-45, 14, 8) (-34, 14, -26) (-24, 13, 40) (5, 16, -34) (10, 15, 30) (20, 13, -3) |
| Bowl | 99 | 6 (the cap) | 34.8 / 28.6 / 44.6 / 20.3 / 35.8 / 45.2 | (-87, 16, -62) (-74, 18, 28) (-45, 24, -113) (-4, 17, 36) (61, 17, -97) (84, 19, -1) |

### Design

- **Extraction (`tools/extract/extract_spectator_audio.py`).** The `extract` action also exports the bed from Arena's `resources.assets`: downmixed to mono, the loop seam
  fixed offline by a 500 ms equal-power crossfade of the clip's head into its tail (result 30.313 s minus 0.5 s, which loops without a step), written as
  `arena_fans_ambient_postmatch.wav` (16-bit PCM, 44.1 kHz) beside the 13 call WAVs. `spectator-audio.json` gains a `bed` object (name, file, channels 1, sample rate, frames,
  bytes, sha256, peak, rms, `seam_crossfade_ms` 500, the source `resources.assets` with the clip's path id, and the clip's own statistics before the downmix). Schema 1 and
  the 13 clips are unchanged, so a client can still read a manifest without `bed` and then simply has no bed. `validate()` requires the bed when present, with hash and size checks;
  `verify` reports it.
- **Staging.** The existing `ArenaMapPortStageSpectatorAudio` target already copies `spectator-audio.json` and `*.wav`; the bed travels into `dist/.../spectator-audio/` (core
  part). The distribution grows from 255 to **256 files** and the core part from 153 to **154** (Equator 24, Bay5 30, Air Pit 22, Bowl 26 unchanged); the packer's
  `spectator audio` rule, `validate_distribution.py` (which checks the staged folder against `work/extract/spectator-audio`) and their tests know the new file.
- **Stands (`SpectatorCrowdBedModel.cs`, Unity-free, linked into the tests).** Deterministic k-means over x/z with fixed seeding (no unseeded `System.Random`): k from 2
  upward until every cluster radius is at most 25 m (30 m in the first draft, which gave Equator two stands instead of its four natural ones, see "Review of the three patches") or k reaches 6. A stand's centre is the mean position (x, mean y of its members, z). Per stand i of k:
  `minDistance = clamp(radius, 8, 60)`, `maxDistance = minDistance + CrowdBedReach`, start offset `i/k` of the loop length, pitch `0.98 + 0.04 * i/(k-1)`, so the stands
  neither repeat in step nor sound identical. The model also parses the manifest's `bed` and owns the excitement smoothing and the volume law.
- **Sources (`SpectatorCrowdBed.cs`, Unity glue).** One owned GameObject and AudioSource per stand, created after the crowd exists and removed with it (also on the raid
  audio reset that rebuilds the call voices; the bed is rebuilt with them). Clip decoded from the WAV with the existing `PcmWav` helper; loop on, spatial blend 1, spread 70,
  doppler 0, priority 200. **The distance law is a Custom rolloff curve, not Logarithmic:** Unity's Logarithmic mode stops attenuating at the maximum distance and holds that
  level for ever (about -10 dB across the whole map), so the curve is full volume inside the stand's minimum distance, 1/d beyond it and a taper to silence at the maximum distance
  (flat tangents on the two plateau keys, because smoothed tangents overshoot gain 1 by 7 to 14 percent). **`AudioSource.volume` cannot exceed 1**, but `CrowdBedVolume` goes to 2
  and the excitement factor to 1.6: the decoded clip carries a fixed gain of 3.2 (2 x 1.6) and the source volume is `volume x factor / 3.2` (0.3125 at the defaults, which is the
  `vol=` the NoiseProbe prints for a stand), so the level that reaches the listener is the clip's own level x `CrowdBedVolume` x the excitement factor. Output goes to the
  Environment mixer group the calls use (`BetterAudio.SourceGroups`, read the way `SpectatorAudio` reads it; the bed waits for BetterAudio and for that group and has no fallback
  to another group). No occlusion raycasts and no scene search; the only per-frame work is the allocation-free excitement smoothing, and a watchdog restarts a stopped stand
  source (at most 8 times per raid).
- **Excitement.** A target in 0..1, smoothed with a 2 s time constant, mapped to a volume factor between 1.0 and 1.6. Nothing sets it yet, so the murmur stays at the base level.
- **Keys (developer-only, honoured only with DeveloperMode, section `Spectators`, none public).** `CrowdBed` (bool, true, live: creates and destroys the sources),
  `CrowdBedVolume` (float 1.0, range 0-2, live), `CrowdBedReach` (float 60 m, range 10-150, live). All are new keys; no existing default changes.
- **Public hook for mode plugins.** `ArenaMapPort.Client.SpectatorCrowd`: `ContractVersion = 1`, `IsAvailable`, `SetExcitement(float level01)`, `Excitement`. Append-only like
  `ScoreMonitors` (a member count pin fails on a new member; `ContractVersion` changes only on a removal or a change of meaning). ArenaMode has not adopted it, and it is never
  edited from here.
- **Logging and NoiseProbe.** One Debug line per raid, `[CrowdBed] <map>: N stands (radii ..., centres ...), clip X s mono, volume V`, and one on removal; a manifest
  without a bed is one Debug line, not a Warning. The NoiseProbe gets `NoiseEv.Bed` (texts `bed+` and `bed-`) with one-line taps behind `if (NoiseProbe.Armed)` at the
  start and stop of each stand source, under the existing tap rules.
- **Tests.** The model (clustering on the four real position sets when the dist graphs are present, synthetic otherwise; determinism; per-stand parameters; manifest parsing with
  and without `bed`; excitement smoothing), source pins (owned sources only, no `FindObjectsOfType`, keys bound through `Settings`, install order) and the contract pin.

**First extraction (pipeline patch, 2026-10-30, written outside the repository; two runs gave byte-identical WAV and manifest).** The bed read from `resources.assets` (path id 5167)
is 1,336,816 frames of stereo at 44.1 kHz before processing; the mean downmix at 0 dB gain and a crossfade of 22,050 frames (500 ms) leave **1,314,766 mono frames (29.813 s,
2,629,576 bytes with the 44-byte header)**, peak 0.0678 (-23.4 dBFS) and rms 0.00997 (-40.0 dBFS), against 0.0752 and 0.01054 before. The mono mix and the crossfade lower the peak by about 0.9 dB and the rms by about 0.5 dB. The SHA-256 is in the manifest's `bed` object.

### Longer pauses between the normal calls

| Constant | First 0.4.0 candidates | Now |
|---|---|---|
| Between two call starts | 1.6-3.2 s | **4.0-8.0 s** |
| First call after the raid starts (`GameStatus.Started`), and after a restart | 2-5 s | **3-8 s** |
| Voices at once | 2 | 2 (unchanged) |
| Retry after a failed selection | 3 s | 3 s (unchanged, not a cadence) |

These are constants in `SpectatorAudioSchedule`, not config keys, so there is no `DefaultsRevision` re-seed; the tests that pin the cadence moved with them. Gain 0.5, the
13 clips, the speaker and family exclusions and the native room processing are unchanged. **A consequence worth knowing:** the 13 clips last 1.35-3.29 s and no call can start
less than 4.0 s after the previous one, so two calls no longer overlap in practice; the limit of two voices stays as a guard and the second slot is idle. The overlap of the
earlier schedules (about two voices on average at 0.8-1.6 s, still common at 1.6-3.2 s, estimated from the clip lengths) is gone, which is what the user asked for. The release texts say "one call every 4 to 8 seconds" and
no longer say that speakers overlap.

### Relation to the Equator static and the unexplained hiss

The murmur is a continuous, quiet voice wash centred on 200-1000 Hz, on every map. Two cautions for the listening and capture work still open: (1) the user's confirmation that the
repaired Equator bundle no longer produces the static ([equator-noise-bundle-block.md](equator-noise-bundle-block.md)) is easier on a build with `Spectators/CrowdBed` false (developer
key) or on the `833275d` DLL, because the bed is a steady background of its own; (2) the "faint steady hiss" that was seen in one recording and is not explained by the bundle fix
was recorded on a build that had no bed, so the bed cannot be its source. The bed's stand sources are owned, non-pooled looping players, which the NoiseProbe's pooled-source walk cannot see: the census therefore prints `ownedBed=<playing>/<stands>`
(next to `ownedAmbient=` and `ownedSpectator=`) and the event ring has `bed+`/`bed-` for every start and stop.

### Host verification (pending; nothing below has been run)

1. Full deploy through `tools/deploy-to-spt.ps1` (the core part gains a file, so not `-DllOnly`) and an EFT restart for the client DLL; the server DLL does not change. Check
   `uibridge_status` (loaded MVID equals the build).
2. One plain PMC raid per map, bots None, about a minute each: the `[CrowdBed]` line (stand count, radii and centres against the model's table above, which is in world coordinates: 4, 4, 6 and 6 stands, and Bay5's centres are not the
   container-local figures of the lead's table), no ArenaMapPort
   Warning or Error, the bed's sources present and playing, gone after leaving the raid; the hideout once.
3. The live keys: `CrowdBed` false then true, `CrowdBedVolume` 0, 1 and 2, `CrowdBedReach` at 10, 60 and 150 m, each seen in the process.
4. Level and reach by numbers: NoiseProbe `Capture` at one fixed spot, the bed off and on, and the RMS difference of the two captures; the clip's own rms is -40.1 dBFS
   before gain, attenuation and the stand distance.
5. Frame time before and after (`uibridge_status` `max`, ModProfiler's `max_ms_in_frame`) and the raid-start load line.
6. The call cadence: the `[SpectatorAudio]` start times in a debug log read 4.0-8.0 s apart with a first call 3-8 s after the raid starts.
7. Excitement: a developer-side call of `SpectatorCrowd.SetExcitement` (no caller exists yet) and a level change within about 2 s; unit-tested, not yet exercised in game.
8. Listening: open. The user's ear is the only evidence for loudness, reach and whether the murmur belongs.
9. The census: at a NoiseProbe mark the `census=[...]` text ends with `ownedBed=n/m` and n equals m while the bed plays (the bed is invisible to the pooled-source count).
10. The watchdog: whether a stand source's `isPlaying` stays true under `AudioListener.pause` (the pause menu). If it does not, the watchdog restarts a paused source at offset 0
    up to 8 times per raid; the Debug line `stand N had stopped; restarted` would show it.

### Review of the three patches (2026-10-30)

Two reviews of the pipeline, client and texts patches (applied to `6f36ecf`) found no blocking defect. What the combined patch changed because of them, and what it left open:

- **Stand rule 25 m, not 30 m.** The first draft's limit of 30 m gave Equator two stands (radii 29.1 and 26.9 m, centres (-18, 11, 5) and (17, 11, 2), each 15-18 m from a real stand), where
  the lead's own figures describe four natural stands of 13-17 m. The largest radius per k, from the reviewer's offline port of the same k-means (it matches the C# result at the final k):

  | Map | k=2 | k=3 | k=4 | k=5 | k=6 |
  |---|---:|---:|---:|---:|---:|
  | Equator | 29.1 | 25.6 | 17.1 | 17.1 | 11.5 |
  | Bay5 | 43.2 | 43.2 | 20.7 | 18.8 | 18.8 |
  | Air Pit | 45.5 | 40.8 | 32.9 | 26.6 | 22.0 |
  | Bowl | 107.7 | 80.5 | 63.7 | 63.7 | 45.2 |

  At 30 m the counts are 2 / 4 / 5 / 6, at 25 m they are 4 / 4 / 6 / 6 (Equator, Bay5, Air Pit, Bowl); no single limit gives 4 everywhere. 25 m is `SpectatorCrowdBedModel.MaxStandRadius`, one constant;
  the pinned counts of the real-graph test and the synthetic rows follow it. It costs at most four more audio sources on a map (never more than six in all). This was the fixer's
  decision where the lead's text ("k from 2 until every radius is at most 30 m") contradicted the lead's own picture of four Equator stands; setting the constant back to 30 restores the
  literal rule.
- **Distance curve and gain** are documented as built (Custom curve, baked clip gain 3.2); the first draft of this section still said logarithmic and a fallback mixer group.
- **NoiseProbe.** The census prints `ownedBed=n/m` (finding: the pooled-source walk could not see the stand sources, the blind spot of the Equator investigation); the bed no longer notes
  `NoiseEv.Reset` (the one audio reset was counted twice, `resets=2`, since `SpectatorAudio` notes it already; `bed-` and `bed+` show the bed's own rebuild); the `Armed` check comes first in the
  stop tap.
- **Contract.** `SpectatorCrowd`'s promise that the excitement is reset when the raid's crowd is removed now also holds for a package without a bed or a bed that failed:
  `ArenaSpectators.OnDestroy` resets the state unless a newer raid's bed is alive.
- **Texts.** The first call comes 3-8 s after the *raid* starts (the schedule starts at `GameStatus.Started`), the generated core `README.txt` carries the "murmur and pauses are new and untuned, not
  listened to" sentence (pinned), the AGENTS NoiseProbe entry lists the new tap file, `arena-audio.md` points to the user's decision, and the unit fixture of the manifest's bed has the shape the extractor writes.
- **Open, for the host and the user's ear.**
  (1) *Level.* At the defaults the murmur arrives at about -40 dBFS rms at a stand's centre (the clip's own rms; the extractor's `BED_GAIN_DB` is 0 and the mono mix costs 0.5 dB), roughly 12 dB below a
  call at the same distance (estimated, not measured), up to +10 dB at `CrowdBedVolume` 2 with full excitement, then 1/d beyond the stand radius. If it is inaudible on the host, add make-up gain in the
  extractor (the mono WAV peaks at -23.4 dBFS, so there is room), re-extract, re-stage and re-cut; do not widen the key range or the excitement factor without moving `ClipGain`.
  (2) *Swells at the loop start.* The first 1.2 s of the bed has windows 6-9 dB above the median (at about 0.35-0.45 s and 0.9-1.15 s) and one more sits at 16.45 s; they belong to the source's own opening, not to the
  crossfade. Every stand plays them once per 29.8 s loop, staggered by the start offsets; if they are heard as a pulse, rotate the loop start in the extractor and re-cut.
  (3) The watchdog's behaviour under `AudioListener.pause` is untested (host item 10). (4) The clip is uploaded (5.3 MB of floats) while the raid loads and freed again if EFT's raid-start audio reset
  fires; wasted, harmless, left as it is.

### Host run of the crowd bed build, GPU-2, 2026-10-30 10:46-11:00 (lead)

Build: `crowd-all.patch` on `6f36ecf` (the commit that carries this paragraph), `extract_spectator_audio.py extract` on the VM
(`SPECTATOR_AUDIO_OK 13 clips, 2815818 bytes; bed arena_fans_ambient_postmatch 29.813 s mono, 2629576 bytes, sha256
f406076a048ba0f5567f74b1044eed5e87fda7be4d49b9c440bc222f6ac27332`), Release client build with `DllOnly` (0 warnings; `dist/` 256 files, `spectator-audio/` 15
files), `validate_distribution.py` `DISTRIBUTION_OK 4 maps 12 bundles 4 sidecars encrypted`, xunit 2,678 OK, Python 2,441 OK (63 skipped). Deploy 10:46 (1 new
file, 3 overwritten), the three files identical on the host after 8 s, EFT started 10:49:50 (instance `328f1b40`, UiBridge 0.9.7). Plain PMC raids, bots None, 12:00,
clear; `NoiseProbe` Detect for the Equator level measurement (set with `persist=false`), `Spectators/CrowdBed` at its default. Nobody listened; everything is logged.

| Raid | `[CrowdBed]` line (stands, radii, world centres) | Crowd load | Perf (first 10 s window) | RAM |
|---|---|---|---|---|
| Equator 10:50 | 4 stands (radii 17.1/13.3/14.0/16.2 m, centres (-19,11,-10) (-17,10,23) (17,11,20) (17,11,-11)), clip 29.8 s mono, volume 1.00, reach 60 m | `load total=2172 ms` (worker, CNG) | 120 fps, mean 8.3 ms, max 9.8 ms (27 s in) | 9022 MB |
| Bay5 10:55 | 4 stands (radii 18.8/14.5/13.3/20.7 m, centres (61,6,38) (61,7,69) (93,7,-15) (125,11,-22)); the Bay5 crowd container sits at (104.1, 6.4, -16.2) | 2119 ms | 109.9 fps, max 475.8 ms (RainCover bootstrap, pre-existing) | 11871 MB |
| Air Pit 10:56 | 6 stands (radii 19.3/20.5/18.8/18.3/22.0/16.7 m, centres (-45,14,8) (-34,14,-26) (-24,13,40) (5,16,-34) (10,15,30) (20,13,-3)) | 2110 ms | 97.3 fps, max 1766.4 ms (IndoorSurface, pre-existing) | 12094 MB |
| Bowl 10:58 | 6 stands (radii 34.8/28.6/44.6/20.3/35.8/45.2 m, centres (-87,16,-62) (-74,18,28) (-45,24,-113) (-4,17,36) (61,17,-97) (84,19,-1)) | 2234 ms | 74.1 fps, max 3037.7 ms (IndoorSurface, pre-existing) | 12581 MB |

Every raid also logged `[SpectatorAudio] <map>: 13 participating clips, N positions, up to 2 simultaneous voices` and, 8-14 s after the crowd line,
`[CrowdBed] <map>: audio configuration changed (device changed=False); the stand sources and the clip are rebuilt` (the raid-start audio reset). The hideout
opened and closed once at 10:59-11:00. The whole process logged 0 ArenaMapPort warnings or errors (Unity's `refrigerator_drink` LOD lines on Equator only).

Level and cadence on Equator at the user's spot (teleport to (6.0, 0.1, 31.5); `NoiseProbe` minute lines, `bgMedian` of the stereo device mix): bed ON
10:51:08-10:52:08 `bgMedian=-43.0` with `spec=+9/-9` (nine calls a minute against 24-26 at the old cadence); `Spectators/CrowdBed` set false at 10:52:40 (the key
is polled, so it took effect without a change handler: the census read `ownedBed=4/4` at 10:52:25 and `ownedBed=0/4` at 10:53:03); the mixed minute read
`-37.3` (`loud=1`, one-shots at 7 m); bed OFF 10:53:08-10:54:08 `bgMedian=-43.9`. At volume 1.0 the bed adds about 1 dB to the median at that spot: subtle by
design (the clip's own level), `CrowdBedVolume` up to 2.0 is live. Not measured: the level at a stand centre, the loop-start swells, `SetExcitement` in game.

## 2026-11-02 - Corrections from the crowd-reaction analysis

A read-only analysis of action-bound spectator calls (workflow `wf_3515f8ba-a57`, plan [../PLAN-CROWD-REACTIONS.md](../PLAN-CROWD-REACTIONS.md)) corrects
two statements of this record; the earlier sections stay as written.

- **Bank names (line 13).** `fan_speaker_voice.bundle` has no `StartRound` bank. Its ten banks are LowDeathIntensity 1116, Cheer 1119, Owned 1120,
  Angry 1121, Neutral 1122, Participating 1123, Hide 1124, ChangedRound 1125, EndRound 1126 and PurifiedTeam 1127 (136 distinct mono clips, 254
  slots). `StartRound` (1109) exists only in the announcer and effects bundles.
- **Crowd ambience (the "Crowd murmur bed" section).** Arena's maps do carry in-match crowd beds: `ambient_voice.bundle` holds 7 stereo beds
  (Cheer 1119: 3 x 7.0 s, about -22.5 dBFS RMS; Angry 1121: 3 x 8.0 s, -26 to -30 dBFS; Neutral 1122: 1 x 80.0 s, -31.1 dBFS) on `FansAmbient`
  emitters in every sound scene (Equator 6 in level56, Bay5 6 in level188, Air Pit 12 in level39, Bowl 6 in level111, Iceberg 12 in level259),
  inactive in the imported scenes. What the data supports is narrower: `arena_fans_ambient_postmatch` is the only crowd clip not bound to a
  match-event trigger. The released 0.4.0 texts carry the broader sentence; the next release texts correct it (list in the plan, section 2.1).

## 2026-11-02 - Neutral bed replaces the postmatch bed

User decision after listening to the full crowd export (`work/extract/crowd-sounds-listening`, with the user's own German subtitles): "arena_fans_ambient_neutral
ist relativ ähnlich zu arena_fans_ambient_postmatch einen tick aufgeregter, ich würde die bisherige schleife dadurch ersetzen".

- **Source.** `ambient_voice.bundle`, bank `Neutral` (trigger 1122), its one embedded clip `arena_fans_ambient_neutral`: 80.0 s, stereo 44.1 kHz,
  channel correlation 0.976, streamed from a CAB resource inside the bundle (no key-source file). `find_bed` checks the bank and trigger, that the
  bank holds exactly this one embedded clip, and the protected-name rule for the bundle and the stream.
- **Processing.** The same mean downmix and 500 ms equal-power seam as before (loop jump 0.84 of a sample step, limit 3; the raw clip alone 2.46). The
  downmix measures -31.17 dBFS RMS against -40.03 dBFS for the approved postmatch bed, so `BED_GAIN_DB` is -8.9 dB: the character changes, the
  loudness the user approved on 2026-10-30 does not (`Spectators/CrowdBedVolume` raises it, up to 2.0). Result `arena_fans_ambient_neutral.wav`:
  3,505,950 frames (79.500 s), 7,011,944 bytes, peak 0.0688, RMS 0.00992 (the postmatch bed: 0.0678 and 0.00997), SHA-256 `928efb0a...`.
- **Wiring.** `BED_MAX_FRAMES` 40 -> 90 s (the client accepts 10-120 s); `SpectatorAudioBed.ClipName` is the new name, so an old manifest gives no bed
  instead of a wrong one; `RETIRED_BED_FILES` makes the extractor delete the old WAV from its output, and the client staging deletes it from `dist/`
  (the copy never removes files and `validate_distribution.py` checks the exact set). An installed old copy is ignored by the client. The packer
  rule and the required core file name follow. The distribution keeps 283 files.
- **Gates.** .NET 2,889 passed; `NoiseProbeModelTests.TheEventRingKeepsTheLastThousandEntriesInOrderAndNeverAllocates` was flaky on its own (one of
  three isolated runs charged 5,416 bytes of on-stack-replacement JIT work to the thread) and now measures in an `AggressiveOptimization` helper
  (0 of 15 isolated runs failed). `test_spectator_audio` (20, including the real-Arena bed check), `test_release_zips`, `test_validate_distribution`
  OK; `DISTRIBUTION_OK 5 maps 15 bundles 5 sidecars encrypted`.
- **Host** (GPU-2, deploy of 4 files, `deploy-wait --server` then `gpu-run start`, 16/16 identical; Equator without bots 17:28, noon, clear):
  `[CrowdBed] arena_equator: 4 stands (radii 17.1/13.3/14.0/16.2 m ...), clip 79.5 s mono, volume 1.00, reach 60 m`; the crowd `load total=2209 ms`
  (`audio=87`); the audio reset at 17:28:05.7 rebuilt clip and sources behind the deploy screen; 0 warnings and errors from mods; 120.0 fps over
  10 s (max 10.7 ms). The one long frame of the raid's first window (934.5 ms) was `[IndoorSurface] ... elapsedMs=870` at 17:28:20, the known
  pre-existing classification freeze, not the bed. Hideout opened and closed. Not listened to on the host; the user's ear decides.

## 2026-11-02 - Action-bound reactions, phase A (offline)

Status: **implemented and reviewed offline on 2026-11-02 (branch `feature/iceberg`); not committed, not deployed to the host, not run in the game, and nobody has listened
to a reaction.** The design, the data audit and decisions D1-D14 are in [../PLAN-CROWD-REACTIONS.md](../PLAN-CROWD-REACTIONS.md); this section is the implementation record. It was built by
a three-stage workflow (data pipeline, Unity-free model and public contract, Unity glue) and then one review round, whose findings and three lead decisions are applied below.
ArenaMode (`D:\SPT41\Development\ArenaMode`) is not touched and has not adopted the contract: the only caller today is the developer demo.

### Decision (user, 2026-11-02)

After the plan with his subtitles (section 2.4: 19 `listen` clips among the 89 usable clips of the six phase-A banks) the user answered: "dann lassen wir die 19 clips erstmal weg. gerne wie
empfohlen mit der umsetzung beginnen" (leave the 19 clips out for now; begin the implementation as recommended). So:
- Phase A exports only the **70 `keep` clips** of the six banks Cheer, Owned, Angry, ChangedRound, EndRound and Hide. The 19 `listen` clips stay out until the user has listened (a clip is
  promoted by its status in `tools/extract/reaction_pools.json`), the 2 SLUR clips stay out for good. LowDeathIntensity (Lull) and PurifiedTeam are not exported: Lull follows after phase A with
  its trigger on ArenaMode's side (D14), PurifiedTeam waits for an ArenaMode event.
- Every other recommendation of plan section 8 applies: gain 0.5 (D4), the external Scav references stay excluded (D2), no automatic excitement from gunfire (D6), no win/loss colouring of
  Finish (D7), no animation (D9), developer keys only and no public key (D10), reactions are allowed while the local player is dead (D12). The plugin's own sensors are phase C and not part of
  this work: there is no `ReactionAuto` key.

### The clips (counts only; names and tags, never voice text)

| Bank (trigger) | Embedded | Exported (`keep`) | Left out |
|---|---:|---:|---|
| Cheer (1119) | 14 | 10 | 4 `listen` |
| Owned (1120) | 16 | 12 | 4 `listen` |
| Angry (1121) | 18 | 14 | 4 `listen` |
| ChangedRound (1125) | 9 | 6 | 3 `listen` |
| EndRound (1126) | 11 | 9 | 2 `listen` |
| Hide (1124) | 36 | 29 | 5 `listen`, 2 `out` (SLUR) |

The union is **70 clips**: Cheer's 10 are all in Owned's 12 (Cheer and Owned are one pool, 12 clips), so 12 + 14 + 6 + 9 + 29 = 70. Pools of the exported clips (tags from the tracked sidecar,
sentence groups = clips that say the same sentence, voice family = the `fanN` of the name, one recorded voice):

| Kind | Bank(s) | Pools (clips) | Sentence groups | Voice families |
|---|---|---|---:|---|
| 1 Approve | Cheer + Owned | praise 6, gloat 4, shout 2, urge 0 | 11 | |
| 2 Disapprove | Angry | lament 10, heckle 4 | 12 | |
| 3 TakeCover | Hide | panic 12, directed 17 | 18 (panic 6, directed 12) | |
| 4 Start | ChangedRound | start 6 | 5 | 4 (fan12 three clips; fan7, fan11, fan13 one each) |
| 5 Finish | EndRound | finish 9 | 9 | |

Export statistics (the extractor's own run and `verify`): 174.98 s, 15,436,496 WAV bytes; shortest 0.701 s (`arena_fan13_kill_angry_08`, 30,912 frames), longest 6.713 s
(`arena_fan12_kill_cheer_11`, 296,045 frames), cap 7 s (308,700 frames); 23 of the 70 needed the ffmpeg resample; the constant gains run from -9.85 dB to +1.02 dB, so no clip met the +6 dB
boost limit. Format and `normalize()` are the calls' (mono 44.1 kHz PCM16, -22 dBFS RMS, peak at most -3 dBFS). All 70 stream from a `CAB-*.resource` inside the fan bundle, none from a stage-2
key-source file.

The tracked sidecar `tools/extract/reaction_pools.json` (schema 1) carries, for every embedded clip of the eight reaction banks (122 names: 86 `keep`, 29 `listen`, 7 `out`), the banks, the
pool tag, the sentence group (`approve-03`, `takecover-11`, ...), the status and a reason code (FLAG, ROLE, TASTE, CTX, SIDED for `listen`; SLUR, UNCL, WRONG for `out`). `check_pools` closes
every vocabulary, requires name category, bank and pool tag to agree, and pins the sidecar's `about` prose by SHA-256 (`POOL_ABOUT_SHA256`), so no free text can enter it.

### The manifest

`spectator-audio.json` gets an optional `reactions` object beside `bed` (schema 1 and every existing key stay; a manifest without `reactions` is still valid and the client then has no
reactions): `schema`, `source_bundle`, `max_frames` (308,700), `banks` (Cheer, Owned, Angry, ChangedRound, EndRound, Hide in that order, each with trigger, embedded count,
`external_entries_omitted`, `null_entries`, the exported `clips` and the `left_out` names with status and reason) and a deduplicated, name-sorted `clips` table (file, format, frames,
bytes, SHA-256, peak, RMS, `source_id`, `processing`, `pool`, `group`). Invariants the extractor and the client check: `embedded == len(clips) + len(left_out)` per bank; every bank clip is in
the table and every table clip in a bank; the pool tag belongs to the bank (Cheer/Owned praise|gloat|shout|urge, Angry lament|heckle, ChangedRound start, EndRound finish, Hide
panic|directed); the group id is `<kind>-NN` with the kind of its tag; names match `^arena_fan\d{1,2}_(?:kill_cheer|kill_angry|match_start|match_end|aim_hide|shoot_hide)_\d{2}$`; frames
22,050 to `max_frames`; at most 256 clips. The extractor decodes and normalises everything in memory before it writes, then `remove_stale_reactions` deletes only unlisted files that fully
match the reaction name pattern.

Client side, `SpectatorAudioManifest.ValidateReactions()` is independent of `Validate()` and `ValidateBed()` in both directions: a damaged reaction set never takes the calls or the bed with
it, and the reverse. It also requires the manifest's own `schema == 1`, `trigger == 1123` and `bank == "Participating"` (the calls' identity), the six known banks with their triggers and
`max_frames == 308700`. `SpectatorAudioSchedule.Hold(now, seconds)` is the one new schedule member (see the runtime).

The key-source guard is new for this audio: the extractor checks the stream source of every clip that would ship, and `validate_distribution.py` refuses a staged spectator WAV that is a copy
of a key-source file of the maps (size comparison first, a hash only on a size match; it needs the installed Arena's data folder, which `verify-keys` needs anyway).

### The public contract (`SpectatorCrowd`, `ContractVersion` still 1)

Four members are appended, so the class has **eight**: `ContractVersion`, `IsAvailable`, `Excitement`, `SetExcitement(float)` and `int ReactionKinds { get; }`,
`int ClaimReactions(string owner)`, `bool React(int claim, int kind, float strength01)`, `bool ReleaseReactions(int claim)`. All any-thread, state only, never throwing, public static with
primitive parameters; `SpectatorCrowdContractTests` pins the eight and keeps every prohibition (no fields, constants, events, generics, optional parameters). `IsAvailable` keeps its meaning (the
bed is on); reactions work with the bed off.
- **Kinds** are documented numbers 1 to 31, not constants (a kind is the bit `1 << kind` of `ReactionKinds`, range-checked before the shift): **1 Approve, 2 Disapprove, 3 TakeCover, 4 Start,
  5 Finish are implemented** (`ReactionKinds` = 62 for the phase-A set), **6 Lull and 7 PurifiedTeam are reserved** and absent from the mask, so `React` refuses them. The kind-to-bank table
  (1 Cheer+Owned, 2 Angry, 3 Hide, 4 ChangedRound, 5 EndRound) is pinned by a test. `ReactionKinds` is 0 on Iceberg, in a menu, on a vanilla map, without a reaction set, with the `Reactions`
  key off and while the component is disabled.
- **Claim:** a null or blank owner gives 0; so does `Kinds == 0`; the same owner gets the same handle back; the developer demo (owner `developer-demo`) yields to any other owner, whose claim voids
  its handle, and never takes the reactions from a plugin; any other holder refuses a different owner. Handles come from one process-wide counter (never 0 or negative), and one that was
  released, taken over or voided by the crowd's removal (`Reset`, a new generation) can never match a later claim. A lost claim shows up as `false` from `React`.
- **`React`:** false for a stale or foreign handle, a kind outside 1..31 or without its bit, NaN and a strength below 0.05; a larger strength is clamped to 1. True means queued, not played.
  The queue is a fixed ring of 16 under one lock; a full ring evicts the OLDEST request and counts it; each entry carries a `Stopwatch` stamp and the main-thread drain drops what is
  older than 2 s. `React` takes no Unity API and allocates nothing. The line-of-fire flag (`Directed`, TakeCover only) is not part of the public contract: the developer demo and the future
  plugin sensor use the internal overload. `ReleaseReactions` is true for the holder; requests already queued still play.

### Runtime (`CrowdReactionModel.cs`, Unity-free, and the reaction section of `SpectatorAudio.cs`)

**Planner** (`CrowdReactionPlanner`, deterministic for a seed, nothing per frame while nothing waits):
- **Coalescing and priority:** within one drain the largest strength per kind is kept (and the line-of-fire flag of any request); an accepted Start or Finish removes the Approve and Disapprove
  of the batch and the pending ones (drop reason `coalesced`; a Finish that its own cooldown dropped leaves the cheer for the deciding kill standing). A request for a kind that already waits is
  merged into it, and the merged request keeps the strength AND the deadline of the request that supplies the strength (on a tie the older). Priority Finish > Start > Approve = Disapprove
  (arrival order) > TakeCover > Lull > PurifiedTeam.
- **Cooldowns:** Approve and Disapprove 3 s, TakeCover 15 s, Start and Finish 5 s (Lull and PurifiedTeam 20 s, reserved); a strength of at least 0.8 halves the gap of THAT request (an
  important event is not swallowed by the cooldown of a small one); all times `Spectators/ReactionGapScale` (0 = none). A request of a kind on cooldown is dropped (`cooldown`).
- **Rate and age:** at most one reaction start per 0.75 s (not scaled); a pending request starts no later than 2 s after its `React` call, else `stale` (with a `Blocker` of `rate` or `noslot`).
- **Voices:** strength up to 1/3 gives 1, up to 2/3 gives 2, above 3; cut to the free reaction slots (3), none free = wait. Each further voice starts 0.12 to 0.45 s after the previous one.
- **Pools by band:** Approve below 0.8 draws praise and gloat for every voice; from 0.8 the FIRST voice draws from shout (+ urge once a listen clip is promoted) and the others from praise and gloat.
  Disapprove below 0.4 draws laments only, from 0.4 laments and heckles. TakeCover draws panic, plus directed only when the request is `Directed`. Start and Finish use their pool.
- **Clip choice:** the voice family `fanN` is drawn with a weight of the square root of its clip count in the candidates, then a clip of that family at random. Within one reaction no
  two voices share a family (voices that sound right now, calls included, count as used) and **no sentence group is ever repeated, at any step**. Across reactions of one kind the last two clips and
  the last three sentence groups (capped at one less than the groups the voices can draw from) are not repeated.
- **Relaxation order (lead decision, 2026-11-02):** when the rules cannot all hold for the voices the strength asks for, every step is tried at the full voice count before a voice is given
  up: (1) drop the last-two-clips rule, (2) shrink the sentence window one sentence at a time (3, 2, 1, none), (3) drop the voice-family rule, (4) a strong Approve's first voice may take the
  normal clips after its shouts. Only then does the reaction get fewer voices. The first order (which never relaxed the window) left every Start after a three-voice Start at two voices for the
  rest of the raid, because Start has 5 sentence groups. `CrowdReactionPlan` reports the step through `ClipsRelaxed`, `GroupWindow`, `FullGroupWindow`, `GroupsRelaxed`,
  `FamiliesRelaxed`, `FellBack` and `RelaxedText` (`Level` is the step number and depends on the pool's window, so tests read the flags). Under this order step (4) is a safety net that can
  essentially not be reached (a shout stays eligible at the last step); it is kept and documented on `FellBack`.
- **Started/Discard:** the Unity side confirms with `Started(plan, now, voices)` (voices clamped to 1..plan voices: only the clips and sentences that really sound enter the no-repeat history)
  or `Discard(plan, reason)`; a plan neither started nor discarded stays pending and expires by its age. Extra drop reasons beyond the spec: `nopool`, `coalesced`, `weak`.
- **Speakers** (`CrowdReactionSpeakers`): the calls' `ChooseSpeaker` law run on a SEPARATE schedule instance (so the calls' no-repeat memory is not disturbed), each further voice at least 4 m from
  the voices already picked; positions as a packed xyz array.

**Glue** (`SpectatorAudio`, one component, two independently validated sets):
- **Two sets:** the 13 calls and the reaction set validate separately and each fails with its own single Warning (`spectator reactions are unavailable (...)`; the calls and the crowd remain).
  `SpectatorAudio.Current` exists when either works. `_families` is a general `fanN_` parse (`CrowdReactionPool.FamilyOf`).
- **The drain sits BEFORE the calls' living-player guard on purpose:** `if (_reactionsReady) UpdateReactions();` runs before `GamePlayerOwner.MyPlayer` is read and before the guard that stops the
  calls. The reaction gate is: the `Reactions` key on, `Status == Started`, spatial audio services ready; it never asks `IsAlive`. The calls' `StopAllVoices` and `_schedule.Stop()` leave the
  reaction slots alone. `SpectatorReactionSourcePinTests` pins the order, the three own slots and that the reaction section (`UpdateReactions`) names neither `IsAlive` nor `MyPlayer` (the body is only the listener's fallback, `TryListener`).
  Not Started: nothing plays and what waited is dropped (`notstarted`, one Debug line with a count); services not up: voices stop and requests die by their own age.
- **Slots:** a separate pool of 3 reaction voices (so at most 5 voices from the shared Environment pool of 32). The playback path is the calls': an Environment `BetterSource`, the
  Environment mixer group, rolloff 90 m (Bowl 150 m), spatial blend 1, `EOcclusionTest.Combined`, gain `Spectators/ReactionVolume` (0.5); candidate range is 0.8 of the rolloff like the calls.
  Reaction voices are cut hard (no fade). A failed `GetSource` for the first voice gives up the reaction (`nosource`, no cooldown starts); a later voice is skipped and counted.
- **Listener:** `Camera.main` position, else the local player's body, else the reaction is dropped (`nolistener`). Where the listener sits in ArenaMode's spectator camera or a death replay is
  not known (host item).
- **Hold of the random calls:** a started reaction calls `schedule.Hold(now, reaction length + gap)`, gap 4 s after Approve, Start and Finish and 1 s after the others (`CrowdReactionPlanner.HoldGap`,
  the one table). `Hold` only postpones (a later `NextAt` stays) and is a no-op while the schedule is stopped; calls that already play finish by themselves, no cut, no new fade.
- **Swell:** a started reaction asks the bed for `SpectatorCrowd.State.RequestSwell(min(1, strength x ReactionSwell))`, only when a bed exists. `CrowdBedState` keeps the largest request until the
  bed's main-thread tick takes it (`TakeSwell`; discarded while the bed is off or the raid has not started). `CrowdExcitement.AddSwell`: linear attack of 0.3 s to the peak, then an
  exponential decay with a 3 s time constant; a smaller amplitude than the current swell is ignored; the volume factor follows `max(smoothed external level, swell)`, never above 1, so the factor
  stays at most 1.6 and `ClipGain` 3.2 is still valid. Allocation-free; `SpectatorCrowdBed.cs` keeps its three `Bind` calls.
- **Clips and reset:** `AudioClip`s are created lazily on first use and cleared with the calls' on an audio reset; the reset stops the reaction voices, the request queue survives it and entries
  older than 2 s are dropped. `OnDisable` withdraws the kinds (Update publishes them again); `OnDestroy` and `ArenaSpectators.OnDestroy` reset the queue and stop the demo whether or not the audio
  component exists.
- **Idle cost:** an idle frame in player mode is a handful of field reads (`HasWaiting` is a volatile mirror of the ring count, so no lock) and returns before any clock read, allocation or engine
  call; in developer mode each frame also reads the clock and the gap-scale key, ticks the demo and checks the drop ring.

### Worker load

`ArenaSpectators.LoadReactionPackage` reads, validates, SHA-256-hashes (through `CryptoProviders.Select(null)`, i.e. CNG) and PCM-decodes the 70 clips on the crowd worker, after the crowd
package; the worker never touches Unity and never logs, a failure travels in `ReactionPackage.Error`. If the worker did not run, the main thread loads them. `SpectatorAudio.Create` takes the
package from `crowd.ReactionData`. A package without a reaction set is `Absent` (one Debug line, not an error). The load summary gains `reactions=<ms>`. 15.4 MB more to read, hash and decode on
the worker: whether `load total=` stays near 2.1 s with `wait=0` is a host item.

### Developer keys and the demo

All in section `Spectators`, bound with `Plugin.Instance.Settings.Bind` in `SpectatorAudio.cs` and `SpectatorReactionDemo.cs` (not in `SpectatorCrowdBed.cs`), none public, honoured only with
DeveloperMode (outside it the defaults apply: reactions on, volume 0.5, swell 1, gap scale 1, demo off): `Reactions` (true, live), `ReactionVolume` (0.5, 0..1), `ReactionSwell` (1, 0..1,
0 = no swell), `ReactionGapScale` (1, 0..4, 0 = no cooldown), `ReactionDemo` (Off|Cycle|Once), `ReactionDemoKind` (0 = all, 1..5).

`SpectatorReactionDemo` (glue) and `SpectatorReactionDemoPlan` (Unity-free, linked into the tests) claim the reactions as `developer-demo` through the PUBLIC contract and fire one pass:
Approve and Disapprove at 0.30, 0.60 and 0.90 (1, 2, 3 voices; Disapprove laments only below 0.4; Approve with a shout first from 0.8), TakeCover at 0.50 once panic-only and once `Directed`
(internal overload), then Start and Finish at 1.0; steps 9 s apart (more than the longest reaction), about 100 s a pass; Cycle repeats, Once runs one pass of `ReactionDemoKind` or all kinds and
stops until the key goes Off and back. A test proves with the real planner that every step passes the cooldowns at gap scale 1 (6 filters, 12 seeds, 3 cycles, zero drops). It yields to a
plugin's claim, releases on Off and ticks only while the reactions run. The host trigger is `uibridge_config_set` (letter keys do not reach EFT).

### NoiseProbe and logging

NoiseProbe (all taps one-liners behind `if (NoiseProbe.Armed)`): `NoiseEv.ReactStart` / `ReactStop` appended (tokens `react+` and `react-`, both in `IsCause`); `NoiseText.ShortClip` strips only the
`arena_` prefix of reaction names (`fan11_kill_cheer_01`, greppable by category); the minute line gains ` react=+a/-b` after `spec=`, the position line `react=N`, the census
`ownedReaction=playing/active`; `ProbeVoices`, `AnyAudible` and `ActiveVoices` cover reaction voices. The armed line does not print the reaction state (its signature is pinned).

Logging is Debug with the tag `[SpectatorAudio]` and, for the per-reaction lines, only with DeveloperMode: one line per played reaction (`<map>: reaction <Kind>[ directed] strength=S, N voice(s)
[<clip> fan <stand> at <m> m +<delay> s, ...], calls held H s`, plus `, rules relaxed to level L (<RelaxedText>)` when a step was taken), one per dropped request (`reaction <Kind> strength=S
dropped (<reason>[, waited for rate|noslot])`; reasons `cooldown`, `rate`, `stale`, `noslot`, `nosource`, `nolistener`, `off`, `notstarted`, `nopool`, `coalesced`, `weak`), one load line per raid
(`<map>: N reaction clips, kinds mask M, up to 3 simultaneous reaction voices`), and the demo's own lines. Warnings: a damaged or unreadable reaction set (once, at load) and a playback failure
(once). Nothing logs from `React`, the drain's callers, the worker or the audio thread.

### Pipeline, staging and counts

- **Extractor** (`extract_spectator_audio.py`): `REACTION_BANKS`, the name regex, `load_pools`/`check_pools`, `find_reactions`, `reaction_object`, `validate(require_reactions, pools)`; `extract` then
  `verify` end `SPECTATOR_AUDIO_OK 13 clips, 2815818 bytes; bed ... 79.500 s ...; reactions 70 clips, 15436496 bytes`. The 13 calls and the bed are byte-identical to the copies already in `dist/`.
- **Client build:** the flat `*.wav` copy of the csproj stages the new files; a new prune deletes staged WAVs that the source folder no longer has (a clip demoted in the sidecar), because the copy
  never removes anything and a stray WAV would fail `validate_distribution.py`'s exact file set.
- **Packer:** the spectator-audio rule admits the six categories with the calls' two-digit suffix and still refuses an unknown category (its local `SPECTATOR_REACTION_CATEGORIES` list is pinned
  equal to the extractor's); reaction names are not in `required_files` (the manifest names them).
- **Counts (read from `make_release_zips.py --plan` on 2026-11-02):** `spectator-audio/` holds **85 files** (13 calls + 70 reactions + the bed + the manifest); the whole `dist/` **353** (283 + 70);
  parts core 224 (was 154), equator 24, bay5 30, airpit 22, bowl 26, iceberg 27; the 0.4.0 layout without Iceberg would be 326 (256 + 70). Raw size 5,393,118,548 B in total.
- **`validate_distribution.py`:** `DISTRIBUTION_OK 5 maps 15 bundles 5 sidecars encrypted`.

### Review round (offline)

One review round of the three stages gave findings that changed code or tests; those the fix report itemises: **M1** the concurrency test now races four writers over 3,000 barrier-aligned rounds
(a racy max and a compare-exchange without the retry both fail it), **M3** the history is per kind (a test interleaves a 3-voice Disapprove between Finishes), **M4** the deadline follows the
strength in a merge, **M5** `Started(plan, now, voices)` for a reaction with fewer speakers than voices, **M9** the strict stand alternation (a test removes `_law.Played`), **P1** the sidecar's
`about` prose is pinned by hash, **P2** the key-source copy test skips when no key-source file is found in the installed Arena. Lead decisions: (1) the relaxation order above; (2) the three
allocation tests (queue, idle planner, swell), whose bound the glue stage had raised to 64 KiB, now measure inside `[MethodImpl(AggressiveOptimization)]` helpers with a 512 B bound (stable over 8 clean runs; under four parallel suite runs the allocation tests still
passed, while `Cng_ReachesAtLeast300MiBPerSecond` and once `OnLoad_WithoutAVerifiedInstallation...` failed in four of eight runs, load and shared-resource artefacts of that stress); (3) the two
cp1252 test errors below are not touched.

### Tests (2026-11-02, this VM)

- `dotnet build ArenaMapPort.Client -c Release -p:ArenaMapPortDllOnly=true`: 0 warnings, 0 errors.
- `dotnet test ArenaMapPort.Server.Tests -c Release`: **3,128 passed**, 0 failed, 0 skipped, twice in a row (3,119 before the review round, 3,073 after stage 1, 2,889 at the Neutral bed). New
  files: `CrowdReactionModelTests`, `SpectatorReactionDemoTests`, `SpectatorReactionPackageTests` (the worker's checks on the real 70 clips and the exact 85-file folder),
  `SpectatorReactionSourcePinTests`; changed: the contract pin (eight members), `SpectatorAudioTests` (`Hold`, the reaction manifest), `SpectatorCrowdBedModelTests` and
  `SpectatorCrowdBedSourcePinTests` (swell), `NoiseProbeModelTests`, `NoiseProbeSourcePinTests`, `SpectatorWorkerSourcePinTests`.
- `python -m unittest` on `test_spectator_audio` (56), `test_reaction_pools` (15, new), `test_release_zips` and `test_validate_distribution`: 323 tests OK in 348 s. The whole suite
  (`discover -s tools/tests -p "test_*.py"`): 2,851 tests in 884.7 s, errors=2, skipped=67. The two errors are `test_door_poses.SidecarTests.test_the_validator_run_passes` and
  `test_score_monitors.SidecarTests.test_the_validator_run_passes` (`AttributeError: 'NoneType' object has no attribute 'startswith'` from `done.stdout`): `validate_map_descriptors.py` prints an em
  dash that a piped cp1252 console cannot carry, and both pass with `PYTHONUTF8=1`. Neither the script nor the tests were touched by this work.

### Not yet host-verified, not listened to

**Nothing in this section has run in the game.** The offline gates above are all there is. What remains, from plan section 7 (GPU-2; first check that `windows-gpu-2` connects, `gpu-run` works
without it):
1. `uibridge_status`; deploy only through `tools/deploy-to-spt.ps1` (dry run first; the whole dist, because the manifest and 70 WAVs travel with it - a `-DllOnly` deploy leaves the host without
   a reaction set and the client then logs that it has none); EFT closed and the Syncthing sync finished; `gpu-run restart --host host2` for the new client DLL.
2. One raid without bots on each crowd map (Equator, Bay5, Air Pit, Bowl) with `Spectators/ReactionDemo=Cycle`: the Debug lines show every implemented kind with its clips, the set loads in the
   worker, `load total=` stays near 2.1 s with `wait=0`, no ArenaMapPort warning, no new hitch (`uibridge_status` max).
3. A reaction right after the crowd is built (the audio-reset window, 8 to 14 s).
4. A plain-raid local death: the reactions end with the raid, no exception.
5. Iceberg: `ReactionKinds == 0` and no warning.
6. NoiseProbe in `Detect`: reaction voices are no orphans and cause no new bursts.
7. The hideout once.
Also open for the host: `Camera.main` as the listener in a raid and in ArenaMode's spectator or replay view, that the raid stays `Started` across the body handover, `GetSource` headroom in the
Environment pool, and the `reactions=` figure of the load line. Phases B (ArenaMode binder and presentation service) and C (the plugin's own sensors) are not started.

**Listening, by the user (nothing may be called approved before it):** each kind alone with `Audio diagnostics/SpectatorCalls` off; loudness against the calls and the murmur, and the swell;
variety (Approve 12 clips in 11 sentences, Disapprove 14 in 12, Start 6 in 5 and TakeCover's panic pool 12 in 6 are the thinnest); reactions over the announcer in ArenaMode; behaviour while
dead; and the 19 `listen` clips, to promote or keep out.

### Corrections to older paragraphs of this record (appended, nothing above is rewritten)

Both items the plan names are already corrected in "2026-11-02 - Corrections from the crowd-reaction analysis" above; phase A rests on them:
- **Line 13** ("Other banks include event reactions such as `Cheer`, `Angry`, `StartRound` and `EndRound`"): the bank is **`ChangedRound`** (trigger 1125), not `StartRound`; the fan bundle has no
  `StartRound` bank. Phase A exports ChangedRound as kind 4 Start.
- **The "Crowd murmur bed" section** (its "Why a bed" paragraph says Arena's four levels carry no crowd-ambience emitter and that `arena_fans_ambient_postmatch` is the only crowd-ambience
  recording): wrong as written. Arena's `ambient_voice.bundle` holds 7 in-match stereo beds (Cheer 3, Angry 3, Neutral 1) on `FansAmbient` emitters that are inactive in the imported scenes;
  the postmatch clip is only the one crowd clip not bound to a match-event trigger, and since 2026-11-02 the murmur is the Neutral in-match bed. The Cheer and Angry beds stay unused (D3).
  The same sentence still stands in the released texts (README.md, FORGE.md, CHANGELOG.md, `docs/releases/v0.4.0.md`, whose wording is the user's decision) and in the older records
  `arena-audio.md` (note of 2026-10-30) and HANDOFF.md ("Crowd murmur bed" bullet).

### Host run on GPU-2 (2026-11-02, 21:09-21:20)

Build: the phase-A working tree on `08964c1` (client DLL `0.4.0+08964c1`; the commit that carries it follows this record). Deploy at about 21:08 through `tools/deploy-to-spt.ps1 -Confirm`
after a dry run: 353 files, 70 new (the reaction WAVs), 3 overwritten (client DLL, `spectator-audio.json`, and the server DLL, which differs from the host's only by its commit stamp
`0.4.0+902329f` -> `+08964c1`: no server source changed, no server restart needed). `gpu-run deploy-wait` (87 files identical after 2.8 s), `gpu-run start --host host2` (EFT up 21:09:21,
16/16 maschine plugins identical, VM build = host disk = loaded). DeveloperMode on; `Spectators/ReactionDemo = Cycle` set with `uibridge_config_set persist=false` (the host's .cfg keeps
`Off`). `windows-gpu-2` did not connect; gpu-run and uibridge-2 were enough. Plain PMC raids, bots None. Nobody listened (the host is not listened to).

| Map | Set loaded | `load total=` (worker) | Reactions seen | Frame max |
|---|---|---|---|---|
| Equator | 21:10:24 `70 reaction clips, kinds mask 62` | 2,160 ms (`wait=1`, `audio=44`, `reactions=27`) | two full Cycles: all five kinds, TakeCover panic and directed | 11.4 ms over 30 s |
| Bay5 | 21:14:55 | 2,129 ms (`wait=0`, `reactions=118`) | Approve 0.3 / 0.6 / 0.9 with 1 / 2 / 3 voices | 11.7 ms over 20 s |
| Air Pit | 21:16:23 | 2,120 ms (`wait=0`, `reactions=15`) | Approve chain, Disapprove 0.3 | 11.1 ms over 10 s |
| Bowl | 21:17:48 | 2,164 ms (`wait=0`, `reactions=16`) | Approve chain, Disapprove 0.3; stands at 41-52 m (Rolloff 150) | - |

Quoted lines (Equator):
- `21:10:48.763 [SpectatorAudio] reaction demo Cycle: claimed the reactions as 'developer-demo'`
- `21:11:07.820 [SpectatorAudio] arena_equator: reaction Approve strength=0.90, 3 voice(s) [arena_fan12_kill_cheer_11 fan 28 at 35.9 m +0.00 s, arena_fan8_kill_cheer_01 fan 77 at 32.4 m +0.39 s, arena_fan13_kill_cheer_01 fan 3 at 34.6 m +0.67 s], calls held 10.7 s`
- `21:11:16.814 ... reaction Disapprove strength=0.30, 1 voice(s) [arena_fan12_kill_angry_02 fan 11 at 32.2 m +0.00 s], calls held 2.9 s` (lament only below 0.4)
- `21:11:59.828 ... reaction TakeCover directed strength=0.50, 2 voice(s) [...]`, `21:12:08.781 ... reaction Start strength=1.00, 3 voice(s) [...]`, `21:12:17.818 ... reaction Finish strength=1.00, 3 voice(s) [...], calls held 7.9 s`
- `21:13:45.843 ... reaction Start strength=1.00, 3 voice(s), rules relaxed to level 4 (last two clips, sentence window 0 of 3) [...]`: the second Start of the raid kept its three voices
  through the lead's relaxation order (the 5-group Start pool), with three distinct sentences inside the reaction.

Plan section 7, host items:
1. Deploy, sync, restart: done as above.
2. One raid per crowd map with the demo: every implemented kind with its clips and distances (22-37 m on Equator, Bay5 and Air Pit, 41-52 m on Bowl), voice families and sentence groups distinct
   inside every reaction, the calls held after each start (a call came 0.05 s after a hold ended on Bowl, `21:18:34.493`). The set loads on the worker in 15 to 118 ms; `load total=` stays at
   2.12-2.16 s with `wait=0` or 1 (2.08-2.16 s before). **0 ArenaMapPort warnings** in the whole process (`uibridge_log level=warning` from 21:09:21), no hitch (frame max 11.1-11.7 ms).
3. The audio-reset window: the reset came at `21:10:38.416` (the countdown), the first reaction at 21:10:49 after the raid was `Started`. Reactions cannot start before `Started`, so a request
   in the reset window is dropped as `notstarted` by design; none was lost after it.
4. Plain-raid local death (`uibridge_hook kill` at 21:12:45, 1 s after a 3-voice Approve): no exception, and the reactions went on while dead (Disapprove 21:12:53 / 21:13:02 / 21:13:11,
   TakeCover, Start, Finish 21:13:54) because **the plain raid stayed `Started` after the death** until `gpu-run leave` ended it 70 s later (the death screen did not come by itself). That corrects
   the plan's sentence (4.4) that a plain raid leaves `Started` at once; it agrees with D12 (reactions allowed while dead) and leaves the random calls silent as before. Teardown:
   `21:14:09.370 reaction demo stopped (the crowd is removed)`, `crowd and shared assets removed`, `all voices, propagation and clips removed`.
5. Iceberg (21:19): no `[Spectator]` or `[SpectatorAudio]` line, no warning; `uibridge_inspect` read in the running process `SpectatorCrowd.ReactionKinds = 0`, `IsAvailable = false`,
   `ContractVersion = 1`.
6. NoiseProbe (armed in `Detect` on the host): minute line `21:12:49.661 ... bursts peak=0 click=0 dropout=0 gap=0 clip=0 loud=0 ... orphan=0 evOrphan=0 ... spec=+8/-9 react=+14/-13`.
7. Hideout (gpu-run scenario `hideout-open`, 21:20:08): the Hideout screen with its labels, no error, 0 warnings; back to the main menu.

Not covered by this run: listening (loudness, the swell, variety, the 19 `listen` clips) is the user's; which listener path ran (`Camera.main` or the player's body: the distances fit either in a
plain raid) and the listener in ArenaMode's spectator or replay view; the raid status across ArenaMode's body handover; `GetSource` headroom under load (no source failure was logged). Phases B
(ArenaMode) and C (the plugin's own sensors) are not started.

## 2026-11-07 - Reaction roar layer: Arena's Cheer and Angry beds replace the murmur swell

Status: implemented and reviewed offline (data, Unity-free model, Unity glue, one review round), **not yet host-verified, not listened to**. Nothing is committed, staged or deployed by this
work. Builds on phase A (`d70acd2`, the sections above); where this section and the phase-A text about the bed swell differ, this section wins (the phase-A paragraphs stay as written,
appended-only record). Spec of the work: the roar-beds spec of 2026-11-07 (workflow scratch, not tracked); plan decision D3 in [../PLAN-CROWD-REACTIONS.md](../PLAN-CROWD-REACTIONS.md).

### Decision (user, 2026-11-07)

"gerne einbauen. das neutrale gemurmel anzuheben macht glaube ich nicht wirklich sinn, eher sollten dann die lauteren Cheer und Angry Betten laufen"

Meaning: a reaction no longer lifts the neutral murmur bed (the transient swell of phase A is removed, not kept as an option); instead it plays one of Arena's in-match crowd beds from every stand
for the length of the reaction: a Cheer bed (roar) or an Angry bed (boos). The sustained `SpectatorCrowd.SetExcitement` level on the neutral bed is a released contract member (0.4.0) and keeps
its meaning exactly.

### Source facts (installed Arena, measured by the extractor)

`ambient_voice.bundle` holds seven stereo 44.1 kHz beds on three banks. The bundle is not a stage-2 key source and no clip streams from one of the protected files (they stream from
`archive:/CAB-e89d838d.../*.resource`); the extractor checks that per clip, as for the reaction clips.

| Bank | Trigger | Clips (names as in the bundle) | Slots | Length | Stereo RMS / peak (dBFS) |
|---|---:|---|---|---|---|
| Cheer | 1119 | `arena_fans_ambient_cheer`, `..._cheer2`, `..._cheer3` | 3 embedded, 0 external, 0 null; SpreadGroups 1 (= Clips) | 7.0 s each | -22.75 / -8.87, -22.54 / -7.35, -22.94 / -8.42 |
| Angry | 1121 | `arena_fans_ambient_angry`, `..._angry2`, `..._angry3` | 3 embedded, 0 external, 0 null; SpreadGroups 1 | 8.0 s each | -26.42 / -11.73, -25.84 / -11.00, -29.70 / -14.17 |
| Neutral | 1122 | `arena_fans_ambient_neutral` (the murmur since 2026-11-02) | 1 embedded | 80 s | -31.1 |

Arena plays these from its `FansAmbient` emitters on the Cheer and Angry triggers; the emitters are inactive in the imported scenes and the triggers do not exist in the port. Playing the beds from
the stands on a reaction is the port's choice, not a proven copy of Arena's in-match behaviour.

### Export (`extract_spectator_audio.py extract`)

The six beds ("roar beds") get the neutral bed's processing (mean downmix, 500 ms equal-power crossfade of the head into the tail, so each loops without a step) but a **common level** instead of
the murmur's fixed -8.9 dB: every roar bed is brought to **-32 dBFS RMS** mono (the murmur sits at -40). One target keeps cheers and boos equally audible (Arena's Cheer beds are about 8.5 dB above
its Neutral bed, its Angry beds 1.4 to 5.3 dB, and a boo 1 dB above the murmur would not read) and the volume key scales all of them. **-32 is a start value for the user's listening.** The gain
is recorded per clip in `processing.gain_db` beside the original stereo RMS and peak. `process_roar` refuses a bed quieter than the target (it would need a boost) and a bed whose peak ends above
-3 dBFS; the level check of `validate` takes +-1.0 dB around the target (`ROAR_LEVEL_TOLERANCE_DB`) because the crossfade shifts the finished loop's RMS slightly.

| Bed | Gain | Frames after the seam | Output RMS / peak (dBFS) | Loop jump / step RMS |
|---|---:|---|---|---|
| cheer | -8.84 dB | 286,650 (6.5 s) | -31.90 / -18.17 | 0.00119 / 0.00489 |
| cheer2 | -9.07 dB | 286,650 | -31.89 / -17.72 | 0.00031 / 0.00489 |
| cheer3 | -8.70 dB | 286,650 | -31.98 / -18.16 | 0.00659 / 0.00480 |
| angry | -5.28 dB | 330,750 (7.5 s) | -32.08 / -17.43 | 0.00381 / 0.00453 |
| angry2 | -5.84 dB | 330,750 | -32.06 / -17.20 | 0.00238 / 0.00414 |
| angry3 | -1.84 dB | 330,750 | -31.95 / -17.29 | 0.00797 / 0.00439 |

The largest loop-jump ratio is 1.8 (angry3), the limit 3.0; output RMS lies within 0.11 dB of -32, peaks at least 14 dB below the -3 dBFS limit. The six WAVs are 3,704,664 bytes. After
`extract` and `verify`: 91 files in `work/extract/spectator-audio` (13 calls, the murmur, 70 reactions, 6 roar beds, the manifest), both OK in 9.4 s.

**Manifest.** `spectator-audio.json` (schema 1 stays) gains the optional `reaction_beds` object beside `bed` and `reactions`: `{"schema": 1, "source", "target_rms_db": -32.0, "banks": [{"bank":
"Cheer", "trigger": 1119, "kind": "cheer", "clips": [...]}, {"bank": "Angry", "trigger": 1121, "kind": "angry", "clips": [...]}]}`, banks in the order Cheer, Angry, clips sorted by name, three per
bank. A clip record has the bed record's fields (`name`, `file`, `channels` 1, `sample_rate` 44100, `frames` within 3 s to 12 s, `bytes`, `sha256`, `peak`, `rms`, `seam_crossfade_ms` 500,
`source`, `source_id`, `processing` with the original format, peak and RMS, `downmix` "mean", `gain_db` (negative), `crossfade_frames`); the key sets are enforced exactly and `schema` and
`trigger` must be real ints. A manifest without `reaction_beds` is valid and gives a crowd without a roar layer (one Debug line).

**Validation and staging.** `validate(folder, require_bed, require_reactions, require_reaction_beds=False, pools=None)`; `extract()` validates with every `require_*` flag true and removes stale
roar WAVs of an earlier export by the exact pattern `arena_fans_ambient_(cheer|angry)[23]?\.wav` only (the `verify` command still validates without the flags and prints the roar line).
`validate_distribution.py` requires the roar beds (`require_reaction_beds=True`) and compares them byte for byte with `work/extract/spectator-audio`; its staged key-source check covers all 91
files. `make_release_zips.py` admits the six names in the core part (`arena_fans_ambient_(?:cheer|angry)[23]?\.wav`). The client's flat `*.wav` staging copy stages them: no csproj change.

### Model (`SpectatorReactionBedModel.cs`, Unity-free, linked into the tests)

- **Manifest and validation.** `SpectatorReactionBedSet`/`Bank`/`Clip` records and `SpectatorAudioManifest.ValidateReactionBeds()` (throws `InvalidDataException`), independent of `Validate`,
  `ValidateBed` and `ValidateReactions` in every direction: exactly 2 banks in the order Cheer, Angry; 1 to 8 clips per bank (the Python side pins 3, the client stays tolerant); names match
  `arena_fans_ambient_(cheer|angry)[23]?` and fit the bank; `target_rms_db` within -60..-10 (not exactly -32: that is a start value).
- **Kind table** (`SpectatorReactionBedKind.For`, pinned): Approve (1), Start (4), Finish (5) -> Cheer (bed kind 1); Disapprove (2) -> Angry (bed kind 2); TakeCover (3), Lull (6),
  PurifiedTeam (7) and anything else -> none (0).
- **Envelope** (`RoarEnvelope`, one per stand and kind, allocation-free, absolute `double` clock): Idle -> Attack (linear, 0.3 s, from the CURRENT level to the amplitude) -> Hold (`holdSeconds`,
  counted from the end of the attack, so the hold ends at trigger + 0.3 + hold) -> Release (linear, 2.0 s, so it reaches exactly 0 and Idle at a computable time) -> Idle. Attack and release
  have fixed durations whatever the start level, so the slope varies. A retrigger of the same kind keeps the larger amplitude while the envelope is in Attack or Hold, moves the hold end to
  the later of the two and restarts the attack from the current level (no jump up, no jump down); in Release the new amplitude counts as given, so a quieter reaction after a dying roar can
  still be heard. `Cut(now)` is a quick release of 0.5 s (used when the other kind takes over); it does nothing when a release has 0.5 s or less left. `Stop()` ends it at once (outside
  Started, audio reset). `Step(now, out changed)` returns the gain 0..1.
- **Formulas.** `Amplitude(strength) = 0.35 + 0.65 * strength` (clamped to 0.35..1, NaN -> 0.35); `HoldSeconds(reactionDurationSeconds) = clamp(duration, 1.5, 5.0)`.
- **Clip choice** (`RoarClipPicker`, `System.Random` with a seed): one clip of the kind per trigger, never the one played last for that kind (three clips, so at least two alternate); the same
  clip on every stand of that reaction; `StartOffset(frames)` gives a per-stand start frame in 0..frames-1, so the stands are out of phase.
- **Volume law** (`SourceVolume`): `min(1, clamp(volumeKey, 0, 2) * clamp(gain, 0, 1) / ClipGain)`, with the murmur's `ClipGain` 3.2 and `MaxVolume` 2 shared (the roar clip floats are baked with
  `SpectatorCrowdBedModel.ToClipFloats`); at key 1 and gain 1 the roar plays at its exported level. A NaN key counts as the default 1, a NaN gain as 0. Defaults: `ReactionBedsDefault = true`,
  `ReactionBedVolumeDefault = 1`.

### Glue (`SpectatorReactionBeds.cs`, one file; `SpectatorAudio.StartReaction` calls it)

- **Stands.** Built by `ArenaSpectators.Create` right after `SpectatorCrowdBed.Create` in its own try, from the same spectator positions and `SpectatorCrowdBedModel.Plan` with the bed's reach: the
  same 2 to 6 stands as the murmur (4/4/6/6), so a roar comes from where the murmur comes from. It does not depend on the bed being on (bed off -> roars still play) and uses the bed's placement,
  environment mixer group and rolloff-curve helpers (now `internal static` on `SpectatorCrowdBed`: `Place`, `EnvironmentGroup`, `ReachNow`). The stands are planned once at build: a live
  `CrowdBedReach` change does not re-plan them. A child of the crowd root, removed with it; `Current` like the bed.
- **Sources.** Per stand up to two owned looping AudioSources (cheer, angry), created lazily at the first trigger of their kind, placed like the bed's source (spatial blend 1, the stand's Custom
  rolloff curve, no occlusion processing), priority 150 (above the murmur's 200, so the roar wins if voices run short), pitch 1.0 with the random start offsets (the murmur's per-stand pitch is
  not used). The AudioClips are one per roar bed, shared by the stands, built lazily and destroyed on audio reset and teardown.
- **Trigger.** `Trigger(kind, strength, reactionDurationSeconds)`, main thread: maps the kind to a bed (none -> false), picks the clip, and per stand cuts the other kind's source if it sounds,
  triggers the envelope (`Amplitude`, `HoldSeconds`) and starts the source at its offset if it is not playing. A retrigger of a kind that is still sounding keeps its clip and does not call
  `Pick`, so the audio does not change under the listener. It is not gated on `Started` itself (`StartReaction` runs only then); a pending audio reset is consumed first, as the tick does.
- **Tick.** Allocation-free; idle it is two or three field reads (`_ready`, `_reset`, `_sounding`) and a return, no Unity call and no scene search. While a roar sounds (about 10 s at most per
  reaction) it reads `AbstractGame` status once and steps one envelope per sounding source, writing `source.volume` only when the gain or the volume key changed. It stops everything at once
  (no release) outside `Started`, when `Spectators/ReactionBeds` or `Spectators/Reactions` is off, and on an audio reset (clips rebuilt lazily). A source whose envelope went Idle is stopped.
- **Worker.** `LoadRoarPackage` in `ArenaSpectators.cs`, directly after `LoadReactionPackage`: the six WAVs are read, CNG-hashed (`CryptoProviders.Select(null)`) and PCM-decoded on the crowd
  worker (no Unity API, no log); a failure travels in `Loaded.Roars` and becomes ONE Warning on the main thread, the calls, the reactions and the murmur keep working. The main thread does it
  when the worker did not. The load summary gains `roars=`.
- **Removed.** The phase-A swell is gone: `CrowdExcitement.AddSwell`/`Swell`/`Level`/`StepSwell`/`SwellAttack`/`SwellTimeConstant`, `CrowdBedState.RequestSwell`/`TakeSwell`, the bed tick's two
  swell lines, `SpectatorAudio.ReactionSwellNow` and the key **`Spectators/ReactionSwell`** (never released; an orphaned `ReactionSwell` line in a developer's .cfg is harmless and is simply
  never read). The murmur's factor is `1 + 0.6 * Current` of the smoothed external level again, exactly as in 0.4.0. The public `SpectatorCrowd` surface is unchanged (eight members,
  `ContractVersion` 1).
- **Keys** (developer-only, bound through `Plugin.Instance.Settings` in the new file's `Install()`, honoured only with DeveloperMode, none public; not in `SpectatorCrowdBed.cs`, whose Bind
  count stays three): `Spectators/ReactionBeds` (true) and `Spectators/ReactionBedVolume` (1.0, 0..2, live next tick). `Plugin.cs` calls `SpectatorReactionBeds.Install()` between the bed's
  and `ArenaSpectators.Install`.
- **NoiseProbe.** New `NoiseEv.Roar` appended after `ReactStop`, counted as a cause; tokens `roar+` (the tap logs `source.volume`) and `roar-`; the census prints `ownedRoar=playing/active`
  beside `ownedBed`. Nothing from the worker.
- **Logging.** Tag `[ReactionBeds]`: one Debug line at build (`<map>: 6 roar beds (3 cheer, 3 angry), N stands`), one Debug per trigger in developer mode (`roar cheer amp=0.74 hold=3.2 s
  clip=arena_fans_ambient_cheer2 stands=4`), one Warning for a damaged set, one Debug at teardown. The per-reaction line of `SpectatorAudio` ends with `roar=cheer|angry|none`.

### Review round and tests (offline, this VM, 2026-11-07)

Four findings, all confirmed against the code and applied: (glue F1) the roar tick now also stops on `!SpectatorAudio.ReactionsOn`, so switching `Spectators/Reactions` off silences a sounding
roar (`ReactionsOn` is `internal static` for it); (glue F2) `Trigger` consumes a pending `_reset` before starting any lane (`Release(true)`, `Fail(e)` and `return false` on an exception);
(compat F1) a stray `TextureOrder` summary was put back above `TextureOrder` in `ArenaSpectators.cs`; (compat F2) the `roar+` tap passes `source.volume`, not the volume key. One pin test
covers F1, F2 and the tap volume.

- Client Release build with `ArenaMapPortDllOnly=true`: 0 warnings, 0 errors.
- `dotnet test ArenaMapPort.Server.Tests -c Release`: **3,184 passed**, 0 failed, 0 skipped (twice after the fixes; the model stage had 74 new model test cases, the glue stage 11 new source pins
  and the rewritten swell pins). The real-data test on `work/extract/spectator-audio/spectator-audio.json` ran (not skipped): `ValidateReactionBeds`, 3 + 3 clips, size, SHA-256 through CNG,
  WAV format and frames, RMS within 0.3 dB of the manifest target, peak at most -3 dBFS, float peak at `ClipGain` at most 3.2, calls, murmur and reactions still validating beside it.
- Python: whole suite `Ran 2886 tests ... OK (skipped=67)` with `PYTHONIOENCODING=utf-8`; the `FAIL: ... HasNativeWater as a boolean` line in its output is a message printed by an existing
  test that expects it. `validate_distribution.py`: `DISTRIBUTION_OK 5 maps 15 bundles 5 sidecars encrypted`.
- **Counts.** `dist/` 359 files (353 + 6); `make_release_zips.py --plan` core 230, equator 24, bay5 30, airpit 22, bowl 26, iceberg 27, raw 5,396,842,180 B, estimated zip 5,121.8 MiB, 0
  unclassified; `spectator-audio/` 91 files.

### Not yet host-verified, not listened to

Nothing of the roar layer has run on a host and nobody has listened: the level (-32 dBFS RMS per bed), the 0.3 s attack, the 2 s release, the hold clamp of 1.5 to 5 s, the amplitude law, the
priority 150 and the two keys are start values for the user's listening. The `SetExcitement` murmur is unchanged and not part of this. Host checklist (GPU-2, DeveloperMode, `Spectators/ReactionDemo
= Cycle`, plain raids with bots None, after a full deploy: the six WAVs and the manifest travel with the whole dist, the client DLL needs an EFT restart):
1. One raid on each crowd map (Equator, Bay5, Air Pit, Bowl): a roar from the stands on Approve, Start and Finish, boos on Disapprove, nothing on TakeCover; the Debug lines `roar cheer|angry`
   with the right `stands=` (4/4/6/6); a Cheer after an Angry (and the reverse) cuts the other bed in 0.5 s; two quick Approves keep the clip and extend the hold; the roar ends 2 s after the hold.
2. `Spectators/CrowdBed` off: the roars still play. `Spectators/ReactionBeds` off: no roar, the voices and the murmur unchanged. `Spectators/Reactions` off while a roar sounds: it stops.
3. Iceberg: no `[ReactionBeds]` line, no warning, `SpectatorCrowd.ReactionKinds = 0`.
4. Hideout once (a prefix or tick must not touch it); the raid end and an audio reset (the countdown): the sources stop and the clips are rebuilt at the next trigger.
5. No ArenaMapPort warning in the whole process; `[ReactionBeds]` Debug lines; the `load total=` line with `roars=` (the worker budget of 2.1 s must not grow noticeably); frame max against 11-12 ms;
   `ownedRoar=playing/active` in a NoiseProbe census; check that the up to 12 owned sources at priority 150 are not culled under load.
6. The user listens: roar level against the voices and the murmur (-32 may need to move either way), whether boos read, the attack and release, `ReactionBedVolume`.

### Host run on GPU-2 (2026-11-07, 15:29-15:42)

**Build and deploy.** The client Release DLL of this working tree (HEAD `d70acd2` plus the roar-layer change; the commit that adds this paragraph carries the code), rebuilt
by the lead after the workflow (0 warnings, 0 errors; `dotnet test` 3,184 passed). Dry run, then `deploy-to-spt.ps1 -Confirm` at 15:29: 359 files, 6 new (the roar WAVs),
3 overwritten (client DLL, `spectator-audio.json`, the server DLL by its commit stamp only; no server restart), `gpu-run deploy-wait` identical on the host after 14.1 s.
EFT was not running; `gpu-run start` brought a fresh process (started 15:31:44, 16/16 plugins identical). `Spectators/ReactionDemo = Cycle` was set with `persist=false`
(DeveloperMode on that host; its NoiseProbe stands at Capture from earlier tests). All settings below were set with `persist=false`; the host's .cfg is unchanged.

**Equator (plain PMC raid, bots None, 15:32-15:36).**
- `15:32:48.425 [ReactionBeds] arena_equator: 6 roar beds (3 cheer, 3 angry), 4 stands, volume 1.00`, then the crowd's summary `load total=2218 ms (wait=1, hash=59, parse=1954,
  textures=2058, actors=56, audio=98, reactions=228, roars=6; hash and parse on the worker thread, crypto CNG)`: the six beds cost 6 ms on the worker.
- `15:33:02.399 [ReactionBeds] arena_equator: audio configuration changed (device changed=False); the roars stop and the clips are rebuilt on demand`: the raid-start audio reset,
  consumed before the first trigger (checklist 4; no clip existed yet, so nothing was rebuilt).
- The demo cycle, every `[SpectatorAudio] reaction` line ending with the matching `roar=`: Approve 0.30/0.60/0.90 gave `roar cheer amp=0.55/0.74/0.93 hold=4.0/2.8/5.0 s`
  (clips cheer2, cheer, cheer2: never the same clip twice in a row), Disapprove 0.30/0.60/0.90 gave `roar angry amp=0.55/0.74/0.93 hold=3.3/2.0/3.2 s` (angry, angry3, angry),
  TakeCover and TakeCover directed `roar=none`, Start 1.00 `roar cheer amp=1.00 hold=4.8 s` (cheer3), Finish 1.00 `roar cheer amp=1.00 hold=4.0 s` (cheer); `stands=4` on every
  line, nine seconds apart (the demo's cadence), 1 to 3 voices as in phase A.
- Checklist 2, bed off: `Spectators/CrowdBed=false` at 15:34:31; the Start at 15:34:32.791 roared, and one `uibridge_inspect` read of frame 17234 gave
  `SpectatorReactionBeds.Current.PlayingSources = 4`, `ActiveSources = 4`, `SpectatorCrowdBed.Current.PlayingStands = 0` of `StandCount = 4`: the roar plays from all four
  stands while the murmur is silent.
- `Spectators/Reactions=false` at 15:35:25.057 (the last roar, Disapprove 0.30 at 15:35:17.874 with hold 1.9 s, had ended at 15:35:22): `spectator reactions enabled=False`,
  0.3 s later `ActiveSources = 0`, `PlayingSources = 0` and the murmur's `PlayingStands = 4` (bed on again) held over 40 frames; `enabled=True` again at 15:35:36.
- Performance during the cycle: 120.0 fps, mean 8.3 ms, p95 8.3 ms, max 11.3 ms over 3,599 frames / 30 s. NoiseProbe minute line at 15:34:13: `bursts peak=0 click=0
  dropout=0 gap=0 clip=0 loud=0 ... orphan=0 evOrphan=0 ... react=+14/-14 ... resets=0`.

**Bowl (plain PMC raid, bots None, 15:36-15:41).**
- `15:36:29.847 [ReactionBeds] arena_bowl: 6 roar beds (3 cheer, 3 angry), 6 stands, volume 1.00`; `load total=2120 ms (... audio=30, reactions=118, roars=5; ...)`.
- Checklist 5, the owned sources under load: one read of frame 24842 gave `PlayingSources = 6`, `ActiveSources = 6` and the murmur's `PlayingStands = 6`: six roar sources
  (priority 150) and six murmur stands (priority 200) sound at once, none culled, with the calls and the reaction voices beside them.
- Checklist 2, `Spectators/Reactions` off while a roar sounds (review finding F1): demo `Once` with kind 4 (Start) at 15:37:10.699, `roar cheer amp=1.00 hold=4.7 s`
  (attack to 11.0, hold to 15.7, release to 17.7); `Reactions=false` at 15:37:16.522, inside the release with 1.2 s left; 0.1 s later `ActiveSources = 0`, `PlayingSources = 0`
  held over 14 frames: the roar stopped with the key instead of running out.
- A cycle with `ReactionDemoKind=2` and `ReactionGapScale=0.3`: Disapprove only (the kind key narrows the cycle), still every 9 s (the demo's cadence is fixed; the gap scale moves
  the planner's cooldowns, not the demo), holds 1.5 to 3.5 s; `PlayingSources` never above 6 in 505 reads over 55 s.
- Performance: 119.6 fps, mean 8.4 ms, p95 8.3 ms, max 42.5 ms once, 2.3 s before the status read at about 15:40:45 while no demo ran (no ModProfiler line in the buffer; not
  attributed, not reproduced).
- Keys restored (`ReactionDemo=Off`, `ReactionDemoKind=0`, `ReactionGapScale=1`, all `persist=false`).

**Hideout.** Opened from the main menu after the Bowl raid at 15:41 (screen Hideout after 5.9 s), back to the main menu. **0 ArenaMapPort warnings in the whole process**
(15:31:44 to 15:42: both raids, the hideout, every key change).

**Not exercised on the host.** The 0.5 s cut of the other kind and the retrigger (same clip, longer hold): the developer demo fires every 9 s at every gap scale and the longest
envelope is 7.3 s (0.3 + 5 + 2), so no two roars overlapped; both paths are unit-tested only (`SpectatorReactionBedModelTests`, the glue pins). Bay5 and Air Pit were not run (the
same code with 4 and 6 stands). Iceberg was not run this time (no crowd, no layer by construction; its `ReactionKinds = 0` was host-checked on 2026-11-02). **Nobody has listened:**
the level (-32 dBFS RMS per bed), the attack, the release, the hold clamp and `ReactionBedVolume` are start values for the user's ear.

### Listened (user, 2026-11-07)

The user listened to the roar layer deployed that afternoon (the build of the host run above, the developer demo cycle `Spectators/ReactionDemo = Cycle` on the user's own PC, which plays the reaction voices
together with the Cheer and Angry beds) and said: "habe ich mir angehoert, klingt gut". It covers what was heard: the roar level of -32 dBFS RMS per bed, the 0.3 s attack and the 2 s release, and the keys at
their defaults (`Spectators/ReactionBeds` on, `ReactionBedVolume` 1.0). It is a judgement of the demo cycle, not a clip-by-clip review of the 70 reaction voices, and it made no statement on the cut of the
other kind or the retrigger. The level stands as accepted; the sections above that call these values start values are history. ArenaMode has not adopted the contract.

### 2026-11-07 evening - roar bed default 2.0 (user)

User, 2026-11-07 evening: "den lautstärkepegel der reaktions-betten anheben, hab den wert auf 2.0 und würde sagen das passt." The developer key `Spectators/ReactionBedVolume` was already 2.0 in his .cfg; 2.0 becomes the default for the next version (0.5.0 shipped 1.0). 2.0 is the top of the key range 0..`SpectatorCrowdBedModel.MaxVolume` (2).

The law is unchanged: `SourceVolume = min(1, key x gain / ClipGain 3.2)`, so 2.0 gives 0.625 at full gain (1.0 gave 0.3125); the exported level stays -32 dBFS RMS per bed, and there is no headroom left on the key. The key description now says "2 = the level the author chose (the top of the range)".

Re-seed (the repository's DefaultsRevision pattern): `SpectatorReactionBeds.Install()` binds the hidden int key `Spectators/ReactionBedDefaultsRevision` (default 0, `BrowsableAttribute.No`, "Internal ... Leave it alone."). `ReactionBedDefaults.VolumeMoves` (Unity-free, `SpectatorReactionBedModel.cs`, Revision 1, OldVolumeDefault 1.0) moves exactly a saved 1.0 to 2.0 when the saved revision is below 1; any other value (2.0, 0.8, 0, 1.5, NaN) stays; the revision is then written and one Debug line `[ReactionBeds] config re-seeded to reaction bed defaults revision 1 (N value(s) moved; volume=...)` says what moved. The Bind count of `SpectatorReactionBeds.cs` is three (pinned in `SpectatorReactionBedsSourcePinTests`).

Tests: the `SpectatorReactionBed` tests 65 of 65, client Release build 0 warnings. **Not host-run yet** (the re-seed line and a raid with a cheer are the lead's check). Correction of the record above: "ArenaMode has not adopted the contract" is history of its date; ArenaMode 0.4.0 and later uses the reaction contract and drives the reactions from its match events (user, 2026-11-07), and `ReactionBedVolume` 1.0 in the listening note above is the 0.5.0 default.

### 2026-11-07 evening - roar beds exported at -26 dBFS RMS, the key back at 1.0 (user)

User, 2026-11-07 evening: "vielleicht sollten wir bei den jubel-betten dann einfach den export-pegel erhöhen und den regler dann wieder auf 1.0 lassen". The paragraph "roar bed default 2.0 (user)" above (default 2.0 at the -32 dBFS export, 02e4361, never released) is superseded: the loudness is moved into the export, the key returns to 1.0.

Export: `extract_spectator_audio.py` brings the six roar beds to **-26 dBFS RMS mono** (6 dB above the -32 of 0.5.0; tolerance +-1 dB, same processing: mean downmix, 500 ms equal-power loop seam). Gain, RMS and peak after the gain: cheer -2.84 dB, -25.90, -12.17 dBFS; cheer2 -3.07, -25.89, -11.72; cheer3 -2.70, -25.98, -12.16; angry +0.72, -26.08, -11.43; angry2 +0.16, -26.06, -11.20; angry3 +4.16, -25.95, -11.29. Loop jumps 0.06 to 1.82 (limit 3.0). The other 84 files of the folder are byte-identical; it still holds 91 files (dist 359).

Peak rule and why: the client bakes ClipGain 3.2 into the decoded clip floats (`SpectatorCrowdBedModel.ToClipFloats`, shared with the murmur) and sets `AudioSource.volume = min(1, key x gain / 3.2)`, so a clip float above 1.0 would clip. The ceiling is a source peak of -10.1 dBFS; the export enforces **-10.5 dBFS after the gain** (10^(-10.5/20) x 3.2 = 0.955), replacing the old -3 dBFS roar rule, in `extract` and in `verify`. A bed that would pass it is refused with a message that names the bed, its peak, its gain and the limit; it is never limited silently. The old rule that a bed quieter than the target is refused ("it would need a boost") is gone, so a gain may be positive (angry, angry2, angry3).

Key: `Spectators/ReactionBedVolume` default 1.0 again (`SourceVolume` 0.3125 at full gain). Re-seed: the hidden key `Spectators/ReactionBedDefaultsRevision` keeps its name and revision 1; `ReactionBedDefaults.VolumeMoves` now moves exactly a saved **2.0** (the value set by ear at the -32 export; the same loudness at the +6 dB export is 1.0) to 1.0 once, every other value stays; one Debug line as before. The Bind count of `SpectatorReactionBeds.cs` stays three.

Evidence: extract and verify `SPECTATOR_AUDIO_OK ... roar beds 6 clips, 3704664 bytes`; client Release build (DllOnly) 0 warnings, 0 errors; `SpectatorReaction` tests 109 of 109 (the real-data test pins -26, the peak at most -10.5 dBFS and the float bound <= 1.0 at ClipGain 3.2); `validate_distribution.py` `DISTRIBUTION_OK 5 maps 15 bundles 5 sidecars encrypted`. Python: 163 tests in three modules, one failing test (a scale tripping the generic RMS ceiling) fixed and `RoarBedTests` re-run (20 OK); the full set is to be re-run before the commit. **Not host-run and not listened to**: the user listens in game at the next change.
