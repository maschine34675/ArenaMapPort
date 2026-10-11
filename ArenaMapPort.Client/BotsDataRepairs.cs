using System.Collections.Generic;
using UnityEngine;

namespace ArenaMapPort.Client
{
    /// <summary>
    /// impl-raidstart-repairs item — new findings from docs/progress/audit-reference-repairs.md
    /// (predicted next blockers after SpatialAudioSystem/EnvironmentManager, both inside
    /// <c>BotsController.Init</c> which runs unconditionally on every local raid regardless of bot
    /// count): <c>AIVoxelesData.RestoreData</c>'s null <c>VoxelsList</c>, and the missing
    /// <c>AIStationaryController</c> BSG's find-and-call sequence assumes exists.
    /// </summary>
    internal static class AIVoxelesDataGuardPrefix
    {
        /// <summary>
        /// <c>BotsController.Init</c> (<c>EFT/BotsController.cs:173-174</c>) calls
        /// <c>AICoversData.CreateOrFind().RestoreData()</c>, which (<c>AICoversData.cs:420</c>) calls
        /// <c>Voxels.RestoreData(dictionary2, this)</c> — <c>AIVoxelesData.RestoreData</c>
        /// (<c>AIVoxelesData.cs:52-57</c>): <c>int num = VoxelsList.Count(x => x.HaveStartPointId);</c>.
        /// <c>VoxelsList</c> (<c>AIVoxelesData.cs:32</c>, <c>public List&lt;NavGraphVoxelSimple&gt;
        /// VoxelsList;</c>) has no field initializer, and <c>AICoversData.CreateOrFind</c>
        /// (<c>AICoversData.cs:345-352</c>) always <c>AddComponent</c>s a fresh <c>AIVoxelesData</c> when
        /// the scene has none (our case — cover/voxel bake data is out of scope, SPEC). A freshly
        /// <c>AddComponent</c>'d MonoBehaviour gets no scene-YAML deserialization pass, so the field
        /// keeps its C# default: null -> NRE on the LINQ <c>Count</c> call, uncaught (the try/catch in
        /// <c>AICoversData.RestoreData</c> only wraps the <c>Points</c>/<c>ManualPoints</c> merge above
        /// this call, not this call itself — <c>AICoversData.cs:383-419</c>).
        ///
        /// Fix: ensure <c>VoxelsList</c> is a non-null EMPTY list before the original body runs, rather
        /// than swallowing the resulting exception and manually reconstructing downstream state the way
        /// ManimalIcebreaker's <c>Patch_CoversRestore</c> finalizer does (<c>RaidFixPatches.cs:3711-3884</c>
        /// — needed there because they have PARTIAL recovered bake data to preserve; we have none, so a
        /// plain empty list lets the original method run to completion normally with zero voxels:
        /// <c>VoxelesArray = new NavGraphVoxelSimple[0,0,0]</c> (default zero Vector3 dims — a 0-length
        /// 3D array is legal), the <c>foreach (VoxelsList)</c> loops do nothing, and
        /// <c>FindObjectsProxy.FindUnityObjectsOfType&lt;NavMeshDoorLink&gt;()</c> returns an empty array
        /// against our door-free scenes — verified against <c>AIVoxelesData.cs</c> in full). Coverless,
        /// zero-voxel bots is the correct outcome for a spike with bots out of scope (SPEC).
        ///
        /// Gated on <see cref="ArenaMapRaid.IsLoaded()"/>: on any other map <c>VoxelsList</c> is
        /// already populated by scene data, so this check is always a no-op there regardless, but the
        /// explicit gate keeps every repair in this plugin scoped the same way.
        /// </summary>
        // Target: AIVoxelesData.RestoreData(Dictionary<int, GroupPoint>, AICoversData), public void —
        // AIVoxelesData.cs:52. Declared in the global namespace in this build (no `using` needed for
        // AIVoxelesData/GroupPoint/AICoversData/NavGraphVoxelSimple).
        internal static void Prefix(AIVoxelesData __instance)
        {
            if (!ArenaMapRaid.IsLoaded() || __instance == null)
            {
                return;
            }

            if (__instance.VoxelsList == null)
            {
                __instance.VoxelsList = new List<NavGraphVoxelSimple>();
                Plugin.Log.LogDebug("[ArenaMapPort][RaidFix] AIVoxelesData.VoxelsList was null (no baked " +
                                    "bot-nav/cover data for this spike) — initialized empty before RestoreData");
            }
        }
    }

    internal static class AIStationaryControllerEnsurePrefix
    {
        /// <summary>
        /// <c>BotsController.Init</c> (<c>EFT/BotsController.cs:185-186</c>):
        /// <c>StationaryWeapons = FindObjectsProxy.FindUnityObjectOfType&lt;AIStationaryController&gt;();
        /// StationaryWeapons.Init(_coversData.AICorePointsHolder);</c> — no null check on the find
        /// result, and we author no stationary-weapon emplacements (out of scope), so the find always
        /// returns null on our scenes -&gt; NRE calling <c>.Init(...)</c> on it.
        /// <c>FindObjectsProxy.FindUnityObjectOfType&lt;T&gt;()</c> (<c>FindObjectsProxy.cs</c>) is a
        /// thin wrapper over <c>UnityEngine.Object.FindObjectOfType&lt;T&gt;()</c>, so creating one
        /// ourselves first makes BSG's own find succeed normally.
        ///
        /// <c>AIStationaryController.Init</c> itself (<c>AIStationaryController.cs:9-17</c>) is safe to
        /// call on a freshly-created, empty instance — verified: <c>Weapons</c>
        /// (<c>AIStationaryController.cs:7</c>) carries an inline <c>= Array.Empty&lt;StationaryWeaponLink&gt;()</c>
        /// initializer in this SPT build (this is the one place the audit found our decompile diverges
        /// from Icebreaker's own decompile, where the equivalent field was apparently nullable — no
        /// Finalizer backstop needed here as a result), so <c>Init</c>'s <c>foreach</c> over zero
        /// weapons is a no-op.
        ///
        /// Adapted from the <c>AIStationaryController</c> half of ManimalIcebreaker's
        /// <c>Patch_EnsureStationaryController</c> prefix (<c>RaidFixPatches.cs:4118-4126</c>, MIT —
        /// `danauraborealis`; attribution in THIRD-PARTY-NOTICES.md) — their companion
        /// <c>ObservedCullingManager</c> half and the whole Postfix (flares/sealed-doors/spawn-trigger
        /// rebuild) are NOT ported: <c>ObservedCullingManager</c> is already ensured earlier, before any
        /// player exists (see <see cref="EnsureEnvironmentPrefix"/>), and doors/flares are out of scope.
        ///
        /// Installed as an ADDITIONAL Harmony prefix on <c>BotsController.Init</c>, alongside the
        /// existing §F.2 <see cref="NavMeshInstallerPrefix"/> — Harmony runs multiple prefixes on one
        /// target independently, and these two ensures are order-independent (each only ever creates
        /// its own kind of component).
        /// </summary>
        internal static void Prefix()
        {
            if (!ArenaMapRaid.IsLoaded())
            {
                return;
            }

            if (Object.FindObjectOfType<AIStationaryController>() == null)
            {
                new GameObject("AIStationaryController(ArenaFallback)").AddComponent<AIStationaryController>();
                Plugin.Log.LogDebug("[ArenaMapPort][RaidFix] created missing AIStationaryController " +
                                    "(no stationary weapons authored for this spike)");
            }
        }
    }
}
