# DynamicMaps 1.3.0 owned overview integration — 2026-11-20

The user requested adapting both ArenaMapPort's overviews and ArenaMode's Domination
overlay to DynamicMaps 1.3.0, retaining the overview files in ArenaMapPort's own
folder. This record covers only the ArenaMapPort loader. ArenaMode is a separate
repository and implementation.

## Why the loader changed

DynamicMaps 1.3.0 changed `MapDef.LoadFromPath(string)` to the two-argument overload,
validates image existence inside that method, and removes previously registered
definitions absent from every new directory scan. The old integration would skip
it at the API guard. Merely changing the reflected signature would still resolve
SVGs too late, and the old recursive per-package scans would discard earlier maps.

## Implementation

`DynamicMapsIntegration` retains the verified 1.2.1 path unchanged. For 1.3 it binds
`DynamicMapsOwnedCatalog`, checks the required native types/members, and patches
only the end of `LoadMapDefsFromPath("Maps")`.

The adapter reads each exact allowlisted owned JSON with Newtonsoft.Json into the
native `MapDef` type, validates the required fields and exactly one ground layer,
and resolves only the existing `Maps/ArenaMapPort_<name>/Layers/Ground.svg` token
to its matching owned file. Static marker paths, geometry, bounds, labels and
images are unchanged. It merges the resulting definitions into the native catalogue
without clearing/re-scanning any other map. Only its own keys are replaced or
removed. Failed packages produce a warning and do not prevent other packages.

Stable `ArenaMapPort/<name>` selection IDs and the native addon flag allow native
map preferences to recognize these choices. Native preference registration and
filtered dropdown refresh run after the merge; the current location filter stays
intact. DynamicMaps can persist its own preferences as usual. ArenaMapPort itself
does not write third-party files or configuration.

## Offline evidence

- Actual release client DLL contracts checked: 1.2.1 SHA256
  `C41507EB1EDB6D6C8101314FEDF9EB52C874C980E757CBC8348F084D2231B1B8`,
  1.3.0 SHA256
  `87878B34A7BB4D50DCF05DAA6CD9A93A27543B0C26AA38FA213C40053A6DB19B`.
- Production-linked tests cover all six owned packs, retained vanilla/addon object
  identity, active filtering, repeated native scans, deletion and recovery,
  malformed/incomplete definitions, incorrect image paths, unrelated requests and
  missing API members. Existing 1.2.1 asset/reentry tests remain.
- Contract negative controls remove the actual loader method in memory. Source
  mutants erase foreign maps, omit image resolution, or remove request scoping;
  each must reach and fail its named assertion, followed by a passing clean baseline.
- `tools/verify-dynamicmaps.ps1` runs these checks. Its no-staging target file only
  affects the explicit offline invocation; normal release staging is unchanged.

## Native acceptance

Pending the parent integration run. Offline results alone do not establish rendered
overview/minimap behavior, in-game marker alignment or the separate Domination overlay.
No installation, host, configuration or map data was changed by this implementation leaf.
