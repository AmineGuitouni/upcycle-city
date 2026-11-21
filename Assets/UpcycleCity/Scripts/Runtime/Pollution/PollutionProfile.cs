using UnityEngine;

namespace UpcycleCity.Pollution
{
    /// <summary>
    /// What pollution looks like: the surface treatment every Pollution Lit material shares, the
    /// clean-up dissolve, and the atmosphere at both ends of the scale.
    /// </summary>
    [CreateAssetMenu(menuName = "Upcycle City/Pollution Profile", fileName = "PollutionProfile")]
    public sealed class PollutionProfile : ScriptableObject
    {
        [Header("Surfaces")]
        [Tooltip("Tint mixed into dirty colours (rgb) and how strongly (alpha).")]
        public Color smogTint = new Color(0.88f, 0.8f, 0.66f, 0.8f);
        [Tooltip("How much colour drains out of polluted surfaces.")]
        [Range(0f, 1f)] public float desaturation = 0.55f;
        [Range(0f, 1f)] public float darkening = 0.12f;
        [Tooltip("Dirt colour multiplied into surfaces (rgb) and its strength (alpha).")]
        public Color grimeColor = new Color(0.5f, 0.45f, 0.38f, 0.85f);
        [Tooltip("Grime blotches per metre. Too fine and it reads as wood grain.")]
        [Min(0.001f)] public float grimeScale = 0.36f;
        [Tooltip("How high street dirt splashes up walls, in metres.")]
        [Min(0.01f)] public float groundDirtHeight = 1.2f;
        [Tooltip("Colour that the green parts of plants dry out to.")]
        public Color deadFoliageColor = new Color(0.47f, 0.41f, 0.25f);

        [Header("Clean-up dissolve")]
        [Tooltip("Optional subtle rim on objects dissolving in or out (black = natural, no glow).")]
        [ColorUsage(false, true)] public Color dissolveEdgeColor = Color.black;
        [Tooltip("Width of the rim, in pollution levels.")]
        [Range(0.005f, 0.2f)] public float dissolveEdgeWidth = 0.005f;
        [Tooltip("Size of the dissolve pattern, in cells per metre.")]
        [Min(0.01f)] public float dissolveNoiseScale = 1.6f;
        [Tooltip("Range of pollution levels over which one object dissolves, patch by patch.")]
        [Range(0f, 0.6f)] public float dissolveSpread = 0.25f;
        [Tooltip("Spread of per-object thresholds, so objects appear and vanish one by one.")]
        [Range(0f, 1f)] public float dissolveStagger = 0.3f;

        [Header("Atmosphere")]
        public AtmosphereState clean = AtmosphereState.DefaultClean;
        public AtmosphereState polluted = AtmosphereState.DefaultPolluted;

        public AtmosphereState AtmosphereAt(float pollution) => AtmosphereState.Lerp(clean, polluted, pollution);
    }
}
