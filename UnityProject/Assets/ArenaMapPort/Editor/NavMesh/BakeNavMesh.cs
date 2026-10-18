// ArenaMapPort WP2 — §B.WP2.7 NavMesh bake (decision #6: re-bake fresh, do NOT port Arena's binary
// NavMeshData). Not exercised by the S1 smoke pipeline by default (SPEC.md §G.S1: "skip the NavMesh
// bake for this slice unless trivial to include") — implemented here so S2's full 6-scene build can
// call it, and left callable standalone (BuildBundles' -bakeNavMesh flag) for anyone who wants to
// smoke-test it against S1 too.
//
// SPT's agent-0 values (spt_gm.json lines 199-217, NOT Unity's own fresh-project agent-0 stock
// default, which is numerically different) are written into NavMesh.SetSettings for agentTypeID 0
// in THIS project before baking — approach (a) from SPEC.md's two documented options.
using System;
using System.Collections.Generic;
using System.IO;
using ArenaMapPort.Editor.Common;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;

namespace ArenaMapPort.Editor.NavMesh
{
    public static class BakeNavMesh
    {
        // spt_gm.json lines 199-217 (NavMeshProjectSettings.m_Settings[0]) — SPT's own configured
        // agent-0, not a generic Unity engine constant (§B.WP2.7's explicit correction).
        public const float AgentRadius = 0.3f;
        public const float AgentHeight = 1.7f;
        public const float AgentSlope = 48f;
        public const float AgentClimb = 0.38f;
        public const int TileSize = 256;

