#nullable disable
using System.Runtime.CompilerServices;
using ArenaMapPort.Client;
using ArenaMapPort.Shared;
using BepInEx.Configuration;
using EFT;
using EFT.Weather;
using HarmonyLib;
using UnityEngine.SceneManagement;
using Xunit;

namespace ArenaMapPort.Server.Tests
{
    // Production prefixes/postfixes against boundary doubles that retain the reviewed native branch
    // order. Tests assert native execution, backend node identity and initialization side effects;
    // they do not claim to simulate Unity rendering or the server's weather generator.
    [Collection("MapPort client lifecycle")]
    public sealed class MapWeatherRuntimeTests : IDisposable
    {
        readonly Harmony _harmony = new("mapport.tests.runtime-time-weather");
        public MapWeatherRuntimeTests()
        {
            PlayerOptions.Install(new ConfigFile());
            SceneManager.Scenes.Clear();
            EquatorNightNeon.Enabled = EquatorNightNeon.Rain = true;
            TimeOfDayRepairs.Install(); WeatherTestOverride.Install();
            Plugin.Instance.Config.Get<bool>("TimeOfDay", "Enabled").Value = true;
            Plugin.Instance.Config.Get<int>("TimeOfDay", "ArenaFixedHour").Value = 0;
            SetWeather(WeatherTestOverride.TestWeather.Clear);
            _harmony.Patch(AccessTools.Method(typeof(LocalGame), nameof(LocalGame.InitWeather)),
                prefix: new HarmonyMethod(typeof(TimeOfDayRepairs), nameof(TimeOfDayRepairs.Prefix)));
            _harmony.Patch(AccessTools.Method(typeof(WeatherController), nameof(WeatherController.SetWeatherNodes)),
                postfix: new HarmonyMethod(typeof(WeatherTestOverride), nameof(WeatherTestOverride.Postfix)));
        }
        public void Dispose()
        {
            PlayerOptions.Install(new ConfigFile());
            _harmony.UnpatchSelf(); SceneManager.Scenes.Clear();
            Comfort.Common.Singleton<GameWorld>.Instance = null;
        }
        static void SetWeather(WeatherTestOverride.TestWeather weather) =>
            Plugin.Instance.Config.Get<WeatherTestOverride.TestWeather>("Weather (test)", "Weather").Value = weather;
        static LocalGame Game(ArenaMapDescriptor map, EDateTime selected)
        {
            EquatorProfiles.BeginLoad(map.Key, selected);
            SceneManager.Scenes.Clear(); SceneManager.Load(new Scene(map.ScriptsSceneName));
            return new LocalGame { Location = new() { Id = map.Key }, _selectedDateTime = selected };
        }
        static TimeAndWeatherSettings RandomSettings => new() { IsRandomTime = true, IsRandomWeather = true };
        static WeatherNode[] BackendNodes() => new[]
        {
            new WeatherNode { Time = DateTime.UtcNow.Ticks, Rain = .2f, Cloudness = -.3f },
            new WeatherNode { Time = DateTime.UtcNow.AddMinutes(10).Ticks, Rain = .7f, Cloudness = .8f },
        };

        [Fact]
        public void NaturalAppearanceKeepsMidnightAndRainThroughBackendResync()
        {
            var options = new ConfigFile();
            PlayerOptions.Install(options);
            options.Get<bool>("Appearance", "NaturalEquatorNight").Value = true;
            var game = Game(ArenaMapRegistry.Equator, EDateTime.PAST);
            game.InitWeather(RandomSettings);
            Assert.True(EquatorProfiles.NaturalNight);
            Assert.Equal(EquatorProfile.Day, EquatorProfiles.Appearance);
            Assert.Equal(EquatorProfile.Night, EquatorProfiles.Current);
            Assert.Equal(0, game.GameDateTime.Calculate().Hour);
            Assert.Equal(0, TimeOfDayRepairs.ActiveFixedHour);
            var controller = new WeatherController();
            controller.SetWeatherNodes(BackendNodes());
            Assert.Equal(5f, controller.Nodes[0].Rain);
            options.Get<bool>("Appearance", "NaturalEquatorNight").Value = false;
            controller.SetWeatherNodes(BackendNodes());
            Assert.True(EquatorProfiles.NaturalNight);
            Assert.Equal(5f, controller.Nodes[0].Rain);
        }

