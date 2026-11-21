using System.Collections.Generic;
using UnityEngine;

namespace UpcycleCity.Crowd
{
    /// <summary>
    /// A route for walking NPCs, such as a pavement: a polyline walked back and forth, or round and
    /// round when looped. Points are stored in local space, so a path can be moved as a whole.
    /// </summary>
    [AddComponentMenu("Upcycle City/Crowd/Crowd Path")]
    public sealed class CrowdPath : MonoBehaviour
    {
        [SerializeField] List<Vector3> points = new List<Vector3> { new Vector3(-5f, 0f, 0f), new Vector3(5f, 0f, 0f) };

        [Tooltip("Walk round and round instead of turning back at the ends.")]
        [SerializeField] bool loop;

        public bool Loop => loop;
        public int PointCount => points.Count;
        public int SegmentCount => points.Count < 2 ? 0 : loop ? points.Count : points.Count - 1;

        public Vector3 GetPoint(int index) => transform.TransformPoint(points[index % points.Count]);

        public float Length
        {
            get
            {
                float length = 0f;
                for (int i = 0; i < SegmentCount; i++)
                    length += FlatDistance(GetPoint(i), GetPoint(i + 1));
                return length;
            }
        }

        /// <summary>Replaces the route with world-space points.</summary>
        public void SetPoints(IReadOnlyList<Vector3> worldPoints, bool isLoop)
        {
            points.Clear();
            foreach (Vector3 point in worldPoints)
                points.Add(transform.InverseTransformPoint(point));
            loop = isLoop;
        }

        /// <summary>The world position at a distance along the path, and the path's direction there.</summary>
        public Vector3 PointAt(float distance, out Vector3 tangent)
        {
            tangent = Vector3.forward;
            for (int i = 0; i < SegmentCount; i++)
            {
                Vector3 a = GetPoint(i), b = GetPoint(i + 1);
                float segment = FlatDistance(a, b);
                if (distance <= segment || i == SegmentCount - 1)
                {
                    tangent = Flat(b - a);
                    return Vector3.Lerp(a, b, segment > 1e-5f ? Mathf.Clamp01(distance / segment) : 0f);
                }
                distance -= segment;
            }
            return points.Count > 0 ? GetPoint(0) : transform.position;
        }

        /// <summary>
        /// The point of the path nearest to a world position: its distance along the path, the signed
        /// sideways offset from it (positive to the right of the path's direction) and the direction there.
        /// </summary>
        public void Project(Vector3 position, out float distance, out float offset, out Vector3 tangent)
        {
            distance = 0f;
            offset = 0f;
            tangent = Vector3.forward;
            float best = float.MaxValue, travelled = 0f;
            for (int i = 0; i < SegmentCount; i++)
            {
                Vector3 a = GetPoint(i), b = GetPoint(i + 1);
                Vector3 ab = Flat(b - a);
                float segment = FlatDistance(a, b);
                Vector3 ap = position - a;
                float along = Mathf.Clamp(ap.x * ab.x + ap.z * ab.z, 0f, segment);
                Vector3 closest = a + ab * along;
                float sqr = (position.x - closest.x) * (position.x - closest.x) + (position.z - closest.z) * (position.z - closest.z);
                if (sqr < best)
                {
                    best = sqr;
                    distance = travelled + along;
                    tangent = ab;
                    offset = ap.x * ab.z - ap.z * ab.x; // right of the path is +
                }
                travelled += segment;
            }
        }

        // Distances are measured on the ground plane, so steps up a kerb don't change walking speed
        public static float FlatDistance(Vector3 a, Vector3 b)
        {
            float dx = b.x - a.x, dz = b.z - a.z;
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        static Vector3 Flat(Vector3 v)
        {
            v.y = 0f;
            return v.sqrMagnitude > 1e-10f ? v.normalized : Vector3.forward;
        }

        void OnDrawGizmos()
        {
            if (points.Count < 2)
                return;
            Gizmos.color = new Color(0.3f, 0.85f, 1f, 0.8f);
            for (int i = 0; i < SegmentCount; i++)
                Gizmos.DrawLine(GetPoint(i) + Vector3.up * 0.05f, GetPoint(i + 1) + Vector3.up * 0.05f);
            for (int i = 0; i < points.Count; i++)
                Gizmos.DrawSphere(GetPoint(i) + Vector3.up * 0.05f, 0.12f);
        }
    }
}
