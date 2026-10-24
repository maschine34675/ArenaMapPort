# ArenaMapPort

Bring **Equator, Bay5, Air Pit and Bowl from Escape from Tarkov: Arena** into SPT.

## Four maps, five atmospheres

- **Equator Day:** clear weather, a fixed noon start and natural lighting.
- **Equator Night:** a rainy midnight variant with neon lights, colored
  chemlights, haze and reflective floors. A natural look is available in settings.
- **Bay5:** the harbor arena with its surrounding scenery, normal time
  progression and dynamic weather.
- **Air Pit:** an abandoned terminal hangar with the furnished main hall and its
  four shops, the passport hall and the apron. Normal time progression with both
  time phases, dynamic weather, and two gates as extractions.
- **Bowl:** the stadium arena with its sunken field, a crashed Mi-26 in the middle, containers, guard
  towers and a gated team pocket at each end. Normal time progression with both time phases, dynamic
  weather, and two gates as extractions. The spectator stands are scenery and no grass is ported.

All four maps include bot navigation, original environmental audio, material
sounds, room acoustics and decals; Equator and Bay5 also have restored dust and
lamp flares. Gunshots ring differently in a big hall than in a small shop: the
per-room gunshot reverb is on by default on all four maps (its room data is
invented) and can be switched off per map with DeveloperMode enabled (enable it and restart EFT).

The gates and vehicles at the team spawn enclosures use Arena's own open positions and,
where Arena has them, its motions (at half their time). They stand open from raid start, or a
mode plugin such as ArenaMode holds them closed and opens them when the fight starts; Air Pit's
passport control stays shut until a mode plugin opens it. A few doors that Arena itself never moves
(three warehouse rollers on Bay5, the FCK shop's roller shutter on Equator) stay shut.

## Compatibility

Tested with **SPT 4.1.6 / EFT 0.16.9.5-40743**.

Requires an installed **Escape from Tarkov: Arena**, which is sold separately from
Escape from Tarkov. The maps are built from its files, so the server lists them and the
client loads them only when the Battlestate Games launcher's Arena installation is found
and complete (the same way SPT needs an installed Escape from Tarkov); without it the
Arena locations do not appear, and a message at the main menu then names the reason
and what to do. For an installation the launcher did not register, set its
folder in `General / ArenaInstallPath` (client) and in
`SPT_Runtime/user/mods/ArenaMapPort/config/arena-install.json` (server); the check still
runs in full. The map bundles are encrypted with a key derived from the Arena files each map
is built from, so they work only next to your own Arena files: a requirement, not copy
protection (anyone with Arena can decrypt them; the art and audio shipped beside the bundles
are not encrypted). The keys are built for Arena 0.6.1.0.48206 and also open on 0.6.0.0.47752;
after an Arena update that changes a map's resource files, that map waits for an updated
ArenaMapPort package with the key for that build; the other maps keep working.

Designed for **ArenaMode**, installed separately. ArenaMapPort provides the
maps; ArenaMode provides its match rules and respawns. No loot or scav raids
are included. Fika compatibility has not been verified.

**DynamicMaps is optional:** overview maps for all four maps are included
and load automatically when it is installed. Air Pit and Bowl show their ground floors;
their upper spectator areas are omitted.

## Installation

Close EFT and the SPT server, then extract the ZIP into your SPT directory.
Merge both `BepInEx` and `SPT_Runtime`, then restart the server and game.

Updating a development build? Move the old `BepInEx/plugins/ArenaMapPort`
directory outside `BepInEx/plugins` before extracting to avoid duplicate plugins.

## Natural Equator nights

In F12, enable **Appearance / NaturalEquatorNight** before your next raid to
remove the added neon treatment. The time remains midnight and the weather
stays unchanged. Equator Day, Bay5, Air Pit and Bowl are unaffected.
