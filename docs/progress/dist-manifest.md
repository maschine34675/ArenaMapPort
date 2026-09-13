# s3-dist — dist/ assembly from the full build (SPEC §G.S3) — progress log

Role: builder, task label `s3-dist`. Task: assemble `dist/` from the FULL build
(`work/build/full` + `work/build/preset`), verify against the client's actual staging/load logic,
rebuild client+server Release, run server tests, write this manifest, write
`tools/deploy-to-spt.ps1` and run it dry-run only.

**Result: done.** Found and fixed a real bug in the client's own dist-staging MSBuild target (a
recursive glob that could non-deterministically pull in stale `work/build/s1/*.bundle` files —
same filenames as the full build's). Fixed, rebuilt both projects Release, `dist/` now holds
exactly the 7 expected files, all traced byte-for-byte back to `work/build/full`/`work/build/preset`
(SHA256-verified below). All 22 server xunit tests pass. `tools/deploy-to-spt.ps1` written and run
in dry-run mode only, as instructed — no file under `D:/SPT41` was touched. No git commit made
(workspace rule: the lead commits).

---

## 1) Verified the exact paths the client's own loading code expects (read code, not assumed)

Read `ArenaMapPort.Client/BundleHost.cs`, `NavMeshInstaller.cs`, `ShaderRebind.cs` directly:

- **`BundleHost.OurBundles()`** (`BundleHost.cs:57-62`): `BundlesDir = <plugin dir>/bundles/`, maps
  the 3 served keys to `Path.Combine(BundlesDir, "arena_equator_preset.bundle")` /
  `"arena_equator_scenes.bundle"` / `"arena_equator_navmesh.bundle"` — exact on-disk filenames.
  `<plugin dir>` = `Path.GetDirectoryName(typeof(BundleHost).Assembly.Location)`, i.e. wherever the
  client DLL itself sits (`dist/BepInEx/plugins/ArenaMapPort/` once promoted).
- **`NavMeshInstaller.LoadOnce()`** (`NavMeshInstaller.cs:109-110`): independently derives the same
  `<plugin dir>/bundles/arena_equator_navmesh.bundle` path (not via `BundleHost` — its own
  `Path.Combine`), confirming the navmesh bundle must be at that exact path regardless of whether
  `BundleHost`'s served-key dictionary is ever consulted for it (§F.1's own comment: nothing in the
  vanilla load chain requests the navmesh bundle by served key).
- **`ShaderRebind.LoadSidecar()`** (`ShaderRebind.cs:56,266`): `PluginDir` (same
  `Assembly.Location`-derived dir, **not** the `bundles/` subfolder) + `"material-shaders.json"` —
  i.e. the sidecar sits next to the DLL, one level up from the bundles.

So the load-bearing expected set under `dist/BepInEx/plugins/ArenaMapPort/` is: the client DLL,
`material-shaders.json`, and `bundles/{arena_equator_preset,arena_equator_scenes,arena_equator_navmesh}.bundle`.
(§G.S3's own terse acceptance-criteria line only says "client DLL + 3 bundles" — it doesn't
enumerate `material-shaders.json` explicitly, but `ShaderRebind.cs` unconditionally needs it at
that exact path; treated as part of the correct expected set, consistent with WP4's own pre-existing
`ArenaMapPortStageWpOutputs` target already staging it.)

## 2) Real bug found: client's dist-staging target could stage stale S1 bundles

`ArenaMapPort.Client.csproj`'s `ArenaMapPortStageWpOutputs` target (pre-existing, from an earlier
WP4 pass) globbed `work\build\**\*.bundle` (excluding `_donor_*.bundle`) and copied every match into
`dist/.../bundles/`. Checked what that glob actually matches right now:

```
work/build/full/arena_equator_navmesh.bundle
work/build/full/arena_equator_scenes.bundle
work/build/preset/_donor_develop_preset.bundle   (excluded by name)
work/build/preset/arena_equator_preset.bundle
work/build/s1/arena_equator_navmesh.bundle        <- SAME FILENAME as work/build/full's
work/build/s1/arena_equator_scenes.bundle         <- SAME FILENAME as work/build/full's
```

