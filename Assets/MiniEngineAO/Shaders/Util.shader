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

    Varyings VertTraditionalBlit(Attributes input)
    {
        Varyings o;
        o.vertex = UnityObjectToClipPos(input.vertex);
        o.uv = input.uv;
        return o;
    }

    Varyings VertProceduralBlit(uint vid : SV_VertexID)
    {
        float vx = vid == 1 ? 2 : 0;
        float vy = vid == 2 ? -1 : 1;

        Varyings o;
        o.vertex = float4(vx * 2 - 1, 1 - vy * 2, 0, 1);
        o.uv = float2(vx, vy);
        return o;
    }

    ENDCG

    SubShader
    {
        Cull Off ZWrite Off ZTest Always

        Pass
        {
            Name "DepthCopy"

            CGPROGRAM

            #pragma vertex VertTraditionalBlit
            #pragma fragment Frag

            sampler2D_float _CameraDepthTexture;

            float4 Frag(Varyings input) : SV_Target
            {
                return SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, input.uv);
            }

            ENDCG
        }

        Pass
        {
            Name "CompositeToGBuffer"

            Blend Zero OneMinusSrcColor, Zero OneMinusSrcAlpha

            CGPROGRAM

            #pragma vertex VertProceduralBlit
            #pragma fragment Frag

            sampler2D _AOTexture;

            struct Output
            {
                float4 gbuffer0 : SV_Target0;
                float4 gbuffer3 : SV_Target1;
            };

            Output Frag(Varyings input)
            {
                float ao = 1 - tex2D(_AOTexture, input.uv).r;
                Output output;
                output.gbuffer0 = float4(0, 0, 0, ao);
                output.gbuffer3 = float4(ao, ao, ao, 0);
                return output;
            }

            ENDCG
        }

        Pass
        {
            Name "CompositeToFrameBuffer"

            Blend Zero SrcAlpha

            CGPROGRAM

            #pragma vertex VertProceduralBlit
            #pragma fragment Frag

            sampler2D _AOTexture;

            float4 Frag(Varyings input) : SV_Target
            {
                return tex2D(_AOTexture, input.uv).r;
            }

            ENDCG
        }
    }
}
