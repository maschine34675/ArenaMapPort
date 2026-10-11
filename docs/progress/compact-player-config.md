# Compact player configuration and natural Equator midnight

## 2026-10-10 — implementation

User supersedes the ten-control player menu with one appearance switch. Normal F12
exposes `Appearance/NaturalEquatorNight`; false retains the accepted neon recipe.
True selects the Day appearance entries for the next Equator midnight raid and
disables the owned neon installation, accents/chemlights, flicker, atmosphere,
temporary PostFX and additional wet-floor boost. Actual time remains 00:00, rain
policy stays unchanged, SAIN remains FactoryNight. Day and Bay5 are unaffected.
The selection is captured before scene loading and retained through InitWeather,
including its loader-bypass fallback. Changing the switch during a raid cannot
mix scene-applied values with another recipe.

`CompactConfiguration` disables autosave before any binding. It archives all old
nonpublic saved keys (including unknown and parked-map values) through a temporary
file and atomic replacement before removing them from the primary configuration.
The primary contains two entries: the visible appearance switch and hidden
`General/DeveloperMode`. The latter requires a manual edit with EFT closed and
a game restart; no additional player control is displayed.

All functional binds now use `Plugin.Settings`. In normal mode this is a non-saving
ConfigFile reading the optional `.developer.cfg` archive, or accepted code defaults
when no archive exists. No developer file is created for a fresh player install.
In developer mode Settings equals the primary plugin ConfigFile, restoring archive
values while preserving current primary values, so native ConfigurationManager
discovers the full supported settings. Returning to normal mode archives the latest
developer values first. The ordinary save path and raid transitions cannot expand
the public file. Retired-key cleanup explicitly postpones persistence to finalization.

Validation covers real installed BepInEx load/bind/save/reload, fresh versus old
configuration, unknown-key merging, developer roundtrips, startup mode latching,
read-only archives, all Day/Night appearance pairs, actual midnight/rain resync and
next-raid behavior. Native checks and final artifacts are recorded below when run.

## 2026-10-10 — build and native acceptance

Implementation commit `88c5d45`. Final Release build succeeds with zero client
warnings/errors. The complete C# suite and installed-BepInEx persistence harness
pass; the latter additionally proves a locked archive leaves both original files
unchanged. Two independent read-only reviews found no blocking defects. The new
configuration layer was tested against BepInEx 5.4.23.5, including actual serialized
files, not only the unit-test facade. Existing test-project warnings are unrelated.

GPU-2 instance `5691d214` (22:22:48 CEST) loaded all 20 tracked DLLs identically
to host disk and VM build. Normal F12 displays exactly one checkbox even with its
Advanced filter enabled. UiBridge reports two public entries: visible
NaturalEquatorNight and hidden/protected DeveloperMode. Runtime Settings has
SaveOnConfigSet=false.

The real previous main CFG contained 294 assignments / 124333 bytes. After startup
and player toggles it contains two assignments / 784 bytes (783 with `true`).
All 293 nonpublic values match the archive exactly; archive size is 9860 bytes.
Archive SHA256: `6940546D3151FABF6C63A31606582FE9F240B11933CAC13C96ABABA7B05C02AA`.
It remains unchanged across the raids. A fresh install's absence of a developer
file is established by the real-BepInEx harness; this host exercised migration.

Native sequence, same process:

- 22:25:44: `selected Night, appearance=Day, naturalNight=True` before scene load.
  22:26:03: native initialization retained, start 00:00, Rain applied.
  22:26:14: SAIN FactoryNight. At atrium `(0,.1,14)`, yaw 180, original lamps remain;
  added neon root, temporary PostFX and distance-haze objects are null, IsActive=false.
  Setting the switch back to false during this raid keeps IsActive=false.
- 22:29:03: next raid selects Night/Night, naturalNight=False. At 22:29:08 the runner
  creates 12 strips / 12 lights; temporary PostFX and distance-haze instances exist.
  The same atrium view visibly restores neon lighting and wet-floor reflections.
  Regular north extraction after both Equator raids preserves the living profile.
  After neon extraction the root and temporary PostFX are null again.
- 22:31:07: Bay5 NO BOTS control starts with native server time/weather, no Equator
  neon root, and the same two public settings. Protected debug disconnect returns
  to MainMenu. No ArenaMapPort warnings/errors in the recorded session.

Screenshots and paired config/player snapshots were saved and visually inspected:
`analysis/screenshots/compact-player-config-2026-10-10/{player-menu,natural-night,neon-night,bay5-control}.png`.
Read-only log export: `work/compact-player-config/native-raids.json` (624 plugin lines).
This is rendering/lifecycle acceptance, not another subjective audio balance review.

Both DLLs were deployed through dry-run then confirmed `tools/deploy-to-spt.ps1
-DllOnly` to D:/SPT41 and, after the raid checks, D:/SPT41vanilla. No config files
were edited manually. The vanilla CFG remains byte-identical to its pre-task copy;
its runtime migration takes place when the user restarts that installation.