`work/build/s1` (the small S1 slice build, `docs/progress/s1-*.md`) produces bundles with the
**identical filenames** as the full S2 build. Both would glob-match and both would `Copy` to the
same `dist/.../bundles/<name>.bundle` destination — MSBuild's glob enumeration order is
filesystem-dependent, not guaranteed alphabetically/path-sorted, so which one wins the last write
was non-deterministic. This is exactly the "never S1/stale ones" risk the task named. Confirmed
this was live: before this pass, `dist/BepInEx/plugins/ArenaMapPort/bundles/` held **only**
`arena_equator_preset.bundle` — `arena_equator_scenes.bundle`/`arena_equator_navmesh.bundle` were
entirely absent from `dist/`, meaning no prior build of this target had actually populated them at
all (the S2 full build post-dates the last time this MSBuild target ran).

**Fix** (`ArenaMapPort.Client/ArenaMapPort.Client.csproj`): replaced the recursive glob with three
explicit, named source paths — `work/build/full/arena_equator_scenes.bundle`,
`work/build/full/arena_equator_navmesh.bundle`, `work/build/preset/arena_equator_preset.bundle` —
plus an explicit `work/build/full/material-shaders.json`. `work/build/s1*`/`work/build/s1_diag` are
never considered, by construction, not by exclusion pattern. Added an MSBuild `<Error>` that fails
the build loudly if any of the 4 expected source files is missing, rather than silently staging a
partial set.

## 3) Client Release build

