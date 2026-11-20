using ArenaMapPort.Client;
using Newtonsoft.Json;
using Xunit;

namespace ArenaMapPort.Server.Tests;

public sealed class DynamicMapsOwnedCatalogTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "ArenaMapPort-Catalog-" + Guid.NewGuid().ToString("N"));
    readonly DynamicMapAssets _assets;
    readonly DynamicMapsOwnedCatalog _catalog;

    public DynamicMapsOwnedCatalogTests()
    {
        _assets = new DynamicMapAssets(_root);
        _catalog = new DynamicMapsOwnedCatalog(_assets, typeof(Dropdown), typeof(Definition), typeof(Layer));
    }

    void WritePack(string map, Action<Definition>? edit = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_assets.ImageFor(map))!);
        var value = new Definition
        {
            DisplayName = map,
            MapInternalNames = ["arena_" + map.ToLowerInvariant()],
            Bounds = new object(),
            Layers = new() { ["Ground"] = new Layer
            {
                ImagePath = $"Maps/ArenaMapPort_{map}/Layers/Ground.svg", ImageBounds = new object(), GameBounds = [new object()]
            } },
            StaticMarkers = ["Markers/exit.png"]
        };
        edit?.Invoke(value);
        File.WriteAllText(_assets.DefinitionFor(map), JsonConvert.SerializeObject(value));
        File.WriteAllText(_assets.ImageFor(map), "<svg/>");
    }

    [Fact]
    public void NativeScanRetainsVanillaAndAddonsWhileAllOwnedPacksJoinTheSameFilteredCatalogue()
    {
        foreach (var map in DynamicMapAssets.Maps) WritePack(map);
        var dropdown = new Dropdown { Filter = "arena_bay5" };
        var vanilla = new Definition { DisplayName = "Vanilla", MapInternalNames = ["bigmap"] };
        var addon = new Definition { DisplayName = "Third party", MapInternalNames = ["bigmap"] };
        dropdown.Definitions["Maps/Customs.jsonc"] = vanilla;
        dropdown.Definitions["Addons/Other/Customs.jsonc"] = addon;
        var warnings = new List<string>();
        _catalog.Merge(dropdown, "Maps", (_, message) => warnings.Add(message));
        Assert.Empty(warnings);
        Assert.Equal(DynamicMapAssets.Maps.Length + 2, dropdown.Definitions.Count);
        Assert.Same(vanilla, dropdown.Definitions["Maps/Customs.jsonc"]);
        Assert.Same(addon, dropdown.Definitions["Addons/Other/Customs.jsonc"]);
        Assert.Equal("Bay5", Assert.Single(dropdown.Available).DisplayName);
        Assert.Equal(dropdown.Definitions.Count, dropdown.RegisteredCount);
        foreach (var map in DynamicMapAssets.Maps)
        {
            var definition = dropdown.Definitions[_assets.DefinitionFor(map)];
            Assert.Equal(_assets.ImageFor(map), Assert.Single(definition.Layers).Value.ImagePath);
            Assert.Equal("ArenaMapPort/" + map, definition.SelectionId);
            Assert.True(definition.IsAddon);
            Assert.Equal("Markers/exit.png", Assert.Single(definition.StaticMarkers));
        }
        // Native 1.3's next complete scan prunes external entries. The single postfix must restore all,
        // without recursive scanning, duplication, replacing vanilla objects or losing the current filter.
        dropdown.PruneExternal();
        Assert.Equal(2, dropdown.Definitions.Count);
        _catalog.Merge(dropdown, "Maps", (_, message) => warnings.Add(message));
        Assert.Empty(warnings);
        Assert.Equal(DynamicMapAssets.Maps.Length + 2, dropdown.Definitions.Count);
        Assert.Same(vanilla, dropdown.Definitions["Maps/Customs.jsonc"]);
        Assert.Same(addon, dropdown.Definitions["Addons/Other/Customs.jsonc"]);
        Assert.Equal("Bay5", Assert.Single(dropdown.Available).DisplayName);
    }

    [Fact]
    public void UnrelatedRequestsDoNotParseOrRefreshAnything()
    {
        WritePack("Equator");
        var dropdown = new Dropdown();
        _catalog.Merge(dropdown, "OtherMaps", (_, _) => Assert.Fail("Unrelated request was processed"));
        Assert.Empty(dropdown.Definitions);
        Assert.Equal(0, dropdown.Refreshes);
    }

    [Fact]
    public void CorruptDeletedAndMissingImagePacksAreRemovedWithoutLosingValidOrForeignMaps()
    {
        foreach (var map in DynamicMapAssets.Maps) WritePack(map);
        var dropdown = new Dropdown();
        var foreign = new Definition { DisplayName = "Unrelated" };
        dropdown.Definitions["Unrelated.json"] = foreign;
        _catalog.Merge(dropdown, "Maps", (_, _) => Assert.Fail("Valid fixture failed"));
        File.WriteAllText(_assets.DefinitionFor("Equator"), "malformed json");
        File.Delete(_assets.DefinitionFor("AirPit"));
        File.Delete(_assets.ImageFor("Bay5"));
        var warnings = new List<string>();
        _catalog.Merge(dropdown, "Maps", (map, _) => warnings.Add(map));
        Assert.Equal(new[] { "Equator", "Bay5", "AirPit" }, warnings);
        Assert.Equal(4, dropdown.Definitions.Count);
        Assert.Equal(3, dropdown.Times.Count);
        Assert.Same(foreign, dropdown.Definitions["Unrelated.json"]);
        Assert.True(dropdown.Definitions.ContainsKey(_assets.DefinitionFor("Bowl")));
        WritePack("Equator");
        _catalog.Merge(dropdown, "Maps", (_, _) => { });
        Assert.True(dropdown.Definitions.ContainsKey(_assets.DefinitionFor("Equator")));
    }

    [Theory]
    [InlineData("display")]
    [InlineData("bounds")]
    [InlineData("names")]
    [InlineData("layers")]
    [InlineData("image-bounds")]
    [InlineData("game-bounds")]
    [InlineData("foreign-image")]
    [InlineData("absolute-image")]
    [InlineData("multiple-layers")]
    public void IncompleteOrUnownedLayerPathsAreRejected(string fault)
    {
        WritePack("Equator", value =>
        {
            var layer = value.Layers["Ground"];
            switch (fault)
            {
                case "display": value.DisplayName = " "; break;
                case "bounds": value.Bounds = null!; break;
                case "names": value.MapInternalNames = null!; break;
                case "layers": value.Layers = null!; break;
                case "image-bounds": layer.ImageBounds = null!; break;
                case "game-bounds": layer.GameBounds = null!; break;
                case "foreign-image": layer.ImagePath = "Maps/ArenaMapPort_Bay5/Layers/Ground.svg"; break;
                case "absolute-image": layer.ImagePath = _assets.ImageFor("Equator"); break;
                case "multiple-layers": value.Layers["Other"] = layer; break;
            }
        });
        var dropdown = new Dropdown();
        var rejected = new List<string>();
        _catalog.Merge(dropdown, "Maps", (map, _) => rejected.Add(map));
        Assert.Contains("Equator", rejected);
        Assert.Empty(dropdown.Definitions);
        Assert.Empty(dropdown.Times);
    }

    [Fact]
    public void ApiBindingRefusesAnUnknownDropdownBeforeAnyCatalogueMutation()
    {
        Assert.Throws<MissingFieldException>(() => new DynamicMapsOwnedCatalog(_assets, typeof(object), typeof(Definition), typeof(Layer)));
    }

    public sealed class Definition
    {
        public string DisplayName { get; set; } = "";
        public object Bounds { get; set; } = new();
        public List<string> MapInternalNames { get; set; } = [];
        public Dictionary<string, Layer> Layers { get; set; } = new();
        public List<string> StaticMarkers { get; set; } = [];
        [JsonIgnore] public string SelectionId { get; set; } = "";
        [JsonIgnore] public bool IsAddon { get; set; }
        // If the old recursive/native-loader path is accidentally restored, the fixture fails.
        public static Definition LoadFromPath(string path, string? addon = null) => throw new InvalidOperationException("Native validation ran before owned path resolution");
    }

    public sealed class Layer
    {
        public string ImagePath { get; set; } = "";
        public object ImageBounds { get; set; } = new();
        public List<object> GameBounds { get; set; } = [];
    }

    public sealed class Dropdown
    {
        readonly Dictionary<string, Definition> _mapDefs = new();
        readonly Dictionary<string, DateTime> _writeTimes = new();
        public Dictionary<string, Definition> Definitions => _mapDefs;
        public Dictionary<string, DateTime> Times => _writeTimes;
        public string? Filter { get; set; }
        public List<Definition> Available { get; private set; } = [];
        public int RegisteredCount { get; private set; }
        public int Refreshes { get; private set; }
        void RegisterMapPreferences() => RegisteredCount = _mapDefs.Count;
        IEnumerable<Definition> FilteredMapDefs() => _mapDefs.Values.Where(v => Filter == null || v.MapInternalNames.Contains(Filter));
        void ChangeAvailableMapDefs(IEnumerable<Definition> values) { Available = values.ToList(); Refreshes++; }
        public void PruneExternal()
        {
            foreach (var key in _mapDefs.Keys.Where(Path.IsPathRooted).ToArray()) { _mapDefs.Remove(key); _writeTimes.Remove(key); }
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
