#ifndef UPCYCLE_POLLUTION_LIT_INPUT_INCLUDED
#define UPCYCLE_POLLUTION_LIT_INPUT_INCLUDED

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/SurfaceInput.hlsl"
#include "Pollution.hlsl"

CBUFFER_START(UnityPerMaterial)
    float4 _BaseMap_ST;
    half4 _BaseColor;
    half4 _EmissionColor;
    half _Smoothness;
    half _Metallic;
    half _PollutionSensitivity;
    half _GrimeAmount;
    half _FoliageAmount;
    half4 _WindowColor;
    float4 _FacadeGrid;
    half _ColorVariation;
    float _VariationScale;
CBUFFER_END

// Soft world-space patches of lighter and darker colour, so big lawns and pavements don't read as carpet
half3 ApplyColorVariation(half3 albedo, float3 positionWS)
{
    if (_ColorVariation <= 0.0h)
        return albedo;
    half patches = PollutionFbm(positionWS * _VariationScale) * 2.0h - 1.0h;
    return albedo * (1.0h + patches * _ColorVariation);
}

// Windows drawn in world space, so a unit-cube skyline block scaled to any size keeps real-size
// windows. _FacadeGrid: x window spacing (m), y floor height (m), zw window width and height (0..1).
half FacadeWindowMask(float3 positionWS, half3 normalWS)
{
    if (abs(normalWS.y) > 0.5h)
        return 0.0h; // roofs

    float2 along = normalize(float2(-normalWS.z, normalWS.x));
    float2 cell = float2(dot(positionWS.xz, along) / _FacadeGrid.x, positionWS.y / _FacadeGrid.y);
    float2 footprint = fwidth(cell);
    float2 edge = _FacadeGrid.zw * 0.5 - abs(frac(cell) - 0.5);
    float2 inside = saturate(edge / max(footprint, 1e-4) + 0.5);
    // Far away a cell shrinks below a pixel: fade to the average coverage instead of shimmering
    inside = lerp(inside, _FacadeGrid.zw, saturate(footprint * 1.5));
    return inside.x * inside.y;
}

PollutionSurface GetPollutionSurface(float3 positionWS, half3 normalWS)
{
    PollutionSurface surface;
    surface.positionWS = positionWS;
    surface.normalWS = normalWS;
    surface.sensitivity = _PollutionSensitivity;
    surface.grime = _GrimeAmount;
    surface.foliage = _FoliageAmount;
    return surface;
}

#endif
