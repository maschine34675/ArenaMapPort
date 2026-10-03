#nullable disable
// Compile the actual repair + raid gate with a small Unity facade. It models material identity,
// scene ownership, clone disposal and config callbacks, not rendering or shader execution.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using ArenaMapPort.Client;
using ArenaMapPort.Shared;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using Xunit;

namespace ArenaMapPort.Server.Tests
{
    [Collection("MapPort client lifecycle")]
    public sealed class SpecularCapTests : IDisposable
    {
        static readonly int ColorId = Shader.PropertyToID("_SpecColor");
        static readonly int StrengthId = Shader.PropertyToID("_Specularness");
        static readonly int GlossId = Shader.PropertyToID("_Glossness");
        static readonly ArenaMapDescriptor Map = ArenaMapRegistry.Equator;

        public SpecularCapTests()
        {
            ReflectiveSpecularRepairs.Clear();
            SceneManager.Scenes.Clear();
            Plugin.Log.Errors.Clear();
            foreach (var map in ArenaMapRegistry.All) WeatherRig.ReflectiveSpecularCapEntry(map).Value = 1f;
            Comfort.Common.Singleton<EFT.AbstractGame>.Instance = new EFT.AbstractGame { Status = EFT.GameStatus.Started };
            ReflectiveSpecularRepairs.Install();
        }
        public void Dispose() { ReflectiveSpecularRepairs.Clear(); SceneManager.Scenes.Clear(); }

        [Theory]
        [InlineData(1, 1, 0.3f, 0.3f)]
        [InlineData(0.5f, 0.9f, 0.3f, 2f / 3f)]
        [InlineData(0.3f, 0.27f, 0.3f, 1)] // existing indoor 0.3 * 0.3 * original 0.9 is preserved
        [InlineData(0.2f, 0.5f, 0.3f, 1)]
        [InlineData(1, 1, 0, 0)]
        [InlineData(3, 2, 1, 1)] // 1 means OFF, even for authored HDR values
        [InlineData(1, 0, 0, 1)]
        public void EffectiveCeilingIsAppliedExactlyOnce(float color, float strength, float cap, float expected) =>
            Assert.Equal(expected, SpecularCapPolicy.ColorScale(color, color / 2f, color / 4f, strength, cap), 5);

        [Theory]
        [InlineData(float.NaN, 1, 0.3f)]
        [InlineData(float.PositiveInfinity, 1, 0.3f)]
        [InlineData(1, float.NaN, 0.3f)]
        [InlineData(1, 1, float.NaN)]
        [InlineData(-1, 1, 0.3f)]
        [InlineData(1, -1, 0.3f)]
        [InlineData(1, 1, -1)]
        public void InvalidInputsAreLeftAlone(float color, float strength, float cap) =>
            Assert.Equal(1f, SpecularCapPolicy.ColorScale(color, color, color, strength, cap));

        [Theory]
        [InlineData("p0/Reflective/Bumped Specular SMap", true)]
        [InlineData("p0/Reflective/Bumped Specular SMap_Decal", true)]
        [InlineData("p0/ReflectiveExtra/Other", false)]
        [InlineData("Hidden/ArenaMapPort/p0/Reflective/Bumped Specular SMap", false)]
        [InlineData("Global Fog/Transparent Reflective Specular", false)]
        [InlineData(null, false)]
        public void FamilyFilterIncludesMeshDecalsOnly(string shader, bool expected) =>
            Assert.Equal(expected, SpecularCapPolicy.MatchesShader(shader));

        [Fact]
        public void SharedOriginalAndVanillaRendererRemainUnchangedAndOnlySpecularRgbChanges()
        {
            var original = Material();
            var owned = Renderer(original);
            var vanilla = Renderer(original);
            SceneManager.Scenes.Add(new Scene("Factory_Day", vanilla.gameObject));
            Start(0.3f, owned);
            var capped = owned.sharedMaterials[0];
            Assert.NotSame(original, capped);
            Assert.Same(original, vanilla.sharedMaterials[0]);
            Assert.Equal(1f, original.GetColor(ColorId).r);
            Assert.Equal(0.3f, capped.GetColor(ColorId).r, 5);
            Assert.Equal(0.15f, capped.GetColor(ColorId).g, 5);
            Assert.Equal(0.075f, capped.GetColor(ColorId).b, 5);
            Assert.Equal(0.7f, capped.GetColor(ColorId).a);
            Assert.Equal(1f, capped.GetFloat(StrengthId));
            Assert.Equal(0.85f, capped.GetFloat(GlossId));
            Assert.Equal(original.Properties.Count, capped.Properties.Count);
            Assert.Single(capped.Writes);
            Assert.Equal(ColorId, capped.Writes[0]);
            Assert.Empty(Plugin.Log.Errors);
        }

