using System;
using System.Collections.Generic;
using System.Reflection;
using ArenaMapPort.Shared;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace ArenaMapPort.Client
{
    /// <summary>
    /// EFT's DistantShadow caches only renderers referenced by LODGroups. Equator's three
    /// lower floor/ceiling shadow meshes have no such group, so the native distant sun can
    /// miss these ceilings. Some outer soffit faces also point only down: the original
    /// back-face-culling shadow shader cannot block SPT's sun arriving from the other side.
    /// Enroll the existing active proxies and clone their materials onto native shadow_2sided,
    /// retaining their exact geometry and the deliberately open atrium. Live disable restores
    /// captured material slots and removes only owned groups; collision is untouched.
    /// </summary>
    internal static class EquatorRoofShadowRepairs
    {
        const string Tag = "[RoofShadow]";
        static string[] Paths => ArenaMapRegistry.Equator.DistantShadowCasterPaths;
        static readonly FieldInfo RenderList = typeof(DistantShadow).GetField("_renderList",
            BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly List<LODGroup> OwnedGroups = new List<LODGroup>();
        sealed class MaterialState
        {
            internal MeshRenderer Renderer;
            internal Material[] Original;
            internal Material[] Replacement;
        }
        static readonly List<MaterialState> OwnedMaterials = new List<MaterialState>();
        static ConfigEntry<bool> _enabled;

        internal static void Install()
        {
            _enabled = Plugin.Instance.Settings.Bind("Lighting (" + ArenaMapRegistry.Equator.Key + ")",
                "RoofDistantShadows", true,
                "Make Equator's original solid ceiling shadows two-sided and include them in the native sun-shadow cache. " +
                "Retains the original atrium openings. Live toggle; the native cache takes a few seconds to refresh.");
            _enabled.SettingChanged += (_, __) => Reapply();
            SceneManager.sceneLoaded += (scene, mode) =>
            {
                if (IsEquator() && IsOwned(scene) && _enabled.Value) Apply(scene);
            };
            SceneManager.sceneUnloaded += scene =>
            {
                if (!IsOwned(scene)) return;
                // The loaded scene owns both renderer and generated group. Remove retired
                // references; do not retain objects from an earlier Equator raid.
                OwnedGroups.RemoveAll(group => group == null);
                ReleaseMaterials(retiredOnly: true);
            };
        }

        static bool IsEquator() => ArenaMapRaid.CurrentMap == ArenaMapRegistry.Equator;
        static bool IsOwned(Scene scene) => Array.IndexOf(ArenaMapRegistry.Equator.AllSceneNames, scene.name) >= 0;

        static void Reapply()
        {
            if (!IsEquator()) return;
            if (!_enabled.Value)
            {
                int removed = 0;
                foreach (var group in OwnedGroups)
                    if (group != null) { Object.DestroyImmediate(group); removed++; }
                OwnedGroups.Clear();
                ReleaseMaterials(retiredOnly: false);
                if (removed > 0)
                {
                    DistantShadowCache.Invalidate(ArenaMapRegistry.Equator, "roof shadow registrations removed");
                    Plugin.Log.LogDebug($"{Tag} disabled; removed owned LOD groups={removed}");
                }
                return;
            }
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (scene.isLoaded && IsOwned(scene)) Apply(scene);
            }
        }

        static void Apply(Scene scene)
        {
            int registered = 0;
            foreach (var path in Paths)
            {
                var target = Find(scene, path);
                if (target == null) continue;
                var renderer = target.GetComponent<MeshRenderer>();
                var filter = target.GetComponent<MeshFilter>();
                if (!target.gameObject.activeInHierarchy || renderer == null || !renderer.enabled
                    || renderer.shadowCastingMode != ShadowCastingMode.ShadowsOnly
                    || filter == null || filter.sharedMesh == null) continue;
                // Includes our earlier registration, making repeated scene/config callbacks
                // idempotent. Never change or nest an existing source-authored LODGroup.
                if (target.GetComponentInParent<LODGroup>(true) != null) continue;
                var materials = renderer.sharedMaterials;
                bool valid = materials.Length == filter.sharedMesh.subMeshCount;
                foreach (var material in materials)
                    valid &= material != null && material.shader != null && material.shader.name == "shadow";
                if (!valid)
                {
                    Plugin.Log.LogWarning($"{Tag} unexpected proxy material layout: '{path}'; skipped");
                    continue;
                }
                // SPT globalgamemanagers.assets Shader249: all four passes use Cull Off,
                // including Forward/pass0 (DistantShadow.DrawMesh) and ShadowCaster.
                // Renderer.shadowCastingMode=TwoSided alone would not fix that direct draw.
                var shader = Shader.Find("shadow_2sided");
                if (shader == null || !shader.isSupported)
                {
                    Plugin.Log.LogWarning($"{Tag} native shadow_2sided unavailable; '{path}' unchanged");
                    continue;
                }
                var replacement = new Material[materials.Length];
                LODGroup group = null;
                try
                {
                    for (int i = 0; i < materials.Length; i++)
                    {
                        replacement[i] = new Material(materials[i])
                        {
                            name = materials[i].name + " [ArenaMapPort two-sided roof]",
                            shader = shader
                        };
                        if (replacement[i].passCount != 4 || replacement[i].FindPass("ShadowCaster") < 0)
                            throw new InvalidOperationException("Unexpected native shadow_2sided pass layout");
                    }
                    group = target.gameObject.AddComponent<LODGroup>();
                    group.fadeMode = LODFadeMode.None;
                    // Zero cull threshold preserves the original always-present shadow proxy.
                    group.SetLODs(new[] { new LOD(0f, new Renderer[] { renderer }) });
                    group.RecalculateBounds();
                    renderer.sharedMaterials = replacement;
                    OwnedGroups.Add(group);
                    OwnedMaterials.Add(new MaterialState { Renderer = renderer,
                        Original = materials, Replacement = replacement });
                }
                catch (Exception e)
                {
                    if (group != null) Object.DestroyImmediate(group);
                    renderer.sharedMaterials = materials;
                    DistantShadowCache.Invalidate(ArenaMapRegistry.Equator, "roof shadow registration rolled back");
                    foreach (var material in replacement)
                        if (material != null) Object.DestroyImmediate(material);
                    Plugin.Log.LogWarning($"{Tag} '{path}': {e.GetType().Name}: {e.Message}");
                    continue;
                }
                registered++;
                Plugin.Log.LogDebug($"{Tag} registered '{path}' mesh={filter.sharedMesh.name} " +
                    $"shader=shadow_2sided bounds={renderer.bounds.center:F2}/{renderer.bounds.size:F2}");
            }
            if (registered > 0)
                DistantShadowCache.Invalidate(ArenaMapRegistry.Equator, "original ceiling proxies registered");
        }

        static void ReleaseMaterials(bool retiredOnly)
        {
            var released = new List<MaterialState>();
            for (int i = OwnedMaterials.Count - 1; i >= 0; i--)
            {
                var state = OwnedMaterials[i];
                if (retiredOnly && state.Renderer != null) continue;
                if (state.Renderer != null)
                {
                    var current = state.Renderer.sharedMaterials;
                    bool changed = false;
                    for (int slot = 0; slot < current.Length && slot < state.Replacement.Length; slot++)
                    {
                        // Preserve another component's later material assignment.
                        if (current[slot] != state.Replacement[slot]) continue;
                        current[slot] = state.Original[slot];
                        changed = true;
                    }
                    if (changed) state.Renderer.sharedMaterials = current;
                }
                released.Add(state);
                OwnedMaterials.RemoveAt(i);
            }
            if (released.Count == 0) return;
            // Native DrawRecords and pending command buffers may still reference our clones.
            // Reset that cycle AFTER restoring slots and BEFORE destroying any material.
            DistantShadowCache.Invalidate(ArenaMapRegistry.Equator, "two-sided ceiling materials released");
            int destroyed = 0;
            foreach (var state in released)
                foreach (var material in state.Replacement)
                    if (material != null) { Object.DestroyImmediate(material); destroyed++; }
            Plugin.Log.LogDebug($"{Tag} released material clones={destroyed}; remaining={OwnedMaterials.Count}");
        }

        internal static void Probe()
        {
            if (!DeveloperOptions.Enabled) return;
            if (!IsEquator()) return;
            try
            {
                var targets = new List<MeshRenderer>();
                for (int i = 0; i < SceneManager.sceneCount; i++)
                {
                    var scene = SceneManager.GetSceneAt(i);
                    if (!scene.isLoaded || !IsOwned(scene)) continue;
                    foreach (var path in Paths)
                    {
                        var renderer = Find(scene, path)?.GetComponent<MeshRenderer>();
                        if (renderer != null) targets.Add(renderer);
                    }
                }
                Plugin.Log.LogDebug($"{Tag} enabled={_enabled.Value} targets={targets.Count} " +
                    $"ownedGroups={OwnedGroups.FindAll(group => group != null).Count} materialSets={OwnedMaterials.Count}");
                foreach (var shadow in Object.FindObjectsOfType<DistantShadow>(true))
                {
                    if (shadow == null || !shadow.gameObject.scene.IsValid() || !shadow.gameObject.scene.isLoaded) continue;
                    var records = RenderList?.GetValue(shadow) as List<DistantShadow.DrawRecord>;
                    Plugin.Log.LogDebug($"{Tag} native camera={shadow.MasterCamera?.name ?? "none"} " +
                        $"active={shadow.isActiveAndEnabled} blend={shadow.EnableShadowsBlend} " +
                        $"keyword={shadow.EnableDistantShadowKeyword} dirty={shadow.InvalidateRenderersOnNextUpdate} " +
                        $"cached={records?.Count.ToString() ?? "unavailable"}");
                    foreach (var renderer in targets)
                    {
                        var mesh = renderer.GetComponent<MeshFilter>().sharedMesh;
                        int count = records == null ? -1 : records.FindAll(record => record.mesh == mesh
                            && record.drawTransform == renderer.transform.localToWorldMatrix).Count;
                        Plugin.Log.LogDebug($"{Tag} proxy={renderer.name} active={renderer.gameObject.activeInHierarchy} " +
                            $"casting={renderer.shadowCastingMode} shader={renderer.sharedMaterial?.shader?.name ?? "none"} " +
                            $"nativeCachedDraws={count}");
                    }
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"{Tag} probe: {e.GetType().Name}: {e.Message}");
            }
        }

        static Transform Find(Scene scene, string path)
        {
            int slash = path.IndexOf('/');
            foreach (var root in scene.GetRootGameObjects())
                if (root.name == path.Substring(0, slash)) return root.transform.Find(path.Substring(slash + 1));
            return null;
        }
    }
}
