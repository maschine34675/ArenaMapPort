using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace ArenaMapPort.Client
{
    /// <summary>
    /// One original, decorative AirPit fan. The owner-sourced sidecar retains skinning and samples
    /// Arena's Transform curves at 60 Hz. There is no Player, AI, physics, loot or Arena ECS code.
    /// Source ApplyRootMotion is false. Idle/cheer cycle automatically for this bounded prototype.
    /// The actor belongs to the design scene; its meshes/materials/textures die with it.
    /// </summary>
    internal sealed class AirPitSpectator : MonoBehaviour
    {
        const string Tag = "[Spectator]";
        const string ActorName = "ArenaMapPort_AirPitSpectator";
        const string SceneName = "Arena_Airpit_DesignStuff";
        const float FadeSeconds = .3f;
        readonly List<Object> _assets = new List<Object>();
        Transform[] _nodes;
        SpectatorData _data;
        Vector3[] _position, _scale, _fromPosition, _fromScale;
        Quaternion[] _rotation, _fromRotation;
        float _phaseStart, _fadeStart;
        int _clip, _loops;
        bool _ready;

        // Inspector evidence, without a developer hotkey or public gameplay API.
        public string CurrentAnimation;
        public int CompletedTransitions;
        public float ClipTime;

        internal static void Create()
        {
            if (ArenaMapRaid.CurrentMap?.Key != "arena_airpit") return;
            var scene = SceneManager.GetSceneByName(SceneName);
            if (!scene.IsValid() || !scene.isLoaded) return;
            if (scene.GetRootGameObjects().Any(x => x.name == ActorName)) return;
            GameObject root = null;
            AirPitSpectator actor = null;
            try
            {
                string folder = Path.Combine(Path.GetDirectoryName(typeof(Plugin).Assembly.Location) ?? "", "spectators", "arena_airpit");
                using (var file = File.OpenRead(Path.Combine(folder, "spectator.json.gz")))
                using (var gzip = new GZipStream(file, CompressionMode.Decompress))
                using (var reader = new StreamReader(gzip))
                {
                    var data = JsonConvert.DeserializeObject<SpectatorData>(reader.ReadToEnd());
                    if (data == null || data.schema != 1 || data.map != "arena_airpit" || data.nodes.Length > 400 || data.clips.Length != 2)
                        throw new InvalidDataException("unsupported spectator package");
                    root = new GameObject(ActorName);
                    root.SetActive(false);
                    SceneManager.MoveGameObjectToScene(root, scene);
                    actor = root.AddComponent<AirPitSpectator>();
                    actor.Build(data, folder);
                    root.SetActive(true);
                    Plugin.Log.LogDebug($"{Tag} one original fan at {root.transform.position}: {data.parts.Length} renderers, " +
                        $"{actor._nodes.Length} transforms; {string.Join(" / ", data.clips.Select(c => c.name))}");
                }
            }
            catch (Exception e)
            {
                // OnDestroy need not run for an object that failed before its first activation.
                if (actor != null) actor.DisposeOwnedAssets();
                if (root != null) Object.Destroy(root);
                Plugin.Log.LogWarning($"{Tag} the AirPit spectator could not be loaded ({e.GetType().Name}: {e.Message}); the raid continues without him");
            }
        }

        T Own<T>(T asset) where T : Object { _assets.Add(asset); return asset; }
        static Vector3 V(float[] a) => new Vector3(a[0], a[1], a[2]);
        static Quaternion Q(float[] a) => new Quaternion(a[0], a[1], a[2], a[3]).normalized;

        void Build(SpectatorData data, string folder)
        {
            _data = data;
            int count = data.nodes.Length;
            _nodes = new Transform[count];
            _position = new Vector3[count]; _scale = new Vector3[count]; _rotation = new Quaternion[count];
            _fromPosition = new Vector3[count]; _fromScale = new Vector3[count]; _fromRotation = new Quaternion[count];
            for (int i = 0; i < count; i++)
            {
                var row = data.nodes[i];
                if ((i == 0 && row.parent != -1) || (i > 0 && (row.parent < 0 || row.parent >= i)))
                    throw new InvalidDataException("invalid spectator hierarchy");
                var tf = i == 0 ? transform : new GameObject(row.name).transform;
                if (i > 0) tf.SetParent(_nodes[row.parent], false);
                tf.localPosition = V(row.position); tf.localRotation = Q(row.rotation); tf.localScale = V(row.scale);
                _nodes[i] = tf;
                _fromPosition[i] = tf.localPosition; _fromRotation[i] = tf.localRotation; _fromScale[i] = tf.localScale;
            }
            var textures = new Dictionary<string, Texture>();
            foreach (var pair in data.textures)
            {
                var row = pair.Value;
                if (row.files.Length == 1)
                {
                    var tex = LoadImage(folder, row.files[0], row.linear, true);
                    tex.name = row.name; tex.wrapMode = (TextureWrapMode)row.wrap; tex.filterMode = (FilterMode)row.filter;
                    tex.Apply(true, true);
                    textures.Add(pair.Key, Own(tex));
                }
                else if (row.files.Length == 6)
                {
                    Cubemap cube = null;
                    for (int face = 0; face < 6; face++)
                    {
                        var image = LoadImage(folder, row.files[face], row.linear, false);
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
            foreach (var part in data.parts)
            {
                var m = part.mesh;
                var mesh = Own(new Mesh { name = m.name, indexFormat = m.vertices.Length > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16 });
                mesh.vertices = m.vertices.Select(V).ToArray();
                mesh.normals = m.normals.Select(V).ToArray();
                mesh.tangents = m.tangents.Select(a => new Vector4(a[0], a[1], a[2], a[3])).ToArray();
                mesh.uv = m.uv.Select(a => new Vector2(a[0], a[1])).ToArray();
                mesh.subMeshCount = m.triangles.Length;
                for (int i = 0; i < m.triangles.Length; i++) mesh.SetTriangles(m.triangles[i], i);
                Renderer renderer;
                var go = _nodes[part.node].gameObject;
                if (part.bones.Length != 0)
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
                    var skin = go.AddComponent<SkinnedMeshRenderer>();
                    skin.sharedMesh = mesh; skin.bones = part.bones.Select(i => _nodes[i]).ToArray(); skin.rootBone = _nodes[part.root];
                    skin.quality = SkinQuality.Bone4;
                    // One small prototype: keep skinning alive off-screen, avoiding wrong bind-pose bounds.
                    skin.updateWhenOffscreen = true;
                    renderer = skin;
                }
                else
                {
                    go.AddComponent<MeshFilter>().sharedMesh = mesh;
                    renderer = go.AddComponent<MeshRenderer>();
                }
                renderer.sharedMaterials = part.materials.Select(id => materials[id]).ToArray();
                renderer.shadowCastingMode = ShadowCastingMode.On;
                renderer.receiveShadows = true;
                renderer.lightProbeUsage = LightProbeUsage.BlendProbes;
                renderer.reflectionProbeUsage = ReflectionProbeUsage.BlendProbes;
            }
            _phaseStart = Time.time;
            _fadeStart = float.NegativeInfinity;
            CurrentAnimation = data.clips[0].name;
            _ready = true;
            LateUpdate();
        }

        static Texture2D LoadImage(string folder, string file, bool linear, bool mips)
        {
            if (Path.GetFileName(file) != file) throw new InvalidDataException("invalid spectator texture path");
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, mips, linear);
            try
            {
                if (!ImageConversion.LoadImage(tex, File.ReadAllBytes(Path.Combine(folder, file)), false))
                    throw new InvalidDataException("invalid spectator image");
                return tex;
            }
            catch { Object.Destroy(tex); throw; }
        }

        void LateUpdate()
        {
            if (!_ready) return;
            var clip = _data.clips[_clip];
            float elapsed = Time.time - _phaseStart;
            if (elapsed >= clip.duration)
            {
                for (int i = 0; i < _nodes.Length; i++)
                {
                    _fromPosition[i] = _nodes[i].localPosition;
                    _fromRotation[i] = _nodes[i].localRotation;
                    _fromScale[i] = _nodes[i].localScale;
                }
                _loops++;
                if (_clip == 1 || _loops >= 3) { _clip = 1 - _clip; _loops = 0; CompletedTransitions++; }
                clip = _data.clips[_clip];
                _phaseStart = Time.time; _fadeStart = Time.time; elapsed = 0;
                CurrentAnimation = clip.name;
            }
            ClipTime = elapsed;
            for (int i = 0; i < _nodes.Length; i++)
            {
                _position[i] = V(_data.nodes[i].position);
                _rotation[i] = Q(_data.nodes[i].rotation);
                _scale[i] = V(_data.nodes[i].scale);
            }
            float frame = Mathf.Clamp01(elapsed / clip.duration) * (clip.frames - 1);
            int lo = (int)frame, hi = Math.Min(lo + 1, clip.frames - 1);
            float fraction = frame - lo;
            foreach (var track in clip.tracks)
            {
                int width = track.kind == 2 ? 4 : 3, a = lo * width, b = hi * width, n = track.node;
                var v = track.values;
                if (track.kind == 2)
                    _rotation[n] = Quaternion.SlerpUnclamped(new Quaternion(v[a], v[a + 1], v[a + 2], v[a + 3]).normalized,
                        new Quaternion(v[b], v[b + 1], v[b + 2], v[b + 3]).normalized, fraction);
                else
                {
                    var value = Vector3.LerpUnclamped(new Vector3(v[a], v[a + 1], v[a + 2]), new Vector3(v[b], v[b + 1], v[b + 2]), fraction);
                    if (track.kind == 1) _position[n] = value;
                    else if (track.kind == 3) _scale[n] = value;
                }
            }
            float blend = Mathf.SmoothStep(0, 1, Mathf.Clamp01((Time.time - _fadeStart) / FadeSeconds));
            for (int i = 1; i < _nodes.Length; i++) // actor root stays at its original placement (no root motion)
            {
                _nodes[i].localPosition = Vector3.LerpUnclamped(_fromPosition[i], _position[i], blend);
                _nodes[i].localRotation = Quaternion.SlerpUnclamped(_fromRotation[i], _rotation[i], blend);
                _nodes[i].localScale = Vector3.LerpUnclamped(_fromScale[i], _scale[i], blend);
            }
        }

        void OnDestroy()
        {
            DisposeOwnedAssets();
            Plugin.Log.LogDebug($"{Tag} spectator and owned assets removed");
        }

        void DisposeOwnedAssets()
        {
            _ready = false;
            foreach (var asset in _assets) if (asset != null) Object.Destroy(asset);
            _assets.Clear();
        }

        // Private package schema; no BSG content is compiled into the plugin or tracked in git.
        #pragma warning disable CS0649 // Populated by Newtonsoft.Json from the validated export.
        sealed class SpectatorData { public int schema; public string map; public Node[] nodes; public Part[] parts; public Dictionary<string, MaterialData> materials; public Dictionary<string, TextureData> textures; public Clip[] clips; }
        sealed class Node { public string name; public int parent; public float[] position, rotation, scale; }
        sealed class Part { public int node, root; public int[] bones; public MeshData mesh; public string[] materials; }
        sealed class MeshData { public string name; public float[][] vertices, normals, tangents, uv, weights, bindposes; public int[][] indices, triangles; }
        sealed class MaterialData { public string name, shader; public int queue; public string[] keywords; public Dictionary<string, float> floats; public Dictionary<string, float[]> colors; public Dictionary<string, TexEnv> textures; }
        sealed class TextureData { public string name; public bool linear; public int wrap, filter; public string[] files; }
        sealed class TexEnv { public string id; public float[] scale, offset; }
        sealed class Clip { public string name; public float duration; public int frames; public Track[] tracks; }
        sealed class Track { public int node, kind; public float[] values; }
        #pragma warning restore CS0649
    }
}
