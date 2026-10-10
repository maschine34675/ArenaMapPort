# First release preparation — Equator and Bay5

## Scope and implementation (2026-10-10)

User selected a few player controls with all remaining tuning in developer mode.
No tag, upload or publication was requested. Stable mod/map IDs and version 0.1.0 remain.

1. Captured accepted audio values before cleanup: Equator gain +5 dB, offset
   0.4037559, range ×2.5; Bay5 +4 dB, offset 0.399061, range ×4. Rain stays at 1.
   Bind both maps at startup so audio controls are present before the first raid.
   `AmbientDefaultsRevision=1` migrates only the entire old neutral tuple (0,0,1);
   any custom tuple remains intact. Subsequent neutral user choices are preserved.
2. Runtime `ArenaMapRegistry.All` now contains Equator/Bay5. `Catalog` retains all
   fourteen descriptors. Registration, locales, bundle interception, bot compatibility,
   map ownership and repair loops use the release subset. No loot/scav/gameplay scope changed.
3. Normal builds stage only these maps. Client/server scopes reversibly archive exact
   known parked outputs under `work/release-parked/`, with hashes and a move manifest.
   The two archive manifests contain 48 client files and 12 server files; source, sidecars,
   extraction packages and work/build outputs remain. Unknown files are rejected by the
   package validator. Links/redirected ancestors are refused before moving any file.
4. Player mode exposes exactly ten F12 settings (see README). DeveloperMode defaults
   false and is latched at process startup. Full supported-map tuning and diagnostic
   probes/hotkeys become available after restart. Functional lab/render/weather/audio
   repairs remain active in both modes. Original slider ranges, drawers, migration markers,
   legacy values and independent Equator profile values are preserved.

## Offline verification

- Release client: zero warnings/errors. Server builds through the test project.
- 606 C# tests passed, including exact runtime map membership, parked-scene exclusion,
  metadata restoration/idempotence, restart-latched developer mode and audio migration.
- Real BepInEx config load/save/reload harness passed accepted defaults, one-shot migration,
  restart persistence and existing retired-key checks.
- Python: 669 tests, 44 existing environment-dependent skips. Archive fixtures cover both
  scopes, all 60 outputs, preserved hashes, active/unknown/source files, a no-op rerun,
  full preflight before mutation and rejected linked ancestors.
- PowerShell Unity log scanner: eight cases passed.
- Distribution: exact two-map set, six bundles, two shader sidecars, two server DBs;
  unique Unity internal bundle names and staged DLL hashes match build outputs.
- Current full dist: 2,179 files / 1,434,920,589 bytes, including native audio/visual
  payloads and the existing optional ArenaMode audio/UI assets. No configs or debug DLL.
  Full SHA256 inventory: ignored `work/first-release-prep/release-inventory.json`.

## Native evidence — GPU-2, SPT 4.1.6, 2026-10-10 CEST

Process `9da9dbc1` (21:35:44), all 20 tracked DLL identities identical to host disk and VM.
The server was stopped before DLL deployment and restarted for the new registry.

- Location selection lists Arena: Equator and Arena: Bay 5 only, plus native locations.
  Bay5 shows two progressing server times twelve hours apart; Equator shows 12:00/00:00.
- F12 with the ArenaMapPort filter fits all ten player controls on one screen.
  UiBridge reads 294 bound entries, exactly ten nonhidden. Changing DeveloperMode to
  true does not change visibility or diagnostic execution until restart.
- Equator Day debug raid: 21:41:18 early Day profile selection, 21:41:38 Clear weather,
  21:41:52 accepted +5 dB / offset 0.404 / ×2.50 applied to native ambience. F10 in player
  mode produced no inspector lines; both the profile and scene repairs remain active.
- Bay5 debug raid: 21:43:09 native server time/weather, 21:43:17 +4 dB / offset 0.399 /
  ×4.00 applied. Raid starts alive with 440 HP and no bots. This was an admission/audio
  smoke check, not a new map-wide visual acceptance pass.
- Both debug raids returned to MainMenu; no ArenaMapPort error entries in the recorded
  post-start buffer. Day profile and rain source leases clean up at exit.
