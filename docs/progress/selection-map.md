# Original Arena region-selection artwork

## 2026-10-13 - source, integration and validation

The user requested Arena's map-selection overview, or its authentic map marker positions if the
artwork could not be imported. The original artwork can be imported independently of scene bundles.

### Source and alignment

- Arena 0.6.0.47752, `EscapeFromTarkovArena_Data/sharedassets1.assets`: Texture2D
  `Main_Map_Screen` (observed path ID 243), Sprite 4216. The extractor resolves the unique texture
  by name, not by path ID.
- Original PNG: 1080 x 1080 RGBA. The geographic image occupies the alpha bounding box
  `(140, 0, 940, 800)` in top-down image coordinates. Its Unity sprite rectangle is therefore
  `(140, 280, 800, 800)`, with pivot `(0.5, 0.5)` and 100 pixels per unit.
- Reviewed decoded RGBA SHA256:
  `6d0ff193b926226768a03a2f24dc11cc0d21b9927ddc2e356a65f7e7205df1f0`.
  Export retains the entire original image without resampling, recoloring or cropping on disk.
- EFT's `region_map_external` is 800 x 800. Comparing the aligned central 720-pixel square gives
  grayscale correlation 0.98714227; phase correlation peaks at zero displacement. Geography matches,
  while Arena's artwork is brighter and has stronger contrast. Stretching the entire padded Arena
  texture into the native image would misalign the existing markers and transit paths.
- Source dumps, exported comparisons and audit results are ignored under
  `work/map-selection-audit/`. No original pixels or Unity dumps enter Git.

### Runtime behavior

`SelectionMapArtwork` hooks the verified native location screen's `ShowInternal` and `Close`.
It resolves the map Image through `_locationButtonsContainer.parent`, requires the expected
native sprite and no competing override, then changes only its sprite. The Image rectangle,
aspect fitter, marker-container inset, marker coordinates and paths remain unchanged.

The client restores the previous sprite on close/re-entry, caches the imported sprite/texture,
and disposes them when the plugin is destroyed. Missing assets or an unexpected screen keep the
native artwork and emit one actionable warning. Successful activation uses Debug. There is no
new player setting. Both PMC and ArenaMode use this native screen.

The extractor and distribution validator verify the image's identity, dimensions, alpha bounds,
reviewed pixel content, manifest rectangle and encoded-file hash. A changed Arena asset requires
an explicit source review. Release/DllOnly builds require and stage only `region-map.png` and
`manifest.json` under `maschine-ArenaMapPort/map-selection`, adding approximately 1.63 MiB.

### Marker provenance and limits

Authentic Arena marker coordinates were not found. Arena's selection prefab has an empty location
container populated at runtime. Its MapSpriteData maps keys to illustration sprites, not geographic
anchors; MapPoints describe raid entry/extraction positions. The current encrypted IL2CPP metadata,
available local files and website gallery did not provide the missing backend/UI coordinates.

Accordingly the existing coordinates remain unchanged: Equator 700/290, Bay5 320/820 and AirPit
500/300 in SPT's 0..1000 anchor space. These are existing project placements, **not verified Arena
positions**. A clear Arena selection-screen screenshot or readable runtime location data would
allow a separate provenance-backed marker adjustment.

### Verification

- Client Release/DllOnly build: 0 warnings, 0 errors.
- Extractor tests: 9 passed, including changed RGB content with unchanged framing, ambiguous
  texture identity, invalid dimensions/alpha, corrupted hashes, redirected payloads and source-write protection.
- Distribution-validator tests: 18 passed; real staged distribution: 3 maps, 9 bundles, 3 sidecars.
- Multipart packager tests: 103 passed, including actual fixture ZIP extraction, byte-identical artwork
  present only in Core, missing required artwork and rejection of unexpected files.
- Deployment dry run: 65 files; 2 added, 1 changed, 62 identical. Only the client DLL and the two new
  selection-map files differ. The sanctioned deployment script copied the accepted distribution to
  `D:\SPT41`. Server and map bundle contents did not change.
