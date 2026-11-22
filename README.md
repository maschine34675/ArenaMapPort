# ArenaMapPort

**Equator, Bay5, Air Pit, Bowl, Iceberg, Chop Shop and Block from Escape from Tarkov: Arena, rebuilt for SPT.**

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
| Bowl | An open-air stadium arena: the sunken stadium field with a crashed Mi-26 in the middle, a gated team pocket at the east and at the west end, containers and guard towers. The spectator stands are scenery. Arena's own grass grows at the walls and lawns (since 0.7.0). Two normal server-time choices, a progressing day/night cycle and dynamic weather, like Bay5. Two gates serve as extractions. |
| Iceberg | The Iceberg resort hotel on a rocky canyon: a multi-storey hotel with a rotunda, with its lobby, atrium, pool hall and cafe inside and a street area around it. It uses Arena's CheckPoint layout: two team pockets behind sliding gates at the east and west ends and three capture points (A, B and C) between them. The sea below the canyon uses the same native water as Bay5's harbor. It uses the server's time of day and weather, as on Bay5; raids were run at noon and in the evening, clear. The east and west gates serve as extractions. Arena's own grass grows along its walls and kerbs (since 0.7.0). There is no spectator crowd. |
| Chop Shop | A garage hall with a walled street yard, in Arena's TeamFight layout: a team pocket at the east end and one at the west end, one capture point and four moving gates. It uses the server's time of day and weather, as on Bay5 (summer set only: no winter or snow look). Arena's own spectator crowd fills its stands. The east and west gates serve as extractions. A DynamicMaps overview is included (since 0.7.0); there is no native water. |
| Block | A residential courtyard between apartment houses, in Arena's TeamFight layout (Arena's own name for it is "Block"; its internal name is `Arena_Yard`; shown in the game as "Arena: Block"): two sealed team pockets about 130 m apart, one behind a rising facade gondola at the south end and one behind two swinging gate leaves at the north end, and one capture point. It uses the server's time of day and weather, as on Bay5 (summer set only: no winter or snow look). Arena's own spectator crowd fills its balconies and stands. Arena's grass grows on the lawns of the courtyard. The south and north gates serve as extractions. A DynamicMaps overview is included (since 0.7.0). There is no native water and no lamp flare. |

All seven maps include imported geometry, materials, lighting, collision and bot
navigation, with restored ambience, material sounds, room acoustics and decals.
Equator and Bay5 also have restored dust. Equator, Bay5, Bowl and Chop Shop have original
lamp flares; Air Pit, Iceberg and Block do not. Bay5 includes its harbor background and
water, and Iceberg its sea. Bowl, Iceberg and Block show Arena's own grass (below).

