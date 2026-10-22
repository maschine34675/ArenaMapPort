#nullable disable
using System;
using System.Collections.Generic;

namespace ArenaMapPort.Client
{
    /// <summary>
    /// The Harmony targets <c>HarmonyPatches.Verify</c> did not find, for the in-game map notices (docs/progress/map-notices.md, row HOOK_MISSING).
    ///
    /// What vanilla does. Nothing: a hook that cannot be attached (a renamed or re-signatured EFT method after a game update) is one Error line in the
    /// BepInEx log and the hook is skipped, every other hook still installs.
    ///
    /// What the port lacks. A player whose game build does not fit this ArenaMapPort build sees the maps in the selection and then a failed load, with no hint
    /// at the cause. Two of the hooks decide whether a map can load at all: the one that serves the map bundles (<c>BundlesManager.LoadBundleAsync</c>) and the
    /// one that installs the NavMesh before the bots start (<c>BotsController.Init</c>).
    ///
    /// What this class does. <see cref="Record"/> keeps every skipped target once, in the order they were found; <see cref="MapCritical"/> returns the short names
    /// (<c>BundlesManager.LoadBundleAsync</c>) of the two map-critical ones, which the main-menu notice names. Other skipped hooks are not worth a message.
    /// The names are plain strings so the class stays free of Unity and EFT references: <see cref="Record"/> takes the declaring type's full name. A static, locked
    /// list; nothing here throws or logs (Verify keeps its own Error line).
    ///
    /// Host test: docs/progress/map-notices.md (the unit tests; a real skipped hook needs a game build that does not fit).
    /// </summary>
    internal static class SkippedHooks
    {
        internal const string BundleServingType = "EFT.AssetsManager.BundlesManager";
        internal const string BundleServingMethod = "LoadBundleAsync";
        internal const string NavMeshType = "EFT.BotsController";
        internal const string NavMeshMethod = "Init";

        static readonly object Sync = new object();
        static readonly List<string> All = new List<string>();
        static readonly List<string> Critical = new List<string>();

        /// <summary>
        /// Notes a target that was not found. <paramref name="declaringTypeFullName"/> is the type's full name (<c>EFT.AssetsManager.BundlesManager</c>), kept as
        /// the short <c>Type.Method</c> in <see cref="Targets"/>. The same target twice (<c>BotsController.Init</c> is verified by two installers) is one entry.
        /// Never throws.
        /// </summary>
        internal static void Record(string declaringTypeFullName, string methodName)
        {
            try
            {
                string type = declaringTypeFullName ?? "?";
                string method = methodName ?? "?";
                string name = ShortName(type) + "." + method;
                lock (Sync)
                {
                    if (!All.Contains(name))
                        All.Add(name);
                    if (IsMapCritical(type, method) && !Critical.Contains(name))
                        Critical.Add(name);
                }
            }
            catch
            {
                // a registry that cannot record says nothing; Verify's Error line is the evidence
            }
        }

        /// <summary>True for the two targets without which a map cannot load (the bundle serving and the NavMesh install).</summary>
        internal static bool IsMapCritical(string declaringTypeFullName, string methodName) =>
            (string.Equals(declaringTypeFullName, BundleServingType, StringComparison.Ordinal) && string.Equals(methodName, BundleServingMethod, StringComparison.Ordinal)) ||
            (string.Equals(declaringTypeFullName, NavMeshType, StringComparison.Ordinal) && string.Equals(methodName, NavMeshMethod, StringComparison.Ordinal));

        /// <summary>The short names of every skipped target, in the order they were found.</summary>
        internal static IReadOnlyList<string> Targets()
        {
            lock (Sync)
                return All.ToArray();
        }

        /// <summary>The short names of the skipped map-critical targets (a subset of <see cref="Targets"/>); empty when all of them were found.</summary>
        internal static IReadOnlyList<string> MapCritical()
        {
            lock (Sync)
                return Critical.ToArray();
        }

        /// <summary>Forget everything (tests only).</summary>
        internal static void ResetForTests()
        {
            lock (Sync)
            {
                All.Clear();
                Critical.Clear();
            }
        }

        static string ShortName(string fullName)
        {
            int dot = fullName.LastIndexOf('.');
            return dot < 0 ? fullName : fullName.Substring(dot + 1);
        }
    }
}
