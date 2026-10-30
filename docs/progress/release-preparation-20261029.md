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

## The release build a4aa0af: rebuild, raid, cut and scan - 2026-10-29 (22:03-22:12)

`a4aa0af` is the commit that carries the records of the `52c8a28` host run and the release texts; its code is `52c8a28`. Both DLLs were rebuilt
from it (client SHA-256 `18c197197086e9df6fc7b6eb429a5790acdf179c2a7a4ecd56055067606715f7`, server
`f415a2e1ae2044d3d48c3e4a5694a752860c109b3bdab558fc9319436a5ab372`, ProductVersion `0.4.0+a4aa0af0e88ec01d2d392ef8252d617951ce55de`) and deployed to
the `D:\SPT41` mirror at 22:03 (255 files, the two DLLs overwritten); `gpu-run deploy-wait --server --game` found them on the host after 2 s and
restarted the server and the game (instance `6b8bf6e7`, started 22:05:18; server log 22:04:51 `Mod: ArenaMapPort Version: 0.4.0`, 22:04:55 Arena
0.6.1.0.48206 verified). `NoiseProbe` was set `Off` for the process (`persist=false`; the host .cfg keeps `Capture`), DeveloperMode stayed on in the host
.cfg as in every acceptance run (a run with it off is still unmeasured). Equator, plain PMC, bots None, 12:00, clear, 22:06: location 5.1 s, game
started 33.3 s; `[Spectator] arena_equator: ... load total=2127 ms (wait=1, hash=61, parse=1972, textures=2057, actors=55, audio=9; hash and parse
on the worker thread, crypto CNG)`; 120 fps, mean 8.3 ms, p95 8.3 ms, max 11.6 ms in the 10 s window 41 s after the start; RAM 9074 MB; no
`[NoiseProbe]` line; warnings since the raid start: Unity's four `refrigerator_drink` LOD lines only. Left through the pause menu.

Cut: `python tools/release/make_release_zips.py --out work/releases/0.4.0/github-a4aa0af --extract-check work/releases/0.4.0/github-a4aa0af-extracted`
(log `work/releases/0.4.0-make-a4aa0af.log`): five parts, `verified part 1..5 of 5`, `extract-check OK: 255 files identical to dist, 8 generated entries
ignored`, `RELEASE_ZIPS_BUILT`. Part hashes (`maschine-ArenaMapPort-0.4.0.sha256`): core `1f8c7d616ab956149e130432cc352a34f0a9936f0bb822904e314c8f44cdb2c4`
(153,917,399 B), equator `13973b86670c1c3c467aa3de6a9e47451181d6270196fd2d0124b6ae3c710017` (591,356,187 B), bay5
`898b7813c66a976d61bda5c37fae78de6364f6f48e4a2ac5d05eb44a8fb21a7e` (1,060,689,233 B), airpit
`0125f5a438c39a2b3247b80ab3c291dc0670eb2342566cbf0edd48f8eec05b93` (1,005,320,455 B), bowl
`750a0c510d6c94f6ec74e2c2398c809df1140ffe83db6c6fc7796c1830a80631` (1,023,629,157 B). VirusTotal (`scan_virustotal.py --version 0.4.0`, log
`work/releases/0.4.0-vt-a4aa0af.log`, record `work/releases/0.4.0/virustotal.json`): client 0 malicious, 0 suspicious of 75 engines (2026-10-29T21:08:56Z),
server 0/0 of 75 (21:10:38Z), `VIRUSTOTAL_SCAN_CLEAN 0.4.0`. docs/releases/v0.4.0.md carries `a4aa0af` and both VirusTotal lines; the
`github-8941caa` cut and the `8941caa` scan are superseded.

**Before any publication:** the user's reauthorization; the tag on the commit that carries this paragraph or a descendant (the package manifests pin
`a4aa0af`); a re-dating of the changelog heading and the release note if the publication happens on another day than 2026-10-29. Nothing was tagged,
uploaded or pushed. The ZIP README sentence of the packer was reworded in `a4aa0af` (open item of the previous section, done).

