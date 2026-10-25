#nullable disable
using System;
using System.Collections.Generic;
using System.Text;
using ArenaMapPort.Shared;

namespace ArenaMapPort.Client
{
    /// <summary>How the clock of the timer faces is driven (<c>ScoreMonitors.SetClock</c>, argument <c>mode</c>).</summary>
    internal static class MonitorClockMode
    {
        internal const int None = 0;      // no clock: the timer faces read 00:00
        internal const int Frozen = 1;    // shows <c>seconds</c> as it is (whole seconds, rounded up like a countdown)
        internal const int CountDown = 2; // <c>seconds</c> is the absolute UnityEngine.Time.time at which the clock reaches 0
        internal const int CountUp = 3;   // <c>seconds</c> is the absolute UnityEngine.Time.time at which the clock was 0

        internal static bool IsValid(int mode) => mode >= None && mode <= CountUp;
    }

    /// <summary>One immutable copy of everything the monitors display; the hub hands it out under its lock, the view reads it on the main thread.</summary>
    internal struct ScoreMonitorSnapshot
    {
        internal int Claim;          // 0 = nobody claimed the monitors
        internal string Owner;
        internal bool Demo;          // the developer demo owns the claim (a plugin claim takes it over)
        internal int ColorA, ColorB; // 0xRRGGBB, or -1 = Arena's red (A) / blue (B)
        internal int TeamSize;       // toggles per side on the alive faces, 0..MaxTeamSize
        internal int ScoreA, ScoreB; // 0..MaxScore
        internal int AliveA, AliveB; // -1 = unknown, else 0..MaxTeamSize
        internal int ClockMode;      // MonitorClockMode
        internal float ClockSeconds; // see MonitorClockMode
        internal int Version;        // changes with every accepted change of any field above
    }

    /// <summary>Pure rules of the monitors that need no Unity type: the clamps of the contract, the clock, the text of a number, and which rows of a map are usable.</summary>
    internal static class ScoreMonitorModel
    {
        internal const int MaxScore = 9999, MaxTeamSize = 16, DefaultTeamSize = 5, MaxClockSeconds = 99 * 60 + 59, OwnerMax = 40;

        /// <summary>The owner name as it is compared and logged: trimmed, at most <see cref="OwnerMax"/> characters, printable ASCII only (anything else becomes '?').
        /// Null for null or blank.</summary>
        internal static string CleanOwner(string owner)
        {
            if (string.IsNullOrWhiteSpace(owner)) return null;
            var sb = new StringBuilder(Math.Min(owner.Length, OwnerMax));
            foreach (char c in owner.Trim())
            {
                if (sb.Length >= OwnerMax) break;
                sb.Append(c >= ' ' && c <= '~' ? c : '?');
            }
            return sb.Length == 0 ? null : sb.ToString().TrimEnd();
        }

        internal static int ClampScore(int value) => value < 0 ? 0 : value > MaxScore ? MaxScore : value;

        internal static int ClampTeamSize(int value) => value < 0 ? 0 : value > MaxTeamSize ? MaxTeamSize : value;

        /// <summary>-1 (unknown) for any negative value, else 0..<see cref="MaxTeamSize"/>. The lit toggles of a side are further limited by the team size (<see cref="ScoreMonitorFrames.Lit"/>).</summary>
        internal static int ClampAlive(int value) => value < 0 ? -1 : value > MaxTeamSize ? MaxTeamSize : value;

        /// <summary>0xRRGGBB as given, or -1 (Arena's own red / blue) for a negative value or one above 0xFFFFFF.</summary>
        internal static int NormalizeColor(int rgb) => rgb < 0 || rgb > 0xFFFFFF ? -1 : rgb;

