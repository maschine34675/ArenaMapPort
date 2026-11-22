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

## The lead's checks after the texts, the commit, and a deploy that met another session's host run (2026-11-21, 23:00 to 23:55)

- **Read by the lead:** the CHANGELOG entry in full, the placeholder set of the release note (16 tokens and two `TBD-AT-PUBLICATION` notes), the wording around the Arena check and the encryption in every changed text (only the established "not copy protection" form), the lead's own paragraphs (AGENTS.md "Release configuration" and status notes, HANDOFF item 21). One correction of the lead's own earlier texts: the rain repair changes SIX released maps, not five (dated note in two-maps-item-6-raincover.md, HANDOFF item 18).
- **Gates on the final tree:** `dotnet test ... -c Release`: `Bestanden! Fehler: 0, erfolgreich: 3808` (alone, on a quiet machine); the Release client build WITHOUT DllOnly (stages and encrypts; exit 0) and the server build; `DLL_VERSIONS_OK 0.7.0 client/server source, project and staged binaries`; the seven `keys/*.json` byte-identical to the staging of the same evening; `DISTRIBUTION_OK 7 maps 21 bundles 3 grass bundles 7 sidecars encrypted`; `validate_bundle_names.py` OK (21 bundles); `validate_port.py --map yard --distribution --package work/extract/yard-native-v2`: `PORT_VALIDATED`; `make_release_zips.py --single --plan`: `total: 422 dist files, raw 7,867,157,355 B`, `estimated zip 7,833,724,745 B`, `estimated single ZIP: 7.30 GiB`; `validate_map_descriptors.py` OK. The whole Python suite on the committed tree: `Ran 3668 tests in 2116.075s`, `OK (skipped=70)`.
- **Commit:** `ca26292` ("0.7.0 prepared: version and release texts (not published)") on `feature/yard`; nothing pushed.
- **A deploy that should not have happened as it did.** The lead deployed the two 0.7.0 DLLs with `-DllOnly` at 23:20 without reading `uibridge_status` first. The test host was in use by ANOTHER session (`codex-h2`, last request five minutes earlier): its EFT instance of 23:11 runs that session's client build `0.6.0+8a1ec4e` (branch `feature/block-dynamicmaps`). The deploy replaced that session's client DLL on the mirror and on the host's disk with `0.7.0+1ad18c3`; the running game keeps the other build loaded. The lead did not restart EFT, started no raid and touched nothing else, and did not put the other DLL back (that session's worktree holds only its client DLL, and no sanctioned deploy mode restores a single foreign DLL). The user was told at once. **Rule taken from it: read `uibridge_status` (other sessions, the loaded build) before EVERY deploy, not only before a raid.** Consequence for this record: the 0.7.0 DLLs are on the host's disk but were never loaded there; no host run of 0.7.0 exists.
- **Two branches of that other session are not part of this preparation:** `fix/dynamicmaps-130` (three commits on `6a5c8c4`) and `feature/block-dynamicmaps` (two commits on `1ad18c3`). Whether they go into 0.7.0 is the user's decision; the texts then change in README, FORGE, CHANGELOG, the release note, the packer prose and its pins together.

## 2026-11-22 - The two DynamicMaps branches merged, the review, the gates and the host run of the merged build

Everything above this line is the state of 2026-11-21 and stays as written; this section records what changed on 2026-11-22 and supersedes the statements named below. Times are the VM's and GPU-2's local clock. Nothing is pushed, tagged, cut, scanned, uploaded or published: `origin/main` = `main` = `6a5c8c4`, release 0.7.0 stays PREPARED, NOT PUBLISHED, and publication needs the user's explicit word. The number 0.7.0 is still the lead's pick; the user has not commented on it.

### The user's decision, the merge and the one code change

