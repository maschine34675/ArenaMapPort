#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using ArenaMapPort.Shared;

namespace ArenaMapPort.Client
{
    /// <summary>How AirPit's 127 <c>Custom/Billboard_FogSheet_Simple</c> quads are treated.</summary>
    internal enum FogSheetMode
    {
        /// <summary>Rebind to an SPT unlit transparent shader and give it a generated soft texture.</summary>
        StandIn = 0,
        /// <summary>Disable the fog-sheet renderers at raid start.</summary>
        Hidden = 1,
    }

    /// <summary>
    /// Which Arena shader names that SPT does not ship may be replaced by an SPT shader, per map, and what the
    /// replacement needs besides the shader swap. Pure data and arithmetic (no Unity, EFT or BepInEx types), so
    /// the xunit project links this file and pins the gating; the Unity work lives in
    /// <see cref="ShaderStandins"/> and <see cref="ShaderRebind"/>.
    ///
    /// Background (docs/progress/shader-compat-and-inspector.md section 3, docs/progress/airpit-release.md item
    /// 11): every Arena material is imported onto a magenta stand-in shader named
    /// <c>Hidden/ArenaMapPort/&lt;original name&gt;</c> and rebound at raid start to the game's compiled shader
    /// of the original name. A name SPT does not ship stays magenta. The six-row fallback table below is the
    /// inferred, unverified substitute list from that audit. It used to apply to Bay5 only, behind the
    /// <c>MaterialFixes/EnableShaderFallbacks</c> switch (default off), because several names are also used on
    /// Equator, which was never evaluated with them.
    ///
    /// Per-map rules. WHICH names a map may substitute is per-map data: <c>ArenaMapDescriptor.ShaderFallbackNames</c>
    /// (mirrored by <c>shader_fallback_names</c> in tools/maps/&lt;key&gt;.json, pinned by the tests); this class
    /// owns the substitute rows, the switches and the extra AirPit handling:
    /// <list type="bullet">
    /// <item>Bay5: unchanged. All six rows, gated by the legacy switch, a plain shader swap and nothing else.</item>
    /// <item>AirPit: only the four names its materials use (fog sheet, both cloth shaders, the mountain rock
    /// shader), gated by its own switch that defaults to ON, plus <see cref="FixFor"/> (texture slots the
    /// substitute reads under other names, an alpha-cutoff override) and the generated fog texture. The backface
    /// cloth uses a double-sided substitute (<see cref="TwoSidedClothShader"/>), see below.</item>
    /// <item>Every other map, Equator included: an empty list, no fallback table. A name not allowed keeps the
    /// stand-in and is logged as unresolved (Error).</item>
    /// </list>
    /// A map whose descriptor lists names but that has no switch wired in <see cref="FallbacksActive"/> is never
    /// active. Widening any of this to another map needs its own review (and a row in the tests).
    ///
    /// SPT shader names below were checked against the game's shaders bundle on 2026-10-12 (read-only census of
    /// <c>EscapeFromTarkov_Data/StreamingAssets/Windows/shaders</c>, 530 named shaders): all targets exist,
    /// the three Arena names do not. <c>Unlit/Transparent Colored</c> declares only <c>_MainTex</c> (default
    /// "black"), has no tint property and multiplies the texture by the vertex colour; the fog-sheet mesh has
    /// no vertex colours (Unity supplies white). <c>MK4/Rock</c> declares <c>_Aldebo</c>/<c>_Normalmap</c>/<c>_AO</c>.
    /// <c>p0/Cutout/Bumped Diffuse</c> reads <c>_MainTex</c>, <c>_BumpMap</c>, <c>_Color</c> and <c>_Cutoff</c> but
    /// is Cull Back in all three passes (FORWARDBASE, FORWARDADD, DEFERRED) and has no <c>_Cull</c> property, so a
    /// single-sided cloth would vanish from behind. <c>Legacy Shaders/Transparent/Cutout/Bumped Diffuse 2 side</c>
    /// has the same pass layout (LOD 300, queue AlphaTest, the same three passes), declares <c>_Color</c>,
    /// <c>_MainTex</c>, <c>_BumpMap</c>, <c>_Cutoff</c> and is Cull Off in all three, so AirPit's
    /// <c>ClothShader_backface</c> (22 renderers, e.g. the flag walls) uses it. It lacks EFT's <c>_Temperature</c>
    /// and stencil terms; whether Arena's backface variant means "double sided" is inferred from its name.
    /// </summary>
    internal static class ShaderStandinPolicy
    {
        internal const string FogSheetShader = "Custom/Billboard_FogSheet_Simple";
        internal const string ClothShader = "Cloth/ClothShader";
        internal const string ClothBackfaceShader = "Cloth/ClothShader_backface";
        internal const string RockShader = "ANGRYMESH/PBR Rocks/PBR BlendTopDetail (Legacy)";
        internal const string TwoSidedClothShader = "Legacy Shaders/Transparent/Cutout/Bumped Diffuse 2 side";

