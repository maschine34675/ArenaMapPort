using System;
using System.ComponentModel;
using BepInEx.Configuration;

namespace ArenaMapPort.Client
{
    /// <summary>The user's accepted map ambience mix, captured before release cleanup.</summary>
    internal static class AmbientAudioDefaults
    {
        internal static void Values(string mapKey, out float gainDb, out float offset, out float range)
        {
            switch (mapKey)
            {
                case "arena_equator": gainDb = 5f; offset = .4f; range = 2.5f; return;
                case "arena_bay5": gainDb = 4f; offset = .4f; range = 4f; return;
                default: throw new ArgumentException("No accepted ambience mix for " + mapKey, nameof(mapKey));
            }
        }

        internal static void Migrate(ConfigFile config, string mapKey,
            ConfigEntry<float> gain, ConfigEntry<float> offset, ConfigEntry<float> range)
        {
            var revision = config.Bind("Audio (" + mapKey + ")", "AmbientDefaultsRevision", 0,
                new ConfigDescription("Internal migration of the original ambience mix and rounded release offsets. Custom mixes are preserved.",
                    null, BrowsableAttribute.No));
            if (revision.Value >= 2) return;
            // Treat the three controls as one mix. A deliberately neutral component in a
            // custom mix must not be independently raised by the migration.
            if (revision.Value < 1 && gain.Value == 0f && offset.Value == 0f && range.Value == 1f)
            {
                Values(mapKey, out float newGain, out float newOffset, out float newRange);
                gain.Value = newGain; offset.Value = newOffset; range.Value = newRange;
            }
            // Round only the previously accepted offset; preserve independently customized
            // gains, ranges and every other offset (including a deliberate zero).
            float oldOffset = mapKey == "arena_equator" ? .4037559f : .399061f;
            if (offset.Value == oldOffset) offset.Value = .4f;
            revision.Value = 2;
        }
    }
}
