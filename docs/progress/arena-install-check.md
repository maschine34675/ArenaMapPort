# Arena installation check - 2026-10-19

Design record for the Arena installation requirement of release 0.2.3 (branch `release/0.2.3`).
Status when written: the detection and verification code is written and unit-tested
(`ArenaMapPort.Shared/ArenaInstallation.cs`), the server gate (`ArenaMapPort.Server/ArenaInstallationGate.cs`,
called from `ArenaMapRegistration.cs`) and the client gate (`ArenaMapPort.Client/ArenaInstallationGate.cs`,
started in `Plugin.Awake`, enforced in `BundleHost.TryServe`) are in the tree, nothing is committed, and
**no host run has happened yet**: the evidence section at the end is empty on purpose.

## Motivation

Escape from Tarkov: Arena is a separate purchase from Escape from Tarkov, so not every SPT player
owns it. ArenaMapPort distributes map content derived from Arena's files. User decision
(2026-10-19): from 0.2.3 on the maps work only with an installed Arena. The server registers the
Arena locations only when an Arena installation is found and verified, and the client serves the map
bundles only then. The reasoning is the one SPT itself applies to Escape from Tarkov: the mod is
usable by people who own the game it is made from.

This is a good-faith installation gate at SPT's own level, **not copy protection**. See "What it is
not" below; the wording matters for the release notes, the README and anything said about the mod.

## What is checked, and why

The BSG launcher registers Arena under
`HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\EscapeFromTarkovArena_live`
(values `InstallLocation`, `DisplayName`, `Publisher`, `DisplayVersion`) and writes a manifest,
`ConsistencyInfo`, into the installation folder. Both exist on a healthy installation, and nothing in
the check needs a file of ours that carries BSG data.

| Step | What | Why |
|---|---|---|
| 1. Registry | Scan the Uninstall branch (WOW6432Node first, then the plain path) for the `_live` key, then any subkey whose name starts with `EscapeFromTarkovArena`; read `InstallLocation`. | The suffix may vary, and the entry gives the folder without any user setting. The registry is read by reflection, so the client (Mono in EFT) and the server (net10) need no package. A system without a readable registry means "not found", never an exception. |
| 2. Executable | `EscapeFromTarkovArena.exe` must exist in the folder. | The cheapest proof that the folder is a game folder and not any folder with a manifest. |
| 3. Manifest | `ConsistencyInfo` must exist, be smaller than 64 MB and parse as UTF-8 JSON `{"Version":..,"Entries":[{"Path","Size","Checksum"}..]}` with a non-empty version and entries. | It is the launcher's own list of what the installation consists of, so the mod embeds no BSG-derived sizes or hashes. The `Checksum` is BSG's own algorithm (not CRC32) and is ignored; `Size` is used. |
| 4. Plausibility floors | At least 5,000 distinct files, at least 40 GB listed, at least 100 level files (`EscapeFromTarkovArena_Data\level*`), a `GameAssembly.dll` of at least 50 MB, and the executable listed. | A hand-made manifest has to list real-looking data. Measured on this VM's installation: 15,352 entries, 76.1 GB, 388 level files, GameAssembly.dll 155 MB, so the floors sit far below a healthy install and cannot reject one that is merely a different build. Raises the effort of a forgery; proves nothing (names and sizes only). |
| 5. Full file check | Every distinct listed file must exist with exactly the listed size (no read of file content). | A stub or a partial download fails; the 76 GB are really there. Sizes, not checksums: the checksum would need BSG's algorithm and a read of 76 GB. |

When the registry holds several entries, each is tried in the order above; the first that verifies wins,
otherwise the first entry's failure reason is reported.

