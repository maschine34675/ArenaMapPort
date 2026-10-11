using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ArenaMapPort.Client
{
    /// <summary>
    /// SmajlecLights 1.3.0 attaches LightingFixer to EFT's generic gameplay camera. Its Start
    /// calls Apply even when its Enabled config is false; settings changes call Apply or Restore.
    /// Both methods write Prism tonemapping/exposure and other camera settings after this port's
    /// camera setup. Skip those two writes while any registered port is loaded. The component's
    /// normal Start/OnDestroy subscription lifecycle and all non-ported locations stay intact.
    /// Optional reflected targets were verified against the installed SmajlecLights.dll; there
    /// is no assembly reference and no edit to the other mod's configuration. Host evidence is
    /// recorded with the lighting follow-up; Probe reports actual skipped/allowed calls.
    ///
    /// BrighterInteriors only changes StencilShadow ambient overlays, which our importer does
    /// not currently author. It is left untouched; Probe counts live stencil components so a
    /// future native stencil port cannot silently invalidate that compatibility assessment.
    /// </summary>
    internal static class LightingModCompatibility
    {
        const string SmajlecFixerName = "SmajlecLights.LightingFixer";
        const string BrighterPluginName = "SevenBoldPencil.BrighterInteriors.Plugin";

        static Type _smajlecFixer;
        static bool _installed;
        static string _reportedMap;
        static long _blockedApply;
        static long _blockedRestore;
        static long _allowedOutside;

        internal static void Install(Harmony harmony)
        {
            if (_installed) return;
            _smajlecFixer = AccessTools.TypeByName(SmajlecFixerName);
            if (_smajlecFixer == null)
            {
                Plugin.Log.LogDebug("[ArenaMapPort][LightingCompat] SmajlecLights absent; no compatibility hooks needed.");
                return;
            }

            const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly;
            var apply = _smajlecFixer.GetMethod("Apply", flags, null, Type.EmptyTypes, null);
            var restore = _smajlecFixer.GetMethod("Restore", flags, null, Type.EmptyTypes, null);
            if (!typeof(MonoBehaviour).IsAssignableFrom(_smajlecFixer)
                || !HarmonyPatches.Verify(apply, _smajlecFixer, "Apply")
                || !HarmonyPatches.Verify(restore, _smajlecFixer, "Restore")
                || apply.ReturnType != typeof(void) || restore.ReturnType != typeof(void))
            {
                Plugin.Log.LogWarning("[ArenaMapPort][LightingCompat] SmajlecLights shape differs from the verified build; " +
                    "map exclusion was not installed.");
                return;
            }

            var prefix = AccessTools.Method(typeof(LightingModCompatibility), nameof(SmajlecWritePrefix));
            harmony.Patch(apply, prefix: new HarmonyMethod(prefix));
            harmony.Patch(restore, prefix: new HarmonyMethod(prefix));
            _installed = true;
            SceneManager.sceneUnloaded += scene =>
            {
                if (ArenaMapRaid.MapForScriptsScene(scene.name) != null) _reportedMap = null;
            };
            Plugin.Log.LogDebug("[ArenaMapPort][LightingCompat] SmajlecLights Apply/Restore excluded on all registered ported maps; " +
                "ordinary maps, menu, hideout and saved settings are unchanged.");
        }

        // No captured settings or enable flags to restore on unload: returning true is the complete
        // original behavior. Keep Start/OnDestroy intact even if EFT ever reuses a camera across maps.
        static bool SmajlecWritePrefix(MethodBase __originalMethod)
        {
            var map = ArenaMapRaid.CurrentMap;
            if (map == null)
            {
                _allowedOutside++;
                return true;
            }

            if (__originalMethod.Name == "Apply") _blockedApply++;
            else _blockedRestore++;
            if (_reportedMap != map.Key)
            {
                _reportedMap = map.Key;
                Plugin.Log.LogDebug($"[ArenaMapPort][LightingCompat] {map.Key}: blocked SmajlecLights camera settings " +
                    $"({__originalMethod.Name}); native port lighting retained.");
            }
            return false;
        }

        internal static void Probe()
        {
            if (!DeveloperOptions.Enabled) return;
            int fixers = 0;
            if (_smajlecFixer != null)
                foreach (var candidate in Resources.FindObjectsOfTypeAll(_smajlecFixer))
                    if (candidate is Component component && IsLiveScene(component.gameObject.scene)) fixers++;

            int stencilActive = 0, stencilOwned = 0;
            foreach (var stencil in Resources.FindObjectsOfTypeAll<StencilShadow>())
            {
                if (stencil == null || !stencil.isActiveAndEnabled || !IsLiveScene(stencil.gameObject.scene)) continue;
                stencilActive++;
                if (ArenaMapRaid.IsOwnedScene(stencil.gameObject.scene.name)) stencilOwned++;
            }

            Plugin.Log.LogDebug($"[ArenaMapPort][LightingCompat] map={ArenaMapRaid.CurrentMap?.Key ?? "none"} " +
                $"SmajlecLights present={_smajlecFixer != null} exclusion={_installed} loadedFixers={fixers} " +
                $"processCalls(blockedApply={_blockedApply},blockedRestore={_blockedRestore},allowedOutside={_allowedOutside}); " +
                $"BrighterInteriors present={AccessTools.TypeByName(BrighterPluginName) != null} untouched; " +
                $"activeStencilShadows(allLoaded={stencilActive},owned={stencilOwned})");
        }

        static bool IsLiveScene(Scene scene) => scene.IsValid() && scene.isLoaded;
    }
}
