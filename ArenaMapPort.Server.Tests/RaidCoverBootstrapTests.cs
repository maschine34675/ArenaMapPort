using System.Collections;
using ArenaMapPort.Client;
using ArenaMapPort.Shared;
using Comfort.Common;
using EFT;
using EFT.CameraControl;
using EFT.Weather;
using UnityEngine;
using UnityEngine.SceneManagement;
using Xunit;

namespace ArenaMapPort.Server.Tests
{
    [Collection("MapPort client lifecycle")]
    public sealed class RaidCoverBootstrapTests : IDisposable
    {
        static readonly ArenaMapDescriptor Map = ArenaMapRegistry.Equator;
        readonly DepthPhotograper _ownedPhoto = new();

        public RaidCoverBootstrapTests()
        {
            RaidCoverBootstrap.Clear();
            ReflectiveSpecularRepairs.Clear();
            SceneManager.Scenes.Clear();
            MonoBehaviour.PendingCoroutines.Clear();
            MonoBehaviour.DeferCoroutines = true;
            Plugin.Log.Errors.Clear();
            IndoorFloorSmoothnessRepairs.Reset();
            CameraManager.instance = null;
            Singleton<AbstractGame>.Instance = new AbstractGame { Status = GameStatus.Started };
            WeatherRig.Active = true;
            WeatherRig.CoverMode = WeatherRig.RainCoverMode.RoofDepthBlack;
            WeatherRig.Controller = new WeatherController { RainController = new RainController { _depthPhotograper = _ownedPhoto } };
            DepthPhotograper.Instance = new DepthPhotograper(); // unrelated last-used hideout photo
            RaidCoverBootstrap.Install();
        }

        public void Dispose()
        {
            RaidCoverBootstrap.Clear();
            ReflectiveSpecularRepairs.Clear();
            IndoorFloorSmoothnessRepairs.Reset();
            WeatherRig.Active = false;
            CameraManager.instance = null;
            SceneManager.Scenes.Clear();
            MonoBehaviour.PendingCoroutines.Clear();
            MonoBehaviour.DeferCoroutines = false;
        }

        [Fact]
        public void ScriptsLoadWithNoManagerWaitsThenRendersAndClassifiesExactlyOnce()
        {
            Load(); Tick(5);
            Assert.False(CameraManager.Exist);
            Assert.Equal(0, _ownedPhoto.Renders);
            CameraReady(); Tick(8);
            Assert.Equal(1, _ownedPhoto.Renders);
            Assert.Equal(1, IndoorFloorSmoothnessRepairs.Classifications);
            Assert.Equal(1, WeatherRig.Controller.CameraRefreshes);
            Assert.Equal(0, DepthPhotograper.Instance!.Renders);
            Assert.Equal(2, CameraManager.Instance.SubscriberCount);
            Assert.Empty(Plugin.Log.Errors);
            Tick(20);
            Assert.Equal(1, _ownedPhoto.Renders);
        }

        [Fact]
        public void ManagerExistsButHasNoCameraDoesNotPrepareEarly()
        {
            CameraManager.instance = new CameraManager();
            Load(); Tick(6);
            Assert.Equal(0, IndoorFloorSmoothnessRepairs.Attempts);
            CameraManager.Instance.Camera = new Camera(); Tick(8);
            Assert.Equal(1, _ownedPhoto.Renders);
        }

