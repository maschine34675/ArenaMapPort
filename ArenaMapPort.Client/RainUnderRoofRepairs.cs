using System;
using System.Collections.Generic;
using ArenaMapPort.Shared;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;

namespace ArenaMapPort.Client
{
    /// <summary>
    /// Rain drops on the screen on the ported maps (user request 2026-11-21: "auch mal wegen den Regentropfen auf dem Bildschirm schauen";
    /// docs/progress/two-maps-item-6-raincover.md "2026-11-21", the goal 3(c) that <see cref="RainCoverRepairs"/> had deferred).
    ///
    /// What vanilla does: <c>RainController.Update</c> sets the static <c>RainController.IsCameraUnderRain</c> every frame from
    /// <c>DepthPhotograper.CheckIfCameraUnderRain</c>, which casts one ray from 100 m above the camera against the one
    /// <c>MeshCollider</c> of <c>WeatherObstacle.Instance</c> and says "under rain" when nothing is hit; it answers false while no
    /// WeatherObstacle exists. Everything that wants rain ON the camera reads that flag: <c>RainScreenDropsPlacer</c> (the drops run down
    /// the screen only with it), <c>ScreenWater</c> (the glasses' distortion), <c>RippleController</c> (the environment ripple cycle),
    /// <c>RainCondensator</c> (the rain direction on wet props) and <c>Player.IsUnderRoof</c> (= its negation; feeds the step-layer
    /// audio's under-roof status).
    ///
    /// What the ported scene lacks: no ported map authors a WeatherObstacle (Arena's Bay5 DryPlane set is a Lighthouse leftover), so the
    /// flag is false for ever: in a storm, standing in the open, no drop ever reaches the screen
    /// (host 2026-11-21, Yard: <c>underRoof=True cameraUnderRain=False</c> in the open yard). The FALLING rain is a different mechanism and
    /// works: its shader culls against the top-down depth photo that <see cref="RainCoverRepairs"/> feeds.
    ///
    /// What this does: answers the flag from that same photo. After every <c>DepthPhotograper.Render()</c> it keeps the photo's conversion
    /// parameters; while the camera moves it asks <c>AsyncGPUReadback</c> for a window of at most 7 x 7 texels (about +-0.25 m) around the
    /// camera's texel (no stall, at most two requests in flight, only when the texel changed). The window's highest photographed surface
    /// is "the top surface at the camera"; a per-frame postfix on <c>CheckIfCameraUnderRain</c> says "under rain" when no surface was
    /// photographed there or the highest one is not higher than the camera minus 0.1 m (<see cref="RainUnderRoofModel.CoverMargin"/>, so
    /// head-height cover still counts as a roof; <see cref="RainUnderRoofModel"/>). Drops on the screen and the falling streaks
    /// therefore agree by construction, in every RainCover mode, with Equator's FullyIndoorCover and with the lab switches.
    ///
    /// Why a window and not the whole photo: the photo is 4096 x 4096 RFloat (64 MiB); a full copy is a stall or a 16.7 M sample reduction
    /// on the main thread at every camera change (every respawn in ArenaMode), and there is no max-filter shader to downsample with.
    /// The readback's row order is not assumed: a 4 x 4 probe texture (filled from a Texture2D through <c>Graphics.Blit</c>, the way the
    /// photo itself is written) is read back once per session through the SAME region overload the windows use (one row, two columns) and
    /// classified by the logical row that came back (<see cref="RainUnderRoofModel.ClassifyRegionProbe"/>), so the region's own y semantics
    /// are what is tested, not only the layout of a whole-texture copy; a result that fits neither order keeps the repair off with one Warning.
    ///
    /// The harmless error is the old behaviour (false, "under roof"): not ready, no result yet, a result older than 2 m of camera travel,
    /// a camera outside the photo, a photo that carries no cover (RainCoverMethod Off or the legacy RoofDepth), a failed readback, the key
    /// off: all leave vanilla's value alone. A real <c>WeatherObstacle</c> with a collider always wins. The armed state is per raid and
    /// <see cref="Release"/> (from <c>WeatherRig.OnSceneUnloaded</c>) disarms it and sets the static back to false when this repair ever
    /// wrote true, because the donor RainController is deactivated at raid end and would leave its last value for the menu and the hideout.
    /// Live developer key <c>Lighting (&lt;key&gt;)/RainUnderRoofCheck</c> (default on, not public). Unit-tested core, host check pending.
    /// </summary>
    internal static class RainUnderRoofRepairs
    {
        const string Tag = "[RainUnderRoof] ";

