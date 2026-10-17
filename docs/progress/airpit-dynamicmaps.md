# AirPit DynamicMaps overview — 2026-10-13

## Delivered

AirPit now has an optional **ground-floor** overview for DynamicMaps 1.2.1.
The user clarified that the upper area is occupied by spectators and is not
part of the playable map. It is therefore omitted, despite having navigation
geometry in the source. There is only one map layer, Level 0.

The package lives entirely inside ArenaMapPort:

```
BepInEx/plugins/maschine-ArenaMapPort/dynamicmaps/ArenaMapPort_AirPit/
  ArenaMapPort_AirPit.jsonc
  Layers/Ground.svg
```

The existing optional adapter now loads AirPit alongside Equator and Bay5.
Client Release builds stage the two files, distribution validation checks them
against the accepted source, and multipart packaging assigns them to the AirPit
part. DynamicMaps itself is not modified. No release archive was published.

## Geometry and provenance

- Extract: `work/extract/airpit-native-v8` (furnished U6 hall).
- Existing built NavMesh: SHA256
  `8e5e959096939798a3db4a582cca9b1d37cc1ba5337daa0fc21a1acc4d9178d8`.
- Fresh Unity 2022.3.43f1 triangulation, isolated
  `work/dynamicmaps/UnityProbe`; no scene or NavMesh rebake.
- Generation: `work/dynamicmaps/airpit/ground-only/` contains the preview,
  input/output hash receipt and pack. `exporter-used.py` preserves the exact
  generator bytes used by the receipt; the subsequent source change only
  restores the map-specific missing-input error message.
- Accepted staging input: `work/dynamicmaps/release/ArenaMapPort_AirPit`.
- Ground band: world Y -0.5…1.2 m. X/Z bounds: **[-60, -86, 40, 73]**.
- A 1 cm precision grid closes floating-point seams between projected NavMesh
  tiles. This keeps the southern apron connected in the drawing. Fixed bounds
  include the west and south edges; the closed passport booths are the north limit.
- Same palette, north compass and coordinate convention as the other maps:
  world `(x,y,z)` → map `(x,z,y)`; world +Z points up.
- 15,469 SVG path points; native sprite has **38,141 vertices**, below the
  DynamicMaps limit of 65,500. No coarse fallback warning was logged.

This is simplified cartography based on ground colliders and navigation data,
not a collision or accessibility guarantee. Muted areas can include disconnected
navigation regions. Elevated spectator decks and roofs are absent.

## Native acceptance — SPT-GPU-1

Fresh process `941ee3cd`, UiBridge 0.9.1, EFT 0.16.9.5, SPT 4.1.6.
Client build, host disk and loaded assembly matched. Started a normal PMC AirPit
raid with no bots through the standard menu. Inventory → MAP selected AirPit
automatically. Runtime inspection found exactly one `MapLayer`.

| World X/Z | Heading | Evidence under `work/dynamicmaps/airpit/` |
|---|---|---|
| -16.91 / -71.70 | North, 0° | `native-ground-south.png` |
| 0 / 0 | East, 90° | `native-center-east.png` |
| -1 / 58.50 | South, 180° | `native-north-south.png` |
| -42.94 / 33.84 | West, 270° | `native-west-west.png` |

Player coordinates and arrow positions/directions agreed at all four locations.
Both native exit markers and the explicit N compass rendered correctly. No
DynamicMaps warnings/errors or SVG/tessellation/null-reference errors occurred.
Exited through North Gate and completed the result screens back to MainMenu;
profile health was 440/440.

## Checks and preservation

- Client Release build: no warnings or errors.
- DynamicMaps adapter: 6 .NET tests passed.
- Geometry/export tests: 44 passed, including an AirPit tile-seam fixture and
  exclusion of upper-floor navigation geometry.
- Scoped PowerShell deployment fixtures passed for all three packages and
  rejection/migration cases. Legacy cleanup remains Equator/Bay5 only.
- Distribution fixtures: 18 passed; real distribution: 3 maps, 9 bundles,
  3 shader sidecars. Release packaging: 103 tests passed; the real AirPit part
  contains exactly the two overview files.
