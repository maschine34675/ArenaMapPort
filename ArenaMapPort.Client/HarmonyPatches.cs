using System;
using System.Reflection;
using EFT;
using EFT.AssetsManager;
using HarmonyLib;
using Koenigz.PerfectCulling.EFT;

namespace ArenaMapPort.Client
{
    /// <summary>
    /// Central, defensive Harmony installer for every hook this plugin owns.
    ///
    /// Per this work package's brief ("Every Harmony target must be verified in the decompiled sources
    /// ... use AccessTools with null checks and log clearly if a target is missing"): each target is
    /// resolved via <c>AccessTools.Method</c> and explicitly null-checked before patching, rather than a
    /// bare attribute-scanned <c>Harmony.PatchAll(assembly)</c>. This matters specifically because a
    /// future game/SPT update could rename or resignature exactly one of our three targets — with
    /// PatchAll, that one bad `[HarmonyPatch]` attribute can abort the whole scan depending on Harmony
    /// version and patch ordering, silently disabling every other hook in the plugin too. Resolving and
    /// patching each target independently means one missing target logs a clear, specific error and
    /// disables only that one hook; the other two keep working.
    /// </summary>
    internal static class HarmonyPatches
    {
        internal static void InstallAll(Harmony harmony)
        {
            InstallBundleHost(harmony);
            InstallNavMesh(harmony);
            InstallPerfectCullingSkip(harmony);
        }

        // §F.1 — BundlesManager.LoadBundleAsync(string bundleName, bool logErrors = true), instance method.
        // Verified: D:/SPT41/Development/_decompiled/EFT.AssetsManager/BundlesManager.cs:141.
        static void InstallBundleHost(Harmony harmony)
        {
            var target = AccessTools.Method(
                typeof(BundlesManager),
                nameof(BundlesManager.LoadBundleAsync),
                new[] { typeof(string), typeof(bool) });

            if (!Verify(target, typeof(BundlesManager), nameof(BundlesManager.LoadBundleAsync)))
            {
                return;
            }

            var prefix = AccessTools.Method(typeof(BundleHostPrefix), "Prefix");
            harmony.Patch(target, prefix: new HarmonyMethod(prefix));
            Plugin.Log.LogInfo("[ArenaMapPort][Harmony] patched BundlesManager.LoadBundleAsync (§F.1)");
        }

        // §F.2 — BotsController.Init(...), instance method, synchronous void.
        // Verified: D:/SPT41/Development/_decompiled/EFT/BotsController.cs:168.
        static void InstallNavMesh(Harmony harmony)
        {
            var target = AccessTools.Method(typeof(BotsController), nameof(BotsController.Init));

            if (!Verify(target, typeof(BotsController), nameof(BotsController.Init)))
            {
                return;
            }

            var prefix = AccessTools.Method(typeof(NavMeshInstallerPrefix), "Prefix");
            harmony.Patch(target, prefix: new HarmonyMethod(prefix));
            Plugin.Log.LogInfo("[ArenaMapPort][Harmony] patched BotsController.Init (§F.2)");
        }

        // §F.6 — PerfectCullingCrossSceneSampler.InitializeAutoCulling(CancellationToken, IProgress<float>),
        // static method, async Task — see PerfectCullingSkip.cs's own doc comment for the __result caveat
        // this signature forces.
        // Verified: D:/SPT41/Development/_decompiled/Koenigz.PerfectCulling.EFT/PerfectCullingCrossSceneSampler.cs.
        static void InstallPerfectCullingSkip(Harmony harmony)
        {
            var target = AccessTools.Method(
                typeof(PerfectCullingCrossSceneSampler),
                nameof(PerfectCullingCrossSceneSampler.InitializeAutoCulling));

            if (!Verify(target, typeof(PerfectCullingCrossSceneSampler),
                    nameof(PerfectCullingCrossSceneSampler.InitializeAutoCulling)))
            {
                return;
            }

            var prefix = AccessTools.Method(typeof(PerfectCullingSkipPrefix), "Prefix");
            harmony.Patch(target, prefix: new HarmonyMethod(prefix));
            Plugin.Log.LogInfo("[ArenaMapPort][Harmony] patched " +
                                "PerfectCullingCrossSceneSampler.InitializeAutoCulling (§F.6)");
        }

        static bool Verify(MethodBase method, Type declaringType, string methodName)
        {
            if (method != null)
            {
                return true;
            }

            Plugin.Log.LogError(
                $"[ArenaMapPort][Harmony] target not found: {declaringType.FullName}.{methodName} — this hook " +
                "is DISABLED for this session. The decompiled evidence docs/SPEC.md §F cites for this target no " +
                "longer matches the running game build (renamed/resignatured method, or a type that moved " +
                "assembly). Every OTHER ArenaMapPort hook still installs and runs normally.");
            return false;
        }
    }
}
