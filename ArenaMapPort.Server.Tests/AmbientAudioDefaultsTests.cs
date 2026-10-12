#nullable disable
using ArenaMapPort.Client;
using BepInEx.Configuration;
using Xunit;

namespace ArenaMapPort.Server.Tests;

public class AmbientAudioDefaultsTests
{
    [Theory]
    [InlineData("arena_equator", 5f, .4f, 2.5f)]
    [InlineData("arena_bay5", 4f, .4f, 4f)]
    public void AcceptedMixMigratesOnlyOnce(string map, float expectedGain, float expectedOffset, float expectedRange)
    {
        var config = new ConfigFile();
        string section = "Audio (" + map + ")";
        var gain = config.Bind(section, "AmbientGainDb", 0f, "");
        var offset = config.Bind(section, "AmbientVolumeOffset", 0f, "");
        var range = config.Bind(section, "AmbientRangeMultiplier", 1f, "");
        AmbientAudioDefaults.Migrate(config, map, gain, offset, range);
        Assert.Equal((expectedGain, expectedOffset, expectedRange), (gain.Value, offset.Value, range.Value));
        AmbientAudioDefaults.Values(map, out float defaultGain, out float defaultOffset, out float defaultRange);
        Assert.Equal((expectedGain, expectedOffset, expectedRange), (defaultGain, defaultOffset, defaultRange));
        gain.Value = 0; offset.Value = 0; range.Value = 1;
        AmbientAudioDefaults.Migrate(config, map, gain, offset, range);
        Assert.Equal((0f, 0f, 1f), (gain.Value, offset.Value, range.Value));
    }

    // PROVISIONAL (lead decision 2026-10-12): the AirPit mix is not ear-tuned yet. This pins the number the driver binds
    // as the default so a retune is a deliberate edit of AmbientAudioDefaults and this row together.
    [Fact]
    public void AirPitStartsWithTheProvisionalMix()
    {
        AmbientAudioDefaults.Values("arena_airpit", out float gain, out float offset, out float range);
        Assert.Equal((4f, .4f, 3f), (gain, offset, range));
    }

    [Theory]
    [InlineData(4f, .4f, 3f)]
    [InlineData(0f, 0f, 1f)]
    [InlineData(-6f, .1f, 2f)]
    public void AirPitKeysAreNewSoNoLegacyMigrationTouchesAnyMix(float savedGain, float savedOffset, float savedRange)
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
        Assert.Equal(2, config.Get<int>(section, "AmbientDefaultsRevision").Value);
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
        Assert.Equal(2, config.Get<int>(section, "AmbientDefaultsRevision").Value);
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
