# PLAN-TWO-MAPS item 4 — NavMesh modifiers (Unity.AI.Navigation.NavMeshModifier / NavMeshModifierVolume)

Scope: [docs/PLAN-TWO-MAPS.md §1.4](../PLAN-TWO-MAPS.md) ("NavMesh modifiers → real collision at Bay5 pump
stations"). Branch `feature/native-dust`, worktree of this task. **Offline only — no Unity build, no
deploy, no host test.** The lead runs the shared Unity/NavMesh rebuild (§1's "Shared build step for items
4–6") and the host verification; see "What the lead must do" at the end.

## What

Extract and bake `NavMeshModifier`/`NavMeshModifierVolume`, previously dropped at all three pipeline
layers (extractor never recorded them, importer never attached them, `BakeNavMesh.Bake()` always passed
an empty markup list), so Bay5's `bruno_helix` pump-station machinery becomes genuinely solid instead of
walkable, and Equator's bake stays numerically unchanged (it has zero override instances).

## Verified facts (this pass, real Arena source, real production code path)

Re-ran the 2026-09-25 spike's typetree reader as `tools/extract/navmesh_typetree.py` (attribution kept in
its module doc comment) and exercised the **actual production functions**
(`extract_equator.build_scene_graph`/`extract_scene`, `extract_bay5`'s own scene-index resolution,
`extract_navmesh_modifiers.augment_scene`/`read_navmesh_areas`) against the real source
(`D:/Battlestate Games/Escape from Tarkov Arena/EscapeFromTarkovArena_Data`), skipping only the (unrelated
to this item, multi-GB) mesh/texture/material dedup write pass — scratch script
`.../scratchpad/item4-offline-evidence/run_navmesh_extract.py`, re-run this pass as `run3.log` (byte-identical
to the prior `run2.log`, confirming reproducibility):

| | Equator (5 content scenes) | Bay5 (9 content scenes) |
|---|---|---|
| `NavMeshModifier` total | **31** | **170** |
| — `override_area=true` | 0 | 113 (all `area=1 "Not Walkable"`) |
| — `ignore_from_build`-only | 31 | 57 |
| `NavMeshModifierVolume` total | **0** | **134** (all `area=1`) |

Per-scene breakdown (Equator: scene 51 `Arena_Equator` +11, scene 57 `Arena_Equator_TeamFight_DesignStuff`
+20, zero elsewhere; Bay5: scene 84 `Arena_Bay5_Main` +81 mod/+85 vol, scene 81 `Arena_Bay5_DesignStuff`
+29, scene 89 `Arena_Bay5_TeamFight` +8 mod/+15 vol, scene 88 `Arena_Bay5_TeamFight_DesignStuff` +20, scene
79 `Arena_Bay5_Background` +32 mod/+34 vol). Bay5's owner-GameObject histogram (override/ignoreFromBuild/
volume instances only): `bruno_helix` 112, `NavMesh Modifier Volume` 134 (the volumes' own default name),
`ExperienceTrigger` 27, `Bear house`/`Usec house` 10 each, `Swamp_collider` 8, three singletons — matching
the plan's "Bay5's bruno_helix pump-station family carries 113 area overrides + all 134
NavMeshModifierVolume instances" (113 vs. 112-by-base-name is one override sitting on a differently-named
sibling within the same prop cluster, not a discrepancy in the total).

`NavMeshProjectSettings` areas (both maps, read from Arena's bare `globalgamemanagers`, not
`globalgamemanagers.assets` — confirmed by this pass; `read_navmesh_areas()`'s own fallback path is unit
tested for exactly that split): `{0: Walkable/1.0, 1: "Not Walkable"/1.0, 2: Jump/2.0, 3: Sitdown/1.0,
4: Danger/2.0}` — numerically matches `BakeNavMesh.cs`'s hardcoded agent-0 constants already, independent
cross-check, no bake-side change needed for that part.

