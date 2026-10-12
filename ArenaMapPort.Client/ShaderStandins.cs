using System.Collections.Generic;
using ArenaMapPort.Shared;
using BepInEx.Configuration;
using UnityEngine;

namespace ArenaMapPort.Client
{
    /// <summary>
    /// AirPit's material work around the shader fallback table (PLAN-AIRPIT item 11; the per-map rules and the
    /// pure arithmetic are in <see cref="ShaderStandinPolicy"/>, the swap itself in <see cref="ShaderRebind"/>).
    ///
    /// What vanilla does: EFT ships no <c>Custom/Billboard_FogSheet_Simple</c>, <c>Cloth/ClothShader</c> or
    /// <c>ANGRYMESH/PBR Rocks/PBR BlendTopDetail (Legacy)</c>; Arena's own build does.
    /// What the ported scene lacks: AirPit uses all three (127 fog-sheet planes, 140 cloth renderers, 10 mountain
    /// renderers), so after the rebind pass they stay on the magenta stand-in. Swapping the shader alone is not
    /// enough either: the fog sheets are procedural (their <c>_MainTex</c> is empty, the look comes from colour,
    /// noise and radial terms), so on an unlit textured shader they would be flat white or invisible; the rock
    /// and the cloth store their normal map and base texture under slot names the substitute does not read, and
    /// the cloth's <c>_Cutoff</c> would discard the whole mesh.
    /// What this does, only for AirPit and only for a material that was rebound through the fallback table:
    /// copies the slots listed in <see cref="ShaderStandinPolicy.FixFor"/>, applies its float overrides, and gives
    /// each fog-sheet material a generated 64 x 64 soft round texture (the material's colour, alpha from the
    /// authored opacity) that follows <c>AirPitFogSheetOpacity</c> live. As an A/B alternative,
    /// <c>AirPitFogSheets = Hidden</c> disables the fog-sheet renderers at raid start instead.
    /// Inputs are captured BEFORE the shader swap: a property the new shader does not declare can no longer be
    /// read from the material afterwards.
    ///
    /// Persistence: the map bundle stays cached in the EFT process, so a rebound material (and the texture
    /// generated for it) outlives the raid. The generated textures are therefore not destroyed on raid end (they
    /// carry <c>DontUnloadUnusedAsset</c> and are released only once their material is gone). Switching
    /// AirPitShaderStandins back to false after a raid that rebound the materials needs an EFT restart; the
    /// StandIn / Hidden choice works in both directions within one process (Hidden recognises the already
    /// rebound fog-sheet materials by instance id, StandIn rebinds the ones Hidden left alone).
    /// Host test: docs/progress/airpit-release.md item 11 checklist (log lines <c>[ArenaMapPort][RebindShaders]</c>).
    ///
    /// 2026-10-12 host finding (raid 7): EFT 0.16.9.5 itself carries the three Arena shaders inside Customs'
    /// scene shared assets (<c>sharedassets13</c> fog sheet, <c>sharedassets5</c> both cloth variants,
    /// <c>sharedassets15</c> rock) and they are loaded in-process before the content scenes rebind, so
    /// <see cref="ShaderRebind"/>'s direct lookup finds the REAL shader and this substitute path never runs
    /// (the fog sheets render natively; no generated texture exists, so <c>AirPitFogSheetOpacity</c> has
    /// nothing to refresh). The stand-in path stays as the safety net for a process where they are absent;
    /// Hidden mode recognises both shader names (<see cref="ShaderStandinPolicy.IsFogSheetShaderName"/>).
    /// </summary>
    internal static class ShaderStandins
    {
        internal sealed class Captured
        {
            internal readonly List<TextureCopy> Textures = new List<TextureCopy>();
            internal bool IsFogSheet;
            internal Color FogColor = Color.white;
            internal float FogOpacityIntensity = 0.1f;
        }

        internal struct TextureCopy
        {
            internal string To;
            internal Texture Texture;
            internal Vector2 Scale;
            internal Vector2 Offset;
        }

        sealed class FogEntry
        {
            internal Material Material;
            internal Texture2D Texture;
            internal Color Color;
            internal float OpacityIntensity;
        }

        static ConfigEntry<bool> _airPitStandins;
        static ConfigEntry<FogSheetMode> _fogSheetMode;
        static ConfigEntry<float> _fogSheetOpacity;

        // Process lifetime on purpose (see the class comment): material instance id -> generated texture.
        static readonly Dictionary<int, FogEntry> Fog = new Dictionary<int, FogEntry>();
        static int _hiddenThisRaid;

        static string FogStandinName => ShaderRebind.StandinPrefix + ShaderStandinPolicy.FogSheetShader;

        internal static bool AirPitStandinsEnabled =>
            _airPitStandins != null ? _airPitStandins.Value : ShaderStandinPolicy.AirPitStandinsDefault;

