# Owned DynamicMaps packages — 2026-10-11

The user approved moving the two overview maps into ArenaMapPort's own directory
and deleting their old copies from DynamicMaps. This changes only asset discovery
and delivery; accepted map geometry, bounds, labels, compass and markers are retained.

## Implementation

- `DynamicMapsIntegration` is a soft dependency on `com.mpstark.dynamicmaps`.
  Installed DynamicMaps 1.2.1 was decompiled and its public loader/definition API
  verified. No compile-time reference or third-party source is included.
- After native `MapSelectDropdown.LoadMapDefsFromPath("Maps")`, each owned package
  directory is loaded through the same method, with a reentry guard. Missing JSON
  or SVG skips that package. The `MapDef.LoadFromPath` postfix resolves only the
  exact owned JSON's historical ground image token. Static marker paths remain native.
- Every Release build stages exactly four accepted files from
  `work/dynamicmaps/release/` into `dist/.../ArenaMapPort/dynamicmaps/`.
  These are byte-identical to the accepted north-marker packs; no scene rebuild.
- Data-only deploy now targets our directory and works without DynamicMaps installed.
  Explicit `-RemoveLegacyDynamicMaps` validates both replacements and installed client
  against dist before deleting the four known old files and their empty directories.
  Unknown content, links and stale/missing replacements abort preflight. No recursive
  deletion. Ordinary/DLL/data deployment remain copy-only.

## Baseline

SPT41: four owned legacy map files and 90 unrelated DynamicMaps files. Vanilla:
no installed DynamicMaps or legacy packs. Machine-readable hashes are under ignored
`work/dynamicmaps-owned/dynamicmaps-before.json`. The previous accepted map SVG hashes
are Equator `6D9B541294DE32D83175E009BAA2BB8F19EEDE6E756675E483DE433BD4B78232`
and Bay5 `CA329000ADFD1385EA48FCAFAD807020A886AFDE65CC01FBD5269C6BE1C9850C`.

Native acceptance and final deployment evidence are appended after testing.