        public static NavMeshData Bake(GameObject[] geometryRoots)
        {
            // Background scenes also contain nearby solid props, so retain their original colliders
            // as navigation sources. Only exclude their distant renderers from the bake volume.
            var gameplayRoots = new List<GameObject>();
            foreach (var root in geometryRoots)
                if (root != null && !ArenaPaths.Map.IsBackgroundScene(root.scene.name))
                    gameplayRoots.Add(root);
            int backgroundRoots = geometryRoots.Length - gameplayRoots.Count;
            if (backgroundRoots > 0)
                Debug.Log("[ArenaMapPort][BakeNavMesh] excluded " + backgroundRoots + " background scene root(s) from renderer bounds; retained their original collider sources");

            var settings = new NavMeshBuildSettings
            {
                agentTypeID = 0,
                agentRadius = AgentRadius,
                agentHeight = AgentHeight,
                agentSlope = AgentSlope,
                agentClimb = AgentClimb,
                tileSize = TileSize,
                minRegionArea = 2f,
                overrideVoxelSize = false,
            };
            // NOTE: this struct is passed directly to NavMeshBuilder.BuildNavMeshData below
            // (SPEC.md §B.WP2.7 option (b), "bypass NavMeshSurface entirely") — there is no
            // UnityEngine.AI.NavMesh.SetSettings API in this Unity version (2022.3.43f1; confirmed by
            // a real compile error, not assumed) to also persist it as the project's agent-0 entry,
            // so option (a)'s "also register it for any NavMeshSurface/agent-0 lookup elsewhere in
            // this process" half is not applicable here — every value SPT's bots need comes from this
            // struct being fed straight into the bake, which is sufficient for producing correct
            // NavMeshData.

            // UnityEditor.AI.NavMeshBuilder and UnityEngine.AI.NavMeshBuilder BOTH exist in this
            // project (confirmed by a real CS0104 ambiguity error — com.unity.ai.navigation 1.1.5
            // exposes CollectSources/BuildNavMeshData on the UnityEngine.AI one here, NOT the
            // UnityEditor.AI one, which a second real compile error (CS0117) also confirmed) - fully
            // qualify to UnityEngine.AI's.
            // s1-fix-r1: `CollectSources(root.transform, ..., NavMeshCollectGeometry.PhysicsColliders, ...)`
            // found 0 sources on the first real end-to-end run despite the scene genuinely holding 16
            // MeshCollider + 4 BoxCollider components on active GameObjects (independently confirmed
            // via editor_verify.json's own GetComponentsInChildren-based counts) — s1-finish.md
            // flagged an untested lead ("PhysX's broadphase hasn't registered colliders added
            // moments earlier in the same synchronous call"). Physics.SyncTransforms() is a real,
            // cheap, documented API for exactly that class of problem — kept below (harmless,
            // costs nothing measurable) even though it did NOT fix the issue (s1-fix-r1's own
            // finding, re-confirmed).
            UnityEngine.Physics.SyncTransforms();

            // s1-fix-r2 ROOT CAUSE, found via a standalone A/B diagnostic
            // (BakeNavMesh.DiagCollectSources, work/build/s1_diag/navmesh_diag1.log): reopened the
            // exact same 2 already-built content scenes headless and called CollectSources 4 ways.
            // Per-content-scene-root Transform (what this method used to pass, both geometry modes):
            // sources=0, every single time. The SAME scenes/colliders via `root=null` (the "collect
            // from every currently loaded scene" overload): sources=13 (PhysicsColliders) / 15
            // (RenderMeshes) — 13 exactly matches an independent, non-NavMesh-API
            // GetComponentsInChildren<Collider> scan's own
            // "enabled && !isTrigger && activeInHierarchy" count over those same roots. This proves
            // the root-Transform overload of NavMeshBuilder.CollectSources is the actually-broken
            // path in this exact Editor/version/batchmode configuration (a genuine Unity API
            // limitation, not a scene/collider/PhysX-timing problem as s1-finish/s1-fix-r1's earlier
            // "not yet registered in the broadphase" theory assumed — SyncTransforms() never helped
            // because that was never the real cause) — the null-root, whole-loaded-scene-set
            // overload works correctly and finds every real collider. Fix: call CollectSources(null,
            // ...) once, then filter the result down to sources whose owning Collider is a
            // descendant of (or is) one of THIS bake's own geometryRoots — restores the original
            // "only these content scenes, not whatever else happens to be open" intent (at bake
            // time inside the real pipeline, the just-authored Arena_Equator_Scripts scene is also
            // open; its 3 SpawnPointMarker BoxColliders are non-trigger and would otherwise leak
            // into the navmesh as bogus tiny obstacles without this filter).
            //
            // fix-lighting-movement, item 1 (2026-09-14): AuthorScriptsScene.cs now authors these same
            // 3 BoxColliders as isTrigger=true (they were non-trigger when the paragraph above was
            // written -- see SpawnColliderRepairs.cs for why that was itself a bug: BSG's own runtime
            // code can never flip isTrigger on a pre-existing collider). This filter's own correctness
            // never depended on the trigger flag either way: it keeps only sources whose Transform is a
            // child of (or is) one of THIS bake's own content-scene roots (geometryRoots --
            // Arena_Equator/_Lighting/etc.), and the Scripts scene these 3 markers live in is never
            // passed as one of those roots, trigger or not. (Separately, Unity's PhysicsColliders
            // geometry mode is commonly documented as skipping trigger colliders entirely -- not
            // independently verified against this exact Editor/version in this pass -- which would mean
            // these 3 sources no longer even reach `allSources` post-fix; harmless either way, since the
            // explicit filter below already excludes them unconditionally.)
            // fix-enabled-state: confirmed this pass that this is safe wrt the newly-applied
            // per-component "enabled" flags (SceneAssembler.cs's AttachMeshCollider/AttachBoxCollider/
            // etc. now set Collider.enabled from the extracted record instead of leaving Unity's
            // AddComponent default of always-true) — CollectSources(null, ...) reads LIVE component
            // state from every currently-open scene at the moment it is called, and
            // BuildBundles.FinishPipeline only calls Bake() after every content scene's
            // SceneAssembler.Assemble() (and the Scripts scene) has already run and set every
            // component's final .enabled value, all within the same synchronous batchmode process
            // (scenes are saved but never closed/reopened in between) — so a Collider disabled in
            // Arena (e.g. the ~25 duplicate 'colider N'/'ballistic' MeshColliders per scene) is
            // already disabled by the time this runs, and DiagCollectSources's own
            // "enabled && !isTrigger && activeInHierarchy" cross-check below confirms
            // PhysicsColliders geometry mode itself respects Collider.enabled.
            // Verification fix-up (2026-09-15, Bay5): Arena's own harbor water plane(s) sit on
            // Unity's built-in "Water" layer (layer 4 in both Arena's TagManager and ours) with an
            // ENABLED, non-trigger MeshCollider covering the whole bay -- e.g. Arena_Bay5_Main's
            // 'Plane' object, world pos (726.25, -8.65, -1144.95), scale (599.68, 1, 690.19),
            // layer=Water. With `layerMask = ~0` this collider was a valid PhysicsColliders source
            // like any other, so CollectSources(null, ...) picked it up and the resulting
            // NavMeshData's m_SourceBounds ballooned to that plane's own footprint (extent
            // ~3003x51x3456, ~2900x more tiles than the real playable geometry needs) -- the water
            // itself became "walkable" ground as far as the bake was concerned. Equator never hit
            // this (no Water-layer collider large enough to matter in its own content), so the gap
            // was latent there. Excluding the Water layer from the source layer mask is the
            // generic fix (not a per-object/per-map special case): no ported Arena map should ever
            // treat its water surface as bot-walkable navmesh.
            int waterLayer = LayerMask.NameToLayer("Water");
            // PLAN-AIRPIT item 8: an optional per-map layer mask (descriptor navmesh_bake_layers; AirPit takes
            // Arena's own NavMeshSurface mask). Absent/empty = every layer but Water, exactly as before.
            int[] bakeLayers = ArenaPaths.Map.navmesh_bake_layers;
            int sourceLayerMask = SourceLayerMask(bakeLayers, waterLayer);
            if (bakeLayers != null && bakeLayers.Length > 0)
            {
                foreach (int layer in bakeLayers)
                    if (layer < 0 || layer > 31)
                        Debug.LogWarning("[ArenaMapPort][BakeNavMesh] navmesh_bake_layers entry " + layer + " is not a layer index (0..31); ignored");
                if (sourceLayerMask == 0)
                    throw new InvalidOperationException("[ArenaMapPort][BakeNavMesh] navmesh_bake_layers leaves no collectable layer");
            }

            var rootTransforms = new List<Transform>();
            foreach (var root in geometryRoots)
                if (root != null) rootTransforms.Add(root.transform);

            // PLAN-TWO-MAPS item 4: NavMeshModifier area-override/ignore-from-build markup, scoped
            // to this bake's own geometryRoots. Unity.AI.Navigation.NavMeshModifier.activeModifiers
            // is populated by each component's own [ExecuteAlways] OnEnable() the moment
            // NavMeshModifierImporter attaches it to an owned, active GameObject during Assemble()
            // -- this list is simply whatever real modifiers exist in the currently-open scenes,
            // the same source NavMeshSurface.CollectSources() itself reads (com.unity.ai.navigation
            // @1.1.5/Runtime/NavMeshSurface.cs, mirrored field-for-field below). Ground truth
            // (2026-09-25 spike): Bay5's bruno_helix pump-station family carries 113 area overrides
            // (area=1, "Not Walkable"); Equator carries zero override instances anywhere, so this
            // list is expected empty-of-overrides there (an ignoreFromBuild-only marker changes
            // nothing observable: PhysicsColliders geometry mode already skips the trigger colliders
            // those markers sit on).
            // Stage 1 (2026-09-20, docs/progress/exfils-stage2.md "Respawn booths"/"Spawn barriers"): the client opens
            // Arena's round-start obstacles at raid start (SpawnPlacementRepairs disables SPAWN_BOUND*,
            // BoothDoorRepairs deactivates the Door_Sliding_Respawn_L/_R leaves and the non-frame parts of every
            // descriptor-listed opened_barriers path). Baked with them in place, the booth and hall interiors were
            // NavMesh islands (bots could not path where players walk), so the same objects are excluded here.
            // A listed path that matches nothing fails the build (it used to be a warning): the barrier would stay
            // baked as a wall while the client opens it at raid start, silently re-sealing the pocket behind it
            // (AirPit: BUS_DOORS / PASSPORT_CONTROL_DOORS and the south spawn pocket). Every released map's paths
            // resolve (Equator 1, Bay5 3 in their accepted build logs, AirPit 2 on the 0.6.0 package), so this
            // changes no successful build; the parked maps list none.
            List<string> unresolvedBarriers;
            var openedBarriers = ResolveHierarchyPaths(geometryRoots, ArenaPaths.Map.opened_barriers, out unresolvedBarriers);
            FailOnUnresolvedPaths("opened_barriers", unresolvedBarriers);

            // PLAN-AIRPIT item 8 (user decision 2026-10-11, "no mode toggler exists"): objects that STAY active at
            // runtime but whose colliders must not shape the NavMesh, because Arena's own bake walks through them
            // and they would otherwise cut a route between spawn areas (AirPit: two closed ramen shops, a roll-door
            // and a fence panel). Unlike opened_barriers nothing is deactivated by the client, so the visuals and
            // the physical solid remain; the bots simply path through the prop like Arena's. The whole subtree is
            // dropped, the listed object itself included (opened_barriers keeps the barrier object and its frame
            // parts, because the client keeps them standing). The inverse rule is unchanged and still applies:
            // anything the client DISABLES at runtime belongs to RoundOpenedReason (name prefixes, leaf names,
            // opened_barriers), otherwise it stays a wall for bots. A path that matches nothing fails the build:
            // a silently missing exclusion would leave the route cut.
            List<string> unresolvedExclusions;
            var bakeExclusions = ResolveHierarchyPaths(geometryRoots, ArenaPaths.Map.navmesh_bake_exclusions, out unresolvedExclusions);
            FailOnUnresolvedPaths("navmesh_bake_exclusions", unresolvedExclusions);
            if (bakeExclusions.Count > 0)
                Debug.Log("[ArenaMapPort][BakeNavMesh] navmesh_bake_exclusions: " + bakeExclusions.Count + " object(s) resolved (their colliders and markups are not NavMesh sources, they stay active at runtime)");
            var excludedByReason = new Dictionary<string, int>();

            var modifierMarkups = new List<NavMeshBuildMarkup>();
            int overrideAreaCount = 0, ignoreFromBuildCount = 0;
            foreach (var m in Unity.AI.Navigation.NavMeshModifier.activeModifiers)
            {
                bool underRoot = false;
                foreach (var rt in rootTransforms) { if (m.transform.IsChildOf(rt)) { underRoot = true; break; } }
                if (!underRoot) continue;
                // Parity with NavMeshSurface.CollectSources: only modifiers for this bake's agent type (0); the
                // importer writes affected_agents from the source, so a modifier limited to another agent is skipped.
                if (!m.AffectsAgentType(0)) continue;
                // Same round-open exclusion as the collider sources (names/opened barriers only — NOT the
                // layer rule, see NonPhysicalLayerReason): a modifier under an opened barrier must not carve
                // a hole the client's runtime removal then walks through.
                string modifierReason = RoundOpenedReason(m.transform, openedBarriers) ?? BakeOnlyExclusionReason(m.transform, bakeExclusions);
                if (modifierReason != null)
                {
                    int nm;
                    excludedByReason.TryGetValue("modifier:" + modifierReason, out nm);
                    excludedByReason["modifier:" + modifierReason] = nm + 1;
                    continue;
                }
                modifierMarkups.Add(new NavMeshBuildMarkup
                {
                    root = m.transform,
                    overrideArea = m.overrideArea,
                    area = m.area,
                    ignoreFromBuild = m.ignoreFromBuild,
                    applyToChildren = m.applyToChildren,
                    overrideGenerateLinks = m.overrideGenerateLinks,
                    generateLinks = m.generateLinks,
                });
                if (m.overrideArea) overrideAreaCount++;
                if (m.ignoreFromBuild) ignoreFromBuildCount++;
            }

            var allSources = new List<NavMeshBuildSource>();
            UnityEngine.AI.NavMeshBuilder.CollectSources(null, sourceLayerMask, NavMeshCollectGeometry.PhysicsColliders, 0, modifierMarkups, allSources);

            var sources = new List<NavMeshBuildSource>();
            foreach (var s in allSources)
            {
                Transform t = s.component != null ? s.component.transform : null;
                if (t == null) continue;
                string reason = NonPhysicalLayerReason(t) ?? RoundOpenedReason(t, openedBarriers) ?? BakeOnlyExclusionReason(t, bakeExclusions);
                if (reason != null)
                {
                    int n;
                    excludedByReason.TryGetValue(reason, out n);
                    excludedByReason[reason] = n + 1;
                    continue;
                }
                foreach (var rt in rootTransforms)
                {
                    if (t.IsChildOf(rt)) { sources.Add(s); break; }
                }
            }
            foreach (var kv in excludedByReason)
                Debug.Log("[ArenaMapPort][BakeNavMesh] excluded " + kv.Value + " nonphysical or round-open source(s): " + kv.Key);

            // PLAN-TWO-MAPS item 4: NavMeshModifierVolume -> ModifierBox NavMeshBuildSource, scoped
            // to this bake's own geometryRoots the same way as the markups above. Box math copied
            // from NavMeshSurface.AppendModifierVolumes (com.unity.ai.navigation@1.1.5/Runtime/
            // NavMeshSurface.cs) rather than re-derived: world center via TransformPoint, world size
            // via the volume's own lossyScale (never the surface/bake root's), area applied straight
            // through as NavMeshBuildSource.area (same "1 == Not Walkable" convention as a modifier's
            // overrideArea). Ground truth: all 134 of Bay5's real NavMeshModifierVolume instances
            // sit on the same bruno_helix pump-station family as the overrides above; Equator has
            // none, so this adds zero sources there.
            var areaCounts = new Dictionary<int, int>();
            int volumeSourceCount = 0;
            foreach (var v in Unity.AI.Navigation.NavMeshModifierVolume.activeModifiers)
            {
                bool underRoot = false;
                foreach (var rt in rootTransforms) { if (v.transform.IsChildOf(rt)) { underRoot = true; break; } }
                if (!underRoot) continue;
                if (!v.AffectsAgentType(0)) continue; // parity with NavMeshSurface.AppendModifierVolumes
                if (RoundOpenedReason(v.transform, openedBarriers) != null) continue; // symmetric with the collider filter
                if (BakeOnlyExclusionReason(v.transform, bakeExclusions) != null) continue;
                Vector3 worldCenter = v.transform.TransformPoint(v.center);
                Vector3 scale = v.transform.lossyScale;
                Vector3 worldSize = new Vector3(v.size.x * Mathf.Abs(scale.x), v.size.y * Mathf.Abs(scale.y), v.size.z * Mathf.Abs(scale.z));
                sources.Add(new NavMeshBuildSource
                {
                    shape = NavMeshBuildSourceShape.ModifierBox,
                    transform = Matrix4x4.TRS(worldCenter, v.transform.rotation, Vector3.one),
                    size = worldSize,
                    area = v.area,
                });
                volumeSourceCount++;
                int n;
                areaCounts.TryGetValue(v.area, out n);
                areaCounts[v.area] = n + 1;
            }
            {
                var areaLog = new System.Text.StringBuilder();
                foreach (var kv in areaCounts) areaLog.Append("area" + kv.Key + "=" + kv.Value + " ");
                Debug.Log("[ArenaMapPort][BakeNavMesh] NavMesh modifiers: " + modifierMarkups.Count + " markup(s) in bake scope (" +
                          overrideAreaCount + " area-override, " + ignoreFromBuildCount + " ignoreFromBuild), " +
                          volumeSourceCount + " volume source(s) added; volume area distribution: " +
                          (areaLog.Length == 0 ? "(none)" : areaLog.ToString()));
            }

            // Verification fix-up (2026-09-15, Bay5), continued: bounds is a SEPARATE pass over
            // every gameplay Renderer, independent of `sources` above -- excluding the
            // Water layer from sourceLayerMask alone left this loop untouched, so the water plane's
            // own (still-enabled) MeshRenderer kept inflating `bounds` to its full footprint even
            // after its MeshCollider stopped contributing navmesh sources (confirmed: a first
            // attempt at this fix baked correctly-filtered sources but an unchanged, still-huge
            // m_SourceBounds). Same Water-layer exclusion applied here, symmetric with the source
            // filter above -- a renderer-only object (no collider) was never going to contribute
            // walkable geometry anyway, but it can still legitimately need to be IN the bake volume
            // (e.g. a rendered-only prop standing on real ground); only Water is special-cased out.
            //
            // PLAN-AIRPIT item 8 (user decision 2026-10-11, 4.3): a descriptor may fix the bake volume instead
            // (bake_bounds = [min_x, max_x, min_y, max_y, min_z, max_z]). AirPit needs it because its far
            // scenery (mountains to +-1.6 km, runway, forecourt) sits in the gameplay scene, so the renderer union
            // spanned 4.3 km and baked 29,596 vertices where Equator needs 10,404; the box is the play space
            // (x -80..56, z -96..92) and y -2..8 (ground floor plus wing mezzanine, Arena's own NavMesh; level 2
            // and 3 floors at y 8.6 / 16.1 stay out). Used as given, without the 10 m expansion of the union.
            // Absent (Equator, Bay5): the renderer-union code below runs unchanged.
            Bounds bounds = new Bounds();
            float[] bakeBounds = ArenaPaths.Map.bake_bounds;
            bool explicitBounds = UsesExplicitBakeBounds(bakeBounds);
            if (explicitBounds)
            {
                bounds.SetMinMax(new Vector3(bakeBounds[0], bakeBounds[2], bakeBounds[4]),
                                 new Vector3(bakeBounds[1], bakeBounds[3], bakeBounds[5]));
            }
            else
            {
                bool first = true;
                foreach (var root in gameplayRoots)
                {
                    if (root == null) continue;
                    foreach (var r in root.GetComponentsInChildren<Renderer>(true))
                    {
                        if (waterLayer >= 0 && r.gameObject.layer == waterLayer) continue;
                        if (first) { bounds = r.bounds; first = false; }
                        else bounds.Encapsulate(r.bounds);
                    }
                }
                if (first) bounds = new Bounds(Vector3.zero, new Vector3(200f, 50f, 200f)); // no renderers found - generous fallback
                else bounds.Expand(10f);
            }

            NavMeshData data = UnityEngine.AI.NavMeshBuilder.BuildNavMeshData(settings, sources, bounds, Vector3.zero, Quaternion.identity);
            Debug.Log("[ArenaMapPort][BakeNavMesh] baked NavMeshData: agentRadius=" + AgentRadius + " agentHeight=" + AgentHeight +
                      " agentSlope=" + AgentSlope + " agentClimb=" + AgentClimb + " tileSize=" + TileSize +
                      " bounds=" + bounds + " sources=" + sources.Count +
                      " (root=null collected " + allSources.Count + " total, " + sources.Count + " kept after filtering to this bake's own content-scene roots" +
                      ", Water layer(" + waterLayer + ") " + (waterLayer >= 0 ? "excluded from sourceLayerMask" : "not found in TagManager, no exclusion applied") + ")");
            Debug.Log("[ArenaMapPort][BakeNavMesh] bake inputs: bounds " + (explicitBounds ? "from the descriptor (bake_bounds)" : "from the renderer union") +
                      ", layer mask 0x" + sourceLayerMask.ToString("X8") + (bakeLayers != null && bakeLayers.Length > 0 ? " (navmesh_bake_layers)" : " (every layer but Water)") +
                      ", " + bakeExclusions.Count + " navmesh_bake_exclusions, " + openedBarriers.Count + " opened_barriers");

            // s2-build: report vertex/triangle counts for comparison against Arena's own bake
            // (analysis/equator_census.json's navmesh block: 41 tiles for Equator's full geometry,
            // Arena's own agent 287145453/radius 0.28). NavMeshData has no public tile-count
            // property in this Unity version -- tile count is read back out-of-band via UnityPy
            // against the built bundle's NavMeshData.m_NavMeshTiles (same field census.py itself
            // used, analysis/scripts/census.py:486). Vertices/triangles ARE available in-Editor via
            // the real public API: temporarily add this bake's data to the active NavMesh, ask for
            // its triangulation, then remove it again (does not affect the returned `data` asset).
            var addedHandle = UnityEngine.AI.NavMesh.AddNavMeshData(data);
            try
            {
                UnityEngine.AI.NavMeshTriangulation tri = UnityEngine.AI.NavMesh.CalculateTriangulation();
                Debug.Log("[ArenaMapPort][BakeNavMesh] triangulation: vertices=" + tri.vertices.Length +
                          " triangles=" + (tri.indices.Length / 3) + " (indices=" + tri.indices.Length + ")");
            }
            finally
            {
                addedHandle.Remove();
            }

            return data;
        }