- **User, 2026-11-22, verbatim: "die beiden anderen zweige können gemerged werden, Codex hat dazu auch irgendwas verfasst".** It answers the open question of 2026-11-21 whether the two branches of the other session go into 0.7.0.
- **`4587e5b`** (2026-11-22): merge of `feature/block-dynamicmaps` (tip `b09bece`) into `feature/yard`, conflict-free. That branch contains `fix/dynamicmaps-130` (tip `647dbc8`) completely. The merge brought 34 files (+1525/-72): the DynamicMaps 1.3.0 adapter (`DynamicMapsOwnedCatalog.cs`, `DynamicMapsIntegration.cs`), Block's overview as the seventh owned pack (`DynamicMapAssets.Maps`, staging, the scoped deploy selector, the validator, the packer rule, tests), the records [dynamicmaps-130.md](dynamicmaps-130.md) and [yard-dynamicmaps.md](yard-dynamicmaps.md), and the other session's edits of README, FORGE, CHANGELOG, the release note, the packer prose, `docs/dist-manifest.md`, AGENTS.md, PLAN-YARD and INDEX.
- **`3dcf98d`** (2026-11-22), the lead's one code change after the review: `Plugin.Awake` wraps `DynamicMapsIntegration.Install(_harmony)` in try/catch (one Warning `[DynamicMaps] Optional overview integration could not be installed (...)`; the installs after it still run).
- The other session's branches `feature/block-dynamicmaps` and `fix/dynamicmaps-130` and its worktrees (`work/dynamicmaps-yard/repo`, `work/dynamicmaps130-impl/MapPort`) still exist; the lead deleted nothing. Its working folders `work/dynamicmaps-yard/` and `work/dynamicmaps130-impl/` (ignored) hold its evidence.

### What the merge supersedes in this record (review finding DOC-8)

