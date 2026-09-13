# SPEC review — Unity 2022.3.43f1 pipeline correctness

Reviewer lens: Unity 2022.3.43f1 editor/AssetBundle pipeline mechanics only (UnityPy read fidelity,
`Mesh`/`TextureImporter`/`Material` reimport APIs, stand-in shaders, LODGroup/Light/collider
recreation, NavMesh bake API, `BuildPipeline.BuildAssetBundles`, batchmode/`-executeMethod`,
MonoScript identity, determinism). Does **not** re-litigate the fixed decisions, the server
registration mechanism (`critic.md` §1), or the EFT component/NRE claims already sourced to decompiled
`_decompiled` citations — those are outside this lens. `docs/SPEC.md` was read in full (1158 lines) and
not edited. Evidence for the source-data claims below was re-derived directly from
`analysis/arena-census.md`, `analysis/spt_gm.json`, `analysis/equator_census.json` and
`analysis/scripts/census.py`, not merely re-quoted from SPEC.md.

**Verdict: needs-changes.** Two items (§1, §2 below) are outright implementation blockers as the
algorithm is literally specified — a competent WP2 implementer following the doc's own Mesh-API call
sequence and bundle-build instructions verbatim will hit a reproducible exception or a bundle that
silently fails to load at runtime, and neither is caught by any of WP6's planned offline checks. The
rest are real correctness/data-model gaps that should be fixed before WP2 starts, but don't block the
spike outright.

---

## 1. [BLOCKER] `MeshImporter.cs`'s Set* sequence never sets `mesh.subMeshCount` — will throw for every multi-submesh mesh

**Section**: §B.WP2.2 (`docs/SPEC.md:258-267`).

**Problem**: the algorithm is described as `mesh.SetVertices`/`SetNormals`/`SetTangents`/`SetColors`/
`SetUVs(channel, ...)`/`SetIndices(..., MeshTopology.Triangles, submesh)` per submesh, with no step
that sets `mesh.subMeshCount` first. Unity's `Mesh.SetIndices`/`SetTriangles` throw
`ArgumentOutOfRangeException` ("submesh index out of bounds") the moment you address a submesh index
`>= mesh.subMeshCount`, and a freshly-constructed `Mesh` defaults to `subMeshCount == 1`. Every mesh
whose `submesh_count` (§C.2 byte 15) is `> 1` needs `mesh.subMeshCount = submesh_count;` set **before**
the per-submesh `SetIndices` loop runs.

**Evidence**: `docs/SPEC.md:258-267` (no `subMeshCount` assignment anywhere in the described sequence,
confirmed by `grep -n "subMeshCount" docs/SPEC.md` → zero matches project-wide). The binary format
itself (`docs/SPEC.md:685-703`, esp. line 696 `submesh_count: uint8`) already carries the value needed
to fix this — it's just never wired into the Set-call sequence. Impact size, from
`analysis/arena-census.md:218` (`arena-census.md` §4 submesh-count distribution): Equator's
submesh-count histogram is `{1:1200, 2:135, 3:36, 4:31, 5:14, 6:15, 7:3, 9:7}` — **241 of 1,441 meshes
(16.7%)** have `submesh_count > 1` and will hit this exception under the algorithm as written.

**Fix**: add `mesh.subMeshCount = header.submesh_count;` immediately after `SetVertices` (before the
`SetUVs`/`SetIndices` loop) in `MeshImporter.cs`. Trivial, one line, but load-bearing for the stated S2
acceptance criterion "Editor Console has zero errors/exceptions" (`docs/SPEC.md:1045-1048`), which would
otherwise fail on the very first multi-submesh mesh WP2 processes.

---

## 2. [BLOCKER] `BuildPipeline.BuildAssetBundles` is never given an explicit `BuildTarget`

**Section**: §B.WP2.8 (`docs/SPEC.md:355-373`).

