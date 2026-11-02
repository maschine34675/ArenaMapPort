using System;
using System.Diagnostics;
using BepInEx.Configuration;
using UnityEngine;

namespace ArenaMapPort.Client
{
    /// <summary>
    /// The developer test for the crowd's reactions (<c>Spectators/ReactionDemo</c> Off|Cycle|Once, <c>Spectators/ReactionDemoKind</c>). A mode plugin (ArenaMode) is the only real
    /// caller of <see cref="SpectatorCrowd.React"/>; this stands in for it on the host: it claims the reactions through the PUBLIC contract as "developer-demo" and fires the steps of
    /// <see cref="SpectatorReactionDemoPlan"/> (every implemented kind at stepped strengths; TakeCover once panic-only and once Directed, which only the internal overload can say),
    /// so the voice counts, the strength bands, the cooldowns, the hold of the random calls and the bed swell can be heard and read in the log without a match.
    /// It yields to a plugin's claim (the contract's rule for the demo owner), never takes the reactions from one, and releases its claim when the key goes Off. Developer keys like
    /// <c>Audio diagnostics/SpectatorCalls</c>: honoured only with DeveloperMode, live; outside it the demo is off. It is ticked by <see cref="SpectatorAudio"/> only while the
    /// reactions are running (a started raid with a crowd), so it costs nothing elsewhere. The host trigger is <c>uibridge_config_set</c>.
    /// </summary>
    internal static class SpectatorReactionDemo
    {
        const string Tag = "[SpectatorAudio]";
        const double TickSeconds = 0.1, RefusedSeconds = 5.0;
        static ConfigEntry<ReactionDemoMode> _mode;
        static ConfigEntry<int> _kind;
        static bool _installed;
        static readonly SpectatorReactionDemoPlan Plan = new SpectatorReactionDemoPlan();
        static int _claim;
        static double _next;

        internal static void Install()
        {
            if (_installed) return;
            _installed = true;
            var s = Plugin.Instance.Settings;
            _mode = s.Bind("Spectators", "ReactionDemo", ReactionDemoMode.Off,
                "Developer test: the spectators react without a mode plugin. Cycle fires every reaction kind at stepped strengths (Approve and Disapprove at 0.3, 0.6 and 0.9, TakeCover " +
                "panic-only and directed, Start, Finish; 9 s apart, about 100 s a pass) and repeats; Once runs one pass; Off stops it and gives the claim back. The claim is owned by " +
                "'developer-demo' and a plugin's claim takes it over. Needs a started raid on a map with a crowd. Live. Ignored outside DeveloperMode.");
            _kind = s.Bind("Spectators", "ReactionDemoKind", 0, new ConfigDescription(
                "Developer test: which kind the demo fires, 0 = all, 1 Approve, 2 Disapprove, 3 TakeCover, 4 Start, 5 Finish. Live. Ignored outside DeveloperMode.",
                new AcceptableValueRange<int>(0, CrowdReactionKind.Finish)));
        }

        /// <summary>The demo's mode now: Off outside DeveloperMode and when the keys were never bound.</summary>
        static ReactionDemoMode ModeNow => DeveloperOptions.Enabled && _mode != null ? _mode.Value : ReactionDemoMode.Off;

        /// <summary>Called once per frame by the reaction section of SpectatorAudio while reactions run; <paramref name="now"/> is Time.realtimeSinceStartup. Never throws.</summary>
        internal static void Tick(double now)
        {
            var mode = ModeNow;
            if (mode == ReactionDemoMode.Off)
            {
                if (_claim != 0 || Plan.Running) Stop("off");
                return;
            }
            if (now < _next) return;
            _next = now + TickSeconds;
            try
            {
                var queue = SpectatorCrowd.Reactions;
                // A plugin took the reactions over, or the crowd was removed and the queue was reset: the demo's claim is gone.
                if (_claim != 0 && !string.Equals(queue.HolderOwner, CrowdReactionQueue.DemoOwner, StringComparison.Ordinal)) Stop("the claim is gone");
                if (_claim == 0)
                {
                    if (queue.ExternalClaimed) return;                                   // the demo waits for the plugin to let go
                    _claim = SpectatorCrowd.ClaimReactions(CrowdReactionQueue.DemoOwner);
                    if (_claim == 0) { _next = now + RefusedSeconds; return; }           // refused (no crowd, reactions off): not every tenth of a second
                    Plugin.Log.LogDebug($"{Tag} reaction demo {mode}: claimed the reactions as '{CrowdReactionQueue.DemoOwner}'");
                }
                if (!Plan.TryNext(now, mode, _kind != null ? _kind.Value : 0, out var step)) return;
                // The internal overload carries the line-of-fire flag; the public React is the same call without it.
                bool queued = step.Directed
                    ? queue.React(_claim, step.Kind, step.Strength, true, Stopwatch.GetTimestamp())
                    : SpectatorCrowd.React(_claim, step.Kind, step.Strength);
                Plugin.Log.LogDebug($"{Tag} reaction demo: {CrowdReactionKind.Name(step.Kind)} strength={step.Strength:F2}{(step.Directed ? " directed" : "")} queued={queued}");
                if (!queued) Stop("the claim ended");
            }
            catch (Exception e)
            {
                _claim = 0;
                Plan.Stop();
                _next = now + RefusedSeconds;
                Plugin.Log.LogDebug($"{Tag} reaction demo stopped ({e.GetType().Name}: {e.Message})");
            }
        }

        /// <summary>The crowd is removed or the demo goes Off: the claim is given back and the pass forgotten.</summary>
        internal static void Stop(string why)
        {
            try
            {
                if (_claim != 0) SpectatorCrowd.ReleaseReactions(_claim);
                if (_claim != 0 || Plan.Running) Plugin.Log.LogDebug($"{Tag} reaction demo stopped ({why})");
            }
            catch (Exception) { /* a developer test never disturbs the game */ }
            _claim = 0;
            Plan.Stop();
        }
    }
}
