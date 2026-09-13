# fix-cs — review-issue fixes (2026-09-13)

Role: fixer. Re-verified both issues from the review payload against primary sources before
touching anything, then applied fixes, rebuilt client+server Release into `dist/`, and re-ran
server tests + `tools/validate/validate_preset.py`.

## Issue 1 (major) — locale keys registered under the wrong field

**Re-verification (independent, this pass):**
- Decompiled `SPTushonka.Server.Core` 4.1.5 myself with `ilspycmd -t LocationBase
  <user-home>/.nuget/packages/sptushonka.server.core/4.1.5/lib/net10.0/SPTarkov.Server.Core.dll`:
  confirmed `[JsonPropertyName("Id")] public required string Id { get; set; }` and, as a
  **separate** property, `[JsonPropertyName("_Id")] public MongoId IdField { get; set; }`.
- Confirmed client-side in `D:/SPT41/Development/_decompiled/JsonType/LocationSettings.cs:243/245/433`:
  `public string _Id; public string Id; ... LocalizedName => (_Id + " Name").Localized();`, and
  `D:/SPT41/Development/_decompiled/EFT.UI/LocationInfoPanel.cs:131/138` reads
  `location.LocalizedName` / `(location._Id + " Description").Localized()`.
- Confirmed the reference implementation this file explicitly ports
  (`D:/sptmaps/_sources/ManimalIcebreaker-main/icebreaker-server/IcebreakerMod.cs:111/252/258`)
  registers locale keyed by `IcebreakerLocation.Id`, which is itself the MongoId
  (`Directory.Build.props: ModLocationId=882b2fa04bbd616567022938`), not the location's string key.
- Conclusion: **confirmed correct as reported.** `ArenaEquatorRegistration.cs` (old lines 191-193)
  registered `locale[ArenaEquatorLocation.Key + " Name"]` (`"arena_equator Name"`), but the client
  looks up `(location._Id + " Name")` where `location._Id` deserializes from base.json's wire
  `"_Id"` field — server-side, `IdField` (the MongoId `"e9b8e12c60dfd2820e949462"`), never `Id`
  (the key). Would have rendered literal `"e9b8e12c60dfd2820e949462 Name"` on the map-select screen.

**Fix applied** (`ArenaMapPort.Server/ArenaEquatorRegistration.cs`):
- Extracted the inline locale-transform lambda in `ArenaMapPortMod.OnLoadAsync` into a new pure,
  testable static method `ArenaEquatorRegistration.ApplyLocale(GlobalLocaleDictionary? locale)`,
  keyed by `Id` (== `ArenaEquatorLocation.IdField`) instead of `Key`. Added `LocaleName` /
  `LocaleDescription` constants alongside it. `OnLoadAsync` now passes
  `ArenaEquatorRegistration.ApplyLocale` directly as the `AddTransformer` delegate.
- Updated the stale comment block above `ArenaMapPortMod` that said `"<Id> Name"` ambiguously.
- Added two regression tests in `ArenaMapPort.Server.Tests/LocationRegistrationTests.cs`:
  `ApplyLocale_KeysByIdField_NotByLocationKey` (asserts the `Id`-keyed entries exist with the right
  values AND that the old, wrong `Key`-keyed entries do **not** exist) and
  `ApplyLocale_ReturnsNull_WhenGivenNull`.
- Corrected `docs/SPEC.md` itself, since its own literal text (§B.WP5 step 6, §E.5, and the
  `Id`/`_Id` row of §E.4) was the source of the bug and contradicted its own cited evidence
  (`ref-manimal.md` §1) and the Icebreaker pattern it explicitly asks WP5 to port. Each edit is
  marked inline `(Corrected fix-cs pass, 2026-09-13 — ...)` with the decompiled evidence, per O5
  (no commit — left for the lead to review before it lands).

## Issue 2 (minor) — `patch_preset.py` output path drift from SPEC.md's literal text

