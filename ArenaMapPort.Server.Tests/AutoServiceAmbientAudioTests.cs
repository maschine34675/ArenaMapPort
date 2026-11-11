#nullable disable
using System.Text.Json;
using ArenaMapPort.Client;
using Xunit;

namespace ArenaMapPort.Server.Tests;

/// <summary>
/// PLAN-AUTOSERVICE item 4 (ambient audio, package v2). The client profile row was STAGED in <c>AmbientAudioProfiles.Staged</c> until the
/// lock-step promotion (item 12, 2026-11-11) moved it into <see cref="AmbientAudioProfiles.All"/>; the generic table tests (<c>AmbientAudioProfilesTests</c>) cover it with the released maps. What is specific to
/// Autoservice is pinned here against the contract the validators check on the package and the bundle (<c>tools/extract/native_contracts/autoservice.json</c>,
/// key <c>audio</c>, filled by the v2 package): the 38 exported players, the nine water_drops with minDistance 0, the four far_fight players that force
/// Arena's CommonAmbOutBypass, and the dropped components (three Winter-only season loops and the inactive spline chain). Until the package-v2 stage
/// writes the audio block (or the contract file exists at all) every test that reads the contract fails with a missing file or a null audio block: that
/// is the pin working, not a regression.
/// </summary>
public sealed class AutoServiceAmbientAudioTests
{
    const string Key = "arena_autoservice";

