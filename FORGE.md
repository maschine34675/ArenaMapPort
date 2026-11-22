# ArenaMapPort

Bring **Equator, Bay5, Air Pit, Bowl, Iceberg, Chop Shop and Block from Escape from Tarkov: Arena** into SPT.

## Seven maps, eight atmospheres

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
  weather, and two gates as extractions. The spectator stands are scenery, and Arena's own grass grows at its walls and lawns (since 0.7.0).
- **Iceberg:** the resort hotel on a rocky canyon, with a rotunda, lobby, atrium, pool hall and cafe
  inside and a street area around it, in Arena's CheckPoint layout: two team pockets behind sliding
  gates at the east and west ends and three capture points between them. The sea below the canyon
  uses the same native water as Bay5's harbor. It uses the server's time of day and weather, as on
  Bay5, and the two gates as extractions. It has no spectator crowd; Arena's own grass grows along its walls and kerbs (since 0.7.0).
- **Chop Shop:** a garage hall with a walled street yard, in Arena's TeamFight layout: a team pocket
  at each end, one capture point and four moving gates, with Arena's own spectator crowd in the
  stands. It uses the server's time of day and weather, as on Bay5 (summer set only, no winter
  look), and the two gates as extractions. It includes a DynamicMaps overview (since 0.7.0); it has no native water.
- **Block:** a residential courtyard between apartment houses (Arena's own name for it; shown in the game as
  "Arena: Block"), in Arena's TeamFight layout: two sealed team pockets about 130 m apart, one behind a rising
  facade gondola and one behind two swinging gate leaves, and one capture point, with Arena's own spectator
  crowd on its balconies. It uses the server's time of day and weather, as on Bay5 (summer set only, no winter
  look), and the two gates as extractions. Arena's grass grows on the lawns of the courtyard. It has
  a DynamicMaps overview including both team pockets and raised routes; it has no lamp flare or native water.

