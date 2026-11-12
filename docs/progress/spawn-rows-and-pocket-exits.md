# Spawn rows and pocket exits - 2026-11-12

More Bot spawn rows on all six maps, the extraction points moved into the team start areas, a tool that measures both, and Chop Shop's
selection marker moved once more. Branch `feature/autoservice`, merged as `48b316d`, built, deployed to GPU-2 and host-checked the same
night. Nothing is pushed. Facts below come from the commits, the repo files and the GPU-2 log of 2026-11-12 (session file
`LogOutput-SPT-GPU-2-2026-11-12_23-32-40.log`); "Not checked" lists what no log or screenshot covers.

## The request

The user, 2026-11-12:

> "können wir noch die anzahl der spawnpunkte auf den maps erhöhen? war zumindest eine empfehlung der ArenaMode sitzung."
>
> "den marker von chop shop sollten wir nochmal 100px nach links verschieben."
>
> "in ArenaMode sind die extracts ja deaktiviert, entsprechend haben die grünen flares keine funktion, ich fände es aber ganz praktisch wenn
> die bei team-modi die spawnbereiche markieren würde. anders formuliert, flares (und damit extract) näher an die spawn-booth setzen"

Three parts: more spawn points, the Chop Shop marker, and the exits (with their green flares) next to the team spawn areas.

## What ArenaMode needs from the rows

Read-only analysis of the ArenaMode checkout `D:/SPT41/Development/ArenaMode-ui-whitelist` (HEAD 99260275, 2026-11-12 16:05) and its notes
under `D:/SPT41/Development/ArenaMode/artifacts/chop-shop-20261112/notes/`. ArenaMode is never edited from here. Files read:
`Bots/BurstSpawnPointSelector.cs`, `Bots/BurstSpawnService.cs`, `Bots/ArenaTeamBoxRuntime.cs`, `Respawn/ArenaSpawnSelector.cs`,
`Pickups/ArenaPickupMapLayout.cs`, `Pickups/ArenaPickupRuntime.cs`, `Pickups/ArenaObjectivePickupSpacing.cs`,
`Shared/ArenaTeamBoxCatalog.cs`, `Server/Services/ArenaLocationMutator.cs`, `Client/Session/ArenaExtractionIsolation.cs`,
`Client/Hud/ArenaExtractionList.cs`, and the test harnesses under `tools/`.

- **The 25 m rule.** In Deathmatch, Gun Game, solo Zombies and Tactical, ArenaMode places bots only on Bot rows that are farther than 25 m (3D)
  from the local player; rows at 25 to 70 m come first, rows beyond 70 m after them, rows on equal coordinates count once. A row layout therefore
  has to leave at least one row 25 m or more away from every spot where a player can stand.
- **Tactical's five rows.** `SpawnSquad(5, ...)` needs five distinct-coordinate rows at 25 m or more from the leader, who can be anywhere at a
  round transition. Before this change Equator's coverage of that rule was 82.2 % of its cells (its ArenaMode harness pins the count 4 from (0, 0.2, 2) by design).
  Tactical is refused on Chop Shop, Equator and Iceberg by a rule inside ArenaMode that was argued from the old row counts; lifting it is
  ArenaMode's decision and needs its own native proof.
- **Pickups.** Six kinds need six usable positions at once: pairwise at least 3 m apart, outside both team boxes plus 2.5 m, at least 6 m from
  every Domination point, at most 16 candidates considered. More rows widen that pool.
- **What does not change.** Team Deathmatch and Domination use ArenaMode's own 20 owned points per raid; generic Bot rows are ignored there
  except the donor row, which is the path target. Appending rows with new ids, the same shape (`Bot`, `BotPmc`, `BotZone_Main`, radius 25) and
  no `arena-<map>-tdm-v1-` prefix breaks no runtime check; the six donor rows must stay byte-exact (list in the Traps entry of AGENTS.md).
  Nothing in ArenaMode reads exit positions, names or counts: `ArenaExtractionIsolation` matches by object, `ArenaExtractionList` only hides
  the panel, and `Formation.BoothExits` is data without a reader. Bots never trigger exits.
