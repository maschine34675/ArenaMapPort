# Native spatial acoustics — Equator and Bay5

## Implementation — 2026-09-28

User accepted Bay5 water and requested the next acoustic step. Branch
`feature/spatial-audio`, based on `12b0363`. This restores native propagation and
wall/room attenuation plus source room-tone clips. Room-dependent mixer reverb
and outdoor ambience blending are separate, still unimplemented systems.

### Source and validation

`tools/extract/extract_spatial_audio.py` decodes the original Sound scenes from
the user's Arena 0.5.4.0 installation: Equator `level56`, Bay5 `level188`.
Private serialized fields are byte-decoded against the actual component layout;
every decoder must consume the complete payload. References, active state,
transforms, trigger ownership, room OBB geometry and reciprocal links are checked.
The independent `spatial_audio_bake.py` reader validates every original baked
route against the source graph, counts, indices and complete file boundaries.

| Source | Equator | Bay5 |
|---|---:|---:|
| All rooms / portals / areas | 23 / 55 / 51 | 44 / 89 / 69 |
| Active rooms / portals / areas | 21 / 50 / 49 | 40 / 85 / 63 |
| Room pairs / complete routes | 210 / 31,676 | 706 / 5,924 |
| Original bake bytes | 1,052,882 | 191,304 |

Inactive originals reuse IDs and remain inactive. Zero-thickness portals are
authored data and valid; zero-volume rooms are rejected. Equator's original
rooms, including the atrium, are all classified indoor. No classification was invented.

New original room tones are `mall_indoor_ambient_tone` (Equator,
`sharedassets56.assets:62`) and `amb_bay5_arena_indoor` (Bay5,
`sharedassets87.assets:74`). Package metadata records source resource-slice and
decoded WAV SHA256 values. Original BSG data stays in ignored `work/` and `dist/`;
this is extraction from the user's installation, not a new asset licence.

Original bake SHA256:

- Equator: `3f7fd1fb5328e1235f24eda4d5ee2f14725e2b49b32de704a5b33de68ca3fa8e`
- Bay5: `7f386cb792aa205e6de3018fc6905525c6596b7dae10b872f9aff3df05994f5b`

### Runtime and packaging

`NativeSpatialAudio` builds an inactive hierarchy in the owned Scripts scene,
wires every portal before activation, creates native location/pool settings and
then lets EFT initialize its real spatial system. EFT backend occlusion tuning
is retained; source room geometry, material categories and transmission data
drive it. Existing SurroundAudio processing remains separate.

Bake and WAV lengths/hashes are checked before use. Missing/rejected data or a
30-second initialization timeout retains the old unoccluded fallback. Caller
cancellation still cancels the raid. Owned groups, clips, sources, native data
and mixer changes are released on unload; cleanup does not clear foreign groups.
Native `RoutesDataCount` is the number of pair ranges, not the total route count.

Room tones start after `GameStatus.Started`: earlier creation gets stopped by
the raid's BetterAudio reset. Bay5 temporarily opens its indoor ambient bus and
restores the previous value only while still owning that value. Existing harbor
and mall ambient players keep their own lifecycle. Source room-tone volume and
fade values are preserved.

Per-map F12: `Audio (<map>) / NativeSpatialAudio=true`; applies next raid.
`Audio diagnostics / SpatialProbe=false` is a silent, pooled remote source for
measuring actual native Combined occlusion and final volume/filter application.
F10 reports listener/source classification and real room-tone playback state.
It does not prove subjective listening quality.

Regenerate with `python tools/extract/extract_spatial_audio.py`; inputs land in
`work/extract/spatial-audio/<map>/`. Release builds, including DllOnly builds,
stage six files under `BepInEx/plugins/ArenaMapPort/acoustics/`: graph JSON,
original bake and one decoded WAV per map. Existing v5 scene bundles are unchanged.

### First native comparison — SPT-GPU-2, 12:31–12:38 CEST

Process `c7133a5e` initialized exact native counts on both maps. Equator source
room 2 to listener room 4 at 12:32:05 applied volume 0.4022 and low-pass 11,205.6 Hz;
moving into source room 2 at 12:32:29 restored volume 1 and zero acoustic effects.
Bay5 correctly classified indoor room 40 and adjacent outdoor room 37. A remote
source restarted after the raid reset was actively playing at 12:35:41, volume
0.9125 across that boundary. These are amplitude factors, not perceived loudness.

This first pass exposed the early source-reset issue; startup was moved after
raid start for both room tones and the diagnostic source. Both graph unloads
returned native Initialized to false and group count to zero. Final-build
verification is recorded below after the restart.

### Runtime-created clip reset — 12:51–12:58 CEST

The next pass (`a70e9fb2`, exact build/disk/loaded identity confirmed) showed that
moving playback after raid start alone was insufficient. Native room tone sources
had their correct clip and bus, but actual `AudioSource.isPlaying=false` and zero
samples on both maps. `LocalGame` calls `AudioUtils.ResetAudioBuffer`, which calls
`AudioSettings.Reset`; Unity discards script-created clip data at that point.
The room-tone WAVs must therefore be decoded into Unity clips **after** the reset.
Source validation still occurs before spatial initialization. Native rooms retain
the same `RoomAmbientData` object, so assigning its newly created clip before the
controller starts preserves native behavior. An audio-configuration event marks
owned room tones for main-thread release/recreation as well. PCM upload is checked.

