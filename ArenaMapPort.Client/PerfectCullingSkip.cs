using System.Threading.Tasks;
using Koenigz.PerfectCulling.EFT;

namespace ArenaMapPort.Client
{
    /// <summary>
    /// §F.6: <c>PerfectCullingCrossSceneSampler.InitializeAutoCulling</c> is wrapped in a catch-and-rethrow
    /// as "Loading data file failed" by its caller (TarkovApplication.cs:2761-2770, confirmed by direct
    /// read this pass — <c>catch (Exception) { throw new Exception("Loading data file failed"); }</c>
    /// around the awaited call). Our scene set ships no baked occlusion data (decision #8 explicitly
    /// excludes a PerfectCulling bake) — down-weighted in §H risk #2 after a full read of the method body
    /// found no path that throws specifically for an occlusion-data-free scene set, but the mitigation
    /// stays in per §F.6 ("cheap and harmless even though the risk it guards against is now assessed as
    /// lower-probability").
    ///
    /// Verified signature: D:/SPT41/Development/_decompiled/Koenigz.PerfectCulling.EFT/
    /// PerfectCullingCrossSceneSampler.cs — <c>public static async Task InitializeAutoCulling(
    /// CancellationToken token, IProgress&lt;float&gt; progress = null)</c>. Note this type lives inside
    /// Assembly-CSharp.dll itself (BSG merges Koenigz.PerfectCulling.EFT into the main assembly — confirmed
    /// by Assembly-CSharp.csproj covering the whole _decompiled tree, no separate PerfectCulling DLL exists
    /// in EscapeFromTarkov_Data/Managed), so no extra reference is needed beyond Assembly-CSharp.
    ///
    /// ASYNC STATE-MACHINE CAVEAT (the exact thing this work package's instructions call out to verify):
    /// the target is `async Task`, so a bare `return false` prefix with no `__result` handling would leave
    /// the Harmony-patched method returning a NULL Task — the caller's `await PerfectCullingCrossSceneSampler
    /// .InitializeAutoCulling(...)` would then NullReferenceException, turning our "cheap, harmless"
    /// mitigation into the very crash it exists to prevent. `ref Task __result` + `__result =
    /// Task.CompletedTask` supplies a valid, already-completed Task instead, so the await resolves
    /// immediately with no exception and none of the original body runs.
    ///
    /// Harmony prefix target resolved and applied defensively by <see cref="HarmonyPatches"/> (AccessTools
    /// + null check — see that file's header comment for why).
    /// </summary>
    internal static class PerfectCullingSkipPrefix
    {
        static bool Prefix(ref Task __result)
        {
            if (!ArenaMapRaid.IsLoaded())
            {
                return true; // vanilla behaviour for every other map's raid
            }

            __result = Task.CompletedTask;
            Plugin.Log.LogDebug("[PerfectCulling] skipped InitializeAutoCulling " +
                                "(no baked occlusion data for this spike, §F.6)");
            return false; // skip the original body entirely
        }
    }
}
