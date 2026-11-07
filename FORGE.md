# ArenaMapPort

Bring **Equator, Bay5, Air Pit, Bowl and Iceberg from Escape from Tarkov: Arena** into SPT.

## Five maps, six atmospheres

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
- **Iceberg:** the resort hotel on a rocky canyon, with a rotunda, lobby, atrium, pool hall and cafe
  inside and a street area around it, in Arena's CheckPoint layout: two team pockets behind sliding
  gates at the east and west ends and three capture points between them. The sea below the canyon
  uses the same native water as Bay5's harbor. It uses the server's time of day and weather, as on
  Bay5, and the two gates as extractions. It has no spectator crowd and no ported grass.

All five maps include bot navigation, original environmental audio, material
sounds, room acoustics and decals; Equator and Bay5 also have restored dust, and
Equator, Bay5 and Bowl original lamp flares (Air Pit and Iceberg have none). Gunshots ring
differently in a big hall than in a small shop: the per-room gunshot reverb is on by default on
all five maps (its room data is invented) and can be switched off per map with DeveloperMode
enabled (enable it and restart EFT).

Iceberg is new in 0.5.0. Arena's data holds a few lamps far brighter than on any other map, so at
raid start the port lowers the strongest of them to a limit of 4.8 and lifts the dimmest to a floor
of 1.9, and restores the originals when the raid ends (values the author chose by eye; changeable
with DeveloperMode). Its ambient mix is a first choice, not yet listened through.

Since 0.4.0 the original animated spectators fill the stands of Equator, Bay5, Air Pit and Bowl
(four of the five maps), with spatial crowd calls (one every 4 to 8 seconds) and a quiet crowd
murmur from each group of spectators. Since 0.5.0 the murmur is Arena's own neutral in-match crowd
bed (0.4.0 used its postmatch recording). The crowds can also answer match events: a mode plugin
asks through the `SpectatorCrowd` contract, and one to three original fan clips (70 in all) answer,
and the stands add one of Arena's louder cheering or angry crowd recordings. ArenaMode 0.4.0 and later uses the contract and drives the reactions from its match events; a plain raid
without a mode sends no event, so nothing reacts there. The crowds use about
0.5 GB of video memory per raid on each crowd map and add about two seconds to raid loading (see
Known limitations).

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
are not encrypted). The keys are built for Arena 0.6.1.0.48206 and were checked against
0.6.2.0.48473 (all five maps); the four older maps also open on 0.6.0.0.47752, Iceberg was not checked there;
after an Arena update that changes a map's resource files, that map waits for an updated
ArenaMapPort package with the key for that build; the other maps keep working.

Designed for **ArenaMode**, installed separately. ArenaMapPort provides the
maps; ArenaMode provides its match rules and respawns. No loot or scav raids
are included. Fika compatibility has not been verified.

**DynamicMaps is optional:** overview maps for all five maps are included
and load automatically when it is installed. Air Pit and Bowl show their ground floors;
their upper spectator areas are omitted.

## Installation

Close EFT and the SPT server, then extract the whole release ZIP into your SPT directory.
Merge both `BepInEx` and `SPT_Runtime`, then restart the server and game. The release's
`.sha256` file lists the checksum of the ZIP.

The ZIP is about 5.0 GiB. Upgrading from 0.4.0 (or 0.3.0): extract the ZIP over the installation the same
way. It replaces both DLLs and adds Iceberg's files, the crowd reaction clips and the roar recordings. The
old postmatch murmur file of 0.4.0 is no longer used and can stay or be deleted. From 0.3.0 it also replaces
the Equator scenes bundle and adds the spectator, gate-sound and score-monitor files.

Updating a development build? Move the old `BepInEx/plugins/ArenaMapPort`
directory outside `BepInEx/plugins` before extracting to avoid duplicate plugins.

## Natural Equator nights

In F12, enable **Appearance / NaturalEquatorNight** before your next raid to
remove the added neon treatment. The time remains midnight and the weather
stays unchanged. Equator Day, Bay5, Air Pit, Bowl and Iceberg are unaffected.

## Known limitations

- The intermittent short static reported during Equator play was fixed in 0.4.0 (a defect in the mod's
  own Equator bundle; the author confirmed in play on 30 October 2026 that it is gone). The Equator
  bundle of 0.3.0 carries the defect, so upgrading from it replaces that bundle.
- EFT's own sound bundle has no impact sounds for the Books and CardBoard prop types, so 39
  Equator props (30 Books: shoes, books, blue cups and a road cone; 9 CardBoard: pizza boxes
  and office folders) and any props of those types on the other maps stay silent when they hit
  something.
- The crowd murmur and the 4-to-8-second pauses between spectator calls date from 0.4.0; the author
  listened to them in play on 30 October 2026 and approved them. The crowd reactions and the cheering
  and angry recordings were heard by the author only in the developer demo (7 November 2026, found
  good), on Equator and Bowl; a plain raid without a mode sends no match events.
- Iceberg has had fewer raids on the test host than the older maps. Its ambient mix, its gate sounds
  and the lamp limits are first choices, the last build has not been through an Iceberg raid on the
  test host yet, and it has no spectators, no per-light flares and no ported grass.
- The crowds decode 123 shared images into about 0.5 GB of video memory per raid on
  each of the four crowd maps and add about two seconds to raid loading. The GitHub release
  notes explain how to skip a map's crowd.
- Fika compatibility has not been verified.
