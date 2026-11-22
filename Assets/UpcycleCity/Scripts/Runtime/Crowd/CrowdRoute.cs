using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using UpcycleCity.Core;

namespace UpcycleCity.Crowd
{
    /// <summary>
    /// A <see cref="CrowdPath"/> made walkable: routed over the <see cref="CrowdNavMesh"/> so it goes around
    /// buildings and props, sampled about every metre, with the ground height and the room walkers have to
    /// either side at each sample (to the nav mesh edge, a solid obstacle or a kerb, whichever is nearest).
    /// </summary>
    sealed class CrowdRoute
    {
        public Vector3[] Points;
        public float[] Distances;
        /// <summary>x = room to the left of the route, y = room to the right, in metres.</summary>
        public Vector2[] Clearance;
        public float Length;
        public bool Loop;

        const float Spacing = 1f;
        const float ProbeStep = 0.25f;
        const float HeightTolerance = 0.12f;
        const float BodyMargin = 0.05f;
        const float SnapDistance = 2.5f;

        static NavMeshPath navPath;
        static readonly Vector3[] CornerBuffer = new Vector3[256];
        static readonly List<Vector3> Waypoints = new List<Vector3>();
        static readonly List<Vector3> Corners = new List<Vector3>();
        static readonly List<Vector3> Dense = new List<Vector3>();

        public static CrowdRoute Build(CrowdPath path, bool useNavMesh, float maxClearance, float walkerRadius)
        {
            Waypoints.Clear();
            for (int i = 0; i <= path.SegmentCount; i++) // a loop's last point is its first again
                Waypoints.Add(path.GetPoint(i));

            Corners.Clear();
            if (useNavMesh)
                RouteOverNavMesh();
            else
                Corners.AddRange(Waypoints);
            if (path.Loop && Corners.Count > 1)
                Corners[Corners.Count - 1] = Corners[0];

            Resample();

            int count = Dense.Count;
            var route = new CrowdRoute
            {
                Points = new Vector3[count],
                Distances = new float[count],
                Clearance = new Vector2[count],
                Loop = path.Loop,
            };

            float travelled = 0f;
            for (int i = 0; i < count; i++)
            {
                Vector3 p = Dense[i];
                p.y = GroundHeight(p);
                if (i > 0)
                    travelled += CrowdPath.FlatDistance(route.Points[i - 1], p);
                route.Points[i] = p;
                route.Distances[i] = travelled;
            }
            if (path.Loop && count > 1)
                route.Points[count - 1] = route.Points[0];
            route.Length = Mathf.Max(travelled, 1e-3f);

            for (int i = 0; i < count; i++)
            {
                Vector3 tangent = Tangent(route.Points, i, path.Loop);
                var right = new Vector3(tangent.z, 0f, -tangent.x);
                Vector3 p = route.Points[i];
                route.Clearance[i] = new Vector2(
                    Room(p, -right, useNavMesh, maxClearance, walkerRadius),
                    Room(p, right, useNavMesh, maxClearance, walkerRadius));
            }
            if (path.Loop && count > 1)
            {
                Vector2 a = route.Clearance[0], b = route.Clearance[count - 1];
                route.Clearance[0] = route.Clearance[count - 1] = Vector2.Min(a, b);
            }
            return route;
        }

        /// <summary>
        /// The point of the route nearest to a world position: its distance along the route, the sideways offset
        /// from it (positive to the right) and the room either side there.
        /// </summary>
        public void Project(Vector3 position, out float distance, out float offset, out Vector3 tangent, out Vector2 room)
        {
            distance = 0f;
            offset = 0f;
            tangent = Vector3.forward;
            room = Vector2.zero;
            float best = float.MaxValue;
            for (int i = 0; i < Points.Length - 1; i++)
            {
                Vector3 a = Points[i], b = Points[i + 1];
                float segment = Distances[i + 1] - Distances[i];
                if (segment < 1e-5f)
                    continue;
                var ab = new Vector3((b.x - a.x) / segment, 0f, (b.z - a.z) / segment);
                float ax = position.x - a.x, az = position.z - a.z;
                float along = Mathf.Clamp(ax * ab.x + az * ab.z, 0f, segment);
                float cx = a.x + ab.x * along, cz = a.z + ab.z * along;
                float sqr = (position.x - cx) * (position.x - cx) + (position.z - cz) * (position.z - cz);
                if (sqr < best)
                {
                    best = sqr;
                    distance = Distances[i] + along;
                    tangent = ab;
                    offset = ax * ab.z - az * ab.x;
                    room = Vector2.Min(Clearance[i], Clearance[i + 1]);
                }
            }
        }

        // ------------------------------------------------------------ building

