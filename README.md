# ArenaMapPort

**Equator and Bay5 from Escape from Tarkov: Arena, rebuilt for SPT.**

The release includes the map assets, client plugin and server component. It is
designed for use with **ArenaMode**, installed separately; ArenaMapPort itself
does not add Arena match rules.

[Download the latest release](https://github.com/maschine34675/ArenaMapPort/releases)

## Maps

| Map | Time and atmosphere |
|---|---|
| Equator Day | Fixed 12:00 start, clear weather and natural daytime lighting. |
| Equator Night | Fixed 00:00 start, rain, neon lights, colored chemlights, haze and reflective floors. A natural appearance is available in settings. |
| Bay5 | Two normal server-time choices, a progressing day/night cycle and dynamic weather. |

Both maps include imported geometry, materials, lighting, collision and bot
navigation, with restored ambience, material sounds, room acoustics, decals,
dust and lamp flares. Bay5 includes its harbor background and water.

## Compatibility

- Tested with **SPT 4.1.6 / EFT 0.16.9.5-40743**. The server declares `~4.1.5`.
- Install **both** the client and server components.
- **ArenaMode** is the intended gameplay companion and is installed separately.
  It is not required to load the maps.
- **DynamicMaps** is optional; integration was tested with version **1.2.1**.
- **SAIN** is optional; location compatibility is handled when it is installed.
- Fika compatibility has not been verified.

The release ZIP contains the built maps. Players do not need to run the export
tools or install Unity.

## Installation

1. Close EFT and the SPT server.
2. Extract the release ZIP into your SPT installation, merging its `BepInEx`
   and `SPT_Runtime` directories.
3. Start the SPT server, then EFT.

The installed folders are:

- `BepInEx/plugins/maschine-ArenaMapPort`
- `SPT_Runtime/user/mods/ArenaMapPort`

When upgrading an older development package, move the former
`BepInEx/plugins/ArenaMapPort` directory outside `BepInEx/plugins` before
extracting, so only one client is installed. Keep your configuration files.
The package README includes cleanup instructions for retired debug-helper and
DynamicMaps development files.

## Settings

In the F12 configuration menu, enable **Appearance / NaturalEquatorNight** to
use the natural appearance for your next Equator midnight raid. This disables
the added neon lights, chemlights, smoke/haze, temporary PostFX and extra
wet-floor reflections. Time, weather and original lamps remain unchanged.
Equator Day and Bay5 are unaffected.

Night PostFX are temporary and do not overwrite your saved game settings.
The colored volumetric haze requires the game's **Volumetric Light** option.

**General / DeveloperMode** is visible with the F12 **Advanced settings** filter.
It is off by default; enabling it and restarting exposes development controls
and diagnostics. Normal installations keep a compact configuration file.
Existing developer tuning is preserved in `com.maschine.ArenaMapPort.developer.cfg`
and remains active when developer mode is off.

## DynamicMaps

Overview maps for Equator and Bay5 are included. If DynamicMaps is installed,
open inventory → **MAP** during a raid.

ArenaMapPort loads these maps from its own directory; you do not need to copy
anything into DynamicMaps. The overviews are simplified ground-floor plans.

## Scope

The release contains **Equator and Bay5 only**. There is no loot or scav-raid
support, and these locations are not intended as ordinary quest/progression
maps. Arena match rules and respawns come from ArenaMode.

## Development

Game-derived assets are distributed in release packages and are not stored in
Git. Building the maps requires local Arena/SPT inputs and Unity 2022.3.43f1.
The repository also retains development tooling for maps outside the release.

- [Development instructions](AGENTS.md)
- [Current working state](HANDOFF.md)
- [Build and validation history](docs/progress/INDEX.md)
- [Third-party notices](THIRD-PARTY-NOTICES.md)
