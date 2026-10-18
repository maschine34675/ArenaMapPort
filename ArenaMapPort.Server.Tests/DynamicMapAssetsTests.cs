using ArenaMapPort.Client;
using Xunit;

namespace ArenaMapPort.Server.Tests;

public sealed class DynamicMapAssetsTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "ArenaMapPort-DynamicMapAssets-" + Guid.NewGuid().ToString("N"));
    readonly DynamicMapAssets _assets;

    public DynamicMapAssetsTests() => _assets = new DynamicMapAssets(_root);

    void WritePack(string map)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_assets.ImageFor(map))!);
        File.WriteAllText(_assets.DefinitionFor(map), "{}");
        File.WriteAllText(_assets.ImageFor(map), "<svg/>");
    }

    [Theory]
    [InlineData("Equator")]
    [InlineData("Bay5")]
    [InlineData("AirPit")]
    [InlineData("Bowl")]
    public void ResolvesOnlyExactOwnedDefinitionAndGroundLayer(string map)
    {
        var token = $"Maps/ArenaMapPort_{map}/Layers/Ground.svg";
        Assert.Equal(_assets.ImageFor(map), _assets.ResolveImage(_assets.DefinitionFor(map), token));
        Assert.Null(_assets.ResolveImage(Path.Combine(_root, "Other", $"ArenaMapPort_{map}.jsonc"), token));
        Assert.Null(_assets.ResolveImage(_assets.DefinitionFor(map), "Markers/exit.png"));
        Assert.Null(_assets.ResolveImage(_assets.DefinitionFor(map), "../../unrelated.svg"));
        Assert.Null(_assets.ResolveImage(_assets.DefinitionFor(map), $"Maps/ArenaMapPort_{(map == "Bay5" ? "Equator" : "Bay5")}/Layers/Ground.svg"));
        Assert.Null(_assets.ResolveImage($"ArenaMapPort_{map}.jsonc", token));
    }

    [Fact]
    public void NormalizesOneGroundLayerAndRejectsMalformedOwnedDefinitions()
    {
        WritePack("Equator");
        var path = _assets.DefinitionFor("Equator");
        var layer = new TestLayer { ImagePath = "Maps/ArenaMapPort_Equator/Layers/Ground.svg" };
        var layers = new System.Collections.Hashtable { ["Ground"] = layer };
        bool Normalize(System.Collections.IDictionary value) => _assets.NormalizeGroundLayer(path, value,
            item => ((TestLayer)item).ImagePath, (item, image) => ((TestLayer)item).ImagePath = image);
        Assert.True(Normalize(layers));
        Assert.Equal(_assets.ImageFor("Equator"), layer.ImagePath);
        layer.ImagePath = "missing.svg";
        Assert.False(Normalize(layers));
        Assert.Equal("missing.svg", layer.ImagePath);
        Assert.False(Normalize(new System.Collections.Hashtable()));
        Assert.False(Normalize(null!));
        Assert.False(Normalize(new System.Collections.Hashtable { ["Ground"] = null }));
        layer.ImagePath = "Maps/ArenaMapPort_Equator/Layers/Ground.svg";
        Assert.Throws<IOException>(() => _assets.NormalizeGroundLayer(path, layers,
            item => ((TestLayer)item).ImagePath, (_, _) => throw new IOException("setter failure")));
        File.Delete(_assets.ImageFor("Equator"));
        Assert.False(Normalize(layers));
    }

    sealed class TestLayer { public string ImagePath = ""; }

    [Fact]
    public void NativeRequestLoadsBothOnceAndRejectsRecursiveAndUnrelatedRequests()
    {
        foreach (var map in DynamicMapAssets.Maps) WritePack(map);
        var loaded = new List<string>();
        void Load(string path)
        {
            loaded.Add(path);
            _assets.LoadAdditional(path, Load, _ => Assert.Fail("missing pack"));
            _assets.LoadAdditional("Maps", Load, _ => Assert.Fail("recursive load"));
        }
        _assets.LoadAdditional("OtherMaps", Load, _ => Assert.Fail("unrelated request"));
        Assert.Empty(loaded);
        _assets.LoadAdditional("Maps", Load, _ => Assert.Fail("missing pack"));
        Assert.Equal(DynamicMapAssets.Maps.Select(_assets.DirectoryFor), loaded);
        loaded.Clear();
        _assets.LoadAdditional("Maps", Load, _ => Assert.Fail("missing pack"));
        Assert.Equal(DynamicMapAssets.Maps.Length, loaded.Count); // native loader retains mtime-based cache/refresh behavior
    }

    [Fact]
    public void MissingSvgIsSkippedAndFailureDoesNotLatchLoadingGuard()
    {
        WritePack("Equator");
        WritePack("Bay5");
        File.Delete(_assets.ImageFor("Bay5"));
        var loaded = new List<string>();
        var missing = new List<string>();
        _assets.LoadAdditional("Maps", loaded.Add, missing.Add);
        Assert.Equal(new[] { _assets.DirectoryFor("Equator") }, loaded);
        Assert.Equal(new[] { "Bay5", "AirPit", "Bowl" }, missing);
        Assert.Throws<IOException>(() => _assets.LoadAdditional("Maps", _ => throw new IOException("fixture"), missing.Add));
        loaded.Clear();
        _assets.LoadAdditional("Maps", loaded.Add, missing.Add);
        Assert.Single(loaded);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
