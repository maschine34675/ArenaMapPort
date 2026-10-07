#nullable disable
using System.Runtime.CompilerServices;
using ArenaMapPort.Client;
using ArenaMapPort.Shared;
using EFT;
using HarmonyLib;
using SAIN.Components;
using UnityEngine.SceneManagement;
using Xunit;

namespace ArenaMapPort.Server.Tests
{
    // Exercise the production soft-dependency reflection and real Harmony prefix against only the
    // native LocationClass boundary (private setter/found latch). This does not simulate bot AI.
    [Collection("MapPort client lifecycle")]
    public sealed class EquatorSainProfileTests : IDisposable
    {
        readonly Harmony _harmony = new("mapport.tests.sain-profiles");
        public EquatorSainProfileTests()
        {
            EquatorProfiles.BeginLoad(null, EDateTime.CURR);
            SceneManager.Scenes.Clear();
            SainLocationShim.BindConfig();
            Plugin.Instance.Config.Get<string>("Bots (arena_equator Day)", "SainLocation").Value = "Factory";
            Plugin.Instance.Config.Get<string>("Bots (arena_equator Night)", "SainLocation").Value = "FactoryNight";
            Assert.True(SainLocationShim.TryInstall(_harmony));
        }
        public void Dispose()
        {
            _harmony.UnpatchSelf();
            EquatorProfiles.BeginLoad(null, EDateTime.CURR);
            SceneManager.Scenes.Clear();
        }
        static void Load(ArenaMapDescriptor map, EDateTime selected)
        {
            SceneManager.Scenes.Clear();
            EquatorProfiles.BeginLoad(map.Key, selected);
            SceneManager.Load(new Scene(map.ScriptsSceneName));
            EquatorProfiles.ConfirmSelection(map.Key, selected);
        }

        [Fact]
        public void UnknownProfileDefersFoundLatchUntilActualSelectionIsConfirmed()
        {
            SceneManager.Load(new Scene(ArenaMapRegistry.Equator.ScriptsSceneName));
            var location = new LocationClass();
            location.ManualUpdate(); location.ManualUpdate();
            Assert.False(location.Found);
            Assert.Equal(ELocation.None, location.Location);
            Assert.Equal(0, location.NativeCalls);
            EquatorProfiles.ConfirmSelection("arena_equator", EDateTime.PAST);
            location.ManualUpdate();
            Assert.True(location.Found);
            Assert.Equal(ELocation.FactoryNight, location.Location);
            Assert.Equal(1, location.SetCalls);
        }

        [Fact]
        public void EachRepeatedRaidLatchesItsOwnAliasAndIgnoresSubsequentConfigChanges()
        {
            foreach (var selected in new[] { EDateTime.PAST, EDateTime.CURR, EDateTime.PAST })
            {
                Load(ArenaMapRegistry.Equator, selected);
                var location = new LocationClass();
                location.ManualUpdate(); location.ManualUpdate();
                Assert.Equal(selected == EDateTime.PAST ? ELocation.FactoryNight : ELocation.Factory, location.Location);
                Assert.True(location.Found);
                Assert.Equal(1, location.SetCalls);
                Assert.Equal(0, location.NativeCalls);
                var entry = Plugin.Instance.Config.Get<string>(selected == EDateTime.PAST
                    ? "Bots (arena_equator Night)" : "Bots (arena_equator Day)", "SainLocation");
                string saved = entry.Value; entry.Value = "Customs";
                location.ManualUpdate();
                Assert.Equal(1, location.SetCalls);
                entry.Value = saved;
            }
        }

        [Theory]
        [InlineData("Factory_Night", ELocation.FactoryNight)]
        [InlineData(" factorynight ", ELocation.FactoryNight)]
        [InlineData("NotARealMap", ELocation.Factory)]
        [InlineData("9999999", ELocation.Factory)]
        public void NightAliasUsesInstalledEnumOrValidatedFallback(string configured, ELocation expected)
        {
            Plugin.Instance.Config.Get<string>("Bots (arena_equator Night)", "SainLocation").Value = configured;
            Load(ArenaMapRegistry.Equator, EDateTime.PAST);
            var location = new LocationClass(); location.ManualUpdate();
            Assert.Equal(expected, location.Location);
            Assert.True(location.Found);
            Assert.Equal(0, location.NativeCalls);
        }

        [Fact]
        public void Bay5RetainsCustomsAndVanillaKeepsItsNativeLookup()
        {
            Load(ArenaMapRegistry.Bay5, EDateTime.PAST);
            var bay = new LocationClass(); bay.ManualUpdate();
            Assert.Equal(ELocation.Customs, bay.Location);
            Assert.Equal(0, bay.NativeCalls);
            SceneManager.Scenes.Clear(); EquatorProfiles.BeginLoad("bigmap", EDateTime.CURR);
            var vanilla = new LocationClass(); vanilla.ManualUpdate();
            Assert.Equal(1, vanilla.NativeCalls);
            Assert.Equal(0, vanilla.SetCalls);
        }
    }
}

namespace SAIN.Components
{
    public enum ELocation { None, Factory, FactoryNight, Customs, Interchange }
    public sealed class LocationClass
    {
        bool _foundLocation;
        ELocation _location;
        public bool Found => _foundLocation;
        public int NativeCalls, SetCalls;
        public ELocation Location { get => _location; private set { _location = value; SetCalls++; } }
        public void ManualUpdate() => findLocation();
        [MethodImpl(MethodImplOptions.NoInlining)]
        void findLocation() { if (!_foundLocation) NativeCalls++; }
    }
}
