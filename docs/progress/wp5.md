# WP5 — Server mod (SPT 4.1.5, net10.0) — progress log

Status: **done**. Builds clean (Release, 0 warnings/errors), 20/20 xunit tests pass,
`dist/SPT_Runtime/user/mods/ArenaMapPort/` has exactly the expected file set. Nothing
written into `D:/SPT41` or `X:/` (verified — see "O4 check" below). No git commit made
(O5).

## What was built

- `ArenaMapPort.Shared/ArenaEquatorLocation.cs` — **not written by this pass**: WP4 had
  already created it (with a note explaining why, dated 2026-09-13) by the time this
  pass started. Values match SPEC §0 exactly; consumed as-is, not modified.
- `ArenaMapPort.Server/ArenaMapPort.Server.csproj` — net10.0, `SPTushonka.*` 4.1.5
  package references (see "Deviations from the brief" below), `DeployToDist` target
  copying the DLL + `db/*.json` into `dist/SPT_Runtime/user/mods/ArenaMapPort/` only.
- `ArenaMapPort.Server/ModMetadata.cs` — `IModMetadata` implementation (every SPT mod
  needs exactly one; SPEC's algorithm didn't call this out explicitly, but skipping it
  would very likely leave the mod unloaded at server start — see "Open items" below).
- `ArenaMapPort.Server/ArenaEquatorRegistration.cs` —
  - `ArenaEquatorRegistration` (static, DI-free): `EnsureAvailable`/`Register` ported
    from Icebreaker's `IcebreakerLocationRegistration`, and `BuildLocation(LocationBase)`
    which stamps the §0 identity constants, sets `DisabledForScav`, and fills the
    LazyLoad-wrapped loot fields with Icebreaker's empty-but-non-null shape.
  - `ArenaMapPortMod` (`[Injectable(TypePriority = OnLoadOrder.Preload + 90000)]`,
    `IOnLoad`): loads `db/base.json`, calls the two static helpers above, registers the
    two locale keys via `LocaleTable.Global[*].AddTransformer(...)` (Icebreaker's
    pattern), logs `"[Manimal-ArenaMapPort] Arena: Equator (spike) registered as an
    independent location"` on success.
- `ArenaMapPort.Server/db/base.json` — per SPEC §E.4. `SpawnPointParams` key is
  **absent** (not `null`, not `[]`) — verified by a raw-string test, not just a
  deserialized-null test.
- `ArenaMapPort.Server.Tests/` — xunit, 20 tests, no live server needed:
  - `LocationRegistrationTests.cs` (11 tests): `EnsureAvailable` on a fresh table / a
    duplicate key / a colliding `IdField` under a different key; `Register` adds and
    is retrievable, and throws on a second call for the same key; `BuildLocation`
    stamps identity + `DisabledForScav`, and its `LooseLoot.Value`/`StaticLoot.Value`/
    `StaticContainers.Value`/`StaticAmmo`/`AllExtracts` are all non-null and
    structurally empty (the exact NRE class Icebreaker's own comment warns raid start
    hits on a null value).
  - `BaseJsonTests.cs` (9 tests): loads the shipped `db/base.json` and asserts `Id`/
    `IdField`, `Scene.path`/`Scene.rcid`, `SpawnPointParams` absent from the raw text
    **and** null once deserialized, `Banners == []`, `DisabledForScav == true`,
    `Enabled == true` / `Locked == false`, `Loot == []`, `MinPlayers == MaxPlayers == 1`.
- `THIRD-PARTY-NOTICES.md` — created (WP3's own section was appended by a parallel
  agent afterward; both sections coexist, no conflict). WP5's section credits
  `ManimalIcebreaker-main` (MIT, danauraborealis) for the registration mechanism, the
  loot-LazyLoad shape, the `OnLoadOrder.Preload + 90000` priority, and the
  locale-transformer pattern.

## Deviations from the task brief — verified, not assumed

SPEC §B.WP5 explicitly told this work package not to assume the brief's `net9.0` /
"same versions as `ArenaMode.Server.csproj`" shorthand was correct, and to verify
against the live 4.1.5 binary instead. Verifying turned up two concrete corrections:

