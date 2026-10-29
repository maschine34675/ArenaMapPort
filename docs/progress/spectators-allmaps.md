# Original crowds on Equator, Bay5 and Bowl — 2026-10-27

User accepted the complete [AirPit crowd](airpit-spectator.md) and requested the same treatment
on the other three maps. This extends the existing decorative crowd; it adds no gameplay AI,
colliders, sounds, match-event reactions or settings. Equator day/night share the source scene.

## Source census and placement

Current Arena BuildSettings, not historical level indices, identify the design scenes:

| Map | Design level | Fans | LOD0 renderer parts | Null meshes recovered |
| --- | --- | ---: | ---: | ---: |
| AirPit | 40 | 99 | 495 | 166 |
| Equator | 53 | 99 | 495 | 165 |
| Bay5 | 81 | 88 | 440 | 148 |
| Bowl | 43 | 99 | 495 | 166 |

Independent scans of all map-associated BuildSettings entries found one real `SCAV_SPECTATORS`
group per map. All maps reuse the same 15 appearances and original shared animation controller
(`Parapet=false`, generic Animator, root motion disabled). The accepted idle/cheer clips and XYZ
Euler conversion remain unchanged. Missing meshes use the previously verified AirPit recovery
rules. The absent legacy lower-face cloth uses the documented current Arena Shroud half-mask:
27 instances on AirPit, 26 on Equator, 23 on Bay5 and 27 on Bowl.

Bay5 differs: its source crowd is active and translated by
`(104.146446228, 6.381745338, -16.224702835)`. The other containers are disabled scene roots with
identity transforms. Schema 3 therefore requires the source scene and container transform;
individual fan placements remain local to that container. Non-root containers and scaled roots
fail extraction until reviewed. The runtime applies container placement before building skeletons
and world-space visibility bounds. Wrong-map and old-schema packages are rejected.

The imported Bay5 root still contains static accessories and 1,056 colliders. After the replacement
has loaded successfully, `ArenaSpectators` disables only the old `SCAV_SPECTATORS` root in that
owned design scene. If construction fails, the original group stays as it was. Equator, Bowl and
AirPit originals remain disabled. Existing gameplay geometry and navigation are unchanged.

## Shared runtime and distribution

One `ArenaSpectators` manager per loaded map owns 39 meshes, 33 source materials, 103 textures,
two sampled animations and the individual skeletons. Names include the map key and repeat creation
is guarded in the design scene. Deterministic independent phases, 30/15 Hz visible pose updates,
off-screen time progression, bounds and scene-owned disposal are retained from AirPit.

Each map has a validated owner-extracted package under `work/extract/spectator-<stem>` and is staged
under `spectators/arena_<stem>`. The full file graph, map/scene identity, exact texture set and staged
bytes are checked. Art remains ignored; encrypted bundles and the Arena installation gate are
unchanged. Release classification now includes the exact spectator manifest and texture filename
shapes in each map's part, requires each manifest, and rejects stray/parked-map files. No release
archive or publication is part of this change.

## Offline evidence

- Focused validation covers map/scene mismatch, stale schema, translated container, invalid TRS,
  reordered BuildSettings, source count, skeleton/mesh/clip references and every real package.
- Independent raw-source and NumPy audits passed on all four packages: exact world placements,
  all source renderer slots and native mesh channels, 15 appearances and 435 deformed poses per
  map within the existing bounds. All 44,376 sampled animation values per map match native curves
  with independently composed rotation matrices (maximum error approximately 1.33e-15).
- Negative controls detect an omitted Bay5 container (104.146 m error) and wrong Euler order
  (matrix error 0.284). They modify only in-memory audit fixtures.
- Release client build, full distribution validation and release packaging fixtures pass. The
  classification smoke test covers all 609 staged files with none unclassified.

Ignored evidence: `work/spectators-allmaps/GATES.md`, per-map source census/audit reports,
`package_audit.py`, `*/independent-audit/`, and `audit-negative-controls.json`.

## GPU-2 acceptance

The sanctioned deploy script copied 609 distribution files to `D:\SPT41`; gpu-run verified all
497 client DLL/crowd files on GPU-2. The unchanged server DLL also matched before the server start.
Fresh game instance `d19997e6` loaded client MVID `183ff357-4e67-46f2-b5c1-0d1edcb6e23c`, matching
the built/deployed DLL (SHA-256 `A327A640BE94A1C77848FB2E575CA8331E579A16B31E844B310BACB6648DF08C`).
The final status still reported 16/16 matching plugin identities.

Five consecutive plain PMC raids, bots None:

| Raid | Fans / renderers | Shared source materials | Steady FPS / p95 | Maximum frame in that 10 s window |
| --- | ---: | ---: | ---: | ---: |
| Equator day | 99 / 495 | 33 | 120 / 8.33 ms | 9.36 ms |
| Equator night | 99 / 495 | 33 | 120 / 8.33 ms | 10.32 ms |
| Bay5 | 88 / 440 | 33 | 120 / 8.33 ms | 9.11 ms |
| Bowl | 99 / 495 | 33 | 120 / 8.33 ms | 9.81 ms |
| AirPit regression | 99 / 495 | 33 | 120 / 8.33 ms | 9.60 ms |

