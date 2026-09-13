// ArenaMapPort WP2 — §B.WP2.7 NavMesh bake (decision #6: re-bake fresh, do NOT port Arena's binary
// NavMeshData). Not exercised by the S1 smoke pipeline by default (SPEC.md §G.S1: "skip the NavMesh
// bake for this slice unless trivial to include") — implemented here so S2's full 6-scene build can
// call it, and left callable standalone (BuildBundles' -bakeNavMesh flag) for anyone who wants to
// smoke-test it against S1 too.
//
// SPT's agent-0 values (spt_gm.json lines 199-217, NOT Unity's own fresh-project agent-0 stock
// default, which is numerically different) are written into NavMesh.SetSettings for agentTypeID 0
// in THIS project before baking — approach (a) from SPEC.md's two documented options.
using System.Collections.Generic;
using System.IO;
using ArenaMapPort.Editor.Common;
using UnityEditor;
using UnityEngine;
using UnityEngine.AI;

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
            var sources = new List<NavMeshBuildSource>();
            var bounds = new Bounds();
            bool first = true;
            foreach (var root in geometryRoots)
            {
                if (root == null) continue;
                UnityEngine.AI.NavMeshBuilder.CollectSources(root.transform, ~0, NavMeshCollectGeometry.PhysicsColliders, 0, new List<NavMeshBuildMarkup>(), sources);
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
                      " bounds=" + bounds + " sources=" + sources.Count);
            return data;
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
