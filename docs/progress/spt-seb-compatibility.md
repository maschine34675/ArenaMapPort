# SPT-SEB (Sound Engine Backport) and ArenaMapPort

Offline analysis of 2026-11-08 (a forked session read SEB's repository and sources, parsed its live-mixer bundle and held it against the port's
own mixer dependencies; nothing was installed or run in game). Recorded here because the fork is archived; the user's instruction was "falls sich
die erkenntnisse aus dem SPT-SEB bericht nutzen lassen gerne mit einbeziehen". Status: **no host run, no listening test**; every statement below
is read from SEB's sources and documentation (v1.0.3) or inferred from them, and marked so.

## What SEB is

[SPT-SEB](https://github.com/RedTheFoxx/SPT-SEB) ("Sound Engine Backport", MIT, v1.0.3, first published 2026-11-06): the sound engine state of EFT
live 1.3.0.48484 for SPT 4.1.x. Per its README and documentation: one clip per shot instead of loop plus tail, 190 weapon sound players matched to
live banks (about 1.4 GB of bundles served by the SPT server), the live master mixer (95 objects, about 90 groups) with limiter and group presets,
a spatializer filter by direction, an occlusion gate (a source stays silent until its occlusion is computed), live ambience values and rain, and
for nine vanilla maps the live room-to-room routes transcribed into SPT's room and portal ids (203,022 room pairs). The documentation is thorough.

## Usable for the port

- **`.audiobakedata` tooling.** SEB's `tools/extract_live_acoustics.py` and `check_live_acoustics.py` (MIT) are a complete reader/writer and an
  independent checker of the acoustics bake format (routes against portal connections, the buffer bounds `maxRoutesCount` /
  `maxRoutePortalsCount`, lost pairs). They confirm that the format is identical between SPT 4.1.6 and live 1.3, which the port knew only from
  practice. As a second opinion they fit the port's five Arena bakes, above all Iceberg's pinned portal repair (66 to 30) and the 533 strict-check
  routes of its graph, and every future map's bake (Autoservice first). Using the checker is tool use; copying code into this repository would
  need a `THIRD-PARTY-NOTICES.md` entry (MIT).
- **Two details worth knowing** (read from SEB's sources, not measured here): (1) Unity's resampler reads a clip's first sample again at its last
  sample, so SEB schedules `SetScheduledEndTime` 2 ms before a clip's end against clicks; the port's beds are crossfaded loops and its one-shots run
  through EFT's own sources, so nothing changes today. (2) A resonant 10 Hz high-pass in the live World bus produces noise bursts on low-frequency
  room tones; SEB disables it by a bundle patch. Whether SPT 4.1.6's vanilla mixer carries the same filter is not known; the port's room tones
  and the Equator "faint steady hiss" noted in the 0.4.0/0.5.0 release notes are not attributed to it (the Equator static had its own cause, the
  LZ4 block).
- SEB's 10 s WAV recorder of the final mix is in principle the port's NoiseProbe Capture; nothing new.

Nothing of this is built into the port now.

## SEB as a companion mod on the Arena maps (offline reading, inferred where marked)

SEB patches globally (`WeaponSoundPlayer`, the mixer, the presets), so ArenaMode matches on the ported maps get the live shots without any work
in the port. Checked offline against SEB's sources and its live-mixer bundle:

- The live mixer carries every group the port addresses (`Environment` for the murmur and the roar beds, `AmbientIn`, `PrecipitationAmbientIn`,
  `AmbientInRoomTone`, `Occluded`, ...); a missing group would make SEB warn "live mixer has no group".
- SEB's route loader acts only when its folder holds a file with the name of the map's bake; it has none for the Arena maps, so the port's bakes
  stay untouched (SEB then logs "SPT 4.1.6 routes (no EFT 1.0 file for this map)") (inferred from the loader's code).
- SEB's mixer sweep runs once per scene load, not periodically: no 1 Hz hitch on the port's 55k-object scenes (inferred from the code).

Four points of friction that only a listening test can settle:

1. **Shot reverb:** the port's `RoomShotAcoustics` (one Meta XR shoebox per room) and SEB's live reverb rule run at the same time.
2. **Ambient sends:** the port's `RoomReverb` writes Arena's 26 mixer parameters per room type; SEB sets three of them to -80 dB indoors; the
   last writer wins. Whether the live mixer still exposes all 26 parameters cannot be seen offline (`TableHandler.Failed` would log it).
3. **Rain:** SEB replaces the rain bundle (shorter loops) and lowers indoor rain by 8 to 21 dB, globally, so on Equator and Bay5 too, whose
   rain the port tuned.
4. **Spectator calls and gate sounds** run through EFT's pool sources and therefore get SEB's live distance and spread curves; the reach may
   feel different from what was approved by ear.

SEB cannot be installed on GPU-2 from here (a write into `D:\SPT41` outside the deploy script). The proposed test: the user installs SEB on
his own PC, plays one round each of Equator and Bowl with ArenaMode, and the BepInEx log is read for SEB's `[acoustics]` / live-mixer lines, the
port's `[RoomReverb]` / `[SpatialAudio]` lines and any warning. If it fits, README gets one compatibility sentence; if not, the log says which of
the four points it is.

## Open

- The user's listening test with SEB installed (above), then the README sentence.
- Autoservice: run SEB's `check_live_acoustics.py` against the new map's bake as a second opinion when its spatial acoustics are exported
  (plan item in [PLAN-AUTOSERVICE.md](../PLAN-AUTOSERVICE.md)); the same check on Iceberg's repaired bake is cheap and still open.
