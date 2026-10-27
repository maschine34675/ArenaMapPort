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
    /// Original decorative AirPit crowd. Shared owner-sourced meshes, materials and 60 Hz source
    /// animation samples; independent source skeletons/placements. No Player, AI, physics or loot.
    /// In-place idle/cheer phases are staggered. All resources belong to the owned design scene.
    /// </summary>
    internal sealed class AirPitSpectator : MonoBehaviour
    {
        const string Tag = "[Spectator]";
        const string ActorName = "ArenaMapPort_AirPitSpectators";
        const string SceneName = "Arena_Airpit_DesignStuff";
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

        internal static void Create()
        {
            if (ArenaMapRaid.CurrentMap?.Key != "arena_airpit") return;
            var scene = SceneManager.GetSceneByName(SceneName);
            if (!scene.IsValid() || !scene.isLoaded) return;
            if (scene.GetRootGameObjects().Any(x => x.name == ActorName)) return;
            GameObject root = null;
            AirPitSpectator crowd = null;
            try
            {
                string folder = Path.Combine(Path.GetDirectoryName(typeof(Plugin).Assembly.Location) ?? "", "spectators", "arena_airpit");
                using (var file = File.OpenRead(Path.Combine(folder, "spectator.json.gz")))
                using (var gzip = new GZipStream(file, CompressionMode.Decompress))
                using (var reader = new StreamReader(gzip))
                {
                    var data = JsonConvert.DeserializeObject<SpectatorData>(reader.ReadToEnd());
                    if (data == null || data.schema != 2 || data.map != "arena_airpit" || data.actors.Length < 1 ||
                        data.actors.Length > 200 || data.source_count != data.actors.Length || data.clips.Length != 2)
                        throw new InvalidDataException("unsupported spectator package");
                    root = new GameObject(ActorName);
                    root.SetActive(false);
                    SceneManager.MoveGameObjectToScene(root, scene);
                    crowd = root.AddComponent<AirPitSpectator>();
                    crowd.Build(data, folder);
                    root.SetActive(true);
                    Plugin.Log.LogDebug($"{Tag} {crowd.ActorCount} original fans, {crowd.RendererCount} renderers; " +
                        $"shared {crowd.SharedMeshCount} meshes, {crowd.SharedMaterialCount} materials; idle/cheer staggered");
                }
            }
            catch (Exception e)
            {
                // An inactive object may fail before its first activation: OnDestroy is not sufficient.
                if (crowd != null) crowd.DisposeOwnedAssets();
                if (root != null) Object.Destroy(root);
                Plugin.Log.LogWarning($"{Tag} the AirPit crowd could not be loaded ({e.GetType().Name}: {e.Message}); the raid continues without spectators");
            }
        }

        T Own<T>(T asset) where T : Object { _assets.Add(asset); return asset; }
        static Vector3 V(float[] a) => new Vector3(a[0], a[1], a[2]);
        static Quaternion Q(float[] a) => new Quaternion(a[0], a[1], a[2], a[3]).normalized;

        void Build(SpectatorData data, string folder)
        {
            _clips = data.clips;
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
            _started = Time.time;
            _ready = true;
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
        sealed class SpectatorData { public int schema, source_count; public string map; public Actor[] actors; public Dictionary<string, MeshData> meshes; public Dictionary<string, MaterialData> materials; public Dictionary<string, TextureData> textures; public Clip[] clips; }
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
