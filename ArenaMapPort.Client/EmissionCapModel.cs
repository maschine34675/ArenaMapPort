#nullable disable
using System;
using System.Collections.Generic;

namespace ArenaMapPort.Client
{
    /// <summary>Which property a material's emission is capped through (see <see cref="EmissionCapModel.Classify"/>).</summary>
    internal enum EmissionFamily
    {
        /// <summary>Not an emissive material of a family the cap knows; never touched.</summary>
        None = 0,
        /// <summary>The emission is the HDR colour <c>_EmissionColor</c> (Unity's Standard shaders with the _EMISSION keyword,
        /// and <c>p0/Cutout/Bumped Diffuse Emissive</c>): the cap scales its RGB, alpha stays.</summary>
        Color = 1,
        /// <summary>The emission is <c>_EmissionPower</c> x <c>_EmissionVisibility</c> x (the shader's <c>_EmissiveColor</c>, when it has one) x the
        /// emission map: the cap scales <c>_EmissionPower</c> only.</summary>
        Power = 2,
    }

    /// <summary>The numbers one family writes: R, G, B, A for <see cref="EmissionFamily.Color"/>, Power for <see cref="EmissionFamily.Power"/>.</summary>
    internal readonly struct EmissionValues
    {
        internal readonly float R, G, B, A, Power;
        internal EmissionValues(float r, float g, float b, float a, float power) { R = r; G = g; B = b; A = a; Power = power; }
    }

    /// <summary>One emissive material's captured state: what it held before the cap (never changes) and the scale the cap has written since.</summary>
    internal sealed class EmissionEntry
    {
        internal readonly EmissionFamily Family;
        internal readonly string Name;
        internal readonly EmissionValues Original;
        /// <summary>Power family: the _EmissionVisibility the level was judged with (1 when the shader has none).</summary>
        internal readonly float Visibility;
        /// <summary>Power family: max RGB of the shader's _EmissiveColor (1 when the shader has none).</summary>
        internal readonly float ColorPeak;
        /// <summary>The original effective level the cap is compared with (max RGB for Color, power x visibility x colour peak for Power); 0 = leave alone.</summary>
        internal readonly float Level;
        /// <summary>The scale that is written now (1 = the material holds its original values).</summary>
        internal float AppliedScale = 1f;

        EmissionEntry(EmissionFamily family, string name, EmissionValues original, float visibility, float colorPeak, float level)
        {
            Family = family; Name = name; Original = original; Visibility = visibility; ColorPeak = colorPeak; Level = level;
        }

        internal static EmissionEntry ForColor(string name, float r, float g, float b, float a) =>
            new EmissionEntry(EmissionFamily.Color, name, new EmissionValues(r, g, b, a, 0f), 1f, 1f, EmissionCapModel.ColorLevel(r, g, b));

        internal static EmissionEntry ForPower(string name, float power, float visibility, float colorPeak) =>
            new EmissionEntry(EmissionFamily.Power, name, new EmissionValues(0f, 0f, 0f, 0f, power), visibility, colorPeak,
                EmissionCapModel.PowerLevel(power, visibility, colorPeak));

        /// <summary>The values the material holds when <paramref name="scale"/> is applied; scale 1 gives the originals bit for bit.</summary>
        internal EmissionValues ValuesAt(float scale)
        {
            if (scale == 1f) return Original;
            return Family == EmissionFamily.Color
                ? new EmissionValues(Original.R * scale, Original.G * scale, Original.B * scale, Original.A, 0f)
                : new EmissionValues(0f, 0f, 0f, 0f, Original.Power * scale);
        }
    }

    /// <summary>What the glue implements over Unity's Material: read what a material holds now, write what the cap decided.</summary>
    internal interface IEmissionSurface<TMaterial>
    {
        /// <summary>false when the material no longer exists (a destroyed Unity object).</summary>
        bool TryRead(TMaterial material, EmissionFamily family, out EmissionValues current);
        void Write(TMaterial material, EmissionFamily family, EmissionValues values);
    }

    /// <summary>The outcome of one <see cref="EmissionLedger{TMaterial}.Apply"/> or <c>Restore</c>.</summary>
    internal sealed class EmissionPassResult
    {
        /// <summary>Entries that still existed.</summary>
        internal int Materials;
        /// <summary>Entries that hold a scaled value after the pass.</summary>
        internal int Capped;
        /// <summary>Entries written by this pass.</summary>
        internal int Written;
        /// <summary>Entries left alone because another controller changed the property after the cap wrote it.</summary>
        internal int Foreign;
        /// <summary>Entries whose material was destroyed; they are dropped.</summary>
        internal int Gone;
        /// <summary>The largest original level among the capped entries (0 = none) and its material.</summary>
        internal float LargestOriginal;
        internal string LargestName;
    }