**Problem**: none of the three bundle-build bullets, nor anything else in the document, specifies the
`BuildTarget` argument to `BuildPipeline.BuildAssetBundles(outputPath, builds, options, target)`.
`grep -n "BuildTarget\|StandaloneWindows\|activeBuildTarget" docs/SPEC.md` returns **zero matches** in
the whole 1158-line document. Left unspecified, the call falls back to whatever
`EditorUserBuildSettings.activeBuildTarget` happens to be for this specific empty project at the moment
the headless `-executeMethod` runs — not necessarily `StandaloneWindows64`, which is what the actual
consumer (`EFT.exe` under BepInEx/SPT, a Windows standalone Mono player) requires. A bundle built for
the wrong `BuildTarget` fails to load at runtime (`AssetBundle.LoadFromFile` returns `null`, or Unity
logs a platform-mismatch error) — this is exactly the failure mode WP4's own bundle-hook code already
defends against generically (`docs/SPEC.md:919` `if (bundle == null) { Plugin.Log.LogError(...); return; }`)
but the SPEC never states the one build-time setting that prevents it from happening in the first
place.

**Evidence**: `docs/SPEC.md:355-373` (no `BuildTarget` mentioned); confirmed this is a genuinely open
question inherited from the reference implementations too, not something already answered elsewhere —
`analysis/ref-interchange.md:59,459` explicitly states *"BuildAssetBundleOptions / bundle naming: not
visible — that's inside the unpublished SDK"* / *"Exact BuildAssetBundleOptions and Editor version used
for the two bundles — genuinely unpublished."* WP6's `validate_bundle_roundtrip.py`
(`docs/SPEC.md:636`) only checks scene names/counts/NavMesh presence — it never re-reads the built
bundle's `m_TargetPlatform` header field, so a wrong-target build would sail through every offline
check and only surface at S4 (`docs/SPEC.md:1062-1099`), the single most expensive point in the
pipeline to discover it.

**Fix**: pin `BuildTarget.StandaloneWindows64` explicitly in `BuildBundles.cs` (matching the live
SPT/EFT client's actual platform), and add a `validate_bundle_roundtrip.py` assertion that the built
bundle's target platform matches (UnityPy exposes this on the bundle's serialized file header). Add
this as an explicit line item to §H's risk register — it currently has no entry for "bundle built for
wrong platform target" at all.

---

## 3. [MAJOR] NavMesh agent-0 settings are never configured in the (empty) Unity project before baking

**Section**: §B.WP2.7 (`docs/SPEC.md:329-353`, esp. `:334-339`).

**Problem**: the spec claims baking with `agentTypeID = 0` in the fresh project will use SPT's exact
values (`agentRadius=0.3, agentHeight=1.7, agentSlope=48, agentClimb=0.38`) "verbatim," and calls these
values *"simultaneously Unity's project default, SPT's own client default, and AutoService's own bake
settings"* (`docs/SPEC.md:335-337`). This conflates two different things: `com.unity.ai.navigation`'s
`NavMeshSurface.agentTypeID` selects an entry from **this project's own**
`ProjectSettings/NavMeshAreas.asset` agent table (via `NavMesh.GetSettingsByID`) — it does not import
SPT's numbers from anywhere. `UnityProject` is explicitly described as empty
(workspace intro: *"Unity project D:/ArenaMapPort/UnityProject (Unity 2022.3.43f1, empty, ...)"*), so its
agent-0 ("Humanoid") entry will hold Unity's own out-of-the-box default (radius 0.5, height 2, slope
45°, step 0.4 — the standard fresh-project NavMesh Agent 0 values), **not** SPT's
0.3/1.7/48/0.38. Nothing in §B.WP2.7 (or anywhere else in the doc) instructs WP2 to overwrite agent-0's
settings in this project before calling `surface.BuildNavMesh()`.

