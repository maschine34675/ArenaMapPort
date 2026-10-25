# Gate and door sounds (2026-10-25)

Point 5 of the 2026-10-25 work list: every gate the port moves (SpawnGates, [door-poses.md](door-poses.md)) opens in silence. Arena plays a
clip of its own on each of them. This item plays those clips, on all four maps, when a mode plugin (ArenaMode) or the developer test hold
RELEASES the held gates. Status: implemented and unit-tested on the build machine; **not yet heard or logged on a host** (section
"Host checks"). Research and review behind the numbers: the 2026-10-25 plan `sounds.md` and its review R1 to R8 (scratch, not tracked);
the decisions below are the lead's.

## What Arena does

An `Audio.Arena.EntitySoundPlayer` (a 100-byte MonoBehaviour: 32 bytes of head, then 17 words) sits on an emitter object and names an
AudioSource, an AudioClip, a volume, a pitch, a min and a max distance and a mixer group. Its layout, decoded from the raw bytes and
checked against four levels (56 Equator, 188 Bay5, 39 AirPit, 111 Bowl):

| word | field |
|---|---|
| w0 | entity id (negative scene ids on Bay5, Bowl and AirPit level 28; 0 on Equator and AirPit level 39) |
| w1-w3 | PPtr to the AudioSource (file id, path id as int64) |
| w4-w6 | PPtr to the AudioClip (file id of the level's externals, path id) |
| w7, w8 | volume (0.9 to 1.0), pitch (1.0; 1.25 AirPit passports, 1.2 AirPit bus doors) |
| w9, w10, w13 | floats, unknown and unused (0, 0 to 0.1, 0.15 to 0.3) |
| w11, w12 | min distance (0.4 to 1.0), max distance (30 to 60) |
| w14-w16 | PPtr to the AudioMixerGroup: resources.assets 8907 = `ArenaGates`, or none |

The mixer group `ArenaGates` (`Master/InGame/World/Main/Ambient/ArenaCommonAmb/ArenaGates`) is **-15.0 dB** in all three snapshots of the
master mixer (the ancestors stand at 0 dB; snapshot 1 reads -14.96 because Ambient is +0.04), with an attenuation effect and a send to
ArenaEffects (that bus is -80 dB in snapshot 1, so no reverb return either). The extractor reads the level from the mixer and refuses a
spread of more than 0.1 dB.

Which player is for which gate: the emitter's horizontal distance to the gate unit (at most 2.0 m, the next gate of another assembly at
least 4.0 m away; the extractor stops otherwise). Of the players in the four maps' scenes 24 belong to a gate of the port; the others are
listed as `skipped` in the sidecars: Bay5 2 and Bowl 2 (ShootOut gates), AirPit 10 (two passport leaves and two respawn gates that no gate
of the port is near, and six level-39 twins of the passport leaves, see below). 24 rows plus one borrowed row make the 25 below.

## Rows (`tools/maps/<stem>.gatesounds.json`, git-tracked)

Names and numbers only, written by `tools/extract/extract_gate_sounds.py rows` (byte-stable; `rows --check` compares). A row belongs to a
gate part when the barrier is equal, the part is `null` (every part of the barrier instance) or equal (`""` = the barrier root itself
moves), `at` is `null` or within 0.05 m of the part's barrier instance and `closed_position` is `null` or within 0.05 m of the part's
closed local position: the keys and tolerances of the door rows (`ArenaDoorPose.AtTolerance`, `ClosedPositionTolerance`). Fields:
`emitter` (world position), `source_volume`, `player_volume`, `authored_pitch` and `pitch` (what the client still applies: always 1),
`min_distance`, `max_distance`, `mixer` / `mixer_db` (the player's group), `source_mixer` (the AudioSource's own group), `origin`,
`match_distance`.

