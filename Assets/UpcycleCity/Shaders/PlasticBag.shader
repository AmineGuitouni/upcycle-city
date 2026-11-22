// Thin plastic for the intro's carrier bags (mobile-safe). Two-sided and slightly see-through: soft wrap
// lighting, light glowing through from behind, a sheen on the creases, a grubby tint in polluted places,
// and a wind flutter that shivers the loose parts (more towards the bottom of a hanging bag).
Shader "UpcycleCity/Plastic Bag"
{
    Properties
    {
        _BaseColor("Colour", Color) = (0.95, 0.95, 0.92, 0.82)
        _DirtyColor("Dirty Colour", Color) = (0.72, 0.7, 0.6, 0.9)
        _Translucency("Translucency", Range(0, 2)) = 0.8
        _Sheen("Sheen", Range(0, 2)) = 0.6
        _Flutter("Flutter (m)", Range(0, 0.05)) = 0.01
        _FlutterSpeed("Flutter Speed", Float) = 7
        [Toggle] _HangMask("Flutter More Towards The Bottom", Float) = 1
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
            #pragma vertex BagVertex
            #pragma fragment BagFragment
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _CLUSTER_LIGHT_LOOP
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Fog.hlsl"
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Include/Pollution.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _BaseColor;
                half4 _DirtyColor;
                half _Translucency;
                half _Sheen;
                float _Flutter;
                float _FlutterSpeed;
                half _HangMask;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                half fogFactor : TEXCOORD2;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings BagVertex(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);
                float3 normalWS = TransformObjectToWorldNormal(input.normalOS);
                float mask = lerp(1.0, saturate(-input.positionOS.y * 3.0), _HangMask);
                float t = _Time.y * _FlutterSpeed;
                float wave = sin(t + dot(positionWS, float3(13.1, 7.7, 11.3))) * 0.6
                           + sin(t * 1.73 + dot(positionWS, float3(-5.3, 17.9, 3.1))) * 0.4;
                positionWS += normalWS * wave * _Flutter * mask;

                output.positionWS = positionWS;
                output.normalWS = normalWS;
                output.positionCS = TransformWorldToHClip(positionWS);
                output.fogFactor = ComputeFogFactor(output.positionCS.z);
                return output;
            }

            half3 ShadeLight(Light light, half3 n, half3 v, half3 albedo)
            {
                half3 l = light.direction;
                half atten = light.distanceAttenuation * light.shadowAttenuation;
                half wrap = saturate((dot(n, l) + 0.5h) / 1.5h);
                half back = pow(saturate(dot(v, -l)), 4.0h) * _Translucency;   // light glowing through
                half3 h = normalize(l + v);
                half sheen = pow(saturate(dot(n, h)), 48.0h) * _Sheen;
                return light.color * atten * (albedo * (wrap + back) + sheen);
            }

            half4 BagFragment(Varyings input, bool isFrontFace : SV_IsFrontFace) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                half3 n = normalize(input.normalWS) * (isFrontFace ? 1.0h : -1.0h);
                half3 v = normalize(GetWorldSpaceViewDir(input.positionWS));
                float pollution = SamplePollution(input.positionWS);
                half4 baseColor = lerp(_BaseColor, _DirtyColor, pollution * 0.8);

                half3 color = baseColor.rgb * SampleSH(n);
                color += ShadeLight(GetMainLight(), n, v, baseColor.rgb);

                #if defined(_ADDITIONAL_LIGHTS)
                uint count = GetAdditionalLightsCount();
                #if USE_CLUSTER_LIGHT_LOOP
                InputData inputData = (InputData)0;
                inputData.positionWS = input.positionWS;
                inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(input.positionCS);
                LIGHT_LOOP_BEGIN(count)
                    color += ShadeLight(GetAdditionalLight(lightIndex, input.positionWS), n, v, baseColor.rgb);
                LIGHT_LOOP_END
                #else
                for (uint i = 0u; i < count; ++i)
                    color += ShadeLight(GetAdditionalLight(i, input.positionWS), n, v, baseColor.rgb);
                #endif
                #endif

                color = MixFog(color, input.fogFactor);
                return half4(color, baseColor.a);
            }
            ENDHLSL
        }
    }

    FallBack Off
}
