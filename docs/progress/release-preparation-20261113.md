# Release 0.6.0 preparation - 2026-11-13

The first release with Chop Shop (the sixth map). The user's words of 2026-11-13: "denke das passt alles, fehlt noch was fuer die veroeffentlichung? ansonsten
schonmal version erhoehen und docs aktualisieren". This record holds what the preparation step wrote and the readiness state. **Nothing is published:** no cut, no VirusTotal
scan, no upload, no tag, no merge, no push, no GitHub call; the release date is unknown and publication waits for the user's explicit go. The number 0.6.0 is the lead's choice
by the project's pattern (a new map is a minor version, as 0.3.0 Bowl and 0.5.0 Iceberg); the user named no number and can still change it, in which case the version
strings, the file names `docs/releases/v0.6.0.md` and `work/releases/0.6.0/...` and the README links move together. The release form stays one whole ZIP on Pixeldrain plus a GitHub release
with the notes and the checksum file (as 0.4.0 and 0.5.0) unless the user says otherwise.

## Baseline

HEAD `86d8b71` on `feature/autoservice` (tree clean at the start), 67 commits since the tag `v0.5.0` (`273a068`); `main` = `origin/main` = `52d0a4d` is an ancestor of HEAD (63 commits ahead, none behind, so `main`
fast-forwards). Recorded gates on `160523d` (`86d8b71` only added records): .NET 3,300 of 3,300, Python 3,262 tests OK (68 skipped), `validate_map_descriptors.py` OK (14 descriptors),
`DISTRIBUTION_OK 6 maps 18 bundles 6 sidecars encrypted`. The staged dist is complete: 396 files, 6 maps x 3 bundles, 6 key files, 6 shader sidecars.
`make_release_zips.py --single --plan`: 396 files (+4 generated), raw 6,378,924,543 B (6,083.4 MiB), estimated ZIP 6,348,651,635 B (about 5.9 GiB; 0.5.0: 5,370,649,086 B, 359 files), parts core 242,
equator 24, bay5 30, airpit 22, bowl 26, iceberg 27, autoservice 25.

## What was bumped and written in this step (no commit, no build)

- **Version 0.6.0:** `ArenaMapPort.Client.csproj`, `ArenaMapPort.Server.csproj`, `Plugin.PluginVersion`, `ModMetadata`; the stale-control literal of `tools/tests/test_dll_versions.py` (`'0.5.0'` to `'0.6.0'`).
  The staged DLLs in `dist/` still carry 0.5.0: `test_dll_versions` and `validate_dll_versions.ps1` stay red until the client (Release, without DllOnly) and the server are rebuilt. History mentions of 0.5.0 stay.
- **Texts:** `CHANGELOG.md` (0.6.0 headed "unreleased", the date and links come at publication; `test_changelog` allows that form on the newest section only), `docs/releases/v0.6.0.md` (placeholders, see below), `README.md`, `FORGE.md`,
  `docs/dist-manifest.md` (section "Release 0.6.0 contract" and the two stale 2026-11-11 "pending" statements marked as superseded), AGENTS.md ("Release configuration" first paragraph and one status sentence in the introduction),
  HANDOFF.md (top block), `docs/progress/INDEX.md`.
- **Packer texts** (`tools/release/make_release_zips.py`): `PROSE_NAMES` maps the registry display "AutoService" to "Chop Shop" (the generated README.txt "Included locations" line would otherwise print AutoService), the crowd sentence lists
  Chop Shop, the audio-limits paragraph says that the roar level, Chop Shop's mix, crowd and gate sounds are not listened to, the gate paragraph says that exits stand in the team start areas, `ENCRYPTION_TEXT` says six maps
  and names Chop Shop's own checks and the maps without a 0.6.0 check. Pins added in `tools/tests/test_release_zips.py` (the Chop Shop prose name, "six maps", the 0.6.0 gap, the 0.6.2.0.48598 check).
