#ifndef UPCYCLE_POLLUTION_INCLUDED
#define UPCYCLE_POLLUTION_INCLUDED

// Shared pollution model for every UpcycleCity shader.
//
// Everything below is a *global* pushed by PollutionController (see PollutionShaderGlobals.cs),
// so materials stay SRP Batcher compatible and the whole city reacts to one value:
//   _Pollution           scene-wide level, 0 = clean, 1 = fully polluted
//   _PollutionZones      local overrides (a cleaned district, a clean-up wave), up to POLLUTION_MAX_ZONES
//   per renderer         PollutionVisibility.cs writes Renderer.SetShaderUserValue to make an object
//                        show only in the polluted or only in the clean city (dissolving in between)

#define POLLUTION_MAX_ZONES 16

// Keep in sync with PollutionVisibilityMode in PollutionVisibility.cs.
#define POLLUTION_VISIBILITY_ALWAYS        0u
#define POLLUTION_VISIBILITY_WHEN_POLLUTED 1u
#define POLLUTION_VISIBILITY_WHEN_CLEAN    2u

float  _Pollution;
float  _PollutionZoneCount;
float4 _PollutionZones[POLLUTION_MAX_ZONES];        // xyz centre (world), w radius (m)
float4 _PollutionZoneSettings[POLLUTION_MAX_ZONES]; // x level inside, y falloff width (m), z weight

float4 _PollutionSmogTint;         // rgb tint for dirty colours, a strength
float4 _PollutionGrimeColor;       // rgb dirt multiplied into surfaces, a strength
float4 _PollutionDeadFoliageColor; // rgb colour that living plants dry out to
float4 _PollutionSurfaceParams;    // x desaturation, y darkening, z grime scale (1/m), w ground dirt height (m)
float4 _PollutionDissolveColor;    // rgb HDR rim colour of the clean-up dissolve
float4 _PollutionDissolveParams;   // x noise scale (1/m), y glowing edge width, z per-object stagger, w noise spread

// ---------------------------------------------------------------- noise

float PollutionHash(float3 p)
{
    p = frac(p * 0.1031);
    p += dot(p, p.zyx + 31.32);
    return frac((p.x + p.y) * p.z);
}

float PollutionNoise(float3 p)
{
    float3 i = floor(p);
    float3 f = frac(p);
    f = f * f * (3.0 - 2.0 * f);

    float a = lerp(PollutionHash(i),                    PollutionHash(i + float3(1, 0, 0)), f.x);
    float b = lerp(PollutionHash(i + float3(0, 1, 0)),  PollutionHash(i + float3(1, 1, 0)), f.x);
    float c = lerp(PollutionHash(i + float3(0, 0, 1)),  PollutionHash(i + float3(1, 0, 1)), f.x);
    float d = lerp(PollutionHash(i + float3(0, 1, 1)),  PollutionHash(i + float3(1, 1, 1)), f.x);
    return lerp(lerp(a, b, f.y), lerp(c, d, f.y), f.z);
}

float PollutionFbm(float3 p)
{
    float sum = PollutionNoise(p) * 0.5;
    sum += PollutionNoise(p * 2.03 + 17.1) * 0.25;
    sum += PollutionNoise(p * 4.01 + 31.7) * 0.125;
    return sum / 0.875;
}

// ---------------------------------------------------------------- level

// Pollution at a world position: the scene-wide level, overridden inside zones.
// Zones are vertical cylinders, so a cleaned district is clean from the street to the rooftops.
float SamplePollution(float3 positionWS)
{
    float level = _Pollution;
    uint count = min((uint)_PollutionZoneCount, (uint)POLLUTION_MAX_ZONES);

    [loop]
    for (uint i = 0; i < count; i++)
    {
        float4 zone = _PollutionZones[i];
        float4 settings = _PollutionZoneSettings[i];
        float distanceToCentre = distance(positionWS.xz, zone.xz);
        float inside = 1.0 - smoothstep(zone.w - settings.y, zone.w, distanceToCentre);
        level = lerp(level, settings.x, inside * settings.z);
    }
    return saturate(level);
}

// ---------------------------------------------------------------- surfaces

