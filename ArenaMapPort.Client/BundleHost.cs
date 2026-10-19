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
    ///
    /// Stage 2 of the Arena gate (docs/PLAN-ENCRYPTION.md): the shipped bundles are AMPB containers whose key is derived from the
    /// player's Arena files (<see cref="BundleKeyResolver"/>). <see cref="LoadAssetBundle"/> is the one place that loads a bundle of
    /// ours, for this class and for <see cref="NavMeshInstaller"/>: an AMPB container opens as a decrypting stream that
    /// <c>AssetBundle.LoadFromStream</c> reads for the bundle's whole lifetime (the stream is tracked and closed once EFT has
    /// unloaded the bundle, not before: EFT keeps a loaded map bundle cached across raids); a plaintext bundle (development
    /// builds) still loads from its file. Nothing here ever throws into EFT.
    /// </summary>
    internal static class BundleHost
    {
        static Dictionary<string, string> _ourBundles; // served key -> absolute file path under bundles/
        static Dictionary<string, string> _ourMapKeys; // served key -> the owning map's key (the key file and the AMPB header name it)

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
            _ourMapKeys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var map in ArenaMapRegistry.All)
            {
                _ourBundles[map.PresetBundleKey] = Path.Combine(BundlesDir, map.PresetBundleFileName);
                _ourBundles[map.ScenesBundleKey] = Path.Combine(BundlesDir, map.ScenesBundleFileName);
                _ourBundles[map.NavMeshBundleKey] = Path.Combine(BundlesDir, map.NavMeshBundleFileName);
                _ourMapKeys[map.PresetBundleKey] = map.Key;
                _ourMapKeys[map.ScenesBundleKey] = map.Key;
                _ourMapKeys[map.NavMeshBundleKey] = map.Key;
            }
            return _ourBundles;
        }

        // Arena installation gate (0.2.3, ArenaInstallationGate): our bundles carry content derived from Escape from
        // Tarkov: Arena, so they are served only when an installed Arena was verified (registry entry or the
        // General/ArenaInstallPath override, both checked against the launcher's file manifest). Without it we do
        // not hand the bundle to EFT: this prefix is void, so the original LoadBundleAsync still runs, finds no
        // entry in _bundles and EFT reports the missing bundle - a loud failure. The server normally prevents
        // that situation by not registering the Arena locations without an installation, so no raid asks for
        // these bundles in the first place. A vanilla bundle never reaches the gate (the lookup above returns).
        // The first request may wait for the check's worker thread (bounded, see ArenaInstallationGate).
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
            if (!ArenaInstallationGate.EnsureVerified())
            {
                Plugin.Log.LogError(ArenaInstallationGate.NotServedMessage(bundleName));
                return; // before the File.Exists check: nothing of ours is touched without Arena
            }
            MaintainStreams(); // stage 2: close the streams of bundles EFT unloaded, report a damaged chunk (once)
            if (manager._bundles.ContainsKey(bundleName))
            {
                return; // already served/loaded this process
            }

            if (!File.Exists(filePath))
            {
                Plugin.Log.LogError($"[Bundles] '{bundleName}' expected at '{filePath}' but the file is missing");
                return;
            }

            // Stage 2: LoadAssetBundle opens an AMPB container with the map's key (resolved from the player's Arena files on
            // the map's first request) or loads a plaintext bundle from the file; null = refused or failed, already logged.
            _ourMapKeys.TryGetValue(bundleName, out var mapKey);
            var bundle = LoadAssetBundle(filePath, mapKey, bundleName, false, out var prepared);
            if (bundle == null)
            {
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

            // Unity reads the stream of an encrypted bundle until the bundle is unloaded: keep it, and close it once EFT's own
            // table no longer holds this bundle (the plaintext path has no stream, Track ignores it).
            BundleKeyResolver.Default.Streams.Track(prepared, () => IsStillServed(manager, bundleName, bundle));
        }

        // The AssetBundle is alive while EFT's table still holds exactly this instance (BundlesManager.UnloadBundle unloads it and
        // removes the entry) and Unity has not destroyed it.
        static bool IsStillServed(BundlesManager manager, string bundleName, AssetBundle bundle)
        {
            if (bundle == null)
            {
                return false;
            }
            return manager._bundles.TryGetValue(bundleName, out var reference) &&
                   ReferenceEquals(reference.AssetBundle, bundle);
        }

        /// <summary>
        /// The one place that loads one of our bundles, for <see cref="TryServe"/> and <see cref="NavMeshInstaller"/>. Returns the
        /// AssetBundle, or null when it was refused or failed to load (the reason is logged, nothing is thrown). A plaintext file loads
        /// with <c>LoadFromFile</c>; an AMPB container is opened by <see cref="BundleKeyResolver.Prepare"/> and loaded from its
        /// decrypting stream with one chunk as the managed read buffer. <paramref name="prepared"/> owns that stream: the caller keeps
        /// it open for as long as the bundle is loaded and disposes it afterwards (null when null is returned).
        /// </summary>
        internal static AssetBundle LoadAssetBundle(string filePath, string mapKey, string servedKey, bool navMesh, out PreparedBundle prepared)
        {
            var tag = navMesh ? BundleKeyResolver.NavMeshTag : BundleKeyResolver.BundlesTag;
            var resolver = BundleKeyResolver.Default;
            prepared = resolver.Prepare(filePath, mapKey, servedKey, navMesh);
            if (prepared.Kind == PreparedKind.Refused)
            {
                prepared = null;
                return null;
            }

            var encrypted = prepared.Kind == PreparedKind.Encrypted;
            var api = encrypted ? "AssetBundle.LoadFromStream" : "AssetBundle.LoadFromFile";
            AssetBundle bundle;
            try
            {
                if (encrypted)
                {
                    // Unity takes the stream's current position as the start of the bundle.
                    prepared.Stream.Position = 0;
                    bundle = AssetBundle.LoadFromStream(prepared.Stream, 0u, BundleKeyResolver.ReadBufferSize);
                }
                else
                {
                    bundle = AssetBundle.LoadFromFile(filePath);
                }
            }
            catch (Exception e)
            {
                resolver.Streams.ReportError(prepared);
                Plugin.Log.LogError($"{tag} failed to load '{filePath}': {e.GetType().Name}: {e.Message}");
                prepared.Dispose();
                prepared = null;
                return null;
            }

            if (bundle == null)
            {
                // A bundle built for the wrong BuildTarget loads as null with no other signal
                // (§B.WP2.8 / §H risk #12) — this is the runtime symptom if that ever regresses. For an encrypted bundle the
                // stream may know better: a damaged chunk is reported first.
                resolver.Streams.ReportError(prepared);
                Plugin.Log.LogError($"{tag} {api} returned null for '{filePath}' " +
                                     "(wrong BuildTarget, or a corrupt/incompatible file?)");
                prepared.Dispose();
                prepared = null;
                return null;
            }

            Plugin.Log.LogDebug(BundleKeyResolver.LoadedLine(prepared, SafeFileLength(filePath)));
            return bundle;
        }

        /// <summary>
        /// Debug line D3 for the open streams of <paramref name="mapKey"/> (<c>[Bundles] stream '&lt;file&gt;' (reason): ...</c>), after the
        /// housekeeping of <see cref="MaintainStreams"/>. <see cref="NavMeshInstaller"/> calls it at raid start and on the Scripts scene unload.
        /// </summary>
        internal static void LogStreamStats(string mapKey, string reason)
        {
            try
            {
                BundleKeyResolver.Default.Streams.LogStats(mapKey, reason);
            }
            catch (Exception e)
            {
                Plugin.Log.LogDebug($"[Bundles] stream statistics failed ({e.GetType().Name}: {e.Message})");
            }
        }

        // Closes the streams of unloaded bundles and logs a damaged chunk once; never throws (a failure here must not stop a bundle request).
        static void MaintainStreams()
        {
            try
            {
                BundleKeyResolver.Default.Streams.Maintain();
            }
            catch (Exception e)
            {
                Plugin.Log.LogDebug($"[Bundles] stream housekeeping failed ({e.GetType().Name}: {e.Message})");
            }
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
