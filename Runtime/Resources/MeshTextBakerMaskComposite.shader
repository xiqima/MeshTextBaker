// Mesh Text Baker — MaskComposite.shader
// Composites zone coverage into a PBR mask map:
//   result = lerp(existingMask, targetValues, coverage)
// where coverage is the alpha of the rendered glyphs/image (0..1).
//
// This cannot be done with regular alpha blending because mask channels carry independent
// meanings (HDRP _MaskMap: R=metallic, G=AO, B=detail, A=smoothness; URP/Built-in
// _MetallicGlossMap: R=metallic, A=smoothness) and all four must lerp by the SAME factor.
//
// Lives in Resources/ so it is always included in builds (runtime baking support).

Shader "Hidden/MeshTextBaker/MaskComposite"
{
    Properties
    {
        _MainTex ("Base Mask", 2D) = "black" {}
        _CoverTex ("Coverage", 2D) = "black" {}
        _TargetValues ("Target Values", Vector) = (0, 1, 0, 0.5)
    }
    SubShader
    {
        Pass
        {
            ZTest Always Cull Off ZWrite Off

            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            sampler2D _CoverTex;
            float4 _TargetValues;

            float4 frag (v2f_img i) : SV_Target
            {
                float4 baseValues = tex2D(_MainTex, i.uv);
                float coverage = tex2D(_CoverTex, i.uv).a;
                return lerp(baseValues, _TargetValues, coverage);
            }
            ENDCG
        }
    }
    Fallback Off
}
