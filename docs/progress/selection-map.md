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