Unity documents this behavior in its [2022.3 AudioSettings manual](https://docs.unity3d.com/2022.3/Documentation/Manual/class-AudioSettings.html).
The pre-existing ambient clips are serialized bundle assets, not procedural clips;
their authored two-stage delays mean an early snapshot with no playing clips does
not alone establish an audio failure. Diagnostic source readback now includes
actual AudioSource playback/sample values in addition to BetterSource's state.

## Final build — 2026-09-28, GPU-2 process `a0b19549`

Client SHA256 `2207A864D75DC72ADC8850B63BF111DFC6767800A9F7CF3DA03B860467079098`.
UiBridge confirmed all 23 plugin builds = host disk = loaded code after the
12:59:47 restart. Earlier final-build attempts were not accepted merely from
local disk identity: Syncthing had still delivered a newer DLL after process
startup, so the game was restarted after host-disk identity was confirmed.

### Bay5 — 13:01–13:03 CEST

- Native counts 40 rooms / 85 portals / 706 pairs / 5,924 routes loaded at
  13:01:13.482. Room tones started after reset at 13:01:28.024.
- At `(90.91,-2.9,74.24)`, listener and diagnostic source both occupy room 40,
  IndoorBrick. Actual source playback was true, volume factor 1, propagation,
  obstruction and occlusion all zero. LPF 18,000 Hz, HPF 80 Hz.
- Native `amb_bay5_arena_indoor` was **actually playing**, clip Loaded,
  AmbientIn bus 0 dB, listener unpaused. Sample cursor 263,423 at 13:01:50.041
  and 230,912 after looping at 13:02:05.233 confirm continued playback.
- At `(94.6,-3.9,88.8)`, listener changed to outdoor room 37 and room-tone
  controller cleared its indoor room. Same indoor source remained actually
  playing with volume factor 0.9125 and native filtering.
- At `(130,-3.8,3.7)`, listener room 17 (Hangar, IndoorMetalThin), that same
  source applied factor 0.0987, LPF 2,405.6 Hz, HPF 119.8 Hz. These factors
  are the native acoustic gain, not perceived loudness or distance attenuation.
- Existing harbor seagulls and shoreline loop were also actually playing at
  13:02:22.895; shoreline loop RMS 0.000390, sample cursor advancing.
- No new errors; the pre-existing inactive MenuScreen coroutine message
  occurred when opening the pause menu at 13:02:48.398.

### Equator — 13:04–13:05 CEST

- Bay5 unload 13:03:07.050 returned `Initialized=False`, groups=0. Equator then
  loaded 21 rooms / 50 portals / 210 pairs / 31,676 routes at 13:04:02.937.
- One north-side spawn `(-11.7,0.1,48.6)` lies outside the original acoustic
  volumes and correctly remained Empty Audio Room. Entering the authored room
  at `(-37.13,0.1,12.81)` classified room 2 and started room tones at 13:04:29.303.
  Source coverage is retained, not extended with invented rooms.
- `mall_indoor_ambient_tone` played with Loaded data, 0 dB indoor bus and
  progressing sample cursor: 312,345 at 13:04:36.387; 108,389 after looping
  at 13:04:57.055. The same-room remote source was actually playing with
  volume 1 and zero acoustic effects.
- Moving the listener to `(-5,0.1,-33.5)` changed it to room 4: same source
  applied gain 0.5399, LPF 14,724.7 Hz, HPF 119.7 Hz at 13:04:57.055.
- Existing ambient system: 25 players, 23 initialized, original two inactive
  dogs retained. Six original sources were playing at 13:04:57; nearby indoor
  wind had nonzero measured output. No new errors occurred.
- Diagnostic source disabled and its position restored to zero before return.

### Repeated Bay5 load — 13:06–13:07 CEST

Equator unload at 13:05:51.351 returned Initialized false and groups zero.
Bay5 then reloaded the exact 40/85/706/5,924 native counts at 13:06:26.165.
Room-tone startup occurred at 13:06:35.074. Re-entering room 40 produced Loaded,
actually playing `amb_bay5_arena_indoor`, sample cursor 338,687 at 13:06:58.884,
indoor bus 0 dB and listener unpaused. Probe remained disabled. No errors during
this raid; player alive, 440/440 HP, no bots. This completes the final-build
Bay5 -> Equator -> Bay5 sequence.

### Verification scope

Client Release build: zero warnings/errors. Python pipeline: all 445 discovered
tests passed or used their existing expected skips; the 20 new spatial tests all
passed, including both real local packages. The full distribution validator passed
14 maps / 42 bundles / 14 sidecars and Release DLL identities. Scene bundles and
server code were unchanged by this task. Independent source/runtime reviews found
no remaining necessary changes after the clip-reset correction.

Native acceptance measures actual room/source classification, propagation,
applied gain/filter values, clip playback and repeated teardown. The host's output
device was muted; subjective listening and sound balance remain the user's check.
No acoustic retuning, invented room coverage, full mixer reverb, outdoor ambience
blending or performance benchmark is claimed. A mid-raid audio-device change was
handled in code but not deliberately induced on the shared host.

### Final deployment and handover — 13:10 CEST

Both installations received the verified build through `tools/deploy-to-spt.ps1`:
`D:\SPT41` and `D:\SPT41vanilla`, 97 staged files each. A separate read-only
SHA256 comparison independently matched all 97 destinations in each install
against `dist/`, with no mismatches. Six new acoustic files total 6,542,571 bytes.
Ignored evidence: `work/spatial-audio-research/deploy-{host,vanilla}-final.txt`,
`install-hashes.json`, `final-gates.txt` and original decode/integrity probes.

Last unload at 13:08:01.191: native Initialized false, zero owned groups. GPU-2
is in MainMenu, no raid, control free, all 23 plugin builds/disk/loaded identical;
local menu profile health 440/440. Both NativeSpatialAudio switches true, silent
probe off and position zero. The original 11 changed settings (including saved
lighting and Clear weather) match the pre-test snapshot. No config file was
edited or deployed. Restart the user's game to load the client; server code is
unchanged. Vanilla's own in-game acceptance remains separate from GPU-2 testing.
