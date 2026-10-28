# Shared spectator textures and release 0.4.0 preparation

## Implementation — 2026-10-28

The user requested deduplicating the spectator textures and then preparing the next release.
All four source packages contain the same 123 PNG files, 146,689,024 bytes per set. Keeping one
set removes 440,067,072 bytes of duplicate image data; the new manifest and graph hash fields
add a small amount of metadata. The owner-extracted packages remain untouched locally.

`stage_spectators.py` validates all four schema-3 source exports, rejects same-name/different-byte
images, and stages schema-4 graphs to `spectators/<map_key>/`. Shared PNGs and a manifest go to
`spectators/shared/`. The manifest records PNG lengths/hashes; each graph pins the exact manifest
hash. Runtime verifies that chain, reading each image once for both hashing and decoding. File
names are strictly bounded. Unity textures, materials and meshes retain scene-owned lifetimes;
no global texture cache or behavior/animation change was introduced.

Every Release/DllOnly client build runs the staging step. The distribution validator compares
graphs and image bytes against the local source exports. The release classifier places shared
textures in core and each graph in its map part, rejecting old per-map PNGs. Staging may remove
only known byte-identical legacy copies from its own output after validating replacements.
Path normalization follows link checks, and staging may not overlap an input export.

Development installation cleanup is explicit: full deployment, then
`tools/deploy-to-spt.ps1 -SptRoot D:/SPT41 -RemoveLegacySpectatorTextures` (dry run) and `-Confirm`.
It first checks the new client DLL, all four graphs and every shared file against dist, and all
legacy candidates against the retained replacement bytes. Changed/unknown files refuse cleanup.
It removes individual known PNGs only, never directories or unrelated files; the original exports
and verified shared copies remain available. Repetition removes zero files. A 0.3.0 release install
has no spectator data and needs no such migration.

The chosen next version is 0.4.0, reflecting the additive features since 0.3.0. Both assemblies and
runtime version declarations agree. CHANGELOG and the new release note include all existing
unreleased changes, not only this size reduction. Publication, tags and uploads are outside this
request. Native and archive evidence follows after the candidate checks.

## Offline acceptance and package — 2026-10-28

The final code candidate is `2f8cff0` on `release/0.4.0`. The full C# Release regression,
client Release build, nine staging cases, five migration cases, release-packaging/changelog
fixtures and the new stale-DLL negative check passed. A read-only second-agent review found
no remaining defect after the graph/manifest-reference checks were added to migration.
The client build completed without warnings or errors. The distribution validator checks
both source/project version declarations and staged DLL metadata, not only byte equality
between build and staging. It rejects an old DLL even if both copies are equally stale.

The first combined check attempt failed; standalone C# and client checks and the complete
final rerun passed. An intermediate ZIP containing an old client was rejected and moved to
ignored `work/release-0.4.0/rejected-stale-client/`; it was neither deployed nor published.
The final archive gate additionally requires a successful build proof for the current
commit and both exact DLL hashes before packaging.

The final package has 255 distribution files (3,851,842,535 bytes), plus four generated
release files inside the ZIP. Full distribution validation and an extraction comparison
against every staged file passed. The local full ZIP is 3,834,882,881 bytes (3.57 GiB):
`work/releases/0.4.0/maschine-ArenaMapPort-0.4.0.zip`, SHA-256
`f8fcfb7294c6eef8467a3ef6953d02e6091d12f1f7115e0956153813b532926f`.
PNG accounting independently confirms 586,756,096 bytes before and 146,689,024 after,
with all original source exports retained. This is exactly 440,067,072 fewer PNG bytes;
new graph/manifest metadata is additional and is included in the final package figures.

The sanctioned full deployment copied the 255 staged files. Migration dry-run found 492
known identical old PNGs; confirmation removed those exact files, and a repeated dry-run
found zero. GPU-2 also has zero per-map PNGs and 124 shared files (123 images plus manifest).
Syncthing was explicitly authorized to resume and reached 100 percent with zero pending
items/deletions. Local evidence is under `work/release-0.4.0/`.

## Native acceptance blocked by host startup — 2026-10-28

`deploy-wait` first timed out while hashing the entire older development installation (2,442 files);
the exact 255-file candidate list then matched twice on GPU-2 in 40 seconds. Server startup failed
after that successful comparison: ArenaMode's `RewardOfflineSession.RecoverBeforeStartup` throws
`Offline Arena recovery is incomplete; normal startup stopped without replacing a profile.`
The server never opened port 6969; EFT remained closed. This is not an ArenaMapPort load failure.
The failed start waiter ended normally at its timeout; no task runner remains active.

The user chose to have the ArenaMode error repaired in the other session. No ArenaMode source,
profile, recovery state or third-party configuration was modified. The four-map/no-bot crowd,
three-voice, cleanup and hideout checks remain pending. Existing pre-deduplication playtests must
not be presented as acceptance of this candidate. Prepared capture/view helpers are in
`work/release-0.4.0/`; resume with a fresh process after the host is repaired, record loaded MVIDs,
and finish G5 before publishing. All original acceptance gates remain in place.
