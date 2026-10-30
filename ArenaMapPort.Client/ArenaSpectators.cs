using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace ArenaMapPort.Client
{
    /// <summary>
    /// Original decorative crowds. Shared owner-sourced meshes, materials and 60 Hz source
    /// animation samples; independent source skeletons/placements. No Player, AI, physics or loot.
    /// In-place idle/cheer phases are staggered. All resources belong to the owned design scene.
    /// The package (gunzip + JSON of the graph, read and SHA-256 of the 123 shared PNGs) is prepared on a worker thread that starts
    /// when the map's Scripts scene loads; the main thread only decodes the images and builds the Unity objects.
    /// </summary>
    internal sealed class ArenaSpectators : MonoBehaviour
    {
        const string Tag = "[Spectator]";
        const float FadeSeconds = .3f;
        readonly List<Object> _assets = new List<Object>();
        readonly List<Fan> _fans = new List<Fan>();
        readonly Plane[] _planes = new Plane[6];
        Clip[] _clips;
        float _started;
        bool _ready;
        public int ActorCount, RendererCount, SharedMeshCount, SharedMaterialCount;
        public int VisibleActors, CheeringActors, CompletedTransitions;
        public float AnimationUpdateMilliseconds;
        public string MapKey;
        public int OriginalGroupsDisabled;

        static string DesignScene(string key)
        {
            switch (key)
            {
                case "arena_airpit": return "Arena_Airpit_DesignStuff";
                case "arena_equator": return "Arena_Equator_DesignStuff"; // Both location IDs share the scene/crowd.
                case "arena_bay5": return "Arena_Bay5_DesignStuff";
                case "arena_bowl": return "Arena_Bowl_DesignStuff";
                default: return null;
            }
        }

        const int JoinTimeoutMs = 30000;

        // The crowd package prepared on a worker thread since the map's Scripts scene loaded (main thread only; see Prepared).
        static Prepared _prepared;

        static string ActorName(string mapKey) => "ArenaMapPort_Spectators_" + mapKey;

        static string PluginDirectory() => Path.GetDirectoryName(typeof(Plugin).Assembly.Location) ?? "";

        /// <summary>
        /// Subscribes the early start (sceneLoaded of the map's Scripts scene) and the clean-up (its sceneUnloaded). Without this call the
        /// worker starts at <see cref="Prewarm"/> or at the latest in <see cref="Create"/>, which still overlaps hashing and decoding but
        /// not the graph parse with the rest of the loading phase.
        /// </summary>
        static bool _installed;

        internal static void Install()
        {
            if (_installed) return;
            _installed = true;
            SceneManager.sceneLoaded += OnScriptsSceneLoaded;
            SceneManager.sceneUnloaded += OnScriptsSceneUnloaded;
        }

        static void OnScriptsSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            try
            {
                var map = ArenaMapRaid.MapForScriptsScene(scene.name);
                if (map != null) PrepareInBackground(map.Key, true);
            }
            catch (Exception e)
            {
                // Never into EFT's load chain: Create loads the crowd on its own when nothing was prepared.
                Plugin.Log.LogDebug($"{Tag} the background start was skipped ({e.GetType().Name}: {e.Message})");
            }
        }

        static void OnScriptsSceneUnloaded(Scene scene)
        {
            try
            {
                if (ArenaMapRaid.MapForScriptsScene(scene.name) != null) DiscardPrepared();
            }
            catch (Exception e)
            {
                Plugin.Log.LogDebug($"{Tag} the prepared crowd could not be discarded ({e.GetType().Name}: {e.Message})");
            }
        }

        /// <summary>
        /// Starts the worker when <see cref="Create"/> would build a crowd right now (same gate). Idempotent: a prepared package of this map
        /// is kept, and the second CreateFromScene pass finds the crowd already built and starts nothing.
        /// </summary>
        internal static void Prewarm()
        {
            try
            {
                if (ShouldBuild(out string mapKey, out _, out _)) PrepareInBackground(mapKey, false);
            }
            catch (Exception e)
            {
                Plugin.Log.LogDebug($"{Tag} the background start was skipped ({e.GetType().Name}: {e.Message})");
            }
        }

        static void PrepareInBackground(string mapKey, bool restart)
        {
            string sceneName = DesignScene(mapKey);
            if (sceneName == null) return;
            if (_prepared != null)
            {
                if (!restart && _prepared.MapKey == mapKey) return;
                DiscardPrepared(); // a package of an earlier raid that never reached Create
            }
            string root = Path.Combine(PluginDirectory(), "spectators");
            string folder = Path.Combine(root, mapKey);
            if (!File.Exists(Path.Combine(folder, "spectator.json.gz"))) return; // Create reports the missing package
            _prepared = new Prepared(mapKey, sceneName, folder, Path.Combine(root, "shared"));
            Plugin.Log.LogDebug($"{Tag} {mapKey}: crowd package preparing on a worker thread");
        }

        static void DiscardPrepared()
        {
            var prepared = _prepared;
            _prepared = null;
            if (prepared != null) prepared.Cancel();
        }

        static Prepared TakePrepared(string mapKey)
        {
            var prepared = _prepared;
            _prepared = null;
            if (prepared == null) return null;
            if (prepared.MapKey == mapKey) return prepared;
            prepared.Cancel();
            return null;
        }

        /// <summary>The gate shared by <see cref="Create"/> and <see cref="Prewarm"/>: an owned design scene is loaded and holds no crowd yet.</summary>
        static bool ShouldBuild(out string mapKey, out string sceneName, out Scene scene)
        {
            mapKey = ArenaMapRaid.CurrentMap?.Key;
            sceneName = DesignScene(mapKey);
            scene = default(Scene);
            if (sceneName == null || !ArenaMapRaid.IsOwnedScene(sceneName)) return false;
            scene = SceneManager.GetSceneByName(sceneName);
            if (!scene.IsValid() || !scene.isLoaded) return false;
            string actorName = ActorName(mapKey);
            return !scene.GetRootGameObjects().Any(x => x.name == actorName);
        }

        internal static void Create()
        {
            if (!ShouldBuild(out string mapKey, out string sceneName, out Scene scene)) return;
            long begin = System.Diagnostics.Stopwatch.GetTimestamp();
            var timing = new LoadTiming();
            Prepared prepared = null;
            GameObject root = null;
            ArenaSpectators crowd = null;
            Loaded loaded = null;
            try
            {
                prepared = TakePrepared(mapKey);
                if (prepared == null)
                {
                    PrepareInBackground(mapKey, true); // nothing started early (the Scripts scene was missed): still overlap hashing and decoding
                    prepared = TakePrepared(mapKey);
                }
                string pluginDir = PluginDirectory();
                string folder = Path.Combine(pluginDir, "spectators", mapKey);
                string shared = Path.Combine(pluginDir, "spectators", "shared");
                if (prepared != null)
                {
                    long wait = System.Diagnostics.Stopwatch.GetTimestamp();
                    try { loaded = prepared.Join(JoinTimeoutMs); }
                    catch (Exception e)
                    {
                        prepared.Cancel();
                        timing.Fallback = Describe(e);
                    }
                    timing.WaitTicks += System.Diagnostics.Stopwatch.GetTimestamp() - wait;
                }
                if (loaded == null) loaded = LoadPackage(mapKey, sceneName, folder, shared); // the synchronous path of the previous releases
                timing.Worker = loaded.Textures != null;
                root = new GameObject(ActorName(mapKey));
                root.SetActive(false);
                SceneManager.MoveGameObjectToScene(root, scene);
                var data = loaded.Data;
                root.transform.localPosition = V(data.container.position);
                root.transform.localRotation = Q(data.container.rotation);
                root.transform.localScale = V(data.container.scale);
                crowd = root.AddComponent<ArenaSpectators>();
                crowd.MapKey = mapKey;
                crowd.Build(loaded, shared, timing);
                // Bay5's imported crowd still carries active accessories and colliders. Replace
                // only this owned scene root, after all replacement resources loaded successfully.
                foreach (var original in scene.GetRootGameObjects().Where(x => x.name == "SCAV_SPECTATORS"))
                {
                    if (original.activeSelf) crowd.OriginalGroupsDisabled++;
                    original.SetActive(false);
                }
                root.SetActive(true);
                long audio = System.Diagnostics.Stopwatch.GetTimestamp();
                var voicePositions = crowd._fans.Select(f => f.Position + Vector3.up * 1.6f).ToArray();
                SpectatorAudio.Create(crowd, voicePositions);
                SpectatorCrowdBed.Create(crowd, voicePositions); // the stand murmur: its own try, a child of the crowd root (removed with it)
                timing.AudioTicks = System.Diagnostics.Stopwatch.GetTimestamp() - audio;
                if (loaded.Textures != null) timing.WaitTicks += (long)(loaded.Textures.WaitMilliseconds * System.Diagnostics.Stopwatch.Frequency / 1000.0);
                Plugin.Log.LogDebug($"{Tag} {mapKey}: {crowd.ActorCount} original fans, {crowd.RendererCount} renderers; " +
                    $"shared {crowd.SharedMeshCount} meshes, {crowd.SharedMaterialCount} materials; idle/cheer staggered; " +
                    timing.Summary(System.Diagnostics.Stopwatch.GetTimestamp() - begin, loaded));
                if (timing.Fallback != null)
                    Plugin.Log.LogWarning($"{Tag} the {mapKey} crowd could not be prepared in the background ({timing.Fallback}); " +
                        "it was loaded on the main thread instead");
            }
            catch (Exception e)
            {
                // An inactive object may fail before its first activation: OnDestroy is not sufficient.
                if (crowd != null) crowd.DisposeOwnedAssets();
                if (root != null) Object.Destroy(root);
                if (prepared != null) prepared.Cancel();
                Plugin.Log.LogWarning($"{Tag} the {mapKey} crowd could not be restored ({e.GetType().Name}: {e.Message})" +
                    (timing.Fallback != null ? $"; the background load had reported {timing.Fallback}" : "") + "; the raid continues");
            }
            finally
            {
                if (loaded != null && loaded.Textures != null) loaded.Textures.Dispose();
            }
        }

        /// <summary>
        /// Reads and validates the crowd graph (gunzip + JSON, no Unity API) and opens the texture package. This is what the worker runs, and what
        /// the main thread runs when the worker failed. The graph is streamed into the serializer: the 20.7 MB of JSON never exist as one string.
        /// </summary>
        static Loaded LoadPackage(string mapKey, string sceneName, string folder, string shared)
        {
            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            SpectatorData data;
            using (var file = File.OpenRead(Path.Combine(folder, "spectator.json.gz")))
            using (var gzip = new GZipStream(file, CompressionMode.Decompress))
            using (var reader = new StreamReader(gzip, System.Text.Encoding.UTF8, true, 64 * 1024))
            using (var json = new JsonTextReader(reader))
                data = JsonSerializer.CreateDefault().Deserialize<SpectatorData>(json);
            if (data == null || data.schema != 4 || data.map != mapKey || data.source_scene != sceneName ||
                data.container == null || data.actors == null || data.clips == null || data.textures == null ||
                data.materials == null || data.meshes == null || data.actors.Length < 1 ||
                data.actors.Length > 200 || data.source_count != data.actors.Length || data.clips.Length != 2)
                throw new InvalidDataException("unsupported spectator package");
            ValidateContainer(data.container);
            long parsed = System.Diagnostics.Stopwatch.GetTimestamp();
            var package = new SpectatorTexturePackage(File.ReadAllBytes(Path.Combine(shared, "spectator-textures.json")),
                data.texture_manifest_sha256);
            return new Loaded { Data = data, Package = package, ParseTicks = parsed - start };
        }

        /// <summary>The files in the order <see cref="Build"/> reads them (one per texture, six per cubemap), for the prefetch.</summary>
        static string[] TextureOrder(SpectatorData data)
        {
            var order = new List<string>();
            foreach (var pair in data.textures)
                foreach (var file in pair.Value.files) order.Add(file);
            return order.ToArray();
        }

        /// <summary>
        /// One crowd package loaded off the main thread. Started when the map's Scripts scene loads (a raid spends tens of seconds loading
        /// after that), joined by <see cref="Create"/>. The task body never throws and never logs: a failure travels in
        /// <see cref="Loaded.Error"/> and Create falls back to <see cref="LoadPackage"/> with one Warning.
        /// </summary>
        sealed class Prepared
        {
            public readonly string MapKey;
            readonly string _sceneName, _folder, _shared;
            readonly Task<Loaded> _task;
            readonly object _gate = new object();
            bool _abandoned;
            SpectatorTexturePrefetch _textures;

            public Prepared(string mapKey, string sceneName, string folder, string shared)
            {
                MapKey = mapKey; _sceneName = sceneName; _folder = folder; _shared = shared;
                _task = Task.Factory.StartNew(() => Work(), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            }

            Loaded Work()
            {
                try
                {
                    var loaded = LoadPackage(MapKey, _sceneName, _folder, _shared);
                    lock (_gate)
                    {
                        if (_abandoned) return loaded; // nobody will decode the textures
                        _textures = loaded.Textures = new SpectatorTexturePrefetch(loaded.Package, _shared, TextureOrder(loaded.Data),
                            SpectatorTexturePrefetch.DefaultBudgetBytes);
                        _textures.Start();
                    }
                    return loaded;
                }
                catch (Exception e)
                {
                    return new Loaded { Error = e };
                }
            }

            /// <summary>The prepared package; throws when the worker failed or did not finish within <paramref name="timeoutMs"/>.</summary>
            public Loaded Join(int timeoutMs)
            {
                if (!_task.Wait(timeoutMs)) throw new TimeoutException("the worker thread did not finish within " + timeoutMs / 1000 + " s");
                var result = _task.Result;
                if (result.Error != null) throw new InvalidOperationException(result.Error.GetType().Name + ": " + result.Error.Message, result.Error);
                return result;
            }

            public void Cancel()
            {
                lock (_gate)
                {
                    _abandoned = true;
                    if (_textures != null) _textures.Dispose();
                    _textures = null;
                }
            }
        }

        sealed class Loaded
        {
            public SpectatorData Data;
            public SpectatorTexturePackage Package;
            public SpectatorTexturePrefetch Textures; // null: the main thread reads the files itself
            public long ParseTicks;
            public Exception Error;
        }

        /// <summary>The Stopwatch figures of one crowd load for the Debug summary (ticks of <see cref="System.Diagnostics.Stopwatch"/>).</summary>
        sealed class LoadTiming
        {
            public long WaitTicks, TexturesTicks, ActorsTicks, AudioTicks;
            public bool Worker;
            public string Fallback;

            static string Ms(long ticks) => (ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency).ToString("F0", CultureInfo.InvariantCulture);

            public string Summary(long totalTicks, Loaded loaded)
            {
                long hash = (long)(loaded.Package.HashMilliseconds * System.Diagnostics.Stopwatch.Frequency / 1000.0);
                return $"load total={Ms(totalTicks)} ms (wait={Ms(WaitTicks)}, hash={Ms(hash)}, parse={Ms(loaded.ParseTicks)}, " +
                    $"textures={Ms(TexturesTicks)}, actors={Ms(ActorsTicks)}, audio={Ms(AudioTicks)}; hash and parse on the " +
                    $"{(Worker ? "worker" : "main")} thread, crypto {loaded.Package.HashProvider})";
            }
        }

        /// <summary>"Type: message" of the root cause (the worker wraps what it caught).</summary>
        static string Describe(Exception e)
        {
            var cause = e.InnerException ?? e;
            return cause.GetType().Name + ": " + cause.Message;
        }

        /// <summary>The bytes of one texture file: from the worker's verified queue, or read and verified here when there is none or it failed.</summary>
        static byte[] ReadTexture(Loaded loaded, string folder, string file, LoadTiming timing)
        {
            var queue = loaded.Textures;
            if (queue != null)
            {
                try { return queue.Take(file, SpectatorTexturePrefetch.DefaultTimeoutMs); }
                catch (Exception e)
                {
                    if (timing.Fallback == null) timing.Fallback = Describe(e);
                    timing.WaitTicks += (long)(queue.WaitMilliseconds * System.Diagnostics.Stopwatch.Frequency / 1000.0);
                    loaded.Textures = null;
                    queue.Dispose();
                    timing.Worker = false;
                }
            }
            return loaded.Package.Read(folder, file);
        }

        T Own<T>(T asset) where T : Object { _assets.Add(asset); return asset; }
        static Vector3 V(float[] a) => new Vector3(a[0], a[1], a[2]);
        static Quaternion Q(float[] a) => new Quaternion(a[0], a[1], a[2], a[3]).normalized;

        static void ValidateContainer(Placement value)
        {
            if (value.position?.Length != 3 || value.rotation?.Length != 4 || value.scale?.Length != 3 ||
                value.position.Concat(value.rotation).Concat(value.scale).Any(v => float.IsNaN(v) || float.IsInfinity(v)) ||
                Math.Abs(value.rotation.Sum(v => v * v) - 1) > .002f || value.scale.Any(v => v != 1f))
                throw new InvalidDataException("invalid spectator container transform");
        }

        void Build(Loaded loaded, string folder, LoadTiming timing)
        {
            var data = loaded.Data;
            _clips = data.clips;
            long buildStart = System.Diagnostics.Stopwatch.GetTimestamp();
            var textures = new Dictionary<string, Texture>();
            foreach (var pair in data.textures)
            {
                var row = pair.Value;
                if (row.files.Length == 1)
                {
                    var tex = LoadImage(ReadTexture(loaded, folder, row.files[0], timing), row.linear, true);
                    tex.name = row.name; tex.wrapMode = (TextureWrapMode)row.wrap; tex.filterMode = (FilterMode)row.filter;
                    tex.Apply(true, true);
                    textures.Add(pair.Key, Own(tex));
                }
                else if (row.files.Length == 6)
                {
                    Cubemap cube = null;
                    for (int face = 0; face < 6; face++)
                    {
                        var image = LoadImage(ReadTexture(loaded, folder, row.files[face], timing), row.linear, false);
                        try
                        {
                            if (cube == null)
                            {
                                var format = row.linear ? UnityEngine.Experimental.Rendering.GraphicsFormat.R8G8B8A8_UNorm
                                    : UnityEngine.Experimental.Rendering.GraphicsFormat.R8G8B8A8_SRGB;
                                cube = Own(new Cubemap(image.width, format, UnityEngine.Experimental.Rendering.TextureCreationFlags.MipChain));
                                cube.name = row.name;
                            }
                            cube.SetPixels(image.GetPixels(), (CubemapFace)face);
                        }
                        finally { Object.Destroy(image); }
                    }
                    cube.Apply(true, true);
                    textures.Add(pair.Key, cube);
                }
                else throw new InvalidDataException("unsupported spectator texture dimension");
            }
            long texturesDone = System.Diagnostics.Stopwatch.GetTimestamp();
            timing.TexturesTicks = texturesDone - buildStart;
            var nativeShaders = Resources.FindObjectsOfTypeAll<Shader>();
            var materials = new Dictionary<string, Material>();
            foreach (var pair in data.materials)
            {
                var row = pair.Value;
                var shader = nativeShaders.FirstOrDefault(s => s != null && s.name == row.shader) ?? Shader.Find(row.shader);
                if (shader == null || !shader.isSupported) throw new InvalidDataException("native shader unavailable: " + row.shader);
                var mat = Own(new Material(shader) { name = row.name + " [spectator]", shaderKeywords = row.keywords });
                if (row.queue >= 0) mat.renderQueue = row.queue;
                foreach (var prop in row.floats) if (mat.HasProperty(prop.Key)) mat.SetFloat(prop.Key, prop.Value);
                foreach (var prop in row.colors) if (mat.HasProperty(prop.Key))
                    mat.SetColor(prop.Key, new Color(prop.Value[0], prop.Value[1], prop.Value[2], prop.Value[3]));
                foreach (var prop in row.textures)
                {
                    if (!mat.HasProperty(prop.Key)) continue;
                    mat.SetTexture(prop.Key, textures[prop.Value.id]);
                    mat.SetTextureScale(prop.Key, new Vector2(prop.Value.scale[0], prop.Value.scale[1]));
                    mat.SetTextureOffset(prop.Key, new Vector2(prop.Value.offset[0], prop.Value.offset[1]));
                }
                materials.Add(pair.Key, mat);
            }
            var meshes = new Dictionary<string, Mesh>();
            foreach (var pair in data.meshes)
            {
                var m = pair.Value;
                var mesh = Own(new Mesh { name = m.name, indexFormat = m.vertices.Length > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16 });
                mesh.vertices = m.vertices.Select(V).ToArray();
                mesh.normals = m.normals.Select(V).ToArray();
                mesh.tangents = m.tangents.Select(a => new Vector4(a[0], a[1], a[2], a[3])).ToArray();
                mesh.uv = m.uv.Select(a => new Vector2(a[0], a[1])).ToArray();
                mesh.subMeshCount = m.triangles.Length;
                for (int i = 0; i < m.triangles.Length; i++) mesh.SetTriangles(m.triangles[i], i);
                if (m.weights != null)
                {
                    var weights = new BoneWeight[m.weights.Length];
                    for (int i = 0; i < weights.Length; i++)
                        weights[i] = new BoneWeight { weight0 = m.weights[i][0], weight1 = m.weights[i][1], weight2 = m.weights[i][2], weight3 = m.weights[i][3],
                            boneIndex0 = m.indices[i][0], boneIndex1 = m.indices[i][1], boneIndex2 = m.indices[i][2], boneIndex3 = m.indices[i][3] };
                    mesh.boneWeights = weights;
                    mesh.bindposes = m.bindposes.Select(a =>
                    {
                        var matrix = new Matrix4x4();
                        for (int r = 0; r < 4; r++) for (int c = 0; c < 4; c++) matrix[r, c] = a[r * 4 + c];
                        return matrix;
                    }).ToArray();
                }
                mesh.RecalculateBounds();
                meshes.Add(pair.Key, mesh);
            }
            foreach (var source in data.actors)
            {
                if (source.nodes.Length < 1 || source.nodes.Length > 400)
                    throw new InvalidDataException("invalid spectator hierarchy size");
                var nodes = new Transform[source.nodes.Length];
                var byPath = new Dictionary<uint, Transform>();
                for (int i = 0; i < nodes.Length; i++)
                {
                    var row = source.nodes[i];
                    if ((i == 0 && row.parent != -1) || (i > 0 && (row.parent < 0 || row.parent >= i)))
                        throw new InvalidDataException("invalid spectator hierarchy");
                    var tf = new GameObject(row.name).transform;
                    tf.SetParent(i == 0 ? transform : nodes[row.parent], false);
                    tf.localPosition = V(row.position); tf.localRotation = Q(row.rotation); tf.localScale = V(row.scale);
                    nodes[i] = tf;
                    byPath.Add(row.path_hash, tf);
                }
                foreach (var part in source.parts)
                {
                    Renderer renderer;
                    var go = nodes[part.node].gameObject;
                    if (part.bones.Length != 0)
                    {
                        var skin = go.AddComponent<SkinnedMeshRenderer>();
                        skin.sharedMesh = meshes[part.mesh_id];
                        skin.bones = part.bones.Select(i => nodes[i]).ToArray();
                        skin.rootBone = nodes[part.root];
                        skin.quality = SkinQuality.Bone4;
                        // Covers the full standing/raised-arm envelope around the source root bone.
                        // The small serialized head bounds were bone-local, not a whole-fan bound.
                        skin.localBounds = new Bounds(Vector3.zero, Vector3.one * 6f);
                        skin.updateWhenOffscreen = false;
                        renderer = skin;
                    }
                    else
                    {
                        go.AddComponent<MeshFilter>().sharedMesh = meshes[part.mesh_id];
                        renderer = go.AddComponent<MeshRenderer>();
                    }
                    renderer.sharedMaterials = part.materials.Select(id => materials[id]).ToArray();
                    renderer.shadowCastingMode = ShadowCastingMode.On;
                    renderer.receiveShadows = true;
                    renderer.lightProbeUsage = LightProbeUsage.BlendProbes;
                    renderer.reflectionProbeUsage = ReflectionProbeUsage.BlendProbes;
                    RendererCount++;
                }
                // Source IDs give repeatable independent timing without touching Unity's global RNG.
                uint seed = unchecked((uint)source.source_id * 2654435761u);
                float idleDuration = _clips[0].duration * (3 + seed % 8);
                var fan = new Fan {
                    Position = nodes[0].position, IdleDuration = idleDuration,
                    Offset = (seed & 65535) / 65536f * (idleDuration + _clips[1].duration),
                    Targets = _clips.Select(c => c.tracks.Select(t => byPath[t.path_hash]).ToArray()).ToArray(),
                    Bounds = new Bounds(nodes[0].position + Vector3.up * 1.2f, new Vector3(4f, 6f, 4f))
                };
                _fans.Add(fan);
                UpdateFan(fan, 0, true);
            }
            ActorCount = _fans.Count; SharedMeshCount = meshes.Count; SharedMaterialCount = materials.Count;
            timing.ActorsTicks = System.Diagnostics.Stopwatch.GetTimestamp() - texturesDone;
            _started = Time.time;
            _ready = true;
        }

        static Texture2D LoadImage(byte[] bytes, bool linear, bool mips)
        {
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, mips, linear);
            try
            {
                if (!ImageConversion.LoadImage(tex, bytes, false))
                    throw new InvalidDataException("invalid spectator image");
                return tex;
            }
            catch { Object.Destroy(tex); throw; }
        }

        void LateUpdate()
        {
            if (!_ready) return;
            long stamp = System.Diagnostics.Stopwatch.GetTimestamp();
            float now = Time.time - _started;
            var camera = Camera.main;
            bool hasCamera = camera != null;
            if (hasCamera) GeometryUtility.CalculateFrustumPlanes(camera, _planes);
            VisibleActors = CheeringActors = 0;
            foreach (var fan in _fans)
            {
                float cycle = Mathf.Repeat(now + fan.Offset, fan.IdleDuration + _clips[1].duration);
                int clip = cycle >= fan.IdleDuration ? 1 : 0;
                if (clip != fan.Clip) { fan.Clip = clip; CompletedTransitions++; }
                if (clip == 1) CheeringActors++;
                bool visible = !hasCamera || GeometryUtility.TestPlanesAABB(_planes, fan.Bounds);
                if (!visible) { fan.WasVisible = false; continue; }
                VisibleActors++;
                if (fan.WasVisible && now < fan.NextUpdate) continue;
                UpdateFan(fan, now, false);
                fan.WasVisible = true;
                float distanceSquared = hasCamera ? (camera.transform.position - fan.Position).sqrMagnitude : 0;
                fan.NextUpdate = now + (distanceSquared > 45f * 45f ? 1f / 15 : 1f / 30);
            }
            AnimationUpdateMilliseconds = (float)((System.Diagnostics.Stopwatch.GetTimestamp() - stamp) * 1000.0 /
                System.Diagnostics.Stopwatch.Frequency);
        }

        void UpdateFan(Fan fan, float now, bool initial)
        {
            float cycle = Mathf.Repeat(now + fan.Offset, fan.IdleDuration + _clips[1].duration);
            int index = cycle >= fan.IdleDuration ? 1 : 0;
            float segment = index == 1 ? cycle - fan.IdleDuration : cycle;
            var clip = _clips[index];
            float time = index == 1 ? segment : Mathf.Repeat(segment, clip.duration);
            float frame = Mathf.Clamp01(time / clip.duration) * (clip.frames - 1);
            float blend = Mathf.SmoothStep(0, 1, Mathf.Clamp01(segment / FadeSeconds));
            var previous = _clips[1 - index];
            // Export validation pins the same ordered path/kind bindings in both clips.
            for (int i = 0; i < clip.tracks.Length; i++)
            {
                var track = clip.tracks[i];
                var target = fan.Targets[index][i];
                if (track.path_hash == 0) continue; // source ApplyRootMotion=false
                Vector4 value = Sample(track, frame, clip.frames);
                if (blend < 1)
                {
                    Vector4 from = Sample(previous.tracks[i], previous.frames - 1, previous.frames);
                    value = Mix(from, value, blend, track.kind == 2);
                }
                if (track.kind == 2)
                {
                    var rotation = new Quaternion(value.x, value.y, value.z, value.w);
                    if (initial || target.localRotation != rotation) target.localRotation = rotation;
                }
                else if (track.kind == 1)
                {
                    Vector3 position = value;
                    if (initial || target.localPosition != position) target.localPosition = position;
                }
                else
                {
                    Vector3 scale = value;
                    if (initial || target.localScale != scale) target.localScale = scale;
                }
            }
            fan.Clip = index;
        }

        static Vector4 Sample(Track track, float frame, int frames)
        {
            int lo = (int)frame, hi = Math.Min(lo + 1, frames - 1), width = track.kind == 2 ? 4 : 3;
            int a = lo * width, b = hi * width;
            var v = track.values;
            var first = new Vector4(v[a], v[a + 1], v[a + 2], width == 4 ? v[a + 3] : 0);
            var second = new Vector4(v[b], v[b + 1], v[b + 2], width == 4 ? v[b + 3] : 0);
            return Mix(first, second, frame - lo, width == 4);
        }

        static Vector4 Mix(Vector4 a, Vector4 b, float amount, bool rotation)
        {
            if (!rotation) return Vector4.LerpUnclamped(a, b, amount);
            var q = Quaternion.SlerpUnclamped(new Quaternion(a.x, a.y, a.z, a.w), new Quaternion(b.x, b.y, b.z, b.w), amount);
            return new Vector4(q.x, q.y, q.z, q.w);
        }

        void OnDestroy()
        {
            DisposeOwnedAssets();
            // What a mode plugin set with SpectatorCrowd.SetExcitement belongs to this raid's crowd. The bed (a child of the root) resets it too; this covers a package without
            // a bed and a bed that failed. A newer raid's live bed is left alone.
            if (SpectatorCrowdBed.Current == null) SpectatorCrowd.State.Reset();
            Plugin.Log.LogDebug($"{Tag} crowd and shared assets removed");
        }

        void DisposeOwnedAssets()
        {
            _ready = false;
            _fans.Clear();
            _clips = null;
            foreach (var asset in _assets) if (asset != null) Object.Destroy(asset);
            _assets.Clear();
        }

        sealed class Fan
        {
            public Transform[][] Targets;
            public Vector3 Position;
            public Bounds Bounds;
            public float IdleDuration, Offset, NextUpdate;
            public int Clip;
            public bool WasVisible;
        }

        // Private package schema; no BSG content is compiled into the plugin or tracked in git.
        #pragma warning disable CS0649 // Populated by Newtonsoft.Json from the validated export.
        sealed class SpectatorData { public int schema, source_count; public string map, source_scene, texture_manifest_sha256; public Placement container; public Actor[] actors; public Dictionary<string, MeshData> meshes; public Dictionary<string, MaterialData> materials; public Dictionary<string, TextureData> textures; public Clip[] clips; }
        sealed class Placement { public float[] position, rotation, scale; }
        sealed class Actor { public long source_id; public string name; public Node[] nodes; public Part[] parts; }
        sealed class Node { public string name; public uint path_hash; public int parent; public float[] position, rotation, scale; }
        sealed class Part { public int node, root; public int[] bones; public string mesh_id; public string[] materials; }
        sealed class MeshData { public string name; public float[][] vertices, normals, tangents, uv, weights, bindposes; public int[][] indices, triangles; }
        sealed class MaterialData { public string name, shader; public int queue; public string[] keywords; public Dictionary<string, float> floats; public Dictionary<string, float[]> colors; public Dictionary<string, TexEnv> textures; }
        sealed class TextureData { public string name; public bool linear; public int wrap, filter; public string[] files; }
        sealed class TexEnv { public string id; public float[] scale, offset; }
        sealed class Clip { public string name; public float duration; public int frames; public Track[] tracks; }
        sealed class Track { public uint path_hash; public int kind; public float[] values; }
        #pragma warning restore CS0649
    }
}
