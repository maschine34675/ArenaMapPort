#nullable disable
using System;

namespace ArenaMapPort.Client
{
    // The Unity-free VALUES of the native grass probe in ONE place (PLAN-YARD item 23, lead decision 2026-11-21): the summer colour lists and the rule that no season of the game reaches the grass,
    // and every number the glue sets on the shader's material, the prototype, the LOD group, the managers and the two meshes. Each block names its source so a reviewer can check it against the dumps
    // under work/tmp/yard-grass/: "G2" = reports/G2-arena-data.md (Arena's own serialized values, decoded with UnityPy and by hand from the raw MonoBehaviours, 2026-11-21; exact float32 values
    // printed by work/tmp/yard-grass/core/exact_values.py into core/exact_values.txt), "G1" = reports/G1-runtime.md (what EFT's GPUInstancer reads, from the decompile
    // D:/SPT41/Development/_decompiled/GPUInstancer/). Numbers and names only: no texture, mesh or audio bytes, no hash.

    /// <summary>A colour list pair (dry, healthy), five RGBA entries each as 20 floats: entry i is the colour of texture slice i.</summary>
    internal sealed class GrassColorLists
    {
        internal GrassColorLists(float[] dry, float[] healthy)
        {
            Dry = dry;
            Healthy = healthy;
        }

        /// <summary>20 floats: r, g, b, a of entries 0..4.</summary>
        internal float[] Dry { get; }
        internal float[] Healthy { get; }
        internal int Count => Dry.Length / 4;
    }

    /// <summary>
    /// The colours of the grass and the season rule. Yard is a summer map (user, 2026-11-08 "sommer reicht"), and the host runs whatever season the server reports (AutumnLate on 2026-11-21): EFT's own
    /// GrassInitialization.SetSeasonsMaterials would pick the SeasonsColors list for Autumn, Spring and AutumnLate and the WinterColors list (white) for Winter and SpringEarly, and throws on anything else
    /// (Storm). The port never calls it and never forwards a season: the grass takes Arena's Summer lists (DryColor, HealthyColor) always, whatever <see cref="ForSeason"/> is asked.
    /// </summary>
    internal static class NativeGrassColorModel
    {
        /// <summary>Entries per list: one per texture slice (G2 section 1: five; EFT's own Streets lists have two, which would index out of range for slices 2 to 4).</summary>
        internal const int ListLength = 5;

        /// <summary>
        /// DryColor of Yard's GrassInitialization (G2 section 1; exact float32 in core/exact_values.txt), RGBA per entry, alpha 0. This is also EFT's Summer branch of SetSeasonsMaterials.
        /// </summary>
        static readonly float[] SummerDry =
        {
            1f, 0.94117653f, 0.078431375f, 0f,
            0.47450984f, 0.56078434f, 0.35686275f, 0f,
            0.6509434f, 0.63684934f, 0.43600926f, 0f,
            0.71330523f, 0.7735849f, 0.4743681f, 0f,
            1f, 0.6683485f, 0f, 0f,
        };

        /// <summary>HealthyColor of Yard's GrassInitialization (G2 section 1), RGBA per entry, alpha 0.</summary>
        static readonly float[] SummerHealthy =
        {
            1f, 0.6156863f, 0.20000002f, 0f,
            1f, 1f, 1f, 0f,
            0.990566f, 0.91738427f, 0.6541474f, 0f,
            0.6509434f, 0.6153164f, 0.53426486f, 0f,
            0.4811321f, 0.45832464f, 0.39489144f, 0f,
        };

        static readonly GrassColorLists Summer = new GrassColorLists(SummerDry, SummerHealthy);

        /// <summary>The lists for ANY season value of the game (ESeason 0..5, ESeasonStatus 0..6 incl. Storm, or any other number): always the summer lists, the same instance.</summary>
        internal static GrassColorLists ForSeason(int seasonValue) => Summer;

        /// <summary>The lists for any season NAME (Summer, Autumn, Winter, Spring, AutumnLate, SpringEarly, Storm, null, text): always the summer lists.</summary>
        internal static GrassColorLists ForSeasonName(string seasonName) => Summer;

