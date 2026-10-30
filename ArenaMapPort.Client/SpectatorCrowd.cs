#nullable disable

namespace ArenaMapPort.Client
{
    /// <summary>
    /// What Arena does: the crowd of a map murmurs; the only crowd ambience clip Arena ships is the results-screen bed (arena_fans_ambient_postmatch). What the port
    /// does: the crowd bed (SpectatorCrowdBed.cs) plays that clip as a looping murmur from every stand of the original spectator crowd, and a mode plugin (the user's
    /// ArenaMode) can lift or lower it with the state of the match.
    ///
    /// This is the public, process-global contract for that, in the style of <see cref="ScoreMonitors"/>: a public static class, primitive parameters only, found by
    /// reflection (<c>assembly.GetType("ArenaMapPort.Client.SpectatorCrowd")</c> from the plugin <c>com.maschine.ArenaMapPort</c>) with exact signature checks. Members
    /// are properties and methods, never fields or constants, so a binder written for ScoreMonitors binds this class the same way.
    ///
    /// Contract version 1, members (APPEND-ONLY: a member is never removed or changed in meaning without raising <see cref="ContractVersion"/>; new members do not
    /// raise it; a pin test lists them): <c>int ContractVersion</c>, <c>bool IsAvailable</c>, <c>float Excitement</c>, <c>void SetExcitement(float level01)</c>.
    ///
    /// Semantics. Every member may be called from any thread (state only; the Unity objects are touched by the bed's own main-thread tick) and none throws.
    /// <see cref="SetExcitement"/> is idempotent and last-write-wins: calling it every frame with an unchanged value costs one volatile write. The level is the
    /// target, not the loudness: the stands follow it with a time constant of two seconds, and it maps to a volume factor from 1.0 (0, calm) to 1.6 (1, peak) on top
    /// of the player's CrowdBedVolume. A level outside 0..1 is clamped and NaN counts as 0. The level is process-global and survives until it is set again; it is
    /// reset to 0 when the raid's crowd (and with it the bed) is removed, so a match that ended without a final call does not leave the next raid excited.
    /// </summary>
    public static class SpectatorCrowd
    {
        // Replaced by the tests (a fresh state per test).
        internal static CrowdBedState State = new CrowdBedState();

        /// <summary>1. Raised only when a member is removed or changes meaning; new members do not change it.</summary>
        public static int ContractVersion => 1;

        /// <summary>An owned map's raid is loaded and its crowd has a murmur bed that is switched on (<c>Spectators/CrowdBed</c>, a developer key that is on for
        /// players). False in a menu, on a vanilla map, when the installed package has no bed and while the bed is off; a plugin may skip its calls then (they are
        /// harmless either way).</summary>
        public static bool IsAvailable { get { try { return State.Available; } catch { return false; } } }

        /// <summary>The level most recently set with <see cref="SetExcitement"/> (0..1); 0 until set and after a raid's crowd was removed.</summary>
        public static float Excitement { get { try { return State.Excitement; } catch { return 0f; } } }

        /// <summary>Sets the target excitement of the crowd, 0 (calm) to 1 (peak): a kill, a close score or the last seconds of a match. Never throws.</summary>
        public static void SetExcitement(float level01)
        {
            try { State.SetExcitement(level01); } catch { }
        }
    }
}
