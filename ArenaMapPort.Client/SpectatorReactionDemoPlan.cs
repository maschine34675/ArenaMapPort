#nullable disable
using System;
using System.Collections.Generic;

namespace ArenaMapPort.Client
{
    /// <summary>Spectators/ReactionDemo: Off, Cycle (the pass repeats) or Once (one pass of Spectators/ReactionDemoKind, then it stops until the key is set to Off and back).</summary>
    internal enum ReactionDemoMode { Off, Cycle, Once }

    /// <summary>One request of the developer demo: <see cref="At"/> seconds after the pass began, a kind (1 to 5), a strength and, for TakeCover, the line-of-fire flag.</summary>
    internal struct ReactionDemoStep
    {
        public double At;
        public int Kind;
        public float Strength;
        public bool Directed;
    }

    /// <summary>
    /// The Unity-free half of the developer demo (<c>Spectators/ReactionDemo</c>, SpectatorReactionDemo.cs): what it fires and when. Without a mode plugin the demo claims the reactions as
    /// "developer-demo" and walks one pass over the kinds the crowd implements, each at stepped strengths, so every voice count and clip band can be heard on the host:
    /// Approve and Disapprove at 0.30 (one voice; Disapprove laments only), 0.60 (two voices; Disapprove from 0.4 with the heckles) and 0.90 (three voices; Approve from 0.8 with a
    /// shout first), TakeCover at 0.50 once panic-only and once Directed (the plugin's shot sensor sets that flag, the public contract never does), then Start and Finish at 1.0.
    ///
    /// The table obeys the planner's own rules at gap scale 1, which a test proves with the real <see cref="CrowdReactionPlanner"/>: steps are <see cref="SpacingSeconds"/> apart (longer
    /// than the longest reaction, so the three reaction slots are free again), two steps of one kind are at least that kind's cooldown (halved from strength 0.8) plus
    /// <see cref="MarginSeconds"/> apart, and a Cycle's pass is long enough that its first step of a kind clears the cooldown of the last step of that kind in the previous pass.
    /// Pure: the caller's clock in, the due step out.
    /// </summary>
    internal sealed class SpectatorReactionDemoPlan
    {
        /// <summary>Seconds between two steps: more than the longest reaction (a 6.7 s clip plus the 0.9 s of staggering).</summary>
        internal const double SpacingSeconds = 9.0, MarginSeconds = 1.0, FirstDelaySeconds = 1.0;
        /// <summary>A step more than this late (a stalled game) is not fired in a burst: the pass is moved so that the step is due now.</summary>
        internal const double LateSeconds = SpacingSeconds;
        internal const float Low = 0.30f, Middle = 0.60f, High = 0.90f, Cover = 0.50f, Full = 1.0f;

        ReactionDemoStep[] _steps = new ReactionDemoStep[0];
        double _start, _cycle;
        int _next, _filter = -1;
        bool _running, _done;
        ReactionDemoMode _mode;

        /// <summary>The steps of one pass: every implemented kind in the order Approve, Disapprove, TakeCover (panic, directed), Start, Finish when <paramref name="kindFilter"/> is 0, only
        /// that kind when it is 1 to 5, and none for any other number. Times are relative to the pass and respect <see cref="SpacingSeconds"/> and every kind's cooldown.</summary>
        internal static ReactionDemoStep[] Pass(int kindFilter)
        {
            var raw = new List<ReactionDemoStep>();
            if (Wants(kindFilter, CrowdReactionKind.Approve)) foreach (float s in new[] { Low, Middle, High }) raw.Add(Step(CrowdReactionKind.Approve, s, false));
            if (Wants(kindFilter, CrowdReactionKind.Disapprove)) foreach (float s in new[] { Low, Middle, High }) raw.Add(Step(CrowdReactionKind.Disapprove, s, false));
            if (Wants(kindFilter, CrowdReactionKind.TakeCover)) { raw.Add(Step(CrowdReactionKind.TakeCover, Cover, false)); raw.Add(Step(CrowdReactionKind.TakeCover, Cover, true)); }
            if (Wants(kindFilter, CrowdReactionKind.Start)) raw.Add(Step(CrowdReactionKind.Start, Full, false));
            if (Wants(kindFilter, CrowdReactionKind.Finish)) raw.Add(Step(CrowdReactionKind.Finish, Full, false));
            var steps = raw.ToArray();
            for (int i = 1; i < steps.Length; i++)
            {
                double at = steps[i - 1].At + SpacingSeconds;
                for (int j = i - 1; j >= 0; j--)
                    if (steps[j].Kind == steps[i].Kind) { at = Math.Max(at, steps[j].At + Gap(steps[i].Kind, steps[i].Strength) + MarginSeconds); break; }
                steps[i].At = at;
            }
            return steps;
        }

