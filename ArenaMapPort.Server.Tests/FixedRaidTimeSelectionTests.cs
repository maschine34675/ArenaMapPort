using System.Reflection;
using System.Runtime.CompilerServices;
using ArenaMapPort.Client;
using ArenaMapPort.Shared;
using EFT;
using EFT.UI.Matchmaker;
using EFT.Utilities;
using HarmonyLib;
using JsonType;
using UnityEngine;
using Xunit;

namespace ArenaMapPort.Server.Tests
{
    // The real production hook is installed with Harmony on a narrow native boundary double.
    // In particular, time toggles notify the controller but never call Set a second time.
    [Collection("MapPort client lifecycle")]
    public sealed class FixedRaidTimeSelectionTests : IDisposable
    {
        readonly Harmony _harmony = new("mapport.tests.menu-time-weather");
        public FixedRaidTimeSelectionTests()
        {
            EquatorNightNeon.Enabled = EquatorNightNeon.Rain = true;
            ResourcesCache.Rain = new Sprite();
            FixedRaidTimeSelection.Install(_harmony);
        }
        public void Dispose() => _harmony.UnpatchSelf();
        static RaidSettings Settings(string key, EDateTime time = EDateTime.CURR) =>
            new() { SelectedLocation = new() { Id = key }, SelectedDateTime = time };
        static void Set(LocationConditionsPanel panel, RaidSettings settings) => panel.Set(settings);

        [Fact]
        public void BothPortedMapsJoinNativeChoicesAndKeepPastWhileOtherMapsKeepVanillaReset()
        {
            Assert.Equal(new[] { "bigmap", "arena_equator", "arena_bay5" }, LocationSettings.Location.NightTimeAllowedLocations);
            foreach (string key in new[] { "arena_equator", "arena_bay5", "bigmap", "unknown" })
            {
                var settings = Settings(key, EDateTime.PAST);
                var controller = new MatchMakerSelectionLocationScreen.SelectionLocationScreenController { RaidSettings = settings };
                controller.UpdateSelectedDateTime();
                Assert.Equal(key == "unknown" ? EDateTime.CURR : EDateTime.PAST, settings.SelectedDateTime);
            }
        }

        [Theory]
        [InlineData("arena_equator")]
        [InlineData("arena_bay5")]
        public void BothChoicesStayFixedAndNativeToggleEventsRemainSingle(string key)
        {
            var panel = new LocationConditionsPanel(); var settings = Settings(key);
            panel.OnTimeChanged += time => settings.SelectedDateTime = time;
            Set(panel, settings);
            Assert.False(panel.NeedsTimer);
            Assert.Equal("12:00:00", panel._currentPhaseTime.text);
            Assert.Equal("00:00:00", panel._nextPhaseTime.text);
            panel.SelectedDateTime = EDateTime.PAST;
            Assert.Equal(1, panel.SetCalls);
            Assert.Equal(1, panel.TimeEvents);
            Assert.Equal(EDateTime.PAST, settings.SelectedDateTime);
            Assert.Same(key == "arena_equator" ? ResourcesCache.Rain : panel.NativeSprite, panel._weatherIcon.sprite);
            panel.SelectedDateTime = EDateTime.CURR;
            Assert.Equal(2, panel.TimeEvents);
            Assert.Same(panel.NativeSprite, panel._weatherIcon.sprite);
        }

        [Fact]
        public void NightToDayRestoresAnOriginallyHiddenWeatherIcon()
        {
            var panel = new LocationConditionsPanel { NativeVisible = false };
            Set(panel, Settings("arena_equator", EDateTime.PAST));
            Assert.Same(ResourcesCache.Rain, panel._weatherIcon.sprite);
            Assert.True(panel._weatherIcon.gameObject.activeSelf);
            panel.SelectedDateTime = EDateTime.CURR;
            Assert.Same(panel.NativeSprite, panel._weatherIcon.sprite);
            Assert.False(panel._weatherIcon.gameObject.activeSelf);
        }

        [Theory]
        [InlineData(false, true)]
        [InlineData(true, false)]
        public void DisabledNightFeatureOrRainKeepsNativeWeather(bool enabled, bool rain)
        {
            EquatorNightNeon.Enabled = enabled; EquatorNightNeon.Rain = rain;
            var panel = new LocationConditionsPanel(); Set(panel, Settings("arena_equator", EDateTime.PAST));
            Assert.Same(panel.NativeSprite, panel._weatherIcon.sprite);
            panel.SelectedDateTime = EDateTime.CURR;
            panel.SelectedDateTime = EDateTime.PAST;
            Assert.Same(panel.NativeSprite, panel._weatherIcon.sprite);
        }