- **Player rows.** ArenaMode's first spawn uses a random Player row of the profile's side and infiltration; Deathmatch and Gun Game respawns use
  every Player row, 40 m or more from the corpse first. Each map has 4 Player rows; they were not changed (option 1 below).

## Method

1. **Survey.** For each map, Arena's own marker sources were listed first: LastHero free-for-all markers (Chop Shop, Air Pit), the AI core points
   of the TeamFight_AI / LastHero_AI scenes, the AI born points, and Iceberg's CheckPoint stage markers. Mesh fill points (2 m lattice on the
   built NavMesh, 1.5 m clearance) were allowed only where no native marker passes.
2. **Tool.** `tools/validate/spawn_coverage.py` measures, on the port's built NavMesh bundle and the tracked sidecars, how many cells of the play
   component have at least 5 Bot rows farther than 25 m, picks additional rows greedily and lists exit-box options per pocket (section "The tool").
3. **Proposals and decisions.** One proposal per map was cross-checked by an independent pass against the sources and the tool; the lead's
   decisions fixed the final rows and exit positions (existing rows byte-identical, new rows appended). The survey files lived in the session
   scratchpad and are not tracked.
4. **Implementation.** Seven worktree streams (tool, Equator, Bay5, Air Pit, Bowl, Iceberg, Chop Shop), each one implementer plus two independent
   reviewers (a data lens and a rule/test lens) and one fix round; merged as `c866bba`, `893d9ea`, `639a7ec`, `5d50477`, `20cc314`, `ec26795`,
   `48b316d`. Stream heads: tool `c36ce6b`, Equator `bec8a5e`, Bay5 `d73bcc4`, Air Pit `c5724d5`, Bowl `08ea7e2`, Iceberg `6867aad`, Chop Shop
   `dd1a8a2`. The marker commit is `6271766`, the tool's first cut `e4b1d04`.

## The data

### Bot rows (appended; the original rows are byte-identical)

Ids are `arena_<stem>-bot-<suffix>`, shape `categories ["Bot", "BotPmc"]`, radius 25, in this order at the end of each map's `bots.json`.
Coverage = share of walkable cells outside the pockets that have at least 5 Bot rows farther than 25 m (3D), before to after; "min" is the
lowest count over those cells.

| Map | Rows | Appended suffixes | Coverage | Min |
|---|---|---|---|---|
| Chop Shop | 7 -> 16 | lasthero-04, lasthero-13, fill-01, core-06, lasthero-03, lasthero-10, fill-02, fill-03, fill-04 | 23.5 % -> 98.3 % (cells with no such row 5.6 % -> 0) | 0 -> 3 |
| Equator | 10 -> 16 | core-12, born-21, born-26, core-11, core-10, core-02 | 82.2 % -> 100 % | 3 -> 6 |
| Iceberg | 9 -> 17 | stage-bear-46, stage-bear-49, fill-01, stage-bear-36, stage-bear-33, stage-usec-35, stage-usec-32, stage-usec-36 | 99.3 % -> 100 % (no row east of x 29 before) | 4 -> 10 |
| Bay5 | 10 -> 16 | core-08, core-07, core-03, core-06, fill-01, fill-02 | 100 % -> 100 % (variety, not coverage) | 6 -> 9 |
| Air Pit | 14 -> 20 | center-lane, north-hall-centre, south-apron-west, south-apron-east, west-lane, west-hall | 100 % -> 100 % | 10 -> 15 |
| Bowl | 17 -> 21 | born-16, born-27, fill-01, fill-02 | 100 % -> 100 % | 8 -> 12 |

