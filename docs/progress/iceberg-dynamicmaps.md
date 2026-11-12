# Iceberg DynamicMaps overview — 2026-10-31

User request: add a DynamicMaps overview for the new Iceberg map. GPU-2 was explicitly
released for a short map/marker test. Implemented on `feature/iceberg`, source commit
`bf48250`; no push, merge, version bump or publication.

## Source and drawing

- Accepted scene package: `work/extract/iceberg-native-v2`; original colliders and
  the eleven open gate pose rows in `tools/maps/iceberg.doors.json`.
- Native Unity 2022.3.43f1 triangulation from
  `work/build/iceberg/arena_iceberg_navmesh.bundle`: 18,703 vertices / 8,599 triangles.
  Probe script matched the repository copy; no scene rebuild. Bundle SHA-256:
  `66cb45c446a925b2ba45c9b6321330ef1a227c240c2727255399c7b1d13c9c3c`.
- Iceberg needs 3D selection **before** the XZ projection: its disconnected roofs
  overlap the ground routes. A centimetre-welded edge component at original AI core 3
  `(10.36, 3.15, -77.21)` keeps the sloping combat routes; a shared corner alone does
  not connect regions, and the anchor's height disambiguates stacked surfaces.
- Independent native Detour check: main component 4,866.92485 m2, Y -2.75..6.15.
  The Unity triangle selection covers 4,866.87985 m2 (under 0.05 m2 difference).
  Native main-component projected area has no stacked overlap. After the existing
  cartographic simplification and seam snapping, the drawing has 4,867.365 m2 of
  connected route surface. There is no second playable floor layer.
- Bounds X -62..121 / Z -131..-33 match the reviewed bake. North is +Z with the
  existing explicit N/compass marker. Coordinates remain Unity `(x,y,z)` to map
  `(x,z,y)`, rotation zero. A profile-specific 190 m extent guard accommodates the
  183 m width; existing maps retain their guard and geometry behavior.
- 5,579 considered colliders, five meshless colliders skipped; all eleven gate rows
  resolved once. The first 0.20 m obstacle simplification exceeded the 16,000-point
  SVG budget. At 0.30 m it is **13,413 points**; the finer route outline is unchanged.
  Dark structural context remains distinct from the light traversable route surface.

## Package and deployment

The only data files are `ArenaMapPort_Iceberg.jsonc` (1,703 bytes) and
`Layers/Ground.svg` (208,502 bytes). Generated preview and input/output receipts stay
in ignored `work/dynamicmaps/iceberg`; accepted files live in
`work/dynamicmaps/release/ArenaMapPort_Iceberg` and are required/staged by Release and
DLL-only client builds. Distribution validation requires exact copies. The packer
already classifies these paths under Iceberg, so its production rules did not change.

The runtime allowlist, build staging, distribution list and scoped deploy selector
all include Iceberg. The lowercase selector is normalized to the canonical folder.
The developer deploy path copies the two files into ArenaMapPort's own
`dynamicmaps/ArenaMapPort_Iceberg` directory, never DynamicMaps' installation.

Client Release build: zero warnings/errors. Dry-run and confirmed deployment copied
exactly the two map files and the client DLL into `D:\SPT41`. The client SHA-256 is
`d19f41439b33e4d262a197dc82108b497d9099c87b69f860d9974a216148293b`.
GPU-2 disk has the same hash. Loaded MVID, local MVID and host disk MVID all equal
`e615d110-00bf-4db4-95b5-943960196549`; loaded informational version is
`0.4.0+bf48250b5179c32ff2d7d3c64f5335ed230f6fcb`.

## Native acceptance

Fresh GPU-2 process started at 09:16:21 Europe/Berlin. Two plain PMC Iceberg raids
without bots used noon/clear test overrides. The first confirmed map loading and the
west-spawn marker; later screenshots in that first scenario accidentally captured
GEAR because reopening inventory resets the tab. Those screenshots are **not** map
acceptance. The corrected scenario explicitly selects MAP after every inventory
open and waits for `ICEBERG / GROUND`.

The second scenario (`work/dynamicmaps/iceberg/native-map`) shows the correct map,
player marker and heading at all three positions:

| Position | Unity X / Y / Z | Facing |
|---|---|---|
| East pocket | 95.29 / 0.88 / -89.56 | north, arrow up |
| Hotel / original core 3 | 10.36 / 3.15 / -77.21 | east, arrow right |
| West pocket | -39.85 / 3.25 / -91.16 | west, arrow left |

Small settled player Y differences are the native floor correction, not a map-axis
shift. The hotel screenshot shows the complete N/compass mark. DynamicMaps selected
`Arena: Iceberg — ground plan`, one level, and used the owned SVG. No ArenaMapPort
warnings/errors; the full DynamicMaps session file scan also found zero warnings/errors.
The 38-step corrected scenario returned to MainMenu, healed the test profile and
disabled godmode; raid exit cleared the clock/weather overrides. Control is free.

Automated check evidence is recorded in `work/dynamicmaps/iceberg/GATES.md` and
`gate-run.log`; the final results are appended below after the complete run.

## Final automated results

All five gates met; zero unmet and zero abandoned. The full synthetic DynamicMaps
suite (including the new 3D component cases), C# DynamicMapAssets tests, crowdless
feature-list fixtures, distribution-validator fixtures, scoped PowerShell deployment
fixtures and actual encrypted distribution validation passed. The final source,
accepted, staged and installed map hashes agree. All 15 player/bot/exit anchors are
within the selected ground footprint tolerance; all 102 pre-existing DynamicMaps,
configuration and other overview files match the pre-deployment snapshot. Actual
dist contains 283 files. The final check runner output is `gate-final.log`; integration
evidence is in the same `GATES.md` (successful G3 from the preceding complete run).

The first runner invocation used an unquoted forward-slash executable path under
cmd.exe for G1/G2; those commands did not run. Quoting the absolute Python path fixed
the runner, and both checks then passed. G3 had already passed and was not repeated.
A read-only code review found no blocking defect. Source/build, deployment/sync and
native in-game evidence are complete for this overview; this is not an Iceberg release.

## 2026-11-12 - The Iceberg exit markers moved with the exits

The exits of Iceberg moved into the team pockets (record: [spawn-rows-and-pocket-exits.md](spawn-rows-and-pocket-exits.md)). The two exit StaticMarkers in the released pack's `ArenaMapPort_Iceberg.jsonc` were patched to the new
positions (backup of the old files and fresh exports under the ignored `work/dynamicmaps/exits-20261112/`) and deployed with the full deploy of build `48b316d`; the exit
coordinates quoted above are history. The patched file is byte-identical to a fresh export. The markers in the game were not looked at on the host.