        [Fact]
        public void LiveOneRestoresExactInputsAndRepeatedCapChangesDoNotAccumulate()
        {
            var original = Material();
            var renderer = Renderer(original);
            Start(0.3f, renderer);
            var first = renderer.sharedMaterials[0];
            WeatherRig.ReflectiveSpecularCapEntry(Map).Value = 0.6f;
            Assert.True(first.Destroyed);
            Assert.Equal(0.6f, renderer.sharedMaterials[0].GetColor(ColorId).r, 5);
            WeatherRig.ReflectiveSpecularCapEntry(Map).Value = 0.3f;
            Assert.Equal(0.3f, renderer.sharedMaterials[0].GetColor(ColorId).r, 5);
            var last = renderer.sharedMaterials[0];
            WeatherRig.ReflectiveSpecularCapEntry(Map).Value = 1f;
            Assert.Same(original, renderer.sharedMaterials[0]);
            Assert.True(last.Destroyed);
            Assert.False(original.Destroyed);
        }

        [Fact]
        public void UncoveredPropAndDecalAreCappedWithoutAnyRoofService()
        {
            var prop = Renderer(Material());
            var decal = Renderer(Material("p0/Reflective/Bumped Specular SMap_Decal"));
            Start(0.3f, prop, decal);
            Assert.Equal(0.3f, prop.sharedMaterials[0].GetColor(ColorId).r, 5);
            Assert.Equal(0.3f, decal.sharedMaterials[0].GetColor(ColorId).r, 5);
            Assert.Contains("meshDecals=1", ReflectiveSpecularRepairs.SnapshotLine());
        }

        [Fact]
        public void ExistingIndoorReductionIsNotMultipliedAgainAndReclassificationUsesFreshInput()
        {
            var indoor = Material();
            indoor.SetColor(ColorId, new Color(0.3f, 0.15f, 0.075f, 0.7f));
            indoor.SetFloat(StrengthId, 0.27f);
            var renderer = Renderer(indoor);
            Start(0.3f, renderer);
            Assert.Same(indoor, renderer.sharedMaterials[0]);
            using (ReflectiveSpecularRepairs.Suspend(Map))
            {
                // Simulate IndoorReflectionStrength=1 using the actual material-layer handoff.
                indoor.SetColor(ColorId, new Color(1, 0.5f, 0.25f, 0.7f));
                indoor.SetFloat(StrengthId, 0.9f);
            }
            Assert.Equal(1f / 3f, renderer.sharedMaterials[0].GetColor(ColorId).r, 5);
            Assert.Equal(0.9f, renderer.sharedMaterials[0].GetFloat(StrengthId));
            var outer = renderer.sharedMaterials[0];
            using (ReflectiveSpecularRepairs.Suspend(Map))
            using (ReflectiveSpecularRepairs.Suspend(Map))
            {
                Assert.Same(indoor, renderer.sharedMaterials[0]);
                UnityEngine.Object.Destroy(indoor);
                renderer.sharedMaterials = new[] { Material() }; // classifier replaces its own input layer
            }
            Assert.True(outer.Destroyed);
            Assert.Equal(0.3f, renderer.sharedMaterials[0].GetColor(ColorId).r, 5);
        }

        [Fact]
        public void TeardownInsideSuspensionCannotResurrectOrDestroyUnderlyingMaterials()
        {
            var original = Material();
            var renderer = Renderer(original);
            Start(0.3f, renderer);
            var capped = renderer.sharedMaterials[0];
            using (ReflectiveSpecularRepairs.Suspend(Map)) ReflectiveSpecularRepairs.Clear();
            Assert.Same(original, renderer.sharedMaterials[0]);
            Assert.True(capped.Destroyed);
            Assert.False(original.Destroyed);
            Assert.Contains("no owned raid", ReflectiveSpecularRepairs.SnapshotLine());
        }

