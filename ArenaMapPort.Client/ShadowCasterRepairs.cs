using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace ArenaMapPort.Client
{
    /// <summary>
    /// Some source-authored props disable their separate shadow proxies while their visible
    /// meshes have casting Off. Restore only exact descriptor-listed active LOD roots, using
    /// their existing ShadowsOnly renderers and native LOD membership. No geometry/material
    /// replacement, blanket activation or change to collision. See bay5-barrier-followup.md.
    /// </summary>
    internal static class ShadowCasterRepairs
    {
        internal static void Install() => SceneManager.sceneLoaded += OnSceneLoaded;

        static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            var map = ArenaMapRaid.CurrentMap;
            if (map == null || Array.IndexOf(map.AllSceneNames, scene.name) < 0
                || map.RestoredShadowCasterRoots.Length == 0) return;

            int activated = 0;
            foreach (var path in map.RestoredShadowCasterRoots)
            {
                var root = Find(scene, path);
                if (root == null) continue; // the descriptor may name a different owned scene
                try
                {
                    var group = root.GetComponent<LODGroup>();
                    if (!root.gameObject.activeInHierarchy || group == null || !group.enabled)
                    {
                        Plugin.Log.LogWarning($"[ShadowCaster] inactive/non-LOD target '{path}', skipped");
                        continue;
                    }
                    // Enumerate only the renderers the existing LODGroup actually references.
                    // HashSet avoids counting a shared shadow renderer twice across LOD levels.
                    var proxies = new HashSet<Renderer>();
                    foreach (var lod in group.GetLODs())
                        foreach (var renderer in lod.renderers)
                        {
                            if (!(renderer is MeshRenderer) || renderer.transform.parent != root
                                || renderer.shadowCastingMode != ShadowCastingMode.ShadowsOnly
                                || !renderer.enabled || renderer.gameObject.activeSelf) continue;
                            var mesh = renderer.GetComponent<MeshFilter>();
                            if (mesh == null || mesh.sharedMesh == null) continue;
                            var materials = renderer.sharedMaterials;
                            bool valid = materials.Length > 0;
                            foreach (var material in materials)
                                valid &= material != null && material.shader != null && material.shader.name == "shadow";
                            if (valid) proxies.Add(renderer);
                        }
                    foreach (var renderer in proxies)
                    {
                        renderer.gameObject.SetActive(true);
                        activated++;
                        Plugin.Log.LogDebug($"[ShadowCaster] map={map.Key} restored '{path}/{renderer.name}' " +
                            $"casting={renderer.shadowCastingMode} position={renderer.transform.position:F2}");
                    }
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning($"[ShadowCaster] '{path}': {e.GetType().Name}: {e.Message}");
                }
            }
            if (activated > 0) DistantShadowCache.Invalidate(map, "restored shadow proxies");
        }

        static Transform Find(Scene scene, string path)
        {
            int slash = path.IndexOf('/');
            if (slash < 1) return null;
            string rootName = path.Substring(0, slash);
            foreach (var root in scene.GetRootGameObjects())
                if (root.name == rootName) return root.transform.Find(path.Substring(slash + 1));
            return null;
        }
    }
}
