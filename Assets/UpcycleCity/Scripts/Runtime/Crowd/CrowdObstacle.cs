using System.Collections.Generic;
using UnityEngine;

namespace UpcycleCity.Crowd
{
    /// <summary>
    /// Something moving or temporary that walking NPCs step around: players (local and remote), carts,
    /// freshly placed props. Static scenery doesn't need one; it is baked into the <see cref="CrowdNavMesh"/>.
    /// Walkers sidestep it when there is room on the pavement, or slow down, wait and turn back when there isn't.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Upcycle City/Crowd/Crowd Obstacle")]
    public sealed class CrowdObstacle : MonoBehaviour
    {
        [Tooltip("Radius walkers keep clear of, in metres.")]
        [SerializeField, Min(0.05f)] float radius = 0.45f;

        internal static readonly List<CrowdObstacle> All = new List<CrowdObstacle>();

        public float Radius
        {
            get => radius;
            set => radius = Mathf.Max(0.05f, value);
        }

        void OnEnable() => All.Add(this);

        void OnDisable() => All.Remove(this);

        // Statics survive Play mode when domain reload is off
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetRegistry() => All.Clear();

        void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(1f, 0.48f, 0.1f, 0.8f);
            Vector3 c = transform.position;
            const int steps = 24;
            for (int i = 0; i < steps; i++)
            {
                float a = i * Mathf.PI * 2f / steps, b = (i + 1) * Mathf.PI * 2f / steps;
                Gizmos.DrawLine(c + new Vector3(Mathf.Sin(a) * radius, 0.05f, Mathf.Cos(a) * radius),
                                c + new Vector3(Mathf.Sin(b) * radius, 0.05f, Mathf.Cos(b) * radius));
            }
        }
    }
}
