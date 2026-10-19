# ArenaMapPort — working notes for agents

ArenaMapPort imports the maps of Escape from Tarkov: Arena (read from the user's own Arena
install) into SPT 4.1.5 (EFT 0.16.9.5, Unity 2022.3.43f1) as separate locations: a Python extractor,
a Unity 2022.3.43f1 batch importer that rebuilds scenes, materials, colliders and a fresh NavMesh into
asset bundles, a BepInEx client plugin that repairs everything a ported scene lacks at raid time, and an
SPT server mod that registers the locations. Fourteen maps retain development inputs; **Equator, Bay5 and AirPit** are registered and staged
for release 0.2.2 (Equator and Bay5 were the first release; AirPit was added in 2026-10 on `feature/airpit`; 0.2.0 was built and withdrawn on 2026-10-16). All three have native
playtest evidence. Release 0.2.3 (branch `release/0.2.3`, published 2026-10-19) is 0.2.2 plus the Arena installation check; 0.2.2 was withdrawn
the same day (GitHub release, tag and Pixeldrain files deleted, nobody had downloaded it), and so were 0.2.1 and 0.1.0
(releases with their assets, tags, Pixeldrain files): only 0.2.3 is downloadable, every package without the check is gone. The maps exist to be played through the user's
ArenaMode mod (arena deathmatch with respawns); see the decisions below.

Read this first, then [HANDOFF.md](HANDOFF.md) (dated working state, what is verified, what is next),
then [docs/progress/INDEX.md](docs/progress/INDEX.md) to find the progress log for the area you touch.
[docs/SPEC.md](docs/SPEC.md) is the original spike specification; its "Decisions 2026-09-20" section at
the end overrides the spike framing. `README.md` is the user-facing summary.

## Decisions that shape every task (user, 2026-09-20)

Current source (2026-10-03) is Arena 0.6.0.47752 / Unity 6000.3.6f1. Export tools
were checked against complete fresh Equator/Bay5 extracts; see
[the source audit](docs/progress/arena-update-20261003.md). Accepted v5 packages
still contain the prior 0.5.4 source. Audit outputs under `work/arena-update-audit/`
are not accepted build inputs. Keep the SPT bundle target on Unity 2022.3.43f1.

- **ArenaMode is the target.** Ordinary SPT raid playability (loot, quests, scav raids, insurance,
  transits) is not a goal. A plain SPT raid started from the menu is only the test vehicle.
- **No loot on any map, ever.** The server keeps the empty loot shape. Do not author or extract loot.
- **No scav raids.** `DisabledForScav = true` stays.
- **Bundles are distributed as built** (`dist/`), like other SPT map mods. Bundles still never go into git.
- **Release 0.2.2 = Equator, Bay5 and AirPit** (0.2.0 was built and withdrawn the same day; 0.2.1 went to GitHub and Pixeldrain
  only; 0.2.2 adds SpawnGates, AirPit's original north box, the renamed Equator sidecar and the crates under AirPit's tarp
  (package v9) and is the Forge candidate). The first release (user, 2026-09-25) was Equator and Bay5 only; AirPit was
  added in 2026-10 on `feature/airpit` and is registered and staged. The other eleven maps retain source and work/build inputs,
  but are excluded from ArenaMapRegistry.All and dist; Catalog retains their identities. Make these three maps as good as they can be (native content,
  bots, audio, lighting, physics — see docs/PLAN-TWO-MAPS.md) BEFORE ArenaMode features such as team starts and
  pickups, which come later on all three maps.

## Hard rules — never do these

- Never write into `D:\SPT41` by hand. It is a Syncthing mirror of the GPU test host; the only write path is
  `pwsh -NoProfile -File tools/deploy-to-spt.ps1 -SptRoot 'D:\SPT41' -Confirm` (quote the path: an
  unquoted backslash in bash once created a stray folder). Without `-Confirm` the script is a dry run.
  The optional DynamicMaps packs use the same script with `-DynamicMapsPackage <pack-folder>`
  and `-DynamicMapsMap Equator|Bay5|AirPit` (default Equator);
  that separate mode copies only its two owned map-data files into maschine-ArenaMapPort/dynamicmaps.
  The user-authorized `-RemoveLegacyDynamicMaps` migration removes only the old Equator/Bay5
  packages from DynamicMaps/Maps, after both replacements and the adapter DLL match dist.
  Dry run first; no recursive cleanup or changes to other maps (see `tools/dynamicmaps/README.md`).
  The separate `-RemoveDebugHelper` cleanup backs up and removes only the retired
  ArenaMapDebug DLL at either known plugin path. Dry run first; close EFT before removal.
  The separate `-RenameClientFolder` migration renames only the complete client
  directory from `ArenaMapPort` to `maschine-ArenaMapPort`; server/config stay unchanged.
  Dry run first; close EFT on the synchronized host before confirming. It refuses links
  and destination collisions. New builds/deployments use the prefixed client directory.
  The separate `-RemoveLegacyShaderSidecar` migration (2026-10-17: Equator's shader sidecar renamed to
  `material-shaders.arena_equator.json`, one rule for every map) backs up and removes only the old
  `material-shaders.json` after the renamed file and the client DLL match dist. Order: full deploy first (not
  `-DllOnly`, the renamed sidecar must be installed), restart EFT so the new client DLL is loaded, then this mode.
  Dry run first.
