#nullable disable
using System;

namespace ArenaMapPort.Client
{
    /// <summary>
    /// The developer demo (<c>Monitors/Demo</c> = Cycle): a deterministic Team-Deathmatch-like cycle, so every face can be looked at on the host without a mode plugin.
    /// Pure: elapsed time in, one step out; the view pushes the step through the same hub setters a plugin uses.
    ///
    /// One cycle is 70 s in four sections. DIGITS (10 s, five slots of 2 s): both scores step through 0, 9, 99, 999 and 9999 (the widths to read on the score faces),
    /// the alive counts through 5, 4/3, 2/5, 0/1 and unknown (-1), the clock frozen at 0, 59, 600, 7200 (clamps to 99:59) and 3599 seconds. MATCH 5 (25 s): teams of
    /// five in Arena's own red and blue, scores climbing, the alive counts moving, the clock counting DOWN to the end of the section. MATCH 16 (25 s): teams of
    /// sixteen in the cyan and red of ArenaMode's HUD, the same numbers, the clock counting UP. RESULT (10 s): the final scores, everybody alive, the clock frozen at 0.
    /// </summary>
    internal static class ScoreMonitorDemo
    {
        internal const float DigitsSeconds = 10f, DigitSlotSeconds = 2f, Match5Seconds = 25f, Match16Seconds = 25f, ResultSeconds = 10f;
        internal const float CycleSeconds = DigitsSeconds + Match5Seconds + Match16Seconds + ResultSeconds;

        /// <summary>ArenaMode's HUD colours (friendly cyan, opponent red) as 0xRRGGBB.</summary>
        internal const int HudCyan = 0x7FDBFF, HudRed = 0xFF6B6B;

        internal static readonly int[] DigitScores = { 0, 9, 99, 999, 9999 };
        internal static readonly int[] DigitScoresB = { 0, 7, 88, 777, 9999 };
        internal static readonly int[] DigitAliveA = { 5, 4, 2, 0, -1 };
        internal static readonly int[] DigitAliveB = { 5, 3, 5, 1, -1 };
        internal static readonly int[] DigitClocks = { 0, 59, 600, 7200, 3599 };

        internal struct Step
        {
            internal int ColorA, ColorB, TeamSize;
            internal int ScoreA, ScoreB, AliveA, AliveB;
            internal int ClockMode;
            internal float ClockSeconds; // frozen: the value; counting: the absolute Time.time
        }

        /// <summary>The step <paramref name="elapsed"/> seconds after the demo began at the absolute time <paramref name="demoStart"/> (UnityEngine.Time.time); the cycle repeats.</summary>
        internal static Step Cycle(float elapsed, float demoStart)
        {
            if (float.IsNaN(elapsed) || float.IsInfinity(elapsed) || elapsed < 0f) elapsed = 0f;
            int cycles = (int)Math.Floor(elapsed / CycleSeconds);
            float t = elapsed - cycles * CycleSeconds;
            float cycleStart = demoStart + cycles * CycleSeconds;

            if (t < DigitsSeconds)
            {
                int slot = Math.Min(DigitScores.Length - 1, (int)(t / DigitSlotSeconds));
                return new Step
                {
                    ColorA = -1, ColorB = -1, TeamSize = 5, ScoreA = DigitScores[slot], ScoreB = DigitScoresB[slot],
                    AliveA = DigitAliveA[slot], AliveB = DigitAliveB[slot], ClockMode = MonitorClockMode.Frozen, ClockSeconds = DigitClocks[slot],
                };
            }
            t -= DigitsSeconds;
            if (t < Match5Seconds)
                return Match(t, 5, -1, -1, MonitorClockMode.CountDown, cycleStart + DigitsSeconds + Match5Seconds);
            t -= Match5Seconds;
            if (t < Match16Seconds)
                return Match(t, 16, HudCyan, HudRed, MonitorClockMode.CountUp, cycleStart + DigitsSeconds + Match5Seconds);
            t -= Match16Seconds;
            return new Step
            {
                ColorA = HudCyan, ColorB = HudRed, TeamSize = 16, ScoreA = FinalScore(Match16Seconds, 1.4f), ScoreB = FinalScore(Match16Seconds, 2.1f),
                AliveA = 16, AliveB = 16, ClockMode = MonitorClockMode.Frozen, ClockSeconds = 0f,
            };
        }

        static int FinalScore(float seconds, float perKill) => (int)(seconds / perKill);

        // One match section: the scores climb a point every 1.4 / 2.1 s, the alive counts dip by up to three, the clock runs against the section's end or from its start.
        static Step Match(float t, int teamSize, int colorA, int colorB, int clockMode, float clockSeconds)
        {
            int dipA = (int)(t / 4f) % 4, dipB = (int)(t / 3f) % 4;
            return new Step
            {
                ColorA = colorA, ColorB = colorB, TeamSize = teamSize, ScoreA = FinalScore(t, 1.4f), ScoreB = FinalScore(t, 2.1f),
                AliveA = Math.Max(0, teamSize - dipA), AliveB = Math.Max(0, teamSize - dipB), ClockMode = clockMode, ClockSeconds = clockSeconds,
            };
        }
    }
}
