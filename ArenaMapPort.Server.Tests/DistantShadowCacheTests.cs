#nullable disable
using ArenaMapPort.Client;
using ArenaMapPort.Shared;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using Xunit;

namespace ArenaMapPort.Server.Tests
{
    [Collection("MapPort client lifecycle")]
    public sealed class DistantShadowCacheTests : IDisposable
    {
        static readonly ArenaMapDescriptor Map = ArenaMapRegistry.Bay5;

        public DistantShadowCacheTests()
        {
            ReflectiveSpecularRepairs.Clear();
            SceneManager.Scenes.Clear();
            UnityEngine.Object.Discoverable.Clear();
            MonoBehaviour.DeferCoroutines = false;
            MonoBehaviour.PendingCoroutines.Clear();
            Plugin.Log.Warnings.Clear();
            foreach (var map in ArenaMapRegistry.All) WeatherRig.ReflectiveSpecularCapEntry(map).Value = 1f;
            Comfort.Common.Singleton<EFT.AbstractGame>.Instance = new EFT.AbstractGame { Status = EFT.GameStatus.Started };
            ReflectiveSpecularRepairs.Install();
        }

        public void Dispose()
        {
            ReflectiveSpecularRepairs.Clear();
            SceneManager.Scenes.Clear();
            UnityEngine.Object.Discoverable.Clear();
        }

        [Fact]
        public void DirtyFlagAloneStillDrawsRetiredMaterialWhileCycleIsPending()
        {
            var renderer = Renderer();
            Load(renderer);
            var shadow = Shadow(renderer);
            var retired = renderer.sharedMaterials[0];
            renderer.sharedMaterials = new[] { Material() };
            UnityEngine.Object.Destroy(retired);
            shadow.InvalidateRenderersOnNextUpdate = true;

            // Negative control reproduces the verified native scheduling rule: the flag is
            // ignored while either old counter is nonzero, so the cached material is still used.
            Assert.Throws<ArgumentNullException>(() => shadow.SimulateNativeUpdate());
            Assert.Equal(0, shadow.Rebuilds);
            Assert.NotEqual(0, shadow.ImpostorsRemaining);
        }

        [Fact]
        public void RestartClearsPendingCommandsAndRebuildsFromCurrentRenderer()
        {
            var renderer = Renderer();
            Load(renderer);
            var shadow = Shadow(renderer);
            var retired = renderer.sharedMaterials[0];
            var replacement = Material();
            renderer.sharedMaterials = new[] { replacement };
            UnityEngine.Object.Destroy(retired);

            DistantShadowCache.Invalidate(Map, "test replacement");

            Assert.Equal(0, shadow.ObjectsRemaining);
            Assert.Equal(0, shadow.ImpostorsRemaining);
            Assert.True(shadow.InvalidateRenderersOnNextUpdate);
            Assert.Empty(shadow.Commands.Submitted);
            Assert.True(shadow.Commands.Clears > 0);
            shadow.SimulateNativeUpdate();
            Assert.Equal(1, shadow.Rebuilds);
            Assert.Same(replacement, Assert.Single(shadow.Commands.Submitted));
            Assert.Empty(Plugin.Log.Warnings);
        }

        [Fact]
        public void ActualLiveCapAndNestedIndoorHandoffRetireTheNativeSnapshot()
        {
            WeatherRig.ReflectiveSpecularCapEntry(Map).Value = 0.3f;
            var renderer = Renderer();
            var original = renderer.sharedMaterials[0];
            Load(renderer);
            var firstClone = renderer.sharedMaterials[0];
            var shadow = Shadow(renderer);

            WeatherRig.ReflectiveSpecularCapEntry(Map).Value = 1f;

            Assert.True(firstClone.Destroyed);
            Assert.Same(original, renderer.sharedMaterials[0]);
            Assert.Equal(0, shadow.ObjectsRemaining);
            Assert.Equal(0, shadow.ImpostorsRemaining);
            shadow.SimulateNativeUpdate();
            Assert.Same(original, Assert.Single(shadow.Commands.Submitted));

            WeatherRig.ReflectiveSpecularCapEntry(Map).Value = 0.3f;
            shadow.SimulateNativeUpdate();
            var secondClone = renderer.sharedMaterials[0];
            using (ReflectiveSpecularRepairs.Suspend(Map))
            using (ReflectiveSpecularRepairs.Suspend(Map))
            {
                Assert.True(secondClone.Destroyed);
                Assert.Equal(0, shadow.ObjectsRemaining);
                Assert.Equal(0, shadow.ImpostorsRemaining);
                renderer.sharedMaterials = new[] { Material() };
            }
            shadow.SimulateNativeUpdate();
            Assert.Same(renderer.sharedMaterials[0], shadow.Commands.Submitted.Last());
            Assert.False(original.Destroyed);
            Assert.Empty(Plugin.Log.Warnings);
        }