- GPU-1 disk hashes match the staged candidate: client
  `e90ae18c1f1e1e1c13ed15b579470402fc9417d0d27cd69116b80be917fda853`, PNG
  `6a10694d06b405385cf8f6027403e1cb17ce306d893cea4c3fb55ddb361aed05`.

The multipart packager now classifies these two exact filenames as required Core files. Its strict
rejection of unrelated files under `map-selection` remains in place. The actual distribution plan
contains 65 files: Core 5, Equator 20, Bay5 25, AirPit 15. No release archive was rebuilt or uploaded.

### Native menu test - SPT-GPU-1

The user released GPU-1 for this test; GPU-2 was not controlled or restarted. Started the GPU-1 test
profile through `gpu-run start --host host1`. UiBridge instance `104e4020`, EFT 0.16.9.5-40743,
Unity 2022.3.43f1, 1920 x 1080. All 19 machine plugins matched local deployment, host disk and loaded
assemblies. All 43 plugin instances loaded successfully.

- PMC: original Arena artwork visible; Equator, Bay5 and AirPit each selected successfully.
  Runtime Image sprite was `ArenaMapPort_RegionMap`, with `region_map_external` retained as original.
- Back to raid-side selection: the same Image reported `region_map_external`; the adapter's owned
  Image field was null, confirming restoration. Reopening through ARENA applied the imported sprite again.
- ArenaMode: original artwork visible with its existing panels and labels; Equator and Bay5 each
  selected successfully. This host's current ArenaMode map list does not expose AirPit; its visibility
  there is controlled by that other mod and was not changed.
- Returning directly to MainMenu restored `region_map_external` again. No raid was started.
- Host log `LogOutput-SPT-GPU-1-2026-10-13_10-54-51.log`: artwork activation at 10:56:38.210 and
  10:58:08.871. No ArenaMapPort Warning/Error entries throughout the menu test.
- Native frame captures, inspected at full resolution, are under ignored
  `work/map-selection-audit/`: `pmc.png`, `pmc-bay5.png`, `arena.png`, `arena-equator.png`, `arena-bay5.png`.

Existing presentation issue seen on this three-map development branch: the longer PMC AirPit and
Equator labels overlap slightly at their unchanged anchors. All three remain selectable. No placement
is inferred from the artwork; authentic Arena marker data remains the separate unresolved source question.
The published release and the user's vanilla installation were not updated.

## 2026-10-13 - marker positions recovered from user screenshots

The user supplied five 2560 x 1440 Arena screenshots in `analysis/screenshots/map`, named
`Screenshot 2026-10-13 112239.png`, `112247.png`, `112256.png`, `112305.png`, and `112323.png`.
This resolves the displayed-position question above without claiming access to backend coordinates.
The tracked measurement record is `tools/maps/selection/markers.json`: image hashes, source keys,
screenshot pin centers, calibration, derived SPT anchors and explicit unmeasured entries. Raw screenshots
and extracted sprite pixels remain ignored.

### Measurement and identity

An independent image-registration pass matched 60 geographic landmarks across the five screenshots
to the original region texture. With top-left pixel coordinates:

`screenshot = originalPNG * 1.27144743554 + (143.539456899, 66.711597767)`.

The fit has RMS error 0.1045 screenshot pixels and maximum error 0.2561 pixels. Pin centers were matched
against their original 80 x 80 sprites (including the selected AirPit variant), with conservative
uncertainty of +/-1 screenshot pixel. Arena's MapToggle IconHandler/Image and sprites have centered
pivots; the measured point is the full sprite center, not the label center or downward pin tip.

The original map's geographic window starts at PNG (140, 0), size 800. SPT's Image is 800 x 800 logical
units in its normal 16:9 layout; its marker container has a 5-unit inset on each edge. For a measured
geographic point `(qx, qy)` with downward Y, the stored native anchors are:

`IconX = round(1000 * (qx - 5) / 790)`

`IconY = round(1000 * (795 - qy) / 790)`

SPT scales these through `RelativeMapPos = (IconX, IconY) / 1000`. The native CanvasScaler keeps this
layout at 16:9 and wider. Narrower aspect ratios enlarge the map's logical height, causing at most a few
pixels of residual inset difference (about 2.3 logical pixels at 4:3); these measurements are not exact
backend coordinates or a guarantee of subpixel alignment on every aspect ratio.