        /// <summary>The summer lists (what the build feeds).</summary>
        internal static GrassColorLists SummerLists => Summer;
    }

    /// <summary>A named material float.</summary>
    internal readonly struct GrassFloat
    {
        internal GrassFloat(string name, float value)
        {
            Name = name;
            Value = value;
        }

        internal string Name { get; }
        internal float Value { get; }
    }

    /// <summary>A named material colour or vector (r, g, b, a / x, y, z, w).</summary>
    internal readonly struct GrassVector
    {
        internal GrassVector(string name, float x, float y, float z, float w)
        {
            Name = name;
            X = x;
            Y = y;
            Z = z;
            W = w;
        }

        internal string Name { get; }
        internal float X { get; }
        internal float Y { get; }
        internal float Z { get; }
        internal float W { get; }
    }

    /// <summary>One mesh as plain arrays: 3 floats per vertex position and normal, 4 per tangent, 2 per uv, 3 indices per triangle.</summary>
    internal sealed class GrassMeshData
    {
        internal float[] Positions;
        internal float[] Normals;
        internal float[] Tangents;
        internal float[] Uvs;
        internal int[] Indices;
        internal int VertexCount => Positions.Length / 3;
    }

    /// <summary>
    /// Every value the glue needs, with its source. Nothing here is read from a file at run time; the numbers are Arena's authored settings (identical in EFT's own Streets grass: G2 section 9).
    /// </summary>
    internal static class NativeGrassValues
    {
        // ---- names and EFT's side (G1 sections 1, 2, 4; decompile) ------------------------------------------------------------------------------------------------------------------

        /// <summary>EFT's real shader, resolved through GPUInstancerSettings.shaderBindings.GetInstancedShader first, Shader.Find as the fallback (design-final.md section 2 step 1). The material
        /// must carry THIS shader when a manager wakes up: the manager clones every prefab material by shader NAME (GPUInstancerShaderBindings.GetInstancedMaterial) and an unknown name becomes a
        /// Standard clone (G1 section 0 point 2).</summary>
        internal const string ShaderName = "GPUInstancer/Foliage_Simple";

        /// <summary>The noise texture of _HealthyDryNoiseTexture: Resources container entry "Textures/Noise/Fractal_Simplex_grayscale" (design-final.md step 1; EFT resources.assets 662, byte-identical to
        /// Arena's 1454, G2 section 9). It is used by the shader (G1 section 4). The fallback is procedural grayscale noise.</summary>
        internal const string NoiseResourcePath = "Textures/Noise/Fractal_Simplex_grayscale";

        /// <summary>The wave normal (EFT resources.assets 832). The slot is dead for DX11 in 0.16.9.5 (G1 section 4: no identifier in the compiled programs), so it may stay null; the path is the neighbour of the
        /// noise texture's; it exists in EFT's Resources container (measured offline by a reviewer, 2026-11-21: resources.assets 832).</summary>
        internal const string WaveNormalResourcePath = "Textures/Noise/Fractal_Simplex_normal";

        internal const string TexArraySlot = "_MainTexArray";
        internal const string NormalArraySlot = "_NormalMapArray";
        internal const string NormalTextureSlot = "_NormalMap";
        internal const string NoiseSlot = "_HealthyDryNoiseTexture";
        internal const string WaveNormalSlot = "_WindWaveNormalTexture";

        /// <summary>Layer 31 "Grass": the prefab, its LOD children and the holder. The draw passes the renderer's layer to DrawMeshInstancedIndirect (G1 section 4, last bullet).</summary>
        internal const int Layer = 31;