Final build SHA256 (no rebuild after the documentation-only commit):

- Client: `05C0A70E47C5B80171E0EDA0C6FCDA1C502EF08FBD567D5944A89DF37ACF7251`
- Server: `471F3C206F5058DB8F47F7CC13938E6C03BD42E5C7AECAD815DBEFC78A6F69CE`

Default and final player switch is false (the accepted neon night remains default).
No publication, tag, map assets or version changes.

Final restart: instance `0fc7d88f`, started 22:33:31, MainMenu, 20/20 tracked DLLs
identical, normal mode still exactly two registered entries / one visible switch.
No ArenaMapPort warnings/errors in its recorded startup buffer. The primary stays
784 bytes and all 293 archived values plus the archive hash remain unchanged.
Both DLL hash pairs match dist, the host mirror and the vanilla installation.

## 2026-10-11 — advanced developer switch and rounded offsets

User requests DeveloperMode as an advanced F12 setting and both ambient offsets
rounded to 0.4. DeveloperOptions now uses the same ConfigurationManagerAttributes
IsAdvanced metadata contract as the existing HDR controls. MapConfigVisibility
allows this entry in both modes; the normal menu still has just NaturalEquatorNight
when the Advanced filter is off. Mode remains startup-latched, and compact persistence
still contains exactly two entries.

AmbientDefaultsRevision 2 selectively rounds Equator's 0.4037559 and Bay5's 0.399061
to 0.4, with no changes to gains or ranges. Arbitrary custom offsets, deliberately
neutral mixes already migrated at revision 1, and later manual reversions remain
intact. Normal mode applies the migration in memory without rewriting its archive;
developer mode saves it through the normal full-config path.

### DynamicMaps delivery investigation (no implementation)

Installed DynamicMaps 1.2.1 still receives separate optional two-file packages under
`mpstark-dynamicmaps/Maps/ArenaMapPort_Equator` and `ArenaMapPort_Bay5`; these are absent
from the ordinary ArenaMapPort dist. Current deployment already preserves its DLL
and configuration, but adds our subdirectories beneath its Maps folder.

Local source and the installed DLL agree: public
`MapSelectDropdown.LoadMapDefsFromPath(string relPath)` combines the argument with
DynamicMaps.Plugin.Path and incrementally loads JSON/JSONC files. An absolute path
works. There is no extra-search-path setting or automatic external-map discovery.
SVG layer paths are also resolved against DynamicMaps.Plugin.Path, rather than the
definition's directory (`SvgUtils.GetOrLoadCachedSprite`).

A small optional adapter can load `ArenaMapPort/dynamicmaps` after native Maps
loading, and resolve our two definitions' SVG paths to that owned directory before
precache/selection. Scope hooks to the detected plugin and exact owned files;
retain native marker paths, guard reentry, and handle pre-existing packages so they
do not appear twice. This needs no DynamicMaps DLL, CFG or folder modification.
The current turn only investigates this alternative; packaging remains unchanged.

### Validation and deployment

Implementation `2f0ff31`: all 610 C# tests pass, including selective revision-2
migration, preservation of custom mixes, advanced visibility tags and compact
persistence. The installed-BepInEx ConfigRetirementCheck passes. Client Release
build has zero warnings/errors; unrelated test-project warnings remain.

GPU-2 instance `85aa779c`, started 2026-10-11 10:27:25 CEST, MainMenu, 20/20
tracked binaries match VM/host/loaded code. UiBridge lists DeveloperMode as advanced
and restart-required, with NaturalEquatorNight normal. F12 was visually checked
with Advanced enabled and disabled: two options versus only NaturalEquatorNight.
The filter was restored to its original enabled state; DeveloperMode remains false.
Search temporarily overrides ConfigurationManager filters, so the negative control
was performed with an empty search field. Screenshots:
`analysis/screenshots/advanced-developer-2026-10-11/advanced-{on,off}.png`.

Read-only runtime inspection of Bay5AmbientAudio._equatorOffset.Value and
_bay5Offset.Value returns 0.4 for both. This host had no developer archive before
the run and still creates none; its main CFG remains two entries / 765 bytes.
No ArenaMapPort warnings/errors in the recorded startup buffer. No additional
raid or subjective audio-balance test was needed for rounding these two constants.

Dry-run then confirmed DLL-only deployment completed for D:/SPT41 and, after the
native checks, D:/SPT41vanilla. Both DLL pairs match dist. Vanilla's config remains
byte-identical to the pre-task copy; that installation requires the user's restart.
No DynamicMaps files were written. Final artifact SHA256:

- Client: `4C3D143C068748CB755C78FDD838BD7F85C21E56756F631B139E71D6DA047AA0`
- Server: `0C5AB8D480C697026850F80F0815891F2C5661D341BA0B2FA74CFCDB587C26DA`