Equator used the real 12:00/00:00 menu selections; the other maps used a stopped 12:00 test clock
and clear weather. Each raid had exactly one manager; idle/cheer phases differed between figures
and transition counters advanced. Bay5 reported one original active group disabled; the inspector
confirmed its root inactive. Equator day's existing SpecularCap repair additionally creates 41
owned accessory material clones (74 total including the 33 shared originals); all of those are
removed on exit as well. No material-policy override was added for spectators.

Each of the five returns to the main menu left zero crowd managers and zero `[spectator]`
materials. The profile was healed after every disconnect. No ArenaMapPort warning/error in the
whole process. Engine messages were separate: Equator's refrigerator has duplicate LODGroup
warnings, and one `MenuScreen` inactive-coroutine error occurred during a menu return; all returns
completed. Raw evidence is retained in `engine-warnings.json`, `final-mod-warnings.json` and the
per-raid recipe outputs. This is not a claim that the entire engine log is empty.

The 120 FPS figures are capped steady observations, not isolated crowd overhead. Initial sampling
windows contained loading hitches of roughly 0.48–2.65 s; steady windows exclude them. No claim
that this work removes raid-start hitches. No bots or multiplayer performance was exercised.

Screenshots: `equator-day.png`, `equator-night.png`, `bay5.png`, `bowl-east-stands.png`,
`bowl-east-stands-2.png`, `airpit.png` in the evidence directory. Bowl's crowd is much farther away
and often occluded by authored canopies/cover. A further independent comparison confirmed every
parent/TRS in its accepted main/design/building scenes and all 356 stadium meshes match the current
Arena source; no placement correction is justified. The eastern stands are visible from the
playfield near `(25.3, 0, -19.6)`, looking towards `(86.19, 19.2, -25.55)`. This pass used no gallery
teleport or navigation changes on Bowl.

36 spectator fixtures and 182 release packaging fixtures pass; all 11 acceptance gates are met.
GPU-2 is in the main menu. Changes remain on `feature/airpit-spectator`; no release/upload/push.

## Load cost: crowd package prepared off the main thread (2026-10-29, 0.4.0 candidate fix)

