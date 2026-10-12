using System;
using System.IO;

namespace ArenaMapPort.Editor.Import
{
    // Unity-independent policy so the packed-normal regression can run without launching Editor.
    public static class NormalTextureImportPolicy
    {
        public sealed class Usage
        {
            public int NormalSlots, ClothMaskSlots, UnsupportedSlots;
            public bool HasDxt1Keyword;
            public bool AllNormalReadersUseRgb = true;

            // AirPit (Arena 0.6.0) shader slots verified against the live source on 2026-10-12: the
            // ANGRYMESH rock shader's three normal slots carry ordinary packed DXT5 normals, and
            // Passenger_ladder_02_part_02 binds its packed _BumpMap texture a second time as _SpecMap on
            // the same SMap shader. That co-usage reads the same preserved bytes Arena reads, so it is
            // neither a normal reader nor an unsupported slot.
            const string RockShader = "ANGRYMESH/PBR Rocks/PBR BlendTopDetail (Legacy)";
            const string SpecMapShader = "p0/Reflective/Bumped Specular SMap";

            public void Add(string shader, string property, bool dxt1Keyword)
            {
                switch (property)
                {
                    case "_BumpMap":
                    case "_BumpMap0":
                    case "_BumpMap1":
                    case "_BumpMap2":
                    case "_NormalMap1":
                    case "_NormalMap2":
                    case "_BaseNormalMap" when shader == RockShader:
                    case "_TopNormalMap" when shader == RockShader:
                    case "_DetailNormalMap" when shader == RockShader:
                        NormalSlots++;
                        HasDxt1Keyword |= dxt1Keyword;
                        AllNormalReadersUseRgb &= dxt1Keyword ||
                            (shader == "Cloth/ClothShader" && (property == "_NormalMap1" || property == "_NormalMap2"));
                        break;
                    case "_NormalsMask" when shader == "Cloth/ClothShader":
                        ClothMaskSlots++;
                        break;
                    case "_SpecMap" when shader == SpecMapShader:
                        break; // co-usage of a normal texture; the normal slot decides the plan
                    default:
                        UnsupportedSlots++;
                        break;
                }
            }
        }

        public sealed class Pixels
        {
            public long Count { get; private set; }
            public int MinR { get; private set; } = 255;
            public int MaxR { get; private set; }
            public int MinB { get; private set; } = 255;
            public int MinA { get; private set; } = 255;
            public int MaxGreenBlueDifference { get; private set; }

            public void Add(byte r, byte g, byte b, byte a)
            {
                Count++;
                MinR = Math.Min(MinR, r);
                MaxR = Math.Max(MaxR, r);
                MinB = Math.Min(MinB, b);
                MinA = Math.Min(MinA, a);
                MaxGreenBlueDifference = Math.Max(MaxGreenBlueDifference, Math.Abs(g - b));
            }
        }

        public sealed class Plan
        {
            public string Encoding { get; }
            public string Compression { get; }
            public bool Srgb { get; }
            internal Plan(string encoding, string compression, bool srgb)
            {
                Encoding = encoding;
                Compression = compression;
                Srgb = srgb;
            }
        }

        public static Plan Classify(string textureId, string sourceFormat, string colorSpace, string dimension,
            int width, int height, Pixels pixels, Usage usage)
        {
            if (dimension != "Tex2D" || width <= 0 || height <= 0 || width > 16384 || height > 16384
                || pixels == null || pixels.Count != (long)width * height || usage == null
                || usage.UnsupportedSlots != 0 || (usage.NormalSlots == 0 && usage.ClothMaskSlots == 0)
                || (usage.NormalSlots != 0 && usage.ClothMaskSlots != 0))
                throw Rejected(textureId, "invalid dimensions/pixels or unsupported/mixed material usage");

            // The extractor's normal flag is a heuristic. Cloth's _NormalsMask is an authored
            // color-space mask, not a normal vector texture; never normal-repack this false positive.
            if (usage.ClothMaskSlots > 0 && sourceFormat == "DXT5" && colorSpace == "sRGB")
                return new Plan("ClothNormalsMask", "DXT5", true);
            // AirPit (Arena 0.6.0) authors the same Cloth _NormalsMask as an opaque DXT1 sRGB grey mask
            // (Lighthouse_tarp_02 / mask2: R = G = B radial falloff, alpha 255 everywhere, on the
            // tarpaulin_wall_01/03 renderers). Preserve it as DXT1 sRGB exactly like the DXT5 case;
            // the alpha and grey bounds keep a packed normal from slipping through this branch.
            if (usage.ClothMaskSlots > 0 && sourceFormat == "DXT1" && colorSpace == "sRGB"
                && pixels.MinA == 255 && pixels.MaxGreenBlueDifference <= 16)
                return new Plan("ClothNormalsMask", "DXT1", true);

            if (usage.NormalSlots > 0 && colorSpace == "Linear" && !usage.HasDxt1Keyword)
            {
                // Source TextureFormat 29 is DXT5Crunched; the older extractor labels it Format29.
                // PNG already holds its decoded top mip. Re-encode as ordinary DXT5, never repack.
                if ((sourceFormat == "DXT5" || sourceFormat == "DXT5Crunched" || sourceFormat == "Format29")
                    && pixels.MinR == 255 && pixels.MinA < 255)
                    return new Plan("PackedRaG", "DXT5", false);

                // Verified BC7 sources have the same packed channels with small compression error
                // in the nominally white R and duplicated G/B. Bounds deliberately reject other
                // layouts rather than guessing from a texture's name. Original RGBA is preserved.
                if (sourceFormat == "BC7" && pixels.MinR >= 239 && pixels.MaxR == 255
                    && pixels.MaxGreenBlueDifference <= 16 && pixels.MinA < 255)
                    return new Plan("PackedRaG", "BC7", false);
            }

            // A real RGB-normal route must consume RGB, not the default packed R*A/G route.
            // City_Tarp_01_N is this source-authored sRGB DXT1 case, on Cloth/_NormalMap1.
            if (usage.NormalSlots > 0 && usage.AllNormalReadersUseRgb && sourceFormat == "DXT1"
                && (colorSpace == "Linear" || colorSpace == "sRGB") && pixels.MinA == 255 && pixels.MinB >= 128)
                return new Plan("RgbNormal", "DXT1", colorSpace == "sRGB");

            throw Rejected(textureId, "unverified normal encoding: format=" + sourceFormat + ", colorSpace=" +
                colorSpace + ", R=" + pixels.MinR + ".." + pixels.MaxR + ", minA=" + pixels.MinA +
                ", maxGreenBlueDifference=" + pixels.MaxGreenBlueDifference + ", DXT1_NM=" + usage.HasDxt1Keyword);
        }

        static InvalidDataException Rejected(string textureId, string reason) =>
            new InvalidDataException("Normal texture " + textureId + " rejected: " + reason +
                ". Preserve/verify the source encoding before adding an import policy; no NormalMap fallback is allowed.");
    }
}
