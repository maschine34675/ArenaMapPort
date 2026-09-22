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
// impl-texture-dimensions §2: a texture's manifest "dimension" (§C-compatible extension, WP1
// side) now drives the SHAPE of the imported asset, never just its 2D import settings --
// "Cube" imports the vertical 6-frame strip WP1 emits as a real TextureImporterShape.TextureCube
// asset (TextureImporterGenerateCubemap.AutoCubemap auto-detects the 1:6 strip layout and builds
// an actual Cubemap object), "Tex3D"/"Tex2DArray" are imported as an ordinary, UNUSED 2D texture
// (kept for inspection only -- MaterialImporter never assigns it to any material property, see
// that file's own comment) since building a real Texture3D/Texture2DArray asset from a flattened
// PNG is out of scope for the spike's one real occurrence (§B.WP2.4 task item 2's explicit
// "if a correct import is not feasible, do not assign" fallback). This is the fix for the
// reported "Error assigning 2D texture to CUBE/3D texture property" scene-load errors: a
// Cubemap-shaped asset now actually exists and gets assigned, and a Tex3D one is never assigned
// at all -- so no material ever carries a 2D Texture2D reference in a CUBE/3D-typed shader slot.
//
// Return type is the base UnityEngine.Texture (not Texture2D) since a Cube-dimension entry
// resolves to a Cubemap asset, not a Texture2D one -- Material.SetTexture accepts either.
//
// Runs BEFORE §B.WP2.3 material import (execution-order note, SPEC.md) - MaterialImporter.SetTexture
// against a not-yet-imported texture is a silent no-op, not an error.
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
        public static Dictionary<string, Texture> ImportAll(ArenaPackage pkg)
        {
            Directory.CreateDirectory(ArenaPaths.TexturesDir);

            HashSet<string> alphaCutoutTexIds = CollectAlphaCutoutTextureIds(pkg);

            var result = new Dictionary<string, Texture>();
            int cubeCount = 0, skippedUnassignableCount = 0;
            foreach (var prop in pkg.Textures.Properties())
            {
                string texId = prop.Name;
                var metadata = (JObject)prop.Value;
                if ((bool?)metadata["generated_terrain_array"] == true)
                {
                    result[texId] = ImportTerrainArray(pkg, texId, metadata);
                    continue;
                }
                string srcPng = pkg.TexturePngPath(texId);
                string assetPath = ArenaPaths.TexturesDir + "/" + texId + ".png";

                if (!File.Exists(assetPath) || !FilesIdentical(srcPng, assetPath))
                    File.Copy(srcPng, assetPath, true);

                AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport);

                var importer = (TextureImporter)AssetImporter.GetAtPath(assetPath);
                bool isNormalMap = pkg.TextureIsNormalMap(texId);
                bool isCutoutMask = alphaCutoutTexIds.Contains(texId);
                string dimension = pkg.TextureDimension(texId);

                if (dimension == "Cube")
                {
                    // §2: WP1 already stitched the 6 real decoded faces into a vertical 1:6 strip
                    // (CUBE_FACE_ORDER = +X,-X,+Y,-Y,+Z,-Z, the standard Unity strip-layout face
                    // order) -- AutoCubemap detects that aspect ratio and builds a real Cubemap.
                    importer.textureType = TextureImporterType.Default;
                    importer.textureShape = TextureImporterShape.TextureCube;
                    importer.generateCubemap = TextureImporterGenerateCubemap.AutoCubemap;
                    // Reflection maps can contain either encoded sRGB or already-linear values.
                    // The source flag, not the texture's purpose, determines GPU decoding.
                    string colorSpace = pkg.TextureColorSpace(texId);
                    if (colorSpace != "sRGB" && colorSpace != "Linear")
                        throw new InvalidDataException("Invalid Cubemap color space: " + texId + " (" + colorSpace + ")");
                    importer.sRGBTexture = colorSpace == "sRGB";
                    importer.alphaIsTransparency = false;
                    importer.mipmapEnabled = true;
                    importer.textureCompression = TextureImporterCompression.Compressed;
                    importer.SaveAndReimport();

                    var cubemap = AssetDatabase.LoadAssetAtPath<Cubemap>(assetPath);
                    if (cubemap == null)
                    {
                        // AutoCubemap failed to recognize the strip (shouldn't happen for a real
                        // WP1-emitted 1:6-aspect strip, but never silently leave a 2D texture
                        // assignable in its place -- that's exactly the bug being fixed here).
                        Debug.LogWarning("[ArenaMapPort][TextureImportDriver] texture " + texId +
                                          " (dimension=Cube) failed to import as a Cubemap (AutoCubemap did not " +
                                          "produce one) -- leaving it OUT of the returned set so MaterialImporter " +
                                          "skips assigning it rather than falling back to a 2D texture asset.");
                        skippedUnassignableCount++;
                        continue;
                    }
                    result[texId] = cubemap;
                    cubeCount++;
                    continue;
                }

                // "Tex3D"/"Tex2DArray": import as a plain, unused 2D asset for inspection --
                // MaterialImporter deliberately never looks this id up (see its own comment), so
                // it is harmless that `result` still contains a Texture2D under this id; the
                // important invariant (never assign a 2D texture to a CUBE/3D-declared property)
                // is enforced on the MaterialImporter side by dimension, not by withholding the
                // asset here. Kept in `result` (rather than skipped) only for potential future
                // tooling/debugging convenience -- no material in this package will ever request it.
                if (dimension == "Tex3D" || dimension == "Tex2DArray")
                    skippedUnassignableCount++;

                importer.textureType = isNormalMap ? TextureImporterType.NormalMap : TextureImporterType.Default;
                importer.sRGBTexture = !isNormalMap;
                importer.alphaIsTransparency = !isNormalMap;
                importer.mipmapEnabled = true;
                importer.textureCompression = isCutoutMask
                    ? TextureImporterCompression.Uncompressed // O3 - no lossy alpha quantization for cutout masks
                    : TextureImporterCompression.Compressed;

                importer.SaveAndReimport();

                if ((bool?)metadata["generated_terrain_control"] == true)
                {
                    if (dimension != "Tex2D" || pkg.TextureColorSpace(texId) != "Linear" || isNormalMap)
                        throw new InvalidDataException("Invalid generated terrain control flags: " + texId);
                    importer.textureType = TextureImporterType.Default;
                    importer.sRGBTexture = false;
                    importer.alphaIsTransparency = false;
                    importer.mipmapEnabled = false;
                    importer.wrapMode = TextureWrapMode.Clamp;
                    importer.filterMode = FilterMode.Bilinear;
                    importer.textureCompression = TextureImporterCompression.Uncompressed;
                    importer.SaveAndReimport();
                }
                result[texId] = AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
            }
            AssetDatabase.SaveAssets();
            Debug.Log("[ArenaMapPort][TextureImportDriver] imported " + result.Count + " textures (" +
                      alphaCutoutTexIds.Count + " flagged alpha-cutout uncompressed, " + cubeCount +
                      " imported as real Cubemap assets, " + skippedUnassignableCount +
                      " Tex3D/Tex2DArray source(s) never assignable to any material property) -> " +
                      ArenaPaths.TexturesDir);
            return result;
        }

        private static Texture2DArray ImportTerrainArray(ArenaPackage pkg, string texId, JObject metadata)
        {
            int width = (int?)metadata["width"] ?? 0;
            int height = (int?)metadata["height"] ?? 0;
            int depth = (int?)metadata["depth"] ?? 0;
            string role = (string)metadata["terrain_role"];
            bool linear = role == "normalSAO";
            var slices = metadata["array_slices"] as JArray;
            if ((role != "diffuse" && !linear) || pkg.TextureDimension(texId) != "Tex2DArray" ||
                pkg.TextureIsNormalMap(texId) || pkg.TextureColorSpace(texId) != (linear ? "Linear" : "sRGB") ||
                width <= 0 || height <= 0 || depth != 12 || slices == null || slices.Count != depth ||
                (string)metadata["file"] != (string)slices[0])
                throw new InvalidDataException("Invalid generated terrain array metadata: " + texId);

            var array = new Texture2DArray(width, height, depth, TextureFormat.RGBA32, true, linear)
            {
                name = texId,
                wrapMode = TextureWrapMode.Repeat,
                filterMode = FilterMode.Trilinear,
                anisoLevel = 4,
            };
            var paths = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
            bool saved = false;
            try
            {
                for (int layer = 0; layer < depth; layer++)
                {
                    string path = Path.GetFullPath(pkg.ResolvePath((string)slices[layer]));
                    string textureRoot = Path.GetFullPath(pkg.ResolvePath("textures")).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
                    if (!path.StartsWith(textureRoot, System.StringComparison.OrdinalIgnoreCase) || !paths.Add(path))
                        throw new InvalidDataException("Terrain array slice path escaped or repeated: " + path);
                    byte[] bytes = File.ReadAllBytes(path);
                    // PNG IHDR: byte depth 8 and RGBA color type 6 are required. NormalSAO's
                    // four packed channels must never enter Unity's normal-map importer.
                    if (bytes.Length < 33 || bytes[0] != 137 || bytes[1] != 80 || bytes[2] != 78 || bytes[3] != 71 ||
                        bytes[24] != 8 || bytes[25] != 6)
                        throw new InvalidDataException("Terrain array slice is not an RGBA8 PNG: " + path);
                    var slice = new Texture2D(2, 2, TextureFormat.RGBA32, false, linear);
                    try
                    {
                        if (!ImageConversion.LoadImage(slice, bytes, false) || slice.width != width || slice.height != height)
                            throw new InvalidDataException("Terrain array slice dimensions/decode mismatch: " + path);
                        array.SetPixels32(slice.GetPixels32(), layer, 0);
                    }
                    finally { UnityEngine.Object.DestroyImmediate(slice); }
                }
                array.Apply(true, false);
                string assetPath = ArenaPaths.TexturesDir + "/" + texId + ".asset";
                var existing = AssetDatabase.LoadAssetAtPath<Texture2DArray>(assetPath);
                if (existing != null)
                {
                    EditorUtility.CopySerialized(array, existing);
                    UnityEngine.Object.DestroyImmediate(array);
                    array = existing;
                    EditorUtility.SetDirty(array);
                }
                else AssetDatabase.CreateAsset(array, assetPath);
                saved = true;
                Debug.Log("[ArenaMapPort][TextureImportDriver] terrain " + role + " array " + texId +
                          ": " + width + "x" + height + "x" + depth + " (" + (linear ? "Linear" : "sRGB") + ")");
                return array;
            }
            finally { if (!saved && array != null) UnityEngine.Object.DestroyImmediate(array); }
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
