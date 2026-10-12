#nullable disable
using System.Text.Json;
using System.Text.RegularExpressions;
using ArenaMapPort.Client;
using ArenaMapPort.Shared;
using Xunit;

namespace ArenaMapPort.Server.Tests;

/// <summary>
/// PLAN-AIRPIT item 13 (phase 2). The imported-ambience driver (Bay5AmbientAudio) and the Editor importer
/// (Bay5AudioImporter) sit on Unity types and cannot be compiled into this project, so everything per-map that they
/// decide comes from the pure <see cref="AmbientAudioProfiles"/> table and is pinned here: against the extraction
/// contracts that the validators check on the package and the bundle, and against the importer's own mirror of the
/// root names and the group-to-mixer pairing.
/// </summary>
public sealed class AmbientAudioProfilesTests
{
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

    static JsonElement ContractAudio(AmbientAudioProfile profile)
    {
        string stem = profile.MapKey.Substring("arena_".Length);
        using var document = JsonDocument.Parse(Text($"tools/extract/native_contracts/{stem}.json"));
        return document.RootElement.GetProperty("audio").Clone();
    }

    static int Count(JsonElement census, string kind) =>
        census.TryGetProperty(kind, out var value) ? value.GetInt32() : 0;

    static int Total(JsonElement census) => census.EnumerateObject().Sum(p => p.Value.GetInt32());

    public static IEnumerable<object[]> Keys => AmbientAudioProfiles.All.Select(p => new object[] { p.MapKey });

    [Fact]
    public void TheTableNamesExactlyTheReleasedMaps()
    {
        Assert.Equal(ArenaMapRegistry.All.Select(m => m.Key).Order().ToArray(),
            AmbientAudioProfiles.All.Select(p => p.MapKey).Order().ToArray());
        Assert.Equal(AmbientAudioProfiles.All.Count, AmbientAudioProfiles.All.Select(p => p.MapKey).Distinct().Count());
        Assert.Equal(new[] { "arena_equator", "arena_bay5", "arena_airpit" }, AmbientAudioProfiles.All.Select(p => p.MapKey).ToArray());
    }

    [Fact]
    public void UnlistedMapsHaveNoProfileAndGetThrows()
    {
        foreach (var map in ArenaMapRegistry.Catalog.Where(m => ArenaMapRegistry.All.All(r => r.Key != m.Key)))
        {
            Assert.False(AmbientAudioProfiles.TryGet(map.Key, out var profile));
            Assert.Null(profile);
            Assert.False(AmbientAudioProfiles.LeasesBus(map.Key, AmbientBus.In));
            Assert.False(AmbientAudioProfiles.LeasesBus(map.Key, AmbientBus.Out));
            Assert.Throws<ArgumentException>(() => AmbientAudioProfiles.Get(map.Key));
        }
        foreach (var key in new string[] { null, "", "arena_", "ARENA_EQUATOR", "arena_equator " })
        {
            Assert.False(AmbientAudioProfiles.TryGet(key, out _));
            Assert.Throws<ArgumentException>(() => AmbientAudioProfiles.Get(key));
        }
    }