39 rows in all. Final numbers of `spawn_coverage.py --check` on the merged state: Chop Shop 1,399 cells, min 3, median 8, 98.3 %; Equator 2,427
cells, min 6, median 11, 100 %; Bay5 3,981 cells, min 9, median 12; Bowl 4,500 cells, min 12, median 17; Iceberg 4,496 cells, min 10, median 14
(Air Pit's numbers are in the table above). Player rows are unchanged (4 per map).

Sources per map:

- **Chop Shop:** LastHero markers `Usec house (4)`, `(13)`, `(3)`, `(10)` (moved 0.5 to 1.0 m onto open floor), AI core point 6 (moved 1.0 m;
  the 2026-11-11 rule had skipped it, 9.0 m from the east big gate), four mesh fill points.
- **Equator:** AICore ids 12, 11, 10 and 2 on their points; AI born points 21 and 26 moved 1.0 and 1.5 m.
- **Iceberg:** CheckPoint stage markers Bear house (46), (49), (36), (33) and Usec house (35), (32), (36), moved 0 to 1.8 m; one mesh fill point in
  the open east yard (60.0, 1.95, -86.0) instead of stage marker Bear house (26), which stands on the balcony ladder's concrete part.
- **Bay5:** AICore ids 8, 7, 3 and 6 of `Arena_Bay5_LastHero_AI` on their points; two mesh fill points.
- **Air Pit:** AICore ids 2, 12, 3 and 9 (group ids of LastHero_AI) on their points, LastHero marker `Usec-Bear-Savage house (1)` moved 1.0 m, AI born
  point 22 moved 2.0 m.
- **Bowl:** AI born points 16 and 27 (moved 1.41 and 1.69 m), two mesh fill points.

### Exits (name, box 4 x 3 x 4 m, rotation 0, 8 s, chance 100, type Individual unchanged; only the position changed)

| Exit | Old | New | Where |
|---|---|---|---|
| Chop Shop East Gate | (-349.0, -3.77, 488.5) | (-340.5, -3.6, 485.0) | east pocket, in the lane just inside the big gate |
| Chop Shop West Gate | (-393.0, -3.79, 501.0) | (-406.5, -3.7, 478.5) | west pocket (roofed warehouse), south end by the south-east shutter |
| Bay5 North Loading Bay | (96.0, -2.9252, 61.0) | (137.0, -2.84, 51.0) | USEC warehouse pocket |
| Bay5 South Container Lane | (99.029, -3.8574, -2.8513) | (50.0, -3.78, -2.5) | BEAR depot pocket |
| Air Pit North Gate | (-11.0, 0.1, 62.0) | (-12.5, 0.1, 77.0) | north pocket behind the held passport gates, on the compass rose |
| Air Pit South Gate | (-3.0, 0.1, -58.0) | (-9.5, 0.1, -62.0) | south pocket mouth, hall side of the bus doors (box edge 0.41 m outside the bound; the listed exception) |
| Bowl BTR Gate | (51.0, 0.1, -31.0) | (49.3, 0.1, -11.1) | east pocket |
| Bowl Army Gate | (-51.0, 0.1, -49.0) | (-57.4, 0.2, -60.8) | west pocket, corner by the shooting-range targets |
| Iceberg East Gate | (72.0, 1.95, -94.0) | (90.88, 1.67, -86.34) | Bear pocket, landing at the top of the stairs (stage marker Bear house (9) inside the box) |
| Iceberg West Gate | (-30.5, 2.92, -85.0) | (-38.79, 3.2, -85.6) | Usec pocket, terrace in front of the entrance (Usec house (11) inside the box) |
| Equator North / South Gate | (2.0, 0.1, 46.0) / (5.0, 0.1, -44.0) | unchanged | already inside the booths (the precedent, [equator-booth-spawns.md](equator-booth-spawns.md)) |

Chop Shop's selection marker is displayed at IconX 743 / IconY 428 (the measured 407/471 stays in `markers.json`; 870/428 from 2026-11-11 until
today). The toggle `ARENA: CHOP SHOP` on EFT's region map now sits right below the end of the word CUSTOMS and above INTERCHANGE. The toggle
rectangles of CUSTOMS and Chop Shop share a 10 x 7 px corner of empty padding, so the label test in `SelectionMapPositionTests` measures the ink
(insets measured on GPU-2: left 5, right 28, top 8, bottom 7 px), and the released-pin rule is "rows at least 55 units apart or dots 360 units
apart" instead of 120 units of straight distance. See [selection-map.md](selection-map.md).

## The rules as they now stand

