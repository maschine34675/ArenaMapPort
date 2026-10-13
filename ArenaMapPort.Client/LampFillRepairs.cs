using System;
using System.Collections.Generic;
using ArenaMapPort.Shared;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ArenaMapPort.Client
{
    /// <summary>
    /// Fills the empty cells of a ceiling lamp lattice the map descriptor lists
    /// (<see cref="ArenaMapDescriptor.LampFill"/>) with dark copies of one authored lamp. AirPit's passport hall is the
    /// only user (user decision 2026-10-13: the map is the port's own interpretation anyway, the thinned ceiling looks
    /// broken, only the lamps that are lit now stay lit and every added lamp stays OFF).
    ///
    /// What vanilla does: nothing, EFT has no such ceiling. The ring lamps are inert Arena level geometry (no Light of
    /// their own, no script switching their emissive material; the ceiling is lit by a few VolumetricSpots and the GI
    /// probes), so the lit/dark state of a lamp is just the material of its LOD0 renderer.
    ///
    /// What the ported scene lacks: Arena 0.6.0 level37 (`SBG_Airpit_Light/OO/Airport_lamp_02`) authors 45 of the 153
    /// cells of a 17 x 9 lattice over the passport hall (43 lit with `Airpit_emissive_01`, 2 authored dark with
    /// `Airpit_emissive_01_off`); the close-packed ceiling of the older build (the user's reference screenshot, the baked
    /// passport cubemaps) is gone from every 0.6.0 level (docs/progress/airpit-release.md, "Passport-hall ceiling lamps").
    ///
    /// What this does: on the sceneLoaded of an owned scene of the map, once the descriptor's template path resolves (the
    /// scenes load behind the loading screen, so this is before the first frame of the raid renders; the scene that is
    /// loading right now is searched directly, the others through BoothDoorRepairs.FindByPath), it instantiates the
    /// template, an authored DARK lamp unit (LOD0-3 and SHADOW_LOD0-2 renderers, a LODGroup and one layer-12 ballistic
    /// MeshCollider child; Instantiate remaps the LODGroup's renderer references to the copy), once per descriptor cell
    /// under the template's own parent. Each copy is placed in world space at (cell x, descriptor y, cell z) with the
    /// template's rotation and scale and named "&lt;template&gt; (fill N)". No Light is added and no material is touched
    /// (the copies share the template's `_off` material); the colliders sit on the ballistic layer only, so the NavMesh
    /// (baked offline) and the movement layer are unaffected. Idempotent: an existing "(fill 0)" copy under the parent
    /// means the fill already ran. HarmonyPatches.InstallAll subscribes this ahead of ShaderRebind and the lighting
    /// repairs, so those see the copies as ordinary scene content.
    ///
    /// Failure degrades to the authored lamps: a missing template or parent logs a Warning and skips the fill, nothing
    /// else depends on it. Switch: `Map (&lt;key&gt;)/PassportHallLampFill` (default true, next raid).
    ///
    /// Verified: host test pending (2026-10-13).
    /// </summary>
    internal static class LampFillRepairs
    {
        const string Tag = "[ArenaMapPort][LampFill]";
        internal const string ConfigKey = "PassportHallLampFill";
        static readonly Dictionary<string, ConfigEntry<bool>> _enabled = new Dictionary<string, ConfigEntry<bool>>();

        internal static void BindConfig()
        {
            foreach (var map in ArenaMapRegistry.All)
            {
                var fill = map.LampFill;
                if (fill == null)
                {
                    continue;
                }
                _enabled[map.Key] = Plugin.Instance.Settings.Bind($"Map ({map.Key})", ConfigKey, true,
                    $"Fills the {fill.Count} empty ring-lamp cells over the passport hall with dark copies of the authored " +
                    "off-lamp (Arena 0.6.0 ships only 45 of the lattice; the older build the user remembers had the full " +
                    "ceiling). Takes effect at the next raid.");
            }
        }

        internal static void Install() => SceneManager.sceneLoaded += OnSceneLoaded;

        static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            var map = ArenaMapRaid.CurrentMap;
            var fill = map?.LampFill;
            if (fill == null || Array.IndexOf(map.AllSceneNames, scene.name) < 0)
            {
                return;
            }
            try
            {
                Apply(map, fill, scene);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"{Tag} map={map.Key} '{fill.TemplatePath}': {e.GetType().Name}: {e.Message}");
            }
        }

        static void Apply(ArenaMapDescriptor map, ArenaLampFillDescriptor fill, Scene loaded)
        {
            // The template lives in one of the map's scenes; before that scene has loaded the path simply does not
            // resolve. Only when every scene of the map is there is a missing template an error.
            var template = FindTemplate(fill.TemplatePath, loaded);
            if (template == null)
            {
                if (AllScenesLoaded(map, loaded))
                {
                    Plugin.Log.LogWarning($"{Tag} map={map.Key} template '{fill.TemplatePath}' not found in the loaded scenes — " +
                                          "the passport-hall lamp fill is skipped (scene changed? path typo?)");
                }
                return;
            }
            if (_enabled.TryGetValue(map.Key, out var entry) && !entry.Value)
            {
                Plugin.Log.LogDebug($"{Tag} map={map.Key} lamp fill: {fill.Count} cell(s) skipped (Map ({map.Key})/{ConfigKey} = false)");
                return;
            }

            var parent = template.parent;
            if (parent == null || !template.gameObject.activeInHierarchy)
            {
                Plugin.Log.LogWarning($"{Tag} map={map.Key} template '{fill.TemplatePath}' " +
                                      (parent == null ? "has no parent" : "is inactive") + " — the passport-hall lamp fill is skipped");
                return;
            }

            string parentPath = PathOf(parent);
            if (parent.Find(ArenaLampFillDescriptor.CloneName(template.name, 0)) != null)
            {
                Plugin.Log.LogDebug($"{Tag} map={map.Key} lamp fill: template '{fill.TemplatePath}' under '{parentPath}', " +
                                    $"0 copied, {fill.Count} skipped (already filled)");
                return;
            }

            // Same parent as the template, so its local scale is the lossy scale the copy needs; the world rotation is
            // the template's. Instantiate with a world pose runs the copy's Awake/OnEnable already in place.
            var rotation = template.rotation;
            var scale = template.localScale;
            int created = 0;
            for (int i = 0; i < fill.Count; i++)
            {
                var copy = UnityEngine.Object.Instantiate(template.gameObject,
                    new Vector3(fill.CellX(i), fill.Y, fill.CellZ(i)), rotation, parent);
                copy.name = ArenaLampFillDescriptor.CloneName(template.name, i);
                copy.transform.localScale = scale;
                copy.SetActive(true);
                created++;
            }
            Plugin.Log.LogDebug($"{Tag} map={map.Key} lamp fill: template '{fill.TemplatePath}' under '{parentPath}', " +
                                $"{created} copied, {fill.Count - created} skipped, y={fill.Y:F2}, " +
                                $"scene={template.gameObject.scene.name}, rotation={rotation.eulerAngles:F1}, scale={scale:F2}");
        }

        // The template is normally in the LAST scene of the map, so the lookup that has to succeed runs inside that scene's own
        // sceneLoaded. FindByPath walks only scenes that report isLoaded; whether the scene of a running sceneLoaded does is not
        // proven by any other caller (they all run after the load), so that scene is searched directly first, the way
        // ShadowCasterRepairs does it (host-verified there). Scenes loaded earlier are found by FindByPath.
        static Transform FindTemplate(string path, Scene loaded)
        {
            var direct = FindInScene(loaded, path);
            if (direct != null)
            {
                return direct;
            }
            var found = BoothDoorRepairs.FindByPath(path);
            return found.Count > 0 ? found[0] : null;
        }

        // "Root/Child/...": the first segment names a root object of the scene, the rest is a Transform.Find path. Roots of the
        // same name can exist in several scenes (SBG_Airpit_Light is in two of AirPit's), so every root of this scene with that
        // name is tried.
        static Transform FindInScene(Scene scene, string path)
        {
            int slash = path.IndexOf('/');
            if (slash < 1 || slash == path.Length - 1)
            {
                return null;
            }
            string rootName = path.Substring(0, slash);
            string rest = path.Substring(slash + 1);
            try
            {
                foreach (var root in scene.GetRootGameObjects())
                {
                    if (root.name != rootName)
                    {
                        continue;
                    }
                    var t = root.transform.Find(rest);
                    if (t != null)
                    {
                        return t;
                    }
                }
            }
            catch (ArgumentException e)
            {
                // Unity refuses the root list of a scene it does not consider loaded; the registry lookup is the fallback.
                Plugin.Log.LogDebug($"{Tag} scene '{scene.name}' root list unavailable in sceneLoaded: {e.Message}");
            }
            return null;
        }

        static bool AllScenesLoaded(ArenaMapDescriptor map, Scene loaded)
        {
            foreach (var name in map.AllSceneNames)
            {
                if (name == loaded.name)
                {
                    continue; // registered with SceneManager before sceneLoaded fires for it
                }
                var scene = SceneManager.GetSceneByName(name);
                if (!scene.IsValid() || !scene.isLoaded)
                {
                    return false;
                }
            }
            return true;
        }

        static string PathOf(Transform t)
        {
            var names = new List<string>();
            for (; t != null; t = t.parent)
            {
                names.Add(t.name);
            }
            names.Reverse();
            return string.Join("/", names);
        }
    }
}
