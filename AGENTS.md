# ArenaMapPort — working notes for agents

ArenaMapPort imports the maps of Escape from Tarkov: Arena (0.5.4.0, read from the user's own Arena
install) into SPT 4.1.5 (EFT 0.16.9.5, Unity 2022.3.43f1) as separate locations: a Python extractor,
a Unity 2022.3.43f1 batch importer that rebuilds scenes, materials, colliders and a fresh NavMesh into
asset bundles, a BepInEx client plugin that repairs everything a ported scene lacks at raid time, and an
SPT server mod that registers the locations. Fourteen maps are built and staged; **Equator** is the
reference map and the only one played extensively. The maps exist to be played through the user's
ArenaMode mod (arena deathmatch with respawns); see the decisions below.

Read this first, then [HANDOFF.md](HANDOFF.md) (dated working state, what is verified, what is next),
then [docs/progress/INDEX.md](docs/progress/INDEX.md) to find the progress log for the area you touch.
[docs/SPEC.md](docs/SPEC.md) is the original spike specification; its "Decisions 2026-09-20" section at
the end overrides the spike framing. `README.md` is the user-facing summary.

## Decisions that shape every task (user, 2026-09-20)

- **ArenaMode is the target.** Ordinary SPT raid playability (loot, quests, scav raids, insurance,
  transits) is not a goal. A plain SPT raid started from the menu is only the test vehicle.
- **No loot on any map, ever.** The server keeps the empty loot shape. Do not author or extract loot.
- **No scav raids.** `DisabledForScav = true` stays.
- **Bundles are distributed as built** (`dist/`), like other SPT map mods. Bundles still never go into git.
- **Equator first**, then the other maps one at a time through the Equator recipe (bots/exits sidecars +
  host raid), then parity only where ArenaMode needs it.

## Hard rules — never do these

- Never write into `D:\SPT41` by hand. It is a Syncthing mirror of the GPU test host; the only write path is
  `pwsh -NoProfile -File tools/deploy-to-spt.ps1 -SptRoot 'D:\SPT41' -Confirm` (quote the path: an
  unquoted backslash in bash once created a stray folder). Without `-Confirm` the script is a dry run.
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
- Git: author `maschine34675 <163792852+maschine34675@users.noreply.github.com>`, commit only when the
  user's task implies it, and end commit messages with the agent's co-author trailer. Working tree
  should be clean after a delivered task.
- The user communicates in German; code, comments and docs are English.

## Layout

| Path | What |
|---|---|
| `ArenaMapPort.Client/` | BepInEx/HarmonyX plugin (netstandard2.1). One file per repair; every patch is gated on `ArenaMapRaid.IsLoaded()` / `IsOwnedScene()` so vanilla maps are untouched. `HarmonyPatches.cs` installs everything in order and binds config. |
| `ArenaMapPort.Server/` | SPT server mod (net10.0): registers each map from `db/<key>/base.json` (Equator: `db/base.json`), locale, brain tables, bot caps, APBS shim. |
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

