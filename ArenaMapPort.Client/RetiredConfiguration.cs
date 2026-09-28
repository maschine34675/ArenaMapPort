using System;
using System.Collections.Generic;
using System.Reflection;
using ArenaMapPort.Shared;
using BepInEx.Configuration;

namespace ArenaMapPort.Client
{
    /// <summary>
    /// BepInEx preserves unbound saved keys as orphaned entries. Remove only the exact settings
    /// proven ineffective by the 2026-09-28 audit; never clear unrelated or hidden-map settings.
    /// Runs after all bindings so a live entry always wins. Native acceptance and migration
    /// checks are recorded in docs/progress/config-cleanup.md.
    /// </summary>
    internal static class RetiredConfiguration
    {
        internal static int RemoveSavedEntries(ConfigFile config)
        {
            // Verified against installed BepInEx 5.4.23.5. Public Remove only touches bound entries,
            // while Save also writes this private dictionary; unbinding alone leaves stale keys.
            var property = typeof(ConfigFile).GetProperty("OrphanedEntries",
                BindingFlags.Instance | BindingFlags.NonPublic);
            if (!(property?.GetValue(config) is Dictionary<ConfigDefinition, string> orphaned))
                throw new InvalidOperationException("Unsupported BepInEx orphaned configuration layout");

            int removed = 0;
            foreach (var definition in Definitions())
                if (!config.ContainsKey(definition) && orphaned.Remove(definition)) removed++;
            if (removed > 0) config.Save();
            return removed;
        }

        static IEnumerable<ConfigDefinition> Definitions()
        {
            yield return new ConfigDefinition("Logging", "Verbose");
            foreach (var map in ArenaMapRegistry.All)
            {
                yield return new ConfigDefinition($"Lighting ({map.Key})", "CoverMinHeight");
                yield return new ConfigDefinition($"Lighting ({map.Key})", "AuthoredRainCover");
                yield return new ConfigDefinition($"LightingLab ({map.Key})", "AmbientCustomIntensity");
            }
            yield return new ConfigDefinition($"Lighting ({ArenaMapRegistry.Bay5.Key})", "SunIntensityMultiplier");
            yield return new ConfigDefinition($"Lighting ({ArenaMapRegistry.Bay5.Key})", "SunColorWhiteBlend");
            yield return new ConfigDefinition($"Lighting ({ArenaMapRegistry.Equator.Key})", "VertexGroundDiffuseMultiplier");
        }
    }
}
