# PLAN-TWO-MAPS item 10 — location-select banner from Arena's own art (Equator + Bay5)

Status: SOURCE-VERIFIED, offline-built and unit-tested (2026-09-27). Not yet host-tested — the
next SPT-GPU-2 pass needs to confirm the location-select screen actually shows the two Arena
banners and the server log carries no `[UNHANDLED]` line; see "Host check" at the end.

## What vanilla does, what the ported scenes lack

EFT 0.16.9.5's location-select screen (`MatchMakerSelectionLocationScreen`) shows exactly one
picture per location: `LocationInfoPanel.Set()` reads only `location.Banners[0]` and assigns its
sprite (client decompile: `EFT.UI/LocationInfoPanel.cs:167`). The client resolves that picture's
URL from `Banner.pic.path` alone (`JsonType/LocationSettings.cs:146-163`: `ImageUrl => "/files/" +
pic.path`; `pic.file`/`pic.type` are server/tooling-only, the client's `ResourceKey` type has no
field for either) and fetches it through the SPT server's `ImageRouter`, which serves whatever
physical file was registered under that exact key
(`analysis/decompiled-server-415-full/SPTarkov.Server.Core.Routers/ImageRouter.cs`). A second,
separate consumer — `MatchmakerBannersPanel` (the matchmaking-wait / reconnect screens' carousel,
NOT location-select) — cycles every entry in `Banners[]` and builds each card's caption from
`banner.id + " Name"` / `" Description"` locale keys.

`tools/server/make_base_json.py` forces `Banners: []` for every ArenaMapPort location (loot- and
display-scope decision, SPEC §B.WP5 step 7 / §E.4 — Factory's own banner must not leak onto an
Arena map), so both Equator and Bay5 have always shown the client's built-in default picture
instead of anything Arena-specific.

Arena ships matching art for both maps in its own `resources.assets`:
`Matchmaker_Banner_Equator` (path_id 3030) and `Matchmaker_Banner_Bay5` (path_id 1361), both
769×460 `RGB24` `Texture2D` (vanilla's own banners are 764/765×460 — close but not identical;
nothing in the client path keys off an exact size, `Sprite.Create` just uses the texture's own
width/height). Re-verified this pass with a live UnityPy 1.25.2 read against
`D:\Battlestate Games\Escape from Tarkov Arena\EscapeFromTarkovArena_Data\resources.assets`
(read-only) — both objects really are `Texture2D`, really are named exactly that, and really
decode to 769×460.

## What was built

**`tools/extract/extract_banners.py`** (new). Reads the two `Texture2D` objects above with
UnityPy, verifies each one's own `m_Name` against the expected `path_id` (path_id drift would
otherwise silently point at the wrong Arena asset) and its decoded size against 769×460, then
writes `arena_equator.<ext>` / `arena_bay5.<ext>` (default `--ext jpg`, quality 95; `--ext png`
also supported) straight into the server mod's **deployed** folder:
`dist/SPT_Runtime/user/mods/ArenaMapPort/banners/`. That directory is a *sibling* of that same
folder's `db/`, not a subfolder of it, on purpose: `ArenaMapPort.Server.csproj`'s existing
`<Content Include="db\**\*.json">` and `DeployToDist` target both glob `db/**/*.json` only, so
putting the banner images anywhere under `db/` would have silently never reached `dist/` without a
csproj change (the gap the original research pass flagged). Landing them in their own top-level
`banners/` folder instead means `dotnet build` never has to know about them at all — the extractor
is a standalone, re-runnable step, exactly like `tools/build_all.ps1` already is for the Unity
bundles. Idempotent (re-running overwrites the same two files with the same source data) and BSG
art never touches git (`dist/` is gitignored; a `--output` override, e.g. for a one-off inspection,
should point at `work/`, never a tracked path).

Fails loudly (`HardExtractError`, matching this pipeline's own convention in
`extract_ballistics.py`/`common_ids.py`) rather than writing a wrong or placeholder image when the
Arena install is missing, the object at that `path_id` is not a `Texture2D`, its own name does not
match the expected asset name, or its decoded dimensions are not exactly 769×460.

Run this pass, into `dist/`:

```
$ python tools/extract/extract_banners.py
OK: arena_bay5 -> D:\ArenaMapPort\dist\SPT_Runtime\user\mods\ArenaMapPort\banners\arena_bay5.jpg (179397 bytes)
OK: arena_equator -> D:\ArenaMapPort\dist\SPT_Runtime\user\mods\ArenaMapPort\banners\arena_equator.jpg (117055 bytes)
```