        internal static FogSheetMode FogMode =>
            _fogSheetMode != null ? _fogSheetMode.Value : ShaderStandinPolicy.FogSheetModeDefault;

        internal static int HiddenFogSheetRenderers => _hiddenThisRaid;

        internal static void BindConfig()
        {
            var settings = Plugin.Instance.Settings;
            _airPitStandins = settings.Bind("MaterialFixes", "AirPitShaderStandins", ShaderStandinPolicy.AirPitStandinsDefault,
                "AirPit only (arena_airpit). Arena's cloth (tarps, signs, fences), mountain-rock and fog-sheet materials " +
                "use shaders SPT does not ship. true = substitute SPT shaders (cloth: p0/Cutout/Bumped Diffuse, rock: " +
                "MK4/Rock, fog sheet: Unlit/Transparent Colored with a generated soft texture); false = leave them on " +
                "the magenta stand-in. The substitutes are inferred and not verified against Arena's look. The map " +
                "bundle keeps rebound materials cached: restart EFT after switching this back to false.");
            _fogSheetMode = settings.Bind("MaterialFixes", "AirPitFogSheets", ShaderStandinPolicy.FogSheetModeDefault,
                "AirPit only. StandIn = fog-sheet quads use the substitute shader (needs AirPitShaderStandins = true, " +
                "otherwise they stay magenta). Hidden = their renderers are disabled at raid start, whatever " +
                "AirPitShaderStandins says. Takes effect on the next raid, in both directions, without restarting EFT.");
            _fogSheetOpacity = settings.Bind("MaterialFixes", "AirPitFogSheetOpacity", ShaderStandinPolicy.FogSheetOpacityDefault,
                new ConfigDescription(
                    "AirPit only, AirPitFogSheets = StandIn. Multiplies the alpha of the generated fog-sheet texture " +
                    "(peak alpha = colour alpha x authored OpacityIntensity x 2 x this value). 0 = invisible, " +
                    "4 = four times the default. Live. No effect when EFT's own copy of the fog-sheet shader is " +
                    "loaded (the usual case on 0.16.9.5: the log says 'resolved to EFT's own loaded shader' and the " +
                    "sheets render natively).",
                    new AcceptableValueRange<float>(ShaderStandinPolicy.FogSheetOpacityMin, ShaderStandinPolicy.FogSheetOpacityMax)));
            _fogSheetOpacity.SettingChanged += (_, __) => RefreshFogTextures();
        }

        /// <summary>
        /// Called once per raid when the Scripts scene unloads. The textures of live materials stay (see the class
        /// comment); those whose material was destroyed with an unloaded bundle are released.
        /// </summary>
        internal static void ResetRaid()
        {
            _hiddenThisRaid = 0;
            PurgeDeadFog();
        }

        static void PurgeDeadFog()
        {
            List<int> dead = null;
            foreach (var pair in Fog)
            {
                if (pair.Value.Material != null && pair.Value.Texture != null) continue;
                (dead ?? (dead = new List<int>())).Add(pair.Key);
            }
            if (dead == null) return;
            for (int i = 0; i < dead.Count; i++)
            {
                var entry = Fog[dead[i]];
                Fog.Remove(dead[i]);
                if (entry.Texture != null) UnityEngine.Object.Destroy(entry.Texture);
            }
        }

        /// <summary>
        /// Reads everything the substitute needs while the material still has the stand-in shader, which
        /// declares every property the importer wrote.
        /// </summary>
        internal static Captured Capture(Material material, string originalName, ArenaMapDescriptor map)
        {
            var captured = new Captured();
            var fix = ShaderStandinPolicy.FixFor(map.Key, originalName);
            for (int i = 0; i < fix.Textures.Length; i++)
            {
                var remap = fix.Textures[i];
                if (!material.HasProperty(remap.From)) continue;
                var texture = material.GetTexture(remap.From);
                if (texture == null) continue;
                captured.Textures.Add(new TextureCopy
                {
                    To = remap.To,
                    Texture = texture,
                    Scale = material.GetTextureScale(remap.From),
                    Offset = material.GetTextureOffset(remap.From),
                });
            }
            if (originalName == ShaderStandinPolicy.FogSheetShader)
            {
                captured.IsFogSheet = true;
                if (material.HasProperty("_Color")) captured.FogColor = material.GetColor("_Color");
                if (material.HasProperty("_OpacityIntensity")) captured.FogOpacityIntensity = material.GetFloat("_OpacityIntensity");
            }
            return captured;
        }