    [Theory]
    [MemberData(nameof(Keys))]
    public void ProfilesMirrorTheExtractionContract(string key)
    {
        var profile = AmbientAudioProfiles.Get(key);
        var audio = ContractAudio(profile);
        var players = audio.GetProperty("players");
        var support = audio.GetProperty("support");
        Assert.Equal(profile.RootName, audio.GetProperty("root").GetString());
        Assert.Equal(profile.Players, Total(players));
        Assert.Equal(profile.Groups, Count(support, "AmbientSoundPlayerGroup"));
        Assert.Equal(profile.Groups, profile.Routes.Count);
        Assert.Equal(profile.Controllers, Count(support, "AmbientSplineEmitterController"));
        Assert.Equal(profile.HasShoreline, Count(support, "AmbientPlayerSplineMappedEmitter") > 0 &&
            Count(support, "SoundAmbientZoneCalculator") > 0);
        Assert.Equal(profile.UsesSeasons, Count(players, "SeasonAmbientSoundPlayer") > 0);

        // Schema 2 maps carry the source activation states in the contract; schema 1 (Bay5) has none and starts everyone.
        var states = audio.GetProperty("states");
        Assert.Equal(profile.OriginalActivation, states.ValueKind == JsonValueKind.Object);
        Assert.Equal(profile.OriginalActivation, audio.GetProperty("schema").GetInt32() == 2);
        if (!profile.OriginalActivation)
        {
            Assert.Equal(profile.Players, profile.ActivePlayers);
            Assert.Equal("all_players", audio.GetProperty("group_coverage").GetString());
            return;
        }
        Assert.Equal(profile.ActivePlayers, states.GetProperty("grouped_players").GetInt32());
        Assert.Equal(profile.Routes.Select(r => r.MixerName).ToArray(),
            states.GetProperty("group_mixers").EnumerateArray().Select(e => e.GetString()).ToArray());
        // Groups are pinned by the leaf name of the GameObject carrying them, the key the driver and the importer route by.
        Assert.Equal(profile.Routes.Select(r => (r.GroupName, r.MixerName, r.Members)).Order().ToArray(),
            states.GetProperty("groups").EnumerateObject()
                .Select(g => (g.Name, g.Value.GetProperty("mixer").GetString(), g.Value.GetProperty("players").GetInt32()))
                .Order().ToArray());
        Assert.Equal(profile.LoopPlayers.Order().ToArray(),
            states.GetProperty("loops").EnumerateObject().Select(l => l.Value.GetString()).Order().ToArray());
        Assert.Equal(profile.InactivePlayers.Order().ToArray(),
            states.GetProperty("inactive_players").EnumerateObject().Select(p => p.Value.GetString()).Order().ToArray());
        Assert.Equal(profile.LoopPlayers.Count, states.GetProperty("loop_players").GetArrayLength());
        Assert.Equal(0, states.GetProperty("disabled_players").GetArrayLength());
        Assert.Equal(profile.InactivePlayers.Count == 0 ? "all_players" : "active_players",
            audio.GetProperty("group_coverage").GetString());
    }

    [Theory]
    [MemberData(nameof(Keys))]
    public void EveryProfileIsInternallyConsistent(string key)
    {
        var profile = AmbientAudioProfiles.Get(key);
        Assert.Equal(key, profile.MapKey);
        Assert.True(AmbientAudioProfiles.TryGet(key, out var again));
        Assert.Same(profile, again);
        Assert.StartsWith("ArenaMapPort_", profile.RootName);
        Assert.EndsWith("NativeAudio", profile.RootName);
        Assert.Equal(profile.Players - profile.ActivePlayers, profile.InactivePlayers.Count);
        Assert.Equal(profile.ActivePlayers, profile.Routes.Sum(r => r.Members));
        Assert.Single(profile.Routes, r => r.Effects);
        Assert.Same(profile.EffectsRoute, profile.Routes.Single(r => r.Effects));
        Assert.All(profile.Routes.Where(r => r.ReverbReroute), r => Assert.True(r.Effects));
        Assert.Equal(profile.Routes.Count, profile.Routes.Select(r => r.GroupName).Distinct().Count());
        for (int i = 0; i < profile.Routes.Count; i++)
        {
            var route = profile.Routes[i];
            Assert.Equal(i, profile.IndexOfGroup(route.GroupName));
            Assert.EndsWith(route.MixerName, route.MixerPath);
            // The interior effects mixer sits behind AmbientIn; every other native ambient mixer is an AmbientOut family bus.
            Assert.Equal(route.MixerName == "CommonAmbInEffects" ? AmbientBus.In : AmbientBus.Out, route.Bus);
        }
        Assert.Equal(-1, profile.IndexOfGroup("NoSuchGroup"));
        Assert.Equal(-1, profile.IndexOfGroup(null));
        Assert.Equal(profile.Routes.Select(r => r.Bus).Distinct().ToArray(), profile.Buses.ToArray());
        Assert.All(profile.Buses, bus => Assert.True(AmbientAudioProfiles.LeasesBus(key, bus)));
        // Everything inactive or looping needs the original-activation contract that pins it.
        if (!profile.OriginalActivation) { Assert.Empty(profile.InactivePlayers); Assert.Empty(profile.LoopPlayers); }
        // Each driver-level config entry the profile's map binds needs its accepted defaults row.
        AmbientAudioDefaults.Values(key, out _, out _, out _);
    }