        // s1-fix-r2 DIAGNOSTIC ONLY (not part of the real S1/S2 pipeline, not invoked by
        // BuildBundles.Run()): isolates the still-open "sources=0" gap (s1-finish.md §4 /
        // s1-fix-r1.md §4) by reopening the already-built, already-saved content scenes directly
        // (no re-import, no full pipeline) and comparing CollectSources' result across geometry
        // modes and root arguments, plus an independent, non-NavMesh-API collider count straight
        // off the loaded GameObjects. `-executeMethod
        // ArenaMapPort.Editor.NavMesh.BakeNavMesh.DiagCollectSources`.
        public static void DiagCollectSources()
        {
            try
            {
                string scenesDir = ArenaPaths.ScenesDir;
                Scene scene1 = EditorSceneManager.OpenScene(scenesDir + "/Arena_Equator.unity", OpenSceneMode.Single);
                Scene scene2 = EditorSceneManager.OpenScene(scenesDir + "/Arena_Equator_Lighting.unity", OpenSceneMode.Additive);

                var roots = new List<GameObject>();
                roots.AddRange(scene1.GetRootGameObjects());
                roots.AddRange(scene2.GetRootGameObjects());
                Debug.Log("[ArenaMapPort][DIAG-NAVMESH] opened " + roots.Count + " root GameObject(s) across 2 scenes.");

                int colliderCount = 0;
                int enabledNonTriggerCount = 0;
                foreach (var root in roots)
                {
                    foreach (var c in root.GetComponentsInChildren<Collider>(true))
                    {
                        colliderCount++;
                        if (c.enabled && !c.isTrigger && c.gameObject.activeInHierarchy) enabledNonTriggerCount++;
                    }
                }
                Debug.Log("[ArenaMapPort][DIAG-NAVMESH] direct Collider component scan: total=" + colliderCount +
                          " enabled+non-trigger+activeInHierarchy=" + enabledNonTriggerCount);

                foreach (NavMeshCollectGeometry geom in new[] { NavMeshCollectGeometry.PhysicsColliders, NavMeshCollectGeometry.RenderMeshes })
                {
                    var sourcesPerRoot = new List<NavMeshBuildSource>();
                    foreach (var root in roots)
                        UnityEngine.AI.NavMeshBuilder.CollectSources(root.transform, ~0, geom, 0, new List<NavMeshBuildMarkup>(), sourcesPerRoot);
                    Debug.Log("[ArenaMapPort][DIAG-NAVMESH] CollectSources(root=<each content root>, geometry=" + geom + ") -> sources=" + sourcesPerRoot.Count);

                    var sourcesNullRoot = new List<NavMeshBuildSource>();
                    UnityEngine.AI.NavMeshBuilder.CollectSources(null, ~0, geom, 0, new List<NavMeshBuildMarkup>(), sourcesNullRoot);
                    Debug.Log("[ArenaMapPort][DIAG-NAVMESH] CollectSources(root=null [[whole loaded scene set]], geometry=" + geom + ") -> sources=" + sourcesNullRoot.Count);
                }

                EditorApplication.Exit(0);
            }
            catch (Exception ex)
            {
                Debug.LogError("[ArenaMapPort][DIAG-NAVMESH] threw: " + ex);
                EditorApplication.Exit(1);
            }
        }

