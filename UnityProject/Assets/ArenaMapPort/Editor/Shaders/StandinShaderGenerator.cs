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
using ArenaMapPort.Editor.Common;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace ArenaMapPort.Editor.Shaders
{
    public enum ArenaPropType { Tex, Float, Color }

    public class StandinShaderGenerator
    {
        // originalShaderName -> (propertyName -> type)
        private readonly Dictionary<string, Dictionary<string, ArenaPropType>> _propsByShader =
            new Dictionary<string, Dictionary<string, ArenaPropType>>();

        // originalShaderName -> Shader asset (the stand-in, named Hidden/ArenaMapPort/<original>)
        public readonly Dictionary<string, Shader> Shaders = new Dictionary<string, Shader>();

        public static string SanitizeFileName(string shaderName)
        {
            var sb = new StringBuilder();
            foreach (char c in shaderName)
                sb.Append(char.IsLetterOrDigit(c) ? c : '_');
            return sb.ToString();
        }

        public void CollectFromPackage(ArenaPackage pkg)
        {
            foreach (var prop in pkg.Materials.Properties())
            {
                string matId = prop.Name;
                JObject matJson = pkg.LoadMaterialJson(matId);
                string shaderName = (string)matJson["shader"];

                Dictionary<string, ArenaPropType> props;
                if (!_propsByShader.TryGetValue(shaderName, out props))
                {
                    props = new Dictionary<string, ArenaPropType>();
                    _propsByShader[shaderName] = props;
                }

                var texEnvs = (JObject)matJson["tex_envs"];
                if (texEnvs != null)
                    foreach (var t in texEnvs.Properties())
                        AddProp(props, shaderName, t.Name, ArenaPropType.Tex, matId);

                var floats = (JObject)matJson["floats"];
                if (floats != null)
                    foreach (var f in floats.Properties())
                        AddProp(props, shaderName, f.Name, ArenaPropType.Float, matId);

                var colors = (JObject)matJson["colors"];
                if (colors != null)
                    foreach (var c in colors.Properties())
                        AddProp(props, shaderName, c.Name, ArenaPropType.Color, matId);
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
                    // sharing one shader name. Fail loudly and specifically, not silently.
                    throw new InvalidOperationException(
                        "[ArenaMapPort][StandinShaderGenerator] property type conflict on shader '" + shaderName +
                        "': '" + propName + "' is " + existing + " in one material and " + type +
                        " in material '" + matId + "'.");
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
                if (shader == null)
                    throw new IOException("[ArenaMapPort][StandinShaderGenerator] failed to compile stand-in shader for '" + originalName + "' (" + assetPath + ") - check Editor log for shader compile errors.");
                Shaders[originalName] = shader;
            }
            AssetDatabase.SaveAssets();
            Debug.Log("[ArenaMapPort][StandinShaderGenerator] generated " + Shaders.Count + " stand-in shaders -> " + ArenaPaths.StandinShadersDir);
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
                switch (kv.Value)
                {
                    case ArenaPropType.Tex:
                        sb.Append("        ").Append(safe).Append(" (\"").Append(safe).Append("\", 2D) = \"white\" {}\n");
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
