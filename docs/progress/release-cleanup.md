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
