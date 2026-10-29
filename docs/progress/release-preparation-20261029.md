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
