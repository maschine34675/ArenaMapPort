using System;
using System.ComponentModel;
using BepInEx.Configuration;

namespace ArenaMapPort.Client
{
    /// <summary>The user's accepted map ambience mix (gain dB, volume offset, range multiplier) per released map.
    /// 2026-10-14 (user decision after the listening pass, "take the current values of the three maps as the
    /// default"): the gains the user had tuned in their config became the defaults - Equator 2 (was 5), Bay5 5 (was 4),
    /// AirPit 3 (was the provisional 4); offsets 0.4 and ranges 2.5 / 4 / 3 unchanged. Revision 3 of
    /// <see cref="Migrate"/> moves a saved mix that still equals the previous default to the new one and leaves every
    /// custom mix alone. Bowl (PLAN-BOWL item 9, 2026-10-18) starts with the same provisional mix AirPit's first release
    /// had (gain 3 dB, offset 0.4, range 3) until its listening pass; its keys are new, so no migration step touches them.</summary>
    internal static class AmbientAudioDefaults
    {
        internal static void Values(string mapKey, out float gainDb, out float offset, out float range)
        {
            switch (mapKey)
            {
                case "arena_equator": gainDb = 2f; offset = .4f; range = 2.5f; return;
                case "arena_bay5": gainDb = 5f; offset = .4f; range = 4f; return;
                case "arena_airpit": gainDb = 3f; offset = .4f; range = 3f; return;
                case "arena_bowl": gainDb = 3f; offset = .4f; range = 3f; return; // provisional, PLAN-BOWL item 9
                default: throw new ArgumentException("No accepted ambience mix for " + mapKey, nameof(mapKey));
            }
        }

        // The defaults each map shipped with before 2026-10-14 (AirPit: the provisional mix of 2026-10-12). A saved mix
        // that still equals them was never touched by the user and follows the new default once (revision 3).
        static void PreviousValues(string mapKey, out float gainDb, out float offset, out float range)
        {
            switch (mapKey)
            {
                case "arena_equator": gainDb = 5f; offset = .4f; range = 2.5f; return;
                case "arena_bay5": gainDb = 4f; offset = .4f; range = 4f; return;
                case "arena_airpit": gainDb = 4f; offset = .4f; range = 3f; return;
                // Bowl has no earlier default: the same as Values, so a saved mix at the provisional default "moves" to itself.
                case "arena_bowl": gainDb = 3f; offset = .4f; range = 3f; return;
                default: throw new ArgumentException("No ambience mix history for " + mapKey, nameof(mapKey));
            }
        }

        // Equator and Bay5 shipped ambience keys with neutral defaults before the accepted mix existed, and with the
        // unrounded offsets below; the revision-1/2 steps migrate those. AirPit's keys are new and first appear with
        // their provisional mix, so neither step may touch them (a deliberately neutral AirPit mix stays neutral).
        static bool HasLegacyMix(string mapKey)
        {
            switch (mapKey)
            {
                case "arena_equator":
                case "arena_bay5": return true;
                case "arena_airpit":
                case "arena_bowl": return false; // new keys: first appear with the provisional mix (PLAN-BOWL item 9)
                default: throw new ArgumentException("No ambience mix history for " + mapKey, nameof(mapKey));
            }
        }

        internal static void Migrate(ConfigFile config, string mapKey,
            ConfigEntry<float> gain, ConfigEntry<float> offset, ConfigEntry<float> range)
        {
            bool legacy = HasLegacyMix(mapKey);
            var revision = config.Bind("Audio (" + mapKey + ")", "AmbientDefaultsRevision", 0,
                new ConfigDescription("Internal migration of the original ambience mix, rounded release offsets and the 2026-10-14 accepted gains. Custom mixes are preserved.",
                    null, BrowsableAttribute.No));
            if (revision.Value >= 3) return;
            if (revision.Value < 2 && legacy)
            {
                // Treat the three controls as one mix. A deliberately neutral component in a
                // custom mix must not be independently raised by the migration.
                if (revision.Value < 1 && gain.Value == 0f && offset.Value == 0f && range.Value == 1f)
                {
                    // The pre-2026-10-14 accepted mix: revision 3 below then carries it on to today's default.
                    PreviousValues(mapKey, out float newGain, out float newOffset, out float newRange);
                    gain.Value = newGain; offset.Value = newOffset; range.Value = newRange;
                }
                // Round only the previously accepted offset; preserve independently customized
                // gains, ranges and every other offset (including a deliberate zero).
                float oldOffset = mapKey == "arena_equator" ? .4037559f : .399061f;
                if (offset.Value == oldOffset) offset.Value = .4f;
            }
            // Revision 3 (2026-10-14): a mix still at the previous default (the user never touched it) follows the
            // accepted gains; anything else is a custom mix and stays as a whole.
            PreviousValues(mapKey, out float prevGain, out float prevOffset, out float prevRange);
            if (gain.Value == prevGain && offset.Value == prevOffset && range.Value == prevRange)
            {
                Values(mapKey, out float acceptedGain, out float acceptedOffset, out float acceptedRange);
                gain.Value = acceptedGain; offset.Value = acceptedOffset; range.Value = acceptedRange;
            }
            revision.Value = 3;
        }
    }
}
