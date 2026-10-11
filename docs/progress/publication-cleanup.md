# Source publication cleanup — 2026-10-11

The user reported a successful SPT41vanilla test and authorized publication of the
development repository after removing raw data and private details, including history.

## Source and local data

Seven raw analysis exports (game-manager settings, presets, census results and scene
externals) are excluded from the tracked tree and targeted for removal throughout
history. These are distinct from the modified server location definitions required
at runtime, which remain tracked.

Original copies remain byte-identical under ignored `work/local-analysis/`.
`work/publication-cleanup/private-before-cleanup.bundle` preserves the original
Git history; an ignored hash manifest records the backup and export copies.
These private files must not be attached to issues, releases or public archives.
The analysis generators now write into that ignored directory, and `.gitignore`
also excludes the former output paths and local task evidence.

Equator and Bay5 obtain TagManager tags from the selected source installation's
`globalgamemanagers`; Equator discovers external files from each selected scene.
Missing tag metadata fails explicitly. Neither exporter requires unpublished
analysis JSONs. The source census, ordering, paths and built-in-resource filtering
are checked against the preserved inputs.

Private workstation names, the test host's LAN address and temporary session paths
are replaced with descriptive placeholders. Technical provenance and third-party
attribution remain. The raw source BuildSettings token is excluded from publication
without attempting to authenticate it against any service.

## Verification

- Complete original Git bundle verified and seven export copies hash-matched.
- Four focused metadata tests and 50 existing map-pipeline tests pass.
- Live read-only Equator (five scenes) and Bay5 (nine scenes) census runs pass.
- Live tags and external dependencies match the preserved reference data, ignoring
  the native spelling of Unity's built-in resources.
- No runtime code or accepted game assets were changed; the accepted test3 release
  archive remains byte-identical. No new deployment or gameplay test is necessary
  for these source and history changes.

The publication gate separately scans every reachable Git blob, full history,
current tree and commit metadata with Gitleaks, verifies a positive control, and
checks GitHub for old sensitive content before changing visibility. Private scan
reports and backup data stay under ignored `work/publication-cleanup/`.

Rewriting history changes commit IDs. Older progress logs retain the original build
identifiers used during testing; the private original-history bundle and local
`.git/filter-repo/commit-map` allow those records to be traced. Existing checkouts
must be replaced with a fresh clone of the cleaned repository before pushing again.

## Publication route selected by the user

The cleaned private remote passed the same scans as the local repository. However,
GitHub still serves removed content by old commit ID in that repository. The user
therefore authorized a new independent `maschine34675/ArenaMapPort` repository from
the clean history, keeping `ArenaMapPort-dev` private. The new target is created
privately, checked using a fresh clone and old-content access probes, then made public.
It becomes the local `origin`; the private development repository is retained.
