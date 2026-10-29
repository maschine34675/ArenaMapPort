# Equator static: one compressed bundle block played as audio - 2026-10-29

Record of the cause of the intermittent Equator noise that [spectator-audio.md](spectator-audio.md) lists as unresolved ("Investigation
2026-10-29 (lead): ranked hypotheses"). It is appended to as the repair is verified.

**Status (2026-10-29, late evening): cause found offline, repair and validator written, host verification pending.** The loud static in the
user's capture `noise-20261029-203147-mark1.wav` is not generated at run time. It is bytes of Equator's scenes bundle, still in their LZ4
compressed form, which Unity's player plays as if they were PCM samples of the ambient clip `moll_indoor_wind_04`. One 128 KiB block of
`arena_equator_scenes.bundle` carries the compression flag but is stored raw, and the build pipeline wrote it that way. The mod, the mix
and the spectator crowd are not involved, and no mix default or config key changes. The figures below are the analysis' own estimates, not
host measurements: 0.99 that this explains the static in the capture, about 0.9 that it is also the user's earlier "Rauschen" (Equator only,
intermittent). Nothing of the repair has run in a game yet (see "Host verification").

## 1. The user's captures

Log `D:\SPT41\BepInEx\LogOutput-MASCHINE11-2026-10-29_20-21-38.log` (the 20:21 log; plugin 0.4.0 of 2026-10-29 16:41), `NoiseProbe=Capture`
armed at 20:25:34 and 20:30:20, both armed lines `driver=Mode5point1 ch=6`. Four captures under `BepInEx/logs/ArenaMapPort-noise/`; the user names
`noise-20261029-203147-mark1.wav` (MARK #1 at 20:31:47.597, position (6.0, 0.1, 31.5), room 5/IndoorGeneric) as the one in which it is well
audible, and his word for the sound is "Rauschen". The first raid's capture (MARK 20:28:45.918, position (20.9, 5.5, 8.8)) and the
auto capture of 20:31:56 hold the same episode (the latter only in its first 0.74 s, sample-exact overlap). The log lines are quoted in
[spectator-audio.md](spectator-audio.md) "Host run of 52c8a28 and the user's captures".

Conventions of the analysis. WAV t = event - 10 s + t, so the mark is t 10.000 = 20:31:47.597; the ring-relative time is t - 10.000 (+-0.02 s).
Channel order FL, FR, C, ch3 (the LFE slot), SL, SR. ch3 is not a source: ch3 = 0.72 x (ch0+ch1+ch2+ch4+ch5), R2 0.994, so the probe log's
"loudest channel" and "@ch4" are artefacts of that sum. Levels are 5-channel dBFS (ch3 excluded). Pan: stream A sits on ch0+ch4 (FL+SL),
stream B on ch1+ch2 (FR+C), independent of each other (|r| <= 0.04 within +-1 s).

## 2. What the signal says

The carrier is the owned ambient player `indoor_wind (4)` (position (-3.51, 12.31, 5.35), 29.8 m from the stand spot), which plays the clip
`moll_indoor_wind_04` (bundle resource name `sharedassets56.assets_51`, 8000 Hz stereo, 29.395 s) at pitch **0.90583**. In the capture it played
from WAV t = -15.0715 (20:31:22.53) to t 17.38 (20:31:54.98); clip time = (t + 15.0715) x 0.90583. Its chain gain is 0.133 per clip channel:
volume 0.424 after `newVolume = Clamp01(v*10^(2/20)+0.4)` (`Bay5AmbientAudio.cs:475-477`) times a rolloff of 0.313 at 29.8 m, because the
authored maximum distance of 20 m is scaled to 50 m (`Bay5AmbientAudio.cs:425`, `AmbientRangeMultiplier` 2.5).

| WAV t (s) | level (5 ch) | content | attribution |
|---|---|---|---|
| 5.159 | | start of block 6657 inside `indoor_wind (4)` (clip 18.3257 s) | predicted from the block table; the first 0.85 s of the block are an LZ4 literal run (real PCM bytes), nearly normal |
| 6.037-7.792 | -21.0 (streams A and B -23.3 each) | static #1: ON 6.037-6.496, 6.510-6.731, 6.868-7.511, 7.623-7.792 | LZ4 token and offset bytes of the block (clip 19.12-20.83 s) read as 16-bit stereo, times 0.133; flat 100-3200 Hz, centroid 1.5 kHz |
| 7.795-8.72 | -35.6, -38.4 | OFF: literal runs of the block, real quiet wind PCM | |
| **8.727-9.6805** | **-22.9**, about 34 pieces | **static #2, the part the user reacted to** (mark -1.27..-0.32 s) | tail of the block (clip 21.58-22.42 s) |
| 9.6805 | ch3 -17.7 dB in 1 ms | **dead stop = end of block 6657**: predicted 9.6812 (clip byte 717,496 = 22.4217 s), measured 9.6805, 0.7 ms off | one source stops, everything else continues |
| 10.000 | | MARK #1, 0.32 s after the stop | where a human mark lands after a sound ends |
| 10.75-12.0 | -47.3 | steady hiss | not explained, see section 8 |

The part before it (WAV t 0-5 s) is bass wind, quiet metal and spectator voices at -37..-47 dBFS, 24-40 dB below the static.

Why static #1 and #2 are block 6657 (six independent checks of the analysis):

1. **Resampler clock.** The second-difference comb of the ON stretches sits at 7246.45 Hz (+31..+39 dB over the median, five runs, identical
   phase) = 8000 Hz x 0.90581; the waveform-refined pitch of the `indoor_wind (4)` voice is 0.90583, 0.002 % away (chance for a random pitch in
   0.9-1.1 to land that close: 0.03 %). Raid 1 (20:28:45): comb 8688.75 Hz = 8000 x 1.08609, and a free least-squares scan of the six 8 kHz
   Equator clips at pitch 1.0845-1.0877 selects wind_04 at 1.0861 (sum R2 1.44 over ch0-5; every other clip <= 0.21).
2. **Same data at the same clip position in both raids.** Mapped to clip time, the maximum cross-correlation of raid 2 against raid 1 at +1.0 ms is
   0.43 (clip 19.3-20.5), -0.50 (19.5-20.6) and **0.79 (21.75-22.4)**; a control window gives -0.03, 0.03, 0.06.
3. **The bundle is right, the played bytes are wrong.** The clip read with UnityPy from `work/build/full/arena_equator_scenes.bundle` is bit-identical to
   the extracted `sharedassets56.assets_51.wav` (235,163 frames, 0 differing). The block table (section 3) has exactly one compressed-flag block
   whose stored size is not smaller than its content, and that block holds clip bytes [586,424, 717,496) = clip 18.3257-22.4217 s = WAV t 5.159-9.681 s.
4. **Sample-exact fit.** With the block's 131,072 raw (still compressed) bytes in place of the decoded PCM, fitting ch0, ch1, ch2 and ch4 (300-3400 Hz) on
   the left and right channels of that variant clip at pitch 0.90583 gives R2 0.929/0.906/0.930/0.913 (clip 19.30-20.50 s, lag -0.06 ms) and
   0.946/0.889/0.953/0.898 (21.75-22.38 s, lag -0.125 ms); the clean clip gives R2 0.000.
5. **Level and shape.** The raw block read as PCM is -5.2 dB in its token-dense stretches (uniform full-scale int16 would be -4.8 dBFS); the measured
   static is -22.7 dB, an offset of -17.5 dB = 20 log10(0.133), the chain gain. The envelope correlation of the measured HP800 level with the
   raw-block-as-PCM envelope (1 ms bins) is +0.806 at lag 0. Linear-interpolated uniform noise has kurtosis 2.07 and the measured ON stretches
   2.09-2.23 (crest 2.3-2.6); Gaussian noise would be 3.16 and 4.7, so the earlier "Gaussian, kurtosis 3/duty" statement of the investigation is wrong.
   The predicted per-stream level for full-scale uniform noise through gain 0.133 is -24.1 dBFS, measured -23.3. The OFF stretches are the literal
   runs of the block, real and quiet.
6. **Scan.** Only this bundle has such a block (section 3).

## 3. The block

`work/build/full/arena_equator_scenes.bundle` is a UnityFS file of 577,488,350 B (plaintext, ignored by git): header flags 0x243 (the block table
LZ4HC-compressed at the head, 16-byte alignment after it), version >= 7, 6,943 blocks of at most 128 KiB, block data starting 16-aligned at file offset 27,392 and ending
exactly at the end of the file. Exactly one block has a compression flag and no gain from it:

| Field | Value |
|---|---|
| block | 6657 of 6943 |
| flags | 3 (LZ4HC) |
| csize == usize | 131,072 |
| uncompressed stream | [872,546,304, 872,677,376) |
| file bytes | [553,666,425, 553,797,497) |
| node | `BuildPlayer-Arena_Equator_Scripts.resource` |
| clip | `moll_indoor_wind_04`, PCM starts at stream offset 871,959,880 (96-byte FSB header), so the block holds clip bytes [586,424, 717,496) |

The block decodes with `lz4.block.decompress` into exactly 131,072 bytes, which are the clean PCM. Unity's player evidently takes a block with
csize == usize as stored and never decodes it, so the raw LZ4 token stream reaches the AudioClip. Other bundles under `work/build/`: the Bay5 (11,807
blocks), Air Pit (11,698) and Bowl (11,386) bundles and all older builds have 0 such blocks. Blocks within 16 B below usize do exist (Bay5 1, custom 4,
factory 1, shoreline 1, woods 5), so the defect is a per-rebuild lottery for any bundle, not an Equator property.

## 4. The cause in the pipeline

`UnityPy.helpers.CompressionHelper.chunk_based_compress` (installed copy, UnityPy 1.25.2) writes a chunk raw, with the compression flag cleared, only
when `len(compressed_data) > chunk_size` (line 191 for the full chunks, line 213 for the last chunk). A chunk whose LZ4HC output is **exactly** the
chunk size fails that test: it is written compressed, with the flag set and csize == usize. The comparison needs `>=`. Our pipeline calls it from
`tools/bundle/patch_monoscript_identity.py` (`BundleFile.save(packer=...)`, around line 451), the mandatory step 3 of `tools/build_all.ps1`. The
bundle also holds 48 ordinary stored blocks with flag 0 (compression flag cleared, csize == usize), which Unity loads without trouble: block 6657 is
the one that was written with the flag still set.

## 5. Why no probe saw it

- The event ring logs `isPlaying` edges of owned one-shots only (`amb+`, `amb-`, `amb~`, `Bay5AmbientAudio.cs:636-704`). `indoor_wind (4)` was
  "just playing" the whole time: it had been playing for 24.75 s when the static stopped, and the ring has no event within 0.8 s of that stop.
- The detector keys on transients (clicks, peaks, dropouts). A continuous -21 dBFS plateau gave `loud=0 clip=0` in the minute line; it did fire on the
  first token bytes (BURST #11, a click, at WAV t 6.001-6.033).
- The census counts pooled `BetterAudio` sources only, and `indoor_wind (4)` is an owned, non-pooled player.
- The blind clip search of the investigation compared the capture against the correct decoded clips, while the played data were compressed bytes
  (blind search |r| <= 0.157 against 65 clips and 13 voices, positive control 0.856). Pitch lock found the right source: hypothesis H3 of
  section 9 (an owned Equator ambient clip the matcher did not recognise) had the right source and the wrong mechanism.
- It is audible at the user's spot only because of the port's `AmbientRangeMultiplier` 2.5: the authored maximum distance of 20 m becomes 50 m, the
  listener stands 29.8 m away. That is how the mix was tuned, not a mix bug, and nothing in it changes.

## 6. Release consequence

- The 0.3.0 public Equator scenes bundle is byte-identical to `dist/BepInEx/plugins/maschine-ArenaMapPort/bundles/arena_equator_scenes.bundle`
  (AMPB-encrypted, 577,506,078 B, sha256 `1ad0431d1b58d5f30a009a919c0e47f35215d1aafbb1fc5db8564f02d751505d`). `ArenaMapPort.BundleCrypt verify
  --map-key arena_equator --plain work/build/full/arena_equator_scenes.bundle --enc <file> --key-store work/release-keys` prints `OK ... is exactly the
  encryption of` the defective plaintext for the `dist` file and for the copy under `D:\SPT41\BepInEx\plugins\maschine-ArenaMapPort\bundles\` (read
  only, no key printed). The `dist` file time is 2026-10-24 01:16 and the tag `v0.3.0` is 2026-10-24 15:01. So **0.3.0 and the local 0.4.0 packages
  (`fe45b84`, `github-a4aa0af`) carry the defect**, on Equator only.
- The repaired bundle has the same size; one block and one table field change. After a re-stage only chunk 0 and the chunk holding file offset
  553,666,425 differ in the AMPB file, the content key `K_map` and the key file `keys/arena_equator.json` stay, and the client and server DLLs are
  unchanged (no rebuild).
- The 0.4.0 package is to be re-cut after the repair (the Equator part and the checksum file change); publication stays postponed (HANDOFF.md).
  Whether the already public 0.3.0 gets a small 0.3.1 that replaces only the Equator scenes bundle (577 MB, same key, no code change) is a user decision.

## 7. The repair and the validator

**Do not change any mix default.** No existing key changes meaning and there is no new public key.

1. **Repair step.** `tools/bundle/fix_stored_blocks.py` (modes: repair, and `--check` which only reports) parses the UnityFS header (version >= 7, 16-byte
   alignment, flag 0x200 aligns after the block table; a block table at the end of the file, flag 0x80, is refused), LZ4-decodes the block table and,
   for every block with `(flags & 0x3F) != 0` and `csize >= usize`, LZ4-decodes the block (a block that does not decode is refused), stores the
   decoded bytes and sets the block flag to 0. The table is recompressed with LZ4HC, the header sizes are rewritten and everything else is copied. It
   runs in `tools/build_all.ps1` directly after the identity patch for every `*.bundle`, always, and is idempotent. It has to be a separate step: the
   identity patch leaves a bundle with clean MonoScripts untouched, so it never re-saves one.
2. **Corrected compressor.** `patch_monoscript_identity.py` installs a chunk compressor with `>=` before `BundleFile.save`, so the pipeline no longer
   writes such a block. UnityPy itself is not changed; the defect is to be reported upstream.
3. **Validator.** `tools/validate/validate_bundle_blocks.py` fails when any block has a compression flag and `csize >= usize`. It is called from the
   validators of `tools/build_all.ps1` and from `tools/validate/validate_distribution.py`; for an AMPB file in `dist/` it reads header and table through
   `ArenaMapPort.BundleCrypt read-range` (the first part of the plaintext), so it needs the VM's key store and Arena like the rest of the distribution
   validation. This is the check that would have caught the file on 2026-10-24. **Never ship a bundle without it.**
4. **Tests.** `tools/tests/test_bundle_blocks.py` builds a tiny synthetic UnityFS (a real colliding block cannot be synthesised on demand), so no
   BSG-derived fixture is involved.

Result of the proof of concept on a copy of the real bundle (0.6 s): bad blocks [6657] -> [], size 577,488,350 unchanged, table csize 27,319 unchanged,
the hash of the correctly decoded stream **88013276d833b2c9350ffcf822788f78954e7cf58a5c5e56eac60c2bd89d49ea** identical before and after, a player model
that takes flagged csize == usize blocks as stored hashes to `c781fe02...` before and `88013276...` after, UnityPy loads the repaired bundle and the clip
is identical. The shipped path is: repair the plaintext `work/build/full/arena_equator_scenes.bundle`, re-stage with a Release client build without
`ArenaMapPortDllOnly` (`K_map` exists, no `--create-key`; the output is deterministic), then `validate_distribution.py` and
`validate_port.py --distribution` (ENC must be exactly the encryption of PLAIN), then the usual deploy with EFT closed (one 577 MB file; restart EFT
afterwards because a loaded bundle stays cached; no server restart).

## 8. What stays open

- **The steady hiss** at WAV t 10.75-12.0: -47.3 dBFS (ch5 -55.6, ch1 -58.1), a mono point source near +80 degrees, flat PSD -96 dB/Hz over 0.4-9 kHz.
  Present in every quiet window of both raid-2 captures, 35 dB below the static, not the electricity loop, not room tone, not this block. Probably
  inaudible at normal gain, unexplained.
- **The click bursts** (#4 and #8, peak -12.3..-12.5 dBFS, <= 50 ms) are not explained by this defect. Hypothesis H10 (the starts of one-shots such as
  the water drops) accounts for these two only.
- **The boosted near wind bed** is a taste question and a separate mix decision: the bass bed at the start of the capture is `indoor_wind (4)` (about
  75 %) plus `indoor_wind (1)` (about 11 %), and `indoor_wind (1)` at 2.4 m reaches -26 dBFS (centroid 300-430 Hz) for about 27 s per cycle.
- **Unity's behaviour for csize == usize** is inferred from the data (R2 0.92), not read from Unity's source. The repaired bundle is also valid under the
  opposite behaviour: a stored block with flag 0 is what 48 other blocks of this bundle already are, and Unity loads them.
- **Which sound the user means.** Three things are in the 20:31:47 WAV: the 3.6 s crackling static (t 6.0-9.7 s, about every 45 s for about 4.5 s; fully
  explained and fixed here), the bass rumble at the start (wind) and the faint constant hiss at the end. The static is the one he reacted to; his
  confirmation that it is "the Rauschen" he knew from play is still open.

## 9. Outcome for the earlier hypotheses

The ids H1-H10 are those of the forensics round over the captures (HB is the new one). "Corrected P" is the probability that the hypothesis explains the
loud static the user marked (WAV 6.0-9.68 s), after adversarial verification (three lenses each: signal, code, timing):

| id | Hypothesis | Corrected P | Verdict |
|---|---|---|---|
| HB | block 6657 (LZ4HC flag, csize == usize) played raw by `indoor_wind (4)` | **0.99** | confirmed by checks 1-6 |
| H1 | an unnamed, un-tapped source | 0.005 | the carrier is the owned player `indoor_wind (4)`; the signal is not Gaussian |
| H2 | Meta XR / FMOD DSP-path artefact | 0.001 | clock, panner pairs and 0.5 ms edges without a tail say otherwise; `KeepReverb=false` reproduced it on the morning of 10-29 |
| H3 | an owned Equator ambient clip the matcher missed | 0.005 | right source, wrong mechanism (compressed bytes, not the decoded clip; clips are DecompressOnLoad PCM, not streamed) |
| H4 | boosted near-field wind bed | 0.002 | not for the static; the bass bed is real but ended at least 5.6 s before the mark |
| H5 | steady hiss at +80 degrees | 0.002 | 35 dB under the static; unexplained |
| H6 | ch3 full-band downmix on the user's rig | 0.005 | an amplifier question; adds no content |
| H7 | spectator voices as murmur | 0.003 | -37..-47 dBFS against -23; edges uncorrelated with voice envelopes (r +0.003..0.12 against a null of 0.23); Air Pit control 320 against 318 calls, noise only on Equator |
| H8 | streaming, IO or DSP starvation | 0.001 | dropout 0, gap 0; the bytes are deterministic |
| H9 | the probe tap itself | 0.0 | the user heard it before the probe existed |
| H10 | one-shot starts as ticks | 0.0 | explains click bursts #4 and #8 only |

Against the five-hypothesis table of the lead's investigation in [spectator-audio.md](spectator-audio.md): #1 (Equator's ambient one-shots at the boosted mix)
named the right family, an owned Equator ambient player at the port's boosted range, but not the mechanism; the one-shots (crackles, water drops, metal) are
refuted as the static, the carrier is the wind player and what it plays is compressed bytes. #2 and #4 (spectator onset overshoot, hard cut at death) are
refuted for this static (H7; the dead stop is the end of the block, 0.7 ms off). #5 (a fault below Unity) is refuted for this static (it is in Unity's
pre-device PCM). #3 (physics-prop impacts) is not the loud static, which has the resampler clock of the wind_04 player; it was not tested otherwise.

Also refuted explicitly: ambient one-shots and spectator voices as the loud static (blind search above), mixer, listener or snapshot steps at 9.68 s,
retrigger or grain loops, block-synchronous gating (edge phases modulo 1024 down to 32 are uniform), the electricity loop (its "restarts" are loop wraps),
a device or driver fault (the static is in Unity's pre-device PCM), the probe's self-test signals. "The detector sees nothing at the drop" is
answered in section 5. Raid 1's static, mixed with gunfire, is the same block (clip 18.96-22.77, carrier wind_04 at 1.0861).

## Host verification

**Pending.** The lead runs it on GPU-2 (muted host, no listener needed: the tap sees Unity's pre-device mix; GPU hosts run Mode5point1 over a stereo
device and the probe reads `ch=2/6`). Nothing below has been measured yet.

Setup: F12 with the Advanced settings filter on, `General/DeveloperMode` on, `Audio diagnostics/NoiseProbe` = Capture, `NoiseProbeMarkKey` as
it is (Pause, or Mouse3/Mouse4), `NoiseProbeMuteAmbient` empty, `SpectatorCalls` true, and the release mix untouched (`Audio (arena_equator)/AmbientGainDb`
2, `AmbientVolumeOffset` 0.4, `AmbientRangeMultiplier` 2.5: the defect needs `indoor_wind (4)` audible at 29.8 m). Start a plain Equator raid,
`uibridge_teleport` to (6.0, 0.1, 31.5) and stand there. Cadence: `indoor_wind (4)` restarts every 37-53 s (32.5 s of wall time for the clip at pitch
0.906 plus a gap of 5-21 s); each play carries one 4.5 s episode that starts 18.33/p (16.7-20.4 s) after its start, its loud part 19.1/p to 22.4/p.

| Step | Action | Expected by numbers | Result |
|---|---|---|---|
| Before (positive control, bundle as deployed now) | stay 3 wind cycles (about 2.5 min), press the mark key within 1.5 s after each static ends | each MARK WAV: HP800 5-channel plateau at about -21..-23 dB for 1.5-3.6 s with gaps; `static_detector.py` ON >= 1,500 ms; second-difference comb at 8000 x pitch (6.5-9.0 kHz) +30..+40 dB; ring silent at the edges; minute line `clip=0 loud=0` | pending |
| Deploy | repaired bundle staged and deployed with EFT closed, EFT restarted | the Debug `[Bundles] loaded ...` line reads encrypted AMPB v1 with the chunk count of before; no `[Bundles]`, `[BundleKeys]` or `[NavMesh]` Error | pending |
| After | the same raid and spot, same duration | `static_detector.py` ON about 0 ms in every 12 s window that contains 16.7-24.9 s after an `amb+ indoor_wind (4)`; no comb peak above +10 dB in 6.5-9.0 kHz; least-squares fit of the raw-block model R2 about 0 | pending |
| After, no ring event | if a window holds no `amb+ indoor_wind (4)` | minute lines with `NoiseProbeMuteAmbient` empty: `loud=0 clip=0`, `bgMedian` the same as in the muted run | pending |
| Static, no game | `validate_bundle_blocks.py` on `work/build/full` and on `dist` | passes after the repair, fails on the pre-repair file (block 6657); the block scan reports 0 compressed-flag blocks with csize >= usize | pending |

An optional control for an unexpected result: `NoiseProbeMuteAmbient` = `indoor_wind (4)` (exact name, live) removes the sound if the player is the
carrier, and `AmbientRangeMultiplier` = 1.0 (live) makes it inaudible at 29.8 m because its maximum distance of 20 m then falls short.

## Evidence

The analysis scripts and the synthesis (`synthesis2.md`, `static_detector.py`, `fix_stored_blocks.py`, `fix_verify.py`, `s2_blocks2.py`,
`s2_scan_blocks.py`, `s2_lz4_garbage2.py` and the other `s2_*.py` files) are in the lead's session scratchpad folder `noise2`, not in the repository. No
key, no hash of an Arena file and no Arena file content appears here; the only hashes are of our own bundle and its decoded stream. A repaired copy made
for the proof of concept is not for deployment; the shipped file is re-staged from `work/build/full` after the pipeline change. Related:
[spectator-audio.md](spectator-audio.md) (the NoiseProbe that recorded it), [identity-fix.md](identity-fix.md) (the step that wrote the block),
[bundle-encryption.md](bundle-encryption.md) ("LZ4 rebuild" of 2026-10-20, which gave the bundle its 128 KiB blocks).
