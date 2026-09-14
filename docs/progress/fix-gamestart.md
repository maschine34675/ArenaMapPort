# fix-gamestart — fix the raid-start hang (entry-point NRE)

Task: apply the reviewed findings for the 2026-09-14 in-game test (map loads, pre-raid countdown
expires, game hangs — `NullReferenceException` in `SPT.SinglePlayer.Patches.RaidFix
.EmptyInfilFixPatch.PatchPrefix`). Four independent write-ups of the same root cause were supplied
(`entrypoint` F1-entrypoint-nre, `raidend` F1, `evidence` F1-blocker-emptyinfil-nre, plus a dormant
`inraid-world` weathercurve finding) — all four confirmed the identical chain; one fix addresses all
three F1 write-ups.

## Finding F1 (blocker) — entry-point NRE hangs raid start

**Root cause, re-verified directly against the decompiled sources before touching anything:**

- `UnityProject/Assets/ArenaMapPort/Editor/Import/AuthorScriptsScene.cs:132` authored all 3
  `SpawnPointMarker`s with `spawnPoint.Infiltration = null;`.
- `SpawnPoint.Infiltration` is a plain mutable public field
  (`D:/SPT41/Development/_decompiled/EFT.Game.Spawning/SpawnPoint.cs:23`); `ISpawnPoint.Infiltration`
  (`:81`) just forwards to it. `SpawnPointMarker.SpawnPoint` (`SpawnPointMarker.cs:58`) returns the
  same instance every call — confirmed nothing downstream ever copies it.
- `BaseLocalGame<T>.Run` collects every marker via `SpawnPointsCollection.CreateFromScene(...,
  location.SpawnPointParams)` (`EFT/BaseLocalGame.cs:526`). Our `db/base.json` ships no
  `SpawnPointParams` (intentional — `analysis/spt-location-contract.md` §7 step 2), so
  `CreateFromScene`'s `if (parameters != null)` branch (`SpawnPointsCollection.cs:157`) — the ONLY
  other code that ever writes `Infiltration` (via `SpawnPointMarker.FixParams`, reached from
  `FixSpawns`) — never runs. The scene-authored `null` survives untouched.
- `spawnSystem.SelectSpawnPoint(...)` later returns one of those exact instances and `_entryPoint =
  spawnPoint.Infiltration` (`BaseLocalGame.cs:577`, duplicated at `:179`) is therefore `null`.
- Vanilla EFT tolerates this: `vmethod_6` skips the exfil-init block when `_entryPoint == null`
  (`BaseLocalGame.cs:790`, `if (_entryPoint != null)`). SPT's own Harmony prefix on that same method,
  `SPT.SinglePlayer.Patches.RaidFix.EmptyInfilFixPatch.PatchPrefix` (scratchpad decompile
  `.../spt-singleplayer/SPT.SinglePlayer.Patches.RaidFix/EmptyInfilFixPatch.cs:20-49`), does **not**:
  when `____entryPoint` is null/whitespace it scans `Resources.FindObjectsOfTypeAll<SpawnPointMarker>()`
  for any marker with non-empty `Infiltration` (`:27-35`) and unconditionally dereferences the nearest
  match at line 48 (`spawnPointMarker.SpawnPoint.Infiltration`) — no null check on `spawnPointMarker`
  itself. All 3 of our markers have `Infiltration=null`, so the candidate list is empty,
  `spawnPointMarker` stays `null`, and line 48 throws — the exact stack in
  `D:\SPT41vanilla\BepInEx\LogOutput.log:1353-1361`
  (`EmptyInfilFixPatch.PatchPrefix <- vmethod_6 <- BaseLocalGame+CG_Class1638.MoveNext`), the last line
  the session ever logs (server log shows a clean `LocationLifecycleService` start and bot-generate
  response, confirming the hang is purely client-side, at this one step).
- Traced what fixing this newly unlocks (`vmethod_6`'s body past line 790:
  `ExfiltrationController.InitAllExfiltrationPoints/EligiblePoints/SecretEligiblePoints`,
  `EndByExitTrigerScenario.Run`, the `TransitController.Exist` check) against our empty
  exits/transits and zero-exfil scenes — every loop degrades to a no-op on the empty arrays, so this
  fix does not just relocate the crash one step later; raid start should reach `GameStatus.Started`
  (`BaseLocalGame.cs:824`) cleanly.

**Fix applied:**

1. New file `ArenaMapPort.Client/SpawnInfiltrationRepairs.cs` — `SpawnInfiltrationFixPostfix`, a
   Harmony postfix on `SpawnPointsCollection.CreateFromScene(DateTime?, SpawnPointParams[])`
   (`SpawnPointsCollection.cs:152`, static, single overload — resolved via
   `AccessTools.Method(typeof(SpawnPointsCollection), nameof(...CreateFromScene))`, no ambiguity).
   Gated on `ArenaEquatorRaid.IsLoaded()`; iterates the returned `SpawnPointsCollection` (it implements
   `IEnumerable<ISpawnPoint>`), casts each to the concrete `SpawnPoint` type, and sets
   `Infiltration = "ArenaEquator"` wherever it is null/empty. This is the earliest point every one of
   our markers is collected into the exact `ISpawnPoint` instances both `SelectSpawnPoint` (`:529`) and
   `EmptyInfilFixPatch`'s own rescan read from later, so the mutation is guaranteed visible everywhere
   downstream. Logs once per raid at `[ArenaMapPort][RaidFix]` info level, only when it actually
   changed something (`CreateFromScene` runs exactly once per `BaseLocalGame.Run` — no per-frame spam
   risk).