        [Fact]
        public void WrongRaidAndPrefabOrUnloadedInstancesRemainUntouched()
        {
            var renderer = Renderer();
            Load(renderer);
            var valid = Shadow(renderer);
            var prefab = Shadow(renderer); prefab.gameObject.scene = default;
            var unloaded = Shadow(renderer); unloaded.gameObject.scene = new Scene("unloaded") { isLoaded = false };
            var noCamera = Shadow(renderer); noCamera.MasterCamera = null;

            DistantShadowCache.Invalidate(ArenaMapRegistry.Equator, "wrong map");
            Assert.Equal(1, valid.ObjectsRemaining);
            DistantShadowCache.Invalidate(Map, "owned mutation");
            Assert.Equal(0, valid.ObjectsRemaining);
            foreach (var excluded in new[] { prefab, unloaded, noCamera })
            {
                Assert.Equal(1, excluded.ObjectsRemaining);
                Assert.Equal(7, excluded.ImpostorsRemaining);
                Assert.False(excluded.InvalidateRenderersOnNextUpdate);
                Assert.Equal(0, excluded.Commands.Clears);
            }

            SceneManager.Unload(Map.ScriptsSceneName);
            valid.Prime(renderer);
            DistantShadowCache.Invalidate(Map, "outside owned raid");
            Assert.Equal(1, valid.ObjectsRemaining);
            Assert.False(valid.InvalidateRenderersOnNextUpdate);
        }

        static void Load(MeshRenderer renderer) => SceneManager.Load(new Scene(Map.ScriptsSceneName, renderer.gameObject));
        static DistantShadow Shadow(MeshRenderer renderer)
        {
            var shadow = new DistantShadow
            {
                gameObject = new GameObject("shadow camera") { scene = renderer.gameObject.scene },
                MasterCamera = new Camera()
            };
            shadow.Prime(renderer);
            UnityEngine.Object.Discoverable.Add(shadow);
            return shadow;
        }
        static MeshRenderer Renderer()
        {
            var go = new GameObject("owned prop");
            var renderer = new MeshRenderer { gameObject = go, sharedMaterials = new[] { Material() } };
            go.Renderers.Add(renderer);
            return renderer;
        }
        static Material Material()
        {
            var material = new Material(new Shader("p0/Reflective/Bumped Specular SMap",
                new Dictionary<string, ShaderPropertyType>
                { ["_SpecColor"] = ShaderPropertyType.Color, ["_Specularness"] = ShaderPropertyType.Float }));
            material.SetColor(Shader.PropertyToID("_SpecColor"), new Color(1, 1, 1, 1));
            material.SetFloat(Shader.PropertyToID("_Specularness"), 1);
            return material;
        }
    }
}

// Native-boundary facade: only the independently decompiled snapshot/counter scheduling rule is
// modelled. It deliberately retains cached material references after Renderer changes. Production
// DistantShadowCache is linked unmodified; GPU rendering and native object lifetime need host tests.
public sealed class DistantShadow : UnityEngine.Object
{
    int _objectsToRenderCount;
    int _impostorsToRenderCount;
    readonly CommandBuffer _renderObjectsCmdBuf = new();
    Material[] _renderList;
    MeshRenderer _renderer;
    public GameObject gameObject;
    public Camera MasterCamera;
    public bool InvalidateRenderersOnNextUpdate;
    public int ObjectsRemaining => _objectsToRenderCount;
    public int ImpostorsRemaining => _impostorsToRenderCount;
    public CommandBuffer Commands => _renderObjectsCmdBuf;
    public int Rebuilds;

    public void Prime(MeshRenderer renderer)
    {
        _renderer = renderer;
        _renderList = renderer.sharedMaterials;
        _objectsToRenderCount = _renderList.Length;
        _impostorsToRenderCount = 7;
        InvalidateRenderersOnNextUpdate = false;
        foreach (var material in _renderList) _renderObjectsCmdBuf.DrawMesh(material);
    }
    public void SimulateNativeUpdate()
    {
        if (_objectsToRenderCount == 0 && _impostorsToRenderCount == 0)
        {
            if (InvalidateRenderersOnNextUpdate)
            {
                _renderList = _renderer.sharedMaterials;
                InvalidateRenderersOnNextUpdate = false;
                Rebuilds++;
            }
            _objectsToRenderCount = _renderList.Length;
        }
        _renderObjectsCmdBuf.Clear();
        while (_objectsToRenderCount > 0)
            _renderObjectsCmdBuf.DrawMesh(_renderList[--_objectsToRenderCount]);
        _impostorsToRenderCount = 0;
    }
}

namespace UnityEngine.Rendering
{
    public sealed class CommandBuffer
    {
        public readonly List<Material> Submitted = new();
        public int Clears;
        public void Clear() { Clears++; Submitted.Clear(); }
        public void DrawMesh(Material material)
        {
            if (material == null || material.Destroyed) throw new ArgumentNullException("material");
            Submitted.Add(material);
        }
    }
}
