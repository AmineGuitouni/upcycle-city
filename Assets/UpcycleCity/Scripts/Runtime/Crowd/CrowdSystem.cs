using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Jobs;
using UpcycleCity.Pollution;

namespace UpcycleCity.Crowd
{
    /// <summary>
    /// Runs the whole crowd from one place, so its cost grows gently with the number of NPCs:
    /// <list type="bullet">
    /// <item>Walkers move in one Burst-compiled transform job, not in per-NPC Update calls.</item>
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
            public CrowdWalkerState WalkerState;
        }

        readonly List<Slot> slots = new List<Slot>();
        readonly Dictionary<CrowdAgent, Slot> slotByAgent = new Dictionary<CrowdAgent, Slot>();
        readonly List<Slot> walkerSlots = new List<Slot>();
        readonly List<Slot> candidates = new List<Slot>();
        readonly Plane[] frustum = new Plane[6];
        readonly System.Random random = new System.Random(1);

        NativeArray<Vector3> points;
        NativeArray<float> distances;
        NativeArray<CrowdPathSpan> paths;
        NativeArray<CrowdWalkerState> walkers;
        TransformAccessArray walkerTransforms;
        JobHandle movement;

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

            bool stateChanged = slot.StateHash != newHash;
            slot.StateHash = newHash;
            slot.Playback = newPlayback;

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
        }

        void OnPollutionChanged(PollutionController controller) => pollutionChanged = true;

        void Update()
        {
            using var _ = UpdateMarker.Auto();
            if (registryChanged)
                Rebuild();
            if (walkerSlots.Count == 0)
                return;

            float deltaTime = Time.deltaTime;
            movement = new CrowdMovementJob
            {
                Points = points,
                Distances = distances,
                Paths = paths,
                Walkers = walkers,
                DeltaTime = deltaTime,
                TurnBlend = 1f - Mathf.Exp(-turnRate * deltaTime),
            }.Schedule(walkerTransforms);
            JobHandle.ScheduleBatchedJobs();
        }

        void LateUpdate()
        {
            using var _ = LateUpdateMarker.Auto();
            movement.Complete();
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
            var spans = new List<CrowdPathSpan>();
            foreach (Slot slot in slots)
            {
                slot.Walker = -1;
                if (!slot.Agent.IsWalker)
                    continue;

                CrowdPath path = slot.Agent.Path;
                if (!pathIndex.TryGetValue(path, out int index))
                {
                    index = spans.Count;
                    pathIndex.Add(path, index);
                    spans.Add(BakePath(path, pointList, distanceList));
                }

                if (!slot.WalkerReady)
                    PlaceOnPath(slot, path);
                slot.WalkerState.Path = index;
                slot.WalkerState.Active = !slot.Hidden && !slot.PausedByBehavior;
                slot.Walker = walkerSlots.Count;
                walkerSlots.Add(slot);
            }

            if (walkerSlots.Count == 0)
                return;

            points = new NativeArray<Vector3>(pointList.ToArray(), Allocator.Persistent);
            distances = new NativeArray<float>(distanceList.ToArray(), Allocator.Persistent);
            paths = new NativeArray<CrowdPathSpan>(spans.ToArray(), Allocator.Persistent);
            walkers = new NativeArray<CrowdWalkerState>(walkerSlots.Count, Allocator.Persistent);
            var transforms = new Transform[walkerSlots.Count];
            for (int i = 0; i < walkerSlots.Count; i++)
            {
                walkers[i] = walkerSlots[i].WalkerState;
                transforms[i] = walkerSlots[i].Transform;
            }
            walkerTransforms = new TransformAccessArray(transforms);
        }

        static CrowdPathSpan BakePath(CrowdPath path, List<Vector3> pointList, List<float> distanceList)
        {
            var span = new CrowdPathSpan { Start = pointList.Count, Segments = path.SegmentCount, Loop = path.Loop };
            float travelled = 0f;
            for (int i = 0; i <= span.Segments; i++) // a loop's last point is its first again
            {
                Vector3 point = path.GetPoint(i);
                if (i > 0)
                    travelled += CrowdPath.FlatDistance(pointList[pointList.Count - 1], point);
                pointList.Add(point);
                distanceList.Add(travelled);
            }
            span.Length = Mathf.Max(travelled, 1e-3f);
            return span;
        }

        // Walkers start from the point of the path nearest to where they were placed, facing their way
        static void PlaceOnPath(Slot slot, CrowdPath path)
        {
            path.Project(slot.Transform.position, out float distance, out float offset, out Vector3 tangent);
            Vector3 forward = slot.Transform.forward;
            slot.WalkerState = new CrowdWalkerState
            {
                Distance = distance,
                Speed = slot.Agent.WalkSpeed,
                Direction = forward.x * tangent.x + forward.z * tangent.z >= 0f ? 1f : -1f,
                Lane = Mathf.Clamp(offset, -2f, 2f),
                Yaw = Mathf.Atan2(forward.x, forward.z),
            };
            slot.WalkerReady = true;
        }

        void DisposeWalkerData()
        {
            if (points.IsCreated) points.Dispose();
            if (distances.IsCreated) distances.Dispose();
            if (paths.IsCreated) paths.Dispose();
            if (walkers.IsCreated) walkers.Dispose();
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
            float reach = animateDistance * animateDistance;

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
            for (int i = candidates.Count - 1; i >= animatedBudget; i--)
            {
                if (candidates[i].Rig != null)
                    ReleaseRig(candidates[i]);
            }
            int count = Mathf.Min(animatedBudget, candidates.Count);
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
                rig.Animator.speed = slot.Playback;
                rig.Animator.Play(slot.StateHash, 0, (float)random.NextDouble());
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
