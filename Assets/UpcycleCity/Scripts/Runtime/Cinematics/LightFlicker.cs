using UnityEngine;

namespace UpcycleCity.Cinematics
{
    /// <summary>A tired street lamp: a gentle buzz in its brightness, and now and then a stutter or a drop-out.</summary>
    [AddComponentMenu("Upcycle City/Cinematics/Light Flicker")]
    [RequireComponent(typeof(Light))]
    public sealed class LightFlicker : MonoBehaviour
    {
        [SerializeField, Range(0f, 1f)] float buzz = 0.08f;
        [SerializeField] Vector2 stutterInterval = new Vector2(2.5f, 6f);
        [SerializeField, Min(0.05f)] float stutterLength = 0.45f;
        [Tooltip("Optional emissive renderer (the bulb) that flickers with the light.")]
        [SerializeField] Renderer bulb;

        static readonly int EmissionId = Shader.PropertyToID("_EmissionColor");
        Light lamp;
        float baseIntensity;
        float nextStutter, stutterEnd;
        MaterialPropertyBlock block;
        Color bulbColor = new Color(1f, 0.75f, 0.45f);

        void Awake()
        {
            lamp = GetComponent<Light>();
            baseIntensity = lamp.intensity;
            nextStutter = Time.time + Random.Range(stutterInterval.x, stutterInterval.y);
            block = new MaterialPropertyBlock();
        }

        void Update()
        {
            float k = 1f - buzz * Mathf.PerlinNoise(Time.time * 9f, 0.3f);
            if (Time.time >= nextStutter)
            {
                stutterEnd = Time.time + stutterLength;
                nextStutter = Time.time + Random.Range(stutterInterval.x, stutterInterval.y);
            }
            if (Time.time < stutterEnd)
                k *= Mathf.PerlinNoise(Time.time * 40f, 1.7f) > 0.5f ? 1f : 0.08f;
            lamp.intensity = baseIntensity * k;
            if (bulb != null)
            {
                block.SetColor(EmissionId, bulbColor * (2.5f * k));
                bulb.SetPropertyBlock(block);
            }
        }
    }
}
