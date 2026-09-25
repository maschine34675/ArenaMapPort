// PLAN-TWO-MAPS item 6 — Arena's own rain-cover geometry (EnvironmentManager > TriggerGroup >
// N IndoorTrigger, and on Bay5 a WeatherObstacle > N DryPlane), read from the package's
// raincover/raincover.json (tools/extract/extract_raincover.py). Replaces, for the two maps that
// have it, both the client's EnvironmentRepairs.cs bare-manager fallback (always-Outdoor, no
// indoor triggers) and RainCoverRepairs.cs's depth-photo heuristic with Arena's own ground-truth
// data — see RainCoverRepairs.cs's new AuthoredRainCover config key.
//
// What vanilla does (decompile): EnvironmentManagerBase.Awake() -> Init() -> InitIndoors() does
// `_rootTriggerGroup = base.gameObject.GetComponentInChildren<TriggerGroup>();` and then every
// player's indoor/outdoor state comes from TriggerGroup.Check(pos)/IndoorTrigger.Check(pos) — a
// plain `Bounds.Contains(pos)` test, no collider involved at all for either class.
// WeatherObstacle.Awake() combines every child DryPlane's Mesh (via each DryPlane's own
// transform.localToWorldMatrix) into one MeshCollider and DESTROYS the DryPlane children
// (EFT.EnvironmentEffect/WeatherObstacle.cs's own CombineMesh()/method_1(), decompile-verified,
// not reimplemented here — our authored DryPlane children just need to exist with the right
// Mesh/transform before that Awake() runs, at real scene load).
//
// What our ported scene lacks: no EnvironmentManager/TriggerGroup/IndoorTrigger/WeatherObstacle
// at all.
//
// The repair, and why it needs no manual `_childrenTriggers` wiring: TriggerGroup.Awake() only
// calls Reinit() (which rebuilds _childrenTriggers/_childrenGroups from
// GetComponentsInChildren<...>() AND recomputes its own Bounds from the live Transform) when its
// serialized Bounds is already near-zero (EFT.EnvironmentEffect/TriggerGroup.cs). This importer
// therefore leaves the authored TriggerGroup's own Bounds at the C# default (zero, i.e. never
// assigned) and parents every IndoorTrigger DIRECTLY under it — Reinit() then does all the wiring
// itself, the first time the real scene loads, from the Bounds values authored on each
// IndoorTrigger below. IndoorTrigger itself always gets a genuinely non-zero baked Bounds
// (extracted verbatim, never a Transform-recomputed one — extract_raincover.py's own doc comment
// explains why a baked-vs-live mismatch is expected on one real Bay5 instance and why the baked
// value is the one the shipped Arena game actually uses), so ITS OWN Awake() never calls Reinit()
// either. Every DryPlane uses ONE shared Unity built-in Quad mesh asset (Arena's own data: every
// instance's Mesh PPtr is byte-identical to its own sibling MeshFilter.m_Mesh, verified over the
// full corpus by the extractor) — no custom mesh import needed.
//
// Host test / verification: docs/progress/two-maps-item-6-raincover.md.
using System;
using System.IO;
using ArenaMapPort.Editor.Common;
using EFT.EnvironmentEffect;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace ArenaMapPort.Editor.Import
{
    public static class RainCoverImporter
    {
        public static void Build(ArenaPackage package, GameObject scriptsRoot)
        {
            var featureEntry = package.Manifest["native_features"]?["raincover"] as JObject;
            if (featureEntry == null)
                return; // no raincover data in this package (e.g. one of the twelve deferred maps) — nothing to author

            const string relPath = "raincover/raincover.json";
            string fullPath = package.ResolvePath(relPath);
            if (!File.Exists(fullPath))
                throw new FileNotFoundException("manifest declares native_features.raincover but " + relPath + " is missing", fullPath);

            var data = JObject.Parse(File.ReadAllText(fullPath));
            if ((int)data["schema_version"] != 1)
                throw new InvalidDataException("raincover.json: unsupported schema_version.");

            var indoorTriggersJson = (JArray)data["indoor_triggers"];
            var dryPlanesJson = (JArray)data["dry_planes"];
            bool weatherObstacle = (bool)data["weather_obstacle"];

            if (indoorTriggersJson == null || indoorTriggersJson.Count == 0)
                throw new InvalidDataException("raincover.json has zero indoor_triggers — refusing to author an empty EnvironmentManager.");
            if (weatherObstacle && (dryPlanesJson == null || dryPlanesJson.Count == 0))
                throw new InvalidDataException("raincover.json: weather_obstacle=true but zero dry_planes.");
            if (!weatherObstacle && dryPlanesJson != null && dryPlanesJson.Count > 0)
                throw new InvalidDataException("raincover.json: has dry_planes but weather_obstacle=false — WeatherObstacle.CombineMesh() needs a root to attach to.");

            // --- EnvironmentManager > TriggerGroup > N IndoorTrigger ---
            var envGo = new GameObject("EnvironmentManager");
            envGo.transform.SetParent(scriptsRoot.transform, false);
            AuthorScriptsScene.AddComponentOrThrow<EnvironmentManager>(envGo); // class defaults (OutdoorFadeTime=0.25f etc.)

            var groupGo = new GameObject("TriggerGroup");
            groupGo.transform.SetParent(envGo.transform, false);
            AuthorScriptsScene.AddComponentOrThrow<TriggerGroup>(groupGo);
            // Bounds deliberately left at the C# default (zero) — see class doc comment above:
            // Awake()'s own Reinit() wires _childrenTriggers and computes Bounds itself, from the
            // IndoorTrigger children authored below, the first time the real scene loads.

            foreach (JObject it in indoorTriggersJson)
            {
                var go = new GameObject((string)it["name"]);
                go.transform.SetParent(groupGo.transform, false);
                SetWorldPositionRotation(go.transform, (JArray)it["world_position"], (JArray)it["world_rotation"]);
                var center = (JArray)it["bounds_center"];
                var size = (JArray)it["bounds_size"];
                // Author-time/gizmo fidelity only (OnDrawGizmos*, editor-only) — Reinit() is never
                // reached for a real IndoorTrigger (see class doc comment), so this scale does not
                // affect the Bounds this component actually uses at runtime (assigned explicitly below).
                go.transform.localScale = Vec3(size);
                var trigger = AuthorScriptsScene.AddComponentOrThrow<IndoorTrigger>(go);
                trigger.IsBunker = (bool)it["is_bunker"];
                trigger.FadeTime = (float)it["fade_time"];
                trigger.BunkerDepth = (float)it["bunker_depth"];
                trigger.BunkerLowPass = (float)it["bunker_low_pass"];
                trigger.ExposureSpeed = (float)it["exposure_speed"];
                trigger.ExposureOffset = (float)it["exposure_offset"];
                trigger.RainVolume = (float)it["rain_volume"];
                trigger.AreaAutoId = (int)it["area_auto_id"];
                trigger.Bounds = new Bounds(Vec3(center), Vec3(size)); // baked ground truth — see class doc comment
            }

            // --- WeatherObstacle > N DryPlane (Bay5 only — Equator authors neither, see class doc comment) ---
            if (weatherObstacle)
            {
                var obstacleGo = new GameObject("WeatherObstacle");
                obstacleGo.transform.SetParent(scriptsRoot.transform, false);
                AuthorScriptsScene.AddComponentOrThrow<global::WeatherObstacle>(obstacleGo); // top-level namespace (decompile-verified)

                Mesh quad = SharedQuadMesh();
                foreach (JObject dp in dryPlanesJson)
                {
                    var go = new GameObject((string)dp["name"]);
                    go.transform.SetParent(obstacleGo.transform, false);
                    SetWorldPositionRotation(go.transform, (JArray)dp["world_position"], (JArray)dp["world_rotation"]);
                    go.transform.localScale = Vec3((JArray)dp["world_scale"]);
                    var plane = AuthorScriptsScene.AddComponentOrThrow<DryPlane>(go);
                    plane.Mesh = quad;
                }
            }

            Debug.Log("[ArenaMapPort][RainCoverImporter] authored EnvironmentManager (TriggerGroup=1, IndoorTrigger=" +
                indoorTriggersJson.Count + "), WeatherObstacle=" + (weatherObstacle ? "1 (DryPlane=" + dryPlanesJson.Count + ")" : "0"));
        }

        // scriptsRoot (and therefore every ancestor here) is parented at the scene root with an
        // identity transform for the whole authoring pass (see AuthorScriptsScene.Build) — world
        // space and local space are the same, so the extracted world_position/world_rotation can be
        // written directly onto transform.localPosition/localRotation.
        static void SetWorldPositionRotation(Transform t, JArray pos, JArray rot)
        {
            t.localPosition = Vec3(pos);
            t.localRotation = new Quaternion((float)rot[0], (float)rot[1], (float)rot[2], (float)rot[3]);
        }

        static Vector3 Vec3(JArray a)
        {
            return new Vector3((float)a[0], (float)a[1], (float)a[2]);
        }

        static Mesh _sharedQuad;

        static Mesh SharedQuadMesh()
        {
            if (_sharedQuad != null)
                return _sharedQuad;
            // Unity's own built-in unit Quad (1x1, local XY plane) — the exact asset every DryPlane
            // in Arena's own source data references too (extract_raincover.py cross-checks each
            // instance's Mesh PPtr against its own sibling MeshFilter.m_Mesh across the full corpus).
            _sharedQuad = Resources.GetBuiltinResource<Mesh>("Quad.fbx");
            if (_sharedQuad == null)
                throw new InvalidOperationException("[ArenaMapPort][RainCoverImporter] Resources.GetBuiltinResource<Mesh>(\"Quad.fbx\") returned null.");
            return _sharedQuad;
        }
    }
}
