# Changelog

All notable changes to ArenaMapPort, newest first. The file starts with 0.2.3, the oldest version that is still
downloadable. 0.1.0, 0.2.0, 0.2.1 and 0.2.2 were withdrawn (their releases, tags and download files are deleted);
their notes stay under [docs/releases/](docs/releases/) for the record, and 0.2.3 contains everything they carried.
Dates are ISO dates. Each entry links its full release note (download links, checksums, install and upgrade steps),
and the settings text is in the [README](README.md).

## Unreleased

- One spectator on Air Pit's gallery now uses his original model, idle animation and cheering
  animation. This first prototype alternates automatically; it does not react to the match yet.

- Avoid a second, misleading `NO_CAUSE` map-error dialog when EFT already reported
  the failure during that loading attempt. Recorded map-file and Arena-installation
  errors still show their specific explanation.
- **Exit signs light with the gates (Equator, Air Pit).** The exit signs above Equator's booth doors and above Air Pit's
  three front bus doors now cast their small green light while the gates stand open: dark while a mode plugin holds the
  gates closed, on in the first moment of the release, and lit from the start in an ordinary raid. The signs themselves
  were always visible; Arena's wider glow around them is not ported.
- **Gate and door sounds.** When a mode plugin releases the held gates, Arena's own sounds play at them on all four maps
  (Equator's booth doors, Bay5's roller and depot gates, Air Pit's bus doors and passport lanes, Bowl's BTR, ambulance and
  gates), at Arena's level and out to 30 to 60 m depending on the gate. Most sounds are shortened to fit the faster motion
  of the gates (half of Arena's time by default). Nothing plays in an ordinary raid, where the gates already stand open.
  A new `gate-sounds` folder with the clips (plain audio files) ships beside the DLL; four developer settings under
  `Spawns/` tune the sound (they need `General/DeveloperMode`).
- **The TV screens can show a mode's timer, team score and alive counts** in Arena's look (its colours, trophy and skull
  icons and the Bender font): 9 screens on Equator, 8 on Bay5 and 6 each on Air Pit and Bowl; the other TVs stay dark.
  Nothing changes without a mode plugin that uses it, such as a future ArenaMode version. Mod authors: the public class
  `ArenaMapPort.Client.ScoreMonitors` (contract version 1) is described in `docs/progress/score-monitors.md`. A new
  `monitors` folder with nine images ships beside the DLL, and the developer setting `Monitors/Demo` shows a test
  cycle without a mode plugin.
- **Puddle footsteps on Equator, Air Pit and Bowl.** Steps on the water puddles use the splash footstep sound on these
  three maps, as they already did on Bay5. On Bowl the bare soil floor counts as ground for the puddles.
- **Four spawn points moved off the navigation edge.** Equator's two southern bot spawns and Bay5's north-west player
  spawn and east-lane bot spawn stood on the very edge of the navigation mesh, and the game pushed at least one of them
  1.5 m inboard at every raid start. They now start 1.3 to 1.5 m further in, where they ended up anyway.

## 0.3.0 - 2026-10-24

Full release note: [docs/releases/v0.3.0.md](docs/releases/v0.3.0.md).

### Added

- **New map: Bowl**, the open-air stadium arena from Arena. The sunken stadium field with a crashed Mi-26 in the
  middle, containers and guard towers, and a gated team pocket at the east and at the west end. Two normal
  server-time choices, a progressing day/night cycle, dynamic weather, bot spawns, waves and PMC squads, and two
  extractions (Bowl BTR Gate and Bowl Army Gate). It has its own DynamicMaps overview (ground floor), banner, four
  loading screens and map descriptions in English, German and Russian. The spectator stands are scenery. The
  package now carries four maps.
- **Gates, doors and vehicles stand in Arena's open poses and move with Arena's motion where it has any**, instead of
  being hidden (Equator's booth doors slid a distance worked out from the frame geometry). The open end poses are
  Arena's own, taken from its animation data; where Arena has motion data for a gate, its motion and timing are used
  too, and where it has none a neighbouring gate's numbers or our own stand in (named per map below). In an ordinary
  raid the parts stand in their open pose; a mode plugin such as ArenaMode can still hold them closed and release
  them, and a release plays the motion (a hold puts the parts back at once). Per map:
  - **Bowl:** the BTR, the ambulance and the gate leaves stand at Arena's open end pose, and on a release move there
    over the durations of Arena's animation clips with a smooth ease (Arena's exact curves are not reproduced). The
    parked BTR closes its slot like in Arena.
  - **Equator:** the booth door leaves sit open in their wall pockets and slide with the motion and timing of Arena's
    own door script on a release; the two leaves of the extra door at the far end of the south booth have no script
    of their own and use their neighbours' numbers. The FCK shop's roller shutter is shut again, as in Arena (the
    hall behind it is cut off).
  - **Bay5:** two of the warehouse's roller gates and the one-man door lift into the lintel, and both depot gates
    swing open to the walls (the second one stayed shut before), all on Arena's motion and timing. The two east-wall
    doors behind their barricades and the roller gate behind the tarp stay shut, because Arena moves none of them.
  - **Air Pit:** the three front bus doors open to the pose of the back row, and the back row stands open instead
    of being hidden. Arena has no animation data for the bus doors: the open pose is Arena's, the swing is ours. On
    a mode plugin's release the sixteen passport-control leaves turn open with Arena's own motion and the lamps flip
    from the red X to the green arrow on the booths' pocket side. In an ordinary raid the passport control stays
    shut, as before.
  - Every motion runs at half of its authored time (roughly one to seven seconds, depending on the gate; the bus
    doors' authored 3 s are ours). The developer setting `Spawns/SpawnGateDriveScale` changes that (default 0.5, range
    0.1 to 1.0); it needs `General/DeveloperMode`.
- **Messages that say why the Arena maps are missing.** When the maps are not available the game shows a message at
  the main menu with the reason and what to do (Arena not found or incomplete, an unusable `ArenaInstallPath`, a
  server that does not offer the maps, ArenaMapPort files missing). After a map failed to load it names the cause
  (an Arena build the keys do not match, a missing or damaged map file). Each message ends with a ref code such as
  `ref NO_ARENA`; the README's [If the maps do not show up](README.md#if-the-maps-do-not-show-up) lists every code
  with its fix. Texts in English or German, following the game's language. The new F12 setting
  `General/ShowMapNotices` (default on) hides only the main-menu message, the "still checking" notice and the
  reminder in the location selection; it never turns the Arena check off, and the answer to a failed raid start
  still appears. The server's exact reason comes from a new read-only route of the server mod
  (`/arenamapport/status`: codes only, no paths or file content).
- **Encrypted map bundles and a `keys` folder.** All twelve bundles (scenes, navigation and preset of the four maps)
  are encrypted, and a new `BepInEx/plugins/maschine-ArenaMapPort/keys` folder holds one small key file per map. A
  map's key is derived on your PC from a fixed sample (a few MB) of the Arena resource files that map is built from;
  the key files list those files by path and size and never contain a hash of them. The bundle is decrypted in
  memory while the map loads (no decrypted copy is written to disk). This is a requirement for your own Arena files,
  not copy protection: anyone who has Arena can derive the key or decrypt the bundles, and the art, audio and data
  files beside the bundles stay plain. The overhead is small: on the test PC key derivation takes tens of
  milliseconds per map and decryption adds up to about 0.9 s to a map load.
- **A done signal for mode plugins.** A mode plugin such as ArenaMode can ask when the gates are open instead of
  waiting a fixed time: `SpawnGates.GatesOpen`, `SpawnGates.IsOpening`, `SpawnGates.OpeningSecondsRemaining` and the
  event `SpawnGates.GatesOpened`. `GatesOpen` is true once no hold is outstanding, no opening is in progress and the
  map's navigation is installed; a map without gates never reports open.

### Changed

- **Equator and Bay5 load much faster.** Their scene bundles were stored in a single block that EFT had to unpack
  completely before the raid; they are now chunked like Air Pit's and Bowl's. On the test PC EFT's location load fell
  from about 19 s to about 5 s on Equator and from about 32 s to about 5 s on Bay5.
- **Navigation rebuilt to match the gates.** Equator, Bay5 and Air Pit have a new navigation mesh built around the new
  poses: the gates that stand open are open in it, and Equator's FCK shutter and the Bay5 roller gate behind the tarp
  are walls. Air Pit's south team box now leaves through the three front bus doorways. Checked on the test PC with
  path probes and walks through the opened gates.
- **Gunshot reverb in steel bodies.** A large thick-steel room, such as Bowl's crashed Mi-26, gets the metal room
  class instead of the plasterboard one. The rooms of the other maps keep their classes.
- **The Arena installation check covers Bowl too.** The check of 0.2.3 and its two override paths are unchanged.
- **Server console.** The server's "Arena not found" line is red, and the launcher advice no longer appears twice in
  the same line.
- **Bundle files differ from 0.2.3** for Equator, Bay5 and Air Pit (encrypted; Equator and Bay5 also chunked). The
  maps inside are the 0.2.3 maps plus the changes above.

### Fixed

- Equator's player spawns. The USEC spawn stood in the FCK shop and the other three on the aprons in front of the
  booth doors; all four now stand on Arena's own spawn markers inside the two booths.
- Bay5's second depot gate stayed shut because only the first instance of the gate was opened; both open now.

### Changed (internal)

- Bundle encryption: the `ArenaMapPort.Crypto` project (AES-CTR container, Windows CNG with a managed fallback), the
  `ArenaMapPort.BundleCrypt` staging tool, key files of schema 2 whose key source is a sample of the maps' resource
  streams (it survives the Arena 0.6.0 to 0.6.1 hotfix, which rewrote every level file), staging and validators.
- Every map's bundles are built with chunked compression. The Unity importer reconstructs each mesh from the package
  being built (mesh ids repeat across packages, which gave one development rebuild of Bay5 another map's geometry).
- Door poses: sidecars `tools/maps/<map>.doors.json` (schema 2, generated from Arena's data) mirrored by the map
  descriptors; the NavMesh bake drops only the parts that move; the spawn placement no longer pushes spawns out of
  Equator's booths. The server records the gate and registration outcomes for the status route.
- Client log lines no longer carry a doubled `[ArenaMapPort]` prefix. Importer fixes for Bowl (normal-map policy,
  backface cloth shader, audio groups) and the descriptor flag that keeps its border walls.

### Known limitations

- **Bowl:** no grass (Arena's grass is a GPU-instanced scene that is not ported); the trees have no wind animation,
  and EFT logs `Invalid windSettings` once while a ported map loads (harmless); no lamp flares (its only active flare
  lights belong to the distant city backdrop); the tunnel tarp shows its cloth texture on both sides without Arena's
  reflection; the capture point of Arena's mode lies under the Mi-26 where there is no walkable ground (where
  objectives go is up to the mode plugin).
- Gates, doors and vehicles move without Arena's gate sounds, which are not ported. Only the opening motion is played;
  a hold closes the parts again at once.
- **Arena builds.** The keys cover Arena 0.6.1.0.48206 and 0.6.0.0.47752. An Arena update that changes a map's
  resource files (re-imported textures or meshes, a Unity upgrade) locks that map until an ArenaMapPort package
  carries the key for the new build; the other maps keep working.
- Without the Windows crypto API (Wine, Proton, very old Windows) the client falls back to slower managed decryption,
  logs a warning, and map loading takes noticeably longer (not measured).

### Compatibility

- Tested with SPT 4.1.6 / EFT 0.16.9.5-40743; the server declares `~4.1.5`. Install both the client and the server
  component.
- Needs an installed Escape from Tarkov: Arena, checked as in 0.2.3. Keys: Arena 0.6.1.0.48206 and 0.6.0.0.47752.
- Optional: DynamicMaps (tested with 1.2.1), SAIN, ArenaMode. Fika compatibility has not been verified.
- **Upgrade from 0.2.3:** close EFT and the SPT server, extract the full ZIP over the installation and keep
  `BepInEx/config`. The ZIP replaces the client and the server DLL together, the bundles and the new `keys` folder;
  there is no DLL-only package (an older client cannot open the encrypted bundles, and this client needs the `keys`
  that belong to them). A server of an older version cannot give the reason for missing maps. The package grows from
  about 2.0 GiB to about 3.4 GiB (Bowl is new, and the chunked Equator and Bay5 bundles are larger).

## 0.2.3 - 2026-10-19

Full release note: [docs/releases/v0.2.3.md](docs/releases/v0.2.3.md). As the oldest version that is still
downloadable, 0.2.3 lists the whole package.

### Added

- **Air Pit**, **Equator** (Day and Night) and **Bay5** from Escape from Tarkov: Arena, with imported geometry,
  materials, lighting, audio, collision and navigation plus map-specific repairs. Bot spawns, waves and PMC squads;
  Air Pit has two gates as extractions. Equator Night has an optional natural appearance
  (`Appearance/NaturalEquatorNight` in F12).
- Optional DynamicMaps overview maps for all three maps, original map descriptions in English, German and Russian,
  and per-room gunshot reverb with invented room data (switchable per map with `General/DeveloperMode`).
- Spawn gates for mode plugins: a plugin such as ArenaMode can keep the team spawn enclosures closed until the fight
  starts.
- **Arena installation check.** The server registers the maps and the client loads their files only when an
  installed Arena is found and checked. It is found through the launcher's entry in the Windows registry and checked
  by names and sizes against the launcher's file list (no game data is read, nothing is sent anywhere). Both
  components log the result at Info. If Arena has no launcher entry, `General/ArenaInstallPath` (client) and
  `config/arena-install.json` (server) name its folder; an override is checked exactly like the automatic path, and
  nothing turns the check off.

### Changed

- The maps now require an installed Escape from Tarkov: Arena, which is sold separately from Escape from Tarkov. The
  check is a good-faith installation check at SPT's own level, not copy protection.

### Fixed

- Weather errors on Equator after raids on other maps in the same session (repeated weather, distant-shadow and
  ambient-highlight errors in the log): the ported map's cached sky is registered again with EFT's sky provider.

### Known limitations

- No loot and no scav raids; Arena's match rules and respawns come from ArenaMode, which is installed separately.
- The Arena check compares file names and sizes, not file contents.
- The per-room gunshot reverb uses invented room data.
- Air Pit's DynamicMaps overview shows the ground floor only.
- In an ordinary raid Air Pit's closed passport control stays the northern limit of the play area; a spawn-gate hold
  that nobody releases opens after five minutes with a warning.

### Compatibility

- Tested with SPT 4.1.6 / EFT 0.16.9.5-40743; the server declares `~4.1.5`. DynamicMaps integration tested with 1.2.1.
  Fika compatibility has not been verified.
- Needs an installed Escape from Tarkov: Arena (see Added).
- Install by extracting the full ZIP into the SPT installation, merging `BepInEx` and `SPT_Runtime`, with EFT and the
  SPT server closed; keep `BepInEx/config`. There is no DLL-only package. The client folder is
  `BepInEx/plugins/maschine-ArenaMapPort`, the server folder `SPT_Runtime/user/mods/ArenaMapPort`.
