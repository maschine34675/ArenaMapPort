#nullable disable // Match the client project when this file is linked into the nullable-enabled test project.
using System;
using System.Reflection;
using ArenaMapPort.Shared;
using UnityEngine;
using UnityEngine.Rendering;

namespace ArenaMapPort.Client
{
    /// <summary>
    /// DistantShadow caches mesh/material references separately from Renderer. Our live clone
    /// replacement must retire that snapshot before its old Unity objects are destroyed. Restart
    /// the native update cycle so it rebuilds from final renderer state; never disable shadows or
    /// suppress DrawMesh exceptions. See the 06:33:30.276 archived Bay5 failure on 2026-09-23.
    /// </summary>
    internal static class DistantShadowCache
    {
        const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic;
        static readonly FieldInfo ObjectsRemaining = typeof(DistantShadow).GetField("_objectsToRenderCount", Fields);
        static readonly FieldInfo ImpostorsRemaining = typeof(DistantShadow).GetField("_impostorsToRenderCount", Fields);
        static readonly FieldInfo GeometryCommands = typeof(DistantShadow).GetField("_renderObjectsCmdBuf", Fields);
        static bool _layoutWarning;

        internal static void Invalidate(ArenaMapDescriptor map, string reason)
        {
            if (map == null || ArenaMapRaid.CurrentMap != map) return;
            // Verified on the installed SPT assembly. A later incompatible layout must not break
            // a config callback or reset unrelated shadow/rendering state by guessing field names.
            if (ObjectsRemaining?.FieldType != typeof(int) || ImpostorsRemaining?.FieldType != typeof(int)
                || GeometryCommands?.FieldType != typeof(CommandBuffer))
            {
                if (!_layoutWarning)
                {
                    _layoutWarning = true;
                    Plugin.Log.LogWarning("[ArenaMapPort][DistantShadowCache] Unsupported native cache layout; " +
                        "could not invalidate shadow references after a live renderer change.");
                }
                return;
            }

            int reset = 0;
            foreach (var shadow in UnityEngine.Object.FindObjectsOfType<DistantShadow>(true))
            {
                // Scene instances only; do not mutate prefab assets or change any enabled state.
                if (shadow == null || !shadow.gameObject.scene.IsValid() || !shadow.gameObject.scene.isLoaded
                    || shadow.MasterCamera == null) continue;
                try
                {
                    // The public InvalidateRenderers method also submits draws and does not reset
                    // these counters. Calling it in the middle of a cycle can leave stale indices.
                    // Native Update checks its invalidation flag only when BOTH counters are zero.
                    ObjectsRemaining.SetValue(shadow, 0);
                    ImpostorsRemaining.SetValue(shadow, 0);
                    shadow.InvalidateRenderersOnNextUpdate = true;
                    (GeometryCommands.GetValue(shadow) as CommandBuffer)?.Clear();
                    reset++;
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning($"[ArenaMapPort][DistantShadowCache] map={map.Key} {reason}: " +
                        $"{e.GetType().Name}: {e.Message}");
                }
            }
            if (reset != 0)
                Plugin.Log.LogDebug($"[ArenaMapPort][DistantShadowCache] map={map.Key} {reason}: " +
                    $"invalidated={reset}; native shadow cache rebuild queued.");
        }
    }
}
