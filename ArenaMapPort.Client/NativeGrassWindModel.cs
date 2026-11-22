#nullable disable
namespace ArenaMapPort.Client
{
    /// <summary>
    /// The Unity-free arithmetic of the grass wind (NativeGrassWind.cs): EFT's <c>EFT.Weather.WindCalculator.SetWind</c> and <c>SetupFoliage</c> reproduced in float arithmetic, nothing else.
    /// The factor is the per-second step of the phase that scrolls the wave lookup of GPUInstancer/Foliage_Simple (<c>_WindTimeScaledVector</c>); the phase is advanced once per call with the frame's delta time.
    /// Parity with EFT, on purpose: no clamp of the time step and no wrap of the phase (EFT has neither), idle sway 0.3 (the default argument of <c>WindCalculator.Create</c>).
    /// </summary>
    internal static class NativeGrassWindModel
    {
        /// <summary>WindCalculator's idle sway (the default argument of Create), added to both wind components.</summary>
        internal const float IdleSway = 0.3f;

        /// <summary>The constant of WindCalculator.SetWind, as the decompile prints it.</summary>
        internal const float FactorScale = -0.020000001f;

        /// <summary>WindCalculator.SetWind: <c>magnitude = |wind|; (x, y) = (wind.x + idle, wind.y + idle); scale = -0.02 * (magnitude + 6 * idle); factor = (x * scale, y * scale)</c>.</summary>
        internal static void Factor(float windX, float windY, out float factorX, out float factorY)
        {
            float magnitude = (float)System.Math.Sqrt(windX * windX + windY * windY);
            float x = windX + IdleSway;
            float y = windY + IdleSway;
            float scale = FactorScale * (magnitude + 6f * IdleSway);
            factorX = x * scale;
            factorY = y * scale;
        }
    }

    /// <summary>The two state vectors of WindCalculator.SetupFoliage (z and w stay 0 in EFT, so they are not kept): the phase and the phase of the call before.</summary>
    internal sealed class NativeGrassWindPhase
    {
        internal float PhaseX;
        internal float PhaseY;
        internal float PreviousX;
        internal float PreviousY;

        /// <summary>SetupFoliage: previous = phase; phase += deltaTime * factor.</summary>
        internal void Advance(float deltaTime, float factorX, float factorY)
        {
            PreviousX = PhaseX;
            PreviousY = PhaseY;
            PhaseX += deltaTime * factorX;
            PhaseY += deltaTime * factorY;
        }

        internal void Reset()
        {
            PhaseX = 0f;
            PhaseY = 0f;
            PreviousX = 0f;
            PreviousY = 0f;
        }
    }
}
