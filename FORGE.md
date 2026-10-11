# ArenaMapPort

Bring **Equator and Bay5 from Escape from Tarkov: Arena** into SPT.

## Two maps, three atmospheres

- **Equator Day:** clear weather, a fixed noon start and natural lighting.
- **Equator Night:** a rainy midnight variant with neon lights, colored
  chemlights, haze and reflective floors. A natural look is available in settings.
- **Bay5:** the harbor arena with its surrounding scenery, normal time
  progression and dynamic weather.

Both maps include bot navigation, original environmental audio, material
sounds, room acoustics, decals, dust and lamp flares.

## Compatibility

Tested with **SPT 4.1.6 / EFT 0.16.9.5-40743**.

Designed for **ArenaMode**, installed separately. ArenaMapPort provides the
maps; ArenaMode provides its match rules and respawns. No loot or scav raids
are included. Fika compatibility has not been verified.

**DynamicMaps is optional:** overview maps for both locations are included
and load automatically when it is installed.

## Installation

Close EFT and the SPT server, then extract the ZIP into your SPT directory.
Merge both `BepInEx` and `SPT_Runtime`, then restart the server and game.

Updating a development build? Move the old `BepInEx/plugins/ArenaMapPort`
directory outside `BepInEx/plugins` before extracting to avoid duplicate plugins.

## Natural Equator nights

In F12, enable **Appearance / NaturalEquatorNight** before your next raid to
remove the added neon treatment. The time remains midnight and the weather
stays unchanged. Equator Day and Bay5 are unaffected.