- **Per-map feature matrix used in every text:** crowd on Equator, Bay5, Air Pit, Bowl and Chop Shop (not Iceberg); per-light flares on Equator, Bay5, Bowl and Chop Shop (not Air Pit, Iceberg); DynamicMaps overview on five maps (not Chop Shop); native water on Bay5
  and Iceberg; Chop Shop: no winter or snow set, summer only.

## Readiness table

| | Item |
|---|---|
| DONE | Staged dist complete (396 files, 18 bundles, 6 key files); code, bundles, deploy and GPU-2 host raids for all six maps (records of 2026-11-11 to 2026-11-13); recorded gates green on `160523d`; `main` fast-forwards; no stray branch carries unmerged work (`claude/cool-mclean-d3f849`, `claude/wonderful-hawking-613f6e`, `fix/spawngate-floor-probe` are ancestors of HEAD); the version bump and the texts above |
| TO DO AT PUBLICATION (lead, on the user's go) | Client Release build WITHOUT DllOnly and server build at 0.6.0; .NET tests, Python suite, `validate_dll_versions.ps1`; `validate_distribution.py`, `validate_bundle_names.py`, `validate_port.py --map autoservice --distribution`; `verify-keys` against every Arena build at hand; deploy and a GPU-2 run of the exact build (all six maps, the hideout, 0 warnings, the Iceberg `[EmissionCap] ... cap=4.60 floor=1.80` line and the two re-seed lines); `make_release_zips.py --single --out work/releases/0.6.0/pixeldrain-<commit> --extract-check <empty dir>`, then `--verify-only`; `scan_virustotal.py --version 0.6.0` for both DLLs; `upload_pixeldrain.py` (dry run first; the ZIP is about 0.9 GiB larger than 0.5.0's, which Pixeldrain took); fill the placeholders of the note; publication commit; fast-forward `main`, tag `v0.6.0`, push; GitHub release with the note as body and the `.sha256` as its only asset; records say "published" |
| USER | Confirm the number 0.6.0; back up `work/release-keys` off the VM (the new `arena_autoservice.key` of 2026-11-11: the user was asked to on that day and no record says it happened; losing it means `rotate-key`); listen and look (below); Forge entry; whether to delete 0.3.0 / 0.4.0 (decided to stay online so far); the stale v0.5.0 GitHub release body (HANDOFF's earlier next step, no record that it was done: it may still say ArenaMode has not adopted the contracts); the open options (more Player rows with sides All for Deathmatch variety; Equator South Gate to (0.5, 0.06, -41.65) to clear the elevator frames); ArenaMode: its whitelist row pins ArenaMapPort 0.5.0 (the check is advisory: a version difference gives a note, no refusal), the pin and its test follow after publication, and ArenaMode must not announce Chop Shop before this release ships |
| RISK | (1) Key wording: Chop Shop's key file has one wrap, for 0.6.2.0.48473, while `KEYS_BUILT_FOR` (pinned equal to `tools/bundle/key-sources.json`) says 0.6.1.0.48206; the texts therefore say that Chop Shop's file is built for 0.6.2.0.48473 and was also checked on 0.6.1.0.48206 and 0.6.2.0.48598. The lead re-runs `verify-keys` on all three installs before the cut and decides whether to stage Chop Shop on 0.6.1 as well. (2) The one-time re-seeds (Iceberg 4.8/1.9 to 4.6/1.8, roar-bed volume 2.0 to 1.0) have unit tests and no host log. (3) The unverified spawn-row and exit items below. (4) Until the staged DLLs are rebuilt, the dist says 0.5.0. (5) A reader of the repo before this step would have seen dist-manifest and AGENTS call Chop Shop "pending / NOT built"; both are updated. (6) ArenaMode warns about the MapPort version on every session until its pin moves (a note only). |

(2026-11-14: the USER row's key backup is done, Chop Shop was listened to by the author, and risk (4) is closed since the rebuild of
2026-11-13 evening; see the last section of this record. The table and the list below are the state of 2026-11-13.)

## Open verification items (stated as such in the release note)

- Not listened to: Chop Shop's ambient mix (a computed start value, 3 dB, offset 0.2, range 3), its gate sounds and its crowd; the roar beds at -26 dBFS RMS (6 dB louder than 0.5.0); Iceberg's ambience, lamp loops and gate sounds (as in 0.5.0).
- The one-time re-seeds (`iceberg-release.md` records "host check pending"; no `cap=4.60` line is quoted): the log lines of the moves were never captured (BepInEx logs them before UiBridge records). Lead's read of
  GPU-2's configuration on 2026-11-13 (`uibridge_config`, build `160523d`): `Lighting (arena_iceberg) › EmissionCap = 4.6 (default 4.6 ...)`, `EmissionFloor = 1.8 (default 1.8 ...)`, `EmissionDefaultsRevision = 1 (default 0; Int32) · hidden,
  protected`, `Spectators › ReactionBedVolume = 1 (default 1; 0–2; Single)`, `ReactionBedDefaultsRevision = 1 (default 0; Int32) · hidden, protected`. So both revision passes ran on that host and it plays with the new values (the Iceberg raids of
  2026-11-12 included); whether its saved pair was exactly 4.8 / 1.9 before, that is whether a value actually MOVED there, cannot be read back.
- Spawn rows and exits (`spawn-rows-and-pocket-exits.md`, "Unverified"): a respawn onto an in-box marker (Iceberg's Bear house (9)); Team Deathmatch on Chop Shop, Air Pit and Bay5 with the new exits; any Domination, Deathmatch, Zombies or
  Tactical match with the new rows; the flares from across a map, at night or in rain, and their sound; bots spawning at each of the 39 new rows; walking to Chop Shop's west exit (teleport only).
- Chop Shop item 15 (host iteration, still open in PLAN-AUTOSERVICE): the equator shutter's collider against its detail levels seen from the east shop, whether a player can climb the tribune decks, the rain-depth readback, a raid with ArenaMode run from this project, and the user's look
  at the lamps. The selection marker at 743/428 was seen on GPU-2 on 2026-11-12 (`toggle "ARENA: CHOP SHOP" click 986,457 [849,441 275x32]` beside `"CUSTOMS" ... [696,415 163x33]`, screenshot
  `analysis/screenshots/pocket-exits-20261112/selection-chopshop-743.png`; record `spawn-rows-and-pocket-exits.md`) and is pinned by the ink test. ArenaMode: the development build installed on GPU-2 offers ICEBERG and CHOP SHOP in its
  menu (gpu-run, 2026-11-12: `catalog drift: menu offers ICEBERG, CHOP SHOP`), and ArenaMode's own notes (`D:/SPT41/Development/ArenaMode/artifacts/chop-shop-20261112`, read only) record its matches on Chop Shop with the earlier port build
  `b7876d3`; none was run from here, and none with the appended rows and the moved exits.
- Sun shadows: Iceberg checked at shadow quality Low and Ultra only; Medium, High and the evening hours not checked. Released 0.5.0 has the Iceberg cafe leak below Ultra.
- Key checks: the 0.6.0.0.47752 check exists for the four maps of 0.4.0 only (not Iceberg, not Chop Shop); the sentence "the four older maps also open on 0.6.0" is not extended to them.

## Decisions taken in this step for the user (the lead surfaces them)

- 0.6.0 (see above); CHANGELOG headed "0.6.0 - unreleased" (the form `test_changelog` accepts without a date).
- Wording of the limitations: "not listened to" for Chop Shop's mix, gates, crowd and the roar level; "checked on the test host's selection screen" for the marker (lead's correction: the first wording, "an ink test, not a screenshot",
  missed the GPU-2 check of 2026-11-12); "not checked" for the unverified spawn and exit items; the Iceberg leak of 0.5.0 is named as such in the Fixed entry.
- User judgements on record: "sieht ganz gut aus, die zuschauer fehlen aber noch oder?" (2026-11-11, after playing Chop Shop on GPU-2), "sieht gut aus." (2026-11-12, after the lead's report on the crowd, the marker at 870/428 and Air Pit's
  board) and "denke das passt alles" (2026-11-13, after the lead's report on the spawn rows, the exits in the start areas and the marker at 743/428). What the author looked at or listened to himself before the last two is not recorded, so
  the player texts quote none of them as an approval of a specific item.
- The note leaves the contract of ArenaMode alone: the contracts are unchanged, "ArenaMode 0.4.0 and later has adopted" them as in the corrected 0.5.0 texts, and no promise about Chop Shop in ArenaMode or about lifting its Tactical refusal.
- The developer-only items (doorway probe, `spawn_coverage.py`, `distant_shadow_census.py`) sit in CHANGELOG "Changed (internal)" and not in the release note's player text.

## Placeholders left in `docs/releases/v0.6.0.md` (all set at publication)

`<release-date>`, `<pixeldrain-list-link>`, `<zip-bytes>`, `<zip-gib>`, `<pixeldrain-zip-page-link>`, `<pixeldrain-zip-direct-link>`, `<zip-sha256>`, `<pixeldrain-checksum-link>`, `<build-commit>`, `<file-count>`, `<scan-date>`, `<virustotal-client>`,
`<virustotal-client-result>`, `<virustotal-server>`, `<virustotal-server-result>`, `<final-gates>`, `<final-raids>`, and one `TBD-AT-PUBLICATION` paragraph note in "Verification and known limitations" (the development-state checks are replaced by those of the exact build).
The checklist comment at the top lists them; grep the file for `<[a-z-]+>` and `TBD-AT-PUBLICATION` and delete the comment before publishing.

## Not done in this step

Client and server builds, the release cut, the extract-check, the VirusTotal scan, the upload, the GitHub release, the tag, the merge into `main`, any push, any deploy, any host run, any key check, any commit.

## Lead's checks after the texts (2026-11-13)

- **Key files, 18 of 18 OK** (`ArenaMapPort.BundleCrypt verify-keys`, the staged `keys/*.json`, read-only against three installations): the five
  older maps `wrap 0 (built for 0.6.1.0.48206)` and Chop Shop `wrap 0 (built for 0.6.2.0.48473)` each `unlocks the store's content key against Arena`
  0.6.2.0.48473 (this VM), 0.6.1.0.48206 (GPU-2, `\\192.168.178.109\C$\Battlestate Games\Escape from Tarkov Arena`) and 0.6.2.0.48598
  (`\\MASCHINE11\Battlestate Games\Escape from Tarkov Arena`). So Chop Shop's one wrap is enough for the three builds at hand; no second wrap was staged.
  Arena 0.6.0 was not checked (no installation of it is at hand).

- **Build at 0.6.0** (the working tree of this step, before its commit): client Release WITHOUT DllOnly, so the staging ran for all six maps:
  `OK stage arena_<map>: 3 bundles encrypted (0 written), key file keys/arena_<map>.json: wrap present for Arena 0.6.2.0.48473, 2 wrap(s) in the file
  (unchanged)` for the five older maps and `1 wrap(s) in the file (unchanged)` for Chop Shop; server Release build; 0 warnings, 0 errors each. No bundle
  and no key file changed: the release differs from the deployed state only in the two DLLs.
- **Gates on that build:** `DLL_VERSIONS_OK 0.6.0 client/server source, project and staged binaries`; `DISTRIBUTION_OK 6 maps 18 bundles 6 sidecars
  encrypted`; `make_release_zips.py --single --plan`: `total: 396 dist files, raw 6,378,924,543 B (6,083.4 MiB), estimated zip 6,348,651,925 B (6,054.5
  MiB)`, `estimated single ZIP: 5.91 GiB`; Python `Ran 3262 tests in 1487.265s`, `OK (skipped=68)` (PYTHONUTF8=1); .NET 3,300 of 3,300
  (`dotnet test ... -c Release`, run alone: the writer's 3,299 was the CNG throughput test under load).
- These gates and the host run are repeated on the exact release build at publication (the publication commit changes the note's placeholders, and
  the DLLs carry the commit).

## Commit, rebuild and the deploy that did not reach GPU-2 (2026-11-13, 21:10-21:25)

- The preparation is commit `08369fc`. Both DLLs were rebuilt from it (client Release with DllOnly, server Release; `DLL_VERSIONS_OK 0.6.0 client/server
  source, project and staged binaries`) and deployed to the mirror: dry run `0 new, 2 to overwrite, 394 already identical` (the two DLLs), then
  `[deploy-to-spt] OK -- 396 file(s) copied into D:\SPT41`.
- **GPU-2 does not hold them.** `gpu-run status`: `server not running · game not running` (somebody closed both since the control raid of 00:41);
  `deploy-wait`: `2 of 24 file(s) are not identical on the host after 180.1 s` (both DLLs, `hash differs`; the host's server DLL is still the 00:35 file of
  build `160523d`). The VM's Syncthing reports the device SPT-GPU-2 connected and its side of the folder `D:\SPT41` as `remoteState=paused` with
  31,468 items / 12.1 GB outstanding: the folder is paused ON the host. That is the host's own setting and was not touched from here (hard rule: no
  host configuration edits). GPU-2 therefore still has the `160523d` DLLs (version string 0.5.0) on disk; the 0.6.0 DLLs arrive when the folder is
  resumed there.
- **Not host-run:** the 0.6.0 DLLs (the change against `160523d` is the version string in four places; no logic changed). The host run of all six maps
  and the hideout on the exact release build is a publication step anyway.

## 2026-11-14: user statements, GPU-2 on the 0.6.0 DLLs, and the hall reverb change (still NOT published)

User, 2026-11-14: "keys sind gesichert. chop shop habe ich mir angehört, passt alles. unseren hall sollten wir aber nochmal abschwächen,
gerade auf Equator & Airpit in der Haupthalle ist der ein wenig too much sobald man mit vollautomatik schießt".

- **Key backup: done (user).** "keys sind gesichert": the off-VM backup of `work/release-keys`, the USER item of the readiness table
  (the `arena_autoservice.key` of 2026-11-11), is closed by the user's statement. Where the copy lies is not recorded here on purpose.
- **Chop Shop listened to (user).** "chop shop habe ich mir angehört, passt alles": the author listened to the map and approves its sound.
  The sentence names the map as a whole, no single item; the texts (CHANGELOG, release note, README, FORGE, the generated README.txt) say
  exactly that, call the ambient mix a computed start value for which he asked no change, and keep the roar beds at -26 dBFS and Iceberg's items as not
  listened to.
- **GPU-2 runs the 0.6.0 DLLs.** The host's Syncthing folder was back on 2026-11-14 morning (completion 100 %, `remoteState=valid`);
  `gpu-run start`: `16/16 identical` (build `08369fc`), an Air Pit raid `warnings 0; errors 0` and an Equator raid `warnings 0 from mods,
  4 Unity Log; errors 0` at 09:34-09:48 (the reverb measurement of that morning). The "deploy that did not reach GPU-2" of 2026-11-13 is
  resolved; the host run of all six maps and the hideout on the exact release build stays a publication step.
- **Hall gunshot reverb weakened** (commit `7dba143`, record [shot-acoustics.md](shot-acoustics.md) "2026-11-14"): Hall ceiling
  AcousticTile and clutter 0.6 with the once-only re-seed `ShotAcousticsLab/HallDefaultsRevision`. Client DLL rebuilt, deployed and
  host-verified on GPU-2 at 10:16-10:25 (the re-seed line, old against new in Air Pit's and Equator's main halls and Bay5's spawn building:
  about -7 dB early and -8 to -12 dB late, the hideout, 0 warnings). .NET 3,305 of 3,305 (five new tests). The change is one new item in
  the 0.6.0 texts, stated as measured and not listened to. dist is unchanged in its file list (396 files); the client DLL differs from the
  build of `08369fc`, the server DLL in `dist/` is the deployed one of `08369fc`.
- **Gates on `7dba143`:** .NET `erfolgreich: 3305, gesamt: 3305`; Python `Ran 3262 tests in 1055.115s`, `OK (skipped=68)` (PYTHONUTF8=1, started after the commit;
  `test_dll_versions` among them); after the later text edits of this step `test_changelog` (12) and `test_release_zips` (195) again, OK.
  `validate_distribution.py` was not rerun (only the client DLL changed); it is a publication gate.
- **Open now:** the user's ear on the weaker hall (live steps: `ShotAcousticsLab/HallClutter` 0.8 for less, 0.4 for more) and on the
  roar beds; the user's go for the publication steps of the readiness table (cut, scan, upload, merge, tag, push), which rebuild both DLLs
  and repeat the gates and the six-map host run; the Forge entry; ArenaMode's pin and announcement after the release.

## 2026-11-14 (2): the hall reverb reworked a second time (revision 2), still NOT published

User, 2026-11-14, after listening to the table of `7dba143`: "habe ich mir angehört, ich glaube es ist eher das lange nachklingen das wir
nochmal überarbeiten sollten".

- Commit `ee46a3e`: Hall table ConcreteBlock walls / AcousticTile ceiling / CeramicTile floor / clutter 0.2, `RoomShotHallDefaults`
  revision 2 (a saved table moves only when it is the default of its saved revision), and the developer probe's far window and tone
  frequency key. Record: [shot-acoustics.md](shot-acoustics.md) "2026-11-14 (2)". The revision-1 table of the morning was never released;
  the release texts describe only the final table.
- Host-verified by measurement on GPU-2 at 11:19-11:32 (the re-seed line from revision 1, 0.5.0's table against the new one in Equator's
  and Air Pit's main halls and Bay5's spawn building at 1500 Hz and 400 Hz, three raids and the hideout, 0 warnings). .NET 3,313.
- The staged client DLL is the build of `ee46a3e`; the server DLL was rebuilt by the test run and deployed with it (same code), GPU-2's
  server was restarted. dist's file list is unchanged (396).
- **Gates:** .NET `erfolgreich: 3313, gesamt: 3313` (rerun; one run right after the build showed 3,312, the failing test was not captured);
  Python `Ran 3262 tests in 1258.803s`, `OK (skipped=68)` on the tree of `ee46a3e` with the texts of this step; `test_release_zips` (195),
  `test_changelog` (12) and `test_dll_versions` again on the final texts, OK. `validate_distribution.py` was not rerun (a publication gate).
- **Open:** the user's ear on revision 2 (live steps: `ShotAcousticsLab/HallCeiling = Concrete` for a longer ring, `HallClutter` 0.4 or
  0.6 for fewer highs). This replaces the "Open now" line of the section above, whose `HallClutter` advice belonged to revision 1.
  Everything else as in the section above.

## Published - 2026-11-14 (user: "habe ich mir angehört, passt so. dann branch mergen, löschen, pushen und release auf github")

The author listened to the final hall reverb table (revision 2) and approved it; the same message is the go for the publication.
He had committed new screenshots under `assets/` himself (`498adbc`).

- **Texts before the cut.** A three-lens audit of the texts that ship (release note against the records, consistency of the five texts,
  the player's eye; read-only agents) found, and the lead fixed: Iceberg's shadow leak is a finding at shadow quality Low only; the key
  sentence now reads the same everywhere; README and FORGE name 1500 Hz for the hall figures; the author's words are quoted in German;
  the release note says which hall rooms were measured; the generated README.txt names the five maps with a DynamicMaps overview.
  Build commit `b06f8dc`.
- **Release build (17:48):** client Release WITHOUT DllOnly: `OK stage arena_<map>: 3 bundles encrypted (0 written) ... (unchanged)` for
  all six maps (two wraps in the five older key files, one in Chop Shop's); server Release build; 0 warnings, 0 errors.
- **Gates on the build machine:** .NET `erfolgreich: 3313, gesamt: 3313`; Python `Ran 3262 tests in 1097.706s`, `OK (skipped=68)`;
  `DLL_VERSIONS_OK 0.6.0 client/server source, project and staged binaries`; `UNITY_LOG_TESTS_OK 8 cases`; `validate_map_descriptors.py`
  OK (14 descriptors); `DISTRIBUTION_OK 6 maps 18 bundles 6 sidecars encrypted`; `validate_bundle_names.py` OK, 18 bundles;
  `validate_port.py --map autoservice --distribution --package work/extract/autoservice-native-v2`: `PORT_VALIDATED` (479 materials,
  4,270 meshes, 950 textures, 15 NavMesh tiles, 1,946 triangles; without `--package` the validator takes the old spike package and
  fails with "source plan digest drift"); after the last text edits `test_changelog` (12) and `test_release_zips` (195) again, OK.
- **Key files, 18 of 18 OK** (`verify-keys`, read-only): 0.6.2.0.48473 (this VM), 0.6.1.0.48206 (GPU-2's share) and **0.6.2.0.48767**
  (`\\MASCHINE11`, a newer hotfix than the 0.6.2.0.48598 of the day before): the five older maps `wrap 0 (built for 0.6.1.0.48206)`,
  Chop Shop `wrap 0 (built for 0.6.2.0.48473)`, each `unlocks the store's content key`.
- **Host run (GPU-2, 17:59-18:08):** deploy `0 new, 2 to overwrite, 394 already identical` (both DLLs); the host's server was stopped
  until Syncthing had delivered both (`2 file(s) identical on the host after 62.2 s`), `gpu-run start`: `16/16 identical`. Server
  `Mod: ArenaMapPort Version: 0.6.0 ... geladen` (17:58:42), `Arena: Chop Shop is available in location selection.`; client `[Arena]
  Escape from Tarkov: Arena 0.6.1.0.48206 ... (registry; 15,351 files, 76.1 GB verified)` and `Version 0.6.0 loaded. Maps: Arena:
  Equator, Arena: Bay 5, Arena: Air Pit, Arena: Bowl, Arena: Iceberg, Arena: Chop Shop.` Plain PMC raids, bots None, each left through
  the menu:

| Map | Time | Load (`diff`) | Crowd `load total=` | NavMesh |
|---|---|---|---|---|
| Equator | 18:00 | location 5.3 s (1.6), game 33.0 s (14.8) | 2,191 ms | 10,395 / 4,567 |
| Bay5 | 18:01 | 5.5 s (1.4), 26.2 s (8.5) | 2,077 ms | 22,130 / 10,220 |
| Air Pit | 18:02 | 4.9 s (1.4), 25.9 s (8.7) | 2,156 ms | 11,811 / 5,433 |
| Bowl | 18:03 | 5.2 s (1.4), 26.9 s (9.2) | 2,101 ms | 12,704 / 5,890 |
| Iceberg | 18:04 | 5.9 s (1.7), 25.3 s (7.3) | none (no crowd) | 18,703 / 8,599 |
| Chop Shop | 18:05 | 4.9 s (1.7), 26.2 s (9.1) | 2,078 ms (30 fans) | 4,246 / 1,946 |

  Iceberg: `[EmissionCap] map=arena_iceberg cap=4.60 floor=1.80 (owned scenes loaded): 37 emissive material(s), 5 capped (largest
  original 67.79 ... -> 4.60), 15 lifted (smallest original 0.29 ...), 20 written, 0 left alone (changed elsewhere), 0 gone; 22693
  renderer(s) scanned in 23 ms` (18:04:47): the first host log of the 4.6 / 1.8 limits. Chop Shop: `BotZone.Init 'BotZone_Main':
  markers=16 onNavMesh=16`; a second raid (18:07) with a teleport into the hall: `arena_autoservice: room=5/IndoorBrick class=Hall
  dims=50.0x22.7x46.7 materials=ConcreteBlock,ConcreteBlock,AcousticTile,CeramicTile,ConcreteBlock,ConcreteBlock clutter=0.20
  model=ShoeboxRoom` (the hall table reaches the room the author had heard before the change; not measured there). gpu-run's raid lines:
  `warnings 0; errors 0` (Equator: `warnings 0 from mods, 4 Unity Log; errors 0`). Hideout opened in 7.8 s. No ArenaMapPort line at
  Warning or above in the log file of the process (20,069 lines). Not captured: the re-seed lines of the Iceberg limits and of the
  roar-bed volume (both revisions were already 1 on this host). Not run: a bot raid, an ArenaMode match, anything by eye or ear.
- **Cut (18:19-18:23):** `python tools/release/make_release_zips.py --single --out work/releases/0.6.0/pixeldrain-b06f8dc --extract-check
  work/tmp/extract-check-0.6.0-b06f8dc` (log `work/releases/0.6.0-make-b06f8dc.log`): `writing single ZIP (full): 396 files,
  6,378,927,615 raw bytes`, **`maschine-ArenaMapPort-0.6.0.zip 6,348,672,857 B 059ff5644843c226873d5b5c7db67bcfb005b071129c92c09a24e53c527e39e5`** (5.91 GiB),
  `extract-check OK: 396 files identical to dist, 4 generated entries ignored`, `RELEASE_ZIPS_BUILT`; `--verify-only`:
  `RELEASE_ZIPS_VERIFIED 0.6.0 (single ZIP)`.
- **Scan:** `scan_virustotal.py --version 0.6.0` (log `work/releases/0.6.0-vt.log`, record `work/releases/0.6.0/virustotal.json`; the
  analyses stayed queued past the tool's wait twice and were read on the third call): client DLL
  `57f2d09b86e3c8cb03018f1074bd9c09bf586ac954a8309a2f963617fbe1c078` 0 malicious, 0 suspicious of 75 engines (2026-11-14T17:10:11Z);
  server DLL `f0b050752306e6ecc6d7094f4835faac47a084ab52b2c60cf2b08f973b3174d5` 0 of 75 (17:22:46Z); `VIRUSTOTAL_SCAN_CLEAN 0.6.0`.
- **Upload:** `upload_pixeldrain.py --version 0.6.0 --release-dir work/releases/0.6.0/pixeldrain-b06f8dc --timeout 300` (dry run
  first; log `work/releases/0.6.0-upload-b06f8dc.log`, record `pixeldrain.json` beside the ZIP): list https://pixeldrain.com/l/QDbri7yE, ZIP https://pixeldrain.com/u/R7HpSAPk
  (direct https://pixeldrain.com/api/file/R7HpSAPk?download), checksum https://pixeldrain.com/u/hWQxcdv9.
- **Publication:** the links, the checksum, the build commit and the scan lines went into `docs/releases/v0.6.0.md` (its checklist
  comment removed; no placeholder left); this commit is the publication commit. What followed it (the fast-forward of `main`, the tag
  `v0.6.0`, the push, the deletion of `feature/autoservice` and the GitHub release) is recorded in the next section.