    static string RepoFile(string relative)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, relative);
            if (File.Exists(path)) return path;
        }
        throw new FileNotFoundException(relative);
    }

    static string Text(string relative) => File.ReadAllText(RepoFile(relative));

    static JsonElement ContractAudio()
    {
        using var document = JsonDocument.Parse(Text("tools/extract/native_contracts/autoservice.json"));
        var audio = document.RootElement.GetProperty("audio");
        Assert.True(audio.ValueKind == JsonValueKind.Object, "the autoservice contract has no audio block yet: package v2 (PLAN-AUTOSERVICE item 4) fills it");
        return audio.Clone();
    }

    static AmbientAudioProfile Profile() => AmbientAudioProfiles.Get(Key);

    static (string Name, string Value)[] Pins(JsonElement states, string name) =>
        states.GetProperty(name).EnumerateObject().Select(p => (p.Name, p.Value.GetString())).Order().ToArray();

    [Fact]
    public void TheContractPinsTheSoundSceneOfAutoservice()
    {
        var audio = ContractAudio();
        Assert.Equal("ambient_audio", audio.GetProperty("feature").GetString());
        Assert.Equal(2, audio.GetProperty("schema").GetInt32());
        Assert.Equal("level112", audio.GetProperty("scene").GetString());
        Assert.Equal("ArenaMapPort_AutoServiceNativeAudio", audio.GetProperty("root").GetString());
        Assert.Equal("all_players", audio.GetProperty("group_coverage").GetString());
        // The exported census: the dropped components are not counted. 42 players stand in the source scene (23 random, 16 season, 3 season loops).
        var players = audio.GetProperty("players");
        Assert.Equal(new[] { ("AmbientSoundPlayer", 22), ("SeasonAmbientSoundPlayer", 16) },
            players.EnumerateObject().Select(p => (p.Name, p.Value.GetInt32())).Order().ToArray());
        Assert.Equal(new[] { ("AmbientSoundPlayerGroup", 2) },
            audio.GetProperty("support").EnumerateObject().Select(p => (p.Name, p.Value.GetInt32())).ToArray());
        var states = audio.GetProperty("states");
        Assert.Equal(38, states.GetProperty("grouped_players").GetInt32());
        Assert.Equal(new[] { ("CommonAmbientInGroup", "CommonAmbInEffects", 18), ("CommonAmbientOutGroup", "CommonAmbOutEffects", 20) },
            states.GetProperty("groups").EnumerateObject()
                .Select(g => (g.Name, g.Value.GetProperty("mixer").GetString(), g.Value.GetProperty("players").GetInt32())).ToArray());
        Assert.Empty(states.GetProperty("inactive_players").EnumerateObject());
        Assert.Equal(0, states.GetProperty("disabled_players").GetArrayLength());
        Assert.Empty(states.GetProperty("loops").EnumerateObject());
    }

    [Fact]
    public void TheNineWaterDropsAreTheOnlyPlayersWithMinDistanceZero()
    {
        // The exporter's reviewed rule (parse_player) is global, its use is pinned per contract (validate_graph): Iceberg 21, Autoservice 9, the others none.
        var zero = Pins(ContractAudio().GetProperty("states"), "zero_min_distance_players");
        Assert.Equal(9, zero.Length);
        Assert.All(zero, p => Assert.StartsWith("level112:", p.Name));
        Assert.All(zero, p => Assert.Matches(@"^water_drops( \(\d\))?$", p.Value));
        Assert.Equal(9, zero.Select(p => p.Value).Distinct().Count());
    }

    [Fact]
    public void TheFourFarFightPlayersForceTheBypassMixerAndAreRoutedLikeTheirGroup()
    {
        var forced = Pins(ContractAudio().GetProperty("states"), "forced_mixer_players");
        Assert.Equal(4, forced.Length);
        Assert.All(forced, p => Assert.Equal("CommonAmbOutBypass", p.Value));
        // The fifth forced player of the source scene (CommonAmbInEffects) is the ungrouped chain player: it is dropped, not forced.
        Assert.DoesNotContain(forced, p => p.Value == "CommonAmbInEffects");
        var states = ContractAudio().GetProperty("states");
        var dropped = Pins(states, "dropped_players").Select(p => p.Name).ToHashSet();
        Assert.False(forced.Any(p => dropped.Contains(p.Name)), "a dropped player must not carry another pin");
    }

    [Fact]
    public void TheWinterOnlyLoopsAndTheInactiveSplineChainAreDroppedNotPorted()
    {
        // The plan decides "summer set only" (user, 2026-11-08): the three SeasonLoopAmbientSoundPlayers carry the clip key Winter only (the exporter refuses to
        // drop a loop with any other key), and the spline chain sits under an inactive GameObject with an ungrouped player. The exporter reads, verifies and
        // audits them (index key dropped) and exports none; the importer, the driver and the profile never see them.
        var states = ContractAudio().GetProperty("states");
        var players = Pins(states, "dropped_players");
        Assert.Equal(4, players.Length);
        Assert.Equal(3, players.Count(p => p.Value.StartsWith("indoor_winds_winter")));
        Assert.Single(players, p => p.Value == "AmbientSoundPlayer");
        var support = Pins(states, "dropped_support");
        Assert.Equal(3, support.Length);
        Assert.Equal(new[] { "AmbientSoundPlayer", "LoopAmbientSoundPlayer_spline_emitter", "spline" }, support.Select(p => p.Value).Order().ToArray());
        // None of them is part of the exported census above: the group member counts (18 + 20 = 38) already exclude the three loops.
        Assert.Equal(38, ContractAudio().GetProperty("players").EnumerateObject().Sum(p => p.Value.GetInt32()));
    }

    [Fact]
    public void TheProfileMirrorsTheContract()
    {
        var audio = ContractAudio();
        var states = audio.GetProperty("states");
        var profile = Profile();
        Assert.Equal(audio.GetProperty("root").GetString(), profile.RootName);
        Assert.Equal(audio.GetProperty("players").EnumerateObject().Sum(p => p.Value.GetInt32()), profile.Players);
        Assert.Equal((38, 38, 2, 0), (profile.Players, profile.ActivePlayers, profile.Groups, profile.Controllers));
        Assert.Equal(states.GetProperty("grouped_players").GetInt32(), profile.GroupedPlayers);
        Assert.False(profile.GroupsListInactivePlayers);
        Assert.Equal(new[] { ("CommonAmbientInGroup", "CommonAmbInEffects", 18), ("CommonAmbientOutGroup", "CommonAmbOutEffects", 20) },
            profile.Routes.Select(r => (r.GroupName, r.MixerName, r.Members)).ToArray());
        Assert.Equal(states.GetProperty("group_mixers").EnumerateArray().Select(e => e.GetString()).ToArray(),
            profile.Routes.Select(r => r.MixerName).ToArray());
        Assert.Empty(profile.InactivePlayers);
        Assert.Empty(profile.LoopPlayers);
        Assert.True(profile.UsesSeasons);
        Assert.True(profile.OriginalActivation);
        Assert.False(profile.HasShoreline);
        // The strict driver check demands the exporter-kind counts: 16 season players, no loop-kind player (the dropped season loops are a class it never accepts).
        Assert.Equal((16, 0), (profile.SeasonKindPlayers, profile.LoopKindPlayers));
        Assert.Equal(audio.GetProperty("players").GetProperty("SeasonAmbientSoundPlayer").GetInt32(), profile.SeasonKindPlayers);
        Assert.False(audio.GetProperty("players").TryGetProperty("LoopAmbientSoundPlayer", out _));
        Assert.Equal(new[] { true, false }, profile.Routes.Select(r => r.ReverbReroute).ToArray());
        Assert.Equal(new[] { AmbientBus.In, AmbientBus.Out }, profile.Buses.ToArray());
        Assert.Equal("CommonAmbientInGroup", profile.EffectsRoute.GroupName);
    }

    [Fact]
    public void TheImporterKnowsTheRootAndTheAssetFolder()
    {
        var source = Text("UnityProject/Assets/ArenaMapPort/Editor/Import/Bay5AudioImporter.cs");
        Assert.Contains("const string AutoServiceRootName = \"ArenaMapPort_AutoServiceNativeAudio\";", source);
        Assert.Contains("case \"arena_autoservice\": return AutoServiceRootName;", source);
        Assert.Contains("case \"arena_autoservice\": return \"AutoService\";", source);
        Assert.Equal("ArenaMapPort_AutoServiceNativeAudio", Profile().RootName);
        Assert.Equal("AutoService", Profile().DisplayName);
    }

    [Fact]
    public void TheStartMixKeepsTheWaterDropsBelowBowlsLift()
    {
        // volume * gain + offset: Bowl's offset 0.4 would put the authored 0.07 drops at about 0.50; the start row keeps them near 0.30.
        AmbientAudioDefaults.Values(Key, out float gainDb, out float offset, out float range);
        Assert.Equal((3f, .2f, 3f), (gainDb, offset, range));
        double gain = Math.Pow(10, gainDb / 20.0);
        Assert.InRange(.07 * gain + offset, .29, .31);
        Assert.InRange(.07 * gain + .4, .49, .51);
    }
}
