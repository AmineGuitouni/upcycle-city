using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Jobs;
using UpcycleCity.Core;
using UpcycleCity.Pollution;

namespace UpcycleCity.Crowd
{
    /// <summary>
    /// Runs the whole crowd from one place, so its cost grows gently with the number of NPCs:
    /// <list type="bullet">
    /// <item>Walkers move in one Burst-compiled transform job, not in per-NPC Update calls. Their routes are
    /// planned over the <see cref="CrowdNavMesh"/>, and they steer round players, each other and
    /// <see cref="CrowdObstacle"/>s within the room the pavement has either side.</item>
    /// <item>Only the nearest visible NPCs, up to <see cref="animatedBudget"/>, get an animated rig, borrowed
    /// from a pool; everyone else shows a posed mesh that costs no more than a prop. Animators that are
    /// off screen are not evaluated.</item>
    /// <item>NPCs the pollution level has dissolved away are switched off completely.</item>
    /// </list>
    /// Put one in a scene with <see cref="CrowdAgent"/>s.
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(-100)]
    [AddComponentMenu("Upcycle City/Crowd/Crowd System")]
    public sealed class CrowdSystem : MonoBehaviour
    {
        static readonly int[] StateHashes =
        {
            Animator.StringToHash(nameof(CrowdActivity.Idle)),
            Animator.StringToHash(nameof(CrowdActivity.Walk)),
            Animator.StringToHash(nameof(CrowdActivity.Sit)),
        };

        [Header("Animation")]
        [Tooltip("Animator Controller with Idle, Walk and Sit states.")]
        [SerializeField] RuntimeAnimatorController animatorController;

        [Tooltip("Ground speed of the Walk clip at normal playback, in m/s. Each walker's playback is scaled from it, so feet don't slide.")]
        [SerializeField, Min(0.1f)] float walkClipSpeed = 1.6f;

        [Tooltip("Random spread of Idle and Sit playback speed, so neighbours don't move in step.")]
        [SerializeField, Range(0f, 0.3f)] float playbackVariation = 0.1f;

        [Header("Level of detail")]
        [Tooltip("Most NPCs animated at once. The nearest visible ones get an animated rig and the rest keep their posed mesh, so the animation cost stays flat however big the crowd.")]
        [SerializeField, Min(0)] int animatedBudget = 96;

        [Tooltip("NPCs further than this from the camera are never animated, in metres.")]
        [SerializeField, Min(1f)] float animateDistance = 140f;

        [Tooltip("Seconds between level-of-detail updates.")]
        [SerializeField, Min(0.02f)] float lodInterval = 0.2f;

        [Tooltip("Camera the level of detail is measured from. Defaults to the main camera.")]
        [SerializeField] Camera viewCamera;

        [Header("Walking")]
        [Tooltip("How quickly walkers turn to face their way, per second.")]
        [SerializeField, Min(0.1f)] float turnRate = 6f;

        [Header("Navigation & avoidance")]
        [Tooltip("Route walkers over a Crowd Nav Mesh. With none in the scene, one is built in the background at start.")]
        [SerializeField] bool useNavMesh = true;

        [Tooltip("Body radius of a walker, in metres.")]
        [SerializeField, Range(0.15f, 0.6f)] float walkerRadius = 0.3f;

        [Tooltip("Furthest a walker strays from its route to make way, in metres.")]
        [SerializeField, Range(0.2f, 4f)] float maxSidestep = 1.6f;

        [Tooltip("Walkers keep this far to the right of their way, so people walking opposite ways pass each other.")]
        [SerializeField, Range(0f, 1f)] float keepRight = 0.25f;

        [Tooltip("How far ahead walkers notice people and obstacles in their way, in metres.")]
        [SerializeField, Range(0.5f, 3f)] float lookAhead = 2.6f;

        [Tooltip("Extra space walkers leave when passing someone, in metres.")]
        [SerializeField, Range(0f, 1f)] float personalSpace = 0.3f;

        [Tooltip("Sideways stepping speed, in m/s.")]
        [SerializeField, Range(0.2f, 3f)] float sidestepSpeed = 1.1f;

        [Tooltip("How quickly walkers slow down and speed up, in m/s².")]
        [SerializeField, Range(0.5f, 10f)] float acceleration = 2.5f;

        [Tooltip("Seconds a walker waits behind something it can't get round before turning back.")]
        [SerializeField, Range(0.5f, 10f)] float turnBackDelay = 3.5f;

        [Tooltip("Below this speed a walker plays its idle animation instead of walking on the spot, in m/s.")]
        [SerializeField, Range(0f, 0.5f)] float stopSpeed = 0.15f;

        static readonly HashSet<CrowdAgent> Registry = new HashSet<CrowdAgent>();
        static bool registryChanged;

        static readonly Comparison<Slot> ByPriority = (a, b) => a.Priority.CompareTo(b.Priority);

        // Shown in the Profiler, to see what the crowd costs
        static readonly ProfilerMarker UpdateMarker = new ProfilerMarker("Crowd.Update");
        static readonly ProfilerMarker LateUpdateMarker = new ProfilerMarker("Crowd.LateUpdate");
        static readonly ProfilerMarker RebuildMarker = new ProfilerMarker("Crowd.Rebuild");

        /// <summary>Everything the system knows about one agent.</summary>
        sealed class Slot
        {
            public CrowdAgent Agent;
            public Transform Transform;
            public Renderer[] StaticRenderers;
            public PollutionVisibility Visibility;
            public uint UserValue;
            public int StateHash;
            public float Playback;
            public CrowdRig Rig;
            public bool Hidden;
            public bool PausedByBehavior;
            public float Priority;
            public int Walker = -1;
            public bool WalkerReady;
            public bool Stopped; // walker held up, showing idle
            public CrowdWalkerState WalkerState;
        }

        const float CellSize = 3f;
        static readonly int IdleHash = StateHashes[(int)CrowdActivity.Idle];
        static readonly int WalkHash = StateHashes[(int)CrowdActivity.Walk];

        readonly List<Slot> slots = new List<Slot>();
        readonly Dictionary<CrowdAgent, Slot> slotByAgent = new Dictionary<CrowdAgent, Slot>();
        readonly List<Slot> walkerSlots = new List<Slot>();
        readonly List<Slot> candidates = new List<Slot>();
        readonly Plane[] frustum = new Plane[6];
        readonly System.Random random = new System.Random(1);

        NativeArray<Vector3> points;
        NativeArray<float> distances;
        NativeArray<Vector2> clearance;
        NativeArray<CrowdPathSpan> paths;
        NativeArray<CrowdWalkerState> walkers;
        NativeArray<Vector4> positions;
        NativeArray<int> cellStart, cellCount, cellCursor, sorted, walkerCell;
        NativeArray<Vector4> obstacles;
        TransformAccessArray walkerTransforms;
        JobHandle movement;

        readonly Dictionary<CrowdPath, CrowdRoute> routes = new Dictionary<CrowdPath, CrowdRoute>();
        int routeVersion = int.MinValue;

        CrowdRigPool pool;
        float lodTimer;
        bool pollutionChanged = true;

        public static CrowdSystem Active { get; private set; }

        public int AgentCount => slots.Count;
        public int WalkerCount => walkerSlots.Count;
        public int AnimatedCount { get; private set; }

        public int AnimatedBudget
        {
            get => animatedBudget;
            set => animatedBudget = Mathf.Max(0, value);
        }

        /// <summary><see cref="AnimatedBudget"/> capped by the player's graphics level.</summary>
        public int EffectiveAnimatedBudget => Mathf.Min(animatedBudget, GraphicsQuality.Current.CrowdAnimatedMax);

        /// <summary>
        /// Updates a live agent's activity, movement pause, speed multiplier, and facing direction
        /// in-place without reallocating or rebuilding the Burst walker buffers.
        /// </summary>
        public bool SetAgentBehavior(
            CrowdAgent agent,
            CrowdActivity activity,
            bool paused,
            float speedMultiplier = 1f,
            Vector3? lookAtWorldPos = null)
        {
            if (agent == null || !slotByAgent.TryGetValue(agent, out Slot slot))
                return false;

            movement.Complete();
            slot.PausedByBehavior = paused;

            float clampedSpeedMult = Mathf.Clamp(speedMultiplier, 0.25f, 2f);
            float effectiveSpeed = agent.WalkSpeed * clampedSpeedMult;

            if (slot.Walker >= 0 && walkers.IsCreated && slot.Walker < walkers.Length)
            {
                CrowdWalkerState walker = walkers[slot.Walker];
                walker.Active = !slot.Hidden && !paused;
                walker.Speed = effectiveSpeed;
                if (paused)
                    walker.CurrentSpeed = 0f;
                walker.Blocked = 0f;

                if (paused && lookAtWorldPos.HasValue)
                {
                    Vector3 toTarget = lookAtWorldPos.Value - slot.Transform.position;
                    toTarget.y = 0f;
                    if (toTarget.sqrMagnitude > 0.01f)
                    {
                        float yaw = Mathf.Atan2(toTarget.x, toTarget.z);
                        walker.Yaw = yaw;
                        slot.Transform.rotation = Quaternion.Euler(0f, yaw * Mathf.Rad2Deg, 0f);
                    }
                }

                walkers[slot.Walker] = walker;
                slot.WalkerState = walker;
            }

            int newHash = StateHashes[(int)activity];
            float newPlayback = activity == CrowdActivity.Walk
                ? effectiveSpeed / walkClipSpeed
                : 1f;

            bool stateChanged = slot.StateHash != newHash || slot.Stopped;
            slot.StateHash = newHash;
            slot.Playback = newPlayback;
            slot.Stopped = false;

            if (slot.Rig != null && slot.Rig.Animator != null)
            {
                slot.Rig.Animator.speed = newPlayback;
                if (stateChanged)
                    slot.Rig.Animator.CrossFade(newHash, 0.2f, 0);
            }

            return true;
        }

        internal static void Register(CrowdAgent agent)
        {
            if (Registry.Add(agent))
                registryChanged = true;
        }

        internal static void Unregister(CrowdAgent agent)
        {
            if (Registry.Remove(agent))
                registryChanged = true;
        }

        // Statics survive Play mode when domain reload is off
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetRegistry()
        {
            Active = null;
            Registry.Clear();
            registryChanged = true;
        }

        void OnEnable()
        {
            Active = this;
            var storage = new GameObject("Animated Rig Pool").transform;
            storage.SetParent(transform, false);
            pool = new CrowdRigPool(storage, animatorController);
            PollutionController.Changed += OnPollutionChanged;
            registryChanged = true;
        }

        void Start()
        {
            // No baked nav mesh in the scene: build one in the background, walkers re-route when it's ready
            if (useNavMesh && CrowdNavMesh.Active == null)
                gameObject.AddComponent<CrowdNavMesh>();
        }

        void OnDisable()
        {
            if (Active == this)
                Active = null;
            PollutionController.Changed -= OnPollutionChanged;
            movement.Complete();
            if (gameObject.activeInHierarchy) // only the component was turned off: give the posed meshes back
            {
                foreach (Slot slot in slots)
                {
                    if (slot.Rig != null)
                        ReleaseRig(slot);
                    SetStaticVisible(slot, true);
                }
            }
            slots.Clear();
            slotByAgent.Clear();
            walkerSlots.Clear();
            DisposeWalkerData();
            if (obstacles.IsCreated) obstacles.Dispose();
            routes.Clear();
            routeVersion = int.MinValue;
        }

        void OnPollutionChanged(PollutionController controller) => pollutionChanged = true;

        void Update()
        {
            using var _ = UpdateMarker.Auto();
            int navVersion = useNavMesh ? CrowdNavMesh.Version : 0;
            if (navVersion != routeVersion)
            {
                // The nav mesh appeared or changed: re-plan every route and put walkers back on them where they stand
                routeVersion = navVersion;
                routes.Clear();
                foreach (Slot slot in slots)
                    slot.WalkerReady = false;
                registryChanged = true;
            }
            if (registryChanged)
                Rebuild();
            if (walkerSlots.Count == 0)
                return;

            int obstacleCount = GatherObstacles();
            float deltaTime = Time.deltaTime;
            float invCell = 1f / CellSize;

            JobHandle snapshot = new CrowdSnapshotJob { Walkers = walkers, Positions = positions }
                .Schedule(walkers.Length, 64);
            JobHandle grid = new CrowdGridJob
            {
                Positions = positions,
                CellStart = cellStart,
                CellCount = cellCount,
                CellCursor = cellCursor,
                Sorted = sorted,
                WalkerCell = walkerCell,
                InvCellSize = invCell,
            }.Schedule(snapshot);
            movement = new CrowdMovementJob
            {
                Points = points,
                Distances = distances,
                Clearance = clearance,
                Paths = paths,
                Positions = positions,
                CellStart = cellStart,
                CellCount = cellCount,
                Sorted = sorted,
                Obstacles = obstacles,
                ObstacleCount = obstacleCount,
                Walkers = walkers,
                Steering = new CrowdSteering
                {
                    KeepRight = keepRight,
                    Sense = Mathf.Min(lookAhead, CellSize),
                    SidestepSpeed = sidestepSpeed,
                    Acceleration = acceleration,
                    TurnBackDelay = turnBackDelay,
                    Personal = personalSpace,
                },
                InvCellSize = invCell,
                DeltaTime = deltaTime,
                TurnBlend = 1f - Mathf.Exp(-turnRate * deltaTime),
                PositionBlend = 1f - Mathf.Exp(-12f * deltaTime),
            }.Schedule(walkerTransforms, grid);
            JobHandle.ScheduleBatchedJobs();
        }

        void LateUpdate()
        {
            using var _ = LateUpdateMarker.Auto();
            movement.Complete();
            UpdateWalkerAnimation();
            if (pollutionChanged)
                UpdateHidden();

            lodTimer -= Time.deltaTime;
            if (lodTimer <= 0f)
            {
                lodTimer = lodInterval;
                UpdateLevelOfDetail();
            }
        }

        // ------------------------------------------------------------ agents

        void Rebuild()
        {
            using var _ = RebuildMarker.Auto();
            registryChanged = false;
            movement.Complete();
            for (int i = 0; i < walkerSlots.Count; i++)
                walkerSlots[i].WalkerState = walkers[i];

            for (int i = slots.Count - 1; i >= 0; i--)
            {
                Slot slot = slots[i];
                if (Registry.Contains(slot.Agent))
                    continue;
                if (slot.Rig != null && slot.Rig.Instance != null && slot.Agent != null)
                    pool.Return(slot.Rig);
                slotByAgent.Remove(slot.Agent);
                slots.RemoveAt(i);
            }

            foreach (CrowdAgent agent in Registry)
            {
                if (!slotByAgent.ContainsKey(agent))
                    AddSlot(agent);
            }

            RebuildWalkers();
            pollutionChanged = true;
            lodTimer = 0f;
        }

        void AddSlot(CrowdAgent agent)
        {
            CrowdActivity activity = agent.Activity == CrowdActivity.Walk && !agent.IsWalker ? CrowdActivity.Idle : agent.Activity;
            var slot = new Slot
            {
                Agent = agent,
                Transform = agent.transform,
                StaticRenderers = (agent.StaticVisual != null ? agent.StaticVisual : agent.gameObject).GetComponentsInChildren<Renderer>(true),
                Visibility = agent.GetComponent<PollutionVisibility>(),
                StateHash = StateHashes[(int)activity],
                Playback = activity == CrowdActivity.Walk
                    ? agent.WalkSpeed / walkClipSpeed
                    : 1f + ((float)random.NextDouble() * 2f - 1f) * playbackVariation,
            };
            if (slot.Visibility != null)
                slot.UserValue = PollutionVisibility.Encode(slot.Visibility.Mode, slot.Visibility.Threshold);
            slots.Add(slot);
            slotByAgent.Add(agent, slot);
        }

        void RebuildWalkers()
        {
            DisposeWalkerData();
            walkerSlots.Clear();

            var pathIndex = new Dictionary<CrowdPath, int>();
            var pointList = new List<Vector3>();
            var distanceList = new List<float>();
            var clearanceList = new List<Vector2>();
            var spans = new List<CrowdPathSpan>();
            foreach (Slot slot in slots)
            {
                slot.Walker = -1;
                if (!slot.Agent.IsWalker)
                    continue;

                CrowdPath path = slot.Agent.Path;
                CrowdRoute route = RouteFor(path);
                if (!pathIndex.TryGetValue(path, out int index))
                {
                    index = spans.Count;
                    pathIndex.Add(path, index);
                    spans.Add(AppendRoute(route, pointList, distanceList, clearanceList));
                }

                if (!slot.WalkerReady)
                    PlaceOnRoute(slot, route);
                slot.WalkerState.Path = index;
                slot.WalkerState.Radius = walkerRadius;
                slot.WalkerState.Active = !slot.Hidden && !slot.PausedByBehavior;
                slot.WalkerState.Present = !slot.Hidden;
                slot.Walker = walkerSlots.Count;
                walkerSlots.Add(slot);
            }

            if (walkerSlots.Count == 0)
                return;

            points = new NativeArray<Vector3>(pointList.ToArray(), Allocator.Persistent);
            distances = new NativeArray<float>(distanceList.ToArray(), Allocator.Persistent);
            clearance = new NativeArray<Vector2>(clearanceList.ToArray(), Allocator.Persistent);
            paths = new NativeArray<CrowdPathSpan>(spans.ToArray(), Allocator.Persistent);
            walkers = new NativeArray<CrowdWalkerState>(walkerSlots.Count, Allocator.Persistent);
            positions = new NativeArray<Vector4>(walkerSlots.Count, Allocator.Persistent);
            sorted = new NativeArray<int>(walkerSlots.Count, Allocator.Persistent);
            walkerCell = new NativeArray<int>(walkerSlots.Count, Allocator.Persistent);
            int cells = Mathf.NextPowerOfTwo(Mathf.Max(64, walkerSlots.Count * 2));
            cellStart = new NativeArray<int>(cells, Allocator.Persistent);
            cellCount = new NativeArray<int>(cells, Allocator.Persistent);
            cellCursor = new NativeArray<int>(cells, Allocator.Persistent);
            if (!obstacles.IsCreated)
                obstacles = new NativeArray<Vector4>(16, Allocator.Persistent);
            var transforms = new Transform[walkerSlots.Count];
            for (int i = 0; i < walkerSlots.Count; i++)
            {
                walkers[i] = walkerSlots[i].WalkerState;
                transforms[i] = walkerSlots[i].Transform;
            }
            walkerTransforms = new TransformAccessArray(transforms);
        }

        CrowdRoute RouteFor(CrowdPath path)
        {
            if (!routes.TryGetValue(path, out CrowdRoute route))
            {
                route = CrowdRoute.Build(path, useNavMesh && CrowdNavMesh.IsReady, maxSidestep, walkerRadius);
                routes.Add(path, route);
            }
            return route;
        }

        static CrowdPathSpan AppendRoute(CrowdRoute route, List<Vector3> pointList, List<float> distanceList, List<Vector2> clearanceList)
        {
            var span = new CrowdPathSpan
            {
                Start = pointList.Count,
                Segments = route.Points.Length - 1,
                Length = route.Length,
                Loop = route.Loop,
            };
            pointList.AddRange(route.Points);
            distanceList.AddRange(route.Distances);
            clearanceList.AddRange(route.Clearance);
            return span;
        }

        // Walkers start from the point of the route nearest to where they stand, facing their way
        void PlaceOnRoute(Slot slot, CrowdRoute route)
        {
            route.Project(slot.Transform.position, out float distance, out float offset, out Vector3 tangent, out Vector2 room);
            Vector3 forward = slot.Transform.forward;
            float direction = forward.x * tangent.x + forward.z * tangent.z >= 0f ? 1f : -1f;
            CrowdWalkerState previous = slot.WalkerState;
            bool keepSpeed = slot.WalkerReady || previous.Speed > 0f;
            slot.WalkerState = new CrowdWalkerState
            {
                Distance = distance,
                Lateral = Mathf.Clamp(offset, -room.x, room.y),
                Lane = Mathf.Clamp(offset * direction, -0.6f, 0.6f),
                Speed = keepSpeed ? previous.Speed : slot.Agent.WalkSpeed,
                CurrentSpeed = keepSpeed ? previous.CurrentSpeed : slot.Agent.WalkSpeed,
                Direction = direction,
                Yaw = Mathf.Atan2(forward.x, forward.z),
                Radius = walkerRadius,
            };
            slot.WalkerReady = true;
        }

        // Players and other dynamic obstacles, copied for the job (no allocation unless the count grows)
        int GatherObstacles()
        {
            List<CrowdObstacle> all = CrowdObstacle.All;
            if (all.Count > obstacles.Length)
            {
                obstacles.Dispose();
                obstacles = new NativeArray<Vector4>(Mathf.NextPowerOfTwo(all.Count), Allocator.Persistent);
            }
            int count = 0;
            for (int i = 0; i < all.Count; i++)
            {
                CrowdObstacle obstacle = all[i];
                if (obstacle == null)
                    continue;
                Vector3 p = obstacle.transform.position;
                obstacles[count++] = new Vector4(p.x, p.y, p.z, obstacle.Radius);
            }
            return count;
        }

        // Walkers held up by someone show idle instead of walking on the spot, and walk at their real pace
        void UpdateWalkerAnimation()
        {
            for (int i = 0; i < walkerSlots.Count; i++)
            {
                Slot slot = walkerSlots[i];
                if (slot.Rig == null || slot.Rig.Animator == null || slot.StateHash != WalkHash || slot.PausedByBehavior)
                    continue;
                CrowdWalkerState walker = walkers[i];
                if (!walker.Active)
                    continue;

                Animator animator = slot.Rig.Animator;
                bool stopped = walker.CurrentSpeed < (slot.Stopped ? stopSpeed * 1.5f : stopSpeed);
                if (stopped != slot.Stopped)
                {
                    slot.Stopped = stopped;
                    animator.CrossFade(stopped ? IdleHash : WalkHash, 0.25f, 0);
                }
                animator.speed = stopped ? 1f : Mathf.Max(0.3f, walker.CurrentSpeed / walkClipSpeed);
            }
        }

        void DisposeWalkerData()
        {
            if (points.IsCreated) points.Dispose();
            if (distances.IsCreated) distances.Dispose();
            if (clearance.IsCreated) clearance.Dispose();
            if (paths.IsCreated) paths.Dispose();
            if (walkers.IsCreated) walkers.Dispose();
            if (positions.IsCreated) positions.Dispose();
            if (sorted.IsCreated) sorted.Dispose();
            if (walkerCell.IsCreated) walkerCell.Dispose();
            if (cellStart.IsCreated) cellStart.Dispose();
            if (cellCount.IsCreated) cellCount.Dispose();
            if (cellCursor.IsCreated) cellCursor.Dispose();
            if (walkerTransforms.isCreated) walkerTransforms.Dispose();
        }

        // ------------------------------------------------------------ pollution

        void UpdateHidden()
        {
            pollutionChanged = false;
            PollutionController controller = PollutionController.Active;
            float margin = HideMargin(controller);

            foreach (Slot slot in slots)
            {
                bool hidden = false;
                if (slot.Visibility != null && slot.Visibility.isActiveAndEnabled && controller != null)
                {
                    float pollution = controller.SampleAt(slot.Transform.position);
                    float threshold = slot.Visibility.Threshold;
                    switch (slot.Visibility.Mode)
                    {
                        case PollutionVisibilityMode.WhenPolluted: hidden = pollution < threshold - margin; break;
                        case PollutionVisibilityMode.WhenClean: hidden = pollution > threshold + margin; break;
                    }
                }
                if (hidden == slot.Hidden)
                    continue;

                slot.Hidden = hidden;
                if (hidden && slot.Rig != null)
                    ReleaseRig(slot);
                SetStaticVisible(slot, !hidden && slot.Rig == null);
                if (slot.Walker >= 0)
                {
                    CrowdWalkerState walker = walkers[slot.Walker];
                    walker.Active = !hidden && !slot.PausedByBehavior;
                    walker.Present = !hidden;
                    walkers[slot.Walker] = walker;
                }
            }
        }

        // How far past its threshold an NPC can still be partly drawn by the dissolve
        static float HideMargin(PollutionController controller)
        {
            PollutionProfile profile = controller != null ? controller.Profile : null;
            return profile != null
                ? (profile.dissolveStagger + profile.dissolveSpread) * 0.5f + profile.dissolveEdgeWidth
                : 0.35f;
        }

        // ------------------------------------------------------------ level of detail

        void UpdateLevelOfDetail()
        {
            Camera view = viewCamera != null ? viewCamera : Camera.main;
            if (view == null)
                return;

            Vector3 eye = view.transform.position;
            GeometryUtility.CalculateFrustumPlanes(view, frustum);
            // The graphics level caps the animated set and how far it reaches (GraphicsQuality, Very Low → High)
            GraphicsProfile quality = GraphicsQuality.Current;
            int budget = EffectiveAnimatedBudget;
            float reachDistance = animateDistance * quality.CrowdAnimateDistanceScale;
            float reach = reachDistance * reachDistance;

            candidates.Clear();
            foreach (Slot slot in slots)
            {
                if (slot.Hidden || slot.Agent.AnimatedPrefab == null)
                    continue;

                Vector3 position = slot.Transform.position;
                float distance = (position - eye).sqrMagnitude;
                bool animated = slot.Rig != null;
                if (distance > reach * (animated ? 1.44f : 1f)) // 20% further before letting go, so the edge doesn't flicker
                {
                    if (animated)
                        ReleaseRig(slot);
                    continue;
                }

                float priority = distance;
                if (!InView(position))
                    priority *= 16f; // off-screen NPCs only get spare budget
                if (animated)
                    priority *= 0.7f; // keep the animated set stable
                slot.Priority = priority;
                candidates.Add(slot);
            }

            candidates.Sort(ByPriority);
            for (int i = candidates.Count - 1; i >= budget; i--)
            {
                if (candidates[i].Rig != null)
                    ReleaseRig(candidates[i]);
            }
            int count = Mathf.Min(budget, candidates.Count);
            for (int i = 0; i < count; i++)
            {
                if (candidates[i].Rig == null)
                    AttachRig(candidates[i]);
            }
            AnimatedCount = count;
        }

        bool InView(Vector3 position)
        {
            Vector3 centre = position + Vector3.up;
            foreach (Plane plane in frustum)
            {
                if (plane.GetDistanceToPoint(centre) < -1.5f)
                    return false;
            }
            return true;
        }

        void AttachRig(Slot slot)
        {
            CrowdRig rig = pool.Get(slot.Agent.AnimatedPrefab, slot.Transform);
            slot.Rig = rig;
            foreach (SkinnedMeshRenderer skin in rig.Renderers)
                skin.SetShaderUserValue(slot.UserValue);
            SetStaticVisible(slot, false);

            if (rig.Animator != null)
            {
                rig.Animator.speed = slot.Stopped ? 1f : slot.Playback;
                rig.Animator.Play(slot.Stopped ? IdleHash : slot.StateHash, 0, (float)random.NextDouble());
                rig.Animator.Update(0f); // pose it straight away, so it never shows a frame of T-pose
            }
        }

        void ReleaseRig(Slot slot)
        {
            pool.Return(slot.Rig);
            slot.Rig = null;
            SetStaticVisible(slot, !slot.Hidden);
        }

        static void SetStaticVisible(Slot slot, bool visible)
        {
            foreach (Renderer staticRenderer in slot.StaticRenderers)
            {
                if (staticRenderer != null)
                    staticRenderer.enabled = visible;
            }
        }
    }
}
