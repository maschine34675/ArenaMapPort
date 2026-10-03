# Native map time choices and Equator rain preview — 2026-10-03

The ArenaMode menu request extends Bay5 to the same native **12:00 / 00:00**
choices as Equator and makes Equator's midnight rain visible in the weather icon.
This belongs to ArenaMapPort: it already owns the maps' actual raid time and
Equator's rain override. ArenaMode must not add a second conflicting time owner.

## Behavior and boundaries

Bay5's shared descriptor and pipeline sidecar now declare fixed day/night hours.
The existing `FixedRaidTimeSelection` hooks retain the native toggles, listeners
and `CURR`/`PAST` settings, while `TimeOfDayRepairs` applies the corresponding real
raid start time. The existing server `FixedRaidTimeWeather` hook derives night
equipment from that selection. Both client and server must therefore be updated.
No scene bundle or map database regeneration is required.

Saved legacy `TimeOfDay / Enabled` and `ArenaFixedHour` values remain unchanged.
Bay5 now uses its explicit menu choice instead, including when the saved legacy
hour was 00:00. The existing internal `FixedTimeSelectionRevision` advances to 2
on the next client start and records the change once. Other maps retain their
legacy behavior; no lighting/configuration values are reseeded.

Equator's weather preview uses the same pure rain policy as the live override:
exact Equator identity, selected midnight, and both `Night neon (arena_equator)`
`Enabled` and `Rain` enabled. The native rain sprite replaces only the current
panel's icon. Daytime restores the exact native icon and active state; missing
rain sprites retain the native fallback. This does not modify backend weather,
weather curves, saved weather settings or the actual rain behavior.

Native `LocationConditionsPanel.Set` installs the presentation, while the native
`SelectedDateTime` setter refreshes it after a time toggle. This second seam is
necessary because `MatchMakerSelectionLocationScreen.UpdateSelectedTime` does not
call `Set` again. A weak per-panel owner preserves the native fallback. A `Set`
prefix drops the previous owner **before** vanilla can emit any new callbacks,
including when the same panel is reused for another map. No listener is added,
and no global/shared location or weather record is mutated.

The preview updates on map/time selection; configuration changes while the menu
is already open become visible on the next selection. Ordinary map panels retain
their original code path. As before, the generic `Weather (test)` override is not
mirrored into the native menu icon; this change previews only the requested
Equator midnight override.

Bay5 does not acquire Equator's artificial-sun targets, neon or rain. Its actual
clock/sky can start at midnight; the appearance of its authored fixed lamps is
unchanged. Native day/night visual acceptance remains a separate test.

## Offline verification

`pwsh -NoProfile -File tools/tests/verify_menu_time_weather.ps1 -Check Tests`
passes **487 tests**, none skipped. Coverage includes descriptor/sidecar parity,
the real server `WeatherHelper` under Harmony for both maps and unchanged other
maps, rain-policy boundaries, and the real client hook source installed with
Harmony on narrow UI doubles. The lifecycle checks exercise changing time without
another Set, first selecting night, hidden native weather, disabled feature/rain,
callbacks during panel reuse, distinct panels, missing sprites and one-clock
summary panels. The boundary double simplifies native Set's argument list and
initial toggle notifications; its event count proves only that this hook adds no
extra notification, not the full native screen's event count. This is not Unity
or native-menu acceptance.

`-Check Client` builds Release with `ArenaMapPortDllOnly=true`, without Unity,
scene builds or live installation writes. It passes with zero warnings/errors.
Only `dist` staging is performed. Independent review and final artifact identities
are tracked separately: review below, final post-commit build hashes in the
coordinating deployment receipt (Git/build metadata can change artifact hashes).

## Deployment and native acceptance

No deployment or GPU action was performed by this implementation task. Promotion
belongs to the coordinating ArenaMode task. Its receipt must identify both
post-commit staged hashes and the exact source commit before copying.
Both SPT server and EFT require restart. Preserve installed user configuration
files. Independent read-only review found no concrete unresolved correctness
issue in time selection or rain presentation; its explicit UI-double limit is
documented above.

Required native checks: Bay5 offers both fixed times, keeps the choice after
forward/back navigation, and logs/uses the selected raid hour; Equator night shows
rain immediately and returning to day restores native weather; Factory/ordinary
map time controls remain functional. Bay5 midnight lighting is explicitly not
certified by a successful menu screenshot or offline clock policy.

## Scoped DLL deployment follow-up — 2026-10-03

