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
    /// had (gain 3 dB, offset 0.4, range 3) until its listening pass; its keys are new, so no migration step touches them.
    /// Iceberg (PLAN-ICEBERG item 12, 2026-10-30) starts with gain 3 dB, offset 0 and range 2, also provisional until a listening pass: NOT
    /// Bowl's offset 0.4 and range 3, because the volume is <c>volume * gain + offset</c> and Iceberg's 20 neon-lamp loops are authored
    /// at 0.25 with a 3 m reach (17 of them; two reach 5 m, one 7 m): Bowl's mix would lift them to about 0.75 and 9 m, a hum in every interior, and would
    /// flatten the Common-in players (median 0.10) to about 0.54. With offset 0 the authored balance between the groups stays; range 2 still
    /// carries the far exterior sources (10 to 100 m) and 17 of the 20 lamps reach 6 m (the other three reach 10, 10 and 14 m). Its keys are new, so no migration step touches them.
    /// Autoservice (PLAN-AUTOSERVICE item 4, 2026-11-08; in <see cref="AmbientAudioProfiles.All"/> since item 12, 2026-11-11) starts with
    /// gain 3 dB, offset 0.2 and range 3, a start value until a listening pass: the plan's first guess was Bowl's row (3 dB, 0.4, 3), but nine of the
    /// 18 interior players are the <c>water_drops</c> season players authored at 0.07 (the other interior volumes are 0.12 x4, 0.15 x2, 0.2 x3; the exterior
    /// median is 0.30): the volume is <c>volume * gain + offset</c>, so offset 0.4 lifts the drops to about 0.50 (7x its authored level) against 0.68 for the 0.2 players
    /// and flattens the interior balance, while offset 0.2 gives about 0.30 against 0.37 to 0.48 (computed from the authored volumes, not heard).
    /// Offset 0 (Iceberg's row) would keep the authored 1:3 balance, with the 0.07 drops at about 0.10. Range 3 is the plan's value and untried: it carries
    /// the drops (20 m) to 60 m through an 80 m map, which may be too much for a quiet interior sound. Its keys are new, so no migration step touches them.
    /// Yard (PLAN-YARD item 6, 2026-11-20) is in the profile table since PLAN-YARD item 15 (2026-11-21) and starts with gain 3 dB,
    /// offset 0.1 and range 2, a start value until a listening pass, computed from the authored volumes and not heard: 31 of its 36 active players are
    /// exterior (CommonAmbientOutGroup, median volume 0.10, range 0.03 to 0.25) and the two loop hums <c>fridge</c> (0.05, reach 7 m) and <c>electricity</c>
    /// (0.03, reach 10 m) author minDistance 0 like Iceberg's lamps. The volume is <c>volume * gain + offset</c>: Bowl's offset 0.4 would put the hums at about
    /// 0.47 and 0.44 (9 to 15 times their authored level) and the 0.10 majority at 0.54, flattening
    /// the whole exterior mix; offset 0 (Iceberg's row) keeps the authored balance but leaves the 0.10 majority at 0.14. Offset 0.1 gives the majority about 0.24, the
    /// 0.25 players 0.45, the five interior players 0.24 to 0.52 and the hums 0.17 and 0.14, which keeps the hums below the median (0.71 and 0.59 of it against the authored 0.5
    /// and 0.3). Range 2 (Iceberg's, not Bowl's 3) carries the hums to 14 and 20 m instead of 21 and 30 m: a refrigerator and a power hum should not reach across the yard,
    /// while the exterior sources (7 to 300 m) already outlast the 80 x 150 m map at range 2. Its keys were new, so revisions 1 to 3 never touch them.
    /// 2026-11-21 (PLAN-YARD item 24, the author's pass on the built map): the author looked and listened on the author's own PC and wrote "sieht gut aus, auch nachts, ist dann halt wie die anderen
    /// maps im außenbereich recht dunkel. hört sich gut an, ambient sounds können etwas lauter sein (gain auf 5, range auf 3)". Yard's default is therefore gain 5 dB, offset 0.1
    /// (unchanged, the author named none) and range 3: the author's values by ear, replacing the computed start mix. By the formula above (gain 5 dB = x1.778, 3 dB = x1.413) the 0.10 majority
    /// goes from 0.24 to 0.28, the 0.25 players from 0.45 to 0.54, the fridge (0.05) from 0.17 to 0.19 and the electricity hum (0.03) from 0.14 to 0.15, so the hums stay below the median
    /// (0.68 and 0.55 of it); the reach goes from 2x to 3x the authored maxDistance: the fridge from 14 to 21 m and the electricity hum from 20 to 30 m (the author asked for range 3; the hums reaching 21 and 30 m is the computed
    /// consequence, not a separately heard item). Revision 4 of <see cref="Migrate"/> moves a saved Yard mix that still equals the start mix (3 / 0.1 / 2, saved at any revision 0 to 3) to the new default once and as a whole;
    /// every other saved Yard mix and every other map stay as they are (a saved Equator, Bay5 or Air Pit mix equal to its <c>PreviousValues</c> at revision 3 is a mix the user chose after
    /// the 2026-10-14 migration, so revision 4 never reuses the generic rule). Not host-checked yet.</summary>
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
                case "arena_iceberg": gainDb = 3f; offset = 0f; range = 2f; return; // provisional, PLAN-ICEBERG item 12 (see the class comment)
                case "arena_autoservice": gainDb = 3f; offset = .2f; range = 3f; return; // start value, PLAN-AUTOSERVICE item 4 (see the class comment)
                case "arena_yard": gainDb = 5f; offset = .1f; range = 3f; return; // the author's values by ear, 2026-11-21 (PLAN-YARD item 24, see the class comment); the start value was 3 / 0.1 / 2
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
                case "arena_iceberg": gainDb = 3f; offset = 0f; range = 2f; return; // likewise: no earlier default
                case "arena_autoservice": gainDb = 3f; offset = .2f; range = 3f; return; // likewise: no earlier default
                case "arena_yard": gainDb = 3f; offset = .1f; range = 2f; return; // revision 3 only: Yard's start mix is the first default; the revision-4 step names it too (YardStartGainDb/Offset/Range)
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
                case "arena_bowl":
                case "arena_iceberg":
                case "arena_autoservice":
                case "arena_yard": return false; // new keys: first appear with the provisional mix (PLAN-BOWL item 9, PLAN-ICEBERG item 12, PLAN-AUTOSERVICE item 4)
                default: throw new ArgumentException("No ambience mix history for " + mapKey, nameof(mapKey));
            }
        }

        internal static void Migrate(ConfigFile config, string mapKey,
            ConfigEntry<float> gain, ConfigEntry<float> offset, ConfigEntry<float> range)
        {
            bool legacy = HasLegacyMix(mapKey);
            var revision = config.Bind("Audio (" + mapKey + ")", "AmbientDefaultsRevision", 0,
                new ConfigDescription("Internal migration of the original ambience mix, rounded release offsets, the 2026-10-14 accepted gains and (revision 4) Yard's 2026-11-21 mix. Custom mixes are preserved.",
                    null, BrowsableAttribute.No));
            if (revision.Value >= 4) return;
            if (revision.Value < 3)
            {
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
            }
            // Revision 4 (2026-11-21, Yard only): the author's 5 / 0.1 / 3 replaces Yard's computed start mix 3 / 0.1 / 2, but only
            // where that start mix is still saved untouched, whatever the saved revision (Migrate stamped 3 on Yard at its first bind).
            // Every other map only advances its stamp: its PreviousValues at revision 3 is a mix chosen after 2026-10-14.
            if (mapKey == "arena_yard" && gain.Value == YardStartGainDb && offset.Value == YardStartOffset && range.Value == YardStartRange)
            {
                Values(mapKey, out float yardGain, out float yardOffset, out float yardRange);
                gain.Value = yardGain; offset.Value = yardOffset; range.Value = yardRange;
            }
            revision.Value = 4;
        }

        // Yard's computed start mix of 2026-11-21 (PLAN-YARD item 6), named explicitly for the revision-4 step.
        const float YardStartGainDb = 3f, YardStartOffset = .1f, YardStartRange = 2f;
    }
}
