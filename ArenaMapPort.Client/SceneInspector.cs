// docs/progress/shader-compat-and-inspector.md §b. F10 (configurable, BepInEx KeyboardShortcut)
// diagnostic hotkey: takes the main camera's center-screen
// ray, finds the nearest N renderers whose world-space AABB (Renderer.bounds) the ray intersects — no
// colliders required — and logs hierarchy/mesh/material/shader/lightmap detail for each. Designed to
// answer "what IS that thing" live in any raid (arena or vanilla), e.g. aiming at the Bay5 floating
// black square and reading back its exact scene/path/shader in one keypress instead of static analysis.
//
// Cost discipline: Update() does nothing but a KeyboardShortcut.IsDown() check (itself a thin wrapper
// over Input.GetKeyDown per key) every frame — the cheapest possible per-frame cost, matching every
// other MonoBehaviour.Update() gate already in this plugin (see WeatherRig.Runner, LightingDiagnosticProbe).
// The actual scene scan (FindObjectsOfType<Renderer>() over the whole loaded scene set, worst case
// ~26,000 renderers on Arena_Bay5_Main alone) runs ONLY inside the keypress branch, never per-frame.
//
// Never throws: every renderer's diagnostic block is its own try/catch (one broken/destroyed-mid-frame
// object must not abort the rest of the dump), RunInspection() itself is wrapped, and Update() is
// wrapped too as defense in depth (KeyboardShortcut.IsDown() cannot itself throw, but this keeps the
// file's contract uniform with the rest of the plugin's Update-hooked code).

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using BepInEx.Configuration;
using EFT.CameraControl;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace ArenaMapPort.Client
{
    internal sealed class SceneInspector : MonoBehaviour
    {
        static ConfigEntry<KeyboardShortcut> _hotkey;
        static ConfigEntry<int> _maxResults;
        static ConfigEntry<float> _maxDistance;
        static ConfigEntry<bool> _verboseShaderProperties;
        static ConfigEntry<bool> _alsoPhysicsRaycast;
        static int _inspectionSequence;
        // UiBridge limits each log entry independently. Leave room below 1,500 characters for the
        // tag and three Int32 labels, even for a path/property line longer than one entry.
        const int MaxLogPayloadLength = 1400;

        internal static void Install()
        {
            _hotkey = Plugin.Instance.Settings.Bind(
                "Inspector", "Hotkey", new KeyboardShortcut(KeyCode.F10),
                "Dumps the nearest renderers under the camera's center crosshair to the log. Works on " +
                "any map (arena or vanilla) — no raid-type gate.");
            _maxResults = Plugin.Instance.Settings.Bind(
                "Inspector", "MaxResults", 5,
                "How many of the nearest ray/bounds-intersecting renderers to log per keypress.");
            _maxDistance = Plugin.Instance.Settings.Bind(
                "Inspector", "MaxDistance", 2000f,
                "Ray hits farther than this (meters) from the camera are ignored.");
            _verboseShaderProperties = Plugin.Instance.Settings.Bind(
                "Inspector", "VerboseShaderProperties", false,
                "false = log only Color/Float/Range shader properties (the usual 'why is this black/" +
                "wrong-tinted' culprits) plus texture slot names. true = every property the shader " +
                "declares, via Shader.GetPropertyCount/GetPropertyName/GetPropertyType, including full " +
                "Vector4s.");
            _alsoPhysicsRaycast = Plugin.Instance.Settings.Bind(
                "Inspector", "AlsoPhysicsRaycast", true,
                "Also fires a Physics.Raycast along the same ray and logs what it hit, for comparison. " +
                "Most decorative/visual-only renderers (decals, shadow-proxy planes, the weather rig's " +
                "helper quads) have NO collider, so this will often legitimately report 'no hit' while " +
                "the bounds-based scan above still finds the renderer — that mismatch is itself a useful " +
                "diagnostic signal, not a bug.");

            // Plugin (BaseUnityPlugin) is itself a persistent, DontDestroyOnLoad-equivalent MonoBehaviour
            // that lives for the whole process — its GameObject is the natural, already-existing host for
            // a plain always-on Update() poller. No new host GameObject needed (contrast WeatherRig's
            // _host, which exists only because IT needs to hold a whole harvested scene hierarchy).
            if (!DeveloperOptions.Enabled) return;
            Plugin.Instance.gameObject.AddComponent<SceneInspector>();
        }

        void Update()
        {
            try
            {
                if (_hotkey.Value.IsDown())
                {
                    RunInspection();
                }
            }
            catch (Exception ex)
            {
                // Defense in depth only — KeyboardShortcut.IsDown() cannot itself throw. Keeps this
                // Update() following the same "never throws out of a hook" contract as the rest of the
                // plugin's per-frame/per-event code (see e.g. WeatherRig's OnSceneLoaded try/catch).
                Plugin.Log.LogWarning($"[ArenaMapPort][Inspector] Update threw: {ex}");
            }
        }

        static void RunInspection()
        {
            try
            {
                Bay5AmbientAudio.Probe();
                NativeSpatialAudio.Probe();
                PrecipitationAudio.Probe();
                NativeWaterRepairs.Probe();
                FootstepSurfaceProbe.Probe();
                PuddleFootstepRepairs.Probe();
                Camera cam = ResolveActiveCamera();
                if (cam == null)
                {
                    Plugin.Log.LogDebug("[ArenaMapPort][Inspector] no active camera resolved — skipping.");
                    return;
                }

                // Center-of-screen ray. Equivalent to cam.ViewportPointToRay(new Vector3(0.5f,0.5f,0f))
                // for a camera with no lens shift (true for every camera this plugin touches), but
                // cheaper (no projection-matrix work) and still valid even if cam.pixelWidth/Height are
                // momentarily 0 during a scene transition.
                var ray = new Ray(cam.transform.position, cam.transform.forward);

                var sb = new StringBuilder();
                sb.AppendLine("[ArenaMapPort][Inspector] ==== inspection " +
                    $"(camera='{cam.name}' pos={Fmt(cam.transform.position)} " +
                    $"fwd={Fmt(cam.transform.forward)} scene='{SceneManager.GetActiveScene().name}') ====");

                if (_alsoPhysicsRaycast.Value)
                {
                    AppendPhysicsComparison(sb, ray);
                }

                // Renderer.bounds is a cached world-space AABB — no mesh re-read, no collider needed.
                // FindObjectsOfType<Renderer>() only returns components on ACTIVE GameObjects (matches
                // "what would a player actually see"); it covers MeshRenderer, SkinnedMeshRenderer
                // (players/bots), and any other Renderer subtype in one pass.
                Renderer[] renderers = UnityEngine.Object.FindObjectsOfType<Renderer>();
                var hits = new List<(float dist, Renderer r)>(_maxResults.Value * 4);
                int skyLayer = LayerMask.NameToLayer("Sky");
                Vector3 camPos = cam.transform.position;
                foreach (Renderer r in renderers)
                {
                    if (r == null || !r.enabled) continue; // destroyed this frame, or not drawn anyway
                    try
                    {
                        // The first version listed only the weather rig's sky dome and rain volumes: the
                        // camera sits inside their enormous bounds, so their entry distance is hugely
                        // negative and they filled every slot (2026-09-17 16:25).
                        if (r.gameObject.layer == skyLayer) continue;
                        Bounds b = r.bounds;
                        Vector3 e = b.extents;
                        if (float.IsNaN(e.x) || float.IsInfinity(e.x) || e.magnitude > 5000f) continue;
                        if (b.Contains(camPos) && e.magnitude > 150f) continue; // surrounding volume, not a target
                        if (r.GetComponentInParent<EFT.Player>() != null) continue; // the local player's own body
                        if (b.IntersectRay(ray, out float dist) && dist <= _maxDistance.Value)
                        {
                            hits.Add((Mathf.Max(dist, 0f), r));
                        }
                    }
                    catch
                    {
                        // A single pathological renderer (e.g. a custom Renderer subclass whose .bounds
                        // getter throws) must never abort the whole scan.
                    }
                }

                hits.Sort((a, b) => a.dist.CompareTo(b.dist));
                int n = Math.Min(_maxResults.Value, hits.Count);
                sb.AppendLine($"  bounds-ray hits (of {renderers.Length} renderers scanned): " +
                    $"{hits.Count} within {_maxDistance.Value:F0}m, showing nearest {n}");

                for (int i = 0; i < n; i++)
                {
                    try
                    {
                        AppendRendererBlock(sb, i, hits[i].dist, hits[i].r);
                    }
                    catch (Exception ex)
                    {
                        sb.AppendLine($"  -- hit #{i}: FAILED to inspect ({ex.GetType().Name}: {ex.Message}) --");
                    }
                }

                LogInspection(sb.ToString());
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[ArenaMapPort][Inspector] RunInspection threw: {ex}");
            }
        }

        static void LogInspection(string dump)
        {
            var parts = SplitLogPayload(dump);
            int inspection = unchecked(++_inspectionSequence);
            for (int i = 0; i < parts.Count; i++)
                Plugin.Log.LogDebug($"[ArenaMapPort][Inspector] inspection={inspection} part={i + 1}/{parts.Count} " + parts[i]);
        }

        // Concatenating payloads reproduces the complete input, including line endings. Prefer whole
        // lines; only an overlong line is continued in the next numbered entry. Never split CRLF or a
        // UTF-16 surrogate pair, so long hierarchy names remain recoverable without damaged text.
        static List<string> SplitLogPayload(string dump)
        {
            var parts = new List<string>();
            for (int start = 0; start < dump.Length;)
            {
                int end = start + Math.Min(MaxLogPayloadLength, dump.Length - start);
                if (end < dump.Length)
                {
                    int newline = dump.LastIndexOf('\n', end - 1, end - start);
                    if (newline >= start) end = newline + 1;
                    else
                    {
                        if (dump[end - 1] == '\r' && dump[end] == '\n') end--;
                        if (char.IsHighSurrogate(dump[end - 1]) && char.IsLowSurrogate(dump[end])) end--;
                    }
                }
                parts.Add(dump.Substring(start, end - start));
                start = end;
            }
            return parts;
        }

        // Same resolution pattern as LightingDiagnosticProbe.ResolveActiveCamera (CameraManager.Instance
        // with its own try/catch, falling back to Camera.main) — duplicated here rather than made public
        // on that file to keep this a self-contained, independently-droppable diagnostic.
        static Camera ResolveActiveCamera()
        {
            try
            {
                Camera c = CameraManager.Instance?.Camera;
                if (c != null) return c;
            }
            catch { /* CameraManager mid-teardown, e.g. between raids — fall through */ }
            return Camera.main;
        }

        static void AppendPhysicsComparison(StringBuilder sb, Ray ray)
        {
            try
            {
                // Skip the local player's own colliders (the first version always hit the player's head).
                int mask = ~LayerMask.GetMask("Player", "HitCollider", "PlayerCollisionTest", "PlayerSpiritAura");
                if (Physics.Raycast(ray, out RaycastHit hit, _maxDistance.Value, mask, QueryTriggerInteraction.Ignore))
                {
                    sb.AppendLine($"  Physics.Raycast: hit '{GetPath(hit.collider.transform)}' " +
                        $"dist={hit.distance:F2} layer={LayerMask.LayerToName(hit.collider.gameObject.layer)} " +
                        $"isTrigger={hit.collider.isTrigger}");
                    var ballistic = hit.collider.GetComponent<EFT.Ballistics.BallisticCollider>();
                    if (ballistic == null && hit.collider.transform.parent != null)
                        ballistic = hit.collider.transform.parent.GetComponent<EFT.Ballistics.BallisticCollider>();
                    sb.AppendLine("  " + BallisticMaterialDiagnostics.Describe(ballistic));
                    // Colliders are usually siblings of the visible LOD renderers under one prop root —
                    // list those renderers' materials, the reliable way to identify e.g. the ground.
                    Transform root = hit.collider.transform.parent != null ? hit.collider.transform.parent : hit.collider.transform;
                    var related = root.GetComponentsInChildren<Renderer>(false);
                    int shown = 0;
                    for (int i = 0; i < related.Length && shown < 3; i++)
                    {
                        if (related[i] == null || !related[i].enabled) continue;
                        try { AppendRendererBlock(sb, shown, hit.distance, related[i]); }
                        catch (Exception ex)
                        {
                            sb.AppendLine($"  -- related renderer #{shown}: FAILED to inspect " +
                                $"({ex.GetType().Name}: {ex.Message}) --");
                        }
                        shown++;
                    }
                    sb.AppendLine($"  (renderers under the hit collider's parent '{root.name}': {related.Length}, shown {shown})");
                }
                else
                {
                    sb.AppendLine("  Physics.Raycast: no hit (expected for purely decorative/collider-" +
                        "less renderers — the bounds scan below is the primary method).");
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine($"  Physics.Raycast: threw ({ex.GetType().Name}) — skipped.");
            }
        }

        static void AppendRendererBlock(StringBuilder sb, int index, float dist, Renderer r)
        {
            GameObject go = r.gameObject;
            sb.AppendLine($"  -- hit #{index} (entry dist={dist:F2}m) --");
            sb.AppendLine($"    path='{GetPath(r.transform)}'");
            sb.AppendLine($"    scene='{go.scene.name}' layer={go.layer}('{LayerMask.LayerToName(go.layer)}') " +
                $"activeSelf={go.activeSelf} activeInHierarchy={go.activeInHierarchy} " +
                $"rendererType={r.GetType().Name} renderer.enabled={r.enabled}");

            AppendMeshInfo(sb, r);

            sb.AppendLine($"    shadowCastingMode={r.shadowCastingMode} receiveShadows={r.receiveShadows} " +
                $"lightmapIndex={r.lightmapIndex} lightProbeUsage={r.lightProbeUsage} " +
                $"reflectionProbeUsage={r.reflectionProbeUsage}");

            // sharedMaterials, NOT .materials — reading `.materials` silently instantiates a unique
            // per-renderer copy of every slot as a side effect, which would leak one Material instance
            // per inspected renderer per keypress. This is a read-only diagnostic; it must not mutate
            // renderer state.
            Material[] mats = r.sharedMaterials;
            sb.AppendLine($"    materials ({mats.Length}):");
            for (int m = 0; m < mats.Length; m++)
            {
                try { AppendMaterialInfo(sb, m, mats[m]); }
                catch (Exception ex)
                {
                    sb.AppendLine($"      [{m}] FAILED to inspect material ({ex.GetType().Name}: {ex.Message})");
                }
            }

            Component[] comps = go.GetComponents<Component>();
            sb.AppendLine("    components: " + string.Join(", ", comps.Select(c =>
                c == null ? "<missing script>" : c.GetType().Name)));
        }

        static void AppendMeshInfo(StringBuilder sb, Renderer r)
        {
            Mesh mesh = null;
            if (r is SkinnedMeshRenderer smr)
            {
                mesh = smr.sharedMesh;
            }
            else
            {
                MeshFilter mf = r.GetComponent<MeshFilter>();
                if (mf != null) mesh = mf.sharedMesh;
            }

            if (mesh == null)
            {
                sb.AppendLine("    mesh=<none> (no MeshFilter/SkinnedMeshRenderer.sharedMesh — " +
                    $"e.g. a {r.GetType().Name} that draws procedurally)");
                return;
            }

            // vertexCount/subMeshCount/bounds are metadata, always readable regardless of the mesh's
            // isReadable flag. Only the CPU-side arrays (.colors/.vertices/.normals/.uv/...) require
            // isReadable=true — accessing them on a non-readable mesh throws UnityException, so that
            // flag must be checked FIRST, per the task's own "isReadable permitting" phrasing.
            string colorsInfo;
            if (!mesh.isReadable)
            {
                colorsInfo = "unknown (mesh.isReadable=false)";
            }
            else
            {
                try
                {
                    colorsInfo = mesh.colors != null && mesh.colors.Length > 0
                        ? $"present ({mesh.colors.Length} entries)"
                        : "empty";
                }
                catch (Exception ex)
                {
                    colorsInfo = $"threw reading .colors ({ex.GetType().Name})";
                }
            }

            sb.AppendLine($"    mesh='{mesh.name}' vertexCount={mesh.vertexCount} " +
                $"subMeshCount={mesh.subMeshCount} isReadable={mesh.isReadable} vertexColors={colorsInfo}");
        }

        static void AppendMaterialInfo(StringBuilder sb, int slot, Material mat)
        {
            if (mat == null)
            {
                sb.AppendLine($"      [{slot}] <null material slot>");
                return;
            }

            Shader sh = mat.shader;
            string shaderName = sh != null ? sh.name : "<null shader>";
            string mainTextureInfo;
            try
            {
                // Unity logs an error (without throwing) for mainTexture on shaders lacking
                // _MainTex, including shadow and layered ground. Other slots are listed below.
                Texture mainTex = mat.HasProperty("_MainTex") ? mat.GetTexture("_MainTex") : null;
                mainTextureInfo = mainTex != null ? mainTex.name : "<none>";
            }
            catch (Exception ex)
            {
                // A failed getter does not establish that the material has no texture.
                mainTextureInfo = $"<unavailable: {ex.GetType().Name}>";
            }

            sb.AppendLine($"      [{slot}] '{mat.name}' shader='{shaderName}' renderQueue={mat.renderQueue} " +
                $"mainTexture={mainTextureInfo} " +
                $"keywords=[{string.Join(",", mat.shaderKeywords)}]");

            if (sh == null) return;

            // This project's Unity/UnityEngine.CoreModule build (2022.3.43f1) exposes these as INSTANCE
            // methods on Shader (sh.GetPropertyCount(), sh.GetPropertyName(i), ...), not the static
            // Shader.GetPropertyCount(Shader) overload the original draft assumed (a newer/Unity-6-style
            // API shape) — confirmed via reflection against EscapeFromTarkov_Data/Managed/
            // UnityEngine.CoreModule.dll (adversarial-review-class fix, caught at build time here).
            int propCount;
            try { propCount = sh.GetPropertyCount(); }
            catch { sb.AppendLine("        <Shader.GetPropertyCount threw>"); return; }

            for (int i = 0; i < propCount; i++)
            {
                string propName = null;
                try
                {
                    propName = sh.GetPropertyName(i);
                    ShaderPropertyType propType = sh.GetPropertyType(i);
                    switch (propType)
                    {
                        case ShaderPropertyType.Color:
                            sb.AppendLine($"        {propName} (Color) = {Fmt(mat.GetColor(propName))}");
                            break;
                        case ShaderPropertyType.Float:
                        case ShaderPropertyType.Range:
                            sb.AppendLine($"        {propName} ({propType}) = {mat.GetFloat(propName):F4}");
                            break;
                        case ShaderPropertyType.Vector:
                            if (_verboseShaderProperties.Value)
                                sb.AppendLine($"        {propName} (Vector) = {Fmt(mat.GetVector(propName))}");
                            break;
                        case ShaderPropertyType.Texture:
                            {
                                Texture t = mat.GetTexture(propName);
                                sb.AppendLine($"        {propName} (Texture) = " +
                                    $"{(t != null ? $"'{t.name}' {t.width}x{t.height}" : "<none>")}");
                                break;
                            }
                        case ShaderPropertyType.Int: // Unity 2021.1+ property type; present in 2022.3
                            if (_verboseShaderProperties.Value)
                                sb.AppendLine($"        {propName} (Int) = {mat.GetInteger(propName)}");
                            break;
                        default:
                            if (_verboseShaderProperties.Value)
                                sb.AppendLine($"        {propName} ({propType}) = <unhandled type>");
                            break;
                    }
                }
                catch (Exception ex)
                {
                    sb.AppendLine($"        property #{i}{(propName != null ? $" '{propName}'" : "")}: " +
                        $"<unavailable: {ex.GetType().Name}>");
                }
            }
        }

        static string GetPath(Transform t)
        {
            if (t == null) return "<null>";
            var sb = new StringBuilder(t.name);
            Transform p = t.parent;
            int guard = 0;
            while (p != null && guard++ < 200) // guard against a corrupt/cyclic hierarchy
            {
                sb.Insert(0, p.name + "/");
                p = p.parent;
            }
            return sb.ToString();
        }

        static string Fmt(Vector3 v) => $"({v.x:F2}, {v.y:F2}, {v.z:F2})";
        static string Fmt(Color c) => $"RGBA({c.r:F3}, {c.g:F3}, {c.b:F3}, {c.a:F3})";
        static string Fmt(Vector4 v) => $"({v.x:F3}, {v.y:F3}, {v.z:F3}, {v.w:F3})";
    }
}
