# Current distribution contract: Equator, Bay5, AirPit and Bowl (0.3.0, encrypted bundles, published 2026-10-24)

2026-10-24 (release 0.3.0 PUBLISHED, user decision "ja, gerne veröffentlichen. 0.2.3 wird später gelöscht"): the cut below went to Pixeldrain
(https://pixeldrain.com/l/UtjWQemL; ZIP https://pixeldrain.com/u/J1ajrRA8, checksum https://pixeldrain.com/u/RTaSMHNm), both DLLs had been scanned on VirusTotal (0 malicious of 70 and 71
verdicts, 75 engines listed), tag `v0.3.0` and the GitHub release "ArenaMapPort 0.3.0" with the `.sha256` asset were created. 0.2.3 stays downloadable until
the user deletes it; the Forge entry (FORGE.md) is the user's.

2026-10-24 (the 0.3.0 cut, `make_release_zips.py --single --force --extract-check`, commit `67b75b1`): dist holds 90 files,
3,661,721,167 B (3,492.1 MiB); the single ZIP `maschine-ArenaMapPort-0.3.0.zip` is 3,647,749,962 B (3.40 GiB), SHA-256
`9ed79ac488a9cd1659199fb87244e9df203ba3b6c284c87614a007012c9b559b`, 94 entries (the 90 dist files stored or deflated, plus README.txt, the two
THIRD-PARTY-NOTICES.txt copies (client and server folder) and the manifest `parts/full.json`; the `.sha256` file sits beside the ZIP); `extract-check OK: 90 files identical to dist`. Client DLL 1,083,904 B
sha256 `8434651566a968a0d41bbb3a6b6bd239fb03557cfacbc36ac164b1295cd943a3`, server DLL 146,432 B sha256 `f28b03ae39aac81910a1b5d25ba90a46c04283ad8d62b53325c05f54a0d415c5` (the build of the a673568 tree at 14:10, five minutes before that
commit, so the DLLs' informational version reads `0.3.0+e652fc9`; the done signal for mode plugins is in them, host-verified on GPU-2 the same
afternoon). The Equator, Bay5 and AirPit scenes and NavMesh bundles are the 2026-10-24
rebakes (door poses; Bay5 roller (3) shut again); Bowl's bundles are unchanged since 2026-10-20. The four DynamicMaps overviews are
the 2026-10-24 refresh (e652fc9). The twelve bundles verify against `work/build`; the four key files are unchanged since 2026-10-20
(Bowl's restored 2026-10-22). The 2026-10-18 candidate cut is deleted (superseded by this one).

2026-10-20 (key source v2, [PLAN-ENCRYPTION.md](PLAN-ENCRYPTION.md) section 6, record
[progress/bundle-encryption.md](progress/bundle-encryption.md) "Key source v2"): the four `keys/<key>.json` are schema 2 now (one wrap each,
`built_for` `0.6.1.0.48206`, `sample` `{block_size 65536, blocks 16}`, the paths and sizes of the map's `sharedassets<N>.assets.resS` /
`.resource` streams: 3, 8, 7 and 8 files; 719, 1,185, 1,094 and 1,182 B). The twelve bundles are byte-identical to the 2026-10-20 LZ4
staging (same content keys). `DISTRIBUTION_OK 4 maps 12 bundles 4 sidecars encrypted`; `check_key_file_schema` requires schema 2, the
`sample` object and the resource-stream names in ascending order. dist: 90 files.

2026-10-19 (stage 2 of the Arena gate, integration build on `cc3a5b0` plus the uncommitted stage 2 tree; design
[PLAN-ENCRYPTION.md](PLAN-ENCRYPTION.md), record [progress/bundle-encryption.md](progress/bundle-encryption.md), "Integration"):
every bundle in dist is now an `AMPB` container and every map carries a key file. Per map the plugin folder holds
`bundles/<key>_{scenes,navmesh,preset}.bundle` (AMPB: magic, version, map key, 96 B of header and MAC for these map keys plus 32 B per 1 MiB chunk, AES-256-CTR) and
`keys/<key>.json` (schema 1, `built_for` `0.6.1.0.48206`, the paths and sizes of the Arena level files the map is keyed from, a salt,
the wrapped content key and its check value; never a hash of an Arena file). dist holds **90 files, 3,256,734,280 B (3,105.9 MiB)**:
the 86 files of the plaintext dist (3,256,631,057 B) with the twelve bundles replaced (3,216,202,258 B plaintext, 3,216,301,874 B
encrypted, +99,616 B; Bowl's scenes bundle 1,006,073,696 B becomes 1,006,104,512 B in 960 chunks) and four new files, 3,607 B together.
The shipped file set per map is core 5, equator 21, bay5 26, airpit 18, bowl 20 (the key file is the new one in each map part). The
staging build writes them with the `BundleCrypt` tool (`ArenaMapPortStageWpOutputs`, one `stage` per map); the output is
deterministic: a second full build left all 16 bundle and key files byte-identical and untouched. `python
tools/validate/validate_distribution.py` ends with **`DISTRIBUTION_OK 4 maps 12 bundles 4 sidecars encrypted`**, which now also
means: every bundle starts with `AMPB`, its header MAC and every chunk hash verify (`inspect --verify-chunks`), the header names the
map of the file name, the decrypted prefix is `UnityFS`, `keys/` holds exactly one `<key>.json` per released map and each unlocks the
key store's content key against this machine's Arena (`verify-keys`), and no file anywhere under `dist/` starts with `UnityFS`. A
dist with a plaintext bundle or a missing key file is refused by the validator and by `make_release_zips.py` (which also names the Arena build of
the keys in the generated README). The estimated single ZIP is 3,242,902,036 B (3.02 GiB; the bundles are STORED, ciphertext does not
compress), 164.9 MB or 5.4 percent above the 2026-10-18 plaintext cut. A deploy dry run to `D:\SPT41` planned 90 files, 4 new (the key files),
14 to overwrite (the twelve bundles and both DLLs) and 72 identical. The 2026-10-18 cut below is superseded; no ZIP of this dist is
cut yet.

2026-10-18 (release 0.3.0 candidate cut, feature/bowl `26b9216`, NOT uploaded): `make_release_zips.py --single --extract-check`
wrote `work/releases/0.3.0/maschine-ArenaMapPort-0.3.0.zip`, 3,078,043,845 B (2.87 GiB), SHA-256
`8f9b65913501adda2206bbd60919e990e3ca312faa7db0f0a8d812a2edddeb8b`, plus its `.sha256`; extract-check: 86 files identical to
dist. Against the final 0.2.2 ZIP's manifest (`326d71b`) the dist differs in exactly three files: both DLLs (0.3.0) and
`locales/map-descriptions.json` (Bowl's text added); the other 64 files of Equator, Bay5 and Air Pit are byte-identical.
The DLLs in dist are the host-verified build of `185f8f8` (`ProductVersion 0.3.0+185f8f8...`); the two commits after it are
docs only. The paragraph below describes the same dist before the cut.

2026-10-18 (0.3.0 integration, feature/bowl, not released): dist holds **86 files, 3,256,514,833 B (3,105.7 MiB)** for the
four maps and `python tools/validate/validate_distribution.py` ends with `DISTRIBUTION_OK 4 maps 12 bundles 4 sidecars`.
This paragraph supersedes the "Pending" sentence and the "Bowl has no flares ... or DynamicMaps overview" sentence of the
paragraph below. Bowl's bundles are staged from the accepted U1 build (`arena_bowl_scenes.bundle` 1,006,073,696 B,
`arena_bowl_navmesh.bundle` 185,840 B, `arena_bowl_preset.bundle` 6,947 B, `material-shaders.arena_bowl.json` 62,154 B,
`ai/bowl.ai.json` 17,413 B). New in dist since that paragraph, five files and 1,676,831 B:

- `flares/arena_bowl/flares.json` 149,472 B (SHA-256 `6090e9e4...b3fc2a2`) and `flares/arena_bowl/textures/`
  `resources.assets_2969.bin` 262,144 B + `sharedassets4.assets_11.bin` 1,048,576 B (together 1,460,192 B). The package comes from
  `tools/extract/extract_multiflare.py --map arena_bowl` (28 source lights, 4 active, 130 elements, 20 active); the two textures
  are the same bytes as Equator's, the noise texture just has the Arena 0.6.0 id. Bowl will draw no flare with the current rule: the
  4 active lights are 771 to 869 m from the play space and the renderer culls beyond 500 m
  ([native-multiflare.md](progress/native-multiflare.md)). It is staged anyway because `NativeMultiFlare.Install` binds the Bowl
  key and `StartFlares` reads the file; without it every Bowl raid would log "disabled after error".
- `dynamicmaps/ArenaMapPort_Bowl/ArenaMapPort_Bowl.jsonc` 1,671 B and `Layers/Ground.svg` 214,968 B (216,639 B together,
  hash-identical to `work/dynamicmaps/release/ArenaMapPort_Bowl`; [bowl-dynamicmaps.md](progress/bowl-dynamicmaps.md)).
  `DynamicMapAssets.Maps` now lists Bowl, the client csproj has the two overview rows, `OVERVIEW_MAP_NAMES` is all four maps.
  Not yet opened in game.

The validator's optional-payload lists are now `FLARE_MAP_KEYS = (arena_equator, arena_bay5, arena_bowl)` and
`OVERVIEW_MAP_NAMES = (Equator, Bay5, AirPit, Bowl)`; AirPit still has no flare package and only Bay5 has a water package.
The shipped file set per map (`make_release_zips.py`, real-dist smoke test): core 5, equator 20, bay5 25, airpit 17, bowl 19 files;
`make_release_zips.py --plan` reads the `<Version>` of both csproj files, now 0.3.0. Both DLLs are 0.3.0: client 819,200 B (SHA-256
`8AFF8B2D...3855F3`), server 74,240 B (`7662D8BB...DAE473`). Size: the estimated single ZIP is 3,148,925,260 B (2.93 GiB), against
2.00 GiB for 0.2.2 (Bowl's scenes bundle alone is 1.0 GB); the multi-part layout would be five parts, the largest 964,531,607 B
(Bowl), all far below the 2 GiB cap. A dry-run deploy to `D:\SPT41` (no `-Confirm`) planned 86 files, 5 new (exactly the files
above), 2 to overwrite (the two DLLs) and 79 identical. The package is not cut: the first host raid of Bowl with these DLLs,
the MAP check in DynamicMaps and the release texts of README, FORGE and `docs/releases/v0.3.0.md` (they still say that Bowl has
no overview) come first.

2026-10-18 (feature/bowl, PLAN-BOWL item 8, not released): Bowl is the fourth map of `ArenaMapRegistry.All`, so every list that
names the release counts four maps: 12 bundles (three per map), 4 shader sidecars (`material-shaders.arena_<key>.json`), 4
server databases (`db/<key>/base.json`), 4 AI sidecars (`ai/<stem>.ai.json`), 4 acoustic packages, 4 banners and both DLLs,
i.e. `DISTRIBUTION_OK 4 maps 12 bundles 4 sidecars` once it can pass. Bowl's own files: `db/arena_bowl/base.json`,
`ai/bowl.ai.json`, `acoustics/arena_bowl/` (`spatial-audio.json` 156,032 B, `source.audiobakedata` 7,039 B and the one room-tone
WAV `sharedassets111.assets_40.wav` 2,800,324 B; 14 rooms, 25 portals, 24 areas, 55 room pairs, 196 routes), `banners/arena_bowl.jpg`
(`Matchmaker_Banner_TheBowl`, 769 x 460, 219,073 B), FOUR loading images `loadscreens/arena_bowl_1..4.jpg` (the
`Arena_MainMenu_Paralax_Bowl-1..4` backgrounds, 2048 x 1024; Arena ships no loading screen for Bowl, so there is no fifth
file and the count is table-driven by `extract_loadscreens.LOADSCREENS`) and its entry in `locales/map-descriptions.json`
(`62eba95b7ba21c1d534d305a Description`, en/ge/ru). Bowl has no flares, water package or DynamicMaps overview (PLAN-BOWL item 17,
polish). **Pending:** the three Bowl bundles (`arena_bowl_scenes.bundle`, `arena_bowl_navmesh.bundle`, `arena_bowl_preset.bundle`)
and `material-shaders.arena_bowl.json` come from the U1 Unity build (`work/build/bowl`), which was still running when this
promotion was made; until a non-DllOnly client build stages them, `validate_distribution.py` and the real-dist smoke test of
`test_release_zips.py` fail or skip on the missing Bowl files, and 0.2.2 stays the released three-map package. The park script no
longer archives Bowl's outputs and `tools/build_remaining.ps1` refuses the map (it would overwrite `work/build/bowl` with the
0.5.4 spike package).

2026-10-19 (release 0.2.3, branch `release/0.2.3`, not cut yet): the distribution is unchanged: 67 files, 2137.4 MB,
`DISTRIBUTION_OK 3 maps 9 bundles 3 sidecars`. A full Release client build restaged every bundle and sidecar byte-identical
(SHA-256 before and after); only the two DLLs differ from the dist as it stood before this build: client `cabc918b...8e055d`
(844,800 B) and server `b0e2831f...edf0e2` (101,376 B), both FileVersion 0.2.3.0. They carry the Arena
installation check ([arena-install-check.md](progress/arena-install-check.md)): no new file kind and no BSG-derived data in
the check; it reads the user's own Arena installation at runtime. The deploy dry run against `D:\SPT41` (0.3.0 four-map build)
planned 0 new, 3 overwrites (two DLLs and `locales/map-descriptions.json`, whose host copy also holds Bowl's descriptions)
and 64 identical files; Bowl's files on the host are not in this dist and stay untouched. `ProductVersion` still names
`a3188cd` (uncommitted tree); the release cut rebuilds from its commit.

2026-10-17 (release 0.2.2, final cut): `maschine-ArenaMapPort-0.2.2.zip` is cut again from this dist (AirPit's U8 bundles
with the crates, the redrawn AirPit DynamicMaps overview, the 0.2.2 DLLs): 2,149,195,967 B, SHA-256
`d4790b66...5e7944`, built from `326d71b`, 67 files identical to dist after extraction; uploaded to Pixeldrain
(https://pixeldrain.com/l/rKL2Pt2f), both DLLs 0/75 on VirusTotal, tag `v0.2.2` and GitHub release. The first
cut (`1f7ce05`, U7 bundles, SHA `734ce122...`) was replaced before any upload.

2026-10-17 (later): dist carries AirPit's U8 bundles (package v9, the three crates under the tarp; scenes 991,967,415 B,
navmesh 169,055 B) and the 0.2.2 DLLs; the first 0.2.2 cut (`1f7ce05`) still held the U7 bundles (replaced, see above).

2026-10-17 (release 0.2.2, first cut `1f7ce05`, replaced by the final cut above): three maps, nine bundles (AirPit's
scenes and NavMesh were the U7 rebake), three sidecars
(`material-shaders.arena_<key>.json` for every map) and both DLLs (client: SpawnGates with held gates, server: version
string and the Shared descriptor), packaged as `maschine-ArenaMapPort-0.2.2.zip` with its `.sha256` file.

2026-10-17: AirPit's scenes and NavMesh bundles are the U7 rebake (bake box z 83 again, the passport control out of
the bake as a held gate; scenes 991,961,232 B, navmesh 168,826 B); dist and both installs carry them, the published 0.2.1
package still has the U6 mesh (z 70). The next package carries U7 and the SpawnGates client.

2026-10-17: Equator's shader sidecar is staged as `material-shaders.arena_equator.json`, the same rule as every other
map (`ShaderRebind.SidecarFileName`, csproj `ArenaMapPortStageWpOutputs`, `make_release_zips.py`, `validate_distribution.py`).
The pre-generalization name `material-shaders.json` is retired before the first Forge upload; ordinary deployment never deletes,
so an install that still holds the old file is cleaned by the explicit `-RemoveLegacyShaderSidecar` deploy mode (backs the file
up under `work/deployment-backups`, removes it only after the renamed file and the client DLL match dist). The published 0.2.1
package (built from `d26b7fd`) still carries the old name; the next package carries the new one.

2026-10-16 (release 0.2.1): same three-map list and bundle set as 0.2.0; only the two DLLs are rebuilt (client:
`ScopeMaskRepairs`, the sharp sight picture inside optics; server: version string only), packaged as
`maschine-ArenaMapPort-0.2.1.zip` with its `.sha256` file.

2026-10-16 (release 0.2.0): the release list is three maps. `ArenaMapRegistry.All` is Equator, Bay5 and AirPit; the
distribution is nine bundles (three per map), three shader sidecars, three server databases (`db/<key>/base.json`) and both
DLLs (`DISTRIBUTION_OK 3 maps 9 bundles 3 sidecars`), in the single-ZIP form (`make_release_zips.py --single`,
`maschine-ArenaMapPort-0.2.0.zip` with its `.sha256` file). The earlier two-map baseline below describes release 0.1.0 (six
bundles, two sidecars, two databases) and is kept as history; its map list is no longer current. The 2026-10-11 follow-up
stays in force for 0.2.0 apart from its two-map wording: the optional `arena-audio` and `arena-ui` exports remain excluded
and every database is `db/<key>/base.json`.

2026-10-11 follow-up: optional `arena-audio` and `arena-ui` exports are excluded from
the release because neither current ArenaMapPort nor ArenaMode consumes them.
Builds archive previously staged copies under `work/release-parked`, preserving
extraction inputs. Both databases now use `db/<key>/base.json`, including Equator.
After a full deployment, the explicit `-RemoveLegacyEquatorDb` deployment mode
backs up and removes only the former loose database after validating its replacement.
See [release cleanup](progress/release-cleanup.md) for the revised test package.

## Earlier preparation baseline

2026-10-10: `ArenaMapRegistry.All` is the two-map runtime/release list; `Catalog`
retains all fourteen development descriptors. Normal Release builds stage exactly
six bundles, two shader sidecars, two server databases and both DLLs. Existing
parked outputs are reversibly archived under ignored `work/release-parked/`.
Unknown output files fail exact-set validation. Nothing is removed from live installs;
old files there are inert because the server and client no longer register their maps.

Equator stages from `work/build/full` and Bay5 from `work/build/bay5`, both with
presets from `work/build/preset`. Accepted v5 source packages remain unchanged.
Acoustics, room tones, original flares, water data, loading screens and banners are
included for these two maps. Optional ArenaMode audio/UI payloads remain included
when their extraction manifests exist. No development/debug plugin or user config
is packaged. `AdditionalMapBuilds.props` is retained as development metadata and
is no longer imported by the client build. `build_remaining.ps1 -Stage` is refused;
its extraction/build workflow remains available without that switch.

Run `python tools/validate/validate_distribution.py` after both Release builds.
See [first-release preparation](progress/first-release-prep.md) for the current
artifact inventory, tests, native evidence and remaining publication work.

---

# Historical distribution contract: 14 Arena locations

Verified: 2026-09-15, 20:34 CEST. **All twelve additions have current schema-2 full-map receipts and passed staged validation.** Client/server Release outputs are staged, and the exact 14-map distribution and both DLL hash pairs passed verification. The server Release run passed all 228 tests; the client completed with zero warnings/errors. The deployment dry run passed without copying files into SPT. The per-map results and scope are maintained in [remaining-maps.md](progress/remaining-maps.md).

The earlier Equator-only distribution log remains unchanged at [progress/dist-manifest.md](progress/dist-manifest.md); Bay5's result remains at [progress/bay5-port.md](progress/bay5-port.md). Their older artifact totals are historical.

## Verified distribution layout

```text
dist/
  BepInEx/plugins/maschine-ArenaMapPort/
    maschine-ArenaMapPort.Client.dll
    material-shaders.arena_<stem>.json           # one per map (Equator's compatibility name retired 2026-10-17)
    bundles/
      arena_<stem>_preset.bundle
      arena_<stem>_scenes.bundle
      arena_<stem>_navmesh.bundle
  SPT_Runtime/user/mods/ArenaMapPort/
    maschine-ArenaMapPort.Server.dll
    db/
      arena_<stem>/base.json                     # one per map (Equator's loose base.json retired 2026-10-11)
```

The 14 stems are `equator`, `bay5`, `airpit`, `airpit2`, `autoservice`, `bowl`, `prison`, `railwaystation`, `yard`, `iceberg`, `factory`, `custom`, `shoreline`, `woods`. The verified set is **42 bundles, 14 shader sidecars, the two Release DLLs and 14 map base JSON files**: **72 files, 11,054,200,717 bytes** (about 11.05 GB). Saw, the Bay5 tournament variant, Draft and the generic test preset add no separate output set.

## Sources used for staging

| Map group | Scenes/NavMesh/sidecar source | Preset source | Sidecar destination |
|---|---|---|---|
| Equator | `work/build/full/` | `work/build/preset/arena_equator_preset.bundle` | `material-shaders.arena_equator.json` |
| Bay5 | `work/build/bay5/` | `work/build/preset/arena_bay5_preset.bundle` | `material-shaders.arena_bay5.json` |
| Each additional map | `work/build/<stem>/` | `work/build/preset/arena_<stem>_preset.bundle` | `material-shaders.arena_<stem>.json` |

The client project's existing explicit Equator/Bay5 item lists remain authoritative. The 12 additional maps are explicitly listed in `tools/maps/AdditionalMapBuilds.props`; Release staging calls `tools/validate/validate_port.py --map <stem>` before accepting their outputs. A recursive search across `work/build/` is not a valid substitute: diagnostic/S1 bundles can share filenames with the full map.

Server Release build staging copies the server DLL and its `db/` files into the repository's `dist/` tree. Server tests must also use `-c Release`, because their referenced server project stages its own build output.

## Validation contract and recorded evidence

For each additional map, `work/build/<stem>/full-map-validation.json` must have schema 2 and come from a successful `tools/build_all.ps1 -FullMap` run. It records hashes for the source plan, descriptor, extraction manifest, scene and NavMesh bundles, preset bundle, shader sidecar, editor verification, server base JSON, `unity-run.json`, the actual Unity log and `validation-inputs.json`. Generic map receipts also include `target-physics.json` (read-only SPT collision-matrix snapshot) and `spawn-placement.json`. `validate_port.py` rechecks recorded files, exact scene membership/order contracts, source plan identity, nonempty NavMesh tiles/bounds and server preset references.

`validation-inputs.json` binds acceptance to every package file and the complete consumed Arena/SPT source files, relevant code, settings and toolchain inputs. `port_inputs.py` captures them before mandatory validation, rehashes them after all checks and rehashes them again during receipt verification. Changed bytes or relevant file-set membership invalidate acceptance. Old schema-1 receipts are rejected and require the complete current validation sequence; a hash-only upgrade is not supported. The [schema-2 workflow](progress/remaining-maps.md#schema-2-input-binding-and-revalidation) describes these input groups and complete revalidation of suitable existing Unity output.

Full-map orchestration protects reference synchronization and the editor run with a named Unity mutex. It regenerates current server-base and preset outputs before validation even with `-SkipUnityBuild`. Every explicit full-map attempt archives previous receipt/snapshot files first; superseded files cannot authorize resume or staging after a failed attempt.

The full-map gates include actual built mesh-channel/index comparisons, source-empty mesh preservation and source/package/bundle checks for mixed material slots and emitted null colliders. Terrain bundles additionally require executable D3D11 vertex and pixel programs for the preserved custom shader. Final reconciliation must use the current validators and current package/bundle contents. An old receipt or successful log line does not remain valid after a package refresh.

After Release staging, run `python tools/validate/validate_port.py --map <stem> --distribution` for all 12 additions. This independently compares staged bundles, sidecars and server base JSON with their validated source artifacts. Its `PORT_VALIDATED` output is the final per-map staging evidence; a receipt alone does not prove that the staged copy matches.

Then run the complete distribution check:

```powershell
python tools/validate/validate_distribution.py
if ($LASTEXITCODE -ne 0) { throw 'Distribution file set or Release DLL hash check failed' }
```

This validator requires the exact registered bundle, sidecar and database sets, checks staged database content against the source JSON, and verifies SHA256 equality for both client/server DLLs against their actual Release build outputs. It asks MSBuild for each effective `TargetPath`, accounting for the client's different output-directory convention. A missing or stale DLL fails validation. `./tools/build_remaining.ps1 -Stage` invokes this check after all twelve per-map staged checks and before its success marker. The final check passed with `DISTRIBUTION_OK 14 maps 42 bundles 14 sidecars`, recorded by G9 in `work/logs/remaining-maps/final-gates.log`.

Equator/Bay5 use their preexisting output directories and evidence. The final baseline check passed: eight original artifacts retain their exact hashes, and six descriptor/base/extractor inputs retain their pre-expansion content. The shared DLLs include the registry expansion. The complete 42-bundle/14-sidecar set passed the check for missing and unexpected map outputs.

Final reconciliation:

| Check | Status |
|---|---|
|12 complete source extraction packages | Present; AirPit2.0 and Customs material slots refreshed |
|12 Unity builds | Complete |
|12 current schema-2 full-map receipts | Passed |
|12 `validate_port.py --distribution` passes | Passed |
|Exact 42 bundles /14 shader sidecars /14 base JSON files | Passed |
|Both built/staged Release DLL SHA256 pairs | Passed |
|Server Release and server tests | Passed: 228/228 tests, zero failures/skips |
|Client Release staging | Passed; all twelve receipts checked before copying, zero warnings/errors |
|Equator/Bay5 baseline artifact equality | Passed: eight artifacts and six descriptor/base/extractor inputs |
|Deployment dry run | Passed: 72 new files, zero overwrites, no copies performed |
|Live deployment | Not performed by this build pass |
|New-map GPU raid checks | Not performed |

The actual final Release pass used direct server `dotnet test -c Release` followed by direct client `dotnet build -c Release`, then the per-map staged checks and distribution gates. Evidence is in `work/logs/remaining-maps/final-server-release-tests.log`, `final-client-release.log` and `final-gates.log`. The local summary files `.unlazy/remaining-maps/reports/final-map-validation.json` and `final-dist-size.json` record the twelve map receipts/metrics and total staged size. The Python suite passed 86 tests; final gates additionally confirmed eight PowerShell log tests and reran the relevant BC6H/input-snapshot checks. `build_remaining.ps1 -Stage` remains the documented equivalent runner entrypoint; this pass used the direct commands.

## Deployment boundary

Builds stage only into `D:/ArenaMapPort/dist/`. The completed `pwsh tools/deploy-to-spt.ps1 -SptRoot D:/SPT41` dry run planned 72 new files, zero overwrites and zero already-identical files, and copied nothing (`work/logs/remaining-maps/final-deployment-dry-run.log`). Actual promotion remains a separate reviewed operation using the script's explicit `-Confirm` switch. A successful dry run proves the planned file mapping, not an installed mod or a playable raid.

Procedural GPUInstancer grass and the other [spike limitations](progress/remaining-maps.md#supported-scope-and-fidelity) remain excluded even when distribution validation passes. File-integrity checks do not establish full Arena visual/gameplay parity.

## Acoustic package addition — 2026-09-28

The current Equator/Bay5 native acoustic port adds six source-derived files,
bringing this workspace's staged/deployed file set from 91 to 97. Each directory
`BepInEx/plugins/maschine-ArenaMapPort/acoustics/arena_{equator,bay5}/` contains
`spatial-audio.json`, `source.audiobakedata` and one original room-tone WAV.
These files are regenerated by `tools/extract/extract_spatial_audio.py`; Release
builds stage them even with `ArenaMapPortDllOnly=true`. Existing v5 bundles,
server data and original ambient-player clips are retained. All source-derived
audio and graph files remain outside git. Final build, host and installation
evidence is in [spatial-audio.md](progress/spatial-audio.md).

## Room-dependent ambient reverb — 2026-09-28

The 97-file layout is unchanged. Both existing spatial-audio JSON files now also
contain the original 26 mixer parameters and provenance; `room_reverb.py` is part
of their existing extraction entrypoint. Scene bundles and server files are unchanged.
Final client SHA256 `963EFD5A0060DFFB77673B9C8F661F61C38620787107189C24394638F0D972D0`.
All 97 installed files independently hash-match dist in both installations after
the approved deployment. Build, actual map-bus DSP tests and scoped Equator mixer
compatibility are recorded in [room-reverb.md](progress/room-reverb.md).

2026-10-19 (release 0.2.3 candidate cut, `release/0.2.3` at `c6536e5`, NOT uploaded): `maschine-ArenaMapPort-0.2.3.zip`,
2,149,219,632 B (2.00 GiB), SHA-256 `f96ccc756cc7c56ff105861f0efcabf676edd0f09adf186fdcb8f4ddbb378090`, plus its `.sha256`;
extract-check 67 files identical to dist. Same file set as 0.2.2 with the two 0.2.3 DLLs (the Arena installation check);
everything else byte-identical to the final 0.2.2 ZIP.

2026-10-19 (release 0.2.3 PUBLISHED): the candidate ZIP above went to Pixeldrain (https://pixeldrain.com/l/9jZS5CnU), both DLLs were
scanned on VirusTotal (0/75 and 0/75 malicious), tag `v0.2.3` and the GitHub release were created,
and 0.2.2 was withdrawn (release, tag and Pixeldrain files deleted; nobody had downloaded it).

2026-10-19 (earlier releases withdrawn): after 0.2.3 went live the user had every earlier package removed: the GitHub
releases 0.2.2, 0.2.1 (with its DLL-only ZIP) and 0.1.0 (with its 1.23 GB full ZIP) including their tags, and on Pixeldrain
the 0.2.2 and 0.2.1 lists and files plus a 2026-10-15 upload-test file. The account holds only the 0.2.3 ZIP, its checksum and
the 0.2.3 list; the only GitHub release and tag is v0.2.3. The `docs/releases/*.md` records carry withdrawn banners.