The manifest is treated as untrusted text. Paths must be plain relative paths inside the installation
(no `..`, no rooted or UNC path, no colon, no segment made only of dots and spaces, which legacy path
normalisation in Mono would trim); a path that is not a plain relative path inside the installation is
reported as "invalid path" and fails the check.
Two entries that resolve to the same file (`/` against `\`, letter case, doubled separators) are one
file, so one real file cannot be listed again and again to reach the floors; a repeated entry with a
different size is refused as a manifest conflict. The real manifest has no repeated path (0 in
15,352 entries), so a healthy installation is not affected.

### The ConsistencyInfo self-entry

The manifest lists itself (entry `ConsistencyInfo`), but its recorded size cannot equal its own final
size. On this VM exactly one of 15,352 entries mismatched on a healthy installation: that one. It is
skipped in the file check (it still counts towards the floors). Anything else that mismatches fails
the gate; see "Open points" for the tolerance decision.

### Cost

A full existence-and-size check of all 15,352 entries took **0.46 s** with a warm file-system cache
(measured on this VM; area A's real-install unit test ran in 195 ms).
A cold cache and a slow disk were not measured; the result is therefore computed once per process and
kept by the caller, never per raid or per frame. The call is synchronous file-system I/O: an
unreachable network path (an override or `InstallLocation` on an unrouted UNC address) blocked for
21 s when measured, so a caller on a game thread runs it on a worker with a bounded wait and treats a
timeout as not verified.

### The registry and manifest versions differ, on purpose

On this VM the registry says `DisplayVersion` 0.6.0.0.47752 while the manifest says `Version`
0.6.1.0.48206: the files and the manifest are newer than the uninstall entry's `DisplayVersion` (observed;
the cause was not investigated). So the **manifest version is authoritative**; the registry version is
kept in the result for logging only and the two are never compared (a comparison would reject healthy,
updated installations). The check also has no version pin against the Arena version the maps were
extracted from: any installation whose listed files are all present passes.

## The two overrides

For an installation without the launcher's registry entry (a copied or moved folder, a machine where
the launcher was never run):

- **Server:** `SPT_Runtime/user/mods/ArenaMapPort/config/arena-install.json` with
  `{"installPath": "<folder>"}`.
- **Client:** `General/ArenaInstallPath` in `BepInEx/config/com.maschine.ArenaMapPort.cfg`; empty means
  automatic.

An override is verified exactly like the registry path (executable, manifest, floors, every file). A
non-empty override that is wrong is **not** verified and does **not** fall back to the registry: the
user named that path.

Details of the two carriers:

- The server file accepts comments and trailing commas and matches `installPath` case-insensitively;
  backslashes in the path are doubled (or written as `/`). An empty or null `installPath` is no
  override. An oversized, unreadable or malformed file, or one without a string `installPath`, is
  ignored with a **Warning** and the registry entry is used instead.
- The client key is bound on the public `Config` and listed in `CompactConfiguration.IsPublic`, so
  compaction keeps a path typed into the public file. The public file therefore has three entries
  (`General/DeveloperMode`, `Appearance/NaturalEquatorNight`, `General/ArenaInstallPath`); earlier notes
  that say two (`compact-player-config.md`, UiBridge reports) describe the state before 0.2.3. A key
  that cannot be read fails closed (not verified) instead of falling back to the registry.

## Decision: no disable switch

There is no setting, command-line flag, environment variable or developer-mode key that turns the
check off, and none may be added (AGENTS.md hard rule). The override paths are not such a switch:
they only say where to look and run the full verification. Tests inject a registry, floors or a
detector through internal seams (`InternalsVisibleTo`, an optional `detect` parameter); no production
caller uses them. A switch would
make the gate meaningless for the one audience it is for (people who did not buy Arena) while adding
nothing for owners, who either have the registry entry or can set the path.

## What it is not

It is not copy protection and must not be described as such. The release ZIP still contains the map
bundles; the gate only decides whether the mod registers and loads them. The check reads names and
sizes, so sparse files or hard links of the right sizes pass it, managed plugin code can be patched,
and the bundles can be read from the ZIP by anyone who downloads it. A good-faith check at SPT's own
level stops the accidental case (a player without Arena installing a mod that does not work without
its files) and states the intent.

A later "stage 2" idea, out of scope for 0.2.3 and not planned: encrypt the map bundles with keys
derived from files of the user's own Arena installation, so they are unreadable without it. That would
make the dependency real, at the price of a key-derivation scheme, a changed build and distribution
pipeline and a new failure mode after every Arena patch, so it needs its own decision.

## Behaviour without a verified installation

- **Server:** one Info line (`[ArenaMapPort] ...`) with the reason and the way out, and the mod returns
  before it touches anything: no location, no locale transformer, no weather hook, no APBS patch. The
  Arena maps are absent from the location selection and the server is left as SPT built it. An
  unexpected exception in the check is logged as an Error and counts as not verified.
- **Client:** the check runs once per process on a worker thread started in `Plugin.Awake`; startup
  waits at most 3 s for it and otherwise logs that it is still checking. The outcome is an Info line
  (tag `[ArenaMapPort][Arena]`) plus, when Arena was not verified, a hint line, and the startup banner
  ends in `Arena: found <version>` or `Arena: not found`. A bundle request for a ported map
  (`BundleHost.TryServe`) waits at most 30 s for a check that is still running and then fails closed;
  without a verified installation the bundle is not served and an Error line says so, so a server that
  registered the maps for a client that cannot verify Arena fails loudly. Vanilla bundles never reach
  the gate. The tag still carries the own `[ArenaMapPort]` prefix, like every client line on this
  branch; main dropped that prefix in `e66d512`, so the forward merge turns it into `[Arena]`.
- Both sides log the state at Info, also when it is fine: `Escape from Tarkov: Arena <manifest
  version> at <path> (registry|override; N files, X GB verified)`. Nothing leaves the PC.

## Host plan

Not run yet. GPU-2 is getting Arena installed by the user (2026-10-19).

1. **Negative, host without Arena** (before the install finishes, or any time with an override that
   points at an empty folder; the client key is set with `uibridge_config_set`, a server override file
   is a host-local test file, written on the host and not into `D:\SPT41`, and removed afterwards):
   restart the server and EFT with the 0.2.3 build; the server log shows the Info line and no
   `arena_*` location is registered; the main menu's location selection has no Arena maps; the client
   log shows its Info line and the hint line and the banner says `Arena: not found`; no ArenaMapPort
   bundle is loaded; the hideout opens and a vanilla raid starts without a gate Error line (the plugin
   must leave non-Arena content alone).
2. **Positive, after Arena is installed on GPU-2:** record the registry key (`DisplayVersion`) and
   the manifest version; deploy with `tools/deploy-to-spt.ps1` (full deploy), restart the server and
   EFT (`gpu-run restart --host host2`); server and client Info lines show the installation with the
   file count and size; the three maps appear and raids on Equator, Bay5 and Air Pit start; open the
   hideout once (acceptance of a client build). Record the time the check took (cold first start and
   warm second start) and `uibridge_status` `max` frame time around the client's check.
3. **Overrides:** client key and server file pointing at the correct folder give `source=override`
   and the same result; a wrong folder gives the Info line and no maps without falling back to the
   registry; a malformed server file gives a Warning and the registry result; clearing both returns
   to `registry`. Optional: a client key at an unrouted UNC address exercises the bounded wait
   (about 21 s measured for such a path; the `still checking` line shows).
4. Leave nothing behind: remove the test override file and reset the client key.

## Open points

- **Mismatch tolerance.** As implemented, one missing or resized file fails the gate. The upside is
  simplicity; the downside is that a file the launcher or BattlEye rewrites in place, or a half-finished
  update, locks an owner out until the launcher repairs it. Whether a small tolerance is wanted is the
  user's decision; the 0.46 s measurement and the single self-entry mismatch say a healthy installation
  has none.
- The release README text is generated by `tools/release/make_release_zips.py` (`readme_text`), and
  the package must say the same thing as README.md and the release notes.
- Client log tag: see "Behaviour without a verified installation"; adjust it when the branch is
  merged forward into main.
- Packaging must keep a user-created `SPT_Runtime/user/mods/ArenaMapPort/config` folder on upgrade
  (extracting a ZIP over the installation does; a cleanup script must not delete it).

## Evidence

Left for the host run. Record, with date, time and build id (`uibridge_status` MVID comparison):

| Run | Date/time | Build | Server log line | Client log line | Maps in selection | Check time | Result |
|---|---|---|---|---|---|---|---|
| Negative (no Arena or empty override) | | | | | | | |
| Positive (GPU-2 with Arena) | | | | | | | |
| Override, correct folder | | | | | | | |
| Override, wrong folder | | | | | | | |


## Integration - 2026-10-19 (release/0.2.3, nothing committed, no host run yet)

Integrator pass over the four areas (shared check, server gate, client gate, texts). Integration edits made on top of the
areas' files:

- `MapConfigVisibility.IsPlayerOption` lists `General/ArenaInstallPath`. Without it F12's player mode hid the override
  although `CompactConfiguration.IsPublic` keeps it (`ReleaseConfigurationTests` failed on exactly that). The public file
  has three entries and F12's normal mode two controls (`NaturalEquatorNight`, `ArenaInstallPath`); AGENTS.md says so now.
- `NavMeshInstaller.LoadOnce` calls `ArenaInstallationGate.EnsureVerified()` before it reads
  `bundles/<map>_navmesh.bundle` from disk. That bundle is read directly (`AssetBundle.LoadFromFile`), not through EFT's
  bundle manager, so `BundleHost.TryServe` did not cover it. Without a verified Arena: an Error line, no mesh, and no
  `_loadTried` latch (a check that finishes later still lets a later raid load it). In practice unreachable without a
  registered map; it closes the second place that reads our bundle files.
- `ArenaInstallationGate.cs` (client) carries `#nullable disable` like the other client files that the test project links
  (`ShaderStandinPolicy.cs`); it removed 17 nullable warnings from the test build (70 warnings remain, all in other files).
- `tools/release/make_release_zips.py`: the generated package `README.txt` carries the requirement
  (`REQUIREMENT_TEXT`, same statement as README.md and the release notes) and names `ArenaInstallPath` in its Settings
  paragraph. `tools/tests/test_release_zips.py` asserts parts of that text and still passes.
- Version 0.2.2 -> 0.2.3 in both csproj `<Version>`, `Plugin.PluginVersion` and `ModMetadata.Version`; no test or tool pinned
  0.2.2 (`git grep 0.2.2` over tracked non-Markdown files finds nothing).

### Build and test evidence (this VM, Arena installed here, sequential runs)

| Step | Result |
|---|---|
| `dotnet build ArenaMapPort.Client/ArenaMapPort.Client.csproj -c Release` (full, restages dist) | `0 Warnung(en)`, `0 Fehler`; `PARKED_DIST_OK Client 0 files` |
| `dotnet build ArenaMapPort.Server/ArenaMapPort.Server.csproj -c Release` | `0 Warnung(en)`, `0 Fehler`; `PARKED_DIST_OK Server 0 files` |
| `dotnet test ArenaMapPort.Server.Tests/ArenaMapPort.Server.Tests.csproj -c Release` | `Bestanden!   : Fehler:     0, erfolgreich:  1095, uebersprungen:     0, gesamt:  1095, Dauer: 9 s`; the feature's classes: `ArenaInstallationTests` 141, `ArenaInstallationGateTests` 57, `ClientArenaGateTests` 22, `ReleaseConfigurationTests` 3, all green |
| `python -m unittest discover -s tools/tests -p "test_*.py"` | `Ran 1728 tests in 203.161s`, `OK (skipped=46)` |
| `python tools/validate/validate_distribution.py` | `DISTRIBUTION_OK 3 maps 9 bundles 3 sidecars` |
| `pwsh -NoProfile -File tools/deploy-to-spt.ps1 -SptRoot 'D:\SPT41'` (dry run, no -Confirm) | `plan: 67 file(s), 2137.4 MB total -- 0 new, 3 to overwrite, 64 already identical`; `DRY RUN (default) -- no files were copied` |

The Release test run includes the real-installation, registry and sparse-install tests, which ran against this VM's Arena
(they skip on a machine without it). The Python run predates the `#nullable disable` line and
the two DLL rebuilds that follow it; none of them touches anything the Python tests read.

### Dist

`dist/` holds 67 files, 2137.4 MB: the three maps, no new kind of file. Per-file SHA-256 before and after the two builds
differ for exactly two files, the DLLs; every bundle, sidecar, acoustics, flares, water, loadscreen, DynamicMaps, banner
and database file is byte-identical to the dist as it stood before this integration build.

| Folder | Files |
|---|---|
| `BepInEx/plugins/maschine-ArenaMapPort/` | `maschine-ArenaMapPort.Client.dll` (844,800 B), 3 `material-shaders.arena_<key>.json` |
| `.../bundles/` | 9 (`arena_<airpit,bay5,equator>_{scenes,navmesh,preset}.bundle`) |
| `.../acoustics/`, `.../flares/`, `.../water/` | 9 (3 maps x graph, bake, room tone), 6 (Bay5 and Equator), 5 (Bay5) |
| `.../ai/`, `.../dynamicmaps/`, `.../loadscreens/`, `.../map-selection/` | 3, 6, 15, 2 |
| `SPT_Runtime/user/mods/ArenaMapPort/` | `maschine-ArenaMapPort.Server.dll` (101,376 B), `banners/` 3, `db/<key>/base.json` 3, `locales/map-descriptions.json` |

| DLL | FileVersion | SHA-256 (dist copy) |
|---|---|---|
| client | 0.2.3.0 | `cabc918b5486b2a5f0d398f10228f29970d5f91aa8f212f6e65d503d2f8e055d` |
| server | 0.2.3.0 | `b0e2831f2ec5fcc3045f3220295f8278a0c457334a9b9980c968b2b0dcedf0e2` |

`ProductVersion` reads `0.2.3+a3188cd...` because the tree is uncommitted; the release cut rebuilds from its commit and the
hashes above change with it.

### Deploy dry run against D:\SPT41

The host holds the 0.3.0 four-map build. The plan differs from "the two DLLs only" in one more file:
`locales/map-descriptions.json` WILL OVERWRITE, because the host's copy holds Bowl's three descriptions
(`descriptions/{en,ge,ru}/arena_bowl`; 13 keys against dist's 10) and this three-map dist does not. Bowl's own files on the
host (bundles, `db/arena_bowl`, acoustics, flares, water, loadscreens, DynamicMaps pack, banner) are not in this dist and the
script never touches them. With the 0.2.3 server DLL the host registers three maps (Bowl is not in `ArenaMapRegistry.All`
on this branch), so the dropped descriptions are not used; deploying the 0.3.0 build later regenerates the four-map file.
Nothing under `D:\SPT41` was written.

### What the host run must show

Order (AGENTS.md): full deploy with `-Confirm` (not `-DllOnly`), then compare the host's server DLL hash with the table
above BEFORE restarting SPT, restart the server, `gpu-run restart --host host2`, `uibridge_status` (MVID of the loaded client
against the VM build).

1. Server log, new process: `[ArenaMapPort] Escape from Tarkov: Arena <manifest version> at <folder> (registry; N files, X GB verified)`
   (the real installation here reads 15,351 files, 76.1 GB; the host's N differs only if its install differs), then the
   normal map-registration lines; the locations appear in the menu.
2. BepInEx log (source `maschine-ArenaMapPort`): the banner `0.2.3 loaded. Maps: Arena: Equator, Arena: Bay 5, Arena: Air Pit. Arena: found <version>.`
   and `[ArenaMapPort][Arena] Escape from Tarkov: Arena <version> at <folder> (registry; N files, X GB verified)`; no hint line
   and no `still checking` line on a warm start. Record the check duration of the first cold start and of a warm one, and
   `uibridge_status` `max` frame time around it.
3. Registry probe on Mono: the `(registry; ...)` source in that client line is the proof that EFT's runtime reaches
   `Microsoft.Win32.Registry` by reflection (the metadata is there, a raid on GPU-2 is the only runtime proof).
4. One raid per map (Equator, Bay5, Air Pit) and one hideout open (acceptance of a client build); no `[Bundles] ... not served`
   and no `[NavMesh] ... not loaded` Error line.
5. Negative and override runs as in "Host plan" above (empty override folder, wrong folder, malformed server file, cleared
   again); the negative run additionally shows the banner ending `Maps: none available. Arena: not found.` and no `arena_*`
   location in the menu.

Results go into the Evidence table above with date, time and build id.

## Tolerances after the review (2026-10-19, same evening)

Two refinements of the verifier after the final review, both in `ArenaInstallation.cs` with tests:

- **Self-updating files.** BattlEye replaces its own files in place when the game starts, and `Logging.config` is
  user-editable, so a strict size comparison could refuse a legitimate installation after a BattlEye update. Entries under
  `BattlEye\` and `Logging.config` now only have to exist (`IsSizeTolerant`); every other listed file still needs the
  listed size. On the VM's install all of them match today.
- **Deep installation folders.** The longest manifest path is 208 characters; with an installation folder longer than
  about 50 characters a full path reaches Windows' classic 260-character limit, where EFT's Mono runtime throws in
  `Path.GetFullPath` and cannot stat the file. Such entries are skipped and counted (`LongPathsSkipped`, shown in the
  Info line as `..., N long paths skipped`) instead of failing the check; `ResolveInside` no longer normalises them (the
  text rule already refuses every escaping spelling). The default launcher folder (46 characters) skips nothing.

## Host evidence (2026-10-19, build `ff4308c`, both GPU hosts)

The user installed Arena on SPT-GPU-2 (host 2) through the BSG launcher and offered SPT-GPU-1 (host 1, no Arena) for the
negative case. Both hosts mirror the same SPT installation, so one `deploy-to-spt.ps1 -Confirm` (3 overwrites: both DLLs and
`locales/map-descriptions.json`, 64 identical) served both; `gpu-run deploy-wait --server --force` saw the files identical
after 96.5 s (host 1) and 2 s (host 2), started each SPT server, and `gpu-run start` brought each EFT to the main menu with
`17/17 identical` plugins (the 0.2.3 client, `ProductVersion 0.2.3+ff4308c`).

- **Host 1, negative (no Arena, no registry entry).** Server log 19:29:26: `[ArenaMapPort] Escape from Tarkov: Arena not
  found: No Escape from Tarkov: Arena installation is registered in the Windows registry
  (HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\EscapeFromTarkovArena_live); install
  Arena with the Battlestate Games launcher, or point the mod's Arena install path override at the folder that contains
  EscapeFromTarkovArena.exe. The 3 Arena maps stay unregistered. Install Escape from Tarkov: Arena through the BSG launcher,
  or set ...` and no `is available in location selection` line. Client log 19:30:10: `[ArenaMapPort][Arena] Escape from
  Tarkov: Arena not found: No ... registered in the Windows registry (...)`, the hint line `the Arena maps need an installed
  Escape from Tarkov: Arena; the server does not register them without one. Set General/ArenaInstallPath ...`, and the banner
  `0.2.3 loaded. Maps: none available. Arena: not found.` The PMC location selection (`selection-check` scenario) listed
  0 `ARENA` tiles among 30 labels (THE LAB ... TERMINAL); the hideout opened (`hideout-open`: screen Hideout, `logAbsent` error
  passed) and closed after the profile's first-visit dialog. No ArenaMapPort warning or error.
- **Host 2, positive (Arena 0.6.1.0.48206 at `C:\Battlestate Games\Escape from Tarkov Arena`).** Server log 19:31:33:
  `[ArenaMapPort] Escape from Tarkov: Arena 0.6.1.0.48206 at C:\Battlestate Games\Escape from Tarkov Arena (registry;
  15,351 files, 76.1 GB verified)` followed by the three `is available in location selection` lines, the fixed-raid-time
  and APBS lines (`3 Arena map key(s)`). Client log 19:32:10: `[ArenaMapPort][Arena] Escape from Tarkov: Arena 0.6.1.0.48206
  at C:\Battlestate Games\Escape from Tarkov Arena (registry; 15,351 files, 76.1 GB verified)` (the reflection registry probe
  works under EFT's Mono: source `registry`), banner `0.2.3 loaded. Maps: Arena: Equator, Arena: Bay 5, Arena: Air Pit. Arena:
  found 0.6.1.0.48206.` The selection listed the 3 `ARENA` tiles. An Air Pit raid (12:00, no bots) loaded its bundles
  through the gate (`[Bundles] loaded 'arena_airpit/arena_airpit_scenes.bundle' ... (991967415 bytes)`, NavMesh 11787/5423,
  acoustics 24/94), 0 ArenaMapPort warnings, left through `gpu-run leave`; the hideout opened and closed afterwards.
- Not measured on the hosts: the check's cold-cache duration (the Info lines carry no timing; the client's 3 s startup wait
  was not exceeded on either host, otherwise the banner would read `still being checked`). The override paths were exercised
  by the unit tests and the VM probe only.

## 0.2.3 candidate cut (2026-10-19, not published)

`python tools/release/make_release_zips.py --single --extract-check work/releases/0.2.3-extract-check` on the clean tree at
`c6536e5`: `writing single ZIP (full): 67 files, 2,241,235,966 raw bytes`, `maschine-ArenaMapPort-0.2.3.zip 2,149,219,632 B
f96ccc756cc7c56ff105861f0efcabf676edd0f09adf186fdcb8f4ddbb378090`, `single ZIP: 2.00 GiB`, `verified single ZIP`,
`extract-check OK: 67 files identical to dist, 4 generated entries ignored`, `RELEASE_ZIPS_BUILT`. Against the final 0.2.2
ZIP's manifest (`326d71b`) the dist differs in exactly the two DLLs (client 815,104 -> 845,312 B, server 73,216 -> 101,888 B);
the other 65 files, including the regenerated banners, loading screens and `map-descriptions.json`, are byte-identical. The
DLLs are the host-verified build of `ff4308c` (`ProductVersion 0.2.3+ff4308c`; `c6536e5` added only the host evidence).
Not done, by design: upload, VirusTotal, tag, GitHub release, the Forge entry, the forward merge into `main` and the
`D:\SPT41vanilla` deploy (that machine needs Arena or an override too); all wait for the user. A DLL-only package for 0.2.2
owners would be exactly the two DLLs; whether one is published is the user's decision (the notes keep that placeholder).

## Merged into main (2026-10-19, late)

`release/0.2.3` was merged into `main` (`12c3731`), so 0.3.0 (four maps) carries the check. On main the client tags follow
`e66d512` (`[Arena]`, `[Bundles]`, `[NavMesh]`) and the load line reads `Version 0.3.0 loaded. Maps: ... Arena: found ...`.
Merged-build evidence (DLLs `0.3.0+12c3731`): GPU-2 with Arena 0.6.1 registered all four maps after the found line, loaded a
Bowl raid (0 warnings); GPU-1 without Arena logged the not-found lines on both sides and `Maps: none available`.

## Stage 2 (2026-10-19)

Stage 2 of the gate encrypts the map bundles with a key derived from the player's Arena level files; the design is
[../PLAN-ENCRYPTION.md](../PLAN-ENCRYPTION.md), the record [bundle-encryption.md](bundle-encryption.md). This check is unchanged and
still comes first: it compares names and sizes only and reads no game data. Stage 2 is what reads game data, and only after this
check has verified the installation: the first time a map's bundle is requested in a session the client hashes the level files that
map is keyed from (`keys/<map_key>.json` lists their paths and sizes), derives the key and opens the bundles through a decrypting
stream. It is not copy protection (an Arena owner can derive the key or decrypt the bundles), and like this check it stops nothing
for a player who owns Arena.

## Stage 2 key source v2 (2026-10-20)

Stage 2 no longer reads the level files: since 2026-10-20 a map's key comes from a fixed sample (16 blocks of 64 KiB per file) of its scenes'
raw resource streams (`sharedassets<N>.assets.resS` / `.resource`), because Arena's 0.6.0 to 0.6.1 hotfix rewrote every level file and left
those streams byte-identical. This check is unchanged and still runs first. Record: [bundle-encryption.md](bundle-encryption.md) "Key source v2";
design: [../PLAN-ENCRYPTION.md](../PLAN-ENCRYPTION.md) section 6.

## In-game notices (2026-10-22)

The check is unchanged, but its result no longer stays in the logs. Every failure site of `ArenaInstallation` now carries a machine-readable
`ArenaInstallCause` (the `Reason` strings and the gate texts are unchanged); the server records its verdict and the per-map registration
outcomes and answers the read-only route `GET /arenamapport/status` (codes only, no path); and the client shows a message at the main menu
that names the cause and the fix (`ClientNoArena`, `ClientOverrideBad`, `ServerNoArena` and the others). `General/ShowMapNotices` hides the
main-menu message only: it is not a switch for this check, and notice code never calls `EnsureVerified`. When no launcher entry is found
the client also logs one extra Info line listing the Uninstall subkeys that mention Arena or Tarkov (support evidence for a launcher that
registers Arena under another name; the server never logs it). Record: [map-notices.md](map-notices.md); host evidence pending.

## Red not-found line, advice once (2026-10-23)

User request: show the server's failure line in red and remove the doubled text. Before, the not-registered line read "... is registered
in the Windows registry (...); install Arena with the Battlestate Games launcher, or point the mod's Arena install path override at the
folder that contains EscapeFromTarkovArena.exe. The 4 Arena maps stay unregistered. Install Escape from Tarkov: Arena through the BSG
launcher, or set "installPath" in ...": the shared reason carried its own advice and the server appended its own. Now the shared reasons
of the registry causes (`NotRegistered`, `RegistryUnavailable`) state only the finding, the server line gives the advice once, and the
client line is followed by its existing hint line (`Set General/ArenaInstallPath ...`) as before. `ArenaInstallationGate.Evaluate` takes an
optional `notFound` sink; the mod passes `logger.LogWithColor(message, Spectre.Console.Color.Red)`, which SPT 4.1.6 logs at Information
level with the colour (`SptLogger.LogWithColor`, the same path its own `Error` uses for red), so the file log is unchanged in level.
Tests: `ArenaMapPortModGateTests` expect the single `LogWithColor` call with `Color.Red`; `Evaluate_SendsTheNotFoundLine...`,
`Evaluate_WithoutANotFoundSink...` and `Decide_TheRegistryVerdictsGiveTheLauncherAdviceOnce` pin the routing and the single advice;
dotnet test 2127/2127.

Host evidence (GPU-1, no Arena, 2026-10-23 10:08 to 10:18, both DLLs rebuilt on `48c47cd` plus this change, deployed with `2 to
overwrite`): the server log file line `[2026-10-23 08:12:45.405][Information][ArenaMapPort.Server.ArenaMapPortMod] [ArenaMapPort] Escape from
Tarkov: Arena not found: No Escape from Tarkov: Arena installation is registered in the Windows registry (...EscapeFromTarkovArena_live).
The 4 Arena maps stay unregistered. Install Escape from Tarkov: Arena through the BSG launcher, or set "installPath" in
C:\SPT41\SPT_Runtime\user\mods\ArenaMapPort\config\arena-install.json for an installation without the launcher's registry entry.`
(UTC). The server first came up through `gpu-run deploy-wait --server`: its console window showed every line white, SPT's own green
`Webserver unter ... gestartet` too, so that console renders no colours. Started again from the host's `SPT.Server` desktop shortcut, the
console showed SPT's green and yellow lines and the ArenaMapPort line in red (screenshot `work/screenshots/notices/server-not-found-red-host1.png`).
The client log of the next EFT start: `[Arena] Escape from Tarkov: Arena not found: No Escape from Tarkov: Arena installation is registered
in the Windows registry (...EscapeFromTarkovArena_live).`, then the unchanged hint line, the registry diagnostic line and `Maps: none
available`; the main-menu notice showed as before.