    [Fact]
    public void AirPitSendsItsTwoGroupsToTheirOwnBusesAndReroutesOnlyTheInteriorOne()
    {
        var airpit = AmbientAudioProfiles.Get(ArenaMapRegistry.AirPit.Key);
        Assert.Equal("AirPit", airpit.DisplayName);
        Assert.Equal("ArenaMapPort_AirPitNativeAudio", airpit.RootName);
        Assert.Equal((26, 26, 2, 0), (airpit.Players, airpit.ActivePlayers, airpit.Groups, airpit.Controllers));
        Assert.False(airpit.HasShoreline);
        Assert.False(airpit.UsesSeasons);
        Assert.True(airpit.OriginalActivation);
        Assert.Empty(airpit.InactivePlayers);
        Assert.Empty(airpit.LoopPlayers);

        Assert.Equal(new[] { "CommonAmbientOutGroup", "CommonAmbientInGroup" }, airpit.Routes.Select(r => r.GroupName).ToArray());
        Assert.Equal(new[] { "AmbientOut/CommonAmbOutEffects", "AmbientIn/CommonAmbInEffects" }, airpit.Routes.Select(r => r.MixerPath).ToArray());
        Assert.Equal(new[] { "CommonAmbOutEffects", "CommonAmbInEffects" }, airpit.Routes.Select(r => r.MixerName).ToArray());
        Assert.Equal(new[] { AmbientBus.Out, AmbientBus.In }, airpit.Routes.Select(r => r.Bus).ToArray());
        Assert.Equal(new[] { AmbientBus.Out, AmbientBus.In }, airpit.Buses.ToArray());
        Assert.Equal("CommonAmbientInGroup", airpit.EffectsRoute.GroupName);
        Assert.Equal(new[] { 4, 22 }, airpit.Routes.Select(r => r.Members).ToArray());
        Assert.Equal(new[] { false, true }, airpit.Routes.Select(r => r.Effects).ToArray());
        Assert.Equal(new[] { false, true }, airpit.Routes.Select(r => r.ReverbReroute).ToArray());
        Assert.True(AmbientAudioProfiles.LeasesBus(airpit.MapKey, AmbientBus.Out));
        Assert.True(AmbientAudioProfiles.LeasesBus(airpit.MapKey, AmbientBus.In));
    }

    [Fact]
    public void EquatorAndBay5KeepTheirAcceptedRouting()
    {
        var equator = AmbientAudioProfiles.Get(ArenaMapRegistry.Equator.Key);
        Assert.Equal((25, 23, 1, 0), (equator.Players, equator.ActivePlayers, equator.Groups, equator.Controllers));
        Assert.Equal("ArenaMapPort_EquatorNativeAudio", equator.RootName);
        Assert.True(equator.OriginalActivation);
        Assert.False(equator.HasShoreline);
        Assert.False(equator.UsesSeasons);
        Assert.Equal(new[] { "far_dogs1", "far_dogs2" }, equator.InactivePlayers.Order().ToArray());
        Assert.Equal(new[] { "electricity" }, equator.LoopPlayers.ToArray());
        var interior = Assert.Single(equator.Routes);
        Assert.Equal(("CommonAmbientInGroup", "AmbientIn/CommonAmbInEffects", "CommonAmbInEffects", AmbientBus.In, 23, true, true),
            (interior.GroupName, interior.MixerPath, interior.MixerName, interior.Bus, interior.Members, interior.Effects, interior.ReverbReroute));
        Assert.Equal(new[] { AmbientBus.In }, equator.Buses.ToArray());
        Assert.True(AmbientAudioProfiles.LeasesBus(equator.MapKey, AmbientBus.In));
        Assert.False(AmbientAudioProfiles.LeasesBus(equator.MapKey, AmbientBus.Out));

        var bay5 = AmbientAudioProfiles.Get(ArenaMapRegistry.Bay5.Key);
        Assert.Equal((24, 24, 2, 1), (bay5.Players, bay5.ActivePlayers, bay5.Groups, bay5.Controllers));
        Assert.Equal("ArenaMapPort_Bay5NativeAudio", bay5.RootName);
        Assert.False(bay5.OriginalActivation);
        Assert.True(bay5.HasShoreline);
        Assert.True(bay5.UsesSeasons);
        Assert.Equal(new[] { "AmbientPlayersGroup", "AmbientSplineEmitterWaterGroup" }, bay5.Routes.Select(r => r.GroupName).ToArray());
        Assert.Equal(new[] { "AmbientOut/CommonAmbOutEffects", "CommonAmbOutBypass" }, bay5.Routes.Select(r => r.MixerPath).ToArray());
        Assert.Equal(new[] { "CommonAmbOutEffects", "CommonAmbOutBypass" }, bay5.Routes.Select(r => r.MixerName).ToArray());
        Assert.Equal(new[] { 23, 1 }, bay5.Routes.Select(r => r.Members).ToArray());
        Assert.Equal(new[] { true, false }, bay5.Routes.Select(r => r.Effects).ToArray());
        Assert.All(bay5.Routes, r => { Assert.Equal(AmbientBus.Out, r.Bus); Assert.False(r.ReverbReroute); });
        Assert.Equal(new[] { AmbientBus.Out }, bay5.Buses.ToArray());
        // Bay5's room tones take their own interior lease (NativeSpatialAudio), because its ambient driver holds only the exterior bus.
        Assert.False(AmbientAudioProfiles.LeasesBus(bay5.MapKey, AmbientBus.In));
    }