        [Fact]
        public void WrongShaderAndPropertyTypesAndPlayersAreUntouched()
        {
            var other = Renderer(Material("Standard"));
            var wrongColor = Material(colorType: ShaderPropertyType.Vector);
            var wrongScalar = Material(strengthType: ShaderPropertyType.Texture);
            var player = Renderer(Material()); player.HasPlayerParent = true;
            Start(0.3f, other, Renderer(wrongColor), Renderer(wrongScalar), player);
            Assert.Equal("Standard", other.sharedMaterials[0].shader.name);
            Assert.False(wrongColor.Destroyed);
            Assert.Equal(1f, wrongColor.GetColor(ColorId).r);
            Assert.Equal(1f, wrongScalar.GetColor(ColorId).r);
            Assert.Equal(1f, player.sharedMaterials[0].GetColor(ColorId).r);
            Assert.Contains("capped=0", ReflectiveSpecularRepairs.SnapshotLine());
        }

        [Fact]
        public void MissingScalarUsesDirectSpecularColorWithoutAddingShaderProperty()
        {
            var original = Material(includeStrength: false);
            var renderer = Renderer(original);
            Start(0.3f, renderer);
            var capped = renderer.sharedMaterials[0];
            Assert.Equal(0.3f, capped.GetColor(ColorId).r, 5);
            Assert.False(capped.Properties.ContainsKey(StrengthId));
        }

        [Fact]
        public void OtherMapConfigAndOtherMapSceneAreUnaffected()
        {
            var original = Material();
            var bay5 = Renderer(original);
            SceneManager.Scenes.Add(new Scene(ArenaMapRegistry.Bay5.AllSceneNames.Last(), bay5.gameObject));
            var equator = Renderer(original);
            Start(0.3f, equator);
            var capped = equator.sharedMaterials[0];
            WeatherRig.ReflectiveSpecularCapEntry(ArenaMapRegistry.Bay5).Value = 0f;
            Assert.Same(capped, equator.sharedMaterials[0]);
            Assert.Same(original, bay5.sharedMaterials[0]);
        }

        [Fact]
        public void UnloadRestoresAndNextRaidStartsFromUncappedMaterial()
        {
            var original = Material(); var renderer = Renderer(original);
            Start(0.3f, renderer);
            var first = renderer.sharedMaterials[0];
            SceneManager.Unload(Map.ScriptsSceneName);
            Assert.True(first.Destroyed);
            Assert.Same(original, renderer.sharedMaterials[0]);
            Start(0.3f, renderer);
            Assert.Equal(0.3f, renderer.sharedMaterials[0].GetColor(ColorId).r, 5);
            Assert.False(original.Destroyed);
        }

        [Fact]
        public void ExternalSlotReplacementSurvivesOurCleanup()
        {
            var renderer = Renderer(Material());
            Start(0.3f, renderer);
            var capped = renderer.sharedMaterials[0];
            var replacement = Material("Standard");
            renderer.sharedMaterials = new[] { replacement };
            ReflectiveSpecularRepairs.Clear();
            Assert.Same(replacement, renderer.sharedMaterials[0]);
            Assert.True(capped.Destroyed);
            Assert.False(replacement.Destroyed);
        }

        [Fact]
        public void MultipleSlotsOnlyCloneTheHighSpecularMaterialAndZeroRestoresToOne()
        {
            var original = Material();
            var unrelated = Material("Standard");
            var renderer = Renderer(original);
            renderer.sharedMaterials = new[] { unrelated, original, unrelated };
            Start(0f, renderer);
            Assert.Same(unrelated, renderer.sharedMaterials[0]);
            Assert.Same(unrelated, renderer.sharedMaterials[2]);
            Assert.Equal(0f, renderer.sharedMaterials[1].GetColor(ColorId).r);
            WeatherRig.ReflectiveSpecularCapEntry(Map).Value = 1f;
            Assert.Equal(new[] { unrelated, original, unrelated }, renderer.sharedMaterials);
        }

        [Fact]
        public void GroundSmoothnessUpdateIsVisibleThroughFreshCapWithoutChangingSpecularStrength()
        {
            var original = Material(); var renderer = Renderer(original);
            Start(0.3f, renderer);
            using (ReflectiveSpecularRepairs.Suspend(Map)) original.SetFloat(GlossId, 0.4f);
            Assert.Equal(0.4f, renderer.sharedMaterials[0].GetFloat(GlossId));
            Assert.Equal(0.3f, renderer.sharedMaterials[0].GetColor(ColorId).r, 5);
            Assert.Equal(1f, renderer.sharedMaterials[0].GetFloat(StrengthId));
        }

