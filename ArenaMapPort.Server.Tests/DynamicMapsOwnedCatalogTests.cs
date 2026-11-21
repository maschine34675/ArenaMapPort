using ArenaMapPort.Client;
using Newtonsoft.Json;
using Xunit;
using Definition = DynamicMaps.Data.MapDef;
using Layer = DynamicMaps.Data.MapLayerDef;
using Dropdown = DynamicMaps.UI.Controls.MapSelectDropdown;

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
        dropdown.Scan(_catalog, "Maps", (_, message) => warnings.Add(message));
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
        // Native 1.3's next complete scan prunes external entries. The preference prefix must restore all,
        // without recursive scanning, duplication, replacing vanilla objects or losing the current filter.
        dropdown.PruneExternal();
        Assert.Equal(2, dropdown.Definitions.Count);
        dropdown.Scan(_catalog, "Maps", (_, message) => warnings.Add(message));
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
        dropdown.Scan(_catalog, "OtherMaps", (_, _) => Assert.Fail("Unrelated request was processed"));
        Assert.Empty(dropdown.Definitions);
        Assert.Equal(1, dropdown.Refreshes); // The untouched native scan still refreshes its own empty list.
    }

    [Fact]
    public void CorruptDeletedAndMissingImagePacksAreRemovedWithoutLosingValidOrForeignMaps()
    {
        foreach (var map in DynamicMapAssets.Maps) WritePack(map);
        var dropdown = new Dropdown();
        var foreign = new Definition { DisplayName = "Unrelated" };
        dropdown.Definitions["Unrelated.json"] = foreign;
        dropdown.Scan(_catalog, "Maps", (_, _) => Assert.Fail("Valid fixture failed"));
        File.WriteAllText(_assets.DefinitionFor("Equator"), "malformed json");
        File.Delete(_assets.DefinitionFor("AirPit"));
        File.Delete(_assets.ImageFor("Bay5"));
        var warnings = new List<string>();
        dropdown.Scan(_catalog, "Maps", (map, _) => warnings.Add(map));
        Assert.Equal(new[] { "Equator", "Bay5", "AirPit" }, warnings);
        Assert.Equal(5, dropdown.Definitions.Count);
        Assert.Equal(4, dropdown.Times.Count);
        Assert.Same(foreign, dropdown.Definitions["Unrelated.json"]);
        Assert.True(dropdown.Definitions.ContainsKey(_assets.DefinitionFor("Bowl")));
        Assert.True(dropdown.Definitions.ContainsKey(_assets.DefinitionFor("Yard")));
        WritePack("Equator");
        dropdown.Scan(_catalog, "Maps", (_, _) => { });
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
        dropdown.Scan(_catalog, "Maps", (map, _) => rejected.Add(map));
        Assert.Contains("Equator", rejected);
        Assert.Empty(dropdown.Definitions);
        Assert.Empty(dropdown.Times);
    }

    [Fact]
    public void ApiBindingRefusesAnUnknownDropdownBeforeAnyCatalogueMutation()
    {
        Assert.Throws<MissingFieldException>(() => new DynamicMapsOwnedCatalog(_assets, typeof(object), typeof(Definition), typeof(Layer)));
    }

    [Fact]
    public void OwnedPreferenceSurvivesFirstNativeRebindWhenAnotherAddonSharesTheLocation()
    {
        WritePack("Equator");
        var dropdown = new Dropdown { Filter = "arena_equator" };
        dropdown.Definitions["Addons/Other/Equator.jsonc"] = new Definition
        {
            DisplayName = "Third party Equator", MapInternalNames = ["arena_equator"]
        };
        dropdown.Preferred["arena_equator"] = "Equator";
        dropdown.Scan(_catalog, "Maps", (_, _) => { });
        Assert.Equal("Equator", dropdown.Preferred["arena_equator"]);
        Assert.Equal("Equator", dropdown.Current!.DisplayName);
        Assert.Equal(1, dropdown.PreferencePasses);
        dropdown.Scan(_catalog, "Maps", (_, _) => { });
        Assert.Equal("Equator", dropdown.Preferred["arena_equator"]);
        Assert.Equal("Equator", dropdown.Current!.DisplayName);
        Assert.Equal(2, dropdown.PreferencePasses);
    }

    [Fact]
    public void UnchangedOwnedDefinitionRetainsViewportAndFloorButFileChangesInvalidateIt()
    {
        WritePack("Equator");
        var dropdown = new Dropdown { Filter = "arena_equator" };
        dropdown.Scan(_catalog, "Maps", (_, _) => { });
        var original = dropdown.Current;
        dropdown.Zoom = 2.75f;
        dropdown.Floor = 2;
        dropdown.Scan(_catalog, "Maps", (_, _) => { });
        Assert.Same(original, dropdown.Current);
        Assert.Equal(1, dropdown.MapLoads);
        Assert.Equal(2.75f, dropdown.Zoom);
        Assert.Equal(2, dropdown.Floor);

        WritePack("Equator", d => d.DisplayName = "Revised Equator");
        dropdown.Scan(_catalog, "Maps", (_, _) => { });
        Assert.NotSame(original, dropdown.Current);
        Assert.Equal("Revised Equator", dropdown.Current!.DisplayName);
        Assert.Equal(2, dropdown.MapLoads);
        Assert.Equal(1, dropdown.Zoom);
        Assert.Equal(0, dropdown.Floor);
        var beforeImageChange = dropdown.Current;
        File.AppendAllText(_assets.ImageFor("Equator"), "<!-- revised -->");
        dropdown.Scan(_catalog, "Maps", (_, _) => { });
        Assert.NotSame(beforeImageChange, dropdown.Current);

        // An invalid replacement must evict the old cache; repair is parsed even at the same file stamp.
        File.Delete(_assets.ImageFor("Equator"));
        dropdown.Scan(_catalog, "Maps", (_, _) => { });
        Assert.False(dropdown.Definitions.ContainsKey(_assets.DefinitionFor("Equator")));
        WritePack("Equator");
        dropdown.Scan(_catalog, "Maps", (_, _) => { });
        Assert.Equal("Equator", dropdown.Current!.DisplayName);
        var otherDropdown = new Dropdown { Filter = "arena_equator" };
        otherDropdown.Scan(_catalog, "Maps", (_, _) => { });
        Assert.NotSame(dropdown.Current, otherDropdown.Current);
    }

    [Fact]
    public void NestedOtherScansAndExceptionalNativeRegistrationCannotLeakTheMapsScope()
    {
        WritePack("Equator");
        var dropdown = new Dropdown();
        var outer = _catalog.BeginScan(dropdown, "Maps");
        var inner = _catalog.BeginScan(dropdown, "OtherMaps");
        _catalog.PreparePreferences(dropdown, (_, _) => Assert.Fail("Nested unrelated scan merged our maps"));
        Assert.Empty(dropdown.Definitions);
        _catalog.EndScan(inner);
        _catalog.PreparePreferences(dropdown, (_, _) => { });
        Assert.Single(dropdown.Definitions);
        _catalog.EndScan(outer);
        dropdown.Definitions.Clear();
        _catalog.PreparePreferences(dropdown, (_, _) => Assert.Fail("Scope survived its finalizer"));
        Assert.Empty(dropdown.Definitions);

        dropdown.ThrowInPreferences = true;
        Assert.Throws<IOException>(() => dropdown.Scan(_catalog, "Maps", (_, _) => { }));
        dropdown.Definitions.Clear();
        _catalog.PreparePreferences(dropdown, (_, _) => Assert.Fail("Exceptional scan leaked its scope"));
        Assert.Empty(dropdown.Definitions);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
