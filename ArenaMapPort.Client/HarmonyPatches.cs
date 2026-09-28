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
            EnsureEnvironmentPrefix.BindConfig(); // [Environment] EnableCullingManagerFallback
            LightCullingProbe.Install();          // [Diagnostics] LightCullingProbeHotkey (F7, read-only, PLAN-TWO-MAPS item 7)
            InstallEnsureEnvironment(harmony);
            InstallEffectsControllerFrostbite(harmony);
            InstallAIVoxelesDataGuard(harmony);
            InstallAIStationaryControllerEnsure(harmony);
            // bots-stage1 — a holder with a null CorePoints list made every bot activation throw silently
            // inside BotCreatorClient.CreateBot. See AICorePointRepairs.cs.
            AICorePointFallback.Install(harmony);
            // exfils-stage2 — runtime-authored exfiltration points. See ExfilRepairs.cs.
            ExfilRepairs.Install(harmony);
            ExfilSignals.BindConfig(); // [Exfils] SmokeSignals / SmokeSignalSound / SmokeSignalColor
            BoothDoorRepairs.BindConfig(); // [Spawns] OpenRespawnDoors — runs from SpawnInfiltrationRepairs' postfix
            BallisticTwinRepairs.BindConfig(); // [Combat] BallisticTwins — same postfix (stage 1)
            BarbedWireRepairs.BindConfig();    // [Combat] BarbedWire — same postfix (PLAN-TWO-MAPS item 2)
            PhysicsPropRepairs.BindConfig();   // [Physics] EnablePhysicsProps — same postfix (PLAN-TWO-MAPS item 5)
            BallisticMaterialDiagnostics.Install(harmony);

            // bots-stage1 — SAIN soft dependency: resolved by name, no-op without SAIN. See SainLocationShim.cs.
            SainLocationShim.BindConfig();
            SainLocationShim.TryInstall(harmony);
            // bots-stage1 — spawn-path diagnostics on ported maps only. See BotSpawnProbe.cs.
            BotSpawnProbe.Install(harmony);

            // fix-combat findings F1/F2 (explosives-items) + one additional confirmed same-class gap —
            // see docs/progress/fix-combat.md and EffectsControllerRepairs.cs's guard doc comments.
            InstallEyeBurnGuard(harmony);
            InstallFlashAccumulatorGuard(harmony);
            InstallDeathEffectsGuard(harmony); // stage 1 — the deferred OnHealthDied half of fix-combat
            InstallCombatScreenEffectsGuard(harmony);

            // Weather rig on the fallback camera — see RainControllerRepairs.cs.
            InstallRainControllerCameraGuard(harmony);

            // daytime-and-visuals F1 — see TimeOfDayRepairs.cs.
            InstallTimeOfDayFix(harmony);
            FixedRaidTimeSelection.Install(harmony);

            // donor-settings-camera-weather §C.3 + §D — see RainWetnessRepairs.cs / WeatherTestOverride.cs.
            InstallWetRendererDamper(harmony);
            InstallWeatherTestOverride(harmony);

            // "Lighting Lab" (docs/progress/donor-settings-camera-weather.md) — see LightingLab.cs.
            InstallLightingLabReflectionOverride(harmony);
            InstallReflectionIntensityScale(harmony);
            InstallAmbientIntensityScale(harmony);
            InstallLightingLabWaterLevelOverride(harmony);

            // donor-settings-camera-weather §12 goal 3 — see RainCoverRepairs.cs.
            InstallRainCoverDepthMask(harmony);

            // PLAN-TWO-MAPS item 5 follow-up — see PhysicsPropSoundRepairs.cs / PhysicsPropTeardownRepairs.cs.
            PhysicsPropSoundRepairs.Install(harmony);
            PhysicsPropTeardownRepairs.Install(harmony);

            // PLAN-TWO-MAPS item 10b — deploy-screen art for ported maps. See LoadingScreenArt.cs.
            LoadingScreenArt.Install(harmony);
        }

        // Lighting (<map>) ReflectionIntensity — AmbientLight.method_3(), public instance, no args
        // (D:/SPT41/Development/_decompiled/AmbientLight.cs:434). See WeatherRig.ReflectionIntensityPostfix.
        static void InstallReflectionIntensityScale(Harmony harmony)
        {
            var target = AccessTools.Method(typeof(AmbientLight), "method_3");

            if (!Verify(target, typeof(AmbientLight), "method_3"))
            {
                return;
            }

            var postfix = AccessTools.Method(typeof(WeatherRig), nameof(WeatherRig.ReflectionIntensityPostfix));
            harmony.Patch(target, postfix: new HarmonyMethod(postfix));
            Plugin.Log.LogInfo("[ArenaMapPort][Harmony] patched AmbientLight.method_3 (Lighting ReflectionIntensity scale)");
        }

        // §18 — Lighting (<map>) AmbientIntensity — AmbientLight.SetSH(SphericalHarmonicsL2)/
        // AmbientHighlight.SetSH(SphericalHarmonicsL2), public instance, single overload each
        // (D:/SPT41/Development/_decompiled/AmbientLight.cs:664, AmbientHighlight.cs:137). See
        // WeatherRig.AmbientIntensityPrefix/AmbientHighlightIntensityPrefix.
        static void InstallAmbientIntensityScale(Harmony harmony)
        {
            var target = AccessTools.Method(typeof(AmbientLight), nameof(AmbientLight.SetSH));
            if (Verify(target, typeof(AmbientLight), nameof(AmbientLight.SetSH)))
            {
                var prefix = AccessTools.Method(typeof(WeatherRig), nameof(WeatherRig.AmbientIntensityPrefix));
                harmony.Patch(target, prefix: new HarmonyMethod(prefix));
                Plugin.Log.LogInfo("[ArenaMapPort][Harmony] patched AmbientLight.SetSH (Lighting AmbientIntensity scale)");
            }

            var htarget = AccessTools.Method(typeof(AmbientHighlight), nameof(AmbientHighlight.SetSH));
            if (Verify(htarget, typeof(AmbientHighlight), nameof(AmbientHighlight.SetSH)))
            {
                var hprefix = AccessTools.Method(typeof(WeatherRig), nameof(WeatherRig.AmbientHighlightIntensityPrefix));
                harmony.Patch(htarget, prefix: new HarmonyMethod(hprefix));
                Plugin.Log.LogInfo("[ArenaMapPort][Harmony] patched AmbientHighlight.SetSH (Lighting AmbientIntensity scale)");
            }
        }

        // Lighting Lab candidate B — AmbientLight.method_2(), public instance, no args (the task brief's
        // own decompile findings call it "private"; re-verified this pass, it is actually `public` —
        // resolves identically either way via AccessTools.Method, noted for the record only).
        // Verified: D:/SPT41/Development/_decompiled/AmbientLight.cs:389.
        static void InstallLightingLabReflectionOverride(Harmony harmony)
        {
            var target = AccessTools.Method(typeof(AmbientLight), "method_2");

            if (!Verify(target, typeof(AmbientLight), "method_2"))
            {
                return;
            }

            var postfix = AccessTools.Method(typeof(LightingLab), nameof(LightingLab.ReflectionPostfix));
            harmony.Patch(target, postfix: new HarmonyMethod(postfix));
            Plugin.Log.LogInfo("[ArenaMapPort][Harmony] patched AmbientLight.method_2 (Lighting Lab candidate B)");
        }

        // Lighting Lab candidate D — RainController.AbstractState.vmethod_15(in Vector3, float, float,
        // float, float), public virtual, nested. AbstractStateSummer explicitly calls
        // base.vmethod_15(...) first (a non-virtual base-call), and no AbstractStateWinter subclass
        // overrides it, so patching THIS base method body catches every season state.
        // Verified: D:/SPT41/Development/_decompiled/RainController.cs:142.
        static void InstallLightingLabWaterLevelOverride(Harmony harmony)
        {
            var target = AccessTools.Method(typeof(RainController.AbstractState), "vmethod_15");

            if (!Verify(target, typeof(RainController.AbstractState), "vmethod_15"))
            {
                return;
            }

            var postfix = AccessTools.Method(typeof(LightingLab), nameof(LightingLab.WaterLevelPostfix));
            harmony.Patch(target, postfix: new HarmonyMethod(postfix));
            Plugin.Log.LogInfo("[ArenaMapPort][Harmony] patched RainController.AbstractState.vmethod_15 " +
                                "(Lighting Lab candidate D)");
        }

        // donor-settings-camera-weather §D — EFT.Weather.WeatherController.SetWeatherNodes(WeatherNode[]),
        // public instance, single overload. Verified:
        // D:/SPT41/Development/_decompiled/EFT.Weather/WeatherController.cs:99.
        static void InstallWeatherTestOverride(Harmony harmony)
        {
            var target = AccessTools.Method(typeof(EFT.Weather.WeatherController), "SetWeatherNodes");

            if (!Verify(target, typeof(EFT.Weather.WeatherController), "SetWeatherNodes"))
            {
                return;
            }

            var postfix = AccessTools.Method(typeof(WeatherTestOverride), "Postfix");
            harmony.Patch(target, postfix: new HarmonyMethod(postfix));
            Plugin.Log.LogInfo("[ArenaMapPort][Harmony] patched EFT.Weather.WeatherController.SetWeatherNodes " +
                                "(donor-settings-camera-weather §D)");
        }

        // donor-settings-camera-weather §C.3 — the nested RainController.AbstractStateSummer.SetWetting
        // (float, float, Vector2, float), public override. Verified:
        // D:/SPT41/Development/_decompiled/RainController.cs (AbstractStateSummer.SetWetting).
        static void InstallWetRendererDamper(Harmony harmony)
        {
            var summer = AccessTools.Inner(typeof(RainController), "AbstractStateSummer");
            var target = summer != null
                ? AccessTools.Method(summer, "SetWetting", new[] { typeof(float), typeof(float), typeof(UnityEngine.Vector2), typeof(float) })
                : null;

            if (!Verify(target, typeof(RainController), "AbstractStateSummer.SetWetting"))
            {
                return;
            }

            var postfix = AccessTools.Method(typeof(RainWetnessRepairs), "Postfix");
            harmony.Patch(target, postfix: new HarmonyMethod(postfix));
            Plugin.Log.LogInfo("[ArenaMapPort][Harmony] patched RainController.AbstractStateSummer.SetWetting " +
                                "(donor-settings-camera-weather §C.3)");
        }

        // donor-settings-camera-weather §12 goal 3 — DepthPhotograper.SetupDepthCamera(), public instance,
        // no args, single overload. Verified: D:/SPT41/Development/_decompiled/DepthPhotograper.cs:117.
        static void InstallRainCoverDepthMask(Harmony harmony)
        {
            var target = AccessTools.Method(typeof(DepthPhotograper), nameof(DepthPhotograper.SetupDepthCamera));

            if (!Verify(target, typeof(DepthPhotograper), nameof(DepthPhotograper.SetupDepthCamera)))
            {
                return;
            }

            var postfix = AccessTools.Method(typeof(RainCoverRepairs), nameof(RainCoverRepairs.SetupDepthCameraPostfix));
            harmony.Patch(target, postfix: new HarmonyMethod(postfix));
            Plugin.Log.LogInfo("[ArenaMapPort][Harmony] patched DepthPhotograper.SetupDepthCamera (§12/§13 rain cover mask + diagnostics)");

            // DepthPhotograper.Render(), public instance, no args (DepthPhotograper.cs:55). The prefix computes
            // RainBounds/cover layers on the raid's first camera change, when every scene is loaded.
            var render = AccessTools.Method(typeof(DepthPhotograper), nameof(DepthPhotograper.Render));
            if (!Verify(render, typeof(DepthPhotograper), nameof(DepthPhotograper.Render)))
            {
                return;
            }
            var prefix = AccessTools.Method(typeof(RainCoverRepairs), nameof(RainCoverRepairs.RenderPrefix));
            harmony.Patch(render, prefix: new HarmonyMethod(prefix));
            Plugin.Log.LogInfo("[ArenaMapPort][Harmony] patched DepthPhotograper.Render (§12 rain cover bounds)");

            // §13 goal 2 — DepthPhotograper.RenderObstacles(), public instance, no args, single overload
            // (DepthPhotograper.cs:138), verified §1 of the §13 investigation. Draws elevated meshes onto
            // the depth camera (RainCoverMode.RoofDepthBlack only) before RenderObstacles()' own,
            // unmodified body calls _depthCamera.Render() a few lines later.
            var renderObstacles = AccessTools.Method(typeof(DepthPhotograper), nameof(DepthPhotograper.RenderObstacles));
            if (!Verify(renderObstacles, typeof(DepthPhotograper), nameof(DepthPhotograper.RenderObstacles)))
            {
                return;
            }
            var obstaclesPrefix = AccessTools.Method(typeof(RainCoverRepairs), nameof(RainCoverRepairs.RenderObstaclesPrefix));
            harmony.Patch(renderObstacles, prefix: new HarmonyMethod(obstaclesPrefix));
            Plugin.Log.LogInfo("[ArenaMapPort][Harmony] patched DepthPhotograper.RenderObstacles (§13 rain cover v2 — roof draws)");
        }

        // daytime-and-visuals F1 — LocalGame.InitWeather(TimeAndWeatherSettings), public instance,
        // inherited from BaseLocalGame<EftGamePlayerOwner> and never overridden by LocalGame itself.
        // AccessTools.Method resolves it via the concrete closed type: InitWeather is public, and
        // Type.GetMethod (which AccessTools.Method wraps) already searches the full inheritance chain
        // for public instance members without needing FlattenHierarchy — unlike the open generic
        // BaseLocalGame<>, which is never a runtime-patchable method target.
        // Verified: D:/SPT41/Development/_decompiled/EFT/BaseLocalGame.cs:489; EFT/LocalGame.cs:29.
        static void InstallTimeOfDayFix(Harmony harmony)
        {
            var target = AccessTools.Method(typeof(LocalGame), nameof(LocalGame.InitWeather));

            if (!Verify(target, typeof(LocalGame), nameof(LocalGame.InitWeather)))
            {
                return;
            }

            var prefix = AccessTools.Method(typeof(TimeOfDayRepairs), nameof(TimeOfDayRepairs.Prefix));
            harmony.Patch(target, prefix: new HarmonyMethod(prefix));
            Plugin.Log.LogInfo("[ArenaMapPort][Harmony] patched LocalGame.InitWeather (daytime-and-visuals F1)");
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
            var postfix = AccessTools.Method(typeof(SpatialAudioInitSkipPrefix), "Postfix");
            harmony.Patch(target, prefix: new HarmonyMethod(prefix), postfix: new HarmonyMethod(postfix));
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

        // Stage 1 (2026-09-20) — EffectsController.OnHealthDied(EDamageType), public instance, single overload.
        // Verified: D:/SPT41/Development/_decompiled/EffectsController.cs:1488.
        static void InstallDeathEffectsGuard(Harmony harmony)
        {
            var target = AccessTools.Method(typeof(EffectsController), "OnHealthDied");
            if (!Verify(target, typeof(EffectsController), "OnHealthDied"))
            {
                return;
            }
            var prefix = AccessTools.Method(typeof(DeathScreenEffectsGuard), "Prefix");
            harmony.Patch(target, prefix: new HarmonyMethod(prefix));
            Plugin.Log.LogInfo("[ArenaMapPort][Harmony] patched EffectsController.OnHealthDied (stage 1 death-effects guard)");
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

        internal static bool Verify(MethodBase method, Type declaringType, string methodName)
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
