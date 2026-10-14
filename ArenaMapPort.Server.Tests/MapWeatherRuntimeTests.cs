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
            Plugin.Log.Errors.Clear();
            Plugin.Instance.Config.Get<bool>("TimeOfDay", "Enabled").Value = true;
            Plugin.Instance.Config.Get<int>("TimeOfDay", "ArenaFixedHour").Value = 0;
            SetWeather(WeatherTestOverride.TestWeather.Clear);
            // The production target (HarmonyPatches.InstallTimeOfDayFix): the closed generic's own method. The
            // detour is shared by every reference-type instantiation, so the HideoutGame double below runs it too.
            _harmony.Patch(AccessTools.DeclaredMethod(typeof(BaseLocalGame<EftGamePlayerOwner>), nameof(LocalGame.InitWeather),
                    new[] { typeof(TimeAndWeatherSettings) }),
                prefix: new HarmonyMethod(typeof(TimeOfDayRepairs), nameof(TimeOfDayRepairs.Prefix)));
            _harmony.Patch(AccessTools.Method(typeof(WeatherController), nameof(WeatherController.SetWeatherNodes)),
                postfix: new HarmonyMethod(typeof(WeatherTestOverride), nameof(WeatherTestOverride.Postfix)));
        }
        public void Dispose()
        {
            PlayerOptions.Install(new ConfigFile());
            SetApplyOnServerTime(false); SetDeveloperMode(false);
            _harmony.UnpatchSelf(); SceneManager.Scenes.Clear();
            Comfort.Common.Singleton<GameWorld>.Instance = null;
        }
        static void SetWeather(WeatherTestOverride.TestWeather weather) =>
            Plugin.Instance.Config.Get<WeatherTestOverride.TestWeather>("Weather (test)", "Weather").Value = weather;
        static void SetApplyOnServerTime(bool value) =>
            Plugin.Instance.Config.Get<bool>("Weather (test)", "ApplyOnServerTimeMaps").Value = value;
        // DeveloperOptions.Enabled is latched at Install time (ReleaseConfigurationTests), so each test
        // re-installs it on a fresh ConfigFile carrying the wanted value.
        static void SetDeveloperMode(bool enabled)
        {
            var cfg = new ConfigFile();
            cfg.Bind("General", "DeveloperMode", false, "test").Value = enabled;
            DeveloperOptions.Install(cfg);
        }
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

        // Regression 2026-10-07..14: the prefix read _selectedDateTime (a BaseLocalGame<EftGamePlayerOwner>
        // field) off the HideoutGame before its map gate, threw ArgumentException out of InitWeather and
        // every hideout visit stayed on its loading screen. The hideout double runs InitWeather through the
        // detour on the closed EftGamePlayerOwner type exactly like the game (shared generic body); the
        // production FieldInfos belong to that closed type, so GetValue on the double throws as the game's
        // did (guard assertion). The prefix must leave the hideout, its settings, the Arena state AND the
        // in-flight Equator selection alone, and nothing may escape. Against the old prefix this test fails
        // with the game's own message (review 2026-10-14 reproduced it on CoreCLR).
        [Fact]
        public void HideoutGameRunsTheSharedDetourUntouched()
        {
            Assert.Throws<ArgumentException>(() => TimeOfDayRepairs.ResolverField.GetValue(new HideoutGame()));
            // a vanilla InitWeather first: it clears the static fixed hour a previous test may have left behind
            new LocalGame { Location = new() { Id = "bigmap" } }.InitWeather(RandomSettings);
            Assert.Null(TimeOfDayRepairs.ActiveFixedHour);
            EquatorProfiles.BeginLoad(ArenaMapRegistry.Equator.Key, EDateTime.PAST); // a raid selection already made
            var hideout = new HideoutGame { Location = new() { Id = ArenaMapRegistry.Equator.Key }, _selectedDateTime = EDateTime.PAST };
            hideout.InitWeather(RandomSettings);
            Assert.Equal(1, hideout.NativeInitializations);
            Assert.True(hideout.ActualSettings.IsRandomTime);           // a mis-gated Apply would have flipped these
            Assert.True(hideout.ActualSettings.IsRandomWeather);
            Assert.Empty(hideout._specialTimeResolver);
            Assert.Null(TimeOfDayRepairs.ActiveFixedHour);
            Assert.Equal(EquatorProfile.Night, EquatorProfiles.Current); // the selection survives the hideout
            Assert.Empty(Plugin.Log.Errors);
            var settings = RandomSettings;
            TimeOfDayRepairs.Prefix(null, ref settings);                 // an unknown owner is just as inert
            Assert.True(settings.IsRandomTime);
            Assert.Empty(Plugin.Log.Errors);
        }

        // Fika's CoopGame : BaseLocalGame<EftGamePlayerOwner> is the other real subclass of the closed type;
        // the gate must accept every such subclass, not only LocalGame.
        [Fact]
        public void AnotherSubclassOfTheClosedTypeGetsTheFullRepair()
        {
            EquatorProfiles.BeginLoad(ArenaMapRegistry.Equator.Key, EDateTime.PAST);
            SceneManager.Scenes.Clear(); SceneManager.Load(new Scene(ArenaMapRegistry.Equator.ScriptsSceneName));
            var coop = new CoopGame { Location = new() { Id = ArenaMapRegistry.Equator.Key }, _selectedDateTime = EDateTime.PAST };
            coop.InitWeather(RandomSettings);
            Assert.Equal(1, coop.NativeInitializations);
            Assert.False(coop.ActualSettings.IsRandomTime);
            Assert.Equal(0, coop.GameDateTime.Calculate().Hour);
            Assert.Equal(0, TimeOfDayRepairs.ActiveFixedHour);
            Assert.Equal(EquatorProfile.Night, EquatorProfiles.Current);
            Assert.Empty(Plugin.Log.Errors);
        }

        // A vanilla map through the closed type keeps InitWeather's own branches (no resolver seed, random time
        // kept) and, as since 04be98c, clears a stale Equator profile at its InitWeather.
        [Fact]
        public void VanillaLocalGameClearsAStaleEquatorProfileAndKeepsRandomTime()
        {
            EquatorProfiles.BeginLoad(ArenaMapRegistry.Equator.Key, EDateTime.PAST);
            var game = new LocalGame { Location = new() { Id = "factory4_day" }, _selectedDateTime = EDateTime.PAST };
            game.InitWeather(RandomSettings);
            Assert.Equal(1, game.NativeInitializations);
            Assert.True(game.ActualSettings.IsRandomTime);
            Assert.Empty(game._specialTimeResolver);
            Assert.Null(TimeOfDayRepairs.ActiveFixedHour);
            Assert.Equal(EquatorProfile.None, EquatorProfiles.Current);
            Assert.Empty(Plugin.Log.Errors);
        }

        // A repair failure must degrade to vanilla: one Error line, and InitWeather sees the settings it was
        // called with even though Apply had already written the ref struct before it threw.
        [Fact]
        public void APrefixFailureLogsOnceAndHandsInitWeatherTheOriginalSettings()
        {
            var game = Game(ArenaMapRegistry.Equator, EDateTime.CURR);
            game._backendDateTime.ElapsedSeconds = 1f; game._backendDateTime.TimeFactor = float.PositiveInfinity; // Calculate() throws
            game.InitWeather(RandomSettings);
            Assert.Equal(1, game.NativeInitializations);
            Assert.True(game.ActualSettings.IsRandomTime);
            Assert.True(game.ActualSettings.IsRandomWeather);
            Assert.Empty(game._specialTimeResolver);
            Assert.Null(TimeOfDayRepairs.ActiveFixedHour);
            var error = Assert.Single(Plugin.Log.Errors);
            Assert.Contains("InitWeather prefix failed for LocalGame", error);
        }

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

        // 2026-10-12: the developer-only opt-in forces the test preset on server-time maps (rain checks on
        // Bay5/AirPit). Three gates: the key, developer mode latched at startup, and Weather != Server.
        [Theory]
        [InlineData("arena_bay5", true, true, (int)WeatherTestOverride.TestWeather.Rain, true)]
        [InlineData("arena_airpit", true, true, (int)WeatherTestOverride.TestWeather.Rain, true)]
        [InlineData("arena_airpit", true, true, (int)WeatherTestOverride.TestWeather.Fog, true)]
        [InlineData("arena_airpit", true, false, (int)WeatherTestOverride.TestWeather.Rain, false)]
        [InlineData("arena_airpit", false, true, (int)WeatherTestOverride.TestWeather.Rain, false)]
        [InlineData("arena_airpit", true, true, (int)WeatherTestOverride.TestWeather.Server, false)]
        public void ApplyOnServerTimeMapsForcesTheTestWeatherOnlyInDeveloperMode(string key, bool applyKey, bool developerMode, int savedValue, bool expectForced)
        {
            var saved = (WeatherTestOverride.TestWeather)savedValue;
            SetWeather(saved); SetApplyOnServerTime(applyKey); SetDeveloperMode(developerMode);
            var map = ReleasedMap(key);
            Assert.Equal(expectForced ? saved : WeatherTestOverride.TestWeather.Server, WeatherTestOverride.ModeForSelection(map, null));
            var game = Game(map, EDateTime.PAST);
            game.InitWeather(RandomSettings);
            Assert.Null(TimeOfDayRepairs.ActiveFixedHour);
            var controller = new WeatherController(); var first = BackendNodes(); var second = BackendNodes();
            controller.SetWeatherNodes(first);
            controller.SetWeatherNodes(second);
            if (expectForced)
            {
                Assert.NotSame(second, controller.Nodes);
                Assert.Equal(2, controller.Nodes.Length);
                Assert.Equal(controller.Nodes[0].Rain, controller.Nodes[1].Rain);
                Assert.Equal(saved == WeatherTestOverride.TestWeather.Rain ? 5f : 0f, controller.Nodes[0].Rain);
            }
            else
            {
                Assert.Same(second, controller.Nodes);
                Assert.Equal(2, controller.NodeCalls);
            }
            // Equator is unaffected by the opt-in: day stays Clear whatever the key says.
            Assert.Equal(WeatherTestOverride.TestWeather.Clear,
                WeatherTestOverride.ModeForSelection(ArenaMapRegistry.Equator, ArenaMapRegistry.Equator.FixedDayHour));
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
    // The shape of EFT/BaseLocalGame.cs: fields, Location (:360) and InitWeather (:489) live on the generic
    // base; LocalGame (EftGamePlayerOwner), Fika's CoopGame (EftGamePlayerOwner) and HideoutGame
    // (HideoutPlayerOwner, EFT/HideoutGame.cs:20) only close it. Harmony's detour on the closed
    // BaseLocalGame<EftGamePlayerOwner>.InitWeather is shared by every reference-type instantiation
    // (Mono in the game, CoreCLR here), so the HideoutGame double runs the production prefix too.
    public class BaseLocalGame<T>
    {
        public Dictionary<string, DateTime> _specialTimeResolver = new();
        public GameDateTime _backendDateTime = new();
        public EDateTime _selectedDateTime;
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
    public sealed class HideoutPlayerOwner { }
    public sealed class HideoutGame : BaseLocalGame<HideoutPlayerOwner> { }
    public sealed class LocalGame : BaseLocalGame<EftGamePlayerOwner> { }
    public sealed class CoopGame : BaseLocalGame<EftGamePlayerOwner> { }
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
