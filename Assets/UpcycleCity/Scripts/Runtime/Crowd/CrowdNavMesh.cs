using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using UpcycleCity.Core;

namespace UpcycleCity.Crowd
{
    /// <summary>
    /// The Unity NavMesh the crowd walks on, built from the city's solid colliders (buildings, bins, benches,
    /// machines, kerbs). <see cref="CrowdSystem"/> routes every <see cref="CrowdPath"/> over it, so walkers go
    /// around anything in the way instead of through it, and keep their sidesteps on walkable ground.
    /// <para>
    /// Bake it in the editor with <b>Upcycle City → Crowd → Bake Crowd NavMesh</b> (needed for phone builds, where
    /// mesh colliders usually aren't readable). Without baked data it builds itself in the background at start.
    /// </para>
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(-200)]
    [AddComponentMenu("Upcycle City/Crowd/Crowd Nav Mesh")]
    public sealed class CrowdNavMesh : MonoBehaviour
    {
        [Tooltip("NavMesh baked in the editor. Leave empty to build it when the scene starts.")]
        [SerializeField] NavMeshData bakedData;

        [Tooltip("Build the NavMesh in the background at start when there is no baked data.")]
        [SerializeField] bool bakeAtRuntimeIfMissing = true;

        [Header("Area")]
        [Tooltip("Cover every Crowd Path in the scene (plus padding) instead of the fixed bounds below.")]
        [SerializeField] bool fitToCrowdPaths = true;

        [SerializeField, Min(0f)] float pathPadding = 20f;

        [SerializeField] Bounds bounds = new Bounds(Vector3.zero, new Vector3(200f, 40f, 200f));

        [Tooltip("Layers whose (non-trigger, static or kinematic) colliders shape the NavMesh.")]
        [SerializeField] LayerMask geometryLayers = GameLayers.GroundMask;

        [Header("Walker body")]
        [SerializeField, Range(0.1f, 1f)] float agentRadius = 0.3f;
        [SerializeField, Range(0.5f, 3f)] float agentHeight = 1.8f;
        [Tooltip("Highest step walkers climb, e.g. a kerb.")]
        [SerializeField, Range(0f, 1f)] float agentClimb = 0.3f;
        [SerializeField, Range(0f, 60f)] float agentSlope = 40f;
        [SerializeField, Range(0.03f, 0.3f)] float voxelSize = 0.1f;

        NavMeshDataInstance instance;
        NavMeshData runtimeData;
        AsyncOperation buildOperation;

        /// <summary>The NavMesh in the scene, if any.</summary>
        public static CrowdNavMesh Active { get; private set; }

        /// <summary>True once walkable data is loaded or built.</summary>
        public static bool IsReady { get; private set; }

        /// <summary>Goes up whenever the NavMesh appears, changes or goes away, so walkers can re-route.</summary>
        public static int Version { get; private set; }

        public NavMeshData BakedData => bakedData;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            Active = null;
            IsReady = false;
            Version++;
        }

        void Reset() => geometryLayers = GameLayers.GroundMask;

        void OnEnable()
        {
            Active = this;
            if (bakedData != null)
            {
                instance = NavMesh.AddNavMeshData(bakedData);
                MarkReady();
            }
            else if (bakeAtRuntimeIfMissing)
            {
                StartRuntimeBuild();
            }
        }

        void OnDisable()
        {
            if (instance.valid)
                instance.Remove();
            instance = default;
            buildOperation = null;
            if (Active == this)
            {
                Active = null;
                IsReady = false;
            }
            Version++;
        }

        void Update()
        {
            if (buildOperation != null && buildOperation.isDone)
            {
                buildOperation = null;
                MarkReady();
            }
        }

        void MarkReady()
        {
            IsReady = true;
            Version++;
        }

        /// <summary>Rebuilds the NavMesh in the background, e.g. after scenery was placed or removed.</summary>
        public void StartRuntimeBuild()
        {
            var sources = new List<NavMeshBuildSource>();
            Bounds area = WorldBounds();
            CollectSources(area, sources);
            NavMeshBuildSettings settings = BuildSettings();

            if (runtimeData == null)
            {
                runtimeData = new NavMeshData(settings.agentTypeID);
                if (instance.valid)
                    instance.Remove();
                instance = NavMesh.AddNavMeshData(runtimeData);
            }
            buildOperation = NavMeshBuilder.UpdateNavMeshDataAsync(runtimeData, settings, sources, area);
        }

        /// <summary>Builds NavMesh data straight away (editor baking). Returns null when nothing walkable was found.</summary>
        public NavMeshData BuildNow()
        {
            var sources = new List<NavMeshBuildSource>();
            Bounds area = WorldBounds();
            CollectSources(area, sources);
            if (sources.Count == 0)
                return null;
            NavMeshData data = NavMeshBuilder.BuildNavMeshData(BuildSettings(), sources, area, Vector3.zero, Quaternion.identity);
            if (data != null)
                data.name = "CrowdNavMesh";
            return data;
        }

        /// <summary>Assigns editor-baked data (used by the bake tool).</summary>
        public void SetBakedData(NavMeshData data) => bakedData = data;

        public NavMeshBuildSettings BuildSettings()
        {
            NavMeshBuildSettings settings = NavMesh.GetSettingsByID(0);
            settings.agentRadius = agentRadius;
            settings.agentHeight = agentHeight;
            settings.agentClimb = agentClimb;
            settings.agentSlope = agentSlope;
            settings.overrideVoxelSize = true;
            settings.voxelSize = voxelSize;
            return settings;
        }

        public Bounds WorldBounds()
        {
            if (!fitToCrowdPaths)
                return bounds;

            CrowdPath[] paths = FindObjectsByType<CrowdPath>(FindObjectsInactive.Exclude);
            bool any = false;
            var area = new Bounds();
            foreach (CrowdPath path in paths)
            {
                for (int i = 0; i < path.PointCount; i++)
                {
                    Vector3 p = path.GetPoint(i);
                    if (!any) area = new Bounds(p, Vector3.zero);
                    else area.Encapsulate(p);
                    any = true;
                }
            }
            if (!any)
                return bounds;
            area.Expand(new Vector3(pathPadding * 2f, 20f, pathPadding * 2f));
            return area;
        }

        void CollectSources(Bounds area, List<NavMeshBuildSource> sources)
        {
            NavMeshBuilder.CollectSources(area, geometryLayers, NavMeshCollectGeometry.PhysicsColliders, 0,
                new List<NavMeshBuildMarkup>(), sources);
            sources.RemoveAll(IsTransient);
        }

        // Triggers, loose physics props (trash) and the crowd itself must not carve the walkable area
        static bool IsTransient(NavMeshBuildSource source)
        {
            if (source.component is Collider collider)
            {
                if (collider.isTrigger)
                    return true;
                Rigidbody body = collider.attachedRigidbody;
                if (body != null && !body.isKinematic)
                    return true;
                if (collider.GetComponentInParent<CrowdAgent>() != null || collider.GetComponentInParent<CrowdObstacle>() != null)
                    return true;
            }
            return false;
        }
    }
}
