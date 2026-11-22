using System.Collections.Generic;
using UnityEngine;

namespace UpcycleCity.Cinematics
{
    /// <summary>
    /// Procedural sea turtle for the intro (model: Art/UpcycleProps/Intro/sea_turtle.fbx, built by
    /// Tools/Blender/sea_turtle.py). In <see cref="TurtleMode.Swim"/> it glides along a smooth closed loop through
    /// its waypoints, banking into turns, with slow flying strokes of the front flippers and paddling back
    /// flippers. In <see cref="TurtleMode.Dead"/> it is still, with limp flippers.
    /// Flippers are found by name (Flipper_F*, Flipper_B*) and sided by their position, so a swapped L/R
    /// naming from the exporter doesn't matter.
    /// </summary>
    [AddComponentMenu("Upcycle City/Cinematics/Sea Turtle")]
    public sealed class SeaTurtle : MonoBehaviour
    {
        public enum TurtleMode { Swim, Dead }

        [SerializeField] TurtleMode mode = TurtleMode.Swim;
        [Tooltip("The model instance (its children are Shell, Head and the four flippers).")]
        [SerializeField] Transform model;
        [Tooltip("Closed loop the turtle swims through, in order.")]
        [SerializeField] Transform[] waypoints = new Transform[0];
        [SerializeField, Min(0.05f)] float speed = 1.6f;
        [Tooltip("Front flipper strokes per second.")]
        [SerializeField, Min(0.05f)] float strokeRate = 0.75f;
        [SerializeField] float strokeDegrees = 38f;
        [SerializeField, Min(0f)] float bankDegrees = 28f;
        [Tooltip("A slow barrel roll every this many metres (0 = never), for the playful dream swim.")]
        [SerializeField, Min(0f)] float rollEvery = 0f;

        struct Flipper
        {
            public Transform transform;
            public Quaternion rest;
            public float side;   // +1 on the model's right (+X), -1 on its left
            public bool front;
        }

        readonly List<Flipper> flippers = new List<Flipper>(4);
        Transform head;
        Quaternion headRest;

        readonly List<Vector3> samples = new List<Vector3>(256);
        readonly List<float> distances = new List<float>(256);
        float loopLength;
        float travelled;
        float stroke;
        float bank;
        Vector3 lastForward;

        public TurtleMode Mode => mode;
        public float Speed { get => speed; set => speed = Mathf.Max(0.05f, value); }
        public float StrokeRate { get => strokeRate; set => strokeRate = Mathf.Max(0.05f, value); }

        public void SetMode(TurtleMode next)
        {
            mode = next;
            if (mode == TurtleMode.Dead)
                PoseDead();
        }

        void Awake()
        {
            if (model == null && transform.childCount > 0)
                model = transform.GetChild(0);
            CollectParts();
            BuildLoop();
            if (mode == TurtleMode.Dead)
                PoseDead();
        }

        void CollectParts()
        {
            flippers.Clear();
            if (model == null)
                return;
            foreach (Transform t in model.GetComponentsInChildren<Transform>(true))
            {
                if (t.name.StartsWith("Flipper_F") || t.name.StartsWith("Flipper_B"))
                {
                    flippers.Add(new Flipper
                    {
                        transform = t,
                        rest = t.localRotation,
                        side = Mathf.Sign(t.localPosition.x == 0f ? 1f : t.localPosition.x),
                        front = t.name.StartsWith("Flipper_F"),
                    });
                }
                else if (t.name == "Head")
                {
                    head = t;
                    headRest = t.localRotation;
                }
            }
        }

        /// <summary>Samples a closed Catmull-Rom spline through the waypoints.</summary>
        void BuildLoop()
        {
            samples.Clear();
            distances.Clear();
            loopLength = 0f;
            int n = waypoints != null ? waypoints.Length : 0;
            if (n < 2)
                return;
            const int perSegment = 24;
            for (int i = 0; i < n; i++)
            {
                Vector3 p0 = waypoints[(i - 1 + n) % n].position;
                Vector3 p1 = waypoints[i].position;
                Vector3 p2 = waypoints[(i + 1) % n].position;
                Vector3 p3 = waypoints[(i + 2) % n].position;
                for (int s = 0; s < perSegment; s++)
                {
                    float t = s / (float)perSegment;
                    Vector3 p = 0.5f * (2f * p1 + (-p0 + p2) * t + (2f * p0 - 5f * p1 + 4f * p2 - p3) * t * t +
                                        (-p0 + 3f * p1 - 3f * p2 + p3) * t * t * t);
                    if (samples.Count > 0)
                        loopLength += Vector3.Distance(samples[samples.Count - 1], p);
                    distances.Add(loopLength);
                    samples.Add(p);
                }
            }
            loopLength += Vector3.Distance(samples[samples.Count - 1], samples[0]);
        }

