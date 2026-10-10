using System;
using System.Globalization;
using BepInEx.Configuration;
using EFT.Ballistics;
using HarmonyLib;
using UnityEngine;

namespace ArenaMapPort.Client
{
    /// <summary>
    /// Observes the real projectile collision after EFT has selected a material and handled the hit.
    /// This is an opt-in bounded log probe; it never recalculates random outcomes or changes a shot.
    /// </summary>
    internal static class BallisticMaterialDiagnostics
    {
        static ConfigEntry<bool> _enabled;
        static int _remaining;

        internal static void Install(Harmony harmony)
        {
            _enabled = Plugin.Instance.Config.Bind("Combat", "LogMaterialHits", false,
                "Log up to 100 real projectile hits on owned map geometry. Re-enable to reset the budget. " +
                "Diagnostic only; does not change hit or penetration behavior.");
            _remaining = 100;
            _enabled.SettingChanged += (_, __) => _remaining = 100;
            if (!DeveloperOptions.Enabled) return;
            var target = AccessTools.Method(typeof(Shot), nameof(Shot.HandleCollision),
                new[] { typeof(float), typeof(Vector3), typeof(Vector3) });
            if (target == null)
            {
                Plugin.Log.LogWarning("[ArenaMapPort][MaterialHits] Shot.HandleCollision unavailable; diagnostics disabled.");
                return;
            }
            harmony.Patch(target, postfix: new HarmonyMethod(typeof(BallisticMaterialDiagnostics), nameof(AfterCollision)));
        }

        static void AfterCollision(Shot __instance)
        {
            if (!_enabled.Value || _remaining <= 0) return;
            try
            {
                var hit = __instance.HitCollider;
                if (hit == null || !ArenaMapRaid.IsOwnedScene(hit.gameObject.scene.name)) return;
                --_remaining;
                var material = __instance.HittedBallisticCollider;
                Plugin.Log.LogInfo("[ArenaMapPort][MaterialHits] scene=" + hit.gameObject.scene.name +
                    " collider='" + Path(hit.transform) + "' " + Describe(material) +
                    " defaultFallback=" + ReferenceEquals(material, __instance._defaultHitBody) +
                    " forward=" + __instance.IsForwardHit + " state=" + __instance.BulletState +
                    " hit=" + __instance.HitPoint.ToString("F3") + " remaining=" + _remaining);
            }
            catch (Exception error)
            {
                Plugin.Log.LogWarning("[ArenaMapPort][MaterialHits] inspection failed: " + error.Message);
            }
        }

        internal static string Describe(BallisticCollider collider)
        {
            if (collider == null) return "ballistic=<none>";
            return "material=" + collider.TypeOfMaterial + " preset=" + collider._presetId +
                " values=" + string.Join("/", Array.ConvertAll(new[] {
                    collider.PenetrationLevel, collider.PenetrationChance, collider.RicochetChance,
                    collider.FragmentationChance, collider.TrajectoryDeviationChance, collider.TrajectoryDeviation
                }, v => v.ToString("R", CultureInfo.InvariantCulture))) +
                " surface=" + collider.SurfaceSound + " hitType=" + collider.HitType + " netId=" + collider.NetId;
        }

        static string Path(Transform transform)
        {
            string result = transform.name;
            while (transform.parent != null)
            {
                transform = transform.parent;
                result = transform.name + "/" + result;
            }
            return result;
        }
    }
}
