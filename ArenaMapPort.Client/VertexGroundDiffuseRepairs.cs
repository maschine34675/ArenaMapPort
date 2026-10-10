using System;
using System.Collections.Generic;
using ArenaMapPort.Shared;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace ArenaMapPort.Client
{
    /// <summary>
    /// Optional diffuse balance for Arena's layered ground under the donor lighting. The Bay5 yard
    /// uses Custom/Vert Paint Shader Solid, not the p0 concrete shader or its specular cap. Capture
    /// bundle-instance layer tints during shader rebinding, before indoor clones are made. Live edits
    /// rebuild those clones from the updated originals. No texture, alpha, wetness or reflection writes.
    /// See docs/progress/bay5-concrete-lighting.md for the native comparison and its limits.
    /// </summary>
    internal static class VertexGroundDiffuseRepairs
    {
        const string GroundShader = "Custom/Vert Paint Shader Solid";
        static readonly string[] LayerColors = { "_Color0", "_Color1", "_Color2" };
        sealed class Tint
        {
            internal int Id;
            internal Color Original, Last;
        }
        sealed class Entry
        {
            internal Material Material;
            internal readonly List<Tint> Tints = new List<Tint>();
        }
        static readonly Dictionary<string, ConfigEntry<float>> Settings = new Dictionary<string, ConfigEntry<float>>();
        static readonly Dictionary<string, Dictionary<Material, Entry>> States = new Dictionary<string, Dictionary<Material, Entry>>();
        static bool _installed;

        internal static void Install()
        {
            if (_installed) return;
            _installed = true;
            foreach (var map in ArenaMapRegistry.All)
            {
                // Equator v5 has no layered-ground shader targets. Bay5 and other maps retain the control.
                if (map == ArenaMapRegistry.Equator) continue;
                var setting = Plugin.Instance.Settings.Bind($"Lighting ({map.Key})", "VertexGroundDiffuseMultiplier", 1f,
                    new ConfigDescription("Diffuse brightness of layered ground materials. 1 preserves the original. " +
                        "Changes only the RGB layer tints of Custom/Vert Paint Shader Solid; leaves reflections, " +
                        "puddles, textures and other shader families unchanged. Live; rebuilding indoor material " +
                        "clones may cause a brief pause. Use small increases for dark outdoor ground.",
                        new AcceptableValueRange<float>(0.5f, 4f)));
                Settings.Add(map.Key, setting);
                setting.SettingChanged += (_, __) => Reapply(map);
            }
            SceneManager.sceneUnloaded += OnSceneUnloaded;
        }

        // ShaderRebind calls this only for materials from this map's owned scenes, after assigning
        // the real EFT shader. Repeated sweeps must neither recapture the scaled tint nor compound it.
        internal static void CaptureAndApply(Material material, string shaderName, ArenaMapDescriptor map)
        {
            if (shaderName != GroundShader || material == null || !Settings.TryGetValue(map.Key, out var setting)) return;
            // A delayed rebind sweep can see indoor copies which already inherited our source tint.
            // Only source instances are baselines; recapturing a derived clone would multiply twice.
            if (material.name.IndexOf("(ArenaMapPort Indoor", StringComparison.Ordinal) >= 0) return;
            if (!States.TryGetValue(map.Key, out var entries))
                States.Add(map.Key, entries = new Dictionary<Material, Entry>());
            if (entries.ContainsKey(material)) return;
            var entry = new Entry { Material = material };
            foreach (var name in LayerColors)
            {
                int property = material.shader.FindPropertyIndex(name);
                if (property < 0 || material.shader.GetPropertyType(property) != ShaderPropertyType.Color) continue;
                int id = Shader.PropertyToID(name);
                Color original = material.GetColor(id);
                entry.Tints.Add(new Tint { Id = id, Original = original, Last = original });
            }
            if (entry.Tints.Count != LayerColors.Length)
            {
                Plugin.Log.LogWarning($"[ArenaMapPort][GroundDiffuse] map={map.Key} material='{material.name}' " +
                    "missing typed layer colors; left unchanged.");
                return;
            }
            entries.Add(material, entry);
            Apply(entry, setting.Value);
            Plugin.Log.LogInfo($"[ArenaMapPort][GroundDiffuse] map={map.Key} material='{material.name}' " +
                $"captured 3 layer tints, multiplier={setting.Value:0.00}");
        }

        static int Apply(Entry entry, float multiplier)
        {
            if (entry.Material == null) return 0;
            int applied = 0;
            foreach (var tint in entry.Tints)
            {
                // Do not overwrite another controller's later tint adjustment.
                if (entry.Material.GetColor(tint.Id) != tint.Last) continue;
                var color = tint.Original;
                color.r *= multiplier;
                color.g *= multiplier;
                color.b *= multiplier;
                if (color != tint.Last) entry.Material.SetColor(tint.Id, color);
                tint.Last = color;
                applied++;
            }
            return applied;
        }

        static void Reapply(ArenaMapDescriptor map)
        {
            if (!ArenaMapRaid.IsLoaded() || ArenaMapRaid.CurrentMap != map || !States.TryGetValue(map.Key, out var entries)) return;
            int applied = 0;
            float multiplier = Settings[map.Key].Value;
            foreach (var entry in entries.Values) applied += Apply(entry, multiplier);
            // Indoor clones inherit the source RGB; this also composes with the outer p0 specular cap.
            IndoorFloorSmoothnessRepairs.ReclassifyIfLoaded(map);
            Plugin.Log.LogInfo($"[ArenaMapPort][GroundDiffuse] map={map.Key} multiplier={multiplier:0.00} " +
                $"applied live to {applied} layer tint(s) in {entries.Count} material(s)");
        }

        static void OnSceneUnloaded(Scene scene)
        {
            var map = ArenaMapRaid.MapForScriptsScene(scene.name);
            if (map == null || !States.TryGetValue(map.Key, out var entries)) return;
            int restored = 0;
            foreach (var entry in entries.Values) restored += Apply(entry, 1f);
            States.Remove(map.Key);
            Plugin.Log.LogInfo($"[ArenaMapPort][GroundDiffuse] map={map.Key} released {entries.Count} material(s), " +
                $"restored {restored} owned layer tint(s)");
        }
    }
}