        Vector3 PointAt(float distance)
        {
            if (samples.Count == 0)
                return transform.position;
            distance = Mathf.Repeat(distance, loopLength);
            int hi = distances.BinarySearch(distance);
            if (hi < 0)
                hi = ~hi;
            if (hi <= 0)
                return samples[0];
            if (hi >= samples.Count)
            {
                float lastSpan = loopLength - distances[samples.Count - 1];
                float k = lastSpan > 0f ? (distance - distances[samples.Count - 1]) / lastSpan : 0f;
                return Vector3.Lerp(samples[samples.Count - 1], samples[0], k);
            }
            float span = distances[hi] - distances[hi - 1];
            float f = span > 0f ? (distance - distances[hi - 1]) / span : 0f;
            return Vector3.Lerp(samples[hi - 1], samples[hi], f);
        }

        void Update()
        {
            if (mode != TurtleMode.Swim)
                return;

            float dt = Time.deltaTime;
            stroke += dt * strokeRate * Mathf.PI * 2f;

            // Speed surges a little on each power stroke
            float surge = 0.75f + 0.5f * Mathf.Max(0f, Mathf.Sin(stroke - 0.6f));
            if (samples.Count > 1)
            {
                travelled += speed * surge * dt;
                Vector3 position = PointAt(travelled);
                Vector3 ahead = PointAt(travelled + 0.6f);
                Vector3 forward = (ahead - position).normalized;
                if (forward.sqrMagnitude < 1e-4f)
                    forward = transform.forward;

                // Bank into turns: signed yaw rate
                float turn = lastForward.sqrMagnitude > 0f
                    ? Vector3.SignedAngle(lastForward, forward, Vector3.up) / Mathf.Max(dt, 1e-4f)
                    : 0f;
                lastForward = forward;
                bank = Mathf.Lerp(bank, Mathf.Clamp(-turn * 0.35f, -bankDegrees, bankDegrees), 1f - Mathf.Exp(-3f * dt));

                float roll = bank;
                if (rollEvery > 0f)
                {
                    // One smooth full roll per rollEvery metres, otherwise level
                    float cycle = Mathf.Repeat(travelled / rollEvery, 1f);
                    roll += cycle > 0.8f ? Mathf.SmoothStep(0f, 360f, (cycle - 0.8f) / 0.2f) : 0f;
                }

                transform.SetPositionAndRotation(position,
                    Quaternion.LookRotation(forward, Vector3.up) * Quaternion.Euler(0f, 0f, roll));
            }

            AnimateFlippers();
        }

        void AnimateFlippers()
        {
            float s = Mathf.Sin(stroke);
            float c = Mathf.Cos(stroke);
            foreach (Flipper f in flippers)
            {
                Quaternion local;
                if (f.front)
                {
                    // Flying stroke: flap up and down (around the body's long axis), feathering the leading edge
                    float flap = s * strokeDegrees;
                    float sweep = c * strokeDegrees * 0.45f;
                    local = Quaternion.Euler(c * 12f, -f.side * sweep, f.side * flap);
                }
                else
                {
                    // Back flippers trail and paddle gently, half a beat behind
                    float paddle = Mathf.Sin(stroke - 1.2f) * 14f;
                    local = Quaternion.Euler(0f, f.side * paddle, f.side * paddle * 0.5f);
                }
                f.transform.localRotation = f.rest * local;
            }

            if (head != null)
                head.localRotation = headRest * Quaternion.Euler(Mathf.Sin(stroke * 0.5f) * 4f, Mathf.Sin(stroke * 0.23f) * 6f, 0f);
        }

        void PoseDead()
        {
            foreach (Flipper f in flippers)
            {
                // Limp: front flippers splayed flat and slightly back, back flippers turned in
                Quaternion limp = f.front
                    ? Quaternion.Euler(0f, f.side * 18f, -f.side * 10f)
                    : Quaternion.Euler(0f, -f.side * 20f, 0f);
                f.transform.localRotation = f.rest * limp;
            }
            if (head != null)
                head.localRotation = headRest * Quaternion.Euler(18f, 22f, 0f);
        }

        void OnDrawGizmosSelected()
        {
            if (waypoints == null || waypoints.Length < 2)
                return;
            Gizmos.color = new Color(0.3f, 0.9f, 0.8f);
            for (int i = 0; i < waypoints.Length; i++)
            {
                if (waypoints[i] != null && waypoints[(i + 1) % waypoints.Length] != null)
                    Gizmos.DrawLine(waypoints[i].position, waypoints[(i + 1) % waypoints.Length].position);
            }
        }
    }
}
