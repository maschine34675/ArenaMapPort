#nullable disable

namespace ArenaMapPort.Client
{
    /// <summary>
    /// What Arena does: the TV props of a map (<c>Arena_Home_theater</c>, each with an <c>ArenaMonitorHolder</c> world-space canvas, 1930 x 1163 at scale 0.00054
    /// under the prop) show a mode's timer, the team score or the alive counts; Arena's ECS systems fill them. What the ported scene lacks: the canvases (the extractor
    /// drops RectTransform objects), their content and those systems; the TVs are dead glass.
    ///
    /// What this does: a public, process-global contract for a mode plugin (the user's ArenaMode) to put its match on those TVs, in the style of
    /// <see cref="SpawnGates"/>: a public static class, primitive parameters only, one method per name, found by reflection
    /// (<c>assembly.GetType("ArenaMapPort.Client.ScoreMonitors")</c> from the plugin <c>com.maschine.ArenaMapPort</c>) with exact signature checks. A claim builds
    /// the faces of Arena's own templates on the map's active TV props (timer, score, alive; <see cref="ScoreMonitorLayout"/>, drawn by ScoreMonitorView) and fills
    /// them from what the plugin sets; nothing is built for a raid nobody claims, and the surfaces die with the raid's scenes.
    ///
    /// Contract version 1, members (APPEND-ONLY: a member is never removed or changed in meaning without raising <see cref="ContractVersion"/>; new members do
    /// not raise it; a pin test lists them):
    /// <c>int ContractVersion</c>, <c>bool IsAvailable</c>, <c>bool IsClaimed</c>, <c>int Count</c>, <c>int Claim(string owner)</c>, <c>bool Release(int claim)</c>,
    /// <c>bool SetSides(int claim, int colorA, int colorB, int teamSize)</c>, <c>bool SetScore(int claim, int scoreA, int scoreB, int aliveA, int aliveB)</c>,
    /// <c>bool SetClock(int claim, int mode, float seconds)</c>.
    ///
    /// Semantics. Every member may be called from any thread (state only; the Unity objects are touched by the plugin's own main-thread tick) and none throws. A
    /// setter is idempotent and last-write-wins: calling it every frame with unchanged values costs a lock and a compare. The first parameter of every setter is
    /// the handle <see cref="Claim"/> returned; a stale or foreign handle (released, replaced, or from an earlier raid) makes every setter return false and change
    /// nothing, so a mode that lost its claim notices at its next call. Side A is the LEFT block (Arena's red) and side B the RIGHT block (Arena's blue); which
    /// team is which is the caller's choice. Text is ASCII digits only.
    /// </summary>
    public static class ScoreMonitors
    {
        // Replaced by ScoreMonitorView.Install (the real raid and descriptor queries) and by the tests (fakes).
        internal static ScoreMonitorHub Hub = new ScoreMonitorHub(() => false, () => 0, () => false);

        /// <summary>1. Raised only when a member is removed or changes meaning; new members do not change it.</summary>
        public static int ContractVersion => 1;

        /// <summary>The feature is on (<c>Monitors/Enabled</c>), an owned map's raid is loaded and the map has monitors that can show something (an active TV prop with a
        /// timer, score or alive face). False in a menu, on a vanilla map and on a map without such monitors; a plugin skips its calls then.</summary>
        public static bool IsAvailable { get { try { return Hub.IsAvailable; } catch { return false; } } }

        /// <summary>True while a plugin or the developer demo holds the monitors of this raid.</summary>
        public static bool IsClaimed { get { try { return Hub.IsClaimed; } catch { return false; } } }

        /// <summary>Surfaces built and showing right now for the current claim (0 until the first frames after <see cref="Claim"/>, 0 after the raid). Diagnostic only;
        /// do not wait for it.</summary>
        public static int Count { get { try { return Hub.BuiltCount; } catch { return 0; } } }

        /// <summary>Takes the monitors of the current raid. Returns a positive handle, or 0 when it cannot: <paramref name="owner"/> is blank, the feature is off, no owned
        /// raid is loaded, the map has no usable monitors, or another owner holds them. The same owner claiming again gets the SAME handle and keeps the content. A
        /// developer demo claim never blocks a plugin: the plugin's claim takes the monitors over. Call it once per match after the raid started and release it on
        /// every exit path.</summary>
        public static int Claim(string owner)
        {
            try { return Hub.Claim(owner, false); } catch { return 0; }
        }

        /// <summary>Gives the monitors back; they go dark. False when the handle was not current (nothing changes).</summary>
        public static bool Release(int claim)
        {
            try { return Hub.Release(claim); } catch { return false; }
        }

        /// <summary>The two teams. <paramref name="colorA"/> / <paramref name="colorB"/> are 0xRRGGBB, or -1 for Arena's own red (A, the left block) and blue (B, the
        /// right block); a block is drawn at Arena's alpha 0.4902 over black. <paramref name="teamSize"/> (0..16, clamped; 5 until set, Arena's team size) is the number
        /// of toggles per side on the ALIVE faces; 0 draws none.</summary>
        public static bool SetSides(int claim, int colorA, int colorB, int teamSize)
        {
            try { return Hub.SetSides(claim, colorA, colorB, teamSize); } catch { return false; }
        }

        /// <summary>The numbers. Scores are 0..9999 (clamped) and drawn on the SCORE faces; <paramref name="aliveA"/> / <paramref name="aliveB"/> are the lit toggles on
        /// the ALIVE faces: 0..teamSize (the smaller of the count and the team size is lit) or -1 for unknown, which shows every toggle off.</summary>
        public static bool SetScore(int claim, int scoreA, int scoreB, int aliveA, int aliveB)
        {
            try { return Hub.SetScore(claim, scoreA, scoreB, aliveA, aliveB); } catch { return false; }
        }

        /// <summary>The clock of the TIMER faces. <paramref name="mode"/> 0 none (00:00), 1 frozen at <paramref name="seconds"/> (whole seconds, rounded up like a
        /// countdown), 2 counting down to the absolute <c>UnityEngine.Time.time</c> <paramref name="seconds"/>, 3 counting up from the absolute Time.time
        /// <paramref name="seconds"/>. The value clamps at 0 and reads MM:SS up to 99:59; the monitors tick by themselves, no call per second. False for an unknown
        /// mode (nothing changes); a non-finite value counts as 0.</summary>
        public static bool SetClock(int claim, int mode, float seconds)
        {
            try { return Hub.SetClock(claim, mode, seconds); } catch { return false; }
        }
    }
}