        /// <summary>Applies the captured slots, the float overrides and the fog texture to the already rebound material.</summary>
        internal static void ApplyAfterSwap(Material material, Captured captured, string originalName, ArenaMapDescriptor map)
        {
            var fix = ShaderStandinPolicy.FixFor(map.Key, originalName);
            int textures = 0, floats = 0;
            for (int i = 0; i < captured.Textures.Count; i++)
            {
                var copy = captured.Textures[i];
                if (!material.HasProperty(copy.To)) continue;
                material.SetTexture(copy.To, copy.Texture);
                material.SetTextureScale(copy.To, copy.Scale);
                material.SetTextureOffset(copy.To, copy.Offset);
                textures++;
            }
            for (int i = 0; i < fix.Floats.Length; i++)
            {
                if (!material.HasProperty(fix.Floats[i].Name)) continue;
                material.SetFloat(fix.Floats[i].Name, fix.Floats[i].Value);
                floats++;
            }

            string fog = string.Empty;
            if (captured.IsFogSheet && material.HasProperty("_MainTex"))
            {
                var entry = CreateFogEntry(material, captured);
                FillFogTexture(entry, UserOpacity());
                material.SetTexture("_MainTex", entry.Texture);
                material.SetTextureScale("_MainTex", Vector2.one);
                material.SetTextureOffset("_MainTex", Vector2.zero);
                fog = $", fog texture colour=({captured.FogColor.r:0.00},{captured.FogColor.g:0.00},{captured.FogColor.b:0.00}) " +
                      $"peak alpha={ShaderStandinPolicy.FogSheetPeakAlpha(captured.FogColor.a, captured.FogOpacityIntensity, UserOpacity()):0.000}";
            }

            Plugin.Log.LogDebug($"[ArenaMapPort][RebindShaders] {map.Key}: '{material.name}' ({originalName}) -> " +
                                $"'{material.shader.name}': {textures} texture slot(s) copied, {floats} float override(s){fog}");
        }

        /// <summary>
        /// Disables a renderer whose materials are all fog-sheet materials when the AirPit mode is Hidden. A renderer
        /// that mixes fog-sheet and other materials is left to the ordinary rebind. Returns true when the renderer
        /// is a hidden fog sheet, so the caller skips it (it must not be reported as unresolved).
        /// </summary>
        internal static bool TryHideFogSheet(Renderer renderer, ArenaMapDescriptor map)
        {
            if (!ShaderStandinPolicy.HidesFogSheets(map.Key, FogMode)) return false;
            var materials = renderer.sharedMaterials;
            bool any = false;
            for (int i = 0; i < materials.Length; i++)
            {
                var material = materials[i];
                if (material == null) continue;
                if (!IsFogSheetMaterial(material)) return false;
                any = true;
            }
            if (!any) return false;
            if (renderer.enabled)
            {
                renderer.enabled = false;
                _hiddenThisRaid++;
            }
            return true;
        }

        static bool IsFogSheetMaterial(Material material) =>
            (material.shader != null && ShaderStandinPolicy.IsFogSheetShaderName(material.shader.name, ShaderRebind.StandinPrefix)) ||
            Fog.ContainsKey(material.GetInstanceID());

        static float UserOpacity() =>
            _fogSheetOpacity != null ? _fogSheetOpacity.Value : ShaderStandinPolicy.FogSheetOpacityDefault;

        static FogEntry CreateFogEntry(Material material, Captured captured)
        {
            var texture = new Texture2D(ShaderStandinPolicy.FogTextureSize, ShaderStandinPolicy.FogTextureSize,
                TextureFormat.RGBA32, false, false)
            {
                name = "ArenaMapPort_FogSheet_" + material.name,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                hideFlags = HideFlags.DontUnloadUnusedAsset,
            };
            var entry = new FogEntry
            {
                Material = material,
                Texture = texture,
                Color = captured.FogColor,
                OpacityIntensity = captured.FogOpacityIntensity,
            };
            Fog[material.GetInstanceID()] = entry;
            return entry;
        }

        static void FillFogTexture(FogEntry entry, float userOpacity)
        {
            float peak = ShaderStandinPolicy.FogSheetPeakAlpha(entry.Color.a, entry.OpacityIntensity, userOpacity);
            var pixels = ShaderStandinPolicy.BuildFogSheetPixels(ShaderStandinPolicy.FogTextureSize,
                entry.Color.r, entry.Color.g, entry.Color.b, peak);
            entry.Texture.LoadRawTextureData(pixels);
            entry.Texture.Apply(false, false);
        }

        static void RefreshFogTextures()
        {
            PurgeDeadFog();
            float opacity = UserOpacity();
            foreach (var entry in Fog.Values) FillFogTexture(entry, opacity);
            Plugin.Log.LogDebug($"[ArenaMapPort][RebindShaders] fog-sheet opacity {opacity:0.00}: {Fog.Count} generated texture(s) refreshed");
        }
    }
}
