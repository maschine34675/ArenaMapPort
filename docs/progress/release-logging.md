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
