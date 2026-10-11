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

        // §F.1: "OurBundles maps every registered map's 3 served keys (§0) -> their absolute file paths
        // under <plugin dir>/bundles/". The navmesh bundle is, in practice, loaded directly by
        // NavMeshInstaller (nothing in the vanilla load chain ever requests it by served key, since it
        // is not referenced from the patched ScenesPreset's _scenesResourceKeys) — registered here anyway
        // per §F.1's instruction and as cheap insurance if that ever changes.
        //
        // Multimap generalization (docs/progress/multimap-generalize.md): loops ArenaMapRegistry.All
        // instead of one hardcoded map's 3 consts — Equator's own 3 entries are byte-identical to the
        // pre-generalization dict literal (same served keys, same "arena_equator_*.bundle" filenames).
        static Dictionary<string, string> OurBundles()
        {
            if (_ourBundles != null)
            {
                return _ourBundles;
            }

            _ourBundles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var map in ArenaMapRegistry.All)
            {
                _ourBundles[map.PresetBundleKey] = Path.Combine(BundlesDir, map.PresetBundleFileName);
                _ourBundles[map.ScenesBundleKey] = Path.Combine(BundlesDir, map.ScenesBundleFileName);
                _ourBundles[map.NavMeshBundleKey] = Path.Combine(BundlesDir, map.NavMeshBundleFileName);
            }
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

            // fix-preset-cab-and-bundle-lifetime, item 5: this Harmony hook is installed as a VOID
            // Prefix (HarmonyPatches.InstallBundleHost), and a void Prefix can never skip Harmony's
            // call to the original method (only a bool Prefix returning false can) -- so
            // BundlesManager.LoadBundleAsync's own body (BundleLoadOperation.Run,
            // EFT.AssetsManager/BundlesManager.cs:64-82) ALWAYS still runs immediately after this
            // method returns. That body's very first check is
            // `if (manager._bundles.TryGetValue(bundleName, out var value)) { value.Counter++; ... }`
            // -- since we just added `bundleName` to `manager._bundles` above, it always finds our
            // freshly-created reference and increments its Counter a second time. Combined with
            // AssetBundleReference's own constructor (which always sets Counter = 1,
            // BundlesManager.cs:20-24, not overridable via any constructor argument), every one of
            // our bundles' FIRST load ends up with Counter == 2 for what is, from the caller's own
            // point of view, exactly ONE logical load request -- a silent double-count BSG's own
            // bundles never have (their AssetBundleReference is only ever constructed once, by
            // BundleLoadOperation.Coroutine itself, never pre-populated by a second, earlier writer).
            // A single call to BundlesManager.UnloadBundle for one of our bundles would then only
            // decrement Counter to 1, never actually reaching the 0 that triggers
            // AssetBundle.Unload() -- so our bundle would need one extra, unmatched Unload call
            // before it could ever be released, unlike an equivalent vanilla bundle.
            //
            // Fix: reset Counter to 0 on OUR OWN freshly-created reference, for OUR OWN bundle only,
            // immediately before handing control back to the original method. The original method's
            // own increment (which still unavoidably runs right after this) then brings it to
            // exactly 1 -- correctly representing the single real load request that triggered this
            // call, byte-for-byte consistent with what BSG's own bookkeeping would have produced for
            // an equivalent bundle it loaded itself. This never touches any other bundle's Counter
            // (vanilla or another mod's) -- only the entry this exact call just created.
            var reference = new BundlesManager.AssetBundleReference(bundle) { Counter = 0 };
            manager._bundles[bundleName] = reference;

            long bytes = SafeFileLength(filePath);
            Plugin.Log.LogDebug($"[ArenaMapPort][Bundles] loaded '{bundleName}' from '{filePath}' ({bytes} bytes)");
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
