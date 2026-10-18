# Bowl DynamicMaps overview - 2026-10-18 (PLAN-BOWL item 17)

## Delivered

Bowl has an optional **ground-floor** overview pack for DynamicMaps 1.2.1, generated offline from the U1 NavMesh
and the Bowl v2 package, the fourth pack after Equator, Bay5 and AirPit. As on AirPit there is one layer, Level 0;
the stands and every upper surface are omitted. The client rows listed under "Integration rows" were applied the
same evening and the pack is staged into dist (see the dated note at the end), but it is **not deployed and not
host-tested**: the in-game acceptance (inventory -> MAP, player arrow at several positions, actual sprite vertex
count) is open.

```
BepInEx/plugins/maschine-ArenaMapPort/dynamicmaps/ArenaMapPort_Bowl/
  ArenaMapPort_Bowl.jsonc
  Layers/Ground.svg
```

## Inputs and provenance

- NavMesh bundle `work/build/bowl/arena_bowl_navmesh.bundle`, 185,840 bytes, SHA256
  `129eb4d65bcc8030d3b9e76a5b81554611a928a41f72ce1059d1d9f37bd6687c`; `dist/.../bundles/arena_bowl_navmesh.bundle`
  is the same file (sha256sum of both identical). `navmesh.json` records this hash as `bundleSha256`.
- Triangulation: the isolated probe `work/dynamicmaps/UnityProbe` (Unity 2022.3.43f1), never `UnityProject`. The probe's
  `Assets/Editor/ExportMapTriangulation.cs` and the repository copy are byte-identical (SHA256
  `1c5d247a74c148f247d60281124f64fea1dd63382a1aec1d79b85baaae1404b9`). Run 2026-10-18 15:53, editor exit code 0,
  log line `[ArenaMapPort][MapTriangulation] exported 12704 vertices / 5890 triangles`. The trailing
  `Curl error 42: Callback aborted` is Unity's exit-time noise (AirPit's log has the same line). That is the U1 bake's
  12,704 / 5,890 (`bowl-release.md`: `triangulation: vertices=12704 triangles=5890`; the live raids logged the same). No scene or NavMesh rebuild took place.
- Source colliders: `work/extract/bowl-native-v2`, descriptor `tools/maps/bowl.json`, exits `tools/maps/bowl.exits.json`.
- Output (ignored): `work/dynamicmaps/bowl/` holds `navmesh.json` (920,450 B, SHA256
  `5d0e4ee90efdec2b5af66cf430df04a1dfb4400162c9dfa8c1a6f51696640c71`), `unity-export.log`, `pack/`, `preview.png`,
  `receipt.json`, `generation.log`, `exporter-used.py` (the exact generator bytes of the receipt) and `iterations/`.
  Accepted staging input: `work/dynamicmaps/release/ArenaMapPort_Bowl/` (same two files, hash-identical to `pack/`).

## Profile

`tools/dynamicmaps/export_equator.py`, `MAP_PROFILES["arena_bowl"]`:

```python
"arena_bowl": {"name": "Bowl", "folder": "ArenaMapPort_Bowl", "title": "BOWL",
               "heights": (-.5, 1.2), "anchor": (2.5, -21.1), "relative": True,
               "seam_grid": .01, "bounds": [-66, -82, 62, 11], "obstacle_tolerance": .20,
               "hidden_paths": ["SBG_Arena_Bowl_Building/SOO_LOD0/BUILDING/Stadium",
                                "SBG_Arena_Bowl_Building/OO/Stadium_Chairs_F1"]},
```

- Ground band -0.5..1.2 m: the pitch is at y 0.0-0.1; the ground-band histogram of the NavMesh triangles is 2,352
  topping out at y 0.05 (the floor cells), 980 between 0.05 and 0.5, 38 between 0.5 and 1.2, and 2,520 triangles (4,630 m2) above 1.2 m (Mi-26
  roof cell y 1.6, tower platforms, roofs and stands up to y 11.05).
- Anchor = Arena's core point 2 (`arena_bowl-bot-core-02`, 2.5 / -21.1), also on the east spawn's component.
- Bounds = the descriptor's `bake_bounds` (x -66..62, z -82..11), a 128 x 93 m frame; the anchored pitch component spans
  x -63.84..59.64, z -79.45..8.75 and the whole ground band (muted ground included) x -66..62, z -81.7..10.9. A mirror test pins `bounds` to `bake_bounds` and `anchor` to core point 2.
