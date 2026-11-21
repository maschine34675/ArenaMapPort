#nullable disable
using System.Text.Json;
using ArenaMapPort.Client;
using Xunit;

namespace ArenaMapPort.Server.Tests;

/// <summary>
/// PLAN-YARD item 6 (ambient audio, package v2). The client profile row was staged in <c>AmbientAudioProfiles.Staged</c> until the lock-step promotion
/// (item 15, 2026-11-21) moved it into <see cref="AmbientAudioProfiles.All"/>; the generic table tests (<c>AmbientAudioProfilesTests</c>) cover it too. What is specific to Yard is pinned here against the contract the validators check on the package and the bundle
/// (<c>tools/extract/native_contracts/yard.json</c>, key <c>audio</c>): 50 exported players of three kinds, the two minDistance-0 hums, the 14 players under an
/// inactive ancestor that no group lists, the one silent source clip, and the start mix.
/// </summary>
public sealed class YardAmbientAudioTests
{
    const string Key = "arena_yard";

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
        using var document = JsonDocument.Parse(Text("tools/extract/native_contracts/yard.json"));
        var audio = document.RootElement.GetProperty("audio");
        Assert.True(audio.ValueKind == JsonValueKind.Object, "the yard contract has no audio block");
        return audio.Clone();
    }

    static AmbientAudioProfile Profile() => AmbientAudioProfiles.Get(Key);

    static (string Name, string Value)[] Pins(JsonElement states, string name) =>
        states.GetProperty(name).EnumerateObject().Select(p => (p.Name, p.Value.GetString())).Order().ToArray();

    [Fact]
    public void TheContractPinsTheSoundSceneOfYard()
    {
        var audio = ContractAudio();
        Assert.Equal("ambient_audio", audio.GetProperty("feature").GetString());
        Assert.Equal(2, audio.GetProperty("schema").GetInt32());
        Assert.Equal("level163", audio.GetProperty("scene").GetString());
        Assert.Equal("ArenaMapPort_YardNativeAudio", audio.GetProperty("root").GetString());
        // Unlike Bowl and Iceberg the source groups list only the active players: the 14 inactive ones are in no group.
        Assert.Equal("active_players", audio.GetProperty("group_coverage").GetString());
        Assert.Equal(new[] { ("AmbientSoundPlayer", 26), ("LoopAmbientSoundPlayer", 2), ("SeasonAmbientSoundPlayer", 22) },
            audio.GetProperty("players").EnumerateObject().Select(p => (p.Name, p.Value.GetInt32())).Order().ToArray());
        Assert.Equal(new[] { ("AmbientSoundPlayerGroup", 2) },
            audio.GetProperty("support").EnumerateObject().Select(p => (p.Name, p.Value.GetInt32())).ToArray());
        Assert.Equal((13, 145), (audio.GetProperty("banks").GetInt32(), audio.GetProperty("clips").GetInt32()));
        var states = audio.GetProperty("states");
        Assert.Equal(36, states.GetProperty("grouped_players").GetInt32());
        Assert.Equal(new[] { ("CommonAmbientOutGroup", "CommonAmbOutEffects", 31), ("CommonAmbientInGroup", "CommonAmbInEffects", 5) },
            states.GetProperty("groups").EnumerateObject()
                .Select(g => (g.Name, g.Value.GetProperty("mixer").GetString(), g.Value.GetProperty("players").GetInt32())).ToArray());
        Assert.Equal(0, states.GetProperty("disabled_players").GetArrayLength());
        Assert.Empty(states.GetProperty("loops").EnumerateObject());
        // Nothing forces a mixer and nothing is dropped: Yard has no season-loop class, no spline chain and no far_fight player.
        Assert.False(states.TryGetProperty("forced_mixer_players", out _));
        Assert.False(states.TryGetProperty("dropped_players", out _));
        Assert.False(states.TryGetProperty("dropped_support", out _));
    }

    [Fact]
    public void TheFourteenInactivePlayersAreTheOnesNoGroupLists()
    {
        var states = ContractAudio().GetProperty("states");
        var inactive = Pins(states, "inactive_players");
        Assert.Equal(14, inactive.Length);
        Assert.All(inactive, p => Assert.StartsWith("level163:", p.Name));
        Assert.Equal(50, ContractAudio().GetProperty("players").EnumerateObject().Sum(p => p.Value.GetInt32()));
        // 50 players - 14 inactive = the 36 group members (31 + 5): the member lists hold exactly the active players.
        Assert.Equal(36, 50 - inactive.Length);
        Assert.Equal(36, states.GetProperty("groups").EnumerateObject().Sum(g => g.Value.GetProperty("players").GetInt32()));
        // A same-named sibling got the importer's rename ('~2'): the first pigeon_flying keeps its name, the inactive one is the second.
        Assert.Single(inactive, p => p.Value == "pigeon_flying~2");
        Assert.Equal(inactive.Select(p => p.Value).Order().ToArray(), Profile().InactivePlayers.Order().ToArray());
    }

    [Fact]
    public void TheTwoHumLoopsAreTheOnlyPlayersWithMinDistanceZero()
    {
        // The exporter's reviewed rule (parse_player) is global, its use is pinned per contract (validate_graph): Iceberg 21, Autoservice 9, Yard 2, the others none.
        var states = ContractAudio().GetProperty("states");
        var zero = Pins(states, "zero_min_distance_players");
        Assert.Equal(new[] { ("level163:672", "fridge"), ("level163:734", "electricity") }, zero);
        // Both are LoopAmbientSoundPlayers (the kind count below), whose AudioSource Loop is set in Awake and not by the source: loops stays empty.
        Assert.Equal(2, ContractAudio().GetProperty("players").GetProperty("LoopAmbientSoundPlayer").GetInt32());
        Assert.Empty(states.GetProperty("loops").EnumerateObject());
    }

    [Fact]
    public void TheOneSilentSourceClipIsPinnedByIdAndName()
    {
        // amb_yard_cat_06 is a 3,642 Hz, 3,200-byte stub in Arena's own sharedassets163.resource that decodes to zeros: the exporter refuses a silent clip
        // unless this pin names it (extract_bay5_audio.validate_wav), and validate_native_features requires a pinned clip to be silent.
        var states = ContractAudio().GetProperty("states");
        Assert.Equal(new[] { ("sharedassets163.assets:109", "amb_yard_cat_06") }, Pins(states, "silent_clips"));
    }

    [Fact]
    public void TheProfileMirrorsTheContract()
    {
        var audio = ContractAudio();
        var states = audio.GetProperty("states");
        var profile = Profile();
        Assert.Equal(audio.GetProperty("root").GetString(), profile.RootName);
        Assert.Equal(audio.GetProperty("players").EnumerateObject().Sum(p => p.Value.GetInt32()), profile.Players);
        Assert.Equal((50, 36, 2, 0), (profile.Players, profile.ActivePlayers, profile.Groups, profile.Controllers));
        Assert.Equal(states.GetProperty("grouped_players").GetInt32(), profile.GroupedPlayers);
        Assert.False(profile.GroupsListInactivePlayers);
        Assert.Equal(new[] { ("CommonAmbientOutGroup", "CommonAmbOutEffects", 31), ("CommonAmbientInGroup", "CommonAmbInEffects", 5) },
            profile.Routes.Select(r => (r.GroupName, r.MixerName, r.Members)).ToArray());
        Assert.Equal(states.GetProperty("group_mixers").EnumerateArray().Select(e => e.GetString()).ToArray(),
            profile.Routes.Select(r => r.MixerName).ToArray());
        Assert.Empty(profile.LoopPlayers);
        Assert.True(profile.UsesSeasons);
        Assert.True(profile.OriginalActivation);
        Assert.False(profile.HasShoreline);
        // The strict driver check demands the exporter-kind counts of the contract.
        Assert.Equal((22, 2), (profile.SeasonKindPlayers, profile.LoopKindPlayers));
        Assert.Equal(audio.GetProperty("players").GetProperty("SeasonAmbientSoundPlayer").GetInt32(), profile.SeasonKindPlayers);
        Assert.Equal(audio.GetProperty("players").GetProperty("LoopAmbientSoundPlayer").GetInt32(), profile.LoopKindPlayers);
        Assert.Equal(new[] { false, true }, profile.Routes.Select(r => r.ReverbReroute).ToArray());
        Assert.Equal(new[] { AmbientBus.Out, AmbientBus.In }, profile.Buses.ToArray());
        Assert.Equal("CommonAmbientInGroup", profile.EffectsRoute.GroupName);
        Assert.Equal("Yard", profile.DisplayName);
    }

    [Fact]
    public void TheProfileIsInTheReleasedTableAndNoLongerStaged()
    {
        // Yard joined ArenaMapRegistry.All with item 15: the released table, TryGet and Get see its profile and the staged list is empty.
        Assert.True(AmbientAudioProfiles.TryGet(Key, out var found));
        Assert.Same(found, AmbientAudioProfiles.Get(Key));
        Assert.Contains(AmbientAudioProfiles.All, p => p.MapKey == Key);
        Assert.Equal(AmbientAudioProfiles.All[^1], AmbientAudioProfiles.Get(Key));          // the last row, after Autoservice's
        Assert.DoesNotContain(AmbientAudioProfiles.Staged, p => p.MapKey == Key);
        Assert.Contains(ArenaMapPort.Shared.ArenaMapRegistry.All, m => m.Key == Key);
        Assert.Contains(ArenaMapPort.Shared.ArenaMapRegistry.Catalog, m => m.Key == Key);
    }

    [Fact]
    public void TheImporterKnowsTheRootAndTheAssetFolder()
    {
        var source = Text("UnityProject/Assets/ArenaMapPort/Editor/Import/Bay5AudioImporter.cs");
        Assert.Contains("const string YardRootName = \"ArenaMapPort_YardNativeAudio\";", source);
        Assert.Contains("case \"arena_yard\": return YardRootName;", source);
        Assert.Contains("case \"arena_yard\": return \"Yard\";", source);
        Assert.Equal("ArenaMapPort_YardNativeAudio", Profile().RootName);
    }

    [Fact]
    public void TheAuthorsMixKeepsTheHumsBelowBowlsLiftAndBelowTheMedian()
    {
        // 2026-11-21 (PLAN-YARD item 24): the author's values by ear (gain 5, offset 0.1, range 3) replace the computed start mix 3 / 0.1 / 2.
        // volume * gain + offset: Bowl's offset 0.4 would put the authored 0.05 and 0.03 hums at about 0.49 and 0.45; this row keeps them near 0.19 and 0.15.
        AmbientAudioDefaults.Values(Key, out float gainDb, out float offset, out float range);
        Assert.Equal((5f, .1f, 3f), (gainDb, offset, range));
        double gain = Math.Pow(10, gainDb / 20.0);
        Assert.InRange(.05 * gain + offset, .18, .20);
        Assert.InRange(.03 * gain + offset, .14, .16);
        Assert.InRange(.05 * gain + .4, .48, .50);
        // The exterior median (0.10) lands near 0.28, so the hums stay below it; their reach is 7 and 10 m times the range multiplier.
        Assert.InRange(.10 * gain + offset, .27, .29);
        Assert.Equal((21d, 30d), (7 * range, 10 * range));
    }
}
