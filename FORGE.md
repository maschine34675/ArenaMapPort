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

Version 0.4.0 adds the original animated spectators on all four maps, with
spatial crowd calls and up to two voices at once. The crowd does not react to
kills or round results yet. The crowds use about 0.5 GB of video memory per raid
and add about two seconds to raid loading (see Known limitations).

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

Close EFT and the SPT server. Download **all five ZIP parts** of the release (core,
Equator, Bay5, Air Pit and Bowl) and extract every part into your SPT directory, merging
`BepInEx` and `SPT_Runtime`. The parts can be extracted in any order and must not be joined
into one archive; a map works only with its own part and the core part, and the release's
`.sha256` file lists the checksum of each part. Then restart the server and game.

Upgrading from 0.3.0: extract all five parts over the installation, then delete
`BepInEx/plugins/maschine-ArenaMapPort/parts/full.json`, the manifest of the former single
ZIP, which this release no longer ships.

Updating a development build? Move the old `BepInEx/plugins/ArenaMapPort`
directory outside `BepInEx/plugins` before extracting to avoid duplicate plugins.

## Natural Equator nights

In F12, enable **Appearance / NaturalEquatorNight** before your next raid to
remove the added neon treatment. The time remains midnight and the weather
stays unchanged. Equator Day, Bay5, Air Pit and Bowl are unaffected.

## Known limitations

- A cause of the intermittent short static reported during Equator play was found and fixed
  in this release. It was a defect in the mod's own Equator bundle, not the game's mix and
  not an extra sound: one 128 KiB block of the Equator scenes bundle carried a compression
  flag although it was stored at full size (a boundary case in a library the mod's build
  uses), so the game played that block's raw bytes as about four seconds of static in the
  indoor wind clip, once per wind cycle. The bundle is repaired and every bundle is now
  checked for such blocks before it is staged. The Equator bundle of 0.3.0 carries the
  defect.
- The static no longer shows up in recordings of the game's sound made on the test host, but
  nobody has listened to the repaired Equator bundle yet; one playing session on Equator is
  the remaining confirmation. A faint steady hiss and short clicks seen in one recording are
  not explained by this fix.
- EFT's own sound bundle has no impact sounds for the Books and CardBoard prop types, so 39
  Equator props (30 Books: shoes, books, blue cups and a road cone; 9 CardBoard: pizza boxes
  and office folders) and any props of those types on the other maps stay silent when they hit
  something.
- The crowds decode 123 shared images into about 0.5 GB of video memory per raid on
  every map and add about two seconds to raid loading. The GitHub release
  notes explain how to skip a map's crowd.
- Fika compatibility has not been verified, and the crowd does not react to match events.