        // Stage 1 — the objects the client opens at raid start (see the comment at the source filter above).
        // "LevelBorder": Arena's TeamFight play-area walls (41 BoxColliders under 'LEVEL-BORDERS TF' on Equator,
        // layer 29), which MapBorderRepairs removes at raid start (RemoveTeamFightBorders, default on). Baked in,
        // 'LevelBorder (83)' sat exactly in the FCK hall's doorway and kept the hall a NavMesh island even with the
        // shutter excluded (host test 2026-09-20 19:05: NavMesh gap x -10.0..-9.2 at z 36).
        static readonly string[] ExcludedNamePrefixes = { "SPAWN_BOUND", "LevelBorder" };
        static readonly string[] ExcludedLeafNames = { "Door_Sliding_Respawn_L", "Door_Sliding_Respawn_R" };

        // Resolves hierarchy paths ("Root/Child/... (n)"; ArenaPaths.Map.opened_barriers and
        // navmesh_bake_exclusions) against the content-scene roots the same way the client does (first segment =
        // a root object, the rest via Transform.Find, which takes the first child of that name at every level).
        // A single segment names the root object itself (AirPit: BUS_DOORS, PASSPORT_CONTROL_DOORS).
        static List<Transform> ResolveHierarchyPaths(GameObject[] geometryRoots, string[] paths, out List<string> unresolved)
        {
            var found = new List<Transform>();
            unresolved = new List<string>();
            if (paths == null) return found;
            foreach (string path in paths)
            {
                if (string.IsNullOrEmpty(path)) continue;
                int slash = path.IndexOf('/');
                string rootName = slash < 0 ? path : path.Substring(0, slash);
                string rest = slash < 0 ? null : path.Substring(slash + 1);
                bool any = false;
                foreach (var root in geometryRoots)
                {
                    if (root == null || root.name != rootName) continue;
                    Transform t = rest == null ? root.transform : root.transform.Find(rest);
                    if (t != null) { found.Add(t); any = true; }
                }
                if (!any) unresolved.Add(path);
            }
            return found;
        }

