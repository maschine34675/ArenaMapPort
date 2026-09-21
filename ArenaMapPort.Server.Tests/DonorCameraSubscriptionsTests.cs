using ArenaMapPort.Client;
using EFT.CameraControl;
using EFT.Weather;
using UnityEngine;
using Xunit;

namespace ArenaMapPort.Server.Tests
{
    [Collection("MapPort client lifecycle")]
    public sealed class DonorCameraSubscriptionsTests : IDisposable
    {
        public DonorCameraSubscriptionsTests() => CameraManager.instance = null;
        public void Dispose() => CameraManager.instance = null;

        [Fact]
        public void FirstActivationReplacesAwakeCallbacksWithoutDoublingThem()
        {
            var manager = new CameraManager();
            var owner = new DonorCameraSubscriptions();
            int weather = 0, rain = 0, unrelated = 0;
            Action onWeather = () => weather++, onRain = () => rain++;
            manager.OnCameraChanged += onWeather; // WeatherController.Awake
            manager.OnCameraChanged += onRain;    // RainController.Awake
            manager.OnCameraChanged += () => unrelated++; // another component must remain subscribed
            owner.Bind(manager, onWeather, onRain);
            manager.Raise();
            Assert.Equal(3, manager.SubscriberCount);
            Assert.Equal(1, weather);
            Assert.Equal(1, rain);
            Assert.Equal(1, unrelated);
            owner.Unbind();
            manager.Raise();
            Assert.Equal(2, unrelated);
            Assert.Equal(1, manager.SubscriberCount);
        }

        [Fact]
        public void RepeatedReestablishmentKeepsExactlyOneWeatherAndRainCallback()
        {
            var manager = new CameraManager(); var owner = new DonorCameraSubscriptions();
            int weather = 0, rain = 0;
            Action onWeather = () => weather++, onRain = () => rain++;
            for (int i = 0; i < 5; i++) owner.Bind(manager, onWeather, onRain);
            manager.Raise();
            Assert.Equal(2, manager.SubscriberCount);
            Assert.Equal(1, weather);
            Assert.Equal(1, rain);
        }

        [Fact]
        public void DisposedFirstRaidManagerIsReplacedAndRainTriggersOnSecondRaid()
        {
            var first = new CameraManager(); var second = new CameraManager();
            var owner = new DonorCameraSubscriptions(); int photos = 0;
            Action onWeather = () => { }, onRain = () => photos++;
            owner.Bind(first, onWeather, onRain);
            first.Raise();
            first.Dispose(); // actual CameraManager.Dispose clears its event, then drops singleton
            owner.Bind(second, onWeather, onRain); // reused components do not run Awake again
            second.Raise();
            Assert.Equal(2, photos);
            Assert.Equal(0, first.SubscriberCount);
            Assert.Equal(2, second.SubscriberCount);
        }

        [Fact]
        public void ReplacementAlsoDetachesTheOldManagerWhenItWasNotDisposed()
        {
            var first = new CameraManager(); var second = new CameraManager();
            var owner = new DonorCameraSubscriptions(); int callbacks = 0;
            Action weather = () => callbacks++, rain = () => callbacks++;
            owner.Bind(first, weather, rain);
            owner.Bind(second, weather, rain);
            first.Raise();
            Assert.Equal(0, callbacks);
            Assert.Equal(0, first.SubscriberCount);
            second.Raise();
            Assert.Equal(2, callbacks);
        }

        [Fact]
        public void TeardownDetachesCapturedManagerAndIsIdempotent()
        {
            var manager = new CameraManager(); var owner = new DonorCameraSubscriptions();
            int calls = 0;
            owner.Bind(manager, () => calls++, () => calls++);
            owner.Unbind();
            owner.Unbind();
            manager.Raise();
            Assert.Equal(0, calls);
            Assert.Equal(0, manager.SubscriberCount);
        }

        [Fact]
        public void MissingRainComponentKeepsWeatherCallbackAndStillDetaches()
        {
            var manager = new CameraManager(); var owner = new DonorCameraSubscriptions();
            int calls = 0;
            owner.Bind(manager, () => calls++, null);
            manager.Raise();
            Assert.Equal(1, calls);
            Assert.Equal(1, manager.SubscriberCount);
            owner.Unbind();
            Assert.Equal(0, manager.SubscriberCount);
        }

