# ArenaMapPort — Implementation Specification (Spike)

Status: architecture for the spike defined in the project brief — **geometry + materials + colliders
+ lights + LODGroups + NavMesh + minimal EFT scene components + player spawn, on `Arena_Equator` /
TeamFight (`arena_equator_tdm_02` preset), registered as a genuinely new SPT 4.1.5 location id
`arena_equator`.** Out of scope for the spike: loot, doors, audio/acoustics, weather/TOD parity,
PerfectCulling bake, bot cover data, ArenaMode integration, Fika. This document is the contract
between work packages; implementer agents should not need to re-derive facts already established
here — file:line citations point at the evidence.

Evidence base (read before questioning a decision here): `analysis/spt-location-contract.md`,
`analysis/critic.md` (supersedes §5.2/§5.3 of the contract doc — **do not ride `suburbs`/`town`**,
register a new id per `analysis/critic.md` §1), `analysis/arena-census.md`, `analysis/ref-manimal.md`,
`analysis/ref-interchange.md` + its four verify-passes, and the raw JSON census files. All client
citations below are `file:line` in `D:/SPT41/Development/_decompiled` unless stated otherwise; all
server citations are against the **live, deployed 4.1.5** server (`analysis/decompiled-server-415/`),
not the 4.1.0 NuGet reference package that `spt-location-contract.md` originally decompiled — the two
are byte-different (critic.md §1) but agree on every mechanism cited here except the location-registry
closedness question, which critic.md corrects.

---

## 0. Identity constants (WP5 owns; never change after first deploy)

| Constant | Value | Note |
|---|---|---|
| Location key (`LocationTable` dictionary key, `base.json Id`, folder-safe) | `arena_equator` | Fixed decision #2. Lowercase, matches `GetMappedKey` convention (`analysis/decompiled-server-415/...LocationTable.decompiled.cs:172-179`). |
| Location `IdField` (MongoId, 24 lowercase hex) | `e9b8e12c60dfd2820e949462` | Placeholder generated for this spec (`sha1("arenamapport:arena_equator:v1")[:24]`) — valid MongoId shape, globally unlikely to collide. WP5 mints it once in `ArenaMapPort.Shared/ArenaEquatorLocation.cs` as a `const string` and never regenerates it (profiles/insurance would desync — see ArenaMode's own `MongoId` trap in its `CLAUDE.md`). |
| Preset bundle key (served by our `BundlesManager` hook, WP4) | `maps/arena_equator_preset.bundle` | Mirrors the shape of real bundle keys (`maps/develop_preset.bundle`) so it never collides with StreamingAssets. |
| Preset asset name (`base.json Scene.rcid`) | `arena_equator.scenespreset.asset` | Lowercase, matches BSG's own `develop.scenespreset.asset` convention (confirmed live, see WP3). |
| Scene-content bundle key | `arena_equator/arena_equator_scenes.bundle` | One bundle, all scenes (Icebreaker pattern, `ref-manimal.md` §2 "Build ONE scene bundle"). |
| NavMesh bundle key | `arena_equator/arena_equator_navmesh.bundle` | Separate bundle, one `NavMeshData` asset, loaded by type not name (`ref-interchange.md` §5, `NavMeshExtension.cs:68-89`). |
| Client plugin assembly | `maschine-ArenaMapPort.Client` | Matches sibling project's naming (`ArenaMode.Client.csproj:7`). |
| Server mod assembly / SPT mod folder | `maschine-ArenaMapPort.Server` / `user/mods/ArenaMapPort/` | Matches `ArenaMode.Server.csproj:11,53-60`. |
| BepInEx plugin folder | `BepInEx/plugins/ArenaMapPort/` | |

---

## A. Directory layout and file ownership

```
D:/ArenaMapPort/
├── docs/
│   ├── SPEC.md                          this file (Architect)
│   └── RESPAWN-API.md                   (unrelated project, pre-existing)
├── analysis/                            READ-ONLY fact base (all WPs read; nobody writes here except
│                                         re-running an already-documented script)
├── work/                                gitignored. WP1 output lives here.
│   └── extract/equator/                 the intermediate package (§C) — WP1 owns, WP2 reads
│       ├── manifest.json
│       ├── meshes/<mesh_id>.mesh.bin
│       ├── textures/<tex_id>.png
│       ├── materials/<mat_id>.json
│       ├── scenes/<scene_name>.json     hierarchy + component data, one file per scene
│       └── navmesh_ref/equator_source_navmesh.json   Arena's own bake settings, REFERENCE ONLY (§C.8)
├── UnityProject/                        Unity 2022.3.43f1 project. WP2 owns everything under Assets/.
│   ├── Assets/
│   │   ├── ArenaMapPort/
│   │   │   ├── Editor/                  tracked in git — WP2's C# editor tooling
│   │   │   │   ├── Import/              intermediate-package → Unity asset importer
│   │   │   │   ├── Shaders/             stand-in shader authoring (§F.3, WP2)
│   │   │   │   ├── NavMesh/             bake driver
│   │   │   │   ├── BundleBuild/         AssetBundle build driver (scenes + navmesh)
│   │   │   │   └── GameRefs/            GITIGNORED. Editor-only Plugin DLL copies of
│   │   │   │                            Assembly-CSharp.dll + deps, synced from
│   │   │   │                            D:/SPT41/EscapeFromTarkov_Data/Managed by
│   │   │   │                            tools/sync-game-refs.ps1 (WP2, §D). Never committed
│   │   │   │                            (BSG-derived, decision #9) — regenerate locally.
│   │   │   └── StandinShaders/          tracked — .shader files with real BSG names/properties
│   │   └── ArenaImport/                 GITIGNORED (repo convention already in .gitignore).
│   │                                    Generated meshes/materials/textures/scenes — WP2 writes,
│   │                                    nobody hand-edits, safe to delete+regenerate from work/.
│   ├── Packages/manifest.json           WP2 adds `com.unity.ai.navigation` (§B.WP2.5)
│   └── ProjectSettings/                 tracked (already exists); WP2 may touch Physics/Tags only if
│                                        an Arena layer/tag is missing (unlikely — §B.WP1 edge cases)
├── ArenaMapPort.Shared/                 NEW, WP5 (+ WP4 links the same files, ArenaMode convention:
│                                        ArenaMode.Client.csproj:19-26 `<Compile Include="..\...Shared\...">`)
│   └── ArenaEquatorLocation.cs          Key/Id constants (§0), shared by client+server
├── ArenaMapPort.Client/                 NEW, WP4. netstandard2.1 (see §F.0 for why, not net472)
├── ArenaMapPort.Server/                 NEW, WP5. net9.0 (SPT 4.1.5 targets net9.0 — see §B.WP5 note)
├── tools/
│   ├── extract/                         WP1's python scripts (extends analysis/scripts/*)
│   ├── sync-game-refs.ps1               WP2 — copies read-only Managed DLLs into GameRefs/
│   └── validate/                        WP6's python validators
├── dist/                                GITIGNORED (already). Mirrors deployment layout:
│   ├── BepInEx/plugins/ArenaMapPort/    client build output + bundles land here
│   └── SPT_Runtime/user/mods/ArenaMapPort/   server build output + db/ lands here
├── THIRD-PARTY-NOTICES.md               NEW, WP4/WP5 jointly — MIT attribution for reused
│                                        Icebreaker/InterchangeRework code (§B, per-WP notes)
└── .gitignore                           already covers work/, dist/, UnityProject/Assets/ArenaImport/;
                                         ADD: UnityProject/Assets/ArenaMapPort/Editor/GameRefs/
```

**File ownership rule**: a WP may *read* any other WP's output once that WP's acceptance criteria are
met; it may only *write* inside its own listed paths. `analysis/` is read-only for every WP (it is the
fact base, not a work product).

---

## B. Work packages

Dependency graph: `WP1 → WP2 → WP3` (WP3 only needs WP2's scene *names*, not its output, so it can
start in parallel once §E's scene list is frozen) `; WP2 → WP4` (client needs built bundles to test
against, but its Harmony hooks can be written against decompiled evidence alone, unblocked) `; WP1
concept → WP5` (server mod needs only the identity constants + §E's `base.json`, not WP1's actual
extraction) `; WP6 depends on all of WP1–WP5 for its respective checks`. In short: **WP1 and WP5 can
start immediately and in parallel; WP2 starts once WP1 produces its first real output; WP3 and WP4 can
be scaffolded immediately against this spec and wired up once WP2/WP3 produce real bundles; WP6 grows
incrementally alongside all of them (S1 checks exist before S2 work starts).**

### WP1 — Extractor (Python / UnityPy)

**Owns**: `tools/extract/*.py`, writes `work/extract/equator/**` (gitignored).

**Inputs**: `X:/Escape from Tarkov Arena/EscapeFromTarkovArena_Data/` (read-only), specifically the 6
scenes selected in §E.1 (`level51`+`sharedassets51`, `level53`+`sharedassets53`, `level55`+`sharedassets55`,
`level57`+`sharedassets57`, `level58`+`sharedassets58`) plus the ~24 shared-asset pool files enumerated in
`analysis/scene_externals.json` (meshes/materials/textures referenced cross-scene — `arena-census.md`
§2.2's "16–21 extra externals" finding: a renderer in `level51` can point at a `Mesh`/`Material` that
physically lives in `sharedassets{2,3,13,14,17,20,28,29,31,34,36,38,41,46,47,49}.assets` etc., not in
`sharedassets51.assets` itself). **No scene named `Arena_Equator_TeamFight_AI` or `*_Sound` or
`*_TeamFight_Culling` is read** — §E.1 drops them (AI/NavMesh is re-baked fresh in WP2 per decision #6,
not ported; Sound is out of scope; Culling is PerfectCulling data, out of scope).

**Reuses directly**: `analysis/scripts/census.py`'s `resolve_script_name()` (MonoScript PPtr resolution
across files — the naive path-id-dict approach silently fails, `arena-census.md` Method note 2),
`is_scene_file()`'s exact-full-path matching (the lazy-duplicate-file pitfall, Method note 3 — **this
is the single most important reuse**: a raw `env.files.items()` iteration or a basename-keyed dict WILL
double-count or crash once a `Texture2D.image`/cross-file `PPtr.deref()` call lazily re-registers a
second copy of an already-loaded `.assets` file), and `world_transform_of_transform()` for anchor
world-position composition.

**Algorithm** (per scene, then a global dedup pass):
1. Load the scene's own `level<N>` + `sharedassets<N>.assets` **plus every external it references**
   (`scene_externals.json`) into one `UnityPy.Environment`, using `common.py`'s exact-path convention.