- The feature matrix cell Block / DynamicMaps overview ("no") is now yes: seven overviews.
- RISK (3) ("the test host runs DynamicMaps 1.3, so Chop Shop's overview is skipped there") no longer applies to a build of the merged tree: the adapter supports 1.2.1 and 1.3.0, and the host's 1.3.0 loads the owned packs (host run below).
- The open verification item that logs `DynamicMaps API differs from verified 1.2.1; optional maps integration skipped` and names the fix as "on the unmerged branch `fix/dynamicmaps-130`": the branch is merged; on the merged build the Warning is gone from the host's log (host run below).
- The decision "Tested with 1.2.1; 1.3 is not promised ... changes if `fix/dynamicmaps-130` ships before the cut": it ships; the DynamicMaps sentence of the texts follows the evidence in "The evidence audit" and the host run below.
- The plan of 422 files is now 424 files (gates below); the Yard part is 24 files.
- The USER row "the DynamicMaps 1.3 branch" is answered by the user's sentence above; what remains for the user is to look at Block's overview.
- The closing statement that the two branches "are not part of this preparation" is the state of 2026-11-21.
- **Yesterday's stale host state is replaced:** the 23:20 deploy of 2026-11-21 left the host's mirror with `0.7.0+1ad18c3` (no 1.3 adapter, no registration of Block's overview). The deploy of 2026-11-22 09:41 replaced it with `0.7.0+3dcf98d`, which was loaded and run on GPU-2 (host run below).

### Gates (lead, 2026-11-22, VM)

On the merge tree `4587e5b` (09:19 to 09:29):
- Client Release build WITHOUT DllOnly: 0 warnings, 0 errors (stages and encrypts); server Release build clean.
- The seven `keys/*.json` byte-identical before and after the staging.
- `dist/`: 424 files (Block's overview pair `dynamicmaps/ArenaMapPort_Yard/ArenaMapPort_Yard.jsonc` and `Layers/Ground.svg` staged; 14 overview files in 7 packs).
- `DLL_VERSIONS_OK 0.7.0 client/server source, project and staged binaries`; `validate_map_descriptors.py` OK (14 descriptors).
- `DISTRIBUTION_OK 7 maps 21 bundles 3 grass bundles 7 sidecars encrypted`; `validate_bundle_names.py` OK (21 bundles); `validate_port.py --map yard --distribution --package work/extract/yard-native-v2`: `PORT_VALIDATED`.
- `make_release_zips.py --single --plan`: `total: 424 dist files, raw 7,867,323,170 B (7,502.9 MiB), estimated zip 7,833,789,314 B`, `estimated single ZIP: 7.30 GiB`. Multi-part `--plan`: core 242, equator 24, bay5 30, airpit 22, bowl 27, iceberg 28, autoservice 27, yard 24 (= 424); the Yard part is raw 1,457,025,946 B. The plan of 2026-11-21, before the overview, read 422 files, raw 7,867,157,355 B, estimate 7,833,724,745 B; the dist grew by 165,815 raw B: Block's overview pair is 160,183 B (1,685 B JSONC + 158,498 B SVG), the remaining 5,632 B are the two rebuilt DLLs.
- PowerShell checks: `UNITY_LOG_TESTS_OK 8 cases`; `DYNAMICMAPS_DEPLOY_TESTS_PASSED 168 cases`; `DLL_ONLY_DEPLOY_TESTS_PASSED (49 cases)`; `tools/tests/test_dynamicmaps_mutants.ps1`: eight source faults rejected, `DYNAMICMAPS_SOURCE_MUTANTS_REJECTED`.
- `tools/tests/test_dynamicmaps_contracts.ps1` against the real DLLs (1.2.1 from the other session's ignored `work/dynamicmaps-130-audit/release-1.2.1/BepInEx/plugins/mpstark-dynamicmaps/DynamicMaps.dll`, 1.3.0 from the mirror `D:\SPT41\BepInEx\plugins\mpstark-dynamicmaps\DynamicMaps.dll`): `DynamicMaps 1.2.1 actual DLL contract passed`, `DynamicMaps 1.3.0 actual DLL contract passed`, `DYNAMICMAPS_REAL_CONTRACTS_PASSED`.
- `tools/tests/test_dynamicmaps_yard.py` skips under the default python (numpy and shapely missing, the convention of `test_dynamicmaps_export.py`); with `work/dynamicmaps/venv/Scripts/python.exe`: 2 tests OK.
- .NET on `4587e5b`: 3,826 of 3,827 with five review agents running beside it; the one failure is the load-dependent `CryptoThroughputTests.Cng_ReachesAtLeast300MiBPerSecond_EncryptingAndDecrypting64MiB`.

On `3dcf98d` (09:35 to 09:40, quiet machine): client Release build WITHOUT DllOnly and server build clean, the key files byte-identical, dist 424 files, `.NET: Bestanden! Fehler: 0, erfolgreich: 3827` (3,808 before the merge, +19), `DLL_VERSIONS_OK 0.7.0`. Staged DLLs: `0.7.0+3dcf98d8d870d8575f3d83b8555901f76e82f63b` (client and server). The whole Python suite on `3dcf98d` was still running when this section was written; the lead records its result in a following section.

### The review (workflow `merge-070-review`, 2026-11-22; five read-only reviewers, four skeptics)

Digests: `work/tmp/merge-070/handoff/{adapter-code,tests-pipeline,release-texts,internal-docs,evidence-audit}.md` (ignored).

- **Code:** no blocker. The reflection contract of both versions was checked with Mono.Cecil on the real DLLs; an unknown version (neither API shape) disables the optional integration with one Warning.
- **CODE-1 (confirmed, not changed, a known limit):** after DynamicMaps 1.3.0 fails to load an OWNED overview and calls its `RemoveUnavailableMap`, the next catalogue scan re-adds the same cached definition (native packs stay removed until their file changes), so a persistently failing owned overview is tried again each time the map screen is opened, with one more DynamicMaps error line each time. No shipped pack fails. Recorded as a limit and flagged as a follow-up.
- **CODE-2 (note, done as `3dcf98d`):** the try/catch in `Plugin.Awake`.
- The warning text `DynamicMaps API differs from verified 1.2.1; optional maps integration skipped.` stays: it is reached only in the 1.2.1-shaped branch (a `LoadFromPath(string)` exists but another member differs); a version with neither shape gets `Optional map loader unavailable; expected DynamicMaps 1.2.1 or 1.3.0 API: ...`.
- **Tests and pipeline:** the counts are confirmed (424 files, Block part 24). `DynamicMapsNoStage.targets` covers every staging target of the client and server projects and is inert for the test project's own build.
- **TEST-2:** the xunit tests prove the adapter against a MODEL of DynamicMaps 1.3 (`DynamicMapsNativeTestDoubles.cs`); only `test_dynamicmaps_contracts.ps1` (signatures and call order in the real DLLs) and a native raid touch the real DLL. The three PowerShell checks (`test_dynamicmaps_contracts.ps1` and `test_dynamicmaps_mutants.ps1`, both run by `tools/verify-dynamicmaps.ps1`, and `test_dynamicmaps_deploy.ps1`) are run by hand; no automated suite calls them.
- **TEST-3:** not tested: the 1.2.1 branch THROUGH `Install`, and the fallback path of `Install` (bind failure, unpatch, one warning). No `[Collection]` attribute groups the two test classes that patch the shared dropdown type; by reading it is harmless.
- **TEST-4:** `tools/tests/test_dynamicmaps_yard.py` reports a skip under the default python; run it with the venv python before calling the exporter row verified (done, above).
- A 1.2.1 DLL for the contract check is not named by any tracked file; on this VM it is under the other session's ignored `work/dynamicmaps-130-audit/`.
- **Texts:** the findings TXT-1 (README's Block row still said "no DynamicMaps overview": a real leftover), TXT-2, TXT-3, TXT-4, TXT-6, TXT-7, TXT-8/COUNT-1, M-1 and DOC-1 (`tools/dynamicmaps/README.md` "six" to "seven") are assigned to the player texts and state documents of the same documentation pass (other writers; the lead checks the result); TXT-5 was refuted and is not applied.

### The evidence audit

- The other session's native claims match its evidence files: Block on client `0.6.0+8a1ec4e` (MVID f8a38cc5), DynamicMaps 1.3.0, 2026-11-21 23:13 CET (raid 22:13:48Z to 22:14:40Z), 18 catalogue entries (11 third-party + 7 owned), 26,771 sprite vertices, five positions and headings, 0 MapPort and 0 DynamicMaps warnings. The 1.3 loader run of 2026-11-20 was Iceberg and Factory on `0.6.0+b7fb819` (17 definitions); the other five older maps only registered there. No native run on DynamicMaps 1.2.1 after the loader change exists.
- The accepted Yard pack (`work/dynamicmaps/release/ArenaMapPort_Yard`) is byte-identical to the other session's final generated pair and to the pair on the mirror; `MapInternalNames` = `arena_yard`; both exit markers lie within 0.01 m of `tools/maps/yard.exits.json`; the SVG is vector-only (9 paths, 10,175 path points, no image, base64, href or script), like the six older packs.
- **EV-1:** `dynamicmaps-130.md` says the ArenaMode client and BigBrain companion DLLs were deployed to GPU-2 for its test, but not by which path; this project's deploy script has no mode for ArenaMode's files. For the Yard pair and DLL the other session's folder retains only a dry-run log.
- **EV-2:** that work created git worktrees of the user's ArenaMode repository: `git -C D:/SPT41/Development/ArenaMode worktree list` (read-only, lead, 2026-11-22) lists `D:/ArenaMapPort/work/dynamicmaps130-impl/ArenaMode` on a branch `fix/dynamicmaps-130` and `ArenaMode-vw1` to `-vw6` (detached) under the same folder, beside the user's own entries. Neither record mentions it. The record's first paragraph says the user asked that session for the ArenaMode adaptation too. It is the user's repository and that session's work; the lead changed nothing in the ArenaMode repository and reports it to the user.
- **EV-5:** the mirror's DynamicMaps config gained DynamicMaps' own comment blocks and the line `arena_yard = Automatic` (DynamicMaps' normal preference registration; no existing value changed).

### Host run of the merged build (lead, GPU-2 = host2, 2026-11-22)

- Before the deploy: `uibridge_status` answered "EFT is not running on host2" (09:18 and again 09:40; `gpu-run hosts`: "host2: EFT not running", no other session). The mirror held `0.7.0+1ad18c3` from the lead's deploy of 2026-11-21 23:20.
- Deploy: dry run `plan: 424 file(s), 7502.9 MB total -- 0 new, 2 to overwrite, 422 already identical` (the two DLLs; Block's overview pair was already on the mirror, identical); `tools/deploy-to-spt.ps1 -SptRoot 'D:\SPT41' -Confirm` at 09:41: `OK -- 424 file(s) copied into D:\SPT41`. `gpu-run deploy-wait --server`: `39 file(s) identical on the host after 79.1 s`, server started; `gpu-run start`: MainMenu after 72 s, `16/16 identical`.
- EFT instance `57d66d71` (started 09:45:16), `uibridge_status`: `Plugins (maschine-*): 16 — 16 identical (VM build = host disk = loaded)`; client `0.7.0+3dcf98d`; DynamicMaps 1.3.0 on the host; Arena 0.6.1.0.48206 there.
- Startup lines (BepInEx log):
  - `09:45:13.221 [Info] [Arena] Escape from Tarkov: Arena 0.6.1.0.48206 at C:\Battlestate Games\Escape from Tarkov Arena (registry; 15,351 files, 76.1 GB verified)`
  - `09:45:14.608 [Debug] [DynamicMaps] Optional owned map loader installed (1.3 catalogue): C:\SPT41\BepInEx\plugins\maschine-ArenaMapPort\dynamicmaps`
  - `09:45:14.935 [Info] Version 0.7.0 loaded. Maps: Arena: Equator, Arena: Bay 5, Arena: Air Pit, Arena: Bowl, Arena: Iceberg, Arena: Chop Shop, Arena: Block. Arena: found 0.6.1.0.48206. ...`
  - The Warning `DynamicMaps API differs from verified 1.2.1; optional maps integration skipped` that every host session logged since the host got DynamicMaps 1.3 is gone.
- Seven plain PMC raids without bots (noon, clear), ONE EFT process, in this order, each with inventory, then MAP opened once:

| map | raid start line of gpu-run | overview seen (screenshot) |
|---|---|---|
| Block (arena_yard), about 09:46 | `location 6.9 s (diff 1.8), game started 35.1 s` | player marker in the south pocket at `Player: -14.86 13.30 7.27` (DynamicMaps prints x, z, y) with the exit marker "Block South Gate"; after a teleport to the raised route (-22.0, 9.2, 48.8), yaw 270: `Player: -22.00 48.80 9.21`, the marker on the raised walkway pointing west |
| Equator, about 09:48 | `location 4.2 s (diff 1.5), game started 24.9 s` | "EQUATOR / GROUND", compass, player marker in the north booth area, "Equator North Gate" |
| Bay 5, about 09:50 | `location 5.6 s (diff 1.5), game started 26.9 s` | "BAY 5 / GROUND", player marker, "Bay5 North Loading Bay" |
| Air Pit, about 09:51 | `location 5.1 s (diff 1.6), game started 26.5 s` | "AIR PIT / GROUND", compass, player marker, "Air Pit North Gate" |
| Bowl, about 09:53 | `location 5.3 s (diff 1.5), game started 27.1 s` | player marker, "Bowl BTR Gate" |
| Iceberg, about 09:54 | `location 6.1 s (diff 1.8), game started 26.2 s` | player marker, "Iceberg West Gate" |
| Chop Shop, about 09:56 | `location 5.0 s (diff 1.7), game started 26.8 s` | player marker, "Chop Shop East Gate" |

  Each `gpu-run raid` line reported `log since start: warnings 0; errors 0` from mods (Equator: `warnings 0 from mods, 4 Unity Log`).
- DynamicMaps catalogue read in the Block raid (`uibridge_inspect` of `MapSelectDropdown._mapDefs`, a `Dictionary<string, DynamicMaps.Data.MapDef>` of `DynamicMaps, Version=1.3.0.0`): count 18 = 11 definitions under `C:\SPT41\BepInEx\plugins\mpstark-dynamicmaps\Maps\` and 7 under `C:\SPT41\BepInEx\plugins\maschine-ArenaMapPort\dynamicmaps\ArenaMapPort_<Yard|AutoService|Iceberg|Bowl|AirPit|Bay5|Equator>\`.
- Grass on the three grass maps from their bundles, same process:
  - `09:46:32.673 [Grass] built in 43.1 ms on the main thread: map=arena_yard instances=10132 x2 managers, art=Pack, upload=SetPixelData, drawing=True`
  - `09:53:01.741 [Grass] built in 15.2 ms on the main thread: map=arena_bowl instances=113643 x2 managers, art=Pack, upload=SetPixelData, drawing=True`
  - `09:54:26.207 [Grass] built in 10.8 ms on the main thread: map=arena_iceberg instances=15410 x2 managers, art=Pack, upload=SetPixelData, drawing=True`
- Iceberg: `09:54:31.332 [EmissionCap] map=arena_iceberg cap=4.60 floor=1.80 (owned scenes loaded): 37 emissive material(s), 5 capped (...), 15 lifted (...), 20 written, 0 left alone (changed elsewhere), 0 gone; 22695 renderer(s) scanned in 20 ms`.
- Block: `09:46:32.008 [RainUnderRoof] readback orientation probe: AsWritten`, `09:46:32.137 [RainUnderRoof] map=arena_yard cameraUnderRain=False camera=(-14.86, 8.76, 13.35) photoTop=29.46 ...` (clear weather, under the pocket's cover: no rain test in this run).
- The hideout was opened once after the raids: `screen=Hideout` after 6.4 s; back to the main menu.
- Warnings of the whole session (log file of the process, `level<=Warning`): 0 lines from the source `maschine-ArenaMapPort`, 0 lines from DynamicMaps. The other lines: three HarmonyX warnings of other mods at start; Unity Log: one `Coroutine couldn't be started because the the game object 'MenuScreen' is inactive!` (09:48:00, leaving the first raid), Equator's `Renderer 'shadow' is registered with more than one LODGroup ('refrigerator_drink (2)' ...)` lines, the hideout's `light_CellPlafonds_holder_LOD0` LODGroup lines and `Can not play a disabled audio source` (all three kinds appear in earlier records of this project).
- End state: MainMenu, no control lease, test hooks off (`weather override ended: the raid ended`), 16/16 identical, no other session. The map zoom was changed by three wheel steps in the Block raid and put back by three; no config value was set.
- Screenshots (ignored folder): `analysis/screenshots/merge-070-20261122/01-block-overview-south-pocket.png`, `02-block-overview-raised-route-yaw270.png`, `03-equator-overview.png`, `04-bay5-overview.png`, `05-airpit-overview.png`, `06-bowl-overview.png`, `07-iceberg-overview.png`, `08-chopshop-overview.png`.
- **Not checked in this run:** any position other than the spawn on the six older maps (one marker each, by eye on a screenshot, no measured comparison); the minimap and peek views; ArenaMode and its Domination overlay; DynamicMaps 1.2.1 in game; rain; bots; a second process or a rescan after a pack file changed; the publication build (the DLL of this run is `0.7.0+3dcf98d`; a later rebuild changes the commit suffix). The author has not looked at any of it.

### What is open

- The full Python result on `3dcf98d`: recorded by the lead in a following section.
- Everything after it, on the user's word: `verify-keys` on every Arena build at hand, the cut (`make_release_zips.py --single --extract-check`, `--verify-only`), VirusTotal for both DLLs, the Pixeldrain upload (dry run first), the placeholders, the publication commit, the merge, the tag, the push and the GitHub release. The release build and the host run are repeated on the publication tree.
- User items: the number 0.7.0, looking at the grass, the rain drops, Block's marker and Block's overview, ArenaMode's Block entry and pin (0.6.0), the Forge entry, the stale v0.5.0 body, and the ArenaMode worktrees of EV-2.

### The documentation pass and the lead's acceptance (2026-11-22, 10:00 to 10:15)

- Workflow `merge-070-docs`: three writers with disjoint files (player texts; AGENTS.md, HANDOFF.md, INDEX, MAP-RECIPE, PLAN-YARD; these progress records), each read by an adversarial checker. The text findings named above are applied: README's Block row, the DynamicMaps sentences of README, FORGE, CHANGELOG, the release note and the package README.txt (all seven overviews opened in game on the test host with DynamicMaps 1.3.0; 1.2.1 offline only since the loader changed; no commit id in a player text), the 424-file figures, "The author has not looked at the overview", `tools/dynamicmaps/README.md` (seven definitions, where a 1.2.1 DLL comes from), the packer docstring, the dist-manifest addendum. TXT-5 stays unapplied (refuted).
- New pin: `PlayerTextOverviewPinTests` in `tools/tests/test_release_zips.py` (README.md and FORGE.md carry none of "no DynamicMaps overview", "Block has no overview", "not Block)"; README's Block row names a DynamicMaps overview): the leftover of TXT-1 had passed because no test read README.md.
- AGENTS.md: the status note, the paragraph "Block overview and DynamicMaps 1.3", the first paragraph of "Release configuration" (plan of 22 November, the DynamicMaps wording rule, what ran on the merged tree, the open list), a Layout row, two recipe lines and the Traps entry "DynamicMaps adapter" (eight points, CODE-1 as point 7). HANDOFF.md item 21 corrected in place and item 22 added. MAP-RECIPE.md has a section "DynamicMaps overview (optional, the user's call)".
- The checkers' verdicts: player texts clean; four corrections in the other two sets, applied by the lead: MAP-RECIPE said an overview's acceptance needs "five or more positions" (no such rule exists: Block was checked at five, the project sets no count); AGENTS.md gave the push of `main` as 07:15 (the reflog says 2026-11-21 07:05:22); HANDOFF item 22 said the text findings "were applied" without the refuted TXT-5; `yard-dynamicmaps.md`'s new section gave the Block raid as "between 23:11 and 23:20" (it ran at 23:13 CET, 22:13:48Z to 22:14:40Z; the instance started at 23:11).
- The lead read every changed passage. Pins on the final texts: `test_release_zips.py` `Ran 205 tests` `OK` (204 before the new pin class), `test_changelog.py` 12 OK, `test_crowdless_maps.py` 14 OK, `test_new_map_checklist.py` 6 OK, `test_dll_versions.py` 1 OK; `git diff --check` clean. The four progress records gained additions only.
- A follow-up for CODE-1 is proposed to the user as a separate task; nothing of it is done here.
- Memory of the incident of 2026-11-21: this time `uibridge_status` and `gpu-run hosts` were read before the deploy (EFT not running, no other session).

## 2026-11-22 (2) - The author's statement, the grass wind, two commits of another session, the Python runs

**The author's statement (user, 2026-11-22, after looking at the game on the author's own PC):** "Block passt, Regentropfen auch, mir fiel aber gerade auf dass das Gras sich nicht im Wind bewegt, bekommen wir das auch noch hin? beim drüberlaufen wird es zumindest platt gedrückt". What it covers, and nothing more:
- Block: "Block passt". The sentence names no single item (not the marker, not the overview, not the gates). It follows the author's approval of 2026-11-21 of Block's look and sound. The author looked at Block again on 22 November and found it fine. The earlier statements that the author has not looked at the marker or the overview are no longer certain either way and are removed from the texts; they are not replaced by a claim that the author saw the marker or the overview.
- Rain drops: "Regentropfen auch [passt]". The author has seen the rain drops on the screen and found them fine. Still not checked by anyone by eye: glasses, ripples, wet props; the other maps than the lead's two (the sentence names no map).
- Grass: the author HAS seen the grass (noticed that it did not sway and that walking over it presses it flat) and asked for wind motion. "The author has not seen the grass" is false now. The sway added in answer has NOT been seen by the author. Nothing in the sentence approves the grass's look.
- Nothing about the version number, and no word for publication.

**The grass wind (commit `0ee4442`), short.** EFT's `Foliage_Simple` has wind code that reads the global `_WindTimeScaledVector`, which only EFT's `WindCalculator` advances; a ported scene has no TreeWind settings, so no calculator exists (host: `_windCalculator = null` on Block in a storm). The port now advances that phase itself (`NativeGrassWind.cs`, `NativeGrassWindModel.cs`, developer key `Map (<key>)/GrassWind`, default on, not public). .NET `Bestanden! Fehler: 0, erfolgreich: 3845`; client Release build without DllOnly and server build clean, dist 424 files, staged DLLs `0.7.0+0ee4442363e4f855e6f94c84dfe8c7441aa7af89`; host run on GPU-2 11:02 to 11:12 on Block, Bowl and Iceberg (frame comparison at wind 0.9: Block's lawn cells 7.6 / 10.5 / 14.4 / 23.2 / 15.3 / 12.9 against 7.6 / 9.1 / 7.6 / 11.9 / 6.7 / 4.3 without the repair; `119.5 fps` with the limiter on; 0 ArenaMapPort warnings; the hideout opens). Cause, repair, tests, review, quoted lines and the not-checked list are in [native-grass.md](native-grass.md), section "2026-11-22 - Wind". The sway is checked by the lead only; the author has not seen it.

**Two commits of another session, fast-forwarded into `feature/yard` at 10:31 (not the lead's work).**
- `1c5feef` "DynamicMaps 1.3: keep an owned overview it could not load out until its files change": the follow-up for review finding CODE-1 that the lead had proposed. `DynamicMapsOwnedCatalog` binds `RemoveUnavailableMap`, flags the cache entry, leaves a flagged entry out while its files are unchanged; a wrapped postfix in `DynamicMapsIntegration`; tests, twelve source mutants (eight before), the 1.3.0 contract check pins the new member. It edited AGENTS.md (Traps "DynamicMaps adapter" point 7 and the mutant count) and appended "2026-11-22 - CODE-1 fixed" to dynamicmaps-130.md. Its own statement: `verify-dynamicmaps.ps1` exit 0 with both DLLs, NOT host-checked. Host fact since then (lead): the build `0.7.0+0ee4442` contains it and logged `[DynamicMaps] Optional owned map loader installed (1.3 catalogue)` with 0 warnings; no overview was opened in the 11:04 session and a failing overview was never provoked on a host. CODE-1 is therefore no longer "a known limit left unchanged": it is fixed in code and unit-tested, its behaviour on a host is unverified.
- `27651f1` "Run the CNG throughput benchmark alone instead of beside the whole suite": `CryptoThroughputTests` is in a collection that xunit runs after the parallel ones, three attempts, floor unchanged; record in bundle-encryption.md. AGENTS.md still told agents that this test can fail singly on a full run; since `27651f1` it runs alone at the end.

**Python suite results (lead).**
- First run, started 09:40 on `3dcf98d` (the texts were edited during the run): `Ran 3672 tests in 1857.612s`, `OK (skipped=72)`.
- Second run, started 10:11 on `d3609ff`; at 10:31:40 the checkout was fast-forwarded to `27651f1` by the other session (C# sources, two PowerShell tests, AGENTS.md and two records changed under it): `Ran 3673 tests in 1947.003s`, `OK (skipped=72)`. Neither run is a run on one fixed tree.
- A run on the final committed tree of this pass: recorded by the lead in a following section.

**What this supersedes in the sections above.**
- The section "2026-11-22 - The two DynamicMaps branches merged ..." says of CODE-1 that a follow-up is proposed and nothing of it is done: answered by `1c5feef` (fixed in code, unit-tested, not host-verified). That section stays as written.
- The same section says the lead records the Python result in a following section: the two runs above are that record, with their limits; the run on the final tree is still open.
- User items: the earlier sections name looking at the grass, the rain drops, Block's marker and Block's overview. The statement settles the rain drops and Block as a whole (neither the marker nor the overview singly); the marker and the overview are no longer listed as items the author has not looked at. Open for the user: the number 0.7.0, looking at the grass's sway, ArenaMode's Block entry and pin (0.6.0), the Forge entry, the stale v0.5.0 body, the ArenaMode worktrees of EV-2.

**Open.** The Python run on the final committed tree and the validators on the new dist (424 files): recorded by the lead in a following section. Publication only on the user's word: the release build without DllOnly, the gates and the host run are repeated on the publication tree, then `verify-keys` on every Arena build at hand, the cut, VirusTotal, the Pixeldrain upload (dry run first), the placeholders, the publication commit, the merge, the tag, the push and the GitHub release. Nothing is pushed or published; release 0.7.0 stays prepared, not published.

### The lead's checks after the texts of "2026-11-22 (2)" (11:11 to 11:30)

- **Validators on the dist of `0ee4442`** (the first run took 14 minutes instead of about 80 seconds: the deploy of 11:02 had Syncthing re-reading the mirror and three writers were running test modules): `DLL_VERSIONS_OK 0.7.0 client/server source, project and staged binaries`; `validate_map_descriptors.py` OK (14 descriptors); `DISTRIBUTION_OK 7 maps 21 bundles 3 grass bundles 7 sidecars encrypted`; `validate_bundle_names.py` `OK -- 21 bundle(s)`; `validate_port.py --map yard --distribution --package work/extract/yard-native-v2`: `PORT_VALIDATED`; `make_release_zips.py --single --plan`: `total: 424 dist files, raw 7,867,328,290 B (7,502.9 MiB), estimated zip 7,833,791,625 B`, `estimated single ZIP: 7.30 GiB`; multi-part: Yard part 24 files, raw 1,457,025,946 B, total estimate 7,833,799,395 B. The morning's figures (raw 7,867,323,170 B, estimate 7,833,789,314 B) belong to the DLLs of `3dcf98d`; the client DLL grew by 5,120 B. Every figure is an estimate until the cut.
- **The documentation pass** (workflow `grass-wind-docs`: three writers with disjoint files, each read by a checker): the records were clean; the checkers found eight stale or over-wide sentences in the other two sets, all applied by the lead: the release note said vanilla Ground Zero "was not changed" (only its calculator's state was read); three index entries, two HANDOFF passages and one PLAN-YARD note still said the author had not seen the grass, the rain drops, the marker or the overview; AGENTS.md named the `3dcf98d` DLLs as the staged ones; the Traps entry "Rain on the camera" still listed "the author's eyes" as unchecked. The lead read the changed passages and softened one README clause that stated the faster sway in a scope view as a fact (it follows from the code; nobody looked).
- **Pins on the final texts:** `test_release_zips.py` `Ran 205 tests` `OK`, `test_changelog.py` 12 OK, `test_crowdless_maps.py` 14 OK, `test_new_map_checklist.py` 6 OK, `test_dll_versions.py` 1 OK; `git diff --check` clean.
- **Host state at the end of the wind run:** GPU-2 at the main menu, client `0.7.0+0ee4442` loaded (16/16 identical), no control lease, no other session; every weather value was set through the test hook and ended with its raid; no config value was written.
- The full Python suite on the committed tree of this pass: recorded in the next section.
