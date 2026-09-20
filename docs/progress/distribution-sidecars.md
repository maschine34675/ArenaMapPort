# Distribution descriptor sidecars

## 2026-09-20 — descriptor discovery repair

`validate_distribution.py` loaded every JSON file under `tools/maps` as a map descriptor. Once
Equator gained `.bots.json` and `.exits.json` sidecars, the validator stopped with
`DISTRIBUTION_FAILED: 'key'` before checking bundles, shader sidecars, databases or DLLs.

Registry and distribution validation now share `map_descriptor_paths`: plain `<stem>.json` files
are descriptors, and dotted stems such as `<stem>.bots.json`, `.exits.json` and `.opened.json` are
sidecars. This preserves the registry validator's existing naming rule. Selection does not depend
on JSON fields, so a malformed real descriptor remains a failure rather than silently disappearing.
The registry preflight and all distribution content checks remain in place.

### Verification

- `python -m unittest discover -s tools/tests -p test_validate_distribution.py`: **6 passed**.
  Fixtures cover bots/exits/opened sidecars, a sidecar containing a misleading `key`, missing-key
  and invalid-JSON real descriptors, and an unexpected distribution bundle. Two cases invoke the
  real registry validator against temporary copies of the current map definitions.
- `python -m unittest discover -s tools/tests -p "test_*.py"`: **82 passed**.
- `python tools/validate/validate_distribution.py`: exit 0,
  **`DISTRIBUTION_OK 14 maps 42 bundles 14 sidecars`**. All 42 Unity internal bundle names were
  unique; staged database content and both staged DLLs matched their source/build artifacts.
- `git diff --check`: passed.

The distribution fixture substitutes external Unity/MSBuild checks with small files; the separate
full distribution run above checked the actual staged artifacts. This tooling-only repair does not
change game behavior and is not evidence of an ArenaMode raid or a new deployment.
