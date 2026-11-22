// Stylised sea for the intro cinematic (mobile-safe, no depth or opaque texture needed).
// Gentle vertex waves, a foam band that washes in and out at the shoreline, sun glints and fresnel.
// Its colour follows the pollution level: murky grey-green at 1, clear turquoise at 0. _Dream adds the
// sparkle and saturation of a memory. Seen from below (Cull Off) it renders as a bright rippled ceiling.
//
// The mesh's UV is in metres from the shoreline edge: u along the shore, v out to sea.
Shader "UpcycleCity/Stylized Water"
{
    Properties
    {
        [Header(Clean)]
        _ShallowColor("Shallow", Color) = (0.32, 0.86, 0.82, 1)
        _DeepColor("Deep", Color) = (0.05, 0.42, 0.62, 1)
        [Header(Polluted)]
        _MurkyShallowColor("Murky Shallow", Color) = (0.42, 0.44, 0.36, 1)
        _MurkyDeepColor("Murky Deep", Color) = (0.20, 0.24, 0.22, 1)
        [Header(Underside)]
        _UndersideColor("Underside", Color) = (0.45, 0.92, 0.95, 1)

        [Header(Shape)]
        _DepthDistance("Shallow to Deep (m)", Float) = 18
        _WaveHeight("Wave Height (m)", Float) = 0.12
        _WaveLength("Wave Length (m)", Float) = 7
        _WaveSpeed("Wave Speed", Float) = 1.2

        [Header(Foam)]
        _FoamColor("Foam", Color) = (0.95, 0.97, 0.95, 1)
        _FoamWidth("Foam Width (m)", Float) = 1.6
        _FoamWash("Wash Distance (m)", Float) = 0.8

        [Header(Look)]
        _Dream("Dream", Range(0, 1)) = 0
        _Glints("Sun Glints", Range(0, 4)) = 1
        _ShoreAlpha("Shore Alpha", Range(0, 1)) = 0.55
        _DeepAlpha("Deep Alpha", Range(0, 1)) = 0.93
    }

    SubShader
    {
        Tags
        {
            "Queue" = "Transparent-10"
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
            #pragma vertex WaterVertex
            #pragma fragment WaterFragment
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Fog.hlsl"
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Include/Pollution.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _ShallowColor;
                half4 _DeepColor;
                half4 _MurkyShallowColor;
                half4 _MurkyDeepColor;
                half4 _UndersideColor;
                float _DepthDistance;
                float _WaveHeight;
                float _WaveLength;
                float _WaveSpeed;
                half4 _FoamColor;
                float _FoamWidth;
                float _FoamWash;
                half _Dream;
                half _Glints;
                half _ShoreAlpha;
                half _DeepAlpha;
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
                float3 positionWS : TEXCOORD0;
                float3 normalWS   : TEXCOORD1;
                float2 uv         : TEXCOORD2;
                half   fogFactor  : TEXCOORD3;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            // Two crossing sine swells; returns height and its xz gradient. Waves calm near the shore.
            float3 Swell(float2 xz, float shoreFade)
            {
                float k = TWO_PI / max(_WaveLength, 0.1);
                float t = _Time.y * _WaveSpeed;
                float2 d1 = normalize(float2(0.25, 1.0));
                float2 d2 = normalize(float2(-0.6, 0.8));
                float p1 = dot(d1, xz) * k - t;
                float p2 = dot(d2, xz) * k * 1.7 - t * 1.3;
                float h = (sin(p1) + 0.45 * sin(p2)) * _WaveHeight * shoreFade;
                float2 grad = (cos(p1) * k * d1 + 0.45 * cos(p2) * k * 1.7 * d2) * _WaveHeight * shoreFade;
                return float3(h, grad);
            }

            Varyings WaterVertex(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);
                float shoreFade = saturate(input.uv.y / 4.0) * 0.8 + 0.2;
                float3 swell = Swell(positionWS.xz, shoreFade);
                positionWS.y += swell.x;

                output.positionWS = positionWS;
                output.normalWS = normalize(float3(-swell.y, 1.0, -swell.z));
                output.uv = input.uv;
                output.positionCS = TransformWorldToHClip(positionWS);
                output.fogFactor = ComputeFogFactor(output.positionCS.z);
                return output;
            }

            half4 WaterFragment(Varyings input, bool isFrontFace : SV_IsFrontFace) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                float pollution = SamplePollution(input.positionWS);
                float3 viewWS = normalize(GetWorldSpaceViewDir(input.positionWS));
                Light mainLight = GetMainLight();

                // Small ripples from noise perturb the normal
                float2 rippleUv = input.positionWS.xz * 0.35;
                float n1 = PollutionFbm(float3(rippleUv + _Time.y * float2(0.05, 0.11), _Time.y * 0.05));
                float n2 = PollutionFbm(float3(rippleUv * 2.3 - _Time.y * float2(0.09, 0.04), 3.1));
                float3 normalWS = normalize(input.normalWS + float3(n1 - 0.5, 0.0, n2 - 0.5) * 0.35);

                if (!isFrontFace)
                {
                    // From below: a bright surface rippling with light
                    half caustic = smoothstep(0.55, 0.75, n1 * 0.6 + n2 * 0.6);
                    half3 under = _UndersideColor.rgb * (0.75 + 0.6 * caustic) * (1.0h + _Dream * 0.4h);
                    under = MixFog(under, input.fogFactor);
                    return half4(under, 0.9h);
                }

                half depth01 = saturate(input.uv.y / max(_DepthDistance, 0.01));
                half3 clean = lerp(_ShallowColor.rgb, _DeepColor.rgb, depth01);
                half3 murky = lerp(_MurkyShallowColor.rgb, _MurkyDeepColor.rgb, depth01);
                half3 color = lerp(clean, murky, pollution);

                // Diffuse-ish shading, sky fresnel and sun glints
                half ndl = saturate(dot(normalWS, mainLight.direction)) * 0.35h + 0.65h;
                color *= ndl * lerp(half3(1, 1, 1), mainLight.color, 0.4h);
                half fresnel = pow(1.0h - saturate(dot(normalWS, viewWS)), 4.0h);
                color = lerp(color, _GlossyEnvironmentColor.rgb * 1.3h + 0.1h, fresnel * 0.5h);
                float3 halfDir = normalize(mainLight.direction + viewWS);
                half spec = pow(saturate(dot(normalWS, halfDir)), lerp(180.0h, 60.0h, _Dream));
                half glint = spec * _Glints * (1.0h - pollution * 0.8h) * (1.0h + _Dream * 2.0h);
                color += mainLight.color * glint;

                // Foam: a noisy band at the shore that washes up and back
                half wash = sin(_Time.y * 0.9) * 0.5h + 0.5h;
                float foamEdge = _FoamWidth + wash * _FoamWash;
                half foamNoise = PollutionFbm(float3(input.uv * float2(0.9, 2.2) + float2(_Time.y * 0.15, 0), 7.0));
                half foam = saturate(1.0h - input.uv.y / max(foamEdge, 0.01));
                foam = smoothstep(0.35h, 0.6h, foam * 0.8h + foamNoise * 0.5h);
                half3 foamColor = lerp(_FoamColor.rgb, _FoamColor.rgb * half3(0.75, 0.72, 0.62), pollution);
                color = lerp(color, foamColor, foam);

                // The dream pass: more saturated and luminous
                half luma = dot(color, half3(0.299, 0.587, 0.114));
                color = lerp(color, lerp(luma.xxx, color, 1.45h) * 1.15h, _Dream);

                half alpha = lerp(_ShoreAlpha, _DeepAlpha, depth01);
                alpha = max(alpha, foam);
                color = MixFog(color, input.fogFactor);
                return half4(color, alpha);
            }
            ENDHLSL
        }
    }

    FallBack Off
}
