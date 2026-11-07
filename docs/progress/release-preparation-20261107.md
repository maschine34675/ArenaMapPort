# Release 0.5.0 preparation - 2026-11-07

The first release with Iceberg (the fifth map), the crowd reactions (phase A) and the roar layer; the user's decisions of 2026-11-07: "0.5.0, Iceberg kommt
rein, keys sind gesichert", one whole ZIP on Pixeldrain as for 0.3.0 and 0.4.0. This record holds the gates, the host run, the cut and the scan of the
candidate; the publication (upload, GitHub release, tag, the merge of `feature/iceberg` into `main`) waits for the user's go and is appended below when done.

## Commits

- `8a407cf` Iceberg emission defaults 4.8 / 1.9 (user) and the roar layer approved by the user's ear ("habe ich mir angehoert, klingt gut").
- `f6fb5cb` 0.5.0 release texts: version 0.5.0 in both csproj files, `Plugin.PluginVersion`, `ModMetadata`; CHANGELOG 0.5.0; `docs/releases/v0.5.0.md`; README and
  FORGE for five maps; the README.txt texts of `make_release_zips.py` with their pins; `docs/dist-manifest.md` 0.5.0 section. Written by a staged workflow (three
  writers on disjoint files, three review lenses, twelve confirmed findings fixed: the DLL-version test's stale-control literal, the demo-listening wording, the
  murmur-swell sentence, the Iceberg exit wording, server-time wording, the placeholder checklist, two over-claims about what was seen in game).
- `a18ecd7` merge of `claude/trusting-bohr-d2a526` (2026-11-05, a separate session): the Arena installation check tolerates a missing root `Uninstall.exe`
  (a player's antivirus had quarantined the launcher's uninstall stub; `ARENA_INCOMPLETE` on 0.4.0). The branch's CHANGELOG "Unreleased" entry became the 0.5.0
  "Fixed" section, the release note got the same bullet; README and AGENTS merged without conflict; 18 new .NET tests (3,184 to 3,202). **This is the build
  commit of the cut.**

## Gates (this VM)

