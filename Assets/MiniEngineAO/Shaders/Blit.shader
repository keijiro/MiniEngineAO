Shader "Hidden/MiniEngineAO/Blit"
{
    Properties
    {
        _AOTexture("", 2D) = "" {}
        _TileTexture("", 2DArray) = "" {}
    }

    CGINCLUDE

    #include "UnityCG.cginc"

    // Full screen triangle with procedural draw
    // This can't be used when the destination can be the back buffer because
    // this doesn't support the situations that requires vertical flipping.
    v2f_img vert_procedural(uint vid : SV_VertexID)
    {
        float x = vid == 1 ? 2 : 0;
        float y = vid >  1 ? 2 : 0;

        v2f_img o;
        o.pos = float4(x * 2 - 1, 1 - y * 2, 0, 1);
    #if UNITY_UV_STARTS_AT_TOP
        o.uv = float2(x, y);
    #else
        o.uv = float2(x, 1 - y);
    #endif
        o.uv = TransformStereoScreenSpaceTex(o.uv, 1);
        return o;
    }

    // The standard vertex shader for blit, slightly modified for supporting
    // single-pass stereo rendering.
    v2f_img vert_img2(appdata_img v)
    {
        v2f_img o;
        o.pos = UnityObjectToClipPos(v.vertex);
        o.uv = TransformStereoScreenSpaceTex(v.texcoord, 1);
        return o;
    }

    ENDCG

    SubShader
    {
        Cull Off ZWrite Off ZTest Always

        // 0: Depth copy with procedural draw
        Pass
        {
            CGPROGRAM

            #pragma vertex vert_procedural
            #pragma fragment frag

            sampler2D_float _CameraDepthTexture;

            float4 frag(v2f_img i) : SV_Target
            {
                return SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, i.uv);
            }

            ENDCG
        }

        // 1: Composite to G-buffer with procedural draw
        Pass
        {
            Blend Zero OneMinusSrcColor, Zero OneMinusSrcAlpha

            CGPROGRAM

            #pragma vertex vert_procedural
            #pragma fragment frag

            sampler2D _AOTexture;

            struct Output
            {
                float4 gbuffer0 : SV_Target0;
                float4 gbuffer3 : SV_Target1;
            };

            Output frag(v2f_img i)
            {
                float ao = 1 - tex2D(_AOTexture, i.uv).r;
                Output o;
                o.gbuffer0 = float4(0, 0, 0, ao);
                o.gbuffer3 = float4(ao, ao, ao, 0);
                return o;
            }

            ENDCG
        }

        // 2: Composite to the frame buffer with the standard blit
        Pass
        {
            Blend Zero SrcAlpha

            CGPROGRAM

            #pragma vertex vert_img2
            #pragma fragment frag

            sampler2D _AOTexture;

            float4 frag(v2f_img i) : SV_Target
            {
                return tex2D(_AOTexture, i.uv).r;
            }

            ENDCG
        }

        // 3: Debug blit with a single channel texture
        Pass
        {
            Name "Debug"

            CGPROGRAM

            #pragma vertex vert_img2
            #pragma fragment frag

            sampler2D _AOTexture;

            float4 frag(v2f_img i) : SV_Target
            {
                return tex2D(_AOTexture, i.uv).r;
            }

            ENDCG
        }

        // 4: Debug blit with a tiled texture
        Pass
        {
            Name "Detile"

            CGPROGRAM

            #pragma vertex vert_img2
            #pragma fragment frag

            UNITY_DECLARE_TEX2DARRAY(_TileTexture);

            float4 frag(v2f_img i) : SV_Target
            {
                float2 uv4 = i.uv * 4;
                float3 uvw = float3(frac(uv4), floor(uv4.x) + floor(uv4.y) * 4);
                return UNITY_SAMPLE_TEX2DARRAY(_TileTexture, uvw);
            }

            ENDCG
        }
    }
}
