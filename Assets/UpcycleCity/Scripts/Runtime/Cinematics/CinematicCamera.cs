using UnityEngine;

namespace UpcycleCity.Cinematics
{
    /// <summary>
    /// Drives a camera through cinematic shots: hard cuts, eased dollies between two <see cref="CameraAnchor"/>s,
    /// an optional look-at target blended over the shot's rotation, and a soft handheld drift.
    /// Runs in LateUpdate so it frames actors after they have moved this frame.
    /// </summary>
    [AddComponentMenu("Upcycle City/Cinematics/Cinematic Camera")]
    [RequireComponent(typeof(Camera))]
    public sealed class CinematicCamera : MonoBehaviour
    {
        [Tooltip("Handheld drift, in degrees.")]
        [SerializeField, Min(0f)] float noiseAmplitude = 0.35f;
        [SerializeField, Min(0f)] float noiseFrequency = 0.3f;
        [Tooltip("How quickly the look-at follows a moving target (higher is snappier).")]
        [SerializeField, Min(0.1f)] float lookSharpness = 3f;

        Camera cam;
        Vector3 fromPos, toPos;
        Quaternion fromRot, toRot;
        float fromFov, toFov;
        float startTime, duration;

        Transform lookTarget;
        Vector3 lookOffset;
        float lookWeight;
        Quaternion smoothedLook;
        bool hasSmoothedLook;

        /// <summary>Multiplies the handheld drift for the current shot (0 for a locked-off shot).</summary>
        public float Shake { get; set; } = 1f;

        public Camera Camera => cam != null ? cam : cam = GetComponent<Camera>();

        void Awake()
        {
            cam = GetComponent<Camera>();
            // Hold the authored pose until the first shot
            fromPos = toPos = transform.position;
            fromRot = toRot = transform.rotation;
            fromFov = toFov = cam.fieldOfView;
        }

        /// <summary>Jumps straight to a pose and holds it.</summary>
        public void Cut(CameraAnchor anchor)
        {
            Move(anchor, anchor, 0f);
        }

        /// <summary>Cuts to <paramref name="from"/>, then dollies to <paramref name="to"/> over the duration (ease in-out).</summary>
        public void Move(CameraAnchor from, CameraAnchor to, float seconds)
        {
            if (from == null || to == null)
                return;
            fromPos = from.transform.position;
            fromRot = from.transform.rotation;
            fromFov = from.fieldOfView;
            toPos = to.transform.position;
            toRot = to.transform.rotation;
            toFov = to.fieldOfView;
            startTime = Time.time;
            duration = Mathf.Max(0f, seconds);
            hasSmoothedLook = false;
            Apply(); // no frame shows the previous shot
        }

        /// <summary>Turns the camera towards a target; weight 1 fully replaces the shot's own rotation.</summary>
        public void LookAt(Transform target, Vector3 offset, float weight = 1f)
        {
            lookTarget = target;
            lookOffset = offset;
            lookWeight = Mathf.Clamp01(weight);
            hasSmoothedLook = false;
        }

        public void ClearLook()
        {
            lookTarget = null;
            lookWeight = 0f;
        }

        void LateUpdate() => Apply();

        void Apply()
        {
            float t = duration > 0f ? Mathf.Clamp01((Time.time - startTime) / duration) : 1f;
            float eased = t * t * (3f - 2f * t);

            Vector3 position = Vector3.LerpUnclamped(fromPos, toPos, eased);
            Quaternion rotation = Quaternion.Slerp(fromRot, toRot, eased);

            if (lookTarget != null && lookWeight > 0f)
            {
                Vector3 dir = lookTarget.position + lookOffset - position;
                if (dir.sqrMagnitude > 1e-4f)
                {
                    Quaternion look = Quaternion.LookRotation(dir, Vector3.up);
                    smoothedLook = hasSmoothedLook
                        ? Quaternion.Slerp(smoothedLook, look, 1f - Mathf.Exp(-lookSharpness * Time.deltaTime))
                        : look;
                    hasSmoothedLook = true;
                    rotation = Quaternion.Slerp(rotation, smoothedLook, lookWeight);
                }
            }

            float amp = noiseAmplitude * Shake;
            if (amp > 0f)
            {
                float time = Time.time * noiseFrequency;
                float pitch = (Mathf.PerlinNoise(time, 0.17f) - 0.5f) * 2f * amp;
                float yaw = (Mathf.PerlinNoise(0.53f, time) - 0.5f) * 2f * amp;
                float roll = (Mathf.PerlinNoise(time * 0.7f, 0.91f) - 0.5f) * amp * 0.6f;
                rotation *= Quaternion.Euler(pitch, yaw, roll);
            }

            transform.SetPositionAndRotation(position, rotation);
            Camera.fieldOfView = Mathf.Lerp(fromFov, toFov, eased);
        }
    }
}