**Correction to this task's own briefing**: the briefing's expected "Equator 124 modifiers" figure is the
sum of Equator's **content** scenes (31, extracted above) **and** its excluded `TeamFight_AI` scene 52
(93, confirmed against the original spike's full record set, which tagged records `game`/`which` per
scene: `{('Equator','imported'):31, ('Equator','ai'):93, ('Bay5','imported'):170, ('Bay5','ai'):75}`,
totalling 369 — the plan doc's own "confirmed byte-exact against all 369 instances" figure). Scene 52 is
never part of Equator's extracted content scenes (`extract_equator.SCENE_REGISTRY` — TeamFight_AI feeds
item 8's separate patrol/core-point extraction, not this one), so **31**, not 124, is the correct expected
count for what this extractor actually records and what `BakeNavMesh.cs` actually bakes. Bay5's 170/134
matches the briefing exactly. The plan doc's own "zero override instances anywhere" claim for Equator is
independently reconfirmed here (Equator's 31 are all `ignore_from_build`-only, `area=0`, no overrides, no
volumes) — Equator's bake is numerically unaffected by this item, as designed.

`Unity.AI.Navigation.NavMeshBuildMarkup`'s `overrideGenerateLinks`/`generateLinks` fields and
`NavMeshSurface.AppendModifierVolumes`'s box-source math (world center via `TransformPoint`, world size via
the volume's own `lossyScale`, never the bake root's) were read directly from the vendored package source
this task's briefing named
(`UnityProject/Library/PackageCache/com.unity.ai.navigation@1.1.5/Runtime/NavMeshSurface.cs`, present in
the main checkout though not in this worktree's own `Library/`) to confirm `BakeNavMesh.cs`'s markup/volume
code mirrors it field-for-field rather than re-deriving it — see that file's own "PLAN-TWO-MAPS item 4"
comments for the side-by-side.

## How

**Extractor** (`tools/extract/extract_navmesh_modifiers.py`, new module + `tools/extract/navmesh_typetree.py`,
copied with attribution from the spike): resolves `Unity.AI.Navigation.NavMeshModifier`/
`NavMeshModifierVolume` MonoBehaviours via `census.resolve_script_name()` (the same MonoScript-pool
PPtr-dereference machinery `extract_ballistics.py` already uses for `BallisticCollider`), reads fields with
the hand-built typetree node lists (`check_read=True`), and appends
`{"type":"NavMeshModifier","schema_version":1,"enabled":…,"override_area":…,"area":…,
"ignore_from_build":…,"apply_to_children":…,"affected_agents":[...]}` /
`{"type":"NavMeshModifierVolume","schema_version":1,"enabled":…,"area":…,"size":[x,y,z],"center":[x,y,z],
"affected_agents":[...]}` onto the owner GameObject already in the scene JSON `extract_scene()` produced —
additive and idempotent, same contract as `extract_ballistics.augment_scene()` (a second pass adds
nothing; a conflicting pre-existing component of the same type is a hard error). `read_navmesh_areas()`
records the area index→name/cost table into `manifest["native_features"]["navmesh_areas"]`.

Hooked into both extractors' existing `--native-features`/native-feature flow with a minimal addition right
after the existing `extract_ballistics.augment_scene()` call, marked `# PLAN-TWO-MAPS item 4` (see exact
hunks below) — `extract_bay5.py` reuses `extract_equator`'s `COMPONENT_BUILDERS`/`build_components()` and
simply imports and calls the same `augment_scene`/`read_navmesh_areas` functions, proving the "augment
AFTER extraction, same as ballistics" pattern generalizes without any Bay5-specific code.

**Importer** (`UnityProject/Assets/ArenaMapPort/Editor/Import/NavMeshModifierImporter.cs`, new):
`AttachModifier`/`AttachVolume` add the real engine components and copy every extracted public property
onto them; both fail loudly (not silently downgrade) if a source component's `affected_agents` is ever
something other than the default `[-1]` ("All agent types"), since `com.unity.ai.navigation@1.1.5` exposes
no public setter for an arbitrary per-agent list (every real instance on both maps carries the default, per
the counts above, so this is a dormant safety check, not a live limitation). Wired into
`SceneAssembler.cs`'s Pass-1c switch with one `case` per type (see hunk below). `ArenaMapPort.Editor.asmdef`
now references `Unity.AI.Navigation` (verified against the vendored package's own asmdef name, confirmed
present in `UnityProject/Packages/manifest.json` at `1.1.5` already).

**Bake** (`UnityEngine/Assets/ArenaMapPort/Editor/NavMesh/BakeNavMesh.cs`): builds a `NavMeshBuildMarkup`
list from `NavMeshModifier.activeModifiers` (populated by each attached component's own `[ExecuteAlways]
OnEnable()` the moment the importer attaches it), filtered to the current bake's own `geometryRoots`
exactly like the existing source-filtering loop already does, and passes it into the existing
`NavMeshBuilder.CollectSources(...)` call (previously always `new List<NavMeshBuildMarkup>()`). Each
in-scope `NavMeshModifierVolume` becomes its own `NavMeshBuildSource` (`ModifierBox` shape), box math copied
field-for-field from `NavMeshSurface.AppendModifierVolumes` per the verified-facts section above, appended
to `sources` after the existing opened-barrier exclusion pass (so a volume can never re-admit a
round-opened/nonphysical source — it only adds new modifier-box sources, never touches the exclusion
logic). The existing `RoundOpenedReason` exclusions (`SPAWN_BOUND*`, `LevelBorder`, sliding-door leaves,
`opened_barriers` non-frame children, layers 13/29) are untouched. Logs per-bake counts: markup count
(with override/ignoreFromBuild sub-counts) and volume-source count with its area-index histogram.

## Exact hunks added to shared files (conflict discipline)

`tools/extract/extract_equator.py` — 7 new lines, all after the existing
`ballistic_contract = load_contract(...)` / `augment_scene(...)` call sites:
```python
        # PLAN-TWO-MAPS item 4: NavMesh modifier/volume components (no cross-version contract needed)
        from extract_navmesh_modifiers import augment_scene as augment_navmesh, read_navmesh_areas
...
            # PLAN-TWO-MAPS item 4
            nm_mod, nm_vol = augment_navmesh(scenes_out[idx], f)
            print(f"    restored {nm_mod} NavMeshModifier + {nm_vol} NavMeshModifierVolume component(s)", file=sys.stderr)
...
        manifest["native_features"]["navmesh_areas"] = read_navmesh_areas(env, args.source)  # PLAN-TWO-MAPS item 4
```

`tools/extract/extract_bay5.py` — same shape, 8 lines:
```python
    # PLAN-TWO-MAPS item 4: NavMesh modifier/volume components (no cross-version contract needed)
    from extract_navmesh_modifiers import augment_scene as augment_navmesh, read_navmesh_areas
...
        # PLAN-TWO-MAPS item 4
        nm_mod, nm_vol = augment_navmesh(scenes_out[idx], f)
        print(f"    restored {nm_mod} NavMeshModifier + {nm_vol} NavMeshModifierVolume component(s)", file=sys.stderr)
...
        "native_features": {"schema_version": 1, "required": ["ballistics", "harbor_audio"],
                            "navmesh_areas": read_navmesh_areas(env, args.source)},  # PLAN-TWO-MAPS item 4
```

`tools/validate/validate_extract.py` — 2 new lines in `KNOWN_COMPONENT_TYPES`:
```python
    "NavMeshModifier",  # PLAN-TWO-MAPS item 4
    "NavMeshModifierVolume",  # PLAN-TWO-MAPS item 4
```
`ENABLED_COMPONENT_TYPES` (and its `validate_bundle_roundtrip.py` duplicate) is **deliberately left
untouched**: both are keyed on the native Unity `obj.type.name` a script-resolved component never carries
(`obj.type.name == "MonoBehaviour"` for every `NavMeshModifier`/`NavMeshModifierVolume` instance, exactly as
for the existing `BallisticCollider` precedent, which is also not in either set) — the ground-truth
recompute in `count_disabled_in_arena()` only ever matches entries by native type name, so adding a
script-resolved type there would silently never match anything from the Arena side while still counting
real disabled instances on the package side, producing a spurious mismatch rather than a working check.

`UnityProject/Assets/ArenaMapPort/Editor/Import/SceneAssembler.cs` — one case block per type, both calling
the new importer class:
```csharp
                        case "NavMeshModifier":  // PLAN-TWO-MAPS item 4
                            NavMeshModifierImporter.AttachModifier(go, comp);
                            break;
                        case "NavMeshModifierVolume":  // PLAN-TWO-MAPS item 4
                            NavMeshModifierImporter.AttachVolume(go, comp);
                            break;
```

`UnityProject/Assets/ArenaMapPort/Editor/ArenaMapPort.Editor.asmdef` — one new reference:
```json
  "references": [
    "Unity.AI.Navigation"
  ],
```

`UnityProject/Assets/ArenaMapPort/Editor/NavMesh/BakeNavMesh.cs` is this item's own file (not a shared
hook) — the markup-collection loop, `CollectSources(...)` call-site update, and the volume-source loop are
substantial and are not reproduced here; see the file's own "PLAN-TWO-MAPS item 4" comments (three blocks,
~65 lines total).

## New files (this item only, no shared-file conflict)

- `tools/extract/navmesh_typetree.py` — hand-built typetree node lists, copied with attribution from the
  spike (`.../scratchpad/navmesh-spike/navmesh_typetree.py`).
- `tools/extract/extract_navmesh_modifiers.py` — the extractor module (`augment_scene`, `read_navmesh_areas`).
- `UnityProject/Assets/ArenaMapPort/Editor/Import/NavMeshModifierImporter.cs` — the importer.
- `tools/tests/test_navmesh_modifiers.py` — unit tests (synthetic fixtures) + one real-source, byte-exact
  round-trip test against a live Bay5 `level84` instance, skipped when the Arena install is unreachable.

## Offline evidence

- `python -m unittest discover -s tools/tests -p "test_*.py"` → **263 tests, OK (27 skipped)** — includes
  `test_navmesh_modifiers.py`'s 14 tests, all run (Arena source reachable on this machine, so the
  real-source test executed rather than skipped).
- `python tools/validate/validate_map_descriptors.py` → **OK**, 14/14 map descriptors, registry, sidecars
  and `ArenaPaths.cs`'s `EquatorDefault()` all agree (this item touches none of that data, expected no-op).
- `dotnet build ArenaMapPort.Client/ArenaMapPort.Client.csproj -c Release -p:ArenaMapPortDllOnly=true` →
  **0 warnings, 0 errors**.
- `dotnet build ArenaMapPort.Server/ArenaMapPort.Server.csproj -c Release` → **0 warnings, 0 errors**.
- `dotnet test ArenaMapPort.Server.Tests/ArenaMapPort.Server.Tests.csproj -c Release` → **356/356 passed**.
- Real-source extractor run (production code path, both maps, scratch output only — nothing copied into
  the accepted `work/extract/*-native-dust` packages): counts above, reproduced identically across two
  separate runs (`run2.log`, `run3.log` in this task's scratchpad).
- **Not run this pass** (correctly out of scope for an offline task): the Unity importer/bake code
  (`NavMeshModifierImporter.cs`, `BakeNavMesh.cs`'s new markup/volume blocks) — Unity Editor C# cannot be
  compiled outside the Unity Editor; verified instead by mirroring the vendored package's own source
  line-for-line (see "Verified facts" above) and by every existing pattern this file's `case`/asmdef/API
  usage follows already compiling in this project (`BallisticCollider`'s own Pass-1c case, `AddComponent<T>`
  usage in `AuthorScriptsScene.cs`).

## What the lead must do

1. Run the shared build step for items 4–6 (one Equator rebuild, one Bay5 rebuild — §1's "Shared build
   step for items 4–6" in `docs/PLAN-TWO-MAPS.md`), merging this item's changes with items 5/6's own
   parallel work first. Equator **without** `-FullMap` (native-dust package), Bay5 **with** `-FullMap`.
2. Watch the Unity batch log for `[ArenaMapPort][BakeNavMesh] NavMesh modifiers: N markup(s) in bake scope
   (…), M volume source(s) added; volume area distribution: …` on both maps — expect Equator's line to show
   `0 area-override, 31 ignoreFromBuild` and `0 volume source(s)` (bake numerically unchanged from before
   this item), Bay5's to show `113 area-override, 57 ignoreFromBuild` and `134 volume source(s)` with
   `area1=134`.
3. Confirm Equator's baked NavMeshData vertex/triangle counts (`[ArenaMapPort][BakeNavMesh] triangulation:
   vertices=… triangles=…`, already logged) are **numerically identical** to the pre-item-4 bake — the
   done criterion in the plan.
4. Host-verify Bay5: `uibridge_teleport dryRun=true` at 2–3 of the `bruno_helix` world positions the offline
   scene JSON now carries (recoverable from `work/extract/bay5-native-dust/scenes/Arena_Bay5_Main.json` /
   `Arena_Bay5_TeamFight.json` / `Arena_Bay5_Background.json` by object name, per `AGENTS.md`'s "offline,
   `work/extract/<map>/scenes/*.json` + `meshes/*.mesh.bin` give every collider's world triangles") — these
   points must now come back **off-mesh** (`reachable=false`), where before this item they were on-mesh
   (walkable, since the marking `BoxCollider` is itself a trigger and contributed no source geometry of its
   own). A live PMC/bot walk near the pump-station machinery should show it as a real obstacle.
5. Since this task ran no Unity build, the very first real compile of `NavMeshModifierImporter.cs` and
   `BakeNavMesh.cs`'s new blocks happens in the lead's own Unity batch run — check the batch log for any
   compile error before trusting the bake output.

**Done when** (plan's own criterion): Bay5's pump-station machinery blocks movement; Equator's NavMesh is
numerically identical to the pre-item-4 bake.

## Lead integration 2026-09-25 (merge, review fixes, build evidence)

Merged into `feature/native-dust` together with items 5 and 6 (one shared extraction + rebuild). Two things
the offline pass could not see surfaced in the first real Unity build (11:41) and are fixed in the same
commit:

1. **The bake-only components must not ship.** `Unity.AI.Navigation.NavMeshModifier` in the built scenes put a
   `'Unity.AI.Navigation'` MonoScript into `BuildPlayer-Arena_Equator.sharedAssets`, which
   `tools/bundle/patch_monoscript_identity.py` rejects by contract (only `Assembly-CSharp[-firstpass]` may
   ship). `NavMeshModifierImporter.StripBakeOnlyComponents()` now runs in `BuildBundles.FinishPipeline` after
   `BakeNavMesh.Bake` (which needs the live components) and before `BuildAssetBundles`: it destroys every
   `NavMeshModifier`/`NavMeshModifierVolume` under the content-scene roots and re-saves those scenes. Build
   logs: Equator `stripped bake-only components before the bundle build: 31 NavMeshModifier, 0
   NavMeshModifierVolume; re-saved 5 content scene(s)`, Bay5 `170 NavMeshModifier, 134 NavMeshModifierVolume;
   re-saved 9 content scene(s)`; `patch_monoscript_identity` then passes (`Assembly-CSharp: 13` only).
2. **Round-open exclusion vs. the layer rule.** The review asked for the collider sources' `RoundOpenedReason`
   to apply to markups too. Applied literally it silenced item 4: that function also excluded owners on layer
   13 (Triggers) and 29 (LevelBorder), and 112 of Bay5's 113 area overrides sit on Triggers-layer trigger
   boxes (the marker box is the trigger, `applyToChildren` carries "Not Walkable" onto the solid children).
   The 11:41 Equator build logged `excluded 31 nonphysical or round-open source(s): modifier:Triggers layer
   13` and `NavMesh modifiers: 0 markup(s) in bake scope`. `BakeNavMesh` now has `NonPhysicalLayerReason`
   (layers, collider sources only) separate from `RoundOpenedReason` (name prefixes, leaf names, descriptor
   `opened_barriers`; applied to sources, markups and volumes). Markups/volumes additionally honour
   `AffectsAgentType(0)` like `NavMeshSurface.CollectSources` (all real instances carry `[-1]`).
3. `validate_native_features.feature_enabled()` accepts the optional `native_features.navmesh_areas` manifest
   key (index → name/cost, must start with `0 Walkable`/`1 Not Walkable`); tests in
   `tools/tests/test_navmesh_modifiers.py` (`FeatureEnabledAcceptsNavmeshAreasKeyTests`).

Bake results (packages `work/extract/equator-native-v3` / `bay5-native-v3`, Unity logs
`work/build/full/unity_build_all_20260925_*.log`, `work/build/bay5/unity_build_all_20260925_*.log`):

| Map | Markups in bake scope | Volume sources | Triangulation (vertices/triangles) | Previous bake |
|---|---|---|---|---|
| Equator | 31 (0 area-override, 31 ignoreFromBuild) | 0 | 10404 / 4572 | 10404 / 4572 (identical; `arena_equator_navmesh.bundle` left byte-identical, mtime 2026-09-23) |
| Bay5 | 109 (81 area-override, 28 ignoreFromBuild) | 134 (`area1=134`) | 22117 / 10213 | 22190 / 10248 on every 2026-09-22 build (−73 vertices, −35 triangles) |

**Correction to the expected Bay5 numbers (113/57 in the sections above).** Those are raw package counts.
`NavMeshModifier.activeModifiers` only holds components whose `OnEnable` ran: 32 of the overrides sit under
the hierarchy-inactive `bruno_helix_combine_04 (7)`… groups of `Arena_Bay5_Main`, and 29 of the
ignoreFromBuild markers under the inactive `ExplorationZones` of `Arena_Bay5_DesignStuff`. A real
`NavMeshSurface` bake in Arena's own Editor skips exactly those, so 81/28 is the faithful number (all 134
volumes are hierarchy-active). Equator's 31 are all trigger-only `ignoreFromBuild` markers — the bake is
numerically unchanged, as the done criterion demands.

### Host evidence 2026-09-25 (EFT instances 9f0fe0dc 13:07 and 70aeac2c 14:27, SPT 4.1.6 host)

- Bay5 raids 13:09 (v2 bundles) and 14:32 (v3 bundles, byte-identical NavMesh): `[ArenaMapPort][NavMesh] added
  navmesh for 'arena_bay5': 22117 vertices, 10213 triangles` — the new bake is live (previous builds: 22190 /
  10248). Equator 14:29: `added navmesh for 'arena_equator': 10404 vertices, 4572 triangles` (unchanged).
- `uibridge_teleport dryRun=true` at area-override wire rows: (44.52, 0.45, 32.91) and the volume centre
  (44.9, 0.73, 47.19) → `no navmesh within 2 m of the point`; (26.96, 0.45, 24.32) and (71.2, −1.2, 49.0) →
  `not reachable on foot` (navmesh exists within the 2 m sampling radius but no path). A real teleport with
  `reachable=false` to (71.2, −1.2, 49.0) — the point item 2 used inside `bruno_helix (6)` — still lands with
  `snapped 0.0 m`: that wire's NavMeshModifierVolume (world y −1.04…0.04) floats 16 cm above the floor navmesh
  at y −1.2, so Arena's own box never marks the floor there (Recast marks spans whose top lies inside the box),
  and the dry-run's 2 m `SamplePosition` radius cannot resolve sub-2 m features anyway. The quantitative
  before/after is the bake delta above (−73 vertices / −35 triangles: coil tops that were walkable are not any
  more); bots were not observed pathing through wire in either raid.
- No new exceptions attributable to this item in either session.

**Status:** VERIFIED 2026-09-25 (bake numerically identical on Equator, changed as authored on Bay5, bundles
ship without the bake-only components).