        [Fact]
        public void PartialAssignmentFailureRestoresEveryEarlierRenderer()
        {
            var original = Material(); var good = Renderer(original); var broken = Renderer(original);
            broken.FailNextWrite = true;
            Start(0.3f, good, broken);
            Assert.Same(original, good.sharedMaterials[0]);
            Assert.Same(original, broken.sharedMaterials[0]);
            Assert.False(original.Destroyed);
            Assert.Single(Plugin.Log.Errors);
            Assert.Contains("reverted after scan failure", Plugin.Log.Errors[0]);
        }

        [Fact]
        public void VanillaRaidDoesNotCreateCapState()
        {
            var original = Material(); var renderer = Renderer(original);
            WeatherRig.ReflectiveSpecularCapEntry(Map).Value = 0f;
            SceneManager.Load(new Scene("Factory_Day", renderer.gameObject));
            Assert.Same(original, renderer.sharedMaterials[0]);
            Assert.Contains("no owned raid", ReflectiveSpecularRepairs.SnapshotLine());
        }

        static void Start(float cap, params MeshRenderer[] renderers)
        {
            WeatherRig.ReflectiveSpecularCapEntry(Map).Value = cap;
            SceneManager.Load(new Scene(Map.ScriptsSceneName, renderers.Select(r => r.gameObject).ToArray()));
        }
        static MeshRenderer Renderer(Material material)
        {
            var root = new GameObject("arbitrary prop");
            var renderer = new MeshRenderer { gameObject = root, sharedMaterials = new[] { material } };
            root.Renderers.Add(renderer);
            return renderer;
        }
        static Material Material(string shader = "p0/Reflective/Bumped Specular SMap",
            ShaderPropertyType colorType = ShaderPropertyType.Color,
            ShaderPropertyType strengthType = ShaderPropertyType.Float, bool includeStrength = true)
        {
            var properties = new Dictionary<string, ShaderPropertyType> { ["_SpecColor"] = colorType, ["_Glossness"] = ShaderPropertyType.Float };
            if (includeStrength) properties["_Specularness"] = strengthType;
            var material = new Material(new Shader(shader, properties)) { name = "Original" };
            material.SetColor(ColorId, new Color(1, 0.5f, 0.25f, 0.7f));
            if (includeStrength) material.SetFloat(StrengthId, 1f);
            material.SetFloat(GlossId, 0.85f);
            return material;
        }
    }
}

