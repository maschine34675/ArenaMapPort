using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using Comfort.Common;
using EFT.BlitDebug;
using HarmonyLib;
using Systems.Effects;
using UnityEngine;
using UnityEngine.Rendering;
using UnityStandardAssets.ImageEffects;

namespace ArenaMapPort.Client
{
    /// <summary>
    /// The restored camera flare also sees muzzle emission. Remove only the projected muzzle
    /// regions from its quarter-resolution seed, before the horizontal stretch. The original
    /// camera image, lighting and broad bloom are separate buffers and remain untouched.
    /// A lamp directly behind a flash shares its masked pixels; other lamp streaks remain live.
    /// Native call-site and acceptance: docs/progress/muzzle-flare-exclusion.md.
    /// </summary>
    internal static class MuzzleFlareMask
    {
        sealed class Shot
        {
            internal MuzzleManager Manager;
            internal float Started, Until;
        }

        static readonly List<Shot> Shots = new List<Shot>();
        static readonly List<MuzzleParticleContainer> Particles = new List<MuzzleParticleContainer>();
        static AccessTools.FieldRef<MuzzleManager, MuzzleJet[]> Jets;
        static Material _mask;
        static bool _failed, _installed;
        // Read-only acceptance counters; no per-shot logging or GPU readbacks.
        internal static int MaskPasses, LastMaskedRegions;
        internal static float LargestRegionArea;

        internal static void Install(Harmony harmony, MethodInfo render)
        {
            var shot = AccessTools.Method(typeof(MuzzleManager), nameof(MuzzleManager.Shot),
                new[] { typeof(bool), typeof(float) });
            var particles = AccessTools.Method(typeof(MuzzleManager), "IMuzzleParticlePivot.Play",
                new[] { typeof(EMuzzleParticlePivot), typeof(Transform) });
            var jets = AccessTools.Field(typeof(MuzzleManager), "__muzzleJets");
            if (!HarmonyPatches.Verify(shot, typeof(MuzzleManager), "Shot") ||
                !HarmonyPatches.Verify(particles, typeof(MuzzleManager), "IMuzzleParticlePivot.Play") ||
                jets == null || jets.FieldType != typeof(MuzzleJet[]))
            {
                Plugin.Log.LogWarning("[Flare] muzzle bindings unavailable; exclusion disabled");
                return;
            }
            Jets = AccessTools.FieldRefAccess<MuzzleManager, MuzzleJet[]>(jets);
            harmony.Patch(render, transpiler: new HarmonyMethod(typeof(MuzzleFlareMask), nameof(Transpile)));
            if (!_installed) return;
            harmony.Patch(shot, postfix: new HarmonyMethod(typeof(MuzzleFlareMask), nameof(AfterShot)));
            harmony.Patch(particles, postfix: new HarmonyMethod(typeof(MuzzleFlareMask), nameof(AfterParticles)));
        }

        static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> input)
        {
            var code = new List<CodeInstruction>(input);
            var material = AccessTools.Field(typeof(BloomAndFlares), "hollywoodFlaresMaterial");
            var blit = AccessTools.Method(typeof(DebugGraphics), nameof(DebugGraphics.Blit),
                new[] { typeof(Texture), typeof(RenderTexture), typeof(Material), typeof(int) });
            int found = -1, count = 0;
            for (int i = 2; i < code.Count; i++)
                if (code[i].Calls(blit) && code[i - 1].opcode == OpCodes.Ldc_I4_2 &&
                    code[i - 2].LoadsField(material)) { found = i; count++; }
            if (count != 1)
            {
                Plugin.Log.LogWarning($"[Flare] expected one native flare seed, found {count}; muzzle exclusion disabled");
                return code;
            }
            // Preserve branch/exception labels on the inserted argument, not the call.
            var instance = new CodeInstruction(OpCodes.Ldarg_0);
            instance.labels.AddRange(code[found].labels);
            instance.blocks.AddRange(code[found].blocks);
            code[found].labels.Clear();
            code[found].blocks.Clear();
            code[found].operand = AccessTools.Method(typeof(MuzzleFlareMask), nameof(FilterSeed));
            code.Insert(found, instance);
            _installed = true;
            Plugin.Log.LogDebug("[Flare] muzzle mask bound to native flare seed before stretching");
            return code;
        }

        static bool Active => _installed && !_failed && AnamorphicFlareTuning.HasRestoredCamera;

        static float Duration(AnimationCurve curve)
            => curve != null && curve.length > 0 ? Mathf.Max(0f, curve[curve.length - 1].time) : 0f;

