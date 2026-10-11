# Official map descriptions — 2026-10-11

## Source and implementation

The installed Arena `EscapeFromTarkovArena_Data/resources.assets` contains both
released maps' descriptions in named TextAssets `TestBackendLocaleEn`,
`TestBackendLocalege` and `TestBackendLocaleRu`. These are shipped embedded locale
data, not a captured response from Arena's live backend. Their map keys are
`621df90bf200a905ed57d31d Description` (Equator) and
`62a05c960a18b50b7c575b1b Description` (Bay5). The current French/Japanese embedded
locales do not contain these map keys. Website wording was unnecessary.

`python tools/extract/extract_map_descriptions.py` reads these assets by name,
validates the two-map language tables and writes an ignored package under
`work/extract/map-descriptions/map-descriptions.json`. No original text is committed.
The server build requires and stages this file as
`SPT_Runtime/user/mods/ArenaMapPort/locales/map-descriptions.json`.

The server supplies the same original description to the location info panel
and banner carousel for each locale. German (`ge`, with a `de` alias), English
and Russian use their native texts; other languages use original English.
Map names, positions, geometry, weather and configuration are unchanged.
Missing or corrupt packages warn once and retain the authored descriptor fallback
so a broken text file cannot prevent map registration.

Synthetic tests cover localization, fallback, both UI key families and damaged
packages without copying game data into fixtures. Distribution validation requires
one valid locale package and byte equality with its extraction input.

## Verification

Implementation and native acceptance results will be appended after execution.

### Offline verification

Release build and 627 C# tests pass, including 12 new locale/fallback cases.
The extractor's 13 synthetic tests and 12 distribution tests pass. Real extraction
finds three languages; distribution validation confirms exact input/staged equality.
Focused independent review found no actionable defects. Existing nullable warnings
are confined to the test harness; the production server build is clean.

### Native acceptance and deployment

Implementation `fa4a484` was rebuilt in Release, then deployed through the sanctioned
full-copy path. The new server DLL and locale JSON were hash-matched on GPU-2
before restarting its server (PID 17336). The 11:30:22 UTC startup log registers
both maps without description/locale warnings.

GPU-2 instance `208f2f74` reached MainMenu with 20/20 matching plugin identities.
Both English descriptions were visually read in ordinary PMC location selection,
matching the extracted text without clipping. Screenshots:
`work/releases/locale-native-check/step-9-screenshot.png` (Bay5) and
`step-12-screenshot.png` (Equator). UiBridge's label snapshot did not expose the
description labels, so the successful scenario alone is not the text evidence;
the screenshots are. German/Russian routing is covered offline, not by changing
the host's language. No new raid was required for this server/menu-only change.

Both D:/SPT41 and D:/SPT41vanilla received the full 48-file distribution via script.
Vanilla requires the user's game/server restart. GPU-2 is left at MainMenu.
Client remains the accepted `e17198d` binary; bundles and configs are unchanged.

SHA256:
- Server: `7FFF7DA7EC3485477B3213D22FB7358943A792547EC3CC26CDC79D91B3984012`
- Locale package: `EC1AA164D2240414A4FCE2DA4CB9DDA78F7E33C46478CFB4E2363BD104D0829C`

### Updated local test ZIP

`work/releases/maschine-ArenaMapPort-0.1.0-test3-2026-10-11.zip` contains 48 runtime
files plus three documentation files (51 total), 1,231,750,103 bytes. Every ZIP entry
was streamed back and checked for CRC, exact length and SHA256 against its source.
No optional arena-audio/arena-ui payload or old loose Equator DB was reintroduced.
The matching `.manifest.json` records per-file hashes and separate accepted client
and server commits. Archive SHA256:
`f421c60b9d757119b7834bff4149eace3ca483fd5b96e5ec6af15fa3fee4bde2`.