        [Fact]
        public void NaturalCameraEventBeforeBootstrapDoesNotInitializeRainTwice()
        {
            Load(); CameraReady();
            var controller = WeatherRig.Controller;
            IndoorFloorSmoothnessRepairs.Owner.Bind(CameraManager.Instance, controller.OnCameraChanged, controller.RainController!.CameraChanged);
            CameraManager.Instance.Raise();
            Assert.Equal(1, _ownedPhoto.Renders);
            Tick(8);
            Assert.Equal(2, _ownedPhoto.Renders); // natural early photo, then one complete post-start photo
            Assert.Equal(1, controller.RainController.Initializations);
            Assert.Equal(1, IndoorFloorSmoothnessRepairs.Classifications);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void LiveCameraStillWaitsUntilTheRaidHasStarted(bool gameExists)
        {
            CameraReady();
            Singleton<AbstractGame>.Instance = gameExists ? new AbstractGame { Status = GameStatus.Starting } : null!;
            Load(); Tick(6);
            Assert.Equal(0, _ownedPhoto.Renders);
            Singleton<AbstractGame>.Instance = new AbstractGame { Status = GameStatus.Started };
            Tick(8);
            Assert.Equal(1, _ownedPhoto.Renders);
        }

        [Fact]
        public void ReplacementDuringReadinessMarginUsesNewManagerAndDonorPhotoEvenWithNoGlobalInstance()
        {
            CameraReady(); var old = CameraManager.Instance;
            Load(); Tick(2); // ready observed; the production 3-second margin has yielded
            old.Dispose();
            DepthPhotograper.Instance = null; // hideout teardown cleared its static photographer
            Tick(2);
            Assert.Equal(0, _ownedPhoto.Renders);
            CameraReady(); var current = CameraManager.Instance; Tick(8);
            Assert.Equal(1, _ownedPhoto.Renders);
            Assert.Same(current.Camera, WeatherRig.Controller.RainController!.BoundCamera);
            Assert.Equal(0, old.SubscriberCount);
            Assert.Equal(2, current.SubscriberCount);
        }

        [Fact]
        public void UnloadCancelsPendingPassAndNextSameMapRaidCannotRunTheOldPass()
        {
            Load(); Tick(3);
            SceneManager.Unload(Map.ScriptsSceneName);
            Load(); CameraReady(); Tick(10);
            Assert.Equal(1, _ownedPhoto.Renders);
            Assert.Equal(1, IndoorFloorSmoothnessRepairs.Classifications);
        }

        [Fact]
        public void TwoRaidsWithInterveningHideoutManagerRenderOnceEach()
        {
            Load(); CameraReady(); Tick(8);
            var first = CameraManager.Instance;
            SceneManager.Unload(Map.ScriptsSceneName);
            IndoorFloorSmoothnessRepairs.Owner.Unbind(); // WeatherRig's actual unload responsibility
            first.Dispose();
            CameraReady(); // transient hideout
            DepthPhotograper.Instance = new DepthPhotograper();
            CameraManager.Instance.Dispose();
            DepthPhotograper.Instance = null;
            Load(); Tick(4);
            Assert.Equal(1, _ownedPhoto.Renders);
            CameraReady(); Tick(8);
            Assert.Equal(2, _ownedPhoto.Renders);
            Assert.Equal(2, IndoorFloorSmoothnessRepairs.Classifications);
            Assert.Equal(0, first.SubscriberCount);
            Assert.Equal(2, CameraManager.Instance.SubscriberCount);
        }

        [Fact]
        public void UnavailablePreparationIsRetriedButNeverReportedAsACompletedPass()
        {
            IndoorFloorSmoothnessRepairs.FailAttempts = 2;
            Load(); CameraReady(); Tick(12);
            Assert.Equal(3, IndoorFloorSmoothnessRepairs.Attempts);
            Assert.Equal(1, _ownedPhoto.Renders);
            Assert.Equal(1, IndoorFloorSmoothnessRepairs.Classifications);
        }

        [Fact]
        public void VanillaAndDisabledDonorDoNotSchedulePreparation()
        {
            CameraReady();
            SceneManager.Load(new Scene("Hideout")); Tick(4);
            Assert.Equal(0, _ownedPhoto.Renders);
            WeatherRig.Active = false;
            Load(); Tick(8);
            Assert.Equal(0, IndoorFloorSmoothnessRepairs.Attempts);
        }

        static void Load() => SceneManager.Load(new Scene(Map.ScriptsSceneName));
        static void CameraReady() => CameraManager.instance = new CameraManager { Camera = new Camera() };
        static void Tick(int count)
        {
            for (int tick = 0; tick < count; tick++)
            foreach (IEnumerator run in MonoBehaviour.PendingCoroutines.ToArray())
                if (!run.MoveNext()) MonoBehaviour.PendingCoroutines.Remove(run);
        }
    }
}

namespace ArenaMapPort.Client
{
    // Boundary facade: the production bootstrap must reach this only after scene/raid/camera readiness.
    // It calls the real production subscription refresh, whose native Rain callback renders its own
    // serialized photographer. Classification arithmetic/rendering is separately tested on the host.
    internal static class IndoorFloorSmoothnessRepairs
    {
        internal static readonly DonorCameraSubscriptions Owner = new();
        internal static int Attempts, FailAttempts, Classifications;
        internal static bool InitializeForCurrentRaid(ArenaMapDescriptor map)
        {
            Attempts++;
            if (FailAttempts-- > 0) return false;
            bool prepared = Owner.Refresh(CameraManager.Instance, WeatherRig.Controller);
            if (prepared) Classifications++;
            return prepared;
        }
        internal static void Reset() { Owner.Unbind(); Attempts = FailAttempts = Classifications = 0; }
    }
}