- `seam_grid` .01: the U1 NavMesh tiles meet at z=-51.2 with a float-sized gap (like AirPit). Without the grid the
  anchored component is 3,322 m2 and the 1,380 m2 south half is a separate component; with it they are one
  4,701.6 m2 component.
- `obstacle_tolerance` .20 (AirPit's value) for point-count margin; see the iteration table.
- Two additions to the generator, neither changes the other three maps (see "Checks"): `hidden_paths` (hierarchy
  prefixes dropped from the drawing like opened barriers, whole path segments only) and the skip of explicit
  `mesh_id: null` MeshColliders (the two `Cabin_ladder` colliders; counted as `meshless_mesh_colliders_skipped`).
  The first attempt stopped with `ValueError: Invalid mesh ID` on exactly those two; a malformed non-null ID still fails.

## Counts and receipt

| Item | Value |
|---|---|
| Native triangulation | 12,704 vertices / 5,890 triangles |
| Native walkable area in the ground band | 7,666.46 m2 |
| Anchored component (drawn light) | 4,701.64 m2, bounds x -64.0..59.7, z -79.5..8.8 |
| Colliders counted at ground height | 6,206 MeshCollider + 60 BoxCollider; 2 meshless skipped |
| Areas: floor / low obstacles / high obstacles | 12,634.50 / 1,528.01 / 1,494.94 m2 |
| SVG path points | 14,104 (guard 16,000): 338 floor, 1,709 muted ground, 1,377 anchored ground, 5,657 low, 5,023 high |
| Drawing bounds (world X/Z) | [-66, -82, 62, 11]; SVG viewBox `0 0 128 93`, path extent 0..128 x 0..93 |
| `ArenaMapPort_Bowl.jsonc` | 1,671 B, SHA256 `0a7601772705111075f4006412991bf917d52f8b4d4c61aee5c4138991de53e3` |
| `Layers/Ground.svg` | 214,968 B, SHA256 `138750ffc16aaef073c74bd2ac5579f1b4c00b839ba680889b10a96d079064dd` |
| `receipt.json` | SHA256 `acba41e054cd3efd7014eb232bd5cea7aabdabb25aa8762b0c6a701af6542d15`, `generator_sha256` `8f085ac46dd6ea384c8a5274b6522c7ba7c40f4cd0ece53d394ae37fd952195e` (= `exporter-used.py` = `tools/dynamicmaps/export_equator.py` of this change) |
| Preview | `work/dynamicmaps/bowl/preview.png` (1280 x 930, 10 px/m) |

Native sprite vertex count is **not measured**. AirPit's ratio (15,035 points -> 37,658 vertices, 2.5x) estimates
about 35,000 vertices for Bowl against DynamicMaps' limit of 65,500; the in-game `MapLayer.Image.sprite.vertices.Length`
must confirm it.

Map definition: `MapInternalNames ["arena_bowl"]`, DisplayName `Arena: Bowl — ground plan` (em dash, as in the other three packs), one layer `Ground Floor`
(`Maps/ArenaMapPort_Bowl/Layers/Ground.svg`), GameBounds world Y -8..12, label `BOWL / GROUND`, two static exit
markers `Bowl BTR Gate` (51, -31) and `Bowl Army Gate` (-51, -49) shown outside raids only. World `(x, y, z)` becomes
map `(x, z, y)`, north is +Z.

## Iterations

| # | Change | Result |
|---|---|---|
| 0 | profile with the 18 cm default and no stadium filter | stopped: `Invalid mesh ID` (two `Cabin_ladder` MeshColliders, `mesh_id` null) |
| 1 | skip meshless colliders; band -0.5..1.2, anchor core 2, seam grid .01, bounds = bake_bounds | 15,836 points; both pockets, pitch, helicopter, containers, exit boxes visible; two full-width strips at z=10 and z=-81 = the stands' lowest rows (Stadium colliders; `iterations/iter1-18cm-with-stadium/`) |
| 2 | `hidden_paths` for the Stadium shell and seating | strips gone, low/high area 1,861/1,795 -> 1,496/1,460 m2; the rest of the preview identical (only the top and bottom bands differ, 470 m2 of preview); 15,878 points, 122 below the guard (`iterations/iter2-18cm-stadium-hidden/`) |
| 3 | obstacle tolerance .20 | 14,104 points, 1,896 below the guard; helicopter body, tail and rotor blade still read; accepted |

What the accepted drawing shows (inspected with the preview PNG, an SVG layer render in distinct colours and crops of
both pockets and the helicopter): light grey = the anchored NavMesh component (the pitch); muted blue-grey = other
ground-level NavMesh components (building interiors, the ring behind the border walls) and, darker, original floor
faces; ochre/dark = low/high obstacle projections 0.4-1.8 m above the local ground (walls, containers, the Mi-26
silhouette with its fallen rotor blade). Both pockets are funnels with their flanking walls and spawn markers, drawn
open (`DOORS_SPAWN_A/B` are in `opened_barriers`). The stands are not drawn: everything above the band is absent and
the stands' footprint is empty background at the top and bottom edges.

Coverage check against the tracked sidecars on the drawn anchored component: all four player spawns (50.16/-3.25,
55.97/-5.85, -55.33/-69.09, -51.59/-63.43) and both exit boxes are on it, as are 14 of 17 bot spawns. The other three
(`core-12/13/14` at 3.5, 5.1 and 1.6 m from it) stand on the Mi-26 roof cells (y 2.05, 1.81 and 1.64, above the
ground band); the capture centroid is 6.3 m off it. Cartography is not proof of accessibility: muted ground includes
isolated regions.

## Checks (2026-10-18)

- `python -m unittest discover -s tools/tests -p "test_dynamicmaps_export.py"`: `Ran 27 tests ... OK`.
- `... -p "test_dynamicmaps_bowl.py"` (new, 9 tests: pack identity/bounds/markers, tile seam and elevated cells,
  obstacle placement and whole-segment hidden paths, meshless skip and malformed IDs, missing triangulation, three
  sidecar mirrors): `Ran 9 tests ... OK`.
- `... -p "test_dynamicmaps*.py"`: `Ran 53 tests in 3.667s OK`.
- `pwsh -NoProfile -File tools/tests/test_dynamicmaps_deploy.ps1`: `DYNAMICMAPS_DEPLOY_TESTS_PASSED 122 cases`
  (Bowl joins the per-map loop, the two-map install orders AirPit<->Bowl, the wrong-selection matrix, seven Bowl
  rejection cases and a lowercase `-DynamicMapsMap bowl` that must land in the canonical `ArenaMapPort_Bowl`).
- Regression: the modified generator reproduces the accepted Equator, Bay5 and AirPit packs byte for byte against
  `work/dynamicmaps/release/` (Equator jsonc `1D5A9865...`, svg `6D9B5412...`; Bay5 `B0F5F02A...`, `CA329000...`;
  AirPit `CC115D91...`, `2033D7CB...`).
- Deploy dry run, no `-Confirm`:

```
pwsh -NoProfile -File tools/deploy-to-spt.ps1 -SptRoot 'D:/SPT41' -DynamicMapsPackage work/dynamicmaps/bowl/pack -DynamicMapsMap Bowl
==== [deploy-to-spt] DynamicMaps Bowl data-only copy plan ====
   ...\work\dynamicmaps\bowl\pack\ArenaMapPort_Bowl.jsonc -> D:\SPT41\...\dynamicmaps\ArenaMapPort_Bowl\ArenaMapPort_Bowl.jsonc (1671 bytes) [NEW]
   ...\work\dynamicmaps\bowl\pack\Layers\Ground.svg -> D:\SPT41\...\dynamicmaps\ArenaMapPort_Bowl\Layers\Ground.svg (214968 bytes) [NEW]
[deploy-to-spt] plan: 2 file(s), DynamicMaps Bowl data only
[deploy-to-spt] DRY RUN (default) -- no files were copied.
```

  (paths shortened here; the real plan prints the full `D:\ArenaMapPort\...` and `D:\SPT41\BepInEx\plugins\maschine-ArenaMapPort\...`
  paths.) Nothing was written to `D:\SPT41`.

## Limits

- Ground floor only; roofs, tower platforms, the Mi-26 roof cell and the stands are omitted. The helicopter appears
  as the projection of its 0.4-1.8 m slab; the spawns under it have no ground cell in the drawing.
- Muted regions (building interiors and the ring behind the level borders) are isolated NavMesh regions, not a
  claim of access; the layer-29 border cubes are excluded from the drawing like every border.
- Round primitive colliders use conservative boxes; simplification is 20 cm on obstacles and 4.5 cm on the NavMesh.
- Four right-edge alcoves at x 59.8..62, z -33..-47 are clipped by the bake volume at x=62; the NavMesh ends there too.
- Not verified in the game: native renderer output, sprite vertex count, arrow alignment, the in-raid map selection.

## Integration rows (handoff to the integrator, not applied by this task)

1. `ArenaMapPort.Client/ArenaMapPort.Client.csproj`, target `ArenaMapPortStageDynamicMaps` (line 194): append
   `;ArenaMapPort_Bowl\ArenaMapPort_Bowl.jsonc;ArenaMapPort_Bowl\Layers\Ground.svg` to the `ArenaMapPortOverview`
   `Include` string (eight files then).
2. `ArenaMapPort.Client/DynamicMapAssets.cs` line 10: `Maps = { "Equator", "Bay5", "AirPit", "Bowl" }`.
3. `tools/validate/validate_distribution.py` line 16: `OVERVIEW_MAP_NAMES = ('Equator', 'Bay5', 'AirPit', 'Bowl')`; the
   comment at lines 12-15 should say all four released maps have overviews (Bowl since item 17) and keep the flare
   remark ("AirPit has no active flare light ... Bowl's MultiFlare is polish too").
4. `tools/release/make_release_zips.py` needs no edit: its dynamicmaps rule derives `ArenaMapPort_{display}` from
   `RELEASE_MAP_NAMES`, which already holds `Bowl`.
5. Tests that pin the old state: `tools/tests/test_validate_distribution.py` line 51 (`for name in ('Equator',
   'Bay5', 'AirPit')` -> add `'Bowl'`) and lines 387-396 (the Bowl block must drop its dynamicmaps assertions: the
   `assertFalse(... ArenaMapPort_Bowl ...exists())` and the mkdir/write/`assertRaisesRegex("Unexpected or missing
   overview")`/rmtree sequence; keep the flares and water ones and fix the comment);
   `ArenaMapPort.Server.Tests/DynamicMapAssetsTests.cs` (add `[InlineData("Bowl")]` after line 23, line 78
   `Assert.Equal(3, loaded.Count)` -> `DynamicMapAssets.Maps.Length`, line 91 expected `missing` ->
   `{ "Bay5", "AirPit", "Bowl" }`); `tools/tests/test_release_zips.py` `BowlPartTests.setUp` strips Bowl's
   dynamicmaps files and stays valid (the pack is optional per map), so it may remain.
6. `AGENTS.md` (Hard rules, DynamicMaps packs): `-DynamicMapsMap Equator|Bay5|AirPit` -> `Equator|Bay5|AirPit|Bowl`.
   Then a Release client build stages the pack into dist, `validate_distribution.py` compares it to
   `work/dynamicmaps/release/ArenaMapPort_Bowl`, and `deploy-to-spt.ps1 -DynamicMapsMap Bowl` (dry run first)
   installs it.

## 2026-10-18 (evening) - integrated into the build

The integration rows above were applied the same evening: the two `ArenaMapPortOverview` rows in the client csproj,
`DynamicMapAssets.Maps` + "Bowl", `OVERVIEW_MAP_NAMES` + 'Bowl' with the validator and client tests, and the
`-DynamicMapsMap Equator|Bay5|AirPit|Bowl` sentence in AGENTS.md. A full client Release build stages
`dynamicmaps/ArenaMapPort_Bowl/ArenaMapPort_Bowl.jsonc` (1,671 B) and `Layers/Ground.svg` (214,968 B) into dist,
byte-identical to `work/dynamicmaps/release/ArenaMapPort_Bowl`; `validate_distribution.py` prints
`DISTRIBUTION_OK 4 maps 12 bundles 4 sidecars`, and the deploy dry run lists both files as new. Still open: the
in-game MAP check on GPU-2 and the measured sprite vertex count (recorded in bowl-release.md when done).

## 2026-10-18 (evening) - opened in game on GPU-2

Raid 3 of the 0.3.0 verification (Bowl, 00:30, no bots): DynamicMaps 1.2.1 loaded `ArenaMapPort_Bowl.jsonc` and precached
`Layers/Ground.svg` at startup with 0 warnings; the inventory MAP tab showed `BOWL / GROUND` with the player arrow at
(50.20, -3.30) in the east pocket, the `Bowl BTR Gate` exit marker, both pockets, the containers and the Mi-26 outline
(still `bowl-u3-map`). `uibridge_inspect` on the `Ground Floor` `MapLayer`: `Image.sprite.vertices.Length = 35530`
(limit 65,500). Player-arrow alignment at the spawn matched the drawn pocket; other positions were not checked.
