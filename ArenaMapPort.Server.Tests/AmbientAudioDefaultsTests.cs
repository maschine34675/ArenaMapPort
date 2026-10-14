#nullable disable
using ArenaMapPort.Client;
using BepInEx.Configuration;
using Xunit;

namespace ArenaMapPort.Server.Tests;

public class AmbientAudioDefaultsTests
{
    // The accepted mixes (user decision 2026-10-14: the gains tuned in the user's config became the defaults).
    [Theory]
    [InlineData("arena_equator", 2f, .4f, 2.5f)]
    [InlineData("arena_bay5", 5f, .4f, 4f)]
    [InlineData("arena_airpit", 3f, .4f, 3f)]
    public void AcceptedMixIsTheDefault(string map, float gain, float offset, float range)
    {
        AmbientAudioDefaults.Values(map, out float defaultGain, out float defaultOffset, out float defaultRange);
        Assert.Equal((gain, offset, range), (defaultGain, defaultOffset, defaultRange));
    }

    // A legacy neutral mix (the keys' first defaults on Equator/Bay5) walks the whole chain once: revision 1 to the
    // first accepted mix, revision 3 on to today's; a mix the user zeroed afterwards is a custom mix and stays.
    [Theory]
    [InlineData("arena_equator", 2f, .4f, 2.5f)]
    [InlineData("arena_bay5", 5f, .4f, 4f)]
    public void LegacyNeutralMixMigratesOnlyOnce(string map, float expectedGain, float expectedOffset, float expectedRange)
    {
        var config = new ConfigFile();
        string section = "Audio (" + map + ")";
        var gain = config.Bind(section, "AmbientGainDb", 0f, "");
        var offset = config.Bind(section, "AmbientVolumeOffset", 0f, "");
        var range = config.Bind(section, "AmbientRangeMultiplier", 1f, "");
        AmbientAudioDefaults.Migrate(config, map, gain, offset, range);
        Assert.Equal((expectedGain, expectedOffset, expectedRange), (gain.Value, offset.Value, range.Value));
        Assert.Equal(3, config.Get<int>(section, "AmbientDefaultsRevision").Value);
        gain.Value = 0; offset.Value = 0; range.Value = 1;
        AmbientAudioDefaults.Migrate(config, map, gain, offset, range);
        Assert.Equal((0f, 0f, 1f), (gain.Value, offset.Value, range.Value));
    }

    // Revision 3: a saved mix still at the previous default (Equator 5 / 0.4 / 2.5, Bay5 4 / 0.4 / 4, AirPit's provisional
    // 4 / 0.4 / 3) was never touched and follows the accepted gain once; the stamp then protects a later manual return
    // to the old numbers.
    [Theory]
    [InlineData("arena_equator", 5f, .4f, 2.5f, 2f)]
    [InlineData("arena_bay5", 4f, .4f, 4f, 5f)]
    [InlineData("arena_airpit", 4f, .4f, 3f, 3f)]
    public void PreviousDefaultFollowsTheAcceptedGainOnce(string map, float oldGain, float oldOffset, float oldRange, float newGain)
    {
        var config = new ConfigFile();
        string section = "Audio (" + map + ")";
        config.Bind(section, "AmbientDefaultsRevision", 2, "");
        var gain = config.Bind(section, "AmbientGainDb", oldGain, "");
        var offset = config.Bind(section, "AmbientVolumeOffset", oldOffset, "");
        var range = config.Bind(section, "AmbientRangeMultiplier", oldRange, "");
        AmbientAudioDefaults.Migrate(config, map, gain, offset, range);
        Assert.Equal((newGain, oldOffset, oldRange), (gain.Value, offset.Value, range.Value));
        Assert.Equal(3, config.Get<int>(section, "AmbientDefaultsRevision").Value);
        gain.Value = oldGain;
        AmbientAudioDefaults.Migrate(config, map, gain, offset, range);
        Assert.Equal(oldGain, gain.Value);
    }

    // The user's own tuned values (the numbers that became the defaults) and every other custom mix are untouched by
    // revision 3, on AirPit as on the legacy maps.
    [Theory]
    [InlineData("arena_airpit", 3f, .4f, 3f)]
    [InlineData("arena_airpit", 0f, 0f, 1f)]
    [InlineData("arena_airpit", -6f, .1f, 2f)]
    [InlineData("arena_equator", 2f, .4f, 2.5f)]
    [InlineData("arena_bay5", 5f, .4f, 4f)]
    [InlineData("arena_bay5", 4f, .4f, 3.5f)]
    public void CustomAndAlreadyAcceptedMixesSurviveRevisionThree(string map, float savedGain, float savedOffset, float savedRange)
    {
        var config = new ConfigFile();
        string section = "Audio (" + map + ")";
        config.Bind(section, "AmbientDefaultsRevision", 2, "");
        var gain = config.Bind(section, "AmbientGainDb", savedGain, "");
        var offset = config.Bind(section, "AmbientVolumeOffset", savedOffset, "");
        var range = config.Bind(section, "AmbientRangeMultiplier", savedRange, "");
        AmbientAudioDefaults.Migrate(config, map, gain, offset, range);
        Assert.Equal((savedGain, savedOffset, savedRange), (gain.Value, offset.Value, range.Value));
        Assert.Equal(3, config.Get<int>(section, "AmbientDefaultsRevision").Value);
    }

