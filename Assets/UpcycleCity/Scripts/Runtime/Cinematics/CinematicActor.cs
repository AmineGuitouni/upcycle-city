using System.Collections;
using UnityEngine;

namespace UpcycleCity.Cinematics
{
    /// <summary>
    /// Procedural acting for an unrigged character (the player's <see cref="Character.CharacterAvatar"/>).
    /// The root moves and turns; a child "body" carries the walk bob and sway, kneeling, sitting and a sad slump.
    /// Every action is a coroutine so <see cref="IntroDirector"/> can run it or yield on it.
    /// </summary>
    [AddComponentMenu("Upcycle City/Cinematics/Cinematic Actor")]
    public sealed class CinematicActor : MonoBehaviour
    {
        [Tooltip("Child that holds the character model. Bob, sway, kneel and slump are applied to it.")]
        [SerializeField] Transform body;
        [Tooltip("Where carried props hang, relative to the root (right hand at rest).")]
        [SerializeField] Vector3 handOffset = new Vector3(0.3f, 0.78f, 0.1f);

        [Header("Walk")]
        [SerializeField, Min(0.1f)] float stepLength = 0.62f;
        [SerializeField, Min(0f)] float bobHeight = 0.035f;
        [SerializeField, Min(0f)] float swayDegrees = 2.2f;
        [SerializeField, Min(0.1f)] float turnSharpness = 5f;

        [Header("Poses")]
        [Tooltip("How far the body drops to kneel; the legs sink out of frame into the sand.")]
        [SerializeField] float kneelDrop = 0.55f;
        [SerializeField] float kneelPitch = 18f;
        [SerializeField] float sitDrop = 0.55f;
        [SerializeField] float sitPitch = -4f;
        [SerializeField] float breathScale = 0.008f;

        float lower;          // current body drop (m)
        float pitch;          // current body lean (deg, + is forward)
        float slump;          // extra forward lean for sadness (deg)
        float walkPhase;
        float swayRoll;
        float bob;

        public Transform Body => body;
        public bool IsWalking { get; private set; }

        void Reset()
        {
            if (transform.childCount > 0)
                body = transform.GetChild(0);
        }

        public void Place(Vector3 position, float yawDegrees)
        {
            transform.SetPositionAndRotation(position, Quaternion.Euler(0f, yawDegrees, 0f));
        }

        public void Place(Transform point)
        {
            if (point != null)
                Place(point.position, point.eulerAngles.y);
        }

        /// <summary>Snaps the pose without a transition (used at cuts).</summary>
        public void SetPose(float drop, float leanDegrees, float slumpDegrees = 0f)
        {
            lower = drop;
            pitch = leanDegrees;
            slump = slumpDegrees;
        }

        public void SetSlump(float degrees) => slump = degrees;

        /// <summary>Call when a walk coroutine is stopped early, so the walk bob settles.</summary>
        public void Halt() => IsWalking = false;

        // ---------------------------------------------------------------- actions

        /// <summary>Walks through the points at the given speed, turning smoothly into each leg.</summary>
        public IEnumerator WalkPath(Vector3[] points, float speed)
        {
            if (points == null || points.Length == 0)
                yield break;
            IsWalking = true;
            for (int i = 0; i < points.Length; i++)
            {
                Vector3 target = points[i];
                target.y = transform.position.y;
                while (true)
                {
                    Vector3 delta = target - transform.position;
                    delta.y = 0f;
                    float distance = delta.magnitude;
                    float step = speed * Time.deltaTime;
                    if (distance <= step || distance < 1e-3f)
                    {
                        transform.position = target;
                        break;
                    }
                    Vector3 dir = delta / distance;
                    transform.position += dir * step;
                    Quaternion facing = Quaternion.LookRotation(dir, Vector3.up);
                    transform.rotation = Quaternion.Slerp(transform.rotation, facing, 1f - Mathf.Exp(-turnSharpness * Time.deltaTime));
                    walkPhase += step / stepLength * Mathf.PI;
                    yield return null;
                }
            }
            IsWalking = false;
        }