        [Fact]
        public void NaturalCallbackThenRepeatedBindAndRefreshOnlyRendersThePhoto()
        {
            var (owner, controller, manager) = Ready();
            owner.Bind(manager, controller.OnCameraChanged, controller.RainController!.CameraChanged);
            manager.Raise();
            owner.Bind(manager, controller.OnCameraChanged, controller.RainController.CameraChanged);
            Assert.True(owner.Refresh(manager, controller));
            Assert.Equal(1, controller.RainController.Initializations);
            Assert.Equal(2, controller.RainController._depthPhotograper!.Renders);
            Assert.Equal(2, manager.SubscriberCount);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void ReplacementCameraOrManagerRequiresNativeInitialization(bool replaceManager)
        {
            var (owner, controller, first) = Ready();
            Assert.True(owner.Refresh(first, controller));
            var current = replaceManager ? new CameraManager { Camera = first.Camera } : first;
            if (!replaceManager) current.Camera = new Camera();
            CameraManager.instance = current;
            Assert.True(owner.Refresh(current, controller));
            Assert.Equal(2, controller.RainController!.Initializations);
            Assert.Same(current.Camera, controller.RainController.BoundCamera);
            if (replaceManager) Assert.Equal(0, first.SubscriberCount);
        }

        [Fact]
        public void RepeatedPreparationOnSameCameraRerendersWithoutReinitialization()
        {
            var (owner, controller, manager) = Ready();
            for (int retry = 0; retry < 4; retry++) Assert.True(owner.Refresh(manager, controller));
            Assert.Equal(1, controller.RainController!.Initializations);
            Assert.Equal(4, controller.RainController._depthPhotograper!.Renders);
        }

        [Fact]
        public void FailedNativeCallbackIsNotRecordedAsInitialized()
        {
            var (owner, controller, manager) = Ready();
            controller.RainController!.FailInitializations = 1;
            Assert.Throws<InvalidOperationException>(() => owner.Refresh(manager, controller));
            Assert.True(owner.Refresh(manager, controller));
            Assert.True(owner.Refresh(manager, controller));
            Assert.Equal(2, controller.RainController.Initializations);
            Assert.Equal(2, controller.RainController._depthPhotograper!.Renders);
        }

        static (DonorCameraSubscriptions, WeatherController, CameraManager) Ready()
        {
            var controller = new WeatherController { RainController = new RainController { _depthPhotograper = new DepthPhotograper() } };
            var manager = CameraManager.instance = new CameraManager { Camera = new Camera() };
            return (new DonorCameraSubscriptions(), controller, manager);
        }
    }
}

// Only the event/disposal contract of the installed game's CameraManager is needed by the
// production subscription owner. Native rendering/classification remains a separate host test.
namespace EFT.CameraControl
{
    public sealed class CameraManager
    {
        public static CameraManager? instance;
        public static CameraManager Instance => instance ??= new CameraManager();
        public static bool Exist => instance != null;
        public UnityEngine.Camera? Camera;
        public event Action? OnCameraChanged;
        public int SubscriberCount => OnCameraChanged?.GetInvocationList().Length ?? 0;
        public void Raise() => OnCameraChanged?.Invoke();
        public void Dispose() { OnCameraChanged = null; if (ReferenceEquals(instance, this)) instance = null; }
    }
}

namespace EFT.Weather
{
    public sealed class WeatherController
    {
        public RainController? RainController;
        public int CameraRefreshes;
        public void OnCameraChanged() => CameraRefreshes++;
    }
}

public sealed class DepthPhotograper
{
    public static DepthPhotograper? Instance;
    public int Renders;
    public void Render() => Renders++;
}
public sealed class RainController
{
    public DepthPhotograper? _depthPhotograper;
    public UnityEngine.Camera? BoundCamera;
    public int Initializations, FailInitializations;
    public void CameraChanged()
    {
        Initializations++;
        if (FailInitializations-- > 0) throw new InvalidOperationException("Injected native rain initialization failure");
        BoundCamera = EFT.CameraControl.CameraManager.Instance.Camera;
        _depthPhotograper!.Render();
    }
}