## Build, test, deploy

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
pwsh tools/build_all.ps1 -MapKey arena_equator -MapDescriptor tools/maps/equator.json -PackageDir D:/ArenaMapPort/work/extract/equator -OutputDir D:/ArenaMapPort/work/build/full -BakeNavMesh $true -Verify $true -SkipGameRefsSync
# then a client Release build WITHOUT DllOnly stages the new bundles into dist/, and:
pwsh -NoProfile -File tools/deploy-to-spt.ps1 -SptRoot 'D:\SPT41' -Confirm
```

Equator's package predates the `-FullMap` convention (its manifest lists the scenes in a different order
than the descriptor), so Equator is built WITHOUT `-FullMap`. Every other map uses `-FullMap` with
`work/build/<stem>/` and `tools/maps/<stem>.json` (exact commands in `docs/progress/remaining-maps.md`).
`-FullMap` also regenerates that map's base.json and preset from the sidecars; to regenerate only base.json
after a sidecar change, add `-SkipUnityBuild` (reuses the bundles). For Equator, base.json is regenerated
with `python tools/server/make_base_json.py` (see its header for the arguments).

After a deploy, Syncthing delivers to the host within ~20 s. A new client DLL needs an EFT restart; a new
server DLL or base.json needs a server restart (the host server is restarted by closing its terminal
window and double-clicking the desktop shortcut; Syncthing only replaces the server DLL once the process
is closed). Both installs carry the user's own `maschine-ArenaMapDebug.dll`: a main-menu button
"EQUATOR DEBUG" that starts an Equator raid without the menu flow, and a disconnect that skips the
confirmations and the vanilla 0-HP penalty for a skipped heal screen.

## Host testing (GPU host, via MCP)

Two MCP servers drive the test host: `uibridge` (sees inside the running EFT) and `windows-gpu`
(clicks, keys, screenshots). Recipe that has worked for every host test so far:

1. `uibridge_status`: is another session using the host? Is the loaded ArenaMapPort DLL identical to the
   VM build ("VM build = host disk = loaded")? Is the EFT window focused (the first click into an unfocused
   window only focuses it)?
2. Restart EFT when the client DLL changed: click points from `uibridge_snapshot contains=EXIT` / `YES`,
   `uibridge_wait unreachableFor=5`, launcher START GAME (1396,323; the "Wipe Profile" checkbox sits left of
   it, never click it), then `uibridge_wait screen=MainMenu afterRestart=true`.
3. Start the raid with the EQUATOR DEBUG button (`uibridge_snapshot contains=Equator`), then
   `uibridge_wait raidStarted=true`.
4. Observe: `uibridge_log` (BepInEx log, filter by `[ArenaMapPort]` tags), `uibridge_player`,
   `uibridge_bots` (positions, brain layers), `uibridge_teleport` (has a NavMesh reachability check —
   `reachable=false` for islands; `dryRun=true` to probe connectivity), `uibridge_look`, screenshots via
   `windows-gpu Screenshot`. Letter keys do not reach EFT; use `uibridge_command` for game commands.
5. Leave: `uibridge_command Escape`, click `DISCONNECT` from `uibridge_snapshot`, `uibridge_wait inRaid=false`.

Log evidence goes into the milestone's `docs/progress/*.md` with timestamps; a change is "verified" only
after such a host raid. The in-game `F10` hotkey (SceneInspector) dumps the object under the crosshair to
the log; the user uses it to point at things.

## Conventions

- One repair per file, a class doc comment that says what vanilla does, what the ported scene lacks, what
  the repair does and which host test proved it. Log lines start with `[ArenaMapPort][<Area>]`.
- Per-map data belongs in `ArenaMapDescriptor` (Shared) and the matching `tools/maps` sidecar, never as a
  literal in a repair. Object names repeat inside Arena scenes: address scene objects by hierarchy path.
- New config keys: `Plugin.Instance.Config.Bind("<Section>", "<Key>", default, "<what/why, takes effect when>")`.
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
  runtime must be added there too, or it stays a wall for bots. Bake volume is y −8..12 (upper rings are
  decoration). Probe connectivity in-game with `uibridge_teleport dryRun=true` (reachability = same NavMesh
  component); offline, `work/extract/<map>/scenes/*.json` + `meshes/*.mesh.bin` give every collider's world
  triangles (see docs/progress/stage1-combat.md for the script pattern).
- Syncthing replaces the client DLL while EFT runs; EFT keeps the old code until restarted. Trust
  `uibridge_status`'s MVID comparison, not file hashes. The same holds for rebuilt bundles: a loaded map
  bundle stays cached in the EFT process, so a new NavMesh/scenes bundle needs an EFT restart before the next
  raid (the `[ArenaMapPort][NavMesh] added navmesh ... N vertices` line tells which bake is live).
- `uibridge_wait logMatches`: only lines logged after the wait started count (use `sinceSeq`).
- Bash tool heredocs over ~180 lines fail; write scripts with the Write tool and run them.
- Fresh 0-HP profile deaths on the host were the skipped heal screen, not the map.