```
dotnet build ArenaMapPort.Client/ArenaMapPort.Client.csproj -c Release
```
Result: **0 Fehler, 0 Warnung(en)** (0 errors, 0 warnings). Build log confirms the fixed staging
target ran and staged all 3 bundles + sidecar from the correct source dirs:
```
[ArenaMapPort] deployed maschine-ArenaMapPort.Client.dll -> dist/BepInEx/plugins/ArenaMapPort
[ArenaMapPort] staged arena_equator_scenes.bundle  from work/build/full/work/build/preset -> dist/.../bundles/
[ArenaMapPort] staged arena_equator_navmesh.bundle from work/build/full/work/build/preset -> dist/.../bundles/
[ArenaMapPort] staged arena_equator_preset.bundle  from work/build/full/work/build/preset -> dist/.../bundles/
[ArenaMapPort] staged material-shaders.json from work/build/full -> dist/.../ArenaMapPort/
```
(dist/ was cleared before this run so the result reflects only this pass's staging, not leftovers.)

## 4) Server Release build + xunit tests

```
dotnet build ArenaMapPort.Server/ArenaMapPort.Server.csproj -c Release
```
Result: **0 Fehler, 0 Warnung(en)**. `DeployToDist` target staged the server DLL + `db/base.json`
into `dist/SPT_Runtime/user/mods/ArenaMapPort/`.

```
dotnet test ArenaMapPort.Server.Tests/ArenaMapPort.Server.Tests.csproj -c Release
```
Result: **22 passed, 0 failed, 0 skipped**, 432 ms.

**Deliberately ran the test with `-c Release`, not the default Debug** — per this workspace's
known `dotnet test`-deploys-the-server-DLL trap (`ArenaMapPort.Server.csproj`'s `DeployToDist`
target runs `AfterTargets="Build"` with no `Configuration` condition, unlike the client's own
Release-gated staging target). A default `dotnet test` would rebuild
`ArenaMapPort.Server.csproj` in **Debug** as a `ProjectReference` dependency and silently
re-deploy a Debug DLL into `dist/`, clobbering the Release one just built in step 4. Running the
test itself as `-c Release` keeps the Server project's rebuild (if any) on Release throughout, so
`dist/SPT_Runtime/.../maschine-ArenaMapPort.Server.dll` stays the Release build after the test run
— confirmed via the SHA256 table below being computed **after** the test run, not before.

## 5) `dist/` — full manifest (post both builds + tests, verified fresh)

`dist/` was `rm -rf`'d and recreated by these two builds alone (see §2) — every file below is this
pass's own output, not a leftover.

```
find dist -type f | sort
```
Result — **exactly 7 files, no strays, none missing**:

| File | Size (bytes) | SHA256 |
|---|---:|---|
| `dist/BepInEx/plugins/ArenaMapPort/maschine-ArenaMapPort.Client.dll` | 24,576 | `84db5e28a18c691d51723c411db3dddfafb15aeaa45403761c293b1b2ea8ffeb` |
| `dist/BepInEx/plugins/ArenaMapPort/material-shaders.json` | 43,237 | `0724b11c2e97b4b1710384d1498af2a2de061e21d1ab98bc1da711c142b1ad17` |
| `dist/BepInEx/plugins/ArenaMapPort/bundles/arena_equator_preset.bundle` | 6,891 | `f12cf0bfbff60e5ec299f211253656d87650d63fb8a3a899b957bf4ee06dfe36` |
| `dist/BepInEx/plugins/ArenaMapPort/bundles/arena_equator_scenes.bundle` | 306,614,329 | `1517f0ae2f9589a43815db0d952d22a8d3a8a11c9e762c786ec6b35ffd179b99` |
| `dist/BepInEx/plugins/ArenaMapPort/bundles/arena_equator_navmesh.bundle` | 81,562 | `fc83c8dd46b7613931bba603ed60f6da508a2b3624b5aabad95f72989bfc0455` |
| `dist/SPT_Runtime/user/mods/ArenaMapPort/maschine-ArenaMapPort.Server.dll` | 18,944 | `7f8086d6e3cd40b35c8a5ecba83479cbe6233e54ad9b4b3888acd7c63c089dcb` |
| `dist/SPT_Runtime/user/mods/ArenaMapPort/db/base.json` | 538 | `4d02932388e87a374b36a2eec1cff22cdf0ca00b8122901a06c321d4d0d45e14` |

**Traceability — every bundle hash matches its S2/WP3 source exactly** (`sha256sum` cross-check):
```
arena_equator_scenes.bundle : dist == work/build/full/arena_equator_scenes.bundle    (1517f0ae...)
arena_equator_navmesh.bundle: dist == work/build/full/arena_equator_navmesh.bundle   (fc83c8dd...)
arena_equator_preset.bundle : dist == work/build/preset/arena_equator_preset.bundle  (f12cf0bf...)
```
These are the same `work/build/full` bundles `docs/progress/s2-validate-r2.md` independently
verified clean (all 3 §B.WP6 validators pass strictly, MonoScript identity patched to
`Assembly-CSharp`, 6/6 preset rcids resolve) — `dist/` carries that exact, already-validated
content, not a re-derived or re-built copy.

**Expected-set note**: SPEC §G.S3's own terse checklist line says "client DLL + 3 bundles" for the
BepInEx side without naming `material-shaders.json` — as established in §1 above, the actual
client code (`ShaderRebind.cs`) requires it at that exact path, so its presence here is correct,
not a stray file.

## 6) `tools/deploy-to-spt.ps1` — written, run dry-run only (as instructed)

Parameters: `-SptRoot` (default `D:/SPT41`), `-Confirm` (switch; real copy only with this passed
explicitly). Default (no `-Confirm`) only builds and prints the copy plan — `Copy-Item` is never
called in that path. The script never calls `Remove-Item` at all, in either mode (the safest way to
satisfy "never deletes anything outside its own ArenaMapPort folders" — it deletes nothing,
anywhere), and asserts per-file, before any write, that every destination path resolves strictly
under `<SptRoot>/BepInEx/plugins/ArenaMapPort/` or `<SptRoot>/SPT_Runtime/user/mods/ArenaMapPort/`
via `Resolve-Path`/`GetFullPath` prefix checks, not string-concat trust.

Ran it **only** in default dry-run mode (`-Confirm` never passed, per task instruction):
```
pwsh tools/deploy-to-spt.ps1
```
Output:
```
==== [deploy-to-spt] Building copy plan (SptRoot = D:/SPT41) ====

-- BepInEx client plugin: D:\ArenaMapPort\dist\BepInEx\plugins\ArenaMapPort
   -> D:\SPT41\BepInEx\plugins\ArenaMapPort
   maschine-ArenaMapPort.Client.dll                      24 KB   [NEW]
   material-shaders.json                               42,2 KB   [NEW]
   arena_equator_navmesh.bundle                        79,7 KB   [NEW]
   arena_equator_preset.bundle                          6,7 KB   [NEW]
   arena_equator_scenes.bundle                     299428,1 KB   [NEW]

-- SPT server mod: D:\ArenaMapPort\dist\SPT_Runtime\user\mods\ArenaMapPort
   -> D:\SPT41\SPT_Runtime\user\mods\ArenaMapPort
   maschine-ArenaMapPort.Server.dll                    18,5 KB   [NEW]
   base.json                                            0,5 KB   [NEW]

[deploy-to-spt] plan: 7 file(s), 292.6 MB total -- 7 new, 0 to overwrite, 0 already identical

[deploy-to-spt] DRY RUN (default) -- no files were copied, nothing under D:/SPT41 was touched.
[deploy-to-spt] Re-run with -Confirm to actually copy the plan above.
```
All 7 rows report `[NEW]` because `D:/SPT41/BepInEx/plugins/ArenaMapPort` and
`D:/SPT41/SPT_Runtime/user/mods/ArenaMapPort` do not exist yet (confirmed directly, `ls` on both
paths fails with "No such file or directory") — this mod has never been promoted into the live
install before, so `[NEW]` for all 7 is the correct, expected result, not a bug. Confirmed
`D:/SPT41` was untouched by this run (script never reached its `-Confirm`-gated copy branch).

---

## Files touched

- `ArenaMapPort.Client/ArenaMapPort.Client.csproj` — `ArenaMapPortStageWpOutputs` target rewritten
  from a recursive `work\build\**\*.bundle` glob to 3 explicit named source files (SS2), plus an
  `<Error>` guard for a missing expected source file.
- `tools/deploy-to-spt.ps1` — new (SS6).
- `dist/**` — cleared and regenerated by this pass's own Release builds (gitignored, not a repo
  file, listed here for completeness per the task).
