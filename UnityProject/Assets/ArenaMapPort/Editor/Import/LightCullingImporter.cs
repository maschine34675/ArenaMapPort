// PLAN-TWO-MAPS item 7 -- Arena's own per-light native components, dropped by every prior stage of
// this pipeline exactly like the NavMeshModifier/SmallPhysicsObject cases before it: CullingLightObject
// (472 instances on Equator / 186 on Bay5) fades a Light's intensity and shadow strength by camera
// distance and disables its GameObjectsToTurnOff/ComponentsToTurnOff list when out of range;
// VolumetricLight (17 / 1) drives the "Sandbox/VolumetricLight" shader's light-shaft rendering through
// VolumetricLightRenderer; EFT.Visual.LightFlicker (2 / 0, both attached alongside a CullingLightObject
// on the same GameObject) drives a flickering Light.intensity off Curve.Evaluate(Time.time-based input)
// or Perlin noise, scaled by CullingCoef -- which is exactly the hook CullingLightObject.UpdateCullingObject
// writes into instead of Light.intensity when a flicker is present (CullingLightObject.cs:240-247). None
// of the three exist in a ported scene today: extract_equator.py/extract_bay5.py never recorded them
// (COMPONENT_BUILDERS never dispatched on their script identity) and this importer never attached them.
//
// Byte layout, field provenance, wrap-mode code mapping and the package JSON schema are pinned in the
// spawning task's design section and in docs/progress/two-maps-item-7-light-culling.md; every member name
// used below is verified against the decompile (D:/SPT41/Development/_decompiled/CullingLightObject.cs,
// CullingObject.cs, VolumetricLight.cs, EFT.Visual/LightFlicker.cs, EFT.Visual/Flicker.cs).
//
// All three classes carry [ExecuteInEditMode], so AddComponent<T>() runs Awake()/OnEnable() synchronously
// inside this batch import (same as BallisticCollider/SmallPhysicsObject before them). Unlike those two,
// an exception here is expected and recoverable: VolumetricLight.Awake() calls
// CreateShaderAndMaterial() -> ShadersFinder.Find(...) and Flicker.OnEnable() calls
// ComponentHelper.RegisterInSystem(this), both of which reach a runtime-only singleton (ShadersFinder,
// FlickerSystem) that is never initialized during headless Editor batch import. When AddComponent throws
// but the native component object was already attached before the throwing callback ran, this importer
// keeps it (logs a warning) rather than destroying it -- the bundle serializes the component and its
// authored field values regardless of whether its edit-time Awake completed, and EFT's own runtime Awake
// runs again, cleanly, the moment the bundle is loaded in a real raid.
using System;
using System.IO;
using Comfort.Common;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace ArenaMapPort.Editor.Import
{
    public static class LightCullingImporter
    {
        const string VolumetricLightShaderName = "Sandbox/VolumetricLight";
        static GameObject _flickerSystemStub;

        /// <summary>
        /// Editor-time stubs for the two things EFT's [ExecuteInEditMode] Awake/OnEnable need that the
        /// batch import does not have (first Equator build 2026-09-26: 57 fatal log lines):
        /// VolumetricLight.Awake builds a private material from ShadersFinder.Find("Sandbox/VolumetricLight"),
        /// which outside play mode is plain Shader.Find -- Assets/ArenaMapPort/EditorStubShaders provides a
        /// never-rendered shader of that name; and Flicker.OnEnable registers with
        /// Singleton&lt;IComponentSystem&lt;Flicker&gt;&gt;, which logs an error and then throws when no
        /// FlickerSystem exists -- one is created on a HideAndDontSave GameObject (never saved into a
        /// scene, lives for the rest of the batch session). Idempotent; call before Pass 4a.
        /// </summary>
        public static void EnsureEditorStubs()
        {
            if (Shader.Find(VolumetricLightShaderName) == null)
                throw new InvalidDataException("LightCullingImporter: the editor stub shader '" + VolumetricLightShaderName +
                    "' is missing (Assets/ArenaMapPort/EditorStubShaders) -- VolumetricLight.Awake would throw per instance.");
            if (!Singleton<IComponentSystem<global::EFT.Visual.Flicker>>.Instantiated)
            {
                _flickerSystemStub = new GameObject("ArenaMapPort FlickerSystem (editor stub)") { hideFlags = HideFlags.HideAndDontSave };
                ComponentSystem<global::EFT.Visual.Flicker, global::EFT.Visual.FlickerSystem>.Register(_flickerSystemStub);
                // FlickerSystem is a plain MonoBehaviour (no ExecuteInEditMode): edit mode never calls its Awake,
                // which allocates the Components list that Register(T) appends to (second Equator build
                // 2026-09-26: NullReferenceException in ComponentSystem.Register per LightFlicker). Awake is public.
                var system = _flickerSystemStub.GetComponent<global::EFT.Visual.FlickerSystem>();
                if (system != null && !Application.isPlaying) system.Awake();
                Debug.Log("[ArenaMapPort][LightCullingImporter] created the editor-time FlickerSystem stub (HideAndDontSave)");
            }
        }

        /// <summary>
        /// SceneAssembler Pass 4a: attach VolumetricLight to a light GameObject. Sets all 14 public
        /// fields (VolumetricLight.cs:21-54); every other field is engine-computed shader/matrix state
        /// (VolumetricLight.cs:56-138), never authored. Requires a sibling Light -- the class itself
        /// carries [RequireComponent(typeof(Light))], which would silently AddComponent a *default*
        /// Light rather than fail if this importer didn't check first, and Pass 1c already attaches
        /// every scene's Light components before this pass runs.
        /// </summary>
        public static global::VolumetricLight AttachVolumetricLight(GameObject owner, JObject comp)
        {
            if (owner == null || comp == null)
                throw new InvalidDataException("VolumetricLight import requires an owner and component.");
            string context = "VolumetricLight on '" + owner.name + "': ";
            int schema = Int(comp, "schema_version", context);
            if (Text(comp, "type", context) != "VolumetricLight" || schema != 1)
                throw new InvalidDataException(context + "unsupported component schema.");
            if (owner.GetComponent<Light>() == null)
                throw new InvalidDataException(context + "requires a sibling Light component.");

            bool enabled = Bool(comp, "enabled", context);
            int sampleCount = Int(comp, "sample_count", context);
            float scatteringCoef = Float(comp, "scattering_coef", context);
            float extinctionCoef = Float(comp, "extinction_coef", context);
            float skyboxExtinctionCoef = Float(comp, "skybox_extinction_coef", context);
            float mieG = Float(comp, "mie_g", context);
            bool heightFog = Bool(comp, "height_fog", context);
            float heightScale = Float(comp, "height_scale", context);
            float groundLevel = Float(comp, "ground_level", context);
            bool noise = Bool(comp, "noise", context);
            float noiseScale = Float(comp, "noise_scale", context);
            float noiseIntensity = Float(comp, "noise_intensity", context);
            float noiseIntensityOffset = Float(comp, "noise_intensity_offset", context);
            Vector2 noiseVelocity = ReadVector2(comp["noise_velocity"], context, "noise_velocity");
            float maxRayLength = Float(comp, "max_ray_length", context);

            global::VolumetricLight vl = AddNativeComponentTolerant<global::VolumetricLight>(owner, context);
            vl.enabled = enabled;
            vl.SampleCount = sampleCount;
            vl.ScatteringCoef = scatteringCoef;
            vl.ExtinctionCoef = extinctionCoef;
            vl.SkyboxExtinctionCoef = skyboxExtinctionCoef;
            vl.MieG = mieG;
            vl.HeightFog = heightFog;
            vl.HeightScale = heightScale;
            vl.GroundLevel = groundLevel;
            vl.Noise = noise;
            vl.NoiseScale = noiseScale;
            vl.NoiseIntensity = noiseIntensity;
            vl.NoiseIntensityOffset = noiseIntensityOffset;
            vl.NoiseVelocity = noiseVelocity;
            vl.MaxRayLength = maxRayLength;
            return vl;
        }

        /// <summary>
        /// SceneAssembler Pass 4b: attach EFT.Visual.LightFlicker to a light GameObject. Sets the ten
        /// authored fields (all inherited from EFT.Visual.Flicker except the class's own Curve build) plus
        /// CullingCoef; RandomSeed/TimeShift's RandomTimeShift re-roll and the base class's own _light
        /// field are left alone -- Flicker.Awake() re-rolls TimeShift itself when RandomTimeShift is set,
        /// and LightFlicker.Awake() self-resolves _light via GetComponent&lt;Light&gt;() exactly like
        /// CullingLightObject does, so this importer never touches it. Requires a sibling Light (no
        /// [RequireComponent] on this class, so nothing but this check would catch the mistake).
        /// </summary>
        public static global::EFT.Visual.LightFlicker AttachLightFlicker(GameObject owner, JObject comp)
        {
            if (owner == null || comp == null)
                throw new InvalidDataException("LightFlicker import requires an owner and component.");
            string context = "LightFlicker on '" + owner.name + "': ";
            int schema = Int(comp, "schema_version", context);
            if (Text(comp, "type", context) != "LightFlicker" || schema != 1)
                throw new InvalidDataException(context + "unsupported component schema.");
            if (owner.GetComponent<Light>() == null)
                throw new InvalidDataException(context + "requires a sibling Light component.");

            bool enabled = Bool(comp, "enabled", context);
            float frequency = Float(comp, "frequency", context);
            float intensity = Float(comp, "intensity", context);
            float intensityShift = Float(comp, "intensity_shift", context);
            float timeShift = Float(comp, "time_shift", context);
            AnimationCurve curve = ReadAnimationCurve(comp["curve"] as JObject, context);
            int generate = Int(comp, "generate", context);
            if (!Enum.IsDefined(typeof(global::EFT.Visual.Flicker.ECurveType), generate))
                throw new InvalidDataException(context + "generate " + generate + " is not an EFT.Visual.Flicker.ECurveType value.");
            bool randomTimeShift = Bool(comp, "random_time_shift", context);
            bool fullRandomCurve = Bool(comp, "full_random_curve", context);
            float cullingCoef = Float(comp, "culling_coef", context);

            global::EFT.Visual.LightFlicker lf = AddNativeComponentTolerant<global::EFT.Visual.LightFlicker>(owner, context);
            lf.enabled = enabled;
            lf.Frequency = frequency;
            lf.Intensity = intensity;
            lf.IntensityShift = intensityShift;
            lf.TimeShift = timeShift;
            lf.Curve = curve;
            lf.Generate = (global::EFT.Visual.Flicker.ECurveType)generate;
            lf.RandomTimeShift = randomTimeShift;
            lf.FullRandomCurve = fullRandomCurve;
            lf.CullingCoef = cullingCoef;
            return lf;
        }

        /// <summary>
        /// SceneAssembler Pass 4c: attach CullingLightObject to a light GameObject, after its optional
        /// VolumetricLight (Pass 4a) and LightFlicker (Pass 4b) siblings already exist -- its own Awake
        /// (CullingLightObject.cs:118-119) caches GetComponent&lt;LightFlicker&gt;()/&lt;VolumetricLight&gt;()
        /// exactly once, so attaching either afterward would leave the cached reference null for the rest
        /// of the raid. Sets the eleven exported fields (schema list below) plus _fadeCurve, which is not
        /// exported -- the extractor hard-fails if any real instance's curve deviates from the uniform
        /// AnimationCurve.EaseInOut(0,1,1,0) every one of the 658 real instances carries, so this importer
        /// sets that literal rather than reading a per-instance curve that was never recorded.
        /// CullDistance/_radius/_shift/_drawSphere, the four *SwitchPerFrame ints and the three
        /// turn-off/turn-on lists are likewise never set here: CullingLightObject.Awake() overwrites
        /// CullDistance/_radius unconditionally from _fadeEndDistance/the sibling Light's range, the rest
        /// is editor-only per the extractor's own hard-fail convention. Leaves _light null -- Awake
        /// self-resolves it via GetComponent&lt;Light&gt;(), and this pass never runs before Pass 1c has
        /// attached that sibling Light.
        /// </summary>
        public static global::CullingLightObject AttachCullingLightObject(GameObject owner, JObject comp)
        {
            if (owner == null || comp == null)
                throw new InvalidDataException("CullingLightObject import requires an owner and component.");
            string context = "CullingLightObject on '" + owner.name + "': ";
            int schema = Int(comp, "schema_version", context);
            if (Text(comp, "type", context) != "CullingLightObject" || schema != 1)
                throw new InvalidDataException(context + "unsupported component schema.");
            if (owner.GetComponent<Light>() == null)
                throw new InvalidDataException(context + "requires a sibling Light component.");

            bool enabled = Bool(comp, "enabled", context);
            bool cullByDistanceOnly = Bool(comp, "cull_by_distance_only", context);
            float fadeStartDistance = Float(comp, "fade_start_distance", context);
            float fadeEndDistance = Float(comp, "fade_end_distance", context);
            bool useLightIntensityFromEditor = Bool(comp, "use_light_intensity_from_editor", context);
            float maxLightIntensity = Float(comp, "max_light_intensity", context);
            bool takeFlareParametersFromThisScript = Bool(comp, "take_flare_parameters_from_this_script", context);
            bool cullShadowsByDistance = Bool(comp, "cull_shadows_by_distance", context);
            float shadowsFadeStartDistance = Float(comp, "shadows_fade_start_distance", context);
            float shadowsFadeEndDistance = Float(comp, "shadows_fade_end_distance", context);
            int initialShadowsMode = Int(comp, "initial_shadows_mode", context);
            if (!Enum.IsDefined(typeof(LightShadows), initialShadowsMode))
                throw new InvalidDataException(context + "initial_shadows_mode " + initialShadowsMode + " is not a LightShadows value.");

            // [ExecuteInEditMode]: AddComponent runs Awake() here, in the batch import, with the class
            // defaults (_useLightIntensityFromEditor=true, _maxLightIntensity=5) -- Awake forces
            // _light.enabled = true and caches float_1 = 5, and a later clo.enabled = false would run
            // OnDisable(), which writes that stale float_1 into Light.intensity and shadowStrength = 1.
            // Capture Pass 1c's authored Light state first, set every field, toggle enabled LAST, then
            // restore the Light so the bundle carries the authored values (review 2026-09-26).
            Light light = owner.GetComponent<Light>();
            bool lightEnabled = light.enabled;
            float lightIntensity = light.intensity;
            float lightShadowStrength = light.shadowStrength;
            LightShadows lightShadows = light.shadows;

            global::CullingLightObject clo = AddNativeComponentTolerant<global::CullingLightObject>(owner, context);
            clo._cullByDistanceOnly = cullByDistanceOnly;
            clo._fadeStartDistance = fadeStartDistance;
            clo._fadeEndDistance = fadeEndDistance;
            clo._useLightIntensityFromEditor = useLightIntensityFromEditor;
            clo._maxLightIntensity = maxLightIntensity;
            clo._takeFlareParametersFromThisScript = takeFlareParametersFromThisScript;
            clo._cullShadowsByDistance = cullShadowsByDistance;
            clo._shadowsFadeStartDistance = shadowsFadeStartDistance;
            clo._shadowsFadeEndDistance = shadowsFadeEndDistance;
            clo._initialShadowsMode = (LightShadows)initialShadowsMode;
            // Uniform across every real instance (schema note above) -- set explicitly rather than
            // trusting the field initializer to survive AddComponent under batch import.
            clo._fadeCurve = AnimationCurve.EaseInOut(0f, 1f, 1f, 0f);
            clo.enabled = enabled;

            light.enabled = lightEnabled;
            light.intensity = lightIntensity;
            light.shadowStrength = lightShadowStrength;
            light.shadows = lightShadows;
            return clo;
        }

        // ---------------------------------------------------------------------------------------
        // Shared AddComponent tolerance for the three [ExecuteInEditMode] classes above.
        // ---------------------------------------------------------------------------------------

        /// <summary>
        /// AddComponent&lt;T&gt;() runs T's Awake()/OnEnable() synchronously here (batch-import Editor
        /// context, GameObject already active -- same as every other native-component attacher in this
        /// project). Two distinct failure modes are handled, matching PhysicsPropImporter/
        /// BallisticColliderImporter's own "native EFT component is not ready" case plus this task's
        /// own tolerant case:
        ///  1. AddComponent returns null without throwing (the native class's managed wrapper was not
        ///     ready this Editor update tick) -- escalate, same message and remedy as the other
        ///     importers.
        ///  2. AddComponent throws (a runtime-only singleton such as ShadersFinder or FlickerSystem is
        ///     unreachable during headless batch import) but the component was already attached to the
        ///     GameObject before the throwing callback ran -- keep it and log a warning. If the
        ///     component was never attached at all, the exception is wrapped and escalated instead.
        /// </summary>
        private static T AddNativeComponentTolerant<T>(GameObject owner, string context) where T : Component
        {
            T instance = null;
            try
            {
                instance = owner.AddComponent<T>();
            }
            catch (Exception ex)
            {
                instance = owner.GetComponent<T>();
                if (instance == null)
                    throw new InvalidDataException(context + typeof(T).Name + " import failed: Awake()/OnEnable() threw " +
                        "and left no component attached (" + ex.GetType().Name + ": " + ex.Message + ").", ex);
                Debug.LogWarning("[ArenaMapPort][LightCullingImporter] " + context + typeof(T).Name +
                    ".Awake()/OnEnable() threw during batch import (" + ex.GetType().Name + ": " + ex.Message +
                    ") -- likely a runtime-only singleton (ShadersFinder, FlickerSystem, ...) unreachable in the " +
                    "editor. Kept the component as-authored; the bundle serializes it regardless and EFT's own " +
                    "Awake runs again, cleanly, at raid time.");
                return instance;
            }
            if (instance == null)
                throw new InvalidDataException(context + "native EFT component is not ready; retry the authoring phase on an Editor update tick.");
            return instance;
        }

        // ---------------------------------------------------------------------------------------
        // JSON helpers
        // ---------------------------------------------------------------------------------------

        private static AnimationCurve ReadAnimationCurve(JObject curveObj, string context)
        {
            if (curveObj == null)
                throw new InvalidDataException(context + "missing 'curve' object.");
            string curveContext = context + "curve: ";
            var keysArray = curveObj["keys"] as JArray;
            if (keysArray == null || keysArray.Count == 0)
                throw new InvalidDataException(curveContext + "no keys.");

            var keys = new Keyframe[keysArray.Count];
            for (int i = 0; i < keysArray.Count; i++)
            {
                var keyObj = keysArray[i] as JObject;
                if (keyObj == null)
                    throw new InvalidDataException(curveContext + "key " + i + " is not an object.");
                string keyContext = curveContext + "key " + i + ": ";
                float time = Float(keyObj, "time", keyContext);
                float value = Float(keyObj, "value", keyContext);
                float inSlope = Float(keyObj, "in_slope", keyContext);
                float outSlope = Float(keyObj, "out_slope", keyContext);
                float inWeight = Float(keyObj, "in_weight", keyContext);
                float outWeight = Float(keyObj, "out_weight", keyContext);
                int weightedMode = Int(keyObj, "weighted_mode", keyContext);
                var kf = new Keyframe(time, value, inSlope, outSlope, inWeight, outWeight);
                kf.weightedMode = (WeightedMode)weightedMode;
                keys[i] = kf;
            }

            var curve = new AnimationCurve(keys);
            curve.preWrapMode = ReadWrapMode(Int(curveObj, "pre_infinity", curveContext), curveContext, "pre_infinity");
            curve.postWrapMode = ReadWrapMode(Int(curveObj, "post_infinity", curveContext), curveContext, "post_infinity");
            return curve;
        }

        // Unity's serialized m_PreInfinity/m_PostInfinity are internal engine codes, not the public
        // WrapMode enum values -- 2 == ClampForever (every default curve in this project), 1 == Loop
        // (both real LightFlicker curves, which sample a Time.time-driven input and must loop). Any
        // other code is unhandled on purpose: the roundtrip validator (per the spawning task) confirms
        // this exact mapping by reading a built bundle's curve ints back, so silently mis-mapping an
        // unseen code would defeat that check.
        private static WrapMode ReadWrapMode(int code, string context, string field)
        {
            switch (code)
            {
                case 1: return WrapMode.Loop;
                case 2: return WrapMode.ClampForever;
                default:
                    throw new InvalidDataException(context + "unsupported " + field + " code " + code +
                        " (only Unity's internal codes 1=Loop and 2=ClampForever are mapped).");
            }
        }

        private static Vector2 ReadVector2(JToken token, string context, string field)
        {
            var array = token as JArray;
            if (array == null || array.Count != 2)
                throw new InvalidDataException(context + "invalid " + field + ".");
            return new Vector2(Float(array[0], context, field + "[0]"), Float(array[1], context, field + "[1]"));
        }

        private static string Text(JObject obj, string key, string context)
        {
            JToken token = obj[key];
            if (token == null || token.Type != JTokenType.String || String.IsNullOrEmpty((string)token))
                throw new InvalidDataException(context + "missing string '" + key + "'.");
            return (string)token;
        }

        private static int Int(JObject obj, string key, string context)
        {
            JToken token = obj[key];
            if (token == null || token.Type != JTokenType.Integer)
                throw new InvalidDataException(context + "missing integer '" + key + "'.");
            long value = token.Value<long>();
            if (value < Int32.MinValue || value > Int32.MaxValue)
                throw new InvalidDataException(context + "integer out of range: " + key + ".");
            return (int)value;
        }

        private static bool Bool(JObject obj, string key, string context)
        {
            JToken token = obj[key];
            if (token == null || token.Type != JTokenType.Boolean)
                throw new InvalidDataException(context + "missing boolean '" + key + "'.");
            return (bool)token;
        }

        private static float Float(JObject obj, string key, string context)
        {
            return Float(obj[key], context, key);
        }

        private static float Float(JToken token, string context, string field)
        {
            if (token == null || (token.Type != JTokenType.Float && token.Type != JTokenType.Integer))
                throw new InvalidDataException(context + "missing number '" + field + "'.");
            double value = token.Value<double>();
            if (Double.IsNaN(value) || Double.IsInfinity(value))
                throw new InvalidDataException(context + "non-finite number '" + field + "'.");
            return (float)value;
        }
    }
}