Test state at `a4aa0af` plus these records: xunit 2,619 tests; two of eight full runs this evening reported one failure each
(`Fehler: 1`) that the next run did not repeat, and the failing test's name was not captured (the default console logger prints the summary only;
a verbose run and three further runs passed 2,619/2,619). An intermittent test exists in the suite; identify it with `--logger "console;verbosity=normal"`
on a failing run before the next release. Python: `test_release_zips` 187 and `test_changelog` 12 OK after the texts.

## Supersession note - 2026-10-30 (the Equator static; the a4aa0af cut)

Several statements above are overtaken by the Equator static work of the night of 2026-10-29/30
([equator-noise-bundle-block.md](equator-noise-bundle-block.md)): the intermittent Equator noise no longer "remains an open finding" or
"stays an open limitation of the release". Its cause is one compressed-flag block of the Equator scenes bundle that Unity plays raw. The
repaired bundle was staged, validated and deployed, and the static reproduced on GPU-2 from the defective bundle (ON 1,581 ms) and read
0-73 ms from the repaired one (measured, nobody has listened). The texts were rewritten accordingly: the four
`[AFTER HOST RUN: ...]` placeholders and the limitation paragraphs of README.md, FORGE.md, CHANGELOG.md and docs/releases/v0.4.0.md, and the
ZIP README sentence of the packer (the to-do to reword it in "Before any publication" above was done in `a4aa0af` and is done again here). Missing
Books and CardBoard impact sounds remain open.

The build `a4aa0af` is superseded as a release build: the `github-a4aa0af` package carries the defective Equator bundle, and the `a4aa0af` DLLs
and their VirusTotal scan are replaced by a rebuild from the commit that carries the 2026-10-30 texts (the "Built from commit" and VirusTotal
lines of v0.4.0.md are placeholders again). The five-part package is cut again from that commit: the Equator part and the core part change (the
Equator scenes bundle and both DLLs), and so does the checksum file. The CHANGELOG heading and the date line of v0.4.0.md still read 2026-10-29
and are re-dated at publication. Publication stays postponed (HANDOFF.md).

## The release build 833275d: rebuild, raid, cut and scan - 2026-10-30 (01:10-01:16)

`833275d` carries the repaired Equator scenes bundle's records and the release texts (the repair itself: `f32eba5`, the NoiseProbe fixes `52c8a28` and
`36a6d12`). Both DLLs were rebuilt from it (client SHA-256 `09b9393e08854197f4d33c39f3c2788f79c5018932649b94acc3e24c2585ab03`, server
`90e47da0b32b4c202a453397d18680dd21f81be7bb57355bd0c96ab554bcfc59`, ProductVersion `0.4.0+833275d91f197d71135b9cf030bfdb0ce87193b7`) and deployed to the mirror at 01:10 (255
files, the two DLLs overwritten; the repaired Equator scenes bundle had been deployed at 00:22). `gpu-run deploy-wait --server --game` found both on the
host after 12 s and restarted the server and the game (instance `7de2fcbf`, 01:13:27, UiBridge 0.9.7 since a restart at 00:49 that was not ours). `NoiseProbe`
set `Off` for the process (`persist=false`). Equator, plain PMC, bots None, 12:00, clear, 01:14: `[BundleKeys] key for 'arena_equator' derived in 3 ms`,
`[Bundles] loaded 'arena_equator/arena_equator_scenes.bundle' ... (577488350 bytes plaintext, encrypted AMPB v1, 551 chunks)`, `[Spectator] arena_equator:
... load total=2161 ms (wait=1, hash=59, parse=1931, textures=2091, actors=57, audio=8; hash and parse on the worker thread, crypto CNG)`; 120 fps, mean 8.3 ms,
p95 8.3 ms, max 10.8 ms in the 10 s window 41 s after the start; RAM 9105 MB; no `[NoiseProbe]` line; warnings since the raid start: Unity's four
`refrigerator_drink` LOD lines only. Left through the pause menu at 01:16.