        /// <summary>The holder GameObject, created inactive in the map's Grass scene. No name part collides with the port's name sweeps (floor, ground, indoor, puddle).</summary>
        internal const string HolderName = "ArenaMapPort_NativeGrass";
        internal const string PrefabRootName = "ArenaMapPort_NativeGrassPrefab";
        internal const string Lod0Name = "lod0";
        internal const string Lod1Name = "lod1";
        internal const string Lod0MaterialName = "ArenaMapPort_Foliage_Simple";
        internal const string Lod1MaterialName = "ArenaMapPort_Foliage_Simple_Billboard";
        internal const string TexArrayObjectName = "ArenaMapPort_GrassTexArray";
        internal const string NormalArrayObjectName = "ArenaMapPort_GrassNormalArray";
        internal const string NormalTextureObjectName = "ArenaMapPort_Grass_new_1_N";

        /// <summary>The variation buffer name GrassInitialization.Awake feeds the slice index to (GrassInitialization.cs INDEX_BUFFER_NAME).</summary>
        internal const string IndexBufferName = "_ArrayIndexBuffer";

        /// <summary>
        /// The four camera command buffers a manager adds (GPUInstancerManager.cs lines 409, 413, 442, 446). Design-final.md section 3: after both managers are active the build removes every command
        /// buffer of these names from the cameras that is not the manager's current one, calls InitializeCameraData() on both managers, and teardown removes the same set.
        /// </summary>
        internal static readonly string[] CommandBufferNames =
        {
            "MotionVectorsPASS",
            "MotionVectorsAfterPASS",
            "MotionVectorsOpticPASS",
            "MotionVectorsAfterOpticPASS",
        };

        /// <summary>The component a manager adds to its camera and that teardown removes only when the build added it (G1 section 1.4).</summary>
        internal const string HiZComponentTypeName = "GPUInstancerHiZOcclusionGenerator";

        // ---- the managers (G2 section 1 table; G1 section 2 "The manager's own serialized state") ------------------------------------------------------------------------------------

        /// <summary>Arena's serialized manager: frustum and occlusion culling on, maxThreads 3, layerMask -1, IsActive true, minCullingDistance 0 (the instancing bounds are replaced at run time).</summary>
        internal const bool ManagerFrustumCulling = true;
        internal const bool ManagerOcclusionCulling = true;
        internal const int ManagerMaxThreads = 3;
        internal const int ManagerLayerMask = -1;
        internal const float ManagerMinCullingDistance = 0f;
        /// <summary>The first manager is the normal one (IsOptic false), the second the optic one (IsOptic true): the order of Arena's Manager object.</summary>
        internal const int ManagerCount = 2;

        // ---- the prototype (G2 section 3, exact float32 in core/exact_values.txt; identical to EFT's Streets prototype but for the script id and the prefab pointer) ---------------

        internal const bool ProtoIsShadowCasting = true;
        internal const bool ProtoUseCustomShadowDistance = true;
        internal const float ProtoShadowDistance = 25f;
        /// <summary>useOriginalShaderForShadow MUST be true whenever isShadowCasting is: otherwise the manager does new Material(Shader.Find("Hidden/GPUInstancer/ShadowsOnly")), a shader EFT does not ship (G1 section 1.1).</summary>
        internal const bool ProtoUseOriginalShaderForShadow = true;
        internal const bool ProtoCullShadows = true;
        internal const float ProtoMinDistance = 0f;
        /// <summary>The grass is culled at 150 m for the normal camera (a freshly generated prototype would say 500).</summary>
        internal const float ProtoMaxDistance = 150f;
        internal const float ProtoMinDistanceOptic = 0f;
        internal const float ProtoMaxDistanceOptic = 500f;
        internal const bool ProtoIsFrustumCulling = true;
        internal const bool ProtoIsOcclusionCulling = true;
        internal const float ProtoMinCullingDistance = 0f;
        internal const float ProtoOcclusionOffset = 0f;
        internal const int ProtoOcclusionAccuracy = 1;
        internal const bool ProtoIsLodCrossFade = false;
        internal const bool ProtoIsLodCrossFadeAnimate = false;
        internal const float ProtoLodFadeTransitionWidth = 0.1f;
        internal const float ProtoLodBiasAdjustment = 1f;
        internal const bool ProtoCheckedForBillboardExtensions = true;
        internal const bool ProtoUseGeneratedBillboard = false;
        internal const bool ProtoIsBillboardDisabled = false;
        internal const bool ProtoAutoUpdateTransformData = false;
        /// <summary>GPUInstancerTreeType.None.</summary>
        internal const int ProtoTreeType = 0;
        /// <summary>false as serialized; GrassInitialization sets it true before the feed and InitializeWithMatrix4x4Array sets it back to false (G1 section 1.2).</summary>
        internal const bool ProtoEnableRuntimeModifications = false;

