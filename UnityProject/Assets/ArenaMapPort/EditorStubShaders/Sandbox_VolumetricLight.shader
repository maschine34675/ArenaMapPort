// Editor-time stand-in for the game's "Sandbox/VolumetricLight" shader (PLAN-TWO-MAPS item 7).
// VolumetricLight is [ExecuteInEditMode]: the importer's AddComponent runs its Awake inside the batch
// import, and Awake builds a private (non-serialized) material from ShadersFinder.Find(name), which
// outside play mode is plain Shader.Find(name). Without this asset that lookup returns null and
// Unity logs an ArgumentNullException per instance (17 on Equator), which the build's log scan
// treats as fatal. The material never reaches the bundle; at raid time VolumetricLight.Awake runs
// again against the game's real shader registry. Never rendered.
Shader "Sandbox/VolumetricLight"
{
    SubShader
    {
        Tags { "RenderType" = "Opaque" }
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            float4 vert(float4 vertex : POSITION) : SV_POSITION
            {
                return UnityObjectToClipPos(vertex);
            }

            fixed4 frag() : SV_Target
            {
                return fixed4(0, 0, 0, 0);
            }
            ENDCG
        }
    }
}
