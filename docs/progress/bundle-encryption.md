# Bundle encryption, stage 2 of the Arena gate - 2026-10-19

Record for stage 2 of the Arena gate (user decision 2026-10-19, "Stufe 2", after stage 1 of release 0.2.3 was merged into
`main` as `12c3731`; stage 1 is [arena-install-check.md](arena-install-check.md)). The design, the work split and the host plan are
in [../PLAN-ENCRYPTION.md](../PLAN-ENCRYPTION.md); this file is the record and is appended to as items land.

**Status (2026-10-19, 23:20, nothing committed): integrated and verified on the build VM, not yet run on a host.** The code of all areas
(`ArenaMapPort.Crypto/`, the `BundleCrypt` tool, the client loaders, staging, validators, packer) and the texts are in one tree and were
built, tested, staged, validated and dry-run-deployed by the integrator: see "Integration" at the end, which quotes every result. What
only a host can answer is still written as `<pending: ...>` (E4 to E7 and the host lines of the release notes): the plaintext baseline,
the GPU-2 run with the timing budget and the first Bowl raid through `AssetBundle.LoadFromStream` (risk R2), the negative tests, the
GPU-1 run. Nothing here is "host-verified" until those blocks are filled. (Earlier status, kept: the plan and the texts were written
first, the DOCS area read the code for names, flags, log lines and sizes only, and the texts were revised once after the first review.)

**First review round (what the texts got wrong and now say):** the client log tags are the code's (`[BundleKeys]` for key resolution,
`[Bundles]` and `[NavMesh]` for the bundles, and the one CNG Warning named); the client classes are named as they exist
(`BundleKeyResolver` with `PreparedBundle` and `BundleStreamRegistry`, and `BundleHost.LoadAssetBundle`; `LoadFromFile` is still called
there for a bundle without the AMPB magic); `validate_port.py --distribution` is NOT adapted and the texts no longer say it is (open item
H1); the Arena build that the 0.3.0 keys cover, 0.6.1.0.48206, is named in README, FORGE and the release notes; the Download block of
the release notes holds placeholders instead of the superseded cut's size, hash and commit; the ZIP growth estimate is 5 to 6 percent,
not 0 to 4; the staging and tool commands are the tool's own usage text.

## What it is, and what it is not

Every shipped map bundle (`<key>_scenes|navmesh|preset.bundle`, twelve for four maps) is an `AMPB` container (1 MiB chunks,
AES-256-CTR, SHA-256 per ciphertext chunk, header authenticated with HMAC-SHA256). The content key `K_map` of a map is random,
lives only in the ignored `work/release-keys/<map_key>.key` and is shipped wrapped, in `keys/<map_key>.json`, under a key that is
derived (HKDF-SHA256) from the SHA-256 of the Arena level files the map is built from. The key file lists those files by path and
size only; a published hash would let anyone derive the key. The client reads the files when a map is first needed in a session
(after the stage-1 check), derives the key, and loads the bundles through `AssetBundle.LoadFromStream` over a decrypting stream.