| map | rows | which |
|---|---|---|
| Equator | 4 + 1 borrowed | the four booth door assemblies (`part` null: both leaves share one sound; the root of the assembly is where the emitter stands) and the fifth door (Respawn_2 Door_Sliding_Respawn (3)) with a row borrowed from its neighbour: Arena authors no emitter for it (it authors no motion either); the row copies the lender's sound and puts the emitter at the same offset (+0.979 m up, +0.005 m along z) from the door origin; the recipe is `borrowed_rows` in `tools/extract/gate_sound_fit.json`, not a literal in the tool |
| Bay5 | 5 | three warehouse rollers (`part` `""`) and the two depot gate assemblies (`part` null: both leaves stand at the frame origin, so the root wins the tie); `Door_01 (3)` has no motion and no emitter and stays silent |
| AirPit | 11 | 8 passport leaves, keyed by closed position (the 16 leaves reuse six names; Arena sounds only the row at local z 5.938, the row at z 3.099 is silent there too) and the 3 front bus-door assemblies |
| Bowl | 4 | BTR, respawn gate (roller), army gate, ambulance |

AirPit's passports sound from the **eight players on the leaves of the TeamFight scene (level 28)**: no mixer group, AudioSource volume
0.2 times player volume 0.9, which is -14.9 dB, the same level as every other gate. The level-39 sound scene holds twins of them that
name ArenaGates (they would play at -29.9 dB): the tool keeps the level-28 players and lists the twins as skipped duplicates.

## Clips (`work/extract/gate-sounds/<map_key>/`, git-ignored; staged to `dist/.../gate-sounds/<map_key>/`)

Nine clips, decoded with UnityPy (fmod for Vorbis) to PCM16 WAV, 44.1 kHz stereo, **9,380,584 bytes** in all (the original decodes were
9.6 MB; the two baked AirPit clips are shorter). `gate-sounds.json` carries schema 1, the rows of the sidecar, the mixer and per clip:
`file`, `channels`, `sampleRate`, `frames`, `bytes`, `sha256` (of the shipped WAV), `pitch_baked` / `baked_pitch`, the `source` (asset, path
id, format and the resource file, offset and size, never a hash of an Arena file) and the `fit` block.

| clip | map | s | bytes | fit | clean seams | removal -> end event at 0.75 | at 0.5 | at 0.25 |
|---|---|---|---|---|---|---|---|---|
| `arena_equator_old_slide_doors` | Equator | 3.569 | 629,628 | body_cut, event 3.0 s, body 0.45-2.85 s | 12/22 | 0.703 -> 2.297 | 1.303 -> 1.697 | 1.504 -> 1.496 |
| `arena_bay5_roller_door_full` | Bay5 | 5.180 | 913,824 | body_cut, event 4.02 s, body 0.5-3.85 s | 30/31 | 1.000 -> 3.020 | 1.999 -> 2.021 | 2.999 -> 1.021 |
| `arena_bay5_big_gates` | Bay5 | 6.104 | 1,076,868 | body_cut, event 2.55 s, body 0.6-2.4 s | 11/16 | 0.596 -> 1.954 | 1.205 -> 1.345 | 1.205 -> 1.345 |
| `arena_airpit_metal_gates` | AirPit | 3.200 | 564,524 | as_is, pitch baked x1.25 | | | | |
| `arena_airpit_bus_doors` | AirPit | 2.123 | 374,584 | as_is, pitch baked x1.2 | | | | |
| `arena_bowl_btr_gates` | Bowl | 12.000 | 2,116,844 | body_cut, event 5.95 s, body 2.0-5.7 s | 29/35 | 1.398 -> 4.552 | 2.900 -> 3.050 | 3.301 -> 2.649 |
| `arena_bowl_emergency_car_gates_full` | Bowl | 7.236 | 1,276,420 | body_cut, event 6.16 s, body 0.6-5.9 s | 44/51 | 1.496 -> 4.664 | 3.001 -> 3.159 | 4.596 -> 1.564 |
| `arena_bowl_double_metal_gates` | Bowl | 7.625 | 1,345,092 | body_cut, event 4.88 s, body 0.9-4.7 s | 26/36 | 1.195 -> 3.685 | 2.403 -> 2.477 | 3.303 -> 1.577 |
| `arena_bowl_roller_gates` | Bowl | 6.138 | 1,082,800 | body_cut, event 4.75 s, body 0.4-3.8 s | 32/32 | 1.096 -> 3.654 | 2.301 -> 2.449 | 3.204 -> 1.546 |

