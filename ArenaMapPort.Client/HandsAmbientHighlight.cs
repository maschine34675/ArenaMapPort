using System;
using System.Collections;
using System.Collections.Generic;
using ArenaMapPort.Shared;
using BepInEx.Configuration;
using Comfort.Common;
using EFT;
using EFT.CameraControl;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ArenaMapPort.Client
{
    /// <summary>
    /// The donor applies a separate ambient highlight to hands and held items. Arena's original
    /// range matches the donor, so this is optional tuning rather than missing-data replacement.
    /// Scale only the Hands stencil's captured range in an owned raid; restore it on teardown.
    /// Native Bay5 comparisons in bay5-shadows-menu.md show the effect on the held knife.
    /// </summary>
    internal static class HandsAmbientHighlight
    {
        sealed class Entry
        {
            internal AmbientHighlight.HighlightSettings Source;
            internal float OriginalMin, OriginalMax, LastMin, LastMax;
        }

        sealed class Raid
        {
            internal ArenaMapDescriptor Map;
            internal CameraManager CameraManager;
            internal Coroutine Startup;
            internal readonly Dictionary<AmbientHighlight.HighlightSettings, Entry> Highlights =
                new Dictionary<AmbientHighlight.HighlightSettings, Entry>();
        }

        static readonly Dictionary<string, ConfigEntry<float>> Configs = new Dictionary<string, ConfigEntry<float>>();
        static Raid _raid;
        static bool _installed;

        // Install after WeatherRig so its activation precedes our Scripts-scene callback.
        internal static void Install()
        {
            if (_installed) return;
            _installed = true;
            foreach (var map in ArenaMapRegistry.All)
            {
                var config = Plugin.Instance.Settings.Bind($"Lighting ({map.Key})", "HandsAmbientHighlightMultiplier", 1f,
                    new ConfigDescription("Live brightness scale of the donor ambient highlight for hands and held items. " +
                        "1 preserves the original. Does not scale world/character highlights or ambient light.",
                        new AcceptableValueRange<float>(0f, 1f)));
                Configs.Add(map.Key, config);
                EquatorProfiles.Subscribe(config, (_, __) =>
                {
                    if (_raid?.Map == map && IsCurrent(_raid)) ApplySafely(_raid, "setting changed");
                });
            }
            SceneManager.sceneLoaded += OnSceneLoaded;
            SceneManager.sceneUnloaded += OnSceneUnloaded;
        }

        static bool IsCurrent(Raid raid) => raid != null && _raid == raid && ArenaMapRaid.CurrentMap == raid.Map;

        static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            var map = ArenaMapRaid.MapForScriptsScene(scene.name);
            if (map == null) return;
            Release();
            var raid = _raid = new Raid { Map = map };
            ApplySafely(raid, "Scripts loaded");
            raid.Startup = Plugin.Instance.StartCoroutine(WaitForRaidCamera(raid));
        }

        static IEnumerator WaitForRaidCamera(Raid raid)
        {
            // CameraManager can be replaced after Scripts loads. Stop the bounded startup retry
            // once the real raid camera is ready; there is no ongoing per-frame polling.
            for (int attempt = 0; attempt < 240 && IsCurrent(raid); attempt++)
            {
                if (Singleton<AbstractGame>.Instantiated && Singleton<AbstractGame>.Instance.Status == GameStatus.Started
                    && CameraManager.Exist && CameraManager.Instance.Camera != null)
                {
                    ApplySafely(raid, "raid camera ready");
                    raid.Startup = null;
                    yield break;
                }
                yield return new WaitForSeconds(1f);
            }
            raid.Startup = null;
            if (IsCurrent(raid)) Plugin.Log.LogWarning($"[ArenaMapPort][HandsHighlight] map={raid.Map.Key} " +
                "startup camera wait expired; scene/config events remain available.");
        }

        static void OnCameraChanged()
        {
            if (IsCurrent(_raid)) ApplySafely(_raid, "camera changed");
        }

        static void ApplySafely(Raid raid, string reason)
        {
            if (!IsCurrent(raid)) return;
            try
            {
                var manager = CameraManager.Exist ? CameraManager.Instance : null;
                if (raid.CameraManager != manager)
                {
                    if (raid.CameraManager != null) raid.CameraManager.OnCameraChanged -= OnCameraChanged;
                    raid.CameraManager = manager;
                    if (manager != null) manager.OnCameraChanged += OnCameraChanged;
                }
                if (WeatherRig.IsActiveForCurrentMap())
                {
                    var highlight = WeatherRig.ActiveRig?.Controller?.TimeOfDayController?.AmbientHighlightScript;
                    if (highlight != null && highlight._highlightSettings != null)
                        foreach (var source in highlight._highlightSettings)
                        {
                            if (source == null || source.StencilTypeToUse != AmbientHighlight.StencilType.Hands
                                || raid.Highlights.ContainsKey(source)) continue;
                            raid.Highlights.Add(source, new Entry { Source = source,
                                OriginalMin = source.HighlightMinMultiplier, LastMin = source.HighlightMinMultiplier,
                                OriginalMax = source.HighlightMaxMultiplier, LastMax = source.HighlightMaxMultiplier });
                        }
                }
                float multiplier = EquatorProfiles.Value(Configs[raid.Map.Key]);
                int changed = Apply(raid, multiplier);
                Plugin.Log.LogDebug($"[ArenaMapPort][HandsHighlight] map={raid.Map.Key} {reason}: " +
                    $"captured={raid.Highlights.Count}, scale={multiplier:0.00}, fieldsChanged={changed}");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[ArenaMapPort][HandsHighlight] map={raid.Map.Key} {reason}: " +
                    $"{e.GetType().Name}: {e.Message}");
            }
        }

        static int Apply(Raid raid, float multiplier)
        {
            int changed = 0;
            foreach (var entry in raid.Highlights.Values)
            {
                // Preserve independently changed fields. Always scale the captured source,
                // never the already-scaled result. At default 1 neither field is written.
                if (entry.Source.StencilTypeToUse != AmbientHighlight.StencilType.Hands) continue;
                float min = entry.OriginalMin * multiplier, max = entry.OriginalMax * multiplier;
                if (entry.Source.HighlightMinMultiplier == entry.LastMin && min != entry.LastMin)
                {
                    entry.Source.HighlightMinMultiplier = entry.LastMin = min;
                    changed++;
                }
                if (entry.Source.HighlightMaxMultiplier == entry.LastMax && max != entry.LastMax)
                {
                    entry.Source.HighlightMaxMultiplier = entry.LastMax = max;
                    changed++;
                }
            }
            return changed;
        }

        static void OnSceneUnloaded(Scene scene)
        {
            if (_raid != null && ArenaMapRaid.MapForScriptsScene(scene.name) == _raid.Map) Release();
        }

        static void Release()
        {
            var raid = _raid;
            _raid = null; // Use captured references, never CurrentMap or ActiveRig during teardown.
            if (raid == null) return;
            if (raid.Startup != null) Plugin.Instance.StopCoroutine(raid.Startup);
            if (raid.CameraManager != null) raid.CameraManager.OnCameraChanged -= OnCameraChanged;
            int changed = Apply(raid, 1f);
            Plugin.Log.LogDebug($"[ArenaMapPort][HandsHighlight] map={raid.Map.Key} released: " +
                $"fieldsRestored={changed}, captured={raid.Highlights.Count}");
        }
    }
}