**Evidence**: the 0.3/1.7/48/0.38 numbers themselves come from `analysis/spt_gm.json:199-217`
(`NavMeshProjectSettings.m_Settings[0]`, `agentTypeID: 0`) — i.e. they are **SPT's own client project's**
configured agent-0 entry, read out of SPT's `globalgamemanagers`, not a generic Unity constant.
`analysis/arena-census.md:484-486` independently corroborates this framing, calling it *"AutoService's
bake instead uses agentTypeID 0, the project's stock default Humanoid settings (identical values to
**SPT's own default agent 0** in spt_gm.json...)"* — the census's own wording carefully says "SPT's own
default," never "Unity's engine default," which the SPEC's phrasing (`docs/SPEC.md:335`, *"Unity's
built-in 'Humanoid' default"*) blurs into a different, incorrect claim. `grep -n
"NavMesh.SetSettings\|GetSettingsByID\|NavMeshProjectSettings\|ProjectSettings/NavMesh"
docs/SPEC.md` returns zero matches — there is no step anywhere that writes these numbers into the
empty project.

**Impact**: the bake will silently succeed (no exception, no console error, `tile_count > 0` — passes
every WP6 check as written) but produce a NavMesh baked for the wrong agent capsule, which directly
contradicts the stated rationale for decision #6 ("zero agent-parameter translation work and an exact
match to what SPT's own bot code expects when bots are added later," `docs/SPEC.md:339-340`) and
undermines "NavMesh" as one of the spike's own named deliverables (`docs/SPEC.md:3-5`). It does not
block the player-walks smoke test (the player uses PhysX collision, not the NavMesh), which is why this
is major rather than blocker.

**Fix**: before `surface.BuildNavMesh()`, either (a) construct a `NavMeshBuildSettings` with the exact
`spt_gm.json` values and call `NavMesh.SetSettings(...)` for `agentTypeID 0` in this project (persists
for the batchmode session, which is all a one-shot bake needs), or (b) bypass `NavMeshSurface` entirely
and use the lower-level `UnityEditor.AI.NavMeshBuilder.BuildNavMeshData(buildSettings, sources, bounds,
position, rotation)` API, passing the `NavMeshBuildSettings` struct directly — this needs no project
agent-table mutation at all and matches this reviewer's lens's suggested API more literally. Either way,
add a WP6 check that reads the baked `NavMeshData`'s agent parameters back (UnityPy or an in-Editor
assertion) and diffs them against `spt_gm.json`, since nothing currently does.

---

## 4. [MAJOR] Material import is sequenced before texture import, but materials need already-imported textures

**Section**: §B.WP2.3 (`docs/SPEC.md:269-282`) vs §B.WP2.4 (`docs/SPEC.md:289-293`).

**Problem**: the document presents `MaterialImporter.cs` (§B.WP2.3, "applies every TexEnv (incl.
`_ST`)... `Material.SetTexture`" per the material's `tex_envs` block) as running **before**
`TextureImporter.cs` (§B.WP2.4, which is what actually turns `work/extract/equator/textures/<id>.png`
into a properly-configured `Texture2D` asset under `Assets/ArenaImport/...`). Every other subsection in
§B.WP2 is written as a literal, sequential how-to (2.1 → 2.2 → 2.3 → ... → 2.8), and the doc's own stated
purpose is that "implementer agents should not need to re-derive facts already established here"
(`docs/SPEC.md:8`) — so the ordering itself reads as part of the contract. If `MaterialImporter`
actually runs before the texture PNGs are imported as `Texture2D` assets, `Material.SetTexture(prop,
tex)` either receives `null` (texture asset not yet created/registered in the AssetDatabase) or a
texture with none of §B.WP2.4's corrected settings (sRGB/normal-map flags) applied yet, producing
materials with missing or wrongly-interpreted textures — silently: `SetTexture(prop, null)` is legal and
just clears the slot, no console error, no exception, so this would not be caught by the S2 "zero
errors" acceptance check (`docs/SPEC.md:1045-1048`), only by the S1 manual visual check
(`docs/SPEC.md:1034-1037`) if the reviewer happens to look closely at textures specifically.

**Evidence**: `docs/SPEC.md:269` (`**B.WP2.3 — Stand-in shaders + material import**`) precedes
`docs/SPEC.md:289` (`**B.WP2.4 — Texture import**`) in document order; §B.WP2.3's own text
(`docs/SPEC.md:280-282`) explicitly describes `Material.SetTexture`/`SetColor`/`SetFloat` calls against
material JSON's `tex_envs`, which reference `texture_id`s (`docs/SPEC.md:718` schema) resolved only by
§B.WP2.4's importer.

**Fix**: either reorder the two subsections (texture import before material import) or add an explicit
sentence stating the required execution order differs from document order — "run `TextureImporter`
before `MaterialImporter`, despite the numbering" — so an implementer following the doc literally
doesn't build materials against not-yet-existent texture assets.

---

## 5. [MAJOR] `LODGroup` schema has `fade_mode` and `fade_transition_width` at the wrong levels relative to Unity's real API

**Section**: §C.6 (`docs/SPEC.md:766`), consumed by §B.WP2.5 (`docs/SPEC.md:312-313`).

**Problem**: the component-record schema is `lods: [{screen_relative_height, renderer_object_ids,
fade_mode}], size, fade_transition_width` — i.e. `fade_mode` is recorded **per LOD entry** and
`fade_transition_width` is recorded **once, at the group level**. Unity's actual object model is the
opposite: `LODGroup.fadeMode` (a `LODFadeMode` enum: `None`/`CrossFade`/`SpeedTree`) is a **single,
group-level** property — there is no such thing as a different fade mode per LOD level within one
group — while `fadeTransitionWidth` is a field of the per-LOD `UnityEngine.LOD` struct
(`screenRelativeTransitionHeight`, `fadeTransitionWidth`, `renderers`), i.e. it legitimately **can**
differ between the LOD0→LOD1 and LOD1→LOD2 transitions and needs to be stored per-LOD, not once for the
whole group.

**Evidence**: `docs/SPEC.md:766` (the exact schema row); `docs/SPEC.md:312-313` describes the importer
consuming it as `"LODGroup (LOD array with renderer references + screen-relative transition heights,
size, fadeMode)"` — repeating the same (incorrect) grouping. Equator has 3,601 `LODGroup` components
(`analysis/arena-census.md:359`, §8.1 table), so this isn't a rare edge case.

