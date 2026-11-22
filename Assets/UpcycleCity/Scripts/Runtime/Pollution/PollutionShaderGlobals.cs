using System.Collections.Generic;
using UnityEngine;

namespace UpcycleCity.Pollution
{
    /// <summary>
    /// The C# side of Shaders/Include/Pollution.hlsl: every global the pollution shaders read.
    /// </summary>
    public static class PollutionShaderGlobals
    {
        /// <summary>Must match POLLUTION_MAX_ZONES in Pollution.hlsl.</summary>
        public const int MaxZones = 16;

        static readonly int PollutionId = Shader.PropertyToID("_Pollution");
        static readonly int ZoneCountId = Shader.PropertyToID("_PollutionZoneCount");
        static readonly int ZonesId = Shader.PropertyToID("_PollutionZones");
        static readonly int ZoneSettingsId = Shader.PropertyToID("_PollutionZoneSettings");
        static readonly int SmogTintId = Shader.PropertyToID("_PollutionSmogTint");
        static readonly int GrimeColorId = Shader.PropertyToID("_PollutionGrimeColor");
        static readonly int DeadFoliageColorId = Shader.PropertyToID("_PollutionDeadFoliageColor");
        static readonly int SurfaceParamsId = Shader.PropertyToID("_PollutionSurfaceParams");
        static readonly int DissolveColorId = Shader.PropertyToID("_PollutionDissolveColor");
        static readonly int DissolveParamsId = Shader.PropertyToID("_PollutionDissolveParams");
        static readonly int GridTexId = Shader.PropertyToID("_PollutionGridTex");
        static readonly int GridRectId = Shader.PropertyToID("_PollutionGridRect");
        static readonly int GridParamsId = Shader.PropertyToID("_PollutionGridParams");
        static readonly int GridLookId = Shader.PropertyToID("_PollutionGridLook");

        static readonly Vector4[] ZoneShapes = new Vector4[MaxZones];
        static readonly Vector4[] ZoneSettings = new Vector4[MaxZones];

        public static void SetLevel(float pollution) => Shader.SetGlobalFloat(PollutionId, pollution);

        public static void SetZones(IReadOnlyList<PollutionZone> zones)
        {
            int count = Mathf.Min(zones.Count, MaxZones);
            for (int i = 0; i < count; i++)
            {
                ZoneShapes[i] = zones[i].ShapeData;
                ZoneSettings[i] = zones[i].SettingsData;
            }
            // Arrays are always uploaded at full length: the first upload fixes their size on the GPU
            Shader.SetGlobalVectorArray(ZonesId, ZoneShapes);
            Shader.SetGlobalVectorArray(ZoneSettingsId, ZoneSettings);
            Shader.SetGlobalFloat(ZoneCountId, count);
        }

        /// <summary>Binds the <see cref="PollutionGrid"/> texture and layout (see SamplePollutionGrid in Pollution.hlsl).</summary>
        /// <param name="rect">xy world X/Z of the grid's minimum corner, zw 1 / world size.</param>
        /// <param name="parameters">x weight (0 turns the grid off), y cells along X, z cells along Z, w edge fade (cells).</param>
        /// <param name="look">x edge jitter (cells), y jitter noise scale (1/m).</param>
        public static void SetGrid(Texture texture, Vector4 rect, Vector4 parameters, Vector4 look)
        {
            Shader.SetGlobalTexture(GridTexId, texture);
            Shader.SetGlobalVector(GridRectId, rect);
            Shader.SetGlobalVector(GridParamsId, parameters);
            Shader.SetGlobalVector(GridLookId, look);
        }

        /// <summary>Turns the grid off: shaders fall back to the scene-wide level and zones.</summary>
        public static void ClearGrid()
        {
            Shader.SetGlobalTexture(GridTexId, Texture2D.blackTexture);
            Shader.SetGlobalVector(GridParamsId, Vector4.zero);
        }

        public static void SetSurfaceLook(PollutionProfile profile)
        {
            Shader.SetGlobalColor(SmogTintId, profile.smogTint);
            Shader.SetGlobalColor(GrimeColorId, profile.grimeColor);
            Shader.SetGlobalColor(DeadFoliageColorId, profile.deadFoliageColor);
            Shader.SetGlobalVector(SurfaceParamsId, new Vector4(
                profile.desaturation, profile.darkening, profile.grimeScale, profile.groundDirtHeight));
            Shader.SetGlobalColor(DissolveColorId, profile.dissolveEdgeColor);
            Shader.SetGlobalVector(DissolveParamsId, new Vector4(
                profile.dissolveNoiseScale, profile.dissolveEdgeWidth, profile.dissolveStagger, profile.dissolveSpread));
        }
    }
}