        sealed class Slot
        {
            internal bool Busy;
            internal RainWindow Window;
            internal Action<AsyncGPUReadbackRequest> Callback;
        }

        static readonly RainUnderRoofState _state = new RainUnderRoofState();
        static readonly Dictionary<string, ConfigEntry<bool>> _switches = new Dictionary<string, ConfigEntry<bool>>();
        static readonly Slot[] _slots = CreateSlots();
        static readonly float[] _buffer = new float[(2 * RainUnderRoofModel.MaxWindowRadiusTexels + 1) * (2 * RainUnderRoofModel.MaxWindowRadiusTexels + 1)];
        static readonly int MapStartId = Shader.PropertyToID("_MapStart");
        static readonly int MapScaleId = Shader.PropertyToID("_MapScale");

        // The fast path of the per-frame hook: ONE static read while no ported raid is armed (every vanilla raid, the hideout, the menu).
        static volatile bool _armed;
        static DepthPhotograper _photographer;
        static ArenaMapDescriptor _map;
        static ConfigEntry<bool> _switch;
        static bool _wroteTrue;
        static bool _failed;
        static int _errors;
        static int _probe; // 0 not started, 1 reading, 2 classified
        static bool _installed;
        static string _lastDisarm = "none";

        // The last evaluated frame (for Describe and the transition lines).
        static bool _haveLast, _lastUnderRain;
        static float _lastCamY = float.NaN, _lastTop = float.NaN, _nextLog;
        static RainNotReady _lastWhy;

        static Slot[] CreateSlots()
        {
            var slots = new Slot[2];
            for (int i = 0; i < slots.Length; i++)
            {
                var slot = new Slot();
                slot.Callback = request => OnReadback(slot, request);
                slots[i] = slot;
            }
            return slots;
        }

        /// <summary>Binds the per-map developer key (Plugin.Awake, after WeatherRig.Install). Nothing is read from any scene here.</summary>
        internal static void Install()
        {
            if (_installed) return;
            _installed = true;
            foreach (var map in ArenaMapRegistry.All)
            {
                var entry = Plugin.Instance.Settings.Bind($"Lighting ({map.Key})", "RainUnderRoofCheck", true,
                    "2026-11-21 — answers RainController.IsCameraUnderRain (rain drops on the screen, the glasses' water, the environment " +
                    "ripples, Player.IsUnderRoof) from the rain-cover depth photo, which vanilla cannot do here (no WeatherObstacle on a " +
                    "ported map: the flag was false for ever, so no drop ever reached the screen). Off = vanilla's value (false). Live; " +
                    "switching it on photographs the depth again. Only while this map's raid is loaded and RainCoverMethod=RoofDepthBlack.");
                var mapCopy = map;
                entry.SettingChanged += (_, __) => OnSwitchChanged(mapCopy);
                _switches[map.Key] = entry;
            }
        }

        static void OnSwitchChanged(ArenaMapDescriptor map)
        {
            try
            {
                if (ArenaMapRaid.CurrentMap != map || !_switches.TryGetValue(map.Key, out var entry)) return;
                if (!entry.Value) Disarm("key switched off");
                else RainCoverRepairs.RescanAndRerender("RainUnderRoofCheck switched on");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning(Tag + "switching the key threw: " + e.GetType().Name + ": " + e.Message);
            }
        }

