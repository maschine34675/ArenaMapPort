# Player-mode hotkeys and mod metadata — 2026-10-11

## Diagnostic shortcut audit

All four keyboard input paths already require DeveloperMode at installation:

| Default | Poller | Creation guard |
| --- | --- | --- |
| F7 | LightCullingProbe.Runner | LightCullingProbe.Install |
| F8 | RainCoverRepairs.LabHotkeyRunner | RainCoverRepairs.InstallLab |
| F9 | LightingLab.HotkeyRunner | LightingLab.Install |
| F10 | SceneInspector | SceneInspector.Install |

Player mode does not create these components, so it neither polls nor consumes their
keys. Binding KeyboardShortcut configuration entries alone does not install input
listeners. A repository-wide input search found no additional keyboard paths or
alternative component creation. DeveloperOptions is latched before the installers;
changing the mode requires an EFT restart. Existing F7–F10 developer defaults remain
useful and do not need to be unbound. The separate ArenaMapDebug test helper provides
mouse menu buttons and has no diagnostic keyboard listener.

The source audit corroborates the native player-mode F10 negative/developer positive
checks in first-release-prep.md and the F9 developer check in release-logging.md.
No gameplay or hotkey logic changed in this task.

## Naming and metadata corrections

Checked against `C:/SPT/Development/spt-mod-naming-convention.md`:

- Client/server GUID is now exactly `com.maschine.ArenaMapPort`; corrected the
  unpublished server's lowercase variant. Source checks of ArenaMode and ArenaMapDebug
  found only consumers of the already-correct client GUID, no lowercase dependency.
- Client BepInEx display name remains `maschine-ArenaMapPort`. The two DLL names
  already match `maschine-ArenaMapPort.Client.dll` / `.Server.dll` and both deployment
  directories correctly use `ArenaMapPort`.
- Both projects declare Authors=maschine and
  Company=`https://github.com/maschine34675/ArenaMapPort`.
- Removed the client's GenerateAssemblyInfo=false override: it had discarded the
  existing Company/Version properties from the DLL. There are no handwritten assembly
  attributes to duplicate. SDK-generated attributes now carry company and version.
- Added the missing server Company and metadata Url. Updated the old Equator-only
  spike description to reflect the supported Equator/Bay5 maps.
- Mod/project versions remain 0.1.0. The generated assembly/file version is 0.1.0.0.
  Server SptVersion ~4.1.5 matches all SPTushonka 4.1.5 references. Package locations,
  assembly names, map IDs and saved-config paths are unchanged.

Build, compiled-attribute inspection and deployment evidence will be appended after
verification. The user has not requested a release version bump or publication.