        // First-run (code) defaults of the AirPit keys. AirPit never shipped with saved keys, so no migration.
        internal const bool AirPitStandinsDefault = true;
        internal const FogSheetMode FogSheetModeDefault = FogSheetMode.StandIn;
        internal const float FogSheetOpacityDefault = 1f;
        internal const float FogSheetOpacityMin = 0f;
        internal const float FogSheetOpacityMax = 4f;

        // The table exactly as it shipped for Bay5 (shader-compat-and-inspector section 3). Pinned by the tests.
        // Nature/SpeedTree8 deliberately has no row: its SPT analog (Nature/SpeedTreeEFT) has a materially different
        // property set, and a plausible-looking wrong tree is worse than a magenta one.
        static readonly Dictionary<string, string> Table = new Dictionary<string, string>
        {
            ["Custom/WorldTriplanarFresnelPro"] = "p0/Reflective/Bumped Specular SMap",
            [RockShader] = "MK4/Rock",
            [ClothShader] = "p0/Cutout/Bumped Diffuse",
            [ClothBackfaceShader] = "p0/Cutout/Bumped Diffuse",
            [FogSheetShader] = "Unlit/Transparent Colored",
            ["p0/Reflective/Bumped Specular SMap Baked"] = "p0/Reflective/Bumped Specular SMap",
        };

        // AirPit rows that differ from the shipped (Bay5) table: the backface cloth must draw from both sides.
        static readonly Dictionary<string, string> AirPitTargets = new Dictionary<string, string>
        {
            [ClothBackfaceShader] = TwoSidedClothShader,
        };

        internal static IReadOnlyDictionary<string, string> FallbackTable => Table;

        static bool IsBay5(string mapKey) => mapKey == ArenaMapRegistry.Bay5.Key;
        static bool IsAirPit(string mapKey) => mapKey == ArenaMapRegistry.AirPit.Key;

        /// <summary>
        /// The Arena shader names this map may rebind to an SPT substitute: its descriptor's
        /// <c>ShaderFallbackNames</c>. An unknown, empty or null key (and every map without the list) allows none.
        /// </summary>
        internal static string[] AllowedNames(string mapKey)
        {
            if (!string.IsNullOrEmpty(mapKey))
            {
                foreach (var map in ArenaMapRegistry.Catalog)
                    if (map.Key == mapKey) return map.ShaderFallbackNames;
            }
            return new string[0];
        }

        /// <summary>True for the maps whose descriptor lists at least one name for the fallback table.</summary>
        internal static bool UsesFallbackTable(string mapKey) => AllowedNames(mapKey).Length > 0;

        /// <summary>
        /// Whether the table is switched on for this map's raid: Bay5 follows the legacy
        /// <c>EnableShaderFallbacks</c> value only, AirPit its own key only, every other map is never on.
        /// </summary>
        internal static bool FallbacksActive(string mapKey, bool bay5LegacySwitch, bool airPitSwitch) =>
            IsBay5(mapKey) ? bay5LegacySwitch : IsAirPit(mapKey) && airPitSwitch;

        /// <summary>The SPT shader allowed to stand in for <paramref name="originalName"/> on this map, if any.</summary>
        internal static bool TryGetFallback(string mapKey, string originalName, out string fallbackName)
        {
            fallbackName = string.Empty;
            if (string.IsNullOrEmpty(originalName) || Array.IndexOf(AllowedNames(mapKey), originalName) < 0)
                return false;
            string target = null;
            bool found = (IsAirPit(mapKey) && AirPitTargets.TryGetValue(originalName, out target)) ||
                         Table.TryGetValue(originalName, out target);
            if (!found) return false;
            fallbackName = target;
            return true;
        }

        /// <summary>Texture and float handling beyond the shader swap is AirPit only; Bay5 keeps a plain swap.</summary>
        internal static bool UsesExtendedStandins(string mapKey) => IsAirPit(mapKey);

        /// <summary>The fog-sheet quads are disabled (rather than rebound) only on AirPit in Hidden mode.</summary>
        internal static bool HidesFogSheets(string mapKey, FogSheetMode mode) =>
            IsAirPit(mapKey) && mode == FogSheetMode.Hidden;

        /// <summary>One texture slot the substitute reads under another name than the Arena shader used.</summary>
        internal readonly struct TextureRemap
        {
            internal readonly string From;
            internal readonly string To;
            internal TextureRemap(string from, string to) { From = from; To = to; }
        }

        internal readonly struct FloatOverride
        {
            internal readonly string Name;
            internal readonly float Value;
            internal FloatOverride(string name, float value) { Name = name; Value = value; }
        }

        internal sealed class StandinFix
        {
            internal static readonly StandinFix None = new StandinFix(new TextureRemap[0], new FloatOverride[0]);
            internal readonly TextureRemap[] Textures;
            internal readonly FloatOverride[] Floats;
            internal StandinFix(TextureRemap[] textures, FloatOverride[] floats) { Textures = textures; Floats = floats; }
        }

