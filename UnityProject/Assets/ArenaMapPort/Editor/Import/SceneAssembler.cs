// ArenaMapPort WP2 — §B.WP2.5 hierarchy assembly + minimal-component authoring.
//
// Runs in two EXPLICIT passes over each scene, not one interleaved loop (SPEC.md): LODGroup.SetLODs
// stores direct Renderer object references and silently accepts a null slot for any renderer not
// yet attached (no exception, just a missing LOD level). Pass 1 instantiates every GameObject and
// attaches every MeshFilter/MeshRenderer/collider/Light across the whole scene, building an
// id -> Renderer map as it goes. Pass 2 attaches every LODGroup, resolving lods[].renderer_object_ids
// against that completed map. (Implemented here as several small, ordered loops rather than two
// literal foreach blocks, but the ordering guarantee — every Renderer exists before any LODGroup is
// attached — is identical.)
using System;
using System.Collections.Generic;
using ArenaMapPort.Editor.Common;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace ArenaMapPort.Editor.Import
{
    public static class SceneAssembler
    {
        public static Scene Assemble(
            ArenaPackage pkg,
            string sceneName,
            Dictionary<string, Mesh> meshes,
            Dictionary<string, Material> materials,
            PhysicMaterialCache physicMaterials,
            bool isFirstScene = false)
        {
            JObject sceneJson = pkg.LoadSceneJson(sceneName);
            var objectsJson = (JArray)sceneJson["objects"];
            bool requireResolvedAssets = !string.IsNullOrEmpty((string)pkg.Manifest["source"]?["map_key"]);

            // EditorSceneManager.NewScene(..., Additive) throws InvalidOperationException
            // ("Cannot create a new scene additively with an untitled scene unsaved") the very
            // first time it's called in a fresh batchmode session, since the default startup scene
            // is itself untitled/unsaved — found this pass via a real run, not anticipated in
            // SPEC.md. The first scene of a Run() must replace that default scene (Single); every
            // scene after it is safe to add Additively.
            NewSceneMode mode = isFirstScene ? NewSceneMode.Single : NewSceneMode.Additive;
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, mode);
            EditorSceneManager.SetActiveScene(scene);

            var goById = new Dictionary<string, GameObject>(objectsJson.Count);
            var rendererById = new Dictionary<string, Renderer>();
            int missingParentWarnings = 0, missingMeshWarnings = 0, missingMaterialWarnings = 0;

            // --- Pass 1a: create every GameObject; name/active/layer/tag ---
            foreach (JObject obj in objectsJson)
            {
                string id = (string)obj["id"];
                var go = new GameObject((string)obj["name"]);
                go.SetActive((bool)obj["active"]);
                go.layer = (int)obj["layer"];
                ApplyTag(go, (string)obj["tag"], sceneName, id);
                goById[id] = go;
            }

            // --- Pass 1b: parent + local TRS (all GameObjects now exist, order-independent) ---
            foreach (JObject obj in objectsJson)
            {
                string id = (string)obj["id"];
                GameObject go = goById[id];

                JToken parentIdTok = obj["parent_id"];
                if (parentIdTok != null && parentIdTok.Type != JTokenType.Null)
                {
                    string parentId = (string)parentIdTok;
                    GameObject parentGo;
                    if (goById.TryGetValue(parentId, out parentGo))
                        go.transform.SetParent(parentGo.transform, false);
                    else if (missingParentWarnings++ < 10)
                        Debug.LogWarning("[ArenaMapPort][SceneAssembler] " + sceneName + ": object " + id +
                                          " (" + go.name + ") references missing parent_id " + parentId);
                }

                go.transform.localPosition = ReadVector3(obj["local_position"]);
                go.transform.localRotation = ReadQuaternion(obj["local_rotation"]);
                go.transform.localScale = ReadVector3(obj["local_scale"]);
            }

            // --- Pass 1c: every component except LODGroup (builds rendererById for Pass 2) ---
            foreach (JObject obj in objectsJson)
            {
                string id = (string)obj["id"];
                GameObject go = goById[id];

                foreach (JObject comp in (JArray)obj["components"])
                {
                    string type = (string)comp["type"];
                    switch (type)
                    {
                        case "MeshFilter":
                            AttachMeshFilter(go, comp, meshes, sceneName, id, requireResolvedAssets, ref missingMeshWarnings);
                            break;
                        case "MeshRenderer":
                        {
                            Renderer r = AttachMeshRenderer(go, comp, materials, sceneName, id, requireResolvedAssets, ref missingMaterialWarnings);
                            if (r != null) rendererById[id] = r;
                            break;
                        }
                        case "SkinnedMeshRenderer":
                            // §B.WP1 edge case: always skip_import for the spike (recorded by WP1 for
                            // completeness only) — static-mesh-only import, no skinning reconstruction.
                            break;
                        case "MeshCollider":
                            AttachMeshCollider(go, comp, meshes, physicMaterials, sceneName, id, requireResolvedAssets, ref missingMeshWarnings);
                            break;
                        case "BoxCollider":
                            AttachBoxCollider(go, comp, physicMaterials);
                            break;
                        case "SphereCollider":
                            AttachSphereCollider(go, comp, physicMaterials);
                            break;
                        case "CapsuleCollider":
                            AttachCapsuleCollider(go, comp, physicMaterials);
                            break;
                        case "Light":
                            AttachLight(go, comp);
                            break;
                        case "NavMeshModifier":  // PLAN-TWO-MAPS item 4
                            NavMeshModifierImporter.AttachModifier(go, comp);
                            break;
                        case "NavMeshModifierVolume":  // PLAN-TWO-MAPS item 4
                            NavMeshModifierImporter.AttachVolume(go, comp);
                            break;
                        case "LODGroup":
                            break; // Pass 2, after every Renderer exists.
                        case "BallisticCollider":
                            break; // Pass 2, after every original physics collider exists.
                        case "ReflectionProbe":
                            break; // out of scope, decision #8 — recorded by WP1, not imported.
                        case "Rigidbody":
                            PhysicsPropImporter.AttachRigidbody(go, comp); // PLAN-TWO-MAPS item 5
                            break;
                        case "SmallPhysicsObject":
                            break; // PLAN-TWO-MAPS item 5 — Pass 2, after colliders/Rigidbody/BallisticCollider exist.
                        default:
                            Debug.LogWarning("[ArenaMapPort][SceneAssembler] " + sceneName + ": object " + id +
                                              " has unrecognized component type '" + type + "' (ignored)");
                            break;
                    }
                }
            }

            // --- Pass 2: LODGroup, resolved against the now-complete id -> Renderer map ---
            foreach (JObject obj in objectsJson)
            {
                string id = (string)obj["id"];
                GameObject go = goById[id];
                foreach (JObject comp in (JArray)obj["components"])
                {
                    if ((string)comp["type"] == "LODGroup")
                        AttachLodGroup(go, comp, rendererById, sceneName, id);
                    else if ((string)comp["type"] == "BallisticCollider")
                        BallisticColliderImporter.Attach(go, comp);
                    else if ((string)comp["type"] == "SmallPhysicsObject") // PLAN-TWO-MAPS item 5
                        PhysicsPropImporter.AttachSmallPhysicsObject(go, comp);
                }
            }

            if (missingParentWarnings > 10) Debug.LogWarning("[ArenaMapPort][SceneAssembler] " + sceneName + ": " + missingParentWarnings + " total missing-parent warnings (only first 10 shown)");

            Debug.Log("[ArenaMapPort][SceneAssembler] assembled '" + sceneName + "': " + goById.Count +
                      " GameObjects, " + rendererById.Count + " renderers, " + physicMaterials.DistinctCount +
                      " distinct PhysicMaterials so far");
            return scene;
        }

        // ---------------------------------------------------------------------------------------
        // Per-component attachers
        // ---------------------------------------------------------------------------------------

        private static void AttachMeshFilter(GameObject go, JObject comp, Dictionary<string, Mesh> meshes, string sceneName, string id, bool requireResolvedAssets, ref int warnCount)
        {
            Mesh mesh;
            if (!TryResolveMesh(comp, meshes, sceneName, id, "MeshFilter", requireResolvedAssets, ref warnCount, out mesh))
                return;
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
        }

        private static Renderer AttachMeshRenderer(GameObject go, JObject comp, Dictionary<string, Material> materials, string sceneName, string id, bool requireResolvedAssets, ref int warnCount)
        {
            var mr = go.AddComponent<MeshRenderer>();

            var idsJson = (JArray)comp["material_ids"];
            var mats = new Material[idsJson.Count];
            for (int i = 0; i < idsJson.Count; i++)
            {
                string context = sceneName + ": object " + id + " MeshRenderer slot " + i;
                string matId = ReadOptionalAssetId(idsJson[i], context + " material_id");
                if (matId == null)
                    continue; // An explicit null slot stays in its original array position.
                Material m;
                if (materials.TryGetValue(matId, out m) && m != null)
                {
                    mats[i] = m;
                }
                else
                {
                    if (requireResolvedAssets)
                        throw new InvalidOperationException(context + " references unresolved material_id " + matId);
                    if (warnCount++ < 10)
                        Debug.LogWarning("[ArenaMapPort][SceneAssembler] " + sceneName + ": object " + id + " MeshRenderer slot " + i + " references unresolved material_id " + matId);
                }
            }
            mr.sharedMaterials = mats;

            mr.shadowCastingMode = (ShadowCastingMode)Enum.Parse(typeof(ShadowCastingMode), (string)comp["cast_shadows"]);
            mr.receiveShadows = (bool)comp["receive_shadows"];
            mr.lightProbeUsage = (LightProbeUsage)Enum.Parse(typeof(LightProbeUsage), (string)comp["light_probe_usage"]);
            mr.reflectionProbeUsage = (ReflectionProbeUsage)Enum.Parse(typeof(ReflectionProbeUsage), (string)comp["reflection_probe_usage"]);
            // fix-enabled-state: Arena disables a handful of MeshRenderers per scene (stencil
            // ceiling/floor cutouts, leftover 'colider N'/'Cube' placeholder meshes) instead of
            // removing them — left at the AddComponent default (enabled=true) before this, every
            // one of them rendered as a large flat untextured block blocking a walkway.
            mr.enabled = ReadEnabled(comp);
            return mr;
        }

        private static void AttachMeshCollider(GameObject go, JObject comp, Dictionary<string, Mesh> meshes, PhysicMaterialCache physicMaterials, string sceneName, string id, bool requireResolvedAssets, ref int warnCount)
        {
            Mesh mesh;
            if (!TryResolveMesh(comp, meshes, sceneName, id, "MeshCollider", requireResolvedAssets, ref warnCount, out mesh))
                return;
            var mc = go.AddComponent<MeshCollider>();
            bool convex = (bool)comp["convex"];
            bool isTrigger = (bool)comp["is_trigger"];
            // Unity requires convex=true for isTrigger=true; set convex first so a trigger flag on
            // a non-convex source record doesn't get silently coerced in the wrong order.
            mc.convex = convex;
            mc.sharedMesh = mesh;
            mc.isTrigger = isTrigger;
            mc.sharedMaterial = physicMaterials.GetOrCreate(comp["physic_material"] as JObject);
            // fix-enabled-state: Arena_Equator disables ~25 duplicate 'colider N'/'ballistic'
            // MeshColliders per scene — applied before BakeNavMesh.Bake ever runs (§B.WP2.7:
            // FinishPipeline bakes only after every content scene is assembled), so its own
            // "enabled && !isTrigger && activeInHierarchy" CollectSources filter correctly
            // excludes these without any change to BakeNavMesh.cs itself.
            mc.enabled = ReadEnabled(comp);
        }

        // A source null reference is a valid component state, unlike an ID whose
        // asset failed to import. Keep the component and every source flag while
        // leaving sharedMesh null; never query Dictionary with a null key.
        private static bool TryResolveMesh(JObject comp, Dictionary<string, Mesh> meshes, string sceneName,
            string id, string componentName, bool requireResolvedAssets, ref int warnCount, out Mesh mesh)
        {
            string context = sceneName + ": object " + id + " " + componentName;
            string meshId = ReadOptionalAssetId(comp["mesh_id"], context + " mesh_id");
            mesh = null;
            if (meshId == null)
                return true;
            if (meshes.TryGetValue(meshId, out mesh) && mesh != null)
                return true;
            if (requireResolvedAssets)
                throw new InvalidOperationException(context + " references unresolved mesh_id " + meshId);
            if (warnCount++ < 10)
                Debug.LogWarning("[ArenaMapPort][SceneAssembler] " + context + " references unresolved mesh_id " + meshId);
            return false;
        }

        private static string ReadOptionalAssetId(JToken token, string context)
        {
            if (token == null)
                throw new InvalidOperationException(context + " is missing; an unassigned source asset must be explicit JSON null");
            if (token.Type == JTokenType.Null)
                return null;
            if (token.Type != JTokenType.String || string.IsNullOrEmpty((string)token))
                throw new InvalidOperationException(context + " must be a nonempty asset ID or explicit JSON null");
            return (string)token;
        }

        private static void AttachBoxCollider(GameObject go, JObject comp, PhysicMaterialCache physicMaterials)
        {
            var c = go.AddComponent<BoxCollider>();
            c.center = ReadVector3(comp["center"]);
            c.size = ReadVector3(comp["size"]);
            c.isTrigger = (bool)comp["is_trigger"];
            c.sharedMaterial = physicMaterials.GetOrCreate(comp["physic_material"] as JObject);
            // fix-enabled-state: e.g. 380 mannequin-gear BoxColliders in
            // Arena_Equator_DesignStuff and 19 stencil-cube ones in Arena_Equator_Lighting are
            // disabled in Arena — left enabled before this fix, they blocked movement as invisible
            // walls.
            c.enabled = ReadEnabled(comp);
        }

        private static void AttachSphereCollider(GameObject go, JObject comp, PhysicMaterialCache physicMaterials)
        {
            var c = go.AddComponent<SphereCollider>();
            c.center = ReadVector3(comp["center"]);
            c.radius = (float)comp["radius"];
            c.isTrigger = (bool)comp["is_trigger"];
            c.sharedMaterial = physicMaterials.GetOrCreate(comp["physic_material"] as JObject);
            c.enabled = ReadEnabled(comp);
        }

        private static void AttachCapsuleCollider(GameObject go, JObject comp, PhysicMaterialCache physicMaterials)
        {
            var c = go.AddComponent<CapsuleCollider>();
            c.center = ReadVector3(comp["center"]);
            c.radius = (float)comp["radius"];
            c.height = (float)comp["height"];
            c.direction = (int)comp["direction"];
            c.isTrigger = (bool)comp["is_trigger"];
            c.sharedMaterial = physicMaterials.GetOrCreate(comp["physic_material"] as JObject);
            c.enabled = ReadEnabled(comp);
        }

        private static void AttachLight(GameObject go, JObject comp)
        {
            var l = go.AddComponent<Light>();
            l.type = (LightType)Enum.Parse(typeof(LightType), (string)comp["light_type"]);
            var colorArr = (JArray)comp["color"];
            l.color = new Color((float)colorArr[0], (float)colorArr[1], (float)colorArr[2], (float)colorArr[3]);
            l.intensity = (float)comp["intensity"];
            l.range = (float)comp["range"];
            l.spotAngle = (float)comp["spot_angle"];
            // §B.WP2.5: force Realtime regardless of the recorded lightmapping_mode — nothing is
            // baked against in this project (decision #8), so a baked/mixed mode here would just be
            // a dark light.
            l.lightmapBakeType = LightmapBakeType.Realtime;
            l.shadows = (LightShadows)Enum.Parse(typeof(LightShadows), (string)comp["shadow_type"]);
            l.enabled = ReadEnabled(comp);
        }

        private static void AttachLodGroup(GameObject go, JObject comp, Dictionary<string, Renderer> rendererById, string sceneName, string objId)
        {
            var lodGroup = go.AddComponent<LODGroup>();

            var lodsJson = (JArray)comp["lods"];
            var lods = new LOD[lodsJson.Count];
            for (int i = 0; i < lodsJson.Count; i++)
            {
                var lodJson = (JObject)lodsJson[i];
                var idsJson = (JArray)lodJson["renderer_object_ids"];
                var renderers = new List<Renderer>(idsJson.Count);
                foreach (JToken idTok in idsJson)
                {
                    string rid = (string)idTok;
                    Renderer r;
                    if (rendererById.TryGetValue(rid, out r) && r != null)
                        renderers.Add(r);
                    else
                        Debug.LogWarning("[ArenaMapPort][SceneAssembler] " + sceneName + ": LODGroup on object " +
                                          objId + " LOD " + i + " references renderer_object_id " + rid +
                                          " with no attached Renderer (omitted, not nulled — WP6's " +
                                          "no-null-renderer check stays satisfied)");
                }
                // LOD's renderers array intentionally omits unresolved ids rather than inserting null
                // entries (§B.WP2's two-pass ordering requirement exists precisely so this list stays
                // non-null-clean in the normal case; every id is expected to resolve here since Pass 1
                // already attached every Renderer in the scene).
                var lod = new LOD((float)lodJson["screen_relative_height"], renderers.ToArray());
                lod.fadeTransitionWidth = (float)lodJson["fade_transition_width"];
                lods[i] = lod;
            }

            lodGroup.SetLODs(lods);
            // Set size/fadeMode AFTER SetLODs — group-level fields (§C.6: fade_mode is single,
            // group-level; size likewise), not touched by RecalculateBounds (never called here, so
            // the extracted authoritative `size` value is preserved verbatim rather than overwritten
            // by a geometry-derived guess).
            lodGroup.size = (float)comp["size"];
            lodGroup.fadeMode = (LODFadeMode)(int)comp["fade_mode"];
            // fix-enabled-state: no LODGroup is disabled anywhere in Equator's 5 content scenes
            // today, but this is recorded/applied for schema completeness with every other
            // component type this fix covers, not because a real disabled instance was found.
            lodGroup.enabled = ReadEnabled(comp);
        }

        /// <summary>fix-enabled-state: a component record's "enabled" key is additive to the WP1
        /// package schema — a package extracted before this field existed (or a hand-authored one)
        /// has no such key at all, and that must mean "enabled" (Unity's own AddComponent default,
        /// and what every one of these components had before this fix), not "disabled".</summary>
        private static bool ReadEnabled(JObject comp)
        {
            JToken tok = comp["enabled"];
            return tok == null || tok.Type == JTokenType.Null || (bool)tok;
        }

        private static void ApplyTag(GameObject go, string tag, string sceneName, string id)
        {
            if (string.IsNullOrEmpty(tag) || tag == "Untagged") return;
            try
            {
                go.tag = tag;
            }
            catch (UnityException)
            {
                // §B.WP2.5: an object's tag string not present in SPT's TagManager silently falls
                // back to Untagged — flagged, not fatal (no in-scope Equator geometry uses a
                // mismatched tag per arena-census.md §7, so this path is defensive, not expected).
                Debug.LogWarning("[ArenaMapPort][SceneAssembler] " + sceneName + ": object " + id +
                                  " has tag '" + tag + "' which is not defined in this project's TagManager — left Untagged");
            }
        }

        private static Vector3 ReadVector3(JToken tok)
        {
            var a = (JArray)tok;
            return new Vector3((float)a[0], (float)a[1], (float)a[2]);
        }

        private static Quaternion ReadQuaternion(JToken tok)
        {
            var a = (JArray)tok;
            return new Quaternion((float)a[0], (float)a[1], (float)a[2], (float)a[3]);
        }
    }
}
