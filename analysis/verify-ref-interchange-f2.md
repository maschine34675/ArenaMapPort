# Verification: ref-interchange/F2 — "Geometry provenance is a retail EFT 1.1.5 rip, stamped in bundle-stamp.json"

## Verdict: Partially confirmed

The underlying fact (the stamp JSON contains these exact values, in both the source repo and the
deployed release) is confirmed by direct read. But the finding's framing overstates what the code
actually does with those two fields, and every line-number citation offered is wrong.

## What I checked

Read directly (read-only):
- `D:/sptmaps/_sources/SPT-InterchangeRework-main/bundles/bundle-stamp.json` (source repo copy)
- `D:/sptmaps/SPT-InterchangeRework-1.0.0-SPT4.1/BepInEx/plugins/LennoxP90-InterchangeRework/bundle-stamp.json` (deployed release copy)
- `D:/sptmaps/_sources/SPT-InterchangeRework-main/InterchangeRework.Shared/BundleStamp.cs`
- `D:/sptmaps/_sources/SPT-InterchangeRework-main/InterchangeRework.Client/PresetSwap.cs`
- `D:/sptmaps/_sources/SPT-InterchangeRework-main/InterchangeRework.Client/PerfectCullingRevival.cs`
- `D:/sptmaps/_sources/SPT-InterchangeRework-main/scripts/Package-Release.ps1`
- grep for `RipSource`/`RipBuild` across the whole `_sources/SPT-InterchangeRework-main` tree

## Confirmed as stated

- Both the source-repo and the deployed `bundle-stamp.json` do contain
  `"RipSource": "interchange-export-115"` and `"RipBuild": "1.1.5.47242"`, and the two files are
  byte-identical (same `GitSha`, `BuiltUtc`, `BundleBytes`, 18-scene list, `CullingGridHash`) — so
  the shipped release really does carry this exact provenance claim, not just the source tree.
  `bundle-stamp.json` (both copies), full file read.
- The file does list exactly 18 `Shopping_Mall_*_IR` scene names.

## Corrections / sharpening

**1. Every cited line number is wrong.**
- `RipSource`/`RipBuild` are at **lines 26–27**, not `:9-10`. Lines 9–10 in `bundle-stamp.json`
  are `"ModVersion": "1.0.0"` (line 2) territory — actually line 9 is the scene name
  `"Shopping_Mall_Culling_IR"`, nowhere near the Rip fields.
- The 18 scene names are at **lines 7–24** (the `"Scenes": [` array opens at line 6, closes at
  line 25), not `:5-22`.
- `BundleStamp.cs:9-21` is roughly right for the class body (opens line 9, closes line 22;
  `RipSource`/`RipBuild`/`CullingGridHash` properties are lines 19–21).

**2. "Consumed/validated by `BundleStamp.cs:9-21,33-58`" overstates what that range does.**
`BundleStampCheck.Compare` (the method at `BundleStamp.cs:35-61`, not `:33-58`) checks
`ModVersion`, `GitSha`, `BuiltUtc`, `BundleBytes`, and the `Scenes` list against the actual
bundle. **It never reads or compares `RipSource`, `RipBuild`, or `CullingGridHash`** — confirmed
by reading the full method body, lines 35–61, and cross-checked with a repo-wide grep of
`RipSource`/`RipBuild` (only 3 hits total: the JSON, the two property declarations in
`BundleStamp.cs`, and one `Write-Host` line in `Package-Release.ps1`). The class comment at
`BundleStamp.cs:17-18` says so explicitly: *"Which rip the scenes came from and the grid they
were baked against... Null is unknown, not wrong."* — i.e. the field's own author documents it as
informational, not enforced.

**3. `RipSource`/`RipBuild` are never validated anywhere in the codebase — only logged.**
The only place they're read outside the JSON/class declaration is
`scripts/Package-Release.ps1:49`: `Write-Host "gate ok: RipSource=$($stamp.RipSource)
RipBuild=$($stamp.RipBuild)"`. This is a log line, not a gate — unlike the actual gate three lines
above it (`Package-Release.ps1:39-44`), which `throw`s if `CullingGridHash` doesn't match the
shipped `*_packed_cull.bytes` filename. So "gate ok" here is misleading phrasing in the source
script itself; nothing throws or fails the build if `RipSource`/`RipBuild` are wrong, missing, or
fabricated.

**4. `CullingGridHash` *is* independently checked — at runtime, not in `BundleStampCheck.Compare`.**
`PerfectCullingRevival.cs:12,32-38,57-64` reads `CullingGridHash` from the stamp and compares it
against a hardcoded constant (`InterchangeGridHash = "3f8c141e1ca84602a218c54bf4429508"`, matching
the JSON value exactly) before re-enabling retail occlusion culling; a mismatch logs a warning and
leaves culling stripped. So of the three "provenance" fields, only `CullingGridHash` has an actual
consumer that acts on a mismatch (at build-package time via the `throw` gate, and at runtime via
this warning-and-skip). `RipSource`/`RipBuild` have no consumer that acts on their value at all.

## Net assessment

The finding's headline claim is true as a description of what's written in the JSON, and it's
present in the actual shipped release, not just an aspirational source-tree note. But calling it
"validated" (via the cited `BundleStamp.cs` range) is not supported — the code never checks
`RipSource`/`RipBuild` against anything; it's a self-reported attestation by whoever built the
bundle, echoed once in a non-gating log line at package time. There's nothing in this repo that
independently verifies the *mesh/texture data itself* came from a 1.1.5 rip — that trust rests
entirely on the stamp's author having written the truth into the JSON. For the arena-map-port
SPIKE, the actionable takeaway (stamp-file pattern to copy, `CullingGridHash` as the one
field with real teeth) still holds; just don't cite this as "code-validated provenance" beyond the
scene-count/byte-count/version checks that `BundleStampCheck.Compare` actually performs.

## Corrected citations (for anyone using this finding downstream)

- `RipSource`/`RipBuild` values: `bundles/bundle-stamp.json:26-27` (source) /
  `BepInEx/plugins/LennoxP90-InterchangeRework/bundle-stamp.json:26-27` (deployed).
- 18 scene names: `bundle-stamp.json:7-24`.
- `BundleStamp` class + Rip fields: `InterchangeRework.Shared/BundleStamp.cs:9-22` (props at
  19-21).
- Actual validation logic (does NOT touch Rip fields): `BundleStamp.cs:35-61`.
- Only consumer of `RipSource`/`RipBuild`: `scripts/Package-Release.ps1:49` (log only, no gate).
- Only consumer of `CullingGridHash` with a real check: `InterchangeRework.Client/PerfectCullingRevival.cs:32-38,57-64`
  (runtime, warn-and-skip) and `scripts/Package-Release.ps1:39-44` (package-time, throws).
