# Equator floor-detail gloss and revised lighting recommendation

## 2026-09-29 — source diagnosis and implementation

The user accepted the Filmic comparison and added ReflectionIntensity 0.85,
IndoorReflectionStrength 0.95 and AnamorphicFlareIntensity 0.40. These are saved
Equator tuning choices, not additional program-default migrations. The remaining
accepted tuning is preserved. The same user reported different gloss controls
for damaged and intact ceramic tiles.

The user's current GlobalLightIntensityMultiplier was 0.5 (the earlier comparison
used 0.6); it was retained at 0.5 throughout this task. The capture metadata records
the complete current Lighting section rather than inferring it from the old preset.

The extracted v5 source explains the split: all 512 damage/shard renderer slots
are smaller than the indoor classifier's 4m minimum. The intact structural floor
passes it. Both use the same reflective shader family, with different authored
textures/gloss/specular values. Ground scanning additionally captured the same
shared material repeatedly (478 users for gray damage), causing compounding at
non-neutral multipliers. Indoor classification also captured ground-scaled inputs.

### Repair

- Ground materials have one immutable property baseline per material instance.
  Live scaling and teardown use that baseline once. Zero is reversible without
  division. Late scans cannot adopt owned indoor clones.
- Indoor clones obtain their smoothness baseline from the original ground-cache
  input, making indoor behavior independent of the ground factor at raid start,
  later changes and reclassification.
- Equator opts into small floor-detail inheritance through its descriptor and
  matching sidecar. Bay5 retains its existing covered-surface partition.
- Eligible floor/ground/asphalt-named details must be thin (at most 0.5m), smaller
  than 4m, active, and use only floor-family non-decal shaders. Their nearly
  horizontal triangles must rest on actual accepted structural-floor triangles,
  within -0.05/+0.2m of their interpolated height. Four probes per face and 90%
  supported projected area reject detached pieces and holes; bounds are not proof.
- Highest-detail authored LOD decides each eligible LODGroup. Independent
  classification originally accepted 510/512: two simplified shard LODs near
  (1.466,0.042,39.458) only covered 88.1%, while their LOD0 passed.
- A 4m spatial index bounds normal support lookups; oversized triangles use a
  fallback list. Textures, normals, meshes, colliders, authored material ratios,
  parquet handling and the existing specular-cap layer remain intact.

At indoor factor 0.5 the authored gray damage gloss 1 becomes 0.5, while intact
gray gloss 0.93 becomes 0.465. The fix gives them the same control, not identical
physical material values. Dark tile and broken edges likewise retain authored
differences.

### Checks

Client Release build: zero warnings/errors. Full Release test suite: 403 passed,
none skipped. Existing nullable warnings occur in the test project's linked
ArenaMapRaid and BaseJsonTests. New tests cover triangle holes versus bounds,
height, winding, slopes, vertical/degenerate/non-finite rejection, unsupported
area, spatial cells/seams, large-face fallback and descriptor/sidecar scope.
Distribution validation: 14 maps, 42 bundles, 14 sidecars.

The first GPU candidate (`47f0aa43`, started 22:06:55 CEST) confirmed 14 unique
ground materials and the source-predicted 510/512 classification. Its initial
unindexed classification took 2499ms. It was superseded by shared LOD decisions
and indexed lookup before final acceptance.

### Final GPU candidate

Client SHA256 `A5B95A4D2A1DC0AEC250E64151DA4CB24C81456EC9D8CC4DFCB9E7F92B63454A`.
GPU-2 `73ab5e5e`, started 22:12:38 CEST: all 24 maschine plugins matched local,
host-disk and loaded identities. Day/Clear Equator started with GroundSmoothness
0.5 (before the ground scan). The scan found 14 unique materials (#3200,
22:13:50); all 512/512 details inherited indoor classification (#3208,
22:13:54). Total classifier elapsed time fell from 2499ms to 907ms, including
its existing roof readbacks and cloning, not a steady-frame cost.

F10 at gray damage (-10.595,0.031,16.232) observed authored indoor values despite
the non-neutral raid-start ground factor: intact gray 0.465, dark 0.65, damage
0.5. Setting ground to 0 kept those values; restoring ground to 1, setting the
Lab multiplier to 0.5 and indoor to 1 gave intact gray 0.93 / dark 1.3 / damage
1.0. Restoring indoor 0.5 and reclassifying twice (cover percent 30->31->30)
returned the exact original indoor values. Both ground multipliers ended at 1.

Dark damage (0.01,0.031,18.74) likewise has its indoor clone and gloss 0.5.
F10 at the previously inconsistent shard (1.466,0.042,39.458) showed all three
LOD0/1/2 clones with gloss 0.5. Parquet retained gloss 0.5, specularness 0.855
and reflection tint 0.475 at the user's reflection strength 0.95.
No ArenaMapPort warning/error occurred during these checks.

Unmodified captures and actual live settings/player metadata are retained at
`analysis/screenshots/equator-floor-gloss-2026-09-29/`: gray tiles, dark tiles,
center view, parquet. Source assets and these raw captures remain untracked.

Bay5 regression in the same process (22:18-22:20 CEST) found eight unique ground
materials and retained the native repair's 32 split meshes/21032 triangles.
Depot F10 compared GroundSmoothness 1->0.5: shared ground gloss 6->3, while the
owned IndoorSurface slot stayed 3. Restored ground to 1. No ArenaMapPort warning
or error was recorded. This confirms indoor/outdoor material isolation across
maps; it is not a new Bay5 aesthetic preset.

Both sanctioned deploy helpers copied 2239 files. The vanilla saved config is
byte-identical to its pre-deploy copy. Only live UiBridge actions changed GPU-2
settings; temporary ground/indoor/cover/Inspector values were restored.

A final Equator re-entry after Bay5 (#5417-5453, 22:21:07-11 CEST) restored
14 unique ground materials, 512/512 inherited details and identical structural
floor gloss values (0.65/0.465/0.5). The classifier took 883ms. The three user
settings remained exactly 0.85/0.95/0.40; no ArenaMapPort warnings/errors occurred.
Independent SHA256 verification matched all 2239 staged files on both installs.
The host finished at MainMenu with no active raid or test hooks. Four native
1920x1080 screenshots and their recorded reflection/flare/ground/indoor values
were independently checked. GPU-2 retains the user's exact tuning.
