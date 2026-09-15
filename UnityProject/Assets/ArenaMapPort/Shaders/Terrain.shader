// Arena ground reconstructed as meshes. Original implementation of the observed
// source material recipe: control top-four, tiled layer UVs, height blending,
// gradient sampling and packed NormalSAO. See the terrain-materials report.
Shader "ArenaMapPort/Terrain"
{
    Properties
    {
        _DiffuseArray ("Diffuse and height layers", 2DArray) = "" {}
        _NormalSAOArray ("Packed normal smoothness AO layers", 2DArray) = "" {}
        _Control0 ("Layer weights 0-3", 2D) = "black" {}
        _Control1 ("Layer weights 4-7", 2D) = "black" {}
        _Control2 ("Layer weights 8-11", 2D) = "black" {}
        _Contrast ("Height blend contrast", Float) = 0.492
        _LayerCount ("Layer count", Float) = 12
        _ControlResolution ("Control resolution", Float) = 1024
        _UVScale ("Source base UV scale and offset", Vector) = (1,1,0,0)
        _LayerUV0 ("Layer 0 UV", Vector) = (1,1,0,0)
        _LayerUV1 ("Layer 1 UV", Vector) = (1,1,0,0)
        _LayerUV2 ("Layer 2 UV", Vector) = (1,1,0,0)
        _LayerUV3 ("Layer 3 UV", Vector) = (1,1,0,0)
        _LayerUV4 ("Layer 4 UV", Vector) = (1,1,0,0)
        _LayerUV5 ("Layer 5 UV", Vector) = (1,1,0,0)
        _LayerUV6 ("Layer 6 UV", Vector) = (1,1,0,0)
        _LayerUV7 ("Layer 7 UV", Vector) = (1,1,0,0)
        _LayerUV8 ("Layer 8 UV", Vector) = (1,1,0,0)
        _LayerUV9 ("Layer 9 UV", Vector) = (1,1,0,0)
        _LayerUV10 ("Layer 10 UV", Vector) = (1,1,0,0)
        _LayerUV11 ("Layer 11 UV", Vector) = (1,1,0,0)
        _LayerAdjust0 ("Layer 0 adjustment", Vector) = (0,1,0,0)
        _LayerAdjust1 ("Layer 1 adjustment", Vector) = (0,1,0,0)
        _LayerAdjust2 ("Layer 2 adjustment", Vector) = (0,1,0,0)
        _LayerAdjust3 ("Layer 3 adjustment", Vector) = (0,1,0,0)
        _LayerAdjust4 ("Layer 4 adjustment", Vector) = (0,1,0,0)
        _LayerAdjust5 ("Layer 5 adjustment", Vector) = (0,1,0,0)
        _LayerAdjust6 ("Layer 6 adjustment", Vector) = (0,1,0,0)
        _LayerAdjust7 ("Layer 7 adjustment", Vector) = (0,1,0,0)
        _LayerAdjust8 ("Layer 8 adjustment", Vector) = (0,1,0,0)
        _LayerAdjust9 ("Layer 9 adjustment", Vector) = (0,1,0,0)
        _LayerAdjust10 ("Layer 10 adjustment", Vector) = (0,1,0,0)
        _LayerAdjust11 ("Layer 11 adjustment", Vector) = (0,1,0,0)
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry-100" }
        LOD 300
        CGPROGRAM
        #pragma target 3.5
        #pragma only_renderers d3d11
        #pragma require 2darray
        #pragma surface surf Standard fullforwardshadows addshadow vertex:vert
        #include "UnityCG.cginc"

        UNITY_DECLARE_TEX2DARRAY(_DiffuseArray);
        UNITY_DECLARE_TEX2DARRAY(_NormalSAOArray);
        // Unity's first surface-analysis pass uses Mojoshader, which only parses
        // DX9 syntax (HLSLSupport.cginc). Actual D3D11 compilation keeps SampleGrad.
        #if defined(SHADER_TARGET_SURFACE_ANALYSIS_MOJOSHADER)
            #define ARENA_SAMPLE_ARRAY_GRAD(tex,coord,dx,dy) UNITY_SAMPLE_TEX2DARRAY(tex,coord)
        #else
            #define ARENA_SAMPLE_ARRAY_GRAD(tex,coord,dx,dy) tex.SampleGrad(sampler##tex,coord,dx,dy)
        #endif
        sampler2D _Control0, _Control1, _Control2;
        float _Contrast, _LayerCount, _ControlResolution;
        float4 _UVScale;
        float4 _LayerUV0, _LayerUV1, _LayerUV2, _LayerUV3, _LayerUV4, _LayerUV5;
        float4 _LayerUV6, _LayerUV7, _LayerUV8, _LayerUV9, _LayerUV10, _LayerUV11;
        float4 _LayerAdjust0, _LayerAdjust1, _LayerAdjust2, _LayerAdjust3, _LayerAdjust4, _LayerAdjust5;
        float4 _LayerAdjust6, _LayerAdjust7, _LayerAdjust8, _LayerAdjust9, _LayerAdjust10, _LayerAdjust11;

        struct Input { float2 terrainUV; };
        void vert(inout appdata_full vertex, out Input output)
        {
            UNITY_INITIALIZE_OUTPUT(Input, output);
            output.terrainUV = vertex.texcoord.xy;
        }

        float4 LayerUV(int index)
        {
            if (index == 0) return _LayerUV0;
            if (index == 1) return _LayerUV1;
            if (index == 2) return _LayerUV2;
            if (index == 3) return _LayerUV3;
            if (index == 4) return _LayerUV4;
            if (index == 5) return _LayerUV5;
            if (index == 6) return _LayerUV6;
            if (index == 7) return _LayerUV7;
            if (index == 8) return _LayerUV8;
            if (index == 9) return _LayerUV9;
            if (index == 10) return _LayerUV10;
            return _LayerUV11;
        }

        float4 LayerAdjust(int index)
        {
            if (index == 0) return _LayerAdjust0;
            if (index == 1) return _LayerAdjust1;
            if (index == 2) return _LayerAdjust2;
            if (index == 3) return _LayerAdjust3;
            if (index == 4) return _LayerAdjust4;
            if (index == 5) return _LayerAdjust5;
            if (index == 6) return _LayerAdjust6;
            if (index == 7) return _LayerAdjust7;
            if (index == 8) return _LayerAdjust8;
            if (index == 9) return _LayerAdjust9;
            if (index == 10) return _LayerAdjust10;
            return _LayerAdjust11;
        }

        void surf(Input input, inout SurfaceOutputStandard output)
        {
            float2 controlUV = (input.terrainUV * (_ControlResolution - 1.0) + 0.5) / _ControlResolution;
            float4 control0 = tex2D(_Control0, controlUV);
            float4 control1 = tex2D(_Control1, controlUV);
            float4 control2 = tex2D(_Control2, controlUV);
            float controls[12] = { control0.x, control0.y, control0.z, control0.w,
                                   control1.x, control1.y, control1.z, control1.w,
                                   control2.x, control2.y, control2.z, control2.w };
            float4 weights = 0;
            int4 indices = 0;
            [unroll] for (int layer = 0; layer < 12; layer++)
            {
                float weight = layer < _LayerCount ? controls[layer] : -1.0;
                // The source comparison is >=: the later layer wins an exact tie.
                if (weight >= weights.x) { weights = float4(weight, weights.xyz); indices = int4(layer, indices.xyz); }
                else if (weight >= weights.y) { weights = float4(weights.x, weight, weights.yz); indices = int4(indices.x, layer, indices.yz); }
                else if (weight >= weights.z) { weights = float4(weights.xy, weight, weights.z); indices = int4(indices.xy, layer, indices.z); }
                else if (weight >= weights.w) { weights.w = weight; indices.w = layer; }
            }
            weights *= rsqrt(max(dot(weights, weights), 1e-12));
            float2 baseUV = input.terrainUV * _UVScale.xy + _UVScale.zw;
            float2 baseDx = ddx(baseUV), baseDy = ddy(baseUV);
            float4 layerUV[4];
            float2 uv[4];
            float2 diffuseGradientScale = 0;
            [unroll] for (int i = 0; i < 4; i++)
            {
                layerUV[i] = LayerUV(indices[i]);
                uv[i] = baseUV * layerUV[i].xy + layerUV[i].zw;
                diffuseGradientScale += layerUV[i].xy * weights[i];
            }
            float4 diffuse[4];
            float4 heights;
            [unroll] for (int d = 0; d < 4; d++)
            {
                diffuse[d] = ARENA_SAMPLE_ARRAY_GRAD(_DiffuseArray, float3(uv[d], indices[d]),
                                                    baseDx * diffuseGradientScale, baseDy * diffuseGradientScale);
                heights[d] = max(diffuse[d].a, 0.001) * weights[d];
            }
            float maxHeight = max(max(heights.x, heights.y), max(heights.z, heights.w));
            float transition = max(maxHeight * _Contrast, 0.0001);
            weights = saturate((heights - (maxHeight - transition)) / transition);
            weights /= max(dot(weights, float4(1,1,1,1)), 0.001);
            float2 normalGradientScale = 0;
            [unroll] for (int g = 0; g < 4; g++) normalGradientScale += layerUV[g].xy * weights[g];

            float3 albedo = 0;
            float2 normalXY = 0;
            float smoothness = 0, occlusion = 0;
            [unroll] for (int j = 0; j < 4; j++)
            {
                float4 adjust = LayerAdjust(indices[j]);
                float3 color = saturate(saturate((diffuse[j].rgb - 0.5) * adjust.y + 0.5) + adjust.x);
                float4 packed = ARENA_SAMPLE_ARRAY_GRAD(_NormalSAOArray, float3(uv[j], indices[j]),
                                                        baseDx * normalGradientScale, baseDy * normalGradientScale);
                albedo += color * weights[j];
                normalXY += (packed.ag * 2.0 - 1.0) * weights[j];
                smoothness += packed.r * weights[j];
                occlusion += packed.b * weights[j];
            }
            output.Albedo = albedo;
            output.Normal = float3(normalXY, sqrt(1.0 - min(dot(normalXY, normalXY), 1.0)));
            output.Metallic = 0;
            output.Smoothness = smoothness;
            output.Occlusion = occlusion;
            output.Alpha = 1;
        }
        ENDCG
    }
    FallBack Off
}
