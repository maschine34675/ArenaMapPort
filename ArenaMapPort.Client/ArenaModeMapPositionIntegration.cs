using System;
using System.Reflection;
using BepInEx.Bootstrap;
using HarmonyLib;
using UnityEngine;

namespace ArenaMapPort.Client
{
    /// <summary>Preserve native server map positions in ArenaMode's optional label presentation.</summary>
    internal static class ArenaModeMapPositionIntegration
    {
        internal const string PluginGuid = "com.maschine.ArenaMode";
        private static FieldInfo _target;
        private static MethodInfo _refresh;

        internal static void Install(Harmony harmony)
        {
            if (!Chainloader.PluginInfos.ContainsKey(PluginGuid)) return;
            try
            {
                var type = AccessTools.TypeByName("ArenaMode.Session.ArenaPortedMapPresentation");
                var create = type == null ? null : AccessTools.DeclaredMethod(type, "Create");
                _target = type == null ? null : AccessTools.DeclaredField(type, "_target");
                _refresh = type == null ? null : AccessTools.DeclaredMethod(type, "Refresh", Type.EmptyTypes);
                var args = create?.GetParameters();
                if (create == null || !create.IsStatic || create.ReturnType != type ||
                    args.Length != 5 || args[0].ParameterType != typeof(string) ||
                    args[1].ParameterType != typeof(RectTransform) ||
                    _target?.FieldType != typeof(Vector2) || _refresh == null || _refresh.IsStatic)
                {
                    Plugin.Log.LogDebug("[ArenaMapPort] ArenaMode map-position integration skipped: unsupported presentation contract.");
                    return;
                }
                harmony.Patch(create,
                    prefix: new HarmonyMethod(typeof(ArenaModeMapPositionIntegration), nameof(CapturePosition)),
                    postfix: new HarmonyMethod(typeof(ArenaModeMapPositionIntegration), nameof(RestorePosition)));
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[ArenaMapPort] Could not align ArenaMode map labels: {e.Message}");
            }
        }

        private static void CapturePosition(RectTransform __1, out Vector2? __state)
        {
            // LocationButton.Show has already assigned Location.RelativeMapPos before Create.
            __state = __1 == null ? (Vector2?)null : __1.anchorMin;
        }

        private static void RestorePosition(object __result, Vector2? __state)
        {
            if (__result == null || !__state.HasValue) return;
            try
            {
                _target.SetValue(__result, __state.Value);
                _refresh.Invoke(__result, null);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[ArenaMapPort] Could not align an ArenaMode map label: {e.Message}");
            }
        }
    }
}
