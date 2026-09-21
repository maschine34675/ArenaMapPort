#nullable disable
using System;
using System.Collections;
using System.Collections.Generic;
using ArenaMapPort.Shared;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace ArenaMapPort.Client
{
    /// <summary>
    /// Arena's p0/Reflective materials assume dim local cubemaps; EFT's deferred pass instead reflects
    /// the global sky from _SpecColor * _Specularness (host evidence: donor-settings-camera-weather §20).
    /// Cap that product on owned scene meshes, including props and mesh decals outside the indoor
    /// classifier. Renderer-local clones protect shared assets and vanilla users of those assets.
    /// The clones sit above existing indoor repairs, are removed before those repairs change anything,
    /// and are rebuilt from their uncapped inputs. Cap 1 and raid teardown restore those exact inputs.
    /// Native acceptance for this addition is recorded in equator-specular-cap.md, separately from tests.
    /// </summary>
    internal static class ReflectiveSpecularRepairs
    {
        static readonly int ColorId = Shader.PropertyToID(SpecularCapPolicy.ColorProperty);
        static readonly int StrengthId = Shader.PropertyToID(SpecularCapPolicy.StrengthProperty);

        sealed class Entry
        {
            internal MeshRenderer Renderer;
            internal Material[] Inputs;
            internal Material[] Outputs;
            internal readonly List<Material> Owned = new List<Material>();
        }

        sealed class State
        {
            internal ArenaMapDescriptor Map;
            internal bool Ready;
            internal int Suspensions;
            internal readonly List<Entry> Entries = new List<Entry>();
            internal int Candidates, Changed, Decals;
        }

        static State _state;
        static GameObject _host;
        static bool _installed;

        internal static void Install()
        {
            if (_installed) return;
            _installed = true;
            SceneManager.sceneLoaded += OnSceneLoaded;
            SceneManager.sceneUnloaded += OnSceneUnloaded;
            foreach (var map in ArenaMapRegistry.All)
            {
                var config = WeatherRig.ReflectiveSpecularCapEntry(map);
                if (config != null) config.SettingChanged += (_, __) =>
                {
                    if (_state?.Map == map) Refresh(_state);
                };
            }
        }

        static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            var map = ArenaMapRaid.MapForScriptsScene(scene.name);
            if (map == null) return;
            Clear();
            var state = _state = new State { Map = map };
            _host = new GameObject("ArenaMapPort_ReflectiveSpecularScan") { hideFlags = HideFlags.HideAndDontSave };
            UnityEngine.Object.DontDestroyOnLoad(_host);
            _host.AddComponent<Runner>().StartCoroutine(RunAfterStart(state));
        }

        sealed class Runner : MonoBehaviour { }

        static IEnumerator RunAfterStart(State state)
        {
            // All additive map scenes must be loaded and ShaderRebind's delayed pass completed.
            bool started = false;
            for (int seconds = 0; seconds < 240 && _state == state; seconds++)
            {
                var game = Comfort.Common.Singleton<EFT.AbstractGame>.Instantiated
                    ? Comfort.Common.Singleton<EFT.AbstractGame>.Instance : null;
                if (game != null && game.Status == EFT.GameStatus.Started) { started = true; break; }
                yield return new WaitForSeconds(1f);
            }
            if (!started || _state != state) yield break;
            yield return new WaitForSeconds(3.5f);
            if (_state != state || ArenaMapRaid.CurrentMap != state.Map) yield break;
            state.Ready = true;
            Refresh(state);
        }

        static void OnSceneUnloaded(Scene scene)
        {
            if (_state != null && ArenaMapRaid.MapForScriptsScene(scene.name) == _state.Map) Clear();
        }

        // Called before the indoor layer destroys its own clones, independently of unload subscriber order.
        internal static void Clear()
        {
            if (_state != null) Restore(_state);
            _state = null;
            if (_host != null) UnityEngine.Object.Destroy(_host);
            _host = null;
        }

        /// <summary>Temporarily remove this outer layer while another material repair reads/writes its inputs.</summary>
        internal static IDisposable Suspend(ArenaMapDescriptor map)
        {
            var state = _state;
            if (state == null || state.Map != map) return new Scope(null);
            if (state.Suspensions++ == 0) Restore(state);
            return new Scope(state);
        }

        sealed class Scope : IDisposable
        {
            State _captured;
            internal Scope(State state) { _captured = state; }
            public void Dispose()
            {
                var state = _captured;
                _captured = null;
                if (state != null && --state.Suspensions == 0 && _state == state) Refresh(state);
            }
        }

        static void Restore(State state)
        {
            foreach (var entry in state.Entries)
            {
                if (entry.Renderer != null)
                {
                    var current = entry.Renderer.sharedMaterials;
                    bool changed = false;
                    for (int i = 0; i < current.Length && i < entry.Outputs.Length; i++)
                    {
                        // Do not overwrite a slot subsequently replaced by another component.
                        if (entry.Outputs[i] != entry.Inputs[i] && current[i] == entry.Outputs[i])
                        {
                            current[i] = entry.Inputs[i];
                            changed = true;
                        }
                    }
                    if (changed) entry.Renderer.sharedMaterials = current;
                }
                foreach (var clone in entry.Owned) if (clone != null) UnityEngine.Object.Destroy(clone);
            }
            state.Entries.Clear();
            state.Candidates = state.Changed = state.Decals = 0;
        }

        static void Refresh(State state)
        {
            if (_state != state || !state.Ready || state.Suspensions != 0 || !ArenaMapRaid.IsLoaded()
                || ArenaMapRaid.CurrentMap != state.Map) return;
            Restore(state);
            float cap = WeatherRig.ReflectiveSpecularCapEntry(state.Map)?.Value ?? 1f;
            if (cap < 1f)
            {
                try
                {
                    for (int i = 0; i < SceneManager.sceneCount; i++)
                    {
                        var scene = SceneManager.GetSceneAt(i);
                        if (!scene.isLoaded || Array.IndexOf(state.Map.AllSceneNames, scene.name) < 0) continue;
                        foreach (var root in scene.GetRootGameObjects())
                        foreach (var renderer in root.GetComponentsInChildren<MeshRenderer>(true))
                        {
                            // Include inactive LODs so changing view does not resurrect an uncapped material.
                            if (renderer == null || renderer.GetComponentInParent<EFT.Player>() != null) continue;
                            ApplyRenderer(state, renderer, cap);
                        }
                    }
                }
                catch (Exception error)
                {
                    Restore(state);
                    Plugin.Log.LogError($"[ArenaMapPort][SpecularCap] map={state.Map.Key} reverted after scan failure: {error}");
                    return;
                }
            }
            Plugin.Log.LogInfo($"[ArenaMapPort][SpecularCap] map={state.Map.Key} cap={cap:0.00} " +
                $"candidates={state.Candidates} capped={state.Changed} meshDecals={state.Decals} " +
                $"renderers={state.Entries.Count}; RGB-only ceiling on _SpecColor * _Specularness; cap=1 restores inputs.");
        }

        static void ApplyRenderer(State state, MeshRenderer renderer, float cap)
        {
            var inputs = renderer.sharedMaterials;
            Entry entry = null;
            try
            {
                for (int i = 0; i < inputs.Length; i++)
                {
                    var input = inputs[i];
                    if (input == null || !SpecularCapPolicy.MatchesShader(input.shader?.name)
                        || !HasProperty(input.shader, SpecularCapPolicy.ColorProperty, false)) continue;
                    int scalarIndex = input.shader.FindPropertyIndex(SpecularCapPolicy.StrengthProperty);
                    if (scalarIndex >= 0 && !HasProperty(input.shader, SpecularCapPolicy.StrengthProperty, true)) continue;
                    state.Candidates++;
                    var color = input.GetColor(ColorId);
                    float strength = scalarIndex < 0 ? 1f : input.GetFloat(StrengthId);
                    float scale = SpecularCapPolicy.ColorScale(color.r, color.g, color.b, strength, cap);
                    if (scale >= 1f) continue;
                    if (entry == null) entry = new Entry
                    {
                        Renderer = renderer, Inputs = inputs, Outputs = (Material[])inputs.Clone()
                    };
                    var clone = new Material(input) { name = input.name + " (ArenaMapPort SpecularCap clone)" };
                    entry.Owned.Add(clone);
                    clone.SetColor(ColorId, new Color(color.r * scale, color.g * scale, color.b * scale, color.a));
                    entry.Outputs[i] = clone;
                    state.Changed++;
                    if (input.shader.name.IndexOf("Decal", StringComparison.OrdinalIgnoreCase) >= 0) state.Decals++;
                }
                if (entry != null)
                {
                    state.Entries.Add(entry); // tracked before assignment, so outer failure cleanup restores safely
                    renderer.sharedMaterials = entry.Outputs;
                }
            }
            catch
            {
                if (entry != null && !state.Entries.Contains(entry))
                    foreach (var clone in entry.Owned) if (clone != null) UnityEngine.Object.Destroy(clone);
                throw;
            }
        }

        static bool HasProperty(Shader shader, string property, bool scalar)
        {
            int index = shader.FindPropertyIndex(property);
            if (index < 0) return false;
            var type = shader.GetPropertyType(index);
            return scalar ? type == ShaderPropertyType.Float || type == ShaderPropertyType.Range
                : type == ShaderPropertyType.Color;
        }

        internal static string SnapshotLine() => _state == null ? "SpecularCap: no owned raid state"
            : $"SpecularCap: map={_state.Map.Key} cap={WeatherRig.ReflectiveSpecularCapEntry(_state.Map)?.Value:0.00} " +
              $"ready={_state.Ready} candidates={_state.Candidates} capped={_state.Changed} meshDecals={_state.Decals} " +
              $"renderers={_state.Entries.Count}; effective max RGB, indoor result retained when already below cap";
    }
}
