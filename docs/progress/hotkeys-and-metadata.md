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

## Compiled and native verification

Implementation **cd51df4** was committed before the final Release builds. Client build:
zero warnings/errors. Full C# suite: **615 passed**, with the existing nullable warnings
in test fixtures. No new tests were needed for these metadata-only edits.

The compiled DLLs were inspected with Mono.Cecil and Windows FileVersionInfo, including
the BepInPlugin attribute and the server metadata constructor. Both carry the exact
Company URL, matching GUID, mod version 0.1.0 and assembly/file version 0.1.0.0.
Before the fix, the client actually had blank Company and version 0.0.0.0; the server's
Company had fallen back to `maschine`. Evidence is under ignored
`work/naming-check/compiled-metadata.json` and `verify.ps1`.

Accepted SHA-256:

- Client: `A220930BC87BDFC07F4CB795A6AE2DF592E14D9605B491380C942FF6ED1B8299`
- Server: `D1BF13C6CF6DC4485095FB7DFE0BC5D31A4B5D96D98012C0CB3A8D7135522CD3`

The DLL-only deployment script was dry-run and confirmed for SPT41. Both hashes were
then checked directly on GPU-2's disk **before** starting server/game. New client
**0d555a71** (11:51:41 local) reached MainMenu with all 20 mod identities matching
build/disk/loaded code. Public configuration still has two entries, both false.

The live plugin root's `gameObject` component inventory positively includes
ArenaMapPort.Plugin and its functional neon/audio drivers, while all four diagnostic
pollers are absent. This corroborates the source audit in the running player-mode
build (`work/naming-check/hotkey-host.txt`). Do not infer this from the generic
inspector `find` action: its scene filter also omitted the existing persistent plugin,
so the direct plugin-root inventory was used instead.

At 09:51:19 UTC the native server ModValidator accepted **com.maschine.ArenaMapPort**,
version 0.1.0, SPT ~4.1.5. Both maps registered and no owned warning/error appeared
in the new startup interval (`work/naming-check/server-native.log`). Native checks
cover startup, metadata and component installation; gameplay code was unchanged.

After host acceptance, the same DLL-only dry-run/Confirm deployment updated
SPT41vanilla. All four installed DLL hashes and Company values match the build
(`work/naming-check/deployed-hashes.json`). Game/server restart on vanilla is the
user's step. GPU-2 EFT was closed gracefully; server stays running. No configuration
or map assets changed. Final documentation is a separate commit, without rebuilding.
