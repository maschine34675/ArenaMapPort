# Arena 0.6.2.0.48473 (2026-11-01): what changed for the port

The user updated Arena to 0.6.2 on 2026-11-01 (no patch notes found). The VM (`D:\Battlestate Games\Escape from Tarkov Arena`, files
of 01.11.2026 11:10) and MASCHINE11 both report `"Version":"0.6.2.0.48473"` in `ConsistencyInfo`; GPU-2 still has 0.6.1.0.48206
(`\\192.168.178.109\C$\Battlestate Games\...`), which made a direct comparison possible. Everything below was read only.

## Result

**The maps did not change and no release is needed.** Released 0.4.0 and the unreleased Iceberg open on 0.6.2 with their existing key
files; the rewritten level files differ only in renumbered pointers into shared files and two procedurally generated dust meshes.

## Stage 2 keys (the question that matters for players)

`ArenaMapPort.BundleCrypt verify-keys` with the dist key files and `work/release-keys`, against both 0.6.2 installs:

```
OK verify-keys arena_equator: wrap 0 (built for 0.6.1.0.48206) unlocks the store's content key against Arena 0.6.2.0.48473 at D:\Battlestate Games\Escape from Tarkov Arena (3 files, 3,145,728 of 218,007,984 B sampled, 368 ms, crypto: CNG)
OK verify-keys arena_bay5: wrap 0 ... (8 files, 7,403,104 of 358,204,848 B sampled, 973 ms, crypto: CNG)
OK verify-keys arena_airpit: wrap 0 ... (7 files, 6,906,912 of 603,532,272 B sampled, 1321 ms, crypto: CNG)
OK verify-keys arena_bowl: wrap 0 ... (8 files, 7,371,520 of 754,464,336 B sampled, 1631 ms, crypto: CNG)
OK verify-keys arena_iceberg: wrap 0 ... (4 files, 3,224,800 of 560,470,032 B sampled, 121 ms, crypto: CNG)
```

The same five lines are OK against `\\MASCHINE11\Battlestate Games\Escape from Tarkov Arena` (85-221 ms). The four key files inside the
published `maschine-ArenaMapPort-0.4.0.zip` (cut `work/releases/0.4.0/pixeldrain-01924a1`) are byte-identical to the dist files that
were verified (SHA-256 prefixes airpit 3E3FE175D6D7, bay5 6A545D50690B, bowl E78CF626EF23, equator 8D212818C962). Key source v2 did its
job: the resource streams (`.resS`, `.resource`) are untouched by this hotfix, as by 0.6.0 -> 0.6.1. A staging build adds no wrap.

## File manifest diff (ConsistencyInfo 0.6.1 on GPU-2 against 0.6.2 on the VM)

15,352 files in both, none added or removed, 481 changed, 457 of them with the same size. Changed: every `level<N>` and
`sharedassets<N>.assets` of every location (all sizes equal), `globalgamemanagers` (+176 B), `globalgamemanagers.assets` (+16 B), `boot.config`,
`StreamingAssets/Windows/Windows.json` and `maps/arena_prison_preset.bundle`. Unchanged: every `.resS` / `.resource` stream.

Byte comparison of the five released and registered maps' level and `.assets` files (scratch `arena062_bytes.py`):

| Map | Differing bytes | Notes |
|---|---:|---|
| Equator | 209,867 | 209,324 of them in level55 (Arena_Equator_Lighting); the rest 2-386 per file |
| Bay5 | 197,906 | 197,344 of them in level83 (Arena_Bay5_Light); the rest 1-341 per file |
| Air Pit | 642 | 2-390 per file |
| Bowl | 851 | 6-390 per file |
| Iceberg | 708 | 1-254 per file (levels 256/258/375, sharedassets 255-260/375-377) |

Object-level comparison with UnityPy (raw bytes per path id, scratch `arena062_objects.py` and `arena062_pptr.py`): no object added or
removed, type trees and externals equal. What differs:

- One byte per object in `MeshFilter.m_Mesh`, `Material` texture slots and `m_Shader`, `Light.m_Cookie`, `BoxCollider`, `PreloadData.m_Assets`
  and MonoBehaviour script pointers: the path ids of objects in `resources.assets` and `sharedassets1.assets` moved by one (e.g. Iceberg's
  `MainSpot` cookie `sharedassets1.assets` 1165 -> 1166, a cubemap `_Cube` 5749 -> 5748, `resources.assets` 3343 -> 3342). Content unchanged.
- Equator level55 and Bay5 level83: exactly one object each, the Mesh `LocalDustParticlesParent GenerateMesh` (376,848 and 313,404 B, about
  two thirds of the bytes differ): a dust particle mesh generated at build time with fresh random positions.

The accepted packages are extracted content (mesh, texture and material data with their own ids), so none of this reaches a built bundle;
no map needs a re-extraction or a rebuild. A re-extraction from 0.6.2 would give different `sha1(file:path_id:type)` ids to objects read
from `resources.assets` / `sharedassets1.assets` (light cookies, cubemaps), with the same content.

## Tests against the installed 0.6.2

Full Python suite before the adjustments: 2,787 tests, 5 failures, 76 skipped. The five were the gate-sound row pins
(`test_gate_sounds.RealDataRowTests` for the four released maps, `RealIcebergRowTests`): the tracked `tools/maps/<stem>.gatesounds.json`
record `arena_version 0.6.1.0.48206` and the test compared the whole rendered text. A row diff with the version normalized (scratch
`gatesounds_diff.py`) gives 0 differing lines on all five maps. The tests now compare the rows and keep the tracked file's version as its
provenance (`as_read_from` in test_gate_sounds.py); the sidecars are not rewritten.

`test_iceberg_bake_inputs.py` skipped its Arena-dependent pins on any build but 0.6.1 (`PINNED_ARENA_BUILD`). They pass on 0.6.2 (2,473
polygons, 93 components, cover counts and the rest), so the constant now accepts `0.6.1.` and `0.6.2.`; `test_iceberg_spawns.py` (no
version gate) passed on 0.6.2 in the full run. `test_iceberg_bake_inputs`, `test_iceberg_emission_cap` and `test_gate_sounds` together:
128 tests OK.
