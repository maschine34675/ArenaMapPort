# Arena 0.6 export compatibility audit — 2026-10-03

The installed source update needs export-tool maintenance, but **does not require
rebuilding or replacing the accepted Equator/Bay5 packages**. All audit outputs
were written under ignored `work/arena-update-audit/`; accepted v5 inputs, built
bundles, distribution art/audio and game installations were not changed by this
export audit. Runtime weather work is documented separately in
[map-weather-restoration.md](map-weather-restoration.md).

## Source identity

- Source: `D:/Battlestate Games/Escape from Tarkov Arena` (read-only).
- `EscapeFromTarkovArena.exe`: **0.6.0.47752**, product
  `0.6.0.0-47752-691e1412`.
- `UnityPlayer.dll`: **6000.3.6f1 (9f410a28ab1a)**; the serialized
  `globalgamemanagers` header independently reports `6000.3.6f1`.
- Reader: existing **UnityPy 1.25.2**, no dependency update necessary.
- BuildSettings contains 388 scene entries. Equator's 51–59 and the selected
  Bay5 78–90/188 identities remain intact. Equator's cached TagManager table
  still exactly matches the installed source.
- SPT and the bundle authoring project remain on Unity 2022.3.43f1. The exporter
  converts source objects to the neutral package format; source Unity 6 does not
  imply that the SPT bundle builder should be upgraded.

## Proven incompatibilities and fixes

### Menu art and UI sprites

Arena repacked `resources.assets` and reused old object IDs. Before the fix,
banner ID 3030 resolved to `GrayscaleRamp` and loadscreen ID 1558 to an unrelated
texture. Both tools correctly stopped, but could no longer export their art.
They now require exactly one Texture2D with the expected name in the selected
source file and still validate the known dimensions. Missing/duplicate names
remain errors. The old ID tables are retained as historical source evidence.

UI names are mostly unique, so the existing name lookup already survived the
update. Mode-logo names occur in multiple size tiers, however, and their ID
hints all moved. The catalog now records the 26 original sprite rectangles,
checked against the accepted UI manifest and the new source. Selection requires
one exact name/rectangle match (0.001 px float tolerance), even if an old ID was
recycled for another tier. Unknown or ambiguous geometry fails closed. Older
custom catalogs without rectangles retain their strict ID behavior.

After the fixes: **159 UI sprites, two banners and ten loading images** export
successfully. All 159 decoded UI images are pixel-identical to the accepted
previous exports, confirming tier and crop preservation.

### Generic map source plans

New/reordered scenes shifted seven of the twelve generic plans: **AirPit 2.0,
Custom, Factory, Iceberg, Shoreline, Woods and Yard**. Their indices were refreshed
from exact full scene paths; imported/reference scenes still require the unique
largest full-data copy. For example, Factory main moved from 373 to 333,
AirPit 2.0 main from 343 to 348, and Yard TeamFight from 309 to 310 (the smaller
duplicate at 316 is rejected).

All stored scene paths, including excluded scene records, are now checked
against BuildSettings before generic extraction. Excluded scenes are not loaded
and do not need a largest-copy choice; Bowl legitimately has two equally stripped
copies of its excluded culling scene. All twelve current plans pass source
mapping validation. No deferred map was exported, built or deployed.

Old packages retain their original plan hashes. Revalidating or re-extracting
one of those old generic packages against the new source is deliberately not
made to look unchanged by editing its provenance.

## New-source extraction coverage

Both release maps were fully extracted into new audit directories, including
native ballistic materials, ambient audio, NavMesh modifiers, physics sound
types, rain cover, light culling/flicker, static decals and 3D volume textures.
Native-volume refresh also passed for one volume on each map.

| Output | Equator | Bay5 |
|---|---:|---:|
| Exported objects | 39,398 | 59,425 |
| Meshes | 2,605 | 3,911 |
| Materials | 392 | 521 |
| Textures | 757 | 1,009 |
| Extract errors | 0 | 0 |
| Existing warning count | 219 | 188 |

Both packages pass `validate_extract.py --skip-reproducibility`, including its
current-source/native-feature and disabled-component checks. Existing warnings
remain non-blocking (unassigned source meshes, cube export notes, unused skinned
mesh data, and two Bay5 LOD references with unsupported source renderers).

The updated source is not byte-identical content: compared with accepted Bay5
v5, its main scene has three additional decal/render objects, one material and
one texture; native decal count is 502 versus 499. Mesh IDs/data and some texture
pixels also changed after the source rebuild. These are recorded source-content
differences, not silently promoted release changes. Equator component counts and
native-feature counts match v5. Full details: `comparison.json`.

Separate current-source exporters also passed without parser changes:

- **MultiFlare:** Equator 25/28 active lights, 33/48 active flares;
  Bay5 24/25 lights, 111/116 flares; original atlas and two textures each.
- **Bay5 water:** two source containers, one surface, four textures,
  2,271,980 native bytes.
- **Spatial audio/reverb:** both room/portal graphs, mixer effects, room tones
  and native bake data; Equator 31,676 routes, Bay5 5,924.
- **AI anchors:** Equator 14 core points/2 hubs/21 subpoints;
  Bay5 11/4/43. Both decoded sidecars are identical to the checked-in versions.
- **Arena audio:** 2,001 OGG clips exported and validated; 17 optional quiet
  clips were skipped by the existing silence threshold. No accepted audio was
  replaced, and newly discovered optional clips were not assigned gameplay cues.

## Regression and limits

Python pipeline suite: **665 tests run, successful, 44 existing optional skips**.
New regressions cover reused art IDs, duplicate/missing names, sprite-tier ID
reuse, rectangle drift/ambiguity/float tolerance, excluded scene drift, and every
repository plan against the installed BuildSettings. Final source review and
`git diff --check` found no unresolved defect in the changed exporter paths.

This is export compatibility evidence. No Unity bundle rebuild, new-source
in-game visual acceptance, full 12-map extraction, or two-run reproducibility
test was performed. The accepted map packages remain the inputs to release
builds. Main logs and source/comparison records are under
`work/arena-update-audit/` (`*-extract.log`, `*-validate.log`, `python-tests.log`,
`build-scenes.json`, `plan-index-changes.json`, `comparison.json`, and auxiliary
export logs).
