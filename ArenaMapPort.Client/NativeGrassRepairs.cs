using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using ArenaMapPort.Shared;
using BepInEx.Configuration;
using Comfort.Common;
using EFT;
using EFT.CameraControl;
using EFT.Settings;
using GPUInstancer;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace ArenaMapPort.Client
{
    /// <summary>
    /// Native grass on Yard, stage 1: a developer-gated PROBE (PLAN-YARD item 23, shape E; design: work/tmp/yard-grass/handoff/design-final.md). Nothing changes for a player: the key
    /// <c>Map (arena_yard)/NativeGrass</c> defaults to Off and is no public option.
    ///
    /// What vanilla and Arena do: Arena's Grass scene carries two GPUInstancerPrefabManager (normal and optic camera) and a GrassInitialization component that reads
    /// StreamingAssets/Grass/ArenaGrassPrefabs_Yard.pcl (68 bytes per clump: slice index and matrix) and feeds both managers; the prefab (a LODGroup with two Foliage_Simple meshes), the prototype and
    /// the two Texture2DArrays live in sharedassets95. EFT 0.16.9.5 ships the same GPUInstancer runtime (Streets and Ground Zero use it) and its real GPUInstancer/Foliage_Simple shader.
    /// What the ported scene lacks: the package carries none of it (the Grass scene is three empty GameObjects), so Yard has no grass.
    /// What this does: at a tick in Plugin.LateUpdate, once the map's scenes are loaded, the game camera and the optic camera exist and the worker delivered the placement and the art, it builds
    /// everything through EFT's own GPUInstancer runtime API, in the order of design-final.md section 2 (shader and noise, inactive holder with two managers, activation, prototype, prototype
    /// fields, colour buffers and feed per manager, the motion-vector command-buffer fix-up of section 3), in a holder inside the map's Grass scene (never DontDestroyOnLoad). The art comes from the
    /// player's verified Arena installation (NativeGrassArtSource: pinned offsets in sharedassets95.assets.resS, read on a worker, kept in memory only, dropped after the upload), or from the BSG-free
    /// placeholder generator. Both managers are created and destroyed together (the motion-vector buffers are process-wide); teardown (key off, raid end, scene unload, camera lost or replaced,
    /// failure) removes the command buffers and the HiZ component the build added and destroys every object, material, mesh and texture it created. Any exception latches the probe for the session
    /// with ONE Warning and a teardown; later raids log Debug. The season never reaches the grass (NativeGrassColorModel: Arena's summer lists always).
    ///
    /// Status: the Unity-free core (placement, state machine, art facts, values) is unit-tested; this glue is unit-pinned by source tests only. Host check pending: every EFT-side fact (first manager
    /// Awake on a ported scene, the prototype API, the compressed array upload, the command-buffer fix-up, HiZ) is read from the decompile, not run. Logs: Debug, tag [Grass].
    /// </summary>
    internal static class NativeGrassRepairs
    {
        const string Tag = "[Grass]";

        sealed class KeySet
        {
            internal ConfigEntry<string> Mode;
            internal ConfigEntry<bool> Drawing;
            internal ConfigEntry<string> Art;
            internal ConfigEntry<string> Upload;
        }

        /// <summary>Everything one build created, filled as it is created so a half-built set can be torn down.</summary>
        sealed class Build
        {
            internal GameObject Holder;
            internal GameObject PrefabRoot;
            internal GPUInstancerPrefabManager Normal;
            internal GPUInstancerPrefabManager Optic;
            internal GPUInstancerPrefabPrototype Prototype;
            internal Material MaterialLod0;
            internal Material MaterialLod1;
            internal Mesh MeshLod0;
            internal Mesh MeshLod1;
            internal Texture2DArray TexArray;
            internal Texture2DArray NormalArray;
            internal Texture2D NormalTexture;
            internal Texture2D ProceduralNoise;
            internal Camera NormalCamera;
            internal Camera OpticCamera;
            internal bool HadHiZNormal;
            internal bool HadHiZOptic;
            internal bool DrawingApplied = true;
            internal readonly List<Material> Clones = new List<Material>();
            internal readonly List<ComputeBuffer> ColorBuffers = new List<ComputeBuffer>();
            internal string UploadPathUsed;
        }

        /// <summary>The array came out smaller than Arena's (a mip limit): no upload path can fix that, so the SetPixelData-to-CopyTexture fallback must not swallow it and misname the cause.</summary>
        sealed class GrassMipLimitException : InvalidOperationException
        {
            internal GrassMipLimitException(string message) : base(message) { }
        }

        static readonly Dictionary<string, KeySet> _keys = new Dictionary<string, KeySet>();
        static readonly NativeGrassState _state = new NativeGrassState();
        static GrassTickInputs _in;

        // The cheap flags of the tick (set by the key handlers, the scene events and the tick itself).
        static bool _hot;
        static bool _tickBroken;
        static bool _wanted;
        static bool _drawingWanted = true;
        static int _signature;
        static GrassArtKind _artKind = GrassArtKind.Arena;
        static GrassUploadPath _uploadPath = GrassUploadPath.SetPixelData;

        // The owned raid (set by the Scripts scene's sceneLoaded, cleared by its sceneUnloaded).
        static ArenaMapDescriptor _owned;
        static NativeGrassMapRow _row;
        static KeySet _ownedKeys;
        static bool[] _loaded;

        // The cameras.
        static int _lastCameraId;
        static int _lastOpticCameraId;
        static int _cameraSerial;

        // The worker.
        static GrassPreparation _prep;
        static GrassArtKind _prepKind;
        static float _prepStartedAt;
        const float WorkerTimeoutSeconds = 30f;

        static Build _build;
        static GrassPhase _lastPhase = GrassPhase.Idle;
        static GrassWait _lastWait = GrassWait.None;
        static readonly string[] CommandBufferNames = NativeGrassValues.CommandBufferNames;
        static readonly FieldInfo CommandBufferPointer = typeof(CommandBuffer).GetField("m_Ptr", BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly FieldInfo HiZBlitMaterialField = typeof(GPUInstancerHiZOcclusionGenerator).GetField("_depthBlitMaterial", BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly FieldInfo DryColorField = typeof(GPUInstancerPrefabManager).GetField("_dryColorBuffer", BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly FieldInfo HealthyColorField = typeof(GPUInstancerPrefabManager).GetField("_healthyColorBuffer", BindingFlags.Instance | BindingFlags.NonPublic);

        // ---- install ------------------------------------------------------------------------------------------------------------------------------------------------------------------

        /// <summary>Binds the developer keys of every map with a grass row (Yard) through Settings and subscribes the scene events. Never throws.</summary>
        internal static void Install()
        {
            try
            {
                foreach (var map in ArenaMapRegistry.All)
                {
                    if (NativeGrassTable.ForMap(map.Key) == null) continue;
                    string section = NativeGrassKeys.SectionOf(map.Key);
                    var keys = new KeySet
                    {
                        Mode = Plugin.Instance.Settings.Bind(section, NativeGrassKeys.Mode, NativeGrassKeys.ModeDefault,
                            new ConfigDescription("Developer probe: native grass of the original Arena data on this map (EFT's GPUInstancer, art read from the installed Arena). " +
                                "Off = nothing is built. On = built once per raid when the cameras exist; Off again tears everything down. Live.",
                                new AcceptableValueList<string>("Off", "On"))),
                        Drawing = Plugin.Instance.Settings.Bind(section, NativeGrassKeys.Drawing, NativeGrassKeys.DrawingDefault,
                            "Developer probe A/B: false stops both grass managers from computing and drawing (no teardown: buffers, the HiZ pass and the motion-vector targets stay), true draws again. Live."),
                        Art = Plugin.Instance.Settings.Bind(section, NativeGrassKeys.ArtSource, NativeGrassKeys.ArtSourceDefault,
                            new ConfigDescription("Developer probe A/B: Arena = the textures of the installed Arena, Placeholder = generated coloured blades (the pcl slice shows as hue and blade count). A change rebuilds.",
                                new AcceptableValueList<string>("Arena", "Placeholder"))),
                        Upload = Plugin.Instance.Settings.Bind(section, NativeGrassKeys.UploadPath, NativeGrassKeys.UploadPathDefault,
                            new ConfigDescription("Developer probe A/B: how the compressed mips reach the texture arrays. SetPixelData = per (slice, mip), CopyTexture = one temporary Texture2D per slice. A change rebuilds.",
                                new AcceptableValueList<string>("SetPixelData", "CopyTexture"))),
                    };
                    keys.Mode.SettingChanged += (_, __) => Reflag();
                    keys.Drawing.SettingChanged += (_, __) => Reflag();
                    keys.Art.SettingChanged += (_, __) => Reflag();
                    keys.Upload.SettingChanged += (_, __) => Reflag();
                    _keys[map.Key] = keys;
                }
                SceneManager.sceneLoaded += OnSceneLoaded;
                SceneManager.sceneUnloaded += OnSceneUnloaded;
                Plugin.Log.LogDebug($"{Tag} keys bound for {_keys.Count} map(s); the probe is off unless 'NativeGrass' is On");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"{Tag} the grass probe keys could not be bound ({e.GetType().Name}: {e.Message}); no grass probe this session");
            }
        }

        // ---- flags and events --------------------------------------------------------------------------------------------------------------------------------------------------------

        static void Reflag()
        {
            try
            {
                KeySet keys = _ownedKeys;
                if (keys != null)
                {
                    _wanted = string.Equals(keys.Mode.Value, "On", StringComparison.OrdinalIgnoreCase);
                    _drawingWanted = keys.Drawing.Value;
                    _artKind = string.Equals(keys.Art.Value, "Placeholder", StringComparison.OrdinalIgnoreCase) ? GrassArtKind.Placeholder : GrassArtKind.Arena;
                    _uploadPath = string.Equals(keys.Upload.Value, "CopyTexture", StringComparison.OrdinalIgnoreCase) ? GrassUploadPath.CopyTexture : GrassUploadPath.SetPixelData;
                }
                else
                {
                    _wanted = false;
                }
                _signature = (_artKind == GrassArtKind.Placeholder ? 1 : 0) | (_uploadPath == GrassUploadPath.CopyTexture ? 2 : 0);
                UpdateHot();
            }
            catch (Exception e)
            {
                _wanted = false;
                Plugin.Log.LogDebug($"{Tag} flags could not be read ({e.GetType().Name}: {e.Message})");
            }
        }

        static void UpdateHot()
        {
            GrassPhase phase = _state.Phase;
            _hot = _wanted || _build != null || _prep != null
                || phase == GrassPhase.Building || phase == GrassPhase.TearingDown || phase == GrassPhase.Built || phase == GrassPhase.Waiting;
        }

        static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            try
            {
                ArenaMapDescriptor map = ArenaMapRaid.MapForScriptsScene(scene.name);
                if (map != null)
                {
                    NativeGrassMapRow row = NativeGrassTable.ForMap(map.Key);
                    KeySet keys;
                    if (row != null && _keys.TryGetValue(map.Key, out keys))
                    {
                        _owned = map;
                        _row = row;
                        _ownedKeys = keys;
                        _loaded = new bool[map.AllSceneNames.Length];
                        _tickBroken = false;
                        _state.NewRaid(); // the last raid's 'game stopped' latch ends here
                        DropPreparation();
                        Reflag();
                    }
                }
                if (_owned != null)
                {
                    int at = Array.IndexOf(_owned.AllSceneNames, scene.name);
                    if (at >= 0) _loaded[at] = true;
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogDebug($"{Tag} sceneLoaded handler: {e.GetType().Name}: {e.Message}");
            }
        }

        /// <summary>The unload path: an owned scene of the grass map going away is the signal to tear down NOW (the holder lives in that scene, and the art objects would outlive it).</summary>
        static void OnSceneUnloaded(Scene scene)
        {
            try
            {
                if (_owned == null) return;
                int at = Array.IndexOf(_owned.AllSceneNames, scene.name);
                if (at >= 0) _loaded[at] = false;
                bool scripts = scene.name == _owned.ScriptsSceneName;
                if (scripts)
                {
                    // the raid is over from here on; the key keeps its value for this last tick so that the reason of the teardown reads RaidEnded, not KeyOff
                    _owned = null;
                    DropPreparation();
                }
                if (_build != null || _state.Phase == GrassPhase.Built || _state.Phase == GrassPhase.Building)
                {
                    // never wait for the next frame: the objects of the unloaded scene are already going and the art objects are ours to free
                    TickCore();
                }
                if (scripts)
                {
                    _row = null;
                    _ownedKeys = null;
                    Reflag();
                }
                UpdateHot();
            }
            catch (Exception e)
            {
                Plugin.Log.LogDebug($"{Tag} sceneUnloaded handler: {e.GetType().Name}: {e.Message}");
            }
        }

        // ---- the tick ----------------------------------------------------------------------------------------------------------------------------------------------------------------

        /// <summary>Plugin.LateUpdate: one static read while nothing is wanted or built. Never throws into the caller.</summary>
        internal static void Tick()
        {
            if (!_hot || _tickBroken) return;
            try
            {
                TickCore();
            }
            catch (Exception e)
            {
                try { OnUnexpected(e); } catch { }
            }
        }

        static void OnUnexpected(Exception e)
        {
            // a throw before the state machine would repeat every frame: say it once, stop ticking until the next raid (OnSceneLoaded clears the flag), tear down quietly
            _tickBroken = true;
            Plugin.Log.LogDebug($"{Tag} tick failed, no more ticks until the next raid: {e}");
            _state.Fail("tick: " + Brief(e));
            TearDownNow(GrassTeardownReason.Failure);
            _state.TeardownDone();
            ReportFailures();
            UpdateHot();
        }

        static void TickCore()
        {
            bool owned = _owned != null;
            _in.Wanted = _wanted;
            _in.HasMapRow = owned && _row != null;
            _in.InOwnedRaid = owned;
            _in.BuildSignature = _signature;
            _in.InputsReady = false;
            _in.InputError = null;
            if (owned)
            {
                _in.ScenesLoaded = AllScenesLoaded();
                ReadCameras();
                _in.GameStopping = IsGameStopping();
                ReadInputs();
            }
            else
            {
                _in.ScenesLoaded = false;
                _in.CameraReady = false;
                _in.OpticCameraReady = false;
                _in.GameStopping = false;
            }

            GrassAction action = _state.Tick(in _in);
            switch (action)
            {
                case GrassAction.Build:
                    DoBuild();
                    break;
                case GrassAction.Teardown:
                    TearDownNow(_state.TeardownReason);
                    _state.TeardownDone();
                    break;
            }

            if (_state.IsBuilt && _build != null && _build.DrawingApplied != _drawingWanted)
            {
                ApplyDrawing(_drawingWanted);
            }
            ReportFailures();
            if (!_wanted && _prep != null && _build == null)
            {
                DropPreparation();
            }
            LogPhaseChange();
            UpdateHot();
        }

        static bool AllScenesLoaded()
        {
            bool[] loaded = _loaded;
            for (int i = 0; i < loaded.Length; i++)
            {
                if (!loaded[i]) return false;
            }
            return true;
        }

        static void ReadCameras()
        {
            Camera game = null;
            Camera optic = null;
            if (CameraManager.Exist)
            {
                CameraManager manager = CameraManager.Instance;
                game = manager.Camera;
                OpticCameraManager opticManager = manager.OpticCameraManager;
                if (opticManager != null) optic = opticManager.Camera;
            }
            _in.CameraReady = game != null;
            _in.OpticCameraReady = optic != null;
            if (game != null && optic != null)
            {
                // OpticCameraManager.Init destroys and re-creates the optic camera, and a replaced game camera takes it along: either instance changing is a replacement
                int id = game.GetInstanceID();
                int opticId = optic.GetInstanceID();
                if (id != _lastCameraId || opticId != _lastOpticCameraId)
                {
                    _lastCameraId = id;
                    _lastOpticCameraId = opticId;
                    _cameraSerial++;
                }
            }
            _in.CameraSerial = _cameraSerial;
        }

        static bool IsGameStopping()
        {
            if (!Singleton<AbstractGame>.Instantiated) return false;
            GameStatus status = Singleton<AbstractGame>.Instance.Status;
            return status == GameStatus.Stopping || status == GameStatus.Stopped || status == GameStatus.SoftStopping;
        }

        /// <summary>The worker side of the tick: start it when the grass is wanted and the gate has verified an installation, then poll it. Never blocks.</summary>
        static void ReadInputs()
        {
            if (!NativeGrassState.WantsInputs(in _in) || _state.Failed || _build != null || _state.IsBuilt || _in.GameStopping || _state.RaidOver)
            {
                return; // nothing wanted, latched, already built, or the raid is ending: the art is read again only for the next build
            }
            if (_prep != null && _prepKind != _artKind && _build == null)
            {
                DropPreparation();
            }
            if (_prep == null)
            {
                GateState gate = ArenaInstallationGate.State;
                if (gate == GateState.Verified)
                {
                    ArenaInstallationResult result = ArenaInstallationGate.Result;
                    if (result == null || !result.Verified || string.IsNullOrEmpty(result.InstallPath))
                    {
                        _in.InputError = "arena:NoVerifiedInstallation";
                        return;
                    }
                    _prep = GrassPreparation.Start(_row, result.InstallPath, _artKind);
                    _prepKind = _artKind;
                    _prepStartedAt = Time.realtimeSinceStartup;
                    Plugin.Log.LogDebug($"{Tag} worker started: art={_artKind}, placement={_row.PlacementRelativePath}");
                }
                else if (gate == GateState.NotStarted || gate == GateState.Running || gate == GateState.TimedOut)
                {
                    return; // the check is still running (or a bundle request stopped waiting for it): pending, not a refusal
                }
                else
                {
                    _in.InputError = "arena:" + gate;
                    return;
                }
            }
            if (_prep.IsCompleted)
            {
                GrassInputs inputs = _prep.Result;
                if (inputs == null)
                {
                    _in.InputError = "worker:NoResult";
                }
                else if (!inputs.Ok)
                {
                    _in.InputError = inputs.Error;
                }
                else
                {
                    _in.InputsReady = true;
                }
            }
            else if (Time.realtimeSinceStartup - _prepStartedAt > WorkerTimeoutSeconds)
            {
                _prep.Abandon();
                _in.InputError = "worker:timeout";
            }
        }

        static void DropPreparation()
        {
            GrassPreparation prep = _prep;
            _prep = null;
            if (prep == null) return;
            prep.Abandon();
            GrassInputs result = prep.IsCompleted ? prep.Result : null;
            if (result != null) result.DropArt();
        }

        static void ReportFailures()
        {
            if (!_state.Failed) return;
            string reason;
            if (_state.TakeWarningDue(out reason))
            {
                Plugin.Log.LogWarning($"{Tag} no native grass: {reason}. The probe stays off for this session; later raids log Debug.");
            }
            if (_state.TakeLatchedNoticeDue())
            {
                Plugin.Log.LogDebug($"{Tag} no grass this raid, latched: {_state.FailureReason}");
            }
        }

        static void LogPhaseChange()
        {
            GrassPhase phase = _state.Phase;
            GrassWait wait = _state.WaitReason;
            if (phase == _lastPhase && wait == _lastWait) return;
            _lastPhase = phase;
            _lastWait = wait;
            if (phase == GrassPhase.Idle && wait == GrassWait.NotWanted) return;
            Plugin.Log.LogDebug($"{Tag} state: {_state.Describe()}");
        }

        // ---- the build ----------------------------------------------------------------------------------------------------------------------------------------------------------------

        static void DoBuild()
        {
            long start = Stopwatch.GetTimestamp();
            try
            {
                BuildCore(start);
                _state.BuildSucceeded();
            }
            catch (Exception e)
            {
                string reason = "build: " + Brief(e);
                _state.Fail(reason);
                Plugin.Log.LogDebug($"{Tag} build failed after {Ms(start):0.0} ms: {e}");
                TearDownNow(GrassTeardownReason.Failure); // the half-built set must not draw for one frame
            }
            finally
            {
                DropPreparation();
            }
        }

        static void BuildCore(long start)
        {
            GrassPreparation prep = _prep;
            GrassInputs inputs = prep != null ? prep.Result : null;
            if (inputs == null || !inputs.Ok) throw new InvalidOperationException("the inputs are not ready");
            NativeGrassMapRow row = _row;
            GrassPlan plan = inputs.Plan;
            var b = new Build();
            _build = b;
            UpdateHot();
            long t;

            Plugin.Log.LogDebug($"{Tag} inputs: art={inputs.ArtName} checks=\"{inputs.Checks}\" placement={inputs.PlacementLength} B records={plan.Count} slices={plan.HistogramText()} " +
                $"extent x {plan.MinX:0.0}..{plan.MaxX:0.0} y {plan.MinY:0.0}..{plan.MaxY:0.0} z {plan.MinZ:0.0}..{plan.MaxZ:0.0} scale {plan.MinScale:0.00}..{plan.MaxScale:0.00} " +
                $"read={inputs.BytesRead} B worker ms: placement={GrassInputs.Ms(inputs.PlacementTicks):0.0} art={GrassInputs.Ms(inputs.ArtTicks):0.0}");

            // 1. the real shader and the noise texture (design-final.md section 2 step 1; G1 section 0 point 2: a stand-in shader must never reach a manager)
            t = Stopwatch.GetTimestamp();
            string shaderPath;
            Shader shader = ResolveShader(out shaderPath);
            string noisePath;
            Texture2D noise = ResolveNoise(b, out noisePath);
            Texture2D waveNormal = Resources.Load<Texture2D>(NativeGrassValues.WaveNormalResourcePath);
            Plugin.Log.LogDebug($"{Tag} shader: path={shaderPath} name={shader.name} supported={shader.isSupported} id={shader.GetInstanceID()}; noise: path={noisePath} " +
                $"{noise.width}x{noise.height}; waveNormal: {(waveNormal != null ? "resources" : "none")}; resolve ms={Ms(t):0.0}");

            // 2. the three textures (Arena's blocks or the placeholder), uploaded and dropped
            string uploadLine = UploadTextures(b, inputs);
            Plugin.Log.LogDebug(uploadLine);

            // 3. meshes, materials, the prefab root with its LODGroup (inactive, in the Grass scene)
            t = Stopwatch.GetTimestamp();
            Scene grassScene = SceneManager.GetSceneByName(row.GrassSceneName);
            if (!grassScene.IsValid() || !grassScene.isLoaded) throw new InvalidOperationException("the Grass scene is not loaded");
            b.MeshLod0 = MakeMesh("ArenaMapPort_grass_lod0", NativeGrassValues.BuildMesh(true), new Bounds(new Vector3(0f, 0.5f, 0f), new Vector3(1f, 1f, 1f)));
            b.MeshLod1 = MakeMesh("ArenaMapPort_grass_lod1", NativeGrassValues.BuildMesh(false), new Bounds(new Vector3(0f, 0.5f, 0f), new Vector3(1f, 1f, 0f)));
            var missing = new List<string>();
            b.MaterialLod0 = MakeMaterial(shader, NativeGrassValues.Lod0MaterialName, NativeGrassValues.Lod0IsBillboard, b, noise, waveNormal, missing);
            b.MaterialLod1 = MakeMaterial(shader, NativeGrassValues.Lod1MaterialName, NativeGrassValues.Lod1IsBillboard, b, noise, waveNormal, null);
            b.PrefabRoot = MakePrefab(b, grassScene);
            Plugin.Log.LogDebug($"{Tag} prefab: root={b.PrefabRoot.name} layer={b.PrefabRoot.layer} lods=2 meshes={b.MeshLod0.vertexCount}/{b.MeshLod1.vertexCount} vertices " +
                $"materials=2 on '{b.MaterialLod0.shader.name}' propertiesMissingInShader={(missing.Count == 0 ? "none" : string.Join(",", missing.ToArray()))}; ms={Ms(t):0.0}");

            // 3b. scene, cameras and what they already carry (the HiZ component is removed on teardown only when this build added it)
            b.NormalCamera = CameraManager.Instance.Camera;
            b.OpticCamera = CameraManager.Instance.OpticCameraManager.Camera;
            if (b.NormalCamera == null || b.OpticCamera == null) throw new InvalidOperationException("a camera disappeared before the build");
            b.HadHiZNormal = b.NormalCamera.GetComponent<GPUInstancerHiZOcclusionGenerator>() != null;
            b.HadHiZOptic = b.OpticCamera.GetComponent<GPUInstancerHiZOcclusionGenerator>() != null;

            // 4. the holder, inactive, with BOTH managers added before anything wakes (design step 2); settings are assigned first (step 1)
            t = Stopwatch.GetTimestamp();
            if (GPUInstancerConstants.gpuiSettings == null)
            {
                GPUInstancerConstants.gpuiSettings = GPUInstancerSettings.GetDefaultGPUInstancerSettings();
            }
            b.Holder = new GameObject(NativeGrassValues.HolderName);
            b.Holder.layer = NativeGrassValues.Layer;
            b.Holder.SetActive(false);
            SceneManager.MoveGameObjectToScene(b.Holder, grassScene);
            b.Normal = AddManager(b.Holder, false);
            b.Optic = AddManager(b.Holder, true);

            // 5. SetActive(true): Awake and OnEnable of both managers now run with both cameras present (step 3). An exception inside them is the engine's to log, so the result is checked.
            b.Holder.SetActive(true);
            CheckManagerWokeUp(b.Normal, "normal");
            CheckManagerWokeUp(b.Optic, "optic");
            int active = GPUInstancerManager.activeManagerList != null ? GPUInstancerManager.activeManagerList.Count : -1;
            Plugin.Log.LogDebug($"{Tag} managers: normal={b.Normal.GetInstanceID()} optic={b.Optic.GetInstanceID()} activeManagers={active} holderScene={b.Holder.scene.name} " +
                $"cameras: game='{b.NormalCamera.name}'#{b.NormalCamera.GetInstanceID()} optic='{b.OpticCamera.name}'#{b.OpticCamera.GetInstanceID()} " +
                $"hiZ before: game={(b.HadHiZNormal ? "existing" : "none")} optic={(b.HadHiZOptic ? "existing" : "none")}; ms={Ms(t):0.0}");

            // 6. the prototype, only now (it reads settings and prototypeList, both created by the first activation), the same one for both managers
            t = Stopwatch.GetTimestamp();
            GPUInstancerPrefabPrototype proto = GPUInstancerAPI.DefineGameObjectAsPrefabPrototypeAtRuntime(b.Normal, b.PrefabRoot);
            if (proto == null) throw new InvalidOperationException("DefineGameObjectAsPrefabPrototypeAtRuntime returned no prototype");
            GPUInstancerPrefabPrototype again = GPUInstancerAPI.DefineGameObjectAsPrefabPrototypeAtRuntime(b.Optic, b.PrefabRoot);
            if (again != proto) throw new InvalidOperationException("the optic manager got another prototype");
            b.Prototype = proto;

            // 7. every prototype field BEFORE the first InitializeWithMatrix4x4Array (the API sets none of them: G1 section 2)
            ConfigurePrototype(proto);

            // 8. the feed per manager, in the call order of GrassInitialization.Awake (decompile GrassInitialization.cs 40-66): colour buffers, matrices, count, slice index
            Matrix4x4[] matrices = MakeMatrices(plan);
            GrassColorLists lists = NativeGrassColorModel.SummerLists;
            List<Color> dry = ToColors(lists.Dry);
            List<Color> healthy = ToColors(lists.Healthy);
            proto.enableRuntimeModifications = true; // as GrassInitialization sets it; InitializeWithMatrix4x4Array puts it back to false
            Feed(b, b.Normal, proto, matrices, plan, dry, healthy);
            Feed(b, b.Optic, proto, matrices, plan, dry, healthy);
            RecordClones(b);
            Plugin.Log.LogDebug($"{Tag} prototype: name={proto.name} shadow={proto.isShadowCasting}/{proto.shadowDistance:0}m originalShadowShader={proto.useOriginalShaderForShadow} maxDistance={proto.maxDistance:0}/{proto.maxDistanceOptic:0} " +
                $"lodHeights={NativeGrassValues.Lod0ScreenHeight:0.0000}/{NativeGrassValues.Lod1ScreenHeight:0.0000} lodBias={QualitySettings.lodBias:0.0}; clones={b.Clones.Count} " +
                $"(all on the real shader); instances={DescribeInstances(b.Normal)} / {DescribeInstances(b.Optic)}; ms={Ms(t):0.0}");

            // 9. the motion-vector command buffers (design-final.md section 3): the second Awake disposed and re-created the static buffers, so the first manager's are stale
            t = Stopwatch.GetTimestamp();
            Plugin.Log.LogDebug(FixCommandBuffers(b));
            Plugin.Log.LogDebug($"{Tag} commandBuffers: fix-up ms={Ms(t):0.0}; hiZ after: game={DescribeHiZ(b.NormalCamera, b.HadHiZNormal)} optic={DescribeHiZ(b.OpticCamera, b.HadHiZOptic)}");

            b.DrawingApplied = true;
            if (!_drawingWanted)
            {
                ApplyDrawing(false);
            }
            Plugin.Log.LogDebug(ContextLine());
            Plugin.Log.LogDebug($"{Tag} built in {Ms(start):0.0} ms on the main thread: instances={plan.Count} x2 managers, art={inputs.ArtName}, upload={b.UploadPathUsed}, drawing={b.DrawingApplied}");
            // after the cost line on purpose: the one-shot light search below is not part of what the grass costs
            Plugin.Log.LogDebug(LayerLine(b));
        }

        /// <summary>Whether layer 31 reaches the two cameras and the sun (the open host question of design-final.md section 7): a camera whose mask lacks the layer draws no grass, a light whose mask lacks it casts none. One scene search, inside the build only.</summary>
        static string LayerLine(Build b)
        {
            try
            {
                long t = Stopwatch.GetTimestamp();
                int bit = 1 << NativeGrassValues.Layer;
                var text = new System.Text.StringBuilder();
                text.Append(Tag).Append(" layers: layer ").Append(NativeGrassValues.Layer).Append(" in cullingMask: game=").Append(b.NormalCamera != null && (b.NormalCamera.cullingMask & bit) != 0)
                    .Append(" optic=").Append(b.OpticCamera != null && (b.OpticCamera.cullingMask & bit) != 0).Append("; directional lights:");
                int lights = 0;
                foreach (Light light in Object.FindObjectsOfType<Light>())
                {
                    if (light == null || light.type != LightType.Directional || !light.isActiveAndEnabled) continue;
                    lights++;
                    text.Append(" '").Append(light.name).Append("' mask=").Append((light.cullingMask & bit) != 0).Append(" shadows=").Append(light.shadows);
                }
                if (lights == 0) text.Append(" none active");
                text.Append("; ms=").Append(Ms(t).ToString("0.0", CultureInfo.InvariantCulture));
                return text.ToString();
            }
            catch (Exception e)
            {
                return $"{Tag} layers: unavailable ({e.GetType().Name})";
            }
        }

        // ---- build helpers -----------------------------------------------------------------------------------------------------------------------------------------------------------

        /// <summary>The bindings row first (the very lookup the manager's clone uses), Shader.Find only as the fallback; a shader that is not the real one is refused.</summary>
        static Shader ResolveShader(out string path)
        {
            GPUInstancerSettings settings = GPUInstancerConstants.gpuiSettings;
            if (settings == null)
            {
                settings = GPUInstancerSettings.GetDefaultGPUInstancerSettings();
                GPUInstancerConstants.gpuiSettings = settings;
            }
            Shader found = null;
            path = "none";
            GPUInstancerShaderBindings bindings = settings != null ? settings.shaderBindings : null;
            if (bindings != null && bindings.shaderInstances != null)
            {
                foreach (ShaderInstance row in bindings.shaderInstances)
                {
                    if (row != null && row.name == NativeGrassValues.ShaderName && string.IsNullOrEmpty(row.extensionCode) && row.instancedShader != null)
                    {
                        found = row.instancedShader;
                        path = "bindings-row";
                        break;
                    }
                }
            }
            if (found == null)
            {
                found = Shader.Find(NativeGrassValues.ShaderName);
                path = "Shader.Find";
            }
            if (found == null) throw new InvalidOperationException("the shader " + NativeGrassValues.ShaderName + " is not available");
            if (found.name != NativeGrassValues.ShaderName) throw new InvalidOperationException("resolved shader is '" + found.name + "', not " + NativeGrassValues.ShaderName);
            if (!found.isSupported) throw new InvalidOperationException("the shader " + NativeGrassValues.ShaderName + " is not supported");
            return found;
        }

        static Texture2D ResolveNoise(Build b, out string path)
        {
            Texture2D noise = Resources.Load<Texture2D>(NativeGrassValues.NoiseResourcePath);
            if (noise != null)
            {
                path = "resources:" + NativeGrassValues.NoiseResourcePath;
                return noise;
            }
            // a procedural grayscale noise of the same kind (smooth, tiling): the dry/healthy blend still has something to sample
            const int size = 128;
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, true, true) { name = "ArenaMapPort_GrassNoise", wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Bilinear };
            var pixels = new Color32[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float u = x / (float)size, v = y / (float)size;
                    float value = 0.5f + 0.25f * Mathf.Sin(u * 6.2831853f * 3f + Mathf.Sin(v * 6.2831853f * 2f) * 1.7f) + 0.25f * Mathf.Sin(v * 6.2831853f * 5f + Mathf.Sin(u * 6.2831853f * 4f) * 1.3f);
                    byte g = (byte)Mathf.Clamp(Mathf.RoundToInt(value * 255f), 0, 255);
                    pixels[y * size + x] = new Color32(g, g, g, 255);
                }
            }
            texture.SetPixels32(pixels);
            texture.Apply(true, true);
            b.ProceduralNoise = texture;
            path = "procedural";
            return texture;
        }

        static string UploadTextures(Build b, GrassInputs inputs)
        {
            long t = Stopwatch.GetTimestamp();
            string used = _uploadPath == GrassUploadPath.CopyTexture ? "CopyTexture" : "SetPixelData";
            string fallback = string.Empty;
            long t0 = Stopwatch.GetTimestamp();
            try
            {
                b.TexArray = CreateArray(NativeGrassArt.TexArray, NativeGrassValues.TexArrayObjectName, inputs.TexArray, _uploadPath);
                b.NormalArray = CreateArray(NativeGrassArt.NormalArray, NativeGrassValues.NormalArrayObjectName, inputs.NormalArray, _uploadPath);
            }
            catch (Exception e) when (_uploadPath == GrassUploadPath.SetPixelData && !(e is GrassMipLimitException))
            {
                // the A/B key stays honest: the fallback is said in the log line, and the other path is the documented second plausible one
                fallback = $" (SetPixelData failed: {Brief(e)}; fell back to CopyTexture)";
                DestroyObject(b.TexArray);
                DestroyObject(b.NormalArray);
                b.TexArray = null;
                b.NormalArray = null;
                used = "CopyTexture";
                t0 = Stopwatch.GetTimestamp();
                b.TexArray = CreateArray(NativeGrassArt.TexArray, NativeGrassValues.TexArrayObjectName, inputs.TexArray, GrassUploadPath.CopyTexture);
                b.NormalArray = CreateArray(NativeGrassArt.NormalArray, NativeGrassValues.NormalArrayObjectName, inputs.NormalArray, GrassUploadPath.CopyTexture);
            }
            double arraysMs = Ms(t0);
            long t1 = Stopwatch.GetTimestamp();
            b.NormalTexture = CreatePlainTexture(NativeGrassArt.NormalTexture, NativeGrassValues.NormalTextureObjectName, inputs.NormalTexture);
            double plainMs = Ms(t1);
            b.UploadPathUsed = used;
            // the buffers are dropped here: nothing keeps Arena's bytes
            inputs.DropArt();
            return $"{Tag} upload: path={used}{fallback} {DescribeArray(b.TexArray)} {DescribeArray(b.NormalArray)} normalTexture={b.NormalTexture.width}x{b.NormalTexture.height} mips={b.NormalTexture.mipmapCount} " +
                $"mipLimit={QualitySettings.globalTextureMipmapLimit} arraysMs={arraysMs:0.0} plainMs={plainMs:0.0} totalMs={Ms(t):0.0}";
        }

        static string DescribeArray(Texture2DArray a)
        {
            return $"{a.name}={a.width}x{a.height}x{a.depth} mips={a.mipmapCount} fmt={a.graphicsFormat}";
        }

        /// <summary>BC1 is GraphicsFormat.RGBA_DXT1_UNorm in this Unity (the serialized RGB_DXT1_UNorm of Arena's arrays is obsolete there and the same 8-byte block).</summary>
        static GraphicsFormat FormatOf(GrassArtRange r) => r.Format == GrassBlockFormat.Dxt5 ? GraphicsFormat.RGBA_DXT5_UNorm : GraphicsFormat.RGBA_DXT1_UNorm;

        /// <summary>
        /// A compressed Texture2DArray of exactly Arena's format (the serialized GraphicsFormat 101 or 97, UNorm: this removes the linear/sRGB question), with the raw block chains of the buffer uploaded
        /// per (slice, mip) or per slice through a temporary Texture2D. The mipmap-limit flag is set at creation (the array API has no ignoreMipmapLimit property): a limited array is refused.
        /// </summary>
        static Texture2DArray CreateArray(GrassArtRange r, string name, byte[] data, GrassUploadPath path)
        {
            if (data == null || data.Length != r.Size) throw new InvalidOperationException("art buffer of " + r.Name + " is missing");
            var array = new Texture2DArray(r.Width, r.Height, r.Slices, FormatOf(r), TextureCreationFlags.MipChain | TextureCreationFlags.IgnoreMipmapLimit, r.MipCount) { name = name };
            try
            {
                if (array.width != r.Width || array.mipmapCount != r.MipCount)
                {
                    throw new GrassMipLimitException($"{r.Name} was created as {array.width}x{array.height} with {array.mipmapCount} mips (mip limit {QualitySettings.globalTextureMipmapLimit})");
                }
                array.filterMode = (FilterMode)r.FilterMode;
                array.wrapMode = (TextureWrapMode)r.WrapMode;
                array.anisoLevel = r.Aniso;
                if (path == GrassUploadPath.SetPixelData)
                {
                    for (int slice = 0; slice < r.Slices; slice++)
                    {
                        for (int mip = 0; mip < r.MipCount; mip++)
                        {
                            array.SetPixelData(data, mip, slice, (int)r.ElementOffset(slice, mip));
                        }
                    }
                    array.Apply(false, true);
                }
                else
                {
                    CopyThroughTexture2D(array, r, data);
                }
            }
            catch
            {
                DestroyObject(array);
                throw;
            }
            return array;
        }

        static void CopyThroughTexture2D(Texture2DArray array, GrassArtRange r, byte[] data)
        {
            var temp = new Texture2D(r.Width, r.Height, FormatOf(r), r.MipCount, TextureCreationFlags.MipChain | TextureCreationFlags.IgnoreMipmapLimit) { name = "ArenaMapPort_GrassUploadTemp" };
            GCHandle pin = GCHandle.Alloc(data, GCHandleType.Pinned);
            try
            {
                IntPtr start = pin.AddrOfPinnedObject();
                int sliceBytes = (int)r.SliceBytes;
                for (int slice = 0; slice < r.Slices; slice++)
                {
                    temp.LoadRawTextureData(new IntPtr(start.ToInt64() + r.ElementOffset(slice, 0)), sliceBytes);
                    temp.Apply(false, false);
                    Graphics.CopyTexture(temp, 0, array, slice);
                }
            }
            finally
            {
                pin.Free();
                Object.DestroyImmediate(temp);
            }
        }

        static Texture2D CreatePlainTexture(GrassArtRange r, string name, byte[] data)
        {
            if (data == null || data.Length != r.Size) throw new InvalidOperationException("art buffer of " + r.Name + " is missing");
            var texture = new Texture2D(r.Width, r.Height, FormatOf(r), r.MipCount, TextureCreationFlags.MipChain | TextureCreationFlags.IgnoreMipmapLimit) { name = name };
            GCHandle pin = GCHandle.Alloc(data, GCHandleType.Pinned);
            try
            {
                texture.LoadRawTextureData(pin.AddrOfPinnedObject(), data.Length);
                texture.Apply(false, true);
            }
            catch
            {
                Object.DestroyImmediate(texture);
                throw;
            }
            finally
            {
                pin.Free();
            }
            texture.filterMode = (FilterMode)r.FilterMode;
            texture.wrapMode = (TextureWrapMode)r.WrapMode;
            texture.anisoLevel = r.Aniso;
            return texture;
        }

        static Mesh MakeMesh(string name, GrassMeshData d, Bounds bounds)
        {
            var mesh = new Mesh { name = name };
            int n = d.VertexCount;
            var positions = new Vector3[n];
            var normals = new Vector3[n];
            var tangents = new Vector4[n];
            var uvs = new Vector2[n];
            for (int i = 0; i < n; i++)
            {
                positions[i] = new Vector3(d.Positions[i * 3], d.Positions[i * 3 + 1], d.Positions[i * 3 + 2]);
                normals[i] = new Vector3(d.Normals[i * 3], d.Normals[i * 3 + 1], d.Normals[i * 3 + 2]);
                tangents[i] = new Vector4(d.Tangents[i * 4], d.Tangents[i * 4 + 1], d.Tangents[i * 4 + 2], d.Tangents[i * 4 + 3]);
                uvs[i] = new Vector2(d.Uvs[i * 2], d.Uvs[i * 2 + 1]);
            }
            mesh.vertices = positions;
            mesh.normals = normals;
            mesh.tangents = tangents;
            mesh.uv = uvs;
            mesh.SetTriangles(d.Indices, 0, false);
            mesh.bounds = bounds;
            return mesh;
        }

        static Material MakeMaterial(Shader shader, string name, float isBillboard, Build b, Texture2D noise, Texture2D waveNormal, List<string> missing)
        {
            var m = new Material(shader) { name = name, enableInstancing = true };
            foreach (GrassFloat f in NativeGrassValues.MaterialFloats)
            {
                if (m.HasProperty(f.Name)) m.SetFloat(f.Name, f.Value);
                else if (missing != null) missing.Add(f.Name);
            }
            if (m.HasProperty(NativeGrassValues.IsBillboardName)) m.SetFloat(NativeGrassValues.IsBillboardName, isBillboard);
            else if (missing != null) missing.Add(NativeGrassValues.IsBillboardName);
            foreach (GrassVector v in NativeGrassValues.MaterialVectors)
            {
                if (m.HasProperty(v.Name)) m.SetVector(v.Name, new Vector4(v.X, v.Y, v.Z, v.W)); // EFT's project is Gamma: SetVector and SetColor are the same here
                else if (missing != null) missing.Add(v.Name);
            }
            m.EnableKeyword(NativeGrassValues.EnabledKeyword);
            SetTexture(m, NativeGrassValues.TexArraySlot, b.TexArray, missing);
            SetTexture(m, NativeGrassValues.NormalArraySlot, b.NormalArray, missing);
            SetTexture(m, NativeGrassValues.NormalTextureSlot, b.NormalTexture, missing);
            SetTexture(m, NativeGrassValues.NoiseSlot, noise, missing);
            if (waveNormal != null) SetTexture(m, NativeGrassValues.WaveNormalSlot, waveNormal, missing);
            return m;
        }

        static void SetTexture(Material m, string slot, Texture texture, List<string> missing)
        {
            if (m.HasProperty(slot)) m.SetTexture(slot, texture);
            else if (missing != null) missing.Add(slot);
        }

        /// <summary>The prefab root of Arena's layout: layer 31, a LODGroup with two LODs, children lod0 (two crossed quads) and lod1 (one quad, billboard material), cast and receive shadows.</summary>
        static GameObject MakePrefab(Build b, Scene scene)
        {
            var root = new GameObject(NativeGrassValues.PrefabRootName) { layer = NativeGrassValues.Layer };
            root.SetActive(false);
            MeshRenderer r0 = MakeLod(root, NativeGrassValues.Lod0Name, b.MeshLod0, b.MaterialLod0);
            MeshRenderer r1 = MakeLod(root, NativeGrassValues.Lod1Name, b.MeshLod1, b.MaterialLod1);
            var group = root.AddComponent<LODGroup>();
            group.fadeMode = (LODFadeMode)NativeGrassValues.LodFadeMode;
            group.animateCrossFading = NativeGrassValues.LodAnimateCrossFading;
            var l0 = new LOD(NativeGrassValues.Lod0ScreenHeight, new Renderer[] { r0 }) { fadeTransitionWidth = NativeGrassValues.LodFadeTransitionWidth };
            var l1 = new LOD(NativeGrassValues.Lod1ScreenHeight, new Renderer[] { r1 }) { fadeTransitionWidth = NativeGrassValues.LodFadeTransitionWidth };
            group.SetLODs(new[] { l0, l1 });
            group.size = NativeGrassValues.LodGroupSize;
            SceneManager.MoveGameObjectToScene(root, scene);
            return root;
        }

        static MeshRenderer MakeLod(GameObject root, string name, Mesh mesh, Material material)
        {
            var child = new GameObject(name) { layer = NativeGrassValues.Layer };
            child.transform.SetParent(root.transform, false);
            child.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = child.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = NativeGrassValues.RendererCastShadows ? ShadowCastingMode.On : ShadowCastingMode.Off;
            renderer.receiveShadows = NativeGrassValues.RendererReceiveShadows;
            return renderer;
        }

        static GPUInstancerPrefabManager AddManager(GameObject holder, bool optic)
        {
            var manager = holder.AddComponent<GPUInstancerPrefabManager>();
            manager.IsOptic = optic;
            manager.isFrustumCulling = NativeGrassValues.ManagerFrustumCulling;
            manager.isOcclusionCulling = NativeGrassValues.ManagerOcclusionCulling;
            manager.maxThreads = NativeGrassValues.ManagerMaxThreads;
            manager.layerMask = NativeGrassValues.ManagerLayerMask;
            manager.minCullingDistance = NativeGrassValues.ManagerMinCullingDistance;
            manager.IsActive = true;
            return manager;
        }

        static void CheckManagerWokeUp(GPUInstancerPrefabManager manager, string label)
        {
            if (GPUInstancerConstants.gpuiSettings == null) throw new InvalidOperationException(label + " manager: no GPUInstancer settings after activation");
            if (GPUInstancerManager.activeManagerList == null || !GPUInstancerManager.activeManagerList.Contains(manager)) throw new InvalidOperationException(label + " manager is not in the active list");
            if (!manager.isInitialized || manager.runtimeDataList == null) throw new InvalidOperationException(label + " manager did not initialise (its Awake threw?)");
            if (manager.Camera == null) throw new InvalidOperationException(label + " manager found no camera");
            if (manager.MotionVectorsCommandBuffer == null) throw new InvalidOperationException(label + " manager has no motion-vector command buffer");
        }

        /// <summary>Every field Arena's prototype carries (G2 section 3), before the first instance call. The API itself sets none of them.</summary>
        static void ConfigurePrototype(GPUInstancerPrefabPrototype p)
        {
            p.isShadowCasting = NativeGrassValues.ProtoIsShadowCasting;
            p.useCustomShadowDistance = NativeGrassValues.ProtoUseCustomShadowDistance;
            p.shadowDistance = NativeGrassValues.ProtoShadowDistance;
            p.shadowLODMap = (float[])NativeGrassValues.ProtoShadowLodMap.Clone();
            p.useOriginalShaderForShadow = NativeGrassValues.ProtoUseOriginalShaderForShadow; // true: EFT ships no Hidden/GPUInstancer/ShadowsOnly
            p.cullShadows = NativeGrassValues.ProtoCullShadows;
            p.minDistance = NativeGrassValues.ProtoMinDistance;
            p.maxDistance = NativeGrassValues.ProtoMaxDistance;
            p.minDistanceOptic = NativeGrassValues.ProtoMinDistanceOptic;
            p.maxDistanceOptic = NativeGrassValues.ProtoMaxDistanceOptic;
            p.isFrustumCulling = NativeGrassValues.ProtoIsFrustumCulling;
            p.isOcclusionCulling = NativeGrassValues.ProtoIsOcclusionCulling;
            p.minCullingDistance = NativeGrassValues.ProtoMinCullingDistance;
            p.occlusionOffset = NativeGrassValues.ProtoOcclusionOffset;
            p.occlusionAccuracy = NativeGrassValues.ProtoOcclusionAccuracy;
            p.boundsOffset = Vector3.zero;
            p.isLODCrossFade = NativeGrassValues.ProtoIsLodCrossFade;
            p.isLODCrossFadeAnimate = NativeGrassValues.ProtoIsLodCrossFadeAnimate;
            p.lodFadeTransitionWidth = NativeGrassValues.ProtoLodFadeTransitionWidth;
            p.lodBiasAdjustment = NativeGrassValues.ProtoLodBiasAdjustment;
            p.checkedForBillboardExtensions = NativeGrassValues.ProtoCheckedForBillboardExtensions;
            p.useGeneratedBillboard = NativeGrassValues.ProtoUseGeneratedBillboard;
            p.isBillboardDisabled = NativeGrassValues.ProtoIsBillboardDisabled;
            p.autoUpdateTransformData = NativeGrassValues.ProtoAutoUpdateTransformData;
            p.treeType = (GPUInstancerTreeType)NativeGrassValues.ProtoTreeType;
            p.enableRuntimeModifications = NativeGrassValues.ProtoEnableRuntimeModifications;
        }

        static Matrix4x4[] MakeMatrices(GrassPlan plan)
        {
            var matrices = new Matrix4x4[plan.Count];
            float[] f = plan.Matrices;
            var m = new Matrix4x4();
            for (int i = 0; i < plan.Count; i++)
            {
                int o = i * NativeGrassPlacement.FloatsPerMatrix;
                // the plan is column-major in Unity's memory order (NativeGrassPlacement): [o + 4 * column + row]
                m.m00 = f[o + 0]; m.m10 = f[o + 1]; m.m20 = f[o + 2]; m.m30 = f[o + 3];
                m.m01 = f[o + 4]; m.m11 = f[o + 5]; m.m21 = f[o + 6]; m.m31 = f[o + 7];
                m.m02 = f[o + 8]; m.m12 = f[o + 9]; m.m22 = f[o + 10]; m.m32 = f[o + 11];
                m.m03 = f[o + 12]; m.m13 = f[o + 13]; m.m23 = f[o + 14]; m.m33 = f[o + 15];
                matrices[i] = m;
            }
            return matrices;
        }

        static List<Color> ToColors(float[] rgba)
        {
            var list = new List<Color>(rgba.Length / 4);
            for (int i = 0; i + 3 < rgba.Length; i += 4) list.Add(new Color(rgba[i], rgba[i + 1], rgba[i + 2], rgba[i + 3]));
            return list;
        }

        static void Feed(Build b, GPUInstancerPrefabManager manager, GPUInstancerPrefabPrototype proto, Matrix4x4[] matrices, GrassPlan plan, List<Color> dry, List<Color> healthy)
        {
            manager.SetColorBuffers(dry, healthy);
            if (DryColorField != null) { var buffer = DryColorField.GetValue(manager) as ComputeBuffer; if (buffer != null) b.ColorBuffers.Add(buffer); }
            if (HealthyColorField != null) { var buffer = HealthyColorField.GetValue(manager) as ComputeBuffer; if (buffer != null) b.ColorBuffers.Add(buffer); }
            GPUInstancerAPI.InitializeWithMatrix4x4Array(manager, proto, matrices);
            GPUInstancerAPI.SetInstanceCount(manager, proto, plan.Count);
            GPUInstancerAPI.DefineAndAddVariationFromArray(manager, proto, NativeGrassValues.IndexBufferName, plan.Slices);
        }

        /// <summary>Collects the materials the managers cloned and refuses any that is not on the real shader (a Standard clone would draw quads at the world origin: G1 risk R1).</summary>
        static void RecordClones(Build b)
        {
            foreach (GPUInstancerPrefabManager manager in new[] { b.Normal, b.Optic })
            {
                GPUInstancerRuntimeData data = manager.GetRuntimeData(b.Prototype, true);
                if (data == null) throw new InvalidOperationException("a manager has no runtime data for the grass prototype");
                foreach (GPUInstancerPrototypeLOD lod in data.instanceLODs)
                {
                    foreach (GPUInstancerRenderer renderer in lod.renderers)
                    {
                        foreach (Material clone in renderer.materials)
                        {
                            b.Clones.Add(clone);
                            if (clone == null || clone.shader == null || clone.shader.name != NativeGrassValues.ShaderName)
                            {
                                throw new InvalidOperationException("a manager cloned a material onto '" + (clone != null && clone.shader != null ? clone.shader.name : "null") + "', not the real grass shader");
                            }
                        }
                    }
                }
            }
        }

        static string DescribeInstances(GPUInstancerPrefabManager manager)
        {
            GPUInstancerRuntimeData data = manager.runtimeDataList != null && manager.runtimeDataList.Count > 0 ? manager.runtimeDataList[0] : null;
            return data == null ? "none" : data.instanceCount.ToString(CultureInfo.InvariantCulture) + "/" + data.bufferSize.ToString(CultureInfo.InvariantCulture) + " lods=" + (data.instanceLODs != null ? data.instanceLODs.Count : 0);
        }

        /// <summary>The settings that decide what the grass costs on this host (G1 section 6, G4 section 5): read for the lead's measurement record, never changed.</summary>
        static string ContextLine()
        {
            try
            {
                string graphics = "settings manager not instantiated";
                if (Singleton<SettingsManager>.Instantiated)
                {
                    var g = Singleton<SettingsManager>.Instance.Graphics.Settings;
                    graphics = $"GrassShadow={(bool)g.GrassShadow} TAA={g.IsTaaEnabled()} SSR={g.SSR.Value} frameLimit={(bool)g.DisableGameFramerateLimit}/{(int)g.GameFramerate} vsync={(bool)g.VSync}";
                }
                return $"{Tag} context: {graphics} motionVectors={GPUInstancerManager.bGenerateMotionVectors} screen={Screen.width}x{Screen.height} lodBias={QualitySettings.lodBias:0.0} " +
                    $"shadowDistance={QualitySettings.shadowDistance:0} mipLimit={QualitySettings.globalTextureMipmapLimit} opticResolution={CameraManager.Instance.OpticCameraManager.OpticRenderResolution} " +
                    $"frame={Time.frameCount} t={Time.realtimeSinceStartup:0.0}";
            }
            catch (Exception e)
            {
                return $"{Tag} context: unavailable ({e.GetType().Name})";
            }
        }

        static string DescribeHiZ(Camera camera, bool existed)
        {
            if (camera == null) return "camera gone";
            bool now = camera.GetComponent<GPUInstancerHiZOcclusionGenerator>() != null;
            return now ? (existed ? "existing" : "added by the build") : "none";
        }

        // ---- the motion-vector command buffers (design-final.md section 3) -------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// The second manager's Awake runs the static motion-vector block again (CreateMotionVectorsData -> DisposeMotionVectorsData resets the init flag), which releases the buffers the first
        /// manager attached. Every command buffer of the four names that is not the current one of its camera's manager is removed (the identity is the managed object or its native pointer: a
        /// camera's GetCommandBuffers may hand out wrappers), then InitializeCameraData() re-attaches the current ones on BOTH managers.
        /// </summary>
        static string FixCommandBuffers(Build b)
        {
            string before = ScanCommandBuffers(b, true, out int removed);
            b.Normal.InitializeCameraData();
            b.Optic.InitializeCameraData();
            string after = ScanCommandBuffers(b, false, out int unused);
            return $"{Tag} commandBuffers: before[{before}] removedStale={removed} after[{after}]";
        }

        static string ScanCommandBuffers(Build b, bool removeStale, out int removed)
        {
            removed = 0;
            var text = new System.Text.StringBuilder();
            ScanCamera(text, "game", b.NormalCamera, b.Normal, removeStale, ref removed);
            text.Append(" | ");
            ScanCamera(text, "optic", b.OpticCamera, b.Optic, removeStale, ref removed);
            return text.ToString();
        }

        static void ScanCamera(System.Text.StringBuilder text, string label, Camera camera, GPUInstancerPrefabManager manager, bool removeStale, ref int removed)
        {
            text.Append(label).Append(':');
            if (camera == null)
            {
                text.Append("camera gone");
                return;
            }
            CameraEvent[] events = { CameraEvent.BeforeGBuffer, CameraEvent.AfterGBuffer };
            for (int e = 0; e < events.Length; e++)
            {
                CommandBuffer current = e == 0 ? manager.MotionVectorsCommandBuffer : manager.MotionVectorsAfterCommandBuffer;
                CommandBuffer[] attached = camera.GetCommandBuffers(events[e]);
                int ours = 0, currentCount = 0, stale = 0;
                var names = new System.Text.StringBuilder();
                for (int i = 0; i < attached.Length; i++)
                {
                    CommandBuffer cb = attached[i];
                    if (cb == null || Array.IndexOf(CommandBufferNames, cb.name) < 0) continue;
                    ours++;
                    if (SameBuffer(cb, current))
                    {
                        currentCount++;
                        names.Append(names.Length > 0 ? "," : string.Empty).Append(cb.name).Append("=current");
                    }
                    else
                    {
                        stale++;
                        names.Append(names.Length > 0 ? "," : string.Empty).Append(cb.name).Append("=stale");
                        if (removeStale)
                        {
                            camera.RemoveCommandBuffer(events[e], cb);
                            removed++;
                        }
                    }
                }
                text.Append(' ').Append(events[e] == CameraEvent.BeforeGBuffer ? "Before" : "After").Append('=').Append(ours).Append("(current ").Append(currentCount).Append(", stale ").Append(stale)
                    .Append(names.Length > 0 ? ": " + names : string.Empty).Append(')');
            }
        }

        static bool SameBuffer(CommandBuffer a, CommandBuffer b)
        {
            if (a == null || b == null) return false;
            if (ReferenceEquals(a, b)) return true;
            if (CommandBufferPointer == null) return false;
            object pa = CommandBufferPointer.GetValue(a), pb = CommandBufferPointer.GetValue(b);
            return pa is IntPtr ia && pb is IntPtr ib && ia != IntPtr.Zero && ia == ib;
        }

        // ---- the A/B drawing switch ------------------------------------------------------------------------------------------------------------------------------------------------

        static void ApplyDrawing(bool on)
        {
            Build b = _build;
            if (b == null) return;
            if (b.Normal != null) b.Normal.IsActive = on;
            if (b.Optic != null) b.Optic.IsActive = on;
            b.DrawingApplied = on;
            Plugin.Log.LogDebug($"{Tag} drawing {(on ? "on" : "off")}: both managers IsActive={on} (no teardown) frame={Time.frameCount} t={Time.realtimeSinceStartup:0.0}");
        }

        // ---- teardown ----------------------------------------------------------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// Everything the build added, best effort and idempotent, every step in its own try: command buffers of the four names off both cameras, the HiZ component only where this build added it, both
        /// managers destroyed together (DestroyImmediate: OnDisable releases the instance buffers, OnDestroy disposes the process-wide motion-vector data and resets _BlendGrassMv), then the cloned
        /// materials, the colour buffers, the prototype, the prefab root, the holder, and every material, mesh and texture the build created.
        /// </summary>
        static void TearDownNow(GrassTeardownReason reason)
        {
            Build b = _build;
            _build = null;
            DropPreparation();
            if (b == null)
            {
                Plugin.Log.LogDebug($"{Tag} teardown reason={reason}: nothing built");
                UpdateHot();
                return;
            }
            long start = Stopwatch.GetTimestamp();
            int removedBuffers = 0, hiZRemoved = 0;
            // a camera replaced mid-raid: the managers' Update re-attached their buffers and a new HiZ component to the NEW camera before this teardown ran, so the cameras to clean are the recorded
            // ones, the ones the managers hold now and the ones CameraManager holds now (review 2026-11-21), and the HiZ components the managers point at are collected before they are destroyed
            var cameras = new List<Camera>();
            var managerHiZ = new List<GPUInstancerHiZOcclusionGenerator>();
            try { CollectTeardownCameras(b, cameras, managerHiZ); } catch (Exception e) { LogTeardownStep("camera collection", e); }
            foreach (Camera camera in cameras)
            {
                try { removedBuffers += RemoveNamedBuffers(camera); } catch (Exception e) { LogTeardownStep("command buffers", e); }
            }
            // the managers' camera data is gone with them: the HiZ components are ours to remove only where the build added them
            try { if (b.Normal != null) b.Normal.IsActive = false; if (b.Optic != null) b.Optic.IsActive = false; } catch (Exception e) { LogTeardownStep("manager switch-off", e); }
            try { DestroyObject(b.Normal); } catch (Exception e) { LogTeardownStep("normal manager", e); }
            try { DestroyObject(b.Optic); } catch (Exception e) { LogTeardownStep("optic manager", e); }
            foreach (Camera camera in cameras)
            {
                try
                {
                    var hiZ = camera != null ? camera.GetComponent<GPUInstancerHiZOcclusionGenerator>() : null;
                    if (hiZ == null) continue;
                    bool ours;
                    if (ReferenceEquals(camera, b.NormalCamera)) ours = !b.HadHiZNormal;
                    else if (ReferenceEquals(camera, b.OpticCamera)) ours = !b.HadHiZOptic;
                    else ours = managerHiZ.Contains(hiZ); // a replacement camera: only a component one of our managers pointed at
                    if (ours && DestroyHiZ(camera)) hiZRemoved++;
                }
                catch (Exception e) { LogTeardownStep("hiZ", e); }
            }
            int clones = 0;
            foreach (Material clone in b.Clones)
            {
                try { if (clone != null) { Object.DestroyImmediate(clone); clones++; } } catch (Exception e) { LogTeardownStep("clone", e); }
            }
            foreach (ComputeBuffer buffer in b.ColorBuffers)
            {
                try { if (buffer != null) buffer.Release(); } catch (Exception e) { LogTeardownStep("colour buffer", e); }
            }
            try { DestroyObject(b.Prototype); } catch (Exception e) { LogTeardownStep("prototype", e); }
            try { DestroyObject(b.PrefabRoot); } catch (Exception e) { LogTeardownStep("prefab", e); }
            try { DestroyObject(b.Holder); } catch (Exception e) { LogTeardownStep("holder", e); }
            try { DestroyObject(b.MaterialLod0); DestroyObject(b.MaterialLod1); } catch (Exception e) { LogTeardownStep("materials", e); }
            try { DestroyObject(b.MeshLod0); DestroyObject(b.MeshLod1); } catch (Exception e) { LogTeardownStep("meshes", e); }
            try { DestroyObject(b.TexArray); DestroyObject(b.NormalArray); DestroyObject(b.NormalTexture); DestroyObject(b.ProceduralNoise); } catch (Exception e) { LogTeardownStep("textures", e); }
            int active = GPUInstancerManager.activeManagerList != null ? GPUInstancerManager.activeManagerList.Count : -1;
            Plugin.Log.LogDebug($"{Tag} teardown reason={reason}: commandBuffersRemoved={removedBuffers} hiZRemoved={hiZRemoved} clones={clones} colourBuffers={b.ColorBuffers.Count} activeManagersLeft={active} ms={Ms(start):0.0}");
            UpdateHot();
        }

        static void AddCamera(List<Camera> cameras, Camera camera)
        {
            if (camera != null && !cameras.Contains(camera)) cameras.Add(camera);
        }

        static void CollectTeardownCameras(Build b, List<Camera> cameras, List<GPUInstancerHiZOcclusionGenerator> managerHiZ)
        {
            AddCamera(cameras, b.NormalCamera);
            AddCamera(cameras, b.OpticCamera);
            foreach (GPUInstancerPrefabManager manager in new[] { b.Normal, b.Optic })
            {
                if (manager == null) continue;
                AddCamera(cameras, manager.Camera);
                GPUInstancerHiZOcclusionGenerator hiZ = manager.cameraData != null ? manager.cameraData.hiZOcclusionGenerator : null;
                if (hiZ != null) managerHiZ.Add(hiZ);
            }
            if (CameraManager.Exist)
            {
                CameraManager current = CameraManager.Instance;
                AddCamera(cameras, current.Camera);
                if (current.OpticCameraManager != null) AddCamera(cameras, current.OpticCameraManager.Camera);
            }
        }

        static int RemoveNamedBuffers(Camera camera)
        {
            if (camera == null) return 0;
            int removed = 0;
            foreach (CameraEvent evt in new[] { CameraEvent.BeforeGBuffer, CameraEvent.AfterGBuffer })
            {
                CommandBuffer[] attached = camera.GetCommandBuffers(evt);
                for (int i = 0; i < attached.Length; i++)
                {
                    if (attached[i] != null && Array.IndexOf(CommandBufferNames, attached[i].name) >= 0)
                    {
                        camera.RemoveCommandBuffer(evt, attached[i]);
                        removed++;
                    }
                }
            }
            return removed;
        }

        static bool DestroyHiZ(Camera camera)
        {
            if (camera == null) return false;
            var hiZ = camera.GetComponent<GPUInstancerHiZOcclusionGenerator>();
            if (hiZ == null) return false;
            // OnEnable made a depth-blit material that OnDestroy never frees (decompile GPUInstancerHiZOcclusionGenerator.cs 65-72 and 104-124): free it here
            try
            {
                var blit = HiZBlitMaterialField != null ? HiZBlitMaterialField.GetValue(hiZ) as Material : null;
                if (blit != null) Object.DestroyImmediate(blit);
            }
            catch (Exception e) { LogTeardownStep("hiZ blit material", e); }
            Object.DestroyImmediate(hiZ);
            return true;
        }

        static void DestroyObject(Object o)
        {
            if (o != null) Object.DestroyImmediate(o);
        }

        static void LogTeardownStep(string what, Exception e)
        {
            Plugin.Log.LogDebug($"{Tag} teardown step '{what}' failed: {e.GetType().Name}: {e.Message}");
        }

        // ---- small helpers -----------------------------------------------------------------------------------------------------------------------------------------------------------

        static double Ms(long startTicks) => (Stopwatch.GetTimestamp() - startTicks) * 1000.0 / Stopwatch.Frequency;

        /// <summary>Exception type and the first line of its message, cut short: enough for the one Warning.</summary>
        static string Brief(Exception e)
        {
            string message = e.Message ?? string.Empty;
            int line = message.IndexOf('\n');
            if (line >= 0) message = message.Substring(0, line);
            if (message.Length > 160) message = message.Substring(0, 160);
            return e.GetType().Name + ": " + message;
        }
    }
}