- `docs/progress/dist-manifest.md` — this file, new.

No file under `D:/SPT41` or `X:/` was written. No `git add`/`git commit` was run — per workspace
rule, the lead commits.

## Handoff

- **`dist/` is complete, correct, and traceable**: exactly the 7 files the client's own loading
  code (`BundleHost.cs`/`NavMeshInstaller.cs`/`ShaderRebind.cs`) expects, at the exact paths they
  expect, every bundle SHA256-identical to the already-validated `work/build/full`/`work/build/preset`
  sources — no S1/stale content anywhere in it.
- **A real, generalizable bug is fixed**: the client csproj's dist-staging target can no longer
  non-deterministically pick up `work/build/s1*` bundles alongside (or instead of) the full build's
  — this would have silently recurred on every future rebuild until now, since nothing before this
  pass had actually exercised the staging target against a workspace where both `work/build/s1` and
  `work/build/full` coexist (they do, right now, in this exact repo).
  - **Flagged, not yet fixed**: the same recursive-glob shape does not exist on the server side
    (`ArenaMapPort.Server.csproj`'s `DeployToDist` target only ever globs its own project-local
    `db/**/*.json`, no cross-slice ambiguity possible there) — no equivalent risk found on that
    side, so nothing to fix.
- **Both projects build clean Release, 22/22 server tests pass.**
- **`tools/deploy-to-spt.ps1` exists and was verified dry-run only** — it has never been run with
  `-Confirm`, and `D:/SPT41/BepInEx/plugins/ArenaMapPort` / `D:/SPT41/SPT_Runtime/user/mods/ArenaMapPort`
  do not exist yet. Promoting `dist/` into the live install (§G.S4) remains a separate, explicit,
  user-approved step — running `pwsh tools/deploy-to-spt.ps1 -Confirm` after reviewing the dry-run
  plan above is how the user would do it.
