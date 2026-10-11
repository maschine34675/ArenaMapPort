# Debug helper retirement and Git consolidation — 2026-10-11

## Scope

The user requested removal of the four main-menu debug buttons and consolidation,
cleanup and push of the project's open branches/worktrees. The buttons originate
only in the separate ArenaMapDebug helper, not in ArenaMapPort. Removing that DLL
also removes its shortcut-only countdown/result/disconnect changes; ordinary
PMC and ArenaMode raids have no dependency on it. The helper's source is retained.

The sanctioned deployment script's isolated `-RemoveDebugHelper` mode recognizes
the accepted helper by SHA256, checks both historical DLL locations, backs up all
present candidates, revalidates them, and removes only the exact DLL files. It
rejects other deployment flags, unexpected builds and redirected paths. Default
is a dry run; folders and configuration remain untouched. Synthetic tests cover
dry run, both paths, bad second candidates, flag conflicts, junctions, backups,
unrelated content and repeated removal.

## Git inventory before changes

There is one ordinary worktree, `D:/ArenaMapPort`, and no attached managed worktrees.
All 27 local branch tips and both remote branch tips are ancestors of `df81966`.
`main` is 139 commits behind that accepted state and can fast-forward without
conflicts or lost work. The remote has no open pull requests and originally uses
`feature/hdr-image-controls` as its default. All unpublished changes were checked
for added binaries/game assets; generated Arena content remains ignored.

Plan: preserve every initial tip in `main`, push `main`, change the remote default,
then delete merged feature/cleanup/fix branches. Keep the sole working directory,
ignored extraction/build inputs and all bin/obj directories.

Verification and execution evidence will be appended below.

## Migration checks

The new synthetic migration suite passes. Existing deployment regression suites also
pass: legacy Equator DB, DLL-only (10 cases), and DynamicMaps (99 cases). A second
read-only review found no substantive defects. The final absence checker was first
run against the installed helper and failed as expected, establishing a positive
control before removal. UiBridge likewise exposed all four shortcut buttons before
the migration.

