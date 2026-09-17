using System;
using System.Collections;
using System.Collections.Generic;
using ArenaMapPort.Shared;
using Comfort.Common;
using EFT.CameraControl;
using EFT.Weather;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace ArenaMapPort.Client
{
    /// <summary>
    /// Weather rig, Stage 0 (Bay5 prototype). See docs/progress/weather-rig-stage0.md and the
    /// implementation spec it was built from for the full evidence trail. Short version:
    ///
    ///  - LIGHTING-DESIGN.md's Candidate B: harvest a vanilla SPT built-in "&lt;Donor&gt;_Scripts" scene's
    ///    "Weather" (full rig) or "Weather_Simple" (simplified) root, deep-clone it, unload the donor
    ///    scene, keep the clone inactive on a DontDestroyOnLoad host, and toggle it active/inactive per
    ///    raid — so <see cref="EFT.Weather.WeatherController"/>/<c>TOD_Sky</c>/<c>AmbientLight</c> feed
    ///    this map's ambient/sky/fog instead of the flat LevelSettings ambient
    ///    <see cref="ArenaLightingRepairs"/> stands in for on every other map.
    ///  - Stage 0 scope: <c>arena_bay5</c> only, Donor = "Reserve_Base_Scripts" (level139, full rig — the
    ///    smallest full-rig donor surveyed; Laboratory's "Weather_Simple" is smaller but has no dome/sun,
    ///    the wrong shape for an outdoor map). Every other map's Donor defaults to "Off" — a complete
    ///    no-op, byte-identical to today's ArenaLightingRepairs-only behavior.
    ///  - Harvest pre-flight gate (<see cref="PreflightClear"/>): "no raid of ANY kind is currently
    ///    loaded" (not just "not one of ours") — a vanilla SPT raid played earlier in the session authors
    ///    the identical LevelSettings/LocationScene/CullingManager/ObservedCullingManager/
    ///    EnvironmentManagerBase/WeatherController singleton set our own raids do, so harvesting while any
    ///    of those exist risks colliding with them. Six independent, freshly re-decompiled checks; see
    ///    each field's own comment for citations.
    ///  - Activation is a plain <c>SetActive(true)</c>/<c>SetActive(false)</c> on the pre-harvested clone
    ///    from THIS plugin's own Scripts-scene sceneLoaded/sceneUnloaded pair (no new Harmony patch) —
    ///    the clone is instantiated from an INACTIVE donor root so Awake/OnEnable/Start are deferred to
    ///    the SetActive(true) call, which runs synchronously and completes before
    ///    <c>BaseLocalGame.InitWeather</c> can ever see a null <c>WeatherController.Instance</c>. See
    ///    <see cref="OnSceneLoaded"/>'s own comment for the full ordering argument.
    ///  - Review fix-ups (docs/progress/weather-rig-stage0.md, "Review fix-ups"): Unity's own message
    ///    dispatch try-catches each MonoBehaviour's Awake/OnEnable/Start internally and logs to console —
    ///    it never lets such an exception reach the C# caller of SetActive()/Instantiate(). A failed child
    ///    Awake is therefore NOT something the try/catch around <c>rig.Root.SetActive(true)</c> can catch
    ///    by itself; <see cref="VerifyFirstActivation"/> is the actual detector (re-reads a couple of the
    ///    fields Awake() is responsible for setting and throws — a real, catchable C# exception — if they
    ///    look wrong). Deactivation also now explicitly unsubscribes/releases the rig's singletons and
    ///    <c>CameraManager.OnCameraChanged</c> subscription (<see cref="OnSceneUnloaded"/>) — SetActive(false)
    ///    alone does not, and since the clone is reused (never <c>Destroy</c>'d), <c>OnDestroy</c>'s own
    ///    cleanup never fires either; left alone this stale state would silently redirect every OTHER
    ///    map's raid onto this rig's frozen sky/ambient for the rest of the session.
    ///  - Everything this file creates (the clone, its DontDestroyOnLoad host) is brand-new state; every
    ///    donor scene it loads is a vanilla SPT built-in scene already safe to load per
    ///    LIGHTING-DESIGN.md's own distribution/legality note. No project file, bundle, or vanilla file is
    ///    ever written by this file.
    /// </summary>
    internal static class WeatherRig
    {
        internal enum SkyboxMode { Dome, FrozenStatic }
        internal enum RigKind { Full, Simplified }

        const string OffDonor = "Off";
        const float PollIntervalSeconds = 3f;
        const int MaxHarvestAttempts = 5;

        sealed class RigConfigBundle
        {
            internal BepInEx.Configuration.ConfigEntry<string> Donor;
            internal BepInEx.Configuration.ConfigEntry<SkyboxMode> SkyboxModeEntry;
            internal BepInEx.Configuration.ConfigEntry<bool> KeepFillLight;
        }

        // Internal (not the default private) — LightingDiagnosticProbe.LastHarvestResult returns this
        // type, so it must be at least as accessible as that internal method (CS0053 otherwise).
        internal sealed class HarvestedRig
        {
            internal GameObject Root;               // clone root, parented under _host, inactive until raid
            internal RigKind Kind;
            internal WeatherController Controller;   // null for Simplified
            internal TOD_Sky Sky;                    // null for Simplified
            internal TODSkySimple SimpleSky;         // null for Full

            // One-time harvest-health snapshot, surfaced by LightingDiagnosticProbe. A HarvestedRig is
            // only ever constructed on full success (every failure path returns before one is built), so
            // these four are always true when a HarvestedRig exists — kept as explicit fields anyway
            // (rather than that fact being implicit) so the probe's log line is self-explanatory without
            // cross-referencing this file.
            internal bool LoadOk, CloneOk, UnloadOk, ValidationOk;
            internal int KeptComponents;
            internal bool LevelSettingsUnchanged, LocationSceneUnchanged, EnvironmentManagerUnchanged;

            // Review fix-ups #1/#4 — false until the very first successful SetActive(true). Awake/OnEnable/
            // Start only ever run once per clone lifetime (the clone is reused, never Destroy()'d), so this
            // tells OnSceneLoaded whether to run VerifyFirstActivation's Awake-postcondition check (only
            // meaningful the first time) versus just ReestablishSingletons (needed every time after).
            internal bool EverActivated;
        }

        static readonly Dictionary<string, RigConfigBundle> _configs = new Dictionary<string, RigConfigBundle>();
        static readonly Dictionary<string, HarvestedRig> _harvested = new Dictionary<string, HarvestedRig>(); // keyed by donor scene name
        static readonly Dictionary<string, int> _failedAttempts = new Dictionary<string, int>();              // keyed by donor scene name
        static readonly HashSet<string> _permanentlyFailed = new HashSet<string>();                            // donor scene names
        static readonly HashSet<string> _pendingDonors = new HashSet<string>();                                // donor scene names still to try
        static GameObject _host;
        static bool _installed;
        static bool _loopRunning; // guards OnDonorConfigChanged from starting a second, overlapping HarvestLoop
        static ArenaMapDescriptor _activeMap; // map this raid's clone was activated for, or null

        // Review fix-ups #5 — the rig OnSceneLoaded actually activated for _activeMap, tracked directly
        // instead of re-derived from the live Donor ConfigEntry at unload time (which can name a
        // different/not-yet-harvested donor if it was edited between this raid's load and unload).
        static HarvestedRig _activeRig;

        internal static void Install()
        {
            if (_installed)
            {
                return;
            }
            _installed = true;

            _host = new GameObject("ArenaMapPort_WeatherRigHost") { hideFlags = HideFlags.HideAndDontSave };
            UnityEngine.Object.DontDestroyOnLoad(_host);

            foreach (var map in ArenaMapRegistry.All)
            {
                _configs[map.Key] = BindConfig(map);
            }
            RecomputePendingDonors();

            SceneManager.sceneLoaded += OnSceneLoaded;
            SceneManager.sceneUnloaded += OnSceneUnloaded;
            _host.AddComponent<Runner>().StartCoroutine(HarvestLoop());
        }

        sealed class Runner : MonoBehaviour { }

        // ---------------------------------------------------------------------------------------------
        // Config binding — one new RigConfigBundle per map, same "Lighting (<key>)" section
        // ArenaLightingRepairs.BindConfig already uses (ArenaLightingRepairs.cs) — BepInEx allows several
        // classes to bind distinct keys into one section; "Donor"/"SkyboxMode"/"KeepFillLight" never
        // collide with ArenaLightingRepairs' own key names.
        // ---------------------------------------------------------------------------------------------
        static RigConfigBundle BindConfig(ArenaMapDescriptor map)
        {
            string section = $"Lighting ({map.Key})";
            string defaultDonor = map.Key == "arena_bay5" ? "Reserve_Base_Scripts" : OffDonor;
            var cfg = new RigConfigBundle();

            cfg.Donor = Plugin.Instance.Config.Bind(section, "Donor", defaultDonor,
                new BepInEx.Configuration.ConfigDescription(
                    "Vanilla SPT built-in scene ('<Name>_Scripts', BuildSettings scene, e.g. " +
                    "'Reserve_Base_Scripts') whose Weather/Weather_Simple subtree is harvested once per " +
                    $"process and toggled on for this map's raids. '{OffDonor}' = today's behavior " +
                    "(ArenaLightingRepairs workaround only, no rig). Takes effect on the NEXT raid — never " +
                    "hot-applied mid-raid."));

            cfg.SkyboxModeEntry = Plugin.Instance.Config.Bind(section, "SkyboxMode", SkyboxMode.Dome,
                new BepInEx.Configuration.ConfigDescription(
                    "Dome = the donor's real procedural TOD_Sky dome renders. FrozenStatic = disable the " +
                    "dome's painter GameObjects (Atmosphere/Clear/Space/Sun/Moon/cloud renderer) if the dome " +
                    "renders with a horizon seam or a flat white/black atmosphere (Icebreaker's own documented " +
                    "failure mode for a cross-build sky rip) — ambient SH, reflections, fog and wind/rain keep " +
                    "running either way. Only read when Donor != 'Off'. Takes effect on the NEXT raid."));

            cfg.KeepFillLight = Plugin.Instance.Config.Bind(section, "KeepFillLight", false,
                new BepInEx.Configuration.ConfigDescription(
                    "When a rig is active for this map (Donor != 'Off' AND harvested successfully in time for " +
                    "this raid), ArenaLightingRepairs' own FillLightIntensity is forced to 0 regardless of its " +
                    "own value, since the rig's real AmbientLight/_SHAr now feeds ambient instead. Set true to " +
                    "keep the fill light on top of the real rig (e.g. as extra interior fill)."));

            // Deliberately NO SettingChanged handlers on these three for re-apply purposes — "changes apply
            // on the next raid" is a hard requirement here (unlike ArenaLightingRepairs' own live-tunable
            // knobs): re-toggling SetActive or re-running the harvest mid-raid is not supported. The one
            // exception: Donor's own SettingChanged pre-warms the harvest for a newly-typed donor name
            // between raids, never reacting during an active one.
            cfg.Donor.SettingChanged += (_, __) => OnDonorConfigChanged();

            return cfg;
        }

        // ---------------------------------------------------------------------------------------------
        // Harvest pre-flight gate + retry bookkeeping.
        // ---------------------------------------------------------------------------------------------

        // "No raid of any kind is currently loaded" — a vanilla SPT raid played earlier this session
        // authors the identical singleton/manager set our own maps do, so gating on
        // !ArenaMapRaid.IsLoaded() alone is insufficient. All six re-verified fresh against the
        // decompile (Comfort.dll ilspycmd -t; LocationScene.cs; CullingManager.cs; ObservedCullingManager
        // .cs; LevelSettings.cs; EnvironmentManagerBase.cs + EFT.EnvironmentEffect/EnvironmentManager.cs;
        // EFT.Weather/WeatherController.cs).
        static bool PreflightClear() =>
            !Singleton<LevelSettings>.Instantiated &&
            LocationScene.LoadedScenes.Count == 0 &&
            CullingManager.Instance == null &&
            !Singleton<ObservedCullingManager>.Instantiated &&
            EnvironmentManagerBase.Instance == null &&
            EFT.Weather.WeatherController.Instance == null;

        // Re-reads every map's current Donor value; any name not already harvested/permanently failed and
        // not "Off" is (re-)added to _pendingDonors. Called once at Install() and again whenever a Donor
        // ConfigEntry changes (OnDonorConfigChanged) so a tester can pre-warm a newly-typed donor between
        // raids without restarting the game.
        static void RecomputePendingDonors()
        {
            foreach (var cfg in _configs.Values)
            {
                string donor = cfg.Donor.Value;
                if (donor == OffDonor || _harvested.ContainsKey(donor) || _permanentlyFailed.Contains(donor))
                {
                    continue;
                }
                _pendingDonors.Add(donor);
            }
        }

        static void OnDonorConfigChanged()
        {
            int before = _pendingDonors.Count;
            RecomputePendingDonors();
            // Only (re)start the loop if it isn't already running — two concurrent HarvestLoop coroutines
            // could both snapshot _pendingDonors on the same tick and both attempt HarvestOne for the same
            // donor at once (SceneManager.LoadScene is not meant to be re-entered for the same scene
            // concurrently). If the loop is still running, it will pick the new donor up on its own next
            // pass — RecomputePendingDonors already added it to the live _pendingDonors set above.
            if (_pendingDonors.Count > before && !_loopRunning)
            {
                _host.AddComponent<Runner>().StartCoroutine(HarvestLoop());
            }
        }

        // ---------------------------------------------------------------------------------------------
        // Harvest coroutine. Retry loop, not a one-shot check: polls every PollIntervalSeconds; a donor
        // stops being retried after MaxHarvestAttempts consecutive FAILED harvest attempts (load/clone/
        // unload threw or produced a corrupted result) — not after any number of ticks where the gate was
        // simply not clear yet, which can legitimately last the whole length of a raid.
        // ---------------------------------------------------------------------------------------------
        static IEnumerator HarvestLoop()
        {
            _loopRunning = true;
            try
            {
                while (_pendingDonors.Count > 0)
                {
                    yield return new WaitForSeconds(PollIntervalSeconds);

                    // Snapshot: iterating a live HashSet while HarvestOne can mutate it (success removes
                    // the entry) needs a copy.
                    var attempt = new List<string>(_pendingDonors);
                    foreach (var donorName in attempt)
                    {
                        if (!PreflightClear())
                        {
                            continue; // expected during an active raid — no log, this is the common case
                        }
                        yield return HarvestOne(donorName);
                    }
                }
                Plugin.Log.LogInfo("[ArenaMapPort][WeatherRig] harvest loop finished — " +
                    $"{_harvested.Count} donor(s) ready, {_permanentlyFailed.Count} permanently failed this session.");
            }
            finally
            {
                _loopRunning = false;
            }
        }

        static IEnumerator HarvestOne(string donorName)
        {
            Scene donorScene = default;
            GameObject weatherRoot = null;
            GameObject clone = null;

            // Pre-load snapshot for the post-harvest identity check. Must be taken BEFORE the donor loads:
            // the donor's own LevelSettings/LocationScene/EnvironmentManager register during its load and
            // release again on unload, so a snapshot taken after loading always reads as "changed".
            bool lsBefore = Singleton<LevelSettings>.Instantiated;
            int locBefore = LocationScene.LoadedScenes.Count;
            bool envBefore = EnvironmentManagerBase.Instance != null;

            // The donor's LevelSettings.Awake pushes its fog/ambient/skybox into RenderSettings
            // (LevelSettings.cs:194-218) and nothing resets them on unload; restore them afterwards.
            var renderSettingsBefore = RenderSettingsSnapshot.Capture();

            // Unload any copy of the donor left loaded by an earlier failed attempt this session.
            foreach (var leftover in LoadedScenesNamed(donorName))
            {
                AsyncOperation leftoverOp = null;
                try
                {
                    leftoverOp = SceneManager.UnloadSceneAsync(leftover);
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning($"[ArenaMapPort][WeatherRig] could not unload leftover '{donorName}': " +
                        $"{e.GetType().Name}: {e.Message}");
                }
                if (leftoverOp != null)
                {
                    yield return leftoverOp;
                }
            }

            // Step 1: load. SceneManager.LoadScene only loads on the NEXT frame (the returned Scene is not
            // loaded yet), so use LoadSceneAsync and wait for it. It returns null (and Unity logs an error)
            // when the name is not a BuildSettings scene of this client.
            AsyncOperation loadOp = null;
            try
            {
                loadOp = SceneManager.LoadSceneAsync(donorName, new LoadSceneParameters(LoadSceneMode.Additive));
                if (loadOp == null)
                {
                    throw new InvalidOperationException("LoadSceneAsync returned null");
                }
            }
            catch (Exception e)
            {
                Fail(donorName, $"load failed: {e.GetType().Name}: {e.Message} " +
                    "(donor scene name not in this client's BuildSettings? typo in the Donor config?)");
                yield break;
            }
            yield return loadOp;

            try
            {
                var loaded = LoadedScenesNamed(donorName);
                donorScene = loaded.Count > 0 ? loaded[loaded.Count - 1] : default;
                if (!donorScene.IsValid() || !donorScene.isLoaded)
                {
                    throw new InvalidOperationException("scene not loaded after LoadSceneAsync completed");
                }
            }
            catch (Exception e)
            {
                Fail(donorName, $"load failed: {e.GetType().Name}: {e.Message}");
                yield break;
            }

            // Step 2: find the rig root by name — "Weather" (full) or "Weather_Simple" (simplified).
            try
            {
                foreach (var root in donorScene.GetRootGameObjects())
                {
                    if (root.name == "Weather" || root.name == "Weather_Simple")
                    {
                        weatherRoot = root;
                        break;
                    }
                }
                if (weatherRoot == null)
                {
                    throw new InvalidOperationException("no 'Weather' or 'Weather_Simple' root GameObject found");
                }
            }
            catch (Exception e)
            {
                Fail(donorName, $"rig-root lookup failed: {e.GetType().Name}: {e.Message}");
                SafeUnload(donorScene);
                renderSettingsBefore.Restore();
                yield break;
            }

            // Step 4: SetActive(false) on the DONOR original before cloning, so the CLONE's own Awake/
            // OnEnable/Start are deferred until WE SetActive(true) it later (once the FPS camera/raid
            // context exists). This does NOT undo the donor original's own Awake, which already ran once
            // as part of the normal additive scene load in Step 1 — safe because the pre-flight gate
            // guarantees nothing of ours existed for it to collide with.
            int keptBefore = 0;
            TOD_Sky cloneSky = null;
            TODSkySimple cloneSimple = null;
            try
            {
                weatherRoot.SetActive(false);
                clone = UnityEngine.Object.Instantiate(weatherRoot);
                clone.name = "WeatherRig_" + donorName;
                keptBefore = clone.GetComponentsInChildren<MonoBehaviour>(true).Length;

                // Defensive: register the clone's own TOD_Sky/TODSkySimple with TODSkyProvider immediately,
                // even while the clone is still inactive — closes the "TODSkyProvider.Instance left
                // dangling at the soon-to-be-destroyed donor original" window. Harmless: it is a plain
                // static setter with no active-object requirement (TODSkyProvider.cs). TOD_Sky.Awake()
                // re-registers itself anyway once the clone is later activated for real.
                cloneSky = clone.GetComponentInChildren<TOD_Sky>(true);
                cloneSimple = cloneSky == null ? clone.GetComponentInChildren<TODSkySimple>(true) : null;
                if (cloneSky != null)
                {
                    TODSkyProvider.RegisterInstance(cloneSky);
                }
                else if (cloneSimple != null)
                {
                    TODSkyProvider.RegisterInstance(cloneSimple);
                }
                else
                {
                    throw new InvalidOperationException("clone has neither TOD_Sky nor TODSkySimple");
                }

                // DontDestroyOnLoad BEFORE SetParent: Object.DontDestroyOnLoad only accepts a ROOT
                // GameObject (logs an error and no-ops otherwise) — clone is still root at this point,
                // fresh from Instantiate. Reparenting under _host afterward keeps it in that same
                // DontDestroyOnLoad scene (a child of an already-DontDestroyOnLoad object persists with
                // it), so the explicit call here is defensive/order-correct rather than load-bearing on
                // its own, but calling it AFTER SetParent (clone no longer root) would instead log a
                // Unity error every harvest for no effect.
                UnityEngine.Object.DontDestroyOnLoad(clone);
                clone.transform.SetParent(_host.transform, worldPositionStays: false);

                // Post-clone cross-reference validation — the load-bearing safety net for any future donor
                // whose rig doesn't self-contain everything under one root the way Reserve_Base_Scripts
                // does (WeatherController/CloudController/the full TOD_* rig/RainController/AmbientLight/
                // AmbientHighlight/ReflectionProbe are all already descendants of "Weather" for that donor,
                // confirmed by a live dump_hierarchy.py pass — Instantiate() correctly remaps in-hierarchy
                // references, standard Unity behavior, so this check should always pass for that donor;
                // it exists for whatever donor Stage 1/2 adds next).
                if (cloneSky != null)
                {
                    var ctrl = clone.GetComponentInChildren<WeatherController>(true);
                    if (ctrl == null)
                    {
                        throw new InvalidOperationException("no WeatherController on the clone");
                    }
                    if (ctrl.WindController == null || ctrl.RainController == null || ctrl.CloudController == null)
                    {
                        throw new InvalidOperationException("WeatherController's Wind/Rain/CloudController is " +
                            "null post-clone — cross-root reference did not survive Instantiate");
                    }
                    if (ctrl.TimeOfDayController.AmbientLightScript == null)
                    {
                        // ToDController.Update() calls AmbientLightScript.SetSH(SH) with NO null check
                        // (unlike its sibling AmbientHighlightScript, which IS guarded) — a broken
                        // cross-reference here would NRE every LateUpdate once the rig activates, not
                        // degrade gracefully. Caught here, before ever activating this clone for a raid.
                        throw new InvalidOperationException("ToDController.AmbientLightScript is null " +
                            "post-clone — would NRE every LateUpdate once active (unguarded call site)");
                    }
                }

                clone.SetActive(false); // stays inactive — activation is per-raid, see OnSceneLoaded
            }
            catch (Exception e)
            {
                Fail(donorName, $"clone/validate failed: {e.GetType().Name}: {e.Message}");
                if (clone != null)
                {
                    UnityEngine.Object.Destroy(clone);
                }
                SafeUnload(donorScene);
                renderSettingsBefore.Restore();
                yield break;
            }

            // Step 6: unload — MUST be awaited so the harvested-flag only flips once the donor's own
            // singletons have actually released (LevelSettings/LocationScene/etc. OnDestroy calls fire
            // synchronously as part of UnloadSceneAsync's own destroy pass, but this still waits for the
            // operation itself to report done before trusting post-unload state).
            AsyncOperation unloadOp = null;
            try
            {
                unloadOp = SceneManager.UnloadSceneAsync(donorScene);
            }
            catch (Exception e)
            {
                Fail(donorName, $"UnloadSceneAsync threw synchronously: {e.GetType().Name}: {e.Message}");
                UnityEngine.Object.Destroy(clone);
                yield break;
            }
            if (unloadOp != null)
            {
                yield return unloadOp; // the actual await — a Unity coroutine yield on the AsyncOperation
            }

            // Step 7: post-unload identity check + the one confirmed-persistent side effect: rebuild the
            // physics broadphase regions the donor's own load/unload disturbed. Never reverted by anything
            // else in this repo (AuthorScriptsScene.cs never authors a LevelPhysicsSettings of its own).
            renderSettingsBefore.Restore();
            bool lsUnchanged = Singleton<LevelSettings>.Instantiated == lsBefore;
            bool locUnchanged = LocationScene.LoadedScenes.Count == locBefore;
            bool envUnchanged = (EnvironmentManagerBase.Instance != null) == envBefore;
            try
            {
                Physics.RebuildBroadphaseRegions(new Bounds(Vector3.zero, Vector3.one * 2000f), 1);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[ArenaMapPort][WeatherRig] physics-broadphase revert failed (non-fatal): " +
                    $"{e.GetType().Name}: {e.Message}");
            }

            // Review fix-ups #6: these three flags used to be computed and logged but never enforced — a
            // donor whose LevelSettings/LocationScene/EnvironmentManager genuinely leaked past
            // UnloadSceneAsync's own completion (exactly the scenario the pre-flight gate exists to keep
            // from ever colliding with) was still marked ValidationOk=true and made eligible for per-raid
            // activation with corrupted world state. Route it through Fail() instead, same as any other
            // harvest failure — it counts toward MaxHarvestAttempts and eventually permanently fails the
            // donor rather than silently shipping a broken rig.
            if (!lsUnchanged || !locUnchanged || !envUnchanged)
            {
                UnityEngine.Object.Destroy(clone);
                Fail(donorName, "post-unload identity check failed: " +
                    $"LevelSettings-unchanged={lsUnchanged} LocationScene-unchanged={locUnchanged} " +
                    $"EnvironmentManager-unchanged={envUnchanged} — the donor scene's own singletons/" +
                    "registries did not fully release after UnloadSceneAsync completed");
                yield break;
            }

            var rig = new HarvestedRig
            {
                Root = clone,
                Kind = cloneSky != null ? RigKind.Full : RigKind.Simplified,
                Controller = cloneSky != null ? clone.GetComponentInChildren<WeatherController>(true) : null,
                Sky = cloneSky,
                SimpleSky = cloneSimple,
                LoadOk = true,
                CloneOk = true,
                UnloadOk = true,
                ValidationOk = true,
                KeptComponents = keptBefore,
                LevelSettingsUnchanged = lsUnchanged,
                LocationSceneUnchanged = locUnchanged,
                EnvironmentManagerUnchanged = envUnchanged,
            };
            _harvested[donorName] = rig;
            _pendingDonors.Remove(donorName);
            _failedAttempts.Remove(donorName);

            EnsureOnRenderObjectManager(); // donor-independent; early attempt — see its own comment on the CameraManager.Exist gate; retried again from OnSceneLoaded

            Plugin.Log.LogInfo($"[ArenaMapPort][WeatherRig] harvested '{donorName}' ({rig.Kind}) — " +
                $"{keptBefore} MonoBehaviour(s) on the clone; post-unload identity unchanged: " +
                $"LevelSettings={lsUnchanged} LocationScene={locUnchanged} EnvironmentManager={envUnchanged}");
        }

        static void Fail(string donorName, string reason)
        {
            int n = _failedAttempts.TryGetValue(donorName, out var c) ? c + 1 : 1;
            _failedAttempts[donorName] = n;
            Plugin.Log.LogError($"[ArenaMapPort][WeatherRig] '{donorName}' harvest attempt {n}/{MaxHarvestAttempts} " +
                $"failed: {reason}");
            if (n >= MaxHarvestAttempts)
            {
                _pendingDonors.Remove(donorName);
                _permanentlyFailed.Add(donorName);
                Plugin.Log.LogError($"[ArenaMapPort][WeatherRig] giving up on '{donorName}' after {n} failed " +
                    "attempts this session — every map configured with this Donor falls back to the existing " +
                    "ArenaLightingRepairs workaround for the rest of this session.");
            }
        }

        // RenderSettings fields LevelSettings.ApplySettings writes (LevelSettings.cs:194-218).
        struct RenderSettingsSnapshot
        {
            AmbientMode _ambientMode;
            Color _ambientSky, _ambientEquator, _ambientGround, _ambientLight;
            float _ambientIntensity;
            bool _fog;
            Color _fogColor;
            float _fogDensity, _fogStart, _fogEnd;
            FogMode _fogMode;
            Material _skybox;

            internal static RenderSettingsSnapshot Capture() => new RenderSettingsSnapshot
            {
                _ambientMode = RenderSettings.ambientMode,
                _ambientSky = RenderSettings.ambientSkyColor,
                _ambientEquator = RenderSettings.ambientEquatorColor,
                _ambientGround = RenderSettings.ambientGroundColor,
                _ambientLight = RenderSettings.ambientLight,
                _ambientIntensity = RenderSettings.ambientIntensity,
                _fog = RenderSettings.fog,
                _fogColor = RenderSettings.fogColor,
                _fogDensity = RenderSettings.fogDensity,
                _fogStart = RenderSettings.fogStartDistance,
                _fogEnd = RenderSettings.fogEndDistance,
                _fogMode = RenderSettings.fogMode,
                _skybox = RenderSettings.skybox,
            };

            internal void Restore()
            {
                RenderSettings.ambientMode = _ambientMode;
                RenderSettings.ambientSkyColor = _ambientSky;
                RenderSettings.ambientEquatorColor = _ambientEquator;
                RenderSettings.ambientGroundColor = _ambientGround;
                RenderSettings.ambientLight = _ambientLight;
                RenderSettings.ambientIntensity = _ambientIntensity;
                RenderSettings.fog = _fog;
                RenderSettings.fogColor = _fogColor;
                RenderSettings.fogDensity = _fogDensity;
                RenderSettings.fogStartDistance = _fogStart;
                RenderSettings.fogEndDistance = _fogEnd;
                if (_fogMode != 0)
                {
                    RenderSettings.fogMode = _fogMode;
                }
                RenderSettings.skybox = _skybox;
            }
        }

        static List<Scene> LoadedScenesNamed(string sceneName)
        {
            var result = new List<Scene>();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (scene.isLoaded && scene.name == sceneName)
                {
                    result.Add(scene);
                }
            }
            return result;
        }

        static void SafeUnload(Scene scene)
        {
            try
            {
                if (scene.IsValid() && scene.isLoaded)
                {
                    SceneManager.UnloadSceneAsync(scene);
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[ArenaMapPort][WeatherRig] cleanup unload also failed: " +
                    $"{e.GetType().Name}: {e.Message}");
            }
        }

        // ---------------------------------------------------------------------------------------------
        // OnRenderObjectManager provision. Lives under the donor's separate "_TechnicalManagers" root
        // (not under "Weather", so it is never part of the clone), its Start() subscribes to
        // OnRenderObjectConnector's static Add/Remove events and does one FindObjectsOfType scan — order
        // relative to AmbientLight.Start() does not matter (self-healing), so AddComponent, not clone.
        //
        // Review fix-ups #2: vanilla's own OnRenderObjectManager.OnRenderObject() dereferences
        // `CameraManager.Instance.Camera`/`...OpticCameraManager.Camera` with NO `CameraManager.Exist`
        // guard (unlike AmbientLight/AudioSourceContainer/TODSkySimple/SoundSuspensionController/
        // AudioUtils/GamePlayerOwner, every one of which gates on `CameraManager.Exist` first —
        // freshly re-verified against the decompile). `CameraManager.Instance` itself can never be null
        // (`instance ?? (instance = new CameraManager())` — it auto-vivifies), so that specific NRE this
        // file's own progress doc originally worried about cannot happen; the real risk is the OPPOSITE
        // problem: OnRenderObject() fires on this component every rendered frame once added, and a
        // freshly auto-vivified "cold" CameraManager's `OpticCameraManager` (a get-only auto-property with
        // no discoverable constructor in the decompile) is null, so
        // `instance.OpticCameraManager.Camera` would NRE continuously, not intermittently, from the
        // moment this component is added if that happens before any real camera/raid setup has ever run
        // `CameraManager.Instance` for real (which is exactly when this used to be called — right after
        // the very first harvest, at the main menu, before any raid). Gated on `CameraManager.Exist` here
        // (the idiomatic vanilla pattern) and retried from <see cref="OnSceneLoaded"/> on every raid
        // activation, since a live raid should always have a real, non-"cold" CameraManager by then.
        // ---------------------------------------------------------------------------------------------
        static void EnsureOnRenderObjectManager()
        {
            if (UnityEngine.Object.FindObjectOfType<OnRenderObjectManager>() != null)
            {
                return; // already present (defensive — no donor's clone includes _TechnicalManagers by design)
            }
            if (!CameraManager.Exist)
            {
                return; // not yet safe — see this method's own comment; retried on the next call site
            }
            _host.AddComponent<OnRenderObjectManager>();
            Plugin.Log.LogInfo("[ArenaMapPort][WeatherRig] added OnRenderObjectManager (lives outside any " +
                "donor's Weather subtree — _TechnicalManagers root).");
        }

        // ---------------------------------------------------------------------------------------------
        // Per-raid activation / deactivation. Reuses this plugin's own Scripts-scene sceneLoaded/
        // sceneUnloaded pair (the same pattern ArenaLightingRepairs/ShaderRebind/LightingDiagnosticProbe
        // already use) rather than a new Harmony prefix on BaseLocalGame<TPlayerOwner>.Create<T>.
        //
        // Ordering argument: Unity dispatches sceneLoaded to every subscriber SYNCHRONOUSLY as part of
        // reporting a scene load's completion, and raid setup's own BaseLocalGame<TPlayerOwner>.Create<T>
        // (which calls InitWeather) is not itself a scene-load callback — it runs only once whatever
        // raid-setup code awaits the scene-load pipeline's own completion, which by construction happens
        // strictly AFTER this handler returns. rig.Root.SetActive(true) below is synchronous: every
        // Awake/OnEnable/Start on the clone (WeatherController, TOD_Sky, AmbientLight, CloudController,
        // WindController, RainController) runs inline, inside this call, before this handler returns — so
        // WeatherController.Instance/MonoBehaviourSingleton&lt;TODSkySimple&gt;.Instance are guaranteed
        // non-null by the time InitWeather's own gate (`if (WeatherController.Instance != null || ...)`)
        // can possibly run. This is the SAME guarantee every other sceneLoaded-based repair in this
        // codebase already depends on for its own, earlier-in-the-pipeline purposes — reusing it here is
        // the minimal-risk choice over a new Harmony prefix. LightingDiagnosticProbe.ProbeWeatherRig's
        // TODSkyProvider.CurrentTime.GameDateTime-set line is the tripwire that would catch this reasoning
        // being wrong in the Stage-0 playtest; a Harmony prefix on Create&lt;T&gt; is the documented (not
        // implemented) fallback if it ever fires.
        //
        // Also install-order-dependent: Plugin.cs calls WeatherRig.Install() BEFORE
        // ArenaLightingRepairs.Install() — both subscribe to the same static sceneLoaded event, and C#
        // multicast delegates invoke subscribers in subscription order, so ArenaLightingRepairs.
        // OnSceneLoaded must read IsActiveForCurrentMap() AFTER this handler has already run on the same
        // dispatch, not before.
        // ---------------------------------------------------------------------------------------------
        static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            var map = ArenaMapRaid.MapForScriptsScene(scene.name); // stateless — see ArenaMapRaid's own doc comment
            if (map == null || scene.name != map.ScriptsSceneName)
            {
                return; // only the Scripts-scene load matters here
            }

            _activeMap = null; // pessimistic default — flips true below only on a clean activation
            var cfg = _configs[map.Key];
            string donorName = cfg.Donor.Value;
            if (donorName == OffDonor)
            {
                return; // today's behavior, unchanged
            }

            if (!_harvested.TryGetValue(donorName, out var rig))
            {
                Plugin.Log.LogWarning($"[ArenaMapPort][WeatherRig] {map.Key}: Donor='{donorName}' configured " +
                    "but not harvested in time for this raid (still pending, or permanently failed this " +
                    "session) — falling back to the ArenaLightingRepairs workaround for this raid.");
                return;
            }

            try
            {
                bool firstActivation = !rig.EverActivated;
                rig.Root.SetActive(true); // synchronous — see this method's own doc comment for the ordering argument

                // Review fix-ups #1: Unity's message dispatch try-catches each MonoBehaviour's own Awake/
                // OnEnable/Start internally and logs to console — it never lets that exception reach us
                // here, so the surrounding try/catch alone cannot detect a failed child Awake. This is the
                // actual detector, and only meaningful the first time this rig is ever activated (Awake
                // never runs again on an already-woken clone) — it re-reads a couple of the fields Awake()
                // is responsible for setting and throws a real, catchable exception if they look wrong.
                if (firstActivation)
                {
                    VerifyFirstActivation(rig);
                }

                // Review fix-ups #4: OnSceneUnloaded's own deactivation below deliberately releases the
                // rig's singletons/event subscription so a stale WeatherController/TOD_Sky can't leak into
                // every OTHER map's raids for the rest of the session — re-establish them here on every
                // activation. Effectively a no-op the very first time (Awake already made the same writes;
                // the -=/+= pair inside guards against ending up double-subscribed), load-bearing from the
                // 2nd activation onward, since Awake does not run again to redo it on its own.
                ReestablishSingletons(rig);
                rig.EverActivated = true;

                if (rig.Kind == RigKind.Full)
                {
                    Shader.EnableKeyword("_WEATHER_DEFINED"); // idempotent; covers a prior deactivate
                }
                _activeMap = map;
                _activeRig = rig; // Review fix-ups #5 — OnSceneUnloaded deactivates THIS, not a re-derived lookup
                EnsureOnRenderObjectManager(); // Review fix-ups #2 — retried here in case CameraManager.Exist was still false at harvest time
                Plugin.Log.LogInfo($"[ArenaMapPort][WeatherRig] {map.Key}: activated '{donorName}' " +
                    $"({rig.Kind}) for this raid. SkyboxMode={cfg.SkyboxModeEntry.Value}.");
                ApplySkyboxMode(rig, cfg.SkyboxModeEntry.Value);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[ArenaMapPort][WeatherRig] {map.Key}: activation failed — " +
                    $"{e.GetType().Name}: {e.Message}. Falling back to the ArenaLightingRepairs workaround " +
                    "for this raid.");
                try { rig.Root.SetActive(false); } catch { /* best-effort */ }
                _activeMap = null;
                _activeRig = null;
            }
        }

        // Review fix-ups #1 — see OnSceneLoaded's own comment for why this, and not the try/catch around
        // SetActive(true), is what actually catches a failed child Awake. Only called on the first-ever
        // activation of a given rig; on later activations Awake does not run again, so these fields would
        // legitimately read back whatever ReestablishSingletons/OnSceneUnloaded last left them as, not
        // Awake's own output — checking them again then would be meaningless.
        static void VerifyFirstActivation(HarvestedRig rig)
        {
            if (rig.Kind == RigKind.Full)
            {
                if (WeatherController.Instance != rig.Controller)
                {
                    throw new InvalidOperationException("post-activation check failed: " +
                        "WeatherController.Instance is not this rig's own controller after SetActive(true) " +
                        "— WeatherController.Awake() likely threw (Unity swallows the exception itself; " +
                        "nothing else sets this field)");
                }
                if (!rig.Sky.Initialized)
                {
                    throw new InvalidOperationException("post-activation check failed: TOD_Sky.Initialized " +
                        "is still false after SetActive(true) — TOD_Sky.Awake()/Initialize() likely threw " +
                        "before reaching the line that sets it");
                }
            }
            else if (!Singleton<TODSkySimple>.Instantiated || Singleton<TODSkySimple>.Instance != rig.SimpleSky)
            {
                throw new InvalidOperationException("post-activation check failed: " +
                    "Singleton<TODSkySimple>.Instance is not this rig's own sky after SetActive(true) — " +
                    "TODSkySimple.Awake() likely threw");
            }
        }

        // Review fix-ups #4 — undoes OnSceneUnloaded's own explicit teardown (below). MonoBehaviourSingleton
        // <T>.OnDestroy()/WeatherController.OnDestroy() would normally do this, but neither ever runs here:
        // the clone is SetActive(false)'d for reuse, never Destroy()'d.
        static void ReestablishSingletons(HarvestedRig rig)
        {
            if (rig.Kind == RigKind.Full)
            {
                // TOD_Sky aims the sun light relative to the dome's WORLD position (TOD_Sky.cs:1097/1103:
                // LookRotation(DomeTransform.position - <unit sun vector>)), so the dome must sit at the
                // world origin exactly as on vanilla maps (Reserve: (0,0,0), scale 1000).
                var dome = rig.Sky != null && rig.Sky.Components != null ? rig.Sky.Components.DomeTransform : null;
                if (dome != null)
                {
                    dome.position = Vector3.zero;
                    dome.localScale = Vector3.one * 1000f;
                }
                Singleton<TOD_Sky>.Create(rig.Sky);
                WeatherController.Instance = rig.Controller;
                if (CameraManager.Exist)
                {
                    CameraManager.Instance.OnCameraChanged -= rig.Controller.OnCameraChanged; // guard against double-subscribe
                    CameraManager.Instance.OnCameraChanged += rig.Controller.OnCameraChanged;
                }
            }
            else
            {
                Singleton<TODSkySimple>.Create(rig.SimpleSky);
                if (CameraManager.Exist)
                {
                    CameraManager.Instance.OnCameraChanged -= rig.SimpleSky.OnCameraCreated;
                    CameraManager.Instance.OnCameraChanged += rig.SimpleSky.OnCameraCreated;
                }
            }
        }

        static void OnSceneUnloaded(Scene scene)
        {
            var map = ArenaMapRaid.MapForScriptsScene(scene.name);
            if (map == null)
            {
                return;
            }

            // Review fix-ups #5: deactivate the rig THIS raid actually activated (_activeRig), not one
            // re-derived from the live Donor ConfigEntry — cfg.Donor.Value can name a different (or
            // not-yet-harvested) donor than the one OnSceneLoaded actually looked up, if it was edited
            // (e.g. via BepInEx's F12 ConfigurationManager) while this raid was in progress; nothing in
            // this file enforces the "next raid only" doc comment on that ConfigEntry, only asks for it.
            if (_activeMap == map && _activeRig != null)
            {
                var rig = _activeRig;
                try
                {
                    // Review fix-ups #4: release everything SetActive(false) below does NOT itself undo, so
                    // a stale WeatherController/TOD_Sky doesn't keep silently feeding every subsequent raid
                    // on every OTHER map for the rest of the session. Concretely:
                    //  - MonoBehaviourSingleton<TOD_Sky>.Instance (Comfort.Common.Singleton<T>.Create is an
                    //    unconditional overwrite with no matching auto-release outside OnDestroy) — every
                    //    ported map's fresh "Cam2" FPS camera picks this up via
                    //    TOD_ImageEffect.Awake()'s own `tod_Sky_0 ??= MonoBehaviourSingleton<TOD_Sky>.Instance`
                    //    AND WeatherController.OnCameraChanged (see next bullet) explicitly re-assigns it.
                    //  - EFT.Weather.WeatherController.Instance — a plain static field, set only in Awake(),
                    //    never cleared by SetActive(false); left stale it also permanently fails
                    //    PreflightClear()'s own `WeatherController.Instance == null` check for the rest of
                    //    the session, blocking any further donor harvest.
                    //  - CameraManager.Instance.OnCameraChanged -= .../SimpleSky's OnCameraCreated — Awake()
                    //    subscribed once and it is never unsubscribed by SetActive(false); left subscribed,
                    //    the still-live delegate fires on every future raid's fresh Cam2 (CameraManager fires
                    //    OnCameraChanged on every raid, not just the first) and re-assigns the stale Sky.
                    // ReestablishSingletons (OnSceneLoaded) puts all of this back on the next activation.
                    if (rig.Kind == RigKind.Full)
                    {
                        Shader.DisableKeyword("_WEATHER_DEFINED");
                        if (CameraManager.Exist)
                        {
                            CameraManager.Instance.OnCameraChanged -= rig.Controller.OnCameraChanged;
                        }
                        if (Singleton<TOD_Sky>.Instantiated && Singleton<TOD_Sky>.Instance == rig.Sky)
                        {
                            Singleton<TOD_Sky>.Release(rig.Sky);
                        }
                        if (WeatherController.Instance == rig.Controller)
                        {
                            WeatherController.Instance = null;
                        }
                    }
                    else
                    {
                        if (CameraManager.Exist)
                        {
                            CameraManager.Instance.OnCameraChanged -= rig.SimpleSky.OnCameraCreated;
                        }
                        if (Singleton<TODSkySimple>.Instantiated && Singleton<TODSkySimple>.Instance == rig.SimpleSky)
                        {
                            Singleton<TODSkySimple>.Release(rig.SimpleSky);
                        }
                    }
                    rig.Root.SetActive(false); // NOT Destroy — reused next raid
                }
                catch (Exception e)
                {
                    Plugin.Log.LogError($"[ArenaMapPort][WeatherRig] {map.Key}: deactivation threw (non-fatal, " +
                        $"raid already ending): {e.GetType().Name}: {e.Message}");
                }
            }
            _activeMap = null;
            _activeRig = null;
        }

        // Public accessor ArenaLightingRepairs' gating reads. Reflects only a SUCCESSFUL activation for
        // THIS raid — a configured-but-failed-to-activate map naturally returns false here, which is
        // exactly the "fall back to the existing workaround" behavior required.
        internal static bool IsActiveForCurrentMap() => _activeMap != null && _activeMap == ArenaMapRaid.CurrentMap;

        internal static bool KeepFillLightForCurrentMap() =>
            _activeMap != null && _configs.TryGetValue(_activeMap.Key, out var c) && c.KeepFillLight.Value;

        // SetActive(true)/SetActive(false) are never called mid-raid in response to a config
        // SettingChanged — matches "changes apply on the next raid" and avoids any re-entrancy question
        // about toggling a live WeatherController/TOD_Sky while LateUpdate is running.
        //
        // Cross-raid reuse needs no extra glue for CloudController/AmbientLight/TOD_Camera (all
        // self-symmetric on OnEnable/OnDisable). Review fix-ups #4 (superseding this comment's earlier,
        // now-incorrect claim that the CameraManager.OnCameraChanged subscription is "deliberately left
        // subscribed forever"): SetActive(false) alone does NOT unsubscribe it or release the
        // WeatherController.Instance/Singleton&lt;TOD_Sky&gt;/Singleton&lt;TODSkySimple&gt; statics —
        // OnSceneUnloaded now does this explicitly (see its own comment), and OnSceneLoaded's
        // ReestablishSingletons puts it all back (including a fresh, exactly-once
        // CameraManager.OnCameraChanged subscription) on the next activation, so cross-raid reuse no
        // longer depends on the still-subscribed delegate happening to fire correctly on its own.

        // ---------------------------------------------------------------------------------------------
        // SkyboxMode — Dome vs FrozenStatic. FrozenStatic adapts ManimalIcebreaker's IcebreakerSky.
        // FreezeSkybox technique (MIT, icebreaker-client/IcebreakerSky.cs — see THIRD-PARTY-NOTICES.md):
        // disable the dome's painter GameObjects, leave everything else (AmbientLight, TOD_Time, wind,
        // rain, the ReflectionProbe) running.
        // ---------------------------------------------------------------------------------------------
        static void ApplySkyboxMode(HarvestedRig rig, SkyboxMode mode)
        {
            if (rig.Kind != RigKind.Full || rig.Sky == null)
            {
                return; // simplified rigs have no dome to freeze
            }
            var c = rig.Sky.Components;
            if (c == null)
            {
                return;
            }

            bool freeze = mode == SkyboxMode.FrozenStatic;
            if (c.Atmosphere != null) c.Atmosphere.SetActive(!freeze);
            if (c.Clear != null) c.Clear.SetActive(!freeze);
            if (c.Space != null) c.Space.SetActive(!freeze);
            if (c.Sun != null) c.Sun.SetActive(!freeze);
            if (c.Moon != null) c.Moon.SetActive(!freeze);
            if (c.CloudRenderer != null)
            {
                c.CloudRenderer.gameObject.SetActive(!freeze);
            }
            // Stage 0 does NOT source a replacement RenderSettings.skybox material (Reserve_Base_Scripts
            // is not known this pass to author its own separate skybox asset; RenderSettings.skybox is
            // already null for every Arena scene today, so FrozenStatic's visual result for Stage 0 is
            // "no dome geometry, camera clears to its own background color" — a deliberately narrower,
            // lower-risk fallback. Sourcing/authoring a flat-gradient skybox material is out of Stage-0
            // scope; only matters if Dome mode's own visual result actually needs it.
        }

        // ---------------------------------------------------------------------------------------------
        // Small accessors LightingDiagnosticProbe needs (ProbeWeatherRig).
        // ---------------------------------------------------------------------------------------------
        internal static string DonorForMap(ArenaMapDescriptor map) =>
            _configs.TryGetValue(map.Key, out var cfg) ? cfg.Donor.Value : OffDonor;

        internal static SkyboxMode SkyboxModeForCurrentMap() =>
            _activeMap != null && _configs.TryGetValue(_activeMap.Key, out var cfg) ? cfg.SkyboxModeEntry.Value : SkyboxMode.Dome;

        internal static HarvestedRig LastHarvestResult(string donorName) =>
            donorName != null && _harvested.TryGetValue(donorName, out var rig) ? rig : null;
    }
}
