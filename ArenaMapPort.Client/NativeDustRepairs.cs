using System;
using System.Collections;
using System.Collections.Generic;
using ArenaMapPort.Shared;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ArenaMapPort.Client
{
    /// <summary>
    /// Arena's dust meshes contain original particle centers, phases, colors and billboard offsets,
    /// but their animation component is not imported. Reuse SPT's native parent with empty generator
    /// children: Awake binds its private material, then returns without regenerating the mesh.
    /// Native Update supplies the original motion. Only exact source owners with real volume textures
    /// qualify. Source audit and native acceptance are recorded in docs/progress/native-dust.md.
    /// </summary>
    internal static class NativeDustRepairs
    {
        const string ShaderName = "Custom/LocalDustParticlesLighted";
        static readonly int TimeId = Shader.PropertyToID("_SelfTime");
        static readonly Dictionary<int, Entry> Entries = new Dictionary<int, Entry>();
        static bool _installed;

        sealed class Entry
        {
            internal int SceneHandle;
            internal ArenaMapDescriptor Map;
            internal GameObject Owner;
            internal MeshRenderer Renderer;
            internal MeshFilter Filter;
            internal Mesh Mesh;
            internal Material Original, Owned;
            internal Bounds OriginalBounds;
            internal LocalDustParticlesParent Animator;
            internal Lifetime Driver;
            internal bool Disposed;
        }

        internal static void Install()
        {
            if (_installed) return;
            _installed = true;
            SceneManager.sceneLoaded += OnSceneLoaded;
            SceneManager.sceneUnloaded += OnSceneUnloaded;
        }

        static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            var map = ArenaMapRaid.CurrentMap;
            var dust = map?.Dust;
            if (dust == null || scene.name != dust.Scene || Entries.ContainsKey(scene.handle)) return;
            var owner = Find(scene, dust.Path);
            var renderer = owner == null ? null : owner.GetComponent<MeshRenderer>();
            var filter = owner == null ? null : owner.GetComponent<MeshFilter>();
            var original = renderer == null ? null : renderer.sharedMaterial;
            var volume = original != null && original.HasProperty("_ParticleTex")
                ? original.GetTexture("_ParticleTex") as Texture3D : null;
            if (owner == null || !owner.activeInHierarchy || renderer == null || !renderer.enabled
                || filter == null || filter.sharedMesh == null || renderer.sharedMaterials.Length != 1
                || original == null || original.shader == null || original.shader.name != ShaderName
                || volume == null || volume.width <= 0 || volume.height <= 0 || volume.depth <= 0
                || owner.GetComponent<LocalDustParticlesParent>() != null)
            {
                Plugin.Log.LogWarning($"[ArenaMapPort][Dust] map={map.Key} '{dust.Scene}/{dust.Path}' skipped: " +
                    "requires active source renderer, generated mesh, native shader and Texture3D; old bundles remain unchanged");
                return;
            }

            var entry = new Entry { SceneHandle = scene.handle, Map = map, Owner = owner, Renderer = renderer,
                Filter = filter, Mesh = filter.sharedMesh, Original = original, OriginalBounds = renderer.localBounds };
            bool active = owner.activeSelf;
            try
            {
                // AddComponent must not invoke Awake with null Childs. No source child components are added.
                owner.SetActive(false);
                // The import stand-in records its opaque default queue (2000) in the sidecar.
                // Original dust uses its real shader's Transparent default (3000), with ZWrite off.
                // Reset only our copy so opaque geometry cannot paint over already-drawn particles.
                entry.Owned = new Material(original) { name = original.name + " (ArenaMapPort dust)", renderQueue = -1 };
                renderer.sharedMaterial = entry.Owned;
                renderer.localBounds = new Bounds(new Vector3(dust.CenterX, dust.CenterY, dust.CenterZ),
                    new Vector3(dust.ExtentX, dust.ExtentY, dust.ExtentZ) * 2f);
                entry.Animator = owner.AddComponent<LocalDustParticlesParent>();
                entry.Animator.Childs = Array.Empty<LocalDustParticles>();
                entry.Animator.TimeSpeed = dust.TimeSpeed;
                entry.Animator.Size = dust.Size;
                entry.Animator.QuadTileSetSide = dust.QuadTileSetSide;
                entry.Animator.UvTilePadding = dust.UvTilePadding;
                entry.Animator.Regenerate = false;
                entry.Driver = owner.AddComponent<Lifetime>();
                entry.Driver.State = entry;
                Entries.Add(scene.handle, entry);
                owner.SetActive(active); // native Awake binds the owned material and keeps the imported mesh
                if (filter.sharedMesh != entry.Mesh)
                    throw new InvalidOperationException("native initialization replaced the source mesh");
                Plugin.Log.LogDebug($"[ArenaMapPort][Dust] map={map.Key} initialized '{dust.Scene}/{dust.Path}' " +
                    $"native={entry.Animator.GetType().FullName} speed={dust.TimeSpeed:F3} mesh={entry.Mesh.GetInstanceID()} " +
                    $"vertices={entry.Mesh.vertexCount} volume={volume.width}x{volume.height}x{volume.depth} " +
                    $"format={volume.format} queue={entry.Owned.renderQueue} bounds={renderer.localBounds}");
                if (DeveloperOptions.Enabled) entry.Driver.StartCoroutine(ObserveAnimation(entry));
            }
            catch (Exception ex)
            {
                if (filter != null && filter.sharedMesh != entry.Mesh) filter.sharedMesh = entry.Mesh;
                Clear(entry, "initialization rollback");
                Plugin.Log.LogWarning($"[ArenaMapPort][Dust] map={map.Key} initialization failed: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                if (owner != null && owner.activeSelf != active) owner.SetActive(active);
            }
        }

        static IEnumerator ObserveAnimation(Entry entry)
        {
            // Two frames let native Update initialize _SelfTime before taking the baseline.
            yield return null;
            yield return null;
            if (entry.Disposed || entry.Owned == null) yield break;
            Vector4 before = entry.Owned.GetVector(TimeId);
            int frame = Time.frameCount;
            yield return new WaitForSeconds(2f);
            if (entry.Disposed || entry.Owned == null) yield break;
            Vector4 after = entry.Owned.GetVector(TimeId);
            bool sameMesh = entry.Filter != null && entry.Filter.sharedMesh == entry.Mesh;
            string evidence = $"[ArenaMapPort][Dust] map={entry.Map.Key} animation frames={Time.frameCount - frame} " +
                $"before={before:F5} after={after:F5} advancing={(after - before).sqrMagnitude > 0f} " +
                $"meshPreserved={sameMesh} visible={entry.Renderer != null && entry.Renderer.isVisible}";
            // Developer-only animation evidence; initialization failures are reported separately.
            Plugin.Log.LogDebug(evidence);
        }

        static void OnSceneUnloaded(Scene scene)
        {
            // Snapshot permits Clear/Lifetime.OnDestroy to remove their own entries safely.
            foreach (var entry in new List<Entry>(Entries.Values))
                if (entry.SceneHandle == scene.handle || entry.Map.ScriptsSceneName == scene.name)
                    Clear(entry, "scene unload");
        }

        static void Clear(Entry entry, string reason)
        {
            if (entry == null || entry.Disposed) return;
            entry.Disposed = true;
            Entries.Remove(entry.SceneHandle);
            int materialsReleased = entry.Owned != null ? 1 : 0;
            if (entry.Animator != null)
            {
                entry.Animator.enabled = false; // no native Update after its material is released
                UnityEngine.Object.Destroy(entry.Animator);
            }
            if (entry.Renderer != null)
            {
                if (entry.Renderer.sharedMaterial == entry.Owned) entry.Renderer.sharedMaterial = entry.Original;
                entry.Renderer.localBounds = entry.OriginalBounds;
            }
            if (entry.Owned != null) UnityEngine.Object.Destroy(entry.Owned);
            if (entry.Driver != null)
            {
                entry.Driver.StopAllCoroutines();
                entry.Driver.State = null;
                UnityEngine.Object.Destroy(entry.Driver);
            }
            Plugin.Log.LogDebug($"[ArenaMapPort][Dust] map={entry.Map.Key} cleanup reason={reason} ownedMaterials={materialsReleased} remaining={Entries.Count}");
        }

        static GameObject Find(Scene scene, string path)
        {
            int slash = path.IndexOf('/');
            string rootName = slash < 0 ? path : path.Substring(0, slash);
            foreach (var root in scene.GetRootGameObjects())
                if (root.name == rootName)
                    return slash < 0 ? root : root.transform.Find(path.Substring(slash + 1))?.gameObject;
            return null;
        }

        sealed class Lifetime : MonoBehaviour
        {
            internal Entry State;
            void OnDestroy() => Clear(State, "owner destroyed");
        }
    }
}