        // Logs every unresolved path of one descriptor list (opened_barriers, navmesh_bake_exclusions) as an
        // Error and fails the build: a silently missing entry would ship a sealed pocket or a cut route.
        static void FailOnUnresolvedPaths(string descriptorKey, List<string> unresolved)
        {
            if (unresolved.Count == 0) return;
            foreach (var missing in unresolved)
                Debug.LogError("[ArenaMapPort][BakeNavMesh] " + descriptorKey + " path not found in the content scenes: '" + missing + "'");
            throw new InvalidOperationException("[ArenaMapPort][BakeNavMesh] " + unresolved.Count + " " + descriptorKey +
                                                " path(s) not found in the content scenes");
        }

        // Non-null for a nonphysical source or one the client opens at raid start: a SPAWN_BOUND* barrier, a
        // sliding-door leaf, or a non-frame part of a listed barrier (BoothDoorRepairs keeps every child whose
        // name contains "frame").
        /// <summary>
        /// Collider-source-only exclusion by the owner's numeric layer. Kept apart from
        /// <see cref="RoundOpenedReason"/> because a NavMeshModifier markup legitimately sits on a
        /// Triggers-layer owner (Bay5: 112 of the 113 bruno_helix area overrides are on layer 13; the
        /// trigger box marks the wire, applyToChildren carries "Not Walkable" onto the solid children) —
        /// running this rule on markups silenced every override (Equator build 2026-09-25 11:41 logged
        /// "excluded 31 ... modifier:Triggers layer 13" and "0 markup(s) in bake scope").
        /// </summary>
        static string NonPhysicalLayerReason(Transform t)
        {
            // Installed EFT names layer 13 Triggers and excludes it from Player collisions. Arena's
            // original spawn-marker boxes can remain enabled/non-trigger after their scripts are
            // stripped (Bay5: SPAWNS/Bear house (9)); baking them creates invisible raised islands.
            // Preserve the source geometry; exclude only this collider owner's numeric layer because
            // the Editor leaves it unnamed. Solid children of a Triggers parent must still contribute.
            if (t.gameObject.layer == 13) return "Triggers layer 13";
            // Arena and installed EFT both name layer 29 LevelBorder. The importer preserves
            // numeric layers, but this Editor's TagManager leaves 29 unnamed. Bay5's borders
            // are called Cube, so name-only filtering kept walls removed by MapBorderRepairs.
            // Match the component owner, as the runtime does; do not inherit a parent's layer. A map whose
            // descriptor keeps its borders (keep_level_borders; Bowl, whose 354 cubes are the stadium's only bound
            // and stay active at runtime) bakes them as walls instead, so bots honour the same limit as players.
            if (t.gameObject.layer == 29 && !ArenaPaths.Map.keep_level_borders) return "LevelBorder layer 29";
            return null;
        }