**Impact**: as specified, an implementer has no correct way to populate Unity's real `LODGroup.fadeMode`
(the schema gives N per-LOD values for a property that only has one group-level slot — which one wins is
undefined) and no correct way to give different LOD transitions different crossfade widths (the schema
only carries one value for the whole group). Low runtime impact for a spike (`fadeMode` is very likely
`None` project-wide since decision #8 doesn't require crossfade parity), but it is a genuine
data-model/schema bug that WP1 and WP2 would otherwise implement against verbatim.

**Fix**: swap the schema — `fade_mode` moves out of the per-LOD list to a single scene-object-level (or
LODGroup-component-level) field; `fade_transition_width` moves into each `lods[]` entry alongside
`screen_relative_height`.

---

## 6. [MAJOR] LODGroup cross-object `Renderer` references have no specified build-order guarantee

**Section**: §B.WP2.5 (`docs/SPEC.md:295-313`).

**Problem**: `SceneAssembler.cs` is described as one pass — "instantiates one `GameObject` per node
... — then attaches `MeshFilter`/`MeshRenderer`... `MeshCollider`... `Light`... `LODGroup`"
(`docs/SPEC.md:295-313`). A `LODGroup`'s `lods[].renderer_object_ids` (§C.6, `docs/SPEC.md:766`)
reference **other** nodes' `MeshRenderer` components by id, which — per Unity's real `LODGroup.SetLODs`
API — must already exist as live `Renderer` components on already-instantiated GameObjects at the
moment `SetLODs()` is called (it stores direct object references, not late-bound ids). The spec doesn't
state that all renderers project-/scene-wide must be attached in a first pass before any `LODGroup` is
built in a second pass; a straightforward single-loop-per-node implementation (attach whatever
components this node has, in node order) will, for any `LODGroup` node that is processed before one of
the sibling/other nodes its LODs reference, silently assign a `null` renderer slot — Unity accepts this
without error (that LOD level just renders nothing), so it produces a silent, hard-to-spot pop/gap in
the LOD chain rather than a build failure.

**Evidence**: `docs/SPEC.md:295-313` describes the single "instantiate, then attach" pass with no
two-phase ordering note; 3,601 `LODGroup` instances on Equator (`analysis/arena-census.md:359`) means
this is not a rare case if it's wrong.

**Fix**: state explicitly that `SceneAssembler` must run in two passes — (1) instantiate every
GameObject and attach every `MeshFilter`/`MeshRenderer`/collider/`Light` across the whole scene, build
an `id → Renderer` map; (2) attach every `LODGroup`, resolving `renderer_object_ids` against that
completed map. Add a WP6 check that no built `LODGroup.GetLODs()` entry contains a null renderer.

---

## 7. [MAJOR] Mesh-compression (`m_MeshCompression`) is never checked — the census/extractor path reads the uncompressed `m_VertexData` layout unconditionally

**Section**: §B.WP1 step 2 (`docs/SPEC.md:136-140`), §C.2 (`docs/SPEC.md:685-703`).

**Problem**: this reviewer's lens explicitly covers "UnityPy reading of Unity 6000.3 Mesh... data
(vertex attribute streams, **compressed mesh**...)". Unity's `Mesh` typetree has an `m_MeshCompression`
byte (`0`=Off, `1..3`=Low/Medium/High): when non-zero, the actual geometry lives in a
`m_CompressedMesh` `PackedBitVector`-encoded struct, and `m_VertexData` (the field the tooling actually
reads) is degenerate/empty. Neither `analysis/arena-census.md` nor the JSON census outputs record this
field at all — confirmed by grepping `equator_census.json`'s top-level keys (`object_counts_per_file`,
`object_counts_total`, `static_batching`, `mesh_features`, `textures`, `materials`, `layers`,
`components`, `lightmap_settings`, `spawn_zone_anchors`, `navmesh` — no compression key anywhere) and by
reading `analysis/scripts/census.py`'s `mesh_features()`, which reads mesh data via
`t = obj.read_typetree(); vd = t.get("m_VertexData", {})` (`census.py:156-159`) — the raw,
uncompressed-only field — with no fallback path for `m_CompressedMesh`. WP1's planned extractor
explicitly reuses this exact script's patterns (`docs/SPEC.md:125-131`, "Reuses directly:
`analysis/scripts/census.py`'s `resolve_script_name()`... and `world_transform_of_transform()`"), and
§C.2's binary format (`docs/SPEC.md:685-703`) is written entirely in terms of plain `float32` position/
normal/tangent/color/uv arrays — i.e. it assumes the uncompressed layout project-wide, with no
per-mesh guard or hard-fail if a mesh turns out to be compressed (unlike the deliberately-designed hard
failure for `UNRESOLVED` shaders, `docs/SPEC.md:154-157`).