- All 891 prior host config assignments retained their values; three new assignments
  are DeveloperMode and the two audio migration markers.

Evidence: `work/first-release-prep/` logs and config baselines; screenshots under
`analysis/screenshots/first-release-prep-2026-10-10/` (ignored, no source art in Git).

## Delivery boundary

Use the sanctioned deploy script only. Existing extra files in an upgraded live install
remain inert; this task does not delete them. The clean release distribution is `dist/`.
Publication, final release text/version decision and a downloadable archive are separate
next steps; this change prepares and validates their inputs.

## Developer-mode and night follow-up

Fresh process `137d5aeb` (21:46:23) exposes 238 of 294 bound settings. The 56
permanently hidden entries include legacy profile aliases and migration markers.
F12 opened/filtered/closed normally. Turning DeveloperMode back off retained the
238 controls until the next restart, as documented. Diagnostic Harmony targets
rose from 57 in player mode to 66 in developer mode.

Ordinary PMC Equator Night, no bots: early Night selection 21:50:20, confirmation
and Rain override 21:50:40, temporary Chillwave grading 21:50:41, accepted ambient
mix 21:51:04. The atrium screenshot retains neon, haze, wet floors and grading.
F10 at 21:51:24 emitted a complete inspection (positive control for the earlier
player-mode absence). Rain diagnostic selected open atrium / outdoor bed with
3/9 covered samples, despite room 19 being IndoorConcrete. This verifies routing
and source playback; it is not a new listening/level acceptance claim.

Regular north-gate extraction succeeded. Cleanup logged zero ambient/MultiFlare
failures, released rain bus leases, restored reverb overrides and cleared Night.
The results flow returned to MainMenu. No ArenaMapPort errors appeared in the
recorded post-start buffers. No unrelated config or game setting was edited.

### Deployed artifact identities

Both sanctioned DLL-only promotions (dry run, then `-Confirm`) completed for
`D:/SPT41` and, after native checks, `D:/SPT41vanilla`. SHA256 matches in dist and
both installs:

- Client: `42C0A592438E217549F29D51C35E928D9597CBF9547699BB4C048D2DC5919D9A`
- Server: `2F2303CBD76107FA54AB5175B8177F79B505F2228B24E46CF4B1C78824FF1CE0`

Bundles/assets/server databases were not redeployed; runtime selection is controlled
by the new registry. The user's vanilla install needs a game and server restart.
Its saved config remains untouched; neutral ambience migrates on next plugin startup.

### Final state

Final GPU-2 process `f507a06e` (21:53:39) is idle at MainMenu, control free,
20/20 tracked binaries identical. Player mode restored: 294 bound settings,
10 visible / 284 hidden, DeveloperMode=false and 57 ArenaMapPort Harmony
targets instead of developer mode's 66. No errors in the final recorded buffer.
All 891 original host assignments and all 804 vanilla assignments still match
before/after snapshots exactly. Host adds only DeveloperMode=false and the two
audio migration markers; vanilla has not been started after deployment.

## Post-commit build identity follow-up

Final gate re-verification after commit `3182f8a` rebuilt both assemblies. All
runtime/package checks passed again (five gates met). The outputs carry the new
build identity, so their hashes supersede the pre-commit pair above. A Mono.Cecil
comparison against that native-tested pre-commit pair found all 1,572 client and
209 server method bodies identical; no functional code changed between runs.
The final pair was promoted again through the sanctioned DLL-only path.

- Client: `76ACDF3531F755D75583D90446965FEE6AFCAAB7825B7EECC167E3FDDD5C39EF`
- Server: `23E090E8BE8BD800F8CC9343E30F6A682A9B68DFEDAE4B42298091365737D7CB`

The server and EFT were stopped before the host promotion and started fresh.

Post-commit GPU-2 process `27186010` (22:00:04) confirmed the final build loaded,
20/20 tracked DLL identities identical, ten visible player controls, DeveloperMode
false and no errors in the recorded startup buffer. MainMenu, no raid, control free.
The verified final pair is identical in dist, host mirror and vanilla install.
Earlier Day/Bay5/Night evidence applies to the identical method bodies; the final
restart additionally verifies loading the actual committed build outputs.