- **R1 (rows).** Every Bot and Player row lies on the play component of the built NavMesh; ids are unique; ArenaMode's donor row of the map is
  present with its exact floats; no id carries `-tdm-v1-`; appended rows keep the border clearance (1.35 m, less 0.02 m for the rounding of
  2-decimal positions). Existing rows are never moved (an older row may stand closer to the border than that, Bay5's 0.02 m).
- **R3 (exits).** One exit per team pocket; the box centre lies inside the pocket's initial spawn-bound footprint (`<stem>.modes.json`); every Player
  row is at least 2.5 m from the box edge; there is floor under the centre; no solid stands in the box between floor+0.3 and floor+2.5.
  Listed exceptions: **Air Pit South Gate** stands at the pocket mouth (its nearest box edge is at most 1 m outside the footprint: 0.41 m; the
  centre is 2.41 m outside, because no in-pocket box keeps the two south Player rows 2.5 m away and boxes nearer the pocket hold solids or lie
  off the mesh); **Equator South Gate** touches the elevator frames on the booth's back wall (it did not move). R3 replaces the earlier rule,
  "exits beyond the gates, 10 m from every Player row / Bot row / pocket marker" ([exfils-stage2.md](exfils-stage2.md)).
- **R4 (accepted consequences).** In a plain raid a player spawns a few metres from an 8 s exit; a held team can reach its exit; Air Pit North Gate
  cannot be reached in a plain raid until a mode plugin releases the passport gates; bots never trigger exits. ArenaMode disables extraction, so
  the flares only mark the team start areas there.
- **R5 (appended Chop Shop rows).** The nine appended Chop Shop rows keep at least 2.5 m to a gate's swept footprint and 1.5 m to the spawn bounds;
  the 10 m / 5 m conventions of 2026-11-11 stay pinned for the seven original rows. The yard is 58 m long and ArenaMode only uses rows 25 m away
  from the player, so the old margins left cells with no usable row.

## The tool

`tools/validate/spawn_coverage.py` (1,697 lines, no Unity, no host, deterministic) and its tests `tools/tests/test_spawn_coverage.py` (synthetic
meshes) and `tools/tests/test_spawn_layout_rules.py` (real data for the six maps plus a table test: 7 tests).

```
# selection: coverage before/after, extra rows, exit options per pocket
PYTHONUTF8=1 python tools/validate/spawn_coverage.py --stem autoservice --package work/extract/autoservice-native-v2 --out x.json --png x.png
# check mode: verifies the COMMITTED layout, one line per violation, exit status 1 on any
PYTHONUTF8=1 python tools/validate/spawn_coverage.py --stem autoservice --package work/extract/autoservice-native-v2 --check --appended-since arena_autoservice-bot-lasthero-04
python -m unittest tools.tests.test_spawn_layout_rules
```

Stems: equator, bay5, airpit, bowl, iceberg, autoservice. The NavMesh defaults to `work/build/<stem>/arena_<stem>_navmesh.bundle`
(`work/build/full` for Equator); `--maps-dir` reads the sidecars from another folder. The tests read `ARENAMAPPORT_WORK_DIR` (default `work/`) and
`ARENAMAPPORT_MAPS_DIR`, skip a map without bundle or package (a skip is not evidence), and pin the first appended row per map in their table.
`--check` takes `--mouth-exit NAME` and `--solid-exception NAME` for the two listed exceptions.

Known limits:

- Standing room (`spawn_placement_probe`, an upper bound) runs in selection mode, not in `--check`; the host's `spawn placement:` line stays the
  authority.
- The check enforces the floor under the box centre only. "Corners on the mesh" is information (Equator's South Gate has 2 of 4).
- The `solids_in_box` list is truncated to four entries; the Equator pin that all of them are elevator frames covers those four.
- The plain-raid flood works on NavMesh polygons and removes every polygon within 0.3 m of a held gate, so it can only say "unreachable" too early.
- Gate footprints are per-collider XZ hulls (rotation rows: a disc around the pivot); without `--package` gates are known only by 3 m discs at
  door rows with `at`, which for Chop Shop means no gates.
- The mesh is the port's built NavMesh, not Arena's, so clearance figures differ slightly from the sidecar comments.
- Two helpers of the existing tools ignore tile-portal edges (`navmesh_oracle.NavMesh.border_edges()`, `clearance()`); the tool has its own.
  `port_colliders.load_door_rows` drops the open pose, so the tool reads `doors.json` itself.

## Gates (merged state, main checkout)

- .NET: 3,300 of 3,300 (`dotnet test ... -c Release`).
- Python: `Ran 3262 tests in 1079.001s`, `OK (skipped=68)` with `PYTHONUTF8=1`.
- `tools.tests.test_spawn_layout_rules`: 7 tests OK.
- `validate_map_descriptors.py` OK (14 descriptors); `validate_distribution.py`: `DISTRIBUTION_OK 6 maps 18 bundles 6 sidecars encrypted`.
- After the records and the comment fixes of the review round (sidecar `_comment` fields, descriptor and `ExfilSignals` comments, test docstrings; no data, no
  generated byte): .NET 3,300 of 3,300 again; Python `Ran 3262 tests in 1066.724s`, `OK (skipped=68)`.

## Deploy

- DynamicMaps: the four released packs (Bay5, Air Pit, Bowl, Iceberg) had only their two exit StaticMarkers patched in
  `work/dynamicmaps/release/ArenaMapPort_<Map>/ArenaMapPort_<Map>.jsonc` (backup of the old files and fresh exports under the ignored
  `work/dynamicmaps/exits-20261112/`). The patched Air Pit, Bowl and Iceberg files are byte-identical to a fresh export; the fresh `Ground.svg` of
  Bay5 and Air Pit differ from the released ones (older `navmesh.json` snapshots, Bay5 also bounds 74 against 75), so the released SVGs were kept.
  Equator's pack is unchanged (its exits did not move); Chop Shop has no pack.
- `tools/deploy-to-spt.ps1 -SptRoot 'D:\SPT41'`: dry run, then `-Confirm`: 12 files overwritten (client DLL, four `ArenaMapPort_<Map>.jsonc`,
  server DLL, six `base.json`), 384 identical. `gpu-run deploy-wait --server --game`: `34 file(s) identical on the host after 2.3 s · server
  stopped · game restarted · build 16/16 identical`. Build of the host run: merge `48b316d`, both DLLs.

## Host evidence (GPU-2, 2026-11-12)

**Pre-check on the previous build** (evening; the NavMesh is unchanged by this work): `uibridge_teleport dryRun=true` from each map's donor row:
all 39 appended rows and 9 of the 10 new exit centres `accepted` with `path complete`. Air Pit North Gate: `REJECTED path_partial ... ends at
(-12.5, 0.1, 65.7), 11.3 m short` (the held passport gates, by design), with `reachable=false` and `would teleport to (-12.5, 0.0, 77.0) · snapped
0.1 m · checks navmesh, zone, room`.

**New build `48b316d`, 23:32 to 23:50.** Selection screen (PMC flow, 1920x1080): `toggle "ARENA: CHOP SHOP" click 986,457 [849,441 275x32]`,
`toggle "CUSTOMS" ... [696,415 163x33]`.

- **Chop Shop**, plain PMC raid without bots (`warnings 0; errors 0`):
  `23:34:11.450 [RaidFix] spawn placement: disabled 2 SPAWN_BOUND barrier collider(s); all spawn points already free`;
  `23:34:22.839 [BotSpawn] BotZone.Init 'BotZone_Main': markers=16 onNavMesh=16 points=16 ... patrolWays=1`;
  `23:34:37.285 [Exfil] authored exit 'Chop Shop East Gate': box (4.00, 3.00, 4.00) at floor (-340.5, -3.7, 485.0) (NavMesh-sampled within 1 m, 0.1 m from authored)`;
  `[Exfil] authored exit 'Chop Shop West Gate': ... at floor (-406.5, -3.7, 478.5) (..., 0.0 m from authored)`;
  `[Exfil] exit 'Chop Shop East Gate' after init: status=RegularMode eligibleEntryPoints=[arenaautoservice] chance=100 time=8s`;
  `[Exfil] smoke signal lit at 'Chop Shop East Gate' ((-340.5, -3.4, 485.0))`, `... 'Chop Shop West Gate' ((-406.5, -3.4, 478.5))`.
  The west flare was seen from the west Player row 17.3 m away (linecast clear), the east flare from the Usec row 18.0 m away (clear); from the
  yard the hall's interior walls hide both. Teleported into the east box: `inRaid=false` after 7.7 s (extraction works in a plain raid).
- **Air Pit:** `23:36:03.948 BotZone.Init ... markers=20 onNavMesh=20`; `spawn placement: ... all spawn points already free`;
  `23:36:18.439 [Exfil] authored exit 'Air Pit North Gate': ... at floor (-12.5, 0.0, 77.0) (..., 0.1 m from authored)`; `'Air Pit South Gate': ...
  (-9.5, 0.1, -62.0) (..., 0.0 m from authored)`; both `status=RegularMode`. The north flare stands on the compass rose in the pocket (seen from
  inside the pocket; from the plain-raid start the closed passport booths hide it); the south flare is seen from the south pocket through the bus.
- **Bowl:** `23:37:29.708 BotZone.Init ... markers=21 onNavMesh=21`; placement free; `23:37:44.761 [Exfil] authored exit 'Bowl BTR Gate': ...
  (49.3, 0.1, -11.1) (..., 0.0 m from authored)`, `'Bowl Army Gate': ... (-57.4, 0.1, -60.8) (..., 0.1 m from authored)`. East flare 7.8 m from the
  'All' Player row, west flare about 6 m from the west 'All' row.
- **Iceberg:** `23:38:55.190 spawn placement: disabled 12 SPAWN_BOUND barrier collider(s); all spawn points already free`;
  `23:38:57.471 [RaidFix] zone 'BotZone_Main' had no patrol ways — built 2 way(s) with 17 point(s) and 173 sub point(s) from 17 Bot spawn marker(s) in 1
  NavMesh-connected component(s) (0 isolated single point(s), 0 marker(s) off the NavMesh)` (the line before it is stamped 23:38:57.354);
  `23:38:58.021 BotZone.Init ... markers=17 onNavMesh=17 points=17 ... patrolWays=2`;
  `23:39:13.144 [Exfil] authored exit 'Iceberg East Gate': ... at floor (90.9, 1.9, -86.3) (NavMesh-sampled within 1 m, 0.2 m from authored)` (the
  authored y 1.67 is the built mesh's interpolated value, the live floor is 1.9), `'Iceberg West Gate': ... (-38.8, 3.2, -85.6) (..., 0.0 m from
  authored)`. East flare on the landing at the top of the stairs, 5.4 m from the 'All' Player row; west flare on the terrace 7.4 m from the west
  'All' row.
- **Bay5:** `23:40:31.939 BotZone.Init ... markers=16 onNavMesh=16`; placement free; `23:40:46.999 [Exfil] authored exit 'Bay5 North Loading Bay': ...
  (137.0, -2.9, 51.0) (..., 0.0 m from authored)`, `'Bay5 South Container Lane': ... (50.0, -3.8, -2.5) (..., 0.0 m from authored)`. Both flares
  seen inside their halls.
- **Equator:** `23:41:57.167 BotZone.Init ... markers=16 onNavMesh=16`; placement free; exits as before, (2.0, 0.1, 46.0) and (5.0, 0.1, -44.0),
  0.0 m from authored.
- **Chop Shop with bots** (medium, godmode, plus `uibridge_hook spawnBot assault x5`): six scavs alive on `PatrolAssault` / `StandBy`, one at
  (-361.8, -3.8, 478.2), 2.8 m from the appended row fill-02 (-360, 476); `23:43:10.478 BotZone.Init ... markers=16 onNavMesh=16`.
- **Hideout** opened in 6.2 s.
- **Warnings:** `uibridge_log file=true source=maschine-ArenaMapPort level=warning` returned 0 lines for the whole session file (20,908 lines at
  that time).

Screenshots (ignored folder `analysis/screenshots/pocket-exits-20261112/`, 13 PNGs): `selection-chopshop-743`, `<map>-<side>-flare` for Chop Shop,
Air Pit, Bowl, Iceberg and Bay5, `arenamode-tdm-bowl-in-exit-box`, `arenamode-tdm-iceberg-spawn-in-exit-box`.

### The two ArenaMode Team Deathmatch runs

Purpose: does an exit box under a player break an Arena session? Both through gpu-run, 4v4.

- **Bowl** (`raid --mode teamdeathmatch --map bowl --participants 8`; `warnings 0; errors 0`): the local BEAR started at (-54.6, 0.0, -63.6) in the
  west pocket. Teleported to the exit box centre (-57.4, 0.1, -60.8) and stood there 12 s through "OPENING SPAWN GATES": still `Raid: arena_bowl ·
  alive`, no extraction, no exit list or timer on the HUD (ArenaMode hides it). Two deaths by `uibridge_hook kill` (`[maschine-ArenaMode] Arena
  death #2 — respawn after optional killcam.`): respawn at (-58.0, 0.0, -66.3), 5.5 m from the box centre. No Warning or Error of any source
  between log #5426 and #6179.
- **Iceberg** (`--map iceberg --map-from-menu`; the ArenaMode menu on GPU-2 offers ICEBERG and CHOP SHOP): the local BEAR's FIRST spawn was
  (89.3, 1.9, -86.4), the stage marker Bear house (9), inside the east exit box and 1.6 m from the flare (the view is tinted green by the flare's
  point light). It stood there from the start through the countdown (raid clock 18:34:12 to 18:36:07 game time, more than 12 s real time): the
  match started ("First to 25 | 05:00 left"), no extraction. Four deaths (one while standing on the box centre): respawns at (97.9, 0.7, -83.7)
  each time, the raid continued (`deaths 4`). Warnings in that raid: ten `[Warning Unity Log] BoxCollider does not support negative scale or size
  ... "ArenaMapPort Native Acoustics arena_iceb…"` at 23:48:02 (Iceberg's ten mirrored acoustic trigger volumes, known) and `The referenced
  script on this Behaviour (Game Object 'extractor_smoke') is missing!` lines at 23:48:34 (a weapon asset); none from either mod.

## Not checked

- A respawn that lands ON the in-box marker (Iceberg Bear house (9) / Usec house (11)): the selector chose (97.9, 0.7, -83.7) four times. ArenaMode's
  analysis names one unverified window (the dead body is still MainPlayer for some frames while the new body appears); its own session should run it.
  The first spawn inside the box through the whole countdown did not extract.
- Team Deathmatch on Chop Shop, Air Pit and Bay5 with the new exits; any Domination, Deathmatch, Zombies or Tactical match with the new rows
  (pickup placement 6 of 6 on Chop Shop Domination is expected, not seen); whether ArenaMode lifts its Tactical refusals.
- The flares seen from across each map, at night, in rain; the 30 m flare hiss inside the pockets (sound) was not listened to.
- The cost of Iceberg's patrol-way build with 17 rows was not measured separately (the two neighbouring log lines are 117 ms apart).
- Bots spawning at each of the 39 new rows (one scav was seen next to one new row).
- Chop Shop's west exit box: 79 of 81 floor samples on the built mesh, 1.99 m border clearance at its centre in Arena's mesh; walked only by
  teleport. Its east exit y -3.60 is the built mesh's interpolated value, about 0.2 m above the collider floor (-3.80); `ExfilRepairs` places the box on
  the sampled floor (the log shows -3.7), so it is cosmetic unless a flare looks to float.
- Iceberg's east exit stands on the upper part of a ramp (collider floor 1.89 m at the centre, 1.20 m at the east edge); if a host walk shows the
  east edge not triggering, nudge it 1 to 2 m west in the sidecar and the descriptor together. The west exit's box edge is 0.61 m from the swept
  GATES_B footprint.
- Bay5: `fill-02` (64.0, 58.0) is 10.12 m from the Player row `player-north-west`, a thin margin against the 10 m test floor (the proposal's 14.3 m
  was the distance to the nearest Bot row).
- Iceberg's original third row `core-04` (19.69, 3.15, -47.93) sits 0.24 m above the built mesh with 0.80 m clearance (Arena's mesh: 0 m and 2.76 m); an old row, not
  changed, not host-checked.

## Hand-off to the ArenaMode session (never edited from here)

Runtime: nothing breaks (analysis of ArenaMode-ui-whitelist HEAD 99260275 and the two Team Deathmatch runs above). To re-pin or re-decide there:

- `tools/ChopShopChecks/Verify.ps1` (the exact set of 7 Bot ids; now 16) and its README ("4 Player and 7 Bot rows").
- `tools/MapModeAdmissionChecks/Program.cs:207` (Equator `CountUsable` from (0, .2, 2) == 4, by design; Equator now has 16 rows).
- `tools/ChopShopTeamBoxChecks` and `IcebergTeamBoxChecks`, "a bot row inside a team box": none of the appended rows is inside a box (Chop Shop rows
  are at least 1.5 m outside the spawn bounds; ArenaMode's own boxes are inset further).
- `tools/ArenaPickupChecks/AllMapCases.cs` (pickup pool 6 to 16 per map: more rows now; 16 is ArenaMode's own cap).
- Docs and comments that say "seven Bot rows" or "no booth exits": AREA-RULES.md, MAP-MODE-COMPATIBILITY.md, FEATURES.md, CHANGELOG.md, HANDOFF.md,
  CHOP-SHOP-INTEGRATION-20261112.md, ICEBERG-INTEGRATION-20261031.md, the comments in `ArenaTeamBoxCatalog.cs`, `ArenaParticipantPolicy.cs`,
  `ArenaAreaCatalog.cs`, the picker hint in `ArenaAreaSelectionState.cs`; `Formation.BoothExits` is data without a reader.
- Decisions that are ArenaMode's: lifting the Tactical refusal on Chop Shop, Equator and Iceberg (needs its native proof), Chop Shop's participant
  default of 6, and a native Team Deathmatch run with a respawn onto an in-box marker.

## Open options for the user

1. **More Player rows** for Deathmatch / Gun Game respawn variety (sides `All`, spread over the map, e.g. Arena's LastHero markers). They would
   widen the respawn pool, but a first spawn could then land mid-map (a solo Zombies start in a dead patch measured `points 0 preferred + 0
   distant`). Not done; Player rows stay 4 per map.
2. **Equator South Gate** could move to (0.5, 0.06, -41.65), which passes the full check with no solid exception and clears the elevator frames. Not
   done: Equator's exits were not part of the request, and ArenaMode lists the two Equator names.
3. **Exit names** such as "Bay5 North Loading Bay" or "Bowl BTR Gate" no longer describe the place. They were kept because the name is the locale
   key and ArenaMode lists Equator's; renaming is a locale and descriptor change.

## 2026-11-13 00:40 - the committed state on the host

The records commit `160523d` changes comments and docs only, but both DLLs carry the commit, so they were rebuilt from it and deployed
(2 files overwritten, 394 identical; `24 file(s) identical on the host after 65.3 s · server stopped · game restarted · build 16/16 identical`).
Control raid, Chop Shop, no bots (`warnings 0; errors 0`): `00:40:58.534 [RaidFix] spawn placement: disabled 2 SPAWN_BOUND barrier collider(s); all
spawn points already free`, `00:41:11.020 [BotSpawn] BotZone.Init 'BotZone_Main': markers=16 onNavMesh=16 points=16`, `00:41:25.502 [Exfil] authored exit
'Chop Shop East Gate': ... at floor (-340.5, -3.7, 485.0) (NavMesh-sampled within 1 m, 0.1 m from authored)`, `'Chop Shop West Gate': ... (-406.5, -3.7, 478.5)
(..., 0.0 m from authored)`. `validate_distribution.py`: `DISTRIBUTION_OK 6 maps 18 bundles 6 sidecars encrypted`. GPU-2 runs `160523d`; nothing is pushed.