**Evidence for the mitigating side**: the one sampled raw typetree in `arena-census.md:237-241`
(`palm_plant_01_destroyed_LOD2`) shows populated `m_Channels`/`m_Streams` offsets (pos@0/normal@12/
tangent@24/uv0@40, `stream:0`) consistent with `m_MeshCompression == 0` for at least that mesh — but
this is one sample out of 1,441 Equator meshes, not a project-wide assertion, and nothing in the
pipeline would notice or fail loudly if a different mesh (e.g. a foliage/SpeedTree-adjacent asset from
the shared pool, `arena-census.md:235`'s 7-UV-channel outlier group) turned out to be compressed.

**Fix**: WP1's extractor should read and assert `m_MeshCompression == 0` per mesh, treating a non-zero
value the same way as the `UNRESOLVED` shader case — a hard extractor failure with the mesh id logged,
not a silent wrong/empty mesh — rather than assuming the uncompressed layout holds for the full
1,441-mesh set based on one sample.

---

## 8. [MAJOR] MonoScript resolution for GameRefs types into a Standalone-target scene bundle is asserted, not verified

**Section**: §B.WP2.1 (`docs/SPEC.md:232-256`, esp. `:243-249`).

**Problem**: `tools/sync-game-refs.ps1` imports `Assembly-CSharp.dll` etc. as a Unity **Plugin** asset
with `SetCompatibleWithEditor(true)` and **every other platform false**
(`docs/SPEC.md:246-249`), on the stated reasoning that this is *"irrelevant here since this project only
ever builds AssetBundles, never a Player"* (`docs/SPEC.md:249-250`). That conclusion may well be correct
— AssetBundles don't embed managed assemblies, so a `MonoScript` reference is plausibly just
metadata (assembly-qualified name + GUID hash) independent of the DLL's own player-platform
compatibility flags — but the SPEC asserts this as settled fact with no citation and no test, for the
single most load-bearing mechanism in §D (all five required EFT components — `LevelSettings`,
`LocationScene`, `BotZone`, `LocationExportInfo`, `SpawnPointMarker` — depend on this DLL's types
serializing correctly into the `Arena_Equator_Scripts` scene when that scene is built into a
`BuildTarget.StandaloneWindows64`-targeted bundle, per finding #2 above). If Unity's asset-bundle build
pipeline *does* gate MonoScript serialization on a plugin's per-platform compatibility flags for the
target build platform (an open question this review cannot resolve from static analysis alone), the
practical effect would be every component from `GameRefs/` silently failing to attach/serialize into the
Scripts scene bundle — which would surface as the exact "player falls through geometry / raid never
starts" failure class §G.S4 is designed to triage, but for a completely different root cause than
anything §H's risk register currently lists.

**Evidence**: `docs/SPEC.md:243-249` (the Editor-only PluginImporter configuration and its stated
justification); no test, citation, or precedent for this specific "Editor-only-plugin MonoBehaviour
serialized into a Standalone-target AssetBundle" combination appears anywhere in
`analysis/ref-manimal.md` or `analysis/ref-interchange.md` (grepped for `PluginImporter`,
`SetCompatibleWithEditor`, `MonoScript` — no matches in either) — both reference implementations solved
this differently (typetree-stub cloning, per `docs/SPEC.md:233-236`'s own framing of why this project's
approach is claimed to be simpler), so there's no prior-art precedent to lean on for this specific
question either.

**Fix**: add this as an explicit S1 smoke-test check (§G.S1 already exists to catch exactly this class
of unproven-mechanism risk cheaply, `docs/SPEC.md:1018-1037`) — build the ~50-object slice with at least
one `GameRefs`-sourced MonoBehaviour attached, into a `StandaloneWindows64`-targeted bundle, and confirm
via UnityPy re-read that the component's `m_Script` PPtr and serialized field data survive. If it does
not survive, mark the DLLs compatible with `Standalone` (not Editor-only) and re-test — cheap to fix if
caught at S1, expensive if only discovered at S4.

---

## 9. [MINOR] `com.unity.ai.navigation` package version is not pinned

**Section**: §B.WP2.7 (`docs/SPEC.md:330-331`).

**Problem**: "add `com.unity.ai.navigation` to `Packages/manifest.json` (ships with 2022.3.43f1 LTS,
standard package — no version pin issue expected)" gives no exact version string. This directly touches
this review's "determinism" lens item: two different WP2 runs (or two different machines) adding this
package at different times could resolve different package versions from the registry, which is exactly
the kind of non-reproducibility §B.WP1's own acceptance criteria explicitly guard against for the
extractor ("re-running the extractor twice over unchanged source data produces byte-identical
manifest.json," `docs/SPEC.md:217-219`) but nothing analogous is stated for WP2's package dependency.

**Fix**: pin an exact version, e.g. `"com.unity.ai.navigation": "2.0.x"` (verify against what actually
resolves for 2022.3.43f1 at implementation time), not just the package name.

---

## 10. [MINOR] `Rigidbody` (413 instances on Equator) is absent from the component schema without being named as out-of-scope

**Section**: §C.6 (`docs/SPEC.md:748-767`).

**Problem**: §C.6's preamble states the recorded component types are exhaustive and explicitly names
what's excluded: *"anything not listed (`ParticleSystem`, `AudioSource`,
`OcclusionPortal`/`OcclusionArea`, `SpeedTree`/`TreeWind`, any `MonoBehaviour` not covering a
required-component role) is out of scope"* (`docs/SPEC.md:751-754`). `Rigidbody` is not in the
component table (`docs/SPEC.md:756-767`) **nor** in that explicit exclusion list, despite 413 instances
on Equator (`analysis/arena-census.md:363`, §8.1) correlating closely with the 850 convex
`MeshCollider`s (`arena-census.md:373-376`) — i.e. a meaningful population of props authored as dynamic
physics objects, not static geometry. Omitting a `Rigidbody` on import is very likely the *right* call
for a spike (a dynamic prop with no `Rigidbody` just behaves as inert static geometry — safe, not a
crash), but it's currently an undocumented gap rather than a stated decision, and a future milestone
that revisits this schema should not have to rediscover the 413-instance count from scratch.

**Fix**: add `Rigidbody` to §C.6's explicit exclusion list (or the component table, flagged
`skip_import: true` like `SkinnedMeshRenderer`/`ReflectionProbe`) so the omission is a documented
decision, not a silent gap.

