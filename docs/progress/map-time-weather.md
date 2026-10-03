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
