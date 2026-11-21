// A drifting deck of smog for a large horizontal plane above the city. It fades out with the
// local pollution level, so it thins over cleaned districts and is gone over a clean city.
Shader "UpcycleCity/Smog Layer"
{
    Properties
    {
        _SmogColor("Colour", Color) = (0.55, 0.50, 0.42, 1)
        _Density("Density", Range(0, 1)) = 0.85
        _NoiseScale("Noise Scale (1/m)", Float) = 0.006
        _Drift("Drift (m/s)", Vector) = (1.5, 0.6, 0, 0)
        _Coverage("Coverage", Range(0, 1)) = 0.65
        _Softness("Softness", Range(0.01, 1)) = 0.35
        _LightInfluence("Sun Influence", Range(0, 1)) = 0.35
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
        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off
        Cull Off

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex SmogVertex
            #pragma fragment SmogFragment
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Fog.hlsl"
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Include/Pollution.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _SmogColor;
                half _Density;
                float _NoiseScale;
                float4 _Drift;
                half _Coverage;
                half _Softness;
                half _LightInfluence;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                half   fogFactor  : TEXCOORD1;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings SmogVertex(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                VertexPositionInputs vertexInput = GetVertexPositionInputs(input.positionOS.xyz);
                output.positionCS = vertexInput.positionCS;
                output.positionWS = vertexInput.positionWS;
                output.fogFactor = ComputeFogFactor(vertexInput.positionCS.z);
                return output;
            }

            half4 SmogFragment(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                float2 drift = _Drift.xy * _Time.y;
                float noise = PollutionFbm(float3((input.positionWS.xz + drift) * _NoiseScale, _Time.y * 0.01));
                half threshold = 1.0h - _Coverage;
                half cover = smoothstep(threshold - _Softness * 0.5h, threshold + _Softness * 0.5h, noise);

                float pollution = SamplePollution(input.positionWS);
                half alpha = cover * _Density * smoothstep(0.15, 0.85, pollution);

                Light mainLight = GetMainLight();
                half3 color = _SmogColor.rgb * lerp(1.0h, mainLight.color, _LightInfluence);
                color = MixFog(color, InitializeInputDataFog(float4(input.positionWS, 1.0), input.fogFactor));
                return half4(color, alpha);
            }
            ENDHLSL
        }
    }

    FallBack Off
}