**Re-verification:** confirmed `tools/preset/patch_preset.py:99` (`DEFAULT_OUT`) and
`tools/validate/validate_preset.py:28` (`DEFAULT_BUNDLE`) both point at
`work/build/preset/arena_equator_preset.bundle`, while `docs/SPEC.md` §B.WP3 said
`tools/extract/patch_preset.py` / `work/build/arena_equator_preset.bundle` (no `preset/`
subfolder, wrong tool folder). Also confirmed via `docs/progress/wp3.md`, `wp4.md`, `wp6-r1.md`
that `work/build/preset/` is the actual, already-shipped, already-downstream-verified convention
(WP4's dist-staging and WP6's re-read checks were both run against it), and that WP4's
`ArenaMapPortStageWpOutputs` MSBuild target globs `work/build/**/*.bundle` recursively, so the
subfolder never affected `dist/` staging. Not a functional bug — confirmed as originally assessed.

**Fix applied:** rather than moving the already-built, already-downstream-verified physical output
(which would have meant re-deriving WP3/WP4/WP6's already-passing acceptance evidence against a
new path for no functional gain), corrected `docs/SPEC.md` §B.WP3's "Owns"/step 9/"Outputs" lines
and §B.WP6's `validate_preset.py` table row to state the actual path
(`tools/preset/patch_preset.py`, `work/build/preset/arena_equator_preset.bundle`), each flagged
inline as a corrected drift from the frozen contract.

## Rebuild + verification

```
dotnet build ArenaMapPort.Server/ArenaMapPort.Server.csproj -c Release   -> 0 Warnung(en), 0 Fehler
dotnet test  ArenaMapPort.Server.Tests/ArenaMapPort.Server.Tests.csproj -c Release
  -> Bestanden!  Fehler: 0, erfolgreich: 22, übersprungen: 0, gesamt: 22   (20 pre-existing + 2 new)
dotnet build ArenaMapPort.Client/ArenaMapPort.Client.csproj -c Release   -> 0 Warnung(en), 0 Fehler
  [ArenaMapPort] deployed maschine-ArenaMapPort.Client.dll -> dist/BepInEx/plugins/ArenaMapPort
  [ArenaMapPort] staged arena_equator_preset.bundle from work/build into dist/.../bundles/
  [ArenaMapPort] staged material-shaders.json from work/build into dist/.../ArenaMapPort/
python tools/validate/validate_preset.py
  -> [validate_preset] OK -- all checks passed for D:\ArenaMapPort\work\build\preset\arena_equator_preset.bundle
```

`dist/` contents after rebuild (all timestamps fresh):
- `dist/BepInEx/plugins/ArenaMapPort/maschine-ArenaMapPort.Client.dll`
- `dist/BepInEx/plugins/ArenaMapPort/bundles/arena_equator_preset.bundle`
- `dist/BepInEx/plugins/ArenaMapPort/material-shaders.json`
- `dist/SPT_Runtime/user/mods/ArenaMapPort/maschine-ArenaMapPort.Server.dll`
- `dist/SPT_Runtime/user/mods/ArenaMapPort/db/base.json`

O4/O8: both csproj files' deploy targets write only under `dist/` (confirmed by reading their
`Target` blocks, both explicitly comment their own O4 compliance); no target references
`D:/SPT41` or `X:/` as a write destination. No `git commit` was run (O5).

## Files touched
- `D:/ArenaMapPort/ArenaMapPort.Server/ArenaEquatorRegistration.cs`
- `D:/ArenaMapPort/ArenaMapPort.Server.Tests/LocationRegistrationTests.cs`
- `D:/ArenaMapPort/docs/SPEC.md` (§B.WP5 step 6, §E.4, §E.5, §B.WP3 Owns/step 9/Outputs, §B.WP6 table)
- Rebuilt: `D:/ArenaMapPort/dist/**` (server + client DLLs, base.json, bundles, shader sidecar)
