// Test-only material with the cross-pipeline property names touched by MeshTextSurface.
Shader "Hidden/MeshTextBaker/Tests/MaterialState"
{
    Properties
    {
        _MainTex ("Main", 2D) = "white" {}
        _BaseMap ("URP Base", 2D) = "white" {}
        _BaseColorMap ("HDRP Base", 2D) = "white" {}
        _BaseColor ("Base Color", Color) = (1,1,1,1)
        _Color ("Legacy Color", Color) = (1,1,1,1)
        _EmissionMap ("Emission", 2D) = "black" {}
        _EmissiveColorMap ("HDRP Emission", 2D) = "black" {}
        _MetallicGlossMap ("Metallic", 2D) = "white" {}
        _Metallic ("Metallic", Range(0, 1)) = 0
        _Smoothness ("Smoothness", Range(0, 1)) = 0.5
        _GlossMapScale ("Gloss Map Scale", Range(0, 1)) = 1
        _SmoothnessTextureChannel ("Smoothness Channel", Float) = 0
    }

    SubShader
    {
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma shader_feature_local _METALLICGLOSSMAP
            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
            };

            struct v2f
            {
                float4 vertex : SV_POSITION;
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                return fixed4(1, 1, 1, 1);
            }
            ENDCG
        }
    }
}