        static void AfterShot(MuzzleManager __instance)
        {
            if (!Active || __instance == null || __instance.JetMaterial == null) return;
            var jets = Jets(__instance);
            if (jets == null || jets.Length == 0) return;
            float now = Time.time;
            // Native ManualUpdate uses these same normalized curves and ShotLength.
            float duration = __instance.ShotLength * Mathf.Max(Duration(__instance.MoveCurve), Duration(__instance.JetLightCurve));
            if (float.IsNaN(duration) || float.IsInfinity(duration) || duration <= 0f) return;
            for (int i = Shots.Count - 1; i >= 0; i--)
            {
                if (Shots[i].Manager == __instance)
                {
                    Shots[i].Started = now; Shots[i].Until = now + duration;
                    return;
                }
                if (Shots[i].Manager == null || Shots[i].Until < now) Shots.RemoveAt(i);
            }
            Shots.Add(new Shot { Manager = __instance, Started = now, Until = now + duration });
        }

        static void AfterParticles(EMuzzleParticlePivot pivot)
        {
            if (!Active) return;
            var effects = Singleton<Effects>.Instance;
            var systems = effects != null ? effects.MuzzleEffect.CommonSystems : null;
            if (systems == null) return;
            foreach (var container in systems)
                if (container != null && container.Pivot == pivot && !Particles.Contains(container))
                    Particles.Add(container);
        }

        static void FilterSeed(Texture source, RenderTexture target, Material material, int pass, BloomAndFlares effect)
        {
            DebugGraphics.Blit(source, target, material, pass);
            if (!Active || !AnamorphicFlareTuning.IsRestored(effect) || (Shots.Count == 0 && Particles.Count == 0)) return;
            try { MaskSeed(effect, target); }
            catch (Exception e)
            {
                _failed = true;
                Plugin.Log.LogWarning($"[Flare] muzzle exclusion failed and is disabled for this raid: {e.Message}");
            }
        }

        static void MaskSeed(BloomAndFlares effect, RenderTexture target)
        {
            var camera = effect.GetComponent<Camera>();
            if (camera == null || target == null) return;
            if (_mask == null)
            {
                var shader = Shader.Find("Hidden/Internal-Colored");
                if (shader == null || !shader.isSupported) throw new InvalidOperationException("native mask shader unavailable");
                _mask = new Material(shader) { name = "ArenaMapPort muzzle flare mask", hideFlags = HideFlags.HideAndDontSave };
                _mask.SetInt("_SrcBlend", (int)BlendMode.One);
                _mask.SetInt("_DstBlend", (int)BlendMode.Zero);
                _mask.SetInt("_Cull", (int)CullMode.Off);
                _mask.SetInt("_ZWrite", 0);
                _mask.SetInt("_ZTest", (int)CompareFunction.Always);
            }
            var previous = RenderTexture.active;
            bool previousSrgb = GL.sRGBWrite;
            GL.PushMatrix();
            try
            {
                RenderTexture.active = target;
                GL.sRGBWrite = false;
                GL.LoadOrtho();
                if (!_mask.SetPass(0)) throw new InvalidOperationException("native mask pass unavailable");
                LastMaskedRegions = 0;
                float now = Time.time;
                // Include the native seed's preceding downsample/vertical blur footprint.
                var padding = new Vector2(4f / target.width, 4f / target.height + effect.sepBlurSpread / 256f);
                for (int i = Shots.Count - 1; i >= 0; i--)
                {
                    var shot = Shots[i];
                    if (shot.Manager == null || !shot.Manager.isActiveAndEnabled || now > shot.Until)
                    { Shots.RemoveAt(i); continue; }
                    var jets = Jets(shot.Manager);
                    if (jets == null) continue;
                    foreach (var jet in jets)
                    {
                        if (jet == null || !jet.gameObject.activeInHierarchy || jet.Chance <= 0f || jet.Particles == null) continue;
                        // Vertices are particle centers; shader billboard size is in UV1, so
                        // the combined MeshRenderer.bounds cannot describe the visible jet.
                        foreach (var particle in jet.Particles)
                        {
                            float shift = Mathf.Abs(particle.AxisShift) + particle.RandomShift.magnitude;
                            float motion = Mathf.Abs(shot.Manager.MoveCurve.Evaluate((now - shot.Started) / shot.Manager.ShotLength));
                            // Native FillParticle also writes axial extension into tangent.x;
                            // bound either direction without assuming a particular jet shader.
                            float radius = Mathf.Abs(particle.Size) * 1.414214f + shift +
                                Mathf.Abs(jet.JetBounds.z - jet.JetBounds.y) * motion;
                            var center = jet.transform.TransformPoint(Vector3.down * particle.Position);
                            var scale = jet.transform.lossyScale;
                            radius *= Mathf.Max(Mathf.Abs(scale.x), Mathf.Max(Mathf.Abs(scale.y), Mathf.Abs(scale.z)));
                            DrawRegion(camera, center, radius, padding);
                        }
                    }
                }
                for (int i = Particles.Count - 1; i >= 0; i--)
                {
                    var container = Particles[i];
                    if (container == null || container.RootParticleSystem == null || !container.RootParticleSystem.IsAlive(true))
                    { Particles.RemoveAt(i); continue; }
                    var data = container.MuzzleParticleData;
                    if (data == null) continue;
                    foreach (var particle in data)
                    {
                        // MBOIT may draw disabled renderers; native particle liveness is authoritative.
                        if (particle.System == null || particle.Renderer == null || particle.System.particleCount == 0) continue;
                        var bounds = particle.Renderer.bounds;
                        DrawBounds(camera, bounds, padding);
                    }
                }
                if (LastMaskedRegions > 0) MaskPasses++;
            }
            finally
            {
                GL.PopMatrix();
                GL.sRGBWrite = previousSrgb;
                RenderTexture.active = previous;
            }
        }

