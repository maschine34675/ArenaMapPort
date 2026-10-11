# Release payload, Equator DB layout and menu placement

## 2026-10-11 implementation

User requested removing unused test-ZIP payload, nesting Equator's server database
and matching the PMC/ArenaMode map-label positions with Bay5 further west on the coast.

### Changes

- Remove the audio/UI staging targets. Neither ArenaMapPort nor the current ArenaMode
  reads `arena-audio` or `arena-ui`; ArenaMode owns different audio/UI assets. The
  removed staging payload comprised 2,136 files / 196,044,140 bytes. Extraction tools
  and ignored source exports remain available. Old dist copies are reversibly parked;
  release validation now rejects either family if reintroduced.
- Equator uses `db/arena_equator/base.json`, consistent with Bay5. Registry, descriptor,
  Unity fallback, server content/staging and active tests are updated. Ordinary deploy
  stays copy-only. Its separate `-RemoveLegacyEquatorDb` migration verifies the new DB
  and server DLL, rejects links/other map IDs, backs up original bytes under ignored
  `work/deployment-backups`, then removes only the loose old file.
- Shared positions: Equator 700/290; Bay5 320/820. The native UI divides these by 1000.
  A soft ArenaMode integration captures the native button anchor before its label
  presentation overrides it, restores that presentation's persistent target and
  refreshes once. Existing names, colors, selection and cleanup remain ArenaMode's.
  Nothing changes in the ArenaMode repository or its installed DLL; its current
  working HEAD contains unrelated pending changes and must not be promoted for this fix.

### Verification before deployment

- Release client: zero warnings/errors; C# suite: 615 passed.
- Release cleanup: four fixture tests; distribution: eleven tests (including rejection
  of unused exports/loose DB); Equator spawn tests: four passed.
- Migration fixtures cover dry-run, stale DLL, other ID, conflicting modes, original
  backup, neighbor preservation, idempotence and redirected mod-root refusal.
- Exact two-map distribution validates. Native UI acceptance and final archive evidence
  will be appended after deployment.
- Independent review checked the actual installed ArenaMode assembly rather than only
  its newer working source: Create signature, mutable target, instance Refresh, native
  Show ordering, presentation Close restoration and absence of a dependency cycle.

### Development-tool compatibility note

The separate ArenaMode repository's `tools/MapModeAdmissionChecks/Program.cs` still
hardcodes the former loose Equator database. It is not a runtime reader and is not
distributed here. Its next maintenance pass should use `arena_equator/base.json`.

## Native acceptance and local test archive

Implementation `e17198d` was rebuilt in Release without client/server warnings.
The complete C# suite remains 615 passed. All four runnable completion gates pass;
both manual gates were verified against the native menus and actual loaded files.

GPU-2 instance `c9548184` (SPT 4.1.6 / unchanged EFT 0.16.9.5) reports 20/20 loaded
plugin/library identities matching local and host disk. Server pid 18280 registers
both locations at 10:29:02 UTC using the nested Equator file. The first earlier
restart preceded completion of the new folder's synchronization; that incomplete
run was discarded. Both databases and server DLL were hash-verified on the host
before the final restart. The current complete client log has no owned Warning/Error.

PMC -> Arena -> PMC checked in the current game:

- Bay5's marker is on the northern docks/coast at approximately (525,148) on the
  1920x1080 screenshot; Equator at (825,566), south-east of Interchange.
- Both contexts use the same anchors; each retains its own original text/color.
- Both map buttons select successfully and show the correct image/time panel.
- Returning from Arena restores the ordinary PMC labels and retains the positions.

Evidence: ignored `work/releases/menu-check` and `arena-menu-check` screenshots/reports,
`client-native-current.log`, `server-native-current.log` and `native-cleanup-log.json`.
The first scenario's BACK helper refused its generic navigation action; navigation
continued through the native Escape command and the remaining menu checks passed.
No raid was required for these data-layout/menu-only changes; scene bundles are unchanged.

Both SPT41 and SPT41vanilla received the exact 47 runtime files through the deployment
script. Each old loose DB was backed up then removed through its explicit migration.
Existing optional audio/UI on live installs remains inert; this task removes it from
the release. Vanilla requires the user's game/server restart.

`work/releases/maschine-ArenaMapPort-0.1.0-test2-2026-10-11.zip` contains 50 files:
47 runtime files plus installation/third-party notices. Size 1,231,747,934 bytes,
193,069,451 bytes smaller than the first test ZIP. CRC and SHA256 of every decompressed
entry match the recorded source inventory; current dist is an exact file-set match.
ZIP SHA256: `c107b831185bbd4753037b33588ce70becbc85900b28b9575a0a84309fb4ddf8`.

Client SHA256: `13F702259A9E3A6F7A1C132DB72B42B90A47453A4CD574453B980FFA02CBD9FC`.
Server SHA256: `14473C642DA34AA61CBD0164E2DB0CBEAD301F3EF887C3339F797FCB3A880CE7`.