    /// <summary>
    /// The Unity-free core of <c>EmissionCapRepairs</c> (the Unity glue): which materials carry a capped emission, how far the cap scales them and the
    /// ledger that makes the write idempotent and exactly reversible.
    ///
    /// The cap is a ceiling on the original effective level of a material's emission, not a multiplier: a level at or below the cap is never
    /// written, a level above it is scaled by cap / level so it lands on the cap, hue kept (one scale for all three channels). Because it is a
    /// ceiling, running it again on an already capped value changes nothing, and the ledger holds the originals per material, so a live change of
    /// the cap re-targets from the original (never from a previous result) and cap 0 writes the originals back.
    ///
    /// The rules per shader are read from EFT's own compiled shaders (UnityPy on StreamingAssets/Windows/shaders, 2026-10-31, docs/progress/
    /// iceberg-release.md "Per-map emission cap"): Standard and Standard (Specular setup) declare _EmissionColor and _EmissionMap and read the
    /// colour only with the _EMISSION keyword; p0/Reflective/Bumped Emissive Specular SMap declares _EmissionMap, _EmissiveColor, _EmissionVisibility
    /// (Range 0..1) and _EmissionPower (Range 0..10) and no _EmissionColor; p0/Reflective/Bumped Emissive Specular and the Animated variant declare
    /// the same without _EmissiveColor; p0/Cutout/Bumped Diffuse Emissive declares only an HDR _EmissionColor (no power, no visibility). The product
    /// form for the power family is the one EFT's own scripts imply: MaterialEmission and EmissionFlicker drive _EmissionVisibility from 0 to the
    /// material's value, and a power of 0 is a lamp that is off (Airpit_emissive_01_off), so both multiply. How the shader combines them with
    /// the map is compiled code that was not read, which is why only one property is scaled, linearly: _EmissionPower, the one that nothing
    /// animates per renderer (the visibility is overridden through a MaterialPropertyBlock by those scripts and would defeat a material write).
    /// </summary>
    internal static class EmissionCapModel
    {
        internal const string EmissionColorProperty = "_EmissionColor";
        internal const string EmissionPowerProperty = "_EmissionPower";
        internal const string EmissionVisibilityProperty = "_EmissionVisibility";
        internal const string EmissiveColorProperty = "_EmissiveColor";
        internal const string EmissionKeyword = "_EMISSION";

        /// <summary>The largest cap the key accepts; the descriptor value and the developer key share it.</summary>
        internal const float MaxCap = 100f;

        static readonly string[] StandardShaders = { "Standard", "Standard (Specular setup)", "Standard (Roughness setup)" };

        /// <summary>
        /// The family of a material, from its REAL shader name (a stand-in that ShaderRebind has not rebound yet has the prefix
        /// "Hidden/ArenaMapPort/" and matches nothing) and what the shader declares. Standard-family shaders qualify only with the _EMISSION keyword
        /// enabled and _EmissionColor declared; p0 shaders whose name contains "Emissive" qualify as Power when they declare _EmissionPower and as
        /// Color when they declare only _EmissionColor.
        /// </summary>
        internal static EmissionFamily Classify(string shaderName, bool emissionKeyword, bool hasEmissionColor, bool hasEmissionPower)
        {
            if (string.IsNullOrEmpty(shaderName)) return EmissionFamily.None;
            if (Array.IndexOf(StandardShaders, shaderName) >= 0)
                return emissionKeyword && hasEmissionColor ? EmissionFamily.Color : EmissionFamily.None;
            if (!shaderName.StartsWith("p0/", StringComparison.Ordinal) || shaderName.IndexOf("Emissive", StringComparison.Ordinal) < 0)
                return EmissionFamily.None;
            if (hasEmissionPower) return EmissionFamily.Power;
            return hasEmissionColor ? EmissionFamily.Color : EmissionFamily.None;
        }

        /// <summary>Largest of the three channels; 0 for a non-finite colour (left alone) or one that is not positive.</summary>
        internal static float ColorLevel(float r, float g, float b)
        {
            if (!Finite(r) || !Finite(g) || !Finite(b)) return 0f;
            return Math.Max(0f, Math.Max(r, Math.Max(g, b)));
        }