        static ArenaMapDescriptor ReleasedMap(string key) => ArenaMapRegistry.All.Single(m => m.Key == key);

        // Bay5 and AirPit share the native server-time policy (descriptor UsesServerTime).
        [Theory]
        [InlineData("arena_bay5", EDateTime.CURR, true, 0)]
        [InlineData("arena_bay5", EDateTime.PAST, true, 23)]
        [InlineData("arena_bay5", EDateTime.CURR, false, 0)]
        [InlineData("arena_bay5", EDateTime.PAST, false, 12)]
        [InlineData("arena_airpit", EDateTime.CURR, true, 0)]
        [InlineData("arena_airpit", EDateTime.PAST, true, 23)]
        [InlineData("arena_airpit", EDateTime.CURR, false, 0)]
        [InlineData("arena_airpit", EDateTime.PAST, false, 12)]
        public void ServerTimeMapsIgnoreLegacyFixedHourAndUseSelectedAdvancingBackendTime(string key, EDateTime variant, bool enabled, int legacyHour)
        {
            Plugin.Instance.Config.Get<bool>("TimeOfDay", "Enabled").Value = enabled;
            Plugin.Instance.Config.Get<int>("TimeOfDay", "ArenaFixedHour").Value = legacyHour;
            var game = Game(ReleasedMap(key), variant);
            game.InitWeather(RandomSettings);
            Assert.Equal(1, game.NativeInitializations);
            Assert.Empty(game._specialTimeResolver);
            Assert.Equal(game._backendDateTime.Calculate().AddHours(variant == EDateTime.PAST ? 12 : 0), game.GameDateTime.Calculate());
            Assert.Equal(7f, game.GameDateTime.TimeFactor);
            Assert.Same(game.GameDateTime, game.SkyClock);
            Assert.Same(game.GameDateTime, Comfort.Common.Singleton<GameWorld>.Instance.GameDateTime);
            Assert.False(game.ActualSettings.IsRandomTime);
            Assert.False(game.ActualSettings.IsRandomWeather);
            Assert.Null(TimeOfDayRepairs.ActiveFixedHour);
            var start = game.GameDateTime.Calculate(); game.GameDateTime.ElapsedSeconds += 10;
            Assert.Equal(start.AddSeconds(70), game.GameDateTime.Calculate());
            Assert.Equal(enabled, Plugin.Instance.Config.Get<bool>("TimeOfDay", "Enabled").Value);
            Assert.Equal(legacyHour, Plugin.Instance.Config.Get<int>("TimeOfDay", "ArenaFixedHour").Value);
        }

        [Theory]
        [InlineData("arena_bay5", (int)WeatherTestOverride.TestWeather.Clear)]
        [InlineData("arena_bay5", (int)WeatherTestOverride.TestWeather.Rain)]
        [InlineData("arena_bay5", (int)WeatherTestOverride.TestWeather.Fog)]
        [InlineData("arena_bay5", (int)WeatherTestOverride.TestWeather.Overcast)]
        [InlineData("arena_airpit", (int)WeatherTestOverride.TestWeather.Clear)]
        [InlineData("arena_airpit", (int)WeatherTestOverride.TestWeather.Rain)]
        [InlineData("arena_airpit", (int)WeatherTestOverride.TestWeather.Fog)]
        [InlineData("arena_airpit", (int)WeatherTestOverride.TestWeather.Overcast)]
        public void ServerTimeMapsPassThroughEveryBackendResyncDespiteSavedTestWeather(string key, int savedValue)
        {
            var saved = (WeatherTestOverride.TestWeather)savedValue;
            SetWeather(saved); var game = Game(ReleasedMap(key), EDateTime.PAST);
            game.InitWeather(RandomSettings);
            var controller = new WeatherController(); var first = BackendNodes(); var second = BackendNodes();
            controller.SetWeatherNodes(first);
            Assert.Same(first, controller.Nodes);
            controller.SetWeatherNodes(second);
            Assert.Same(second, controller.Nodes);
            Assert.Equal(2, controller.NodeCalls);
            Assert.NotEqual(second[0].Rain, second[1].Rain);
            Assert.Equal(saved, WeatherTestOverride.Mode);
        }