Block is new in 0.7.0 (Arena's `Arena_Yard`). The author looked at the map and listened to
it on 21 November 2026, night included, and approved it ("sieht gut aus, auch nachts, ist dann
halt wie die anderen maps im außenbereich recht dunkel. hört sich gut an, ambient sounds können
etwas lauter sein (gain auf 5, range auf 3)", meaning: looks good, also at night, though dark outdoors like the other
maps; sounds good, the ambient sounds can be a little louder): the statement covers the look and the sound as a
whole and names no single item, and the ambient sound mix of Block (gain 5 dB, range 3) is the
author's own by ear. Like the other maps outdoors, it is rather dark at night. Block's gate sounds, crowd calls and
reactions were not listened to one by one. The map was raided on the test host with and without
bots and with a gate hold and release. The author looked at it again on 22 November 2026 and
found it fine ("Block passt"); that sentence names no single item either.

Native grass is new in 0.7.0 on Bowl, Iceberg and Block (Equator, Bay5, Air Pit and Chop Shop have
none in Arena). It shows Arena's own tufts with seed stalks along walls, kerbs and lawns and is on by default;
there is no setting for it. It comes from a fourth encrypted file per map next to the map's three
bundles (`bundles/<map>_grass.bundle`, 12 to 19 MB) and the grass code itself reads no file from your Arena folder; the file opens with the same map key as the map's other
bundles, so the installed Arena is still required. The grass has no collision and no footstep or bullet effect;
it is pressed flat when a player walks over it, and it sways with the weather's wind: the port
advances the wind phase that EFT's own wind calculator would advance, and strength and direction
follow the weather. On the test host (1920 x 1080, the game's frame limiter lifted)
it cost about 0.13 to 0.27 ms per frame (measured before the sway was added); with the 120 fps limiter the frame rate stayed at the baseline.
The first version stood still in the wind; the sway was added at the author's request on 22 November
2026. It was checked on the test host by a frame comparison on Block, Bowl and Iceberg at strong wind,
and after it was added the author wrote "ja das Gras passt jetzt" (meaning 'yes, the grass is fine now',
22 November 2026; the sentence names no map, weather or view). If you replace only the DLLs
of an older installation, the log warns about the missing grass file and no grass appears:
extract the whole ZIP.

Rain on the screen is new in 0.7.0 on every map. In rain, in the open and not under a roof, drops now
run down the screen: until now the game's "camera under rain" answer was always no on a ported
map. The repair answers it from the same depth picture the falling rain already uses, so that
drops and streaks agree (under a roof or inside a hall there are no drops; under Equator's open
roof lattice there are). It needs real rain and a camera that does not look steeply down, and
there is no setting for it. The same answer also drives the water on glasses, the ripples on wet ground and
the rain on wet props, which nobody has looked at. Checked on the test host on Block and Equator only. The
author has seen the drops on the screen and found them fine ("Block passt, Regentropfen auch", 22 November 2026; the
sentence names no map).

Chop Shop is new in 0.6.0 (Arena's `Arena_AutoService`, shown in the game as "Arena: Chop Shop").
Its ambient sound mix is a computed start value; the author listened to the map on
14 November 2026 and asked for no change ("passt alles"; the statement covers the map
as it was played and names no single item). It has no native water and no
winter set.

Iceberg joined in 0.5.0. Its brightest lamps (the lobby crystal lamps and the
chandeliers, among others) are far brighter in Arena's data than on any other map, so
at raid start the port lowers the strongest of them and lifts the dimmest to a floor,
and restores the originals when the raid ends. The two limits (4.6 and 1.8 since
0.6.0; 4.8 and 1.9 in 0.5.0, a saved pair of those moves once) were
chosen by the author by eye on the author's own PC and can be changed with DeveloperMode.
The ambient sound mix of Iceberg is a first choice that has not been listened through
yet.

Since 0.6.0 the six older maps have more Bot spawn rows for mode plugins (Chop Shop 16, Equator 16,
Bay5 16, Iceberg 17, Air Pit 20, Bowl 21) and their two exits stand in the team start
areas (Block has 18 Bot rows and 4 Player rows from the start, and its two exits stand in its two pockets), marked by their green flares: in a plain raid you spawn a few metres from an
8-second exit (in the two ArenaMode Team Deathmatch runs on Bowl and Iceberg, ArenaMode
hid the extraction and nobody extracted; bots never use exits). On Air Pit the north gate
cannot be reached in a plain raid, because the passport gates stay shut until a mode plugin
opens them. On Iceberg, the first spawn at some start points can stand within 2 m of an
exit flare and look tinted green.

Since 0.4.0 the original animated spectators fill the stands of Equator, Bay5,
Air Pit and Bowl, since 0.6.0 those of Chop Shop and since 0.7.0 those of Block (six of the seven maps; Iceberg has none), with spatial crowd
calls from 13 original fan recordings (one call every 4 to 8 seconds) and a quiet
crowd murmur from each group of spectators. Since 0.5.0 the murmur is Arena's own
neutral in-match crowd bed (0.4.0 used its postmatch recording); playing it from the
stands is the port's choice. The crowds can also answer match events: through the
`SpectatorCrowd` contract a mode plugin asks them to approve, disapprove, take cover,
start or finish, and one to three voices answer with original fan clips (70 in
all) while the stands add one of Arena's louder cheering or angry crowd recordings.
ArenaMode 0.4.0 and later uses the contract and drives the reactions from its match events; a plain raid without a mode sends no such event, so nothing reacts there. The author listened to the developer demo on
7 November 2026 and found it good (the author did not report on the angry recordings
separately; the demo and the 70 clips ran on the four older crowd maps on 2 November, the roar layer only on Equator and Bowl, not on Bay5 and Air Pit; Iceberg has no crowd,
and no single reaction on Chop Shop or Block is recorded as heard apart from the author's statements about those maps as a whole). The roar beds are exported 6 dB louder than in 0.5.0 (since 0.6.0; the developer
volume key stays at 1.0); that level has not been listened to. The idle and cheering animations run independently
of match events.
Their textures are stored once for all maps (135 images, about 147 MB on disk). The crowds
decode 135 shared images into about 0.54 GB of video memory per raid on each of the
six crowd maps (Block adds no image) and add about two seconds to raid loading; the
[0.7.0 release notes](docs/releases/v0.7.0.md) explain how to skip a map's crowd.

The gates and vehicles at the team spawn enclosures use Arena's own open positions
and, where Arena has them, its motions (at half their time). They stand open from raid
start, or a mode plugin such as ArenaMode holds them closed and opens them when the
fight starts; Air Pit's passport control stays shut until a mode plugin opens it. A few doors that
Arena itself never moves (three warehouse rollers on Bay5, the FCK shop's roller shutter on Equator)
stay shut. Block's two gates are a rising facade gondola at the south and two swinging leaves at the north;
the gondola overshoots its open pose a little, as Arena's does.

## Compatibility

- Tested with **SPT 4.1.6 / EFT 0.16.9.5-40743**. The server declares `~4.1.5`.
- Install **both** the client and server components.
- Chop Shop and Block are registered as locations in SPT, but whether and when ArenaMode offers
  them in its matches is up to ArenaMode. This project has run no ArenaMode match on Chop Shop
  or Block.
- Requires an installed **Escape from Tarkov: Arena**. Arena is sold separately from
  Escape from Tarkov and the maps are built from its files, so the server registers the
  maps and the client loads them only on a PC where Arena is installed with the
  Battlestate Games launcher, the same way SPT itself needs an installed Escape from
  Tarkov. Both components find Arena through the launcher's entry in the Windows registry
  and check the installation folder: it must contain the game executable and the
  launcher's file list (`ConsistencyInfo`), and every file in that list must exist with
  the listed size (BattlEye's own files and `Logging.config` may differ in size, and the
  launcher's uninstall stub `Uninstall.exe` may be missing). The installation check compares only names and sizes, reads no game
  data, sends nothing anywhere and takes about a second. If Arena is installed but has no
  launcher entry (a copied or moved folder), set its folder in `General / ArenaInstallPath`
  in `BepInEx/config/com.maschine.ArenaMapPort.cfg` (client) and in
  `SPT_Runtime/user/mods/ArenaMapPort/config/arena-install.json` (server,
  `{"installPath": "D:\\Battlestate Games\\Escape from Tarkov Arena"}`); an override is
  verified exactly like the automatic path, and nothing turns the check off. Without a
  verified installation both components log the reason at Info; the server does not
  register the Arena locations, so they are missing from the location selection (a message
  at the main menu then names the reason and what to do), and the
  client does not load the map files. The map bundles in the release (three per map, and a fourth, the grass
  bundle, for Bowl, Iceberg and Block) are encrypted with a key
  that your PC derives from the Arena resource files each map is built from (the texture and
  mesh data of its scenes; a fixed sample of a few MB is read when the map is first used and
  never sent anywhere; the mod's `keys` folder lists the files by path and size only), so they
  work only next to your own Arena files; this is a requirement, not copy protection (anyone
  with Arena can decrypt them), and the art and audio files shipped beside the bundles are not
  encrypted). The keys in this release are built for Arena 0.6.1.0.48206 (the key files of Chop Shop and Block for
  0.6.2.0.48473), and the key files of the six older maps were checked on 0.6.1.0.48206, 0.6.2.0.48473 and, for 0.6.0, the
  newer hotfixes 0.6.2.0.48598 and 0.6.2.0.48767 (these files are byte-identical to 0.6.0's); Block's key file was checked on 0.6.1.0.48206,
  0.6.2.0.48473 and 0.6.2.0.48767; the four maps of 0.4.0 also open on
  0.6.0.0.47752, whose resource files are the same; Iceberg, Chop Shop and Block were not checked
  there (your build is in the client log at startup, `Arena: found ...`); if an Arena update changes a map's resource files, that map
  will not load (the client log names the cause) until an updated ArenaMapPort package
  carries the key for your build; the other maps keep working.
- **ArenaMode** is the intended gameplay companion and is installed separately.
  It is not required to load the maps.
- **DynamicMaps** is optional; the loader is written for **1.2.1 and 1.3.0**. On the test host with 1.3.0 all seven overviews were opened in a raid; 1.2.1 was checked offline against its DLL for this update and not run in game again. See the [1.3 compatibility record](docs/progress/dynamicmaps-130.md) and [Block's overview record](docs/progress/yard-dynamicmaps.md) for the exact scope.
- **SAIN** is optional; location compatibility is handled when it is installed.
- Fika compatibility has not been verified.

The release ZIP contains the built maps. Players do not need to run the export
tools or install Unity. It is larger than before, about 7.3 GiB, and nothing needs to be installed by hand besides it.

## Installation

1. Close EFT and the SPT server.
2. Download the release ZIP (its links are in the notes of the GitHub release) and extract
   the whole ZIP into your SPT installation, merging its `BepInEx` and `SPT_Runtime`
   directories. GitHub's automatic source-code archives do not contain the maps.
3. Start the SPT server, then EFT.

The installed folders are:

- `BepInEx/plugins/maschine-ArenaMapPort`
- `SPT_Runtime/user/mods/ArenaMapPort`

When upgrading an older development package, move the former
`BepInEx/plugins/ArenaMapPort` directory outside `BepInEx/plugins` before
extracting, so only one client is installed. Keep your configuration files.
The release's `.sha256` file lists the checksum of the ZIP. The ZIP's
package README includes cleanup instructions for old DynamicMaps and development
files; the [0.7.0 release notes](docs/releases/v0.7.0.md) cover the upgrade from 0.6.0, 0.5.0 and 0.4.0; the
[0.4.0 notes](docs/releases/v0.4.0.md) cover the retired debug helper.

## Settings

In the F12 configuration menu, enable **Appearance / NaturalEquatorNight** to
use the natural appearance for your next Equator midnight raid. This disables
the added neon lights, chemlights, smoke/haze, temporary PostFX and extra
wet-floor reflections. Time, weather and original lamps remain unchanged.
Equator Day, Bay5, Air Pit, Bowl, Iceberg, Chop Shop and Block are unaffected.

Night PostFX are temporary and do not overwrite your saved game settings.
The colored volumetric haze requires the game's **Volumetric Light** option.

**General / ShowMapNotices** is on by default. When the Arena maps are not
available (Arena is not installed or not found, the server does not offer the
maps, a map file is missing) it shows a message at the main menu with the reason
and what to do, and it names the cause after a map failed to load. Turning it off
hides only the main-menu message, the "still checking" notice and the reminder in
the location selection: the Arena check, the server's registration and the log
lines stay as they are, and the message that answers a failed raid start still
appears. The server's exact reason needs the client and the server of the same
version.

**General / DeveloperMode** is visible with the F12 **Advanced settings** filter.
It is off by default; enabling it and restarting exposes development controls
and diagnostics. Normal installations keep a compact configuration file.
Existing developer tuning is preserved in `com.maschine.ArenaMapPort.developer.cfg`
and remains active when developer mode is off.

Per-room gunshot reverb (**RoomShotAcoustics**) is on by default on all seven
maps: shots ring differently in a big hall than in a small shop. Its room data is
invented, because neither Escape from Tarkov nor Arena provides gunshot reverb
values. Since 0.6.0 the reverb of the big halls (eight rooms, among them the main halls of
Equator and Air Pit) is weaker and much shorter: in the main halls of Equator and Air Pit the tail
starts 8 to 10 dB lower at 1500 Hz and decays in about 2.5 s instead of about 6.5 s there
(about 3 to 3.5 s instead of 9 to 14 s in the low end), measured on the test host; the author listened to it on 14 November 2026 and approved it. Shops and steel bodies are unchanged.
It can be switched off per map under `Audio (arena_equator)`,
`Audio (arena_bay5)`, `Audio (arena_airpit)`, `Audio (arena_bowl)`, `Audio (arena_iceberg)`, `Audio (arena_autoservice)` or `Audio (arena_yard)` with
**General / DeveloperMode** enabled (enable it and restart EFT, the keys then appear in F12); switching it off
restores the shipped gunshot acoustics.

## If the maps do not show up

When the Arena maps are not available, ArenaMapPort shows a message at the main menu with the reason and what to do, and after a map
fails to load it names the cause. The message is in English or German, following the game's language, and its last line carries a ref
code such as `ArenaMapPort 0.7.0 - ref NO_ARENA`. Find the ref below and follow the fix. If you report a problem at
https://github.com/maschine34675/ArenaMapPort/issues, include the ref and the lines marked `[Arena]` from `BepInEx/LogOutput.log`.
The setting **General / ShowMapNotices** hides the main-menu message, the "still checking" notice and the reminder in the location selection;
it never turns the Arena check off, and the message that answers a failed raid start still appears. These notices cover installation
and map-loading failures; optional feature warnings may still appear in the log.

The server's exact reason (the `SERVER_` refs) needs the client and the server of the same version: install both parts from the same release ZIP,
with the SPT server closed (a running server keeps its DLL locked). An older server cannot say why, and the message then asks for exactly
that.

| Ref | What it means | What to do |
|---|---|---|
| `NO_ARENA` | Arena was not found on this PC. | Install Arena with the Battlestate Games launcher, then restart the game and the SPT server. Installed but not found: set its folder in `F12 > maschine-ArenaMapPort > General > ArenaInstallPath` and in the server's `arena-install.json` (see Compatibility). |
| `REGISTRY` | The Windows registry could not be read to look for Arena. | Restart the game once; if it repeats, set the Arena folder (the one with `EscapeFromTarkovArena.exe`) in `ArenaInstallPath`. |
| `OVERRIDE_BAD` | The folder set in `ArenaInstallPath` cannot be used. | Set it to the Arena folder that contains `EscapeFromTarkovArena.exe` (not the launcher folder), or clear it. Restart the game. |
| `FOLDER_BAD` | The launcher lists an Arena folder that cannot be used. | Reinstall or update Arena in the launcher, or fix the folder if you moved it. Restart the game and the server. |
| `ARENA_INCOMPLETE` | Your Arena installation looks incomplete or is still updating. | Let the launcher finish updating, or check the installation there. Arena has to be complete and up to date. The `[Arena]` log line names the first file that is missing or has a different size. If it is a file your antivirus quarantined (on one PC it was the launcher's `Uninstall.exe`, taken after every reinstall), restore it from the antivirus quarantine or let the launcher repair the installation, and tell the antivirus to leave the Arena folder alone; a missing `Uninstall.exe` on its own is tolerated since the release after 0.4.0 and no longer causes this. |
| `CHECK_FAILED` | The installation check hit an internal error. | Restart the game; if it repeats, send the `[Arena]` lines of `BepInEx/LogOutput.log`. |
| `CHECK_SLOW` | The installation check did not finish in time (slow disk or unreachable folder). | Start the raid again; if it repeats, check that the Arena folder is on a local, connected drive. |
| `HOOK_MISSING` | This ArenaMapPort build does not fit your game version. | Install the ArenaMapPort release that matches your SPT version. |
| `SERVER_NO_ARENA` | The SPT server could not verify Arena, although this game found it. | Follow the message: usually create `SPT_Runtime/user/mods/ArenaMapPort/config/arena-install.json` on the server PC (the message shows the content), fix its path or its backslashes, let the launcher finish updating Arena there, or restart the server. |
| `SERVER_UNKNOWN` | The server does not offer the maps and did not say why (its ArenaMapPort is missing or older, or could not find Arena). | Close the server, reinstall both parts from the same release ZIP, restart the server and the game. The server console says why in lines starting with `[ArenaMapPort]`. |
| `SERVER_NO_ANSWER` | The server did not answer the question why (timeout or error). | Restart the SPT server, then the game. |
| `SERVER_ALL_FAILED` | The server could not register any Arena map. | Close the server, reinstall the server files (`SPT_Runtime/user/mods/ArenaMapPort`) from the release ZIP, restart it. |
| `SERVER_PARTIAL` | The server does not offer some maps this game knows (its ArenaMapPort is probably older). | Close the server, copy the release ZIP's `SPT_Runtime` folder over the installation (overwrite its files), restart it. |
| `SERVER_PARTIAL_FAILED` | The server could not register some maps. | Close the server, reinstall the server files (`SPT_Runtime/user/mods/ArenaMapPort`) from the release ZIP, restart it. |
| `FILES_MISSING` | ArenaMapPort's files for some maps are missing (an incomplete installation). | Extract the complete release ZIP again (client folder `BepInEx/plugins/maschine-ArenaMapPort` with its `bundles` and `keys`), restart the game. |
| `KEY_UPDATED` | A map cannot be opened: your Arena is newer than the build this ArenaMapPort was made for, and files it needs changed. | Install the newest ArenaMapPort release. |
| `KEY_ARENA_OLDER` | A map cannot be opened: your Arena is older than the build this ArenaMapPort was made for. | Update Arena in the launcher. |
| `KEY_DIFFERS` | A map cannot be opened: the Arena files it is built from do not match the unlock file. | Repair Arena in the launcher (damaged or modified files); if it stays, your Arena version needs an ArenaMapPort update. |
| `KEY_UNREADABLE` | ArenaMapPort cannot read an Arena file it needs (locked, access denied). | Close the launcher and any scan of the Arena folder, check the folder's permissions. |
| `KEY_FILE_BAD` | The unlock file of a map (`keys` folder) is missing or damaged. | Reinstall ArenaMapPort and copy the complete client folder, including `keys`. |
| `KEY_INTERNAL` | An internal error while reading your Arena files. | Restart the game; if it repeats, send the `[BundleKeys]` lines of `BepInEx/LogOutput.log`. |
| `BUNDLE_BAD` | One of ArenaMapPort's map files is missing or damaged. | Reinstall ArenaMapPort and copy the complete client folder (bundles and keys included). |
| `NO_CAUSE` | A map did not start and ArenaMapPort recorded no cause. | Send `BepInEx/LogOutput.log` and the newest `errors.log` of the game's `Logs` folder. |

Four short notices appear as a toast, without a ref: "still checking your Arena installation" (a slow check; the maps load once it
passes), "the Arena maps are not available" (shown once in the location selection with the short fix), "the navigation data could not
be loaded" (a raid started without bot navigation: reinstall the complete client folder) and "a map file reported damage during the
raid" (reinstall the complete client folder).

## DynamicMaps

Overview maps for Equator, Bay5, Air Pit, Bowl, Iceberg, Chop Shop and Block are included. If DynamicMaps is
installed, open inventory → **MAP** during a raid.

ArenaMapPort loads these maps from its own directory; you do not need to copy
anything into DynamicMaps. The overviews are simplified ground-floor plans; the upper
spectator areas of Air Pit and Bowl are deliberately omitted. Iceberg's is one
overview of the connected combat routes across their different ground heights, not
a separate upper-floor map. Block similarly combines its yard, both start pockets
and connected raised routes; its west alley remains visible beyond the bot bake.

## Scope

The release contains **Equator, Bay5, Air Pit, Bowl, Iceberg, Chop Shop and Block**. There is no loot or scav-raid
support, and these locations are not intended as ordinary quest/progression
maps. Arena match rules and respawns come from ArenaMode, and the maps work only
with an installed Escape from Tarkov: Arena (see Compatibility).

## Known limitations

The intermittent short static that was reported during Equator play was fixed in
0.4.0 (a defect in the mod's own Equator bundle, see the
[0.4.0 release notes](docs/releases/v0.4.0.md)). If you still have the Equator
bundle of 0.3.0, extracting this ZIP replaces it. The crowd murmur and the
4-to-8-second pauses between spectator calls also date from 0.4.0; the author
listened to them in play on 30 October 2026 and approved them.

Iceberg has had raids on the test host (with and without bots, the hideout opened
afterwards) with no ArenaMapPort warning, but fewer than the older four maps. Its
ambient sound mix, its gate sounds and the lamp limits of 4.6 and 1.8 are first
choices (the lamp limits were set by the author by eye on the author's PC; the 0.6.0 values ran in an
Iceberg raid on the test host on 14 November 2026, the log line of the one-time move
from 4.8 and 1.9 was not captured). It has no spectators
and no per-light flares, and it is a server-time map
without a winter or snow look. Its grass (since 0.7.0) and its grass sway were looked at on the test host only; the
rain drops on the screen were checked there on Block and Equator only. The author's remarks of 22 November 2026 on
the grass and the rain drops name no map.

Chop Shop has had raids on the test host with and without bots, and the hideout opened
afterwards, with no ArenaMapPort warning, but fewer than the older maps. The author
listened to the map on 14 November 2026 and said everything fits (no single item named;
the ambient mix stays a computed start value). Not listened to: the louder roar beds. Not checked: whether a player can climb the tribune decks above the hall,
the rain-depth readback and a raid with ArenaMode from this project. It is a server-time map with a
summer set only (no winter or snow look). It has no grass in Arena, and its rain drops on the screen
(since 0.7.0) were not checked on the test host. The appended spawn rows and the exits in the
team start areas ran in plain raids on all six older maps and in two ArenaMode Team Deathmatch
runs (Bowl and Iceberg); not checked are a respawn onto an in-box marker, Team Deathmatch
on Chop Shop, Air Pit and Bay5 with the new exits, any Domination, Deathmatch, Zombies or
Tactical match with the new rows, and the flares seen from across a map.

Block has had raids on the test host with and without bots, a gate hold and release, and PMC squads at the
game's frame limit, with no ArenaMapPort warning, but fewer than the older maps. The author approved the
look and the sound of the map as a whole and looked at it again on 22 November (see above); not listened to one by one: the gate sounds, the crowd
calls, the murmur, the reactions and the room acoustics. Not checked: the north gate leaves seen and heard
from the north pocket, a long raid with every wave, a player touching one of the invisible border walls
or a bullet against one, the area south of the south pocket, the look of its two tree stand-ins, the sun shadows of its houses at the lowest
shadow quality and at hours other than 08:00, the banner, description and loading images on the test host, and the
ambient mix's one-time move for a saved start value (unit-tested only). Nobody has played an ArenaMode match on it (no ArenaMode Team Deathmatch, Domination, Deathmatch, Tactical or Zombies match
ran with its spawn rows). The grass of Bowl, Iceberg and Block was checked on the test host by day (about 0.13 to 0.27 ms
per frame at 1080p with the frame limiter lifted, one sample per view); not checked: a missing or damaged grass file, a scope view, grass
shadows off, the lowest texture quality, TAA off or DLSS/FSR, a respawn in ArenaMode, Bowl and Iceberg at night and
in rain, GPU memory, the sway at calm weather by eye (by the code it runs faster in a scope view, as the game's own grass wind does; not looked at), and the sway of the grass shadows.
The rain drops ran on Block and Equator only; not checked: glasses,
ripples and wet props by eye, light rain, and the other maps in rain.

EFT's own sound bundle has no impact sounds for the Books and CardBoard
prop types, so 39 Equator props (shoes, books, blue cups, a road cone, pizza boxes
and office folders) and any props of those types on other maps stay silent when they
hit something. Fika compatibility is not included in the verified scope, and a
plain raid without a mode sends the crowd no match event. The [0.7.0 release notes](docs/releases/v0.7.0.md)
describe the bounded native checks and what has not been checked yet.

## Development

Game-derived assets are distributed in release packages and are not stored in
Git. Building the maps requires local Arena/SPT inputs and Unity 2022.3.43f1.
The repository also retains development tooling for maps outside the release.

- [Development instructions](AGENTS.md)
- [Current working state](HANDOFF.md)
- [Build and validation history](docs/progress/INDEX.md)
- [Changelog](CHANGELOG.md)
- [Third-party notices](THIRD-PARTY-NOTICES.md)
