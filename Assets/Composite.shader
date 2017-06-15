Shader "Hidden/ComputeAO/Composite"
{
    Properties
    {
        _AOTexture("", 2D) = "" {}
    }
    SubShader
    {
        Cull Off ZWrite Off ZTest Always

        Pass
        {
            CGPROGRAM

            #pragma vertex Vert
            #pragma fragment Frag

            #include "UnityCG.cginc"

            sampler2D _AOTexture;

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

            fixed4 Frag(Attributes input) : SV_Target
            {
                return tex2D(_AOTexture, input.uv);
            }

            ENDCG
        }
    }
}
