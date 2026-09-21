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
            int sourceLayerMask = waterLayer >= 0 ? ~0 & ~(1 << waterLayer) : ~0;

            var allSources = new List<NavMeshBuildSource>();
            UnityEngine.AI.NavMeshBuilder.CollectSources(null, sourceLayerMask, NavMeshCollectGeometry.PhysicsColliders, 0, new List<NavMeshBuildMarkup>(), allSources);

            var rootTransforms = new List<Transform>();
            foreach (var root in geometryRoots)
                if (root != null) rootTransforms.Add(root.transform);

            // Stage 1 (2026-09-20, docs/progress/exfils-stage2.md "Respawn booths"/"Spawn barriers"): the client opens
            // Arena's round-start obstacles at raid start (SpawnPlacementRepairs disables SPAWN_BOUND*,
            // BoothDoorRepairs deactivates the Door_Sliding_Respawn_L/_R leaves and the non-frame parts of every
            // descriptor-listed opened_barriers path). Baked with them in place, the booth and hall interiors were
            // NavMesh islands (bots could not path where players walk), so the same objects are excluded here.
            List<string> unresolvedBarriers;
            var openedBarriers = ResolveOpenedBarriers(geometryRoots, out unresolvedBarriers);
            foreach (var missing in unresolvedBarriers)
                Debug.LogWarning("[ArenaMapPort][BakeNavMesh] opened_barriers path not found in the content scenes: '" + missing + "'");
            var excludedByReason = new Dictionary<string, int>();

            var sources = new List<NavMeshBuildSource>();
            foreach (var s in allSources)
            {
                Transform t = s.component != null ? s.component.transform : null;
                if (t == null) continue;
                string reason = RoundOpenedReason(t, openedBarriers);
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

            // Verification fix-up (2026-09-15, Bay5), continued: bounds is a SEPARATE pass over
            // every Renderer in geometryRoots, independent of `sources` above -- excluding the
            // Water layer from sourceLayerMask alone left this loop untouched, so the water plane's
            // own (still-enabled) MeshRenderer kept inflating `bounds` to its full footprint even
            // after its MeshCollider stopped contributing navmesh sources (confirmed: a first
            // attempt at this fix baked correctly-filtered sources but an unchanged, still-huge
            // m_SourceBounds). Same Water-layer exclusion applied here, symmetric with the source
            // filter above -- a renderer-only object (no collider) was never going to contribute
            // walkable geometry anyway, but it can still legitimately need to be IN the bake volume
            // (e.g. a rendered-only prop standing on real ground); only Water is special-cased out.
            var bounds = new Bounds();
            bool first = true;
            foreach (var root in geometryRoots)
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

            NavMeshData data = UnityEngine.AI.NavMeshBuilder.BuildNavMeshData(settings, sources, bounds, Vector3.zero, Quaternion.identity);
            Debug.Log("[ArenaMapPort][BakeNavMesh] baked NavMeshData: agentRadius=" + AgentRadius + " agentHeight=" + AgentHeight +
                      " agentSlope=" + AgentSlope + " agentClimb=" + AgentClimb + " tileSize=" + TileSize +
                      " bounds=" + bounds + " sources=" + sources.Count +
                      " (root=null collected " + allSources.Count + " total, " + sources.Count + " kept after filtering to this bake's own content-scene roots" +
                      ", Water layer(" + waterLayer + ") " + (waterLayer >= 0 ? "excluded from sourceLayerMask" : "not found in TagManager, no exclusion applied") + ")");

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

        // Resolves ArenaPaths.Map.opened_barriers ("Root/Child/... (n)") against the content-scene roots the
        // same way the client does (first segment = a root object, the rest via Transform.Find).
        static List<Transform> ResolveOpenedBarriers(GameObject[] geometryRoots, out List<string> unresolved)
        {
            var found = new List<Transform>();
            unresolved = new List<string>();
            string[] paths = ArenaPaths.Map.opened_barriers;
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

        // Non-null for a nonphysical source or one the client opens at raid start: a SPAWN_BOUND* barrier, a
        // sliding-door leaf, or a non-frame part of a listed barrier (BoothDoorRepairs keeps every child whose
        // name contains "frame").
        static string RoundOpenedReason(Transform t, List<Transform> openedBarriers)
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
            // Match the component owner, as the runtime does; do not inherit a parent's layer.
            if (t.gameObject.layer == 29) return "LevelBorder layer 29";
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