        /// <summary>Harmony postfix on <c>DepthPhotograper.Render()</c> (HarmonyPatches): a new photo, a new generation.</summary>
        internal static void RenderPostfix(DepthPhotograper __instance)
        {
            try
            {
                if (_failed) return;
                var map = ArenaMapRaid.CurrentMap;
                if (map == null || !WeatherRig.IsActiveForCurrentMap() || __instance != WeatherRig.DepthPhotographerForCurrentMap()) return;
                if (!_switches.TryGetValue(map.Key, out var entry) || !entry.Value)
                {
                    if (_armed) Disarm("key off");
                    return;
                }
                var obstacle = global::WeatherObstacle.Instance;
                if (obstacle != null && obstacle.MeshCollider != null)
                {
                    if (_armed) Disarm("a real WeatherObstacle answers the flag");
                    return;
                }
                if (!RainCoverRepairs.PhotoCarriesCover(map, out int coverDraws, out string why))
                {
                    Disarm("not armed for map=" + map.Key + ": " + why);
                    return;
                }
                if (!SystemInfo.supportsAsyncGPUReadback)
                {
                    Fail("this graphics device does not support AsyncGPUReadback");
                    return;
                }
                if (!RainCoverRepairs.TryGetCachedCameraMeta(map, out float top, out float far))
                {
                    Disarm("not armed for map=" + map.Key + ": the depth camera's parameters were not captured");
                    return;
                }
                Vector3 start = Shader.GetGlobalVector(MapStartId), scale = Shader.GetGlobalVector(MapScaleId);
                var photo = new RainPhoto(start.x, start.z, scale.x, scale.z, (int)__instance._depthTextureDimension, top, far,
                    SystemInfo.usesReversedZBuffer);
                if (!photo.IsUsable || __instance._depthRT == null)
                {
                    Disarm("not armed for map=" + map.Key + ": the photo's parameters are not usable");
                    return;
                }
                _state.BeginPhoto(photo);
                _photographer = __instance;
                _map = map;
                _switch = entry;
                _haveLast = false;
                _armed = true;
                StartProbe();
                Plugin.Log.LogDebug(Tag + "map=" + map.Key + " armed on a new photo: generation=" + _state.Generation + " dim=" + photo.Dim +
                    " texel=" + photo.TexelMetres.ToString("0.000") + "m window=" + (2 * RainUnderRoofModel.RadiusTexels(photo) + 1) + "x" +
                    (2 * RainUnderRoofModel.RadiusTexels(photo) + 1) + " depthTop=" + top.ToString("0.00") + " far=" + far.ToString("0.00") +
                    " reversedZ=" + photo.ReversedZ + " coverDraws=" + coverDraws + " margin=" +
                    RainUnderRoofModel.CoverMargin.ToString("0.00") + " orientation=" + _state.Orientation);
            }
            catch (Exception e)
            {
                Fail("the photo hook threw: " + e.GetType().Name + ": " + e.Message);
            }
        }

        /// <summary>Harmony postfix on <c>DepthPhotograper.CheckIfCameraUnderRain</c>: runs every frame while a RainController lives.</summary>
        internal static void CheckPostfix(DepthPhotograper __instance, Transform cameraTransform, ref bool __result)
        {
            if (!_armed) return;
            try
            {
                if (__instance != _photographer || cameraTransform == null) return;
                var entry = _switch;
                if (entry == null || !entry.Value)
                {
                    Disarm("key switched off");
                    return;
                }
                var obstacle = global::WeatherObstacle.Instance;
                if (obstacle != null && obstacle.MeshCollider != null) return; // a real obstacle always wins
                Vector3 p = cameraTransform.position;
                bool decided = _state.TryDecide(p.x, p.y, p.z, out bool underRain, out float top, out RainNotReady why);
                _lastCamY = p.y;
                _lastTop = top;
                _lastWhy = why;
                if (decided)
                {
                    __result = underRain;
                    if (underRain) _wroteTrue = true;
                    if (!_haveLast || underRain != _lastUnderRain) LogDecision(p, top, underRain);
                    _haveLast = true;
                    _lastUnderRain = underRain;
                }
                RequestWindow(p);
            }
            catch (Exception e)
            {
                Fail("the per-frame hook threw: " + e.GetType().Name + ": " + e.Message);
            }
        }

        static void RequestWindow(Vector3 p)
        {
            Slot slot = null;
            for (int i = 0; i < _slots.Length; i++)
            {
                if (!_slots[i].Busy) { slot = _slots[i]; break; }
            }
            if (slot == null) return;
            var rt = _photographer._depthRT;
            if (rt == null || !rt.IsCreated() || rt.width != _state.Photo.Dim || rt.height != _state.Photo.Dim) return;
            if (!_state.TryBeginRequest(p.x, p.z, out RainWindow window)) return;
            slot.Busy = true;
            slot.Window = window;
            AsyncGPUReadback.Request(rt, 0, window.X, window.Width, window.Y, window.Height, 0, 1, TextureFormat.RFloat, slot.Callback);
        }

