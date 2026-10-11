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

## Native and installation acceptance

Implementation `664564c` removed the accepted helper from D:/SPT41 through the
sanctioned script. Its original bytes are preserved under
`work/deployment-backups/debug-helper-0f0ce4b08f0543548017c167708f2865/`.
Syncthing initially delayed the host deletion; an early restart still loaded the
helper. EFT was closed again, and host absence was verified over SMB before the
final restart. GPU-2 instance `25d3d20a` then reported 19/19 matching identities.
The previously observable four debug controls are absent; the main-menu screenshot
was visually inspected. The normal PMC flow reaches SelectLocation and exposes
both Equator and Bay5. Evidence: `work/releases/no-debug-helper-native/report.md`
and `step-3-screenshot.png`. GPU-2 is left at MainMenu.

Only after native acceptance, the same script removed the vanilla helper and
preserved its bytes under
`work/deployment-backups/debug-helper-6164eabbde9740e3b375b81c8184e50a/`.
The final file check covers both installations and GPU-2. Existing client/server
DLLs match dist; no ArenaMapPort build or bundle change was needed. Restart EFT on
the vanilla installation if it was open. No server restart is needed for this cleanup.
The test3 ZIP already excludes the helper and remains valid without repackaging.

## Git consolidation and cleanup

`main` fast-forwarded to `664564c`, preserving the complete accepted development
history. It was pushed, configured as GitHub's default, and set to track origin/main.
The 26 other local branches and two obsolete remote feature branches were removed
only after ancestry checks. Remote deletions used their freshly fetched commit
hashes as leases to reject a concurrent update. There were no secondary worktrees
to remove. All 30 initial ref entries (including the symbolic origin/HEAD) still
resolve to commits contained in main. Final verification requires one clean
worktree, only local/remote main, matching HEAD/origin/main and GitHub default main.

