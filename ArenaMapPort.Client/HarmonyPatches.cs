using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Audio.SpatialSystem;
using EFT;
using EFT.AssetsManager;
using EFT.Game.Spawning;
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
            // fix-gamestart, finding F1 (blocker) — CreateFromScene runs in BaseLocalGame.Run before spawn
            // selection; install order here is irrelevant, all patches are in place before any raid.
            InstallSpawnInfiltrationFix(harmony);

            // fix-lighting-movement, item 1 — spawn colliders authored solid/non-trigger; see
            // SpawnColliderRepairs.cs for the full root-cause chain.
            InstallSpawnColliderFix(harmony);

            InstallBundleHost(harmony);
            InstallNavMesh(harmony);
            InstallPerfectCullingSkip(harmony);

            // impl-raidstart-repairs — see docs/progress/impl-raidstart-repairs.md for the audit trail.
            InstallSpatialAudioCreateDefaults(harmony);
            InstallSpatialAudioInitSkip(harmony);
            InstallSpatialAudioOcclusionGuard(harmony);
            InstallSpatialAudioInteractiveOcclusionGuard(harmony);
            InstallEnsureEnvironment(harmony);
            InstallEffectsControllerFrostbite(harmony);
            InstallAIVoxelesDataGuard(harmony);
            InstallAIStationaryControllerEnsure(harmony);

            // fix-combat findings F1/F2 (explosives-items) + one additional confirmed same-class gap —
            // see docs/progress/fix-combat.md and EffectsControllerRepairs.cs's guard doc comments.
            InstallEyeBurnGuard(harmony);
            InstallFlashAccumulatorGuard(harmony);
            InstallCombatScreenEffectsGuard(harmony);

            // Weather rig on the fallback camera — see RainControllerRepairs.cs.
            InstallRainControllerCameraGuard(harmony);
        }

        // RainController.AbstractStateSummer.CameraChanged(CameraManager), public override, nested type.
        // Verified: D:/SPT41/Development/_decompiled/RainController.cs:151,163.
        static void InstallRainControllerCameraGuard(Harmony harmony)
        {
            var summer = AccessTools.Inner(typeof(RainController), "AbstractStateSummer");
            var target = summer != null ? AccessTools.Method(summer, "CameraChanged") : null;

            if (!Verify(target, typeof(RainController), "AbstractStateSummer.CameraChanged"))
            {
                return;
            }

            var prefix = AccessTools.Method(typeof(RainControllerCameraGuard), "Prefix");
            harmony.Patch(target, prefix: new HarmonyMethod(prefix));
            Plugin.Log.LogInfo("[ArenaMapPort][Harmony] patched RainController.AbstractStateSummer.CameraChanged " +
                                "(weather rig on fallback camera)");
        }

        // fix-gamestart, finding F1 — SpawnPointsCollection.CreateFromScene(DateTime?, SpawnPointParams[]),
        // static, single overload. Verified: D:/SPT41/Development/_decompiled/EFT.Game.Spawning/
        // SpawnPointsCollection.cs:152. See SpawnInfiltrationRepairs.cs for the full root-cause chain.
        static void InstallSpawnInfiltrationFix(Harmony harmony)
        {
            var target = AccessTools.Method(
                typeof(SpawnPointsCollection),
                nameof(SpawnPointsCollection.CreateFromScene));

            if (!Verify(target, typeof(SpawnPointsCollection), nameof(SpawnPointsCollection.CreateFromScene)))
            {
                return;
            }

            var postfix = AccessTools.Method(typeof(SpawnInfiltrationFixPostfix), "Postfix");
            harmony.Patch(target, postfix: new HarmonyMethod(postfix));
            Plugin.Log.LogInfo("[ArenaMapPort][Harmony] patched SpawnPointsCollection.CreateFromScene " +
                                "(fix-gamestart F1 — entry-point blocker fix)");
        }

        // fix-lighting-movement, item 1 — SpawnPointMarker.Start(), public instance, no args, single
        // overload. Verified: D:/SPT41/Development/_decompiled/EFT.Game.Spawning/SpawnPointMarker.cs:96.
        // See SpawnColliderRepairs.cs for the full root-cause chain (BSG's own runtime code can never
        // flip isTrigger on a pre-existing collider).
        static void InstallSpawnColliderFix(Harmony harmony)
        {
            var target = AccessTools.Method(typeof(SpawnPointMarker), nameof(SpawnPointMarker.Start));

            if (!Verify(target, typeof(SpawnPointMarker), nameof(SpawnPointMarker.Start)))
            {
                return;
            }

            var postfix = AccessTools.Method(typeof(SpawnColliderRepairs), "Postfix");
            harmony.Patch(target, postfix: new HarmonyMethod(postfix));
            Plugin.Log.LogInfo("[ArenaMapPort][Harmony] patched SpawnPointMarker.Start " +
                                "(fix-lighting-movement — spawn collider trigger fix)");
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

        // impl-raidstart-repairs item (1a) — SpatialAudioSystem.CreateSpatialAudioSystem(Transform),
        // static factory. Verified: D:/SPT41/Development/_decompiled/Audio.SpatialSystem/
        // SpatialAudioSystem.cs:286. See SpatialAudioRepairs.cs for the full decision writeup.
        static void InstallSpatialAudioCreateDefaults(Harmony harmony)
        {
            var target = AccessTools.Method(
                typeof(SpatialAudioSystem),
                nameof(SpatialAudioSystem.CreateSpatialAudioSystem));

            if (!Verify(target, typeof(SpatialAudioSystem), nameof(SpatialAudioSystem.CreateSpatialAudioSystem)))
            {
                return;
            }

            var postfix = AccessTools.Method(typeof(SpatialAudioCreateDefaultsPostfix), "Postfix");
            harmony.Patch(target, postfix: new HarmonyMethod(postfix));
            Plugin.Log.LogInfo("[ArenaMapPort][Harmony] patched SpatialAudioSystem.CreateSpatialAudioSystem " +
                                "(impl-raidstart-repairs 1a)");
        }

        // impl-raidstart-repairs item (1b) — SpatialAudioSystem.Initialize(CancellationToken,
        // IProgress<float>), instance, async Task. Verified: same file, lines 139-182.
        static void InstallSpatialAudioInitSkip(Harmony harmony)
        {
            var target = AccessTools.Method(
                typeof(SpatialAudioSystem),
                nameof(SpatialAudioSystem.Initialize),
                new[] { typeof(CancellationToken), typeof(IProgress<float>) });

            if (!Verify(target, typeof(SpatialAudioSystem), nameof(SpatialAudioSystem.Initialize)))
            {
                return;
            }

            var prefix = AccessTools.Method(typeof(SpatialAudioInitSkipPrefix), "Prefix");
            harmony.Patch(target, prefix: new HarmonyMethod(prefix));
            Plugin.Log.LogInfo("[ArenaMapPort][Harmony] patched SpatialAudioSystem.Initialize (impl-raidstart-repairs 1b)");
        }

        // impl-raidstart-repairs item (1c) — every public instance overload of
        // SpatialAudioSystem.ProcessSourceOcclusion (3 in this build: IPlayer-, GameObject-, and
        // BetterSource-keyed). Resolved by reflection (name match, not a fixed signature) — same
        // technique ManimalIcebreaker's own Patch_OcclusionWhenUninitialized/Patch_OcclusionNeverThrows
        // use for this exact target, because listing 3 full parameter-type arrays by hand is exactly
        // the kind of brittle-against-a-future-overload-change situation this file's own header
        // comment warns about; reflection-by-name is *more* robust here, not less.
        static void InstallSpatialAudioOcclusionGuard(Harmony harmony)
        {
            var targets = typeof(SpatialAudioSystem)
                .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(m => m.Name == nameof(SpatialAudioSystem.ProcessSourceOcclusion))
                .ToArray();

            if (targets.Length == 0)
            {
                Plugin.Log.LogError(
                    "[ArenaMapPort][Harmony] target not found: Audio.SpatialSystem.SpatialAudioSystem." +
                    "ProcessSourceOcclusion (no overload matched by name) — this hook is DISABLED for this " +
                    "session. Every OTHER ArenaMapPort hook still installs and runs normally.");
                return;
            }

            var prefix = new HarmonyMethod(AccessTools.Method(typeof(SpatialAudioOcclusionGuard), "Prefix"));
            var finalizer = new HarmonyMethod(AccessTools.Method(typeof(SpatialAudioOcclusionGuard), "Finalizer"));
            foreach (var target in targets)
            {
                harmony.Patch(target, prefix: prefix, finalizer: finalizer);
            }
            Plugin.Log.LogInfo($"[ArenaMapPort][Harmony] patched SpatialAudioSystem.ProcessSourceOcclusion " +
                                $"({targets.Length} overload(s)) (impl-raidstart-repairs 1c)");
        }

        // fix-raidstart — SpatialAudioSystem.ProcessInteractiveObjectOcclusion(string, BetterSource,
        // Vector3, EOcclusionTest), instance, void, single overload (resolved by name only, same
        // convention as InstallEnsureEnvironment below). A second, previously-unguarded
        // Initialized-independent NRE entry point found by re-review — see SpatialAudioRepairs.cs's
        // SpatialAudioInteractiveOcclusionGuard doc comment for the full writeup.
        // Verified: D:/SPT41/Development/_decompiled/Audio.SpatialSystem/SpatialAudioSystem.cs:554.
        static void InstallSpatialAudioInteractiveOcclusionGuard(Harmony harmony)
        {
            var target = AccessTools.Method(
                typeof(SpatialAudioSystem),
                nameof(SpatialAudioSystem.ProcessInteractiveObjectOcclusion));

            if (!Verify(target, typeof(SpatialAudioSystem), nameof(SpatialAudioSystem.ProcessInteractiveObjectOcclusion)))
            {
                return;
            }

            var prefix = AccessTools.Method(typeof(SpatialAudioInteractiveOcclusionGuard), "Prefix");
            var finalizer = AccessTools.Method(typeof(SpatialAudioInteractiveOcclusionGuard), "Finalizer");
            harmony.Patch(target, prefix: new HarmonyMethod(prefix), finalizer: new HarmonyMethod(finalizer));
            Plugin.Log.LogInfo("[ArenaMapPort][Harmony] patched SpatialAudioSystem.ProcessInteractiveObjectOcclusion " +
                                "(fix-raidstart)");
        }

        // impl-raidstart-repairs item (2) — Player.Init(...), instance, async Task (many parameters —
        // resolved by name only since exactly one method named "Init" is declared directly on EFT.Player).
        // Verified: D:/SPT41/Development/_decompiled/EFT/Player.cs:28167.
        static void InstallEnsureEnvironment(Harmony harmony)
        {
            var target = AccessTools.Method(typeof(Player), nameof(Player.Init));

            if (!Verify(target, typeof(Player), nameof(Player.Init)))
            {
                return;
            }

            var prefix = AccessTools.Method(typeof(EnsureEnvironmentPrefix), "Prefix");
            harmony.Patch(target, prefix: new HarmonyMethod(prefix));
            Plugin.Log.LogInfo("[ArenaMapPort][Harmony] patched Player.Init (impl-raidstart-repairs 2)");
        }

        // impl-raidstart-repairs item (3) — EffectsController.Awake(), public void.
        // Verified: D:/SPT41/Development/_decompiled/EffectsController.cs:1217.
        static void InstallEffectsControllerFrostbite(Harmony harmony)
        {
            var target = AccessTools.Method(typeof(EffectsController), "Awake");

            if (!Verify(target, typeof(EffectsController), "Awake"))
            {
                return;
            }

            var prefix = AccessTools.Method(typeof(EffectsControllerFrostbitePrefix), "Prefix");
            harmony.Patch(target, prefix: new HarmonyMethod(prefix));
            Plugin.Log.LogInfo("[ArenaMapPort][Harmony] patched EffectsController.Awake (impl-raidstart-repairs 3)");
        }

        // impl-raidstart-repairs — AIVoxelesData.RestoreData(Dictionary<int, GroupPoint>, AICoversData),
        // public void. Verified: D:/SPT41/Development/_decompiled/AIVoxelesData.cs:52.
        static void InstallAIVoxelesDataGuard(Harmony harmony)
        {
            var target = AccessTools.Method(
                typeof(AIVoxelesData),
                nameof(AIVoxelesData.RestoreData),
                new[] { typeof(System.Collections.Generic.Dictionary<int, GroupPoint>), typeof(AICoversData) });

            if (!Verify(target, typeof(AIVoxelesData), nameof(AIVoxelesData.RestoreData)))
            {
                return;
            }

            var prefix = AccessTools.Method(typeof(AIVoxelesDataGuardPrefix), "Prefix");
            harmony.Patch(target, prefix: new HarmonyMethod(prefix));
            Plugin.Log.LogInfo("[ArenaMapPort][Harmony] patched AIVoxelesData.RestoreData (impl-raidstart-repairs)");
        }

        // impl-raidstart-repairs — BotsController.Init(...), same target §F.2's NavMeshInstallerPrefix
        // already patches; installed as an ADDITIONAL, independent prefix (Harmony supports multiple
        // prefixes per target). Verified: D:/SPT41/Development/_decompiled/EFT/BotsController.cs:168.
        static void InstallAIStationaryControllerEnsure(Harmony harmony)
        {
            var target = AccessTools.Method(typeof(BotsController), nameof(BotsController.Init));

            if (!Verify(target, typeof(BotsController), nameof(BotsController.Init)))
            {
                return;
            }

            var prefix = AccessTools.Method(typeof(AIStationaryControllerEnsurePrefix), "Prefix");
            harmony.Patch(target, prefix: new HarmonyMethod(prefix));
            Plugin.Log.LogInfo("[ArenaMapPort][Harmony] patched BotsController.Init for AIStationaryController " +
                                "ensure (impl-raidstart-repairs, additional prefix alongside §F.2)");
        }

        // fix-combat F1 (explosives-items) — EffectsController.OnHealthBurnEyes(Vector3, float, float) and
        // .OnClientPlayerEffectsPauseEvent(ClientPlayerEffectsPauseEvent), both public instance, single
        // overload each. Verified: D:/SPT41/Development/_decompiled/EffectsController.cs:1256,1404.
        static void InstallEyeBurnGuard(Harmony harmony)
        {
            var burnEyes = AccessTools.Method(typeof(EffectsController), "OnHealthBurnEyes");
            if (Verify(burnEyes, typeof(EffectsController), "OnHealthBurnEyes"))
            {
                var prefix = AccessTools.Method(typeof(EyeBurnGuardPrefix), "Prefix");
                harmony.Patch(burnEyes, prefix: new HarmonyMethod(prefix));
                Plugin.Log.LogInfo("[ArenaMapPort][Harmony] patched EffectsController.OnHealthBurnEyes (fix-combat F1)");
            }

            var pauseEvent = AccessTools.Method(typeof(EffectsController), "OnClientPlayerEffectsPauseEvent");
            if (Verify(pauseEvent, typeof(EffectsController), "OnClientPlayerEffectsPauseEvent"))
            {
                var prefix = AccessTools.Method(typeof(EyeBurnPauseGuardPrefix), "Prefix");
                harmony.Patch(pauseEvent, prefix: new HarmonyMethod(prefix));
                Plugin.Log.LogInfo("[ArenaMapPort][Harmony] patched EffectsController.OnClientPlayerEffectsPauseEvent " +
                                    "(fix-combat F1)");
            }
        }

        // fix-combat F2 (explosives-items) — the nested EffectsController.FlashAccumulator.AddEffect
        // (IHealthEffect), public instance override, single overload.
        // Verified: D:/SPT41/Development/_decompiled/EffectsController.cs:619,634.
        static void InstallFlashAccumulatorGuard(Harmony harmony)
        {
            var target = AccessTools.Method(typeof(EffectsController.FlashAccumulator), "AddEffect");
            if (!Verify(target, typeof(EffectsController.FlashAccumulator), "AddEffect"))
            {
                return;
            }

            var prefix = AccessTools.Method(typeof(FlashAccumulatorGuardPrefix), "Prefix");
            harmony.Patch(target, prefix: new HarmonyMethod(prefix));
            Plugin.Log.LogInfo("[ArenaMapPort][Harmony] patched EffectsController.FlashAccumulator.AddEffect " +
                                "(fix-combat F2)");
        }

        // fix-combat, additional finding alongside F1/F2 — EffectsController.OnHealthApplyDamage
        // (EBodyPart, float, DamageInfo) and .OnPlayerDamaged(float, EBodyPart, EDamageType, float,
        // MaterialType), both public instance, single overload each.
        // Verified: D:/SPT41/Development/_decompiled/EffectsController.cs:1410,1421.
        static void InstallCombatScreenEffectsGuard(Harmony harmony)
        {
            var applyDamage = AccessTools.Method(typeof(EffectsController), "OnHealthApplyDamage");
            if (Verify(applyDamage, typeof(EffectsController), "OnHealthApplyDamage"))
            {
                var prefix = AccessTools.Method(typeof(CombatScreenEffectsGuard), "Prefix");
                harmony.Patch(applyDamage, prefix: new HarmonyMethod(prefix));
                Plugin.Log.LogInfo("[ArenaMapPort][Harmony] patched EffectsController.OnHealthApplyDamage (fix-combat)");
            }

            var playerDamaged = AccessTools.Method(typeof(EffectsController), "OnPlayerDamaged");
            if (Verify(playerDamaged, typeof(EffectsController), "OnPlayerDamaged"))
            {
                var finalizer = AccessTools.Method(typeof(CombatScreenEffectsGuard), "Finalizer");
                harmony.Patch(playerDamaged, finalizer: new HarmonyMethod(finalizer));
                Plugin.Log.LogInfo("[ArenaMapPort][Harmony] patched EffectsController.OnPlayerDamaged (fix-combat)");
            }
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