        /// <summary>
        /// Objects the client opens/removes at raid start (name prefixes, leaf names, descriptor-listed
        /// opened_barriers). Applied to collider sources AND to NavMeshModifier/Volume markups: a markup
        /// under such an object must not carve a hole the runtime removal then walks through.
        /// </summary>
        static string RoundOpenedReason(Transform t, List<Transform> openedBarriers)
        {
            for (Transform cur = t; cur != null; cur = cur.parent)
            {
                foreach (string prefix in ExcludedNamePrefixes)
                    if (cur.name.StartsWith(prefix, StringComparison.Ordinal)) return prefix + "*";
                foreach (string leaf in ExcludedLeafNames)
                    if (cur.name == leaf) return leaf;
            }
            foreach (Transform barrier in openedBarriers)
            {
                if (t == barrier || !t.IsChildOf(barrier)) continue;
                bool frame = false;
                for (Transform cur = t; cur != null && cur != barrier; cur = cur.parent)
                    if (cur.name.IndexOf("frame", StringComparison.OrdinalIgnoreCase) >= 0) { frame = true; break; }
                if (!frame) return "opened_barriers:" + barrier.name;
            }
            return null;
        }

        /// <summary>
        /// PLAN-AIRPIT item 8: bake-only exclusion. Non-null for the listed object itself and everything below it
        /// (<see cref="Transform.IsChildOf"/> is true for the object itself). Nothing is deactivated at runtime; the
        /// object is merely not a NavMesh source. Applied to collider sources AND to NavMeshModifier/Volume markups,
        /// like <see cref="RoundOpenedReason"/>: a Not-Walkable markup under an excluded prop would cut the route
        /// the exclusion exists to keep.
        /// </summary>
        static string BakeOnlyExclusionReason(Transform t, List<Transform> exclusions)
        {
            foreach (Transform excluded in exclusions)
                if (t.IsChildOf(excluded)) return "navmesh_bake_exclusions:" + excluded.name;
            return null;
        }

