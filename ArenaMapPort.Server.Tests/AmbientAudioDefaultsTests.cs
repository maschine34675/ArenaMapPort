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
