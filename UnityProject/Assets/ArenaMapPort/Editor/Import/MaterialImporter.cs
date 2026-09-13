// ArenaMapPort WP2 — §B.WP2.3 material import + LEAD OVERRIDE O1's sidecar emission.
//
// Runs AFTER TextureImportDriver (§B.WP2.4) and StandinShaderGenerator - creates one .mat asset
// per material id, sets shader = the stand-in ("Hidden/ArenaMapPort/" + original name, O1 — NEVER
// Shader.Find(original) at import time, since no shader of that bare name exists in this SDK
// Editor), applies every TexEnv (incl. _ST tiling/offset), Float, Color, keyword, and renderQueue.
//
// O1 sidecar: emits <outputDir>/material-shaders.json = { "<mat_id>": { shader: <original name>,
// renderQueue: <int>, keywords: [...] } } — ships alongside the built bundles so WP4 can rebind
// each stand-in-shaded material back onto the game's own compiled shader of the ORIGINAL name at
// runtime (never to a prefixed shader).
using System.Collections.Generic;
using System.IO;
using ArenaMapPort.Editor.Common;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace ArenaMapPort.Editor.Import
{
    public static class MaterialImporter
    {
        public static Dictionary<string, Material> ImportAll(
            ArenaPackage pkg,
            Dictionary<string, Shader> standinShaders,
            Dictionary<string, Texture2D> textures,
            string outputDir)
        {
            Directory.CreateDirectory(ArenaPaths.MaterialsDir);
            var result = new Dictionary<string, Material>();
            var sidecar = new JObject();

            foreach (var prop in pkg.Materials.Properties())
            {
                string matId = prop.Name;
                string assetPath = ArenaPaths.MaterialsDir + "/" + matId + ".mat";
                JObject matJson = pkg.LoadMaterialJson(matId);
                string shaderName = (string)matJson["shader"];

                Shader shader;
                if (!standinShaders.TryGetValue(shaderName, out shader) || shader == null)
                {
                    throw new IOException("[ArenaMapPort][MaterialImporter] no stand-in shader for '" + shaderName +
                                           "' (material " + matId + ") - StandinShaderGenerator must run first.");
                }

                var mat = new Material(shader);
                mat.name = matId;

                var texEnvs = (JObject)matJson["tex_envs"];
                if (texEnvs != null)
                {
                    foreach (var t in texEnvs.Properties())
                    {
                        // s2-build: skip an empty property name outright (StandinShaderGenerator
                        // doesn't declare a slot for it either, see its own CollectFromPackage
                        // comment for the real material this was found on) -- Material.SetTexture/
                        // SetFloat/SetColor("", ...) has no meaningful target to write to.
                        if (string.IsNullOrEmpty(t.Name)) continue;
                        var entry = (JObject)t.Value;
                        var texIdTok = entry["texture_id"];
                        if (texIdTok != null && texIdTok.Type != JTokenType.Null)
                        {
                            string texId = (string)texIdTok;
                            Texture2D tex;
                            if (textures.TryGetValue(texId, out tex) && tex != null)
                                mat.SetTexture(t.Name, tex);
                            else
                                Debug.LogWarning("[ArenaMapPort][MaterialImporter] material " + matId + " prop " +
                                                  t.Name + " references missing texture_id " + texId);
                        }
                        // texture_id == null is a real, valid "declared but unassigned" state (§C.4) -
                        // skip SetTexture for that slot rather than treating it as an error.

                        var scale = (JArray)entry["scale"];
                        var offset = (JArray)entry["offset"];
                        if (scale != null) mat.SetTextureScale(t.Name, new Vector2((float)scale[0], (float)scale[1]));
                        if (offset != null) mat.SetTextureOffset(t.Name, new Vector2((float)offset[0], (float)offset[1]));
                    }
                }

                var floats = (JObject)matJson["floats"];
                if (floats != null)
                    foreach (var f in floats.Properties())
                        if (!string.IsNullOrEmpty(f.Name)) mat.SetFloat(f.Name, (float)f.Value);

                var colors = (JObject)matJson["colors"];
                if (colors != null)
                    foreach (var c in colors.Properties())
                    {
                        if (string.IsNullOrEmpty(c.Name)) continue;
                        var arr = (JArray)c.Value;
                        mat.SetColor(c.Name, new Color((float)arr[0], (float)arr[1], (float)arr[2], (float)arr[3]));
                    }

                var keywordsTok = (JArray)matJson["keywords"];
                var keywordList = new List<string>();
                if (keywordsTok != null)
                {
                    foreach (var k in keywordsTok)
                    {
                        string kw = (string)k;
                        mat.EnableKeyword(kw);
                        keywordList.Add(kw);
                    }
                }

                int renderQueue = (int)matJson["render_queue"];
                if (renderQueue >= 0) mat.renderQueue = renderQueue;

                AssetDatabase.CreateAsset(mat, assetPath);
                result[matId] = mat;

                var sidecarEntry = new JObject();
                sidecarEntry["shader"] = shaderName;
                sidecarEntry["renderQueue"] = mat.renderQueue; // actual applied queue (explicit or stand-in shader default)
                sidecarEntry["keywords"] = new JArray(keywordList);
                sidecar[matId] = sidecarEntry;
            }

            AssetDatabase.SaveAssets();

            Directory.CreateDirectory(outputDir);
            string sidecarPath = Path.Combine(outputDir, "material-shaders.json").Replace('\\', '/');
            File.WriteAllText(sidecarPath, sidecar.ToString(Formatting.Indented));

            Debug.Log("[ArenaMapPort][MaterialImporter] imported " + result.Count + " materials -> " +
                      ArenaPaths.MaterialsDir + "; sidecar -> " + sidecarPath);
            return result;
        }
    }
}