- Generated, accepted, dist, live and GPU-host overview bytes matched.
- All 103 snapshotted existing bundle/map/DynamicMaps asset files stayed
  byte-identical. The separate DynamicMaps CFG hash changed during the native
  restart/test; no task tool wrote its settings, and byte preservation of that
  game-managed file is not claimed.
- The discarded two-layer experiment was never released. Its exact known
  `Mezzanine.svg` was removed from the live owned folder through an inspected,
  hash-guarded one-off branch of `tools/deploy-to-spt.ps1` (dry run, then Confirm).
  That temporary branch was removed afterwards; receipt and script snapshot
  remain under ignored `work/dynamicmaps/airpit/`.

All source-derived graphics and native screenshots remain in ignored work/dist.

## Team-box refresh — 2026-10-17

After ArenaMode's native acceptance of the original north/south starts, the user
requested a fresh overview including those boxes. The old drawing used U6 and
ended at world Z73; widening its frame alone would not restore the north floor.

- Fresh Unity 2022.3.43f1 triangulation of the **existing U8 bundle**, SHA256
  `2a72a71ba0c959a816c26885c58ea50412075625ae62e4cfdb420b54d185d275`:
  11,787 vertices / 5,423 triangles. No scene/NavMesh rebuild or DLL build.
- Source colliders: `work/extract/airpit-native-v9`. Drawing bounds now
  **[-60, -86, 40, 86]**; the ground-only height band and 1 cm tile seam grid
  remain unchanged. All twenty original team markers lie on the selected native
  ground component and within the new drawing.
- The first candidate at 18 cm obstacle simplification had 16,283 SVG points,
  exceeding the existing 16,000-point guard. AirPit alone now uses 20 cm obstacle
  simplification, yielding **15,035 points**. Its native walkable geometry is not
  further simplified; Equator/Bay5 retain their original settings and files.
- The inspected preview includes the northern passport hall, its booth row and
  the southern bus pocket. Spectator floors remain excluded.

Accepted output is under ignored `work/dynamicmaps/airpit-team-boxes/final/`;
the adjacent fresh `navmesh.json`, logs, `geometry-verified.json` and
`final-verification.json` bind source, current bundle and delivered bytes. The old
pack is retained under `previous-pack/`. Only these two files were restaged into
the accepted release inputs and dist, then deployed through the reviewed
two-file dry run and `deploy-to-spt.ps1 -DynamicMapsMap AirPit -Confirm`:

| File | SHA256 |
|---|---|
| ArenaMapPort_AirPit.jsonc | `cc115d91c0ef5958f4e8a215a163999a535c5b5c5f5658552a4335cb26008b67` |
| Layers/Ground.svg | `2033d7cb9f4049075964c777cc37ce66f78cf53c8af54b3bcae1e7e29441ee07` |

Generation, accepted release inputs, dist, local install and GPU1 copies match.
The existing 0.2.2 release archive was **not** rewritten or published; a subsequent
package build will consume these updated staged files.

Checks: 44 export tests passed (including north/south coverage, consistent bounds
and explicit spectator-floor exclusion), 104 scoped deployment fixtures passed,
and real distribution validation passed for all three maps/nine bundles.
All 2,349 snapshotted other MapPort/server/DynamicMaps files, Arena DLLs and
MapPort config remained byte-identical through deployment.

Native check on **SPT-GPU-1**, fresh instance `456ec42b`, 22:00–22:01 local:
Inventory → MAP selected `Arena: Air Pit — ground plan`; one ground layer,
north bound 86, both boxes/compass/exits visibly rendered. Actual
`MapLayer.Image.sprite.vertices.Length` was **37,658**, below DynamicMaps'
65,500 limit; the complete process log had zero DynamicMaps warnings/errors.
Evidence: `native-map.png` and `native-inspection.txt` in the task output directory.
The host returned to MainMenu. This was a **menu-only renderer check**, not a
new raid or player-arrow alignment test; coordinate formulas/axes are unchanged.
