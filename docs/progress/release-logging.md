# Release logging and compressed decal copy warnings — 2026-10-11

## Scope and policy

The user requested classification of all runtime logs, reserving Info for information
that matters to players, plus investigation of current warnings/errors.

- Reviewed 331 client Info call sites. Only the version/map startup summary remains
  at Info. Patch installation, assets, repair success, settings and cleanup use Debug.
- Server Info reports each map that actually registered. Banner paths, fixed-time
  compatibility and optional APBS setup use Debug; removed the stale Manimal log prefix.
- Expensive diagnostic work additionally requires DeveloperMode: HDR state sampling,
  dust animation observation, rain-photo GPU readback, room mixer reports, per-material
  surface values, renderer/name lists, loading-screen hierarchy and smoke-buffer census.
  Public probe entry points also guard against calls outside their original hotkey path.
- F7–F10 diagnostics now use Debug. Both current installations already have BepInEx
  disk/console LogLevels=All; no third-party configuration is changed. On installations
  that filter Debug out, include it before collecting developer reports.
- Successful old-default migration and yielding to another audio writer are Debug.
  Missing assets/APIs and repair failures remain Warning/Error. Missing combat camera
  overlays, unavailable exits and missing native decal data are visible warnings,
  rather than hidden technical traces. Gameplay/repair conditions are unchanged.

## CopyTexture root cause and repair

The prior GPU-2 process log `LogOutput-SPT-GPU-2-2026-10-11_10-49-37.log` had
**214 warnings on Equator and 390 on Bay5**, immediately before native decal activation.
The source is vanilla `StaticDeferredDecalDrawInstance.CopyMipsManual`: regional copies
of compressed, readable normal maps into its shorter-mip-chain texture arrays.
Installed Unity's GraphicsFormat value **101** is **RGBA_DXT5_UNorm**.

The imported normal maps retain readable CPU bytes for bundle serialization
(`TextureImportDriver`, Apply(true,false)). Unity performs the GPU copy but cannot
also copy the compressed region into CPU-readable data. See Unity 2022.3's
[CopyTexture](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/Graphics.CopyTexture.html)
and [Texture2D.Apply](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/Texture2D.Apply.html).

Before creating our native decal manager, `NativeDecalRepairs` collects distinct
readable compressed `_BumpMap` textures from the validated, active owned decals and
calls Apply(false,true). This discards the unnecessary CPU copies while preserving
the uploaded pixels and authored mip chain. The native region-copy path, material
assignments and destination arrays remain intact. No generic Graphics patch or log
filter; no scene bundle rebuild. Sources remain bundle-owned and ordinary maps are
not scanned. Known source sets contain nine Equator and seventeen Bay5 normal maps.

## Other warning/error audit

- **Owned Harmony warning:** InitWeather was reflected via LocalGame although declared
  on closed BaseLocalGame<EftGamePlayerOwner>. Resolve the exact declaring method with
  its TimeAndWeatherSettings signature; retain the existing prefix logic.
- **Equator duplicate LOD groups:** accepted source object 1351, refrigerator_drink (2),
  references shadow renderers 1353/1358 that also belong to nested LOD groups. Warnings
  occur during scene activation, before our sceneLoaded repairs. Fixing their initial
  warnings requires a scoped importer change and Equator scene rebuild; left visible.
- **Inactive MenuScreen coroutine:** appears immediately after Escape opens the pause
  menu, before debug disconnect or ArenaMapPort teardown. Seen since September. No
  ArenaMapPort MenuScreen hook exists; exact native/other-mod cause remains unproven.
  Do not label it harmless or suppress it without a stack/owner investigation.
- Optional Fika dependency messages and Harmony warnings from DynamicMaps/UIFixes/WTT
  belong to other installed mods. Server warnings concern unavailable trader/profile
  fallbacks elsewhere. No ArenaMapPort server warnings/errors in the baseline log.

## Verification

Initial Release build: zero warnings/errors. Existing C# suite: 615 tests passed.
Native acceptance, final build identities and deployment evidence follow after testing.

### Final Release and GPU-2 acceptance — 2026-10-11

Implementation commit **c102b6c** was committed before the final build. The approved
gates reran the full **615-test C# suite** and client Release build successfully
(client: zero warnings/errors; existing test-project nullable warnings remain).
No bundles or map-data files were rebuilt. SHA-256 of the accepted pair:

- Client: `DA1E11A783CC73F0307AB7C7F99903EF730AED3ACB55BA271F6DDE8ED2EADC35`
- Server: `C35732E8D2436006119154A4BD516027E341D3CAF9C63A4FD294C7E72A79967B`

GPU-2 normal-mode process **d5422b2e**, started 11:15:36, reported all 20 mod
identities identical between local build, host disk and loaded process. Its public
configuration contained only NaturalEquatorNight=false and DeveloperMode=false.
Botless sequence: **Equator Day/Clear -> Bay5 -> Equator Day/Clear**, followed by
MainMenu after each raid.

