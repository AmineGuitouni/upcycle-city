#ifndef UPCYCLE_POLLUTION_LIT_DEPTH_PASSES_INCLUDED
#define UPCYCLE_POLLUTION_LIT_DEPTH_PASSES_INCLUDED

// ShadowCaster, DepthOnly and DepthNormals for PollutionLit. Each one clips with the same
// dissolve as the forward pass, so hidden objects cast no shadow and leave no depth or SSAO.

#include "PollutionLitInput.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"
#if defined(LOD_FADE_CROSSFADE)
    #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/LODCrossFade.hlsl"
#endif

// Set by URP's ShadowUtils for the light being rendered
float3 _LightDirection;
float3 _LightPosition;

struct DepthAttributes
{
    float4 positionOS : POSITION;
    float3 normalOS   : NORMAL;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

struct DepthVaryings
{
    float4 positionCS : SV_POSITION;
    float3 positionWS : TEXCOORD0;
    half3  normalWS   : TEXCOORD1;
    UNITY_VERTEX_INPUT_INSTANCE_ID
    UNITY_VERTEX_OUTPUT_STEREO
};

DepthVaryings InitDepthVaryings(DepthAttributes input)
{
    DepthVaryings output = (DepthVaryings)0;
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_TRANSFER_INSTANCE_ID(input, output);
    UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
    output.positionWS = TransformObjectToWorld(input.positionOS.xyz);
    output.normalWS = TransformObjectToWorldNormal(input.normalOS);
    return output;
}

void ClipDepthFragment(DepthVaryings input)
{
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
    PollutionClip(input.positionWS);
#if defined(LOD_FADE_CROSSFADE)
    LODFadeCrossFade(input.positionCS);
#endif
}

// ---------------------------------------------------------------- ShadowCaster

DepthVaryings ShadowCasterVertex(DepthAttributes input)
{
    DepthVaryings output = InitDepthVaryings(input);
#if _CASTING_PUNCTUAL_LIGHT_SHADOW
    float3 lightDirectionWS = normalize(_LightPosition - output.positionWS);
#else
    float3 lightDirectionWS = _LightDirection;
#endif
    float4 positionCS = TransformWorldToHClip(ApplyShadowBias(output.positionWS, output.normalWS, lightDirectionWS));
    output.positionCS = ApplyShadowClamping(positionCS);
    return output;
}

half4 ShadowCasterFragment(DepthVaryings input) : SV_TARGET
{
    ClipDepthFragment(input);
    return 0;
}

// ---------------------------------------------------------------- DepthOnly

DepthVaryings DepthOnlyVertex(DepthAttributes input)
{
    DepthVaryings output = InitDepthVaryings(input);
    output.positionCS = TransformWorldToHClip(output.positionWS);
    return output;
}

half DepthOnlyFragment(DepthVaryings input) : SV_TARGET
{
    ClipDepthFragment(input);
    return input.positionCS.z;
}

// ---------------------------------------------------------------- DepthNormals

void DepthNormalsFragment(
    DepthVaryings input
    , out half4 outNormalWS : SV_Target0
#ifdef _WRITE_RENDERING_LAYERS
    , out uint outRenderingLayers : SV_Target1
#endif
)
{
    ClipDepthFragment(input);

#if defined(_GBUFFER_NORMALS_OCT)
    float3 normalWS = normalize(input.normalWS);
    float2 octNormalWS = PackNormalOctQuadEncode(normalWS);
    float2 remappedOctNormalWS = saturate(octNormalWS * 0.5 + 0.5);
    outNormalWS = half4(PackFloat2To888(remappedOctNormalWS), 0.0);
#else
    outNormalWS = half4(NormalizeNormalPerPixel(input.normalWS), 0.0);
#endif

#ifdef _WRITE_RENDERING_LAYERS
    outRenderingLayers = EncodeMeshRenderingLayer();
#endif
}

#endif
