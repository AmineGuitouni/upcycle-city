using System;
using UnityEngine;

namespace UpcycleCity.Pollution
{
    /// <summary>
    /// Sun, ambient light and fog at one end of the pollution scale.
    /// <see cref="PollutionController"/> blends between a clean and a polluted state.
    /// </summary>
    [Serializable]
    public struct AtmosphereState
    {
        [Header("Sun")]
        public Color sunColor;
        [Min(0f)] public float sunIntensity;
        [Tooltip("Height of the sun above the horizon, in degrees.")]
        [Range(0f, 90f)] public float sunElevation;

        [Header("Ambient")]
        public Color ambientSky;
        public Color ambientEquator;
        public Color ambientGround;

        [Header("Fog")]
        public Color fogColor;
        [Tooltip("Exponential squared fog density, per metre.")]
        [Min(0f)] public float fogDensity;

        public static AtmosphereState Lerp(in AtmosphereState a, in AtmosphereState b, float t)
        {
            return new AtmosphereState
            {
                sunColor = Color.Lerp(a.sunColor, b.sunColor, t),
                sunIntensity = Mathf.Lerp(a.sunIntensity, b.sunIntensity, t),
                sunElevation = Mathf.Lerp(a.sunElevation, b.sunElevation, t),
                ambientSky = Color.Lerp(a.ambientSky, b.ambientSky, t),
                ambientEquator = Color.Lerp(a.ambientEquator, b.ambientEquator, t),
                ambientGround = Color.Lerp(a.ambientGround, b.ambientGround, t),
                fogColor = Color.Lerp(a.fogColor, b.fogColor, t),
                fogDensity = Mathf.Lerp(a.fogDensity, b.fogDensity, t),
            };
        }

        /// <summary>A bright, clear morning.</summary>
        public static AtmosphereState DefaultClean => new AtmosphereState
        {
            sunColor = new Color(1f, 0.95f, 0.86f),
            sunIntensity = 1.7f,
            sunElevation = 40f,
            ambientSky = new Color(0.56f, 0.67f, 0.84f),
            ambientEquator = new Color(0.58f, 0.62f, 0.64f),
            ambientGround = new Color(0.36f, 0.37f, 0.32f),
            fogColor = new Color(0.72f, 0.82f, 0.92f),
            fogDensity = 0.0011f,
        };

        /// <summary>A low, weak sun behind a brown smog deck.</summary>
        public static AtmosphereState DefaultPolluted => new AtmosphereState
        {
            sunColor = new Color(1f, 0.84f, 0.64f),
            sunIntensity = 1.0f,
            sunElevation = 30f,
            ambientSky = new Color(0.64f, 0.59f, 0.49f),
            ambientEquator = new Color(0.58f, 0.53f, 0.44f),
            ambientGround = new Color(0.34f, 0.31f, 0.27f),
            fogColor = new Color(0.74f, 0.66f, 0.53f),
            fogDensity = 0.0027f,
        };
    }
}
