#nullable disable

namespace ArenaMapPort.Client
{
    /// <summary>
    /// What Arena does: the crowd of a map murmurs; Arena ships in-match crowd beds (ambient_voice.bundle: Cheer, Angry, Neutral) on emitters that are inactive in
    /// the imported scenes. What the port does: the crowd bed (SpectatorCrowdBed.cs) plays the Neutral bed (arena_fans_ambient_neutral, since 2026-11-02) as a looping
    /// murmur from every stand of the original spectator crowd, and a mode plugin (the user's ArenaMode) can lift or lower it with the state of the match.
    ///
    /// This is the public, process-global contract for that, in the style of <see cref="ScoreMonitors"/>: a public static class, primitive parameters only, found by
    /// reflection (<c>assembly.GetType("ArenaMapPort.Client.SpectatorCrowd")</c> from the plugin <c>com.maschine.ArenaMapPort</c>) with exact signature checks. Members
    /// are properties and methods, never fields or constants, so a binder written for ScoreMonitors binds this class the same way.
    ///
    /// Contract version 1, members (APPEND-ONLY: a member is never removed or changed in meaning without raising <see cref="ContractVersion"/>; new members do not
    /// raise it; a pin test lists them): <c>int ContractVersion</c>, <c>bool IsAvailable</c>, <c>float Excitement</c>, <c>void SetExcitement(float level01)</c>, and
    /// since 2026-11-02 the four reaction members <c>int ReactionKinds</c>, <c>int ClaimReactions(string owner)</c>, <c>bool React(int claim, int kind, float strength01)</c>
    /// and <c>bool ReleaseReactions(int claim)</c>. A binder treats the reaction members as optional and binds them apart from <see cref="SetExcitement"/>: their absence means
    /// an older ArenaMapPort, a member with another signature is an incompatibility.
    ///
    /// Semantics. Every member may be called from any thread (state only; the Unity objects are touched by the bed's own main-thread tick) and none throws.
    /// <see cref="SetExcitement"/> is idempotent and last-write-wins: calling it every frame with an unchanged value costs one volatile write. The level is the
    /// target, not the loudness: the stands follow it with a time constant of two seconds, and it maps to a volume factor from 1.0 (0, calm) to 1.6 (1, peak) on top
    /// of the player's CrowdBedVolume. A level outside 0..1 is clamped and NaN counts as 0. The level is process-global and survives until it is set again; it is
    /// reset to 0 when the raid's crowd (and with it the bed) is removed, so a match that ended without a final call does not leave the next raid excited.
    ///
    /// Reactions. The spectators answer an event of the match with a few voices of Arena's own reaction clips (docs/PLAN-CROWD-REACTIONS.md) while Arena's Cheer or Angry crowd bed roars from the stands for the reaction's length (user decision 2026-11-07; the murmur bed itself is not lifted).
    /// They work with the bed switched off; <see cref="IsAvailable"/> keeps its meaning. <see cref="ReactionKinds"/> is a bit mask, <c>1 &lt;&lt; kind</c> for every kind the
    /// current raid's crowd can play, and 0 when it can play none (a menu, a vanilla map, Iceberg, a package without reaction clips, reactions switched off). The kinds are numbers
    /// from 1 to 31 (documented here, never constants of this class): 1 Approve (good for the viewer's side: an own kill, an award, a lead taken; the result of a round or a match is
    /// Finish), 2 Disapprove (bad: an own death, a lead lost, a point lost), 3 TakeCover (a shot or an aim toward a stand; the plugin's own sensor in a later release), 4 Start (a round
    /// or the match begins), 5 Finish (a round or the match ends with a result: neutral commentary, no winner is named). 6 (Lull) and 7 (PurifiedTeam) are reserved and absent from
    /// <see cref="ReactionKinds"/>. <see cref="ClaimReactions"/> returns a handle above 0, or 0 when refused: no owner name, no crowd that can react, or another owner holds the reactions
    /// (the same owner gets the same handle back; the developer demo, owner "developer-demo", yields to any other owner and never takes them from one). The handle is void when it was
    /// released, taken over, or the raid's crowd was removed: <see cref="React"/> then returns false, and so it does for a kind outside 1..31 or without a bit in
    /// <see cref="ReactionKinds"/>, a strength below 0.05 and NaN (a larger strength is clamped to 1). A true only means the reaction was queued: the crowd plays it when its cooldown,
    /// the pace of one start per 0.75 s and a free voice allow, and drops a request that has waited 2 s. The strength sets the number of voices (1 to 3) and the size of the roar, not
    /// the loudness.
    /// </summary>
    public static class SpectatorCrowd
    {
        // Replaced by the tests (a fresh state per test).
        internal static CrowdBedState State = new CrowdBedState();
        internal static CrowdReactionQueue Reactions = new CrowdReactionQueue();

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

        /// <summary>The reactions the current raid's crowd can play, as a mask of <c>1 &lt;&lt; kind</c> (kinds 1 to 31); 0 when it can play none. Never throws.</summary>
        public static int ReactionKinds { get { try { return Reactions.Kinds; } catch { return 0; } } }

        /// <summary>Claims the reactions of the current raid for <paramref name="owner"/>; a handle above 0, or 0 when refused (see the class comment). Never throws.</summary>
        public static int ClaimReactions(string owner)
        {
            try { return Reactions.Claim(owner); } catch { return 0; }
        }

        /// <summary>Asks the crowd to react: <paramref name="kind"/> 1 to 31, <paramref name="strength01"/> 0.05 to 1. True = queued; false = refused (see the class comment). Any thread,
        /// no Unity API, no allocation. Never throws.</summary>
        public static bool React(int claim, int kind, float strength01)
        {
            try { return Reactions.React(claim, kind, strength01); } catch { return false; }
        }

        /// <summary>Gives the reactions back; true when <paramref name="claim"/> was the holder. Reactions already queued still play. Never throws.</summary>
        public static bool ReleaseReactions(int claim)
        {
            try { return Reactions.Release(claim); } catch { return false; }
        }
    }
}