        /// <summary>shadowLODMap, 16 floats (note index 4 = 9 where the class default is 1; EFT's own copy has the 9 too, G2 section 3).</summary>
        internal static readonly float[] ProtoShadowLodMap = { 0f, 4f, 0f, 0f, 9f, 5f, 0f, 0f, 2f, 6f, 0f, 0f, 3f, 7f, 0f, 0f };

        // ---- the LOD group of the prefab root (G2 section 2) ------------------------------------------------------------------------------------------------------------------------

        /// <summary>Two LODs: lod0 (the two crossed quads) from 0.17512812, lod1 (one quad, billboard material) from 0.0020024395; fade transition width 0 for both; the culled-below height is the last LOD's.</summary>
        internal const float Lod0ScreenHeight = 0.17512812f;
        internal const float Lod1ScreenHeight = 0.0020024395f;
        internal const float LodFadeTransitionWidth = 0f;
        /// <summary>LODGroup.size as serialized (it follows the bounds when recalculated).</summary>
        internal const float LodGroupSize = 2.0000002f;
        /// <summary>LODFadeMode.None.</summary>
        internal const int LodFadeMode = 0;
        internal const bool LodAnimateCrossFading = true;
        /// <summary>The renderers cast and receive shadows, motion vectors per object, light probes blended (G2 section 2); the instanced draw ignores most of it but the prototype's shadow flags read castShadows.</summary>
        internal const bool RendererCastShadows = true;
        internal const bool RendererReceiveShadows = true;

        // ---- the material (G2 section 4; both materials, exact float32 in core/exact_values.txt) ------------------------------------------------------------------------------------

        /// <summary>The one keyword that is declared by the shader and enabled on Arena's materials (GPUInstancer's AddRenderer switches it off on its clone, G1 section 1.1).</summary>
        internal const string EnabledKeyword = "AditionClip_ON";

        /// <summary>The floats common to Foliage_Simple (lod0) and Foliage_Simple_Billboard (lod1). `_IsBillboard` is the only difference, see <see cref="Lod0IsBillboard"/>.</summary>
        internal static readonly GrassFloat[] MaterialFloats =
        {
            new GrassFloat("AditionClip", 1f),
            new GrassFloat("IsBillboard", 0f),
            new GrassFloat("_AmbientOcclusion", 0.045f),
            new GrassFloat("_ArrayIndex", 0f),
            new GrassFloat("_BillboardFaceCamPos", 0f),
            new GrassFloat("_Cutoff", 0.33f),
            new GrassFloat("_FlipUV", 1f),
            new GrassFloat("_NoiseSpread", 19.5f),
            new GrassFloat("_TaaBias", -1.5f),
            new GrassFloat("_WindIdleSway", 0.3f),
            new GrassFloat("_WindWaveSway", 0.15f),
            new GrassFloat("_WindWaveTint", 0.5f),
            new GrassFloat("_WindWavesOn", 1f),
            new GrassFloat("_ZTest", 2f),
            new GrassFloat("_ZWrite", 1f),
            new GrassFloat("__dirty", 1f),
        };

        internal const string IsBillboardName = "_IsBillboard";
        internal const float Lod0IsBillboard = 0f;
        internal const float Lod1IsBillboard = 1f;