        // Rock: the material holds a base albedo (alpha = smoothness) and a base normal under the ANGRYMESH names;
        // MK4/Rock samples them as _Aldebo and _Normalmap and shows a white rock if they stay empty. The material's
        // other texture slots are left alone on purpose: its "AO + noise mask" slot holds the detail albedo texture
        // (same texture id as the detail slot) and the top/detail blending of the two shaders is not comparable.
        static readonly StandinFix RockFix = new StandinFix(
            new[] { new TextureRemap("_BaseAlbedoASmoothness", "_Aldebo"), new TextureRemap("_BaseNormalMap", "_Normalmap") },
            new FloatOverride[0]);

        // Cloth: the normal map lives in _NormalMap1. The albedo alpha carries gloss, not coverage (measured on the
        // 0.6.0 package: tarp albedo alpha 0..232, mean 69/255; military tarp constant 31/255), and the material's
        // _Cutoff is 0.5..0.67 for the original shader's separate _CutoutMask. Through p0/Cutout/Bumped Diffuse that
        // cutoff would discard every texel and make the cloth invisible, so it is zeroed (opaque cloth).
        static readonly StandinFix ClothFix = new StandinFix(
            new[] { new TextureRemap("_NormalMap1", "_BumpMap") },
            new[] { new FloatOverride("_Cutoff", 0f) });

        /// <summary>The extra material work for a name that was rebound through the table (AirPit only).</summary>
        internal static StandinFix FixFor(string mapKey, string originalName)
        {
            if (!UsesExtendedStandins(mapKey)) return StandinFix.None;
            switch (originalName)
            {
                case RockShader: return RockFix;
                case ClothShader:
                case ClothBackfaceShader: return ClothFix;
                default: return StandinFix.None;
            }
        }

        // ---- fog-sheet texture ------------------------------------------------------------------------------

        internal const int FogTextureSize = 64;
        // The Arena sheets carry _Color.a ~0.95 and _OpacityIntensity 0.1; the original shader's exact alpha formula
        // is not known (noise, radial, angle and distance terms), so the peak is an inferred ~2x of the authored
        // product and the F12 opacity key scales it live.
        internal const float FogPeakGain = 2f;

        internal static float FogSheetPeakAlpha(float colorAlpha, float opacityIntensity, float userOpacity)
        {
            float v = colorAlpha * opacityIntensity * FogPeakGain * userOpacity;
            if (float.IsNaN(v) || v < 0f) return 0f;
            return v > 1f ? 1f : v;
        }

        /// <summary>Soft round falloff on [-1,1]^2: 1 at the centre, 0 with zero slope at the circle, 0 outside.</summary>
        internal static float FogFalloff(float nx, float ny)
        {
            float r2 = nx * nx + ny * ny;
            if (r2 >= 1f) return 0f;
            float k = 1f - r2;
            return k * k;
        }

        /// <summary>Straight-alpha RGBA32 pixels, row major, constant colour with the falloff in the alpha channel.</summary>
        internal static byte[] BuildFogSheetPixels(int size, float r, float g, float b, float peakAlpha)
        {
            var pixels = new byte[size * size * 4];
            byte cr = ToByte(r), cg = ToByte(g), cb = ToByte(b);
            for (int y = 0; y < size; y++)
            {
                float ny = (y + 0.5f) / size * 2f - 1f;
                for (int x = 0; x < size; x++)
                {
                    float nx = (x + 0.5f) / size * 2f - 1f;
                    int i = (y * size + x) * 4;
                    pixels[i] = cr;
                    pixels[i + 1] = cg;
                    pixels[i + 2] = cb;
                    pixels[i + 3] = ToByte(peakAlpha * FogFalloff(nx, ny));
                }
            }
            return pixels;
        }

        static byte ToByte(float v)
        {
            if (float.IsNaN(v) || v <= 0f) return 0;
            return v >= 1f ? (byte)255 : (byte)Math.Round(v * 255f);
        }

        // ---- unresolved names -------------------------------------------------------------------------------

        /// <summary>
        /// True only when the magenta result is the deliberate outcome of the developer A/B switch: one of AirPit's
        /// allowed names while <c>AirPitShaderStandins</c> is false. Those are reported as ONE Info line plus Debug
        /// detail. Every other unresolved name is a real degradation and keeps its Error line: Equator and Bay5
        /// (whose names stay magenta exactly as before this item), AirPit with the switch on (a substitute that
        /// could not be resolved), and any name outside a map's allow-list.
        /// </summary>
        internal static bool IsKnownGap(string mapKey, string originalName, bool airPitSwitchOn) =>
            IsAirPit(mapKey) && !airPitSwitchOn && !string.IsNullOrEmpty(originalName) &&
            Array.IndexOf(AllowedNames(mapKey), originalName) >= 0;

        /// <summary>The single Info line per raid (names sorted ordinally, no duplicates).</summary>
        internal static string FormatUnresolvedSummary(string mapKey, IEnumerable<string> names)
        {
            var list = names.Distinct().OrderBy(n => n, StringComparer.Ordinal).ToList();
            return $"{mapKey}: {list.Count} shader name(s) without an SPT shader keep the flat stand-in: {string.Join(", ", list)}";
        }
    }
}