**`ArenaMapPort.Server/ArenaMapBanners.cs`** (new). One small server-local table — Equator/Bay5
only — mapping each map's key to a banner id (`"arena_equator_banner"` / `"arena_bay5_banner"`,
PLAN-TWO-MAPS §10's own naming) and file stem. Deliberately **not** a field on
`ArenaMapDescriptor` (Shared/Client/UnityProject were out of scope for this pass, and keeping it
server-local means the twelve deferred maps need no descriptor change at all to stay bannerless).
Its `Resolve(map, bannersDir, warn)` looks for `<bannersDir>/<stem>.{jpg,jpeg,png}` on disk and,
**only on a hit**, returns the `Banner` entry to assign plus the `ImageRouter` key/physical path to
register; on a miss it warns once and returns `null` — the caller must not create a `Banners` entry
either way. This is the one invariant the whole design protects: AGENTS.md's documented landmine is
a `Banners` entry whose `pic.path` nothing serves, which resolves to an `[UNHANDLED]` server log
line (`SptHttpListener.GetResponseObjectAsync`'s generic JSON-API fallback, per the research pass —
not independently reproduced this round, treated as prior verified knowledge) and an empty client
error dialog at the location-select screen — never just "shows the default picture" the way a truly
empty `Banners: []` does. `ArenaMapBanners.ApplyLocale` stamps the two locale keys
(`"<id> Name"`/`"<id> Description"`, English only for this pass) the client's carousel panel reads,
mirroring `ArenaMapRegistration.ApplyLocale`'s own map-name keys.

**`ArenaMapPort.Server/ArenaMapRegistration.cs`** (edited). `ArenaMapPortMod` now takes `ImageRouter`
as a constructor DI parameter (same pattern Icebreaker uses — see THIRD-PARTY-NOTICES.md) and
computes `bannersDir = Path.Combine(modDir, "banners")` once. Inside the existing per-map
try/catch registration loop, right before `Register(...)`, a **second, inner try/catch** calls
`ArenaMapBanners.Resolve` and, on a hit, assigns `newBase.Banners` and calls
`imageRouter.AddRoute(routeKey, filePath)`, logging success; on a miss (or any exception) it only
warns — a banner-wiring problem can never block that map's own `Register()` call, matching
PLAN-TWO-MAPS §10's own instruction. `db/<key>/base.json` keeps shipping `Banners: []` for every
map, byte-for-byte unchanged (verified below) — the twelve deferred maps are completely untouched
by this change, and even Equator/Bay5's own base.json files on disk are unmodified; the `Banners`
list is built in memory at `OnLoadAsync` instead. The existing locale-transformer loop also gets
one more line: `lazyLocale.AddTransformer(locale => ArenaMapBanners.ApplyLocale(locale, map));`.

**Attribution.** `ArenaMapBanners.cs`'s `ImageRouter.AddRoute` wiring pattern and its multi-extension
probe reasoning are ported (adapted, not verbatim) from `IcebreakerMod.cs:367-397`'s own banner
block; the locale key convention likewise follows its `BannerLocales`/comment at `IcebreakerMod.cs:
352-353`. New `THIRD-PARTY-NOTICES.md` section: "ManimalIcebreaker (PLAN-TWO-MAPS item 10 —
location-select banner routing)", including the one deliberate divergence (Icebreaker still leaves
a `Banner` entry in its array when the file backing it is missing and only skips the route; this
repo never does — see that section for the detail).

## Verification run (2026-09-27, no host access this pass)

