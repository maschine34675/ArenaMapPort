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