Measured on the host before this change: the crowd build blocked the main thread for about 4.7 s per raid on every
map, inside the loading phase. About 2.0-2.3 s of it was `SHA256.Create()` over the 146,689,024 bytes of the 123
shared PNGs (every map reads all 123 files; the managed SHA-256 of EFT's Mono) and 1.6-3.0 s was Newtonsoft parsing
the 20.7 MB `spectator.json.gz` graph (`ReadToEnd` into one 41 MB string, then `DeserializeObject`). The rest is PNG
decode (`ImageConversion.LoadImage`, `Apply`) and the Unity object creation, which has to stay on the main thread.

What changed (host verification is still to do: nothing here ran inside EFT, the Unity-free parts are unit-tested and
the numbers below come from Unity's own Mono, see "Benchmark"):

- **CNG hashing.** `SpectatorTexturePackage` hashes through `CryptoProviders.Select(null)` (Windows CNG through the
  `bcrypt.dll` P/Invoke the bundle decryption already uses, the managed provider only as the silent fallback, no
  Warning of its own: `BundleKeyResolver` raises the one CNG Warning per process) and compares the lowercase hex of the
  digest exactly as before. The size check and the digest comparison stay fail closed; `Read` is thread safe (immutable
  manifest, interlocked counters). A new three-argument constructor takes the provider (the tests run both).
- **Worker thread.** `ArenaSpectators.Prepared` runs one `LongRunning` task per raid: open the graph, gunzip, parse with
  `JsonSerializer.CreateDefault().Deserialize` straight from a `JsonTextReader` (the same default settings as
  `JsonConvert`, no 41 MB string), validate it, open the texture manifest, and start a `SpectatorTexturePrefetch` that
  reads and verifies the texture files in the order the builder uses them. The worker is plain managed code: no Unity
  API, no logging, and its body never throws (a failure travels in `Loaded.Error`, so the task never faults).
  The prefetch keeps at most 48 MiB of verified files waiting (the budget plus the file that crossed it): the 146 MB of
  a crowd are never all in memory, which also matters for speed (see "Benchmark").
- **Early start.** `ArenaSpectators.Install()` subscribes `sceneLoaded` (start when a ported map's Scripts scene loads,
  a raid then spends tens of seconds loading) and `sceneUnloaded` of that scene (cancel and drop an unused package).
  `Install()` must be called once from `Plugin.Awake`, next to `SpectatorAudio.Install()`. Without that call
  `ArenaSpectators.Prewarm()` (first statement of `SpawnInfiltrationFixPostfix.Postfix` after the map gate, same gate as
  `Create`: design scene loaded and no crowd root yet) and, at the latest, `Create` start the worker, which still
  overlaps hashing and decoding but not the parse with the loading phase. The second CreateFromScene pass finds the
  crowd root and starts nothing.
- **Join and fallback.** `Create` joins with a 30 s bounded `Task.Wait`; each texture is taken from the prefetch with a
  30 s bound. Any worker failure (exception, timeout, order mismatch) falls back to the synchronous path of the
  previous releases (`LoadPackage` on the main thread, `SpectatorTexturePackage.Read` per file) and produces exactly one
  Warning for the raid: `[Spectator] the <map> crowd could not be prepared in the background (<Type: message>); it was
  loaded on the main thread instead` when the crowd then loads, or the usual `could not be restored (...)` Warning with
  `; the background load had reported <Type: message>` appended when it does not. A damaged texture therefore still
  ends as one Warning and the raid continues.
- **Debug summary.** The existing line keeps its prefix and gains the Stopwatch figures:

```
[Spectator] <map_key>: <n> original fans, <r> renderers; shared <m> meshes, <k> materials; idle/cheer staggered; load total=<ms> ms (wait=<ms>, hash=<ms>, parse=<ms>, textures=<ms>, actors=<ms>, audio=<ms>; hash and parse on the <worker|main> thread, crypto <CNG|managed>)
```

  `total` is the whole `Create` on the main thread; `wait` is the time the main thread was blocked on the worker (join
  plus all texture takes); `hash` and `parse` are the worker's own time (the file read is not in `hash`); `textures` is
  the decode loop (PNG decode, Apply, including its waits); `actors` is everything after it (materials, meshes, actor
  hierarchies); `audio` is `SpectatorAudio.Create`. `main thread` appears after a fallback or when no worker ran.

### Benchmark (no EFT on this VM)

The scratch program `bench/Bench.cs` (under the session scratchpad, not tracked) runs the production
`SpectatorTexturePackage`/`SpectatorTexturePrefetch` and the Crypto providers against `dist/` twice from one source: as
a net10.0 console, and as a net471 exe compiled with the SDK's Roslyn against Unity's `4.7.1-api` references and run
with the Unity 2022.3.43f1 editor's `mono.exe` (Mono 6.13.0) with `MONO_PATH` set to `lib/mono/unityjit-win32`, the
profile EFT's player ships. There `SHA256.Create()` is `SHA256Managed`, as in the game. The page cache was warm.

| Mono 6.13 (EFT's profile) | Before | After |
| --- | ---: | ---: |
| SHA-256 of the 146,689,024 bytes, provider only | managed 1926 / 1969 ms (73 / 71 MiB/s) | CNG 142 / 126 ms (984 / 1106 MiB/s) |
| `SpectatorTexturePackage.Read` x 123 (size check + read + hash + compare) | managed 2154 ms (read 76, hash 2067) | CNG 210 ms (read 74, hash 131) |
| Prefetch pipeline, instant consumer (CNG) | - | consumer wall 230 ms, peak 5.7 MiB pending |
| Graph parse Bowl, three rounds | `ReadToEnd` + `DeserializeObject` 1344 / 1110 / 1046 ms | streamed 1047 / 1092 / 971 ms |
| Graph parse Bay5, three rounds | 1110 / 1084 / 1069 ms | 1015 / 991 / 1014 ms |

The managed figure reproduces the host's 2.0-2.3 s, so the hash was the Mono SHA-256 and nothing else. On .NET 10 (not
EFT's runtime) both providers run at 1.3-1.4 GiB/s and the parse takes 0.5-0.65 s: those numbers say nothing about the
game. Reading all 123 files into memory at once cost 597 ms on Mono against 76 ms for the read-and-drop loop (the
Boehm heap grows by 147 MB of live arrays), which is why the prefetch is bounded instead of preloading everything.

Expectation, not a measurement: both the hash and the parse leave the main thread, which then spends its time on PNG
decode, `Apply` and object creation only. The first host raid's Debug line shows the real figures; compare `total` with
the 4.7 s above. The parse stays 1-3 s of CPU on the worker (the streamed reader saves 5-25 % and the 41 MB string),
contended with the loading threads.

### Notes

- `SpectatorAudio.Create` still hashes its 13 WAV files (2,824,501 bytes) with `SHA256.Create()`, about 40 ms on Mono. The
  same change to the CNG provider is left to the owner of that file.
- Notice texts: the in-game reinstall advice no longer says "the ZIP" (0.4.0 ships five ZIP parts that go into the same
  SPT root). The English and German texts of `ServerUnknown`, `ServerAllFailed`, `ServerSomeMissing`, `ServerSomeFailed`
  and `InstallIncomplete` now say "release package (all its ZIP parts)" / "Release-Paket (alle seine ZIP-Teile)";
  `MapNoticeTextTests.ReinstallAdviceNamesTheReleasePackageWithAllItsZipPartsInBothLanguages` pins it.
- Unit tests: `SpectatorLoadTests` (both providers accept the exact bytes and reject a flipped bit, a grown and a short
  file; concurrent reads; in-order delivery inside the budget; a file larger than the budget; a damaged and a missing
  file surfacing from `Take` with the original cause after the files before it; order mismatch; dispose of a worker
  that waits for room; timeout; asking for more than scheduled; double start).