```
$ python tools/extract/extract_banners.py
OK: arena_bay5 -> ...\dist\SPT_Runtime\user\mods\ArenaMapPort\banners\arena_bay5.jpg (179397 bytes)
OK: arena_equator -> ...\dist\SPT_Runtime\user\mods\ArenaMapPort\banners\arena_equator.jpg (117055 bytes)

$ python tools/extract/extract_banners.py --check
OK: 2 banner(s) present under ...\dist\SPT_Runtime\user\mods\ArenaMapPort\banners

$ python -m unittest discover -s tools/tests -p "test_*.py"
Ran 366 tests in ~11s
OK (skipped=28)
# includes tools/tests/test_banners.py's 19 cases: the BANNERS table (map_key -> asset_name/path_id),
# output_filename/output_path, validate_dimensions, --check's own logic (all Unity-independent, no
# BSG data), plus one real-source pass (skipped automatically if the Arena install is unreachable)
# that decodes both objects and asserts they really are 769x460.

$ dotnet build ArenaMapPort.Server/ArenaMapPort.Server.csproj -c Release
Der Buildvorgang wurde erfolgreich ausgeführt. 0 Warnung(en), 0 Fehler(er).

$ dotnet test ArenaMapPort.Server.Tests/ArenaMapPort.Server.Tests.csproj -c Release
Bestanden!   : Fehler: 0, erfolgreich: 382, übersprungen: 0, gesamt: 382
# includes the new ArenaMapBannersTests.cs: For()/Resolve()/ApplyLocale() against a throwaway temp
# directory (no BSG art) -- Resolve never returns a Banner when the file is absent (both maps, every
# candidate extension), the jpg-over-png preference, the two maps' files are never confused, the
# built Banner/Pic/RouteKey/FilePath shape, and ApplyLocale's key format + no-overwrite/no-op cases.
```

`db/base.json` and `db/arena_bay5/base.json` confirmed unchanged (`Banners: []` in both, verified by
re-reading them after the edit — this pass never wrote to either file).

## Host check the next SPT-GPU-2 pass should do

1. Deploy: `dotnet build ArenaMapPort.Server/ArenaMapPort.Server.csproj -c Release` (already run
   this pass, stages the new DLL into `dist/`) and a client Release build (not run this pass —
   reserved for the concurrent Unity build chain), then
   `pwsh -NoProfile -File tools/deploy-to-spt.ps1 -SptRoot 'D:\SPT41' -Confirm` (also not run this
   pass) and a server restart.
2. `uibridge_log` filtered on `[ArenaMapPort]` right after server start: expect one
   `banner wired (/files/banners/arena_equator -> ...\banners\arena_equator.jpg)` / `arena_bay5`
   line per map (route key extension-free, physical path with the real extension — see
   `ArenaMapBanners.Resolve`'s own comment on why those must differ), and **no** `[UNHANDLED]`
   line naming either banner path.
3. Open the location-select screen through the normal main menu (not the `ArenaMapDebug`
   instant-start button — it bypasses this screen) and confirm Equator's atrium/"EQUATOR" sign art
   and Bay5's container yard art each show in place of the client's default picture, with no error
   dialog.
4. Optional, separate check: the matchmaking-wait/reconnect screen's banner carousel
   (`MatchmakerBannersPanel`) would additionally exercise the `"<id> Name"`/`"<id> Description"`
   locale text — needs the normal queue flow, not just the debug button, and was not itself
   source-verifiable further this pass (see the research citations under `client_url` in the
   session's own notes for why).

## Host evidence (SPT-GPU-2, 2026-09-27 16:01–16:03, server restarted, EFT ae4a6d7b)

Deployed to `D:\SPT41` (server DLL + `banners/arena_equator.jpg` 117 055 B, `banners/arena_bay5.jpg` 179 397 B), host
server closed and restarted from its desktop shortcut. Server log (mirrored to
`D:\SPT41\SPT_Runtime\user\logs\spt\spt-SPT-GPU-2-20260927.log`, times UTC):

```
14:01:33.363 [ArenaMapPort] arena_equator: location-select banner wired (/files/banners/arena_equator -> C:\SPT41\SPT_Runtime\user\mods\ArenaMapPort\banners\arena_equator.jpg)
14:01:33.365 [ArenaMapPort] arena_bay5: location-select banner wired (/files/banners/arena_bay5 -> C:\SPT41\SPT_Runtime\user\mods\ArenaMapPort\banners\arena_bay5.jpg)
14:02:40.725 [Client-Anfrage] /files/banners/arena_equator.jpg
14:02:40.730 [Client-Anfrage] /files/banners/arena_bay5.jpg
```

No `[UNHANDLED]` line, no client error dialog; the client fetches both images while the main menu loads. The
normal menu flow (ESCAPE FROM TARKOV → PMC → NEXT) opens the location-select screen, and selecting "ARENA: EQUATOR"
shows Arena's matchmaker art (mall interior, "EQUATOR" sign) above the name and description, "ARENA: BAY 5" the
container-yard art. The matchmaking carousel (`"<id> Name"`/`"<id> Description"`) was not exercised (it needs the
queue screens). **Status: VERIFIED.** Side observation for item 9: the fourteen "ARENA: …" labels overlap in two
rows on the location-select map; the release package should hide the twelve deferred maps.
