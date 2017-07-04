Shader "Hidden/MiniEngineAO/Util"
{
    Properties
    {
        _AOTexture("", 2D) = "" {}
        _TileTexture("", 2DArray) = "" {}
    }

    CGINCLUDE

    #include "UnityCG.cginc"

    struct Attributes
    {
        float4 vertex : POSITION;
        float2 uv : TEXCOORD0;
    };

    struct Varyings
    {
        float4 vertex : SV_POSITION;
        float2 uv : TEXCOORD0;
    };

    Varyings Vert(Attributes input)
    {
        Varyings o;
        o.vertex = UnityObjectToClipPos(input.vertex);
        o.uv = input.uv;
        return o;
    }

    ENDCG

    SubShader
    {
        Cull Off ZWrite Off ZTest Always

        Pass
        {
            CGPROGRAM

            #pragma vertex Vert
            #pragma fragment Frag

            sampler2D_float _CameraDepthTexture;

            float4 Frag(Attributes input) : SV_Target
            {
                return SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, input.uv);
            }

            ENDCG
        }

        Pass
        {
            CGPROGRAM

            #pragma vertex Vert
            #pragma fragment Frag

            sampler2D _AOTexture;

            half4 Frag(Attributes input) : SV_Target
            {
                return tex2D(_AOTexture, input.uv).r;
            }

            ENDCG
        }

        Pass
        {
            CGPROGRAM

            #pragma vertex Vert
            #pragma fragment Frag

            UNITY_DECLARE_TEX2DARRAY(_TileTexture);

            half4 Frag(Attributes input) : SV_Target
            {
                float3 uvw = float3(input.uv, fmod(_Time.y * 8, 16));
                return UNITY_SAMPLE_TEX2DARRAY(_TileTexture, uvw).r;
            }

            ENDCG
        }
    }
}