| Raid | Prepared normal maps | Active/registered decals | Native groups | CopyTexture warnings |
| --- | ---: | ---: | ---: | ---: |
| Equator, 11:16:42 | 9 | 346 / 346 | 23 | 0 |
| Bay5, 11:18:50 | 17 | 468 / 468 | 36 | 0 |
| Equator again, 11:19:54 | 9 | 346 / 346 | 23 | 0 |

The read-only runtime inspector independently confirmed the native manager counts
346/468. Equator source texture `4c62178b551ed415` is now non-readable while keeping
DXT5 / RGBA_DXT5_UNorm, 512x256 and ten mips. Every teardown removed the manager;
Equator released 23 materials/25 command buffers, Bay5 36/38. No manager was found
in the menu after any of the three raids.

Representative graffiti remained visible: Equator wall at (6.5,0.1,-28.95), yaw 90,
and Bay5 hanging warehouse tarp from (124,-3.9,45.2), looking at (129.4,-1.4,45.8).
Screenshots `equator-decals.png`, `bay5-decals.png` and `equator-repeat.png` were
captured and visually inspected under ignored
`analysis/screenshots/release-logging-2026-10-11/`. This is representative visual
coverage, not an exhaustive comparison of all 814 active decals.

The complete closed-process log, copied to ignored
`work/release-logging/normal-full.log` (11:15:28.772–11:21:15.345), contains exactly
**one ArenaMapPort Info**, **546 Debug**, **zero Warning/Error/Fatal**. Info is the
concise version/map summary. CopyTexture and our inherited-method Harmony warning
are absent. Developer-only probes, per-renderer/rejection dumps, hierarchy enumeration
and room mixer diagnostics are absent in normal mode. The unfiltered bridge capture
`normal-native-all.json` contains 3429 entries, including 480 ArenaMapPort Debug;
it begins after plugin startup, so the complete file is required for startup claims.

Remaining across all sources in that full normal-mode process: **16 warnings and
one error** — four optional Fika assembly exclusions, four other-plugin Harmony
warnings, eight refrigerator duplicate-LOD warnings (four per Equator load), and
the MenuScreen coroutine error at 11:17:43.464, eight milliseconds after Escape.
ArenaMapPort teardown began only at 11:17:53.915. These known findings remain visible.

### Developer diagnostics and already-prepared texture reuse

DeveloperMode was temporarily enabled through UiBridge's normal F12-setting API,
then EFT restarted. Process **8e4cfae9**, started 11:21:58, again had 20/20 identical
loaded files. On Equator, room-mixer diagnostics appeared at Debug and F9 produced
the LightingLab Snapshot at **11:23:14.401**, also Debug. No ArenaMapPort warning/error.

The existing Decals (arena_equator)/Enabled control was switched off and back on
through the same API. Off removed the manager. On at **11:23:47.764** registered
all 346 decals/23 groups with **zero new CPU copies to release**, proving that
already-prepared source textures can be reused without warning. Both tested settings
were restored (Decals=true, DeveloperMode=false) before exiting. Unfiltered evidence:
`work/release-logging/developer-native-all.json` (3077 entries). The bridge truncates
individual long records at 2000 characters; full BepInEx files retain full F9 reports.

The sanctioned DLL-only script was dry-run, confirmed and hash-verified for
`D:/SPT41`, then repeated for `D:/SPT41vanilla` after native acceptance. Only the
client/server pair changed; bundle/data/config deployment was not requested.
SPT41vanilla's game/server restart remains the user's step.

### Server identity and final restoration

The first server startup at 09:15 UTC still emitted the old messages: the server had
loaded before Syncthing delivered its new DLL. Local bin/dist/mirror hashes alone
did not prove the remote process identity. No rebuild was needed. After a second
restart, the GPU-2 disk was read directly over SMB and matched the accepted server
hash above. Its current server log, copied to
`work/release-logging/server-native-current.log`, proves the new behavior from
**09:25:02 UTC**: **two Info**, **four Debug**, **zero own Warning/Error**. The two
Info messages announce Equator/Bay5 availability; banner routing, fixed time and
APBS compatibility are Debug. Six warnings from other mods/profile fallbacks and
zero errors remained in that captured new interval.

Final normal-mode client **1091e9eb**, started 11:25:23 local, confirmed 20/20
matching mod identities and exactly two public settings, both false. The built-in
compaction preserved the temporary developer-mode settings in its normal archive;
no config file was edited by hand. EFT was then closed gracefully; the verified
server remains running. `work/release-logging/deployed-hashes.json` records matching
dist/SPT41/SPT41vanilla client and server hashes. Final documentation is committed
separately; do not rebuild merely because that documentation commit changes HEAD.