        [Fact]
        public void MapReuseClearsOldContextBeforeNativeSetAndNeverTouchesOrdinaryPanel()
        {
            var panel = new LocationConditionsPanel(); Set(panel, Settings("arena_equator", EDateTime.PAST));
            Assert.Same(ResourcesCache.Rain, panel._weatherIcon.sprite);
            panel.NativeSprite = new Sprite(); panel.EmitTimeInsideSet = true;
            Set(panel, Settings("bigmap", EDateTime.PAST));
            Assert.True(panel.NeedsTimer);
            Assert.Same(panel.NativeSprite, panel._weatherIcon.sprite);
            Assert.Same(panel.NativeSprite, panel.SpriteDuringSet);
            panel.SelectedDateTime = EDateTime.CURR;
            panel.SelectedDateTime = EDateTime.PAST;
            Assert.Same(panel.NativeSprite, panel._weatherIcon.sprite);
            Set(panel, Settings("arena_equator", EDateTime.PAST));
            Assert.Same(ResourcesCache.Rain, panel._weatherIcon.sprite);
            Set(panel, Settings("arena_bay5", EDateTime.PAST));
            Assert.Same(panel.NativeSprite, panel._weatherIcon.sprite);
        }

        [Fact]
        public void DistinctPanelsKeepDistinctFallbacksAndMissingRainSpriteFailsOpen()
        {
            var first = new LocationConditionsPanel(); var second = new LocationConditionsPanel();
            Set(first, Settings("arena_equator", EDateTime.PAST));
            Set(second, Settings("arena_equator", EDateTime.PAST));
            first.SelectedDateTime = EDateTime.CURR;
            Assert.Same(first.NativeSprite, first._weatherIcon.sprite);
            Assert.Same(ResourcesCache.Rain, second._weatherIcon.sprite);
            ResourcesCache.Rain = null;
            second.SelectedDateTime = EDateTime.CURR;
            second.SelectedDateTime = EDateTime.PAST;
            Assert.Same(second.NativeSprite, second._weatherIcon.sprite);
        }

        [Fact]
        public void SingleTimeSummaryUsesSelectedHourAndUnknownChoiceNeverClaimsRain()
        {
            var panel = new LocationConditionsPanel { _amTimeToggle = null, _pmTimeToggle = null };
            Set(panel, Settings("arena_bay5", EDateTime.PAST));
            Assert.Equal("00:00:00", panel._currentPhaseTime.text);
            Set(panel, Settings("arena_equator", EDateTime.PAST));
            panel.SelectedDateTime = (EDateTime)123;
            Assert.Same(panel.NativeSprite, panel._weatherIcon.sprite);
        }
    }
}

namespace UnityEngine { public sealed class Sprite : Object { } }
namespace JsonType
{
    public sealed class LocationSettings
    {
        public sealed class Location
        {
            public string Id;
            public static IEnumerable<string> NightTimeAllowedLocations
            { [MethodImpl(MethodImplOptions.NoInlining)] get => new[] { "bigmap" }; }
        }
    }
}
namespace EFT
{
    public enum EDateTime { CURR, PAST }
    public sealed class RaidSettings
    { public LocationSettings.Location SelectedLocation; public EDateTime SelectedDateTime; }
}
namespace EFT.Weather { public enum EWeatherType { Rain } }
namespace EFT.UI.Matchmaker
{
    public sealed class MatchMakerSelectionLocationScreen
    {
        public sealed class SelectionLocationScreenController
        {
            public RaidSettings RaidSettings;
            [MethodImpl(MethodImplOptions.NoInlining)]
            public void UpdateSelectedDateTime()
            { if (RaidSettings.SelectedLocation.Id != "bigmap") RaidSettings.SelectedDateTime = EDateTime.CURR; }
        }
    }
    public sealed class LocationConditionsPanel
    {
        public sealed class Label { public string text; }
        public sealed class Image { public Sprite sprite; public GameObject gameObject = new("weather"); }
        public Label _currentPhaseTime = new(), _nextPhaseTime = new();
        public Image _weatherIcon = new();
        public object _amTimeToggle = new(), _pmTimeToggle = new();
        bool _needTimerUpdate;
        public bool NeedsTimer => _needTimerUpdate;
        public Sprite NativeSprite = new(), SpriteDuringSet;
        public bool NativeVisible = true, EmitTimeInsideSet;
        public int SetCalls, TimeEvents;
        public event Action<EDateTime> OnTimeChanged;
        EDateTime _selected;
        public EDateTime SelectedDateTime
        {
            get => _selected;
            [MethodImpl(MethodImplOptions.NoInlining)]
            set { _selected = value; TimeEvents++; OnTimeChanged?.Invoke(value); }
        }
        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Set(RaidSettings raidSettings)
        {
            SetCalls++; _selected = raidSettings.SelectedDateTime; _needTimerUpdate = true;
            _weatherIcon.sprite = NativeSprite; _weatherIcon.gameObject.SetActive(NativeVisible);
            if (EmitTimeInsideSet) SelectedDateTime = _selected;
            SpriteDuringSet = _weatherIcon.sprite;
        }
    }
}
namespace EFT.Utilities
{
    public static class ResourcesCache
    {
        public static Sprite Rain = new();
        public static T Pop<T>(string path) where T : class => path == "UI/WeatherIcons/Rain" ? Rain as T : null;
    }
}
namespace ArenaMapPort.Client
{
    internal static class HarmonyPatches
    { internal static bool Verify(MethodBase? method, Type type, string name) => method != null; }
    internal static class EquatorNightNeon
    {
        internal static bool Enabled = true, Rain = true;
        internal static bool RainForSelection(string key, int? hour) => ArenaNeonPolicy.RequestsRain(Enabled, Rain, key, hour);
    }
}
