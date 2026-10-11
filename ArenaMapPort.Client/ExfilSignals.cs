using System;
using System.Threading.Tasks;
using BepInEx.Configuration;
using Comfort.Common;
using EFT;
using UnityEngine;

namespace ArenaMapPort.Client
{
    /// <summary>
    /// exfils-stage2 (docs/progress/exfils-stage2.md, "Smoke signals") — a smoke column, a coloured light and a
    /// faint looping sound on every runtime-authored exit, so players find the gates (user, 2026-09-20: the
    /// names showed in the list but the exits were nowhere to be seen — a bare trigger box has no visual).
    ///
    /// Vanilla exfils get their smoke from scene art parented under <c>ExfiltrationPoint._root</c>; there is no
    /// BSG component that draws one. The always-on smoke EFT does ship is the airdrop crate's signal
    /// (<c>AirdropSynchronizableObject.AirdropFlare</c>, toggled by <c>ClientAirDrop</c> with SetActive + Play).
    /// This takes the crate bundle's "Flare_Smoke" particle material and colour curve and its
    /// <c>FlareSound</c> clip (<c>EasyAssets.Retain</c> + <c>GetAsset</c>) and builds fresh Light / AudioSource /
    /// ParticleSystem components on a child of the exit object — the same approach as RaiRaiTheRaichu's
    /// HiddenCaches (independent re-implementation, see THIRD-PARTY-NOTICES.md). The child sits on the Default
    /// layer (the exit object itself is on Triggers, which cameras may cull) and uses
    /// <c>ParticleSystemCullingMode.AlwaysSimulate</c> so the column is there when the player looks over from
    /// 30 m. The bundle is retained lazily in a MonoBehaviour's Start (never inside a Harmony patch) and the
    /// harvested references are dropped at every raid start so an unloaded bundle is never reused.
    /// </summary>
    internal static class ExfilSignals
    {
        const string Tag = "[ArenaMapPort][Exfil]";
        internal const string CrateBundle = "assets/content/location_objects/lootable/prefab/scontainer_crate.bundle";
        internal const string SignalSuffix = " signal";

        static ConfigEntry<bool> _enabled;
        static ConfigEntry<bool> _sound;
        static ConfigEntry<Color> _color;

        internal static Material Material;
        internal static AudioClip Clip;
        internal static ParticleSystem Template;
        static Task _harvest;

        internal static bool Enabled => _enabled != null && _enabled.Value;
        internal static bool SoundEnabled => _sound != null && _sound.Value;
        internal static Color SignalColor => _color != null ? _color.Value : new Color(0.25f, 1f, 0.35f);

        internal static void BindConfig()
        {
            _enabled = Plugin.Instance.Settings.Bind("Exfils", "SmokeSignals", true,
                "Smoke column + light at every runtime-authored exit on ported maps (EFT's airdrop signal effect). Next raid.");
            _sound = Plugin.Instance.Settings.Bind("Exfils", "SmokeSignalSound", true,
                "Play the airdrop flare's hiss at the exits (3D, quiet, 30 m). Next raid.");
            _color = Plugin.Instance.Settings.Bind("Exfils", "SmokeSignalColor", new Color(0.25f, 1f, 0.35f),
                "Tint of the smoke and the light (green like a vanilla extraction flare). Next raid.");
        }

        /// <summary>Called at every InitAllExfiltrationPoints: forget last raid's bundle objects.</summary>
        internal static void ResetCache()
        {
            Material = null;
            Clip = null;
            Template = null;
            _harvest = null;
        }

        internal static void Attach(GameObject exitObject, string exitName, Vector3 floor)
        {
            if (!Enabled || exitObject == null)
            {
                return;
            }
            var child = new GameObject(exitName + SignalSuffix);
            child.layer = 0; // Default — not the exit's Triggers layer
            child.transform.SetParent(exitObject.transform, false);
            child.transform.position = floor + Vector3.up * 0.3f;
            child.AddComponent<ExfilSmokeSignal>();
        }

        internal static Task EnsureHarvested()
        {
            if (_harvest == null || _harvest.IsFaulted)
            {
                _harvest = Harvest();
            }
            return _harvest;
        }

