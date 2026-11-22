// Additive god-ray card for the intro's underwater dream: bright at the top (uv.y = 1), fading
// downwards and towards its side edges, with a slow shimmer. Put it on a tall quad, any rotation.
Shader "UpcycleCity/Light Shaft"
{
    Properties
    {
        [HDR] _Color("Colour", Color) = (0.6, 0.95, 1.0, 1)
        _Intensity("Intensity", Range(0, 4)) = 0.6
        _Shimmer("Shimmer", Range(0, 1)) = 0.4
        _ShimmerSpeed("Shimmer Speed", Float) = 0.6
    }

    SubShader
    {
        Tags
        {
            "Queue" = "Transparent"
            "RenderType" = "Transparent"
            "RenderPipeline" = "UniversalPipeline"
            "IgnoreProjector" = "True"
        }
        Blend One One
        ZWrite Off
        Cull Off

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex ShaftVertex
            #pragma fragment ShaftFragment
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _Color;
                half _Intensity;
                half _Shimmer;
                float _ShimmerSpeed;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float  seed : TEXCOORD1;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings ShaftVertex(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.uv = input.uv;
                float3 origin = TransformObjectToWorld(float3(0, 0, 0));
                output.seed = frac(sin(dot(origin.xz, float2(12.9898, 78.233))) * 43758.5453);
                return output;
            }

            half4 ShaftFragment(Varyings input) : SV_Target
            {
                half vertical = input.uv.y * input.uv.y;
                half edge = sin(input.uv.x * PI);
                edge *= edge;
                float t = _Time.y * _ShimmerSpeed + input.seed * 6.2831;
                half shimmer = 1.0h - _Shimmer * (0.5h + 0.5h * sin(t + input.uv.x * 5.0));
                half a = vertical * edge * shimmer * _Intensity;
                return half4(_Color.rgb * a, 1.0h);
            }
            ENDHLSL
        }
    }

    FallBack Off
}
