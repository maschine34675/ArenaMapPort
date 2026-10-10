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
