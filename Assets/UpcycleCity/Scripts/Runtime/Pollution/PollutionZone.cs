using UnityEngine;

namespace UpcycleCity.Pollution
{
    /// <summary>
    /// A vertical cylinder where pollution differs from the scene-wide level: a district the player
    /// has cleaned, a polluted factory yard, or a growing clean-up wave (see <see cref="CleanupWave"/>).
    /// Zones are applied in the order they were enabled, each blending the level towards its own.
    /// </summary>
    [ExecuteAlways]
    [AddComponentMenu("Upcycle City/Pollution Zone")]
    public sealed class PollutionZone : MonoBehaviour
    {
        [Tooltip("Pollution inside the zone: 0 is clean, 1 is polluted.")]
        [SerializeField, Range(0f, 1f)] float pollution;

        [Tooltip("How much the zone overrides the scene-wide level. Animate it to fade the zone in or out.")]
        [SerializeField, Range(0f, 1f)] float weight = 1f;

        [Tooltip("Radius of the zone, in metres.")]
        [SerializeField, Min(0f)] float radius = 30f;

        [Tooltip("Width of the soft edge inside the radius, in metres.")]
        [SerializeField, Min(0.01f)] float falloff = 10f;

        public float Pollution
        {
            get => pollution;
            set { pollution = Mathf.Clamp01(value); PollutionController.NotifyZonesChanged(); }
        }

        public float Weight
        {
            get => weight;
            set { weight = Mathf.Clamp01(value); PollutionController.NotifyZonesChanged(); }
        }

        public float Radius
        {
            get => radius;
            set { radius = Mathf.Max(0f, value); PollutionController.NotifyZonesChanged(); }
        }

        public float Falloff
        {
            get => falloff;
            set { falloff = Mathf.Max(0.01f, value); PollutionController.NotifyZonesChanged(); }
        }

        internal Vector4 ShapeData
        {
            get
            {
                Vector3 centre = transform.position;
                return new Vector4(centre.x, centre.y, centre.z, radius);
            }
        }

        internal Vector4 SettingsData => new Vector4(pollution, falloff, weight, 0f);

        /// <summary>Blends <paramref name="level"/> towards this zone's pollution at a world position.</summary>
        internal float Apply(float level, Vector3 position)
        {
            Vector3 centre = transform.position;
            float distance = Vector2.Distance(new Vector2(position.x, position.z), new Vector2(centre.x, centre.z));
            float inside = 1f - SmoothStep(radius - falloff, radius, distance);
            return Mathf.Lerp(level, pollution, inside * weight);
        }

        // HLSL smoothstep, which Mathf.SmoothStep is not
        static float SmoothStep(float edge0, float edge1, float x)
        {
            float t = Mathf.Clamp01((x - edge0) / Mathf.Max(edge1 - edge0, 1e-5f));
            return t * t * (3f - 2f * t);
        }

        void OnEnable() => PollutionController.RegisterZone(this);

        void OnDisable() => PollutionController.UnregisterZone(this);

        void OnValidate() => PollutionController.NotifyZonesChanged();

        void Update()
        {
            if (transform.hasChanged)
            {
                transform.hasChanged = false;
                PollutionController.NotifyZonesChanged();
            }
        }

        void OnDrawGizmosSelected()
        {
            Gizmos.color = Color.Lerp(new Color(0.3f, 1f, 0.7f), new Color(0.6f, 0.45f, 0.2f), pollution);
            DrawCircle(radius);
            DrawCircle(Mathf.Max(0f, radius - falloff));
        }

        void DrawCircle(float circleRadius)
        {
            const int segments = 64;
            Vector3 centre = transform.position;
            Vector3 previous = centre + new Vector3(circleRadius, 0f, 0f);
            for (int i = 1; i <= segments; i++)
            {
                float angle = i * Mathf.PI * 2f / segments;
                Vector3 next = centre + new Vector3(Mathf.Cos(angle) * circleRadius, 0f, Mathf.Sin(angle) * circleRadius);
                Gizmos.DrawLine(previous, next);
                previous = next;
            }
        }
    }
}
