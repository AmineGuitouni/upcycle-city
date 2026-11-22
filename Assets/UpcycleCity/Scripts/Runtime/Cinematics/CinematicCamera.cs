using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace UpcycleCity.Cinematics
{
    /// <summary>
    /// Drives a camera through cinematic shots: hard cuts, eased dollies between two <see cref="CameraAnchor"/>s,
    /// an optional look-at target, a soft handheld drift, jolts, and the depth of field (each shot's focus
    /// distance, or a rack focus onto a subject). Runs late so it frames actors after they have moved.
    /// </summary>
    [AddComponentMenu("Upcycle City/Cinematics/Cinematic Camera")]
    [RequireComponent(typeof(Camera))]
    [DefaultExecutionOrder(200)]
    public sealed class CinematicCamera : MonoBehaviour
    {
        [Tooltip("Handheld drift, in degrees.")]
        [SerializeField, Min(0f)] float noiseAmplitude = 0.35f;
        [SerializeField, Min(0f)] float noiseFrequency = 0.3f;
        [Tooltip("How quickly the look-at follows a moving target (higher is snappier).")]
        [SerializeField, Min(0.1f)] float lookSharpness = 3f;
        [Tooltip("Global volume whose profile has a Bokeh Depth Of Field; its focus is driven per shot.")]
        [SerializeField] Volume focusVolume;

        Camera cam;
        Vector3 fromPos, toPos;
        Quaternion fromRot, toRot;
        float fromFov, toFov, fromRoll, toRoll, fromFocus, toFocus;
        float startTime, duration;

        Transform lookTarget;
        Vector3 lookOffset;
        float lookWeight;
        Quaternion smoothedLook;
        bool hasSmoothedLook;

        DepthOfField dof;
        Transform focusTarget;
        float focusBlendStart, focusBlendTime, focusBlendFrom, currentFocus = 10f;

        float jolt;

        /// <summary>Multiplies the handheld drift for the current shot (0 for a locked-off shot).</summary>
        public float Shake { get; set; } = 1f;

        public Camera Camera => cam != null ? cam : cam = GetComponent<Camera>();
        public float FocusDistance => currentFocus;

        void Awake()
        {
            cam = GetComponent<Camera>();
            fromPos = toPos = transform.position;
            fromRot = toRot = transform.rotation;
            fromFov = toFov = cam.fieldOfView;
            if (focusVolume != null && focusVolume.profile != null)
                focusVolume.profile.TryGet(out dof); // .profile gives this volume its own runtime copy
        }

        /// <summary>Jumps straight to a pose and holds it.</summary>
        public void Cut(CameraAnchor anchor) => Move(anchor, anchor, 0f);

        /// <summary>Cuts to <paramref name="from"/>, then dollies to <paramref name="to"/> over the duration (ease in-out).</summary>
        public void Move(CameraAnchor from, CameraAnchor to, float seconds)
        {
            if (from == null || to == null)
                return;
            fromPos = from.transform.position;
            fromRot = from.transform.rotation;
            fromFov = from.fieldOfView;
            fromRoll = from.roll;
            fromFocus = from.focusDistance;
            toPos = to.transform.position;
            toRot = to.transform.rotation;
            toFov = to.fieldOfView;
            toRoll = to.roll;
            toFocus = to.focusDistance;
            startTime = Time.time;
            duration = Mathf.Max(0f, seconds);
            hasSmoothedLook = false;
            focusTarget = null;
            if (fromFocus > 0f)
                currentFocus = fromFocus;
            Apply();
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

        /// <summary>Pulls focus onto a subject over the given time, then keeps it sharp as it moves.</summary>
        public void RackFocus(Transform subject, float seconds)
        {
            focusTarget = subject;
            focusBlendStart = Time.time;
            focusBlendTime = Mathf.Max(0.01f, seconds);
            focusBlendFrom = currentFocus;
        }

        /// <summary>A sudden shove of the camera that settles quickly (an impact, a shock).</summary>
        public void Jolt(float degrees) => jolt = Mathf.Max(jolt, degrees);

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

            rotation *= Quaternion.Euler(0f, 0f, Mathf.Lerp(fromRoll, toRoll, eased));

            float amp = noiseAmplitude * Shake;
            if (amp > 0f)
            {
                float time = Time.time * noiseFrequency;
                float pitch = (Mathf.PerlinNoise(time, 0.17f) - 0.5f) * 2f * amp;
                float yaw = (Mathf.PerlinNoise(0.53f, time) - 0.5f) * 2f * amp;
                float roll = (Mathf.PerlinNoise(time * 0.7f, 0.91f) - 0.5f) * amp * 0.6f;
                rotation *= Quaternion.Euler(pitch, yaw, roll);
            }
            if (jolt > 0.01f)
            {
                float j = Time.time * 47f;
                rotation *= Quaternion.Euler(Mathf.Sin(j) * jolt, Mathf.Cos(j * 1.3f) * jolt * 0.7f, Mathf.Sin(j * 0.7f) * jolt * 0.5f);
                jolt = Mathf.Lerp(jolt, 0f, 1f - Mathf.Exp(-6f * Time.deltaTime));
            }

            transform.SetPositionAndRotation(position, rotation);
            Camera.fieldOfView = Mathf.Lerp(fromFov, toFov, eased);
            UpdateFocus(eased, position);
        }

        void UpdateFocus(float eased, Vector3 position)
        {
            if (focusTarget != null)
            {
                float k = Mathf.SmoothStep(0f, 1f, (Time.time - focusBlendStart) / focusBlendTime);
                currentFocus = Mathf.Lerp(focusBlendFrom, Vector3.Distance(position, focusTarget.position), k);
            }
            else if (fromFocus > 0f || toFocus > 0f)
            {
                currentFocus = Mathf.Lerp(fromFocus > 0f ? fromFocus : toFocus, toFocus > 0f ? toFocus : fromFocus, eased);
            }
            if (dof != null)
                dof.focusDistance.value = Mathf.Max(0.1f, currentFocus);
        }
    }
}