### Pitch bake (review R1, stage 2 of the Arena gate)

`tools/bundle/key-sources.json` lists `sharedassets28.resource` for `arena_airpit` (scene 28). That file is 705,728 bytes, so the key
derivation samples it whole, and it is the `arena_airpit_metal_gates` clip: its 705,600 PCM bytes are bytes 96 to 705,696 of the file.
Shipping the decoded WAV would publish 99.98 % of a whole-file key input in plaintext (not a total break, six other AirPit key files
stay unpublished, but exactly the class of leak [bundle-encryption.md](bundle-encryption.md) forbids). The authored players play the
clip at pitch 1.25 (and the bus doors at 1.2), so the extractor **resamples both AirPit clips by their authored pitch** (windowed-sinc,
Kaiser window, cutoff at the faster clip's Nyquist; 4.0 s to 3.2 s and 2.548 s to 2.123 s). The shipped bytes are no copy, the client needs no
resampler, `pitch` is 1.0 in the manifest and `pitch_baked` is true. The other eight clips are no key source of any map (the files are
sharedassets56, 87, 39 and 111; Bay5 and Bowl clips are Vorbis). Guards, all of them fail closed:

- the extractor refuses a clip whose resource file is a key-source file of any map unless `bake_pitch` is set (`prepare_clip`);
- `tools/validate/validate_distribution.py` (`validate_gate_sounds`) refuses it too, from the staged manifest;
- `test_gate_sounds.py` searches 512-byte windows of every shipped WAV in every key-source file of its map (mmap, skipped without Arena)
  and proves the detector with a positive control (a slice of the unbaked clip is found in `sharedassets28.resource`).

## The fit (Spawns/GateSoundFit)

The port opens a gate in `SpawnGateDriveScale` (default 0.5) of Arena's time, the clip runs as long as Arena's motion: played as it is the
end event (the thunk or bang that closes the motion) comes 1.3 to 2.6 s after the door stopped. The extractor measured per clip where the
motion ends (`event_s`) and where ONE stationary stretch can go: for every removal on a 0.1 s grid it searches the cut with the least
audible seam (80 ms band-energy and level cost on a 10 ms grid, then a +-5 ms alignment by correlation) and stores `c0f` / `c1f` (frames),
`remove_s`, `ramp_db` (the material from the body start to the seam is ramped, linear in dB, so its level meets the level after it),
`jump_db`, `centroid_jump_hz` and `ok` (jump within 1 dB of the clip's natural 50 ms level changes, centroid jump within 1.5 kHz). The
client picks the largest `ok` entry whose removal does not exceed (1 - scale) x `event_s`, cuts it (a 60 ms equal-power crossfade; head,
the end event and the tail are untouched) and plays that. No FFT at run time. At a scale of 0.95 or more, for an `as_is` clip and when
no clean entry is short enough, the clip plays whole.

The end event does **not** land within 0.01 s of the ideal for every clip. With the clean tables, at scale 0.5 it lands late by:
Equator 0.197 s (the largest clean removal is 1.303 s, 1.4 to 1.5 are rejected; it lands at 1.697 s, the port's visible end of the door is
1.71 s), Bay5 roller 0.011, depot 0.070, Bowl BTR 0.075, ambulance 0.079, army gate 0.037, respawn gate 0.074 (the table above). At 0.25 five
clips are 0.36 to 1.16 s late. That is still far better than as-is, and the tests pin the picks per clip (`test_gate_sounds.py`
`StagedPackageTests`, `GateSoundModelTests.TheRealTablePicksAtScaleHalf...`), not a blanket tolerance. `remove_s` is not on a clean 0.1 s
grid (1.303, 1.205, 2.403 ...: the alignment moves c0 / c1), so `c1f - c0f` equals it within 1 ms only. Equator's seam is the marginal one
(jump 0.57 dB at 1.303 s with a +1.8 dB ramp); if it is audible, set that clip's mode to `as_is` in `gate_sound_fit.json` and re-run
`clips`, nothing else changes.

## Client

`GateSoundModel.cs` and `PcmWav.cs` are Unity-free and linked into `ArenaMapPort.Server.Tests`; `GateSounds.cs` is the glue.
`SpawnGates.cs` changed in four places (the pins in `GateSoundWiringTests` and `DoorPoseWiringTests` hold): a `Key` field on `PosedPart`
(`GateSoundModel.PartKey`: the barrier, part and `At` of the first pose row of the part and its closed local position, set where
`NotePoses` builds the part, whose class is private, so only the key crosses over), `GateSounds.Prepare()` after `_posed[part] = posed;`,
`if (drive) GateSounds.OnRelease(SoundKeys(drivers), scale);` in `Open` right after `_openLongestSeconds = longest;` (the existing local
`scale`, no second `_driveScale.Value`, no `SetActive(false)`; lamp parts are not handed over) and `GateSounds.OnHold();` in `Hold`.
`GateSounds.Install()` follows `SpawnGates.Install()` in `HarmonyPatches` (it reads SpawnGates' `SpawnGateDriveScale` entry through the
config file for the prepare-time guess).

- **Silent by design** where nothing drives: a plain raid (the gates are parked open at raid start, `Release` without a hold returns before
  `Open`), a Hold, a re-close, a snap (`SpawnGateDrive` false). **The listening test therefore needs `Spawns/SpawnGateHold` = true
  (+ `SpawnGateHoldSeconds`) or ArenaMode**, not a plain raid.
- **Prepare** (first `NotePoses` of the raid): a coroutine on the plugin reads the manifest and builds one clip per frame
  (`AudioClip.Create(stream: false)` + `SetData`, stereo kept) for the drive scale that is set. A release that comes first loads what it
  needs on the spot (one hitch). Length is checked against the manifest at load; the SHA-256 is checked by `validate_distribution.py`
  (a managed SHA over 6 MB in EFT's Mono would cost the raid-start frame hundreds of ms). A clip is built again for another seam (the drive scale
  changed) and after an audio reset (`AudioSettings.OnAudioConfigurationChanged`: Unity discards the data of script-created clips).
- **Release**: rows = the first applicable row per driven part, each once (both leaves of a booth door, both leaves of a depot gate: one
  sound). `BetterAudio.PlayAtPoint(position, clip, AudioSourceGroupType.InteractiveObjects, rolloff, volume, occlusion)` through
  `MonoBehaviourSingleton<BetterAudio>.Exist`, in the first frame of the drive, after `GameStatus.Started` (a release before it waits,
  EFT's raid-start audio reset stops earlier sources). The occlusion mode is decided once per release (OneShotPropagation when
  `SpatialAudioSystem.Initialized`, else none) and PlayAtPoint is never retried: BetterAudio plays the clip before it processes the
  occlusion, so a retry after an exception would play it twice and leak a source. `volume` = `source_volume` x `player_volume` x
  10^(`mixer_db`/20) x `Spawns/GateSoundVolume`; `rolloff` = round(`max_distance` x `Spawns/GateSoundRolloffScale`).
- **Hold** stops the sources the last release started and still plays (BetterAudio's own queue gives a stopped source back at the end of
  its clip, as with EFT's one-shots); **scene unload** (the owned Scripts scene only) destroys the clips.
- Developer keys, all on `Plugin.Instance.Settings`, none public: `Spawns/GateSounds` (true), `Spawns/GateSoundVolume` (live, 0 to 8, 1; the cap was raised from 2 to 8 on 2026-10-25 so the -15 dB default can reach EFT's own gate level),
  `Spawns/GateSoundFit` (true), `Spawns/GateSoundRolloffScale` (1, 0.5 to 4). No public key means no entry in
  `CompactConfiguration.IsPublic`, `MapConfigVisibility.IsPlayerOption`, the README or the package texts.
- Logging: Debug for every row and one summary per release (`[GateSounds] <origin> / <clip>: volume, reach, position, occlusion`, `released N
  part(s): R sound row(s), S playing, ...`, one frame later `K/N playing`), Warning once per clip or package that fails, nothing at Info.

Gain table (the baked mixer level follows the player's group): Equator, Bowl -15.0 dB, Bay5 rollers -15.9, depot gates -17.5, AirPit bus
doors -19.2, passports -14.9.

## Staging, validation, packaging

- Order of the work (review R7): extractor and package first, then the csproj target, then the validators, then the client. The target
  `ArenaMapPortStageGateSounds` (`AfterTargets="ArenaMapPortDistCopy"`, Release including DllOnly builds) copies each map's
  `gate-sounds.json` and `*.wav` to `dist/BepInEx/plugins/maschine-ArenaMapPort/gate-sounds/<key>/` and raises an Error naming
  `tools/extract/extract_gate_sounds.py clips` when an input is missing (a fresh clone needs the extraction before a Release client build).
- `validate_distribution.py`: exact file set per released key, `schema_version` 1 and the map's key, manifest byte-equal to the extracted
  one, rows equal to the tracked sidecar and each resolving to a transform row of the doors sidecar, mixer ArenaGates at -15.0, every
  row at pitch 1 with its clip, length and SHA-256 of every WAV, RIFF/WAVE header and the 44-byte-header size, and a clip whose resource
  file is a key-source file of the map must be pitch-baked.
- `validate_map_descriptors.py`: the sidecar against the descriptor and its doors sidecar (schema, exact keys, finite numbers, pitch 1,
  mixer, unique rows, every row resolves); every released map must have one.
- `make_release_zips.py`: rule `gate sounds` per map (`gate-sounds/<key>/gate-sounds.json` and `<LEAF>.wav`, an unclassified file is an
  error) and `gate-sounds.json` in `required_files`. `deploy-to-spt.ps1` copies `dist/` recursively: no change.

No Unity rebuild, no NavMesh rebake, no re-extraction of a map package, no bundle, key file, `key-sources.json`, server DLL or `base.json`
change. A deploy moves the client DLL, 4 manifests and 9 WAVs (about 9.4 MB).

## Tests

- `tools/tests/test_gate_sounds.py` (rows, matching rule, player decode, join, pitch bake, fitter, package writer, validator negatives,
  real-data classes that skip without Arena or the package: the rows regenerate byte for byte, the picks, the key-source overlap guard),
  `test_validate_distribution.py` and `test_release_zips.py` (fixtures extended).
- `ArenaMapPort.Server.Tests`: `GateSoundModelTests` (matching incl. repeated AirPit names and Equator wildcard leaves, the gain table, the
  seam pick, the cut against a numpy reference, manifest rejections, the WAV reader, the real tables) and `GateSoundWiringTests` (source
  pins: the four hook points, the guarded entry points, one `PlayAtPoint` call, developer keys, the csproj target).

## Host checks (the lead; the host is muted, so log first, then the user listens)

1. `uibridge_status`: loaded client DLL = the VM build; restart EFT (`gpu-run restart`). After the first encrypted deploy nothing of the
   bundles changes; the deploy dry run must show the client DLL, 4 `gate-sounds.json` and 9 WAVs as NEW and nothing else.
2. Per map: `Spawns/SpawnGateHold` = true and `SpawnGateHoldSeconds` = 20 (or ArenaMode), a plain raid, bots none. Expect in the log
   (`uibridge_log`, tag `[GateSounds]`): after the raid-start opening `prepared 2/2 clip(s)` style lines (clip lines with the seam for scale
   0.5), then at the release one line per row and `released N part(s): R sound row(s), R playing`, one frame later `R/R playing`.
   Expected rows: Equator 4 + 1 (5 booth doors), Bay5 5, AirPit 11 (8 passports + 3 bus doors), Bowl 4; 0 Warnings. A plain raid without the
   hold must log nothing from `[GateSounds]` except possibly the prepare lines.
3. Open the hideout once (the SpawnGates generic-class trap in AGENTS.md); check `uibridge_status` max frame time at the release (the
   first-release fallback and the clip builds are one-time costs).
4. The user listens, 3 m from a booth door, a roller, the passport row and the Bowl gates, and answers: loudness (`GateSoundVolume`; the
   Bay5 rollers/depot gates and AirPit's bus doors depend on the Environment-vs-ArenaGates group question below), reach
   (`GateSoundRolloffScale`: EFT's InteractiveObjects falloff is steeper than Arena's per-source curves), whether any seam is audible
   (`GateSoundFit` true against false; Equator's is the marginal one) and the passport level.

## Open and unknown

- **Mixer group of the AudioSource.** The AudioSources of Bay5's five gates and AirPit's three bus doors carry `OutputAudioMixerGroup`
  resources.assets 8952 = `Environment` (0 dB) while the player names ArenaGates (-15 dB); the level-39 passport twins carry ArenaGates on
  both, the level-28 leaf players none. The baked level follows the player (and, for the leaf passports, "source volume times player volume"
  gives -14.9 dB, the same as every other gate, which looks like the author compensating by hand). If Unity honours the source's group, Bay5
  and the bus doors are up to 15 dB louder than baked: `GateSoundVolume` is the lever. The row records both groups.
- **Bus doors.** The clip suggests a cycle of about 1.1 to 1.4 s (motor 0 to 0.9 s, clunk at 0.91 s, over by 1.35 s after the pitch bake
  1.13 s) while the port's borrowed motion takes 3.0 s (1.5 s at scale 0.5). Whether the bus-door pose seconds should follow the sound is
  a question for the door work, not decided here.
- Unknown and unused: the floats w9, w10, w13, the trigger and any start delay (the evidence only says the sound starts with the motion);
  the negative entity ids on Bay5 and Bowl do not appear in the gate scenes, so they cannot confirm the position join (the margins do:
  at most 1.73 m against more than 4 m).
- The Meta spatializer path of a stereo clip on a ported map is untested (EFT plays 275 stereo clips itself); if it misbehaves, a mono
  downmix fallback would sit behind the same keys.
- The InteractiveObjects pool holds 16 sources (8 precached); AirPit starts 11 at once. A refused source is a Debug line with the reason.
- Extractor dependencies (UnityPy 1.25.2, fmod_toolkit 0.1.3, numpy 2.5.0) are present on the build VM. An Arena update that renames or moves
  a clip, or changes a player's layout, fails the extraction loudly (the counts 5 / 5 / 11 / 4 rows, 9 clips and -15.0 dB are pinned);
  the clips are found by name, the path ids are hints.

## Host evidence (GPU-2, 2026-10-25 17:09 to 17:22, build cc2f024): all four maps logged, nothing listened to

Correction to the status line at the top of this file ("not yet heard or logged on a host"): the sounds were **logged on a host** in the
session below and were **not listened to** (the host is muted). Build, deploy and test values are in [door-poses.md](door-poses.md),
section "2026-10-25 - host verification on GPU-2 of the exit lamps and the moved Bay5 spawns". In short: HEAD cc2f024, client DLL
1,160,192 B built 17:03, deployed 17:05 (13 gate-sounds files new), EFT started 17:08:21 with 16/16 plugins identical; for raids 2 to 5
`Spawns/SpawnGateHold` true and `SpawnGateHoldSeconds` 20 (`persist=false`); PMC, bots None. The whole process holds 0 Warning and 0 Error
lines from `maschine-ArenaMapPort`.

Per map (all lines Debug, tag `[GateSounds]`):

| map (raid, time) | clips prepared for scale 0.50 (seam = the removed stretch) | release | rows | playing | one frame later |
|---|---|---|---|---|---|
| Equator (2, 17:12) | `arena_equator_old_slide_doors`: seam -1.303 s (end event at 1.697 s of 3.00 s), 2.27 s long; `prepared 1/1 clip(s) of arena_equator for drive scale 0.50` | 17:12:46.70 | 5 | 2 | 2/2 |
| Bay5 (3, 17:14) | `arena_bay5_big_gates`: seam -1.205 s (end event at 1.345 s of 2.55 s); `arena_bay5_roller_door_full`: seam -1.999 s (end event at 2.021 s of 4.02 s); `prepared 2/2` | 17:15:11.27 | 5 | 0 | not quoted |
| Air Pit (4, 17:16) | `arena_airpit_bus_doors`: whole, 2.12 s; `arena_airpit_metal_gates`: whole, 3.20 s (pitch-baked); `prepared 2/2` | 17:17:30.25 | 11 | 3 | 3/3 |
| Bowl (5, 17:19) | BTR: seam -2.900 s (end event 3.050 s of 5.95 s); double metal gates: -2.403 s (2.477 s of 4.88 s); emergency car: -3.001 s (3.159 s of 6.16 s); roller: -2.301 s (2.449 s of 4.75 s); `prepared 4/4` | 17:19:59.26 | 4 | 2 | 2/2 |

The live seams equal the picks of the table in "Clips" above (Equator 1.303 -> 1.697, Bay5 1.205 -> 1.345 and 1.999 -> 2.021, Bowl 2.900 ->
3.050, 2.403 -> 2.477, 3.001 -> 3.159, 2.301 -> 2.449).

The release lines:

- **Equator.** 17:12:46.70 `[GateSounds] ... Door_Sliding_Respawn / arena_equator_old_slide_doors: volume 0.178 (-15.0 dB), reaches 30 m,
  from (4.5, 1.0, 40.0), occlusion OneShotPropagation`, the same for `Door_Sliding_Respawn (1)`; the two south-booth rows and the borrowed row
  `not played (the listener is beyond 30 m ...)`; `released 10 part(s): 5 sound row(s), 2 playing, 3 out of reach or without a free source,
  0 failed (drive scale 0.50, fit on, volume 1.00)`; `one frame later: 2/2 playing`. The drive's own `released (...)` line followed at
  17:12:50.188, 3.5 s later: the sound starts in the first frame of the drive.
- **Bay5.** 17:15:11.27 `5 sound row(s), 0 playing`: every row was beyond 30 m from the north-west spawn (55.2, -3.9, 53.0); 0 failed. The
  drive's line followed at 17:15:14.76.
- **Air Pit.** The player was teleported from the south pocket (-16.9, 0, -71.7) to (-14, 0.1, -58) before the release. 17:17:30.25: the
  three bus-door rows `volume 0.110 (-19.2 dB), reaches 35 m, from (-11.0|-14.2|-18.2, 0.3, -65.0)`; the eight passport rows beyond 35 m;
  `released 19 part(s): 11 sound row(s), 3 playing, 8 out of reach ..., 0 failed`; `one frame later: 3/3 playing`. The drive's line followed at
  17:17:31.745.
- **Bowl.** The player stood at (36, 0.1, -16). 17:19:59.26: the BTR row `volume 0.178 (-15.0 dB), reaches 60 m, from (51.2, 0.0, -21.5)` and the
  respawn-gate row (reach 50 m) playing; the ambulance and the army gate beyond reach; `released 4 part(s): 4 sound row(s), 2 playing ...`;
  `one frame later: 2/2`. The drive's line followed at 17:20:06.004 (6.8 s).
- **Plain raid.** Equator raid 1 (17:09, no hold) logged the prepare lines (`clip arena_equator_old_slide_doors: seam -1.303 s ...`,
  `prepared 1/1 ...`) and played nothing, as designed. The plain raids of the other maps were not run.

Against the host checks above: the row counts (Equator 4 + 1, Bay5 5, Air Pit 11, Bowl 4), the gains (-15.0 dB on Equator and Bowl, -19.2 dB
for the Air Pit bus doors) and 0 failed on every map are as expected; the reaches seen are 30 m (Equator), 35 m (bus doors), 50 m and 60 m
(Bowl). The hideout was opened once at 17:22 (`logAbsent` error passed).

Not covered, so these stay open:

- **Listening.** Loudness (`Spawns/GateSoundVolume`, 0 to 8 since today, default 1 = Arena's -15 dB), the reach against EFT's steeper
  falloff (`GateSoundRolloffScale`), whether any seam is audible (`GateSoundFit` on against off; Equator's is the marginal one) and the
  passport level are for the user's ears (developer hold or ArenaMode).
- **Bay5 never played in this session.** From the north-west spawn every row was out of reach and no second run next to a roller was made.
  Air Pit's eight passport rows were out of reach as well (only the bus doors played), so the passport clip and its level were not exercised.
- **The source pool.** 3 of Air Pit's 11 rows played, so 11 simultaneous sources against the pool of 16 (8 precached) are still untested.
- No evidence from this session for a Hold that stops running sources, for `SpawnGateDrive` false, or for ArenaMode's own Hold and Release.
- No separate frame reading for the first release. The Equator window around it read max 33.0 ms (not attributed to the sounds).
