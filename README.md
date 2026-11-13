# ArenaMapPort

**Equator, Bay5, Air Pit, Bowl, Iceberg and Chop Shop from Escape from Tarkov: Arena, rebuilt for SPT.**

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
| Iceberg | The Iceberg resort hotel on a rocky canyon: a multi-storey hotel with a rotunda, with its lobby, atrium, pool hall and cafe inside and a street area around it. It uses Arena's CheckPoint layout: two team pockets behind sliding gates at the east and west ends and three capture points (A, B and C) between them. The sea below the canyon uses the same native water as Bay5's harbor. It uses the server's time of day and weather, as on Bay5; raids were run at noon and in the evening, clear. The east and west gates serve as extractions. There is no spectator crowd and no grass is ported. |
| Chop Shop | A garage hall with a walled street yard, in Arena's TeamFight layout: a team pocket at the east end and one at the west end, one capture point and four moving gates. It uses the server's time of day and weather, as on Bay5 (summer set only: no winter or snow look). Arena's own spectator crowd fills its stands. The east and west gates serve as extractions. There is no DynamicMaps overview and no native water. |

All six maps include imported geometry, materials, lighting, collision and bot
navigation, with restored ambience, material sounds, room acoustics and decals.
Equator and Bay5 also have restored dust. Equator, Bay5, Bowl and Chop Shop have original
lamp flares; Air Pit and Iceberg do not. Bay5 includes its harbor background and
water, and Iceberg its sea.