        static void DrawRegion(Camera camera, Vector3 center, float radius, Vector2 padding)
        {
            if (float.IsNaN(radius) || float.IsInfinity(radius) || radius <= 0f) return;
            DrawCameraBounds(camera, camera.transform.InverseTransformPoint(center), Vector3.one * radius, padding);
        }

        static void DrawBounds(Camera camera, Bounds bounds, Vector2 padding)
        {
            // Long launcher plumes should not become a sphere whose radius also widens
            // the mask across the screen. Enclose the actual AABB in camera space.
            var matrix = camera.transform.worldToLocalMatrix;
            var e = bounds.extents;
            var x = matrix.MultiplyVector(new Vector3(e.x, 0, 0));
            var y = matrix.MultiplyVector(new Vector3(0, e.y, 0));
            var z = matrix.MultiplyVector(new Vector3(0, 0, e.z));
            var extent = new Vector3(Mathf.Abs(x.x) + Mathf.Abs(y.x) + Mathf.Abs(z.x),
                Mathf.Abs(x.y) + Mathf.Abs(y.y) + Mathf.Abs(z.y),
                Mathf.Abs(x.z) + Mathf.Abs(y.z) + Mathf.Abs(z.z));
            DrawCameraBounds(camera, matrix.MultiplyPoint3x4(bounds.center), extent, padding);
        }

        static void DrawCameraBounds(Camera camera, Vector3 local, Vector3 extent, Vector2 padding)
        {
            if (local.z + extent.z <= camera.nearClipPlane) return;
            float near = Mathf.Max(camera.nearClipPlane, local.z - extent.z);
            float far = Mathf.Max(near, local.z + extent.z);
            float x0 = float.PositiveInfinity, y0 = x0, x1 = float.NegativeInfinity, y1 = x1;
            // Clip the conservative volume to the camera near plane before projecting.
            for (int z = 0; z < 2; z++)
                for (int y = -1; y <= 1; y += 2)
                    for (int x = -1; x <= 1; x += 2)
                    {
                        var p = camera.WorldToViewportPoint(camera.transform.TransformPoint(
                            new Vector3(local.x + x * extent.x, local.y + y * extent.y, z == 0 ? near : far)));
                        x0 = Mathf.Min(x0, p.x); x1 = Mathf.Max(x1, p.x);
                        y0 = Mathf.Min(y0, p.y); y1 = Mathf.Max(y1, p.y);
                    }
            x0 = Mathf.Clamp01(x0 - padding.x); x1 = Mathf.Clamp01(x1 + padding.x);
            y0 = Mathf.Clamp01(y0 - padding.y); y1 = Mathf.Clamp01(y1 + padding.y);
            if (!(x1 > x0 && y1 > y0)) return;
            LargestRegionArea = Mathf.Max(LargestRegionArea, (x1 - x0) * (y1 - y0));
            GL.Begin(GL.QUADS);
            try
            {
                GL.Color(Color.black);
                GL.Vertex3(x0, y0, 0); GL.Vertex3(x1, y0, 0);
                GL.Vertex3(x1, y1, 0); GL.Vertex3(x0, y1, 0);
            }
            finally { GL.End(); }
            LastMaskedRegions++;
        }

        internal static void Release()
        {
            Shots.Clear(); Particles.Clear();
            if (_mask != null) UnityEngine.Object.Destroy(_mask);
            _mask = null; _failed = false;
            MaskPasses = LastMaskedRegions = 0;
            LargestRegionArea = 0f;
        }
    }
}