namespace UnityEngine.Rendering { public enum ShaderPropertyType { Color, Vector, Float, Range, Texture } }
namespace UnityEngine
{
    public class Object
    {
        public bool Destroyed;
        public static readonly List<Object> Discoverable = new();
        public static T[] FindObjectsOfType<T>(bool includeInactive) => Discoverable.Where(o => !o.Destroyed).OfType<T>().ToArray();
        public static void Destroy(Object value) { if (value != null) value.Destroyed = true; }
        public static void DontDestroyOnLoad(Object value) { }
    }
    public enum HideFlags { HideAndDontSave }
    public sealed class GameObject : Object
    {
        public string name;
        public bool activeSelf = true;
        public void SetActive(bool active) => activeSelf = active;
        public HideFlags hideFlags;
        public Scene scene;
        public readonly List<MeshRenderer> Renderers = new();
        public GameObject(string name) { this.name = name; }
        public T AddComponent<T>() => (T)Activator.CreateInstance(typeof(T), true);
        public T[] GetComponentsInChildren<T>(bool includeInactive) => Renderers.OfType<T>().ToArray();
    }
    public class MonoBehaviour : Object
    {
        public static bool DeferCoroutines;
        public static readonly List<IEnumerator> PendingCoroutines = new();
        public void StartCoroutine(IEnumerator run)
        {
            if (DeferCoroutines) PendingCoroutines.Add(run);
            else while (run.MoveNext()) { }
        }
    }
    public sealed class Camera : Object { }
    public sealed class WaitForSeconds { public WaitForSeconds(float seconds) { } }
    public sealed class MeshRenderer : Object
    {
        Material[] _materials;
        public GameObject gameObject;
        public bool HasPlayerParent;
        public bool FailNextWrite;
        public Material[] sharedMaterials
        {
            get => (Material[])_materials.Clone();
            set
            {
                _materials = (Material[])value.Clone();
                if (FailNextWrite) { FailNextWrite = false; throw new InvalidOperationException("Injected renderer assignment failure"); }
            }
        }
        public T GetComponentInParent<T>() where T : new() => HasPlayerParent ? new T() : default;
    }
    public struct Color
    {
        public float r, g, b, a;
        public Color(float r, float g, float b, float a) { this.r = r; this.g = g; this.b = b; this.a = a; }
    }
    public sealed class Shader : Object
    {
        static readonly Dictionary<string, int> Ids = new();
        readonly Dictionary<string, ShaderPropertyType> _properties;
        public string name;
        public Shader(string name, Dictionary<string, ShaderPropertyType> properties) { this.name = name; _properties = properties; }
        public static int PropertyToID(string name) { if (!Ids.ContainsKey(name)) Ids[name] = Ids.Count + 1; return Ids[name]; }
        public int FindPropertyIndex(string property) => _properties.ContainsKey(property) ? PropertyToID(property) : -1;
        public ShaderPropertyType GetPropertyType(int index) => _properties.Single(p => PropertyToID(p.Key) == index).Value;
    }
    public sealed class Material : Object
    {
        public string name;
        public Shader shader;
        public readonly Dictionary<int, object> Properties;
        public readonly List<int> Writes = new();
        public Material(Shader shader) { this.shader = shader; Properties = new(); }
        public Material(Material input) { name = input.name; shader = input.shader; Properties = new(input.Properties); }
        public Color GetColor(int property) => (Color)Properties[property];
        public float GetFloat(int property) => (float)Properties[property];
        public void SetColor(int property, Color value) { Properties[property] = value; Writes.Add(property); }
        public void SetFloat(int property, float value) { Properties[property] = value; Writes.Add(property); }
    }
}
namespace UnityEngine.SceneManagement
{
    public enum LoadSceneMode { Single }
    public struct Scene
    {
        public string name;
        public bool isLoaded;
        GameObject[] _roots;
        public Scene(string name, params GameObject[] roots)
        {
            this.name = name; isLoaded = true; _roots = roots;
            foreach (var root in roots) root.scene = this;
        }
        public GameObject[] GetRootGameObjects() => _roots;
        public bool IsValid() => name != null;
    }
    public static class SceneManager
    {
        public static readonly List<Scene> Scenes = new();
        public static event Action<Scene, LoadSceneMode> sceneLoaded;
        public static event Action<Scene> sceneUnloaded;
        public static int sceneCount => Scenes.Count;
        public static Scene GetSceneAt(int index) => Scenes[index];
        public static Scene GetSceneByName(string name) => Scenes.FirstOrDefault(s => s.name == name);
        public static void Load(Scene scene) { Scenes.Add(scene); sceneLoaded?.Invoke(scene, LoadSceneMode.Single); }
        public static void Unload(string name)
        {
            var scene = GetSceneByName(name); Scenes.RemoveAll(s => s.name == name); sceneUnloaded?.Invoke(scene);
        }
    }
}
namespace EFT
{
    public sealed class Player { }
    public enum GameStatus { Starting, Started }
    public sealed class AbstractGame { public GameStatus Status; }
}
namespace Comfort.Common
{
    public static class Singleton<T> where T : class
    {
        public static T Instance;
        public static bool Instantiated => Instance != null;
    }
}
namespace ArenaMapPort.Client
{
    internal sealed class TestConfig
    {
        float _value = 1f;
        public event EventHandler SettingChanged;
        public float Value { get => _value; set { _value = value; SettingChanged?.Invoke(this, EventArgs.Empty); } }
    }
    internal static class WeatherRig
    {
        internal enum RainCoverMode { RoofDepthBlack, Off }
        internal static bool Active;
        internal static RainCoverMode CoverMode = RainCoverMode.RoofDepthBlack;
        internal static EFT.Weather.WeatherController Controller;
        internal static bool IsActiveForCurrentMap() => Active && ArenaMapRaid.IsLoaded();
        internal static RainCoverMode RainCoverForCurrentMap() => CoverMode;
        static readonly Dictionary<string, TestConfig> Configs = new();
        internal static TestConfig ReflectiveSpecularCapEntry(ArenaMapDescriptor map)
        {
            if (!Configs.ContainsKey(map.Key)) Configs[map.Key] = new TestConfig();
            return Configs[map.Key];
        }
    }
    internal static class Plugin
    {
        internal static readonly TestLog Log = new();
        internal sealed class TestLog
        {
            internal readonly List<string> Errors = new();
            internal readonly List<string> Warnings = new();
            internal void LogInfo(string message) { }
            internal void LogWarning(string message) => Warnings.Add(message);
            internal void LogError(string message) => Errors.Add(message);
        }
    }
}