        [Theory]
        [InlineData(EDateTime.CURR, 12, 0f)]
        [InlineData(EDateTime.PAST, 0, 5f)]
        public void EquatorActualWeatherMatchesSelectedVariantThroughBackendResync(EDateTime variant, int hour, float rain)
        {
            SetWeather(WeatherTestOverride.TestWeather.Fog);
            var game = Game(ArenaMapRegistry.Equator, variant); game.InitWeather(RandomSettings);
            Assert.Equal(hour, TimeOfDayRepairs.ActiveFixedHour);
            Assert.Equal(hour, game.GameDateTime.Calculate().Hour);
            Assert.Equal(1, game.NativeInitializations);
            Assert.Same(game.GameDateTime, game.SkyClock);
            var controller = new WeatherController();
            for (int sync = 0; sync < 2; sync++)
            {
                var nodes = BackendNodes(); controller.SetWeatherNodes(nodes);
                Assert.NotSame(nodes, controller.Nodes);
                Assert.Equal(rain, controller.Nodes[0].Rain);
                Assert.Equal(controller.Nodes[0].Rain, controller.Nodes[1].Rain);
            }
            Assert.Equal(4, controller.NodeCalls); // one native call plus one guarded replacement per sync
        }

        [Fact]
        public void EquatorDayStaysClearAfterItsClockAdvancesAndNightDisabledKeepsSavedFallback()
        {
            SetWeather(WeatherTestOverride.TestWeather.Fog);
            var day = Game(ArenaMapRegistry.Equator, EDateTime.CURR); day.InitWeather(RandomSettings);
            day.GameDateTime.ElapsedSeconds = 7200; // sky may advance; the selected Day variant owns weather.
            var controller = new WeatherController(); controller.SetWeatherNodes(BackendNodes());
            Assert.Equal(0f, controller.Nodes[0].Rain);
            Assert.Equal(-.5f, controller.Nodes[0].Cloudness);
            EquatorNightNeon.Enabled = false;
            var night = Game(ArenaMapRegistry.Equator, EDateTime.PAST); night.InitWeather(RandomSettings);
            controller.SetWeatherNodes(BackendNodes());
            Assert.Equal(.15f, controller.Nodes[0].ScaterringFogDensity);
        }

        [Fact]
        public void EquatorToServerTimeMapsToVanillaClearsFixedStateAndNeverOverridesVanillaWeather()
        {
            var eq = Game(ArenaMapRegistry.Equator, EDateTime.PAST); eq.InitWeather(RandomSettings);
            Assert.Equal(0, TimeOfDayRepairs.ActiveFixedHour);
            var bay = Game(ArenaMapRegistry.Bay5, EDateTime.PAST); bay.InitWeather(RandomSettings);
            Assert.Null(TimeOfDayRepairs.ActiveFixedHour);
            var eq2 = Game(ArenaMapRegistry.Equator, EDateTime.CURR); eq2.InitWeather(RandomSettings);
            Assert.Equal(12, TimeOfDayRepairs.ActiveFixedHour);
            var air = Game(ArenaMapRegistry.AirPit, EDateTime.PAST); air.InitWeather(RandomSettings);
            Assert.Null(TimeOfDayRepairs.ActiveFixedHour); // server-time map: the Equator hour does not leak into it
            Assert.False(air.ActualSettings.IsRandomTime);
            SceneManager.Scenes.Clear();
            var vanilla = new LocalGame { Location = new() { Id = "bigmap" } }; vanilla.InitWeather(RandomSettings);
            Assert.True(vanilla.ActualSettings.IsRandomTime);
            Assert.True(vanilla.ActualSettings.IsRandomWeather);
            var nodes = BackendNodes(); var controller = new WeatherController(); controller.SetWeatherNodes(nodes);
            Assert.Same(nodes, controller.Nodes);
            Assert.Equal(1, controller.NodeCalls);
        }

