using System;
using EFT.CameraControl;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ArenaMapPort.Client
{
    /// <summary>
    /// CameraManager.Dispose clears its event and destroys the singleton after each raid.
    /// A one-time subscription therefore only repairs the first raid. SetCamera assigns the
    /// fully configured camera and fires that event at its end (EFT 0.16.9.5, CameraManager.cs
    /// 386-408); a static postfix survives manager replacement without polling or stale delegates.
    /// GPU verification: docs/progress/equator-anamorphic-hdr.md.
    /// </summary>
    internal static class CameraRepairHooks
    {
        internal static void Install(Harmony harmony)
        {
            var target = AccessTools.Method(typeof(CameraManager), nameof(CameraManager.SetCamera), new[] { typeof(Camera) });
            if (!HarmonyPatches.Verify(target, typeof(CameraManager), nameof(CameraManager.SetCamera))) return;
            harmony.Patch(target, postfix: new HarmonyMethod(typeof(CameraRepairHooks), nameof(AfterSetCamera)));
            SceneManager.sceneUnloaded += scene =>
            {
                if (ArenaMapRaid.MapForScriptsScene(scene.name) != null) Release();
            };
            Plugin.Log.LogDebug("[ArenaMapPort][Camera] patched CameraManager.SetCamera; repairs survive manager disposal");
        }

        static void AfterSetCamera(CameraManager __instance)
        {
            if (!ArenaMapRaid.IsLoaded()) { Release(); HdrImageTuning.LogDiagnostics(); return; }
            if (__instance.Camera == null) return;
            // Isolate repairs: an optional native effect failure must not suppress bloom/flare.
            try { CameraEffectsRepairs.OnCameraChanged(); }
            catch (Exception e) { Plugin.Log.LogError($"[ArenaMapPort][Camera] repair failed: {e}"); }
            BloomTuning.ApplyToCurrentCamera("camera set");
            AnamorphicFlareTuning.ApplyToCurrentCamera("camera set");
            HdrImageTuning.OnCameraChanged();
        }

        static void Release()
        {
            HdrImageTuning.Release();
            BloomTuning.Release();
            AnamorphicFlareTuning.Release();
        }
    }
}
