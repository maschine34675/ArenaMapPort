// ArenaMapPort WP2 — §B.WP2.4 texture import (named "Driver" to avoid colliding with
// UnityEditor.TextureImporter). Copies WP1's already-decoded PNGs into
// Assets/ArenaImport/Textures/, sets textureType=NormalMap + sRGBTexture=false for anything WP1
// flagged as a normal map, sRGBTexture=true otherwise (diffuse/albedo convention).
//
// LEAD OVERRIDE O3: textures used as alpha-cutout masks must not be quantized lossy-wrong (use
// BC7/high quality or uncompressed where alpha matters); normal maps: textureType=NormalMap,
// sRGB off. A texture is flagged alpha-cutout here if any material whose shader name contains
// "Cutout" (§B.WP1's own texture pool, arena-census.md §6's confirmed shader-name families)
// references it via any tex_env slot.
//
// Runs BEFORE §B.WP2.3 material import (execution-order note, SPEC.md) - MaterialImporter.SetTexture
// against a not-yet-imported Texture2D is a silent no-op, not an error.
using System.Collections.Generic;
using System.IO;
using ArenaMapPort.Editor.Common;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace ArenaMapPort.Editor.Import
{
    public static class TextureImportDriver
    {
        public static Dictionary<string, Texture2D> ImportAll(ArenaPackage pkg)
        {
            Directory.CreateDirectory(ArenaPaths.TexturesDir);

            HashSet<string> alphaCutoutTexIds = CollectAlphaCutoutTextureIds(pkg);

            var result = new Dictionary<string, Texture2D>();
            foreach (var prop in pkg.Textures.Properties())
            {
                string texId = prop.Name;
                string srcPng = pkg.TexturePngPath(texId);
                string assetPath = ArenaPaths.TexturesDir + "/" + texId + ".png";

                if (!File.Exists(assetPath) || !FilesIdentical(srcPng, assetPath))
                    File.Copy(srcPng, assetPath, true);

                AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport);

                var importer = (TextureImporter)AssetImporter.GetAtPath(assetPath);
                bool isNormalMap = pkg.TextureIsNormalMap(texId);
                bool isCutoutMask = alphaCutoutTexIds.Contains(texId);

                importer.textureType = isNormalMap ? TextureImporterType.NormalMap : TextureImporterType.Default;
                importer.sRGBTexture = !isNormalMap;
                importer.alphaIsTransparency = !isNormalMap;
                importer.mipmapEnabled = true;
                importer.textureCompression = isCutoutMask
                    ? TextureImporterCompression.Uncompressed // O3 - no lossy alpha quantization for cutout masks
                    : TextureImporterCompression.Compressed;

                importer.SaveAndReimport();

                result[texId] = AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
            }
            AssetDatabase.SaveAssets();
            Debug.Log("[ArenaMapPort][TextureImportDriver] imported " + result.Count + " textures (" +
                      alphaCutoutTexIds.Count + " flagged alpha-cutout, uncompressed) -> " + ArenaPaths.TexturesDir);
            return result;
        }

        private static HashSet<string> CollectAlphaCutoutTextureIds(ArenaPackage pkg)
        {
            var set = new HashSet<string>();
            foreach (var prop in pkg.Materials.Properties())
            {
                string matId = prop.Name;
                string shaderName = pkg.MaterialShaderName(matId);
                if (string.IsNullOrEmpty(shaderName) || shaderName.IndexOf("Cutout", System.StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                JObject matJson = pkg.LoadMaterialJson(matId);
                var texEnvs = (JObject)matJson["tex_envs"];
                if (texEnvs == null) continue;
                foreach (var t in texEnvs.Properties())
                {
                    var entry = (JObject)t.Value;
                    var texIdTok = entry["texture_id"];
                    if (texIdTok != null && texIdTok.Type != JTokenType.Null)
                        set.Add((string)texIdTok);
                }
            }
            return set;
        }

        private static bool FilesIdentical(string a, string b)
        {
            byte[] ba = File.ReadAllBytes(a);
            byte[] bb = File.ReadAllBytes(b);
            if (ba.Length != bb.Length) return false;
            for (int i = 0; i < ba.Length; i++) if (ba[i] != bb[i]) return false;
            return true;
        }
    }
}
