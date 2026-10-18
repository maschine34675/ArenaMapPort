# ArenaMapPort

**Equator, Bay5, Air Pit and Bowl from Escape from Tarkov: Arena, rebuilt for SPT.**

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
| Air Pit | An abandoned terminal hangar: the furnished main hall with its four shops, the passport hall and the apron. Two normal server-time choices, a progressing day/night cycle and dynamic weather, like Bay5. Two gates serve as extractions. |
| Bowl | An open-air stadium arena: the sunken stadium field with a crashed Mi-26 in the middle, a gated team pocket at the east and at the west end, containers and guard towers. The spectator stands are scenery and no grass is ported. Two normal server-time choices, a progressing day/night cycle and dynamic weather, like Bay5. Two gates serve as extractions. |

All four maps include imported geometry, materials, lighting, collision and bot
navigation, with restored ambience, material sounds, room acoustics and decals.
Equator and Bay5 also have restored dust and original lamp flares. Bay5 includes
its harbor background and water.

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
Equator Day, Bay5, Air Pit and Bowl are unaffected.

Night PostFX are temporary and do not overwrite your saved game settings.
The colored volumetric haze requires the game's **Volumetric Light** option.

**General / DeveloperMode** is visible with the F12 **Advanced settings** filter.
It is off by default; enabling it and restarting exposes development controls
and diagnostics. Normal installations keep a compact configuration file.
Existing developer tuning is preserved in `com.maschine.ArenaMapPort.developer.cfg`
and remains active when developer mode is off.

Per-room gunshot reverb (**RoomShotAcoustics**) is on by default on all four
maps: shots ring differently in a big hall than in a small shop. Its room data is
invented, because neither Escape from Tarkov nor Arena provides gunshot reverb
values. It can be switched off per map under `Audio (arena_equator)`,
`Audio (arena_bay5)`, `Audio (arena_airpit)` or `Audio (arena_bowl)` with
**General / DeveloperMode** enabled (enable it and restart EFT, the keys then appear in F12); switching it off
restores the shipped gunshot acoustics.

## DynamicMaps

Overview maps for Equator, Bay5 and Air Pit are included. If DynamicMaps is
installed, open inventory → **MAP** during a raid.

ArenaMapPort loads these maps from its own directory; you do not need to copy
anything into DynamicMaps. The overviews are simplified ground-floor plans; Air
Pit's upper spectator area is deliberately omitted. Bowl does not have an overview
map yet.

## Scope

The release contains **Equator, Bay5, Air Pit and Bowl**. There is no loot or scav-raid
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
