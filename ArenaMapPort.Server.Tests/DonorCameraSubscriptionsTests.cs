using ArenaMapPort.Client;
using EFT.CameraControl;
using Xunit;

namespace ArenaMapPort.Server.Tests
{
    public sealed class DonorCameraSubscriptionsTests
    {
        [Fact]
        public void FirstActivationReplacesAwakeCallbacksWithoutDoublingThem()
        {
            var manager = new CameraManager();
            var owner = new DonorCameraSubscriptions();
            int weather = 0, rain = 0;
            Action onWeather = () => weather++, onRain = () => rain++;
            manager.OnCameraChanged += onWeather; // WeatherController.Awake
            manager.OnCameraChanged += onRain;    // RainController.Awake
            owner.Bind(manager, onWeather, onRain);
            manager.Raise();
            Assert.Equal(2, manager.SubscriberCount);
            Assert.Equal(1, weather);
            Assert.Equal(1, rain);
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
    public void CameraChanged()
    {
        BoundCamera = EFT.CameraControl.CameraManager.Instance.Camera;
        _depthPhotograper!.Render();
    }
}
