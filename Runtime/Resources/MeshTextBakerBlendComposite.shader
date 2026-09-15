// Mesh Text Baker — Photoshop-style albedo layer composite.
Shader "Hidden/MeshTextBaker/BlendComposite"
{
    Properties
    {
        _MainTex ("Base", 2D) = "white" {}
        _LayerTex ("Layer", 2D) = "black" {}
        _Mode ("Mode", Float) = 0
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
            sampler2D _LayerTex;
            int _Mode;

            float3 Overlay(float3 b, float3 s)
            {
                return float3(
                    b.r < 0.5 ? 2.0*b.r*s.r : 1.0-2.0*(1.0-b.r)*(1.0-s.r),
                    b.g < 0.5 ? 2.0*b.g*s.g : 1.0-2.0*(1.0-b.g)*(1.0-s.g),
                    b.b < 0.5 ? 2.0*b.b*s.b : 1.0-2.0*(1.0-b.b)*(1.0-s.b));
            }

            float4 frag(v2f_img i) : SV_Target
            {
                float4 baseColor = tex2D(_MainTex, i.uv);
                float4 layer = tex2D(_LayerTex, i.uv);
                float a = saturate(layer.a);
                // TMP renders premultiplied layers. Recover straight RGB for blend math.
                float3 src = a > 1e-5 ? layer.rgb / a : 0.0;
                float3 blended = src;
                if (_Mode == 1) blended = baseColor.rgb * src;                         // Multiply
                else if (_Mode == 2) blended = 1.0 - (1.0-baseColor.rgb)*(1.0-src);    // Screen
                else if (_Mode == 3) blended = Overlay(baseColor.rgb, src);             // Overlay
                else if (_Mode == 4) blended = saturate(baseColor.rgb + src);           // Add
                else if (_Mode == 5) blended = saturate(baseColor.rgb - src);           // Subtract
                return float4(lerp(baseColor.rgb, blended, a),
                              baseColor.a + a * (1.0 - baseColor.a));
            }
            ENDCG
        }
    }
    Fallback Off
}