        /// <summary>power x visibility x colour peak; 0 (left alone) when any factor is non-finite or not positive.</summary>
        internal static float PowerLevel(float power, float visibility, float colorPeak)
        {
            if (!Finite(power) || !Finite(visibility) || !Finite(colorPeak) || power <= 0f || visibility <= 0f || colorPeak <= 0f) return 0f;
            return (float)((double)power * visibility * colorPeak);
        }

        /// <summary>A level within this fraction above the cap counts as at the cap: a value the cap itself wrote, read back and rounded to float, must
        /// not be scaled a second time.</summary>
        internal const double CapTolerance = 1e-5;

        /// <summary>The scale that brings <paramref name="level"/> onto <paramref name="cap"/>: 1 when the cap is off (0, negative, not finite), when the level is
        /// within the cap (or within <see cref="CapTolerance"/> above it) or when the level is 0.</summary>
        internal static float DesiredScale(float level, float cap)
        {
            if (!Finite(cap) || cap <= 0f || !Finite(level) || !(level > cap * (1.0 + CapTolerance))) return 1f;
            return (float)(cap / (double)level);
        }

        /// <summary>true when what a material holds is what the ledger last wrote (or the original), within float noise.</summary>
        internal static bool SameValues(EmissionFamily family, EmissionValues a, EmissionValues b) =>
            family == EmissionFamily.Color
                ? Near(a.R, b.R) && Near(a.G, b.G) && Near(a.B, b.B) && Near(a.A, b.A)
                : Near(a.Power, b.Power);

        static bool Near(float a, float b) => Math.Abs(a - b) <= 1e-6f + 1e-4f * Math.Max(Math.Abs(a), Math.Abs(b));

        static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }

    /// <summary>
    /// The originals of every emissive material the repair has seen, keyed by material instance, and the writes that cap or restore them. A
    /// material is captured once (<see cref="TryAdd"/> refuses a known one), so a second raid that meets the same cached bundle material, a
    /// repeated scan or a live change of the cap never compounds; <see cref="Apply"/> always starts from the original.
    /// </summary>
    internal sealed class EmissionLedger<TMaterial> where TMaterial : class
    {
        readonly Dictionary<TMaterial, EmissionEntry> _entries = new Dictionary<TMaterial, EmissionEntry>();

        internal int Count => _entries.Count;

        internal bool Contains(TMaterial material) => _entries.ContainsKey(material);

        internal IEnumerable<KeyValuePair<TMaterial, EmissionEntry>> Entries => _entries;

        /// <summary>Captures a material's originals. false (and no change) when the material is already known.</summary>
        internal bool TryAdd(TMaterial material, EmissionEntry entry)
        {
            if (material == null || entry == null || entry.Family == EmissionFamily.None || _entries.ContainsKey(material)) return false;
            _entries.Add(material, entry);
            return true;
        }

        internal void Clear() => _entries.Clear();

        /// <summary>
        /// Brings every captured material to the scale <paramref name="cap"/> asks for. A material whose property no longer holds what this ledger
        /// last wrote (another controller moved it) is left alone and counted as foreign; a destroyed material is dropped.
        /// </summary>
        internal EmissionPassResult Apply(IEmissionSurface<TMaterial> surface, float cap)
        {
            var result = new EmissionPassResult();
            List<TMaterial> gone = null;
            foreach (var pair in _entries)
            {
                var entry = pair.Value;
                if (!surface.TryRead(pair.Key, entry.Family, out var current))
                {
                    (gone ?? (gone = new List<TMaterial>())).Add(pair.Key);
                    result.Gone++;
                    continue;
                }
                float scale = EmissionCapModel.DesiredScale(entry.Level, cap);
                if (scale != entry.AppliedScale)
                {
                    if (!EmissionCapModel.SameValues(entry.Family, current, entry.ValuesAt(entry.AppliedScale)))
                    {
                        result.Foreign++;
                        continue;
                    }
                    surface.Write(pair.Key, entry.Family, entry.ValuesAt(scale));
                    entry.AppliedScale = scale;
                    result.Written++;
                }
                result.Materials++;
                if (entry.AppliedScale < 1f)
                {
                    result.Capped++;
                    if (entry.Level > result.LargestOriginal) { result.LargestOriginal = entry.Level; result.LargestName = entry.Name; }
                }
            }
            if (gone != null) foreach (var key in gone) _entries.Remove(key);
            return result;
        }

        /// <summary>Writes every original back (cap off); the entries stay, so a later <see cref="Apply"/> needs no new scan.</summary>
        internal EmissionPassResult Restore(IEmissionSurface<TMaterial> surface) => Apply(surface, 0f);
    }
}
