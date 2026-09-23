// ArenaMapPort WP2 — §B.WP2.4 texture import (named "Driver" to avoid colliding with
// UnityEditor.TextureImporter). Copies ordinary WP1 PNGs into Assets/ArenaImport/Textures/.
// Source normals are ALREADY GPU-encoded: validate their source format/color-space and persist
// them as native Texture2D assets. A PNG importer can be changed by Unity's material postprocessor after
// this method returns, repacking R into A and corrupting packed DXT5/BC7 normals whose X is R*A.
// Native assets have no TextureImporter to repack. Ordinary color textures retain the
// diffuse/albedo import convention below.
//
// LEAD OVERRIDE O3: textures used as alpha-cutout masks must not be quantized lossy-wrong (use
// BC7/high quality or uncompressed where alpha matters). For source normals, preserve the known
// source compression and alpha rather than passing already-packed pixels through a normal packer.
// A texture is flagged alpha-cutout here if any material whose shader name contains
// "Cutout" (§B.WP1's own texture pool, arena-census.md §6's confirmed shader-name families)
// references it via any tex_env slot.
//
// impl-texture-dimensions §2: a texture's manifest "dimension" (§C-compatible extension, WP1
// side) now drives the SHAPE of the imported asset, never just its 2D import settings --
// "Cube" imports the vertical 6-frame strip WP1 emits as a real TextureImporterShape.TextureCube
// asset (TextureImporterGenerateCubemap.AutoCubemap auto-detects the 1:6 strip layout and builds
// an actual Cubemap object). Versioned native_volume payloads import as real Alpha8 Texture3D
// assets, preserving native bytes, mips, axes and samplers. Legacy unsupported Tex3D/Tex2DArray
// packages retain the safe fallback: inspection-only PNG, never assigned to a material slot.
//
// Return type is the base UnityEngine.Texture (not Texture2D) since a Cube-dimension entry
// resolves to a Cubemap asset, not a Texture2D one -- Material.SetTexture accepts either.
//
// Runs BEFORE §B.WP2.3 material import (execution-order note, SPEC.md) - MaterialImporter.SetTexture
// against a not-yet-imported texture is a silent no-op, not an error.
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
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
            var normalUsages = CollectNormalTextureUsages(pkg);

            var result = new Dictionary<string, Texture>();
            int cubeCount = 0, volumeCount = 0, skippedUnassignableCount = 0;
            int packedNormalCount = 0, rgbNormalCount = 0, clothMaskCount = 0;
            foreach (var prop in pkg.Textures.Properties())
            {
                string texId = prop.Name;
                var metadata = (JObject)prop.Value;
                if (metadata["native_volume"] != null)
                {
                    result[texId] = ImportNativeVolume(pkg, texId, metadata);
                    volumeCount++;
                    continue;
                }
                if ((bool?)metadata["generated_terrain_array"] == true)
                {
                    result[texId] = ImportTerrainArray(pkg, texId, metadata);
                    continue;
                }
                string srcPng = pkg.TexturePngPath(texId);
                bool isNormalMap = pkg.TextureIsNormalMap(texId);
                string dimension = pkg.TextureDimension(texId);
                if (isNormalMap)
                {
                    normalUsages.TryGetValue(texId, out var usage);
                    // Read ONLY the authoritative package PNG, never a previous imported asset.
                    // No channel conversion/derived PNG means repeated imports cannot double-decode.
                    var normalPlan = InspectNormalSource(srcPng, texId, metadata, dimension, usage);
                    result[texId] = ImportPreservedNormal(srcPng, texId, metadata, normalPlan);
                    if (normalPlan.Encoding == "PackedRaG") packedNormalCount++;
                    else if (normalPlan.Encoding == "RgbNormal") rgbNormalCount++;
                    else clothMaskCount++;
                    continue;
                }

                string assetPath = ArenaPaths.TexturesDir + "/" + texId + ".png";
                if (!File.Exists(assetPath) || !FilesIdentical(srcPng, assetPath))
                    File.Copy(srcPng, assetPath, true);

                AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport);

                var importer = (TextureImporter)AssetImporter.GetAtPath(assetPath);
                bool isCutoutMask = alphaCutoutTexIds.Contains(texId);

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

                importer.textureType = TextureImporterType.Default;
                importer.sRGBTexture = true;
                importer.alphaIsTransparency = true;
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
                      " real Cubemap assets, " + volumeCount + " real Texture3D assets, " + skippedUnassignableCount +
                      " Tex3D/Tex2DArray source(s) never assignable to any material property, " +
                      packedNormalCount + " packed normals / " + rgbNormalCount + " RGB normals / " +
                      clothMaskCount + " Cloth masks saved as native source-encoded Texture2D assets) -> " +
                      ArenaPaths.TexturesDir);
            return result;
        }

        private static int VolumeInt(JToken token, string name, int minimum, int maximum)
        {
            if (token == null || token.Type != JTokenType.Integer)
                throw new InvalidDataException("Missing/non-integer native volume " + name);
            long value = (long)token;
            if (value < minimum || value > maximum)
                throw new InvalidDataException("Out-of-range native volume " + name + ": " + value);
            return (int)value;
        }

        private static Texture3D ImportNativeVolume(ArenaPackage pkg, string texId, JObject metadata)
        {
            // Native Texture3D.m_Format uses GraphicsFormat. In particular 54 is internal
            // Alpha8, NOT TextureFormat.ASTC_4x4. A native Unity2022 probe verified the enum,
            // linear color space and sampled channels (1,1,1,A); dust samples .a only.
            var volume = metadata["native_volume"] as JObject;
            if (volume == null || pkg.TextureDimension(texId) != "Tex3D" || pkg.TextureIsNormalMap(texId)
                || pkg.TextureColorSpace(texId) != "Linear" || (string)volume["color_space"] != "Linear"
                || VolumeInt(volume["schema_version"], "schema_version", 1, 1) != 1
                || (string)volume["encoding"] != "Alpha8" || (string)volume["layout"] != "x-fastest-y-z-mips"
                || VolumeInt(volume["source_graphics_format"], "source_graphics_format", 54, 54) != 54)
                throw new InvalidDataException("Unsupported native volume metadata: " + texId);

            int width = VolumeInt(volume["width"], "width", 1, 2048);
            int height = VolumeInt(volume["height"], "height", 1, 2048);
            int depth = VolumeInt(volume["depth"], "depth", 1, 2048);
            int maxMips = 1;
            for (int size = Mathf.Max(width, Mathf.Max(height, depth)); size > 1; size >>= 1) maxMips++;
            int mipCount = VolumeInt(volume["mip_count"], "mip_count", 1, maxMips);
            if (VolumeInt(metadata["width"], "preview width", 1, 2048) != width
                || VolumeInt(metadata["height"], "preview height", 1, 2048 * 2048) != height * depth
                || VolumeInt(metadata["volume_source_height"], "source height", 1, 2048) != height
                || VolumeInt(metadata["volume_source_depth"], "source depth", 1, 2048) != depth)
                throw new InvalidDataException("Native volume source/preview dimensions disagree: " + texId);

            var mips = volume["mips"] as JArray;
            if (mips == null || mips.Count != mipCount)
                throw new InvalidDataException("Native volume mip table mismatch: " + texId);
            var offsets = new int[mipCount];
            int total = 0;
            for (int level = 0; level < mipCount; level++)
            {
                int w = Mathf.Max(1, width >> level), h = Mathf.Max(1, height >> level), d = Mathf.Max(1, depth >> level);
                long size = (long)w * h * d;
                // These tiny dust volumes need 2KiB. Bound resource use before reading an
                // accidental/hostile multi-gigabyte payload while allowing normal small volumes.
                if (size > 64 * 1024 * 1024 || total + size > 64 * 1024 * 1024)
                    throw new InvalidDataException("Native volume exceeds 64MiB payload limit: " + texId);
                var mip = mips[level] as JObject;
                if (mip == null || VolumeInt(mip["width"], "mip width", 1, 2048) != w
                    || VolumeInt(mip["height"], "mip height", 1, 2048) != h
                    || VolumeInt(mip["depth"], "mip depth", 1, 2048) != d
                    || VolumeInt(mip["offset"], "mip offset", 0, int.MaxValue) != total
                    || VolumeInt(mip["byte_length"], "mip byte length", 1, int.MaxValue) != size)
                    throw new InvalidDataException("Native volume mip layout mismatch: " + texId + "/" + level);
                offsets[level] = total;
                total += (int)size;
            }
            if (VolumeInt(volume["byte_length"], "byte_length", 1, int.MaxValue) != total)
                throw new InvalidDataException("Native volume byte length mismatch: " + texId);

            string sourceFile = (string)volume["source_file"];
            JToken sourceIdToken = volume["source_path_id"];
            if (sourceFile == null || !System.Text.RegularExpressions.Regex.IsMatch(sourceFile, "^[A-Za-z0-9_.-]+\\.assets$")
                || sourceIdToken == null || sourceIdToken.Type != JTokenType.Integer || (long)sourceIdToken <= 0)
                throw new InvalidDataException("Native volume source provenance missing/invalid: " + texId);
            string identity = sourceFile + ":" + ((long)sourceIdToken).ToString(System.Globalization.CultureInfo.InvariantCulture)
                + ":Texture2D"; // Existing texture-pool IDs share this historical discriminator.
            string identityHash;
            using (var hash = SHA1.Create())
                identityHash = System.BitConverter.ToString(hash.ComputeHash(System.Text.Encoding.UTF8.GetBytes(identity)))
                    .Replace("-", "").ToLowerInvariant().Substring(0, 16);
            if (identityHash != texId)
                throw new InvalidDataException("Native volume source provenance/texture ID mismatch: " + texId);

            string relative = (string)volume["file"];
            if (!System.Text.RegularExpressions.Regex.IsMatch(texId, "^[0-9a-f]{16}$")
                || relative != "textures/" + texId + ".volume.bin")
                throw new InvalidDataException("Native volume payload path is not its exact owned texture path: " + texId);
            string sourcePath = pkg.ResolvePath(relative);
            if (!File.Exists(sourcePath) || new FileInfo(sourcePath).Length != total)
                throw new InvalidDataException("Native volume payload missing or truncated: " + texId);
            byte[] data = File.ReadAllBytes(sourcePath);
            string digest;
            using (var hash = SHA256.Create())
                digest = System.BitConverter.ToString(hash.ComputeHash(data)).Replace("-", "").ToLowerInvariant();
            if (!string.Equals(digest, (string)volume["sha256"], System.StringComparison.Ordinal))
                throw new InvalidDataException("Native volume payload SHA256 mismatch: " + texId);

            var sampler = volume["sampler"] as JObject;
            if (sampler == null) throw new InvalidDataException("Missing native volume sampler: " + texId);
            var filter = (FilterMode)VolumeInt(sampler["filter_mode"], "filter_mode", 0, 2);
            int aniso = VolumeInt(sampler["aniso_level"], "aniso_level", 0, 16);
            var wrapU = (TextureWrapMode)VolumeInt(sampler["wrap_u"], "wrap_u", 0, 3);
            var wrapV = (TextureWrapMode)VolumeInt(sampler["wrap_v"], "wrap_v", 0, 3);
            var wrapW = (TextureWrapMode)VolumeInt(sampler["wrap_w"], "wrap_w", 0, 3);
            JToken biasToken = sampler["mip_bias"];
            if (biasToken == null || (biasToken.Type != JTokenType.Float && biasToken.Type != JTokenType.Integer))
                throw new InvalidDataException("Missing/non-numeric native volume mip bias: " + texId);
            double bias = (double)biasToken;
            if (double.IsNaN(bias) || double.IsInfinity(bias) || bias < -16 || bias > 16)
                throw new InvalidDataException("Invalid native volume mip bias: " + texId);

            string assetPath = ArenaPaths.TexturesDir + "/" + texId + ".asset";
            var existing = AssetDatabase.LoadMainAssetAtPath(assetPath);
            if ((existing != null && !(existing is Texture3D)) || (existing == null && File.Exists(assetPath)))
                throw new InvalidDataException("Native volume asset has incompatible/unreadable class: " + assetPath);
            var texture = new Texture3D(width, height, depth, TextureFormat.Alpha8, mipCount)
            {
                name = texId,
                filterMode = filter,
                anisoLevel = aniso,
                wrapModeU = wrapU,
                wrapModeV = wrapV,
                wrapModeW = wrapW,
                mipMapBias = (float)bias,
            };
            bool saved = false;
            try
            {
                for (int level = 0; level < mipCount; level++) texture.SetPixelData(data, level, offsets[level]);
                // No regenerated mips, alpha dilation, y/z flip, color conversion or compression.
                texture.Apply(false, false);
                if (texture.width != width || texture.height != height || texture.depth != depth
                    || texture.mipmapCount != mipCount || texture.format != TextureFormat.Alpha8
                    || (int)texture.graphicsFormat != 54)
                    throw new InvalidDataException("Unity changed native volume dimensions/mips/encoding: " + texId);
                if (existing != null)
                {
                    EditorUtility.CopySerialized(texture, existing);
                    EditorUtility.SetDirty(existing);
                    return (Texture3D)existing;
                }
                AssetDatabase.CreateAsset(texture, assetPath);
                saved = true;
                return texture;
            }
            finally { if (!saved) UnityEngine.Object.DestroyImmediate(texture); }
        }

        private static NormalTextureImportPolicy.Plan InspectNormalSource(string path, string texId, JObject metadata,
            string dimension, NormalTextureImportPolicy.Usage usage)
        {
            byte[] bytes = File.ReadAllBytes(path);
            // The extractor writes RGBA8 PNGs. A lost alpha channel must be rejected, not invented.
            if (bytes.Length < 33 || bytes[0] != 137 || bytes[1] != 80 || bytes[2] != 78 || bytes[3] != 71
                || bytes[24] != 8 || bytes[25] != 6)
                throw new InvalidDataException("Normal source is not an RGBA8 PNG: " + texId);
            var source = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
            try
            {
                int width = (int?)metadata["width"] ?? 0, height = (int?)metadata["height"] ?? 0;
                if (!ImageConversion.LoadImage(source, bytes, false) || source.width != width || source.height != height)
                    throw new InvalidDataException("Normal source dimensions/decode mismatch: " + texId);
                var pixels = new NormalTextureImportPolicy.Pixels();
                foreach (var pixel in source.GetPixels32()) pixels.Add(pixel.r, pixel.g, pixel.b, pixel.a);
                return NormalTextureImportPolicy.Classify(texId, (string)metadata["format_src"],
                    (string)metadata["color_space"], dimension, width, height, pixels, usage);
            }
            finally { UnityEngine.Object.DestroyImmediate(source); }
        }

        private static Texture2D ImportPreservedNormal(string sourcePng, string texId, JObject metadata,
            NormalTextureImportPolicy.Plan plan)
        {
            int width = (int)metadata["width"], height = (int)metadata["height"];
            TextureFormat format;
            switch (plan.Compression)
            {
                case "DXT5": format = TextureFormat.DXT5; break;
                case "BC7": format = TextureFormat.BC7; break;
                case "DXT1": format = TextureFormat.DXT1; break;
                default: throw new InvalidDataException("Unsupported preserved normal compression: " + plan.Compression);
            }
            string assetPath = ArenaPaths.TexturesDir + "/" + texId + ".asset";
            var existing = AssetDatabase.LoadMainAssetAtPath(assetPath);
            if ((existing != null && !(existing is Texture2D)) || (existing == null && File.Exists(assetPath)))
                throw new InvalidDataException("Preserved normal asset has an incompatible or unreadable class: " + assetPath);

            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, true, !plan.Srgb)
            {
                name = texId,
                // Match the existing normal PNG metadata: Repeat, Bilinear, anisotropy 1, bias 0.
                // Terrain arrays deliberately have different filtering and retain their own path.
                wrapMode = TextureWrapMode.Repeat,
                filterMode = FilterMode.Bilinear,
                anisoLevel = 1,
                mipMapBias = 0,
            };
            bool saved = false;
            try
            {
                bool decoded = ImageConversion.LoadImage(texture, File.ReadAllBytes(sourcePng), false);
                // LoadImage may change RGBA32 to ARGB32 for PNGs. Both retain the same logical
                // eight-bit RGBA channels; their native byte layout does not require a swizzle.
                bool rgba8 = texture.format == TextureFormat.RGBA32 || texture.format == TextureFormat.ARGB32;
                if (!decoded || texture.width != width || texture.height != height || !rgba8)
                    throw new InvalidDataException("Native normal dimensions/decode mismatch: " + texId +
                        " (decoded=" + decoded + ", actual=" + texture.width + "x" + texture.height +
                        " " + texture.format + ", expected=" + width + "x" + height + " RGBA32/ARGB32)");

                // Generate the complete chain before compression, without normal conversion or
                // alpha dilation. Keep CPU data readable: discarding it before native serialization
                // has not been verified safe. Compression keeps this copy in the source-sized format.
                texture.Apply(true, false);
                EditorUtility.CompressTexture(texture, format, TextureCompressionQuality.Best);
                int mipCount = 1;
                for (int size = Mathf.Max(width, height); size > 1; size >>= 1) mipCount++;
                if (texture.format != format || texture.width != width || texture.height != height
                    || texture.mipmapCount != mipCount)
                    throw new InvalidDataException("Native normal compression/dimensions/mip chain mismatch: " + texId);

                if (existing != null)
                {
                    // Keep the GUID and references on a repeated import; never replace asset class.
                    EditorUtility.CopySerialized(texture, existing);
                    EditorUtility.SetDirty(existing);
                    return (Texture2D)existing;
                }
                AssetDatabase.CreateAsset(texture, assetPath);
                saved = true;
                return texture;
            }
            finally { if (!saved) UnityEngine.Object.DestroyImmediate(texture); }
        }

        private static Dictionary<string, NormalTextureImportPolicy.Usage> CollectNormalTextureUsages(ArenaPackage pkg)
        {
            var result = new Dictionary<string, NormalTextureImportPolicy.Usage>();
            foreach (var material in pkg.Materials.Properties())
            {
                var json = pkg.LoadMaterialJson(material.Name);
                var texEnvs = json["tex_envs"] as JObject;
                if (texEnvs == null) continue;
                bool dxt1Keyword = false;
                if (json["keywords"] is JArray keywords)
                    foreach (var keyword in keywords) if ((string)keyword == "DXT1_NM") dxt1Keyword = true;
                foreach (var slot in texEnvs.Properties())
                {
                    var entry = slot.Value as JObject;
                    string id = entry == null ? null : (string)entry["texture_id"];
                    if (id == null || pkg.Textures[id] == null || !pkg.TextureIsNormalMap(id)) continue;
                    if (!result.TryGetValue(id, out var usage)) result.Add(id, usage = new NormalTextureImportPolicy.Usage());
                    usage.Add(pkg.MaterialShaderName(material.Name), slot.Name, dxt1Keyword);
                }
            }
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
