# Client folder and first GitHub release — 2026-10-11

User request: rename the installed client directory to `maschine-ArenaMapPort`,
leave the server directory unchanged, and publish the tested first release ZIP.

## Folder contract

Client staging, deployment, loading-art output and distribution validators now use
`BepInEx/plugins/maschine-ArenaMapPort`. Server paths remain
`SPT_Runtime/user/mods/ArenaMapPort`. GUID, configuration keys, map IDs and DynamicMaps
logical identifiers do not change. The unused shared folder-name constant follows
the new packaging path; the accepted DLLs do not use that constant at runtime.

Every runtime resource is rooted at the plugin assembly's actual location, including
bundles, water, acoustics, flares and DynamicMaps. No DLL or bundle rebuild is required.
Close EFT before migration, including the host receiving D:/SPT41 through synchronization:
an already loaded assembly still reports its old path until restart.

`tools/deploy-to-spt.ps1 -RenameClientFolder` is a dry run by default. With `-Confirm`,
it moves only the complete owned client directory. It refuses existing destinations,
linked paths, missing client DLLs and combinations with other deployment modes. Unknown
local files and empty directories move with their parent; no contents are deleted or
rewritten. Server data and configuration are outside this operation.

## Release payload

`maschine-ArenaMapPort-0.1.0.zip` derives from the accepted test3 archive. All 48 runtime
files and both third-party notices remain byte-identical; only the client ZIP-entry
prefix and package README change. Every entry is checked against the prior manifest
using SHA256 and ZIP CRC. The release contains 51 files, including three documentation
files. The unmodified test3 archive remains available locally for provenance.

Release notes: [v0.1.0](../releases/v0.1.0.md). The publication attaches the verified ZIP
and its SHA256 file. Local evidence under ignored `work/first-github-release/` records
the complete pre/post installation inventories, package comparison and uploaded asset
digests. The first-release gate ledger also verifies the source tag and remote HEAD.