        static bool Wants(int filter, int kind) => filter == 0 || filter == kind;

        static ReactionDemoStep Step(int kind, float strength, bool directed) => new ReactionDemoStep { Kind = kind, Strength = strength, Directed = directed };

        /// <summary>The cooldown a request of <paramref name="kind"/> at <paramref name="strength"/> meets at gap scale 1 (the planner's own table).</summary>
        internal static double Gap(int kind, float strength) => CrowdReactionPlanner.BaseGap(kind) * (strength >= CrowdReactionPlanner.StrongGap ? 0.5 : 1.0);

        /// <summary>The length of a repeating pass: after the last step one spacing, and long enough that the first step of every kind clears the cooldown of that kind's last step.</summary>
        internal static double CycleSeconds(ReactionDemoStep[] steps)
        {
            if (steps == null || steps.Length == 0) return SpacingSeconds;
            double length = steps[steps.Length - 1].At + SpacingSeconds;
            for (int i = 0; i < steps.Length; i++)
            {
                int first = i, last = i;
                for (int j = 0; j < steps.Length; j++)
                    if (steps[j].Kind == steps[i].Kind) { if (j < first) first = j; if (j > last) last = j; }
                if (first != i) continue;
                length = Math.Max(length, steps[last].At - steps[first].At + Gap(steps[first].Kind, steps[first].Strength) + MarginSeconds);
            }
            return length;
        }

        /// <summary>True while a pass is under way (started and not finished).</summary>
        internal bool Running => _running && !_done;

        internal ReactionDemoStep[] Steps => _steps;

        /// <summary>Seconds of one repeating pass of the current table.</summary>
        internal double Cycle => _cycle;

        /// <summary>Stops the demo; the next due step after a mode other than Off begins a fresh pass.</summary>
        internal void Stop()
        {
            _running = false; _done = false; _next = 0; _filter = -1; _mode = ReactionDemoMode.Off;
        }

        /// <summary>The step that is due at <paramref name="now"/> (the caller's clock in seconds), at most one per call. Off stops and returns false; a change of mode or kind begins a new
        /// pass <see cref="FirstDelaySeconds"/> after <paramref name="now"/>; Cycle wraps to the next pass after the last step; Once stops after it.</summary>
        internal bool TryNext(double now, ReactionDemoMode mode, int kindFilter, out ReactionDemoStep step)
        {
            step = default(ReactionDemoStep);
            if (mode == ReactionDemoMode.Off) { if (_running || _mode != ReactionDemoMode.Off) Stop(); return false; }
            if (kindFilter < 0 || kindFilter > CrowdReactionKind.Finish) kindFilter = 0;
            if (!_running || mode != _mode || kindFilter != _filter) Begin(now, mode, kindFilter);
            if (_done || _steps.Length == 0) return false;
            double late = now - _start - _steps[_next].At;
            if (late < 0) return false;
            if (late > LateSeconds) { _start = now - _steps[_next].At; }
            step = _steps[_next++];
            if (_next >= _steps.Length)
            {
                if (mode == ReactionDemoMode.Cycle) { _start += _cycle; _next = 0; }
                else _done = true;
            }
            return true;
        }

        void Begin(double now, ReactionDemoMode mode, int kindFilter)
        {
            _mode = mode; _filter = kindFilter; _running = true; _done = false; _next = 0;
            _steps = Pass(kindFilter);
            _cycle = CycleSeconds(_steps);
            _start = now + FirstDelaySeconds;
        }
    }
}
