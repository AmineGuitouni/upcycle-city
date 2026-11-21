using Unity.Burst;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Jobs;

namespace UpcycleCity.Crowd
{
    /// <summary>One path, as a slice of the shared point and distance arrays.</summary>
    struct CrowdPathSpan
    {
        public int Start;    // first point
        public int Segments; // points Start .. Start + Segments; a loop repeats its first point at the end
        public float Length;
        public bool Loop;
    }

    /// <summary>A walker's progress along its path.</summary>
    struct CrowdWalkerState
    {
        public int Path;
        public float Distance;
        public float Speed;
        public float Direction; // +1 along the path, -1 back
        public float Lane;      // metres to the right of the path
        public float Yaw;       // radians
        public bool Active;
    }

    /// <summary>
    /// Moves every walker along its path and turns it to face its way. Burst-compiled and run as a
    /// transform job, so thousands of walkers cost a fraction of a millisecond.
    /// </summary>
    [BurstCompile]
    struct CrowdMovementJob : IJobParallelForTransform
    {
        [ReadOnly] public NativeArray<Vector3> Points;
        [ReadOnly] public NativeArray<float> Distances; // distance along its path at each point
        [ReadOnly] public NativeArray<CrowdPathSpan> Paths;
        public NativeArray<CrowdWalkerState> Walkers;
        public float DeltaTime;
        public float TurnBlend;

        public void Execute(int index, TransformAccess transform)
        {
            CrowdWalkerState walker = Walkers[index];
            if (!walker.Active)
                return;

            CrowdPathSpan path = Paths[walker.Path];
            walker.Distance += walker.Speed * walker.Direction * DeltaTime;
            if (path.Loop)
            {
                walker.Distance = Repeat(walker.Distance, path.Length);
            }
            else if (walker.Distance > path.Length)
            {
                walker.Distance = Mathf.Max(0f, 2f * path.Length - walker.Distance);
                walker.Direction = -1f;
            }
            else if (walker.Distance < 0f)
            {
                walker.Distance = Mathf.Min(path.Length, -walker.Distance);
                walker.Direction = 1f;
            }

            Sample(path, walker.Distance, out Vector3 position, out Vector3 tangent);
            var right = new Vector3(tangent.z, 0f, -tangent.x);
            transform.position = position + right * walker.Lane;

            float targetYaw = Mathf.Atan2(tangent.x * walker.Direction, tangent.z * walker.Direction);
            walker.Yaw += DeltaAngle(walker.Yaw, targetYaw) * TurnBlend;
            transform.rotation = new Quaternion(0f, Mathf.Sin(walker.Yaw * 0.5f), 0f, Mathf.Cos(walker.Yaw * 0.5f));

            Walkers[index] = walker;
        }

        void Sample(CrowdPathSpan path, float distance, out Vector3 position, out Vector3 tangent)
        {
            // Binary search for the segment that contains the distance
            int low = path.Start, high = path.Start + path.Segments - 1;
            while (low < high)
            {
                int middle = (low + high + 1) >> 1;
                if (Distances[middle] <= distance)
                    low = middle;
                else
                    high = middle - 1;
            }

            Vector3 a = Points[low], b = Points[low + 1];
            float segment = Distances[low + 1] - Distances[low];
            float t = segment > 1e-5f ? Mathf.Clamp01((distance - Distances[low]) / segment) : 0f;
            position = a + (b - a) * t;

            float dx = b.x - a.x, dz = b.z - a.z;
            float length = Mathf.Sqrt(dx * dx + dz * dz);
            tangent = length > 1e-5f ? new Vector3(dx / length, 0f, dz / length) : new Vector3(0f, 0f, 1f);
        }

        static float Repeat(float value, float length) => value - Mathf.Floor(value / length) * length;

        static float DeltaAngle(float from, float to)
        {
            const float tau = 2f * Mathf.PI;
            float delta = Repeat(to - from + Mathf.PI, tau) - Mathf.PI;
            return delta;
        }
    }
}
