using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using ArenaMapPort.Shared;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ArenaMapPort.Client
{
    /// <summary>
    /// Per-map ceiling on the emission of the map's own emissive materials, so a few over-bright lamps no longer blow out under the normal bloom.
    ///
    /// What vanilla does: nothing. EFT's maps keep their emissive materials near 1 and tune the camera's bloom (UltimateBloom 2.10/0.35) for that.
    ///
    /// What the ported scene lacks: Arena's Iceberg authors emission far above anything the released maps contain (the strongest of the four released
    /// maps is 4.13). The two lobby crystal lamps (material Emission_Yellow, Standard shader, _EmissionColor 67.8) stay fully over-exposed unless
    /// BloomIntensity is about 0.001, and the respawn-hall candle chandeliers (Sanatorium_Emissive_Orange, _EmissionPower 10) bloom into discs
    /// (docs/progress/iceberg-release.md, 2026-10-30 evening and "Per-map emission cap"). Lowering the bloom for the whole map (the first answer,
    /// 4.0/0.2) dulls every light; the cause is the two or three materials.
    ///
    /// What this does: once per raid, a fixed delay after the last owned scene loaded (so ShaderRebind has bound the real shaders; the pass runs
    /// behind the loading screen, never in Update), it collects the materials of every renderer of the map's scenes into
    /// <see cref="EmissionLedger{TMaterial}"/> and scales those whose effective emission level exceeds the map's cap down to the cap (the rules and
    /// the shader facts are <see cref="EmissionCapModel"/>'s). The cap is the descriptor's <see cref="ArenaMapDescriptor.EmissionCap"/>, overridable
    /// per map with the developer key `Lighting (&lt;key&gt;)/EmissionCap` (0 = off, live). Materials are changed in place, not cloned: the ledger
    /// keeps every original per material instance, a ceiling is idempotent by nature, a live change re-targets from the originals, and the scene
    /// unload writes the originals back so a second raid with the bundle still cached starts clean. A property that another controller moved after
    /// the cap wrote it is left alone. The renderer-local clones of ReflectiveSpecularRepairs are suspended around the pass, so they are rebuilt
    /// from the capped inputs. A map whose cap and floor are 0 schedules nothing and scans nothing.
    ///
    /// The floor (2026-11-01, user feedback "every light source seems to need its own slider"): the same pass also lifts a lamp below the map's
    /// <see cref="ArenaMapDescriptor.EmissionFloor"/> onto it (developer key `Lighting (&lt;key&gt;)/EmissionFloor`, 0 = off, live), so the map's lamps
    /// sit in one band and react alike to the threshold bloom; which materials it may lift is <see cref="EmissionCapModel"/>'s rule.
    ///
    /// Host evidence: docs/progress/iceberg-release.md "Per-map emission cap" (GPU-2 2026-10-31: 6 materials capped in 20 ms) and "Emission floor".
    /// </summary>
    internal static class EmissionCapRepairs
    {
        const string Tag = "[EmissionCap]";
        // ShaderRebind's sweep fires 3 s after the first owned scene loaded and DecalRepairs waits 4 s; every owned scene load restarts this timer,
        // so the pass runs 4 s after the LAST one, well before the raid starts (scenes load behind the loading screen).
        const float PassDelaySeconds = 4f;

        static readonly int EmissionColorId = Shader.PropertyToID(EmissionCapModel.EmissionColorProperty);
        static readonly int EmissionPowerId = Shader.PropertyToID(EmissionCapModel.EmissionPowerProperty);
        static readonly int EmissionVisibilityId = Shader.PropertyToID(EmissionCapModel.EmissionVisibilityProperty);
        static readonly int EmissiveColorId = Shader.PropertyToID(EmissionCapModel.EmissiveColorProperty);

        sealed class Runner : MonoBehaviour { }

        sealed class State
        {
            internal ArenaMapDescriptor Map;
            internal readonly EmissionLedger<Material> Ledger = new EmissionLedger<Material>();
            internal int Generation;
            internal bool Scanned;
            internal GameObject Host;
            internal Runner Runner;
        }

        static readonly Dictionary<string, ConfigEntry<float>> Caps = new Dictionary<string, ConfigEntry<float>>();
        static readonly Dictionary<string, ConfigEntry<float>> Floors = new Dictionary<string, ConfigEntry<float>>();
        static readonly MaterialSurface Surface = new MaterialSurface();
        static State _state;
        static bool _installed;

        internal static void Install()
        {
            if (_installed)
            {
                return;
            }
            _installed = true;

            foreach (var map in ArenaMapRegistry.All)
            {
                var captured = map;
                var entry = Plugin.Instance.Settings.Bind($"Lighting ({map.Key})", "EmissionCap", map.EmissionCap,
                    new ConfigDescription(
                        "Ceiling on the emission of this map's own emissive materials; 0 = off. Standard-family materials: the largest " +
                        "channel of _EmissionColor. p0 emissive shaders: _EmissionPower x _EmissionVisibility x the largest channel of " +
                        "_EmissiveColor, scaled through _EmissionPower. A material at or below the cap is never touched, a brighter one is " +
                        "scaled onto the cap (hue kept); the originals are kept and written back when this is 0 and at the end of the raid. " +
                        "Default: the map's own value (Iceberg 4.8; Equator, Bay5, AirPit and Bowl 0 = off). Live.",
                        new AcceptableValueRange<float>(0f, EmissionCapModel.MaxCap)));
                Caps[map.Key] = entry;
                entry.SettingChanged += (_, __) => OnCapChanged(captured);
                var floor = Plugin.Instance.Settings.Bind($"Lighting ({map.Key})", "EmissionFloor", map.EmissionFloor,
                    new ConfigDescription(
                        "Floor under the emission of this map's lamps; 0 = off. A lamp whose level (as for EmissionCap) lies between 0.25 and " +
                        "the floor is scaled up onto it (hue kept), so the map's lamps sit between this floor and EmissionCap and the bloom " +
                        "treats them alike. Levels below 0.25 (glass tints, dark light bars) and animated emissive shaders (screens) are " +
                        "never lifted; a floor above the cap counts as the cap. Default: the map's own value (Iceberg 1.9; Equator, Bay5, " +
                        "AirPit and Bowl 0 = off). Live.",
                        new AcceptableValueRange<float>(0f, EmissionCapModel.MaxCap)));
                Floors[map.Key] = floor;
                floor.SettingChanged += (_, __) => OnCapChanged(captured);
            }

            SceneManager.sceneLoaded += OnSceneLoaded;
            SceneManager.sceneUnloaded += OnSceneUnloaded;
        }

        static float CapFor(ArenaMapDescriptor map) => Caps.TryGetValue(map.Key, out var entry) ? entry.Value : map.EmissionCap;

        static float FloorFor(ArenaMapDescriptor map) => Floors.TryGetValue(map.Key, out var entry) ? entry.Value : map.EmissionFloor;

        static bool Active(ArenaMapDescriptor map) => CapFor(map) > 0f || FloorFor(map) > 0f;

        static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (!ArenaMapRaid.IsOwnedScene(scene.name))
            {
                return;
            }
            var map = ArenaMapRaid.CurrentMap;
            if (map == null || !Active(map))
            {
                return; // a map without a cap or a floor costs nothing
            }
            try
            {
                var state = StateFor(map);
                if (state.Host == null)
                {
                    state.Host = new GameObject("ArenaMapPort_EmissionCap") { hideFlags = HideFlags.HideAndDontSave };
                    UnityEngine.Object.DontDestroyOnLoad(state.Host);
                    state.Runner = state.Host.AddComponent<Runner>();
                }
                state.Runner.StartCoroutine(RunAfter(state, ++state.Generation));
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"{Tag} map={map.Key} could not be scheduled: {e.GetType().Name}: {e.Message}");
            }
        }

        static State StateFor(ArenaMapDescriptor map)
        {
            if (_state != null && _state.Map != map)
            {
                Release("another map's raid begins");
            }
            return _state ?? (_state = new State { Map = map });
        }

        static IEnumerator RunAfter(State state, int generation)
        {
            yield return new WaitForSecondsRealtime(PassDelaySeconds);
            if (_state != state || state.Generation != generation)
            {
                yield break; // a later scene load restarted the timer, or the raid is over
            }
            Pass(state, "owned scenes loaded", rescan: true);
        }

        static void OnCapChanged(ArenaMapDescriptor map)
        {
            try
            {
                if (!ArenaMapRaid.IsLoaded() || ArenaMapRaid.CurrentMap != map)
                {
                    return;
                }
                var state = _state;
                if (state == null && !Active(map))
                {
                    return;
                }
                state = StateFor(map);
                Pass(state, "setting changed", rescan: !state.Scanned);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"{Tag} map={map.Key} live change failed: {e.GetType().Name}: {e.Message}");
            }
        }

        static void OnSceneUnloaded(Scene scene)
        {
            // Stateless resolution: see ArenaMapRaid's doc comment for why an unload handler never reads the "current map".
            if (ArenaMapRaid.MapForScriptsScene(scene.name) == null)
            {
                return;
            }
            Release("raid ended");
        }

        static void Pass(State state, string reason, bool rescan)
        {
            if (_state != state || ArenaMapRaid.CurrentMap != state.Map)
            {
                return;
            }
            float cap = CapFor(state.Map);
            float floor = FloorFor(state.Map);
            bool on = cap > 0f || floor > 0f;
            if (!on && state.Ledger.Count == 0)
            {
                return;
            }
            var watch = Stopwatch.StartNew();
            int renderers = 0;
            EmissionPassResult result = default;
            bool failed = false;
            try
            {
                // The renderer-local clones of the specular cap are built from these very materials: take them off for the read and the write, and
                // let Dispose rebuild them from the capped (or restored) inputs. A failed pass restores the originals INSIDE the scope, so the
                // clones the scope's Dispose rebuilds never carry a half-applied cap.
                using (ReflectiveSpecularRepairs.Suspend(state.Map))
                {
                    try
                    {
                        if (on && rescan)
                        {
                            renderers = Scan(state);
                        }
                        result = on ? state.Ledger.Apply(Surface, floor, cap) : state.Ledger.Restore(Surface);
                    }
                    catch (Exception e)
                    {
                        failed = true;
                        Plugin.Log.LogError($"{Tag} map={state.Map.Key} pass failed ({reason}), originals restored: {e.GetType().Name}: {e.Message}");
                        try { state.Ledger.Restore(Surface); } catch (Exception) { /* the next raid captures again */ }
                    }
                }
            }
            catch (Exception e)
            {
                // Suspend or the scope's rebuild itself failed; the ledger's originals were restored above when the pass failed.
                Plugin.Log.LogError($"{Tag} map={state.Map.Key} specular suspend/rebuild failed ({reason}): {e.GetType().Name}: {e.Message}");
                return;
            }
            if (failed)
            {
                return;
            }
            watch.Stop();

            if (on)
            {
                Plugin.Log.LogDebug($"{Tag} map={state.Map.Key} cap={F(cap)} floor={F(floor)} ({reason}): {result.Materials} emissive material(s), {result.Capped} capped" +
                    (result.Capped > 0 ? $" (largest original {F(result.LargestOriginal)} '{result.LargestName}' -> {F(cap)})" : "") +
                    $", {result.Lifted} lifted" +
                    (result.Lifted > 0 ? $" (smallest original {F(result.SmallestLifted)} '{result.SmallestLiftedName}')" : "") +
                    $", {result.Written} written, {result.Foreign} left alone (changed elsewhere), {result.Gone} gone" +
                    (renderers > 0 ? $"; {renderers} renderer(s) scanned" : "") + $" in {watch.ElapsedMilliseconds} ms");
                if (DeveloperOptions.Enabled)
                {
                    foreach (var pair in state.Ledger.Entries)
                    {
                        var entry = pair.Value;
                        if (entry.AppliedScale != 1f)
                        {
                            Plugin.Log.LogDebug($"{Tag}   '{entry.Name}' ({entry.Family}) level {F(entry.Level)} -> {F(entry.Level * entry.AppliedScale)} (x{entry.AppliedScale.ToString("0.000", CultureInfo.InvariantCulture)})");
                        }
                    }
                }
            }
            else
            {
                Plugin.Log.LogDebug($"{Tag} map={state.Map.Key} cap and floor off ({reason}): {result.Written} material(s) restored to their originals");
            }
        }

        // Every Renderer of the map's loaded scenes, inactive ones included (a LOD or a lamp that switches on later must not come up uncapped).
        // One pass per scene load; the ledger refuses a material it already holds, so a repeated scan never captures a capped value as an original.
        static int Scan(State state)
        {
            int renderers = 0;
            var seen = new HashSet<Material>();
            var materials = new List<Material>();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded || Array.IndexOf(state.Map.AllSceneNames, scene.name) < 0)
                {
                    continue;
                }
                foreach (var root in scene.GetRootGameObjects())
                {
                    foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
                    {
                        if (renderer == null)
                        {
                            continue;
                        }
                        renderers++;
                        renderer.GetSharedMaterials(materials);
                        for (int m = 0; m < materials.Count; m++)
                        {
                            var material = materials[m];
                            if (material == null || !seen.Add(material) || state.Ledger.Contains(material))
                            {
                                continue;
                            }
                            var entry = Capture(material);
                            if (entry != null)
                            {
                                state.Ledger.TryAdd(material, entry);
                            }
                        }
                    }
                }
            }
            state.Scanned = true;
            return renderers;
        }

        static EmissionEntry Capture(Material material)
        {
            var shader = material.shader;
            if (shader == null)
            {
                return null;
            }
            var family = EmissionCapModel.Classify(shader.name, material.IsKeywordEnabled(EmissionCapModel.EmissionKeyword),
                material.HasProperty(EmissionColorId), material.HasProperty(EmissionPowerId));
            switch (family)
            {
                case EmissionFamily.Color:
                {
                    var color = material.GetColor(EmissionColorId);
                    return EmissionEntry.ForColor(material.name, color.r, color.g, color.b, color.a, EmissionCapModel.Liftable(shader.name));
                }
                case EmissionFamily.Power:
                {
                    float visibility = material.HasProperty(EmissionVisibilityId) ? material.GetFloat(EmissionVisibilityId) : 1f;
                    float peak = 1f;
                    if (material.HasProperty(EmissiveColorId))
                    {
                        var emissive = material.GetColor(EmissiveColorId);
                        peak = Math.Max(emissive.r, Math.Max(emissive.g, emissive.b));
                    }
                    return EmissionEntry.ForPower(material.name, material.GetFloat(EmissionPowerId), visibility, peak, EmissionCapModel.Liftable(shader.name));
                }
                default:
                    return null;
            }
        }

        // Raid end (or another map's raid): the originals go back into the materials, which outlive the scene in a cached bundle.
        static void Release(string reason)
        {
            var state = _state;
            _state = null;
            if (state == null)
            {
                return;
            }
            int restored = 0;
            int held = state.Ledger.Count;
            try
            {
                restored = state.Ledger.Restore(Surface).Written;
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"{Tag} map={state.Map.Key} restore failed ({reason}): {e.GetType().Name}: {e.Message}");
            }
            state.Ledger.Clear();
            if (state.Host != null)
            {
                UnityEngine.Object.Destroy(state.Host);
            }
            if (held > 0)
            {
                Plugin.Log.LogDebug($"{Tag} map={state.Map.Key} released {held} material(s) ({reason}), restored {restored} to their originals");
            }
        }

        static string F(float value) => value.ToString("0.00", CultureInfo.InvariantCulture);

        sealed class MaterialSurface : IEmissionSurface<Material>
        {
            public bool TryRead(Material material, EmissionFamily family, out EmissionValues current)
            {
                if (material == null)
                {
                    current = default(EmissionValues);
                    return false;
                }
                if (family == EmissionFamily.Color)
                {
                    var color = material.GetColor(EmissionColorId);
                    current = new EmissionValues(color.r, color.g, color.b, color.a, 0f);
                }
                else
                {
                    current = new EmissionValues(0f, 0f, 0f, 0f, material.GetFloat(EmissionPowerId));
                }
                return true;
            }

            public void Write(Material material, EmissionFamily family, EmissionValues values)
            {
                if (family == EmissionFamily.Color)
                {
                    material.SetColor(EmissionColorId, new Color(values.R, values.G, values.B, values.A));
                }
                else
                {
                    material.SetFloat(EmissionPowerId, values.Power);
                }
            }
        }
    }
}
