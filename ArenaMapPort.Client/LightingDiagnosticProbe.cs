using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Comfort.Common;
using EFT;
using EFT.CameraControl;
using EFT.Game.Spawning;
using EFT.Weather;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ArenaMapPort.Client
{
    /// <summary>
    /// fix-lighting-movement, item 4 — one bounded, once-per-raid diagnostic dump, gated on
    /// ArenaMapRaid.IsLoaded(), fired a few seconds after the Scripts scene loads (long enough for
    /// the local player to have spawned and this file's own repairs — ArenaLightingRepairs,
    /// SpawnColliderRepairs — to have already run at least once). Every section here exists to
    /// directly confirm or refute one specific claim from the investigator/verifier reports (see
    /// docs/progress/fix-lighting-movement.md for the mapping) rather than dumping raw state for its
    /// own sake — and every section is capped to a small, fixed number of lines: never a per-object
    /// dump of the ~39k GameObjects or ~477 lights the bundle actually holds.
    /// </summary>
    internal static class LightingDiagnosticProbe
    {
        const float DelaySeconds = 5f;
        const int TopLightsCount = 5;
        const int SampleMaterialsCount = 3;
        const int MaxOverlapsLogged = 10;
        const float CapsuleHalfHeight = 0.9f; // approx half-height of a standing player capsule
        const float CapsuleRadius = 0.4f;
        const float GroundRayMaxDistance = 5f;

        static bool _installed;
        static bool _ranThisRaid;
        static GameObject _host;

        internal static void Install()
        {
            if (_installed)
            {
                return;
            }
            _installed = true;

            SceneManager.sceneLoaded += OnSceneLoaded;
            SceneManager.sceneUnloaded += OnSceneUnloaded;
        }

        static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            // Stateless resolution (not ArenaMapRaid.CurrentMap) — see ArenaMapRaid's own doc comment
            // for why an unload/reset handler must never depend on a shared mutable "current map" field.
            if (ArenaMapRaid.MapForScriptsScene(scene.name) == null || _ranThisRaid)
            {
                return;
            }

            if (_host == null)
            {
                _host = new GameObject("ArenaMapPort_DiagnosticProbe") { hideFlags = HideFlags.HideAndDontSave };
                UnityEngine.Object.DontDestroyOnLoad(_host);
            }
            _host.AddComponent<Runner>().StartCoroutine(Runner.RunAfter(DelaySeconds));
        }

        static void OnSceneUnloaded(Scene scene)
        {
            if (ArenaMapRaid.MapForScriptsScene(scene.name) == null)
            {
                return;
            }
            _ranThisRaid = false;
            if (_host != null)
            {
                UnityEngine.Object.Destroy(_host);
            }
            _host = null;
        }

        sealed class Runner : MonoBehaviour
        {
            internal static IEnumerator RunAfter(float seconds)
            {
                // Scripts-scene load precedes spawn and the 10 s countdown; wait until the raid has
                // actually started (AbstractGame.Status, set Started at BaseLocalGame.cs:824) so the
                // player, the raid camera and our lighting repairs are all in their final state.
                const float startTimeout = 180f;
                float waited = 0f;
                while (waited < startTimeout)
                {
                    var game = Singleton<AbstractGame>.Instantiated ? Singleton<AbstractGame>.Instance : null;
                    if (game != null && game.Status == GameStatus.Started)
                    {
                        break;
                    }
                    yield return new WaitForSeconds(1f);
                    waited += 1f;
                }
                yield return new WaitForSeconds(seconds);
                try
                {
                    RunProbe();
                }
                catch (Exception e)
                {
                    Plugin.Log.LogError($"[Diag] probe threw: {e}");
                }
            }
        }

        static void RunProbe()
        {
            var map = ArenaMapRaid.CurrentMap;
            if (map == null || _ranThisRaid)
            {
                return;
            }
            _ranThisRaid = true;

            var sb = new StringBuilder();
            sb.AppendLine($"[Diag] ==== lighting/movement probe ({map.Key}, once per raid) ====");

            ProbePlayerAndColliders(sb);
            ProbeSpawnColliders(sb);
            ProbeRenderSettings(sb);
            ProbeAmbientGlobals(sb);
            ProbeCameraAndPostEffects(sb);
            ProbeLightingRigPresence(sb);
            ProbeWeatherRig(sb, map);
            ProbeLights(sb, map);
            ProbeSampleMaterials(sb, map);

            Plugin.Log.LogDebug(sb.ToString());
        }

        // Confirms/refutes: player position sane; ground actually under the player (not falling through
        // geometry); which colliders the player capsule is embedded in right now — the direct empirical
        // check for SpawnColliderRepairs having actually fixed the "cannot move" symptom this raid.
        static void ProbePlayerAndColliders(StringBuilder sb)
        {
            sb.AppendLine("-- player --");
            var world = Singleton<GameWorld>.Instantiated ? Singleton<GameWorld>.Instance : null;
            var player = world?.MainPlayer;
            if (player == null)
            {
                sb.AppendLine("  (no MainPlayer yet — probe fired before local player spawn)");
                return;
            }

            Vector3 pos = player.Position;
            sb.AppendLine($"  position={pos}");

            if (Physics.Raycast(pos + Vector3.up * 0.5f, Vector3.down, out var hit, GroundRayMaxDistance))
            {
                sb.AppendLine($"  groundRaycast: hit='{hit.collider.name}' " +
                               $"layer={LayerMask.LayerToName(hit.collider.gameObject.layer)} distance={hit.distance:0.00}");
            }
            else
            {
                sb.AppendLine($"  groundRaycast: NO HIT within {GroundRayMaxDistance}m below player " +
                               "(falling / out of bounds / no geometry here)");
            }

            // Approximate capsule (ICharacterController exposes no public collider bounds in the
            // decompile checked this pass) — good enough for a diagnostic log, not used for any
            // gameplay decision. QueryTriggerInteraction.Collide so a still-solid (bug not yet fixed,
            // or Enabled=false) spawn box shows up here just as readily as a proper trigger would.
            Vector3 bottom = pos - Vector3.up * CapsuleHalfHeight;
            Vector3 top = pos + Vector3.up * CapsuleHalfHeight;
            var overlaps = Physics.OverlapCapsule(bottom, top, CapsuleRadius, ~0, QueryTriggerInteraction.Collide);
            sb.AppendLine($"  colliders overlapping player capsule (approx r={CapsuleRadius}m): {overlaps.Length}");
            for (int i = 0; i < overlaps.Length && i < MaxOverlapsLogged; i++)
            {
                var c = overlaps[i];
                sb.AppendLine($"    '{c.name}' layer={LayerMask.LayerToName(c.gameObject.layer)} isTrigger={c.isTrigger}");
            }
        }

        // Confirms: SpawnColliderRepairs' postfix actually ran and flipped isTrigger this raid (source
        // of truth independent of the overlap check above, which only samples whichever marker the
        // player actually spawned at).
        static void ProbeSpawnColliders(StringBuilder sb)
        {
            sb.AppendLine("-- spawn markers --");
            var markers = UnityEngine.Object.FindObjectsOfType<SpawnPointMarker>();
            foreach (var m in markers)
            {
                var c = m._collider;
                sb.AppendLine($"  '{m.gameObject.name}' " +
                               $"isTrigger={(c != null ? c.isTrigger.ToString() : "n/a (no collider)")} " +
                               $"enabled={(c != null ? c.enabled.ToString() : "n/a")}");
            }
        }

        // Confirms/refutes: RC4 / "levelsettings-not-actually-executing" / "importer-never-authors-
        // rendersettings" — what is actually LIVE right now, after ArenaLightingRepairs has already run
        // at least once (so this also confirms the repair's own writes stuck).
        static void ProbeRenderSettings(StringBuilder sb)
        {
            sb.AppendLine("-- RenderSettings --");
            sb.AppendLine($"  ambientMode={RenderSettings.ambientMode} ambientLight={RenderSettings.ambientLight} " +
                           $"ambientIntensity={RenderSettings.ambientIntensity:0.00} (note: ambientIntensity has " +
                           "no effect while ambientMode!=Skybox, see ArenaLightingRepairs' doc comment)");
            sb.AppendLine($"  ambientSkyColor={RenderSettings.ambientSkyColor} " +
                           $"ambientEquatorColor={RenderSettings.ambientEquatorColor} " +
                           $"ambientGroundColor={RenderSettings.ambientGroundColor}");
            sb.AppendLine($"  fog={RenderSettings.fog} fogColor={RenderSettings.fogColor} " +
                           $"fogDensity={RenderSettings.fogDensity:0.000} fogMode={RenderSettings.fogMode}");
            sb.AppendLine($"  skybox={(RenderSettings.skybox != null ? RenderSettings.skybox.name : "null")} " +
                           $"defaultReflectionMode={RenderSettings.defaultReflectionMode} " +
                           $"customReflectionTexture-bound={(RenderSettings.customReflectionTexture != null)}");
        }

        // Confirms/refutes: RC1/RC2 (AmbientLight.cs's custom globals) — read back live rather than
        // inferred from a shader-bytecode scan. Reading an unset global property is a safe, well-defined
        // Unity API call (returns the type's zero value / null texture), never an exception.
        static void ProbeAmbientGlobals(StringBuilder sb)
        {
            sb.AppendLine("-- EFT-custom ambient/reflection shader globals (live readback) --");
            // _EFT_Ambient is written with SetGlobalVector (AmbientLight.cs:677), so read it as a vector.
            var shar = Shader.GetGlobalVector("_SHAr");
            sb.AppendLine($"  _SHAr={shar} _EFT_Ambient={Shader.GetGlobalVector("_EFT_Ambient")}");
            // weather-rig-stage0: PASS bar for the Stage-0 exit criteria — non-zero once a rig is
            // active and its AmbientLight has actually pushed SH into this global; 0 today (no rig),
            // confirmed unbound.
            sb.AppendLine($"  _SHAr.magnitude={shar.magnitude:0.000} (PASS bar: non-zero once a rig is active; " +
                           "0 today with no rig, confirmed unbound)");
            sb.AppendLine($"  _MyGlobalReflectionProbe-bound={(Shader.GetGlobalTexture("_MyGlobalReflectionProbe") != null)} " +
                           $"_MySimpleGlobalReflectionProbe-bound={(Shader.GetGlobalTexture("_MySimpleGlobalReflectionProbe") != null)}");
        }

        // Confirms/refutes: RC5 (Cam2 fallback camera's own baked exposure/tonemap preset applying
        // unmodified).
        // Factored out of this method so ProbeWeatherRig (weather-rig-stage0) can resolve the same
        // active camera without duplicating the try/catch — no behavior change to the resolution itself.
        static Camera ResolveActiveCamera(StringBuilder sb)
        {
            Camera cam = null;
            try
            {
                cam = CameraManager.Instance?.Camera;
            }
            catch (Exception e)
            {
                sb.AppendLine($"  (CameraManager.Instance.Camera threw: {e.GetType().Name}: {e.Message})");
            }
            if (cam == null)
            {
                cam = Camera.main;
            }
            return cam;
        }

        static void ProbeCameraAndPostEffects(StringBuilder sb)
        {
            sb.AppendLine("-- camera --");
            Camera cam = ResolveActiveCamera(sb);

            if (cam == null)
            {
                sb.AppendLine("  (no active camera found)");
                return;
            }

            sb.AppendLine($"  name='{cam.name}' tag={cam.tag} renderingPath={cam.renderingPath} " +
                           $"actualRenderingPath={cam.actualRenderingPath} allowHDR={cam.allowHDR}");

            // Whether EFT's deferred ambient pass (AmbientLight, "Custom Ambient" command buffer at
            // AfterLighting, AmbientLight.cs:311) and TOD/fog effects exist on this camera at all.
            var names = new StringBuilder();
            foreach (var behaviour in cam.GetComponents<Behaviour>())
            {
                if (behaviour == null)
                {
                    continue;
                }
                names.Append(behaviour.GetType().Name).Append(behaviour.enabled ? "" : "(off)").Append(' ');
            }
            sb.AppendLine($"  components: {names}");
            var afterLighting = new StringBuilder();
            foreach (var buffer in cam.GetCommandBuffers(UnityEngine.Rendering.CameraEvent.AfterLighting))
            {
                afterLighting.Append('\'').Append(buffer.name).Append("' ");
            }
            sb.AppendLine($"  AfterLighting command buffers: {afterLighting}");

            var prism = cam.GetComponent<PrismEffects>();
            if (prism != null)
            {
                sb.AppendLine($"  PrismEffects: useBloom={prism.useBloom} useVignette={prism.useVignette} " +
                               $"useTonemap={prism.useTonemap} useExposure={prism.useExposure} " +
                               $"exposureMiddleGrey={prism.exposureMiddleGrey:0.000} " +
                               $"exposureLowerLimit={prism.exposureLowerLimit:0.0} " +
                               $"exposureUpperLimit={prism.exposureUpperLimit:0.0}");
            }
            else
            {
                sb.AppendLine("  PrismEffects: none on active camera");
            }
        }

        // Confirms/refutes: "levelsettings-exists-and-is-spec-compliant" vs "levelsettings-not-actually-
        // executing", plus the whole TOD/WeatherController-absence premise RC1-RC3 build on.
        static void ProbeLightingRigPresence(StringBuilder sb)
        {
            sb.AppendLine("-- lighting rig presence --");
            bool lsInstantiated = Singleton<LevelSettings>.Instantiated;
            LevelSettings ls = lsInstantiated ? Singleton<LevelSettings>.Instance : null;
            bool matchesSceneInstance = ls != null && ReferenceEquals(ls, UnityEngine.Object.FindObjectOfType<LevelSettings>());

            sb.AppendLine($"  LevelSettings.Instantiated={lsInstantiated} matchesSceneInstance={matchesSceneInstance}");
            if (ls != null)
            {
                sb.AppendLine($"  AmbientMode={ls.AmbientMode} SkyColor={ls.SkyColor} " +
                               $"CameraPrefab-set={ls.CameraPrefab != null} AmbientType={ls.AmbientType} fog={ls.fog}");
            }

            sb.AppendLine($"  TODSkyProvider.IsAvailable={TODSkyProvider.IsAvailable} " +
                           $"TOD_Sky.Instantiated={MonoBehaviourSingleton<TOD_Sky>.Instantiated} " +
                           $"TODSkySimple.Instantiated={MonoBehaviourSingleton<TODSkySimple>.Instantiated} " +
                           $"WeatherController.Instance-set={(WeatherController.Instance != null)}");
        }

        // weather-rig-stage0: confirms/refutes every open question LIGHTING-DESIGN.md's own diagnostics
        // section raised plus the two verification reports' amendments (OnRenderObjectManager presence,
        // the FPS camera's TOD_Scattering.Sky wiring, the InitWeather-ordering tripwire) — the harvest
        // half is logged once at harvest time too (WeatherRig.HarvestOne); this re-prints the SAME cached
        // result here so a single post-raid log read has everything in one place. Every line below the
        // harvest summary assumes an activated rig and is skipped otherwise.
        static void ProbeWeatherRig(StringBuilder sb, ArenaMapPort.Shared.ArenaMapDescriptor map)
        {
            sb.AppendLine("-- weather rig (WeatherRig.cs) --");
            bool active = WeatherRig.IsActiveForCurrentMap();
            string donor = WeatherRig.DonorForMap(map);
            sb.AppendLine($"  configured donor for {map.Key}='{donor}' " +
                           $"active-this-raid={active} skyboxMode={WeatherRig.SkyboxModeForCurrentMap()}");

            var harvest = WeatherRig.LastHarvestResult(donor); // may be null
            if (harvest != null)
            {
                sb.AppendLine($"  harvest: loaded={harvest.LoadOk} cloned={harvest.CloneOk} " +
                               $"unloaded={harvest.UnloadOk} validated={harvest.ValidationOk} " +
                               $"keptComponents={harvest.KeptComponents}");
                sb.AppendLine($"  post-unload identity unchanged: LevelSettings={harvest.LevelSettingsUnchanged} " +
                               $"LocationScene={harvest.LocationSceneUnchanged} " +
                               $"EnvironmentManager={harvest.EnvironmentManagerUnchanged}");
            }
            else
            {
                sb.AppendLine("  harvest: (no result cached — Donor='Off', or still pending/permanently failed)");
            }

            if (!active)
            {
                return; // nothing further to probe — every line below assumes an activated rig
            }

            // is the LIVE instance our clone, in a DontDestroyOnLoad scene, never one of our owned scenes.
            var wc = EFT.Weather.WeatherController.Instance;
            sb.AppendLine($"  WeatherController.Instance-scene='{(wc != null ? wc.gameObject.scene.name : "null")}' " +
                           "(expect a DontDestroyOnLoad scene, never an owned map scene)");

            var ambientLight = UnityEngine.Object.FindObjectOfType<AmbientLight>();
            sb.AppendLine($"  AmbientLight found={ambientLight != null} " +
                           $"shaderFieldsSet={(ambientLight != null &&
                               ambientLight.DepthWriteShader != null && ambientLight.ClearStencilShader != null &&
                               ambientLight.WriteStencilShader != null && ambientLight.ScreenAmbientShader != null &&
                               ambientLight.AnalyticSourceShader != null)}");

            var cloud = wc != null ? wc.CloudController : null;
            sb.AppendLine($"  CloudController.Settings-set={(cloud != null && cloud.Settings != null)}");

            sb.AppendLine($"  OnRenderObjectManager present={UnityEngine.Object.FindObjectOfType<OnRenderObjectManager>() != null}");

            // sun light resolved.
            var sky = MonoBehaviourSingleton<TOD_Sky>.Instantiated ? MonoBehaviourSingleton<TOD_Sky>.Instance : null;
            var sunLight = sky != null ? sky.LightObject : null;
            if (sunLight != null)
            {
                sb.AppendLine($"  sun: '{sunLight.name}' intensity={sunLight.intensity:0.00} color={sunLight.color} " +
                               $"forward={sunLight.transform.forward}");
            }
            else
            {
                sb.AppendLine("  sun: (TOD_Sky.LightObject null — no active rig sun this frame)");
            }

            // ambient globals magnitude — extends the EXISTING ProbeAmbientGlobals (one added line there,
            // not duplicated here).

            // FPS camera's TOD_Scattering.Sky wiring (amendment 2).
            Camera cam = ResolveActiveCamera(sb);
            var scattering = cam != null ? cam.GetComponent<TOD_Scattering>() : null;
            sb.AppendLine($"  FPS camera TOD_Scattering present={scattering != null} " +
                           $"Sky-set={(scattering != null && scattering.Sky != null)}");

            // reflections.
            sb.AppendLine($"  ReflectionProbe count={UnityEngine.Object.FindObjectsOfType<ReflectionProbe>().Length}");

            // fog side-by-side (which system last wrote fogColor this frame). Review fix-ups #3: TOD_Sky.
            // UpdateFog only writes RenderSettings.fogColor for Fog.Mode Color/Directional — for Mode==None
            // it writes nothing, so ArenaLightingRepairs skipping its own fogColor write (because a rig is
            // active) would leave fogColor stale with nobody driving it. Not independently verified this
            // pass whether Reserve_Base_Scripts' authored TOD_Sky.Fog.Mode is ever None (would need the
            // donor scene open in Unity) — logged explicitly so a playtest can confirm/refute it directly.
            sb.AppendLine($"  fogColor(live)={RenderSettings.fogColor} ArenaLightingRepairs.FogColor={ArenaLightingRepairs.FogColor} " +
                           $"TOD_Sky.Fog.Mode={(sky != null ? sky.Fog.Mode.ToString() : "n/a")} " +
                           "(if fogColor differs from ArenaLightingRepairs.FogColor and a rig is active, TOD_Sky is " +
                           "correctly winning fogColor; if Fog.Mode=None and they DO match, fogColor is stale — " +
                           "nobody is writing it)");

            // current TOD time / weather, and the InitWeather-ordering tripwire.
            var gdt = TODSkyProvider.IsAvailable ? TODSkyProvider.Instance.CurrentTime.GameDateTime : null;
            sb.AppendLine($"  TODSkyProvider.CurrentTime.GameDateTime-set={gdt != null} " +
                           "(null here means BaseLocalGame.InitWeather's gate ran BEFORE this rig activated — " +
                           "the ordering tripwire; if this is ever false, the activation hook needs the " +
                           "Harmony-prefix fallback documented in docs/progress/weather-rig-stage0.md)");
            if (wc != null)
            {
                var curve = wc.WeatherCurve;
                sb.AppendLine($"  weather: fog={curve.Fog:0.00} cloudiness={curve.Cloudiness:0.00} " +
                               $"wind={curve.Wind} rain={curve.Rain:0.00}");
            }

            // donor-settings-camera-weather §A — live readback of the donor-consistent LevelSettings
            // overrides (WeatherRig.ApplyDonorLevelSettingsOverrides already logged one line on apply;
            // this confirms the values actually stuck through to probe time, same convention as
            // ProbeRenderSettings above).
            var ls = Singleton<LevelSettings>.Instantiated ? Singleton<LevelSettings>.Instance : null;
            if (ls != null)
            {
                sb.AppendLine($"  LevelSettingsOverride (live): ZeroLevel={ls.ZeroLevel:0.00} " +
                               $"HeightFalloff={ls.HeightFalloff:0.000} MaxMapTextureMemory={ls.MaxMapTextureMemory} " +
                               $"RainBounds=center({ls.RainBounds.center}) size({ls.RainBounds.size}) " +
                               $"NorthDirection={ls._northDirection:0.0}");
            }

            // donor-settings-camera-weather §B — camera prefab takeover state. absent-with-warning here
            // (rather than "present=false") means the Cam2 fallback path is silently in use this raid.
            var activeRig = WeatherRig.ActiveRig;
            bool donorCameraEnabled = WeatherRig.UseDonorCameraPrefabForCurrentMap();
            sb.AppendLine($"  CameraPrefabTakeover: enabled={donorCameraEnabled} " +
                           $"harvestedDonorCamera={(activeRig != null && activeRig.CameraPrefab != null)} " +
                           $"live LevelSettings.CameraPrefab-set={(ls != null && ls.CameraPrefab != null)} " +
                           $"(expect all three true together for the donor path to actually be live; if " +
                           "enabled=true but the other two are false, the harvest had no donor camera and " +
                           "this raid silently fell back to Cam2)");

            // donor-settings-camera-weather §C — Tier-2 wetness mitigations' current config + live state.
            var rain = wc != null ? wc.RainController : null;
            sb.AppendLine($"  wetness mitigations: groundMaterialFix={ShaderRebind.GroundMaterialWetnessFixEnabled} " +
                           $"hideProjectorDecalsV2={DecalRepairs.Enabled} " +
                           $"wetRendererMultiplier={RainWetnessRepairs.WetRendererIntensityMultiplier:0.00} " +
                           $"maxLyingWaterOverrideEnabled={RainWetnessRepairs.MaxLyingWaterOverrideEnabled} " +
                           $"maxLyingWaterOverrideValue={RainWetnessRepairs.MaxLyingWaterOverrideValue:0.00}");
            if (rain != null)
            {
                sb.AppendLine($"  wetness live: PuddleWaterLevel={rain.PuddleWaterLevel} " +
                               $"WetRenderer.Intensity={(rain._wetRenderer != null ? rain._wetRenderer.Intensity.ToString("0.00") : "n/a")}");
            }
        }

        // Confirms/refutes: RC3 (sun light presence/intensity/color) and the general "are lights even
        // active" question — top-5 by intensity plus a type breakdown, never all ~477.
        static void ProbeLights(StringBuilder sb, ArenaMapPort.Shared.ArenaMapDescriptor map)
        {
            sb.AppendLine("-- lights --");
            var lights = new List<Light>();
            foreach (var sceneName in map.AllSceneNames)
            {
                var scene = SceneManager.GetSceneByName(sceneName);
                if (!scene.IsValid() || !scene.isLoaded)
                {
                    continue;
                }
                foreach (var root in scene.GetRootGameObjects())
                {
                    lights.AddRange(root.GetComponentsInChildren<Light>(true));
                }
            }

            int point = 0, spot = 0, directional = 0, area = 0, other = 0, activeEnabled = 0;
            foreach (var l in lights)
            {
                if (l.gameObject.activeInHierarchy && l.enabled)
                {
                    activeEnabled++;
                }
                switch (l.type)
                {
                    case LightType.Point: point++; break;
                    case LightType.Spot: spot++; break;
                    case LightType.Directional: directional++; break;
                    case LightType.Area: area++; break;
                    default: other++; break;
                }
            }
            sb.AppendLine($"  total={lights.Count} active+enabled={activeEnabled} point={point} spot={spot} " +
                           $"directional={directional} area={area} other={other}");

            foreach (var l in lights.OrderByDescending(l => l.intensity).Take(TopLightsCount))
            {
                sb.AppendLine($"  top: '{l.name}' type={l.type} intensity={l.intensity:0.00} range={l.range:0.0} " +
                               $"color={l.color} enabled={l.enabled} activeInHierarchy={l.gameObject.activeInHierarchy}");
            }
        }

        // Confirms/refutes: materials lens M1/M7/M8 — whether the ALREADY-REBOUND (post-ShaderRebind)
        // materials near spawn carry plausible, non-black color/texture data at runtime, not just in
        // the extraction JSON.
        static void ProbeSampleMaterials(StringBuilder sb, ArenaMapPort.Shared.ArenaMapDescriptor map)
        {
            sb.AppendLine("-- sample materials --");
            var seen = new HashSet<string>();
            int logged = 0;

            foreach (var sceneName in map.AllSceneNames)
            {
                if (logged >= SampleMaterialsCount)
                {
                    break;
                }
                var scene = SceneManager.GetSceneByName(sceneName);
                if (!scene.IsValid() || !scene.isLoaded)
                {
                    continue;
                }

                foreach (var root in scene.GetRootGameObjects())
                {
                    if (logged >= SampleMaterialsCount)
                    {
                        break;
                    }
                    foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
                    {
                        if (logged >= SampleMaterialsCount)
                        {
                            break;
                        }
                        foreach (var mat in renderer.sharedMaterials)
                        {
                            if (mat == null || mat.shader == null || !seen.Add(mat.name))
                            {
                                continue;
                            }
                            if (mat.shader.name.StartsWith(ShaderRebind.StandinPrefix, StringComparison.Ordinal))
                            {
                                continue; // not yet rebound — sample only settled materials
                            }

                            var color = mat.HasProperty("_Color") ? mat.GetColor("_Color").ToString() : "n/a";
                            var reflect = mat.HasProperty("_ReflectColor") ? mat.GetColor("_ReflectColor").ToString() : "n/a";
                            var hasMainTex = mat.HasProperty("_MainTex") && mat.GetTexture("_MainTex") != null;
                            sb.AppendLine($"  '{mat.name}' shader='{mat.shader.name}' _Color={color} " +
                                           $"_ReflectColor={reflect} _MainTex-bound={hasMainTex}");
                            logged++;
                            if (logged >= SampleMaterialsCount)
                            {
                                break;
                            }
                        }
                    }
                }
            }

            if (logged == 0)
            {
                sb.AppendLine("  (none found — every sampled material still on its Hidden/ArenaMapPort/ " +
                               "stand-in, or no renderers loaded yet)");
            }
        }
    }
}
