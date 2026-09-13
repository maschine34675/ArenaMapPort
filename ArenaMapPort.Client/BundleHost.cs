using System;
using System.Collections.Generic;
using System.IO;
using ArenaMapPort.Shared;
using EFT.AssetsManager;
using UnityEngine;

namespace ArenaMapPort.Client
{
    /// <summary>
    /// Harmony prefix target for <see cref="BundlesManager.LoadBundleAsync"/>, resolved and applied
    /// defensively by <see cref="HarmonyPatches"/> (AccessTools + null check, not attribute-scanned
    /// PatchAll — see that file's header comment for why).
    /// </summary>
    internal static class BundleHostPrefix
    {
        static void Prefix(BundlesManager __instance, string bundleName)
        {
            BundleHost.TryServe(__instance, bundleName);
        }
    }

    /// <summary>
    /// §F.1: serves our 3 bundles from &lt;plugin dir&gt;/bundles/ by pre-populating
    /// <see cref="BundlesManager._bundles"/> before <c>BundleLoadOperation.Run</c> (BundlesManager.cs:64-82)
    /// checks it — that check is the method's very first line, before anything network-related, so once we
    /// have populated the entry the original body returns immediately with our bundle and never reaches the
    /// <c>DownloadingUrl</c> fetch branch.
    ///
    /// Verified against THIS project's own decompiled 4.1 client
    /// (D:/SPT41/Development/_decompiled/EFT.AssetsManager/BundlesManager.cs, full file read) — on this
    /// build <c>_bundles</c> is a public field and <c>AssetBundleReference</c> a public nested class with a
    /// public (AssetBundle) constructor (BundlesManager.cs:14-25,113), unlike Icebreaker's retail target
    /// where the equivalents are private/obfuscated and need AccessTools reflection
    /// (icebreaker-client/IcebreakerBundleHost.cs). None needed here.
    /// </summary>
    internal static class BundleHost
    {
        static Dictionary<string, string> _ourBundles; // served key -> absolute file path under bundles/

        static string PluginDir => Path.GetDirectoryName(typeof(BundleHost).Assembly.Location) ?? ".";

        static string BundlesDir => Path.Combine(PluginDir, "bundles");

        // §F.1: "OurBundles maps all 3 served keys (§0) -> their absolute file paths under
        // <plugin dir>/bundles/". The navmesh bundle is, in practice, loaded directly by
        // NavMeshInstaller (nothing in the vanilla load chain ever requests it by served key, since it
        // is not referenced from the patched ScenesPreset's _scenesResourceKeys) — registered here anyway
        // per §F.1's instruction and as cheap insurance if that ever changes.
        static Dictionary<string, string> OurBundles()
        {
            if (_ourBundles != null)
            {
                return _ourBundles;
            }

            _ourBundles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [ArenaEquatorLocation.PresetBundleKey] = Path.Combine(BundlesDir, "arena_equator_preset.bundle"),
                [ArenaEquatorLocation.ScenesBundleKey] = Path.Combine(BundlesDir, "arena_equator_scenes.bundle"),
                [ArenaEquatorLocation.NavMeshBundleKey] = Path.Combine(BundlesDir, "arena_equator_navmesh.bundle"),
            };
            return _ourBundles;
        }

        internal static void TryServe(BundlesManager manager, string bundleName)
        {
            if (manager == null || string.IsNullOrEmpty(bundleName))
            {
                return;
            }
            if (!OurBundles().TryGetValue(bundleName, out var filePath))
            {
                return; // not one of ours — vanilla behaviour
            }
            if (manager._bundles.ContainsKey(bundleName))
            {
                return; // already served/loaded this process
            }

            if (!File.Exists(filePath))
            {
                Plugin.Log.LogError($"[ArenaMapPort][Bundles] '{bundleName}' expected at '{filePath}' but the file is missing");
                return;
            }

            AssetBundle bundle;
            try
            {
                bundle = AssetBundle.LoadFromFile(filePath);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[ArenaMapPort][Bundles] failed to load '{filePath}': {e.GetType().Name}: {e.Message}");
                return;
            }

            if (bundle == null)
            {
                // A bundle built for the wrong BuildTarget loads as null with no other signal
                // (§B.WP2.8 / §H risk #12) — this is the runtime symptom if that ever regresses.
                Plugin.Log.LogError($"[ArenaMapPort][Bundles] AssetBundle.LoadFromFile returned null for '{filePath}' " +
                                     "(wrong BuildTarget, or a corrupt/incompatible file?)");
                return;
            }

            manager._bundles[bundleName] = new BundlesManager.AssetBundleReference(bundle);

            long bytes = SafeFileLength(filePath);
            Plugin.Log.LogInfo($"[ArenaMapPort][Bundles] loaded '{bundleName}' from '{filePath}' ({bytes} bytes)");
        }

        static long SafeFileLength(string path)
        {
            try
            {
                return new FileInfo(path).Length;
            }
            catch
            {
                return -1;
            }
        }
    }
}