        /// <summary>The colours of both materials (RGBA / xyzw).</summary>
        internal static readonly GrassVector[] MaterialVectors =
        {
            new GrassVector("_CutoffFade", 40f, 45f, 45f, 50f),
            new GrassVector("_DryColor", 0.47390676f, 0.50735295f, 0.32082614f, 1f),
            new GrassVector("_HealthyColor", 1f, 1f, 1f, 0f),
            new GrassVector("_WaveAndDistance", 12f, 3.6f, 1f, 1f),
            new GrassVector("_WindWaveTintColor", 0.499f, 0.48356444f, 0.27518383f, 0f),
        };

        /// <summary>Texture scale and offset of every slot: (1, 1) and (0, 0) (G2 section 4).</summary>
        internal const float TextureScale = 1f;
        internal const float TextureOffset = 0f;

        // ---- the meshes (G2 section 6): two crossed unit quads (lod0) and one quad (lod1), generated by the rule, compared with Arena's decoded vertices offline --------------------

        /// <summary>
        /// lod0: two quads crossed at 90 degrees about the vertical axis, x or z in -0.5..0.5, y 0..1, uv 0..1; lod1: the first quad alone. Vertex order, normals, tangents, uvs and indices follow Arena's
        /// meshes (equal within 4e-7 in positions and uvs, float noise in Arena's authored vertices, and exactly equal in normals, tangents and indices: compared offline against the UnityPy decode of sharedassets95.assets paths 5 and 6, work/tmp/yard-grass/core/mesh_proof.py): quad A at z = 0 with normal (0, 0, 1) and
        /// tangent (-1, 0, 0, -1); quad B at x = 0 with normal (-1, 0, 0) and tangent (0, 0, -1, -1); per quad the vertices (+0.5, 1), (+0.5, 0), (-0.5, 1), (-0.5, 0) along the quad's axis with
        /// u = 0.5 - axis and v = y; triangles (0, 2, 3) and (0, 3, 1) of each quad. Bounds, as Unity recalculates them: centre (0, 0.5, 0), extents (0.5, 0.5, 0.5) for lod0 (z extent 0 for lod1).
        /// </summary>
        internal static GrassMeshData BuildMesh(bool crossed)
        {
            int quads = crossed ? 2 : 1;
            var mesh = new GrassMeshData
            {
                Positions = new float[quads * 4 * 3],
                Normals = new float[quads * 4 * 3],
                Tangents = new float[quads * 4 * 4],
                Uvs = new float[quads * 4 * 2],
                Indices = new int[quads * 6],
            };
            float[] axis = { 0.5f, 0.5f, -0.5f, -0.5f };
            float[] height = { 1f, 0f, 1f, 0f };
            for (int q = 0; q < quads; q++)
            {
                for (int k = 0; k < 4; k++)
                {
                    int v = q * 4 + k;
                    // quad A runs along x (z = 0), quad B along z (x = 0)
                    mesh.Positions[v * 3 + 0] = q == 0 ? axis[k] : 0f;
                    mesh.Positions[v * 3 + 1] = height[k];
                    mesh.Positions[v * 3 + 2] = q == 0 ? 0f : axis[k];
                    mesh.Normals[v * 3 + 0] = q == 0 ? 0f : -1f;
                    mesh.Normals[v * 3 + 1] = 0f;
                    mesh.Normals[v * 3 + 2] = q == 0 ? 1f : 0f;
                    mesh.Tangents[v * 4 + 0] = q == 0 ? -1f : 0f;
                    mesh.Tangents[v * 4 + 1] = 0f;
                    mesh.Tangents[v * 4 + 2] = q == 0 ? 0f : -1f;
                    mesh.Tangents[v * 4 + 3] = -1f;
                    mesh.Uvs[v * 2 + 0] = 0.5f - axis[k];
                    mesh.Uvs[v * 2 + 1] = height[k];
                }
                int b = q * 4;
                int i = q * 6;
                mesh.Indices[i + 0] = b + 0;
                mesh.Indices[i + 1] = b + 2;
                mesh.Indices[i + 2] = b + 3;
                mesh.Indices[i + 3] = b + 0;
                mesh.Indices[i + 4] = b + 3;
                mesh.Indices[i + 5] = b + 1;
            }
            return mesh;
        }
    }
}
