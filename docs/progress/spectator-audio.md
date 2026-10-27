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
