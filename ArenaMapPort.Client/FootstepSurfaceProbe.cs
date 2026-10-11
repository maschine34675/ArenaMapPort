using System;
using System.Globalization;
using Audio.AmbientSubsystem;
using Comfort.Common;
using CommonAssets.Scripts.Audio;
using EFT;
using EFT.Weather;
using HarmonyLib;
using UnityEngine;

namespace ArenaMapPort.Client
{
    /// <summary>On-demand observation of native footstep selection. No settings, audio playback or surface writes.</summary>
    internal static class FootstepSurfaceProbe
    {
        const string Tag = "[ArenaMapPort][Footsteps] ";

        internal static void Probe()
        {
            if (!DeveloperOptions.Enabled) return;
            // Any released map (CurrentMap only resolves over ArenaMapRegistry.All): Equator, Bay5 and, since
            // PLAN-AIRPIT item 13, AirPit, whose ballistic-material footsteps are checked with the F10 probe.
            var map = ArenaMapRaid.CurrentMap;
            if (map == null) return;
            try
            {
                var world = Singleton<GameWorld>.Instantiated ? Singleton<GameWorld>.Instance : null;
                var player = world?.MainPlayer;
                if (player == null) return;

                Vector3 origin = player.MovementContext.PlayerColliderCenter;
                float distance = player.MovementContext.CharacterController.height + 0.5f;
                Vector3 end = origin + Vector3.down * distance;
                var maskField = AccessTools.Field(typeof(Player), "_stepLayerMask");
                LayerMask mask = maskField != null
                    ? (LayerMask)maskField.GetValue(player)
                    : LayersMaskController.AudioControllerStepLayerMask;
                var before = player.CurrentSurface;

                // This native query does not call UpdateSurfaceData or play a sound. Its return is
                // authoritative; the separate collider replay below is explicitly labelled as such.
                var queried = player.CalculateMovementSurface();
                bool replayHit = NonAllocLineCaster.GetNearestHit(origin, end, out var selected, distance, mask);
                var all = Physics.RaycastAll(origin, end - origin, distance, mask, QueryTriggerInteraction.UseGlobal);
                Array.Sort(all, (a, b) => a.distance.CompareTo(b.distance));

                var set = AccessTools.Field(typeof(Player), "_currentSet")?.GetValue(player) as SurfaceSet;
                var layer = AccessTools.Field(typeof(Player), "_specificStepAudioController")?.GetValue(player)
                    as PlayerStepLayerAudioController;
                string season = Seasons.Controller != null ? Seasons.Controller.Status.ToString() : "<none>";
                Plugin.Log.LogDebug(Tag + "map=" + map.Key + " position=" + V(player.Position) +
                    " ray=" + V(origin) + " -> " + V(end) + " mask=" + mask.value +
                    " queriesHitTriggers=" + Physics.queriesHitTriggers + " current=" + before +
                    " afterQuery=" + player.CurrentSurface + " nativeHit=" + queried.Item1 +
                    " queriedSurface=" + queried.Item2 + " allHitCount=" + all.Length +
                    " nativeBufferCapacity=" + NonAllocLineCaster._nearestRayHits.Length);
                Plugin.Log.LogDebug(Tag + "environment=" + player.Environment + " underRoof=" + player.IsUnderRoof +
                    " cameraUnderRain=" + RainController.IsCameraUnderRain + " season=" + season +
                    " currentSet=" + (set != null ? set.Surface.ToString() : "<none>") +
                    " runBank=" + Bank(set?.RunSoundBank) + " duckBank=" + Bank(set?.DuckSoundBank) +
                    " sprintBank=" + Bank(set?.SprintSoundBank));
                if (layer != null)
                {
                    layer.TryGetBank(EAudioMovementState.Run, out var run);
                    Plugin.Log.LogDebug(Tag + "seasonLayerRun=" + Bank(run) + " layerSurface=" + layer._currentSurface +
                        " layerUnderRoof=" + layer._isUnderRoof + " layerSourceInit=" + layer._isSourceInit +
                        " overallMultiplier=" + F(layer._overallVolumeMultiplier));
                }
                var weather = WeatherController.Instance;
                Plugin.Log.LogDebug(Tag + "rain=" + (weather?.WeatherCurve != null ? F(weather.WeatherCurve.Rain) : "<none>") +
                    " wetting=" + F(RainController.Wetting) + " rainIntensity=" + RainController.IntensityType +
                    " minLyingWater=" + (weather != null ? F(weather.MinLyingWater) : "<none>") +
                    " shaderWaterLevel=" + F(Shader.GetGlobalFloat("_WaterLevel")));
                if (replayHit && selected.collider != null)
                    LogHit("nativeQueryReplay", selected);
                else
                    Plugin.Log.LogDebug(Tag + "nativeQueryReplay=<no hit>");
                // RaycastNonAlloc may return an arbitrary subset when its three-slot native buffer
                // fills. Keep RaycastAll separate so an omitted closer collider is visible.
                for (int i = 0; i < all.Length; i++) LogHit("allSorted[" + i + "]", all[i]);
            }
            catch (Exception error)
            {
                Plugin.Log.LogWarning(Tag + "probe failed: " + error);
            }
        }

        static void LogHit(string label, RaycastHit hit)
        {
            var collider = hit.collider;
            if (collider == null) return;
            var direct = collider.GetComponent<BaseBallistic>();
            var parent = collider.transform.parent != null
                ? collider.transform.parent.GetComponentInParent<BaseBallistic>() : null;
            Plugin.Log.LogDebug(Tag + label + " distance=" + F(hit.distance) + " point=" + V(hit.point) +
                " collider='" + collider.gameObject.scene.name + "/" + Path(collider.transform) +
                "' instance=" + collider.GetInstanceID() + " type=" + collider.GetType().Name +
                " layer=" + collider.gameObject.layer + "/" + LayerMask.LayerToName(collider.gameObject.layer) +
                " enabled=" + collider.enabled + " trigger=" + collider.isTrigger +
                " direct=" + Describe(direct) + " parentOnly=" + Describe(parent));
        }

        static string Describe(BaseBallistic value)
        {
            if (value == null) return "<none>";
            string material = value is EFT.Ballistics.BallisticCollider ballistic
                ? ",material=" + ballistic.TypeOfMaterial + ",preset=" + ballistic._presetId : "";
            return value.GetType().Name + "(surface=" + value.SurfaceSound + material + ",path=" + Path(value.transform) + ")";
        }

        static string Bank(SoundBank bank)
        {
            if (bank == null) return "<none>";
            foreach (var environment in bank.Environments ?? Array.Empty<EnvironmentVariety>())
                foreach (var distance in environment?.Clips ?? Array.Empty<DistanceVarity>())
                    foreach (var clip in distance?.Clips ?? Array.Empty<AudioClip>())
                        if (clip != null) return bank.name + "[example=" + clip.name + "]";
            return bank.name + "[no clips]";
        }
        static string F(float value) => value.ToString("0.###", CultureInfo.InvariantCulture);
        static string V(Vector3 value) => "(" + F(value.x) + "," + F(value.y) + "," + F(value.z) + ")";

        static string Path(Transform value)
        {
            string result = value.name;
            while (value.parent != null)
            {
                value = value.parent;
                result = value.name + "/" + result;
            }
            return result;
        }
    }
}