        /// <summary>The whole second the timer faces show at <paramref name="now"/> (UnityEngine.Time.time), 0..<see cref="MaxClockSeconds"/>, or -1 for "no clock"
        /// (the faces then read 00:00). A countdown and a frozen value round up like ArenaMode's HUD (Math.Ceiling of the time left), a count-up rounds down. Computed in
        /// double so that an absolute Time.time in the hours still gives exact seconds. This is the value the view compares to its last one, so a clock costs one
        /// string per second and nothing in between.</summary>
        internal static int ClockSecond(int mode, float seconds, float now)
        {
            double s = Finite(seconds);
            switch (mode)
            {
                case MonitorClockMode.Frozen: return ClampClock(Math.Ceiling(s));
                case MonitorClockMode.CountDown: return ClampClock(Math.Ceiling(s - (double)now));
                case MonitorClockMode.CountUp: return ClampClock(Math.Floor((double)now - s));
                default: return -1;
            }
        }

        private static double Finite(float v) => float.IsNaN(v) || float.IsInfinity(v) ? 0.0 : v;

        private static int ClampClock(double seconds) => seconds <= 0.0 ? 0 : seconds >= MaxClockSeconds ? MaxClockSeconds : (int)seconds;

        /// <summary>The two digits of the minutes text for a clock second (-1 = no clock = "00").</summary>
        internal static string MinutesText(int clockSecond) => Two(clockSecond < 0 ? 0 : clockSecond / 60);

        /// <summary>The two digits of the seconds text for a clock second (-1 = no clock = "00").</summary>
        internal static string SecondsText(int clockSecond) => Two(clockSecond < 0 ? 0 : clockSecond % 60);

        private static string Two(int v) => v < 10 ? "0" + v : v.ToString();

        /// <summary>The rows of a map that get a surface: active in the imported scene (AirPit's two team-box screens and Bowl's inactive base set stay dark) and a type
        /// word with a face (Equator's player boards and Bowl's word 10 stay dark).</summary>
        internal static ArenaScoreMonitor[] UsableRows(ArenaScoreMonitor[] rows)
        {
            var list = new List<ArenaScoreMonitor>();
            if (rows != null)
            {
                foreach (ArenaScoreMonitor row in rows)
                {
                    if (row != null && row.ActiveInScene && ScoreMonitorLayout.FaceOf(row.ArenaType) != ScoreMonitorFace.None) list.Add(row);
                }
            }
            return list.ToArray();
        }
    }

    /// <summary>What the faces show for one snapshot at one clock second: formatted once per change, read by every monitor of a face. A value type; the view compares
    /// the fields with what each monitor already shows, so a monitor whose face does not use a field never touches an object for it.</summary>
    internal struct ScoreMonitorFrame
    {
        internal string Minutes, Colon, Seconds;   // timer faces: "09" ":" "01"
        internal string ScoreA, ScoreB;            // score faces
        internal int ColorA, ColorB;               // score and alive faces: the contract colours (-1 = Arena's), already normalised
        internal int TeamSize, LitA, LitB;         // alive faces: toggles per side and how many of them are lit
    }

    internal static class ScoreMonitorFrames
    {
        /// <summary>The number of lit toggles of a side: its alive count, but at most the team size; an unknown count (-1) lights none.</summary>
        internal static int Lit(int alive, int teamSize)
        {
            if (alive < 0) return 0;
            int size = ScoreMonitorModel.ClampTeamSize(teamSize);
            return alive > size ? size : alive;
        }

        /// <summary>The frame for a snapshot and a clock second (<see cref="ScoreMonitorModel.ClockSecond"/>, -1 = no clock).</summary>
        internal static ScoreMonitorFrame Build(in ScoreMonitorSnapshot s, int clockSecond)
        {
            return new ScoreMonitorFrame
            {
                Minutes = ScoreMonitorModel.MinutesText(clockSecond),
                Colon = ":",
                Seconds = ScoreMonitorModel.SecondsText(clockSecond),
                ScoreA = ScoreMonitorModel.ClampScore(s.ScoreA).ToString(),
                ScoreB = ScoreMonitorModel.ClampScore(s.ScoreB).ToString(),
                ColorA = ScoreMonitorModel.NormalizeColor(s.ColorA),
                ColorB = ScoreMonitorModel.NormalizeColor(s.ColorB),
                TeamSize = ScoreMonitorModel.ClampTeamSize(s.TeamSize),
                LitA = Lit(s.AliveA, s.TeamSize),
                LitB = Lit(s.AliveB, s.TeamSize),
            };
        }
    }
}
