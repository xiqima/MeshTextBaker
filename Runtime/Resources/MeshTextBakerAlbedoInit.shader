// Mesh Text Baker — copies the source albedo and folds the material inspector tint into it.
Shader "Hidden/MeshTextBaker/AlbedoInit"
{
    Properties
    {
        _MainTex ("Source", 2D) = "white" {}
        _Tint ("Tint", Color) = (1,1,1,1)
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
            float4 _Tint;
            float4 frag(v2f_img i) : SV_Target { return tex2D(_MainTex, i.uv) * _Tint; }
            ENDCG
        }
    }
    Fallback Off
}
