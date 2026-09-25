# PLAN-TWO-MAPS item 5 — physics props: Rigidbody + SmallPhysicsObject (2026-09-25)

Scope: [docs/PLAN-TWO-MAPS.md](../PLAN-TWO-MAPS.md) §1.5, "Physics props — Rigidbody + SmallPhysicsObject
(both maps)". User decision 2026-09-25 (§4.1): activate by default on both maps. This pass implements the
extractor, Unity importer and client repair and proves them **offline** (real extractor runs against the
live Arena source, unit tests, `dotnet build`/`dotnet test`); **no Unity build and no host raid were run**
(forbidden for this pass — see "What the lead must do" below).

## What

Equator's 391 / Bay5's 18 movable props ("PHYSIC" containers — cups, bottles, boxes, garbage) get a real
Unity `Rigidbody` plus EFT's native `SmallPhysicsObject` class, so they react to shots and touches instead
of being static dead geometry. Equator's entire `PHYSIC` container ships hierarchy-disabled in Arena's own
data; the client activates it by default (config to disable).

## Verified facts (re-checked this pass against the live Arena source, not just the prior spike)

- **Counts, exactly as predicted**: a real extractor run (below) tags **391** `SmallPhysicsObject` owners
  and **413** `Rigidbody` components on Equator, and **18** / **22** on Bay5. The Bay5 gap (22 Rigidbody vs
  18 SmallPhysicsObject) is 4 standalone `sliver_*` glass-shard Rigidbodies in `Arena_Bay5_Main` with no
  SmallPhysicsObject parent at all (`is_kinematic: true` — inert debris, not part of any PHYSIC subtree) —
  confirmed by scene, so gating Rigidbody extraction on SmallPhysicsObject presence would have silently
  dropped them.
- **Field names**: the shipped `Rigidbody` typetree keys are `m_LinearDamping`/`m_AngularDamping`, **not**
  `m_Drag`/`m_AngularDrag` (`physics-spike/inspect_sliver.py`, `read_physics_props.py` — a prior scratch
  attempt using the wrong names silently got `drag: null` for all 409 instances then). `Rigidbody` has
  **no `m_Enabled` field at all** — confirmed again this pass by its absence from every one of the 413/22
  real typetree reads; the extractor's `build_rigidbody()` therefore records no `"enabled"` key, and
  neither `KNOWN_COMPONENT_TYPES`-adjacent set (`ENABLED_COMPONENT_TYPES`) lists it, matching the existing
  `MeshFilter`-has-none precedent.
- **`SmallPhysicsObject`'s own custom fields are unrecoverable** (a direct typetree read fails), but class
  identity + owning GameObject resolve cleanly through `analysis/scripts/census.py`'s
  `resolve_script_name()` — reused directly (not reimplemented) since its `mb_obj.parse_monobehaviour_head()`
  + `m_Script` dereference recipe already tolerates the unknown custom-field layout that a full typetree
  read chokes on.
- **`PHYSIC` is a single, root-level GameObject on each map** (`m_Father` path_id `0`), confirmed by direct
  read against the real source this pass: Equator's is `level51` path_id `1191` (`activeSelf=False`, 391
  direct children, 2193 descendant GameObjects including itself — matches the plan's "2,192 objects" other
  than the root); Bay5's is `level89` (TeamFight) path_id `234` (`activeSelf=True`, already active). No
  second `PHYSIC` GameObject exists anywhere in either map's imported scene set.