2. `ArenaMapPort.Client/HarmonyPatches.cs` — added `using EFT.Game.Spawning;`; added
   `InstallSpawnInfiltrationFix(harmony)` as the *first* call inside `InstallAll` (must run before
   spawn selection, and its target fires earliest of every hook this file installs); added the
   `InstallSpawnInfiltrationFix` method following the file's existing `AccessTools` + `Verify(...)` +
   log-on-success convention, so a future game/SPT update that renames the method logs a clear error
   and disables only this one hook instead of crashing plugin load.
3. `UnityProject/Assets/ArenaMapPort/Editor/Import/AuthorScriptsScene.cs:132` — changed
   `spawnPoint.Infiltration = null;` to `spawnPoint.Infiltration = "ArenaEquator";`, with a comment
   pointing at `SpawnInfiltrationRepairs.cs` and noting the literal must stay in sync (this file runs
   in the Unity Editor assembly domain and cannot reference `ArenaMapPort.Client`, so the string is
   duplicated rather than shared — same pattern this file already uses for its own `SpawnDefNames`
   array). **Source-only change: no Unity/bundle rebuild was run**, per the hard rules — this is
   belt-and-suspenders for the *next* rebuild; the shipped fix is entirely the runtime Harmony postfix
   in (1)/(2), which needs no rebuild and takes effect on next plugin load.

Rejected alternative (considered per the supplied findings): adding non-empty `SpawnPointParams` to
`db/base.json` so `CreateFromScene`'s `FixSpawns` path sets `Infiltration` itself. Discarded —
`FixSpawns` (`SpawnPointsCollection.cs:196-211`) `DestroyImmediate`s any scene marker whose `Id` isn't
present in the params array; every `SpawnPointParams.Id` would have to exactly match a baked marker's
GUID, and a mismatch here silently deletes our only player spawn points (worse failure than the
current crash). The client-side postfix avoids that risk entirely and needs no generator/base.json
change.

## Finding deliberately deferred — `LocalGame.WeatherCurve` unguarded (info, not fixed this pass)

`EFT/LocalGame.cs:135`, `public IWeatherCurve WeatherCurve => WeatherController.Instance.WeatherCurve;`,
has no null check, and no `WeatherController`/`TOD_Sky` component exists in any of our 6 scenes, so
`WeatherController.Instance` stays null for the whole raid. Reachable via bot AI
(`LookSensor.cs:397,410`, `ZryachiyAbstractLayer.cs:167`) and, separately, `EFT.Airdrop/ServerPlane.cs
:118` / `ServerAirDrop.cs:104` reading `WeatherController.Instance.WeatherCurve` directly.

**Deferred, not fixed, because:**

- Reviewed severity is **info** (currently unreachable) — `db/base.json` ships `BossLocationSpawn: []`,
  `MinMaxBots: []`, `waves: []`, so no bots are configured this milestone and the two bot-AI call sites
  can never run. No airdrop system is wired up either. Per this task's own severity taxonomy this is
  neither a blocker nor error-spam, and "no bots" is an explicitly *acceptable* degraded state for this
  spike.
- The skeptic review's own correction of the proposed fix found it **incomplete as scoped**: a Harmony
  prefix on `LocalGame.get_WeatherCurve` (returning a benign `new WeatherCurve()`, per
  `EFT.Weather/WeatherCurve.cs:66-68`'s safe parameterless ctor) covers only the two bot-AI call sites
  — it does nothing for the two airdrop call sites, which read `WeatherController.Instance` directly
  and would NRE on the `Instance` access itself, before ever reaching a `WeatherCurve` getter. Fixing
  this properly needs either a shared helper all three prefixes call, or dropping the airdrop trigger
  from scope — deciding which is a design choice for whichever milestone actually adds bots or
  airdrops, not something to guess at now.
- Implementing a partial fix today would add an untested, unexercisable code path (nothing in the
  current scene set can trigger it) while leaving a documented gap in its own doc comment — worse than
  leaving a clearly-documented deferred item for the milestone that actually needs it.

No code change made for this item. Flagging it here (per this task's "what was deliberately deferred
and why" requirement) so the next milestone that adds bots or airdrops sees this before hitting the
NRE.

## Build

`dotnet build ArenaMapPort.Client/ArenaMapPort.Client.csproj -c Release` — 0 warnings, 0 errors. The
project's post-build targets re-deployed the DLL and re-staged bundles/`material-shaders.json` into
`dist/` as part of the same build.

Server was **not** touched this pass (no `base.json`/generator change was needed — see "Rejected
alternative" above), so `ArenaMapPort.Server.Tests`/`ArenaMapPort.Server` were not rebuilt.

## Changed dist files

- `dist/BepInEx/plugins/ArenaMapPort/maschine-ArenaMapPort.Client.dll` — rebuilt, contains the new
  `SpawnInfiltrationFixPostfix` hook.

Unchanged (re-staged from the same unmodified `work/build/full`/`work/build/preset` sources — no
bundle content changed): `dist/.../bundles/arena_equator_scenes.bundle`,
`dist/.../bundles/arena_equator_navmesh.bundle`, `dist/.../bundles/arena_equator_preset.bundle`,
`dist/.../ArenaMapPort/material-shaders.json`.

## Not done / next steps

- No in-game raid test was run this pass. The next step is exactly the test that produced the
  original hang: start a raid on `arena_equator` and confirm the pre-raid countdown now completes and
  the raid reaches `GameStatus.Started` instead of hanging.
- `LocalGame.WeatherCurve` guard — deferred, see above; revisit when a milestone adds bots or an
  airdrop system to `db/base.json`.
- No git commits were made, per the task's hard rules.
