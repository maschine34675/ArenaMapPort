# GitHub release preparation — 29 October 2026

The user authorized publication of ArenaMapPort 0.4.0 alongside ArenaMode 0.4.0 and Redeploy 1.1.0.
This preparation changes release documentation and generated installation wording only; the client/server
source identities already agree at 0.4.0. The independent root owns the final committed build and publication.

The [release notes](../releases/v0.4.0.md), changelog and README now describe the two-voice spectator
schedule, shared textures, gate sounds/score screens, developer-only output readback and the owned-scene
late-collision sound guard. [The native sweep](release-sweep-20261029.md) supersedes the old startup block
with bounded four-map/cleanup evidence. Intermittent Equator noise and missing Books impact sounds remain
open findings; neither clean source checks nor diagnostic peaks establish subjective hearing acceptance.

The October 28 single ZIP is obsolete and remains local historical evidence. GitHub's current per-file
limit is under 2 GiB, so this release uses the packer's existing five-part format: core, Equator, Bay5,
Air Pit and Bowl, plus one SHA-256 file. Every part is a normal ZIP extracted into the same SPT root;
install all five. Shared textures/calls travel in core, and each map's encrypted bundles, wrapped-key JSON
and database travel together. All current raw parts are below 1.1 GB; finished ZIP sizes are checked by
the packer. The exact distribution was 255 files before the final rebuild.

The source/input oracle and detailed root handoff live under ignored `work/releases/0.4.0/`.
Full distribution authentication, current Arena key derivation, committed DLL provenance and complete ZIP
extraction must pass again on the final build. No content keys, raw inputs or plaintext bundles enter the release.
This note records preparation, not final upload, runtime-loaded binary identity or publication acceptance.

## Preparation checks completed — 2026-10-29

The read-only source/input oracle passed parsed client/server identities and source versions at 0.4.0,
the five-part 255-file partition, twelve AMPB bundle headers, strict schema-2 wrapped-key metadata,
shared texture integrity (123 images), actor counts 99/88/99/99 and all 13 original call clips. Forbidden
content-key, raw-resource, PDB and Thumbs.db filenames were rejected by explicit negative controls.
The existing changelog, release-packaging and DLL-version fixtures passed: 196 tests, zero failures.
The first fixture run caught two outdated README wording expectations (three assertion failures including
the single/multipart cases); the art labels were retained and the multipart update wording corrected.
`git diff --check` passed. The source/input gate is recorded; final package authentication and publication
remain with the independent root after commit and fresh build.

## Release audit, fixes and the corrected candidate - 2026-10-29 (evening)

Publication is still postponed by the user (HANDOFF.md, top). This section supersedes in part the paragraphs above, which describe the first cut
`fe45b84`.

