using System;
using System.Collections.Generic;
using System.Globalization;
using ArenaMapPort.Shared;
using BepInEx.Configuration;
using Comfort.Common;
using EFT;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ArenaMapPort.Client
{
    /// <summary>
    /// Map-authored water decals have no physics surface. Translate the existing native ground
    /// result only inside their visible mask, without creating collision or playing audio.
    /// Per-map data (2026-10-25, docs/progress/puddle-footsteps-all-maps.md): a map takes part when its descriptor lists
    /// <see cref="ArenaMapDescriptor.PuddleWaterMaterialNames"/> (Bay5 since 2026-09-28; Equator, Air Pit and Bowl since
    /// 2026-10-25), and <see cref="ArenaMapDescriptor.PuddleExtraSurfaces"/> names ground surfaces besides Concrete/Asphalt that
    /// the Puddle set may replace (Bowl: its whole field is one soil collider). Every map has its own
    /// "Water (&lt;key&gt;)/PuddleFootsteps" developer switch (Bay5's key is the one of 2026-09-28, unchanged). The cache is
    /// built lazily on the first eligible footstep and paced by <see cref="PuddleCacheGate"/>.
    /// </summary>
    internal static class PuddleFootstepRepairs
    {
        const string Tag = "[PuddleFootsteps] ";
        const string ShaderName = "Decal/Water Deferred Decal";
        internal const float MinimumCoverage = 0.1f;
        internal const float MaximumFootPlaneDistance = 0.16f;
        static readonly Dictionary<string, ConfigEntry<bool>> Enabled = new Dictionary<string, ConfigEntry<bool>>();
        static readonly PuddleCacheGate Gate = new PuddleCacheGate();
        static bool _installed, _dirty = true, _warned;
        static ArenaMapDescriptor _cacheMap;     // the map the cache was built (or is being built) for
        static ArenaMapDescriptor _extraMap;     // the map _extraSurfaces was parsed for
        static BaseBallistic.ESurfaceSound[] _extraSurfaces = new BaseBallistic.ESurfaceSound[0];
        static readonly List<Entry> Entries = new List<Entry>();
        static readonly Dictionary<Texture2D, Mask> Masks = new Dictionary<Texture2D, Mask>();
        static readonly List<Material> CurrentMaterials = new List<Material>();
        static Observation _last;

        sealed class Entry
        {
            internal MeshRenderer Renderer;
            internal MeshFilter Filter;
            internal Mesh Mesh;
            internal Material Material;
            internal Mask Mask;
            internal Vector3[] Vertices;
            internal Vector2[] UV;
            internal Color[] Colors;
            internal int[] Triangles;
            internal int Submesh;
        }

        sealed class Mask
        {
            internal int Width, Height;
            internal float[] Red;
            internal Texture2D Source;
        }

        struct Match
        {
            internal Entry Entry;
            internal float Coverage, PlaneDistance, Red;
            internal Vector2 UV;
        }

        struct Observation
        {
            internal Player Player;
            internal ValueTuple<bool, BaseBallistic.ESurfaceSound> Native, Result;
            internal Match Match;
        }

        internal static void Install(Harmony harmony)
        {
            if (_installed) return;
            var target = AccessTools.Method(typeof(Player), nameof(Player.CalculateMovementSurface));
            if (target == null)
            {
                Plugin.Log.LogWarning(Tag + "native surface query unavailable; puddle translation disabled.");
                return;
            }
            // One developer switch per participating map, "Water (<key>)/PuddleFootsteps". Bay5's key and text are the
            // ones of 2026-09-28 (a saved value keeps applying); the other maps bind the same key name in their own section.
            foreach (var map in ArenaMapRegistry.All)
            {
                if (map.PuddleWaterMaterialNames.Length == 0) continue;
                var setting = Plugin.Instance.Settings.Bind("Water (" + map.Key + ")", "PuddleFootsteps", true,
                    "Use native puddle footsteps on " + (map.Key == ArenaMapRegistry.Bay5.Key ? "Bay5's" : "this map's") +
                    " original water decals. Matches mesh triangles and " +
                    "the shader's red texture mask (at least 10% coverage), within 16 cm of the feet. " +
                    "Oil, dry/faded edges, elevated surfaces and other ground materials stay unchanged. Live." +
                    (map.PuddleExtraSurfaces.Length > 0
                        ? " Also replaces " + string.Join(", ", map.PuddleExtraSurfaces) + " ground under the decals." : ""));
                setting.SettingChanged += (_, __) => Reset();
                Enabled[map.Key] = setting;
            }
            harmony.Patch(target,
                postfix: new HarmonyMethod(typeof(PuddleFootstepRepairs), nameof(AfterSurface)));
            SceneManager.sceneLoaded += (scene, _) => { if (ArenaMapRaid.IsOwnedScene(scene.name)) Reset(); };
            SceneManager.sceneUnloaded += scene => { if (ArenaMapRaid.IsOwnedScene(scene.name)) Reset(); };
            _installed = true;
        }

        /// <summary>The current map when it lists water materials, otherwise null (every other raid, the hideout, vanilla maps).</summary>
        static ArenaMapDescriptor PuddleMap()
        {
            var map = ArenaMapRaid.CurrentMap;
            return map != null && map.PuddleWaterMaterialNames.Length > 0 ? map : null;
        }

        static void Reset()
        {
            Entries.Clear();
            Masks.Clear(); // Only managed pixel arrays persist; readback objects are released immediately.
            CurrentMaterials.Clear();
            _dirty = true;
            _cacheMap = null;
            Gate.Reset();
            _warned = false;
            _last = default;
        }

        static void AfterSurface(Player __instance, ref ValueTuple<bool, BaseBallistic.ESurfaceSound> __result)
        {
            var map = PuddleMap();
            if (map == null || __instance == null) return;
            var native = __result;
            Match match = default;
            try
            {
                if (Enabled.TryGetValue(map.Key, out var enabled) && enabled.Value && Eligible(map, native) && EnsureCache(map))
                {
                    match = FindMatch(__instance.Position);
                    if (match.Entry != null && match.Coverage >= MinimumCoverage)
                    {
                        __result = (native.Item1, BaseBallistic.ESurfaceSound.Puddle);
                        if (NoiseProbe.Armed) NoiseProbe.NoteAt(NoiseEv.Puddle, __instance.Position, __instance.IsYourPlayer ? "you" : "bot");
                    }
                }
            }
            catch (Exception error)
            {
                // A GPU readback failure must not be retried during every later ground query.
                // A new scene/raid or config toggle explicitly permits a fresh attempt.
                Entries.Clear();
                Masks.Clear();
                _dirty = false;
                if (!_warned)
                {
                    _warned = true;
                    Plugin.Log.LogWarning(Tag + "keeping native surface after inspection failure: " + error);
                }
            }
            if (__instance.IsYourPlayer)
                _last = new Observation { Player = __instance, Native = native, Result = __result, Match = match };
        }

        // Concrete and Asphalt are always eligible; the map's PuddleExtraSurfaces add to them (Bowl: Soil).
        static bool Eligible(ArenaMapDescriptor map, ValueTuple<bool, BaseBallistic.ESurfaceSound> result)
        {
            if (!result.Item1) return false;
            if (result.Item2 == BaseBallistic.ESurfaceSound.Concrete || result.Item2 == BaseBallistic.ESurfaceSound.Asphalt) return true;
            var extra = ExtraSurfaces(map);
            for (int i = 0; i < extra.Length; i++)
                if (extra[i] == result.Item2) return true;
            return false;
        }

        // The descriptor names the extra surfaces as text (Shared does not reference EFT); parse them once per map.
        static BaseBallistic.ESurfaceSound[] ExtraSurfaces(ArenaMapDescriptor map)
        {
            if (_extraMap == map) return _extraSurfaces;
            var parsed = new List<BaseBallistic.ESurfaceSound>();
            foreach (string name in map.PuddleExtraSurfaces)
            {
                if (Enum.TryParse(name, false, out BaseBallistic.ESurfaceSound surface) &&
                    Enum.IsDefined(typeof(BaseBallistic.ESurfaceSound), surface))
                    parsed.Add(surface);
                else
                    Plugin.Log.LogWarning(Tag + map.Key + ": '" + name + "' in PuddleExtraSurfaces is not a ground surface; ignored.");
            }
            _extraSurfaces = parsed.ToArray();
            _extraMap = map;
            return _extraSurfaces;
        }

        static bool EnsureCache(ArenaMapDescriptor map)
        {
            if (_cacheMap != map)
            {
                // A different map than the cache was built for (every scene change already resets; this is the cheap guard).
                Reset();
                _cacheMap = map;
            }
            if (!_dirty) return true;
            if (!Gate.TryBegin(Time.unscaledTime)) return false;
            foreach (string name in map.AllSceneNames)
            {
                var scene = SceneManager.GetSceneByName(name);
                if (scene.IsValid() && scene.isLoaded) continue;
                // Cheap check, repeated each second until the scene is there; say it once so a cache that never builds is not silent.
                if (Gate.ReportScenesMissing())
                    Plugin.Log.LogDebug(Tag + "water cache of " + map.Key + " not built: scene '" + name + "' is not loaded.");
                return false;
            }
            Entries.Clear();
            bool waitingForRebind = false;
            int unready = 0;
            foreach (string name in map.AllSceneNames)
            {
                if (map.IsBackgroundScene(name)) continue;
                foreach (var root in SceneManager.GetSceneByName(name).GetRootGameObjects())
                foreach (var renderer in root.GetComponentsInChildren<MeshRenderer>(true))
                {
                    if (!Rendered(renderer)) continue;
                    var materials = renderer.sharedMaterials;
                    var filter = renderer.GetComponent<MeshFilter>();
                    var mesh = filter != null ? filter.sharedMesh : null;
                    if (mesh == null || !mesh.isReadable || materials.Length != mesh.subMeshCount) continue;
                    for (int sub = 0; sub < materials.Length; sub++)
                    {
                        var material = materials[sub];
                        if (material == null || !IsWaterMaterial(map, material)) continue;
                        if (material.shader == null || material.shader.name != ShaderName)
                        {
                            waitingForRebind = true;
                            unready++;
                            continue;
                        }
                        var texture = material.GetTexture("_MainTex") as Texture2D;
                        if (texture == null) continue;
                        if (!Masks.TryGetValue(texture, out var mask))
                        {
                            mask = ReadMask(texture);
                            Masks.Add(texture, mask);
                        }
                        Vector3[] vertices = mesh.vertices;
                        Vector2[] uv = mesh.uv;
                        if (uv.Length != vertices.Length || mesh.GetTopology(sub) != MeshTopology.Triangles) continue;
                        Entries.Add(new Entry { Renderer = renderer, Filter = filter, Mesh = mesh, Material = material,
                            Mask = mask, Vertices = vertices, UV = uv, Colors = mesh.colors,
                            Triangles = mesh.GetTriangles(sub), Submesh = sub });
                    }
                }
            }
            // A listed water renderer that does not yet wear the native shader (ShaderRebind has not reached it) asks for another
            // scan, but only MaxFullScans times: every scan walks all renderers of the map, and an unbounded wait repeated that
            // once a second while the player walked on concrete.
            var scan = Gate.AfterFullScan(waitingForRebind);
            _dirty = scan == PuddleScanResult.Retry;
            if (scan == PuddleScanResult.GaveUp)
                Plugin.Log.LogDebug(Tag + "water cache of " + map.Key + ": " + unready + " listed water material slot(s) still lack shader '" +
                    ShaderName + "' after " + Gate.FullScans + " scans; keeping the " + Entries.Count + " surfaces that have it.");
            if (!_dirty)
                Plugin.Log.LogDebug(Tag + "cached water surfaces=" + Entries.Count + " masks=" + Masks.Count +
                    " colorSpace=" + QualitySettings.activeColorSpace + " minimumShaderCoverage=" + F(MinimumCoverage) +
                    " maximumFootPlaneDistance=" + F(MaximumFootPlaneDistance));
            return !_dirty;
        }

        static bool IsWaterMaterial(ArenaMapDescriptor map, Material material)
        {
            // Reviewed source water identities belong to map data: the same native shader also
            // renders oil, and some water objects themselves are misleadingly named puddle_oil.
            foreach (string name in map.PuddleWaterMaterialNames)
                if (material.name == name || material.name == name + " (Instance)") return true;
            return false;
        }

        // isVisible follows the camera frustum; a puddle under/behind the feet must still count.
        static bool Rendered(MeshRenderer renderer) => renderer != null && renderer.enabled &&
            renderer.gameObject.activeInHierarchy && !renderer.forceRenderingOff;

        static Match FindMatch(Vector3 feet)
        {
            Match best = default;
            foreach (var entry in Entries)
            {
                if (!Rendered(entry.Renderer) || entry.Filter == null || entry.Filter.sharedMesh != entry.Mesh ||
                    entry.Material == null || entry.Material.shader == null || entry.Material.shader.name != ShaderName)
                    continue;
                Bounds bounds = entry.Renderer.bounds;
                if (feet.x < bounds.min.x || feet.x > bounds.max.x || feet.z < bounds.min.z || feet.z > bounds.max.z ||
                    feet.y < bounds.min.y - MaximumFootPlaneDistance || feet.y > bounds.max.y + MaximumFootPlaneDistance)
                    continue;
                CurrentMaterials.Clear();
                entry.Renderer.GetSharedMaterials(CurrentMaterials);
                if (entry.Submesh >= CurrentMaterials.Count || CurrentMaterials[entry.Submesh] != entry.Material ||
                    entry.Material.GetTexture("_MainTex") != entry.Mask.Source) continue;
                Matrix4x4 world = entry.Renderer.localToWorldMatrix;
                for (int i = 0; i < entry.Triangles.Length; i += 3)
                {
                    int ia = entry.Triangles[i], ib = entry.Triangles[i + 1], ic = entry.Triangles[i + 2];
                    Vector3 a = world.MultiplyPoint3x4(entry.Vertices[ia]);
                    Vector3 b = world.MultiplyPoint3x4(entry.Vertices[ib]);
                    Vector3 c = world.MultiplyPoint3x4(entry.Vertices[ic]);
                    if (!TryFootTriangle(feet, a, b, c, out var weights, out float height)) continue;
                    Vector2 uv = entry.UV[ia] * weights.x + entry.UV[ib] * weights.y + entry.UV[ic] * weights.z;
                    float alpha = entry.Colors.Length == entry.Vertices.Length
                        ? entry.Colors[ia].a * weights.x + entry.Colors[ib].a * weights.y + entry.Colors[ic].a * weights.z : 1f;
                    Vector2 scale = entry.Material.GetTextureScale("_MainTex");
                    uv = Vector2.Scale(uv, scale) + entry.Material.GetTextureOffset("_MainTex");
                    float red = Sample(entry.Mask, uv);
                    float level = entry.Material.IsKeywordEnabled("USERAIN")
                        ? Shader.GetGlobalFloat("_WaterLevel") : entry.Material.GetFloat("_EditorWaterLevel");
                    float coverage = Coverage(red, alpha, level, entry.Material.GetFloat("_FadeStrength"));
                    if (best.Entry == null || coverage > best.Coverage)
                        best = new Match { Entry = entry, Coverage = coverage, UV = uv, Red = red, PlaneDistance = height };
                }
            }
            return best;
        }

        internal static bool TryFootTriangle(Vector3 feet, Vector3 a, Vector3 b, Vector3 c,
            out Vector3 weights, out float height)
        {
            weights = default;
            height = 0f;
            Vector3 ab = b - a, ac = c - a, ap = feet - a;
            float determinant = ab.x * ac.z - ab.z * ac.x;
            if (Mathf.Abs(determinant) < 0.000001f) return false;
            float u = (ap.x * ac.z - ap.z * ac.x) / determinant;
            float v = (ab.x * ap.z - ab.z * ap.x) / determinant;
            if (u < 0f || v < 0f || u + v > 1f) return false;
            weights = new Vector3(1f - u - v, u, v);
            float planeY = a.y * weights.x + b.y * weights.y + c.y * weights.z;
            height = feet.y - planeY;
            return Mathf.Abs(height) <= MaximumFootPlaneDistance;
        }

        // Shader 283, Decal/Water Deferred Decal. MainTex alpha and _Color.a are not the
        // surface-coverage mask: the native specular/normal passes use this red-channel term.
        internal static float Coverage(float red, float vertexAlpha, float waterLevel, float fadeStrength) =>
            Mathf.Clamp01((red + vertexAlpha * waterLevel - 1f) * fadeStrength);

        static Mask ReadMask(Texture2D source)
        {
            RenderTexture previous = RenderTexture.active;
            bool previousSrgb = GL.sRGBWrite;
            RenderTexture target = null;
            Texture2D readable = null;
            try
            {
                target = RenderTexture.GetTemporary(source.width, source.height, 0,
                    RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
                GL.sRGBWrite = false;
                Graphics.Blit(source, target);
                RenderTexture.active = target;
                readable = new Texture2D(source.width, source.height, TextureFormat.RGBAFloat, false, true);
                readable.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0, false);
                readable.Apply(false, false);
                Color[] pixels = readable.GetPixels();
                var red = new float[pixels.Length];
                for (int i = 0; i < pixels.Length; i++) red[i] = pixels[i].r;
                // GPU sampling already follows the active color space. In this game's Gamma
                // project, converting these values again would incorrectly shrink wet areas.
                return new Mask { Width = source.width, Height = source.height, Red = red, Source = source };
            }
            finally
            {
                RenderTexture.active = previous;
                GL.sRGBWrite = previousSrgb;
                if (readable != null) UnityEngine.Object.Destroy(readable);
                if (target != null) RenderTexture.ReleaseTemporary(target);
            }
        }

        static float Sample(Mask mask, Vector2 uv)
        {
            float x = uv.x * mask.Width - 0.5f, y = uv.y * mask.Height - 0.5f;
            int ix = Mathf.FloorToInt(x), iy = Mathf.FloorToInt(y);
            float tx = x - ix, ty = y - iy;
            int x0 = Wrap(ix, mask.Width, mask.Source.wrapModeU), x1 = Wrap(ix + 1, mask.Width, mask.Source.wrapModeU);
            int y0 = Wrap(iy, mask.Height, mask.Source.wrapModeV), y1 = Wrap(iy + 1, mask.Height, mask.Source.wrapModeV);
            return Mathf.Lerp(Mathf.Lerp(mask.Red[y0 * mask.Width + x0], mask.Red[y0 * mask.Width + x1], tx),
                Mathf.Lerp(mask.Red[y1 * mask.Width + x0], mask.Red[y1 * mask.Width + x1], tx), ty);
        }

        static int Wrap(int value, int size, TextureWrapMode mode)
        {
            if (mode == TextureWrapMode.Clamp) return Mathf.Clamp(value, 0, size - 1);
            if (mode == TextureWrapMode.MirrorOnce) return Mathf.Clamp(value < 0 ? -value - 1 : value, 0, size - 1);
            if (mode == TextureWrapMode.Mirror)
            {
                int p = ((value % (size * 2)) + size * 2) % (size * 2);
                return p < size ? p : size * 2 - p - 1;
            }
            return ((value % size) + size) % size;
        }

        internal static void Probe()
        {
            if (!DeveloperOptions.Enabled) return;
            var map = PuddleMap();
            if (!_installed || map == null) return;
            try
            {
                var player = Singleton<GameWorld>.Instantiated ? Singleton<GameWorld>.Instance.MainPlayer : null;
                if (player == null) return;
                var result = player.CalculateMovementSurface(); // Captures the unmodified native result in our postfix.
                Match match = EnsureCache(map) ? FindMatch(player.Position) : default;
                Plugin.Log.LogDebug(Tag + "map=" + map.Key + " enabled=" + (Enabled.TryGetValue(map.Key, out var enabled) && enabled.Value) +
                    " cached=" + Entries.Count +
                    " native=" + _last.Native.Item1 + "/" + _last.Native.Item2 +
                    " result=" + result.Item1 + "/" + result.Item2 + " feet=" + player.Position.ToString("F3") +
                    " matched=" + (match.Entry != null) + " coverage=" + F(match.Coverage) +
                    " minimum=" + F(MinimumCoverage) + " red=" + F(match.Red) + " uv=" + match.UV.ToString("F4") +
                    " planeDistance=" + F(match.PlaneDistance) + " path='" +
                    (match.Entry != null ? Path(match.Entry.Renderer.transform) : "<none>") + "'");
            }
            catch (Exception error) { Plugin.Log.LogWarning(Tag + "probe failed: " + error); }
        }

        static string F(float value) => value.ToString("0.####", CultureInfo.InvariantCulture);
        static string Path(Transform value)
        {
            string path = value.name;
            while (value.parent != null) { value = value.parent; path = value.name + "/" + path; }
            return path;
        }
    }
}