        static void OnReadback(Slot slot, AsyncGPUReadbackRequest request)
        {
            slot.Busy = false;
            try
            {
                // a request of an older photo, or one that ends after the raid: neither counts, not even as an error
                if (!_armed || slot.Window.Generation != _state.Generation) return;
                if (request.hasError)
                {
                    _state.ForgetRequest(slot.Window.Generation); // ask again on the next frame
                    if (++_errors >= 5) Fail("the depth readback failed " + _errors + " times in a row");
                    return;
                }
                var data = request.GetData<float>();
                int count = Math.Min(data.Length, _buffer.Length);
                if (count < slot.Window.Count)
                {
                    _state.ForgetRequest(slot.Window.Generation);
                    return;
                }
                for (int i = 0; i < count; i++) _buffer[i] = data[i];
                _errors = 0;
                _state.Submit(slot.Window, _buffer, count);
            }
            catch (Exception e)
            {
                Fail("the readback callback threw: " + e.GetType().Name + ": " + e.Message);
            }
        }

        // The one-off orientation probe: a 4 x 4 RFloat target filled by Graphics.Blit from a Texture2D with known, all different texels
        // (the way DepthPhotograper writes its photo), read back through the region overload the windows use. Destroyed in the callback.
        static void StartProbe()
        {
            if (_probe != 0) return;
            _probe = 1;
            Texture2D source = null;
            RenderTexture target = null;
            try
            {
                int n = RainUnderRoofModel.ProbeSize;
                var values = new float[n * n];
                for (int y = 0; y < n; y++)
                    for (int x = 0; x < n; x++)
                        values[y * n + x] = RainUnderRoofModel.ProbeValue(x, y);
                source = new Texture2D(n, n, TextureFormat.RFloat, false, true) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
                source.SetPixelData(values, 0);
                source.Apply(false);
                target = new RenderTexture(n, n, 0, RenderTextureFormat.RFloat) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
                target.Create();
                var previous = RenderTexture.active; // Blit leaves its target active; we run inside RainController.CameraChanged
                try { Graphics.Blit(source, target); }
                finally { RenderTexture.active = previous; }
                var src = source;
                var dst = target;
                AsyncGPUReadback.Request(target, 0, RainUnderRoofModel.ProbeRegionX, RainUnderRoofModel.ProbeRegionWidth,
                    RainUnderRoofModel.ProbeRegionY, RainUnderRoofModel.ProbeRegionHeight, 0, 1, TextureFormat.RFloat,
                    request => OnProbe(request, src, dst));
            }
            catch (Exception e)
            {
                if (source != null) UnityEngine.Object.Destroy(source);
                if (target != null) { target.Release(); UnityEngine.Object.Destroy(target); }
                Fail("the orientation probe could not be started: " + e.GetType().Name + ": " + e.Message);
            }
        }

        static void OnProbe(AsyncGPUReadbackRequest request, Texture2D source, RenderTexture target)
        {
            try
            {
                var orientation = RainProbeOrientation.Unusable;
                if (!request.hasError)
                {
                    var data = request.GetData<float>();
                    var copy = new float[data.Length];
                    for (int i = 0; i < copy.Length; i++) copy[i] = data[i];
                    orientation = RainUnderRoofModel.ClassifyRegionProbe(copy);
                }
                _state.Orientation = orientation;
                _probe = 2;
                Plugin.Log.LogDebug(Tag + "readback orientation probe: " + orientation);
                if (orientation == RainProbeOrientation.Unusable) Fail("the region readback's row order could not be determined");
            }
            catch (Exception e)
            {
                Fail("the orientation probe threw: " + e.GetType().Name + ": " + e.Message);
            }
            finally
            {
                if (source != null) UnityEngine.Object.Destroy(source);
                if (target != null) { target.Release(); UnityEngine.Object.Destroy(target); }
            }
        }

        static void LogDecision(Vector3 p, float top, bool underRain)
        {
            float now = Time.unscaledTime;
            if (now < _nextLog) return; // a doorway edge may flip twice in a second; one line is enough
            _nextLog = now + 0.5f;
            Plugin.Log.LogDebug(Tag + "map=" + (_map != null ? _map.Key : "?") + " cameraUnderRain=" + underRain + " camera=(" +
                p.x.ToString("0.00") + ", " + p.y.ToString("0.00") + ", " + p.z.ToString("0.00") + ") photoTop=" +
                (float.IsNaN(top) ? "nothing" : top.ToString("0.00")) + " margin=" + RainUnderRoofModel.CoverMargin.ToString("0.00") +
                " generation=" + _state.Generation);
        }

