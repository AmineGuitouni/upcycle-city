using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

namespace UpcycleCity.Cinematics
{
    /// <summary>
    /// A small school of fish circling a point on a wobbling ellipse, each playing its swim clip through a
    /// lightweight Playable graph (no Animator Controller needed). Spawned on Start; dressing for the intro's
    /// underwater dream.
    /// </summary>
    [AddComponentMenu("Upcycle City/Cinematics/Fish School")]
    public sealed class FishSchool : MonoBehaviour
    {
        [System.Serializable]
        public struct Species
        {
            public GameObject prefab;
            public AnimationClip swim;
            [Tooltip("Uniform scale applied to the model.")]
            public float scale;
        }

        [SerializeField] Species[] species = new Species[0];
        [SerializeField, Range(1, 40)] int count = 10;
        [Tooltip("Ellipse radii (x, z) and vertical spread (y), in metres.")]
        [SerializeField] Vector3 radii = new Vector3(3f, 1f, 2f);
        [SerializeField] float angularSpeed = 22f;
        [Tooltip("Rotation fix so the model's nose points along +Z.")]
        [SerializeField] Vector3 modelEulerOffset;
        [SerializeField] int seed = 7;

        struct Fish
        {
            public Transform transform;
            public float angle, radiusScale, height, speed, wobble;
        }

        readonly List<Fish> fish = new List<Fish>(40);
        readonly List<PlayableGraph> graphs = new List<PlayableGraph>(40);

        void Start()
        {
            if (species == null || species.Length == 0)
                return;
            var random = new System.Random(seed);
            float Rand(float a, float b) => a + (float)random.NextDouble() * (b - a);

            for (int i = 0; i < count; i++)
            {
                Species sp = species[i % species.Length];
                if (sp.prefab == null)
                    continue;
                GameObject go = Instantiate(sp.prefab, transform);
                go.name = $"{sp.prefab.name}_{i}";
                float scale = (sp.scale > 0f ? sp.scale : 1f) * Rand(0.85f, 1.15f);
                go.transform.localScale = Vector3.one * scale;

                if (sp.swim != null)
                {
                    if (!go.TryGetComponent(out Animator animator))
                        animator = go.AddComponent<Animator>();
                    animator.cullingMode = AnimatorCullingMode.CullCompletely;
                    AnimationClipPlayable playable = AnimationPlayableUtilities.PlayClip(animator, sp.swim, out PlayableGraph graph);
                    playable.SetTime(Rand(0f, sp.swim.length));
                    playable.SetSpeed(Rand(0.85f, 1.2f));
                    graphs.Add(graph);
                }

                fish.Add(new Fish
                {
                    transform = go.transform,
                    angle = Rand(0f, 360f),
                    radiusScale = Rand(0.7f, 1.15f),
                    height = Rand(-1f, 1f),
                    speed = angularSpeed * Rand(0.85f, 1.15f),
                    wobble = Rand(0f, 10f),
                });
            }
        }

        void Update()
        {
            float dt = Time.deltaTime;
            Quaternion fix = Quaternion.Euler(modelEulerOffset);
            for (int i = 0; i < fish.Count; i++)
            {
                Fish f = fish[i];
                f.angle += f.speed * dt;
                fish[i] = f;
                Vector3 p = LocalPoint(f, f.angle);
                Vector3 ahead = LocalPoint(f, f.angle + Mathf.Sign(f.speed) * 4f);
                Vector3 world = transform.TransformPoint(p);
                Vector3 dir = transform.TransformPoint(ahead) - world;
                if (dir.sqrMagnitude > 1e-6f)
                    f.transform.SetPositionAndRotation(world, Quaternion.LookRotation(dir, Vector3.up) * fix);
            }
        }

        Vector3 LocalPoint(in Fish f, float angleDegrees)
        {
            float a = angleDegrees * Mathf.Deg2Rad;
            float r = f.radiusScale;
            float y = (f.height + Mathf.Sin(Time.time * 0.4f + f.wobble) * 0.25f) * radii.y;
            return new Vector3(Mathf.Cos(a) * radii.x * r, y, Mathf.Sin(a) * radii.z * r);
        }

        void OnDestroy()
        {
            foreach (PlayableGraph g in graphs)
            {
                if (g.IsValid())
                    g.Destroy();
            }
            graphs.Clear();
        }

        void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(1f, 0.6f, 0.2f);
            Gizmos.matrix = transform.localToWorldMatrix;
            Gizmos.DrawWireCube(Vector3.zero, new Vector3(radii.x * 2f, radii.y * 2f, radii.z * 2f));
        }
    }
}
