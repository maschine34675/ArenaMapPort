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
