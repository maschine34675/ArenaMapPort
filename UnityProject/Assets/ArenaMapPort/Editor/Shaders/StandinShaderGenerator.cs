// ArenaMapPort WP2 — §B.WP2.3 stand-in shaders, per LEAD OVERRIDE O1.
//
// O1: every stand-in shader is named "Hidden/ArenaMapPort/" + <original BSG shader name> (NEVER
// the bare original name — that would let the SDK Editor's own Shader.Find(original) ambiguously
// resolve to our own placeholder) and declares the original's property names/types (TexEnv ->
// 2D, Float -> Float, Color -> Color), unioned across every material that uses the shader. The
// stand-in's actual shading math is irrelevant (WP4 rebinds to the game's real compiled shader
// before the player ever sees a frame, §F.3) — it only needs to (a) compile with zero errors and
// (b) declare every property slot a material JSON references, with a matching type, so
// Material.SetFloat/SetColor/SetTexture on import doesn't throw/silently no-op.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using ArenaMapPort.Editor.Common;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace ArenaMapPort.Editor.Shaders
{
    // impl-texture-dimensions §2: Tex split into Tex2D/TexCube/Tex3D -- "Check the stand-in
    // shader property declarations use the right types" (task item 2). Every stand-in TexEnv
    // property used to be declared "2D" unconditionally, which is exactly how a Cubemap/Texture3D
    // source ends up bound to a mismatched-dimension shader slot in the first place (a 2D-typed
    // property accepts a Texture2D SetTexture call with no error, silently). Declaring the real
    // dimension here doesn't by itself fix anything at runtime (WP4 rebinds every material onto
    // the game's own REAL compiled shader before a frame renders, §F.3 -- our stand-in's own
    // declared types are never seen by the player), but it keeps this Editor-only asset correct
    // and self-consistent with what MaterialImporter actually assigns (§2's own dimension-gated
    // SetTexture calls), and gives validate_bundle_roundtrip.py's new cross-check (task item 3)
    // real Cube/3D dimension metadata to read back out of the built bundle's compiled Shader
    // objects instead of only ever seeing "2D".
    public enum ArenaPropType { Tex2D, TexCube, Tex3D, Tex2DArray, Float, Color }

    public class StandinShaderGenerator
    {
        private static readonly Regex PropertyIdentifier = new Regex(@"\A[A-Za-z_][A-Za-z0-9_]*\z", RegexOptions.CultureInvariant);

        // Source saved-property dictionaries can retain orphaned serialization
        // placeholders. Their original values remain in the extracted JSON.
        // Nature/SpeedTreeEFT's actual source property table has no <noninit> slot.
        public static bool ShouldImportProperty(string name, string shaderName, string matId)
        {
            if (string.IsNullOrEmpty(name)) return false; // Existing source empty-slot policy.
            if (name == "<noninit>")
            {
                Debug.LogWarning("[ArenaMapPort][MaterialProperty] material " + matId + " shader '" + shaderName +
                                 "': skipping source saved-property placeholder '<noninit>'; original value remains in package JSON.");
                return false;
            }
            if (!PropertyIdentifier.IsMatch(name))
                throw new IOException("Invalid ShaderLab property identifier '" + name + "' on material " + matId +
                                      " (shader '" + shaderName + "'); requires source investigation, refusing to drop it.");
            return true;
        }

        // originalShaderName -> (propertyName -> type)
        private readonly Dictionary<string, Dictionary<string, ArenaPropType>> _propsByShader =
            new Dictionary<string, Dictionary<string, ArenaPropType>>();

        // originalShaderName -> Shader asset (the stand-in, named Hidden/ArenaMapPort/<original>)
        public readonly Dictionary<string, Shader> Shaders = new Dictionary<string, Shader>();

        private static ArenaPropType TexPropTypeForDimension(string dimension)
        {
            switch (dimension)
            {
                case "Cube": return ArenaPropType.TexCube;
                case "Tex3D": return ArenaPropType.Tex3D;
                case "Tex2DArray": return ArenaPropType.Tex2DArray;
                default: return ArenaPropType.Tex2D;
            }
        }

        public static string SanitizeFileName(string shaderName)
        {
            var sb = new StringBuilder();
            foreach (char c in shaderName)
                sb.Append(char.IsLetterOrDigit(c) ? c : '_');
            return sb.ToString();
        }

        // s2-build: real full-material-set data exposed one genuine empty-string property name
        // (material 0c970e4ab3f2caa7 / arena_equator_decal_drip, shader "Decal/Ultra Deferred Decal
        // Of God 3000") -- present in BOTH that material's own floats AND colors dicts simultaneously
        // (confirmed: the only material out of the full 332 with an empty key at all, in either
        // dict), which self-conflicted against AddProp's type-mismatch guard on its very first real
        // exercise. A stale/orphaned serialized property slot with no name is not something any real
        // ShaderLab `Properties {}` block can declare (empty identifiers are a syntax error) or that
        // Material.SetFloat/SetColor("", ...) does anything meaningful with (a silent no-op) --
        // skipping it is strictly safe, not a truncation of real, usable data.
        public void CollectFromPackage(ArenaPackage pkg)
        {
            // impl-texture-dimensions §2: a tex-prop name whose ONLY material references so far
            // all have texture_id == null carries no dimension signal yet -- collected here
            // separately and defaulted to Tex2D in the finalize pass below, ONLY if no material
            // anywhere in the whole package ever resolves it to something else. This two-pass
            // split (instead of calling AddProp with a Tex2D placeholder inline) matters
            // precisely because AddProp's conflict logic is first-wins: if a placeholder Tex2D
            // were written before a later material's real Cube/Tex3D resolution is seen, AddProp
            // would incorrectly "keep" the placeholder and warn about a spurious conflict.
            var unresolvedTexPropsByShader = new Dictionary<string, HashSet<string>>();

            foreach (var prop in pkg.Materials.Properties())
            {
                string matId = prop.Name;
                JObject matJson = pkg.LoadMaterialJson(matId);
                string shaderName = (string)matJson["shader"];
                if ((bool?)matJson["preserve_shader"] == true)
                {
                    if (shaderName != "ArenaMapPort/Terrain")
                        throw new IOException("Only ArenaMapPort/Terrain may bypass stand-in generation: " + shaderName);
                    continue;
                }

                Dictionary<string, ArenaPropType> props;
                if (!_propsByShader.TryGetValue(shaderName, out props))
                {
                    props = new Dictionary<string, ArenaPropType>();
                    _propsByShader[shaderName] = props;
                }

                var texEnvs = (JObject)matJson["tex_envs"];
                if (texEnvs != null)
                {
                    foreach (var t in texEnvs.Properties())
                    {
                        if (!ShouldImportProperty(t.Name, shaderName, matId)) continue;
                        var entry = (JObject)t.Value;
                        var texIdTok = entry["texture_id"];
                        if (texIdTok != null && texIdTok.Type != JTokenType.Null)
                        {
                            // A real texture reference -- resolve its actual dimension from the
                            // package (task item 2: "Check the stand-in shader property
                            // declarations use the right types") instead of always declaring 2D.
                            string texId = (string)texIdTok;
                            ArenaPropType resolvedType = TexPropTypeForDimension(pkg.TextureDimension(texId));
                            AddProp(props, shaderName, t.Name, resolvedType, matId);
                        }
                        else
                        {
                            HashSet<string> set;
                            if (!unresolvedTexPropsByShader.TryGetValue(shaderName, out set))
                            {
                                set = new HashSet<string>();
                                unresolvedTexPropsByShader[shaderName] = set;
                            }
                            set.Add(t.Name);
                        }
                    }
                }

                var floats = (JObject)matJson["floats"];
                if (floats != null)
                    foreach (var f in floats.Properties())
                        if (ShouldImportProperty(f.Name, shaderName, matId)) AddProp(props, shaderName, f.Name, ArenaPropType.Float, matId);

                var colors = (JObject)matJson["colors"];
                if (colors != null)
                    foreach (var c in colors.Properties())
                        if (ShouldImportProperty(c.Name, shaderName, matId)) AddProp(props, shaderName, c.Name, ArenaPropType.Color, matId);
            }

            // Finalize: default any tex-prop name that NEVER resolved to a real dimension
            // anywhere in the package to Tex2D (the old, always-2D behavior) -- without ever
            // overriding a real resolution already written into `props` above.
            foreach (var kv in unresolvedTexPropsByShader)
            {
                Dictionary<string, ArenaPropType> props = _propsByShader[kv.Key];
                foreach (string propName in kv.Value)
                    if (!props.ContainsKey(propName))
                        props[propName] = ArenaPropType.Tex2D;
            }
        }

        private void AddProp(Dictionary<string, ArenaPropType> props, string shaderName, string propName, ArenaPropType type, string matId)
        {
            ArenaPropType existing;
            if (props.TryGetValue(propName, out existing))
            {
                if (existing != type)
                {
                    // §B.WP2 edge case: a property type mismatch between two materials nominally
                    // sharing one shader name — confirmed for real on the full material set (s2-build):
                    // exactly 1 genuine cross-material conflict out of 332 materials/24 shaders
                    // (shader "Global Fog/Transparent Reflective Specular", property "_BumpTiling",
                    // Float on 3 materials vs a Color/Vector4 override on 1 -- a real BSG authoring
                    // inconsistency, not an extractor bug; a from-scratch full-package scan found no
                    // other instance). §B.WP2's own edge-case text calls for failing the import of
                    // "that specific material", NOT the whole multi-thousand-object pipeline, and
                    // explicitly "do not silently drop the renderer" — a renderer left with no
                    // material at all is the worse failure mode. A ShaderLab `Properties {}` block
                    // can only declare one type per name, so keep the first-seen type here (warn,
                    // don't throw) and let MaterialImporter proceed: it applies every material's own
                    // float/color/tex values independently of what this generator declared, and
                    // Material.SetColor/SetFloat for a name the shader didn't declare in that type is
                    // a well-documented, harmless no-op in Unity's API (not a throw) — so every OTHER
                    // property on the conflicting material still applies correctly; only this one
                    // property's precise value is dropped on the outlier material, which is within
                    // the spike's own stated tolerance (§B.WP2.4: "visual fidelity is not a spike
                    // success criterion, only renders/doesn't crash/colliders match").
                    Debug.LogWarning(
                        "[ArenaMapPort][StandinShaderGenerator] property type conflict on shader '" + shaderName +
                        "': '" + propName + "' is " + existing + " (kept) vs " + type +
                        " in material '" + matId + "' (that material's value for this one property will not apply).");
                }
                return;
            }
            props[propName] = type;
        }

        public void GenerateAll()
        {
            Directory.CreateDirectory(ArenaPaths.StandinShadersDir);
            foreach (var kv in _propsByShader)
            {
                string originalName = kv.Key;
                string standinName = ArenaPaths.ShaderNamePrefix + originalName;
                string fileName = SanitizeFileName(originalName) + ".shader";
                string assetPath = ArenaPaths.StandinShadersDir + "/" + fileName;

                string source = BuildShaderSource(standinName, kv.Value);
                File.WriteAllText(assetPath, source);
                AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport);

                Shader shader = AssetDatabase.LoadAssetAtPath<Shader>(assetPath);
                if (shader == null || shader.name != standinName || ShaderUtil.ShaderHasError(shader))
                {
                    if (shader != null)
                        foreach (var message in ShaderUtil.GetShaderMessages(shader))
                            Debug.LogError("[ArenaMapPort][StandinShaderGenerator] " + assetPath + ":" + message.line +
                                           " " + message.severity + ": " + message.message);
                    throw new IOException("[ArenaMapPort][StandinShaderGenerator] failed to compile stand-in shader for '" + originalName + "' (" + assetPath + ") - check Editor log for shader compile errors.");
                }
                Shaders[originalName] = shader;
            }
            AssetDatabase.SaveAssets();
            Debug.Log("[ArenaMapPort][StandinShaderGenerator] generated " + Shaders.Count + " stand-in shaders -> " + ArenaPaths.StandinShadersDir);
        }

        // Bounded import/compiler diagnosis. This creates only transient shader objects,
        // never scenes or bundles. Invoke with -executeMethod and inspect the JSON/log.
        public static void DiagnoseCompile()
        {
            var report = new JObject();
            var cases = new JArray();
            report["unity_version"] = Application.unityVersion;
            report["graphics_device"] = SystemInfo.graphicsDeviceType.ToString();
            report["cases"] = cases;
            int exitCode = 0;
            try
            {
                string standardPath = ArenaPaths.StandinShadersDir + "/Standard.shader";
                foreach (string path in new[] { standardPath, "Assets/ArenaMapPort/Shaders/Terrain.shader" })
                {
                    Shader existing = AssetDatabase.LoadAssetAtPath<Shader>(path);
                    cases.Add(DescribeDiagnosticShader("existing:" + path, existing));
                }

                string minimal = "Shader \"Hidden/ArenaMapPort/Diagnostic/Minimal\" { SubShader { Pass { } } }";
                string properties = "Shader \"Hidden/ArenaMapPort/Diagnostic/Properties\" { Properties { " +
                    "_AlphaTestRef (\"_AlphaTestRef\", Float) = 0 " +
                    "_BumpMap (\"_BumpMap\", 2D) = \"white\" {} " +
                    "_Color (\"_Color\", Color) = (1,1,1,1) } SubShader { Pass { } } }";
                AddDiagnosticCase(cases, "minimal-reference", minimal);
                AddDiagnosticCase(cases, "property-reference", properties);
                var props = new Dictionary<string, ArenaPropType>
                {
                    { "_AlphaTestRef", ArenaPropType.Float }, { "_BumpMap", ArenaPropType.Tex2D },
                    { "_Color", ArenaPropType.Color }
                };
                AddDiagnosticCase(cases, "current-generator", BuildShaderSource(
                    ArenaPaths.ShaderNamePrefix + "Diagnostic/Current", props));
                if (File.Exists(standardPath))
                {
                    string source = File.ReadAllText(standardPath).Replace(
                        "Shader \"Hidden/ArenaMapPort/Standard\"", "Shader \"Hidden/ArenaMapPort/Diagnostic/Standard\"");
                    AddDiagnosticCase(cases, "actual-standard", source);
                    AddDiagnosticCase(cases, "actual-standard-ascii", new string(source.Select(c => c <= 127 ? c : ' ').ToArray()));
                    AddDiagnosticCase(cases, "actual-standard-without-comments", string.Join("\n", source.Split('\n').Where(
                        line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal))));
                }
                // Optional real-package preflight: every actual property set is
                // collected and compiled before any texture or scene import.
                string[] args = Environment.GetCommandLineArgs();
                int packageIndex = Array.IndexOf(args, "-packageDir");
                if (packageIndex >= 0)
                {
                    if (packageIndex + 1 >= args.Length || args[packageIndex + 1].StartsWith("-", StringComparison.Ordinal))
                        throw new ArgumentException("-packageDir requires an extracted package directory");
                    var package = new ArenaPackage(args[packageIndex + 1]);
                    var generator = new StandinShaderGenerator();
                    generator.CollectFromPackage(package);
                    generator.GenerateAll();
                    foreach (var shader in generator.Shaders.OrderBy(entry => entry.Key, StringComparer.Ordinal))
                        cases.Add(DescribeDiagnosticShader("package:" + shader.Key, shader.Value));
                    report["package"] = Path.GetFullPath(package.Root);
                    report["package_material_count"] = package.Materials.Count;
                    report["package_shader_count"] = generator.Shaders.Count;
                }
            }
            catch (Exception error)
            {
                report["exception"] = error.ToString();
                exitCode = 1;
            }
            string output = Path.GetFullPath(Path.Combine(Application.dataPath, "../../work/shader-compile-diagnostic.json"));
            Directory.CreateDirectory(Path.GetDirectoryName(output));
            File.WriteAllText(output, report.ToString());
            Debug.Log("[ArenaMapPort][ShaderDiagnostic] " + report.ToString());
            Debug.Log("[ArenaMapPort][ShaderDiagnostic] report=" + output);
            EditorApplication.Exit(exitCode);
        }

        private static void AddDiagnosticCase(JArray cases, string label, string source)
        {
            Shader shader = null;
            try
            {
                shader = ShaderUtil.CreateShaderAsset(source);
                JObject result = DescribeDiagnosticShader(label, shader);
                result["source"] = source;
                cases.Add(result);
            }
            catch (Exception error)
            {
                cases.Add(new JObject { ["label"] = label, ["exception"] = error.ToString(), ["source"] = source });
            }
            finally
            {
                if (shader != null) UnityEngine.Object.DestroyImmediate(shader);
            }
        }

        private static JObject DescribeDiagnosticShader(string label, Shader shader)
        {
            var result = new JObject { ["label"] = label, ["exists"] = shader != null };
            if (shader == null) return result;
            result["name"] = shader.name;
            result["has_error"] = ShaderUtil.ShaderHasError(shader);
            result["supported"] = shader.isSupported;
            var messages = new JArray();
            foreach (var message in ShaderUtil.GetShaderMessages(shader))
                messages.Add(new JObject { ["line"] = message.line, ["severity"] = message.severity.ToString(), ["message"] = message.message });
            result["messages"] = messages;
            return result;
        }

        private static string BuildShaderSource(string standinName, Dictionary<string, ArenaPropType> props)
        {
            var sb = new StringBuilder();
            sb.Append("// AUTO-GENERATED by ArenaMapPort.Editor.Shaders.StandinShaderGenerator - do not hand-edit.\n");
            sb.Append("// Stand-in for the real BSG shader named exactly (without the \"Hidden/ArenaMapPort/\" prefix):\n");
            sb.Append("//   ").Append(standinName.Substring(ArenaPaths.ShaderNamePrefix.Length)).Append('\n');
            sb.Append("// Never rendered in-game - WP4 rebinds every material using this shader back onto the\n");
            sb.Append("// game's own compiled shader of that real name before the player sees a frame (§F.3/O1).\n");
            sb.Append("Shader \"").Append(EscapeShaderName(standinName)).Append("\"\n{\n");
            sb.Append("    Properties\n    {\n");
            foreach (var kv in props.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                string safe = kv.Key;
                if (!PropertyIdentifier.IsMatch(safe))
                    throw new IOException("Cannot emit invalid ShaderLab identifier: " + safe);
                switch (kv.Value)
                {
                    case ArenaPropType.Tex2D:
                        sb.Append("        ").Append(safe).Append(" (\"").Append(safe).Append("\", 2D) = \"white\" {}\n");
                        break;
                    case ArenaPropType.TexCube:
                        // impl-texture-dimensions §2 -- declared with the REAL dimension
                        // (matches the real BSG shader's own property type) so this Editor-only
                        // asset never mismatches what MaterialImporter actually assigns.
                        sb.Append("        ").Append(safe).Append(" (\"").Append(safe).Append("\", Cube) = \"white\" {}\n");
                        break;
                    case ArenaPropType.Tex3D:
                        sb.Append("        ").Append(safe).Append(" (\"").Append(safe).Append("\", 3D) = \"white\" {}\n");
                        break;
                    case ArenaPropType.Tex2DArray:
                        sb.Append("        ").Append(safe).Append(" (\"").Append(safe).Append("\", 2DArray) = \"white\" {}\n");
                        break;
                    case ArenaPropType.Float:
                        sb.Append("        ").Append(safe).Append(" (\"").Append(safe).Append("\", Float) = 0\n");
                        break;
                    case ArenaPropType.Color:
                        sb.Append("        ").Append(safe).Append(" (\"").Append(safe).Append("\", Color) = (1,1,1,1)\n");
                        break;
                }
            }
            sb.Append("    }\n");
            sb.Append("    SubShader\n    {\n");
            sb.Append("        Tags { \"RenderType\"=\"Opaque\" }\n");
            sb.Append("        LOD 100\n");
            sb.Append("        Pass\n        {\n");
            sb.Append("            CGPROGRAM\n");
            sb.Append("            #pragma vertex vert\n");
            sb.Append("            #pragma fragment frag\n");
            sb.Append("            #include \"UnityCG.cginc\"\n");
            sb.Append("            struct appdata { float4 vertex : POSITION; };\n");
            sb.Append("            struct v2f { float4 vertex : SV_POSITION; };\n");
            sb.Append("            v2f vert (appdata v) { v2f o; o.vertex = UnityObjectToClipPos(v.vertex); return o; }\n");
            sb.Append("            fixed4 frag (v2f i) : SV_Target { return fixed4(1,0,1,1); }\n");
            sb.Append("            ENDCG\n");
            sb.Append("        }\n");
            sb.Append("    }\n");
            sb.Append("}\n");
            return sb.ToString();
        }

        private static string EscapeShaderName(string name)
        {
            return name.Replace("\"", "\\\"");
        }
    }
}
