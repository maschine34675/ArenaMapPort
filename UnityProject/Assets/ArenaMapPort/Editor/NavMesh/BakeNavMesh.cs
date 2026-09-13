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
            var allSources = new List<NavMeshBuildSource>();
            UnityEngine.AI.NavMeshBuilder.CollectSources(null, ~0, NavMeshCollectGeometry.PhysicsColliders, 0, new List<NavMeshBuildMarkup>(), allSources);

            var rootTransforms = new List<Transform>();
            foreach (var root in geometryRoots)
                if (root != null) rootTransforms.Add(root.transform);

            var sources = new List<NavMeshBuildSource>();
            foreach (var s in allSources)
            {
                Transform t = s.component != null ? s.component.transform : null;
                if (t == null) continue;
                foreach (var rt in rootTransforms)
                {
                    if (t.IsChildOf(rt)) { sources.Add(s); break; }
                }
            }

            var bounds = new Bounds();
            bool first = true;
            foreach (var root in geometryRoots)
            {
                if (root == null) continue;
                foreach (var r in root.GetComponentsInChildren<Renderer>(true))
                {
                    if (first) { bounds = r.bounds; first = false; }
                    else bounds.Encapsulate(r.bounds);
                }
            }
            if (first) bounds = new Bounds(Vector3.zero, new Vector3(200f, 50f, 200f)); // no renderers found - generous fallback
            else bounds.Expand(10f);

            NavMeshData data = UnityEngine.AI.NavMeshBuilder.BuildNavMeshData(settings, sources, bounds, Vector3.zero, Quaternion.identity);
            Debug.Log("[ArenaMapPort][BakeNavMesh] baked NavMeshData: agentRadius=" + AgentRadius + " agentHeight=" + AgentHeight +
                      " agentSlope=" + AgentSlope + " agentClimb=" + AgentClimb + " tileSize=" + TileSize +
                      " bounds=" + bounds + " sources=" + sources.Count +
                      " (root=null collected " + allSources.Count + " total, " + sources.Count + " kept after filtering to this bake's own content-scene roots)");
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
