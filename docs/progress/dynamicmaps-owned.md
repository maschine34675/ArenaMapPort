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

## Acceptance — 2026-10-11, 10:49–10:55 CEST

Implementation commit: `efbbceb`. Release client build passed with no warnings/errors;
the complete C# suite passed, including five new path/normalization/reentry cases.
The actual-script deployment suite passed **99 cases**, overview exporter tests **43**,
distribution fixture tests **10**, and actual two-map distribution validation passed.
The independent review caught an MSBuild item transform producing a literal filename;
it was corrected before deployment and the four real staged files were verified.

GPU-2 process `d8713da3`, started 10:49:45, ran debug no-bot raids on Equator and Bay5.
UiBridge verified **20/20** tracked loaded identities equal to VM and host disk.
Runtime MapSelectDropdown contained **13 definitions**: eleven existing maps, then exactly
one Equator and one Bay5 definition from `C:/SPT41/BepInEx/plugins/ArenaMapPort/dynamicmaps/`.
Neither old DynamicMaps/Maps path was registered. In each raid `_selectableMapDefs.Count`
was **1**, and each layer's ImagePath was the absolute SVG in its own ArenaMapPort package.
Reopening the map and changing raids retained thirteen definitions, without duplicates.

Inventory → MAP rendered both accepted ground plans with the green player/direction
marker and native active-exit labels. Captures and paired player coordinates:

- `analysis/screenshots/dynamicmaps-owned-2026-10-11/equator.png` (10:51:11;
  world position −5, 0.1, −33.5).
- `analysis/screenshots/dynamicmaps-owned-2026-10-11/bay5.png` (10:53:08;
  world position 56.4, −3.78, 1.9).

There were no DynamicMaps or ArenaMapPort warnings/errors in the recorded process
log. Exported ArenaMapPort evidence: `work/dynamicmaps-owned/native-log.json`.
Both debug raids returned to MainMenu; the final host remains there, outside a raid.
This test verifies changed discovery and image loading; unchanged geometry was not
recertified at every coordinate, and no physical extraction was attempted this time.

Both D:/SPT41 and D:/SPT41vanilla received the verified DLL pair and four owned map
files through reviewed dry-run/Confirm plans. Explicit migration deleted the four
old files plus empty package folders on SPT41; vanilla had none and remains without
a DynamicMaps installation. All **90/90** unrelated DynamicMaps files and its config
match the baseline byte-for-byte. New map data matches accepted sources and dist.
Vanilla needs the user's EFT/server restart; no native vanilla session was run.

Final SHA-256 (dist = both installs):

- Client: `F34A262CCE7C6F8EBDC885574B0FEDC5A77E0D1FC3DDFC548D6C45DFE8C3C905`
- Server: `3B2D76D6EDF8968B305270003E0B2907846FFD8376C5864DAA753C9A66577EC9`

No version bump, release publication, source-map rebuild or unrelated map deletion.