Aliases were checked against Arena's locale and original `MapSpriteData` table: AutoService is Chop
Shop, Prison uses Fort art, RailwayStation uses SkyBridge art, Yard is Block, Shoreline uses Base art,
Woods uses EMERCOM art. Custom's unique three-arch icon matches Dorms. Factory and Iceberg retain their
own source identities. Independent source/registration evidence is under ignored
`work/map-selection-audit/source/marker-identity-evidence.json` and `work/map-marker-audit/review/`.

| Display name | Catalog key | IconX | IconY |
|---|---|---:|---:|
| Equator | arena_equator | 788 | 521 |
| Bay 5 | arena_bay5 | 469 | 92 |
| Air Pit | arena_airpit | 792 | 212 |
| Bowl | arena_bowl | 919 | 679 |
| Fort | arena_prison | 234 | 731 |
| EMERCOM | arena_woods | 479 | 690 |
| Chop Shop | arena_autoservice | 407 | 471 |
| Dorms | arena_custom | 547 | 402 |
| Base | arena_shoreline | 219 | 307 |
| Iceberg | arena_iceberg | 110 | 608 |
| Block | arena_yard | 691 | 799 |
| Factory | arena_factory | 488 | 580 |
| Skybridge | arena_railwaystation | 867 | 401 |
| Labs Replica | No catalog entry; measurement reserved | 752 | 334 |

The 13 mapped positions are applied to the shared catalog, their build sidecars and server JSON.
Only Equator, Bay5 and AirPit remain active. Labs Replica is recorded for future work, without inventing
a location ID or activating a map. AirPit 2.0 has Gate_playtest art and is not shown; its old development
placeholder is intentionally unchanged. Sawmill is also absent from these screenshots, so it receives
no invented coordinate.

### Verification of the position update

`SelectionMapPositionTests` checks the screenshot-to-SPT round trip, every catalog/sidecar/database
pair, the three-map release subset and the distinction between AirPit and AirPit 2.0. AirPit's older
arbitrary "inside the Equator/Bay5 rectangle" test is replaced with the real 0..1000 bounds; released
markers still satisfy its separation check. Native evidence follows after deployment.

- Client Release build: 0 warnings/errors. All 738 .NET tests pass, including the three new coordinate
  tests. `validate_distribution.py` passes for 3 maps, 9 bundles and 3 sidecars. The checked-in Unity
  Equator fallback was updated too, so future descriptor generation retains the measured position.
- Deployment plan: 65 files, 5 changed, 60 identical. The changed files are the client/server DLLs
  and the three active `base.json` files; all scene bundles and artwork remain byte-identical.
- GPU-1 `deploy-wait --server --game` stopped the server, verified those five file hashes twice,
  then restarted server and EFT. UiBridge instance `a72a67c9` reports 19/19 plugins identical to the
  deployed candidate and 43 plugins loaded successfully. Server log `spt-SPT-GPU-1-20261013.log`
  records all three locations registered at 09:50:23 UTC after this restart.
- Client SHA256 `0305a19eb68ad7c9eca7b9aaaf0456977e4c06a9b6eba13c0e8127aabff3e221`;
  server SHA256 `467f3a59eff162fcfd0ff17c64e2d01a9d5c036a517d7bcc499b3abc0c32ae69`.
- PMC: Equator, AirPit and Bay5 are visible at the new geographic positions and each responds to
  selection. The old AirPit/Equator label overlap is gone. ArenaMode: Equator and Bay5 appear at
  the same anchors and both select successfully. This host's ArenaMode list still excludes AirPit;
  that unrelated mod's filtering/configuration was not changed.
- Native screenshots inspected: `work/map-marker-audit/pmc.png` and `arena.png`. All recovered
  positions are also plotted on `work/map-marker-audit/review/geographic-centers.png` for review.
- No ArenaMapPort Warning/Error in the client log `LogOutput-SPT-GPU-1-2026-10-13_11-50-40.log`.
  Host returned to MainMenu, no raid started. No GPU-2 restart, vanilla deployment, release ZIP or upload.
