using System.Reflection;
using ArenaMapPort.Client;
using HarmonyLib;
using Newtonsoft.Json;
using Xunit;
using Definition = DynamicMaps.Data.MapDef;
using Dropdown = DynamicMaps.UI.Controls.MapSelectDropdown;
using Layer = DynamicMaps.Data.MapLayerDef;

namespace ArenaMapPort.Server.Tests
{
    // Real Harmony runs production Install's registrations on minimal native API fixtures.
    // The actual released DynamicMaps binary is independently checked by test_dynamicmaps_contracts.ps1.
    public sealed class DynamicMapsIntegrationTests : IDisposable
    {
        readonly string _root = Path.Combine(Path.GetTempPath(), "ArenaMapPort-Hooks-" + Guid.NewGuid().ToString("N"));
        readonly DynamicMapAssets _assets;
        readonly Harmony _harmony = new("test.mapport.dynamicmaps." + Guid.NewGuid().ToString("N"));
        readonly MethodInfo _load = typeof(Dropdown).GetMethod("LoadMapDefsFromPath")!;
        readonly MethodInfo _preferences = typeof(Dropdown).GetMethod("RegisterMapPreferences", BindingFlags.NonPublic | BindingFlags.Instance)!;

        public DynamicMapsIntegrationTests()
        {
            _assets = new DynamicMapAssets(_root);
            BepInEx.Bootstrap.Chainloader.PluginInfos[DynamicMapsIntegration.PluginGuid] = new object();
            DynamicMapsIntegration.Install(_harmony);
            // Production derives its root from its DLL location. Only this test redirects the data root.
            var catalog = new DynamicMapsOwnedCatalog(_assets, typeof(Dropdown), typeof(Definition), typeof(Layer));
            typeof(DynamicMapsIntegration).GetField("_catalog", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, catalog);
            foreach (var map in DynamicMapAssets.Maps)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_assets.ImageFor(map))!);
                var definition = new Definition
                {
                    DisplayName = map, MapInternalNames = ["arena_" + map.ToLowerInvariant()],
                    Layers = new() { ["Ground"] = new Layer { ImagePath = $"Maps/ArenaMapPort_{map}/Layers/Ground.svg" } }
                };
                File.WriteAllText(_assets.DefinitionFor(map), JsonConvert.SerializeObject(definition));
                File.WriteAllText(_assets.ImageFor(map), "<svg/>");
            }
        }

        [Fact]
        public void InstalledPreferencePrefixRunsBeforeTheFirstNativeRebind()
        {
            var dropdown = new Dropdown { Filter = "arena_equator" };
            dropdown.Definitions["Addons/Other/Equator.jsonc"] = new Definition { DisplayName = "Other", MapInternalNames = ["arena_equator"] };
            dropdown.Preferred["arena_equator"] = "Equator";
            dropdown.LoadMapDefsFromPath("Maps");
            Assert.Equal("Equator", dropdown.Preferred["arena_equator"]);
            Assert.Equal(1, dropdown.PreferencePasses);
            var definition = dropdown.Definitions[_assets.DefinitionFor("Equator")];
            dropdown.LoadMapDefsFromPath("Maps");
            Assert.Same(definition, dropdown.Definitions[_assets.DefinitionFor("Equator")]);
            Assert.Equal("Equator", dropdown.Preferred["arena_equator"]);
            Assert.Equal(2, dropdown.PreferencePasses);
        }

        [Fact]
        public void InstalledFinalizerClearsTheScopeOnNativeThrowAndSkippedOriginal()
        {
            var original = new IOException("native load failure");
            var dropdown = new Dropdown { NativeFailure = original };
            Assert.Same(original, Assert.Throws<IOException>(() => dropdown.LoadMapDefsFromPath("Maps")));
            dropdown.NativeFailure = null;
            dropdown.Definitions.Clear();
            _preferences.Invoke(dropdown, null);
            Assert.Empty(dropdown.Definitions);
            // Skip the original AFTER our normal-priority scope prefix has run.
            _harmony.Patch(_load, prefix: new HarmonyMethod(typeof(DynamicMapsIntegrationTests), nameof(SkipOriginal)) { priority = Priority.Last });
            dropdown.LoadMapDefsFromPath("Maps");
            _preferences.Invoke(dropdown, null);
            Assert.Empty(dropdown.Definitions);
        }

        static bool SkipOriginal() => false;

        public void Dispose()
        {
            _harmony.UnpatchSelf();
            BepInEx.Bootstrap.Chainloader.PluginInfos.Clear();
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
    }
}

namespace BepInEx.Bootstrap
{
    internal static class Chainloader { internal static readonly Dictionary<string, object> PluginInfos = new(); }
}