- **Every individual prop's own GameObject stays `activeSelf=True`** even on Equator — only the shared
  `PHYSIC` ancestor is off (`physics-spike/find_inactive_ancestors.py`, re-run this pass: "own GameObject
  inactive: 0" for all 391 instances, all with exactly one inactive ancestor named `PHYSIC`). This is why
  the client only ever needs to flip the one container's `activeSelf`.
- **`SmallPhysicsObject.Awake()`** (`D:/SPT41/Development/_decompiled/SmallPhysicsObject.cs:124-158`) walks
  `GetComponentsInChildren(includeInactive: true, ...)` for both `BallisticCollider`s and `Rigidbody`s, then
  calls `SmallPhysicsSystem.Instance.AddObject(this)`.
- **`SmallPhysicsSystem.Instance` is a SELF-CREATING singleton**
  (`D:/SPT41/Development/_decompiled/SmallPhysicsSystem.cs:32-51`): its getter unconditionally does
  `if (_instance == null) { var obj = new GameObject(); _instance = obj.AddComponent<SmallPhysicsSystem>(); }`
  — unlike `EnvironmentManager`/`ObservedCullingManager` (which only expose an already-set `_instance` field
  and need `EnvironmentRepairs.cs`'s own fallback-creation pattern), this class needs **no client-side
  fallback creation at all**. `Update()` only simulates props within 5 m of a registered player
  (`_minSqrDistance = 25f`) under a measured-FPS budget (`_maxUpdateObjects`), confirming the plan's own
  "not an unbounded per-frame cost" claim directly against the decompile.

## How

- **Extractor** — new module `tools/extract/extract_physics_props.py`:
  - `build_rigidbody(obj, ctx)`: reads `m_Mass, m_LinearDamping, m_AngularDamping, m_UseGravity,
    m_IsKinematic, m_Interpolate, m_Constraints, m_CollisionDetection` (back-compat defaults for every
    field; no `"enabled"` key, see above).
  - `augment_scene(scene_json, source_file)`: pattern of `extract_ballistics.augment_scene` — scans every
    `MonoBehaviour`, resolves its class via `census.resolve_script_name()`, and for each real
    `SmallPhysicsObject` owner appends `{"type":"SmallPhysicsObject","schema_version":1,"enabled":bool}`
    (idempotent, conflict-checked, orphan-checked — same invariants as the ballistics pass).
  - `extract_equator.py` hooks (minimal, marked `# PLAN-TWO-MAPS item 5`): one import line, one
    `COMPONENT_BUILDERS["Rigidbody"]` registration next to the dict's other entries, and a 4-line
    native-features augment call placed immediately after the existing ballistics `augment_scene` call
    (only under `--native-features`, no new manifest schema key — `native_features.required` stays
    `["ballistics", "ambient_audio"]`/`["ballistics","harbor_audio"]` unchanged, since that list is
    exact-compared by `validate_native_features.feature_enabled()`).
  - `extract_bay5.py` gets the same augment call (Bay5 has no `--native-features` flag; ballistics already
    runs unconditionally there, so physics props does too) plus the one import line. Bay5's
    `COMPONENT_BUILDERS["Rigidbody"]` entry is inherited automatically (it reuses `extract_equator.py`'s
    module-level dict via `EQ.build_components`/`EQ.extract_scene`, never its own copy).
- **Validator** — `tools/validate/validate_extract.py`: `KNOWN_COMPONENT_TYPES` gets `"Rigidbody"` and
  `"SmallPhysicsObject"` (separate lines, `# PLAN-TWO-MAPS item 5`). **Neither goes into
  `ENABLED_COMPONENT_TYPES`**: `Rigidbody` has no `m_Enabled` field to compare, and
  `SmallPhysicsObject`'s "enabled" comes from a MonoBehaviour class name that
  `count_disabled_in_arena()`'s live source recompute can never observe (it keys off `obj.type.name`, which
  is always `"MonoBehaviour"` for any custom class, never the class name) — adding either would compare a
  real package count against a ground truth that is always empty, exactly the reason `BallisticCollider`
  (the existing precedent) isn't in that set either. Confirmed empirically: the real Equator/Bay5
  extractions below both pass `validate_disabled_component_counts` with zero mismatches.
- **Importer** — new `UnityProject/Assets/ArenaMapPort/Editor/Import/PhysicsPropImporter.cs`:
  - `AttachRigidbody(go, comp)`: `SceneAssembler` Pass-1c, mirrors `AttachBoxCollider`'s simplicity (direct
    field sets, enums cast from the recorded ints — `RigidbodyInterpolation`, `RigidbodyConstraints`,
    `CollisionDetectionMode` — no `ReadEnabled` call, since the record never has one).
  - `AttachSmallPhysicsObject(owner, comp)`: `SceneAssembler` Pass 2 (after every
    Collider/Rigidbody/BallisticCollider on the subtree exists — `SmallPhysicsObject.Awake()` walks them at
    `AddComponent` time), mirrors `BallisticColliderImporter.Attach`'s validation shape (schema/type check,
    `Text`/`Int`/`Bool` JSON helpers, throws with context on failure, cleans up on exception). Checks
    `owner.GetComponentInChildren<Rigidbody>(true)` — `includeInactive: true` is required here: Pass 1a
    already applied every object's authored active state before Pass 2 runs, and Equator's `PHYSIC`
    ancestor is inactive at that point even though each prop's own `activeSelf` is true (see "Verified
    facts" above), so a default-`false` search would spuriously fail this check on every Equator prop.
  - `SceneAssembler.cs`: one `case "Rigidbody":` block in the Pass-1c switch (calls
    `PhysicsPropImporter.AttachRigidbody`), one `case "SmallPhysicsObject": break;` deferring to Pass 2, and
    one `else if` in the Pass-2 loop calling `PhysicsPropImporter.AttachSmallPhysicsObject` — three small,
    isolated additions, no reformatting of anything else in that shared file.