- Never write under `C:\SPT41*`, `D:\sptmaps`, `D:\Battlestate Games` (the read-only Arena source) or
  `D:\SPT41\Development\ArenaMode` (the user's other mod; read it if you must, never edit it).
- Never edit host/vanilla config files or third-party mod configs on either install.
- Never commit BSG-derived data. `work/`, `dist/`, `arena-src/`, `analysis/screenshots/` and the Unity
  import/reference folders are gitignored on purpose. Git-tracked files are code, docs, sidecars, tests.
- Never reuse code from ManimalLighthouse (no license). Icebreaker, Labs and InterchangeRework are MIT:
  reused snippets get an entry in `THIRD-PARTY-NOTICES.md`. Approach-only borrowing (e.g. HiddenCaches'
  smoke idea) is noted there too, without code.
- Never delete `bin/` or `obj/` (an agent's `rm -rf` once hit an MCP proxy running from there). Let
  `dotnet` manage them.
- BepInEx persists config values in the user's `.cfg`. Never change an existing key's default or meaning
  silently: add a new key, or use the `DefaultsRevision` re-seed pattern in `WeatherRig.cs`.
- `D:\SPT41vanilla` (the user's own test bed, no SAIN) receives only host-verified builds via the same
  deploy script with `-SptRoot 'D:\SPT41vanilla'`; restarting its server is the user's job.
- Never add a switch that disables the Arena installation check (`ArenaInstallation` in Shared; release/0.2.3, user
  decision 2026-10-19: Arena is sold separately from EFT, so the maps need an installed Arena). The override paths
  (server `config/arena-install.json`, client `General/ArenaInstallPath`) still verify the installation. It is a
  good-faith gate, not copy protection: never describe it as more. See
  [arena-install-check.md](docs/progress/arena-install-check.md).
- Git: author `maschine34675 <163792852+maschine34675@users.noreply.github.com>`, commit only when the
  user's task implies it, and end commit messages with the agent's co-author trailer. Working tree
  should be clean after a delivered task.
- The user communicates in German; code, comments and docs are English.

## Layout

| Path | What |
|---|---|
| `ArenaMapPort.Client/` | BepInEx/HarmonyX plugin (netstandard2.1). One file per repair; every patch is gated on `ArenaMapRaid.IsLoaded()` / `IsOwnedScene()` so vanilla maps are untouched. `HarmonyPatches.cs` installs everything in order and binds config. |
| `ArenaMapPort.Server/` | SPT server mod (net10.0): registers each map from `db/<key>/base.json` (including `arena_equator`), locale, brain tables, bot caps, APBS shim. |
| `ArenaMapPort.Shared/` | `ArenaMapDescriptor` / `ArenaMapRegistry`: the per-map table (keys, scenes, infiltration, exits, opened barriers). Compiled into both. |
| `ArenaMapPort.Server.Tests/` | xunit; the only .NET tests. The client has none — test what is Unity-independent here. |
| `tools/maps/<key>.json` (+ `.bots.json`, `.exits.json`) | Per-map sidecars for the Python/Unity side; mirrored by the descriptor and guarded by mirror tests. |
| `tools/extract/`, `tools/bundle/`, `tools/preset/`, `tools/server/make_base_json.py`, `tools/validate/`, `tools/tests/` | The build pipeline and its pytest suite. |
| `UnityProject/Assets/ArenaMapPort/Editor/` | Importer, shader rebinding, `NavMesh/BakeNavMesh.cs`, bundle build. Runs headless via `tools/build_all.ps1`. |
| `work/extract/<map>/` | Extracted package: `scenes/*.json` (full hierarchy with local transforms, layers, components), `meshes/*.mesh.bin`, textures, materials. Offline source of truth for "where is object X" (compute world positions through the parent chain). |
| `work/build/<map>/`, `dist/` | Built bundles; `dist/` is what deploy copies. |
| `docs/progress/` | One log per milestone or fix, with host-test evidence. `INDEX.md` says which are current. |
| `docs/MAP-RECIPE.md` | How to give a built map spawns, waves, PMC squads, exits and opened barriers (sidecar schemas + host checklist). |
| `docs/reviews/` | The two 2026-09-13 reviews of SPEC.md (unity-pipeline, spt-runtime lens); resolved in SPEC's "Review resolution", historical. |
| `analysis/` | Read-only evidence: decompiled SPT server, Arena census, screenshots. |

EFT client decompile for reference: `D:/SPT41/Development/_decompiled/*.cs` (0.16.9.5). Every obfuscated
member name pinned in the client (`method_N`, `vmethod_N`) is verified there and installed through
`HarmonyPatches.Verify()`, which logs and skips a missing target instead of throwing.

## Release configuration

Logging: Info is reserved for a concise startup/map-availability summary or actionable
player information. Successful patches/repairs, counts, file paths, configuration and
cleanup use Debug. Expensive probes, per-object dumps and GPU readbacks additionally
require DeveloperOptions.Enabled; gate their work, not only the log call. F7–F10 output
uses Debug (BepInEx's disk/console filters must include it when collecting diagnostics).
Real degraded behavior and failures retain Warning/Error in player mode. Never suppress
Unity/Harmony warnings globally; repair an owned cause or document its origin.
Both the server and the client log the Arena installation state at Info at startup (the verified
installation, or why none was found); see [arena-install-check.md](docs/progress/arena-install-check.md).

Player mode is the default: two normal F12 controls, `Appearance/NaturalEquatorNight` (next raid) and
`General/ArenaInstallPath` (the Arena installation override of release 0.2.3, empty = automatic).
`General/DeveloperMode` is visible with F12's Advanced settings filter enabled.
It is latched on startup, exposes supported-map controls and enables F7–F10 diagnostics.
Never gate functional repairs behind it. All tuning binds/enumeration must use
`Plugin.Instance.Settings`, never `Config`: in normal mode Settings reads the optional
developer archive without saving, while Config contains only the three public-file entries
(DeveloperMode, NaturalEquatorNight, ArenaInstallPath).
In developer mode Settings is Config so ConfigurationManager sees the full controls.
Never explicitly save normal-mode Settings. CompactConfiguration archives existing tuning
before compaction and preserves unknown keys. Ambient bindings happen in Plugin.Awake.
See [compact configuration](docs/progress/compact-player-config.md) and
[release preparation](docs/progress/first-release-prep.md). The staging helper
`tools/park-unreleased-dist.ps1` archives only exact known parked-map outputs under ignored
`work/release-parked/`; do not delete their source/extract/build inputs or clean live installs.

## Build, test, deploy

After deploying through the Syncthing mirror, verify the GPU host's server DLL hash
before restarting SPT. UiBridge's loaded-plugin comparison covers the client only;
confirm changed server behavior in the new process log as well.

```powershell
# client plugin (Release; DllOnly skips re-staging the bundles into dist/)
dotnet build ArenaMapPort.Client/ArenaMapPort.Client.csproj -c Release -p:ArenaMapPortDllOnly=true
# server mod + tests (always -c Release: a Debug test run would redeploy a Debug DLL into dist/)
dotnet build ArenaMapPort.Server/ArenaMapPort.Server.csproj -c Release
dotnet test ArenaMapPort.Server.Tests/ArenaMapPort.Server.Tests.csproj -c Release
# Python pipeline tests (unittest, no pytest installed) and the PowerShell log-scan tests
python -m unittest discover -s tools/tests -p "test_*.py"
pwsh -NoProfile -File tools/tests/test_unity_log_scan.ps1
# rebuild Equator's bundles + NavMesh from its extracted package (≈ 15 min). The [bool] parameters only parse
# from a PowerShell prompt or `pwsh -Command "& '...build_all.ps1' ... -BakeNavMesh \$true"`; `-File` from bash
# turns "$true"/1 into strings and the script refuses them.
pwsh tools/build_all.ps1 -MapKey arena_equator -MapDescriptor D:/ArenaMapPort/tools/maps/equator.json -PackageDir D:/ArenaMapPort/work/extract/equator-native-v5 -OutputDir D:/ArenaMapPort/work/build/full -BakeNavMesh $true -Verify $true -SkipGameRefsSync
# then a client Release build WITHOUT DllOnly stages the new bundles into dist/, and:
pwsh -NoProfile -File tools/deploy-to-spt.ps1 -SptRoot 'D:\SPT41' -Confirm
```

Equator's package predates the `-FullMap` convention (its manifest lists the scenes in a different order
than the descriptor), so Equator is built WITHOUT `-FullMap`. Every other map uses `-FullMap` with
`work/build/<stem>/` and `tools/maps/<stem>.json` (exact commands in `docs/progress/remaining-maps.md`).
Current accepted inputs are `work/extract/equator-native-v5` and
`work/extract/bay5-native-v5` (2026-09-28: v5 adds original StaticDeferredDecal components, their materials
and textures, and `native_features.static_decals`; see `docs/progress/native-decals.md`). These retain
the earlier v4 features (2026-09-26: v3 — NavMesh modifiers/volumes + `navmesh_areas`, Rigidbody +
SmallPhysicsObject with SoundType, `raincover/raincover.json` — plus the light-culling feature: CullingLightObject /
VolumetricLight / LightFlicker component dicts and `native_features.light_culling`; see
`docs/progress/two-maps-item-{4,5,6,7}-*.md`). A package is
produced by `extract_equator.py --native-features --scenes all5 --output <dir>` / `extract_bay5.py
--output <dir>` followed by `python tools/extract/refresh_native_volumes.py <dir>`.
The older `equator-native-dust`/`bay5-native-dust`, `equator`, `bay5` and `bay5-background` packages
omit later native features (and old Bay5 omits the harbor background), and the v3 packages lack the light-culling
components; v4 lacks native projection decals. Do not rebuild either accepted map from those older packages.
Bay5 uses its v5 package with `-FullMap`
and `-OutputDir D:/ArenaMapPort/work/build/bay5`. AirPit (on `main` since the 2026-10-17 merge of `feature/airpit`) builds from
`work/extract/airpit-native-v9` (2026-10-17: v8 + the three level38 SOO crates under the tarp at (-31.3, -16.7), explicit
roots with the five same-named siblings excluded by position; v8 = 2026-10-13: v7 + the rest of the furnished hall; v7 = v6 + the furnished shops. Both
come from the two PARTIAL scenes `Arena_Airpit_Main_hall` (38) and `Arena_Airpit_Light` (37), selected by the plan's
`select` records (roots / region_roots on the decoration box, `skip_twins_of` 324/325, `exclude` for the 208 props
level324 replaced plus the five crate siblings, `twin_descendants: import_missing` for the parts level325 dropped from its twin fixtures); v6 = v5 +
native ambient audio; via `extract_map.py --plan tools/extract/plans/airpit.json --native-features` followed by
`refresh_native_volumes.py`. The contract requires audio and the `partial_scenes` pins, so the older packages fail the
validators; the five full scenes of v9 are byte-identical to v6/v7/v8) with `-FullMap` and `-OutputDir
D:/ArenaMapPort/work/build/airpit`. `validate_extract.py` on this package needs the 1800 s extractor timeout (the
default since 2026-10-13; the extraction alone takes about 5 min). Always give `-MapDescriptor` an absolute
path: Unity resolves relative descriptor paths from its project directory.

Native spatial acoustics use separate ignored `work/extract/spatial-audio/<map>` inputs, regenerated
by `python tools/extract/extract_spatial_audio.py`. Release/DllOnly builds require and stage their
graph JSON, original bake and room-tone WAV. Do not rebuild scene bundles for this runtime graph port.
The native loader needs portal endpoints wired before room initialization; `RoutesDataCount` counts
pair ranges, not individual routes. Start room tones only after `GameStatus.Started` or the raid audio
reset stops them. See [spatial-audio.md](docs/progress/spatial-audio.md).
Room-dependent ambient reverb adds `mixer_effects` to that same graph JSON. Keep
its original 26-parameter table and detailed-type compatibility mapping together.
EFT mutes Equator's original hall return; the client scopes its compatible routing
to the imported ambient group only. See [room-reverb.md](docs/progress/room-reverb.md).

Original per-light flares use ignored `work/extract/multiflare/<arena_map_key>` inputs.
Generate each with `tools/extract/extract_multiflare.py --map <key> --output <dir>`.
Release/DllOnly builds stage JSON plus two raw textures per map. The original Arena
atlas differs from EFT's; keep it. NativeMultiFlare owns isolated native batches,
never the global weapon-light manager. See `docs/progress/native-multiflare.md`.

Optional Arena audio/UI exports remain development inputs under ignored `work/extract/arena-audio`
and `work/extract/arena-ui`. Neither current mod consumes them; Release/DllOnly builds no longer stage
them. The release cleanup archives old dist copies without changing the extraction inputs.
Their extractors and provenance remain available; see `docs/progress/arena-audio.md` and `arena-ui.md`.

The region-selection artwork is a separate, consumed export: run
`python tools/extract/extract_selection_map.py` against the installed Arena data. Release/DllOnly builds
require its PNG and manifest under ignored `work/extract/map-selection` and stage only those two files
to the client's `map-selection` directory. The extractor pins reviewed pixel content and framing;
review a changed Arena source before updating either pin. The runtime image replacement is optional
ArenaMode integration: only its exact selected menu flow receives the artwork; ordinary PMC/Scav
and installations without ArenaMode retain EFT's original. Never gate this on map ID or a global
Arena flag alone. See `docs/progress/selection-map.md`.

Server builds require original descriptions from
`python tools/extract/extract_map_descriptions.py`. The ignored output under
`work/extract/map-descriptions` stages to the server mod's `locales` directory.
Keep these BSG-derived texts out of tracked source; see `docs/progress/official-map-descriptions.md`.

`-FullMap` also regenerates that map's base.json and preset from the sidecars; to regenerate only base.json
after a sidecar change, add `-SkipUnityBuild` (reuses the bundles). For Equator, base.json is regenerated
with `python tools/server/make_base_json.py` (see its header for the arguments).

After a deploy, Syncthing delivers to the host within ~20 s. A new client DLL needs an EFT restart; a new
server DLL or base.json needs a server restart (the host server is restarted by closing its terminal
window and double-clicking the desktop shortcut; Syncthing only replaces the server DLL once the process
is closed). The standalone `maschine-ArenaMapDebug.dll` and its four shortcut buttons
were retired for release preparation. Use the ordinary PMC/ArenaMode selection flow
for testing. Do not redeploy the helper; its source remains in the separate development
project if future debugging explicitly needs it.

## Host testing (GPU host, via MCP)

Two MCP servers drive the test host. Since 2026-09-26 the host is **SPT-GPU-2**, reached through `uibridge-2`
(sees inside the running EFT) and `windows-gpu-2` (clicks, keys, screenshots); the un-suffixed `uibridge`/`windows-gpu`
servers belong to the retired first host — do not test there. The tool names below are written without the suffix.
Same click points as before, raid start ≈ 9 s, `D:\SPT41` still mirrors it. Recipe that has worked for every host test:

0. `uibridge_help topic=overview` once per session (UiBridge proxy 0.8.1+): the playbooks (start, raid, inventory,
   shooting, gpu-run, troubleshooting) hold the general sequences, arguments and failure pictures; the steps below are
   only what is specific to this project.
1. `uibridge_status`: is another session using the host? Is the loaded ArenaMapPort DLL identical to the
   VM build ("VM build = host disk = loaded")? Is the EFT window focused (the first click into an unfocused
   window only focuses it)?
2. Restart EFT when the client DLL changed: `node gpu-run.mjs restart --host host2` (in
   `D:\SPT41\Development\UiBridge\tools\gpu-run`; since UiBridge 0.7.0 it quits EFT, waits, starts the SPT
   server if needed and EFT directly, no SPT launcher, ~65 s; `start --host host2` when EFT is not running).
   By hand only as a fallback: click points from `uibridge_snapshot contains=EXIT` / `YES`,
   `uibridge_wait unreachableFor=5`, then `node gpu-run.mjs start --host host2` and
   `uibridge_wait screen=MainMenu afterRestart=true`. Never click the launcher's "Wipe Profile" checkbox.
3. Start the raid with the debug buttons (since 2026-09-27: `EQUATOR / WITH BOTS` (299,718), `EQUATOR / NO BOTS`
   (299,798), `BAY5 / WITH BOTS` (299,878), `BAY5 / NO BOTS` (299,958) — the old single-button positions moved;
   check with `uibridge_snapshot contains=BOTS`), then `uibridge_wait raidStarted=true`. Use NO BOTS for visual or
   measurement checks: SAIN bots kill the debug player within ~15–40 s.
4. Observe: `uibridge_log` (BepInEx log, filter by `[ArenaMapPort]` tags), `uibridge_player`,
   `uibridge_bots` (positions, brain layers), `uibridge_teleport` (has a NavMesh reachability check —
   `reachable=false` for islands; `dryRun=true` to probe connectivity), `uibridge_look`, screenshots via
   `windows-gpu Screenshot`. Letter keys do not reach EFT; use `uibridge_command` for game commands.
5. Leave: `uibridge_command Escape`, click `DISCONNECT` from `uibridge_snapshot`, `uibridge_wait inRaid=false`.

Keep diagnostic raids short and leave before doing extended source analysis. The pause menu does not
stop raid time or energy/hydration loss; an expired raid still receives normal MIA treatment. Fresh EFT
processes can reset offline bot settings, so select bot amount None through the normal menu before a
botless comparison. Disable bosses before selecting None, while that checkbox is still enabled.

Log evidence goes into the milestone's `docs/progress/*.md` with timestamps; a change is "verified" only
after such a host raid. The in-game `F10` hotkey (SceneInspector) dumps the object under the crosshair to
the log; the user uses it to point at things.

## Conventions

- One repair per file, a class doc comment that says what vanilla does, what the ported scene lacks, what
  the repair does and which host test proved it. Log lines start with `[ArenaMapPort][<Area>]`.
- Per-map data belongs in `ArenaMapDescriptor` (Shared) and the matching `tools/maps` sidecar, never as a
  literal in a repair. Object names repeat inside Arena scenes: address scene objects by hierarchy path.
- New tuning keys: `Plugin.Instance.Settings.Bind("<Section>", "<Key>", default, "<what/why, takes effect when>")`.
- Progress docs are appended, not rewritten; host-test paragraphs carry date, time, build id, log quotes.

## Traps (each cost hours once)

- EFT calls `SpawnPointsCollection.CreateFromScene` twice per raid; raid-start repairs must be idempotent.
- Async bot creation swallows exceptions (`.HandleExceptions()`); nothing reaches the log. Harmony
  finalizers on async stubs miss post-await exceptions; `BotOwner.method_10` catches everything into
  `BotState = ActiveFail`. `BotSpawnProbe` (`[Bots] ActivationDiagnostics`) is how those were found.
- Arena's AI holder components have no list initialisers, the voxel grid is 0×0×0 and there are no patrol
  ways: `AICorePointRepairs` and `PatrolWayRepairs` synthesize them. Bots need SPT brain-table rows per map
  key (`ApplyBrainTable`, `ApplyPmcBrainTables`) and an APBS key remap (`ApbsCompat`), or they never spawn.
- NavMesh: baked from EVERY physics collider in the content scenes (any layer but Water), including things the
  client removes at raid start — TeamFight `LevelBorder` walls, `SPAWN_BOUND` boxes, booth door leaves, listed
  barriers. `BakeNavMesh.RoundOpenedReason` excludes those by name; anything else the client disables at
  runtime must be added there too, or it stays a wall for bots. The bake volume is the renderer union + 10 m
  unless the descriptor carries `bake_bounds` (AirPit: x −57..37, y −2..8, z −83..83 = the AABB of its
  `authored_borders` (z 83.6) rounded; from 2026-10-12 to 2026-10-17 it ended at z 70 to keep bots out of the north
  spawn pocket, now the held passport gate does that, see `docs/progress/airpit-team-boxes.md`. Equator/Bay5 have none — their ground floors sit around y −8..12, the upper rings are
  decoration). Arena bounds AirPit's TeamFight area by a data polygon, not colliders (`ExcludeLevelBorders` in its
  culling scene); `AuthoredBorderRepairs` raises invisible movement-layer walls along that outline at raid start. `navmesh_bake_exclusions` drops a
  subtree from the bake WITHOUT deactivating it at runtime — no map uses it any more: AirPit's four "route cutters"
  (shop shutters) put NavMesh through solids and the 2026-10-12 host walk ended face-first in the DonB_Ramen shutter.
  A closed gate that Arena opens for the mode belongs in `opened_barriers` instead (AirPit's two
  `Prison_Metal_Fence_01` portal gates: closed fence active, swung-open halves inactive).
  `navmesh_bake_layers` restricts the collected layers (AirPit: Arena's own 9/11/13/18/30). The offline coverage
  metric of `navmesh_diff.py` dilates by one cell and misses one-cell cuts; check routes with Dijkstra on the built
  bundle (`navmesh_diff.load_bundle_mesh`) and in-game with `uibridge_walk` ("N m along the navmesh"). Probe connectivity in-game with `uibridge_teleport dryRun=true` (reachability = same NavMesh
  component); offline, `work/extract/<map>/scenes/*.json` + `meshes/*.mesh.bin` give every collider's world
  triangles (see docs/progress/stage1-combat.md for the script pattern).
- NavMesh modifiers: `NavMeshModifier`/`NavMeshModifierVolume` exist only for the bake. `BuildBundles` strips them
  (`NavMeshModifierImporter.StripBakeOnlyComponents`, re-saving the content scenes) before `BuildAssetBundles`;
  without that the scenes bundle carries a `Unity.AI.Navigation` MonoScript and `patch_monoscript_identity.py`
  fails. In `BakeNavMesh` the Triggers/LevelBorder layer rule applies to collider sources only — the wire markups
  sit on Triggers-layer owners on purpose. Expect Bay5 `81 area-override, 28 ignoreFromBuild, 134 volume` (only
  hierarchy-active components register) and Equator `0/31/0` with an unchanged 10404/4572 triangulation.
- Rain cover: the falling rain is culled by the shader against the depth photo `DepthPhotograper` renders
  (Terrain layer + `WeatherObstacle` mesh); `RainCoverRepairs`' heuristic feeds that photo on every ported map and
  must never be switched off without a replacement. Arena's Bay5 `DryPlane` set is a Lighthouse leftover
  (`Lighthouse_DryPlanes`, off-map) and is dropped at extraction; Equator ships none. A "verified" rain check needs
  the F8 depth readback plus a screenshot of the falling streaks, not the floor wetness. Equator's atrium roof is
  broken on purpose (pane mesh inactive in Arena's raw level, "broken glass atlas" material, inactive glass-shard
  debris under it; the puddles are generic mall dressing): rain through the lattice is by design (user decision
  2026-09-26), `FullyIndoorCover` is an opt-in dry mall, and puddle ripples follow the global
  rain value (`RippleController`), never the depth photo — no cover setting removes them.
- Light culling (item 7): `CullingLightObject`/`VolumetricLight`/`LightFlicker` are `[ExecuteInEditMode]`, so their Awake
  runs inside the batch import when the importer calls `AddComponent` — `LightCullingImporter` keeps the component when
  Awake throws (no `FlickerSystem`/`ShadersFinder` in the editor), captures the sibling Light's enabled/intensity before
  `AddComponent<CullingLightObject>` (Awake forces `_light.enabled = true`, `OnDisable` writes the cached intensity)
  and sets `enabled` last. At raid time `CullingLightObject` keeps writing `_light.intensity = float_1 * fade`, so
  `ArenaLightingRepairs` also writes the private `float_1` (reflection) or the brightness uplift is erased; the
  `CullingManager` singleton and the camera's `VolumetricLightRenderer` are client fallbacks, not package data.
- Bloom/glare: `UltimateBloom` controls broad glow. Equator/Bay5 defaults now match the donor threshold
  2.10 and intensity 0.35; `BloomDefaultsRevision` migrates exact legacy defaults once (2026-09-29).
  The early horizontal streak is **BloomAndFlares before Prism**:
  `AnamorphicFlareTuning` restores it on Equator and moves its native render before HDR-to-LDR, with captured
  state restored on disable/unload. Merely enabling its HDR mode after Prism cannot recover highlights.
  New Equator controls: `AnamorphicFlare`, `AnamorphicFlareHDR`, `AnamorphicFlareIntensity`. `CameraRepairHooks`
  uses SetCamera, not a one-time event subscription (CameraManager.Dispose clears that event between raids).
  F7 logs each camera effect separately. Details: `docs/progress/equator-anamorphic-hdr.md`.
  Verify light work with F7 (`LightCullingProbe`: distance-fade bands, volumetric state, flicker samples), not with
  screenshots — the SceneInspector (F10) only hits mesh colliders, never lights. Every culling object is also
  frustum-tested on `BoundingSphere(position + _shift, _radius)` unless `_cullByDistanceOnly`; Awake replaces
  `_radius` with `Light.range` only for Point lights, so Spot lights need the authored sphere (component schema 2,
  2026-09-27 — schema 1 left 1 m at the lamp and Equator's atrium floodlights went dark whenever the lamp left the view).
- Footsteps: EFT picks the step sound from the `BallisticCollider` material under one downward raycast (layers
  Terrain/HighPolyCollider/TransparentCollider/Water); Arena's puddle decals have no collider at all, so they sound
  like the floor beneath without an explicit mapping. `PuddleFootstepRepairs` now maps Bay5's reviewed water
  material to the native Puddle audio set using actual mesh triangles, UVs and shader red-channel coverage;
  dry holes and oil are excluded. Never use texture alpha or whole quad bounds as the wet footprint, and never
  unconditionally linearize the mask (this EFT project uses Gamma). F10 logs native/result surfaces and clip
  examples. Historical packages showed no lost water collider; the user's earlier sound remains unexplained.
- Harbor water: Bay5 now translates the source WaterContainer settings to native `WaterForSSRv3`, using
  `work/extract/bay5-water` (regenerate with `tools/extract/extract_water.py`). The four original raw texture mip
  chains and water.json stage with client builds, including DllOnly. They are BSG-derived and must stay ignored.
  Do not assign Hidden/WaterForSSR as an ordinary mesh material: its two passes need the native deferred renderer.
  Dispose captured command buffers explicitly after native OnDisable; native cleanup only detaches them.
  See `docs/progress/bay5-water.md` for source, controls and host evidence.
- Static projection decals: original material pointers live on the native component, not MeshRenderer.
  Arena's serialized field order differs from EFT: LastTexSize precedes the material pointer. The importer
  rescales pixel corners against the actual target texture, retaining normalized atlas regions. Rebind
  component materials before the native manager clones them. Destroy the owned manager for live off;
  setting enabled=false does not stop Camera.onPreCull, and ReInitialize duplicates subscriptions.
  Only one manager may exist. GPU-2 accepted both maps, live comparisons and repeat/cross-map raids
  on 2026-09-27/28; see `docs/progress/native-decals.md` for counts, cleanup and evidence limits.
- BepInEx config re-seeds (`config re-seeded to defaults revision N`) are logged before UiBridge starts recording;
  check the result with `uibridge_config … DefaultsRevision` / the key's value instead of waiting for the line.
- Syncthing replaces the client DLL while EFT runs; EFT keeps the old code until restarted. Trust
  `uibridge_status`'s MVID comparison, not file hashes. The same holds for rebuilt bundles: a loaded map
  bundle stays cached in the EFT process, so a new NavMesh/scenes bundle needs an EFT restart before the next
  raid (the `[ArenaMapPort][NavMesh] added navmesh ... N vertices` line tells which bake is live).
- `uibridge_wait logMatches`: only lines logged after the wait started count (use `sinceSeq`).
- Partial scenes (AirPit level38/37, `docs/progress/airpit-release.md` "Phase 1"/"Phase 1b"): `PROJECTION`, `PHYSIC`,
  `VARIANT`, `PROPSnoCulling` are LEVEL ROOTS of level38, not children of `SBG_Arena_Airpit_Main_hall`; the `*` in
  `ChinaPlace*` is part of the Arena name (no wildcards); `FLOOR_DAMAGE ` keeps its trailing space. The twin rule compares
  unit ROOTS only (base name, component multiset, activity, 0.02 m per axis) - a reference twin may have lost or replaced
  children, which is what `twin_descendants: import_missing` recovers and what `exclude` takes out again when the
  reference carries a deliberate replacement (level325's SupportLight stand-ins, emptied LODGroups). `exclude` entries
  name one node each (`at` within 0.05 m when sibling names repeat, as in VARIANT) and fail closed: a census list with
  duplicates or bare ambiguous paths is refused. Counters: `selected` counts BEFORE descendants/exclusions and
  `sum(selected) + sum(descendants_added) - sum(excluded) == units`. Re-pin a changed selection with
  `python tools/validate/partial_scope.py <package>` (contract block) plus the manifest totals; the live pins in
  `test_scene_select.py`, `test_native_contracts.py` and `test_airpit_bake_inputs.py` follow. Sidecar `opened_barriers`
  and the C# descriptor must change in the same commit (the bake reads the sidecar, the client the descriptor).
- Layer-0 blockout: BSG's ShootOut cover includes white `Standard`-material panels on layer Default (AirPit
  `SBG_Arena_AirPit_ShootOut/SOO/Cube (10) Group`, three 13 x 3.3 x 0.16 m boxes on the south-west roll-door lines).
  Arena's bake mask (9/11/13/18/30) never sees layer 0, so the NavMesh runs through them while the player collides -
  the shutter-trap picture without any bake-only exclusion. `test_airpit_bake_inputs` reports the layers the mask drops;
  a live layer-0 source inside the play space is a blockout to open (`opened_barriers`) or to keep out of the mesh.
- Bash tool heredocs over ~180 lines fail; write scripts with the Write tool and run them.
- Fresh 0-HP profile deaths on the host were the skipped heal screen, not the map.
- Shader stand-ins (PLAN-AIRPIT item 11): EFT 0.16.9.5 carries `Custom/Billboard_FogSheet_Simple`, both `Cloth/ClothShader`
  variants and the ANGRYMESH rock shader itself, inside Customs' scene shared assets (`sharedassets13/5/15.assets`), not in the
  global `StreamingAssets/Windows/shaders` bundle a name census reads. They are loaded in-process by raid start, so
  `ShaderRebind`'s direct lookup wins and the AirPit substitutes never run (log `resolved to EFT's own loaded shader`); the
  fog-sheet haze is Arena's own look and `AirPitFogSheetOpacity` has nothing to refresh. Keep the stand-in rows as the safety net.
- Harmony on generic classes: Mono compiles ONE body per generic method for all reference-type instantiations, so a
  patch on `BaseLocalGame<EftGamePlayerOwner>.InitWeather` also runs for `HideoutGame : BaseLocalGame<HideoutPlayerOwner>`
  (every hideout visit) with the HideoutGame as `__instance`; a `FieldInfo` of the closed EftGamePlayerOwner type throws
  `ArgumentException` on it. From 2026-10-07 (`04be98c`) to 2026-10-14 that exception left `TimeOfDayRepairs.Prefix`
  before its map gate, `HideoutGame.smethod_6` faulted and the hideout never left its loading screen. Nothing reached
  BepInEx's log: EFT writes faulted load tasks only to its own `Logs/<session>/*errors.log` (host:
  `\\192.168.178.109\C$\SPT41\Logs`), so grep there first for a hang without a BepInEx trace. Rules: a prefix on a
  generic-class method takes `object __instance`, type-tests it before any reflection, and never lets an exception
  escape into EFT's load chain (vanilla must continue when a repair fails). Host acceptance of a client build includes
  opening the hideout once.
- Scene-wide searches in periodic code: `Object.FindObjectsOfType`, `Resources.FindObjectsOfTypeAll` and
  `GetComponentsInChildren` over a ported scene cost about 30 ms on the main thread (55k objects on AirPit) and must
  never sit in Update/LateUpdate, a coroutine loop or a timer. `PrecipitationAudio.Driver` polled
  `FindObjectsOfType<PrecipitationAmbientBlender>` once per second for a week: a 30 ms freeze at exactly 1 Hz on every
  ported map, found 2026-10-14 by frame-diffing the user's recordings (one duplicated frame every 60) and by
  ModProfiler's spike list (`Driver.Update 30 ms` at 1 Hz on both machines). Scan once per raid and cache; a
  measurement recipe lives in `docs/progress/equator-rain-accents.md` ("1 Hz hitch"). Check `uibridge_status`'s
  `max` frame time and ModProfiler's `max_ms_in_frame` before accepting a client build on the host.
- Vanilla Scripts scenes carry a `TechnicalManagers` root (Factory: `ScopeMaskRenderer`, `WindowBreakerManager`,
  `WaterRenderer`, `lightManager`, `OnRenderObjectManager`, `ImpostorsRenderer`, `GrassTrampler`); Arena's scenes have
  none and the extracts hold none of them. The plugin provides `OnRenderObjectManager` (WeatherRig), the CullingManager
  fallback (EnvironmentRepairs) and, since 2026-10-16, `ScopeMaskRenderer` (`ScopeMaskRepairs`): without it the global
  `_ScopeMask` texture is never written and the aiming depth-of-field blurs the image inside every magnifying optic
  (ELCAN 4x on AirPit soft, the same scope on Factory sharp). A screen/aiming effect that works on vanilla but not on
  a ported map: check that root's list first, with `uibridge_inspect gameObject path=TechnicalManagers` on a vanilla raid.
- Spawn gates: EFT bots path only on the NavMesh (area mask -1, no NavMeshAgent) and never see colliders, so a gate part
  re-activated at runtime is a wall for players and air for bots (SAIN presses against it, the vanilla mover snaps through).
  `SpawnGates` seals a held gate with carving `NavMeshObstacle` boxes; carving on the ported `NavMeshData` (added through
  `NavMesh.AddNavMeshData`) works and un-carves within 0.5 s (GPU-2, 2026-10-17). Equator's booths double as the map's
  extraction boxes (4 x 4 m at (2, 46) and (5, -44)): a tester standing inside one extracts after 8 s. Door (3) of the
  south booth is disconnected on the map's own NavMesh (Arena's fence behind it); probes across it are Partial by design.
  Since 2026-10-17 the descriptor says which opened barriers a hold may close (`start_gates`; AirPit: BUS_DOORS only) and
  which stay closed until the release in every mode (`held_gates`; AirPit's `PASSPORT_CONTROL_DOORS` + `_EMISSIVE`, the
  original north team box: closed at raid start, carved from the NavMesh install on, so bots cannot path into the
  pocket behind them in a plain raid; whether a player can vault the 1.35 m turnstiles is open). Both are subsets of
  `opened_barriers` (that is how the bake leaves them out), and carving off opens the held gates too; sidecar and C#
  descriptor change in one commit.
- Meta XR Audio is process-global: `MetaXRAcousticSettings.Instance.AcousticModel` and the one shoebox room
  (`MetaXRAudioRoomAcousticProperties`, two native calls per frame while enabled) belong to the whole process, not to a
  scene, and the plugin's managed model value does not vouch for the native side after an audio reset. `RoomShotAcoustics`
  saves the shipped model on first use, restores model and the plugin's default room (8 x 3 x 5 m, applied like the plugin's
  own `CheckSceneHasRoom`) on switch-off, outdoors and in `NativeSpatialAudio.Release`, and re-sends the model after
  `AudioSettings.OnAudioConfigurationChanged`. Measuring reverb on the muted host: `Audio diagnostics/ReverbPulse` with a
  Gunshots bus at `ReverbPulseVolume` 0.8 and the rain/ambience keys muted, otherwise the rain bed sits at the tail level
  (recipe in `docs/progress/shot-acoustics.md`).