    [Fact]
    public void BusParametersAreTheOnesTheLeaseAccepts()
    {
        Assert.Equal("AmbientInVolume", AmbientAudioProfiles.Parameter(AmbientBus.In));
        Assert.Equal("AmbientOutVolume", AmbientAudioProfiles.Parameter(AmbientBus.Out));
        // AmbientBusLease.Acquire throws ArgumentException for any other parameter name, so the profile can only name these two.
        var source = Text("ArenaMapPort.Client/AmbientBusLease.cs");
        Assert.Contains("parameter != \"" + AmbientAudioProfiles.InVolumeParameter + "\"", source);
        Assert.Contains("parameter != \"" + AmbientAudioProfiles.OutVolumeParameter + "\"", source);
    }

    [Fact]
    public void InconsistentProfilesAreRejected()
    {
        var interior = new AmbientGroupRoute("G", "AmbientIn/CommonAmbInEffects", "CommonAmbInEffects", AmbientBus.In, members: 2, effects: true);
        var other = new AmbientGroupRoute("H", "CommonAmbOutBypass", "CommonAmbOutBypass", AmbientBus.Out, members: 1);
        Assert.NotNull(new AmbientAudioProfile("arena_x", "X", "Root", 3, 3, 0, false, false, false, null, null, new[] { interior, other }));
        // the groups' members must add up to the active players
        Assert.Throws<ArgumentException>(() => new AmbientAudioProfile("arena_x", "X", "Root", 4, 4, 0, false, false, false, null, null, new[] { interior, other }));
        // no effects route, two effects routes, duplicate group names, no routes
        Assert.Throws<ArgumentException>(() => new AmbientAudioProfile("arena_x", "X", "Root", 3, 3, 0, false, false, false, null, null, new[] { other }));
        var second = new AmbientGroupRoute("H", "AmbientOut/CommonAmbOutEffects", "CommonAmbOutEffects", AmbientBus.Out, members: 1, effects: true);
        Assert.Throws<ArgumentException>(() => new AmbientAudioProfile("arena_x", "X", "Root", 3, 3, 0, false, false, false, null, null, new[] { interior, second }));
        Assert.Throws<ArgumentException>(() => new AmbientAudioProfile("arena_x", "X", "Root", 4, 4, 0, false, false, false, null, null, new[] { interior, other, other }));
        Assert.Throws<ArgumentException>(() => new AmbientAudioProfile("arena_x", "X", "Root", 3, 3, 0, false, false, false, null, null, Array.Empty<AmbientGroupRoute>()));
        // inactive players that do not add up, or inactive/looping players without the original-activation contract
        Assert.Throws<ArgumentException>(() => new AmbientAudioProfile("arena_x", "X", "Root", 3, 2, 0, false, false, true, null, null, new[] { interior }));
        Assert.Throws<ArgumentException>(() => new AmbientAudioProfile("arena_x", "X", "Root", 3, 2, 0, false, false, false, new[] { "a" }, null, new[] { interior }));
        Assert.Throws<ArgumentException>(() => new AmbientAudioProfile("arena_x", "X", "Root", 2, 2, 0, false, false, false, null, new[] { "a" }, new[] { interior }));
        // a mixer path that does not end in the mixer name, a reroute without the effects flag, empty names
        Assert.Throws<ArgumentException>(() => new AmbientGroupRoute("G", "AmbientIn/Other", "CommonAmbInEffects", AmbientBus.In, members: 1));
        Assert.Throws<ArgumentException>(() => new AmbientGroupRoute("G", "AmbientIn/CommonAmbInEffects", "CommonAmbInEffects", AmbientBus.In, members: 1, effects: false, reverbReroute: true));
        Assert.Throws<ArgumentException>(() => new AmbientGroupRoute("", "AmbientIn/CommonAmbInEffects", "CommonAmbInEffects", AmbientBus.In, members: 1));
        Assert.Throws<ArgumentException>(() => new AmbientGroupRoute("G", "AmbientIn/CommonAmbInEffects", "CommonAmbInEffects", AmbientBus.In, members: 0));
    }

