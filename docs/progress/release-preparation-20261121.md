# Release 0.7.0 preparation - 2026-11-21

The first release with Block (Arena's `Arena_Yard`, the seventh map), the native grass of Bowl, Iceberg and Block and the rain drops on the screen. The user's words of 2026-11-21:
"version erhöhen und release-texte aktualisieren". This record holds what the preparation step wrote and the readiness state. **Nothing is published:** no cut, no VirusTotal
scan, no upload, no tag, no merge, no push, no GitHub call, no deploy, no host run, no commit; the release date is unknown and publication waits for the user's explicit go. The number 0.7.0
is the lead's choice by the project's pattern (a new map is a minor version, as 0.3.0 Bowl, 0.5.0 Iceberg and 0.6.0 Chop Shop; PLAN-YARD decision 8 recommended it); the user named no number and can still
change it, in which case the version strings, the file names `docs/releases/v0.7.0.md` and `work/releases/0.7.0/...` and the README links move together. The release form stays one whole ZIP on
Pixeldrain plus a GitHub release with the notes and the checksum file (as 0.3.0 to 0.6.0) unless the user says otherwise.

## Baseline

HEAD `1ad18c3` on `feature/yard` (tree clean at the start), 22 commits since the tag `v0.6.0`: the publication record, the two Chop Shop DynamicMaps commits (`322e3a2`, `6a5c8c4`), 17 commits of Block, the native grass and the
rain drops, and records. Staged dist: 422 files (7 maps x 3 bundles = 21 bundles, 3 grass bundles, 7 key files, 7 shader sidecars). `make_release_zips.py --plan` on 2026-11-21 (after the DLL rebuild at 0.7.0): 422 files
(+4 generated), raw 7,867,157,355 B (7,502.7 MiB), estimated single ZIP 7,833,724,745 B (7.30 GiB); parts core 242, equator 24, bay5 30, airpit 22, bowl 27, iceberg 28, autoservice 27, yard 22. Against 0.6.0 (396 files, estimate
6,348,651,635 B, real ZIP 6,348,672,857 B): +26 files (bowl +1 and iceberg +1 grass bundles, autoservice +2 overview, yard +22). The estimate undershot 0.6.0's real ZIP by 21,222 B, so the texts say "about 7.3 GiB" only.
Recorded gates: .NET 3,808 tests (grass stage 2), `DISTRIBUTION_OK 7 maps 21 bundles 3 grass bundles 7 sidecars encrypted`, `validate_bundle_names` OK, `validate_port.py --map yard --distribution` PORT_VALIDATED.

## What was bumped and written in this step (no commit)

- **Version 0.7.0:** `ArenaMapPort.Client.csproj`, `ArenaMapPort.Server.csproj`, `Plugin.PluginVersion`, `ModMetadata`; the stale-control literal of `tools/tests/test_dll_versions.py` (`'0.6.0'` to `'0.7.0'`).
  Client (Release, DllOnly) and server (Release) were built under the lock and the staged DLLs are 0.7.0 (`DLL_VERSIONS_OK 0.7.0`); the publication build is made again without DllOnly.
- **Texts:** `CHANGELOG.md` ("0.7.0 - unreleased"), `docs/releases/v0.7.0.md` (new, with placeholders), `README.md`, `FORGE.md`, `docs/dist-manifest.md` ("Release 0.7.0 contract"; the 2026-11-21 "pending" statements marked as superseded),
  the packer prose (`tools/release/make_release_zips.py`: `ENCRYPTION_TEXT` says seven maps and names Block's checks, the crowd sentence, the audio-limits paragraph, the gate sentence, the grass and rain paragraphs, the DynamicMaps sentence)
  and its pins (`tools/tests/test_release_zips.py`).
- **Per-map feature matrix used in every text:**

| Map | Crowd | Per-light flares | DynamicMaps overview | Native water | Native grass | Bot / Player rows |
|---|---|---|---|---|---|---|
| Equator | yes | yes | yes | no | no | 16 / 4 |
| Bay5 | yes | yes | yes | yes | no | 16 / 4 |
| Air Pit | yes | no | yes | no | no | 20 / 4 |
| Bowl | yes | yes | yes | no | yes (113,643 clumps) | 21 / 4 |
| Iceberg | no | no | yes | yes | yes (15,410) | 17 / 4 |
| Chop Shop | yes (30 fans) | yes (8 gas lamps) | yes (since `322e3a2`) | no | no | 16 / 4 |
| Block | yes (27 fans) | no | no | no | yes (10,132) | 18 / 4 (original, not appended) |

  Rain drops on the screen: every map in rain in the open. Winter or snow look: Chop Shop and Block have none (summer set only); nothing is stated for the other maps. Key files: the six older maps 2 wraps, Block 1 wrap
  (0.6.2.0.48473).

## Readiness table

| | Item |
|---|---|
| DONE | Staged dist complete (422 files); Block built, keyed (off-VM backup confirmed by the user), deployed and host-run on GPU-2; grass and rain drops host-run (records `native-grass.md`, `two-maps-item-6-raincover.md`); the six older key files are byte-identical to those inside the published 0.6.0 ZIP (compared 2026-11-21 by the lead's reviewer and again in this step); version 0.7.0 and every text written; staged DLLs 0.7.0. |
| TO DO AT PUBLICATION (lead, on the user's go) | Client Release build WITHOUT DllOnly and server build at 0.7.0; .NET tests and the full Python suite on the final tree; `validate_dll_versions.ps1`; `validate_distribution.py`, `validate_bundle_names.py`, `validate_port.py --map yard --distribution --package work/extract/yard-native-v2`; `verify-keys` on every Arena build at hand; deploy and a GPU-2 run of all seven maps and the hideout (0 warnings, the Iceberg `[EmissionCap] ... cap=4.60 floor=1.80` line); `make_release_zips.py --single --extract-check`, `--verify-only`; VirusTotal for both DLLs; `upload_pixeldrain.py` (dry run first); fill the placeholders; publication commit; merge (see "Branch" below); tag `v0.7.0`; push; GitHub release. |
| USER | Confirm 0.7.0; look at the grass and the rain drops (and Block's marker at 691/857); the DynamicMaps 1.3 branch; ArenaMode's Block entry and its pin (item 28); the Forge entry; the stale v0.5.0 release body; whether to delete older releases. |
| RISK | (1) No complete Python run exists on the exact tree: the integrator's discover was still running when this step ended (see "Gates"); the note says so. (2) Block's key file has ONE wrap and was verified on 0.6.2.0.48473 and 0.6.1.0.48206 only. (3) The test host runs DynamicMaps 1.3, so Chop Shop's overview is skipped there. (4) Rain drops change six older maps in rain and were host-run on Equator and Block only. (5) The grass and the rain were never seen by the author. |

## Open verification items (stated as such in the release note)

- Block: nothing is recorded as heard or looked at except the author's whole-map sentence; gate sounds were logged only (not listened to); crowd calls, murmur, reactions and shot acoustics have no single-item record; the marker at 691/857 was read back
  on the host but the user has not looked at it. Not checked: the north gate leaves from the north pocket, a long raid with every wave, a player touching a border wall and bullets against layer 29, the band south of the play area, the 15
  distant sun-shadow rows at Low and at hours other than 08:00, the banner, description and loading images on the host, the look of the two tree stand-ins, a respawn onto an in-box marker, any ArenaMode match, Team Deathmatch, Domination, Deathmatch,
  Tactical or Zombies with its rows. The one-time move of a saved Block ambient mix (revision 4) is unit-tested only (the user waived the in-game check).
- Native grass (Bowl, Iceberg, Block): host-run by the lead on 2026-11-21 (pictures by day, about 0.13 to 0.27 ms per frame at 1080p with the limiter lifted, one sample per view); the author has not seen it. Not checked: a really missing or damaged grass bundle
  (unit-tested), a scope view, grass shadows off, texture quality Low, TAA off / DLSS / FSR, a respawn in ArenaMode, Bowl and Iceberg at night and in rain, GPU memory, wind motion (none implemented).
- Rain drops: host-run on Block and Equator only; the author has not seen it; glasses, ripples and wet props are driven by the same flag and nobody looked at them. The record `two-maps-item-6-raincover.md` says five released maps are affected; it is six (Iceberg shipped in 0.5.0): the texts print no count.
- Chop Shop's overview: accepted natively with DynamicMaps 1.2.1 on 2026-11-14; the test host now runs 1.3 and logs `DynamicMaps API differs from verified 1.2.1; optional maps integration skipped`; the fix is on the unmerged branch `fix/dynamicmaps-130`.
- Key checks: Block not on 0.6.2.0.48598, 0.6.2.0.48767 (MASCHINE11 unreachable) or 0.6.0.0.47752; the 0.6.0.0.47752 check exists for the four maps of 0.4.0 only.
- Carried over from 0.6.0, unchanged: the roar beds at -26 dBFS, Iceberg's ambience, lamp loops and gate sounds not listened to; Chop Shop's tribune decks, rain-depth readback and equator-shutter collider; Iceberg sun shadows at Medium/High and in the evening; the unverified spawn-row and exit items.

## Decisions taken in this step for the user (the lead surfaces them)

- **0.7.0** (see above); CHANGELOG headed "0.7.0 - unreleased"; stated as the lead's pick only in the note's checklist comment, never in player text.
- **Wording of the limits.** "Approved by the author" only where a quoted sentence covers it: the sentence of 2026-11-21 ("sieht gut aus, auch nachts, ... (gain auf 5, range auf 3)") covers Block's look, the night included, and its sound as a whole; it names no single item,
  so no single sound item is recorded as heard. The ambient mix 5 dB / 0.1 / 3 is "the author's by ear". "Checked on the test host" for the lead's runs; "not listened to", "not looked at", "not checked" for the rest. The author is written about without a gendered pronoun in the new text.
- **ArenaMode.** The texts say only that Block is registered as a location in SPT, that whether and when ArenaMode offers it is ArenaMode's decision, and that this project ran no ArenaMode match on it. ArenaMode's source (read-only; dev HEAD `6b3ba6a4`, tag v0.6.0) has no Block entry,
  but a player's installed build was not inspected, so no sentence says "ArenaMode has no entry" (a reviewer's point: that would go stale and exposes a working method). ArenaMode 0.6.0 does offer Chop Shop (its CHANGELOG); the texts stay neutral about that too. The contracts are unchanged
  and "ArenaMode 0.4.0 and later has adopted" them stays. ArenaMode's whitelist pin (0.6.0, advisory) differs from 0.7.0 until the user moves it; Block's donor-row proposals (item 28: `arena_yard-bot-fill-04` or `arena_yard-bot-core-04`) are not adopted.
- **Grass and rain, given that the author has not seen them.** Both are described as on by default with no public setting and no key named (a source test pins that the README names no grass key); the grass as Arena's own tufts, no collision, no footstep or bullet effect, no trampling, no wind, with the measured cost; the rain
  as drops in rain in the open and none under a roof. Both say "the author has not seen it" in the bullets and again under Verification, and name what was checked (host, which maps) and what was not. No "turn it off" advice is given because there is no player setting. The grass code reads
  no Arena file but its bundle opens with the map's key, so the installed Arena is still required (a reviewer's point, applied).
- **DynamicMaps.** "Tested with 1.2.1"; 1.3 is not promised; the note states the observed 1.3 behaviour. The sentence changes if `fix/dynamicmaps-130` ships before the cut.
- **Upgrade from 0.6.0.** Extract the whole ZIP, keep `BepInEx/config`, replace both DLLs, nothing to delete. Basis: the plan counts rise by exactly 26 and the six older key files are byte-identical to 0.6.0's; the reviewer also compared the 0.6.0 ZIP's file list with dist (only the three generated entries differ).
- Developer-only items sit in CHANGELOG "Changed (internal)" and not in player text (checklist test, normal-texture preflight, border extractor and model, grass pack tools, the `outBack` ease, `silent_clips`, validators, `build_remaining.ps1` refusing yard, `.gitattributes`, the removed `AdditionalMapBuilds.props`). The never-released stage-1 grass probe is not mentioned.
- **Reviewer fixes applied in this step:** Block's grass "on the lawns of the courtyard" (its file spans x -67..9, z 17..118; "in front of the south pocket" was wrong); the grass sentence about Arena files; the key sentences say "for 0.6.0" and "byte-identical to 0.6.0's"; "pictures by day and at night"; the gate clip "started within its 30 m reach, not listened to";
  the tree stand-ins in the not-checked lists; the ArenaMode clause; the stale plan figure in the checklist; the CHANGELOG quote in full and its `<file-count>`; the six-entry text-pack clause. Not applied: the hanging sheet at x -53.5 (a body passes it; the record's own result is "no change").

## Placeholders left in `docs/releases/v0.7.0.md` (all set at publication)

`<release-date>`, `<pixeldrain-list-link>`, `<zip-bytes>`, `<zip-gib>`, `<pixeldrain-zip-page-link>`, `<pixeldrain-zip-direct-link>`, `<zip-sha256>`, `<pixeldrain-checksum-link>`, `<build-commit>`, `<file-count>` (the plan reads 422), `<scan-date>`, `<virustotal-client>`,
`<virustotal-client-result>`, `<virustotal-server>`, `<virustotal-server-result>`, `<final-gates>`, `<final-raids>`, and one `TBD-AT-PUBLICATION` paragraph note in "Verification and known limitations" (the development-state checks are replaced by those of the exact build).
The CHANGELOG has none left except the date in its heading. The checklist comment at the top of the note lists them; grep for `<[a-z-]+>` and `TBD-AT-PUBLICATION` and delete the comment before publishing. Also re-check at the cut: "about 7.3 GiB", the key sentences (README, FORGE, note, `ENCRYPTION_TEXT` and its pins move together), the DynamicMaps sentence.

## Gates of this step (2026-11-21)

Client (Release, DllOnly) and server (Release) builds under the lock; `validate_dll_versions.ps1` `DLL_VERSIONS_OK 0.7.0`; .NET 3,808 tests (one run: 3,806 passed, `CryptoThroughputTests` and `CrowdReactionModelTests.ReactAndDrainAllocateNothing` failed while the full Python discover was running in
parallel, and both pass alone: the known load-dependent pair); Python `test_dll_versions`, `test_changelog`, `test_release_zips`, `test_crowdless_maps`, `test_upload_pixeldrain`: 360 tests OK; `make_release_zips.py --single --plan` 422 files, 7,833,724,745 B; `validate_map_descriptors.py` OK;
`git diff --check` clean apart from LF/CRLF notices. The full Python discover started by the integrator is reported in `work/tmp/release-070/handoff/final.md` if it finished.

## Not done in this step

The Release build without DllOnly (it stages and encrypts with the key store), the release cut, the extract-check, the VirusTotal scan, the upload, the GitHub release, the tag, the merge, any push, any deploy, any host run, any key check, any commit. `work/release-keys` was not read.

## Branch

The work is on `feature/yard`; local `main` is an ancestor of HEAD (19 commits ahead, none behind), so `main` fast-forwards. Whether `feature/yard` is fast-forwarded into `main` before or at the publication commit is the lead's and the user's call; the 0.6.0 pattern merged, deleted the branch and pushed on the user's go.