**Audit.** At the user's request ("schau mal über den aktuellen stand ob der so als 0.4.0 veröffentlicht werden kann") a read-only audit with 63
agents went over the local candidate `fe45b84`. Verdict: no blocker, only should-fix items. The crowd build blocked the main thread about 4.7 s per
raid on every map; the 123 shared PNGs decode to about 0.5 GB of RGBA32 video memory per raid; 31 of 44 `DistributionSidecarTests` errored on HEAD
(stale fixtures); VirusTotal had not been run for the `fe45b84` DLLs; FORGE.md said "extract the ZIP" and the in-game notices said "the same ZIP";
several claims exceeded the records (gate sounds never listened to, Equator's puddles not checked in game, lamps by state reads only); the Books AND
CardBoard impact banks are both null (39 Equator props); a 0.3.0 player keeps a stale `parts/full.json`; the public notes mentioned the internal
October 28 candidate.

**Fixes.** `694143d`: the crowd package is hashed through the CNG provider and prepared on a worker thread ([spectators-allmaps.md](spectators-allmaps.md)
"Load cost" and its 2026-10-29 corrections), the NoiseProbe diagnostic ([spectator-audio.md](spectator-audio.md)), the five reinstall notice texts
([map-notices.md](map-notices.md)) and the repaired distribution fixtures (31 errors gone, 5 new negative tests). `8941caa`: the release texts after
the audit: five parts everywhere, hedged claims, the crowd cost (video memory, load time, how to skip one map's crowd), the upgrade note (delete
`parts/full.json`), Books and CardBoard, and the placeholders listed below. The texts are [v0.4.0.md](../releases/v0.4.0.md), CHANGELOG.md, README.md,
FORGE.md and the correction in [release-sweep-20261029.md](release-sweep-20261029.md).

**The new cut.** The dist DLLs were rebuilt from `8941caa` (client SHA-256 `b624acc2a497258bece9bed48700160a14d40506868f7c2561658d1bc56cf36a`, server
`d3b7a274038b1b2b82cb9cfe2ceea5e92ad7f1d37d0eae0403d6fbeb6784ea16`, ProductVersion `0.4.0+8941caa...`) and deployed to the `D:\SPT41` mirror at 16:45. A
new five-part cut from `8941caa` is being built under `work/releases/0.4.0/github-8941caa`, and a VirusTotal scan of the two DLLs is running. The
packages of `fe45b84` at `D:\AMP040-29\packages` are unchanged; they are the **host-verified fallback** (lead's GPU-2 run 14:34-14:47: four maps with
Medium bots, crowds 99 / 88 / 99 / 99, two voices, 120 fps steady, 0 ArenaMapPort warnings or errors, crowd build 4.68-4.70 s per raid; details in
spectators-allmaps.md).

**The host run of `8941caa` is blocked.** Since 16:45 the GPU-2 SPT server refuses to start: ArenaMode's server mod stops on "A paid Tarkova Cola reward
needs companion-file recovery" (`RewardColaRecoveryException`, expected-object), a profile written by a 15:02-15:07 session of another agent. That is
not an ArenaMapPort matter, and ArenaMode and the host profiles are not touched from here. The worker, the CNG hashing, the NoiseProbe self-test and the
changed notice texts are therefore covered by unit tests and builds only; the checklist is in HANDOFF.md.

**Placeholders still open** (filled after the host run and the scan): `<CROWD_LOAD_SECONDS>` (CHANGELOG.md, README.md, FORGE.md twice, v0.4.0.md; the
number comes from the `load total=` line of the host run, not from the 4.68-4.70 s of `fe45b84`), and in v0.4.0.md `<BUILD_COMMIT>`, `<scan date>` and
the VirusTotal URLs and counts of the two DLLs.

**Before any publication:** the user's reauthorization; the host run of `8941caa`; the tag on `8941caa` or a descendant, so that it contains the
correction in release-sweep-20261029.md (the release note links that file at the tag); a re-dating of the changelog heading and the release note if the
publication happens on another day than 2026-10-29; a package whose part manifests pin the commit it was built from. Nothing was tagged, uploaded or
pushed.

## The 52c8a28 state: host run and the candidate - 2026-10-29 (late evening)

Publication is still postponed by the user (HANDOFF.md, top). This section supersedes the corrected-candidate and "host run blocked" paragraphs above.

**Build and host run.** GPU-2 is runnable again. `52c8a28` (`8941caa` plus the NoiseProbe fix for a stereo driver under a 5.1 speaker mode, see
[spectator-audio.md](spectator-audio.md); xunit: 2,619 tests pass) was deployed at 20:55 (255 files, 2 overwritten: client SHA-256
`c052926a3e7373cb40d47bc7344fc753dd820e95d93ba60be7ebcac205574327`, server `aaeae0c77e52aba5b3174fdf041e43aba64f24a881b69d14895c1020aa5b0a00`, ProductVersion
`0.4.0+52c8a281cf765bcb13473104366f5a1cd06348f8`; the deploy-wait took 116 s). The server log shows ArenaMapPort 0.4.0 loaded, Arena 0.6.1.0.48206 verified and
four maps available. EFT instance `2896f154` started at 20:59:11 (`LogOutput-SPT-GPU-2-2026-10-29_20-59-01.log`; DeveloperMode was on for the whole process, so every figure below comes from a developer-mode process).
Four plain PMC raids without bots (bots None, 12:00, clear) on Equator (21:00), Bay5 (21:02), Air Pit (21:04) and Bowl (21:05), then the hideout once (21:06, opened and back to the menu; EFT's
first-time "WELCOME TO THE HIDEOUT" window had to be closed by hand):

| Raid | Load location / game (diff) | Crowd build on the main thread | Steady perf and first-seconds maxima | RAM |
| --- | --- | --- | --- | --- |
| Equator (first raid of the process) | 4.7 s / 31.5 s (1.4 / 14.2) | 2110 ms | 120 fps, mean 8.3 ms, max 10.4 ms (10 s window 50 s in, most likely inside the probe's armed span 21:00:51-21:02:07); raid-start max 955 ms | 9041-9048 MB |
| Bay5 | 5.5 s / 26.0 s (1.4 / 8.2) | 2076 ms | 99 fps mean 10.1 ms over the first 10 s, max 484.5 ms (RainCover bootstrap) | 11862 MB |
| Air Pit | 5.2 s / 25.9 s (1.5 / 8.4) | 2100 ms | 76.8 fps mean 13.0 ms over the first 10 s, max 1915.5 ms (`[IndoorSurface]` 1815 ms) | 12069 MB |
| Bowl | 5.3 s / 26.7 s (1.5 / 9.1) | 2158 ms | 72.7 fps mean 13.8 ms over the first 10 s, max 3067.1 ms (`[IndoorSurface]` 2936 ms) | 12574 MB |

Crowd lines: worker, CNG, `wait=0` on every map; `fe45b84` blocked the main thread 4.68-4.70 s ([spectators-allmaps.md](spectators-allmaps.md) "Host run of
the worker build"). No `could not be prepared` or `could not be restored` line. 0 ArenaMapPort warnings or errors in the whole process. The log also holds third-party startup
warnings (three FixPluginTypesSerialization and four HarmonyX notices, 20:59:01-20:59:13), Unity's `refrigerator_drink` and `light_CellPlafonds` LOD lines, one
Unity `Can not play a disabled audio source` in the hideout and the known `MenuScreen` coroutine error on leaving Equator; none is ours. The user's MASCHINE11
log (the 16:41 build, Equator, DeveloperMode on) holds two more crowd lines of the same form, 20:25:19 `load total=2204 ms (wait=1, hash=56, parse=1920,
textures=2093, actors=51, audio=54)` and 20:30:01 `2124 ms (wait=0, hash=60, parse=1885, textures=2081, actors=38, audio=4)`, both on the worker with CNG, so "about
two seconds" stands on a second machine. The Equator load row is the first raid of the process; the other three maps ran after it, and no cause is claimed for
its longer game load (14.2 s against 8.2-9.1 s of diff). NoiseProbe on Equator: armed on the stereo driver (`ch=2/6`), the self-test found 1 of 4 with a background
peak of about -37 dBFS (the detector is relative; [spectator-audio.md](spectator-audio.md) "Host run of 52c8a28"), `Off` logged nothing on the other three maps.

**What this run does not cover:** bots, and listening to anything (gate and door sounds, puddle splash, the spectator voices, the Equator noise). It is a
functional run of the worker build and of the probe; the claims in the release texts that are hedged on "not listened to" stay hedged.

**Texts.** `<CROWD_LOAD_SECONDS>` is filled: "about two seconds to raid loading" in CHANGELOG.md, README.md, FORGE.md (twice) and v0.4.0.md (measured 2.08-2.16
s). "Built from commit" and the VirusTotal lines of v0.4.0.md are placeholders again (`<BUILD_COMMIT>`, `<scan date>`, `<VirusTotal URL>`, `<n>`), because the
cut and the scan of `8941caa` no longer describe the build that is shipped: the package `work/releases/0.4.0/github-8941caa` and its VirusTotal lines are
superseded. The CHANGELOG bullet about the NoiseProbe diagnostic now says that the probe reads the output channels the driver delivers and no longer says that it was never run in
a game. The Known-limitations sentence "has not yet been re-tested with the shipped two-voice schedule" (README.md, FORGE.md, CHANGELOG.md, v0.4.0.md) is replaced: the
user heard the noise again on 2026-10-29 (below) and his log shows the shipped two-voice schedule.
Python fixtures `test_release_zips` and `test_changelog`: 199 tests OK with the texts of this commit.

**The user's Equator noise.** The user heard the noise again in a plain PMC raid on Equator while standing still and ran `NoiseProbe=Capture` twice
(`D:\SPT41\BepInEx\LogOutput-MASCHINE11-2026-10-29_20-21-38.log`, four captures); an analysis of those captures is in progress, its result is to be appended
to [spectator-audio.md](spectator-audio.md). His build was `maschine-ArenaMapPort.Client.dll` 0.4.0 of 2026-10-29 16:41 (the `694143d`/`8941caa` code, before the
stereo fix) with Arena 0.6.0.0.47752, SurroundAudio 1.0.0 and DeveloperMode on; the crowd line reads `up to 2 simultaneous voices` and both armed lines `calls=on`,
`ch=6`. The noise stays an open limitation of the release.

**Before any publication** (in this order): the user's reauthorization; the commit that carries these texts; the release DLLs rebuilt from that commit
(ProductVersion embeds the commit) and deployed to the `D:\SPT41` mirror; one more Equator raid on GPU-2 with those DLLs, with DeveloperMode and `NoiseProbe` off as
players run it (this run had DeveloperMode on); the ZIP README sentence in `tools/release/make_release_zips.py` ("Known audio limits", "on development builds with a
denser crowd-call schedule than this release uses") and its pin in `tools/tests/test_release_zips.py` reworded, because the noise was heard again with the shipped
schedule; the five-part cut from that commit
(extract-check against dist) and a VirusTotal scan of its two DLLs; `<BUILD_COMMIT>`, `<scan date>` and the VirusTotal URLs and counts filled; the tag on
that commit; a re-dating of the changelog heading and the release note if the publication happens on another day than 2026-10-29. Nothing was tagged,
uploaded or pushed.