Before the merge (workflow gates agent on the text commit's tree, 17:30-18:00): client Release build WITHOUT DllOnly (0 warnings; the 15 bundles byte-identical,
every `keys/<map>.json` gained the Arena 0.6.2.0.48473 wrap beside the 0.6.1.0.48206 one, so the shipped key files now carry two wraps; `work/release-keys/*.key`
were appended at 17:58 and the user's off-VM backup should be refreshed), server build, `dotnet test` 3,184, Python `Ran 2886 tests ... OK (skipped=67)`,
`test_unity_log_scan.ps1` 8 cases, `validate_map_descriptors.py` OK, `DISTRIBUTION_OK 5 maps 15 bundles 5 sidecars encrypted`, `validate_bundle_names.py` OK 15
bundles, `validate_port.py --map iceberg --distribution` and `--map bowl` PORT_VALIDATED, `make_release_zips.py --plan` 359 files (core 230, equator 24, bay5 30,
airpit 22, bowl 26, iceberg 27). After the merge (18:05-18:15, on `a18ecd7`): client DllOnly and server builds 0 warnings, `dotnet test` **3,202 passed**,
`DLL_VERSIONS_OK 0.5.0 client/server source, project and staged binaries`, `test_dll_versions` + `test_release_zips` 196 OK, `DISTRIBUTION_OK` again.
`verify-keys` for all five maps against the VM's Arena 0.6.2.0.48473 passed in the release audit of the same day; the hosts' 0.6.1 install loaded every map in the
host run below.

## Host run (GPU-2, 2026-11-07, 18:25-18:38)

Full deploy of `dist/` (359 files, 7 overwritten: both DLLs and the five key files), Syncthing identical after 69 s, `gpu-run start` (the SPT server restarted,
`Mod: ArenaMapPort Version: 0.5.0 ... geladen` at 18:26:02; EFT instance 49b4d370 from 18:26:27, 16/16 plugins identical). Client startup: `[Arena] Escape from
Tarkov: Arena 0.6.1.0.48206 at C:\Battlestate Games\Escape from Tarkov Arena (registry; 15,351 files, 76.1 GB verified)` and `Version 0.5.0 loaded. Maps: Arena:
Equator, Arena: Bay 5, Arena: Air Pit, Arena: Bowl, Arena: Iceberg.` Plain PMC raids with bots None, one per map, each left through the menu:

| Map | Time | Load (`diff`) | Crowd `load total=` | NavMesh | Notes |
|---|---|---|---|---|---|
| Equator | 18:27 | location 5.2 s (1.6), game 32.8 s (14.7) | 2,169 ms (roars=6) | 10,395 / 4,567 | scenes bundle 577 MB plaintext, 551 AMPB chunks |
| Bay5 | 18:29 | 5.3 s (1.3), 25.8 s (8.1) | 2,151 ms | 22,130 / 10,220 | |
| Air Pit | 18:30 | 5.2 s (1.4), 25.9 s (8.2) | 2,070 ms | 11,811 / 5,433 | |
| Bowl | 18:31 | 5.1 s (1.4), 26.2 s (8.8) | 2,082 ms | 12,704 / 5,890 | |
| Iceberg | 18:32 | 5.9 s (1.6), 25.2 s (7.0) | none (no crowd) | 18,703 / 8,599 | see below |

Iceberg, first raid (bots None, `Spawns/SpawnGateHold = true`, `SpawnGateHoldSeconds = 20`, both `persist=false`): `[EmissionCap] map=arena_iceberg cap=4.80
floor=1.90 (owned scenes loaded): 37 emissive material(s), 5 capped (largest original 67.79 ... -> 4.80), 15 lifted (smallest original 0.29 ...), 20 written, 0 left
alone, 0 gone; 22693 renderer(s) scanned in 23 ms`; `[SpawnGates] navmesh installed: 11 carver(s) over 11 movement collider(s) of 11 gate part(s)`; the probe found
every probed gate sealed (5 PathPartial); the player spawned in the WEST pocket at (-39.8, 3.2, -91.2). Release at 18:33:03-06, 20.0 s after the raid timer: `[GateSounds]
released 11 part(s): 6 sound row(s), 4 playing, 2 out of reach or without a free source` (`arena_san_gates_blue` from the three west sliding doors, `arena_equator_old_slide_doors`
from the motor door, each -0.9 dB, reach 30 m; the two east `arena_san_gates_red` rows beyond 30 m), `[SpawnGates] released (developer test hold, 20.0 s after the
raid timer started): ... 11 carver(s) removed, drove 11 part(s) to the open pose in 2.5 s (scale 0.50), 5 park carver(s) in place`, `drive frames: 298 frame(s) in 2.5 s,
mean 8.4 ms, max 20.4 ms`, the A, B and CheckPoint gates parked at the open pose. Then `uibridge_teleport` to `exfil:pmc:Iceberg West Gate` (-30.5, 2.8, -85.0): the
raid ended within 10 s, screen `ExitStatus`: "RAID ENDED - EARLY EXTRACTION - Run Through - 00:00:31" (the exit works; the "Run Through" is EFT's own
too-early verdict). `[EmissionCap] ... released 37 material(s) (raid ended), restored 20 to their originals`.

Iceberg, second raid (bots Medium, 18:34-18:37): `uibridge_bots` listed three bots within 45 m (two `pmcUSEC` on SAIN layers, one `assault` Scav on
`AssaultHaveEnemy`); a 20 s observation: the Scav walked at least 19.3 m (native mover 95 %), one PMC 2 m, one held position in SAIN's combat layer; one bot
killed another (`last death: ... by HereticJ (ADAR 2-15, Head, 9.4 m)`). 119.9 fps, mean 8.3 ms, max 12.7 ms, RAM 13.7 GB. Left through the menu.

Hideout opened from the main menu at 18:38 (6.2 s) and left. **0 ArenaMapPort warnings in the whole process** (the log file of 20,093 lines read at 18:39).
Not run today: a longer bot raid, the east pocket as the player's side, Bay5's gate sounds in range, the olive tree and the rocks by eye.

## Cut and scan (18:34-18:37)

`python tools/release/make_release_zips.py --single --out work/releases/0.5.0/pixeldrain-a18ecd7 --extract-check <empty scratch dir>` (log
`work/releases/0.5.0-make-a18ecd7.log`): `writing single ZIP (full): 359 files, 5,396,847,882 raw bytes`, **`maschine-ArenaMapPort-0.5.0.zip 5,370,649,086 B
70d0777ec60d8b248d75a62511715851ff1c363b29b5a60e1b8f2957363b3f05`** (5.00 GiB), `maschine-ArenaMapPort-0.5.0.sha256` written, `extract-check OK: 359 files identical to
dist, 4 generated entries ignored`, `RELEASE_ZIPS_BUILT`; `--verify-only work/releases/0.5.0/pixeldrain-a18ecd7`: `RELEASE_ZIPS_VERIFIED 0.5.0 (single ZIP)`.
`scan_virustotal.py --version 0.5.0` (log `work/releases/0.5.0-vt-a18ecd7.log`, record `work/releases/0.5.0/virustotal.json`): client DLL
`b0d86f10c6adbe21df8f3f07c76f2cc465cf28e3416e3b616b06c89d01ffd656` 0 malicious, 0 suspicious of 75 engines (2026-11-07T17:34:47Z); server DLL
`9a41aab5b5e9c1be65514b1aa0091af075e0df7922372118178f88ae46730536` 0 of 75 (17:36:30Z); `VIRUSTOTAL_SCAN_CLEAN 0.5.0`. The release note carries the size,
the checksum, the build commit and the scan lines; only the four Pixeldrain links and the date are placeholders.

## Open before publication (user)

Upload (`upload_pixeldrain.py --dry-run` first; the single file is 5.0 GiB against 3.6 GiB for 0.4.0, the key check reports a per-file limit if any), the GitHub release
with the notes and the `.sha256` asset, the fast-forward merge of `feature/iceberg` into `main` and the tag `v0.5.0` on the publication commit (the commit that carries the
filled-in links), the push of `main` and the branch; afterwards the records say "published", the 0.2.3 and 0.3.0 releases are deleted when the user says so, and the
Forge entry is the user's. The key store changed today (the 0.6.2 wraps): refresh the off-VM backup of `work/release-keys`.

## Published - 2026-11-07 (user: "go")

`upload_pixeldrain.py --version 0.5.0 --release-dir work/releases/0.5.0/pixeldrain-a18ecd7 --timeout 300` (dry run first; log `work/releases/0.5.0-upload-a18ecd7.log`,
record `pixeldrain.json` beside the ZIP): list https://pixeldrain.com/l/VHbxKwMB, ZIP https://pixeldrain.com/u/shfhQhBF (direct https://pixeldrain.com/api/file/shfhQhBF?download), checksum https://pixeldrain.com/u/mJEWfZS3.
The links went into `docs/releases/v0.5.0.md` (its checklist comment removed), HANDOFF and AGENTS say published, the publication commit was fast-forwarded onto
`main`, tagged `v0.5.0`, and `main`, `feature/iceberg` and the tag were pushed; the GitHub release "ArenaMapPort 0.5.0" carries the note as its body and
`maschine-ArenaMapPort-0.5.0.sha256` as its only asset. The ZIP's manifest pins the build commit `a18ecd7`; the tag sits on the publication commit, as for 0.4.0.
