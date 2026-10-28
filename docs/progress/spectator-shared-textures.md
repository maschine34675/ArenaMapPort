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