---

## 11. [MINOR] No explicit mesh-bounds guarantee in the binary format or the importer algorithm

**Section**: §C.2 (`docs/SPEC.md:685-703`), §B.WP2.2 (`docs/SPEC.md:258-267`).

**Problem**: the mesh binary layout carries no bounds field, and §B.WP2.2's algorithm never mentions
`Mesh.bounds`/`RecalculateBounds()` explicitly. In practice this is very likely fine —
`Mesh.SetIndices(..., calculateBounds: true, ...)` defaults `calculateBounds` to `true` and recomputes
bounds from whatever vertex positions are already set — but that safety net depends entirely on
`SetVertices` (positions) being called **before** the `SetIndices` calls for every submesh, an ordering
the spec implies (positions are listed first in the §C.2 field order) but never states as a requirement
for the importer's own call sequence. A renderer with degenerate/zero bounds silently frustum-culls
itself out of every camera — a quiet, easy-to-miss visual bug (some geometry just never draws) that
would not show up as a console error.

**Fix**: add one explicit line to §B.WP2.2 — call `mesh.RecalculateBounds()` (or confirm via code review
that every `SetIndices` call already runs after all vertex-stream data is set) as a final, unconditional
step before `AssetDatabase.CreateAsset`, rather than relying implicitly on default-parameter behavior.