        static void RouteOverNavMesh()
        {
            navPath ??= new NavMeshPath();
            for (int i = 0; i < Waypoints.Count; i++)
            {
                if (NavMesh.SamplePosition(Waypoints[i], out NavMeshHit hit, SnapDistance, NavMesh.AllAreas))
                    Waypoints[i] = hit.position;
            }

            Corners.Add(Waypoints[0]);
            for (int i = 0; i < Waypoints.Count - 1; i++)
            {
                Vector3 a = Waypoints[i], b = Waypoints[i + 1];
                int count = 0;
                if (NavMesh.CalculatePath(a, b, NavMesh.AllAreas, navPath) && navPath.status == NavMeshPathStatus.PathComplete)
                    count = navPath.GetCornersNonAlloc(CornerBuffer);

                // A route that wanders far off the drawn one means the waypoints sit on disconnected ground: keep the line
                if (count >= 2 && PolylineLength(count) <= CrowdPath.FlatDistance(a, b) * 2.5f + 8f)
                {
                    for (int k = 1; k < count; k++)
                        AddCorner(CornerBuffer[k]);
                }
                else
                {
                    AddCorner(b);
                }
            }
        }

        static void AddCorner(Vector3 p)
        {
            if (Corners.Count == 0 || CrowdPath.FlatDistance(Corners[Corners.Count - 1], p) > 0.05f)
                Corners.Add(p);
        }

        static float PolylineLength(int count)
        {
            float length = 0f;
            for (int i = 1; i < count; i++)
                length += CrowdPath.FlatDistance(CornerBuffer[i - 1], CornerBuffer[i]);
            return length;
        }

        static void Resample()
        {
            Dense.Clear();
            for (int i = 0; i < Corners.Count - 1; i++)
            {
                Vector3 a = Corners[i], b = Corners[i + 1];
                int steps = Mathf.Max(1, Mathf.CeilToInt(CrowdPath.FlatDistance(a, b) / Spacing));
                for (int k = 0; k < steps; k++)
                    Dense.Add(Vector3.Lerp(a, b, k / (float)steps));
            }
            if (Corners.Count > 0)
                Dense.Add(Corners[Corners.Count - 1]);
            if (Dense.Count == 1)
                Dense.Add(Dense[0] + Vector3.forward * 0.01f);
        }

        static Vector3 Tangent(Vector3[] points, int i, bool loop)
        {
            int last = points.Length - 1;
            Vector3 prev = i > 0 ? points[i - 1] : loop && last > 1 ? points[last - 1] : points[i];
            Vector3 next = i < last ? points[i + 1] : loop && last > 1 ? points[1] : points[i];
            var d = new Vector3(next.x - prev.x, 0f, next.z - prev.z);
            return d.sqrMagnitude > 1e-8f ? d.normalized : Vector3.forward;
        }

        static float GroundHeight(Vector3 p)
        {
            return Physics.Raycast(new Vector3(p.x, p.y + 0.6f, p.z), Vector3.down, out RaycastHit hit, 1.6f,
                GameLayers.GroundMask, QueryTriggerInteraction.Ignore)
                ? hit.point.y
                : p.y;
        }

        // How far a walker can step from p towards dir: to the nav mesh edge, a solid obstacle or a change of level
        static float Room(Vector3 p, Vector3 dir, bool useNavMesh, float max, float radius)
        {
            float limit = max;
            if (useNavMesh && NavMesh.SamplePosition(p, out NavMeshHit start, 0.6f, NavMesh.AllAreas)
                && NavMesh.Raycast(start.position, start.position + dir * max, out NavMeshHit edge, NavMesh.AllAreas))
            {
                limit = Mathf.Min(limit, edge.distance);
            }

            // Anything solid the nav mesh doesn't know about, at waist height
            if (Physics.Raycast(new Vector3(p.x, p.y + 0.9f, p.z), dir, out RaycastHit wall, limit + radius,
                    GameLayers.ObstacleMask, QueryTriggerInteraction.Ignore))
            {
                limit = Mathf.Min(limit, wall.distance - radius);
            }

            // Stay on this level: stop at kerbs, steps and drops
            for (float d = ProbeStep; d <= limit + 1e-3f; d += ProbeStep)
            {
                Vector3 q = p + dir * d;
                if (!Physics.Raycast(new Vector3(q.x, p.y + 0.5f, q.z), Vector3.down, out RaycastHit ground, 1f,
                        GameLayers.GroundMask, QueryTriggerInteraction.Ignore)
                    || Mathf.Abs(ground.point.y - p.y) > HeightTolerance)
                {
                    limit = d - ProbeStep;
                    break;
                }
            }
            return Mathf.Max(0f, limit - BodyMargin);
        }
    }
}