**1. `net10.0`, not `net9.0`.** `D:/SPT41/SPT_Runtime/SPT.Server.runtimeconfig.json`
(the live deployed server's own runtime config) declares `"tfm": "net10.0"` against
`Microsoft.NETCore.App`/`Microsoft.AspNetCore.App` 10.0.0. `ArenaMode.Server.csproj` —
a sibling mod already built, deployed and playtested against this exact install per
its own `CLAUDE.md` — also targets `net10.0`. `net9.0` would not match either piece of
evidence.

**2. `SPTushonka.*` NuGet packages at `4.1.5`, not `SPTarkov.*` at `4.1.0`.**
- `https://api.nuget.org/v3-flatcontainer/sptarkov.server.core/index.json` tops out at
  `4.1.2` — no release on the official feed matches the live `4.1.5` binary.
- Decompiling the live `D:/SPT41/SPT_Runtime/SPTarkov.Server.Core.dll` with `ilspycmd
  11` (this pass) shows `FileVersion 4.1.5` and `Utils.Json.LazyLoad<T>`'s constructor
  as `(Func<T> deserialize, bool cacheValue = false)`. The `4.1.2` `SPTarkov.Server.Core`
  NuGet package (downloaded and decompiled the same way, this pass) still has only the
  old one-arg `(Func<T> deserialize)` constructor — confirming `critic.md`'s general
  4.1.0-reference-vs-4.1.5-live drift warning applies concretely to this exact type,
  which WP5's algorithm (§B.WP5 step 3) directly depends on.
- `https://api.nuget.org/v3-flatcontainer/sptushonka.server.core/index.json` **does**
  list `4.1.5`. Downloaded and decompiled that package: `FileVersion 4.1.5`, and the
  `LazyLoad<T>` constructor is byte-for-byte the same two-arg shape as the live DLL's.
  (Package id is `SPTushonka.*`; the assembly inside is still named
  `SPTarkov.Server.Core.dll` / `SPTarkov.Common.dll` / `SPTarkov.DI.dll` — same
  namespaces, so no code changes were needed beyond the `<PackageReference Include=...>`
  lines.)
- This isn't a guess: Icebreaker's own `icebreaker-server.csproj` (the MIT reference
  this file's registration pattern and loot shape are ported from) already pins
  exactly `SPTushonka.Common/.DI/.Server.Core Version="4.1.5"` — independent
  confirmation that this is the known-correct feed/version for a 4.1.5-targeting mod.

`SPTushonka.Reflection` / `SPTushonka.Server.Web` were **not** referenced (unlike
`ArenaMode.Server.csproj`, which needs them for a Blazor config page this spike
doesn't have) — `icebreaker-server.csproj`, doing the same registration-only job,
references the same three packages and nothing else.

All three `SPTushonka.*` package references use `ExcludeAssets="runtime"`
(`ArenaMode.Server.csproj:39-43`'s own pattern) — the live server process supplies
these assemblies itself; only the compile-time API surface is needed.

The exact commands used to verify all of the above (kept out of `analysis/`, which is
read-only for every WP, but reproducible from this list):
```
curl -s https://api.nuget.org/v3-flatcontainer/sptarkov.server.core/index.json
curl -s https://api.nuget.org/v3-flatcontainer/sptushonka.server.core/index.json
powershell -Command "(Get-Item 'D:/SPT41/SPT_Runtime/SPTarkov.Server.Core.dll').VersionInfo.FileVersion"
ilspycmd -t SPTarkov.Server.Core.Utils.Json.LazyLoad`1 D:/SPT41/SPT_Runtime/SPTarkov.Server.Core.dll
ilspycmd -t SPTarkov.Server.Core.Models.Eft.Common.Location            D:/SPT41/SPT_Runtime/SPTarkov.Server.Core.dll
ilspycmd -t SPTarkov.Server.Core.Models.Eft.Common.LocationBase        D:/SPT41/SPT_Runtime/SPTarkov.Server.Core.dll
ilspycmd -t SPTarkov.Server.Core.Models.Eft.Common.StaticContainerDetails D:/SPT41/SPT_Runtime/SPTarkov.Server.Core.dll
ilspycmd -t SPTarkov.Server.Core.Models.Common.MongoId                 D:/SPT41/SPT_Runtime/SPTarkov.Server.Core.dll
```
(then the same `-t` calls against the downloaded `4.1.2` `SPTarkov.Server.Core` and
`4.1.5` `SPTushonka.Server.Core` nupkgs' `lib/net10.0/*.dll`, for the side-by-side
diff described above).

**3. `StaticContainerDetails`'s real field names** — SPEC §B.WP5 step 3 flagged
`ObjectsSpawnPoints`/`Containers` as a placeholder "verify at implementation time".
The live 4.1.5 shape (confirmed via the same decompile) is
`StaticWeapons`/`StaticContainers`/`StaticForced`, all `IEnumerable<T>`, none
`required`. `BuildLocation` uses the real names.

## O4 check (no writes outside this repo)

```
find /d/SPT41 -iname "*ArenaMapPort*"   -> (nothing)
find /x       -iname "*ArenaMapPort*"   -> (nothing)
ls /d/SPT41/SPT_Runtime/user/mods/      -> only ArenaMode (pre-existing)
```

## Verification run

```
cd D:\ArenaMapPort\ArenaMapPort.Server
dotnet build -c Release        # 0 Warnung(en), 0 Fehler
cd D:\ArenaMapPort\ArenaMapPort.Server.Tests
dotnet test                    # Bestanden!: Fehler: 0, erfolgreich: 20, gesamt: 20
```
`dist/SPT_Runtime/user/mods/ArenaMapPort/` after the Release build:
```
db/base.json
maschine-ArenaMapPort.Server.dll
```
— exactly the expected file set (matches `db/base.json` byte-for-byte with the source
under `ArenaMapPort.Server/db/`; no stray `.pdb`/`.deps.json`, `DebugType=None` +
`GenerateDependencyFile=false` in the Release `PropertyGroup`).

## Open items / handoff

- **§G.S4 is a live-server check this WP cannot run** — WP5's own acceptance criteria
  (offline: build + xunit) are met, but the actual "`OnLoadAsync` runs with no
  exception, `[Manimal-ArenaMapPort] ... registered`, map-select shows the location"
  chain needs the dist/ tree promoted into a real `D:/SPT41` install by the user
  (decision #9 / O4 — never automated).
- **`ModMetadata.cs` isn't called out anywhere in SPEC §B.WP5's algorithm**, but every
  SPT mod needs exactly one `IModMetadata` implementation per the interface's own doc
  comment and per both reference mods (`ArenaMode.Server/ModMetadata.cs`,
  Icebreaker's inline `ModMetadata` in `IcebreakerMod.cs:27-53`). Added one; flagging
  in case the Architect wants a different `ModGuid`/`Version`/`SptVersion` range than
  the placeholders chosen here (`com.maschine.arenamapport`, `0.1.0`, `~4.1.5`).
- **Bot cap / `ScavRaidTimeSettings` / loot multipliers / `NonMaps`** — left as a
  `TODO(bots-milestone)` comment in `ArenaMapPortMod.OnLoadAsync`, per SPEC §B.WP5
  step 5 ("skip for the spike"). `analysis/ref-manimal.md` §1 has the retail levers
  Icebreaker uses, for whoever picks this up.
- **WP6's `validate_server_db.py`** can run directly against
  `dist/SPT_Runtime/user/mods/ArenaMapPort/db/base.json` — the same file the xunit
  suite already checks, so the two should never disagree; if they ever do, the xunit
  suite is the one to trust (it round-trips through the real `LocationBase` model with
  the real `StringToMongoIdConverter`, not a hand-rolled JSON parser).
- Server mod folder is `ArenaMapPort` (not `maschine-ArenaMapPort`) under
  `user/mods/` — matches Icebreaker's own asymmetric assembly-name-vs-mod-folder
  convention (`icebreaker-server` assembly -> `ManimalIcebreaker` mod folder) and SPEC
  §0's explicit `user/mods/ArenaMapPort/` value.
