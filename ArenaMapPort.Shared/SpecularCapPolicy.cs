#nullable disable
using System;

namespace ArenaMapPort.Shared
{
    /// <summary>Unity-independent ceiling on deferred specular color, not a brightness multiplier.</summary>
    public static class SpecularCapPolicy
    {
        public const string ColorProperty = "_SpecColor";
        public const string StrengthProperty = "_Specularness";

        public static bool MatchesShader(string name) =>
            name != null && name.StartsWith("p0/Reflective/", StringComparison.Ordinal);

        /// <summary>
        /// Return the single RGB scale needed to bound max(SpecColor.rgb * Specularness).
        /// The caller supplies the uncapped material, including any existing indoor adjustment.
        /// 1 disables the repair even for authored HDR values. Invalid data is left untouched.
        /// Alpha, Specularness, diffuse color, smoothness and reflection tint are never changed.
        /// </summary>
        public static float ColorScale(float red, float green, float blue, float strength, float cap)
        {
            if (!Finite(red) || !Finite(green) || !Finite(blue) || !Finite(strength) || !Finite(cap)
                || cap >= 1f || cap < 0f || red < 0f || green < 0f || blue < 0f || strength <= 0f)
                return 1f;
            double peak = (double)Math.Max(red, Math.Max(green, blue)) * strength;
            return peak > cap ? (float)(cap / peak) : 1f;
        }

        static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