        [Fact]
        public void OwnedFailureClearsReentryGuardAndSubsequentWeatherStillWorks()
        {
            var game = Game(ArenaMapRegistry.Equator, EDateTime.CURR); game.InitWeather(RandomSettings);
            var controller = new WeatherController { FailOnCall = 2 };
            controller.SetWeatherNodes(BackendNodes()); // replacement throws; production finally must clear.
            controller.SetWeatherNodes(BackendNodes());
            Assert.Equal(4, controller.NodeCalls);
            Assert.Equal(-.5f, controller.Nodes[0].Cloudness);
        }
    }
}

namespace ArenaMapPort.Client
{
    internal sealed class TestPluginInstance
    {
        internal readonly ConfigFile Config = new();
        internal ConfigFile Settings => Config;
    }
}
namespace BepInEx.Configuration
{
    public sealed record ConfigDefinition(string Section, string Key);
    public abstract class ConfigEntryBase
    {
        public ConfigFile ConfigFile { get; }
        public ConfigDefinition Definition { get; }
        public ConfigDescription Description { get; }
        public Type SettingType { get; }
        public object DefaultValue { get; }
        public abstract object BoxedValue { get; set; }
        protected ConfigEntryBase(ConfigFile config, ConfigDefinition definition, Type type, object defaultValue, ConfigDescription description)
        { ConfigFile = config; Definition = definition; SettingType = type; DefaultValue = defaultValue; Description = description; }
    }
    public sealed class ConfigEntry<T> : ConfigEntryBase
    {
        T _value;
        public T Value
        {
            get => _value;
            set
            {
                var clamped = Description.AcceptableValues == null ? value : (T)Description.AcceptableValues.Clamp(value);
                if (EqualityComparer<T>.Default.Equals(_value, clamped)) return;
                _value = clamped; SettingChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        public override object BoxedValue { get => Value; set => Value = (T)value; }
        public event EventHandler SettingChanged;
        public ConfigEntry(ConfigFile config, ConfigDefinition definition, T value, ConfigDescription description)
            : base(config, definition, typeof(T), value, description) { _value = value; }
    }
    public sealed class ConfigDescription
    {
        public string Description { get; }
        public AcceptableValueBase AcceptableValues { get; }
        public object[] Tags { get; }
        public ConfigDescription(string description, AcceptableValueBase range = null, params object[] tags)
        { Description = description; AcceptableValues = range; Tags = tags; }
    }
    public abstract class AcceptableValueBase { public abstract object Clamp(object value); }
    public sealed class AcceptableValueRange<T> : AcceptableValueBase where T : IComparable
    {
        readonly T _min, _max;
        public AcceptableValueRange(T min, T max) { _min = min; _max = max; }
        public override object Clamp(object value) => ((T)value).CompareTo(_min) < 0 ? _min : ((T)value).CompareTo(_max) > 0 ? _max : value;
    }
    public static class TomlTypeConverter
    {
        public static T ConvertToValue<T>(string value) => (T)ConvertToValue(value, typeof(T));
        public static object ConvertToValue(string value, Type type)
        {
            if (type.IsEnum) return Enum.Parse(type, value, true);
            if (type == typeof(UnityEngine.Color))
            {
                var bytes = Convert.FromHexString(value);
                return new UnityEngine.Color(bytes[0] / 255f, bytes[1] / 255f, bytes[2] / 255f, bytes[3] / 255f);
            }
            if (type == typeof(UnityEngine.Vector3))
            {
                using var json = System.Text.Json.JsonDocument.Parse(value);
                return new UnityEngine.Vector3(json.RootElement.GetProperty("x").GetSingle(), json.RootElement.GetProperty("y").GetSingle(), json.RootElement.GetProperty("z").GetSingle());
            }
            return Convert.ChangeType(value, type, System.Globalization.CultureInfo.InvariantCulture);
        }
    }
    public sealed class ConfigFile : IEnumerable<KeyValuePair<ConfigDefinition, ConfigEntryBase>>
    {
        readonly Dictionary<ConfigDefinition, ConfigEntryBase> _entries = new();
        public ConfigEntry<T> Bind<T>(string section, string key, T value, object description)
        {
            var definition = new ConfigDefinition(section, key);
            if (!_entries.ContainsKey(definition)) _entries[definition] = new ConfigEntry<T>(this, definition, value,
                description as ConfigDescription ?? new ConfigDescription((string)description));
            return Get<T>(section, key);
        }
        public ConfigEntry<T> Get<T>(string section, string key) => (ConfigEntry<T>)_entries[new ConfigDefinition(section, key)];
        public IEnumerator<KeyValuePair<ConfigDefinition, ConfigEntryBase>> GetEnumerator() => _entries.GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
namespace EFT
{
    public sealed class ClientGameWorld { }
    public sealed class TarkovApplication
    {
        public RaidSettings CurrentRaidSettings;
        public Action DuringLoad;
        public Task<ClientGameWorld> LoadResult = Task.FromResult(new ClientGameWorld());
        [MethodImpl(MethodImplOptions.NoInlining)]
        public Task<ClientGameWorld> LoadMapAndData()
        { DuringLoad?.Invoke(); return LoadResult; }
    }
    public struct TimeAndWeatherSettings { public bool IsRandomTime, IsRandomWeather; }
    public sealed class EftGamePlayerOwner { }
    public sealed class GameDateTime
    {
        public DateTime StatedGameDateTime = new(2026, 10, 3, 4, 18, 53);
        public float TimeFactor = 7f, ElapsedSeconds;
        public DateTime Calculate() => StatedGameDateTime.AddSeconds(ElapsedSeconds * TimeFactor);
    }
    public sealed class GameWorld { public GameDateTime GameDateTime; }
    public class BaseLocalGame<T>
    {
        public Dictionary<string, DateTime> _specialTimeResolver = new();
        public GameDateTime _backendDateTime = new();
        public EDateTime _selectedDateTime;
    }
    public sealed class LocalGame : BaseLocalGame<EftGamePlayerOwner>
    {
        public JsonType.LocationSettings.Location Location;
        public GameDateTime GameDateTime, SkyClock;
        public TimeAndWeatherSettings ActualSettings;
        public int NativeInitializations;
        [MethodImpl(MethodImplOptions.NoInlining)]
        public void InitWeather(TimeAndWeatherSettings timeAndWeather)
        {
            NativeInitializations++; ActualSettings = timeAndWeather;
            DateTime time;
            if (timeAndWeather.IsRandomTime) time = new DateTime(2016, 4, 30, 9, 33, 0);
            else if (!_specialTimeResolver.TryGetValue(Location.Id, out time))
                time = _backendDateTime.Calculate().AddHours(_selectedDateTime == EDateTime.CURR ? 0 : 12);
            GameDateTime = new GameDateTime { StatedGameDateTime = time, TimeFactor = _backendDateTime.TimeFactor };
            Comfort.Common.Singleton<GameWorld>.Instance = new GameWorld { GameDateTime = GameDateTime };
            SkyClock = GameDateTime;
        }
    }
}

namespace UnityEngine
{
    public readonly record struct Vector3(float x, float y, float z);
}
namespace EFT.Weather
{
    public sealed partial class WeatherController
    {
        public WeatherNode[] Nodes;
        public int NodeCalls, FailOnCall;
        [MethodImpl(MethodImplOptions.NoInlining)]
        public void SetWeatherNodes(WeatherNode[] nodes)
        {
            if (++NodeCalls == FailOnCall) throw new InvalidOperationException("Injected weather initialization failure");
            Nodes = nodes;
        }
    }
    public struct WeatherNode
    {
        public long Time;
        public float Cloudness, Wind, WindDirection, Rain, RainRandomness, ScaterringFogDensity,
            Temperature, AtmospherePressure, LyingWater;
        public static WeatherNode CreateDefault() => new();
    }
}
