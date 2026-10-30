#nullable disable
using System.Text.Json;
using System.Text.RegularExpressions;
using ArenaMapPort.Client;
using Xunit;

namespace ArenaMapPort.Server.Tests;

/// <summary>
/// PLAN-ICEBERG item 5 (ambient audio, package v2) and item 12 (its promotion into <c>ArenaMapRegistry.All</c>). The client profile row lives in
/// <see cref="AmbientAudioProfiles"/> now and the generic table tests (<c>AmbientAudioProfilesTests</c>) cover it like any released map; what is
/// specific to Iceberg stays pinned here, against the contract the validators check on the package and the bundle: the contract's audio block, the 21
/// players that author minDistance 0 (pinned by id since the review of 2026-10-30), the kind counts the strict activation check of the driver demands,
/// the importer's mirror (root name, asset folder, the lamps group) and the fact that the released maps keep their old fail-closed driver check.
/// </summary>
public sealed class IcebergAmbientAudioTests
{
    const string Key = "arena_iceberg";

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
        using var document = JsonDocument.Parse(Text("tools/extract/native_contracts/iceberg.json"));
        return document.RootElement.GetProperty("audio").Clone();
    }

    static (string Name, string Mixer, int Players)[] ContractGroups(JsonElement audio) =>
        audio.GetProperty("states").GetProperty("groups").EnumerateObject()
            .Select(g => (g.Name, g.Value.GetProperty("mixer").GetString(), g.Value.GetProperty("players").GetInt32())).ToArray();

    static AmbientAudioProfile Profile() => AmbientAudioProfiles.Get(Key);

    [Fact]
    public void TheContractPinsTheSoundSceneOfIceberg()
    {
        var audio = ContractAudio();
        Assert.Equal("ambient_audio", audio.GetProperty("feature").GetString());
        Assert.Equal(2, audio.GetProperty("schema").GetInt32());
        Assert.Equal("level259", audio.GetProperty("scene").GetString());
        Assert.Equal("ArenaMapPort_IcebergNativeAudio", audio.GetProperty("root").GetString());
        Assert.Equal("all_players", audio.GetProperty("group_coverage").GetString());
        Assert.Equal((28, 237), (audio.GetProperty("banks").GetInt32(), audio.GetProperty("clips").GetInt32()));
        var players = audio.GetProperty("players");
        Assert.Equal((52, 22, 30), (players.GetProperty("AmbientSoundPlayer").GetInt32(),
            players.GetProperty("SeasonAmbientSoundPlayer").GetInt32(), players.GetProperty("LoopAmbientSoundPlayer").GetInt32()));
        var support = audio.GetProperty("support");
        Assert.Equal(new[] { ("AmbientSoundPlayerGroup", 4), ("AmbientSplineEmitterController", 1), ("AmbientPlayerSplineMappedEmitter", 1),
                ("BezierSpline", 1), ("SoundAmbientZoneCalculator", 1) }.Order().ToArray(),
            support.EnumerateObject().Select(p => (p.Name, p.Value.GetInt32())).Order().ToArray());
        var states = audio.GetProperty("states");
        Assert.Equal(104, states.GetProperty("grouped_players").GetInt32());
        // The source order of the group records; the client profile lists its routes in this order.
        Assert.Equal(new[]
            {
                ("CommonAmbientInGroup", "CommonAmbInEffects", 46), ("LampsAmbientInGroup", "CommonAmbInEffects", 20),
                ("CommonAmbientOutGroup", "CommonAmbOutEffects", 37), ("AmbientSplineEmitterWaterGroup", "CommonAmbOutBypass", 1),
            }, ContractGroups(audio));
        Assert.Equal(new[] { "CommonAmbInEffects", "CommonAmbInEffects", "CommonAmbOutEffects", "CommonAmbOutBypass" },
            states.GetProperty("group_mixers").EnumerateArray().Select(e => e.GetString()).ToArray());
        Assert.Equal(new[] { "river", "river (2)", "sparks (2)" },
            states.GetProperty("inactive_players").EnumerateObject().Select(p => p.Value.GetString()).Order().ToArray());
        Assert.Equal(0, states.GetProperty("disabled_players").GetArrayLength());
        // No AudioSource of the source scene has Loop set: the 30 LoopAmbientSoundPlayers switch it on in Awake themselves.
        Assert.Empty(states.GetProperty("loops").EnumerateObject());
        Assert.Equal(0, states.GetProperty("loop_players").GetArrayLength());
        Assert.False(states.TryGetProperty("forced_mixer_players", out var forced) && forced.EnumerateObject().Any());
        // The 21 players with minDistance 0 (20 neon lamps and the projector) are pinned by id and leaf name: the exporter's reviewed rule is global,
        // its use is not (extract_bay5_audio.validate_graph compares the players with the pin, the four released contracts pin none).
        var zero = states.GetProperty("zero_min_distance_players").EnumerateObject().ToArray();
        Assert.Equal(21, zero.Length);
        Assert.All(zero, p => Assert.StartsWith("level259:", p.Name));
        Assert.Equal(20, zero.Count(p => Regex.IsMatch(p.Value.GetString()!, @"^neon_lamp( \(\d+\))?$")));
        Assert.Contains(zero, p => p.Value.GetString() == "projector");
        Assert.True(zero.Length <= audio.GetProperty("players").GetProperty("LoopAmbientSoundPlayer").GetInt32());   // they are loop-kind players (30 in all)
    }

    [Fact]
    public void TheClientProfileMirrorsTheContract()
    {
        var audio = ContractAudio();
        var states = audio.GetProperty("states");
        var profile = Profile();
        var players = audio.GetProperty("players").EnumerateObject().Sum(p => p.Value.GetInt32());
        Assert.Equal(audio.GetProperty("root").GetString(), profile.RootName);
        Assert.Equal(players, profile.Players);
        Assert.Equal(104, profile.GroupedPlayers);
        Assert.Equal(states.GetProperty("grouped_players").GetInt32(), profile.GroupedPlayers);
        Assert.True(profile.GroupsListInactivePlayers);
        Assert.Equal(ContractGroups(audio), profile.Routes.Select(r => (r.GroupName, r.MixerName, r.Members)).ToArray());
        Assert.Equal(states.GetProperty("group_mixers").EnumerateArray().Select(e => e.GetString()).ToArray(),
            profile.Routes.Select(r => r.MixerName).ToArray());
        Assert.Equal(states.GetProperty("inactive_players").EnumerateObject().Select(p => p.Value.GetString()).Order().ToArray(),
            profile.InactivePlayers.Order().ToArray());
        Assert.Equal(profile.LoopPlayers.Count, states.GetProperty("loop_players").GetArrayLength());
        Assert.Equal(audio.GetProperty("support").GetProperty("AmbientSplineEmitterController").GetInt32(), profile.Controllers);
        Assert.True(profile.HasShoreline);
        Assert.True(profile.UsesSeasons);
        Assert.True(profile.OriginalActivation);
        // Two interior groups share the interior mixer and one lease; the exterior bypass group joins the AmbientOut family.
        Assert.Equal(new[] { AmbientBus.In, AmbientBus.Out }, profile.Buses.ToArray());
        Assert.Equal("CommonAmbientInGroup", profile.EffectsRoute.GroupName);
        Assert.Equal(new[] { true, false, false, false }, profile.Routes.Select(r => r.ReverbReroute).ToArray());
        Assert.All(profile.Routes, r => Assert.Equal(r.MixerName == "CommonAmbInEffects" ? AmbientBus.In : AmbientBus.Out, r.Bus));
        for (int i = 0; i < profile.Routes.Count; i++) Assert.Equal(i, profile.IndexOfGroup(profile.Routes[i].GroupName));
    }

    [Fact]
    public void TheImporterKnowsTheRootTheAssetFolderAndTheLampsGroup()
    {
        var source = Text("UnityProject/Assets/ArenaMapPort/Editor/Import/Bay5AudioImporter.cs");
        var declaration = Regex.Match(source, @"const string (?<n>\w+) = ""ArenaMapPort_IcebergNativeAudio"";");
        Assert.True(declaration.Success, "importer declares no const for Iceberg's root");
        Assert.Matches($@"case ""{Key}"": return {declaration.Groups["n"].Value};", source);
        Assert.Matches($@"case ""{Key}"": return ""Iceberg"";", source);
        var block = Regex.Match(source, @"GroupMixers\s*=\s*new Dictionary<string, string>\s*\{(?<body>.*?)\};", RegexOptions.Singleline);
        Assert.True(block.Success, "GroupMixers table not found in the importer");
        var importer = Regex.Matches(block.Groups["body"].Value, @"\[""(?<group>\w+)""\]\s*=\s*""(?<mixer>\w+)""")
            .Select(m => (m.Groups["group"].Value, m.Groups["mixer"].Value)).ToArray();
        // Every group of Iceberg's contract has the importer pairing the contract names (the importer refuses a mismatch).
        foreach (var (name, mixer, _) in ContractGroups(ContractAudio()))
            Assert.Contains((name, mixer), importer);
        Assert.Contains(("LampsAmbientInGroup", "CommonAmbInEffects"), importer);
    }

    [Fact]
    public void TheProfileDemandsTheKindCountsAndTheReleasedMapsKeepTheirFailClosedCheck()
    {
        // Iceberg is the first graph that combines the original-activation contract (three inactive players, the groups define what starts) with
        // season and loop players. The driver's strict check used to demand AmbientSoundPlayer for every player; it now accepts the three kinds but
        // demands the profile's counts, which are zero for every released map (review of 2026-10-30): a season or loop-kind player appearing on
        // Equator, AirPit or Bowl still fails closed.
        var audio = ContractAudio();
        var profile = Profile();
        Assert.Equal((22, 30), (profile.SeasonKindPlayers, profile.LoopKindPlayers));
        Assert.Equal(audio.GetProperty("players").GetProperty("SeasonAmbientSoundPlayer").GetInt32(), profile.SeasonKindPlayers);
        Assert.Equal(audio.GetProperty("players").GetProperty("LoopAmbientSoundPlayer").GetInt32(), profile.LoopKindPlayers);
        foreach (var released in AmbientAudioProfiles.All.Where(p => p.OriginalActivation && p.MapKey != Key))
            Assert.Equal((0, 0), (released.SeasonKindPlayers, released.LoopKindPlayers));
        var driver = Text("ArenaMapPort.Client/Bay5AmbientAudio.cs");
        var code = string.Join(Environment.NewLine, driver.Split((char)10).Where(l => !l.TrimStart().StartsWith("//")));
        Assert.Contains("p is AmbientSoundPlayer || p is SeasonAmbientSoundPlayer || p is LoopAmbientSoundPlayer", code);
        Assert.Contains("_players.Count(p => p is SeasonAmbientSoundPlayer) != profile.SeasonKindPlayers", code);
        Assert.Contains("_players.Count(p => p is LoopAmbientSoundPlayer) != profile.LoopKindPlayers", code);
        Assert.DoesNotContain("!(p is AmbientSoundPlayer) ||", code);
    }

    [Fact]
    public void TheKindCountsNeedAStrictProfileThatUsesSeasons()
    {
        var route = new AmbientGroupRoute("CommonAmbientInGroup", "AmbientIn/CommonAmbInEffects", "CommonAmbInEffects", AmbientBus.In, members: 4, effects: true);
        Assert.NotNull(new AmbientAudioProfile("arena_x", "X", "Root", 4, 4, 0, false, true, true, null, null, new[] { route }, seasonKindPlayers: 1, loopKindPlayers: 1));
        // season players need usesSeasons, the counts need original activation and cannot exceed the players
        Assert.Throws<ArgumentException>(() => new AmbientAudioProfile("arena_x", "X", "Root", 4, 4, 0, false, false, true, null, null, new[] { route }, seasonKindPlayers: 1));
        Assert.Throws<ArgumentException>(() => new AmbientAudioProfile("arena_x", "X", "Root", 4, 4, 0, false, true, false, null, null, new[] { route }, seasonKindPlayers: 1));
        Assert.Throws<ArgumentException>(() => new AmbientAudioProfile("arena_x", "X", "Root", 4, 4, 0, false, false, false, null, null, new[] { route }, loopKindPlayers: 1));
        Assert.Throws<ArgumentException>(() => new AmbientAudioProfile("arena_x", "X", "Root", 4, 4, 0, false, true, true, null, null, new[] { route }, seasonKindPlayers: 3, loopKindPlayers: 2));
        Assert.Throws<ArgumentException>(() => new AmbientAudioProfile("arena_x", "X", "Root", 4, 4, 0, false, true, true, null, null, new[] { route }, loopKindPlayers: -1));
    }
}