        static async Task Harvest()
        {
            var factory = Singleton<ObjectsFactory>.Instance;
            if (factory == null)
            {
                throw new InvalidOperationException("ObjectsFactory singleton is not up yet");
            }
            var assets = factory.EasyAssets;
            await assets.Retain(CrateBundle, null, null).LoadingJob;
            var crate = assets.GetAsset<GameObject>(CrateBundle);
            if (crate == null)
            {
                throw new InvalidOperationException($"'{CrateBundle}' loaded but carries no GameObject asset");
            }
            foreach (var component in crate.GetComponentsInChildren<Component>(true))
            {
                if (component == null) continue;
                if (component.name == "Flare_Smoke")
                {
                    if (component is ParticleSystemRenderer renderer) Material = renderer.sharedMaterial;
                    else if (component is ParticleSystem system) Template = system;
                }
                else if (component.GetType().Name == "AirdropSynchronizableObject")
                {
                    // AirdropSynchronizableObject : SerializedMonoBehaviour (Sirenix) — read FlareSound.Clip by
                    // reflection so the plugin needs no Sirenix.Serialization reference.
                    var tagged = HarmonyLib.AccessTools.Field(component.GetType(), "FlareSound")?.GetValue(component);
                    Clip = tagged == null ? null : HarmonyLib.AccessTools.Field(tagged.GetType(), "Clip")?.GetValue(tagged) as AudioClip;
                }
            }
            Plugin.Log.LogDebug($"{Tag} signal assets from the airdrop crate: material={(Material != null ? Material.name : "MISSING")} " +
                               $"curve={(Template != null)} clip={(Clip != null ? Clip.name : "none")}");
            if (Material == null)
            {
                throw new InvalidOperationException("the crate bundle has no 'Flare_Smoke' ParticleSystemRenderer — EFT build changed?");
            }
        }
    }

    /// <summary>One exit's signal; builds itself once the crate bundle is in.</summary>
    internal sealed class ExfilSmokeSignal : MonoBehaviour
    {
        const string Tag = "[ArenaMapPort][Exfil]";

        async void Start()
        {
            try
            {
                await ExfilSignals.EnsureHarvested();
                if (this == null || gameObject == null) return; // the raid ended while the bundle loaded
                Build();
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"{Tag} no smoke signal on '{transform.parent?.name}': {e.GetType().Name}: {e.Message}");
            }
        }

        void Build()
        {
            var tint = ExfilSignals.SignalColor;

            var light = gameObject.AddComponent<Light>();
            light.type = LightType.Point;
            light.color = tint;
            light.range = 6f;
            light.intensity = 2f;
            light.shadows = LightShadows.None;

            if (ExfilSignals.SoundEnabled && ExfilSignals.Clip != null)
            {
                var audio = gameObject.AddComponent<AudioSource>();
                audio.clip = ExfilSignals.Clip;
                audio.loop = true;
                audio.spatialBlend = 1f;
                audio.rolloffMode = AudioRolloffMode.Linear;
                audio.minDistance = 2f;
                audio.maxDistance = 30f;
                audio.volume = 0.2f;
                audio.playOnAwake = false;
                audio.Play();
            }

            var ps = gameObject.AddComponent<ParticleSystem>();
            ps.Stop();
            var main = ps.main;
            main.loop = true;
            main.duration = 5f;
            main.startLifetime = 10f;
            main.startSpeed = 3f;
            main.startSize = 1f;
            main.gravityModifier = -0.01f;
            main.maxParticles = 150;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.scalingMode = ParticleSystemScalingMode.Shape;
            main.cullingMode = ParticleSystemCullingMode.AlwaysSimulate;
            var emission = ps.emission;
            emission.rateOverTime = 15f;
            var shape = ps.shape;
            shape.shapeType = ParticleSystemShapeType.Sphere;
            shape.radius = 0.05f;
            if (ExfilSignals.Template != null)
            {
                var col = ps.colorOverLifetime;
                var src = ExfilSignals.Template.colorOverLifetime;
                col.enabled = src.enabled;
                col.color = src.color;
                var sheet = ps.textureSheetAnimation;
                var srcSheet = ExfilSignals.Template.textureSheetAnimation;
                sheet.enabled = srcSheet.enabled;
                sheet.numTilesX = srcSheet.numTilesX;
                sheet.numTilesY = srcSheet.numTilesY;
            }
            var limit = ps.limitVelocityOverLifetime;
            limit.enabled = true;
            limit.dampen = 1f;
            limit.limitMultiplier = 0.4f;

            var renderer = GetComponent<ParticleSystemRenderer>();
            renderer.enableGPUInstancing = false;
            renderer.maxParticleSize = 20f;
            renderer.receiveShadows = true;
            renderer.material = ExfilSignals.Material;           // assigning + the instance getter below = per-signal copy
            renderer.material.SetColor("_LocalMinimalAmbientLight", Color.white);
            renderer.material.SetColor("_TintColor", tint);
            ps.Play();
            Plugin.Log.LogDebug($"{Tag} smoke signal lit at '{transform.parent?.name}' ({transform.position:F1})");
        }
    }
}