        /// <summary>
        /// The layer mask handed to CollectSources. No descriptor layers (Equator, Bay5): every layer but Water,
        /// the expression this method replaced. Descriptor layers (AirPit: Arena's own NavMeshSurface mask
        /// 9, 11, 13, 18, 30): only those, still without Water. The owner-layer rule of
        /// <see cref="NonPhysicalLayerReason"/> (13 Triggers, 29 LevelBorder) is applied on top in both cases,
        /// because Arena's mask also names the Triggers layer while this port drops non-trigger boxes left on it.
        /// Out-of-range entries are ignored.
        /// </summary>
        static int SourceLayerMask(int[] layers, int waterLayer)
        {
            int mask = 0;
            if (layers == null || layers.Length == 0)
                mask = ~0;
            else
                foreach (int layer in layers)
                    if (layer >= 0 && layer <= 31) mask |= 1 << layer;
            return waterLayer >= 0 ? mask & ~(1 << waterLayer) : mask;
        }

        /// <summary>
        /// bake_bounds = [min_x, max_x, min_y, max_y, min_z, max_z]. Null or empty: the renderer union applies
        /// (false). A well-formed box: true. Anything else throws, because falling back to the renderer union
        /// would silently bake a map whose far scenery spans kilometres.
        /// </summary>
        static bool UsesExplicitBakeBounds(float[] b)
        {
            if (b == null || b.Length == 0) return false;
            if (b.Length != 6 || !(b[0] < b[1]) || !(b[2] < b[3]) || !(b[4] < b[5]))
                throw new InvalidOperationException("[ArenaMapPort][BakeNavMesh] bake_bounds must hold six numbers " +
                    "[min_x, max_x, min_y, max_y, min_z, max_z] with min < max, got " + string.Join(", ", b));
            return true;
        }

        public static string SaveAsset(NavMeshData data, string assetPath)
        {
            Directory.CreateDirectory(ArenaPaths.NavMeshAssetDir);
            AssetDatabase.CreateAsset(data, assetPath);
            AssetDatabase.SaveAssets();
            Debug.Log("[ArenaMapPort][BakeNavMesh] saved -> " + assetPath);
            return assetPath;
        }
    }
}