Cut (started 01:10, parallel to the deploy): `python tools/release/make_release_zips.py --out work/releases/0.4.0/github-833275d --extract-check
work/releases/0.4.0/github-833275d-extracted` (log `work/releases/0.4.0-make-833275d.log`): `verified part 1..5 of 5`, `extract-check OK: 255 files identical to
dist, 8 generated entries ignored`, `RELEASE_ZIPS_BUILT`. Part hashes (`maschine-ArenaMapPort-0.4.0.sha256`): core
`439d55e0d04933abd29aff505b994e59f08d42e32430db8094fac8f78f7342ca`, equator `88070915ec4fb2c011e5782b7dfb186583be50566af83522bf31a278268a53c6`, bay5
`f44eaad8206bd4bd68a892aec89ab9c3372c0f9d29976f94a429840c5629c46e`, airpit `ff48188cdccc7f94c82366b99e14a21efa858b4f9f49e02e11a4093a0c7dc706`, bowl
`32456558fef24fa915565917eb8213babb8ce9a5e5f35333a94ee99c9ab4ff33`. VirusTotal (`scan_virustotal.py --version 0.4.0`, log `work/releases/0.4.0-vt-833275d.log`,
record `work/releases/0.4.0/virustotal.json`): client 0 malicious, 0 suspicious of 75 engines (2026-10-30T00:12:58Z), server 0/0 of 75 (00:14:20Z),
`VIRUSTOTAL_SCAN_CLEAN 0.4.0`. docs/releases/v0.4.0.md names `833275d` and both scans; the `github-a4aa0af` cut and its scan are superseded.

**Before any publication:** the user's reauthorization and his three decisions (the "Rauschen" identity, a 0.3.1 with only the Equator bundle, the repair
instead of a Unity rebuild); the tag on the commit that carries this paragraph or a descendant (the manifests pin `833275d`); a re-dating of the changelog
heading and the release note if the publication happens on another day than the one they name. Nothing was tagged, uploaded or pushed.

## Release form changed to one whole ZIP - 2026-10-30 (user decision)

The user decided (German): "das zip archiv wird wieder als ganzes auf pixeldrain hochgeladen, wie bei den letzten veröffentlichungen, kein fünfer-split". The sections above prepare the five-part GitHub form of
2026-10-29 and its re-cuts (`github-8941caa`, `github-a4aa0af`, `github-833275d`, `github-8a28c1d`); that plan is superseded. 0.4.0 ships as in 0.3.0: one ZIP,
`maschine-ArenaMapPort-0.4.0.zip`, plus its `.sha256`, on Pixeldrain, and a GitHub release whose only asset is the checksum file and whose notes carry the Pixeldrain
links. `work/releases/0.4.0/PREPARATION.md` and `Verify-Preparation.ps1` were written for the multipart set (they say "do not use `--single`") and are not a recipe for
this cut.

Changed with this decision: `docs/releases/v0.4.0.md` (Download and Installation in the 0.3.0 form, placeholders for the Pixeldrain links, the ZIP size and the ZIP
checksum beside the build commit and the VirusTotal lines; the upgrade notes now say that the ZIP overwrites 0.3.0's `parts/full.json` instead of asking the player to
delete it; the way to restore a skipped crowd is to extract the ZIP again), README, FORGE, CHANGELOG, the five reinstall texts of the in-game notices (client DLL
changes: "release ZIP" / "Release-ZIP"), and the single-layout `README.txt` of `make_release_zips.py` ("Updating: extract the complete ZIP ..."; the intro lists the
shared files, which a multi-part set keeps in its core part). The multi-part README.txt, the partition and their pins are unchanged.

Still true from the sections above and not about parts: the hedged claims (gate sounds, puddles, lamps, the crowd murmur and the pauses are not listened to), the Books and
CardBoard limitation, the Equator static fix and its measured-not-listened wording, the crowd cost paragraph, the upgrade of a development build. Cut and publication:
`python tools/release/make_release_zips.py --single --out work/releases/0.4.0/pixeldrain-<commit> --extract-check <empty scratch dir>`, then `--verify-only`, then
`upload_pixeldrain.py`; the DLLs are rebuilt first because the notice texts are in the client DLL (HANDOFF.md, top block). No release build, cut, tag, upload or push was done with this decision.
