#nullable disable
using System.Text.Json;
using System.Text.RegularExpressions;
using ArenaMapPort.Client;
using Xunit;

namespace ArenaMapPort.Server.Tests;

/// <summary>
/// PLAN-ICEBERG item 5 (ambient audio, package v2). Iceberg is not in <c>ArenaMapRegistry.All</c> yet, so
/// <see cref="AmbientAudioProfiles"/> has no row for it and <c>AmbientAudioProfilesTests</c> does not cover it. Everything the
/// promotion needs is pinned here instead, against the contract the validators check on the package and the bundle:
/// the contract's audio block, the client profile row the promotion adds (built from the contract values and checked by the
/// profile's own constructor), the importer's mirror (root name, asset folder, group-to-mixer pairing) and the driver rule
/// that lets an original-activation graph hold season and loop players.
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

    // The row PLAN-ICEBERG item 12 adds to AmbientAudioProfiles.Table, written from the contract values so the profile's own
    // constructor checks the census (members add up to all 104 players, three inactive, exactly one effects route).
    static AmbientAudioProfile PlannedProfile() => new AmbientAudioProfile("arena_iceberg", "Iceberg", "ArenaMapPort_IcebergNativeAudio",
        players: 104, activePlayers: 101, controllers: 1, hasShoreline: true, usesSeasons: true, originalActivation: true,
        inactivePlayers: new[] { "river (2)", "river", "sparks (2)" }, loopPlayers: null,
        routes: new[]
        {
            new AmbientGroupRoute("CommonAmbientInGroup", "AmbientIn/CommonAmbInEffects", "CommonAmbInEffects",
                AmbientBus.In, members: 46, effects: true, reverbReroute: true),
            new AmbientGroupRoute("LampsAmbientInGroup", "AmbientIn/CommonAmbInEffects", "CommonAmbInEffects",
                AmbientBus.In, members: 20),
            new AmbientGroupRoute("CommonAmbientOutGroup", "AmbientOut/CommonAmbOutEffects", "CommonAmbOutEffects",
                AmbientBus.Out, members: 37),
            new AmbientGroupRoute("AmbientSplineEmitterWaterGroup", "CommonAmbOutBypass", "CommonAmbOutBypass",
                AmbientBus.Out, members: 1),
        },
        groupsListInactivePlayers: true);

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
    }

    [Fact]
    public void ThePlannedClientProfileMirrorsTheContract()
    {
        var audio = ContractAudio();
        var states = audio.GetProperty("states");
        var profile = PlannedProfile();
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
    public void TheDriverAcceptsSeasonAndLoopPlayersUnderOriginalActivation()
    {
        // Iceberg is the first graph that combines the original-activation contract (three inactive players, the groups
        // define what starts) with season and loop players; the check used to demand AmbientSoundPlayer for every player.
        var driver = Text("ArenaMapPort.Client/Bay5AmbientAudio.cs");
        var code = string.Join("\n", driver.Split('\n').Where(l => !l.TrimStart().StartsWith("//")));
        Assert.Contains("p is AmbientSoundPlayer || p is SeasonAmbientSoundPlayer || p is LoopAmbientSoundPlayer", code);
        Assert.DoesNotContain("!(p is AmbientSoundPlayer) ||", code);
    }

    [Fact]
    public void TheExporterAcceptsAZeroMinDistanceAndKeepsTheOtherBounds()
    {
        // The reviewed rule of PLAN-ICEBERG item 5 lives in extract_bay5_audio.parse_player (unit-tested in tools/tests);
        // this pins its text so a revert shows up in the .NET run too.
        var exporter = Text("tools/extract/extract_bay5_audio.py");
        Assert.Contains("0<=d['minDistance']<=d['maxDistance'] or not d['maxDistance']>0", exporter);
        Assert.DoesNotContain("0<d['minDistance']<=d['maxDistance']", exporter);
        Assert.Contains("0<=d['volume']<=1", exporter);
        Assert.Contains("0<=d['spatialBlend']<=1", exporter);
    }
}
