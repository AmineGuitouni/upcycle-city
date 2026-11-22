using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;
using UnityEngine.Jobs;

namespace UpcycleCity.Crowd
{
    /// <summary>One route, as a slice of the shared point, distance and clearance arrays.</summary>
    struct CrowdPathSpan
    {
        public int Start;    // first point
        public int Segments; // points Start .. Start + Segments; a loop repeats its first point at the end
        public float Length;
        public bool Loop;
    }

    /// <summary>A walker's progress along its route and how it is weaving through the people around it.</summary>
    struct CrowdWalkerState
    {
        public int Path;
        public float Distance;     // along the route
        public float Lateral;      // metres to the right of the route, right now
        public float Lane;         // preferred metres to the right of the walking direction
        public float Speed;        // cruising speed
        public float CurrentSpeed; // actual speed along the route (drops when held up)
        public float Direction;    // +1 along the route, -1 back
        public float Yaw;          // radians
        public float Blocked;      // seconds held up in a row
        public float Radius;
        public Vector3 Position;   // where it is drawn
        public bool HasPosition;
        public bool Active;        // moving (not hidden, not paused by a behaviour)
        public bool Present;       // in the world, so others avoid it (paused NPCs still count)
    }

    /// <summary>Tunables shared by every walker.</summary>
    struct CrowdSteering
    {
        public float KeepRight;      // extra offset to the right of the walking direction, so opposing flows split
        public float Sense;          // how far ahead walkers look for people in the way
        public float SidestepSpeed;  // m/s sideways
        public float Acceleration;   // m/s² along the route
        public float TurnBackDelay;  // seconds stuck before turning round
        public float Personal;       // extra space kept around others
    }

    /// <summary>Copies each walker's drawn position (and radius, or 0 when absent) for the neighbour grid.</summary>
    [BurstCompile]
    struct CrowdSnapshotJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<CrowdWalkerState> Walkers;
        [WriteOnly] public NativeArray<Vector4> Positions;