---

## Summary table

| # | Severity | Section | One-line problem |
|---|---|---|---|
| 1 | Blocker | §B.WP2.2 | `mesh.subMeshCount` never set — throws for 241/1441 Equator meshes |
| 2 | Blocker | §B.WP2.8 | No `BuildTarget` specified for `BuildPipeline.BuildAssetBundles` |
| 3 | Major | §B.WP2.7 | NavMesh agent-0 settings never written into the empty project before baking |
| 4 | Major | §B.WP2.3/2.4 | Material import documented before the texture import it depends on |
| 5 | Major | §C.6 | `LODGroup` schema: `fade_mode`/`fade_transition_width` at wrong levels vs. real Unity API |
| 6 | Major | §B.WP2.5 | LODGroup cross-object renderer references have no instantiation-order guarantee |
| 7 | Major | §B.WP1/§C.2 | `m_MeshCompression` never checked; extractor path assumes uncompressed `m_VertexData` |
| 8 | Major | §B.WP2.1 | MonoScript serialization of Editor-only-plugin types into a Standalone bundle is asserted, not tested |
| 9 | Minor | §B.WP2.7 | `com.unity.ai.navigation` package version not pinned |
| 10 | Minor | §C.6 | `Rigidbody` (413 instances) silently absent from the schema |
| 11 | Minor | §C.2/§B.WP2.2 | No explicit mesh-bounds guarantee, relies on implicit default-parameter behavior |