        /// <summary>Raid end (WeatherRig.OnSceneUnloaded): disarm, drop pending results, and undo this repair's own write of the static flag.</summary>
        internal static void Release()
        {
            try
            {
                bool wasArmed = _armed;
                _errors = 0;
                Disarm(null);
                if (wasArmed) Plugin.Log.LogDebug(Tag + "released at the end of the raid");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning(Tag + "release threw: " + e.GetType().Name + ": " + e.Message);
            }
        }

        static void Disarm(string reason)
        {
            bool was = _armed;
            _armed = false;
            _state.Reset();
            _photographer = null;
            _map = null;
            _switch = null;
            _haveLast = false;
            if (_wroteTrue)
            {
                _wroteTrue = false;
                RainController.IsCameraUnderRain = false; // the donor RainController stops updating it when its rig is deactivated
            }
            if (reason != null)
            {
                _lastDisarm = reason;
                Plugin.Log.LogDebug(Tag + reason + (was ? " (disarmed)" : string.Empty));
            }
        }

        static void Fail(string why)
        {
            bool first = !_failed;
            _failed = true;
            Disarm(null);
            if (first)
            {
                Plugin.Log.LogWarning(Tag + "the rain-drops check cannot work (" + why + "): the screen drops stay at vanilla's setting, " +
                    "which is off on a ported map. Nothing else changes.");
            }
        }

        /// <summary>The decision's inputs for the F8 readback and the footstep probe; allocates, never called per frame.</summary>
        internal static string Describe(Vector3 position)
        {
            if (_failed) return "rainUnderRoof[failed]";
            bool on = _map != null && _switches.TryGetValue(_map.Key, out var entry) && entry.Value;
            string text = "rainUnderRoof[armed=" + _armed + " key=" + (on ? "on" : "off/none") + " generation=" + _state.Generation +
                " orientation=" + _state.Orientation + " margin=" + RainUnderRoofModel.CoverMargin.ToString("0.00") +
                " lastCameraY=" + _lastCamY.ToString("0.00") + " lastPhotoTop=" + (float.IsNaN(_lastTop) ? "nothing" : _lastTop.ToString("0.00"));
            RainNotReady why = RainNotReady.None;
            bool underRain = false;
            float top = float.NaN;
            if (_armed && _state.TryDecide(position.x, position.y, position.z, out underRain, out top, out why))
                text += " atPosition: photoTop=" + (float.IsNaN(top) ? "nothing" : top.ToString("0.00")) + " cameraY=" + position.y.ToString("0.00") +
                    " cameraUnderRain=" + underRain;
            else
                text += " notReady=" + (_armed ? why.ToString() : "disarmed") + " lastWhy=" + _lastWhy + " lastDisarm=" + _lastDisarm;
            return text + "]";
        }

        /// <summary>Installs the two Harmony hooks (HarmonyPatches); a missing target is logged and skipped through <c>Verify</c>.</summary>
        internal static void InstallHooks(Harmony harmony)
        {
            var check = AccessTools.Method(typeof(DepthPhotograper), nameof(DepthPhotograper.CheckIfCameraUnderRain));
            if (HarmonyPatches.Verify(check, typeof(DepthPhotograper), nameof(DepthPhotograper.CheckIfCameraUnderRain)))
            {
                harmony.Patch(check, postfix: new HarmonyMethod(AccessTools.Method(typeof(RainUnderRoofRepairs), nameof(CheckPostfix))));
                Plugin.Log.LogDebug("[Harmony] patched DepthPhotograper.CheckIfCameraUnderRain (rain drops on the screen from the depth photo)");
            }
            var render = AccessTools.Method(typeof(DepthPhotograper), nameof(DepthPhotograper.Render));
            if (HarmonyPatches.Verify(render, typeof(DepthPhotograper), nameof(DepthPhotograper.Render)))
            {
                harmony.Patch(render, postfix: new HarmonyMethod(AccessTools.Method(typeof(RainUnderRoofRepairs), nameof(RenderPostfix))));
                Plugin.Log.LogDebug("[Harmony] patched DepthPhotograper.Render (rain drops on the screen: new photo, new generation)");
            }
        }
    }
}
