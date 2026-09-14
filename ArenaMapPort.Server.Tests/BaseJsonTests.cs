using System.Text.Json;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Utils.Json;
using Xunit;

namespace ArenaMapPort.Server.Tests;

/// <summary>
/// Loads the shipped db/base.json and asserts the invariants docs/SPEC.md §E.4 and
/// §B.WP6's validate_server_db.py table demand: identity constants, Scene.path/rcid,
/// SpawnPointParams absent (not []), Banners empty, DisabledForScav true.
/// </summary>
public class BaseJsonTests
{
    private const string ExpectedKey = "arena_equator";
    private const string ExpectedIdField = "e9b8e12c60dfd2820e949462";
    private const string ExpectedScenePath = "maps/arena_equator_preset.bundle";
    private const string ExpectedSceneRcid = "arena_equator.scenespreset.asset";

    // Same converter set the live server's JsonUtil registers (SptJsonConverterRegistrator),
    // so these tests parse db/base.json exactly as the server does at load.
    private static readonly JsonSerializerOptions Options = CreateServerOptions();

    private static JsonSerializerOptions CreateServerOptions()
    {
        var options = new JsonSerializerOptions { ReadCommentHandling = JsonCommentHandling.Skip };
        foreach (var converter in new SptJsonConverterRegistrator().GetJsonConverters())
            options.Converters.Add(converter);
        return options;
    }

    private static string BaseJsonPath => Path.Combine(AppContext.BaseDirectory, "db", "base.json");

    private static string ReadRawJson() => File.ReadAllText(BaseJsonPath);

    private static LocationBase Deserialize()
    {
        var json = ReadRawJson();
        var location = JsonSerializer.Deserialize<LocationBase>(json, Options);
        Assert.NotNull(location);
        return location!;
    }

    [Fact]
    public void File_Exists()
    {
        Assert.True(File.Exists(BaseJsonPath), $"db/base.json not found at {BaseJsonPath}");
    }

    [Fact]
    public void SpawnPointParams_KeyIsAbsentFromTheRawJson()
    {
        // critic.md §6: an empty-but-PRESENT array actively deletes every
        // scene-authored SpawnPointMarker on load (FixSpawns) — the key must not be in
        // the file at all, not merely null or []. A substring check on the raw text
        // catches "SpawnPointParams": [] just as much as it catches a present key with
        // any other value.
        var json = ReadRawJson();

        Assert.DoesNotContain("SpawnPointParams", json);
    }

    [Fact]
    public void Deserializes_WithSpawnPointParamsNull()
    {
        var location = Deserialize();

        Assert.Null(location.SpawnPointParams);
    }

    [Fact]
    public void Identity_MatchesSpecConstants()
    {
        var location = Deserialize();

        Assert.Equal(ExpectedKey, location.Id);
        Assert.Equal(ExpectedIdField, location.IdField.ToString());
    }

    [Fact]
    public void Scene_PathAndRcid_MatchSpecConstants()
    {
        var location = Deserialize();

        Assert.NotNull(location.Scene);
        Assert.Equal(ExpectedScenePath, location.Scene!.Path);
        Assert.Equal(ExpectedSceneRcid, location.Scene.Rcid);
    }

    [Fact]
    public void Banners_IsEmpty_NotNull()
    {
        var location = Deserialize();

        Assert.NotNull(location.Banners);
        Assert.Empty(location.Banners!);
    }

    [Fact]
    public void DisabledForScav_IsTrue()
    {
        var location = Deserialize();

        Assert.True(location.DisabledForScav);
    }

    [Fact]
    public void EnabledAndLocked_AllowSelectionInMapSelectUI()
    {
        var location = Deserialize();

        Assert.True(location.Enabled);
        Assert.False(location.Locked);
    }

    [Fact]
    public void Loot_IsEmpty_NotNull()
    {
        var location = Deserialize();

        Assert.NotNull(location.Loot);
        Assert.Empty(location.Loot!);
    }

    [Fact]
    public void MinAndMaxPlayers_AreOne()
    {
        var location = Deserialize();

        Assert.Equal(1, location.MinPlayers);
        Assert.Equal(1, location.MaxPlayers);
    }

    // The map selection screen calls location.BossLocationSpawn.Any(...) and the raid start
    // reads location.waves without null checks (MatchMakerSelectionLocationScreen.cs:439/451,
    // LocationInfoPanel.cs:174, BaseLocalGame.cs:415, LocalGame.cs:143/147). A missing array
    // throws while the button is built, so the location silently never appears.
    [Fact]
    public void ClientDereferencedArrays_ArePresentAndEmpty()
    {
        var location = Deserialize();

        Assert.NotNull(location.BossLocationSpawn);
        Assert.Empty(location.BossLocationSpawn!);
        Assert.NotNull(location.Waves);
        Assert.Empty(location.Waves!);
    }
}
