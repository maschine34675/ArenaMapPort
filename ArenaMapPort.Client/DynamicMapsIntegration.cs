using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BepInEx.Bootstrap;
using HarmonyLib;

namespace ArenaMapPort.Client
{
    /// <summary>
    /// DynamicMaps scans only its own folders. Add our owned packages before its native precache:
    /// 1.2.1 through its additive loader; 1.3.0 through one post-scan catalogue merge.
    /// No binary dependency or direct config/third-party file writes. Evidence: docs/progress/dynamicmaps-owned.md and dynamicmaps-130.md.
    /// </summary>
    internal static class DynamicMapsIntegration
    {
        internal const string PluginGuid = "com.mpstark.dynamicmaps";
        static DynamicMapAssets _assets;
        static DynamicMapsOwnedCatalog _catalog;
        static MethodInfo _load;
        static PropertyInfo _layers, _image;
        static readonly HashSet<string> Reported = new HashSet<string>();

        internal static void Install(Harmony harmony)
        {
            if (!Chainloader.PluginInfos.ContainsKey(PluginGuid))
            {
                Plugin.Log.LogDebug("[DynamicMaps] Optional mod absent; overview integration inactive.");
                return;
            }
            var dropdown = AccessTools.TypeByName("DynamicMaps.UI.Controls.MapSelectDropdown");
            var definition = AccessTools.TypeByName("DynamicMaps.Data.MapDef");
            var layer = AccessTools.TypeByName("DynamicMaps.Data.MapLayerDef");
            const BindingFlags instance = BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly;
            _load = dropdown?.GetMethod("LoadMapDefsFromPath", instance, null, new[] { typeof(string) }, null);
            var read = definition?.GetMethod("LoadFromPath", BindingFlags.Public | BindingFlags.Static,
                null, new[] { typeof(string) }, null);
            _assets = new DynamicMapAssets(Path.GetDirectoryName(typeof(Plugin).Assembly.Location));
            if (read == null)
            {
                try
                {
                    if (_load?.ReturnType != typeof(void)) throw new MissingMethodException("LoadMapDefsFromPath(string)");
                    _catalog = new DynamicMapsOwnedCatalog(_assets, dropdown, definition, layer);
                    harmony.Patch(_load, postfix: new HarmonyMethod(typeof(DynamicMapsIntegration), nameof(CatalogPostfix)));
                    Plugin.Log.LogDebug("[DynamicMaps] Optional owned map loader installed (1.3 catalogue): " + _assets.Root);
                }
                catch (Exception ex)
                {
                    if (_load != null) harmony.Unpatch(_load, HarmonyPatchType.Postfix, harmony.Id);
                    _catalog = null;
                    WarnOnce("api", "Optional map loader unavailable; expected DynamicMaps 1.2.1 or 1.3.0 API: " + ex.GetBaseException().Message);
                }
                return;
            }
            _layers = definition?.GetProperty("Layers", instance);
            _image = layer?.GetProperty("ImagePath", instance);
            if (_load?.ReturnType != typeof(void) || read == null || read.ReturnType != definition
                || _layers == null || !typeof(IDictionary).IsAssignableFrom(_layers.PropertyType)
                || _layers.GetGetMethod() == null || _image?.PropertyType != typeof(string)
                || _image.GetGetMethod() == null || _image.GetSetMethod() == null)
            {
                WarnOnce("api", "DynamicMaps API differs from verified 1.2.1; optional maps integration skipped.");
                return;
            }
            try
            {
                harmony.Patch(read, postfix: new HarmonyMethod(typeof(DynamicMapsIntegration), nameof(DefinitionPostfix)));
                harmony.Patch(_load, postfix: new HarmonyMethod(typeof(DynamicMapsIntegration), nameof(LoadPostfix)));
                Plugin.Log.LogDebug("[DynamicMaps] Optional owned map loader installed: " + _assets.Root);
            }
            catch (Exception ex)
            {
                harmony.Unpatch(read, HarmonyPatchType.Postfix, harmony.Id);
                harmony.Unpatch(_load, HarmonyPatchType.Postfix, harmony.Id);
                WarnOnce("install", "Optional map loader unavailable: " + ex.Message);
            }
        }

        static void CatalogPostfix(object __instance, string __0)
        {
            try { _catalog.Merge(__instance, __0, WarnOnce); }
            catch (Exception ex) { WarnOnce("load", "Could not load owned map data: " + ex.GetBaseException().Message); }
        }

        static void LoadPostfix(object __instance, string __0)
        {
            try
            {
                _assets.LoadAdditional(__0, path =>
                {
                    try { _load.Invoke(__instance, new object[] { path }); }
                    catch (Exception ex) { WarnOnce(path, "Could not load " + path + ": " + ex.GetBaseException().Message); }
                },
                    map => WarnOnce(map, "Missing owned " + map + " map definition or SVG; package skipped."));
            }
            catch (Exception ex) { WarnOnce("load", "Could not load owned map data: " + ex.GetBaseException().Message); }
        }

        static void DefinitionPostfix(string __0, ref object __result)
        {
            if (__result == null || !_assets.OwnsDefinition(__0)) return;
            try
            {
                if (!_assets.NormalizeGroundLayer(__0, _layers.GetValue(__result) as IDictionary,
                    layer => _image.GetValue(layer) as string, (layer, path) => _image.SetValue(layer, path)))
                    throw new InvalidDataException("Expected one owned ground layer with its SVG present.");
            }
            catch (Exception ex)
            {
                // DynamicMaps skips null definitions; never let a broken owned layer stop its global precache.
                __result = null;
                WarnOnce(__0, "Rejected owned map definition " + __0 + ": " + ex.Message);
            }
        }

        static void WarnOnce(string key, string message)
        {
            if (Reported.Add(key)) Plugin.Log.LogWarning("[DynamicMaps] " + message);
        }
    }
}
