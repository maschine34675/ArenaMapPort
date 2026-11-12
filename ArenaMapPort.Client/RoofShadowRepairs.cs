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
    /// What vanilla does: EFT's DistantShadow draws the sun's shadow beyond the Unity cascades
    /// (QualitySettings.shadowDistance, a StableFit sphere ahead of the camera: the shadow quality's
    /// 30 to 80 m, down to a third of that for an outdoor player in the morning and evening). Its
    /// render list (InvalidateRenderers) is built from LODGroups only: per group the second-to-last
    /// LOD (or an earlier one when that has no caster), only renderers whose shadowCastingMode is
    /// not Off, that are active in the hierarchy, have a MeshFilter mesh and a bounds edge of at
    /// least 1 m. A shadow-only proxy that no LODGroup references never reaches that list.
    /// What a ported scene lacks: some of Arena's structural shadow proxies sit outside any LODGroup
    /// (Equator's three lower floor/ceiling meshes; Chop Shop's two hall-building hulls; the three
    /// storey shells of Iceberg's cafe). The
    /// cascades still draw them, the distant sun does not, so geometry beyond the cascade sphere is
    /// sunlit through a roof or ceiling (Chop Shop: a round sun patch on the far walls that follows
    /// the view; Iceberg: with a short cascade, morning sun behind the cafe's glazing reaches the
    /// lobby). What the repair does: for every map whose descriptor lists
    /// <c>DistantShadowCasterPaths</c>, enrol each existing active proxy in an owned single-level
    /// LODGroup (zero cull threshold, so the always-present proxy stays always present) and queue a
    /// native cache rebuild. Equator additionally clones the proxy materials onto the native
    /// two-sided shadow shader (<c>DistantShadowTwoSided</c>): some of its outer soffit faces point
    /// only down, and the original back-face-culling shadow shader cannot block SPT's sun arriving
    /// from the other side; the descriptor's open atrium is retained. A map without that flag keeps
    /// Arena's own material, so the distant map draws exactly what the cascades draw. Live disable
    /// restores captured material slots and removes only owned groups; collision is untouched.
    /// Host-verified for Equator (GPU-2, 2026-09-28, docs/progress/lighting-followup.md) and, with the
    /// live key as control, for Chop Shop and Iceberg (same document, 2026-11-12; Iceberg at shadow
    /// quality Low, where the cascades end before the cafe). Which proxies a map needs:
    /// tools/validate/distant_shadow_census.py.
    /// </summary>
    internal static class RoofShadowRepairs
    {
        const string Tag = "[RoofShadow]";
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
        static readonly Dictionary<ArenaMapDescriptor, ConfigEntry<bool>> Enabled =
            new Dictionary<ArenaMapDescriptor, ConfigEntry<bool>>();
        static readonly HashSet<string> WarnedPaths = new HashSet<string>();
        // The map whose proxies the owned groups/clones belong to; unload and live-off callbacks can
        // run after the Scripts scene is gone, when ArenaMapRaid.CurrentMap is already null.
        static ArenaMapDescriptor _ownerMap;

        internal static void Install()
        {
            foreach (var map in ArenaMapRegistry.All)
            {
                if (map.DistantShadowCasterPaths.Length == 0) continue;
                // Equator's text is unchanged since 2026-09-28 (a config comment; the key's meaning is the same).
                string description = map.DistantShadowTwoSided
                    ? "Make Equator's original solid ceiling shadows two-sided and include them in the native sun-shadow cache. " +
                      "Retains the original atrium openings. Live toggle; the native cache takes a few seconds to refresh."
                    : "Include this map's original building shadow proxies in the native sun-shadow cache, so the sun does " +
                      "not light the interior through the roof beyond the cascade distance. Keeps Arena's own shadow material. " +
                      "Live toggle; the native cache takes a few seconds to refresh.";
                var entry = Plugin.Instance.Settings.Bind("Lighting (" + map.Key + ")",
                    "RoofDistantShadows", true, description);
                Enabled[map] = entry;
                entry.SettingChanged += (_, __) => Reapply();
            }
            if (Enabled.Count == 0) return;
            SceneManager.sceneLoaded += (scene, mode) =>
            {
                var map = ActiveMap();
                if (map == null || !IsOwned(map, scene) || !Enabled[map].Value) return;
                Apply(map, scene);
                WarnUnresolved(map);
            };
            SceneManager.sceneUnloaded += scene =>
            {
                if (OwnerOf(scene) == null) return;
                // The loaded scene owns both renderer and generated group. Remove retired
                // references; do not retain objects from an earlier raid.
                OwnedGroups.RemoveAll(group => group == null);
                WarnedPaths.Clear();
                ReleaseMaterials(retiredOnly: true);
            };
        }

        // The running raid's map when it has enrolment paths, else null.
        static ArenaMapDescriptor ActiveMap()
        {
            var map = ArenaMapRaid.CurrentMap;
            return map != null && Enabled.ContainsKey(map) ? map : null;
        }

        static bool IsOwned(ArenaMapDescriptor map, Scene scene) => Array.IndexOf(map.AllSceneNames, scene.name) >= 0;

        static ArenaMapDescriptor OwnerOf(Scene scene)
        {
            foreach (var map in Enabled.Keys)
                if (IsOwned(map, scene)) return map;
            return null;
        }

        static void Reapply()
        {
            var map = ActiveMap();
            if (map == null) return;
            if (!Enabled[map].Value)
            {
                int removed = 0;
                foreach (var group in OwnedGroups)
                    if (group != null) { Object.DestroyImmediate(group); removed++; }
                OwnedGroups.Clear();
                ReleaseMaterials(retiredOnly: false);
                if (removed > 0)
                {
                    DistantShadowCache.Invalidate(map, "roof shadow registrations removed");
                    Plugin.Log.LogDebug($"{Tag} disabled; removed owned LOD groups={removed}");
                }
                return;
            }
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (scene.isLoaded && IsOwned(map, scene)) Apply(map, scene);
            }
            WarnUnresolved(map);
        }

        static void Apply(ArenaMapDescriptor map, Scene scene)
        {
            int registered = 0;
            foreach (var path in map.DistantShadowCasterPaths)
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
                Material[] replacement = null;
                Shader twoSided = null;
                if (map.DistantShadowTwoSided)
                {
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
                    twoSided = Shader.Find("shadow_2sided");
                    if (twoSided == null || !twoSided.isSupported)
                    {
                        Plugin.Log.LogWarning($"{Tag} native shadow_2sided unavailable; '{path}' unchanged");
                        continue;
                    }
                    replacement = new Material[materials.Length];
                }
                LODGroup group = null;
                try
                {
                    if (replacement != null)
                    {
                        for (int i = 0; i < materials.Length; i++)
                        {
                            replacement[i] = new Material(materials[i])
                            {
                                name = materials[i].name + " [ArenaMapPort two-sided roof]",
                                shader = twoSided
                            };
                            if (replacement[i].passCount != 4 || replacement[i].FindPass("ShadowCaster") < 0)
                                throw new InvalidOperationException("Unexpected native shadow_2sided pass layout");
                        }
                    }
                    group = target.gameObject.AddComponent<LODGroup>();
                    group.fadeMode = LODFadeMode.None;
                    // Zero cull threshold preserves the original always-present shadow proxy.
                    group.SetLODs(new[] { new LOD(0f, new Renderer[] { renderer }) });
                    group.RecalculateBounds();
                    if (replacement != null) renderer.sharedMaterials = replacement;
                    OwnedGroups.Add(group);
                    _ownerMap = map;
                    if (replacement != null)
                        OwnedMaterials.Add(new MaterialState { Renderer = renderer,
                            Original = materials, Replacement = replacement });
                }
                catch (Exception e)
                {
                    if (group != null) Object.DestroyImmediate(group);
                    renderer.sharedMaterials = materials;
                    DistantShadowCache.Invalidate(map, "roof shadow registration rolled back");
                    if (replacement != null)
                        foreach (var material in replacement)
                            if (material != null) Object.DestroyImmediate(material);
                    Plugin.Log.LogWarning($"{Tag} '{path}': {e.GetType().Name}: {e.Message}");
                    continue;
                }
                registered++;
                Plugin.Log.LogDebug($"{Tag} registered '{path}' mesh={filter.sharedMesh.name} " +
                    $"shader={(replacement != null ? "shadow_2sided" : renderer.sharedMaterial?.shader?.name ?? "none")} " +
                    $"bounds={renderer.bounds.center:F2}/{renderer.bounds.size:F2}");
            }
            if (registered > 0)
                DistantShadowCache.Invalidate(map, "original roof proxies registered");
        }

        // One Warning per unresolved path and raid, once every scene of the map is loaded (a path lives
        // in exactly one scene, so it cannot be judged before that). A path that resolves but is
        // inactive or already under a LODGroup is a deliberate skip in Apply, not a missing path.
        static void WarnUnresolved(ArenaMapDescriptor map)
        {
            foreach (var name in map.AllSceneNames)
            {
                var scene = SceneManager.GetSceneByName(name);
                if (!scene.IsValid() || !scene.isLoaded) return;
            }
            foreach (var path in map.DistantShadowCasterPaths)
            {
                bool found = false;
                foreach (var name in map.AllSceneNames)
                    if (Find(SceneManager.GetSceneByName(name), path) != null) { found = true; break; }
                if (!found && WarnedPaths.Add(path))
                    Plugin.Log.LogWarning($"{Tag} '{path}' not found in any loaded scene of {map.Key}; not registered");
            }
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
            DistantShadowCache.Invalidate(_ownerMap, "two-sided ceiling materials released");
            int destroyed = 0;
            foreach (var state in released)
                foreach (var material in state.Replacement)
                    if (material != null) { Object.DestroyImmediate(material); destroyed++; }
            Plugin.Log.LogDebug($"{Tag} released material clones={destroyed}; remaining={OwnedMaterials.Count}");
        }

        internal static void Probe()
        {
            if (!DeveloperOptions.Enabled) return;
            var map = ActiveMap();
            if (map == null) return;
            try
            {
                var targets = new List<MeshRenderer>();
                for (int i = 0; i < SceneManager.sceneCount; i++)
                {
                    var scene = SceneManager.GetSceneAt(i);
                    if (!scene.isLoaded || !IsOwned(map, scene)) continue;
                    foreach (var path in map.DistantShadowCasterPaths)
                    {
                        var renderer = Find(scene, path)?.GetComponent<MeshRenderer>();
                        if (renderer != null) targets.Add(renderer);
                    }
                }
                Plugin.Log.LogDebug($"{Tag} enabled={Enabled[map].Value} targets={targets.Count} " +
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
            if (!scene.IsValid()) return null;
            int slash = path.IndexOf('/');
            foreach (var root in scene.GetRootGameObjects())
                if (root.name == path.Substring(0, slash)) return root.transform.Find(path.Substring(slash + 1));
            return null;
        }
    }
}