2. **Meshes**: for every `Mesh` object reachable from a `MeshFilter`/`MeshCollider`/`SkinnedMeshRenderer`
   in one of the 6 scenes (including ones that live in a shared-pool file — dereference, don't skip),
   **first read `m_MeshCompression` from the mesh's typetree and assert it is `0` (Off)** — reused
   `census.py`/`common.py` code reads geometry via the raw, uncompressed `m_VertexData` field only
   (`census.py:156-159`'s `t.get("m_VertexData", {})`, no `m_CompressedMesh` fallback), and neither
   `arena-census.md` nor the JSON census outputs record this field at all; the one raw typetree sampled in
   the census (`palm_plant_01_destroyed_LOD2`, `arena-census.md:237-241`) happens to show
   `m_MeshCompression == 0`, but that is one sample out of Equator's 1,441 meshes, not a project-wide
   guarantee. Treat `m_MeshCompression != 0` exactly like the `UNRESOLVED` shader case below — a **hard
   extractor failure** with the mesh id logged, not a silently wrong/empty mesh — since §C.2's binary
   layout is written entirely in plain `float32` arrays and has no compressed-mesh decode path. Once
   confirmed uncompressed, emit one `meshes/<mesh_id>.mesh.bin` (binary layout §C.2) + one manifest entry.
   `mesh_id` = a stable hash of `(source_file_basename, path_id)` — **not** just `path_id`, since path_ids
   collide across files (confirmed pitfall, `arena-census.md` Method note 2/3).
3. **Textures**: every `Texture2D` reachable from a `Material` used by an in-scope renderer → decode via
   `.image` (Pillow), save as PNG (lossless — no re-compression artifacts to debug later; WP2 recompresses
   to the target format at import time if needed). Preserve `m_ColorSpace`/sRGB flag and whether the
   texture's usage implies a normal map (heuristic: property name is `_BumpMap`/`_NormalMap`/ends in `_N`/
   `_normal`, cross-checked against `arena-census.md` §6's shader prop samples) — WP2 needs this to set
   `TextureImporter.textureType = NormalMap` and `sRGBTexture = false` correctly, since a flipped flag
   here is a classic silent-wrong-lighting bug. All 5 formats already round-tripped 100% in the census
   (`arena-census.md` §5) — no format-specific special-casing needed beyond what UnityPy does.
4. **Materials**: for every `Material` used by an in-scope renderer, emit `materials/<mat_id>.json`
   (schema §C.4) — shader name (dereferenced via `m_Shader.read_typetree()`, matching `census.py`'s
   `material_stats()` approach), full `m_SavedProperties` (TexEnvs incl. `_ST` tiling/offset, Floats,
   Colors), `m_ShaderKeywords` (space-split string) + `m_ValidKeywords`, and `m_CustomRenderQueue`
   (`-1` = shader default). **Edge case**: `arena-census.md` §6 confirms 0 unresolved shaders for Equator
   — if the extractor ever hits `UNRESOLVED(...)` (the placeholder `census.py`'s `material_stats` emits
   on a dereference failure), that is a **hard extractor failure**, not a soft warning — halt and report
   the material id; do not silently emit a placeholder shader name into the package, since WP2's importer
   has no way to recover a shader name once lost.
5. **Hierarchy + components**: walk `scene.GetRootGameObjects()`-equivalent (UnityPy: every `Transform`
   in the scene file with no in-scene parent — a cross-scene parent, if any, roots at that object
   locally, logged as a warning; not expected in practice since Arena's own scenes are self-contained
   per role). Emit `scenes/<scene_name>.json` (schema §C.5/§C.6): one node per GameObject with local
   TRS, and per-node component records for `MeshFilter`, `MeshRenderer` (+ shadow/probe settings),
   `SkinnedMeshRenderer` (recorded but flagged `skip_import: true` — see edge case below),
   `MeshCollider` (+`convex`, +`m_Material` PPtr → dereferenced `PhysicMaterial` values), `BoxCollider`/
   `SphereCollider`/`CapsuleCollider` (+`isTrigger`), `Light`, `LODGroup`, `ReflectionProbe` (recorded,
   flagged `skip_import: true` — §C.6, out of spike scope per decision #8's "geometry, materials,
   colliders... lights... LODGroups" list not mentioning probes; Arena has only 1 anyway).
6. **Anchors**: reuse `spawn_zone_anchors()` verbatim for `SpawnPointMarker`/`BotZone`/`AICorePoint`/
   `PatrolPoint`/`AIMinePoint`/`AdditionalNavmeshBlock` world positions — **written to the package for
   human reference only** (`manifest.json`'s `anchors_reference` block). **Per `critic.md` §2c, these are
   NOT used for import**: Arena's custom MonoBehaviour field data (Category/Side/Id/BotZoneName on a
   `SpawnPointMarker`, `PatrolWays` on a `BotZone`) is unrecoverable via UnityPy on shipped Player data —
   only class name + world position survive. WP2 authors its own fresh `SpawnPointMarker`(s) per §D/§E,
   using these positions only as a sanity check that a chosen author-time position falls within the
   playable area (not a scripted import).
7. **Dedup**: a mesh/material/texture referenced from multiple scenes must be emitted exactly once and
   referenced by id from every scene that uses it — do not duplicate the payload. Build the id→content
   map across all 6 scenes' passes before writing `manifest.json`.

**Edge cases**:
- **SkinnedMeshRenderer** (506 in Equator's full 9-scene set, likely far fewer in the 5 in-scope scenes
  since most are character-rig armatures from the shared char-asset pool, `arena-census.md` §2.1/§4):
  record the component (bind pose, bones-by-name) but mark `skip_import: true` in the package — WP2 does
  **not** attempt to reconstruct skeletal skinning for the spike (decision #8 doesn't list skinned meshes;
  static-mesh-only import is the pragmatic cut). If a skinned mesh turns out to be load-bearing background
  geometry (e.g. a door or prop rigged for a simple animation), WP2 imports it as a **static** mesh using
  the bind pose instead of failing the whole object.
- **Vertex-color / multi-UV meshes** (§ arena-census.md §4's channel-signature table): the binary layout
  (§C.2) must carry every channel Arena's meshes actually use (position, normal, tangent, color, uv0–uv7)
  — do not truncate to position/normal/uv0 just because it's the common case; the 887-count "standard"
  signature is a majority, not 100%.
- **`UNRESOLVED` shader** (see step 4): hard failure, not soft.
- **A mesh/collider whose owning `.assets` file is a shared pool outside the explicit `scene_externals.json`
  list** (possible if that list, gathered from `level<N>.m_Externals` header data, misses a *second-order*
  external — a shared-pool file that itself references a third file): catch the `PPtr` dereference
  failure, log the missing file path, and re-run with it added — do not silently drop the object.
- **A `Texture2D` UnityPy cannot decode** (none observed in the census, §5, but not proven impossible for
  the full in-scope object set): record the failure in `manifest.json`'s `errors` array and emit a flat
  magenta 64×64 PNG placeholder rather than halting the whole extraction — WP6's validator (§B.WP6) must
  fail the build if `errors` is non-empty, but a single extraction *run* should still produce a complete,
  inspectable package.

**Outputs**: `work/extract/equator/manifest.json` (top-level index, schema §C.1) +
`meshes/*.mesh.bin` + `textures/*.png` + `materials/*.json` + `scenes/*.json`.

**Acceptance criteria**:
- `manifest.json.errors == []` for a full run over the 5 content scenes (Scripts is WP2-authored, not
  extracted — see §E.1).
- Object-count cross-check: for each of the 5 scenes, `len(scenes/<scene>.json.objects)` with a
  `MeshRenderer` component equals the `MeshRenderer` count `arena-census.md`'s per-file breakdown would
  predict for that `level<N>` restricted to that scene's own root objects (the per-file breakdown itself
  mixes multiple logical "scenes" per file in a few cases — use this as a sanity order-of-magnitude check,
  not an exact assertion, and document the actual numbers in the package for WP6).
- Every `material_id` referenced by a scene's renderers resolves to a file under `materials/`; every
  `mesh_id` referenced resolves under `meshes/`; every texture referenced by a material resolves under
  `textures/` — a broken reference is an extractor bug, not a downstream problem to paper over.
- Re-running the extractor twice over unchanged source data produces byte-identical `manifest.json` (ids
  are content-hash-derived, not run-order-derived) — required for WP6's reproducibility check (§G.S1).

---

### WP2 — Unity importer + stand-in shaders + NavMesh bake + bundle build (C# Editor)

**Owns**: `UnityProject/Assets/ArenaMapPort/Editor/**` (tracked), `UnityProject/Assets/ArenaMapPort/StandinShaders/**`
(tracked), `UnityProject/Assets/ArenaImport/**` (generated, gitignored), `tools/sync-game-refs.ps1`.

**Inputs**: WP1's `work/extract/equator/**` package; `D:/SPT41/EscapeFromTarkov_Data/Managed/*.dll`
(read-only, for `GameRefs/`); `D:/SPT41/EscapeFromTarkov_Data/globalgamemanagers` (read-only, for the
exact 32-slot layer table and NavMesh agent-0 settings — already dumped to `analysis/spt_gm.json`, no
need to re-read the live file).

**B.WP2.1 — Editor game-type references (resolves `critic.md` §2d's open mechanism question)**

Unlike Icebreaker/Labs (targeting a different/obfuscated live retail client, `ref-manimal.md` §3's "SDK
project is an external prerequisite, not in-repo"), **this project's Unity Editor is the exact same
version (2022.3.43f1) as the target Mono client**, and SPT 4.1 does not modify `Assembly-CSharp.dll`
itself (only BepInEx-side Harmony patches at runtime — confirmed by `ArenaMode.Client.csproj:81-82`
referencing `Assembly-CSharp`/`Assembly-CSharp-firstpass` straight from the live install, no shim). This
means **typetree-exact stub generation is unnecessary and strictly worse** than referencing the real
types: `tools/sync-game-refs.ps1` copies `Assembly-CSharp.dll`, `Assembly-CSharp-firstpass.dll`,
`Comfort.dll`, `Comfort.Unity.dll`, `AnimationSystem.Types.dll`, `ItemComponent.Types.dll`,
`ItemTemplate.Types.dll` from `D:/SPT41/EscapeFromTarkov_Data/Managed` into
`Assets/ArenaMapPort/Editor/GameRefs/` (must be physically inside `Assets/` — Unity's AssetBundle build
pipeline only serializes `MonoScript` references for types it can resolve through its own asset
database, which requires the DLL to be imported as a **Plugin asset**, not merely referenced via an
external `.asmdef` path). Each DLL's `.meta` (via `PluginImporter`, set by a one-time editor script) is
configured **Editor-only** (`SetCompatibleWithEditor(true)`, every other platform `false`) — the working
assumption is that this is irrelevant since this project only ever builds AssetBundles, never a Player,
but **that assumption is asserted here, not yet tested**: no citation or precedent for "Editor-only-plugin
MonoBehaviour serialized into a `StandaloneWindows64`-targeted AssetBundle" appears in either
`ref-manimal.md` or `ref-interchange.md` (both reference implementations solved this differently, via
typetree-stub cloning). §G.S1's smoke test explicitly verifies this before the full 6-scene build is
attempted — if a GameRefs-sourced component's `m_Script`/field data does not survive the round trip, mark
the DLLs `Standalone`-compatible instead of Editor-only and re-test. **Never commit `GameRefs/*.dll`**
(BSG-derived, decision #9) — `tools/sync-game-refs.ps1` is the reproducible setup step every fresh
checkout runs once.

With these in place, `LocationScene`, `BotZone`, `LevelSettings`, `LocationExportInfo`,
`EFT.Game.Spawning.SpawnPointMarker` (namespace matters — do not confuse with any editor-local type of
similar name) are ordinary `AddComponent<T>()`-able MonoBehaviours in editor scripts, with **perfect**
serialized-layout fidelity by construction (it is literally the class the target client deserializes
with) — see §D for the exact fields each needs authored.

**B.WP2.2 — Mesh import**: `Editor/Import/MeshImporter.cs` reads `meshes/<id>.mesh.bin` (§C.2) and
builds a `UnityEngine.Mesh` via, **in this exact order**: `mesh.SetVertices` (positions) →
**`mesh.subMeshCount = header.submesh_count;`** (must be set before any per-submesh call — a fresh
`Mesh` defaults to `subMeshCount == 1`, and `SetIndices`/`SetTriangles` throw
`ArgumentOutOfRangeException` the instant a submesh index `>= subMeshCount` is addressed; 241 of
Equator's 1,441 meshes (16.7%, histogram `{1:1200, 2:135, 3:36, 4:31, 5:14, 6:15, 7:3, 9:7}`,
`arena-census.md` §4) have `submesh_count > 1` and would hit this immediately otherwise) → `SetNormals`/
`SetTangents`/`SetColors`/`SetUVs(channel, ...)` → `SetIndices(..., MeshTopology.Triangles, submesh)` per
submesh — **do not** use the legacy `mesh.vertices =`/`mesh.triangles =` setters for anything with >65535
indices (confirmed 100% `UInt16` index format in the census, so this specific project doesn't hit it, but
write it correctly anyway: `mesh.indexFormat = IndexFormat.UInt16` explicitly, don't rely on Unity's
auto-promotion, since an accidental `UInt32` mesh built from `UInt16` source data with 8 meshes >64k
verts total across the map would otherwise silently work today and break the moment a bigger mesh is
added later). Call `mesh.RecalculateBounds()` as an explicit final step (not an implicit reliance on
`SetIndices`' default `calculateBounds: true` parameter) before saving — a mesh with unset/degenerate
bounds silently frustum-culls itself out of every camera, a quiet bug with no console error. Save each as
a `.asset` under `Assets/ArenaImport/Meshes/<mesh_id>.asset` via `AssetDatabase.CreateAsset`.

**Execution-order note**: §B.WP2.4 (texture import) must run **before** §B.WP2.3 (material import)
despite the document's numbering — `MaterialImporter.cs` (§B.WP2.3) calls `Material.SetTexture` against
`Texture2D` assets that only exist once §B.WP2.4 has created them; `SetTexture(prop, null)` on a
not-yet-imported texture is legal and silent (no exception, no console error), so running the
subsections in their written order would produce materials with missing/wrong textures that slip past
the S2 "zero errors" acceptance gate. `MeshImporter` (§B.WP2.2) has no such dependency and can run
before or after textures.

**B.WP2.3 — Stand-in shaders + material import**: for every distinct shader name in the package's
materials (Equator: 20 distinct, `arena-census.md` §6, 0 unresolved), author one editor stand-in
`.shader` file under `StandinShaders/` **with the exact real name and exact property declarations**
(TexEnv/Float/Color names from the material JSON's `shader_props_sample`-equivalent, per material —
union across all materials using that shader) so imported `Material` assets keep valid property slots —
this is the documented Icebreaker/Lighthouse/InterchangeRework convergent pattern (`ref-manimal.md` §3,
`ref-interchange.md` §3): *"author placeholder shaders with the REAL BSG shader's name in the SDK build
... at runtime `Shader.Find`/name-table lookup back onto the game's own compiled shader."* The stand-in's
actual shading math is irrelevant (it never ships/renders in-game — WP4's runtime rebind replaces it
before the player ever sees a frame, §F.3) but it MUST declare every property the material JSON
references, with matching types, or `Material.SetFloat`/`SetColor`/`SetTexture` on import throws/silently
no-ops. `MaterialImporter.cs` then creates one `.mat` asset per material id, sets `shader =
Shader.Find(<name>)` (resolves to the stand-in within the Editor), applies every TexEnv (incl. `_ST`),
Float, Color, keyword (`EnableKeyword`), and `renderQueue`.

**Edge case**: a shader name containing characters illegal in a Unity `.shader` `Shader "..."` name
declaration (none observed — Equator's list is standard `p0/...`, `Decal/...` etc., `arena-census.md` §6)
— if WP1 or a future map surfaces one, escape per Unity's shader-name grammar; do not silently rename
(the whole point is exact-name match for the runtime rebind, §F.3).

**B.WP2.4 — Texture import**: `TextureImporter.cs` sets `textureType = NormalMap` + `sRGBTexture=false`
for anything WP1 flagged as a normal map (§B.WP1 step 3), `sRGBTexture=true` for everything else
(diffuse/albedo convention), reasonable default compression (`DXT5`/`BC7` per source format, or leave
uncompressed for the spike if compression settings prove finicky — visual fidelity is not a spike
success criterion, only "renders, doesn't crash, colliders match").

**B.WP2.5 — Hierarchy assembly + minimal-component authoring**: `SceneAssembler.cs` runs in **two
explicit passes over each scene, not one interleaved loop** — `LODGroup.SetLODs()` stores direct
`Renderer` object references, not late-bound ids, and silently accepts a `null` slot for any renderer not
yet attached (no exception — just a missing LOD level). **Pass 1**: instantiate every `GameObject` and
attach every `MeshFilter`/`MeshRenderer`/collider/`Light` across the *whole* scene, building an
`id → Renderer` map as it goes. **Pass 2**: attach every `LODGroup`, resolving `lods[].renderer_object_ids`
against that completed map. Equator has 3,601 `LODGroup` components (`arena-census.md` §8.1) — a
single-pass, node-order implementation would non-deterministically null out LOD slots depending on
sibling instantiation order.

Pass 1 instantiates one `GameObject` per node (name, active, **layer by index** — verify
against `analysis/spt_gm.json`'s `TagManager.layers` array before assigning; Arena and SPT's 32-slot
layer tables are byte-identical per `ref-interchange-verify-F7.md` §2, confirmed independently by direct
`globalgamemanagers` comparison, so **no remap table is needed, assign the index verbatim**), **tag**
(string-based in modern Unity, `m_TagString` — the one mismatch at index 18/19 between Arena's and SPT's
tag *tables* is irrelevant since tags serialize by name not index, `arena-census.md` §7; if an object's
tag string is `AdditionalCamera`, it doesn't exist in SPT's `TagManager` — Unity will silently fall back
to `Untagged` on import; flag this in the manifest but do not treat it as fatal, no in-scope geometry
uses that tag per the census), parent, local TRS — then attaches `MeshFilter`/`MeshRenderer` (shadow
casting mode, motion vectors, light-probe usage/anchor override, reflection-probe usage — all copied
1:1 from the recorded component data, §C.6), `MeshCollider` (+`convex`, +`PhysicMaterial` asset created
per distinct material record and assigned by reference — dedup by value, not by source pointer, since
Arena's own 13-or-so distinct `PhysicMaterial` assets are shared across hundreds of colliders per
`ref-interchange-verify-F7.md` §3), `BoxCollider`/`SphereCollider`/`CapsuleCollider` (+`isTrigger`),
`Light` (type/color/intensity/range/spot angle/shadow settings/lightmapping mode — recorded as `4`
(Realtime) on 474/479 Equator lights, `arena-census.md` §8.1; **do not** attempt lightmap baking, there
is nothing to bake against, decision #8).

Pass 2 (after every `Renderer` in the scene exists) attaches every `LODGroup`: LOD array built from the
`id → Renderer` map (`lods[].renderer_object_ids` per entry) + per-LOD `screenRelativeTransitionHeight`
and `fadeTransitionWidth`, group-level `size` and group-level `fadeMode` — see §C.6 for the corrected
field-level schema (`fadeMode` is a single group-level enum in Unity's real API, `fadeTransitionWidth` is
per-LOD).

Saves each populated scene under `Assets/ArenaImport/Scenes/<SceneName>.unity` — **the `SceneName` used
here is load-bearing**: it must exactly equal the `rcid` WP3 writes into the patched preset's
`_scenesResourceKeys` entries (§E.2), because `AssetsManagerExtension.LoadScene` resolves scene identity
by that string (`EFT/ResourceKey.cs:22-39`, confirmed: `ToAssetName()` returns `rcid` directly when
non-empty, and `Unity.SceneManager.LoadSceneAsync(name, ...)` finds a scene of that exact name in *any*
loaded AssetBundle regardless of who loaded it — the InterchangeRework/Labs-confirmed "name-based, not
bundle-scoped" resolution, `ref-interchange.md` §1/§2, `ref-manimal.md` §2 Labs section).

**B.WP2.6 — The `Arena_Equator_Scripts` scene (fresh-authored, not extracted)**: one scene,
`Assets/ArenaImport/Scenes/Arena_Equator_Scripts.unity`, containing **only** the five required EFT
components per §D — built by a dedicated `Editor/Import/AuthorScriptsScene.cs` menu command, not by
`SceneAssembler` (there is no `scenes/Arena_Equator_Scripts.json` in the WP1 package at all — §E.1
explicitly excludes it from extraction). See §D for exact field values.

**B.WP2.7 — NavMesh bake (decision #6: re-bake fresh, do NOT port Arena's binary `NavMeshData`)**:
add `com.unity.ai.navigation` version `1.1.5` (pinned — this exact version ships as the bundled default
for this Editor install, confirmed present at
`C:/Program Files/Unity/Hub/Editor/2022.3.43f1/Editor/Data/Resources/PackageManager/Editor/com.unity.ai.navigation-1.1.5.tgz`;
pin the literal version string in `manifest.json` rather than the bare package name, so two different WP2
runs/machines can't silently resolve different package versions — the same determinism standard §B.WP1
already holds the extractor to) to `Packages/manifest.json`. `Editor/NavMesh/BakeNavMesh.cs`: add a
`NavMeshSurface` component (temporary, editor-only — not saved into the shipped scene) spanning the union
of all 5 content scenes' collider geometry, `agentTypeID = 0`. **Important correction**: `agentTypeID = 0`
alone does **not** give you SPT's values "for free" — `NavMeshSurface.agentTypeID` selects an entry from
**this project's own** `ProjectSettings/NavMeshAreas.asset` agent table (via `NavMesh.GetSettingsByID`),
and this project is explicitly empty, so its fresh agent-0 ("Humanoid") entry holds *Unity's* stock
out-of-the-box default (radius 0.5/height 2/slope 45°/step 0.4), **not** SPT's numbers — those two only
happen to look alike in casual description ("agent 0", "Humanoid default") but are numerically different
defaults from different sources. The values this spec actually wants —
`agentRadius=0.3`, `agentHeight=1.7`, `agentSlope=48`, `agentClimb=0.38`, `manualCellSize=false`,
`tileSize=256` — come from **SPT's own client project's** configured agent-0 entry (`spt_gm.json` lines
199-217, i.e. `NavMeshProjectSettings.m_Settings[0]` read out of SPT's `globalgamemanagers`), not a
generic Unity engine constant, and nothing in this empty project writes them in automatically. **Before**
calling `surface.BuildNavMesh()`, `BakeNavMesh.cs` must explicitly write these values into the bake —
either (a) build a `NavMeshBuildSettings` from `spt_gm.json`'s numbers and call `NavMesh.SetSettings(...)`
for `agentTypeID 0` in this project (persists for the batchmode session, sufficient for a one-shot bake),
or (b) bypass `NavMeshSurface` entirely and call the lower-level
`UnityEditor.AI.NavMeshBuilder.BuildNavMeshData(buildSettings, sources, bounds, position, rotation)` with
an explicit `NavMeshBuildSettings` struct built from those same numbers — either way, add a WP6 check
(`validate_bundle_roundtrip.py`) that reads the baked `NavMeshData`'s agent parameters back and diffs them
against `spt_gm.json`, since nothing currently does. Equator's own bespoke agent (`agentTypeID
287145453`, radius 0.28/height 1.60, `arena-census.md` §10) is **not** reused — baking fresh for SPT's
agent-0 values means an exact match to what SPT's own bot code expects when bots are added later, at the
cost of this one explicit settings-write step. Include every `MeshCollider` marked non-trigger + every
`BoxCollider`/`CapsuleCollider`/`SphereCollider` non-trigger as NavMesh-static (walkable) sources;
respect any `NavMeshModifier`/`NavMeshModifierVolume` the extraction recorded (Arena's own
`Unity.AI.Navigation.NavMeshModifier` usage, 124 instances on Equator's full set, `arena-census.md` §8.2
— **[OPEN, WP1/WP2 boundary]**: WP1 records these MonoBehaviours' presence + GameObject reference in
`scenes/*.json` per §C.6's generic-MonoBehaviour-passthrough rule, but their *field values*
(`overrideArea`/`area`) are Arena-custom serialized data subject to the same IL2CPP-typetree limitation
as §B.WP1's spawn-anchor caveat — WP2 defaults every carried-over `NavMeshModifier` to "no override" and
treats a correct area-cost translation as a stretch goal, not a blocker, since the spike's success
criterion is "the player can walk," not "bots path correctly through every carve-out"). Call
`surface.BuildNavMesh()`, then `AssetDatabase.CreateAsset(surface.navMeshData, "Assets/ArenaImport/NavMesh/arena_equator.asset")`
— loaded by type at runtime (§F, matches `NavMeshExtension.cs:68-89`'s documented "asset is called
whatever the editor named it" pattern, so the exact asset name here is not load-bearing, only its
presence in the navmesh bundle is).

**B.WP2.8 — Bundle build**: `Editor/BundleBuild/BuildBundles.cs`, `BuildPipeline.BuildAssetBundles(outputPath,
builds, options, BuildTarget.StandaloneWindows64)` — **the `BuildTarget` argument must be passed
explicitly**, never left to default to `EditorUserBuildSettings.activeBuildTarget` (which this empty
project has no reason to already have set to the right platform). The live consumer is `EFT.exe`, a
Windows standalone Mono player — a bundle built for the wrong target loads as `null` from
`AssetBundle.LoadFromFile` at runtime (or logs a platform-mismatch error), with no equivalent offline
signal anywhere upstream of §G.S4. `validate_bundle_roundtrip.py` (§B.WP6) must re-read the built bundle's
serialized-file header via UnityPy and assert its target platform is `StandaloneWindows64`, so a wrong
target is caught at WP6's offline check rather than at the most expensive point in the pipeline (S4).
- `arena_equator_scenes.bundle`: `AssetBundleBuild` with `assetNames` = the 6 scene paths (5 imported +
  1 authored), `assetBundleName` matches the on-disk bundle name — **note**: the *served key* WP4's
  `BundlesManager` hook uses (§0: `arena_equator/arena_equator_scenes.bundle`) is a distinct concept from
  Unity's own `assetBundleName` string; WP4 only cares about the physical file path it loads via
  `AssetBundle.LoadFromFile`, and registers it under the served-key string in `BundlesManager._bundles`
  — the two strings don't need to match, but keeping them identical avoids confusion (recommended).
- `arena_equator_navmesh.bundle`: one `NavMeshData` asset.
- `arena_equator_preset.bundle`: **not built here** — WP3 owns this (it patches a donor bundle with
  UnityPy, entirely outside Unity's own bundle pipeline, since the donor must already contain a
  `MonoScript` reference that resolves against the *live SPT Mono assembly*, which only the SPT Editor
  itself — or a UnityPy binary patch of an SPT-built bundle — can produce; a bundle built by *this*
  Editor session would reference *this* session's own `Assembly-CSharp` copy, not necessarily
  binary-identical to the deployed one for `MonoScript` `PPtr` resolution purposes — WP3's patch-a-real-
  donor approach sidesteps that risk entirely, matching Icebreaker's own justification for the same
  choice, `ref-manimal.md` §2).
- Output written to `work/build/` (gitignored, ad hoc — not part of the tracked repo), then copied into
  `dist/BepInEx/plugins/ArenaMapPort/bundles/` by a deploy step WP4 owns (decision #9: dist/ is a
  separate, user-approved deployment step, not auto-run).

**Edge cases**:
- **A material/shader combination that fails to compile as a stand-in** (e.g. a property type mismatch
  between two materials nominally sharing one shader name — not expected per the census's clean 20/26
  shader counts, but not proven impossible for the full in-scope set): fail the import for that specific
  material with a clear id in the log, do not silently drop the renderer (a renderer with no material is
  worse than a build failure — it's a silent, hard-to-spot missing-collider-adjacent visual bug).
- **A GameObject whose recorded layer index has no name in SPT's `TagManager`** (none exist — all 32
  slots match, `ref-interchange-verify-F7.md` §2): impossible per current evidence; if it ever occurs,
  treat as an extractor bug (WP1), not something WP2 papers over.
- **Two materials with the same name but different content** (Unity dedups by asset path, not content) —
  `mat_id` from WP1 (content-hash-derived) is the asset filename, not the material's `m_Name`, so this
  cannot collide.
- **A mesh with zero triangles** (a placeholder/trigger-only object) — import as-is; do not filter it out
  (it may be a legitimate collider-only volume).

**Outputs**: `UnityProject/Assets/ArenaImport/**` (generated Unity assets), `work/build/*.bundle`
(gitignored build output).

**Acceptance criteria**:
- `-batchmode -nographics -quit -executeMethod ArenaMapPort.Editor.BundleBuild.BuildBundles.Run` exits 0
  from a clean `Assets/ArenaImport` (i.e. the whole pipeline — import, shader authoring, NavMesh bake,
  bundle build — is one headless, reproducible command per the workspace's stated batchmode requirement).
- Every material in the built scenes resolves to a non-null shader (no `Hidden/InternalErrorShader`
  pink/magenta materials in the Editor Console after import).
- `NavMeshSurface.BuildNavMesh()` produces at least 1 tile covering the walkable footprint (sanity: tile
  count same order of magnitude as Equator's own 41, not exactly — different agent settings and a subset
  of scenes will shift this; flag if it's off by more than ~2× without an obvious cause, e.g. a missing
  collider scene).
- WP6's S1/S2 checks (§G) pass against this output.

---

### WP3 — Preset bundle patcher (Python / UnityPy)

**Owns**: `tools/preset/patch_preset.py`, writes `work/build/preset/arena_equator_preset.bundle`
(gitignored). (Corrected fix-cs pass, 2026-09-13 — the actual, intentional convention: both the
`tools/preset/` location and the `work/build/preset/` subfolder are established across WP3/WP4/WP6's own
output and progress docs; `validate_preset.py`'s `DEFAULT_BUNDLE` already matches. This section previously
said `tools/extract/patch_preset.py` / `work/build/arena_equator_preset.bundle`, which was never what got
built. WP4's staging target globs `work/build/**/*.bundle` recursively so this never affected `dist/`.)

**Inputs**: a **donor** SPT 4.1 preset bundle — `D:/SPT41/EscapeFromTarkov_Data/StreamingAssets/Windows/maps/develop_preset.bundle`
(read-only). Confirmed live on this exact install via direct UnityPy read this pass: one `MonoBehaviour`
named `develop.ScenesPreset` (`path_id 7034189889941257301`), fields `ActiveSceneGuid={guid:"",
_onlyOffline:0}`, `ServerName:"develop"`, `_activeSceneName:""`, `ChildPresets:[]`,
`_scenesResourceKeys`: 3 entries, each `{path:"Assets/Content/Locations/Develop/develop*.unity",
rcid:<same>, _onlyOffline:0}`. **Correction (spt-runtime review)**: an earlier pass's field dump also
listed `GameModes:null`, but the live, decompiled `EFT.ScenesPreset` class this `MonoBehaviour.m_Script`
resolves against has no such member (`D:/SPT41/Development/_decompiled/EFT/ScenesPreset.cs:10-99`, full
class: `ActiveSceneGuid`, `ServerName`, `ScenesGuids`, `_disableServerScenes`, `ChildPresets`,
`_activeSceneName`, `_scenesResourceKeys` only — confirmed again this revision, and no subclass of
`ScenesPreset` exists anywhere in the decompile). The patch script must **not** assume a `GameModes` key
exists in the donor's raw typetree — see step 6 below, which now re-dumps and asserts on the real key set
instead of writing to an unconfirmed field. This is chosen over `suburbs`/`town` (which have **no** preset bundle at
all — `spt-location-contract.md` §5.3 confirmed zero `*_preset.bundle` exists for them) specifically
*because* it is small, real, and its `MonoBehaviour.m_Script` `PPtr` already resolves to
`EFT.ScenesPreset` inside the live SPT Mono client — the exact same reasoning Icebreaker used for
patching *retail's own* `icebreaker.bundle` rather than authoring a `ScenesPreset` `ScriptableObject`
from scratch (`ref-manimal.md` §2, `patch_icebreaker_preset.py:1-8`'s own header comment: *"the
MonoBehaviour's script reference stays BSG's own ... binds to EFT.ScenesPreset by class identity in the
SPT client"*).

**Algorithm** (near-verbatim port of `patch_icebreaker_preset.py`, adapted):
1. `env = UnityPy.load(donor_bundle_path)`.
2. Find the `MonoBehaviour` whose `read_typetree()['m_Name']` contains `"ScenesPreset"`.
3. Build the target `_scenesResourceKeys` list, **in this exact order** (first entry loads
   `LoadSceneMode.Single`, per `LoadScenesFromPresetOperation.cs:237-238,261,283` cited in
   `spt-location-contract.md` §1 step 3 — must be the Scripts scene):

   | # | `rcid` (= exact Unity scene name from WP2 §B.WP2.5/6) | Source |
   |---|---|---|
   | 1 | `Arena_Equator_Scripts` | WP2-authored (§D) |
   | 2 | `Arena_Equator` | extracted `level51` |
   | 3 | `Arena_Equator_Lighting` | extracted `level55` |
   | 4 | `Arena_Equator_DesignStuff` | extracted `level53` |
   | 5 | `Arena_Equator_TeamFight` | extracted `level58` |
   | 6 | `Arena_Equator_TeamFight_DesignStuff` | extracted `level57` |

   For every entry: `path = "arena_equator/arena_equator_scenes.bundle"` (the served scene-bundle key,
   §0 — identical for all 6, matching the documented "name resolves in any loaded bundle" behavior,
   `ref-interchange.md` §1/§2), `rcid` = the table value above, `_onlyOffline = 0`.
4. `tt['ServerName'] = "arena_equator_tdm"` (any unique string — nothing in the traced load chain reads
   `ServerName` except the mod's *own* future gating code, which this spike doesn't add — see §F, there
   is no `ServerName`-gated Harmony prefix in this design at all, unlike InterchangeRework's variant-swap
   pattern; our preset is the *only* preset the `Scene` ResourceKey in `base.json` ever points at, so no
   runtime gate is needed).
5. `tt['_activeSceneName'] = "Arena_Equator_Scripts"` (this is the string `SceneManager.SetActiveScene`
   uses post-load, `spt-location-contract.md` §1 step 3 — **not** derived from `ActiveSceneGuid`, so that
   field's value is irrelevant; leave it `{"guid":"", "_onlyOffline":0}` as-is from the donor, or clear
   it — both are inert).
6. `tt['ChildPresets'] = []` — a flat, single preset; no per-mode branching for the spike (matches
   Icebreaker's own single-preset simplification, not Arena's own nested TDM/LastHero/ShootOut structure —
   decision #1 only requires the TDM scene set to load, not the mode-select machinery). **Before this
   step, print `tt.keys()` and diff against `EFT.ScenesPreset`'s real field set** (`ActiveSceneGuid`,
   `ServerName`, `ScenesGuids`, `_disableServerScenes`, `ChildPresets`, `_activeSceneName`,
   `_scenesResourceKeys`) — an earlier evidence-gathering pass reported a `GameModes` key that does not
   exist on the class, so **do not** write `tt['GameModes'] = ...` unless the re-dump actually shows that
   key present in the donor's raw typetree (UnityPy's `save_typetree` typically ignores unrecognized dict
   keys silently, so this is unlikely to corrupt the save either way, but ship the verified key set, not
   the unverified one).
7. `tt['ScenesGuids'] = tt['ScenesGuids'][:6]` if `len(tt['ScenesGuids']) >= 6`, **else pad with
   placeholder `Guid` entries up to 6** — print the donor's actual `ScenesGuids` length first; do not
   assume it's ≥6 (only `_scenesResourceKeys`'s 3-entry count was directly confirmed for the donor).
   `ScenesGuids` is **not** fully unused by the load chain as an earlier pass claimed — `ScenesPreset.
   GetTotalSceneCount()` (`ScenesPreset.cs:95-98`) reads it and is used as the loading-progress-bar
   denominator (`LoadScenesFromPresetOperation.cs:116-122`) — it is unused only for **scene identity/
   gating** (that's driven entirely by `_scenesResourceKeys`), not for the progress callback. A
   shorter-than-6 `ScenesGuids` left untrimmed would silently under-count the progress bar (cosmetic
   overshoot, e.g. reads `4/3`) rather than a functional bug, but pad it anyway since the fix is free.
8. `tt['m_Name'] = "arena_equator.ScenesPreset"` (cosmetic, matches the `rcid`'s lowercase-with-`.asset`
   convention for the *containing bundle asset name* — `arena_equator.scenespreset.asset`, §0 — the
   `m_Name` on the object itself follows BSG's `<id>.ScenesPreset` capitalization convention observed on
   the donor, e.g. `develop.ScenesPreset`).
9. `obj.save_typetree(tt)`, then `env.file.save()` to `work/build/preset/arena_equator_preset.bundle`.

**Edge cases**:
- **`rcid` misspelled relative to WP2's actual saved scene name** (the single highest-leverage typo risk
  in this whole pipeline — a one-character mismatch means `SceneManager.LoadSceneAsync` finds nothing and
  the scene silently never loads, no exception): WP6 owns a cross-check (§B.WP6) that diffs this table
  against the actual scene names inside the built `arena_equator_scenes.bundle`.
- **Donor bundle's `MonoBehaviour` count > 1 matching `"ScenesPreset"` in its name** (not the case for
  `develop_preset.bundle`, confirmed single match this pass) — if a future donor swap hits this, the
  script must fail loudly rather than patch the first match.
- **UnityPy's `save_typetree`/`env.file.save()` altering the donor bundle's compression or breaking its
  external file references** (the donor's `MonoScript` `PPtr` points into `globalgamemanagers.assets` /
  the Mono assembly's own script table by file+path-id — the save must preserve this dependency
  unchanged): verified by WP6's re-read check (§B.WP6) — read the saved bundle back and confirm
  `m_Script`'s `PPtr` target file+path-id is byte-identical to the donor's.

**Outputs**: `work/build/preset/arena_equator_preset.bundle`.

**Acceptance criteria**:
- UnityPy re-read of the saved bundle shows exactly 6 `_scenesResourceKeys` entries, in the order above,
  each `path == "arena_equator/arena_equator_scenes.bundle"`, `rcid` matching the table exactly.
- `_activeSceneName == "Arena_Equator_Scripts"`.
- The `MonoBehaviour`'s `m_Script` `PPtr` is unchanged from the donor (same file index + path id) —
  proves the patch didn't disturb the one field the client actually needs to resolve `EFT.ScenesPreset`
  class identity.

---

### WP4 — Client plugin (BepInEx)

**Owns**: `ArenaMapPort.Client/**`, links `ArenaMapPort.Shared/ArenaEquatorLocation.cs`.

See §F for the full hook/timing design. Summary of responsibilities: serve the 3 bundles from the
plugin folder (§F.1, exact SPT-4.1.5-verified `BundlesManager` hook — simpler than Icebreaker's, no
`AccessTools` reflection needed at all since the relevant members are public in this decompiled build),
add the NavMesh data before `BotsController.Init` runs (§F.2, direct `NavMeshExtension.cs` port), rebind
stand-in shaders to the game's own compiled shaders once per raid (§F.3), log diagnostics (§F.5), no
config beyond a debug-logging toggle (the spike has no player-facing settings).

**Build target**: `netstandard2.1`, **not** `net472` as the task brief's shorthand said — corrected here
against hard evidence: the sibling `ArenaMode.Client.csproj:6` (a shipped, playtested BepInEx 5.4.23.5
plugin for this exact SPT 4.1 install) targets `netstandard2.1` and references
`D:/SPT41/EscapeFromTarkov_Data/Managed/*.dll` directly (Unity's Mono runtime accepts netstandard2.1
assemblies even though vanilla .NET Framework 4.7.2 nominally doesn't fully implement that surface —
this is a known, working Unity/BepInEx modding convention, not a novel risk). Mirror
`ArenaMode.Client.csproj`'s reference list exactly (Assembly-CSharp, Comfort/Comfort.Unity, UnityEngine.*
modules, Newtonsoft.Json, BepInEx.Core) plus `UnityEngine.AIModule` (already listed there) for
`NavMesh.AddNavMeshData`.

**Outputs**: `ArenaMapPort.Client.dll` deployed to `dist/BepInEx/plugins/ArenaMapPort/` (build-time copy
target, mirroring `ArenaMode.Client.csproj:61-69`'s `DeployArenaAudioAssets`-style pattern but targeting
`dist/`, not `D:/SPT41` directly — decision #9: never write into `D:/SPT41`, a separate user-run script
promotes `dist/` → the live install).

**Acceptance criteria**: see §G.S3/S4 — compiles clean against the referenced DLLs; S4 is the real test
(in-game load).

---

### WP5 — Server mod (SPT 4.1.5, net9.0)

**Owns**: `ArenaMapPort.Server/**`, links `ArenaMapPort.Shared/ArenaEquatorLocation.cs`, owns
`dist/SPT_Runtime/user/mods/ArenaMapPort/db/*.json`.

**Target framework note**: ArenaMode.Server.csproj targets `net10.0` against `SPTarkov.Server.Core
Version="4.1.0"` (the NuGet reference package) — but critic.md §1 established the **live, deployed**
server binary is a different, newer build (`FileVersion 4.1.5`, different SHA-256) and the brief's own
instruction says "SPT 4.1.5 (net9)". Follow the brief's `net9.0` and pin
`SPTarkov.Server.Core`/`.Common`/`.DI`/`.Reflection`/`.Web` to whatever NuGet version resolves against
4.1.5 at implementation time — **do not** assume `4.1.0` is correct without checking; if only `4.1.0`
packages exist on the feed, ExcludeAssets="runtime" (as ArenaMode.Server.csproj:39-43 already does) is
still safe for compilation even against a slightly newer running binary, since `critic.md` §1 already
confirmed `LocationTable`'s registration-relevant surface (`GetDictionary()`/`HydrateDictionary()`) is
byte-identical between the two versions — but re-verify `Location`/`LocationBase`/`LazyLoad<T>`'s shape
didn't drift if compilation errors surface.

**Algorithm** (`ArenaMapPort.Server/ArenaEquatorRegistration.cs`, an `IOnLoad` mod at
`TypePriority = OnLoadOrder.Preload + 90000` matching Icebreaker's own priority choice,
`IcebreakerMod.cs`'s class attribute — high enough that `LocationTable` is already hydrated by other
mods' `PostLoad`-order location edits, but registration itself only needs `LocationTable` to exist, not
be finalized, since we `.Add()` a brand-new key):

1. Deserialize `db/base.json` (§E.4 template) into `LocationBase`.
2. `newBase.Id = ArenaEquatorLocation.Key; newBase.IdField = ArenaEquatorLocation.Id;` (§0 constants).
3. **Loot must be non-null, contrary to `spt-location-contract.md` §5.1's "optional at the model level"
   framing** — that claim is true for `Location` *construction* (the C# properties are nullable) but
   false for **raid start**: `LocationLifecycleService.GenerateLocationAndLoot(sessionId, name,
   generateLoot=true)` (default `true` for every normal raid-creation call) unconditionally calls
   `locationLootGenerator.GenerateLocationLoot(name)` (`analysis/decompiled-server/LocationLifecycleService.cs:281`,
   confirmed live 4.1.5-equivalent code path) — and Icebreaker's own `IcebreakerMod.cs:132-138`'s comment
   states plainly: *"Our new Location needs explicit loot data so the server's raid-start loot generation
   ... NREs on null LooseLoot/StaticLoot/StaticContainers."* For the spike (loot explicitly out of scope,
   decision #8), supply the **minimal non-null, empty** shape, exactly Icebreaker's own "loot-free" branch
   (`IcebreakerMod.cs:196-204`):
   ```csharp
   icebreaker.LooseLoot = new LazyLoad<LooseLoot>(() => new LooseLoot {
       SpawnpointCount = new SpawnpointCount { Mean = 0, Std = 0 },
       Spawnpoints = [], SpawnpointsForced = [],
   }, cacheValue: false);
   icebreaker.StaticLoot = new LazyLoad<Dictionary<MongoId, StaticLootDetails>>(() => new(), cacheValue: true);
   icebreaker.StaticContainers = new LazyLoad<StaticContainerDetails>(() => new StaticContainerDetails {
       ObjectsSpawnPoints = [], Containers = [],   // exact field names TBD against the live 4.1.5
                                                     // StaticContainerDetails shape — verify at
                                                     // implementation time, the 4.1.0 reference shape
                                                     // may have drifted; if so this is a WP5-owned fixup,
                                                     // not a spec blocker.
   }, cacheValue: true);
   icebreaker.StaticAmmo = new Dictionary<string, IEnumerable<StaticAmmoDetails>>();
   icebreaker.AllExtracts = [];
   ```
4. `newBase.DisabledForScav = true` (same lever Icebreaker/Labs use, `ref-manimal.md` §1 point 4 — the
   spike only needs a PMC-side test raid).
5. Bot cap / `ScavRaidTimeSettings` / `StaticLootMultiplier`/`LooseLootMultiplier` / `NonMaps` — **skip
   for the spike** (bots are out of scope, decision #8; `critic.md` §1's own "not urgent for this spike"
   note applies verbatim). Leave a `// TODO(bots-milestone)` comment pointing at `ref-manimal.md` §1's
   punch-list so the next milestone doesn't have to re-derive it.
6. Locale keys: `"<IdField> Name"` and `"<IdField> Description"` at minimum (`LocationInfoPanel` reads
   `location._Id`, which the client deserializes from `base.json`'s wire `"_Id"` field — i.e. our
   `IdField`/MongoId `"e9b8e12c60dfd2820e949462"`, **not** the location key `"arena_equator"` — the JSON
   property named `"Id"` is a distinct field carrying the key; only `"_Id"` maps to `IdField`. Confirmed
   this pass by decompiling both sides: `SPTarkov.Server.Core.dll`'s `LocationBase` has
   `[JsonPropertyName("_Id")] public MongoId IdField` separate from `[JsonPropertyName("Id")] public
   required string Id`; the client's `JsonType/LocationSettings.cs:243/245/433` deserializes that same
   `"_Id"` wire value into its own `_Id` field and reads `LocalizedName => (_Id + " Name").Localized()`
   (`LocationInfoPanel.cs:131/138` for the description). Icebreaker's own reference implementation
   registers this identically — `IcebreakerMod.cs:252/258` uses `IcebreakerLocation.Id + " Name"` where
   `IcebreakerLocation.Id` is itself the MongoId (`Directory.Build.props: ModLocationId=882b2fa04bbd616567022938`),
   not the location key — placeholder English text is fine for the spike ("Arena: Equator (spike)" /
   "Internal test build"). (Corrected fix-cs pass, 2026-09-13 — this section previously said `"<Key>
   Name"`, contradicting both the decompiled evidence above and `ref-manimal.md` §1's own citation of the
   same convention; see `ArenaMapPort.Server/ArenaEquatorRegistration.cs`'s `ApplyLocale`.)
7. **Banners**: either supply zero entries in `base.json`'s `Banners` array, or wire real files via
   `imageRouter.AddRoute` — do **not** ship a `Banners` array with unresolvable ids (Icebreaker's own
   documented landmine, `ref-manimal.md` §1: *"a ripped base.json ships retail's Banners array with
   MongoIds nothing serves; the server logs [UNHANDLED] and the client shows an empty error dialog at the
   menu."*). For the spike, **empty `Banners: []`** is the simplest safe choice.
8. `ArenaEquatorRegistration.EnsureAvailable(locationTable)` (throws if the key or identity is already
   taken — direct port of `IcebreakerLocationRegistration.EnsureAvailable`, `icebreaker-server/IcebreakerLocationRegistration.cs:11-20`)
   then `locationTable.GetDictionary().Add(ArenaEquatorLocation.Key, icebreaker)` — the corrected
   registration mechanism per `critic.md` §1: `GetDictionary()` returns the **live, persistent**
   dictionary in the deployed 4.1.5 binary (confirmed identical `HydrateDictionary`/`GetDictionary` logic
   between the 4.1.0 reference and the live 4.1.5 build, `critic.md` §1), so a plain `.Add()` during
   `OnLoadAsync` is sufficient — **do not** attempt any reflection-based "closed record" workaround; none
   is needed.

**Attribution**: `THIRD-PARTY-NOTICES.md` must credit `ManimalIcebreaker-main` (MIT, "Copyright (c) 2026
danauraborealis") for the registration pattern and the loot-LazyLoad shape this section ports — per
`ref-manimal.md` §6's license table, this is explicitly permitted reuse under MIT with attribution.

**base.json**: see §E.4 for the full field template, derived from `develop`'s own `base.json` shape as
the donor structure (same reasoning as WP3's bundle donor — a real, minimal, already-`Enabled:false`
SPT-native location) with every field the load chain actually consumes (§spt-location-contract.md §5.1)
explicitly set.

**Edge cases**:
- **A second mod also tries to register `arena_equator`** (unlikely for a spike, but the exact scenario
  `EnsureAvailable` exists to catch) — the throw surfaces as a clear server-startup error naming the
  conflicting key, not a silent overwrite.
- **`StaticContainerDetails`'s exact field shape drifted between the 4.1.0 reference and the live 4.1.5
  binary** — flagged explicitly above as a compile-time risk to verify, not assumed identical (unlike
  `LocationTable`, which critic.md §1 *did* directly diff and confirm byte-identical for the relevant
  methods — `StaticContainerDetails` was not independently re-diffed this pass).

**Outputs**: `ArenaMapPort.Server.dll` + `db/base.json` deployed to
`dist/SPT_Runtime/user/mods/ArenaMapPort/`.

**Acceptance criteria**: see §G.S3/S4. Additionally: `dotnet build` succeeds against the pinned SPT
package version; a unit test (xunit, matching `ArenaMode.Tests`' convention) asserts
`ArenaEquatorRegistration.EnsureAvailable` throws on a duplicate key and that the assembled `Location`'s
`LooseLoot.Value`/`StaticLoot.Value`/`StaticContainers.Value` are all non-null and structurally valid
(catches the exact NRE class Icebreaker's own comment warns about, without needing a live server to
prove it).

---

### WP6 — Validation tooling

**Owns**: `tools/validate/*.py` (offline, no game/server required) + a documented manual checklist for
the one check that genuinely needs a live raid (§G.S4).

**Checks** (each a standalone script, run in CI-like fashion after WP1/WP2/WP3/WP5 each produce output):

| Check | Input | What it asserts |
|---|---|---|
| `validate_extract.py` | `work/extract/equator/manifest.json` | WP1's acceptance criteria (§B.WP1) programmatically: `errors == []`, every reference resolves, id determinism (run twice, diff) |
| `validate_bundle_roundtrip.py` | `work/build/*.bundle` | Re-reads the built scene bundle with UnityPy: scene names present match §E.1's table exactly (no typo, no missing scene); mesh/material/texture counts are non-zero and roughly match the extracted package's counts (catches "importer silently skipped everything" class of bug); NavMesh bundle contains exactly one `NavMeshData` with `tile_count > 0`; **every bundle's serialized-file header reports target platform `StandaloneWindows64`** (§B.WP2.8 — catches a wrong-`BuildTarget` build offline instead of at S4); **no built `LODGroup.GetLODs()` entry contains a null renderer** (§B.WP2.5's two-pass ordering requirement) |
| `validate_preset.py` | `work/build/preset/arena_equator_preset.bundle` | WP3's acceptance criteria (§B.WP3) — the `rcid` cross-check against the scene bundle's actual scene names is here, not in WP3 itself, since it needs *both* artifacts |
| `validate_server_db.py` | `dist/SPT_Runtime/user/mods/ArenaMapPort/db/base.json` | Every field `spt-location-contract.md` §5.1 marks "required to start" is present and non-empty: `Scene.path`/`Scene.rcid` match §0's constants exactly, `Id`/`IdField` match §0, `SpawnPointParams` is **absent/null, not `[]`** (critic.md §6 — an empty-but-present array actively deletes every scene-authored `SpawnPointMarker` on load, `SpawnPointsCollection.FixSpawns`, `spt-location-contract.md` §3) |
| `validate_spawn_authoring.py` | the exported `Arena_Equator_Scripts.unity` scene (read via UnityPy against the built bundle, or against the raw `.unity` YAML pre-build) | At least one `SpawnPointMarker`-typed object exists with `Categories` including `Player` and `Sides == All`(or a superset covering the test profile's side); a `Collider` component is present and enabled on the same object; exactly one `BotZone` exists with `PatrolWays` serialized as an empty array (`[]`), never absent/null; **the same `BotZone`'s `SpawnPointMarkers` is also serialized as an empty array (`[]`), never absent/null** (§D — a `null` here throws `NullReferenceException` on every raid start via `SpawnPointsCollection.CalculateCanSpawnPmcBots`/`BotsController.Init`, before the player ever spawns) |

**Manual checklist** (§G.S4) — cannot be automated without a live game process; documented as exact log
lines / visual checks the user performs.

**Acceptance criteria for WP6 itself**: every script above exits non-zero with a specific, actionable
message on failure (never a bare stack trace) — a future implementer re-running these after a change
must get a diagnosis, not a puzzle.

---

## C. Intermediate package format contract

Root: `work/extract/equator/`. All files UTF-8 JSON unless noted. All ids are lowercase hex, 16
characters, derived as `sha1(f"{source_file_basename}:{path_id}:{object_type}")[:16]` — stable across
re-runs (content/identity-derived, not run-order-derived, per §B.WP1's acceptance criteria).

### C.1 `manifest.json`

```jsonc
{
  "schema_version": 1,
  "source": {
    "arena_data_root": "X:/Escape from Tarkov Arena/EscapeFromTarkovArena_Data",
    "extractor_script": "tools/extract/extract_equator.py",
    "extracted_utc": "2026-09-13T00:00:00Z"
  },
  "scenes": ["Arena_Equator", "Arena_Equator_Lighting", "Arena_Equator_DesignStuff",
             "Arena_Equator_TeamFight", "Arena_Equator_TeamFight_DesignStuff"],
  // NOTE: "Arena_Equator_Scripts" is intentionally absent — WP2-authored, not extracted (§E.1).
  "meshes": { "<mesh_id>": {"file": "meshes/<mesh_id>.mesh.bin", "source_object_name": "palm_plant_01_LOD2",
                              "vertex_count": 561, "submesh_count": 4} },
  "textures": { "<tex_id>": {"file": "textures/<tex_id>.png", "source_name": "road_marking",
                               "width": 1024, "height": 1024, "format_src": "DXT5",
                               "color_space": "sRGB", "is_normal_map": false} },
  "materials": { "<mat_id>": {"file": "materials/<mat_id>.json", "source_name": "wood_dark_mat",
                                "shader": "p0/Reflective/Bumped Specular SMap"} },
  "anchors_reference": {
    // human/QA reference only, NOT consumed by WP2's import (critic.md §2c) — world positions of
    // Arena's own authored spawn/zone markers, for sanity-checking author-time placement.
    "EFT.Game.Spawning.SpawnPointMarker": {"count": 61, "sample_positions": [[27.38,0.12,-16.49], "..."]}
  },
  "errors": []   // MUST be empty for an acceptance-passing run
}
```

### C.2 `meshes/<id>.mesh.bin` — binary layout

Little-endian, no padding beyond what's stated. One file per distinct mesh.

| Offset | Field | Type | Notes |
|---|---|---|---|
| 0 | `magic` | `char[4]` | `"AMPM"` (ArenaMapPort Mesh) |
| 4 | `version` | `uint32` | `1` |
| 8 | `vertex_count` | `uint32` | |
| 12 | `channel_mask` | `uint16` | bit 0=position(always 1), 1=normal, 2=tangent, 3=color, 4..11=uv0..uv7 |
| 14 | `index_format` | `uint8` | `0`=UInt16, `1`=UInt32 (mirrors Unity's `IndexFormat`; 100% UInt16 observed but the field exists so a future bigger map doesn't need a format bump) |
| 15 | `submesh_count` | `uint8` | |
| 16 | `positions` | `float32[vertex_count][3]` | present unconditionally |
| … | `normals` | `float32[vertex_count][3]` | present iff channel_mask bit 1 |
| … | `tangents` | `float32[vertex_count][4]` | present iff bit 2 (xyz + w handedness) |
| … | `colors` | `float32[vertex_count][4]` | present iff bit 3 (RGBA, linear) |
| … | `uv<N>` | `float32[vertex_count][2]` | present iff bit `4+N` set, for N=0..7, in ascending order |
| … | `submeshes` | `submesh_count ×` `{uint32 index_start, uint32 index_count, uint8 topology}` | `topology`: `0`=Triangles (only value observed/needed) |
| … | `indices` | `uint16[]` or `uint32[]` per `index_format`, total length = sum of all submeshes' `index_count` | |

### C.3 Texture files

Plain PNG under `textures/<id>.png`, RGBA8, losslessly decoded from source (§B.WP1 step 3). Metadata
(`format_src`/`color_space`/`is_normal_map`) lives in `manifest.json`, not embedded in the PNG.

**`dimension`** (added task `impl-texture-dimensions`, compatible extension — absent/unknown means
`"Tex2D"`): one of `"Tex2D"` (default), `"Cube"`, `"Tex3D"`, `"Tex2DArray"` — the source object's
real Unity runtime shape, so WP2's importer never imports/assigns a Cubemap or Texture3D source as
a plain 2D texture (a Texture2D bound to a shader property declared `Cube`/`3D` is a hard runtime
error at scene load: `Error assigning 2D texture to CUBE texture property ...: Dimensions must
match`). For `"Cube"`, the PNG is a **vertical 6-frame strip** (`width x height*6`, one source face
per `height`-tall band, face order `+X,-X,+Y,-Y,+Z,-Z` — Unity's own strip-layout/`CubemapFace`
convention), decoded from all 6 real serialized faces (never just face 0); three extra manifest
keys accompany it: `cube_layout` (currently always `"vertical_strip"`), `cube_face_order` (the
6-entry list above), `cube_face_width`/`cube_face_height` (one face's own dimensions — `width`/
`height` above are the whole strip). WP2 imports a `"Cube"` texture as a real `Cubemap` asset
(`TextureImporterShape.TextureCube` + `TextureImporterGenerateCubemap.AutoCubemap`, which
auto-detects this exact strip layout) and assigns it normally. For `"Tex3D"`/`"Tex2DArray"`
(the latter recorded defensively — none observed in Equator's package), WP2 does **not** attempt a
real Texture3D/Texture2DArray Unity asset from the flattened/placeholder PNG; per this task's own
explicit fallback, the property is left **unassigned** (so the real shader's own default applies)
rather than ever binding a 2D texture to a 3D/array-declared property. A `"Tex3D"` entry also
carries `is_volume_texture`/`volume_source_depth`/`volume_source_height` (pre-existing, unchanged)
describing the stacked-slices flatten.

### C.4 `materials/<id>.json`

```jsonc
{
  "source_name": "wood_dark_mat",
  "shader": "p0/Reflective/Bumped Specular SMap",
  "render_queue": -1,                 // -1 = shader default, else explicit m_CustomRenderQueue
  "keywords": ["_EMISSION", "TINTMASK"],
  "tex_envs": { "_MainTex": {"texture_id": "<tex_id>|null", "scale": [1,1], "offset": [0,0]} },
  "floats":   { "_Glossiness": 0.5, "_BumpScale": 1.0 },
  "colors":   { "_Color": [1,1,1,1], "_EmissionColor": [0,0,0,1] }
}
```
A `tex_envs` entry with `texture_id: null` is a real, valid state (a property slot the material declares
but leaves unassigned) — WP2 must not treat this as an error, only skip `SetTexture` for that slot.

### C.5 `scenes/<scene_name>.json` — hierarchy

```jsonc
{
  "scene_name": "Arena_Equator",
  "objects": [
    {
      "id": "<stable local id, e.g. path_id as decimal string>",
      "name": "palm_plant_01_destroyed_LOD2",
      "active": true,
      "layer": 12,
      "tag": "Untagged",
      "parent_id": "<id>|null",
      "local_position": [0.0, 0.0, 0.0],
      "local_rotation": [0.0, 0.0, 0.0, 1.0],   // quaternion xyzw
      "local_scale": [1.0, 1.0, 1.0],
      "components": [ /* §C.6 */ ]
    }
  ]
}
```

### C.6 Component records (entries of `objects[].components`)

Tagged union by `"type"`. Every type below is what §B.WP1 step 5 extracts; anything not listed
(`ParticleSystem`, `AudioSource`, `OcclusionPortal`/`OcclusionArea`, `SpeedTree`/`TreeWind`, `Rigidbody`
(413 instances on Equator, `arena-census.md` §8.1, correlating with the 850 convex `MeshCollider`s,
`arena-census.md` §8.2 — a dynamic prop authored without an imported `Rigidbody` just behaves as inert
static geometry, safe for the spike, not a crash), any `MonoBehaviour` not covering a required-component
role) is **out of scope for the spike** and is not recorded at all — WP1 does not need a generic
passthrough mechanism for the spike, since decision #8's scope list is exhaustive for what must
render/collide.

fix-enabled-state: `MeshRenderer`/`SkinnedMeshRenderer`/`MeshCollider`/`BoxCollider`/
`SphereCollider`/`CapsuleCollider`/`Light`/`LODGroup` records additionally carry `"enabled": bool`
(from the component's own `m_Enabled`, **not** the owning GameObject's `active` field above it —
Unity distinguishes "this GameObject is active" from "this component on it is enabled", and Arena
uses the latter for a handful of leftover/placeholder objects per scene: disabled `MeshRenderer`s
on stencil ceiling/floor cutouts and `colider N`/`Cube` placeholder meshes, disabled `MeshCollider`s
on duplicate `colider N`/`ballistic` collision meshes, disabled `BoxCollider`s on decorative
mannequin-gear and stencil-cube objects — see `docs/progress/fix-enabled-state.md` for the full
per-scene counts). §B.WP1 step 5's schema was extended to record this field additively — a package
extracted before this field existed has no `"enabled"` key on these records at all, and every
reader (the importer, both validators) treats a missing key as `true`, so an old package remains
valid without re-extraction. `ReflectionProbe` has no `enabled` field since it is never imported
(`skip_import: true` unconditionally, see below) and `MeshFilter` has no enabled concept of its own
(a `MeshFilter` is not a `Behaviour`, it has no `m_Enabled` field in Unity itself).

| `type` | Fields |
|---|---|
| `MeshFilter` | `mesh_id` |
| `MeshRenderer` | `material_ids: [id,...]` (one per submesh, ordered), `cast_shadows: "On"|"Off"|"TwoSided"|"ShadowsOnly"` (from `m_CastShadows`), `receive_shadows: bool`, `light_probe_usage: "Off"|"BlendProbes"|"UseProxyVolume"` (from `m_LightProbeUsage`; record but §D notes light-probe *assets* themselves are out of scope — a renderer requesting probes with none baked degrades gracefully, Unity's own documented fallback), `reflection_probe_usage: "Off"|"BlendProbes"` (record only — no `ReflectionProbe` import per decision #8's scope list), `enabled: bool` (fix-enabled-state, see note above; default `true` if absent) |
| `SkinnedMeshRenderer` | same as `MeshRenderer` plus `bind_pose_bone_names: [string,...]` and `enabled: bool`; **always flagged** `"skip_import": true` (§B.WP1 edge case — recorded for completeness, WP2 does not build a skinned mesh from this) |
| `MeshCollider` | `mesh_id`, `convex: bool`, `is_trigger: bool`, `physic_material: {dynamic_friction, static_friction, bounciness, friction_combine: "Average"|"Minimum"|"Maximum"|"Multiply", bounce_combine: same enum}|null`, `enabled: bool` (fix-enabled-state; default `true` if absent) |
| `BoxCollider` | `center: [x,y,z]`, `size: [x,y,z]`, `is_trigger: bool`, `physic_material` (same shape as above)`|null`, `enabled: bool` (fix-enabled-state; default `true` if absent) |
| `SphereCollider` | `center`, `radius`, `is_trigger`, `physic_material|null`, `enabled: bool` (fix-enabled-state; default `true` if absent) |
| `CapsuleCollider` | `center`, `radius`, `height`, `direction: 0|1|2` (X/Y/Z axis), `is_trigger`, `physic_material|null`, `enabled: bool` (fix-enabled-state; default `true` if absent) |
| `Light` | `light_type: "Spot"|"Directional"|"Point"` (from `m_Type` 0/1/2), `color: [r,g,b,a]`, `intensity`, `range`, `spot_angle`, `lightmapping_mode: int` (record verbatim — `4`=Realtime dominant per census; WP2 forces Realtime regardless since no baking occurs), `shadow_type: "None"|"Hard"|"Soft"`, `enabled: bool` (fix-enabled-state; default `true` if absent) |
| `LODGroup` | `lods: [{screen_relative_height: float, renderer_object_ids: [id,...], fade_transition_width: float}]`, `size: float`, `fade_mode: int` — **note the level of each field**: `fade_mode` is Unity's single, group-level `LODFadeMode` enum (`None`/`CrossFade`/`SpeedTree`) and is recorded once per `LODGroup`, not per LOD entry; `fade_transition_width` is a field of Unity's per-LOD `LOD` struct and legitimately differs between transitions, so it is recorded per `lods[]` entry, not once for the group (the reverse of an earlier draft of this schema — 3,601 `LODGroup` components on Equator, `arena-census.md` §8.1, so this is not a rare case); `enabled: bool` (fix-enabled-state; default `true` if absent) |
| `ReflectionProbe` | recorded, **always** `"skip_import": true` (out of scope, decision #8; Equator has exactly 1, `arena-census.md` §8.1 — negligible loss) |

### C.7 Anchors (reference-only, §C.1's `anchors_reference` block)

One entry per class in `census.py`'s `SPAWN_ZONE_CLASS_KEYWORDS` (`common.py`/`census.py:434-435`) that
was found, `{count, sample_positions: [[x,y,z],...] (first 8)}` — a direct reuse of
`spawn_zone_anchors()`'s existing return shape (`census.py:463-467`), not a new schema.

### C.8 `navmesh_ref/equator_source_navmesh.json` — Arena's own bake settings, reference only

Direct copy of `arena-census.md` §10's table for Equator (`agentTypeID 287145453`, radius 0.28/height
1.60/slope 48/climb 0.38, cellSize 0.07, tileSize 256, source bounds center `(0,2,0)` extent
`(71.65,10.05,86.35)`, 41 tiles) — **not consumed by WP2's bake** (decision #6: bake fresh for agent 0,
§B.WP2.7), kept only so a human can sanity-check WP2's own fresh bake's tile count/extent against a known
comparable, and as forward reference for a later milestone that might want to match Arena's own agent
radius more closely for tighter corridors.

---

## D. Required EFT scene components — authored vs. runtime, and exact values

**Decision (resolves `critic.md` §2d)**: all five components below are **authored directly in the
Unity Editor**, as real `Assembly-CSharp` types referenced via WP2's `GameRefs/` mechanism (§B.WP2.1) —
not typetree-stub clones, not runtime-created. This is safe specifically *because* this project's Unity
Editor is the same version as the target Mono client and references the actual, unmodified
`Assembly-CSharp.dll` the client loads — a materially different (and simpler, more reliable) situation
than Icebreaker/Labs faced against an external, undocumented SDK project. All five live in the single
fresh-authored `Arena_Equator_Scripts` scene (§B.WP2.6), loaded first (`LoadSceneMode.Single`).

| Component | Authoring | Exact field values | Evidence |
|---|---|---|---|
| `LevelSettings` | One instance, any GameObject (active) in the Scripts scene. | Defaults are acceptable for the spike (decision #8 doesn't require lighting-fidelity parity) — `AmbientMode = Flat`, `SkyColor`/`EquatorColor`/`GroundColor` = a neutral grey (e.g. `(0.5,0.5,0.5,1)`), `AmbientIntensity = 1`, `fog = false`, `skybox = null`, `CameraPrefab = null`, `PostProcessProfilePrefab = null` (both guarded-null-safe per `spt-location-contract.md` §2's table row for this component). **Must exist** — `Singleton<LevelSettings>.Instance.OnPostLoadingScene()` NREs on a null/stale singleton right after scene load (`TarkovApplication.cs:2731`, cited `spt-location-contract.md` §2). | `LevelSettings.cs:19-171` (this pass, full field list read) — `Awake()` at `:166-171` calls `Singleton<LevelSettings>.Create(this)`. |
| `LocationScene` | One instance, same or a separate GameObject in the Scripts scene. After placing every other authored object (BotZone, SpawnPointMarker(s)) as children of the Scripts scene, call `locationScene.FillArrays()` (public method, `LocationScene.cs:423-426`) **once, at editor build time**, via `AuthorScriptsScene.cs` — this auto-populates `BotZones`/`SpawnPointMarkers`/every other array field from `GetComponentsInChildren<T>` over the scene roots (`LocationScene.cs:487-489`'s `FindAllObjectsOnScene`), guaranteeing **every one of the ~30 array fields ends up a real, non-null (possibly empty) array** — the single hard requirement `spt-location-contract.md` §2 flags for `WorldInteractiveObjects` specifically (and the same null-safety applies to every other field by construction, since `FillArraysFrom` assigns all of them unconditionally, `LocationScene.cs:432-463`). | No manual field-by-field authoring needed — `FillArrays()` IS the SDK-support method BSG ships for exactly this. | `LocationScene.cs:201-263` (full field list, all `public T[] Foo;`), `:319-389` (`Awake()`, the NRE site for `WorldInteractiveObjects` specifically at `:381-388`), `:423-464` (`FillArrays`/`FillArraysFrom`). |
| `BotZone` | One instance, a GameObject named e.g. `BotZone_Main`, child of the Scripts-scene root (so `FillArrays()` above picks it up automatically for `LocationScene.BotZones`). | `PatrolWays = new PatrolWay[0]` (explicit empty array — **a hard-NRE field**, `BotZone.Init` does `PatrolWay[] patrolWays = PatrolWays; for (...patrolWays.Length...)` unconditionally, `BotZone.cs:138-139`, no null guard). **`SpawnPointMarkers = new List<SpawnPointMarker>()` — explicit empty list, MUST NOT be left `null`** (corrected this revision; empty is the right value, since bots are out of scope and `BotsController.cs:230-233`'s `list.Count<=0 → _canSpawn=false` is the expected outcome — but `null` crashes raid start, see below). `Id = 0` (only used for logging/lookup, not gated), `CanSpawnBoss = false`, `SnipeZone = false`, `_maxPersons = -1` (unlimited), `Modifier` assigned at runtime by `BotZone.Init`'s caller (`BotZone.cs:147`), not authored. `UnSpawnPoints = new UnspawnPoint[0]` (same null-unsafety pattern as `PatrolWays` is plausible but not confirmed traced this pass — set explicitly empty defensively, zero cost). | `BotZone.cs:11-171` (full file read this pass) — the exact NRE line for `PatrolWays` confirmed at `:138-139`. **`SpawnPointMarkers` correction**: `BotZone.cs:149`'s `if (SpawnPointMarkers != null && SpawnPointMarkers.Count > 0)` guard inside `BotZone.Init()` is real, but it is **not** the only read site — `SpawnPointsCollection.CalculateCanSpawnPmcBots` (`EFT.Game.Spawning/SpawnPointsCollection.cs:264`, `obj.SpawnPointMarkers.Any(...)`, no null check) is called unconditionally from `CreateFromScene` (`:168`), itself called unconditionally from `BaseLocalGame.Run:526` on **every** local raid start — before `BotsController.Init` is even reached — and `BotsController.Init` itself (`EFT/BotsController.cs:225`, `botZone.SpawnPointMarkers.Count != 0`, no null check) is a second independent unguarded site. `List<T>.Any()`/`.Count` on a null `List<T>` throws `NullReferenceException` immediately. `LocationScene.FillArrays()` does **not** cover this — it only populates `LocationScene`'s own, separate `SpawnPointMarkers` field (`LocationScene.cs:441`), never `BotZone.SpawnPointMarkers` (`BotZone.cs:43`). Confirmed by direct read of all four cited call sites this revision; see §H risk #11 and "Review resolution". |
| `LocationExportInfo` | One instance, any object in the Scripts scene. | `_unixDateTime` = a Unix timestamp **at or before** the value WP5 puts in `base.json`'s `UnixDateTime` field (§E.4) — for the spike, set both to the same build-time timestamp (e.g. `DateTimeOffset.UtcNow.ToUnixTimeSeconds()` captured once and hardcoded at WP2/WP5 coordination time, not regenerated per-build, so the two never drift). **Only matters if `base.json`'s `SpawnPointParams` is non-null** (§E.4/critic.md §6 — kept `null` for the spike, so this component is authored defensively but not load-bearing yet). | `EFT/LocationExportInfo.cs:6-22` (full file, 3 fields total — trivial). `spt-location-contract.md` §2's table row + §3's "DateTime rule" explanation. |
| `EFT.Game.Spawning.SpawnPointMarker` | **At least one** instance — a GameObject named e.g. `SpawnPoint_Player_01`, positioned at a location known-inside the playable area (cross-check against §C.7's `anchors_reference` sample positions — pick a position near one of Arena's own real spawn points, since those are guaranteed to be on walkable, in-bounds ground), child of the Scripts-scene root, with a `BoxCollider` (e.g. size `(1,2,1)`, centered) also attached and **enabled**. | `_collider` = the `BoxCollider` reference (assign in the inspector/editor script — `Start()` logs an error, not a crash, if left null and none is found via `GetComponent`, `SpawnPointMarker.cs:103-114`, but author it correctly anyway). `_spawnPoint` = a new `SpawnPoint { Id = <stable GUID string>, Name = <object name>, Position = <world pos>, Rotation = Quaternion.identity, Sides = EPlayerSideMask.All (7), Categories = ESpawnCategoryMask.Player (1), Infiltration = null, BotZoneName = "" (no BotZone linkage needed — player spawn selection is global, not BotZone-scoped, per `spt-location-contract.md` §3 point 1), DelayToCanSpawnSec = 4f (field default), CorePointId = 0 }`. **`BotZone` linkage is not required** for the player-spawn success criterion — `SpawnPointsCollection.CreateFromScene` finds markers via a global `FindObjectsProxy.FindUnityObjectsOfType<SpawnPointMarker>()` scan, independent of any `BotZone`'s own list (`spt-location-contract.md` §3 point 1). Author **2–4** markers in practice (one per side/category combination is cheap insurance — e.g. one `Sides=All`, and dedicated `Usec`/`Bear` ones matching whatever test profile side is used in §G.S4), not just one, since a raid with zero *matching* markers for the test profile's side is a silent dead-end (not a crash, `spt-location-contract.md` §2's table, last "logical dead-end" row). | `EFT.Game.Spawning/SpawnPointMarker.cs:1-470` (full file), `SpawnPoint.cs:1-270` (full file, the serialized data class `_spawnPoint` wraps), `EPlayerSideMask.cs`/`ESpawnCategoryMask.cs` (full enum values, both `[Flags]`). |

**Explicitly NOT authored for the spike** (all confirmed **soft** per `spt-location-contract.md` §2's
table — degrade gracefully, no crash): `ExfiltrationPoint`, `EnvironmentManagerBase`, camera/
post-process prefabs, `LevelPhysicsSettings`. None of these block "player can walk and shoot."

---

## E. Bundles, scenes, preset, `base.json`, locale, menu visibility

### E.1 Scene set (final — a deliberate reduction from Arena's own 9-scene TDM set)

| Scene (final name) | Content | Source | In WP1 scope? |
|---|---|---|---|
| `Arena_Equator_Scripts` | 5 required EFT components only (§D) | WP2-authored fresh | **No** — never extracted |
| `Arena_Equator` | main geometry | `level51`/`sharedassets51` (+ shared-pool externals) | Yes |
| `Arena_Equator_Lighting` | realtime lights | `level55`/`sharedassets55` | Yes |
| `Arena_Equator_DesignStuff` | base dressing/props | `level53`/`sharedassets53` | Yes |
| `Arena_Equator_TeamFight` | TDM-mode geometry overlay | `level58`/`sharedassets58` | Yes |
| `Arena_Equator_TeamFight_DesignStuff` | TDM-mode dressing | `level57`/`sharedassets57` | Yes |

Dropped from Arena's own 9-scene TDM union (`arena-census.md` §1): `Arena_Equator_Sound` (audio, out of
scope decision #8), `Arena_Equator_TeamFight_Culling` (PerfectCulling/occlusion data, out of scope
decision #8), `Arena_Equator_TeamFight_AI` (NavMesh + Arena-custom spawn/zone MonoBehaviours — NavMesh is
re-baked fresh into a *separate, non-scene* bundle per decision #6, and the custom field data on this
scene's markers is unextractable per `critic.md` §2c anyway, so nothing of value is lost by not porting
this scene's content).

### E.2 Bundle contents

| Bundle (served key) | Built by | Contents |
|---|---|---|
| `maps/arena_equator_preset.bundle` | WP3 | One `EFT.ScenesPreset` MonoBehaviour, patched from `develop_preset.bundle` (§B.WP3) |
| `arena_equator/arena_equator_scenes.bundle` | WP2 | The 6 scenes in §E.1, all meshes/materials/textures/colliders/lights/LODGroups they reference |
| `arena_equator/arena_equator_navmesh.bundle` | WP2 | One `NavMeshData` asset (fresh bake, agent 0) |

### E.3 `ScenesPreset` fields set (WP3, restated from §B.WP3 for a single reference table)

| Field | Value |
|---|---|
| `ServerName` | `"arena_equator_tdm"` |
| `_activeSceneName` | `"Arena_Equator_Scripts"` |
| `ActiveSceneGuid` | inert, left as donor's |
| `ChildPresets` | `[]` |
| `_scenesResourceKeys` | 6 entries, exact order/`rcid` per §B.WP3 step 3's table, each `path = "arena_equator/arena_equator_scenes.bundle"`, `_onlyOffline = 0` |
| `ScenesGuids` | trimmed/padded to 6 entries (donor values where available; unused for scene identity/gating, but read by `GetTotalSceneCount()` for the loading-progress-bar denominator, `ScenesPreset.cs:95-98`) |

**Note**: an earlier revision of this table also listed a `GameModes` field — `EFT.ScenesPreset`
(`D:/SPT41/Development/_decompiled/EFT/ScenesPreset.cs:10-99`) has no such member; see §B.WP3 step 6.

### E.4 `base.json` template

Derived from `develop`'s own on-disk `base.json` shape (same "small, real, minimal" donor reasoning as
WP3's bundle donor) — every field the load chain actually reads is set explicitly; everything else
inherits sane empty/false/0 defaults per `JsonType.LocationSettings.Location`'s ~90-field shape
(`spt-location-contract.md` §5.1, no field is `[Required]` at the C# level, missing = default).

| Field | Value | Why |
|---|---|---|
| `Id` (wire JSON key `"Id"`) | `"arena_equator"` | §0. The location key — `LocationTable` dictionary key, folder-safe name. Distinct from `IdField`/wire `"_Id"` below (separate `[JsonPropertyName]`s on the server model). |
| `IdField` (wire JSON key `"_Id"`) | `"e9b8e12c60dfd2820e949462"` | §0. Used for exfil MongoID derivation, **and** this — not `Id` — is what the client's `location._Id` deserializes to and looks locale keys up by (`"<_Id> Name"`/`"<_Id> Description"`, see corrected §E.5). |
| `Name` | `"Arena: Equator (spike)"` | display only |
| `Scene.path` | `"maps/arena_equator_preset.bundle"` | §0 |
| `Scene.rcid` | `"arena_equator.scenespreset.asset"` | §0 |
| `Enabled` | `true` | required for the location to be handed out / selectable — `LocationController.GenerateAll` doesn't filter server-side, but the client's `Enabled`/`Locked` booleans are what the map-select UI gates on (`spt-location-contract.md` §5.4) |
| `Locked` | `false` | see above |
| `UnixDateTime` | a fixed build-time timestamp, matching §D's `LocationExportInfo._unixDateTime` | |
| `SpawnPointParams` | **absent from the JSON (`null`), never `[]`** | critic.md §6 — an empty array actively deletes every scene-authored marker on load (`FixSpawns`, `spt-location-contract.md` §3); omitting the field entirely skips the whole reconciliation branch, scene-baked markers stand alone |
| `waves` / `BossLocationSpawn` / `MinMaxBots` / `BotLocationModifier` | omit / empty | optional, empty-tolerant (`spt-location-contract.md` §5.1) — bots out of scope |
| `DisabledForScav` | `true` | §B.WP5 step 4 |
| `Banners` | `[]` | §B.WP5 step 7 — avoid the unresolvable-MongoId landmine |
| `MinPlayers` / `MaxPlayers` | `1` / `1` | spike is single-player local raid only |
| `AveragePlayTime` | a plausible number, e.g. `10` | shown on the map-select card timer (`ref-manimal.md` §1 — "AveragePlayTime + MIN", not `EscapeTimeLimit`) |
| `EscapeTimeLimit` | e.g. `15` (minutes) | the actual raid length enforced server-side |
| `exits` / `AllExtracts` | `[]` | exfils out of scope |
| `IconX` / `IconY` | any value in-bounds of the map-select screen art, e.g. `500`/`500` | cosmetic only, doesn't block the spike's success criterion |

### E.5 Locale keys (WP5, §B.WP5 step 6)

Keyed by `IdField` (wire `"_Id"`, the MongoId), **not** by the location key `Id` (`"arena_equator"`) — see
§B.WP5 step 6 and §E.4's `IdField` row for the decompiled evidence. (Corrected fix-cs pass, 2026-09-13;
was previously, and wrongly, `"arena_equator Name"`/`"arena_equator Description"`.)

| Key | Value |
|---|---|
| `"e9b8e12c60dfd2820e949462 Name"` | `"Arena: Equator (spike)"` |
| `"e9b8e12c60dfd2820e949462 Description"` | `"Internal test build — geometry/collision/NavMesh spike."` |

### E.6 Menu visibility

No extra work beyond `Enabled: true` / `Locked: false` in `base.json` (§E.4) — `LocationController.GenerateAll`
returns every entry with a non-null `Base` regardless of those flags (`spt-location-contract.md` §5.4),
and the client-side map-select screen (`MatchMakerSelectionLocationScreen.ShowInternal`, cited
`ref-manimal.md` §1, not independently re-traced this pass) builds a button for every enabled location in
`session.LocationSettings.locations.Values` — no allowlist/catalog registration needed anywhere (this is
explicitly **not** an ArenaMode integration, decision #8, so `ArenaMapCatalog.IsSupportedMap` etc. is
irrelevant — the spike's raid is started as a plain SPT local raid against the new location id, not
through ArenaMode's flow).

---

## F. Client plugin design

**F.0 — Target framework**: `netstandard2.1` (corrected from the brief's `net472` shorthand — see §B.WP4).

**F.1 — Bundle hosting.** Exact hook, verified against **this project's own decompiled 4.1 client**
(`D:/SPT41/Development/_decompiled/EFT.AssetsManager/BundlesManager.cs`, full file read this pass) —
**simpler than Icebreaker's own implementation**, which targeted live retail EFT where the equivalent
members are obfuscated/private (`icebreaker-client/IcebreakerBundleHost.cs`'s own comments reference
`AccessTools.Field`/reflection-built cache entries because retail's `_bundles` field and its entry type
are private/obfuscated there). On **this** decompiled build, `BundlesManager._bundles` is a **public**
field (`Dictionary<string, AssetBundleReference>`) and `AssetBundleReference` is a **public** nested
class with a public `(AssetBundle)` constructor (`BundlesManager.cs:14-25,113`) — no `AccessTools`
reflection is needed at all:

```csharp
[HarmonyPatch(typeof(BundlesManager), nameof(BundlesManager.LoadBundleAsync))]
[HarmonyPrefix]
static void Prefix(BundlesManager __instance, string bundleName)
{
    if (!OurBundles.TryGetValue(bundleName, out var filePath)) return;   // case-insensitive dict, §0's 3 keys
    if (__instance._bundles.ContainsKey(bundleName)) return;             // already served/loaded
    var bundle = AssetBundle.LoadFromFile(filePath);                     // memory-mapped, cheap
    if (bundle == null) { Plugin.Log.LogError($"[ArenaMapPort] failed to load {filePath}"); return; }
    __instance._bundles[bundleName] = new BundlesManager.AssetBundleReference(bundle);
}
```
`BundleLoadOperation.Run` (`BundlesManager.cs:64-82`) checks `manager._bundles.TryGetValue(bundleName,
...)` as its **very first** line, before anything network-related — so this prefix, running before the
original method body, guarantees the static `.Run` call that follows sees the entry already populated
and returns immediately with `Result = bundle`, never reaching the `DownloadingUrl` fetch branch
(`BundlesManager.cs:64-109`). This is the exact mechanism Icebreaker documents (`IcebreakerBundleHost.cs:18-34`'s
header comment) but with the extra reflection layer removed since it isn't needed on this build. Register
the prefix in `Plugin.Awake()`. `OurBundles` maps all 3 served keys (§0) → their absolute file paths under
`Path.GetDirectoryName(typeof(Plugin).Assembly.Location)/bundles/`.

The scene bundle and navmesh bundle are then found by Unity's own `SceneManager`/`AssetBundle.LoadAllAssets`
"any loaded bundle, by name/by type" resolution — no further hook needed for scene loading itself
(confirmed pattern, `ref-interchange.md` §1/§2, `ref-manimal.md` §2 Labs section: *"the scene itself is
then loaded BY NAME ... which works for any loaded bundle regardless of who loaded it."*).

**F.2 — NavMesh.** Direct, near-verbatim port of `InterchangeRework.Client/NavMeshExtension.cs`
(`ref-interchange.md` §5, full mechanism read and confirmed) — a Harmony prefix on `BotsController.Init`:
```csharp
[HarmonyPatch(typeof(BotsController), nameof(BotsController.Init))]
[HarmonyPrefix]
static void Prefix()
{
    if (!ArenaEquatorRaid.IsLoaded()) return;         // gate: is Arena_Equator_Scripts currently loaded?
    var data = LoadNavMeshDataOnce();                  // lazy-load arena_equator_navmesh.bundle, kept
                                                         // alive via HideFlags.DontUnloadUnusedAsset
    NavMesh.RemoveAllNavMeshData();                    // order matters — remove BEFORE add, or ours
    _instance = NavMesh.AddNavMeshData(data);           // gets dropped along with theirs (InterchangeRework's
                                                         // own documented ordering note, NavMeshExtension.cs:41-53)
}
```
`BotsController.Init` runs unconditionally on every raid start regardless of bot count (it's called from
`LocalGame.vmethod_1`, `EFT/BotsController.cs:168-219`, cited `spt-location-contract.md` §1 step 7) —
this fires even for a raid with zero configured bots, so it's a reliable, raid-start-adjacent hook point
for the spike's "player can walk on the NavMesh-covered floor" criterion even though bots themselves are
out of scope. `IsLoaded()` gate mirrors InterchangeRework's `IrRaid.IsLoaded()` (`ref-interchange.md` §5)
— checks whether `Arena_Equator_Scripts` is among `SceneManager`'s currently-loaded scenes, so this
prefix is a complete no-op for every other map's raids (same "coexists with vanilla, zero cost elsewhere"
property as the bundle hook). Cleanup on `SceneManager.sceneUnloaded` for that scene name, same as
`NavMeshExtension.cs:35-39`.

**F.3 — Shader rebind.** Direct port of the convergent Icebreaker/InterchangeRework pattern (§F.3
mechanism identical in both, `ref-manimal.md` §3, `ref-interchange.md` §3): on `SceneManager.sceneLoaded`
for each of the 6 owned scenes, walk every `Renderer.sharedMaterials`, and for each material whose
current shader name matches one of WP2's stand-in shader names, `material.shader = Shader.Find(name) ??
ShadersFinder.Find(name)` — **for this spike, `Shader.Find` alone is expected to suffice**: Equator's 20
distinct shader names are already confirmed to exist in SPT's own `shaders` bundle (project context,
corroborated independently by `arena-census.md` §6's "0 unresolved shader refs" and the observation that
all names are standard `p0/*`/`Decal/*`/`Global Fog/*` families, no Arena-exclusive shader family found)
— so, unlike InterchangeRework's retail-1.1-rip case (which needed a hard-coded `Substitutes` table for a
handful of genuinely-foreign shader names, `ref-interchange.md` §3 point 2's third bullet), **no
substitution table is needed for the spike's material set**. Run the rebind pass twice per raid (at scene
load, and again ~2s later) to catch any late-instantiated material, matching both reference
implementations' defensive double-pass (`ref-manimal.md` §3 "~2s/~6s into the raid", `ref-interchange.md`
§3 point 3's "+5s and +25s" sweeper) — for the spike, two passes (immediate + 3s) is sufficient; the
fuller multi-pass sweeper is a stretch goal, not required for "renders correctly."

**F.4 — Runtime component bootstrap: none required.** Because §D authors all five required components
directly in the SDK (not at runtime), WP4 has **no** `LocationScene`/`BotZone` repair code to write —
this is a deliberate divergence from Icebreaker (which never wired `LocationScene`'s arrays at all and
patches around the consequence at runtime, `ref-manimal.md` §5) and from Labs (which rebuilds
`LocationScene` reflectively at `sceneLoaded`, `ref-manimal.md` §5's "Labs' approach is the more complete
fix and directly reusable" note) — **both exist because their source content was a rip that didn't carry
these arrays populated.** Our content is authored fresh with `FillArrays()` already called at build time
(§D), so there is nothing to repair at runtime. If a future milestone re-introduces extracted
`LocationScene`-relevant content (e.g. real `WorldInteractiveObject`/`LootableContainer` instances) that
this spike doesn't have, **Labs' reflective runtime-rebuild pattern is the recommended reference** at that
point (`LabsBoilerLocationSceneFix.cs`, cited `ref-manimal.md` §5) — noted here for forward reference,
not implemented now.

**F.5 — Diagnostics.** Log at `Info` level: which of the 3 bundles loaded (path + byte size), scene load
confirmation per scene name, NavMesh tile/vertex count after `AddNavMeshData` (mirrors
`NavMeshExtension.cs`'s own "logging vertex/triangle counts before/after", `ref-interchange.md` §5), shader
rebind pass summary (`[RebindShaders] Nx <name>` per InterchangeRework's own documented "only ground
truth" log line, `ref-manimal.md` §3 point 7). No config beyond a `LogLevel` toggle — no player-facing
settings for the spike.

**F.6 — PerfectCulling risk handling.** Per `spt-location-contract.md` §2's table (flagged **[OPEN,
potential hard requirement]**) and `critic.md` §2b (unresolved by any prior pass, "carry forward as the
top runtime risk to test first"): `PerfectCullingCrossSceneSampler.InitializeAutoCulling` is wrapped in a
catch-and-rethrow as `"Loading data file failed"` (`TarkovApplication.cs:2761-2770`) — if a scene set with
**no** baked occlusion data (ours has none — decision #8 explicitly excludes a PerfectCulling bake) causes
this to hard-abort the whole `LoadMapAndData`, the spike cannot start at all, regardless of how correct
everything else is. **Down-weighted this revision** (spt-runtime review): a full read of the method body
(`Koenigz.PerfectCulling.EFT/PerfectCullingCrossSceneSampler.cs:500-522`) found no code path that throws
specifically because occlusion data is absent — `AllRuntimeCrossGroupVolumes.Count>0` guards stale state,
not absence; `PerfectCullingCrossSceneGroup.AllCrossGroups` empty makes the sync foreach a no-op; and
`PerfectCullingAdaptiveGrid.Instance == null` (no baked grid ever registered) skips `InitializeGridAsync`,
the only branch that plausibly touches baked-data files. This is no longer treated as the single top
runtime risk — §H risk #11 (`BotZone.SpawnPointMarkers`) is a confirmed, unconditional crash that fires
earlier in the raid-start sequence (`BaseLocalGame.Run`, before `LoadMapAndData`'s PerfectCulling call
even matters) and is the correct first triage target if S4 fails to start a raid. **Mitigation, following
Labs' documented "just strip it" posture** (`ref-manimal.md` §4, the explicitly recommended scope match
for a spike: *"Labs' 'just strip PerfectCulling, run everything unculled' posture is the right scope match
... the full PerfectCullingRuntime.dll recompilation is real, working, but substantial standing
infrastructure that only earns its cost once the map is otherwise playable."*): WP4 still adds a Harmony
prefix on `PerfectCullingCrossSceneSampler.InitializeAutoCulling` that, when `ArenaEquatorRaid.IsLoaded()`,
**skips the original body entirely** (returns `false` from the prefix) — cheap and harmless even though
the risk it guards against is now assessed as lower-probability than previously stated. See §G.S4's
checklist (now ordered with the `BotZone` check first).

---

## G. Milestones with offline acceptance checks

### S1 — Smoke round-trip (~50 objects)

**Goal**: prove the WP1→WP2→bundle→UnityPy-re-read chain works end to end before investing in the full
6-scene extraction — this is `critic.md` §2a's explicitly-demanded prerequisite (*"do a single, minimal
round-trip test ... before investing in a full extractor for 880k vertices."*).

**Procedure**: WP1 extracts a ~50-object slice of `Arena_Equator` (e.g. one contiguous cluster of props
with a representative mix of mesh/material/collider/light types — not cherry-picked to avoid every hard
case, deliberately include at least one multi-submesh mesh, one convex `MeshCollider`, one non-convex
`MeshCollider`, one `Light`, one `LODGroup`) into the same package format (§C) as the full run. WP2 runs
its complete pipeline (import → stand-in shader → bundle build, skip the NavMesh bake for this slice
unless trivial to include) against just this slice, **including at least one `Arena_Equator_Scripts`-style
object carrying a `GameRefs`-sourced `Assembly-CSharp` MonoBehaviour (§B.WP2.1/§D), built into the same
`BuildTarget.StandaloneWindows64`-targeted bundle** — this is currently an asserted-but-untested
mechanism (no citation or precedent for "Editor-only-plugin MonoBehaviour serialized into a Standalone
AssetBundle" appears in `ref-manimal.md` or `ref-interchange.md`), and all five of §D's required
components depend on it working. WP6's `validate_bundle_roundtrip.py` re-reads the resulting bundle with
UnityPy and asserts: object count matches the slice's manifest, every mesh's vertex count matches, every
material's shader name matches (not `Hidden/InternalErrorShader`), every collider's `convex`/`isTrigger`
flags match, **and the GameRefs MonoBehaviour's `m_Script` PPtr and serialized field data survive the
round trip** — if they don't, mark the DLLs in `GameRefs/` compatible with `Standalone` (not Editor-only)
in `tools/sync-game-refs.ps1` and re-test before WP2 proceeds to the full 6-scene build.

**Pass condition**: all of the above, plus a **manual** open of the built bundle in the actual
`UnityProject` Editor (drag the `.unity` scene open, or `AssetDatabase.LoadAssetAtPath` it) showing
recognizable, correctly-shaped geometry with no console errors — a human look, not just an automated
count-match, since a byte-shuffled-but-count-correct mesh is a realistic failure mode this early.

### S2 — Full Equator TDM build

**Goal**: the full 6-scene set (§E.1) extracted, imported, NavMesh-baked, bundled.

**Checks**: `validate_extract.py` + `validate_bundle_roundtrip.py` (§B.WP6 table) both pass against the
full package; NavMesh tile count is within the sanity range noted in §B.WP2's acceptance criteria;
Editor Console has zero errors/exceptions during the full headless
`-executeMethod ArenaMapPort.Editor.BundleBuild.BuildBundles.Run` pass (grep the `-logFile` output for
`Exception`/`Error` — the workspace's own batchmode convention makes this a scriptable check, not a
manual one).

### S3 — Plugin + server compile + dist layout

**Goal**: both halves build clean and land in the right `dist/` shape (decision #9 — dist/ mirrors
`BepInEx/plugins/ArenaMapPort` and `SPT_Runtime/user/mods/ArenaMapPort`, nothing writes into `D:/SPT41`
directly from any build step).

**Checks**: `dotnet build` (or the client's normal MSBuild path) exits 0 for both
`ArenaMapPort.Client.csproj` and `ArenaMapPort.Server.csproj`; WP5's xunit test (§B.WP5 acceptance
criteria) passes; a post-build listing of `dist/**` matches the exact expected file set (client DLL +
3 bundles under `BepInEx/plugins/ArenaMapPort/`; server DLL + `db/base.json` under
`SPT_Runtime/user/mods/ArenaMapPort/`) — no stray files, no missing ones.

### S4 — In-game test checklist (for the user; cannot be automated)

A **separate, explicit, user-approved step** promotes `dist/` into the live `D:/SPT41` install (decision
#9 — this is not run automatically by any agent). Once promoted:

1. **Start the server, watch for the registration log line** (`"[Manimal-ArenaMapPort]... registered as
   an independent location"`-equivalent, §B.WP5 step 8's final log) and confirm **no** exception during
   `OnLoadAsync` — this alone validates WP5's whole registration chain including the loot-LazyLoad shape
   (§B.WP5 step 3) without needing a raid yet.
2. **Open the client, go to the map-select screen, confirm "Arena: Equator (spike)" is listed and
   selectable** — validates §E.4/§E.5/§E.6 (locale keys, `Enabled`/`Locked`, no `[UNHANDLED]` banner
   error in the server log for the empty `Banners: []`).
3. **Start a local PMC raid on it. Watch the client log (`D:\SPT41\BepInEx\LogOutput.log`, local time —
   per `ArenaMode/CLAUDE.md`'s own documented convention) for, in order**:
   - `[ArenaMapPort][Bundles] loaded 'maps/arena_equator_preset.bundle'` / the scene bundle / the navmesh
     bundle (§F.1/F.5) — confirms the `BundlesManager` prefix actually intercepted all three.
   - All 6 scene names loading (Unity's own `"Loading level..."`-style engine log lines, or WP4's own
     per-scene confirmation, §F.5) — **if any scene is silently absent, re-check §B.WP3's `rcid` table
     for a typo first** (the single highest-leverage failure mode flagged in §B.WP3's edge cases).
   - **No** `"Loading data file failed"` exception around scene-load completion — this is the
     PerfectCulling risk (§F.6); if it appears, §F.6's prefix isn't engaging, check the Harmony patch
     target resolved (`AccessTools.Method` didn't silently return null).
   - `[ArenaMapPort][NavMesh] added N tiles` (§F.2/F.5).
   - `[RebindShaders] Nx <name>` lines for each of the 20 distinct shaders, with **zero** materials left
     on `Hidden/InternalErrorShader` (§F.3/F.5) — visually, this means no magenta/checkerboard surfaces
     anywhere in view at spawn.
4. **Visual/gameplay check**: the player spawns standing on solid ground (not falling through, not
   floating — validates collider layer/placement fidelity, `ref-interchange-verify-F7.md`'s confirmed
   "no layer remap needed" finding, applied for the first time to real Arena-sourced geometry rather than
   only reasoned about), can walk across the Equator courtyard's open area without falling through floors
   or clipping through walls the census shows as non-convex `MeshCollider`s, can fire a weapon (ballistics/
   hit-detection sanity — not a focus of the spike's own scope list, but a free confirmation that nothing
   about the import pipeline corrupted collider layers in a way that breaks hit registration).
5. **Report back**: which of steps 1–4 failed first, and the exact log line/screenshot — this is the
   single most valuable piece of information for triaging which WP's assumption broke, since every step
   above is deliberately ordered to isolate one WP's contribution (registration → menu → bundle-serving →
   scene-load → NavMesh → shaders → physical geometry).

---

## H. Risk register

| # | Risk | Probability (this pass's assessment) | Probe | Fallback |
|---|---|---|---|---|
| 1 | **Unity 6 IL2CPP → 2022.3.43f1 Mono round-trip for real geometry is unproven** — every technique here is reasoned from a same-Unity-family Mono→Mono precedent (InterchangeRework, Icebreaker), never demonstrated against an actual Arena rip (`critic.md` §2a, the single biggest open item across all four prior analysis passes). | Medium — UnityPy already reads Arena's format cleanly (confirmed, `arena-census.md` Method note 1) and all texture formats round-trip 100% (§5); the *unproven* part is specifically Unity Editor *re-import and bundle rebuild*, not raw data extraction. | S1 (§G) exists specifically to catch this early and cheaply, before the full 6-scene extraction. | If a specific object type fails re-import (e.g. a particular vertex-channel signature Unity's `Mesh` API rejects), narrow the failing case via S1's manual slice, fix `MeshImporter.cs`'s handling for that channel combination — the binary format (§C.2) already carries every channel Arena uses, so this should be a WP2 bug, not a fundamental blocker. |
| 2 | **`PerfectCullingCrossSceneSampler.InitializeAutoCulling` hard-aborts the whole raid load on a scene set with no baked occlusion data** (`spt-location-contract.md` §2, `critic.md` §2b — carried as **[OPEN]** by every prior pass; **down-weighted this revision** after a full read of the method body, `Koenigz.PerfectCulling.EFT/PerfectCullingCrossSceneSampler.cs:500-522` — for an empty-culling-data scene set, `AllRuntimeCrossGroupVolumes.Count>0` doesn't fire (guards stale state, not absence), `PerfectCullingCrossSceneGroup.AllCrossGroups` is empty so the sync foreach is a no-op, and `PerfectCullingAdaptiveGrid.Instance == null` skips the one branch (`InitializeGridAsync`) that could plausibly touch baked-data files — no path was found that throws specifically because occlusion data is absent). | Low-Medium for the *un-mitigated* case (down from Medium-High — no confirmed throw path found on direct read, but not proven impossible for every code path either); Low once §F.6's prefix ships regardless. | §G.S4 step 3's log-order checklist puts this check immediately after scene-load. | §F.6's prefix remains the primary plan (cheap, harmless even if unnecessary). **Not the top triage priority if S4 fails to start a raid** — risk #11 below (`BotZone.SpawnPointMarkers`) fires earlier in the sequence (`BaseLocalGame.Run`, step 6) than this method even runs (`LoadMapAndData`, before `BaseLocalGame.Run`), so check #11 first. |
| 3 | **`rcid` typo/mismatch between WP2's actual saved Unity scene names and WP3's patched preset entries** — silent (no exception), the scene simply never loads. | Medium (pure string-literal-discipline risk, six entries, easy to typo one). | WP6's `validate_preset.py` (§B.WP6 table) — an automated, mandatory cross-check between the two artifacts, run before S4. | Fix the mismatched string; re-run WP3 (cheap, no Unity rebuild needed since it doesn't touch the scene bundle). |
| 4 | **Custom `MonoBehaviour` field data (spawn Category/Side, `BotZone.PatrolWays`, `NavMeshModifier` overrides) is unextractable from Arena's shipped Player data** (confirmed this pass and in `critic.md` §2c — not a risk so much as a settled fact). | N/A — confirmed, not probabilistic. | N/A | §D's decision to hand-author fresh `SpawnPointMarker`/`BotZone` components entirely sidesteps this for the spike's own success criterion; NavMesh area-cost fidelity is explicitly deferred (§B.WP2.7's `NavMeshModifier` edge case) as a stretch goal, not a blocker. |
| 5 | **Texture format/compression edge case UnityPy can't decode** for an object outside the 9 samples already tested (`arena-census.md` §5). | Low (100% success rate on a representative 5-format sample; Equator's own pool spans only 4 formats). | WP1's edge-case handling (§B.WP1) already specifies a non-fatal placeholder + `manifest.json.errors` entry rather than a hard stop. | WP6's `errors == []` acceptance gate means the *build* still fails visibly if this occurs — it just doesn't lose the whole extraction run's other progress. |
| 6 | **Huge mesh / index-format edge case** (a mesh needing `UInt32` indices). | Very low — 100% `UInt16` observed across both full candidate maps (`arena-census.md` §4), and Equator specifically is the smaller of the two. | §C.2's binary format already reserves an `index_format` byte for this; §B.WP2.2 explicitly calls out not to silently rely on Unity's auto-promotion. | If hit, `MeshImporter.cs` sets `mesh.indexFormat = IndexFormat.UInt32` explicitly per the flag — already designed for, not a surprise fix. |
| 7 | **Shader rebind misses a material** (stand-in shader authored with a typo'd property name, or a genuinely-foreign shader name not in SPT's own set). | Low — Equator's shader list is a confirmed subset of SPT's own standard families, 0 unresolved in the census (`arena-census.md` §6). | §F.5's `[RebindShaders] Nx <name>` log line is the ground-truth check (per `ref-manimal.md` §3 point 7's own explicit warning: *"four identical-looking 'fix attempts' produced the same bug because none of them ever actually rendered; the log line is the only ground truth"*) — §G.S4 step 3 makes checking it mandatory, not optional. | If a specific material never rebinds, InterchangeRework's `Substitutes`-table pattern (`ref-interchange.md` §3 point 2, third bullet) is the documented fallback mechanism — not implemented for the spike since no case is currently known to need it, but the pattern is cited and ready to port if S4 surfaces one. |
| 8 | **Collider layer/PhysicMaterial values don't carry through this specific (Arena, not retail-EFT) rip pipeline correctly**, despite the strong same-codebase precedent (`ref-interchange-verify-F7.md`). | Low — layer *tables* are independently confirmed byte-identical between Arena and SPT (`ref-interchange-verify-F7.md` §2, re-derived from live `globalgamemanagers` reads on both clients, not just asserted), and a same-tooling-family rip (InterchangeRework's) demonstrably preserves both fields with semantically correct values (§3 of that same verify pass). The *specific* extrapolation from Mono-rip to IL2CPP-rip fidelity is the one residual, explicitly-flagged gap (`ref-interchange-verify-F7.md`'s own "Sharpened conclusion" final bullet). | §G.S4 step 4's visual/gameplay check (falls-through-floor, clips-through-wall) is the direct, cheap probe — a layer/collider fidelity bug is immediately, visibly obvious in a 30-second walk test. | If a specific object's layer is wrong, it is very likely an extractor read/write bug (WP1's `layer` field, §C.5) rather than a pipeline-wide problem, given the strength of the table-identity evidence — fix at the specific object, don't assume a systemic remap is needed. |
| 9 | **`StaticContainerDetails`'s field shape drifted** between the `4.1.0` reference package WP5's loot-shape code is modeled on and the live `4.1.5` binary (§B.WP5's explicit flag — not independently re-verified this pass, unlike `LocationTable` which was). | Low-Medium (untested this pass). | Compile-time — a drifted shape fails `dotnet build` immediately, not silently at runtime. | Re-derive the exact 4.1.5 field names via `ilspycmd` against the live `D:/SPT41/SPT_Runtime/SPTarkov.Server.Core.dll` (same technique `critic.md` §1 already used for `LocationTable`) — a WP5-owned, contained fixup, not a design change. |
| 10 | **`SpawnPointParams` accidentally shipped as `[]` instead of omitted/`null`** in `base.json` — actively *deletes* every authored `SpawnPointMarker` on load (critic.md §6, the one landmine explicitly called out as "easy to miss"). | Low (now explicitly documented in §E.4 and covered by WP6's `validate_server_db.py`, §B.WP6 table). | `validate_server_db.py` asserts the field is absent, not merely falsy. | Trivial JSON fix once caught — the check exists precisely so this is caught before S4, not during it. |
| 11 | **`BotZone.SpawnPointMarkers` left `null`** (the SPEC's own §D previously instructed this, believing `BotZone.Init`'s own null-guard was sufficient) **crashes raid start via two unconditional, unguarded read sites** — `SpawnPointsCollection.CalculateCanSpawnPmcBots` (`EFT.Game.Spawning/SpawnPointsCollection.cs:264`, `obj.SpawnPointMarkers.Any(...)`), called from `CreateFromScene` (`:168`) which `BaseLocalGame.Run:526` calls on every raid regardless of `SpawnPointParams`; and `BotsController.Init` (`EFT/BotsController.cs:225`, `botZone.SpawnPointMarkers.Count != 0`) — both independently confirmed this pass by direct read of the cited lines. `LocationScene.FillArrays()` does not touch this field (it only populates `LocationScene`'s own, separate `SpawnPointMarkers` field, `LocationScene.cs:441`). **Fixed this revision** — §D now instructs `BotZone.SpawnPointMarkers = new List<SpawnPointMarker>()`. | Was Medium-High (a guaranteed `NullReferenceException` on every raid start under the prior §D text); now Low — fixed in §D and covered by `validate_spawn_authoring.py`'s non-null assertion (§B.WP6 table). | `validate_spawn_authoring.py` asserts the authored `BotZone`'s `SpawnPointMarkers` is present (`[]`, not absent/null) alongside `PatrolWays`. | Was a one-line authoring fix, already applied. This was the single highest-severity, easiest-to-miss authoring gap found across both review passes — see "Review resolution" for the corrected §D text. |
| 12 | **`BuildAssetBundles` resolves to the wrong `BuildTarget`** (falls back to `EditorUserBuildSettings.activeBuildTarget`, not necessarily `StandaloneWindows64`, if left unspecified) — a bundle built for the wrong platform loads as `null` from `AssetBundle.LoadFromFile` at runtime, with no offline signal upstream of S4 under the prior spec text. **Fixed this revision** — §B.WP2.8 now pins `BuildTarget.StandaloneWindows64` explicitly. | Was Medium (an empty project's `activeBuildTarget` isn't guaranteed correct, and neither reference implementation documented this setting either, `ref-interchange.md:59,459`); now Low — pinned explicitly and covered by `validate_bundle_roundtrip.py`'s target-platform header check (§B.WP6 table). | `validate_bundle_roundtrip.py` re-reads the built bundle's serialized-file header via UnityPy and asserts `StandaloneWindows64`. | Was a one-line `BuildPipeline.BuildAssetBundles` argument fix, already applied. |

---

## Summary for the calling agent

Key design choices this spec makes beyond the brief's fixed decisions:

- **Registration**: confirmed via `critic.md`'s correction — genuinely new location id `arena_equator`,
  registered by `LocationTable.GetDictionary().Add(...)` in an `IOnLoad` mod, Icebreaker v1.1.0's exact
  pattern. Do not ride `suburbs`/`town`.
- **Required EFT components are hand-authored in the Editor using the real `Assembly-CSharp` types**
  (via Editor-only Plugin DLL references synced from the live SPT install), not typetree stubs and not
  runtime-created — safe specifically because this project's Editor is the same Unity version as the
  target Mono client. This resolves `critic.md` §2d's open mechanism question outright.
- **`LocationScene`'s ~30 array fields are populated for free** by calling BSG's own
  `LocationScene.FillArrays()` once at editor build time over a small, fully-authored `Scripts` scene —
  no runtime repair code needed (unlike every prior reference mod, which all had rip-damaged content to
  patch around).
- **NavMesh is re-baked fresh for agent 0, using SPT's own configured agent-0 values** (`spt_gm.json`'s
  radius 0.3/height 1.7/slope 48°/climb 0.38, explicitly written into this empty project's bake settings
  via `NavMesh.SetSettings`/`NavMeshBuilder.BuildNavMeshData` before baking — **not** the fresh project's
  own stock-default agent-0 entry, which is numerically different, §B.WP2.7), not ported from Arena's own
  custom-agent bake, then shipped in a separate bundle and added via `NavMesh.AddNavMeshData` in a
  `BotsController.Init` prefix — a direct, unmodified port of InterchangeRework's `NavMeshExtension.cs`
  mechanism, with zero agent-parameter *translation* needed (SPT's own bot code already expects these
  exact agent-0 values) once this one explicit settings-write step runs.
- **The `BundlesManager` hook needs no reflection** on this specific decompiled 4.1 build (`_bundles` and
  `AssetBundleReference` are public) — simpler than every reference mod's implementation, which all
  target live retail EFT's more obfuscated equivalent.
- **The scene set is deliberately reduced to 6** (5 extracted content scenes + 1 authored Scripts scene)
  from Arena's own 9-scene TDM union, dropping Sound/Culling/AI per the brief's own out-of-scope list —
  the AI scene's NavMesh is superseded by the fresh bake above, and its custom spawn/zone field data is
  confirmed unextractable anyway.
- **`base.json`'s loot fields must be non-null-but-empty**, correcting `spt-location-contract.md`'s
  "optional at the model level" framing against Icebreaker's own hard-won comment and the live
  `GenerateLocationAndLoot`→`GenerateLocationLoot` call chain.

Open risks carried forward, ranked by how much they could still invalidate the plan (see §H for full
detail): (1) the Unity-6→2022.3 geometry round-trip is unproven until S1 runs — the spec's whole
structure is built around getting S1's answer *before* committing to the full 6-scene extraction, and S1
now also covers whether a `GameRefs`-sourced `Assembly-CSharp` MonoBehaviour survives serialization into a
`StandaloneWindows64`-targeted bundle (§H risk #8, previously asserted, now an explicit S1 gate); (2)
collider layer/PhysicMaterial fidelity specifically for an *Arena* (IL2CPP) rip, as opposed to the
already-confirmed retail-EFT (Mono) case, remains the one extrapolation no report has closed — cheaply
falsifiable in 30 seconds of S4's walk test if wrong; (3) `PerfectCullingCrossSceneSampler` — down-weighted
this revision after a direct read of the method body found no path that throws for an occlusion-data-free
scene set (§H risk #2) — still pre-mitigated by design (§F.6) as a cheap defensive measure, but no longer
ranked as the top runtime risk. Two items that were carried as open risks through the prior revision are
now **fixed, not merely probed**: `BotZone.SpawnPointMarkers` left `null` (§D, §H risk #11 — was a
confirmed, unconditional crash on every raid start, found by the spt-runtime review) and an unpinned
`BuildTarget` on the AssetBundle build (§B.WP2.8, §H risk #12 — found by the unity-pipeline review); both
are now also covered by WP6 offline checks so a regression is caught before S4, not during it.

---

## Review resolution

Two review passes (`docs/reviews/SPEC-review-unity-pipeline.md`, `docs/reviews/SPEC-review-spt-runtime.md`)
were run against this spec. Every issue below was independently re-verified against the cited primary
sources (`D:/SPT41/Development/_decompiled`, `analysis/*.md`/`*.json`, `analysis/scripts/census.py`, and
the live 2022.3.43f1 Editor install) before being accepted or rejected — none was accepted on the
reviewer's citation alone. **15 of 15 issues accepted, 0 rejected.**

### unity-pipeline lens

| # | Severity | Section | Verdict | Resolution |
|---|---|---|---|---|
| 1 | Blocker | §B.WP2.2 | **Accepted** | Confirmed: a fresh `Mesh` defaults to `subMeshCount==1` and `SetIndices` throws past it; 241/1441 Equator meshes are multi-submesh (`arena-census.md` §4). Added explicit `mesh.subMeshCount = header.submesh_count;` before the per-submesh loop. |
| 2 | Blocker | §B.WP2.8 | **Accepted** | Confirmed zero `BuildTarget`/`StandaloneWindows64` mentions in the prior text. Pinned `BuildTarget.StandaloneWindows64` explicitly in §B.WP2.8; added a `validate_bundle_roundtrip.py` target-platform header check and risk-register row #12. |
| 3 | Major | §B.WP2.7 | **Accepted** | Confirmed: `NavMeshSurface.agentTypeID` reads *this project's own* `NavMeshAreas.asset` table, which for a fresh project holds Unity's stock default (0.5/2/45°/0.4), not `spt_gm.json`'s SPT-specific 0.3/1.7/48°/0.38 — re-verified both value sets directly. Added an explicit `NavMesh.SetSettings`/`NavMeshBuilder.BuildNavMeshData` step to write SPT's values into the empty project before baking, plus a WP6 back-check against `spt_gm.json`. |
| 4 | Major | §B.WP2.3/§B.WP2.4 | **Accepted** | Confirmed §B.WP2.3 (materials, needs textures) is written before §B.WP2.4 (textures) in document order, and `SetTexture(prop, null)` is a silent no-op. Added an explicit execution-order note ahead of §B.WP2.3 stating WP2.4 must run first. |
| 5 | Major | §C.6 | **Accepted** | Confirmed against Unity's real API: `LODGroup.fadeMode` is one group-level enum, `LOD.fadeTransitionWidth` is per-LOD-struct — the prior schema had them at the reverse levels. Swapped the schema and the §B.WP2.5 consuming text; 3,601 `LODGroup`s on Equator make this non-trivial. |
| 6 | Major | §B.WP2.5 | **Accepted** | `LODGroup.SetLODs()` stores direct `Renderer` references and silently accepts nulls for not-yet-attached siblings — a real single-pass-ordering hazard for 3,601 `LODGroup`s. Made `SceneAssembler.cs`'s two-pass requirement (all renderers, then all `LODGroup`s) explicit; added a WP6 null-renderer check. |
| 7 | Major | §B.WP1/§C.2 | **Accepted** | Confirmed by reading `census.py:156-159`: only `m_VertexData` is read, no `m_MeshCompression`/`m_CompressedMesh` handling anywhere in the reused code or the JSON census schema. Added a per-mesh `m_MeshCompression == 0` hard-fail assertion to §B.WP1 step 2, matching the existing `UNRESOLVED`-shader hard-failure pattern. |
| 8 | Major | §B.WP2.1 | **Accepted** | Confirmed this is asserted, not tested — no precedent for "Editor-only-plugin MonoBehaviour into a Standalone-target bundle" in either reference report. Softened §B.WP2.1's claim to state the open question explicitly and added a mandatory GameRefs-component round-trip check to §G.S1, with the documented fallback (mark DLLs `Standalone`-compatible) if it fails. |
| 9 | Minor | §B.WP2.7 | **Accepted** | Confirmed the package version was unpinned. Found the exact version bundled with this Editor install (`com.unity.ai.navigation-1.1.5.tgz` under the 2022.3.43f1 Editor's own `PackageManager/Editor` resources) and pinned it explicitly in §B.WP2.7. |
| 10 | Minor | §C.6 | **Accepted** | Confirmed `Rigidbody` was absent from both the component table and the explicit exclusion list despite 413 Equator instances. Added it to §C.6's exclusion list with the correlating convex-`MeshCollider` count noted. |
| 11 | Minor | §C.2/§B.WP2.2 | **Accepted** | Confirmed no explicit `RecalculateBounds()`/bounds-ordering statement existed. Added an explicit `mesh.RecalculateBounds()` step (not an implicit reliance on `SetIndices`' default parameter) in §B.WP2.2. |

### spt-runtime lens

| # | Severity | Section | Verdict | Resolution |
|---|---|---|---|---|
| 1 | Blocker | §D (`BotZone`) / §B.WP2.6 | **Accepted** | Re-confirmed by direct read of all four cited call sites this pass: `SpawnPointsCollection.CalculateCanSpawnPmcBots` (`SpawnPointsCollection.cs:264`, unguarded) is called unconditionally from `CreateFromScene:168`, itself called unconditionally from `BaseLocalGame.Run:526` on every raid; `BotsController.Init` (`BotsController.cs:225`) is a second unguarded site; `LocationScene.FillArrays()` only fills `LocationScene`'s own field, never `BotZone`'s. §D's `BotZone` row now authors `SpawnPointMarkers = new List<SpawnPointMarker>()` (was: left `null`); added a non-null assertion to `validate_spawn_authoring.py`; added risk-register row #11. |
| 2 | Major | §B.WP3 intro / step 6 | **Accepted** | Re-confirmed by reading the full `EFT.ScenesPreset` class this pass: no `GameModes` member exists, no subclass exists anywhere in the decompile. Removed the false "confirmed" `GameModes:null` claim from §B.WP3's intro, added an explicit re-dump-and-diff-against-the-real-field-set instruction before step 6, and removed the unconditional `tt['GameModes'] = []` write; corrected §E.3's field table to match. |
| 3 | Minor | §B.WP3 step 7 / §E.3 | **Accepted** | Re-confirmed `ScenesPreset.GetTotalSceneCount()` (`ScenesPreset.cs:95-98`) reads `ScenesGuids` for the loading-progress-bar denominator — "unused by the load chain" was overstated. Corrected the wording to "unused for scene identity/gating, but read for progress totals," and changed the blind `[:6]` slice to print the donor's actual length first and pad rather than silently no-op if shorter than 6. |
| 4 | Minor/informational | §F.6, §H risk #2 | **Accepted** (risk-register correction, not a spec defect) | Independently re-read the full `InitializeAutoCulling` method body this pass and found no path that throws specifically for an occlusion-data-free scene set, matching the review's finding. Down-weighted §H risk #2's probability and reworded §F.6 to note this is no longer the top runtime-risk triage target — risk #11 (`BotZone.SpawnPointMarkers`) fires earlier in the raid-start sequence and is now the primary S4 triage target if a raid fails to start. The §F.6 Harmony prefix mitigation itself is unchanged (cheap, kept as a defensive measure regardless). |

## Decisions 2026-09-20 (user, after the gap analysis — supersede the spike framing above where they conflict)

- **Target: ArenaMode.** The ported maps are meant to be played through the user's ArenaMode mod (arena
  deathmatch with respawns). Playability as ordinary SPT raid locations is NOT a goal — exfils, raid-end
  outcomes, insurance and the map-select flow only matter as far as the test vehicle (a plain SPT raid
  started from the menu) needs them.
- **No loot, on any map.** Decision #8 stands permanently: the location keeps the empty loot shape
  (`ArenaMapRegistration.BuildLocation`). Nothing loot-related is to be authored or extracted.
- **No scav raids.** `DisabledForScav = true` stays; no scav spawn categories, scav exits or scav hostility work.
- **Distribution: bundles are shipped.** The built `dist/` (BSG-derived bundles included) is distributed the
  way other SPT map mods do it; the user carries the licensing grey zone ("the player needs a valid EFT
  installation"). No build-it-yourself pipeline for end users is required. Bundles still never go into git.
- **Server target framework:** `ArenaMapPort.Server` builds for net10.0 against this SPT 4.1.5 install
  (WP5's "net9.0" above is the state at spec time).
- **Order of work:** stage 1 of docs/HANDOFF.md (combat/death correctness and the NavMesh bake on Equator),
  then the other maps one at a time through the Equator recipe (bots/exits sidecars + host raid), then parity
  items only where ArenaMode needs them.
