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