        public void Execute(int index)
        {
            CrowdWalkerState w = Walkers[index];
            Positions[index] = w.Present && w.HasPosition
                ? new Vector4(w.Position.x, w.Position.y, w.Position.z, w.Radius)
                : Vector4.zero;
        }
    }

    /// <summary>Buckets walkers into a hashed grid (counting sort), so each one only checks its neighbours.</summary>
    [BurstCompile]
    struct CrowdGridJob : IJob
    {
        [ReadOnly] public NativeArray<Vector4> Positions;
        public NativeArray<int> CellStart;
        public NativeArray<int> CellCount;
        public NativeArray<int> CellCursor;
        public NativeArray<int> Sorted;
        public NativeArray<int> WalkerCell;
        public float InvCellSize;

        public void Execute()
        {
            int mask = CellStart.Length - 1;
            for (int c = 0; c < CellCount.Length; c++)
                CellCount[c] = 0;

            for (int i = 0; i < Positions.Length; i++)
            {
                Vector4 p = Positions[i];
                if (p.w <= 0f)
                {
                    WalkerCell[i] = -1;
                    continue;
                }
                int cell = CrowdGrid.Hash(Mathf.FloorToInt(p.x * InvCellSize), Mathf.FloorToInt(p.z * InvCellSize)) & mask;
                WalkerCell[i] = cell;
                CellCount[cell] = CellCount[cell] + 1;
            }

            int running = 0;
            for (int c = 0; c < CellStart.Length; c++)
            {
                CellStart[c] = running;
                CellCursor[c] = running;
                running += CellCount[c];
            }

            for (int i = 0; i < Positions.Length; i++)
            {
                int cell = WalkerCell[i];
                if (cell < 0)
                    continue;
                int slot = CellCursor[cell];
                Sorted[slot] = i;
                CellCursor[cell] = slot + 1;
            }
        }
    }

    static class CrowdGrid
    {
        public static int Hash(int x, int z) => (x * 73856093) ^ (z * 19349663);
    }

    /// <summary>
    /// Moves every walker along its NavMesh route and steers it round the people and obstacles in its way:
    /// it sidesteps (keeping right when two meet head-on) within the room the route has either side, slows down
    /// and waits when there's no room, and turns back if it stays stuck. Burst-compiled transform job, so
    /// hundreds of walkers cost a fraction of a millisecond.
    /// </summary>
    [BurstCompile]
    struct CrowdMovementJob : IJobParallelForTransform
    {
        [ReadOnly] public NativeArray<Vector3> Points;
        [ReadOnly] public NativeArray<float> Distances; // distance along its route at each point
        [ReadOnly] public NativeArray<Vector2> Clearance; // room left (x) and right (y) at each point
        [ReadOnly] public NativeArray<CrowdPathSpan> Paths;
        [ReadOnly] public NativeArray<Vector4> Positions;
        [ReadOnly] public NativeArray<int> CellStart;
        [ReadOnly] public NativeArray<int> CellCount;
        [ReadOnly] public NativeArray<int> Sorted;
        [ReadOnly] public NativeArray<Vector4> Obstacles; // xyz + radius
        public int ObstacleCount;
        public NativeArray<CrowdWalkerState> Walkers;
        public CrowdSteering Steering;
        public float InvCellSize;
        public float DeltaTime;
        public float TurnBlend;
        public float PositionBlend;

        public void Execute(int index, TransformAccess transform)
        {
            CrowdWalkerState w = Walkers[index];
            if (!w.Active)
                return;

            float dt = DeltaTime;
            CrowdPathSpan path = Paths[w.Path];
            Sample(path, w.Distance, out Vector3 basePoint, out Vector3 tangent, out Vector2 room);
            w.Lateral = Mathf.Clamp(w.Lateral, -room.x, room.y);

            // Travel frame: forward along the way it walks, right of that
            Vector3 forward = tangent * w.Direction;
            var rightPath = new Vector3(tangent.z, 0f, -tangent.x);
            var rightTravel = rightPath * w.Direction;
            Vector3 here = basePoint + rightPath * w.Lateral;
            float travelLateral = w.Lateral * w.Direction;

            float preferred = w.Lane + Steering.KeepRight;
            float dodgeTarget = preferred;
            float dodgeWeight = 0f;
            float dodgeSide = 0f;   // side of the most urgent thing in the way
            float dodgeClear = 0f;  // how far to its side to pass it
            float brake = 1f;
            float push = 0f;

            float sense = Steering.Sense;
            float senseSqr = sense * sense;

            // Other walkers in the 3x3 cells around
            int mask = CellStart.Length - 1;
            int cx = Mathf.FloorToInt(here.x * InvCellSize), cz = Mathf.FloorToInt(here.z * InvCellSize);
            for (int dz = -1; dz <= 1; dz++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    int cell = CrowdGrid.Hash(cx + dx, cz + dz) & mask;
                    int start = CellStart[cell], end = start + CellCount[cell];
                    for (int s = start; s < end; s++)
                    {
                        int other = Sorted[s];
                        if (other == index)
                            continue;
                        Consider(Positions[other], here, forward, rightTravel, w.Radius, senseSqr,
                            ref dodgeWeight, ref dodgeSide, ref dodgeClear, ref brake, ref push);
                    }
                }
            }

            // Players and other dynamic obstacles
            for (int o = 0; o < ObstacleCount; o++)
                Consider(Obstacles[o], here, forward, rightTravel, w.Radius, senseSqr,
                    ref dodgeWeight, ref dodgeSide, ref dodgeClear, ref brake, ref push);

            // Pick the side to pass on (right by default, unless the thing is already to our right),
            // falling back to the other side when the pavement has no room there
            float minTravel = w.Direction > 0f ? -room.x : -room.y;
            float maxTravel = w.Direction > 0f ? room.y : room.x;
            bool dodgeBlocked = false;
            if (dodgeWeight > 0f)
            {
                float firstSide = dodgeSide > 0.1f ? -1f : 1f;
                float a = travelLateral + dodgeSide + firstSide * dodgeClear;
                float b = travelLateral + dodgeSide - firstSide * dodgeClear;
                float target = a;
                if (a < minTravel - 0.05f || a > maxTravel + 0.05f)
                {
                    target = b;
                    if (b < minTravel - 0.05f || b > maxTravel + 0.05f)
                    {
                        dodgeBlocked = true;
                        target = Mathf.Abs(a - Mathf.Clamp(a, minTravel, maxTravel)) <= Mathf.Abs(b - Mathf.Clamp(b, minTravel, maxTravel)) ? a : b;
                    }
                }
                dodgeTarget = Mathf.Lerp(preferred, target, Mathf.Sqrt(dodgeWeight));
            }
            if (!dodgeBlocked && dodgeWeight > 0f)
                brake = Mathf.Max(brake, 0.35f); // there's room to get round: keep shuffling forward while stepping aside

            float desired = Mathf.Clamp(dodgeTarget, minTravel, maxTravel) * w.Direction;
            float step = Steering.SidestepSpeed * dt;
            float lateralBefore = w.Lateral;
            w.Lateral += Mathf.Clamp(desired - w.Lateral, -step, step) + push * w.Direction * dt;

            // Speed along the route
            float targetSpeed = w.Speed * brake;
            w.CurrentSpeed = Mathf.MoveTowards(w.CurrentSpeed, targetSpeed, Steering.Acceleration * dt);

            // Held up for too long (a player standing in a narrow alley): turn round
            if (brake < 0.3f)
                w.Blocked += dt;
            else
                w.Blocked = Mathf.Max(0f, w.Blocked - dt * 2f);
            if (w.Blocked > Steering.TurnBackDelay)
            {
                w.Blocked = 0f;
                w.Direction = -w.Direction;
                w.CurrentSpeed = 0f;
            }

            w.Distance += w.CurrentSpeed * w.Direction * dt;
            if (path.Loop)
            {
                w.Distance = Repeat(w.Distance, path.Length);
            }
            else if (w.Distance > path.Length)
            {
                w.Distance = Mathf.Max(0f, 2f * path.Length - w.Distance);
                w.Direction = -1f;
            }
            else if (w.Distance < 0f)
            {
                w.Distance = Mathf.Min(path.Length, -w.Distance);
                w.Direction = 1f;
            }

            Sample(path, w.Distance, out basePoint, out tangent, out room);
            w.Lateral = Mathf.Clamp(w.Lateral, -room.x, room.y);
            rightPath = new Vector3(tangent.z, 0f, -tangent.x);
            Vector3 target3 = basePoint + rightPath * w.Lateral;

            // Smooth the drawn position, so corners of the route and sidesteps never pop
            w.Position = w.HasPosition ? Vector3.Lerp(w.Position, target3, PositionBlend) : target3;
            w.HasPosition = true;
            transform.position = w.Position;

            // Face the way it is actually going; keep the facing when standing still
            float lateralRate = dt > 1e-5f ? (w.Lateral - lateralBefore) / dt : 0f;
            Vector3 heading = tangent * (w.Direction * w.CurrentSpeed) + rightPath * lateralRate;
            if (heading.x * heading.x + heading.z * heading.z > 0.01f)
            {
                float targetYaw = Mathf.Atan2(heading.x, heading.z);
                w.Yaw += DeltaAngle(w.Yaw, targetYaw) * TurnBlend;
            }
            transform.rotation = new Quaternion(0f, Mathf.Sin(w.Yaw * 0.5f), 0f, Mathf.Cos(w.Yaw * 0.5f));

            Walkers[index] = w;
        }

        void Consider(Vector4 other, Vector3 here, Vector3 forward, Vector3 right, float radius, float senseSqr,
            ref float dodgeWeight, ref float dodgeSide, ref float dodgeClear, ref float brake, ref float push)
        {
            if (other.w <= 0f)
                return;
            float dx = other.x - here.x, dz = other.z - here.z;
            float sqr = dx * dx + dz * dz;
            if (sqr > senseSqr || sqr < 1e-6f || Mathf.Abs(other.y - here.y) > 1.5f)
                return;

            float ahead = dx * forward.x + dz * forward.z;
            float side = dx * right.x + dz * right.z;
            float reach = radius + other.w;
            float distance = Mathf.Sqrt(sqr);

            // Overlapping already: ease apart sideways
            if (distance < reach)
                push += (side > 0.05f ? -1f : 1f) * (reach - distance) * 4f;

            // In our lane ahead: plan to pass it, and slow down if it's close
            float pass = reach + Steering.Personal;
            if (ahead > -0.1f && Mathf.Abs(side) < pass)
            {
                float urgency = 1f - Mathf.Clamp01(ahead / Mathf.Sqrt(senseSqr));
                if (urgency > dodgeWeight)
                {
                    dodgeWeight = urgency;
                    dodgeSide = side;
                    dodgeClear = pass + 0.05f;
                }
                if (ahead > 0f && Mathf.Abs(side) < reach + 0.05f)
                    brake = Mathf.Min(brake, Mathf.Clamp01((ahead - reach - 0.1f) / 0.9f));
            }
        }

        void Sample(CrowdPathSpan path, float distance, out Vector3 position, out Vector3 tangent, out Vector2 room)
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
            room = Vector2.Min(Clearance[low], Clearance[low + 1]);

            float dx = b.x - a.x, dz = b.z - a.z;
            float length = Mathf.Sqrt(dx * dx + dz * dz);
            tangent = length > 1e-5f ? new Vector3(dx / length, 0f, dz / length) : new Vector3(0f, 0f, 1f);
        }

        static float Repeat(float value, float length) => value - Mathf.Floor(value / length) * length;

        static float DeltaAngle(float from, float to)
        {
            const float tau = 2f * Mathf.PI;
            return Repeat(to - from + Mathf.PI, tau) - Mathf.PI;
        }
    }
}