    [Fact]
    public void TheImporterMirrorsTheRootNamesFolderNamesAndGroupMixerPairing()
    {
        // The Editor assembly cannot reference the client; Bay5AudioImporter repeats the root name per map, the asset
        // folder and the group name to mixer pairing, and this is the guard that the repeats cannot drift from the table.
        var source = Text("UnityProject/Assets/ArenaMapPort/Editor/Import/Bay5AudioImporter.cs");
        foreach (var profile in AmbientAudioProfiles.All)
        {
            // The root name must be declared as a const AND be the one RootNameFor returns for this very map key; finding
            // the string alone would pass a swapped or copy-pasted case (a wrong root makes the driver silently find none).
            var declaration = Regex.Match(source, $@"const string (?<n>\w+) = ""{profile.RootName}"";");
            Assert.True(declaration.Success, $"importer declares no const for root {profile.RootName}");
            string constName = declaration.Groups["n"].Value;
            Assert.Matches($@"case ""{profile.MapKey}"": return {constName};", source);
            Assert.Matches($@"case ""{profile.MapKey}"": return ""{profile.DisplayName}"";", source);
        }
        var block = Regex.Match(source, @"GroupMixers\s*=\s*new Dictionary<string, string>\s*\{(?<body>.*?)\};", RegexOptions.Singleline);
        Assert.True(block.Success, "GroupMixers table not found in the importer");
        var importer = Regex.Matches(block.Groups["body"].Value, @"\[""(?<group>\w+)""\]\s*=\s*""(?<mixer>\w+)""")
            .Select(m => (m.Groups["group"].Value, m.Groups["mixer"].Value)).Order().ToArray();
        var client = AmbientAudioProfiles.All.SelectMany(p => p.Routes).Select(r => (r.GroupName, r.MixerName)).Distinct().Order().ToArray();
        Assert.Equal(client, importer);
        // A group name must pair with one mixer on every map that uses it.
        var pairs = AmbientAudioProfiles.All.SelectMany(p => p.Routes).Select(r => (r.GroupName, r.MixerName)).Distinct().ToArray();
        Assert.Equal(pairs.Length, pairs.Select(p => p.GroupName).Distinct().Count());
    }

    [Fact]
    public void TheDriverReadsEverythingPerMapFromTheTable()
    {
        // Bay5AmbientAudio finds its map through the profile table and carries no map-key comparison of its own.
        var lines = Text("ArenaMapPort.Client/Bay5AmbientAudio.cs").Split('\n')
            .Where(l => !l.TrimStart().StartsWith("//") && !l.TrimStart().StartsWith("///"));
        var code = string.Join("\n", lines);
        Assert.Contains("AmbientAudioProfiles.TryGet(mapKey, out var profile)", code);
        Assert.Contains("foreach (var profile in AmbientAudioProfiles.All)", code);
        Assert.DoesNotMatch(@"[!=]=\s*""arena_", code);
        Assert.DoesNotMatch(@"ArenaMapRegistry\.(Equator|Bay5|AirPit)\b", code);
    }
}
