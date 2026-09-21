#nullable disable
using System;
using EFT.CameraControl;

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

        internal void Bind(CameraManager manager, Action weatherChanged, Action rainChanged)
        {
            Unbind();
            if (manager == null) return;
            _manager = manager;
            _weatherChanged = weatherChanged;
            _rainChanged = rainChanged;
            manager.OnCameraChanged -= weatherChanged;
            manager.OnCameraChanged -= rainChanged;
            manager.OnCameraChanged += weatherChanged;
            manager.OnCameraChanged += rainChanged;
        }

        internal void Unbind()
        {
            if (_manager != null)
            {
                _manager.OnCameraChanged -= _weatherChanged;
                _manager.OnCameraChanged -= _rainChanged;
            }
            _manager = null;
            _weatherChanged = _rainChanged = null;
        }
    }
}