- **Client** — new `ArenaMapPort.Client/PhysicsPropRepairs.cs` (pattern of `BarbedWireRepairs.cs`):
  `Config.Bind("Physics", "EnablePhysicsProps", true, ...)`; `Apply()` runs from the same raid-start postfix
  chain (`SpawnInfiltrationRepairs.cs`, right after `BarbedWireRepairs.Apply()`), finds every root-level
  GameObject named `"PHYSIC"` in owned scenes and `SetActive(want)` (idempotent — a repeat
  `CreateFromScene` call re-applies the same target state, a no-op the second time), logs a single line with
  the container count and the `SmallPhysicsObject`/`Rigidbody` counts found under it.
  `HarmonyPatches.cs` gets one `PhysicsPropRepairs.BindConfig();` line next to `BarbedWireRepairs.BindConfig()`.
  No fallback singleton creation was added — `SmallPhysicsSystem.Instance` self-creates (see "Verified
  facts" above), so none is needed.

## Offline evidence

### Unit tests (`tools/tests/test_physics_props.py`, 15 new tests)

`RigidbodyBuilderTests`: every verified field maps correctly, the record never carries an `"enabled"` key,
back-compat defaults apply when a field is absent, booleans survive as real Python `bool`.
`AugmentSceneTests`: presence-marker tagging, disabled-state recording, idempotent second pass (adds zero,
scene byte-identical), conflicting-duplicate rejection, unrelated/null-script MonoBehaviours ignored,
namespaced-class still matches by short name, missing/duplicate/cross-file owner rejection, orphaned-marker
rejection, duplicate scene-object-id rejection.

```
$ python -m unittest discover -s tools/tests -p "test_*.py"
Ran 264 tests in ~10s
OK (skipped=27)
```

`python tools/validate/validate_map_descriptors.py` — unaffected (no descriptor/sidecar data touched by
this item): `OK — 14 tools/maps descriptors, all 14 runtime registry entries ... agree.`

### .NET (client Release build, server Release build + xunit)

```
dotnet build ArenaMapPort.Client/ArenaMapPort.Client.csproj -c Release -p:ArenaMapPortDllOnly=true
  → 0 Warnung(en), 0 Fehler

dotnet build ArenaMapPort.Server/ArenaMapPort.Server.csproj -c Release
  → 0 Warnung(en), 0 Fehler

dotnet test ArenaMapPort.Server.Tests/ArenaMapPort.Server.Tests.csproj -c Release
  → Bestanden!: Fehler: 0, erfolgreich: 356, übersprungen: 0, gesamt: 356
```

No Shared/Server/sidecar data changed by this item, so no new mirror test was needed there (per the task's
own "if you add descriptor/sidecar data" condition — this item adds neither).

### Real extractor runs against the live Arena source (scratch output, not committed)

Equator, all 5 content scenes, `--native-features`:

```
$ python tools/extract/extract_equator.py --source "D:/Battlestate Games/Escape from Tarkov Arena/EscapeFromTarkovArena_Data" \
    --target "D:/SPT41/EscapeFromTarkov_Data" --scenes all5 --native-features --output <scratch>
  scene 51 (Arena_Equator): ... restored 2639 original ballistic material components
                             tagged 391 SmallPhysicsObject owners
  scene 53/55/57/58: tagged 0 (PHYSIC only exists in the main scene)
DONE. 39398 objects, 2605 meshes, 332 materials, 708 textures. 0 errors, 219 warnings.
```

Counted directly from the written `scenes/Arena_Equator.json`:

- **Rigidbody: 413**, **SmallPhysicsObject: 391** — exactly the plan's predicted counts.
- Sample record (object id `4387`, `fire_extinguisher_dismounted_Physic`):
  `Rigidbody {mass: 6.3, linear_damping: 0.0, angular_damping: 0.05, use_gravity: true, is_kinematic: false,
  interpolate: 0, constraints: 0, collision_detection: 0}` and
  `SmallPhysicsObject {schema_version: 1, enabled: true}` on the same owner.

`validate_extract.py --package <scratch> --skip-reproducibility`: **`OK — all checks passed`** (2
non-blocking warnings, both pre-existing and unrelated — 219 "no mesh assigned" MeshFilters + 21 orphaned
mesh ids, identical categories seen on every prior Equator extraction). Disabled-component ground truth
recomputed fresh from Arena's own `level<N>` files still matches exactly
(`{'MeshCollider': 25, 'MeshRenderer': 4}` etc., unchanged by this item, confirming
`ENABLED_COMPONENT_TYPES` was correctly left untouched).

Bay5, all 9 content scenes (its extractor always runs the native-features passes, no flag):

```
$ python tools/extract/extract_bay5.py --output <scratch>
  scene 89 (Arena_Bay5_TeamFight): ... restored 429 original ballistic material components
                                    tagged 18 SmallPhysicsObject owners
  every other scene: tagged 0
DONE. 59422 objects, 3911 meshes, 453 materials, 945 textures. 0 errors, 188 warnings.
```

Counted per-scene:

| scene | Rigidbody | SmallPhysicsObject |
|---|---|---|
| Arena_Bay5_Main | 4 (`sliver_*` glass shards, kinematic, no SmallPhysicsObject parent) | 0 |
| Arena_Bay5_TeamFight | 18 | 18 |
| every other scene | 0 | 0 |
| **total** | **22** | **18** |

Exactly the plan's predicted counts. Sample record (`package_garbage3_Physic`, Arena_Bay5_TeamFight):
`SmallPhysicsObject {schema_version: 1, enabled: true}`. `validate_extract.py` on this package: **`OK — all
checks passed`** (4 non-blocking warnings, all pre-existing — the same MeshFilter/mesh-dedup/cubemap
categories plus the known `Land_Cruiser_close` LODGroup warning, unrelated to this item).

## What the lead must do

1. **Rebuild both maps' bundles** (this pass touched the Unity Editor importer and cannot run Unity itself):
   ```
   pwsh tools/build_all.ps1 -MapKey arena_equator -MapDescriptor D:/ArenaMapPort/tools/maps/equator.json \
     -PackageDir D:/ArenaMapPort/work/extract/equator-native-dust -OutputDir D:/ArenaMapPort/work/build/full \
     -BakeNavMesh $true -Verify $true -SkipGameRefsSync
   pwsh tools/build_all.ps1 -MapKey arena_bay5 -MapDescriptor D:/ArenaMapPort/tools/maps/bay5.json \
     -PackageDir D:/ArenaMapPort/work/extract/bay5-native-dust -OutputDir D:/ArenaMapPort/work/build/bay5 \
     -FullMap -BakeNavMesh $true -Verify $true -SkipGameRefsSync
   ```
   (Equator without `-FullMap` per AGENTS.md; combine with items 4/6's own Unity changes first if they land
   in the same rebuild per PLAN-TWO-MAPS.md §1's "shared build step for items 4–6".) Watch the Unity log for
   `PhysicsPropImporter`/`SceneAssembler` warnings during the Equator/Bay5 scene passes — none are expected,
   but this is the first time these two component types go through a real Editor `AddComponent` call.
2. **Client Release build** (without `DllOnly`) to stage the new bundles into `dist/`, then
   `deploy-to-spt.ps1 -Confirm`.
3. **Host-test recipe**:
   - Bay5: EQUATOR/BAY5 DEBUG raid, `uibridge_teleport` near `plasticCup_2_Physic (14)` at
     `[143.78, -2.89, 53.66]` (a known Bay5 prop from the spike census), shoot and walk into it, confirm it
     reacts (moves/falls) instead of staying static.
   - Equator: confirm the raid-start log line from `PhysicsPropRepairs.Apply()`
     (`[ArenaMapPort][RaidFix] physics props: activated 1 PHYSIC container(s) ... N SmallPhysicsObject / M
     Rigidbody component(s) found under them`), expect the container counts to read 391/413 — a log-only
     regression check that the new bundle's PHYSIC subtree round-tripped through the whole pipeline intact.
   - Shoot/walk into a compound prop (`Cactus_4`, 6 fragments/Rigidbodies) — check for a visible pop/clip on
     wake and a flat FPS reading (the plan's own concern re: several rigidbodies waking in one call).
   - `ForceEnableEquatorProps`-style A/B is not needed — the default is already "on"; toggle
     `[Physics] EnablePhysicsProps=false` and confirm props go static and the log reports "deactivated" to
     prove the config path both ways.
4. **Done criterion**: props on both maps react to shots/touches by default; no clipping, no props sinking
   through shelves at raid start, no FPS drop observed (per PLAN-TWO-MAPS.md §1.5's own "Done when").

## Exact hunks in shared files (for the lead's merge with items 4/6)

- `tools/extract/extract_equator.py`: one import line (`import extract_physics_props as physics_props`),
  one `COMPONENT_BUILDERS["Rigidbody"] = ...` dict entry, and a 4-line `if args.native_features:` block
  immediately after the existing ballistics `augment_scene(...)` call in `main()`'s per-scene loop. Nothing
  else in this file was touched or reformatted.
- `tools/extract/extract_bay5.py`: one import line and a 2-line augment call immediately after its own
  ballistics `augment_scene(...)` call. Nothing else touched.
- `tools/validate/validate_extract.py`: two new lines inside `KNOWN_COMPONENT_TYPES` (`"Rigidbody"`,
  `"SmallPhysicsObject"`) plus a comment explaining why neither goes into `ENABLED_COMPONENT_TYPES`. Nothing
  else touched.
- `UnityProject/Assets/ArenaMapPort/Editor/Import/SceneAssembler.cs`: two new `case` entries in the Pass-1c
  switch (`"Rigidbody"`, `"SmallPhysicsObject"`) and one new `else if` branch in the Pass-2 loop. Nothing
  else touched or reformatted.