The 0.3.0 key files carry one wrap per map, for Arena 0.6.1.0.48206: the build on the build VM, the launcher manifest's version, which
the client prints at startup as `Arena: found 0.6.1.0.48206` (the launcher's registry entry reads the same on this VM). A player whose
files differ for a map gets no raid on that map until a package adds a wrap; a later package keeps this one's.
`built_for` is informational, the sizes and the derivation decide. Confirmed by reading the four staged `keys/*.json` on
2026-10-19: one wrap each, `"built_for": "0.6.1.0.48206"`, 5 (Equator), 9 (Bay5), 7 (Air Pit) and 7 (Bowl) level files.

It is **not copy protection**, and every text says so. An Arena owner can derive the key or run the (public) tool and decrypt the
bundles, and can share the key or the decrypted bundles; the key is tied to files, not to a purchase; a patched client skips the
gate but still needs the key. It raises the bar from "download and play" to "needs the game's own files", nothing more. What stays
plaintext (a possible stage 3, not planned): the art and audio (`loadscreens/`, `banners/`, `map-selection/`, the `acoustics/`
sounds and bake, the flare and water textures, `locales/map-descriptions.json`) and the derived data (`dynamicmaps/` SVGs,
`ai/*.ai.json`, `material-shaders.*.json`, `db/*/base.json`). Wording rule for every text: encrypted with a key derived from your
Arena files; not copy protection; art and audio stay plaintext; the keys are built for Arena 0.6.1.0.48206. Plan section 0.1 has the
full threat table.

## What is built (the contract the integrator verifies against)

| Part | What | Plan | State |
|---|---|---|---|
| `ArenaMapPort.Crypto/*.cs` | providers (Windows CNG through `bcrypt.dll` with a managed fallback), HKDF, AES-CTR, AMPB writer and `AmpbReadStream`, key-file parser, `KeyResolver` | 1.0, 1.1 | verified: `dotnet test` 1440 of 1440, 0 skipped, which includes `BundleCryptoTests` (234, both providers) and the real-data tests against this VM's Arena and the staged key files |
| `tools/bundle/ArenaMapPort.BundleCrypt` | `stage`, `encrypt`, `decrypt`, `inspect`, `read-range`, `plain-sha256`, `keys`, `verify`, `verify-keys`, `rotate-key`, `bench`; key store `work/release-keys`; Python wrapper `tools/bundle/bundle_crypt.py` | 1.2 TOOL-1, TOOL-2 | verified: Release build 0 warnings, the staging run and the validators below use it on the real bundles |
| Client csproj staging | target `ArenaMapPortBuildBundleCrypt` builds the tool, `ArenaMapPortStageWpOutputs` runs `stage` per map instead of copying the plaintext bundles | TOOL-3 | verified: a full client Release build staged 12 encrypted bundles and 4 key files, a second one left all 16 untouched; a DllOnly build skips it |
| Validators and packer | `validate_distribution` (magic, authenticating header, chunks, key files unlock against the installed Arena, no `UnityFS` file in `dist/`), `validate_bundle_names` (through `read-range`), `make_release_zips` (key-file rule, bundles STORED, AMPB check on dist and on the ZIPs). `validate_port --distribution` compares the staged bundles through `verify` (H1, done at integration). | TOOL-5 to TOOL-7 | verified: `unittest` 1893 tests OK (55 skipped), `DISTRIBUTION_OK 4 maps 12 bundles 4 sidecars encrypted` |
| Deploy and park scripts | no change: `deploy-to-spt.ps1` plans every file under `dist/` recursively (so `keys/` travels with the bundles, read at its copy-plan loop); `park-unreleased-dist.ps1` moves exact parked-map outputs by name and staging writes key files only for the four released maps, so nothing to park (if a map is ever un-released after staging, add `keys/arena_<stem>.json` to its list) | TOOL-7 | verified: the dry run lists the four key files as NEW with the bundles, `PARKED_DIST_OK Client 0 files` in every build |
| Client | `BundleKeyResolver` (key resolution once per map and session, `PreparedBundle`, `BundleStreamRegistry` closing the streams of unloaded bundles), `BundleHost.LoadAssetBundle` (the one loader, called by `BundleHost.TryServe` and `NavMeshInstaller.LoadOnce`; `LoadFromStream` for an AMPB container, `LoadFromFile` only for a plaintext development bundle); log lines E1-E9, W1, D1-D3 | 1.3, 0.9 | builds with 0 warnings, 72 `BundleKeyResolverTests` pass; **not run in EFT**: R2 and the Mono P/Invoke path are the GPU-2 run's questions |

Commands as the tool's usage text prints them (`ArenaMapPort.BundleCrypt help`, `Commands.UsageText`, read 2026-10-19):
`encrypt`, `decrypt`, `inspect [--verify-chunks] [--json]`, `read-range --offset N --length L`, `plain-sha256`, `keys`, `verify-keys`,
`verify --map-key K --plain PLAIN --enc ENC --key-store DIR` (ENC must be exactly the encryption of PLAIN), `stage`, `rotate-key
--yes` and `bench`. The flags are `--map-key` (not `--map`), `--key-store`, `--levels FILE` or `--manifest FILE`, `--arena DIR|auto`
and `--create-key`. Exit 0 is success with one `OK` line, 1 a failure with one `FAIL` line, 2 a usage error. The build is
`dotnet build tools/bundle/ArenaMapPort.BundleCrypt/ArenaMapPort.BundleCrypt.csproj -c Release` (the staging build does it by itself).
The staging build (a client Release build without `ArenaMapPortDllOnly`) runs once per map:
`ArenaMapPort.BundleCrypt stage --map-key K --bundle <plain scenes> --bundle <plain navmesh> --bundle <plain preset> --out-dir
dist/BepInEx/plugins/maschine-ArenaMapPort --key-store work/release-keys --levels tools/bundle/key-sources.json --arena auto
--create-key`; `-p:ArenaMapPortCreateReleaseKeys=false` drops `--create-key` and `-p:ArenaMapPortArena=<folder>` replaces `--arena
auto`. The validators call `inspect --verify-chunks`, `read-range`, `plain-sha256`, `decrypt` (a temporary copy under `work/tmp` for
the UnityPy fallback of `validate_bundle_names`) and `verify-keys`, so they need the VM's key store and Arena installation. Key
material is never printed. `rotate-key` is the recovery of a lost key store and needs `--yes`: every released bundle of that map
stops opening, so the map is re-encrypted and released again. The test seam `--arena-unverified DIR` needs `ARENAMAPPORT_TOOL_TEST=1`,
which only the tool's own tests set.

## Texts (DOCS, 2026-10-19)

| File | What changed |
|---|---|
| `README.md` | the Compatibility Arena bullet: the sentences below, and "no game data is read" narrowed to the installation check |
| `FORGE.md` | the same statement, shorter |
| `docs/releases/v0.3.0.md` | intro, Included, Changes (encrypted bundles, what it is not, which Arena build and Arena updates, Distribution), Known limitations, Installation (`keys` folder), the Download block as placeholders only |
| `AGENTS.md` | decision "distributed encrypted", Hard rule on keys (with `rotate-key`), Layout rows, Info/Debug/Error/Warning rule with the real tags, build and staging paragraph, a Traps bullet |
| `HANDOFF.md`, `docs/progress/INDEX.md` | a dated block, an index entry |

The README sentences (the only text the player reads in the repository's front page):

> The map bundles in the ZIP are encrypted with a key that your PC derives from the Arena level files each map is built from (read
> when the map is first used and never sent anywhere; the mod's `keys` folder lists them by path and size only), so they work only
> next to your own Arena files; this is a requirement, not copy protection (anyone with Arena can decrypt them), and the art and
> audio files shipped beside the bundles are not encrypted. The keys in this release are built for Arena 0.6.1.0.48206 (your build is
> in the client log at startup, `Arena: found ...`); if your build has different level files for a map, or an Arena update changes one
> of them, that map will not load (the client log names the cause) until an updated ArenaMapPort package carries the key for your
> build; the other maps keep working.

## Handoff items for the other owners (state when written, and at integration)

| # | Item | Owner | State |
|---|---|---|---|
| H1 | `tools/validate/validate_port.py` lines 121-128 (`--distribution`): the loop requires `sha256(work/build bundle) == sha256(dist bundle)`, which no encrypted bundle satisfies ("Staged artifact mismatch"). Replace the three bundle pairs with `bundle_crypt.verify(map_key, plain, enc, key_store=REPO/'work'/'release-keys')`; keep the hash comparison for the shader sidecar and `base.json`. Plan line 280 assigned it. | TOOL | **done at integration (2026-10-19)**: `bundle_crypt.verify` for the three bundles; `validate_port.py --map bowl --distribution --package work/extract/bowl-native-v2` ends `PORT_VALIDATED` |
| H2 | `docs/dist-manifest.md`: a dated block on top (encrypted `AMPB` bundles, `keys/<map_key>.json` per map, `DISTRIBUTION_OK ... encrypted`, the validators' checks, the new file count and sizes after staging) | integrator | **done** (dated block on top of the file) |
| H3 | `docs/progress/arena-install-check.md`: a "Stage 2 (2026-10-19)" pointer here; the check still compares names and sizes only, stage 2 additionally reads the level files of the map being loaded | integrator | **done** |
| H4 | `docs/PLAN-ENCRYPTION.md` line 284: the ZIP estimate "0-4 %" becomes "+5 to +6 %" (see E2) | plan owner | **done at integration**: the line now says +5 to +6 % and the measured +5.4 % is in section 5 of the plan |
| H5 | `ENCRYPTION_TEXT` in `tools/release/make_release_zips.py`: add that the keys are built for Arena 0.6.1.0.48206 (the generated README is the one text without it) | TOOL | **done at integration**: `KEYS_BUILT_FOR` in `make_release_zips.py`, a pin in `RealRepoTests` ties it to `key-sources.json`, README, FORGE and the release notes |
| H6 | the staging target passed `--create-key` by default, so a lost `work/release-keys` silently became a new content key (review finding) | integrator | **done**: the target passes `--create-key` only with `-p:ArenaMapPortCreateReleaseKeys=true`; `BuildWiringTests.test_staging_creates_a_content_key_only_on_request` pins it |

Done meanwhile by TOOL (read 2026-10-19): the generated package README no longer says that no game data is read (`REQUIREMENT_TEXT`
narrowed, `ENCRYPTION_TEXT` added; the pin in `test_release_zips.py` is theirs).

## Evidence

Each block carries date, time and build id (`uibridge_status` MVID for host runs). E1 to E3 and E8 were run at integration (2026-10-19, build VM) and are quoted in "Integration"; E4 to E7 are host runs and have not been done.

**E1 Builds and tests** (plan 2.1 steps 1-2): `dotnet test ArenaMapPort.Server.Tests -c Release`: `Bestanden!   : Fehler:     0, erfolgreich:  1440, übersprungen:     0, gesamt:  1440`
(baseline before stage 2 was 1134/1134); tool, client (full) and server Release builds `0 Warnung(en)`, `0 Fehler`;
`python -m unittest discover -s tools/tests -p "test_*.py"`: `Ran 1893 tests`, `OK (skipped=55)` (baseline 1818 tests, 55 skips).

**E2 Staging** (steps 3-6): the first full client Release build (`-p:ArenaMapPortCreateReleaseKeys=true`) printed `CREATED ...\arena_equator.key` and the same for
Bay5, Air Pit and Bowl (2026-10-19, about 22:56); `work/release-keys` copied outside the repo at once (a folder under the user profile, see
"Integration"); `python tools/validate/validate_distribution.py` ended with `DISTRIBUTION_OK 4 maps 12 bundles 4 sidecars encrypted`;
the second full build, without the property, gave identical SHA-256 for all 12 bundles and 4 key files (and untouched modification times);
`bench` (64 MiB, .NET 10 on this VM): CNG `aes-ctr 1716, sha256 1356, hmac-sha256 1699` MiB/s against managed `aes-ctr 2376, sha256 1355,
hmac-sha256 1307`, which says only that both are OS-backed here (Mono's number is the GPU-2 run's);
`make_release_zips.py --plan --single`: estimated single ZIP 3,242,902,036 B (3.02 GiB), 164.9 MB (+5.4 %) above the 3,078,043,845 B
plaintext cut of 2026-10-18 (0.2.3's 2,149,219,632 B held three maps, so it is no comparison). The estimate, from the dist measured on 2026-10-19 before staging (86 files, 3,256,631,057 B: bundles
3,216,202,258 B, everything else 40,428,799 B): the packer STORES ciphertext (it fails the 3 percent deflate probe), the container adds
about 32 B per MiB chunk plus a header (about 0.1 MB over 3.2 GB), and the old ZIP is about 5.5 percent smaller than the plaintext dist measured above, so the new
ZIP was expected at about 3.24 to 3.26 GB, 5 to 6 percent more than the old cut; the planner's 3.243 GB confirms it. Server DLL: no server source
changed by stage 2; the DLL in dist (2e67117c...) differs from the host's (18ad0dac...) only in the commit suffix of its informational
version (`0.3.0+cc3a5b0...` here, `0.3.0+12c3731...` on `D:\SPT41`), so the host's server needs no restart for behavior, only the file
swap the deploy plans.

**E3 Deploy** (2.2): the dry run to `D:\SPT41` (2026-10-19, no `-Confirm`) planned `90 file(s), 3105.9 MB total -- 4 new, 14 to overwrite, 72
already identical`: the 12 bundles, the client DLL and the server DLL overwrite, the four `keys/*.json` are NEW. The real deploy has NOT been
done (user's step: EFT closed first; then `-Confirm`, wait for Syncthing, check the loaded-plugin MVID `<pending>`).

**E4 Plaintext baseline** (2.3, before the deploy): three raids per map on the current build, NO BOTS.

| Map | Cold raidStarted (s) | Warm 1 | Warm 2 | `uibridge_status` max frame (ms) | `[NavMesh]` line |
|---|---|---|---|---|---|
| Bowl | `<pending>` | `<pending>` | `<pending>` | `<pending>` | `<pending>` (12704 / 5890 on 2026-10-19) |
| Air Pit | `<pending>` | `<pending>` | `<pending>` | `<pending>` | `<pending>` (11787 / 5423 on 2026-10-19) |

**E5 GPU-2 run** (2.4; Bowl and Air Pit, a cross-map sequence, one hideout open, one WITH BOTS raid on each of the two): per first request of a
map the Debug lines `[BundleKeys] key for 'arena_bowl' derived in <= 500 ms (7 files, 41.1 MB hashed, built for Arena 0.6.1.0.48206, crypto: CNG)`,
`[Bundles] loaded '<key>' from '<path>' (<n> bytes plaintext, encrypted AMPB v1, <n> chunks)` for preset and scenes (1,006,073,696 bytes,
960 chunks), `[NavMesh] loaded navmesh bundle for 'arena_bowl' ...` for the NavMesh bundle, and at raid start the `[Bundles] stream '<file>' (raid start): ...
MiB decrypted in ... chunks, cache hits ... / misses ..., crypto ... ms` line; unchanged `[NavMesh] added navmesh for 'arena_bowl': 12704 vertices, 5890 triangles`,
acoustics 11/19, flares 4/28, no ArenaMapPort warning or error.

| Budget | Measured |
|---|---|
| raid start to `raidStarted`: baseline + 3 s cold, + 2 s warm, per map | `<pending>` |
| key derivation: at most 0.5 s per map | `<pending>` |
| `max` frame time: unchanged except one loading-screen frame of at most 0.6 s | `<pending>` |
| decrypted bytes per scenes bundle: at most 1.5 x its size | `<pending>` |
| no new `[BundleKeys]` / `[Bundles]` / `[NavMesh]` Warning or Error | `<pending>` |

The first Bowl raid also answers plan risk R2 (a roughly 1 GB scene bundle through `AssetBundle.LoadFromStream`): `<pending>`. If it fails, plan B (a
decrypted temporary file) needs the user's decision D11 and weakens the claim "no decrypted copy is written to disk" in the release notes.

**E6 Negative tests** (2.5; copies under `work/negative/`, an EFT restart after each step and after the restore, a full staging build and a
deploy dry run with `0 to overwrite` at the end):

| Test | Setup | Expected | Result |
|---|---|---|---|
| N1 wrong size | Bowl key file with the first `size` + 1 | `[BundleKeys] key for 'arena_bowl' unavailable: ...` (E4, size differs) at Bowl's first request, no Bowl raid, an Air Pit raid in the same session works | `<pending>` |
| N2 content mismatch | one hex digit of a `check` changed | the E5 line (the files have the listed sizes but do not unlock the key) | `<pending>` |
| N3 tamper | one byte flipped in chunk 0 of the Bowl NavMesh bundle, and one in a header byte | `[NavMesh] 'arena_bowl_navmesh.bundle' failed its integrity check at chunk 0`, no NavMesh; for the header byte the E7 line (not a valid map bundle) | `<pending>` |
| E1, E2 | missing and corrupt key file | unit tests only (the deploy script never deletes) | the two causes are covered by `BundleKeyResolverTests` (green in the 1440); no host run possible |

**E7 GPU-1, no Arena** (2.6): stage 1 stops everything first: the not-found lines, `Maps: none available`, no `[BundleKeys]` line, no E-line, no `crypto:`
line, no read of `keys/`, hideout opens, a vanilla raid starts; how the build reached that host `<pending>`.

**E8 Text checklist** (plan 1.4), results of the check run on 2026-10-19 against the tree as it was then; the integrator re-runs it
after the other areas finish:

- README, FORGE and the release notes state the same four things (encrypted with a key derived from your Arena files; not copy
  protection; art and audio stay plaintext; the keys are built for Arena 0.6.1.0.48206): checked by reading; the generated package
  README had the first three and lacked the fourth until H5 was done at integration (it now says `built for Arena 0.6.1.0.48206`).
- `git grep -n "as built\|plaintext bundle"` outside the plan and this file: the literal phrase "distributed as built" remains only in HANDOFF.md (the dated 2026-09-20 handoff, kept as recorded); "plaintext bundle" appears only in AGENTS.md (the new decision and the staging paragraph, both correct: plaintext exists only under `work/build/`).
- Log tags in the texts against `BundleKeyResolver` (`KeyTag` `[BundleKeys]`, `BundlesTag` `[Bundles]`, `NavMeshTag` `[NavMesh]`): AGENTS, the release notes and this file quote them.
- No hash of an Arena level file in any tracked or untracked non-ignored file (the 28 level files of the four maps hashed in memory and
  searched for, the values printed nowhere): re-run at integration over the 741 git-visible files and all 90 files of `dist/` (the 12 encrypted bundles
  and the four staged key files included; hex lower and upper case plus the 32 raw bytes, and the four content keys): 0 hits; a positive control (the
  key store file itself) hit the content key and the Bowl level hashes, so the scan works.
- AGENTS, HANDOFF, INDEX and this file agree on the commands: read against `Commands.UsageText` and the csproj `Exec` lines on 2026-10-19 (flags, `rotate-key`, the properties `ArenaMapPortCreateReleaseKeys` and `ArenaMapPortArena`); re-read at integration after the create-key change (AGENTS now says the first stage needs `-p:ArenaMapPortCreateReleaseKeys=true`).

## Open decisions (plan section 4; recommendation first)

D5 resolve keys lazily on first use (chosen) or prefetch when the gate completes; revisit if GPU-2 shows a hitch above 0.6 s. D7 the server
does not check keys, so after an Arena update a map stays listed and fails at load; a later increment lets the server resolve the same key and
not register the map. D9 who backs up `work/release-keys` and the rotation policy. D10 whether a keys update ships as a separate small ZIP.
D11 plan B for R2 (decrypted temporary file) acceptable or not. D15 whether art and audio get a stage 3.

## Integration (2026-10-19, 22:50 to 23:20, build VM, integrator)

Tree: `main` at `cc3a5b0` plus the uncommitted work of the CORE, TOOL, CLIENT and DOCS areas (nothing committed, nothing under `work/` or
`dist/` tracked). Arena 0.6.1.0.48206 at `D:\Battlestate Games\Escape from Tarkov Arena`, found through the launcher's registry entry.
Client DLL in dist: 906,240 B, SHA-256 `9567b631...`; server DLL 103,424 B, `2e67117c...` (both `0.3.0+cc3a5b0`). Nothing in this section ran
in EFT.

### What the integrator changed

- H1: `tools/validate/validate_port.py --distribution` compares each staged bundle with its `work/build` output through
  `bundle_crypt.verify` (the container is deterministic); sidecar and `base.json` keep the SHA-256 comparison.
- H2, H3: the dated block on top of `docs/dist-manifest.md` and the "Stage 2" pointer in `arena-install-check.md`. H4: the ZIP estimate in the plan
  (line 284) and a new section 5, "As built", that maps the plan's names, tags and flags to the code. H5: `KEYS_BUILT_FOR` in
  `make_release_zips.py` puts the Arena build into the generated README, and `RealRepoTests.test_the_arena_build_of_the_keys_is_named_the_same_everywhere`
  ties it to `tools/bundle/key-sources.json`, README, FORGE and the release notes.
- H6 (review finding, `should-fix`): the staging target used to pass `--create-key` on every build, so a lost or missing `work/release-keys` became a
  new content key with a one-line `CREATED` notice, and every bundle and key file differed from what had been released. It now passes
  `--create-key` only with `-p:ArenaMapPortCreateReleaseKeys=true` (the first stage of a map); otherwise the tool refuses, and the build says
  where the keys come from. `BuildWiringTests.test_staging_creates_a_content_key_only_on_request` pins it; AGENTS.md says so.
- AGENTS.md: the staging paragraph (create-key, `validate_port`), no other rule changed. `HANDOFF.md` and `INDEX.md`: the stage 2 block now states
  the verified state. This file: status, state columns, H1 to H6, E1 to E3 and E8.

### Evidence (quoted from the runs)

**Tool build** (`dotnet build tools/bundle/ArenaMapPort.BundleCrypt/ArenaMapPort.BundleCrypt.csproj -c Release`): `0 Warnung(en)`, `0 Fehler`.

**The create-key guard** (a full client Release build without the property, key store absent): `FAIL there is no content key for 'arena_equator' in
D:\ArenaMapPort\ArenaMapPort.Client\..\work\release-keys (the first build of a map passes --create-key; a key store that has gone missing must be
restored from its backup, not recreated)`; the build failed with MSB3073 and `work/release-keys` still did not exist.

**First full staging** (`dotnet build ArenaMapPort.Client/ArenaMapPort.Client.csproj -c Release -p:ArenaMapPortCreateReleaseKeys=true`, about 22:56,
`0 Warnung(en)`, `0 Fehler`, 17.9 s), quoted:

```
CREATED D:\ArenaMapPort\ArenaMapPort.Client\..\work\release-keys\arena_equator.key (the content key of arena_equator): BACK UP the key store folder now and never commit or ship it; ...
  arena_equator_scenes.bundle: 431,645,444 B plaintext, 431,658,724 B encrypted, 412 chunks, written, 1331 ms, crypto: CNG
  arena_equator_navmesh.bundle: 72,968 B plaintext, 73,096 B encrypted, 1 chunks, written, 3 ms, crypto: CNG
  arena_equator_preset.bundle: 6,891 B plaintext, 7,019 B encrypted, 1 chunks, written, 3 ms, crypto: CNG
OK stage arena_equator: 3 bundles encrypted (3 written), key file keys/arena_equator.json: new wrap for Arena 0.6.1.0.48206, 1 wrap(s) in the file (written), 5 level files 21,817,368 B hashed, self-check unlocks with wrap 0, 69 ms, crypto: CNG
OK stage arena_bay5: 3 bundles encrypted (3 written), key file keys/arena_bay5.json: new wrap for Arena 0.6.1.0.48206, 1 wrap(s) in the file (written), 9 level files 31,784,868 B hashed, self-check unlocks with wrap 0, 107 ms, crypto: CNG
OK stage arena_airpit: 3 bundles encrypted (3 written), key file keys/arena_airpit.json: new wrap for Arena 0.6.1.0.48206, 1 wrap(s) in the file (written), 7 level files 38,725,724 B hashed, self-check unlocks with wrap 0, 156 ms, crypto: CNG
OK stage arena_bowl: 3 bundles encrypted (3 written), key file keys/arena_bowl.json: new wrap for Arena 0.6.1.0.48206, 1 wrap(s) in the file (written), 7 level files 41,126,600 B hashed, self-check unlocks with wrap 0, 140 ms, crypto: CNG
```

(`arena_bay5.key`, `arena_airpit.key` and `arena_bowl.key` printed the same `CREATED` line; the Bowl scenes bundle took 2986 ms to encrypt, Air Pit's 2856 ms,
Bay5's 2355 ms.) The key store was copied at once to `C:\Users\Administrator\ArenaMapPort-release-keys-backup\20261019-first-stage\` (four files, byte-identical
copies, checked by SHA-256). That is a copy on the same machine; an off-machine copy is still the user's decision D9.

**Second full staging** (same command without the property): every `stage` line ends `unchanged` and every `OK stage` line reads `3 bundles encrypted
(0 written), key file keys/<map>.json: wrap present for Arena 0.6.1.0.48206, 1 wrap(s) in the file (unchanged)`, for example
`OK stage arena_bowl: 3 bundles encrypted (0 written), ... (unchanged), 7 level files 41,126,600 B hashed, self-check unlocks with wrap 0, 112 ms, crypto: CNG`.
SHA-256, size and modification time of all 12 bundles and 4 key files were identical to the first run (`diff` of the two listings: empty).
A DllOnly build (`-p:ArenaMapPortDllOnly=true`) printed no `stage` line and left the same 16 files untouched.

**Server build:** `0 Warnung(en)`, `0 Fehler`. **dotnet test** (`ArenaMapPort.Server.Tests -c Release`):
`Bestanden!   : Fehler:     0, erfolgreich:  1440, übersprungen:     0, gesamt:  1440, Dauer: 10 s - ArenaMapPort.Server.Tests.dll (net10.0)`
(the CORE area's run, before the client's tests were linked, read 1368; the real-data tests ran against this VM's Arena and the staged key files).

**Python:** `python -m unittest discover -s tools/tests -p "test_*.py"` -> `Ran 1893 tests in 295.695s`, `OK (skipped=55)`
(baseline 1818 tests, 55 skips; the preceding run, before H5 and H6 added two tests, read `Ran 1891 tests in 289.205s`, `OK (skipped=55)`);
the log line `real dist: 90 files, 0 unclassified, parts core=5, equator=21, bay5=26, airpit=18, bowl=20` comes from the packer's real-dist smoke test.

**validate_distribution:** `python tools/validate/validate_distribution.py` -> `[validate_bundle_names] OK -- 12 bundle(s), every internal directory name is unique`
and `DISTRIBUTION_OK 4 maps 12 bundles 4 sidecars encrypted` (20 s).

**The dist bundles are encrypted** (`inspect --verify-chunks` with the key store, for each of the 12; the on-disk first four bytes `AMPB`, the decrypted first
bytes `UnityFS`; a scan of all 90 files of `dist/` found none that starts with `UnityFS`). One of the twelve lines:
`OK inspect arena_bowl_scenes.bundle: AMPB v1 arena_bowl, 1,006,073,696 B plaintext in 960 chunks, mac verified, chunks verified`.
The tool's `verify` for all 12 against `work/build` (each staged file is exactly the encryption of its plaintext), one line:
`OK verify arena_bowl: arena_bowl_scenes.bundle is exactly the encryption of arena_bowl_scenes.bundle (1,006,073,696 B, 2710 ms, crypto: CNG)`;
and `verify-keys` for the 4 key files (the player's derivation against this machine's Arena): `OK verify-keys arena_bowl: wrap 0 (built for 0.6.1.0.48206)
unlocks the store's content key against Arena 0.6.1.0.48206 at D:\Battlestate Games\Escape from Tarkov Arena (7 files, 41,126,600 B hashed, 86 ms, crypto: CNG)`.

**validate_port --distribution (H1):** `python tools/validate/validate_port.py --map bowl --distribution --package work/extract/bowl-native-v2` ->
`PORT_VALIDATED {"distribution": true, "map_key": "arena_bowl", ... "scene_bundle_bytes": 1006073696 ...}`. For the other three maps the command stops
before the distribution step, on checks older than stage 2 (`airpit`: `source plan digest drift`; `bay5`: `generic source-plan package must use the fixed
extract_map.py entrypoint`; `equator`: `Missing build output: ...work\build\equator\arena_equator_scenes.bundle`, Equator's build lives in `work/build/full`).

**Secrets scan:** the SHA-256 of the 28 level files (4 maps), the four content keys, each as hex (lower and upper case) and as the 32 raw bytes, searched in the
741 git-visible files (tracked and untracked, ignored excluded) and all 90 files of `dist/`: `TOTAL HITS: 0`; the positive control (the key store file) hit.

**bench** (`bench --provider cng|managed --size-mib 64`, .NET 10, this VM; not Mono): CNG `aes-ctr 1716, sha256 1356, hmac-sha256 1699` MiB/s, managed
`aes-ctr 2376, sha256 1355, hmac-sha256 1307` MiB/s: both OS-backed here, so the table says nothing about Mono. CORE's end-to-end figure for Bowl's real scenes bundle:
chunk verification 1.00 s, sequential decrypt plus SHA-256 2.37 s (405 MiB/s), encrypt 2.8 to 3.1 s, container overhead 30,816 B.

**Packer plan:** `python tools/release/make_release_zips.py --plan --single --allow-dirty` -> `dist files 90 (+4 generated)  raw 3,256,734,280 B (3,105.9 MiB)  estimated zip
3,242,902,036 B (3,092.7 MiB)`, `estimated single ZIP: 3.02 GiB`; the four `*_scenes.bundle` files are `STORED`. No ZIP was written.

**Deploy dry run** (`pwsh -NoProfile -File tools/deploy-to-spt.ps1 -SptRoot 'D:\SPT41'`, no `-Confirm`): `[deploy-to-spt] plan: 90 file(s), 3105.9 MB total -- 4 new, 14 to
overwrite, 72 already identical` and `DRY RUN (default) -- no files were copied, nothing under D:\SPT41 was touched.` The 14 are the client DLL, the server DLL and the twelve
bundles; the 4 new are `keys/arena_{airpit,bay5,bowl,equator}.json`. The server DLL differs from the host's only in the commit suffix of its informational version
(`0.3.0+cc3a5b0...` against `0.3.0+12c3731...`); no server source changed in stage 2. `park-unreleased-dist.ps1` and `deploy-to-spt.ps1` needed no change, as the plan said.

**git:** `git status --porcelain` lists only tracked source, test and doc files and the new, untracked files (`ArenaMapPort.Crypto/`, `tools/bundle/ArenaMapPort.BundleCrypt/`
without `bin/` and `obj/`, `bundle_crypt.py`, `key-sources.json`, the new tests, `docs/PLAN-ENCRYPTION.md`, this file); `git ls-files work dist` is empty.

### Sizes before and after encryption (bytes)

| Bundle | Plaintext | Encrypted | Container overhead |
|---|---:|---:|---:|
| arena_equator_scenes | 431,645,444 | 431,658,724 | 13,280 (412 chunks) |
| arena_equator_navmesh | 72,968 | 73,096 | 128 |
| arena_equator_preset | 6,891 | 7,019 | 128 |
| arena_bay5_scenes | 785,870,418 | 785,894,514 | 24,096 (750 chunks) |
| arena_bay5_navmesh | 189,450 | 189,578 | 128 |
| arena_bay5_preset | 7,107 | 7,235 | 128 |
| arena_airpit_scenes | 991,967,415 | 991,997,815 | 30,400 (947 chunks) |
| arena_airpit_navmesh | 169,055 | 169,183 | 128 |
| arena_airpit_preset | 7,027 | 7,155 | 128 |
| arena_bowl_scenes | 1,006,073,696 | 1,006,104,512 | 30,816 (960 chunks) |
| arena_bowl_navmesh | 185,840 | 185,968 | 128 |
| arena_bowl_preset | 6,947 | 7,075 | 128 |
| **12 bundles** | **3,216,202,258** | **3,216,301,874** | **99,616** |

The overhead is 96 B of header and MAC (for these map keys) plus 32 B per 1 MiB chunk. The four key files add 3,607 B; dist went from 86 files and 3,256,631,057 B to 90 files and
3,256,734,280 B (+103,223 B). The ZIP grows by the loss of deflate on the bundles, not by the container: see E2.

### Host checklist (what is still open; nothing below has been done)

1. **Baseline first (E4).** The deploy replaces the plaintext bundles, so record the plaintext baseline on GPU-2 before it: restart EFT with `gpu-run restart --host host2`,
   three NO BOTS raids on Bowl and three on Air Pit (the first after the restart is cold), `uibridge_wait raidStarted=true` wall time, `uibridge_status` `max` frame time, the
   `[NavMesh] added navmesh for ...` line (Bowl 12704 / 5890, Air Pit 11787 / 5423 expected).
2. **Deploy.** Close EFT on the synchronized host (the first encrypted deploy moves about 3.2 GB; a half-synced mix of plaintext and encrypted files fails with E7 or E1). Run the dry run
   again (expect `4 new, 14 to overwrite, 72 already identical`), then `-Confirm`; wait for Syncthing, then check on the host that every `bundles/*.bundle` starts with `AMPB` and
   `keys/` holds four files. The server DLL is swapped only once its process is closed; restart the host server afterwards.
3. **GPU-2 run (E5, plan 2.4).** `gpu-run restart --host host2`, `uibridge_status` (loaded client DLL MVID equals the VM build), then Bowl and Air Pit raids (NO BOTS for timing), the cross-map
   sequence Bowl, menu, Air Pit, Equator in one session, one hideout open, one WITH BOTS raid on each new map. In `uibridge_log` (source `maschine-ArenaMapPort`) expect, per map and first
   request, `[BundleKeys] key for '<map>' derived in N ms (... crypto: CNG)` (Debug, so enable Debug in BepInEx's log filter), `[Bundles] loaded '<key>' ... encrypted AMPB v1, N chunks`, at raid
   start `[Bundles] stream '<file>' (raid start): X MiB decrypted in N chunks, cache hits ... crypto N ms`, and the unchanged `[NavMesh] added navmesh` counts. Budget (plan 2.4):
   raid start at most baseline + 3 s cold and + 2 s warm, key derivation at most 0.5 s per map, `max` frame time unchanged except one loading-screen frame of at most 0.6 s, decrypted bytes at
   most 1.5 times the scenes bundle, no new Warning or Error from `[BundleKeys]`, `[Bundles]`, `[NavMesh]`. The first Bowl raid answers risk R2. If a `[Bundles] Windows CNG unavailable` Warning
   appears, record why and how long the load took (managed fallback). Also check `uibridge_status`'s `max` and ModProfiler (AGENTS, "Scene-wide searches" trap) before accepting the DLL.
4. **Negative tests (E6, plan 2.5)**, tampered copies under `work/negative/`, an EFT restart after each step and after the restore: N1 (first `size` + 1), N2 (one hex digit of a `check`), N3 (one
   byte in chunk 0 of the Bowl NavMesh bundle, and one in a header byte). Restore by a full staging build (without the property: it must say `unchanged`), then a deploy dry run that reports
   `0 to overwrite`.
5. **GPU-1, no Arena (E7).** Stage 1 must stop everything first: not-found lines, `Maps: none available`, no `[BundleKeys]` line, no `crypto:` line, hideout opens, a vanilla raid starts.
6. **After the runs:** fill E4 to E7 and the host lines of `docs/releases/v0.3.0.md` (key derivation ms, the added load time, the ZIP size), then the user's steps: commit, the re-cut
   (`make_release_zips.py --single --extract-check`, the real ZIP size and SHA-256 into the Download block), upload, VirusTotal, tag, Forge. The DLLs embed the commit in their informational
   version, so a build after the commit differs from the one deployed for the host run in that suffix only.

### Open risks

- **R2 is untested in EFT:** `AssetBundle.LoadFromStream` on a roughly 1 GB LZ4HC scene bundle through a managed stream, with reads from Unity's loading thread, has run nowhere but in unit tests with
  `AmpbReadStream`. If it fails, plan B (a decrypted temporary file) needs the user's decision D11 and weakens the claim that no decrypted copy is written to disk.
- **Mono P/Invoke and speed:** the CNG path (`bcrypt.dll`) is proven on .NET 10 and by known-answer self-test, but EFT's Mono marshalling is first exercised on the host; the managed fallback is slow on
  Mono (plan R4) and logs one Warning. Throughput on Mono is not measured.
- **Key store:** `work/release-keys` is the only copy of the four content keys apart from the same-machine backup above; losing both means `rotate-key`, a re-encrypt and a re-release, and every released
  bundle of that map stops opening (D9). The staging guard (H6) is the build-side protection against a silent re-creation.
- **Level index table:** `tools/bundle/key-sources.json` takes the level indices from the accepted packages' manifests and extractor plans (extracted from Arena 0.5.4 and 0.6.0) and pins them against those; the
  file sizes match the plan's table on Arena 0.6.1.0.48206, but the index-to-scene mapping of 0.6.1 itself was not re-derived from its build settings (the plan's TOOL-9, `resolve_key_sources.py`, does not exist). This
  does not affect function or security (any fixed set of files unlocks the key), only which files a player's installation must match.
- **Arena builds:** the keys are built for Arena 0.6.1.0.48206 only. A player with other level files for a map gets no raid on that map (E4 or E5 in the log) until a package adds a wrap (D10); the server does not
  check keys (D7), so the map stays listed.
- **Not copy protection** (every text says so). The key is tied to files, not to a purchase; any owner can derive it or decrypt the bundles with the public tool; art, audio and derived data stay plaintext (D15).
- **Other maps' `validate_port.py --distribution`:** the command cannot be run for Air Pit, Bay5 and Equator for the reasons above, so H1 is proven on Bowl only; the code path is the same for all four, and the full `verify`
  of all twelve bundles ran through the tool directly.
- **ZIP size:** the packer's estimate is 3.02 GiB; Pixeldrain takes it (no size cap), GitHub's 2 GiB asset cap was already out of reach for four maps (R7). The real figure comes from the cut.
- **Commit-dependent bytes:** both DLLs carry the commit id; committing this tree changes their hash (not their behavior), so the host-verified DLL and the released DLL differ in that suffix unless the release is
  built from the verified commit.