All seven maps include bot navigation, original environmental audio, material
sounds, room acoustics and decals; Equator and Bay5 also have restored dust, and
Equator, Bay5, Bowl and Chop Shop original lamp flares (Air Pit, Iceberg and Block have none). Gunshots ring
differently in a big hall than in a small shop: the per-room gunshot reverb is on by default on
all seven maps (its room data is invented; since 0.6.0 the big halls ring weaker and much shorter,
about 2.5 s instead of about 6.5 s at 1500 Hz in the main halls of Equator and Air Pit; approved by the author's ear on 14 November 2026) and can be switched off per map with DeveloperMode
enabled (enable it and restart EFT).

Block is new in 0.7.0 (Arena's `Arena_Yard`). The author looked at the map and listened to it on 21 November 2026,
night included, and approved it ("sieht gut aus, auch nachts, ist dann halt wie die anderen maps im außenbereich
recht dunkel. hört sich gut an, ambient sounds können etwas lauter sein (gain auf 5, range auf 3)"); the statement
covers the look and the sound as a whole and names no single item, and Block's ambient mix (gain 5 dB, range 3) is the
author's own by ear. The author looked at it again on 22 November 2026 and found it fine ("Block passt"; no single
item named).

Native grass is new in 0.7.0 on Bowl, Iceberg and Block: Arena's own tufts with seed stalks along walls, kerbs and
lawns, on by default (no setting), from a fourth encrypted file per map (the grass code reads no file from your Arena folder; the file opens with the map's key, so the installed
Arena is still required). It has no collision, no footstep or bullet effect, is pressed flat when walked over and sways with the weather's wind, and it cost
about 0.13 to 0.27 ms per frame on the test host (1080p, frame limiter lifted; measured before the sway was added). Rain on the screen is new in 0.7.0 on every map: in rain, in
the open and not under a roof, drops run down the screen (it was missing on every ported map before; under a roof there
are none; no setting). The author has seen the rain drops and found them fine ("Block passt, Regentropfen auch",
22 November 2026); they were checked on the test host on Block and Equator only. The author has seen the grass, which first
stood still in the wind, and asked for the sway; the sway was checked on the test host by a frame comparison on the
three grass maps and the author has not seen it.

Chop Shop is new in 0.6.0 (Arena's `Arena_AutoService`, shown in the game as "Arena: Chop Shop"). Its
ambient mix is a computed start value; the author listened to the map on 14 November 2026 and asked for no change (he said everything
fits; the statement names no single item).

Iceberg joined in 0.5.0. Arena's data holds a few lamps far brighter than on any other map, so at
raid start the port lowers the strongest of them to a limit of 4.6 and lifts the dimmest to a floor
of 1.8 (4.8 and 1.9 in 0.5.0; a saved pair of those moves once), and restores the originals when the raid ends (values the author chose by eye; changeable
with DeveloperMode). Its ambient mix is a first choice, not yet listened through.

Since 0.6.0 the six older maps have more Bot spawn rows for mode plugins, and their two exits stand in the team
start areas (Block has its own 18 Bot rows and two exits in its pockets from the start) with their green flares (a plain raid spawns you a few metres from an 8-second exit;
in the two ArenaMode Team Deathmatch runs on Bowl and Iceberg, ArenaMode hid the extraction and nobody extracted; bots never use exits).
On Air Pit the north gate cannot be reached in a plain raid, because the passport gates stay shut until a mode
plugin opens them. On Iceberg, the first spawn at some start points can stand within 2 m of an exit flare and
look tinted green.

Since 0.4.0 the original animated spectators fill the stands of Equator, Bay5, Air Pit and Bowl, and
since 0.6.0 those of Chop Shop and since 0.7.0 those of Block (six of the seven maps), with spatial crowd calls (one every 4 to 8 seconds) and a quiet crowd
murmur from each group of spectators. Since 0.5.0 the murmur is Arena's own neutral in-match crowd
bed (0.4.0 used its postmatch recording). The crowds can also answer match events: a mode plugin
asks through the `SpectatorCrowd` contract, and one to three original fan clips (70 in all) answer,
and the stands add one of Arena's louder cheering or angry crowd recordings. ArenaMode 0.4.0 and later uses the contract and drives the reactions from its match events; a plain raid
without a mode sends no event, so nothing reacts there. The roar beds are exported 6 dB louder than in 0.5.0 (since 0.6.0);
that level has not been listened to. The crowds use about
0.54 GB of video memory per raid on each crowd map and add about two seconds to raid loading (see
Known limitations).

The gates and vehicles at the team spawn enclosures use Arena's own open positions and,
where Arena has them, its motions (at half their time). They stand open from raid start, or a
mode plugin such as ArenaMode holds them closed and opens them when the fight starts; Air Pit's
passport control stays shut until a mode plugin opens it. A few doors that Arena itself never moves
(three warehouse rollers on Bay5, the FCK shop's roller shutter on Equator) stay shut. Block's two gates are a
rising facade gondola and two swinging leaves.

## Compatibility

Tested with **SPT 4.1.6 / EFT 0.16.9.5-40743**.

Chop Shop and Block are registered as locations in SPT, but whether and when ArenaMode offers them in its
matches is up to ArenaMode. This project has run no ArenaMode match on Chop Shop or Block.

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
are not encrypted). The keys are built for Arena 0.6.1.0.48206 (the key files of Chop Shop and Block for 0.6.2.0.48473), and the key files of
the six older maps were checked on 0.6.1.0.48206, 0.6.2.0.48473 and for 0.6.0 the newer hotfixes 0.6.2.0.48598 and 0.6.2.0.48767, byte-identical to 0.6.0's files (Block's on 0.6.1.0.48206 and 0.6.2.0.48473 only); the four older maps also open on 0.6.0.0.47752; Iceberg, Chop Shop and Block were not checked there;
after an Arena update that changes a map's resource files, that map waits for an updated
ArenaMapPort package with the key for that build; the other maps keep working.

Designed for **ArenaMode**, installed separately. ArenaMapPort provides the
maps; ArenaMode provides its match rules and respawns. No loot or scav raids
are included. Fika compatibility has not been verified.

**DynamicMaps is optional:** overview maps for all seven maps are included and load automatically
from ArenaMapPort's own `dynamicmaps` folder; DynamicMaps' installation stays untouched.
All seven overviews were opened in game on the test host with DynamicMaps 1.3.0. The 1.2.1 path was checked
offline against the actual DLL and has not been run in game since the loader changed. Air Pit and Bowl show their
ground floors; their upper spectator areas are omitted. Block combines its connected raised routes
and both team pockets in one overview.

## Installation

Close EFT and the SPT server, then extract the whole release ZIP into your SPT directory.
Merge both `BepInEx` and `SPT_Runtime`, then restart the server and game. The release's
`.sha256` file lists the checksum of the ZIP.

The ZIP is about 7.3 GiB (the planned size; the final figure is in the release notes). Upgrading from 0.6.0: extract the whole ZIP over the
installation the same way (keep `BepInEx/config`); it replaces both DLLs and adds Block's files, the grass files of Bowl, Iceberg and Block and
the overview files for Chop Shop and Block, so take everything from the ZIP (replacing only the DLLs gives a missing-file warning and no grass).
Upgrading from 0.5.0: extract the whole ZIP over the installation the same way (keep
`BepInEx/config`). It replaces both DLLs and adds Chop Shop's files; it also replaces the other maps' server data, the crowd
graphs together with the shared texture manifest, Air Pit's changed bundles and the roar recordings, so take everything from the ZIP.
Upgrading from 0.4.0 (or 0.3.0): the same; it also adds Iceberg's files and the crowd reaction clips. The
old postmatch murmur file of 0.4.0 is no longer used and can stay or be deleted. From 0.3.0 it also replaces
the Equator scenes bundle and adds the spectator, gate-sound and score-monitor files.

Updating a development build? Move the old `BepInEx/plugins/ArenaMapPort`
directory outside `BepInEx/plugins` before extracting to avoid duplicate plugins.

## Natural Equator nights

In F12, enable **Appearance / NaturalEquatorNight** before your next raid to
remove the added neon treatment. The time remains midnight and the weather
stays unchanged. Equator Day, Bay5, Air Pit, Bowl, Iceberg, Chop Shop and Block are unaffected.

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
  and the lamp limits (4.6 and 1.8) are first choices, the 0.6.0 lamp values ran in one Iceberg raid on the
  test host, and it has no spectators and no per-light flares.
- Chop Shop has had raids on the test host with and without bots (no warning), but fewer than the older maps. Its ambient mix
  (computed start values) stays: the author listened to the map on 14 November 2026 and asked for no change (no single item named); the louder roar beds
  have not been listened to; whether a player can climb
  the tribune decks, the rain readback and a raid with ArenaMode from this project are not checked.
- Block has had raids on the test host with and without bots, a gate hold and release, and PMC squads, with no warning, but
  fewer than the older maps. The author approved its look and sound as a whole and looked at it again on 22 November 2026; its gate sounds, crowd calls and reactions
  were not listened to one by one. Not checked: the north gate leaves seen from the north pocket, a long raid with every
  wave, a player at one of its invisible border walls, its sun shadows at the lowest shadow quality, and any ArenaMode match
  (no ArenaMode match ran on it).
- Grass (Bowl, Iceberg, Block) and the rain drops on the screen (all maps) were checked on the test host only, the drops
  on Block and Equator. The author has seen the rain drops and the still grass, not the sway. Not checked: a missing or damaged grass file, a scope view, the lowest
  texture quality, DLSS/FSR, Bowl and Iceberg at night and in rain, the sway at calm weather and by the author's eye, and the glasses and wet ground in rain.
- The appended spawn rows and the exits in the team start areas ran in plain raids on all six older maps and in two ArenaMode Team
  Deathmatch runs (Bowl, Iceberg); a respawn onto an in-box marker, other ArenaMode modes with the new rows and the flares seen
  from across a map are not checked.
- The crowds decode 135 shared images into about 0.54 GB of video memory per raid on
  each of the six crowd maps and add about two seconds to raid loading. The GitHub release
  notes explain how to skip a map's crowd.
- Fika compatibility has not been verified.
