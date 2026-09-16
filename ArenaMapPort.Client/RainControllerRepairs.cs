using System.Reflection;
using EFT.CameraControl;
using HarmonyLib;

namespace ArenaMapPort.Client
{
    /// <summary>
    /// With a harvested weather rig active, RainController.CameraChanged fires from CameraManager.SetCamera
    /// during raid start. AbstractStateSummer.CameraChanged (RainController.cs:163-169) calls
    /// EffectsController.RainScreenDrops.Init() unguarded, but our maps render through the fallback camera
    /// Cam2, which has no RainScreenDrops component (EffectsController.Init, EffectsController.cs:1284,
    /// runs in Awake, so the property is null only when the component is missing). The NRE aborted raid
    /// start (2026-09-16 22:29:27). Every other RainController use of _rainScreenDrops null-checks it
    /// (RainController.cs:182/204), so skipping only the Init call is enough.
    /// </summary>
    internal static class RainControllerCameraGuard
    {
        static readonly FieldInfo CameraField = AccessTools.Field(typeof(RainController), "_camera");
        static readonly FieldInfo ScreenWaterField = AccessTools.Field(typeof(RainController), "_screenWater");
        static readonly FieldInfo RainScreenDropsField = AccessTools.Field(typeof(RainController), "_rainScreenDrops");
        static bool _logged;

        // Target: RainController.AbstractStateSummer.CameraChanged(CameraManager), public override.
        internal static bool Prefix(RainController.AbstractState __instance, CameraManager cameraManager)
        {
            if (!ArenaMapRaid.IsLoaded() || cameraManager == null)
            {
                return true;
            }
            var effects = cameraManager.EffectsController;
            var controller = __instance?.Controller;
            if (controller == null || (effects != null && effects.RainScreenDrops != null))
            {
                return true; // camera carries the component: original path is safe
            }
            if (CameraField == null || ScreenWaterField == null || RainScreenDropsField == null)
            {
                return true; // fields moved — keep vanilla behaviour rather than half-apply
            }

            // Same as AbstractState.CameraChanged (RainController.cs:97-101) plus the summer assignments,
            // minus RainScreenDrops.Init().
            if (cameraManager.Camera != null)
            {
                CameraField.SetValue(controller, cameraManager.Camera.transform);
            }
            if (controller._depthPhotograper != null)
            {
                controller._depthPhotograper.Render();
            }
            ScreenWaterField.SetValue(controller, effects != null ? effects.ScreenWater : null);
            RainScreenDropsField.SetValue(controller, null);

            if (!_logged)
            {
                _logged = true;
                Plugin.Log.LogInfo("[ArenaMapPort][RaidFix] camera has no RainScreenDrops (fallback Cam2) — " +
                                    "skipped RainController's screen-drop init; rain on the lens is disabled");
            }
            return false;
        }
    }
}