    [Theory]
    [InlineData(0f, 0f, 1f)]
    [InlineData(-6f, .1f, 2f)]
    public void AirPitKeysAreNewSoNoLegacyMigrationTouchesAMix(float savedGain, float savedOffset, float savedRange)
    {
        // Equator and Bay5 migrate neutral defaults and rounded offsets from earlier releases. AirPit's keys never had
        // either, so even a deliberately neutral mix must survive, and the revision is only stamped.
        var config = new ConfigFile();
        const string section = "Audio (arena_airpit)";
        var gain = config.Bind(section, "AmbientGainDb", savedGain, "");
        var offset = config.Bind(section, "AmbientVolumeOffset", savedOffset, "");
        var range = config.Bind(section, "AmbientRangeMultiplier", savedRange, "");
        AmbientAudioDefaults.Migrate(config, "arena_airpit", gain, offset, range);
        Assert.Equal((savedGain, savedOffset, savedRange), (gain.Value, offset.Value, range.Value));
        Assert.Equal(3, config.Get<int>(section, "AmbientDefaultsRevision").Value);
        // A value equal to Bay5's unrounded legacy offset is not rewritten either.
        offset.Value = .399061f;
        config.Get<int>(section, "AmbientDefaultsRevision").Value = 0;
        AmbientAudioDefaults.Migrate(config, "arena_airpit", gain, offset, range);
        Assert.Equal(.399061f, offset.Value);
    }

    [Fact]
    public void OnlyMapsWithAcceptedMixesHaveDefaults()
    {
        foreach (var map in ArenaMapPort.Shared.ArenaMapRegistry.Catalog)
        {
            bool released = ArenaMapPort.Shared.ArenaMapRegistry.All.Any(r => r.Key == map.Key);
            if (released) { AmbientAudioDefaults.Values(map.Key, out _, out _, out _); continue; }
            Assert.Throws<ArgumentException>(() => AmbientAudioDefaults.Values(map.Key, out _, out _, out _));
            var config = new ConfigFile();
            var gain = config.Bind("Audio (" + map.Key + ")", "AmbientGainDb", 0f, "");
            var offset = config.Bind("Audio (" + map.Key + ")", "AmbientVolumeOffset", 0f, "");
            var range = config.Bind("Audio (" + map.Key + ")", "AmbientRangeMultiplier", 1f, "");
            Assert.Throws<ArgumentException>(() => AmbientAudioDefaults.Migrate(config, map.Key, gain, offset, range));
        }
        Assert.Throws<ArgumentException>(() => AmbientAudioDefaults.Values("", out _, out _, out _));
    }

    [Theory]
    [InlineData(0f, .2f, 1f)]
    [InlineData(-6f, 0f, 1f)]
    [InlineData(0f, 0f, 2f)]
    [InlineData(5f, .42f, 2.5f)]
    public void CustomMixIsPreservedAsAWhole(float savedGain, float savedOffset, float savedRange)
    {
        var config = new ConfigFile();
        const string section = "Audio (arena_equator)";
        var gain = config.Bind(section, "AmbientGainDb", savedGain, "");
        var offset = config.Bind(section, "AmbientVolumeOffset", savedOffset, "");
        var range = config.Bind(section, "AmbientRangeMultiplier", savedRange, "");
        AmbientAudioDefaults.Migrate(config, "arena_equator", gain, offset, range);
        Assert.Equal((savedGain, savedOffset, savedRange), (gain.Value, offset.Value, range.Value));
        Assert.Equal(3, config.Get<int>(section, "AmbientDefaultsRevision").Value);
    }

    [Theory]
    [InlineData("arena_equator", .4037559f)]
    [InlineData("arena_bay5", .399061f)]
    public void PriorAcceptedOffsetRoundsOnceWithoutChangingOtherMixComponents(string map, float priorOffset)
    {
        var config = new ConfigFile();
        string section = "Audio (" + map + ")";
        config.Bind(section, "AmbientDefaultsRevision", 1, "");
        var gain = config.Bind(section, "AmbientGainDb", -2f, "");
        var offset = config.Bind(section, "AmbientVolumeOffset", priorOffset, "");
        var range = config.Bind(section, "AmbientRangeMultiplier", 3f, "");
        AmbientAudioDefaults.Migrate(config, map, gain, offset, range);
        Assert.Equal((-2f, .4f, 3f), (gain.Value, offset.Value, range.Value));
        offset.Value = priorOffset;
        AmbientAudioDefaults.Migrate(config, map, gain, offset, range);
        Assert.Equal(priorOffset, offset.Value);
    }
}