        /// <summary>Total length of a path from the actor's current position.</summary>
        public float PathLength(Vector3[] points)
        {
            float length = 0f;
            Vector3 from = transform.position;
            foreach (Vector3 p in points)
            {
                Vector3 d = p - from;
                d.y = 0f;
                length += d.magnitude;
                from = p;
            }
            return length;
        }

        public IEnumerator TurnTowards(Vector3 worldPoint, float seconds)
        {
            Vector3 dir = worldPoint - transform.position;
            dir.y = 0f;
            if (dir.sqrMagnitude < 1e-4f)
                yield break;
            Quaternion from = transform.rotation;
            Quaternion to = Quaternion.LookRotation(dir.normalized, Vector3.up);
            yield return Tween(seconds, t => transform.rotation = Quaternion.Slerp(from, to, t));
        }

        public IEnumerator Kneel(float seconds) => BlendPose(kneelDrop, kneelPitch, seconds);
        public IEnumerator Sit(float seconds) => BlendPose(sitDrop, sitPitch, seconds);
        public IEnumerator Stand(float seconds) => BlendPose(0f, 0f, seconds);

        public IEnumerator Slump(float degrees, float seconds)
        {
            float from = slump;
            yield return Tween(seconds, t => slump = Mathf.Lerp(from, degrees, t));
        }

        IEnumerator BlendPose(float drop, float lean, float seconds)
        {
            float fromDrop = lower, fromLean = pitch;
            yield return Tween(seconds, t =>
            {
                lower = Mathf.Lerp(fromDrop, drop, t);
                pitch = Mathf.Lerp(fromLean, lean, t);
            });
        }

        /// <summary>Parents a prop to the actor's hand (on the body, so it follows kneeling and sitting).</summary>
        public void Hold(Transform prop)
        {
            if (prop == null)
                return;
            Transform hand = body != null ? body : transform;
            prop.SetParent(hand, true);
            prop.localPosition = handOffset;
        }

        /// <summary>The world position of the hand, for arcs into or out of it.</summary>
        public Vector3 HandPosition => (body != null ? body : transform).TransformPoint(handOffset);

        /// <summary>Moves a prop along an arc to a world position (unparenting it), e.g. setting it down.</summary>
        public static IEnumerator Toss(Transform prop, Vector3 to, Quaternion rotation, float arcHeight, float seconds)
        {
            if (prop == null)
                yield break;
            prop.SetParent(null, true);
            Vector3 from = prop.position;
            Quaternion fromRot = prop.rotation;
            yield return Tween(seconds, t =>
            {
                Vector3 p = Vector3.Lerp(from, to, t);
                p.y += Mathf.Sin(t * Mathf.PI) * arcHeight;
                prop.SetPositionAndRotation(p, Quaternion.Slerp(fromRot, rotation, t));
            });
        }

        public static IEnumerator Tween(float seconds, System.Action<float> step)
        {
            if (seconds <= 0f)
            {
                step(1f);
                yield break;
            }
            for (float t = 0f; t < seconds; t += Time.deltaTime)
            {
                float k = t / seconds;
                step(k * k * (3f - 2f * k));
                yield return null;
            }
            step(1f);
        }

        // ---------------------------------------------------------------- per-frame body motion

        void LateUpdate()
        {
            if (body == null)
                return;

            // Walk bob and sway settle to rest when standing still
            float targetBob = IsWalking ? Mathf.Abs(Mathf.Sin(walkPhase)) * bobHeight : 0f;
            float targetSway = IsWalking ? Mathf.Sin(walkPhase) * swayDegrees : 0f;
            float settle = 1f - Mathf.Exp(-10f * Time.deltaTime);
            bob = Mathf.Lerp(bob, targetBob, settle);
            swayRoll = Mathf.Lerp(swayRoll, targetSway, settle);

            float breath = Mathf.Sin(Time.time * 1.6f) * breathScale;
            body.localPosition = new Vector3(0f, bob - lower, 0f);
            body.localRotation = Quaternion.Euler(pitch + slump, 0f, swayRoll);
            body.localScale = new Vector3(1f, 1f + breath, 1f);
        }
    }
}