struct PollutionSurface
{
    float3 positionWS;
    half3  normalWS;
    half   sensitivity; // how strongly the material reacts at all
    half   grime;       // how much dirt sticks to it
    half   foliage;     // 1 for living plants: green parts dry out to brown
};

// The polluted look: colour drained to smoggy grey, plants dried out, blotchy grime,
// rain streaks down walls and dirt splashed up from the street.
half3 PollutionAlbedo(half3 albedo, PollutionSurface surface, float pollution)
{
    half amount = saturate(pollution * surface.sensitivity);

    half luminance = dot(albedo, half3(0.2126h, 0.7152h, 0.0722h));
    half3 dirty = lerp(albedo, luminance.xxx, _PollutionSurfaceParams.x);
    dirty *= lerp(half3(1.0h, 1.0h, 1.0h), _PollutionSmogTint.rgb, _PollutionSmogTint.a);
    dirty *= 1.0h - _PollutionSurfaceParams.y;

    // Only green pixels dry out, so trunks, flowers and pots keep their colour
    half greenness = saturate((albedo.g - max(albedo.r, albedo.b)) * 8.0h);
    dirty = lerp(dirty, _PollutionDeadFoliageColor.rgb * (0.6h + luminance), surface.foliage * greenness);

    float3 p = surface.positionWS * _PollutionSurfaceParams.z;
    half blotches = smoothstep(0.42h, 0.78h, PollutionFbm(p));
    half wall = 1.0h - abs(surface.normalWS.y);
    half streaks = smoothstep(0.55h, 0.85h, PollutionNoise(float3(p.x * 6.0, p.y * 0.6, p.z * 6.0))) * wall;
    half ground = 1.0h - saturate(surface.positionWS.y / max(_PollutionSurfaceParams.w, 0.01h));
    half grime = saturate(max(blotches, streaks) * 0.8h + ground * 0.6h) * surface.grime * _PollutionGrimeColor.a;
    dirty = lerp(dirty, dirty * _PollutionGrimeColor.rgb, grime);

    return lerp(albedo, dirty, amount);
}

// ---------------------------------------------------------------- visibility

// PollutionVisibility.cs packs the per-renderer setup into the renderer's shader user value:
// bits 0-1 mode, bits 8-15 threshold (0..255 maps to 0..1).
uint PollutionVisibilityMode()
{
    return unity_RendererUserValue & 3u;
}

float PollutionVisibilityThreshold()
{
    return ((unity_RendererUserValue >> 8) & 255u) / 255.0;
}

// Signed distance to the dissolve edge: positive is visible, negative is clipped.
// Each object gets its own offset from its pivot, so litter vanishes piece by piece, and
// world-space noise gives the edge an organic shape that sweeps along with clean zones.
float PollutionVisibilityMargin(float3 positionWS, float pollution)
{
    uint mode = PollutionVisibilityMode();
    if (mode == POLLUTION_VISIBILITY_ALWAYS)
        return 1e4;

    float3 pivot = GetObjectToWorldMatrix()._m03_m13_m23;
    float stagger = (PollutionHash(floor(pivot * 3.7)) - 0.5) * _PollutionDissolveParams.z;
    float edgeNoise = (PollutionNoise(positionWS * _PollutionDissolveParams.x) - 0.5) * _PollutionDissolveParams.w;
    float margin = pollution - (PollutionVisibilityThreshold() + stagger + edgeNoise);
    return mode == POLLUTION_VISIBILITY_WHEN_POLLUTED ? margin : -margin;
}

// For depth and shadow passes: skips the pollution lookup for objects that are always visible.
void PollutionClip(float3 positionWS)
{
    if (PollutionVisibilityMode() != POLLUTION_VISIBILITY_ALWAYS)
        clip(PollutionVisibilityMargin(positionWS, SamplePollution(positionWS)));
}

// Glowing rim just inside the dissolve edge.
half3 PollutionDissolveGlow(float margin)
{
    half rim = 1.0h - saturate(margin / max(_PollutionDissolveParams.y, 1e-3));
    return _PollutionDissolveColor.rgb * (rim * rim);
}

#endif
