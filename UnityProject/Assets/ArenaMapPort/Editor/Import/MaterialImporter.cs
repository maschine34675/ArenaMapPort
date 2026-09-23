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
using ArenaMapPort.Editor.Shaders;
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
            Dictionary<string, Texture> textures,
            string outputDir)
        {
            Directory.CreateDirectory(ArenaPaths.MaterialsDir);
            var result = new Dictionary<string, Material>();
            var sidecar = new JObject();
            int skippedUnassignable = 0;

            foreach (var prop in pkg.Materials.Properties())
            {
                string matId = prop.Name;
                string assetPath = ArenaPaths.MaterialsDir + "/" + matId + ".mat";
                JObject matJson = pkg.LoadMaterialJson(matId);
                string shaderName = (string)matJson["shader"];

                Shader shader;
                bool preserveShader = (bool?)matJson["preserve_shader"] == true;
                if (preserveShader)
                {
                    if (shaderName != "ArenaMapPort/Terrain")
                        throw new IOException("Only the owned terrain shader may bypass a stand-in: " + shaderName);
                    shader = AssetDatabase.LoadAssetAtPath<Shader>("Assets/ArenaMapPort/Shaders/Terrain.shader");
                    if (shader == null || shader.name != shaderName || ShaderUtil.ShaderHasError(shader))
                        throw new IOException("Owned terrain shader missing or failed compilation: " + shaderName);
                }
                else if (!standinShaders.TryGetValue(shaderName, out shader) || shader == null)
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
                        if (!StandinShaderGenerator.ShouldImportProperty(t.Name, shaderName, matId)) continue;
                        var entry = (JObject)t.Value;
                        var texIdTok = entry["texture_id"];
                        if (texIdTok != null && texIdTok.Type != JTokenType.Null)
                        {
                            string texId = (string)texIdTok;
                            string dimension = pkg.TextureDimension(texId);
                            bool terrainArray = (bool?)((JObject)pkg.Textures[texId])["generated_terrain_array"] == true;
                            bool nativeVolume = ((JObject)pkg.Textures[texId])["native_volume"] != null;
                            if (terrainArray && (!preserveShader || dimension != "Tex2DArray"))
                                throw new IOException("Generated terrain array may only bind to its owned terrain shader: " + texId);
                            if ((dimension == "Tex3D" && !nativeVolume) || (dimension == "Tex2DArray" && !terrainArray))
                            {
                                // Legacy packages without a validated native payload retain
                                // impl-texture-dimensions §2's safe fallback: leave the property
                                // UNASSIGNED so the stand-in/real shader's own declared default
                                // applies, instead of ever binding a 2D texture to a CUBE/3D
                                // slot. This is an expected, logged skip, not an error.
                                Debug.Log("[ArenaMapPort][MaterialImporter] material " + matId + " prop " +
                                          t.Name + ": texture " + texId + " has dimension '" + dimension +
                                          "' (no validated native payload) -- leaving the " +
                                          "property unassigned rather than binding a 2D texture to it.");
                                skippedUnassignable++;
                            }
                            else
                            {
                                // Native shape is required; a preview PNG must never reach a
                                // volume shader slot even if a future caller bypasses the driver.
                                Texture tex;
                                if (textures.TryGetValue(texId, out tex) && tex != null)
                                {
                                    if (terrainArray && !(tex is Texture2DArray))
                                        throw new IOException("Generated terrain array was not imported as Texture2DArray: " + texId);
                                    if (nativeVolume && (dimension != "Tex3D" || !(tex is Texture3D)))
                                        throw new IOException("Native volume was not imported as Texture3D: " + texId);
                                    mat.SetTexture(t.Name, tex);
                                }
                                else if (nativeVolume)
                                    throw new IOException("Required native volume texture missing: " + texId);
                                else if (preserveShader)
                                    throw new IOException("Required terrain texture missing: " + texId);
                                else
                                    Debug.LogWarning("[ArenaMapPort][MaterialImporter] material " + matId + " prop " +
                                                      t.Name + " references missing texture_id " + texId +
                                                      " (dimension=" + dimension + ")");
                            }
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
                        if (StandinShaderGenerator.ShouldImportProperty(f.Name, shaderName, matId)) mat.SetFloat(f.Name, (float)f.Value);

                var colors = (JObject)matJson["colors"];
                if (colors != null)
                    foreach (var c in colors.Properties())
                    {
                        if (!StandinShaderGenerator.ShouldImportProperty(c.Name, shaderName, matId)) continue;
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
                if (preserveShader) sidecarEntry["preserveShader"] = true;
                sidecar[matId] = sidecarEntry;
            }

            AssetDatabase.SaveAssets();

            Directory.CreateDirectory(outputDir);
            string sidecarPath = Path.Combine(outputDir, "material-shaders.json").Replace('\\', '/');
            File.WriteAllText(sidecarPath, sidecar.ToString(Formatting.Indented));

            Debug.Log("[ArenaMapPort][MaterialImporter] imported " + result.Count + " materials (" +
                      skippedUnassignable + " Tex3D/Tex2DArray texture slot(s) left unassigned, " +
                      "impl-texture-dimensions §2) -> " + ArenaPaths.MaterialsDir + "; sidecar -> " + sidecarPath);
            return result;
        }
    }
}