The coordinator requested a safe two-DLL path in the existing sanctioned script.
Use `tools/deploy-to-spt.ps1 -SptRoot 'D:\SPT41' -DllOnly` for its dry run, then
add `-Confirm`. This mode accepts only the exact existing client and server DLLs;
it cannot be combined with either DynamicMaps option and cannot perform a first
installation. All bundles, UI/audio assets, database files and configurations
remain outside the copy plan. The original whole-dist and DynamicMaps modes are
unchanged.

Both destination files are exclusively locked before the first write. Source
bytes must still match the reviewed plan, and both writes receive a SHA256
readback check. A failure attempts and verifies restoration of each prior DLL
independently; restoration failures are aggregated with the original error rather
than preventing restoration of the other file. No successful deployment is
reported after any failure. Filesystem/hardware failures can still prevent a
restore, in which case the error explicitly warns against starting EFT/SPT.

`tools/tests/test_dllonly_deploy.ps1` runs the actual entry point from temporary
fake repo/install directories. Its ten cases cover dry run, exact two-file scope,
repeat, conflicting options, missing source/installed DLL, locked target, original
whole-dist behavior and two injected write/restore failures. No live installation
is involved in these tests. All ten pass, along with all 90 cases in the existing
`test_dynamicmaps_deploy.ps1` regression suite. A separate independent read-only
review of the new deployment path found no unresolved concrete correctness issue.

## Coordinated deployment and GPU-2 menu acceptance — 2026-10-03

The coordinating ArenaMode task subsequently reverified all 487 tests, the client
Release build (zero warnings/errors), and all ten DLL-only deployment fixtures.
It deployed source `1cb4572` (menu implementation `aa0a8c1`) through the sanctioned
`-DllOnly` path to the local mirror and GPU-2. No bundles/assets or database files
were copied. Deployment receipt:
`D:/ArenaMapPort/work/map-time-weather-deployment-gpu2.json`.

| Artifact | Verified deployed SHA256 |
|---|---|
| ArenaMapPort.Client | `794B5AE286A30F64F0440B6315B1ACEED168322668C54A596E3050B5BD27CABA` |
| ArenaMapPort.Server | `3D54A40A79174F894BEDA0475F4F746482C6DDB961BB80E11FAF08D6AFF34E7F` |

**Native menu acceptance passed; no raid was started.** The user authorized GPU-2
because GPU-1 was unavailable. Final process `52a36096` started at 19:06:15
(Europe/Berlin), profile SPT-GPU-2, 1920x1080 FullScreenWindow, with ArenaMode
`5a63fa9` and the MapPort build above.

- Bay5 displayed both native 12:00:00/00:00:00 controls. Selecting night, advancing
  to MatchMakerAccept and returning kept PAST selected. The native UI and
  `TimeSelection ... selected=PAST` log agreed.
- Equator night immediately displayed the rain icon. Changing only the time to
  12:00 restored the sun icon. Selecting night again and then Factory restored
  Factory's native sun icon.
- A normal PMC Factory control screen retained its native information and time
  controls with no Arena mutators/panels.

Evidence is owned by the coordinating repo under
`D:/SPT41/Development/ArenaMode/artifacts/mutators-map-options-gpu2/`, notably
`03-bay5-night-return`, `04-equator-night-rain`,
`06-tdm-tracers-factory-weather`, and `08-pmc-factory-native`. The Equator day
transition was observed directly through Windows-MCP; the additional image-file
export for that view failed once through the proxy. This is not claimed as a
saved screenshot. Full report:
[GPU-MUTATORS-MAP-OPTIONS-RESULTS-20261003.md](D:/SPT41/Development/ArenaMode/GPU-MUTATORS-MAP-OPTIONS-RESULTS-20261003.md).

UiBridge confirmed loaded/disk/local identity at process start (24/24 maschine
plugins). A later parallel UiBridge disk update did not change the bridge version
already loaded in this process; the coordinating report records that distinction
and both identities. Arena artifact hashes remained unchanged. The complete saved
client log contained no Error/Fatal. All previously saved ArenaMode/MapPort values
were retained/restored; the intended MapPort difference was only
`FixedTimeSelectionRevision: 1 -> 2` (ArenaMode separately added its new false
Deathmatch tracer key). Game and server were stopped again after the menu test.

This native pass does **not** certify Bay5 midnight lighting, in-raid clock
progression, bot night equipment, or rain rendering in a raid. The time/gear path
has offline coverage only in this task; authored Bay5 lamps remain unchanged.
This follow-up is documentation only: no build or deployment was performed after
recording it, so the installed artifacts remain from `1cb4572`.
