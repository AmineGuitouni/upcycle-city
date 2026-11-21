// Procedural skybox that moves from a clear blue day to a brown smog sky with the scene-wide
// pollution level. The horizon blends into the scene fog colour, so distant buildings and the sky
// always meet without a seam, whatever the controller does to the fog.
Shader "UpcycleCity/Pollution Sky"
{
    Properties
    {
        [Header(Clean)]
        _CleanZenith("Zenith", Color) = (0.24, 0.47, 0.82, 1)
        _CleanHorizon("Horizon", Color) = (0.70, 0.82, 0.94, 1)
        _CleanHorizonFalloff("Horizon Falloff", Range(1, 12)) = 5
        _CloudColor("Cloud Colour", Color) = (1, 1, 1, 1)
        _CloudCover("Cloud Cover", Range(0, 1)) = 0.45

        [Header(Polluted)]
        _SmogZenith("Zenith", Color) = (0.50, 0.46, 0.39, 1)
        _SmogHorizon("Horizon", Color) = (0.76, 0.68, 0.54, 1)
        _SmogHorizonFalloff("Horizon Falloff", Range(1, 12)) = 1.6
        _SmogStreaks("Smog Streaks", Range(0, 1)) = 0.35

        [Header(Ground and Sun)]
        _GroundColor("Below Horizon", Color) = (0.42, 0.42, 0.40, 1)
        _FogBlend("Horizon Fog Blend", Range(0, 1)) = 0.85
        _SunSize("Sun Size", Range(0.0005, 0.02)) = 0.003
        _SunIntensity("Sun Disc Intensity", Float) = 8
        _SunGlow("Sun Glow", Range(0, 2)) = 0.5
        _Exposure("Exposure", Range(0, 4)) = 1
    }

    SubShader
    {
        Tags
        {
            "Queue" = "Background"
            "RenderType" = "Background"
            "PreviewType" = "Skybox"
            "RenderPipeline" = "UniversalPipeline"
        }
        Cull Off
        ZWrite Off

        Pass
        {
            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex SkyVertex
            #pragma fragment SkyFragment

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Include/Pollution.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _CleanZenith;
                half4 _CleanHorizon;
                half _CleanHorizonFalloff;
                half4 _CloudColor;
                half _CloudCover;
                half4 _SmogZenith;
                half4 _SmogHorizon;
                half _SmogHorizonFalloff;
                half _SmogStreaks;
                half4 _GroundColor;
                half _FogBlend;
                half _SunSize;
                half _SunIntensity;
                half _SunGlow;
                half _Exposure;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 direction  : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings SkyVertex(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.direction = input.positionOS.xyz;
                return output;
            }

            // Cloud and smog layers projected onto a flat ceiling above the camera
            float LayerNoise(float3 direction, float scale, float drift)
            {
                float2 uv = direction.xz / (direction.y + 0.12);
                return PollutionFbm(float3(uv * scale + _Time.y * drift, _Time.y * 0.01));
            }

            half4 SkyFragment(Varyings input) : SV_Target
            {
                float3 direction = normalize(input.direction);
                float pollution = _Pollution; // the sky follows the scene-wide level; zones stay on the ground
                float up = saturate(direction.y);

                half3 zenith = lerp(_CleanZenith.rgb, _SmogZenith.rgb, pollution);
                half3 horizon = lerp(_CleanHorizon.rgb, _SmogHorizon.rgb, pollution);
                horizon = lerp(horizon, unity_FogColor.rgb, _FogBlend);
                half falloff = lerp(_CleanHorizonFalloff, _SmogHorizonFalloff, pollution);
                half3 sky = lerp(horizon, zenith, 1.0h - pow(1.0h - up, falloff));

                // Clean: drifting cumulus. Polluted: flat brown streaks of smog.
                half aboveHorizon = smoothstep(0.0h, 0.15h, direction.y);
                half clouds = smoothstep(1.0h - _CloudCover, 1.0h - _CloudCover + 0.25h, LayerNoise(direction, 0.9, 0.004));
                sky = lerp(sky, _CloudColor.rgb * (0.85h + 0.15h * up), clouds * aboveHorizon * (1.0h - pollution) * 0.85h);
                half smog = smoothstep(0.35h, 0.75h, LayerNoise(direction * float3(1, 1, 3), 0.5, 0.002));
                sky = lerp(sky, _SmogZenith.rgb * 0.85h, smog * aboveHorizon * pollution * _SmogStreaks);

                // Smog scatters the sun into a wide, weak glow and almost hides the disc
                float3 sunDirection = _MainLightPosition.xyz;
                float sunDot = saturate(dot(direction, sunDirection));
                half disc = smoothstep(1.0 - _SunSize, 1.0 - _SunSize * 0.5, sunDot) * (1.0h - 0.85h * pollution);
                half glow = pow(sunDot, lerp(48.0, 6.0, pollution)) * _SunGlow;
                sky += _MainLightColor.rgb * (disc * _SunIntensity + glow * 0.25h);

                sky = lerp(sky, lerp(_GroundColor.rgb, unity_FogColor.rgb, _FogBlend), smoothstep(0.0h, -0.08h, direction.y));
                return half4(sky * _Exposure, 1.0h);
            }
            ENDHLSL
        }
    }

    FallBack Off
}