Chop Shop is new in 0.6.0 (Arena's `Arena_AutoService`, shown in the game as "Arena: Chop Shop").
Its ambient sound mix is a computed start value that nobody has listened to, and
nobody has listened to its gate sounds or its crowd either. It has no DynamicMaps
overview, no native water and no winter set.

Iceberg joined in 0.5.0. Its brightest lamps (the lobby crystal lamps and the
chandeliers, among others) are far brighter in Arena's data than on any other map, so
at raid start the port lowers the strongest of them and lifts the dimmest to a floor,
and restores the originals when the raid ends. The two limits (4.6 and 1.8 since
0.6.0; 4.8 and 1.9 in 0.5.0, a saved pair of those moves once) were
chosen by the author by eye on his own PC and can be changed with DeveloperMode.
The ambient sound mix of Iceberg is a first choice that has not been listened through
yet.

Since 0.6.0 every map has more Bot spawn rows for mode plugins (Chop Shop 16, Equator 16,
Bay5 16, Iceberg 17, Air Pit 20, Bowl 21) and the two exits stand in the team start
areas, marked by their green flares: in a plain raid you spawn a few metres from an
8-second exit (in the two ArenaMode Team Deathmatch runs on Bowl and Iceberg, ArenaMode
hid the extraction and nobody extracted; bots never use exits). On Air Pit the north gate
cannot be reached in a plain raid, because the passport gates stay shut until a mode plugin
opens them. On Iceberg, the first spawn at some start points can stand within 2 m of an
exit flare and look tinted green.

Since 0.4.0 the original animated spectators fill the stands of Equator, Bay5,
Air Pit and Bowl, and since 0.6.0 those of Chop Shop (five of the six maps; Iceberg has none), with spatial crowd
calls from 13 original fan recordings (one call every 4 to 8 seconds) and a quiet
crowd murmur from each group of spectators. Since 0.5.0 the murmur is Arena's own
neutral in-match crowd bed (0.4.0 used its postmatch recording); playing it from the
stands is the port's choice. The crowds can also answer match events: through the
`SpectatorCrowd` contract a mode plugin asks them to approve, disapprove, take cover,
start or finish, and one to three voices answer with original fan clips (70 in
all) while the stands add one of Arena's louder cheering or angry crowd recordings.
ArenaMode 0.4.0 and later uses the contract and drives the reactions from its match events; a plain raid without a mode sends no such event, so nothing reacts there. The author listened to the developer demo on
7 November 2026 and found it good (he did not report on the angry recordings
separately; the demo and the 70 clips ran on the four older crowd maps on 2 November, the roar layer only on Equator and Bowl, not on Bay5 and Air Pit; Iceberg has no crowd,
and nobody has listened to a reaction on Chop Shop). The roar beds are exported 6 dB louder than in 0.5.0 (since 0.6.0; the developer
volume key stays at 1.0); that level has not been listened to. The idle and cheering animations run independently
of match events.
Their textures are stored once for all maps (135 images, about 147 MB on disk). The crowds
decode 135 shared images into about 0.54 GB of video memory per raid on each of the
five crowd maps and add about two seconds to raid loading; the
[0.6.0 release notes](docs/releases/v0.6.0.md) explain how to skip a map's crowd.

The gates and vehicles at the team spawn enclosures use Arena's own open positions
and, where Arena has them, its motions (at half their time). They stand open from raid
start, or a mode plugin such as ArenaMode holds them closed and opens them when the
fight starts; Air Pit's passport control stays shut until a mode plugin opens it. A few doors that
Arena itself never moves (three warehouse rollers on Bay5, the FCK shop's roller shutter on Equator)
stay shut.

## Compatibility

- Tested with **SPT 4.1.6 / EFT 0.16.9.5-40743**. The server declares `~4.1.5`.
- Install **both** the client and server components.
- Chop Shop is registered as a location in SPT, but whether and when ArenaMode offers it in
  its matches is up to ArenaMode. This project has run no ArenaMode match on Chop Shop.
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
  client does not load the map files. The map bundles in the release are encrypted with a key
  that your PC derives from the Arena resource files each map is built from (the texture and
  mesh data of its scenes; a fixed sample of a few MB is read when the map is first used and
  never sent anywhere; the mod's `keys` folder lists the files by path and size only), so they
  work only next to your own Arena files; this is a requirement, not copy protection (anyone
  with Arena can decrypt them), and the art and audio files shipped beside the bundles are not
  encrypted). The keys in this release are built for Arena 0.6.1.0.48206 and were checked
  against 0.6.2.0.48473 (all six maps; Chop Shop's key file is built for 0.6.2.0.48473 and was also
  checked on 0.6.1.0.48206 and on the newer hotfix 0.6.2.0.48598); the four maps of 0.4.0 also open on
  0.6.0.0.47752, whose resource files are the same, and Iceberg and Chop Shop were not checked
  there (your build is in the client log at startup, `Arena: found ...`); if an Arena update changes a map's resource files, that map
  will not load (the client log names the cause) until an updated ArenaMapPort package
  carries the key for your build; the other maps keep working.
- **ArenaMode** is the intended gameplay companion and is installed separately.
  It is not required to load the maps.
- **DynamicMaps** is optional; integration was tested with version **1.2.1**.
- **SAIN** is optional; location compatibility is handled when it is installed.
- Fika compatibility has not been verified.

The release ZIP contains the built maps. Players do not need to run the export
tools or install Unity.

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
files; the [0.6.0 release notes](docs/releases/v0.6.0.md) cover the upgrade from 0.5.0 and 0.4.0; the
[0.4.0 notes](docs/releases/v0.4.0.md) cover the retired debug helper.

## Settings

In the F12 configuration menu, enable **Appearance / NaturalEquatorNight** to
use the natural appearance for your next Equator midnight raid. This disables
the added neon lights, chemlights, smoke/haze, temporary PostFX and extra
wet-floor reflections. Time, weather and original lamps remain unchanged.
Equator Day, Bay5, Air Pit, Bowl, Iceberg and Chop Shop are unaffected.

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

Per-room gunshot reverb (**RoomShotAcoustics**) is on by default on all six
maps: shots ring differently in a big hall than in a small shop. Its room data is
invented, because neither Escape from Tarkov nor Arena provides gunshot reverb
values. It can be switched off per map under `Audio (arena_equator)`,
`Audio (arena_bay5)`, `Audio (arena_airpit)`, `Audio (arena_bowl)`, `Audio (arena_iceberg)` or `Audio (arena_autoservice)` with
**General / DeveloperMode** enabled (enable it and restart EFT, the keys then appear in F12); switching it off
restores the shipped gunshot acoustics.

## If the maps do not show up

When the Arena maps are not available, ArenaMapPort shows a message at the main menu with the reason and what to do, and after a map
fails to load it names the cause. The message is in English or German, following the game's language, and its last line carries a ref
code such as `ArenaMapPort 0.6.0 - ref NO_ARENA`. Find the ref below and follow the fix. If you report a problem at
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

Overview maps for Equator, Bay5, Air Pit, Bowl and Iceberg are included (Chop Shop has none). If DynamicMaps is
installed, open inventory → **MAP** during a raid.

ArenaMapPort loads these maps from its own directory; you do not need to copy
anything into DynamicMaps. The overviews are simplified ground-floor plans; the upper
spectator areas of Air Pit and Bowl are deliberately omitted. Iceberg's is one
overview of the connected combat routes across their different ground heights, not
a separate upper-floor map.

## Scope

The release contains **Equator, Bay5, Air Pit, Bowl, Iceberg and Chop Shop**. There is no loot or scav-raid
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
choices (the lamp limits were set by the author by eye on his PC; the 0.6.0 values and
the one-time move from 4.8 and 1.9 have not been through an Iceberg raid on the
test host yet). It has no spectators,
no per-light flares and no ported grass, and it is a server-time map
without a winter or snow look.

Chop Shop has had raids on the test host with and without bots, and the hideout opened
afterwards, with no ArenaMapPort warning, but fewer than the older maps. Not listened
to: its ambient mix (computed start values), its gate sounds, its crowd and the louder
roar beds. Not checked: whether a player can climb the tribune decks above the hall,
the rain-depth readback and a raid with ArenaMode from this project. It is a server-time map with a
summer set only (no winter or snow look). The appended spawn rows and the exits in the
team start areas ran in plain raids on all six maps and in two ArenaMode Team Deathmatch
runs (Bowl and Iceberg); not checked are a respawn onto an in-box marker, Team Deathmatch
on Chop Shop, Air Pit and Bay5 with the new exits, any Domination, Deathmatch, Zombies or
Tactical match with the new rows, and the flares seen from across a map.

EFT's own sound bundle has no impact sounds for the Books and CardBoard
prop types, so 39 Equator props (shoes, books, blue cups, a road cone, pizza boxes
and office folders) and any props of those types on other maps stay silent when they
hit something. Fika compatibility is not included in the verified scope, and a
plain raid without a mode sends the crowd no match event. The [0.6.0 release notes](docs/releases/v0.6.0.md)
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
