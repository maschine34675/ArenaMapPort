#nullable disable
using System;
using EFT.CameraControl;
using EFT.Weather;
using UnityEngine;

namespace ArenaMapPort.Client
{
    /// <summary>
    /// Reused weather/rain components only run Awake once, while CameraManager.Dispose clears its
    /// events and replaces the manager between raids. Own both callbacks on the actual subscribed
    /// manager; remove Awake's initial callbacks before adding them to avoid first-raid duplicates.
    /// </summary>
    internal sealed class DonorCameraSubscriptions
    {
        CameraManager _manager;
        Action _weatherChanged;
        Action _rainChanged;
        Camera _initializedRainCamera;

        // The serialized donor reference survives other photographers overwriting/clearing Instance.
        internal static DepthPhotograper Photo(WeatherController controller) => controller?.RainController?._depthPhotograper;

        internal bool Refresh(CameraManager manager, WeatherController controller)
        {
            if (manager?.Camera == null || Photo(controller) == null) return false;
            var rain = controller.RainController;
            Bind(manager, controller.OnCameraChanged, rain.CameraChanged);
            controller.OnCameraChanged();
            // RainScreenDrops.Init allocates material/state in the native callback. A natural camera
            // event may already have initialized this camera; the post-start pass only needs a photo.
            if (_initializedRainCamera == manager.Camera) Photo(controller).Render();
            else OnRainCameraChanged();
            return true;
        }

        internal void Bind(CameraManager manager, Action weatherChanged, Action rainChanged)
        {
            if (_manager == manager && _weatherChanged == weatherChanged && _rainChanged == rainChanged) return;
            Unbind();
            if (manager == null) return;
            _manager = manager;
            _weatherChanged = weatherChanged;
            _rainChanged = rainChanged;
            manager.OnCameraChanged -= weatherChanged;
            manager.OnCameraChanged -= rainChanged;
            manager.OnCameraChanged += weatherChanged;
            if (rainChanged != null) manager.OnCameraChanged += OnRainCameraChanged;
        }

        void OnRainCameraChanged()
        {
            var manager = _manager;
            var camera = manager?.Camera;
            var rainChanged = _rainChanged;
            _initializedRainCamera = null;
            rainChanged?.Invoke();
            // Record only a completed callback on the still-current binding/camera. Failed native
            // initialization must remain retryable rather than being accepted as a completed refresh.
            if (manager == _manager && rainChanged == _rainChanged && manager?.Camera == camera)
                _initializedRainCamera = camera;
        }

        internal void Unbind()
        {
            if (_manager != null)
            {
                _manager.OnCameraChanged -= _weatherChanged;
                _manager.OnCameraChanged -= OnRainCameraChanged;
            }
            _manager = null;
            _weatherChanged = _rainChanged = null;
            _initializedRainCamera = null;
        }
    }
}
