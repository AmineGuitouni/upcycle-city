using System.Collections;
using UnityEngine;

namespace UpcycleCity.Cinematics
{
    /// <summary>
    /// Stage directions for the intro's actor. It moves and turns the actor's root; the body itself is animated by
    /// <see cref="CharacterRigPoser"/> (walk cycle, kneel, sit, reach, look), which this drives by tweening its
    /// layers. Every action is a coroutine so <see cref="IntroDirector"/> can run it or yield on it.
    /// </summary>
    [AddComponentMenu("Upcycle City/Cinematics/Cinematic Actor")]
    [RequireComponent(typeof(CharacterRigPoser))]
    public sealed class CinematicActor : MonoBehaviour
    {
        [SerializeField] CharacterRigPoser poser;
        [SerializeField, Min(0.1f)] float turnSharpness = 4f;

        public CharacterRigPoser Poser => poser != null ? poser : poser = GetComponent<CharacterRigPoser>();
        public bool IsWalking { get; private set; }

        void Reset() => poser = GetComponent<CharacterRigPoser>();

        // ---------------------------------------------------------------- placement and instant poses

        public void Place(Vector3 position, float yawDegrees)
        {
            transform.SetPositionAndRotation(position, Quaternion.Euler(0f, yawDegrees, 0f));
        }

        public void Place(Transform point)
        {
            if (point != null)
                Place(point.position, point.eulerAngles.y);
        }

        /// <summary>Snaps the body's pose layers (used at cuts).</summary>
        public void SetPose(float kneel, float sit, float slumpDegrees = 0f)
        {
            Poser.kneel = kneel;
            Poser.sit = sit;
            Poser.slump = slumpDegrees;
        }

        public void SetSlump(float degrees) => Poser.slump = degrees;
        public void SetWalkStyle(CharacterRigPoser.WalkStyle style) => Poser.walk = style;
        public void SetCarry(float elbowDegrees) => Poser.carryRight = elbowDegrees;

        /// <summary>Call when a walk coroutine is stopped early.</summary>
        public void Halt() => IsWalking = false;

        // ---------------------------------------------------------------- moving

        /// <summary>Walks through the points at the given speed, turning smoothly into each leg. The legs follow.</summary>
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
                    // Ease into the final stop instead of halting mid-stride
                    float pace = i == points.Length - 1 ? Mathf.Lerp(0.35f, 1f, Mathf.Clamp01(distance / 0.9f)) : 1f;
                    float step = speed * pace * Time.deltaTime;
                    if (distance <= step || distance < 1e-3f)
                    {
                        transform.position = target;
                        break;
                    }
                    Vector3 dir = delta / distance;
                    transform.position += dir * step;
                    Quaternion facing = Quaternion.LookRotation(dir, Vector3.up);
                    transform.rotation = Quaternion.Slerp(transform.rotation, facing, 1f - Mathf.Exp(-turnSharpness * Time.deltaTime));
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

        // ---------------------------------------------------------------- body poses

        public IEnumerator Kneel(float seconds) => BlendPose(1f, 0f, seconds);
        public IEnumerator Sit(float seconds) => BlendPose(0f, 1f, seconds);
        public IEnumerator Stand(float seconds) => BlendPose(0f, 0f, seconds);

        IEnumerator BlendPose(float kneel, float sit, float seconds)
        {
            float k0 = Poser.kneel, s0 = Poser.sit;
            yield return Tween(seconds, t =>
            {
                Poser.kneel = Mathf.Lerp(k0, kneel, t);
                Poser.sit = Mathf.Lerp(s0, sit, t);
            });
        }

        public IEnumerator Slump(float degrees, float seconds)
        {
            float from = Poser.slump;
            yield return Tween(seconds, t => Poser.slump = Mathf.Lerp(from, degrees, t));
        }

        /// <summary>Reaches a hand to a world point (IK) over the given time, and holds it there.</summary>
        public IEnumerator Reach(CharacterRigPoser.Side side, Vector3 target, float seconds)
        {
            SetReachTarget(side, target);
            float from = ReachWeight(side);
            yield return Tween(seconds, t => SetReachWeight(side, Mathf.Lerp(from, 1f, t)));
        }

        /// <summary>Lets a reaching hand fall back to the body's own pose.</summary>
        public IEnumerator Release(CharacterRigPoser.Side side, float seconds)
        {
            float from = ReachWeight(side);
            yield return Tween(seconds, t => SetReachWeight(side, Mathf.Lerp(from, 0f, t)));
        }

        public void SetReachTarget(CharacterRigPoser.Side side, Vector3 target)
        {
            if (side == CharacterRigPoser.Side.Left)
                Poser.reachLeft = target;
            else
                Poser.reachRight = target;
        }

        float ReachWeight(CharacterRigPoser.Side side) =>
            side == CharacterRigPoser.Side.Left ? Poser.reachLeftWeight : Poser.reachRightWeight;

        void SetReachWeight(CharacterRigPoser.Side side, float w)
        {
            if (side == CharacterRigPoser.Side.Left)
                Poser.reachLeftWeight = w;
            else
                Poser.reachRightWeight = w;
        }

        public IEnumerator LookAt(Transform target, float seconds, float weight = 1f)
        {
            Poser.lookTarget = target;
            float from = Poser.lookWeight;
            yield return Tween(seconds, t => Poser.lookWeight = Mathf.Lerp(from, weight, t));
        }

        public IEnumerator LookAtPoint(Vector3 point, float seconds, float weight = 1f)
        {
            Poser.lookTarget = null;
            Poser.lookPoint = point;
            float from = Poser.lookWeight;
            yield return Tween(seconds, t => Poser.lookWeight = Mathf.Lerp(from, weight, t));
        }

        public IEnumerator LookAway(float seconds)
        {
            float from = Poser.lookWeight;
            yield return Tween(seconds, t => Poser.lookWeight = Mathf.Lerp(from, 0f, t));
        }

        // ---------------------------------------------------------------- props

        /// <summary>Puts a prop in a hand: parented to the hand bone, its pivot at the palm.</summary>
        public void Hold(Transform prop, CharacterRigPoser.Side side = CharacterRigPoser.Side.Right)
        {
            if (prop == null)
                return;
            Transform hand = Poser.Hand(side);
            prop.SetParent(hand != null ? hand : transform, true);
            prop.position = Poser.Palm(side);
        }

        /// <summary>The middle of the right palm in world space, for arcs into or out of it.</summary>
        public Vector3 HandPosition => Poser.Palm(CharacterRigPoser.Side.Right);

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

        /// <summary>Moves a prop to follow a moving point (e.g. a hand) for the given time.</summary>
        public static IEnumerator Follow(Transform prop, System.Func<Vector3> point, float seconds)
        {
            if (prop == null)
                yield break;
            prop.SetParent(null, true);
            Vector3 from = prop.position;
            yield return Tween(seconds, t => prop.position = Vector3.Lerp(from, point(), t));
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
    }
}
